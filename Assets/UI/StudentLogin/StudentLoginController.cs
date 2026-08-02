using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentLogin.uxml. Attach to a GameObject that has a
    /// UIDocument component pointed at StudentLogin.uxml / StudentLogin.uss.
    ///
    /// Responsibilities:
    ///  - Wires up buttons (Sign In, Google, Admin Login, password toggle)
    ///  - Basic client-side validation with inline error labels
    ///  - Applies the purple->pink gradient textures at runtime (USS can't do
    ///    linear-gradient, so we generate small Texture2Ds and assign them as
    ///    backgroundImage on the logo circle and the Sign In button)
    ///  - A simple "compact" breakpoint toggle for very small phone screens
    ///
    /// Hook up your real auth call inside OnSignInClicked() / OnGoogleClicked()
    /// - e.g. call into your existing PlayerSessionManager here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentLoginController : MonoBehaviour
    {
        [Header("Gradient colors (matches the mock)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("Compact breakpoints (px, logical panel size)")]
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

        private VisualElement _passwordEyeIcon;

        private Button _signInButton;
        private Button _createAccountButton;
        private Button _forgotPasswordButton;
        private TextField _emailField;
        private Label _emailError;

        private TextField _passwordField;
        private Label _passwordError;
        private Button _togglePasswordButton;

        private Button _googleButton;
        private Button _adminLoginButton;
        private Label _statusLabel;

        private bool _passwordVisible;

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


        }

        private void OnDisable()
        {
            if (_root == null) return;

            _signInButton?.UnregisterCallback<ClickEvent>(OnSignInClicked);
            _googleButton?.UnregisterCallback<ClickEvent>(OnGoogleClicked);
            _adminLoginButton?.UnregisterCallback<ClickEvent>(OnAdminLoginClicked);
            _togglePasswordButton?.UnregisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _createAccountButton?.UnregisterCallback<ClickEvent>(OnCreateAccountClicked);
            _forgotPasswordButton?.UnregisterCallback<ClickEvent>(OnForgotPasswordClicked);
            _root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {

            _signInButton = _root.Q<Button>("sign-in-button");
            _createAccountButton = _root.Q<Button>("navigate-to-create-account-button");

            _emailField = _root.Q<TextField>("email-field");
            _emailError = _root.Q<Label>("email-error");

            _passwordField = _root.Q<TextField>("password-field");
            _passwordError = _root.Q<Label>("password-error");
            _togglePasswordButton = _root.Q<Button>("toggle-password-button");
            _forgotPasswordButton = _root.Q<Button>("forgot-password-button");
            // Get the eye icon element inside the toggle button
            _passwordEyeIcon = _togglePasswordButton?.Q<VisualElement>(className: "icon-eye");

            _googleButton = _root.Q<Button>("google-signin-button");
            _adminLoginButton = _root.Q<Button>("admin-login-button");
            _statusLabel = _root.Q<Label>("status-label");

        }

        private void WireCallbacks()
        {
            _signInButton.RegisterCallback<ClickEvent>(OnSignInClicked);
            _googleButton.RegisterCallback<ClickEvent>(OnGoogleClicked);
            _adminLoginButton.RegisterCallback<ClickEvent>(OnAdminLoginClicked);
            _togglePasswordButton.RegisterCallback<ClickEvent>(OnTogglePasswordClicked);
            _createAccountButton.RegisterCallback<ClickEvent>(OnCreateAccountClicked);
            _forgotPasswordButton?.RegisterCallback<ClickEvent>(OnForgotPasswordClicked);
            // Re-evaluate the compact layout whenever the panel is resized
            _root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);


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

        private void OnForgotPasswordClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowForgotPassword();
        }
        private void OnCreateAccountClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowCreateAccount();
        }


        private void OnSignInClicked(ClickEvent evt)
        {
            bool valid = true;

            string email = _emailField.value?.Trim();
            if (string.IsNullOrEmpty(email) || !EmailRegex.IsMatch(email))
            {
                SetError(_emailError, "Please enter a valid email address");
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
            _signInButton.SetEnabled(false);

            // TODO: replace with your real auth call, e.g.:
            // PlayerSessionManager.Instance.LoginStudent(email, _passwordField.value, OnLoginResult);
            PlayerSessionManager.Instance.LoginStudent(email, _passwordField.value, (success, errorMessage) =>
            {
                if (success)
                {
                    // Navigate to the student dashboard on successful login
                    UIManager.Instance.ShowStudentDashboard();
                }
                else
                {
                    // Show error message on failed login
                    SetStatus(errorMessage);
                    _signInButton.SetEnabled(true);
                }
            });

        }



        private void OnGoogleClicked(ClickEvent evt)
        {
            Debug.Log("[StudentLoginController] Google sign-in tapped.");
            SetStatus("Signing in with Google...");
            _googleButton.SetEnabled(false);

            PlayerSessionManager.Instance.LoginWithGoogle((success, errorMessage) =>
            {
                _googleButton.SetEnabled(true);

                if (success)
                {
                    UIManager.Instance.ShowStudentDashboard();
                }
                else
                {
                    SetStatus(errorMessage);
                }
            });
        }

        private void OnAdminLoginClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowAdminLogin();
        }

        // ---------------- Helpers ----------------

        private void SetError(Label label, string message)
        {
            label.text = message;
            label.RemoveFromClassList("hidden");
        }

        private void ClearError(Label label)
        {
            label.text = string.Empty;
            label.AddToClassList("hidden");
        }

        private void SetStatus(string message)
        {
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
            float width = _root.resolvedStyle.width;
            float height = _root.resolvedStyle.height;

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

            _root.EnableInClassList("compact", compact);
            _root.EnableInClassList("landscape", landscape);
            _root.EnableInClassList("ultra-compact", ultraCompact);
        }

        // ---------------- Gradients (USS has no linear-gradient) ----------------

        private void ApplyGradients()
        {
            var horizontal = BuildGradientTexture(gradientStart, gradientEnd, true);
            var diagonal = BuildGradientTexture(gradientStart, gradientEnd, false);

            _signInButton.style.backgroundImage = new StyleBackground(horizontal);
            // If you want to apply gradient to logo circle as well:
            // _logoCircle.style.backgroundImage = new StyleBackground(diagonal);
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