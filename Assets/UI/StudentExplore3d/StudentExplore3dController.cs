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

    // ===== Play Mode entry point =====
    private Button _playModeEntryButton;
    private VisualElement _playModePickerBanner;
    private Button _playModePickerCancelButton;
    private VisualElement _cardList;

    // True from the moment play-mode-entry-button is tapped until either a
    // card is chosen or Cancel is pressed. While true, the NEXT card tap
    // launches that system's Anatomy Screen already in Play Mode instead
    // of the normal Explore Mode. There is no separate picker UI - the
    // existing card list doubles as the picker (see WireAnatomySystemCards).
    private bool _playModePickingArmed;


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
        if (_playModeEntryButton != null) _playModeEntryButton.clicked -= OnPlayModeEntryButtonClicked;
        if (_playModePickerCancelButton != null) _playModePickerCancelButton.clicked -= OnPlayModePickerCancelClicked;

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

        _playModeEntryButton = _screenRoot.Q<Button>("play-mode-entry-button");
        _playModePickerBanner = _screenRoot.Q<VisualElement>("play-mode-picker-banner");
        _playModePickerCancelButton = _screenRoot.Q<Button>("play-mode-picker-cancel");
        _cardList = _screenRoot.Q<VisualElement>("card-list");

        Debug.Log($"[StudentExplore3dController] Found back button: {_backButton != null}, header: {_header != null}, play mode entry button: {_playModeEntryButton != null}");
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
        WirePlayModeEntry();
    }

    // Play Mode's entry point lives here (Student Explore 3D), not on the
    // Anatomy Screen toolbar - there's no loaded 3D model/selection system
    // to play against until a system has been chosen. Tapping this button
    // just arms "picking" mode: the banner appears and the next card tap
    // (handled in WireAnatomySystemCards) launches
    // UIManager.ShowStudentAnatomyScreen(system, startInPlayMode: true)
    // instead of the normal Explore Mode call. There's no separate picker
    // popup - the card list already lists the three systems, so it doubles
    // as the picker rather than duplicating it.
    private void WirePlayModeEntry()
    {
        if (_playModeEntryButton != null)
        {
            _playModeEntryButton.clicked -= OnPlayModeEntryButtonClicked;
            _playModeEntryButton.clicked += OnPlayModeEntryButtonClicked;
        }

        if (_playModePickerCancelButton != null)
        {
            _playModePickerCancelButton.clicked -= OnPlayModePickerCancelClicked;
            _playModePickerCancelButton.clicked += OnPlayModePickerCancelClicked;
        }

        // Always start disarmed - ShowScreen clones a fresh UI tree each
        // time this screen opens, so nothing carries over from a previous
        // visit, but make sure the visuals match that (banner hidden,
        // entry button not in its "active" state).
        SetPlayModePickingArmed(false);
    }

    private void OnPlayModeEntryButtonClicked()
    {
        // Tapping the button again while armed cancels, same as Cancel.
        SetPlayModePickingArmed(!_playModePickingArmed);
    }

    private void OnPlayModePickerCancelClicked()
    {
        SetPlayModePickingArmed(false);
    }

    private void SetPlayModePickingArmed(bool armed)
    {
        _playModePickingArmed = armed;

        if (_playModePickerBanner != null)
        {
            if (armed) _playModePickerBanner.RemoveFromClassList("hidden");
            else _playModePickerBanner.AddToClassList("hidden");
        }

        if (_playModeEntryButton != null)
        {
            if (armed)
            {
                _playModeEntryButton.AddToClassList("play-mode-entry-button-active");
                _playModeEntryButton.text = "✕ Cancel";
            }
            else
            {
                _playModeEntryButton.RemoveFromClassList("play-mode-entry-button-active");
                _playModeEntryButton.text = "▶ Play";
            }
        }

        // Ring every card so it's obvious tapping one right now starts a
        // game instead of opening Explore Mode.
        if (_cardList != null)
        {
            var cards = _cardList.Query<Button>(className: "card").ToList();
            foreach (var card in cards)
            {
                if (armed) card.AddToClassList("card-play-armed");
                else card.RemoveFromClassList("card-play-armed");
            }
        }
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
    //
    // Each card also doubles as the Play Mode picker: if
    // _playModePickingArmed is true when a card is tapped, that tap opens
    // the system in Play Mode instead of Explore Mode, and disarms picking
    // mode again either way.
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
                Debug.LogWarning($"[AnatomyNav] Card '{card.name}' matched .card but has no known icon class - skipping.");
                continue; // not one of the three anatomy-system cards (e.g. none - skip)
            }
         
            card.clicked += () =>
            {
                bool playMode = _playModePickingArmed;
                SetPlayModePickingArmed(false);

                try
                {
                    UIManager.Instance.ShowStudentAnatomyScreen(system, startInPlayMode: playMode);
                    Debug.Log($"[AnatomyNav] ShowStudentAnatomyScreen({system}, startInPlayMode: {playMode}) called successfully.");
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
