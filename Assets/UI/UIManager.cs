using System;
using System.Collections;
using System.Collections.Generic;
using Anatomia3D.Backend;
using Anatomia3D.UI.Animation;
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
      //  [SerializeField] private VisualTreeAsset studentQuizSelectionScreen;
        [SerializeField] private VisualTreeAsset studentQuizGameplayScreen;
        // File Submission assignment type - shown instead of studentQuizGameplayScreen
        // when the quiz's submissionType is "file" (see ShowStudentFileSubmission).
        [SerializeField] private VisualTreeAsset studentFileSubmissionScreen;
        // Teacher's "View Submissions" screen for a File Submission assignment
        // (see ShowAdminSubmissionReview) - separate asset from
        // adminQuizManagementScreen, reached from its quiz detail view.
        [SerializeField] private VisualTreeAsset adminSubmissionReviewScreen;
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
        private StudentQuizGameplayController _studentQuizGameplayController;
        private Anatomia3D.UI.StudentFileSubmissionController _studentFileSubmissionController;
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
        private Anatomia3D.UI.AdminSubmissionReviewController _adminSubmissionReviewController;
        private AdminAnalyticsReportsController _adminAnalyticsController;
        private AdminClassroomDetailController _adminClassroomDetailController;
        private AdminProfileController _adminProfileController;
        private AdminEditProfileController _adminEditProfileController;
        private AboutAnatomiaAdminController _aboutAnatomiaAdminController;

        private UIDocument _uiDocument;
        private VisualElement _root;

        // Student screens that play the enter animation (see PlayStudentScreenEnter).
        // Left out on purpose: the 3D anatomy screen and the timed quiz gameplay screen.
        private HashSet<VisualTreeAsset> _animatedScreens;

        [Header("Debug")]
        [Tooltip("Logs every screen animation start/finish to the Console.")]
        [SerializeField] private bool debugScreenAnimations;

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

            _animatedScreens = new HashSet<VisualTreeAsset>
            {
                studentLoginScreen, createAccountScreen, forgotPasswordScreen,
                studentDashboardScreen, studentExplore3dScreen, studentAchivementScreen,
                studentProfileScreen, studentClassroomScreen,
                studentFileSubmissionScreen, studentProgressScreen, studentQuizResultScreen,
                studentQuizResult, studentClassroomHubScreen, studentClassroomDetailScreen,
                studentEditProfileScreen, studentNotificationsScreen,
                // Admin screens (same layout names, so the same enter animation applies).
                adminLoginScreen, adminForgotPasswordScreen, adminCreateAccountScreen,
                adminDashboardScreen, adminCreateClassroomScreen, adminClassroomCreatedScreen,
                adminGamificationSettingsScreen, adminQuizManagementScreen, adminAnalyticsReportsScreen,
                adminClassroomDetailScreen, adminProfileScreen, adminEditProfileScreen,
                adminAboutAnatomiaScreen, adminSubmissionReviewScreen
            };
            _animatedScreens.Remove(null);   // unassigned inspector slots

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
           

            StartCoroutine(DecideInitialScreen());
        }

    
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
                    bool restored = PlayerSessionManager.Instance != null
                        && PlayerSessionManager.Instance.TryRestoreSessionOffline();

                    if (restored)
                    {
                        Debug.Log("[UIManager] Offline with no biometric hardware and a cached student session - skipping Login, opening Student Explore 3D.");
                        ShowStudentExplore3d();
                        yield break;
                    }

                   
                }
            }

            
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
            _studentQuizGameplayController = GetComponent<StudentQuizGameplayController>();
            _studentFileSubmissionController = GetComponent<Anatomia3D.UI.StudentFileSubmissionController>();
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
            _adminSubmissionReviewController = GetComponent<Anatomia3D.UI.AdminSubmissionReviewController>();
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
            if (BaselineAssessmentService.Instance != null)
            {
                BaselineAssessmentService.Instance.GetStatus((pretestDone, _) =>
                {
                    if (!pretestDone)
                        ShowStudentAnatomyScreenForBaselineAssessment(BaselineAssessmentType.Pretest);
                    else
                        ShowScreen(studentDashboardScreen, _studentdashboardController);
                });
                return;
            }

            ShowScreen(studentDashboardScreen, _studentdashboardController);
        }

       
        public void ShowStudentDashboardSkipBaselineGate()
        {
            ShowScreen(studentDashboardScreen, _studentdashboardController);
        }

        public void ShowStudentExplore3d()
        {
            ShowScreen(studentExplore3dScreen, _studentExplore3dController);
        }

       
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

        /// <summary>Opens the File Submission screen instead of
        /// StudentQuizGameplayController - called from
        /// StudentClassroomDetailController.OnStartQuizClicked when the tapped
        /// quiz's SubmissionType is SubmissionTypes.File.</summary>
        /// <param name="classroomId">Same role as in ShowStudentQuizGameplay - the
        /// classroom this assignment was opened from.</param>
        /// <param name="classroomName">/<param name="instructorName">Carried through
        /// only so the screen's back button can return to
        /// ShowStudentClassroomDetailOnQuizzesTab with the same header the student
        /// already saw, without an extra Firestore read.</param>
        public void ShowStudentFileSubmission(string classroomId, string quizId, string classroomName, string instructorName)
        {
            ShowScreen(studentFileSubmissionScreen, _studentFileSubmissionController, () =>
            {
                _studentFileSubmissionController?.LoadAssignment(classroomId, quizId, classroomName, instructorName);
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

      
        private BaselineAssessmentController GetBaselineAssessmentController()
        {
            var controller = _studentAnatomyScreenController != null
                ? _studentAnatomyScreenController.GetComponent<BaselineAssessmentController>()
                : null;

            if (controller == null)
                Debug.LogWarning("[UIManager] No BaselineAssessmentController was found on the Anatomy Screen GameObject.");

            return controller;
        }

        public void ShowStudentAnatomyScreenForBaselineAssessment(BaselineAssessmentType type)
        {
            
            _studentAnatomyScreenController?.SetAnatomySystem(AnatomySystem.Skeletal);

           
            GetBaselineAssessmentController()?.PrepareCombinedSession();

            ShowScreen(studentAnatomyScreen, _studentAnatomyScreenController, () =>
            {
                var baseline = _studentAnatomyScreenController != null
                    ? _studentAnatomyScreenController.GetComponent<BaselineAssessmentController>()
                    : null;

                if (baseline != null)
                    baseline.RequestBaselineAssessmentOnOpen(type);
                else
                    Debug.LogWarning("[UIManager] ShowStudentAnatomyScreenForBaselineAssessment: no " +
                                      "BaselineAssessmentController was found on the Anatomy Screen GameObject.");
            });
        }

        public void ShowStudentQuizResult(
            string quizName,
            int correctCount,
            int incorrectCount,
            int pointsEarned,
            int pointsPossible,
            int timeSpentSeconds = 0)
        {
            ShowScreen(studentQuizResultScreen, _studentQuizResultController, () =>
            {
                _studentQuizResultController?.SetResult(quizName, correctCount, incorrectCount, pointsEarned, pointsPossible, timeSpentSeconds);
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

 
        public void ShowStudentClassroomDetailOnQuizzesTab(string classroomId, string classroomName, string instructorName)
        {
            ShowScreen(studentClassroomDetailScreen, _studentClassroomDetailController, () =>
            {
                if (_studentClassroomDetailController == null) return;

          
                _studentClassroomDetailController.SetClassroomIdentity(classroomId, classroomName, instructorName);
                _studentClassroomDetailController.OpenQuizzesTab();
            });
        }

       
        public void ShowStudentClassroomDetailOnMaterialsTab(string classroomId, string classroomName, string instructorName)
        {
            ShowScreen(studentClassroomDetailScreen, _studentClassroomDetailController, () =>
            {
                if (_studentClassroomDetailController == null) return;

              
                _studentClassroomDetailController.SetClassroomIdentity(classroomId, classroomName, instructorName);
                _studentClassroomDetailController.OpenMaterialsTab();
            });
        }

        public void ShowStudentEditProfile(string fullName, string email)
        {
            ShowScreen(studentEditProfileScreen, _studentEditProfileController, () =>
            {
                _studentEditProfileController?.LoadProfileData(fullName, email);
            });
        }

      
        private Action _notificationsReturnAction;

        /// <param name="returnAction">Call this to go back to the screen that's
        /// opening Notifications, e.g. pass ShowStudentProfile from Profile's
        /// notification bell. Defaults to ShowStudentDashboard when omitted.</param>
        public void ShowStudentNotifications(Action returnAction = null)
        {
            _notificationsReturnAction = returnAction ?? ShowStudentDashboard;
            ShowScreen(studentNotificationsScreen, _studentNotificationsController);
        }

     
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

        
        public void ShowAdminSubmissionReview(string quizId, string classroomId, string quizTitle, int pointsPossible, int passingScorePercent)
        {
            ShowScreen(adminSubmissionReviewScreen, _adminSubmissionReviewController, () =>
            {
                _adminSubmissionReviewController?.LoadSubmissions(quizId, classroomId, quizTitle, pointsPossible, passingScorePercent);
            });
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

            if (_animatedScreens != null && _animatedScreens.Contains(screenAsset))
            {
                PlayStudentScreenEnter();
            }

            // Start coroutine to initialize the controller after UI is built
            if (controller != null)
            {
                StartCoroutine(InitializeControllerAfterUI(controller, onReady));
            }

            Debug.Log($"[UIManager] Showing {controller?.GetType().Name}");
        }

      
        private void PlayStudentScreenEnter()
        {
            UIAnimationUtility.DebugLog = debugScreenAnimations;

         
            UIAnimationUtility.AddPressFeedbackToAll(_root);

            var screenRoot = _root.Q<VisualElement>("screen-root") ?? _root.Q<VisualElement>("file-submission-root")
                            ?? _root.Q<VisualElement>("submission-review-root");
            UIAnimationUtility.FadeIn(screenRoot, duration: 0.35f);

            var header = _root.Q<VisualElement>("header") ?? _root.Q<VisualElement>("fs-header")
                         ?? _root.Q<VisualElement>("sr-header");
            UIAnimationUtility.FadeAndSlideIn(header, UIAnimationUtility.Direction.Top,
                duration: 0.45f, delay: 0.05f, distance: 60f);

            var body = _root.Q<VisualElement>("content-wrapper") ?? _root.Q<VisualElement>("fs-scroll");
            if (body == null) return;

            var sections = new List<VisualElement>();
            foreach (var child in body.Children())
            {
                if (child == header) continue;   // Dashboard keeps its header inside content-wrapper
                if (child.ClassListContains("hidden")) continue;   // e.g. admin banners hidden by USS

                if (child.name != null && child.name.EndsWith("-list") && child.childCount > 0)
                {
                    foreach (var item in child.Children()) sections.Add(item);
                }
                else
                {
                    sections.Add(child);
                }
            }

            UIAnimationUtility.StaggerIn(sections, UIAnimationUtility.Direction.Bottom,
                step: 0.07f, startDelay: 0.12f, duration: 0.4f, distance: 50f, maxCount: 8);
        }

      
        public void CloseKeyboard()
        {
            if (_root?.panel?.focusController != null)
            {
                var focused = _root.panel.focusController.focusedElement as VisualElement;
                focused?.Blur();
            }

           
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
              //  Debug.Log($"[UIManager] Controller initialized: {controller.GetType().Name}");
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
            if (_studentQuizGameplayController != null) _studentQuizGameplayController.enabled = false;
            if (_studentFileSubmissionController != null) _studentFileSubmissionController.enabled = false;
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
            if (_adminSubmissionReviewController != null) _adminSubmissionReviewController.enabled = false;
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


        public void InvalidateStudentClassroomHub()
        {
            _studentClassroomHubController?.InvalidateClassrooms();
        }

    }
}