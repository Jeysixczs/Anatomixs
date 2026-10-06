using System.Collections.Generic;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// In-memory hand-off of badge definitions between student screens. The
    /// Classroom Detail screen registers the badges of the classroom's teacher
    /// when it loads them; the Achievements screen merges whatever was registered
    /// into its own list, so a badge shown as earned inside a classroom always
    /// shows up under Achievements too - even if that classroom's teacher wasn't
    /// in the list Achievements looked up earlier (e.g. the student joined the
    /// classroom after Achievements first opened).
    /// Scoped to one student uid; switching accounts starts a fresh catalog.
    /// </summary>
    public static class BadgeCatalog
    {
        private static string _ownerUid;
        private static readonly Dictionary<string, AdminGamificationService.BadgeEntry> Badges =
            new Dictionary<string, AdminGamificationService.BadgeEntry>();

        public static void Register(string studentUid, IEnumerable<AdminGamificationService.BadgeEntry> badges)
        {
            if (string.IsNullOrEmpty(studentUid) || badges == null) return;

            if (_ownerUid != studentUid)
            {
                Badges.Clear();
                _ownerUid = studentUid;
            }

            foreach (var b in badges)
            {
                if (b != null && !string.IsNullOrEmpty(b.BadgeId)) Badges[b.BadgeId] = b;
            }
        }

        public static IEnumerable<AdminGamificationService.BadgeEntry> For(string studentUid)
        {
            if (string.IsNullOrEmpty(studentUid) || _ownerUid != studentUid)
                return new List<AdminGamificationService.BadgeEntry>();
            return new List<AdminGamificationService.BadgeEntry>(Badges.Values);
        }
    }
}
