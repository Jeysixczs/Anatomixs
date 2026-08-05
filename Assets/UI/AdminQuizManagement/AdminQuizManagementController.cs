using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminQuizManagement.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the "New Quiz" header button
    ///  - Stats row: Total Quizzes / Total Questions / Categories, recomputed
    ///    whenever a quiz or question is added or removed
    ///  - Renders each quiz as an expandable/collapsible card (chevron toggle)
    ///    with a delete (trash) button, and - when expanded - its question
    ///    list with an "Add Question" button
    ///  - "New Quiz" opens a modal (title/category/time limit/passing score)
    ///  - "Add Question" opens a modal (question text, question type dropdown,
    ///    the 4 option fields shown for Multiple Choice AND Multiple
    ///    Identification (distractors like "keyboard" need somewhere to live),
    ///    correct answer, difficulty dropdown, points). Correct Answer is a
    ///    single text field for every type - Enumeration and Multiple
    ///    Identification expect a comma-separated list there (see
    ///    UpdateCorrectAnswerHint), True/False expects literally "True" or
    ///    "False".
    ///  - Each question row shows its Q# / difficulty / type badges, matching
    ///    the mock, with its own delete button
    ///  - Applies the green->blue gradient at runtime to the header, "New
    ///    Quiz" button, "Add Question" buttons and the two modal submit
    ///    buttons (USS has no linear-gradient)
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///
    /// Backed by QuizService (Firestore `quizzes/{quizId}` docs). Quizzes
    /// created here have `classroomId == null`, i.e. they land in the shared
    /// quiz bank available to every classroom (see QuizService.CreateQuiz).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminQuizManagementController : MonoBehaviour
    {
        private const string TypeMultipleChoice = "multiple-choice";
        private const string TypeTrueFalse = "true-false";
        private const string TypeIdentification = "identification";
        private const string TypeEnumeration = "enumeration";
        private const string TypeMultipleIdentification = "multiple-identification";
        private const string TypeImageBased = "image-based";

        private static readonly List<string> QuestionTypeDisplayChoices = new List<string>
        {
            "Multiple Choice", "True or False", "Identification",
            "Enumeration", "Multiple Identification", "Image Based"
        };

        private static readonly Dictionary<string, string> QuestionTypeDisplayToSlug = new Dictionary<string, string>
        {
            { "Multiple Choice", TypeMultipleChoice },
            { "True or False", TypeTrueFalse },
            { "Identification", TypeIdentification },
            { "Enumeration", TypeEnumeration },
            { "Multiple Identification", TypeMultipleIdentification },
            { "Image Based", TypeImageBased },
        };

        private static readonly List<string> DifficultyDisplayChoices = new List<string> { "Easy", "Medium", "Hard" };

        /// <summary>Plain data for a single question belonging to a quiz.</summary>
        public class QuestionData
        {
            public string QuestionText;
            public string QuestionTypeSlug; // e.g. "multiple-choice"
            public List<string> Options = new List<string>();
            public string CorrectAnswer;
            public string Difficulty; // "easy" | "medium" | "hard"
            public int Points;
        }

        /// <summary>Plain data for a single quiz, including its questions.</summary>
        public class QuizData
        {
            /// <summary>Firestore doc id. Empty until CreateQuiz() returns.</summary>
            public string QuizId = "";
            public string Title;
            public string Category;
            public int PassingScorePercent;

            /// <summary>0 = unlimited attempts.</summary>
            public int MaxAttempts;
            public int TimeLimitMinutes;
            public bool HasTimeLimit;
            public bool IsDeadlineEnabled;
            public DateTime? DeadlineUtc;

            public List<QuestionData> Questions = new List<QuestionData>();
            public bool IsExpanded;
        }

        // Preset minute choices shown in the Time Limit dropdown, plus "Custom" and "No Time Limit".
        private const string TimeLimitCustomChoice = "Custom";
        private const string TimeLimitNoLimitChoice = "No Time Limit";
        private static readonly List<int> TimeLimitPresetMinutes = new List<int> { 1, 5, 10, 15, 20, 30, 45, 60 };
        private static readonly List<string> TimeLimitDisplayChoices = BuildTimeLimitChoices();

        private static List<string> BuildTimeLimitChoices()
        {
            var choices = new List<string>();
            foreach (var m in TimeLimitPresetMinutes) choices.Add($"{m} minute{(m == 1 ? "" : "s")}");
            choices.Add(TimeLimitCustomChoice);
            choices.Add(TimeLimitNoLimitChoice);
            return choices;
        }

        [Header("Gradient colors (matches AdminDashboard: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;
        private Texture2D _newQuizButtonGradientTexture;
        private Texture2D _createQuizSubmitGradientTexture;
        private Texture2D _addQuestionSubmitGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private Button _newQuizButton;

        private Label _totalQuizzesValueLabel;
        private Label _totalQuestionsValueLabel;
        private Label _categoriesValueLabel;

        private VisualElement _quizzesEmptyState;
        private Button _createFirstQuizButton;
        private VisualElement _quizzesList;

        // Create/Edit Quiz modal (same modal is reused for both - see _editingQuiz)
        private VisualElement _createQuizModalOverlay;
        private Label _createQuizModalTitleLabel;
        private Button _createQuizCloseButton;
        private Button _createQuizCancelButton;
        private Button _createQuizSubmitButton;
        private Label _createQuizSubmitLabel;
        private TextField _quizTitleField;
        private Label _quizTitleError;
        private TextField _quizCategoryField;
        private Label _quizCategoryError;
        private TextField _quizMaxAttemptsField;
        private DropdownField _quizTimeLimitDropdown;
        private TextField _quizTimeLimitCustomField;
        private TextField _quizPassingScoreField;
        private Toggle _quizDeadlineEnabledToggle;
        private TextField _quizDeadlineDateField;
        private TextField _quizDeadlineTimeField;
        private VisualElement _quizDeadlineFieldsRow;
        private Label _quizDeadlineError;
        private Label _createQuizStatusLabel;

        /// <summary>Null while the modal is in "create" mode; set to the quiz being edited
        /// while the modal is in "edit settings" mode.</summary>
        private QuizData _editingQuiz;

        // Add Question modal
        private VisualElement _addQuestionModalOverlay;
        private Button _addQuestionCloseButton;
        private Button _addQuestionCancelButton;
        private Button _addQuestionSubmitButton;
        private TextField _questionTextField;
        private Label _questionTextError;
        private DropdownField _questionTypeDropdown;
        private VisualElement _optionsContainer;
        private TextField _option1Field;
        private TextField _option2Field;
        private TextField _option3Field;
        private TextField _option4Field;
        private TextField _correctAnswerField;
        private Label _correctAnswerError;
        private DropdownField _difficultyDropdown;
        private TextField _questionPointsField;
        private Label _addQuestionStatusLabel;

        private QuizData _quizPendingQuestion; // which quiz "Add Question" is currently targeting

        private readonly List<QuizData> _currentQuizzes = new List<QuizData>();

        private void OnEnable()
        {
            Debug.Log("[AdminQuizManagementController] OnEnable called");

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
                Debug.LogError("[AdminQuizManagementController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshQuizzesUI();
            RefreshStats();

            CloseCreateQuizModal();
            CloseAddQuestionModal();

            LoadQuizzes();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_headerGradientTexture != null) { Destroy(_headerGradientTexture); _headerGradientTexture = null; }
            if (_newQuizButtonGradientTexture != null) { Destroy(_newQuizButtonGradientTexture); _newQuizButtonGradientTexture = null; }
            if (_createQuizSubmitGradientTexture != null) { Destroy(_createQuizSubmitGradientTexture); _createQuizSubmitGradientTexture = null; }
            if (_addQuestionSubmitGradientTexture != null) { Destroy(_addQuestionSubmitGradientTexture); _addQuestionSubmitGradientTexture = null; }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _newQuizButton?.UnregisterCallback<ClickEvent>(OnNewQuizClicked);
            _createFirstQuizButton?.UnregisterCallback<ClickEvent>(OnNewQuizClicked);

            _createQuizCloseButton?.UnregisterCallback<ClickEvent>(OnCreateQuizCancelClicked);
            _createQuizCancelButton?.UnregisterCallback<ClickEvent>(OnCreateQuizCancelClicked);
            _createQuizSubmitButton?.UnregisterCallback<ClickEvent>(OnCreateQuizSubmitClicked);
            _quizTimeLimitDropdown?.UnregisterCallback<ChangeEvent<string>>(OnTimeLimitChoiceChanged);
            _quizDeadlineEnabledToggle?.UnregisterCallback<ChangeEvent<bool>>(OnDeadlineEnabledChanged);

            _addQuestionCloseButton?.UnregisterCallback<ClickEvent>(OnAddQuestionCancelClicked);
            _addQuestionCancelButton?.UnregisterCallback<ClickEvent>(OnAddQuestionCancelClicked);
            _addQuestionSubmitButton?.UnregisterCallback<ClickEvent>(OnAddQuestionSubmitClicked);
            _questionTypeDropdown?.UnregisterCallback<ChangeEvent<string>>(OnQuestionTypeChanged);

            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminQuizManagementController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");
            _newQuizButton = _screenRoot.Q<Button>("new-quiz-button");

            _totalQuizzesValueLabel = _screenRoot.Q<Label>("total-quizzes-value-label");
            _totalQuestionsValueLabel = _screenRoot.Q<Label>("total-questions-value-label");
            _categoriesValueLabel = _screenRoot.Q<Label>("categories-value-label");

            _quizzesEmptyState = _screenRoot.Q<VisualElement>("quizzes-empty-state");
            _createFirstQuizButton = _screenRoot.Q<Button>("create-first-quiz-button");
            _quizzesList = _screenRoot.Q<VisualElement>("quizzes-list");

            _createQuizModalOverlay = _screenRoot.Q<VisualElement>("create-quiz-modal-overlay");
            _createQuizModalTitleLabel = _screenRoot.Q<Label>("create-quiz-modal-title");
            _createQuizCloseButton = _screenRoot.Q<Button>("create-quiz-close-button");
            _createQuizCancelButton = _screenRoot.Q<Button>("create-quiz-cancel-button");
            _createQuizSubmitButton = _screenRoot.Q<Button>("create-quiz-submit-button");
            _createQuizSubmitLabel = _screenRoot.Q<Label>("create-quiz-submit-label");
            _quizTitleField = _screenRoot.Q<TextField>("quiz-title-field");
            _quizTitleError = _screenRoot.Q<Label>("quiz-title-error");
            _quizCategoryField = _screenRoot.Q<TextField>("quiz-category-field");
            _quizCategoryError = _screenRoot.Q<Label>("quiz-category-error");
            _quizMaxAttemptsField = _screenRoot.Q<TextField>("quiz-max-attempts-field");
            _quizTimeLimitDropdown = _screenRoot.Q<DropdownField>("quiz-time-limit-dropdown");
            _quizTimeLimitCustomField = _screenRoot.Q<TextField>("quiz-time-limit-custom-field");
            _quizPassingScoreField = _screenRoot.Q<TextField>("quiz-passing-score-field");
            _quizDeadlineEnabledToggle = _screenRoot.Q<Toggle>("quiz-deadline-enabled-toggle");
            _quizDeadlineFieldsRow = _screenRoot.Q<VisualElement>("quiz-deadline-fields-row");
            _quizDeadlineDateField = _screenRoot.Q<TextField>("quiz-deadline-date-field");
            _quizDeadlineTimeField = _screenRoot.Q<TextField>("quiz-deadline-time-field");
            _quizDeadlineError = _screenRoot.Q<Label>("quiz-deadline-error");
            _createQuizStatusLabel = _screenRoot.Q<Label>("create-quiz-status-label");

            if (_quizTimeLimitDropdown != null)
            {
                _quizTimeLimitDropdown.choices = TimeLimitDisplayChoices;
                _quizTimeLimitDropdown.SetValueWithoutNotify("10 minutes");
            }

            _addQuestionModalOverlay = _screenRoot.Q<VisualElement>("add-question-modal-overlay");
            _addQuestionCloseButton = _screenRoot.Q<Button>("add-question-close-button");
            _addQuestionCancelButton = _screenRoot.Q<Button>("add-question-cancel-button");
            _addQuestionSubmitButton = _screenRoot.Q<Button>("add-question-submit-button");
            _questionTextField = _screenRoot.Q<TextField>("question-text-field");
            _questionTextError = _screenRoot.Q<Label>("question-text-error");
            _questionTypeDropdown = _screenRoot.Q<DropdownField>("question-type-dropdown");
            _optionsContainer = _screenRoot.Q<VisualElement>("options-container");
            _option1Field = _screenRoot.Q<TextField>("option-1-field");
            _option2Field = _screenRoot.Q<TextField>("option-2-field");
            _option3Field = _screenRoot.Q<TextField>("option-3-field");
            _option4Field = _screenRoot.Q<TextField>("option-4-field");
            _correctAnswerField = _screenRoot.Q<TextField>("correct-answer-field");
            _correctAnswerError = _screenRoot.Q<Label>("correct-answer-error");
            _difficultyDropdown = _screenRoot.Q<DropdownField>("difficulty-dropdown");
            _questionPointsField = _screenRoot.Q<TextField>("question-points-field");
            _addQuestionStatusLabel = _screenRoot.Q<Label>("add-question-status-label");

            if (_questionTypeDropdown != null)
            {
                _questionTypeDropdown.choices = QuestionTypeDisplayChoices;
                _questionTypeDropdown.SetValueWithoutNotify(QuestionTypeDisplayChoices[0]);
            }

            if (_difficultyDropdown != null)
            {
                _difficultyDropdown.choices = DifficultyDisplayChoices;
                _difficultyDropdown.SetValueWithoutNotify("Medium");
            }

            Debug.Log($"[AdminQuizManagementController] Found quizzes list: {_quizzesList != null}, question type dropdown: {_questionTypeDropdown != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _newQuizButton?.RegisterCallback<ClickEvent>(OnNewQuizClicked);
            _createFirstQuizButton?.RegisterCallback<ClickEvent>(OnNewQuizClicked);

            _createQuizCloseButton?.RegisterCallback<ClickEvent>(OnCreateQuizCancelClicked);
            _createQuizCancelButton?.RegisterCallback<ClickEvent>(OnCreateQuizCancelClicked);
            _createQuizSubmitButton?.RegisterCallback<ClickEvent>(OnCreateQuizSubmitClicked);
            _quizTimeLimitDropdown?.RegisterCallback<ChangeEvent<string>>(OnTimeLimitChoiceChanged);
            _quizDeadlineEnabledToggle?.RegisterCallback<ChangeEvent<bool>>(OnDeadlineEnabledChanged);

            _addQuestionCloseButton?.RegisterCallback<ClickEvent>(OnAddQuestionCancelClicked);
            _addQuestionCancelButton?.RegisterCallback<ClickEvent>(OnAddQuestionCancelClicked);
            _addQuestionSubmitButton?.RegisterCallback<ClickEvent>(OnAddQuestionSubmitClicked);
            _questionTypeDropdown?.RegisterCallback<ChangeEvent<string>>(OnQuestionTypeChanged);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Loading from QuizService ----------------

        private void LoadQuizzes()
        {
            if (QuizService.Instance == null)
            {
                Debug.LogWarning("[AdminQuizManagementController] QuizService not available yet.");
                return;
            }

            QuizService.Instance.FetchMyQuizzes(records =>
            {
                _currentQuizzes.Clear();
                foreach (var record in records) _currentQuizzes.Add(ToQuizData(record));

                RefreshQuizzesUI();
                RefreshStats();
            });
        }

        // ---------------- Public API ----------------

        /// <summary>Replace the current quiz list (e.g. loaded from backend).</summary>
        public void SetQuizzes(List<QuizData> quizzes)
        {
            _currentQuizzes.Clear();
            if (quizzes != null) _currentQuizzes.AddRange(quizzes);
            RefreshQuizzesUI();
            RefreshStats();
        }

        // ---------------- Stats ----------------

        private void RefreshStats()
        {
            int totalQuizzes = _currentQuizzes.Count;
            int totalQuestions = _currentQuizzes.Sum(q => q.Questions.Count);
            int categories = _currentQuizzes
                .Select(q => q.Category?.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .Count();

            if (_totalQuizzesValueLabel != null) _totalQuizzesValueLabel.text = totalQuizzes.ToString();
            if (_totalQuestionsValueLabel != null) _totalQuestionsValueLabel.text = totalQuestions.ToString();
            if (_categoriesValueLabel != null) _categoriesValueLabel.text = categories.ToString();
        }

        // ---------------- Quizzes list ----------------

        private void RefreshQuizzesUI()
        {
            bool hasQuizzes = _currentQuizzes.Count > 0;

            _quizzesEmptyState?.EnableInClassList("hidden", hasQuizzes);
            _quizzesList?.EnableInClassList("hidden", !hasQuizzes);

            if (_quizzesList == null) return;

            _quizzesList.Clear();

            foreach (var quiz in _currentQuizzes)
            {
                _quizzesList.Add(BuildQuizCard(quiz));
            }
        }

        private VisualElement BuildQuizCard(QuizData quiz)
        {
            var card = new VisualElement();
            card.AddToClassList("quiz-card");

            var topRow = new VisualElement();
            topRow.AddToClassList("quiz-card-top-row");

            var titleLabel = new Label(quiz.Title);
            titleLabel.AddToClassList("quiz-title-label");

            var actionsRow = new VisualElement();
            actionsRow.AddToClassList("quiz-header-actions");

            var expandButton = new Button(() => OnToggleQuizExpanded(quiz)) { text = quiz.IsExpanded ? "\u25B4" : "\u25BE" };
            expandButton.AddToClassList("quiz-expand-button");

            var editButton = new Button(() => OnEditQuizClicked(quiz)) { text = "\u270E" };
            editButton.AddToClassList("quiz-edit-button");

            var deleteButton = new Button(() => OnDeleteQuizClicked(quiz)) { text = "\U0001F5D1" };
            deleteButton.AddToClassList("quiz-delete-button");
            deleteButton.Q<Label>()?.AddToClassList("quiz-delete-icon");

            actionsRow.Add(expandButton);
            actionsRow.Add(editButton);
            actionsRow.Add(deleteButton);

            topRow.Add(titleLabel);
            topRow.Add(actionsRow);
            card.Add(topRow);

            var categoryLabel = new Label(quiz.Category);
            categoryLabel.AddToClassList("quiz-category-label");
            card.Add(categoryLabel);

            var metaRow = new VisualElement();
            metaRow.AddToClassList("quiz-meta-row");
            AddMetaEntry(metaRow, $"{quiz.Questions.Count} questions", false);
            AddMetaEntry(metaRow, quiz.HasTimeLimit ? $"{quiz.TimeLimitMinutes} min" : "No time limit", true);
            AddMetaEntry(metaRow, $"{quiz.PassingScorePercent}% passing", true);
            card.Add(metaRow);

            // Teacher-view settings row: max attempts / time limit / deadline, per the
            // "Teacher View" requirement - these mirror what's editable in the modal.
            var settingsRow = new VisualElement();
            settingsRow.AddToClassList("quiz-meta-row");
            AddMetaEntry(settingsRow, quiz.MaxAttempts > 0 ? $"Max attempts: {quiz.MaxAttempts}" : "Unlimited attempts", false);
            AddMetaEntry(settingsRow, quiz.IsDeadlineEnabled && quiz.DeadlineUtc.HasValue
                ? $"Deadline: {quiz.DeadlineUtc.Value.ToLocalTime():MMM d, yyyy h:mm tt}"
                : "No deadline", true);
            card.Add(settingsRow);

            if (quiz.IsExpanded)
            {
                var divider = new VisualElement();
                divider.AddToClassList("quiz-divider");
                card.Add(divider);

                var questionsHeaderRow = new VisualElement();
                questionsHeaderRow.AddToClassList("questions-header-row");

                var questionsTitle = new Label("Questions");
                questionsTitle.AddToClassList("questions-header-title");

                var addQuestionButton = new Button(() => OnAddQuestionClicked(quiz));
                addQuestionButton.AddToClassList("add-question-button");
                var plusLabel = new Label("+");
                plusLabel.AddToClassList("add-question-plus");
                var addLabel = new Label("Add Question");
                addLabel.AddToClassList("add-question-label");
                addQuestionButton.Add(plusLabel);
                addQuestionButton.Add(addLabel);

                questionsHeaderRow.Add(questionsTitle);
                questionsHeaderRow.Add(addQuestionButton);
                card.Add(questionsHeaderRow);

                if (quiz.Questions.Count == 0)
                {
                    var emptyLabel = new Label("No questions yet");
                    emptyLabel.AddToClassList("questions-empty-label");
                    card.Add(emptyLabel);
                }
                else
                {
                    var questionsList = new VisualElement();
                    questionsList.AddToClassList("questions-list");

                    for (int i = 0; i < quiz.Questions.Count; i++)
                    {
                        questionsList.Add(BuildQuestionRow(quiz, quiz.Questions[i], i + 1));
                    }

                    card.Add(questionsList);
                }
            }

            return card;
        }

        private void AddMetaEntry(VisualElement metaRow, string text, bool withLeadingDot)
        {
            if (withLeadingDot)
            {
                var dot = new Label("\u2022");
                dot.AddToClassList("quiz-meta-dot");
                metaRow.Add(dot);
            }

            var label = new Label(text);
            label.AddToClassList("quiz-meta-text");
            metaRow.Add(label);
        }

        private VisualElement BuildQuestionRow(QuizData quiz, QuestionData question, int index)
        {
            var row = new VisualElement();
            row.AddToClassList("question-row");

            var topRow = new VisualElement();
            topRow.AddToClassList("question-row-top");

            var indexBadge = new Label($"Q{index}");
            indexBadge.AddToClassList("question-index-badge");

            var difficultyBadge = new VisualElement();
            difficultyBadge.AddToClassList("question-difficulty-badge");
            difficultyBadge.AddToClassList($"question-difficulty-{(string.IsNullOrEmpty(question.Difficulty) ? "easy" : question.Difficulty)}");
            var difficultyLabel = new Label(string.IsNullOrEmpty(question.Difficulty) ? "easy" : question.Difficulty);
            difficultyLabel.AddToClassList("question-difficulty-label");
            difficultyBadge.Add(difficultyLabel);

            var typeBadge = new VisualElement();
            typeBadge.AddToClassList("question-type-badge");
            var typeLabel = new Label(question.QuestionTypeSlug);
            typeLabel.AddToClassList("question-type-label");
            typeBadge.Add(typeLabel);

            var deleteButton = new Button(() => OnDeleteQuestionClicked(quiz, question)) { text = "\U0001F5D1" };
            deleteButton.AddToClassList("question-delete-button");
            deleteButton.Q<Label>()?.AddToClassList("question-delete-icon");

            topRow.Add(indexBadge);
            topRow.Add(difficultyBadge);
            topRow.Add(typeBadge);
            topRow.Add(deleteButton);
            row.Add(topRow);

            var questionTextLabel = new Label(question.QuestionText);
            questionTextLabel.AddToClassList("question-text-label");
            row.Add(questionTextLabel);

            var pointsLabel = new Label($"{question.Points} points");
            pointsLabel.AddToClassList("question-points-label");
            row.Add(pointsLabel);

            return row;
        }

        private void OnToggleQuizExpanded(QuizData quiz)
        {
            quiz.IsExpanded = !quiz.IsExpanded;
            RefreshQuizzesUI();
        }

        private void OnDeleteQuizClicked(QuizData quiz)
        {
            if (string.IsNullOrEmpty(quiz.QuizId))
            {
                Debug.LogWarning("[AdminQuizManagementController] Quiz has no id yet - ignoring delete.");
                return;
            }

            Debug.Log($"[AdminQuizManagementController] Deleting quiz '{quiz.Title}'.");

            QuizService.Instance.DeleteQuiz(quiz.QuizId, (ok, error) =>
            {
                if (!ok)
                {
                    Debug.LogError($"[AdminQuizManagementController] Could not delete quiz: {error}");
                    return;
                }

                _currentQuizzes.Remove(quiz);
                RefreshQuizzesUI();
                RefreshStats();
            });
        }

        private void OnDeleteQuestionClicked(QuizData quiz, QuestionData question)
        {
            int index = quiz.Questions.IndexOf(question);
            if (string.IsNullOrEmpty(quiz.QuizId) || index < 0)
            {
                Debug.LogWarning("[AdminQuizManagementController] Quiz/question not backed by Firestore yet - ignoring delete.");
                return;
            }

            Debug.Log($"[AdminQuizManagementController] Deleting question from '{quiz.Title}'.");

            QuizService.Instance.DeleteQuestion(quiz.QuizId, index, (ok, error, record) =>
            {
                if (!ok)
                {
                    Debug.LogError($"[AdminQuizManagementController] Could not delete question: {error}");
                    return;
                }

                ApplyQuestionsFromRecord(quiz, record);
                RefreshQuizzesUI();
                RefreshStats();
            });
        }

        // ---------------- Create / Edit Quiz modal ----------------

        private void OnNewQuizClicked(ClickEvent evt)
        {
            _editingQuiz = null;
            OpenCreateQuizModal();
        }

        private void OnEditQuizClicked(QuizData quiz)
        {
            _editingQuiz = quiz;
            OpenCreateQuizModal();
        }

        private void OpenCreateQuizModal()
        {
            bool editing = _editingQuiz != null;

            if (_createQuizModalTitleLabel != null) _createQuizModalTitleLabel.text = editing ? "Edit Quiz Settings" : "Create New Quiz";
            if (_createQuizSubmitLabel != null) _createQuizSubmitLabel.text = editing ? "Save Changes" : "Create Quiz";

            if (_quizTitleField != null) _quizTitleField.value = editing ? _editingQuiz.Title : string.Empty;
            if (_quizCategoryField != null) _quizCategoryField.value = editing ? _editingQuiz.Category : string.Empty;
            if (_quizMaxAttemptsField != null) _quizMaxAttemptsField.value = editing && _editingQuiz.MaxAttempts > 0 ? _editingQuiz.MaxAttempts.ToString() : "3";
            if (_quizPassingScoreField != null) _quizPassingScoreField.value = editing ? _editingQuiz.PassingScorePercent.ToString() : "70";

            SetTimeLimitFields(editing ? _editingQuiz.HasTimeLimit : true, editing ? _editingQuiz.TimeLimitMinutes : 10);

            bool deadlineEnabled = editing && _editingQuiz.IsDeadlineEnabled && _editingQuiz.DeadlineUtc.HasValue;
            if (_quizDeadlineEnabledToggle != null) _quizDeadlineEnabledToggle.SetValueWithoutNotify(deadlineEnabled);
            if (deadlineEnabled)
            {
                var local = _editingQuiz.DeadlineUtc.Value.ToLocalTime();
                if (_quizDeadlineDateField != null) _quizDeadlineDateField.value = local.ToString("yyyy-MM-dd");
                if (_quizDeadlineTimeField != null) _quizDeadlineTimeField.value = local.ToString("HH:mm");
            }
            else
            {
                if (_quizDeadlineDateField != null) _quizDeadlineDateField.value = string.Empty;
                if (_quizDeadlineTimeField != null) _quizDeadlineTimeField.value = string.Empty;
            }
            _quizDeadlineFieldsRow?.EnableInClassList("hidden", !deadlineEnabled);

            ClearError(_quizTitleError);
            ClearError(_quizCategoryError);
            ClearError(_quizDeadlineError);
            SetStatus(_createQuizStatusLabel, string.Empty);

            _createQuizModalOverlay?.RemoveFromClassList("hidden");
        }

        /// <summary>Sets the Time Limit dropdown + its custom-minutes field from a
        /// (hasTimeLimit, minutes) pair - shared by OpenCreateQuizModal and the dropdown's
        /// own change handler.</summary>
        private void SetTimeLimitFields(bool hasTimeLimit, int minutes)
        {
            if (!hasTimeLimit)
            {
                _quizTimeLimitDropdown?.SetValueWithoutNotify(TimeLimitNoLimitChoice);
                _quizTimeLimitCustomField?.EnableInClassList("hidden", true);
                return;
            }

            if (TimeLimitPresetMinutes.Contains(minutes))
            {
                _quizTimeLimitDropdown?.SetValueWithoutNotify($"{minutes} minute{(minutes == 1 ? "" : "s")}");
                _quizTimeLimitCustomField?.EnableInClassList("hidden", true);
            }
            else
            {
                _quizTimeLimitDropdown?.SetValueWithoutNotify(TimeLimitCustomChoice);
                if (_quizTimeLimitCustomField != null)
                {
                    _quizTimeLimitCustomField.value = minutes.ToString();
                    _quizTimeLimitCustomField.EnableInClassList("hidden", false);
                }
            }
        }

        private void OnTimeLimitChoiceChanged(ChangeEvent<string> evt)
        {
            bool isCustom = evt.newValue == TimeLimitCustomChoice;
            _quizTimeLimitCustomField?.EnableInClassList("hidden", !isCustom);
            if (isCustom && _quizTimeLimitCustomField != null && string.IsNullOrEmpty(_quizTimeLimitCustomField.value))
            {
                _quizTimeLimitCustomField.value = "10";
            }
        }

        private void OnDeadlineEnabledChanged(ChangeEvent<bool> evt)
        {
            _quizDeadlineFieldsRow?.EnableInClassList("hidden", !evt.newValue);
        }

        /// <summary>Reads the Time Limit dropdown + custom field into (hasTimeLimit, minutes).</summary>
        private (bool hasTimeLimit, int minutes) ReadTimeLimit()
        {
            string choice = _quizTimeLimitDropdown != null ? _quizTimeLimitDropdown.value : "10 minutes";

            if (choice == TimeLimitNoLimitChoice) return (false, 0);

            if (choice == TimeLimitCustomChoice)
            {
                int custom = ParseIntOrDefault(_quizTimeLimitCustomField, 10);
                return (true, Mathf.Max(1, custom));
            }

            // "N minute(s)" preset - pull the leading number back out.
            var digits = new string(choice.TakeWhile(char.IsDigit).ToArray());
            int.TryParse(digits, out int minutes);
            return (true, minutes > 0 ? minutes : 10);
        }

        private void CloseCreateQuizModal()
        {
            _createQuizModalOverlay?.AddToClassList("hidden");
            _editingQuiz = null;
        }

        private void OnCreateQuizCancelClicked(ClickEvent evt) => CloseCreateQuizModal();

        private void OnCreateQuizSubmitClicked(ClickEvent evt)
        {
            string title = _quizTitleField?.value?.Trim();
            string category = _quizCategoryField?.value?.Trim();

            bool valid = true;

            if (string.IsNullOrEmpty(title))
            {
                SetError(_quizTitleError, "Please enter a quiz title");
                valid = false;
            }
            else
            {
                ClearError(_quizTitleError);
            }

            if (string.IsNullOrEmpty(category))
            {
                SetError(_quizCategoryError, "Please enter a category");
                valid = false;
            }
            else
            {
                ClearError(_quizCategoryError);
            }

            bool deadlineEnabled = _quizDeadlineEnabledToggle?.value ?? false;
            DateTime? deadlineUtc = null;

            if (deadlineEnabled)
            {
                string datePart = _quizDeadlineDateField?.value?.Trim();
                string timePart = _quizDeadlineTimeField?.value?.Trim();
                string combined = $"{datePart} {timePart}".Trim();

                if (string.IsNullOrEmpty(datePart) || string.IsNullOrEmpty(timePart) ||
                    !DateTime.TryParse(combined, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedLocal))
                {
                    SetError(_quizDeadlineError, "Enter a valid deadline date (YYYY-MM-DD) and time (HH:MM)");
                    valid = false;
                }
                else
                {
                    ClearError(_quizDeadlineError);
                    deadlineUtc = DateTime.SpecifyKind(parsedLocal, DateTimeKind.Local).ToUniversalTime();
                }
            }
            else
            {
                ClearError(_quizDeadlineError);
            }

            if (!valid)
            {
                SetStatus(_createQuizStatusLabel, "Please fix the highlighted fields.");
                return;
            }

            int maxAttempts = Mathf.Max(0, ParseIntOrDefault(_quizMaxAttemptsField, 3));
            var (hasTimeLimit, timeLimitMinutes) = ReadTimeLimit();
            int passingScore = ParseIntOrDefault(_quizPassingScoreField, 70);

            _createQuizSubmitButton?.SetEnabled(false);

            if (_editingQuiz != null)
            {
                var quizBeingEdited = _editingQuiz;
                SetStatus(_createQuizStatusLabel, "Saving changes...");

                QuizService.Instance.UpdateQuizSettings(
                    quizBeingEdited.QuizId, title, category, maxAttempts, timeLimitMinutes, hasTimeLimit,
                    deadlineEnabled, deadlineUtc, passingScore, (ok, error, record) =>
                    {
                        _createQuizSubmitButton?.SetEnabled(true);
                        if (!ok)
                        {
                            SetStatus(_createQuizStatusLabel, error ?? "Could not save changes. Please try again.");
                            return;
                        }

                        ApplyQuizSettingsFromRecord(quizBeingEdited, record);
                        RefreshQuizzesUI();
                        RefreshStats();
                        CloseCreateQuizModal();
                    });
                return;
            }

            SetStatus(_createQuizStatusLabel, "Creating quiz...");

            // classroomId is null - quizzes created here go into the shared
            // quiz bank (available to every classroom). Assign a specific
            // classroom from AdminClassroomDetailController if needed.

            QuizService.Instance.CreateQuiz(
                title, category, maxAttempts, timeLimitMinutes, hasTimeLimit,
                deadlineEnabled, deadlineUtc, passingScore, null, (ok, error, record) =>
                {
                    _createQuizSubmitButton?.SetEnabled(true);
                    if (!ok)
                    {
                        SetStatus(_createQuizStatusLabel, error ?? "Could not create quiz. Please try again.");
                        return;
                    }
                    var newQuiz = ToQuizData(record);
                    _currentQuizzes.Add(newQuiz);
                    RefreshQuizzesUI();
                    RefreshStats();
                    CloseCreateQuizModal();
                });
        }

        // ---------------- Add Question modal ----------------

        private void OnAddQuestionClicked(QuizData quiz)
        {
            _quizPendingQuestion = quiz;
            OpenAddQuestionModal();
        }

        private void OpenAddQuestionModal()
        {
            if (_questionTextField != null) _questionTextField.value = string.Empty;
            if (_questionTypeDropdown != null) _questionTypeDropdown.SetValueWithoutNotify(QuestionTypeDisplayChoices[0]);
            if (_option1Field != null) _option1Field.value = string.Empty;
            if (_option2Field != null) _option2Field.value = string.Empty;
            if (_option3Field != null) _option3Field.value = string.Empty;
            if (_option4Field != null) _option4Field.value = string.Empty;
            if (_correctAnswerField != null) _correctAnswerField.value = string.Empty;
            if (_difficultyDropdown != null) _difficultyDropdown.SetValueWithoutNotify("Medium");
            if (_questionPointsField != null) _questionPointsField.value = "10";

            ClearError(_questionTextError);
            ClearError(_correctAnswerError);
            SetStatus(_addQuestionStatusLabel, string.Empty);
            UpdateOptionsVisibility(QuestionTypeDisplayChoices[0]);
            UpdateCorrectAnswerHint(QuestionTypeDisplayChoices[0]);

            _addQuestionModalOverlay?.RemoveFromClassList("hidden");
        }

        private void CloseAddQuestionModal()
        {
            _addQuestionModalOverlay?.AddToClassList("hidden");
            _quizPendingQuestion = null;
        }

        private void OnAddQuestionCancelClicked(ClickEvent evt) => CloseAddQuestionModal();

        private void OnQuestionTypeChanged(ChangeEvent<string> evt)
        {
            UpdateOptionsVisibility(evt.newValue);
            UpdateCorrectAnswerHint(evt.newValue);
        }

        private void UpdateOptionsVisibility(string displayType)
        {
            // Multiple Identification also needs the 4 option fields - that's where
            // its distractors (e.g. "keyboard" alongside "Skin"/"Hair"/"Nails") live.
            // Enumeration doesn't use them - it's free-text blanks on the student side.
            bool showOptions = displayType == "Multiple Choice" || displayType == "Multiple Identification";
            _optionsContainer?.EnableInClassList("hidden", !showOptions);
        }

        /// <summary>Correct Answer is a single TextField for every question type, so this
        /// swaps its placeholder to spell out the expected format per type.</summary>
        private void UpdateCorrectAnswerHint(string displayType)
        {
            if (_correctAnswerField == null) return;

            _correctAnswerField.textEdition.placeholder = displayType switch
            {
                "True or False" => "True or False",
                "Enumeration" => "Comma-separated, e.g. Epithelial, Connective, Muscle, Nervous",
                "Multiple Identification" => "Comma-separated, must match option text exactly, e.g. Skin, Hair, Nails",
                _ => "",
            };
        }

        private void OnAddQuestionSubmitClicked(ClickEvent evt)
        {
            if (_quizPendingQuestion == null)
            {
                CloseAddQuestionModal();
                return;
            }

            string questionText = _questionTextField?.value?.Trim();
            string correctAnswer = _correctAnswerField?.value?.Trim();

            bool valid = true;

            if (string.IsNullOrEmpty(questionText))
            {
                SetError(_questionTextError, "Please enter the question");
                valid = false;
            }
            else
            {
                ClearError(_questionTextError);
            }

            if (string.IsNullOrEmpty(correctAnswer))
            {
                SetError(_correctAnswerError, "Please enter the correct answer");
                valid = false;
            }
            else
            {
                ClearError(_correctAnswerError);
            }

            if (!valid)
            {
                SetStatus(_addQuestionStatusLabel, "Please fix the highlighted fields.");
                return;
            }

            string displayType = _questionTypeDropdown != null ? _questionTypeDropdown.value : QuestionTypeDisplayChoices[0];
            string typeSlug = QuestionTypeDisplayToSlug.TryGetValue(displayType ?? string.Empty, out var slug) ? slug : TypeMultipleChoice;

            var question = new QuestionData
            {
                QuestionText = questionText,
                QuestionTypeSlug = typeSlug,
                CorrectAnswer = correctAnswer,
                Difficulty = (_difficultyDropdown != null ? _difficultyDropdown.value : "Medium").ToLowerInvariant(),
                Points = ParseIntOrDefault(_questionPointsField, 10),
            };

            if (typeSlug == TypeMultipleChoice || typeSlug == TypeMultipleIdentification)
            {
                foreach (var optionField in new[] { _option1Field, _option2Field, _option3Field, _option4Field })
                {
                    string option = optionField?.value?.Trim();
                    if (!string.IsNullOrEmpty(option)) question.Options.Add(option);
                }
            }

            var quiz = _quizPendingQuestion;
            if (string.IsNullOrEmpty(quiz.QuizId))
            {
                SetStatus(_addQuestionStatusLabel, "This quiz hasn't finished saving yet - try again in a moment.");
                return;
            }

            SetStatus(_addQuestionStatusLabel, "Saving question...");
            _addQuestionSubmitButton?.SetEnabled(false);

            var questionRecord = ToQuestionRecord(question);

            QuizService.Instance.AddQuestion(quiz.QuizId, questionRecord, (ok, error, record) =>
            {
                _addQuestionSubmitButton?.SetEnabled(true);

                if (!ok)
                {
                    SetStatus(_addQuestionStatusLabel, error ?? "Could not add question. Please try again.");
                    return;
                }

                ApplyQuestionsFromRecord(quiz, record);
                RefreshQuizzesUI();
                RefreshStats();

                CloseAddQuestionModal();
            });
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[AdminQuizManagementController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        // ---------------- QuizService <-> view-model conversion ----------------

        private static QuizData ToQuizData(QuizService.QuizRecord record)
        {
            var quiz = new QuizData
            {
                QuizId = record.QuizId,
                Title = record.Title,
                Category = record.Category,
                PassingScorePercent = record.PassingScorePercent,
                MaxAttempts = record.MaxAttempts,
                TimeLimitMinutes = record.TimeLimitMinutes,
                HasTimeLimit = record.HasTimeLimit,
                IsDeadlineEnabled = record.IsDeadlineEnabled,
                DeadlineUtc = record.DeadlineUtc,
            };

            foreach (var q in record.Questions) quiz.Questions.Add(ToQuestionData(q));

            return quiz;
        }

        /// <summary>After UpdateQuizSettings() succeeds, copy the authoritative settings back
        /// onto the same QuizData instance so IsExpanded/Questions survive (mirrors
        /// ApplyQuestionsFromRecord's approach for question edits).</summary>
        private static void ApplyQuizSettingsFromRecord(QuizData quiz, QuizService.QuizRecord record)
        {
            quiz.Title = record.Title;
            quiz.Category = record.Category;
            quiz.PassingScorePercent = record.PassingScorePercent;
            quiz.MaxAttempts = record.MaxAttempts;
            quiz.TimeLimitMinutes = record.TimeLimitMinutes;
            quiz.HasTimeLimit = record.HasTimeLimit;
            quiz.IsDeadlineEnabled = record.IsDeadlineEnabled;
            quiz.DeadlineUtc = record.DeadlineUtc;
        }

        private static QuestionData ToQuestionData(QuizService.QuestionRecord record)
        {
            return new QuestionData
            {
                QuestionText = record.QuestionText,
                QuestionTypeSlug = record.QuestionTypeSlug,
                Options = new List<string>(record.Options ?? new List<string>()),
                CorrectAnswer = record.CorrectAnswer,
                Difficulty = record.Difficulty,
                Points = record.Points,
            };
        }

        private static QuizService.QuestionRecord ToQuestionRecord(QuestionData data)
        {
            return new QuizService.QuestionRecord
            {
                QuestionText = data.QuestionText,
                QuestionTypeSlug = data.QuestionTypeSlug,
                Options = new List<string>(data.Options ?? new List<string>()),
                CorrectAnswer = data.CorrectAnswer,
                Difficulty = data.Difficulty,
                Points = data.Points,
            };
        }

        /// <summary>Replace a quiz's local question list with the authoritative
        /// list from a QuizService response (after AddQuestion/DeleteQuestion),
        /// keeping the same QuizData instance so IsExpanded etc. survive.</summary>
        private static void ApplyQuestionsFromRecord(QuizData quiz, QuizService.QuizRecord record)
        {
            quiz.Questions.Clear();
            foreach (var q in record.Questions) quiz.Questions.Add(ToQuestionData(q));
        }

        // ---------------- Helpers ----------------

        private static int ParseIntOrDefault(TextField field, int fallback)
        {
            if (field != null && int.TryParse(field.value, out int value))
            {
                return value;
            }
            return fallback;
        }

        private void SetError(Label label, string message)
        {
            if (label == null) return;
            label.text = message;
            label.RemoveFromClassList("hidden");
        }

        private void ClearError(Label label)
        {
            if (label == null) return;
            label.text = string.Empty;
            label.AddToClassList("hidden");
        }

        private void SetStatus(Label label, string message)
        {
            if (label == null) return;
            label.text = message;
            if (string.IsNullOrEmpty(message))
                label.AddToClassList("hidden");
            else
                label.RemoveFromClassList("hidden");
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradients (USS has no linear-gradient) ----------------

        private void ApplyGradients()
        {
            if (_header != null)
            {
                if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
                _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_newQuizButton != null)
            {
                if (_newQuizButtonGradientTexture != null) Destroy(_newQuizButtonGradientTexture);
                _newQuizButtonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _newQuizButton.style.backgroundImage = new StyleBackground(_newQuizButtonGradientTexture);
            }

            if (_createQuizSubmitButton != null)
            {
                if (_createQuizSubmitGradientTexture != null) Destroy(_createQuizSubmitGradientTexture);
                _createQuizSubmitGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _createQuizSubmitButton.style.backgroundImage = new StyleBackground(_createQuizSubmitGradientTexture);
            }

            if (_addQuestionSubmitButton != null)
            {
                if (_addQuestionSubmitGradientTexture != null) Destroy(_addQuestionSubmitGradientTexture);
                _addQuestionSubmitGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _addQuestionSubmitButton.style.backgroundImage = new StyleBackground(_addQuestionSubmitGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminQuizManagementGradientTexture"
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