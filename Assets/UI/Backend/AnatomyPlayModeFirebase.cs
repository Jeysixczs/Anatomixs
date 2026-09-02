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
    /// Correct, points-earning answers also credit students/{uid}.totalPoints
    /// - the same field QuizService's quiz-scoring writes to - so Play Mode
    /// points count toward the student's overall Total Points/level. See
    /// SaveAnswer and SyncRecord for how each avoids double-crediting.
    ///
    /// Attach anywhere on the persistent Bootstrap GameObject (same one as
    /// FirebaseBootstrap/PlayerSessionManager), and assign it to
    /// AnatomyPlayModeController's "Firebase" field in the Inspector.
    /// </summary>
    public class AnatomyPlayModeFirebase : MonoBehaviour
    {
        private const string CollectionName = "anatomyPlayModeAttempts";
        private const string SyncStatusCollectionName = "anatomyPlayModeSyncStatus";

        private FirebaseFirestore Db => FirebaseBootstrap.Instance != null ? FirebaseBootstrap.Instance.Db : null;

        /// <summary>Saves one Play Mode attempt and, if it's a correct
        /// answer that earned points, credits those points to the
        /// student's students/{uid}.totalPoints in the SAME atomic batch -
        /// the same field QuizService.SubmitQuizAttemptInternal adds quiz
        /// points to, so Play Mode points show up in Total Points/level
        /// everywhere that field is already read (dashboard, leaderboard,
        /// etc.) with no separate reconciliation step. PlayerSessionManager's
        /// existing students/{uid} listener (see StartStudentListener)
        /// picks up the change automatically - nothing here needs to poke
        /// CurrentStudent directly.
        ///
        /// This method has no retry path (it's only used for incorrect
        /// attempts, and for correct-answer fallback when no local storage
        /// is assigned - see AnatomyPlayModeController.SaveCorrectAnswer),
        /// so unlike SyncRecord below it doesn't need an idempotency guard
        /// against being called twice for the same attempt.</summary>
        public void SaveAnswer(
            string key,
            string displayName,
            bool correct,
            int hintsUsed,
            int pointsEarned)
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
            var answerData = new Dictionary<string, object>
            {
                { "studentId", student.Uid },
                { "key", key },
                { "displayName", displayName },
                { "correct", correct },
                { "hintsUsed", hintsUsed },
                { "pointsEarned", pointsEarned },
                { "timestamp", Timestamp.GetCurrentTimestamp() }
            };

            bool awardsPoints = correct && pointsEarned > 0;
            if (!awardsPoints)
            {
                docRef.SetAsync(answerData).ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not save Play Mode answer for '{key}': {task.Exception}");
                    }
                });
                return;
            }

            var batch = Db.StartBatch();
            batch.Set(docRef, answerData);
            batch.Update(Db.Collection("students").Document(student.Uid), "totalPoints", FieldValue.Increment(pointsEarned));
            batch.CommitAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not save Play Mode answer/points for '{key}': {task.Exception}");
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
        /// it's safe to mark the local record 'synced'.
        ///
        /// If the record is a correct, points-earning answer, this also
        /// credits pointsEarned to students/{uid}.totalPoints - the same
        /// field QuizService adds quiz points to, so Play Mode points feed
        /// Total Points/level everywhere that's already read, and
        /// PlayerSessionManager's live students/{uid} listener picks up the
        /// change with no extra wiring needed here.
        ///
        /// Crediting points is wrapped in the SAME transaction as the
        /// existence check below, NOT a plain increment, because - unlike
        /// the document overwrite this method is named for - "+= points"
        /// is not naturally safe to repeat. A retry (e.g. the write
        /// actually reached Firestore but the app lost connectivity before
        /// hearing back, so the sync service reasonably retries later)
        /// must overwrite the same document without crediting the points a
        /// second time. Reading the document inside the transaction first
        /// and only incrementing when it doesn't already exist gives that
        /// guarantee: the very first successful write for a given
        /// student+structure awards the points, and every write after that
        /// (retry or otherwise) is a no-op on points, exactly mirroring the
        /// "overwrites rather than duplicates" guarantee the deterministic
        /// DocumentId already gives the answer doc itself.</summary>
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
            var answerData = new Dictionary<string, object>
            {
                { "studentId", record.studentId },
                { "key", record.key },
                { "displayName", record.displayName },
                { "correct", record.correct },
                { "hintsUsed", record.hintsUsed },
                { "pointsEarned", record.pointsEarned },
                { "timestamp", Timestamp.GetCurrentTimestamp() }
            };

            bool awardsPoints = record.correct && record.pointsEarned > 0 && !string.IsNullOrEmpty(record.studentId);
            if (!awardsPoints)
            {
                docRef.SetAsync(answerData).ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not sync Play Mode record for '{record.key}': {task.Exception}");
                        onComplete?.Invoke(false);
                        return;
                    }

                    onComplete?.Invoke(true);
                });
                return;
            }

            var studentRef = Db.Collection("students").Document(record.studentId);

            Db.RunTransactionAsync(async transaction =>
            {
                var existingSnap = await transaction.GetSnapshotAsync(docRef);

                transaction.Set(docRef, answerData);

                if (!existingSnap.Exists)
                {
                    // First time this student+structure has ever been
                    // written - safe to credit the points exactly once.
                    transaction.Update(studentRef, "totalPoints", FieldValue.Increment(record.pointsEarned));
                }

                return true;
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not sync Play Mode record/points for '{record.key}': {task.Exception}");
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

        /// <summary>Records the UTC time this device just finished a fully-
        /// successful sync, in a tiny per-student doc separate from the
        /// attempt records above - so any device signed into the same
        /// account can show an accurate "Last synced" time, not just
        /// whatever a single device remembers locally (see the plan's
        /// cross-device sync-status follow-up). Only
        /// AnatomyPlayModeSyncService should call this, right after a sync
        /// it ran itself actually completed. Fire-and-forget from the
        /// sync flow's point of view: a failure here never fails or blocks
        /// the sync that just happened, it only means other devices won't
        /// see this particular timestamp until the next successful
        /// write.</summary>
        public void UpdateLastSyncedTimestamp(string studentId, DateTime utcTime, Action<bool> onComplete = null)
        {
            if (Db == null)
            {
                Debug.LogWarning("[AnatomyPlayModeFirebase] Firebase not ready - skipping last-synced update.");
                onComplete?.Invoke(false);
                return;
            }

            if (string.IsNullOrEmpty(studentId))
            {
                onComplete?.Invoke(false);
                return;
            }

            var docRef = Db.Collection(SyncStatusCollectionName).Document(studentId);
            docRef.SetAsync(new Dictionary<string, object>
            {
                { "studentId", studentId },
                { "lastSyncedUtc", Timestamp.FromDateTime(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)) }
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[AnatomyPlayModeFirebase] Could not update last-synced timestamp for '{studentId}': {task.Exception}");
                    onComplete?.Invoke(false);
                    return;
                }

                onComplete?.Invoke(true);
            });
        }

        /// <summary>Reads the most recent "last synced" time any device has
        /// recorded for studentId - so a device that's behind (freshly
        /// installed, or hasn't synced in a while) can reflect sync
        /// activity that happened elsewhere instead of only its own local
        /// history. Purely informational: always calls onComplete, with
        /// null (never an error) if there's no record yet or the read
        /// fails, so callers can safely fold it into their own status
        /// without special-casing failures.</summary>
        public void FetchLastSyncedTimestamp(string studentId, Action<DateTime?> onComplete)
        {
            if (Db == null || string.IsNullOrEmpty(studentId))
            {
                onComplete?.Invoke(null);
                return;
            }

            Db.Collection(SyncStatusCollectionName).Document(studentId).GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                    {
                        onComplete?.Invoke(null);
                        return;
                    }

                    var snapshot = task.Result;
                    if (!snapshot.ContainsField("lastSyncedUtc"))
                    {
                        onComplete?.Invoke(null);
                        return;
                    }

                    var ts = snapshot.GetValue<Timestamp>("lastSyncedUtc");
                    onComplete?.Invoke(ts.ToDateTime());
                });
        }
    }
}