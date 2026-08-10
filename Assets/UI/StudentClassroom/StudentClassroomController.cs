using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentClassroom.uxml (the "Join Classroom" screen). Attach to
    /// the same GameObject as UIManager (it uses RequireComponent(UIDocument) like
    /// the other screen controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the Join Classroom button
    ///  - Validates the classroom code (6-8 alphanumeric characters)
    ///  - Applies the green->blue gradient to the header and submit button at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///
    /// Hook up your real "join classroom" call inside OnJoinClassroomSubmitClicked()
    /// - e.g. call into your existing ClassroomService/PlayerSessionManager here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentClassroomController : MonoBehaviour
    {
        [Header("Gradient colors (matches the mock: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        [Header("Validation")]
        [SerializeField] private int minCodeLength = 6;
        [SerializeField] private int maxCodeLength = 8;

        private static readonly Regex CodeRegex = new Regex(@"^[A-Za-z0-9]+$", RegexOptions.Compiled);

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;
        private Texture2D _submitGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        private TextField _classroomCodeField;
        private Label _codeError;
        private Label _statusLabel;
        private Button _joinClassroomSubmitButton;

        private void OnEnable()
        {
            Debug.Log("[StudentClassroomController] OnEnable called");

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
                Debug.LogError("[StudentClassroomController] Root is null!");
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

            if (_submitGradientTexture != null)
            {
                Destroy(_submitGradientTexture);
                _submitGradientTexture = null;
            }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _joinClassroomSubmitButton?.UnregisterCallback<ClickEvent>(OnJoinClassroomSubmitClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentClassroomController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _classroomCodeField = _screenRoot.Q<TextField>("classroom-code-field");
            _codeError = _screenRoot.Q<Label>("code-error");
            _statusLabel = _screenRoot.Q<Label>("status-label");
            _joinClassroomSubmitButton = _screenRoot.Q<Button>("join-classroom-submit-button");

            Debug.Log($"[StudentClassroomController] Found submit button: {_joinClassroomSubmitButton != null}, header: {_header != null}");
        }

        private void WireCallbacks()
        {
            if (_backButton != null)
            {
                _backButton.RegisterCallback<ClickEvent>(OnBackClicked);
            }

            if (_joinClassroomSubmitButton != null)
            {
                _joinClassroomSubmitButton.RegisterCallback<ClickEvent>(OnJoinClassroomSubmitClicked);
            }

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentClassroomController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnJoinClassroomSubmitClicked(ClickEvent evt)
        {
            string code = _classroomCodeField?.value?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(code) ||
                code.Length < minCodeLength ||
                code.Length > maxCodeLength ||
                !CodeRegex.IsMatch(code))
            {
                SetError($"Please enter a valid {minCodeLength}-{maxCodeLength} character classroom code");
                SetStatus(string.Empty);
                return;
            }

            ClearError();
            SetStatus("Joining classroom...");
            _joinClassroomSubmitButton.SetEnabled(false);

            // TODO: replace with your real "join classroom" call, e.g.:
            // ClassroomService.Instance.JoinClassroom(code, OnJoinResult);
            ClassroomService.Instance.JoinClassroom(code, OnJoinResult);
          
        }

        private void OnJoinResult(bool success, string message)
        {
            if (success)
            {
                Debug.Log("[StudentClassroomController] Join classroom succeeded");
                SetStatus("Successfully joined classroom!");

                // Hub's "My Classrooms" list only fetches once per screen instance
                // (see StudentClassroomHubController.OnEnable) - without this, a
                // newly-joined classroom wouldn't show up there until something
                // else forces a reload.
                UIManager.Instance.InvalidateStudentClassroomHub();

                // Same idea for the dashboard's Recent Activity feed - a join isn't
                // covered by PlayerSessionManager.OnStudentProfileChanged (points/level/
                // badges don't change here), so it needs its own explicit invalidation
                // or the "Joined 'X'" row wouldn't show up until some other event
                // happened to trigger a refetch.
                UIManager.Instance.GetComponent<StudentDashboardController>()?.MarkActivityDirty();

                // Navigate to the dashboard or classroom view
                UIManager.Instance.ShowStudentDashboard();
            }
            else
            {
                Debug.LogWarning($"[StudentClassroomController] Join classroom failed: {message}");
                SetError($"Failed to join classroom: {message}");
                SetStatus(string.Empty);
                _joinClassroomSubmitButton.SetEnabled(true);
            }
        }
       

        // ---------------- Helpers ----------------

        private void SetError(string message)
        {
            if (_codeError == null) return;
            _codeError.text = message;
            _codeError.RemoveFromClassList("hidden");
        }

        private void ClearError()
        {
            if (_codeError == null) return;
            _codeError.text = string.Empty;
            _codeError.AddToClassList("hidden");
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

        // ---------------- Gradient (USS has no linear-gradient) ----------------

        private void ApplyGradients()
        {
            if (_header != null)
            {
                if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
                _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_joinClassroomSubmitButton != null)
            {
                if (_submitGradientTexture != null) Destroy(_submitGradientTexture);
                _submitGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _joinClassroomSubmitButton.style.backgroundImage = new StyleBackground(_submitGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "ClassroomGradientTexture"
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
