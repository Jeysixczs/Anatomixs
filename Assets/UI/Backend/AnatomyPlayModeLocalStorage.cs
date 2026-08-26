using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Offline-first local storage for Play Mode progress. This is the
    /// single source of truth for "what has this student correctly
    /// answered" - AnatomyPlayModeController seeds _completedKeys from it
    /// every time Play Mode (re)starts, and AnatomyPlayModeSyncService reads
    /// its pending records to push up to Firebase.
    ///
    /// Contains NO Firebase calls and NO gameplay/UI logic - it only
    /// reads/writes records to a per-student JSON file under
    /// Application.persistentDataPath, the same separation
    /// AnatomyPlayModeFirebase keeps for Firestore.
    ///
    /// Attach anywhere on the persistent Bootstrap GameObject (same one as
    /// FirebaseBootstrap/PlayerSessionManager) and assign it to
    /// AnatomyPlayModeController's "Local Storage" field in the Inspector.
    /// </summary>
    public class AnatomyPlayModeLocalStorage : MonoBehaviour
    {
        // Singleton, same convention as FirebaseBootstrap/QuizService/
        // AdminGamificationService - lets other screens (e.g.
        // StudentProgressController) read this student's local Play Mode
        // progress without needing an Inspector-wired reference to whatever
        // GameObject AnatomyPlayModeController happens to live on.
        public static AnatomyPlayModeLocalStorage Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        [Serializable]
        private class RecordListWrapper
        {
            public List<PlayModeAnswerRecord> records = new List<PlayModeAnswerRecord>();
        }

        // In-memory cache for whichever student was last passed to Load(),
        // keyed by structure key (BoneInfo.boneName). Every write here is
        // immediately persisted to disk too - see Persist().
        private readonly Dictionary<string, PlayModeAnswerRecord> _byKey = new Dictionary<string, PlayModeAnswerRecord>();
        private string _loadedStudentId;

        private string FilePath(string studentId) =>
            Path.Combine(Application.persistentDataPath, $"playmode_progress_{Sanitize(studentId)}.json");

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        /// <summary>Loads studentId's saved records from disk into memory.
        /// Safe (and expected) to call every time Play Mode starts or the
        /// Anatomy Screen reopens - it fully reloads from disk rather than
        /// merging, so it always reflects exactly what's been persisted.
        /// A student with no saved file yet simply starts with zero
        /// records - this is not an error.</summary>
        public void Load(string studentId)
        {
            _byKey.Clear();
            _loadedStudentId = studentId;

            if (string.IsNullOrEmpty(studentId)) return;

            string path = FilePath(studentId);
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                var wrapper = JsonUtility.FromJson<RecordListWrapper>(json);
                if (wrapper?.records == null) return;

                foreach (var r in wrapper.records)
                {
                    if (r == null || string.IsNullOrEmpty(r.key)) continue;
                    _byKey[r.key] = r;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnatomyPlayModeLocalStorage] Failed to read '{path}': {e.Message}");
            }
        }

        private void Persist()
        {
            if (string.IsNullOrEmpty(_loadedStudentId)) return;

            try
            {
                var wrapper = new RecordListWrapper { records = _byKey.Values.ToList() };
                string json = JsonUtility.ToJson(wrapper);
                File.WriteAllText(FilePath(_loadedStudentId), json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnatomyPlayModeLocalStorage] Failed to write progress for '{_loadedStudentId}': {e.Message}");
            }
        }

        /// <summary>Every structure key the currently-loaded student has
        /// correctly answered - what AnatomyPlayModeController seeds
        /// _completedKeys from before allowing any new questions (see the
        /// plan's section 5). Call Load() first.</summary>
        public HashSet<string> GetCompletedKeys()
        {
            return new HashSet<string>(_byKey.Values.Where(r => r.correct).Select(r => r.key));
        }

        public bool TryGetRecord(string key, out PlayModeAnswerRecord record) => _byKey.TryGetValue(key, out record);

        /// <summary>Every record for the currently-loaded student, as saved
        /// (correct answers only - see SaveAnswer/HandleIncorrectAnswer,
        /// incorrect attempts are never written here). Used by
        /// StudentProgressController's Weekly Activity Panel to fold Play
        /// Mode points into the same Mon-Sun chart as Quiz points, keyed by
        /// each record's own timestampUtc/pointsEarned - no new tracking
        /// list, just a read of what's already stored.</summary>
        public IReadOnlyList<PlayModeAnswerRecord> GetAllRecords()
        {
            return _byKey.Values.ToList();
        }

        /// <summary>Saves a correct answer to local storage immediately -
        /// this is the offline-first write: it always succeeds regardless
        /// of connectivity or Firebase availability (see the plan's section
        /// 2/3). A structure that's already correctly recorded is never
        /// overwritten or duplicated by a later call, so the same structure
        /// can never be scored twice. Returns the stored record so the
        /// caller can hand it straight to AnatomyPlayModeSyncService /
        /// AnatomyPlayModeFirebase.</summary>
        public PlayModeAnswerRecord SaveAnswer(
            string studentId,
            string key,
            string displayName,
            bool correct,
            int hintsUsed,
            int pointsEarned,
            int streak,
            int streakBonus)
        {
            if (_byKey.TryGetValue(key, out var existing) && existing.correct)
            {
                // Already correctly answered and stored - see the "must
                // only exist once" rule in the plan's section 1. A stale or
                // repeated call (e.g. a queued click) is a no-op.
                return existing;
            }

            var record = new PlayModeAnswerRecord
            {
                studentId = studentId,
                key = key,
                displayName = displayName,
                correct = correct,
                hintsUsed = hintsUsed,
                pointsEarned = pointsEarned,
                streak = streak,
                streakBonus = streakBonus,
                timestampUtc = DateTime.UtcNow.ToString("o"),
                SyncStatus = PlayModeSyncStatus.Pending
            };

            _byKey[key] = record;
            Persist();
            return record;
        }

        /// <summary>Marks a record synced once Firebase has confirmed the
        /// write. Local data is never deleted here or anywhere else in this
        /// class - it stays available so the student's completed state can
        /// still be restored the next time they play offline (see the
        /// plan's section 3, "do not delete local progress").</summary>
        public void MarkSynced(string key)
        {
            if (!_byKey.TryGetValue(key, out var record)) return;
            record.SyncStatus = PlayModeSyncStatus.Synced;
            Persist();
        }

        /// <summary>Every record still waiting to reach Firebase - what
        /// AnatomyPlayModeSyncService retries whenever connectivity
        /// returns.</summary>
        public List<PlayModeAnswerRecord> GetPendingRecords()
        {
            return _byKey.Values.Where(r => r.SyncStatus == PlayModeSyncStatus.Pending).ToList();
        }

        /// <summary>How many records are still waiting to reach Firebase -
        /// the number the Sync Progress button/status label shows (e.g.
        /// "5 answers waiting to sync"). Cheap convenience over
        /// GetPendingRecords().Count so callers that only need the count
        /// (like a UI refresh) don't have to allocate the full list.</summary>
        public int PendingCount => _byKey.Values.Count(r => r.SyncStatus == PlayModeSyncStatus.Pending);

        /// <summary>Merges one record downloaded from Firebase into local
        /// storage - the "download" half of the Sync Progress feature (see
        /// AnatomyPlayModeSyncService.RequestFullSync). Never overwrites an
        /// existing correct local record's answer data - only its sync
        /// status, since Firebase having the record means it's definitely
        /// synced even if this device's local copy still said "pending"
        /// (e.g. a previous upload actually succeeded but the app closed
        /// before MarkSynced ran). If the structure isn't known locally at
        /// all, the downloaded record is added as already-synced.
        ///
        /// Returns true only when a brand-new record was added (i.e. this
        /// structure was completed on another device/session and is only
        /// now reaching this one) - callers can use that to know whether
        /// _completedKeys actually grew.</summary>
        public bool MergeRemoteRecord(PlayModeAnswerRecord remote)
        {
            if (remote == null || string.IsNullOrEmpty(remote.key)) return false;

            if (_byKey.TryGetValue(remote.key, out var existing))
            {
                if (existing.correct && existing.SyncStatus != PlayModeSyncStatus.Synced)
                {
                    // Firebase confirms this one made it up already -
                    // reconcile the local status rather than leaving it
                    // stuck "pending" forever.
                    existing.SyncStatus = PlayModeSyncStatus.Synced;
                    Persist();
                }
                return false;
            }

            remote.SyncStatus = PlayModeSyncStatus.Synced;
            _byKey[remote.key] = remote;
            Persist();
            return true;
        }
    }
}
