using System;
using Firebase;
using Firebase.Auth;
using Firebase.Extensions;
using Firebase.Firestore;
using Google;
using UnityEngine;


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
            public string Email;
            public int Level;
            public int TotalPoints;
            public int QuizzesCompleted;
        }

        public StudentProfile CurrentStudent { get; private set; }
        public bool IsLoggedIn => CurrentStudent != null;

        private FirebaseAuth Auth => FirebaseBootstrap.Instance.Auth;
        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ---------------- Login ----------------

        /// <summary>Call from StudentLoginController.OnSignInClicked() after validation passes.</summary>
        public void LoginStudent(string email, string password, Action<bool, string> onComplete)
        {
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

                    CurrentStudent = profile;
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
#if UNITY_EDITOR
            onComplete?.Invoke(false, "Google sign-in needs a real Android/iOS build - it doesn't work in the Editor.");
            return;
#else
            ConfigureGoogleSignIn();

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
#endif
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

                        CurrentStudent = profile;
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
                Level = 1,
                TotalPoints = 0,
                QuizzesCompleted = 0
            };

            var batch = Db.StartBatch();
            batch.Set(Db.Collection("users").Document(uid), new
            {
                role = "student",
                email = email,
                createdAt = Timestamp.GetCurrentTimestamp()
            });
            batch.Set(Db.Collection("students").Document(uid), new
            {
                fullName = fullName,
                email = email,
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

                CurrentStudent = profile;
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
                    Level = 1,
                    TotalPoints = 0,
                    QuizzesCompleted = 0
                };

                var batch = Db.StartBatch();
                batch.Set(Db.Collection("users").Document(uid), new
                {
                    role = "student",
                    email = email,
                    createdAt = Timestamp.GetCurrentTimestamp()
                });
                batch.Set(Db.Collection("students").Document(uid), new
                {
                    fullName = fullName,
                    email = email,
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

                    CurrentStudent = profile;
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

        /// <summary>Call from StudentEditProfileController.OnSaveChangesClicked().</summary>
        public void UpdateProfile(string fullName, string email, Action<bool, string> onComplete)
        {
            if (Auth.CurrentUser == null) { onComplete?.Invoke(false, "Not signed in."); return; }
            string uid = Auth.CurrentUser.UserId;

            Db.Collection("students").Document(uid).UpdateAsync(new System.Collections.Generic.Dictionary<string, object>
            {
                { "fullName", fullName },
                { "email", email }
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not save your profile.");
                    return;
                }

                if (CurrentStudent != null)
                {
                    CurrentStudent.FullName = fullName;
                    CurrentStudent.Email = email;
                }
                onComplete?.Invoke(true, null);
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

        // ---------------- Logout ----------------

        public void LogoutStudent()
        {
            Auth.SignOut();
            try { GoogleSignIn.DefaultInstance.SignOut(); } catch { /* wasn't signed in via Google - fine */ }
            CurrentStudent = null;
        }

        // ---------------- Helpers ----------------

        private void FetchStudentDoc(string uid, Action<bool, StudentProfile, string> onComplete)
        {
            Db.Collection("students").Document(uid).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                {
                    onComplete?.Invoke(false, null, "Student profile not found.");
                    return;
                }

                var snap = task.Result;
                var profile = new StudentProfile
                {
                    Uid = uid,
                    FullName = snap.GetValue<string>("fullName"),
                    Email = snap.GetValue<string>("email"),
                    Level = snap.ContainsField("level") ? snap.GetValue<int>("level") : 1,
                    TotalPoints = snap.ContainsField("totalPoints") ? snap.GetValue<int>("totalPoints") : 0,
                    QuizzesCompleted = snap.ContainsField("quizzesCompleted") ? snap.GetValue<int>("quizzesCompleted") : 0
                };

                onComplete?.Invoke(true, profile, null);
            });
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