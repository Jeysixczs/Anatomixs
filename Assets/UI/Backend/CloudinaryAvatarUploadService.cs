using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// Also maintains a local on-disk copy of each user's avatar (under
    /// Application.persistentDataPath), keyed by uid, purely so profile
    /// screens can show the avatar instantly and while offline instead of
    /// depending on a network fetch of the Cloudinary URL every time the
    /// screen opens. This local cache is a convenience mirror, not a source
    /// of truth - students/{uid}.avatarUrl (Firestore) still is. See
    /// SaveAvatarLocally/TryLoadLocalAvatar/DeleteLocalAvatar below.
    ///
    /// Caller (e.g. StudentEditProfileController) is responsible for:
    ///   1. Getting image bytes (native photo picker / file dialog)
    ///   2. Calling UploadAvatar()
    ///   3. On success, writing the returned URL to students/{uid}.avatarUrl
    ///      via Firestore - StudentProfileController already renders
    ///      whatever's in PlayerSessionManager.CurrentStudent.AvatarUrl.
    ///   4. Calling DeleteLocalAvatar(uid) when that user logs out, so a
    ///      signed-out device doesn't keep showing (or leaking) the previous
    ///      user's cached photo - see PlayerSessionManager.LogoutStudent().
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

        [Tooltip("Cloudinary 'Asset folder' the preset is scoped to, purely for grouping " +
                 "each user's avatars under a shared prefix (e.g. 'avatars/{uid}_{timestamp}'). " +
                 "Cloudinary never overwrites existing assets on unsigned uploads, so every " +
                 "upload still gets its own unique public_id underneath this folder. Leave " +
                 "blank if your preset doesn't use one.")]
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

            // IMPORTANT: Cloudinary permanently ignores "overwrite" on UNSIGNED
            // uploads - it is hard-coded to false server-side no matter what the
            // request or the upload preset says (see "Cloudinary always treats
            // overwrite as false in unsigned uploads" in Cloudinary's own Upload
            // API reference). A previous version of this method reused a fixed,
            // deterministic public_id ("avatars/{uid}") on the theory that
            // re-uploading would overwrite the old asset. In practice that only
            // "worked" for a student's/admin's FIRST ever avatar upload, when
            // nothing existed at that public_id yet. Every upload after that hit
            // an existing asset with the same public_id; Cloudinary responded
            // with existing:true, HTTP 200, and the OLD asset's unchanged
            // secure_url - so UploadAvatar reported success, but the "new" URL
            // handed back (and then written to Firestore by the caller) was
            // identical to the one already on file. Net effect: changing a photo
            // silently did nothing once a photo already existed, which is why it
            // reappeared unchanged after leaving/reopening Edit Profile or
            // restarting the app.
            //
            // Fix: give every upload its own unique public_id (still scoped
            // under this user's own asset folder, so assets stay grouped and
            // attributable) so there is never a collision and Cloudinary always
            // creates a brand-new asset with a genuinely new secure_url. This
            // works no matter how the unsigned upload preset is configured.
            //
            // We deliberately do NOT try to delete the old Cloudinary asset from
            // here - that requires a signed request with the API secret, which
            // this class's whole design intentionally keeps out of the client
            // build (see class summary above). The orphaned old asset is
            // harmless: nothing in the app reads it once Firestore's avatarUrl
            // has moved on to the new one.
            string uniqueSuffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string publicId = string.IsNullOrEmpty(assetFolder)
                ? $"{studentUid}_{uniqueSuffix}"
                : $"{assetFolder}/{studentUid}_{uniqueSuffix}";
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

                // Cache the exact bytes we just uploaded, keyed by uid, so this
                // avatar shows up instantly (and offline) next time a profile
                // screen opens - see SaveAvatarLocally below. Deliberately done
                // here rather than at photo-pick time: this only runs once the
                // upload (and therefore the eventual Firestore avatarUrl write)
                // has actually succeeded, so the local cache never gets ahead of
                // what's really saved.
                SaveAvatarLocally(imageBytes, studentUid);

                // Belt-and-braces: with a unique public_id per upload (above) this
                // should never happen again, but if it ever does, "existing":true
                // means Cloudinary handed back an OLD asset's URL instead of a new
                // one - surface that loudly rather than silently reporting success
                // with a stale photo.
                if (ResponseIndicatesExistingAsset(request.downloadHandler.text))
                {
                    Debug.LogWarning($"[CloudinaryAvatarUploadService] Cloudinary reported 'existing: true' for '{studentUid}' - " +
                                      "the returned secure_url may point at a pre-existing asset instead of this upload.");
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

        private static bool ResponseIndicatesExistingAsset(string json)
        {
            try
            {
                return MiniJson.Parse(json) is Dictionary<string, object> root &&
                       root.TryGetValue("existing", out var value) && value is bool existing && existing;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------- Local avatar cache (offline viewing) ----------------
        //
        // Stored under Application.persistentDataPath/avatars/{uid}.jpg - one
        // file per user, always overwritten in place (unlike the Cloudinary
        // public_id above, there's no "existing asset wins" problem here since
        // we're writing straight to a known local path we fully control).

        private string GetLocalAvatarPath(string uid) =>
            Path.Combine(Application.persistentDataPath, "avatars", $"{uid}.jpg");

        /// <summary>Writes imageBytes to this uid's local avatar cache file,
        /// overwriting whatever was there before. Safe to call even if the
        /// "avatars" folder doesn't exist yet. Failures are logged and
        /// swallowed - a missing local cache just means the profile screen
        /// falls back to a network fetch (or initials) next time, same as
        /// before this feature existed.</summary>
        public void SaveAvatarLocally(byte[] imageBytes, string uid)
        {
            if (imageBytes == null || imageBytes.Length == 0 || string.IsNullOrEmpty(uid)) return;

            try
            {
                string path = GetLocalAvatarPath(uid);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, imageBytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudinaryAvatarUploadService] Could not cache avatar locally for '{uid}': {e.Message}");
            }
        }

        /// <summary>Reads this uid's cached avatar bytes, if any exist on disk.
        /// Callers (e.g. StudentEditProfileController.RefreshAvatarPreview)
        /// decode these into a Texture2D themselves via
        /// ImageConversion.LoadImage - kept out of this class since texture
        /// creation/ownership is a UI concern, not an upload-service one.</summary>
        public bool TryLoadLocalAvatar(string uid, out byte[] imageBytes)
        {
            imageBytes = null;
            if (string.IsNullOrEmpty(uid)) return false;

            string path = GetLocalAvatarPath(uid);
            if (!File.Exists(path)) return false;

            try
            {
                imageBytes = File.ReadAllBytes(path);
                return imageBytes != null && imageBytes.Length > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudinaryAvatarUploadService] Could not read cached avatar for '{uid}': {e.Message}");
                imageBytes = null;
                return false;
            }
        }

        /// <summary>Deletes this uid's local avatar cache file, if any. Call on
        /// logout (see PlayerSessionManager.LogoutStudent / the admin
        /// equivalent) so a signed-out device doesn't keep the previous
        /// user's photo sitting on disk, or show it to whoever signs in
        /// next on a shared device.</summary>
        public void DeleteLocalAvatar(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;

            try
            {
                string path = GetLocalAvatarPath(uid);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CloudinaryAvatarUploadService] Could not delete cached avatar for '{uid}': {e.Message}");
            }
        }
    }
}
