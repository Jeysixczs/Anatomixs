using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminCreateAccount.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// This is the dark-themed, admin-specific counterpart to
    /// CreateAccountController - same field validation and fake-async flow,
    /// but its links return to Admin Login instead of Student Login, and the
    /// gradient/background match AdminLoginController's dark purple theme.
    ///
    /// SECURITY NOTE: this screen lets anyone create an admin account with no
    /// approval step, which is unusual for production apps. If you want
    /// gatekeeping (e.g. requiring an invite/access code, or routing new
    /// admins into a "pending approval" state) add that check inside
    /// OnCreateAccountClicked() before the fake network call below - e.g. call
    /// into your existing AdminAuthService here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminCreateAccountController : MonoBehaviour
    {
        [Header("Gradient colors (matches AdminLogin: purple -> pink)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f);
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);

        [Header("Screen background colors (dark purple -> near-black)")]
        [SerializeField] private Color backgroundTop = new Color(0.145f, 0.055f, 0.235f);
        [SerializeField] private Color backgroundBottom = new Color(0.055f, 0.020f, 0.098f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        [Header("Password Requirements")]
        [SerializeField] private int minimumPasswordLength = 8;

        private static readonly Regex EmailRegex =
            new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        private static readonly Regex PasswordRegex =
            new Regex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).+$", RegexOptions.Compiled);

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _backgroundGradientTexture;
        private Texture2D _logoGradientTexture;
        private Texture2D _buttonGradientTexture;

        private VisualElement _logoCircle;

        private Button _createAccountButton;
        private Button _backToAdminLoginButton;
        private Button _googleSignupButton;

        private TextField _firstNameField;
        private Label _firstNameError;

        private TextField _lastNameField;
        private Label _lastNameError;

        private TextField _emailField;
        private Label _emailError;

        private TextField _passwordField;
        private Label _passwordError;
        private Button _togglePasswordButton;
        private VisualElement _passwordEyeIcon;

        private TextField _confirmPasswordField;
        private Label _confirmPasswordError;
        private Button _toggleConfirmPasswordButton;
        private VisualElement _confirmPasswordEyeIcon;

        private Button _termsCheckbox;
        private bool _termsAccepted;

        private Label _statusLabel;

        private bool _passwordVisible;
        private bool _confirmPasswordVisible;

        private void OnEnable()
        {
            Debug.Log("[AdminCreateAccountController] OnEnable called");

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
                Debug.LogError("[AdminCreateAccountController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            if (_passwordField != null) _passwordField.isPasswordField = true;
            if (_confirmPasswordField != null) _confirmPasswordField.isPasswordField = true;
            _passwordVisible = false;
            _confirmPasswordVisible = false;

            ClearAllErrors();
            SetStatus(string.Empty);

            _termsAccepted = false;
            _termsCheckbox?.RemoveFromClassList("checked");
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

            _createAccountButton?.UnregisterCallback<ClickEvent>(OnCreateAccountClicked);
            _backToAdminLoginButton?.UnregisterCallback<ClickEvent>(OnBackToAdminLoginClicked);
            _googleSignupButton?.UnregisterCallback<ClickEvent>(OnGoogleSignupClicked);
            _togglePasswordButton?.UnregisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _toggleConfirmPasswordButton?.UnregisterCallback<ClickEvent>(OnToggleConfirmPasswordClicked);
            _termsCheckbox?.UnregisterCallback<ClickEvent>(OnTermsClicked);

            _firstNameField?.UnregisterCallback<ChangeEvent<string>>(OnFirstNameChanged);
            _lastNameField?.UnregisterCallback<ChangeEvent<string>>(OnLastNameChanged);
            _emailField?.UnregisterCallback<ChangeEvent<string>>(OnEmailChanged);
            _passwordField?.UnregisterCallback<ChangeEvent<string>>(OnPasswordChanged);
            _confirmPasswordField?.UnregisterCallback<ChangeEvent<string>>(OnConfirmPasswordChanged);

            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminCreateAccountController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _logoCircle = _screenRoot.Q<VisualElement>("logo-circle");

            _createAccountButton = _screenRoot.Q<Button>("create-account-button");
            _backToAdminLoginButton = _screenRoot.Q<Button>("back-to-admin-login-button");
            _googleSignupButton = _screenRoot.Q<Button>("google-signup-button");

            _firstNameField = _screenRoot.Q<TextField>("firstname-field");
            _firstNameError = _screenRoot.Q<Label>("firstname-error");

            _lastNameField = _screenRoot.Q<TextField>("lastname-field");
            _lastNameError = _screenRoot.Q<Label>("lastname-error");

            _emailField = _screenRoot.Q<TextField>("email-field");
            _emailError = _screenRoot.Q<Label>("email-error");

            _passwordField = _screenRoot.Q<TextField>("password-field");
            _passwordError = _screenRoot.Q<Label>("password-error");
            _togglePasswordButton = _screenRoot.Q<Button>("toggle-password-button");
            _passwordEyeIcon = _togglePasswordButton?.Q<VisualElement>(className: "icon-eye");

            _confirmPasswordField = _screenRoot.Q<TextField>("confirm-password-field");
            _confirmPasswordError = _screenRoot.Q<Label>("confirm-password-error");
            _toggleConfirmPasswordButton = _screenRoot.Q<Button>("toggle-confirm-password-button");
            _confirmPasswordEyeIcon = _toggleConfirmPasswordButton?.Q<VisualElement>(className: "icon-eye");

            _termsCheckbox = _screenRoot.Q<Button>("terms-checkbox");
            _statusLabel = _screenRoot.Q<Label>("status-label");

            Debug.Log($"[AdminCreateAccountController] Found create button: {_createAccountButton != null}, back link: {_backToAdminLoginButton != null}");
        }

        private void WireCallbacks()
        {
            _createAccountButton?.RegisterCallback<ClickEvent>(OnCreateAccountClicked);
            _backToAdminLoginButton?.RegisterCallback<ClickEvent>(OnBackToAdminLoginClicked);
            _googleSignupButton?.RegisterCallback<ClickEvent>(OnGoogleSignupClicked);
            _togglePasswordButton?.RegisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _toggleConfirmPasswordButton?.RegisterCallback<ClickEvent>(OnToggleConfirmPasswordClicked);
            _termsCheckbox?.RegisterCallback<ClickEvent>(OnTermsClicked);

            _firstNameField?.RegisterCallback<ChangeEvent<string>>(OnFirstNameChanged);
            _lastNameField?.RegisterCallback<ChangeEvent<string>>(OnLastNameChanged);
            _emailField?.RegisterCallback<ChangeEvent<string>>(OnEmailChanged);
            _passwordField?.RegisterCallback<ChangeEvent<string>>(OnPasswordChanged);
            _confirmPasswordField?.RegisterCallback<ChangeEvent<string>>(OnConfirmPasswordChanged);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Validation ----------------

        private void OnFirstNameChanged(ChangeEvent<string> evt) => ValidateFirstName(evt.newValue);
        private void OnLastNameChanged(ChangeEvent<string> evt) => ValidateLastName(evt.newValue);
        private void OnEmailChanged(ChangeEvent<string> evt) => ValidateEmail(evt.newValue);

        private void OnPasswordChanged(ChangeEvent<string> evt)
        {
            ValidatePassword(evt.newValue);
            if (!string.IsNullOrEmpty(_confirmPasswordField?.value))
            {
                ValidateConfirmPassword(_confirmPasswordField.value);
            }
        }

        private void OnConfirmPasswordChanged(ChangeEvent<string> evt) => ValidateConfirmPassword(evt.newValue);

        private bool ValidateFirstName(string value)
        {
            string trimmed = value?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed))
            {
                SetError(_firstNameError, "First name is required");
                return false;
            }
            if (trimmed.Length < 2)
            {
                SetError(_firstNameError, "First name must be at least 2 characters");
                return false;
            }
            ClearError(_firstNameError);
            return true;
        }

        private bool ValidateLastName(string value)
        {
            string trimmed = value?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed))
            {
                SetError(_lastNameError, "Last name is required");
                return false;
            }
            if (trimmed.Length < 2)
            {
                SetError(_lastNameError, "Last name must be at least 2 characters");
                return false;
            }
            ClearError(_lastNameError);
            return true;
        }

        private bool ValidateEmail(string value)
        {
            string trimmed = value?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed))
            {
                SetError(_emailError, "Admin email is required");
                return false;
            }
            if (!EmailRegex.IsMatch(trimmed))
            {
                SetError(_emailError, "Please enter a valid email address");
                return false;
            }
            ClearError(_emailError);
            return true;
        }

        private bool ValidatePassword(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                SetError(_passwordError, "Password is required");
                return false;
            }
            if (value.Length < minimumPasswordLength)
            {
                SetError(_passwordError, $"Password must be at least {minimumPasswordLength} characters");
                return false;
            }
            if (!PasswordRegex.IsMatch(value))
            {
                SetError(_passwordError, "Password must contain at least one uppercase, one lowercase, and one number");
                return false;
            }
            ClearError(_passwordError);
            return true;
        }

        private bool ValidateConfirmPassword(string value)
        {
            string password = _passwordField?.value;
            if (string.IsNullOrEmpty(value))
            {
                SetError(_confirmPasswordError, "Please confirm your password");
                return false;
            }
            if (value != password)
            {
                SetError(_confirmPasswordError, "Passwords do not match");
                return false;
            }
            ClearError(_confirmPasswordError);
            return true;
        }

        private bool ValidateAllFields()
        {
            bool isValid = true;

            if (!ValidateFirstName(_firstNameField?.value)) isValid = false;
            if (!ValidateLastName(_lastNameField?.value)) isValid = false;
            if (!ValidateEmail(_emailField?.value)) isValid = false;
            if (!ValidatePassword(_passwordField?.value)) isValid = false;
            if (!ValidateConfirmPassword(_confirmPasswordField?.value)) isValid = false;

            return isValid;
        }

        private void ClearAllErrors()
        {
            ClearError(_firstNameError);
            ClearError(_lastNameError);
            ClearError(_emailError);
            ClearError(_passwordError);
            ClearError(_confirmPasswordError);
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

        private void OnToggleConfirmPasswordClicked(ClickEvent evt)
        {
            _confirmPasswordVisible = !_confirmPasswordVisible;
            _confirmPasswordField.isPasswordField = !_confirmPasswordVisible;

            if (_confirmPasswordEyeIcon != null)
            {
                _confirmPasswordEyeIcon.EnableInClassList("icon-eye-off", _confirmPasswordVisible);
            }
        }

        private void OnTermsClicked(ClickEvent evt)
        {
            _termsAccepted = !_termsAccepted;
            _termsCheckbox.EnableInClassList("checked", _termsAccepted);
        }

        private void OnBackToAdminLoginClicked(ClickEvent evt)
        {
            Debug.Log("[AdminCreateAccountController] Navigating back to admin login");
            UIManager.Instance.ShowAdminLogin();
        }

        private void OnGoogleSignupClicked(ClickEvent evt)
        {
            SetStatus("Connecting to Google...");
            Debug.Log("[AdminCreateAccountController] Google signup tapped.");
            _googleSignupButton.SetEnabled(false);

            AdminAuthService.Instance?.LoginWithGoogle((success, errorMessage) =>
            {
                _googleSignupButton.SetEnabled(true);

                if (success)
                {
                    Debug.Log("[AdminCreateAccountController] Google account signed up/in successfully");
                    SetStatus("Signed in with Google! Redirecting...");
                    Invoke(nameof(RedirectAfterGoogle), 1f);
                }
                else
                {
                    Debug.LogError($"[AdminCreateAccountController] Google signup failed: {errorMessage}");
                    SetStatus(errorMessage ?? "Google sign-in failed. Please try again.");
                }
            });
        }

        private void RedirectAfterGoogle()
        {
            // LoginWithGoogle already signs the admin in, so go straight to
            // the dashboard rather than back through the login screen.
            UIManager.Instance?.ShowAdminDashboard();
        }

        private void OnCreateAccountClicked(ClickEvent evt)
        {
            if (!ValidateAllFields())
            {
                SetStatus("Please fix the highlighted fields.");
                return;
            }

            if (!_termsAccepted)
            {
                SetStatus("Please accept the Terms of Service");
                return;
            }

            SetStatus("Creating admin account...");
            _createAccountButton.SetEnabled(false);

            // TODO: replace with your real admin account creation call, e.g.:
            // AdminAuthService.Instance.CreateAdminAccount(firstName, lastName, email, password, OnAccountCreated);
            AdminAuthService.Instance?.CreateAdminAccount(
                fullName: $"{_firstNameField.value.Trim()} {_lastNameField.value.Trim()}",
                email: _emailField.value.Trim(),
                password: _passwordField.value,
                onComplete: (success, errorMessage) =>
                {
                    if (success)
                    {
                        Debug.Log("[CreateAccountController] Account created successfully");
                        SetStatus("Account created successfully! Redirecting...");
                        Invoke(nameof(RedirectToAdminLogin), 1.5f);
                    }
                    else
                    {
                        Debug.LogError($"[CreateAccountController] Account creation failed: {errorMessage}");
                        SetStatus($"Account creation failed: {errorMessage}");
                        _createAccountButton.SetEnabled(true);
                    }
                }
            );
        }

        private void RedirectToAdminLogin()
        {
            UIManager.Instance?.ShowAdminLogin();
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

            if (_createAccountButton != null)
            {
                if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
                _buttonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd, true);
                _createAccountButton.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end, bool horizontal)
        {
            const int size = 64;
            var tex = new Texture2D(horizontal ? size : 1, horizontal ? 1 : size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminCreateAccountGradientTexture"
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