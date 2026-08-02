using System;
using System.Collections.Generic;
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
    }
}
