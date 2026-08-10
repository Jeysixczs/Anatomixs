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

        private Label _verifyEmailStatusBadge;
        private Button _verifyEmailButton;
        private Label _verifyEmailStatusLabel;
        private Label _cardSubtitle;
        private VisualElement _verifyEmailWarning;
        private Label _emailPendingHint;

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
        private string _loadedEmail;

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

            RefreshVerificationBadge();
            UpdatePendingEmailHint();

            if (AdminAuthService.Instance != null)
            {
                AdminAuthService.Instance.OnEmailChangeConfirmed -= OnEmailChangeConfirmed;
                AdminAuthService.Instance.OnEmailChangeConfirmed += OnEmailChangeConfirmed;
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (AdminAuthService.Instance != null)
            {
                AdminAuthService.Instance.OnEmailChangeConfirmed -= OnEmailChangeConfirmed;
            }

            if (_headerGradientTexture != null) { Destroy(_headerGradientTexture); _headerGradientTexture = null; }
            if (_buttonGradientTexture != null) { Destroy(_buttonGradientTexture); _buttonGradientTexture = null; }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _togglePasswordVisibilityButton?.UnregisterCallback<ClickEvent>(OnTogglePasswordVisibilityClicked);
            _verifyEmailButton?.UnregisterCallback<ClickEvent>(OnVerifyEmailClicked);
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

            _verifyEmailStatusBadge = _screenRoot.Q<Label>("verify-email-status-badge");
            _verifyEmailButton = _screenRoot.Q<Button>("verify-email-button");
            _verifyEmailStatusLabel = _screenRoot.Q<Label>("verify-email-status-label");
            _verifyEmailWarning = _screenRoot.Q<VisualElement>("verify-email-warning");
            _cardSubtitle = _screenRoot.Q<Label>("card-subtitle");
            _emailPendingHint = _screenRoot.Q<Label>("email-pending-hint");

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
            _verifyEmailButton?.RegisterCallback<ClickEvent>(OnVerifyEmailClicked);
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
            _loadedEmail = email;
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

        private void OnVerifyEmailClicked(ClickEvent evt)
        {
            if (AdminAuthService.Instance.IsEmailVerified)
            {
                SetVerifyEmailStatus("Your email is already verified.");
                RefreshVerificationBadge();
                return;
            }

            _verifyEmailButton.SetEnabled(false);
            SetVerifyEmailStatus("Sending verification email...");

            AdminAuthService.Instance.SendEmailVerification((success, error) =>
            {
                _verifyEmailButton.SetEnabled(true);

                if (!success)
                {
                    Debug.LogError($"[AdminEditProfileController] Send verification email failed: {error}");
                    SetVerifyEmailStatus(error ?? "Could not send verification email. Please try again.");
                    return;
                }

                SetVerifyEmailStatus($"Verification email sent to {_loadedEmail}. Check your inbox and click the link, then reopen this screen.");
            });
        }

        /// <summary>Reloads the current user from Firebase so the verified/not
        /// verified badge reflects a link the admin may have just clicked, then
        /// updates the badge text/style.</summary>
        private void RefreshVerificationBadge()
        {
            UpdateVerifyBadge(AdminAuthService.Instance.IsEmailVerified);

            AdminAuthService.Instance.RefreshEmailVerificationStatus(isVerified =>
            {
                UpdateVerifyBadge(isVerified);
            });
        }

        private void UpdateVerifyBadge(bool isVerified)
        {
            if (_verifyEmailStatusBadge != null)
            {
                _verifyEmailStatusBadge.text = isVerified ? "Verified" : "Not verified";
                _verifyEmailStatusBadge.EnableInClassList("verify-status-verified", isVerified);
                _verifyEmailStatusBadge.EnableInClassList("verify-status-unverified", !isVerified);
            }

            if (_verifyEmailButton != null)
            {
                _verifyEmailButton.text = isVerified ? "Resend Verification Email" : "Send Verification Email";
            }

            // The "you won't be able to recover this account" warning only
            // matters while the email is actually unverified - hide it once
            // verified instead of leaving it up permanently.
            _verifyEmailWarning?.EnableInClassList("hidden", isVerified);
            _verifyEmailButton?.EnableInClassList("hidden", isVerified);

            // chnage the label of the card subtitle mkae the you are verified

            if (_cardSubtitle != null)
            {
                _cardSubtitle.text = isVerified ? "Your email is verified." : "We'll send a verification link to your current email address.";
            }


        }

        private void SetVerifyEmailStatus(string message)
        {
            if (_verifyEmailStatusLabel == null) return;
            _verifyEmailStatusLabel.text = message;
            if (string.IsNullOrEmpty(message))
                _verifyEmailStatusLabel.AddToClassList("hidden");
            else
                _verifyEmailStatusLabel.RemoveFromClassList("hidden");
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

            // Changing the email is a sensitive Auth operation and always needs
            // the current password, whether or not they're also setting a new
            // password below.
            bool emailChanged = !string.IsNullOrEmpty(email) &&
                !string.Equals(email, _loadedEmail, System.StringComparison.OrdinalIgnoreCase);

            string currentPassword = _currentPasswordField.value;
            string newPassword = _newPasswordField.value;
            string confirmPassword = _confirmPasswordField.value;
            bool changingPassword = !string.IsNullOrEmpty(newPassword) || !string.IsNullOrEmpty(confirmPassword);
            bool needsCurrentPassword = changingPassword || emailChanged;

            if (needsCurrentPassword && string.IsNullOrEmpty(currentPassword))
            {
                SetError(_currentPasswordError, emailChanged && !changingPassword
                    ? "Enter your current password to change your email"
                    : "Enter your current password");
                valid = false;
            }

            if (changingPassword)
            {
                if (newPassword.Length < minPasswordLength)
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

            AdminAuthService.Instance.UpdateProfile(fullName, email, currentPassword, (success, error) =>
            {
                if (!success)
                {
                    Debug.LogError($"[AdminEditProfileController] Profile update failed: {error}");
                    SetStatus(error ?? "Failed to save changes. Please try again.");
                    _saveChangesButton.SetEnabled(true);
                    return;
                }

                if (changingPassword)
                {
                    AdminAuthService.Instance.ChangePassword(currentPassword, newPassword, (pwSuccess, pwError) =>
                    {
                        if (pwSuccess)
                        {
                            Debug.Log("[AdminEditProfileController] Profile and password updated successfully.");
                            OnSaveComplete(emailChanged, email);
                        }
                        else
                        {
                            Debug.LogError($"[AdminEditProfileController] Password change failed: {pwError}");
                            SetStatus(pwError ?? "Failed to change password. Please try again.");
                            _saveChangesButton.SetEnabled(true);
                        }
                    });
                }
                else
                {
                    Debug.Log("[AdminEditProfileController] Profile updated successfully.");
                    OnSaveComplete(emailChanged, email);
                }
            });
        }

        private void OnSaveComplete(bool emailChanged, string newEmail)
        {
            _saveChangesButton.SetEnabled(true);

            // Clear password fields either way; they're never re-displayed.
            _currentPasswordField.value = string.Empty;
            _newPasswordField.value = string.Empty;
            _confirmPasswordField.value = string.Empty;

            if (emailChanged)
            {
                // Auth.CurrentUser.Email (and therefore this screen's display)
                // won't actually become newEmail until the admin clicks the
                // verification link in their inbox. Stay signed in on the OLD
                // email in the meantime - don't log out here or on a fixed
                // timer. AdminAuthService.UpdateProfile already started
                // background polling (on itself, not this screen) the moment
                // it sent the link, so the admin gets logged out automatically
                // the instant they confirm it - even if they've since
                // navigated away from this screen.
                SetStatus($"Saved. A verification link was sent to {newEmail}. Check your inbox and click it - you'll be logged out automatically once it's confirmed.");
                UpdatePendingEmailHint();
                _saveChangesButton.SetEnabled(false);
                return;
            }

            SetStatus(string.Empty);

            // Push the updated name/email back into AdminProfileController so it
            // doesn't keep showing stale values, then navigate back.
            UIManager.Instance.ShowAdminProfile();
        }

        /// <summary>Fired by AdminAuthService the instant it detects the admin
        /// has confirmed a pending email change (from its own background
        /// polling, which keeps running no matter which screen is visible).
        /// By the time this fires AdminAuthService has already logged the
        /// admin out and navigated to Login, so if this screen still happens
        /// to be enabled in that instant there's nothing left to do here
        /// except make sure it isn't left showing stale state.</summary>
        private void OnEmailChangeConfirmed()
        {
            SetStatus(string.Empty);
            UpdatePendingEmailHint();
        }

        /// <summary>Shows a small hint under the verify-email section while a
        /// change is pending, so the admin isn't confused about why the
        /// email field still shows the old address after saving.</summary>
        private void UpdatePendingEmailHint()
        {
            if (_emailPendingHint == null) return;

            string pending = AdminAuthService.Instance?.PendingEmail;
            if (string.IsNullOrEmpty(pending))
            {
                _emailPendingHint.text = string.Empty;
                _emailPendingHint.AddToClassList("hidden");
                return;
            }

            _emailPendingHint.text = $"Pending change to {pending} - check your inbox to confirm.";
            _emailPendingHint.RemoveFromClassList("hidden");
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