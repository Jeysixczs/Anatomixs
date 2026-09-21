using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Keeps a screen's content on-screen WITHOUT scrolling. If <c>content</c> is taller
    /// (or wider) than the space available in <c>viewport</c>, it is scaled down so the
    /// whole thing fits, and it is never scaled up. Because the content is centered by
    /// the viewport's flex layout and UI Toolkit scales around the element's center, the
    /// shrunken content stays centered.
    ///
    /// Same drop-in style as LoadingOverlay: a plain C# helper, not a MonoBehaviour.
    ///   _fit = new FitToScreen(root.Q("scroll-view"), root.Q("content-wrapper"));
    ///   ... and in OnDisable: _fit?.Dispose();
    ///
    /// Screens are rebuilt on every UIManager.ShowScreen(), so create a fresh instance
    /// each time the controller is enabled.
    ///
    /// Requirements on the USS side: the content element must keep its natural size
    /// (flex-shrink: 0, flex-grow: 0) and the viewport must center it. Scaling is a
    /// transform, so it never changes layout and cannot cause a resize feedback loop.
    /// </summary>
    public class FitToScreen : IDisposable
    {
        private readonly VisualElement _viewport;
        private readonly VisualElement _content;
        private readonly float _minScale;
        private float _appliedScale = -1f;

        /// <param name="minScale">Lower bound so content never becomes unreadably small.</param>
        public FitToScreen(VisualElement viewport, VisualElement content, float minScale = 0.35f)
        {
            _viewport = viewport;
            _content = content;
            _minScale = Mathf.Clamp(minScale, 0.05f, 1f);

            if (_viewport == null || _content == null) return;

            // Re-fit whenever either the available space or the content's own size changes
            // (rotation, split-screen, an error message appearing, compact class toggling...).
            _viewport.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _content.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        private void OnGeometryChanged(GeometryChangedEvent evt) => Refit();

        /// <summary>Recomputes and applies the scale. Safe to call any time.</summary>
        public void Refit()
        {
            if (_viewport == null || _content == null) return;

            float availW = _viewport.contentRect.width;
            float availH = _viewport.contentRect.height;

            // layout.* is the unscaled layout size (transforms don't affect it).
            float contentW = _content.layout.width;
            float contentH = _content.layout.height;

            if (availW <= 0f || availH <= 0f || contentW <= 0f || contentH <= 0f) return;

            float scale = Mathf.Min(1f, availW / contentW, availH / contentH);
            scale = Mathf.Clamp(scale, _minScale, 1f);

            if (Mathf.Abs(scale - _appliedScale) < 0.001f) return;
            _appliedScale = scale;

            _content.style.scale = new StyleScale(new Scale(new Vector3(scale, scale, 1f)));
        }

        public void Dispose()
        {
            _viewport?.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _content?.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }
    }
}
