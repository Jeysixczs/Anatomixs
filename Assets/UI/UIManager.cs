using System;
using System.Collections;
using Anatomia3D.Backend;
using Anatomia3D.UI.Quiz;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UnityEngine.Android;

    
namespace Anatomia3D.UI
{
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        [Header("UI Screens (Visual Tree Assets)")]
        [SerializeField] private VisualTreeAsset studentLoginScreen;
        [SerializeField] private VisualTreeAsset createAccountScreen;
        [SerializeField] private VisualTreeAsset forgotPasswordScreen;
        [SerializeField] private VisualTreeAsset studentDashboardScreen;
        [SerializeField] private VisualTreeAsset studentExplore3dScreen;
        [SerializeField] private VisualTreeAsset studentAchivementScreen;
        [SerializeField] private VisualTreeAsset studentProfileScreen;
        [SerializeField] private VisualTreeAsset studentClassroomScreen;
        [SerializeField] private VisualTreeAsset studentQuizSelectionScreen;
        [SerializeField] private VisualTreeAsset studentQuizGameplayScreen;
        [SerializeField] private VisualTreeAsset studentProgressScreen;
        [SerializeField] private VisualTreeAsset studentQuizResultScreen;
        [SerializeField] private VisualTreeAsset studentClassroomHubScreen;
        [SerializeField] private VisualTreeAsset studentClassroomDetailScreen;
        [SerializeField] private VisualTreeAsset studentEditProfileScreen;
        [SerializeField] private VisualTreeAsset studentNotificationsScreen;
        [SerializeField] private VisualTreeAsset studentAnatomyScreen;
        // not sure if dito sya nakalagay
        [SerializeField] private VisualTreeAsset studentQuizResult;

        // Controllers will be found automatically
        private StudentLoginController _studentLoginController;
        private CreateAccountController _createAccountController;
        private ForgotPasswordController _forgotPasswordController;
        private StudentDashboardController _studentdashboardController;
        private StudentExplore3dController _studentExplore3dController;
        private StudentAchievementsController _studentAchievementsController;
        private StudentProfileController _studentProfileController;
        private StudentClassroomController _studentClassroomController;
        private StudentQuizSelectionController _studentQuizSelectionController;
        private StudentQuizGameplayController _studentQuizGameplayController;
        private StudentQuizResultController _studentQuizResultController;
        private StudentProgressController _studentProgressController;
        private StudentClassroomHubController _studentClassroomHubController;
        private StudentClassroomDetailController _studentClassroomDetailController;
        private StudentEditProfileController _studentEditProfileController;
        private StudentNotificationsController _studentNotificationsController;
        private AnatomyScreenController _studentAnatomyScreenController;


        // ADMIN SCREENS

        [SerializeField] private VisualTreeAsset adminLoginScreen;
        [SerializeField] private VisualTreeAsset adminForgotPasswordScreen;
        [SerializeField] private VisualTreeAsset adminCreateAccountScreen;
        [SerializeField] private VisualTreeAsset adminDashboardScreen;
        [SerializeField] private VisualTreeAsset adminCreateClassroomScreen;
        [SerializeField] private VisualTreeAsset adminClassroomCreatedScreen;
        [SerializeField] private VisualTreeAsset adminGamificationSettingsScreen;
        [SerializeField] private VisualTreeAsset adminQuizManagementScreen;
        [SerializeField] private VisualTreeAsset adminAnalyticsReportsScreen;
        [SerializeField] private VisualTreeAsset adminClassroomDetailScreen;
        [SerializeField] private VisualTreeAsset adminProfileScreen;
        [SerializeField] private VisualTreeAsset adminEditProfileScreen;
        [SerializeField] private VisualTreeAsset adminAboutAnatomiaScreen;
        // ADMIN CONTROLLERS

