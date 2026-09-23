using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminSubmissionReview.uxml - the teacher's "View Submissions"
    /// screen for a File Submission assignment. Attach to the same GameObject as
    /// UIManager, like every other screen controller.
    ///
    /// Reached from AdminQuizManagementController's quiz detail view, which only
    /// shows the "View Submissions" button for a quiz whose `submissionType` is
    /// "file" - question-based quizzes are untouched.
    ///
    /// The list is per CLASSROOM. The same quiz can be published into several of
    /// the teacher's classrooms (see AdminClassroomService.SetQuizPublished), so
    /// on open this screen finds every classroom of the teacher that has the quiz
    /// published, lets the teacher switch between them, and always queries
    /// `fileSubmissions` with a concrete classroomId. That is what the Firestore
    /// read rule needs (it does a get() on the submission's classroom to prove the
    /// teacher owns it) - a quizId-only list query can't satisfy it.
    ///
    /// For the selected classroom it merges the class roster with the submissions,
    /// so the teacher sees one row per STUDENT: Passed, Did not pass, To review, or
    /// Not submitted yet.
    ///
    /// Downloading a submitted file goes Unity -> Cloudflare Worker -> R2 (see
    /// R2FileUploadService); the Worker re-checks that the signed-in teacher
    /// actually owns the classroom the submission belongs to. Scoring goes
    /// through FileSubmissionService.ReviewSubmission, which writes the grade
    /// into the existing `quizAttempts` / student / classroom-member records, so
    /// a graded assignment shows up in the Scores tab, progress and analytics
    /// like any other completed quiz.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminSubmissionReviewController : MonoBehaviour
    {
        [Header("Header gradient (green -> purple, matches Quiz Management)")]
        [SerializeField] private Color gradientStart = new Color(0.141f, 0.765f, 0.384f);
        [SerializeField] private Color gradientEnd = new Color(0.529f, 0.376f, 0.941f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private Label _titleLabel;
        private Label _subtitleLabel;
        private Label _classroomLabel;
        private Label _passedValue;
        private Label _toReviewValue;
        private Label _missingValue;

        private VisualElement _classroomSection;
        private VisualElement _classroomChips;
        private VisualElement _filterRow;
        private VisualElement _list;
        private VisualElement _emptyState;
        private Label _emptyLabel;
        private Label _statusLabel;
        private ScrollView _listScroll;
        private ScrollView _modalScroll;

        private VisualElement _reviewOverlay;
        private VisualElement _reviewAvatar;
        private Label _reviewAvatarLabel;
        private Label _reviewStudentLabel;
        private Label _reviewAttemptPill;
        private Button _reviewCloseButton;
        private Label _reviewFileLabel;
        private Label _reviewSizeLabel;
        private Label _reviewAttemptLabel;
        private Label _reviewSubmittedLabel;
        private Button _openFileButton;
        private Label _scoreFieldLabel;
        private TextField _scoreField;
        private VisualElement _scorePreview;
        private Label _scorePreviewLabel;
        private TextField _feedbackField;
        private Label _reviewError;
        private Button _reviewCancelButton;
        private Button _reviewSaveButton;

        private VisualElement _workingOverlay;
        private Label _workingLabel;

        // ---------------- State ----------------

        private enum StudentStatus { ToReview = 0, NotSubmitted = 1, NotPassed = 2, Passed = 3 }
        private enum StatusFilter { All, Passed, ToReview, NotPassed, NotSubmitted }

        /// <summary>One student in the selected classroom: their roster entry
        /// (if they're still in the class) merged with all their submissions.</summary>
        private class StudentEntry
        {
            public string StudentId;
            public string Name;
            public bool InRoster;

            /// <summary>Oldest attempt first.</summary>
            public readonly List<FileSubmissionService.SubmissionRecord> Submissions =
                new List<FileSubmissionService.SubmissionRecord>();

            /// <summary>Highest-scoring graded attempt, or null if none is graded yet.</summary>
            public FileSubmissionService.SubmissionRecord BestReviewed;

            /// <summary>Newest attempt still waiting for the teacher, or null.</summary>
            public FileSubmissionService.SubmissionRecord PendingLatest;

            public StudentStatus Status;

            public FileSubmissionService.SubmissionRecord Latest =>
                Submissions.Count > 0 ? Submissions[Submissions.Count - 1] : null;

            /// <summary>What the main row button opens: something waiting for review
            /// first, otherwise the newest attempt.</summary>
            public FileSubmissionService.SubmissionRecord Primary => PendingLatest ?? Latest;
        }

        private string _quizId;
        private string _requestedClassroomId;
        private string _quizTitle;
        private int _pointsPossible;
        private int _passingScorePercent = 70;

        // Classrooms of this teacher that have the quiz published (plus the one
        // explicitly requested by the caller, if any).
        private readonly List<AdminClassroomService.ClassroomRecord> _classrooms =
            new List<AdminClassroomService.ClassroomRecord>();
        private AdminClassroomService.ClassroomRecord _selectedClassroom;
        private bool _classroomsLoaded;

        private readonly List<AdminClassroomService.StudentStat> _roster =
            new List<AdminClassroomService.StudentStat>();
        private bool _rosterLoaded;

        private readonly List<FileSubmissionService.SubmissionRecord> _submissions =
            new List<FileSubmissionService.SubmissionRecord>();

        private readonly List<StudentEntry> _entries = new List<StudentEntry>();
        private StatusFilter _filter = StatusFilter.All;

        // Bumped on every (re)load so a slow response for a classroom the teacher
        // has already switched away from can't overwrite the current one.
        private int _loadToken;

        private FileSubmissionService.SubmissionRecord _reviewing;
        private bool _isSaving;
        private bool _clampingListScroll;
        // Bumped on every silent resume refresh so only the newest one applies.
        private int _silentRefreshId;

        // ==================================================================
        // Lifecycle
        // ==================================================================

        private void OnEnable()
        {
            if (_document == null) _document = GetComponent<UIDocument>();

            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null) _root = uiDocument.rootVisualElement;
            }

            if (_root == null && _document != null) _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[AdminSubmissionReviewController] Root is null!");
                return;
            }

            UnregisterCallbacks();
            NetworkStatusMonitor.OnAppResumed -= HandleAppResumed;
            NetworkStatusMonitor.OnAppResumed += HandleAppResumed;
            QueryElements();
            WireCallbacks();
            ApplyGradients();
            UpdateResponsiveLayout();
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

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("submission-review-root");
            _header = _root.Q<VisualElement>("sr-header");
            _backButton = _root.Q<Button>("sr-back-button");
            _titleLabel = _root.Q<Label>("sr-title-label");
            _subtitleLabel = _root.Q<Label>("sr-subtitle-label");
            _classroomLabel = _root.Q<Label>("sr-classroom-label");
            _passedValue = _root.Q<Label>("sr-passed-value");
            _toReviewValue = _root.Q<Label>("sr-toreview-value");
            _missingValue = _root.Q<Label>("sr-missing-value");

            _classroomSection = _root.Q<VisualElement>("sr-classroom-section");
            _classroomChips = _root.Q<VisualElement>("sr-classroom-chips");
            _filterRow = _root.Q<VisualElement>("sr-filter-row");
            _list = _root.Q<VisualElement>("sr-list");
            _emptyState = _root.Q<VisualElement>("sr-empty-state");
            _emptyLabel = _root.Q<Label>("sr-empty-label");
            _statusLabel = _root.Q<Label>("sr-status-label");
            _listScroll = _root.Q<ScrollView>("sr-scroll");
            _modalScroll = _root.Q<ScrollView>("sr-modal-scroll");

            // The "touch-scroll-type" UXML attribute is affected by a long-standing,
            // acknowledged Unity binding bug (its name doesn't match the C# field
            // "touchScrollBehavior", so the value written in the .uxml isn't reliably
            // applied at load time). Setting it directly on the ScrollView object here
            // is the documented-working path: it guarantees dragging is clamped to the
            // real content bounds - i.e. a short list that already fits on screen simply
            // can't be dragged at all - rather than depending on the UXML parser picking
            // up the attribute correctly.
            if (_listScroll != null) _listScroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
            if (_modalScroll != null) _modalScroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;

            _reviewOverlay = _root.Q<VisualElement>("sr-review-overlay");
            _reviewAvatar = _root.Q<VisualElement>("sr-review-avatar");
            _reviewAvatarLabel = _root.Q<Label>("sr-review-avatar-label");
            _reviewStudentLabel = _root.Q<Label>("sr-review-student-label");
            _reviewAttemptPill = _root.Q<Label>("sr-review-attempt-pill");
            _reviewCloseButton = _root.Q<Button>("sr-review-close-button");
            _reviewFileLabel = _root.Q<Label>("sr-review-file-label");
            _reviewSizeLabel = _root.Q<Label>("sr-review-size-label");
            _reviewAttemptLabel = _root.Q<Label>("sr-review-attempt-label");
            _reviewSubmittedLabel = _root.Q<Label>("sr-review-submitted-label");
            _openFileButton = _root.Q<Button>("sr-open-file-button");
            _scoreFieldLabel = _root.Q<Label>("sr-score-field-label");
            _scoreField = _root.Q<TextField>("sr-score-field");
            _scorePreview = _root.Q<VisualElement>("sr-score-preview");
            _scorePreviewLabel = _root.Q<Label>("sr-score-preview-label");
            _feedbackField = _root.Q<TextField>("sr-feedback-field");
            _reviewError = _root.Q<Label>("sr-review-error");
            _reviewCancelButton = _root.Q<Button>("sr-review-cancel-button");
            _reviewSaveButton = _root.Q<Button>("sr-review-save-button");

            _workingOverlay = _root.Q<VisualElement>("sr-working-overlay");
            _workingLabel = _root.Q<Label>("sr-working-label");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _reviewCloseButton?.RegisterCallback<ClickEvent>(OnReviewCloseClicked);
            _reviewCancelButton?.RegisterCallback<ClickEvent>(OnReviewCloseClicked);
            _reviewSaveButton?.RegisterCallback<ClickEvent>(OnReviewSaveClicked);
            _openFileButton?.RegisterCallback<ClickEvent>(OnOpenFileClicked);
            _scoreField?.RegisterValueChangedCallback(OnScoreFieldChanged);
            _screenRoot?.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            if (_listScroll != null)
            {
                _listScroll.verticalScroller.valueChanged += OnListScrollChanged;
                _listScroll.contentContainer.RegisterCallback<GeometryChangedEvent>(OnListScrollGeometryChanged);
                _listScroll.contentViewport.RegisterCallback<GeometryChangedEvent>(OnListScrollGeometryChanged);
            }
        }

        private void UnregisterCallbacks()
        {
            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _reviewCloseButton?.UnregisterCallback<ClickEvent>(OnReviewCloseClicked);
            _reviewCancelButton?.UnregisterCallback<ClickEvent>(OnReviewCloseClicked);
            _reviewSaveButton?.UnregisterCallback<ClickEvent>(OnReviewSaveClicked);
            _openFileButton?.UnregisterCallback<ClickEvent>(OnOpenFileClicked);
            _scoreField?.UnregisterValueChangedCallback(OnScoreFieldChanged);
            _screenRoot?.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            if (_listScroll != null)
            {
                _listScroll.verticalScroller.valueChanged -= OnListScrollChanged;
                _listScroll.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnListScrollGeometryChanged);
                _listScroll.contentViewport.UnregisterCallback<GeometryChangedEvent>(OnListScrollGeometryChanged);
            }
        }

        // ==================================================================
        // List scroll clamp
        // ==================================================================

        private void OnListScrollChanged(float _) => ClampListScroll();

        private void OnListScrollGeometryChanged(GeometryChangedEvent evt) => ClampListScroll();

        /// <summary>
        /// Keeps the student list's scroll offset inside [0, contentHeight - viewportHeight].
        /// When the content is SHORTER than the viewport the scroller's range collapses
        /// to a negative/inverted range, and the ScrollView (even in Clamped mode) then
        /// lets the content be dragged DOWN, leaving blank space above the first row.
        /// Forcing the offset back into the real range pins the list to the top, and
        /// still lets a long list scroll normally.
        /// </summary>
        private void ClampListScroll()
        {
            if (_listScroll == null || _clampingListScroll) return;

            var viewport = _listScroll.contentViewport;
            var content = _listScroll.contentContainer;
            if (viewport == null || content == null) return;

            float maxScroll = Mathf.Max(0f, content.layout.height - viewport.layout.height);
            Vector2 offset = _listScroll.scrollOffset;
            float clampedY = Mathf.Clamp(offset.y, 0f, maxScroll);
            if (Mathf.Approximately(offset.y, clampedY)) return;

            _clampingListScroll = true;
            try { _listScroll.scrollOffset = new Vector2(offset.x, clampedY); }
            finally { _clampingListScroll = false; }
        }

        // ==================================================================
        // Load
        // ==================================================================

        /// <summary>Entry point, called by UIManager.ShowAdminSubmissionReview().
        /// classroomId is optional: when it's null/empty (how Quiz Management opens
        /// this screen - a quiz detail isn't tied to one classroom) the screen finds
        /// the teacher's classrooms that have this quiz published and starts on the
        /// first one; when it's set, that classroom is selected first.</summary>
        public void LoadSubmissions(string quizId, string classroomId, string quizTitle, int pointsPossible, int passingScorePercent)
        {
            _quizId = quizId;
            _requestedClassroomId = classroomId;
            _quizTitle = quizTitle;
            _pointsPossible = pointsPossible;
            _passingScorePercent = passingScorePercent > 0 ? passingScorePercent : 70;

            _classrooms.Clear();
            _selectedClassroom = null;
            _classroomsLoaded = false;
            _roster.Clear();
            _rosterLoaded = false;
            _submissions.Clear();
            _entries.Clear();
            _filter = StatusFilter.All;
            _loadToken++;

            if (_titleLabel != null) _titleLabel.text = string.IsNullOrEmpty(quizTitle) ? "Submissions" : quizTitle;
            if (_subtitleLabel != null)
                _subtitleLabel.text = $"File Submission \u2022 {_pointsPossible} points \u2022 Passing {_passingScorePercent}%";

            SetStatus(null);
            RefreshAll();
            LoadClassrooms();
        }

        /// <summary>Step 1: which of this teacher's classrooms have the quiz?</summary>
        private void LoadClassrooms()
        {
            if (AdminClassroomService.Instance == null || FileSubmissionService.Instance == null)
            {
                SetStatus("Submissions can't be loaded right now. Please try again in a moment.");
                return;
            }

            int token = ++_loadToken;
            ShowWorking("Loading classrooms...");

            AdminClassroomService.Instance.FetchMyClassrooms(all =>
            {
                if (token != _loadToken) return;

                var matching = (all ?? new List<AdminClassroomService.ClassroomRecord>())
                    .Where(c => (c.PublishedQuizIds != null && c.PublishedQuizIds.Contains(_quizId))
                                || (!string.IsNullOrEmpty(_requestedClassroomId) && c.ClassroomId == _requestedClassroomId))
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _classrooms.Clear();
                _classrooms.AddRange(matching);
                _classroomsLoaded = true;

                if (matching.Count == 0)
                {
                    HideWorking();
                    RefreshAll();
                    return;
                }

                _selectedClassroom = matching.FirstOrDefault(c => c.ClassroomId == _requestedClassroomId) ?? matching[0];
                LoadSelectedClassroom();
            });
        }

        /// <summary>Step 2: the class roster and this quiz's submissions for ONE
        /// classroom, fetched together. The submissions query always carries a
        /// concrete classroomId (see class summary).</summary>
        private void LoadSelectedClassroom()
        {
            var classroom = _selectedClassroom;
            if (classroom == null) return;

            int token = ++_loadToken;

            _roster.Clear();
            _rosterLoaded = false;
            _submissions.Clear();
            _entries.Clear();

            SetStatus(null);
            RefreshAll();
            ShowWorking("Loading submissions...");

            int remaining = 2;
            bool submissionsOk = false;
            string submissionsError = null;

            void Finish()
            {
                remaining--;
                if (remaining > 0 || token != _loadToken) return;

                HideWorking();
                RebuildEntries();

                if (!submissionsOk)
                    SetStatus(submissionsError ?? "Could not load submissions.");
                else if (!_rosterLoaded)
                    SetStatus("Couldn't load this classroom's student list, so students who haven't submitted yet can't be shown.");
                else
                    SetStatus(null);

                RefreshAll();
            }

            AdminClassroomService.Instance.FetchClassroomMembers(classroom.ClassroomId, (ok, error, members) =>
            {
                if (token == _loadToken)
                {
                    _rosterLoaded = ok;
                    if (ok && members != null) _roster.AddRange(members);
                }
                Finish();
            });

            FileSubmissionService.Instance.FetchSubmissionsForQuiz(classroom.ClassroomId, _quizId, (ok, error, results) =>
            {
                if (token == _loadToken)
                {
                    submissionsOk = ok;
                    submissionsError = error;
                    if (results != null) _submissions.AddRange(results);
                }
                Finish();
            });
        }
        // ==================================================================
        // App resume
        // ==================================================================

        /// <summary>App came back from the background (NetworkStatusMonitor.OnAppResumed, which
        /// only fires while online) - e.g. the teacher opened a submitted file in another app.
        /// The student list is loaded with one-shot fetches, so reload it now: students may have
        /// submitted in the meantime.</summary>
        private void HandleAppResumed(float secondsAway) => RefreshSelectedClassroomSilently();

        /// <summary>Re-fetches the selected classroom's roster and submissions WITHOUT the
        /// full-screen "Loading..." state, without clearing what is on screen, and without
        /// touching the review dialog (so a score or feedback being typed is kept). The new data
        /// replaces the old only when BOTH fetches succeed, and is dropped if the teacher
        /// switched classroom, a full reload started, or a save began in the meantime.</summary>
        private void RefreshSelectedClassroomSilently()
        {
            var classroom = _selectedClassroom;
            if (classroom == null || !_classroomsLoaded || _isSaving) return;
            if (AdminClassroomService.Instance == null || FileSubmissionService.Instance == null) return;

            int loadToken = _loadToken;
            int refreshId = ++_silentRefreshId;

            var roster = new List<AdminClassroomService.StudentStat>();
            var submissions = new List<FileSubmissionService.SubmissionRecord>();
            bool rosterOk = false;
            bool submissionsOk = false;
            int remaining = 2;

            void Finish()
            {
                remaining--;
                if (remaining > 0) return;
                if (loadToken != _loadToken || refreshId != _silentRefreshId || _isSaving) return;
                if (!rosterOk || !submissionsOk) return; // keep what is on screen; the next resume retries

                Vector2 scrollOffset = _listScroll != null ? _listScroll.scrollOffset : Vector2.zero;

                _roster.Clear();
                _roster.AddRange(roster);
                _rosterLoaded = true;
                _submissions.Clear();
                _submissions.AddRange(submissions);

                RebuildEntries();
                SetStatus(null);
                RefreshAll();

                // The rebuild resets the list; put the teacher back where they were
                // (ClampListScroll keeps it inside the new content's range).
                if (_listScroll != null)
                    _listScroll.schedule.Execute(() => _listScroll.scrollOffset = scrollOffset).ExecuteLater(0);
            }

            AdminClassroomService.Instance.FetchClassroomMembers(classroom.ClassroomId, (ok, error, members) =>
            {
                rosterOk = ok;
                if (ok && members != null) roster.AddRange(members);
                Finish();
            });

            FileSubmissionService.Instance.FetchSubmissionsForQuiz(classroom.ClassroomId, _quizId, (ok, error, results) =>
            {
                submissionsOk = ok;
                if (results != null) submissions.AddRange(results);
                Finish();
            });
        }


        // ==================================================================
        // Student entries (roster + submissions)
        // ==================================================================

        private float PercentOf(int score) => _pointsPossible > 0 ? score * 100f / _pointsPossible : 0f;

        /// <summary>Same rule ReviewSubmission writes to the attempt row's `passed`
        /// field: percent >= the assignment's passing score.</summary>
        private bool IsPassingScore(int score) => PercentOf(score) >= _passingScorePercent;

        private string ScoreText(int score) =>
            _pointsPossible > 0
                ? $"{score}/{_pointsPossible} pts ({PercentOf(score):0}%)"
                : $"{score} pts";

        private void RebuildEntries()
        {
            _entries.Clear();
            var byStudent = new Dictionary<string, StudentEntry>();

            // Everyone currently in the class, whether or not they've submitted.
            foreach (var member in _roster)
            {
                string id = member.StudentId ?? string.Empty;
                if (id.Length == 0 || byStudent.ContainsKey(id)) continue;

                byStudent[id] = new StudentEntry { StudentId = id, Name = member.Name, InRoster = true };
            }

            // Submissions; a student who has since left the class still shows up.
            foreach (var submission in _submissions)
            {
                string id = submission.StudentId ?? string.Empty;
                if (!byStudent.TryGetValue(id, out var entry))
                {
                    entry = new StudentEntry { StudentId = id, Name = submission.StudentName, InRoster = false };
                    byStudent[id] = entry;
                }

                entry.Submissions.Add(submission);
            }

            foreach (var entry in byStudent.Values)
            {
                entry.Submissions.Sort((a, b) =>
                {
                    int byAttempt = a.AttemptNumber.CompareTo(b.AttemptNumber);
                    return byAttempt != 0 ? byAttempt : a.SubmittedAtUtc.CompareTo(b.SubmittedAtUtc);
                });

                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name == "Student")
                {
                    var named = entry.Submissions.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.StudentName));
                    entry.Name = named != null ? named.StudentName : "Student";
                }

                entry.PendingLatest = entry.Submissions.LastOrDefault(s => !s.IsReviewed);
                entry.BestReviewed = entry.Submissions
                    .Where(s => s.IsReviewed)
                    .OrderByDescending(s => s.Score)
                    .FirstOrDefault();

                // Work waiting on the teacher outranks an older grade: a student who
                // resubmitted shows as "To review" until that new file is graded.
                if (entry.Submissions.Count == 0) entry.Status = StudentStatus.NotSubmitted;
                else if (entry.PendingLatest != null) entry.Status = StudentStatus.ToReview;
                else if (entry.BestReviewed != null && IsPassingScore(entry.BestReviewed.Score)) entry.Status = StudentStatus.Passed;
                else entry.Status = StudentStatus.NotPassed;

                _entries.Add(entry);
            }
        }

        private bool MatchesFilter(StudentEntry entry)
        {
            switch (_filter)
            {
                case StatusFilter.Passed: return entry.Status == StudentStatus.Passed;
                case StatusFilter.ToReview: return entry.Status == StudentStatus.ToReview;
                case StatusFilter.NotPassed: return entry.Status == StudentStatus.NotPassed;
                case StatusFilter.NotSubmitted: return entry.Status == StudentStatus.NotSubmitted;
                default: return true;
            }
        }

        // ==================================================================
        // Rendering
        // ==================================================================

        private void RefreshAll()
        {
            RefreshHeader();
            RefreshStats();
            RefreshClassroomChips();
            RefreshFilters();
            RefreshList();
        }

        private void RefreshHeader()
        {
            if (_classroomLabel == null) return;

            if (_selectedClassroom == null)
            {
                _classroomLabel.text = string.Empty;
                _classroomLabel.AddToClassList("hidden");
                return;
            }

            int students = _rosterLoaded ? _roster.Count : _selectedClassroom.StudentCount;
            string name = string.IsNullOrEmpty(_selectedClassroom.Name) ? "Classroom" : _selectedClassroom.Name;
            _classroomLabel.text = $"{name} \u2022 {students} student{(students == 1 ? "" : "s")}";
            _classroomLabel.RemoveFromClassList("hidden");
        }

        private void RefreshStats()
        {
            int passed = _entries.Count(e => e.Status == StudentStatus.Passed);
            int toReview = _entries.Count(e => e.Status == StudentStatus.ToReview);
            int missing = _entries.Count(e => e.Status == StudentStatus.NotSubmitted);

            if (_passedValue != null) _passedValue.text = passed.ToString();
            if (_toReviewValue != null) _toReviewValue.text = toReview.ToString();
            // Without the class roster, "not submitted" can't be worked out - show a
            // dash rather than a misleading 0.
            if (_missingValue != null) _missingValue.text = _rosterLoaded ? missing.ToString() : "-";
        }

        /// <summary>Classroom switcher - only shown when the quiz is published in
        /// more than one of the teacher's classrooms.</summary>
        private void RefreshClassroomChips()
        {
            if (_classroomChips == null) return;

            _classroomChips.Clear();
            bool show = _classrooms.Count > 1;
            _classroomSection?.EnableInClassList("hidden", !show);
            if (!show) return;

            foreach (var classroom in _classrooms)
            {
                var captured = classroom;
                string text = string.IsNullOrEmpty(captured.Name) ? "Classroom" : captured.Name;
                bool active = _selectedClassroom != null && _selectedClassroom.ClassroomId == captured.ClassroomId;

                _classroomChips.Add(MakeChip(text, active, () =>
                {
                    if (_isSaving) return;
                    if (_selectedClassroom != null && _selectedClassroom.ClassroomId == captured.ClassroomId) return;

                    _selectedClassroom = captured;
                    _filter = StatusFilter.All;
                    LoadSelectedClassroom();
                }));
            }
        }

        private void RefreshFilters()
        {
            if (_filterRow == null) return;

            _filterRow.Clear();
            bool show = _selectedClassroom != null;
            _filterRow.EnableInClassList("hidden", !show);
            if (!show) return;

            AddFilterChip(StatusFilter.All, "All", _entries.Count);
            AddFilterChip(StatusFilter.Passed, "Passed", _entries.Count(e => e.Status == StudentStatus.Passed));
            AddFilterChip(StatusFilter.ToReview, "To review", _entries.Count(e => e.Status == StudentStatus.ToReview));
            AddFilterChip(StatusFilter.NotPassed, "Not passed", _entries.Count(e => e.Status == StudentStatus.NotPassed));
            if (_rosterLoaded)
                AddFilterChip(StatusFilter.NotSubmitted, "Not submitted", _entries.Count(e => e.Status == StudentStatus.NotSubmitted));
        }

        private void AddFilterChip(StatusFilter filter, string label, int count)
        {
            _filterRow.Add(MakeChip($"{label} ({count})", _filter == filter, () =>
            {
                _filter = filter;
                RefreshFilters();
                RefreshList();
            }));
        }

        private static Button MakeChip(string text, bool active, Action onClick)
        {
            var chip = new Button(onClick) { text = text };
            chip.AddToClassList("sr-chip");
            if (active) chip.AddToClassList("sr-chip--active");
            return chip;
        }

        private void RefreshList()
        {
            if (_list == null) return;

            _list.Clear();

            // Nothing to say until the classroom lookup has finished.
            if (!_classroomsLoaded)
            {
                _emptyState?.AddToClassList("hidden");
                return;
            }

            if (_selectedClassroom == null)
            {
                SetEmpty("This assignment isn't published to any of your classrooms yet. Publish it from a classroom's Quizzes tab, then come back.");
                return;
            }

            var visible = _entries
                .Where(MatchesFilter)
                .OrderBy(e => (int)e.Status)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (visible.Count == 0)
            {
                SetEmpty(EmptyMessageForFilter());
                return;
            }

            _emptyState?.AddToClassList("hidden");
            foreach (var entry in visible) _list.Add(BuildRow(entry));
            _list.Children().LastOrDefault()?.AddToClassList("sr-row--last");
        }

        private string EmptyMessageForFilter()
        {
            switch (_filter)
            {
                case StatusFilter.Passed: return "No student has passed yet.";
                case StatusFilter.ToReview: return "Nothing is waiting for review.";
                case StatusFilter.NotPassed: return "No student is below the passing score.";
                case StatusFilter.NotSubmitted: return "Everyone in this classroom has submitted.";
                default: return "No students in this classroom yet.";
            }
        }

        private void SetEmpty(string message)
        {
            if (_emptyLabel != null) _emptyLabel.text = message;
            _emptyState?.RemoveFromClassList("hidden");
        }

        private static readonly string[] StatusAccentClass =
        {
            "sr-row--review", "sr-row--missing", "sr-row--failed", "sr-row--passed"
        };

        private VisualElement BuildRow(StudentEntry entry)
        {
            var row = new VisualElement();
            row.AddToClassList("sr-row");
            row.AddToClassList(StatusAccentClass[(int)entry.Status]);

            var headerRow = new VisualElement();
            headerRow.AddToClassList("sr-row-header");

            var avatar = new VisualElement();
            avatar.AddToClassList("sr-avatar");
            avatar.AddToClassList("sr-row-avatar");
            var avatarLabel = new Label(InitialsFor(entry.Name));
            avatarLabel.AddToClassList("sr-avatar-label");
            avatar.Add(avatarLabel);
            headerRow.Add(avatar);

            var nameCol = new VisualElement();
            nameCol.AddToClassList("sr-row-name-col");

            var student = new Label(entry.Name);
            student.AddToClassList("sr-row-student");
            nameCol.Add(student);

            string tagText;
            string tagClass;
            switch (entry.Status)
            {
                case StudentStatus.Passed: tagText = "Passed"; tagClass = "sr-status-tag--passed"; break;
                case StudentStatus.NotPassed: tagText = "Did not pass"; tagClass = "sr-status-tag--failed"; break;
                case StudentStatus.ToReview: tagText = "To review"; tagClass = "sr-status-tag--submitted"; break;
                default: tagText = "Not submitted"; tagClass = "sr-status-tag--missing"; break;
            }

            var statusTag = new Label(tagText);
            statusTag.AddToClassList("sr-status-tag");
            statusTag.AddToClassList(tagClass);
            nameCol.Add(statusTag);

            headerRow.Add(nameCol);
            row.Add(headerRow);

            if (entry.Status == StudentStatus.NotSubmitted)
            {
                var waiting = new Label("Hasn't uploaded a file yet.");
                waiting.AddToClassList("sr-row-file");
                waiting.AddToClassList("sr-row-file--muted");
                row.Add(waiting);
                return row;
            }

            var primary = entry.Primary;

            var fileRow = new VisualElement();
            fileRow.AddToClassList("sr-row-file-row");
            var fileIcon = new VisualElement();
            fileIcon.AddToClassList("sr-row-file-icon");
            var fileLabel = new Label(primary.FileName);
            fileLabel.AddToClassList("sr-row-file");
            fileRow.Add(fileIcon);
            fileRow.Add(fileLabel);
            row.Add(fileRow);

            string submittedAt = primary.SubmittedAtUtc.ToLocalTime().ToString("MMM d, h:mm tt");
            string scoreText = primary.IsReviewed ? $" \u2022 {ScoreText(primary.Score)}" : string.Empty;

            var meta = new Label($"Attempt {primary.AttemptNumber} \u2022 {FileSubmissionConfig.FormatSize(primary.FileSize)} \u2022 {submittedAt}{scoreText}");
            meta.AddToClassList("sr-row-meta");
            row.Add(meta);

            // A resubmission waiting for review still shows the grade they already have.
            if (entry.Status == StudentStatus.ToReview && entry.BestReviewed != null)
            {
                bool passedBefore = IsPassingScore(entry.BestReviewed.Score);
                var previous = new Label($"Best graded so far: {ScoreText(entry.BestReviewed.Score)} \u2022 {(passedBefore ? "passed" : "not passed")}");
                previous.AddToClassList("sr-row-meta");
                previous.AddToClassList("sr-row-meta--previous");
                row.Add(previous);
            }

            if (_rosterLoaded && !entry.InRoster)
            {
                var left = new Label("No longer in this classroom");
                left.AddToClassList("sr-row-meta");
                left.AddToClassList("sr-row-meta--warning");
                row.Add(left);
            }

            var action = new Button(() => OpenReview(primary))
            {
                text = primary.IsReviewed ? "View / Re-grade" : "Review Submission"
            };
            action.AddToClassList("sr-row-action");
            action.AddToClassList(primary.IsReviewed ? "sr-row-action--secondary" : "sr-row-action--primary");
            row.Add(action);

            // Every attempt stays reachable, not just the newest one.
            if (entry.Submissions.Count > 1)
            {
                var attemptsLabel = new Label($"All attempts ({entry.Submissions.Count})");
                attemptsLabel.AddToClassList("sr-row-meta");
                attemptsLabel.AddToClassList("sr-attempts-heading");
                row.Add(attemptsLabel);

                var attemptsRow = new VisualElement();
                attemptsRow.AddToClassList("sr-attempts-row");

                foreach (var attempt in entry.Submissions)
                {
                    var captured = attempt;
                    string text = attempt.IsReviewed
                        ? $"#{attempt.AttemptNumber} \u2022 {attempt.Score}"
                        : $"#{attempt.AttemptNumber} \u2022 new";

                    var chip = new Button(() => OpenReview(captured)) { text = text };
                    chip.AddToClassList("sr-attempt-chip");
                    if (!attempt.IsReviewed) chip.AddToClassList("sr-attempt-chip--new");
                    attemptsRow.Add(chip);
                }

                row.Add(attemptsRow);
            }

            return row;
        }

        // ==================================================================
        // Review modal
        // ==================================================================

        private void OpenReview(FileSubmissionService.SubmissionRecord submission)
        {
            _reviewing = submission;
            SetReviewError(null);

            if (_reviewStudentLabel != null)
                _reviewStudentLabel.text = string.IsNullOrEmpty(submission.StudentName) ? "Student" : submission.StudentName;
            if (_reviewFileLabel != null) _reviewFileLabel.text = submission.FileName;
            if (_reviewSizeLabel != null) _reviewSizeLabel.text = FileSubmissionConfig.FormatSize(submission.FileSize);
            if (_reviewAttemptLabel != null) _reviewAttemptLabel.text = submission.AttemptNumber.ToString();
            if (_reviewSubmittedLabel != null)
                _reviewSubmittedLabel.text = submission.SubmittedAtUtc.ToLocalTime().ToString("MMMM d, yyyy, h:mm tt");

            if (_scoreFieldLabel != null)
                _scoreFieldLabel.text = _pointsPossible > 0 ? $"Score (out of {_pointsPossible})" : "Score";

            string studentName = string.IsNullOrEmpty(submission.StudentName) ? "Student" : submission.StudentName;
            if (_reviewAvatarLabel != null) _reviewAvatarLabel.text = InitialsFor(studentName);
            if (_reviewAttemptPill != null)
            {
                _reviewAttemptPill.text = submission.IsReviewed ? "Reviewed" : "Pending review";
                _reviewAttemptPill.RemoveFromClassList("sr-modal-attempt-pill--reviewed");
                _reviewAttemptPill.RemoveFromClassList("sr-modal-attempt-pill--pending");
                _reviewAttemptPill.AddToClassList(submission.IsReviewed
                    ? "sr-modal-attempt-pill--reviewed"
                    : "sr-modal-attempt-pill--pending");
            }

            if (_scoreField != null) _scoreField.value = submission.Score.ToString();
            if (_feedbackField != null) _feedbackField.value = submission.Feedback ?? string.Empty;
            UpdateScorePreview();

            _reviewSaveButton?.SetEnabled(true);
            _reviewOverlay?.RemoveFromClassList("hidden");
        }

        private void OnReviewCloseClicked(ClickEvent evt)
        {
            if (_isSaving) return;
            CloseReview();
        }

        private void CloseReview()
        {
            _reviewOverlay?.AddToClassList("hidden");
            _reviewing = null;
        }

        /// <summary>"Open File": downloads the file, then opens it straight in the phone's own
        /// viewer app (Adobe / WPS / Word / Excel / Photos...) - see FileOpener. The viewer app
        /// itself offers Save / Share, so no separate download button is needed. If no viewer
        /// can be launched (iOS, Editor, no app for this file type, FileProvider not set up) it
        /// falls back to the Save As / share sheet.</summary>
        private void OnOpenFileClicked(ClickEvent evt)
        {
            if (NativeFilePicker.IsFilePickerBusy()) return;

            DownloadSubmissionFile(path =>
            {
                if (FileOpener.TryOpen(path, out string openError))
                    return;

                // No viewer app for this file type - tell the teacher why, then fall through.
                if (!string.IsNullOrEmpty(openError))
                    SetReviewError(openError);

                ExportToDevice(path);
            });
        }

        private static void ExportToDevice(string path)
        {
            NativeFilePicker.ExportFile(path, success =>
            {
                if (!success) Debug.Log("[AdminSubmissionReviewController] File export cancelled.");
            });
        }

        /// <summary>Downloads the submission being reviewed into the cache folder and calls
        /// <paramref name="onReady"/> with the local path. Shows the working overlay and reports
        /// any failure in the review dialog, so both file buttons behave the same.</summary>
        private void DownloadSubmissionFile(Action<string> onReady)
        {
            if (_reviewing == null) return;

            if (!NetworkStatusMonitor.IsOnline)
            {
                SetReviewError("You need an internet connection to open this file.");
                return;
            }

            var submission = _reviewing;
            SetReviewError(null);
            ShowWorking("Downloading file...");

            R2FileUploadService.Instance.DownloadSubmission(submission.StorageKey, (ok, error, bytes) =>
            {
                HideWorking();

                if (!ok || bytes == null)
                {
                    SetReviewError(error ?? "Could not download that file.");
                    return;
                }

                string fileName = SafeFileName($"{submission.StudentName}_{submission.FileName}");
                string path = Path.Combine(Application.temporaryCachePath, fileName);

                try
                {
                    File.WriteAllBytes(path, bytes);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AdminSubmissionReviewController] Could not cache the download: {e.Message}");
                    SetReviewError("Could not open that file on this device.");
                    return;
                }

                onReady(path);
            });
        }

        private void OnScoreFieldChanged(ChangeEvent<string> evt) => UpdateScorePreview();

        /// <summary>Live "would this pass?" preview next to the score field, so the
        /// teacher doesn't have to save first to find out.</summary>
        private void UpdateScorePreview()
        {
            if (_scorePreview == null || _scorePreviewLabel == null) return;

            string raw = _scoreField?.value?.Trim();
            _scorePreview.RemoveFromClassList("sr-score-preview--pass");
            _scorePreview.RemoveFromClassList("sr-score-preview--fail");
            _scorePreview.RemoveFromClassList("sr-score-preview--empty");

            if (!int.TryParse(raw, out int score) || score < 0)
            {
                _scorePreviewLabel.text = "\u2013";
                _scorePreview.AddToClassList("sr-score-preview--empty");
                return;
            }

            bool passing = IsPassingScore(score);
            _scorePreviewLabel.text = $"{PercentOf(score):0}% \u2022 {(passing ? "Passing" : "Below passing")}";
            _scorePreview.AddToClassList(passing ? "sr-score-preview--pass" : "sr-score-preview--fail");
        }

        /// <summary>Up to two initials for an avatar circle, e.g. "Maria Cruz" -> "MC".</summary>
        private static string InitialsFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpperInvariant();
        }

        private void OnReviewSaveClicked(ClickEvent evt)
        {
            if (_isSaving || _reviewing == null) return;

            string raw = _scoreField?.value?.Trim();
            if (!int.TryParse(raw, out int score))
            {
                SetReviewError("Enter the score as a whole number.");
                return;
            }

            if (score < 0)
            {
                SetReviewError("Score cannot be negative.");
                return;
            }

            if (_pointsPossible > 0 && score > _pointsPossible)
            {
                SetReviewError($"Score cannot be higher than {_pointsPossible}.");
                return;
            }

            _isSaving = true;
            SetReviewError(null);
            _reviewSaveButton?.SetEnabled(false);
            ShowWorking("Saving review...");

            FileSubmissionService.Instance.ReviewSubmission(
                _reviewing, score, _feedbackField?.value ?? string.Empty,
                _pointsPossible, _passingScorePercent,
                (ok, error, updated) =>
                {
                    _isSaving = false;
                    HideWorking();
                    _reviewSaveButton?.SetEnabled(true);

                    if (!ok)
                    {
                        SetReviewError(error ?? "Could not save this review.");
                        return;
                    }

                    CloseReview();
                    RebuildEntries();
                    RefreshStats();
                    RefreshFilters();
                    RefreshList();
                });
        }

        // ==================================================================
        // Small helpers
        // ==================================================================

        private void OnBackClicked(ClickEvent evt)
        {
            if (_isSaving) return;
            UIManager.Instance.ShowAdminQuizManagement();
        }

        private void SetStatus(string message)
        {
            if (_statusLabel == null) return;

            if (string.IsNullOrEmpty(message))
            {
                _statusLabel.text = string.Empty;
                _statusLabel.AddToClassList("hidden");
                return;
            }

            _statusLabel.text = message;
            _statusLabel.RemoveFromClassList("hidden");
        }

        private void SetReviewError(string message)
        {
            if (_reviewError == null) return;

            if (string.IsNullOrEmpty(message))
            {
                _reviewError.text = string.Empty;
                _reviewError.AddToClassList("hidden");
                return;
            }

            _reviewError.text = message;
            _reviewError.RemoveFromClassList("hidden");
        }

        private void ShowWorking(string message)
        {
            if (_workingLabel != null) _workingLabel.text = message;
            _workingOverlay?.RemoveFromClassList("hidden");
        }

        private void HideWorking() => _workingOverlay?.AddToClassList("hidden");

        private static string SafeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "submission";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        private void ApplyGradients()
        {
            if (_header == null) return;

            if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
            _headerGradientTexture = CreateHorizontalGradient(gradientStart, gradientEnd);
            _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
        }

        private static Texture2D CreateHorizontalGradient(Color start, Color end, int width = 128)
        {
            var texture = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int x = 0; x < width; x++)
            {
                texture.SetPixel(x, 0, Color.Lerp(start, end, x / (float)(width - 1)));
            }

            texture.Apply();
            return texture;
        }
    }
}
