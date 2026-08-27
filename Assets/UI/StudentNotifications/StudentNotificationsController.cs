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
    ///  - Live-updates from ClassroomService.ListenToNotifications() - these are
    ///    teacher announcements merged across every classroom the student is
    ///    enrolled in (no separate "notifications" collection - see that method's
    ///    doc comment). A new announcement, a newly-joined classroom, or
    ///    MarkAllNotificationsRead() all repaint this screen automatically, with
    ///    no re-fetch needed on re-open; shows the empty state when there are none
    ///  - Diffs incoming notifications against already-built rows (keyed by
    ///    NotificationEntry.Id) so an update patches labels in place instead of
    ///    tearing down and rebuilding the whole list
    ///  - Keeps the header subtitle in sync with the unread count
    ///  - Applies the purple->pink gradient (matches StudentProfile) to the
    ///    header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetNotifications() so other code (e.g. push-notification /
    ///    deep-link handling) can inject entries directly instead of going
    ///    through the live listener.
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
            /// <summary>Stable identity for this row - the backing announcementId when
            /// this came from ClassroomService, or an auto-generated one for entries
            /// injected directly via SetNotifications(). Lets RefreshNotificationsUI()
            /// patch an existing row in place instead of destroying/recreating it.</summary>
            public string Id;
            public string Title;
            public string Message;
            public string TimeAgo;
            public bool IsRead;
            public NotificationIcon Icon;
            /// <summary>Which classroom this came from (empty for entries not tied
            /// to a classroom, e.g. injected via SetNotifications()). Shown next to
            /// TimeAgo since one student can belong to several classrooms.</summary>
            public string ClassroomName;

            public NotificationEntry(string title, string message, string timeAgo, bool isRead, NotificationIcon icon, string classroomName = "", string id = null)
            {
                Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
                Title = title;
                Message = message;
                TimeAgo = timeAgo;
                IsRead = isRead;
                Icon = icon;
                ClassroomName = classroomName;
            }
        }

        /// <summary>Element refs for one already-built notification row, keyed by
        /// NotificationEntry.Id, so a later update patches labels/classes in place
        /// instead of tearing down and rebuilding the row's GameObjects.</summary>
        private class NotificationRowRefs
        {
            public VisualElement Row;
            public VisualElement IconBox;
            public Label IconLabel;
            public Label TitleLabel;
            public VisualElement UnreadDot;
            public Label MessageLabel;
            public Label TimeLabel;
            public string LastIconColorClass;

            /// <summary>Current entry this row displays - read by the row's click
            /// handler (registered once in BuildNotificationRow) so the handler
            /// always acts on the latest entry without needing to be re-registered
            /// on every update.</summary>
            public NotificationEntry LastEntry;
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

        // Populated by OnNotificationsFetched() (ClassroomService.ListenToNotifications).
        // Starts empty so the empty state shows correctly if the listener is slow
        // to attach or the student isn't in any classrooms yet.
        private List<NotificationEntry> _notifications = new List<NotificationEntry>();

        /// <summary>Live subscription started in OnEnable, stopped in OnDisable - see
        /// ClassroomService.ListenToNotifications. Replaces the old "fetch once per
        /// screen visit" call: a teacher posting an announcement, or this student
        /// joining another classroom, now updates this screen automatically.</summary>
        private ClassroomService.NotificationsSubscription _notificationsSubscription;

        /// <summary>Rows built so far, keyed by NotificationEntry.Id - lets
        /// RefreshNotificationsUI() patch existing rows instead of clearing and
        /// rebuilding the whole list on every update.</summary>
        private readonly Dictionary<string, NotificationRowRefs> _rowsById = new Dictionary<string, NotificationRowRefs>();

        /// <summary>Ids the student has tapped read locally this session. The backend
        /// only tracks a single "read up to" cursor (see ClassroomService.
        /// MarkAllNotificationsRead), not per-notification state, so a tap needs to
        /// survive the next live update from the listener - otherwise a new
        /// announcement arriving elsewhere would rebuild the merged list and silently
        /// revert the tap. Cleared implicitly once the server cursor catches up (a
        /// later Recompute() will already report IsRead=true for it anyway).</summary>
        private readonly HashSet<string> _locallyReadIds = new HashSet<string>();

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

            // Show whatever we already have (empty on first open, or last session's
            // data if this screen was re-enabled) immediately, then let the listener
            // patch it in place as real data arrives.
            RefreshNotificationsUI();
            StartNotificationsListener();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            StopNotificationsListener();

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }
        }

        // ---------------- Live data ----------------

        private void StartNotificationsListener()
        {
            StopNotificationsListener();

            bool sessionReady = PlayerSessionManager.Instance != null && PlayerSessionManager.Instance.IsLoggedIn;

            if (ClassroomService.Instance != null && sessionReady)
            {
                _notificationsSubscription = ClassroomService.Instance.ListenToNotifications(OnNotificationsFetched);
                return;
            }

            Debug.LogWarning("[StudentNotificationsController] ClassroomService not ready or no student signed in - showing empty state.");
            SetNotifications(new List<NotificationEntry>());
        }

        private void StopNotificationsListener()
        {
            _notificationsSubscription?.Stop();
            _notificationsSubscription = null;
        }

        private void OnNotificationsFetched(List<ClassroomService.NotificationRecord> records)
        {
            var entries = records.ConvertAll(r =>
            {
                // A locally-tapped row stays read even if this update was triggered by
                // something unrelated (e.g. a new announcement in another classroom) -
                // see _locallyReadIds' doc comment.
                bool isRead = r.IsRead || _locallyReadIds.Contains(r.AnnouncementId);

                return new NotificationEntry(
                    string.IsNullOrEmpty(r.Title) ? "New announcement" : r.Title,
                    r.Body,
                    FormatTimeAgo(r.CreatedAt.ToDateTime()),
                    isRead,
                    NotificationIcon.Classroom,
                    r.ClassroomName,
                    r.AnnouncementId);
            });

            SetNotifications(entries);
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

            var incomingIds = new HashSet<string>();

            for (int i = 0; i < _notifications.Count; i++)
            {
                var entry = _notifications[i];
                incomingIds.Add(entry.Id);

                if (_rowsById.TryGetValue(entry.Id, out var refs))
                {
                    UpdateNotificationRow(refs, entry);
                }
                else
                {
                    refs = BuildNotificationRow(entry);
                    _rowsById[entry.Id] = refs;
                }

                // Insert() on an already-parented element just moves it, so rows
                // whose position didn't change aren't touched at all.
                if (_notificationsList.IndexOf(refs.Row) != i)
                {
                    _notificationsList.Insert(i, refs.Row);
                }
            }

            // Drop rows for notifications no longer in the merged list (e.g. a
            // teacher deleted the announcement, or maxPerClassroom pushed it out).
            List<string> staleIds = null;
            foreach (var id in _rowsById.Keys)
            {
                if (!incomingIds.Contains(id)) (staleIds ??= new List<string>()).Add(id);
            }

            if (staleIds != null)
            {
                foreach (var id in staleIds)
                {
                    _rowsById[id].Row.RemoveFromHierarchy();
                    _rowsById.Remove(id);
                }
            }
        }

        private NotificationRowRefs BuildNotificationRow(NotificationEntry entry)
        {
            var refs = new NotificationRowRefs();

            var row = new VisualElement();
            row.AddToClassList("notification-row");
            refs.Row = row;

            var iconBox = new VisualElement();
            iconBox.AddToClassList("notification-icon-box");
            var iconLabel = new Label();
            iconLabel.AddToClassList("notification-icon-emoji");
            iconBox.Add(iconLabel);
            refs.IconBox = iconBox;
            refs.IconLabel = iconLabel;

            var textCol = new VisualElement();
            textCol.AddToClassList("notification-text");

            var topRow = new VisualElement();
            topRow.AddToClassList("notification-top-row");
            var titleLabel = new Label();
            titleLabel.AddToClassList("notification-title");
            topRow.Add(titleLabel);
            refs.TitleLabel = titleLabel;

            // Always present, toggled with the shared "hidden" class instead of being
            // added/removed from the hierarchy, so read<->unread doesn't need a
            // structural change.
            var dot = new VisualElement();
            dot.AddToClassList("notification-unread-dot");
            topRow.Add(dot);
            refs.UnreadDot = dot;

            var messageLabel = new Label();
            messageLabel.AddToClassList("notification-message");
            refs.MessageLabel = messageLabel;

            var timeLabel = new Label();
            timeLabel.AddToClassList("notification-time");
            refs.TimeLabel = timeLabel;

            textCol.Add(topRow);
            textCol.Add(messageLabel);
            textCol.Add(timeLabel);

            row.Add(iconBox);
            row.Add(textCol);

            // Tapping a notification marks it read for this session (a real app
            // might also deep-link to the relevant screen here, e.g. the classroom
            // that posted it). Registered once here rather than per-update - the
            // handler always reads refs.LastEntry, so it stays correct as
            // UpdateNotificationRow() patches this row with newer entries.
            row.RegisterCallback<ClickEvent>(_ =>
            {
                var current = refs.LastEntry;
                if (current == null || current.IsRead) return;
                current.IsRead = true;
                _locallyReadIds.Add(current.Id);
                RefreshNotificationsUI();
            });

            UpdateNotificationRow(refs, entry);
            return refs;
        }

        /// <summary>Patches one already-built row's labels/classes to match entry,
        /// touching only the fields that actually changed.</summary>
        private void UpdateNotificationRow(NotificationRowRefs refs, NotificationEntry entry)
        {
            refs.LastEntry = entry;

            refs.Row.EnableInClassList("notification-row-unread", !entry.IsRead);
            refs.UnreadDot.EnableInClassList("hidden", entry.IsRead);

            string iconEmoji = GetIconEmoji(entry.Icon);
            if (refs.IconLabel.text != iconEmoji) refs.IconLabel.text = iconEmoji;

            string iconColorClass = GetIconColorClass(entry.Icon);
            if (refs.LastIconColorClass != iconColorClass)
            {
                if (refs.LastIconColorClass != null) refs.IconBox.RemoveFromClassList(refs.LastIconColorClass);
                refs.IconBox.AddToClassList(iconColorClass);
                refs.LastIconColorClass = iconColorClass;
            }

            if (refs.TitleLabel.text != entry.Title) refs.TitleLabel.text = entry.Title;
            if (refs.MessageLabel.text != entry.Message) refs.MessageLabel.text = entry.Message;

            string timeText = string.IsNullOrEmpty(entry.ClassroomName)
                ? entry.TimeAgo
                : $"{entry.ClassroomName} • {entry.TimeAgo}";
            if (refs.TimeLabel.text != timeText) refs.TimeLabel.text = timeText;
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
            Debug.Log("[StudentNotificationsController] Navigating back to previous screen");
            UIManager.Instance.ReturnFromStudentNotifications();
        }

        private void OnMarkAllReadClicked(ClickEvent evt)
        {
            // Optimistic UI update - flip everything to read immediately, and
            // remember each id locally so a live update that arrives before the
            // cursor write below finishes can't flicker any of them back to unread.
            foreach (var entry in _notifications)
            {
                entry.IsRead = true;
                _locallyReadIds.Add(entry.Id);
            }
            RefreshNotificationsUI();

            // ...then persist the "read up to now" cursor so it survives a
            // re-open/sign-out. If this fails, the live listener still has whatever
            // the server's actual cursor is, so a later real change will correctly
            // show unread items again rather than silently losing the failure.
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