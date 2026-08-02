using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminEditProfile.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button, the "Show/Hide" password-visibility
    ///    toggle, and Save Changes
    ///  - Validates name/email, and - only if the person is actually trying
    ///    to change it - the current/new/confirm password fields
    ///  - Applies the green->blue gradient (matches AdminProfile) to the
    ///    header and submit button at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes LoadProfileData() so AdminProfileController can prefill
    ///    the name/email fields right after this screen is shown (see
    ///    UIManager.ShowAdminEditProfile()).
    ///
    /// Hook up your real "update profile" / "change password" calls inside
    /// OnSaveChangesClicked() - e.g. call into your existing
    /// PlayerSessionManager here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminEditProfileController : MonoBehaviour
    {
        [Header("Gradient colors (matches AdminProfile: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f); // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        [Header("Password rules")]
        [SerializeField] private int minPasswordLength = 6;

        private static readonly Regex EmailRegex =
            new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;
        private Texture2D _buttonGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        private TextField _fullNameField;
        private Label _fullNameError;
        private TextField _emailField;
        private Label _emailError;

        private Button _togglePasswordVisibilityButton;
        private TextField _currentPasswordField;
        private Label _currentPasswordError;
        private TextField _newPasswordField;
        private Label _newPasswordError;
        private TextField _confirmPasswordField;
        private Label _confirmPasswordError;

        private Label _statusLabel;
        private Button _saveChangesButton;

        private bool _passwordsVisible;

        private void OnEnable()
        {
            Debug.Log("[AdminEditProfileController] OnEnable called");

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
                Debug.LogError("[AdminEditProfileController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            _passwordsVisible = false;
            UpdatePasswordVisibility();
            ClearAllErrors();
            SetStatus(string.Empty);
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_headerGradientTexture != null) { Destroy(_headerGradientTexture); _headerGradientTexture = null; }
            if (_buttonGradientTexture != null) { Destroy(_buttonGradientTexture); _buttonGradientTexture = null; }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _togglePasswordVisibilityButton?.UnregisterCallback<ClickEvent>(OnTogglePasswordVisibilityClicked);
            _saveChangesButton?.UnregisterCallback<ClickEvent>(OnSaveChangesClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminEditProfileController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _fullNameField = _screenRoot.Q<TextField>("full-name-field");
            _fullNameError = _screenRoot.Q<Label>("full-name-error");
            _emailField = _screenRoot.Q<TextField>("email-field");
            _emailError = _screenRoot.Q<Label>("email-error");

            _togglePasswordVisibilityButton = _screenRoot.Q<Button>("toggle-password-visibility-button");
            _currentPasswordField = _screenRoot.Q<TextField>("current-password-field");
            _currentPasswordError = _screenRoot.Q<Label>("current-password-error");
            _newPasswordField = _screenRoot.Q<TextField>("new-password-field");
            _newPasswordError = _screenRoot.Q<Label>("new-password-error");
            _confirmPasswordField = _screenRoot.Q<TextField>("confirm-password-field");
            _confirmPasswordError = _screenRoot.Q<Label>("confirm-password-error");

            _statusLabel = _screenRoot.Q<Label>("status-label");
            _saveChangesButton = _screenRoot.Q<Button>("save-changes-button");

            Debug.Log($"[AdminEditProfileController] Found name field: {_fullNameField != null}, save button: {_saveChangesButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _togglePasswordVisibilityButton?.RegisterCallback<ClickEvent>(OnTogglePasswordVisibilityClicked);
            _saveChangesButton?.RegisterCallback<ClickEvent>(OnSaveChangesClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Prefill the Full Name / Email fields with the person's current profile data.</summary>
        public void LoadProfileData(string fullName, string email)
        {
            if (_fullNameField != null) _fullNameField.SetValueWithoutNotify(fullName);
            if (_emailField != null) _emailField.SetValueWithoutNotify(email);
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[AdminEditProfileController] Navigating back to profile");
            UIManager.Instance.ShowAdminProfile();
        }

        private void OnTogglePasswordVisibilityClicked(ClickEvent evt)
        {
            _passwordsVisible = !_passwordsVisible;
            UpdatePasswordVisibility();
        }

        private void UpdatePasswordVisibility()
        {
            if (_currentPasswordField != null) _currentPasswordField.isPasswordField = !_passwordsVisible;
            if (_newPasswordField != null) _newPasswordField.isPasswordField = !_passwordsVisible;
            if (_confirmPasswordField != null) _confirmPasswordField.isPasswordField = !_passwordsVisible;
            if (_togglePasswordVisibilityButton != null) _togglePasswordVisibilityButton.text = _passwordsVisible ? "Hide" : "Show";
        }

        private void OnSaveChangesClicked(ClickEvent evt)
        {
            ClearAllErrors();
            bool valid = true;

            string fullName = _fullNameField.value?.Trim();
            if (string.IsNullOrEmpty(fullName))
            {
                SetError(_fullNameError, "Please enter your name");
                valid = false;
            }

            string email = _emailField.value?.Trim();
            if (string.IsNullOrEmpty(email) || !EmailRegex.IsMatch(email))
            {
                SetError(_emailError, "Please enter a valid email address");
                valid = false;
            }

            string currentPassword = _currentPasswordField.value;
            string newPassword = _newPasswordField.value;
            string confirmPassword = _confirmPasswordField.value;
            bool changingPassword = !string.IsNullOrEmpty(currentPassword) || !string.IsNullOrEmpty(newPassword) || !string.IsNullOrEmpty(confirmPassword);

            if (changingPassword)
            {
                if (string.IsNullOrEmpty(currentPassword))
                {
                    SetError(_currentPasswordError, "Enter your current password");
                    valid = false;
                }

                if (string.IsNullOrEmpty(newPassword) || newPassword.Length < minPasswordLength)
                {
                    SetError(_newPasswordError, $"New password must be at least {minPasswordLength} characters");
                    valid = false;
                }
                else if (newPassword != confirmPassword)
                {
                    SetError(_confirmPasswordError, "Passwords do not match");
                    valid = false;
                }
            }

            if (!valid)
            {
                SetStatus("Please fix the highlighted fields.");
                return;
            }

            SetStatus("Saving changes...");
            _saveChangesButton.SetEnabled(false);

            // TODO: replace with your real "update profile" / "change password" calls, e.g.:
            // AdminAccountService.Instance.UpdateProfile(fullName, email, OnProfileSaved);
            // if (changingPassword) AdminAccountService.Instance.ChangePassword(currentPassword, newPassword, OnPasswordChanged);

            AdminAuthService.Instance.UpdateProfile(fullName, email, (success, error) =>
            {
                if (success)
                {
                    if (changingPassword)
                    {
                        AdminAuthService.Instance.ChangePassword(currentPassword, newPassword, (pwSuccess, pwError) =>
                        {
                            if (pwSuccess)
                            {
                                Debug.Log("[AdminEditProfileController] Profile and password updated successfully.");
                                FakeSaveComplete();
                            }
                            else
                            {
                                Debug.LogError($"[AdminEditProfileController] Password change failed: {pwError}");
                                SetStatus("Failed to change password. Please try again.");
                                _saveChangesButton.SetEnabled(true);
                            }
                        });
                    }
                    else
                    {
                        Debug.Log("[AdminEditProfileController] Profile updated successfully.");
                        FakeSaveComplete();
                    }
                }
                else
                {
                    Debug.LogError($"[AdminEditProfileController] Profile update failed: {error}");
                    SetStatus("Failed to save changes. Please try again.");
                    _saveChangesButton.SetEnabled(true);
                }
            });

            Invoke(nameof(FakeSaveComplete), 0.4f);
        }

        private void FakeSaveComplete()
        {
            _saveChangesButton.SetEnabled(true);
            SetStatus(string.Empty);

            Debug.Log("[AdminEditProfileController] Save stub complete - hook up AdminAccountService here.");

            // Clear password fields either way; they're never re-displayed.
            _currentPasswordField.value = string.Empty;
            _newPasswordField.value = string.Empty;
            _confirmPasswordField.value = string.Empty;

            // TODO: only navigate once your real save call reports success, and
            // ideally push the updated name/email back into AdminProfileController
            // (e.g. via UIManager.Instance.ShowAdminProfile() + SetProfileData()).
            UIManager.Instance.ShowAdminProfile();
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

        private void ClearAllErrors()
        {
            ClearError(_fullNameError);
            ClearError(_emailError);
            ClearError(_currentPasswordError);
            ClearError(_newPasswordError);
            ClearError(_confirmPasswordError);
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

            if (_saveChangesButton != null)
            {
                if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
                _buttonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _saveChangesButton.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
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
