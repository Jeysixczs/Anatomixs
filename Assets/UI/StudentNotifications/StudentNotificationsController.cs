using System;
using System.Collections.Generic;
using Anatomia3D.Backend;
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
    ///  - Loads notifications from ClassroomService.FetchNotifications() - these
    ///    are teacher announcements merged across every classroom the student is
    ///    enrolled in (no separate "notifications" collection - see that method's
    ///    doc comment), and shows the empty state when there are none
    ///  - Keeps the header subtitle in sync with the unread count
    ///  - Applies the purple->pink gradient (matches StudentProfile) to the
    ///    header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetNotifications() so other code (e.g. push-notification /
    ///    deep-link handling) can inject entries directly instead of going
    ///    through RefreshFromBackend().
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
            /// <summary>Which classroom this came from (empty for entries not tied
            /// to a classroom, e.g. injected via SetNotifications()). Shown next to
            /// TimeAgo since one student can belong to several classrooms.</summary>
            public string ClassroomName;

            public NotificationEntry(string title, string message, string timeAgo, bool isRead, NotificationIcon icon, string classroomName = "")
            {
                Title = title;
                Message = message;
                TimeAgo = timeAgo;
                IsRead = isRead;
                Icon = icon;
                ClassroomName = classroomName;
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

        // Populated by RefreshFromBackend() (ClassroomService.FetchNotifications).
        // Starts empty so the empty state shows correctly if the fetch is slow
        // or the student isn't in any classrooms yet.
        private List<NotificationEntry> _notifications = new List<NotificationEntry>();

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

            // Show whatever we already have (empty on first open) immediately,
            // then replace it once the live fetch below comes back.
            RefreshNotificationsUI();
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

        /// <summary>Loads teacher announcements from every classroom the student is
        /// enrolled in via ClassroomService.FetchNotifications() and rebuilds the
        /// list. Safe to call any time (e.g. pull-to-refresh) since it always
        /// replaces _notifications wholesale rather than diffing.</summary>
        public void RefreshFromBackend()
        {
            if (ClassroomService.Instance == null)
            {
                Debug.LogWarning("[StudentNotificationsController] ClassroomService.Instance is null - showing empty state.");
                SetNotifications(new List<NotificationEntry>());
                return;
            }

            ClassroomService.Instance.FetchNotifications(records =>
            {
                var entries = records.ConvertAll(r => new NotificationEntry(
                    string.IsNullOrEmpty(r.Title) ? "New announcement" : r.Title,
                    r.Body,
                    FormatTimeAgo(r.CreatedAt.ToDateTime()),
                    r.IsRead,
                    NotificationIcon.Classroom,
                    r.ClassroomName));

                SetNotifications(entries);
            });
        }

        /// <summary>Converts a UTC timestamp into a short relative label ("2h ago",
        /// "3d ago", etc.) for the notification row.</summary>
        private static string FormatTimeAgo(DateTime utcTime)
        {
            var elapsed = DateTime.UtcNow - utcTime;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            if (elapsed.TotalMinutes < 1) return "Just now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
            if (elapsed.TotalDays < 30) return $"{(int)(elapsed.TotalDays / 7)}w ago";
            return $"{(int)(elapsed.TotalDays / 30)}mo ago";
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

            string timeText = string.IsNullOrEmpty(entry.ClassroomName)
                ? entry.TimeAgo
                : $"{entry.ClassroomName} • {entry.TimeAgo}";
            var timeLabel = new Label(timeText);
            timeLabel.AddToClassList("notification-time");

            textCol.Add(topRow);
            textCol.Add(messageLabel);
            textCol.Add(timeLabel);

            row.Add(iconBox);
            row.Add(textCol);

            // Tapping a notification marks it read for this session (a real app
            // might also deep-link to the relevant screen here, e.g. the classroom
            // that posted it). This is local-only: read state isn't tracked per
            // notification server-side, only as a single "read up to" cursor (see
            // ClassroomService.MarkAllNotificationsRead) - re-opening this screen
            // re-derives IsRead from that cursor, so an individual tap won't persist
            // across sessions until "Mark all read" is used.
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
            // Optimistic UI update - flip everything to read immediately...
            foreach (var entry in _notifications) entry.IsRead = true;
            RefreshNotificationsUI();

            // ...then persist the "read up to now" cursor so it survives a
            // re-open/sign-out. If this fails, the next RefreshFromBackend() call
            // (e.g. next time this screen opens) will correctly show unread items
            // again rather than silently losing the failure.
            if (ClassroomService.Instance == null) return;

            ClassroomService.Instance.MarkAllNotificationsRead(success =>
            {
                if (!success)
                {
                    Debug.LogWarning("[StudentNotificationsController] Could not persist mark-all-read; will re-sync next time this screen opens.");
                }
            });
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