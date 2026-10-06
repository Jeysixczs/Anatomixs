using System;
using System.Collections.Generic;
using System.Linq;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Counts the student's earned badges with the same rule the Achievements screen uses,
    /// so every screen shows the same number: a badge is earned if its id is in the
    /// student's saved badgesEarned list OR their total points reach its requirement.
    /// Badge definitions are merged across every teacher whose classroom the student is
    /// in (plus anything BadgeCatalog already holds); saved ids with no matching
    /// definition (renamed/removed badges) still count.
    /// </summary>
    public static class BadgeProgress
    {
        /// <summary>onComplete receives the earned count, or -1 if it couldn't be worked out
        /// (no signed-in student / services not ready) - callers should keep their value.</summary>
        public static void FetchEarnedCount(Action<int> onComplete)
        {
            var student = PlayerSessionManager.Instance?.CurrentStudent;
            if (student == null || AdminGamificationService.Instance == null)
            {
                onComplete?.Invoke(-1);
                return;
            }

            void LoadForTeachers(List<string> teacherIds)
            {
                if (teacherIds.Count == 0) teacherIds.Add(null); // shared default badge set

                var merged = new Dictionary<string, AdminGamificationService.BadgeEntry>();
                int pending = teacherIds.Count;

                foreach (var teacherId in teacherIds)
                {
                    AdminGamificationService.Instance.FetchSettingsForTeacher(teacherId, settings =>
                    {
                        foreach (var b in settings?.Badges ?? new List<AdminGamificationService.BadgeEntry>())
                        {
                            if (!string.IsNullOrEmpty(b.BadgeId)) merged[b.BadgeId] = b;
                        }

                        pending--;
                        if (pending == 0) onComplete?.Invoke(CountEarned(student, merged));
                    });
                }
            }

            if (ClassroomService.Instance == null)
            {
                LoadForTeachers(new List<string>());
                return;
            }

            ClassroomService.Instance.FetchMyClassrooms(classrooms =>
            {
                var teacherIds = (classrooms ?? new List<ClassroomService.ClassroomRecord>())
                    .Select(c => c.TeacherId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct()
                    .ToList();
                LoadForTeachers(teacherIds);
            });
        }

        private static int CountEarned(PlayerSessionManager.StudentProfile student, Dictionary<string, AdminGamificationService.BadgeEntry> merged)
        {
            foreach (var shared in BadgeCatalog.For(student.Uid))
            {
                if (!merged.ContainsKey(shared.BadgeId)) merged[shared.BadgeId] = shared;
            }

            var earnedIds = new HashSet<string>(student.BadgesEarned ?? new List<string>());

            int count = merged.Values.Count(b => earnedIds.Contains(b.BadgeId) || student.TotalPoints >= b.PointsRequired);
            count += earnedIds.Count(id => !merged.ContainsKey(id));
            return count;
        }
    }
}
