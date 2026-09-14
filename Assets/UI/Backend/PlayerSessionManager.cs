using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Anatomia3D.UI;
using Firebase;
using Firebase.Auth;
using Firebase.Extensions;
using Firebase.Firestore;
using Google;
using NativeBiometricAuth;
using UnityEngine;
using UnityEngine.Networking;


namespace Anatomia3D.Backend
{
    /// <summary>
    /// Student-side auth + session state, backed by Firebase Auth (email/password)
    /// and the `students/{uid}` Firestore doc. This is the "PlayerSessionManager"
    /// referenced in the TODOs inside StudentLoginController, CreateAccountController,
    /// ForgotPasswordController and StudentEditProfileController.
    ///
    /// Attach to the same persistent GameObject as FirebaseBootstrap/UIManager.
    /// </summary>
    public class PlayerSessionManager : MonoBehaviour
    {
        public static PlayerSessionManager Instance { get; private set; }

        [Serializable]
        public class StudentProfile
        {
            public string Uid;
            public string FullName;
            public int Level;
            public int TotalPoints;
            public int QuizzesCompleted;
            /// <summary>Badge ids this student has earned, mirrored from the
            /// `badgesEarned` array on students/{uid}. Matched against
            /// AdminGamificationService.BadgeEntry.BadgeId to know which of a
            /// teacher's configured badges to render as unlocked (see
            /// StudentAchievementsController / StudentClassroomDetailController's
            /// Badges tab).</summary>
            public List<string> BadgesEarned = new List<string>();
            /// <summary>Cloudinary `secure_url` for this student's avatar image,
            /// mirrored from the `avatarUrl` field on students/{uid}. Null/empty
            /// means no avatar has been set - callers (StudentProfileController)
            /// fall back to initials in that case. The upload itself (Cloudinary
            /// unsigned upload + writing this field back to Firestore) happens on
            /// the Edit Profile screen, not here - this class only reads it.</summary>
            public string AvatarUrl;
            // Email/EmailVerified are NOT stored in Firestore - they're always
            // mirrored straight from Firebase Auth (Auth.CurrentUser) whenever
            // this profile is built, so there's exactly one source of truth
            // and no risk of the two disagreeing.
            public string Email;
            public bool EmailVerified;
        }

        public StudentProfile CurrentStudent { get; private set; }
        public bool IsLoggedIn => CurrentStudent != null;

        // ---------------- Offline session restore ----------------
        //
        // Firebase Auth persists its own signed-in user locally on-device
        // (Auth.CurrentUser survives an app restart with zero network
        // calls) - what's missing without the cache below is the
        // students/{uid} PROFILE data (fullName, level, points, etc.),
        // which normally only arrives via a Firestore read. CacheStudent
        // mirrors the last known-good profile to disk every time
        // CurrentStudent is set from real data (login or the live
        // listener), so TryRestoreSessionOffline can rebuild a full
        // CurrentStudent with no network at all.

        private const string SessionCacheFileName = "student_session_cache.json";
        private string SessionCacheFilePath => Path.Combine(Application.persistentDataPath, SessionCacheFileName);

        // ---------------- Biometric login ----------------
        //
        // Biometrics here are a LOCK SCREEN on top of the Firebase session that's
        // already persisted on-device (Auth.CurrentUser survives an app restart -
        // see TryRestoreSessionOffline above), not a replacement for the
        // email/password or Google sign-in flows. A student still has to sign in
        // normally at least once per device; after that, a successful biometric
        // check just unlocks the profile that's already cached locally instead of
        // asking for the password again.
        //
        // The opt-in flag is stored per-uid so a shared/lab device doesn't offer
        // "sign in with biometrics" for a different student than whoever's
        // fingerprint/face is enrolled on that device, and so logging out (which
        // clears the cached profile) doesn't leave a stale flag pointing at data
        // that no longer exists.

        private const string BiometricEnabledPrefKeyPrefix = "biometric_login_enabled_";
        private static string BiometricPrefKeyForUid(string uid) => BiometricEnabledPrefKeyPrefix + uid;

        /// <summary>Pure hardware/enrollment capability check - true if this device
        /// COULD do a biometric or device-credential check at all, regardless of
        /// whether any account has opted in yet. Different from
        /// IsBiometricLoginAvailable below (which also requires a persisted Auth
        /// session and the per-uid opt-in flag): UIManager's offline cold-start
        /// uses this one to tell "there's a lock screen the student could use" apart
        /// from "there's nothing this device can ever do to verify anyone," since
        /// only the second case has no safe way through at all when offline.</summary>
        public bool IsBiometricHardwareAvailable => Biometric.IsAvailable(allowDeviceCredential: true);

        /// <summary>True only when there's a Firebase user already persisted on this
        /// device AND that student has previously been through a successful
        /// password/Google login here with biometric hardware available (see
        /// SetCurrentStudentAndListen). StudentLoginController should check this
        /// before showing a "Sign in with biometrics" button - showing it any other
        /// time would just fail, since there's nothing local to unlock yet.</summary>
        public bool IsBiometricLoginAvailable
        {
            get
            {
                if (FirebaseBootstrap.Instance == null || FirebaseBootstrap.Instance.Auth == null) return false;
                var user = Auth.CurrentUser;
                if (user == null) return false;
                return PlayerPrefs.GetInt(BiometricPrefKeyForUid(user.UserId), 0) == 1;
            }
        }

