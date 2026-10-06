using System.Collections.Generic;
using System.Linq;
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
    ///    Overview/Students/Quizzes/Analytics/Announcements/Materials tab switcher
    ///    (Overview is the default tab - see RefreshOverview()), the per-quiz
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
        [Header("Legacy gradient colors (only used when AnatomiaTheme.UseGradientChrome = true)")]
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

        // Hamburger drawer + section bar (mirrors StudentClassroomDetailController)
        private Button _menuButton;
        private Button _drawerCloseButton;
        private Button _drawerBackButton;
        private VisualElement _drawerScrim;
        private VisualElement _drawerPanel;
        private Label _drawerClassroomLabel;
        private Label _sectionTitleLabel;
        private ScrollView _screenScroll;

        // Archive
        private Button _archiveButton;
        private Label _archivedBadgeLabel;
        // Edit-details dialog (name / section / code) - opened from the drawer's "Edit Details" button.
        private Button _editDetailsButton;
        private VisualElement _editDialogOverlay;
        private TextField _editCodeField;   // "Classroom Code" - stored as the classroom `name`
        private TextField _editNameField;   // "Classroom Name" - stored as the classroom `description`
        private TextField _editSectionField;
        private Label _editDialogErrorLabel;
        private Button _editDialogCancelButton;
        private Button _editDialogSaveButton;

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
        private Button _overviewTabButton;
        private Button _studentsTabButton;
        private Button _quizzesTabButton;
        private Button _analyticsTabButton;
        private Button _announcementsTabButton;
        private Button _materialsTabButton;
        private VisualElement _overviewPanel;
        private VisualElement _studentsPanel;
        private VisualElement _quizzesPanel;
        private VisualElement _analyticsPanel;
        private VisualElement _announcementsPanel;
        private VisualElement _materialsPanel;

        // Overview tab. Read-only summary; everything here is derived from state the other
        // tabs already load (see RefreshOverview()), so it costs no extra Firestore reads.
        private Label _overviewNameValue;
        private Label _overviewSectionValue;
        private Label _overviewCodeValue;
        private Label _overviewTeacherValue;
        private Label _overviewStatusPill;
        private Label _overviewStudentsValue;
        private Label _overviewAvgValue;
        private Label _overviewQuizzesValue;
        private Label _overviewPointsValue;
        private VisualElement _overviewTopEmpty;
        private VisualElement _overviewTopList;
        private VisualElement _overviewAnnouncementEmpty;
        private VisualElement _overviewAnnouncementHost;
        private VisualElement _overviewMaterialsEmpty;
        private VisualElement _overviewMaterialsHost;
        private Button _overviewSeeAnalyticsButton;
        private Button _overviewSeeAnnouncementsButton;
        private Button _overviewSeeMaterialsButton;
        private string _classroomSection = "";
        private string _teacherName = "";
        private const int OverviewTopStudentCount = 3;
        private const int OverviewMaterialCount = 3;

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
            public VisualElement Card;
            public Label StatusPill;
            public Label FooterLabel;
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

        // Materials tab
        private TextField _materialTitleField;
        private TextField _materialDescriptionField;
        private Button _materialChooseFileButton;
        private VisualElement _materialSelectedFilePanel;
        private Label _materialSelectedFileName;
        private Label _materialSelectedFileSize;
        private Button _materialClearFileButton;
        private Label _materialStatusLabel;
        private Button _uploadMaterialButton;
        private VisualElement _materialsEmptyState;
        private VisualElement _materialsList;

        private readonly List<ClassroomMaterial> _materials = new();
        private string _pendingMaterialPath;
        private string _pendingMaterialName;
        private long _pendingMaterialSize;
        private bool _isUploadingMaterial;
        /// <summary>Delete is two taps (arm, then confirm) so a stray tap can't remove a file.</summary>
        private string _armedDeleteMaterialId;

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
        private readonly List<(string studentId, string name, int points, int quizzesCompleted)> _lastTopPerformers = new();

        private void OnEnable()
        {
            //Debug.Log("[AdminClassroomDetailController] OnEnable called");

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
                //Debug.LogError("[AdminClassroomDetailController] Root is null!");
                return;
            }

            UnregisterCallbacks();
            NetworkStatusMonitor.OnAppResumed -= HandleAppResumed;
            NetworkStatusMonitor.OnAppResumed += HandleAppResumed;

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            ShowOverviewTab();

            // Re-apply cached identity/state (survives screen rebuilds within the same session).
            if (!string.IsNullOrEmpty(_classroomName) || !string.IsNullOrEmpty(_classroomCode))
            {
                if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
                if (_drawerClassroomLabel != null) _drawerClassroomLabel.text = _classroomName;
                if (_classroomCodeLabel != null) _classroomCodeLabel.text = _classroomCode;
            }
            SetLeaderboardVisibility(LeaderboardVisibleToStudents);
            SetArchived(IsArchived);
            _archiveDialogOverlay?.AddToClassList("hidden");
            _editDialogOverlay?.AddToClassList("hidden");
            _unenrollDialogOverlay?.AddToClassList("hidden");
            _pendingUnenrollStudentId = null;
            _pendingUnenrollStudentName = null;
            RefreshAnnouncementsUI();
            RefreshMaterialsUI();
            RefreshOverview();

            // Screen was re-enabled (e.g. switching tabs elsewhere and coming back)
            // with a classroom already loaded - refresh from Firestore.
            if (!string.IsNullOrEmpty(_classroomId)) LoadClassroomContent();
        }

        private void OnDisable()
        {
            NetworkStatusMonitor.OnAppResumed -= HandleAppResumed;
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
            _drawerBackButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _menuButton?.UnregisterCallback<ClickEvent>(OnMenuClicked);
            _drawerCloseButton?.UnregisterCallback<ClickEvent>(OnMenuCloseClicked);
            _drawerScrim?.UnregisterCallback<ClickEvent>(OnMenuCloseClicked);
            _copyCodeButton?.UnregisterCallback<ClickEvent>(OnCopyCodeClicked);
            _copyClassroomCodeButton?.UnregisterCallback<ClickEvent>(OnCopyCodeClicked);

            _archiveButton?.UnregisterCallback<ClickEvent>(OnArchiveButtonClicked);
            _editDetailsButton?.UnregisterCallback<ClickEvent>(OnEditDetailsClicked);
            _editDialogCancelButton?.UnregisterCallback<ClickEvent>(OnEditDialogCancelClicked);
            _editDialogSaveButton?.UnregisterCallback<ClickEvent>(OnEditDialogSaveClicked);
            _archiveDialogCancelButton?.UnregisterCallback<ClickEvent>(OnArchiveDialogCancelClicked);
            _archiveDialogConfirmButton?.UnregisterCallback<ClickEvent>(OnArchiveDialogConfirmClicked);

            _unenrollDialogCancelButton?.UnregisterCallback<ClickEvent>(OnUnenrollDialogCancelClicked);
            _unenrollDialogConfirmButton?.UnregisterCallback<ClickEvent>(OnUnenrollDialogConfirmClicked);

            _overviewTabButton?.UnregisterCallback<ClickEvent>(OnOverviewTabClicked);
            _overviewSeeAnalyticsButton?.UnregisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _overviewSeeAnnouncementsButton?.UnregisterCallback<ClickEvent>(OnAnnouncementsTabClicked);
            _overviewSeeMaterialsButton?.UnregisterCallback<ClickEvent>(OnMaterialsTabClicked);
            _studentsTabButton?.UnregisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.UnregisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _analyticsTabButton?.UnregisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _announcementsTabButton?.UnregisterCallback<ClickEvent>(OnAnnouncementsTabClicked);
            _materialsTabButton?.UnregisterCallback<ClickEvent>(OnMaterialsTabClicked);

            _showToStudentsToggle?.UnregisterCallback<ClickEvent>(OnShowToStudentsToggleClicked);

            _postAnnouncementButton?.UnregisterCallback<ClickEvent>(OnPostAnnouncementClicked);

            _materialChooseFileButton?.UnregisterCallback<ClickEvent>(OnMaterialChooseFileClicked);
            _materialClearFileButton?.UnregisterCallback<ClickEvent>(OnMaterialClearFileClicked);
            _uploadMaterialButton?.UnregisterCallback<ClickEvent>(OnUploadMaterialClicked);

            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                //Debug.LogWarning("[AdminClassroomDetailController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");
            _classroomNameLabel = _screenRoot.Q<Label>("classroom-name-label");
            _classroomCodeLabel = _screenRoot.Q<Label>("classroom-code-label");
            _copyCodeButton = _screenRoot.Q<Button>("copy-code-button");

            _archiveButton = _screenRoot.Q<Button>("archive-button");
            _menuButton = _screenRoot.Q<Button>("menu-button");
            _drawerCloseButton = _screenRoot.Q<Button>("drawer-close-button");
            _drawerBackButton = _screenRoot.Q<Button>("drawer-back-button");
            _drawerScrim = _screenRoot.Q<VisualElement>("drawer-scrim");
            _drawerPanel = _screenRoot.Q<VisualElement>("drawer-panel");
            _drawerClassroomLabel = _screenRoot.Q<Label>("drawer-classroom-label");
            _sectionTitleLabel = _screenRoot.Q<Label>("section-title-label");
            _screenScroll = _screenRoot.Q<ScrollView>("screen-scroll");
            _archivedBadgeLabel = _screenRoot.Q<Label>("archived-badge-label");
            _editDetailsButton = _screenRoot.Q<Button>("edit-details-button");
            _editDialogOverlay = _screenRoot.Q<VisualElement>("edit-dialog-overlay");
            _editCodeField = _screenRoot.Q<TextField>("edit-code-field");
            _editNameField = _screenRoot.Q<TextField>("edit-name-field");
            _editSectionField = _screenRoot.Q<TextField>("edit-section-field");
            _editDialogErrorLabel = _screenRoot.Q<Label>("edit-dialog-error-label");
            _editDialogCancelButton = _screenRoot.Q<Button>("edit-dialog-cancel-button");
            _editDialogSaveButton = _screenRoot.Q<Button>("edit-dialog-save-button");
            _archiveDialogOverlay = _screenRoot.Q<VisualElement>("archive-dialog-overlay");
            _archiveDialogTitleLabel = _screenRoot.Q<Label>("archive-dialog-title-label");
            _archiveDialogMessageLabel = _screenRoot.Q<Label>("archive-dialog-message-label");
            _archiveDialogCancelButton = _screenRoot.Q<Button>("archive-dialog-cancel-button");
            _archiveDialogConfirmButton = _screenRoot.Q<Button>("archive-dialog-confirm-button");

            _studentsValueLabel = _screenRoot.Q<Label>("students-value-label");
            _avgScoreValueLabel = _screenRoot.Q<Label>("avg-score-value-label");
            _quizzesDoneValueLabel = _screenRoot.Q<Label>("quizzes-done-value-label");

            _overviewTabButton = _screenRoot.Q<Button>("overview-tab-button");
            _overviewPanel = _screenRoot.Q<VisualElement>("overview-panel");
            _overviewNameValue = _screenRoot.Q<Label>("overview-name-value");
            _overviewSectionValue = _screenRoot.Q<Label>("overview-section-value");
            _overviewCodeValue = _screenRoot.Q<Label>("overview-code-value");
            _overviewTeacherValue = _screenRoot.Q<Label>("overview-teacher-value");
            _overviewStatusPill = _screenRoot.Q<Label>("overview-status-pill");
            _overviewStudentsValue = _screenRoot.Q<Label>("overview-students-value");
            _overviewAvgValue = _screenRoot.Q<Label>("overview-avg-value");
            _overviewQuizzesValue = _screenRoot.Q<Label>("overview-quizzes-value");
            _overviewPointsValue = _screenRoot.Q<Label>("overview-points-value");
            _overviewTopEmpty = _screenRoot.Q<VisualElement>("overview-top-empty");
            _overviewTopList = _screenRoot.Q<VisualElement>("overview-top-list");
            _overviewAnnouncementEmpty = _screenRoot.Q<VisualElement>("overview-announcement-empty");
            _overviewAnnouncementHost = _screenRoot.Q<VisualElement>("overview-announcement-host");
            _overviewMaterialsEmpty = _screenRoot.Q<VisualElement>("overview-materials-empty");
            _overviewMaterialsHost = _screenRoot.Q<VisualElement>("overview-materials-host");
            _overviewSeeAnalyticsButton = _screenRoot.Q<Button>("overview-see-analytics-button");
            _overviewSeeAnnouncementsButton = _screenRoot.Q<Button>("overview-see-announcements-button");
            _overviewSeeMaterialsButton = _screenRoot.Q<Button>("overview-see-materials-button");

            _studentsTabButton = _screenRoot.Q<Button>("students-tab-button");
            _quizzesTabButton = _screenRoot.Q<Button>("quizzes-tab-button");
            _analyticsTabButton = _screenRoot.Q<Button>("analytics-tab-button");
            _announcementsTabButton = _screenRoot.Q<Button>("announcements-tab-button");
            _materialsTabButton = _screenRoot.Q<Button>("materials-tab-button");
            _studentsPanel = _screenRoot.Q<VisualElement>("students-panel");
            _quizzesPanel = _screenRoot.Q<VisualElement>("quizzes-panel");
            _analyticsPanel = _screenRoot.Q<VisualElement>("analytics-panel");
            _announcementsPanel = _screenRoot.Q<VisualElement>("announcements-panel");
            _materialsPanel = _screenRoot.Q<VisualElement>("materials-panel");

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

            _materialTitleField = _screenRoot.Q<TextField>("material-title-field");
            _materialDescriptionField = _screenRoot.Q<TextField>("material-description-field");
            _materialChooseFileButton = _screenRoot.Q<Button>("material-choose-file-button");
            _materialSelectedFilePanel = _screenRoot.Q<VisualElement>("material-selected-file-panel");
            _materialSelectedFileName = _screenRoot.Q<Label>("material-selected-file-name");
            _materialSelectedFileSize = _screenRoot.Q<Label>("material-selected-file-size");
            _materialClearFileButton = _screenRoot.Q<Button>("material-clear-file-button");
            _materialStatusLabel = _screenRoot.Q<Label>("material-status-label");
            _uploadMaterialButton = _screenRoot.Q<Button>("upload-material-button");
            _materialsEmptyState = _screenRoot.Q<VisualElement>("materials-empty-state");
            _materialsList = _screenRoot.Q<VisualElement>("materials-list");

            //Debug.Log($"[AdminClassroomDetailController] Found tabs: {_studentsTabButton != null}/{_quizzesTabButton != null}/{_analyticsTabButton != null}/{_announcementsTabButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _drawerBackButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _menuButton?.RegisterCallback<ClickEvent>(OnMenuClicked);
            _drawerCloseButton?.RegisterCallback<ClickEvent>(OnMenuCloseClicked);
            _drawerScrim?.RegisterCallback<ClickEvent>(OnMenuCloseClicked);
            _copyCodeButton?.RegisterCallback<ClickEvent>(OnCopyCodeClicked);
            _copyClassroomCodeButton?.RegisterCallback<ClickEvent>(OnCopyCodeClicked);

            _archiveButton?.RegisterCallback<ClickEvent>(OnArchiveButtonClicked);
            _editDetailsButton?.RegisterCallback<ClickEvent>(OnEditDetailsClicked);
            _editDialogCancelButton?.RegisterCallback<ClickEvent>(OnEditDialogCancelClicked);
            _editDialogSaveButton?.RegisterCallback<ClickEvent>(OnEditDialogSaveClicked);
            _archiveDialogCancelButton?.RegisterCallback<ClickEvent>(OnArchiveDialogCancelClicked);
            _archiveDialogConfirmButton?.RegisterCallback<ClickEvent>(OnArchiveDialogConfirmClicked);

            _unenrollDialogCancelButton?.RegisterCallback<ClickEvent>(OnUnenrollDialogCancelClicked);
            _unenrollDialogConfirmButton?.RegisterCallback<ClickEvent>(OnUnenrollDialogConfirmClicked);

            _overviewTabButton?.RegisterCallback<ClickEvent>(OnOverviewTabClicked);
            _overviewSeeAnalyticsButton?.RegisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _overviewSeeAnnouncementsButton?.RegisterCallback<ClickEvent>(OnAnnouncementsTabClicked);
            _overviewSeeMaterialsButton?.RegisterCallback<ClickEvent>(OnMaterialsTabClicked);
            _studentsTabButton?.RegisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.RegisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _analyticsTabButton?.RegisterCallback<ClickEvent>(OnAnalyticsTabClicked);
            _announcementsTabButton?.RegisterCallback<ClickEvent>(OnAnnouncementsTabClicked);
            _materialsTabButton?.RegisterCallback<ClickEvent>(OnMaterialsTabClicked);

            _showToStudentsToggle?.RegisterCallback<ClickEvent>(OnShowToStudentsToggleClicked);

            _postAnnouncementButton?.RegisterCallback<ClickEvent>(OnPostAnnouncementClicked);

            _materialChooseFileButton?.RegisterCallback<ClickEvent>(OnMaterialChooseFileClicked);
            _materialClearFileButton?.RegisterCallback<ClickEvent>(OnMaterialClearFileClicked);
            _uploadMaterialButton?.RegisterCallback<ClickEvent>(OnUploadMaterialClicked);

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
            if (_drawerClassroomLabel != null) _drawerClassroomLabel.text = _classroomName;
            if (_classroomCodeLabel != null) _classroomCodeLabel.text = _classroomCode;
            if (_studentsValueLabel != null) _studentsValueLabel.text = studentCount.ToString("N0");
            if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(avgScorePercent)}%";
            if (_quizzesDoneValueLabel != null) _quizzesDoneValueLabel.text = quizzesDone.ToString("N0");

            // Reset from whatever classroom was previously loaded into this reused screen -
            // LoadClassroomContent()'s FetchClassroomDetail callback will set the real value
            // once it resolves.
            SetArchived(false);
            _classroomSection = "";
            _teacherName = "";

            // Same reused-screen rule for the Materials tab: don't carry the previous
            // classroom's list or half-filled upload form into this one.
            _materials.Clear();
            ResetMaterialForm();
            RefreshMaterialsUI();
            RefreshOverview();

            LoadClassroomContent();
        }
        // ---------------- App resume ----------------

        /// <summary>App came back from the background (NetworkStatusMonitor.OnAppResumed, which
        /// only fires while online). This screen loads its data with one-shot fetches, so nothing
        /// would update by itself: re-run the load so the student list, leaderboard, analytics,
        /// announcements and quizzes are current. It is silent - no spinner, nothing cleared.</summary>
        private void HandleAppResumed(float secondsAway)
        {
            if (_screenRoot == null || string.IsNullOrEmpty(_classroomId)) return;

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
                //Debug.LogWarning("[AdminClassroomDetailController] LoadClassroomContent called with no classroom id set.");
                return;
            }

            if (AdminClassroomService.Instance == null)
            {
                //Debug.LogWarning("[AdminClassroomDetailController] AdminClassroomService not available yet.");
                return;
            }

            AdminClassroomService.Instance.FetchClassroomDetail(_classroomId, record =>
            {
                if (record == null) return;
                _publishedQuizIds = record.PublishedQuizIds ?? new List<string>();
                _classroomSection = record.Section ?? "";
                _teacherName = record.TeacherName ?? "";
                SetLeaderboardVisibility(record.LeaderboardVisible);
                SetArchived(record.IsArchived);
                LoadQuizzesTab();
                RefreshOverview();
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

            string materialsClassroomId = _classroomId;
            AdminClassroomService.Instance.FetchMaterials(materialsClassroomId, materials =>
            {
                if (materialsClassroomId != _classroomId) return;
                _materials.Clear();
                _materials.AddRange(materials);
                RefreshMaterialsUI();
            });

            AdminClassroomService.Instance.FetchClassroomAnalytics(_classroomId, analytics =>
            {
                SetAnalyticsOverview(analytics.TotalPointsEarned, analytics.TotalQuizzesCompleted, analytics.AvgScorePercent, analytics.ActiveStudents);
                SetLeaderboard(analytics.Leaderboard.ConvertAll(s => (s.StudentId, s.Name, s.Points, s.QuizzesCompleted)));
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

                RefreshOverview();
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
                //Debug.LogWarning("[AdminClassroomDetailController] QuizService not available yet.");
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
                    bool canRetake = !record.IsFileSubmission && !record.IsRetake && record.MaxAttempts > 0;
                    _quizzesList.Add(BuildQuizRow(record.QuizId, record.Title, record.Category, published, i == records.Count - 1, canRetake));
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
        public void SetLeaderboard(List<(string studentId, string name, int points, int quizzesCompleted)> rankedStudents)
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
                var (studentId, name, points, _) = _lastTopPerformers[i];
                _topPerformersList.Add(BuildPerformerRow(i + 1, studentId, name, points));
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
            ApplyQuizCardState(row);
        }

        /// <summary>Syncs the quiz card's accent strip, status pill and footer text with its published flag.</summary>
        private static void ApplyQuizCardState(QuizRow row)
        {
            if (row == null) return;
            row.Card?.EnableInClassList("quiz-card-off", !row.Published);
            if (row.StatusPill != null)
            {
                row.StatusPill.text = row.Published ? "Published" : "Not published";
                row.StatusPill.EnableInClassList("quiz-status-pill-off", !row.Published);
            }
            if (row.FooterLabel != null)
                row.FooterLabel.text = row.Published ? "Visible to students" : "Hidden from students";
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            //Debug.Log("[AdminClassroomDetailController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        private void OnCopyCodeClicked(ClickEvent evt)
        {
            if (_classroomCodeLabel == null) return;
            GUIUtility.systemCopyBuffer = _classroomCodeLabel.text;
            //Debug.Log($"[AdminClassroomDetailController] Copied classroom code: {_classroomCodeLabel.text}");
        }

        private void OnOverviewTabClicked(ClickEvent evt) => ShowOverviewTab();
        private void OnStudentsTabClicked(ClickEvent evt) => ShowStudentsTab();
        private void OnQuizzesTabClicked(ClickEvent evt) => ShowQuizzesTab();
        private void OnAnalyticsTabClicked(ClickEvent evt) => ShowAnalyticsTab();
        private void OnAnnouncementsTabClicked(ClickEvent evt) => ShowAnnouncementsTab();
        private void OnMaterialsTabClicked(ClickEvent evt) => ShowMaterialsTab();

        private void ShowOverviewTab()
        {
            RefreshOverview();
            SetActiveTab(_overviewTabButton, _overviewPanel, "Overview");
        }

        private void ShowStudentsTab()
        {
            SetActiveTab(_studentsTabButton, _studentsPanel, "Students");
        }

        private void ShowQuizzesTab()
        {
            SetActiveTab(_quizzesTabButton, _quizzesPanel, "Quizzes");
        }

        private void ShowAnalyticsTab()
        {
            SetActiveTab(_analyticsTabButton, _analyticsPanel, "Analytics");
        }

        private void ShowAnnouncementsTab()
        {
            SetActiveTab(_announcementsTabButton, _announcementsPanel, "Announcements");
        }

        private void ShowMaterialsTab()
        {
            SetActiveTab(_materialsTabButton, _materialsPanel, "Materials");
        }

        private void SetActiveTab(Button activeButton, VisualElement activePanel, string tabName)
        {
            _overviewTabButton?.RemoveFromClassList("tab-button-active");
            _studentsTabButton?.RemoveFromClassList("tab-button-active");
            _quizzesTabButton?.RemoveFromClassList("tab-button-active");
            _analyticsTabButton?.RemoveFromClassList("tab-button-active");
            _announcementsTabButton?.RemoveFromClassList("tab-button-active");
            _materialsTabButton?.RemoveFromClassList("tab-button-active");
            activeButton?.AddToClassList("tab-button-active");

            _overviewPanel?.AddToClassList("hidden");
            _studentsPanel?.AddToClassList("hidden");
            _quizzesPanel?.AddToClassList("hidden");
            _analyticsPanel?.AddToClassList("hidden");
            _announcementsPanel?.AddToClassList("hidden");
            _materialsPanel?.AddToClassList("hidden");
            activePanel?.RemoveFromClassList("hidden");

            if (_sectionTitleLabel != null) _sectionTitleLabel.text = tabName;
            SetDrawerOpen(false);
            _screenScroll?.schedule.Execute(() =>
            {
                if (_screenScroll != null) _screenScroll.scrollOffset = Vector2.zero;
            }).ExecuteLater(0);
        }

        // ---------------- Hamburger drawer ----------------

        private void OnMenuClicked(ClickEvent evt) => SetDrawerOpen(true);
        private void OnMenuCloseClicked(ClickEvent evt) => SetDrawerOpen(false);

        private void SetDrawerOpen(bool open)
        {
            if (_drawerScrim == null || _drawerPanel == null) return;

            if (open)
            {
                // Show first, then flip the "on" classes one frame later so the
                // fade / slide transitions actually run.
                _drawerScrim.RemoveFromClassList("hidden");
                _drawerScrim.schedule.Execute(() =>
                {
                    _drawerScrim.AddToClassList("drawer-scrim-on");
                    _drawerPanel.RemoveFromClassList("drawer-closed");
                }).ExecuteLater(10);
            }
            else
            {
                _drawerScrim.RemoveFromClassList("drawer-scrim-on");
                _drawerPanel.AddToClassList("drawer-closed");
                // Remove the scrim after the fade so it stops swallowing taps.
                _drawerScrim.schedule.Execute(() =>
                {
                    if (_drawerPanel.ClassListContains("drawer-closed")) _drawerScrim.AddToClassList("hidden");
                }).ExecuteLater(260);
            }
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

                    //Debug.Log($"[AdminClassroomDetailController] Quiz {row.QuizId} published: {newState}");
                    return;
                }

                //Debug.LogWarning($"[AdminClassroomDetailController] Could not update quiz {row.QuizId} publish state: {error}");
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
                    //Debug.Log($"[AdminClassroomDetailController] Leaderboard visible to students: {newState}");
                    return;
                }

                //Debug.LogWarning($"[AdminClassroomDetailController] Could not update leaderboard visibility: {error}");
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
            if (_archiveButton != null) _archiveButton.text = archived ? "Unlock Classroom" : "Lock Classroom";
            RefreshOverview();
        }

        private void OnArchiveButtonClicked(ClickEvent evt)
        {
            SetDrawerOpen(false);
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
                    //Debug.LogWarning($"[AdminClassroomDetailController] Could not update archived state: {error}");
                    return;
                }

                SetArchived(newState);
                //Debug.Log($"[AdminClassroomDetailController] Classroom {_classroomId} archived: {newState}");
            });
        }

        // ---------------- Edit classroom details ----------------

        /// <summary>"Edit Details" in the drawer. Loads the classroom fresh (so the dialog
        /// is pre-filled with the raw code / name / section, not the combined "Name - Section"
        /// header text) and opens the dialog.</summary>
        private void OnEditDetailsClicked(ClickEvent evt)
        {
            SetDrawerOpen(false);
            if (string.IsNullOrEmpty(_classroomId) || _editDialogOverlay == null || AdminClassroomService.Instance == null) return;

            _editDetailsButton?.SetEnabled(false);
            AdminClassroomService.Instance.FetchClassroomDetail(_classroomId, record =>
            {
                _editDetailsButton?.SetEnabled(true);
                if (record == null) return;

                _editCodeField?.SetValueWithoutNotify(record.Name ?? "");
                _editNameField?.SetValueWithoutNotify(record.Description ?? "");
                _editSectionField?.SetValueWithoutNotify(record.Section ?? "");
                SetEditDialogError(null);
                _editDialogSaveButton?.SetEnabled(true);
                _editDialogOverlay.RemoveFromClassList("hidden");
            });
        }

        private void OnEditDialogCancelClicked(ClickEvent evt)
        {
            _editDialogOverlay?.AddToClassList("hidden");
        }

        private void OnEditDialogSaveClicked(ClickEvent evt)
        {
            if (string.IsNullOrEmpty(_classroomId) || AdminClassroomService.Instance == null) return;

            string code = _editCodeField?.value?.Trim();
            string name = _editNameField?.value?.Trim();
            string section = _editSectionField?.value?.Trim() ?? "";

            if (string.IsNullOrEmpty(code)) { SetEditDialogError("Please enter a classroom code."); return; }
            if (string.IsNullOrEmpty(name)) { SetEditDialogError("Please enter a classroom name."); return; }
            if (string.IsNullOrEmpty(section)) { SetEditDialogError("Please enter a section."); return; }

            SetEditDialogError(null);
            _editDialogSaveButton?.SetEnabled(false);

            AdminClassroomService.Instance.UpdateClassroomDetails(_classroomId, code, name, section, (success, error, record) =>
            {
                _editDialogSaveButton?.SetEnabled(true);

                if (!success)
                {
                    SetEditDialogError(error);
                    return;
                }

                // Repaint everything on this screen that shows the identity. (The dashboard's
                // classroom list and the student screens are fed by live listeners, so they
                // update on their own.)
                _classroomName = record.DisplayName;
                _classroomSection = record.Section ?? "";
                if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
                if (_drawerClassroomLabel != null) _drawerClassroomLabel.text = _classroomName;
                RefreshOverview();

                _editDialogOverlay?.AddToClassList("hidden");
            });
        }

        private void SetEditDialogError(string message)
        {
            if (_editDialogErrorLabel == null) return;
            _editDialogErrorLabel.text = message ?? "";
            _editDialogErrorLabel.EnableInClassList("hidden", string.IsNullOrEmpty(message));
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
                    //Debug.LogWarning($"[AdminClassroomDetailController] Could not remove student {studentId}: {error}");
                    return;
                }

                //Debug.Log($"[AdminClassroomDetailController] Removed student \"{studentName}\" ({studentId}) from classroom {_classroomId}");

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
            if (_announcementsList == null) { RefreshOverview(); return; }

            _announcementsList.Clear();

            RefreshOverview();

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
                //Debug.LogWarning("[AdminClassroomDetailController] Post Announcement tapped with no classroom loaded - ignoring.");
                return;
            }

            string title = _announcementTitleField != null ? (_announcementTitleField.value ?? "").Trim() : "";
            string body = _announcementBodyField != null ? (_announcementBodyField.value ?? "").Trim() : "";

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body))
            {
                //Debug.Log("[AdminClassroomDetailController] Post Announcement tapped with an empty title and message - ignoring.");
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
                    //Debug.LogWarning($"[AdminClassroomDetailController] Could not post announcement: {error}");
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

                //Debug.Log($"[AdminClassroomDetailController] Posted announcement \"{record.Title}\" to classroom {_classroomId}");
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
                    //Debug.LogWarning($"[AdminClassroomDetailController] Could not delete announcement: {error}");
                    return;
                }

                _liveAnnouncements.Remove(announcement);
                RefreshAnnouncementsUI();

                //Debug.Log($"[AdminClassroomDetailController] Deleted announcement \"{announcement.Info.Title}\" from classroom {_classroomId}");
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

        // ---------------- Materials (modules / lessons) ----------------

        private void OnMaterialChooseFileClicked(ClickEvent evt)
        {
            if (_isUploadingMaterial || NativeFilePicker.IsFilePickerBusy()) return;

            SetMaterialStatus(null, false);

            var allowedTypes = MaterialConfig.AllowedExtensions
                .Select(e => NativeFilePicker.ConvertExtensionToFileType(e))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToArray();

            NativeFilePicker.PickFile(path =>
            {
                if (string.IsNullOrEmpty(path)) return; // teacher cancelled

                string fileName = System.IO.Path.GetFileName(path);
                long size;

                try
                {
                    size = new System.IO.FileInfo(path).Length;
                }
                catch (System.Exception)
                {
                    //Debug.LogWarning($"[AdminClassroomDetailController] Could not read '{path}': {e.Message}");
                    SetMaterialStatus("Could not read that file. Please choose a different one.", true);
                    return;
                }

                string problem = MaterialConfig.Validate(fileName, size);
                if (problem != null)
                {
                    ClearPendingMaterialFile();
                    SetMaterialStatus(problem, true);
                    return;
                }

                _pendingMaterialPath = path;
                _pendingMaterialName = fileName;
                _pendingMaterialSize = size;

                if (_materialSelectedFileName != null) _materialSelectedFileName.text = fileName;
                if (_materialSelectedFileSize != null) _materialSelectedFileSize.text = FileSubmissionConfig.FormatSize(size);
                _materialSelectedFilePanel?.RemoveFromClassList("hidden");

                SetMaterialStatus(null, false);
            }, allowedTypes.Length > 0 ? allowedTypes : null);
        }

        private void OnMaterialClearFileClicked(ClickEvent evt)
        {
            if (_isUploadingMaterial) return;
            ClearPendingMaterialFile();
            SetMaterialStatus(null, false);
        }

        private void ClearPendingMaterialFile()
        {
            _pendingMaterialPath = null;
            _pendingMaterialName = null;
            _pendingMaterialSize = 0;
            _materialSelectedFilePanel?.AddToClassList("hidden");
            if (_materialSelectedFileName != null) _materialSelectedFileName.text = string.Empty;
            if (_materialSelectedFileSize != null) _materialSelectedFileSize.text = string.Empty;
        }

        private void ResetMaterialForm()
        {
            ClearPendingMaterialFile();
            if (_materialTitleField != null) _materialTitleField.value = string.Empty;
            if (_materialDescriptionField != null) _materialDescriptionField.value = string.Empty;
            _armedDeleteMaterialId = null;
            SetUploadingMaterial(false);
            SetMaterialStatus(null, false);
        }

        private void SetUploadingMaterial(bool uploading)
        {
            _isUploadingMaterial = uploading;
            _uploadMaterialButton?.SetEnabled(!uploading);
            _materialChooseFileButton?.SetEnabled(!uploading);
            _materialClearFileButton?.SetEnabled(!uploading);
            if (_uploadMaterialButton != null) _uploadMaterialButton.text = uploading ? "Uploading..." : "Upload Material";
        }

        private void SetMaterialStatus(string message, bool isError)
        {
            if (_materialStatusLabel == null) return;

            bool has = !string.IsNullOrEmpty(message);
            _materialStatusLabel.text = has ? message : string.Empty;
            _materialStatusLabel.EnableInClassList("hidden", !has);
            _materialStatusLabel.EnableInClassList("material-status-error", has && isError);
            _materialStatusLabel.EnableInClassList("material-status-success", has && !isError);
        }

        private void OnUploadMaterialClicked(ClickEvent evt)
        {
            if (_isUploadingMaterial) return;

            if (string.IsNullOrEmpty(_classroomId) || AdminClassroomService.Instance == null || R2FileUploadService.Instance == null)
            {
                SetMaterialStatus("Materials are not available right now.", true);
                return;
            }

            if (string.IsNullOrEmpty(_pendingMaterialPath))
            {
                SetMaterialStatus("Choose a file to upload first.", true);
                return;
            }

            if (!NetworkStatusMonitor.IsOnline)
            {
                SetMaterialStatus(R2FileUploadService.MaterialOfflineMessage, true);
                return;
            }

            byte[] bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(_pendingMaterialPath);
            }
            catch (System.Exception)
            {
                //Debug.LogWarning($"[AdminClassroomDetailController] Could not read the picked file: {e.Message}");
                SetMaterialStatus("Could not read that file. Please choose it again.", true);
                return;
            }

            string problem = MaterialConfig.Validate(_pendingMaterialName, bytes.LongLength);
            if (problem != null)
            {
                SetMaterialStatus(problem, true);
                return;
            }

            string classroomId = _classroomId;
            string fileName = _pendingMaterialName;
            string title = (_materialTitleField?.value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(title)) title = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string description = (_materialDescriptionField?.value ?? string.Empty).Trim();
            string mimeType = FileOpener.GetMimeType(fileName);
            string materialId = AdminClassroomService.Instance.ReserveMaterialId(classroomId);

            SetUploadingMaterial(true);
            SetMaterialStatus("Uploading file...", false);

            R2FileUploadService.Instance.UploadMaterial(classroomId, materialId, fileName, mimeType, bytes, (ok, error, result) =>
            {
                if (!ok)
                {
                    SetUploadingMaterial(false);
                    SetMaterialStatus(error ?? "Could not upload that file.", true);
                    return;
                }

                var material = new ClassroomMaterial
                {
                    MaterialId = materialId,
                    Title = title,
                    Description = description,
                    FileName = fileName,
                    FileSize = result.FileSize,
                    MimeType = string.IsNullOrEmpty(result.MimeType) ? mimeType : result.MimeType,
                    StorageKey = result.StorageKey
                };

                AdminClassroomService.Instance.SaveMaterial(classroomId, material, (saved, saveError, record) =>
                {
                    SetUploadingMaterial(false);

                    if (!saved)
                    {
                        // The file made it into R2 but has no doc, so nobody could ever open
                        // it. Remove it instead of leaving an orphan behind.
                        R2FileUploadService.Instance.DeleteMaterialFile(result.StorageKey, null);
                        SetMaterialStatus(saveError ?? "Could not save the material. Please try again.", true);
                        return;
                    }

                    // The teacher may have moved to another classroom while this uploaded.
                    if (classroomId != _classroomId) return;

                    _materials.Insert(0, record);
                    ResetMaterialForm();
                    RefreshMaterialsUI();
                    SetMaterialStatus("Uploaded. Students can open it from their Materials tab.", false);

                    //Debug.Log($"[AdminClassroomDetailController] Uploaded material \"{record.Title}\" to classroom {classroomId}");
                });
            });
        }

        private void RefreshMaterialsUI()
        {
            if (_materialsList == null) { RefreshOverview(); return; }

            _materialsList.Clear();

            RefreshOverview();

            bool hasData = _materials.Count > 0;
            _materialsEmptyState?.EnableInClassList("hidden", hasData);
            _materialsList.EnableInClassList("hidden", !hasData);

            if (!hasData) return;

            foreach (var material in _materials) _materialsList.Add(BuildMaterialCard(material));
        }

        private VisualElement BuildMaterialCard(ClassroomMaterial material)
        {
            var card = new VisualElement();
            card.AddToClassList("material-card");

            // Header: file-type badge | title + meta | Open pill (same layout as the student Materials tab).
            var headerRow = new VisualElement();
            headerRow.AddToClassList("material-card-header-row");

            string ext = FileSubmissionConfig.ExtensionOf(material.FileName);
            var badge = new Label(string.IsNullOrEmpty(ext) ? "FILE" : ext.ToUpperInvariant());
            badge.AddToClassList("material-type-badge");
            headerRow.Add(badge);

            var textColumn = new VisualElement();
            textColumn.AddToClassList("material-card-text");

            var titleLabel = new Label(string.IsNullOrEmpty(material.Title) ? material.FileName : material.Title);
            titleLabel.AddToClassList("material-title-label");
            textColumn.Add(titleLabel);

            var metaLabel = new Label($"{FileSubmissionConfig.FormatSize(material.FileSize)}  \u2022  {material.CreatedAt.ToDateTime().ToLocalTime():MMM d, yyyy}");
            metaLabel.AddToClassList("material-meta-label");
            textColumn.Add(metaLabel);
            headerRow.Add(textColumn);

            var actions = new VisualElement();
            actions.AddToClassList("material-card-actions");

            var openButton = new Button { text = "Open" };
            openButton.AddToClassList("material-open-button");
            openButton.clicked += () => MaterialFileOpener.Open(material,
                busy => { openButton.SetEnabled(!busy); openButton.text = busy ? "Opening..." : "Open"; },
                error => SetMaterialStatus(error, true));
            actions.Add(openButton);
            headerRow.Add(actions);
            card.Add(headerRow);

            if (!string.IsNullOrEmpty(material.Description))
            {
                var descriptionLabel = new Label(material.Description);
                descriptionLabel.AddToClassList("material-description-label");
                card.Add(descriptionLabel);
            }

            var deleteButton = new Button { text = "Delete" };
            deleteButton.AddToClassList("announcement-delete-button");
            deleteButton.AddToClassList("material-card-delete");
            deleteButton.clicked += () => OnDeleteMaterialClicked(material, deleteButton);
            card.Add(deleteButton);

            return card;
        }

        private void OnDeleteMaterialClicked(ClassroomMaterial material, Button deleteButton)
        {
            if (string.IsNullOrEmpty(_classroomId) || AdminClassroomService.Instance == null) return;

            // First tap arms it for a few seconds; the second tap inside that window deletes.
            if (_armedDeleteMaterialId != material.MaterialId)
            {
                _armedDeleteMaterialId = material.MaterialId;
                deleteButton.text = "Tap again to delete";
                deleteButton.schedule.Execute(() =>
                {
                    if (_armedDeleteMaterialId != material.MaterialId) return;
                    _armedDeleteMaterialId = null;
                    deleteButton.text = "Delete";
                }).StartingIn(3500);
                return;
            }

            _armedDeleteMaterialId = null;
            deleteButton.SetEnabled(false);

            string classroomId = _classroomId;
            AdminClassroomService.Instance.DeleteMaterial(classroomId, material, (success, error) =>
            {
                if (!success)
                {
                    deleteButton.SetEnabled(true);
                    deleteButton.text = "Delete";
                    SetMaterialStatus(error ?? "Could not delete the material.", true);
                    return;
                }

                if (classroomId != _classroomId) return;

                _materials.Remove(material);
                RefreshMaterialsUI();
                //Debug.Log($"[AdminClassroomDetailController] Deleted material \"{material.Title}\" from classroom {classroomId}");
            });
        }

        // ---------------- Overview tab ----------------

        /// <summary>Repaints the Overview tab from state this screen already holds: the cached
        /// identity, the header stat labels (kept live by LoadClassroomContent()), the
        /// leaderboard, announcements and materials lists. Safe to call at any time and
        /// before the elements exist - it just returns.</summary>
        private void RefreshOverview()
        {
            if (_overviewPanel == null) return;

            string Or(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

            if (_overviewNameValue != null) _overviewNameValue.text = Or(_classroomName);
            if (_overviewSectionValue != null) _overviewSectionValue.text = Or(_classroomSection);
            if (_overviewCodeValue != null) _overviewCodeValue.text = Or(_classroomCodeLabel != null ? _classroomCodeLabel.text : _classroomCode);
            if (_overviewTeacherValue != null) _overviewTeacherValue.text = Or(_teacherName);

            if (_overviewStatusPill != null)
            {
                _overviewStatusPill.text = IsArchived ? "Locked" : "Active";
                _overviewStatusPill.EnableInClassList("overview-status-pill-locked", IsArchived);
            }

            if (_overviewStudentsValue != null && _studentsValueLabel != null) _overviewStudentsValue.text = _studentsValueLabel.text;
            if (_overviewAvgValue != null && _avgScoreValueLabel != null) _overviewAvgValue.text = _avgScoreValueLabel.text;
            if (_overviewPointsValue != null && _totalPointsValueLabel != null) _overviewPointsValue.text = _totalPointsValueLabel.text;
            if (_overviewQuizzesValue != null) _overviewQuizzesValue.text = (_publishedQuizIds?.Count ?? 0).ToString("N0");

            RefreshOverviewTopStudents();
            RefreshOverviewLatestAnnouncement();
            RefreshOverviewRecentMaterials();
        }

        private void RefreshOverviewTopStudents()
        {
            if (_overviewTopList == null) return;
            _overviewTopList.Clear();

            int count = Mathf.Min(OverviewTopStudentCount, _lastTopPerformers.Count);
            _overviewTopEmpty?.EnableInClassList("hidden", count > 0);
            _overviewTopList.EnableInClassList("hidden", count == 0);

            for (int i = 0; i < count; i++)
            {
                var (studentId, name, points, _) = _lastTopPerformers[i];
                _overviewTopList.Add(BuildPerformerRow(i + 1, studentId, name, points));
            }
        }

        private void RefreshOverviewLatestAnnouncement()
        {
            if (_overviewAnnouncementHost == null) return;
            _overviewAnnouncementHost.Clear();

            bool hasData = _liveAnnouncements.Count > 0;
            _overviewAnnouncementEmpty?.EnableInClassList("hidden", hasData);
            _overviewAnnouncementHost.EnableInClassList("hidden", !hasData);
            if (!hasData) return;

            // _liveAnnouncements is most-recent-first. Read-only card: no Delete button here,
            // that stays on the Announcements tab.
            var info = _liveAnnouncements[0].Info;

            var card = new VisualElement();
            card.AddToClassList("announcement-card");

            var headerRow = new VisualElement();
            headerRow.AddToClassList("announcement-header-row");

            var titleLabel = new Label(info.Title);
            titleLabel.AddToClassList("announcement-title-label");
            var dateLabel = new Label(info.DateText);
            dateLabel.AddToClassList("announcement-date-label");
            headerRow.Add(titleLabel);
            headerRow.Add(dateLabel);
            card.Add(headerRow);

            if (!string.IsNullOrEmpty(info.Body))
            {
                var bodyLabel = new Label(info.Body);
                bodyLabel.AddToClassList("announcement-body-label");
                bodyLabel.style.marginBottom = 0;
                card.Add(bodyLabel);
            }

            _overviewAnnouncementHost.Add(card);
        }

        private void RefreshOverviewRecentMaterials()
        {
            if (_overviewMaterialsHost == null) return;
            _overviewMaterialsHost.Clear();

            bool hasData = _materials.Count > 0;
            _overviewMaterialsEmpty?.EnableInClassList("hidden", hasData);
            _overviewMaterialsHost.EnableInClassList("hidden", !hasData);
            if (!hasData) return;

            // Sorted locally so this doesn't depend on the order FetchMaterials() returns.
            var recent = _materials.OrderByDescending(m => m.CreatedAt.ToDateTime()).Take(OverviewMaterialCount).ToList();
            for (int i = 0; i < recent.Count; i++)
            {
                var material = recent[i];

                var row = new VisualElement();
                row.AddToClassList("overview-material-row");
                if (i == recent.Count - 1) row.AddToClassList("overview-material-row-last");

                var text = new VisualElement();
                text.AddToClassList("overview-material-text");

                string title = string.IsNullOrWhiteSpace(material.Title) ? material.FileName : material.Title;
                var titleLabel = new Label(title);
                titleLabel.AddToClassList("overview-material-title");

                var metaLabel = new Label($"{FileSubmissionConfig.FormatSize(material.FileSize)}  \u2022  {material.CreatedAt.ToDateTime().ToLocalTime():MMM d, yyyy}");
                metaLabel.AddToClassList("overview-material-meta");

                text.Add(titleLabel);
                text.Add(metaLabel);
                row.Add(text);
                _overviewMaterialsHost.Add(row);
            }
        }

        // ---------------- Row builders (built at runtime - lists are dynamic) ----------------

        private VisualElement BuildPerformerRow(int rank, string studentId, string name, int points)
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

            var avatar = new VisualElement();
            avatar.AddToClassList("performer-avatar");
            var initialsLabel = new Label(GetInitials(name));
            initialsLabel.AddToClassList("performer-avatar-label");
            avatar.Add(initialsLabel);

            // Profile picture (students/{uid}.avatarUrl); the initials stay as the fallback.
            StudentAvatarLoader.Apply(avatar, initialsLabel, studentId);

            var nameLabel = new Label(name);
            nameLabel.AddToClassList("performer-name-label");

            var pointsLabel = new Label($"{points:N0} pts");
            pointsLabel.AddToClassList("performer-points-label");

            row.Add(badge);
            row.Add(avatar);
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

            // Profile picture (students/{uid}.avatarUrl); the initials stay as the fallback
            // for students without a photo or if the download fails.
            StudentAvatarLoader.Apply(avatar, initialsLabel, studentId);

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

        private VisualElement BuildQuizRow(string quizId, string title, string category, bool published, bool isLast, bool canRetake)
        {
            var card = new VisualElement();
            card.AddToClassList("quiz-card");

            // Header: subject tag on the left, published/not-published pill on the right.
            var headerRow = new VisualElement();
            headerRow.AddToClassList("quiz-card-header-row");

            var subjectLabel = new Label(string.IsNullOrEmpty(category) ? "QUIZ" : category.ToUpperInvariant());
            subjectLabel.AddToClassList("quiz-subject-tag");

            var statusPill = new Label();
            statusPill.AddToClassList("quiz-status-pill");

            headerRow.Add(subjectLabel);
            headerRow.Add(statusPill);
            card.Add(headerRow);

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("quiz-card-title");
            card.Add(titleLabel);

            // Footer: visibility hint on the left, Retake + publish toggle on the right.
            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("quiz-card-bottom-row");

            var footerLabel = new Label();
            footerLabel.AddToClassList("quiz-card-bottom-text");
            bottomRow.Add(footerLabel);

            var actions = new VisualElement();
            actions.AddToClassList("quiz-card-actions");

            var quizRow = new QuizRow { QuizId = quizId, Published = published, Card = card, StatusPill = statusPill, FooterLabel = footerLabel };

            // Retake button: opens the "Create Retake Exam" dialog for this quiz. Not shown for
            // file-submission quizzes or for retake copies (they can't be retaken again).
            if (canRetake)
            {
                var retakeButton = new Button(() => OnRetakeClicked(quizId, title)) { text = "Retake" };
                retakeButton.AddToClassList("quiz-retake-button");
                actions.Add(retakeButton);
            }

            var toggle = new Button(() => OnQuizToggleClicked(quizRow));
            toggle.AddToClassList("toggle-switch");
            toggle.EnableInClassList("toggle-on", published);

            var knob = new VisualElement();
            knob.AddToClassList("toggle-knob");
            toggle.Add(knob);

            quizRow.ToggleButton = toggle;
            _quizRows.Add(quizRow);
            actions.Add(toggle);

            bottomRow.Add(actions);
            card.Add(bottomRow);

            ApplyQuizCardState(quizRow);
            return card;
        }

        private void OnRetakeClicked(string quizId, string quizTitle)
        {
            if (string.IsNullOrEmpty(_classroomId) || string.IsNullOrEmpty(quizId)) return;
            AdminRetakeExamModal.Show(_screenRoot ?? _root, _classroomId, quizId, quizTitle, LoadQuizzesTab);
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
            if (!AnatomiaTheme.UseGradientChrome) return; // minimalist theme: flat chrome, see Theme/AnatomiaTheme.cs
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
