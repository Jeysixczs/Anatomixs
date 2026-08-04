using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// Image-based questions are intentionally skipped here - Jeysi has a separate plan
    /// for that type, so this screen leaves a placeholder and never scores it.
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

        // --- state ---
        private QuizService.QuizRecord _quiz;
        private int _currentIndex;
        private float _timeRemaining;
        private bool _timerRunning;
        private Coroutine _timerRoutine;

        // Answer storage per question index. Value type depends on question type:
        // MultipleChoice/TrueFalse/Identification -> string
        // MultipleIdentification -> HashSet<string>
        // Enumeration -> string[]
        private readonly Dictionary<int, object> _answers = new Dictionary<int, object>();

        private Texture2D _headerGradientTexture;
        private Texture2D _buttonGradientTexture;

        // Set via LoadQuiz() before the screen finished enabling (shouldn't normally
        // happen given how UIManager.ShowStudentQuizGameplay() calls it, but kept as
        // a safety net so a quiz id is never silently dropped).
        private string _pendingQuizId;

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
                _pendingQuizId = null;
                LoadQuiz(quizId);
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
        }

        private void WireEvents()
        {
            _quizRoot?.RegisterCallback<GeometryChangedEvent>(OnRootResized);
            _closeButton?.RegisterCallback<ClickEvent>(OnCloseClicked);
            _actionButton?.RegisterCallback<ClickEvent>(OnActionButtonClicked);
        }

        private void OnCloseClicked(ClickEvent evt)
        {
            StopTimer();
            OnCloseRequested?.Invoke();

            // TODO: if you thread the originating classroom id through LoadQuiz(),
            // prefer sending the student back there instead of the dashboard.
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

        /// <summary>Call from UIManager.ShowStudentQuizGameplay() once this screen is showing.</summary>
        public void LoadQuiz(string quizId)
        {
            if (_root == null)
            {
                // Screen hasn't finished enabling yet - remember this and load once it has.
                _pendingQuizId = quizId;
                return;
            }

            QuizService.Instance.FetchQuiz(quizId, (success, error, record) =>
            {
                if (!success)
                {
                    Debug.LogError($"[QuizGameplay] {error}");
                    return;
                }

                StartQuiz(record);
            });
        }

        private void StartQuiz(QuizService.QuizRecord quiz)
        {
            _quiz = quiz;
            _currentIndex = 0;
            _answers.Clear();
            _quizLabel.text = quiz.Title;

            RenderQuestion(_currentIndex);

            // TimeLimitSeconds is for the whole quiz, not per question - the countdown
            // runs continuously from the first question through submission.
            StartTimer(quiz.TimeLimitSeconds > 0 ? quiz.TimeLimitSeconds : 600);
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
                    // TODO(Jeysi): image-based question type is handled by a separate
                    // flow - not built here. Leaving a placeholder so the screen still
                    // renders something sane if one slips into a quiz.
                    var placeholder = new Label("Image-based question - handled separately.");
                    placeholder.AddToClassList("helper-text");
                    _answerContainer.Add(placeholder);
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
            StopTimer();

            int correctCount = 0;
            int incorrectCount = 0;
            int pointsEarned = 0;

            for (int i = 0; i < _quiz.Questions.Count; i++)
            {
                var q = _quiz.Questions[i];
                if (q.QuestionTypeSlug == QuestionTypeSlugs.ImageBased)
                    continue; // scored separately, not part of this flow

                _answers.TryGetValue(i, out var answer);
                if (IsAnswerCorrect(q, answer))
                {
                    correctCount++;
                    pointsEarned += q.Points;
                }
                else
                {
                    incorrectCount++;
                }
            }

            string quizTitle = _quiz.Title;
            int pointsPossible = _quiz.PointsPossible;

            QuizService.Instance.SubmitQuizAttempt(
                _quiz.QuizId,
                _quiz.Title,
                _quiz.Category,
                _quiz.ClassroomId,
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
                        return;
                    }

                    OnQuizSubmitted?.Invoke(result);

                    UIManager.Instance.ShowStudentQuizResult(
                        quizTitle, correctCount, incorrectCount, pointsEarned, pointsPossible, bonusXp: 0);
                });
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

        // ---------------------------------------------------------------
        // Timer (single countdown for the whole quiz, per QuizRecord.TimeLimitSeconds)
        // ---------------------------------------------------------------

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