using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminClassroomDetail.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button, the classroom-code copy buttons, the
    ///    Students/Quizzes/Analytics/Announcements tab switcher, the per-quiz
    ///    publish toggles, the "Show to Students" leaderboard-visibility
    ///    toggle, and the announcement compose form
    ///  - Applies the green->blue gradient (matches AdminDashboard) to the
    ///    header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetClassroomData() / SetAnalyticsOverview() /
    ///    SetLeaderboard() / SetStudents() / SetQuizPublished() /
    ///    SetAnnouncements() / GetAnnouncements() so admin/session code can
    ///    push real values in instead of the mock data
    ///
    /// The "Leaderboard" card in the Analytics tab (with its "Show to Students"
    /// toggle) is the full, live-ranked leaderboard - not a preview that links
    /// out to another screen. Toggling "Show to Students" controls whether
    /// students in this classroom can see it on their end.
    ///
    /// The Announcements tab lets the teacher post/delete announcements for
    /// this classroom. Posted announcements are held in-memory
    /// (_announcements) and survive the UIManager's clear-and-rebuild screen
    /// transitions; forward GetAnnouncements() into every enrolled student's
    /// StudentClassroomDetailController.SetAnnouncements() so it shows up on
    /// their Overview tab.
    ///
    /// Hook up your real backend calls inside OnQuizToggleClicked() /
    /// OnShowToStudentsToggleClicked() / OnPostAnnouncementClicked() /
    /// OnDeleteAnnouncementClicked() - e.g. call into your existing
    /// AdminClassroomService here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminClassroomDetailController : MonoBehaviour
    {
        [Header("Gradient colors (matches AdminDashboard: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f);
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private Label _classroomNameLabel;
        private Label _classroomCodeLabel;
        private Button _copyCodeButton;

        // Stats
        private Label _studentsValueLabel;
        private Label _avgScoreValueLabel;
        private Label _quizzesDoneValueLabel;

        // Tabs
        private Button _studentsTabButton;
        private Button _quizzesTabButton;
        private Button _analyticsTabButton;
        private Button _announcementsTabButton;
        private VisualElement _studentsPanel;
        private VisualElement _quizzesPanel;
        private VisualElement _analyticsPanel;
        private VisualElement _announcementsPanel;

        // Students tab
        private VisualElement _studentsEmptyState;
        private VisualElement _studentsListCard;
        private VisualElement _studentsList;
        private Button _copyClassroomCodeButton;

        // Quizzes tab
        private Button _quizToggle1;
        private Button _quizToggle2;
        public bool Quiz1Published { get; private set; } = true;
        public bool Quiz2Published { get; private set; } = false;

        // Analytics tab
        private Label _totalPointsValueLabel;
        private Label _totalQuizzesValueLabel;
        private Label _analyticsAvgScoreValueLabel;
        private Label _activeStudentsValueLabel;

        private Button _showToStudentsToggle;
        private VisualElement _topPerformersEmptyState;
        private VisualElement _topPerformersList;

        // Announcements tab
        private TextField _announcementTitleField;
        private TextField _announcementBodyField;
        private Button _postAnnouncementButton;
        private VisualElement _announcementsEmptyState;
        private VisualElement _announcementsList;

        /// <summary>A single posted announcement. Field shape matches
        /// StudentClassroomDetailController.AnnouncementInfo - forward the result
        /// of GetAnnouncements() into that screen's SetAnnouncements().</summary>
        public struct AnnouncementInfo
        {
            public string Title;
            public string Body;
            public string DateText;

            public AnnouncementInfo(string title, string body, string dateText)
            {
                Title = title;
                Body = body;
                DateText = dateText;
            }
        }

        // Most recent first.
        private readonly List<AnnouncementInfo> _announcements = new();

        /// <summary>Whether the leaderboard is currently visible to students in this classroom.</summary>
        public bool LeaderboardVisibleToStudents { get; private set; } = false;

        // Cached identity/state so it can be forwarded to the Leaderboard screen and
        // survives the UIManager's clear-and-rebuild screen transitions.
        private string _classroomCode = "";
        private string _classroomName = "";
        private readonly List<(string name, int points, int quizzesCompleted)> _lastTopPerformers = new();

        private void OnEnable()
        {
            Debug.Log("[AdminClassroomDetailController] OnEnable called");

            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null)
                {
                    _root = uiDocument.rootVisualElement;
                }
            }

            if (_root == null && _document != null)
            {
                _root = _document.rootVisualElement;
            }

            if (_root == null)
            {
                Debug.LogError("[AdminClassroomDetailController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            ShowStudentsTab();

            // Re-apply cached identity/state (survives screen rebuilds within the same session).
            if (!string.IsNullOrEmpty(_classroomName) || !string.IsNullOrEmpty(_classroomCode))
            {
                if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
                if (_classroomCodeLabel != null) _classroomCodeLabel.text = _classroomCode;
            }
            SetQuizPublished(1, Quiz1Published);
            SetQuizPublished(2, Quiz2Published);
            SetLeaderboardVisibility(LeaderboardVisibleToStudents);
            RefreshAnnouncementsUI();
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

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _copyCodeButton?.UnregisterCallback<ClickEvent>(OnCopyCodeClicked);
            _copyClassroomCodeButton?.UnregisterCallback<ClickEvent>(OnCopyCodeClicked);

            _studentsTabButton?.UnregisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.UnregisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _analyticsTabButton?.UnregisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _announcementsTabButton?.UnregisterCallback<ClickEvent>(OnAnnouncementsTabClicked);

            _quizToggle1?.UnregisterCallback<ClickEvent>(OnQuizToggle1Clicked);
            _quizToggle2?.UnregisterCallback<ClickEvent>(OnQuizToggle2Clicked);

            _showToStudentsToggle?.UnregisterCallback<ClickEvent>(OnShowToStudentsToggleClicked);

            _postAnnouncementButton?.UnregisterCallback<ClickEvent>(OnPostAnnouncementClicked);

            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminClassroomDetailController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");
            _classroomNameLabel = _screenRoot.Q<Label>("classroom-name-label");
            _classroomCodeLabel = _screenRoot.Q<Label>("classroom-code-label");
            _copyCodeButton = _screenRoot.Q<Button>("copy-code-button");

            _studentsValueLabel = _screenRoot.Q<Label>("students-value-label");
            _avgScoreValueLabel = _screenRoot.Q<Label>("avg-score-value-label");
            _quizzesDoneValueLabel = _screenRoot.Q<Label>("quizzes-done-value-label");

            _studentsTabButton = _screenRoot.Q<Button>("students-tab-button");
            _quizzesTabButton = _screenRoot.Q<Button>("quizzes-tab-button");
            _analyticsTabButton = _screenRoot.Q<Button>("analytics-tab-button");
            _announcementsTabButton = _screenRoot.Q<Button>("announcements-tab-button");
            _studentsPanel = _screenRoot.Q<VisualElement>("students-panel");
            _quizzesPanel = _screenRoot.Q<VisualElement>("quizzes-panel");
            _analyticsPanel = _screenRoot.Q<VisualElement>("analytics-panel");
            _announcementsPanel = _screenRoot.Q<VisualElement>("announcements-panel");

            _studentsEmptyState = _screenRoot.Q<VisualElement>("students-empty-state");
            _studentsListCard = _screenRoot.Q<VisualElement>("students-list-card");
            _studentsList = _screenRoot.Q<VisualElement>("students-list");
            _copyClassroomCodeButton = _screenRoot.Q<Button>("copy-classroom-code-button");

            _quizToggle1 = _screenRoot.Q<Button>("quiz-toggle-1");
            _quizToggle2 = _screenRoot.Q<Button>("quiz-toggle-2");

            _totalPointsValueLabel = _screenRoot.Q<Label>("total-points-value-label");
            _totalQuizzesValueLabel = _screenRoot.Q<Label>("total-quizzes-value-label");
            _analyticsAvgScoreValueLabel = _screenRoot.Q<Label>("analytics-avg-score-value-label");
            _activeStudentsValueLabel = _screenRoot.Q<Label>("active-students-value-label");

            _showToStudentsToggle = _screenRoot.Q<Button>("show-to-students-toggle");
            _topPerformersEmptyState = _screenRoot.Q<VisualElement>("top-performers-empty-state");
            _topPerformersList = _screenRoot.Q<VisualElement>("top-performers-list");

            _announcementTitleField = _screenRoot.Q<TextField>("announcement-title-field");
            _announcementBodyField = _screenRoot.Q<TextField>("announcement-body-field");
            _postAnnouncementButton = _screenRoot.Q<Button>("post-announcement-button");
            _announcementsEmptyState = _screenRoot.Q<VisualElement>("announcements-empty-state");
            _announcementsList = _screenRoot.Q<VisualElement>("announcements-list");

            Debug.Log($"[AdminClassroomDetailController] Found tabs: {_studentsTabButton != null}/{_quizzesTabButton != null}/{_analyticsTabButton != null}/{_announcementsTabButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _copyCodeButton?.RegisterCallback<ClickEvent>(OnCopyCodeClicked);
            _copyClassroomCodeButton?.RegisterCallback<ClickEvent>(OnCopyCodeClicked);

            _studentsTabButton?.RegisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.RegisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _analyticsTabButton?.RegisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _announcementsTabButton?.RegisterCallback<ClickEvent>(OnAnnouncementsTabClicked);

            _quizToggle1?.RegisterCallback<ClickEvent>(OnQuizToggle1Clicked);
            _quizToggle2?.RegisterCallback<ClickEvent>(OnQuizToggle2Clicked);

            _showToStudentsToggle?.RegisterCallback<ClickEvent>(OnShowToStudentsToggleClicked);

            _postAnnouncementButton?.RegisterCallback<ClickEvent>(OnPostAnnouncementClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push the classroom's identity and header stats into the screen.</summary>
        public void SetClassroomData(string classroomName, string classroomCode, int studentCount, float avgScorePercent, int quizzesDone)
        {
            _classroomName = classroomName ?? "";
            _classroomCode = classroomCode ?? "";

            if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
            if (_classroomCodeLabel != null) _classroomCodeLabel.text = _classroomCode;
            if (_studentsValueLabel != null) _studentsValueLabel.text = studentCount.ToString("N0");
            if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(avgScorePercent)}%";
            if (_quizzesDoneValueLabel != null) _quizzesDoneValueLabel.text = quizzesDone.ToString("N0");
        }

        /// <summary>Push real class-wide statistics into the Analytics tab's Performance Overview card.</summary>
        public void SetAnalyticsOverview(int totalPointsEarned, int totalQuizzesCompleted, float avgScorePercent, int activeStudents)
        {
            if (_totalPointsValueLabel != null) _totalPointsValueLabel.text = totalPointsEarned.ToString("N0");
            if (_totalQuizzesValueLabel != null) _totalQuizzesValueLabel.text = totalQuizzesCompleted.ToString("N0");
            if (_analyticsAvgScoreValueLabel != null) _analyticsAvgScoreValueLabel.text = $"{Mathf.RoundToInt(avgScorePercent)}%";
            if (_activeStudentsValueLabel != null) _activeStudentsValueLabel.text = activeStudents.ToString("N0");
        }

        /// <summary>
        /// Push the full, already-ranked (highest points first) student list into the
        /// inline "Leaderboard" card in the Analytics tab. This is the complete
        /// leaderboard, not a preview - every entry passed in is rendered.
        /// </summary>
        public void SetLeaderboard(List<(string name, int points, int quizzesCompleted)> rankedStudents)
        {
            _lastTopPerformers.Clear();
            if (rankedStudents != null) _lastTopPerformers.AddRange(rankedStudents);

            if (_topPerformersList == null) return;

            _topPerformersList.Clear();

            bool hasData = _lastTopPerformers.Count > 0;
            _topPerformersEmptyState?.EnableInClassList("hidden", hasData);
            _topPerformersList.EnableInClassList("hidden", !hasData);

            for (int i = 0; i < _lastTopPerformers.Count; i++)
            {
                var (name, points, _) = _lastTopPerformers[i];
                _topPerformersList.Add(BuildPerformerRow(i + 1, name, points));
            }
        }

        /// <summary>Push the full student roster into the Students tab (empty state if the list is empty/null).</summary>
        public void SetStudents(List<(string name, int points, int quizzesCompleted)> students)
        {
            bool hasStudents = students != null && students.Count > 0;

            _studentsEmptyState?.EnableInClassList("hidden", hasStudents);
            _studentsListCard?.EnableInClassList("hidden", !hasStudents);

            if (_studentsList == null) return;
            _studentsList.Clear();

            if (!hasStudents) return;

            for (int i = 0; i < students.Count; i++)
            {
                var (name, points, quizzesCompleted) = students[i];
                _studentsList.Add(BuildStudentRow(name, points, quizzesCompleted, i == students.Count - 1));
            }
        }

        /// <summary>Set whether a given quiz (1 or 2, matching the mock's two rows) is published to this classroom.</summary>
        public void SetQuizPublished(int quizNumber, bool published)
        {
            Button toggle = quizNumber == 1 ? _quizToggle1 : _quizToggle2;
            if (toggle == null) return;

            toggle.EnableInClassList("toggle-on", published);

            if (quizNumber == 1) Quiz1Published = published;
            else Quiz2Published = published;
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[AdminClassroomDetailController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        private void OnCopyCodeClicked(ClickEvent evt)
        {
            if (_classroomCodeLabel == null) return;
            GUIUtility.systemCopyBuffer = _classroomCodeLabel.text;
            Debug.Log($"[AdminClassroomDetailController] Copied classroom code: {_classroomCodeLabel.text}");
        }

        private void OnStudentsTabClicked(ClickEvent evt) => ShowStudentsTab();
        private void OnQuizzesTabClicked(ClickEvent evt) => ShowQuizzesTab();
        private void OnAnalyticsTabClicked(ClickEvent evt) => ShowAnalyticsTab();
        private void OnAnnouncementsTabClicked(ClickEvent evt) => ShowAnnouncementsTab();

        private void ShowStudentsTab()
        {
            SetActiveTab(_studentsTabButton, _studentsPanel);
        }

        private void ShowQuizzesTab()
        {
            SetActiveTab(_quizzesTabButton, _quizzesPanel);
        }

        private void ShowAnalyticsTab()
        {
            SetActiveTab(_analyticsTabButton, _analyticsPanel);
        }

        private void ShowAnnouncementsTab()
        {
            SetActiveTab(_announcementsTabButton, _announcementsPanel);
        }

        private void SetActiveTab(Button activeButton, VisualElement activePanel)
        {
            _studentsTabButton?.RemoveFromClassList("tab-button-active");
            _quizzesTabButton?.RemoveFromClassList("tab-button-active");
            _analyticsTabButton?.RemoveFromClassList("tab-button-active");
            _announcementsTabButton?.RemoveFromClassList("tab-button-active");
            activeButton?.AddToClassList("tab-button-active");

            _studentsPanel?.AddToClassList("hidden");
            _quizzesPanel?.AddToClassList("hidden");
            _analyticsPanel?.AddToClassList("hidden");
            _announcementsPanel?.AddToClassList("hidden");
            activePanel?.RemoveFromClassList("hidden");
        }

        private void OnQuizToggle1Clicked(ClickEvent evt)
        {
            SetQuizPublished(1, !Quiz1Published);
            Debug.Log($"[AdminClassroomDetailController] Quiz 1 published: {Quiz1Published}");

            // TODO: replace with your real backend call, e.g.:
            // AdminClassroomService.Instance.SetQuizPublished(_classroomCode, quizId: "skeletal-system-basics", Quiz1Published);
        }

        private void OnQuizToggle2Clicked(ClickEvent evt)
        {
            SetQuizPublished(2, !Quiz2Published);
            Debug.Log($"[AdminClassroomDetailController] Quiz 2 published: {Quiz2Published}");

            // TODO: replace with your real backend call, e.g.:
            // AdminClassroomService.Instance.SetQuizPublished(_classroomCode, quizId: "muscular-system-advanced", Quiz2Published);
        }

        private void OnShowToStudentsToggleClicked(ClickEvent evt)
        {
            SetLeaderboardVisibility(!LeaderboardVisibleToStudents);
            Debug.Log($"[AdminClassroomDetailController] Leaderboard visible to students: {LeaderboardVisibleToStudents}");

            // TODO: replace with your real backend call, e.g.:
            // AdminClassroomService.Instance.SetLeaderboardVisibility(_classroomCode, LeaderboardVisibleToStudents);
        }

        /// <summary>Sets the leaderboard's student-visibility state and updates the toggle's visual state.</summary>
        public void SetLeaderboardVisibility(bool visible)
        {
            LeaderboardVisibleToStudents = visible;
            _showToStudentsToggle?.EnableInClassList("toggle-on", visible);
        }

        // ---------------- Announcements ----------------

        /// <summary>
        /// Returns a copy of this classroom's announcements, most recent first.
        /// Forward this into StudentClassroomDetailController.SetAnnouncements()
        /// for every student enrolled in this classroom (map each
        /// AnnouncementInfo across - the two types share the same Title/Body/
        /// DateText shape but are declared on different controllers).
        /// </summary>
        public List<AnnouncementInfo> GetAnnouncements() => new List<AnnouncementInfo>(_announcements);

        /// <summary>Preload announcements from your backend (most recent first), e.g. when this screen is first opened for a classroom.</summary>
        public void SetAnnouncements(List<AnnouncementInfo> announcements)
        {
            _announcements.Clear();
            if (announcements != null) _announcements.AddRange(announcements);
            RefreshAnnouncementsUI();
        }

        private void RefreshAnnouncementsUI()
        {
            if (_announcementsList == null) return;

            _announcementsList.Clear();

            bool hasData = _announcements.Count > 0;
            _announcementsEmptyState?.EnableInClassList("hidden", hasData);
            _announcementsList.EnableInClassList("hidden", !hasData);

            if (!hasData) return;

            foreach (var announcement in _announcements)
            {
                _announcementsList.Add(BuildAnnouncementCard(announcement));
            }
        }

        private void OnPostAnnouncementClicked(ClickEvent evt)
        {
            string title = _announcementTitleField != null ? (_announcementTitleField.value ?? "").Trim() : "";
            string body = _announcementBodyField != null ? (_announcementBodyField.value ?? "").Trim() : "";

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body))
            {
                Debug.Log("[AdminClassroomDetailController] Post Announcement tapped with an empty title and message - ignoring.");
                return;
            }

            if (string.IsNullOrEmpty(title)) title = "Announcement";

            var announcement = new AnnouncementInfo(title, body, System.DateTime.Now.ToString("MMM d, yyyy"));
            _announcements.Insert(0, announcement);

            if (_announcementTitleField != null) _announcementTitleField.value = "";
            if (_announcementBodyField != null) _announcementBodyField.value = "";

            RefreshAnnouncementsUI();

            Debug.Log($"[AdminClassroomDetailController] Posted announcement \"{announcement.Title}\" to classroom {_classroomCode}");

            // TODO: replace with your real backend call, e.g.:
            // AdminClassroomService.Instance.PostAnnouncement(_classroomCode, announcement.Title, announcement.Body);
        }

        private void OnDeleteAnnouncementClicked(AnnouncementInfo announcement)
        {
            _announcements.Remove(announcement);
            RefreshAnnouncementsUI();

            Debug.Log($"[AdminClassroomDetailController] Deleted announcement \"{announcement.Title}\" from classroom {_classroomCode}");

            // TODO: replace with your real backend call, e.g.:
            // AdminClassroomService.Instance.DeleteAnnouncement(_classroomCode, announcement.Title);
        }

        private VisualElement BuildAnnouncementCard(AnnouncementInfo announcement)
        {
            var card = new VisualElement();
            card.AddToClassList("announcement-card");

            var headerRow = new VisualElement();
            headerRow.AddToClassList("announcement-header-row");

            var titleLabel = new Label(announcement.Title);
            titleLabel.AddToClassList("announcement-title-label");

            var dateLabel = new Label(announcement.DateText);
            dateLabel.AddToClassList("announcement-date-label");

            headerRow.Add(titleLabel);
            headerRow.Add(dateLabel);
            card.Add(headerRow);

            if (!string.IsNullOrEmpty(announcement.Body))
            {
                var bodyLabel = new Label(announcement.Body);
                bodyLabel.AddToClassList("announcement-body-label");
                card.Add(bodyLabel);
            }

            var deleteButton = new Button(() => OnDeleteAnnouncementClicked(announcement)) { text = "Delete" };
            deleteButton.AddToClassList("announcement-delete-button");
            card.Add(deleteButton);

            return card;
        }

        // ---------------- Row builders (built at runtime - lists are dynamic) ----------------

        private VisualElement BuildPerformerRow(int rank, string name, int points)
        {
            var row = new VisualElement();
            row.AddToClassList("performer-row");
            if (rank == 1) row.AddToClassList("performer-row-gold");
            else if (rank == 2) row.AddToClassList("performer-row-silver");
            else if (rank == 3) row.AddToClassList("performer-row-bronze");

            var badge = new VisualElement();
            badge.AddToClassList("performer-rank-badge");
            if (rank == 1) badge.AddToClassList("performer-rank-badge-gold");
            else if (rank == 2) badge.AddToClassList("performer-rank-badge-silver");
            else if (rank == 3) badge.AddToClassList("performer-rank-badge-bronze");

            var rankLabel = new Label(rank.ToString());
            rankLabel.AddToClassList("performer-rank-label");
            badge.Add(rankLabel);

            var nameLabel = new Label(name);
            nameLabel.AddToClassList("performer-name-label");

            var pointsLabel = new Label($"{points:N0} pts");
            pointsLabel.AddToClassList("performer-points-label");

            row.Add(badge);
            row.Add(nameLabel);
            row.Add(pointsLabel);
            return row;
        }

        private VisualElement BuildStudentRow(string name, int points, int quizzesCompleted, bool isLast)
        {
            var row = new VisualElement();
            row.AddToClassList("student-row");
            if (isLast) row.AddToClassList("student-row-last");

            var avatar = new VisualElement();
            avatar.AddToClassList("student-avatar");
            var initialsLabel = new Label(GetInitials(name));
            initialsLabel.AddToClassList("student-avatar-label");
            avatar.Add(initialsLabel);

            var info = new VisualElement();
            info.AddToClassList("student-info");
            var nameLabel = new Label(name);
            nameLabel.AddToClassList("student-name-label");
            var metaLabel = new Label($"{quizzesCompleted} quizzes completed");
            metaLabel.AddToClassList("student-meta-label");
            info.Add(nameLabel);
            info.Add(metaLabel);

            var pointsLabel = new Label($"{points:N0} pts");
            pointsLabel.AddToClassList("student-points-label");

            row.Add(avatar);
            row.Add(info);
            row.Add(pointsLabel);
            return row;
        }

        private static string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "?";
            var parts = fullName.Trim().Split(' ');
            if (parts.Length == 1) return parts[0].Substring(0, Mathf.Min(2, parts[0].Length)).ToUpper();
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradient (USS has no linear-gradient) ----------------

        private void ApplyHeaderGradient()
        {
            if (_header == null) return;

            if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
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
                name = "AdminClassroomDetailGradientTexture"
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
