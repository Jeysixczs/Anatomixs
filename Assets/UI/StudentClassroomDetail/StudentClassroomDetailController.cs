using System;
using System.Collections.Generic;
using System.Linq;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentClassroomDetail.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// The student-facing counterpart to AdminClassroomDetail, for ONE classroom
    /// (a student may belong to several - see StudentClassroomHub for the list
    /// they pick from). Six tabs:
    ///  - Overview: classroom info + teacher-posted announcements (see
    ///    AdminClassroomDetailController's Announcements tab, which is where a
    ///    teacher creates the entries pushed into SetAnnouncements() here)
    ///  - Students: read-only roster of classmates
    ///  - Available Quizzes: quizzes the teacher published to this classroom,
    ///    with a Start button (locked ones show a "Locked" badge instead)
    ///  - Leaderboard: ranked by score, gated by the teacher's "Show to
    ///    Students" toggle (AdminClassroomDetailController.LeaderboardVisibleToStudents)
    ///  - Scores: this student's own completed-quiz history for this classroom
    ///  - Badges: this classroom's teacher's configured badges (AdminGamificationService,
    ///    per-teacher), each flagged earned/locked against this student's global
    ///    points/earned-badges (see LoadBadges())
    ///
    /// UIManager.ShowStudentClassroomDetail(classroomId, classroomName, classroomCode)
    /// calls SetClassroomIdentity() right after showing this screen; follow that
    /// with the rest of the Set...() calls from your backend.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentClassroomDetailController : MonoBehaviour
    {
        [Header("Gradient colors (matches Join Classroom / AdminClassroomDetail: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private Label _classroomNameLabel;
        private Label _instructorLabel;
        private Label _studentsEnrolledValueLabel;
        private Label _quizzesAvailableValueLabel;

        // Archived-blocked state (shown in place of screen-scroll when the
        // classroom's isArchived flag is true - see LoadClassroomContent()).
        private VisualElement _screenScroll;
        private VisualElement _archivedBlockedPanel;
        private Button _archivedBlockedBackButton;

        // Tabs
        private Button _overviewTabButton;
        private Button _studentsTabButton;
        private Button _quizzesTabButton;
        private Button _leaderboardTabButton;
        private Button _scoresTabButton;
        private Button _badgesTabButton;
        private VisualElement _overviewPanel;
        private VisualElement _studentsPanel;
        private VisualElement _quizzesPanel;
        private VisualElement _leaderboardPanel;
        private VisualElement _scoresPanel;
        private VisualElement _badgesPanel;

        // Overview tab
        private Label _teacherValueLabel;
        private Label _descriptionValueLabel;
        private Label _totalPointsValueLabel;
        private VisualElement _announcementsEmptyState;
        private VisualElement _announcementsList;

        // Students tab
        private Label _studentsCountTitleLabel;
        private VisualElement _studentsEmptyState;
        private VisualElement _peersList;

        // Quizzes tab
        private VisualElement _quizzesEmptyState;
        private VisualElement _quizzesList;

        // Leaderboard tab
        private VisualElement _leaderboardLockedState;
        private VisualElement _leaderboardCard;
        private VisualElement _leaderboardEmptyState;
        private VisualElement _leaderboardList;

        // Scores tab
        private VisualElement _scoresEmptyState;
        private VisualElement _scoresList;

        // Badges tab
        private VisualElement _badgesEmptyState;
        private VisualElement _badgesList;

        /// <summary>A single row in the Overview tab's Announcements section.</summary>
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

        /// <summary>A single row in the Students tab.</summary>
        public struct PeerInfo
        {
            public string Name;
            public int Level;

            public PeerInfo(string name, int level)
            {
                Name = name;
                Level = level;
            }
        }

        /// <summary>Why the Start Quiz button on a card should be disabled, per this
        /// student's QuizService.CheckAttemptEligibility result for that quiz. None
        /// means the button is a normal, clickable "Start Quiz".</summary>
        public enum QuizStartBlock
        {
            None,
            DeadlineExpired,
            NoAttemptsLeft
        }

        /// <summary>A single card in the Available Quizzes tab.</summary>
        public struct QuizCardInfo
        {
            public string QuizId;
            public string Title;
            public string Subject;
            public int Questions;
            public int TimeMinutes;
            public bool HasTimeLimit;
            /// <summary>0 = unlimited.</summary>
            public int MaxAttempts;
            public bool IsDeadlineEnabled;
            public DateTime? DeadlineUtc;
            public int Points;
            public string Difficulty;
            public bool IsAvailable;
            /// <summary>Per-student restriction state for the Start button - see
            /// QuizStartBlock. Always None until LoadQuizStartEligibility() resolves.</summary>
            public QuizStartBlock StartBlock;

            public QuizCardInfo(string quizId, string title, string subject, int questions, int timeMinutes,
                bool hasTimeLimit, int maxAttempts, bool isDeadlineEnabled, DateTime? deadlineUtc,
                int points, string difficulty, bool isAvailable, QuizStartBlock startBlock = QuizStartBlock.None)
            {
                QuizId = quizId;
                Title = title;
                Subject = subject;
                Questions = questions;
                TimeMinutes = timeMinutes;
                HasTimeLimit = hasTimeLimit;
                MaxAttempts = maxAttempts;
                IsDeadlineEnabled = isDeadlineEnabled;
                DeadlineUtc = deadlineUtc;
                Points = points;
                Difficulty = difficulty;
                IsAvailable = isAvailable;
                StartBlock = startBlock;
            }
        }

        /// <summary>A single ranked row in the Leaderboard tab.</summary>
        public struct PerformerInfo
        {
            public string Name;
            public int Level;
            public int QuizzesCompleted;
            public float ScorePercent;
            public int Points;
            public bool IsCurrentStudent;

            public PerformerInfo(string name, int level, int quizzesCompleted, float scorePercent, int points, bool isCurrentStudent = false)
            {
                Name = name;
                Level = level;
                QuizzesCompleted = quizzesCompleted;
                ScorePercent = scorePercent;
                Points = points;
                IsCurrentStudent = isCurrentStudent;
            }
        }

        /// <summary>A single row in the Scores tab's Quiz History.</summary>
        public struct ScoreHistoryInfo
        {
            public string QuizTitle;
            public string CompletedDateText;
            public bool Passed;
            public int ScoreCorrect;
            public int ScoreTotal;
            public string TimeText;
            public int Attempt;

            public ScoreHistoryInfo(string quizTitle, string completedDateText, bool passed, int scoreCorrect, int scoreTotal, string timeText, int attempt)
            {
                QuizTitle = quizTitle;
                CompletedDateText = completedDateText;
                Passed = passed;
                ScoreCorrect = scoreCorrect;
                ScoreTotal = scoreTotal;
                TimeText = timeText;
                Attempt = attempt;
            }
        }

        /// <summary>A single card in the Badges tab - this classroom's teacher's
        /// configured badges (AdminGamificationService.BadgeEntry), each flagged
        /// against this student's global earned-badges/points.</summary>
        public struct BadgeInfo
        {
            public string BadgeId;
            public string Name;
            public string IconEmoji;
            public int PointsRequired;
            public bool Earned;

            public BadgeInfo(string badgeId, string name, string iconEmoji, int pointsRequired, bool earned)
            {
                BadgeId = badgeId;
                Name = name;
                IconEmoji = iconEmoji;
                PointsRequired = pointsRequired;
                Earned = earned;
            }
        }

        // Cached state so it survives the UIManager's clear-and-rebuild screen transitions.
        private string _classroomId = "";
        /// <summary>The classroomId LoadClassroomContent() last kicked off a fetch for.
        /// This screen is reused across classrooms (see SetClassroomIdentity), so
        /// "loaded once" has to be tracked per-id rather than as a single session
        /// flag - re-entering the SAME classroom (OnEnable firing without a fresh
        /// SetClassroomIdentity call) can skip the refetch, but navigating to a
        /// DIFFERENT classroom always needs one.</summary>
        private string _lastLoadedClassroomId = "";
        private string _classroomName = "";
        private string _instructorName = "";
        private readonly List<AnnouncementInfo> _lastAnnouncements = new();
        private readonly List<PeerInfo> _lastPeers = new();
        private readonly List<QuizCardInfo> _lastQuizzes = new();
        private bool _leaderboardVisible;
        private readonly List<PerformerInfo> _lastLeaderboard = new();
        private readonly List<ScoreHistoryInfo> _lastScores = new();
        private readonly List<BadgeInfo> _lastBadges = new();
        private int _lastBadgePoints;

        private void OnEnable()
        {
            Debug.Log("[StudentClassroomDetailController] OnEnable called");

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
                Debug.LogError("[StudentClassroomDetailController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            ShowOverviewTab();

            // Re-apply cached identity/state (survives screen rebuilds within the same session).
            if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
            if (_instructorLabel != null) _instructorLabel.text = $"Instructor: {_instructorName}";
            SetAnnouncements(_lastAnnouncements);
            SetPeers(_lastPeers);
            SetQuizzes(_lastQuizzes);
            SetLeaderboard(_leaderboardVisible, _lastLeaderboard);
            SetScores(_lastScores);
            SetBadges(_lastBadgePoints, _lastBadges);

            // Screen was re-enabled (e.g. switching tabs elsewhere and coming back).
            // Only refetch if this is a DIFFERENT classroom than what's currently
            // loaded - SetClassroomIdentity() already forces a fresh load whenever
            // the student actually navigates into a classroom (same or different),
            // so this only catches the "re-enabled with nothing new to show" case,
            // which the cache repaint above already handled.
            if (!string.IsNullOrEmpty(_classroomId) && _classroomId != _lastLoadedClassroomId)
            {
                LoadClassroomContent();
            }
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
            _archivedBlockedBackButton?.UnregisterCallback<ClickEvent>(OnArchivedBlockedBackClicked);

            _overviewTabButton?.UnregisterCallback<ClickEvent>(OnOverviewTabClicked);
            _studentsTabButton?.UnregisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.UnregisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _leaderboardTabButton?.UnregisterCallback<ClickEvent>(OnLeaderboardTabClicked);
            _scoresTabButton?.UnregisterCallback<ClickEvent>(OnScoresTabClicked);
            _badgesTabButton?.UnregisterCallback<ClickEvent>(OnBadgesTabClicked);

            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentClassroomDetailController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _screenScroll = _screenRoot.Q<VisualElement>("screen-scroll");
            _archivedBlockedPanel = _screenRoot.Q<VisualElement>("archived-blocked-panel");
            _archivedBlockedBackButton = _screenRoot.Q<Button>("archived-blocked-back-button");

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");
            _classroomNameLabel = _screenRoot.Q<Label>("classroom-name-label");
            _instructorLabel = _screenRoot.Q<Label>("instructor-label");
            _studentsEnrolledValueLabel = _screenRoot.Q<Label>("students-enrolled-value-label");
            _quizzesAvailableValueLabel = _screenRoot.Q<Label>("quizzes-available-value-label");

            _overviewTabButton = _screenRoot.Q<Button>("overview-tab-button");
            _studentsTabButton = _screenRoot.Q<Button>("students-tab-button");
            _quizzesTabButton = _screenRoot.Q<Button>("quizzes-tab-button");
            _leaderboardTabButton = _screenRoot.Q<Button>("leaderboard-tab-button");
            _scoresTabButton = _screenRoot.Q<Button>("scores-tab-button");
            _badgesTabButton = _screenRoot.Q<Button>("badges-tab-button");
            _overviewPanel = _screenRoot.Q<VisualElement>("overview-panel");
            _studentsPanel = _screenRoot.Q<VisualElement>("students-panel");
            _quizzesPanel = _screenRoot.Q<VisualElement>("quizzes-panel");
            _leaderboardPanel = _screenRoot.Q<VisualElement>("leaderboard-panel");
            _scoresPanel = _screenRoot.Q<VisualElement>("scores-panel");
            _badgesPanel = _screenRoot.Q<VisualElement>("badges-panel");

            _teacherValueLabel = _screenRoot.Q<Label>("teacher-value-label");
            _descriptionValueLabel = _screenRoot.Q<Label>("description-value-label");
            _totalPointsValueLabel = _screenRoot.Q<Label>("total-points-value-label");
            _announcementsEmptyState = _screenRoot.Q<VisualElement>("announcements-empty-state");
            _announcementsList = _screenRoot.Q<VisualElement>("announcements-list");

            _studentsCountTitleLabel = _screenRoot.Q<Label>("students-count-title-label");
            _studentsEmptyState = _screenRoot.Q<VisualElement>("students-empty-state");
            _peersList = _screenRoot.Q<VisualElement>("peers-list");

            _quizzesEmptyState = _screenRoot.Q<VisualElement>("quizzes-empty-state");
            _quizzesList = _screenRoot.Q<VisualElement>("quizzes-list");

            _leaderboardLockedState = _screenRoot.Q<VisualElement>("leaderboard-locked-state");
            _leaderboardCard = _screenRoot.Q<VisualElement>("leaderboard-card");
            _leaderboardEmptyState = _screenRoot.Q<VisualElement>("leaderboard-empty-state");
            _leaderboardList = _screenRoot.Q<VisualElement>("leaderboard-list");

            _scoresEmptyState = _screenRoot.Q<VisualElement>("scores-empty-state");
            _scoresList = _screenRoot.Q<VisualElement>("scores-list");

            _badgesEmptyState = _screenRoot.Q<VisualElement>("badges-empty-state");
            _badgesList = _screenRoot.Q<VisualElement>("badges-list");

            Debug.Log($"[StudentClassroomDetailController] Found tabs: {_overviewTabButton != null}/{_studentsTabButton != null}/{_quizzesTabButton != null}/{_leaderboardTabButton != null}/{_scoresTabButton != null}/{_badgesTabButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _archivedBlockedBackButton?.RegisterCallback<ClickEvent>(OnArchivedBlockedBackClicked);

            _overviewTabButton?.RegisterCallback<ClickEvent>(OnOverviewTabClicked);
            _studentsTabButton?.RegisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.RegisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _leaderboardTabButton?.RegisterCallback<ClickEvent>(OnLeaderboardTabClicked);
            _scoresTabButton?.RegisterCallback<ClickEvent>(OnScoresTabClicked);
            _badgesTabButton?.RegisterCallback<ClickEvent>(OnBadgesTabClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Called by UIManager.ShowStudentClassroomDetail() right after the screen
        /// is shown. Also kicks off the Firestore loads for every tab (Overview,
        /// Students, Available Quizzes, Leaderboard, Scores) via ClassroomService.</summary>
        public void SetClassroomIdentity(string classroomId, string classroomName, string instructorName)
        {
            _classroomId = classroomId ?? "";
            _classroomName = classroomName ?? "";
            _instructorName = instructorName ?? "";

            if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
            if (_instructorLabel != null) _instructorLabel.text = $"Instructor: {_instructorName}";

            // This screen is reused across classrooms - make sure a previous classroom's
            // archived-block isn't still showing while we load this one.
            HideArchivedBlockedState();

            // Clear whatever the previously-viewed classroom left behind so there's
            // no window where this classroom's screen still shows another
            // classroom's announcements/roster/etc. while the new fetch is in
            // flight - better an empty state briefly than the wrong classroom's data.
            SetAnnouncements(new List<AnnouncementInfo>());
            SetPeers(new List<PeerInfo>());
            SetQuizzes(new List<QuizCardInfo>());
            SetLeaderboard(false, new List<PerformerInfo>());
            SetScores(new List<ScoreHistoryInfo>());
            SetBadges(0, new List<BadgeInfo>());

            LoadClassroomContent();
        }

        // ---------------- Loading from ClassroomService ----------------

        private void LoadClassroomContent()
        {
            if (string.IsNullOrEmpty(_classroomId))
            {
                Debug.LogWarning("[StudentClassroomDetailController] LoadClassroomContent called with no classroom id set.");
                return;
            }

            if (ClassroomService.Instance == null)
            {
                Debug.LogWarning("[StudentClassroomDetailController] ClassroomService not available yet.");
                return;
            }

            // This screen is reused across classrooms (see the OnEnable comment about
            // surviving screen rebuilds), so if a student opens classroom A then
            // quickly backs out and opens classroom B, an in-flight Firestore call
            // for A can still resolve after _classroomId has moved on to B - Task
            // completion order isn't guaranteed to match call order. Snapshot the id
            // we're fetching FOR here, and every callback below checks it against
            // the live _classroomId before touching the UI, so a late response for a
            // classroom the student already left can never overwrite what's on
            // screen for the classroom they're now viewing (announcements included).
            string requestedClassroomId = _classroomId;
            bool IsStale() => requestedClassroomId != _classroomId;

            _lastLoadedClassroomId = requestedClassroomId;

            ClassroomService.Instance.FetchClassroomDetail(requestedClassroomId, detail =>
            {
                if (IsStale() || detail == null) return;

                if (detail.IsArchived)
                {
                    ShowArchivedBlockedState();
                    return;
                }

                HideArchivedBlockedState();

                SetHeaderStats(detail.StudentCount, detail.PublishedQuizIds?.Count ?? 0);

                ClassroomService.Instance.FetchAvailableQuizzes(requestedClassroomId, quizzes =>
                {
                    if (IsStale()) return;

                    LoadQuizStartEligibility(quizzes, IsStale);
                });

                ClassroomService.Instance.FetchClassroomRoster(requestedClassroomId, roster =>
                {
                    if (IsStale()) return;

                    var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;

                    SetPeers(roster.ConvertAll(m => new PeerInfo(m.Name, m.Level)));

                    var ranked = new List<ClassroomService.MemberStat>(roster);
                    ranked.Sort((a, b) => b.Points != a.Points ? b.Points.CompareTo(a.Points) : b.QuizzesCompleted.CompareTo(a.QuizzesCompleted));

                    SetLeaderboard(detail.LeaderboardVisible, ranked.ConvertAll(m => new PerformerInfo(
                        m.Name, m.Level, m.QuizzesCompleted, m.AvgScorePercent, m.Points,
                        student != null && m.StudentId == student.Uid)));

                    int myPoints = 0;
                    if (student != null)
                    {
                        var me = roster.Find(m => m.StudentId == student.Uid);
                        if (me != null) myPoints = me.Points;
                    }
                    SetClassroomInfo(detail.TeacherName, detail.Description, myPoints);
                });

                // Announcements/Scores are only fetched once we know the classroom isn't
                // archived - both are scoped to requestedClassroomId at the Firestore level
                // (classrooms/{id}/announcements, this student's own quizAttempts), AND
                // guarded against a stale/late response painting a previous classroom's
                // data over this one.
                ClassroomService.Instance.FetchAnnouncements(requestedClassroomId, announcements =>
                {
                    if (IsStale()) return;

                    SetAnnouncements(announcements.ConvertAll(a => new AnnouncementInfo(
                        a.Title, a.Body, a.CreatedAt.ToDateTime().ToLocalTime().ToString("MMM d, yyyy"))));
                });

                ClassroomService.Instance.FetchMyScores(requestedClassroomId, scores =>
                {
                    if (IsStale()) return;

                    SetScores(scores.ConvertAll(s => new ScoreHistoryInfo(
                        s.QuizTitle, s.CompletedAt.ToDateTime().ToLocalTime().ToString("MMM d, yyyy"), s.Passed,
                        s.ScoreCorrect, s.ScoreTotal, FormatDuration(s.TimeSpentSeconds), s.Attempt)));
                });

                LoadBadges(detail.TeacherId, IsStale);
            });
        }

        /// <summary>
        /// Loads this classroom's teacher's badge config (AdminGamificationService,
        /// per-teacher) and flags each badge as earned/locked against this student's
        /// global points/earned-badges (PlayerSessionManager.CurrentStudent) - badges
        /// are defined per teacher but always evaluated against the student's total
        /// points across every classroom, same as ComputeNewlyEarnedBadges/
        /// ComputeLevelProgress elsewhere in the app.
        /// </summary>
        private void LoadBadges(string teacherId, Func<bool> isStale)
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null || AdminGamificationService.Instance == null)
            {
                if (!isStale()) SetBadges(0, new List<BadgeInfo>());
                return;
            }

            AdminGamificationService.Instance.FetchSettingsForTeacher(teacherId, settings =>
            {
                if (isStale()) return;

                var earnedIds = new HashSet<string>(student.BadgesEarned ?? new List<string>());

                var badges = (settings?.Badges ?? new List<AdminGamificationService.BadgeEntry>())
                    .OrderBy(b => b.PointsRequired)
                    .Select(b => new BadgeInfo(
                        b.BadgeId,
                        b.Name,
                        b.IconEmoji,
                        b.PointsRequired,
                        earnedIds.Contains(b.BadgeId) || student.TotalPoints >= b.PointsRequired))
                    .ToList();

                SetBadges(student.TotalPoints, badges);
            });
        }

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string FormatDuration(int totalSeconds)
        {
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
        }

        /// <summary>
        /// Resolves this student's Start-button state for every published quiz before
        /// the Available Quizzes tab is populated. Runs QuizService.CheckAttemptEligibility
        /// per quiz (deadline + attempts-used, same check StudentQuizGameplayController.LoadQuiz
        /// re-runs as the real enforcement point when Start is actually tapped) so the button
        /// can already show "Deadline Expired" / "No More Attempts" and be disabled up front,
        /// instead of only failing after the student taps it.
        /// </summary>
        private void LoadQuizStartEligibility(List<ClassroomService.QuizSummary> quizzes, Func<bool> isStale)
        {
            if (quizzes == null || quizzes.Count == 0)
            {
                if (!isStale()) SetQuizzes(new List<QuizCardInfo>());
                return;
            }

            if (QuizService.Instance == null)
            {
                // Backend not ready yet - fall back to a normal, unblocked Start button
                // rather than leaving the tab empty; LoadQuiz() still guards entry either way.
                if (!isStale()) SetQuizzes(quizzes.ConvertAll(q => ToQuizCardInfo(q, QuizStartBlock.None)));
                return;
            }

            var cards = new QuizCardInfo[quizzes.Count];
            int remaining = quizzes.Count;

            for (int i = 0; i < quizzes.Count; i++)
            {
                var q = quizzes[i];
                int index = i;

                QuizService.Instance.CheckAttemptEligibility(q.QuizId, (checkOk, checkError, eligibility) =>
                {
                    var block = QuizStartBlock.None;
                    if (checkOk && eligibility != null && !eligibility.CanStart)
                    {
                        // Mirrors CheckAttemptEligibility's own priority (deadline checked
                        // before attempts), so the reason shown here always matches what
                        // LoadQuiz()'s re-check would report if the button were clickable.
                        bool deadlinePassed = q.IsDeadlineEnabled && q.DeadlineUtc.HasValue
                            && DateTime.UtcNow > q.DeadlineUtc.Value;
                        block = deadlinePassed ? QuizStartBlock.DeadlineExpired : QuizStartBlock.NoAttemptsLeft;
                    }

                    cards[index] = ToQuizCardInfo(q, block);

                    remaining--;
                    if (remaining == 0 && !isStale())
                    {
                        SetQuizzes(cards.ToList());
                    }
                });
            }
        }

        private static QuizCardInfo ToQuizCardInfo(ClassroomService.QuizSummary q, QuizStartBlock startBlock)
        {
            return new QuizCardInfo(
                q.QuizId, q.Title, q.Category, q.QuestionCount, q.TimeLimitMinutes, q.HasTimeLimit,
                q.MaxAttempts, q.IsDeadlineEnabled, q.DeadlineUtc,
                q.TotalPoints, Capitalize(q.Difficulty), true, startBlock);
        }

        /// <summary>Push the two glass header stat cards.</summary>
        public void SetHeaderStats(int studentsEnrolled, int quizzesAvailable)
        {
            if (_studentsEnrolledValueLabel != null) _studentsEnrolledValueLabel.text = studentsEnrolled.ToString("N0");
            if (_quizzesAvailableValueLabel != null) _quizzesAvailableValueLabel.text = quizzesAvailable.ToString("N0");
        }

        /// <summary>Push the Overview tab's "Classroom Info" card.</summary>
        public void SetClassroomInfo(string teacherName, string description, int totalPoints)
        {
            if (_teacherValueLabel != null) _teacherValueLabel.text = teacherName;
            if (_descriptionValueLabel != null) _descriptionValueLabel.text = description;
            if (_totalPointsValueLabel != null) _totalPointsValueLabel.text = totalPoints.ToString("N0");
        }

        /// <summary>
        /// Push teacher-posted announcements into the Overview tab. This is fed by
        /// the teacher's Announcements tab on AdminClassroomDetail - forward
        /// whatever AdminClassroomDetailController.GetAnnouncements() (or your
        /// backend equivalent) returns for this classroom.
        /// </summary>
        public void SetAnnouncements(List<AnnouncementInfo> announcements)
        {
            _lastAnnouncements.Clear();
            if (announcements != null) _lastAnnouncements.AddRange(announcements);

            bool hasData = _lastAnnouncements.Count > 0;
            _announcementsEmptyState?.EnableInClassList("hidden", hasData);
            _announcementsList?.EnableInClassList("hidden", !hasData);

            if (_announcementsList == null) return;
            _announcementsList.Clear();
            if (!hasData) return;

            foreach (var announcement in _lastAnnouncements)
            {
                _announcementsList.Add(BuildAnnouncementCard(announcement));
            }
        }

        /// <summary>Push the classroom roster into the Students tab (empty state if the list is empty/null).</summary>
        public void SetPeers(List<PeerInfo> peers)
        {
            _lastPeers.Clear();
            if (peers != null) _lastPeers.AddRange(peers);

            bool hasPeers = _lastPeers.Count > 0;
            if (_studentsCountTitleLabel != null) _studentsCountTitleLabel.text = $"Enrolled Students ({_lastPeers.Count})";

            _studentsEmptyState?.EnableInClassList("hidden", hasPeers);
            _peersList?.EnableInClassList("hidden", !hasPeers);

            if (_peersList == null) return;
            _peersList.Clear();
            if (!hasPeers) return;

            for (int i = 0; i < _lastPeers.Count; i++)
            {
                _peersList.Add(BuildPeerRow(_lastPeers[i], i == _lastPeers.Count - 1));
            }
        }

        /// <summary>Push the classroom's published/locked quizzes into the Available Quizzes tab.</summary>
        public void SetQuizzes(List<QuizCardInfo> quizzes)
        {
            _lastQuizzes.Clear();
            if (quizzes != null) _lastQuizzes.AddRange(quizzes);

            bool hasQuizzes = _lastQuizzes.Count > 0;
            _quizzesEmptyState?.EnableInClassList("hidden", hasQuizzes);
            _quizzesList?.EnableInClassList("hidden", !hasQuizzes);

            if (_quizzesList == null) return;
            _quizzesList.Clear();
            if (!hasQuizzes) return;

            foreach (var quiz in _lastQuizzes)
            {
                _quizzesList.Add(BuildQuizCard(quiz));
            }
        }

        /// <summary>
        /// Push the leaderboard. If <paramref name="visibleToStudents"/> is false
        /// (the teacher hasn't turned it on via AdminClassroomDetailController's
        /// "Show to Students" toggle), a locked state is shown instead.
        /// </summary>
        public void SetLeaderboard(bool visibleToStudents, List<PerformerInfo> rankedStudents)
        {
            _leaderboardVisible = visibleToStudents;
            _lastLeaderboard.Clear();
            if (rankedStudents != null) _lastLeaderboard.AddRange(rankedStudents);

            _leaderboardLockedState?.EnableInClassList("hidden", visibleToStudents);
            _leaderboardCard?.EnableInClassList("hidden", !visibleToStudents);

            if (!visibleToStudents || _leaderboardList == null) return;

            bool hasData = _lastLeaderboard.Count > 0;
            _leaderboardEmptyState?.EnableInClassList("hidden", hasData);
            _leaderboardList.EnableInClassList("hidden", !hasData);

            _leaderboardList.Clear();
            for (int i = 0; i < _lastLeaderboard.Count; i++)
            {
                _leaderboardList.Add(BuildPerformerRow(i + 1, _lastLeaderboard[i]));
            }
        }

        /// <summary>Push this student's completed-quiz history for this classroom into the Scores tab.</summary>
        public void SetScores(List<ScoreHistoryInfo> scores)
        {
            _lastScores.Clear();
            if (scores != null) _lastScores.AddRange(scores);

            bool hasScores = _lastScores.Count > 0;
            _scoresEmptyState?.EnableInClassList("hidden", hasScores);
            _scoresList?.EnableInClassList("hidden", !hasScores);

            if (_scoresList == null) return;
            _scoresList.Clear();
            if (!hasScores) return;

            foreach (var score in _lastScores)
            {
                _scoresList.Add(BuildScoreCard(score));
            }
        }

        /// <summary>Push this classroom's teacher-configured badges into the Badges tab
        /// (empty state if the teacher hasn't set any up, or the list is null).</summary>
        public void SetBadges(int currentPoints, List<BadgeInfo> badges)
        {
            _lastBadgePoints = currentPoints;
            _lastBadges.Clear();
            if (badges != null) _lastBadges.AddRange(badges);

            bool hasBadges = _lastBadges.Count > 0;
            _badgesEmptyState?.EnableInClassList("hidden", hasBadges);
            _badgesList?.EnableInClassList("hidden", !hasBadges);

            if (_badgesList == null) return;
            _badgesList.Clear();
            if (!hasBadges) return;

            foreach (var badge in _lastBadges)
            {
                _badgesList.Add(BuildBadgeCard(badge, currentPoints));
            }
        }

        // ---------------- Row builders (built at runtime - lists are dynamic) ----------------

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

            var bodyLabel = new Label(announcement.Body);
            bodyLabel.AddToClassList("announcement-body-label");

            card.Add(headerRow);
            card.Add(bodyLabel);
            return card;
        }

        private VisualElement BuildPeerRow(PeerInfo peer, bool isLast)
        {
            var row = new VisualElement();
            row.AddToClassList("peer-row");
            if (isLast) row.AddToClassList("peer-row-last");

            var avatar = new VisualElement();
            avatar.AddToClassList("peer-avatar");
            var initialsLabel = new Label(GetInitials(peer.Name));
            initialsLabel.AddToClassList("peer-avatar-label");
            avatar.Add(initialsLabel);

            var nameLabel = new Label(peer.Name);
            nameLabel.AddToClassList("peer-name-label");

            var levelBadge = new Label($"Lvl {peer.Level}");
            levelBadge.AddToClassList("peer-level-badge");

            row.Add(avatar);
            row.Add(nameLabel);
            row.Add(levelBadge);
            return row;
        }

        private VisualElement BuildQuizCard(QuizCardInfo quiz)
        {
            var card = new VisualElement();
            card.AddToClassList("quiz-card");
            if (!quiz.IsAvailable) card.AddToClassList("quiz-card-locked");

            // Eyebrow row: subject tag on the left, availability pill on the right -
            // read before the title so the card sorts/scans by subject at a glance.
            var headerRow = new VisualElement();
            headerRow.AddToClassList("quiz-card-header-row");

            var subjectTag = new Label(quiz.Subject);
            subjectTag.AddToClassList("quiz-subject-tag");
            if (!quiz.IsAvailable) subjectTag.AddToClassList("quiz-subject-tag-locked");

            var statusPill = new Label(quiz.IsAvailable ? "Available" : "Locked");
            statusPill.AddToClassList("quiz-status-pill");
            if (!quiz.IsAvailable) statusPill.AddToClassList("quiz-status-pill-locked");

            headerRow.Add(subjectTag);
            headerRow.Add(statusPill);
            card.Add(headerRow);

            var titleLabel = new Label(quiz.Title);
            titleLabel.AddToClassList("quiz-card-title");
            card.Add(titleLabel);

            // A single wrapping row of stat chips replaces the old 3-row key/value
            // grid - same info (questions, points, time, difficulty, attempts,
            // deadline) but scannable in one glance instead of six lines.
            var metaChips = new VisualElement();
            metaChips.AddToClassList("quiz-meta-chips");

            metaChips.Add(BuildMetaChip(quiz.Questions == 1 ? "1 Question" : $"{quiz.Questions} Questions"));
            metaChips.Add(BuildMetaChip($"{quiz.Points} pts"));
            metaChips.Add(BuildMetaChip(quiz.HasTimeLimit ? $"{quiz.TimeMinutes} mins" : "No time limit"));
            metaChips.Add(BuildMetaChip(quiz.MaxAttempts > 0
                ? (quiz.MaxAttempts == 1 ? "1 attempt" : $"{quiz.MaxAttempts} attempts")
                : "Unlimited attempts"));

            if (quiz.IsDeadlineEnabled && quiz.DeadlineUtc.HasValue)
            {
                metaChips.Add(BuildMetaChip($"Due {quiz.DeadlineUtc.Value.ToLocalTime():MMM d, h:mm tt}"));
            }

            if (!string.IsNullOrEmpty(quiz.Difficulty))
            {
                var difficultyChip = BuildMetaChip(quiz.Difficulty);
                difficultyChip.AddToClassList(GetDifficultyChipClass(quiz.Difficulty));
                metaChips.Add(difficultyChip);
            }

            card.Add(metaChips);

            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("quiz-card-bottom-row");

            if (quiz.IsAvailable)
            {
                bool blocked = quiz.StartBlock != QuizStartBlock.None;

                var startButton = new Button(blocked ? (Action)null : () => OnStartQuizClicked(quiz));
                startButton.AddToClassList("quiz-start-button");

                var playIcon = new VisualElement();
                playIcon.AddToClassList("quiz-start-play-icon");
                startButton.Add(playIcon);

                string startLabelText;
                switch (quiz.StartBlock)
                {
                    case QuizStartBlock.DeadlineExpired:
                        startLabelText = "Deadline Expired";
                        break;
                    case QuizStartBlock.NoAttemptsLeft:
                        startLabelText = "No More Attempts";
                        break;
                    default:
                        startLabelText = "Start Quiz";
                        break;
                }
                startButton.Add(new Label(startLabelText));

                if (blocked)
                {
                    // Grayed-out and non-interactive - no popup/dialog, the button itself
                    // is the message. SetEnabled(false) both blocks clicks (belt-and-suspenders
                    // alongside the null click handler above) and applies Unity's built-in
                    // :disabled USS pseudo-class; quiz-start-button-disabled covers the look.
                    startButton.SetEnabled(false);
                    startButton.AddToClassList("quiz-start-button-disabled");
                }

                bottomRow.Add(startButton);
            }
            else
            {
                var lockedBadge = new Label("Locked");
                lockedBadge.AddToClassList("quiz-locked-badge");
                bottomRow.Add(lockedBadge);
            }

            card.Add(bottomRow);
            return card;
        }

        private VisualElement BuildMetaChip(string text)
        {
            var chip = new Label(text);
            chip.AddToClassList("quiz-meta-chip");
            return chip;
        }

        /// <summary>Maps a free-text difficulty (Easy/Medium/Hard, as set in the admin
        /// form) to a color-coded chip variant. Anything else falls back to the
        /// neutral chip style rather than guessing.</summary>
        private static string GetDifficultyChipClass(string difficulty)
        {
            switch (difficulty.Trim().ToLowerInvariant())
            {
                case "easy": return "quiz-chip-easy";
                case "medium": return "quiz-chip-medium";
                case "hard": return "quiz-chip-hard";
                default: return "quiz-chip-neutral";
            }
        }

        private VisualElement BuildPerformerRow(int rank, PerformerInfo performer)
        {
            var row = new VisualElement();
            row.AddToClassList("performer-row");
            if (rank == 1) row.AddToClassList("performer-row-gold");
            else if (rank == 2) row.AddToClassList("performer-row-silver");
            else if (rank == 3) row.AddToClassList("performer-row-bronze");
            if (performer.IsCurrentStudent) row.AddToClassList("performer-row-you");

            var badge = new VisualElement();
            badge.AddToClassList("performer-rank-badge");
            if (rank == 1) badge.AddToClassList("performer-rank-badge-gold");
            else if (rank == 2) badge.AddToClassList("performer-rank-badge-silver");
            else if (rank == 3) badge.AddToClassList("performer-rank-badge-bronze");
            var rankLabel = new Label($"#{rank}");
            rankLabel.AddToClassList("performer-rank-label");
            badge.Add(rankLabel);

            var avatar = new VisualElement();
            avatar.AddToClassList("performer-avatar");
            var initialsLabel = new Label(GetInitials(performer.Name));
            initialsLabel.AddToClassList("performer-avatar-label");
            avatar.Add(initialsLabel);

            var info = new VisualElement();
            info.AddToClassList("performer-info");

            var nameRow = new VisualElement();
            nameRow.AddToClassList("performer-name-row");
            var nameLabel = new Label(performer.Name);
            nameLabel.AddToClassList("performer-name-label");
            nameRow.Add(nameLabel);
            if (performer.IsCurrentStudent)
            {
                var youBadge = new Label("YOU");
                youBadge.AddToClassList("performer-you-badge");
                nameRow.Add(youBadge);
            }

            var metaLabel = new Label($"Lvl {performer.Level} \u2022 {performer.QuizzesCompleted} quizzes");
            metaLabel.AddToClassList("performer-meta-label");

            info.Add(nameRow);
            info.Add(metaLabel);

            var scoreBlock = new VisualElement();
            scoreBlock.AddToClassList("performer-score-block");
            var percentLabel = new Label($"{Mathf.RoundToInt(performer.ScorePercent)}%");
            percentLabel.AddToClassList("performer-score-percent");
            var pointsLabel = new Label($"{performer.Points:N0} pts");
            pointsLabel.AddToClassList("performer-points-sub");
            scoreBlock.Add(percentLabel);
            scoreBlock.Add(pointsLabel);

            row.Add(badge);
            row.Add(avatar);
            row.Add(info);
            row.Add(scoreBlock);
            return row;
        }

        private VisualElement BuildScoreCard(ScoreHistoryInfo score)
        {
            var card = new VisualElement();
            card.AddToClassList("score-card");

            var topRow = new VisualElement();
            topRow.AddToClassList("score-top-row");

            var titleBlock = new VisualElement();
            titleBlock.AddToClassList("score-title-block");
            var titleLabel = new Label(score.QuizTitle);
            titleLabel.AddToClassList("score-title-label");
            var completedLabel = new Label($"Completed: {score.CompletedDateText}");
            completedLabel.AddToClassList("score-completed-label");
            titleBlock.Add(titleLabel);
            titleBlock.Add(completedLabel);

            var statusPill = new Label(score.Passed ? "Passed" : "Failed");
            statusPill.AddToClassList("score-status-pill");
            statusPill.AddToClassList(score.Passed ? "score-status-pill-passed" : "score-status-pill-failed");

            topRow.Add(titleBlock);
            topRow.Add(statusPill);
            card.Add(topRow);

            var midRow = new VisualElement();
            midRow.AddToClassList("score-mid-row");
            var fractionLabel = new Label($"Score: {score.ScoreCorrect} / {score.ScoreTotal}");
            fractionLabel.AddToClassList("score-fraction-label");
            float percent = score.ScoreTotal > 0 ? (score.ScoreCorrect / (float)score.ScoreTotal) * 100f : 0f;
            var percentLabel = new Label($"{Mathf.RoundToInt(percent)}%");
            percentLabel.AddToClassList("score-percent-label");
            percentLabel.AddToClassList(score.Passed ? "score-percent-label-passed" : "score-percent-label-failed");
            midRow.Add(fractionLabel);
            midRow.Add(percentLabel);
            card.Add(midRow);

            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("score-bottom-row");
            var timeLabel = new Label($"Time: {score.TimeText}");
            timeLabel.AddToClassList("score-meta-label");
            var attemptLabel = new Label($"Attempt: {score.Attempt}");
            attemptLabel.AddToClassList("score-meta-label");
            bottomRow.Add(timeLabel);
            bottomRow.Add(attemptLabel);
            card.Add(bottomRow);

            return card;
        }

        private VisualElement BuildBadgeCard(BadgeInfo badge, int currentPoints)
        {
            var card = new VisualElement();
            card.AddToClassList("badge-card");
            if (!badge.Earned) card.AddToClassList("badge-card-locked");

            var iconBox = new VisualElement();
            iconBox.AddToClassList("badge-icon-box");
            iconBox.AddToClassList(badge.Earned ? "badge-icon-gold" : "badge-icon-locked");

            if (badge.Earned)
            {
                var emojiLabel = new Label(string.IsNullOrEmpty(badge.IconEmoji) ? "\U0001F3C6" : badge.IconEmoji);
                emojiLabel.AddToClassList("badge-icon-emoji");
                iconBox.Add(emojiLabel);
            }
            else
            {
                var lockIcon = new VisualElement();
                lockIcon.AddToClassList("badge-lock-icon");
                iconBox.Add(lockIcon);
            }

            var info = new VisualElement();
            info.AddToClassList("badge-info");

            var titleLabel = new Label(badge.Name);
            titleLabel.AddToClassList("badge-title");
            if (!badge.Earned) titleLabel.AddToClassList("badge-title-locked");
            info.Add(titleLabel);

            var statusLabel = new Label(badge.Earned ? "Unlocked!" : $"Earn {badge.PointsRequired:N0} points to unlock");
            statusLabel.AddToClassList("badge-status");
            statusLabel.AddToClassList(badge.Earned ? "badge-status-unlocked" : "badge-status-locked");
            info.Add(statusLabel);

            var progressRow = new VisualElement();
            progressRow.AddToClassList("badge-progress-row");

            var track = new VisualElement();
            track.AddToClassList("progress-track");

            float pct = badge.Earned
                ? 1f
                : (badge.PointsRequired > 0 ? Mathf.Clamp01((float)currentPoints / badge.PointsRequired) : 0f);

            var fill = new VisualElement();
            fill.AddToClassList("progress-fill");
            fill.AddToClassList(badge.Earned ? "progress-fill-complete" : "progress-fill-locked");
            fill.style.width = new Length(pct * 100f, LengthUnit.Percent);
            track.Add(fill);
            progressRow.Add(track);

            if (badge.Earned)
            {
                var completeLabel = new Label("\u2713 Completed");
                completeLabel.AddToClassList("progress-label");
                completeLabel.AddToClassList("progress-label-complete");
                progressRow.Add(completeLabel);
            }

            info.Add(progressRow);

            if (!badge.Earned)
            {
                var footerRow = new VisualElement();
                footerRow.AddToClassList("badge-progress-footer-row");

                var countLabel = new Label($"{Mathf.Min(currentPoints, badge.PointsRequired):N0} / {badge.PointsRequired:N0}");
                countLabel.AddToClassList("progress-footer-label");

                var percentLabel = new Label($"{Mathf.RoundToInt(pct * 100f)}%");
                percentLabel.AddToClassList("progress-footer-label");
                percentLabel.AddToClassList("progress-footer-label-right");

                footerRow.Add(countLabel);
                footerRow.Add(percentLabel);
                info.Add(footerRow);
            }

            card.Add(iconBox);
            card.Add(info);
            return card;
        }

        private static string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "?";
            var parts = fullName.Trim().Split(' ');
            if (parts.Length == 1) return parts[0].Substring(0, Mathf.Min(2, parts[0].Length)).ToUpper();
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentClassroomDetailController] Navigating back to classroom hub");
            UIManager.Instance.ShowStudentClassroomHub();
        }

        private void OnArchivedBlockedBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentClassroomDetailController] Archived classroom - returning to classroom hub");
            UIManager.Instance.ShowStudentClassroomHub();
        }

        // ---------------- Archived-blocked state ----------------

        /// <summary>Hides the classroom's content (screen-scroll) and shows the
        /// archived-blocked message instead. Called from LoadClassroomContent() when
        /// ClassroomDetailRecord.IsArchived comes back true - no tab content is fetched
        /// or rendered for an archived classroom.</summary>
        private void ShowArchivedBlockedState()
        {
            _screenScroll?.AddToClassList("hidden");
            _archivedBlockedPanel?.RemoveFromClassList("hidden");
        }

        /// <summary>Reverts ShowArchivedBlockedState() - called at the start of every fresh
        /// load (SetClassroomIdentity/LoadClassroomContent) so a previously-archived
        /// classroom's block doesn't linger over a new, non-archived classroom in this
        /// reused screen.</summary>
        private void HideArchivedBlockedState()
        {
            _archivedBlockedPanel?.AddToClassList("hidden");
            _screenScroll?.RemoveFromClassList("hidden");
        }

        private void OnOverviewTabClicked(ClickEvent evt) => ShowOverviewTab();
        private void OnStudentsTabClicked(ClickEvent evt) => ShowStudentsTab();
        private void OnQuizzesTabClicked(ClickEvent evt) => ShowQuizzesTab();
        private void OnLeaderboardTabClicked(ClickEvent evt) => ShowLeaderboardTab();
        private void OnScoresTabClicked(ClickEvent evt) => ShowScoresTab();
        private void OnBadgesTabClicked(ClickEvent evt) => ShowBadgesTab();

        private void ShowOverviewTab() => SetActiveTab(_overviewTabButton, _overviewPanel, "Overview");
        private void ShowStudentsTab() => SetActiveTab(_studentsTabButton, _studentsPanel, "Students");
        private void ShowQuizzesTab() => SetActiveTab(_quizzesTabButton, _quizzesPanel, "Available Quizzes");
        private void ShowLeaderboardTab() => SetActiveTab(_leaderboardTabButton, _leaderboardPanel, "Leaderboard");
        private void ShowScoresTab() => SetActiveTab(_scoresTabButton, _scoresPanel, "Scores");
        private void ShowBadgesTab() => SetActiveTab(_badgesTabButton, _badgesPanel, "Badges");

        private void SetActiveTab(Button activeButton, VisualElement activePanel, string tabName)
        {
            Debug.Log($"[StudentClassroomDetailController] Switching to tab: {tabName} (button found: {activeButton != null}, panel found: {activePanel != null})");

            _overviewTabButton?.RemoveFromClassList("tab-button-active");
            _studentsTabButton?.RemoveFromClassList("tab-button-active");
            _quizzesTabButton?.RemoveFromClassList("tab-button-active");
            _leaderboardTabButton?.RemoveFromClassList("tab-button-active");
            _scoresTabButton?.RemoveFromClassList("tab-button-active");
            _badgesTabButton?.RemoveFromClassList("tab-button-active");
            activeButton?.AddToClassList("tab-button-active");

            _overviewPanel?.AddToClassList("hidden");
            _studentsPanel?.AddToClassList("hidden");
            _quizzesPanel?.AddToClassList("hidden");
            _leaderboardPanel?.AddToClassList("hidden");
            _scoresPanel?.AddToClassList("hidden");
            _badgesPanel?.AddToClassList("hidden");
            activePanel?.RemoveFromClassList("hidden");
        }

        private void OnStartQuizClicked(QuizCardInfo quiz)
        {
            Debug.Log($"[StudentClassroomDetailController] Start Quiz tapped: {quiz.Title} (classroom {_classroomId})");

            if (string.IsNullOrEmpty(quiz.QuizId))
            {
                Debug.LogError($"[StudentClassroomDetailController] '{quiz.Title}' has no quiz id - can't start it.");
                return;
            }

            // BuildQuizCard() already disables Start (and relabels it "Deadline Expired" /
            // "No More Attempts") once QuizStartBlock != None, so this handler only ever
            // fires for a quiz this student was eligible for at load time. The real
            // enforcement point remains the gameplay screen itself
            // (StudentQuizGameplayController.LoadQuiz -> QuizService.CheckAttemptEligibility),
            // which re-checks fresh in case the deadline passed or another attempt was
            // used since this tab loaded - it shows the blocking message there if so,
            // rather than trusting this now-stale card state.
            UIManager.Instance.ShowStudentQuizGameplay(_classroomId, quiz.QuizId);
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
                name = "StudentClassroomDetailGradientTexture"
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