using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentNotifications.uxml. Attach to the same GameObject
    /// as UIManager (it uses RequireComponent(UIDocument) like the other
    /// screen controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and "Mark all read"
    ///  - Builds the notification list at runtime from in-memory data (no
    ///    notifications -> shows the empty state)
    ///  - Keeps the header subtitle in sync with the unread count
    ///  - Applies the purple->pink gradient (matches StudentProfile) to the
    ///    header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetNotifications() so gameplay/session code can push real
    ///    notifications in instead of the placeholder mock data.
    ///
    /// Hook up your real "fetch notifications" / "mark as read" calls where
    /// noted below - e.g. call into your existing PlayerSessionManager /
    /// NotificationService.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentNotificationsController : MonoBehaviour
    {
        public enum NotificationIcon
        {
            Quiz,       // purple book
            Achievement,// green trophy
            Classroom,  // blue people
            System      // orange bell
        }

        public class NotificationEntry
        {
            public string Title;
            public string Message;
            public string TimeAgo;
            public bool IsRead;
            public NotificationIcon Icon;

            public NotificationEntry(string title, string message, string timeAgo, bool isRead, NotificationIcon icon)
            {
                Title = title;
                Message = message;
                TimeAgo = timeAgo;
                IsRead = isRead;
                Icon = icon;
            }
        }

        [Header("Gradient colors (matches StudentProfile: purple -> pink)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f);
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private Label _headerSubtitleLabel;
        private Button _markAllReadButton;

        private VisualElement _emptyState;
        private VisualElement _notificationsList;

        // Placeholder mock data so the screen is demonstrable out of the box.
        // Replace with a call to SetNotifications() once a real backend exists.
        private List<NotificationEntry> _notifications = new List<NotificationEntry>
        {
            new NotificationEntry("Quiz graded", "You scored 90% on \"Skeletal System Basics\".", "2h ago", false, NotificationIcon.Quiz),
            new NotificationEntry("New badge unlocked", "You earned the \"Quiz Master\" badge!", "5h ago", false, NotificationIcon.Achievement),
            new NotificationEntry("Classroom announcement", "Your teacher posted an update in Anatomia.", "1d ago", false, NotificationIcon.Classroom),
            new NotificationEntry("Level up!", "You reached Level 5. Keep it up!", "2d ago", true, NotificationIcon.Achievement),
            new NotificationEntry("Welcome to Anatomia 3D", "Explore the 3D model to get started.", "5d ago", true, NotificationIcon.System),
        };

        private void OnEnable()
        {
            Debug.Log("[StudentNotificationsController] OnEnable called");

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
                Debug.LogError("[StudentNotificationsController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshNotificationsUI();
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
            _markAllReadButton?.UnregisterCallback<ClickEvent>(OnMarkAllReadClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentNotificationsController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");
            _headerSubtitleLabel = _screenRoot.Q<Label>("header-subtitle-label");
            _markAllReadButton = _screenRoot.Q<Button>("mark-all-read-button");

            _emptyState = _screenRoot.Q<VisualElement>("notifications-empty-state");
            _notificationsList = _screenRoot.Q<VisualElement>("notifications-list");

            Debug.Log($"[StudentNotificationsController] Found list: {_notificationsList != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _markAllReadButton?.RegisterCallback<ClickEvent>(OnMarkAllReadClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Replace the notification list. Pass an empty/null list to show the empty state.</summary>
        public void SetNotifications(List<NotificationEntry> notifications)
        {
            _notifications = notifications ?? new List<NotificationEntry>();
            RefreshNotificationsUI();
        }

        // ---------------- Notifications list ----------------

        private void RefreshNotificationsUI()
        {
            int unreadCount = _notifications.FindAll(n => !n.IsRead).Count;

            if (_headerSubtitleLabel != null)
            {
                _headerSubtitleLabel.text = unreadCount > 0
                    ? $"{unreadCount} unread notification{(unreadCount == 1 ? "" : "s")}"
                    : "You're all caught up";
            }

            if (_markAllReadButton != null) _markAllReadButton.SetEnabled(unreadCount > 0);

            bool hasNotifications = _notifications.Count > 0;
            _emptyState?.EnableInClassList("hidden", hasNotifications);
            _notificationsList?.EnableInClassList("hidden", !hasNotifications);

            if (_notificationsList == null) return;

            _notificationsList.Clear();

            foreach (var entry in _notifications)
            {
                _notificationsList.Add(BuildNotificationRow(entry));
            }
        }

        private VisualElement BuildNotificationRow(NotificationEntry entry)
        {
            var row = new VisualElement();
            row.AddToClassList("notification-row");
            if (!entry.IsRead) row.AddToClassList("notification-row-unread");

            var iconBox = new VisualElement();
            iconBox.AddToClassList("notification-icon-box");
            iconBox.AddToClassList(GetIconColorClass(entry.Icon));
            var iconLabel = new Label(GetIconEmoji(entry.Icon));
            iconLabel.AddToClassList("notification-icon-emoji");
            iconBox.Add(iconLabel);

            var textCol = new VisualElement();
            textCol.AddToClassList("notification-text");

            var topRow = new VisualElement();
            topRow.AddToClassList("notification-top-row");
            var titleLabel = new Label(entry.Title);
            titleLabel.AddToClassList("notification-title");
            topRow.Add(titleLabel);
            if (!entry.IsRead)
            {
                var dot = new VisualElement();
                dot.AddToClassList("notification-unread-dot");
                topRow.Add(dot);
            }

            var messageLabel = new Label(entry.Message);
            messageLabel.AddToClassList("notification-message");

            var timeLabel = new Label(entry.TimeAgo);
            timeLabel.AddToClassList("notification-time");

            textCol.Add(topRow);
            textCol.Add(messageLabel);
            textCol.Add(timeLabel);

            row.Add(iconBox);
            row.Add(textCol);

            // Tapping a notification marks it read (a real app might also deep-link
            // to the relevant screen here, e.g. the related quiz or classroom).
            row.RegisterCallback<ClickEvent>(_ =>
            {
                if (entry.IsRead) return;
                entry.IsRead = true;
                RefreshNotificationsUI();
            });

            return row;
        }

        private static string GetIconEmoji(NotificationIcon icon)
        {
            switch (icon)
            {
                case NotificationIcon.Quiz: return "\U0001F4DA";        // 📚
                case NotificationIcon.Achievement: return "\U0001F3C6"; // 🏆
                case NotificationIcon.Classroom: return "\U0001F465";   // 👥
                default: return "\U0001F514";                          // 🔔
            }
        }

        private static string GetIconColorClass(NotificationIcon icon)
        {
            switch (icon)
            {
                case NotificationIcon.Quiz: return "notification-icon-purple";
                case NotificationIcon.Achievement: return "notification-icon-green";
                case NotificationIcon.Classroom: return "notification-icon-blue";
                default: return "notification-icon-orange";
            }
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentNotificationsController] Navigating back to profile");
            UIManager.Instance.ShowStudentProfile();
        }

        private void OnMarkAllReadClicked(ClickEvent evt)
        {
            foreach (var entry in _notifications) entry.IsRead = true;
            RefreshNotificationsUI();

            // TODO: replace with your real call, e.g.:
            // NotificationService.Instance.MarkAllRead();
            Debug.Log("[StudentNotificationsController] Marked all notifications as read.");
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Header Gradient (USS has no linear-gradient) ----------------

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
                name = "StudentNotificationsHeaderGradientTexture"
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
