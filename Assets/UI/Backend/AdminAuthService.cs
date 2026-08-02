using System;
using System.Collections.Generic;
using Firebase.Auth;
using Firebase.Extensions;
using Firebase.Firestore;
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
                batch.Set(Db.Collection("users").Document(uid), new
                {
                    role = "admin",
                    email = email,
                    createdAt = Timestamp.GetCurrentTimestamp()
                });
                batch.Set(Db.Collection("admins").Document(uid), new
                {
                    fullName = fullName,
                    email = email,
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

        /// <summary>Call from AdminEditProfileController.OnSaveChangesClicked().</summary>
        public void UpdateProfile(string fullName, string email, Action<bool, string> onComplete)
        {
            if (Auth.CurrentUser == null) { onComplete?.Invoke(false, "Not signed in."); return; }
            string uid = Auth.CurrentUser.UserId;

            Db.Collection("admins").Document(uid).UpdateAsync(new Dictionary<string, object>
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

                if (CurrentAdmin != null)
                {
                    CurrentAdmin.FullName = fullName;
                    CurrentAdmin.Email = email;
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

        // ---------------- Logout ----------------

        public void LogoutAdmin()
        {
            Auth.SignOut();
            CurrentAdmin = null;
        }

        // ---------------- Helpers ----------------

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
                    Email = snap.GetValue<string>("email"),
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
