using System.Collections.Generic;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminDashboard.uxml ("Teacher Dashboard"). Attach to the
    /// same GameObject as UIManager (it uses RequireComponent(UIDocument) like
    /// the other screen controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the logout button, the header "Create New Classroom"
    ///    button, the empty-state "Create Your First Classroom" button, and
    ///    the three Quick Action cards
    ///  - Applies the green->blue gradient to the header at runtime
    ///  - Builds the "My Classrooms" list at runtime from a list of
    ///    ClassroomSummary (no classrooms yet -> shows the empty state, same
    ///    as the mock)
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetHeaderData() / SetDashboardStats() / SetClassrooms() /
    ///    SetRecentActivity() so admin/session code can push real values in
    ///    instead of the placeholder mock data.
    ///
    /// NOTE: Manage Quizzes / View Analytics / Gamification Configuration /
    /// View Details don't have dedicated screens yet, so their buttons just
    /// log a TODO - wire them up to UIManager.Show...() once those screens
    /// exist.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminDashboardController : MonoBehaviour
    {
        /// <summary>Plain data for a single row in the "My Classrooms" list.</summary>
        public struct ClassroomSummary
        {
            public string ClassroomId;
            public string Name;
            public string Code;
            public string Description;
            public int StudentCount;

            /// <summary>Drives the "Archived" badge on the card (see BuildClassroomCard()).
            /// Archived classrooms stay visible here (read-only from the student's
            /// perspective) so the teacher can still open View Details to review data or
            /// Unarchive - see AdminClassroomDetailController's Archive/Unarchive button.</summary>
            public bool IsArchived;

            public ClassroomSummary(string classroomId, string name, string code, string description, int studentCount, bool isArchived = false)
            {
                ClassroomId = classroomId;
                Name = name;
                Code = code;
                Description = description;
                StudentCount = studentCount;
                IsArchived = isArchived;
            }
        }

        /// <summary>Plain data for a single row in the "Recent Activity" list.</summary>
        public struct ActivityEntry
        {
            public string Text;
            public string TimeAgo;

            public ActivityEntry(string text, string timeAgo)
            {
                Text = text;
                TimeAgo = timeAgo;
            }
        }

        [Header("Gradient colors (matches Classroom / Quiz Selection: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Label _headerSubtitleLabel;
        private Button _logoutButton;
        private Button _profileButton;

        private Label _classroomsCountLabel;
        private Label _studentsCountLabel;
        private Label _avgScoreValueLabel;

        private Button _createClassroomButton;
        private Button _createFirstClassroomButton;

        private VisualElement _classroomsEmptyState;
        private VisualElement _classroomsList;

        private Button _manageQuizzesButton;
        private Button _viewAnalyticsButton;
        private Button _gamificationButton;

        private Label _recentActivityEmptyLabel;
        private VisualElement _recentActivityList;

        private List<ClassroomSummary> _currentClassrooms = new List<ClassroomSummary>();
        private List<ActivityEntry> _currentActivity = new List<ActivityEntry>();

        private void OnEnable()
        {
            Debug.Log("[AdminDashboardController] OnEnable called");

            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            // Get the root from UIManager's document (shared across all screens)
            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null)
                {
                    _root = uiDocument.rootVisualElement;
                }
            }

            // Fallback: use this component's own document
            if (_root == null && _document != null)
            {
                _root = _document.rootVisualElement;
            }

            if (_root == null)
            {
                Debug.LogError("[AdminDashboardController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshClassroomsUI();
            RefreshRecentActivityUI();

            LoadDashboardData();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _logoutButton?.UnregisterCallback<ClickEvent>(OnLogoutClicked);
            _profileButton?.UnregisterCallback<ClickEvent>(OnProfileClicked);
            _createClassroomButton?.UnregisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _createFirstClassroomButton?.UnregisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _manageQuizzesButton?.UnregisterCallback<ClickEvent>(OnManageQuizzesClicked);
            _viewAnalyticsButton?.UnregisterCallback<ClickEvent>(OnViewAnalyticsClicked);
            _gamificationButton?.UnregisterCallback<ClickEvent>(OnGamificationClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminDashboardController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _headerSubtitleLabel = _screenRoot.Q<Label>("header-subtitle-label");
            _logoutButton = _screenRoot.Q<Button>("logout-button");
            _profileButton = _screenRoot.Q<Button>("profile-button");

            _classroomsCountLabel = _screenRoot.Q<Label>("classrooms-count-label");
            _studentsCountLabel = _screenRoot.Q<Label>("students-count-label");
            _avgScoreValueLabel = _screenRoot.Q<Label>("avg-score-value-label");

            _createClassroomButton = _screenRoot.Q<Button>("create-classroom-button");
            _createFirstClassroomButton = _screenRoot.Q<Button>("create-first-classroom-button");

            _classroomsEmptyState = _screenRoot.Q<VisualElement>("classrooms-empty-state");
            _classroomsList = _screenRoot.Q<VisualElement>("classrooms-list");

            _manageQuizzesButton = _screenRoot.Q<Button>("manage-quizzes-button");
            _viewAnalyticsButton = _screenRoot.Q<Button>("view-analytics-button");
            _gamificationButton = _screenRoot.Q<Button>("gamification-button");

            _recentActivityEmptyLabel = _screenRoot.Q<Label>("recent-activity-empty-label");
            _recentActivityList = _screenRoot.Q<VisualElement>("recent-activity-list");

            Debug.Log($"[AdminDashboardController] Found create classroom button: {_createClassroomButton != null}, classrooms list: {_classroomsList != null}");
        }

        private void WireCallbacks()
        {
            _logoutButton?.RegisterCallback<ClickEvent>(OnLogoutClicked);
            _profileButton?.RegisterCallback<ClickEvent>(OnProfileClicked);
            _createClassroomButton?.RegisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _createFirstClassroomButton?.RegisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _manageQuizzesButton?.RegisterCallback<ClickEvent>(OnManageQuizzesClicked);
            _viewAnalyticsButton?.RegisterCallback<ClickEvent>(OnViewAnalyticsClicked);
            _gamificationButton?.RegisterCallback<ClickEvent>(OnGamificationClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push the signed-in teacher's name into the header subtitle.</summary>
        public void SetHeaderData(string teacherName)
        {
            if (_headerSubtitleLabel != null) _headerSubtitleLabel.text = $"Welcome back, {teacherName}";
        }

        /// <summary>Push real values into the three glass stat cards in the header.</summary>
        public void SetDashboardStats(int classroomCount, int studentCount, float avgScorePercent)
        {
            if (_classroomsCountLabel != null) _classroomsCountLabel.text = classroomCount.ToString("N0");
            if (_studentsCountLabel != null) _studentsCountLabel.text = studentCount.ToString("N0");
            if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(avgScorePercent)}%";
        }

        /// <summary>Push the teacher's classrooms into "My Classrooms". Pass an empty/null list to show the empty state.</summary>
        public void SetClassrooms(List<ClassroomSummary> classrooms)
        {
            _currentClassrooms = classrooms ?? new List<ClassroomSummary>();
            RefreshClassroomsUI();
        }

        /// <summary>Push recent activity entries. Pass an empty/null list to show "No recent activity yet".</summary>
        public void SetRecentActivity(List<ActivityEntry> entries)
        {
            _currentActivity = entries ?? new List<ActivityEntry>();
            RefreshRecentActivityUI();
        }

        // ---------------- Real data loading ----------------

        /// <summary>
        /// Pulls the signed-in admin's name and their real classrooms from
        /// AdminAuthService / AdminClassroomService and pushes them into the
        /// UI via SetHeaderData/SetDashboardStats/SetClassrooms. Without this,
        /// the dashboard just sits on its placeholder/empty state forever,
        /// since nothing else in the project calls those setters.
        /// </summary>
        private void LoadDashboardData()
        {
            var admin = AdminAuthService.Instance != null ? AdminAuthService.Instance.CurrentAdmin : null;
            if (admin == null)
            {
                Debug.LogWarning("[AdminDashboardController] No signed-in admin found; leaving dashboard empty.");
                SetClassrooms(null);
                return;
            }

            SetHeaderData(admin.FullName);

            if (AdminClassroomService.Instance == null)
            {
                Debug.LogWarning("[AdminDashboardController] AdminClassroomService not available; classrooms/stats won't load.");
                return;
            }

            AdminClassroomService.Instance.FetchMyClassrooms(records =>
            {
                var summaries = new List<ClassroomSummary>();
                int totalStudents = 0;

                if (records != null)
                {
                    foreach (var record in records)
                    {
                        summaries.Add(new ClassroomSummary(record.ClassroomId, record.Name, record.Code, record.Description, record.StudentCount, record.IsArchived));
                        totalStudents += record.StudentCount;
                    }
                }

                SetClassrooms(summaries);

                // No quiz/analytics service exists yet to source a real average
                // score, so we report 0% rather than fabricate a number. Swap
                // this out once that backend exists.
                SetDashboardStats(summaries.Count, totalStudents, 0f);
            });
        }

        // ---------------- My Classrooms ----------------

        private void RefreshClassroomsUI()
        {
            bool hasClassrooms = _currentClassrooms != null && _currentClassrooms.Count > 0;

            _classroomsEmptyState?.EnableInClassList("hidden", hasClassrooms);
            _classroomsList?.EnableInClassList("hidden", !hasClassrooms);

            if (_classroomsList == null) return;

            _classroomsList.Clear();

            if (!hasClassrooms) return;

            foreach (var classroom in _currentClassrooms)
            {
                _classroomsList.Add(BuildClassroomCard(classroom));
            }
        }

        private VisualElement BuildClassroomCard(ClassroomSummary classroom)
        {
            var card = new VisualElement();
            card.AddToClassList("classroom-card");
            if (classroom.IsArchived) card.AddToClassList("classroom-card-archived");

            var topRow = new VisualElement();
            topRow.AddToClassList("classroom-card-top-row");

            var nameLabel = new Label(classroom.Name);
            nameLabel.AddToClassList("classroom-name-label");

            var codeBadge = new VisualElement();
            codeBadge.AddToClassList("classroom-code-badge");
            var codeLabel = new Label(classroom.Code);
            codeLabel.AddToClassList("classroom-code-badge-label");
            codeBadge.Add(codeLabel);

            topRow.Add(nameLabel);
            topRow.Add(codeBadge);

            if (classroom.IsArchived)
            {
                var archivedBadge = new Label("Archived");
                archivedBadge.AddToClassList("classroom-archived-badge");
                topRow.Add(archivedBadge);
            }

            card.Add(topRow);

            var descLabel = new Label(string.IsNullOrEmpty(classroom.Description) ? "Description" : classroom.Description);
            descLabel.AddToClassList("classroom-description-label");
            if (string.IsNullOrEmpty(classroom.Description)) descLabel.AddToClassList("classroom-description-placeholder");
            card.Add(descLabel);

            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("classroom-card-bottom-row");

            var studentsRow = new VisualElement();
            studentsRow.AddToClassList("classroom-students-row");
            var studentsIcon = new Label("\U0001F465");
            studentsIcon.AddToClassList("classroom-students-icon");
            var studentsLabel = new Label($"{classroom.StudentCount} students");
            studentsLabel.AddToClassList("classroom-students-label");
            studentsRow.Add(studentsIcon);
            studentsRow.Add(studentsLabel);

            var viewDetailsButton = new Button(() => OnViewClassroomDetailsClicked(classroom)) { text = "View Details" };
            viewDetailsButton.AddToClassList("view-details-button");

            bottomRow.Add(studentsRow);
            bottomRow.Add(viewDetailsButton);
            card.Add(bottomRow);

            return card;
        }

        // ---------------- Recent Activity ----------------

        private void RefreshRecentActivityUI()
        {
            bool hasActivity = _currentActivity != null && _currentActivity.Count > 0;

            _recentActivityEmptyLabel?.EnableInClassList("hidden", hasActivity);
            _recentActivityList?.EnableInClassList("hidden", !hasActivity);

            if (_recentActivityList == null) return;

            _recentActivityList.Clear();

            if (!hasActivity) return;

            foreach (var entry in _currentActivity)
            {
                var row = new VisualElement();
                row.AddToClassList("recent-activity-row");

                var textLabel = new Label(entry.Text);
                textLabel.AddToClassList("recent-activity-text");

                var timeLabel = new Label(entry.TimeAgo);
                timeLabel.AddToClassList("recent-activity-time");

                row.Add(textLabel);
                row.Add(timeLabel);
                _recentActivityList.Add(row);
            }
        }

        // ---------------- Button handlers ----------------

        private void OnLogoutClicked(ClickEvent evt)
        {
            Debug.Log("[AdminDashboardController] Logout tapped.");

            // TODO: replace with your real admin logout call, e.g.:
            // AdminAuthService.Instance.LogoutAdmin();
            UIManager.Instance.ShowAdminLogin();
        }

        private void OnProfileClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowAdminProfile();
        }

        private void OnCreateClassroomClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowAdminCreateClassroom();
        }

        private void OnViewClassroomDetailsClicked(ClassroomSummary classroom)
        {
            Debug.Log($"[AdminDashboardController] View Details tapped for classroom '{classroom.Name}' ({classroom.Code}).");

            string classroomId = classroom.ClassroomId;
            string classroomName = classroom.Name;
            string classroomCode = classroom.Code;
            int classroomStudentCount = classroom.StudentCount;
            float avgScorePercent = 0f; // TODO: fetch real average score from backend when available
            int quizCount = 0; // TODO: fetch real quiz count from backend when available

            UIManager.Instance.ShowAdminClassroomDetail(classroomId, classroomName, classroomCode, classroomStudentCount, avgScorePercent, quizCount);

        }

        private void OnManageQuizzesClicked(ClickEvent evt)
        {
            Debug.Log("[AdminDashboardController] Manage Quizzes tapped.");

            // TODO: navigate once an Admin Quiz Management screen exists, e.g.:
            UIManager.Instance.ShowAdminQuizManagement();

        }

        private void OnViewAnalyticsClicked(ClickEvent evt)
        {
            Debug.Log("[AdminDashboardController] View Analytics tapped.");
            UIManager.Instance.ShowAdminAnalytics();
            // TODO: navigate once an Analytics screen exists, e.g.:
            // UIManager.Instance.ShowAdminAnalytics();
        }

        private void OnGamificationClicked(ClickEvent evt)
        {
            Debug.Log("[AdminDashboardController] Gamification Configuration tapped.");

            // TODO: navigate once a Gamification Config screen exists, e.g.:
            UIManager.Instance.ShowGamificationConfig();
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Header Gradient (USS has no linear-gradient) ----------------

        private void ApplyHeaderGradient()
        {
            if (_header == null) return;

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
            }

            _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
            _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminDashboardHeaderGradientTexture"
            };

            for (int i = 0; i < size; i++)
            {
                float t = i / (float)(size - 1);
                tex.SetPixel(i, 0, Color.Lerp(start, end, t));
            }

            tex.Apply();
            return tex;
        }
    }
}