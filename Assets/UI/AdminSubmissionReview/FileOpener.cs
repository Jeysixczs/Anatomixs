using System.IO;
using UnityEngine;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Opens a file that is already on the device directly in the phone's own viewer app
    /// (Adobe / WPS / Google Docs / Excel / Photos ...), instead of the Save As / share sheet.
    ///
    /// Android only. It sends an ACTION_VIEW intent for a content:// URI produced by
    /// androidx FileProvider - the ONLY way another app is allowed to read a file from this
    /// app's private/cache storage on modern Android. That needs the one-time
    /// AnatomiaFileProvider.androidlib setup (see the setup notes) so the provider exists in
    /// the built manifest.
    ///
    /// On iOS / in the Editor TryOpen returns false with no error, so callers fall back to
    /// their existing share/export flow.
    /// </summary>
    public static class FileOpener
    {
        /// <summary>Must match the authority declared in AnatomiaFileProvider.androidlib.</summary>
        private const string AuthoritySuffix = ".anatomia.fileprovider";

        /// <summary>
        /// Tries to open <paramref name="path"/> in an installed viewer app.
        /// Returns true if a viewer was launched. On false, <paramref name="error"/> is a
        /// user-facing message when the failure is worth telling the user about (no app installed
        /// for this file type), or null when it just isn't supported on this platform.
        /// </summary>
        public static bool TryOpen(string path, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "That file is no longer available on this device.";
                return false;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var javaFile = new AndroidJavaObject("java.io.File", path))
                using (var fileProvider = new AndroidJavaClass("androidx.core.content.FileProvider"))
                {
                    string authority = Application.identifier + AuthoritySuffix;

                    using (var uri = fileProvider.CallStatic<AndroidJavaObject>(
                               "getUriForFile", activity, authority, javaFile))
                    using (var intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.VIEW"))
                    {
                        using (intent.Call<AndroidJavaObject>("setDataAndType", uri, GetMimeType(path))) { }
                        // FLAG_GRANT_READ_URI_PERMISSION: lets the viewer read our cache file.
                        using (intent.Call<AndroidJavaObject>("addFlags", 1)) { }

                        activity.Call("startActivity", intent);
                    }
                }
                return true;
            }
            catch (AndroidJavaException e)
            {
                // ActivityNotFoundException = no installed app handles this MIME type.
                if (e.Message != null && e.Message.Contains("ActivityNotFoundException"))
                {
                    error = "No app on this phone can open this type of file. Choose where to save it instead.";
                    return false;
                }

                // Anything else (most likely the FileProvider isn't in the manifest yet).
                Debug.LogWarning("[FileOpener] Could not open the file in a viewer app: " + e.Message);
                return false;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[FileOpener] Could not open the file in a viewer app: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>MIME type for the viewer intent, from the file extension.</summary>
        public static string GetMimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".pdf":  return "application/pdf";
                case ".doc":  return "application/msword";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls":  return "application/vnd.ms-excel";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".ppt":  return "application/vnd.ms-powerpoint";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                case ".csv":  return "text/csv";
                case ".txt":  return "text/plain";
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif":  return "image/gif";
                case ".webp": return "image/webp";
                case ".zip":  return "application/zip";
                default:      return "*/*";
            }
        }
    }
}
