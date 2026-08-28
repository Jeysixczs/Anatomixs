using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Uploads a student's avatar image to Cloudinary via an UNSIGNED upload
    /// preset - no API secret ever ships in the build. This script contains
    /// NO Firestore logic and NO UI logic - it only talks to Cloudinary and
    /// hands the caller back a secure_url, the same separation
    /// AnatomyPlayModeFirebase/AnatomyPlayModeLocalStorage keep for their
    /// own concerns.
    ///
    /// Caller (e.g. StudentEditProfileController) is responsible for:
    ///   1. Getting image bytes (native photo picker / file dialog)
    ///   2. Calling UploadAvatar()
    ///   3. On success, writing the returned URL to students/{uid}.avatarUrl
    ///      via Firestore - StudentProfileController already renders
    ///      whatever's in PlayerSessionManager.CurrentStudent.AvatarUrl.
    ///
    /// Attach anywhere on the persistent Bootstrap GameObject (same one as
    /// FirebaseBootstrap/PlayerSessionManager) and set cloudName/uploadPreset
    /// in the Inspector from your Cloudinary dashboard.
    /// </summary>
    public class CloudinaryAvatarUploadService : MonoBehaviour
    {
        public static CloudinaryAvatarUploadService Instance { get; private set; }

        [Header("Cloudinary")]
        [Tooltip("Cloudinary Dashboard home page, top-left. Not a secret.")]
        [SerializeField] private string cloudName;

        [Tooltip("Settings -> Upload -> Upload presets. Must be an UNSIGNED preset - " +
                 "signed presets require an API secret, which must never ship in the app.")]
        [SerializeField] private string uploadPreset;

        [Tooltip("Cloudinary 'Asset folder' the preset is scoped to, purely for building " +
                 "a stable public_id below (e.g. 'avatars/{uid}') so re-uploads overwrite " +
                 "the old avatar instead of piling up new assets. Leave blank if your " +
                 "preset doesn't use one.")]
        [SerializeField] private string assetFolder = "avatars";

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        /// <summary>Uploads imageBytes (jpg/png) as studentUid's avatar. onComplete
        /// fires exactly once with (true, secure_url) on success or (false, null)
        /// on failure - callers decide what to do next (e.g. write secure_url to
        /// Firestore, or show a retry prompt).</summary>
        public void UploadAvatar(byte[] imageBytes, string studentUid, Action<bool, string> onComplete)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                Debug.LogWarning("[CloudinaryAvatarUploadService] No image bytes provided - skipping upload.");
                onComplete?.Invoke(false, null);
                return;
            }

            if (string.IsNullOrEmpty(cloudName) || string.IsNullOrEmpty(uploadPreset))
            {
                Debug.LogError("[CloudinaryAvatarUploadService] cloudName/uploadPreset not configured in the Inspector.");
                onComplete?.Invoke(false, null);
                return;
            }

            if (string.IsNullOrEmpty(studentUid))
            {
                Debug.LogWarning("[CloudinaryAvatarUploadService] No studentUid - skipping upload.");
                onComplete?.Invoke(false, null);
                return;
            }

            StartCoroutine(UploadRoutine(imageBytes, studentUid, onComplete));
        }

        private IEnumerator UploadRoutine(byte[] imageBytes, string studentUid, Action<bool, string> onComplete)
        {
            string url = $"https://api.cloudinary.com/v1_1/{cloudName}/image/upload";

            var form = new WWWForm();
            form.AddBinaryData("file", imageBytes, $"{studentUid}_avatar.jpg", "image/jpeg");
            form.AddField("upload_preset", uploadPreset);

            // Deterministic public_id (same shape as PlayModeAnswerRecord.DocumentId) -
            // means re-uploading a new photo overwrites this student's existing
            // Cloudinary asset instead of accumulating a new one every time.
            string publicId = string.IsNullOrEmpty(assetFolder) ? studentUid : $"{assetFolder}/{studentUid}";
            form.AddField("public_id", publicId);

            using (var request = UnityWebRequest.Post(url, form))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[CloudinaryAvatarUploadService] Upload failed for '{studentUid}': " +
                                      $"{request.error} ({request.downloadHandler?.text})");
                    onComplete?.Invoke(false, null);
                    yield break;
                }

                string secureUrl = ExtractSecureUrl(request.downloadHandler.text);
                if (string.IsNullOrEmpty(secureUrl))
                {
                    Debug.LogWarning($"[CloudinaryAvatarUploadService] Upload succeeded but no secure_url in response: {request.downloadHandler.text}");
                    onComplete?.Invoke(false, null);
                    yield break;
                }

                onComplete?.Invoke(true, secureUrl);
            }
        }

        // Reuses the project's existing MiniJson parser (see BoneDatabaseService)
        // rather than pulling in a new JSON dependency just for one field.
        private static string ExtractSecureUrl(string json)
        {
            try
            {
                if (MiniJson.Parse(json) is Dictionary<string, object> root &&
                    root.TryGetValue("secure_url", out var value) && value is string url)
                {
                    return url;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudinaryAvatarUploadService] Could not parse Cloudinary response: {e.Message}");
            }

            return null;
        }
    }
}
