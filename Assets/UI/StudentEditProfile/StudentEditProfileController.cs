using System;
using System.Collections;
using System.Text.RegularExpressions;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentEditProfile.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button, the "Show/Hide" password-visibility
    ///    toggle, and Save Changes
    ///  - Validates name/email, and - only if the person is actually trying
    ///    to change it - the current/new/confirm password fields
    ///  - Applies the purple->pink gradient (matches StudentProfile) to the
    ///    header and submit button at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes LoadProfileData() so StudentProfileController can prefill
    ///    the name/email fields right after this screen is shown (see
    ///    UIManager.ShowStudentEditProfile()).
    ///
    /// Hook up your real "update profile" / "change password" calls inside
    /// OnSaveChangesClicked() - e.g. call into your existing
    /// PlayerSessionManager here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentEditProfileController : MonoBehaviour
    {
        [Header("Gradient colors (matches StudentProfile: purple -> pink)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f);
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        [Header("Password rules")]
        [SerializeField] private int minPasswordLength = 6;

        [Header("Email verification live-update")]
        [Tooltip("While this screen is open and the email is unverified, poll Firebase this often (seconds) so the badge updates the moment the student clicks the link in their inbox - no need to reopen the screen.")]
        [SerializeField] private float verificationPollIntervalSeconds = 4f;

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
        private Label _emailPendingHint;

        private VisualElement _avatarPreview;
        private Label _avatarPreviewInitialsLabel;
        private Button _changePhotoButton;
        private Label _avatarStatusLabel;

        // Local-only state for the photo the student just picked but hasn't
        // saved yet. The circle preview updates immediately (optimistic, same
        // spirit as everywhere else in this project); the actual Cloudinary
        // upload + Firestore write only happens once Save Changes is clicked
        // and the rest of validation passes (see FinalizeSave). Nulled out on
        // OnDisable so leaving this screen without saving discards the pick.
        private byte[] _pendingAvatarBytes;
        private Texture2D _avatarPreviewTexture;
        private string _loadedAvatarUrl;
        private Coroutine _avatarPreviewLoadRoutine;

        private Label _verifyEmailStatusBadge;
        private Button _verifyEmailButton;
        private Label _verifyEmailStatusLabel;
        private Label _cardSubtitle;
        private VisualElement _verifyEmailWarning;

        private Button _changePasswordButton;
        private VisualElement _passwordOverlay;
        private VisualElement _passwordOverlayBackdrop;
        private Button _togglePasswordVisibilityButton;
        private TextField _currentPasswordField;
        private Label _currentPasswordError;
        private TextField _newPasswordField;
        private Label _newPasswordError;
        private TextField _confirmPasswordField;
        private Label _confirmPasswordError;
        private Button _closePasswordCardButton;

        private Label _statusLabel;
        private Button _saveChangesButton;

        private bool _passwordsVisible;
        private string _loadedEmail;

        private void OnEnable()
        {
            Debug.Log("[StudentEditProfileController] OnEnable called");

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
                Debug.LogError("[StudentEditProfileController] Root is null!");
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
            ClosePasswordOverlay();

            _pendingAvatarBytes = null;
            RefreshAvatarPreview();

            RefreshVerificationBadge();
            UpdatePendingEmailHint();
            StartVerificationPolling();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnEmailChangeConfirmed -= OnEmailChangeConfirmed;
                PlayerSessionManager.Instance.OnEmailChangeConfirmed += OnEmailChangeConfirmed;
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            StopVerificationPolling();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnEmailChangeConfirmed -= OnEmailChangeConfirmed;
            }

            if (_headerGradientTexture != null) { Destroy(_headerGradientTexture); _headerGradientTexture = null; }
            if (_buttonGradientTexture != null) { Destroy(_buttonGradientTexture); _buttonGradientTexture = null; }

            if (_avatarPreviewLoadRoutine != null)
            {
                StopCoroutine(_avatarPreviewLoadRoutine);
                _avatarPreviewLoadRoutine = null;
            }
            if (_avatarPreviewTexture != null) { Destroy(_avatarPreviewTexture); _avatarPreviewTexture = null; }
            _pendingAvatarBytes = null;
            _loadedAvatarUrl = null;
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _changePhotoButton?.UnregisterCallback<ClickEvent>(OnChangePhotoClicked);
            _changePasswordButton?.UnregisterCallback<ClickEvent>(OnChangePasswordClicked);
            _passwordOverlayBackdrop?.UnregisterCallback<ClickEvent>(OnClosePasswordOverlayClicked);
            _closePasswordCardButton?.UnregisterCallback<ClickEvent>(OnClosePasswordOverlayClicked);
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
                Debug.LogWarning("[StudentEditProfileController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _fullNameField = _screenRoot.Q<TextField>("full-name-field");
            _fullNameError = _screenRoot.Q<Label>("full-name-error");
            _emailField = _screenRoot.Q<TextField>("email-field");
            _emailError = _screenRoot.Q<Label>("email-error");
            _emailPendingHint = _screenRoot.Q<Label>("email-pending-hint");

            _avatarPreview = _screenRoot.Q<VisualElement>("avatar-preview");
            _avatarPreviewInitialsLabel = _screenRoot.Q<Label>("avatar-preview-initials-label");
            _changePhotoButton = _screenRoot.Q<Button>("change-photo-button");
            _avatarStatusLabel = _screenRoot.Q<Label>("avatar-status-label");

            _verifyEmailStatusBadge = _screenRoot.Q<Label>("verify-email-status-badge");
            _verifyEmailButton = _screenRoot.Q<Button>("verify-email-button");
            _verifyEmailStatusLabel = _screenRoot.Q<Label>("verify-email-status-label");
            _cardSubtitle = _screenRoot.Q<Label>("card-subtitle");
            _verifyEmailWarning = _screenRoot.Q<VisualElement>("verify-email-warning");


            _changePasswordButton = _screenRoot.Q<Button>("change-password-button");
            _passwordOverlay = _screenRoot.Q<VisualElement>("password-overlay");
            _passwordOverlayBackdrop = _screenRoot.Q<VisualElement>("password-overlay-backdrop");

            _togglePasswordVisibilityButton = _screenRoot.Q<Button>("toggle-password-visibility-button");
            _currentPasswordField = _screenRoot.Q<TextField>("current-password-field");
            _currentPasswordError = _screenRoot.Q<Label>("current-password-error");
            _newPasswordField = _screenRoot.Q<TextField>("new-password-field");
            _newPasswordError = _screenRoot.Q<Label>("new-password-error");
            _confirmPasswordField = _screenRoot.Q<TextField>("confirm-password-field");
            _confirmPasswordError = _screenRoot.Q<Label>("confirm-password-error");
            _closePasswordCardButton = _screenRoot.Q<Button>("close-password-card-button");

            _statusLabel = _screenRoot.Q<Label>("status-label");
            _saveChangesButton = _screenRoot.Q<Button>("save-changes-button");

            Debug.Log($"[StudentEditProfileController] Found name field: {_fullNameField != null}, save button: {_saveChangesButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _changePhotoButton?.RegisterCallback<ClickEvent>(OnChangePhotoClicked);
            _changePasswordButton?.RegisterCallback<ClickEvent>(OnChangePasswordClicked);
            _passwordOverlayBackdrop?.RegisterCallback<ClickEvent>(OnClosePasswordOverlayClicked);
            _closePasswordCardButton?.RegisterCallback<ClickEvent>(OnClosePasswordOverlayClicked);
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
            Debug.Log("[StudentEditProfileController] Navigating back to profile");
            UIManager.Instance.ShowStudentProfile();
        }

        private void OnChangePasswordClicked(ClickEvent evt)
        {
            OpenPasswordOverlay();
        }

        // ---------------- Avatar photo ----------------
        //
        // Requires the free "Native Gallery for Android & iOS" asset
        // (github.com/yasirkula/UnityNativeGallery / Asset Store) for
        // NativeGallery.GetImageFromGallery - it handles the OS-level photo
        // picker and runtime permission prompt on both platforms. Swap this
        // one method for a different picker if the project ends up using
        // something else; nothing downstream (preview/upload/save) depends
        // on how the bytes were obtained.

        private void OnChangePhotoClicked(ClickEvent evt)
        {
            NativeGallery.GetImageFromGallery(path =>
            {
                if (string.IsNullOrEmpty(path)) return; // student cancelled the picker

                Texture2D picked = NativeGallery.LoadImageAtPath(path, maxSize: 1024, markTextureNonReadable: false);
                if (picked == null)
                {
                    Debug.LogWarning($"[StudentEditProfileController] Could not load image at '{path}'.");
                    SetAvatarStatus("Could not load that photo. Please try a different one.");
                    return;
                }

                _pendingAvatarBytes = picked.EncodeToJPG(85);
                ShowAvatarPreviewTexture(picked); // takes ownership of picked - no separate decode needed
                SetAvatarStatus("Photo selected - tap Save Changes to upload it.");
            }, "Select a profile photo", "image/*");
        }

        /// <summary>Loads whatever avatar the signed-in student currently has
        /// into the preview circle, or falls back to initials if there isn't
        /// one. Called fresh every time this screen opens - see OnEnable.
        ///
        /// Tries the local on-disk cache first (see
        /// CloudinaryAvatarUploadService.TryLoadLocalAvatar) so the photo
        /// shows up instantly and works offline, then still kicks off a
        /// network refresh from student.AvatarUrl in the background - that
        /// keeps this device in sync if the avatar was changed elsewhere,
        /// and self-heals the local cache if it's missing (e.g. first login
        /// on a new device).</summary>
        private void RefreshAvatarPreview()
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;

            if (_avatarPreviewInitialsLabel != null)
                _avatarPreviewInitialsLabel.text = GetInitials(student?.FullName);

            if (student == null || string.IsNullOrEmpty(student.AvatarUrl))
            {
                ShowAvatarInitials();
                return;
            }

            if (CloudinaryAvatarUploadService.Instance != null &&
                CloudinaryAvatarUploadService.Instance.TryLoadLocalAvatar(student.Uid, out byte[] cachedBytes))
            {
                var cachedTex = new Texture2D(2, 2);
                if (ImageConversion.LoadImage(cachedTex, cachedBytes))
                {
                    ShowAvatarPreviewTexture(cachedTex); // takes ownership, same as a freshly-picked photo
                }
                else
                {
                    Destroy(cachedTex);
                }
            }

            if (_avatarPreviewLoadRoutine != null) StopCoroutine(_avatarPreviewLoadRoutine);
            _avatarPreviewLoadRoutine = StartCoroutine(LoadAvatarPreviewFromUrl(student.AvatarUrl, student.Uid));
        }

        private IEnumerator LoadAvatarPreviewFromUrl(string avatarUrl, string uid)
        {
            using (var request = UnityWebRequestTexture.GetTexture(avatarUrl))
            {
                yield return request.SendWebRequest();
                _avatarPreviewLoadRoutine = null;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[StudentEditProfileController] Could not load current avatar '{avatarUrl}': {request.error}");
                    yield break; // leave whatever's already showing (local cache or initials fallback)
                }

                if (_avatarPreview == null) yield break; // screen closed while the request was in flight

                if (_avatarPreviewTexture != null) Destroy(_avatarPreviewTexture);
                _avatarPreviewTexture = DownloadHandlerTexture.GetContent(request);
                _loadedAvatarUrl = avatarUrl;

                _avatarPreview.style.backgroundImage = new StyleBackground(_avatarPreviewTexture);
                ApplyCoverBackground(_avatarPreview);
                if (_avatarPreviewInitialsLabel != null) _avatarPreviewInitialsLabel.style.display = DisplayStyle.None;

                // Keep the local cache in sync in case the avatar changed on
                // another device since we last cached it here.
                CloudinaryAvatarUploadService.Instance?.SaveAvatarLocally(request.downloadHandler.data, uid);
            }
        }

        /// <summary>Immediately previews a just-picked photo using the texture
        /// NativeGallery already decoded - no upload has happened yet at this
        /// point (see OnChangePhotoClicked/FinalizeSave). Takes ownership of tex
        /// (destroys the previous preview texture, keeps this one alive as
        /// _avatarPreviewTexture until it's replaced or the screen closes).</summary>
        private void ShowAvatarPreviewTexture(Texture2D tex)
        {
            if (_avatarPreview == null) { Destroy(tex); return; }

            if (_avatarPreviewLoadRoutine != null) { StopCoroutine(_avatarPreviewLoadRoutine); _avatarPreviewLoadRoutine = null; }
            if (_avatarPreviewTexture != null) Destroy(_avatarPreviewTexture);

            _avatarPreviewTexture = tex;

            _avatarPreview.style.backgroundImage = new StyleBackground(_avatarPreviewTexture);
            ApplyCoverBackground(_avatarPreview);
            if (_avatarPreviewInitialsLabel != null) _avatarPreviewInitialsLabel.style.display = DisplayStyle.None;
        }

        private void ShowAvatarInitials()
        {
            if (_avatarPreview != null) _avatarPreview.style.backgroundImage = StyleKeyword.Null;
            if (_avatarPreviewInitialsLabel != null) _avatarPreviewInitialsLabel.style.display = DisplayStyle.Flex;

            if (_avatarPreviewLoadRoutine != null) { StopCoroutine(_avatarPreviewLoadRoutine); _avatarPreviewLoadRoutine = null; }
            if (_avatarPreviewTexture != null) { Destroy(_avatarPreviewTexture); _avatarPreviewTexture = null; }
            _loadedAvatarUrl = null;
        }

        private void SetAvatarStatus(string message)
        {
            if (_avatarStatusLabel == null) return;
            _avatarStatusLabel.text = message;
            if (string.IsNullOrEmpty(message))
                _avatarStatusLabel.AddToClassList("hidden");
            else
                _avatarStatusLabel.RemoveFromClassList("hidden");
        }

        // Same initials logic as StudentProfileController.GetInitials - duplicated
        // rather than shared since the two controllers don't otherwise reference
        // each other and this is a couple of lines.
        // unityBackgroundScaleMode is obsolete (deprecated in favor of the CSS-style
        // background-* properties) - this is the ScaleAndCrop-equivalent combination:
        // fill the element, keep aspect ratio, crop overflow, centered.
        private static void ApplyCoverBackground(VisualElement element)
        {
            element.style.backgroundPositionX = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundPositionY = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundRepeat = new StyleBackgroundRepeat(new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat));
            element.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Cover));
        }

        private string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "?";

            var parts = fullName.Trim().Split(' ');
            if (parts.Length == 1) return parts[0].Substring(0, Mathf.Min(2, parts[0].Length)).ToUpper();

            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        private void OnClosePasswordOverlayClicked(ClickEvent evt)
        {
            ClosePasswordOverlay();
        }

        /// <summary>Reveals the password-card, floated over the whole screen
        /// with a dimmed backdrop behind it (like a modal).</summary>
        private void OpenPasswordOverlay()
        {
            _passwordOverlay?.RemoveFromClassList("hidden");
        }

        /// <summary>Hides the floating password-card overlay. Does not clear
        /// the password fields - only OnSaveComplete does that, so an
        /// in-progress edit survives closing/reopening the overlay.</summary>
        private void ClosePasswordOverlay()
        {
            _passwordOverlay?.AddToClassList("hidden");
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
            if (PlayerSessionManager.Instance.IsEmailVerified)
            {
                SetVerifyEmailStatus("Your email is already verified.");
                RefreshVerificationBadge();
                return;
            }

            _verifyEmailButton.SetEnabled(false);
            SetVerifyEmailStatus("Sending verification email...");

            PlayerSessionManager.Instance.SendEmailVerification((success, error) =>
            {
                _verifyEmailButton.SetEnabled(true);

                if (!success)
                {
                    Debug.LogError($"[StudentEditProfileController] Send verification email failed: {error}");
                    SetVerifyEmailStatus(error ?? "Could not send verification email. Please try again.");
                    return;
                }

                SetVerifyEmailStatus($"Verification email sent to {_loadedEmail}. Check your inbox and click the link, then reopen this screen.");
            });
        }

        /// <summary>Reloads the current user from Firebase so the verified/not
        /// verified badge reflects a link the student may have just clicked,
        /// then updates the badge text/style.</summary>
        private void RefreshVerificationBadge()
        {
            UpdateVerifyBadge(PlayerSessionManager.Instance.IsEmailVerified);

            PlayerSessionManager.Instance.RefreshEmailVerificationStatus(isVerified =>
            {
                UpdateVerifyBadge(isVerified);
                if (isVerified) StopVerificationPolling();
            });
        }

        /// <summary>Live-updates the verification badge while this screen is
        /// open: repeatedly re-checks Firebase so that if the student clicks
        /// the verification link in their inbox (e.g. on their phone or in
        /// another tab) the badge flips to "Verified" without them needing to
        /// leave and reopen this screen. No-ops (and stops itself) once the
        /// email is already verified, since there's nothing left to watch for.</summary>
        private void StartVerificationPolling()
        {
            CancelInvoke(nameof(PollEmailVerificationStatus));

            if (PlayerSessionManager.Instance == null || PlayerSessionManager.Instance.IsEmailVerified)
            {
                return;
            }

            InvokeRepeating(nameof(PollEmailVerificationStatus), verificationPollIntervalSeconds, verificationPollIntervalSeconds);
        }

        private void StopVerificationPolling()
        {
            CancelInvoke(nameof(PollEmailVerificationStatus));
        }

        private void PollEmailVerificationStatus()
        {
            if (PlayerSessionManager.Instance == null)
            {
                StopVerificationPolling();
                return;
            }

            PlayerSessionManager.Instance.RefreshEmailVerificationStatus(isVerified =>
            {
                UpdateVerifyBadge(isVerified);
                if (isVerified) StopVerificationPolling();
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

        /// <summary>Shows/hides the amber hint under the email field based on
        /// PlayerSessionManager.PendingEmail - covers both right after Save
        /// and reopening this screen later while a change is still
        /// unconfirmed (e.g. the student navigated away before verifying).</summary>
        private void UpdatePendingEmailHint()
        {
            if (_emailPendingHint == null) return;

            string pending = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.PendingEmail : null;
            if (string.IsNullOrEmpty(pending))
            {
                _emailPendingHint.text = string.Empty;
                _emailPendingHint.AddToClassList("hidden");
                return;
            }

            _emailPendingHint.text = $"Verification link sent to {pending} - your email stays as-is until you click it.";
            _emailPendingHint.RemoveFromClassList("hidden");
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

            PlayerSessionManager.Instance.UpdateProfile(fullName, email, currentPassword, (success, error) =>
            {
                if (!success)
                {
                    Debug.LogError($"[StudentEditProfileController] Profile update failed: {error}");
                    SetStatus(error ?? "Failed to save changes. Please try again.");
                    _saveChangesButton.SetEnabled(true);
                    return;
                }

                if (changingPassword)
                {
                    PlayerSessionManager.Instance.ChangePassword(currentPassword, newPassword, (pwSuccess, pwError) =>
                    {
                        if (pwSuccess)
                        {
                            Debug.Log("[StudentEditProfileController] Profile and password updated successfully.");
                            FinalizeSave(emailChanged, email);
                        }
                        else
                        {
                            Debug.LogError($"[StudentEditProfileController] Password change failed: {pwError}");
                            SetStatus(pwError ?? "Failed to change password. Please try again.");
                            _saveChangesButton.SetEnabled(true);
                        }
                    });
                }
                else
                {
                    Debug.Log("[StudentEditProfileController] Profile updated successfully.");
                    FinalizeSave(emailChanged, email);
                }
            });
        }

        /// <summary>Runs after the name/email/password fields (whichever applied)
        /// have already saved successfully. Uploads any photo the student picked
        /// this visit (see OnChangePhotoClicked) before handing off to
        /// OnSaveComplete - kept as a separate last step since a photo upload has
        /// nothing to do with Auth/email validation and can fail independently
        /// without the rest of the save being rolled back.</summary>
        private void FinalizeSave(bool emailChanged, string email)
        {
            if (_pendingAvatarBytes == null)
            {
                OnSaveComplete(emailChanged, email);
                return;
            }

            string uid = PlayerSessionManager.Instance?.CurrentStudent?.Uid;
            if (CloudinaryAvatarUploadService.Instance == null || string.IsNullOrEmpty(uid))
            {
                Debug.LogWarning("[StudentEditProfileController] Avatar upload service unavailable - other changes were still saved.");
                _pendingAvatarBytes = null;
                OnSaveComplete(emailChanged, email);
                return;
            }

            SetStatus("Saving changes... uploading photo...");

            CloudinaryAvatarUploadService.Instance.UploadAvatar(_pendingAvatarBytes, uid, (uploadSuccess, avatarUrl) =>
            {
                _pendingAvatarBytes = null; // either way, don't retry a stale pick on a future Save click

                if (!uploadSuccess)
                {
                    Debug.LogWarning("[StudentEditProfileController] Photo upload failed - other changes were still saved.");
                    OnSaveComplete(emailChanged, email);
                    return;
                }

                PlayerSessionManager.Instance.UpdateAvatarUrl(avatarUrl, (dbSuccess, dbError) =>
                {
                    if (!dbSuccess)
                        Debug.LogWarning($"[StudentEditProfileController] Uploaded photo but could not save it to the profile: {dbError}");

                    OnSaveComplete(emailChanged, email);
                });
            });
        }

        private void OnSaveComplete(bool emailChanged, string newEmail)
        {
            _saveChangesButton.SetEnabled(true);

            // Clear password fields either way; they're never re-displayed.
            _currentPasswordField.value = string.Empty;
            _newPasswordField.value = string.Empty;
            _confirmPasswordField.value = string.Empty;
            ClosePasswordOverlay();

            if (emailChanged)
            {
                // Auth.CurrentUser.Email (and therefore the profile screen's
                // display) won't actually become newEmail until the student
                // clicks the verification link in their inbox. Stay signed in
                // on the OLD email in the meantime - don't log out here or on
                // a fixed timer. PlayerSessionManager.UpdateProfile already
                // started background polling (on itself, not this screen) the
                // moment it sent the link, so the student gets logged out
                // automatically the instant they confirm it - even if they've
                // since navigated away from this screen. See
                // PlayerSessionManager.PollPendingEmailConfirmation.
                SetStatus($"Saved. A verification link was sent to {newEmail}. Check your inbox and click it - you'll be logged out automatically once it's confirmed.");
                UpdatePendingEmailHint();
                _saveChangesButton.SetEnabled(false);
                return;
            }

            SetStatus(string.Empty);

            // Push the updated name back into StudentProfileController so
            // it doesn't keep showing a stale value, then navigate back.
            UIManager.Instance.ShowStudentProfile();
        }

        /// <summary>Fired by PlayerSessionManager the instant it detects the
        /// student has confirmed a pending email change (from its own
        /// background polling, which keeps running no matter which screen is
        /// visible). By the time this fires PlayerSessionManager has already
        /// logged the student out and navigated to Login, so if this screen
        /// still happens to be enabled in that instant there's nothing left
        /// to do here except make sure it isn't left showing stale state.</summary>
        private void OnEmailChangeConfirmed()
        {
            SetStatus(string.Empty);
            UpdatePendingEmailHint();
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