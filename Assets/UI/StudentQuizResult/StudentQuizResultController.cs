using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentQuizResult.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up "Back to Dashboard", "Retry" and "Share"
    ///  - Applies a runtime gradient to the header and the "Back to Dashboard"
    ///    button, choosing a color pair + headline based on the score tier
    ///    (matches the "Keep Practicing!" orange/red mock for a low score)
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetResult() so gameplay code can push the real quiz outcome
    ///    in instead of the placeholder mock numbers.
    ///
    /// Hook up your real retry/share calls inside OnRetryClicked() / OnShareClicked()
    /// - e.g. call into your existing QuizManager/native share sheet here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentQuizResultController : MonoBehaviour
    {
        [Header("Score tier thresholds (percent, 0-100)")]
        [SerializeField] private float greatScoreThreshold = 80f;
        [SerializeField] private float goodScoreThreshold = 50f;

        [Header("Gradient colors - low score (matches the mock)")]
        [SerializeField] private Color lowGradientStart = new Color(0.949f, 0.325f, 0.078f); // orange
        [SerializeField] private Color lowGradientEnd = new Color(0.863f, 0.078f, 0.078f);    // red

        [Header("Gradient colors - mid score")]
        [SerializeField] private Color midGradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color midGradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("Gradient colors - high score")]
        [SerializeField] private Color highGradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color highGradientEnd = new Color(0.145f, 0.388f, 0.922f);    // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;
        private Texture2D _buttonGradientTexture;

        private VisualElement _header;

        private Label _resultTitleLabel;
        private Label _quizNameLabel;

        private Label _scorePercentLabel;
        private Label _correctCountLabel;
        private Label _incorrectCountLabel;
        private Label _totalCountLabel;

        private Label _pointsEarnedValueLabel;
        private VisualElement _pointsProgressFill;

        private Label _pointsMiniValueLabel;
        private Label _bonusMiniValueLabel;

        private Button _backToDashboardButton;
        private Button _retryButton;
        private Button _shareButton;

        // Cached from the last SetResult() call so Retry can reference the quiz.
        private string _lastQuizName;

        private void OnEnable()
        {
            Debug.Log("[StudentQuizResultController] OnEnable called");

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
                Debug.LogError("[StudentQuizResultController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            WireCallbacks();
            UpdateResponsiveLayout();

            // Re-apply the gradient for whatever score is currently displayed
            // (either the mock values in the UXML, or the last SetResult() call).
            ApplyGradientForCurrentScore();
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

            _backToDashboardButton?.UnregisterCallback<ClickEvent>(OnBackToDashboardClicked);
            _retryButton?.UnregisterCallback<ClickEvent>(OnRetryClicked);
            _shareButton?.UnregisterCallback<ClickEvent>(OnShareClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentQuizResultController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");

            _resultTitleLabel = _screenRoot.Q<Label>("result-title-label");
            _quizNameLabel = _screenRoot.Q<Label>("quiz-name-label");

            _scorePercentLabel = _screenRoot.Q<Label>("score-percent-label");
            _correctCountLabel = _screenRoot.Q<Label>("correct-count-label");
            _incorrectCountLabel = _screenRoot.Q<Label>("incorrect-count-label");
            _totalCountLabel = _screenRoot.Q<Label>("total-count-label");

            _pointsEarnedValueLabel = _screenRoot.Q<Label>("points-earned-value-label");
            _pointsProgressFill = _screenRoot.Q<VisualElement>("points-progress-fill");

            _pointsMiniValueLabel = _screenRoot.Q<Label>("points-mini-value-label");
            _bonusMiniValueLabel = _screenRoot.Q<Label>("bonus-mini-value-label");

            _backToDashboardButton = _screenRoot.Q<Button>("back-to-dashboard-button");
            _retryButton = _screenRoot.Q<Button>("retry-button");
            _shareButton = _screenRoot.Q<Button>("share-button");

            Debug.Log($"[StudentQuizResultController] Found back-to-dashboard: {_backToDashboardButton != null}, header: {_header != null}");
        }

        private void WireCallbacks()
        {
            if (_backToDashboardButton != null)
            {
                _backToDashboardButton.RegisterCallback<ClickEvent>(OnBackToDashboardClicked);
            }

            if (_retryButton != null)
            {
                _retryButton.RegisterCallback<ClickEvent>(OnRetryClicked);
            }

            if (_shareButton != null)
            {
                _shareButton.RegisterCallback<ClickEvent>(OnShareClicked);
            }

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>
        /// Push a real quiz outcome into the result screen. Also picks the
        /// headline ("Keep Practicing!" / "Good Job!" / "Excellent Work!") and
        /// the header/button gradient based on the resulting score percentage.
        /// </summary>
        public void SetResult(
            string quizName,
            int correctCount,
            int incorrectCount,
            int pointsEarned,
            int pointsPossible,
            int bonusXp)
        {
            _lastQuizName = quizName;

            int total = correctCount + incorrectCount;
            float percent = total > 0 ? (correctCount / (float)total) * 100f : 0f;

            if (_quizNameLabel != null) _quizNameLabel.text = quizName;
            if (_scorePercentLabel != null) _scorePercentLabel.text = $"{Mathf.RoundToInt(percent)}%";
            if (_correctCountLabel != null) _correctCountLabel.text = correctCount.ToString();
            if (_incorrectCountLabel != null) _incorrectCountLabel.text = incorrectCount.ToString();
            if (_totalCountLabel != null) _totalCountLabel.text = total.ToString();

            if (_pointsEarnedValueLabel != null) _pointsEarnedValueLabel.text = $"{pointsEarned} / {pointsPossible}";
            if (_pointsProgressFill != null)
            {
                float pointsPct = pointsPossible > 0 ? Mathf.Clamp01((float)pointsEarned / pointsPossible) : 0f;
                _pointsProgressFill.style.width = new Length(pointsPct * 100f, LengthUnit.Percent);
            }

            if (_pointsMiniValueLabel != null) _pointsMiniValueLabel.text = $"+{pointsEarned}";
            if (_bonusMiniValueLabel != null) _bonusMiniValueLabel.text = $"+{bonusXp}";

            ApplyGradientForScore(percent);
        }

        // ---------------- Button handlers ----------------

        private void OnBackToDashboardClicked(ClickEvent evt)
        {
            Debug.Log("[StudentQuizResultController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnRetryClicked(ClickEvent evt)
        {
            Debug.Log($"[StudentQuizResultController] Retry tapped for: {_lastQuizName ?? _quizNameLabel?.text}");

            // TODO: replace with your real quiz-restart call, e.g.:
            // QuizManager.Instance.RetryLastQuiz();
            // For now, send the student back to quiz selection so they can start again.
            UIManager.Instance.ShowStudentQuizSelection();
        }

        private void OnShareClicked(ClickEvent evt)
        {
            Debug.Log("[StudentQuizResultController] Share tapped.");

            // TODO: hook up a native share sheet here, e.g.:
            // NativeShare.Instance.ShareText($"I scored {_scorePercentLabel.text} on {_quizNameLabel.text}!");
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradient / score tier (USS has no linear-gradient) ----------------

        private void ApplyGradientForCurrentScore()
        {
            float percent = 0f;

            if (_correctCountLabel != null && _totalCountLabel != null &&
                int.TryParse(_correctCountLabel.text, out int correct) &&
                int.TryParse(_totalCountLabel.text, out int total) &&
                total > 0)
            {
                percent = (correct / (float)total) * 100f;
            }

            ApplyGradientForScore(percent);
        }

        private void ApplyGradientForScore(float percent)
        {
            Color start, end;
            string title;

            if (percent >= greatScoreThreshold)
            {
                start = highGradientStart;
                end = highGradientEnd;
                title = "Excellent Work!";
            }
            else if (percent >= goodScoreThreshold)
            {
                start = midGradientStart;
                end = midGradientEnd;
                title = "Good Job!";
            }
            else
            {
                start = lowGradientStart;
                end = lowGradientEnd;
                title = "Keep Practicing!";
            }

            if (_resultTitleLabel != null) _resultTitleLabel.text = title;

            if (_header != null)
            {
                if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
                _headerGradientTexture = BuildGradientTexture(start, end);
                _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_backToDashboardButton != null)
            {
                if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
                _buttonGradientTexture = BuildGradientTexture(start, end);
                _backToDashboardButton.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "QuizResultGradientTexture"
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
