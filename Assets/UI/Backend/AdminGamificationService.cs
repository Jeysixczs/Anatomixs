using System;
using System.Collections.Generic;
using System.Linq;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Reads/writes gamification config under the `gamificationSettings`
    /// collection. This is the "AdminGamificationService" referenced in
    /// AdminGamificationSettingsController.OnSaveChangesClicked(), and it's
    /// also read by QuizService when a student finishes a quiz (to work out
    /// their new level / any newly-earned badges).
    ///
    /// Points-per-difficulty and badges are PER TEACHER - each signed-in
    /// admin has their own doc, so one teacher editing their badges/points
    /// never affects any other teacher's classrooms. Levels remain global
    /// and fixed across every teacher/classroom, in their own doc:
    ///
    /// gamificationSettings/{teacherId}   (teacherId = AdminAuthService.CurrentAdmin.Uid)
    ///   pointsPerDifficulty: { easy: number, medium: number, hard: number }
    ///   badges: map&lt;badgeId, { name: string, icon: string, pointsRequired: number }&gt;
    ///
    /// gamificationSettings/levels        (singleton - shared by everyone)
    ///   levels: array&lt;{ level: number, title: string, pointsRequired: number }&gt;
    ///
    /// SaveSettings() below only ever writes to the caller's own
    /// gamificationSettings/{teacherId} doc, and only ever writes
    /// pointsPerDifficulty + badges. The levels doc has intentionally no
    /// write path from the app - seed/change it directly in the Firebase
    /// console (or a one-off admin script) if the progression path ever
    /// needs to change, so every teacher/classroom always shares the exact
    /// same level thresholds.
    ///
    /// Schema note: FIRESTORE_SCHEMA.md described `levelThresholds` as a bare
    /// `array&lt;number&gt;` and `badgeDefinitions` as `{ name, description,
    /// targetPoints }` on one shared doc. AdminGamificationSettingsController's
    /// actual UI needs a title per level and an icon per badge (no
    /// description field), and badges/points needed to be per-teacher rather
    /// than shared, hence the split shown above.
    ///
    /// Attach to the same persistent GameObject as FirebaseBootstrap/UIManager.
    /// </summary>
    public class AdminGamificationService : MonoBehaviour
    {
        public static AdminGamificationService Instance { get; private set; }

        [Serializable]
        public class LevelEntry
        {
            public int LevelNumber;
            public string Title;
            public int PointsRequired;
        }

        [Serializable]
        public class BadgeEntry
        {
            public string BadgeId;
            public string Name;
            public string IconEmoji;
            public int PointsRequired;
        }

        [Serializable]
        public class GamificationSettings
        {
            public int EasyPoints = 10;
            public int MediumPoints = 20;
            public int HardPoints = 30;
            public List<LevelEntry> Levels = new List<LevelEntry>();
            public List<BadgeEntry> Badges = new List<BadgeEntry>();
        }

        /// <summary>Last-fetched settings. Populated by FetchSettings(); used as a
        /// synchronous fallback by QuizService if a fresh read isn't available.</summary>
        public GamificationSettings CurrentSettings { get; private set; }

        private FirebaseFirestore Db => FirebaseBootstrap.Instance.Db;

        /// <summary>Per-teacher points+badges doc. Public so QuizService can build the
        /// same reference (e.g. inside a transaction) without duplicating the path.</summary>
        public DocumentReference ConfigRefFor(string teacherId) => Db.Collection("gamificationSettings").Document(teacherId);

        /// <summary>Global, app-read-only levels doc shared by every teacher/classroom.</summary>
        public DocumentReference LevelsRef => Db.Collection("gamificationSettings").Document("levels");

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Call when showing AdminGamificationSettingsController - feeds
        /// SetPointsConfiguration() / SetBadges() / SetLevels() directly.
        /// Reads the signed-in admin's own gamificationSettings/{teacherId}
        /// doc plus the shared gamificationSettings/levels doc. Returns
        /// built-in defaults (matching the controller's own mock data) if
        /// this teacher hasn't saved anything yet, so the screen never
        /// renders empty.
        /// </summary>
        public void FetchSettings(Action<GamificationSettings> onComplete)
        {
            var admin = AdminAuthService.Instance?.CurrentAdmin;
            if (admin == null)
            {
                CurrentSettings = DefaultSettings();
                onComplete?.Invoke(CurrentSettings);
                return;
            }

            var configTask = ConfigRefFor(admin.Uid).GetSnapshotAsync();
            var levelsTask = LevelsRef.GetSnapshotAsync();

            System.Threading.Tasks.Task.WhenAll(configTask, levelsTask).ContinueWithOnMainThread(_ =>
            {
                var configSnap = configTask.IsFaulted ? null : configTask.Result;
                var levelsSnap = levelsTask.IsFaulted ? null : levelsTask.Result;

                CurrentSettings = ToSettings(configSnap, levelsSnap);
                onComplete?.Invoke(CurrentSettings);
            });
        }

        /// <summary>
        /// Call from QuizService when a student submits a quiz, to score them against
        /// the specific teacher's badges/points (resolved from the classroom's
        /// teacherId) plus the shared global levels. Unlike FetchSettings(), this
        /// doesn't touch CurrentSettings or require an admin to be signed in - it's
        /// used from the student side.
        /// </summary>
        public void FetchSettingsForTeacher(string teacherId, Action<GamificationSettings> onComplete)
        {
            if (string.IsNullOrEmpty(teacherId))
            {
                LevelsRef.GetSnapshotAsync().ContinueWithOnMainThread(task =>
                {
                    onComplete?.Invoke(ToSettings(null, task.IsFaulted ? null : task.Result));
                });
                return;
            }

            var configTask = ConfigRefFor(teacherId).GetSnapshotAsync();
            var levelsTask = LevelsRef.GetSnapshotAsync();

            System.Threading.Tasks.Task.WhenAll(configTask, levelsTask).ContinueWithOnMainThread(_ =>
            {
                var configSnap = configTask.IsFaulted ? null : configTask.Result;
                var levelsSnap = levelsTask.IsFaulted ? null : levelsTask.Result;
                onComplete?.Invoke(ToSettings(configSnap, levelsSnap));
            });
        }

        /// <summary>
        /// Call from AdminGamificationSettingsController.OnSaveChangesClicked().
        /// Writes only to the signed-in admin's own gamificationSettings/{teacherId}
        /// doc - never touches any other teacher's config. Intentionally has no
        /// `levels` parameter - see the class doc comment; levels live in a separate
        /// shared doc this method never writes to.
        /// </summary>
        public void SaveSettings(
            int easyPoints,
            int mediumPoints,
            int hardPoints,
            List<BadgeEntry> badges,
            Action<bool, string> onComplete)
        {
            var admin = AdminAuthService.Instance?.CurrentAdmin;
            if (admin == null) { onComplete?.Invoke(false, "Not signed in."); return; }

            var badgeMap = new Dictionary<string, object>();
            foreach (var badge in badges ?? new List<BadgeEntry>())
            {
                string id = string.IsNullOrEmpty(badge.BadgeId) ? MakeBadgeId(badge.Name) : badge.BadgeId;
                badgeMap[id] = new Dictionary<string, object>
                {
                    { "name", badge.Name },
                    { "icon", badge.IconEmoji },
                    { "pointsRequired", badge.PointsRequired }
                };
            }

            var data = new Dictionary<string, object>
            {
                { "pointsPerDifficulty", new Dictionary<string, object>
                    {
                        { "easy", easyPoints },
                        { "medium", mediumPoints },
                        { "hard", hardPoints }
                    }
                },
                { "badges", badgeMap }
            };

            ConfigRefFor(admin.Uid).SetAsync(data, SetOptions.MergeAll).ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    onComplete?.Invoke(false, "Could not save gamification settings.");
                    return;
                }

                CurrentSettings = new GamificationSettings
                {
                    EasyPoints = easyPoints,
                    MediumPoints = mediumPoints,
                    HardPoints = hardPoints,
                    // Levels live in the separate shared doc and are never written by
                    // this call - keep whatever was last fetched so callers reading
                    // CurrentSettings right after a save still see the correct
                    // (unchanged) global levels.
                    Levels = CurrentSettings?.Levels ?? DefaultLevels(),
                    Badges = badges ?? new List<BadgeEntry>()
                };

                onComplete?.Invoke(true, null);
            });
        }

        // ---------------- Helpers (also used by QuizService inside transactions) ----------------

        /// <summary>Decode a per-teacher gamificationSettings/{teacherId} snapshot
        /// (points + badges) together with the shared gamificationSettings/levels
        /// snapshot, fetched elsewhere (e.g. inside a QuizService transaction) without
        /// needing another round trip. Either snapshot may be null/nonexistent - e.g.
        /// a brand-new teacher with no config doc yet, or a caller (like
        /// QuizService.FetchProgressData) that only cares about levels and passes
        /// teacherConfigSnap as null.</summary>
        public static GamificationSettings ToSettings(DocumentSnapshot teacherConfigSnap, DocumentSnapshot levelsSnap)
        {
            var settings = new GamificationSettings();

            if (teacherConfigSnap != null && teacherConfigSnap.Exists)
            {
                if (teacherConfigSnap.TryGetValue<Dictionary<string, object>>("pointsPerDifficulty", out var pointsMap))
                {
                    settings.EasyPoints = ToInt(pointsMap, "easy", 10);
                    settings.MediumPoints = ToInt(pointsMap, "medium", 20);
                    settings.HardPoints = ToInt(pointsMap, "hard", 30);
                }

                if (teacherConfigSnap.TryGetValue<Dictionary<string, object>>("badges", out var badgesMap))
                {
                    foreach (var kvp in badgesMap)
                    {
                        if (kvp.Value is Dictionary<string, object> map)
                        {
                            settings.Badges.Add(new BadgeEntry
                            {
                                BadgeId = kvp.Key,
                                Name = map.TryGetValue("name", out var n) ? n.ToString() : kvp.Key,
                                IconEmoji = map.TryGetValue("icon", out var i) ? i.ToString() : "\U0001F3C6",
                                PointsRequired = ToInt(map, "pointsRequired", 0)
                            });
                        }
                    }
                }
            }

            if (levelsSnap != null && levelsSnap.Exists && levelsSnap.TryGetValue<List<object>>("levels", out var levelList))
            {
                foreach (var raw in levelList)
                {
                    if (raw is Dictionary<string, object> map)
                    {
                        settings.Levels.Add(new LevelEntry
                        {
                            LevelNumber = ToInt(map, "level", 1),
                            Title = map.TryGetValue("title", out var t) ? t.ToString() : "",
                            PointsRequired = ToInt(map, "pointsRequired", 0)
                        });
                    }
                }
                settings.Levels.Sort((a, b) => a.PointsRequired.CompareTo(b.PointsRequired));
            }

            if (settings.Levels.Count == 0) settings.Levels = DefaultLevels();
            if (settings.Badges.Count == 0) settings.Badges = DefaultPointsAndBadges().Badges;

            return settings;
        }

        private static int ToInt(Dictionary<string, object> map, string key, int fallback)
        {
            if (map != null && map.TryGetValue(key, out var value))
            {
                if (value is long l) return (int)l;
                if (value is int i) return i;
                if (int.TryParse(value.ToString(), out var parsed)) return parsed;
            }
            return fallback;
        }

        private static string MakeBadgeId(string name)
        {
            string slug = (name ?? "badge").ToLowerInvariant().Replace(" ", "-");
            return $"{slug}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }

        /// <summary>Built-in default level thresholds - used as a fallback wherever the
        /// shared gamificationSettings/levels doc is missing or empty.</summary>
        private static List<LevelEntry> DefaultLevels()
        {
            return new List<LevelEntry>
            {
                new LevelEntry { LevelNumber = 1, Title = "Novice", PointsRequired = 0 },
                new LevelEntry { LevelNumber = 2, Title = "Learner", PointsRequired = 100 },
                new LevelEntry { LevelNumber = 3, Title = "Student", PointsRequired = 300 },
                new LevelEntry { LevelNumber = 4, Title = "Scholar", PointsRequired = 600 },
                new LevelEntry { LevelNumber = 5, Title = "Expert", PointsRequired = 1000 },
                new LevelEntry { LevelNumber = 6, Title = "Master", PointsRequired = 1500 },
                new LevelEntry { LevelNumber = 7, Title = "Guru", PointsRequired = 2100 },
                new LevelEntry { LevelNumber = 8, Title = "Legend", PointsRequired = 2800 },
            };
        }

        /// <summary>Built-in default points-per-difficulty + badges - used as a fallback
        /// wherever a given teacher's gamificationSettings/{teacherId} doc is missing or
        /// empty (e.g. a brand-new teacher who hasn't opened the settings screen yet).</summary>
        private static GamificationSettings DefaultPointsAndBadges()
        {
            return new GamificationSettings
            {
                EasyPoints = 10,
                MediumPoints = 20,
                HardPoints = 30,
                Badges = new List<BadgeEntry>
                {
                    new BadgeEntry { BadgeId = "beginner", Name = "Beginner", IconEmoji = "\U0001F31F", PointsRequired = 100 },
                    new BadgeEntry { BadgeId = "quiz-master", Name = "Quiz Master", IconEmoji = "\U0001F3C6", PointsRequired = 500 },
                    new BadgeEntry { BadgeId = "anatomist", Name = "Anatomist", IconEmoji = "\U0001F9E0", PointsRequired = 1000 },
                    new BadgeEntry { BadgeId = "expert", Name = "Expert", IconEmoji = "\U0001F451", PointsRequired = 2000 },
                }
            };
        }

        /// <summary>Combined built-in defaults (points + badges + levels) - used when
        /// there's no admin signed in / nothing to look up at all, so the settings
        /// screen still has something sensible to render.</summary>
        private static GamificationSettings DefaultSettings()
        {
            var defaults = DefaultPointsAndBadges();
            defaults.Levels = DefaultLevels();
            return defaults;
        }

        /// <summary>Given a total points value, returns (levelNumber, title, nextLevelNumber,
        /// nextTitle, progress01, pointsToNext) for StudentProgressController.SetProgressData().</summary>
        public static (int level, string title, int nextLevel, string nextTitle, float progress01, int pointsToNext)
            ComputeLevelProgress(GamificationSettings settings, int totalPoints)
        {
            var levels = (settings?.Levels != null && settings.Levels.Count > 0) ? settings.Levels : DefaultLevels();
            var ordered = levels.OrderBy(l => l.PointsRequired).ToList();

            LevelEntry current = ordered[0];
            LevelEntry next = null;

            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].PointsRequired <= totalPoints)
                {
                    current = ordered[i];
                    next = (i + 1 < ordered.Count) ? ordered[i + 1] : null;
                }
                else
                {
                    next = ordered[i];
                    break;
                }
            }

            if (next == null)
            {
                // Already at (or past) the top level - show full progress against itself.
                return (current.LevelNumber, current.Title, current.LevelNumber, current.Title, 1f, 0);
            }

            int span = Mathf.Max(1, next.PointsRequired - current.PointsRequired);
            float progress = Mathf.Clamp01((totalPoints - current.PointsRequired) / (float)span);
            int pointsToNext = Mathf.Max(0, next.PointsRequired - totalPoints);

            return (current.LevelNumber, current.Title, next.LevelNumber, next.Title, progress, pointsToNext);
        }

        /// <summary>Badge ids the student has newly earned given their updated total points
        /// and the badges they already hold. Used by QuizService after a quiz attempt.</summary>
        public static List<string> ComputeNewlyEarnedBadges(GamificationSettings settings, int totalPoints, IEnumerable<string> alreadyEarned)
        {
            var owned = new HashSet<string>(alreadyEarned ?? Enumerable.Empty<string>());
            var earned = new List<string>();

            foreach (var badge in settings?.Badges ?? new List<BadgeEntry>())
            {
                if (!owned.Contains(badge.BadgeId) && totalPoints >= badge.PointsRequired)
                {
                    earned.Add(badge.BadgeId);
                }
            }

            return earned;
        }
    }
}