        private AdminLoginController _adminLoginController;
        private AdminForgotPasswordController _adminForgotPasswordController;
        private AdminCreateAccountController _adminCreateAccountController;
        private AdminDashboardController _adminDashboardController;
        private AdminCreateClassroomController _adminCreateClassroomController;
        private AdminClassroomCreatedController _adminClassroomCreatedController;
        private AdminGamificationSettingsController _adminGamificationSettingsController;
        private AdminQuizManagementController _adminQuizManagementController;
        private AdminAnalyticsReportsController _adminAnalyticsController;
        private AdminClassroomDetailController _adminClassroomDetailController;
        private AdminProfileController _adminProfileController;
        private AdminEditProfileController _adminEditProfileController;
        private AboutAnatomiaAdminController _aboutAnatomiaAdminController;

        private UIDocument _uiDocument;
        private VisualElement _root;

        [SerializeField] private VisualTreeAsset aboutAnatomiaScreen;
        private AboutAnatomiaController _aboutAnatomiaController;


     
        private void Awake()
        {
            OfflineTextToSpeech.InitializeOnStartup();
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
                return;
            }

#if UNITY_ANDROID
            if (!Permission.HasUserAuthorizedPermission("android.permission.POST_NOTIFICATIONS"))
            {
                Permission.RequestUserPermission("android.permission.POST_NOTIFICATIONS");
            }
#endif

            _uiDocument = GetComponent<UIDocument>();
            if (_uiDocument == null)
            {
                _uiDocument = gameObject.AddComponent<UIDocument>();
            }

            _root = _uiDocument.rootVisualElement;

            // Find all controllers on this GameObject
            FindControllers();
        }

        private void Start()
        {
            // What happens next depends on connectivity AND biometric hardware -
            // see DecideInitialScreen for the actual logic. Short version: online
            // always shows Login; offline with biometric/PIN hardware also shows
            // Login (so the "Sign in with biometrics" button gates access - see
            // PlayerSessionManager.TryOfflineGate); offline with NO hardware
            // at all falls back to auto-restoring the last cached session
            // (PlayerSessionManager.TryRestoreSessionOffline) straight to Student
            // Explore 3D, since there'd be no way through Login at all otherwise.

            StartCoroutine(DecideInitialScreen());
        }

        /// <summary>Android's hardware back button and the gesture-nav back
        /// swipe both surface as the Escape key. UI Toolkit has no built-in
        /// "close keyboard on back" behavior the way native Android views
        /// do, so on gesture nav in particular the OS can otherwise
        /// intercept the gesture for the IME with Unity never seeing it, or
        /// see it and fall through to screen navigation while the keyboard
        /// stays open. Checking TouchScreenKeyboard.visible first and
        /// returning early makes back-to-close-keyboard the higher-priority
        /// action and stops it from also triggering screen navigation in the
        /// same press. Only handles the keyboard for now - this is NOT a
        /// general back-stack/back-navigation handler.
        ///
        /// Uses Keyboard.current from the new Input System package, NOT
        /// UnityEngine.Input.GetKeyDown - this project's Active Input
        /// Handling (Project Settings > Player) is set to "Input System
        /// Package (New)", under which the legacy Input class never
        /// receives events at all, so Input.GetKeyDown would silently
        /// always return false here.</summary>
        private void Update()
        {

            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Debug.Log($"[UIManager] Back pressed. TouchScreenKeyboard.visible={TouchScreenKeyboard.visible}, " +
                          $"focusedElement={_root?.panel?.focusController?.focusedElement?.GetType().Name ?? "null"}");

                if (TouchScreenKeyboard.visible)
                {
                    CloseKeyboard();
                    return;
                }
            }
        }
     


