using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminForgotPassword.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// This is the dark-themed, admin-specific counterpart to
    /// ForgotPasswordController - same validation/fake-async flow, but its
    /// "Back" button returns to Admin Login instead of Student Login, and the
    /// gradient/background match AdminLoginController's dark purple theme.
    ///
    /// Hook up your real password reset call inside OnSendResetLinkClicked()
    /// - e.g. call into your existing AdminAuthService here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminForgotPasswordController : MonoBehaviour
    {
        [Header("Gradient colors (matches AdminLogin: purple -> pink)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("Screen background colors (dark purple -> near-black)")]
        [SerializeField] private Color backgroundTop = new Color(0.145f, 0.055f, 0.235f);
        [SerializeField] private Color backgroundBottom = new Color(0.055f, 0.020f, 0.098f);

        [Header("Compact breakpoints (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;
        // Also go compact when the panel is short - e.g. a phone rotated to
        // landscape has plenty of width but very little height.
        [SerializeField] private int compactHeightThreshold = 700;
        // Below this height, tighten vertical spacing further on top of
        // "compact" (logo, paddings, margins) so more fits without scrolling.
        [SerializeField] private int landscapeHeightThreshold = 550;
        // Below this width, tighten horizontal spacing further on top of
        // "compact" - small phones in portrait, split-screen / multi-window,
        // or a folded foldable where the usable width is quite narrow.
        [SerializeField] private int ultraCompactWidthThreshold = 620;

        private static readonly Regex EmailRegex =
            new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _backgroundGradientTexture;
        private Texture2D _logoGradientTexture;
        private Texture2D _buttonGradientTexture;

        private VisualElement _logoCircle;

        private Button _sendResetLinkButton;
        private Button _backToAdminLoginButton;
        private TextField _emailField;
        private Label _emailError;
        private Label _statusLabel;
        private Label _successMessageLabel;

        private void OnEnable()
        {
            Debug.Log("[AdminForgotPasswordController] OnEnable called");

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
                Debug.LogError("[AdminForgotPasswordController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            ClearError();
            SetStatus(string.Empty);
            _successMessageLabel?.AddToClassList("hidden");
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_backgroundGradientTexture != null)
            {
                Destroy(_backgroundGradientTexture);
                _backgroundGradientTexture = null;
            }

            if (_logoGradientTexture != null)
            {
                Destroy(_logoGradientTexture);
                _logoGradientTexture = null;
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

            _sendResetLinkButton?.UnregisterCallback<ClickEvent>(OnSendResetLinkClicked);
            _backToAdminLoginButton?.UnregisterCallback<ClickEvent>(OnBackToAdminLoginClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminForgotPasswordController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _logoCircle = _screenRoot.Q<VisualElement>("logo-circle");

            _sendResetLinkButton = _screenRoot.Q<Button>("send-reset-link-button");
            _backToAdminLoginButton = _screenRoot.Q<Button>("back-to-admin-login-button");

            _emailField = _screenRoot.Q<TextField>("email-field");
            _emailError = _screenRoot.Q<Label>("email-error");
            _statusLabel = _screenRoot.Q<Label>("status-label");
            _successMessageLabel = _screenRoot.Q<Label>("success-message-label");

            Debug.Log($"[AdminForgotPasswordController] Found send button: {_sendResetLinkButton != null}, back link: {_backToAdminLoginButton != null}");
        }

        private void WireCallbacks()
        {
            _sendResetLinkButton?.RegisterCallback<ClickEvent>(OnSendResetLinkClicked);
            _backToAdminLoginButton?.RegisterCallback<ClickEvent>(OnBackToAdminLoginClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Button handlers ----------------

        private void OnBackToAdminLoginClicked(ClickEvent evt)
        {
            Debug.Log("[AdminForgotPasswordController] Navigating back to admin login");
            UIManager.Instance.ShowAdminLogin();
        }

        private void OnSendResetLinkClicked(ClickEvent evt)
        {
            // Hide previous success message
            _successMessageLabel?.AddToClassList("hidden");

            string email = _emailField?.value?.Trim();
            if (string.IsNullOrEmpty(email) || !EmailRegex.IsMatch(email))
            {
                SetError("Please enter a valid admin email address");
                SetStatus("Please fix the highlighted field.");
                return;
            }

            ClearError();
            SetStatus("Sending reset link...");
            _sendResetLinkButton.SetEnabled(false);

            // TODO: replace with your real admin password reset call, e.g.:
            // AdminAuthService.Instance.SendPasswordResetEmail(email, OnResetLinkSent);
            AdminAuthService.Instance.SendPasswordResetEmail(email, (success, error) =>
            {
                if (success)
                {
                    _sendResetLinkButton.SetEnabled(true);
                    SetStatus(string.Empty);
                    _successMessageLabel?.RemoveFromClassList("hidden");
                    if (_emailField != null) _emailField.value = string.Empty;
                    ClearError();
                }
                else
                {
                    _sendResetLinkButton.SetEnabled(true);
                    SetStatus("Failed to send reset link.");
                    SetError(error ?? "An unknown error occurred.");
                }
            });
         //   Invoke(nameof(FakeResetLinkSent), 0.4f);
        }

        //private void FakeResetLinkSent()
        //{
        //    _sendResetLinkButton.SetEnabled(true);
        //    SetStatus(string.Empty);

        //    // Show success message
        //    _successMessageLabel?.RemoveFromClassList("hidden");

        //    // Clear the email field
        //    if (_emailField != null) _emailField.value = string.Empty;
        //    ClearError();

        //    Debug.Log("[AdminForgotPasswordController] Password reset email sent stub - hook up AdminAuthService here.");
        //}

        // ---------------- Helpers ----------------

        private void SetError(string message)
        {
            if (_emailError == null) return;
            _emailError.text = message;
            _emailError.RemoveFromClassList("hidden");
        }

        private void ClearError()
        {
            if (_emailError == null) return;
            _emailError.text = string.Empty;
            _emailError.AddToClassList("hidden");
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

            float width = _screenRoot.resolvedStyle.width;
            float height = _screenRoot.resolvedStyle.height;

            if (width <= 0 || height <= 0) return;

            // Narrow OR short both count as "compact" - a rotated phone is
            // wide but short, so width alone would miss it.
            bool compact = width < compactWidthThreshold || height < compactHeightThreshold;

            // Very short panels (typical phone landscape) need extra
            // vertical tightening beyond what "compact" already does.
            bool landscape = height < landscapeHeightThreshold;

            // Very narrow panels (small phones in portrait, split-screen,
            // folded foldables) need extra horizontal tightening beyond
            // what "compact" already does.
            bool ultraCompact = width < ultraCompactWidthThreshold;

            _screenRoot.EnableInClassList("compact", compact);
            _screenRoot.EnableInClassList("landscape", landscape);
            _screenRoot.EnableInClassList("ultra-compact", ultraCompact);
        }

        // ---------------- Gradients (USS has no linear-gradient) ----------------

        private void ApplyGradients()
        {
            if (_screenRoot != null)
            {
                if (_backgroundGradientTexture != null) Destroy(_backgroundGradientTexture);
                _backgroundGradientTexture = BuildGradientTexture(backgroundTop, backgroundBottom, false);
                _screenRoot.style.backgroundImage = new StyleBackground(_backgroundGradientTexture);
            }

            if (_logoCircle != null)
            {
                if (_logoGradientTexture != null) Destroy(_logoGradientTexture);
                _logoGradientTexture = BuildGradientTexture(gradientStart, gradientEnd, false);
                _logoCircle.style.backgroundImage = new StyleBackground(_logoGradientTexture);
            }

            if (_sendResetLinkButton != null)
            {
                if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
                _buttonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd, true);
                _sendResetLinkButton.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end, bool horizontal)
        {
            const int size = 64;
            var tex = new Texture2D(horizontal ? size : 1, horizontal ? 1 : size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminForgotPasswordGradientTexture"
            };

            for (int i = 0; i < size; i++)
            {
                float t = i / (float)(size - 1);
                Color c = Color.Lerp(start, end, t);
                if (horizontal) tex.SetPixel(i, 0, c);
                else tex.SetPixel(0, i, c);
            }

            tex.Apply();
            return tex;
        }
    }
}
