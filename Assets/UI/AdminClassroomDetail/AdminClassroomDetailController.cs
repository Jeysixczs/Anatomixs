using System.Collections.Generic;
using Anatomia3D.Backend;
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
    ///    SetLeaderboard() / SetStudents() / SetAnnouncements() /
    ///    GetAnnouncements() so admin/session code can push real values in
    ///    instead of the mock data. The Quizzes tab's rows are built
    ///    entirely at runtime (see LoadQuizzesTab()) so there's no fixed-slot
    ///    setter for it anymore.
    ///
    /// The "Leaderboard" card in the Analytics tab (with its "Show to Students"
    /// toggle) is the full, live-ranked leaderboard - not a preview that links
    /// out to another screen. Toggling "Show to Students" controls whether
    /// students in this classroom can see it on their end.
    ///
    /// The Announcements tab lets the teacher post/delete announcements for
    /// this classroom, backed by Firestore's `classrooms/{id}/announcements`
    /// subcollection via AdminClassroomService (see LoadClassroomContent() /
    /// OnPostAnnouncementClicked() / OnDeleteAnnouncementClicked()). Posted
    /// announcements are cached in _liveAnnouncements and survive the
    /// UIManager's clear-and-rebuild screen transitions; forward
    /// GetAnnouncements() into every enrolled student's
    /// StudentClassroomDetailController.SetAnnouncements() (or, on the student
    /// side, just call ClassroomService.FetchAnnouncements() directly) so it
    /// shows up on their Overview tab.
    ///
    /// Quiz publishing (OnQuizToggleClicked, one per dynamically-built row),
    /// leaderboard visibility (OnShowToStudentsToggleClicked) and the
    /// roster/analytics rollups (Students + Analytics tabs) are likewise
    /// backed by AdminClassroomService - see LoadClassroomContent() /
    /// LoadQuizzesTab().
    ///
    /// The header stats (students-value-label / avg-score-value-label /
    /// quizzes-done-value-label) are seeded from whatever SetClassroomData()
    /// was called with (usually a placeholder from the dashboard - see
    /// AdminDashboardController.OnViewClassroomDetailsClicked), then
    /// overwritten with the real, live-computed values once
    /// LoadClassroomContent()'s FetchClassroomAnalytics() call resolves - see
    /// the end of that callback.
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

        // Archive
        private Button _archiveButton;
        private Label _archivedBadgeLabel;
        private VisualElement _archiveDialogOverlay;
        private Label _archiveDialogTitleLabel;
        private Label _archiveDialogMessageLabel;
        private Button _archiveDialogCancelButton;
        private Button _archiveDialogConfirmButton;

        /// <summary>Whether this classroom is currently archived. Archived classrooms are
        /// blocked from student access - see ClassroomService.FetchClassroomDetail /
        /// StudentClassroomDetailController.LoadClassroomContent() on the student side.</summary>
        public bool IsArchived { get; private set; }

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

        // Unenroll-student confirmation dialog. Mirrors the archive dialog's
        // overlay/title/message/cancel/confirm shape (see the Archive block below) -
        // add matching elements to the uxml with these ids if they're not there yet:
        // unenroll-dialog-overlay, unenroll-dialog-title-label,
        // unenroll-dialog-message-label, unenroll-dialog-cancel-button,
        // unenroll-dialog-confirm-button.
        private VisualElement _unenrollDialogOverlay;
        private Label _unenrollDialogTitleLabel;
        private Label _unenrollDialogMessageLabel;
        private Button _unenrollDialogCancelButton;
        private Button _unenrollDialogConfirmButton;

        // Which student the dialog is currently asking to remove - set by
        // OnRemoveStudentClicked(), read by OnUnenrollDialogConfirmClicked(), cleared once
        // the dialog closes either way.
        private string _pendingUnenrollStudentId;
        private string _pendingUnenrollStudentName;

        // Quizzes tab
        private VisualElement _quizzesEmptyState;
        private VisualElement _quizzesCard;
        private VisualElement _quizzesList;

        /// <summary>One dynamically-built row in the Quizzes tab, bound to a specific quiz id
        /// rather than a fixed uxml slot - see LoadQuizzesTab() / BuildQuizRow().</summary>
        private class QuizRow
        {
            public string QuizId;
            public Button ToggleButton;
            public bool Published;
        }

        // Rebuilt from scratch by LoadQuizzesTab() every time the admin's quiz library
        // (QuizService.FetchMyQuizzes()) is (re)loaded, so there's one row per quiz - not
        // just the first two.
        private readonly List<QuizRow> _quizRows = new();
        private List<string> _publishedQuizIds = new List<string>();

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

        /// <summary>An AnnouncementInfo plus the Firestore doc id it was loaded from
        /// (empty for announcements added via the legacy SetAnnouncements() overload
        /// that doesn't carry ids - those can still be shown, just can't be deleted
        /// from the backend).</summary>
        private class LiveAnnouncement
        {
            public string AnnouncementId;
            public AnnouncementInfo Info;
        }

        // Most recent first.
        private readonly List<LiveAnnouncement> _liveAnnouncements = new();

        /// <summary>Whether the leaderboard is currently visible to students in this classroom.</summary>
        public bool LeaderboardVisibleToStudents { get; private set; } = false;

        // Cached identity/state so it can be forwarded to the Leaderboard screen and
        // survives the UIManager's clear-and-rebuild screen transitions.
        private string _classroomId = "";
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
            SetLeaderboardVisibility(LeaderboardVisibleToStudents);
            SetArchived(IsArchived);
            _archiveDialogOverlay?.AddToClassList("hidden");
            _unenrollDialogOverlay?.AddToClassList("hidden");
            _pendingUnenrollStudentId = null;
            _pendingUnenrollStudentName = null;
            RefreshAnnouncementsUI();

            // Screen was re-enabled (e.g. switching tabs elsewhere and coming back)
            // with a classroom already loaded - refresh from Firestore.
            if (!string.IsNullOrEmpty(_classroomId)) LoadClassroomContent();
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

            _archiveButton?.UnregisterCallback<ClickEvent>(OnArchiveButtonClicked);
            _archiveDialogCancelButton?.UnregisterCallback<ClickEvent>(OnArchiveDialogCancelClicked);
            _archiveDialogConfirmButton?.UnregisterCallback<ClickEvent>(OnArchiveDialogConfirmClicked);

            _unenrollDialogCancelButton?.UnregisterCallback<ClickEvent>(OnUnenrollDialogCancelClicked);
            _unenrollDialogConfirmButton?.UnregisterCallback<ClickEvent>(OnUnenrollDialogConfirmClicked);

            _studentsTabButton?.UnregisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.UnregisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _analyticsTabButton?.UnregisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _announcementsTabButton?.UnregisterCallback<ClickEvent>(OnAnnouncementsTabClicked);

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

            _archiveButton = _screenRoot.Q<Button>("archive-button");
            _archivedBadgeLabel = _screenRoot.Q<Label>("archived-badge-label");
            _archiveDialogOverlay = _screenRoot.Q<VisualElement>("archive-dialog-overlay");
            _archiveDialogTitleLabel = _screenRoot.Q<Label>("archive-dialog-title-label");
            _archiveDialogMessageLabel = _screenRoot.Q<Label>("archive-dialog-message-label");
            _archiveDialogCancelButton = _screenRoot.Q<Button>("archive-dialog-cancel-button");
            _archiveDialogConfirmButton = _screenRoot.Q<Button>("archive-dialog-confirm-button");

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

            _unenrollDialogOverlay = _screenRoot.Q<VisualElement>("unenroll-dialog-overlay");
            _unenrollDialogTitleLabel = _screenRoot.Q<Label>("unenroll-dialog-title-label");
            _unenrollDialogMessageLabel = _screenRoot.Q<Label>("unenroll-dialog-message-label");
            _unenrollDialogCancelButton = _screenRoot.Q<Button>("unenroll-dialog-cancel-button");
            _unenrollDialogConfirmButton = _screenRoot.Q<Button>("unenroll-dialog-confirm-button");

            _quizzesEmptyState = _screenRoot.Q<VisualElement>("quizzes-empty-state");
            _quizzesCard = _screenRoot.Q<VisualElement>("quizzes-card");
            _quizzesList = _screenRoot.Q<VisualElement>("quizzes-list");

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

            _archiveButton?.RegisterCallback<ClickEvent>(OnArchiveButtonClicked);
            _archiveDialogCancelButton?.RegisterCallback<ClickEvent>(OnArchiveDialogCancelClicked);
            _archiveDialogConfirmButton?.RegisterCallback<ClickEvent>(OnArchiveDialogConfirmClicked);

            _unenrollDialogCancelButton?.RegisterCallback<ClickEvent>(OnUnenrollDialogCancelClicked);
            _unenrollDialogConfirmButton?.RegisterCallback<ClickEvent>(OnUnenrollDialogConfirmClicked);

            _studentsTabButton?.RegisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.RegisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _analyticsTabButton?.RegisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _announcementsTabButton?.RegisterCallback<ClickEvent>(OnAnnouncementsTabClicked);

            _showToStudentsToggle?.RegisterCallback<ClickEvent>(OnShowToStudentsToggleClicked);

            _postAnnouncementButton?.RegisterCallback<ClickEvent>(OnPostAnnouncementClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>
        /// Push the classroom's identity and header stats into the screen, and kick off
        /// the Firestore loads for the Students / Quizzes / Analytics / Announcements
        /// tabs. Call from UIManager.ShowAdminClassroomDetail() right after showing this
        /// screen (its callers - AdminDashboardController's classroom list and
        /// AdminClassroomCreatedController.OnGoToDashboardClicked - need to pass the
        /// classroom's Firestore doc id, i.e. AdminClassroomService.ClassroomRecord.
        /// ClassroomId, as the new first argument).
        ///
        /// avgScorePercent/quizzesDone here are typically just placeholders (the caller
        /// usually doesn't have the real numbers yet) - LoadClassroomContent() overwrites
        /// students-value-label / avg-score-value-label / quizzes-done-value-label with
        /// live values from FetchClassroomAnalytics() a moment later, once that fetch
        /// resolves.
        /// </summary>
        public void SetClassroomData(string classroomId, string classroomName, string classroomCode, int studentCount, float avgScorePercent, int quizzesDone)
        {
            _classroomId = classroomId ?? "";
            _classroomName = classroomName ?? "";
            _classroomCode = classroomCode ?? "";

            if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
            if (_classroomCodeLabel != null) _classroomCodeLabel.text = _classroomCode;
            if (_studentsValueLabel != null) _studentsValueLabel.text = studentCount.ToString("N0");
            if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(avgScorePercent)}%";
            if (_quizzesDoneValueLabel != null) _quizzesDoneValueLabel.text = quizzesDone.ToString("N0");

            // Reset from whatever classroom was previously loaded into this reused screen -
            // LoadClassroomContent()'s FetchClassroomDetail callback will set the real value
            // once it resolves.
            SetArchived(false);

            LoadClassroomContent();
        }

        // ---------------- Loading from AdminClassroomService / QuizService ----------------

        /// <summary>Pulls publishedQuizIds + leaderboardVisible + the quiz library (for the
        /// Quizzes tab), announcements (for the Announcements tab), and the roster/
        /// leaderboard/rollup stats (for the Students and Analytics tabs, and now also
        /// the header stats - see the end of the FetchClassroomAnalytics callback below)
        /// - all from Firestore, replacing the old in-memory mock data.</summary>
        private void LoadClassroomContent()
        {
            if (string.IsNullOrEmpty(_classroomId))
            {
                Debug.LogWarning("[AdminClassroomDetailController] LoadClassroomContent called with no classroom id set.");
                return;
            }

            if (AdminClassroomService.Instance == null)
            {
                Debug.LogWarning("[AdminClassroomDetailController] AdminClassroomService not available yet.");
                return;
            }

            AdminClassroomService.Instance.FetchClassroomDetail(_classroomId, record =>
            {
                if (record == null) return;
                _publishedQuizIds = record.PublishedQuizIds ?? new List<string>();
                SetLeaderboardVisibility(record.LeaderboardVisible);
                SetArchived(record.IsArchived);
                LoadQuizzesTab();
            });

            AdminClassroomService.Instance.FetchAnnouncements(_classroomId, announcements =>
            {
                _liveAnnouncements.Clear();
                foreach (var a in announcements)
                {
                    _liveAnnouncements.Add(new LiveAnnouncement
                    {
                        AnnouncementId = a.AnnouncementId,
                        Info = new AnnouncementInfo(a.Title, a.Body, a.CreatedAt.ToDateTime().ToLocalTime().ToString("MMM d, yyyy"))
                    });
                }
                RefreshAnnouncementsUI();
            });

            AdminClassroomService.Instance.FetchClassroomAnalytics(_classroomId, analytics =>
            {
                SetAnalyticsOverview(analytics.TotalPointsEarned, analytics.TotalQuizzesCompleted, analytics.AvgScorePercent, analytics.ActiveStudents);
                SetLeaderboard(analytics.Leaderboard.ConvertAll(s => (s.Name, s.Points, s.QuizzesCompleted)));
                SetStudents(analytics.Students.ConvertAll(s => (s.StudentId, s.Name, s.Points, s.QuizzesCompleted)));

                // Header stats: SetClassroomData() seeded these with whatever the caller
                // had on hand (often a placeholder - e.g. AdminDashboardController passes
                // 0f/0 today, see OnViewClassroomDetailsClicked). Now that the real,
                // live-computed analytics are in, overwrite the header with those instead.
                // analytics.Students.Count comes straight off the classroom's `members`
                // subcollection (the same source SetStudents()/SetLeaderboard() just used),
                // so it can't drift from what the Students tab is showing.
                if (_studentsValueLabel != null) _studentsValueLabel.text = analytics.Students.Count.ToString("N0");
                if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(analytics.AvgScorePercent)}%";
                if (_quizzesDoneValueLabel != null) _quizzesDoneValueLabel.text = analytics.TotalQuizzesCompleted.ToString("N0");
            });
        }

        /// <summary>Rebuilds the Quizzes tab's list from scratch with one row per quiz in
        /// the admin's full quiz library (QuizService.FetchMyQuizzes()) - not just the
        /// first two - showing the empty state instead if they haven't created any yet.
        /// Each row's toggle reflects (and writes back to) this classroom's
        /// publishedQuizIds via AdminClassroomService.SetQuizPublished(); see
        /// BuildQuizRow() / OnQuizToggleClicked().</summary>
        private void LoadQuizzesTab()
        {
            if (QuizService.Instance == null)
            {
                Debug.LogWarning("[AdminClassroomDetailController] QuizService not available yet.");
                return;
            }

            QuizService.Instance.FetchMyQuizzes(records =>
            {
                _quizRows.Clear();
                _quizzesList?.Clear();

                bool hasQuizzes = records != null && records.Count > 0;
                _quizzesEmptyState?.EnableInClassList("hidden", hasQuizzes);
                _quizzesCard?.EnableInClassList("hidden", !hasQuizzes);

                if (!hasQuizzes || _quizzesList == null) return;

                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    bool published = !string.IsNullOrEmpty(record.QuizId) && _publishedQuizIds.Contains(record.QuizId);
                    _quizzesList.Add(BuildQuizRow(record.QuizId, record.Title, record.Category, published, i == records.Count - 1));
                }
            });
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

        /// <summary>Push the full student roster into the Students tab (empty state if the
        /// list is empty/null). Each row now carries studentId (needed for the remove-
        /// student action - see BuildStudentRow() / OnRemoveStudentClicked()).</summary>
        public void SetStudents(List<(string studentId, string name, int points, int quizzesCompleted)> students)
        {
            bool hasStudents = students != null && students.Count > 0;

            _studentsEmptyState?.EnableInClassList("hidden", hasStudents);
            _studentsListCard?.EnableInClassList("hidden", !hasStudents);

            if (_studentsList == null) return;
            _studentsList.Clear();

            if (!hasStudents) return;

            for (int i = 0; i < students.Count; i++)
            {
                var (studentId, name, points, quizzesCompleted) = students[i];
                _studentsList.Add(BuildStudentRow(studentId, name, points, quizzesCompleted, i == students.Count - 1));
            }
        }

        /// <summary>Sets a single quiz row's published state (and its toggle's visual state).</summary>
        private void SetQuizRowPublished(QuizRow row, bool published)
        {
            if (row == null) return;
            row.Published = published;
            row.ToggleButton?.EnableInClassList("toggle-on", published);
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

        private void OnQuizToggleClicked(QuizRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.QuizId) || string.IsNullOrEmpty(_classroomId)) return;

            bool wasPublished = row.Published;
            bool newState = !wasPublished;

            // Optimistic UI update; roll back on failure.
            SetQuizRowPublished(row, newState);

            AdminClassroomService.Instance.SetQuizPublished(_classroomId, row.QuizId, newState, (success, error) =>
            {
                if (success)
                {
                    if (newState) { if (!_publishedQuizIds.Contains(row.QuizId)) _publishedQuizIds.Add(row.QuizId); }
                    else _publishedQuizIds.Remove(row.QuizId);

                    Debug.Log($"[AdminClassroomDetailController] Quiz {row.QuizId} published: {newState}");
                    return;
                }

                Debug.LogWarning($"[AdminClassroomDetailController] Could not update quiz {row.QuizId} publish state: {error}");
                SetQuizRowPublished(row, wasPublished); // roll back
            });
        }

        private void OnShowToStudentsToggleClicked(ClickEvent evt)
        {
            if (string.IsNullOrEmpty(_classroomId)) return;

            bool wasVisible = LeaderboardVisibleToStudents;
            bool newState = !wasVisible;

            SetLeaderboardVisibility(newState);

            AdminClassroomService.Instance.SetLeaderboardVisibility(_classroomId, newState, (success, error) =>
            {
                if (success)
                {
                    Debug.Log($"[AdminClassroomDetailController] Leaderboard visible to students: {newState}");
                    return;
                }

                Debug.LogWarning($"[AdminClassroomDetailController] Could not update leaderboard visibility: {error}");
                SetLeaderboardVisibility(wasVisible); // roll back
            });
        }

        /// <summary>Sets the leaderboard's student-visibility state and updates the toggle's visual state.</summary>
        public void SetLeaderboardVisibility(bool visible)
        {
            LeaderboardVisibleToStudents = visible;
            _showToStudentsToggle?.EnableInClassList("toggle-on", visible);
        }

        // ---------------- Archive ----------------

        /// <summary>Reflects the classroom's archived state in the header badge and the
        /// Archive/Unarchive button label. Does not itself write to Firestore - see
        /// OnArchiveDialogConfirmClicked() for that.</summary>
        public void SetArchived(bool archived)
        {
            IsArchived = archived;
            _archivedBadgeLabel?.EnableInClassList("hidden", !archived);
            if (_archiveButton != null) _archiveButton.text = archived ? "Locked" : "Lock";
        }

        private void OnArchiveButtonClicked(ClickEvent evt)
        {
            if (string.IsNullOrEmpty(_classroomId) || _archiveDialogOverlay == null) return;

            if (IsArchived)
            {
                if (_archiveDialogTitleLabel != null) _archiveDialogTitleLabel.text = "Restore Classroom";
                if (_archiveDialogMessageLabel != null) _archiveDialogMessageLabel.text = "Students will be able to access this classroom again. Continue?";
                if (_archiveDialogConfirmButton != null) _archiveDialogConfirmButton.text = "Unlocked";
            }
            else
            {
                if (_archiveDialogTitleLabel != null) _archiveDialogTitleLabel.text = "Lock Classroom";
                if (_archiveDialogMessageLabel != null) _archiveDialogMessageLabel.text = "Are you sure you want to lock this classroom? Locked classrooms will no longer accept student access.";
                if (_archiveDialogConfirmButton != null) _archiveDialogConfirmButton.text = "Lock";
            }

            _archiveDialogOverlay.RemoveFromClassList("hidden");
        }

        private void OnArchiveDialogCancelClicked(ClickEvent evt)
        {
            _archiveDialogOverlay?.AddToClassList("hidden");
        }

        private void OnArchiveDialogConfirmClicked(ClickEvent evt)
        {
            _archiveDialogOverlay?.AddToClassList("hidden");

            if (string.IsNullOrEmpty(_classroomId) || AdminClassroomService.Instance == null) return;

            bool newState = !IsArchived;

            _archiveButton?.SetEnabled(false);

            AdminClassroomService.Instance.SetArchived(_classroomId, newState, (success, error) =>
            {
                _archiveButton?.SetEnabled(true);

                if (!success)
                {
                    Debug.LogWarning($"[AdminClassroomDetailController] Could not update archived state: {error}");
                    return;
                }

                SetArchived(newState);
                Debug.Log($"[AdminClassroomDetailController] Classroom {_classroomId} archived: {newState}");
            });
        }

        // ---------------- Unenroll student ----------------

        /// <summary>Tapping "Remove" on a student row - populates and shows the confirmation
        /// dialog rather than removing immediately, since this deletes the student's
        /// progress in this classroom and can't be undone from here.</summary>
        private void OnRemoveStudentClicked(string studentId, string studentName)
        {
            if (string.IsNullOrEmpty(_classroomId) || string.IsNullOrEmpty(studentId) || _unenrollDialogOverlay == null) return;

            _pendingUnenrollStudentId = studentId;
            _pendingUnenrollStudentName = studentName;

            if (_unenrollDialogTitleLabel != null) _unenrollDialogTitleLabel.text = "Remove Student";
            if (_unenrollDialogMessageLabel != null) _unenrollDialogMessageLabel.text =
                $"Remove {studentName} from this classroom? Their points and quiz history in this classroom will be lost. This can't be undone.";
            if (_unenrollDialogConfirmButton != null) _unenrollDialogConfirmButton.text = "Remove";

            _unenrollDialogOverlay.RemoveFromClassList("hidden");
        }

        private void OnUnenrollDialogCancelClicked(ClickEvent evt)
        {
            _unenrollDialogOverlay?.AddToClassList("hidden");
            _pendingUnenrollStudentId = null;
            _pendingUnenrollStudentName = null;
        }

        private void OnUnenrollDialogConfirmClicked(ClickEvent evt)
        {
            _unenrollDialogOverlay?.AddToClassList("hidden");

            if (string.IsNullOrEmpty(_classroomId) || string.IsNullOrEmpty(_pendingUnenrollStudentId) || AdminClassroomService.Instance == null)
            {
                _pendingUnenrollStudentId = null;
                _pendingUnenrollStudentName = null;
                return;
            }

            string studentId = _pendingUnenrollStudentId;
            string studentName = _pendingUnenrollStudentName;
            _pendingUnenrollStudentId = null;
            _pendingUnenrollStudentName = null;

            _unenrollDialogConfirmButton?.SetEnabled(false);

            AdminClassroomService.Instance.UnenrollStudent(_classroomId, studentId, (success, error) =>
            {
                _unenrollDialogConfirmButton?.SetEnabled(true);

                if (!success)
                {
                    Debug.LogWarning($"[AdminClassroomDetailController] Could not remove student {studentId}: {error}");
                    return;
                }

                Debug.Log($"[AdminClassroomDetailController] Removed student \"{studentName}\" ({studentId}) from classroom {_classroomId}");

                // Re-pull everything from Firestore rather than just deleting the row
                // locally - removing a student changes the header stats, the Analytics
                // tab's totals, and the Leaderboard, not just the Students list, and
                // LoadClassroomContent() already knows how to refresh all of that in
                // one call (see FetchClassroomAnalytics()'s callback).
                LoadClassroomContent();
            });
        }

        // ---------------- Announcements ----------------

        /// <summary>
        /// Returns a copy of this classroom's announcements, most recent first.
        /// Forward this into StudentClassroomDetailController.SetAnnouncements()
        /// for every student enrolled in this classroom (map each
        /// AnnouncementInfo across - the two types share the same Title/Body/
        /// DateText shape but are declared on different controllers).
        /// </summary>
        public List<AnnouncementInfo> GetAnnouncements() => _liveAnnouncements.ConvertAll(a => a.Info);

        /// <summary>Preload announcements without backend ids (e.g. for tests/mocks). Entries
        /// loaded this way can't be deleted from Firestore - LoadClassroomContent()'s
        /// AdminClassroomService.FetchAnnouncements() call is what normally populates
        /// this screen with real, deletable announcements.</summary>
        public void SetAnnouncements(List<AnnouncementInfo> announcements)
        {
            _liveAnnouncements.Clear();
            if (announcements != null)
            {
                foreach (var info in announcements) _liveAnnouncements.Add(new LiveAnnouncement { AnnouncementId = "", Info = info });
            }
            RefreshAnnouncementsUI();
        }

        private void RefreshAnnouncementsUI()
        {
            if (_announcementsList == null) return;

            _announcementsList.Clear();

            bool hasData = _liveAnnouncements.Count > 0;
            _announcementsEmptyState?.EnableInClassList("hidden", hasData);
            _announcementsList.EnableInClassList("hidden", !hasData);

            if (!hasData) return;

            foreach (var announcement in _liveAnnouncements)
            {
                _announcementsList.Add(BuildAnnouncementCard(announcement));
            }
        }

        private void OnPostAnnouncementClicked(ClickEvent evt)
        {
            if (string.IsNullOrEmpty(_classroomId))
            {
                Debug.LogWarning("[AdminClassroomDetailController] Post Announcement tapped with no classroom loaded - ignoring.");
                return;
            }

            string title = _announcementTitleField != null ? (_announcementTitleField.value ?? "").Trim() : "";
            string body = _announcementBodyField != null ? (_announcementBodyField.value ?? "").Trim() : "";

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body))
            {
                Debug.Log("[AdminClassroomDetailController] Post Announcement tapped with an empty title and message - ignoring.");
                return;
            }

            if (string.IsNullOrEmpty(title)) title = "Announcement";

            if (_announcementTitleField != null) _announcementTitleField.value = "";
            if (_announcementBodyField != null) _announcementBodyField.value = "";
            _postAnnouncementButton?.SetEnabled(false);

            AdminClassroomService.Instance.PostAnnouncement(_classroomId, title, body, (success, error, record) =>
            {
                _postAnnouncementButton?.SetEnabled(true);

                if (!success)
                {
                    Debug.LogWarning($"[AdminClassroomDetailController] Could not post announcement: {error}");
                    // Restore the text the teacher typed so they don't lose it.
                    if (_announcementTitleField != null) _announcementTitleField.value = title;
                    if (_announcementBodyField != null) _announcementBodyField.value = body;
                    return;
                }

                _liveAnnouncements.Insert(0, new LiveAnnouncement
                {
                    AnnouncementId = record.AnnouncementId,
                    Info = new AnnouncementInfo(record.Title, record.Body, record.CreatedAt.ToDateTime().ToLocalTime().ToString("MMM d, yyyy"))
                });
                RefreshAnnouncementsUI();

                Debug.Log($"[AdminClassroomDetailController] Posted announcement \"{record.Title}\" to classroom {_classroomId}");
            });
        }

        private void OnDeleteAnnouncementClicked(LiveAnnouncement announcement)
        {
            if (string.IsNullOrEmpty(_classroomId) || string.IsNullOrEmpty(announcement.AnnouncementId))
            {
                _liveAnnouncements.Remove(announcement);
                RefreshAnnouncementsUI();
                return;
            }

            AdminClassroomService.Instance.DeleteAnnouncement(_classroomId, announcement.AnnouncementId, (success, error) =>
            {
                if (!success)
                {
                    Debug.LogWarning($"[AdminClassroomDetailController] Could not delete announcement: {error}");
                    return;
                }

                _liveAnnouncements.Remove(announcement);
                RefreshAnnouncementsUI();

                Debug.Log($"[AdminClassroomDetailController] Deleted announcement \"{announcement.Info.Title}\" from classroom {_classroomId}");
            });
        }

        private VisualElement BuildAnnouncementCard(LiveAnnouncement announcement)
        {
            var card = new VisualElement();
            card.AddToClassList("announcement-card");

            var headerRow = new VisualElement();
            headerRow.AddToClassList("announcement-header-row");

            var titleLabel = new Label(announcement.Info.Title);
            titleLabel.AddToClassList("announcement-title-label");

            var dateLabel = new Label(announcement.Info.DateText);
            dateLabel.AddToClassList("announcement-date-label");

            headerRow.Add(titleLabel);
            headerRow.Add(dateLabel);
            card.Add(headerRow);

            if (!string.IsNullOrEmpty(announcement.Info.Body))
            {
                var bodyLabel = new Label(announcement.Info.Body);
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

        private VisualElement BuildStudentRow(string studentId, string name, int points, int quizzesCompleted, bool isLast)
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

            // Built at runtime like the announcement card's Delete button - no uxml
            // dependency for the button itself, just the confirmation dialog's elements
            // (see the "Unenroll-student confirmation dialog" fields above).
            var removeButton = new Button(() => OnRemoveStudentClicked(studentId, name)) { text = "Remove" };
            removeButton.AddToClassList("student-remove-button");
            row.Add(removeButton);

            return row;
        }

        private VisualElement BuildQuizRow(string quizId, string title, string category, bool published, bool isLast)
        {
            var row = new VisualElement();
            row.AddToClassList("quiz-row");
            if (isLast) row.AddToClassList("quiz-row-last");

            var textContainer = new VisualElement();
            textContainer.AddToClassList("quiz-row-text");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("quiz-row-title");

            var subjectLabel = new Label(category);
            subjectLabel.AddToClassList("quiz-row-subject");

            textContainer.Add(titleLabel);
            textContainer.Add(subjectLabel);

            var quizRow = new QuizRow { QuizId = quizId, Published = published };

            var toggle = new Button(() => OnQuizToggleClicked(quizRow));
            toggle.AddToClassList("toggle-switch");
            toggle.EnableInClassList("toggle-on", published);

            var knob = new VisualElement();
            knob.AddToClassList("toggle-knob");
            toggle.Add(knob);

            quizRow.ToggleButton = toggle;
            _quizRows.Add(quizRow);

            row.Add(textContainer);
            row.Add(toggle);
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