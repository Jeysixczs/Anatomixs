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
}
