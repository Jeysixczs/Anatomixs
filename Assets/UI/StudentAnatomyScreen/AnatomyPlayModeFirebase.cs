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
    /// do in QuizService.cs. AnatomyPlayModeController calls SaveAnswer;
    /// nothing else should call into this class.
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

        /// <summary>
        /// Fire-and-forget save of one answered (or attempted) question.
        /// key/displayName identify the anatomical structure - key must be
        /// BoneInfo.boneName / BoneDatabaseEntry.boneId, displayName must be
        /// BoneDatabaseEntry.displayName. baseName is never part of this record.
        /// </summary>
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
    }
}
