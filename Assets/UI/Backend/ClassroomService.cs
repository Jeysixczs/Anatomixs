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

            /// <summary>Owning teacher's uid, e.g. to look up that teacher's
            /// AdminGamificationService badge/points config (badges are configured
            /// per-teacher and apply across all of that teacher's classrooms) - see
            /// StudentAchievementsController.LoadData().</summary>
            public string TeacherId;

            public int StudentCount;

            /// <summary>When true, StudentClassroomHubController should render this classroom
            /// with an "Archived" badge and treat its card as non-interactive (locked) rather
            /// than routing into StudentClassroomDetailController - entry is 
            /// there too
            /// (see ClassroomDetailRecord.IsArchived below) but the hub should avoid the
            /// navigation entirely so the student never sees an empty flash before the block.</summary>
            public bool IsArchived;
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
                                TeacherId = doc.ContainsField("teacherId") ? doc.GetValue<string>("teacherId") : null,
                                StudentCount = doc.ContainsField("studentCount") ? doc.GetValue<int>("studentCount") : 0,
                                IsArchived = doc.ContainsField("isArchived") && doc.GetValue<bool>("isArchived")
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

            /// <summary>StudentClassroomDetailController checks this first, before loading any
            /// tab content - if true it shows the archived-blocked state instead (see
            /// LoadClassroomContent()) rather than letting the student view/interact with the
            /// classroom.</summary>
            public bool IsArchived;
        }

        /// <summary>Call when showing StudentClassroomDetailController - feeds the header
        /// stats and Overview tab's Classroom Info card. Callers must check
        /// IsArchived before rendering any classroom content - see
        /// StudentClassroomDetailController.LoadClassroomContent().</summary>
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
                    LeaderboardVisible = doc.ContainsField("leaderboardVisible") && doc.GetValue<bool>("leaderboardVisible"),
                    IsArchived = doc.ContainsField("isArchived") && doc.GetValue<bool>("isArchived")
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

        // ---------------- Notifications (derived live from per-classroom announcements) ----------------

        /// <summary>
        /// One row for StudentNotificationsController. There's no separate
        /// `notifications` collection/fan-out write - this is just each enrolled
        /// classroom's `announcements` subcollection, merged and re-sorted, with
        /// `IsRead` computed client-side against the student's
        /// `students/{uid}.notificationsLastReadAt` cursor. Call
        /// MarkAllNotificationsRead() to advance that cursor (e.g. from
        /// StudentNotificationsController.OnMarkAllReadClicked()).
        /// </summary>
        [Serializable]
        public class NotificationRecord
        {
            public string ClassroomId;
            public string ClassroomName;
            public string AnnouncementId;
            public string Title;
            public string Body;
            public Timestamp CreatedAt;
            public bool IsRead;
        }

        /// <summary>Call when showing StudentNotificationsController. Reads every
        /// classroom the student is a member of, pulls each one's most recent
        /// announcements (newest `maxPerClassroom` each), and merges them into one
        /// list sorted newest-first. Each entry's classroom name is included since
        /// notifications span multiple classrooms.</summary>
        public void FetchNotifications(Action<List<NotificationRecord>> onComplete, int maxPerClassroom = 20)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(new List<NotificationRecord>()); return; }

            var studentRef = Db.Collection("students").Document(student.Uid);
            studentRef.GetSnapshotAsync().ContinueWithOnMainThread(studentTask =>
            {
                bool hasLastRead = !studentTask.IsCanceled && !studentTask.IsFaulted
                    && studentTask.Result.Exists && studentTask.Result.ContainsField("notificationsLastReadAt");
                Timestamp lastReadAt = hasLastRead
                    ? studentTask.Result.GetValue<Timestamp>("notificationsLastReadAt")
                    : default;

                Db.Collection("classrooms")
                    .WhereArrayContains("memberIds", student.Uid)
                    .GetSnapshotAsync()
                    .ContinueWithOnMainThread(classroomsTask =>
                    {
                        if (classroomsTask.IsCanceled || classroomsTask.IsFaulted || classroomsTask.Result.Count == 0)
                        {
                            onComplete?.Invoke(new List<NotificationRecord>());
                            return;
                        }

                        var classroomDocs = classroomsTask.Result.Documents.ToList();
                        var results = new List<NotificationRecord>();
                        int remaining = classroomDocs.Count;

                        foreach (var classroomDoc in classroomDocs)
                        {
                            string classroomId = classroomDoc.Id;
                            string classroomName = classroomDoc.ContainsField("name") ? classroomDoc.GetValue<string>("name") : "Classroom";

                            Db.Collection("classrooms").Document(classroomId).Collection("announcements")
                                .OrderByDescending("createdAt")
                                .Limit(maxPerClassroom)
                                .GetSnapshotAsync()
                                .ContinueWithOnMainThread(annTask =>
                                {
                                    if (!annTask.IsCanceled && !annTask.IsFaulted)
                                    {
                                        foreach (var doc in annTask.Result.Documents)
                                        {
                                            var createdAt = doc.ContainsField("createdAt") ? doc.GetValue<Timestamp>("createdAt") : Timestamp.GetCurrentTimestamp();

                                            results.Add(new NotificationRecord
                                            {
                                                ClassroomId = classroomId,
                                                ClassroomName = classroomName,
                                                AnnouncementId = doc.Id,
                                                Title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "",
                                                Body = doc.ContainsField("body") ? doc.GetValue<string>("body") : "",
                                                CreatedAt = createdAt,
                                                IsRead = hasLastRead && createdAt.ToDateTime() <= lastReadAt.ToDateTime()
                                            });
                                        }
                                    }

                                    remaining--;
                                    if (remaining == 0)
                                    {
                                        results.Sort((a, b) => b.CreatedAt.ToDateTime().CompareTo(a.CreatedAt.ToDateTime()));
                                        onComplete?.Invoke(results);
                                    }
                                });
                        }
                    });
            });
        }

        /// <summary>Call from StudentNotificationsController.OnMarkAllReadClicked().
        /// Advances the student's read cursor to now, so every announcement posted
        /// up to this point reads as read next time FetchNotifications() runs (a
        /// fresh sign-in / re-open will re-derive IsRead from this cursor, since it
        /// isn't tracked per-notification).</summary>
        public void MarkAllNotificationsRead(Action<bool> onComplete = null)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(false); return; }

            Db.Collection("students").Document(student.Uid)
                .UpdateAsync("notificationsLastReadAt", Timestamp.GetCurrentTimestamp())
                .ContinueWithOnMainThread(task => onComplete?.Invoke(!task.IsCanceled && !task.IsFaulted));
        }

        // ---------------- Students / roster / leaderboard ----------------

        /// <summary>One row from the classroom's `members` subcollection. `Points` /
        /// `QuizzesCompleted` / `AvgScorePercent` / `Level` are denormalized there by
        /// RecordQuizCompletion() below - see its doc comment. `Points` is this
        /// classroom's own running total; `Level` is the student's true global level
        /// (from their total points across every classroom), so it matches what they
        /// see on their dashboard/progress screens.</summary>
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
            public int TimeLimitMinutes;
            public bool HasTimeLimit;
            /// <summary>0 = unlimited.</summary>
            public int MaxAttempts;
            public bool IsDeadlineEnabled;
            public DateTime? DeadlineUtc;
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
                TimeLimitMinutes = doc.ContainsField("timeLimitMinutes")
                    ? doc.GetValue<int>("timeLimitMinutes")
                    : (doc.ContainsField("timeLimitSeconds") ? Mathf.Max(1, doc.GetValue<int>("timeLimitSeconds") / 60) : 10),
                HasTimeLimit = doc.ContainsField("hasTimeLimit") ? doc.GetValue<bool>("hasTimeLimit") : true,
                MaxAttempts = doc.ContainsField("maxAttempts") ? doc.GetValue<int>("maxAttempts") : 0,
                IsDeadlineEnabled = doc.ContainsField("isDeadlineEnabled") ? doc.GetValue<bool>("isDeadlineEnabled") : false,
                DeadlineUtc = doc.ContainsField("deadline") && doc.GetValue<object>("deadline") != null
                    ? doc.GetValue<Timestamp>("deadline").ToDateTime()
                    : (DateTime?)null,
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
                    if (task.IsFaulted)
                    {
                        // Most likely cause: Firestore needs a composite index for this
                        // query (classroomId == , studentId == , orderBy completedAt desc).
                        // Without one this call fails silently from the UI's point of view -
                        // the Scores tab just shows "No quiz attempts yet" forever. Check the
                        // Firebase console (Firestore -> Indexes) or the exception logged
                        // below for a direct "create index" link.
                        Debug.LogError($"[ClassroomService] FetchMyScores failed - likely a missing " +
                            $"Firestore composite index (classroomId + studentId + completedAt). " +
                            $"Exception: {task.Exception}");
                    }
                    else if (!task.IsCanceled)
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
        ///
        /// `pointsEarned` here is just this classroom's own running total (useful for
        /// "points earned in this class" style stats) - it does NOT drive the
        /// student's level. Levels are global: the student's actual level is computed
        /// once in QuizService.SubmitQuizAttempt from their `students/{uid}.totalPoints`
        /// (summed across every classroom they're in) against the fixed global
        /// `gamificationSettings/config` levels, and passed in here as `globalLevel` so
        /// this roster doc always shows the same level a student sees everywhere else.
        /// </summary>
        public void RecordQuizCompletion(string classroomId, int pointsEarned, float scorePercent, int globalLevel, Action<bool> onComplete = null)
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
                    { "avgScorePercent", newAvg },
                    { "level", globalLevel }
                };

                transaction.Update(memberRef, update);
            }).ContinueWithOnMainThread(task =>
            {
                onComplete?.Invoke(!task.IsCanceled && !task.IsFaulted);
            });
        }

        // ---------------- Recent classroom joins (StudentDashboardController) ----------------

        [Serializable]
        public class ClassroomJoinRecord
        {
            public string ClassroomId;
            public string ClassroomName;
            public Timestamp JoinedAt;
        }

        /// <summary>Call when showing StudentDashboardController's Recent Activity card.
        /// `joinedAt` is only ever written on the classroom's own `members/{uid}` doc
        /// (see JoinClassroom() above) - there's no top-level collection to query it
        /// from directly - so this reads every classroom the student belongs to (same
        /// query FetchMyClassrooms/FetchNotifications already use) and then reads each
        /// one's `members/{uid}` doc for the timestamp. Same fan-out shape as
        /// FetchNotifications' per-classroom announcements read; cost is bounded by
        /// how many classrooms the student is in, not by how much history exists.</summary>
        public void FetchRecentJoins(Action<List<ClassroomJoinRecord>> onComplete, int maxItems = 8)
        {
            var student = PlayerSessionManager.Instance.CurrentStudent;
            if (student == null) { onComplete?.Invoke(new List<ClassroomJoinRecord>()); return; }

            Db.Collection("classrooms")
                .WhereArrayContains("memberIds", student.Uid)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(classroomsTask =>
                {
                    if (classroomsTask.IsCanceled || classroomsTask.IsFaulted || classroomsTask.Result.Count == 0)
                    {
                        onComplete?.Invoke(new List<ClassroomJoinRecord>());
                        return;
                    }

                    var classroomDocs = classroomsTask.Result.Documents.ToList();
                    var results = new List<ClassroomJoinRecord>();
                    int remaining = classroomDocs.Count;

                    foreach (var classroomDoc in classroomDocs)
                    {
                        string classroomId = classroomDoc.Id;
                        string classroomName = classroomDoc.ContainsField("name") ? classroomDoc.GetValue<string>("name") : "Classroom";

                        Db.Collection("classrooms").Document(classroomId).Collection("members").Document(student.Uid)
                            .GetSnapshotAsync()
                            .ContinueWithOnMainThread(memberTask =>
                            {
                                if (!memberTask.IsCanceled && !memberTask.IsFaulted
                                    && memberTask.Result.Exists && memberTask.Result.ContainsField("joinedAt"))
                                {
                                    results.Add(new ClassroomJoinRecord
                                    {
                                        ClassroomId = classroomId,
                                        ClassroomName = classroomName,
                                        JoinedAt = memberTask.Result.GetValue<Timestamp>("joinedAt")
                                    });
                                }

                                remaining--;
                                if (remaining == 0)
                                {
                                    results.Sort((a, b) => b.JoinedAt.ToDateTime().CompareTo(a.JoinedAt.ToDateTime()));
                                    if (results.Count > maxItems)
                                    {
                                        results.RemoveRange(maxItems, results.Count - maxItems);
                                    }
                                    onComplete?.Invoke(results);
                                }
                            });
                    }
                });
        }
    }
}