private IEnumerator DecideInitialScreen()
        {
            bool offline = Application.internetReachability == NetworkReachability.NotReachable;

            if (offline)
            {
                // FirebaseBootstrap's dependency check is async and can still be
                // running on this exact frame. This does NOT wait for network -
                // Auth becomes ready from Firebase's own on-device persisted state,
                // which doesn't require a connection - it just hasn't finished
                // initializing yet. Give it a short window so the Login screen's
                // biometric button (see
                // StudentLoginController.UpdateBiometricButtonVisibility /
                // PlayerSessionManager.IsBiometricLoginAvailable) reflects the real
                // Auth.CurrentUser state the moment it's shown, instead of coming
                // up hidden just because Firebase was a beat slow to initialize.
                float timeout = 3f;
                float elapsed = 0f;
                while ((FirebaseBootstrap.Instance == null || FirebaseBootstrap.Instance.Auth == null) && elapsed < timeout)
                {
                    yield return null;
                    elapsed += Time.unscaledDeltaTime;
                }

                bool hasBiometricHardware = PlayerSessionManager.Instance != null
                    && PlayerSessionManager.Instance.IsBiometricHardwareAvailable;

                if (!hasBiometricHardware)
                {
                    // Offline AND this device has no biometric/PIN hardware at all -
                    // there's no lock screen to put in front of the student either
                    // way, and typing a password is off the table without a
                    // connection. Login would just be a dead end here, so fall back
                    // to auto-restoring the last cached session instead of
                    // stranding the student on a screen with nothing they can do.
                    // A device WITH hardware still always goes to Login below, even
                    // offline - that's the biometric gate doing its job.
                    bool restored = PlayerSessionManager.Instance != null
                        && PlayerSessionManager.Instance.TryRestoreSessionOffline();

                    if (restored)
                    {
                        Debug.Log("[UIManager] Offline with no biometric hardware and a cached student session - skipping Login, opening Student Explore 3D.");
                        ShowStudentExplore3d();
                        yield break;
                    }

                    // Nothing cached to restore either (never logged in on this
                    // device) - fall through to Login, which will just show the
                    // normal (currently unusable-offline) password form. There's
                    // nothing better to offer a device with no prior session and no
                    // hardware.
                }
            }

            // Being offline no longer skips straight to the dashboard on its own
            // when there IS biometric hardware - that would bypass the security
            // check entirely. Login is shown, and a student who was previously
            // signed in on this device still gets in fast, but only via a real
            // biometric/device-credential check (the "Sign in with biometrics"
            // button). Typing a password still requires connectivity either way
            // (Firebase Auth needs a network round-trip), so biometrics is what
            // actually lets an offline student back in - see
            // PlayerSessionManager.TryOfflineGate, which is what restores the
            // cached profile once that check succeeds.
            ShowStudentLogin();
        }


        private void FindControllers()
        {
            _studentLoginController = GetComponent<StudentLoginController>();
            _createAccountController = GetComponent<CreateAccountController>();
            _forgotPasswordController = GetComponent<ForgotPasswordController>();
            _studentdashboardController = GetComponent<StudentDashboardController>();
            _studentExplore3dController = GetComponent<StudentExplore3dController>();
            _studentdashboardController = GetComponent<StudentDashboardController>();
            _studentAchievementsController = GetComponent<StudentAchievementsController>();
            _studentProfileController = GetComponent<StudentProfileController>();
            _studentClassroomController = GetComponent<StudentClassroomController>();
            _studentQuizSelectionController = GetComponent<StudentQuizSelectionController>();
            _studentQuizGameplayController = GetComponent<StudentQuizGameplayController>();
            _studentQuizResultController = GetComponent<StudentQuizResultController>();
            _studentProgressController = GetComponent<StudentProgressController>();
            _studentClassroomHubController = GetComponent<StudentClassroomHubController>();
            _studentClassroomDetailController = GetComponent<StudentClassroomDetailController>();
            _studentEditProfileController = GetComponent<StudentEditProfileController>();
            _studentNotificationsController = GetComponent<StudentNotificationsController>();
            _studentAnatomyScreenController = GetComponent<AnatomyScreenController>();

            //admin controllers
            _adminLoginController = GetComponent<AdminLoginController>();
            _adminCreateAccountController = GetComponent<AdminCreateAccountController>();
            _adminForgotPasswordController = GetComponent<AdminForgotPasswordController>();
            _adminDashboardController = GetComponent<AdminDashboardController>();
            _adminCreateClassroomController = GetComponent<AdminCreateClassroomController>();
            _adminClassroomCreatedController = GetComponent<AdminClassroomCreatedController>();
            _adminGamificationSettingsController = GetComponent<AdminGamificationSettingsController>();
            _adminQuizManagementController = GetComponent<AdminQuizManagementController>();
            _adminAnalyticsController = GetComponent<AdminAnalyticsReportsController>();
            _adminClassroomDetailController = GetComponent<AdminClassroomDetailController>();
            _adminProfileController = GetComponent<AdminProfileController>();
            _adminEditProfileController = GetComponent<AdminEditProfileController>();
            _aboutAnatomiaAdminController = GetComponent<AboutAnatomiaAdminController>();
            // Disable all controllers initially

            _aboutAnatomiaController = GetComponent<AboutAnatomiaController>();
            DisableAllControllers();
        }

        // ---------------- Screen Navigation ----------------

        public void ShowAboutAnatomia()
        {
            ShowScreen(aboutAnatomiaScreen, _aboutAnatomiaController);
        }
        public void ShowStudentLogin()
        {
            ShowScreen(studentLoginScreen, _studentLoginController);
        }

        public void ShowCreateAccount()
        {
            ShowScreen(createAccountScreen, _createAccountController);
        }

        public void ShowForgotPassword()
        {
            ShowScreen(forgotPasswordScreen, _forgotPasswordController);
        }

        public void ShowStudentDashboard()
        {
            ShowScreen(studentDashboardScreen, _studentdashboardController);
        }

        public void ShowStudentExplore3d()
        {
            ShowScreen(studentExplore3dScreen, _studentExplore3dController);
        }

        /// <summary>Centralized offline-routing entry point for any screen
        /// that requires an internet connection. This does NOT add a
        /// connectivity listener and does NOT run automatically - it only
        /// routes when a screen explicitly calls it after checking
        /// connectivity itself, e.g.:
        ///
        ///   if (Application.internetReachability == NetworkReachability.NotReachable)
        ///   {
        ///       UIManager.Instance?.OfflineDetected();
        ///       return;
        ///   }
        ///
        /// Student Explore 3D is the safe landing screen because it (and
        /// Play Mode launched from it) already works fully offline via
        /// AnatomyPlayModeLocalStorage/AnatomyPlayModeSyncService - nothing
        /// about that offline support is changed by this method. This is
        /// separate from the offline-at-launch handling in Start(), which
        /// stays exactly as-is.</summary>
        public void OfflineDetected()
        {
            ShowStudentExplore3d();
        }

       
        public void ShowStudentAchievements()
        {

            ShowScreen(studentAchivementScreen, _studentAchievementsController);
        }

        public void ShowStudentProfile()
        {

            ShowScreen(studentProfileScreen, _studentProfileController);
        }

        public void ShowJoinClassroom()
        {
            ShowScreen(studentClassroomScreen, _studentClassroomController);
        }

        public void ShowStudentQuizSelection()
        {
            ShowScreen(studentQuizSelectionScreen, _studentQuizSelectionController);
        }

        /// <param name="classroomId">The classroom this quiz was launched from (e.g. Student
        /// Classroom Detail's Available Quizzes tab). Threaded through to
        /// StudentQuizGameplayController so it can attach the correct classroomId to the
        /// quizAttempts doc on submit - pass null/empty for entry points with no classroom
        /// context.</param>
        public void ShowStudentQuizGameplay(string classroomId, string quizId)
        {
            ShowScreen(studentQuizGameplayScreen, _studentQuizGameplayController, () =>
            {
                _studentQuizGameplayController?.LoadQuiz(classroomId, quizId);
            });
        }

        /// <summary>Returns to the SAME in-progress quiz attempt after a round trip to
        /// the Anatomy Screen for an Image-Based question's "View on 3D Model" button
        /// (see AnatomyQuizHighlightController) - repaints the current question and
        /// resumes the countdown from wherever it was left, but never re-fetches the
        /// quiz or resets progress the way ShowStudentQuizGameplay above does.</summary>
        public void ShowStudentQuizGameplayResume()
        {
            ShowScreen(studentQuizGameplayScreen, _studentQuizGameplayController, () =>
            {
                _studentQuizGameplayController?.ResumeInProgressQuiz();
            });
        }

        /// <param name="startInPlayMode">Pass true from Student Explore 3D's Play
        /// Mode system picker so the Anatomy Screen comes up with Play Mode
        /// already active - the student picked "play the Skeletal System",
        /// not "explore it and then find the Play button". Every other
        /// caller (the normal Explore Mode cards) omits this and gets the
        /// existing Explore Mode behavior unchanged.</param>
        public void ShowStudentAnatomyScreen(AnatomySystem system, bool startInPlayMode = false)
        {
            _studentAnatomyScreenController?.SetAnatomySystem(system);
            ShowScreen(studentAnatomyScreen, _studentAnatomyScreenController, () =>
            {
                if (!startInPlayMode) return;

                // Play Mode itself still lives entirely on the Anatomy Screen's
                // GameObject (AnatomyPlayModeController) - this just requests
                // that it switch itself on as soon as its own UI is wired.
                // This is the ONLY way Play Mode is ever entered - the
                // Anatomy Screen no longer has its own Play Mode button.
                var playMode = _studentAnatomyScreenController != null
                    ? _studentAnatomyScreenController.GetComponent<AnatomyPlayModeController>()
                    : null;

                if (playMode != null)
                    playMode.RequestPlayModeOnOpen();
                else
                    Debug.LogWarning("[UIManager] ShowStudentAnatomyScreen: startInPlayMode was true but no " +
                                      "AnatomyPlayModeController was found on the Anatomy Screen GameObject.");
            });
        }

        /// <summary>Opens the same reusable Anatomy Screen, but in Teacher Selection
        /// Mode - called from Admin Quiz Management's Image-Based system cards (see
        /// AdminQuizManagementController.OpenAnatomyScreenForStructureSelection) so a
        /// teacher can pick the exact 3D structure that becomes a question's correct
        /// answer. Never used by any student-facing flow; Play Mode and Explore Mode
        /// are untouched by this path (see AnatomyTeacherSelectionController).</summary>
        public void ShowStudentAnatomyScreenForTeacherSelection(AnatomySystem system)
        {
            _studentAnatomyScreenController?.SetAnatomySystem(system);
            ShowScreen(studentAnatomyScreen, _studentAnatomyScreenController, () =>
            {
                var teacherSelection = _studentAnatomyScreenController != null
                    ? _studentAnatomyScreenController.GetComponent<AnatomyTeacherSelectionController>()
                    : null;

                if (teacherSelection != null)
                    teacherSelection.RequestTeacherSelectionModeOnOpen(system);
                else
                    Debug.LogWarning("[UIManager] ShowStudentAnatomyScreenForTeacherSelection: no " +
                                      "AnatomyTeacherSelectionController was found on the Anatomy Screen GameObject.");
            });
        }

        /// <summary>Opens the Anatomy Screen read-only, highlighting/focusing the exact
        /// structure a teacher picked for an Image-Based quiz question, WITHOUT
        /// revealing its name (see AnatomyQuizHighlightController) - called from the
        /// quiz card's "View on 3D Model" button. Back returns to the same in-progress
        /// attempt via ShowStudentQuizGameplayResume below, never to Student Explore 3D.</summary>
        public void ShowStudentAnatomyScreenForQuizHighlight(AnatomySystem system, string structureKey)
        {
            _studentAnatomyScreenController?.SetAnatomySystem(system);
            ShowScreen(studentAnatomyScreen, _studentAnatomyScreenController, () =>
            {
                var quizHighlight = _studentAnatomyScreenController != null
                    ? _studentAnatomyScreenController.GetComponent<AnatomyQuizHighlightController>()
                    : null;

                if (quizHighlight != null)
                    quizHighlight.RequestHighlightModeOnOpen(structureKey);
                else
                    Debug.LogWarning("[UIManager] ShowStudentAnatomyScreenForQuizHighlight: no " +
                                      "AnatomyQuizHighlightController was found on the Anatomy Screen GameObject.");
            });
        }

        public void ShowStudentQuizResult(
            string quizName,
            int correctCount,
            int incorrectCount,
            int pointsEarned,
            int pointsPossible)
        {
            ShowScreen(studentQuizResultScreen, _studentQuizResultController, () =>
            {
                _studentQuizResultController?.SetResult(quizName, correctCount, incorrectCount, pointsEarned, pointsPossible);
            });
        }

        public void ShowStudentProgress()
        {
            ShowScreen(studentProgressScreen, _studentProgressController);
        }

        public void ShowStudentClassroomHub()
        {
            ShowScreen(studentClassroomHubScreen, _studentClassroomHubController);
        }

        public void ShowStudentClassroomDetail(string classroomId, string classroomName, string instructorName)
        {
            ShowScreen(studentClassroomDetailScreen, _studentClassroomDetailController, () =>
            {
                _studentClassroomDetailController?.SetClassroomIdentity(classroomId, classroomName, instructorName);
            });
        }

        public void ShowStudentEditProfile(string fullName, string email)
        {
            ShowScreen(studentEditProfileScreen, _studentEditProfileController, () =>
            {
                _studentEditProfileController?.LoadProfileData(fullName, email);
            });
        }

        /// <summary>Screen to return to when the Notifications back button is
        /// tapped. Set by ShowStudentNotifications() to whichever screen opened
        /// it (Dashboard, Profile, ...); defaults to Dashboard if none was
        /// given, since that's the original/most common entry point.</summary>
        private Action _notificationsReturnAction;

        /// <param name="returnAction">Call this to go back to the screen that's
        /// opening Notifications, e.g. pass ShowStudentProfile from Profile's
        /// notification bell. Defaults to ShowStudentDashboard when omitted.</param>
        public void ShowStudentNotifications(Action returnAction = null)
        {
            _notificationsReturnAction = returnAction ?? ShowStudentDashboard;
            ShowScreen(studentNotificationsScreen, _studentNotificationsController);
        }

        /// <summary>Called by StudentNotificationsController's back button -
        /// returns to whichever screen opened Notifications.</summary>
        public void ReturnFromStudentNotifications()
        {
            var action = _notificationsReturnAction ?? ShowStudentDashboard;
            _notificationsReturnAction = null;
            action();
        }

        // ADMIN SCREENS

        public void ShowAdminLogin()
        {
            ShowScreen(adminLoginScreen, _adminLoginController);
        }


        public void ShowAdminForgotPassword()
        {
            ShowScreen(adminForgotPasswordScreen, _adminForgotPasswordController);
        }

        public void ShowAdminCreateAccount()
        {
            ShowScreen(adminCreateAccountScreen, _adminCreateAccountController);
        }
        public void ShowAdminDashboard()
        {
            ShowScreen(adminDashboardScreen, _adminDashboardController);
        }

        public void ShowAdminCreateClassroom()
        {
            ShowScreen(adminCreateClassroomScreen, _adminCreateClassroomController);
        }

        public void ShowAdminClassroomCreated(string classroomId, string classroomCode, string classroomName, int studentCount)
        {
            ShowScreen(adminClassroomCreatedScreen, _adminClassroomCreatedController, () =>
            {
                _adminClassroomCreatedController?.SetClassroomData(classroomId, classroomCode, classroomName, studentCount);
            });
        }

        public void ShowGamificationConfig()
        {
            ShowScreen(adminGamificationSettingsScreen, _adminGamificationSettingsController);

        }

        public void ShowAdminQuizManagement()
        {
            ShowScreen(adminQuizManagementScreen, _adminQuizManagementController);
        }

        public void ShowAdminAnalytics()
        {
            ShowScreen(adminAnalyticsReportsScreen, _adminAnalyticsController);
        }

        public void ShowAdminClassroomDetail(
            string classroomId,
            string classroomName,
            string classroomCode,
            int studentCount,
            float avgScorePercent,
            int quizCount)
        {
            ShowScreen(adminClassroomDetailScreen, _adminClassroomDetailController, () =>
            {
                _adminClassroomDetailController?.SetClassroomData(classroomId, classroomName, classroomCode, studentCount, avgScorePercent, quizCount);
            });
        }
        public void ShowAdminProfile()
        {
            ShowScreen(adminProfileScreen, _adminProfileController);
        }


        public void ShowAdminEditProfile(string fullName, string email)
        {
            ShowScreen(adminEditProfileScreen, _adminEditProfileController, () =>
            {
                _adminEditProfileController?.LoadProfileData(fullName, email);
            });
        }

        public void ShowAboutAnatomiaAdmin()
        {
            ShowScreen(adminAboutAnatomiaScreen, _aboutAnatomiaAdminController);
        }

        // ---------------- Core Screen Management ----------------

        private void ShowScreen(VisualTreeAsset screenAsset, MonoBehaviour controller, Action onReady = null)
        {
            if (screenAsset == null)
            {
                Debug.LogError($"Screen asset is null for {controller?.GetType().Name}");
                return;
            }

            // UI Toolkit doesn't release focus (or close the on-screen keyboard)
            // just because the focused element is about to be removed from the
            // tree. Force a blur here so the keyboard actually closes and no
            // stale focus state carries over to the next screen.
            CloseKeyboard();

            if (_root != null)
            {
                _root.Clear();
                _root.styleSheets.Clear();  
            }

            // Disable all controllers
            DisableAllControllers();

            // Clone the new screen
            screenAsset.CloneTree(_root);

            // Start coroutine to initialize the controller after UI is built
            if (controller != null)
            {
                StartCoroutine(InitializeControllerAfterUI(controller, onReady));
            }

            Debug.Log($"[UIManager] Showing {controller?.GetType().Name}");
        }

        /// <summary>
        /// Blurs whatever element currently has keyboard focus (e.g. a TextField
        /// left focused on the current screen) and closes the mobile on-screen
        /// keyboard if one is open. UI Toolkit does not do this automatically -
        /// not on tree rebuild, and not just because a button was clicked - so
        /// call this explicitly.
        ///
        /// ShowScreen() already calls this on every screen transition, so you
        /// don't need to call it yourself when navigating to another screen.
        /// Call it directly from a controller when a "Done"/"Save"/"Submit"
        /// action should close the keyboard WITHOUT necessarily leaving the
        /// current screen (e.g. StudentEditProfileController staying on-screen
        /// after a pending email change, or AnatomyPlayModeController's letter-box
        /// Submit).
        /// </summary>
        public void CloseKeyboard()
        {
            if (_root?.panel?.focusController != null)
            {
                var focused = _root.panel.focusController.focusedElement as VisualElement;
                focused?.Blur();
            }

            // Blur() alone is not reliable on real Android devices when focus
            // is dropped by script (e.g. navigating away) rather than the user
            // tapping somewhere else on screen - the IME window can be left
            // open even though UI Toolkit's own focus state is correctly
            // cleared. Ask Android's InputMethodManager directly as a backup.
            ForceHideAndroidKeyboard();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void ForceHideAndroidKeyboard()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity == null) return;

                    using (var view = activity.Call<AndroidJavaObject>("getCurrentFocus"))
                    {
                        if (view == null) return; // nothing focused - IME wasn't open on the native side

                        using (var inputMethodManager = activity.Call<AndroidJavaObject>("getSystemService", "input_method"))
                        using (var windowToken = view.Call<AndroidJavaObject>("getWindowToken"))
                        {
                            inputMethodManager?.Call<bool>("hideSoftInputFromWindow", windowToken, 0);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Never let a keyboard-close side effect break navigation.
                Debug.LogWarning($"[UIManager] ForceHideAndroidKeyboard failed: {e.Message}");
            }
        }
