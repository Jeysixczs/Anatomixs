using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Anatomia3D.UI;
using Firebase.Extensions;
using Firebase.Messaging;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Free-tier (Spark plan, no billing account) classroom-announcement push
    /// notifications, built on top of the existing Firestore
    /// `classrooms/{id}/announcements` subcollection - see AdminClassroomService.
    /// PostAnnouncement() / ClassroomService.FetchAnnouncements().
    ///
    /// FCM's client SDK can only subscribe/receive - it can never *send* a
    /// message (that always requires server credentials, which is why the
    /// actual "notify students" step lives outside this app entirely, in the
    /// AnnouncementPushSender Google Apps Script project - see SETUP.md). This
    /// service only ever does two things: (1) keep this device's topic
    /// subscriptions in sync with the student's enrolled classrooms, and
    /// (2) react to messages FCM hands it, whether that's a live foreground
    /// delivery or the app being launched by a tap on a system-tray notification.
    ///
    /// Attach to the same persistent Bootstrap GameObject as FirebaseBootstrap /
    /// ClassroomService / PlayerSessionManager. Requires FirebaseBootstrap to
    /// finish initializing first (this waits on FirebaseBootstrap.OnReady), and
    /// requires PlayerSessionManager.CurrentStudent for tap-navigation, since a
    /// cold-start tap can land here before login has finished restoring the
    /// session - see ProcessPendingNavigation().
    ///
    /// Topics are named `classroom_{classroomId}` (see SubscribeToClassroom).
    /// Nothing here talks to Firestore Security Rules directly - a student can
    /// only ever be *subscribed* to topics client-side; whether the classroom's
    /// announcements themselves are readable is still enforced the normal way
    /// by the existing rules on `classrooms/{id}/announcements`.
    /// </summary>
    public class FCMNotificationService : MonoBehaviour
    {
        public static FCMNotificationService Instance { get; private set; }

        private const string TopicPrefix = "classroom_";
        private const string SubscribedTopicsPrefKeyFormat = "fcm_subscribed_classrooms_{0}"; // {0} = student uid

        // Per-student topic, used for pushes that must reach some students in a classroom
        // but not others - currently the 1-hour quiz-deadline reminder, which the
        // QuizDeadlineReminder Apps Script sends only to students with no attempt on that
        // quiz yet. A topic can't be filtered per-recipient, so the filtering happens
        // sender-side and each surviving student is addressed on their own topic.
        private const string StudentTopicPrefix = "student_";
        private const string SubscribedPersonalTopicPrefKeyFormat = "fcm_subscribed_personal_{0}"; // {0} = student uid
        private const string AndroidNotificationChannelId = "announcements";
        private const string AndroidNotificationChannelName = "Announcements";

        // A separate channel for quiz-deadline reminders, so a student can mute
        // reminders in Android's notification settings without also losing their
        // teacher's announcements (and vice versa). The Apps Script sender picks
        // which channel each push lands in via android.notification.channel_id -
        // see QuizDeadlineReminder.gs.
        private const string AndroidQuizReminderChannelId = "quiz_reminders";
        private const string AndroidQuizReminderChannelName = "Quiz Reminders";

        // How long to wait, after a cold-start notification tap arrives, for
        // PlayerSessionManager.CurrentStudent to become non-null before giving up
        // on navigating (see ProcessPendingNavigation). Login/session restore can
        // still be in flight at this point.
        private const float PendingNavigationTimeoutSeconds = 12f;

        /// <summary>Raised when an announcement push is delivered while the app is in the
        /// foreground (i.e. NOT a notification tap - Android already suppresses the system
        /// tray banner in that case, per FCM's default behavior). Args are (classroomId,
        /// announcementId, title, body). Hook this up to whatever in-app toast/snackbar the
        /// UI uses if a discreet heads-up is wanted; nothing subscribes to this by default, so
        /// doing nothing here is a valid, non-crashing choice too - see section 8 of the spec
        /// ("do not interrupt the student unnecessarily").</summary>
        public event Action<string, string, string, string> OnForegroundAnnouncement;

        /// <summary>Same idea as OnForegroundAnnouncement, but for a quiz-deadline reminder
        /// that arrives while the app is already open. Args are (classroomId, quizId, title,
        /// body). Nothing subscribes by default - a student who is already in the app is
        /// arguably the one who least needs interrupting, so leaving this unhandled is a
        /// valid choice.</summary>
        public event Action<string, string, string, string> OnForegroundQuizDeadline;

        private bool _initialized;
        private string _pendingNavigationClassroomId;
        private bool _pendingNavigationOpensQuizzesTab;
        private float _pendingNavigationDeadline = -1f;

        // Small FIFO of recently-handled message ids, guarding against FCM's
        // at-least-once delivery occasionally redelivering the same message (section 14).
        // Shared across push types - ids are prefixed by type (see IsDuplicateMessage) so an
        // announcement id and a quiz id can never collide with each other.
        private readonly Queue<string> _recentAnnouncementIds = new Queue<string>();
        private readonly HashSet<string> _recentAnnouncementIdSet = new HashSet<string>();
        private const int RecentIdCapacity = 50;

        // MessageReceived / TokenReceived can fire off the Unity main thread - everything
        // that touches Unity APIs (Firestore calls, UI, PlayerPrefs) is queued here and
        // drained in Update() instead of running inline.
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (FirebaseBootstrap.Instance != null && FirebaseBootstrap.Instance.IsReady)
            {
                Initialize();
            }
            else if (FirebaseBootstrap.Instance != null)
            {
                FirebaseBootstrap.Instance.OnReady += Initialize;
            }
            else
            {
                Debug.LogError("[FCMNotificationService] FirebaseBootstrap.Instance is missing - " +
                                "make sure this is on the Bootstrap GameObject alongside it.");
            }
        }

        private void Update()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError($"[FCMNotificationService] Queued action threw: {e}"); }
            }

            if (_pendingNavigationDeadline > 0f)
            {
                TryProcessPendingNavigation();
            }
        }

        private void Initialize()
        {
            if (_initialized) return; // guards against a double OnReady fire re-registering handlers (section 14)
            _initialized = true;

            CreateAndroidNotificationChannel();
            RequestNotificationPermissionIfNeeded();

            FirebaseMessaging.TokenReceived += OnTokenReceived;
            FirebaseMessaging.MessageReceived += OnMessageReceived;

            Debug.Log("[FCMNotificationService] Initialized.");
        }

        private void OnDestroy()
        {
            if (!_initialized) return;
            FirebaseMessaging.TokenReceived -= OnTokenReceived;
            FirebaseMessaging.MessageReceived -= OnMessageReceived;
        }

        // ---------------- Topic subscription ----------------

        /// <summary>Call right after a student's membership in a classroom is confirmed -
        /// see ClassroomService.JoinClassroom(). Safe to call again for a classroom the
        /// student is already subscribed to (SubscribeAsync is idempotent server-side, and
        /// this also short-circuits locally against the cached subscription list).</summary>
        public void SubscribeToClassroom(string classroomId)
        {
            if (string.IsNullOrEmpty(classroomId)) return;

            var subscribed = LoadSubscribedTopics();
            if (subscribed.Contains(classroomId))
            {
                Debug.Log($"[FCMNotificationService] Already subscribed to classroom {classroomId} - skipping.");
                return;
            }

            string topic = TopicPrefix + classroomId;
            FirebaseMessaging.SubscribeAsync(topic).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[FCMNotificationService] Could not subscribe to {topic}: {task.Exception}");
                    return;
                }

                subscribed.Add(classroomId);
                SaveSubscribedTopics(subscribed);
                Debug.Log($"[FCMNotificationService] Subscribed to {topic}.");
            });
        }

        /// <summary>Not called anywhere yet - there's no "leave classroom" flow in the app
        /// today - but kept ready for when one exists, so that feature doesn't also need to
        /// touch FCM plumbing. Mirrors SubscribeToClassroom().</summary>
        public void UnsubscribeFromClassroom(string classroomId)
        {
            if (string.IsNullOrEmpty(classroomId)) return;

            string topic = TopicPrefix + classroomId;
            FirebaseMessaging.UnsubscribeAsync(topic).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[FCMNotificationService] Could not unsubscribe from {topic}: {task.Exception}");
                    return;
                }

                var subscribed = LoadSubscribedTopics();
                subscribed.Remove(classroomId);
                SaveSubscribedTopics(subscribed);
                Debug.Log($"[FCMNotificationService] Unsubscribed from {topic}.");
            });
        }

        /// <summary>Subscribes to every classroom id in enrolledClassroomIds that isn't
        /// already tracked locally. Called from ClassroomService.SyncClassroomSubscriptions()
        /// - see that method for when to call it (once per login).</summary>
        public void SyncSubscriptions(List<string> enrolledClassroomIds)
        {
            // Personal topic first, and unconditionally: a student enrolled in zero
            // classrooms still needs it (and still can't receive anything they shouldn't,
            // since the sender only ever addresses it for quizzes they're a member for).
            SubscribeToPersonalTopic();

            if (enrolledClassroomIds == null) return;
            foreach (var id in enrolledClassroomIds) SubscribeToClassroom(id);
        }

        /// <summary>Subscribes this device to the signed-in student's own topic
        /// (`student_{uid}`). Targeted pushes land here rather than on classroom_{id} -
        /// see StudentTopicPrefix. Idempotent: FirebaseMessaging.SubscribeAsync is
        /// idempotent server-side, and this also short-circuits against a local flag, so
        /// calling it on every login and every token refresh is cheap.
        ///
        /// Caveat worth knowing: FCM topic names are not access-controlled - any client
        /// can subscribe to any topic string. Nothing sensitive goes in these payloads
        /// (quiz id and title only), but if that ever changes, move targeted sends onto
        /// stored registration tokens instead of a topic.</summary>
        public void SubscribeToPersonalTopic()
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null || string.IsNullOrEmpty(student.Uid)) return;

            string prefKey = string.Format(SubscribedPersonalTopicPrefKeyFormat, student.Uid);
            if (PlayerPrefs.GetInt(prefKey, 0) == 1)
            {
                Debug.Log("[FCMNotificationService] Already subscribed to personal topic - skipping.");
                return;
            }

            string topic = StudentTopicPrefix + student.Uid;
            FirebaseMessaging.SubscribeAsync(topic).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[FCMNotificationService] Could not subscribe to {topic}: {task.Exception}");
                    return;
                }

                PlayerPrefs.SetInt(prefKey, 1);
                PlayerPrefs.Save();
                Debug.Log($"[FCMNotificationService] Subscribed to {topic}.");
            });
        }

        /// <summary>Call on logout if a device can be shared between students - otherwise
        /// the previous student's quiz reminders keep arriving on it. Takes the uid
        /// explicitly because PlayerSessionManager.CurrentStudent is usually already null
        /// by the time logout teardown runs.</summary>
        public void UnsubscribeFromPersonalTopic(string studentUid)
        {
            if (string.IsNullOrEmpty(studentUid)) return;

            string topic = StudentTopicPrefix + studentUid;
            FirebaseMessaging.UnsubscribeAsync(topic).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[FCMNotificationService] Could not unsubscribe from {topic}: {task.Exception}");
                    return;
                }

                PlayerPrefs.DeleteKey(string.Format(SubscribedPersonalTopicPrefKeyFormat, studentUid));
                PlayerPrefs.Save();
                Debug.Log($"[FCMNotificationService] Unsubscribed from {topic}.");
            });
        }

        private HashSet<string> LoadSubscribedTopics()
        {
            string key = SubscribedTopicsPrefKey();
            string raw = PlayerPrefs.GetString(key, "");
            return string.IsNullOrEmpty(raw)
                ? new HashSet<string>()
                : new HashSet<string>(raw.Split(','));
        }

        private void SaveSubscribedTopics(HashSet<string> topics)
        {
            PlayerPrefs.SetString(SubscribedTopicsPrefKey(), string.Join(",", topics));
            PlayerPrefs.Save();
        }

        private string SubscribedTopicsPrefKey()
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            string uid = student != null ? student.Uid : "unknown";
            return string.Format(SubscribedTopicsPrefKeyFormat, uid);
        }

        // ---------------- Token handling ----------------

        /// <summary>FCM manages topic subscriptions against the app's stable Firebase
        /// Installation, not the raw registration token, so a token refresh does NOT by
        /// itself drop existing topic subscriptions - Google's SDK carries them over. This
        /// handler exists anyway (a) to satisfy explicit token-refresh handling, and (b) as a
        /// defensive re-sync: if a token rotates, re-asserting subscriptions for every
        /// classroom the student is enrolled in is cheap and idempotent, so it's a fine hedge
        /// against edge cases either side of that guarantee.</summary>
        private void OnTokenReceived(object sender, TokenReceivedEventArgs e)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Debug.Log("[FCMNotificationService] FCM token (re)issued - re-syncing classroom subscriptions.");
                var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
                if (student == null) return; // nothing to sync yet - login will call SyncClassroomSubscriptions() itself

                ClassroomService.Instance?.SyncClassroomSubscriptions();
            });
        }

        // ---------------- Message handling ----------------

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            _mainThreadActions.Enqueue(() => HandleMessage(e.Message));
        }

        private void HandleMessage(FirebaseMessage message)
        {
            if (message?.Data == null) return;

            if (!message.Data.TryGetValue("type", out string type)) return;

            switch (type)
            {
                case "announcement":
                    HandleAnnouncementMessage(message);
                    break;
                case "quiz_deadline":
                    HandleQuizDeadlineMessage(message);
                    break;
                default:
                    // Unknown push type - ignore rather than guess. Keeps older app
                    // builds from misreading a payload shape added after they shipped.
                    break;
            }
        }

        private void HandleAnnouncementMessage(FirebaseMessage message)
        {
            message.Data.TryGetValue("announcementId", out string announcementId);
            message.Data.TryGetValue("classroomId", out string classroomId);
            string title = message.Notification != null ? message.Notification.Title : "";
            string body = message.Notification != null ? message.Notification.Body : "";

            if (string.IsNullOrEmpty(classroomId))
            {
                Debug.LogWarning("[FCMNotificationService] Announcement push missing classroomId - ignoring.");
                return;
            }

            if (IsDuplicateMessage("announcement:" + announcementId)) return;

            if (message.NotificationOpened)
            {
                // Student tapped the system-tray notification (background or fully-closed
                // app) - navigate them straight to the announcement.
                NavigateToClassroom(classroomId, openQuizzesTab: false);
            }
            else
            {
                // Delivered while the app was already open. Android already skipped showing
                // a system-tray banner for this case - just let the app decide how to
                // surface it in-app (or not at all).
                OnForegroundAnnouncement?.Invoke(classroomId, announcementId, title, body);
            }
        }

        /// <summary>A "your quiz closes soon" reminder, sent by the QuizDeadlineReminder
        /// Apps Script project on an hourly time-driven trigger (see its header comment for
        /// the payload contract). Unlike announcements this is NOT addressed to
        /// `classroom_{classroomId}` - the script queries quizAttempts first and sends to
        /// `student_{uid}` for each student with no attempt on that quiz yet, so a student
        /// who has already taken it is never nagged. The payload otherwise carries the same
        /// classroomId/quizId fields as before, plus studentId.
        ///
        /// The reminderKey in the payload is the script's own dedupe key
        /// (`{quizId}_{classroomId}_{studentId}_{hours}h`) and doubles as this client's
        /// duplicate guard, so an FCM redelivery of the same milestone is dropped here too.</summary>
        private void HandleQuizDeadlineMessage(FirebaseMessage message)
        {
            message.Data.TryGetValue("classroomId", out string classroomId);
            message.Data.TryGetValue("quizId", out string quizId);
            message.Data.TryGetValue("reminderKey", out string reminderKey);
            string title = message.Notification != null ? message.Notification.Title : "";
            string body = message.Notification != null ? message.Notification.Body : "";

            if (string.IsNullOrEmpty(classroomId))
            {
                Debug.LogWarning("[FCMNotificationService] Quiz deadline push missing classroomId - ignoring.");
                return;
            }

            // Fall back to quizId if the sender omitted reminderKey, so a payload from an
            // older/hand-sent message still gets some duplicate protection.
            string dedupeKey = !string.IsNullOrEmpty(reminderKey) ? reminderKey : quizId;
            if (IsDuplicateMessage("quiz_deadline:" + dedupeKey)) return;

            if (message.NotificationOpened)
            {
                // Land on Available Quizzes rather than Overview - the student tapped a
                // "closes soon" reminder, so the quiz list is what they came for.
                NavigateToClassroom(classroomId, openQuizzesTab: true);
            }
            else
            {
                OnForegroundQuizDeadline?.Invoke(classroomId, quizId, title, body);
            }
        }

        /// <summary>FCM guarantees at-least-once delivery, so the same push can arrive twice.
        /// Returns true if this key was already handled recently. A null/empty key can't be
        /// deduped, so it is always treated as new rather than silently swallowed.</summary>
        private bool IsDuplicateMessage(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            if (_recentAnnouncementIdSet.Contains(key)) return true;

            _recentAnnouncementIdSet.Add(key);
            _recentAnnouncementIds.Enqueue(key);
            if (_recentAnnouncementIds.Count > RecentIdCapacity)
            {
                _recentAnnouncementIdSet.Remove(_recentAnnouncementIds.Dequeue());
            }

            return false;
        }

        /// <summary>Loads the classroom (for its name/code, which
        /// UIManager.ShowStudentClassroomDetail() needs) and opens it on the Overview tab,
        /// where announcements live. Handles the classroom/announcement no longer existing
        /// gracefully (section 7) instead of crashing, and handles a cold-start tap arriving
        /// before login/session-restore has finished by deferring briefly - see
        /// TryProcessPendingNavigation().</summary>
        private void NavigateToClassroom(string classroomId, bool openQuizzesTab)
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null)
            {
                _pendingNavigationClassroomId = classroomId;
                _pendingNavigationOpensQuizzesTab = openQuizzesTab;
                _pendingNavigationDeadline = Time.unscaledTime + PendingNavigationTimeoutSeconds;
                return;
            }

            if (ClassroomService.Instance == null || UIManager.Instance == null)
            {
                Debug.LogWarning("[FCMNotificationService] ClassroomService/UIManager not ready - cannot navigate to announcement.");
                return;
            }

            ClassroomService.Instance.FetchClassroomDetail(classroomId, detail =>
            {
                if (detail == null)
                {
                    Debug.LogWarning($"[FCMNotificationService] Classroom {classroomId} from a notification tap no longer exists - staying put.");
                    return;
                }

                if (openQuizzesTab)
                {
                    UIManager.Instance.ShowStudentClassroomDetailOnQuizzesTab(detail.ClassroomId, detail.Name, detail.Code);
                }
                else
                {
                    UIManager.Instance.ShowStudentClassroomDetail(detail.ClassroomId, detail.Name, detail.Code);
                }
            });
        }

        private void TryProcessPendingNavigation()
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            bool timedOut = Time.unscaledTime >= _pendingNavigationDeadline;

            if (student == null && !timedOut) return;

            string classroomId = _pendingNavigationClassroomId;
            bool openQuizzesTab = _pendingNavigationOpensQuizzesTab;
            _pendingNavigationClassroomId = null;
            _pendingNavigationOpensQuizzesTab = false;
            _pendingNavigationDeadline = -1f;

            if (student == null)
            {
                Debug.LogWarning("[FCMNotificationService] Gave up waiting for session restore before navigating to a tapped notification.");
                return;
            }

            NavigateToClassroom(classroomId, openQuizzesTab);
        }

        // ---------------- Android setup helpers ----------------

        /// <summary>Android 8+ (API 26+) silently drops any notification posted to a channel
        /// id that hasn't been created yet - the manifest's default_notification_channel_id
        /// meta-data (see SETUP.md) only tells FCM *which* channel to use, it doesn't create
        /// it. Uses AndroidJavaObject reflection instead of a native plugin, so no extra
        /// build step or package is required.</summary>
        private void CreateAndroidNotificationChannel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var buildVersion = new AndroidJavaClass("android.os.Build$VERSION");
                int sdkInt = buildVersion.GetStatic<int>("SDK_INT");
                if (sdkInt < 26) return; // notification channels don't exist before Oreo

                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var notificationManager = activity.Call<AndroidJavaObject>("getSystemService", "notification");

                const int importanceHigh = 4; // NotificationManager.IMPORTANCE_HIGH

                using (var channel = new AndroidJavaObject(
                    "android.app.NotificationChannel",
                    AndroidNotificationChannelId,
                    AndroidNotificationChannelName,
                    importanceHigh))
                {
                    notificationManager.Call("createNotificationChannel", channel);
                }

                // Creating a channel that already exists is a no-op, so this stays safe to
                // run on every launch. Note Android will NOT apply a changed importance to
                // an already-created channel - only the name/description can be updated.
                using (var reminderChannel = new AndroidJavaObject(
                    "android.app.NotificationChannel",
                    AndroidQuizReminderChannelId,
                    AndroidQuizReminderChannelName,
                    importanceHigh))
                {
                    notificationManager.Call("createNotificationChannel", reminderChannel);
                }

                Debug.Log("[FCMNotificationService] Android notification channels ready.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FCMNotificationService] Could not create Android notification channels: {e}");
            }
#endif
        }

        /// <summary>Android 13+ (API 33+) requires runtime consent for notifications, unlike
        /// every earlier version where it was implicit. A denial here isn't fatal to anything
        /// else in the app (section 8/10) - the student simply won't get push notifications
        /// until they enable it in system settings.</summary>
        private void RequestNotificationPermissionIfNeeded()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string postNotifications = "android.permission.POST_NOTIFICATIONS";
            try
            {
                if (!Permission.HasUserAuthorizedPermission(postNotifications))
                {
                    Permission.RequestUserPermission(postNotifications);
                }
            }
            catch (Exception e)
            {
                // Older Android versions/AndroidManifest targetSdk below 33 won't recognize
                // this permission string - safe to ignore, notifications work without it there.
                Debug.Log($"[FCMNotificationService] Notification permission request skipped: {e.Message}");
            }
#endif
        }
    }
}
