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
            public List<HintUseRecord> hintUses = new List<HintUseRecord>();
            public List<RevealedHintRecord> revealedHints = new List<RevealedHintRecord>();
        }

        // In-memory cache for whichever student was last passed to Load(),
        // keyed by structure key (BoneInfo.boneName). Every write here is
        // immediately persisted to disk too - see Persist().
        private readonly Dictionary<string, PlayModeAnswerRecord> _byKey = new Dictionary<string, PlayModeAnswerRecord>();

        // Every hint use for the currently-loaded student, across every
        // system and every day - NOT keyed by structure, since a hint
        // isn't tied to one bone/muscle/vessel the way an answer record is.
        // Kept as a flat list (not a dictionary) since there's no natural
        // single key - TryUseHint/GetHintCountToday filter it by
        // system + "today" on demand. Same file, same Persist() call as
        // _byKey - see RecordListWrapper above.
        private readonly List<HintUseRecord> _hintUses = new List<HintUseRecord>();

        // Every revealed-letter hint position for the currently-loaded
        // student, across every structure - kept flat like _hintUses
        // (not a Dictionary<key, ...>) since one structure can have
        // several revealed indices; GetRevealedIndices filters this by
        // key on demand. This is what lets the letter row restore exactly
        // which letters were already revealed for a structure, whether
        // the student backed out and reselected it or closed and
        // reopened the app entirely - see
        // AnatomyPlayModeController.RestoreRevealedHints. Same file, same
        // Persist() call as _byKey/_hintUses.
        private readonly List<RevealedHintRecord> _revealedHints = new List<RevealedHintRecord>();
        private string _loadedStudentId;

        // Max hints allowed per anatomy system per calendar day (local
        // device time, resetting at midnight) - see TryUseHint. Public so
        // AnatomyPlayModeController can compare against
        // GetHintCountToday() without duplicating the number.
        public const int MaxHintsPerSystemPerDay = 3;

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
            _hintUses.Clear();
            _revealedHints.Clear();
            _loadedStudentId = studentId;

            if (string.IsNullOrEmpty(studentId)) return;

            string path = FilePath(studentId);
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path);
                var wrapper = JsonUtility.FromJson<RecordListWrapper>(json);
                if (wrapper == null) return;

                if (wrapper.records != null)
                {
                    foreach (var r in wrapper.records)
                    {
                        if (r == null || string.IsNullOrEmpty(r.key)) continue;
                        _byKey[r.key] = r;
                    }
                }

                if (wrapper.hintUses != null)
                {
                    foreach (var h in wrapper.hintUses)
                    {
                        if (h == null || string.IsNullOrEmpty(h.timestampUtc)) continue;
                        _hintUses.Add(h);
                    }
                }

                if (wrapper.revealedHints != null)
                {
                    foreach (var r in wrapper.revealedHints)
                    {
                        if (r == null || string.IsNullOrEmpty(r.key)) continue;
                        _revealedHints.Add(r);
                    }
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
                var wrapper = new RecordListWrapper
                {
                    records = _byKey.Values.ToList(),
                    hintUses = _hintUses.ToList(),
                    revealedHints = _revealedHints.ToList()
                };
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
            int pointsEarned)
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

        // ===== Hints (per-system daily limit) =====
        //
        // A hint use is a timestamped event, not a per-structure record -
        // the same student can use hints on many different bones/muscles/
        // vessels in one day, and what's capped is the count per system per
        // day, not per structure. See HintUseRecord and the class comment
        // on _hintUses above.

        // "Today" is a local-device-time calendar day (resets at midnight),
        // NOT a rolling 24-hour window and NOT UTC - matches the product
        // decision to use a fixed midnight reset. DateTime.Parse below
        // parses the stored ISO-8601 UTC timestamp and converts it to local
        // time before comparing dates, so a hint used late at night UTC but
        // already "tomorrow" locally (or vice versa) is bucketed correctly
        // for the student's own device.
        private bool IsSameLocalDay(string timestampUtc, DateTime localNow)
        {
            if (!DateTime.TryParse(timestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc))
                return false;

            DateTime local = utc.ToLocalTime();
            return local.Date == localNow.Date;
        }

        /// <summary>How many hints the currently-loaded student has used on
        /// `system` so far today (local device day) - what
        /// AnatomyPlayModeController checks to decide whether the Hint
        /// button should be enabled, and what TryUseHint checks against
        /// MaxHintsPerSystemPerDay before allowing another one.</summary>
        public int GetHintCountToday(AnatomySystem system)
        {
            DateTime now = DateTime.Now;
            string systemStr = system.ToString();
            int count = 0;
            foreach (var h in _hintUses)
            {
                if (h == null) continue;
                if (h.system != systemStr) continue;
                if (IsSameLocalDay(h.timestampUtc, now)) count++;
            }
            return count;
        }

        /// <summary>Attempts to use one hint for `system` - the offline-
        /// first write: always evaluated and persisted locally regardless
        /// of connectivity (see the class comment). Returns false (and no
        /// record) if the student has already used MaxHintsPerSystemPerDay
        /// hints on this system today; AnatomyPlayModeController must not
        /// reveal a letter when this returns false. On success, returns the
        /// new record so the caller can hand it straight to
        /// AnatomyPlayModeSyncService/AnatomyPlayModeFirebase, same pattern
        /// as SaveAnswer.</summary>
        public bool TryUseHint(string studentId, AnatomySystem system, out HintUseRecord record)
        {
            if (GetHintCountToday(system) >= MaxHintsPerSystemPerDay)
            {
                record = null;
                return false;
            }

            record = new HintUseRecord
            {
                studentId = studentId,
                system = system.ToString(),
                timestampUtc = DateTime.UtcNow.ToString("o"),
                SyncStatus = PlayModeSyncStatus.Pending
            };

            _hintUses.Add(record);
            Persist();
            return true;
        }

        /// <summary>Marks a hint-use record synced once Firebase has
        /// confirmed the write - same reasoning as MarkSynced, matched by
        /// DocumentId since hint uses have no natural single-field key like
        /// PlayModeAnswerRecord.key.</summary>
        public void MarkHintSynced(string documentId)
        {
            var record = _hintUses.FirstOrDefault(h => h != null && h.DocumentId == documentId);
            if (record == null) return;
            record.SyncStatus = PlayModeSyncStatus.Synced;
            Persist();
        }

        /// <summary>Every hint-use record still waiting to reach Firebase -
        /// what AnatomyPlayModeSyncService retries whenever connectivity
        /// returns, same role as GetPendingRecords().</summary>
        public List<HintUseRecord> GetPendingHintUses()
        {
            return _hintUses.Where(h => h != null && h.SyncStatus == PlayModeSyncStatus.Pending).ToList();
        }

        /// <summary>Merges one hint-use record downloaded from Firebase
        /// into local storage - the download half of the Sync Progress
        /// feature for hints, same role as MergeRemoteRecord. A hint use
        /// synced from another device is added as already-synced; an
        /// already-known one (matched by DocumentId) is left as-is other
        /// than reconciling its sync status, since hint uses are never
        /// re-scored or edited after creation.</summary>
        public bool MergeRemoteHintUse(HintUseRecord remote)
        {
            if (remote == null || string.IsNullOrEmpty(remote.timestampUtc)) return false;

            var existing = _hintUses.FirstOrDefault(h => h != null && h.DocumentId == remote.DocumentId);
            if (existing != null)
            {
                if (existing.SyncStatus != PlayModeSyncStatus.Synced)
                {
                    existing.SyncStatus = PlayModeSyncStatus.Synced;
                    Persist();
                }
                return false;
            }

            remote.SyncStatus = PlayModeSyncStatus.Synced;
            _hintUses.Add(remote);
            Persist();
            return true;
        }

        // ===== Revealed hint letters (per-structure restore) =====
        //
        // Separate from the hint-count tracking above: that's "how many
        // hints has this student spent today" (a daily limit check), this
        // is "which specific letters has this student revealed on this
        // specific structure" (what the letter row should show right
        // now). See RevealedHintRecord's class comment for the full
        // reasoning.

        /// <summary>Every DisplayName character index already revealed by
        /// a hint for `key`, so AnatomyPlayModeController can pre-fill the
        /// letter row exactly as it was left - whether the student backed
        /// out and reselected this structure, or closed and reopened the
        /// app/tab entirely. Empty for a structure with no hints used yet.
        /// Deliberately does NOT check GetCompletedKeys() itself - callers
        /// (AnatomyPlayModeController.OnStructureSelected) never call this
        /// for an already-completed structure in the first place, since
        /// completed structures show the answer directly and skip the
        /// guessing UI entirely.</summary>
        public List<int> GetRevealedIndices(string key)
        {
            if (string.IsNullOrEmpty(key)) return new List<int>();
            return _revealedHints
                .Where(h => h != null && h.key == key)
                .Select(h => h.index)
                .Distinct()
                .OrderBy(i => i)
                .ToList();
        }

        /// <summary>Persists one newly-revealed letter position for `key` -
        /// called alongside TryUseHint so the SPECIFIC letter revealed is
        /// saved, not just the daily count. A no-op (returns the existing
        /// record unchanged) if this exact index was already saved for
        /// this key, so a stray double-call can never create a
        /// duplicate.</summary>
        public RevealedHintRecord SaveRevealedIndex(string studentId, string key, int index)
        {
            var existing = _revealedHints.FirstOrDefault(h => h != null && h.key == key && h.index == index);
            if (existing != null) return existing;

            var record = new RevealedHintRecord
            {
                studentId = studentId,
                key = key,
                index = index,
                timestampUtc = DateTime.UtcNow.ToString("o"),
                SyncStatus = PlayModeSyncStatus.Pending
            };

            _revealedHints.Add(record);
            Persist();
            return record;
        }

        /// <summary>Every revealed-hint record still waiting to reach
        /// Firebase - same role as GetPendingRecords()/
        /// GetPendingHintUses().</summary>
        public List<RevealedHintRecord> GetPendingRevealedHints()
        {
            return _revealedHints.Where(h => h != null && h.SyncStatus == PlayModeSyncStatus.Pending).ToList();
        }

        /// <summary>Marks a revealed-hint record synced once Firebase has
        /// confirmed the write - same reasoning as MarkHintSynced, matched
        /// by DocumentId.</summary>
        public void MarkRevealedHintSynced(string documentId)
        {
            var record = _revealedHints.FirstOrDefault(h => h != null && h.DocumentId == documentId);
            if (record == null) return;
            record.SyncStatus = PlayModeSyncStatus.Synced;
            Persist();
        }

        /// <summary>Merges one revealed-hint record downloaded from
        /// Firebase into local storage - the download half of the
        /// cross-device restore, same role as MergeRemoteRecord/
        /// MergeRemoteHintUse. Skipped entirely for a structure this
        /// device already has correctly answered (see GetCompletedKeys) -
        /// a solved structure's revealed letters are no longer meaningful
        /// here, and this doubles as a safety net for any Firestore doc
        /// that AnatomyPlayModeController.CleanupRevealedHints's
        /// best-effort delete didn't reach. Matched by DocumentId, same as
        /// the other two Merge* methods.</summary>
        public bool MergeRemoteRevealedHint(RevealedHintRecord remote)
        {
            if (remote == null || string.IsNullOrEmpty(remote.key)) return false;

            if (_byKey.TryGetValue(remote.key, out var answered) && answered.correct) return false;

            var existing = _revealedHints.FirstOrDefault(h => h != null && h.DocumentId == remote.DocumentId);
            if (existing != null)
            {
                if (existing.SyncStatus != PlayModeSyncStatus.Synced)
                {
                    existing.SyncStatus = PlayModeSyncStatus.Synced;
                    Persist();
                }
                return false;
            }

            remote.SyncStatus = PlayModeSyncStatus.Synced;
            _revealedHints.Add(remote);
            Persist();
            return true;
        }

        /// <summary>Removes every revealed-hint record for `key` from
        /// local storage - called once a structure is correctly answered
        /// (see AnatomyPlayModeController.HandleCorrectAnswer), since a
        /// solved structure is never asked again and there's nothing left
        /// to restore. Local data is deleted here (unlike answer/hint-use
        /// records elsewhere in this class) because, unlike those, keeping
        /// it around serves no purpose once the structure is done. Returns
        /// the removed records so the caller can best-effort delete their
        /// Firestore docs too, keeping the remote collection from growing
        /// forever with orphaned data for already-completed
        /// structures.</summary>
        public List<RevealedHintRecord> ClearRevealedHints(string key)
        {
            if (string.IsNullOrEmpty(key)) return new List<RevealedHintRecord>();

            var removed = _revealedHints.Where(h => h != null && h.key == key).ToList();
            if (removed.Count == 0) return removed;

            _revealedHints.RemoveAll(h => h != null && h.key == key);
            Persist();
            return removed;
        }
    }
}
