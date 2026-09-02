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

        // The merged, still-Firestore-sourced badge *definitions* (id/name/icon/points
        // required), keyed by BadgeId - separate from _lastBadges (which also bakes in
        // per-badge Earned, computed against the student's current points/badgesEarned).
        // Kept around so a plain points/badge change (OnStudentProfileChanged) can
        // recompute Earned/progress purely in memory instead of re-querying every
        // teacher's gamificationSettings doc again.
        private Dictionary<string, AdminGamificationService.BadgeEntry> _lastConfiguredBadges;

        // Keyed row refs for diffed rendering - avoids Clear()+rebuild on every repaint.
        private readonly Dictionary<string, BadgeCardRefs> _badgeCardsById = new();

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
                // Re-apply cached state - no network call. Teacher-configured badge
                // definitions (name/icon/points) rarely change and aren't worth a fresh
                // read on every open; a points/badgesEarned change is instead caught
                // live via OnStudentProfileChanged below and recomputed from
                // _lastConfiguredBadges without touching Firestore at all.
                //
                // QueryElements() above just re-queried _totalPointsLabel/_badgesEarnedLabel
                // (fresh Label instances if this screen's tree was rebuilt) - RenderBadges()
                // alone only repaints the badge-list, so the summary card needs its own
                // repaint here too or it shows blank/stale text on every re-open after the
                // first.
                int earnedCount = _lastBadges.Count(b => b.Earned);
                SetSummaryData(_lastTotalPoints, earnedCount, _lastBadges.Count);
                RenderBadges();
            }
            else
            {
                LoadData();
            }

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
                PlayerSessionManager.Instance.OnStudentProfileChanged += OnStudentProfileChanged;
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
            }

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }
        }

        /// <summary>A quiz submission can both add points and unlock a new badge - both
        /// are reflected here purely from the already-cached badge definitions, no
        /// Firestore read needed.</summary>
        private void OnStudentProfileChanged(PlayerSessionManager.StudentProfile student)
        {
            if (!_hasLoadedOnce || _lastConfiguredBadges == null) return;
            RenderMergedBadges(student, _lastConfiguredBadges);
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
            _lastConfiguredBadges = configuredBadges;

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
            if (_badgesEarnedLabel != null) _badgesEarnedLabel.text = $"{badgesEarned}";
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

        /// <summary>Element refs for one badge card, plus the (badge, currentPoints) pair
        /// last painted into it - so a repaint can skip a field that hasn't changed and
        /// skip rebuilding the optional footer row entirely when it wasn't touched.</summary>
        private class BadgeCardRefs
        {
            public VisualElement Card;
            public VisualElement IconBox;
            public VisualElement IconContent; // emoji label or lock icon, swapped whole when Earned flips
            public Label TitleLabel;
            public Label StatusLabel;
            public VisualElement ProgressFill;
            public VisualElement ProgressRow;
            public Label CompleteLabel;
            public VisualElement FooterRow;
            public Label FooterCountLabel;
            public Label FooterPercentLabel;
            public BadgeInfo LastBadge;
            public int LastCurrentPoints;
            public bool HasLastPaint;
        }

        /// <summary>Diffs _lastBadges against the cards already on screen (keyed by
        /// BadgeId) instead of clearing and rebuilding all of them - unlocking a single
        /// badge, or a points change that only moves one progress bar, now patches just
        /// that one card.</summary>
        private void RenderBadges()
        {
            if (_badgeList == null) return;

            var incomingIds = new HashSet<string>();

            for (int i = 0; i < _lastBadges.Count; i++)
            {
                var badge = _lastBadges[i];
                incomingIds.Add(badge.BadgeId);

                if (_badgeCardsById.TryGetValue(badge.BadgeId, out var refs))
                {
                    ApplyBadgeCardContent(refs, badge, _lastTotalPoints);
                }
                else
                {
                    refs = BuildBadgeCard(badge, _lastTotalPoints);
                    _badgeCardsById[badge.BadgeId] = refs;
                }

                // Keep list order in sync with _lastBadges - Insert() on an
                // already-parented element just moves it, so unaffected cards
                // elsewhere in the list aren't touched.
                if (_badgeList.IndexOf(refs.Card) != i)
                {
                    _badgeList.Insert(i, refs.Card);
                }
            }

            List<string> staleIds = null;
            foreach (var id in _badgeCardsById.Keys)
            {
                if (!incomingIds.Contains(id)) (staleIds ??= new List<string>()).Add(id);
            }
            if (staleIds != null)
            {
                foreach (var id in staleIds)
                {
                    _badgeCardsById[id].Card.RemoveFromHierarchy();
                    _badgeCardsById.Remove(id);
                }
            }
        }

        // ---------------- Card builder (built at runtime - badges are teacher-configured) ----------------

        private BadgeCardRefs BuildBadgeCard(BadgeInfo badge, int currentPoints)
        {
            var card = new VisualElement();
            card.AddToClassList("badge-card");

            var iconBox = new VisualElement();
            iconBox.AddToClassList("badge-icon-box");
            card.Add(iconBox);

            var info = new VisualElement();
            info.AddToClassList("badge-info");

            var titleLabel = new Label();
            titleLabel.AddToClassList("badge-title");
            info.Add(titleLabel);

            var statusLabel = new Label();
            statusLabel.AddToClassList("badge-status");
            info.Add(statusLabel);

            var progressRow = new VisualElement();
            progressRow.AddToClassList("badge-progress-row");

            var track = new VisualElement();
            track.AddToClassList("progress-track");

            var fill = new VisualElement();
            fill.AddToClassList("progress-fill");
            track.Add(fill);
            progressRow.Add(track);

            info.Add(progressRow);
            card.Add(info);

            var refs = new BadgeCardRefs
            {
                Card = card,
                IconBox = iconBox,
                TitleLabel = titleLabel,
                StatusLabel = statusLabel,
                ProgressFill = fill,
                ProgressRow = progressRow
            };

            ApplyBadgeCardContent(refs, badge, currentPoints);
            return refs;
        }

        /// <summary>Patches an existing card in place, touching only what changed since
        /// the last paint (or everything, on first paint).</summary>
        private void ApplyBadgeCardContent(BadgeCardRefs refs, BadgeInfo badge, int currentPoints)
        {
            var last = refs.LastBadge;
            bool isFirstPaint = !refs.HasLastPaint;
            bool earnedChanged = isFirstPaint || last.Earned != badge.Earned;

            float pct = badge.Earned
                ? 1f
                : (badge.PointsRequired > 0 ? Mathf.Clamp01((float)currentPoints / badge.PointsRequired) : 0f);
            float lastPct = last.Earned
                ? 1f
                : (last.PointsRequired > 0 ? Mathf.Clamp01((float)refs.LastCurrentPoints / last.PointsRequired) : 0f);

            if (earnedChanged)
            {
                refs.Card.EnableInClassList("badge-card-locked", !badge.Earned);

                refs.IconBox.EnableInClassList("badge-icon-gold", badge.Earned);
                refs.IconBox.EnableInClassList("badge-icon-locked", !badge.Earned);
                refs.IconContent?.RemoveFromHierarchy();

                if (badge.Earned)
                {
                    var emojiLabel = new Label(string.IsNullOrEmpty(badge.IconEmoji) ? "\U0001F3C6" : badge.IconEmoji);
                    emojiLabel.AddToClassList("badge-icon-emoji");
                    refs.IconBox.Add(emojiLabel);
                    refs.IconContent = emojiLabel;
                }
                else
                {
                    var lockIcon = new VisualElement();
                    lockIcon.AddToClassList("badge-lock-icon");
                    refs.IconBox.Add(lockIcon);
                    refs.IconContent = lockIcon;
                }

                refs.TitleLabel.EnableInClassList("badge-title-locked", !badge.Earned);

                refs.StatusLabel.EnableInClassList("badge-status-unlocked", badge.Earned);
                refs.StatusLabel.EnableInClassList("badge-status-locked", !badge.Earned);

                refs.ProgressFill.EnableInClassList("progress-fill-complete", badge.Earned);
                refs.ProgressFill.EnableInClassList("progress-fill-locked", !badge.Earned);

                if (badge.Earned && refs.CompleteLabel == null)
                {
                    refs.CompleteLabel = new Label("\u2713 Completed");
                    refs.CompleteLabel.AddToClassList("progress-label");
                    refs.CompleteLabel.AddToClassList("progress-label-complete");
                    refs.ProgressRow.Add(refs.CompleteLabel);
                }
                else if (!badge.Earned && refs.CompleteLabel != null)
                {
                    refs.CompleteLabel.RemoveFromHierarchy();
                    refs.CompleteLabel = null;
                }

                if (!badge.Earned && refs.FooterRow == null)
                {
                    refs.FooterRow = new VisualElement();
                    refs.FooterRow.AddToClassList("badge-progress-footer-row");

                    refs.FooterCountLabel = new Label();
                    refs.FooterCountLabel.AddToClassList("progress-footer-label");

                    refs.FooterPercentLabel = new Label();
                    refs.FooterPercentLabel.AddToClassList("progress-footer-label");
                    refs.FooterPercentLabel.AddToClassList("progress-footer-label-right");

                    refs.FooterRow.Add(refs.FooterCountLabel);
                    refs.FooterRow.Add(refs.FooterPercentLabel);
                    refs.ProgressRow.parent.Add(refs.FooterRow);
                }
                else if (badge.Earned && refs.FooterRow != null)
                {
                    refs.FooterRow.RemoveFromHierarchy();
                    refs.FooterRow = null;
                    refs.FooterCountLabel = null;
                    refs.FooterPercentLabel = null;
                }
            }

            if (isFirstPaint || last.Name != badge.Name) refs.TitleLabel.text = badge.Name;

            if (earnedChanged || last.PointsRequired != badge.PointsRequired)
            {
                refs.StatusLabel.text = badge.Earned ? "Unlocked!" : $"Earn {badge.PointsRequired:N0} points to unlock";
            }

            if (earnedChanged || !Mathf.Approximately(pct, lastPct))
            {
                refs.ProgressFill.style.width = new Length(pct * 100f, LengthUnit.Percent);
            }

            if (!badge.Earned && refs.FooterCountLabel != null &&
                (earnedChanged || last.PointsRequired != badge.PointsRequired || refs.LastCurrentPoints != currentPoints))
            {
                refs.FooterCountLabel.text = $"{Mathf.Min(currentPoints, badge.PointsRequired):N0} / {badge.PointsRequired:N0}";
                refs.FooterPercentLabel.text = $"{Mathf.RoundToInt(pct * 100f)}%";
            }

            refs.LastBadge = badge;
            refs.LastCurrentPoints = currentPoints;
            refs.HasLastPaint = true;
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