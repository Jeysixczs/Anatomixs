using System;
using System.Collections.Generic;
using System.Linq;
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
    ///  - Refreshes the Performance Panel (Anatomy Play Mode completion by system,
    ///    percentage only) and the Weekly Activity Panel (total points/day from Quiz +
    ///    Anatomy Play Mode) from real data - see RefreshProgressUI().
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

        // Category rows - Anatomy Play Mode systems only (Skeletal/Muscular/
        // Cardiovascular). The "-count" labels are kept queried (existing
        // UXML/USS is untouched - see the plan's section 12) but are hidden
        // at runtime: Anatomy Play Mode completion is reported as a
        // percentage only, never a "X quizzes"-style count.
        private VisualElement _categorySkeletalFill;
        private Label _categorySkeletalPercent;
        private Label _categorySkeletalCount;

        private VisualElement _categoryMuscularFill;
        private Label _categoryMuscularPercent;
        private Label _categoryMuscularCount;

        private VisualElement _categoryCardiovascularFill;
        private Label _categoryCardiovascularPercent;
        private Label _categoryCardiovascularCount;

        // Nervous isn't supported by Anatomy Play Mode - its whole row is
        // hidden rather than populated with invented data (see the plan's
        // section 1). Kept as a "category-nervous" element reference only
        // (no fill/percent/count needed since nothing is ever written into
        // it), so the row can be trivially re-enabled if Nervous is added
        // to Anatomy Play Mode later.
        private VisualElement _categoryNervousRow;

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

            // Always repopulate on open: PlayerSessionManager's students/{uid} listener
            // stays alive for the whole session regardless of whether this screen is
            // open, so CurrentStudent can already reflect a change (a quiz submission, a
            // teacher edit, another device) that happened while this screen was disabled
            // and therefore never heard OnStudentProfileChanged. Both calls are cheap
            // (one cached read + one lightweight Firestore fetch), so there's no real
            // cost to just always doing this on open - the same treatment
            // RefreshProgressUI() below already gets.
            PopulateLevelProgress();
            PopulateProgressExtras();

            // Anatomy Play Mode performance and the weekly points chart are
            // refreshed every time this screen opens (see the plan's
            // section 14: "screen opens" / "returns after Play Mode" are
            // both just this OnEnable firing again) - RefreshProgressUI()
            // itself decides what actually needs a Firebase/Firestore call
            // vs. a local-only read, so this never adds unconditional
            // network traffic beyond what already happened here before.
            RefreshProgressUI();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
                PlayerSessionManager.Instance.OnStudentProfileChanged += OnStudentProfileChanged;
            }

            if (AnatomyPlayModeSyncService.Instance != null)
            {
                AnatomyPlayModeSyncService.Instance.OnSyncCompleted -= OnAnatomySyncCompleted;
                AnatomyPlayModeSyncService.Instance.OnSyncCompleted += OnAnatomySyncCompleted;
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
            }

            if (AnatomyPlayModeSyncService.Instance != null)
            {
                AnatomyPlayModeSyncService.Instance.OnSyncCompleted -= OnAnatomySyncCompleted;
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
        /// warrants a fresh read, instead of polling on every OnEnable. New quiz points
        /// also affect the Weekly Activity Panel, so refresh that here too (see the
        /// plan's section 14: "new quiz/gamification points become available").</summary>
        private void OnStudentProfileChanged(PlayerSessionManager.StudentProfile student)
        {
            PopulateLevelProgress();
            PopulateProgressExtras();
            RefreshWeeklyActivity();
        }

        /// <summary>Fires once AnatomyPlayModeSyncService finishes uploading and/or
        /// downloading+merging progress - local completed structures may have just
        /// grown (progress synced down from another device) and pending Play Mode
        /// points may have just been confirmed, so both panels are refreshed (see
        /// the plan's section 14: "Firebase synchronization successfully updates
        /// progress").</summary>
        private void OnAnatomySyncCompleted()
        {
            RefreshAnatomyPerformance();
            RefreshWeeklyActivity();
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

            _categoryCardiovascularFill = _screenRoot.Q<VisualElement>("category-cardiovascular-fill");
            _categoryCardiovascularPercent = _screenRoot.Q<Label>("category-cardiovascular-percent");
            _categoryCardiovascularCount = _screenRoot.Q<Label>("category-cardiovascular-count");

            // Nervous isn't supported by Anatomy Play Mode - hide the whole
            // row (see the plan's section 1) rather than deleting it from
            // the UXML, so re-enabling it later is a one-line change.
            _categoryNervousRow = _screenRoot.Q<VisualElement>("category-nervous");
            _categoryNervousRow?.AddToClassList("hidden");

            // The Performance Panel shows a percentage only - never a
            // "X quizzes"/"X structures" count (see the plan's section 2/3).
            _categorySkeletalCount?.AddToClassList("hidden");
            _categoryMuscularCount?.AddToClassList("hidden");
            _categoryCardiovascularCount?.AddToClassList("hidden");

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
        /// Note: avg score and badges-earned count aren't wired up here - see
        /// PopulateProgressExtras(). The weekly chart and the category breakdown aren't
        /// wired up here either - see RefreshWeeklyActivity()/RefreshAnatomyPerformance(),
        /// called together from RefreshProgressUI().</summary>
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

        /// <summary>Fills in the two stat-card values PopulateLevelProgress() above
        /// leaves alone: average score and badges-earned count. Both come from
        /// QuizService.FetchProgressData(), which already aggregates the student's
        /// quizAttempts for exactly this purpose.
        ///
        /// The weekly chart and the Performance Panel are NOT populated here -
        /// FetchProgressData's WeeklyPoints only ever reflected quiz points, and its
        /// CategoryBreakdown was quiz-attempt counts, neither of which is what those
        /// two panels are supposed to show (see the plan's sections 1/6/11). See
        /// RefreshWeeklyActivity() and RefreshAnatomyPerformance() instead - both are
        /// called from RefreshProgressUI().</summary>
        private void PopulateProgressExtras()
        {
            if (QuizService.Instance == null)
            {
                Debug.LogWarning("[StudentProgressController] QuizService.Instance is null - " +
                    "leaving avg score and badges as placeholders.");
                return;
            }

            QuizService.Instance.FetchProgressData((success, result) =>
            {
                if (!success || result == null) return;

                if (_avgScoreValueLabel != null) _avgScoreValueLabel.text = $"{Mathf.RoundToInt(result.AvgScorePercent)}%";
                if (_badgesValueLabel != null) _badgesValueLabel.text = result.BadgesEarnedCount.ToString();
            });
        }

        // ==================================================================
        // ===== Performance Panel (Anatomy Play Mode only) ================
        // ==================================================================

        /// <summary>Refreshes both real-data panels this screen owns. Call on every
        /// OnEnable and whenever local Play Mode progress or a Firebase sync could
        /// have changed (see the plan's section 14) - RefreshAnatomyPerformance() is
        /// a pure local read (offline-safe, no Firebase call), RefreshWeeklyActivity()
        /// makes one Firestore query for the student's quiz points.</summary>
        private void RefreshProgressUI()
        {
            RefreshAnatomyPerformance();
            RefreshWeeklyActivity();
        }

        /// <summary>Populates the Performance Panel from the student's REAL Anatomy
        /// Play Mode progress - Skeletal/Muscular/Cardiovascular only (see the plan's
        /// section 1). Entirely local/offline: reads AnatomyPlayModeLocalStorage's
        /// completed keys and AnatomyScreenController's per-system selectable-structure
        /// keys, with no Firebase call and no dependency on the Anatomy screen being
        /// open. Firebase-synced progress is already reflected here too, since
        /// AnatomyPlayModeSyncService merges any downloaded records straight into the
        /// same local storage this reads from (see the plan's section 4/5).</summary>
        private void LoadAnatomyPlayModeProgress()
        {
            string studentId = PlayerSessionManager.Instance?.CurrentStudent?.Uid;
            if (string.IsNullOrEmpty(studentId))
            {
                Debug.LogWarning("[StudentProgressController] No signed-in student - leaving the Performance Panel as-is.");
                return;
            }

            if (AnatomyPlayModeLocalStorage.Instance == null)
            {
                Debug.LogWarning("[StudentProgressController] AnatomyPlayModeLocalStorage.Instance is null - can't read Anatomy Play Mode progress.");
                return;
            }

            if (AnatomyScreenController.Instance == null)
            {
                Debug.LogWarning("[StudentProgressController] AnatomyScreenController.Instance is null - can't determine actual structure totals per system.");
                return;
            }

            // Reload from disk every time (same pattern
            // AnatomyPlayModeController.LoadCompletedKeysFromLocalStorage uses) -
            // local storage is the authoritative record, and this picks up anything
            // merged down by a Firebase sync since the last read.
            AnatomyPlayModeLocalStorage.Instance.Load(studentId);
            var completedKeys = AnatomyPlayModeLocalStorage.Instance.GetCompletedKeys();

            float skeletalPercent = CalculatePerformanceBySystem(AnatomySystem.Skeletal, completedKeys, out int skeletalCompleted, out int skeletalTotal);
            float muscularPercent = CalculatePerformanceBySystem(AnatomySystem.Muscular, completedKeys, out int muscularCompleted, out int muscularTotal);
            float cardioPercent = CalculatePerformanceBySystem(AnatomySystem.Cardiovascular, completedKeys, out int cardioCompleted, out int cardioTotal);

            Debug.Log($"[StudentProgress] Completed Skeletal structures: {skeletalCompleted}");
            Debug.Log($"[StudentProgress] Total Skeletal structures: {skeletalTotal}");
            Debug.Log($"[StudentProgress] Skeletal progress: {skeletalPercent:0.00}%");

            Debug.Log($"[StudentProgress] Completed Muscular structures: {muscularCompleted}");
            Debug.Log($"[StudentProgress] Total Muscular structures: {muscularTotal}");
            Debug.Log($"[StudentProgress] Muscular progress: {muscularPercent:0.00}%");

            Debug.Log($"[StudentProgress] Completed Cardiovascular structures: {cardioCompleted}");
            Debug.Log($"[StudentProgress] Total Cardiovascular structures: {cardioTotal}");
            Debug.Log($"[StudentProgress] Cardiovascular progress: {cardioPercent:0.00}%");
            // Totals/completed counts are logged for debugging only - never shown in
            // the UI itself (see the plan's section 16).

            SetCategoryProgress(skeletalPercent, muscularPercent, cardioPercent);
        }

        private void RefreshAnatomyPerformance() => LoadAnatomyPlayModeProgress();

        /// <summary>completed / actual total x 100 for one anatomy system - the actual
        /// total comes from AnatomyScreenController.GetSelectableStructureKeys(system),
        /// the same matched-against-the-real-model source Anatomy Play Mode itself uses
        /// (see the plan's section 2), never a hardcoded or database-only count.</summary>
        private static float CalculatePerformanceBySystem(
            AnatomySystem system, HashSet<string> completedKeys, out int completed, out int total)
        {
            var structureKeys = AnatomyScreenController.Instance.GetSelectableStructureKeys(system);
            total = structureKeys.Count;

            completed = 0;
            foreach (var key in completedKeys)
            {
                if (structureKeys.Contains(key)) completed++;
            }

            return total > 0 ? (completed / (float)total) * 100f : 0f;
        }

        // ==================================================================
        // ===== Weekly Activity Panel (all point-earning activities) =====
        // ==================================================================

        /// <summary>Populates the Weekly Activity Panel with the student's REAL total
        /// points earned per day (Mon-Sun) from every existing point-earning activity -
        /// currently Quiz attempts (QuizService, the project's source of truth for quiz
        /// points) and Anatomy Play Mode (AnatomyPlayModeLocalStorage, offline-first, no
        /// Firebase dependency). Each activity is read from its own single existing
        /// source exactly once, so nothing here can double-count (see the plan's
        /// section 9).</summary>
        private void RefreshWeeklyActivity()
        {
            if (QuizService.Instance == null)
            {
                Debug.LogWarning("[StudentProgressController] QuizService.Instance is null - leaving the Weekly Activity Panel as-is.");
                return;
            }

            QuizService.Instance.FetchStudentAttemptPoints(quizPoints =>
            {
                var weekly = new float[7];
                AddPointsToWeek(weekly, quizPoints);

                string studentId = PlayerSessionManager.Instance?.CurrentStudent?.Uid;
                if (AnatomyPlayModeLocalStorage.Instance != null && !string.IsNullOrEmpty(studentId))
                {
                    AnatomyPlayModeLocalStorage.Instance.Load(studentId);
                    var anatomyPoints = AnatomyPlayModeLocalStorage.Instance.GetAllRecords()
                        .Where(r => r.correct)
                        .Select(r => ((float)(r.pointsEarned + r.streakBonus), ParseTimestampUtc(r.timestampUtc)));
                    AddPointsToWeek(weekly, anatomyPoints);
                }

                Debug.Log("[StudentProgress] Weekly points:\n" +
                    $"Mon={weekly[0]}\nTue={weekly[1]}\nWed={weekly[2]}\nThu={weekly[3]}\n" +
                    $"Fri={weekly[4]}\nSat={weekly[5]}\nSun={weekly[6]}");

                SetWeeklyPoints(weekly);
            });
        }

        /// <summary>Buckets (points, UTC timestamp) entries into `weekly` (index 0 =
        /// Monday .. 6 = Sunday) for the CURRENT week, converting each timestamp to the
        /// student's local time before deciding which calendar day - and therefore which
        /// Monday-Sunday week - it falls into (see the plan's section 8: a late-Sunday-UTC
        /// activity must not spill into Monday just because the stored timestamp is UTC).
        /// Entries outside the current local week are ignored, exactly like the existing
        /// quiz-only calculation this replaces.</summary>
        private static void AddPointsToWeek(float[] weekly, IEnumerable<(float points, DateTime completedAtUtc)> entries)
        {
            var nowLocal = DateTime.Now;
            int daysSinceMonday = ((int)nowLocal.DayOfWeek + 6) % 7; // Sunday=0 in DayOfWeek -> shift so Monday=0.
            var mondayLocal = nowLocal.Date.AddDays(-daysSinceMonday);
            var nextMondayLocal = mondayLocal.AddDays(7);

            foreach (var (points, completedAtUtc) in entries)
            {
                var utc = DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc);
                var local = utc.ToLocalTime();

                if (local < mondayLocal || local >= nextMondayLocal) continue;

                int dayIndex = ((int)local.DayOfWeek + 6) % 7;
                weekly[dayIndex] += points;
            }
        }

        /// <summary>Parses a PlayModeAnswerRecord.timestampUtc string (written via
        /// DateTime.UtcNow.ToString("o")) back into a UTC DateTime. Falls back to
        /// DateTime.UtcNow for a malformed/missing value rather than throwing, so one
        /// bad record can't break the whole weekly chart.</summary>
        private static DateTime ParseTimestampUtc(string isoUtc)
        {
            if (DateTime.TryParse(
                    isoUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            }

            Debug.LogWarning($"[StudentProgressController] Could not parse Play Mode timestamp '{isoUtc}' - using current time instead.");
            return DateTime.UtcNow;
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

        /// <summary>Push real Anatomy Play Mode completion percentages into the
        /// "Progress by Category" card - Skeletal/Muscular/Cardiovascular only (see the
        /// plan's section 1: Nervous isn't supported by Anatomy Play Mode, and its whole
        /// row is hidden rather than populated - see QueryElements). Percentage only,
        /// exactly two decimal places, never a count (see the plan's section 2/3).</summary>
        public void SetCategoryProgress(float skeletalPercent, float muscularPercent, float cardiovascularPercent)
        {
            ApplyCategory(_categorySkeletalFill, _categorySkeletalPercent, skeletalPercent);
            ApplyCategory(_categoryMuscularFill, _categoryMuscularPercent, muscularPercent);
            ApplyCategory(_categoryCardiovascularFill, _categoryCardiovascularPercent, cardiovascularPercent);
        }

        private static void ApplyCategory(VisualElement fill, Label percentLabel, float percent)
        {
            float clamped = Mathf.Clamp(percent, 0f, 100f);
            if (fill != null) fill.style.width = new Length(clamped, LengthUnit.Percent);
            if (percentLabel != null) percentLabel.text = $"{clamped:0.00}%";
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
