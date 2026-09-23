using System;
using System.Collections.Generic;
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
    ///  - Search row: filters the quiz list by title/category as the teacher
    ///    types (search-filter-button is a stub for a future filter panel)
    ///  - Renders each quiz as a simple tappable row (icon, title, one-line
    ///    meta summary, trailing chevron). Tapping it opens a full-screen quiz
    ///    detail view (quiz-detail-view - a sibling of the list, not a floating
    ///    modal) with a back button, a delete (trash) button in its header, and
    ///    Overview/Questions segmented tabs: Overview shows a 2x2 stat grid
    ///    (time limit/passing score/max attempts/deadline), Questions shows the
    ///    question list with delete buttons and an "Add Question" button
    ///  - "New Quiz" opens a 3-step wizard modal (Basic Info -> Settings ->
    ///    Availability) that ends on a success screen; the same wizard is
    ///    reused for "Edit Quiz Settings" (see _editingQuiz), which skips the
    ///    success screen and just closes on save.
    ///  - "Add Question" opens a 3-step wizard modal (Question type -> Question
    ///    & answer -> Difficulty/points) that ends on a success screen offering
    ///    "Add Another Question". Step 1 is a tappable card grid instead of a
    ///    dropdown, so the teacher only ever sees what's relevant to the type
    ///    they picked and never retypes an answer they already typed:
    ///      - Multiple Choice: 4 option fields (A-D) + a single-select radio
    ///        picker for the correct one.
    ///      - True or False: two buttons, no typing.
    ///      - Identification: the plain question/correct-answer text fields.
    ///      - Enumeration: answers added one at a time via a text field + "+".
    ///      - Multiple Identification: the same 4 option fields as Multiple
    ///        Choice, but with checkboxes (one or more correct).
    ///      - Image-Based: an anatomy system picked via 3 cards (Skeletal /
    ///        Muscular / Cardiovascular); the question text and correct answer
    ///        are derived from the selection rather than typed.
    ///    Whatever the teacher picks is still funneled into the same
    ///    `correct-answer-field`/`option-N-field` data fields before saving
    ///    (see StageQuestionFromStep2), which is what QuizService actually
    ///    persists - Enumeration and Multiple Identification store it there as
    ///    a comma-separated list, True/False as literally "True"/"False".
    ///  - Each question row shows its Q# / difficulty / type badges, matching
    ///    the mock, with its own delete button
    ///  - Difficulty picked in the Add Question wizard (Step 3) drives the
    ///    question's point value directly via PointsForDifficulty, which reads
    ///    this teacher's own configured Easy/Medium/HardPoints from
    ///    AdminGamificationService (falling back to DefaultDifficultyToPoints
    ///    until that fetch completes - see ApplyPointsForSelectedDifficulty). The
    ///    points field itself is locked (SetEnabled(false)) so its displayed
    ///    value can never drift from whatever difficulty is actually selected.
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

        private static readonly Dictionary<string, string> QuestionTypeSlugToDisplay = new Dictionary<string, string>
        {
            { TypeMultipleChoice, "Multiple Choice" },
            { TypeTrueFalse, "True or False" },
            { TypeIdentification, "Identification" },
            { TypeEnumeration, "Enumeration" },
            { TypeMultipleIdentification, "Multiple Identification" },
            { TypeImageBased, "Image Based" },
        };

        // Reuses the same per-type icon images/USS classes as the Add Question
        // step-1 type picker (.question-type-icon--*) so the Questions tab row
        // badge shows a distinct glyph per question type instead of one generic
        // icon for every non-image-based type.
        private static readonly Dictionary<string, string> QuestionTypeSlugToIconClass = new Dictionary<string, string>
        {
            { TypeMultipleChoice, "question-type-icon--multiple-choice" },
            { TypeTrueFalse, "question-type-icon--true-false" },
            { TypeIdentification, "question-type-icon--identification" },
            { TypeEnumeration, "question-type-icon--enumeration" },
            { TypeMultipleIdentification, "question-type-icon--multiple-id" },
            { TypeImageBased, "question-type-icon--image-based" },
        };

        /// <summary>Fallback difficulty -> points if AdminGamificationService hasn't
        /// returned this teacher's configured values yet (e.g. this screen was opened
        /// before FetchSettings' callback fired). Once real settings arrive, PointsForDifficulty
        /// reads EasyPoints/MediumPoints/HardPoints from there instead - see
        /// AdminGamificationService.GamificationSettings, which is exactly what
        /// AdminGamificationSettingsController.OnSaveChangesClicked() writes via
        /// SaveSettings(). This keeps question points in sync with whatever the teacher
        /// has actually configured, rather than a value fixed in this file.</summary>
        private static readonly Dictionary<string, int> DefaultDifficultyToPoints = new Dictionary<string, int>
        {
            { "easy", 1 },
            { "medium", 2 },
            { "hard", 5 },
        };

        /// <summary>This teacher's points-per-difficulty (+ badges/levels, unused here),
        /// fetched once in OnEnable via AdminGamificationService. Null until that fetch
        /// completes, in which case PointsForDifficulty falls back to
        /// DefaultDifficultyToPoints.</summary>
        private AdminGamificationService.GamificationSettings _gamificationSettings;

        /// <summary>Single source of truth for "how many points is a question of this
        /// difficulty worth" - see ApplyPointsForSelectedDifficulty (keeps the Step 3
        /// points field in sync as a read-only display) and OnAddQuestionSubmitClicked
        /// (reads from here directly when building the QuestionData that actually gets
        /// persisted). Reads the signed-in teacher's own configured
        /// Easy/Medium/HardPoints from AdminGamificationService.CurrentSettings once
        /// loaded, falling back to DefaultDifficultyToPoints until then.</summary>
        private int PointsForDifficulty(string difficulty)
        {
            if (_gamificationSettings != null)
            {
                switch (difficulty)
                {
                    case "easy": return _gamificationSettings.EasyPoints;
                    case "medium": return _gamificationSettings.MediumPoints;
                    case "hard": return _gamificationSettings.HardPoints;
                }
            }

            return DefaultDifficultyToPoints.TryGetValue(difficulty ?? "", out var fallback) ? fallback : 1;
        }

        // Deadline date/time. UI Toolkit runtime has no DateTimePicker - that's
        // UnityEditor.UIElements only - so the deadline picker is a hand-built
        // calendar grid (day cells) plus Hour/Minute dropdowns and AM/PM buttons.
        private static readonly List<string> DeadlineHourDisplayChoices = BuildTwoDigitChoices(1, 12);
        private static readonly List<string> DeadlineMinuteDisplayChoices = BuildTwoDigitChoices(0, 59, 1);
        private static readonly string[] CalendarWeekdayLabels = { "S", "M", "T", "W", "T", "F", "S" };

        /// <summary>24-hour (0-23) -> (12-hour display "01".."12", "AM"/"PM").</summary>
        private static (string hour12, string amPm) ToDeadlineHour12(int hour24)
        {
            int h = hour24 % 12;
            if (h == 0) h = 12;
            return (h.ToString("00"), hour24 < 12 ? "AM" : "PM");
        }

        /// <summary>12-hour display + AM/PM -> 24-hour (0-23). Returns 0 if hour12 fails to parse.</summary>
        private static int FromDeadlineHour12(int hour12, string amPm)
        {
            int h = hour12 % 12; // 12 -> 0
            if (amPm == "PM") h += 12;
            return h;
        }

        private static List<string> BuildTwoDigitChoices(int min, int max, int step = 1)
        {
            var choices = new List<string>();
            for (int i = min; i <= max; i += step) choices.Add(i.ToString("00"));
            return choices;
        }

        /// <summary>Plain data for a single question belonging to a quiz.</summary>
        public class QuestionData
        {
            public string QuestionText;
            public string QuestionTypeSlug; // e.g. "multiple-choice"
            public List<string> Options = new List<string>();
            public string CorrectAnswer;
            public string Difficulty; // "easy" | "medium" | "hard"
            public int Points;

            // Image-Based only (QuestionTypeSlug == TypeImageBased) - see
            // AnatomyTeacherSelectionController.TeacherStructureSelectionResult.
            // Empty/null for every other question type.
            public string AnatomySystemKey;
            public string AnatomySystemDisplayName;
            public string StructureKey;
            public string StructureDisplayName;
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

            /// <summary>SubmissionTypes.Question (default) or SubmissionTypes.File.
            /// Set once at creation and never changed by the edit-settings flow -
            /// see OpenCreateQuizModal, which hides the selector entirely when
            /// _editingQuiz is set.</summary>
            public string SubmissionType = SubmissionTypes.Question;

            /// <summary>File Submission only - shown on the student's assignment
            /// screen. Unused for a question quiz.</summary>
            public string Instructions = string.Empty;

            /// <summary>File Submission only - allowed extensions/MIME types and
            /// the max file size the teacher configured. Null for a question quiz.</summary>
            public FileSubmissionConfig FileConfig;

            /// <summary>File Submission only - a question quiz's points come from
            /// summing Questions instead (see RefreshQuizDetailView /
            /// RefreshQuizzesUI, both of which already do that sum for the card/
            /// overview display).</summary>
            public int PointsPossible;

            public bool IsFileSubmission => SubmissionTypes.IsFileSubmission(SubmissionType);
        }

        /// <summary>Shared wording with QuizService's own server-side check, so the UI's
        /// live/pre-submit message matches whatever the backend would reject with.</summary>
        public const string PastDeadlineErrorMessage = "The selected date and time must be later than the current date and time.";

        // Basic-info field limits, enforced in OnCreateQuizSubmitClicked. Generous enough
        // for any real quiz title/category but tight enough to keep the quiz list row
        // ("title - one-line meta summary") and Firestore doc from taking arbitrary text.
        private const int QuizTitleMaxLength = 100;
        private const int QuizCategoryMaxLength = 40;
        private const int PassingScoreMin = 0;
        private const int PassingScoreMax = 100;
        private const int CustomTimeLimitMinMinutes = 1;
        private const int CustomTimeLimitMaxMinutes = 999;

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

        // Preset choices for the Maximum Attempts dropdown, plus "Unlimited" (-> 0 attempts).
        private const string MaxAttemptsUnlimitedChoice = "Unlimited";
        private static readonly List<string> MaxAttemptsPresetChoices = new List<string> { "1", "2", "3", "5", "10" };
        private static readonly List<string> MaxAttemptsDisplayChoices =
            new List<string> { MaxAttemptsUnlimitedChoice }.Concat(MaxAttemptsPresetChoices).ToList();

        private static readonly string[] AddQuestionStepNames = { "Question type", "Question & answer", "Difficulty & points" };

        [Header("Gradient colors (green -> purple, matches reference mock)")]
        [SerializeField] private Color gradientStart = new Color(0.141f, 0.765f, 0.384f); // green
        [SerializeField] private Color gradientEnd = new Color(0.529f, 0.376f, 0.941f);   // purple

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        /// <summary>Tracks exactly which StyleSheet objects THIS controller copied onto the
        /// shared panel root (see CopyAncestorStyleSheetsOnto), and which panel root they were
        /// copied onto, so OnDisable can remove precisely those sheets again. Without this,
        /// AdminQuizManagement.uss stays attached to panel.visualTree - the ancestor shared by
        /// every screen on the single UIManager UIDocument - forever after the first visit,
        /// bleeding class-name-colliding rules (e.g. .modal-card) into other screens such as
        /// the Dashboard's classrooms-view-all-card even after navigating back and this screen
        /// being disabled.</summary>
        private readonly List<StyleSheet> _stylesheetsCopiedToPanelRoot = new List<StyleSheet>();
        private VisualElement _panelRootWithCopiedStyles;

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

        // Search
        private TextField _quizSearchField;
        private Button _searchFilterButton;

        private VisualElement _quizzesEmptyState;
        private VisualElement _quizzesNoResultsState;
        private Button _createFirstQuizButton;
        private VisualElement _quizzesList;

        // ---------------- Quiz Detail (full screen, not a modal) ----------------
        private VisualElement _quizDetailView;
        private VisualElement _quizDetailHeader;
        private Button _quizDetailBackButton;
        private Button _quizDetailDeleteButton;
        private Label _quizDetailTitleLabel;
        private Label _quizDetailSubtitleLabel;
        private Button _quizDetailTabOverviewButton;
        private Button _quizDetailTabQuestionsButton;
        private VisualElement _quizDetailOverviewPanel;
        private VisualElement _quizDetailQuestionsPanel;
        private VisualElement _quizDetailAddQuestionFooter;
        private Label _quizDetailTimeLimitValue;

        // ---------------- Quiz delete confirmation modal ----------------
        private VisualElement _quizDeleteConfirmOverlay;
        private Button _quizDeleteConfirmButton;
        private Button _quizDeleteCancelButton;
        /// <summary>Quiz awaiting the user's confirmation in the delete modal; null when the
        /// modal is closed.</summary>
        private QuizData _quizPendingDelete;
        private Label _quizDetailPassingScoreValue;
        private Label _quizDetailMaxAttemptsValue;
        private Label _quizDetailDeadlineValue;
        private Label _quizDetailTotalQuestionsValue;
        private Label _quizDetailTotalPointsValue;
        private VisualElement _quizDetailQuestionTypesRow;
        private VisualElement _quizDetailQuestionTypesCard;
        private VisualElement _quizDetailQuestionsList;
        private Label _quizDetailQuestionsEmptyLabel;
        private Button _quizDetailAddQuestionButton;
        // File Submission only - shown instead of the Questions tab/Add Question
        // footer for a quiz whose SubmissionType is SubmissionTypes.File.
        private Button _quizDetailViewSubmissionsButton;

        /// <summary>The quiz currently shown in the full-screen detail view, or null
        /// while the list is showing.</summary>
        private QuizData _currentDetailQuiz;

        /// <summary>"overview" or "questions" - which segmented tab is active in the
        /// detail view, preserved across refreshes.</summary>
        private string _quizDetailActiveTab = "overview";

        // ---------------- Create/Edit Quiz wizard modal ----------------
        private VisualElement _createQuizModalOverlay;
        private VisualElement _createQuizModalCard;
        private VisualElement _createQuizWizardHeader;
        private Button _createQuizWizardBackButton;
        private Label _createQuizModalTitleLabel;
        private Label _createQuizStepLabel;
        private List<VisualElement> _createQuizProgressSegments;

        private VisualElement _createQuizStep1;
        // Submission Type selector - which kind of assignment this quiz is.
        // Hidden entirely when editing an existing quiz (see OpenCreateQuizModal);
        // the type is fixed once a quiz has been created.
        private VisualElement _submissionTypeRow;
        private Button _submissionTypeCardQuestion;
        private Button _submissionTypeCardFile;
        private string _selectedSubmissionType = SubmissionTypes.Question;

        // File Submission only - hidden for a Question-Based quiz.
        private VisualElement _fileSettingsGroup;
        private TextField _quizFileInstructionsField;
        private Label _quizFileInstructionsError;
        private Dictionary<string, Toggle> _fileExtensionToggles;
        private Label _quizFileExtensionsError;
        private TextField _quizFileMaxSizeField;
        private Label _quizFileMaxSizeError;
        private TextField _quizFilePointsField;
        private Label _quizFilePointsError;

        // Question-Based only - a timer only makes sense for a live gameplay session.
        private VisualElement _timeLimitGroup;

        private TextField _quizTitleField;
        private Label _quizTitleError;
        private TextField _quizCategoryField;
        private Label _quizCategoryError;

        private DropdownField _quizTimeLimitDropdown;
        private TextField _quizTimeLimitCustomField;
        private Label _quizTimeLimitError;
        private TextField _quizPassingScoreField;
        private Label _quizPassingScoreError;
        private DropdownField _quizMaxAttemptsDropdown;

        private Button _quizDeadlineSelectorButton;
        private Label _quizDeadlineSelectorLabel;
        private Label _quizDeadlineSelectorChevron;
        private VisualElement _quizDeadlinePickerPanel;
        private Button _calendarPrevMonthButton;
        private Label _calendarMonthYearLabel;
        private Button _calendarNextMonthButton;
        private VisualElement _calendarWeekdayRow;
        private VisualElement _calendarGrid;
        private DropdownField _quizDeadlineHourDropdown;
        private DropdownField _quizDeadlineMinuteDropdown;
        private Button _calendarAmButton;
        private Button _calendarPmButton;
        private Button _quizDeadlineClearButton;
        private Button _quizDeadlineSetButton;
        private Label _quizDeadlineEmptyHint;
        private Label _quizDeadlineError;
        private Label _createQuizStatusLabel;
        private Button _createQuizCancelButton;
        private Button _createQuizSubmitButton;
        private Label _createQuizSubmitLabel;

        private VisualElement _createQuizSuccessView;
        private Label _createQuizSuccessSubtitle;
        private Button _createQuizSuccessAddQuestionButton;
        private Button _createQuizSuccessFinishButton;

        /// <summary>1 while the (single-screen) form is showing, 2 while the success view is showing.</summary>
        private int _createQuizWizardStep = 1;

        /// <summary>Null while the modal is in "create" mode; set to the quiz being edited
        /// while the modal is in "edit settings" mode.</summary>
        private QuizData _editingQuiz;

        /// <summary>The quiz just created/edited, kept around so the success screen's
        /// "Add First Question" button knows which quiz to target.</summary>
        private QuizData _lastSavedQuiz;

        // Deadline picker state - a "draft" the calendar/time controls edit; only committed
        // to the quiz's actual deadline when "Set Deadline" is clicked.
        private int _calendarDisplayedYear;
        private int _calendarDisplayedMonth; // 1-12
        private DateTime? _deadlineDraftDate; // date only (no time-of-day), from tapping a calendar day
        private string _deadlineDraftAmPm = "AM";
        private DateTime? _quizDeadlineLocal; // the committed deadline for the quiz currently being created/edited

        // ---------------- Add Question wizard modal ----------------
        private VisualElement _addQuestionModalOverlay;
        private VisualElement _addQuestionModalCard;
        private VisualElement _addQuestionWizardHeader;
        private Button _addQuestionWizardBackButton;
        private Label _addQuestionStepLabel;
        private Button _addQuestionCancelLinkButton;
        private List<VisualElement> _addQuestionProgressSegments;
        private List<VisualElement> _addQuestionStepItems;
        private List<Label> _addQuestionStepCircleLabels;

        private VisualElement _addQuestionStep1;
        private Dictionary<string, Button> _questionTypeCardButtons;

        private VisualElement _addQuestionStep2;
        private Label _questionTextLabel;
        private TextField _questionTextField;
        private Label _questionTextError;
        private VisualElement _optionsContainer;
        private TextField _option1Field;
        private TextField _option2Field;
        private TextField _option3Field;
        private TextField _option4Field;
        private TextField _correctAnswerField;
        private Label _correctAnswerError;
        private VisualElement _identificationContainer;
        private Button _addQuestionStep2NextButton;

        private VisualElement _mcCorrectAnswerContainer;
        private Toggle _mcCorrectAToggle;
        private Toggle _mcCorrectBToggle;
        private Toggle _mcCorrectCToggle;
        private Toggle _mcCorrectDToggle;
        private Label _mcCorrectAnswerError;
        private List<Toggle> _mcCorrectToggles;

        private VisualElement _miCorrectAnswerContainer;
        private Toggle _miCorrectAToggle;
        private Toggle _miCorrectBToggle;
        private Toggle _miCorrectCToggle;
        private Toggle _miCorrectDToggle;
        private Label _miCorrectAnswerError;

        private VisualElement _trueFalseContainer;
        private Button _trueFalseTrueButton;
        private Button _trueFalseFalseButton;
        private string _selectedTrueFalseAnswer = "True";

        private VisualElement _enumerationContainer;
        private Label _enumerationAnswerCountLabel;
        private TextField _enumerationNewAnswerField;
        private Button _enumerationAddAnswerButton;
        private VisualElement _enumerationAnswersList;
        private Label _enumerationAnswerError;
        private readonly List<string> _enumerationAnswers = new List<string>();

        private VisualElement _imageBasedContainer;
        private VisualElement _imageBasedSystemRow;
        private Button _imageBasedSystemSkeletalButton;
        private Button _imageBasedSystemMuscularButton;
        private Button _imageBasedSystemCardiovascularButton;
        private Dictionary<string, Button> _imageBasedSystemButtons;
        private string _selectedImageBasedSystem;

        // The teacher's actual structure pick, made on the reused Student Anatomy
        // Screen (see OpenAnatomyScreenForStructureSelection) - never hardcoded and
        // never auto-selected. StructureKey is BoneDatabase.json's internal id;
        // StructureDisplayName is the human-readable name and is what CorrectAnswer
        // must equal for Image-Based questions (see StageQuestionFromStep2).
        private string _imageBasedSelectedSystemKey;
        private string _imageBasedSelectedSystemDisplayName;
        private string _imageBasedSelectedStructureKey;
        private string _imageBasedSelectedStructureDisplayName;

        private VisualElement _imageBasedSelectedStructurePanel;
        private Label _imageBasedSelectedStructureNameLabel;
        private Label _imageBasedSelectedStructureSystemLabel;
        private Button _imageBasedChangeStructureButton;
        private Label _imageBasedStructureError;

        private static readonly List<string> AnatomySystemDisplayChoices = new List<string> { "Skeletal", "Muscular", "Cardiovascular" };

        private static readonly Dictionary<string, string> AnatomySystemNoun = new Dictionary<string, string>
        {
            { "Skeletal", "bone" },
            { "Muscular", "muscle" },
            { "Cardiovascular", "structure" },
        };

        // The AnatomyTeacherSelectionController living on the Student Anatomy
        // Screen's GameObject (same GameObject as UIManager - see that class's own
        // header comment). Subscribed to once; the subscription is intentionally
        // never removed in OnDisable, because the teacher's selection event fires
        // while THIS controller is disabled (the Anatomy Screen is the active
        // screen at that moment) - see OnTeacherStructureSelected.
        private AnatomyTeacherSelectionController _anatomyTeacherSelection;

        // Set right before navigating to the Anatomy Screen for a structure pick, so
        // the next OnEnable (when UIManager.ShowScreen destroys and rebuilds this
        // entire screen's UI tree) knows to reopen the Add Question modal at Step 2
        // for this same quiz/question instead of the normal fresh-open reset. Plain
        // fields on this persistent MonoBehaviour survive that rebuild even though
        // every VisualElement reference queried before it does not.
        private bool _resumeAddQuestionOnNextEnable;
        private QuizData _pendingResumeQuiz;

        private VisualElement _addQuestionStep3;
        private Button _difficultyEasyButton;
        private Button _difficultyMediumButton;
        private Button _difficultyHardButton;
        private Dictionary<string, Button> _difficultyButtons;
        private string _selectedDifficulty = "easy";
        private TextField _questionPointsField;
        private Label _addQuestionStatusLabel;
        private Button _addQuestionStep3BackButton;
        private Button _addQuestionSubmitButton;

        private VisualElement _addQuestionSuccessView;
        private Button _addQuestionSuccessAddAnotherButton;
        private Button _addQuestionSuccessDoneButton;

        /// <summary>1-3 while a wizard step is showing, 4 while the success view is showing.</summary>
        private int _addQuestionWizardStep = 1;

        /// <summary>The type slug picked on step 1 - null until a card is tapped.</summary>
        private string _selectedQuestionTypeSlug;

        // Staged from step 2's validated fields (see StageQuestionFromStep2), consumed by
        // the step 3 submit handler once difficulty/points are known too.
        private string _stagedQuestionText;
        private List<string> _stagedOptions;
        private string _stagedCorrectAnswer;

        private QuizData _quizPendingQuestion; // which quiz "Add Question" is currently targeting

        private readonly List<QuizData> _currentQuizzes = new List<QuizData>();
        private bool _realDataReceived;

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

            // Pull this teacher's configured Easy/Medium/HardPoints (Gamification
            // Settings) so question points reflect what they actually set, not a
            // value hardcoded in this file. Paint from CurrentSettings immediately if
            // AdminGamificationService already has one cached (e.g. FetchSettings ran
            // earlier this session, such as from opening Gamification Settings), then
            // refresh once the live fetch below completes - ApplyPointsForSelectedDifficulty
            // re-reads PointsForDifficulty each time, so an Add Question modal that's
            // already open picks up the corrected value automatically.
            if (AdminGamificationService.Instance?.CurrentSettings != null)
            {
                _gamificationSettings = AdminGamificationService.Instance.CurrentSettings;
                ApplyPointsForSelectedDifficulty();
            }
            AdminGamificationService.Instance?.FetchSettings(settings =>
            {
                _gamificationSettings = settings;
                ApplyPointsForSelectedDifficulty();
            });

            CloseCreateQuizModal();
            CloseQuizDeleteConfirm();

            // Returning from the Student Anatomy Screen's teacher structure picker
            // (see OpenAnatomyScreenForStructureSelection) - reopen the Add Question
            // modal on the same quiz/question instead of the normal fresh-open reset
            // below, which would otherwise silently discard everything the teacher
            // was doing. Handles both a confirmed pick and a plain Back/cancel.
            if (_resumeAddQuestionOnNextEnable && _pendingResumeQuiz != null)
            {
                _resumeAddQuestionOnNextEnable = false;
                ResumeAddQuestionModalForImageBased(_pendingResumeQuiz);
            }
            else
            {
                CloseAddQuestionModal();
            }

            if (_anatomyTeacherSelection == null)
                _anatomyTeacherSelection = GetComponent<AnatomyTeacherSelectionController>();

            if (_anatomyTeacherSelection != null)
            {
                // Intentionally never unsubscribed in OnDisable - the teacher's
                // selection event fires while this controller is disabled (the
                // Anatomy Screen is the active screen at that moment). The -=
                // before += just guards against a duplicate handler if OnEnable
                // runs again before this GameObject is destroyed.
                _anatomyTeacherSelection.OnTeacherStructureSelected -= OnTeacherStructureSelected;
                _anatomyTeacherSelection.OnTeacherStructureSelected += OnTeacherStructureSelected;
            }

            // First time this screen opens this session -> fetch. Every mutation
            // this screen makes (create/delete quiz, add question) already patches
            // _currentQuizzes in place, so a re-enable (e.g. switching tabs
            // elsewhere and coming back) can just repaint from it via
            // RefreshQuizzesUI()/RefreshStats() above instead of re-fetching. Same
            // pattern as StudentAchievementsController.
            if (!_realDataReceived)
            {
                LoadQuizzes();
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            RemoveCopiedStyleSheetsFromPanelRoot();

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
            _quizSearchField?.UnregisterCallback<ChangeEvent<string>>(OnQuizSearchChanged);
            _searchFilterButton?.UnregisterCallback<ClickEvent>(OnSearchFilterClicked);

            _quizDetailAddQuestionButton?.UnregisterCallback<ClickEvent>(OnQuizDetailAddQuestionClicked);
            _quizDetailViewSubmissionsButton?.UnregisterCallback<ClickEvent>(OnQuizDetailViewSubmissionsClicked);

            _createQuizWizardBackButton?.UnregisterCallback<ClickEvent>(OnCreateQuizWizardBackClicked);
            _createQuizCancelButton?.UnregisterCallback<ClickEvent>(OnCreateQuizCancelClicked);
            _createQuizSubmitButton?.UnregisterCallback<ClickEvent>(OnCreateQuizSubmitClicked);
            _quizTimeLimitDropdown?.UnregisterCallback<ChangeEvent<string>>(OnTimeLimitChoiceChanged);
            _quizTimeLimitCustomField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _quizPassingScoreField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _quizFileMaxSizeField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _quizFilePointsField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _submissionTypeCardQuestion?.UnregisterCallback<ClickEvent>(OnSubmissionTypeCardClicked);
            _submissionTypeCardFile?.UnregisterCallback<ClickEvent>(OnSubmissionTypeCardClicked);

            _quizDeadlineSelectorButton?.UnregisterCallback<ClickEvent>(OnDeadlineSelectorClicked);
            _calendarPrevMonthButton?.UnregisterCallback<ClickEvent>(OnCalendarPrevMonthClicked);
            _calendarNextMonthButton?.UnregisterCallback<ClickEvent>(OnCalendarNextMonthClicked);
            _quizDeadlineHourDropdown?.UnregisterCallback<ChangeEvent<string>>(OnDeadlinePanelFieldChanged);
            _quizDeadlineMinuteDropdown?.UnregisterCallback<ChangeEvent<string>>(OnDeadlinePanelFieldChanged);
            _calendarAmButton?.UnregisterCallback<ClickEvent>(OnCalendarAmClicked);
            _calendarPmButton?.UnregisterCallback<ClickEvent>(OnCalendarPmClicked);
            _quizDeadlineClearButton?.UnregisterCallback<ClickEvent>(OnDeadlineClearClicked);
            _quizDeadlineSetButton?.UnregisterCallback<ClickEvent>(OnDeadlineSetClicked);

            _createQuizSuccessAddQuestionButton?.UnregisterCallback<ClickEvent>(OnCreateQuizSuccessAddQuestionClicked);
            _createQuizSuccessFinishButton?.UnregisterCallback<ClickEvent>(OnCreateQuizSuccessFinishClicked);

            _addQuestionWizardBackButton?.UnregisterCallback<ClickEvent>(OnAddQuestionWizardBackClicked);
            _addQuestionCancelLinkButton?.UnregisterCallback<ClickEvent>(OnAddQuestionCancelClicked);
            _addQuestionStep2NextButton?.UnregisterCallback<ClickEvent>(OnAddQuestionStep2NextClicked);
            _addQuestionStep3BackButton?.UnregisterCallback<ClickEvent>(OnAddQuestionStep3BackClicked);
            _addQuestionSubmitButton?.UnregisterCallback<ClickEvent>(OnAddQuestionSubmitClicked);

            if (_questionTypeCardButtons != null)
            {
                foreach (var kvp in _questionTypeCardButtons)
                {
                    kvp.Value?.UnregisterCallback<ClickEvent>(OnQuestionTypeCardClicked);
                }
            }

            if (_mcCorrectToggles != null)
            {
                foreach (var toggle in _mcCorrectToggles)
                {
                    toggle?.UnregisterCallback<ChangeEvent<bool>>(OnMcCorrectToggleChanged);
                }
            }

            _trueFalseTrueButton?.UnregisterCallback<ClickEvent>(OnTrueFalseTrueClicked);
            _trueFalseFalseButton?.UnregisterCallback<ClickEvent>(OnTrueFalseFalseClicked);

            if (_imageBasedSystemButtons != null)
            {
                foreach (var kvp in _imageBasedSystemButtons)
                {
                    kvp.Value?.UnregisterCallback<ClickEvent>(OnImageBasedSystemButtonClicked);
                }
            }

            _imageBasedChangeStructureButton?.UnregisterCallback<ClickEvent>(OnImageBasedChangeStructureClicked);

            _enumerationAddAnswerButton?.UnregisterCallback<ClickEvent>(OnEnumerationAddAnswerClicked);

            if (_difficultyButtons != null)
            {
                foreach (var kvp in _difficultyButtons)
                {
                    kvp.Value?.UnregisterCallback<ClickEvent>(OnDifficultyButtonClicked);
                }
            }

            _addQuestionSuccessAddAnotherButton?.UnregisterCallback<ClickEvent>(OnAddQuestionSuccessAddAnotherClicked);
            _addQuestionSuccessDoneButton?.UnregisterCallback<ClickEvent>(OnAddQuestionSuccessDoneClicked);

            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            _screenRoot.UnregisterCallback<AttachToPanelEvent>(OnScreenRootAttachedToPanel);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminQuizManagementController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            PropagateStyleSheetsToPanelRoot();

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");
            _newQuizButton = _screenRoot.Q<Button>("new-quiz-button");

            _totalQuizzesValueLabel = _screenRoot.Q<Label>("total-quizzes-value-label");
            _totalQuestionsValueLabel = _screenRoot.Q<Label>("total-questions-value-label");
            _categoriesValueLabel = _screenRoot.Q<Label>("categories-value-label");

            _quizSearchField = _screenRoot.Q<TextField>("quiz-search-field");
            _searchFilterButton = _screenRoot.Q<Button>("search-filter-button");

            _quizzesEmptyState = _screenRoot.Q<VisualElement>("quizzes-empty-state");
            _quizzesNoResultsState = _screenRoot.Q<VisualElement>("quizzes-no-results-state");
            _createFirstQuizButton = _screenRoot.Q<Button>("create-first-quiz-button");
            _quizzesList = _screenRoot.Q<VisualElement>("quizzes-list");

            QueryQuizDetailElements();
            QueryCreateQuizElements();
            QueryAddQuestionElements();

            Debug.Log($"[AdminQuizManagementController] Found quizzes list: {_quizzesList != null}, question type grid: {_addQuestionStep1 != null}");
        }

        private void QueryQuizDetailElements()
        {
            _quizDetailView = _screenRoot.Q<VisualElement>("quiz-detail-view");
            _quizDetailHeader = _screenRoot.Q<VisualElement>("quiz-detail-header");
            _quizDetailBackButton = _screenRoot.Q<Button>("quiz-detail-back-button");
            _quizDetailDeleteButton = _screenRoot.Q<Button>("quiz-detail-delete-button");
            _quizDeleteConfirmOverlay = _screenRoot.Q<VisualElement>("quiz-delete-confirm-overlay");
            _quizDeleteConfirmButton = _screenRoot.Q<Button>("quiz-delete-confirm-button");
            _quizDeleteCancelButton = _screenRoot.Q<Button>("quiz-delete-cancel-button");
            _quizDetailTitleLabel = _screenRoot.Q<Label>("quiz-detail-title-label");
            _quizDetailSubtitleLabel = _screenRoot.Q<Label>("quiz-detail-subtitle-label");
            _quizDetailTabOverviewButton = _screenRoot.Q<Button>("quiz-detail-tab-overview-button");
            _quizDetailTabQuestionsButton = _screenRoot.Q<Button>("quiz-detail-tab-questions-button");
            _quizDetailOverviewPanel = _screenRoot.Q<VisualElement>("quiz-detail-overview-panel");
            _quizDetailQuestionsPanel = _screenRoot.Q<VisualElement>("quiz-detail-questions-panel");
            _quizDetailAddQuestionFooter = _screenRoot.Q<VisualElement>("quiz-detail-add-question-footer");
            _quizDetailTimeLimitValue = _screenRoot.Q<Label>("quiz-detail-time-limit-value");
            _quizDetailPassingScoreValue = _screenRoot.Q<Label>("quiz-detail-passing-score-value");
            _quizDetailMaxAttemptsValue = _screenRoot.Q<Label>("quiz-detail-max-attempts-value");
            _quizDetailDeadlineValue = _screenRoot.Q<Label>("quiz-detail-deadline-value");
            _quizDetailTotalQuestionsValue = _screenRoot.Q<Label>("quiz-detail-total-questions-value");
            _quizDetailTotalPointsValue = _screenRoot.Q<Label>("quiz-detail-total-points-value");
            _quizDetailQuestionTypesRow = _screenRoot.Q<VisualElement>("quiz-detail-question-types-row");
            _quizDetailQuestionTypesCard = _screenRoot.Q<VisualElement>("quiz-detail-question-types-card");
            _quizDetailQuestionsList = _screenRoot.Q<VisualElement>("quiz-detail-questions-list");
            _quizDetailQuestionsEmptyLabel = _screenRoot.Q<Label>("quiz-detail-questions-empty-label");
            _quizDetailAddQuestionButton = _screenRoot.Q<Button>("quiz-detail-add-question-button");
            _quizDetailViewSubmissionsButton = _screenRoot.Q<Button>("quiz-detail-view-submissions-button");
        }

        private void QueryCreateQuizElements()
        {
            _createQuizModalOverlay = _screenRoot.Q<VisualElement>("create-quiz-modal-overlay");
            _createQuizModalCard = _screenRoot.Q<VisualElement>("create-quiz-modal-card");
            _createQuizWizardHeader = _screenRoot.Q<VisualElement>("create-quiz-wizard-header");
            _createQuizWizardBackButton = _screenRoot.Q<Button>("create-quiz-wizard-back-button");
            _createQuizModalTitleLabel = _screenRoot.Q<Label>("create-quiz-modal-title");
            _createQuizStepLabel = _screenRoot.Q<Label>("create-quiz-step-label");
            _createQuizProgressSegments = new List<VisualElement>
            {
                _screenRoot.Q<VisualElement>("create-quiz-progress-1"),
            };

            _createQuizStep1 = _screenRoot.Q<VisualElement>("create-quiz-step-1");
            _submissionTypeRow = _screenRoot.Q<VisualElement>("submission-type-row");
            _submissionTypeCardQuestion = _screenRoot.Q<Button>("submission-type-card-question");
            _submissionTypeCardFile = _screenRoot.Q<Button>("submission-type-card-file");

            _fileSettingsGroup = _screenRoot.Q<VisualElement>("file-settings-group");
            _quizFileInstructionsField = _screenRoot.Q<TextField>("quiz-file-instructions-field");
            _quizFileInstructionsError = _screenRoot.Q<Label>("quiz-file-instructions-error");
            _fileExtensionToggles = new Dictionary<string, Toggle>
            {
                { "pdf", _screenRoot.Q<Toggle>("file-ext-toggle-pdf") },
                { "doc", _screenRoot.Q<Toggle>("file-ext-toggle-doc") },
                { "docx", _screenRoot.Q<Toggle>("file-ext-toggle-docx") },
                { "xls", _screenRoot.Q<Toggle>("file-ext-toggle-xls") },
                { "xlsx", _screenRoot.Q<Toggle>("file-ext-toggle-xlsx") },
                { "txt", _screenRoot.Q<Toggle>("file-ext-toggle-txt") },
            };
            _quizFileExtensionsError = _screenRoot.Q<Label>("quiz-file-extensions-error");
            _quizFileMaxSizeField = _screenRoot.Q<TextField>("quiz-file-max-size-field");
            _quizFileMaxSizeError = _screenRoot.Q<Label>("quiz-file-max-size-error");
            _quizFilePointsField = _screenRoot.Q<TextField>("quiz-file-points-field");
            _quizFilePointsError = _screenRoot.Q<Label>("quiz-file-points-error");

            _timeLimitGroup = _screenRoot.Q<VisualElement>("time-limit-group");

            _quizTitleField = _screenRoot.Q<TextField>("quiz-title-field");
            _quizTitleError = _screenRoot.Q<Label>("quiz-title-error");
            _quizCategoryField = _screenRoot.Q<TextField>("quiz-category-field");
            _quizCategoryError = _screenRoot.Q<Label>("quiz-category-error");

            _quizTimeLimitDropdown = _screenRoot.Q<DropdownField>("quiz-time-limit-dropdown");
            _quizTimeLimitCustomField = _screenRoot.Q<TextField>("quiz-time-limit-custom-field");
            _quizTimeLimitError = _screenRoot.Q<Label>("quiz-time-limit-error");
            _quizPassingScoreField = _screenRoot.Q<TextField>("quiz-passing-score-field");
            _quizPassingScoreError = _screenRoot.Q<Label>("quiz-passing-score-error");
            _quizMaxAttemptsDropdown = _screenRoot.Q<DropdownField>("quiz-max-attempts-dropdown");

            if (_quizTimeLimitDropdown != null)
            {
                _quizTimeLimitDropdown.choices = TimeLimitDisplayChoices;
                _quizTimeLimitDropdown.SetValueWithoutNotify("10 minutes");
            }
            if (_quizMaxAttemptsDropdown != null)
            {
                _quizMaxAttemptsDropdown.choices = new List<string>(MaxAttemptsDisplayChoices);
                _quizMaxAttemptsDropdown.SetValueWithoutNotify("3");
            }

            _quizDeadlineSelectorButton = _screenRoot.Q<Button>("quiz-deadline-selector-button");
            _quizDeadlineSelectorLabel = _screenRoot.Q<Label>("quiz-deadline-selector-label");
            _quizDeadlineSelectorChevron = _screenRoot.Q<Label>("quiz-deadline-selector-chevron");
            _quizDeadlinePickerPanel = _screenRoot.Q<VisualElement>("quiz-deadline-picker-panel");
            _calendarPrevMonthButton = _screenRoot.Q<Button>("calendar-prev-month-button");
            _calendarMonthYearLabel = _screenRoot.Q<Label>("calendar-month-year-label");
            _calendarNextMonthButton = _screenRoot.Q<Button>("calendar-next-month-button");
            _calendarWeekdayRow = _screenRoot.Q<VisualElement>("calendar-weekday-row");
            _calendarGrid = _screenRoot.Q<VisualElement>("calendar-grid");
            _quizDeadlineHourDropdown = _screenRoot.Q<DropdownField>("quiz-deadline-hour-dropdown");
            _quizDeadlineMinuteDropdown = _screenRoot.Q<DropdownField>("quiz-deadline-minute-dropdown");
            _calendarAmButton = _screenRoot.Q<Button>("calendar-am-button");
            _calendarPmButton = _screenRoot.Q<Button>("calendar-pm-button");
            _quizDeadlineClearButton = _screenRoot.Q<Button>("quiz-deadline-clear-button");
            _quizDeadlineSetButton = _screenRoot.Q<Button>("quiz-deadline-set-button");
            _quizDeadlineEmptyHint = _screenRoot.Q<Label>("quiz-deadline-empty-hint");
            _quizDeadlineError = _screenRoot.Q<Label>("quiz-deadline-error");
            _createQuizStatusLabel = _screenRoot.Q<Label>("create-quiz-status-label");
            _createQuizCancelButton = _screenRoot.Q<Button>("create-quiz-cancel-button");
            _createQuizSubmitButton = _screenRoot.Q<Button>("create-quiz-submit-button");
            _createQuizSubmitLabel = _screenRoot.Q<Label>("create-quiz-submit-label");

            if (_quizDeadlineHourDropdown != null) _quizDeadlineHourDropdown.choices = DeadlineHourDisplayChoices;
            if (_quizDeadlineMinuteDropdown != null) _quizDeadlineMinuteDropdown.choices = DeadlineMinuteDisplayChoices;

            BuildCalendarWeekdayRow();

            _createQuizSuccessView = _screenRoot.Q<VisualElement>("create-quiz-success-view");
            _createQuizSuccessSubtitle = _screenRoot.Q<Label>("create-quiz-success-subtitle");
            _createQuizSuccessAddQuestionButton = _screenRoot.Q<Button>("create-quiz-success-add-question-button");
            _createQuizSuccessFinishButton = _screenRoot.Q<Button>("create-quiz-success-finish-button");
        }

        private void QueryAddQuestionElements()
        {
            _addQuestionModalOverlay = _screenRoot.Q<VisualElement>("add-question-modal-overlay");
            _addQuestionModalCard = _screenRoot.Q<VisualElement>("add-question-modal-card");
            _addQuestionWizardHeader = _screenRoot.Q<VisualElement>("add-question-wizard-header");
            _addQuestionWizardBackButton = _screenRoot.Q<Button>("add-question-wizard-back-button");
            _addQuestionStepLabel = _screenRoot.Q<Label>("add-question-step-label");
            _addQuestionCancelLinkButton = _screenRoot.Q<Button>("add-question-cancel-link-button");
            _addQuestionProgressSegments = new List<VisualElement>
            {
                _screenRoot.Q<VisualElement>("add-question-progress-1"),
                _screenRoot.Q<VisualElement>("add-question-progress-2"),
                _screenRoot.Q<VisualElement>("add-question-progress-3"),
            };
            _addQuestionStepItems = new List<VisualElement>
            {
                _screenRoot.Q<VisualElement>("add-question-step-item-1"),
                _screenRoot.Q<VisualElement>("add-question-step-item-2"),
                _screenRoot.Q<VisualElement>("add-question-step-item-3"),
            };
            _addQuestionStepCircleLabels = new List<Label>
            {
                _screenRoot.Q<Label>("add-question-step-circle-label-1"),
                _screenRoot.Q<Label>("add-question-step-circle-label-2"),
                _screenRoot.Q<Label>("add-question-step-circle-label-3"),
            };

            _addQuestionStep1 = _screenRoot.Q<VisualElement>("add-question-step-1");
            _questionTypeCardButtons = new Dictionary<string, Button>
            {
                { TypeMultipleChoice, _screenRoot.Q<Button>("qtype-card-multiple-choice") },
                { TypeTrueFalse, _screenRoot.Q<Button>("qtype-card-true-false") },
                { TypeIdentification, _screenRoot.Q<Button>("qtype-card-identification") },
                { TypeEnumeration, _screenRoot.Q<Button>("qtype-card-enumeration") },
                { TypeMultipleIdentification, _screenRoot.Q<Button>("qtype-card-multiple-identification") },
                { TypeImageBased, _screenRoot.Q<Button>("qtype-card-image-based") },
            };

            _addQuestionStep2 = _screenRoot.Q<VisualElement>("add-question-step-2");
            _questionTextLabel = _screenRoot.Q<Label>("question-text-label");
            _questionTextField = _screenRoot.Q<TextField>("question-text-field");
            _questionTextError = _screenRoot.Q<Label>("question-text-error");
            _optionsContainer = _screenRoot.Q<VisualElement>("options-container");
            _option1Field = _screenRoot.Q<TextField>("option-1-field");
            _option2Field = _screenRoot.Q<TextField>("option-2-field");
            _option3Field = _screenRoot.Q<TextField>("option-3-field");
            _option4Field = _screenRoot.Q<TextField>("option-4-field");
            _correctAnswerField = _screenRoot.Q<TextField>("correct-answer-field");
            _correctAnswerError = _screenRoot.Q<Label>("correct-answer-error");
            _identificationContainer = _screenRoot.Q<VisualElement>("identification-container");
            _addQuestionStep2NextButton = _screenRoot.Q<Button>("add-question-step2-next-button");

            _mcCorrectAnswerContainer = _screenRoot.Q<VisualElement>("mc-correct-answer-container");
            _mcCorrectAToggle = _screenRoot.Q<Toggle>("mc-correct-a-toggle");
            _mcCorrectBToggle = _screenRoot.Q<Toggle>("mc-correct-b-toggle");
            _mcCorrectCToggle = _screenRoot.Q<Toggle>("mc-correct-c-toggle");
            _mcCorrectDToggle = _screenRoot.Q<Toggle>("mc-correct-d-toggle");
            _mcCorrectAnswerError = _screenRoot.Q<Label>("mc-correct-answer-error");
            _mcCorrectToggles = new List<Toggle> { _mcCorrectAToggle, _mcCorrectBToggle, _mcCorrectCToggle, _mcCorrectDToggle };

            _miCorrectAnswerContainer = _screenRoot.Q<VisualElement>("mi-correct-answer-container");
            _miCorrectAToggle = _screenRoot.Q<Toggle>("mi-correct-a-toggle");
            _miCorrectBToggle = _screenRoot.Q<Toggle>("mi-correct-b-toggle");
            _miCorrectCToggle = _screenRoot.Q<Toggle>("mi-correct-c-toggle");
            _miCorrectDToggle = _screenRoot.Q<Toggle>("mi-correct-d-toggle");
            _miCorrectAnswerError = _screenRoot.Q<Label>("mi-correct-answer-error");

            _trueFalseContainer = _screenRoot.Q<VisualElement>("true-false-container");
            _trueFalseTrueButton = _screenRoot.Q<Button>("true-false-true-button");
            _trueFalseFalseButton = _screenRoot.Q<Button>("true-false-false-button");

            _enumerationContainer = _screenRoot.Q<VisualElement>("enumeration-container");
            _enumerationAnswerCountLabel = _screenRoot.Q<Label>("enumeration-answer-count-label");
            _enumerationNewAnswerField = _screenRoot.Q<TextField>("enumeration-new-answer-field");
            _enumerationAddAnswerButton = _screenRoot.Q<Button>("enumeration-add-answer-button");
            _enumerationAnswersList = _screenRoot.Q<VisualElement>("enumeration-answers-list");
            _enumerationAnswerError = _screenRoot.Q<Label>("enumeration-answer-error");

            _imageBasedContainer = _screenRoot.Q<VisualElement>("image-based-container");
            _imageBasedSystemRow = _screenRoot.Q<VisualElement>("image-based-system-row");
            _imageBasedSystemSkeletalButton = _screenRoot.Q<Button>("image-based-system-skeletal-button");
            _imageBasedSystemMuscularButton = _screenRoot.Q<Button>("image-based-system-muscular-button");
            _imageBasedSystemCardiovascularButton = _screenRoot.Q<Button>("image-based-system-cardiovascular-button");
            _imageBasedSystemButtons = new Dictionary<string, Button>
            {
                { "Skeletal", _imageBasedSystemSkeletalButton },
                { "Muscular", _imageBasedSystemMuscularButton },
                { "Cardiovascular", _imageBasedSystemCardiovascularButton },
            };
            _imageBasedSelectedStructurePanel = _screenRoot.Q<VisualElement>("image-based-selected-structure-panel");
            _imageBasedSelectedStructureNameLabel = _screenRoot.Q<Label>("image-based-selected-structure-name");
            _imageBasedSelectedStructureSystemLabel = _screenRoot.Q<Label>("image-based-selected-structure-system");
            _imageBasedChangeStructureButton = _screenRoot.Q<Button>("image-based-change-structure-button");
            _imageBasedStructureError = _screenRoot.Q<Label>("image-based-structure-error");

            _addQuestionStep3 = _screenRoot.Q<VisualElement>("add-question-step-3");
            _difficultyEasyButton = _screenRoot.Q<Button>("difficulty-easy-button");
            _difficultyMediumButton = _screenRoot.Q<Button>("difficulty-medium-button");
            _difficultyHardButton = _screenRoot.Q<Button>("difficulty-hard-button");
            _difficultyButtons = new Dictionary<string, Button>
            {
                { "easy", _difficultyEasyButton },
                { "medium", _difficultyMediumButton },
                { "hard", _difficultyHardButton },
            };
            _questionPointsField = _screenRoot.Q<TextField>("question-points-field");
            _addQuestionStatusLabel = _screenRoot.Q<Label>("add-question-status-label");
            _addQuestionStep3BackButton = _screenRoot.Q<Button>("add-question-step3-back-button");
            _addQuestionSubmitButton = _screenRoot.Q<Button>("add-question-submit-button");

            _addQuestionSuccessView = _screenRoot.Q<VisualElement>("add-question-success-view");
            _addQuestionSuccessAddAnotherButton = _screenRoot.Q<Button>("add-question-success-add-another-button");
            _addQuestionSuccessDoneButton = _screenRoot.Q<Button>("add-question-success-done-button");
        }

        /// <summary>Dropdown choice popups (GenericDropdownMenu) attach themselves directly to
        /// VisualElement.panel.visualTree - the panel's TRUE root - not to _root
        /// (UIDocument.rootVisualElement). In this app _root is itself just a child of
        /// panel.visualTree (this project uses one shared UIDocument on UIManager for every
        /// screen), which makes the popup a SIBLING of _root, not a descendant of it. Copying
        /// stylesheets onto _root therefore never reaches the popup - .unity-base-dropdown__
        /// container-outer etc. stay unstyled (default Unity look, no max-height clipping).
        /// panel.visualTree is the correct, actual common ancestor of both, so that's the
        /// target that makes the cascade reach the popup.</summary>
        private void PropagateStyleSheetsToPanelRoot()
        {
            if (_screenRoot == null) return;

            var panelRoot = _screenRoot.panel?.visualTree;
            if (panelRoot == null)
            {
                Debug.Log("[AdminQuizManagementController] PropagateStyleSheetsToPanelRoot: panel not attached yet, waiting for AttachToPanelEvent");
                _screenRoot.RegisterCallback<AttachToPanelEvent>(OnScreenRootAttachedToPanel);
                return;
            }

            CopyAncestorStyleSheetsOnto(panelRoot);
        }

        private void OnScreenRootAttachedToPanel(AttachToPanelEvent evt)
        {
            _screenRoot.UnregisterCallback<AttachToPanelEvent>(OnScreenRootAttachedToPanel);
            var panelRoot = _screenRoot.panel?.visualTree;
            Debug.Log($"[AdminQuizManagementController] OnScreenRootAttachedToPanel fired, panelRoot found: {panelRoot != null}");
            if (panelRoot != null) CopyAncestorStyleSheetsOnto(panelRoot);
        }

        /// <summary>The &lt;Style src&gt; tag in this screen's UXML attaches its stylesheet to
        /// the TemplateContainer Unity creates when cloning the tree - an ANCESTOR of
        /// "screen-root", not screen-root itself - so screen-root.styleSheets is empty even
        /// though the cascade still works normally for its own descendants. Walk up from
        /// screen-root to panelRoot (exclusive) collecting stylesheets from every node along
        /// that chain, so whichever ancestor actually holds the sheet gets found.</summary>
        private void CopyAncestorStyleSheetsOnto(VisualElement panelRoot)
        {
            int copied = 0;
            var current = _screenRoot;
            while (current != null && current != panelRoot)
            {
                for (int i = 0; i < current.styleSheets.count; i++)
                {
                    var sheet = current.styleSheets[i];
                    if (sheet != null && !panelRoot.styleSheets.Contains(sheet))
                    {
                        panelRoot.styleSheets.Add(sheet);
                        _stylesheetsCopiedToPanelRoot.Add(sheet);
                        copied++;
                    }
                }
                current = current.parent;
            }
            _panelRootWithCopiedStyles = panelRoot;
            Debug.Log($"[AdminQuizManagementController] CopyAncestorStyleSheetsOnto: copied {copied} new stylesheet(s) onto panelRoot (now has {panelRoot.styleSheets.count} total)");
        }

        /// <summary>Undoes CopyAncestorStyleSheetsOnto: removes exactly the stylesheet(s) this
        /// screen added to the shared panel root, restoring it to how it looked before this
        /// screen was ever shown. Must run on OnDisable (navigating away), not just OnDestroy,
        /// because the panel root is shared with every other screen (Dashboard included) via
        /// the single UIManager UIDocument - leaving AdminQuizManagement.uss attached there
        /// after leaving this screen means its class-name-colliding rules (.modal-card, etc.)
        /// keep cascading into other screens for as long as the app runs.</summary>
        private void RemoveCopiedStyleSheetsFromPanelRoot()
        {
            if (_stylesheetsCopiedToPanelRoot.Count == 0) return;

            var panelRoot = _panelRootWithCopiedStyles ?? _screenRoot?.panel?.visualTree;
            if (panelRoot != null)
            {
                foreach (var sheet in _stylesheetsCopiedToPanelRoot)
                {
                    if (sheet != null && panelRoot.styleSheets.Contains(sheet))
                    {
                        panelRoot.styleSheets.Remove(sheet);
                    }
                }
                Debug.Log($"[AdminQuizManagementController] RemoveCopiedStyleSheetsFromPanelRoot: removed {_stylesheetsCopiedToPanelRoot.Count} stylesheet(s) from panelRoot (now has {panelRoot.styleSheets.count} total)");
            }

            _stylesheetsCopiedToPanelRoot.Clear();
            _panelRootWithCopiedStyles = null;
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _newQuizButton?.RegisterCallback<ClickEvent>(OnNewQuizClicked);
            _createFirstQuizButton?.RegisterCallback<ClickEvent>(OnNewQuizClicked);
            _quizSearchField?.RegisterCallback<ChangeEvent<string>>(OnQuizSearchChanged);
            _searchFilterButton?.RegisterCallback<ClickEvent>(OnSearchFilterClicked);

            _quizDetailBackButton?.RegisterCallback<ClickEvent>(OnQuizDetailBackClicked);
            _quizDetailDeleteButton?.RegisterCallback<ClickEvent>(OnQuizDetailDeleteClicked);
            _quizDeleteConfirmButton?.RegisterCallback<ClickEvent>(OnQuizDeleteConfirmClicked);
            _quizDeleteCancelButton?.RegisterCallback<ClickEvent>(OnQuizDeleteCancelClicked);
            _quizDetailTabOverviewButton?.RegisterCallback<ClickEvent>(OnQuizDetailTabOverviewClicked);
            _quizDetailTabQuestionsButton?.RegisterCallback<ClickEvent>(OnQuizDetailTabQuestionsClicked);
            _quizDetailAddQuestionButton?.RegisterCallback<ClickEvent>(OnQuizDetailAddQuestionClicked);
            _quizDetailViewSubmissionsButton?.RegisterCallback<ClickEvent>(OnQuizDetailViewSubmissionsClicked);

            _createQuizWizardBackButton?.RegisterCallback<ClickEvent>(OnCreateQuizWizardBackClicked);
            _createQuizCancelButton?.RegisterCallback<ClickEvent>(OnCreateQuizCancelClicked);
            _createQuizSubmitButton?.RegisterCallback<ClickEvent>(OnCreateQuizSubmitClicked);
            _quizTimeLimitDropdown?.RegisterCallback<ChangeEvent<string>>(OnTimeLimitChoiceChanged);
            _quizTimeLimitCustomField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _quizPassingScoreField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _quizFileMaxSizeField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _quizFilePointsField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _submissionTypeCardQuestion?.RegisterCallback<ClickEvent>(OnSubmissionTypeCardClicked);
            _submissionTypeCardFile?.RegisterCallback<ClickEvent>(OnSubmissionTypeCardClicked);

            _quizDeadlineSelectorButton?.RegisterCallback<ClickEvent>(OnDeadlineSelectorClicked);
            _calendarPrevMonthButton?.RegisterCallback<ClickEvent>(OnCalendarPrevMonthClicked);
            _calendarNextMonthButton?.RegisterCallback<ClickEvent>(OnCalendarNextMonthClicked);
            _quizDeadlineHourDropdown?.RegisterCallback<ChangeEvent<string>>(OnDeadlinePanelFieldChanged);
            _quizDeadlineMinuteDropdown?.RegisterCallback<ChangeEvent<string>>(OnDeadlinePanelFieldChanged);
            _calendarAmButton?.RegisterCallback<ClickEvent>(OnCalendarAmClicked);
            _calendarPmButton?.RegisterCallback<ClickEvent>(OnCalendarPmClicked);
            _quizDeadlineClearButton?.RegisterCallback<ClickEvent>(OnDeadlineClearClicked);
            _quizDeadlineSetButton?.RegisterCallback<ClickEvent>(OnDeadlineSetClicked);

            _createQuizSuccessAddQuestionButton?.RegisterCallback<ClickEvent>(OnCreateQuizSuccessAddQuestionClicked);
            _createQuizSuccessFinishButton?.RegisterCallback<ClickEvent>(OnCreateQuizSuccessFinishClicked);

            _addQuestionWizardBackButton?.RegisterCallback<ClickEvent>(OnAddQuestionWizardBackClicked);
            _addQuestionCancelLinkButton?.RegisterCallback<ClickEvent>(OnAddQuestionCancelClicked);
            _addQuestionStep2NextButton?.RegisterCallback<ClickEvent>(OnAddQuestionStep2NextClicked);
            _addQuestionStep3BackButton?.RegisterCallback<ClickEvent>(OnAddQuestionStep3BackClicked);
            _addQuestionSubmitButton?.RegisterCallback<ClickEvent>(OnAddQuestionSubmitClicked);

            if (_questionTypeCardButtons != null)
            {
                foreach (var kvp in _questionTypeCardButtons)
                {
                    kvp.Value?.RegisterCallback<ClickEvent>(OnQuestionTypeCardClicked);
                }
            }

            if (_mcCorrectToggles != null)
            {
                foreach (var toggle in _mcCorrectToggles)
                {
                    toggle?.RegisterCallback<ChangeEvent<bool>>(OnMcCorrectToggleChanged);
                }
            }

            _trueFalseTrueButton?.RegisterCallback<ClickEvent>(OnTrueFalseTrueClicked);
            _trueFalseFalseButton?.RegisterCallback<ClickEvent>(OnTrueFalseFalseClicked);

            if (_imageBasedSystemButtons != null)
            {
                foreach (var kvp in _imageBasedSystemButtons)
                {
                    kvp.Value?.RegisterCallback<ClickEvent>(OnImageBasedSystemButtonClicked);
                }
            }

            _imageBasedChangeStructureButton?.RegisterCallback<ClickEvent>(OnImageBasedChangeStructureClicked);

            _enumerationAddAnswerButton?.RegisterCallback<ClickEvent>(OnEnumerationAddAnswerClicked);

            if (_difficultyButtons != null)
            {
                foreach (var kvp in _difficultyButtons)
                {
                    kvp.Value?.RegisterCallback<ClickEvent>(OnDifficultyButtonClicked);
                }
            }

            _addQuestionSuccessAddAnotherButton?.RegisterCallback<ClickEvent>(OnAddQuestionSuccessAddAnotherClicked);
            _addQuestionSuccessDoneButton?.RegisterCallback<ClickEvent>(OnAddQuestionSuccessDoneClicked);

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

                _realDataReceived = true;
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

        // ---------------- Search ----------------

        private void OnQuizSearchChanged(ChangeEvent<string> evt) => RefreshQuizzesUI();

        /// <summary>Stub - no filter panel exists in the mock yet. Wire this up to a
        /// category/type filter sheet once that UI is designed.</summary>
        private void OnSearchFilterClicked(ClickEvent evt)
        {
            // TODO: open a filter panel (by category / question type / deadline status).
        }

        private IEnumerable<QuizData> GetFilteredQuizzes()
        {
            string search = _quizSearchField?.value?.Trim();
            if (string.IsNullOrEmpty(search)) return _currentQuizzes;

            return _currentQuizzes.Where(q =>
                (!string.IsNullOrEmpty(q.Title) && q.Title.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(q.Category) && q.Category.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        // ---------------- Quizzes list ----------------

        private void RefreshQuizzesUI()
        {
            bool hasAnyQuizzes = _currentQuizzes.Count > 0;
            bool isSearching = !string.IsNullOrEmpty(_quizSearchField?.value?.Trim());
            var filtered = GetFilteredQuizzes().ToList();
            bool hasVisibleQuizzes = filtered.Count > 0;

            _quizzesEmptyState?.EnableInClassList("hidden", hasAnyQuizzes);
            _quizzesNoResultsState?.EnableInClassList("hidden", !(hasAnyQuizzes && isSearching && !hasVisibleQuizzes));
            _quizzesList?.EnableInClassList("hidden", !hasVisibleQuizzes);

            if (_quizzesList == null) return;

            _quizzesList.Clear();

            foreach (var quiz in filtered)
            {
                _quizzesList.Add(BuildQuizCard(quiz));
            }

            // Keep the full-screen detail view (if open) in sync with whatever just
            // changed the list - a question added/deleted, settings edited, etc.
            // Close it instead if the quiz it was showing no longer exists.
            if (_currentDetailQuiz != null)
            {
                if (!_currentQuizzes.Contains(_currentDetailQuiz))
                {
                    CloseQuizDetail();
                }
                else
                {
                    RefreshQuizDetailView();
                }
            }
        }

        /// <summary>Simple tappable row - icon avatar, title, one-line meta summary,
        /// and a trailing chevron - matching the reference mock exactly. Tapping
        /// anywhere on the card navigates to the full-screen quiz detail view
        /// (OpenQuizDetail) instead of expanding in place; editing/deleting the
        /// quiz now lives in that detail view instead of icon buttons here.</summary>
        private VisualElement BuildQuizCard(QuizData quiz)
        {
            // Faux-shadow wrapper: USS has no box-shadow, so .quiz-card-shadow is a
            // second element offset behind .quiz-card (see USS comments). Neither
            // participates in click handling, so this doesn't change behavior.
            var wrapper = new VisualElement();
            wrapper.AddToClassList("quiz-card-wrapper");

            var shadow = new VisualElement();
            shadow.AddToClassList("quiz-card-shadow");
            wrapper.Add(shadow);

            var card = new VisualElement();
            card.AddToClassList("quiz-card");
            card.RegisterCallback<ClickEvent>(evt => OpenQuizDetail(quiz));
            wrapper.Add(card);

            var row = new VisualElement();
            row.AddToClassList("quiz-card-header-row");

            var iconAvatar = new VisualElement();
            iconAvatar.AddToClassList("quiz-card-icon-avatar");
            var iconGlyph = new VisualElement(); // rendered via the quiz-icon.png background image (see USS)
            iconGlyph.AddToClassList("quiz-card-icon-glyph");
            iconAvatar.Add(iconGlyph);
            row.Add(iconAvatar);

            var main = new VisualElement();
            main.AddToClassList("quiz-card-header-main");

            var titleLabel = new Label(quiz.Title);
            titleLabel.AddToClassList("quiz-title-label");
            main.Add(titleLabel);

            string timeText = quiz.IsFileSubmission ? $"{quiz.PointsPossible} pts" : (quiz.HasTimeLimit ? $"{quiz.TimeLimitMinutes} minutes" : "No time limit");
            var metaLabel = new Label(quiz.IsFileSubmission
                ? $"File Submission \u00B7 {timeText} \u00B7 {quiz.PassingScorePercent}%"
                : $"{quiz.Questions.Count} Qs \u00B7 {timeText} \u00B7 {quiz.PassingScorePercent}%");
            metaLabel.AddToClassList("quiz-meta-summary");
            main.Add(metaLabel);

            row.Add(main);

            var chevron = new Label("\u203A");
            chevron.AddToClassList("quiz-card-chevron");
            row.Add(chevron);

            card.Add(row);

            return wrapper;
        }

        // ---------------- Quiz Detail (full screen, not a modal/overlay) ----------------

        /// <summary>Opens the full-screen quiz detail view for the given quiz. This
        /// replaces the old in-place accordion expand - the list itself is left
        /// untouched underneath; quiz-detail-view is just a sibling that's shown
        /// full-screen on top of it via the "hidden" class (see
        /// AdminQuizManagement.uxml/uss - it's position:absolute covering the
        /// whole screen-root, not a centered floating card over a dim backdrop
        /// like the wizard modals).</summary>
        private void OpenQuizDetail(QuizData quiz)
        {
            _currentDetailQuiz = quiz;
            _quizDetailActiveTab = "overview";
            RefreshQuizDetailView();
            _quizDetailView?.RemoveFromClassList("hidden");
        }

        private void CloseQuizDetail()
        {
            _currentDetailQuiz = null;
            _quizDetailView?.AddToClassList("hidden");
        }

        private void OnQuizDetailBackClicked(ClickEvent evt) => CloseQuizDetail();

        private void OnQuizDetailDeleteClicked(ClickEvent evt)
        {
            if (_currentDetailQuiz == null) return;

            OpenQuizDeleteConfirm(_currentDetailQuiz);
        }

        private void OpenQuizDeleteConfirm(QuizData quiz)
        {
            _quizPendingDelete = quiz;
            _quizDeleteConfirmOverlay?.RemoveFromClassList("hidden");
        }

        private void CloseQuizDeleteConfirm()
        {
            _quizPendingDelete = null;
            _quizDeleteConfirmOverlay?.AddToClassList("hidden");
        }

        private void OnQuizDeleteCancelClicked(ClickEvent evt) => CloseQuizDeleteConfirm();

        private void OnQuizDeleteConfirmClicked(ClickEvent evt)
        {
            if (_quizPendingDelete == null)
            {
                CloseQuizDeleteConfirm();
                return;
            }

            var quiz = _quizPendingDelete;
            CloseQuizDeleteConfirm();
            CloseQuizDetail();
            OnDeleteQuizClicked(quiz);
        }

        private void OnQuizDetailTabOverviewClicked(ClickEvent evt) => SetQuizDetailTab("overview");

        private void OnQuizDetailTabQuestionsClicked(ClickEvent evt) => SetQuizDetailTab("questions");

        private void OnQuizDetailAddQuestionClicked(ClickEvent evt)
        {
            if (_currentDetailQuiz == null) return;
            OnAddQuestionClicked(_currentDetailQuiz);
        }

        /// <summary>Switches the Overview/Questions segmented control, matching the
        /// reference mock's gradient-filled active pill (reuses the header's
        /// gradient texture rather than building a new one per switch).</summary>
        private void SetQuizDetailTab(string tab)
        {
            _quizDetailActiveTab = tab;
            RefreshQuizDetailTabGradient();

            bool overview = tab == "overview";
            _quizDetailOverviewPanel?.EnableInClassList("hidden", !overview);
            _quizDetailQuestionsPanel?.EnableInClassList("hidden", overview);
            _quizDetailAddQuestionFooter?.EnableInClassList("hidden", overview);
        }

        private void RefreshQuizDetailTabGradient()
        {
            bool overview = _quizDetailActiveTab == "overview";

            _quizDetailTabOverviewButton?.EnableInClassList("active", overview);
            _quizDetailTabQuestionsButton?.EnableInClassList("active", !overview);

            if (_quizDetailTabOverviewButton != null)
            {
                _quizDetailTabOverviewButton.style.backgroundImage = overview && _headerGradientTexture != null
                    ? new StyleBackground(_headerGradientTexture)
                    : new StyleBackground(StyleKeyword.Null);
            }

            if (_quizDetailTabQuestionsButton != null)
            {
                _quizDetailTabQuestionsButton.style.backgroundImage = !overview && _headerGradientTexture != null
                    ? new StyleBackground(_headerGradientTexture)
                    : new StyleBackground(StyleKeyword.Null);
            }
        }

        /// <summary>Refreshes every field in the detail view from _currentDetailQuiz -
        /// called on open, and again whenever the quiz list changes (question
        /// added/deleted, settings edited) while the detail view is showing.</summary>
        private void RefreshQuizDetailView()
        {
            if (_currentDetailQuiz == null) return;
            var quiz = _currentDetailQuiz;
            bool isFile = quiz.IsFileSubmission;

            if (_quizDetailTitleLabel != null) _quizDetailTitleLabel.text = quiz.Title;

            if (_quizDetailSubtitleLabel != null)
            {
                string category = string.IsNullOrEmpty(quiz.Category) ? "Uncategorized" : quiz.Category;
                _quizDetailSubtitleLabel.text = isFile
                    ? $"{category} \u00B7 File Submission"
                    : $"{category} \u00B7 {quiz.Questions.Count} question{(quiz.Questions.Count == 1 ? "" : "s")}";
            }

            if (_quizDetailTimeLimitValue != null)
                _quizDetailTimeLimitValue.text = isFile ? "N/A" : (quiz.HasTimeLimit ? $"{quiz.TimeLimitMinutes} minutes" : "None");

            if (_quizDetailPassingScoreValue != null)
                _quizDetailPassingScoreValue.text = $"{quiz.PassingScorePercent}%";

            if (_quizDetailMaxAttemptsValue != null)
                _quizDetailMaxAttemptsValue.text = quiz.MaxAttempts > 0 ? $"{quiz.MaxAttempts} Attempt{(quiz.MaxAttempts == 1 ? "" : "s")}" : "Unlimited";

            if (_quizDetailDeadlineValue != null)
            {
                _quizDetailDeadlineValue.text = quiz.IsDeadlineEnabled && quiz.DeadlineUtc.HasValue
                    ? quiz.DeadlineUtc.Value.ToLocalTime().ToString("MMM d, yyyy")
                    : "None";
            }

            // A File Submission assignment has no questions[] - its single "question"
            // count is meaningless, and its points come straight from PointsPossible
            // (set via QuizService.SetFileSubmissionPoints) rather than a sum.
            if (_quizDetailTotalQuestionsValue != null)
                _quizDetailTotalQuestionsValue.text = isFile ? "N/A" : quiz.Questions.Count.ToString();

            if (_quizDetailTotalPointsValue != null)
                _quizDetailTotalPointsValue.text = (isFile ? quiz.PointsPossible : quiz.Questions.Sum(q => q.Points)).ToString();

            // Questions tab / Add Question only apply to a Question-Based quiz -
            // a File Submission assignment shows "View Submissions" instead, right
            // on the Overview tab it's always pinned to (see SetQuizDetailTab).
            _quizDetailTabQuestionsButton?.EnableInClassList("hidden", isFile);
            _quizDetailViewSubmissionsButton?.EnableInClassList("hidden", !isFile);
            _quizDetailQuestionTypesCard?.EnableInClassList("hidden", isFile);
            if (isFile && _quizDetailActiveTab != "overview") SetQuizDetailTab("overview");

            RefreshQuizDetailQuestionTypesSummary();
            RefreshQuizDetailTabGradient();
            RefreshQuizDetailQuestionsList();
        }

        private void OnQuizDetailViewSubmissionsClicked(ClickEvent evt)
        {
            if (_currentDetailQuiz == null) return;
            var quiz = _currentDetailQuiz;

            // classroomId is null for a shared-bank assignment - AdminSubmissionReviewController
            // then lists every submission for this quiz the teacher is allowed to read.
            UIManager.Instance.ShowAdminSubmissionReview(quiz.QuizId, null, quiz.Title, quiz.PointsPossible, quiz.PassingScorePercent);
        }

        /// <summary>Builds the "Question types" pill row on the Overview tab -
        /// one pill per distinct question type used in this quiz, each showing
        /// the display name and how many questions use that type.</summary>
        private void RefreshQuizDetailQuestionTypesSummary()
        {
            if (_quizDetailQuestionTypesRow == null || _currentDetailQuiz == null) return;

            _quizDetailQuestionTypesRow.Clear();

            var counts = _currentDetailQuiz.Questions
                .GroupBy(q => q.QuestionTypeSlug)
                .Select(g => new { Slug = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Slug);

            bool any = false;
            foreach (var entry in counts)
            {
                any = true;
                string display = QuestionTypeSlugToDisplay.TryGetValue(entry.Slug, out var d) ? d : entry.Slug;

                var pill = new VisualElement();
                pill.AddToClassList("quiz-overview-type-pill");

                var label = new Label(display);
                label.AddToClassList("quiz-overview-type-pill-label");
                pill.Add(label);

                var count = new Label(entry.Count.ToString());
                count.AddToClassList("quiz-overview-type-pill-count");
                pill.Add(count);

                _quizDetailQuestionTypesRow.Add(pill);
            }

            if (!any)
            {
                var emptyLabel = new Label("No questions yet");
                emptyLabel.AddToClassList("quiz-overview-types-empty-label");
                _quizDetailQuestionTypesRow.Add(emptyLabel);
            }
        }

        private void RefreshQuizDetailQuestionsList()
        {
            if (_quizDetailQuestionsList == null || _currentDetailQuiz == null) return;

            var quiz = _currentDetailQuiz;
            bool hasQuestions = quiz.Questions.Count > 0;
            _quizDetailQuestionsEmptyLabel?.EnableInClassList("hidden", hasQuestions);

            _quizDetailQuestionsList.Clear();
            for (int i = 0; i < quiz.Questions.Count; i++)
            {
                _quizDetailQuestionsList.Add(BuildQuizDetailQuestionRow(quiz, quiz.Questions[i], i + 1));
            }
        }

        /// <summary>Builds one question row for the detail view's Questions tab -
        /// icon badge, "Q{n}. {text}", a row of type/difficulty/points pill badges,
        /// and a delete button - matching the reference mock.</summary>
        private VisualElement BuildQuizDetailQuestionRow(QuizData quiz, QuestionData question, int index)
        {
            var row = new VisualElement();
            row.AddToClassList("quiz-detail-question-row");

            var iconBadge = new VisualElement();
            iconBadge.AddToClassList("quiz-detail-question-icon-badge");
            var iconGlyph = new VisualElement();
            iconGlyph.AddToClassList("quiz-detail-question-icon-glyph");
            iconGlyph.AddToClassList(QuestionTypeSlugToIconClass.TryGetValue(question.QuestionTypeSlug, out var iconClass)
                ? iconClass
                : "question-type-icon--multiple-choice");
            iconBadge.Add(iconGlyph);
            row.Add(iconBadge);

            var main = new VisualElement();
            main.AddToClassList("quiz-detail-question-main");

            var textLabel = new Label($"Q{index}. {question.QuestionText}");
            textLabel.AddToClassList("quiz-detail-question-text");
            main.Add(textLabel);

            var badgesRow = new VisualElement();
            badgesRow.AddToClassList("quiz-detail-question-badges-row");

            var typeBadge = new VisualElement();
            typeBadge.AddToClassList("quiz-detail-badge");
            typeBadge.AddToClassList("quiz-detail-badge-type");
            var typeLabel = new Label(QuestionTypeSlugToDisplay.TryGetValue(question.QuestionTypeSlug, out var typeDisplay) ? typeDisplay : question.QuestionTypeSlug);
            typeLabel.AddToClassList("quiz-detail-badge-label");
            typeBadge.Add(typeLabel);
            badgesRow.Add(typeBadge);

            string difficulty = string.IsNullOrEmpty(question.Difficulty) ? "medium" : question.Difficulty;
            var difficultyBadge = new VisualElement();
            difficultyBadge.AddToClassList("quiz-detail-badge");
            difficultyBadge.AddToClassList($"quiz-detail-badge-difficulty-{difficulty}");
            var difficultyLabel = new Label(char.ToUpper(difficulty[0]) + difficulty.Substring(1));
            difficultyLabel.AddToClassList("quiz-detail-badge-label");
            difficultyBadge.Add(difficultyLabel);
            badgesRow.Add(difficultyBadge);

            var pointsBadge = new VisualElement();
            pointsBadge.AddToClassList("quiz-detail-badge");
            pointsBadge.AddToClassList("quiz-detail-badge-points");
            var pointsLabel = new Label($"{question.Points} pts");
            pointsLabel.AddToClassList("quiz-detail-badge-label");
            pointsBadge.Add(pointsLabel);
            badgesRow.Add(pointsBadge);

            main.Add(badgesRow);
            row.Add(main);

            var deleteButton = new Button(() => OnDeleteQuestionClicked(quiz, question));
            deleteButton.AddToClassList("quiz-detail-question-delete-button");
            var deleteIcon = new VisualElement();
            deleteIcon.AddToClassList("quiz-detail-question-delete-icon");
            deleteButton.Add(deleteIcon);
            row.Add(deleteButton);

            return row;
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

        // ---------------- Create / Edit Quiz wizard ----------------

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

            if (_createQuizModalTitleLabel != null)
                _createQuizModalTitleLabel.text = editing
                    ? (_editingQuiz.IsFileSubmission ? "Edit Assignment Settings" : "Edit Quiz Settings")
                    : "Create New Quiz";
            if (_createQuizSubmitLabel != null) _createQuizSubmitLabel.text = editing ? "Save Changes" : "Create Quiz";

            // ---- Submission type (fixed once a quiz exists) ----
            _selectedSubmissionType = editing ? SubmissionTypes.Normalize(_editingQuiz.SubmissionType) : SubmissionTypes.Question;
            _submissionTypeRow?.EnableInClassList("hidden", editing);
            RefreshSubmissionTypeCards();
            UpdateSubmissionTypeFieldsVisibility(_selectedSubmissionType);

            // ---- File Submission fields ----
            var fileConfig = editing && _editingQuiz.FileConfig != null ? _editingQuiz.FileConfig : FileSubmissionConfig.Default();
            if (_quizFileInstructionsField != null) _quizFileInstructionsField.value = editing ? (_editingQuiz.Instructions ?? string.Empty) : string.Empty;
            if (_fileExtensionToggles != null)
            {
                foreach (var kvp in _fileExtensionToggles)
                {
                    bool isChecked = editing && SubmissionTypes.IsFileSubmission(_editingQuiz.SubmissionType)
                        ? fileConfig.IsExtensionAllowed(kvp.Key)
                        : (kvp.Key == "pdf" || kvp.Key == "doc" || kvp.Key == "docx"); // sensible default for a new assignment
                    kvp.Value?.SetValueWithoutNotify(isChecked);
                }
            }
            if (_quizFileMaxSizeField != null) _quizFileMaxSizeField.value = fileConfig.MaxFileSizeMB.ToString();
            if (_quizFilePointsField != null) _quizFilePointsField.value = editing ? _editingQuiz.PointsPossible.ToString() : "100";
            ClearError(_quizFileExtensionsError);
            ClearError(_quizFileMaxSizeError);
            ClearError(_quizFilePointsError);
            ClearError(_quizFileInstructionsError);

            // ---- Basic info ----
            if (_quizTitleField != null) _quizTitleField.value = editing ? _editingQuiz.Title : string.Empty;
            if (_quizCategoryField != null) _quizCategoryField.value = editing ? _editingQuiz.Category : string.Empty;
            ClearError(_quizTitleError);
            ClearError(_quizCategoryError);

            // ---- Settings ----
            SetTimeLimitFields(editing ? _editingQuiz.HasTimeLimit : true, editing ? _editingQuiz.TimeLimitMinutes : 10);
            if (_quizPassingScoreField != null) _quizPassingScoreField.value = editing ? _editingQuiz.PassingScorePercent.ToString() : "70";
            SetMaxAttemptsField(editing ? _editingQuiz.MaxAttempts : 3);
            ClearError(_quizTimeLimitError);
            ClearError(_quizPassingScoreError);

            // ---- Availability: deadline ----
            _quizDeadlineLocal = editing && _editingQuiz.IsDeadlineEnabled && _editingQuiz.DeadlineUtc.HasValue
                ? _editingQuiz.DeadlineUtc.Value.ToLocalTime()
                : (DateTime?)null;
            CloseDeadlinePanel();
            RefreshDeadlineSelectorLabel();
            ClearError(_quizDeadlineError);
            SetStatus(_createQuizStatusLabel, string.Empty);

            _createQuizSubmitButton?.SetEnabled(true);

            _lastSavedQuiz = null;
            SetCreateQuizWizardStep(1);
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

        /// <summary>Sets the Maximum Attempts dropdown from an int (0 = unlimited),
        /// extending the preset choices if editing a quiz whose value isn't one of them.</summary>
        private void SetMaxAttemptsField(int maxAttempts)
        {
            if (_quizMaxAttemptsDropdown == null) return;

            if (maxAttempts <= 0)
            {
                _quizMaxAttemptsDropdown.SetValueWithoutNotify(MaxAttemptsUnlimitedChoice);
                return;
            }

            string value = maxAttempts.ToString();
            if (!_quizMaxAttemptsDropdown.choices.Contains(value))
            {
                var choices = new List<string>(_quizMaxAttemptsDropdown.choices) { value };
                _quizMaxAttemptsDropdown.choices = choices;
            }
            _quizMaxAttemptsDropdown.SetValueWithoutNotify(value);
        }

        /// <summary>Reads the Maximum Attempts dropdown into an int (0 = unlimited).</summary>
        private int ReadMaxAttempts()
        {
            string choice = _quizMaxAttemptsDropdown != null ? _quizMaxAttemptsDropdown.value : "3";
            if (choice == MaxAttemptsUnlimitedChoice) return 0;
            return int.TryParse(choice, out int value) ? Mathf.Max(0, value) : 3;
        }

        // ---------------- Deadline picker (calendar) ----------------

        private void BuildCalendarWeekdayRow()
        {
            if (_calendarWeekdayRow == null) return;
            _calendarWeekdayRow.Clear();
            foreach (var wd in CalendarWeekdayLabels)
            {
                var label = new Label(wd);
                label.AddToClassList("calendar-weekday-label");
                _calendarWeekdayRow.Add(label);
            }
        }

        private void OnDeadlineSelectorClicked(ClickEvent evt)
        {
            bool isOpen = _quizDeadlinePickerPanel != null && !_quizDeadlinePickerPanel.ClassListContains("hidden");
            if (isOpen)
            {
                CloseDeadlinePanel();
            }
            else
            {
                OpenDeadlinePanel();
            }
        }

        private void OpenDeadlinePanel()
        {
            PositionDeadlinePanel();

            var basis = _quizDeadlineLocal ?? DateTime.Now;

            _deadlineDraftDate = _quizDeadlineLocal.HasValue ? basis.Date : (DateTime?)null;
            var (hour12, amPm) = ToDeadlineHour12(basis.Hour);
            _deadlineDraftAmPm = amPm;

            _quizDeadlineHourDropdown?.SetValueWithoutNotify(hour12);
            int snappedMinute = (int)(Math.Round(basis.Minute / 5.0) * 5) % 60;
            _quizDeadlineMinuteDropdown?.SetValueWithoutNotify(snappedMinute.ToString("00"));
            RefreshAmPmButtons();

            _calendarDisplayedYear = basis.Year;
            _calendarDisplayedMonth = basis.Month;
            BuildCalendarGrid();

            _quizDeadlineSelectorChevron.text = "\u2303"; // pointing up while open
            _quizDeadlinePickerPanel?.RemoveFromClassList("hidden");
            UpdateDeadlinePanelValidity();
        }

        private void CloseDeadlinePanel()
        {
            if (_quizDeadlineSelectorChevron != null) _quizDeadlineSelectorChevron.text = "\u2304"; // pointing down while closed
            _quizDeadlinePickerPanel?.AddToClassList("hidden");
        }

        /// <summary>Positions the floating deadline-picker-panel just ABOVE the selector
        /// button (rather than below it), and clamps its height to whatever room is
        /// actually left above that point. The panel floats as the last top-level sibling
        /// of #screen-root (see the UXML comment next to it) rather than living inside
        /// #create-quiz-step-1, the same way Unity's own dropdowns attach to the panel
        /// root instead of a local ancestor - so that overflow:hidden on
        /// #create-quiz-step-1 (a fixed, non-scrolling section) can never clip it, and so
        /// UI Toolkit's draw order (which follows sibling order, not position:absolute
        /// layering) puts it on top of every other on-screen element rather than under
        /// some of them. Since it's no longer nested under (or even adjacent to) the
        /// button in the tree, none of left/bottom/width can be left as fixed CSS offsets
        /// - all three are computed here each time the panel opens by converting the
        /// button's world bounds into the panel's parent's local space, so the panel still
        /// lines up with the button (and matches its width, mirroring how a dropdown's
        /// outer container matches its source control) even if content above it (e.g.
        /// validation errors) shifts the button's position. "bottom" (not "top") is set so
        /// the panel grows upward from the button instead of downward - "top" is
        /// explicitly cleared each time since it's what a previous open would have set
        /// when the panel still opened downward, and leaving both set at once would
        /// stretch the panel to fill the gap between them instead of sizing to its
        /// content. The height clamp matters because the panel must still fit on screen -
        /// without it, a calendar opened low in the step could get cut off at the top of
        /// the screen with no way to reach the rest of it. Once clamped,
        /// .deadline-picker-scroll (which wraps the ENTIRE panel body - calendar, time
        /// controls, AND the Clear/Set Deadline row) scrolls internally instead, so
        /// everything stays reachable by scrolling rather than being stranded off-screen.
        /// If everything fits, the panel just renders at full size with no scrollbar.</summary>
        private void PositionDeadlinePanel()
        {
            if (_quizDeadlinePickerPanel == null || _quizDeadlineSelectorButton == null) return;
            var panelParent = _quizDeadlinePickerPanel.parent;
            if (panelParent == null) return;

            const float gap = 12f;
            const float topMargin = 16f;
            const float minPanelHeight = 260f;

            Rect buttonWorldBound = _quizDeadlineSelectorButton.worldBound;
            Vector2 topLeftLocal = panelParent.WorldToLocal(new Vector2(buttonWorldBound.xMin, buttonWorldBound.yMin));
            Vector2 topRightLocal = panelParent.WorldToLocal(new Vector2(buttonWorldBound.xMax, buttonWorldBound.yMin));

            float parentHeight = panelParent.layout.height;
            float bottom = parentHeight - topLeftLocal.y + gap;

            _quizDeadlinePickerPanel.style.top = StyleKeyword.Auto; // clear any downward-open leftover
            _quizDeadlinePickerPanel.style.bottom = bottom;
            _quizDeadlinePickerPanel.style.left = topLeftLocal.x;
            _quizDeadlinePickerPanel.style.width = topRightLocal.x - topLeftLocal.x;

            float available = topLeftLocal.y - gap - topMargin;
            if (float.IsNaN(available) || available < minPanelHeight) available = minPanelHeight;
            _quizDeadlinePickerPanel.style.maxHeight = available;
        }

        private void OnCalendarPrevMonthClicked(ClickEvent evt)
        {
            _calendarDisplayedMonth--;
            if (_calendarDisplayedMonth < 1) { _calendarDisplayedMonth = 12; _calendarDisplayedYear--; }
            BuildCalendarGrid();
        }

        private void OnCalendarNextMonthClicked(ClickEvent evt)
        {
            _calendarDisplayedMonth++;
            if (_calendarDisplayedMonth > 12) { _calendarDisplayedMonth = 1; _calendarDisplayedYear++; }
            BuildCalendarGrid();
        }

        private void BuildCalendarGrid()
        {
            if (_calendarGrid == null) return;

            if (_calendarMonthYearLabel != null)
            {
                _calendarMonthYearLabel.text = new DateTime(_calendarDisplayedYear, _calendarDisplayedMonth, 1).ToString("MMMM yyyy");
            }

            _calendarGrid.Clear();

            var firstOfMonth = new DateTime(_calendarDisplayedYear, _calendarDisplayedMonth, 1);
            int daysInMonth = DateTime.DaysInMonth(_calendarDisplayedYear, _calendarDisplayedMonth);
            int leadingBlanks = (int)firstOfMonth.DayOfWeek; // Sunday = 0

            var prevMonth = firstOfMonth.AddMonths(-1);
            int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);

            int totalCells = 42; // 6 rows x 7 columns, enough for any month layout
            var today = DateTime.Now.Date;

            for (int cell = 0; cell < totalCells; cell++)
            {
                DateTime cellDate;
                bool otherMonth;

                if (cell < leadingBlanks)
                {
                    cellDate = new DateTime(prevMonth.Year, prevMonth.Month, daysInPrevMonth - (leadingBlanks - 1 - cell));
                    otherMonth = true;
                }
                else if (cell - leadingBlanks < daysInMonth)
                {
                    cellDate = new DateTime(_calendarDisplayedYear, _calendarDisplayedMonth, cell - leadingBlanks + 1);
                    otherMonth = false;
                }
                else
                {
                    var nextMonth = firstOfMonth.AddMonths(1);
                    cellDate = new DateTime(nextMonth.Year, nextMonth.Month, cell - leadingBlanks - daysInMonth + 1);
                    otherMonth = true;
                }

                _calendarGrid.Add(BuildCalendarDayCell(cellDate, otherMonth, today));
            }
        }

        private VisualElement BuildCalendarDayCell(DateTime cellDate, bool otherMonth, DateTime today)
        {
            var wrapper = new VisualElement();
            wrapper.AddToClassList("calendar-day-cell");

            var button = new Button(() => OnCalendarDayClicked(cellDate)) { text = cellDate.Day.ToString() };
            button.AddToClassList("calendar-day-button");

            bool isPast = cellDate.Date < today;
            bool isToday = cellDate.Date == today;
            bool isSelected = _deadlineDraftDate.HasValue && _deadlineDraftDate.Value.Date == cellDate.Date;

            button.EnableInClassList("other-month", otherMonth);
            button.EnableInClassList("past-day", isPast && !otherMonth);
            button.EnableInClassList("today", isToday);
            button.EnableInClassList("selected", isSelected);

            wrapper.Add(button);
            return wrapper;
        }

        private void OnCalendarDayClicked(DateTime date)
        {
            _deadlineDraftDate = date.Date;
            BuildCalendarGrid();
            UpdateDeadlinePanelValidity();
        }

        private void OnDeadlinePanelFieldChanged(ChangeEvent<string> evt) => UpdateDeadlinePanelValidity();

        private void OnCalendarAmClicked(ClickEvent evt) { _deadlineDraftAmPm = "AM"; RefreshAmPmButtons(); UpdateDeadlinePanelValidity(); }
        private void OnCalendarPmClicked(ClickEvent evt) { _deadlineDraftAmPm = "PM"; RefreshAmPmButtons(); UpdateDeadlinePanelValidity(); }

        private void RefreshAmPmButtons()
        {
            _calendarAmButton?.EnableInClassList("selected", _deadlineDraftAmPm == "AM");
            _calendarPmButton?.EnableInClassList("selected", _deadlineDraftAmPm == "PM");
        }

        /// <summary>Truncates to minute precision so "now" (which includes seconds) doesn't
        /// make the current minute look like it's already in the past - the picker only
        /// lets the admin pick a minute, so that's the granularity we validate against.</summary>
        private static DateTime TruncateToMinute(DateTime dt) =>
            new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);

        /// <summary>Combines the draft calendar day with the Hour/Minute/AM-PM controls.
        /// Returns false if no day has been tapped yet.</summary>
        private bool TryReadDraftDeadlineLocal(out DateTime deadlineLocal)
        {
            deadlineLocal = default;
            if (!_deadlineDraftDate.HasValue) return false;

            bool haveHour = int.TryParse(_quizDeadlineHourDropdown?.value, out int hour12);
            bool haveMinute = int.TryParse(_quizDeadlineMinuteDropdown?.value, out int minute);
            if (!haveHour || !haveMinute) return false;

            int hour24 = FromDeadlineHour12(hour12, _deadlineDraftAmPm);
            var d = _deadlineDraftDate.Value;
            deadlineLocal = new DateTime(d.Year, d.Month, d.Day, hour24, minute, 0, DateTimeKind.Local);
            return true;
        }

        /// <summary>Live re-check of the deadline panel, called whenever the calendar day,
        /// hour, minute, or AM/PM selection changes. Shows/clears the deadline error and
        /// enables/disables "Set Deadline" accordingly, so a past date/time can never be set.</summary>
        private void UpdateDeadlinePanelValidity()
        {
            bool hasDraft = TryReadDraftDeadlineLocal(out DateTime draft);
            bool isPast = hasDraft && TruncateToMinute(draft) < TruncateToMinute(DateTime.Now);

            if (isPast)
            {
                SetError(_quizDeadlineError, PastDeadlineErrorMessage);
            }
            else
            {
                ClearError(_quizDeadlineError);
            }

            _quizDeadlineSetButton?.SetEnabled(hasDraft && !isPast);
        }

        private void OnDeadlineClearClicked(ClickEvent evt)
        {
            _quizDeadlineLocal = null;
            _deadlineDraftDate = null;
            ClearError(_quizDeadlineError);
            RefreshDeadlineSelectorLabel();
            CloseDeadlinePanel();
        }

        private void OnDeadlineSetClicked(ClickEvent evt)
        {
            if (!TryReadDraftDeadlineLocal(out DateTime draft)) return;
            if (TruncateToMinute(draft) < TruncateToMinute(DateTime.Now))
            {
                SetError(_quizDeadlineError, PastDeadlineErrorMessage);
                return;
            }

            _quizDeadlineLocal = draft;
            ClearError(_quizDeadlineError);
            RefreshDeadlineSelectorLabel();
            CloseDeadlinePanel();
        }

        private void RefreshDeadlineSelectorLabel()
        {
            if (_quizDeadlineSelectorLabel != null)
            {
                _quizDeadlineSelectorLabel.text = _quizDeadlineLocal.HasValue
                    ? _quizDeadlineLocal.Value.ToString("MMM d, yyyy 'at' h:mm tt")
                    : "Select date and time";
            }
            _quizDeadlineEmptyHint?.EnableInClassList("hidden", _quizDeadlineLocal.HasValue);
        }

        // ---------------- Create Quiz wizard navigation ----------------

        private void SetCreateQuizWizardStep(int step)
        {
            _createQuizWizardStep = step;

            // Step 1 is the single merged form (basic info + settings + availability);
            // step 2 is the success view shown after a successful create/save.
            _createQuizStep1?.EnableInClassList("hidden", step != 1);
            _createQuizSuccessView?.EnableInClassList("hidden", step != 2);

            if (step == 1)
            {
                UpdateProgressSegments(_createQuizProgressSegments, step);
            }
        }

        private void UpdateProgressSegments(List<VisualElement> segments, int currentStep)
        {
            if (segments == null) return;
            for (int i = 0; i < segments.Count; i++)
            {
                int segmentNumber = i + 1;
                segments[i]?.EnableInClassList("done", segmentNumber < currentStep);
                segments[i]?.EnableInClassList("active", segmentNumber == currentStep);
            }
        }

        /// Toggles "done"/"active" on each step-indicator item (circle + label) and
        /// swaps a completed step's circle number for a checkmark, matching the
        /// reference design's green-check / blue-current / grey-upcoming states.
        private void UpdateStepIndicatorItems(List<VisualElement> stepItems, List<Label> circleLabels, int currentStep)
        {
            if (stepItems == null) return;
            for (int i = 0; i < stepItems.Count; i++)
            {
                int stepNumber = i + 1;
                bool done = stepNumber < currentStep;
                stepItems[i]?.EnableInClassList("done", done);
                stepItems[i]?.EnableInClassList("active", stepNumber == currentStep);
                if (circleLabels != null && i < circleLabels.Count && circleLabels[i] != null)
                {
                    circleLabels[i].text = done ? "\u2713" : stepNumber.ToString();
                }
            }
        }

        private void OnCreateQuizWizardBackClicked(ClickEvent evt)
        {
            if (_createQuizWizardStep <= 1)
            {
                CloseCreateQuizModal();
            }
            else
            {
                SetCreateQuizWizardStep(_createQuizWizardStep - 1);
            }
        }

        private void OnCreateQuizCancelClicked(ClickEvent evt) => CloseCreateQuizModal();

        private void CloseCreateQuizModal()
        {
            _createQuizModalOverlay?.AddToClassList("hidden");
            CloseDeadlinePanel();
            _editingQuiz = null;
        }

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
            else if (title.Length > QuizTitleMaxLength)
            {
                SetError(_quizTitleError, $"Quiz title must be {QuizTitleMaxLength} characters or fewer.");
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
            else if (category.Length > QuizCategoryMaxLength)
            {
                SetError(_quizCategoryError, $"Category must be {QuizCategoryMaxLength} characters or fewer.");
                valid = false;
            }
            else
            {
                ClearError(_quizCategoryError);
            }

            // Passing score - always required (both quiz types show it), so validate it
            // as a whole-number percentage rather than silently falling back to 70 via
            // ParseIntOrDefault like the read below does for a value that's already valid.
            if (!int.TryParse(_quizPassingScoreField?.value, out int passingScoreInput))
            {
                SetError(_quizPassingScoreError, "Enter the passing score as a whole number.");
                valid = false;
            }
            else if (passingScoreInput < PassingScoreMin || passingScoreInput > PassingScoreMax)
            {
                SetError(_quizPassingScoreError, $"Passing score must be between {PassingScoreMin} and {PassingScoreMax}.");
                valid = false;
            }
            else
            {
                ClearError(_quizPassingScoreError);
            }

            // Custom time limit - only shown/relevant when "Custom" is selected in the
            // dropdown; ReadTimeLimit() below would otherwise silently clamp a bad value
            // to 1 minute instead of telling the teacher what they typed was rejected.
            bool customTimeLimitSelected = _quizTimeLimitDropdown != null && _quizTimeLimitDropdown.value == TimeLimitCustomChoice;
            if (customTimeLimitSelected)
            {
                if (!int.TryParse(_quizTimeLimitCustomField?.value, out int customMinutesInput))
                {
                    SetError(_quizTimeLimitError, "Enter the time limit in minutes as a whole number.");
                    valid = false;
                }
                else if (customMinutesInput < CustomTimeLimitMinMinutes || customMinutesInput > CustomTimeLimitMaxMinutes)
                {
                    SetError(_quizTimeLimitError, $"Time limit must be between {CustomTimeLimitMinMinutes} and {CustomTimeLimitMaxMinutes} minutes.");
                    valid = false;
                }
                else
                {
                    ClearError(_quizTimeLimitError);
                }
            }
            else
            {
                ClearError(_quizTimeLimitError);
            }

            bool deadlineEnabled = _quizDeadlineLocal.HasValue;
            DateTime? deadlineUtc = null;

            if (deadlineEnabled)
            {
                // Belt-and-suspenders: the panel already keeps "Set Deadline" disabled
                // while the selection is in the past, but re-check here too in case time
                // has passed since it was committed, so a stale deadline can never be saved.
                if (TruncateToMinute(_quizDeadlineLocal.Value) < TruncateToMinute(DateTime.Now))
                {
                    SetError(_quizDeadlineError, PastDeadlineErrorMessage);
                    valid = false;
                }
                else
                {
                    deadlineUtc = _quizDeadlineLocal.Value.ToUniversalTime();
                }
            }

            bool isFile = SubmissionTypes.IsFileSubmission(_selectedSubmissionType);
            FileSubmissionConfig fileConfig = null;
            int filePoints = 0;

            if (isFile)
            {
                string instructionsInput = _quizFileInstructionsField?.value?.Trim();
                if (string.IsNullOrEmpty(instructionsInput))
                {
                    SetError(_quizFileInstructionsError, "Please enter instructions for this file submission.");
                    valid = false;
                }
                else
                {
                    ClearError(_quizFileInstructionsError);
                }

                var checkedExtensions = (_fileExtensionToggles ?? new Dictionary<string, Toggle>())
                    .Where(kvp => kvp.Value != null && kvp.Value.value)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (checkedExtensions.Count == 0)
                {
                    SetError(_quizFileExtensionsError, "Select at least one allowed file type.");
                    valid = false;
                }
                else
                {
                    ClearError(_quizFileExtensionsError);
                }

                if (!int.TryParse(_quizFileMaxSizeField?.value, out int maxSizeMB) || maxSizeMB <= 0)
                {
                    SetError(_quizFileMaxSizeError, "Enter the maximum file size in MB as a whole number.");
                    valid = false;
                }
                else
                {
                    ClearError(_quizFileMaxSizeError);
                    fileConfig = FileSubmissionConfig.FromExtensions(checkedExtensions, maxSizeMB);
                }

                if (!int.TryParse(_quizFilePointsField?.value, out filePoints) || filePoints < 0)
                {
                    SetError(_quizFilePointsError, "Enter the assignment's points as a whole number.");
                    valid = false;
                }
                else
                {
                    ClearError(_quizFilePointsError);
                }
            }

            if (!valid)
            {
                SetStatus(_createQuizStatusLabel, "Please fix the highlighted fields.");
                return;
            }

            int maxAttempts = ReadMaxAttempts();
            var (hasTimeLimit, timeLimitMinutes) = ReadTimeLimit();
            int passingScore = ParseIntOrDefault(_quizPassingScoreField, 70);
            string instructions = isFile ? (_quizFileInstructionsField?.value ?? string.Empty) : string.Empty;

            _createQuizSubmitButton?.SetEnabled(false);

            if (_editingQuiz != null)
            {
                var quizBeingEdited = _editingQuiz;
                SetStatus(_createQuizStatusLabel, "Saving changes...");

                QuizService.Instance.UpdateQuizSettings(
                    quizBeingEdited.QuizId, title, category, maxAttempts, timeLimitMinutes, hasTimeLimit,
                    deadlineEnabled, deadlineUtc, passingScore, (ok, error, record) =>
                    {
                        if (!ok)
                        {
                            _createQuizSubmitButton?.SetEnabled(true);
                            SetStatus(_createQuizStatusLabel, error ?? "Could not save changes. Please try again.");
                            return;
                        }

                        ApplyQuizSettingsFromRecord(quizBeingEdited, record);

                        // File Submission's points aren't part of UpdateQuizSettings
                        // (they don't exist for a question quiz) - save them with the
                        // same call CreateQuiz's file-type branch uses below.
                        if (quizBeingEdited.IsFileSubmission)
                        {
                            QuizService.Instance.SetFileSubmissionPoints(quizBeingEdited.QuizId, filePoints, (pointsOk, pointsError) =>
                            {
                                _createQuizSubmitButton?.SetEnabled(true);
                                if (pointsOk) quizBeingEdited.PointsPossible = filePoints;
                                else Debug.LogWarning($"[AdminQuizManagementController] Could not save assignment points: {pointsError}");

                                RefreshQuizzesUI();
                                RefreshStats();
                                if (_currentDetailQuiz == quizBeingEdited) RefreshQuizDetailView();
                                CloseCreateQuizModal();
                            });
                            return;
                        }

                        _createQuizSubmitButton?.SetEnabled(true);
                        RefreshQuizzesUI();
                        RefreshStats();
                        if (_currentDetailQuiz == quizBeingEdited) RefreshQuizDetailView();
                        CloseCreateQuizModal();
                    },
                    instructions: quizBeingEdited.IsFileSubmission ? instructions : null,
                    fileConfig: quizBeingEdited.IsFileSubmission ? fileConfig : null);
                return;
            }

            SetStatus(_createQuizStatusLabel, isFile ? "Creating assignment..." : "Creating quiz...");

            // classroomId is null - quizzes created here go into the shared
            // quiz bank (available to every classroom). Assign a specific
            // classroom from AdminClassroomDetailController if needed.

            QuizService.Instance.CreateQuiz(
                title, category, maxAttempts, timeLimitMinutes, hasTimeLimit,
                deadlineEnabled, deadlineUtc, passingScore, null, (ok, error, record) =>
                {
                    if (!ok)
                    {
                        _createQuizSubmitButton?.SetEnabled(true);
                        SetStatus(_createQuizStatusLabel, error ?? "Could not create quiz. Please try again.");
                        return;
                    }

                    var newQuiz = ToQuizData(record);

                    if (isFile)
                    {
                        // CreateQuiz's shared signature always writes pointsPossible: 0 -
                        // set the teacher's actual points value right after (see
                        // QuizService.SetFileSubmissionPoints's own doc comment).
                        QuizService.Instance.SetFileSubmissionPoints(newQuiz.QuizId, filePoints, (pointsOk, pointsError) =>
                        {
                            _createQuizSubmitButton?.SetEnabled(true);
                            if (pointsOk) newQuiz.PointsPossible = filePoints;
                            else Debug.LogWarning($"[AdminQuizManagementController] Could not save assignment points: {pointsError}");

                            _currentQuizzes.Add(newQuiz);
                            _lastSavedQuiz = newQuiz;
                            RefreshQuizzesUI();
                            RefreshStats();
                            FinishCreateQuizWizard(newQuiz);
                        });
                        return;
                    }

                    _createQuizSubmitButton?.SetEnabled(true);
                    _currentQuizzes.Add(newQuiz);
                    _lastSavedQuiz = newQuiz;
                    RefreshQuizzesUI();
                    RefreshStats();
                    FinishCreateQuizWizard(newQuiz);
                },
                submissionType: _selectedSubmissionType,
                instructions: isFile ? instructions : null,
                fileConfig: isFile ? fileConfig : null);
        }

        /// <summary>Shows the Create Quiz wizard's success step - "+ Add First
        /// Question" only makes sense for a Question-Based quiz (a File Submission
        /// assignment has no questions[] at all), so that button is hidden for one.</summary>
        private void FinishCreateQuizWizard(QuizData newQuiz)
        {
            bool isFile = newQuiz.IsFileSubmission;

            if (_createQuizSuccessSubtitle != null)
            {
                _createQuizSuccessSubtitle.text = isFile
                    ? "Assignment is ready. Students can now submit their files."
                    : "Quiz is ready. Add your first question or come back later.";
            }

            _createQuizSuccessAddQuestionButton?.EnableInClassList("hidden", isFile);
            SetCreateQuizWizardStep(2);
        }

        private void OnCreateQuizSuccessAddQuestionClicked(ClickEvent evt)
        {
            var quiz = _lastSavedQuiz;
            CloseCreateQuizModal();
            if (quiz == null) return;

            _quizPendingQuestion = quiz;
            OpenAddQuestionModal();
        }

        private void OnCreateQuizSuccessFinishClicked(ClickEvent evt) => CloseCreateQuizModal();

        // ---------------- Add Question wizard ----------------

        private void OnAddQuestionClicked(QuizData quiz)
        {
            _quizPendingQuestion = quiz;
            OpenAddQuestionModal();
        }

        private void OpenAddQuestionModal()
        {
            if (_questionTextField != null) _questionTextField.value = string.Empty;
            if (_option1Field != null) _option1Field.value = string.Empty;
            if (_option2Field != null) _option2Field.value = string.Empty;
            if (_option3Field != null) _option3Field.value = string.Empty;
            if (_option4Field != null) _option4Field.value = string.Empty;
            if (_correctAnswerField != null) _correctAnswerField.value = string.Empty;

            foreach (var optionField in new[] { _option1Field, _option2Field, _option3Field, _option4Field })
            {
                MarkFieldInvalid(optionField, false);
            }
            MarkFieldInvalid(_correctAnswerField, false);

            if (_mcCorrectToggles != null)
            {
                foreach (var toggle in _mcCorrectToggles) toggle?.SetValueWithoutNotify(false);
            }
            foreach (var toggle in new[] { _miCorrectAToggle, _miCorrectBToggle, _miCorrectCToggle, _miCorrectDToggle })
            {
                toggle?.SetValueWithoutNotify(false);
            }

            _selectedTrueFalseAnswer = "True";
            RefreshTrueFalseButtons();

            ResetEnumerationAnswers();

            // No anatomical system is pre-picked on a fresh open - the teacher must
            // actually tap Skeletal/Muscular/Cardiovascular themselves before any card
            // shows as selected. (OpenAnatomyScreenForStructureSelection and the Step 2
            // validation both already fall back to AnatomySystemDisplayChoices[0] on
            // their own if the teacher tries to proceed without picking one.)
            _selectedImageBasedSystem = null;
            RefreshImageBasedSystemButtons();
            ClearImageBasedSelection();

            // Difficulty defaults to "easy", which also seeds the (now read-only)
            // points field via PointsForDifficulty - see ApplyPointsForSelectedDifficulty.
            _selectedDifficulty = "easy";
            RefreshDifficultyButtons();
            ApplyPointsForSelectedDifficulty();

            ClearError(_questionTextError);
            ClearError(_correctAnswerError);
            ClearError(_mcCorrectAnswerError);
            ClearError(_miCorrectAnswerError);
            ClearError(_enumerationAnswerError);
            SetStatus(_addQuestionStatusLabel, string.Empty);

            _selectedQuestionTypeSlug = null;
            RefreshQuestionTypeCards();
            _addQuestionSubmitButton?.SetEnabled(true);

            SetAddQuestionWizardStep(1);
            _addQuestionModalOverlay?.RemoveFromClassList("hidden");
        }

        private void CloseAddQuestionModal()
        {
            _addQuestionModalOverlay?.AddToClassList("hidden");
            _quizPendingQuestion = null;
            _resumeAddQuestionOnNextEnable = false;
            _pendingResumeQuiz = null;
        }

        /// <summary>Reopens the Add Question modal after returning from the Student
        /// Anatomy Screen's teacher structure picker, resuming on the same quiz at
        /// Step 2 with Image-Based selected and whatever structure was (or wasn't)
        /// picked - never the fresh-open reset OpenAddQuestionModal does.</summary>
        private void ResumeAddQuestionModalForImageBased(QuizData quiz)
        {
            _quizPendingQuestion = quiz;

            _selectedQuestionTypeSlug = TypeImageBased;
            RefreshQuestionTypeCards();
            UpdateAddQuestionFieldsVisibility(TypeImageBased);

            RefreshImageBasedSystemButtons();
            RefreshImageBasedSelectedStructurePanel();

            _addQuestionModalOverlay?.RemoveFromClassList("hidden");
            SetAddQuestionWizardStep(2);
        }

        private void OnAddQuestionCancelClicked(ClickEvent evt) => CloseAddQuestionModal();

        // ---------------- Add Question wizard navigation ----------------

        private void SetAddQuestionWizardStep(int step)
        {
            _addQuestionWizardStep = step;

            _addQuestionStep1?.EnableInClassList("hidden", step != 1);
            _addQuestionStep2?.EnableInClassList("hidden", step != 2);
            _addQuestionStep3?.EnableInClassList("hidden", step != 3);
            _addQuestionSuccessView?.EnableInClassList("hidden", step != 4);

            bool onWizardStep = step >= 1 && step <= 3;
            if (onWizardStep)
            {
                if (_addQuestionStepLabel != null) _addQuestionStepLabel.text = $"Step {step} of 3 \u00b7 {AddQuestionStepNames[step - 1]}";
                UpdateProgressSegments(_addQuestionProgressSegments, step);
                UpdateStepIndicatorItems(_addQuestionStepItems, _addQuestionStepCircleLabels, step);
            }

            // Success (step 4) has its own Add Another / Done buttons - the wizard's
            // Back and Cancel controls belong to steps 1-3 only and should disappear
            // once the question has actually been saved.
            _addQuestionWizardBackButton?.EnableInClassList("hidden", !onWizardStep);
            _addQuestionCancelLinkButton?.EnableInClassList("hidden", !onWizardStep);
        }

        private void OnAddQuestionWizardBackClicked(ClickEvent evt)
        {
            if (_addQuestionWizardStep <= 1)
            {
                CloseAddQuestionModal();
            }
            else
            {
                SetAddQuestionWizardStep(_addQuestionWizardStep - 1);
            }
        }

        private void OnSubmissionTypeCardClicked(ClickEvent evt)
        {
            // The type is immutable once a quiz exists - this handler is only ever
            // reachable while creating a new quiz (see OpenCreateQuizModal, which
            // hides _submissionTypeRow entirely when editing).
            if (_editingQuiz != null) return;

            string type = evt.target == _submissionTypeCardFile ? SubmissionTypes.File : SubmissionTypes.Question;
            _selectedSubmissionType = type;
            RefreshSubmissionTypeCards();
            UpdateSubmissionTypeFieldsVisibility(type);
        }

        private void RefreshSubmissionTypeCards()
        {
            _submissionTypeCardQuestion?.EnableInClassList("selected", _selectedSubmissionType == SubmissionTypes.Question);
            _submissionTypeCardFile?.EnableInClassList("selected", SubmissionTypes.IsFileSubmission(_selectedSubmissionType));
        }

        /// <summary>Time Limit only applies to a live gameplay session; the file
        /// settings group (instructions/allowed types/size/points) only applies to
        /// a File Submission assignment - never both.</summary>
        private void UpdateSubmissionTypeFieldsVisibility(string type)
        {
            bool isFile = SubmissionTypes.IsFileSubmission(type);
            _fileSettingsGroup?.EnableInClassList("hidden", !isFile);
            _timeLimitGroup?.EnableInClassList("hidden", isFile);
        }

        private void OnQuestionTypeCardClicked(ClickEvent evt)
        {
            var target = evt.target as Button;
            if (target == null || _questionTypeCardButtons == null) return;

            string slug = _questionTypeCardButtons.FirstOrDefault(kvp => kvp.Value == target).Key;
            if (slug == null) return;

            _selectedQuestionTypeSlug = slug;
            RefreshQuestionTypeCards();
            UpdateAddQuestionFieldsVisibility(slug);
            SetAddQuestionWizardStep(2);
        }

        private void RefreshQuestionTypeCards()
        {
            if (_questionTypeCardButtons == null) return;
            foreach (var kvp in _questionTypeCardButtons)
            {
                kvp.Value?.EnableInClassList("selected", kvp.Key == _selectedQuestionTypeSlug);
            }
        }

        /// <summary>Shows only the fields relevant to the selected question type - the
        /// options list is shared by Multiple Choice and Multiple Identification, each
        /// type gets its own correct-answer control, and the free-text Correct Answer
        /// field (Identification) is the only type that still asks the teacher to type
        /// the answer out.</summary>
        private void UpdateAddQuestionFieldsVisibility(string typeSlug)
        {
            bool isMultipleChoice = typeSlug == TypeMultipleChoice;
            bool isTrueFalse = typeSlug == TypeTrueFalse;
            bool isIdentification = typeSlug == TypeIdentification;
            bool isEnumeration = typeSlug == TypeEnumeration;
            bool isMultipleIdentification = typeSlug == TypeMultipleIdentification;
            bool isImageBased = typeSlug == TypeImageBased;

            bool showOptions = isMultipleChoice || isMultipleIdentification;
            bool showQuestionText = !isImageBased;

            _optionsContainer?.EnableInClassList("hidden", !showOptions);
            _mcCorrectAnswerContainer?.EnableInClassList("hidden", !isMultipleChoice);
            _miCorrectAnswerContainer?.EnableInClassList("hidden", !isMultipleIdentification);
            _trueFalseContainer?.EnableInClassList("hidden", !isTrueFalse);
            _enumerationContainer?.EnableInClassList("hidden", !isEnumeration);
            _imageBasedContainer?.EnableInClassList("hidden", !isImageBased);
            _identificationContainer?.EnableInClassList("hidden", !isIdentification);

            // Image-Based questions are generated from the System picked below, so the
            // free-typed question prompt isn't needed for that type.
            _questionTextLabel?.EnableInClassList("hidden", !showQuestionText);
            _questionTextField?.EnableInClassList("hidden", !showQuestionText);
            if (!showQuestionText) ClearError(_questionTextError);
            else _questionTextError?.EnableInClassList("hidden", string.IsNullOrEmpty(_questionTextError?.text));
        }

        private void OnMcCorrectToggleChanged(ChangeEvent<bool> evt)
        {
            if (!evt.newValue || _mcCorrectToggles == null) return;

            var target = evt.target as Toggle;
            foreach (var toggle in _mcCorrectToggles)
            {
                if (toggle != null && toggle != target) toggle.SetValueWithoutNotify(false);
            }
            ClearError(_mcCorrectAnswerError);
        }

        private int GetMcSelectedIndex()
        {
            if (_mcCorrectToggles == null) return -1;
            for (int i = 0; i < _mcCorrectToggles.Count; i++)
            {
                if (_mcCorrectToggles[i] != null && _mcCorrectToggles[i].value) return i;
            }
            return -1;
        }

        private List<int> GetMiSelectedIndices()
        {
            var indices = new List<int>();
            var toggles = new[] { _miCorrectAToggle, _miCorrectBToggle, _miCorrectCToggle, _miCorrectDToggle };
            for (int i = 0; i < toggles.Length; i++)
            {
                if (toggles[i] != null && toggles[i].value) indices.Add(i);
            }
            return indices;
        }

        // ---------------- True or False: two buttons instead of a dropdown ----------------

        private void OnTrueFalseTrueClicked(ClickEvent evt) { _selectedTrueFalseAnswer = "True"; RefreshTrueFalseButtons(); }
        private void OnTrueFalseFalseClicked(ClickEvent evt) { _selectedTrueFalseAnswer = "False"; RefreshTrueFalseButtons(); }

        private void RefreshTrueFalseButtons()
        {
            _trueFalseTrueButton?.EnableInClassList("selected", _selectedTrueFalseAnswer == "True");
            _trueFalseFalseButton?.EnableInClassList("selected", _selectedTrueFalseAnswer == "False");
        }

        // ---------------- Enumeration: answers added one at a time ----------------

        private void OnEnumerationAddAnswerClicked(ClickEvent evt)
        {
            string answer = _enumerationNewAnswerField?.value?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                SetError(_enumerationAnswerError, "Please type an answer before adding it");
                return;
            }

            ClearError(_enumerationAnswerError);
            _enumerationAnswers.Add(answer);
            if (_enumerationNewAnswerField != null) _enumerationNewAnswerField.value = string.Empty;
            RefreshEnumerationAnswersUI();
        }

        private void ResetEnumerationAnswers()
        {
            _enumerationAnswers.Clear();
            if (_enumerationNewAnswerField != null) _enumerationNewAnswerField.value = string.Empty;
            RefreshEnumerationAnswersUI();
        }

        private void RefreshEnumerationAnswersUI()
        {
            if (_enumerationAnswerCountLabel != null)
            {
                _enumerationAnswerCountLabel.text = $"{_enumerationAnswers.Count} added";
            }

            _enumerationAnswersList?.Clear();
            if (_enumerationAnswersList == null) return;

            for (int i = 0; i < _enumerationAnswers.Count; i++)
            {
                _enumerationAnswersList.Add(BuildEnumerationAnswerRow(i));
            }
        }

        private VisualElement BuildEnumerationAnswerRow(int index)
        {
            var row = new VisualElement();
            row.AddToClassList("enumeration-answer-row");

            var badge = new Label($"{index + 1}");
            badge.AddToClassList("enumeration-answer-index-badge");

            var textLabel = new Label(_enumerationAnswers[index]);
            textLabel.AddToClassList("enumeration-answer-field");

            var removeButton = new Button(() => RemoveEnumerationAnswer(index));
            removeButton.AddToClassList("enumeration-answer-remove-button");
            var removeIcon = new VisualElement();
            removeIcon.AddToClassList("enumeration-answer-remove-icon");
            removeButton.Add(removeIcon);

            row.Add(badge);
            row.Add(textLabel);
            row.Add(removeButton);

            return row;
        }

        private void RemoveEnumerationAnswer(int index)
        {
            if (index < 0 || index >= _enumerationAnswers.Count) return;
            _enumerationAnswers.RemoveAt(index);
            RefreshEnumerationAnswersUI();
        }

        // ---------------- Image-Based: anatomy system, picked via cards ----------------

        private void OnImageBasedSystemButtonClicked(ClickEvent evt)
        {
            var target = evt.target as Button;
            if (target == null || _imageBasedSystemButtons == null) return;

            string system = _imageBasedSystemButtons.FirstOrDefault(kvp => kvp.Value == target).Key;
            if (system == null) return;

            // Switching systems invalidates any structure already picked for the
            // previous system - never carry a Skeletal pick over onto Muscular, etc.
            if (system != _selectedImageBasedSystem) ClearImageBasedSelection();

            _selectedImageBasedSystem = system;
            RefreshImageBasedSystemButtons();

            OpenAnatomyScreenForStructureSelection(system);
        }

        private void RefreshImageBasedSystemButtons()
        {
            if (_imageBasedSystemButtons == null) return;
            foreach (var kvp in _imageBasedSystemButtons)
            {
                kvp.Value?.EnableInClassList("selected", kvp.Key == _selectedImageBasedSystem);
            }
        }

        private void OnImageBasedChangeStructureClicked(ClickEvent evt)
        {
            OpenAnatomyScreenForStructureSelection(_selectedImageBasedSystem ?? AnatomySystemDisplayChoices[0]);
        }

        /// <summary>Opens the existing Student Anatomy Screen (see
        /// UIManager.ShowStudentAnatomyScreenForTeacherSelection /
        /// AnatomyTeacherSelectionController) for the given system, in teacher
        /// selection mode, so the teacher can tap the actual structure from the 3D
        /// model instead of typing/picking from a hardcoded list. Marks this modal to
        /// resume at Step 2 the moment the teacher returns (see OnEnable).</summary>
        private void OpenAnatomyScreenForStructureSelection(string systemKey)
        {
            if (_quizPendingQuestion == null || string.IsNullOrEmpty(systemKey)) return;
            if (!Enum.TryParse<AnatomySystem>(systemKey, out var anatomySystem))
            {
                Debug.LogWarning($"[AdminQuizManagementController] Unknown anatomy system '{systemKey}' - cannot open the Anatomy Screen.");
                return;
            }

            _resumeAddQuestionOnNextEnable = true;
            _pendingResumeQuiz = _quizPendingQuestion;

            UIManager.Instance.ShowStudentAnatomyScreenForTeacherSelection(anatomySystem);
        }

        /// <summary>Called via AnatomyTeacherSelectionController.OnTeacherStructureSelected
        /// the instant the teacher taps "SELECT THIS STRUCTURE" - fires while this
        /// controller is disabled (the Anatomy Screen is still the active screen), so
        /// this only ever stores the pick; the resumed modal picks it up in OnEnable
        /// via RefreshImageBasedSelectedStructurePanel.</summary>
        private void OnTeacherStructureSelected(AnatomyTeacherSelectionController.TeacherStructureSelectionResult result)
        {
            if (result == null) return;

            _selectedImageBasedSystem = result.SystemKey;
            _imageBasedSelectedSystemKey = result.SystemKey;
            _imageBasedSelectedSystemDisplayName = result.SystemDisplayName;
            _imageBasedSelectedStructureKey = result.StructureKey;
            _imageBasedSelectedStructureDisplayName = result.StructureDisplayName;
        }

        private void ClearImageBasedSelection()
        {
            _imageBasedSelectedSystemKey = null;
            _imageBasedSelectedSystemDisplayName = null;
            _imageBasedSelectedStructureKey = null;
            _imageBasedSelectedStructureDisplayName = null;
            RefreshImageBasedSelectedStructurePanel();
        }

        /// <summary>Shows the "Selected Structure" summary + Change Structure button
        /// once a structure has been picked (plan section 10), otherwise shows the
        /// "please select a structure" prompt in its place.</summary>
        private void RefreshImageBasedSelectedStructurePanel()
        {
            bool hasSelection = !string.IsNullOrEmpty(_imageBasedSelectedStructureDisplayName);

            _imageBasedSelectedStructurePanel?.EnableInClassList("hidden", !hasSelection);

            if (hasSelection)
            {
                if (_imageBasedSelectedStructureNameLabel != null)
                    _imageBasedSelectedStructureNameLabel.text = _imageBasedSelectedStructureDisplayName;
                if (_imageBasedSelectedStructureSystemLabel != null)
                    _imageBasedSelectedStructureSystemLabel.text = _imageBasedSelectedSystemDisplayName;
                ClearError(_imageBasedStructureError);
            }
        }

        // ---------------- Difficulty: three buttons instead of a dropdown ----------------

        private void OnDifficultyButtonClicked(ClickEvent evt)
        {
            var target = evt.target as Button;
            if (target == null || _difficultyButtons == null) return;

            string difficulty = _difficultyButtons.FirstOrDefault(kvp => kvp.Value == target).Key;
            if (difficulty == null) return;

            _selectedDifficulty = difficulty;
            RefreshDifficultyButtons();
            ApplyPointsForSelectedDifficulty();
        }

        private void RefreshDifficultyButtons()
        {
            if (_difficultyButtons == null) return;
            foreach (var kvp in _difficultyButtons)
            {
                kvp.Value?.EnableInClassList($"selected-{kvp.Key}", kvp.Key == _selectedDifficulty);
            }
        }

        /// <summary>Syncs the (read-only) points field to whatever _selectedDifficulty
        /// currently is, via PointsForDifficulty (this teacher's configured
        /// Easy/Medium/HardPoints, from Gamification Settings). Called on modal open
        /// and every time a difficulty button is clicked, so the displayed value is
        /// always exactly what OnAddQuestionSubmitClicked will persist - the field
        /// itself is disabled (see OpenAddQuestionModal) purely for display, since
        /// points are no longer teacher-editable here (they're editable in
        /// Gamification Settings instead).</summary>
        private void ApplyPointsForSelectedDifficulty()
        {
            int points = PointsForDifficulty(_selectedDifficulty);
            if (_questionPointsField != null)
            {
                _questionPointsField.value = points.ToString();
                _questionPointsField.SetEnabled(false);
            }
        }

        // ---------------- Step 2 -> Step 3 ----------------

        private void OnAddQuestionStep2NextClicked(ClickEvent evt)
        {
            if (!StageQuestionFromStep2()) return;
            SetAddQuestionWizardStep(3);
        }

        private void OnAddQuestionStep3BackClicked(ClickEvent evt) => SetAddQuestionWizardStep(2);

        /// <summary>Validates the fields relevant to the selected question type and, on
        /// success, stages the question text/options/correct answer for the step 3 submit
        /// handler to combine with difficulty/points. Every type funnels into the same
        /// staged correct-answer string that QuizService actually persists - Enumeration
        /// and Multiple Identification as a comma-separated list, True/False as literally
        /// "True"/"False".</summary>
        private bool StageQuestionFromStep2()
        {
            string typeSlug = _selectedQuestionTypeSlug ?? TypeMultipleChoice;

            bool valid = true;
            string questionText = _questionTextField?.value?.Trim();

            // Image-Based questions don't require the teacher to type a question -
            // one is generated below from the selected system.
            if (typeSlug != TypeImageBased && string.IsNullOrEmpty(questionText))
            {
                SetError(_questionTextError, "Please enter the question");
                valid = false;
            }
            else
            {
                ClearError(_questionTextError);
            }

            var options = new List<string>();
            string correctAnswer = string.Empty;

            switch (typeSlug)
            {
                case TypeMultipleChoice:
                    {
                        foreach (var optionField in new[] { _option1Field, _option2Field, _option3Field, _option4Field })
                        {
                            string option = optionField?.value?.Trim() ?? string.Empty;
                            options.Add(option);
                            bool empty = string.IsNullOrEmpty(option);
                            MarkFieldInvalid(optionField, empty);
                            if (empty) valid = false;
                        }

                        int selected = GetMcSelectedIndex();
                        if (selected < 0)
                        {
                            SetError(_mcCorrectAnswerError, "Please select the correct answer");
                            valid = false;
                        }
                        else
                        {
                            ClearError(_mcCorrectAnswerError);
                            correctAnswer = selected < options.Count ? options[selected] : string.Empty;
                        }
                        break;
                    }
                case TypeMultipleIdentification:
                    {
                        foreach (var optionField in new[] { _option1Field, _option2Field, _option3Field, _option4Field })
                        {
                            string option = optionField?.value?.Trim() ?? string.Empty;
                            options.Add(option);
                            bool empty = string.IsNullOrEmpty(option);
                            MarkFieldInvalid(optionField, empty);
                            if (empty) valid = false;
                        }

                        var selectedIndices = GetMiSelectedIndices();
                        if (selectedIndices.Count == 0)
                        {
                            SetError(_miCorrectAnswerError, "Please select at least one correct answer");
                            valid = false;
                        }
                        else
                        {
                            ClearError(_miCorrectAnswerError);
                            correctAnswer = string.Join(", ", selectedIndices.Where(i => i < options.Count).Select(i => options[i]));
                        }
                        break;
                    }
                case TypeTrueFalse:
                    {
                        correctAnswer = _selectedTrueFalseAnswer;
                        break;
                    }
                case TypeEnumeration:
                    {
                        if (_enumerationAnswers.Count == 0)
                        {
                            SetError(_enumerationAnswerError, "Please add at least one answer");
                            valid = false;
                        }
                        else
                        {
                            ClearError(_enumerationAnswerError);
                            correctAnswer = string.Join(", ", _enumerationAnswers);
                        }
                        break;
                    }
                case TypeImageBased:
                    {
                        string system = _selectedImageBasedSystem ?? AnatomySystemDisplayChoices[0];

                        // The teacher must have actually tapped a structure on the 3D
                        // model - never silently fall back to the first/any structure
                        // (plan section 12).
                        if (string.IsNullOrEmpty(_imageBasedSelectedStructureDisplayName))
                        {
                            SetError(_imageBasedStructureError, "Please select a structure from the anatomy model.");
                            valid = false;
                        }
                        else
                        {
                            ClearError(_imageBasedStructureError);
                            // MUST be the displayName, never the internal structureKey -
                            // see the plan's section 7.
                            correctAnswer = _imageBasedSelectedStructureDisplayName;
                            string noun = AnatomySystemNoun.TryGetValue(system ?? string.Empty, out var n) ? n : "structure";
                            if (string.IsNullOrEmpty(questionText)) questionText = $"What is the name of the highlighted {noun}?";
                        }
                        break;
                    }
                default: // Identification
                    {
                        correctAnswer = _correctAnswerField?.value?.Trim() ?? string.Empty;
                        bool empty = string.IsNullOrEmpty(correctAnswer);
                        MarkFieldInvalid(_correctAnswerField, empty);
                        if (empty)
                        {
                            SetError(_correctAnswerError, "Please enter the correct answer");
                            valid = false;
                        }
                        else
                        {
                            ClearError(_correctAnswerError);
                        }
                        break;
                    }
            }

            if (!valid)
            {
                SetStatus(_addQuestionStatusLabel, "Please fix the highlighted fields.");
                return false;
            }

            SetStatus(_addQuestionStatusLabel, string.Empty);

            // Every type above still lands in the same underlying data fields that
            // QuizService persists - keep the (possibly hidden) correct-answer-field in
            // sync so nothing downstream needs to know which control the teacher used.
            if (_correctAnswerField != null) _correctAnswerField.value = correctAnswer;

            _stagedQuestionText = questionText;
            _stagedCorrectAnswer = correctAnswer;
            _stagedOptions = (typeSlug == TypeMultipleChoice || typeSlug == TypeMultipleIdentification)
                ? options
                : null;

            return true;
        }

        private void OnAddQuestionSubmitClicked(ClickEvent evt)
        {
            if (_quizPendingQuestion == null)
            {
                CloseAddQuestionModal();
                return;
            }

            string typeSlug = _selectedQuestionTypeSlug ?? TypeMultipleChoice;

            // Points are derived from the selected difficulty, using this teacher's
            // configured Easy/Medium/HardPoints (Gamification Settings) via
            // PointsForDifficulty - read straight from there rather than from
            // _questionPointsField, which is now a disabled/display-only mirror of it.
            int points = PointsForDifficulty(_selectedDifficulty);

            var question = new QuestionData
            {
                QuestionText = _stagedQuestionText,
                QuestionTypeSlug = typeSlug,
                CorrectAnswer = _stagedCorrectAnswer,
                Difficulty = _selectedDifficulty,
                Points = points,
            };

            if (_stagedOptions != null) question.Options.AddRange(_stagedOptions);

            if (typeSlug == TypeImageBased)
            {
                question.AnatomySystemKey = _imageBasedSelectedSystemKey;
                question.AnatomySystemDisplayName = _imageBasedSelectedSystemDisplayName;
                question.StructureKey = _imageBasedSelectedStructureKey;
                question.StructureDisplayName = _imageBasedSelectedStructureDisplayName;
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

                SetAddQuestionWizardStep(4);
            });
        }

        private void OnAddQuestionSuccessAddAnotherClicked(ClickEvent evt)
        {
            // Keep _quizPendingQuestion (same quiz), just reset the wizard back to step 1.
            var quiz = _quizPendingQuestion;
            OpenAddQuestionModal();
            _quizPendingQuestion = quiz;
        }

        private void OnAddQuestionSuccessDoneClicked(ClickEvent evt) => CloseAddQuestionModal();

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
                SubmissionType = record.SubmissionType,
                Instructions = record.Instructions,
                FileConfig = record.FileConfig,
                PointsPossible = record.PointsPossible,
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
            // SubmissionType itself never changes (see QuizData.SubmissionType) -
            // only instructions/fileConfig may have been updated.
            if (record.IsFileSubmission)
            {
                quiz.Instructions = record.Instructions;
                quiz.FileConfig = record.FileConfig;
            }
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
                AnatomySystemKey = record.AnatomySystemKey,
                AnatomySystemDisplayName = record.AnatomySystemDisplayName,
                StructureKey = record.StructureKey,
                StructureDisplayName = record.StructureDisplayName,
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
                AnatomySystemKey = data.AnatomySystemKey,
                AnatomySystemDisplayName = data.AnatomySystemDisplayName,
                StructureKey = data.StructureKey,
                StructureDisplayName = data.StructureDisplayName,
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

        /// <summary>Live keystroke filter for TextFields that should only ever hold a
        /// whole number (passing score, custom time limit, max file size, file points).
        /// A TextField has no built-in "digits only" mode, so without this a teacher can
        /// type letters/symbols into it and only find out it's rejected on submit - this
        /// strips anything non-digit as it's typed instead. Shared across every numeric
        /// TextField (registered as the same delegate on each) so Wire/UnregisterCallbacks
        /// only need one pair of calls per field.</summary>
        private void OnNumericFieldChanged(ChangeEvent<string> evt)
        {
            var field = evt.target as TextField;
            if (field == null) return;

            string digitsOnly = new string((evt.newValue ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digitsOnly != evt.newValue)
            {
                field.SetValueWithoutNotify(digitsOnly);
            }
        }

        private static int ParseIntOrDefault(TextField field, int fallback)
        {
            if (field != null && int.TryParse(field.value, out int value))
            {
                return value;
            }
            return fallback;
        }

        /// <summary>Toggles a red outline on a text field that failed validation
        /// (empty answer choice, empty correct answer, etc).</summary>
        private void MarkFieldInvalid(TextField field, bool invalid)
        {
            field?.EnableInClassList("field-invalid", invalid);
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

            // Create Quiz wizard: header banner reuses the header's texture too, so
            // it reads as the same brand color as the header rather than a flat
            // fallback green. The final "Create Quiz" submit button already gets
            // its own texture above.
            if (_createQuizWizardHeader != null && _headerGradientTexture != null)
            {
                _createQuizWizardHeader.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            // Add Question wizard: same treatment, so its header banner matches
            // the Create Quiz wizard header's brand color instead of staying
            // uncolored.
            if (_addQuestionWizardHeader != null && _headerGradientTexture != null)
            {
                _addQuestionWizardHeader.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            // ...and its step 2 "Next" button + the success view's "Add Another
            // Question" button reuse the same texture too, so they read as the
            // same brand color as the header rather than a flat fallback green.
            // (Step 3's "Add Question" submit button already gets its own
            // texture above via _addQuestionSubmitButton.)
            if (_addQuestionStep2NextButton != null && _headerGradientTexture != null)
            {
                _addQuestionStep2NextButton.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_addQuestionSuccessAddAnotherButton != null && _headerGradientTexture != null)
            {
                _addQuestionSuccessAddAnotherButton.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_quizDeadlineSetButton != null && _headerGradientTexture != null)
            {
                _quizDeadlineSetButton.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            // Quiz detail view: reuses the header's texture for its own header
            // background, full-width "Add Question" button, and whichever
            // segmented tab (Overview/Questions) is active, rather than
            // generating another texture for each.
            if (_quizDetailHeader != null && _headerGradientTexture != null)
            {
                _quizDetailHeader.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_quizDetailAddQuestionButton != null && _headerGradientTexture != null)
            {
                _quizDetailAddQuestionButton.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }
            RefreshQuizDetailTabGradient();
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