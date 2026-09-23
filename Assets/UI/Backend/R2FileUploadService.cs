using System;
using System.Collections;
using System.Text;
using Firebase.Extensions;
using UnityEngine;
using UnityEngine.Networking;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Talks to the Anatomia Cloudflare Worker, which is the ONLY thing that
    /// ever touches Cloudflare R2. No R2 account id, access key id or secret
    /// exists anywhere in this project - the Worker holds the R2 binding, and
    /// this client only ever sends the signed-in user's Firebase ID token.
    ///
    ///     Unity  ->  Cloudflare Worker  ->  Cloudflare R2
    ///
    /// The Worker re-verifies that ID token, re-reads the quiz + classroom docs
    /// from Firestore itself, and re-checks enrolment / ownership / deadline /
    /// attempts / file type / file size before it will store or serve anything.
    /// Everything this client sends (classroomId, quizId, filename) is treated
    /// as untrusted input there - studentId and teacherId are never sent at
    /// all, they're derived from the verified token's `sub` claim.
    ///
    /// This class contains NO Firestore logic and NO UI logic, matching the
    /// separation CloudinaryAvatarUploadService already keeps - the caller
    /// (StudentFileSubmissionController / AdminSubmissionReviewController) is
    /// responsible for writing the Firestore metadata via FileSubmissionService.
    ///
    /// Attach to the same persistent Bootstrap GameObject as FirebaseBootstrap /
    /// PlayerSessionManager / QuizService, and set workerBaseUrl in the
    /// Inspector to your deployed Worker (e.g.
    /// https://anatomia-submissions.&lt;your-subdomain&gt;.workers.dev).
    /// </summary>
    public class R2FileUploadService : MonoBehaviour
    {
        public static R2FileUploadService Instance { get; private set; }

        [Header("Cloudflare Worker")]
        [Tooltip("Base URL of the deployed anatomia-submissions Worker, with no " +
                 "trailing slash. Not a secret - it only accepts requests carrying " +
                 "a valid Firebase ID token.")]
        [SerializeField] private string workerBaseUrl = "https://anatomia-submissions.example.workers.dev";

        [Tooltip("Seconds before an upload/download request gives up.")]
        [SerializeField] private int requestTimeoutSeconds = 120;

        /// <summary>Shown verbatim whenever a submit/download is attempted with no
        /// connectivity. File transfer always needs a real connection - Firestore's
        /// offline queue can't carry file bytes.</summary>
        public const string OfflineMessage = "Cannot submit file while offline. Please connect to the internet and try again.";

        [Serializable]
        public class UploadResult
        {
            /// <summary>R2 object key the Worker actually stored the file under.
            /// This is what goes into `fileSubmissions/{id}.storageKey` - never
            /// build this path on the client, always persist what came back.</summary>
            public string StorageKey;
            public long FileSize;
            public string MimeType;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ==================================================================
        // Auth
        // ==================================================================

        /// <summary>Fetches a fresh Firebase ID token for whoever is signed in
        /// (student or teacher). onComplete gets null if nobody is signed in or
        /// the token could not be minted.</summary>
        private void GetIdToken(Action<string> onComplete)
        {
            var auth = FirebaseBootstrap.Instance != null ? FirebaseBootstrap.Instance.Auth : null;
            var user = auth != null ? auth.CurrentUser : null;

            if (user == null)
            {
                Debug.LogWarning("[R2FileUploadService] No signed-in Firebase user - cannot call the Worker.");
                onComplete?.Invoke(null);
                return;
            }

            user.TokenAsync(false).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || string.IsNullOrEmpty(task.Result))
                {
                    Debug.LogWarning("[R2FileUploadService] Could not get a Firebase ID token.");
                    onComplete?.Invoke(null);
                    return;
                }

                onComplete?.Invoke(task.Result);
            });
        }

        // ==================================================================
        // Upload (student)
        // ==================================================================

        /// <summary>
        /// Uploads one submission file for the signed-in student.
        ///
        /// The Worker decides the final object key; <paramref name="submissionId"/>
        /// is the Firestore doc id the caller reserved for this submission, so the
        /// R2 object and the `fileSubmissions` doc line up one-to-one.
        ///
        /// onComplete fires exactly once with (true, null, result) or
        /// (false, userFacingMessage, null).
        /// </summary>
        public void UploadSubmission(
            string classroomId,
            string quizId,
            string submissionId,
            string fileName,
            string mimeType,
            byte[] fileBytes,
            Action<bool, string, UploadResult> onComplete)
        {
            if (!NetworkStatusMonitor.IsOnline)
            {
                onComplete?.Invoke(false, OfflineMessage, null);
                return;
            }

            if (fileBytes == null || fileBytes.Length == 0)
            {
                onComplete?.Invoke(false, "That file is empty. Please choose a different file.", null);
                return;
            }

            if (string.IsNullOrEmpty(workerBaseUrl))
            {
                Debug.LogError("[R2FileUploadService] workerBaseUrl is not configured in the Inspector.");
                onComplete?.Invoke(false, "File uploads are not configured yet. Please contact your teacher.", null);
                return;
            }

            GetIdToken(token =>
            {
                if (string.IsNullOrEmpty(token))
                {
                    onComplete?.Invoke(false, "Your session expired. Please sign in again.", null);
                    return;
                }

                StartCoroutine(UploadRoutine(token, classroomId, quizId, submissionId, fileName, mimeType, fileBytes, onComplete));
            });
        }

        private IEnumerator UploadRoutine(
            string token, string classroomId, string quizId, string submissionId,
            string fileName, string mimeType, byte[] fileBytes,
            Action<bool, string, UploadResult> onComplete)
        {
            string url = $"{workerBaseUrl.TrimEnd('/')}/v1/submissions/upload";

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(fileBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = requestTimeoutSeconds;

                request.SetRequestHeader("Authorization", $"Bearer {token}");
                request.SetRequestHeader("Content-Type", string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType);
                // studentId is deliberately NOT sent - the Worker uses the verified
                // token's `sub` claim, so a tampered client can't upload as someone else.
                request.SetRequestHeader("X-Anatomia-Classroom-Id", classroomId ?? string.Empty);
                request.SetRequestHeader("X-Anatomia-Quiz-Id", quizId ?? string.Empty);
                request.SetRequestHeader("X-Anatomia-Submission-Id", submissionId ?? string.Empty);
                request.SetRequestHeader("X-Anatomia-File-Name", EncodeHeaderValue(fileName));

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string message = ExtractWorkerError(request);
                    Debug.LogWarning($"[R2FileUploadService] Upload failed ({request.responseCode}): {request.error} / {request.downloadHandler?.text}");
                    onComplete?.Invoke(false, message, null);
                    yield break;
                }

                var parsed = ParseUploadResponse(request.downloadHandler.text);
                if (parsed == null || string.IsNullOrEmpty(parsed.StorageKey))
                {
                    onComplete?.Invoke(false, "Upload finished but the server did not confirm it. Please try again.", null);
                    yield break;
                }

                onComplete?.Invoke(true, null, parsed);
            }
        }

        // ==================================================================
        // Download (student re-view / teacher review)
        // ==================================================================

        /// <summary>
        /// Downloads a stored submission's bytes. The Worker authorizes this per
        /// request: a student may only fetch their own submission, a teacher may
        /// only fetch submissions belonging to a classroom they own.
        ///
        /// Callers typically write the bytes to Application.temporaryCachePath and
        /// hand the path to NativeFilePicker.ExportFile, the same way
        /// AdminReportExportService/AdminAnalyticsReportsController already do for
        /// generated reports.
        /// </summary>
        public void DownloadSubmission(string storageKey, Action<bool, string, byte[]> onComplete)
        {
            if (!NetworkStatusMonitor.IsOnline)
            {
                onComplete?.Invoke(false, "You need an internet connection to open this file.", null);
                return;
            }

            if (string.IsNullOrEmpty(storageKey))
            {
                onComplete?.Invoke(false, "This submission has no stored file.", null);
                return;
            }

            GetIdToken(token =>
            {
                if (string.IsNullOrEmpty(token))
                {
                    onComplete?.Invoke(false, "Your session expired. Please sign in again.", null);
                    return;
                }

                StartCoroutine(DownloadRoutine(token, storageKey, onComplete));
            });
        }

        private IEnumerator DownloadRoutine(string token, string storageKey, Action<bool, string, byte[]> onComplete)
        {
            string url = $"{workerBaseUrl.TrimEnd('/')}/v1/submissions/download?key={UnityWebRequest.EscapeURL(storageKey)}";

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = requestTimeoutSeconds;
                request.SetRequestHeader("Authorization", $"Bearer {token}");

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string message = ExtractWorkerError(request);
                    Debug.LogWarning($"[R2FileUploadService] Download failed ({request.responseCode}): {request.error}");
                    onComplete?.Invoke(false, message, null);
                    yield break;
                }

                onComplete?.Invoke(true, null, request.downloadHandler.data);
            }
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>HTTP headers are latin-1 only, and student filenames are very
        /// often not. Percent-encode so "Buto ng Tao.docx" survives the trip; the
        /// Worker decodes it again (and then sanitizes it regardless).</summary>
        private static string EncodeHeaderValue(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : Uri.EscapeDataString(value);
        }

        /// <summary>The Worker replies to every rejection with
        /// {"error":"...","message":"..."} - surface `message` to the student
        /// rather than a raw HTTP code, and fall back to something readable when
        /// the failure happened below that (DNS, timeout, airplane mode).</summary>
        private static string ExtractWorkerError(UnityWebRequest request)
        {
            string body = request.downloadHandler != null ? request.downloadHandler.text : null;

            if (!string.IsNullOrEmpty(body))
            {
                string message = ExtractJsonString(body, "message");
                if (!string.IsNullOrEmpty(message)) return message;
            }

            if (request.responseCode == 401 || request.responseCode == 403)
                return "You are not allowed to do that. Please sign in again and retry.";
            if (request.responseCode == 413)
                return "That file is too large for this assignment.";
            if (request.responseCode == 415)
                return "That file type is not accepted for this assignment.";
            if (request.responseCode >= 500)
                return "The file server is having trouble right now. Please try again in a moment.";

            return "Could not reach the file server. Please check your connection and try again.";
        }

        private static UploadResult ParseUploadResponse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string key = ExtractJsonString(json, "storageKey");
            if (string.IsNullOrEmpty(key)) return null;

            return new UploadResult
            {
                StorageKey = key,
                FileSize = ExtractJsonLong(json, "fileSize"),
                MimeType = ExtractJsonString(json, "mimeType")
            };
        }

        // Deliberately tiny hand-rolled readers rather than pulling in a JSON
        // dependency - the Worker's two response shapes are fixed and flat.
        private static string ExtractJsonString(string json, string field)
        {
            string needle = "\"" + field + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;

            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return null;

            int firstQuote = json.IndexOf('"', colon + 1);
            if (firstQuote < 0) return null;

            var sb = new StringBuilder();
            for (int i = firstQuote + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                if (c == '"') break;
                sb.Append(c);
            }

            return sb.ToString();
        }

        private static long ExtractJsonLong(string json, string field)
        {
            string needle = "\"" + field + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return 0;

            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return 0;

            var sb = new StringBuilder();
            for (int i = colon + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (char.IsDigit(c)) sb.Append(c);
                else if (sb.Length > 0) break;
            }

            return sb.Length > 0 && long.TryParse(sb.ToString(), out var value) ? value : 0;
        }
    }
}
