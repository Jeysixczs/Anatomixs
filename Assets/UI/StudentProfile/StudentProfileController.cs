using System.Collections;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentProfile.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the account/support menu buttons
    ///  - Loads real data from PlayerSessionManager.CurrentStudent via
    ///    RefreshFromBackend() (paints the cached copy immediately, then
    ///    refreshes it from Firestore since gameplay code doesn't keep that
    ///    cache in sync after a quiz - see PlayerSessionManager.RefreshCurrentStudent)
    ///  - Applies the purple->pink gradient to the header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetProfileData() so other code can still push values in
    ///    directly instead of going through RefreshFromBackend().
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentProfileController : MonoBehaviour
    {
        [Header("Gradient colors (matches the other screens)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        private VisualElement _avatar;

        // Cloudinary avatar image state. AvatarUrl is a Cloudinary secure_url
        // (set on the Edit Profile screen) - _loadedAvatarUrl/_avatarTexture
        // let ApplyStudent skip re-downloading the same image on every
        // profile refresh (e.g. the OnStudentProfileChanged echo after a quiz
        // updates points), and only kick off a new request when the URL
        // actually changes.
        private string _loadedAvatarUrl;
        private Texture2D _avatarTexture;
        private Coroutine _avatarLoadRoutine;

        private VisualElement _quizzesBackground;
        private VisualElement _levelBackground;
        private VisualElement _pointsBackground;
        private VisualElement _editprofileBackground;
        private VisualElement _notificationBackground;
        private VisualElement _aboutBackground;
        private VisualElement _logoutBackground;
        private Label _avatarInitialsLabel;
        private Label _studentNameLabel;
        private Label _studentEmailLabel;
        private Label _levelBadgeLabel;

        private Label _quizzesValueLabel;
        private Label _levelValueLabel;
        private Label _pointsValueLabel;

        private Button _editProfileButton;
        private Button _notificationsButton;
        private Button _aboutButton;
        private Button _logoutButton;

        private void OnEnable()
        {
            Debug.Log("[StudentProfileController] OnEnable called");

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
                Debug.LogError("[StudentProfileController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            // Paint whatever's already cached immediately (no network wait), then
            // stay subscribed so a later change - local or from the real-time
            // listener - repaints this screen without needing to be re-opened.
            RefreshFromBackend();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
                PlayerSessionManager.Instance.OnStudentProfileChanged += OnStudentProfileChanged;
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
            }

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
                _avatarLoadRoutine = null;
            }

            if (_avatarTexture != null)
            {
                Destroy(_avatarTexture);
                _avatarTexture = null;
            }
            _loadedAvatarUrl = null;
        }

        private void OnStudentProfileChanged(PlayerSessionManager.StudentProfile student)
        {
            ApplyStudent(student);
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _editProfileButton?.UnregisterCallback<ClickEvent>(OnEditProfileClicked);
            _notificationsButton?.UnregisterCallback<ClickEvent>(OnNotificationsClicked);
            _aboutButton?.UnregisterCallback<ClickEvent>(OnAboutClicked);
            _logoutButton?.UnregisterCallback<ClickEvent>(OnLogoutClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentProfileController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _avatar = _screenRoot.Q<VisualElement>("avatar");
            _avatarInitialsLabel = _screenRoot.Q<Label>("avatar-initials-label");
            _studentNameLabel = _screenRoot.Q<Label>("student-name-label");
            _studentEmailLabel = _screenRoot.Q<Label>("student-email-label");
            _levelBadgeLabel = _screenRoot.Q<Label>("level-badge-label");

            _quizzesValueLabel = _screenRoot.Q<Label>("quizzes-value-label");
            _levelValueLabel = _screenRoot.Q<Label>("level-value-label");
            _pointsValueLabel = _screenRoot.Q<Label>("points-value-label");

            _editProfileButton = _screenRoot.Q<Button>("edit-profile-button");
            _notificationsButton = _screenRoot.Q<Button>("notifications-button");
            _aboutButton = _screenRoot.Q<Button>("about-button");
            _logoutButton = _screenRoot.Q<Button>("logout-button");

            _quizzesBackground = _screenRoot.Q<VisualElement>("quizzes-background");
            _levelBackground = _screenRoot.Q<VisualElement>("level-background");
            _pointsBackground = _screenRoot.Q<VisualElement>("points-background");
            _editprofileBackground = _screenRoot.Q<VisualElement>("editprofile-background");
            _notificationBackground = _screenRoot.Q<VisualElement>("notifications-background");
            _aboutBackground = _screenRoot.Q<VisualElement>("about-background");
            _logoutBackground = _screenRoot.Q<VisualElement>("logout-background");


            Debug.Log($"[StudentProfileController] Found back button: {_backButton != null}, logout: {_logoutButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _editProfileButton?.RegisterCallback<ClickEvent>(OnEditProfileClicked);
            _notificationsButton?.RegisterCallback<ClickEvent>(OnNotificationsClicked);
            _aboutButton?.RegisterCallback<ClickEvent>(OnAboutClicked);
            _logoutButton?.RegisterCallback<ClickEvent>(OnLogoutClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Loads PlayerSessionManager.CurrentStudent into the header summary
        /// card and stat row. Reads straight from the cache - no Firestore call here
        /// anymore. Gameplay/profile/classroom code keeps CurrentStudent in sync at
        /// the point each of those actually changes the student doc (see
        /// PlayerSessionManager.ApplyQuizAttemptResult and .WriteFullName), so by the
        /// time this screen opens the cached copy is already accurate.</summary>
        public void RefreshFromBackend()
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null)
            {
                Debug.LogWarning("[StudentProfileController] No student signed in - leaving profile fields as-is.");
                return;
            }

            ApplyStudent(student);
        }

        private void ApplyStudent(PlayerSessionManager.StudentProfile student)
        {
            SetProfileData(student.FullName, student.Email, student.Level, student.TotalPoints, student.QuizzesCompleted, student.AvatarUrl);
        }

        /// <summary>Push real student data into the header summary card and stat row.
        /// avatarUrl is optional (older/never-set profiles have none) - pass null or
        /// empty to fall back to the initials label.</summary>
        public void SetProfileData(
            string studentName,
            string email,
            int level,
            int totalPoints,
            int quizzesCompleted,
            string avatarUrl = null)
        {
            if (_studentNameLabel != null) _studentNameLabel.text = studentName;
            if (_studentEmailLabel != null) _studentEmailLabel.text = email;
            if (_levelBadgeLabel != null) _levelBadgeLabel.text = $"Level {level} \u2022 {totalPoints:N0} pts";

            if (_quizzesValueLabel != null) _quizzesValueLabel.text = quizzesCompleted.ToString();
            if (_levelValueLabel != null) _levelValueLabel.text = level.ToString();
            if (_pointsValueLabel != null) _pointsValueLabel.text = totalPoints.ToString();

            if (_avatarInitialsLabel != null) _avatarInitialsLabel.text = GetInitials(studentName);

            ApplyAvatar(avatarUrl);
        }

        // ---------------- Avatar (Cloudinary) ----------------

        /// <summary>Shows the student's Cloudinary avatar image in #avatar if a URL
        /// is set, otherwise falls back to the initials label (the pre-existing
        /// behavior). Skips re-downloading when avatarUrl hasn't actually changed
        /// since the last successful load, since ApplyStudent/SetProfileData can be
        /// called repeatedly (RefreshFromBackend, the real-time listener echo,
        /// ApplyQuizAttemptResult's optimistic update).</summary>
        private void ApplyAvatar(string avatarUrl)
        {
            if (_avatar == null) return;

            if (string.IsNullOrEmpty(avatarUrl))
            {
                ShowInitialsAvatar();
                return;
            }

            if (avatarUrl == _loadedAvatarUrl && _avatarTexture != null)
            {
                // Already showing this exact image - nothing to do.
                return;
            }

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
            }
            _avatarLoadRoutine = StartCoroutine(LoadAvatarImage(avatarUrl));
        }

        private void ShowInitialsAvatar()
        {
            _avatar.style.backgroundImage = StyleKeyword.Null;
            if (_avatarInitialsLabel != null)
                _avatarInitialsLabel.style.display = DisplayStyle.Flex;

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
                _avatarLoadRoutine = null;
            }

            if (_avatarTexture != null)
            {
                Destroy(_avatarTexture);
                _avatarTexture = null;
            }
            _loadedAvatarUrl = null;
        }

        private IEnumerator LoadAvatarImage(string avatarUrl)
        {
            using (var request = UnityWebRequestTexture.GetTexture(avatarUrl))
            {
                yield return request.SendWebRequest();

                _avatarLoadRoutine = null;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[StudentProfileController] Could not load Cloudinary avatar '{avatarUrl}': {request.error}");
                    // Leave whatever's currently showing (initials, most likely)
                    // rather than blanking the avatar out over a transient network hiccup.
                    yield break;
                }

                if (_avatarTexture != null)
                {
                    Destroy(_avatarTexture);
                }

                _avatarTexture = DownloadHandlerTexture.GetContent(request);
                _loadedAvatarUrl = avatarUrl;

                if (_avatar == null) yield break; // screen may have been disabled while the request was in flight.

                _avatar.style.backgroundImage = new StyleBackground(_avatarTexture);
                ApplyCoverBackground(_avatar);

                // Image fills the circle now - the initials fallback underneath
                // would otherwise show through any transparent corners.
                if (_avatarInitialsLabel != null)
                    _avatarInitialsLabel.style.display = DisplayStyle.None;
            }
        }

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

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentProfileController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnEditProfileClicked(ClickEvent evt)
        {
            Debug.Log("[StudentProfileController] Edit Profile tapped.");

            UIManager.Instance.ShowStudentEditProfile(_studentNameLabel?.text, _studentEmailLabel?.text);
        }

        private void OnNotificationsClicked(ClickEvent evt)
        {
            // TODO: navigate to Notification settings.
            Debug.Log("[StudentProfileController] Notifications tapped.");

            UIManager.Instance.ShowStudentNotifications(UIManager.Instance.ShowStudentProfile);
        }

        private void OnAboutClicked(ClickEvent evt)
        {

            UIManager.Instance.ShowAboutAnatomia();
        }

        private void OnLogoutClicked(ClickEvent evt)
        {
            Debug.Log("[StudentProfileController] Log Out tapped.");
            PlayerSessionManager.Instance?.LogoutStudent();
            UIManager.Instance.ShowStudentLogin();
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
           
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "ProfileHeaderGradientTexture"
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