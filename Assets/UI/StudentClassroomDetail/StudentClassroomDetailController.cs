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
    /// they pick from). Five tabs:
    ///  - Overview: classroom info + teacher-posted announcements (see
    ///    AdminClassroomDetailController's Announcements tab, which is where a
    ///    teacher creates the entries pushed into SetAnnouncements() here)
    ///  - Students: read-only roster of classmates
    ///  - Available Quizzes: quizzes the teacher published to this classroom,
    ///    with a Start button (locked ones show a "Locked" badge instead)
    ///  - Leaderboard: ranked by score, gated by the teacher's "Show to
    ///    Students" toggle (AdminClassroomDetailController.LeaderboardVisibleToStudents)
    ///  - Scores: this student's own completed-quiz history for this classroom
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

        // Tabs
        private Button _overviewTabButton;
        private Button _studentsTabButton;
        private Button _quizzesTabButton;
        private Button _leaderboardTabButton;
        private Button _scoresTabButton;
        private VisualElement _overviewPanel;
        private VisualElement _studentsPanel;
        private VisualElement _quizzesPanel;
        private VisualElement _leaderboardPanel;
        private VisualElement _scoresPanel;

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

        /// <summary>A single card in the Available Quizzes tab.</summary>
        public struct QuizCardInfo
        {
            public string Title;
            public string Subject;
            public int Questions;
            public int TimeMinutes;
            public int Points;
            public string Difficulty;
            public bool IsAvailable;

            public QuizCardInfo(string title, string subject, int questions, int timeMinutes, int points, string difficulty, bool isAvailable)
            {
                Title = title;
                Subject = subject;
                Questions = questions;
                TimeMinutes = timeMinutes;
                Points = points;
                Difficulty = difficulty;
                IsAvailable = isAvailable;
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

        // Cached state so it survives the UIManager's clear-and-rebuild screen transitions.
        private string _classroomId = "";
        private string _classroomName = "";
        private string _instructorName = "";
        private readonly List<AnnouncementInfo> _lastAnnouncements = new();
        private readonly List<PeerInfo> _lastPeers = new();
        private readonly List<QuizCardInfo> _lastQuizzes = new();
        private bool _leaderboardVisible;
        private readonly List<PerformerInfo> _lastLeaderboard = new();
        private readonly List<ScoreHistoryInfo> _lastScores = new();

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

            _overviewTabButton?.UnregisterCallback<ClickEvent>(OnOverviewTabClicked);
            _studentsTabButton?.UnregisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.UnregisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _leaderboardTabButton?.UnregisterCallback<ClickEvent>(OnLeaderboardTabClicked);
            _scoresTabButton?.UnregisterCallback<ClickEvent>(OnScoresTabClicked);

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
            _overviewPanel = _screenRoot.Q<VisualElement>("overview-panel");
            _studentsPanel = _screenRoot.Q<VisualElement>("students-panel");
            _quizzesPanel = _screenRoot.Q<VisualElement>("quizzes-panel");
            _leaderboardPanel = _screenRoot.Q<VisualElement>("leaderboard-panel");
            _scoresPanel = _screenRoot.Q<VisualElement>("scores-panel");

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

            Debug.Log($"[StudentClassroomDetailController] Found tabs: {_overviewTabButton != null}/{_studentsTabButton != null}/{_quizzesTabButton != null}/{_leaderboardTabButton != null}/{_scoresTabButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);

            _overviewTabButton?.RegisterCallback<ClickEvent>(OnOverviewTabClicked);
            _studentsTabButton?.RegisterCallback<ClickEvent>(OnStudentsTabClicked);
            _quizzesTabButton?.RegisterCallback<ClickEvent>(OnQuizzesTabClicked);
            _leaderboardTabButton?.RegisterCallback<ClickEvent>(OnLeaderboardTabClicked);
            _scoresTabButton?.RegisterCallback<ClickEvent>(OnScoresTabClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Called by UIManager.ShowStudentClassroomDetail() right after the screen is shown.</summary>
        public void SetClassroomIdentity(string classroomId, string classroomName, string instructorName)
        {
            _classroomId = classroomId ?? "";
            _classroomName = classroomName ?? "";
            _instructorName = instructorName ?? "";

            if (_classroomNameLabel != null) _classroomNameLabel.text = _classroomName;
            if (_instructorLabel != null) _instructorLabel.text = $"Instructor: {_instructorName}";
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

            var topRow = new VisualElement();
            topRow.AddToClassList("quiz-card-top-row");
            var titleLabel = new Label(quiz.Title);
            titleLabel.AddToClassList("quiz-card-title");
            var statusPill = new Label(quiz.IsAvailable ? "Available" : "Locked");
            statusPill.AddToClassList("quiz-status-pill");
            if (!quiz.IsAvailable) statusPill.AddToClassList("quiz-status-pill-locked");
            topRow.Add(titleLabel);
            topRow.Add(statusPill);
            card.Add(topRow);

            var subjectLabel = new Label(quiz.Subject);
            subjectLabel.AddToClassList("quiz-card-subject");
            card.Add(subjectLabel);

            var metaGrid = new VisualElement();
            metaGrid.AddToClassList("quiz-meta-grid");

            var leftColumn = new VisualElement();
            leftColumn.AddToClassList("quiz-meta-column");
            leftColumn.Add(BuildMetaRow("Questions:", quiz.Questions.ToString()));
            leftColumn.Add(BuildMetaRow("Points:", quiz.Points.ToString()));

            var rightColumn = new VisualElement();
            rightColumn.AddToClassList("quiz-meta-column");
            rightColumn.Add(BuildMetaRow("Time:", $"{quiz.TimeMinutes} mins"));
            rightColumn.Add(BuildMetaRow("Difficulty:", quiz.Difficulty));

            metaGrid.Add(leftColumn);
            metaGrid.Add(rightColumn);
            card.Add(metaGrid);

            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("quiz-card-bottom-row");

            if (quiz.IsAvailable)
            {
                var startButton = new Button(() => OnStartQuizClicked(quiz));
                startButton.AddToClassList("quiz-start-button");
                var playIcon = new VisualElement();
                playIcon.AddToClassList("quiz-start-play-icon");
                var startLabel = new Label("Start Quiz");
                startButton.Add(playIcon);
                startButton.Add(startLabel);
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

        private VisualElement BuildMetaRow(string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("quiz-meta-row");
            var keyLabel = new Label(key);
            keyLabel.AddToClassList("quiz-meta-key");
            var valueLabel = new Label(value);
            valueLabel.AddToClassList("quiz-meta-value");
            row.Add(keyLabel);
            row.Add(valueLabel);
            return row;
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

        private void OnOverviewTabClicked(ClickEvent evt) => ShowOverviewTab();
        private void OnStudentsTabClicked(ClickEvent evt) => ShowStudentsTab();
        private void OnQuizzesTabClicked(ClickEvent evt) => ShowQuizzesTab();
        private void OnLeaderboardTabClicked(ClickEvent evt) => ShowLeaderboardTab();
        private void OnScoresTabClicked(ClickEvent evt) => ShowScoresTab();

        private void ShowOverviewTab() => SetActiveTab(_overviewTabButton, _overviewPanel, "Overview");
        private void ShowStudentsTab() => SetActiveTab(_studentsTabButton, _studentsPanel, "Students");
        private void ShowQuizzesTab() => SetActiveTab(_quizzesTabButton, _quizzesPanel, "Available Quizzes");
        private void ShowLeaderboardTab() => SetActiveTab(_leaderboardTabButton, _leaderboardPanel, "Leaderboard");
        private void ShowScoresTab() => SetActiveTab(_scoresTabButton, _scoresPanel, "Scores");

        private void SetActiveTab(Button activeButton, VisualElement activePanel, string tabName)
        {
            Debug.Log($"[StudentClassroomDetailController] Switching to tab: {tabName} (button found: {activeButton != null}, panel found: {activePanel != null})");

            _overviewTabButton?.RemoveFromClassList("tab-button-active");
            _studentsTabButton?.RemoveFromClassList("tab-button-active");
            _quizzesTabButton?.RemoveFromClassList("tab-button-active");
            _leaderboardTabButton?.RemoveFromClassList("tab-button-active");
            _scoresTabButton?.RemoveFromClassList("tab-button-active");
            activeButton?.AddToClassList("tab-button-active");

            _overviewPanel?.AddToClassList("hidden");
            _studentsPanel?.AddToClassList("hidden");
            _quizzesPanel?.AddToClassList("hidden");
            _leaderboardPanel?.AddToClassList("hidden");
            _scoresPanel?.AddToClassList("hidden");
            activePanel?.RemoveFromClassList("hidden");
        }

        private void OnStartQuizClicked(QuizCardInfo quiz)
        {
            Debug.Log($"[StudentClassroomDetailController] Start Quiz tapped: {quiz.Title} (classroom {_classroomId})");

            // TODO: replace with your real quiz-launch call scoped to this classroom, e.g.:
            // QuizManager.Instance.StartQuiz(_classroomId, quiz.Title);
            UIManager.Instance.ShowStudentQuizSelection();
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