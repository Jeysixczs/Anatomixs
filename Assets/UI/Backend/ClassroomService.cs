using System;
using System.Collections.Generic;
using System.Linq;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Student-side classroom operations, backed by Firestore. This is the
    /// "ClassroomService" referenced in StudentClassroomController's join flow,
    /// and it also powers StudentClassroomHubController's "My Classrooms" list.
    ///
    /// Attach to the same persistent GameObject as FirebaseBootstrap/UIManager.
    /// Requires PlayerSessionManager.CurrentStudent to be set (i.e. the student
    /// is signed in) before calling any method here.
    /// </summary>
    public class ClassroomService : MonoBehaviour
    {
        public static ClassroomService Instance { get; private set; }

        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>Call from StudentClassroomController.OnJoinClassroomSubmitClicked() after validation.</summary>
        public void JoinClassroom(string code, Action<bool, string> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(false, "Not signed in."); return; }

            var codeRef = Db.Collection("classroomCodes").Document(code);
            codeRef.GetSnapshotAsync().ContinueWithOnMainThread(codeTask =>
            {
                if (codeTask.IsCanceled || codeTask.IsFaulted || !codeTask.Result.Exists)
                {
                    onComplete?.Invoke(false, "That classroom code doesn't exist.");
                    return;
                }

                string classroomId = codeTask.Result.GetValue<string>("classroomId");
                var classroomRef = Db.Collection("classrooms").Document(classroomId);
                var memberRef = classroomRef.Collection("members").Document(student.Uid);
                var studentRef = Db.Collection("students").Document(student.Uid);

                Db.RunTransactionAsync(async transaction =>
                {
                    var classroomSnap = await transaction.GetSnapshotAsync(classroomRef);
                    if (!classroomSnap.Exists)
                    {
                        throw new InvalidOperationException("Classroom no longer exists.");
                    }

                    var memberIds = classroomSnap.ContainsField("memberIds")
                        ? classroomSnap.GetValue<List<string>>("memberIds")
                        : new List<string>();

                    if (memberIds.Contains(student.Uid))
                    {
                        throw new InvalidOperationException("You're already in this classroom.");
                    }

                    memberIds.Add(student.Uid);

                    transaction.Update(classroomRef, new Dictionary<string, object>
                    {
                        { "memberIds", memberIds },
                        { "studentCount", memberIds.Count }
                    });

                    transaction.Set(memberRef, new Dictionary<string, object>
                    {
                        { "studentName", student.FullName },
                        { "joinedAt", Timestamp.GetCurrentTimestamp() }
                    });

                    transaction.Update(studentRef, "enrolledClassroomIds", FieldValue.ArrayUnion(classroomId));
                }).ContinueWithOnMainThread(txTask =>
                {
                    if (txTask.IsCanceled || txTask.IsFaulted)
                    {
                        string message = txTask.Exception?.InnerException?.Message ?? "Could not join classroom.";
                        onComplete?.Invoke(false, message);
                        return;
                    }

                    onComplete?.Invoke(true, null);
                });
            });
        }

        [Serializable]
        public class ClassroomRecord
        {
            public string ClassroomId;
            public string Name;
            public string Code;
            public string TeacherName;
            public int StudentCount;
        }

        /// <summary>
        /// Call when showing StudentClassroomHubController - feeds
        /// SetHeaderStats() / SetClassrooms() directly.
        /// </summary>
        public void FetchMyClassrooms(Action<List<ClassroomRecord>> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(new List<ClassroomRecord>()); return; }

            Db.Collection("classrooms")
                .WhereArrayContains("memberIds", student.Uid)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<ClassroomRecord>();

                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            results.Add(new ClassroomRecord
                            {
                                ClassroomId = doc.Id,
                                Name = doc.GetValue<string>("name"),
                                Code = doc.GetValue<string>("code"),
                                TeacherName = doc.GetValue<string>("teacherName"),
                                StudentCount = doc.ContainsField("studentCount") ? doc.GetValue<int>("studentCount") : 0
                            });
                        }
                    }

                    onComplete?.Invoke(results);
                });
        }

        // ---------------- Classroom detail (StudentClassroomDetailController) ----------------

        [Serializable]
        public class ClassroomDetailRecord
        {
            public string ClassroomId;
            public string Name;
            public string Description;
            public string Code;
            public string TeacherId;
            public string TeacherName;
            public int StudentCount;
            public List<string> PublishedQuizIds = new List<string>();
            public bool LeaderboardVisible;
        }

        /// <summary>Call when showing StudentClassroomDetailController - feeds the header
        /// stats and Overview tab's Classroom Info card.</summary>
        public void FetchClassroomDetail(string classroomId, Action<ClassroomDetailRecord> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(null); return; }

            Db.Collection("classrooms").Document(classroomId).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists) { onComplete?.Invoke(null); return; }

                var doc = task.Result;
                onComplete?.Invoke(new ClassroomDetailRecord
                {
                    ClassroomId = doc.Id,
                    Name = doc.GetValue<string>("name"),
                    Description = doc.ContainsField("description") ? doc.GetValue<string>("description") : "",
                    Code = doc.GetValue<string>("code"),
                    TeacherId = doc.GetValue<string>("teacherId"),
                    TeacherName = doc.GetValue<string>("teacherName"),
                    StudentCount = doc.ContainsField("studentCount") ? doc.GetValue<int>("studentCount") : 0,
                    PublishedQuizIds = doc.ContainsField("publishedQuizIds") ? doc.GetValue<List<string>>("publishedQuizIds") : new List<string>(),
                    LeaderboardVisible = doc.ContainsField("leaderboardVisible") && doc.GetValue<bool>("leaderboardVisible")
                });
            });
        }

        // ---------------- Announcements (read-only here - posted by AdminClassroomService) ----------------

        /// <summary>Shares the `classrooms/{id}/announcements` subcollection with
        /// AdminClassroomService.AnnouncementRecord - same fields, declared separately
        /// so this side doesn't need to depend on the admin service.</summary>
        [Serializable]
        public class AnnouncementRecord
        {
            public string AnnouncementId;
            public string Title;
            public string Body;
            public Timestamp CreatedAt;
        }

        /// <summary>Call when showing the Overview tab. Most recent first.</summary>
        public void FetchAnnouncements(string classroomId, Action<List<AnnouncementRecord>> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(new List<AnnouncementRecord>()); return; }

            Db.Collection("classrooms").Document(classroomId).Collection("announcements")
                .OrderByDescending("createdAt")
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<AnnouncementRecord>();
                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            results.Add(new AnnouncementRecord
                            {
                                AnnouncementId = doc.Id,
                                Title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "",
                                Body = doc.ContainsField("body") ? doc.GetValue<string>("body") : "",
                                CreatedAt = doc.ContainsField("createdAt") ? doc.GetValue<Timestamp>("createdAt") : Timestamp.GetCurrentTimestamp()
                            });
                        }
                    }
                    onComplete?.Invoke(results);
                });
        }

        // ---------------- Students / roster / leaderboard ----------------

        /// <summary>One row from the classroom's `members` subcollection. `Points` /
        /// `QuizzesCompleted` / `AvgScorePercent` / `Level` are denormalized there by
        /// RecordQuizCompletion() below - see its doc comment.</summary>
        [Serializable]
        public class MemberStat
        {
            public string StudentId;
            public string Name;
            public int Level;
            public int Points;
            public int QuizzesCompleted;
            public float AvgScorePercent;
        }

        /// <summary>Call when showing the Students tab (roster, unsorted / join order)
        /// and the Leaderboard tab (same data, sorted by points - see
        /// FetchClassroomAnalytics-style sorting in AdminClassroomService if you want
        /// the exact same tie-break rule on both sides).</summary>
        public void FetchClassroomRoster(string classroomId, Action<List<MemberStat>> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(new List<MemberStat>()); return; }

            Db.Collection("classrooms").Document(classroomId).Collection("members")
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<MemberStat>();
                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            results.Add(new MemberStat
                            {
                                StudentId = doc.Id,
                                Name = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : "Student",
                                Level = doc.ContainsField("level") ? doc.GetValue<int>("level") : 1,
                                Points = doc.ContainsField("points") ? doc.GetValue<int>("points") : 0,
                                QuizzesCompleted = doc.ContainsField("quizzesCompleted") ? doc.GetValue<int>("quizzesCompleted") : 0,
                                AvgScorePercent = doc.ContainsField("avgScorePercent") ? (float)doc.GetValue<double>("avgScorePercent") : 0f
                            });
                        }
                    }
                    onComplete?.Invoke(results);
                });
        }

        // ---------------- Available quizzes ----------------

        /// <summary>One card in the Available Quizzes tab. Field names here mirror
        /// QuizService.QuizRecord / QuestionRecord as used by AdminQuizManagementController -
        /// adjust ToQuizSummary() below if your actual `quizzes/{id}` doc shape differs.</summary>
        [Serializable]
        public class QuizSummary
        {
            public string QuizId;
            public string Title;
            public string Category;
            public int QuestionCount;
            public int TimeLimitSeconds;
            public int TotalPoints;
            public string Difficulty; // hardest difficulty among the quiz's questions
        }

        /// <summary>Call when showing the Available Quizzes tab. Reads the classroom's
        /// publishedQuizIds (set by AdminClassroomService.SetQuizPublished) then fetches
        /// just those quiz docs.</summary>
        public void FetchAvailableQuizzes(string classroomId, Action<List<QuizSummary>> onComplete)
        {
            FetchClassroomDetail(classroomId, detail =>
            {
                if (detail?.PublishedQuizIds == null || detail.PublishedQuizIds.Count == 0)
                {
                    onComplete?.Invoke(new List<QuizSummary>());
                    return;
                }

                FetchQuizzesByIds(detail.PublishedQuizIds, onComplete);
            });
        }

        private void FetchQuizzesByIds(List<string> quizIds, Action<List<QuizSummary>> onComplete)
        {
            var chunks = new List<List<string>>();
            for (int i = 0; i < quizIds.Count; i += 10) // Firestore WhereIn caps at 10 values
            {
                chunks.Add(quizIds.GetRange(i, Mathf.Min(10, quizIds.Count - i)));
            }

            var results = new List<QuizSummary>();
            int remaining = chunks.Count;
            if (remaining == 0) { onComplete?.Invoke(results); return; }

            foreach (var chunk in chunks)
            {
                Db.Collection("quizzes")
                    .WhereIn(FieldPath.DocumentId, chunk.ConvertAll(id => (object)id))
                    .GetSnapshotAsync()
                    .ContinueWithOnMainThread(task =>
                    {
                        if (!task.IsCanceled && !task.IsFaulted)
                        {
                            foreach (var doc in task.Result.Documents) results.Add(ToQuizSummary(doc));
                        }

                        remaining--;
                        if (remaining == 0) onComplete?.Invoke(results);
                    });
            }
        }

        private static QuizSummary ToQuizSummary(DocumentSnapshot doc)
        {
            var questions = doc.ContainsField("questions") ? doc.GetValue<List<object>>("questions") : new List<object>();
            int totalPoints = 0;
            string difficulty = "easy";
            int hardestRank = 0;

            foreach (var raw in questions)
            {
                if (raw is Dictionary<string, object> q)
                {
                    if (q.TryGetValue("points", out var p)) totalPoints += Convert.ToInt32(p);
                    if (q.TryGetValue("difficulty", out var d))
                    {
                        string diff = d?.ToString() ?? "easy";
                        int rank = diff == "hard" ? 3 : diff == "medium" ? 2 : 1;
                        if (rank > hardestRank) { hardestRank = rank; difficulty = diff; }
                    }
                }
            }

            return new QuizSummary
            {
                QuizId = doc.Id,
                Title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "Untitled Quiz",
                Category = doc.ContainsField("category") ? doc.GetValue<string>("category") : "",
                QuestionCount = questions.Count,
                TimeLimitSeconds = doc.ContainsField("timeLimitSeconds") ? doc.GetValue<int>("timeLimitSeconds") : 0,
                TotalPoints = totalPoints,
                Difficulty = difficulty
            };
        }

        // ---------------- My scores (quiz attempt history) ----------------

        [Serializable]
        public class ScoreRecord
        {
            public string QuizTitle;
            public Timestamp CompletedAt;
            public bool Passed;
            public int ScoreCorrect;
            public int ScoreTotal;
            public int TimeSpentSeconds;
            public int Attempt;
        }

        /// <summary>Call when showing the Scores tab. Only reads this student's own
        /// quizAttempts docs for this classroom - matches the Firestore rule
        /// (`resource.data.studentId == request.auth.uid`), which is also why this
        /// can't be used to build the Leaderboard (a student can't read classmates'
        /// attempts - that's what the `members` roster/FetchClassroomRoster is for).</summary>
        public void FetchMyScores(string classroomId, Action<List<ScoreRecord>> onComplete)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null || string.IsNullOrEmpty(classroomId))
            {
                onComplete?.Invoke(new List<ScoreRecord>());
                return;
            }

            Db.Collection("quizAttempts")
                .WhereEqualTo("classroomId", classroomId)
                .WhereEqualTo("studentId", student.Uid)
                .OrderByDescending("completedAt")
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<ScoreRecord>();
                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        int attemptNumber = task.Result.Documents.Count();
                        foreach (var doc in task.Result.Documents)
                        {
                            int scoreCorrect = doc.ContainsField("scoreCorrect") ? doc.GetValue<int>("scoreCorrect") : 0;
                            int scoreTotal = doc.ContainsField("scoreTotal") ? doc.GetValue<int>("scoreTotal") : 0;

                            results.Add(new ScoreRecord
                            {
                                QuizTitle = doc.ContainsField("quizTitle") ? doc.GetValue<string>("quizTitle") : "Quiz",
                                CompletedAt = doc.ContainsField("completedAt") ? doc.GetValue<Timestamp>("completedAt") : Timestamp.GetCurrentTimestamp(),
                                Passed = doc.ContainsField("passed") && doc.GetValue<bool>("passed"),
                                ScoreCorrect = scoreCorrect,
                                ScoreTotal = scoreTotal,
                                TimeSpentSeconds = doc.ContainsField("timeSpentSeconds") ? doc.GetValue<int>("timeSpentSeconds") : 0,
                                Attempt = attemptNumber--
                            });
                        }
                    }
                    onComplete?.Invoke(results);
                });
        }

        // ---------------- Recording a completed quiz (call from QuizService) ----------------

        /// <summary>
        /// Call this from QuizService right after it writes a `quizAttempts` doc, so the
        /// classroom's `members/{studentId}` roster stays in sync. This is what
        /// AdminClassroomService.FetchClassroomAnalytics() and this class's
        /// FetchClassroomRoster() read for the Students tab, Analytics tab and
        /// Leaderboard - not the quizAttempts collection itself, since a student can
        /// only read their own attempts there.
        /// </summary>
        public void RecordQuizCompletion(string classroomId, int pointsEarned, float scorePercent, Action<bool> onComplete = null)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null || string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(false); return; }

            var memberRef = Db.Collection("classrooms").Document(classroomId).Collection("members").Document(student.Uid);

            Db.RunTransactionAsync(async transaction =>
            {
                var snap = await transaction.GetSnapshotAsync(memberRef);

                int priorQuizzes = snap.ContainsField("quizzesCompleted") ? snap.GetValue<int>("quizzesCompleted") : 0;
                int priorPoints = snap.ContainsField("points") ? snap.GetValue<int>("points") : 0;
                float priorAvg = snap.ContainsField("avgScorePercent") ? (float)snap.GetValue<double>("avgScorePercent") : 0f;

                int newQuizzes = priorQuizzes + 1;
                int newPoints = priorPoints + pointsEarned;
                float newAvg = ((priorAvg * priorQuizzes) + scorePercent) / newQuizzes;

                var update = new Dictionary<string, object>
                {
                    { "quizzesCompleted", newQuizzes },
                    { "points", newPoints },
                    { "avgScorePercent", newAvg }
                };

                // Keep the roster's displayed level current too, if gamification settings
                // are already loaded (avoids an extra async fetch inside the transaction).
                if (AdminGamificationService.Instance?.CurrentSettings != null)
                {
                    var (level, _, _, _, _, _) = AdminGamificationService.ComputeLevelProgress(
                        AdminGamificationService.Instance.CurrentSettings, newPoints);
                    update["level"] = level;
                }

                transaction.Update(memberRef, update);
            }).ContinueWithOnMainThread(task =>
            {
                onComplete?.Invoke(!task.IsCanceled && !task.IsFaulted);
            });
        }
    }
}