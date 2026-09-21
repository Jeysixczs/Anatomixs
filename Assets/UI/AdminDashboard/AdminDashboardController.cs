using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Anatomia3D.Backend;
using Firebase.Firestore;
using UnityEngine;
using UnityEngine.Networking;
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
    ///  - Exposes SetHeaderData() / SetDashboardStats() / SetClassrooms() so
    ///    admin/session code can push real values in instead of placeholder
    ///    mock data. Recent Activity pushes itself: quiz completions stream in
    ///    live via QuizService.ListenToRecentActivityForAdmin (instant update
    ///    the moment a student submits, no manual refresh needed) merged with
    ///    a one-shot classroom-joins fetch, both re-triggered whenever the
    ///    classroom list changes - see RefreshRecentActivitySources().
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

        /// <summary>What kind of row BuildActivityRow() should render - a rich quiz-completion
        /// card (student/quiz/classroom/score/status), or a simple one-line join row.</summary>
        private enum ActivityKind { QuizCompleted, ClassroomJoin }

        /// <summary>Plain data for a single row in the "Recent Activity" list. Unifies the two
        /// sources merged into that list (live quiz completions + one-shot classroom joins)
        /// behind one shape with a stable Id, so RefreshRecentActivityUI() can diff against
        /// what's already on screen instead of tearing the whole list down on every update.</summary>
        private readonly struct ActivityCardData
        {
            /// <summary>Stable across re-fetches/re-subscribes for the same underlying event -
            /// the quizAttempts doc id for quiz completions, or a composite key for joins
            /// (which have no single doc id of their own). This is what prevents duplicate
            /// cards when the dashboard reloads or a listener reconnects.</summary>
            public readonly string Id;
            public readonly ActivityKind Kind;
            public readonly DateTime OccurredAtUtc;

            // Quiz-completion fields (ignored for ClassroomJoin rows)
            public readonly string StudentName;
            public readonly string QuizTitle;
            public readonly string ClassroomName;
            public readonly int ScoreCorrect;
            public readonly int ScoreTotal;
            public readonly float ScorePercent;
            public readonly string Status;

            // Classroom-join fields (ignored for QuizCompleted rows)
            public readonly string JoinText;

            public ActivityCardData(QuizService.ActivityRecord quiz)
            {
                Id = "quiz:" + quiz.DocId;
                Kind = ActivityKind.QuizCompleted;
                OccurredAtUtc = quiz.OccurredAt.ToDateTime();
                StudentName = quiz.StudentName;
                QuizTitle = quiz.QuizTitle;
                ClassroomName = quiz.ClassroomName;
                ScoreCorrect = quiz.ScoreCorrect;
                ScoreTotal = quiz.ScoreTotal;
                ScorePercent = quiz.ScorePercent;
                Status = string.IsNullOrEmpty(quiz.Status) ? "Completed" : quiz.Status;
                JoinText = null;
            }

            public ActivityCardData(ClassroomService.ClassroomJoinRecord join)
            {
                var occurredAt = join.JoinedAt.ToDateTime();
                // Timestamp has no public Seconds/Nanoseconds accessor in this SDK version -
                // ToDateTime().Ticks is already used elsewhere in this codebase for the same
                // purpose and is precise enough to keep this key stable/unique per join.
                Id = $"join:{join.ClassroomId}:{join.StudentName}:{occurredAt.Ticks}";
                Kind = ActivityKind.ClassroomJoin;
                OccurredAtUtc = occurredAt;
                JoinText = $"{join.StudentName} joined '{join.ClassroomName}'";
                StudentName = join.StudentName;
                QuizTitle = null;
                ClassroomName = join.ClassroomName;
                ScoreCorrect = 0;
                ScoreTotal = 0;
                ScorePercent = 0f;
                Status = null;
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
        private Label _profileInitialsLabel;

        // Cloudinary avatar state for #profile-button - same pattern as
        // AdminProfileController's #avatar / StudentDashboardController's
        // #profile-button: show the photo if AvatarUrl is set, otherwise
        // fall back to initials.
        private string _loadedAvatarUrl;
        private Texture2D _avatarTexture;
        private Coroutine _avatarLoadRoutine;

        private Label _classroomsCountLabel;
        private Label _studentsCountLabel;
        private Label _avgScoreValueLabel;

        private Button _createClassroomButton;
        private Button _createFirstClassroomButton;

        private VisualElement _classroomsEmptyState;
        private VisualElement _classroomsList;
        private Button _classroomsViewAllButton;

        private VisualElement _classroomsViewAllOverlay;
        private Button _classroomsViewAllCloseButton;
        private VisualElement _classroomsViewAllList;
        private TextField _classroomsViewAllSearchField;
        private VisualElement _classroomsViewAllNoResults;

        private Button _manageQuizzesButton;
        private Button _viewAnalyticsButton;
        private Button _gamificationButton;

        private Label _recentActivityEmptyLabel;
        private VisualElement _recentActivityList;
        private Button _recentActivityViewAllButton;

        private VisualElement _recentActivityViewAllOverlay;
        private Button _recentActivityViewAllCloseButton;
        private VisualElement _recentActivityViewAllList;
        private TextField _recentActivityViewAllSearchField;
        private VisualElement _recentActivityViewAllNoResults;
        private Label _recentActivityViewAllNoResultsLabel;

        private List<ClassroomSummary> _currentClassrooms = new List<ClassroomSummary>();

        // ---- Recent Activity state ----
        // Two independently-refreshed sources merged into one sorted feed:
        // quiz completions stream in live (StartListeningToRecentActivity), while
        // classroom joins are still a plain one-shot fetch, refreshed whenever the
        // classroom list changes. _activityRowsById is the diff key -> already-
        // rendered-VisualElement map RefreshRecentActivityUI() uses to insert/move/
        // remove only what actually changed instead of clearing the whole list.
        private List<QuizService.ActivityRecord> _liveQuizActivity = new List<QuizService.ActivityRecord>();
        private List<ClassroomService.ClassroomJoinRecord> _joinActivity = new List<ClassroomService.ClassroomJoinRecord>();
        private readonly Dictionary<string, VisualElement> _activityRowsById = new Dictionary<string, VisualElement>();
        private bool _hasRenderedActivityOnce;

        /// <summary>Latest merged/sorted/trimmed feed from BuildMergedActivity(), cached so the
        /// View All search field can re-filter and re-render on every keystroke without
        /// re-merging the two underlying sources.</summary>
        private List<ActivityCardData> _lastMergedActivity = new List<ActivityCardData>();
        private const int MaxRecentActivityItems = 8;

        /// <summary>Max activity rows shown inline on the dashboard before "View All" is used
        /// instead. The full (up to MaxRecentActivityItems) list still renders in
        /// recent-activity-view-all-list - same inline-cap/full-list split as
        /// MaxDashboardClassroomItems below.</summary>
        private const int MaxDashboardActivityItems = 4;

        /// <summary>Max classroom cards shown inline on the dashboard before "View All" is
        /// used instead. Full list still renders in classrooms-view-all-list.</summary>
        private const int MaxDashboardClassroomItems = 5;

        private ListenerRegistration _classroomsListener;
        private QuizService.ActivityListenerHandle _activityListenerHandle;
        private IVisualElementScheduledItem _activityTimeRefreshSchedule;
        private string _lastTeacherName = "";
        private string _lastAvatarUrl;
        private int _lastClassroomCount;
        private int _lastStudentCount;
        private float _lastAvgScorePercent;

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

            SetHeaderData(_lastTeacherName, _lastAvatarUrl);
            SetDashboardStats(_lastClassroomCount, _lastStudentCount);
            RefreshClassroomsUI();
            RefreshRecentActivityUI();

            // Attaching (or re-attaching, if we were disabled) delivers a fresh
            // snapshot immediately - same effect as a fetch - and then keeps
            // pushing updates for as long as this screen stays open, including
            // changes made from elsewhere (e.g. a student joining one of these
            // classrooms from their own device, which a one-shot fetch could
            // never catch). See AdminClassroomService.ListenToMyClassrooms.
            NetworkStatusMonitor.OnAppResumed -= HandleAppResumed;
            NetworkStatusMonitor.OnAppResumed += HandleAppResumed;
            StartListeningToClassrooms();
        }

        private void OnDisable()
        {
            NetworkStatusMonitor.OnAppResumed -= HandleAppResumed;
            UnregisterCallbacks();
            StopListeningToClassrooms();
            StopListeningToRecentActivity();

            _activityTimeRefreshSchedule?.Pause();
            _activityTimeRefreshSchedule = null;

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
                _avatarLoadRoutine = null;
            }

            if (_avatarTexture != null)
            {
                Destroy(_avatarTexture);
                _avatarTexture = null;
            }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _logoutButton?.UnregisterCallback<ClickEvent>(OnLogoutClicked);
            _profileButton?.UnregisterCallback<ClickEvent>(OnProfileClicked);
            _createClassroomButton?.UnregisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _createFirstClassroomButton?.UnregisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _classroomsViewAllButton?.UnregisterCallback<ClickEvent>(OnViewAllClassroomsClicked);
            _classroomsViewAllCloseButton?.UnregisterCallback<ClickEvent>(OnCloseViewAllClassroomsClicked);
            _classroomsViewAllOverlay?.UnregisterCallback<ClickEvent>(OnViewAllOverlayBackdropClicked);
            _classroomsViewAllSearchField?.UnregisterValueChangedCallback(OnClassroomsViewAllSearchChanged);
            _manageQuizzesButton?.UnregisterCallback<ClickEvent>(OnManageQuizzesClicked);
            _viewAnalyticsButton?.UnregisterCallback<ClickEvent>(OnViewAnalyticsClicked);
            _gamificationButton?.UnregisterCallback<ClickEvent>(OnGamificationClicked);
            _recentActivityViewAllButton?.UnregisterCallback<ClickEvent>(OnViewAllActivityClicked);
            _recentActivityViewAllCloseButton?.UnregisterCallback<ClickEvent>(OnCloseViewAllActivityClicked);
            _recentActivityViewAllOverlay?.UnregisterCallback<ClickEvent>(OnViewAllActivityOverlayBackdropClicked);
            _recentActivityViewAllSearchField?.UnregisterValueChangedCallback(OnRecentActivityViewAllSearchChanged);
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
            _profileInitialsLabel = _screenRoot.Q<Label>("profile-initials-label");

            _classroomsCountLabel = _screenRoot.Q<Label>("classrooms-count-label");
            _studentsCountLabel = _screenRoot.Q<Label>("students-count-label");
            _avgScoreValueLabel = _screenRoot.Q<Label>("avg-score-value-label");

            _createClassroomButton = _screenRoot.Q<Button>("create-classroom-button");
            _createFirstClassroomButton = _screenRoot.Q<Button>("create-first-classroom-button");

            _classroomsEmptyState = _screenRoot.Q<VisualElement>("classrooms-empty-state");
            _classroomsList = _screenRoot.Q<VisualElement>("classrooms-list");
            _classroomsViewAllButton = _screenRoot.Q<Button>("classrooms-view-all-button");

            _classroomsViewAllOverlay = _screenRoot.Q<VisualElement>("classrooms-view-all-overlay");
            _classroomsViewAllCloseButton = _screenRoot.Q<Button>("classrooms-view-all-close-button");
            _classroomsViewAllList = _screenRoot.Q<VisualElement>("classrooms-view-all-list");
            _classroomsViewAllSearchField = _screenRoot.Q<TextField>("classrooms-view-all-search-field");
            _classroomsViewAllNoResults = _screenRoot.Q<VisualElement>("classrooms-view-all-no-results");

            _manageQuizzesButton = _screenRoot.Q<Button>("manage-quizzes-button");
            _viewAnalyticsButton = _screenRoot.Q<Button>("view-analytics-button");
            _gamificationButton = _screenRoot.Q<Button>("gamification-button");

            _recentActivityEmptyLabel = _screenRoot.Q<Label>("recent-activity-empty-label");
            _recentActivityList = _screenRoot.Q<VisualElement>("recent-activity-list");
            _recentActivityViewAllButton = _screenRoot.Q<Button>("recent-activity-view-all-button");

            _recentActivityViewAllOverlay = _screenRoot.Q<VisualElement>("recent-activity-view-all-overlay");
            _recentActivityViewAllCloseButton = _screenRoot.Q<Button>("recent-activity-view-all-close-button");
            _recentActivityViewAllList = _screenRoot.Q<VisualElement>("recent-activity-view-all-list");
            _recentActivityViewAllSearchField = _screenRoot.Q<TextField>("recent-activity-view-all-search-field");
            _recentActivityViewAllNoResults = _screenRoot.Q<VisualElement>("recent-activity-view-all-no-results");
            _recentActivityViewAllNoResultsLabel = _recentActivityViewAllNoResults?.Q<Label>();

            Debug.Log($"[AdminDashboardController] Found create classroom button: {_createClassroomButton != null}, classrooms list: {_classroomsList != null}");
        }

        private void WireCallbacks()
        {
            _logoutButton?.RegisterCallback<ClickEvent>(OnLogoutClicked);
            _profileButton?.RegisterCallback<ClickEvent>(OnProfileClicked);
            _createClassroomButton?.RegisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _createFirstClassroomButton?.RegisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _classroomsViewAllButton?.RegisterCallback<ClickEvent>(OnViewAllClassroomsClicked);
            _classroomsViewAllCloseButton?.RegisterCallback<ClickEvent>(OnCloseViewAllClassroomsClicked);
            _classroomsViewAllOverlay?.RegisterCallback<ClickEvent>(OnViewAllOverlayBackdropClicked);
            _classroomsViewAllSearchField?.RegisterValueChangedCallback(OnClassroomsViewAllSearchChanged);
            _manageQuizzesButton?.RegisterCallback<ClickEvent>(OnManageQuizzesClicked);
            _viewAnalyticsButton?.RegisterCallback<ClickEvent>(OnViewAnalyticsClicked);
            _gamificationButton?.RegisterCallback<ClickEvent>(OnGamificationClicked);
            _recentActivityViewAllButton?.RegisterCallback<ClickEvent>(OnViewAllActivityClicked);
            _recentActivityViewAllCloseButton?.RegisterCallback<ClickEvent>(OnCloseViewAllActivityClicked);
            _recentActivityViewAllOverlay?.RegisterCallback<ClickEvent>(OnViewAllActivityOverlayBackdropClicked);
            _recentActivityViewAllSearchField?.RegisterValueChangedCallback(OnRecentActivityViewAllSearchChanged);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push the signed-in teacher's name and avatar into the header.</summary>
        public void SetHeaderData(string teacherName, string avatarUrl = null)
        {
            _lastTeacherName = teacherName ?? "";
            _lastAvatarUrl = avatarUrl;
            if (_headerSubtitleLabel != null) _headerSubtitleLabel.text = $"Welcome back, {teacherName}";
            ApplyAvatar(teacherName, avatarUrl);
        }

        // ---------------- Avatar (Cloudinary) ----------------
        // Same approach as AdminProfileController's #avatar and
        // StudentDashboardController's #profile-button: show the admin's
        // Cloudinary photo if AvatarUrl is set, otherwise fall back to
        // initials. Skips re-downloading when avatarUrl hasn't changed since
        // the last successful load.

        private void ApplyAvatar(string teacherName, string avatarUrl)
        {
            if (_profileButton == null) return;

            if (_profileInitialsLabel != null)
                _profileInitialsLabel.text = GetInitials(teacherName);

            if (string.IsNullOrEmpty(avatarUrl))
            {
                ShowInitialsAvatar();
                return;
            }

            if (avatarUrl == _loadedAvatarUrl && _avatarTexture != null)
            {
                // Already showing this exact image - nothing to do.
                return;
            }

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
            }
            _avatarLoadRoutine = StartCoroutine(LoadAvatarImage(avatarUrl));
        }

        private void ShowInitialsAvatar()
        {
            _profileButton.style.backgroundImage = StyleKeyword.Null;
            if (_profileInitialsLabel != null)
                _profileInitialsLabel.style.display = DisplayStyle.Flex;

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
                _avatarLoadRoutine = null;
            }

            if (_avatarTexture != null)
            {
                Destroy(_avatarTexture);
                _avatarTexture = null;
            }
            _loadedAvatarUrl = null;
        }

        private IEnumerator LoadAvatarImage(string avatarUrl)
        {
            using (var request = UnityWebRequestTexture.GetTexture(avatarUrl))
            {
                yield return request.SendWebRequest();

                _avatarLoadRoutine = null;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[AdminDashboardController] Could not load Cloudinary avatar '{avatarUrl}': {request.error}");
                    // Leave whatever's currently showing (initials, most likely)
                    // rather than blanking the avatar out over a transient network hiccup.
                    yield break;
                }

                if (_avatarTexture != null)
                {
                    Destroy(_avatarTexture);
                }

                _avatarTexture = DownloadHandlerTexture.GetContent(request);
                _loadedAvatarUrl = avatarUrl;

                if (_profileButton == null) yield break; // screen may have been disabled while the request was in flight.

                _profileButton.style.backgroundImage = new StyleBackground(_avatarTexture);
                ApplyCoverBackground(_profileButton);

                // Image fills the circle now - the initials fallback underneath
                // would otherwise show through any transparent corners.
                if (_profileInitialsLabel != null)
                    _profileInitialsLabel.style.display = DisplayStyle.None;
            }
        }

        // unityBackgroundScaleMode is obsolete (deprecated in favor of the CSS-style
        // background-* properties) - this is the ScaleAndCrop-equivalent combination:
        // fill the element, keep aspect ratio, crop overflow, centered.
        private static void ApplyCoverBackground(VisualElement element)
        {
            element.style.backgroundPositionX = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundPositionY = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundRepeat = new StyleBackgroundRepeat(new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat));
            element.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Cover));
        }

        private static string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "?";

            var parts = fullName.Trim().Split(' ');
            if (parts.Length == 1) return parts[0].Substring(0, Mathf.Min(2, parts[0].Length)).ToUpper();

            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        /// <summary>Push real values into the three glass stat cards in the header.</summary>
        public void SetDashboardStats(int classroomCount, int studentCount)
        {
            _lastClassroomCount = classroomCount;
            _lastStudentCount = studentCount;

            if (_classroomsCountLabel != null) _classroomsCountLabel.text = classroomCount.ToString("N0");
            if (_studentsCountLabel != null) _studentsCountLabel.text = studentCount.ToString("N0");
           
        }

        /// <summary>Push the teacher's classrooms into "My Classrooms". Pass an empty/null list to show the empty state.</summary>
        public void SetClassrooms(List<ClassroomSummary> classrooms)
        {
            _currentClassrooms = classrooms ?? new List<ClassroomSummary>();
            RefreshClassroomsUI();
        }

        // Recent Activity no longer has a single external setter - quiz completions are
        // pushed live via StartListeningToRecentActivity() and classroom joins via
        // RefreshJoinActivity(), each independently, then merged by RefreshRecentActivityUI().

        // ---------------- Real data loading ----------------

        /// <summary>
        /// Pulls the signed-in admin's name and their real classrooms from
        /// AdminAuthService / AdminClassroomService and pushes them into the
        /// UI via SetHeaderData/SetDashboardStats/SetClassrooms. Without this,
        /// the dashboard just sits on its placeholder/empty state forever,
        /// since nothing else in the project calls those setters.
        /// </summary>
        private void StartListeningToClassrooms()
        {
            if (_classroomsListener != null) return; // already listening

            var admin = AdminAuthService.Instance != null ? AdminAuthService.Instance.CurrentAdmin : null;
            if (admin == null)
            {
                Debug.LogWarning("[AdminDashboardController] No signed-in admin found; leaving dashboard empty.");
                SetClassrooms(null);
                return;
            }

            SetHeaderData(admin.FullName, admin.AvatarUrl);

            if (AdminClassroomService.Instance == null)
            {
                Debug.LogWarning("[AdminDashboardController] AdminClassroomService not available; classrooms/stats won't load.");
                return;
            }

            _classroomsListener = AdminClassroomService.Instance.ListenToMyClassrooms(records =>
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
                SetDashboardStats(summaries.Count, totalStudents);

                RefreshRecentActivitySources(summaries);
            });
        }
        /// <summary>The app just came back from the background (NetworkStatusMonitor.OnAppResumed).
        /// Firestore normally re-syncs listeners by itself, but re-attaching them here guarantees
        /// the classroom list, the stats and the Student Activity feed are never left showing
        /// pre-background data. Re-attaching serves the cached snapshot instantly and then only
        /// pulls what changed. The classrooms snapshot also re-subscribes the quiz-activity
        /// listener and re-fetches classroom joins (see RefreshRecentActivitySources).</summary>
        private void HandleAppResumed(float secondsAway)
        {
            if (_screenRoot == null) return;

            RefreshActivityTimeLabels(); // "5m ago" labels shouldn't wait for the 30s tick
            StopListeningToClassrooms();
            StartListeningToClassrooms();
        }


        private void StopListeningToClassrooms()
        {
            _classroomsListener?.Stop();
            _classroomsListener = null;
        }

        /// <summary>Called every time the classroom list changes (including the very first
        /// snapshot) - re-subscribes the live quiz-completion listener and re-fetches
        /// classroom joins for the current set of classrooms. Both sources independently
        /// call RefreshRecentActivityUI() whenever they update, so quiz completions show up
        /// the instant a student submits, without waiting on the join fetch or vice versa.</summary>
        private void RefreshRecentActivitySources(List<ClassroomSummary> classrooms)
        {
            var classroomIdAndName = (classrooms ?? new List<ClassroomSummary>())
                .Select(c => (c.ClassroomId, c.Name))
                .ToList();

            StartListeningToRecentActivity(classroomIdAndName);
            RefreshJoinActivity(classroomIdAndName);
        }

        /// <summary>(Re)subscribes QuizService.ListenToRecentActivityForAdmin for the given
        /// classrooms. Always stops any previous subscription first - the classroom set can
        /// change (a classroom created/archived), and leaving an old listener running would
        /// both leak reads and let a stale classroom's activity keep appearing.</summary>
        private void StartListeningToRecentActivity(List<(string ClassroomId, string Name)> classroomIdAndName)
        {
            StopListeningToRecentActivity();

            if (classroomIdAndName == null || classroomIdAndName.Count == 0 || QuizService.Instance == null)
            {
                _liveQuizActivity = new List<QuizService.ActivityRecord>();
                RefreshRecentActivityUI();
                return;
            }

            var classrooms = classroomIdAndName.Select(c => (c.ClassroomId, c.Name)).ToList();

            _activityListenerHandle = QuizService.Instance.ListenToRecentActivityForAdmin(classrooms, activities =>
            {
                // Fires immediately with the current snapshot, then again every time a
                // quiz is submitted in any of these classrooms - each call already
                // arrives merged/sorted/trimmed, so just hand it straight to the UI.
                _liveQuizActivity = activities ?? new List<QuizService.ActivityRecord>();
                RefreshRecentActivityUI();
            }, maxItemsPerClassroom: MaxRecentActivityItems, maxItemsTotal: MaxRecentActivityItems);

            // Keep "2 minutes ago"-style labels fresh on already-rendered cards without
            // waiting on the next Firestore update (which might not arrive for a while).
            if (_activityTimeRefreshSchedule == null && _screenRoot != null)
            {
                _activityTimeRefreshSchedule = _screenRoot.schedule.Execute(RefreshActivityTimeLabels).Every(30000);
            }
        }

        private void StopListeningToRecentActivity()
        {
            _activityListenerHandle?.Stop();
            _activityListenerHandle = null;
        }

        /// <summary>One-shot fetch of classroom joins (ClassroomService.FetchRecentJoinsForClassrooms) -
        /// joins don't need to be live for this feature, so this stays a plain fetch, re-run
        /// whenever the classroom list changes (same trigger as the quiz listener above).</summary>
        private void RefreshJoinActivity(List<(string ClassroomId, string Name)> classroomIdAndName)
        {
            if (classroomIdAndName == null || classroomIdAndName.Count == 0 || ClassroomService.Instance == null)
            {
                _joinActivity = new List<ClassroomService.ClassroomJoinRecord>();
                RefreshRecentActivityUI();
                return;
            }

            var classrooms = classroomIdAndName.Select(c => (c.ClassroomId, c.Name)).ToList();

            ClassroomService.Instance.FetchRecentJoinsForClassrooms(classrooms, joins =>
            {
                _joinActivity = joins ?? new List<ClassroomService.ClassroomJoinRecord>();
                RefreshRecentActivityUI();
            }, maxItems: MaxRecentActivityItems);
        }

        /// <summary>Merges the live quiz-completion feed with the one-shot join feed,
        /// newest-first, trimmed to MaxRecentActivityItems total.</summary>
        private List<ActivityCardData> BuildMergedActivity()
        {
            var merged = new List<ActivityCardData>(_liveQuizActivity.Count + _joinActivity.Count);
            foreach (var quiz in _liveQuizActivity) merged.Add(new ActivityCardData(quiz));
            foreach (var join in _joinActivity) merged.Add(new ActivityCardData(join));

            merged.Sort((a, b) => b.OccurredAtUtc.CompareTo(a.OccurredAtUtc));
            if (merged.Count > MaxRecentActivityItems)
            {
                merged.RemoveRange(MaxRecentActivityItems, merged.Count - MaxRecentActivityItems);
            }
            return merged;
        }

        /// <summary>Timestamp.ToDateTime() returns UTC - compare against UtcNow, not
        /// Now. Same wording as StudentDashboardController's FormatRelativeTime.</summary>
        private static string FormatRelativeTime(DateTime occurredAtUtc)
        {
            var span = DateTime.UtcNow - occurredAtUtc;

            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")} ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
            if (span.TotalDays < 2) return "Yesterday";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
            return occurredAtUtc.ToLocalTime().ToString("MMM d");
        }

        // ---------------- My Classrooms ----------------

        private void RefreshClassroomsUI()
        {
            bool hasClassrooms = _currentClassrooms != null && _currentClassrooms.Count > 0;
            bool hasOverflow = hasClassrooms && _currentClassrooms.Count > MaxDashboardClassroomItems;

            _classroomsEmptyState?.EnableInClassList("hidden", hasClassrooms);
            _classroomsList?.EnableInClassList("hidden", !hasClassrooms);
            _classroomsViewAllButton?.EnableInClassList("hidden", !hasOverflow);

            // If the list shrank back under the cap (or emptied out) while the overlay
            // happened to be open, close it rather than leave it showing a stale/oversized
            // list on top of a dashboard that no longer has an overflow to view.
            if (!hasOverflow) CloseViewAllClassroomsOverlay();

            if (_classroomsList != null)
            {
                _classroomsList.Clear();

                if (hasClassrooms)
                {
                    var inlineItems = _currentClassrooms.Count > MaxDashboardClassroomItems
                        ? _currentClassrooms.GetRange(0, MaxDashboardClassroomItems)
                        : _currentClassrooms;

                    foreach (var classroom in inlineItems)
                    {
                        _classroomsList.Add(BuildClassroomCard(classroom));
                    }
                }
            }

            RefreshViewAllList();
        }

        /// <summary>Rebuilds classrooms-view-all-list from _currentClassrooms, filtered by
        /// whatever's currently typed into the View All search field (matches name or
        /// code, case-insensitive). Called whenever the classroom data changes and every
        /// time the search text changes.</summary>
        private void RefreshViewAllList()
        {
            if (_classroomsViewAllList == null) return;

            string query = _classroomsViewAllSearchField?.value?.Trim() ?? "";

            IEnumerable<ClassroomSummary> filtered = _currentClassrooms;
            if (!string.IsNullOrEmpty(query))
            {
                filtered = _currentClassrooms.Where(c =>
                    (!string.IsNullOrEmpty(c.Name) && c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(c.Code) && c.Code.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var filteredList = filtered.ToList();
            bool hasResults = filteredList.Count > 0;

            _classroomsViewAllNoResults?.EnableInClassList("hidden", hasResults);

            _classroomsViewAllList.Clear();
            foreach (var classroom in filteredList)
            {
                _classroomsViewAllList.Add(BuildClassroomCard(classroom));
            }
        }

        private void OnClassroomsViewAllSearchChanged(ChangeEvent<string> evt)
        {
            RefreshViewAllList();
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

        /// <summary>Re-renders the Recent Activity list from the current merged data
        /// (BuildMergedActivity), diffing against what's already on screen (_activityRowsById)
        /// instead of clearing and rebuilding every row:
        ///  - Cards whose Id is already rendered are reused in place (only moved if their
        ///    sort position changed).
        ///  - Cards whose Id is new are built once and Insert()ed at the right index, with a
        ///    brief highlight so a just-submitted quiz visibly lands at the top.
        ///  - Cards no longer in the merged list (aged past MaxRecentActivityItems) are
        ///    removed.
        /// This only ever touches the recent-activity-list subtree - the rest of the
        /// dashboard (header, stats, classroom cards) is completely untouched by an activity
        /// update, which is what keeps a live quiz submission from ever causing a visible
        /// flicker anywhere else on the screen.</summary>
        private void RefreshRecentActivityUI()
        {
            var merged = BuildMergedActivity();
            bool hasActivity = merged.Count > 0;
            bool hasOverflow = merged.Count > MaxDashboardActivityItems;

            var inline = hasOverflow ? merged.GetRange(0, MaxDashboardActivityItems) : merged;

            _recentActivityEmptyLabel?.EnableInClassList("hidden", hasActivity);
            _recentActivityList?.EnableInClassList("hidden", !hasActivity);
            _recentActivityViewAllButton?.RemoveFromClassList("hidden");

            // If activity dropped back under the cap (or emptied out) while the overlay
            // happened to be open, close it rather than leave it showing a stale list on
            // top of a dashboard that no longer has an overflow to view.
            if (!hasOverflow) CloseViewAllActivityOverlay();

            if (_recentActivityList != null)
            {
                var inlineIds = new HashSet<string>(inline.Select(m => m.Id));

                // Drop rows that fell out of the inline top MaxDashboardActivityItems.
                foreach (var staleId in _activityRowsById.Keys.Where(id => !inlineIds.Contains(id)).ToList())
                {
                    _activityRowsById[staleId].RemoveFromHierarchy();
                    _activityRowsById.Remove(staleId);
                }

                for (int i = 0; i < inline.Count; i++)
                {
                    var data = inline[i];

                    if (_activityRowsById.TryGetValue(data.Id, out var existingRow))
                    {
                        // Already on screen - only touch the DOM if its position actually moved
                        // (e.g. a new item was inserted above it).
                        if (_recentActivityList.IndexOf(existingRow) != i)
                        {
                            existingRow.RemoveFromHierarchy();
                            _recentActivityList.Insert(i, existingRow);
                        }
                        continue;
                    }

                    var row = BuildActivityRow(data);
                    _recentActivityList.Insert(i, row);
                    _activityRowsById[data.Id] = row;

                    // Pop-in highlight for genuinely new entries only - skip on the very first
                    // render so the initial snapshot doesn't flash every card at once.
                    if (_hasRenderedActivityOnce)
                    {
                        row.AddToClassList("activity-row-new");
                        row.schedule.Execute(() => row.RemoveFromClassList("activity-row-new")).ExecuteLater(1500);
                    }
                }
            }

            _hasRenderedActivityOnce = true;

            // Cache so the search field can re-filter/re-render without re-merging the
            // two underlying sources on every keystroke.
            _lastMergedActivity = merged;

            // Keeps the modal in sync too, whether or not it's currently open - cheap to
            // rebuild (no diffing needed since it isn't live-highlighted like the inline list).
            RefreshActivityViewAllList();
        }

        /// <summary>Rebuilds recent-activity-view-all-list from _lastMergedActivity, filtered by
        /// whatever's currently typed into the View All search field (matches student name,
        /// classroom name, or quiz title, case-insensitive - same approach as the classrooms
        /// View All search). Called whenever the activity data changes and every time the
        /// search text changes. Simple clear-and-rebuild each call - the modal isn't
        /// live-diffed/highlighted the way the dashboard's inline list is.</summary>
        private void RefreshActivityViewAllList()
        {
            if (_recentActivityViewAllList == null) return;

            string query = _recentActivityViewAllSearchField?.value?.Trim() ?? "";

            IEnumerable<ActivityCardData> filtered = _lastMergedActivity;
            if (!string.IsNullOrEmpty(query))
            {
                filtered = _lastMergedActivity.Where(data =>
                    (!string.IsNullOrEmpty(data.StudentName) && data.StudentName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(data.ClassroomName) && data.ClassroomName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(data.QuizTitle) && data.QuizTitle.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var filteredList = filtered.ToList();
            bool hasResults = filteredList.Count > 0;

            if (_recentActivityViewAllNoResultsLabel != null)
            {
                _recentActivityViewAllNoResultsLabel.text = string.IsNullOrEmpty(query)
                    ? "No student activity yet"
                    : "No student activity matches your search";
            }
            _recentActivityViewAllNoResults?.EnableInClassList("hidden", hasResults);

            _recentActivityViewAllList.Clear();
            foreach (var data in filteredList)
            {
                _recentActivityViewAllList.Add(BuildActivityRow(data));
            }
        }

        private void OnRecentActivityViewAllSearchChanged(ChangeEvent<string> evt)
        {
            RefreshActivityViewAllList();
        }

        /// <summary>Periodic tick (see StartListeningToRecentActivity) that just refreshes
        /// the "X minutes ago" text on already-rendered rows, without touching layout,
        /// order, or anything else - so a card that's been sitting there for a while
        /// doesn't keep reading "Just now".</summary>
        private void RefreshActivityTimeLabels()
        {
            foreach (var row in _activityRowsById.Values)
            {
                var timeLabel = row.Q<Label>(className: "recent-activity-time");
                if (timeLabel == null) continue;
                if (row.userData is DateTime occurredAtUtc)
                {
                    timeLabel.text = FormatRelativeTime(occurredAtUtc);
                }
            }
        }

        private VisualElement BuildActivityRow(ActivityCardData data)
        {
            return data.Kind == ActivityKind.QuizCompleted ? BuildQuizActivityCard(data) : BuildJoinActivityRow(data);
        }

        /// <summary>Rich card for a quiz-completion event: an initials avatar (tinted by score
        /// band) on the left, then student name + relative time, quiz title, classroom name,
        /// and a bottom row with the score badge, a slim progress bar and the status pill.</summary>
        private VisualElement BuildQuizActivityCard(ActivityCardData data)
        {
            string band = GetScoreBand(data.ScorePercent);

            var card = new VisualElement();
            card.AddToClassList("recent-activity-row");
            card.AddToClassList("activity-quiz-card");
            card.userData = data.OccurredAtUtc;

            card.Add(BuildActivityAvatar(data.StudentName, band));

            var body = new VisualElement();
            body.AddToClassList("activity-body");

            var topRow = new VisualElement();
            topRow.AddToClassList("activity-card-top-row");

            var studentLabel = new Label(data.StudentName);
            studentLabel.AddToClassList("activity-student-name");

            var timeLabel = new Label(FormatRelativeTime(data.OccurredAtUtc));
            timeLabel.AddToClassList("recent-activity-time");

            topRow.Add(studentLabel);
            topRow.Add(timeLabel);
            body.Add(topRow);

            var quizLabel = new Label(data.QuizTitle);
            quizLabel.AddToClassList("activity-quiz-title");
            body.Add(quizLabel);

            var classroomLabel = new Label(data.ClassroomName);
            classroomLabel.AddToClassList("activity-classroom-name");
            body.Add(classroomLabel);

            var metaRow = new VisualElement();
            metaRow.AddToClassList("activity-meta-row");

            var scoreLabel = new Label($"{data.ScoreCorrect}/{data.ScoreTotal} \u2022 {Mathf.RoundToInt(data.ScorePercent)}%");
            scoreLabel.AddToClassList("activity-score-badge");
            scoreLabel.AddToClassList("activity-score-badge-" + band);

            var track = new VisualElement();
            track.AddToClassList("activity-score-track");
            var fill = new VisualElement();
            fill.AddToClassList("activity-score-fill");
            fill.AddToClassList("activity-score-fill-" + band);
            fill.style.width = Length.Percent(Mathf.Clamp(data.ScorePercent, 0f, 100f));
            track.Add(fill);

            var statusPill = new Label(data.Status);
            statusPill.AddToClassList("activity-status-pill");

            metaRow.Add(scoreLabel);
            metaRow.Add(track);
            metaRow.Add(statusPill);
            body.Add(metaRow);

            card.Add(body);
            return card;
        }

        /// <summary>Row for a classroom-join event: same avatar + name + time layout as the
        /// quiz card (in the neutral blue tint), with a single "Joined ..." line under it.</summary>
        private VisualElement BuildJoinActivityRow(ActivityCardData data)
        {
            var row = new VisualElement();
            row.AddToClassList("recent-activity-row");
            row.userData = data.OccurredAtUtc;

            row.Add(BuildActivityAvatar(data.StudentName, null));

            var body = new VisualElement();
            body.AddToClassList("activity-body");

            var topRow = new VisualElement();
            topRow.AddToClassList("activity-card-top-row");

            var nameLabel = new Label(data.StudentName);
            nameLabel.AddToClassList("activity-student-name");

            var timeLabel = new Label(FormatRelativeTime(data.OccurredAtUtc));
            timeLabel.AddToClassList("recent-activity-time");

            topRow.Add(nameLabel);
            topRow.Add(timeLabel);
            body.Add(topRow);

            var joinLabel = new Label($"Joined {data.ClassroomName}");
            joinLabel.AddToClassList("activity-join-text");
            body.Add(joinLabel);

            row.Add(body);
            return row;
        }

        /// <summary>Round avatar with the student's initials. <paramref name="band"/> is
        /// "high"/"mid"/"low" to tint it by score, or null for the neutral blue.</summary>
        private static VisualElement BuildActivityAvatar(string studentName, string band)
        {
            var avatar = new VisualElement();
            avatar.AddToClassList("activity-avatar");
            if (!string.IsNullOrEmpty(band)) avatar.AddToClassList("activity-avatar-" + band);

            var initials = new Label(GetActivityInitials(studentName));
            initials.AddToClassList("activity-avatar-label");
            avatar.Add(initials);
            return avatar;
        }

        /// <summary>First letter of the first and last word ("Juan Dela Cruz" -> "JC"),
        /// or just one letter for a single name.</summary>
        private static string GetActivityInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, 1).ToUpperInvariant();
            return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
        }

        /// <summary>75%+ = "high" (green), 50-74% = "mid" (amber), below 50% = "low" (red).</summary>
        private static string GetScoreBand(float percent)
        {
            if (percent >= 75f) return "high";
            if (percent >= 50f) return "mid";
            return "low";
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

        private void OnViewAllClassroomsClicked(ClickEvent evt)
        {
            if (_classroomsViewAllSearchField != null) _classroomsViewAllSearchField.value = "";
            RefreshViewAllList();
            _classroomsViewAllOverlay?.RemoveFromClassList("hidden");
        }

        private void OnCloseViewAllClassroomsClicked(ClickEvent evt)
        {
            CloseViewAllClassroomsOverlay();
        }

        /// <summary>Tapping the dimmed backdrop closes the overlay, same as the close
        /// button - but only when the tap actually landed on the backdrop itself, not on
        /// the card or anything inside it (ClickEvent bubbles up from children).</summary>
        private void OnViewAllOverlayBackdropClicked(ClickEvent evt)
        {
            if (evt.target == _classroomsViewAllOverlay)
            {
                CloseViewAllClassroomsOverlay();
            }
        }

        private void CloseViewAllClassroomsOverlay()
        {
            _classroomsViewAllOverlay?.AddToClassList("hidden");
        }

        private void OnViewAllActivityClicked(ClickEvent evt)
        {
            if (_recentActivityViewAllSearchField != null) _recentActivityViewAllSearchField.value = "";
            _lastMergedActivity = BuildMergedActivity();
            RefreshActivityViewAllList();
            _recentActivityViewAllOverlay?.RemoveFromClassList("hidden");
        }

        private void OnCloseViewAllActivityClicked(ClickEvent evt)
        {
            CloseViewAllActivityOverlay();
        }

        /// <summary>Tapping the dimmed backdrop closes the overlay, same as the close
        /// button - but only when the tap actually landed on the backdrop itself, not on
        /// the card or anything inside it (ClickEvent bubbles up from children).</summary>
        private void OnViewAllActivityOverlayBackdropClicked(ClickEvent evt)
        {
            if (evt.target == _recentActivityViewAllOverlay)
            {
                CloseViewAllActivityOverlay();
            }
        }

        private void CloseViewAllActivityOverlay()
        {
            _recentActivityViewAllOverlay?.AddToClassList("hidden");
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