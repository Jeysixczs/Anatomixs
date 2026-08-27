using System;
using System.Collections.Generic;
using System.Linq;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Firestore-backed quiz authoring (admin), quiz selection + attempts
    /// (student), and the progress-tracker aggregation query. This is the
    /// "AdminQuizService" / "QuizService" referenced in the TODOs inside
    /// AdminQuizManagementController (OnCreateQuizSubmitClicked /
    /// OnAddQuestionSubmitClicked), StudentQuizSelectionController,
    /// StudentQuizResultController and StudentProgressController.
    ///
    /// Schema note: FIRESTORE_SCHEMA.md's `quizzes/{quizId}` doc didn't include
    /// a time limit or passing score, since AdminQuizManagementController's
    /// "New Quiz" modal collects both, this service persists them too:
    ///   quizzes/{quizId}: title, category, classroomId, createdBy,
    ///     pointsPossible, timeLimitSeconds, passingScorePercent,
    ///     questions: array&lt;{ questionText, questionTypeSlug, options,
    ///       correctAnswer, difficulty, points }&gt;, createdAt
    /// `quizAttempts/{attemptId}` also gets one extra denormalized field,
    /// `category` (copied from the quiz at attempt time), so
    /// StudentProgressController's per-category breakdown doesn't need a
    /// second round trip to look up each quiz.
    ///
    /// Attach to the same persistent GameObject as FirebaseBootstrap/UIManager.
    /// Admin methods require AdminAuthService.CurrentAdmin; student methods
    /// require PlayerSessionManager.CurrentStudent.
    /// </summary>
    public class QuizService : MonoBehaviour
    {
        public static QuizService Instance { get; private set; }

        private static readonly string[] KnownCategories = { "skeletal", "muscular", "nervous", "cardiovascular" };

        [Serializable]
        public class QuestionRecord
        {
            public string QuestionText;
            public string QuestionTypeSlug;
            public List<string> Options = new List<string>();
            public string CorrectAnswer;
            public string Difficulty;
            public int Points;

            // Image-Based only (QuestionTypeSlugs.ImageBased) - which anatomy system/
            // structure the teacher picked via the Student Anatomy Screen. Left null/
            // empty for every other question type (see QuestionTypeSlugs.cs).
            // structureKey is the internal 3D-model/BoneDatabase.json identifier -
            // structureDisplayName is the human-readable name, which CorrectAnswer
            // above must always equal for this type. Never used to derive
            // CorrectAnswer here - AdminQuizManagementController sets both from the
            // same selection so they can never drift apart.
            public string AnatomySystemKey;
            public string AnatomySystemDisplayName;
            public string StructureKey;
            public string StructureDisplayName;
        }

        [Serializable]
        public class QuizRecord
        {
            public string QuizId;
            public string Title;
            public string Category;
            public string ClassroomId; // null/empty = available to everyone
            public string CreatedBy;
            public int PointsPossible;
            public int PassingScorePercent;

            /// <summary>0 = unlimited attempts.</summary>
            public int MaxAttempts;
            public int TimeLimitMinutes;
            /// <summary>False = timer is disabled entirely ("No Time Limit").</summary>
            public bool HasTimeLimit;
            /// <summary>Last moment a student may take the quiz. Only meaningful when IsDeadlineEnabled.</summary>
            public DateTime? DeadlineUtc;
            public bool IsDeadlineEnabled;

            public List<QuestionRecord> Questions = new List<QuestionRecord>();
        }

        /// <summary>Result of a pre-flight check run before letting a student start a quiz.</summary>
        [Serializable]
        public class AttemptEligibility
        {
            public bool CanStart;
            /// <summary>User-facing message to show when CanStart is false.</summary>
            public string BlockReason;
            public int AttemptsUsed;
            /// <summary>0 = unlimited.</summary>
            public int MaxAttempts;
            /// <summary>-1 = unlimited.</summary>
            public int RemainingAttempts;
        }

        /// <summary>One question's outcome within a single attempt. Pass a list of these into
        /// SubmitQuizAttempt so FetchClassroomReportData can build the "Common Incorrect
        /// Answers" list on the Mistakes tab. Build this list in your gameplay screen's
        /// submit handler (StudentQuizGameplayController) from whatever per-question
        /// right/wrong tracking it already does while scoring correctCount/incorrectCount -
        /// this class doesn't exist anywhere yet, so that call site needs a small update to
        /// pass it through.</summary>
        [Serializable]
        public class QuestionAttemptResult
        {
            public string QuestionText;
            public bool WasCorrect;

            public QuestionAttemptResult(string questionText, bool wasCorrect)
            {
                QuestionText = questionText;
                WasCorrect = wasCorrect;
            }
        }

        [Serializable]
        public class AttemptResult
        {
            public int NewTotalPoints;
            public int NewLevel;
            public string NewLevelTitle;
            public List<string> NewlyEarnedBadgeIds = new List<string>();
            public List<string> NewlyEarnedBadgeNames = new List<string>();
        }

        [Serializable]
        public class ProgressResult
        {
            public int CurrentLevel;
            public string CurrentLevelTitle;
            public int NextLevel;
            public string NextLevelTitle;
            public float LevelProgress01;
            public int PointsToNextLevel;

            public int QuizzesCompleted;
            public float AvgScorePercent;
            public int TotalPoints;
            public int BadgesEarnedCount;

            /// <summary>Monday..Sunday total points (quiz + bonus) for the current week.</summary>
            public float[] WeeklyPoints = new float[7];

            /// <summary>category -> (quizzes attempted, average score 0-1). Keys match KnownCategories.</summary>
            public Dictionary<string, (int quizzes, float percent01)> CategoryBreakdown =
                new Dictionary<string, (int, float)>();
        }

        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ==================================================================
        // Admin: quiz authoring
        // ==================================================================

        /// <summary>Server-side mirror of the same rule AdminQuizManagementController enforces
        /// live in the UI (RevalidateDeadlineDate) - re-checked here so a stale client, a
        /// modified request, or any future caller of CreateQuiz/UpdateQuizSettings can never
        /// persist a deadline that's already in the past. Truncates to minute precision so
        /// the currently-selected minute isn't rejected just because a few seconds elapsed
        /// in transit.</summary>
        public const string PastDeadlineErrorMessage = "The selected date and time must be later than the current date and time.";

        private static bool IsDeadlineInPast(bool isDeadlineEnabled, DateTime? deadlineUtc)
        {
            if (!isDeadlineEnabled || !deadlineUtc.HasValue) return false;

            var deadline = DateTime.SpecifyKind(deadlineUtc.Value, DateTimeKind.Utc);
            var nowTruncated = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
                DateTime.UtcNow.Hour, DateTime.UtcNow.Minute, 0, DateTimeKind.Utc);
            var deadlineTruncated = new DateTime(deadline.Year, deadline.Month, deadline.Day,
                deadline.Hour, deadline.Minute, 0, DateTimeKind.Utc);

            return deadlineTruncated < nowTruncated;
        }

        /// <summary>Call from AdminQuizManagementController.OnCreateQuizSubmitClicked().</summary>
        public void CreateQuiz(
            string title,
            string category,
            int maxAttempts,
            int timeLimitMinutes,
            bool hasTimeLimit,
            bool isDeadlineEnabled,
            DateTime? deadlineUtc,
            int passingScorePercent,
            string classroomId,
            Action<bool, string, QuizRecord> onComplete)
        {
            var admin = AdminAuthService.Instance.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }

            if (IsDeadlineInPast(isDeadlineEnabled, deadlineUtc))
            {
                onComplete?.Invoke(false, PastDeadlineErrorMessage, null);
                return;
            }

            var quizRef = Db.Collection("quizzes").Document(); // auto id

            var batch = Db.StartBatch();
            batch.Set(quizRef, new Dictionary<string, object>
            {
                { "title", title },
                { "category", category },
                { "classroomId", classroomId },
                { "createdBy", admin.Uid },
                { "pointsPossible", 0 },
                { "maxAttempts", maxAttempts },
                { "timeLimitMinutes", timeLimitMinutes },
                { "hasTimeLimit", hasTimeLimit },
                { "isDeadlineEnabled", isDeadlineEnabled },
                { "deadline", isDeadlineEnabled && deadlineUtc.HasValue ? (object)Timestamp.FromDateTime(DateTime.SpecifyKind(deadlineUtc.Value, DateTimeKind.Utc)) : null },
                { "passingScorePercent", passingScorePercent },
                { "questions", new List<object>() },
                { "createdAt", Timestamp.GetCurrentTimestamp() }
            });
            batch.Update(Db.Collection("admins").Document(admin.Uid), "quizzesCreated", FieldValue.Increment(1));

            batch.CommitAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not create quiz. Please try again.", null);
                    return;
                }

                // Patch AdminAuthService.CurrentAdmin locally with what this batch
                // just wrote (quizzesCreated +1). See AdminAuthService.ApplyQuizzesCreatedDelta.
                AdminAuthService.Instance?.ApplyQuizzesCreatedDelta(1);

                onComplete?.Invoke(true, null, new QuizRecord
                {
                    QuizId = quizRef.Id,
                    Title = title,
                    Category = category,
                    ClassroomId = classroomId,
                    CreatedBy = admin.Uid,
                    PointsPossible = 0,
                    MaxAttempts = maxAttempts,
                    TimeLimitMinutes = timeLimitMinutes,
                    HasTimeLimit = hasTimeLimit,
                    IsDeadlineEnabled = isDeadlineEnabled,
                    DeadlineUtc = isDeadlineEnabled ? deadlineUtc : null,
                    PassingScorePercent = passingScorePercent,
                    Questions = new List<QuestionRecord>()
                });
            });
        }

        /// <summary>Call from AdminQuizManagementController's "Edit Settings" modal (same fields as
        /// CreateQuiz, minus classroomId which doesn't change after creation). Only touches the
        /// settings fields - questions/pointsPossible are left alone.</summary>
        public void UpdateQuizSettings(
            string quizId,
            string title,
            string category,
            int maxAttempts,
            int timeLimitMinutes,
            bool hasTimeLimit,
            bool isDeadlineEnabled,
            DateTime? deadlineUtc,
            int passingScorePercent,
            Action<bool, string, QuizRecord> onComplete)
        {
            if (IsDeadlineInPast(isDeadlineEnabled, deadlineUtc))
            {
                onComplete?.Invoke(false, PastDeadlineErrorMessage, null);
                return;
            }

            var quizRef = Db.Collection("quizzes").Document(quizId);

            var update = new Dictionary<string, object>
            {
                { "title", title },
                { "category", category },
                { "maxAttempts", maxAttempts },
                { "timeLimitMinutes", timeLimitMinutes },
                { "hasTimeLimit", hasTimeLimit },
                { "isDeadlineEnabled", isDeadlineEnabled },
                { "deadline", isDeadlineEnabled && deadlineUtc.HasValue ? (object)Timestamp.FromDateTime(DateTime.SpecifyKind(deadlineUtc.Value, DateTimeKind.Utc)) : null },
                { "passingScorePercent", passingScorePercent }
            };

            quizRef.UpdateAsync(update).ContinueWithOnMainThread(updateTask =>
            {
                if (updateTask.IsCanceled || updateTask.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not update quiz settings. Please try again.", null);
                    return;
                }

                quizRef.GetSnapshotAsync().ContinueWithOnMainThread(getTask =>
                {
                    if (getTask.IsCanceled || getTask.IsFaulted || !getTask.Result.Exists)
                    {
                        onComplete?.Invoke(false, "Saved, but could not reload the quiz.", null);
                        return;
                    }

                    onComplete?.Invoke(true, null, ToQuizRecord(getTask.Result));
                });
            });
        }

        /// <summary>Call from AdminQuizManagementController.OnAddQuestionSubmitClicked().</summary>
        public void AddQuestion(string quizId, QuestionRecord question, Action<bool, string, QuizRecord> onComplete)
        {
            var quizRef = Db.Collection("quizzes").Document(quizId);

            Db.RunTransactionAsync(async transaction =>
            {
                var snap = await transaction.GetSnapshotAsync(quizRef);
                if (!snap.Exists) throw new InvalidOperationException("This quiz no longer exists.");

                var record = ToQuizRecord(snap);
                record.Questions.Add(question);
                int newPointsPossible = record.Questions.Sum(q => q.Points);

                transaction.Update(quizRef, new Dictionary<string, object>
                {
                    { "questions", record.Questions.Select(QuestionToMap).ToList<object>() },
                    { "pointsPossible", newPointsPossible }
                });

                record.PointsPossible = newPointsPossible;
                return record;
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    string message = task.Exception?.InnerException?.Message ?? "Could not add question.";
                    onComplete?.Invoke(false, message, null);
                    return;
                }

                onComplete?.Invoke(true, null, task.Result);
            });
        }

        /// <summary>Call from AdminQuizManagementController's per-question delete button.</summary>
        public void DeleteQuestion(string quizId, int questionIndex, Action<bool, string, QuizRecord> onComplete)
        {
            var quizRef = Db.Collection("quizzes").Document(quizId);

            Db.RunTransactionAsync(async transaction =>
            {
                var snap = await transaction.GetSnapshotAsync(quizRef);
                if (!snap.Exists) throw new InvalidOperationException("This quiz no longer exists.");

                var record = ToQuizRecord(snap);
                if (questionIndex < 0 || questionIndex >= record.Questions.Count)
                {
                    throw new InvalidOperationException("That question no longer exists.");
                }

                record.Questions.RemoveAt(questionIndex);
                int newPointsPossible = record.Questions.Sum(q => q.Points);

                transaction.Update(quizRef, new Dictionary<string, object>
                {
                    { "questions", record.Questions.Select(QuestionToMap).ToList<object>() },
                    { "pointsPossible", newPointsPossible }
                });

                record.PointsPossible = newPointsPossible;
                return record;
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    string message = task.Exception?.InnerException?.Message ?? "Could not delete question.";
                    onComplete?.Invoke(false, message, null);
                    return;
                }

                onComplete?.Invoke(true, null, task.Result);
            });
        }

        /// <summary>Call from AdminQuizManagementController's per-quiz delete (trash) button.</summary>
        public void DeleteQuiz(string quizId, Action<bool, string> onComplete)
        {
            var admin = AdminAuthService.Instance.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(false, "Not signed in."); return; }

            var quizRef = Db.Collection("quizzes").Document(quizId);

            var batch = Db.StartBatch();
            batch.Delete(quizRef);
            batch.Update(Db.Collection("admins").Document(admin.Uid), "quizzesCreated", FieldValue.Increment(-1));

            batch.CommitAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not delete quiz.");
                    return;
                }

                // Patch AdminAuthService.CurrentAdmin locally with what this batch
                // just wrote (quizzesCreated -1). See AdminAuthService.ApplyQuizzesCreatedDelta.
                AdminAuthService.Instance?.ApplyQuizzesCreatedDelta(-1);

                onComplete?.Invoke(true, null);
            });
        }

        /// <summary>Call when showing AdminQuizManagementController - feeds its quiz list directly.</summary>
        public void FetchMyQuizzes(Action<List<QuizRecord>> onComplete)
        {
            var admin = AdminAuthService.Instance.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(new List<QuizRecord>()); return; }

            Db.Collection("quizzes")
                .WhereEqualTo("createdBy", admin.Uid)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<QuizRecord>();
                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents) results.Add(ToQuizRecord(doc));
                    }
                    onComplete?.Invoke(results);
                });
        }

        // Quiz browsing for students lives entirely in the classroom's "Available
        // Quizzes" tab (StudentClassroomDetailController), which reads
        // ClassroomService.FetchAvailableQuizzes() -> the classroom's publishedQuizIds
        // array (set via AdminClassroomService.SetQuizPublished /
        // AdminClassroomDetailController's Quizzes tab). There's no separate
        // "browse all quizzes" screen, so no quiz-listing method belongs here -
        // StudentQuizSelectionController is reached per-quiz from that tab and only
        // needs FetchQuizStats() below for the specific quiz being started.

        // NEW: nothing previously fetched a single quiz's full question list -
        // FetchQuizStats() only returns past-attempt aggregates. StudentQuizGameplayController
        // needs the actual QuizRecord (with Questions) once the student taps "Start", so
        // this reads quizzes/{quizId} directly and reuses ToQuizRecord() like the admin path.
        /// <summary>Call when starting gameplay (StudentQuizGameplayController) - fetches
        /// the full quiz doc, including its questions, right before the student begins.</summary>
        public void FetchQuiz(string quizId, Action<bool, string, QuizRecord> onComplete)
        {
            Db.Collection("quizzes").Document(quizId)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                    {
                        onComplete?.Invoke(false, "Could not load this quiz.", null);
                        return;
                    }

                    onComplete?.Invoke(true, null, ToQuizRecord(task.Result));
                });
        }

        /// <summary>
        /// Call once gameplay finishes, right before showing StudentQuizResultController.
        /// Writes the `quizAttempts` doc, updates the student's totalPoints /
        /// quizzesCompleted / level, and awards any newly-qualifying badges.
        /// </summary>
        public void SubmitQuizAttempt(
            string quizId,
            string quizName,
            string category,
            string classroomId,
            int correctCount,
            int incorrectCount,
            int pointsEarned,
            int pointsPossible,
            int bonusXp,
            Action<bool, string, AttemptResult> onComplete,
            int passingScorePercent = 70,
            List<QuestionAttemptResult> questionResults = null,
            int timeSpentSeconds = 0)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }

            // Re-check the deadline/attempts right before writing the result, in case either
            // changed while the student was on the gameplay screen (CheckAttemptEligibility()
            // is also run up-front by StudentQuizGameplayController.LoadQuiz(), which is what
            // normally stops a student from getting this far in the first place).
            CheckAttemptEligibility(quizId, (checkOk, checkError, eligibility) =>
            {
                if (checkOk && eligibility != null && !eligibility.CanStart)
                {
                    onComplete?.Invoke(false, eligibility.BlockReason, null);
                    return;
                }

                int attemptNumber = (eligibility?.AttemptsUsed ?? 0) + 1;
                int maxAttempts = eligibility?.MaxAttempts ?? 0;

                SubmitQuizAttemptInternal(quizId, quizName, category, classroomId, correctCount, incorrectCount,
                    pointsEarned, pointsPossible, bonusXp, attemptNumber, maxAttempts, onComplete,
                    passingScorePercent, questionResults, timeSpentSeconds);
            });
        }

        private void SubmitQuizAttemptInternal(
            string quizId,
            string quizName,
            string category,
            string classroomId,
            int correctCount,
            int incorrectCount,
            int pointsEarned,
            int pointsPossible,
            int bonusXp,
            int attemptNumber,
            int maxAttempts,
            Action<bool, string, AttemptResult> onComplete,
            int passingScorePercent = 70,
            List<QuestionAttemptResult> questionResults = null,
            int timeSpentSeconds = 0)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }

            var attemptRef = Db.Collection("quizAttempts").Document();
            var studentRef = Db.Collection("students").Document(student.Uid);
            // classroomId identifies which teacher's gamificationSettings/{teacherId}
            // doc to score against (badges/points are per-teacher - see
            // AdminGamificationService). If this ever fires for a classroom-less
            // "available to everyone" quiz (empty classroomId), there's no teacher
            // to resolve and we fall back to built-in defaults inside ToSettings().
            var classroomRef = string.IsNullOrEmpty(classroomId) ? null : Db.Collection("classrooms").Document(classroomId);
            var levelsRef = AdminGamificationService.Instance.LevelsRef;

            int total = correctCount + incorrectCount;
            float percent = total > 0 ? (correctCount / (float)total) * 100f : 0f;

            Db.RunTransactionAsync(async transaction =>
            {
                var studentSnap = await transaction.GetSnapshotAsync(studentRef);

                string teacherId = null;
                if (classroomRef != null)
                {
                    var classroomSnap = await transaction.GetSnapshotAsync(classroomRef);
                    teacherId = classroomSnap.Exists && classroomSnap.ContainsField("teacherId")
                        ? classroomSnap.GetValue<string>("teacherId")
                        : null;
                }

                DocumentSnapshot configSnap = null;
                if (!string.IsNullOrEmpty(teacherId))
                {
                    configSnap = await transaction.GetSnapshotAsync(AdminGamificationService.Instance.ConfigRefFor(teacherId));
                }

                var levelsSnap = await transaction.GetSnapshotAsync(levelsRef);

                int currentTotalPoints = studentSnap.ContainsField("totalPoints") ? studentSnap.GetValue<int>("totalPoints") : 0;
                var existingBadges = studentSnap.ContainsField("badgesEarned")
                    ? studentSnap.GetValue<List<string>>("badgesEarned")
                    : new List<string>();

                int newTotalPoints = currentTotalPoints + pointsEarned + bonusXp;
                var settings = AdminGamificationService.ToSettings(configSnap, levelsSnap);
                var levelInfo = AdminGamificationService.ComputeLevelProgress(settings, newTotalPoints);
                var newBadgeIds = AdminGamificationService.ComputeNewlyEarnedBadges(settings, newTotalPoints, existingBadges);

                var attemptData = new Dictionary<string, object>
                {
                    { "studentId", student.Uid },
                    { "studentName", student.FullName },
                    { "quizId", quizId },
                    { "quizName", quizName },
                    // Duplicated under "quizTitle"/"scoreCorrect"/"scoreTotal"/"timeSpentSeconds" -
                    // that's what ClassroomService.FetchMyScores (Student Classroom Detail's
                    // Scores tab) reads. Keep both sets in sync if either changes; the
                    // quizName/correctCount/incorrectCount originals are still read by
                    // QuizService's own FetchClassroomReportData / FetchProgressData / etc.
                    { "quizTitle", quizName },
                    { "category", category },
                    { "classroomId", classroomId },
                    { "correctCount", correctCount },
                    { "incorrectCount", incorrectCount },
                    { "scoreCorrect", correctCount },
                    { "scoreTotal", correctCount + incorrectCount },
                    { "timeSpentSeconds", timeSpentSeconds },
                    { "pointsEarned", pointsEarned },
                    { "pointsPossible", pointsPossible },
                    { "bonusXp", bonusXp },
                    { "percent", percent },
                    { "passed", percent >= passingScorePercent },
                    { "status", "Completed" },
                    { "score", percent },
                    { "attemptCount", attemptNumber },
                    { "remainingAttempts", maxAttempts > 0 ? Mathf.Max(0, maxAttempts - attemptNumber) : -1 },
                    { "completedAt", Timestamp.GetCurrentTimestamp() }
                };

                // Per-question right/wrong breakdown, used by FetchClassroomReportData to
                // build the "Common Incorrect Answers" list on the admin Mistakes tab.
                // Optional - omitted entirely for callers that haven't been updated to pass it.
                if (questionResults != null && questionResults.Count > 0)
                {
                    attemptData["questionResults"] = questionResults
                        .Select(q => (object)new Dictionary<string, object>
                        {
                            { "questionText", q.QuestionText },
                            { "correct", q.WasCorrect }
                        })
                        .ToList();
                }

                transaction.Set(attemptRef, attemptData);

                var studentUpdate = new Dictionary<string, object>
                {
                    { "totalPoints", newTotalPoints },
                    { "quizzesCompleted", FieldValue.Increment(1) },
                    { "level", levelInfo.level }
                };
                if (newBadgeIds.Count > 0)
                {
                    studentUpdate["badgesEarned"] = FieldValue.ArrayUnion(newBadgeIds.ToArray());
                }
                transaction.Update(studentRef, studentUpdate);

                var badgeNames = newBadgeIds
                    .Select(id => settings.Badges.FirstOrDefault(b => b.BadgeId == id)?.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                return new AttemptResult
                {
                    NewTotalPoints = newTotalPoints,
                    NewLevel = levelInfo.level,
                    NewLevelTitle = levelInfo.title,
                    NewlyEarnedBadgeIds = newBadgeIds,
                    NewlyEarnedBadgeNames = badgeNames
                };
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not save your quiz result.", null);
                    return;
                }

                var result = task.Result;

                // Keep this classroom's members/{uid} roster doc in sync (Students tab,
                // Leaderboard, Analytics) - pass the just-computed GLOBAL level through
                // rather than letting ClassroomService recompute it from this classroom's
                // own points, so the level shown here always matches the student's real
                // level everywhere else.
                ClassroomService.Instance?.RecordQuizCompletion(
                    classroomId, pointsEarned + bonusXp, percent, result.NewLevel);

                if (result.NewlyEarnedBadgeIds.Count > 0)
                {
                    RecordBadgeAwards(student.Uid, result.NewlyEarnedBadgeIds, classroomId, quizId, quizName);
                }

                onComplete?.Invoke(true, null, result);
            });
        }

        /// <summary>
        /// Writes one `students/{uid}/badgeAwards/{badgeId}` doc per newly-earned badge,
        /// recording which classroom and quiz triggered it. Badge *definitions* live on
        /// the awarding teacher's own doc (gamificationSettings/{teacherId}.badges), but
        /// this lets the UI show "earned in [classroom]" detail for each badge a student
        /// has - e.g. StudentProgress or a
        /// future badges screen can FetchBadgeAwards() then resolve classroomId to a name
        /// via ClassroomService.FetchClassroomDetail(), the same lazy-resolve pattern
        /// ClassroomService.FetchAvailableQuizzes() already uses.
        /// Fire-and-forget outside the main transaction - if this write fails the badge
        /// is still recorded on students/{uid}.badgesEarned, just without source detail.
        /// </summary>
        // ==================================================================
        // Student restrictions: deadline + max attempts
        // ==================================================================

        /// <summary>
        /// Call before letting a student start a quiz - StudentQuizGameplayController.LoadQuiz()
        /// is the canonical caller, since every entry point into gameplay routes through it.
        /// Checks the quiz's deadline and the student's attempt count against maxAttempts.
        /// If the deadline has already passed and the student never attempted the quiz, this
        /// also fire-and-forgets a "Missed" quizAttempts doc (score 0) so it shows up in the
        /// student's history and the teacher's records without requiring a scheduled job.
        /// </summary>
        public void CheckAttemptEligibility(string quizId, Action<bool, string, AttemptEligibility> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }

            Db.Collection("quizzes").Document(quizId).GetSnapshotAsync().ContinueWithOnMainThread(quizTask =>
            {
                if (quizTask.IsCanceled || quizTask.IsFaulted || !quizTask.Result.Exists)
                {
                    onComplete?.Invoke(false, "Could not load this quiz.", null);
                    return;
                }

                var quiz = ToQuizRecord(quizTask.Result);

                Db.Collection("quizAttempts")
                    .WhereEqualTo("studentId", student.Uid)
                    .WhereEqualTo("quizId", quizId)
                    .GetSnapshotAsync()
                    .ContinueWithOnMainThread(attemptsTask =>
                    {
                        if (attemptsTask.IsCanceled || attemptsTask.IsFaulted)
                        {
                            onComplete?.Invoke(false, "Could not check your quiz attempts.", null);
                            return;
                        }

                        var docs = attemptsTask.Result.Documents;
                        bool IsMissed(DocumentSnapshot d) => d.ContainsField("status") && d.GetValue<string>("status") == "Missed";

                        int attemptsUsed = docs.Count(d => !IsMissed(d));
                        bool alreadyRecordedMissed = docs.Any(IsMissed);

                        bool deadlinePassed = quiz.IsDeadlineEnabled && quiz.DeadlineUtc.HasValue
                            && DateTime.UtcNow > quiz.DeadlineUtc.Value;

                        int remaining = quiz.MaxAttempts > 0 ? Mathf.Max(0, quiz.MaxAttempts - attemptsUsed) : -1;

                        if (deadlinePassed)
                        {
                            if (attemptsUsed == 0 && !alreadyRecordedMissed)
                            {
                                RecordMissedAttempt(quiz, student.Uid);
                            }

                            onComplete?.Invoke(true, null, new AttemptEligibility
                            {
                                CanStart = false,
                                BlockReason = "The deadline for this quiz has passed. You can no longer take this quiz.",
                                AttemptsUsed = attemptsUsed,
                                MaxAttempts = quiz.MaxAttempts,
                                RemainingAttempts = remaining
                            });
                            return;
                        }

                        if (quiz.MaxAttempts > 0 && attemptsUsed >= quiz.MaxAttempts)
                        {
                            onComplete?.Invoke(true, null, new AttemptEligibility
                            {
                                CanStart = false,
                                BlockReason = "You have used all available attempts for this quiz.",
                                AttemptsUsed = attemptsUsed,
                                MaxAttempts = quiz.MaxAttempts,
                                RemainingAttempts = 0
                            });
                            return;
                        }

                        onComplete?.Invoke(true, null, new AttemptEligibility
                        {
                            CanStart = true,
                            BlockReason = null,
                            AttemptsUsed = attemptsUsed,
                            MaxAttempts = quiz.MaxAttempts,
                            RemainingAttempts = remaining
                        });
                    });
            });
        }

        /// <summary>Fire-and-forget: writes a `status: "Missed", score: 0` quizAttempts doc for a
        /// student who never attempted the quiz before its deadline passed.</summary>
        private void RecordMissedAttempt(QuizRecord quiz, string studentUid)
        {
            var attemptRef = Db.Collection("quizAttempts").Document();
            attemptRef.SetAsync(new Dictionary<string, object>
            {
                { "studentId", studentUid },
                { "quizId", quiz.QuizId },
                { "quizName", quiz.Title },
                { "quizTitle", quiz.Title },
                { "category", quiz.Category },
                { "classroomId", quiz.ClassroomId },
                { "correctCount", 0 },
                { "incorrectCount", 0 },
                { "scoreCorrect", 0 },
                { "scoreTotal", quiz.Questions?.Count ?? 0 },
                { "timeSpentSeconds", 0 },
                { "pointsEarned", 0 },
                { "pointsPossible", quiz.PointsPossible },
                { "bonusXp", 0 },
                { "percent", 0f },
                { "status", "Missed" },
                { "score", 0 },
                { "attemptCount", 0 },
                { "remainingAttempts", quiz.MaxAttempts > 0 ? quiz.MaxAttempts : -1 },
                { "completedAt", Timestamp.GetCurrentTimestamp() }
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning("[QuizService] Could not record missed quiz attempt.");
                }
            });
        }

        private void RecordBadgeAwards(string studentUid, List<string> badgeIds, string classroomId, string quizId, string quizName)
        {
            var badgeAwardsCol = Db.Collection("students").Document(studentUid).Collection("badgeAwards");
            var batch = Db.StartBatch();

            foreach (var badgeId in badgeIds)
            {
                batch.Set(badgeAwardsCol.Document(badgeId), new Dictionary<string, object>
                {
                    { "classroomId", classroomId },
                    { "quizId", quizId },
                    { "quizName", quizName },
                    { "earnedAt", Timestamp.GetCurrentTimestamp() }
                });
            }

            batch.CommitAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Debug.LogWarning("[QuizService] Could not record badge award source detail.");
                }
            });
        }

        [Serializable]
        public class BadgeAwardRecord
        {
            public string BadgeId;
            public string ClassroomId;
            public string QuizId;
            public string QuizName;
            public Timestamp EarnedAt;
        }

        /// <summary>Call when showing a badges screen that needs "earned in [classroom]"
        /// detail. Returns one record per badge the student has earned; resolve
        /// ClassroomId to a display name via ClassroomService.FetchClassroomDetail().</summary>
        public void FetchBadgeAwards(Action<List<BadgeAwardRecord>> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(new List<BadgeAwardRecord>()); return; }

            Db.Collection("students").Document(student.Uid).Collection("badgeAwards")
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<BadgeAwardRecord>();
                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            results.Add(new BadgeAwardRecord
                            {
                                BadgeId = doc.Id,
                                ClassroomId = doc.ContainsField("classroomId") ? doc.GetValue<string>("classroomId") : "",
                                QuizId = doc.ContainsField("quizId") ? doc.GetValue<string>("quizId") : "",
                                QuizName = doc.ContainsField("quizName") ? doc.GetValue<string>("quizName") : "",
                                EarnedAt = doc.ContainsField("earnedAt") ? doc.GetValue<Timestamp>("earnedAt") : Timestamp.GetCurrentTimestamp()
                            });
                        }
                    }
                    onComplete?.Invoke(results);
                });
        }

        // ---------------- Recent activity (StudentDashboardController) ----------------

        public enum ActivityType
        {
            QuizCompleted,
            BadgeEarned
        }

        /// <summary>One row for StudentDashboardController's Recent Activity card.</summary>
        [Serializable]
        public class ActivityRecord
        {
            public ActivityType Type;
            public string Title;
            /// <summary>Points to show as "+N"; 0 means don't render a points label
            /// (badge awards don't carry their own point value - the points that
            /// unlocked them were already shown on the quiz-completed entry).</summary>
            public int PointsDelta;
            public Timestamp OccurredAt;

            // ---- Admin-side (AdminDashboardController) card fields ----
            // Only populated for Type == QuizCompleted rows built by
            // ListenToRecentActivityForAdmin / ToAdminActivityRecord - the
            // student-facing FetchRecentActivity() above only ever sets Title/
            // PointsDelta/OccurredAt and leaves these at their defaults, since
            // that feed doesn't need a full card breakdown of the student's own
            // attempt. AdminDashboardController reads these directly instead of
            // re-parsing Title, so keep them in sync with the Firestore fields
            // written in SubmitQuizAttemptInternal if either changes.
            /// <summary>Firestore quizAttempts doc id - stable dedup/diff key for the
            /// live-updating admin activity feed.</summary>
            public string DocId;
            public string StudentName;
            public string QuizTitle;
            public string ClassroomName;
            public int ScoreCorrect;
            public int ScoreTotal;
            /// <summary>0-100.</summary>
            public float ScorePercent;
            /// <summary>"Completed" for a normal submission ("Missed" rows are filtered
            /// out before this class is ever constructed - see ToAdminActivityRecord).</summary>
            public string Status;
        }

        /// <summary>Handle returned by ListenToRecentActivityForAdmin. Fans out one
        /// Firestore listener per classroom (same per-classroom query shape as
        /// FetchRecentActivityForAdmin), so Stop() needs to tear all of them down
        /// together. Keep this and call Stop() in OnDisable, the same way a plain
        /// ListenerRegistration is handled elsewhere (e.g. AdminClassroomService.
        /// ListenToMyClassrooms) - otherwise these keep streaming updates, and
        /// billing you for reads, long after the dashboard is gone.</summary>
        public class ActivityListenerHandle
        {
            private readonly List<ListenerRegistration> _registrations = new List<ListenerRegistration>();
            internal void Add(ListenerRegistration registration)
            {
                if (registration != null) _registrations.Add(registration);
            }

            public void Stop()
            {
                foreach (var registration in _registrations) registration?.Stop();
                _registrations.Clear();
            }
        }

        /// <summary>Call when showing StudentDashboardController's Recent Activity card.
        /// Merges this student's most recent completed quiz attempts (`quizAttempts`,
        /// filtered to `studentId == uid`) with their most recent badge awards
        /// (`students/{uid}/badgeAwards`), sorts newest-first, and trims to
        /// maxItems. Unlike ClassroomService.FetchNotifications, no per-classroom
        /// fan-out is needed here - both source collections are already directly
        /// queryable by this student's uid, so this is always exactly 2 reads
        /// regardless of how many classrooms the student is in.</summary>
        public void FetchRecentActivity(Action<List<ActivityRecord>> onComplete, int maxItems = 8)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(new List<ActivityRecord>()); return; }

            var results = new List<ActivityRecord>();
            int pending = 2;

            void OnPartComplete()
            {
                pending--;
                if (pending > 0) return;

                results.Sort((a, b) => b.OccurredAt.ToDateTime().CompareTo(a.OccurredAt.ToDateTime()));
                if (results.Count > maxItems)
                {
                    results.RemoveRange(maxItems, results.Count - maxItems);
                }
                onComplete?.Invoke(results);
            }

            Db.Collection("quizAttempts")
                .WhereEqualTo("studentId", student.Uid)
                .OrderByDescending("completedAt")
                .Limit(maxItems)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsFaulted)
                    {
                        // Most likely cause: Firestore needs a composite index for this
                        // query (studentId + completedAt) - same situation as
                        // FetchMyScores above. Check the Firebase console (Firestore ->
                        // Indexes) or the exception below for a direct "create index" link.
                        Debug.LogError($"[QuizService] FetchRecentActivity (quizAttempts) failed - " +
                            $"likely a missing Firestore composite index (studentId + completedAt). " +
                            $"Exception: {task.Exception}");
                    }
                    else if (!task.IsCanceled)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            // Skip auto-recorded "Missed" docs (see RecordMissedAttempt) -
                            // those aren't something the student did, so they don't belong
                            // in an activity feed of the student's own actions.
                            bool isMissed = doc.ContainsField("status") && doc.GetValue<string>("status") == "Missed";
                            if (isMissed) continue;

                            string quizName = doc.ContainsField("quizName") ? doc.GetValue<string>("quizName") : "a quiz";
                            int pointsEarned = doc.ContainsField("pointsEarned") ? doc.GetValue<int>("pointsEarned") : 0;
                            int bonusXp = doc.ContainsField("bonusXp") ? doc.GetValue<int>("bonusXp") : 0;

                            results.Add(new ActivityRecord
                            {
                                Type = ActivityType.QuizCompleted,
                                Title = $"Completed '{quizName}' Quiz",
                                PointsDelta = pointsEarned + bonusXp,
                                OccurredAt = doc.ContainsField("completedAt") ? doc.GetValue<Timestamp>("completedAt") : Timestamp.GetCurrentTimestamp(),
                                DocId = doc.Id
                            });
                        }
                    }
                    OnPartComplete();
                });

            Db.Collection("students").Document(student.Uid).Collection("badgeAwards")
                .OrderByDescending("earnedAt")
                .Limit(maxItems)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            results.Add(new ActivityRecord
                            {
                                Type = ActivityType.BadgeEarned,
                                Title = $"Earned '{FormatBadgeName(doc.Id)}' Badge",
                                PointsDelta = 0,
                                OccurredAt = doc.ContainsField("earnedAt") ? doc.GetValue<Timestamp>("earnedAt") : Timestamp.GetCurrentTimestamp(),
                                DocId = doc.Id
                            });
                        }
                    }
                    OnPartComplete();
                });
        }

        /// <summary>Call when showing AdminDashboardController's Recent Activity card.
        /// Same idea as FetchRecentActivity() above but scoped to an admin's
        /// classrooms instead of one student - fans out one `quizAttempts` query
        /// per classroomId (same shape as ClassroomService.FetchRecentJoinsForClassrooms
        /// and FetchNotifications' per-classroom fan-out), merges, sorts newest-first,
        /// and trims to maxItems. Badge awards aren't included here - those are a
        /// personal/per-student thing, not really "what happened in my classrooms".</summary>
        public void FetchRecentActivityForAdmin(List<string> classroomIds, Action<List<ActivityRecord>> onComplete, int maxItems = 8)
        {
            if (classroomIds == null || classroomIds.Count == 0) { onComplete?.Invoke(new List<ActivityRecord>()); return; }

            var results = new List<ActivityRecord>();
            int pending = classroomIds.Count;

            foreach (var classroomId in classroomIds)
            {
                Db.Collection("quizAttempts")
                    .WhereEqualTo("classroomId", classroomId)
                    .OrderByDescending("completedAt")
                    .Limit(maxItems)
                    .GetSnapshotAsync()
                    .ContinueWithOnMainThread(task =>
                    {
                        if (task.IsFaulted)
                        {
                            // Most likely cause: Firestore needs a composite index for this
                            // query (classroomId + completedAt) - check the Firebase console
                            // (Firestore -> Indexes) or the exception below for a direct
                            // "create index" link.
                            Debug.LogError($"[QuizService] FetchRecentActivityForAdmin (quizAttempts) failed for " +
                                $"classroom {classroomId} - likely a missing Firestore composite index " +
                                $"(classroomId + completedAt). Exception: {task.Exception}");
                        }
                        else if (!task.IsCanceled)
                        {
                            foreach (var doc in task.Result.Documents)
                            {
                                // Skip auto-recorded "Missed" docs (see RecordMissedAttempt) -
                                // those aren't something the student did, so they don't
                                // belong in an activity feed.
                                bool isMissed = doc.ContainsField("status") && doc.GetValue<string>("status") == "Missed";
                                if (isMissed) continue;

                                string quizName = doc.ContainsField("quizName") ? doc.GetValue<string>("quizName") : "a quiz";
                                // Older attempt docs predate the studentName field added
                                // above - falls back gracefully rather than showing blank.
                                string studentName = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : "A student";
                                float percent = doc.ContainsField("percent") ? (float)doc.GetValue<double>("percent") : 0f;

                                results.Add(new ActivityRecord
                                {
                                    Type = ActivityType.QuizCompleted,
                                    Title = $"{studentName} completed '{quizName}' ({Mathf.RoundToInt(percent)}%)",
                                    PointsDelta = 0,
                                    OccurredAt = doc.ContainsField("completedAt") ? doc.GetValue<Timestamp>("completedAt") : Timestamp.GetCurrentTimestamp()
                                });
                            }
                        }

                        pending--;
                        if (pending > 0) return;

                        results.Sort((a, b) => b.OccurredAt.ToDateTime().CompareTo(a.OccurredAt.ToDateTime()));
                        if (results.Count > maxItems)
                        {
                            results.RemoveRange(maxItems, results.Count - maxItems);
                        }
                        onComplete?.Invoke(results);
                    });
            }
        }

        /// <summary>Live version of FetchRecentActivityForAdmin() - call when showing
        /// AdminDashboardController, keep the returned handle and Stop() it in
        /// OnDisable (mirrors how AdminClassroomService.ListenToMyClassrooms /
        /// its ListenerRegistration is handled). Fans out one real-time listener
        /// per classroom - same query shape as the one-shot version, just
        /// swapping GetSnapshotAsync() for Listen() - so this fires onUpdate
        /// immediately with the current top items (same as a fetch), then again
        /// the moment any of those classrooms' quizAttempts changes for ANY
        /// reason, including a student submitting a quiz from their own device
        /// right now. Each classroom's own last-known top-N list is cached and
        /// re-merged/re-sorted/trimmed on every single update (from whichever
        /// classroom changed), so onUpdate always receives one complete,
        /// de-duplicated, newest-first list - the caller never has to reason
        /// about which classroom triggered the update or stitch anything
        /// together itself. That also makes reconnects/re-subscribes safe: a
        /// fresh Listen() call always redelivers a full current snapshot first,
        /// so nothing is ever double-counted even if the dashboard is closed and
        /// reopened.</summary>
        public ActivityListenerHandle ListenToRecentActivityForAdmin(
            List<(string classroomId, string classroomName)> classrooms,
            Action<List<ActivityRecord>> onUpdate,
            int maxItemsPerClassroom = 8,
            int maxItemsTotal = 8)
        {
            var handle = new ActivityListenerHandle();
            if (classrooms == null || classrooms.Count == 0) { onUpdate?.Invoke(new List<ActivityRecord>()); return handle; }

            // Latest known top-N rows per classroom, keyed by classroomId. Rebuilt
            // wholesale from that classroom's own snapshot whenever it fires, then
            // every classroom's cached rows are flattened/sorted/trimmed together
            // below - this is what lets N independent per-classroom listeners
            // behave like one merged feed without ever re-querying Firestore.
            var latestByClassroom = new Dictionary<string, List<ActivityRecord>>();

            void PushMerged()
            {
                var merged = latestByClassroom.Values
                    .SelectMany(rows => rows)
                    .OrderByDescending(r => r.OccurredAt.ToDateTime())
                    .Take(maxItemsTotal)
                    .ToList();
                onUpdate?.Invoke(merged);
            }

            foreach (var (classroomId, classroomName) in classrooms)
            {
                if (string.IsNullOrEmpty(classroomId)) continue;

                latestByClassroom[classroomId] = new List<ActivityRecord>();

                var registration = Db.Collection("quizAttempts")
                    .WhereEqualTo("classroomId", classroomId)
                    .OrderByDescending("completedAt")
                    .Limit(maxItemsPerClassroom)
                    .Listen(snapshot =>
                    {
                        var rows = new List<ActivityRecord>();
                        foreach (var doc in snapshot.Documents)
                        {
                            // Skip auto-recorded "Missed" docs (see RecordMissedAttempt) -
                            // those aren't a submission, so they don't belong in an
                            // activity feed of quizzes students actually finished.
                            bool isMissed = doc.ContainsField("status") && doc.GetValue<string>("status") == "Missed";
                            if (isMissed) continue;

                            rows.Add(ToAdminActivityRecord(doc, classroomName));
                        }

                        latestByClassroom[classroomId] = rows;
                        PushMerged();
                    });

                handle.Add(registration);
            }

            return handle;
        }

        /// <summary>Builds one admin-facing ActivityRecord (student/quiz/classroom/score/
        /// status card fields, not just the flattened Title string FetchRecentActivityForAdmin
        /// builds) from a quizAttempts doc. Falls back to the legacy scoreCorrect/scoreTotal-less
        /// correctCount/incorrectCount fields for attempts written before those were added
        /// (see the "Duplicated under..." comment in SubmitQuizAttemptInternal).</summary>
        private static ActivityRecord ToAdminActivityRecord(DocumentSnapshot doc, string classroomName)
        {
            string studentName = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : "A student";
            string quizTitle = doc.ContainsField("quizTitle")
                ? doc.GetValue<string>("quizTitle")
                : (doc.ContainsField("quizName") ? doc.GetValue<string>("quizName") : "a quiz");

            int scoreCorrect = doc.ContainsField("scoreCorrect")
                ? doc.GetValue<int>("scoreCorrect")
                : (doc.ContainsField("correctCount") ? doc.GetValue<int>("correctCount") : 0);
            int scoreTotal = doc.ContainsField("scoreTotal")
                ? doc.GetValue<int>("scoreTotal")
                : scoreCorrect + (doc.ContainsField("incorrectCount") ? doc.GetValue<int>("incorrectCount") : 0);

            float percent = doc.ContainsField("percent") ? Convert.ToSingle(doc.GetValue<double>("percent")) : 0f;
            string status = doc.ContainsField("status") ? doc.GetValue<string>("status") : "Completed";

            return new ActivityRecord
            {
                Type = ActivityType.QuizCompleted,
                DocId = doc.Id,
                StudentName = studentName,
                QuizTitle = quizTitle,
                ClassroomName = string.IsNullOrEmpty(classroomName) ? "Classroom" : classroomName,
                ScoreCorrect = scoreCorrect,
                ScoreTotal = scoreTotal,
                ScorePercent = percent,
                Status = status,
                Title = $"{studentName} completed '{quizTitle}' ({Mathf.RoundToInt(percent)}%)",
                PointsDelta = 0,
                OccurredAt = doc.ContainsField("completedAt") ? doc.GetValue<Timestamp>("completedAt") : Timestamp.GetCurrentTimestamp()
            };
        }

        /// <summary>Turns a badge doc id (e.g. "quiz-master", from AdminGamificationService's
        /// default badges - see BadgeEntry/MakeBadgeId) into a display-friendly title
        /// ("Quiz Master") without an extra read of the teacher's gamificationSettings
        /// doc. Good enough for the dashboard preview; a screen that needs the exact
        /// configured badge name/icon should resolve it via AdminGamificationService
        /// like StudentAchievementsController already does.</summary>
        private static string FormatBadgeName(string badgeId)
        {
            if (string.IsNullOrEmpty(badgeId)) return "New";

            var parts = badgeId.Split('-');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
                }
            }
            return string.Join(" ", parts);
        }

        /// <summary>Call when showing StudentQuizSelectionController - feeds SetQuizStats() directly.</summary>
        public void FetchQuizStats(string quizId, Action<bool, string, int, int, float> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(false, "Not signed in.", 0, 0, 0f); return; }
            Db.Collection("quizAttempts")
                .WhereEqualTo("studentId", student.Uid)
                .WhereEqualTo("quizId", quizId)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        onComplete?.Invoke(false, "Could not fetch quiz stats.", 0, 0, 0f);
                        return;
                    }
                    var attempts = task.Result.Documents.Select(doc => new
                    {
                        CorrectCount = doc.ContainsField("correctCount") ? doc.GetValue<int>("correctCount") : 0,
                        IncorrectCount = doc.ContainsField("incorrectCount") ? doc.GetValue<int>("incorrectCount") : 0,
                        Percent = doc.ContainsField("percent") ? Convert.ToSingle(doc.GetValue<double>("percent")) : 0f
                    }).ToList();
                    int totalAttempts = attempts.Count;
                    int totalCorrect = attempts.Sum(a => a.CorrectCount);
                    int totalIncorrect = attempts.Sum(a => a.IncorrectCount);
                    float avgPercent = totalAttempts > 0 ? attempts.Average(a => a.Percent) : 0f;
                    onComplete?.Invoke(true, null, totalAttempts, totalCorrect + totalIncorrect, avgPercent);
                });
        }

        /// <summary>Call when showing StudentProgressController - feeds SetProgressData() /
        /// SetWeeklyPoints() / SetCategoryProgress() directly.</summary>
        public void FetchProgressData(Action<bool, ProgressResult> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(false, null); return; }

            var studentRef = Db.Collection("students").Document(student.Uid);
            // A student's overall progress spans every classroom (and thus every
            // teacher) they're enrolled in, so there's no single teacher's
            // points/badges to score against here - only the shared global levels
            // doc is needed to compute level/progress from their totalPoints.
            var levelsRef = AdminGamificationService.Instance.LevelsRef;

            var studentTask = studentRef.GetSnapshotAsync();
            var levelsTask = levelsRef.GetSnapshotAsync();

            System.Threading.Tasks.Task.WhenAll(studentTask, levelsTask).ContinueWithOnMainThread(_ =>
            {
                if (studentTask.IsFaulted || !studentTask.Result.Exists)
                {
                    onComplete?.Invoke(false, null);
                    return;
                }

                var studentSnap = studentTask.Result;
                int totalPoints = studentSnap.ContainsField("totalPoints") ? studentSnap.GetValue<int>("totalPoints") : 0;
                int quizzesCompleted = studentSnap.ContainsField("quizzesCompleted") ? studentSnap.GetValue<int>("quizzesCompleted") : 0;
                int badgeCount = studentSnap.ContainsField("badgesEarned") ? studentSnap.GetValue<List<string>>("badgesEarned").Count : 0;

                var settings = AdminGamificationService.ToSettings(null, levelsTask.IsFaulted ? null : levelsTask.Result);
                var levelInfo = AdminGamificationService.ComputeLevelProgress(settings, totalPoints);

                FetchStudentAttempts(attempts =>
                {
                    var result = new ProgressResult
                    {
                        CurrentLevel = levelInfo.level,
                        CurrentLevelTitle = levelInfo.title,
                        NextLevel = levelInfo.nextLevel,
                        NextLevelTitle = levelInfo.nextTitle,
                        LevelProgress01 = levelInfo.progress01,
                        PointsToNextLevel = levelInfo.pointsToNext,
                        QuizzesCompleted = quizzesCompleted,
                        AvgScorePercent = attempts.Count > 0 ? (float)attempts.Average(a => a.Percent) : 0f,
                        TotalPoints = totalPoints,
                        BadgesEarnedCount = badgeCount,
                        WeeklyPoints = ComputeWeeklyPoints(attempts),
                        CategoryBreakdown = ComputeCategoryBreakdown(attempts)
                    };

                    onComplete?.Invoke(true, result);
                });
            });
        }

        /// <summary>Points + timestamp only, one entry per quizAttempts doc for
        /// the signed-in student - for StudentProgressController's Weekly
        /// Activity Panel, which merges this with Anatomy Play Mode's local
        /// records into a single points-per-day chart. Reuses the exact same
        /// FetchStudentAttempts query FetchProgressData already runs (the
        /// project's one source of truth for a student's quiz points) rather
        /// than adding a second quizAttempts read, so nothing here can ever
        /// double-count against FetchProgressData's own numbers.</summary>
        public void FetchStudentAttemptPoints(Action<List<(float points, DateTime completedAtUtc)>> onComplete)
        {
            FetchStudentAttempts(attempts =>
            {
                onComplete?.Invoke(attempts.Select(a => (a.PointsPlusBonus, a.CompletedAtUtc)).ToList());
            });
        }

        // ==================================================================
        // Admin: classroom-scoped analytics report (AdminAnalyticsReportsController)
        // ==================================================================

        [Serializable]
        public class QuizScoreSummary
        {
            public string QuizId;
            public string QuizTitle;
            public float AvgScorePercent;
        }

        [Serializable]
        public class CategoryScoreSummary
        {
            public string Category;
            public float AvgScorePercent;
        }

        /// <summary>One row in the "Common Incorrect Answers" list - a question text plus how
        /// many times it was answered wrong across this classroom's attempts. Only populated
        /// from attempts whose submitter passed a questionResults list into SubmitQuizAttempt
        /// (see that method's doc comment) - attempts submitted before that data existed just
        /// don't contribute any mistake rows.</summary>
        [Serializable]
        public class MistakeSummary
        {
            public string QuestionText;
            public string Category;
            public int ErrorCount;
        }

        [Serializable]
        public class ClassroomReportData
        {
            public List<QuizScoreSummary> ScoreTrend = new List<QuizScoreSummary>();
            public List<CategoryScoreSummary> TopicPerformance = new List<CategoryScoreSummary>();
            public List<MistakeSummary> TopMistakes = new List<MistakeSummary>();
        }

        /// <summary>Call for the Performance and Mistakes tabs of AdminAnalyticsReportsController.
        /// Reads every quizAttempts doc for the given classroom once and buckets it three ways:
        /// by quiz (Score Trend), by category (Topic Performance), and by question text, counting
        /// only wrong answers (Common Incorrect Answers, top 10).</summary>
        public void FetchClassroomReportData(string classroomId, Action<ClassroomReportData> onComplete)
        {
            var result = new ClassroomReportData();
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(result); return; }

            Db.Collection("quizAttempts")
                .WhereEqualTo("classroomId", classroomId)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted) { onComplete?.Invoke(result); return; }

                    var byQuiz = new Dictionary<string, (string title, List<float> percents)>();
                    var byCategory = new Dictionary<string, List<float>>();
                    var mistakeCounts = new Dictionary<string, (string category, int count)>();

                    foreach (var doc in task.Result.Documents)
                    {
                        string quizId = doc.ContainsField("quizId") ? doc.GetValue<string>("quizId") : "";
                        string quizName = doc.ContainsField("quizName") ? doc.GetValue<string>("quizName") : "Quiz";
                        string category = doc.ContainsField("category") ? doc.GetValue<string>("category") : "";
                        float percent = doc.ContainsField("percent") ? Convert.ToSingle(doc.GetValue<double>("percent")) : 0f;

                        if (!string.IsNullOrEmpty(quizId))
                        {
                            if (!byQuiz.TryGetValue(quizId, out var quizEntry))
                            {
                                quizEntry = (quizName, new List<float>());
                            }
                            quizEntry.percents.Add(percent);
                            byQuiz[quizId] = quizEntry;
                        }

                        if (!string.IsNullOrEmpty(category))
                        {
                            if (!byCategory.TryGetValue(category, out var percents))
                            {
                                percents = new List<float>();
                            }
                            percents.Add(percent);
                            byCategory[category] = percents;
                        }

                        if (doc.ContainsField("questionResults"))
                        {
                            var raw = doc.GetValue<List<object>>("questionResults");
                            foreach (var item in raw)
                            {
                                if (item is Dictionary<string, object> map)
                                {
                                    bool correct = map.TryGetValue("correct", out var correctVal) && Convert.ToBoolean(correctVal);
                                    if (correct) continue;

                                    string questionText = map.TryGetValue("questionText", out var qt) ? qt.ToString() : "";
                                    if (string.IsNullOrEmpty(questionText)) continue;

                                    if (!mistakeCounts.TryGetValue(questionText, out var mistake))
                                    {
                                        mistake = (category, 0);
                                    }
                                    mistakeCounts[questionText] = (mistake.category, mistake.count + 1);
                                }
                            }
                        }
                    }

                    foreach (var kvp in byQuiz)
                    {
                        result.ScoreTrend.Add(new QuizScoreSummary
                        {
                            QuizId = kvp.Key,
                            QuizTitle = kvp.Value.title,
                            AvgScorePercent = kvp.Value.percents.Count > 0 ? kvp.Value.percents.Average() : 0f
                        });
                    }

                    foreach (var kvp in byCategory)
                    {
                        result.TopicPerformance.Add(new CategoryScoreSummary
                        {
                            Category = kvp.Key,
                            AvgScorePercent = kvp.Value.Count > 0 ? kvp.Value.Average() : 0f
                        });
                    }

                    result.TopMistakes = mistakeCounts
                        .Select(kvp => new MistakeSummary
                        {
                            QuestionText = kvp.Key,
                            Category = kvp.Value.category,
                            ErrorCount = kvp.Value.count
                        })
                        .OrderByDescending(m => m.ErrorCount)
                        .Take(10)
                        .ToList();

                    onComplete?.Invoke(result);
                });
        }

        /// <summary>Call for the four overview stat cards on AdminAnalyticsReportsController.
        /// Buckets this classroom's quizAttempts into "this calendar month" vs "last calendar
        /// month" (both in UTC) and reports each stat plus its month-over-month delta - a
        /// previous-month value of 0 reports +100% if this month has activity, 0% otherwise.</summary>
        [Serializable]
        public class OverviewStats
        {
            public int ActiveUsers;
            public float ActiveUsersDeltaPercent;
            public float AvgScorePercent;
            public float AvgScoreDeltaPercent;
            public int QuizzesDone;
            public float QuizzesDoneDeltaPercent;
            public float CompletionPercent;
            public float CompletionDeltaPercent;
        }

        public void FetchClassroomOverviewStats(string classroomId, Action<OverviewStats> onComplete)
        {
            var result = new OverviewStats();
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(result); return; }

            Db.Collection("quizAttempts")
                .WhereEqualTo("classroomId", classroomId)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted) { onComplete?.Invoke(result); return; }

                    var now = DateTime.UtcNow;
                    var thisMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                    var lastMonthStart = thisMonthStart.AddMonths(-1);

                    var thisMonth = new List<DocumentSnapshot>();
                    var lastMonth = new List<DocumentSnapshot>();

                    foreach (var doc in task.Result.Documents)
                    {
                        var completedAt = doc.ContainsField("completedAt")
                            ? doc.GetValue<Timestamp>("completedAt").ToDateTime()
                            : now;

                        if (completedAt >= thisMonthStart) thisMonth.Add(doc);
                        else if (completedAt >= lastMonthStart) lastMonth.Add(doc);
                    }

                    int activeThisMonth = CountDistinctStudents(thisMonth);
                    int activeLastMonth = CountDistinctStudents(lastMonth);

                    result.ActiveUsers = activeThisMonth;
                    result.QuizzesDone = thisMonth.Count;
                    result.AvgScorePercent = AveragePercent(thisMonth);
                    result.CompletionPercent = CompletionRate(thisMonth);

                    float lastAvgScore = AveragePercent(lastMonth);
                    float lastCompletion = CompletionRate(lastMonth);

                    result.ActiveUsersDeltaPercent = PercentDelta(activeThisMonth, activeLastMonth);
                    result.QuizzesDoneDeltaPercent = PercentDelta(result.QuizzesDone, lastMonth.Count);
                    result.AvgScoreDeltaPercent = result.AvgScorePercent - lastAvgScore;
                    result.CompletionDeltaPercent = result.CompletionPercent - lastCompletion;

                    onComplete?.Invoke(result);
                });
        }

        private static int CountDistinctStudents(List<DocumentSnapshot> docs)
        {
            return docs
                .Where(d => d.ContainsField("studentId"))
                .Select(d => d.GetValue<string>("studentId"))
                .Distinct()
                .Count();
        }

        private static float AveragePercent(List<DocumentSnapshot> docs)
        {
            if (docs.Count == 0) return 0f;
            return docs.Average(d => d.ContainsField("percent") ? Convert.ToSingle(d.GetValue<double>("percent")) : 0f);
        }

        private static float CompletionRate(List<DocumentSnapshot> docs)
        {
            if (docs.Count == 0) return 0f;
            int passed = docs.Count(d => d.ContainsField("passed") && d.GetValue<bool>("passed"));
            return (passed / (float)docs.Count) * 100f;
        }

        private static float PercentDelta(int current, int previous)
        {
            if (previous == 0) return current > 0 ? 100f : 0f;
            return ((current - previous) / (float)previous) * 100f;
        }

        // ==================================================================
        // Internal helpers
        // ==================================================================

        private class AttemptRow
        {
            public string Category;
            public float Percent;
            public float PointsPlusBonus;
            public DateTime CompletedAtUtc;
        }

        private void FetchStudentAttempts(Action<List<AttemptRow>> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(new List<AttemptRow>()); return; }

            Db.Collection("quizAttempts")
                .WhereEqualTo("studentId", student.Uid)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var rows = new List<AttemptRow>();

                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            rows.Add(new AttemptRow
                            {
                                Category = doc.ContainsField("category") ? doc.GetValue<string>("category") : "",
                                Percent = doc.ContainsField("percent") ? Convert.ToSingle(doc.GetValue<double>("percent")) : 0f,
                                PointsPlusBonus = (doc.ContainsField("pointsEarned") ? doc.GetValue<int>("pointsEarned") : 0)
                                    + (doc.ContainsField("bonusXp") ? doc.GetValue<int>("bonusXp") : 0),
                                CompletedAtUtc = doc.ContainsField("completedAt") ? doc.GetValue<Timestamp>("completedAt").ToDateTime() : DateTime.UtcNow
                            });
                        }
                    }

                    onComplete?.Invoke(rows);
                });
        }

        private static float[] ComputeWeeklyPoints(List<AttemptRow> attempts)
        {
            var points = new float[7]; // Mon..Sun

            var now = DateTime.UtcNow;
            int daysSinceMonday = ((int)now.DayOfWeek + 6) % 7; // Sunday=0 in DayOfWeek -> shift so Monday=0
            var mondayStart = now.Date.AddDays(-daysSinceMonday);
            var nextMonday = mondayStart.AddDays(7);

            foreach (var attempt in attempts)
            {
                if (attempt.CompletedAtUtc >= mondayStart && attempt.CompletedAtUtc < nextMonday)
                {
                    int dayIndex = ((int)attempt.CompletedAtUtc.DayOfWeek + 6) % 7;
                    points[dayIndex] += attempt.PointsPlusBonus;
                }
            }

            return points;
        }

        private static Dictionary<string, (int quizzes, float percent01)> ComputeCategoryBreakdown(List<AttemptRow> attempts)
        {
            var result = new Dictionary<string, (int, float)>();

            foreach (var category in KnownCategories)
            {
                var inCategory = attempts.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
                int count = inCategory.Count;
                float avgPercent01 = count > 0 ? (float)(inCategory.Average(a => a.Percent) / 100.0) : 0f;
                result[category] = (count, avgPercent01);
            }

            return result;
        }

        private static Dictionary<string, object> QuestionToMap(QuestionRecord q)
        {
            return new Dictionary<string, object>
            {
                { "questionText", q.QuestionText },
                { "questionTypeSlug", q.QuestionTypeSlug },
                { "options", q.Options ?? new List<string>() },
                { "correctAnswer", q.CorrectAnswer },
                { "difficulty", q.Difficulty },
                { "points", q.Points },
                // Empty string (not null) for non-Image-Based questions - Firestore
                // dictionary values can't be C# null here without extra handling, and
                // an absent/empty field reads back the same way via TryGetValue below.
                { "anatomySystemKey", q.AnatomySystemKey ?? string.Empty },
                { "anatomySystemDisplayName", q.AnatomySystemDisplayName ?? string.Empty },
                { "structureKey", q.StructureKey ?? string.Empty },
                { "structureDisplayName", q.StructureDisplayName ?? string.Empty }
            };
        }

        private static QuizRecord ToQuizRecord(DocumentSnapshot doc)
        {
            var record = new QuizRecord
            {
                QuizId = doc.Id,
                Title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "",
                Category = doc.ContainsField("category") ? doc.GetValue<string>("category") : "",
                ClassroomId = doc.ContainsField("classroomId") ? doc.GetValue<string>("classroomId") : null,
                CreatedBy = doc.ContainsField("createdBy") ? doc.GetValue<string>("createdBy") : "",
                PointsPossible = doc.ContainsField("pointsPossible") ? doc.GetValue<int>("pointsPossible") : 0,
                PassingScorePercent = doc.ContainsField("passingScorePercent") ? doc.GetValue<int>("passingScorePercent") : 70,
                MaxAttempts = doc.ContainsField("maxAttempts") ? doc.GetValue<int>("maxAttempts") : 0,
                // Falls back to the legacy timeLimitSeconds field (pre-dates the minutes-based
                // config) for quizzes created before this feature existed.
                TimeLimitMinutes = doc.ContainsField("timeLimitMinutes")
                    ? doc.GetValue<int>("timeLimitMinutes")
                    : (doc.ContainsField("timeLimitSeconds") ? Mathf.Max(1, doc.GetValue<int>("timeLimitSeconds") / 60) : 10),
                HasTimeLimit = doc.ContainsField("hasTimeLimit") ? doc.GetValue<bool>("hasTimeLimit") : true,
                IsDeadlineEnabled = doc.ContainsField("isDeadlineEnabled") ? doc.GetValue<bool>("isDeadlineEnabled") : false,
                DeadlineUtc = doc.ContainsField("deadline") && doc.GetValue<object>("deadline") != null
                    ? doc.GetValue<Timestamp>("deadline").ToDateTime()
                    : (DateTime?)null
            };

            if (doc.ContainsField("questions"))
            {
                var raw = doc.GetValue<List<object>>("questions");
                foreach (var item in raw)
                {
                    if (item is Dictionary<string, object> map)
                    {
                        record.Questions.Add(new QuestionRecord
                        {
                            QuestionText = map.TryGetValue("questionText", out var qt) ? qt.ToString() : "",
                            QuestionTypeSlug = map.TryGetValue("questionTypeSlug", out var qs) ? qs.ToString() : "multiple-choice",
                            Options = map.TryGetValue("options", out var opts) && opts is List<object> optList
                                ? optList.Select(o => o.ToString()).ToList()
                                : new List<string>(),
                            CorrectAnswer = map.TryGetValue("correctAnswer", out var ca) ? ca.ToString() : "",
                            Difficulty = map.TryGetValue("difficulty", out var diff) ? diff.ToString() : "medium",
                            Points = map.TryGetValue("points", out var pts) ? Convert.ToInt32(pts) : 0,
                            AnatomySystemKey = map.TryGetValue("anatomySystemKey", out var ask) ? ask.ToString() : "",
                            AnatomySystemDisplayName = map.TryGetValue("anatomySystemDisplayName", out var asd) ? asd.ToString() : "",
                            StructureKey = map.TryGetValue("structureKey", out var sk) ? sk.ToString() : "",
                            StructureDisplayName = map.TryGetValue("structureDisplayName", out var sd) ? sd.ToString() : ""
                        });
                    }
                }
            }

            return record;
        }
    }
}