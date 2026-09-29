using System;
using System.Collections.Generic;
using System.Linq;
using Firebase.Firestore;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// One teacher-uploaded learning material (a module / lesson file) for a classroom.
    /// Stored at `classrooms/{classroomId}/materials/{materialId}`; the file bytes
    /// themselves live in Cloudflare R2 under `storageKey`, written by the Worker.
    /// Shared by the admin side (AdminClassroomService) and the student side
    /// (ClassroomService) so both read exactly the same fields.
    /// </summary>
    [Serializable]
    public class ClassroomMaterial
    {
        public string MaterialId;
        public string Title;
        public string Description;
        public string FileName;
        public long FileSize;
        public string MimeType;
        /// <summary>R2 object key the Worker returned - always persist what came back,
        /// never build this on the client.</summary>
        public string StorageKey;
        public string AuthorId;
        public Timestamp CreatedAt;

        public static ClassroomMaterial FromSnapshot(DocumentSnapshot doc)
        {
            return new ClassroomMaterial
            {
                MaterialId = doc.Id,
                Title = doc.ContainsField("title") ? doc.GetValue<string>("title") : "",
                Description = doc.ContainsField("description") ? doc.GetValue<string>("description") : "",
                FileName = doc.ContainsField("fileName") ? doc.GetValue<string>("fileName") : "",
                FileSize = doc.ContainsField("fileSize") ? doc.GetValue<long>("fileSize") : 0L,
                MimeType = doc.ContainsField("mimeType") ? doc.GetValue<string>("mimeType") : "",
                StorageKey = doc.ContainsField("storageKey") ? doc.GetValue<string>("storageKey") : "",
                AuthorId = doc.ContainsField("authorId") ? doc.GetValue<string>("authorId") : "",
                CreatedAt = doc.ContainsField("createdAt") ? doc.GetValue<Timestamp>("createdAt") : Timestamp.GetCurrentTimestamp()
            };
        }

        public Dictionary<string, object> ToMap()
        {
            return new Dictionary<string, object>
            {
                { "title", Title ?? "" },
                { "description", Description ?? "" },
                { "fileName", FileName ?? "" },
                { "fileSize", FileSize },
                { "mimeType", MimeType ?? "" },
                { "storageKey", StorageKey ?? "" },
                { "authorId", AuthorId ?? "" },
                { "createdAt", CreatedAt }
            };
        }
    }

    /// <summary>Client-side mirror of the Worker's material rules (the Worker is the
    /// real gate - this only lets the teacher find out before waiting on an upload).
    /// Keep in sync with MATERIAL_ALLOWED_EXTENSIONS / MATERIAL_MAX_BYTES in
    /// cloudflare/anatomia-submissions/src/index.js.</summary>
    public static class MaterialConfig
    {
        /// <summary>Teacher upload limit for classroom materials (students default to 25 MB
        /// for submissions - see FileSubmissionConfig.DefaultMaxFileSizeMB).</summary>
        public const int MaxFileSizeMB = 100;

        public static readonly string[] AllowedExtensions =
        {
            "pdf", "doc", "docx", "ppt", "pptx", "xls", "xlsx", "txt", "csv", "png", "jpg", "jpeg"
        };

        /// <summary>Returns a teacher-facing problem, or null when the file is acceptable.</summary>
        public static string Validate(string fileName, long sizeBytes)
        {
            string ext = FileSubmissionConfig.ExtensionOf(fileName);
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
                return "Only " + string.Join(", ", AllowedExtensions.Select(e => e.ToUpperInvariant())) + " files can be uploaded.";

            if (sizeBytes <= 0)
                return "That file is empty. Please choose a different file.";

            if (sizeBytes > MaxFileSizeMB * 1024L * 1024L)
                return $"That file is larger than the {MaxFileSizeMB} MB limit.";

            return null;
        }
    }
}
