using System;
using System.Collections.Generic;
using Anatomia3D.UI;
using Firebase.Auth;
using Firebase.Extensions;
using Firebase.Firestore;
using Google;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Admin-side auth + session state, backed by Firebase Auth (email/password)
    /// and the `admins/{uid}` Firestore doc. This is the "AdminAuthService" /
    /// "AdminAccountService" referenced in the TODOs inside AdminLoginController,
    /// AdminCreateAccountController, AdminForgotPasswordController and
    /// AdminEditProfileController.
    ///
    /// Attach to the same persistent GameObject as FirebaseBootstrap/UIManager.
    /// </summary>
    public class AdminAuthService : MonoBehaviour
    {
        public static AdminAuthService Instance { get; private set; }

        [Serializable]
        public class AdminProfile
        {
            public string Uid;
            public string FullName;
            public string Email;
            public int ClassroomCount;
            public int StudentCount;
            public int QuizzesCreated;
        }

        public AdminProfile CurrentAdmin { get; private set; }
        public bool IsLoggedIn => CurrentAdmin != null;

        /// <summary>The new address a verification link was just sent to, if
        /// any - purely in-memory for this session, never written to
        /// Firestore. Auth.CurrentUser.Email only becomes this once the admin
        /// clicks the link; until then, UI can show this to explain why the
        /// displayed email hasn't changed yet. Null when there's no pending
        /// change.</summary>
        public string PendingEmail { get; private set; }

        [Header("Email confirmation polling")]
        [Tooltip("How often to check Firebase for a pending email change having been confirmed via the link in the admin's inbox.")]
        [SerializeField] private float emailConfirmationPollIntervalSeconds = 3f;

        /// <summary>Fires the moment a pending email change is confirmed (the
        /// admin clicked the link in their inbox) - right before this class
        /// logs them out. Screens can subscribe if they want to show a
        /// message first; nothing needs to subscribe for the logout itself to
        /// happen, since that's handled here regardless of which screen (if
        /// any) is currently visible.</summary>
        public event Action OnEmailChangeConfirmed;

        private FirebaseAuth Auth => FirebaseBootstrap.Instance.Auth;
        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ---------------- Login ----------------

        /// <summary>Call from AdminLoginController.OnSecureLoginClicked() after validation passes.</summary>
        public void LoginAdmin(string email, string password, Action<bool, string> onComplete)
        {
            PendingEmail = null;
            CancelInvoke(nameof(PollPendingEmailConfirmation));

            Auth.SignInWithEmailAndPasswordAsync(email, password).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, DescribeAuthError(task.Exception));
                    return;
                }

                FetchAdminDoc(task.Result.User.UserId, (ok, profile, error) =>
                {
                    if (!ok)
                    {
                        onComplete?.Invoke(false, error ?? "This account is not registered as an admin.");
                        return;
                    }

                    CurrentAdmin = profile;
                    onComplete?.Invoke(true, null);
                });
            });
        }

        // ---------------- Google sign-in ----------------

        /// <summary>Call from AdminLoginController.OnGoogleSigninClicked(). Signs the
        /// admin in with Google, and - like CreateAdminAccount - transparently
        /// provisions the `users/{uid}` + `admins/{uid}` docs the first time a given
        /// Google account is used here. If that Google account is already registered
        /// with a different role (e.g. as a student), this fails rather than silently
        /// double-provisioning it.</summary>
        public void LoginWithGoogle(Action<bool, string> onComplete)
        {

            ConfigureGoogleSignIn();

            // GoogleSignIn.SignIn() silently reuses a cached credential from a
            // previous session if one exists, which is why the account chooser
            // stops appearing after the first sign-in. Signing out immediately
            // beforehand clears that cached credential (this is a local/plugin-side
            // reset, not a network call) so the native picker is shown every time,
            // letting the user pick a different Google account.
            GoogleSignIn.DefaultInstance.SignOut();

            GoogleSignIn.DefaultInstance.SignIn().ContinueWithOnMainThread(signInTask =>
            {
                if (signInTask.IsCanceled)
                {
                    onComplete?.Invoke(false, "Google sign-in was cancelled.");
                    return;
                }

                if (signInTask.IsFaulted)
                {
                    onComplete?.Invoke(false, DescribeGoogleSignInError(signInTask.Exception));
                    return;
                }

                Credential credential = GoogleAuthProvider.GetCredential(signInTask.Result.IdToken, null);
                Auth.SignInWithCredentialAsync(credential).ContinueWithOnMainThread(authTask =>
                {
                    if (authTask.IsCanceled || authTask.IsFaulted)
                    {
                        onComplete?.Invoke(false, DescribeAuthError(authTask.Exception));
                        return;
                    }

                    var user = authTask.Result;
                    string displayName = string.IsNullOrEmpty(user.DisplayName) ? user.Email : user.DisplayName;
                    ResolveGoogleAdmin(user.UserId, displayName, user.Email, onComplete);
                });
            });
        }

        private void ResolveGoogleAdmin(string uid, string displayName, string email, Action<bool, string> onComplete)
        {
            Db.Collection("users").Document(uid).GetSnapshotAsync().ContinueWithOnMainThread(userTask =>
            {
                if (userTask.IsCanceled || userTask.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not verify your account. Please try again.");
                    return;
                }

                var userSnap = userTask.Result;
                if (userSnap.Exists)
                {
                    string role = userSnap.ContainsField("role") ? userSnap.GetValue<string>("role") : null;
                    if (role != "admin")
                    {
                        Auth.SignOut();
                        onComplete?.Invoke(false, "This Google account is already registered as a student, not an admin.");
                        return;
                    }

                    FetchAdminDoc(uid, (ok, profile, error) =>
                    {
                        if (!ok)
                        {
                            onComplete?.Invoke(false, error ?? "Admin profile not found.");
                            return;
                        }

                        CurrentAdmin = profile;
                        onComplete?.Invoke(true, null);
                    });
                    return;
                }

                // First time this Google account has signed in here - provision the
                // same docs CreateAdminAccount writes for a password-based signup.
                ProvisionAdminProfile(uid, displayName, email, onComplete);
            });
        }

        private void ProvisionAdminProfile(string uid, string fullName, string email, Action<bool, string> onComplete)
        {
            var profile = new AdminProfile { Uid = uid, FullName = fullName, Email = email };

            var batch = Db.StartBatch();
            // users/{uid} only needs `role` - it's read solely to route a
            // Google sign-in to the right collection (see ResolveGoogleAdmin).
            // email lives ONLY in Firebase Auth, never in Firestore - see
            // FetchAdminDoc, which mirrors it from Auth.CurrentUser.Email.
            batch.Set(Db.Collection("users").Document(uid), new
            {
                role = "admin",
                createdAt = Timestamp.GetCurrentTimestamp()
            });
            batch.Set(Db.Collection("admins").Document(uid), new
            {
                fullName = fullName,
                createdAt = Timestamp.GetCurrentTimestamp(),
                classroomCount = 0,
                studentCount = 0,
                quizzesCreated = 0
            });

            batch.CommitAsync().ContinueWithOnMainThread(writeTask =>
            {
                if (writeTask.IsCanceled || writeTask.IsFaulted)
                {
                    onComplete?.Invoke(false, "Signed in, but saving your profile failed. Please try again.");
                    return;
                }

                CurrentAdmin = profile;
                onComplete?.Invoke(true, null);
            });
        }

        private void ConfigureGoogleSignIn()
        {
            if (GoogleSignIn.Configuration != null) return;

            GoogleSignIn.Configuration = new GoogleSignInConfiguration
            {
                WebClientId = FirebaseBootstrap.Instance.GoogleWebClientId,
                RequestIdToken = true,
                RequestEmail = true,
                UseGameSignIn = false
            };
        }

        private static string DescribeGoogleSignInError(AggregateException ex)
        {
            if (ex?.InnerException is GoogleSignIn.SignInException signInEx)
            {
                switch (signInEx.Status)
                {
                    case GoogleSignInStatusCode.Canceled:
                        return "Google sign-in was cancelled.";
                    case GoogleSignInStatusCode.NetworkError:
                        return "Network error during Google sign-in. Check your connection.";
                    default:
                        return "Google sign-in failed. Please try again.";
                }
            }
            return "Google sign-in failed. Please try again.";
        }

        // ---------------- Create account ----------------

        /// <summary>Call from AdminCreateAccountController once fields are validated.</summary>
        public void CreateAdminAccount(string fullName, string email, string password, Action<bool, string> onComplete)
        {
            Auth.CreateUserWithEmailAndPasswordAsync(email, password).ContinueWithOnMainThread(createTask =>
            {
                if (createTask.IsCanceled || createTask.IsFaulted)
                {
                    onComplete?.Invoke(false, DescribeAuthError(createTask.Exception));
                    return;
                }

                string uid = createTask.Result.User.UserId;
                var profile = new AdminProfile { Uid = uid, FullName = fullName, Email = email };

                var batch = Db.StartBatch();
                // See ProvisionAdminProfile - users/{uid} only needs `role`;
                // email lives only in Firebase Auth, not in either doc.
                batch.Set(Db.Collection("users").Document(uid), new
                {
                    role = "admin",
                    createdAt = Timestamp.GetCurrentTimestamp()
                });
                batch.Set(Db.Collection("admins").Document(uid), new
                {
                    fullName = fullName,
                    createdAt = Timestamp.GetCurrentTimestamp(),
                    classroomCount = 0,
                    studentCount = 0,
                    quizzesCreated = 0
                });

                batch.CommitAsync().ContinueWithOnMainThread(writeTask =>
                {
                    if (writeTask.IsCanceled || writeTask.IsFaulted)
                    {
                        onComplete?.Invoke(false, "Account created, but saving your profile failed. Please try signing in.");
                        return;
                    }

                    CurrentAdmin = profile;
                    onComplete?.Invoke(true, null);
                });
            });
        }

        // ---------------- Forgot password ----------------

        /// <summary>Call from AdminForgotPasswordController.OnSendResetLinkClicked().</summary>
        public void SendPasswordResetEmail(string email, Action<bool, string> onComplete)
        {
            Auth.SendPasswordResetEmailAsync(email).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, DescribeAuthError(task.Exception));
                    return;
                }

                onComplete?.Invoke(true, null);
            });
        }

        // ---------------- Profile updates ----------------

        /// <summary>Call from AdminEditProfileController.OnSaveChangesClicked().
        /// If the email is unchanged, just writes fullName/email to Firestore as
        /// before. If it changed, this also needs `currentPassword` to
        /// re-authenticate (Firebase Auth requires a recent sign-in for email
        /// changes) and, on success, sends a verification link to the new
        /// address rather than flipping Auth's email immediately - see the
        /// note inside.</summary>
        public void UpdateProfile(string fullName, string email, string currentPassword, Action<bool, string> onComplete)
        {
            var user = Auth.CurrentUser;
            if (user == null) { onComplete?.Invoke(false, "Not signed in."); return; }
            string uid = user.UserId;
            bool emailChanged = !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase);

            if (!emailChanged)
            {
                WriteFullName(uid, fullName, onComplete);
                return;
            }

            if (string.IsNullOrEmpty(currentPassword))
            {
                onComplete?.Invoke(false, "Enter your current password to change your email.");
                return;
            }

            var credential = EmailAuthProvider.GetCredential(user.Email, currentPassword);
            user.ReauthenticateAsync(credential).ContinueWithOnMainThread(reauthTask =>
            {
                if (reauthTask.IsCanceled || reauthTask.IsFaulted)
                {
                    onComplete?.Invoke(false, "Current password is incorrect.");
                    return;
                }

                // Sends a confirmation link to the NEW address. Auth's own
                // user.Email doesn't flip to it until the admin clicks that
                // link - CurrentAdmin.Email will pick it up automatically the
                // next time it's mirrored from Auth.CurrentUser (see
                // RefreshEmailVerificationStatus / FetchAdminDoc), so there's
                // no Firestore write to make here for the email itself.
                //
                // Once the link is sent, start polling in the background (see
                // PollPendingEmailConfirmation) so the admin gets logged out
                // the moment they confirm it, no matter which screen they're on.
                user.SendEmailVerificationBeforeUpdatingEmailAsync(email).ContinueWithOnMainThread(updateTask =>
                {
                    if (updateTask.IsCanceled || updateTask.IsFaulted)
                    {
                        onComplete?.Invoke(false, DescribeAuthError(updateTask.Exception));
                        return;
                    }

                    PendingEmail = email;
                    StartPendingEmailPolling();
                    WriteFullName(uid, fullName, onComplete);
                });
            });
        }

        /// <summary>Writes fullName to admins/{uid}. Email lives only in
        /// Firebase Auth - see FetchAdminDoc - so it's never written here;
        /// FieldValue.Delete strips any leftover `email` field from before
        /// this change so older docs self-heal the first time they're
        /// edited. Uses merge-Set rather than Update: Update() throws if the
        /// doc doesn't exist in exactly the expected shape, which can
        /// silently fail the write - a merge-Set can't fail that way,
        /// matching PlayerSessionManager.WriteFullName's approach.</summary>
        private void WriteFullName(string uid, string fullName, Action<bool, string> onComplete)
        {
            Db.Collection("admins").Document(uid).SetAsync(
                new Dictionary<string, object>
                {
                    { "fullName", fullName },
                    { "email", FieldValue.Delete }
                },
                SetOptions.MergeAll)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        Debug.LogError($"[AdminAuthService] WriteFullName failed (uid={uid}): {task.Exception}");
                        onComplete?.Invoke(false, "Could not save your profile.");
                        return;
                    }

                    if (CurrentAdmin != null)
                    {
                        CurrentAdmin.FullName = fullName;
                    }
                    onComplete?.Invoke(true, null);
                });
        }

        /// <summary>Call from AdminEditProfileController when changingPassword is true.</summary>
        public void ChangePassword(string currentPassword, string newPassword, Action<bool, string> onComplete)
        {
            var user = Auth.CurrentUser;
            if (user == null) { onComplete?.Invoke(false, "Not signed in."); return; }

            var credential = EmailAuthProvider.GetCredential(user.Email, currentPassword);
            user.ReauthenticateAsync(credential).ContinueWithOnMainThread(reauthTask =>
            {
                if (reauthTask.IsCanceled || reauthTask.IsFaulted)
                {
                    onComplete?.Invoke(false, "Current password is incorrect.");
                    return;
                }

                user.UpdatePasswordAsync(newPassword).ContinueWithOnMainThread(updateTask =>
                {
                    if (updateTask.IsCanceled || updateTask.IsFaulted)
                    {
                        onComplete?.Invoke(false, DescribeAuthError(updateTask.Exception));
                        return;
                    }

                    onComplete?.Invoke(true, null);
                });
            });
        }

        // ---------------- Email verification ----------------

        /// <summary>Whether Firebase Auth currently considers this admin's email
        /// verified. Reflects whatever was true as of the last sign-in or
        /// RefreshEmailVerificationStatus() call - call that first if you need
        /// this to be current (e.g. right after the admin may have clicked the
        /// link in another tab).</summary>
        public bool IsEmailVerified => Auth.CurrentUser != null && Auth.CurrentUser.IsEmailVerified;

        /// <summary>Call from AdminEditProfileController's "Verify Email" button.
        /// Sends a verification link to the admin's CURRENT email address (not a
        /// pending new one - that flow is SendEmailVerificationBeforeUpdatingEmailAsync
        /// inside UpdateProfile). Surface to the admin that an unverified account
        /// can't be recovered if they lose access to it.</summary>
        public void SendEmailVerification(Action<bool, string> onComplete)
        {
            var user = Auth.CurrentUser;
            if (user == null) { onComplete?.Invoke(false, "Not signed in."); return; }

            if (user.IsEmailVerified)
            {
                onComplete?.Invoke(true, null);
                return;
            }

            user.SendEmailVerificationAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, DescribeAuthError(task.Exception));
                    return;
                }

                onComplete?.Invoke(true, null);
            });
        }

        /// <summary>Re-fetches the current user from Firebase so IsEmailVerified
        /// (and CurrentAdmin.Email, if there's a CurrentAdmin) reflect a link
        /// the admin may have just clicked in their inbox. Call this when the
        /// edit-profile screen becomes visible so the verified badge doesn't
        /// show stale state from earlier in the session.
        ///
        /// IMPORTANT: confirming a pending email change makes Firebase revoke
        /// the session's existing token server-side (expected - it's a
        /// security-sensitive change). So the very next ReloadAsync() after
        /// the admin clicks the link doesn't come back with the new email; it
        /// FAILS with "the user's credential is no longer valid". While a
        /// change is pending, that specific failure IS the confirmation
        /// signal - see IsInvalidCredentialError below - not a reason to give
        /// up and leave PendingEmail set forever.</summary>
        public void RefreshEmailVerificationStatus(Action<bool> onComplete = null)
        {
            var user = Auth.CurrentUser;
            if (user == null) { onComplete?.Invoke(false); return; }

            user.ReloadAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.LogWarning($"[AdminAuthService] ReloadAsync failed (PendingEmail={PendingEmail}): {task.Exception}");

                    if (PendingEmail != null && IsInvalidCredentialError(task.Exception))
                    {
                        Debug.Log("[AdminAuthService] Invalidated credential while an email change was pending - treating as confirmed.");
                        PendingEmail = null;
                    }

                    onComplete?.Invoke(false);
                    return;
                }

                bool verified = !task.IsCanceled && user.IsEmailVerified;

                if (!task.IsCanceled && CurrentAdmin != null)
                {
                    CurrentAdmin.Email = user.Email;
                }

                if (!task.IsCanceled &&
                    PendingEmail != null && string.Equals(user.Email, PendingEmail, StringComparison.OrdinalIgnoreCase))
                {
                    PendingEmail = null;
                }

                onComplete?.Invoke(verified);
            });
        }

        /// <summary>True if the failure is Firebase revoking the session's
        /// token (message text: "The user's credential is no longer valid.
        /// The user must sign in again."), which is exactly what happens the
        /// instant a pending email-change link gets confirmed. Matches on
        /// message text rather than a specific AuthError enum member since
        /// that's what's actually visible in the logs and is stable across
        /// SDK versions.</summary>
        private static bool IsInvalidCredentialError(AggregateException ex)
        {
            var fbEx = ex?.InnerException as Firebase.FirebaseException;
            if (fbEx == null) return false;

            return fbEx.Message.IndexOf("no longer valid", StringComparison.OrdinalIgnoreCase) >= 0
                || fbEx.Message.IndexOf("sign in again", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Starts (or restarts) background polling for a pending
        /// email change being confirmed. Lives on AdminAuthService - not on
        /// any UI screen - specifically so it keeps running across screen
        /// navigation (this GameObject is DontDestroyOnLoad and never gets
        /// disabled the way a screen's controller does when the admin taps
        /// Back or the app swaps screens). CancelInvoke first avoids
        /// double-scheduling if this somehow gets called twice.</summary>
        private void StartPendingEmailPolling()
        {
            CancelInvoke(nameof(PollPendingEmailConfirmation));
            InvokeRepeating(nameof(PollPendingEmailConfirmation), emailConfirmationPollIntervalSeconds, emailConfirmationPollIntervalSeconds);
        }

        /// <summary>Ticks every emailConfirmationPollIntervalSeconds while a
        /// change is pending. Reloads the Firebase user; once PendingEmail
        /// clears (RefreshEmailVerificationStatus sets it to null either
        /// because Auth.CurrentUser.Email now matches it, or because reload
        /// failed with an invalidated-credential error - both mean the admin
        /// clicked the link), logs the admin out immediately, regardless of
        /// what screen is currently showing.</summary>
        private void PollPendingEmailConfirmation()
        {
            if (PendingEmail == null || Auth.CurrentUser == null)
            {
                CancelInvoke(nameof(PollPendingEmailConfirmation));
                return;
            }

            RefreshEmailVerificationStatus(_ =>
            {
                if (PendingEmail == null)
                {
                    CancelInvoke(nameof(PollPendingEmailConfirmation));
                    OnEmailChangeConfirmed?.Invoke();
                    LogoutAdmin();
                    UIManager.Instance?.ShowAdminLogin();
                }
            });
        }

        // ---------------- Logout ----------------

        public void LogoutAdmin()
        {
            CancelInvoke(nameof(PollPendingEmailConfirmation));
            Auth.SignOut();
            try { GoogleSignIn.DefaultInstance.SignOut(); } catch { /* wasn't signed in via Google - fine */ }
            CurrentAdmin = null;
            PendingEmail = null;
        }

        // ---------------- Refresh ----------------

        /// <summary>Re-fetches `admins/{uid}` and replaces CurrentAdmin with the
        /// result, so classroomCount/quizzesCreated reflect anything changed
        /// elsewhere this session (a classroom created/deleted, a quiz
        /// published/deleted). Note this does NOT fix studentCount - see
        /// AdminProfileController, which computes that separately since the
        /// `admins/{uid}.studentCount` field itself is never incremented
        /// anywhere and would just come back 0.</summary>
        public void RefreshCurrentAdmin(Action<bool> onComplete = null)
        {
            if (CurrentAdmin == null) { onComplete?.Invoke(false); return; }

            FetchAdminDoc(CurrentAdmin.Uid, (ok, profile, error) =>
            {
                if (ok) CurrentAdmin = profile;
                onComplete?.Invoke(ok);
            });
        }

        // ---------------- Helpers ----------------

        /// <summary>Fetches admins/{uid} for fullName/counts, then mirrors
        /// Email from Auth.CurrentUser rather than Firestore - email is never
        /// stored there. Callers of this (LoginAdmin, ResolveGoogleAdmin,
        /// RefreshCurrentAdmin) all run after Auth already has a signed-in
        /// user, so Auth.CurrentUser is always available here.</summary>
        private void FetchAdminDoc(string uid, Action<bool, AdminProfile, string> onComplete)
        {
            Db.Collection("admins").Document(uid).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                {
                    onComplete?.Invoke(false, null, "Admin profile not found.");
                    return;
                }

                var snap = task.Result;
                var profile = new AdminProfile
                {
                    Uid = uid,
                    FullName = snap.GetValue<string>("fullName"),
                    Email = Auth.CurrentUser?.Email,
                    ClassroomCount = snap.ContainsField("classroomCount") ? snap.GetValue<int>("classroomCount") : 0,
                    StudentCount = snap.ContainsField("studentCount") ? snap.GetValue<int>("studentCount") : 0,
                    QuizzesCreated = snap.ContainsField("quizzesCreated") ? snap.GetValue<int>("quizzesCreated") : 0
                };

                onComplete?.Invoke(true, profile, null);
            });
        }

        private static string DescribeAuthError(AggregateException ex)
        {
            if (ex?.InnerException is Firebase.FirebaseException fbEx)
            {
                var code = (AuthError)fbEx.ErrorCode;
                switch (code)
                {
                    case AuthError.InvalidEmail: return "That email address looks invalid.";
                    case AuthError.WrongPassword: return "Incorrect password.";
                    case AuthError.UserNotFound: return "No account found with that email.";
                    case AuthError.EmailAlreadyInUse: return "An account with that email already exists.";
                    case AuthError.WeakPassword: return "Password is too weak.";
                    default: return "Something went wrong. Please try again.";
                }
            }
            return "Something went wrong. Please try again.";
        }
    }
}