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
  
    private Button _backButton;
    private Label _headerTitle;
  

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

        if (_backButton != null) _backButton.clicked -= OnBackButtonClicked;

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
    
        _backButton = _screenRoot.Q<Button>("back-button");
        _headerTitle = _screenRoot.Q<Label>("header-title");
       

        Debug.Log($"[StudentExplore3dController] Found back button: {_backButton != null}, header: {_header != null}");
    }

    private void WireCallbacks()
    {
        if (_backButton != null)
        {
            // Use the Button's built-in .clicked event (same reliable input path as
            // the anatomy-system cards) instead of manually registering ClickEvent.
            _backButton.clicked -= OnBackButtonClicked;
            _backButton.clicked += OnBackButtonClicked;
        }

        WireAnatomySystemCards();
    }

    // Connects each existing Card List card (see StudentExplore3d.uxml -
    // "card-list") to the matching AnatomySystem, so tapping it opens the
    // one reusable Anatomy Screen loaded with that system's model/database
    // (see UIManager.ShowStudentAnatomyScreen(AnatomySystem) /
    // AnatomyScreenController.ResolveAnatomySystem). Cards aren't
    // individually named in the UXML, so each is matched by the same
    // icon-skeletal/icon-muscular/icon-cardio class its icon-box already
    // carries (see StudentExplore3d.uss) rather than by list position -
    // reordering the cards in the UXML can't silently mis-wire them. Safe
    // to call every OnEnable: ShowScreen clones a brand-new UI tree each
    // time the screen opens, so there are never stale elements left with a
    // callback registered twice.
    private void WireAnatomySystemCards()
    {
        if (_screenRoot == null)
        {
            Debug.LogError("[AnatomyNav] WireAnatomySystemCards: _screenRoot is NULL, cannot wire cards.");
            return;
        }

      
        var cards = _screenRoot.Query<Button>(className: "card").ToList();
        

        

        foreach (var card in cards)
        {
            AnatomySystem system;
            if (card.Q<VisualElement>(className: "icon-skeletal") != null)
                system = AnatomySystem.Skeletal;
            else if (card.Q<VisualElement>(className: "icon-muscular") != null)
                system = AnatomySystem.Muscular;
            else if (card.Q<VisualElement>(className: "icon-cardio") != null)
                system = AnatomySystem.Cardiovascular;
            else
            {
                Debug.LogWarning($"[AnatomyNav] Card '{card.name}' matched .card but has no known icon class — skipping.");
                continue; // not one of the three anatomy-system cards (e.g. none - skip)
            }
         
            card.clicked += () =>
            {
               

                try
                {
                    UIManager.Instance.ShowStudentAnatomyScreen(system);
                    Debug.Log($"[AnatomyNav] ShowStudentAnatomyScreen({system}) called successfully.");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[AnatomyNav] EXCEPTION in ShowStudentAnatomyScreen({system}): {e}");
                 
                }
            };
        }
    }

    private void OnBackButtonClicked()
    {
        Debug.Log("[StudentExplore3dController] Back button tapped, navigating back to dashboard");
        if (_headerTitle != null)
            _headerTitle.text = $"Back tapped @ {Time.time:F1}s";
       // UIManager.Instance.ShowStudentDashboard();
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