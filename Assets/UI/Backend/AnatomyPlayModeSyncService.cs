using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>Every state the Sync Progress button/status label can be
    /// in - see StudentExplore3dController for how each one is
    /// presented.</summary>
    public enum PlayModeSyncState
    {
        Idle,       // Nothing evaluated yet (before the first RefreshStatus/RequestFullSync).
        Offline,    // No internet connection right now.
        Pending,    // Online, but local records are still waiting to reach Firebase.
        Syncing,    // A sync (upload and/or download+merge) is running right now.
        Synced,     // Fully up to date - nothing pending, last sync (if any) succeeded.
        Failed      // The last sync attempt didn't fully succeed; records are still safely local/pending.
    }

    /// <summary>
    /// Coordinates syncing locally-stored, still-pending Play Mode answers
    /// up to Firebase, AND downloading + merging this student's existing
    /// Firebase progress back into local storage (the "Sync Progress"
    /// feature - see the plan's sections 3/4/5/6). Reads pending records
    /// from AnatomyPlayModeLocalStorage, pushes each one through
    /// AnatomyPlayModeFirebase.SyncRecord - which writes to a deterministic
    /// document ID, so a retry updates the same Firebase document instead
    /// of creating a duplicate - and marks it synced in local storage only
    /// once Firebase confirms the write. It never deletes or overwrites a
    /// local record's answer data; a failed sync always leaves progress
    /// exactly as it was, still safely on-device.
    ///
    /// This class owns NO gameplay state and NO UI - it exposes OnStatusChanged
    /// (for StudentExplore3dController's Sync button/label) and
    /// OnSyncCompleted (for AnatomyPlayModeController to refresh
    /// _completedKeys if Play Mode happens to be open mid-sync) instead of
    /// reaching into either directly.
    ///
    /// Two ways to sync:
    ///   - RequestSync(): fire-and-forget UPLOAD ONLY. Used right after a
    ///     correct answer is saved while online (see
    ///     AnatomyPlayModeController.SaveCorrectAnswer) - fast, no need to
    ///     re-download the student's own progress they already have in
    ///     memory.
    ///   - RequestFullSync(onComplete): UPLOAD then DOWNLOAD+MERGE. Used for
    ///     everything the Sync Progress button/automatic triggers care
    ///     about (see StudentExplore3dController) - it's the only path that
    ///     can pull down progress made on another device.
    /// It also watches Application.internetReachability itself so a sync is
    /// retried automatically the moment the device comes back online, with
    /// no action required from any caller.
    ///
    /// Attach anywhere on the persistent Bootstrap GameObject and assign it
    /// to AnatomyPlayModeController's "Sync Service" and
    /// StudentExplore3dController's "Sync Service" fields in the Inspector.
    /// Optional for AnatomyPlayModeController: it still works, saving
    /// locally and to Firebase directly, if left unassigned there.
    /// </summary>
    public class AnatomyPlayModeSyncService : MonoBehaviour
    {
        [Tooltip("How often (seconds) to poll connectivity for a return-to-online transition.")]
        [SerializeField] private float pollIntervalSeconds = 5f;

        [Tooltip("Required. Where pending records to sync come from, and where confirmed syncs / downloaded records get merged.")]
        [SerializeField] private AnatomyPlayModeLocalStorage localStorage;

        [Tooltip("Required. Actually performs the Firebase upload/download for each record.")]
        [SerializeField] private AnatomyPlayModeFirebase firebase;

        private bool _syncInProgress;
        private bool _wasOnline;
        private Coroutine _pollRoutine;
        private bool _subscribedToSession;

        public bool IsOnline => Application.internetReachability != NetworkReachability.NotReachable;

        /// <summary>The most recent status this service has published -
        /// what a UI element should read on first wiring up, before
        /// waiting for the next OnStatusChanged.</summary>
        public PlayModeSyncState CurrentState { get; private set; } = PlayModeSyncState.Idle;

        /// <summary>The pending-record count as of the last published
        /// status - matches what OnStatusChanged's second argument last
        /// carried.</summary>
        public int PendingCount { get; private set; }

        /// <summary>Fires whenever sync status changes - state, plus how
        /// many local records are still pending. StudentExplore3dController
        /// uses this to drive the Sync button text/status label without
        /// polling.</summary>
        public event Action<PlayModeSyncState, int> OnStatusChanged;

        /// <summary>Fires after a RequestFullSync attempt finishes (upload
        /// phase done, and download+merge phase done or skipped), success
        /// or not. Local storage's completed-structure set may have grown
        /// (new structures merged down from Firebase) - AnatomyPlayModeController
        /// subscribes to this so an already-open Play Mode session can
        /// refresh _completedKeys without the player having to leave and
        /// reopen the screen (see the plan's section 7).</summary>
        public event Action OnSyncCompleted;

        // The signed-in student's uid - same identity every other Play Mode
        // script reads from PlayerSessionManager, never a locally-invented
        // one, so this service's local/Firebase reads always target the
        // right student.
        private static string CurrentStudentId =>
            PlayerSessionManager.Instance != null && PlayerSessionManager.Instance.CurrentStudent != null
                ? PlayerSessionManager.Instance.CurrentStudent.Uid
                : null;

        private void OnEnable()
        {
            _wasOnline = IsOnline;
            _pollRoutine = StartCoroutine(PollConnectivity());

            // Auto-sync trigger: "the application starts and internet is
            // available" (plan section 8). If a student is already signed
            // in by the time this enables, this covers it; if not, the
            // session-changed subscription below (attempted every poll
            // tick until it succeeds - PlayerSessionManager.Instance may
            // not exist yet on this exact frame) covers sign-in instead.
            TryAutoFullSync();
        }

        private void OnDisable()
        {
            if (_pollRoutine != null)
            {
                StopCoroutine(_pollRoutine);
                _pollRoutine = null;
            }

            if (_subscribedToSession && PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
                _subscribedToSession = false;
            }
        }

        // Polls rather than relying solely on a caller to notice
        // connectivity returned - satisfies the plan's section 3.4/8
        // ("automatically retry" / "device reconnects") even if nothing
        // else in the app happens to call a sync method right when the
        // connection comes back. Also opportunistically (re)subscribes to
        // PlayerSessionManager here, since FirebaseBootstrap/PlayerSessionManager
        // initialize asynchronously and may not have set Instance yet on
        // this component's own OnEnable frame.
        private IEnumerator PollConnectivity()
        {
            var wait = new WaitForSeconds(pollIntervalSeconds);
            while (true)
            {
                EnsureSubscribedToSession();

                bool online = IsOnline;
                if (online && !_wasOnline)
                    RequestFullSync(); // reconnect - also worth re-downloading in case another device wrote progress while this one was offline.
                else if (!online)
                    PublishStatus(PlayModeSyncState.Offline);

                _wasOnline = online;
                yield return wait;
            }
        }

        private void EnsureSubscribedToSession()
        {
            if (_subscribedToSession || PlayerSessionManager.Instance == null) return;

            PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
            PlayerSessionManager.Instance.OnStudentProfileChanged += OnStudentProfileChanged;
            _subscribedToSession = true;
        }

        // Auto-sync trigger: student just signed in (or their profile
        // otherwise changed/loaded) - see the plan's section 8.
        private void OnStudentProfileChanged(PlayerSessionManager.StudentProfile profile)
        {
            if (profile != null)
                TryAutoFullSync();
        }

        private void TryAutoFullSync()
        {
            if (!string.IsNullOrEmpty(CurrentStudentId) && IsOnline)
                RequestFullSync();
            else
                RefreshStatus();
        }

        /// <summary>Recomputes and publishes CurrentState/PendingCount
        /// WITHOUT touching the network - just what's already on disk plus
        /// current connectivity. Cheap, safe to call any time (e.g. the
        /// instant StudentExplore3dController wires up its Sync button, so
        /// the label shows something correct before any actual sync has
        /// run).</summary>
        public void RefreshStatus()
        {
            if (localStorage == null)
            {
                PublishStatus(CurrentState);
                return;
            }

            string studentId = CurrentStudentId;
            if (!string.IsNullOrEmpty(studentId))
                localStorage.Load(studentId);

            if (!IsOnline)
            {
                PublishStatus(PlayModeSyncState.Offline);
                return;
            }

            PublishStatus(localStorage.PendingCount > 0 ? PlayModeSyncState.Pending : PlayModeSyncState.Synced);
        }

        private void PublishStatus(PlayModeSyncState state)
        {
            CurrentState = state;
            PendingCount = localStorage != null ? localStorage.PendingCount : 0;
            OnStatusChanged?.Invoke(CurrentState, PendingCount);
        }

        /// <summary>Attempts to push every currently-pending local record to
        /// Firebase right now - UPLOAD ONLY, no download/merge (see the
        /// class comment for when to use this vs RequestFullSync). Safe to
        /// call any time - offline, Firebase not ready, mid-sync already,
        /// nothing pending - it simply no-ops rather than throwing, and a
        /// failed attempt never removes or alters the local record (it
        /// stays 'pending' for the next try).</summary>
        public void RequestSync()
        {
            if (_syncInProgress) return;
            if (firebase == null || localStorage == null) return;
            if (!IsOnline)
            {
                PublishStatus(PlayModeSyncState.Offline);
                return;
            }

            var pending = localStorage.GetPendingRecords();
            if (pending.Count == 0)
            {
                PublishStatus(PlayModeSyncState.Synced);
                return;
            }

            _syncInProgress = true;
            PublishStatus(PlayModeSyncState.Syncing);

            UploadPending(pending, allSucceeded =>
            {
                _syncInProgress = false;
                PublishStatus(localStorage.PendingCount > 0
                    ? PlayModeSyncState.Failed
                    : PlayModeSyncState.Synced);
            });
        }

        /// <summary>Manual/automatic full sync - UPLOAD every pending local
        /// record, then DOWNLOAD this student's Firebase progress and MERGE
        /// it into local storage (see the plan's sections 4/5/6). Idempotent
        /// and safe to call repeatedly/concurrently-in-spirit: a call while
        /// one is already running is ignored (onComplete gets false)
        /// instead of double-syncing, and nothing here ever deletes local
        /// data - a failure just leaves records 'pending' for next
        /// time.
        ///
        /// onComplete receives true only if the upload fully succeeded AND
        /// nothing is left pending afterward; either way, OnSyncCompleted
        /// fires and local storage's completed-structure set is safe to
        /// re-read (e.g. via AnatomyPlayModeController.RefreshCompletedKeys)
        /// the moment onComplete/OnSyncCompleted runs.</summary>
        public void RequestFullSync(Action<bool> onComplete = null)
        {
            if (_syncInProgress)
            {
                onComplete?.Invoke(false);
                return;
            }

            if (firebase == null || localStorage == null)
            {
                onComplete?.Invoke(false);
                return;
            }

            string studentId = CurrentStudentId;
            if (string.IsNullOrEmpty(studentId))
            {
                onComplete?.Invoke(false);
                return;
            }

            // Always (re)load from disk first so this sync reflects
            // whatever this device has actually persisted, not just
            // whatever happens to already be in memory.
            localStorage.Load(studentId);

            if (!IsOnline)
            {
                PublishStatus(PlayModeSyncState.Offline);
                onComplete?.Invoke(false);
                return;
            }

            _syncInProgress = true;
            PublishStatus(PlayModeSyncState.Syncing);

            var pending = localStorage.GetPendingRecords();
            UploadPending(pending, uploadSucceeded =>
            {
                firebase.FetchProgress(studentId,
                    remoteRecords =>
                    {
                        foreach (var record in remoteRecords)
                            localStorage.MergeRemoteRecord(record);

                        FinishFullSync(uploadSucceeded, onComplete);
                    },
                    _ =>
                    {
                        // Download failed - uploaded records are still
                        // safely marked synced above; just skip the
                        // merge step this time.
                        FinishFullSync(uploadSucceeded, onComplete);
                    });
            });
        }

        private void FinishFullSync(bool uploadSucceeded, Action<bool> onComplete)
        {
            _syncInProgress = false;

            bool stillPending = localStorage.PendingCount > 0;
            PublishStatus(!uploadSucceeded || stillPending
                ? PlayModeSyncState.Failed
                : PlayModeSyncState.Synced);

            OnSyncCompleted?.Invoke();
            onComplete?.Invoke(uploadSucceeded && !stillPending);
        }

        // Uploads pending records one at a time (rather than firing every
        // request at once) so a burst of pending records doesn't flood
        // Firebase the instant connectivity returns. onDone reports whether
        // every single record succeeded - a partial failure still marks
        // whichever ones DID succeed as synced and leaves the rest
        // 'pending' for the next attempt (see AnatomyPlayModeLocalStorage.MarkSynced).
        private void UploadPending(List<PlayModeAnswerRecord> pending, Action<bool> onDone)
        {
            UploadNext(pending, 0, true, onDone);
        }

        private void UploadNext(List<PlayModeAnswerRecord> pending, int index, bool allSucceededSoFar, Action<bool> onDone)
        {
            if (index >= pending.Count)
            {
                onDone(allSucceededSoFar);
                return;
            }

            var record = pending[index];
            firebase.SyncRecord(record, success =>
            {
                if (success)
                    localStorage.MarkSynced(record.key);
                // On failure the record is simply left 'pending' - it will
                // be retried on the next sync (next connectivity change,
                // manual Sync Progress tap, or the next answer saved while
                // online).
                UploadNext(pending, index + 1, allSucceededSoFar && success, onDone);
            });
        }
    }
}
