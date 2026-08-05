using System.Collections.Generic;
using System.Linq;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentAchievements.uxml. Attach to the same GameObject as
    /// UIManager (it uses RequireComponent(UIDocument) like the other screen
    /// controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button
    ///  - Applies the purple->pink gradient to the header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Loads this student's points + badge config and renders the badge
    ///    list (see LoadData()/LoadDataForTeacher()), replacing the old
    ///    hard-coded mock badges with real data from PlayerSessionManager +
    ///    AdminGamificationService.
    ///
    /// Badge definitions (AdminGamificationService.BadgeEntry) are configured
    /// PER TEACHER but apply across all of that teacher's classrooms. This
    /// screen has no single classroom in context, and a student can be
    /// enrolled under more than one teacher, so LoadData() looks up every
    /// classroom the student belongs to (ClassroomService.FetchMyClassrooms),
    /// collects the distinct teacher ids, and merges all of their badge
    /// configs together before matching against student.BadgesEarned. That
    /// way a badge earned under any of the student's teachers renders with
    /// its real name/icon/points, not just whichever teacher happened to be
    /// passed in. If the caller already knows a single classroom/teacher to
    /// scope to, LoadDataForTeacher(teacherId) is still available for that.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentAchievementsController : MonoBehaviour
    {
        [Header("Gradient colors (matches the other screens)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f); // purple
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);   // pink

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        private Label _totalPointsLabel;
        private Label _badgesEarnedLabel;
        private VisualElement _badgeList;

        /// <summary>One row in the main badge list.</summary>
        public struct BadgeInfo
        {
            public string BadgeId;
            public string Name;
            public string IconEmoji;
            public int PointsRequired;
            public bool Earned;

            public BadgeInfo(string badgeId, string name, string iconEmoji, int pointsRequired, bool earned)
            {
                BadgeId = badgeId;
                Name = name;
                IconEmoji = iconEmoji;
                PointsRequired = pointsRequired;
                Earned = earned;
            }
        }

        // Cached so a re-enable (screen rebuild) can redraw without re-fetching.
        private readonly List<BadgeInfo> _lastBadges = new();
        private int _lastTotalPoints;
        private List<string> _lastTeacherIds = new();
        private bool _hasLoadedOnce;

        private void OnEnable()
        {
            Debug.Log("[StudentAchievementsController] OnEnable called");

            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            // Get the root from UIManager's document (shared across all screens)
            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null)
                {
                    _root = uiDocument.rootVisualElement;
                }
            }

            // Fallback: use this component's own document
            if (_root == null && _document != null)
            {
                _root = _document.rootVisualElement;
            }

            if (_root == null)
            {
                Debug.LogError("[StudentAchievementsController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            if (_hasLoadedOnce)
            {
                // Re-apply cached state so the screen isn't briefly blank while a
                // fresh load is in flight (same pattern as the other controllers).
                RenderBadges();
                LoadBadgesForTeachers(_lastTeacherIds);
            }
            else
            {
                LoadData();
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentAchievementsController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _totalPointsLabel = _screenRoot.Q<Label>("total-points-label");
            _badgesEarnedLabel = _screenRoot.Q<Label>("badges-earned-label");
            _badgeList = _screenRoot.Q<VisualElement>("badge-list");

            Debug.Log($"[StudentAchievementsController] Found back button: {_backButton != null}, header: {_header != null}, badge-list: {_badgeList != null}");
        }

        private void WireCallbacks()
        {
            if (_backButton != null)
            {
                _backButton.RegisterCallback<ClickEvent>(OnBackClicked);
            }

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Loading ----------------

        /// <summary>Looks up every classroom this student belongs to
        /// (ClassroomService.FetchMyClassrooms), collects the distinct teacher
        /// ids, and loads/merges all of those teachers' badge configs - so a
        /// badge earned under any of the student's teachers shows its real
        /// name/icon/points instead of a generic fallback. Call this from
        /// OnEnable's default path, or whenever the caller has no single
        /// classroom to attribute the screen to.</summary>
        public void LoadData()
        {
            if (ClassroomService.Instance == null)
            {
                Debug.LogWarning("[StudentAchievementsController] ClassroomService not available - falling back to the default badge set.");
                LoadBadgesForTeachers(new List<string> { null });
                return;
            }

            ClassroomService.Instance.FetchMyClassrooms(classrooms =>
            {
                var teacherIds = (classrooms ?? new List<ClassroomService.ClassroomRecord>())
                    .Select(c => c.TeacherId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct()
                    .ToList();

                // No classrooms yet (or none with a resolvable teacher) - fall back
                // to the shared "no teacher" config so the screen still renders
                // something sensible instead of an empty list.
                if (teacherIds.Count == 0) teacherIds.Add(null);

                LoadBadgesForTeachers(teacherIds);
            });
        }

        /// <summary>Loads this student's points/earned badges plus the given teacher's
        /// configured badges (AdminGamificationService), then renders the badge list
        /// and summary card. Pass null for the app-wide default badge set. Prefer
        /// LoadData() when the student may belong to more than one teacher's
        /// classroom - this scopes to a single teacher only.</summary>
        public void LoadDataForTeacher(string teacherId) => LoadBadgesForTeachers(new List<string> { teacherId });

        /// <summary>Fetches AdminGamificationService settings for each given teacher id
        /// (null means the shared "no teacher" fallback config), merges their badge
        /// definitions into one set keyed by BadgeId, then matches
        /// student.BadgesEarned against the merge and renders the result.</summary>
        private void LoadBadgesForTeachers(List<string> teacherIds)
        {
            _lastTeacherIds = teacherIds ?? new List<string>();
            if (_lastTeacherIds.Count == 0) _lastTeacherIds.Add(null);

            var student = PlayerSessionManager.Instance?.CurrentStudent;
            if (student == null)
            {
                Debug.LogWarning("[StudentAchievementsController] No signed-in student - can't load achievements.");
                return;
            }

            if (AdminGamificationService.Instance == null)
            {
                Debug.LogWarning("[StudentAchievementsController] AdminGamificationService not available yet.");
                return;
            }

            // Badge ids are stable per-teacher, so a plain "last write wins" merge
            // by BadgeId is fine even if two teachers happen to reuse the same
            // built-in slug (e.g. "beginner") - they're the same default badge.
            var mergedBadges = new Dictionary<string, AdminGamificationService.BadgeEntry>();
            int pending = _lastTeacherIds.Count;

            foreach (var teacherId in _lastTeacherIds)
            {
                AdminGamificationService.Instance.FetchSettingsForTeacher(teacherId, settings =>
                {
                    foreach (var badge in settings?.Badges ?? new List<AdminGamificationService.BadgeEntry>())
                    {
                        if (!string.IsNullOrEmpty(badge.BadgeId)) mergedBadges[badge.BadgeId] = badge;
                    }

                    pending--;
                    if (pending == 0) RenderMergedBadges(student, mergedBadges);
                });
            }
        }

        private void RenderMergedBadges(PlayerSessionManager.StudentProfile student, Dictionary<string, AdminGamificationService.BadgeEntry> configuredBadges)
        {
            var earnedIds = new HashSet<string>(student.BadgesEarned ?? new List<string>());

            var badges = configuredBadges.Values
                .OrderBy(b => b.PointsRequired)
                .Select(b => new BadgeInfo(
                    b.BadgeId,
                    b.Name,
                    b.IconEmoji,
                    b.PointsRequired,
                    earnedIds.Contains(b.BadgeId) || student.TotalPoints >= b.PointsRequired))
                .ToList();

            // student.BadgesEarned can still contain ids that don't exist in any of
            // the merged configs - e.g. a badge that's since been renamed/removed
            // from its teacher's config. Show those too instead of silently
            // dropping them - otherwise "Badges Earned" in the summary card (and
            // the list itself) undercounts what's actually in Firestore.
            foreach (var id in earnedIds)
            {
                if (configuredBadges.ContainsKey(id)) continue;
                badges.Add(new BadgeInfo(id, HumanizeBadgeId(id), "\U0001F3C5", 0, true));
            }

            _hasLoadedOnce = true;
            _lastTotalPoints = student.TotalPoints;

            int earnedCount = badges.Count(b => b.Earned);
            SetSummaryData(student.TotalPoints, earnedCount, badges.Count);
            SetBadges(student.TotalPoints, badges);
        }

        /// <summary>Turns a badge id like "new-badge-37d426" (auto-generated by
        /// AdminGamificationService.MakeBadgeId when a custom badge has no explicit
        /// id) into a readable fallback title, e.g. "New Badge". Strips a trailing
        /// hex-looking suffix segment and title-cases the rest; falls back to the
        /// raw id if nothing recognizable is left. Only used as a last resort for
        /// earned badge ids that don't match any of the student's teachers'
        /// current configs.</summary>
        private static string HumanizeBadgeId(string badgeId)
        {
            if (string.IsNullOrEmpty(badgeId)) return "Badge";

            var parts = badgeId.Split('-').ToList();
            if (parts.Count > 1)
            {
                var last = parts[parts.Count - 1];
                bool looksLikeHexSuffix = last.Length is >= 4 and <= 8 &&
                    last.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
                if (looksLikeHexSuffix) parts.RemoveAt(parts.Count - 1);
            }

            if (parts.Count == 0) return badgeId;

            var title = string.Join(" ", parts.Select(p =>
                p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p.Substring(1)));

            return string.IsNullOrWhiteSpace(title) ? badgeId : title;
        }

        // ---------------- Public API ----------------

        /// <summary>Push real values into the summary card at the top of the header.</summary>
        public void SetSummaryData(int totalPoints, int badgesEarned, int badgesTotal)
        {
            if (_totalPointsLabel != null) _totalPointsLabel.text = totalPoints.ToString("N0");
            if (_badgesEarnedLabel != null) _badgesEarnedLabel.text = $"{badgesEarned} / {badgesTotal}";
        }

        /// <summary>Push the full badge list (any count, teacher-configured) into the
        /// badge-list container - unlocked badges show a completed progress bar,
        /// locked ones show a live progress bar/labels against currentPoints.</summary>
        public void SetBadges(int currentPoints, List<BadgeInfo> badges)
        {
            _lastTotalPoints = currentPoints;
            _lastBadges.Clear();
            if (badges != null) _lastBadges.AddRange(badges);

            RenderBadges();
        }

        private void RenderBadges()
        {
            if (_badgeList == null) return;

            _badgeList.Clear();
            foreach (var badge in _lastBadges)
            {
                _badgeList.Add(BuildBadgeCard(badge, _lastTotalPoints));
            }
        }

        // ---------------- Card builder (built at runtime - badges are teacher-configured) ----------------

        private VisualElement BuildBadgeCard(BadgeInfo badge, int currentPoints)
        {
            var card = new VisualElement();
            card.AddToClassList("badge-card");
            if (!badge.Earned) card.AddToClassList("badge-card-locked");

            var iconBox = new VisualElement();
            iconBox.AddToClassList("badge-icon-box");
            iconBox.AddToClassList(badge.Earned ? "badge-icon-gold" : "badge-icon-locked");

            if (badge.Earned)
            {
                var emojiLabel = new Label(string.IsNullOrEmpty(badge.IconEmoji) ? "\U0001F3C6" : badge.IconEmoji);
                emojiLabel.AddToClassList("badge-icon-emoji");
                iconBox.Add(emojiLabel);
            }
            else
            {
                var lockIcon = new VisualElement();
                lockIcon.AddToClassList("badge-lock-icon");
                iconBox.Add(lockIcon);
            }

            var info = new VisualElement();
            info.AddToClassList("badge-info");

            var titleLabel = new Label(badge.Name);
            titleLabel.AddToClassList("badge-title");
            if (!badge.Earned) titleLabel.AddToClassList("badge-title-locked");
            info.Add(titleLabel);

            var statusLabel = new Label(badge.Earned ? "Unlocked!" : $"Earn {badge.PointsRequired:N0} points to unlock");
            statusLabel.AddToClassList("badge-status");
            statusLabel.AddToClassList(badge.Earned ? "badge-status-unlocked" : "badge-status-locked");
            info.Add(statusLabel);

            var progressRow = new VisualElement();
            progressRow.AddToClassList("badge-progress-row");

            var track = new VisualElement();
            track.AddToClassList("progress-track");

            float pct = badge.Earned
                ? 1f
                : (badge.PointsRequired > 0 ? Mathf.Clamp01((float)currentPoints / badge.PointsRequired) : 0f);

            var fill = new VisualElement();
            fill.AddToClassList("progress-fill");
            fill.AddToClassList(badge.Earned ? "progress-fill-complete" : "progress-fill-locked");
            fill.style.width = new Length(pct * 100f, LengthUnit.Percent);
            track.Add(fill);
            progressRow.Add(track);

            if (badge.Earned)
            {
                var completeLabel = new Label("\u2713 Completed");
                completeLabel.AddToClassList("progress-label");
                completeLabel.AddToClassList("progress-label-complete");
                progressRow.Add(completeLabel);
            }

            info.Add(progressRow);

            if (!badge.Earned)
            {
                var footerRow = new VisualElement();
                footerRow.AddToClassList("badge-progress-footer-row");

                var countLabel = new Label($"{Mathf.Min(currentPoints, badge.PointsRequired):N0} / {badge.PointsRequired:N0}");
                countLabel.AddToClassList("progress-footer-label");

                var percentLabel = new Label($"{Mathf.RoundToInt(pct * 100f)}%");
                percentLabel.AddToClassList("progress-footer-label");
                percentLabel.AddToClassList("progress-footer-label-right");

                footerRow.Add(countLabel);
                footerRow.Add(percentLabel);
                info.Add(footerRow);
            }

            card.Add(iconBox);
            card.Add(info);
            return card;
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentAchievementsController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradient (USS has no linear-gradient) ----------------

        private void ApplyHeaderGradient()
        {
            if (_header == null) return;

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
            }

            _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
            _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AchievementsHeaderGradientTexture"
            };

            for (int i = 0; i < size; i++)
            {
                float t = i / (float)(size - 1);
                tex.SetPixel(i, 0, Color.Lerp(start, end, t));
            }

            tex.Apply();
            return tex;
        }
    }
}