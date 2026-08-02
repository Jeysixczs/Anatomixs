using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminCreateClassroom.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the Create Classroom submit button
    ///  - Basic client-side validation (classroom name is required) with an
    ///    inline error label
    ///  - Applies the green->blue gradient (matches AdminDashboard) to the
    ///    header and submit button at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///
    /// Hook up your real classroom-creation call inside
    /// OnCreateClassroomClicked() - e.g. call into your existing
    /// AdminClassroomService / backend here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminCreateClassroomController : MonoBehaviour
    {
        [Header("Gradient colors (matches AdminDashboard: green -> blue)")]
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

        private TextField _nameField;
        private Label _nameError;
        private TextField _descriptionField;
        private Label _statusLabel;
        private Button _createButton;

        private void OnEnable()
        {
            Debug.Log("[AdminCreateClassroomController] OnEnable called");

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
                Debug.LogError("[AdminCreateClassroomController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            ClearError();
            SetStatus(string.Empty);
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
            _createButton?.UnregisterCallback<ClickEvent>(OnCreateClassroomClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminCreateClassroomController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _nameField = _screenRoot.Q<TextField>("classroom-name-field");
            _nameError = _screenRoot.Q<Label>("classroom-name-error");
            _descriptionField = _screenRoot.Q<TextField>("classroom-description-field");
            _statusLabel = _screenRoot.Q<Label>("status-label");
            _createButton = _screenRoot.Q<Button>("create-classroom-button");

            Debug.Log($"[AdminCreateClassroomController] Found name field: {_nameField != null}, create button: {_createButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _createButton?.RegisterCallback<ClickEvent>(OnCreateClassroomClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[AdminCreateClassroomController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        private void OnCreateClassroomClicked(ClickEvent evt)
        {
            string name = _nameField.value?.Trim();
            string description = _descriptionField.value?.Trim();


            if (string.IsNullOrEmpty(name))
            {
                SetError("Please enter a classroom name");
                SetStatus(string.Empty);
                return;
            }

            ClearError();
            SetStatus("Creating classroom...");
            _createButton.SetEnabled(false);

            // TODO: replace with your real classroom-creation call, e.g.:
            // AdminClassroomService.Instance.CreateClassroom(name, description, OnCreateResult);
            AdminClassroomService.Instance.CreateClassroom(name, description, (success, errorMessage, record) =>
            {
                OnCreateResult(success, record, errorMessage);
            });

        }

        private void OnCreateResult(bool success, AdminClassroomService.ClassroomRecord record, string errorMessage)
        {
            _createButton.SetEnabled(true);
            if (success)
            {
                SetStatus(string.Empty);
                Debug.Log($"[AdminCreateClassroomController] Classroom created successfully with code: {record.Code}");
                string createdName = string.IsNullOrEmpty(_nameField.value) ? "Classroom" : _nameField.value;
                // Clear the form for the next classroom.
                _nameField.value = string.Empty;
                _descriptionField.value = string.Empty;
                // NOTE: ShowAdminClassroomCreated now needs a leading classroomId
                // parameter (record.ClassroomId) - update its signature in UIManager.cs
                // to match, so AdminClassroomCreatedController can forward it on to
                // ShowAdminClassroomDetail().
                UIManager.Instance.ShowAdminClassroomCreated(record.ClassroomId, record.Code, createdName, 0);
            }
            else
            {
                SetStatus(string.Empty);
                SetError($"Failed to create classroom: {errorMessage}");
                Debug.LogError($"[AdminCreateClassroomController] Failed to create classroom: {errorMessage}");
            }
        }



        // ---------------- Helpers ----------------

        private void SetError(string message)
        {
            if (_nameError == null) return;
            _nameError.text = message;
            _nameError.RemoveFromClassList("hidden");
        }

        private void ClearError()
        {
            if (_nameError == null) return;
            _nameError.text = string.Empty;
            _nameError.AddToClassList("hidden");
        }

        private void SetStatus(string message)
        {
            if (_statusLabel == null) return;
            _statusLabel.text = message;
            if (string.IsNullOrEmpty(message))
                _statusLabel.AddToClassList("hidden");
            else
                _statusLabel.RemoveFromClassList("hidden");
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

            if (_createButton != null)
            {
                if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
                _buttonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _createButton.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminCreateClassroomGradientTexture"
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