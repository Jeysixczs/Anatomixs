using System;
using System.IO;
using System.Linq;
using Anatomia3D.Backend;
using UnityEngine;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Downloads a teaching material through the Worker and opens it in the phone's
    /// own viewer app (Adobe / WPS / Word / Photos ...), falling back to the Save As /
    /// share sheet when no viewer can be launched. Shared by the teacher's Materials tab
    /// (AdminClassroomDetailController) and the student's (StudentClassroomDetailController)
    /// so both open files exactly the same way.
    /// </summary>
    public static class MaterialFileOpener
    {
        /// <param name="onBusyChanged">true when the download starts, false when it ends
        /// (success or failure) - use it to disable the tapped button.</param>
        /// <param name="onError">A user-facing message when something went wrong.</param>
        public static void Open(ClassroomMaterial material, Action<bool> onBusyChanged, Action<string> onError)
        {
            if (material == null || string.IsNullOrEmpty(material.StorageKey))
            {
                onError?.Invoke("This material has no stored file.");
                return;
            }

            if (NativeFilePicker.IsFilePickerBusy()) return;

            if (!NetworkStatusMonitor.IsOnline)
            {
                onError?.Invoke("You need an internet connection to open this file.");
                return;
            }

            if (R2FileUploadService.Instance == null)
            {
                onError?.Invoke("File downloads are not available right now.");
                return;
            }

            onBusyChanged?.Invoke(true);

            R2FileUploadService.Instance.DownloadMaterial(material.StorageKey, (ok, error, bytes) =>
            {
                onBusyChanged?.Invoke(false);

                if (!ok || bytes == null)
                {
                    onError?.Invoke(error ?? "Could not open that file.");
                    return;
                }

                string path = Path.Combine(Application.temporaryCachePath, SafeFileName(material.FileName));
                try
                {
                    File.WriteAllBytes(path, bytes);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MaterialFileOpener] Could not cache the downloaded file: {e.Message}");
                    onError?.Invoke("Could not open that file on this device.");
                    return;
                }

                if (FileOpener.TryOpen(path, out string openError)) return;

                // No viewer app for this type (or iOS / Editor) - explain, then offer Save As.
                if (!string.IsNullOrEmpty(openError)) onError?.Invoke(openError);

                NativeFilePicker.ExportFile(path, success =>
                {
                    if (!success) Debug.Log("[MaterialFileOpener] File export cancelled.");
                });
            });
        }

        private static string SafeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "material";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