#else
        private void ForceHideAndroidKeyboard() { }
#endif

        private IEnumerator InitializeControllerAfterUI(MonoBehaviour controller, Action onReady = null)
        {
            // Wait for the UI to be fully built
            yield return new WaitForEndOfFrame();
            yield return null; // One more frame for safety

            // Now enable the controller - UI is ready
            if (controller != null)
            {
                controller.enabled = false;
                controller.enabled = true;
                onReady?.Invoke();
                Debug.Log($"[UIManager] Controller initialized: {controller.GetType().Name}");
            }
        }

        private void DisableAllControllers()
        {
            if (_studentLoginController != null) _studentLoginController.enabled = false;
            if (_createAccountController != null) _createAccountController.enabled = false;
            if (_forgotPasswordController != null) _forgotPasswordController.enabled = false;
            if (_studentdashboardController != null) _studentdashboardController.enabled = false;
            if (_studentExplore3dController != null) _studentExplore3dController.enabled = false;
            if (_studentAchievementsController != null) _studentAchievementsController.enabled = false;
            if (_studentProfileController != null) _studentProfileController.enabled = false;
            if (_studentClassroomController != null) _studentClassroomController.enabled = false;
            if (_studentQuizSelectionController != null) _studentQuizSelectionController.enabled = false;
            if (_studentQuizGameplayController != null) _studentQuizGameplayController.enabled = false;
            if (_studentQuizResultController != null) _studentQuizResultController.enabled = false;
            if (_studentProgressController != null) _studentProgressController.enabled = false;
            if (_studentClassroomHubController != null) _studentClassroomHubController.enabled = false;
            if (_studentClassroomDetailController != null) _studentClassroomDetailController.enabled = false;
            if (_studentEditProfileController != null) _studentEditProfileController.enabled = false;
            if (_studentNotificationsController != null) _studentNotificationsController.enabled = false;

            // Disable admin controllers

            if (_adminLoginController != null) _adminLoginController.enabled = false;
            if (_adminForgotPasswordController != null) _adminForgotPasswordController.enabled = false;
            if (_adminCreateAccountController != null) _adminCreateAccountController.enabled = false;
            if (_adminDashboardController != null) _adminDashboardController.enabled = false;
            if (_adminCreateClassroomController != null) _adminCreateClassroomController.enabled = false;
            if (_adminClassroomCreatedController != null) _adminClassroomCreatedController.enabled = false;
            if (_adminGamificationSettingsController != null) _adminGamificationSettingsController.enabled = false;
            if (_adminQuizManagementController != null) _adminQuizManagementController.enabled = false;
            if (_adminAnalyticsController != null) _adminAnalyticsController.enabled = false;
            if (_adminClassroomDetailController != null) _adminClassroomDetailController.enabled = false;
            if (_adminProfileController != null) _adminProfileController.enabled = false;
            if (_adminEditProfileController != null) _adminEditProfileController.enabled = false;


            if (_aboutAnatomiaAdminController != null) _aboutAnatomiaAdminController.enabled = false;
            if (_aboutAnatomiaController != null) _aboutAnatomiaController.enabled = false;
        }

        // ---------------- Public Methods ----------------

        public void UpdateDashboardData(string name, int level, int nextLevel, float progress, int pointsToNext, int quizzes, int totalPoints)
        {
            if (_studentdashboardController != null && _studentdashboardController.enabled)
            {
                _studentdashboardController.SetStudentData(name, level, nextLevel, progress, pointsToNext, quizzes, totalPoints);
            }
        }

        /// <summary>Call after this student's classroom enrollment changes (e.g. right
        /// after StudentClassroomController.OnJoinResult's join succeeds) so
        /// StudentClassroomHub re-fetches instead of showing a stale "My Classrooms"
        /// list next time it's opened. See StudentClassroomHubController.
        /// InvalidateClassrooms().</summary>
        public void InvalidateStudentClassroomHub()
        {
            _studentClassroomHubController?.InvalidateClassrooms();
        }

    }
}