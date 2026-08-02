using System;
using System.Collections.Generic;
using System.Linq;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Reads/writes the `gamificationSettings/config` singleton doc. This is
    /// the "AdminGamificationService" referenced in
    /// AdminGamificationSettingsController.OnSaveChangesClicked(), and it's
    /// also read by QuizService when a student finishes a quiz (to work out
    /// their new level / any newly-earned badges).
    ///
    /// Schema note: FIRESTORE_SCHEMA.md described `levelThresholds` as a bare
    /// `array&lt;number&gt;` and `badgeDefinitions` as `{ name, description,
    /// targetPoints }`. AdminGamificationSettingsController's actual UI needs
    /// a title per level and an icon per badge (no description field), so the
    /// doc shape here is slightly richer than that first draft:
    ///
    /// gamificationSettings/config
    ///   pointsPerDifficulty: { easy: number, medium: number, hard: number }
    ///   levels: array&lt;{ level: number, title: string, pointsRequired: number }&gt;
    ///   badges: map&lt;badgeId, { name: string, icon: string, pointsRequired: number }&gt;
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
        private DocumentReference ConfigRef => Db.Collection("gamificationSettings").Document("config");

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Call when showing AdminGamificationSettingsController - feeds
        /// SetPointsConfiguration() / SetBadges() / SetLevels() directly.
        /// Returns built-in defaults (matching the controller's own mock data)
        /// if the doc doesn't exist yet, so the screen never renders empty.
        /// </summary>
        public void FetchSettings(Action<GamificationSettings> onComplete)
        {
            ConfigRef.GetSnapshotAsync().ContinueWithOnMainThread(task =>
            {
                if (task.IsCanceled || task.IsFaulted || !task.Result.Exists)
                {
                    CurrentSettings = DefaultSettings();
                    onComplete?.Invoke(CurrentSettings);
                    return;
                }

                CurrentSettings = ToSettings(task.Result);
                onComplete?.Invoke(CurrentSettings);
            });
        }

        /// <summary>Call from AdminGamificationSettingsController.OnSaveChangesClicked().</summary>
        public void SaveSettings(
            int easyPoints,
            int mediumPoints,
            int hardPoints,
            List<LevelEntry> levels,
            List<BadgeEntry> badges,
            Action<bool, string> onComplete)
        {
            var levelMaps = new List<object>();
            foreach (var level in levels ?? new List<LevelEntry>())
            {
                levelMaps.Add(new Dictionary<string, object>
                {
                    { "level", level.LevelNumber },
                    { "title", level.Title },
                    { "pointsRequired", level.PointsRequired }
                });
            }

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
                { "levels", levelMaps },
                { "badges", badgeMap }
            };

            ConfigRef.SetAsync(data, SetOptions.MergeAll).ContinueWithOnMainThread(task =>
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
                    Levels = levels ?? new List<LevelEntry>(),
                    Badges = badges ?? new List<BadgeEntry>()
                };

                onComplete?.Invoke(true, null);
            });
        }

        // ---------------- Helpers (also used by QuizService inside transactions) ----------------

        /// <summary>Decode a `gamificationSettings/config` snapshot fetched elsewhere
        /// (e.g. inside a QuizService transaction) without needing another round trip.</summary>
        public static GamificationSettings ToSettings(DocumentSnapshot snap)
        {
            var settings = new GamificationSettings();

            if (snap == null || !snap.Exists) return DefaultSettings();

            if (snap.TryGetValue<Dictionary<string, object>>("pointsPerDifficulty", out var pointsMap))
            {
                settings.EasyPoints = ToInt(pointsMap, "easy", 10);
                settings.MediumPoints = ToInt(pointsMap, "medium", 20);
                settings.HardPoints = ToInt(pointsMap, "hard", 30);
            }

            if (snap.TryGetValue<List<object>>("levels", out var levelList))
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

            if (snap.TryGetValue<Dictionary<string, object>>("badges", out var badgesMap))
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

            if (settings.Levels.Count == 0) settings.Levels = DefaultSettings().Levels;
            if (settings.Badges.Count == 0) settings.Badges = DefaultSettings().Badges;

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

        private static GamificationSettings DefaultSettings()
        {
            return new GamificationSettings
            {
                EasyPoints = 10,
                MediumPoints = 20,
                HardPoints = 30,
                Levels = new List<LevelEntry>
                {
                    new LevelEntry { LevelNumber = 1, Title = "Novice", PointsRequired = 0 },
                    new LevelEntry { LevelNumber = 2, Title = "Learner", PointsRequired = 100 },
                    new LevelEntry { LevelNumber = 3, Title = "Student", PointsRequired = 300 },
                    new LevelEntry { LevelNumber = 4, Title = "Scholar", PointsRequired = 600 },
                    new LevelEntry { LevelNumber = 5, Title = "Expert", PointsRequired = 1000 },
                    new LevelEntry { LevelNumber = 6, Title = "Master", PointsRequired = 1500 },
                    new LevelEntry { LevelNumber = 7, Title = "Guru", PointsRequired = 2100 },
                    new LevelEntry { LevelNumber = 8, Title = "Legend", PointsRequired = 2800 },
                },
                Badges = new List<BadgeEntry>
                {
                    new BadgeEntry { BadgeId = "beginner", Name = "Beginner", IconEmoji = "\U0001F31F", PointsRequired = 100 },
                    new BadgeEntry { BadgeId = "quiz-master", Name = "Quiz Master", IconEmoji = "\U0001F3C6", PointsRequired = 500 },
                    new BadgeEntry { BadgeId = "anatomist", Name = "Anatomist", IconEmoji = "\U0001F9E0", PointsRequired = 1000 },
                    new BadgeEntry { BadgeId = "expert", Name = "Expert", IconEmoji = "\U0001F451", PointsRequired = 2000 },
                }
            };
        }

        /// <summary>Given a total points value, returns (levelNumber, title, nextLevelNumber,
        /// nextTitle, progress01, pointsToNext) for StudentProgressController.SetProgressData().</summary>
        public static (int level, string title, int nextLevel, string nextTitle, float progress01, int pointsToNext)
            ComputeLevelProgress(GamificationSettings settings, int totalPoints)
        {
            var levels = (settings?.Levels != null && settings.Levels.Count > 0) ? settings.Levels : DefaultSettings().Levels;
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
