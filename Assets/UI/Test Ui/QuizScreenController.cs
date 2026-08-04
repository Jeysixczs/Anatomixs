using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace AnatomiaApp.UI.Quiz
{
    public enum QuestionType
    {
        MultipleChoice,
        TrueFalse,
        Identification,
        Enumeration,
        MultipleIdentification,
        ImageBased
    }

    [Serializable]
    public class QuizQuestion
    {
        public QuestionType type;
        public string questionText;
        public int points = 10;

        // Multiple Choice / True-False
        public List<string> choices = new List<string>();
        public int correctChoiceIndex;

        // Identification / Image Based
        public string correctAnswer;

        // Enumeration
        public int enumerationCount;

        // Multiple Identification
        public List<string> multiChoices = new List<string>();
        public List<int> correctChoiceIndices = new List<int>();

        // Image Based
        public Sprite image;
        // Normalized (0-1) position of the pointer/arrow over the image frame
        public Vector2 arrowNormalizedPosition = new Vector2(0.4f, 0.35f);
    }

    /// <summary>
    /// Drives a single quiz-taking screen built from QuizScreen.uxml / QuizScreen.uss.
    /// Follows the project's per-panel controller pattern (instantiated / shown by UIManager).
    /// </summary>
    public class QuizScreenController : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private VisualTreeAsset quizScreenAsset;
        [SerializeField] private UIDocument uiDocument;

        [Header("Quiz Data")]
        [SerializeField] private string quizName = "Quiz 1";
        [SerializeField] private List<QuizQuestion> questions = new List<QuizQuestion>();
        [SerializeField] private float secondsPerQuestion = 600f;

        [Header("Gradient Colors")]
        [SerializeField] private Color gradientStart = new Color32(0x0E, 0xA5, 0x8A, 0xFF); // teal/green
        [SerializeField] private Color gradientEnd = new Color32(0x2F, 0x6F, 0xED, 0xFF);   // blue

        public event Action<Dictionary<int, object>> OnQuizSubmitted;

        // --- root elements ---
        private VisualElement _root;
        private VisualElement _header;
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
        private int _currentIndex;
        private float _timeRemaining;
        private bool _timerRunning;
        private readonly Dictionary<int, object> _answers = new Dictionary<int, object>();

        private static readonly string[] QuestionTypeLabels =
        {
            "MULTIPLE CHOICE",
            "TRUE FALSE",
            "IDENTIFICATION",
            "ENUMERATION",
            "MULTIPLE IDENTIFICATION",
            "IMAGE BASED"
        };

        private void Awake()
        {
            if (uiDocument == null)
                uiDocument = GetComponent<UIDocument>();

            if (quizScreenAsset != null)
                uiDocument.visualTreeAsset = quizScreenAsset;

            _root = uiDocument.rootVisualElement;
            CacheElements();
            PaintGradients();
            WireEvents();
        }

        private void OnEnable()
        {
            if (questions != null && questions.Count > 0)
                StartQuiz(questions);
        }

        private void CacheElements()
        {
            var quizRoot = _root.Q<VisualElement>("quiz-root");
            quizRoot.RegisterCallback<GeometryChangedEvent>(OnRootResized);

            _header = _root.Q<VisualElement>("header");
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
            _closeButton.clicked += HandleClose;
            _actionButton.clicked += HandleNextOrSubmit;
        }

        private void OnRootResized(GeometryChangedEvent evt)
        {
            // Toggle the .compact breakpoint class for narrow layouts,
            // matching the project's existing responsive pattern.
            bool compact = evt.newRect.width < 380f;
            _root.Q<VisualElement>("quiz-root").EnableInClassList("compact", compact);
        }

        // ---------------------------------------------------------------
        // Gradient painting (USS has no linear-gradient, so we bake one)
        // ---------------------------------------------------------------

        private void PaintGradients()
        {
            var headerTex = GradientTextureUtility.CreateDiagonalGradient(64, 64, gradientStart, gradientEnd);
            _header.style.backgroundImage = new StyleBackground(Background.FromTexture2D(headerTex));

            var buttonTex = GradientTextureUtility.CreateDiagonalGradient(64, 64, gradientStart, gradientEnd);
            _actionButton.style.backgroundImage = new StyleBackground(Background.FromTexture2D(buttonTex));
        }

        // ---------------------------------------------------------------
        // Quiz lifecycle
        // ---------------------------------------------------------------

        public void StartQuiz(List<QuizQuestion> quizQuestions)
        {
            questions = quizQuestions;
            _currentIndex = 0;
            _answers.Clear();
            _quizLabel.text = quizName;

            RenderQuestion(_currentIndex);
            StartTimer(secondsPerQuestion);
        }

        private void RenderQuestion(int index)
        {
            var q = questions[index];

            _typeLabel.text = QuestionTypeLabels[(int)q.type];
            _questionTextLabel.text = q.questionText;
            _questionCounterLabel.text = $"Question {index + 1} of {questions.Count}";
            _pointsLabel.text = $"{q.points} pts";

            float progress = (index + 1) / (float)questions.Count;
            _progressFill.style.width = new Length(progress * 100f, LengthUnit.Percent);

            bool isLast = index == questions.Count - 1;
            _actionButton.text = isLast ? "Submit Quiz" : "Next Question";
            _actionButton.EnableInClassList("action-button--disabled", isLast && !HasAnswer(index));

            _answerContainer.Clear();

            switch (q.type)
            {
                case QuestionType.MultipleChoice:
                case QuestionType.TrueFalse:
                    BuildSingleSelect(q);
                    break;
                case QuestionType.Identification:
                    BuildIdentification(q);
                    break;
                case QuestionType.Enumeration:
                    BuildEnumeration(q);
                    break;
                case QuestionType.MultipleIdentification:
                    BuildMultiSelect(q);
                    break;
                case QuestionType.ImageBased:
                    BuildImageBased(q);
                    break;
            }
        }

        // ---------------------------------------------------------------
        // Question type builders
        // ---------------------------------------------------------------

        private void BuildSingleSelect(QuizQuestion q)
        {
            int selected = _answers.TryGetValue(_currentIndex, out var stored) ? (int)stored : -1;

            for (int i = 0; i < q.choices.Count; i++)
            {
                int optionIndex = i;
                var row = CreateOptionRow(q.choices[i], isCheckbox: false);
                row.EnableInClassList("option-row--selected", optionIndex == selected);

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    _answers[_currentIndex] = optionIndex;
                    foreach (var child in _answerContainer.Children())
                        child.RemoveFromClassList("option-row--selected");
                    row.AddToClassList("option-row--selected");
                    RefreshActionButtonState();
                });

                _answerContainer.Add(row);
            }
        }

        private void BuildMultiSelect(QuizQuestion q)
        {
            var selected = _answers.TryGetValue(_currentIndex, out var stored)
                ? (HashSet<int>)stored
                : new HashSet<int>();
            _answers[_currentIndex] = selected;

            var helper = new Label("Select multiple correct answers:");
            helper.AddToClassList("helper-text");
            _answerContainer.Add(helper);

            for (int i = 0; i < q.multiChoices.Count; i++)
            {
                int optionIndex = i;
                var row = CreateOptionRow(q.multiChoices[i], isCheckbox: true);
                row.EnableInClassList("option-row--selected", selected.Contains(optionIndex));

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    if (!selected.Remove(optionIndex))
                        selected.Add(optionIndex);
                    row.EnableInClassList("option-row--selected", selected.Contains(optionIndex));
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

        private void BuildIdentification(QuizQuestion q)
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

        private void BuildEnumeration(QuizQuestion q)
        {
            var helper = new Label($"Enter {q.enumerationCount} answers:");
            helper.AddToClassList("helper-text");
            _answerContainer.Add(helper);

            var stored = _answers.TryGetValue(_currentIndex, out var raw)
                ? (string[])raw
                : new string[q.enumerationCount];
            _answers[_currentIndex] = stored;

            for (int i = 0; i < q.enumerationCount; i++)
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

        private void BuildImageBased(QuizQuestion q)
        {
            var frame = new VisualElement();
            frame.AddToClassList("image-frame");

            var image = new VisualElement();
            image.AddToClassList("quiz-image");
            if (q.image != null)
                image.style.backgroundImage = new StyleBackground(q.image);
            frame.Add(image);

            // Optional pointer/arrow overlay, positioned normalized within the frame.
            var arrow = new Label("\u2196");
            arrow.AddToClassList("arrow-marker");
            arrow.style.left = new Length(q.arrowNormalizedPosition.x * 100f, LengthUnit.Percent);
            arrow.style.top = new Length(q.arrowNormalizedPosition.y * 100f, LengthUnit.Percent);
            frame.Add(arrow);

            _answerContainer.Add(frame);

            var label = new Label("Identify the name of this bone");
            label.AddToClassList("question-text");
            label.style.fontSize = 18;
            label.style.marginBottom = 14;
            _answerContainer.Add(label);

            var field = new TextField { multiline = false };
            field.AddToClassList("answer-text-field");
            field.textEdition.placeholder = "Type your answer here based on the image...";
            field.value = _answers.TryGetValue(_currentIndex, out var stored) ? (string)stored : string.Empty;

            field.RegisterValueChangedCallback(evt =>
            {
                _answers[_currentIndex] = evt.newValue;
                RefreshActionButtonState();
            });

            _answerContainer.Add(field);
        }

        // ---------------------------------------------------------------
        // Navigation
        // ---------------------------------------------------------------

        private void RefreshActionButtonState()
        {
            bool isLast = _currentIndex == questions.Count - 1;
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
                case HashSet<int> set:
                    return set.Count > 0;
                case string[] arr:
                    return arr.Any(a => !string.IsNullOrWhiteSpace(a));
                case int i:
                    return i >= 0;
                default:
                    return true;
            }
        }

        private void HandleNextOrSubmit()
        {
            bool isLast = _currentIndex == questions.Count - 1;

            if (isLast)
            {
                SubmitQuiz();
                return;
            }

            _currentIndex++;
            RenderQuestion(_currentIndex);
            StartTimer(secondsPerQuestion);
        }

        private void SubmitQuiz()
        {
            StopTimer();
            OnQuizSubmitted?.Invoke(new Dictionary<int, object>(_answers));
        }

        private void HandleClose()
        {
            // Hook into UIManager's navigation stack / back-button handling here.
            gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------
        // Timer
        // ---------------------------------------------------------------

        private Coroutine _timerRoutine;

        private void StartTimer(float seconds)
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
                HandleNextOrSubmit();
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
    /// Runtime gradient texture generator, matching the project's existing
    /// pattern for compensating USS's lack of linear-gradient support.
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
