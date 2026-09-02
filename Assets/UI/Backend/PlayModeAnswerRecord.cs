using System;
using System.Text;

namespace Anatomia3D.Backend
{
    public enum PlayModeSyncStatus
    {
        Pending,
        Synced
    }

    /// <summary>
    /// One locally-stored Play Mode answer. This is the record type shared
    /// by AnatomyPlayModeLocalStorage (which owns reading/writing it to
    /// disk), AnatomyPlayModeFirebase (which uploads it to Firestore under
    /// DocumentId), and AnatomyPlayModeSyncService (which shuttles pending
    /// ones between the two).
    ///
    /// [Serializable] + only primitive/string fields so JsonUtility can
    /// (de)serialize it directly - see AnatomyPlayModeLocalStorage.
    /// </summary>
    [Serializable]
    public class PlayModeAnswerRecord
    {
        public string studentId;
        public string key;          // BoneInfo.boneName / BoneDatabaseEntry.boneId - unique identifier, never baseName.
        public string displayName;
        public bool correct;
        public int hintsUsed;
        public int pointsEarned;
        public string timestampUtc; // ISO-8601 (DateTime.UtcNow.ToString("o"))

        // Stored as a string (not the enum) purely so JsonUtility can
        // (de)serialize it without extra converter plumbing.
        public string syncStatus = PlayModeSyncStatus.Pending.ToString();

        public PlayModeSyncStatus SyncStatus
        {
            get => Enum.TryParse<PlayModeSyncStatus>(syncStatus, out var parsed) ? parsed : PlayModeSyncStatus.Pending;
            set => syncStatus = value.ToString();
        }

        /// <summary>Deterministic Firestore document ID for this record -
        /// the same studentId+key always maps to the same ID, so retrying a
        /// sync updates the existing Firebase document instead of creating
        /// a duplicate (see AnatomyPlayModeFirebase.SyncRecord and the
        /// plan's section 4).</summary>
        public string DocumentId => BuildDocumentId(studentId, key);

        public static string BuildDocumentId(string studentId, string key)
        {
            string raw = $"{studentId}_{key}";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');

            string id = sb.Length > 0 ? sb.ToString() : "unknown";

            // Firestore reserves document IDs starting and ending with "__".
            if (id.StartsWith("__") && id.EndsWith("__"))
                id = "id_" + id;

            return id;
        }
    }

    /// <summary>
    /// One locally-stored hint use, for the per-anatomy-system daily hint
    /// limit (3 hints per system per student per day - see
    /// AnatomyPlayModeLocalStorage.TryUseHint). Shares the same
    /// storage/sync/upload pattern as PlayModeAnswerRecord: owned on disk
    /// by AnatomyPlayModeLocalStorage, uploaded by
    /// AnatomyPlayModeFirebase.SyncHintUse, shuttled by
    /// AnatomyPlayModeSyncService.
    ///
    /// Unlike PlayModeAnswerRecord, a HintUseRecord is never re-scored or
    /// re-evaluated after creation - it's just a fact ("this hint
    /// happened") - so there's no correct/pointsEarned/hintsUsed fields
    /// here, only enough to identify who used a hint, on what system, and
    /// when.
    /// </summary>
    [Serializable]
    public class HintUseRecord
    {
        public string studentId;
        public string system;       // AnatomySystem.ToString() - e.g. "Skeletal", "Muscular", "Cardiovascular".
        public string timestampUtc; // ISO-8601 (DateTime.UtcNow.ToString("o"))

        // Stored as a string, same reasoning as PlayModeAnswerRecord.syncStatus.
        public string syncStatus = PlayModeSyncStatus.Pending.ToString();

        // Populated ONLY when this record was downloaded from Firebase
        // (see AnatomyPlayModeFirebase.FetchTodayHintUses) - holds the
        // ACTUAL Firestore document ID (doc.Id) for this hint use, exactly
        // as it was written. Left null/empty for locally-created records
        // (AnatomyPlayModeLocalStorage.TryUseHint), which have no Firestore
        // doc yet and rely on DocumentId being recomputed from fields below.
        //
        // This exists because recomputing BuildDocumentId(studentId, system,
        // timestampUtc) after a round trip through Firestore's Timestamp
        // type is NOT guaranteed to reproduce the original ID string: a
        // Firestore Timestamp's nanosecond value doesn't always map back
        // to the exact same .NET DateTime tick it started as, so
        // timestampUtc.ToString("o") can differ from the original by a
        // single 100ns tick. That was silently turning every downloaded
        // hint use into a "new" record instead of the same one already
        // stored locally, doubling (then quadrupling) the day's hint count
        // every time Sync Progress ran. Trusting the real doc.Id sidesteps
        // the round-trip entirely: it's the exact same ID used to write
        // the document in the first place, so it always matches.
        public string remoteDocumentId;

