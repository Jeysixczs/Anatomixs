using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI.Quiz
{
    /// <summary>
    /// Drives the quiz-taking screen (QuizScreen.uxml / QuizScreen.uss).
    /// Reached from the classroom's "Available Quizzes" tab -> StudentQuizSelectionController
    /// "Start" -> here -> StudentQuizResultController, matching the flow QuizService's
    /// header comment already describes.
    ///
    /// Data comes from QuizService.Instance.FetchQuiz(); the finished attempt is written
    /// via QuizService.Instance.SubmitQuizAttempt().
    ///
    /// Image-based questions are answered with a typed text field, same as
    /// Identification - the difference is the "View on 3D Model" button, which sends
    /// the student to the reused Student Anatomy Screen to see the highlighted
    /// structure (see AnatomyQuizHighlightController) without revealing its name, then
    /// back here via ResumeInProgressQuiz to type the answer.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentQuizGameplayController : MonoBehaviour
    {
        [Header("Gradient Colors")]
        [SerializeField] private Color gradientStart = new Color32(0x0E, 0xA5, 0x8A, 0xFF); // teal/green
        [SerializeField] private Color gradientEnd = new Color32(0x2F, 0x6F, 0xED, 0xFF);   // blue

        /// <summary>Fired once SubmitQuizAttempt() succeeds - hand this to StudentQuizResultController.</summary>
        public event Action<QuizService.AttemptResult> OnQuizSubmitted;
        public event Action OnCloseRequested;

        private UIDocument _document;

        // --- root elements ---
        private VisualElement _root;
        private VisualElement _quizRoot;
        private VisualElement _header;
        private ScrollView _answerScroll;
        private Button _closeButton;
        private Label _timerLabel;
        private Label _pointsLabel;
        private VisualElement _progressFill;
        private Label _questionCounterLabel;
        private Label _typeLabel;
        private Label _quizLabel;
        private Label _questionTextLabel;
        private VisualElement _answerContainer;
        private Button _actionButton;
        private VisualElement _blockedOverlay;
        private Label _blockedMessageLabel;
        private Button _blockedBackButton;

        // --- state ---
        private QuizService.QuizRecord _quiz;
        private int _currentIndex;
        private float _timeRemaining;
        private bool _timerRunning;
        private Coroutine _timerRoutine;

        // Wall-clock time the current attempt started, used to compute timeSpentSeconds
        // for SubmitQuizAttempt (the Scores tab's "Time: Xm Ys" field). realtimeSinceStartup
        // instead of DateTime.UtcNow since it isn't affected by device clock changes and
        // keeps advancing across scenes without needing DontDestroyOnLoad bookkeeping.
        private float _quizStartRealtime;

        // Answer storage per question index. Value type depends on question type:
        // MultipleChoice/TrueFalse/Identification -> string
        // MultipleIdentification -> HashSet<string>
        // Enumeration -> string[]
        private readonly Dictionary<int, object> _answers = new Dictionary<int, object>();

        // Guards against multiple rapid clicks (or a click racing the auto-submit
        // timer) firing SubmitQuizAttempt() more than once for the same attempt.
        private bool _submitInFlight;

        private Texture2D _headerGradientTexture;
        private Texture2D _buttonGradientTexture;

        // Set via LoadQuiz() before the screen finished enabling (shouldn't normally
        // happen given how UIManager.ShowStudentQuizGameplay() calls it, but kept as
        // a safety net so a quiz id is never silently dropped).
        private string _pendingQuizId;
        private string _pendingClassroomId;

        // The classroom the student launched this quiz FROM (StudentClassroomDetail's
        // Available Quizzes tab), as opposed to _quiz.ClassroomId (the quiz doc's own
        // classroomId field, set once at quiz-creation time). These can differ - a quiz
        // can be published into a classroom's publishedQuizIds without its own
        // classroomId field pointing back at that classroom - so this is what actually
        // gets written onto the quizAttempts doc (see SubmitQuizAttempt call below),
        // otherwise Student Classroom Detail's Scores tab query (classroomId + studentId)
        // never matches and the Scores tab looks permanently empty.
        private string _launchClassroomId;

        private static readonly Dictionary<string, string> TypeLabels = new Dictionary<string, string>
        {
            { QuestionTypeSlugs.MultipleChoice, "MULTIPLE CHOICE" },
            { QuestionTypeSlugs.TrueFalse, "TRUE FALSE" },
            { QuestionTypeSlugs.Identification, "IDENTIFICATION" },
            { QuestionTypeSlugs.Enumeration, "ENUMERATION" },
            { QuestionTypeSlugs.MultipleIdentification, "MULTIPLE IDENTIFICATION" },
            { QuestionTypeSlugs.ImageBased, "IMAGE BASED" }
        };

        private void OnEnable()
        {
            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            // Screens are cloned onto UIManager's shared root (see UIManager.ShowScreen),
            // so resolve the root from there rather than from this component's own document.
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
                Debug.LogError("[StudentQuizGameplayController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            CacheElements();
            PaintGradients();
            WireEvents();

            if (!string.IsNullOrEmpty(_pendingQuizId))
            {
                string quizId = _pendingQuizId;
                string classroomId = _pendingClassroomId;
                _pendingQuizId = null;
                _pendingClassroomId = null;
                LoadQuiz(classroomId, quizId);
            }
        }

        private void OnDisable()
        {
            // Navigating to any other screen clears the visual tree but does not stop
            // a running coroutine on its own - without this the countdown (and a stray
            // auto-submit) would keep firing in the background after leaving the quiz.
            StopTimer();
            UnregisterCallbacks();

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }

            if (_buttonGradientTexture != null)
            {
                Destroy(_buttonGradientTexture);
                _buttonGradientTexture = null;
            }
        }

        private void UnregisterCallbacks()
        {
            _closeButton?.UnregisterCallback<ClickEvent>(OnCloseClicked);
            _actionButton?.UnregisterCallback<ClickEvent>(OnActionButtonClicked);
            _blockedBackButton?.UnregisterCallback<ClickEvent>(OnBlockedBackClicked);
            _quizRoot?.UnregisterCallback<GeometryChangedEvent>(OnRootResized);
        }

        private void CacheElements()
        {
            _quizRoot = _root.Q<VisualElement>("quiz-root");

            _header = _root.Q<VisualElement>("header");
            _answerScroll = _root.Q<ScrollView>("answer-scroll");
            if (_answerScroll != null)
            {
                // Belt-and-suspenders alongside the UXML "touch-scroll-type=Clamped"
                // attribute: with Elastic (the default) the view can rubber-band up
                // and down even when the answers already fit, which is what made it
                // look scrollable when it shouldn't be. Clamped means the scroll
                // range is 0 (and therefore un-draggable) whenever the answer list
                // fits within the card, and only becomes draggable once the options
                // genuinely overflow (e.g. a long Enumeration/Multiple Identification
                // list). The card itself (question text, type label) never scrolls -
                // only this inner view, which wraps just the answer options.
                _answerScroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
            }
            _closeButton = _root.Q<Button>("close-button");
            _timerLabel = _root.Q<Label>("timer-label");
            _pointsLabel = _root.Q<Label>("points-label");
            _progressFill = _root.Q<VisualElement>("progress-fill");
            _questionCounterLabel = _root.Q<Label>("question-counter-label");
            _typeLabel = _root.Q<Label>("type-label");
            _quizLabel = _root.Q<Label>("quiz-label");
            _questionTextLabel = _root.Q<Label>("question-text-label");
            _answerContainer = _root.Q<VisualElement>("answer-container");
            _actionButton = _root.Q<Button>("action-button");
            _blockedOverlay = _root.Q<VisualElement>("blocked-overlay");
            _blockedMessageLabel = _root.Q<Label>("blocked-message-label");
            _blockedBackButton = _root.Q<Button>("blocked-back-button");
        }

        private void WireEvents()
        {
            _quizRoot?.RegisterCallback<GeometryChangedEvent>(OnRootResized);
            _closeButton?.RegisterCallback<ClickEvent>(OnCloseClicked);
            _actionButton?.RegisterCallback<ClickEvent>(OnActionButtonClicked);
            _blockedBackButton?.RegisterCallback<ClickEvent>(OnBlockedBackClicked);
            _blockedOverlay?.AddToClassList("hidden");
        }

        private void OnBlockedBackClicked(ClickEvent evt)
        {
            StopTimer();
            OnCloseRequested?.Invoke();
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnCloseClicked(ClickEvent evt)
        {
            StopTimer();
            OnCloseRequested?.Invoke();

            // _launchClassroomId is now available here (see LoadQuiz), but
            // ShowStudentClassroomDetail also needs the classroom's display name and
            // instructor name to render its header, and neither is threaded through
            // to this screen today. Wire those through LoadQuiz()/_pendingClassroomId
            // alongside classroomId if you want this to return to
            // StudentClassroomDetail instead of the dashboard.
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnActionButtonClicked(ClickEvent evt) => HandleNextOrSubmit();

        private void OnRootResized(GeometryChangedEvent evt)
        {
            bool compact = evt.newRect.width < 380f;
            _quizRoot?.EnableInClassList("compact", compact);
        }

        private void PaintGradients()
        {
            if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
            _headerGradientTexture = GradientTextureUtility.CreateDiagonalGradient(64, 64, gradientStart, gradientEnd);
            if (_header != null) _header.style.backgroundImage = new StyleBackground(Background.FromTexture2D(_headerGradientTexture));

            if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
            _buttonGradientTexture = GradientTextureUtility.CreateDiagonalGradient(64, 64, gradientStart, gradientEnd);
            if (_actionButton != null) _actionButton.style.backgroundImage = new StyleBackground(Background.FromTexture2D(_buttonGradientTexture));
        }

        // ---------------------------------------------------------------
        // Loading a quiz from Firestore
        // ---------------------------------------------------------------

        /// <summary>Call from UIManager.ShowStudentQuizGameplay() once this screen is showing.
        /// <paramref name="classroomId"/> is the classroom the student launched this quiz FROM
        /// (StudentClassroomDetail's Available Quizzes tab) - NOT necessarily the same as the
        /// quiz doc's own classroomId field. It's what gets attached to the quizAttempts doc on
        /// submit, so Student Classroom Detail's Scores tab can find it again. Pass null/empty
        /// for entry points with no classroom context (e.g. a future deep link).</summary>
        public void LoadQuiz(string classroomId, string quizId)
        {
            if (_root == null)
            {
                // Screen hasn't finished enabling yet - remember this and load once it has.
                _pendingClassroomId = classroomId;
                _pendingQuizId = quizId;
                return;
            }

            _launchClassroomId = classroomId;

            // _blockedOverlay?.AddToClassList("hidden");

            // Single choke point for the "Student Restrictions" checks - every entry point
            // into gameplay (classroom detail's Start button, deep links, etc.) routes
            // through LoadQuiz(), so enforcing the deadline/attempts check here means it
            // can't be bypassed by skipping some other screen's pre-check.
            QuizService.Instance.CheckAttemptEligibility(quizId, (checkOk, checkError, eligibility) =>
            {
                //if (!checkOk)
                //{
                //    ShowBlocked(checkError ?? "Could not check this quiz right now. Please try again.");
                //    return;
                //}

                //if (!eligibility.CanStart)
                //{
                //    ShowBlocked(eligibility.BlockReason);
                //    return;
                //}

                QuizService.Instance.FetchQuiz(quizId, (success, error, record) =>
                {
                    if (!success)
                    {
                        Debug.LogError($"[QuizGameplay] {error}");
                        return;
                    }

                    StartQuiz(record);
                });
            });
        }

        private void StartQuiz(QuizService.QuizRecord quiz)
        {
            _quiz = quiz;
            _currentIndex = 0;
            _answers.Clear();
            _submitInFlight = false;
            _quizStartRealtime = Time.realtimeSinceStartup;
            _quizLabel.text = quiz.Title;

            RenderQuestion(_currentIndex);

            // TimeLimitMinutes is for the whole quiz, not per question - the countdown
            // runs continuously from the first question through submission. HasTimeLimit
            // == false means "No Time Limit" was picked in the admin form - skip the
            // countdown entirely and show a static label instead.
            if (quiz.HasTimeLimit)
            {
                int seconds = Mathf.Max(1, quiz.TimeLimitMinutes) * 60;
                StartTimer(seconds);
            }
            else
            {
                StopTimer();
                if (_timerLabel != null) _timerLabel.text = "No limit";
            }
        }

        private void RenderQuestion(int index)
        {
            var q = _quiz.Questions[index];

            _typeLabel.text = TypeLabels.TryGetValue(q.QuestionTypeSlug, out var label) ? label : q.QuestionTypeSlug.ToUpperInvariant();
            _questionTextLabel.text = q.QuestionText;
            _questionCounterLabel.text = $"Question {index + 1} of {_quiz.Questions.Count}";
            _pointsLabel.text = $"{q.Points} pts";

            float progress = (index + 1) / (float)_quiz.Questions.Count;
            _progressFill.style.width = new Length(progress * 100f, LengthUnit.Percent);

            bool isLast = index == _quiz.Questions.Count - 1;
            _actionButton.text = isLast ? "Submit Quiz" : "Next Question";
            RefreshActionButtonState();

            _answerContainer.Clear();
            BuildAnswerUi(q);
        }

        // ---------------------------------------------------------------
        // Answer UI per question type
        // ---------------------------------------------------------------

        private void BuildAnswerUi(QuizService.QuestionRecord q)
        {
            switch (q.QuestionTypeSlug)
            {
                case QuestionTypeSlugs.MultipleChoice:
                case QuestionTypeSlugs.TrueFalse:
                    BuildSingleSelect(q);
                    break;
                case QuestionTypeSlugs.Identification:
                    BuildIdentification();
                    break;
                case QuestionTypeSlugs.Enumeration:
                    BuildEnumeration(q);
                    break;
                case QuestionTypeSlugs.MultipleIdentification:
                    BuildMultiSelect(q);
                    break;
                case QuestionTypeSlugs.ImageBased:
                    BuildImageBased(q);
                    break;
                default:
                    Debug.LogWarning($"[QuizGameplay] Unknown question type slug '{q.QuestionTypeSlug}'.");
                    break;
            }
        }

        private static readonly List<string> TrueFalseChoices = new List<string> { "True", "False" };

        private void BuildSingleSelect(QuizService.QuestionRecord q)
        {
            string selected = _answers.TryGetValue(_currentIndex, out var stored) ? (string)stored : null;

            // The admin form only collects Options for Multiple Choice / Multiple
            // Identification - True/False has no options saved, so those two choices
            // are hardcoded here instead of reading an always-empty list.
            var choices = q.QuestionTypeSlug == QuestionTypeSlugs.TrueFalse ? TrueFalseChoices : q.Options;

            foreach (var choice in choices)
            {
                var row = CreateOptionRow(choice, isCheckbox: false);
                row.EnableInClassList("option-row--selected", choice == selected);

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    _answers[_currentIndex] = choice;
                    foreach (var child in _answerContainer.Children())
                        child.RemoveFromClassList("option-row--selected");
                    row.AddToClassList("option-row--selected");
                    RefreshActionButtonState();
                });

                _answerContainer.Add(row);
            }
        }

        private void BuildMultiSelect(QuizService.QuestionRecord q)
        {
            var selected = _answers.TryGetValue(_currentIndex, out var stored)
                ? (HashSet<string>)stored
                : new HashSet<string>();
            _answers[_currentIndex] = selected;

            var helper = new Label("Select multiple correct answers:");
            helper.AddToClassList("helper-text");
            _answerContainer.Add(helper);

            foreach (var choice in q.Options)
            {
                var row = CreateOptionRow(choice, isCheckbox: true);
                row.EnableInClassList("option-row--selected", selected.Contains(choice));

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    if (!selected.Remove(choice))
                        selected.Add(choice);
                    row.EnableInClassList("option-row--selected", selected.Contains(choice));
                    RefreshActionButtonState();
                });

                _answerContainer.Add(row);
            }
        }

        private VisualElement CreateOptionRow(string label, bool isCheckbox)
        {
            var row = new VisualElement();
            row.AddToClassList("option-row");

            if (isCheckbox)
            {
                var box = new VisualElement();
                box.AddToClassList("checkbox-outer");
                var check = new Label("\u2713");
                check.AddToClassList("checkbox-check");
                box.Add(check);
                row.Add(box);
            }
            else
            {
                var outer = new VisualElement();
                outer.AddToClassList("radio-outer");
                var inner = new VisualElement();
                inner.AddToClassList("radio-inner");
                outer.Add(inner);
                row.Add(outer);
            }

            var text = new Label(label);
            text.AddToClassList("option-label");
            row.Add(text);

            return row;
        }

        private void BuildIdentification()
        {
            var field = new TextField { multiline = false };
            field.AddToClassList("answer-text-field");
            field.textEdition.placeholder = "Type your answer here...";
            field.value = _answers.TryGetValue(_currentIndex, out var stored) ? (string)stored : string.Empty;

            field.RegisterValueChangedCallback(evt =>
            {
                _answers[_currentIndex] = evt.newValue;
                RefreshActionButtonState();
            });

            _answerContainer.Add(field);
        }

        private void BuildEnumeration(QuizService.QuestionRecord q)
        {
            // QuestionRecord has no dedicated "how many blanks" field - Correct Answer
            // is one comma-separated TextField in AdminQuizManagementController (see
            // UpdateCorrectAnswerHint there), so the blank count is inferred from it.
            int count = SplitDelimited(q.CorrectAnswer).Length;
            if (count <= 0) count = 4;

            var helper = new Label($"Enter {count} answers:");
            helper.AddToClassList("helper-text");
            _answerContainer.Add(helper);

            var stored = _answers.TryGetValue(_currentIndex, out var raw)
                ? (string[])raw
                : new string[count];
            _answers[_currentIndex] = stored;

            for (int i = 0; i < count; i++)
            {
                int slot = i;
                var field = new TextField { multiline = false };
                field.AddToClassList("answer-text-field");
                field.textEdition.placeholder = $"Answer {i + 1}";
                field.value = stored[slot] ?? string.Empty;

                field.RegisterValueChangedCallback(evt =>
                {
                    stored[slot] = evt.newValue;
                    RefreshActionButtonState();
                });

                _answerContainer.Add(field);
            }
        }

        /// <summary>"View on 3D Model" + a typed-answer field. The 3D view lives on the
        /// reused Student Anatomy Screen (see AnatomyQuizHighlightController), never
        /// embedded here - this screen is 2D UI Toolkit only. The button is a round
        /// trip: leaving and coming back resumes this exact attempt via
        /// UIManager.ShowStudentAnatomyScreenForQuizHighlight / ShowStudentQuizGameplayResume,
        /// it never restarts the quiz.</summary>
        private void BuildImageBased(QuizService.QuestionRecord q)
        {
            var viewButton = new Button(() => OnViewOnModelClicked(q)) { text = "🦴  View on 3D Model" };
            viewButton.AddToClassList("view-model-button");
            _answerContainer.Add(viewButton);

            var helper = new Label("Tap above to see the highlighted structure, then type its name below.");
            helper.AddToClassList("helper-text");
            _answerContainer.Add(helper);

            var field = new TextField { multiline = false };
            field.AddToClassList("answer-text-field");
            field.textEdition.placeholder = "Type the structure's name...";
            field.value = _answers.TryGetValue(_currentIndex, out var stored) ? (string)stored : string.Empty;

            field.RegisterValueChangedCallback(evt =>
            {
                _answers[_currentIndex] = evt.newValue;
                RefreshActionButtonState();
            });

            _answerContainer.Add(field);
        }

        private void OnViewOnModelClicked(QuizService.QuestionRecord q)
        {
            if (string.IsNullOrEmpty(q.AnatomySystemKey) || string.IsNullOrEmpty(q.StructureKey))
            {
                Debug.LogWarning("[QuizGameplay] Image-based question is missing AnatomySystemKey/StructureKey - " +
                                  "cannot open the 3D model. This question may have been saved before that metadata existed.");
                return;
            }

            if (!Enum.TryParse<AnatomySystem>(q.AnatomySystemKey, out var system))
            {
                Debug.LogWarning($"[QuizGameplay] Unknown anatomy system '{q.AnatomySystemKey}' on an Image-Based question.");
                return;
            }

            UIManager.Instance.ShowStudentAnatomyScreenForQuizHighlight(system, q.StructureKey);
        }

        // ---------------------------------------------------------------
        // Navigation / submit
        // ---------------------------------------------------------------

        private void RefreshActionButtonState()
        {
            bool isLast = _currentIndex == _quiz.Questions.Count - 1;
            _actionButton.EnableInClassList("action-button--disabled", isLast && !HasAnswer(_currentIndex));
        }

        private bool HasAnswer(int index)
        {
            if (!_answers.TryGetValue(index, out var value) || value == null)
                return false;

            switch (value)
            {
                case string s:
                    return !string.IsNullOrWhiteSpace(s);
                case HashSet<string> set:
                    return set.Count > 0;
                case string[] arr:
                    return arr.Any(a => !string.IsNullOrWhiteSpace(a));
                default:
                    return true;
            }
        }

        private void HandleNextOrSubmit()
        {
            bool isLast = _currentIndex == _quiz.Questions.Count - 1;

            if (isLast)
            {
                SubmitQuiz();
                return;
            }

            _currentIndex++;
            RenderQuestion(_currentIndex);
        }

        private void SubmitQuiz()
        {
            // First click (or the auto-submit timer) wins - ignore any further
            // clicks that land before SubmitQuizAttempt()'s callback comes back.
            if (_submitInFlight) return;
            _submitInFlight = true;

            if (_actionButton != null)
            {
                _actionButton.SetEnabled(false);
                _actionButton.AddToClassList("action-button--disabled");
            }

            StopTimer();

            int correctCount = 0;
            int incorrectCount = 0;
            int pointsEarned = 0;

            // Per-question right/wrong breakdown - feeds AdminAnalyticsReportsController's
            // "Common Incorrect Answers" list via QuizService.FetchClassroomReportData.
            // Without this, that list stays empty no matter how many attempts exist.
            var questionResults = new List<QuizService.QuestionAttemptResult>(_quiz.Questions.Count);

            for (int i = 0; i < _quiz.Questions.Count; i++)
            {
                var q = _quiz.Questions[i];

                _answers.TryGetValue(i, out var answer);
                bool isCorrect = IsAnswerCorrect(q, answer);
                if (isCorrect)
                {
                    correctCount++;
                    pointsEarned += q.Points;
                }
                else
                {
                    incorrectCount++;
                }

                questionResults.Add(new QuizService.QuestionAttemptResult(q.QuestionText, isCorrect));
            }

            string quizTitle = _quiz.Title;
            int pointsPossible = _quiz.PointsPossible;
            int timeSpentSeconds = Mathf.Max(0, Mathf.RoundToInt(Time.realtimeSinceStartup - _quizStartRealtime));

            // Prefer the classroom the student actually launched this quiz from
            // (_launchClassroomId) over the quiz doc's own classroomId field
            // (_quiz.ClassroomId) - a quiz can be published into a classroom's
            // publishedQuizIds without its own classroomId field pointing back at
            // that classroom, and it's this value that Student Classroom Detail's
            // Scores tab later queries by. Fall back to _quiz.ClassroomId only for
            // entry points that never had classroom context to begin with.
            string classroomIdForAttempt = !string.IsNullOrEmpty(_launchClassroomId)
                ? _launchClassroomId
                : _quiz.ClassroomId;

            QuizService.Instance.SubmitQuizAttempt(
                _quiz.QuizId,
                _quiz.Title,
                _quiz.Category,
                classroomIdForAttempt,
                correctCount,
                incorrectCount,
                pointsEarned,
                _quiz.PointsPossible,
                bonusXp: 0,
                (success, error, result) =>
                {
                    if (!success)
                    {
                        Debug.LogError($"[QuizGameplay] {error}");

                        // Submission failed (e.g. network hiccup) - let the student try
                        // again instead of leaving the button permanently disabled.
                        _submitInFlight = false;
                        if (_actionButton != null)
                        {
                            _actionButton.SetEnabled(true);
                            _actionButton.RemoveFromClassList("action-button--disabled");
                        }
                        return;
                    }

                    OnQuizSubmitted?.Invoke(result);

                    PlayerSessionManager.Instance.RefreshCurrentStudent(_ =>
                    {
                        UIManager.Instance.ShowStudentQuizResult(
                            quizTitle, correctCount, incorrectCount, pointsEarned, pointsPossible, bonusXp: 0);
                    });

                },
                questionResults: questionResults,
                timeSpentSeconds: timeSpentSeconds);
        }

        // ---------------------------------------------------------------
        // Scoring
        // ---------------------------------------------------------------

        /// <summary>
        /// CorrectAnswer encoding, matching AdminQuizManagementController's single
        /// Correct Answer TextField:
        ///  - MultipleChoice: matches one of Options verbatim.
        ///  - TrueFalse: "True" or "False" (Options is hardcoded client-side, see
        ///    BuildSingleSelect - the admin form never saves options for this type).
        ///  - Identification: free text, compared case-insensitively/trimmed.
        ///  - Enumeration / MultipleIdentification: comma-separated list of correct
        ///    values, e.g. "Skin, Hair, Nails" - see UpdateCorrectAnswerHint in
        ///    AdminQuizManagementController for the exact placeholder shown to admins.
        ///  - ImageBased: free text against CorrectAnswer, all whitespace stripped and
        ///    case-insensitive (see NormalizeForComparison) - CorrectAnswer is always
        ///    the picked structure's displayName (never its internal StructureKey),
        ///    set by AdminQuizManagementController's teacher structure picker.
        /// </summary>
        private static bool IsAnswerCorrect(QuizService.QuestionRecord q, object answer)
        {
            switch (q.QuestionTypeSlug)
            {
                case QuestionTypeSlugs.MultipleChoice:
                case QuestionTypeSlugs.TrueFalse:
                    return answer is string chosen &&
                           string.Equals(chosen.Trim(), q.CorrectAnswer?.Trim(), StringComparison.OrdinalIgnoreCase);

                case QuestionTypeSlugs.Identification:
                    return answer is string typed &&
                           string.Equals(typed.Trim(), q.CorrectAnswer?.Trim(), StringComparison.OrdinalIgnoreCase);

                case QuestionTypeSlugs.ImageBased:
                    // Structure display names sometimes get typed back with extra/odd
                    // spacing ("Left  Femur", a stray trailing space, etc.) - collapse
                    // all whitespace runs to a single space (in addition to trimming
                    // and ignoring case) before comparing, so that never costs a
                    // student a correct answer.
                    return answer is string imageBasedTyped &&
                           string.Equals(NormalizeForComparison(imageBasedTyped), NormalizeForComparison(q.CorrectAnswer), StringComparison.OrdinalIgnoreCase);

                case QuestionTypeSlugs.Enumeration:
                    {
                        if (!(answer is string[] entries)) return false;
                        var expected = new HashSet<string>(SplitDelimited(q.CorrectAnswer), StringComparer.OrdinalIgnoreCase);
                        var given = new HashSet<string>(entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()), StringComparer.OrdinalIgnoreCase);
                        return expected.Count > 0 && expected.SetEquals(given);
                    }

                case QuestionTypeSlugs.MultipleIdentification:
                    {
                        if (!(answer is HashSet<string> selected)) return false;
                        var expected = new HashSet<string>(SplitDelimited(q.CorrectAnswer), StringComparer.OrdinalIgnoreCase);
                        return expected.Count > 0 && expected.SetEquals(selected);
                    }

                default:
                    return false;
            }
        }

        private static string[] SplitDelimited(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            return raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        // Strips ALL whitespace (not just collapsing runs of it) before comparing, so
        // "Frontal Bone", "frontalbone", and "Frontal  Bone " all compare equal -
        // spacing shouldn't be what costs a student a correct answer here. Used for
        // Image-Based answer matching - see IsAnswerCorrect.
        private static string NormalizeForComparison(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            return Regex.Replace(raw, @"\s+", "");
        }

        // ---------------------------------------------------------------
        // Timer (single countdown for the whole quiz, per QuizRecord.TimeLimitMinutes)
        // ---------------------------------------------------------------

        /// <summary>Repaints the current question and resumes the countdown on the SAME
        /// in-progress attempt - called via UIManager.ShowStudentQuizGameplayResume when
        /// returning from the Anatomy Screen's "View on 3D Model" round trip. Deliberately
        /// does nothing (not even log) if there's no attempt in progress, since a resume
        /// call always follows an active LoadQuiz in normal use; the guard just protects
        /// against a stray call reaching here first.</summary>
        public void ResumeInProgressQuiz()
        {
            if (_quiz == null) return;

            RenderQuestion(_currentIndex);

            if (_quiz.HasTimeLimit && _timeRemaining > 0f && !_timerRunning)
            {
                _timerRunning = true;
                _timerRoutine = StartCoroutine(TimerLoop());
            }
        }

        private void StartTimer(int seconds)
        {
            StopTimer();
            _timeRemaining = seconds;
            _timerRunning = true;
            _timerRoutine = StartCoroutine(TimerLoop());
        }

        private void StopTimer()
        {
            _timerRunning = false;
            if (_timerRoutine != null)
            {
                StopCoroutine(_timerRoutine);
                _timerRoutine = null;
            }
        }

        private IEnumerator TimerLoop()
        {
            while (_timerRunning && _timeRemaining > 0f)
            {
                UpdateTimerLabel(_timeRemaining);
                yield return new WaitForSeconds(1f);
                _timeRemaining -= 1f;
            }

            if (_timerRunning)
            {
                UpdateTimerLabel(0f);
                SubmitQuiz(); // time's up - auto-submit whatever was answered
            }
        }

        private void UpdateTimerLabel(float seconds)
        {
            int totalSeconds = Mathf.CeilToInt(Mathf.Max(0f, seconds));
            int minutes = totalSeconds / 60;
            int secs = totalSeconds % 60;
            _timerLabel.text = $"{minutes}:{secs:00}";
        }

        private void OnDestroy()
        {
            StopTimer();
        }
    }

    /// <summary>
    /// Runtime gradient texture generator, matching the project's existing pattern
    /// for compensating USS's lack of linear-gradient support.
    /// </summary>
    public static class GradientTextureUtility
    {
        public static Texture2D CreateDiagonalGradient(int width, int height, Color start, Color end)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float t = (x / (float)(width - 1) + y / (float)(height - 1)) * 0.5f;
                    pixels[y * width + x] = Color.Lerp(start, end, t);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}