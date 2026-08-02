using System;
using System.Collections.Generic;
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
                StudentCount = doc.ContainsField("studentCount") ? doc.GetValue<int>("studentCount") : 0
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
    }
}
