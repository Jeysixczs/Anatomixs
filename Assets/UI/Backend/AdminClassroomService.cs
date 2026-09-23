using System;
using System.Collections.Generic;
using System.Linq;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Admin-side classroom operations, backed by Firestore. This is the
    /// "AdminClassroomService" referenced in AdminCreateClassroomController,
    /// and it also feeds AdminDashboardController / AdminClassroomDetailController.
    ///
    /// Attach to the same persistent GameObject as FirebaseBootstrap/UIManager.
    /// Requires AdminAuthService.CurrentAdmin to be set before calling any
    /// method here.
    /// </summary>
    public class AdminClassroomService : MonoBehaviour
    {
        public static AdminClassroomService Instance { get; private set; }

        private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789"; // no I/O, matches the stub generator
        private const int CodeLength = 8;
        private const int MaxCodeGenerationAttempts = 5;

        [Serializable]
        public class ClassroomRecord
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

            /// <summary>Archived classrooms stay visible (read-only) in the teacher's "My
            /// Classrooms" list but are blocked from student access - see
            /// AdminClassroomDetailController's Archive button / SetArchived(), and
            /// ClassroomService.FetchClassroomDetail on the student side.</summary>
            public bool IsArchived;
        }

        /// <summary>One row in the Students tab / Leaderboard - reads straight off the
        /// `classrooms/{id}/members/{studentId}` doc's denormalized `points` /
        /// `quizzesCompleted` / `avgScorePercent` / `level` fields (see ClassroomService.
        /// RecordQuizCompletion, called by QuizService.SubmitQuizAttempt right after it
        /// writes each quizAttempts doc). Rolling these up here - rather than querying
        /// quizAttempts directly - matters because students can only read their *own*
        /// quizAttempts docs (see the Firestore rules), so a classroom-wide read has to
        /// come from something everyone in the classroom is allowed to read: the members
        /// subcollection.</summary>
        [Serializable]
        public class StudentStat
        {
            public string StudentId;
            public string Name;
            public int Level;
            public int Points;
            public int QuizzesCompleted;
            public float AvgScorePercent;
        }

        /// <summary>Bundle returned by FetchClassroomAnalytics() - feeds
        /// AdminClassroomDetailController.SetStudents() / SetLeaderboard() /
        /// SetAnalyticsOverview() in one call.</summary>
        [Serializable]
        public class ClassroomAnalytics
        {
            public List<StudentStat> Students = new List<StudentStat>();     // roster order
            public List<StudentStat> Leaderboard = new List<StudentStat>();  // sorted by points desc
            public int TotalPointsEarned;
            public int TotalQuizzesCompleted;
            public float AvgScorePercent;
            public int ActiveStudents;
        }

        /// <summary>One row in the Announcements tab. Shares the same
        /// `classrooms/{id}/announcements` subcollection as
        /// ClassroomService.AnnouncementRecord on the student side.</summary>
        [Serializable]
        public class AnnouncementRecord
        {
            public string AnnouncementId;
            public string Title;
            public string Body;
            public string AuthorId;
            public Timestamp CreatedAt;
        }

        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Call from AdminCreateClassroomController.OnCreateClassroomClicked()
        /// in place of the fake stub. Retries a few times on the (very rare)
        /// chance of a code collision.
        /// </summary>
        public void CreateClassroom(string name, string description, Action<bool, string, ClassroomRecord> onComplete)
        {
            var admin = AdminAuthService.Instance.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }

            TryCreateWithFreshCode(admin, name, description, 0, onComplete);
        }

        private void TryCreateWithFreshCode(AdminAuthService.AdminProfile admin, string name, string description, int attempt, Action<bool, string, ClassroomRecord> onComplete)
        {
            string code = GenerateClassroomCode();
            var codeRef = Db.Collection("classroomCodes").Document(code);
            var classroomRef = Db.Collection("classrooms").Document(); // auto id

            Db.RunTransactionAsync(async transaction =>
            {
                var codeSnap = await transaction.GetSnapshotAsync(codeRef);
                if (codeSnap.Exists)
                {
                    throw new InvalidOperationException("__CODE_COLLISION__");
                }

                transaction.Set(codeRef, new Dictionary<string, object> { { "classroomId", classroomRef.Id } });
                transaction.Set(classroomRef, new Dictionary<string, object>
                {
                    { "name", name },
                    { "description", description },
                    { "code", code },
                    { "teacherId", admin.Uid },
                    { "teacherName", admin.FullName },
                    { "studentCount", 0 },
                    { "memberIds", new List<string>() },
                    { "createdAt", Timestamp.GetCurrentTimestamp() }
                });

                var adminRef = Db.Collection("admins").Document(admin.Uid);
                transaction.Update(adminRef, "classroomCount", FieldValue.Increment(1));
            }).ContinueWithOnMainThread(txTask =>
            {
                if (txTask.IsCanceled || txTask.IsFaulted)
                {
                    bool wasCollision = txTask.Exception?.InnerException?.Message == "__CODE_COLLISION__";
                    if (wasCollision && attempt < MaxCodeGenerationAttempts)
                    {
                        TryCreateWithFreshCode(admin, name, description, attempt + 1, onComplete);
                        return;
                    }

                    onComplete?.Invoke(false, "Could not create classroom. Please try again.", null);
                    return;
                }

                var record = new ClassroomRecord
                {
                    ClassroomId = classroomRef.Id,
                    Name = name,
                    Description = description,
                    Code = code,
                    TeacherId = admin.Uid,
                    TeacherName = admin.FullName,
                    StudentCount = 0
                };

                // Patch AdminAuthService.CurrentAdmin locally with what this
                // transaction just wrote (classroomCount +1), so AdminProfile reads
                // up-to-date stats without an extra admins/{uid} read. See
                // AdminAuthService.ApplyClassroomCreated.
                AdminAuthService.Instance?.ApplyClassroomCreated();

                onComplete?.Invoke(true, null, record);
            });
        }

        /// <summary>Call when showing AdminDashboardController's "My Classrooms" list.</summary>
        public void FetchMyClassrooms(Action<List<ClassroomRecord>> onComplete)
        {
            var admin = AdminAuthService.Instance.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(new List<ClassroomRecord>()); return; }

            Db.Collection("classrooms")
                .WhereEqualTo("teacherId", admin.Uid)
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    var results = new List<ClassroomRecord>();

                    if (!task.IsCanceled && !task.IsFaulted)
                    {
                        foreach (var doc in task.Result.Documents)
                        {
                            results.Add(ToRecord(doc));
                        }
                    }

                    onComplete?.Invoke(results);
                });
        }

        /// <summary>Live version of FetchMyClassrooms() - call when showing
        /// AdminDashboardController, keep the returned ListenerRegistration and
        /// Stop() it in OnDisable. Unlike a one-shot fetch, onUpdate fires once
        /// immediately with the current data (so the caller doesn't need both a
        /// fetch AND a listener), then again every time this admin's classroom
        /// docs change for ANY reason - including changes made from elsewhere,
        /// like a student joining from their own device and incrementing
        /// studentCount. That cross-device case is exactly what a one-shot fetch
        /// can never catch, no matter when it's called - the classroom's own
        /// document didn't change on this device, so there was nothing local to
        /// invalidate. This is a live server-pushed subscription instead: stop it
        /// when the screen isn't visible to avoid paying for updates nobody's
        /// looking at.</summary>
        public ListenerRegistration ListenToMyClassrooms(Action<List<ClassroomRecord>> onUpdate)
        {
            var admin = AdminAuthService.Instance?.CurrentAdmin;
            if (admin == null) { onUpdate?.Invoke(new List<ClassroomRecord>()); return null; }

            return Db.Collection("classrooms")
                .WhereEqualTo("teacherId", admin.Uid)
                .Listen(snapshot =>
                {
                    var results = new List<ClassroomRecord>();
                    foreach (var doc in snapshot.Documents)
                    {
                        results.Add(ToRecord(doc));
                    }
                    onUpdate?.Invoke(results);
                });
        }

        /// <summary>Call when showing AdminClassroomDetailController for a specific classroom.</summary>
        public void FetchClassroomDetail(string classroomId, Action<ClassroomRecord> onComplete)
        {
            Db.Collection("classrooms").Document(classroomId).GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                {
                    onComplete?.Invoke(null);
                    return;
                }

                onComplete?.Invoke(ToRecord(task.Result));
            });
        }

        private static ClassroomRecord ToRecord(DocumentSnapshot doc)
        {
            return new ClassroomRecord
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
            };
        }

        private static string GenerateClassroomCode()
        {
            var buffer = new char[CodeLength];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = CodeChars[UnityEngine.Random.Range(0, CodeChars.Length)];
            }
            return new string(buffer);
        }

        // ---------------- Quiz publishing ----------------

        /// <summary>Call from AdminClassroomDetailController.OnQuizToggle1/2Clicked() in
        /// place of the TODO. Adds/removes quizId from the classroom's publishedQuizIds
        /// array - this is what StudentClassroomDetailController's Available Quizzes tab
        /// reads (via ClassroomService.FetchAvailableQuizzes).</summary>
        public void SetQuizPublished(string classroomId, string quizId, bool published, Action<bool, string> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId) || string.IsNullOrEmpty(quizId))
            {
                onComplete?.Invoke(false, "Missing classroom or quiz id.");
                return;
            }

            var update = published ? FieldValue.ArrayUnion(quizId) : FieldValue.ArrayRemove(quizId);

            Db.Collection("classrooms").Document(classroomId)
                .UpdateAsync("publishedQuizIds", update)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        onComplete?.Invoke(false, "Could not update quiz visibility.");
                        return;
                    }
                    onComplete?.Invoke(true, null);
                });
        }

        /// <summary>Call from AdminClassroomDetailController.OnShowToStudentsToggleClicked().</summary>
        public void SetLeaderboardVisibility(string classroomId, bool visible, Action<bool, string> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(false, "Missing classroom id."); return; }

            Db.Collection("classrooms").Document(classroomId)
                .UpdateAsync("leaderboardVisible", visible)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        onComplete?.Invoke(false, "Could not update leaderboard visibility.");
                        return;
                    }
                    onComplete?.Invoke(true, null);
                });
        }

        // ---------------- Archiving ----------------

        /// <summary>Call from AdminClassroomDetailController's Archive-confirmation dialog
        /// (archived: true) and its Unarchive/Restore action (archived: false). Setting
        /// isArchived does not delete or otherwise touch any classroom data - it's purely
        /// a flag that ClassroomService.FetchClassroomDetail / FetchMyClassrooms on the
        /// student side check to block access, and that AdminDashboardController's "My
        /// Classrooms" list can check to show an "Archived" badge.</summary>
        public void SetArchived(string classroomId, bool archived, Action<bool, string> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(false, "Missing classroom id."); return; }

            Db.Collection("classrooms").Document(classroomId)
                .UpdateAsync("isArchived", archived)
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        onComplete?.Invoke(false, archived ? "Could not archive classroom." : "Could not restore classroom.");
                        return;
                    }
                    onComplete?.Invoke(true, null);
                });
        }

        // ---------------- Announcements ----------------

        /// <summary>Call from AdminClassroomDetailController.OnPostAnnouncementClicked().</summary>
        public void PostAnnouncement(string classroomId, string title, string body, Action<bool, string, AnnouncementRecord> onComplete)
        {
            var admin = AdminAuthService.Instance.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(false, "Not signed in.", null); return; }
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(false, "Missing classroom id.", null); return; }

            var docRef = Db.Collection("classrooms").Document(classroomId).Collection("announcements").Document();
            var createdAt = Timestamp.GetCurrentTimestamp();

            var data = new Dictionary<string, object>
            {
                { "title", title },
                { "body", body },
                { "authorId", admin.Uid },
                { "createdAt", createdAt }
            };

            docRef.SetAsync(data).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not post announcement.", null);
                    return;
                }

                onComplete?.Invoke(true, null, new AnnouncementRecord
                {
                    AnnouncementId = docRef.Id,
                    Title = title,
                    Body = body,
                    AuthorId = admin.Uid,
                    CreatedAt = createdAt
                });
            });
        }

        /// <summary>Call from AdminClassroomDetailController.OnDeleteAnnouncementClicked().</summary>
        public void DeleteAnnouncement(string classroomId, string announcementId, Action<bool, string> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId) || string.IsNullOrEmpty(announcementId))
            {
                onComplete?.Invoke(false, "Missing classroom or announcement id.");
                return;
            }

            Db.Collection("classrooms").Document(classroomId).Collection("announcements").Document(announcementId)
                .DeleteAsync().ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        onComplete?.Invoke(false, "Could not delete announcement.");
                        return;
                    }
                    onComplete?.Invoke(true, null);
                });
        }

        /// <summary>Call when opening AdminClassroomDetailController's Announcements tab
        /// (or right after SetClassroomData(), to preload before the tab is opened).
        /// Most recent first.</summary>
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
                        foreach (var doc in task.Result.Documents) results.Add(ToAnnouncementRecord(doc));
                    }
                    onComplete?.Invoke(results);
                });
        }

        private static AnnouncementRecord ToAnnouncementRecord(DocumentSnapshot doc)
        {
            return new AnnouncementRecord
            {
                AnnouncementId = doc.Id,
                Title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "",
                Body = doc.ContainsField("body") ? doc.GetValue<string>("body") : "",
                AuthorId = doc.ContainsField("authorId") ? doc.GetValue<string>("authorId") : "",
                CreatedAt = doc.ContainsField("createdAt") ? doc.GetValue<Timestamp>("createdAt") : Timestamp.GetCurrentTimestamp()
            };
        }

        // ---------------- Students / Analytics / Leaderboard ----------------

        /// <summary>Call from AdminClassroomDetailController's Students tab remove-student
        /// action, after the teacher confirms the "Remove Student" dialog. Removes the
        /// student from THIS classroom only: deletes their `members/{studentId}` doc,
        /// pulls their id out of the classroom's `memberIds` array, and decrements
        /// `studentCount` - all in one transaction so the count can't drift out of sync
        /// with the member doc actually being gone.
        ///
        /// This does not touch the student's account, their other classrooms, or their
        /// past quizAttempts docs (those stay as history) - it only removes their
        /// membership/points/progress *for this classroom*. If the student re-joins later
        /// with the same classroom code, they'll start over in this classroom at 0
        /// points/quizzes, same as any brand-new member.</summary>
        public void UnenrollStudent(string classroomId, string studentId, Action<bool, string> onComplete)
        {
            if (string.IsNullOrEmpty(classroomId) || string.IsNullOrEmpty(studentId))
            {
                onComplete?.Invoke(false, "Missing classroom or student id.");
                return;
            }

            var classroomRef = Db.Collection("classrooms").Document(classroomId);
            var memberRef = classroomRef.Collection("members").Document(studentId);

            Db.RunTransactionAsync(async transaction =>
            {
                var memberSnap = await transaction.GetSnapshotAsync(memberRef);
                if (!memberSnap.Exists)
                {
                    // Already removed (e.g. a double-tap, or removed from another device) -
                    // nothing left to do here, but don't touch memberIds/studentCount again
                    // since that would double-decrement.
                    return;
                }

                transaction.Delete(memberRef);
                transaction.Update(classroomRef, new Dictionary<string, object>
                {
                    { "memberIds", FieldValue.ArrayRemove(studentId) },
                    { "studentCount", FieldValue.Increment(-1) }
                });
            }).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not remove student from classroom.");
                    return;
                }
                onComplete?.Invoke(true, null);
            });
        }

        /// <summary>
        /// Roster for one classroom (every doc in `classrooms/{id}/members`), for
        /// screens that need to know who is in the class - e.g. AdminSubmissionReview
        /// uses it to show which students haven't uploaded yet. Unlike
        /// FetchClassroomAnalytics, which folds a failed read into an empty result,
        /// this reports success so the caller can tell "no students" apart from
        /// "couldn't load the list".
        /// </summary>
        public void FetchClassroomMembers(string classroomId, Action<bool, string, List<StudentStat>> onComplete)
        {
            var results = new List<StudentStat>();
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(false, "Missing classroom id.", results); return; }

            Db.Collection("classrooms").Document(classroomId).Collection("members")
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        Debug.LogError($"[AdminClassroomService] FetchClassroomMembers failed for classroom {classroomId}: {task.Exception?.Flatten().InnerException}");
                        onComplete?.Invoke(false, "Could not load this classroom's student list.", results);
                        return;
                    }

                    foreach (var doc in task.Result.Documents)
                    {
                        results.Add(new StudentStat
                        {
                            StudentId = doc.Id,
                            Name = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : "Student",
                            Level = doc.ContainsField("level") ? doc.GetValue<int>("level") : 1,
                            Points = doc.ContainsField("points") ? doc.GetValue<int>("points") : 0,
                            QuizzesCompleted = doc.ContainsField("quizzesCompleted") ? doc.GetValue<int>("quizzesCompleted") : 0,
                            AvgScorePercent = doc.ContainsField("avgScorePercent") ? (float)doc.GetValue<double>("avgScorePercent") : 0f
                        });
                    }

                    onComplete?.Invoke(true, null, results);
                });
        }

        /// <summary>
        /// Call when opening AdminClassroomDetailController's Students or Analytics tab
        /// (or right after SetClassroomData(), to have both ready before either tab is
        /// opened). Feeds SetStudents() (analytics.Students), SetLeaderboard()
        /// (analytics.Leaderboard) and SetAnalyticsOverview() (the four rollup fields)
        /// all from one read of the classroom's `members` subcollection.
        /// </summary>
        public void FetchClassroomAnalytics(string classroomId, Action<ClassroomAnalytics> onComplete)
        {
            var result = new ClassroomAnalytics();
            if (string.IsNullOrEmpty(classroomId)) { onComplete?.Invoke(result); return; }

            Db.Collection("classrooms").Document(classroomId).Collection("members")
                .GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted) { onComplete?.Invoke(result); return; }

                    int totalPoints = 0;
                    int totalQuizzes = 0;
                    float scoreWeightedSum = 0f;

                    foreach (var doc in task.Result.Documents)
                    {
                        var stat = new StudentStat
                        {
                            StudentId = doc.Id,
                            Name = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : "Student",
                            Level = doc.ContainsField("level") ? doc.GetValue<int>("level") : 1,
                            Points = doc.ContainsField("points") ? doc.GetValue<int>("points") : 0,
                            QuizzesCompleted = doc.ContainsField("quizzesCompleted") ? doc.GetValue<int>("quizzesCompleted") : 0,
                            AvgScorePercent = doc.ContainsField("avgScorePercent") ? (float)doc.GetValue<double>("avgScorePercent") : 0f
                        };

                        result.Students.Add(stat);

                        totalPoints += stat.Points;
                        totalQuizzes += stat.QuizzesCompleted;
                        if (stat.QuizzesCompleted > 0) scoreWeightedSum += stat.AvgScorePercent * stat.QuizzesCompleted;
                    }

                    result.Leaderboard = new List<StudentStat>(result.Students);
                    result.Leaderboard.Sort((a, b) =>
                        b.Points != a.Points ? b.Points.CompareTo(a.Points) : b.QuizzesCompleted.CompareTo(a.QuizzesCompleted));

                    result.TotalPointsEarned = totalPoints;
                    result.TotalQuizzesCompleted = totalQuizzes;
                    result.AvgScorePercent = totalQuizzes > 0 ? scoreWeightedSum / totalQuizzes : 0f;
                    result.ActiveStudents = result.Students.Count(s => s.QuizzesCompleted > 0);

                    onComplete?.Invoke(result);
                });
        }
    }
}