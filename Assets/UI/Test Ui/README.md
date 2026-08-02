# Firebase setup for Anatomia 3D

## 1. Create the Firebase project
1. Go to the [Firebase console](https://console.firebase.google.com) → **Add project**.
2. Add an Android app with your package name (must match `Player Settings →
   Package Name` in Unity). Download `google-services.json` and drop it in
   `Assets/`.
3. **Build → Authentication → Sign-in method** → enable **Email/Password**.
4. **Build → Firestore Database → Create database** → start in production
   mode (the rules file below replaces the default-locked rules).
5. In the Firestore **Rules** tab, paste in `firestore.rules` from this
   folder and Publish.

## 2. Import the Unity SDK
Via Package Manager or the raw SDK zip, add these Firebase packages:
- `FirebaseAuth.unitypackage`
- `FirebaseFirestore.unitypackage`

Both pull in the shared `Firebase.App` and `Firebase.Extensions` (for
`ContinueWithOnMainThread`) dependencies automatically.

## 3. Add the scripts
Drop everything in `Scripts/` onto the **same persistent GameObject as
`UIManager`** (the one that survives scene loads):

- `FirebaseBootstrap` — must initialize before anything else touches Auth/Firestore
- `PlayerSessionManager` — student auth + session
- `AdminAuthService` — admin auth + session
- `ClassroomService` — student-side classroom join / "my classrooms"
- `AdminClassroomService` — admin-side classroom create / list / detail

Script Execution Order: put `FirebaseBootstrap` first (Edit → Project
Settings → Script Execution Order), since the other four read
`FirebaseBootstrap.Instance.Auth` / `.Db` in their own `Awake()`-adjacent
calls.

## 4. Wire the controller TODOs
Each controller already has a comment marking exactly where to call in:

| Controller | Replace stub with |
|---|---|
| `StudentLoginController.OnSignInClicked` | `PlayerSessionManager.Instance.LoginStudent(email, password, OnLoginResult)` |
| `CreateAccountController` | `PlayerSessionManager.Instance.CreateStudentAccount(...)` |
| `ForgotPasswordController.OnSendResetLinkClicked` | `PlayerSessionManager.Instance.SendPasswordResetEmail(...)` |
| `StudentEditProfileController.OnSaveChangesClicked` | `PlayerSessionManager.Instance.UpdateProfile(...)` / `.ChangePassword(...)` |
| `AdminLoginController.OnSecureLoginClicked` | `AdminAuthService.Instance.LoginAdmin(...)` |
| `AdminCreateAccountController` | `AdminAuthService.Instance.CreateAdminAccount(...)` |
| `AdminForgotPasswordController.OnSendResetLinkClicked` | `AdminAuthService.Instance.SendPasswordResetEmail(...)` |
| `AdminEditProfileController.OnSaveChangesClicked` | `AdminAuthService.Instance.UpdateProfile(...)` / `.ChangePassword(...)` |
| `StudentClassroomController.OnJoinClassroomSubmitClicked` | `ClassroomService.Instance.JoinClassroom(code, ...)` |
| `StudentClassroomHubController` (on show) | `ClassroomService.Instance.FetchMyClassrooms(...)` → `SetHeaderStats` / `SetClassrooms` |
| `AdminCreateClassroomController.OnCreateClassroomClicked` | `AdminClassroomService.Instance.CreateClassroom(name, description, ...)` |
| `AdminDashboardController` / `AdminClassroomDetailController` | `AdminClassroomService.Instance.FetchMyClassrooms(...)` / `.FetchClassroomDetail(...)` |

All callbacks follow the same `Action<bool, string>` (success, error
message) or `Action<bool, string, TRecord>` shape the existing stub
comments already imply, so dropping them in should be closer to
find-and-replace than a rewrite.

## 5. Not built yet
`FIRESTORE_SCHEMA.md` also defines `quizzes`, `quizAttempts`,
`students/{uid}/notifications`, and `gamificationSettings` — these back
`AdminQuizManagementController`, `StudentQuizSelectionController`,
`StudentQuizResultController`, `StudentProgressController`,
`StudentNotificationsController` and `AdminGamificationSettingsController`.
Say the word and I'll build `QuizService` and `NotificationService` next,
following the same pattern as the two classroom services here.
