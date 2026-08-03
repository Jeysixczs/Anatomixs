using System.Linq;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminProfile.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the Account menu buttons
    ///  - Loads real data from AdminAuthService.CurrentAdmin via
    ///    RefreshFromBackend() - classroomCount/quizzesCreated come straight
    ///    from the admin doc (kept accurate by AdminClassroomService/QuizService
    ///    incrementing them), but studentCount is computed by summing each
    ///    classroom's own studentCount instead, since admins/{uid}.studentCount
    ///    itself is never incremented anywhere and would just read 0
    ///  - Applies the green->blue gradient (matches AdminDashboard) to the
    ///    header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetProfileData() so other code can still push values in
    ///    directly instead of going through RefreshFromBackend().
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminProfileController : MonoBehaviour
    {
        [Header("Gradient colors (matches AdminDashboard: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private VisualElement _classroomsBackground;
        private VisualElement _studentsBackground;
        private VisualElement _quizzesBackground;
        private VisualElement _editprofileBackground;
        private VisualElement _aboutBackground;
        private VisualElement _logoutBackground;
        private Button _backButton;

        private VisualElement _avatar;
        private Label _avatarInitialsLabel;
        private Label _teacherNameLabel;
        private Label _teacherEmailLabel;
        private Label _roleBadgeLabel;

        private Label _classroomsValueLabel;
        private Label _studentsValueLabel;
        private Label _quizzesValueLabel;

        private Button _editProfileButton;
        private Button _aboutButton;
        private Button _logoutButton;

        private void OnEnable()
        {
            Debug.Log("[AdminProfileController] OnEnable called");

            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            // Get the root from UIManager's document (shared across all screens)
            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null)
                {
                    _root = uiDocument.rootVisualElement;
                }
            }

            // Fallback: use this component's own document
            if (_root == null && _document != null)
            {
                _root = _document.rootVisualElement;
            }

            if (_root == null)
            {
                Debug.LogError("[AdminProfileController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshFromBackend();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _editProfileButton?.UnregisterCallback<ClickEvent>(OnEditProfileClicked);
            _aboutButton?.UnregisterCallback<ClickEvent>(OnAboutClicked);
            _logoutButton?.UnregisterCallback<ClickEvent>(OnLogoutClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminProfileController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _avatar = _screenRoot.Q<VisualElement>("avatar");
            _avatarInitialsLabel = _screenRoot.Q<Label>("avatar-initials-label");
            _teacherNameLabel = _screenRoot.Q<Label>("teacher-name-label");
            _teacherEmailLabel = _screenRoot.Q<Label>("teacher-email-label");
            _roleBadgeLabel = _screenRoot.Q<Label>("role-badge-label");

            _classroomsValueLabel = _screenRoot.Q<Label>("classrooms-value-label");
            _studentsValueLabel = _screenRoot.Q<Label>("students-value-label");
            _quizzesValueLabel = _screenRoot.Q<Label>("quizzes-value-label");

            _editProfileButton = _screenRoot.Q<Button>("edit-profile-button");
            _aboutButton = _screenRoot.Q<Button>("about-button");
            _logoutButton = _screenRoot.Q<Button>("logout-button");

            _classroomsBackground = _screenRoot.Q<VisualElement>("classrooms-background");
            _studentsBackground = _screenRoot.Q<VisualElement>("students-background");
            _quizzesBackground = _screenRoot.Q<VisualElement>("quizzes-background");
            _editprofileBackground = _screenRoot.Q<VisualElement>("editprofile-background");
            _aboutBackground = _screenRoot.Q<VisualElement>("about-background");
            _logoutBackground = _screenRoot.Q<VisualElement>("logout-background");


            Debug.Log($"[AdminProfileController] Found back button: {_backButton != null}, logout: {_logoutButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _editProfileButton?.RegisterCallback<ClickEvent>(OnEditProfileClicked);
            _aboutButton?.RegisterCallback<ClickEvent>(OnAboutClicked);
            _logoutButton?.RegisterCallback<ClickEvent>(OnLogoutClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        // Cached so classroomCount/quizzesCreated (from the admin doc) and
        // studentCount (summed separately from each classroom) can each update
        // independently while still calling SetProfileData with all three.
        private string _teacherName = "";
        private string _teacherEmail = "";
        private int _classroomCount;
        private int _quizzesCreated;
        private int _studentCount;

        /// <summary>Loads AdminAuthService.CurrentAdmin into the header summary
        /// card and stat row, then (a) re-fetches the admin doc so
        /// classroomCount/quizzesCreated reflect anything changed elsewhere this
        /// session, and (b) sums studentCount live across this admin's classrooms
        /// since the admin doc's own studentCount field is never kept in sync.</summary>
        public void RefreshFromBackend()
        {
            var admin = AdminAuthService.Instance != null ? AdminAuthService.Instance.CurrentAdmin : null;
            if (admin == null)
            {
                Debug.LogWarning("[AdminProfileController] No admin signed in - leaving profile fields as-is.");
                return;
            }

            ApplyAdminProfile(admin);

            AdminAuthService.Instance.RefreshCurrentAdmin(success =>
            {
                if (success) ApplyAdminProfile(AdminAuthService.Instance.CurrentAdmin);
            });

            if (AdminClassroomService.Instance != null)
            {
                AdminClassroomService.Instance.FetchMyClassrooms(classrooms =>
                {
                    _studentCount = classrooms.Sum(c => c.StudentCount);
                    SetProfileData(_teacherName, _teacherEmail, _classroomCount, _studentCount, _quizzesCreated);
                });
            }
        }

        private void ApplyAdminProfile(AdminAuthService.AdminProfile admin)
        {
            _teacherName = admin.FullName;
            _teacherEmail = admin.Email;
            _classroomCount = admin.ClassroomCount;
            _quizzesCreated = admin.QuizzesCreated;
            SetProfileData(_teacherName, _teacherEmail, _classroomCount, _studentCount, _quizzesCreated);
        }

        /// <summary>Push real teacher data into the header summary card and stat row.</summary>
        public void SetProfileData(
            string teacherName,
            string email,
            int classroomCount,
            int studentCount,
            int quizzesCreated)
        {
            if (_teacherNameLabel != null) _teacherNameLabel.text = teacherName;
            if (_teacherEmailLabel != null) _teacherEmailLabel.text = email;
            if (_roleBadgeLabel != null) _roleBadgeLabel.text = "Teacher";

            if (_classroomsValueLabel != null) _classroomsValueLabel.text = classroomCount.ToString();
            if (_studentsValueLabel != null) _studentsValueLabel.text = studentCount.ToString();
            if (_quizzesValueLabel != null) _quizzesValueLabel.text = quizzesCreated.ToString();

            if (_avatarInitialsLabel != null) _avatarInitialsLabel.text = GetInitials(teacherName);
        }

        private string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "?";

            var parts = fullName.Trim().Split(' ');
            if (parts.Length == 1) return parts[0].Substring(0, Mathf.Min(2, parts[0].Length)).ToUpper();

            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[AdminProfileController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        private void OnEditProfileClicked(ClickEvent evt)
        {
            Debug.Log("[AdminProfileController] Edit Profile tapped.");
            UIManager.Instance.ShowAdminEditProfile(_teacherNameLabel?.text, _teacherEmailLabel?.text);

        }


        private void OnAboutClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowAboutAnatomia();
        }

        private void OnLogoutClicked(ClickEvent evt)
        {
            Debug.Log("[AdminProfileController] Log Out tapped.");
            AdminAuthService.Instance?.LogoutAdmin();
            UIManager.Instance.ShowAdminLogin();
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradient (USS has no linear-gradient) ----------------

        private void ApplyHeaderGradient()
        {
            if (_header == null) return;

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
            }

            _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
            _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            _classroomsBackground.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            _studentsBackground.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            _quizzesBackground.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            _editprofileBackground.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            _aboutBackground.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            _logoutBackground.style.backgroundImage = new StyleBackground(_headerGradientTexture);

        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminProfileHeaderGradientTexture"
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