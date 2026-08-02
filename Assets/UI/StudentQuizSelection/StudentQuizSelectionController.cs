using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentQuizSelection.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and each quiz's "Start Quiz" button
    ///  - Applies the green->blue gradient to the header and Start Quiz buttons at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetQuizStats() so gameplay code can push real values in instead
    ///    of the placeholder mock numbers.
    ///
    /// Hook up your real quiz-launch call inside OnStartQuizClicked()
    /// - e.g. call into your existing QuizManager/gameplay scene loader here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentQuizSelectionController : MonoBehaviour
    {
        [Header("Gradient colors (matches the mock: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;
        private Texture2D _buttonGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        private Button _startQuizButton1;
        private Button _startQuizButton2;

        private Label _quizTitle1;
        private Label _quizTitle2;

        private Label _statsCompletedLabel;
        private Label _statsAvgScoreLabel;
        private Label _statsPerfectLabel;

        private void OnEnable()
        {
            Debug.Log("[StudentQuizSelectionController] OnEnable called");

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
                Debug.LogError("[StudentQuizSelectionController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();
        }

        private void OnDisable()
        {
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
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _startQuizButton1?.UnregisterCallback<ClickEvent>(OnStartQuiz1Clicked);
            _startQuizButton2?.UnregisterCallback<ClickEvent>(OnStartQuiz2Clicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentQuizSelectionController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _startQuizButton1 = _screenRoot.Q<Button>("start-quiz-button-1");
            _startQuizButton2 = _screenRoot.Q<Button>("start-quiz-button-2");

            _quizTitle1 = _screenRoot.Q<Label>("quiz-title-1");
            _quizTitle2 = _screenRoot.Q<Label>("quiz-title-2");

            _statsCompletedLabel = _screenRoot.Q<Label>("quiz-stats-completed-label");
            _statsAvgScoreLabel = _screenRoot.Q<Label>("quiz-stats-avg-score-label");
            _statsPerfectLabel = _screenRoot.Q<Label>("quiz-stats-perfect-label");

            Debug.Log($"[StudentQuizSelectionController] Found start buttons: {_startQuizButton1 != null}/{_startQuizButton2 != null}, header: {_header != null}");
        }

        private void WireCallbacks()
        {
            if (_backButton != null)
            {
                _backButton.RegisterCallback<ClickEvent>(OnBackClicked);
            }

            if (_startQuizButton1 != null)
            {
                _startQuizButton1.RegisterCallback<ClickEvent>(OnStartQuiz1Clicked);
            }

            if (_startQuizButton2 != null)
            {
                _startQuizButton2.RegisterCallback<ClickEvent>(OnStartQuiz2Clicked);
            }

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push real values into the "Your Quiz Stats" summary card.</summary>
        public void SetQuizStats(int completed, float avgScorePercent, int perfectScores)
        {
            if (_statsCompletedLabel != null) _statsCompletedLabel.text = completed.ToString("N0");
            if (_statsAvgScoreLabel != null) _statsAvgScoreLabel.text = $"{Mathf.RoundToInt(avgScorePercent)}%";
            if (_statsPerfectLabel != null) _statsPerfectLabel.text = perfectScores.ToString("N0");
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentQuizSelectionController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnStartQuiz1Clicked(ClickEvent evt)
        {
            string quizName = _quizTitle1 != null ? _quizTitle1.text : "quiz 1";
            Debug.Log($"[StudentQuizSelectionController] Start Quiz tapped: {quizName}");

            // TODO: replace with your real quiz-launch call, e.g.:
            // QuizManager.Instance.StartQuiz("skeletal-system-basics");
            UIManager.Instance.ShowStudentQuizResult();
        }

        private void OnStartQuiz2Clicked(ClickEvent evt)
        {
            string quizName = _quizTitle2 != null ? _quizTitle2.text : "quiz 2";
            Debug.Log($"[StudentQuizSelectionController] Start Quiz tapped: {quizName}");

            // TODO: replace with your real quiz-launch call, e.g.:
            // QuizManager.Instance.StartQuiz("muscular-system-advanced");
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

        private void ApplyGradients()
        {
            if (_header != null)
            {
                if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
                _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
            _buttonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);

            if (_startQuizButton1 != null)
                _startQuizButton1.style.backgroundImage = new StyleBackground(_buttonGradientTexture);

            if (_startQuizButton2 != null)
                _startQuizButton2.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "QuizSelectionGradientTexture"
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
