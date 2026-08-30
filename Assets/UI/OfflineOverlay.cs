using System;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Reusable "You're Offline" overlay for any screen that depends on a live
    /// Firestore listener - currently used by StudentClassroomHubController
    /// (opening "My Classrooms" while offline) and
    /// StudentClassroomDetailController (opening/staying on a classroom while
    /// offline). Covers two scenarios with the same component:
    ///
    ///  1. The student opens the screen while already offline - the owning
    ///     controller checks NetworkStatusMonitor.IsOnline before starting its
    ///     listener/fetch and calls Show() instead.
    ///  2. The student is already on the screen and the connection drops - the
    ///     owning controller subscribes to NetworkStatusMonitor.OnConnectivityChanged
    ///     and calls Show()/Hide() as that fires.
    ///
    /// This class is NOT a MonoBehaviour and does not itself watch connectivity -
    /// it is a plain C# helper a screen controller instantiates once (typically
    /// in OnEnable, since UIManager.ShowScreen() rebuilds the whole screen tree
    /// via VisualTreeAsset.CloneTree on every navigation - see UIManager.ShowScreen -
    /// so any overlay parented into the old tree is gone the next time the screen
    /// is shown and must be rebuilt). Dispose() it when the screen goes away.
    ///
    /// Built entirely in code (no matching .uxml) so it can be dropped onto any
    /// existing screen without a UI Builder asset. Uses a handful of USS class
    /// names (offline-overlay*) purely so a stylesheet can override the inline
    /// styling later if one is ever added - none are required for it to work.
    /// </summary>
    public class OfflineOverlay
    {
        private const string DefaultMessage =
            "Check your Wi-Fi or mobile data connection, then tap Retry.";

        private readonly VisualElement _root;
        private readonly Label _messageLabel;
        private readonly Button _retryButton;
        private readonly Button _goBackButton;

        [SerializeField] private Texture2D offlineIcon;

        private readonly Action _onRetry;
        private readonly Action _onGoBack;

        /// <summary>True while the overlay is showing (DisplayStyle.Flex).</summary>
        public bool IsVisible => _root != null && _root.style.display == DisplayStyle.Flex;

        /// <param name="parent">The screen's own screen-root (or root) to overlay.
        /// Added as the LAST child so it paints on top of everything else already
        /// on that screen, and captures clicks so nothing underneath is
        /// interactable while it's showing.</param>
        /// <param name="onRetry">Called when Retry is tapped AND a connectivity
        /// check at that moment says we're back online. The caller is
        /// responsible for actually reloading data (e.g. StartClassroomsListener()
        /// / LoadClassroomContent()) - this class only decides whether it's worth
        /// attempting.</param>
        /// <param name="onGoBack">Called when "Go back to Dashboard" is tapped -
        /// pass e.g. () => UIManager.Instance?.ShowStudentDashboard().</param>
        /// <param name="offlineIconTexture">Optional explicit icon texture (e.g. wired up
        /// from a serialized field on the owning MonoBehaviour). If omitted, this falls
        /// back to <c>Resources.Load&lt;Texture2D&gt;(ResourcesIconPath)</c> - note that
        /// only works if the PNG actually lives under a "Resources" folder at runtime;
        /// a "project://database/..." path is an Editor-only asset-database reference
        /// and will not resolve in a build.</param>
        public OfflineOverlay(VisualElement parent, Action onRetry, Action onGoBack, Texture2D offlineIconTexture = null)
        {
            _onRetry = onRetry;
            _onGoBack = onGoBack;

            _root = new VisualElement { name = "offline-overlay" };
            _root.AddToClassList("offline-overlay");
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.right = 0;
            _root.style.top = 0;
            _root.style.bottom = 0;
            _root.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;
            _root.style.display = DisplayStyle.None; // hidden until Show()
            _root.pickingMode = PickingMode.Position; // block taps to whatever is underneath

            var card = new VisualElement { name = "offline-overlay-card" };
            card.AddToClassList("offline-overlay-card");
            card.style.backgroundColor = Color.white;
            card.style.borderTopLeftRadius = 24;
            card.style.borderTopRightRadius = 24;
            card.style.borderBottomLeftRadius = 24;
            card.style.borderBottomRightRadius = 24;
            card.style.paddingTop = 36;
            card.style.paddingBottom = 30;
            card.style.paddingLeft = 30;
            card.style.paddingRight = 30;
            card.style.width = new Length(84, LengthUnit.Percent);
            card.style.maxWidth = 460;
            card.style.alignItems = Align.Center;

            var icon = new VisualElement { name = "offline-overlay-icon" };
            icon.AddToClassList("unity-image");

            Debug.Log($"[OfflineOverlay] offlineIconTexture param is {(offlineIconTexture != null ? "SET (" + offlineIconTexture.name + ")" : "NULL")}");

            var iconTexture = offlineIconTexture;
            if (iconTexture == null)
            {
                iconTexture = Resources.Load<Texture2D>(ResourcesIconPath);
                Debug.Log($"[OfflineOverlay] Resources.Load(\"{ResourcesIconPath}\") returned {(iconTexture != null ? "a texture (" + iconTexture.width + "x" + iconTexture.height + ")" : "NULL - check the file is under a 'Resources' folder and the path/case match exactly")}");

                if (iconTexture == null)
                {
                    // Diagnostic: list every texture Resources.Load can actually see,
                    // at increasingly broad folder scopes, to find where the asset really is.
                    var inIcons = Resources.LoadAll<Texture2D>("Icons");
                    Debug.Log($"[OfflineOverlay] DIAGNOSTIC: Resources.LoadAll<Texture2D>(\"Icons\") found {inIcons.Length} texture(s): {string.Join(", ", System.Array.ConvertAll(inIcons, t => t.name))}");

                    var everything = Resources.LoadAll<Texture2D>("");
                    Debug.Log($"[OfflineOverlay] DIAGNOSTIC: Resources.LoadAll<Texture2D>(\"\") (ALL textures under ANY Resources folder) found {everything.Length} texture(s): {string.Join(", ", System.Array.ConvertAll(everything, t => t.name))}");
                }
            }

            if (iconTexture != null)
            {
                icon.style.backgroundImage = new StyleBackground(iconTexture);
                Debug.Log("[OfflineOverlay] backgroundImage assigned successfully.");
            }
            else
            {
                Debug.LogWarning("[OfflineOverlay] No icon texture available - background image will be blank. " +
                    "Either pass offlineIconTexture explicitly, or verify the PNG exists at Assets/Resources/" + ResourcesIconPath + ".png");
            }

         //   icon.style.unityBackgroundScaleMode = new StyleEnum<ScaleMode>(ScaleMode.ScaleToFit);
            icon.style.width = 64;
            icon.style.height = 64;
            icon.style.marginBottom = 18;
            card.Add(icon);

            icon.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                Debug.Log($"[OfflineOverlay] icon element resolved size: {evt.newRect.width}x{evt.newRect.height}, " +
                    $"resolved style.backgroundImage.value.texture is {(icon.resolvedStyle.backgroundImage.texture != null ? "non-null" : "NULL")}");
            });

            var titleLabel = new Label("You're Offline");
            titleLabel.style.fontSize = 22;
            titleLabel.style.color = new Color(0.11f, 0.12f, 0.15f);
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 10;
            titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            card.Add(titleLabel);

            _messageLabel = new Label(DefaultMessage);
            _messageLabel.style.fontSize = 14;
            _messageLabel.style.color = new Color(0.45f, 0.47f, 0.52f);
            _messageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _messageLabel.style.whiteSpace = WhiteSpace.Normal;
            _messageLabel.style.marginBottom = 26;
            card.Add(_messageLabel);

            var buttonsColumn = new VisualElement();
            buttonsColumn.style.width = new Length(100, LengthUnit.Percent);
            buttonsColumn.style.flexDirection = FlexDirection.Column;
            buttonsColumn.style.alignItems = Align.Center;

            _retryButton = new Button { text = "Retry" };
            _retryButton.AddToClassList("offline-overlay-retry-button");
            StylePrimaryButton(_retryButton);
            _retryButton.style.marginBottom = 14;
            _retryButton.clicked += HandleRetryClicked;
            buttonsColumn.Add(_retryButton);

            _goBackButton = new Button { text = "Go back to Dashboard" };
            _goBackButton.AddToClassList("offline-overlay-go-back-button");
            StyleLinkButton(_goBackButton);
            _goBackButton.clicked += HandleGoBackClicked;
            buttonsColumn.Add(_goBackButton);

            card.Add(buttonsColumn);
            _root.Add(card);

            parent?.Add(_root);
        }

        /// <summary>Resources-relative path used when no explicit icon texture is passed
        /// in. Place the PNG at Assets/Resources/UI/Icons/offline-icon.png for this to
        /// resolve at runtime (Resources.Load takes a path without the extension and
        /// without the leading "Resources/").</summary>
        private const string ResourcesIconPath = "Icons/offline-icon";

        private static void StylePrimaryButton(Button button)
        {
            button.style.width = new Length(100, LengthUnit.Percent);
            button.style.height = 50;
            button.style.borderTopLeftRadius = 14;
            button.style.borderTopRightRadius = 14;
            button.style.borderBottomLeftRadius = 14;
            button.style.borderBottomRightRadius = 14;
            button.style.backgroundColor = new Color(0.29f, 0.42f, 0.90f); // matches app's blue accent
            button.style.color = Color.white;
            button.style.fontSize = 16;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.borderLeftWidth = 0;
            button.style.borderRightWidth = 0;
            button.style.borderTopWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
        }

        /// <summary>Plain text "link" style - no background/border, just blue text,
        /// used for the lower-priority "Go back to Dashboard" action.</summary>
        private static void StyleLinkButton(Button button)
        {
            button.style.height = 24;
            button.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
            button.style.color = new Color(0.29f, 0.42f, 0.90f); // same blue as the primary button
            button.style.fontSize = 15;
            button.style.unityFontStyleAndWeight = FontStyle.Normal;
            button.style.borderLeftWidth = 0;
            button.style.borderRightWidth = 0;
            button.style.borderTopWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.marginTop = 0;
            button.style.paddingLeft = 4;
            button.style.paddingRight = 4;
        }

        /// <summary>Shows the overlay. Pass a custom message to override the default
        /// (e.g. after a failed Retry) - omit it to reset to the default copy, so a
        /// stale "still offline" message from a previous attempt doesn't linger the
        /// next time this is shown fresh.</summary>
        public void Show(string message = null)
        {
            if (_root == null) return;

            _messageLabel.text = string.IsNullOrEmpty(message) ? DefaultMessage : message;
            _root.style.display = DisplayStyle.Flex;
            _root.BringToFront();
        }

        public void Hide()
        {
            if (_root == null) return;
            _root.style.display = DisplayStyle.None;
        }

        private void HandleRetryClicked()
        {
            if (NetworkStatusMonitor.IsOnline)
            {
                Hide();
                _onRetry?.Invoke();
            }
            else
            {
                // Still offline - give a quick nudge instead of the button doing
                // nothing visible.
                Show("Still no connection. Check your Wi-Fi or mobile data and try again.");
            }
        }

        private void HandleGoBackClicked()
        {
            _onGoBack?.Invoke();
        }

        /// <summary>Detaches the overlay from its parent and unhooks button handlers.
        /// Call from the owning controller's OnDisable (or right before building a
        /// fresh one in OnEnable) - screens are fully rebuilt via CloneTree on every
        /// UIManager.ShowScreen() call, so a previous overlay's VisualElement is
        /// already gone from the tree, but this also drops the closures so they
        /// don't linger on the old instance.</summary>
        public void Dispose()
        {
            if (_retryButton != null) _retryButton.clicked -= HandleRetryClicked;
            if (_goBackButton != null) _goBackButton.clicked -= HandleGoBackClicked;
            _root?.RemoveFromHierarchy();
        }
    }
}