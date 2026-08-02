using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

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
        [SerializeField] private VisualTreeAsset studentProgressScreen;
        [SerializeField] private VisualTreeAsset studentQuizResultScreen;
        [SerializeField] private VisualTreeAsset studentClassroomHubScreen;
        [SerializeField] private VisualTreeAsset studentClassroomDetailScreen;
        [SerializeField] private VisualTreeAsset studentEditProfileScreen;
        [SerializeField] private VisualTreeAsset studentNotificationsScreen;

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
        private StudentQuizResultController _studentQuizResultController;
        private StudentProgressController _studentProgressController;
        private StudentClassroomHubController _studentClassroomHubController;
        private StudentClassroomDetailController _studentClassroomDetailController;
        private StudentEditProfileController _studentEditProfileController;
        private StudentNotificationsController _studentNotificationsController;


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

        private UIDocument _uiDocument;
        private VisualElement _root;

        [SerializeField] private VisualTreeAsset aboutAnatomiaScreen;
        private AboutAnatomiaController _aboutAnatomiaController;


        private void Awake()
        {
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
            // Show login screen by default
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
            _studentQuizResultController = GetComponent<StudentQuizResultController>();
            _studentProgressController = GetComponent<StudentProgressController>();
            _studentClassroomHubController = GetComponent<StudentClassroomHubController>();
            _studentClassroomDetailController = GetComponent<StudentClassroomDetailController>();
            _studentEditProfileController = GetComponent<StudentEditProfileController>();
            _studentNotificationsController = GetComponent<StudentNotificationsController>();

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

        public void ShowStudentQuizResult()
        {
            ShowScreen(studentQuizResultScreen, _studentQuizResultController);
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

        public void ShowStudentNotifications()
        {
            ShowScreen(studentNotificationsScreen, _studentNotificationsController);
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

        public void ShowAdminClassroomCreated(string classroomCode, string classroomName, int studentCount)
        {
            ShowScreen(adminClassroomCreatedScreen, _adminClassroomCreatedController, () =>
            {
                _adminClassroomCreatedController?.SetClassroomData(classroomCode, classroomName, studentCount);
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
            
            string classroomName,
            string classroomCode,
            int studentCount,
            float avgScorePercent,
            int quizCount)
        {
            ShowScreen(adminClassroomDetailScreen, _adminClassroomDetailController, () =>
            {
                _adminClassroomDetailController?.SetClassroomData(classroomName, classroomCode, studentCount, avgScorePercent ,quizCount);
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
        

        // ---------------- Core Screen Management ----------------

        private void ShowScreen(VisualTreeAsset screenAsset, MonoBehaviour controller, Action onReady = null)
        {
            if (screenAsset == null)
            {
                Debug.LogError($"Screen asset is null for {controller?.GetType().Name}");
                return;
            }

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

    }
}