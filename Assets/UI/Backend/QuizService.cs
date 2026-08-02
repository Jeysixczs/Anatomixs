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
            public int TimeLimitSeconds;
            public int PassingScorePercent;
            public List<QuestionRecord> Questions = new List<QuestionRecord>();
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

        /// <summary>Call from AdminQuizManagementController.OnCreateQuizSubmitClicked().</summary>
        public void CreateQuiz(
            string title,
            string category,
            int timeLimitSeconds,
            int passingScorePercent,
            string classroomId,
            Action<bool, string, QuizRecord> onComplete)
        {
            var admin = AdminAuthService.Instance.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }

            var quizRef = Db.Collection("quizzes").Document(); // auto id

            var batch = Db.StartBatch();
            batch.Set(quizRef, new Dictionary<string, object>
            {
                { "title", title },
                { "category", category },
                { "classroomId", classroomId },
                { "createdBy", admin.Uid },
                { "pointsPossible", 0 },
                { "timeLimitSeconds", timeLimitSeconds },
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

                onComplete?.Invoke(true, null, new QuizRecord
                {
                    QuizId = quizRef.Id,
                    Title = title,
                    Category = category,
                    ClassroomId = classroomId,
                    CreatedBy = admin.Uid,
                    PointsPossible = 0,
                    TimeLimitSeconds = timeLimitSeconds,
                    PassingScorePercent = passingScorePercent,
                    Questions = new List<QuestionRecord>()
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
            Action<bool, string, AttemptResult> onComplete)
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

                transaction.Set(attemptRef, new Dictionary<string, object>
                {
                    { "studentId", student.Uid },
                    { "quizId", quizId },
                    { "quizName", quizName },
                    { "category", category },
                    { "classroomId", classroomId },
                    { "correctCount", correctCount },
                    { "incorrectCount", incorrectCount },
                    { "pointsEarned", pointsEarned },
                    { "pointsPossible", pointsPossible },
                    { "bonusXp", bonusXp },
                    { "percent", percent },
                    { "completedAt", Timestamp.GetCurrentTimestamp() }
                });

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
                { "points", q.Points }
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
                TimeLimitSeconds = doc.ContainsField("timeLimitSeconds") ? doc.GetValue<int>("timeLimitSeconds") : 600,
                PassingScorePercent = doc.ContainsField("passingScorePercent") ? doc.GetValue<int>("passingScorePercent") : 70
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
                            Points = map.TryGetValue("points", out var pts) ? Convert.ToInt32(pts) : 0
                        });
                    }
                }
            }

            return record;
        }
    }
}