        public PlayModeSyncStatus SyncStatus
        {
            get => Enum.TryParse<PlayModeSyncStatus>(syncStatus, out var parsed) ? parsed : PlayModeSyncStatus.Pending;
            set => syncStatus = value.ToString();
        }

        /// <summary>Deterministic Firestore document ID for this hint use.
        /// Prefers remoteDocumentId (the real doc.Id from Firestore) when
        /// present - i.e. for anything downloaded via FetchTodayHintUses -
        /// so merge/dedup matching is never at the mercy of Timestamp
        /// round-trip precision. Falls back to recomputing from
        /// studentId+system+timestampUtc for locally-created records that
        /// have no Firestore doc yet (same reasoning as
        /// PlayModeAnswerRecord.DocumentId), which is also exactly the ID
        /// SyncHintUse uses the first time this record is uploaded.</summary>
        public string DocumentId => !string.IsNullOrEmpty(remoteDocumentId)
            ? remoteDocumentId
            : BuildDocumentId(studentId, system, timestampUtc);

        public static string BuildDocumentId(string studentId, string system, string timestampUtc)
        {
            string raw = $"{studentId}_{system}_{timestampUtc}";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');

            string id = sb.Length > 0 ? sb.ToString() : "unknown";

            if (id.StartsWith("__") && id.EndsWith("__"))
                id = "id_" + id;

            return id;
        }
    }

    /// <summary>
    /// One revealed-letter hint position for a specific structure -
    /// distinct from HintUseRecord, which only records THAT a hint was
    /// spent (for the daily per-system limit). This records WHICH
    /// DisplayName character index that hint revealed, on WHICH
    /// structure, so AnatomyPlayModeController can restore the letter row
    /// exactly as it was left: when the student backs out and reselects
    /// the same still-unanswered structure, and when they close and
    /// reopen the app/tab entirely (see AnatomyPlayModeController.
    /// RestoreRevealedHints). A structure typically has several of these,
    /// one per revealed index.
    ///
    /// Shares the same owned-by-local-storage/uploaded-by-Firebase/
    /// shuttled-by-sync-service split as PlayModeAnswerRecord and
    /// HintUseRecord: AnatomyPlayModeLocalStorage owns it on disk,
    /// AnatomyPlayModeFirebase.SyncRevealedHint/FetchRevealedHints/
    /// DeleteRevealedHint move it to/from Firestore, and
    /// AnatomyPlayModeSyncService shuttles pending ones and merges
    /// downloaded ones - the same pattern that lets a student's revealed
    /// hints restore correctly on a different device, not just this one.
    /// </summary>
    [Serializable]
    public class RevealedHintRecord
    {
        public string studentId;
        public string key;   // BoneInfo.boneName / BoneDatabaseEntry.boneId - same identity rule as PlayModeAnswerRecord.key.
        public int index;    // DisplayName character index revealed - matches AnatomyPlayModeController._revealedIndices.
        public string timestampUtc; // ISO-8601 (DateTime.UtcNow.ToString("o")) - informational only, never used for matching.

        // Stored as a string, same reasoning as the other two records' syncStatus.
        public string syncStatus = PlayModeSyncStatus.Pending.ToString();

        // Populated ONLY when downloaded from Firebase (see
        // AnatomyPlayModeFirebase.FetchRevealedHints) - the real Firestore
        // document ID, same remoteDocumentId pattern HintUseRecord uses.
        // Not strictly required here the way it was for HintUseRecord
        // (studentId+key+index has no floating-point/Timestamp precision
        // to lose on a round trip - an int survives exactly), but kept for
        // consistency and as a safety net if this record ever grows a
        // Timestamp-based field later.
        public string remoteDocumentId;

        public PlayModeSyncStatus SyncStatus
        {
            get => Enum.TryParse<PlayModeSyncStatus>(syncStatus, out var parsed) ? parsed : PlayModeSyncStatus.Pending;
            set => syncStatus = value.ToString();
        }

        /// <summary>Deterministic Firestore document ID - studentId+key+
        /// index always maps to the same ID, since a given structure's Nth
        /// revealed letter position is a fixed fact once spent (never
        /// re-revealed), so a sync retry overwrites the same document
        /// instead of duplicating it. Prefers remoteDocumentId when set,
        /// same reasoning as HintUseRecord.DocumentId.</summary>
        public string DocumentId => !string.IsNullOrEmpty(remoteDocumentId)
            ? remoteDocumentId
            : BuildDocumentId(studentId, key, index);

        public static string BuildDocumentId(string studentId, string key, int index)
        {
            string raw = $"{studentId}_{key}_{index}";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');

            string id = sb.Length > 0 ? sb.ToString() : "unknown";

            if (id.StartsWith("__") && id.EndsWith("__"))
                id = "id_" + id;

            return id;
        }
    }
}
