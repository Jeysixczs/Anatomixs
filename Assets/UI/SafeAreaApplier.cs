using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Applies the device safe area (notch, punch-hole, status bar, home
    /// indicator) as padding on a UI Toolkit element. UIDocument has no
    /// built-in safe-area support, unlike Unity's Canvas + Screen.safeArea
    /// recipes for uGUI, so this bridges that gap manually.
    ///
    /// Usage:
    ///  - Drop this on the same GameObject as your UIDocument.
    ///  - Set targetElementName to whatever should be inset. Don't target
    ///    screen-root if that element also paints a full-bleed background
    ///    color you want to run under the notch/status bar (e.g. the
    ///    Dashboard header) - target a child row instead so the color
    ///    still bleeds edge-to-edge while the padding only pushes the
    ///    icons/text inward. For the login/signup screens (flat background,
    ///    nothing needs to bleed under the notch) targeting screen-root
    ///    directly is fine.
    ///  - Toggle applyTop/Bottom/Left/Right per instance - e.g. on the
    ///    Dashboard you'd put one instance on "header-top-row" with only
    ///    applyTop checked, and optionally another on "content-wrapper"
    ///    with only applyBottom checked for the home-indicator area.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class SafeAreaApplier : MonoBehaviour
    {
        [SerializeField] private string targetElementName = "screen-root";

        [Header("Which edges to inset")]
        [SerializeField] private bool applyTop = true;
        [SerializeField] private bool applyBottom = true;
        [SerializeField] private bool applyLeft = true;
        [SerializeField] private bool applyRight = true;

        private UIDocument _document;
        private VisualElement _target;

        private Rect _lastSafeArea;
        private Vector2Int _lastScreenSize;

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            var root = _document.rootVisualElement;

            _target = string.IsNullOrEmpty(targetElementName)
                ? root
                : root.Q<VisualElement>(targetElementName) ?? root;

            // Re-apply once the panel has actually laid out, since panel
            // space isn't reliable before the first geometry pass.
            _target.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            Apply();
        }

        private void OnDisable()
        {
            _target?.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        private void OnGeometryChanged(GeometryChangedEvent evt) => Apply();

        private void Update()
        {
            // Cheap early-out: only recompute on real change (device
            // rotation, split-screen resize, foldable unfold, etc.)
            if (Screen.safeArea != _lastSafeArea ||
                Screen.width != _lastScreenSize.x ||
                Screen.height != _lastScreenSize.y)
            {
                Apply();
            }
        }

        private void Apply()
        {
            var panel = _target?.panel;
            if (panel == null) return;

            Rect safeArea = Screen.safeArea;
            _lastSafeArea = safeArea;
            _lastScreenSize = new Vector2Int(Screen.width, Screen.height);

            // Screen space (bottom-left origin) -> UI Toolkit panel space
            // (top-left origin, already scaled for the reference resolution).
            Vector2 panelOrigin = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(0, Screen.height));
            Vector2 panelTopLeftInset = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(safeArea.xMin, Screen.height - safeArea.yMax));
            Vector2 panelBottomRightInset = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(safeArea.xMax, Screen.height - safeArea.yMin));
            Vector2 panelFullSize = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(Screen.width, 0));

            float leftInset = panelTopLeftInset.x - panelOrigin.x;
            float topInset = panelTopLeftInset.y - panelOrigin.y;
            float rightInset = panelFullSize.x - panelBottomRightInset.x;
            float bottomInset = panelFullSize.y - panelBottomRightInset.y;

            _target.style.paddingLeft = applyLeft ? leftInset : 0f;
            _target.style.paddingTop = applyTop ? topInset : 0f;
            _target.style.paddingRight = applyRight ? rightInset : 0f;
            _target.style.paddingBottom = applyBottom ? bottomInset : 0f;
        }
    }
}
