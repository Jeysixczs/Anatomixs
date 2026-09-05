using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AboutAnatomia.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button
    ///  - Applies the purple->pink gradient to the header at runtime
    ///    (matches StudentProfile)
    ///  - Builds the "Developers" list at runtime from a list of names
    ///    (defaults to the thesis group below)
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetAppInfo() / SetDescription() / SetDevelopers() so this
    ///    can be swapped for real data instead of the placeholder text
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AboutAnatomiaAdminController : MonoBehaviour
    {
        [Header("Gradient colors (matches StudentProfile: purple -> pink)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("App info (fallback if SetAppInfo() is never called)")]
        [SerializeField] private string appName = "ANATOMIA";
        [SerializeField] private string appFullTitle = "A Gamified Mobile Application for Interactive Human Anatomy Learning";
        [SerializeField] private string appVersion = "1.0.0";

        [TextArea(3, 6)]
        [SerializeField]
        private string appDescription =
            "ANATOMIA is a gamified mobile application that makes learning human anatomy interactive and engaging. " +
            "Through 3D models, quizzes, points, and levels, it transforms traditional anatomy lessons into a fun and " +
            "immersive learning experience for students, with tools that let teachers create classrooms and track progress.";

        [Header("Developers (fallback if SetDevelopers() is never called)")]
        [SerializeField]
        private List<string> developerNames = new List<string>
        {
            "Abuyan, Christian D.",
            "Acopio, Jorge Matthew M.",
            "Alvaro, John Carl F.",
            "Aquino, John Carlo Ds.",
        };

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        private Label _appNameLabel;
        private Label _appFullTitleLabel;
        private Label _descriptionTextLabel;
        private Label _versionLabel;

        private VisualElement _developersList;

        private void OnEnable()
        {
            Debug.Log("[AboutAnatomiaController] OnEnable called");

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
                Debug.LogError("[AboutAnatomiaController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshAppInfoUI();
            RefreshDevelopersUI();
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
                Debug.LogWarning("[AboutAnatomiaController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _appNameLabel = _screenRoot.Q<Label>("app-name-label");
            _appFullTitleLabel = _screenRoot.Q<Label>("app-full-title-label");
            _descriptionTextLabel = _screenRoot.Q<Label>("description-text-label");
            _versionLabel = _screenRoot.Q<Label>("version-label");

            _developersList = _screenRoot.Q<VisualElement>("developers-list");

            Debug.Log($"[AboutAnatomiaController] Found back button: {_backButton != null}, developers list: {_developersList != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push the app logo title, full descriptive title and version instead of the placeholder mock data.</summary>
        public void SetAppInfo(string name, string fullTitle, string version)
        {
            appName = name;
            appFullTitle = fullTitle;
            appVersion = version;
            RefreshAppInfoUI();
        }

        /// <summary>Push the paragraph shown under "Description".</summary>
        public void SetDescription(string description)
        {
            appDescription = description;
            if (_descriptionTextLabel != null) _descriptionTextLabel.text = appDescription;
        }

        /// <summary>Push the list of names shown under "Developers" (e.g. "Last, First M.").</summary>
        public void SetDevelopers(List<string> names)
        {
            developerNames = names ?? new List<string>();
            RefreshDevelopersUI();
        }

        private void RefreshAppInfoUI()
        {
            if (_appNameLabel != null) _appNameLabel.text = appName;
            if (_appFullTitleLabel != null) _appFullTitleLabel.text = appFullTitle;
            if (_descriptionTextLabel != null) _descriptionTextLabel.text = appDescription;
            if (_versionLabel != null) _versionLabel.text = $"{appName}  v{appVersion}";
        }

        private void RefreshDevelopersUI()
        {
            if (_developersList == null) return;

            _developersList.Clear();

            for (int i = 0; i < developerNames.Count; i++)
            {
                var row = new VisualElement();
                row.AddToClassList("developer-row");
                if (i == developerNames.Count - 1) row.AddToClassList("developer-row-last");

                var avatar = new VisualElement();
                avatar.AddToClassList("developer-avatar");
                var initialsLabel = new Label(GetInitials(developerNames[i]));
                initialsLabel.AddToClassList("developer-avatar-initials");
                avatar.Add(initialsLabel);
                row.Add(avatar);

                var nameLabel = new Label(developerNames[i]);
                nameLabel.AddToClassList("developer-name-label");
                row.Add(nameLabel);

                _developersList.Add(row);

                if (i < developerNames.Count - 1)
                {
                    var divider = new VisualElement();
                    divider.AddToClassList("menu-divider");
                    _developersList.Add(divider);
                }
            }
        }

        private string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "?";

            // Names are formatted as "Last, First M." - grab the first letter of
            // the surname and the first letter of the given name.
            var mainParts = fullName.Split(',');
            string surname = mainParts[0].Trim();
            string given = mainParts.Length > 1 ? mainParts[1].Trim() : string.Empty;

            char first = surname.Length > 0 ? surname[0] : '?';
            char second = given.Length > 0 ? given[0] : first;

            return $"{first}{second}".ToUpper();
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            UIManager.Instance?.ShowAdminProfile();
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
                name = "AboutAnatomiaHeaderGradientTexture"
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
