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

                    // Announcement push notifications are topic-based (see FCMNotificationService) -
                    // subscribe as soon as membership is confirmed. Best-effort: a failure here
                    // (e.g. no network) must never block the join itself, since Firestore membership
                    // is already committed - SyncClassroomSubscriptions() below covers the student
                    // catching back up on next launch if this call is missed.
                    FCMNotificationService.Instance?.SubscribeToClassroom(classroomId);

                    onComplete?.Invoke(true, null);
                });
            });
        }

        /// <summary>Reconciles this student's FCM topic subscriptions against every classroom
        /// they're currently enrolled in. Call this once after sign-in (e.g. from wherever
        /// StudentClassroomHubController first calls FetchMyClassrooms()) so a student who
        /// joined classrooms on another device, or whose subscription silently failed, still
        /// ends up subscribed - FCMNotificationService.SyncSubscriptions() is idempotent, so
        /// calling this on every login is safe and cheap (no Firestore writes, just FCM topic
        /// calls skipped for classrooms already tracked locally).</summary>
        public void SyncClassroomSubscriptions()
        {
            if (FCMNotificationService.Instance == null) return;

            FetchMyClassrooms(classrooms =>
            {
                var ids = classrooms.ConvertAll(c => c.ClassroomId);
                FCMNotificationService.Instance.SyncSubscriptions(ids);
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

        /// <summary>Live version of FetchMyClassrooms() - call when showing
        /// StudentClassroomHubController, keep the returned ListenerRegistration and
        /// Stop() it in OnDisable (mirrors AdminClassroomService.ListenToMyClassrooms).
        /// onUpdate fires once immediately with the current data, then again any time
        /// a classroom this student belongs to changes for ANY reason - including a
        /// teacher archiving it or another student joining and bumping studentCount -
        /// which a one-shot fetch can never catch on its own since nothing changed on
        /// this device to trigger a re-fetch. Stop it when the screen isn't visible so
        /// you're not paying for updates nobody's looking at.</summary>
        public ListenerRegistration ListenToMyClassrooms(Action<List<ClassroomRecord>> onUpdate)
        {
            var student = PlayerSessionManager.Instance?.CurrentStudent;
            if (student == null) { onUpdate?.Invoke(new List<ClassroomRecord>()); return null; }

            return Db.Collection("classrooms")
                .WhereArrayContains("memberIds", student.Uid)
                .Listen(snapshot =>
                {
                    var results = new List<ClassroomRecord>();
                    foreach (var doc in snapshot.Documents)
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
                    onUpdate?.Invoke(results);
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
                onComplete?.Invoke(ToClassroomDetailRecord(task.Result));
            });
        }

        /// <summary>Live version of FetchClassroomDetail() - call when showing
        /// StudentClassroomDetailController's Overview tab / header stats. Keep the
        /// returned ListenerRegistration and Stop() it in OnDisable (or when the
        /// student navigates to a different classroom) - mirrors ListenToMyClassrooms.
        /// onUpdate fires once immediately with the current doc, then again any time
        /// the teacher edits the classroom (name/description/leaderboard visibility/
        /// published quizzes/archived flag) or studentCount changes - all without the
        /// student needing to leave and re-enter the screen.</summary>
        public ListenerRegistration ListenToClassroomDetail(string classroomId, Action<ClassroomDetailRecord> onUpdate)
        {
            if (string.IsNullOrEmpty(classroomId)) { onUpdate?.Invoke(null); return null; }

            return Db.Collection("classrooms").Document(classroomId).Listen(snapshot =>
            {
                onUpdate?.Invoke(snapshot.Exists ? ToClassroomDetailRecord(snapshot) : null);
            });
        }

        private static ClassroomDetailRecord ToClassroomDetailRecord(DocumentSnapshot doc)
        {
            return new ClassroomDetailRecord
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
                        foreach (var doc in task.Result.Documents) results.Add(ToAnnouncementRecord(doc));
                    }
                    onComplete?.Invoke(results);
                });
        }

        /// <summary>Live version of FetchAnnouncements() - call when showing the Overview
        /// tab. Keep the returned ListenerRegistration and Stop() it in OnDisable / on
        /// classroom change. onUpdate fires once immediately, then again the instant the
        /// teacher posts, edits, or deletes an announcement for this classroom (see
        /// AdminClassroomDetailController's Announcements tab) - no manual refresh needed.</summary>
        public ListenerRegistration ListenToAnnouncements(string classroomId, Action<List<AnnouncementRecord>> onUpdate)
        {
            if (string.IsNullOrEmpty(classroomId)) { onUpdate?.Invoke(new List<AnnouncementRecord>()); return null; }

            return Db.Collection("classrooms").Document(classroomId).Collection("announcements")
                .OrderByDescending("createdAt")
                .Listen(snapshot =>
                {
                    var results = new List<AnnouncementRecord>();
                    foreach (var doc in snapshot.Documents) results.Add(ToAnnouncementRecord(doc));
                    onUpdate?.Invoke(results);
                });
        }

        private static AnnouncementRecord ToAnnouncementRecord(DocumentSnapshot doc)
        {
            return new AnnouncementRecord
            {
                AnnouncementId = doc.Id,
                Title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "",
                Body = doc.ContainsField("body") ? doc.GetValue<string>("body") : "",
                CreatedAt = doc.ContainsField("createdAt") ? doc.GetValue<Timestamp>("createdAt") : Timestamp.GetCurrentTimestamp()
            };
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

        /// <summary>Wraps every listener behind one ListenToNotifications() subscription -
        /// the read-cursor doc, the classroom-membership query, and one listener per
        /// enrolled classroom's announcements subcollection (added/removed dynamically
        /// as classroom membership changes) - so the caller only needs to hold and
        /// Stop() a single object.</summary>
        public sealed class NotificationsSubscription
        {
            private ListenerRegistration _studentListener;
            private ListenerRegistration _classroomsListener;
            private readonly Dictionary<string, ListenerRegistration> _announcementListeners = new Dictionary<string, ListenerRegistration>();

            internal void SetStudentListener(ListenerRegistration listener) => _studentListener = listener;
            internal void SetClassroomsListener(ListenerRegistration listener) => _classroomsListener = listener;

            internal void SetAnnouncementListener(string classroomId, ListenerRegistration listener)
            {
                StopAnnouncementListener(classroomId);
                _announcementListeners[classroomId] = listener;
            }

            internal void StopAnnouncementListener(string classroomId)
            {
                if (_announcementListeners.TryGetValue(classroomId, out var listener))
                {
                    listener.Stop();
                    _announcementListeners.Remove(classroomId);
                }
            }

            public void Stop()
            {
                _studentListener?.Stop();
                _studentListener = null;
                _classroomsListener?.Stop();
                _classroomsListener = null;

                foreach (var listener in _announcementListeners.Values) listener.Stop();
                _announcementListeners.Clear();
            }
        }

        /// <summary>Live version of FetchNotifications() - call once when showing
        /// StudentNotificationsController, keep the returned NotificationsSubscription
        /// and Stop() it in OnDisable. onUpdate fires once immediately (per classroom,
        /// as each one's cached/first snapshot arrives) and again any time a teacher
        /// posts/edits/deletes an announcement in any enrolled classroom, the student
        /// joins/leaves a classroom, or MarkAllNotificationsRead() advances the read
        /// cursor - all without re-fetching anything this device already has. Each
        /// classroom's announcements are read once (bounded by maxPerClassroom) and
        /// then only pushed deltas afterward, so a busy classroom doesn't cost repeat
        /// full reads the way re-opening this screen with FetchNotifications() would.</summary>
        public NotificationsSubscription ListenToNotifications(Action<List<NotificationRecord>> onUpdate, int maxPerClassroom = 20)
        {
            var subscription = new NotificationsSubscription();

            var student = PlayerSessionManager.Instance?.CurrentStudent;
            if (student == null) { onUpdate?.Invoke(new List<NotificationRecord>()); return subscription; }

            string studentUid = student.Uid;

            bool hasLastRead = false;
            Timestamp lastReadAt = default;

            var classroomNames = new Dictionary<string, string>();
            var announcementsByClassroom = new Dictionary<string, List<NotificationRecord>>();

            void Recompute()
            {
                var merged = new List<NotificationRecord>();
                foreach (var list in announcementsByClassroom.Values)
                {
                    merged.AddRange(list);
                }

                // Re-derive IsRead against whatever the cursor currently is - this is
                // what makes MarkAllNotificationsRead() reflect instantly here without
                // re-reading any announcement.
                for (int i = 0; i < merged.Count; i++)
                {
                    merged[i].IsRead = hasLastRead && merged[i].CreatedAt.ToDateTime() <= lastReadAt.ToDateTime();
                }

                merged.Sort((a, b) => b.CreatedAt.ToDateTime().CompareTo(a.CreatedAt.ToDateTime()));
                onUpdate?.Invoke(merged);
            }

            var studentListener = Db.Collection("students").Document(studentUid).Listen(snap =>
            {
                hasLastRead = snap.Exists && snap.ContainsField("notificationsLastReadAt");
                lastReadAt = hasLastRead ? snap.GetValue<Timestamp>("notificationsLastReadAt") : default;
                Recompute();
            });
            subscription.SetStudentListener(studentListener);

            var classroomsListener = Db.Collection("classrooms")
                .WhereArrayContains("memberIds", studentUid)
                .Listen(snapshot =>
                {
                    var incomingIds = new HashSet<string>();

                    foreach (var doc in snapshot.Documents)
                    {
                        string classroomId = doc.Id;
                        incomingIds.Add(classroomId);
                        classroomNames[classroomId] = doc.ContainsField("name") ? doc.GetValue<string>("name") : "Classroom";

                        // Only start a new announcements listener for classrooms we're not
                        // already watching - joining/archiving elsewhere shouldn't restart
                        // (and re-read) listeners for classrooms that were already enrolled.
                        if (!announcementsByClassroom.ContainsKey(classroomId))
                        {
                            announcementsByClassroom[classroomId] = new List<NotificationRecord>();

                            var annListener = Db.Collection("classrooms").Document(classroomId).Collection("announcements")
                                .OrderByDescending("createdAt")
                                .Limit(maxPerClassroom)
                                .Listen(annSnapshot =>
                                {
                                    var list = new List<NotificationRecord>();
                                    foreach (var annDoc in annSnapshot.Documents)
                                    {
                                        var createdAt = annDoc.ContainsField("createdAt") ? annDoc.GetValue<Timestamp>("createdAt") : Timestamp.GetCurrentTimestamp();
                                        list.Add(new NotificationRecord
                                        {
                                            ClassroomId = classroomId,
                                            ClassroomName = classroomNames.TryGetValue(classroomId, out var name) ? name : "Classroom",
                                            AnnouncementId = annDoc.Id,
                                            Title = annDoc.ContainsField("title") ? annDoc.GetValue<string>("title") : "",
                                            Body = annDoc.ContainsField("body") ? annDoc.GetValue<string>("body") : "",
                                            CreatedAt = createdAt,
                                            IsRead = hasLastRead && createdAt.ToDateTime() <= lastReadAt.ToDateTime()
                                        });
                                    }

                                    announcementsByClassroom[classroomId] = list;
                                    Recompute();
                                });

                            subscription.SetAnnouncementListener(classroomId, annListener);
                        }
                    }

                    // Stop listening to classrooms we're no longer enrolled in (e.g. the
                    // teacher removed this student) - avoids leaking a listener per
                    // classroom the student has since left.
                    List<string> staleIds = null;
                    foreach (var id in announcementsByClassroom.Keys)
                    {
                        if (!incomingIds.Contains(id)) (staleIds ??= new List<string>()).Add(id);
                    }

                    if (staleIds != null)
                    {
                        foreach (var id in staleIds)
                        {
                            subscription.StopAnnouncementListener(id);
                            announcementsByClassroom.Remove(id);
                            classroomNames.Remove(id);
                        }
                    }

                    Recompute();
                });
            subscription.SetClassroomsListener(classroomsListener);

            return subscription;
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
                        foreach (var doc in task.Result.Documents) results.Add(ToMemberStat(doc));
                    }
                    onComplete?.Invoke(results);
                });
        }

        /// <summary>Live version of FetchClassroomRoster() - feeds both the Students tab
        /// (unsorted) and the Leaderboard tab (caller sorts by points) since they're the
        /// same `members` subcollection. Keep the returned ListenerRegistration and
        /// Stop() it in OnDisable / on classroom change. onUpdate fires once immediately,
        /// then again any time ANY member's denormalized stats change - i.e. every time
        /// any student in this classroom completes a quiz (see RecordQuizCompletion) -
        /// so the leaderboard reorders live without the viewer refreshing anything.</summary>
        public ListenerRegistration ListenToClassroomRoster(string classroomId, Action<List<MemberStat>> onUpdate)
        {
            if (string.IsNullOrEmpty(classroomId)) { onUpdate?.Invoke(new List<MemberStat>()); return null; }

            return Db.Collection("classrooms").Document(classroomId).Collection("members")
                .Listen(snapshot =>
                {
                    var results = new List<MemberStat>();
                    foreach (var doc in snapshot.Documents) results.Add(ToMemberStat(doc));
                    onUpdate?.Invoke(results);
                });
        }

        private static MemberStat ToMemberStat(DocumentSnapshot doc)
        {
            return new MemberStat
            {
                StudentId = doc.Id,
                Name = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : "Student",
                Level = doc.ContainsField("level") ? doc.GetValue<int>("level") : 1,
                Points = doc.ContainsField("points") ? doc.GetValue<int>("points") : 0,
                QuizzesCompleted = doc.ContainsField("quizzesCompleted") ? doc.GetValue<int>("quizzesCompleted") : 0,
                AvgScorePercent = doc.ContainsField("avgScorePercent") ? (float)doc.GetValue<double>("avgScorePercent") : 0f
            };
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

        /// <summary>Handle returned by ListenToAvailableQuizzes. Wraps one live listener on
        /// the classroom doc (to track publishedQuizIds) plus a set of live listeners on
        /// the quizzes themselves (rebuilt, chunked by Firestore's 10-value WhereIn cap,
        /// only when the id SET changes) into a single object - keep it and Stop() it in
        /// OnDisable, the same way a plain ListenerRegistration is handled elsewhere.</summary>
        public class AvailableQuizzesListenerHandle
        {
            private ListenerRegistration _classroomListener;
            private readonly List<ListenerRegistration> _quizChunkListeners = new List<ListenerRegistration>();

            internal void SetClassroomListener(ListenerRegistration listener) => _classroomListener = listener;

            internal void ReplaceQuizListeners(List<ListenerRegistration> listeners)
            {
                foreach (var l in _quizChunkListeners) l.Stop();
                _quizChunkListeners.Clear();
                _quizChunkListeners.AddRange(listeners);
            }

            public void Stop()
            {
                _classroomListener?.Stop();
                _classroomListener = null;
                foreach (var l in _quizChunkListeners) l.Stop();
                _quizChunkListeners.Clear();
            }
        }

        /// <summary>Live version of FetchAvailableQuizzes(). Keep the returned handle and
        /// Stop() it in OnDisable / on classroom change. onUpdate fires once immediately
        /// and again whenever: the teacher publishes/unpublishes a quiz for this classroom
        /// (tracked via the classroom doc's publishedQuizIds), OR the content of an
        /// already-published quiz changes (title/questions/time limit/deadline/etc,
        /// tracked via a live WhereIn listener on the quizzes themselves). The per-chunk
        /// quiz listeners are only torn down and rebuilt when the id SET changes - editing
        /// one quiz's title doesn't churn any chunk's subscription.</summary>
        public AvailableQuizzesListenerHandle ListenToAvailableQuizzes(string classroomId, Action<List<QuizSummary>> onUpdate)
        {
            var handle = new AvailableQuizzesListenerHandle();
            if (string.IsNullOrEmpty(classroomId)) { onUpdate?.Invoke(new List<QuizSummary>()); return handle; }

            List<string> lastIds = null;
            // One entry per chunk listener, merged and re-pushed whenever any chunk updates.
            var latestByChunk = new Dictionary<int, List<QuizSummary>>();

            void PushMerged()
            {
                var merged = new List<QuizSummary>();
                foreach (var kv in latestByChunk.OrderBy(k => k.Key)) merged.AddRange(kv.Value);
                onUpdate?.Invoke(merged);
            }

            void RebuildQuizListeners(List<string> quizIds)
            {
                latestByChunk.Clear();

                if (quizIds == null || quizIds.Count == 0)
                {
                    handle.ReplaceQuizListeners(new List<ListenerRegistration>());
                    onUpdate?.Invoke(new List<QuizSummary>());
                    return;
                }

                var chunks = new List<List<string>>();
                for (int i = 0; i < quizIds.Count; i += 10) // Firestore WhereIn caps at 10 values
                {
                    chunks.Add(quizIds.GetRange(i, Mathf.Min(10, quizIds.Count - i)));
                }

                var newListeners = new List<ListenerRegistration>();
                for (int c = 0; c < chunks.Count; c++)
                {
                    int chunkIndex = c;
                    var listener = Db.Collection("quizzes")
                        .WhereIn(FieldPath.DocumentId, chunks[chunkIndex].ConvertAll(id => (object)id))
                        .Listen(snapshot =>
                        {
                            var chunkResults = new List<QuizSummary>();
                            foreach (var doc in snapshot.Documents) chunkResults.Add(ToQuizSummary(doc));
                            latestByChunk[chunkIndex] = chunkResults;
                            PushMerged();
                        });
                    newListeners.Add(listener);
                }
                handle.ReplaceQuizListeners(newListeners);
            }

            var classroomListener = Db.Collection("classrooms").Document(classroomId).Listen(snapshot =>
            {
                if (!snapshot.Exists) { RebuildQuizListeners(null); return; }

                var ids = snapshot.ContainsField("publishedQuizIds")
                    ? snapshot.GetValue<List<string>>("publishedQuizIds")
                    : new List<string>();

                // Only rebuild the (relatively expensive) per-chunk listeners when the SET
                // of published ids actually changed - a classroom edit unrelated to
                // publishedQuizIds (e.g. description) fires this same Listen() callback but
                // shouldn't tear down and resubscribe every quiz chunk.
                bool changed = lastIds == null || ids.Count != lastIds.Count
                    || !new HashSet<string>(ids).SetEquals(lastIds);
                if (!changed) return;

                lastIds = ids;
                RebuildQuizListeners(ids);
            });

            handle.SetClassroomListener(classroomListener);
            return handle;
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
            /// <summary>Only populated by FetchRecentJoinsForClassrooms (admin side) -
            /// left blank by FetchRecentJoins (student side), which doesn't need it
            /// since the joins it returns are always the current student's own.</summary>
            public string StudentName;
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

        /// <summary>Call when showing AdminDashboardController's Recent Activity card.
        /// Same idea as FetchRecentJoins() above, but for an admin: instead of
        /// discovering classrooms via memberIds (a student only knows their own),
        /// the caller already knows which classrooms are theirs (from
        /// AdminClassroomService.FetchMyClassrooms), so this just queries each
        /// classroom's `members` subcollection directly, ordered by joinedAt, and
        /// merges. Same per-classroom fan-out cost as FetchRecentJoins/
        /// FetchNotifications.</summary>
        public void FetchRecentJoinsForClassrooms(List<(string classroomId, string classroomName)> classrooms, Action<List<ClassroomJoinRecord>> onComplete, int maxItems = 8)
        {
            if (classrooms == null || classrooms.Count == 0) { onComplete?.Invoke(new List<ClassroomJoinRecord>()); return; }

            var results = new List<ClassroomJoinRecord>();
            int pending = classrooms.Count;

            foreach (var (classroomId, classroomName) in classrooms)
            {
                Db.Collection("classrooms").Document(classroomId).Collection("members")
                    .OrderByDescending("joinedAt")
                    .Limit(maxItems)
                    .GetSnapshotAsync()
                    .ContinueWithOnMainThread(task =>
                    {
                        if (!task.IsCanceled && !task.IsFaulted)
                        {
                            foreach (var doc in task.Result.Documents)
                            {
                                if (!doc.ContainsField("joinedAt")) continue;

                                results.Add(new ClassroomJoinRecord
                                {
                                    ClassroomId = classroomId,
                                    ClassroomName = classroomName,
                                    JoinedAt = doc.GetValue<Timestamp>("joinedAt"),
                                    StudentName = doc.ContainsField("studentName") ? doc.GetValue<string>("studentName") : "A student"
                                });
                            }
                        }

                        pending--;
                        if (pending > 0) return;

                        results.Sort((a, b) => b.JoinedAt.ToDateTime().CompareTo(a.JoinedAt.ToDateTime()));
                        if (results.Count > maxItems)
                        {
                            results.RemoveRange(maxItems, results.Count - maxItems);
                        }
                        onComplete?.Invoke(results);
                    });
            }
        }
    }
}