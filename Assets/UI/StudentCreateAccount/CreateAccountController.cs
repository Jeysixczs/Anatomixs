using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class CreateAccountController : MonoBehaviour
    {
        [Header("Gradient colors (matches the mock)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f);
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);

        [Header("Compact breakpoint (px, logical screen width)")]
        [SerializeField] private int compactWidthThreshold = 900;

        [Header("Password Requirements")]
        [SerializeField] private int minimumPasswordLength = 8;

        private static readonly Regex EmailRegex =
            new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        private static readonly Regex PasswordRegex =
            new Regex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).+$", RegexOptions.Compiled);

        private UIDocument _document;
        private VisualElement _root;

        // UI Elements
        private Button _createAccountButton;
        private Button _backToLoginButton;
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

        private TextField _confirmPasswordField;
        private Label _confirmPasswordError;
        private Button _toggleConfirmPasswordButton;

        // Change to Button for click support
        private Button _termsCheckbox;
        private bool _termsAccepted = false;

        private Label _statusLabel;

        private bool _passwordVisible;
        private bool _confirmPasswordVisible;

        private VisualElement _passwordEyeIcon;

        private void OnEnable()
        {
            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
                _root = _document?.rootVisualElement;
            }

            if (_root == null) return;

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            _passwordField.isPasswordField = true;
            _confirmPasswordField.isPasswordField = true;
        }

        private void OnDisable()
        {
            if (_root == null) return;

            _createAccountButton?.UnregisterCallback<ClickEvent>(OnCreateAccountClicked);
            _backToLoginButton?.UnregisterCallback<ClickEvent>(OnBackToLoginClicked);
            _googleSignupButton?.UnregisterCallback<ClickEvent>(OnGoogleSignupClicked);
            _togglePasswordButton?.UnregisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _toggleConfirmPasswordButton?.UnregisterCallback<ClickEvent>(OnToggleConfirmPasswordClicked);
            _termsCheckbox?.UnregisterCallback<ClickEvent>(OnTermsClicked);

            _firstNameField?.UnregisterCallback<ChangeEvent<string>>(OnFirstNameChanged);
            _lastNameField?.UnregisterCallback<ChangeEvent<string>>(OnLastNameChanged);
            _emailField?.UnregisterCallback<ChangeEvent<string>>(OnEmailChanged);
            _passwordField?.UnregisterCallback<ChangeEvent<string>>(OnPasswordChanged);
            _confirmPasswordField?.UnregisterCallback<ChangeEvent<string>>(OnConfirmPasswordChanged);

            _root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _createAccountButton = _root.Q<Button>("create-account-button");
            _backToLoginButton = _root.Q<Button>("back-to-login-button");
            _googleSignupButton = _root.Q<Button>("google-signup-button");

            _firstNameField = _root.Q<TextField>("firstname-field");
            _firstNameError = _root.Q<Label>("firstname-error");

            _lastNameField = _root.Q<TextField>("lastname-field");
            _lastNameError = _root.Q<Label>("lastname-error");

            _emailField = _root.Q<TextField>("email-field");
            _emailError = _root.Q<Label>("email-error");

            _passwordField = _root.Q<TextField>("password-field");
            _passwordError = _root.Q<Label>("password-error");
            _togglePasswordButton = _root.Q<Button>("toggle-password-button");

            _confirmPasswordField = _root.Q<TextField>("confirm-password-field");
            _confirmPasswordError = _root.Q<Label>("confirm-password-error");
            _toggleConfirmPasswordButton = _root.Q<Button>("toggle-confirm-password-button");

            _passwordEyeIcon = _togglePasswordButton?.Q<VisualElement>(className: "icon-eye");
            // Changed to Button for ClickEvent support
            _termsCheckbox = _root.Q<Button>("terms-checkbox");

            _statusLabel = _root.Q<Label>("status-label");
        }

        private void WireCallbacks()
        {
            _createAccountButton?.RegisterCallback<ClickEvent>(OnCreateAccountClicked);
            _backToLoginButton?.RegisterCallback<ClickEvent>(OnBackToLoginClicked);
            _googleSignupButton?.RegisterCallback<ClickEvent>(OnGoogleSignupClicked);
            _togglePasswordButton?.RegisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _toggleConfirmPasswordButton?.RegisterCallback<ClickEvent>(OnToggleConfirmPasswordClicked);
            _termsCheckbox?.RegisterCallback<ClickEvent>(OnTermsClicked);

            _firstNameField?.RegisterCallback<ChangeEvent<string>>(OnFirstNameChanged);
            _lastNameField?.RegisterCallback<ChangeEvent<string>>(OnLastNameChanged);
            _emailField?.RegisterCallback<ChangeEvent<string>>(OnEmailChanged);
            _passwordField?.RegisterCallback<ChangeEvent<string>>(OnPasswordChanged);
            _confirmPasswordField?.RegisterCallback<ChangeEvent<string>>(OnConfirmPasswordChanged);

            // Register geometry changed for responsive layout
            _root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        // ---------------- Validation Methods ----------------

        private void OnFirstNameChanged(ChangeEvent<string> evt)
        {
            ValidateFirstName(evt.newValue);
        }

        private void OnLastNameChanged(ChangeEvent<string> evt)
        {
            ValidateLastName(evt.newValue);
        }

        private void OnEmailChanged(ChangeEvent<string> evt)
        {
            ValidateEmail(evt.newValue);
        }

        private void OnPasswordChanged(ChangeEvent<string> evt)
        {
            ValidatePassword(evt.newValue);
            if (!string.IsNullOrEmpty(_confirmPasswordField.value))
            {
                ValidateConfirmPassword(_confirmPasswordField.value);
            }
        }

        private void OnConfirmPasswordChanged(ChangeEvent<string> evt)
        {
            ValidateConfirmPassword(evt.newValue);
        }

        private bool ValidateFirstName(string value)
        {
            string trimmed = value?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed))
            {
                SetError(_firstNameError, "First name is required");
                return false;
            }
            else if (trimmed.Length < 2)
            {
                SetError(_firstNameError, "First name must be at least 2 characters");
                return false;
            }
            else
            {
                ClearError(_firstNameError);
                return true;
            }
        }

        private bool ValidateLastName(string value)
        {
            string trimmed = value?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed))
            {
                SetError(_lastNameError, "Last name is required");
                return false;
            }
            else if (trimmed.Length < 2)
            {
                SetError(_lastNameError, "Last name must be at least 2 characters");
                return false;
            }
            else
            {
                ClearError(_lastNameError);
                return true;
            }
        }

        private bool ValidateEmail(string value)
        {
            string trimmed = value?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed))
            {
                SetError(_emailError, "Email is required");
                return false;
            }
            else if (!EmailRegex.IsMatch(trimmed))
            {
                SetError(_emailError, "Please enter a valid email address");
                return false;
            }
            else
            {
                ClearError(_emailError);
                return true;
            }
        }

        private bool ValidatePassword(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                SetError(_passwordError, "Password is required");
                return false;
            }
            else if (value.Length < minimumPasswordLength)
            {
                SetError(_passwordError, $"Password must be at least {minimumPasswordLength} characters");
                return false;
            }
            else if (!PasswordRegex.IsMatch(value))
            {
                SetError(_passwordError, "Password must contain at least one uppercase, one lowercase, and one number");
                return false;
            }
            else
            {
                ClearError(_passwordError);
                return true;
            }
        }

        private bool ValidateConfirmPassword(string value)
        {
            string password = _passwordField.value;
            if (string.IsNullOrEmpty(value))
            {
                SetError(_confirmPasswordError, "Please confirm your password");
                return false;
            }
            else if (value != password)
            {
                SetError(_confirmPasswordError, "Passwords do not match");
                return false;
            }
            else
            {
                ClearError(_confirmPasswordError);
                return true;
            }
        }

        private bool ValidateAllFields()
        {
            bool isValid = true;

            if (!ValidateFirstName(_firstNameField.value)) isValid = false;
            if (!ValidateLastName(_lastNameField.value)) isValid = false;
            if (!ValidateEmail(_emailField.value)) isValid = false;
            if (!ValidatePassword(_passwordField.value)) isValid = false;
            if (!ValidateConfirmPassword(_confirmPasswordField.value)) isValid = false;

            return isValid;
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
        }

        private void OnTermsClicked(ClickEvent evt)
        {
            _termsAccepted = !_termsAccepted;
            if (_termsAccepted)
            {
                _termsCheckbox.AddToClassList("checked");
            }
            else
            {
                _termsCheckbox.RemoveFromClassList("checked");
            }
        }

        private void OnBackToLoginClicked(ClickEvent evt)
        {
            UIManager.Instance?.ShowStudentLogin();
            Debug.Log("[CreateAccountController] Navigating back to login");
        }

        private void OnGoogleSignupClicked(ClickEvent evt)
        {
            SetStatus("Connecting to Google...");
            Debug.Log("[CreateAccountController] Google signup clicked");
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

            SetStatus("Creating account...");
            _createAccountButton.SetEnabled(false);

            PlayerSessionManager.Instance?.CreateStudentAccount(
                fullName: $"{_firstNameField.value.Trim()} {_lastNameField.value.Trim()}",
                email: _emailField.value.Trim(),
                password: _passwordField.value,
                onComplete: (success, errorMessage) =>
                {
                    if (success)
                    {
                        Debug.Log("[CreateAccountController] Account created successfully");
                        SetStatus("Account created successfully! Redirecting...");
                        Invoke(nameof(RedirectToLogin), 1.5f);
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

        private void RedirectToLogin()
        {
            UIManager.Instance?.ShowStudentLogin();
        }

        // ---------------- Helpers ----------------

        private void SetError(Label label, string message)
        {
            if (label != null)
            {
                label.text = message;
                label.RemoveFromClassList("hidden");
            }
        }

        private void ClearError(Label label)
        {
            if (label != null)
            {
                label.text = string.Empty;
                label.AddToClassList("hidden");
            }
        }

        private void SetStatus(string message)
        {
            if (_statusLabel != null)
            {
                _statusLabel.text = message;
                if (string.IsNullOrEmpty(message))
                {
                    _statusLabel.AddToClassList("hidden");
                }
                else
                {
                    _statusLabel.RemoveFromClassList("hidden");
                }
            }
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            bool compact = _root.resolvedStyle.width > 0 && _root.resolvedStyle.width < compactWidthThreshold;
            _root.EnableInClassList("compact", compact);
        }

        // ---------------- Gradients ----------------

        private void ApplyGradients()
        {
            var horizontal = BuildGradientTexture(gradientStart, gradientEnd, true);

            if (_createAccountButton != null)
            {
                _createAccountButton.style.backgroundImage = new StyleBackground(horizontal);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end, bool horizontal)
        {
            const int size = 64;
            var tex = new Texture2D(horizontal ? size : 1, horizontal ? 1 : size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
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