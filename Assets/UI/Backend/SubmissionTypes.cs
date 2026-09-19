using System;
using System.Collections.Generic;
using System.Linq;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Values expected in `quizzes/{quizId}.submissionType`.
    ///
    /// This is the QUIZ-LEVEL type (how a student answers the whole assignment)
    /// and is deliberately separate from QuestionTypeSlugs, which is the
    /// QUESTION-LEVEL type inside a normal question-based quiz.
    ///
    /// Every quiz created before this feature existed has no `submissionType`
    /// field at all, so QuizService.ToQuizRecord / ClassroomService.ToQuizSummary
    /// default a missing field to <see cref="Question"/> - nothing about the
    /// existing quiz system changes behaviour.
    /// </summary>
    public static class SubmissionTypes
    {
        /// <summary>The existing behaviour: a quiz made of `questions[]`, answered
        /// on StudentQuizGameplayController.</summary>
        public const string Question = "question";

        /// <summary>File-submission assignment: no `questions[]`. The student
        /// uploads one file per attempt (StudentFileSubmissionController) and the
        /// teacher grades it by hand (AdminSubmissionReviewController).</summary>
        public const string File = "file";

        public static bool IsFileSubmission(string submissionType) =>
            string.Equals(submissionType, File, StringComparison.OrdinalIgnoreCase);

        /// <summary>Normalizes whatever came out of Firestore (missing, empty,
        /// odd casing) into exactly one of the two constants above.</summary>
        public static string Normalize(string submissionType) =>
            IsFileSubmission(submissionType) ? File : Question;
    }

    /// <summary>
    /// The teacher-configured rules for a File Submission assignment. Persisted
    /// as `quizzes/{quizId}.fileSubmissionSettings`:
    ///
    ///   fileSubmissionSettings:
    ///     allowedExtensions: ["pdf", "docx", ...]   (lowercase, no dot)
    ///     allowedMimeTypes:  ["application/pdf", ...]
    ///     maxFileSizeMB:     25
    ///
    /// Attempts and deadline are NOT duplicated in here - a file assignment
    /// reuses the quiz doc's existing `maxAttempts` / `isDeadlineEnabled` /
    /// `deadline` fields, and its point value reuses `pointsPossible`, so the
    /// existing attempt + deadline + scoring rules apply unchanged.
    ///
    /// Everything this class validates is re-validated inside the Cloudflare
    /// Worker against the same quiz doc. Client-side checks here exist to give
    /// the student a fast, friendly error - they are never the enforcement point.
    /// </summary>
    [Serializable]
    public class FileSubmissionConfig
    {
        /// <summary>Lowercase, no leading dot - e.g. "pdf", "docx".</summary>
        public List<string> AllowedExtensions = new List<string>();

        public List<string> AllowedMimeTypes = new List<string>();

        public int MaxFileSizeMB = 25;

        /// <summary>The six types the assignment form offers out of the box.
        /// The teacher ticks whichever of these they want - nothing is hard-coded
        /// to PDF anywhere in the flow.</summary>
        public static readonly string[] SupportedExtensions = { "pdf", "doc", "docx", "xls", "xlsx", "txt" };

        /// <summary>extension -> the MIME type Android/iOS report for it. Used to
        /// build `allowedMimeTypes` from the teacher's ticked extensions, and to
        /// drive the native file picker's type filter on the student side.</summary>
        public static readonly Dictionary<string, string> ExtensionToMimeType = new Dictionary<string, string>
        {
            { "pdf",  "application/pdf" },
            { "doc",  "application/msword" },
            { "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            { "xls",  "application/vnd.ms-excel" },
            { "xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
            { "txt",  "text/plain" }
        };

        public static FileSubmissionConfig Default()
        {
            return FromExtensions(new List<string> { "pdf", "doc", "docx" }, 25);
        }

        /// <summary>Builds a config from the teacher's ticked extensions, deriving
        /// the MIME allow-list from the same list so the two can never drift.</summary>
        public static FileSubmissionConfig FromExtensions(IEnumerable<string> extensions, int maxFileSizeMB)
        {
            var normalized = (extensions ?? Enumerable.Empty<string>())
                .Select(NormalizeExtension)
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct()
                .ToList();

            return new FileSubmissionConfig
            {
                AllowedExtensions = normalized,
                AllowedMimeTypes = normalized
                    .Select(e => ExtensionToMimeType.TryGetValue(e, out var mime) ? mime : null)
                    .Where(m => !string.IsNullOrEmpty(m))
                    .Distinct()
                    .ToList(),
                MaxFileSizeMB = maxFileSizeMB > 0 ? maxFileSizeMB : 25
            };
        }

        /// <summary>"PDF", ".pdf", "  pdf " -> "pdf".</summary>
        public static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return string.Empty;
            return extension.Trim().TrimStart('.').ToLowerInvariant();
        }

        public static string ExtensionOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return string.Empty;
            int dot = fileName.LastIndexOf('.');
            return dot < 0 || dot == fileName.Length - 1
                ? string.Empty
                : NormalizeExtension(fileName.Substring(dot + 1));
        }

        public static string MimeTypeFor(string extension)
        {
            string normalized = NormalizeExtension(extension);
            return ExtensionToMimeType.TryGetValue(normalized, out var mime) ? mime : "application/octet-stream";
        }

        public long MaxFileSizeBytes => (long)Math.Max(1, MaxFileSizeMB) * 1024L * 1024L;

        public bool IsExtensionAllowed(string extension)
        {
            string normalized = NormalizeExtension(extension);
            if (string.IsNullOrEmpty(normalized)) return false;
            if (AllowedExtensions == null || AllowedExtensions.Count == 0) return false;
            return AllowedExtensions.Any(e => NormalizeExtension(e) == normalized);
        }

        /// <summary>Human-readable accept list for the student screen -
        /// "PDF, DOC, DOCX, XLS, XLSX, TXT".</summary>
        public string DisplayExtensions()
        {
            if (AllowedExtensions == null || AllowedExtensions.Count == 0) return "None";
            return string.Join(", ", AllowedExtensions.Select(e => NormalizeExtension(e).ToUpperInvariant()));
        }

        /// <summary>Validates a picked file BEFORE any upload is attempted.
        /// Returns null when the file is acceptable, or the message to show.</summary>
        public string Validate(string fileName, long fileSizeBytes)
        {
            string extension = ExtensionOf(fileName);

            if (string.IsNullOrEmpty(extension))
                return "That file has no extension, so it can't be checked against the accepted types.";

            if (!IsExtensionAllowed(extension))
                return $"Only {DisplayExtensions()} files are accepted for this assignment.";

            if (fileSizeBytes <= 0)
                return "That file is empty. Please choose a different file.";

            if (fileSizeBytes > MaxFileSizeBytes)
                return $"That file is {FormatSize(fileSizeBytes)}. The maximum for this assignment is {MaxFileSizeMB} MB.";

            return null;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L) return $"{bytes / (1024f * 1024f):0.0} MB";
            if (bytes >= 1024L) return $"{bytes / 1024f:0.0} KB";
            return $"{bytes} B";
        }

        // ---------------- Firestore mapping ----------------

        public Dictionary<string, object> ToMap()
        {
            return new Dictionary<string, object>
            {
                { "allowedExtensions", (AllowedExtensions ?? new List<string>()).Select(NormalizeExtension).ToList() },
                { "allowedMimeTypes", AllowedMimeTypes ?? new List<string>() },
                { "maxFileSizeMB", MaxFileSizeMB > 0 ? MaxFileSizeMB : 25 }
            };
        }

        /// <summary>Reads the `fileSubmissionSettings` map back off a quiz doc.
        /// A missing/!malformed map falls back to Default() rather than throwing,
        /// so a half-written quiz can still be opened and fixed.</summary>
        public static FileSubmissionConfig FromMap(object raw)
        {
            if (!(raw is Dictionary<string, object> map)) return Default();

            var config = new FileSubmissionConfig();

            if (map.TryGetValue("allowedExtensions", out var exts) && exts is List<object> extList)
            {
                config.AllowedExtensions = extList
                    .Select(e => NormalizeExtension(e?.ToString()))
                    .Where(e => !string.IsNullOrEmpty(e))
                    .ToList();
            }

            if (map.TryGetValue("allowedMimeTypes", out var mimes) && mimes is List<object> mimeList)
            {
                config.AllowedMimeTypes = mimeList
                    .Select(m => m?.ToString())
                    .Where(m => !string.IsNullOrEmpty(m))
                    .ToList();
            }

            config.MaxFileSizeMB = map.TryGetValue("maxFileSizeMB", out var size)
                ? Math.Max(1, Convert.ToInt32(size))
                : 25;

            if (config.AllowedExtensions.Count == 0) return Default();

            // Self-heal a quiz whose MIME list was never written (or was written by
            // an older build) - derive it from the extensions the teacher chose.
            if (config.AllowedMimeTypes.Count == 0)
            {
                config.AllowedMimeTypes = config.AllowedExtensions
                    .Select(e => ExtensionToMimeType.TryGetValue(e, out var mime) ? mime : null)
                    .Where(m => !string.IsNullOrEmpty(m))
                    .Distinct()
                    .ToList();
            }

            return config;
        }
    }
}
