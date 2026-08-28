using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Student Explore 3D -> Play Mode gameplay: the anatomy guessing game.
    ///
    /// This script owns ONLY Play Mode state (current question, hints,
    /// points, streak, completedKeys, progress, Isolate Answered). It never
    /// duplicates selection, outline, camera-focus, or hide/restore logic -
    /// all of that stays in AnatomyScreenController and is reused through
    /// the small public API at the bottom of that class:
    ///   - OnStructureSelected (event)
    ///   - AllBoneData / TryGetBoneDatabaseEntry
    ///   - SetInfoPanelTitle / SetInfoPanelDescription
    ///   - ExitExploreOnlyModes
    ///   - SetIsolateAnsweredActive
    ///
    /// BottomToolbar in Play Mode shows all Explore Mode buttons plus
    /// Answered (Isolate Answered), Unanswered (Isolate Unanswered), and
    /// Random (jump to a random unanswered structure). Isolate Selected
    /// Bone/Hide/Undo are never hidden or disabled just because Play Mode
    /// is active - see the plan's section 12/14: Isolate Selected Bone
    /// must keep working, completely independently of Isolate
    /// Answered/Unanswered, in both modes.
    ///
    /// Attach to the SAME GameObject as AnatomyScreenController (the one
    /// with the UIDocument) so it can query the same UXML tree for the
    /// Info Panel guessing UI. Play Mode is switched on only from Student
    /// Explore 3D's Play Mode picker (see RequestPlayModeOnOpen /
    /// UIManager.ShowStudentAnatomyScreen) - there is no Play Mode toggle
    /// button on the Anatomy Screen itself.
    ///
    /// Identity rules (do not deviate):
    ///   - BoneInfo.boneName / BoneDatabaseEntry.boneId = unique identifier
    ///   - BoneDatabaseEntry.displayName                = the guessing answer
    ///   - BoneDatabaseEntry.baseName                    = NEVER used here
    /// </summary>
    [RequireComponent(typeof(AnatomyScreenController))]
    public class AnatomyPlayModeController : MonoBehaviour
    {
        [Header("Points")]
        [Tooltip("Points awarded for a correct answer with 0 hints used.")]
        [SerializeField] private int pointsNoHints = 100;
        [Tooltip("Points awarded for a correct answer with exactly 1 hint used.")]
        [SerializeField] private int points1Hint = 60;
        [Tooltip("Points awarded for a correct answer with exactly 2 hints used.")]
        [SerializeField] private int points2Hints = 30;
        [Tooltip("Points awarded for a correct answer with 3+ hints used (the minimum).")]
        [SerializeField] private int points3PlusHints = 10;

        [Header("Streak Bonuses")]
        [Tooltip("Streak count -> bonus points awarded on top of the question's points " +
                 "the moment the streak reaches that count. Suggested: 3->25, 5->50, 10->100.")]
        [SerializeField]
        private List<StreakBonus> streakBonuses = new List<StreakBonus>
        {
            new StreakBonus { streakCount = 3, bonusPoints = 25 },
            new StreakBonus { streakCount = 5, bonusPoints = 50 },
            new StreakBonus { streakCount = 10, bonusPoints = 100 },
        };

        [Serializable]
        public class StreakBonus
        {
            public int streakCount;
            public int bonusPoints;
        }

        [Header("Firebase")]
        [Tooltip("Optional. If assigned, incorrect attempts are logged via this script directly, " +
                 "and it's used as a fallback for correct answers when no LocalStorage is assigned. " +
                 "Leave unassigned to run Play Mode without any Firebase saving.")]
        [SerializeField] private AnatomyPlayModeFirebase firebase;

        [Header("Offline Storage & Sync")]
        [Tooltip("Required for offline play. Every correct answer is saved here immediately, " +
                 "before any Firebase call is attempted - this is what makes Play Mode work " +
                 "with no internet connection, and what restores completed structures the next " +
                 "time Play Mode opens. Leave unassigned to fall back to saving straight to " +
                 "Firebase (no offline support, no restore across sessions).")]
        [SerializeField] private AnatomyPlayModeLocalStorage localStorage;

        [Tooltip("Optional. Automatically retries syncing pending local answers to Firebase " +
                 "once the device is back online. Leave unassigned to skip auto-retry - answers " +
                 "still save locally and sync immediately whenever they're first answered while online.")]
        [SerializeField] private AnatomyPlayModeSyncService syncService;

        private AnatomyScreenController _screen;
        private VisualElement _root;

        // ===== Play Mode session data (see the plan's section 19) =====
        private readonly HashSet<string> _completedKeys = new HashSet<string>();
        private AnatomyScreenController.BoneInfo _currentQuestion;
        private int _currentHints;
        private int _currentStreak;
        private int _highestStreak;
        private int _totalPoints;
        private int _correctAnswers;
        private int _incorrectAnswers;
        private int _hintsUsedTotal;
        private bool _isolateAnsweredEnabled;
        private bool _isolateUnansweredEnabled;
        private bool _isPlayModeActive;

        // Set by RequestPlayModeOnOpen (called from UIManager when the
        // student launched this system from Student Explore 3D's Play Mode
        // picker - the only way Play Mode ever starts). WireUi runs one
        // frame after OnEnable, so the request may arrive before this
        // screen's UI is wired - it's consumed as soon as WireUi finishes.
        private bool _pendingAutoStart;

        // True once WireUi has actually queried this visit's UI elements.
        // RequestPlayModeOnOpen can fire before that happens (it's called
        // the instant ShowScreen finishes, but WireUi is deliberately
        // scheduled a frame later - see StartCoroutineDeferredWire), so
        // TryConsumePendingAutoStart must not call ActivatePlayMode until
        // this is true, or it ends up running against still-null UI
        // references that never get touched again once _pendingAutoStart
        // is consumed.
        private bool _uiWired;

        // Which characters of the current question's DisplayName have been
        // revealed by a hint, keyed by character index. Never contains an
        // index that is whitespace in the DisplayName - spaces are always
        // shown, never counted as a hintable position.
        private readonly HashSet<int> _revealedIndices = new HashSet<int>();

        // The signed-in student's uid, the same identity AnatomyPlayModeFirebase
        // already reads from PlayerSessionManager - never a locally-invented id,
        // so local storage and Firebase records for the same student always line up.
        private static string CurrentStudentId =>
            PlayerSessionManager.Instance != null && PlayerSessionManager.Instance.CurrentStudent != null
                ? PlayerSessionManager.Instance.CurrentStudent.Uid
                : null;

        public bool IsPlayModeActive => _isPlayModeActive;
        public int TotalPoints => _totalPoints;
        public int CurrentStreak => _currentStreak;
        public int HighestStreak => _highestStreak;
        public IReadOnlyCollection<string> CompletedKeys => _completedKeys;

        // ===== UI refs (queried from the same root AnatomyScreenController uses) =====
        private VisualElement _questionControlsSection;
        private Button _isolateAnsweredButton;
        private Button _unansweredButton;
        private Button _randomButton;
        private Label _progressLabel;
        private Button _hintButton;
        private VisualElement _letterRow;
        private Button _submitButton;
        private VisualElement _completionPanel;
        private Label _completionLabel;
        private Button _completionCloseButton;
        private VisualElement _playModeControlsRow;
        private VisualElement _audioRow;

        // One TextField per non-space character of the current question's
        // DisplayName, in left-to-right order, built fresh by
        // BuildLetterBoxes for every question. _letterFieldSourceIndex[i]
        // is the DisplayName index that _letterFields[i] represents, so a
        // hint (which reveals by DisplayName index) can find the matching
        // box. Spaces never get a TextField - see BuildLetterBoxes.
        private readonly List<TextField> _letterFields = new List<TextField>();
        private readonly List<int> _letterFieldSourceIndex = new List<int>();

        private void OnEnable()
        {
            _screen = GetComponent<AnatomyScreenController>();

            // IMPORTANT: this OnEnable can only fire once for the lifetime of
            // this persistent GameObject - UIManager toggles
            // AnatomyScreenController's `enabled` flag every time it (re)shows
            // this screen (that's what re-triggers AnatomyScreenController's
            // own OnEnable each visit), but it never touches THIS component's
            // `enabled` flag. So this method body must not be the only place
            // UI wiring happens, or the letter boxes/hint/submit buttons
            // (and everything else here) would silently stop working after
            // the very first screen visit - they'd still point at a
            // VisualElement tree that ShowScreen already destroyed.
            // Instead, subscribe to AnatomyScreenController's OnScreenReady,
            // which DOES fire on every visit, and do all wiring from that
            // handler.
            _screen.OnStructureSelected += OnStructureSelected;
            _screen.OnScreenReady -= HandleScreenReady;
            _screen.OnScreenReady += HandleScreenReady;

            // Sync Progress can complete while this screen is sitting open
            // in Play Mode (a background auto-sync, or the student
            // switching back after tapping Sync on Student Explore 3D) -
            // subscribe so _completedKeys picks up anything newly merged
            // down from Firebase without requiring a screen reopen (see the
            // plan's section 7 and RefreshCompletedKeys below).
            if (syncService != null)
            {
                syncService.OnSyncCompleted -= HandleSyncCompleted;
                syncService.OnSyncCompleted += HandleSyncCompleted;
            }

            // Also cover the screen instance that's already active right now
            // (the one that caused this OnEnable to run in the first place).
            HandleScreenReady();
        }

        private void OnDisable()
        {
            if (_screen != null)
            {
                _screen.OnStructureSelected -= OnStructureSelected;
                _screen.OnScreenReady -= HandleScreenReady;
            }

            if (syncService != null)
                syncService.OnSyncCompleted -= HandleSyncCompleted;

            if (_isolateAnsweredButton != null) _isolateAnsweredButton.clicked -= OnIsolateAnsweredClicked;
            if (_unansweredButton != null) _unansweredButton.clicked -= OnUnansweredClicked;
            if (_randomButton != null) _randomButton.clicked -= OnRandomClicked;
            if (_hintButton != null) _hintButton.clicked -= OnHintClicked;
            if (_submitButton != null) _submitButton.clicked -= OnSubmitClicked;
            if (_completionCloseButton != null) _completionCloseButton.clicked -= HideCompletionPanel;

            // Leaving the screen entirely - don't leave Play Mode's
            // Isolate Answered visibility rule stuck active underneath
            // Explore Mode next time this screen opens.
            if (_isPlayModeActive)
                DeactivatePlayMode();

            _pendingAutoStart = false;
        }

        // Re-wires this script's UI against whatever UXML tree
        // AnatomyScreenController just finished building - called every time
        // OnScreenReady fires (i.e. every time the Anatomy Screen opens),
        // not just once. See the OnEnable comment above for why that
        // distinction matters.
        private void HandleScreenReady()
        {
            // A fresh UXML tree just replaced the previous one (or this is
            // the very first time the screen opened). Any "Play Mode is
            // currently active" state from a prior visit describes a UI
            // tree that's already gone - reset the active-state flags here
            // (this is the reliable per-visit hook; OnDisable is not, see
            // the OnEnable comment above) without touching persistent
            // progress like _totalPoints/_completedKeys, which is meant to
            // carry across toggling Play Mode on and off.
            _isPlayModeActive = false;
            _isolateAnsweredEnabled = false;
            _isolateUnansweredEnabled = false;
            _currentQuestion = null;
            _uiWired = false;

            StartCoroutineDeferredWire();
        }

        // UI Toolkit elements aren't guaranteed queryable the exact same
        // frame OnEnable runs on every subscriber - schedule one frame out,
        // same pattern AnatomyScreenController itself uses elsewhere
        // (see ClampPanelToScreen / UpdateScrollHint scheduling).
        private void StartCoroutineDeferredWire()
        {
            var uiDocument = GetComponent<UnityEngine.UIElements.UIDocument>();
            if (uiDocument == null) return;
            _root = uiDocument.rootVisualElement;
            if (_root == null) return;

            _root.schedule.Execute(WireUi).ExecuteLater(0);
        }

        private void WireUi()
        {
            _questionControlsSection = _root.Q<VisualElement>("QuestionControlsSection");
            _isolateAnsweredButton = _root.Q<Button>("IsolateAnsweredButton");
            _unansweredButton = _root.Q<Button>("UnansweredButton");
            _randomButton = _root.Q<Button>("RandomButton");
            _progressLabel = _root.Q<Label>("PlayModeProgressLabel");
            _hintButton = _root.Q<Button>("PlayModeHintButton");
            _letterRow = _root.Q<VisualElement>("PlayModeLetterRow");
            _submitButton = _root.Q<Button>("PlayModeSubmitButton");
            _completionPanel = _root.Q<VisualElement>("PlayModeCompletionPanel");
            _completionLabel = _root.Q<Label>("PlayModeCompletionLabel");
            _completionCloseButton = _root.Q<Button>("PlayModeCompletionCloseButton");
            _playModeControlsRow = _root.Q<VisualElement>("PlayModeControls");
            _audioRow = _root.Q<VisualElement>("AudioRow");

            if (_letterRow == null)
            {
                Debug.LogWarning("[AnatomyPlayModeController] 'PlayModeLetterRow' not found in UXML - " +
                                  "Play Mode's letter-box guessing UI cannot be built. See AnatomyScreen.uxml.");
            }

            // Hidden as a whole (label included) until Play Mode is
            // switched on - see ActivatePlayMode/DeactivatePlayMode. The
            // three buttons inside it are queried/wired below regardless
            // of this section's current visibility.
            if (_questionControlsSection != null)
                _questionControlsSection.style.display = DisplayStyle.None;

            if (_isolateAnsweredButton != null)
            {
                _isolateAnsweredButton.clicked -= OnIsolateAnsweredClicked;
                _isolateAnsweredButton.clicked += OnIsolateAnsweredClicked;
            }

            if (_unansweredButton != null)
            {
                _unansweredButton.clicked -= OnUnansweredClicked;
                _unansweredButton.clicked += OnUnansweredClicked;
            }

            if (_randomButton != null)
            {
                _randomButton.clicked -= OnRandomClicked;
                _randomButton.clicked += OnRandomClicked;
            }

            if (_hintButton != null)
            {
                _hintButton.clicked -= OnHintClicked;
                _hintButton.clicked += OnHintClicked;
            }

            if (_submitButton != null)
            {
                _submitButton.clicked -= OnSubmitClicked;
                _submitButton.clicked += OnSubmitClicked;
            }

            if (_completionPanel != null)
                _completionPanel.AddToClassList("hidden");

            if (_completionCloseButton != null)
            {
                _completionCloseButton.clicked -= HideCompletionPanel;
                _completionCloseButton.clicked += HideCompletionPanel;
            }

            _uiWired = true;
            TryConsumePendingAutoStart();
        }

        // ===== Play Mode on/off =====

        /// <summary>Called by UIManager.ShowStudentAnatomyScreen right after this
        /// screen was opened from Student Explore 3D's Play Mode picker - the
        /// only place Play Mode is ever started from. Safe to call before
        /// WireUi has run (it always is - WireUi is scheduled a frame after
        /// ShowScreen completes): the request is queued in _pendingAutoStart
        /// and only actually applied once _uiWired confirms WireUi has
        /// queried this visit's real UI elements.</summary>
        public void RequestPlayModeOnOpen()
        {
            _pendingAutoStart = true;
            TryConsumePendingAutoStart();
        }

        private void TryConsumePendingAutoStart()
        {
            if (!_pendingAutoStart || !_uiWired || _isPlayModeActive) return;
            _pendingAutoStart = false;
            ActivatePlayMode();
        }

        private void ActivatePlayMode()
        {
            _isPlayModeActive = true;

            // Restore this student's completed structures from local
            // storage before anything else below can generate or allow a
            // question - see the plan's section 5 ("Populate
            // _completedKeys before generating or allowing new questions").
            // This works with no internet connection, since local storage
            // never touches Firebase.
            LoadCompletedKeysFromLocalStorage();

            // Play Mode's own Isolate Answered must never fight Explore
            // Mode's Isolate Selected Bone / Hide - turn those off first so
            // Play Mode starts from a clean visibility state. Their
            // buttons stay visible and fully usable though: the
            // BottomToolbar in Play Mode shows the Anatomy Controls
            // section (Reset, Isolate, Hide, Undo) plus a second Question
            // Controls section (Answered, Unanswered, Random) - not a
            // reduced set.
            _screen.ExitExploreOnlyModes();

            if (_questionControlsSection != null)
                _questionControlsSection.style.display = DisplayStyle.Flex;

            // Searching by name would let a player jump straight to the
            // answer - Explore Mode's only, so it's hidden for the
            // duration of Play Mode (see ActivatePlayMode).
            _screen.SetSearchBarVisible(false);

            // The audio guide (a normal-Explore-Mode feature tied to the
            // real bone description) and Play Mode's hint/answer/submit row
            // occupy the same slot in the Info Panel - never show both.
            _playModeControlsRow?.RemoveFromClassList("hidden");
            _audioRow?.AddToClassList("hidden");

            // Play Mode opens the Info Panel a bit higher (top: 50%) than
            // Explore Mode's default (top: 58%) - see SetInfoPanelPlayModeStartPosition.
            _screen.SetInfoPanelPlayModeStartPosition(true);

            UpdateProgressLabel();
        }

        private void DeactivatePlayMode()
        {
            _isPlayModeActive = false;

            if (_isolateAnsweredEnabled)
                SetIsolateAnswered(false);

            if (_isolateUnansweredEnabled)
                SetIsolateUnanswered(false);

            if (_questionControlsSection != null)
                _questionControlsSection.style.display = DisplayStyle.None;

            // Back to Explore Mode - search is fair game again.
            _screen.SetSearchBarVisible(true);

            _playModeControlsRow?.AddToClassList("hidden");
            _audioRow?.RemoveFromClassList("hidden");

            // Back to Explore Mode's own default Info Panel position.
            _screen.SetInfoPanelPlayModeStartPosition(false);

            _currentQuestion = null;
            HideCompletionPanel();
        }

        // Populates _completedKeys from AnatomyPlayModeLocalStorage - the
        // same data that's used to restore "Already Answered" state and to
        // stop the Random button from ever picking a completed structure.
        // Reloading from local storage (rather than trusting whatever was
        // already in memory) is deliberate: local storage is the
        // authoritative record of what this student has completed, and it's
        // exactly what section 5 of the plan asks for on every Play Mode
        // start. Safe to call with no signed-in student or no LocalStorage
        // assigned - _completedKeys is simply left as-is (in-memory-only
        // progress, same as before this feature existed).
        private void LoadCompletedKeysFromLocalStorage()
        {
            if (localStorage == null) return;

            string studentId = CurrentStudentId;
            if (string.IsNullOrEmpty(studentId))
            {
                Debug.LogWarning("[AnatomyPlayModeController] No signed-in student - cannot restore Play Mode progress from local storage.");
                return;
            }

            localStorage.Load(studentId);

            _completedKeys.Clear();
            foreach (var key in localStorage.GetCompletedKeys())
                _completedKeys.Add(key);
        }

        // Fires whenever AnatomyPlayModeSyncService finishes a full sync
        // (upload + download/merge), whether or not this screen - or Play
        // Mode within it - happens to be open right now. Only act if Play
        // Mode is actually active; otherwise the next ActivatePlayMode call
        // will call LoadCompletedKeysFromLocalStorage itself anyway.
        private void HandleSyncCompleted()
        {
            if (_isPlayModeActive)
                RefreshCompletedKeys();
        }

        /// <summary>Re-reads _completedKeys from local storage and updates
        /// every part of the UI that depends on it, WITHOUT resetting the
        /// current question, points, or streak - the safe "refresh, don't
        /// restart" path the plan's section 7 asks for so a Sync Progress
        /// download that merges in structures answered on another device
        /// shows up immediately if Play Mode is already open. Newly-merged
        /// keys are also excluded from being asked again by CurrentQuestion/
        /// PickRandomUnanswered - they simply behave exactly like a
        /// just-answered-in-this-session key would.
        ///
        /// Safe to call whether or not Play Mode is currently active (it
        /// simply does nothing useful if not - HandleSyncCompleted already
        /// guards that for the automatic path, but this stays public so
        /// StudentExplore3dController/other callers don't need to know
        /// that).</summary>
        public void RefreshCompletedKeys()
        {
            if (!_isPlayModeActive) return;

            int before = _completedKeys.Count;
            LoadCompletedKeysFromLocalStorage();

            UpdateProgressLabel();

            // Isolate Answered/Unanswered fully replace the visible set on
            // every call rather than layering - re-apply whichever is
            // currently on so newly-merged structures are reflected without
            // the player having to re-toggle it themselves.
            if (_isolateAnsweredEnabled)
                SetIsolateAnswered(true);
            else if (_isolateUnansweredEnabled)
                SetIsolateUnanswered(true);

            // A structure the player is CURRENTLY being quizzed on just got
            // marked complete by the merge (e.g. answered on another
            // device moments ago) - don't leave a stale question up.
            if (_currentQuestion != null && _completedKeys.Contains(_currentQuestion.boneName))
            {
                _currentQuestion = null;
                _screen.SetInfoPanelDescription("This structure was already answered on another device.");
                SetGuessUiEnabled(false);
                _letterRow?.AddToClassList("hidden");
            }

            if (_completedKeys.Count > before)
                CheckForCompletion();
        }

        // ===== Isolate Answered / Isolate Unanswered =====
        // Two independent toggles that both drive the same underlying
        // visibility rule (AnatomyScreenController.SetIsolateAnsweredActive
        // fully replaces which structures are visible on every call, it
        // doesn't layer) - so they're kept mutually exclusive here:
        // turning one on always turns the other off first.

        private void OnIsolateAnsweredClicked() => SetIsolateAnswered(!_isolateAnsweredEnabled);

        private void SetIsolateAnswered(bool enabled)
        {
            if (enabled && _isolateUnansweredEnabled)
                SetIsolateUnanswered(false);

            _isolateAnsweredEnabled = enabled;
            _screen.SetIsolateAnsweredActive(enabled, info => info != null && _completedKeys.Contains(info.boneName));

            if (_isolateAnsweredButton != null)
                _isolateAnsweredButton.EnableInClassList("toolbar-btn-active", enabled);
        }

        private void OnUnansweredClicked() => SetIsolateUnanswered(!_isolateUnansweredEnabled);

        // Shows every structure the player hasn't identified yet and hides
        // every already-answered one - the inverse of Isolate Answered,
        // reusing the exact same screen-side visibility rule with an
        // inverted predicate.
        private void SetIsolateUnanswered(bool enabled)
        {
            if (enabled && _isolateAnsweredEnabled)
                SetIsolateAnswered(false);

            _isolateUnansweredEnabled = enabled;
            _screen.SetIsolateAnsweredActive(enabled, info => info != null && !_completedKeys.Contains(info.boneName));

            if (_unansweredButton != null)
                _unansweredButton.EnableInClassList("toolbar-btn-active", enabled);
        }

        // ===== Random (jump to a random unidentified structure) =====

        // Picks a random structure (from AllBoneData) that isn't in
        // _completedKeys yet and selects/focuses it through the same path
        // a hotspot tap or search pick would use - see
        // AnatomyScreenController.SelectAndFocusStructure. That selection
        // fires OnStructureSelected, which OnStructureSelected (below)
        // turns into this button's actual question, exactly like any
        // other selection. Tapping the button again re-rolls: when more
        // than one unanswered structure remains, the currently-active
        // question is excluded from the pick so a repeat tap always
        // visibly changes the selection instead of occasionally landing
        // back on the same one.
        private void OnRandomClicked()
        {
            var unanswered = _screen.AllBoneData
                .Where(b => b != null && !_completedKeys.Contains(b.boneName))
                .ToList();

            if (unanswered.Count == 0)
            {
                // Nothing left to find - CheckForCompletion (run after the
                // last correct answer) already shows the completion panel
                // for this case, so there's nothing further to do here.
                return;
            }

            AnatomyScreenController.BoneInfo pick;
            if (unanswered.Count == 1)
            {
                pick = unanswered[0];
            }
            else
            {
                do
                {
                    pick = unanswered[UnityEngine.Random.Range(0, unanswered.Count)];
                } while (pick == _currentQuestion);
            }

            _screen.SelectAndFocusStructure(pick);
        }

        // ===== Question flow =====

        // Fires for every selection made through the existing selection
        // system (hotspot / search / mesh-tap) regardless of mode - only
        // act on it while Play Mode is actually active.
        private void OnStructureSelected(AnatomyScreenController.BoneInfo info)
        {
            if (!_isPlayModeActive || info == null) return;

            if (!_screen.TryGetBoneDatabaseEntry(info, out var entry) || string.IsNullOrEmpty(entry.displayName))
            {
                Debug.LogWarning($"[AnatomyPlayModeController] No DisplayName for '{info.boneName}' - cannot use as a Play Mode question.");
                return;
            }

            _currentQuestion = info;
            _currentHints = 0;
            _revealedIndices.Clear();

            bool alreadyAnswered = _completedKeys.Contains(info.boneName);

            if (alreadyAnswered)
            {
                // Already solved - show the answer, no re-guessing, no re-scoring.
                _screen.SetInfoPanelTitle(entry.displayName);
                _screen.SetInfoPanelDescription("Already identified. Great work!");
                BuildLetterBoxes(string.Empty);
                _letterRow?.AddToClassList("hidden");
                SetGuessUiEnabled(false);
            }
            else
            {
                _screen.SetInfoPanelTitle("Identify this structure");
                _screen.SetInfoPanelDescription("Type each letter, or use a hint.");
                _letterRow?.RemoveFromClassList("hidden");
                BuildLetterBoxes(entry.displayName);
                SetGuessUiEnabled(true);
                FocusLetterField(0);
            }


           
        }

        private void SetGuessUiEnabled(bool enabled)
        {
            if (_hintButton != null) _hintButton.SetEnabled(enabled);
            if (_submitButton != null) _submitButton.SetEnabled(enabled);
            foreach (var field in _letterFields)
                field.SetEnabled(enabled);
        }

        // ===== Letter-box guessing UI =====
        //
        // Play Mode is guessed by typing directly into one box per letter
        // (Wordle-style) instead of a single free-text field. One TextField
        // per non-space character of DisplayName; spaces are never their
        // own box - see the plan's section 7/9.
        //
        // Boxes are grouped by word (see BuildLetterBoxes) so a long
        // answer like "LEFT COSTAL CARTILAGE OF SEVENTH RIB" wraps whole
        // words onto new lines instead of splitting a word awkwardly
        // across two rows of tiny boxes - each .letter-word-group is a
        // single flex item that either fits or moves to the next line as
        // a whole, while the boxes inside it never wrap. _letterFields /
        // _letterFieldSourceIndex stay a flat left-to-right list exactly
        // like before (grouping is purely visual, in which VisualElement
        // each field is parented to) so every other method below -
        // RevealLetterField, GatherGuessText, hint/submit/backspace
        // navigation - needs no changes.

        // Rebuilds _letterRow's children from scratch for a new question.
        // Safe to call with an empty string (e.g. for the already-answered
        // case) - it just clears the row.
        private void BuildLetterBoxes(string displayName)
        {
            _letterFields.Clear();
            _letterFieldSourceIndex.Clear();
            if (_letterRow == null) return;

            _letterRow.Clear();

            VisualElement currentWordGroup = null;

            for (int i = 0; i < displayName.Length; i++)
            {
                char c = displayName[i];
                if (char.IsWhiteSpace(c))
                {
                    // Ends the current word group - the next non-space
                    // character starts a new one. No visual element is
                    // created for the space itself; the gap comes from
                    // .letter-word-group's own margin in the USS.
                    currentWordGroup = null;
                    continue;
                }

                if (currentWordGroup == null)
                {
                    currentWordGroup = new VisualElement();
                    currentWordGroup.AddToClassList("letter-word-group");
                    _letterRow.Add(currentWordGroup);
                }

                var field = new TextField { maxLength = 1, isDelayed = false };
                field.AddToClassList("letter-box");

                int fieldIndex = _letterFields.Count; // captured for the closures below
                field.RegisterValueChangedCallback(evt => OnLetterFieldChanged(fieldIndex, evt.newValue));
                field.RegisterCallback<KeyDownEvent>(evt => OnLetterFieldKeyDown(fieldIndex, evt), TrickleDown.TrickleDown);
                field.RegisterCallback<FocusInEvent>(_ => field.AddToClassList("letter-box-active"));
                field.RegisterCallback<FocusOutEvent>(_ => field.RemoveFromClassList("letter-box-active"));

                currentWordGroup.Add(field);
                _letterFields.Add(field);
                _letterFieldSourceIndex.Add(i);
            }
        }

        // Keeps only the last character typed (guards against paste/IME
        // giving more than one char), upper-cases it to match DisplayName's
        // convention, then auto-advances to the next box - typing fills the
        // whole word left to right without needing to tab manually.
        private void OnLetterFieldChanged(int fieldIndex, string newValue)
        {
            if (fieldIndex < 0 || fieldIndex >= _letterFields.Count) return;

            if (string.IsNullOrEmpty(newValue))
                return;

            string single = char.ToUpperInvariant(newValue[newValue.Length - 1]).ToString();
            if (newValue != single)
                _letterFields[fieldIndex].SetValueWithoutNotify(single);

            FocusLetterField(fieldIndex + 1);
        }

        // Backspace on an empty box steps back to the previous box (so
        // clearing a whole guess reads naturally right to left); Enter
        // submits, same as tapping the Submit button.
        private void OnLetterFieldKeyDown(int fieldIndex, KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Backspace)
            {
                if (fieldIndex >= 0 && fieldIndex < _letterFields.Count && string.IsNullOrEmpty(_letterFields[fieldIndex].value))
                    FocusLetterField(fieldIndex - 1);
                return;
            }

            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                OnSubmitClicked();
        }

        // Focuses the box at index, skipping forward over any already
        // hint-revealed (read-only) boxes so typing always lands somewhere
        // editable.
        private void FocusLetterField(int index)
        {
            while (index >= 0 && index < _letterFields.Count && _letterFields[index].isReadOnly)
                index++;

            if (index >= 0 && index < _letterFields.Count)
                _letterFields[index].Focus();
        }

        // Fills and locks the box for the hinted DisplayName index so it
        // can't be typed over, then highlights it - see OnHintClicked.
        private void RevealLetterField(int displayNameIndex, char c)
        {
            int fieldIndex = _letterFieldSourceIndex.IndexOf(displayNameIndex);
            if (fieldIndex < 0 || fieldIndex >= _letterFields.Count) return;

            var field = _letterFields[fieldIndex];
            field.SetValueWithoutNotify(char.ToUpperInvariant(c).ToString());
            field.isReadOnly = true;
            field.AddToClassList("letter-box-revealed");
        }

        // Empties every box that wasn't filled in by a hint, for another
        // attempt after an incorrect guess - see HandleIncorrectAnswer.
        private void ClearUnrevealedLetterFields()
        {
            foreach (var field in _letterFields)
            {
                if (!field.isReadOnly)
                    field.SetValueWithoutNotify(string.Empty);
            }
        }

        // Reassembles the player's current guess from the letter boxes,
        // in DisplayName's exact character order (spaces included), so it
        // can be compared against DisplayName the same way a free-text
        // answer would be - see NormalizeAnswer.
        private string GatherGuessText(string displayName)
        {
            var sb = new StringBuilder(displayName.Length);
            int fieldCursor = 0;
            for (int i = 0; i < displayName.Length; i++)
            {
                if (char.IsWhiteSpace(displayName[i]))
                {
                    sb.Append(' ');
                    continue;
                }

                string v = fieldCursor < _letterFields.Count ? _letterFields[fieldCursor].value : string.Empty;
                sb.Append(string.IsNullOrEmpty(v) ? '_' : v[0]);
                fieldCursor++;
            }
            return sb.ToString();
        }

        // ===== Hints =====

        private void OnHintClicked()
        {
            if (_currentQuestion == null) return;
            if (!_screen.TryGetBoneDatabaseEntry(_currentQuestion, out var entry)) return;

            string displayName = entry.displayName;

            // Every non-whitespace, not-yet-revealed index.
            var candidates = new List<int>();
            for (int i = 0; i < displayName.Length; i++)
            {
                if (!char.IsWhiteSpace(displayName[i]) && !_revealedIndices.Contains(i))
                    candidates.Add(i);
            }

            if (candidates.Count == 0) return; // fully revealed already

            // Pick a random not-yet-revealed position rather than always
            // the leftmost one, so hints don't just fill the word in
            // left-to-right order.
            int pickedIndex = candidates[UnityEngine.Random.Range(0, candidates.Count)];

            _revealedIndices.Add(pickedIndex);
            _currentHints++;
            _hintsUsedTotal++;

            RevealLetterField(pickedIndex, displayName[pickedIndex]);
        }

        // ===== Answer submission =====

        private void OnSubmitClicked()
        {
            if (_currentQuestion == null) return;
            if (!_screen.TryGetBoneDatabaseEntry(_currentQuestion, out var entry)) return;

            string guess = GatherGuessText(entry.displayName);
            bool correct = NormalizeAnswer(guess) == NormalizeAnswer(entry.displayName);

            if (correct) HandleCorrectAnswer(_currentQuestion, entry);
            else HandleIncorrectAnswer();
        }

        // Ignore capitalization, normalize reasonable spacing - see the
        // plan's section 10. Never used to change what's stored/displayed,
        // only to compare.
        private static string NormalizeAnswer(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var collapsed = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");
            return collapsed.ToLowerInvariant();
        }

        private void HandleCorrectAnswer(AnatomyScreenController.BoneInfo info, BoneDatabaseEntry entry)
        {
            // Prevent duplicate scoring for an already-completed key -
            // see the plan's section 15. OnStructureSelected already
            // disables the guess UI for completed keys, but guard here too
            // in case of a stale/queued click.
            if (_completedKeys.Contains(info.boneName)) return;

            int questionPoints = PointsForHints(_currentHints);
            _currentStreak++;
            _highestStreak = Mathf.Max(_highestStreak, _currentStreak);

            int streakBonus = StreakBonusFor(_currentStreak);

            _totalPoints += questionPoints + streakBonus;
            _correctAnswers++;
            _completedKeys.Add(info.boneName);

            _screen.SetInfoPanelTitle(entry.displayName);
            _screen.SetInfoPanelDescription(streakBonus > 0
                ? $"Correct! +{questionPoints} points (+{streakBonus} streak bonus)."
                : $"Correct! +{questionPoints} points.");
            SetGuessUiEnabled(false);
            _letterRow?.AddToClassList("hidden");

            SaveCorrectAnswer(info.boneName, entry.displayName, questionPoints, streakBonus);

            // Isolate Answered is live - a newly-completed key should
            // immediately become visible if the player has it toggled on.
            if (_isolateAnsweredEnabled)
                SetIsolateAnswered(true);

            // Isolate Unanswered is live - a newly-completed key should
            // immediately drop out of view if the player has it toggled on.
            if (_isolateUnansweredEnabled)
                SetIsolateUnanswered(true);

            UpdateProgressLabel();
            CheckForCompletion();
        }

        // Offline-first save for a correct answer - see the plan's section
        // 3. Local storage is written to immediately and always succeeds
        // regardless of connectivity; Firebase is then attempted (via the
        // sync service if one is assigned, so retries stay idempotent) but
        // is never required for the answer itself to be accepted.
        private void SaveCorrectAnswer(string key, string displayName, int pointsEarned, int streakBonus)
        {
            string studentId = CurrentStudentId;
            if (localStorage == null || string.IsNullOrEmpty(studentId))
            {
                // No offline storage available (or no signed-in student) -
                // fall back to the original direct-to-Firebase save so Play
                // Mode still works, just without offline/restore support.
                firebase?.SaveAnswer(key, displayName, true, _currentHints, pointsEarned, _currentStreak, streakBonus);
                return;
            }

            var record = localStorage.SaveAnswer(
                studentId, key, displayName, true, _currentHints, pointsEarned, _currentStreak, streakBonus);

            if (syncService != null)
            {
                syncService.RequestSync();
            }
            else if (firebase != null)
            {
                // No sync service assigned - still try to reach Firebase
                // right away (e.g. while online) using the same idempotent,
                // deterministic-ID write a retry would use.
                firebase.SyncRecord(record, success =>
                {
                    if (success) localStorage.MarkSynced(key);
                });
            }
        }

        private void HandleIncorrectAnswer()
        {
            _currentStreak = 0;
            _incorrectAnswers++;

            if (_screen.TryGetBoneDatabaseEntry(_currentQuestion, out var entry))
            {
                _screen.SetInfoPanelDescription("Not quite - try again.");

                firebase?.SaveAnswer(_currentQuestion.boneName, entry.displayName, false, _currentHints, 0, 0, 0);
            }

            ClearUnrevealedLetterFields();
            FocusLetterField(0);
            // Structure stays visible, another attempt is allowed - guess UI stays enabled.
        }

        private int PointsForHints(int hints)
        {
            if (hints <= 0) return pointsNoHints;
            if (hints == 1) return points1Hint;
            if (hints == 2) return points2Hints;
            return points3PlusHints;
        }

        private int StreakBonusFor(int streak)
        {
            int total = 0;
            foreach (var b in streakBonuses)
                if (b.streakCount == streak) total += b.bonusPoints;
            return total;
        }

        // ===== Progress / completion =====

        private void UpdateProgressLabel()
        {
            if (_progressLabel == null) return;
            int total = _screen.AllBoneData.Count;
            _progressLabel.text = $"Progress: {_completedKeys.Count} / {total}";
        }

        private void CheckForCompletion()
        {
            int total = _screen.AllBoneData.Count;
            if (total == 0 || _completedKeys.Count < total) return;

            if (_completionPanel != null && _completionLabel != null)
            {
                _completionLabel.text =
                    $"ANATOMY COMPLETE!\nYou identified {_completedKeys.Count} / {total} structures\n" +
                    $"Total Points: {_totalPoints}\nHighest Streak: {_highestStreak}";
                _completionPanel.RemoveFromClassList("hidden");
            }
        }

        private void HideCompletionPanel()
        {
            _completionPanel?.AddToClassList("hidden");
        }
    }
}
