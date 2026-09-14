using System;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    public enum BaselineAssessmentType
    {
        Pretest,
        Posttest
    }

    /// <summary>Result of a finished baseline assessment attempt, passed back to
    /// whoever asked (BaselineAssessmentController) after RecordAttempt succeeds.</summary>
    public struct BaselineAssessmentResult
    {
        public int CorrectCount;
        public int TotalCount;
        public int Points;
    }

    /// <summary>
    /// Reads/writes the student's one-time Pretest/Posttest baseline assessment
    /// results. Contains NO UI and NO gameplay logic - it only reads/writes a
    /// single map field on students/{uid}, the same "one Firestore call per
    /// concern" split AnatomyPlayModeFirebase and QuizService already use.
    ///
    /// Deliberately separate from AnatomyPlayModeFirebase/anatomyPlayModeAttempts:
    /// a baseline attempt is a one-shot, fixed-question formal assessment, not an
    /// ordinary Play Mode session, and must never be mixed with or count toward
    /// the student's regular Play Mode progress, points, or completed-structures
    /// list (see AnatomyPlayModeController's _isBaselineMode comment).
    ///
    /// Storage shape - students/{uid}.baselineAssessment:
    ///   {
    ///     pretestCompleted: bool,  pretestCorrect: int,  pretestTotal: int,  pretestPoints: int,  pretestCompletedAtUtc: string (ISO-8601),
    ///     posttestCompleted: bool, posttestCorrect: int, posttestTotal: int, posttestPoints: int, posttestCompletedAtUtc: string (ISO-8601)
    ///   }
    /// A single map field (rather than a subcollection) keeps "has this student
    /// done their pretest yet" a one-document read - the same document
    /// PlayerSessionManager's students/{uid} listener already has open - instead
    /// of a second round trip.
    /// </summary>
    public class BaselineAssessmentService : MonoBehaviour
    {
        // Singleton, same pattern as PlayerSessionManager/FirebaseBootstrap - needed
        // because both UIManager (the pretest gate in ShowStudentDashboard) and
        // BaselineAssessmentController (recording the finished attempt) need to
        // reach the same instance, and a persistent Bootstrap-object singleton is
        // less error-prone than wiring the same reference into two separate
        // Inspector fields by hand.
        public static BaselineAssessmentService Instance { get; private set; }

        private const string StudentsCollection = "students";
        private const string FieldName = "baselineAssessment";

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private FirebaseFirestore Db => FirebaseBootstrap.Instance != null ? FirebaseBootstrap.Instance.Db : null;

        private static string CurrentStudentId =>
            PlayerSessionManager.Instance != null && PlayerSessionManager.Instance.CurrentStudent != null
                ? PlayerSessionManager.Instance.CurrentStudent.Uid
                : null;

        /// <summary>Reads whether the signed-in student has completed the pretest
        /// and/or posttest yet. Calls onResult(pretestDone, posttestDone) exactly
        /// once; both false if there's no signed-in student, Firebase isn't
        /// ready, or the field simply doesn't exist yet (a student who has never
        /// taken either).</summary>
        public void GetStatus(Action<bool, bool> onResult)
        {
            string studentId = CurrentStudentId;
            if (Db == null || string.IsNullOrEmpty(studentId))
            {
                onResult?.Invoke(false, false);
                return;
            }

            Db.Collection(StudentsCollection).Document(studentId).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled || !task.Result.Exists)
                {
                    onResult?.Invoke(false, false);
                    return;
                }

                if (!task.Result.TryGetValue<Dictionary<string, object>>(FieldName, out var map) || map == null)
                {
                    onResult?.Invoke(false, false);
                    return;
                }

                bool pretestDone = map.TryGetValue("pretestCompleted", out var p) && p is bool pb && pb;
                bool posttestDone = map.TryGetValue("posttestCompleted", out var q) && q is bool qb && qb;
                Debug.Log($"[BaselineAssessmentService] GetStatus: pretestDone={pretestDone}, posttestDone={posttestDone}");
                onResult?.Invoke(pretestDone, posttestDone);
            });
        }

        /// <summary>Reads the full pretest/posttest record for the signed-in
        /// student - used by the Progress screen's before/after comparison.
        /// Calls onResult exactly once with a default (all-false/zero) result if
        /// there's no signed-in student, Firebase isn't ready, or the student
        /// hasn't taken either assessment yet. Separate from the lightweight
        /// GetStatus(bool,bool) above, which UIManager's pretest gate uses and
        /// doesn't need score detail for.</summary>
        public void GetFullResult(Action<bool, BaselineAssessmentResult, bool, BaselineAssessmentResult> onResult)
        {
            string studentId = CurrentStudentId;
            var empty = new BaselineAssessmentResult();

            if (Db == null || string.IsNullOrEmpty(studentId))
            {
                onResult?.Invoke(false, empty, false, empty);
                return;
            }

            Db.Collection(StudentsCollection).Document(studentId).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled || !task.Result.Exists ||
                    !task.Result.TryGetValue<Dictionary<string, object>>(FieldName, out var map) || map == null)
                {
                    onResult?.Invoke(false, empty, false, empty);
                    return;
                }

                bool pretestDone = map.TryGetValue("pretestCompleted", out var p) && p is bool pb && pb;
                bool posttestDone = map.TryGetValue("posttestCompleted", out var q) && q is bool qb && qb;

                var pretestResult = pretestDone ? ReadResult(map, "pretest") : empty;
                var posttestResult = posttestDone ? ReadResult(map, "posttest") : empty;

                onResult?.Invoke(pretestDone, pretestResult, posttestDone, posttestResult);
            });
        }

        private static BaselineAssessmentResult ReadResult(Dictionary<string, object> map, string prefix)
        {
            return new BaselineAssessmentResult
            {
                CorrectCount = map.TryGetValue(prefix + "Correct", out var c) ? Convert.ToInt32(c) : 0,
                TotalCount = map.TryGetValue(prefix + "Total", out var t) ? Convert.ToInt32(t) : 0,
                Points = map.TryGetValue(prefix + "Points", out var pts) ? Convert.ToInt32(pts) : 0
            };
        }

        /// <summary>Records a finished attempt. Refuses (calls onComplete(false))
        /// if that assessment type was already completed - the one-shot rule that
        /// keeps the pretest/posttest comparison valid for a capstone (see the
        /// plan discussion: no retakes). Always re-checks the CURRENT server
        /// value in a transaction rather than trusting whatever GetStatus last
        /// returned, so two rapid submits (e.g. a double-tap) can't both
        /// succeed.</summary>
        public void RecordAttempt(BaselineAssessmentType type, int correctCount, int totalCount, int points, Action<bool> onComplete)
        {
            string studentId = CurrentStudentId;
            if (Db == null || string.IsNullOrEmpty(studentId))
            {
                Debug.LogWarning("[BaselineAssessmentService] Firebase not ready or no signed-in student - cannot record attempt.");
                onComplete?.Invoke(false);
                return;
            }

            var docRef = Db.Collection(StudentsCollection).Document(studentId);
            string completedField = type == BaselineAssessmentType.Pretest ? "pretestCompleted" : "posttestCompleted";
            string correctField = type == BaselineAssessmentType.Pretest ? "pretestCorrect" : "posttestCorrect";
            string totalField = type == BaselineAssessmentType.Pretest ? "pretestTotal" : "posttestTotal";
            string pointsField = type == BaselineAssessmentType.Pretest ? "pretestPoints" : "posttestPoints";
            string completedAtField = type == BaselineAssessmentType.Pretest ? "pretestCompletedAtUtc" : "posttestCompletedAtUtc";

            Db.RunTransactionAsync(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(docRef);

                Dictionary<string, object> existingMap = null;
                bool alreadyCompleted = false;
                if (snapshot.Exists &&
                    snapshot.TryGetValue<Dictionary<string, object>>(FieldName, out existingMap) &&
                    existingMap != null &&
                    existingMap.TryGetValue(completedField, out var existingVal) &&
                    existingVal is bool existingBool)
                {
                    alreadyCompleted = existingBool;
                }

                if (alreadyCompleted)
                {
                    Debug.LogWarning($"[BaselineAssessmentService] RecordAttempt({type}) refused - already completed.");
                    return false; // signal "refused" back through the task result below
                }

                // Start from whatever's already in the map (e.g. the OTHER
                // assessment type's fields, recorded earlier) and layer this
                // attempt's fields on top - transaction.Set with MergeAll only
                // merges at the granularity of the field paths given here
                // ("baselineAssessment" as a whole), so passing just this
                // attempt's 5 fields would silently overwrite/erase whatever
                // else already lived in that nested map.
                var updatedMap = existingMap != null
                    ? new Dictionary<string, object>(existingMap)
                    : new Dictionary<string, object>();

                updatedMap[completedField] = true;
                updatedMap[correctField] = correctCount;
                updatedMap[totalField] = totalCount;
                updatedMap[pointsField] = points;
                updatedMap[completedAtField] = DateTime.UtcNow.ToString("o");

                transaction.Set(docRef, new Dictionary<string, object> { { FieldName, updatedMap } },
                    SetOptions.MergeAll);

                return true;
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    Debug.LogWarning($"[BaselineAssessmentService] Failed to record {type} attempt: {task.Exception}");
                    onComplete?.Invoke(false);
                    return;
                }

                onComplete?.Invoke(task.Result);
                Debug.Log($"[BaselineAssessmentService] RecordAttempt({type}) transaction result: {task.Result}");

            });
        }
    }
}
