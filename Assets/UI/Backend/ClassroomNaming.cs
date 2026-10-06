using Firebase.Firestore;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// One place that decides how a classroom's name and section are shown together,
    /// so every screen (dashboard cards, student hub, classroom detail headers,
    /// notifications, activity feed, reports...) renders them identically.
    /// e.g. Name "Anatomy 101" + Section "BSN 1-A" -> "Anatomy 101 - BSN 1-A".
    /// Classrooms created before sections existed have no "section" field and
    /// simply show their name.
    /// </summary>
    public static class ClassroomNaming
    {
        public const string Separator = " - ";

        public static string Compose(string name, string section)
        {
            name = name?.Trim();
            section = section?.Trim();

            if (string.IsNullOrEmpty(section)) return name ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return section;
            return name + Separator + section;
        }

        /// <summary>Reads "name" + "section" off a classrooms/{id} doc (section is optional).</summary>
        public static string FromDoc(DocumentSnapshot doc, string fallbackName = "Classroom")
        {
            string name = doc.ContainsField("name") ? doc.GetValue<string>("name") : fallbackName;
            string section = doc.ContainsField("section") ? doc.GetValue<string>("section") : null;
            return Compose(name, section);
        }
    }
}
