using UnityEngine;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class StudentDashboardController : MonoBehaviour
    {
        [Header("Gradient colors (matches the login screen)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f);
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot; // ADD THIS

        private VisualElement _header;
        private VisualElement _joinclassroomcard;

        private Button _menuButton;
        private Button _logoutButton;
        private Label _studentNameLabel;

        private Label _currentLevelLabel;
        private Label _nextLevelLabel;
        private VisualElement _progressFill;
        private Label _pointsToNextLabel;

        private Label _quizzesValueLabel;
        private Label _levelValueLabel;
        private Label _pointsValueLabel;

        private Button _explore3DButton;
        private Button _classroomHubButton;
        private Button _badgesButton;
        private Button _progressButton;
        private Button _joinClassroomButton;

        private void OnEnable()
        {
            Debug.Log("[StudentDashboardController] OnEnable called");

            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            // Get the root from UIManager's document
            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null)
                {
                    _root = uiDocument.rootVisualElement;
                }
            }

            // Fallback: use this component's document
            if (_root == null && _document != null)
            {
                _root = _document.rootVisualElement;
            }

            if (_root == null)
            {
                Debug.LogError("[StudentDashboardController] Root is null!");
                return;
            }

            // Unregister old callbacks first
            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();
            PopulateDashboard();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            // Clean up gradient texture if needed
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _menuButton?.UnregisterCallback<ClickEvent>(OnProfileClicked);
            _logoutButton?.UnregisterCallback<ClickEvent>(OnLogoutClicked);
            _explore3DButton?.UnregisterCallback<ClickEvent>(OnExplore3DClicked);
            _classroomHubButton?.UnregisterCallback<ClickEvent>(OnClassroomHubClicked);
            _badgesButton?.UnregisterCallback<ClickEvent>(OnBadgesClicked);
            _progressButton?.UnregisterCallback<ClickEvent>(OnProgressClicked);
            _joinClassroomButton?.UnregisterCallback<ClickEvent>(OnJoinClassroomClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {

            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentDashboardController] screen-root not found, using root directly");
                _screenRoot = _root;
            }
            else
            {
                Debug.Log("[StudentDashboardController] Found screen-root wrapper");
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _joinclassroomcard = _screenRoot.Q<VisualElement>("join-classroom-icon-box");

            _menuButton = _screenRoot.Q<Button>("menu-button");
            _logoutButton = _screenRoot.Q<Button>("logout-button");
            _studentNameLabel = _screenRoot.Q<Label>("student-name-label");

            _currentLevelLabel = _screenRoot.Q<Label>("current-level-label");
            _nextLevelLabel = _screenRoot.Q<Label>("next-level-label");
            _progressFill = _screenRoot.Q<VisualElement>("progress-fill");
            _pointsToNextLabel = _screenRoot.Q<Label>("points-to-next-label");

            _quizzesValueLabel = _screenRoot.Q<Label>("quizzes-value-label");
            _levelValueLabel = _screenRoot.Q<Label>("level-value-label");
            _pointsValueLabel = _screenRoot.Q<Label>("points-value-label");

            _explore3DButton = _screenRoot.Q<Button>("explore-3d-button");
            _classroomHubButton = _screenRoot.Q<Button>("classroom-hub-button");
            _badgesButton = _screenRoot.Q<Button>("badges-button");
            _progressButton = _screenRoot.Q<Button>("progress-button");
            _joinClassroomButton = _screenRoot.Q<Button>("join-classroom-button");

            Debug.Log($"[StudentDashboardController] Found Explore3D: {_explore3DButton != null}, Header: {_header != null}");
        }

        private void WireCallbacks()
        {
            if (_menuButton != null) _menuButton.RegisterCallback<ClickEvent>(OnProfileClicked);
            if (_logoutButton != null) _logoutButton.RegisterCallback<ClickEvent>(OnLogoutClicked);
            if (_explore3DButton != null) _explore3DButton.RegisterCallback<ClickEvent>(OnExplore3DClicked);
            if (_classroomHubButton != null) _classroomHubButton.RegisterCallback<ClickEvent>(OnClassroomHubClicked);
            if (_badgesButton != null) _badgesButton.RegisterCallback<ClickEvent>(OnBadgesClicked);
            if (_progressButton != null) _progressButton.RegisterCallback<ClickEvent>(OnProgressClicked);
            if (_joinClassroomButton != null) _joinClassroomButton.RegisterCallback<ClickEvent>(OnJoinClassroomClicked);

            // Re-evaluate the compact layout whenever the panel is resized
            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Data loading ----------------

        /// <summary>Pulls the signed-in student's profile from PlayerSessionManager and the
        /// level thresholds from AdminGamificationService, then feeds SetStudentData().
        /// Call whenever the dashboard is shown so it reflects the latest points/quiz count
        /// (e.g. right after finishing a quiz).</summary>
        private void PopulateDashboard()
        {
            var student = PlayerSessionManager.Instance?.CurrentStudent;
            if (student == null)
            {
                Debug.LogWarning("[StudentDashboardController] No signed-in student found - " +
                    "showing the dashboard with placeholder data. Was this screen opened without " +
                    "going through login/create-account first?");
                return;
            }

            if (AdminGamificationService.Instance == null)
            {
                Debug.LogWarning("[StudentDashboardController] AdminGamificationService.Instance is null - " +
                    "falling back to raw points/quiz stats without level-progress math.");
                SetStudentData(student.FullName, student.Level, student.Level, 0f, 0, student.QuizzesCompleted, student.TotalPoints);
                return;
            }

            AdminGamificationService.Instance.FetchSettings(settings =>
            {
                var progress = AdminGamificationService.ComputeLevelProgress(settings, student.TotalPoints);

                SetStudentData(
                    studentName: student.FullName,
                    currentLevel: progress.level,
                    nextLevel: progress.nextLevel,
                    levelProgress01: progress.progress01,
                    pointsToNextLevel: progress.pointsToNext,
                    quizzesCompleted: student.QuizzesCompleted,
                    totalPoints: student.TotalPoints
                );
            });
        }

        public void SetStudentData(
            string studentName,
            int currentLevel,
            int nextLevel,
            float levelProgress01,
            int pointsToNextLevel,
            int quizzesCompleted,
            int totalPoints)
        {
            if (_studentNameLabel != null) _studentNameLabel.text = studentName;
            if (_currentLevelLabel != null) _currentLevelLabel.text = $"Level {currentLevel}";
            if (_nextLevelLabel != null) _nextLevelLabel.text = $"Level {currentLevel + 1}";
            if (_progressFill != null) _progressFill.style.width = new Length(Mathf.Clamp01(levelProgress01) * 100f, LengthUnit.Percent);
            if (_pointsToNextLabel != null) _pointsToNextLabel.text = $"{pointsToNextLevel} points to next level";
            if (_quizzesValueLabel != null) _quizzesValueLabel.text = quizzesCompleted.ToString();
            if (_levelValueLabel != null) _levelValueLabel.text = currentLevel.ToString();
            if (_pointsValueLabel != null) _pointsValueLabel.text = totalPoints.ToString();
        }

        // ---------------- Button handlers ----------------

        private void OnProfileClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentProfile();
        }

        private void OnLogoutClicked(ClickEvent evt)
        {
            Debug.Log("[DashboardController] Logout tapped.");
            PlayerSessionManager.Instance?.LogoutStudent();
            UIManager.Instance?.ShowStudentLogin();
        }

        private void OnExplore3DClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentExplore3d();
        }

        private void OnClassroomHubClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentClassroomHub();
        }

        private void OnBadgesClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentAchievements();
        }

        private void OnProgressClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentProgress();
        }

        private void OnJoinClassroomClicked(ClickEvent evt)
        {

            UIManager.Instance.ShowJoinClassroom();

        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradient ----------------

        private void ApplyGradients()
        {
            if (_header == null) return;
            var horizontal = BuildGradientTexture(gradientStart, gradientEnd, true);
            _header.style.backgroundImage = new StyleBackground(horizontal);
            _joinclassroomcard.style.backgroundImage = new StyleBackground(horizontal);
            _joinClassroomButton.style.backgroundImage = new StyleBackground(horizontal);
        }

        private Texture2D BuildGradientTexture(Color start, Color end, bool horizontal)
        {
            const int size = 64;
            var tex = new Texture2D(horizontal ? size : 1, horizontal ? 1 : size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int i = 0; i < size; i++)
            {
                float t = i / (float)(size - 1);
                Color c = Color.Lerp(start, end, t);
                if (horizontal) tex.SetPixel(i, 0, c);
                else tex.SetPixel(0, i, c);
            }

            tex.Apply();
            return tex;
        }
    }
}