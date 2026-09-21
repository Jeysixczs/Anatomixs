using System;
using System.Collections;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Polls Application.internetReachability on an interval and exposes the
    /// result as both a static property (NetworkStatusMonitor.IsOnline) and a
    /// static C# event (OnConnectivityChanged), so any screen controller -
    /// StudentClassroomHubController, StudentClassroomDetailController, or
    /// anything added later - can react to a connection dropping or coming
    /// back WITHOUT each one running its own polling coroutine.
    ///
    /// Attach this to the same persistent Bootstrap GameObject as
    /// FirebaseBootstrap and UIManager (DontDestroyOnLoad, created once at
    /// startup). Unity has no OS-level "connectivity changed" callback that's
    /// reliable across Android/iOS/Editor, so polling is the standard
    /// approach - the default 2s interval is frequent enough to feel
    /// responsive without hammering anything, since this only reads a local
    /// OS flag and makes no network calls of its own.
    ///
    /// IMPORTANT: Application.internetReachability only reports whether a
    /// network interface is up (Wi-Fi/cellular/LAN), NOT whether it actually
    /// has working internet access - a captive portal or a router with no
    /// upstream will still report Reachable. Treat IsOnline as "worth
    /// attempting a request", not a guarantee it'll succeed. Firestore calls
    /// can still time out or fail even when this reports true; callers (e.g.
    /// OfflineOverlay's Retry button) re-check this flag before retrying, but
    /// should still handle their own request failures separately.
    /// </summary>
    public class NetworkStatusMonitor : MonoBehaviour
    {
        public static NetworkStatusMonitor Instance { get; private set; }

        [Tooltip("How often to re-check Application.internetReachability, in seconds.")]
        [SerializeField] private float pollIntervalSeconds = 2f;

        /// <summary>True if a network interface is currently reachable. This reads
        /// Application.internetReachability directly (not a cached field), so it's
        /// safe to call even before this component's own Awake has run - e.g. from
        /// another screen controller's OnEnable during the very first frame.</summary>
        public static bool IsOnline => Application.internetReachability != NetworkReachability.NotReachable;

        /// <summary>Fires whenever reachability flips, with the NEW state (true =
        /// back online, false = just went offline). Only fires on an actual change,
        /// not on every poll, so subscribers don't need to de-dupe themselves.</summary>
        public static event Action<bool> OnConnectivityChanged;

        /// <summary>Fires when the app returns to the foreground after at least
        /// <see cref="minBackgroundSecondsForResume"/> seconds in the background (e.g. the
        /// teacher opened a file in another app, or switched away and came back). The payload
        /// is how many seconds the app was away. Only fires while online: if the phone has no
        /// connection at the moment of resuming, it is held back and fired the moment
        /// connectivity returns. Screens with live data subscribe in OnEnable, unsubscribe in
        /// OnDisable, and use it to re-attach their listeners / refresh one-shot loads.</summary>
        public static event Action<float> OnAppResumed;

        [Tooltip("Ignore quick pause/resume blips (permission dialogs, share sheets) shorter than this, in seconds.")]
        [SerializeField] private float minBackgroundSecondsForResume = 3f;

        private bool _lastKnownOnline;
        private Coroutine _pollRoutine;

        private bool _isBackgrounded;
        private DateTime _backgroundedAtUtc;
        private bool _resumePending;
        private float _pendingAwaySeconds;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            _lastKnownOnline = IsOnline;
        }

        private void OnEnable()
        {
            _pollRoutine = StartCoroutine(PollLoop());
        }

        private void OnDisable()
        {
            if (_pollRoutine != null)
            {
                StopCoroutine(_pollRoutine);
                _pollRoutine = null;
            }
        }

        // Unity calls this on Android/iOS when the app goes to the background (paused = true)
        // and when it comes back (paused = false). UTC wall-clock is used for the duration
        // because Time.* doesn't reliably advance while the app is suspended.
        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                _isBackgrounded = true;
                _backgroundedAtUtc = DateTime.UtcNow;
                return;
            }

            if (!_isBackgrounded) return; // not a real resume (e.g. the very first callback at startup)
            _isBackgrounded = false;

            float awaySeconds = (float)(DateTime.UtcNow - _backgroundedAtUtc).TotalSeconds;
            if (awaySeconds < minBackgroundSecondsForResume) return;

            if (IsOnline)
            {
                RaiseAppResumed(awaySeconds);
            }
            else
            {
                // Radios are often still reconnecting for a moment after resuming. Hold the
                // signal until PollLoop sees the connection is back.
                _resumePending = true;
                _pendingAwaySeconds = awaySeconds;
            }
        }

        private void RaiseAppResumed(float awaySeconds)
        {
            Debug.Log($"[NetworkStatusMonitor] App resumed after {awaySeconds:F0}s in the background.");
            OnAppResumed?.Invoke(awaySeconds);
        }

        private IEnumerator PollLoop()
        {
            // Realtime, not scaled - connectivity should still be detected if the
            // game is paused (Time.timeScale = 0) on a menu/overlay screen.
            var wait = new WaitForSecondsRealtime(pollIntervalSeconds);

            while (true)
            {
                bool online = IsOnline;
                if (online != _lastKnownOnline)
                {
                    _lastKnownOnline = online;
                    Debug.Log($"[NetworkStatusMonitor] Connectivity changed: {(online ? "online" : "offline")}");
                    OnConnectivityChanged?.Invoke(online);
                }

                if (online && _resumePending)
                {
                    _resumePending = false;
                    RaiseAppResumed(_pendingAwaySeconds);
                }

                yield return wait;
            }
        }
    }
}
