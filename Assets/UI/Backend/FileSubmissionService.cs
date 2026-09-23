using System;
using System.Collections.Generic;
using System.Linq;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Firestore side of the File Submission assignment type. The file BYTES are
    /// never touched here - those go to Cloudflare R2 through the Worker (see
    /// R2FileUploadService). This service owns only the metadata and the
    /// grading, in a new `fileSubmissions` collection:
    ///
    ///   fileSubmissions/{submissionId}
    ///     quizId, quizTitle, classroomId, studentId, studentName, teacherId,
    ///     attemptNumber, fileName, fileExtension, mimeType, fileSize,
    ///     storageKey, submittedAt, status, score, feedback, reviewedAt,
    ///     reviewedBy, attemptDocId
    ///
    /// Why a separate collection rather than more fields on `quizAttempts`: an
    /// ungraded submission has no score yet, and `quizAttempts` docs are read by
    /// FetchProgressData / FetchClassroomReportData / FetchMyScores as finished,
    /// scored results. Dropping score-less docs in there would quietly skew every
    /// one of those aggregates. Instead, a `quizAttempts` doc is written the
    /// moment the teacher actually grades the submission (see ReviewSubmission),
    /// which is the point at which it really is a scored result - so file
    /// assignments flow into the existing scores/analytics/leaderboard system
    /// with no changes to any of those readers.
    ///
    /// Attempt + deadline rules reuse the quiz doc's own `maxAttempts` /
    /// `isDeadlineEnabled` / `deadline`, so a file assignment behaves exactly
    /// like every other quiz (see CheckSubmitEligibility, which mirrors
    /// QuizService.CheckAttemptEligibility's priority: deadline first, then
    /// attempts).
    ///
    /// Attach to the same persistent Bootstrap GameObject as FirebaseBootstrap /
    /// QuizService / ClassroomService.
    /// </summary>
    public class FileSubmissionService : MonoBehaviour
    {
        public static FileSubmissionService Instance { get; private set; }

        public const string StatusSubmitted = "Submitted";
        public const string StatusReviewed = "Reviewed";

        private const string SubmissionsCollection = "fileSubmissions";

        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        [Serializable]
        public class SubmissionRecord
        {
            public string SubmissionId;
            public string QuizId;
            public string QuizTitle;
            public string ClassroomId;
            public string StudentId;
            public string StudentName;
            public string TeacherId;

            public int AttemptNumber;

            public string FileName;
            public string FileExtension;
            public string MimeType;
            public long FileSize;
            /// <summary>R2 object key, exactly as the Worker reported it.</summary>
            public string StorageKey;

            public DateTime SubmittedAtUtc;

            /// <summary>StatusSubmitted or StatusReviewed.</summary>
            public string Status = StatusSubmitted;

            public int Score;
            public string Feedback;
            public DateTime? ReviewedAtUtc;
            public string ReviewedBy;

            /// <summary>The `quizAttempts` doc this submission's grade was written
            /// into, set the first time it's reviewed. Re-grading updates that same
            /// doc instead of creating a second attempt row.</summary>
            public string AttemptDocId;

            public bool IsReviewed => Status == StatusReviewed;
        }

        /// <summary>Pre-flight result, same shape/priority as
        /// QuizService.AttemptEligibility so the two read alike at call sites.</summary>
        [Serializable]
        public class SubmitEligibility
        {
            public bool CanSubmit;
            public string BlockReason;
            public int AttemptsUsed;
            /// <summary>0 = unlimited.</summary>
            public int MaxAttempts;
            /// <summary>-1 = unlimited.</summary>
            public int RemainingAttempts;
            public bool DeadlinePassed;
            /// <summary>The student's most recent submission for this quiz in this
            /// classroom, or null if they've never submitted.</summary>
            public SubmissionRecord LatestSubmission;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ==================================================================
        // Ids
        // ==================================================================

        /// <summary>Reserves the submission's Firestore doc id up front, so the R2
        /// object key and the `fileSubmissions` doc can be created with the same
        /// id and stay one-to-one even if the metadata write later fails.</summary>
        public string NewSubmissionId() => Db.Collection(SubmissionsCollection).Document().Id;

        // ==================================================================
        // Student: eligibility + submit
        // ==================================================================

        /// <summary>
        /// Call before showing the submit button (and again right before writing
        /// the submission). Checks the quiz's deadline and the student's used
        /// attempts against `maxAttempts`, scoped to this classroom - the same
        /// quiz can be published into several classrooms, and an attempt used in
        /// one must not block the student in another.
        /// </summary>
        public void CheckSubmitEligibility(string classroomId, string quizId, Action<bool, string, SubmitEligibility> onComplete)
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }

            QuizService.Instance.FetchQuiz(quizId, (ok, error, quiz) =>
            {
                if (!ok || quiz == null)
                {
                    onComplete?.Invoke(false, error ?? "Could not load this assignment.", null);
                    return;
                }

                FetchStudentSubmissions(classroomId, quizId, student.Uid, submissions =>
                {
                    var eligibility = BuildEligibility(quiz, submissions);
                    onComplete?.Invoke(true, null, eligibility);
                });
            });
        }

        private static SubmitEligibility BuildEligibility(QuizService.QuizRecord quiz, List<SubmissionRecord> submissions)
        {
            submissions = submissions ?? new List<SubmissionRecord>();

            int attemptsUsed = submissions.Count;
            int remaining = quiz.MaxAttempts > 0 ? Mathf.Max(0, quiz.MaxAttempts - attemptsUsed) : -1;

            bool deadlinePassed = quiz.IsDeadlineEnabled && quiz.DeadlineUtc.HasValue
                && DateTime.UtcNow > quiz.DeadlineUtc.Value;

            var latest = submissions
                .OrderByDescending(s => s.SubmittedAtUtc)
                .FirstOrDefault();

            var eligibility = new SubmitEligibility
            {
                AttemptsUsed = attemptsUsed,
                MaxAttempts = quiz.MaxAttempts,
                RemainingAttempts = remaining,
                DeadlinePassed = deadlinePassed,
                LatestSubmission = latest
            };

            // Deadline is checked before attempts, matching
            // QuizService.CheckAttemptEligibility, so the reason shown here always
            // matches what the re-check at submit time would report.
            if (deadlinePassed)
            {
                eligibility.CanSubmit = false;
                eligibility.BlockReason = "The deadline for this assignment has passed. You can no longer submit.";
                return eligibility;
            }

            if (quiz.MaxAttempts > 0 && attemptsUsed >= quiz.MaxAttempts)
            {
                eligibility.CanSubmit = false;
                eligibility.BlockReason = "You have used all available attempts for this assignment.";
                return eligibility;
            }

            eligibility.CanSubmit = true;
            return eligibility;
        }

        /// <summary>
        /// Writes the `fileSubmissions` doc AFTER R2FileUploadService has confirmed
        /// the upload. Re-checks the deadline/attempts one last time first, in case
        /// either changed while the student was picking their file.
        ///
        /// storageKey must be the value the Worker returned - never a path built on
        /// the client.
        /// </summary>
        public void SubmitFile(
            QuizService.QuizRecord quiz,
            string classroomId,
            string submissionId,
            string fileName,
            string mimeType,
            long fileSize,
            string storageKey,
            Action<bool, string, SubmissionRecord> onComplete)
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }
            if (quiz == null) { onComplete?.Invoke(false, "Could not load this assignment.", null); return; }

            FetchStudentSubmissions(classroomId, quiz.QuizId, student.Uid, existing =>
            {
                var eligibility = BuildEligibility(quiz, existing);
                if (!eligibility.CanSubmit)
                {
                    onComplete?.Invoke(false, eligibility.BlockReason, null);
                    return;
                }

                int attemptNumber = eligibility.AttemptsUsed + 1;

                ResolveTeacherId(classroomId, quiz.CreatedBy, teacherId =>
                {
                    var record = new SubmissionRecord
                    {
                        SubmissionId = submissionId,
                        QuizId = quiz.QuizId,
                        QuizTitle = quiz.Title,
                        ClassroomId = classroomId,
                        StudentId = student.Uid,
                        StudentName = student.FullName,
                        TeacherId = teacherId,
                        AttemptNumber = attemptNumber,
                        FileName = fileName,
                        FileExtension = FileSubmissionConfig.ExtensionOf(fileName),
                        MimeType = mimeType,
                        FileSize = fileSize,
                        StorageKey = storageKey,
                        SubmittedAtUtc = DateTime.UtcNow,
                        Status = StatusSubmitted,
                        Score = 0,
                        Feedback = string.Empty
                    };

                    Db.Collection(SubmissionsCollection).Document(submissionId)
                        .SetAsync(ToMap(record))
                        .ContinueWithOnMainThread(task =>
                        {
                            if (task.IsCanceled || task.IsFaulted)
                            {
                                onComplete?.Invoke(false, "Your file was uploaded but the submission could not be recorded. Please try submitting again.", null);
                                return;
                            }

                            onComplete?.Invoke(true, null, record);
                        });
                });
            });
        }

        /// <summary>All of one student's submissions for one quiz in one classroom,
        /// newest first. Used by the student screen's history and by the eligibility
        /// check's attempt count.</summary>
        public void FetchStudentSubmissions(string classroomId, string quizId, string studentId, Action<List<SubmissionRecord>> onComplete)
        {
            Db.Collection(SubmissionsCollection)
                .WhereEqualTo("studentId", studentId)
                .WhereEqualTo("quizId", quizId)
                .WhereEqualTo("classroomId", classroomId)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<SubmissionRecord>();
                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents) results.Add(ToRecord(doc));
                    }

                    onComplete?.Invoke(results.OrderByDescending(r => r.SubmittedAtUtc).ToList());
                });
        }

        /// <summary>Convenience wrapper for the signed-in student.</summary>
        public void FetchMySubmissions(string classroomId, string quizId, Action<List<SubmissionRecord>> onComplete)
        {
            var student = PlayerSessionManager.Instance != null ? PlayerSessionManager.Instance.CurrentStudent : null;
            if (student == null) { onComplete?.Invoke(new List<SubmissionRecord>()); return; }
            FetchStudentSubmissions(classroomId, quizId, student.Uid, onComplete);
        }

        // ==================================================================
        // Teacher: review
        // ==================================================================

        /// <summary>Every submission for one assignment IN ONE CLASSROOM, newest
        /// first. Feeds the teacher's View Submissions list.
        ///
        /// classroomId is required. The `fileSubmissions` read rule proves the
        /// teacher owns a submission by looking up its classroom, and Firestore only
        /// lets a list query through when the query itself pins that down - a
        /// quizId-only query could match documents from classrooms the teacher
        /// doesn't own, so the whole read is denied (PERMISSION_DENIED) even when
        /// every actual match would have been theirs.</summary>
        public void FetchSubmissionsForQuiz(string classroomId, string quizId, Action<bool, string, List<SubmissionRecord>> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId) || string.IsNullOrEmpty(quizId))
            {
                onComplete?.Invoke(false, "Choose a classroom to see its submissions.", new List<SubmissionRecord>());
                return;
            }

            Db.Collection(SubmissionsCollection)
                .WhereEqualTo("quizId", quizId)
                .WhereEqualTo("classroomId", classroomId)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        // The real reason (PERMISSION_DENIED, FAILED_PRECONDITION with an
                        // index-creation link, ...) goes to the Console / logcat instead of
                        // being swallowed behind a generic message.
                        var ex = task.Exception?.Flatten().InnerException;
                        Debug.LogError($"[FileSubmissionService] FetchSubmissionsForQuiz failed (quizId={quizId}, classroomId={classroomId}): {ex}");

                        onComplete?.Invoke(false, DescribeFirestoreError(ex, "Could not load submissions."), new List<SubmissionRecord>());
                        return;
                    }

                    var results = new List<SubmissionRecord>();
                    foreach (var doc in task.Result.Documents) results.Add(ToRecord(doc));

                    onComplete?.Invoke(true, null, results
                        .OrderByDescending(r => r.SubmittedAtUtc)
                        .ToList());
                });
        }

        /// <summary>Turns the handful of Firestore failures a teacher can actually act
        /// on into a readable line; everything else keeps the caller's fallback.</summary>
        private static string DescribeFirestoreError(Exception ex, string fallback)
        {
            if (ex is FirestoreException firestoreException)
            {
                switch (firestoreException.ErrorCode)
                {
                    case FirestoreError.PermissionDenied:
                        return "You don't have permission to read these submissions.";
                    case FirestoreError.FailedPrecondition:
                        return "Submissions need a database index that hasn't been created yet.";
                    case FirestoreError.Unavailable:
                        return "Can't reach the server. Check your internet connection and try again.";
                }
            }

            return fallback;
        }

        /// <summary>
        /// Saves the teacher's score + feedback, marks the submission Reviewed, and
        /// pushes the result into the existing performance system:
        ///
        ///   1. `fileSubmissions/{id}` -> status/score/feedback/reviewedAt/reviewedBy
        ///   2. `quizAttempts/{attemptDocId}` -> a normal scored attempt row, so
        ///      Student Classroom Detail's Scores tab, StudentProgress and the
        ///      admin analytics/report readers pick it up with no changes
        ///   3. `students/{studentId}` -> totalPoints + level, and
        ///      `classrooms/{cid}/members/{studentId}` -> points / quizzesCompleted /
        ///      avgScorePercent
        ///
        /// Best-attempt semantics match QuizService exactly: totals only move when
        /// this grade beats the student's existing best graded submission for this
        /// quiz in this classroom, and quizzesCompleted only increments the first
        /// time any submission for it is graded. Re-grading the same submission
        /// adjusts by the difference rather than double-counting.
        ///
        /// Badges are intentionally not awarded here - badge writes live under
        /// `students/{uid}/badgeAwards` and are owned by the student's own session
        /// (QuizService.RecordBadgeAwards). The student picks up any newly-crossed
        /// badge threshold the next time they finish a quiz.
        /// </summary>
        public void ReviewSubmission(
            SubmissionRecord submission,
            int score,
            string feedback,
            int pointsPossible,
            int passingScorePercent,
            Action<bool, string, SubmissionRecord> onComplete)
        {
            var admin = AdminAuthService.Instance != null ? AdminAuthService.Instance.CurrentAdmin : null;
            if (admin == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }
            if (submission == null) { onComplete?.Invoke(false, "That submission no longer exists.", null); return; }

            if (score < 0)
            {
                onComplete?.Invoke(false, "Score cannot be negative.", null);
                return;
            }

            if (pointsPossible > 0 && score > pointsPossible)
            {
                onComplete?.Invoke(false, $"Score cannot be higher than the assignment's {pointsPossible} points.", null);
                return;
            }

            // The "was this already graded, and what was the best grade so far"
            // reads are queries, which can't run inside a Firestore transaction -
            // so they happen here, just before it.
            FetchStudentSubmissions(submission.ClassroomId, submission.QuizId, submission.StudentId, siblings =>
            {
                var otherReviewed = siblings
                    .Where(s => s.IsReviewed && s.SubmissionId != submission.SubmissionId)
                    .ToList();

                bool hasPriorGrade = otherReviewed.Count > 0 || submission.IsReviewed;
                int previousBest = otherReviewed.Count > 0 ? otherReviewed.Max(s => s.Score) : 0;
                if (submission.IsReviewed) previousBest = Mathf.Max(previousBest, submission.Score);

                float previousBestPercent = pointsPossible > 0 ? previousBest / (float)pointsPossible * 100f : 0f;
                float newPercent = pointsPossible > 0 ? score / (float)pointsPossible * 100f : 0f;

                bool isNewBest = !hasPriorGrade || score > previousBest;
                int pointsDelta = !hasPriorGrade ? score : (isNewBest ? score - previousBest : 0);

                // First grade for this quiz/classroom counts as a completion once -
                // re-grading, or grading a second attempt, must not inflate it.
                bool isFirstCompletion = !hasPriorGrade;

                RunReviewTransaction(submission, score, feedback, pointsPossible, passingScorePercent,
                    admin.Uid, isNewBest, pointsDelta, isFirstCompletion, newPercent, previousBestPercent, onComplete);
            });
        }

        private void RunReviewTransaction(
            SubmissionRecord submission, int score, string feedback, int pointsPossible, int passingScorePercent,
            string reviewerUid, bool isNewBest, int pointsDelta, bool isFirstCompletion,
            float newPercent, float previousBestPercent,
            Action<bool, string, SubmissionRecord> onComplete)
        {
            var submissionRef = Db.Collection(SubmissionsCollection).Document(submission.SubmissionId);
            var studentRef = Db.Collection("students").Document(submission.StudentId);
            var memberRef = string.IsNullOrEmpty(submission.ClassroomId)
                ? null
                : Db.Collection("classrooms").Document(submission.ClassroomId).Collection("members").Document(submission.StudentId);

            // Reuse the existing attempt row when this submission has been graded
            // before, so a correction replaces the score instead of stacking a
            // second attempt into quizAttempts.
            var attemptRef = string.IsNullOrEmpty(submission.AttemptDocId)
                ? Db.Collection("quizAttempts").Document()
                : Db.Collection("quizAttempts").Document(submission.AttemptDocId);

            var levelsRef = AdminGamificationService.Instance != null ? AdminGamificationService.Instance.LevelsRef : null;
            var configRef = AdminGamificationService.Instance != null && !string.IsNullOrEmpty(submission.TeacherId)
                ? AdminGamificationService.Instance.ConfigRefFor(submission.TeacherId)
                : null;

            var reviewedAt = Timestamp.GetCurrentTimestamp();

            Db.RunTransactionAsync(async transaction =>
            {
                var studentSnap = await transaction.GetSnapshotAsync(studentRef);
                DocumentSnapshot memberSnap = memberRef != null ? await transaction.GetSnapshotAsync(memberRef) : null;
                DocumentSnapshot configSnap = configRef != null ? await transaction.GetSnapshotAsync(configRef) : null;
                DocumentSnapshot levelsSnap = levelsRef != null ? await transaction.GetSnapshotAsync(levelsRef) : null;

                int currentTotalPoints = studentSnap.ContainsField("totalPoints") ? studentSnap.GetValue<int>("totalPoints") : 0;
                int newTotalPoints = currentTotalPoints + pointsDelta;

                var settings = AdminGamificationService.ToSettings(configSnap, levelsSnap);
                var levelInfo = AdminGamificationService.ComputeLevelProgress(settings, newTotalPoints);

                // ---- 1. the submission itself ----
                transaction.Update(submissionRef, new Dictionary<string, object>
                {
                    { "status", StatusReviewed },
                    { "score", score },
                    { "feedback", feedback ?? string.Empty },
                    { "reviewedAt", reviewedAt },
                    { "reviewedBy", reviewerUid },
                    { "attemptDocId", attemptRef.Id }
                });

                // ---- 2. the scored attempt row ----
                // Field-for-field the same shape QuizService.SubmitQuizAttemptInternal
                // writes, so every existing reader (Scores tab, progress, analytics)
                // treats a graded file assignment like any other completed quiz.
                // correctCount/incorrectCount are 0/0 - a file submission has no
                // per-question breakdown, and `percent` is derived from points, which
                // is what every score calculation actually reads.
                transaction.Set(attemptRef, new Dictionary<string, object>
                {
                    { "studentId", submission.StudentId },
                    { "studentName", submission.StudentName ?? string.Empty },
                    { "quizId", submission.QuizId },
                    { "quizName", submission.QuizTitle ?? string.Empty },
                    { "quizTitle", submission.QuizTitle ?? string.Empty },
                    { "category", string.Empty },
                    { "classroomId", submission.ClassroomId ?? string.Empty },
                    { "correctCount", 0 },
                    { "incorrectCount", 0 },
                    { "scoreCorrect", 0 },
                    { "scoreTotal", 0 },
                    { "timeSpentSeconds", 0 },
                    { "pointsEarned", score },
                    { "pointsPossible", pointsPossible },
                    { "percent", newPercent },
                    { "passed", newPercent >= passingScorePercent },
                    { "status", "Completed" },
                    { "score", newPercent },
                    { "isBestAttempt", isNewBest },
                    { "attemptCount", submission.AttemptNumber },
                    { "remainingAttempts", -1 },
                    { "submissionType", SubmissionTypes.File },
                    { "fileSubmissionId", submission.SubmissionId },
                    { "completedAt", submission.SubmittedAtUtc == default(DateTime)
                        ? reviewedAt
                        : Timestamp.FromDateTime(DateTime.SpecifyKind(submission.SubmittedAtUtc, DateTimeKind.Utc)) }
                });

                // ---- 3. aggregates ----
                if (pointsDelta != 0 || isFirstCompletion)
                {
                    var studentUpdate = new Dictionary<string, object>
                    {
                        { "totalPoints", newTotalPoints },
                        { "level", levelInfo.level }
                    };
                    if (isFirstCompletion) studentUpdate["quizzesCompleted"] = FieldValue.Increment(1);
                    transaction.Update(studentRef, studentUpdate);
                }

                if (memberRef != null && memberSnap != null && memberSnap.Exists && isNewBest)
                {
                    int priorQuizzes = memberSnap.ContainsField("quizzesCompleted") ? memberSnap.GetValue<int>("quizzesCompleted") : 0;
                    int priorPoints = memberSnap.ContainsField("points") ? memberSnap.GetValue<int>("points") : 0;
                    float priorAvg = memberSnap.ContainsField("avgScorePercent") ? (float)memberSnap.GetValue<double>("avgScorePercent") : 0f;

                    int newQuizzes = isFirstCompletion ? priorQuizzes + 1 : priorQuizzes;
                    float priorSum = priorAvg * priorQuizzes;
                    float newSum = isFirstCompletion ? priorSum + newPercent : priorSum - previousBestPercent + newPercent;

                    transaction.Update(memberRef, new Dictionary<string, object>
                    {
                        { "quizzesCompleted", newQuizzes },
                        { "points", priorPoints + pointsDelta },
                        { "avgScorePercent", newQuizzes > 0 ? newSum / newQuizzes : 0f },
                        { "level", levelInfo.level }
                    });
                }

                return attemptRef.Id;
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning($"[FileSubmissionService] Review failed: {task.Exception?.InnerException?.Message}");
                    onComplete?.Invoke(false, "Could not save this review. Please try again.", null);
                    return;
                }

                submission.Status = StatusReviewed;
                submission.Score = score;
                submission.Feedback = feedback ?? string.Empty;
                submission.ReviewedAtUtc = DateTime.UtcNow;
                submission.ReviewedBy = reviewerUid;
                submission.AttemptDocId = task.Result;

                onComplete?.Invoke(true, null, submission);
            });
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>The classroom's teacher owns the submission for access-control
        /// purposes. Falls back to the quiz's own createdBy if the classroom doc
        /// can't be read (e.g. a quiz with no classroom context).</summary>
        private void ResolveTeacherId(string classroomId, string fallbackTeacherId, Action<string> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId))
            {
                onComplete?.Invoke(fallbackTeacherId ?? string.Empty);
                return;
            }

            Db.Collection("classrooms").Document(classroomId).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists || !task.Result.ContainsField("teacherId"))
                {
                    onComplete?.Invoke(fallbackTeacherId ?? string.Empty);
                    return;
                }

                onComplete?.Invoke(task.Result.GetValue<string>("teacherId"));
            });
        }

        private static Dictionary<string, object> ToMap(SubmissionRecord r)
        {
            return new Dictionary<string, object>
            {
                { "quizId", r.QuizId ?? string.Empty },
                { "quizTitle", r.QuizTitle ?? string.Empty },
                { "classroomId", r.ClassroomId ?? string.Empty },
                { "studentId", r.StudentId ?? string.Empty },
                { "studentName", r.StudentName ?? string.Empty },
                { "teacherId", r.TeacherId ?? string.Empty },
                { "attemptNumber", r.AttemptNumber },
                { "fileName", r.FileName ?? string.Empty },
                { "fileExtension", r.FileExtension ?? string.Empty },
                { "mimeType", r.MimeType ?? string.Empty },
                { "fileSize", r.FileSize },
                { "storageKey", r.StorageKey ?? string.Empty },
                { "submittedAt", Timestamp.FromDateTime(DateTime.SpecifyKind(
                    r.SubmittedAtUtc == default(DateTime) ? DateTime.UtcNow : r.SubmittedAtUtc, DateTimeKind.Utc)) },
                { "status", r.Status ?? StatusSubmitted },
                { "score", r.Score },
                { "feedback", r.Feedback ?? string.Empty },
                { "reviewedAt", null },
                { "reviewedBy", string.Empty },
                { "attemptDocId", string.Empty }
            };
        }

        private static SubmissionRecord ToRecord(DocumentSnapshot doc)
        {
            return new SubmissionRecord
            {
                SubmissionId = doc.Id,
                QuizId = doc.ContainsField("quizId") ? doc.GetValue<string>("quizId") : string.Empty,
                QuizTitle = doc.ContainsField("quizTitle") ? doc.GetValue<string>("quizTitle") : string.Empty,
                ClassroomId = doc.ContainsField("classroomId") ? doc.GetValue<string>("classroomId") : string.Empty,
                StudentId = doc.ContainsField("studentId") ? doc.GetValue<string>("studentId") : string.Empty,
                StudentName = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : string.Empty,
                TeacherId = doc.ContainsField("teacherId") ? doc.GetValue<string>("teacherId") : string.Empty,
                AttemptNumber = doc.ContainsField("attemptNumber") ? doc.GetValue<int>("attemptNumber") : 1,
                FileName = doc.ContainsField("fileName") ? doc.GetValue<string>("fileName") : string.Empty,
                FileExtension = doc.ContainsField("fileExtension") ? doc.GetValue<string>("fileExtension") : string.Empty,
                MimeType = doc.ContainsField("mimeType") ? doc.GetValue<string>("mimeType") : string.Empty,
                FileSize = doc.ContainsField("fileSize") ? Convert.ToInt64(doc.GetValue<long>("fileSize")) : 0L,
                StorageKey = doc.ContainsField("storageKey") ? doc.GetValue<string>("storageKey") : string.Empty,
                SubmittedAtUtc = doc.ContainsField("submittedAt") && doc.GetValue<object>("submittedAt") != null
                    ? doc.GetValue<Timestamp>("submittedAt").ToDateTime()
                    : DateTime.UtcNow,
                Status = doc.ContainsField("status") ? doc.GetValue<string>("status") : StatusSubmitted,
                Score = doc.ContainsField("score") ? doc.GetValue<int>("score") : 0,
                Feedback = doc.ContainsField("feedback") ? doc.GetValue<string>("feedback") : string.Empty,
                ReviewedAtUtc = doc.ContainsField("reviewedAt") && doc.GetValue<object>("reviewedAt") != null
                    ? doc.GetValue<Timestamp>("reviewedAt").ToDateTime()
                    : (DateTime?)null,
                ReviewedBy = doc.ContainsField("reviewedBy") ? doc.GetValue<string>("reviewedBy") : string.Empty,
                AttemptDocId = doc.ContainsField("attemptDocId") ? doc.GetValue<string>("attemptDocId") : string.Empty
            };
        }
    }
}
