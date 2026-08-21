using System;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Saves Student Explore 3D -> Play Mode results to Firestore. This
    /// script contains NO UI logic and NO guessing-game logic - it only
    /// writes documents, the same way RecordMissedAttempt/RecordBadgeAwards
    /// do in QuizService.cs. AnatomyPlayModeController calls SaveAnswer
    /// directly for one-off/incorrect attempts; AnatomyPlayModeSyncService
    /// calls SyncRecord for locally-queued offline answers that need a
    /// stable document ID (see the plan's section 4). Nothing else should
    /// call into this class.
    ///
    /// Reuses the project's existing Firebase setup: FirebaseBootstrap for
    /// the Firestore instance, PlayerSessionManager for the signed-in
    /// student's uid - never its own auth or its own Firestore instance.
    ///
    /// Attach anywhere on the persistent Bootstrap GameObject (same one as
    /// FirebaseBootstrap/PlayerSessionManager), and assign it to
    /// AnatomyPlayModeController's "Firebase" field in the Inspector.
    /// </summary>
    public class AnatomyPlayModeFirebase : MonoBehaviour
    {
        private const string CollectionName = "anatomyPlayModeAttempts";

        private FirebaseFirestore Db => FirebaseBootstrap.Instance != null ? FirebaseBootstrap.Instance.Db : null;

        public void SaveAnswer(
            string key,
            string displayName,
            bool correct,
            int hintsUsed,
            int pointsEarned,
            int streak,
            int streakBonus)
        {
            if (Db == null)
            {
                Debug.LogWarning("[AnatomyPlayModeFirebase] Firebase not ready - skipping save.");
                return;
            }

            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null)
            {
                Debug.LogWarning("[AnatomyPlayModeFirebase] No signed-in student - skipping save.");
                return;
            }

            var docRef = Db.Collection(CollectionName).Document();
            docRef.SetAsync(new Dictionary<string, object>
            {
                { "studentId", student.Uid },
                { "key", key },
                { "displayName", displayName },
                { "correct", correct },
                { "hintsUsed", hintsUsed },
                { "pointsEarned", pointsEarned },
                { "streak", streak },
                { "streakBonus", streakBonus },
                { "timestamp", Timestamp.GetCurrentTimestamp() }
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not save Play Mode answer for '{key}': {task.Exception}");
                }
            });
        }

        /// <summary>Uploads a locally-queued PlayModeAnswerRecord using its
        /// deterministic DocumentId (studentId+key) instead of an
        /// auto-generated one. Because the document ID is always the same
        /// for a given student+structure, calling this again for the same
        /// record - e.g. a sync retry after a dropped connection - simply
        /// overwrites the same Firestore document rather than creating a
        /// duplicate (see the plan's section 4). Only
        /// AnatomyPlayModeSyncService should call this.
        ///
        /// onComplete is invoked with true only once Firestore confirms the
        /// write; the sync service uses that (and only that) to decide when
        /// it's safe to mark the local record 'synced'.</summary>
        public void SyncRecord(PlayModeAnswerRecord record, Action<bool> onComplete)
        {
            if (record == null || string.IsNullOrEmpty(record.key))
            {
                onComplete?.Invoke(false);
                return;
            }

            if (Db == null)
            {
                Debug.LogWarning($"[AnatomyPlayModeFirebase] Firebase not ready - cannot sync '{record.key}' yet.");
                onComplete?.Invoke(false);
                return;
            }

            var docRef = Db.Collection(CollectionName).Document(record.DocumentId);
            docRef.SetAsync(new Dictionary<string, object>
            {
                { "studentId", record.studentId },
                { "key", record.key },
                { "displayName", record.displayName },
                { "correct", record.correct },
                { "hintsUsed", record.hintsUsed },
                { "pointsEarned", record.pointsEarned },
                { "streak", record.streak },
                { "streakBonus", record.streakBonus },
                { "timestamp", Timestamp.GetCurrentTimestamp() }
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not sync Play Mode record for '{record.key}': {task.Exception}");
                    onComplete?.Invoke(false);
                    return;
                }

                onComplete?.Invoke(true);
            });
        }

        /// <summary>Downloads every correctly-answered record Firebase has for
        /// studentId - used by the Sync Progress feature to pull down progress
        /// made on another device (or before local storage existed on this
        /// one) and merge it into AnatomyPlayModeLocalStorage. Read-only: this
        /// never writes anything, and never touches local storage itself -
        /// see AnatomyPlayModeSyncService.RequestFullSync, which is the only
        /// caller and owns the merge step.
        ///
        /// Only correct==true documents are requested - Play Mode never
        /// needs to restore incorrect attempts, and skipping them keeps the
        /// downloaded set exactly matched to what GetCompletedKeys() expects
        /// to merge against.
        ///
        /// Note: this query (studentId ==, correct ==) may require a
        /// composite index the first time it runs - Firestore's error
        /// message (surfaced via task.Exception below, visible in the
        /// Console log) includes a direct link to create it.</summary>
        public void FetchProgress(string studentId, Action<List<PlayModeAnswerRecord>> onComplete, Action<string> onError = null)
        {
            if (Db == null)
            {
                Debug.LogWarning("[AnatomyPlayModeFirebase] Firebase not ready - cannot fetch progress.");
                onError?.Invoke("Firebase is not ready yet.");
                return;
            }

            if (string.IsNullOrEmpty(studentId))
            {
                onError?.Invoke("No signed-in student.");
                return;
            }

            Db.Collection(CollectionName)
                .WhereEqualTo("studentId", studentId)
                .WhereEqualTo("correct", true)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not fetch progress for '{studentId}': {task.Exception}");
                        onError?.Invoke("Could not reach Firebase.");
                        return;
                    }

                    var records = new List<PlayModeAnswerRecord>();
                    foreach (var doc in task.Result.Documents)
                    {
                        if (!doc.Exists) continue;

                        string key = GetString(doc, "key");
                        if (string.IsNullOrEmpty(key)) continue; // malformed doc - skip rather than crash the merge.

                        var record = new PlayModeAnswerRecord
                        {
                            studentId = GetString(doc, "studentId") ?? studentId,
                            key = key,
                            displayName = GetString(doc, "displayName") ?? key,
                            correct = true,
                            hintsUsed = GetInt(doc, "hintsUsed"),
                            pointsEarned = GetInt(doc, "pointsEarned"),
                            streak = GetInt(doc, "streak"),
                            streakBonus = GetInt(doc, "streakBonus"),
                            timestampUtc = DateTime.UtcNow.ToString("o")
                        };
                        record.SyncStatus = PlayModeSyncStatus.Synced; // it came FROM Firebase - it's synced by definition.

                        records.Add(record);
                    }

                    onComplete?.Invoke(records);
                });
        }

        private static string GetString(DocumentSnapshot doc, string field) =>
            doc.ContainsField(field) ? doc.GetValue<string>(field) : null;

        private static int GetInt(DocumentSnapshot doc, string field) =>
            doc.ContainsField(field) ? doc.GetValue<int>(field) : 0;
    }
}
