using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentProgress.uxml (the "Progress Tracker" screen). Attach to
    /// the same GameObject as UIManager (it uses RequireComponent(UIDocument) like
    /// the other screen controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the Weekly Activity / Performance tab toggle
    ///  - Applies the green->blue gradient to the header at runtime
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetProgressData() / SetWeeklyPoints() / SetCategoryProgress() so
    ///    gameplay/session code can push real values in instead of the mock data.
    ///
    /// NOTE: Add a call to UIManager for this screen, e.g.:
    ///   [SerializeField] private VisualTreeAsset studentProgressScreen;
    ///   private StudentProgressController _studentProgressController;
    ///   public void ShowStudentProgress() => ShowScreen(studentProgressScreen, _studentProgressController);
    /// and wire the "Progress" quick-action button on the dashboard to call it.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentProgressController : MonoBehaviour
    {
        [Header("Gradient colors (matches Classroom / Quiz Selection: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        [Header("Weekly chart max value (top of y-axis)")]
        [SerializeField] private float chartMaxValue = 220f;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;

        // Level progress
        private Label _currentLevelLabel;
        private Label _currentLevelTitleLabel;
        private Label _nextLevelLabel;
        private Label _nextLevelTitleLabel;
        private VisualElement _progressFill;
        private Label _pointsToNextLabel;

        // Level roadmap (all configured levels + points required)
        private VisualElement _levelRoadmapList;

        // Stats
        private Label _quizzesValueLabel;
        private Label _avgScoreValueLabel;
        private Label _pointsValueLabel;
        private Label _badgesValueLabel;

        // Tabs
        private Button _weeklyTabButton;
        private Button _performanceTabButton;
        private VisualElement _weeklyActivityPanel;
        private VisualElement _performancePanel;

        // Weekly chart bars (Mon..Sun)
        private readonly VisualElement[] _chartBars = new VisualElement[7];
        private static readonly string[] BarNames =
        {
            "bar-mon", "bar-tue", "bar-wed", "bar-thu", "bar-fri", "bar-sat", "bar-sun"
        };

        // Category rows
        private VisualElement _categorySkeletalFill;
        private Label _categorySkeletalPercent;
        private Label _categorySkeletalCount;

        private VisualElement _categoryMuscularFill;
        private Label _categoryMuscularPercent;
        private Label _categoryMuscularCount;

        private VisualElement _categoryNervousFill;
        private Label _categoryNervousPercent;
        private Label _categoryNervousCount;

        private VisualElement _categoryCardiovascularFill;
        private Label _categoryCardiovascularPercent;
        private Label _categoryCardiovascularCount;

        // Both PopulateLevelProgress() and PopulateProgressExtras() used to re-hit
        // Firestore on every single OnEnable, even though this data only ever changes
        // as a side effect of submitting a quiz (which already fires
        // PlayerSessionManager.OnStudentProfileChanged). _hasLoadedOnce gates the
        // network calls to "first open" + "an actual profile change", instead of
        // "every time this screen becomes visible".
        private bool _hasLoadedOnce;
        private readonly Dictionary<int, LevelRoadmapRowRefs> _roadmapRowsByLevel = new Dictionary<int, LevelRoadmapRowRefs>();

        private void OnEnable()
        {
            Debug.Log("[StudentProgressController] OnEnable called");

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
                Debug.LogError("[StudentProgressController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            ShowWeeklyTab();

            if (!_hasLoadedOnce)
            {
                PopulateLevelProgress();
                PopulateProgressExtras();
                _hasLoadedOnce = true;
            }
            // else: labels/roadmap already reflect the last-known values from when this
            // screen (or OnStudentProfileChanged) last populated them - nothing to redo
            // just because the student navigated back here.

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

        /// <summary>A quiz submission is the only thing that moves points/quizzesCompleted/
        /// badgesEarned, which is everything both PopulateLevelProgress() and
        /// PopulateProgressExtras() depend on - so this is the one signal that actually
        /// warrants a fresh read, instead of polling on every OnEnable.</summary>
        private void OnStudentProfileChanged(PlayerSessionManager.StudentProfile student)
        {
            PopulateLevelProgress();
            PopulateProgressExtras();
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _weeklyTabButton?.UnregisterCallback<ClickEvent>(OnWeeklyTabClicked);
            _performanceTabButton?.UnregisterCallback<ClickEvent>(OnPerformanceTabClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentProgressController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");

            _currentLevelLabel = _screenRoot.Q<Label>("current-level-label");
            _currentLevelTitleLabel = _screenRoot.Q<Label>("current-level-title-label");
            _nextLevelLabel = _screenRoot.Q<Label>("next-level-label");
            _nextLevelTitleLabel = _screenRoot.Q<Label>("next-level-title-label");
            _progressFill = _screenRoot.Q<VisualElement>("progress-fill");
            _pointsToNextLabel = _screenRoot.Q<Label>("points-to-next-label");

            _levelRoadmapList = _screenRoot.Q<VisualElement>("level-roadmap-list");

            _quizzesValueLabel = _screenRoot.Q<Label>("quizzes-value-label");
            _avgScoreValueLabel = _screenRoot.Q<Label>("avg-score-value-label");
            _pointsValueLabel = _screenRoot.Q<Label>("points-value-label");
            _badgesValueLabel = _screenRoot.Q<Label>("badges-value-label");

            _weeklyTabButton = _screenRoot.Q<Button>("weekly-tab-button");
            _performanceTabButton = _screenRoot.Q<Button>("performance-tab-button");
            _weeklyActivityPanel = _screenRoot.Q<VisualElement>("weekly-activity-panel");
            _performancePanel = _screenRoot.Q<VisualElement>("performance-panel");

            for (int i = 0; i < BarNames.Length; i++)
            {
                _chartBars[i] = _screenRoot.Q<VisualElement>(BarNames[i]);
            }

            _categorySkeletalFill = _screenRoot.Q<VisualElement>("category-skeletal-fill");
            _categorySkeletalPercent = _screenRoot.Q<Label>("category-skeletal-percent");
            _categorySkeletalCount = _screenRoot.Q<Label>("category-skeletal-count");

            _categoryMuscularFill = _screenRoot.Q<VisualElement>("category-muscular-fill");
            _categoryMuscularPercent = _screenRoot.Q<Label>("category-muscular-percent");
            _categoryMuscularCount = _screenRoot.Q<Label>("category-muscular-count");

            _categoryNervousFill = _screenRoot.Q<VisualElement>("category-nervous-fill");
            _categoryNervousPercent = _screenRoot.Q<Label>("category-nervous-percent");
            _categoryNervousCount = _screenRoot.Q<Label>("category-nervous-count");

            _categoryCardiovascularFill = _screenRoot.Q<VisualElement>("category-cardiovascular-fill");
            _categoryCardiovascularPercent = _screenRoot.Q<Label>("category-cardiovascular-percent");
            _categoryCardiovascularCount = _screenRoot.Q<Label>("category-cardiovascular-count");

            Debug.Log($"[StudentProgressController] Found back button: {_backButton != null}, tabs: {_weeklyTabButton != null}/{_performanceTabButton != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _weeklyTabButton?.RegisterCallback<ClickEvent>(OnWeeklyTabClicked);
            _performanceTabButton?.RegisterCallback<ClickEvent>(OnPerformanceTabClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Data loading ----------------

        /// <summary>Pulls the signed-in student's points from PlayerSessionManager and the
        /// level thresholds from AdminGamificationService, updates the Level Progress card
        /// (current/next level, progress bar, points-to-next) with real numbers, and rebuilds
        /// the Level Roadmap list below it so the student can see the points required for
        /// every level - not just the next one.
        ///
        /// Note: avg score, badges-earned count, the weekly chart and the category breakdown
        /// aren't wired up here - PlayerSessionManager.StudentProfile doesn't track those yet
        /// (no avgScorePercent/badgesEarned fields, no per-day or per-category rollups), so
        /// those parts of the screen are left as-is. Call SetWeeklyPoints()/SetCategoryProgress()
        /// /SetProgressData() directly once that data is available.</summary>
        private void PopulateLevelProgress()
        {
            var student = PlayerSessionManager.Instance?.CurrentStudent;
            if (student == null)
            {
                Debug.LogWarning("[StudentProgressController] No signed-in student found - " +
                    "leaving the Level Progress card and roadmap with placeholder data.");
                return;
            }

            if (AdminGamificationService.Instance == null)
            {
                Debug.LogWarning("[StudentProgressController] AdminGamificationService.Instance is null - " +
                    "can't compute level progress or build the roadmap.");
                return;
            }

            AdminGamificationService.Instance.FetchSettings(settings =>
            {
                var progress = AdminGamificationService.ComputeLevelProgress(settings, student.TotalPoints);

                if (_currentLevelLabel != null) _currentLevelLabel.text = $"Level {progress.level}";
                if (_currentLevelTitleLabel != null) _currentLevelTitleLabel.text = progress.title;
                if (_nextLevelLabel != null) _nextLevelLabel.text = $"Level {progress.nextLevel}";
                if (_nextLevelTitleLabel != null) _nextLevelTitleLabel.text = progress.nextTitle;
                if (_progressFill != null) _progressFill.style.width = new Length(Mathf.Clamp01(progress.progress01) * 100f, LengthUnit.Percent);
                if (_pointsToNextLabel != null)
                {
                    _pointsToNextLabel.text = progress.pointsToNext > 0
                        ? $"{progress.pointsToNext} points to next level"
                        : "You're at the top level!";
                }

                if (_quizzesValueLabel != null) _quizzesValueLabel.text = student.QuizzesCompleted.ToString();
                if (_pointsValueLabel != null) _pointsValueLabel.text = student.TotalPoints.ToString();

                BuildLevelRoadmap(settings.Levels, progress.level, student.TotalPoints);
            });
        }

        /// <summary>Fills in the parts PopulateLevelProgress() above leaves alone: average
        /// score, badges-earned count, the weekly bar chart and the per-category breakdown.
        /// All come from QuizService.FetchProgressData(), which already aggregates the
        /// student's quizAttempts for exactly this purpose.</summary>
        private void PopulateProgressExtras()
        {
            if (QuizService.Instance == null)
            {
                Debug.LogWarning("[StudentProgressController] QuizService.Instance is null - " +
                    "leaving avg score, badges, weekly chart and category breakdown as placeholders.");
                return;
            }

            QuizService.Instance.FetchProgressData((success, result) =>
            {
                if (!success || result == null) return;

                if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(result.AvgScorePercent)}%";
                if (_badgesValueLabel != null) _badgesValueLabel.text = result.BadgesEarnedCount.ToString();

                SetWeeklyPoints(result.WeeklyPoints);

                result.CategoryBreakdown.TryGetValue("skeletal", out var skeletal);
                result.CategoryBreakdown.TryGetValue("muscular", out var muscular);
                result.CategoryBreakdown.TryGetValue("nervous", out var nervous);
                result.CategoryBreakdown.TryGetValue("cardiovascular", out var cardiovascular);

                SetCategoryProgress(
                    skeletal.quizzes, skeletal.percent01,
                    muscular.quizzes, muscular.percent01,
                    nervous.quizzes, nervous.percent01,
                    cardiovascular.quizzes, cardiovascular.percent01);
            });
        }

        /// <summary>Element refs for one roadmap row, plus what was last painted into it,
        /// so a repaint (e.g. after a points change moves "Current" to the next level)
        /// only touches the rows whose status actually changed.</summary>
        private class LevelRoadmapRowRefs
        {
            public VisualElement Row;
            public Label TitleLabel;
            public Label PointsLabel;
            public Label StatusLabel;
            public AdminGamificationService.LevelEntry LastLevel;
            public int LastCurrentLevelNumber;
            public int LastTotalPoints;
            public bool HasLastPaint;
        }

        /// <summary>Diffs the roadmap against the rows already on screen (keyed by
        /// LevelNumber) instead of clearing and rebuilding every row on every points
        /// change - typically only the row that just became "Current" (and the one that
        /// stops being current) actually needs a repaint.</summary>
        private void BuildLevelRoadmap(List<AdminGamificationService.LevelEntry> levels, int currentLevelNumber, int totalPoints)
        {
            if (_levelRoadmapList == null) return;

            if (levels == null)
            {
                foreach (var refs in _roadmapRowsByLevel.Values) refs.Row.RemoveFromHierarchy();
                _roadmapRowsByLevel.Clear();
                return;
            }

            var ordered = new List<AdminGamificationService.LevelEntry>(levels);
            ordered.Sort((a, b) => a.PointsRequired.CompareTo(b.PointsRequired));

            var incomingLevels = new HashSet<int>();

            for (int i = 0; i < ordered.Count; i++)
            {
                var level = ordered[i];
                incomingLevels.Add(level.LevelNumber);

                if (_roadmapRowsByLevel.TryGetValue(level.LevelNumber, out var refs))
                {
                    ApplyLevelRoadmapRowContent(refs, level, currentLevelNumber, totalPoints);
                }
                else
                {
                    refs = BuildLevelRoadmapRow(level, currentLevelNumber, totalPoints);
                    _roadmapRowsByLevel[level.LevelNumber] = refs;
                }

                if (_levelRoadmapList.IndexOf(refs.Row) != i)
                {
                    _levelRoadmapList.Insert(i, refs.Row);
                }
            }

            List<int> staleLevels = null;
            foreach (var levelNumber in _roadmapRowsByLevel.Keys)
            {
                if (!incomingLevels.Contains(levelNumber)) (staleLevels ??= new List<int>()).Add(levelNumber);
            }
            if (staleLevels != null)
            {
                foreach (var levelNumber in staleLevels)
                {
                    _roadmapRowsByLevel[levelNumber].Row.RemoveFromHierarchy();
                    _roadmapRowsByLevel.Remove(levelNumber);
                }
            }
        }

        private LevelRoadmapRowRefs BuildLevelRoadmapRow(AdminGamificationService.LevelEntry level, int currentLevelNumber, int totalPoints)
        {
            var row = new VisualElement();
            row.AddToClassList("level-roadmap-item");

            var badge = new VisualElement();
            badge.AddToClassList("level-roadmap-badge");
            var badgeNumber = new Label(level.LevelNumber.ToString());
            badgeNumber.AddToClassList("level-roadmap-badge-number");
            badge.Add(badgeNumber);

            var textCol = new VisualElement();
            textCol.AddToClassList("level-roadmap-text-col");
            var titleLabel = new Label();
            titleLabel.AddToClassList("level-roadmap-title");
            var pointsLabel = new Label();
            pointsLabel.AddToClassList("level-roadmap-points");
            textCol.Add(titleLabel);
            textCol.Add(pointsLabel);

            var statusLabel = new Label();
            statusLabel.AddToClassList("level-roadmap-status");

            row.Add(badge);
            row.Add(textCol);
            row.Add(statusLabel);

            var refs = new LevelRoadmapRowRefs
            {
                Row = row,
                TitleLabel = titleLabel,
                PointsLabel = pointsLabel,
                StatusLabel = statusLabel
            };

            ApplyLevelRoadmapRowContent(refs, level, currentLevelNumber, totalPoints);
            return refs;
        }

        /// <summary>Patches an existing roadmap row in place, only touching what changed
        /// since the last paint (or everything, on first paint).</summary>
        private void ApplyLevelRoadmapRowContent(LevelRoadmapRowRefs refs, AdminGamificationService.LevelEntry level, int currentLevelNumber, int totalPoints)
        {
            bool isFirstPaint = !refs.HasLastPaint;
            var last = refs.LastLevel;

            bool isCurrent = level.LevelNumber == currentLevelNumber;
            bool isReached = !isCurrent && totalPoints >= level.PointsRequired;
            bool isLocked = !isCurrent && !isReached;

            bool wasCurrent = !isFirstPaint && last.LevelNumber == refs.LastCurrentLevelNumber;
            bool wasReached = !isFirstPaint && !wasCurrent && refs.LastTotalPoints >= last.PointsRequired;
            bool statusGroupChanged = isFirstPaint || isCurrent != wasCurrent || isReached != wasReached;

            if (statusGroupChanged)
            {
                refs.Row.EnableInClassList("level-roadmap-item-current", isCurrent);
                refs.Row.EnableInClassList("level-roadmap-item-completed", isReached);
                refs.Row.EnableInClassList("level-roadmap-item-locked", isLocked);
            }

            if (isFirstPaint || last.LevelNumber != level.LevelNumber || last.Title != level.Title)
            {
                refs.TitleLabel.text = string.IsNullOrEmpty(level.Title)
                    ? $"Level {level.LevelNumber}"
                    : $"Level {level.LevelNumber} - {level.Title}";
            }

            if (isFirstPaint || last.PointsRequired != level.PointsRequired)
            {
                refs.PointsLabel.text = $"{level.PointsRequired:N0} points required";
            }

            string statusText;
            if (isCurrent) statusText = "Current";
            else if (isReached) statusText = "\u2713 Reached";
            else statusText = $"{level.PointsRequired - totalPoints:N0} to unlock";

            if (statusGroupChanged || refs.StatusLabel.text != statusText)
            {
                refs.StatusLabel.text = statusText;
            }

            refs.LastLevel = level;
            refs.LastCurrentLevelNumber = currentLevelNumber;
            refs.LastTotalPoints = totalPoints;
            refs.HasLastPaint = true;
        }

        // ---------------- Public API ----------------

        /// <summary>Push real values into the level-progress card and the four stat cards.</summary>
        public void SetProgressData(
            int currentLevel,
            string currentLevelTitle,
            int nextLevel,
            string nextLevelTitle,
            float levelProgress01,
            int pointsToNextLevel,
            int quizzesCompleted,
            float avgScorePercent,
            int totalPoints,
            int badgesEarned)
        {
            if (_currentLevelLabel != null) _currentLevelLabel.text = $"Level {currentLevel}";
            if (_currentLevelTitleLabel != null) _currentLevelTitleLabel.text = currentLevelTitle;
            if (_nextLevelLabel != null) _nextLevelLabel.text = $"Level {nextLevel}";
            if (_nextLevelTitleLabel != null) _nextLevelTitleLabel.text = nextLevelTitle;
            if (_progressFill != null) _progressFill.style.width = new Length(Mathf.Clamp01(levelProgress01) * 100f, LengthUnit.Percent);
            if (_pointsToNextLabel != null) _pointsToNextLabel.text = $"{pointsToNextLevel} points to next level";

            if (_quizzesValueLabel != null) _quizzesValueLabel.text = quizzesCompleted.ToString();
            if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(avgScorePercent)}%";
            if (_pointsValueLabel != null) _pointsValueLabel.text = totalPoints.ToString();
            if (_badgesValueLabel != null) _badgesValueLabel.text = badgesEarned.ToString();
        }

        /// <summary>Push real daily point totals (Mon..Sun, 7 values) into the weekly bar chart.</summary>
        public void SetWeeklyPoints(float[] mondayToSundayPoints)
        {
            if (mondayToSundayPoints == null) return;

            for (int i = 0; i < _chartBars.Length && i < mondayToSundayPoints.Length; i++)
            {
                if (_chartBars[i] == null) continue;

                float pct = chartMaxValue > 0f
                    ? Mathf.Clamp01(mondayToSundayPoints[i] / chartMaxValue) * 100f
                    : 0f;

                _chartBars[i].style.height = new Length(pct, LengthUnit.Percent);
            }
        }

        /// <summary>Push real per-category quiz progress into the "Progress by Category" card.</summary>
        public void SetCategoryProgress(
            int skeletalQuizzes, float skeletalPercent01,
            int muscularQuizzes, float muscularPercent01,
            int nervousQuizzes, float nervousPercent01,
            int cardiovascularQuizzes, float cardiovascularPercent01)
        {
            ApplyCategory(_categorySkeletalFill, _categorySkeletalPercent, _categorySkeletalCount, skeletalQuizzes, skeletalPercent01);
            ApplyCategory(_categoryMuscularFill, _categoryMuscularPercent, _categoryMuscularCount, muscularQuizzes, muscularPercent01);
            ApplyCategory(_categoryNervousFill, _categoryNervousPercent, _categoryNervousCount, nervousQuizzes, nervousPercent01);
            ApplyCategory(_categoryCardiovascularFill, _categoryCardiovascularPercent, _categoryCardiovascularCount, cardiovascularQuizzes, cardiovascularPercent01);
        }

        private void ApplyCategory(VisualElement fill, Label percentLabel, Label countLabel, int quizzes, float percent01)
        {
            float pct = Mathf.Clamp01(percent01) * 100f;
            if (fill != null) fill.style.width = new Length(pct, LengthUnit.Percent);
            if (percentLabel != null) percentLabel.text = $"{Mathf.RoundToInt(pct)}%";
            if (countLabel != null) countLabel.text = $"{quizzes} quizzes";
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentProgressController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnWeeklyTabClicked(ClickEvent evt) => ShowWeeklyTab();

        private void OnPerformanceTabClicked(ClickEvent evt) => ShowPerformanceTab();

        private void ShowWeeklyTab()
        {
            _weeklyTabButton?.AddToClassList("tab-button-active");
            _performanceTabButton?.RemoveFromClassList("tab-button-active");

            _weeklyActivityPanel?.RemoveFromClassList("hidden");
            _performancePanel?.AddToClassList("hidden");
        }

        private void ShowPerformanceTab()
        {
            _performanceTabButton?.AddToClassList("tab-button-active");
            _weeklyTabButton?.RemoveFromClassList("tab-button-active");

            _performancePanel?.RemoveFromClassList("hidden");
            _weeklyActivityPanel?.AddToClassList("hidden");
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

        private void ApplyGradients()
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
                name = "ProgressHeaderGradientTexture"
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
