using System;
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
    /// sync on that screen.
    ///
    /// Look: minimal and full-screen. A translucent dark-gray layer covers the
    /// whole screen (it dims the screen, which stays faintly visible underneath,
    /// and nothing under it can be tapped), with a thin white arc spinner and a
    /// single line of white text centred on it. After a
    /// few seconds a quiet "still working" line fades in for slow connections.
    /// No card, no shadow, no extra decoration.
    ///
    /// Everything animates from ONE scheduled tick that derives its state from
    /// real time (Time.realtimeSinceStartup), so motion stays smooth even if a
    /// tick is late and keeps running when Time.timeScale is 0.
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
        /// <summary>Spinner accent colour. Student = the student dashboard purple,
        /// Admin = the admin dashboard green. Defaults to Student, so existing
        /// <c>new LoadingOverlay(parent)</c> calls compile unchanged.</summary>
        public enum Theme { Student, Admin }

        private const string DefaultMessage = "Loading...";
        private const string DefaultSlowHint = "Still working - this can take longer on a weak connection.";

        // A slow/weak connection can leave a save/load pending far longer than
        // usual - past this many ms the overlay swaps in the reassuring "still
        // working" hint instead of leaving the spinner as the only feedback.
        private const long SlowHintDelayMs = 6000;

        // ---- Sizes (px, 1080 x 1920 reference canvas - same scale as the student/admin screens) ----
        private const float SpinnerSize = 116f;

        // ---- Motion ----
        private const float TickMs = 16f;            // ~60 fps
        private const float FadeInSec = 0.18f;
        private const float HintFadeSec = 0.30f;
        private const float SpinDegPerSec = 300f;    // base rotation of the arc
        private const float SweepCycleSec = 1.6f;    // one grow/shrink breath of the arc
        private const float MinSweepDeg = 60f;
        private const float MaxSweepDeg = 240f;

        // Dark gray (the app's title colour, rgb 31,36,48) laid over the screen so it dims it while
        // staying faintly visible. 0 = invisible, 1 = solid. Lower = more of the screen shows.
        private const float BackdropAlpha = 0.55f;
        private static readonly Color BackdropColor = new Color(31f / 255f, 36f / 255f, 48f / 255f, BackdropAlpha);

        private readonly VisualElement _root;
        private readonly VisualElement _spinner;
        private readonly Label _messageLabel;
        private readonly Label _submessageLabel;

        private readonly Color _tint;   // lightened theme accent - the arc's tail colour (head is white)

        private IVisualElementScheduledItem _tickSchedule;
        private IVisualElementScheduledItem _slowHintSchedule;
        private IVisualElementScheduledItem _timeoutSchedule;

        private float _shownAt;            // realtime the overlay was opened (fade-in + spin phase)
        private float _hintShownAt = -1f;  // realtime the slow hint was revealed, -1 = not shown

        /// <summary>True while the overlay is showing (DisplayStyle.Flex).</summary>
        public bool IsVisible => _root != null && _root.style.display == DisplayStyle.Flex;

        /// <param name="parent">The screen's own screen-root (or root) to overlay.
        /// Added as the LAST child so it paints on top of everything else already
        /// on that screen, and captures clicks so nothing underneath is
        /// interactable while it's showing.</param>
        /// <param name="theme">Spinner accent colour - see <see cref="Theme"/>.</param>
        public LoadingOverlay(VisualElement parent, Theme theme = Theme.Student)
        {
            Color accent = theme == Theme.Admin
                ? new Color(22f / 255f, 188f / 255f, 118f / 255f)   // green
                : new Color(142f / 255f, 45f / 255f, 226f / 255f);  // purple
            _tint = Color.Lerp(accent, Color.white, 0.45f);          // lightened so it reads on the dark layer

            // ---------------- Full-screen layer ----------------
            _root = new VisualElement { name = "loading-overlay" };
            _root.AddToClassList("reusable-loading-overlay");
            _root.style.position = Position.Absolute;
            _root.style.left = 0;
            _root.style.right = 0;
            _root.style.top = 0;
            _root.style.bottom = 0;
            _root.style.backgroundColor = BackdropColor;
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;
            _root.style.paddingLeft = 80;
            _root.style.paddingRight = 80;
            _root.style.paddingTop = 80;
            _root.style.paddingBottom = 80;
            _root.style.display = DisplayStyle.None;   // hidden until Show()
            _root.pickingMode = PickingMode.Position;  // block taps to whatever is underneath

            // ---------------- Spinner (Painter2D arc) ----------------
            _spinner = new VisualElement { name = "loading-overlay-spinner" };
            _spinner.AddToClassList("reusable-loading-overlay-spinner");
            _spinner.style.width = SpinnerSize;
            _spinner.style.height = SpinnerSize;
            _spinner.style.flexShrink = 0;
            _spinner.style.marginBottom = 56;
            _spinner.pickingMode = PickingMode.Ignore;
            _spinner.generateVisualContent += OnGenerateSpinner;
            _root.Add(_spinner);

            // ---------------- Headline ----------------
            _messageLabel = new Label(DefaultMessage) { name = "loading-overlay-message" };
            _messageLabel.AddToClassList("reusable-loading-overlay-message");
            _messageLabel.style.fontSize = 34;
            _messageLabel.style.color = Color.white;
            _messageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _messageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _messageLabel.style.whiteSpace = WhiteSpace.Normal;
            _messageLabel.style.maxWidth = new Length(100, LengthUnit.Percent);
            _root.Add(_messageLabel);

            // ---------------- "Still working" line ----------------
            _submessageLabel = new Label(string.Empty) { name = "loading-overlay-submessage" };
            _submessageLabel.AddToClassList("reusable-loading-overlay-submessage");
            _submessageLabel.style.fontSize = 26;
            _submessageLabel.style.color = new Color(1f, 1f, 1f, 0.72f);
            _submessageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _submessageLabel.style.whiteSpace = WhiteSpace.Normal;
            _submessageLabel.style.marginTop = 20;
            _submessageLabel.style.maxWidth = new Length(86, LengthUnit.Percent);
            _submessageLabel.style.opacity = 0f;
            _submessageLabel.style.display = DisplayStyle.None;
            _root.Add(_submessageLabel);

            parent?.Add(_root);
        }

        /// <summary>Shows the overlay with the given headline (or the default
        /// "Loading..." if omitted) and arms the delayed "still working" hint
        /// fresh. If the overlay is already up (e.g. swapping to a "Success!
        /// Redirecting..." message) the fade-in is NOT replayed, so there's no
        /// flicker. To change the headline of a wait already in progress without
        /// resetting the hint timer, use SetMessage instead.</summary>
        /// <param name="slowHint">Optional override for the delayed hint text -
        /// omit for the default "Still working..." copy.</param>
        public void Show(string message = null, string slowHint = null)
        {
            if (_root == null) return;

            // A plain Show() never carries a timeout - cancel any left over from an earlier
            // ShowWithTimeout (e.g. when swapping to a "Success! Redirecting..." message).
            _timeoutSchedule?.Pause();
            _timeoutSchedule = null;

            bool wasVisible = IsVisible;

            _messageLabel.text = string.IsNullOrEmpty(message) ? DefaultMessage : message;

            // (Re)arm the slow hint: hide it until the delay passes again.
            _hintShownAt = -1f;
            _submessageLabel.text = string.Empty;
            _submessageLabel.style.opacity = 0f;
            _submessageLabel.style.display = DisplayStyle.None;

            if (!wasVisible)
            {
                _shownAt = Time.realtimeSinceStartup;
                _root.style.opacity = 0f;
            }

            _root.style.display = DisplayStyle.Flex;
            _root.BringToFront();

            _tickSchedule?.Pause();
            _tickSchedule = _root.schedule.Execute(() => Tick()).Every((long)TickMs);
            Tick(); // paint the first frame immediately instead of waiting for the first interval

            _slowHintSchedule?.Pause();
            string hint = string.IsNullOrEmpty(slowHint) ? DefaultSlowHint : slowHint;
            _slowHintSchedule = _root.schedule.Execute(() =>
            {
                _submessageLabel.text = hint;
                _submessageLabel.style.opacity = 0f;
                _submessageLabel.style.display = DisplayStyle.Flex;
                _hintShownAt = Time.realtimeSinceStartup; // Tick() fades it in
            });
            _slowHintSchedule.ExecuteLater(SlowHintDelayMs);
        }

        /// <summary>Like Show(), but if Hide() hasn't been called within
        /// <paramref name="timeoutMs"/> the overlay closes itself and calls
        /// <paramref name="onTimeout"/>. Use it around network calls that can hang
        /// forever (sign-in / account creation), so a stalled request can't leave the
        /// full-screen overlay up and block every tap. Opt-in: plain Show() behaves as before.</summary>
        public void ShowWithTimeout(string message, long timeoutMs, Action onTimeout, string slowHint = null)
        {
            if (_root == null) return;

            Show(message, slowHint);

            _timeoutSchedule = _root.schedule.Execute(() =>
            {
                _timeoutSchedule = null;
                if (!IsVisible) return;
                Hide();
                onTimeout?.Invoke();
            });
            _timeoutSchedule.ExecuteLater(timeoutMs);
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
            _tickSchedule?.Pause();
            _tickSchedule = null;
            _slowHintSchedule?.Pause();
            _slowHintSchedule = null;
            _timeoutSchedule?.Pause();
            _timeoutSchedule = null;
            _hintShownAt = -1f;
        }

        /// <summary>Detaches the overlay from its parent and stops its schedules.
        /// Call from the owning controller's OnDisable (or right before building
        /// a fresh one for a newly (re)opened screen) - same reasoning as
        /// OfflineOverlay.Dispose.</summary>
        public void Dispose()
        {
            _tickSchedule?.Pause();
            _slowHintSchedule?.Pause();
            _timeoutSchedule?.Pause();
            _root?.RemoveFromHierarchy();
        }

        // ------------------------------------------------------------------
        // Animation
        // ------------------------------------------------------------------

        /// <summary>One frame of everything that moves: fade-in, hint fade, and a
        /// repaint of the arc.</summary>
        private void Tick()
        {
            if (_root == null || !IsVisible) return;

            float now = Time.realtimeSinceStartup;

            _root.style.opacity = Mathf.Clamp01((now - _shownAt) / FadeInSec);

            if (_hintShownAt >= 0f)
                _submessageLabel.style.opacity = Mathf.Clamp01((now - _hintShownAt) / HintFadeSec);

            _spinner.MarkDirtyRepaint();
        }

        /// <summary>Draws a faint ring and a thin sweeping arc that fades from
        /// transparent (tail) to white (head). The arc is a run of short butt-capped
        /// segments with rising alpha, plus one round cap drawn on the head, so the
        /// tail can be truly transparent over the translucent layer.</summary>
        private void OnGenerateSpinner(MeshGenerationContext mgc)
        {
            Rect r = _spinner.contentRect;
            float size = Mathf.Min(r.width, r.height);
            if (size <= 1f) return;

            var p = mgc.painter2D;
            float stroke = size * 0.07f;
            float radius = (size - stroke) * 0.5f;
            var center = new Vector2(r.width * 0.5f, r.height * 0.5f);

            // Track ring.
            p.lineWidth = stroke;
            p.lineCap = LineCap.Round;
            p.strokeColor = new Color(1f, 1f, 1f, 0.16f);
            p.BeginPath();
            p.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Stroke();

            // Sweeping arc.
            float t = Time.realtimeSinceStartup - _shownAt;
            float breath = 0.5f - 0.5f * Mathf.Cos(t * (Mathf.PI * 2f / SweepCycleSec)); // 0..1
            float sweep = Mathf.Lerp(MinSweepDeg, MaxSweepDeg, breath);
            float start = (t * SpinDegPerSec) % 360f - 90f; // begin at 12 o'clock

            p.lineCap = LineCap.Butt;
            int segments = Mathf.Clamp(Mathf.CeilToInt(sweep / 10f), 6, 26);
            float step = sweep / segments;
            for (int i = 0; i < segments; i++)
            {
                float k = (i + 0.5f) / segments; // 0 = tail, 1 = head
                Color c = Color.Lerp(_tint, Color.white, k);
                c.a = Mathf.Pow(k, 1.3f);

                float a0 = start + step * i;
                float a1 = a0 + step + (i < segments - 1 ? 0.6f : 0f); // tiny overlap hides seams

                p.strokeColor = c;
                p.BeginPath();
                p.Arc(center, radius, Angle.Degrees(a0), Angle.Degrees(a1));
                p.Stroke();
            }

            // Round cap on the head.
            float headRad = (start + sweep) * Mathf.Deg2Rad;
            var head = center + new Vector2(Mathf.Cos(headRad), Mathf.Sin(headRad)) * radius;
            p.fillColor = Color.white;
            p.BeginPath();
            p.Arc(head, stroke * 0.5f, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Fill();
        }
    }
}
