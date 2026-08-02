using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentAchievements.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button
    ///  - Applies the purple->pink gradient to the header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetSummaryData()/SetExpertBadgeProgress() so gameplay code
    ///    can push real values in instead of the placeholder mock numbers.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentAchievementsController : MonoBehaviour
    {
        [Header("Gradient colors (matches the other screens)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        private Label _totalPointsLabel;
        private Label _badgesEarnedLabel;

        private VisualElement _expertProgressFill;
        private Label _expertProgressCountLabel;
        private Label _expertProgressPercentLabel;

        private void OnEnable()
        {
            Debug.Log("[StudentAchievementsController] OnEnable called");

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
                Debug.LogError("[StudentAchievementsController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
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
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentAchievementsController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _totalPointsLabel = _screenRoot.Q<Label>("total-points-label");
            _badgesEarnedLabel = _screenRoot.Q<Label>("badges-earned-label");

            _expertProgressFill = _screenRoot.Q<VisualElement>("expert-progress-fill");
            _expertProgressCountLabel = _screenRoot.Q<Label>("expert-progress-count-label");
            _expertProgressPercentLabel = _screenRoot.Q<Label>("expert-progress-percent-label");

            Debug.Log($"[StudentAchievementsController] Found back button: {_backButton != null}, header: {_header != null}");
        }

        private void WireCallbacks()
        {
            if (_backButton != null)
            {
                _backButton.RegisterCallback<ClickEvent>(OnBackClicked);
            }

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push real values into the summary card at the top of the header.</summary>
        public void SetSummaryData(int totalPoints, int badgesEarned, int badgesTotal)
        {
            if (_totalPointsLabel != null) _totalPointsLabel.text = totalPoints.ToString("N0");
            if (_badgesEarnedLabel != null) _badgesEarnedLabel.text = $"{badgesEarned} / {badgesTotal}";
        }

        /// <summary>Update the progress bar/labels for the locked "Expert" badge.</summary>
        public void SetExpertBadgeProgress(int currentPoints, int targetPoints)
        {
            float pct = targetPoints > 0 ? Mathf.Clamp01((float)currentPoints / targetPoints) : 0f;

            if (_expertProgressFill != null)
                _expertProgressFill.style.width = new Length(pct * 100f, LengthUnit.Percent);

            if (_expertProgressCountLabel != null)
                _expertProgressCountLabel.text = $"{currentPoints} / {targetPoints}";

            if (_expertProgressPercentLabel != null)
                _expertProgressPercentLabel.text = $"{Mathf.RoundToInt(pct * 100f)}%";
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentAchievementsController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
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

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
            }

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
                name = "AchievementsHeaderGradientTexture"
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
