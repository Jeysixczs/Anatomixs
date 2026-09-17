using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Reusable "working..." overlay for any screen with a save/submit/load step
    /// that can take a moment - same drop-in pattern as OfflineOverlay in this
    /// same folder: a plain C# helper (NOT a MonoBehaviour) built entirely in
    /// code, so any screen controller can add one with just
    /// <c>new LoadingOverlay(parent)</c> and call Show()/Hide() around whatever
    /// async call it's waiting on - no matching .uxml/.uss to add or keep in
    /// sync on that screen. A ring spinner is animated by nudging its rotation
    /// every tick (UI Toolkit has no USS keyframe animation), and a delayed
    /// "still working" hint appears under it for slow connections.
    ///
    /// Usage (typically from the owning controller's OnEnable, right after the
    /// screen's root is available):
    ///   _loading = new LoadingOverlay(screenRoot);
    ///   ...
    ///   _loading.Show("Saving changes...");
    ///   SomeAsyncCall(result => { _loading.Hide(); ... });
    ///
    /// Dispose() it when the screen goes away. Screens are fully rebuilt via
    /// VisualTreeAsset.CloneTree on every UIManager.ShowScreen() call (see
    /// UIManager.ShowScreen), so an overlay parented into the old tree is
    /// already gone the next time the screen shows and a fresh instance is
    /// needed for the new tree - exactly the same requirement OfflineOverlay's
    /// class comment calls out, and for the same reason.
    /// </summary>
    public class LoadingOverlay
    {
        private const string DefaultMessage = "Loading...";
        private const string DefaultSlowHint = "Still working - this can take longer on a weak connection.";

        // A slow/weak connection can leave a save/load pending far longer than
        // usual - past this many ms the overlay swaps in the reassuring "still
        // working" hint instead of leaving the spinner as the only feedback.
        private const long SlowHintDelayMs = 6000;

        private readonly VisualElement _root;
        private readonly VisualElement _spinner;
        private readonly Label _messageLabel;
        private readonly Label _submessageLabel;

        private IVisualElementScheduledItem _spinSchedule;
        private IVisualElementScheduledItem _slowHintSchedule;
        private float _spinAngle;

        /// <summary>True while the overlay is showing (DisplayStyle.Flex).</summary>
        public bool IsVisible => _root != null && _root.style.display == DisplayStyle.Flex;

        /// <param name="parent">The screen's own screen-root (or root) to overlay.
        /// Added as the LAST child so it paints on top of everything else already
        /// on that screen, and captures clicks so nothing underneath is
        /// interactable while it's showing.</param>
        public LoadingOverlay(VisualElement parent)
        {
            _root = new VisualElement { name = "loading-overlay" };
            _root.AddToClassList("reusable-loading-overlay");
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.right = 0;
            _root.style.top = 0;
            _root.style.bottom = 0;
            _root.style.backgroundColor = new Color(15f / 255f, 18f / 255f, 28f / 255f, 0.72f);
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;
            _root.style.paddingLeft = 60;
            _root.style.paddingRight = 60;
            _root.style.paddingTop = 60;
            _root.style.paddingBottom = 60;
            _root.style.display = DisplayStyle.None; // hidden until Show()
            _root.pickingMode = PickingMode.Position; // block taps to whatever is underneath

            var card = new VisualElement { name = "loading-overlay-card" };
            card.AddToClassList("reusable-loading-overlay-card");
            card.style.backgroundColor = Color.white;
            card.style.borderTopLeftRadius = 32;
            card.style.borderTopRightRadius = 32;
            card.style.borderBottomLeftRadius = 32;
            card.style.borderBottomRightRadius = 32;
            card.style.paddingTop = 54;
            card.style.paddingBottom = 54;
            card.style.paddingLeft = 48;
            card.style.paddingRight = 48;
            card.style.alignItems = Align.Center;
            card.style.maxWidth = 620;

            _spinner = new VisualElement { name = "loading-overlay-spinner" };
            _spinner.AddToClassList("reusable-loading-overlay-spinner");
            _spinner.style.width = 64;
            _spinner.style.height = 64;
            _spinner.style.borderTopLeftRadius = 32;
            _spinner.style.borderTopRightRadius = 32;
            _spinner.style.borderBottomLeftRadius = 32;
            _spinner.style.borderBottomRightRadius = 32;
            _spinner.style.borderTopWidth = 6;
            _spinner.style.borderBottomWidth = 6;
            _spinner.style.borderLeftWidth = 6;
            _spinner.style.borderRightWidth = 6;
            var purple = new Color(136f / 255f, 45f / 255f, 226f / 255f);
            _spinner.style.borderTopColor = purple;
            _spinner.style.borderLeftColor = purple;
            _spinner.style.borderRightColor = purple;
            _spinner.style.borderBottomColor = new Color(purple.r, purple.g, purple.b, 0.15f);
            _spinner.style.marginBottom = 28;
            card.Add(_spinner);

            _messageLabel = new Label(DefaultMessage) { name = "loading-overlay-message" };
            _messageLabel.AddToClassList("reusable-loading-overlay-message");
            _messageLabel.style.fontSize = 30;
            _messageLabel.style.color = new Color(31f / 255f, 36f / 255f, 48f / 255f);
            _messageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _messageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _messageLabel.style.whiteSpace = WhiteSpace.Normal;
            card.Add(_messageLabel);

            _submessageLabel = new Label(string.Empty) { name = "loading-overlay-submessage" };
            _submessageLabel.AddToClassList("reusable-loading-overlay-submessage");
            _submessageLabel.style.fontSize = 22;
            _submessageLabel.style.color = new Color(107f / 255f, 114f / 255f, 128f / 255f);
            _submessageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _submessageLabel.style.whiteSpace = WhiteSpace.Normal;
            _submessageLabel.style.marginTop = 12;
            _submessageLabel.style.display = DisplayStyle.None;
            card.Add(_submessageLabel);

            _root.Add(card);
            parent?.Add(_root);
        }

        /// <summary>Shows the overlay with the given headline (or the default
        /// "Loading..." if omitted), (re)starts the spinner spinning from 0, and
        /// arms the delayed "still working" hint fresh. Safe to call this to open
        /// a brand new wait - to change the headline of a wait already in
        /// progress without resetting the spinner or hint timer, use SetMessage
        /// instead.</summary>
        /// <param name="slowHint">Optional override for the delayed hint text -
        /// omit for the default "Still working..." copy.</param>
        public void Show(string message = null, string slowHint = null)
        {
            if (_root == null) return;

            _messageLabel.text = string.IsNullOrEmpty(message) ? DefaultMessage : message;
            _submessageLabel.text = string.Empty;
            _submessageLabel.style.display = DisplayStyle.None;
            _root.style.display = DisplayStyle.Flex;
            _root.BringToFront();

            // Spin the ring ~1.4 revolutions/sec by nudging its rotation every
            // frame-ish tick.
            _spinAngle = 0f;
            _spinSchedule?.Pause();
            _spinSchedule = _root.schedule.Execute(() =>
            {
                _spinAngle = (_spinAngle + 15f) % 360f;
                _spinner.style.rotate = new StyleRotate(new Rotate(_spinAngle));
            }).Every(30);

            _slowHintSchedule?.Pause();
            _slowHintSchedule = _root.schedule.Execute(() =>
            {
                _submessageLabel.text = string.IsNullOrEmpty(slowHint) ? DefaultSlowHint : slowHint;
                _submessageLabel.style.display = DisplayStyle.Flex;
            });
            _slowHintSchedule.ExecuteLater(SlowHintDelayMs);
        }

        /// <summary>Updates the headline while the overlay is already showing
        /// (e.g. switching from "Saving changes..." to "Uploading photo...")
        /// without resetting the spinner or the "still working" hint timer.</summary>
        public void SetMessage(string message)
        {
            if (_messageLabel == null) return;
            _messageLabel.text = message;
        }

        public void Hide()
        {
            if (_root == null) return;
            _root.style.display = DisplayStyle.None;
            _spinSchedule?.Pause();
            _spinSchedule = null;
            _slowHintSchedule?.Pause();
            _slowHintSchedule = null;
        }

        /// <summary>Detaches the overlay from its parent and stops its schedules.
        /// Call from the owning controller's OnDisable (or right before building
        /// a fresh one for a newly (re)opened screen) - same reasoning as
        /// OfflineOverlay.Dispose.</summary>
        public void Dispose()
        {
            _spinSchedule?.Pause();
            _slowHintSchedule?.Pause();
            _root?.RemoveFromHierarchy();
        }
    }
}
