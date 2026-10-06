using System;
using System.Collections.Generic;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Loads a student's profile picture (students/{uid}.avatarUrl - the Cloudinary URL set
    /// on Edit Profile) for admin-side screens, and drops it into a round avatar element.
    /// Shared by AdminStudentStatsModal and the Student Activity feed on AdminDashboard so
    /// both use one session cache (a photo downloaded for one is instantly available to
    /// the other) and one implementation.
    ///
    /// Usage: StudentAvatarLoader.Apply(avatarElement, initialsLabel, studentId);
    /// The initials stay as the fallback for students with no photo / failed downloads.
    /// </summary>
    public static class StudentAvatarLoader
    {
        // A missing entry means "not fetched yet"; a null value means "fetched, this student
        // has no usable photo".
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, List<Action<Texture2D>>> Pending =
            new Dictionary<string, List<Action<Texture2D>>>();

        /// <summary>Shows the student's photo inside <paramref name="avatar"/> (a fixed-size
        /// round element) and hides <paramref name="initialsLabel"/> once it arrives.</summary>
        public static void Apply(VisualElement avatar, VisualElement initialsLabel, string studentId)
        {
            if (avatar == null || string.IsNullOrEmpty(studentId)) return;

            avatar.style.overflow = Overflow.Hidden; // clip the photo to the round shape

            Load(studentId, tex =>
            {
                // No avatar.panel check on purpose: on a cache hit this runs synchronously,
                // before the avatar has been added to the screen, so its panel is still null.
                // Adding the image to a detached element is harmless.
                if (tex == null) return;

                var image = new Image { image = tex, scaleMode = ScaleMode.ScaleAndCrop };
                image.pickingMode = PickingMode.Ignore;
                image.style.position = Position.Absolute;
                image.style.left = image.style.top = image.style.right = image.style.bottom = 0;
                avatar.Add(image);
                if (initialsLabel != null) initialsLabel.style.display = DisplayStyle.None;
            });
        }

        /// <summary>Reads students/{uid}.avatarUrl. Uses AdminClassroomService when it exists
        /// (admin screens); otherwise reads Firestore directly so the student-side classroom
        /// screens (Students / Leaderboard tabs) work too. Calls back with null on no photo
        /// or a failed read.</summary>
        private static void FetchAvatarUrl(string studentId, Action<string> onComplete)
        {
            if (AdminClassroomService.Instance != null)
            {
                AdminClassroomService.Instance.FetchStudentAvatarUrl(studentId, onComplete);
                return;
            }

            var db = FirebaseBootstrap.Instance != null ? FirebaseBootstrap.Instance.Db : null;
            if (db == null) { onComplete(null); return; }

            db.Collection("students").Document(studentId).GetSnapshotAsync()
                .ContinueWithOnMainThread(task =>
                {
                    if (task.IsCanceled || task.IsFaulted || !task.Result.Exists || !task.Result.ContainsField("avatarUrl"))
                    {
                        //if (task.IsFaulted)
                            //Debug.LogWarning($"[StudentAvatarLoader] Could not read avatarUrl for '{studentId}': {task.Exception?.Flatten().InnerException?.Message}");
                        onComplete(null);
                        return;
                    }

                    string url = task.Result.GetValue<string>("avatarUrl");
                    onComplete(string.IsNullOrEmpty(url) ? null : url);
                });
        }

        // ---- Direct-URL path (no Firestore read) ----
        // Student-side screens already have each classmate's avatarUrl from the classroom's
        // members docs (ClassroomService.MemberStat.AvatarUrl), so they skip the per-student
        // students/{uid} read entirely. Keyed by URL: a new photo means a new Cloudinary URL,
        // so a stale image can never be served.
        private static readonly Dictionary<string, Texture2D> UrlCache = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, List<Action<Texture2D>>> UrlPending =
            new Dictionary<string, List<Action<Texture2D>>>();

        /// <summary>Like <see cref="Apply"/>, but with a known avatarUrl: only downloads the
        /// image, never touches Firestore. A null/empty url (no photo, or the member's doc
        /// hasn't been backfilled yet) leaves the initials showing.</summary>
        public static void ApplyFromUrl(VisualElement avatar, VisualElement initialsLabel, string avatarUrl)
        {
            if (avatar == null || string.IsNullOrEmpty(avatarUrl)) return;

            avatar.style.overflow = Overflow.Hidden;

            LoadUrl(avatarUrl, tex =>
            {
                if (tex == null) return;

                var image = new Image { image = tex, scaleMode = ScaleMode.ScaleAndCrop };
                image.pickingMode = PickingMode.Ignore;
                image.style.position = Position.Absolute;
                image.style.left = image.style.top = image.style.right = image.style.bottom = 0;
                avatar.Add(image);
                if (initialsLabel != null) initialsLabel.style.display = DisplayStyle.None;
            });
        }

        public static void LoadUrl(string url, Action<Texture2D> onLoaded)
        {
            if (UrlCache.TryGetValue(url, out var cached))
            {
                if (ReferenceEquals(cached, null) || cached) { onLoaded(cached); return; }
                UrlCache.Remove(url); // texture was unloaded (e.g. scene change) - refetch
            }

            if (UrlPending.TryGetValue(url, out var waiting)) { waiting.Add(onLoaded); return; }
            UrlPending[url] = new List<Action<Texture2D>> { onLoaded };

            var request = UnityWebRequestTexture.GetTexture(url);
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                Texture2D tex = null;
                if (request.result == UnityWebRequest.Result.Success)
                    tex = DownloadHandlerTexture.GetContent(request);
                //else
                    //Debug.LogWarning($"[StudentAvatarLoader] Could not load avatar '{url}': {request.error}");
                request.Dispose();

                UrlCache[url] = tex;
                if (UrlPending.TryGetValue(url, out var callbacks))
                {
                    UrlPending.Remove(url);
                    foreach (var cb in callbacks) cb(tex);
                }
            };
        }

        public static void Load(string studentId, Action<Texture2D> onLoaded)
        {
            if (Cache.TryGetValue(studentId, out var cached))
            {
                // A null entry means "no photo"; a non-null-but-destroyed texture (Unity can
                // unload it on a scene change) is stale, so refetch.
                if (ReferenceEquals(cached, null) || cached)
                {
                    onLoaded(cached);
                    return;
                }
                Cache.Remove(studentId);
            }

            // Same student requested again while the first request is still in flight: share it.
            if (Pending.TryGetValue(studentId, out var waiting))
            {
                waiting.Add(onLoaded);
                return;
            }

            Pending[studentId] = new List<Action<Texture2D>> { onLoaded };

            void Finish(Texture2D tex)
            {
                Cache[studentId] = tex;
                if (Pending.TryGetValue(studentId, out var callbacks))
                {
                    Pending.Remove(studentId);
                    foreach (var cb in callbacks) cb(tex);
                }
            }

            FetchAvatarUrl(studentId, url =>
            {
                if (string.IsNullOrEmpty(url)) { Finish(null); return; }

                var request = UnityWebRequestTexture.GetTexture(url);
                var op = request.SendWebRequest();
                op.completed += _ =>
                {
                    Texture2D tex = null;
                    if (request.result == UnityWebRequest.Result.Success)
                        tex = DownloadHandlerTexture.GetContent(request);
                    //else
                        //Debug.LogWarning($"[StudentAvatarLoader] Could not load avatar for '{studentId}': {request.error}");

                    request.Dispose();
                    Finish(tex);
                };
            });
        }
    }
}
