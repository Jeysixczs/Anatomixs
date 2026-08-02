using Anatomia3D.UI;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class StudentExplore3dController : MonoBehaviour
{
    [Header("Gradient colors (matches the mock)")]
    [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f);
    [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);
    [SerializeField] private bool diagonalGradient = true;
    [SerializeField, Range(2, 256)] private int gradientTextureResolution = 64;

    private UIDocument _document;
    private VisualElement _root;
    private VisualElement _screenRoot;
    private Texture2D _headerGradientTexture;

    private VisualElement _header;
    private VisualElement _headerTopRow;
    private Button _backButton;
    private Label _headerTitle;
    private Label _headerSubtitle;

    private void OnEnable()
    {
        Debug.Log("[StudentExplore3dController] OnEnable called");

        // Get the root from UIManager
        if (UIManager.Instance != null)
        {
            var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
            if (uiDocument != null)
            {
                _root = uiDocument.rootVisualElement;
            }
        }

        // Fallback: use this component's document
        if (_root == null)
        {
            if (_document == null) _document = GetComponent<UIDocument>();
            if (_document != null) _root = _document.rootVisualElement;
        }

        if (_root == null)
        {
            Debug.LogError("[StudentExplore3dController] Root is null!");
            return;
        }

        QueryElements();
        WireCallbacks();
        ApplyHeaderGradient();
    }

    private void OnDisable()
    {
        if (_screenRoot == null) return;

        _backButton?.UnregisterCallback<ClickEvent>(OnbackToDashboard);

        if (_headerGradientTexture != null)
        {
            Destroy(_headerGradientTexture);
            _headerGradientTexture = null;
        }
    }

    private void QueryElements()
    {
        // Find the screen-root wrapper (matches the name used in the UXML)
        _screenRoot = _root.Q<VisualElement>("screen-root");

        // If no wrapper, use root directly
        if (_screenRoot == null)
        {
            Debug.LogWarning("[StudentExplore3dController] screen-root not found, using root directly");
            _screenRoot = _root;
        }

        _header = _screenRoot.Q<VisualElement>("header");
        _headerTopRow = _screenRoot.Q<VisualElement>("header-top-row");
        _backButton = _screenRoot.Q<Button>("back-button");
        _headerTitle = _screenRoot.Q<Label>("header-title");
        _headerSubtitle = _screenRoot.Q<Label>("header-subtitle");

        Debug.Log($"[StudentExplore3dController] Found back button: {_backButton != null}, header: {_header != null}");
    }

    private void WireCallbacks()
    {
        if (_backButton != null)
        {
            _backButton.UnregisterCallback<ClickEvent>(OnbackToDashboard);
            _backButton.RegisterCallback<ClickEvent>(OnbackToDashboard);
        }
    }

    private void OnbackToDashboard(ClickEvent evt)
    {
        Debug.Log("[StudentExplore3dController] Navigating back to dashboard");
        UIManager.Instance.ShowStudentDashboard();
    }

    private void ApplyHeaderGradient()
    {
        if (_header == null) return;

        if (_headerGradientTexture != null)
        {
            Destroy(_headerGradientTexture);
        }

        int size = Mathf.Max(2, gradientTextureResolution);
        int height = diagonalGradient ? size : 1;

        _headerGradientTexture = new Texture2D(size, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "HeaderGradientTexture"
        };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float t = diagonalGradient
                    ? (x + y) / (float)(size - 1 + height - 1)
                    : x / (float)(size - 1);
                _headerGradientTexture.SetPixel(x, y, Color.Lerp(gradientStart, gradientEnd, t));
            }
        }
        _headerGradientTexture.Apply();

        _header.style.backgroundColor = new StyleColor(StyleKeyword.Null);
        _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
        _header.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(Length.Percent(100), Length.Percent(100)));
        _header.style.backgroundRepeat = new StyleBackgroundRepeat(new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat));
    }
}