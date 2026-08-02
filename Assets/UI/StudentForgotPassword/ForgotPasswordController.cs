using Anatomia3D.Backend;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for ForgotPassword.uxml. Attach to a GameObject that has a
    /// UIDocument component pointed at ForgotPassword.uxml / ForgotPassword.uss.
    ///
    /// Responsibilities:
    ///  - Wires up buttons (Send Reset Link, Back to Login)
    ///  - Email validation with inline error labels
    ///  - Applies gradient textures at runtime
    ///  - A "compact" / "landscape" / "ultra-compact" breakpoint system
    ///    (mirrors StudentLoginController) that reacts to rotation and
    ///    panel resizing
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class ForgotPasswordController : MonoBehaviour
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

        private Button _sendResetLinkButton;
        private Button _backToLoginButton;
        private TextField _emailField;
        private Label _emailError;
        private Label _statusLabel;
        private Label _successMessageLabel;

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

            // Hide success message initially
            _successMessageLabel?.AddToClassList("hidden");
        }

        private void OnDisable()
        {
            if (_root == null) return;

            _sendResetLinkButton?.UnregisterCallback<ClickEvent>(OnSendResetLinkClicked);
            _backToLoginButton?.UnregisterCallback<ClickEvent>(OnBackToLoginClicked);

            _root.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _sendResetLinkButton = _root.Q<Button>("send-reset-link-button");
            _backToLoginButton = _root.Q<Button>("back-to-login-button");

            _emailField = _root.Q<TextField>("email-field");
            _emailError = _root.Q<Label>("email-error");
            _statusLabel = _root.Q<Label>("status-label");
            _successMessageLabel = _root.Q<Label>("success-message-label");
        }

        private void WireCallbacks()
        {
            _sendResetLinkButton.RegisterCallback<ClickEvent>(OnSendResetLinkClicked);
            _backToLoginButton.RegisterCallback<ClickEvent>(OnBackToLoginClicked);

            // Re-evaluate the compact layout whenever the panel is resized
            _root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        // ---------------- Button handlers ----------------

        private void OnBackToLoginClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentLogin();
        }

        private void OnSendResetLinkClicked(ClickEvent evt)
        {
            // Hide previous success message
            _successMessageLabel?.AddToClassList("hidden");

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

            if (!valid)
            {
                SetStatus("Please fix the highlighted fields.");
                return;
            }

            SetStatus("Sending reset link...");
            _sendResetLinkButton.SetEnabled(false);

            PlayerSessionManager.Instance.SendPasswordResetEmail(email, OnResetLinkSent);
        }

        private void OnResetLinkSent(bool success, string error)
        {
            _sendResetLinkButton.SetEnabled(true);

            if (!success)
            {
                SetStatus(string.Empty);
                SetError(_emailError, error ?? "Could not send the reset link. Please try again.");
                return;
            }

            SetStatus(string.Empty);

            // Show success message
            _successMessageLabel?.RemoveFromClassList("hidden");

            // Clear the email field
            _emailField.value = string.Empty;
            ClearError(_emailError);

            Debug.Log("[ForgotPasswordController] Password reset email sent.");
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

            _sendResetLinkButton.style.backgroundImage = new StyleBackground(horizontal);
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