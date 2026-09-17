using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminLogin.uxml. Attach to the same GameObject as UIManager
    /// (it uses RequireComponent(UIDocument) like the other screen controllers,
    /// and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the Secure Login button, password visibility toggle and the
    ///    "Back to Student Login" link
    ///  - Basic client-side validation with inline error labels (same rules as
    ///    StudentLoginController, reused here for the admin email/password)
    ///  - Applies the purple->pink gradient textures at runtime (USS can't do
    ///    linear-gradient, so we generate small Texture2Ds and assign them as
    ///    backgroundImage on the shield logo circle, the Secure Login button and
    ///    the full-screen background, to match the dark admin theme in the mock)
    ///  - A simple "compact" breakpoint toggle for very small phone screens
    ///
    /// Hook up your real admin auth call inside OnSecureLoginClicked() - e.g.
    /// call into your existing PlayerSessionManager / AdminAuthService here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminLoginController : MonoBehaviour
    {
        [Header("Gradient colors (matches the mock: purple -> pink)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("Screen background colors (dark purple -> near-black)")]
        [SerializeField] private Color backgroundTop = new Color(0.145f, 0.055f, 0.235f);
        [SerializeField] private Color backgroundBottom = new Color(0.055f, 0.020f, 0.098f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private static readonly Regex EmailRegex =
            new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _backgroundGradientTexture;
        private Texture2D _logoGradientTexture;
        private Texture2D _buttonGradientTexture;

        private VisualElement _logoCircle;
        private VisualElement _passwordEyeIcon;

        private TextField _emailField;
        private Label _emailError;

        private TextField _passwordField;
        private Label _passwordError;
        private Button _togglePasswordButton;

        private Button _forgotPasswordButton;
        private Button _googleSigninButton;
        private Button _createAccountButton;

        private Button _secureLoginButton;
        private Button _backToStudentLoginButton;
        private Label _statusLabel;

        private bool _passwordVisible;

        private LoadingOverlay _loadingOverlay;

        private void OnEnable()
        {
            Debug.Log("[AdminLoginController] OnEnable called");

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
                Debug.LogError("[AdminLoginController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            ClearError(_emailError);
            ClearError(_passwordError);
            SetStatus(string.Empty);

            _loadingOverlay = new LoadingOverlay(_root);
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

            _loadingOverlay?.Dispose();
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _secureLoginButton?.UnregisterCallback<ClickEvent>(OnSecureLoginClicked);
            _togglePasswordButton?.UnregisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _backToStudentLoginButton?.UnregisterCallback<ClickEvent>(OnBackToStudentLoginClicked);
            _forgotPasswordButton?.UnregisterCallback<ClickEvent>(OnForgotPasswordClicked);
            _googleSigninButton?.UnregisterCallback<ClickEvent>(OnGoogleSigninClicked);
            _createAccountButton?.UnregisterCallback<ClickEvent>(OnCreateAccountClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminLoginController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _logoCircle = _screenRoot.Q<VisualElement>("logo-circle");

            _emailField = _screenRoot.Q<TextField>("admin-email-field");
            _emailError = _screenRoot.Q<Label>("email-error");

            _passwordField = _screenRoot.Q<TextField>("password-field");
            _passwordError = _screenRoot.Q<Label>("password-error");
            _togglePasswordButton = _screenRoot.Q<Button>("toggle-password-button");
            _passwordEyeIcon = _togglePasswordButton?.Q<VisualElement>(className: "icon-eye");

            _forgotPasswordButton = _screenRoot.Q<Button>("forgot-password-button");
            _googleSigninButton = _screenRoot.Q<Button>("google-signin-button");
            _createAccountButton = _screenRoot.Q<Button>("navigate-to-create-account-button");

            _secureLoginButton = _screenRoot.Q<Button>("secure-login-button");
            _backToStudentLoginButton = _screenRoot.Q<Button>("back-to-student-login-button");
            _statusLabel = _screenRoot.Q<Label>("status-label");

            Debug.Log($"[AdminLoginController] Found secure login button: {_secureLoginButton != null}, back link: {_backToStudentLoginButton != null}");
        }

        private void WireCallbacks()
        {
            _secureLoginButton?.RegisterCallback<ClickEvent>(OnSecureLoginClicked);
            _togglePasswordButton?.RegisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _backToStudentLoginButton?.RegisterCallback<ClickEvent>(OnBackToStudentLoginClicked);
            _forgotPasswordButton?.RegisterCallback<ClickEvent>(OnForgotPasswordClicked);
            _googleSigninButton?.RegisterCallback<ClickEvent>(OnGoogleSigninClicked);
            _createAccountButton?.RegisterCallback<ClickEvent>(OnCreateAccountClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Button handlers ----------------

        private void OnTogglePasswordClicked(ClickEvent evt)
        {
            _passwordVisible = !_passwordVisible;
            _passwordField.isPasswordField = !_passwordVisible;

            if (_passwordEyeIcon != null)
            {
                _passwordEyeIcon.EnableInClassList("icon-eye-off", _passwordVisible);
            }
        }

        private void OnBackToStudentLoginClicked(ClickEvent evt)
        {
            Debug.Log("[AdminLoginController] Navigating back to student login");
            UIManager.Instance.ShowStudentLogin();
        }

        private void OnForgotPasswordClicked(ClickEvent evt)
        {
            Debug.Log("[AdminLoginController] Forgot Password tapped.");
            UIManager.Instance.ShowAdminForgotPassword();
        }

        private void OnGoogleSigninClicked(ClickEvent evt)
        {
            Debug.Log("[AdminLoginController] Google sign-in tapped.");
            SetStatus("Signing in with Google...");
            _googleSigninButton.SetEnabled(false);

            AdminAuthService.Instance.LoginWithGoogle((success, errorMessage) =>
            {
                _googleSigninButton.SetEnabled(true);

                if (success)
                {
                    Debug.Log("[AdminLoginController] Google admin login successful.");
                    SetStatus(string.Empty);
                    UIManager.Instance.ShowAdminDashboard();
                }
                else
                {
                    Debug.LogWarning($"[AdminLoginController] Google admin login failed: {errorMessage}");
                    SetStatus(errorMessage);
                }
            });
        }

        private void OnCreateAccountClicked(ClickEvent evt)
        {
            Debug.Log("[AdminLoginController] Create Account tapped.");
            UIManager.Instance.ShowAdminCreateAccount();
        }

        private void OnSecureLoginClicked(ClickEvent evt)
        {
            bool valid = true;

            string email = _emailField.value?.Trim();
            if (string.IsNullOrEmpty(email) || !EmailRegex.IsMatch(email))
            {
                SetError(_emailError, "Please enter a valid admin email");
                valid = false;
            }
            else
            {
                ClearError(_emailError);
            }

            if (string.IsNullOrEmpty(_passwordField.value))
            {
                SetError(_passwordError, "Password is required");
                valid = false;
            }
            else
            {
                ClearError(_passwordError);
            }

            if (!valid)
            {
                SetStatus("Please fix the highlighted fields.");
                return;
            }

            SetStatus("Signing in...");
            _secureLoginButton.SetEnabled(false);
            _loadingOverlay.Show("Signing in...");
            // TODO: replace with your real admin auth call, e.g.:
            // AdminAuthService.Instance.LoginAdmin(email, _passwordField.value, OnLoginResult);

            AdminAuthService.Instance.LoginAdmin(email, _passwordField.value, (success, errorMessage) =>
            {
                if (success)
                {
                    Debug.Log("[AdminLoginController] Admin login successful.");
                    _loadingOverlay.Hide();
                    UIManager.Instance.ShowAdminDashboard();
                }
                else
                {
                    Debug.LogWarning($"[AdminLoginController] Admin login failed: {errorMessage}");
                    SetStatus("Login failed: " + errorMessage);
                    _secureLoginButton.SetEnabled(true);
                }
            });
        
        }

     

        // ---------------- Helpers ----------------

        private void SetError(Label label, string message)
        {
            if (label == null) return;
            label.text = message;
            label.RemoveFromClassList("hidden");
        }

        private void ClearError(Label label)
        {
            if (label == null) return;
            label.text = string.Empty;
            label.AddToClassList("hidden");
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

            if (_secureLoginButton != null)
            {
                if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
                _buttonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd, true);
                _secureLoginButton.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end, bool horizontal)
        {
            const int size = 64;
            var tex = new Texture2D(horizontal ? size : 1, horizontal ? 1 : size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminLoginGradientTexture"
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