        /// <summary>Turns the per-device biometric opt-in on/off for whoever's
        /// currently signed in. Called automatically after a successful login (see
        /// SetCurrentStudentAndListen); exposed publicly too in case you want to add
        /// an explicit toggle (e.g. in profile/account settings) instead of relying
        /// on the automatic opt-in.</summary>
        public void SetBiometricLoginEnabled(bool enabled)
        {
            if (Auth.CurrentUser == null) return;
            PlayerPrefs.SetInt(BiometricPrefKeyForUid(Auth.CurrentUser.UserId), enabled ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>Outcome of TryOfflineGate - deliberately three-way instead of a
        /// plain bool, so the login screen can tell "wrong PIN/fingerprint, try
        /// again or use your password" apart from "there is no OS credential on
        /// this device at all, offline sign-in genuinely isn't possible here."
        /// Collapsing those into one failure message would tell a student with a
        /// broken sensor to "try again" forever with no way through.</summary>
        public enum OfflineGateResult
        {
            EnteredApp,
            NeedsPasswordLogin,
            NoSecureFallbackAvailable
        }

        /// <summary>Call from StudentLoginController's biometric button. Requires the
        /// OS-level biometric/device-credential check to succeed BEFORE touching any
        /// session state - only then does this restore the profile from cache
        /// (same as TryRestoreSessionOffline) and kick off a background refresh so
        /// stale cached points/level get corrected the moment there's connectivity,
        /// without making the student wait for a network round-trip just to see
        /// their dashboard.
        ///
        /// Supersedes the old LoginWithBiometrics(Action&lt;bool,string&gt;) - same
        /// underlying OS check, but reports NoSecureFallbackAvailable separately from
        /// a failed/cancelled attempt. That distinction matters because opting in to
        /// biometric sign-in (SetBiometricLoginEnabled) doesn't guarantee the OS
        /// credential still exists later - e.g. a student could enable biometrics,
        /// then remove their screen lock in device settings before the next offline
        /// launch. Biometric.IsAvailable(allowDeviceCredential: true), read via
        /// IsBiometricHardwareAvailable, is what actually reflects "is there still an
        /// OS-level check to run right now."</summary>
        public void TryOfflineGate(Action<OfflineGateResult, string> onComplete)
        {
            if (!IsBiometricLoginAvailable)
            {
                onComplete?.Invoke(OfflineGateResult.NeedsPasswordLogin,
                    "Sign in with your password to enable offline sign-in on this device.");
                return;
            }

            if (!IsBiometricHardwareAvailable)
            {
                // Opted in previously, but there's no biometric AND no device
                // credential (PIN/pattern/password) available right now - most
                // likely the student removed their screen lock since opting in.
                // There's no OS-verified check left to run, so don't show a
                // biometric prompt that would just fail confusingly.
                onComplete?.Invoke(OfflineGateResult.NoSecureFallbackAvailable,
                    "This device has no fingerprint, face, or screen lock set up, so " +
                    "offline sign-in isn't available. Please connect to the internet to " +
                    "sign in, or set up a screen lock in your device settings.");
                return;
            }

            Biometric.Authenticate(
                allowDeviceCredential: true,
                onSuccess: () =>
                {
                    if (TryRestoreSessionOffline())
                    {
                        RefreshCurrentStudent();
                        onComplete?.Invoke(OfflineGateResult.EnteredApp, null);
                    }
                    else
                    {
                        onComplete?.Invoke(OfflineGateResult.NeedsPasswordLogin,
                            "Could not restore your session. Please sign in with your password.");
                    }
                },
                onFailure: reason =>
                {
                    // TEMP diagnostic logging - remove once biometric login is
                    // confirmed working end-to-end on target devices. The generic
                    // message below is what the student sees either way; this is
                    // purely so `adb logcat -s Unity` shows WHY the OS-level check
                    // failed (no hardware, nothing enrolled, user cancelled,
                    // lockout, etc.) instead of us having to guess.
                    Debug.Log($"[PlayerSessionManager] Offline gate failed: {reason}");

                    bool offline = Application.internetReachability == NetworkReachability.NotReachable;
                    onComplete?.Invoke(OfflineGateResult.NeedsPasswordLogin, offline
                        ? "Authentication failed. Please try again once your device recognizes you, or reconnect to sign in with your password."
                        : "Authentication failed. Please sign in with your password.");
                });
        }

        private void CacheStudent(StudentProfile profile)
        {
            if (profile == null) return;
            try
            {
                File.WriteAllText(SessionCacheFilePath, JsonUtility.ToJson(profile));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayerSessionManager] Could not cache session: {e.Message}");
            }
        }

        private StudentProfile LoadCachedStudent()
        {
            try
            {
                if (!File.Exists(SessionCacheFilePath)) return null;
                return JsonUtility.FromJson<StudentProfile>(File.ReadAllText(SessionCacheFilePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayerSessionManager] Could not read cached session: {e.Message}");
                return null;
            }
        }

        private void ClearCachedStudent()
        {
            try
            {
                if (File.Exists(SessionCacheFilePath)) File.Delete(SessionCacheFilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayerSessionManager] Could not clear cached session: {e.Message}");
            }
        }

        /// <summary>Restores CurrentStudent from the last cached profile with
        /// NO network call - call this once at app start (see
        /// UIManager.Start) before deciding whether to show the login
        /// screen, so a student who's already signed in doesn't get stuck
        /// on Login with no internet to submit it. Only succeeds if Firebase
        /// Auth still has a locally-persisted signed-in user (Auth.CurrentUser
        /// - Firebase's own on-device cache, unrelated to connectivity)
        /// whose uid matches the cached profile, so a logged-out device or a
        /// different account never picks up someone else's cached data.
        ///
        /// Still starts the normal real-time students/{uid} listener, so the
        /// moment connectivity returns the restored profile is refreshed
        /// from the server exactly as if this had been a normal online
        /// login - this is a starting point, not a permanent substitute for
        /// real data.</summary>
        public bool TryRestoreSessionOffline()
        {
            if (CurrentStudent != null) return true; // already signed in this session

            // Firebase initializes asynchronously (see FirebaseBootstrap.Awake) -
            // this can be called (from UIManager.Start) before that finishes,
            // in which case Auth isn't available yet. Nothing has changed
            // about WHAT gets restored or how - this just avoids reading
            // through a not-yet-ready FirebaseBootstrap.Instance.Auth.
            if (FirebaseBootstrap.Instance == null || FirebaseBootstrap.Instance.Auth == null)
            {
                Debug.LogWarning("[PlayerSessionManager] Firebase not ready yet - cannot restore offline session this early.");
                return false;
            }

            var authUser = Auth.CurrentUser;
            if (authUser == null) return false; // Firebase itself has no persisted sign-in

            var cached = LoadCachedStudent();
            if (cached == null || cached.Uid != authUser.UserId) return false;

            // Email/EmailVerified always mirror Auth directly (see the
            // StudentProfile field comments) - refresh them from Auth even
            // though the rest of the profile below is coming from cache.
            cached.Email = authUser.Email;
            cached.EmailVerified = authUser.IsEmailVerified;

            CurrentStudent = cached;
            OnStudentProfileChanged?.Invoke(CurrentStudent);
            StartStudentListener(cached.Uid);
            return true;
        }

        /// <summary>The new address a verification link was just sent to, if
        /// any - purely in-memory for this session, never written to
        /// Firestore. Auth.CurrentUser.Email (and CurrentStudent.Email) only
        /// becomes this once the student clicks the link; until then, UI can
        /// show this to explain why the displayed email hasn't changed yet.
        /// Null when there's no pending change.</summary>
        public string PendingEmail { get; private set; }

        [Header("Email confirmation polling")]
        [Tooltip("How often to check Firebase for a pending email change having been confirmed via the link in the student's inbox.")]
        [SerializeField] private float emailConfirmationPollIntervalSeconds = 3f;

        /// <summary>Fires the moment a pending email change is confirmed (the
        /// student clicked the link in their inbox) - right before this class
        /// logs them out. Screens can subscribe if they want to show a
        /// message first; nothing needs to subscribe for the logout itself to
        /// happen, since that's handled here regardless of which screen (if
        /// any) is currently visible.</summary>
        public event Action OnEmailChangeConfirmed;

        /// <summary>Fires whenever CurrentStudent's data actually changes - either
        /// from a local optimistic patch (ApplyQuizAttemptResult, WriteFullName) or
        /// from the real-time students/{uid} listener picking up an out-of-band
        /// change (e.g. a teacher edit, or the same student signed in on another
        /// device). Does NOT fire on every snapshot callback - only when the
        /// incoming data differs from what's already cached, so a screen that's
        /// subscribed won't redraw for no reason. Screens should subscribe in
        /// OnEnable and unsubscribe in OnDisable rather than re-fetching on open.</summary>
        public event Action<StudentProfile> OnStudentProfileChanged;

        private FirebaseAuth Auth => FirebaseBootstrap.Instance.Auth;
        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        private ListenerRegistration _studentListener;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            StopStudentListener();
        }

        // ---------------- Login ----------------

        /// <summary>Call from StudentLoginController.OnSignInClicked() after validation passes.</summary>
        public void LoginStudent(string email, string password, Action<bool, string> onComplete)
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

                FetchStudentDoc(task.Result.User.UserId, (ok, profile, error) =>
                {
                    if (!ok)
                    {
                        onComplete?.Invoke(false, error ?? "Could not load your profile.");
                        return;
                    }

                    SetCurrentStudentAndListen(profile);
                    onComplete?.Invoke(true, null);
                });
            });
        }

        // ---------------- Google sign-in ----------------

        /// <summary>Call from StudentLoginController.OnGoogleClicked(). Signs the
        /// student in with Google, and - like CreateStudentAccount - transparently
        /// provisions the `users/{uid}` + `students/{uid}` docs the first time a given
        /// Google account is used here. If that Google account is already registered
        /// with a different role (e.g. as an admin), this fails rather than silently
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
                    ResolveGoogleStudent(user.UserId, displayName, user.Email, onComplete);
                });
            });

        }

        private void ResolveGoogleStudent(string uid, string displayName, string email, Action<bool, string> onComplete)
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
                    if (role != "student")
                    {
                        Auth.SignOut();
                        onComplete?.Invoke(false, "This Google account is already registered as an admin, not a student.");
                        return;
                    }

                    FetchStudentDoc(uid, (ok, profile, error) =>
                    {
                        if (!ok)
                        {
                            onComplete?.Invoke(false, error ?? "Student profile not found.");
                            return;
                        }

                        SetCurrentStudentAndListen(profile);
                        onComplete?.Invoke(true, null);
                    });
                    return;
                }

                // First time this Google account has signed in here - provision the
                // same docs CreateStudentAccount writes for a password-based signup.
                ProvisionStudentProfile(uid, displayName, email, onComplete);
            });
        }

        private void ProvisionStudentProfile(string uid, string fullName, string email, Action<bool, string> onComplete)
        {
            var profile = new StudentProfile
            {
                Uid = uid,
                FullName = fullName,
                Email = email,
                EmailVerified = Auth.CurrentUser?.IsEmailVerified ?? false,
                Level = 1,
                TotalPoints = 0,
                QuizzesCompleted = 0,
                BadgesEarned = new List<string>()
            };

            var batch = Db.StartBatch();
            // users/{uid} only needs `role` - it's read solely to route a
            // Google sign-in to the right collection. Email lives only in
            // Firebase Auth (never duplicated into Firestore), so there's
            // nothing here that can ever fall out of sync with it.
            batch.Set(Db.Collection("users").Document(uid), new
            {
                role = "student",
                createdAt = Timestamp.GetCurrentTimestamp()
            });
            batch.Set(Db.Collection("students").Document(uid), new
            {
                fullName = fullName,
                createdAt = Timestamp.GetCurrentTimestamp(),
                level = 1,
                totalPoints = 0,
                quizzesCompleted = 0,
                badgesEarned = new string[0],
                enrolledClassroomIds = new string[0]
            });

            batch.CommitAsync().ContinueWithOnMainThread(writeTask =>
            {
                if (writeTask.IsCanceled || writeTask.IsFaulted)
                {
                    onComplete?.Invoke(false, "Signed in, but saving your profile failed. Please try again.");
                    return;
                }

                SetCurrentStudentAndListen(profile);
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

        /// <summary>Call from CreateAccountController once fields are validated.</summary>
        public void CreateStudentAccount(string fullName, string email, string password, Action<bool, string> onComplete)
        {
            Auth.CreateUserWithEmailAndPasswordAsync(email, password).ContinueWithOnMainThread(createTask =>
            {
                if (createTask.IsCanceled || createTask.IsFaulted)
                {
                    onComplete?.Invoke(false, DescribeAuthError(createTask.Exception));
                    return;
                }

                string uid = createTask.Result.User.UserId;
                var profile = new StudentProfile
                {
                    Uid = uid,
                    FullName = fullName,
                    Email = email,
                    EmailVerified = createTask.Result.User.IsEmailVerified,
                    Level = 1,
                    TotalPoints = 0,
                    QuizzesCompleted = 0,
                    BadgesEarned = new List<string>()
                };

                var batch = Db.StartBatch();
                // See ProvisionStudentProfile - users/{uid} only needs `role`;
                // email lives only in Firebase Auth, never in Firestore.
                batch.Set(Db.Collection("users").Document(uid), new
                {
                    role = "student",
                    createdAt = Timestamp.GetCurrentTimestamp()
                });
                batch.Set(Db.Collection("students").Document(uid), new
                {
                    fullName = fullName,
                    createdAt = Timestamp.GetCurrentTimestamp(),
                    level = 1,
                    totalPoints = 0,
                    quizzesCompleted = 0,
                    badgesEarned = new string[0],
                    enrolledClassroomIds = new string[0]
                });

                batch.CommitAsync().ContinueWithOnMainThread(writeTask =>
                {
                    if (writeTask.IsCanceled || writeTask.IsFaulted)
                    {
                        onComplete?.Invoke(false, "Account created, but saving your profile failed. Please try signing in.");
                        return;
                    }

                    SetCurrentStudentAndListen(profile);
                    onComplete?.Invoke(true, null);
                });
            });
        }

        // ---------------- Forgot password ----------------

        /// <summary>Call from ForgotPasswordController.OnSendResetLinkClicked().</summary>
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

        /// <summary>Call from StudentEditProfileController.OnSaveChangesClicked().
        /// fullName always goes to Firestore. Email is never written to
        /// Firestore - it lives only in Firebase Auth. If it changed, this
        /// needs `currentPassword` to re-authenticate (Firebase Auth requires
        /// a recent sign-in for email changes) and sends a verification link
        /// to the new address; Auth.CurrentUser.Email (and therefore
        /// CurrentStudent.Email) only updates once the student actually
        /// clicks that link - see RefreshEmailVerificationStatus(). Once the
        /// link is sent, this starts polling in the background (see
        /// PollPendingEmailConfirmation) so the student gets logged out the
        /// moment they confirm it, no matter which screen they're on.</summary>
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
                // user.Email (and CurrentStudent.Email, which mirrors it)
                // stays on the OLD address until the student clicks that
                // link - there's no separate Firestore copy to write early.
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

        /// <summary>Writes fullName to students/{uid}. Uses a merge-Set rather
        /// than Update: Update() throws if the doc doesn't exist in exactly
        /// the expected shape, which can silently fail the write - a
        /// merge-Set can't fail that way and self-heals odd/older docs.
        ///
        /// Also fires SyncStudentNameToClassrooms() to fix the fact that
        /// `classrooms/{id}/members/{uid}.studentName` is a denormalized copy
        /// taken at join time (see ClassroomService.JoinClassroom) - without
        /// this, a renamed student keeps showing their old name in every
        /// classroom's roster/analytics even though students/{uid} itself is
        /// correct. That sync is best-effort and reported via onComplete only
        /// through logging, never by failing the profile save itself: the
        /// name change to students/{uid} - the source of truth - already
        /// succeeded by that point, and the roster copies healing a few
        /// seconds later (or on a retry) is an acceptable trade-off against
        /// blocking Save Changes on N extra classroom writes.</summary>
        private void WriteFullName(string uid, string fullName, Action<bool, string> onComplete)
        {
            Db.Collection("students").Document(uid).SetAsync(
                new System.Collections.Generic.Dictionary<string, object> { { "fullName", fullName } },
                SetOptions.MergeAll)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        onComplete?.Invoke(false, "Could not save your profile.");
                        return;
                    }

                    if (CurrentStudent != null)
                    {
                        CurrentStudent.FullName = fullName;
                        OnStudentProfileChanged?.Invoke(CurrentStudent);
                    }
                    onComplete?.Invoke(true, null);

                    SyncStudentNameToClassrooms(uid, fullName);
                });
        }

        /// <summary>Best-effort fix-up for the denormalized `studentName` field
        /// that ClassroomService.JoinClassroom copies into
        /// `classrooms/{classroomId}/members/{uid}` at join time. Re-reads
        /// students/{uid}.enrolledClassroomIds (not cached on StudentProfile)
        /// and batches a merge-Set of the new name onto every classroom
        /// membership doc, so AdminClassroomService.FetchClassroomAnalytics
        /// and the student-facing ClassroomService reads pick up the new name
        /// immediately instead of only at next join. Failures here are logged
        /// and swallowed - the caller already reported success for the actual
        /// profile save via WriteFullName's onComplete.</summary>
        private void SyncStudentNameToClassrooms(string uid, string fullName)
        {
            Db.Collection("students").Document(uid).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                {
                    Debug.LogWarning($"[PlayerSessionManager] Could not read enrolledClassroomIds for {uid} - classroom rosters may show a stale name until next sync.");
                    return;
                }

                var snap = task.Result;
                if (!snap.ContainsField("enrolledClassroomIds")) return;

                var classroomIds = snap.GetValue<List<string>>("enrolledClassroomIds");
                if (classroomIds == null || classroomIds.Count == 0) return;

                var batch = Db.StartBatch();
                foreach (var classroomId in classroomIds)
                {
                    if (string.IsNullOrEmpty(classroomId)) continue;
                    var memberRef = Db.Collection("classrooms").Document(classroomId)
                        .Collection("members").Document(uid);
                    batch.Set(memberRef,
                        new System.Collections.Generic.Dictionary<string, object> { { "studentName", fullName } },
                        SetOptions.MergeAll);
                }

                batch.CommitAsync().ContinueWithOnMainThread(commitTask =>
                {
                    if (commitTask.IsCanceled || commitTask.IsFaulted)
                    {
                        Debug.LogWarning($"[PlayerSessionManager] Failed to sync new name to one or more classroom rosters for {uid}: {commitTask.Exception?.InnerException?.Message}");
                    }
                });
            });
        }

        /// <summary>Call from StudentEditProfileController when changingPassword is true.</summary>
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

        /// <summary>Whether Firebase Auth currently considers this student's
        /// email verified. Reflects whatever was true as of the last sign-in or
        /// RefreshEmailVerificationStatus() call - call that first if you need
        /// this to be current (e.g. right after the student may have clicked
        /// the link in another tab).</summary>
        public bool IsEmailVerified => Auth.CurrentUser != null && Auth.CurrentUser.IsEmailVerified;

        /// <summary>Call from StudentEditProfileController's "Verify Email"
        /// button. Sends a verification link to the student's CURRENT email
        /// address (not a pending new one - that flow is
        /// SendEmailVerificationBeforeUpdatingEmailAsync inside UpdateProfile).
        /// Surface to the student that an unverified account can't be
        /// recovered if they lose access to it.</summary>
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
        /// (and CurrentStudent.Email/EmailVerified, which mirror Auth) reflect
        /// a link the student may have just clicked in their inbox - including
        /// a completed email change, since Auth.CurrentUser.Email only flips
        /// to the new address once that link is clicked. Call this when the
        /// edit-profile screen becomes visible so nothing shows stale state
        /// from earlier in the session.
        ///
        /// IMPORTANT: confirming a pending email change makes Firebase revoke
        /// the session's existing token server-side (expected - it's a
        /// security-sensitive change). So the very next ReloadAsync() after
        /// the student clicks the link doesn't come back with the new email;
        /// it FAILS with "the user's credential is no longer valid". While a
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
                    Debug.LogWarning($"[PlayerSessionManager] ReloadAsync failed (PendingEmail={PendingEmail}): {task.Exception}");

                    if (PendingEmail != null && IsInvalidCredentialError(task.Exception))
                    {
                        Debug.Log("[PlayerSessionManager] Invalidated credential while an email change was pending - treating as confirmed.");
                        PendingEmail = null;
                    }

                    onComplete?.Invoke(false);
                    return;
                }

                bool verified = !task.IsCanceled && user.IsEmailVerified;

                if (!task.IsCanceled && CurrentStudent != null)
                {
                    CurrentStudent.Email = user.Email;
                    CurrentStudent.EmailVerified = verified;
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
        /// SDK versions; add an ErrorCode check too if you want a second,
        /// more precise signal later.</summary>
        private static bool IsInvalidCredentialError(AggregateException ex)
        {
            var fbEx = ex?.InnerException as FirebaseException;
            if (fbEx == null) return false;

            return fbEx.Message.IndexOf("no longer valid", StringComparison.OrdinalIgnoreCase) >= 0
                || fbEx.Message.IndexOf("sign in again", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Starts (or restarts) background polling for a pending
        /// email change being confirmed. Lives on PlayerSessionManager - not
        /// on any UI screen - specifically so it keeps running across screen
        /// navigation (this GameObject is DontDestroyOnLoad and never gets
        /// disabled the way a screen's controller does when the student taps
        /// Back or the app swaps screens). CancelInvoke first avoids
        /// double-scheduling if this somehow gets called twice.</summary>
        private void StartPendingEmailPolling()
        {
            CancelInvoke(nameof(PollPendingEmailConfirmation));
            InvokeRepeating(nameof(PollPendingEmailConfirmation), emailConfirmationPollIntervalSeconds, emailConfirmationPollIntervalSeconds);
        }


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
                    LogoutStudent();
                    UIManager.Instance?.ShowStudentLogin();

                }
            });
        }

        // ---------------- Logout ----------------

        public void LogoutStudent()
        {
            CancelInvoke(nameof(PollPendingEmailConfirmation));
            StopStudentListener();

            // Clear the biometric opt-in for this uid before signing out, while
            // Auth.CurrentUser (and therefore its UserId) is still available -
            // otherwise a signed-out device would still have IsBiometricLoginAvailable
            // pointing at cached data that ClearCachedStudent is about to delete.
            var uid = Auth.CurrentUser?.UserId;
            if (uid != null) PlayerPrefs.DeleteKey(BiometricPrefKeyForUid(uid));

            // Same reasoning: wipe this uid's cached avatar file (see
            // CloudinaryAvatarUploadService.SaveAvatarLocally/DeleteLocalAvatar)
            // while we still have the uid, so a signed-out device - especially
            // a shared one - doesn't keep showing this student's photo to
            // whoever logs in next.
            if (uid != null) CloudinaryAvatarUploadService.Instance?.DeleteLocalAvatar(uid);

            Auth.SignOut();
            try { GoogleSignIn.DefaultInstance.SignOut(); } catch { /* wasn't signed in via Google - fine */ }
            CurrentStudent = null;
            PendingEmail = null;

            // Without this, TryRestoreSessionOffline would have nothing of
            // this student's to restore next launch - which is exactly
            // right, since they explicitly signed out.
            ClearCachedStudent();
        }

        // ---------------- Refresh ----------------

        /// <summary>Re-fetches `students/{uid}` and replaces CurrentStudent with the
        /// result. Screens should NOT call this just to open - CurrentStudent is
        /// kept in sync locally by ApplyQuizAttemptResult() (see below) and by
        /// WriteFullName(), covering every write path that currently touches the
        /// student doc, at zero extra reads. Keep this around as a manual/
        /// catch-all resync (e.g. a pull-to-refresh gesture, or recovering from a
        /// teacher-side edit made out of band) rather than an OnEnable habit.</summary>
        public void RefreshCurrentStudent(Action<bool> onComplete = null)
        {
            if (CurrentStudent == null) { onComplete?.Invoke(false); return; }

            var uid = CurrentStudent.Uid;
            FetchStudentDoc(uid, (ok, profile, error) =>
            {
                if (ok && CurrentStudent != null && CurrentStudent.Uid == uid && !StudentProfilesEqual(CurrentStudent, profile))
                {
                    CurrentStudent = profile;
                    OnStudentProfileChanged?.Invoke(CurrentStudent);
                }
                onComplete?.Invoke(ok);
            });
        }

        /// <summary>Patches CurrentStudent in place with the outcome of a just-submitted
        /// quiz attempt, instead of re-fetching students/{uid}. Call this from
        /// QuizService.SubmitQuizAttemptInternal right after its transaction commits -
        /// every field it writes (totalPoints, level, quizzesCompleted, badgesEarned)
        /// is already known at that point, so there's nothing left to read from
        /// Firestore. No-ops if nobody's signed in or the attempt belonged to a
        /// different uid than the one currently cached (shouldn't happen in practice,
        /// since SubmitQuizAttempt always reads CurrentStudent.Uid itself, but this
        /// keeps a stale/late callback from corrupting a newer session's cache).</summary>
        public void ApplyQuizAttemptResult(string studentUid, int newTotalPoints, int newLevel, List<string> newlyEarnedBadgeIds)
        {
            if (CurrentStudent == null || CurrentStudent.Uid != studentUid) return;

            CurrentStudent.TotalPoints = newTotalPoints;
            CurrentStudent.Level = newLevel;
            CurrentStudent.QuizzesCompleted += 1;

            if (newlyEarnedBadgeIds != null && newlyEarnedBadgeIds.Count > 0)
            {
                foreach (var badgeId in newlyEarnedBadgeIds)
                {
                    if (!CurrentStudent.BadgesEarned.Contains(badgeId))
                    {
                        CurrentStudent.BadgesEarned.Add(badgeId);
                    }
                }
            }

            // Optimistic UI update - don't wait for the listener's server round-trip
            // (or its local-write echo, which will match this already and no-op).
            OnStudentProfileChanged?.Invoke(CurrentStudent);
        }

        /// <summary>Writes a new Cloudinary avatar URL to students/{uid}.avatarUrl and
        /// updates CurrentStudent once Firestore confirms it - called by
        /// StudentEditProfileController right after CloudinaryAvatarUploadService
        /// reports a successful upload. Deliberately a single-field UpdateAsync
        /// rather than routing through UpdateProfile, since a photo change has none
        /// of Email's re-auth/verification requirements.</summary>
        public void UpdateAvatarUrl(string avatarUrl, Action<bool, string> onComplete)
        {
            if (CurrentStudent == null)
            {
                onComplete?.Invoke(false, "No signed-in student.");
                return;
            }

            string uid = CurrentStudent.Uid;
            Db.Collection("students").Document(uid).UpdateAsync("avatarUrl", avatarUrl).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[PlayerSessionManager] Could not save avatarUrl for '{uid}': {task.Exception}");
                    onComplete?.Invoke(false, "Could not save your photo. Please try again.");
                    return;
                }

                // Stale-callback guard, same reasoning as ApplyQuizAttemptResult - a
                // slow callback landing after logout/relogin should never touch a
                // different session's cache.
                if (CurrentStudent != null && CurrentStudent.Uid == uid)
                {
                    CurrentStudent.AvatarUrl = avatarUrl;
                    CacheStudent(CurrentStudent);
                    OnStudentProfileChanged?.Invoke(CurrentStudent);
                }

                onComplete?.Invoke(true, null);
            });
        }

        // ---------------- Helpers ----------------

        /// <summary>Fetches students/{uid} for fullName/level/stats, then mirrors
        /// Email/EmailVerified from Auth.CurrentUser rather than Firestore -
        /// email is never stored there. Callers of this (LoginStudent,
        /// ResolveGoogleStudent, RefreshCurrentStudent) all run after Auth
        /// already has a signed-in user, so Auth.CurrentUser is always
        /// available here.</summary>
        private void FetchStudentDoc(string uid, Action<bool, StudentProfile, string> onComplete)
        {
            Db.Collection("students").Document(uid).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                {
                    onComplete?.Invoke(false, null, "Student profile not found.");
                    return;
                }

                onComplete?.Invoke(true, BuildProfileFromSnapshot(uid, task.Result), null);
            });
        }

        /// <summary>Shared students/{uid} DocumentSnapshot -> StudentProfile mapping,
        /// used by both the one-shot FetchStudentDoc (login) and the real-time
        /// listener below, so the two never drift apart on field defaults.</summary>
        private StudentProfile BuildProfileFromSnapshot(string uid, DocumentSnapshot snap)
        {
            var authUser = Auth.CurrentUser;
            return new StudentProfile
            {
                Uid = uid,
                FullName = snap.GetValue<string>("fullName"),
                Email = authUser?.Email,
                EmailVerified = authUser?.IsEmailVerified ?? false,
                Level = snap.ContainsField("level") ? snap.GetValue<int>("level") : 1,
                TotalPoints = snap.ContainsField("totalPoints") ? snap.GetValue<int>("totalPoints") : 0,
                QuizzesCompleted = snap.ContainsField("quizzesCompleted") ? snap.GetValue<int>("quizzesCompleted") : 0,
                BadgesEarned = snap.ContainsField("badgesEarned")
                    ? new List<string>(snap.GetValue<List<string>>("badgesEarned"))
                    : new List<string>(),
                AvatarUrl = snap.ContainsField("avatarUrl") ? snap.GetValue<string>("avatarUrl") : null
            };
        }

        // ---------------- Real-time sync ----------------

        /// <summary>Sets CurrentStudent, fires OnStudentProfileChanged once (this is
        /// always the *first* known-good copy for this session, so it's always a
        /// real change), and attaches the live students/{uid} listener so any later
        /// change - local or remote - flows through ApplyIncomingSnapshot instead of
        /// requiring a screen to re-fetch.</summary>
        private void SetCurrentStudentAndListen(StudentProfile profile)
        {
            CurrentStudent = profile;
            CacheStudent(profile);
            OnStudentProfileChanged?.Invoke(CurrentStudent);
            StartStudentListener(profile.Uid);

            // Cache the avatar locally right at login too, not just whenever
            // Edit Profile happens to load it (see
            // CloudinaryAvatarUploadService.TryLoadLocalAvatar, and
            // StudentDashboardController/StudentProfileController, which now
            // both read from that cache). This way a student who signs in once
            // online has their photo available offline everywhere those
            // screens are shown, even if they never open Edit Profile this
            // session. Fire-and-forget: failure here (no connection, no
            // avatar set, etc.) just means those screens fall back to their
            // own network fetch/initials, same as before this existed.
            if (!string.IsNullOrEmpty(profile.AvatarUrl))
            {
                StartCoroutine(CacheAvatarFromNetwork(profile.AvatarUrl, profile.Uid));
            }

            // Opt this device+account into biometric sign-in the moment there's a
            // real, freshly-authenticated session to unlock later - but only if
            // this device can actually do a biometric/device-credential check at
            // all, so we don't set a flag that IsBiometricLoginAvailable would
            // then advertise on hardware that can't back it up.
            bool biometricAvailable = Biometric.IsAvailable(allowDeviceCredential: true);
            if (biometricAvailable)
            {
                // IsAvailable() only checks hardware/enrollment capability - it does NOT
                // flip the plugin's own internal "active" flag, which Authenticate()
                // requires to be on (this is exactly what BiometricFailureReason.Inactive
                // was telling us: we'd never called SetActive). Do that here.
                //
                // authenticate: false is deliberate - SetActive's default is `true`,
                // which per the plugin's README triggers its OWN biometric prompt
                // immediately to "verify the user before enabling". We don't want
                // that here: the student just proved who they are via password/Google
                // a moment ago, so prompting again right now would be a redundant,
                // confusing double-auth. Only persist our own opt-in flag once
                // SetActive confirms success, so IsBiometricLoginAvailable never
                // advertises a button that would just fail again on the next login.
                Biometric.SetActive(
                    value: true,
                    allowDeviceCredential: true,
                    authenticate: false,
                    onSuccess: () =>
                    {
                        SetBiometricLoginEnabled(true);
                    },
                    onFailure: reason =>
                    {
                        Debug.LogWarning($"[PlayerSessionManager] Biometric.SetActive(true) onFailure fired: {reason} - biometric opt-in not enabled this login.");
                    });

                // Confirmed via logcat: with authenticate:false, neither onSuccess nor
                // onFailure above ever fires - there's nothing for the plugin to verify
                // or report back on, since authenticate:false skips the OS prompt
                // entirely. Those callbacks appear to only apply to the authenticate:true
                // path. So don't depend on them - read the plugin's own synchronous
                // Biometric.IsActive property right after the call instead, and persist
                // our opt-in flag from that ground truth.
                if (Biometric.IsActive)
                {
                    SetBiometricLoginEnabled(true);
                }
            }
        }

        /// <summary>Downloads avatarUrl's raw bytes and writes them to uid's local
        /// avatar cache (CloudinaryAvatarUploadService.SaveAvatarLocally). Only
        /// called right after a fresh login (see SetCurrentStudentAndListen) - not
        /// on every RefreshCurrentStudent/listener echo - so this isn't
        /// re-downloading the same photo on every Firestore update, just making
        /// sure it's on disk by the time the student might go offline later in the
        /// session.</summary>
        private IEnumerator CacheAvatarFromNetwork(string avatarUrl, string uid)
        {
            using (var request = UnityWebRequest.Get(avatarUrl))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[PlayerSessionManager] Could not cache avatar for '{uid}' at login: {request.error}");
                    yield break;
                }

                CloudinaryAvatarUploadService.Instance?.SaveAvatarLocally(request.downloadHandler.data, uid);
            }
        }

        private void StartStudentListener(string uid)
        {
            StopStudentListener();

            _studentListener = Db.Collection("students").Document(uid)
                .Listen(snapshot => ApplyIncomingSnapshot(uid, snapshot));

            // Every session-start path (LoginStudent, LoginWithGoogle, account creation,
            // and TryRestoreSessionOffline) funnels through here, so this is the one place
            // that needs to trigger FCM topic reconciliation - see
            // ClassroomService.SyncClassroomSubscriptions() / FCMNotificationService.
            // Fire-and-forget and non-blocking: if there's no connectivity yet (e.g. right
            // after an offline restore) this silently no-ops rather than failing loudly,
            // same as the listener above just sitting and waiting for a connection.
            ClassroomService.Instance?.SyncClassroomSubscriptions();
        }

        private void StopStudentListener()
        {
            _studentListener?.Stop();
            _studentListener = null;
        }

        /// <summary>Callback for the live students/{uid} listener. Fires on every
        /// server round-trip AND on the local optimistic echo of our own writes
        /// (e.g. right after ApplyQuizAttemptResult patches CurrentStudent and the
        /// underlying transaction commits) - both of those echoes carry data that
        /// already matches the cache, so the equality check below is what keeps
        /// this from re-pushing an identical profile and causing a redundant UI
        /// redraw. Only a genuine change (a teacher edit, another device, or the
        /// server confirming a value CurrentStudent didn't already have) reaches
        /// subscribers.</summary>
        private void ApplyIncomingSnapshot(string uid, DocumentSnapshot snapshot)
        {
            // Stale callback from a listener we've already torn down (e.g. the
            // student logged out or logged into a different account) between the
            // server call going out and this callback arriving.
            if (CurrentStudent == null || CurrentStudent.Uid != uid) return;
            if (!snapshot.Exists) return;

            var incoming = BuildProfileFromSnapshot(uid, snapshot);
            if (StudentProfilesEqual(CurrentStudent, incoming)) return;

            CurrentStudent = incoming;
            CacheStudent(incoming);
            OnStudentProfileChanged?.Invoke(CurrentStudent);
        }

        private static bool StudentProfilesEqual(StudentProfile a, StudentProfile b)
        {
            if (a.FullName != b.FullName) return false;
            if (a.Level != b.Level) return false;
            if (a.TotalPoints != b.TotalPoints) return false;
            if (a.QuizzesCompleted != b.QuizzesCompleted) return false;
            if (a.AvatarUrl != b.AvatarUrl) return false;
            if (a.BadgesEarned.Count != b.BadgesEarned.Count) return false;
            for (int i = 0; i < a.BadgesEarned.Count; i++)
            {
                if (a.BadgesEarned[i] != b.BadgesEarned[i]) return false;
            }
            return true;
        }

        private static string DescribeAuthError(AggregateException ex)
        {
            if (ex?.InnerException is FirebaseException fbEx)
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