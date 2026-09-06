using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminAnalyticsReports.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the Excel / PDF export buttons
    ///  - Drives the "Performance / Students / Mistakes" segmented tab control
    ///    (only one panel visible at a time, matching the mock)
    ///  - Populates the four overview stat cards (Active Users, Avg Score,
    ///    Quizzes Done, Completion)
    ///  - Builds the Top Performers list, Student Activity summary, Common
    ///    Incorrect Answers list and Recommendations list at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetOverviewStats() / SetTopPerformers() / SetStudentActivity()
    ///    / SetTopicPerformance() / SetScoreTrend() / SetCommonMistakes() /
    ///    SetRecommendations() so real analytics data can be pushed in instead
    ///    of the placeholder mock data below.
    ///
    /// Hook up your real reporting/export calls inside OnExportExcelClicked()
    /// and OnExportPdfClicked().
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminAnalyticsReportsController : MonoBehaviour
    {
        private const string TabPerformance = "performance";
        private const string TabStudents = "students";
        private const string TabMistakes = "mistakes";

        /// <summary>Plain data for a single row in the "Top Performers" list.</summary>
        public struct TopPerformer
        {
            public string Name;
            public int QuizzesCompleted;
            public int Points;
            public int Level;

            public TopPerformer(string name, int quizzesCompleted, int points, int level)
            {
                Name = name;
                QuizzesCompleted = quizzesCompleted;
                Points = points;
                Level = level;
            }
        }

        /// <summary>Plain data for the "Student Activity" summary card.</summary>
        public struct StudentActivitySummary
        {
            public int TotalStudents;
            public int ActiveThisMonth;
            public float AverageLevel;
            public int AveragePoints;

            public StudentActivitySummary(int totalStudents, int activeThisMonth, float averageLevel, int averagePoints)
            {
                TotalStudents = totalStudents;
                ActiveThisMonth = activeThisMonth;
                AverageLevel = averageLevel;
                AveragePoints = averagePoints;
            }
        }

        /// <summary>Plain data for a single row in the "Common Incorrect Answers" list.</summary>
        public struct MistakeEntry
        {
            public string Question;
            public string Category;
            public int Errors;

            public MistakeEntry(string question, string category, int errors)
            {
                Question = question;
                Category = category;
                Errors = errors;
            }
        }

        /// <summary>Plain data for a single "Recommendations" item.</summary>
        public struct RecommendationEntry
        {
            public string Title;
            public string Description;

            /// <summary>The topic/category this recommendation is about (e.g. "Skeletal
            /// System"). Populated by BuildRecommendations so downstream consumers (like
            /// AdminReportExportService) have the real topic without having to guess it
            /// out of Title, or index into unrelated lists like ScoreTrend.</summary>
            public string Topic;

            public RecommendationEntry(string title, string description, string topic = null)
            {
                Title = title;
                Description = description;
                Topic = topic;
            }
        }

        /// <summary>Plain data for a single row in the "Score Trend" list (0-100).</summary>
        public struct ScoreTrendEntry
        {
            public string Label;
            public float ScorePercent;

            public ScoreTrendEntry(string label, float scorePercent)
            {
                Label = label;
                ScorePercent = scorePercent;
            }
        }

        /// <summary>Plain data for a single row in the "Performance by Topic" list (0-100).</summary>
        public struct TopicPerformanceEntry
        {
            public string Topic;
            public float ScorePercent;

            public TopicPerformanceEntry(string topic, float scorePercent)
            {
                Topic = topic;
                ScorePercent = scorePercent;
            }
        }

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Button _backButton;
      
        private Button _exportPdfButton;

        private Label _activeUsersValueLabel;
        private Label _avgScoreValueLabel;
        private Label _quizzesDoneValueLabel;
        private Label _completionValueLabel;

        private Button _performanceTabButton;
        private Button _studentsTabButton;
        private Button _mistakesTabButton;

        private VisualElement _performancePanel;
        private VisualElement _studentsPanel;
        private VisualElement _mistakesPanel;

        private VisualElement _scoreTrendList;

        private VisualElement _topPerformersList;

        private Label _totalStudentsValueLabel;
        private Label _activeThisMonthValueLabel;
        private Label _averageLevelValueLabel;
        private Label _averagePointsValueLabel;

        private VisualElement _commonMistakesList;
        private VisualElement _recommendationsList;

        private string _activeTab = TabStudents;

        // Classroom picker - lets the teacher pick which classroom this report covers
        // (AdminAnalyticsReportsController shows one classroom at a time, unlike
        // AdminClassroomDetailController which is already scoped to a classroomId).
        // Lives in #filters-row, immediately to the left of the quiz export picker.
        private DropdownField _classroomPicker;
        private List<AdminClassroomService.ClassroomRecord> _classrooms = new List<AdminClassroomService.ClassroomRecord>();
        private string _selectedClassroomId;

        // Quiz export picker - lets the teacher pick one quiz that is both scoped to
        // whichever classroom is selected above AND already published to it (see
        // LoadQuizzesForClassroom); the currently-selected quiz's per-student scores
        // are folded into the existing Excel/PDF export buttons as an extra "Quiz Scores"
        // section (see PrepareAndExport/BuildExportData) rather than needing an export
        // button of their own.
        private DropdownField _quizExportPicker;

        private List<QuizService.QuizRecord> _classroomQuizzes = new List<QuizService.QuizRecord>();
        private string _selectedQuizExportId;

        // Classroom roster for whichever classroom is selected - kept around so the quiz
        // export can list every student (including ones who never attempted the selected
        // quiz) rather than only the ones QuizService.FetchQuizScoresForClassroom finds
        // quizAttempts docs for. Populated by LoadAnalyticsFor, same call that already
        // fetches this classroom's ClassroomAnalytics for the Students tab.
        private List<AdminClassroomService.StudentStat> _currentClassroomStudents = new List<AdminClassroomService.StudentStat>();

        private List<ScoreTrendEntry> _currentScoreTrend = new List<ScoreTrendEntry>();
        private List<TopicPerformanceEntry> _currentTopicPerformance = new List<TopicPerformanceEntry>();
        private List<TopPerformer> _currentTopPerformers = new List<TopPerformer>();
        private StudentActivitySummary _currentStudentActivity;
        private List<MistakeEntry> _currentMistakes = new List<MistakeEntry>();
        private List<RecommendationEntry> _currentRecommendations = new List<RecommendationEntry>();

        // Raw numbers behind the four overview stat cards - SetOverviewStats() only
        // ever formatted these straight into label text, so ExportCsv()/ExportPdf()
        // (which need the numbers, not "82%") would otherwise have nothing to read.
        private int _currentActiveUsers;
        private string _currentActiveUsersDelta = "+0%";
        private float _currentAvgScorePercent;

        private int _currentQuizzesDone;
        private string _currentQuizzesDoneDelta = "No quiz selected";
        private int _currentCompletionPercent;
        private string _currentCompletionDelta = "No students enrolled";

        private void OnEnable()
        {
            Debug.Log("[AdminAnalyticsReportsController] OnEnable called");

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
                Debug.LogError("[AdminAnalyticsReportsController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            WireCallbacks();
            UpdateResponsiveLayout();

            _quizExportPicker?.SetEnabled(false);

            RefreshScoreTrendUI();
            RefreshTopPerformersUI();
            RefreshStudentActivityUI();
            RefreshMistakesUI();
            RefreshRecommendationsUI();
            SetActiveTab(_activeTab);

            LoadClassroomsAndData();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            // The screen's whole UXML tree gets re-instantiated on the next OnEnable
            // (QueryElements() re-queries "screen-root" from scratch), so the old
            // filters-row - and the picker we inserted into it - goes away with it.
            // Clear this so BuildClassroomPicker() rebuilds against the new tree
            // instead of skipping itself because _classroomPicker is still non-null.
            _classroomPicker = null;

            // Same reasoning as _classroomPicker above - the quiz export picker is a static
            // UXML element, but its choices/selection are runtime state tied to whichever
            // classroom was selected; start clean rather than carrying stale quiz choices
            // into a freshly re-instantiated tree.
            _classroomQuizzes.Clear();
            _selectedQuizExportId = null;
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            
            _exportPdfButton?.UnregisterCallback<ClickEvent>(OnExportPdfClicked);
            _performanceTabButton?.UnregisterCallback<ClickEvent>(OnPerformanceTabClicked);
            _studentsTabButton?.UnregisterCallback<ClickEvent>(OnStudentsTabClicked);
            _mistakesTabButton?.UnregisterCallback<ClickEvent>(OnMistakesTabClicked);
            _classroomPicker?.UnregisterValueChangedCallback(OnClassroomPickerChanged);
            _quizExportPicker?.UnregisterValueChangedCallback(OnQuizExportPickerChanged);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminAnalyticsReportsController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _backButton = _screenRoot.Q<Button>("back-button");
         
            _exportPdfButton = _screenRoot.Q<Button>("export-pdf-button");

            _activeUsersValueLabel = _screenRoot.Q<Label>("active-users-value-label");
            _avgScoreValueLabel = _screenRoot.Q<Label>("avg-score-value-label");
            _quizzesDoneValueLabel = _screenRoot.Q<Label>("quizzes-done-value-label");
            _completionValueLabel = _screenRoot.Q<Label>("completion-value-label");

            _performanceTabButton = _screenRoot.Q<Button>("performance-tab-button");
            _studentsTabButton = _screenRoot.Q<Button>("students-tab-button");
            _mistakesTabButton = _screenRoot.Q<Button>("mistakes-tab-button");

            _performancePanel = _screenRoot.Q<VisualElement>("performance-panel");
            _studentsPanel = _screenRoot.Q<VisualElement>("students-panel");
            _mistakesPanel = _screenRoot.Q<VisualElement>("mistakes-panel");

            _scoreTrendList = _screenRoot.Q<VisualElement>("score-trend-list");

            _topPerformersList = _screenRoot.Q<VisualElement>("top-performers-list");

            _totalStudentsValueLabel = _screenRoot.Q<Label>("total-students-value-label");
            _activeThisMonthValueLabel = _screenRoot.Q<Label>("active-this-month-value-label");
            _averageLevelValueLabel = _screenRoot.Q<Label>("average-level-value-label");
            _averagePointsValueLabel = _screenRoot.Q<Label>("average-points-value-label");

            _commonMistakesList = _screenRoot.Q<VisualElement>("common-mistakes-list");
            _recommendationsList = _screenRoot.Q<VisualElement>("recommendations-list");

            _quizExportPicker = _screenRoot.Q<DropdownField>("quiz-export-picker");

            Debug.Log($"[AdminAnalyticsReportsController] Found tabs row: {_performanceTabButton != null && _studentsTabButton != null && _mistakesTabButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
          
            _exportPdfButton?.RegisterCallback<ClickEvent>(OnExportPdfClicked);
            _performanceTabButton?.RegisterCallback<ClickEvent>(OnPerformanceTabClicked);
            _studentsTabButton?.RegisterCallback<ClickEvent>(OnStudentsTabClicked);
            _mistakesTabButton?.RegisterCallback<ClickEvent>(OnMistakesTabClicked);
            _quizExportPicker?.RegisterValueChangedCallback(OnQuizExportPickerChanged);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push the four overview stat cards. activeUsersDelta is shown as-is
        /// (includes the sign), e.g. "+12%". avgScorePercent has no delta anymore - it's a
        /// plain unweighted average across all enrolled students' scores, not a month-over-
        /// month comparison (see LoadAnalyticsFor). quizzesDoneDelta and completionDelta
        /// are also no longer month-over-month percents - they're roster-based summary
        /// strings, e.g. "8/10 students completed - "Skeletal System Quiz"" (see
        /// FormatQuizzesDoneSummary) and "8/10 students completed" (see
        /// FormatCompletionSummary), since quizzesDone/completionPercent themselves are now
        /// roster-based rather than a month-bucketed attempt count / passed-attempt rate.
        /// quizzesDone/quizzesDoneDelta specifically are kept in sync with the quiz export
        /// picker's selection by RefreshQuizzesDoneStat() rather than being pushed through
        /// this method directly from LoadAnalyticsFor - see its call sites.</summary>
        public void SetOverviewStats(int activeUsers, string activeUsersDelta, float avgScorePercent,
            int quizzesDone, string quizzesDoneDelta, int completionPercent, string completionDelta)
        {
            _currentActiveUsers = activeUsers;
            _currentActiveUsersDelta = activeUsersDelta;
            _currentAvgScorePercent = avgScorePercent;
            _currentQuizzesDone = quizzesDone;
            _currentQuizzesDoneDelta = quizzesDoneDelta;
            _currentCompletionPercent = completionPercent;
            _currentCompletionDelta = completionDelta;

            if (_activeUsersValueLabel != null) _activeUsersValueLabel.text = _currentStudentActivity.TotalStudents.ToString();
            if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{avgScorePercent:F2}%";
            if (_quizzesDoneValueLabel != null) _quizzesDoneValueLabel.text = quizzesDone.ToString();
            if (_completionValueLabel != null) _completionValueLabel.text = $"{completionPercent}%";
        }

        public void SetScoreTrend(List<ScoreTrendEntry> entries)
        {
            _currentScoreTrend = entries ?? new List<ScoreTrendEntry>();
            RefreshScoreTrendUI();
        }

        /// <summary>Stores per-topic scores for use in Excel/PDF exports (see BuildExportData)
        /// and recommendations (see BuildRecommendations) - there's no more Performance by
        /// Topic card in the UI to render this into.</summary>
        public void SetTopicPerformance(List<TopicPerformanceEntry> entries)
        {
            _currentTopicPerformance = entries ?? new List<TopicPerformanceEntry>();
        }

        public void SetTopPerformers(List<TopPerformer> performers)
        {
            _currentTopPerformers = performers ?? new List<TopPerformer>();
            RefreshTopPerformersUI();
        }

        public void SetStudentActivity(StudentActivitySummary summary)
        {
            _currentStudentActivity = summary;
            RefreshStudentActivityUI();
        }

        public void SetCommonMistakes(List<MistakeEntry> mistakes)
        {
            _currentMistakes = mistakes ?? new List<MistakeEntry>();
            RefreshMistakesUI();
        }

        public void SetRecommendations(List<RecommendationEntry> recommendations)
        {
            _currentRecommendations = recommendations ?? new List<RecommendationEntry>();
            RefreshRecommendationsUI();
        }

        // ---------------- Classroom picker + real data loading ----------------

        /// <summary>Loads the signed-in teacher's classrooms into the picker, then loads the
        /// report for whichever one is selected (defaults to the first). If the teacher has
        /// no classrooms yet, leaves the placeholder mock data in place instead.</summary>
        private void LoadClassroomsAndData()
        {
            if (AdminClassroomService.Instance == null)
            {
                Debug.LogWarning("[AdminAnalyticsReportsController] AdminClassroomService.Instance is null - " +
                    "leaving placeholder data in place.");
                return;
            }

            AdminClassroomService.Instance.FetchMyClassrooms(classrooms =>
            {
                _classrooms = classrooms ?? new List<AdminClassroomService.ClassroomRecord>();
                BuildClassroomPicker();

                if (_classrooms.Count == 0)
                {
                    Debug.Log("[AdminAnalyticsReportsController] No classrooms yet - showing placeholder data.");
                    return;
                }

                _selectedClassroomId = _classrooms[0].ClassroomId;
                LoadAnalyticsFor(_selectedClassroomId);
            });
        }

        /// <summary>Creates the classroom picker the first time this runs and inserts it into
        /// #filters-row, just before the quiz export picker (there's no dedicated element for
        /// it in the .uxml), then keeps its choices in sync with _classrooms on every
        /// subsequent call. Styled with the same .dropdown-field look used in
        /// AdminQuizManagement, rather than a bespoke pill.</summary>
        private void BuildClassroomPicker()
        {
            if (_screenRoot == null) return;

            if (_classroomPicker == null)
            {
                var filtersRow = _screenRoot.Q<VisualElement>("filters-row");
                if (filtersRow == null) return;

                _classroomPicker = new DropdownField();
                _classroomPicker.AddToClassList("dropdown-field");
                _classroomPicker.AddToClassList("classroom-picker-dropdown");
                _classroomPicker.RegisterValueChangedCallback(OnClassroomPickerChanged);

                filtersRow.Insert(0, _classroomPicker);
            }

            _classroomPicker.choices = _classrooms.Select(c => c.Name).ToList();

            if (_classrooms.Count > 0)
            {
                _classroomPicker.SetValueWithoutNotify(_classrooms[0].Name);
            }
        }

        private void OnClassroomPickerChanged(ChangeEvent<string> evt)
        {
            // Match by index rather than by Name - two classrooms can share a
            // display name (e.g. two sections both called "Grade 10"), and
            // matching on the string would silently resolve analytics to the
            // wrong classroom.
            int index = _classroomPicker.index;
            if (index < 0 || index >= _classrooms.Count) return;

            var match = _classrooms[index];
            _selectedClassroomId = match.ClassroomId;
            LoadAnalyticsFor(_selectedClassroomId);
        }

        /// <summary>Pulls real data for the given classroom from AdminClassroomService and
        /// QuizService and pushes it through the same Set*() public API a caller with its own
        /// data source would use - so this method doubles as a usage example.</summary>
        private void LoadAnalyticsFor(string classroomId)
        {
            if (string.IsNullOrEmpty(classroomId)) return;

            AdminClassroomService.Instance?.FetchClassroomAnalytics(classroomId, analytics =>
            {
                // Kept around (roster order, not the leaderboard's points-sorted order) so
                // the quiz export can list every student in the classroom, including ones
                // with no attempt at the selected quiz - see BuildQuizScoreExportRows().
                _currentClassroomStudents = analytics.Students ?? new List<AdminClassroomService.StudentStat>();

                SetTopPerformers(analytics.Leaderboard
                    .Take(10)
                    .Select(s => new TopPerformer(s.Name, s.QuizzesCompleted, s.Points, s.Level))
                    .ToList());

                QuizService.Instance?.FetchClassroomOverviewStats(classroomId, overview =>
                {
                    int totalStudents = analytics.Students.Count;
                    float avgLevel = totalStudents > 0 ? (float)analytics.Students.Average(s => s.Level) : 0f;
                    int avgPoints = totalStudents > 0 ? Mathf.RoundToInt((float)analytics.Students.Average(s => s.Points)) : 0;

                    SetStudentActivity(new StudentActivitySummary(totalStudents, overview.ActiveUsers, avgLevel, avgPoints));

                    // Completion is roster-driven rather than the month-bucketed "passed"
                    // rate QuizService.FetchClassroomOverviewStats reports: every student
                    // currently enrolled in this classroom must have completed at least one
                    // quiz before the classroom counts as fully complete. See
                    // CalculateClassroomCompletionPercent() below.
                    int completionPercent = CalculateClassroomCompletionPercent(analytics.Students);

                    // The old CompletionDeltaPercent ("+3% from last month") compared two
                    // month-bucketed "passed" rates, which no longer matches what
                    // completionPercent means now (roster completion, not a passed-attempt
                    // rate) - report the roster count behind the percentage instead, so the
                    // exported report's third column stays meaningful.
                    string completionSummary = FormatCompletionSummary(analytics.Students);

                    // Quizzes Done is no longer sourced from this month-bucketed overview -
                    // it's tied to whichever quiz is selected in the quiz export picker and
                    // kept current by RefreshQuizzesDoneStat() (called once the picker's
                    // default selection resolves, and again on every dropdown change), so
                    // pass through whatever it last computed rather than overwriting it here.
                    //
                    // Avg Score is a plain unweighted average of AvgScorePercent across
                    // students who have actually completed at least one quiz (QuizzesCompleted
                    // > 0) - students who haven't taken anything yet are excluded rather than
                    // counted as a 0% score. E.g. student A at 50%, B at 100%, C at 100%,
                    // and a 4th student with 0 quizzes completed -> average is still (50+100+
                    // 100)/3 = 83%, not /4. Not month-bucketed, and no month-over-month delta.
                    var scoredStudents = analytics.Students.Where(s => s.QuizzesCompleted > 0).ToList();

                   

                    float avgScorePercent = scoredStudents.Count > 0
                    ? scoredStudents.Average(s => s.AvgScorePercent)
                    : 0f;

                    SetOverviewStats(
                        overview.ActiveUsers, FormatDelta(overview.ActiveUsersDeltaPercent),
                        avgScorePercent,
                        _currentQuizzesDone, _currentQuizzesDoneDelta,
                        completionPercent, completionSummary);
                });
            });

            QuizService.Instance?.FetchClassroomReportData(classroomId, report =>
            {
                SetScoreTrend(report.ScoreTrend
                    .Select(q => new ScoreTrendEntry(q.QuizTitle, q.AvgScorePercent))
                    .ToList());

                SetTopicPerformance(report.TopicPerformance
                    .Select(c => new TopicPerformanceEntry(CapitalizeCategory(c.Category), c.AvgScorePercent))
                    .ToList());

                SetCommonMistakes(report.TopMistakes
                    .Select(m => new MistakeEntry(m.QuestionText, CapitalizeCategory(m.Category), m.ErrorCount))
                    .ToList());

                SetRecommendations(BuildRecommendations(report.TopicPerformance));
            });

            LoadQuizzesForClassroom(classroomId);
        }

        // ---------------- Quiz export picker ----------------

        /// <summary>Loads the quizzes selectable for this classroom's export picker: this
        /// admin's own quizzes that are both (a) scoped directly to classroomId or available
        /// to every classroom (QuizRecord.ClassroomId empty - see its doc comment), AND
        /// (b) actually published to classroomId - i.e. present in that classroom's
        /// PublishedQuizIds (set via AdminClassroomService.SetQuizPublished, the same flag
        /// ClassroomService.FetchAvailableQuizzes gates the student-facing list on). A quiz
        /// the teacher hasn't published yet has no scores worth exporting, so it's excluded
        /// here rather than only from the student-facing list. There's no "quizzes for one
        /// classroom" query on QuizService, so this reuses FetchMyQuizzes() (already used by
        /// Admin Quiz Management) and filters client-side, the same tradeoff BuildExportData()
        /// elsewhere in this class makes.</summary>
        private void LoadQuizzesForClassroom(string classroomId)
        {
            if (QuizService.Instance == null) return;

            // Read from _classrooms (already fetched by LoadClassroomsAndData/
            // BuildClassroomPicker) rather than issuing a fresh Firestore read just for
            // this list - see PublishedQuizIds' doc comment on AdminClassroomService.ClassroomRecord.
            var publishedQuizIds = _classrooms.FirstOrDefault(c => c.ClassroomId == classroomId)?.PublishedQuizIds
                ?? new List<string>();

            QuizService.Instance.FetchMyQuizzes(quizzes =>
            {
                _classroomQuizzes = (quizzes ?? new List<QuizService.QuizRecord>())
                    .Where(q => (string.IsNullOrEmpty(q.ClassroomId) || q.ClassroomId == classroomId)
                                && publishedQuizIds.Contains(q.QuizId))
                    .ToList();

                BuildQuizExportPicker();
            });
        }

        /// <summary>Keeps the quiz export picker's choices in sync with _classroomQuizzes
        /// (already filtered down to published-to-this-classroom quizzes by
        /// LoadQuizzesForClassroom) - called every time the classroom selection (and
        /// therefore the eligible quiz list) changes. Selection defaults to the first quiz,
        /// same convention BuildClassroomPicker() uses for the classroom picker. If the
        /// classroom has no published quizzes yet, the picker is simply disabled with no
        /// choices - leaving it with no valid quiz selected just means the next Excel/PDF
        /// export skips the "Quiz Scores" section - see FetchSelectedQuizScoreRows() -
        /// there's nothing else that depends on it.</summary>
        private void BuildQuizExportPicker()
        {
            if (_quizExportPicker == null) return;

            _quizExportPicker.choices = _classroomQuizzes.Select(q => q.Title).ToList();

            bool hasQuizzes = _classroomQuizzes.Count > 0;
            _quizExportPicker.SetEnabled(hasQuizzes);

            if (hasQuizzes)
            {
                _selectedQuizExportId = _classroomQuizzes[0].QuizId;
                _quizExportPicker.SetValueWithoutNotify(_classroomQuizzes[0].Title);
            }
            else
            {
                _selectedQuizExportId = null;
                _quizExportPicker.SetValueWithoutNotify(null);
            }

            // Show the default (first) quiz's "Quizzes Done" count as soon as the picker's
            // choices resolve - and reset it to "no quiz selected" if this classroom turns
            // out to have nothing published to it.
            RefreshQuizzesDoneStat();
        }

        private void OnQuizExportPickerChanged(ChangeEvent<string> evt)
        {
            // Match by index rather than by Title - two quizzes can share a display title,
            // and matching on the string would silently export the wrong quiz's scores.
            int index = _quizExportPicker.index;
            if (index < 0 || index >= _classroomQuizzes.Count) return;

            _selectedQuizExportId = _classroomQuizzes[index].QuizId;

            // Quizzes Done tracks whichever quiz is picked here, so it needs to update live
            // on every selection change, not just when the classroom first loads.
            RefreshQuizzesDoneStat();
        }

        /// <summary>Recomputes the "Quizzes Done" overview stat for whichever quiz is
        /// currently selected in the quiz export picker: the number of students in this
        /// classroom who have completed that specific quiz - not a raw attempt count, and
        /// not "any quiz". Reuses QuizService.FetchQuizScoresForClassroom (the same query
        /// FetchSelectedQuizScoreRows uses for the export), which is already scoped to one
        /// classroom + one quiz, excludes auto-recorded "Missed" placeholder docs, and
        /// collapses each student down to a single best attempt - so Attempted == true there
        /// means "this student completed this quiz in this classroom".
        ///
        /// Called once the quiz export picker's default selection resolves (so the card
        /// shows a real number as soon as the classroom loads) and again on every dropdown
        /// change (see BuildQuizExportPicker/OnQuizExportPickerChanged), rather than being
        /// folded into the month-bucketed QuizService.FetchClassroomOverviewStats call in
        /// LoadAnalyticsFor.</summary>
        private void RefreshQuizzesDoneStat()
        {
            if (string.IsNullOrEmpty(_selectedClassroomId) || string.IsNullOrEmpty(_selectedQuizExportId) || QuizService.Instance == null)
            {
                _currentQuizzesDone = 0;
                _currentQuizzesDoneDelta = "No quiz selected";
                if (_quizzesDoneValueLabel != null) _quizzesDoneValueLabel.text = "0";
                return;
            }

            // Captured so a late-arriving response from a classroom/quiz the teacher has
            // since navigated away from doesn't clobber a newer selection's numbers.
            string classroomId = _selectedClassroomId;
            string quizId = _selectedQuizExportId;

            QuizService.Instance.FetchQuizScoresForClassroom(classroomId, quizId, scores =>
            {
                if (classroomId != _selectedClassroomId || quizId != _selectedQuizExportId) return;

                int completedCount = (scores ?? new List<QuizService.StudentQuizScoreEntry>()).Count(s => s.Attempted);
                int totalEnrolled = _currentClassroomStudents.Count;
                string quizTitle = _classroomQuizzes.FirstOrDefault(q => q.QuizId == quizId)?.Title;

                _currentQuizzesDone = completedCount;
                _currentQuizzesDoneDelta = FormatQuizzesDoneSummary(completedCount, totalEnrolled, quizTitle);

                if (_quizzesDoneValueLabel != null) _quizzesDoneValueLabel.text = completedCount.ToString();
            });
        }

        /// <summary>Human-readable companion to RefreshQuizzesDoneStat(), e.g. "8/10 students
        /// completed - "Skeletal System Quiz"" - shown in the Quizzes Done row's third column
        /// on the exported report (see AdminReportExportService.BuildPdfLines) in place of
        /// the old month-over-month delta.</summary>
        private static string FormatQuizzesDoneSummary(int completedCount, int totalEnrolled, string quizTitle)
        {
            if (totalEnrolled == 0) return "No students enrolled";

            string quizPart = string.IsNullOrEmpty(quizTitle) ? "" : $" - \"{quizTitle}\"";
            return $"{completedCount}/{totalEnrolled} students completed{quizPart}";
        }

        /// <summary>Fetches the quiz export picker's currently-selected quiz's per-student
        /// scores, joined against the classroom roster (see BuildQuizScoreExportRows) so a
        /// student who never attempted it still gets a row - or calls back with an empty list
        /// immediately if no classroom/quiz is selected yet. Feeds BuildExportData()'s
        /// QuizScoreRows/QuizScoreQuizTitle so both the existing Excel and PDF export buttons
        /// include a "Quiz Scores" section for whichever quiz is picked, without either of
        /// them needing an export button of their own.</summary>
        private void FetchSelectedQuizScoreRows(Action<List<QuizService.StudentQuizScoreEntry>> onComplete)
        {
            if (string.IsNullOrEmpty(_selectedClassroomId) || string.IsNullOrEmpty(_selectedQuizExportId) || QuizService.Instance == null)
            {
                onComplete(new List<QuizService.StudentQuizScoreEntry>());
                return;
            }

            QuizService.Instance.FetchQuizScoresForClassroom(_selectedClassroomId, _selectedQuizExportId, scores =>
            {
                onComplete(BuildQuizScoreExportRows(scores));
            });
        }

        /// <summary>Joins the classroom roster (_currentClassroomStudents) against a quiz's
        /// fetched scores so every student in the classroom gets an export row - students who
        /// never attempted the selected quiz get an Attempted = false row instead of being
        /// silently left out.</summary>
        private List<QuizService.StudentQuizScoreEntry> BuildQuizScoreExportRows(List<QuizService.StudentQuizScoreEntry> scores)
        {
            var byStudentId = (scores ?? new List<QuizService.StudentQuizScoreEntry>())
                .ToDictionary(s => s.StudentId, s => s);

            var rows = new List<QuizService.StudentQuizScoreEntry>();

            foreach (var student in _currentClassroomStudents)
            {
                if (byStudentId.TryGetValue(student.StudentId, out var scored))
                {
                    rows.Add(scored);
                }
                else
                {
                    rows.Add(new QuizService.StudentQuizScoreEntry
                    {
                        StudentId = student.StudentId,
                        StudentName = student.Name,
                        Attempted = false
                    });
                }
            }

            return rows;
        }

        /// <summary>One simple recommendation: call out whichever topic is scoring lowest,
        /// naming the specific quiz whose attempts are actually dragging that topic's
        /// average down (QuizService.FetchClassroomReportData computes this per-category
        /// breakdown). Replace/extend with real rules once you know what else you want
        /// flagged.</summary>
        private List<RecommendationEntry> BuildRecommendations(List<QuizService.CategoryScoreSummary> topicPerformance)
        {
            var recommendations = new List<RecommendationEntry>();
            if (topicPerformance == null || topicPerformance.Count == 0) return recommendations;

            var weakest = topicPerformance.OrderBy(c => c.AvgScorePercent).First();
            string topic = CapitalizeCategory(weakest.Category);

            string title = !string.IsNullOrEmpty(weakest.WeakestQuizTitle)
                ? $"Focus on {weakest.WeakestQuizTitle} ({topic})"
                : $"Focus on {topic}";

            recommendations.Add(new RecommendationEntry(
                title,
                $"Average score is {Mathf.RoundToInt(weakest.AvgScorePercent)}% - consider adding more practice questions in this topic.",
                topic));

            return recommendations;
        }

        private static string FormatDelta(float deltaPercent)
        {
            string sign = deltaPercent >= 0 ? "+" : "";
            return $"{sign}{Mathf.RoundToInt(deltaPercent)}%";
        }

        /// <summary>Completion for the "Completion" overview stat card: the percentage of
        /// currently-enrolled students who have completed at least one quiz in this
        /// classroom (StudentStat.QuizzesCompleted > 0). Only reaches 100% when every
        /// enrolled student has completed a quiz - a single student with zero completions
        /// keeps the classroom below 100%. Classrooms with no enrolled students report 0%
        /// rather than dividing by zero.</summary>
        private static int CalculateClassroomCompletionPercent(List<AdminClassroomService.StudentStat> enrolledStudents)
        {
            if (enrolledStudents == null || enrolledStudents.Count == 0) return 0;

            int completedCount = enrolledStudents.Count(s => s.QuizzesCompleted > 0);
            return Mathf.RoundToInt((completedCount / (float)enrolledStudents.Count) * 100f);
        }

        /// <summary>Human-readable companion to CalculateClassroomCompletionPercent(), e.g.
        /// "8/10 students completed" - shown in the Completion row's third column on the
        /// exported report (see AdminReportExportService.BuildPdfLines) in place of the old
        /// month-over-month delta.</summary>
        private static string FormatCompletionSummary(List<AdminClassroomService.StudentStat> enrolledStudents)
        {
            if (enrolledStudents == null || enrolledStudents.Count == 0) return "No students enrolled";

            int completedCount = enrolledStudents.Count(s => s.QuizzesCompleted > 0);
            return $"{completedCount}/{enrolledStudents.Count} students completed";
        }

        private static string CapitalizeCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return category;
            return char.ToUpperInvariant(category[0]) + category.Substring(1);
        }

        // ---------------- Tabs ----------------

        private void OnPerformanceTabClicked(ClickEvent evt) => SetActiveTab(TabPerformance);
        private void OnStudentsTabClicked(ClickEvent evt) => SetActiveTab(TabStudents);
        private void OnMistakesTabClicked(ClickEvent evt) => SetActiveTab(TabMistakes);

        private void SetActiveTab(string tab)
        {
            _activeTab = tab;

            _performanceTabButton?.EnableInClassList("tab-button-active", tab == TabPerformance);
            _studentsTabButton?.EnableInClassList("tab-button-active", tab == TabStudents);
            _mistakesTabButton?.EnableInClassList("tab-button-active", tab == TabMistakes);

            _performancePanel?.EnableInClassList("hidden", tab != TabPerformance);
            _studentsPanel?.EnableInClassList("hidden", tab != TabStudents);
            _mistakesPanel?.EnableInClassList("hidden", tab != TabMistakes);
        }

        // ---------------- Performance tab ----------------

        private void RefreshScoreTrendUI()
        {
            if (_scoreTrendList == null) return;

            _scoreTrendList.Clear();

            for (int i = 0; i < _currentScoreTrend.Count; i++)
            {
                var entry = _currentScoreTrend[i];

                var row = new VisualElement();
                row.AddToClassList("score-trend-row");
                if (i == _currentScoreTrend.Count - 1) row.AddToClassList("score-trend-row-last");

                var topRow = new VisualElement();
                topRow.AddToClassList("score-trend-top-row");

                var nameLabel = new Label(entry.Label);
                nameLabel.AddToClassList("score-trend-name-label");

                var valueLabel = new Label($"{Mathf.RoundToInt(entry.ScorePercent)}%");
                valueLabel.AddToClassList("score-trend-value-label");

                topRow.Add(nameLabel);
                topRow.Add(valueLabel);
                row.Add(topRow);

                var track = new VisualElement();
                track.AddToClassList("score-trend-bar-track");

                var fill = new VisualElement();
                fill.AddToClassList("score-trend-bar-fill");
                fill.style.width = new Length(Mathf.Clamp(entry.ScorePercent, 0f, 100f), LengthUnit.Percent);
                track.Add(fill);

                row.Add(track);
                _scoreTrendList.Add(row);
            }
        }

        // ---------------- Students tab ----------------

        private void RefreshTopPerformersUI()
        {
            if (_topPerformersList == null) return;

            _topPerformersList.Clear();

            for (int i = 0; i < _currentTopPerformers.Count; i++)
            {
                var performer = _currentTopPerformers[i];
                int rank = i + 1;

                var row = new VisualElement();
                row.AddToClassList("performer-row");
                if (i == _currentTopPerformers.Count - 1) row.AddToClassList("performer-row-last");

                var badge = new VisualElement();
                badge.AddToClassList("performer-rank-badge");
                badge.AddToClassList(RankBadgeClass(rank));
                var rankLabel = new Label(rank.ToString());
                rankLabel.AddToClassList("performer-rank-label");
                badge.Add(rankLabel);
                row.Add(badge);

                var info = new VisualElement();
                info.AddToClassList("performer-info");
                var nameLabel = new Label(performer.Name);
                nameLabel.AddToClassList("performer-name-label");
                var quizzesLabel = new Label($"{performer.QuizzesCompleted} quizzes completed");
                quizzesLabel.AddToClassList("performer-quizzes-label");
                info.Add(nameLabel);
                info.Add(quizzesLabel);
                row.Add(info);

                var stats = new VisualElement();
                stats.AddToClassList("performer-stats");
                var pointsLabel = new Label($"{performer.Points} pts");
                pointsLabel.AddToClassList("performer-points-label");
                var levelLabel = new Label($"Level {performer.Level}");
                levelLabel.AddToClassList("performer-level-label");
                stats.Add(pointsLabel);
                stats.Add(levelLabel);
                row.Add(stats);

                _topPerformersList.Add(row);
            }
        }

        private static string RankBadgeClass(int rank)
        {
            switch (rank)
            {
                case 1: return "performer-rank-gold";
                case 2: return "performer-rank-silver";
                case 3: return "performer-rank-bronze";
                default: return "performer-rank-default";
            }
        }

        private void RefreshStudentActivityUI()
        {
            if (_totalStudentsValueLabel != null) _totalStudentsValueLabel.text = _currentStudentActivity.TotalStudents.ToString();
            if (_activeThisMonthValueLabel != null) _activeThisMonthValueLabel.text = _currentStudentActivity.ActiveThisMonth.ToString();
            if (_averageLevelValueLabel != null) _averageLevelValueLabel.text = _currentStudentActivity.AverageLevel.ToString("0.0");
            if (_averagePointsValueLabel != null) _averagePointsValueLabel.text = _currentStudentActivity.AveragePoints.ToString("N0");
        }

        // ---------------- Mistakes tab ----------------

        private void RefreshMistakesUI()
        {
            if (_commonMistakesList == null) return;

            _commonMistakesList.Clear();

            foreach (var mistake in _currentMistakes)
            {
                var row = new VisualElement();
                row.AddToClassList("mistake-row");

                var info = new VisualElement();
                info.AddToClassList("mistake-info");
                var questionLabel = new Label(mistake.Question);
                questionLabel.AddToClassList("mistake-question-label");
                var categoryLabel = new Label(mistake.Category);
                categoryLabel.AddToClassList("mistake-category-label");
                info.Add(questionLabel);
                info.Add(categoryLabel);
                row.Add(info);

                var countCol = new VisualElement();
                countCol.AddToClassList("mistake-count-col");
                var countValue = new Label(mistake.Errors.ToString());
                countValue.AddToClassList("mistake-count-value");
                var countLabel = new Label("errors");
                countLabel.AddToClassList("mistake-count-label");
                countCol.Add(countValue);
                countCol.Add(countLabel);
                row.Add(countCol);

                _commonMistakesList.Add(row);
            }
        }

        private void RefreshRecommendationsUI()
        {
            if (_recommendationsList == null) return;

            _recommendationsList.Clear();

            foreach (var recommendation in _currentRecommendations)
            {
                var item = new VisualElement();
                item.AddToClassList("recommendation-item");

                var iconCircle = new VisualElement();
                iconCircle.AddToClassList("recommendation-icon-circle");
                var icon = new Label("\U0001F4A1"); // 💡
                icon.AddToClassList("recommendation-icon");
                iconCircle.Add(icon);
                item.Add(iconCircle);

                var textCol = new VisualElement();
                textCol.AddToClassList("recommendation-text-col");
                var titleLabel = new Label(recommendation.Title);
                titleLabel.AddToClassList("recommendation-title-label");
                var descLabel = new Label(recommendation.Description);
                descLabel.AddToClassList("recommendation-description-label");
                textCol.Add(titleLabel);
                textCol.Add(descLabel);
                item.Add(textCol);

                _recommendationsList.Add(item);
            }
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[AdminAnalyticsReportsController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        private void OnExportPdfClicked(ClickEvent evt)
        {
            Debug.Log("[AdminAnalyticsReportsController] Export to PDF tapped.");
            PrepareAndExport(AdminReportExportService.ExportPdf, "PDF");
        }

        /// <summary>Shared by both export buttons. First fetches the quiz export picker's
        /// currently-selected quiz's scores (if any - see FetchSelectedQuizScoreRows), so the
        /// exported file includes a "Quiz Scores" section for that quiz alongside the usual
        /// analytics report, then hands the finished file to the native share/save dialog.
        /// exportFn is AdminReportExportService.ExportCsv or ExportPdf - both buttons just
        /// point this at their own writer and label.</summary>
        private void PrepareAndExport(Func<AdminReportExportService.ReportExportData, string> exportFn, string reportLabel)
        {
           
            _exportPdfButton?.SetEnabled(false);

            FetchSelectedQuizScoreRows(quizScoreRows =>
            {
                string path = exportFn(BuildExportData(quizScoreRows));

                if (path == null)
                {
                   
                    _exportPdfButton?.SetEnabled(true);
                    Debug.LogWarning($"[AdminAnalyticsReportsController] {reportLabel} export failed - see the logged error above.");
                    return;
                }

                ExportViaNativeFilePicker(path, reportLabel);
            });
        }

        // Hands a just-generated report file to NativeFilePicker so the teacher gets
        // the real OS share/save dialog (Android's "Save As"/Storage Access Framework
        // picker, or iOS's share sheet) instead of the file just sitting silently in
        // Application.temporaryCachePath. Shared by both export buttons since the
        // hand-off/cleanup logic is identical - only the label used in the log lines
        // and the source file differ.
        //
        // Disables both export buttons for the duration of the native dialog so a
        // second tap can't queue a second picker session on top of one already open -
        // NativeFilePicker.IsFilePickerBusy() would just silently no-op that second
        // call anyway (its callback fires with false), so re-enabling promptly here
        // is friendlier than leaving the teacher wondering why nothing happened.
        private void ExportViaNativeFilePicker(string path, string reportLabel)
        {
          
            _exportPdfButton?.SetEnabled(false);

            NativeFilePicker.ExportFile(path, success =>
            {
              
                _exportPdfButton?.SetEnabled(true);

                if (success)
                    Debug.Log($"[AdminAnalyticsReportsController] {reportLabel} report exported successfully.");
                else
                    Debug.Log($"[AdminAnalyticsReportsController] {reportLabel} export was cancelled or failed.");

                // The hand-off is done either way (the OS now has its own copy on
                // success; on cancel/failure there's nothing left to retry from this
                // exact temp file - a fresh export regenerates it) - see
                // AdminReportExportService.CleanUpExportedFile's doc comment.
                AdminReportExportService.CleanUpExportedFile(path);
            });
        }

        /// <summary>Snapshots everything currently on screen (real data if a classroom has
        /// loaded, placeholder data otherwise) into the shape AdminReportExportService needs.
        /// quizScoreRows is whatever FetchSelectedQuizScoreRows() resolved with - empty if no
        /// quiz is selected, in which case the exported file's "Quiz Scores" section is
        /// simply omitted (see AdminReportExportService.ExportCsv/ExportPdf).</summary>
        private AdminReportExportService.ReportExportData BuildExportData(List<QuizService.StudentQuizScoreEntry> quizScoreRows)
        {
            string classroomName = null;
            if (!string.IsNullOrEmpty(_selectedClassroomId))
            {
                var match = _classrooms.FirstOrDefault(c => c.ClassroomId == _selectedClassroomId);
                classroomName = match?.Name;
            }

            string quizScoreTitle = null;
            if (!string.IsNullOrEmpty(_selectedQuizExportId))
            {
                var quiz = _classroomQuizzes.FirstOrDefault(q => q.QuizId == _selectedQuizExportId);
                quizScoreTitle = quiz?.Title;
            }

            return new AdminReportExportService.ReportExportData
            {
                ClassroomName = classroomName,
                DateRangeLabel = null,

                ActiveUsers = _currentActiveUsers,
                ActiveUsersDelta = _currentActiveUsersDelta,
                AvgScorePercent = _currentAvgScorePercent,
                QuizzesDone = _currentQuizzesDone,
                QuizzesDoneDelta = _currentQuizzesDoneDelta,
                CompletionPercent = _currentCompletionPercent,
                CompletionDelta = _currentCompletionDelta,

                TopPerformers = _currentTopPerformers,
                StudentActivity = _currentStudentActivity,
                ScoreTrend = _currentScoreTrend,
                TopicPerformance = _currentTopicPerformance,
                Mistakes = _currentMistakes,
                Recommendations = _currentRecommendations,

                QuizScoreQuizTitle = quizScoreTitle,
                QuizScoreRows = quizScoreRows ?? new List<QuizService.StudentQuizScoreEntry>()
            };
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }
        
    }
}
