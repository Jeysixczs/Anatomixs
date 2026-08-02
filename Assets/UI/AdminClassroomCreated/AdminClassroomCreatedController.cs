using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminClassroomCreated.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Applies the pale green->blue gradients (background, checkmark circle,
    ///    code display box) and the saturated green->blue gradient (the
    ///    "Go to Dashboard" button) at runtime
    ///  - Wires up "Copy Code" (copies to the system clipboard and flashes a
    ///    "Copied!" confirmation) and "Go to Dashboard"
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetClassroomData() so UIManager can push the freshly created
    ///    classroom's code/name/student-count in right after showing this
    ///    screen (see UIManager.ShowAdminClassroomCreated()).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminClassroomCreatedController : MonoBehaviour
    {
        [Header("Accent gradient (matches AdminDashboard: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Pale background gradient")]
        [SerializeField] private Color backgroundStart = new Color(0.906f, 0.973f, 0.949f); // pale mint
        [SerializeField] private Color backgroundEnd = new Color(0.918f, 0.949f, 0.984f);   // pale blue

        [Header("Pale code display box gradient")]
        [SerializeField] private Color codeBoxStart = new Color(0.827f, 0.937f, 0.878f);
        [SerializeField] private Color codeBoxEnd = new Color(0.827f, 0.878f, 0.965f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        [Header("Copy confirmation")]
        [SerializeField] private float copyConfirmationSeconds = 1.5f;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _backgroundGradientTexture;
        private Texture2D _checkmarkGradientTexture;
        private Texture2D _codeBoxGradientTexture;
        private Texture2D _buttonGradientTexture;

        private VisualElement _checkmarkCircle;
        private Label _classroomCodeLabel;
        private VisualElement _codeDisplayBox;
        private Button _copyCodeButton;
        private Label _copyCodeLabel;
        private Label _classroomNameValueLabel;
        private Label _studentsValueLabel;
        private Button _goToDashboardButton;

        private AdminDashboardController.ClassroomSummary classroom;
        private string _classroomId = "";

        private void OnEnable()
        {
            Debug.Log("[AdminClassroomCreatedController] OnEnable called");

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
                Debug.LogError("[AdminClassroomCreatedController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            ResetCopyLabel();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            CancelInvoke(nameof(ResetCopyLabel));

            if (_backgroundGradientTexture != null) { Destroy(_backgroundGradientTexture); _backgroundGradientTexture = null; }
            if (_checkmarkGradientTexture != null) { Destroy(_checkmarkGradientTexture); _checkmarkGradientTexture = null; }
            if (_codeBoxGradientTexture != null) { Destroy(_codeBoxGradientTexture); _codeBoxGradientTexture = null; }
            if (_buttonGradientTexture != null) { Destroy(_buttonGradientTexture); _buttonGradientTexture = null; }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _copyCodeButton?.UnregisterCallback<ClickEvent>(OnCopyCodeClicked);
            _goToDashboardButton?.UnregisterCallback<ClickEvent>(OnGoToDashboardClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminClassroomCreatedController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _checkmarkCircle = _screenRoot.Q<VisualElement>("checkmark-circle");
            _classroomCodeLabel = _screenRoot.Q<Label>("classroom-code-label");
            _codeDisplayBox = _screenRoot.Q<VisualElement>("code-display-box");
            _copyCodeButton = _screenRoot.Q<Button>("copy-code-button");
            _copyCodeLabel = _screenRoot.Q<Label>("copy-code-label");
            _classroomNameValueLabel = _screenRoot.Q<Label>("classroom-name-value-label");
            _studentsValueLabel = _screenRoot.Q<Label>("students-value-label");
            _goToDashboardButton = _screenRoot.Q<Button>("go-to-dashboard-button");

            Debug.Log($"[AdminClassroomCreatedController] Found code label: {_classroomCodeLabel != null}, go to dashboard: {_goToDashboardButton != null}");
        }

        private void WireCallbacks()
        {
            _copyCodeButton?.RegisterCallback<ClickEvent>(OnCopyCodeClicked);
            _goToDashboardButton?.RegisterCallback<ClickEvent>(OnGoToDashboardClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push the freshly created classroom's details into the screen. Called
        /// from UIManager.ShowAdminClassroomCreated() - see the note in
        /// AdminCreateClassroomController.OnCreateResult() about adding classroomId as
        /// that method's new leading parameter so it can be forwarded here and on to
        /// ShowAdminClassroomDetail() in OnGoToDashboardClicked() below.</summary>
        public void SetClassroomData(string classroomId, string classroomCode, string classroomName, int studentCount)
        {
            _classroomId = classroomId ?? "";
            if (_classroomCodeLabel != null) _classroomCodeLabel.text = classroomCode;
            if (_classroomNameValueLabel != null) _classroomNameValueLabel.text = classroomName;
            if (_studentsValueLabel != null) _studentsValueLabel.text = studentCount.ToString();
        }

        // ---------------- Button handlers ----------------

        private void OnCopyCodeClicked(ClickEvent evt)
        {
            if (_classroomCodeLabel == null) return;

            GUIUtility.systemCopyBuffer = _classroomCodeLabel.text;
            Debug.Log($"[AdminClassroomCreatedController] Copied classroom code: {_classroomCodeLabel.text}");

            if (_copyCodeLabel != null)
            {
                _copyCodeLabel.text = "Copied!";
                CancelInvoke(nameof(ResetCopyLabel));
                Invoke(nameof(ResetCopyLabel), copyConfirmationSeconds);
            }
        }

        private void ResetCopyLabel()
        {
            if (_copyCodeLabel != null) _copyCodeLabel.text = "Copy Code";
        }

        private void OnGoToDashboardClicked(ClickEvent evt)
        {
            Debug.Log("[AdminClassroomCreatedController] Navigating to admin dashboard");
            //UIManager.Instance.ShowAdminDashboard();
            string classroomName = classroom.Name;
            string classroomCode = classroom.Code;
            int classroomStudentCount = classroom.StudentCount;
            float avgScorePercent = 0f; // a brand new classroom has no quiz attempts yet
            int quizCount = 0;          // and no quizzes published yet

            // NOTE: ShowAdminClassroomDetail now needs a leading classroomId parameter -
            // update its signature in UIManager.cs to match
            // AdminClassroomDetailController.SetClassroomData(classroomId, ...).
            UIManager.Instance.ShowAdminClassroomDetail(_classroomId, classroomName, classroomCode, classroomStudentCount, avgScorePercent, quizCount);

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
            if (_screenRoot != null)
            {
                if (_backgroundGradientTexture != null) Destroy(_backgroundGradientTexture);
                _backgroundGradientTexture = BuildGradientTexture(backgroundStart, backgroundEnd);
                _screenRoot.style.backgroundImage = new StyleBackground(_backgroundGradientTexture);
            }

            if (_checkmarkCircle != null)
            {
                if (_checkmarkGradientTexture != null) Destroy(_checkmarkGradientTexture);
                _checkmarkGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _checkmarkCircle.style.backgroundImage = new StyleBackground(_checkmarkGradientTexture);
            }

            if (_codeDisplayBox != null)
            {
                if (_codeBoxGradientTexture != null) Destroy(_codeBoxGradientTexture);
                _codeBoxGradientTexture = BuildGradientTexture(codeBoxStart, codeBoxEnd);
                _codeDisplayBox.style.backgroundImage = new StyleBackground(_codeBoxGradientTexture);
            }

            if (_goToDashboardButton != null)
            {
                if (_buttonGradientTexture != null) Destroy(_buttonGradientTexture);
                _buttonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _goToDashboardButton.style.backgroundImage = new StyleBackground(_buttonGradientTexture);
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