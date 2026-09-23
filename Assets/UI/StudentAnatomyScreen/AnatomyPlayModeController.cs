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
    /// points, completedKeys, progress, Isolate Answered). It never
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
        // Verbose diagnostics. [Conditional] removes every call - INCLUDING
        // the string-interpolation arguments - from device builds, so the
        // per-keystroke logging in the letter-box input path no longer
        // costs allocations + logcat I/O on every key press. Logs still
        // appear in the Editor; add ANATOMY_VERBOSE_LOG to Scripting
        // Define Symbols to get them in a device build too.
        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("ANATOMY_VERBOSE_LOG")]
        private static void Log(string message) => Debug.Log(message);

        [Header("Points")]
        [Tooltip("Points awarded for every correct answer, regardless of how many hints were used.")]
        [SerializeField] private int pointsPerCorrectAnswer = 1;

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
        private int _totalPoints;
        private int _correctAnswers;
        private int _incorrectAnswers;
        private int _hintsUsedTotal;
        private bool _isolateAnsweredEnabled;
        private bool _isolateUnansweredEnabled;
        private bool _isPlayModeActive;

        // ===== Baseline Assessment (Pretest/Posttest) mode =====
        //
        // A restricted variant of Play Mode used ONLY by BaselineAssessmentController
        // for the student's one-time pretest/posttest: fixed set of structures
        // (baselineTargetKeys, always Skeletal in practice), no hints, and
        // completion/scoring measured against that fixed set instead of every
        // structure in the current system. Deliberately uses its own counters
        // (_baselineCorrectCount/_baselinePoints) rather than _totalPoints/
        // _correctAnswers, so a baseline session never contaminates the student's
        // real, ongoing Play Mode stats or completed-structures list - nothing
        // here is written to AnatomyPlayModeLocalStorage/AnatomyPlayModeFirebase
        // at all (see HandleCorrectAnswer/HandleIncorrectAnswer's _isBaselineMode
        // branches). Every other Play Mode code path is unchanged when this is
        // false, which it is unless StartBaselineAssessment was just called.
        private bool _isBaselineMode;
        private HashSet<string> _baselineTargetKeys;
        private int _baselineCorrectCount;
        private int _baselinePoints;
        private Action<int, int, int> _onBaselineCompleted; // (correctCount, totalCount, totalPoints)
        private Action<string> _onBaselineStructureIdentified; // (structureKey just answered correctly)

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
        public IReadOnlyCollection<string> CompletedKeys => _completedKeys;

        // ===== UI refs (queried from the same root AnatomyScreenController uses) =====
        private VisualElement _questionControlsSection;
        private Button _isolateAnsweredButton;
        private Button _unansweredButton;
        private Button _randomButton;
        private Label _progressLabel;
        private Button _hintButton;
        // The Hint button is built in UXML with its OWN child Labels (icon +
        // text). Setting Button.text draws a SECOND text on top of those
        // children (the "two hints overlapping" bug), so the caption is
        // changed on this child label instead - never on the button itself.
        private Label _hintButtonLabel;
        private VisualElement _letterRow;
        private Button _submitButton;
        private VisualElement _completionPanel;
        private Label _completionLabel;
        private Button _completionCloseButton;
        private VisualElement _noHintsPanel;
        private Label _noHintsLabel;
        private Button _noHintsCloseButton;
        private VisualElement _playModeControlsRow;
        private VisualElement _audioRow;

        // One display-only TextField per non-space character of the
        // current question's DisplayName, in left-to-right order, built
        // fresh by BuildLetterBoxes for every question. _letterFieldSourceIndex[i]
        // is the DisplayName index that _letterFields[i] represents, so a
        // hint (which reveals by DisplayName index) can find the matching
        // box. Spaces never get a TextField - see BuildLetterBoxes.
        //
        // These boxes are NOT directly typed into (isReadOnly/non-focusable
        // - see BuildLetterBoxes) - all real keystrokes go through
        // _masterInput below, and these are kept in sync as pure display.
        // See the comment above _masterInput for why.
        private readonly List<TextField> _letterFields = new List<TextField>();
        private readonly List<int> _letterFieldSourceIndex = new List<int>();

        // The single real (hidden) text input every keystroke actually
        // goes to - see the comment above BuildLetterBoxes for why a
        // per-box TextField design can't reliably support backspace
        // on-device, which is what this replaces.
        private TextField _masterInput;

        // _letterFields indices that are currently typeable, in
        // left-to-right order - i.e. NOT hint-revealed. _masterInput.value[i]
        // is always the letter typed for the box at _editableBoxIndices[i];
        // a hint reveal splices the revealed box's index out of this list
        // (see RevealLetterField) so subsequent typed characters keep
        // lining up with the right boxes.
        private readonly List<int> _editableBoxIndices = new List<int>();

        // Sequence index (into _editableBoxIndices, NOT _letterFields) of
        // the box the player last tapped directly, so the next character
        // they type overwrites THAT box instead of appending at the end -
        // see SelectBoxForEdit/OnMasterInputChanged. -1 means no box is
        // selected, i.e. normal left-to-right typing.
        private int _selectedEditSeq = -1;

        // Mirrors _masterInput's value after every programmatic change
        // (BuildLetterBoxes/RevealLetterField/ClearUnrevealedLetterFields -
        // see SetMasterValue), so OnMasterInputChanged can tell exactly
        // which single character the OS keyboard just appended even
        // though _masterInput is never visibly focused with a real
        // on-screen cursor (see the class comment above BuildLetterBoxes).
        private string _masterInputPrevValue = string.Empty;

        // Set right after a box-overwrite splice (see OnMasterInputChanged)
        // to the exact raw fragment that was just spliced in. The
        // Blur()+Focus() cycle used to redirect the keyboard back to
        // _masterInput after a box tap (see _letterRow's PointerDownEvent
        // handler in WireUi) reliably causes Android's Game Activity
        // TouchScreenKeyboard to fire a SECOND ChangeEvent carrying that
        // same fragment again, arriving after _selectedEditSeq has already
        // been reset to -1 by the first event. Without this guard that
        // second event is indistinguishable from ordinary sequential
        // typing and overwrites the whole guess with just that fragment
        // (see OnMasterInputChanged). Null means no echo is expected;
        // consumed (checked and cleared) on the very next ChangeEvent
        // regardless of whether it actually matched.
        private string _pendingEchoRaw = null;

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
            if (_noHintsCloseButton != null) _noHintsCloseButton.clicked -= HideNoHintsPanel;

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
            _hintButtonLabel = _hintButton?.Q<Label>(className: "play-mode-btn-label");
            _letterRow = _root.Q<VisualElement>("PlayModeLetterRow");
            _submitButton = _root.Q<Button>("PlayModeSubmitButton");
            _completionPanel = _root.Q<VisualElement>("PlayModeCompletionPanel");
            _completionLabel = _root.Q<Label>("PlayModeCompletionLabel");
            _completionCloseButton = _root.Q<Button>("PlayModeCompletionCloseButton");
            _noHintsPanel = _root.Q<VisualElement>("NoHintsPanel");
            _noHintsLabel = _root.Q<Label>("NoHintsLabel");
            _noHintsCloseButton = _root.Q<Button>("NoHintsCloseButton");
            _playModeControlsRow = _root.Q<VisualElement>("PlayModeControls");
            _audioRow = _root.Q<VisualElement>("AudioRow");

            // This controller lives on the persistent UIManager GameObject and is
            // enabled at app start, so OnEnable -> HandleScreenReady -> WireUi also
            // runs while Student Login (or any other non-Anatomy screen) is showing.
            // None of the Anatomy screen's elements exist then, and wiring anyway
            // would add the _masterInput TextField to the shared root, where it
            // shows up as a stray, unstyled "unity-text-input" (its
            // .letter-master-input style lives in the Anatomy screen's stylesheet,
            // which UIManager clears on every screen change). Bail out quietly.
            if (_letterRow == null && _questionControlsSection == null &&
                _playModeControlsRow == null && _submitButton == null)
            {
                return;
            }

            if (_letterRow == null)
            {
                Debug.LogWarning("[AnatomyPlayModeController] 'PlayModeLetterRow' not found in UXML - " +
                                  "Play Mode's letter-box guessing UI cannot be built. See AnatomyScreen.uxml.");
            }

            // The single real input for the whole guess - see the field
            // comment above _masterInput. Created fresh per screen visit
            // (this whole UXML tree is rebuilt each visit anyway) and
            // parented to _root rather than _letterRow, since BuildLetterBoxes
            // clears _letterRow's children on every new question and this
            // needs to survive that. pickingMode = Ignore because it's
            // never tapped directly - see the _letterRow PointerDownEvent
            // handler below, which forwards focus to it instead.
            if (_root != null)
            {
                _masterInput?.RemoveFromHierarchy(); // WireUi can run twice on the same tree - never keep two
                _masterInput = new TextField { isDelayed = false, multiline = false };
                _masterInput.AddToClassList("letter-master-input");
                // Autocorrect/predictive text keeps a "composing" region open
                // in the Android IME; every text write-back into the field
                // resets it and swallows fast keystrokes. The guess is letters
                // only, so there is nothing useful to correct anyway.
                _masterInput.autoCorrection = false;
                _masterInput.pickingMode = PickingMode.Ignore;
                _masterInput.RegisterValueChangedCallback(OnMasterInputChanged);
                _masterInput.RegisterCallback<KeyDownEvent>(OnMasterInputKeyDown, TrickleDown.TrickleDown);
                _root.Add(_masterInput);
            }

            // Boxes themselves are isReadOnly/non-focusable display only
            // (see BuildLetterBoxes) - tapping one should still bring the
            // keyboard up and resume typing, so forward focus here.
            //
            // Deliberately Blur() before Focus() (deferred one frame -
            // Focus() called in the very same callback as Blur() doesn't
            // reliably reopen the on-screen keyboard, same reasoning as
            // other one-frame-deferred UI Toolkit calls in this class -
            // see StartCoroutineDeferredWire). This matters because
            // tapping the keyboard's own Done/check key dismisses the OS
            // on-screen keyboard WITHOUT UI Toolkit's focus state ever
            // changing to match - _masterInput is still the "focused"
            // element as far as UI Toolkit is concerned. Calling Focus()
            // on something already considered focused is a no-op (no
            // focus change happens), so nothing tells the OS to reopen
            // the keyboard. Blurring first guarantees an actual focus
            // transition on the following Focus() regardless of which
            // state (still-focused vs genuinely blurred) _masterInput was
            // already in, so tapping a box reliably reopens the keyboard
            // either way.
            // TrickleDown.TrickleDown (capture phase): letter boxes are
            // TextFields, and even readOnly/non-focusable TextFields still
            // run their own internal pointer-down handling on their inner
            // TextElement (cursor placement) which calls StopPropagation().
            // That happens AT the target, before bubble-phase handlers ever
            // run - so a plain bubble-phase registration here silently never
            // fires when the tap lands on a box's glyph. Capture phase runs
            // before the target is reached, so it can't be swallowed.
            _letterRow?.RegisterCallback<PointerDownEvent>(_ =>
            {
                if (_masterInput == null) return;
                Log("[AnatomyPlayModeController] _letterRow PointerDownEvent: re-focusing _masterInput.");
                _masterInput.Blur();
                _masterInput.schedule.Execute(() => _masterInput?.Focus());
            }, TrickleDown.TrickleDown);

            // DIAGNOSTIC: capture phase, so this fires before ANY other
            // handler on this element or its children - even ones that
            // stop propagation - can swallow it. If a tap on a letter box
            // logs nothing anywhere else (no "Box tapped", no the bubble
            // handler above), but this line ALSO never appears, the touch
            // never reached UI Toolkit's event system at all for that tap.
            // The leading suspect for that: the OS on-screen keyboard is
            // up (because _masterInput is focused) and the tap is being
            // consumed by the OS to dismiss the keyboard instead of being
            // forwarded to Unity - the same category of platform quirk as
            // the Done-key case already handled above, just triggered by
            // tapping elsewhere instead of the keyboard's own dismiss key.
            // If this DOES log but nothing downstream does, it's a
            // geometric/hit-test issue instead - evt.target below tells you
            // which element actually absorbed it.
            _letterRow?.RegisterCallback<PointerDownEvent>(evt =>
            {
                Log($"[AnatomyPlayModeController] DIAGNOSTIC _letterRow capture-phase PointerDownEvent: target={evt.target}, position={evt.position}, pointerId={evt.pointerId}.");
            }, TrickleDown.TrickleDown);

            // Same idea but at _root, so a tap that lands OUTSIDE
            // _letterRow entirely (e.g. it's hitting BodyArea underneath
            // because of a layering/z-order issue) still shows up with
            // its real target, instead of producing silence everywhere.
            _root?.RegisterCallback<PointerDownEvent>(evt =>
            {
                Log($"[AnatomyPlayModeController] DIAGNOSTIC _root capture-phase PointerDownEvent: target={evt.target}, position={evt.position}, pointerId={evt.pointerId}.");
            }, TrickleDown.TrickleDown);

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

            if (_noHintsPanel != null)
                _noHintsPanel.AddToClassList("hidden");

            if (_noHintsCloseButton != null)
            {
                _noHintsCloseButton.clicked -= HideNoHintsPanel;
                _noHintsCloseButton.clicked += HideNoHintsPanel;
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

        /// <summary>Called by BaselineAssessmentController to start the student's
        /// pretest/posttest: same tap-the-model/type-the-name interaction as normal
        /// Play Mode, but restricted to targetKeys only (every other structure on
        /// the model is hidden via the same Isolate mechanism Answered/Unanswered
        /// already use), with hints disabled entirely. onStructureIdentified fires
        /// after each individual correct answer (structureKey just answered) - lets
        /// the caller advance whatever "current question" prompt it's showing.
        /// onCompleted fires exactly once, when every key in targetKeys has been
        /// answered correctly, with (correctCount, totalCount, totalPoints) -
        /// BaselineAssessmentController uses that to record the attempt and
        /// navigate away; this class does not navigate or save anywhere itself.
        /// Safe to call before WireUi has run, same as RequestPlayModeOnOpen.</summary>
        public void StartBaselineAssessment(IEnumerable<string> targetKeys, Action<string> onStructureIdentified, Action<int, int, int> onCompleted)
        {
            _isBaselineMode = true;

            // Normalized (NormalizeKey: trims/collapses whitespace, strips a
            // "(Clone)" suffix, lower-cases) - the JSON's StructureKey values
            // are hand-typed and may not match a clicked GameObject's raw
            // name byte-for-byte, exactly the mismatch BoneDatabaseService
            // already solves for everywhere else bone names are matched.
            // Every comparison against this set below normalizes info.boneName
            // the same way before checking it, so both sides are on equal footing.
            _baselineTargetKeys = new HashSet<string>(targetKeys.Select(BoneDatabaseService.NormalizeKey));
            _baselineCorrectCount = 0;
            _baselinePoints = 0;
            _onBaselineStructureIdentified = onStructureIdentified;
            _onBaselineCompleted = onCompleted;
            _pendingAutoStart = true;
            TryConsumePendingAutoStart();
        }

        /// <summary>Called by BaselineAssessmentController's Finish button - ends
        /// the session immediately with whatever's been answered so far, instead
        /// of only completing once every target key is found. totalCount stays
        /// the full target count (not just what was attempted), so an early
        /// finish is scored as "X out of 10" rather than "X out of X" - an
        /// honest partial result rather than one that looks complete. No-op if
        /// baseline mode isn't active or has already completed/been finished
        /// (guards against a stray double-click firing the callback twice).</summary>
        public void FinishBaselineAssessmentEarly()
        {
            if (!_isBaselineMode || _onBaselineCompleted == null)
            {
                Debug.LogWarning($"[AnatomyPlayModeController] FinishBaselineAssessmentEarly: no-op " +
                                  $"(_isBaselineMode={_isBaselineMode}, _onBaselineCompleted null={_onBaselineCompleted == null}).");
                return;
            }

            var callback = _onBaselineCompleted;
            int correctCount = _baselineCorrectCount;
            int totalCount = _baselineTargetKeys.Count;
            int points = _baselinePoints;
            _onBaselineCompleted = null; // fire exactly once, same as natural completion
            Log($"[AnatomyPlayModeController] FinishBaselineAssessmentEarly: {correctCount}/{totalCount}, {points} pts.");
            callback.Invoke(correctCount, totalCount, points);
        }

        /// <summary>Explicitly leaves Play Mode (normal or baseline), running
        /// the same cleanup DeactivatePlayMode always has - undoing Isolate
        /// Answered, baseline isolation to the fixed 10-structure set,
        /// hidden Play Mode buttons, etc.
        ///
        /// This exists because that cleanup previously only ran from
        /// OnDisable, but this component's OnEnable/OnDisable only fire
        /// once for the GameObject's entire lifetime - UIManager's screen
        /// navigation (ShowScreen/InitializeControllerAfterUI) only toggles
        /// AnatomyScreenController's enabled flag on each visit, never this
        /// component's (see the OnEnable comment above). Without an
        /// explicit call like this, ending a baseline assessment and
        /// navigating to Explore 3D left the model still isolated down to
        /// just the pretest/posttest's 10 structures. Called by
        /// BaselineAssessmentController.OnAssessmentCompleted before it
        /// navigates away. No-op if Play Mode isn't currently active.</summary>
        public void ExitPlayMode()
        {
            if (_isPlayModeActive)
                DeactivatePlayMode();
        }

        private void ActivatePlayMode()
        {
            _isPlayModeActive = true;

            if (_isBaselineMode)
            {
                // A baseline session is always a fresh, in-memory-only set of
                // completed keys - never mixed with the student's real,
                // persisted Play Mode progress (see the class-level comment on
                // _isBaselineMode above).
                _completedKeys.Clear();
            }
            else
            {
                // Restore this student's completed structures from local
                // storage before anything else below can generate or allow a
                // question - see the plan's section 5 ("Populate
                // _completedKeys before generating or allowing new questions").
                // This works with no internet connection, since local storage
                // never touches Firebase.
                LoadCompletedKeysFromLocalStorage();
            }

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

            if (_isBaselineMode)
            {
                // Restrict the visible/tappable model to exactly the fixed
                // baseline set, reusing the same Isolate mechanism Answered/
                // Unanswered already use elsewhere in this class - this is a
                // controlled test, not free exploration, so every other
                // structure on the skeleton is hidden rather than merely
                // "not asked about".
                _screen.SetIsolateAnsweredActive(true, info => info != null && _baselineTargetKeys.Contains(BoneDatabaseService.NormalizeKey(info.boneName)));

                // Reset re-enables every bone's renderer/collider unconditionally
                // (correct for ordinary Explore/Play Mode) - re-apply the same
                // isolation immediately after, or a student tapping Reset mid-test
                // would suddenly see and be able to tap the entire skeleton.
                _screen.AfterReset += ReapplyBaselineIsolation;

                // Isolate Answered/Unanswered and Random all assume the full
                // AllBoneData pool - none of them make sense against a fixed
                // 10-structure test, so they're hidden rather than adapted.
                if (_isolateAnsweredButton != null) _isolateAnsweredButton.style.display = DisplayStyle.None;
                if (_unansweredButton != null) _unansweredButton.style.display = DisplayStyle.None;
                if (_randomButton != null) _randomButton.style.display = DisplayStyle.None;

                // No hints during a formal assessment - hide the button
                // entirely rather than merely disabling it, so there's no
                // ambiguity about why it doesn't work.
                if (_hintButton != null) _hintButton.style.display = DisplayStyle.None;
            }

            UpdateProgressLabel();
        }

        /// <summary>Re-applies the baseline isolation after AnatomyScreenController's
        /// Reset button unconditionally re-enables every bone - see the
        /// _screen.AfterReset subscription above. No-op once baseline mode has
        /// ended (guarded by _isBaselineMode) so a stray late-firing event can't
        /// re-isolate a screen that's moved on to something else.</summary>
        private void ReapplyBaselineIsolation()
        {
            if (!_isBaselineMode) return;
            _screen.SetIsolateAnsweredActive(true, info => info != null && _baselineTargetKeys.Contains(BoneDatabaseService.NormalizeKey(info.boneName)));
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

            if (_isBaselineMode)
            {
                // Undo the isolation/hidden-controls changes ActivatePlayMode
                // made for the baseline session, so a later NORMAL Play Mode
                // visit starts from the same clean state it always has.
                _screen.SetIsolateAnsweredActive(false, null);
                _screen.AfterReset -= ReapplyBaselineIsolation;
                if (_isolateAnsweredButton != null) _isolateAnsweredButton.style.display = DisplayStyle.Flex;
                if (_unansweredButton != null) _unansweredButton.style.display = DisplayStyle.Flex;
                if (_randomButton != null) _randomButton.style.display = DisplayStyle.Flex;
                if (_hintButton != null) _hintButton.style.display = DisplayStyle.Flex;

                _isBaselineMode = false;
                _baselineTargetKeys = null;
                _onBaselineCompleted = null;
                _onBaselineStructureIdentified = null;
            }

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
        /// current question or points - the safe "refresh, don't
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

            // A baseline session's _completedKeys is deliberately its own
            // fresh, in-memory-only set (see ActivatePlayMode) - a background
            // sync completing mid-test must never overwrite it with the
            // student's real, persisted Play Mode progress.
            if (_isBaselineMode) return;

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
            // Unreachable while _isBaselineMode is true - the Random button
            // is hidden entirely during a baseline session (see
            // ActivatePlayMode) since a fixed 10-structure test has nothing
            // meaningful for it to jump to. Guarded anyway in case a stale
            // click is already queued the instant the button hides.
            if (_isBaselineMode) return;

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

            // Structures outside the fixed set are hidden/non-tappable
            // already (see ActivatePlayMode's SetIsolateAnsweredActive
            // call) - this is a safety net, not the primary guard.
            if (_isBaselineMode && (_baselineTargetKeys == null || !_baselineTargetKeys.Contains(BoneDatabaseService.NormalizeKey(info.boneName))))
                return;

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
                RestoreRevealedHints(info.boneName, entry.displayName);
                SetGuessUiEnabled(true);
                _masterInput?.Focus();

                // Reflect today's already-used hint count for the active
                // system immediately - a student who hit the limit earlier
                // (on this device or another, once synced) should see "No
                // more hints" the instant a new question loads, not only
                // after tapping Hint and being denied.
                RefreshHintButtonState();
            }
        }

        private void SetGuessUiEnabled(bool enabled)
        {
            if (_hintButton != null) _hintButton.SetEnabled(enabled);
            if (_submitButton != null) _submitButton.SetEnabled(enabled);
            if (_masterInput != null) _masterInput.SetEnabled(enabled);
            foreach (var field in _letterFields)
                field.SetEnabled(enabled);
        }

        // ===== Letter-box guessing UI =====
        //
        // Play Mode is guessed Wordle-style: one box shown per non-space
        // character of DisplayName. An EARLIER version gave each box its
        // own real TextField to type into directly, auto-advancing focus
        // to the next (empty) box after every letter. That broke backspace
        // on-device: typing auto-advances into an empty box, and on a real
        // device the on-screen keyboard's backspace on an ALREADY-EMPTY
        // field raises no event of any kind, on any input path (not
        // Keyboard.current, not Input.inputString either) - there's
        // nothing for the OS to report because nothing about the text
        // actually changed. That's not a detection bug to work around;
        // it's a real absence of any signal to detect.
        //
        // So instead, only ONE real TextField exists at all - _masterInput,
        // hidden, always holding the FULL currently-typed guess (for the
        // still-editable boxes) as one continuous string. Backspacing a
        // box that still has a letter in it now always has a letter in
        // the underlying string to delete too, so native deletion (which
        // already reliably raises a value-changed event on-device - the
        // earlier design relied on exactly this for its non-empty case)
        // is all that's needed. There is no more "already empty" case to
        // special-case, so nothing has to poll for a keypress at all.
        //
        // The boxes below (_letterFields) are pure display now -
        // isReadOnly/non-focusable, just kept in sync with _masterInput's
        // content (see RenderLetterBoxesFromMaster) and with hint reveals
        // (see RevealLetterField). Grouped by word (see BuildLetterBoxes)
        // so a long answer like "LEFT COSTAL CARTILAGE OF SEVENTH RIB"
        // wraps whole words onto new lines instead of splitting a word
        // awkwardly across two rows of tiny boxes.

        // Rebuilds _letterRow's children from scratch for a new question,
        // and resets _masterInput/_editableBoxIndices to match. Safe to
        // call with an empty string (e.g. for the already-answered case)
        // - it just clears the row.
        private void BuildLetterBoxes(string displayName)
        {
            _letterFields.Clear();
            _letterFieldSourceIndex.Clear();
            _editableBoxIndices.Clear();
            ClearBoxSelection();

            if (_letterRow != null)
            {
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

                    var field = new TextField { isReadOnly = true, focusable = false };
                    field.AddToClassList("letter-box");

                    // Still-editable boxes accept taps directly (Position) so
                    // SelectBoxForEdit can tell exactly which one was tapped;
                    // the tap then also bubbles up to _letterRow's own
                    // PointerDownEvent handler (see WireUi) which forwards
                    // focus to _masterInput. Hint-revealed boxes go
                    // back to Ignore in RevealLetterField, since they can no
                    // longer be selected for editing.
                    field.pickingMode = PickingMode.Position;
                    int capturedFieldIndex = _letterFields.Count; // this box's index, fixed at creation time.
                    // TrickleDown.TrickleDown: same reasoning as the
                    // _letterRow re-focus handler in WireUi - TextField's
                    // own inner TextElement stops the PointerDownEvent
                    // before it can bubble back up to `field`, so a
                    // tap that lands on the glyph itself (not the box's
                    // padding) never reaches a bubble-phase handler here.
                    field.RegisterCallback<PointerDownEvent>(_ =>
                    {
                        Log($"[AnatomyPlayModeController] Box tapped: fieldIndex={capturedFieldIndex}.");
                        SelectBoxForEdit(capturedFieldIndex);
                    }, TrickleDown.TrickleDown);

                    currentWordGroup.Add(field);
                    _letterFields.Add(field);
                    _letterFieldSourceIndex.Add(i);
                    _editableBoxIndices.Add(_letterFields.Count - 1); // none hint-revealed yet at build time.
                }
            }

            SetMasterValue(string.Empty);
            if (_masterInput != null)
            {
                // +1, not _editableBoxIndices.Count: this is native-level
                // enforcement, and it fires BEFORE OnMasterInputChanged ever
                // sees the keystroke. Capping it exactly at the box count
                // silently eats the keystroke whenever the guess is already
                // full and the player taps a box to fix a letter - the OS
                // keyboard appends at the (invisible) end, the field is
                // already at maxLength, so Unity drops the key and no
                // ChangeEvent fires at all, which is why the overwrite path
                // in OnMasterInputChanged looked "dead". Give it one
                // character of headroom for that append-then-splice
                // round-trip; FilterMasterInputValue already truncates the
                // *logical* value back to _editableBoxIndices.Count right
                // after, so the real cap still holds.
                _masterInput.maxLength = _editableBoxIndices.Count + 1;
                Log($"[AnatomyPlayModeController] BuildLetterBoxes: {_editableBoxIndices.Count} editable box(es), _masterInput.maxLength set to {_masterInput.maxLength}.");
            }

            RenderLetterBoxesFromMaster();
        }

        // Centralizes every PROGRAMMATIC write to _masterInput's value
        // (as opposed to the player typing) so _masterInputPrevValue never
        // drifts out of sync with what's actually in the field - see the
        // field comment above _masterInputPrevValue for why that matters.
        private void SetMasterValue(string value)
        {
            _masterInputPrevValue = value ?? string.Empty;
            _masterInput?.SetValueWithoutNotify(_masterInputPrevValue);

            // SetValueWithoutNotify re-syncs the TEXT shown to the native
            // on-screen (mobile) keyboard, but it does NOT move that
            // keyboard's cursor - the cursor stays wherever it was last
            // left (e.g. right after the single character typed into a
            // just-reset field during a box overwrite - see the comment in
            // OnMasterInputChanged). Left alone, the next real keystroke
            // the player types gets inserted at that stale position
            // instead of continuing from the end, splicing it into the
            // middle of the word. Explicitly park the cursor at the end of
            // the new value after every programmatic write so normal
            // typing always resumes by appending, not inserting.
            //
            // This can legitimately fail: the same Blur()+Focus() cycle
            // that causes the keyboard-echo behaviour documented above
            // _pendingEchoRaw also leaves the native TouchScreenKeyboard's
            // own internal text buffer out of sync with the logical value
            // we just wrote via SetValueWithoutNotify - e.g. the native
            // buffer may still only contain the single just-typed
            // fragment (length 1) while _masterInputPrevValue is the full
            // spliced word (length 8). Asking SelectRange for a position
            // based on the logical length is then out of range for the
            // native buffer and ITextSelection.SelectRange throws
            // ArgumentOutOfRangeException. There's no reliable way to ask
            // the native keyboard for its real current length up front, so
            // rather than pre-validating a bound we can't trust, swallow a
            // failed cursor-park here: losing the cursor-position nicety in
            // that edge case is harmless, but letting the exception escape
            // is not - it used to unwind straight out of this method and
            // abort whatever the caller (OnMasterInputChanged) did next,
            // which was the call to RenderLetterBoxesFromMaster() that
            // actually redraws the tapped box with its new letter. That's
            // why the splice used to succeed internally while the box
            // visibly never updated.
            try
            {
                _masterInput?.SelectRange(_masterInputPrevValue.Length, _masterInputPrevValue.Length);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Log($"[AnatomyPlayModeController] SetMasterValue: SelectRange({_masterInputPrevValue.Length}, {_masterInputPrevValue.Length}) rejected by native keyboard (buffer desync after Blur()+Focus()) - ignoring, value is still correctly set. {ex.Message}");
            }
        }

        // Tapping a still-editable letter box selects it as the target for
        // the NEXT character typed, so that character overwrites just this
        // box instead of appending at the end of the guess - see
        // OnMasterInputChanged for how the overwrite itself happens.
        // Boxes that are already hint-revealed (spliced out of
        // _editableBoxIndices) and boxes further ahead than anything typed
        // so far (nothing there yet to overwrite) can't be selected; a tap
        // on either still forwards focus/keyboard to _masterInput via
        // _letterRow's own PointerDownEvent handler in WireUi, it just
        // doesn't arm an overwrite.
        private void SelectBoxForEdit(int fieldIndex)
        {
            int seq = _editableBoxIndices.IndexOf(fieldIndex);
            string value = _masterInputPrevValue ?? string.Empty;

            if (seq < 0)
            {
                Log($"[AnatomyPlayModeController] SelectBoxForEdit: fieldIndex={fieldIndex} is NOT in _editableBoxIndices (likely hint-revealed) - bailing, no selection armed.");
                return;
            }
            if (seq > value.Length)
            {
                Log($"[AnatomyPlayModeController] SelectBoxForEdit: fieldIndex={fieldIndex}, seq={seq} is past the current guess length ({value.Length}) - nothing typed there yet to overwrite, bailing.");
                return;
            }

            _selectedEditSeq = seq;
            Log($"[AnatomyPlayModeController] SelectBoxForEdit: fieldIndex={fieldIndex}, seq={seq} ARMED, currentValueLen={value.Length}, maxLength={_masterInput?.maxLength}.");

            foreach (var f in _letterFields)
                f.RemoveFromClassList("letter-box-selected");
            _letterFields[fieldIndex].AddToClassList("letter-box-selected");
        }

        // Cancels any pending "next character overwrites this box" selection
        // (see SelectBoxForEdit) and clears its highlight, without touching
        // _masterInput's actual text. Called whenever that selection is
        // consumed (a character was typed) or invalidated (a new question
        // was built).
        private void ClearBoxSelection()
        {
            _selectedEditSeq = -1;
            foreach (var f in _letterFields)
                f.RemoveFromClassList("letter-box-selected");
        }

        // Enter submits, same as tapping the Submit button. Best-effort
        // only (same as before this rewrite) - a KeyDownEvent isn't
        // guaranteed to fire on-device for every key, but Enter has a
        // reliable fallback already: the physical Submit button.
        private void OnMasterInputKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                OnSubmitClicked();
        }

        // The only place keystrokes are handled at all now - see the
        // class comment above BuildLetterBoxes for why. Filters out
        // anything that isn't a plain letter (guards against mobile
        // autocapitalize/autocomplete/swipe-typing inserting spaces,
        // punctuation, or a whole suggested word - the same intent the
        // old per-box maxLength=1 guard had), upper-cases the rest to
        // match DisplayName's convention, and hard-caps the length at the
        // number of boxes still open to type into. Works the same for
        // typing (string grows) and backspacing (string shrinks) - both
        // are just "the string changed", which is why this never has an
        // undetectable case the way the old per-box design did.
        private void OnMasterInputChanged(ChangeEvent<string> evt)
        {
            Log($"[AnatomyPlayModeController] OnMasterInputChanged: raw='{evt.newValue}' (len {evt.newValue?.Length ?? 0}), prev='{_masterInputPrevValue}' (len {_masterInputPrevValue.Length}), maxLength={_masterInput?.maxLength}, selectedEditSeq={_selectedEditSeq}.");

            // Consume any armed echo-guard before doing anything else - it
            // only ever guards exactly one ChangeEvent (see the field
            // comment above _pendingEchoRaw), whether or not this is the
            // one it was waiting for.
            bool isDuplicateEcho = _pendingEchoRaw != null && evt.newValue == _pendingEchoRaw;
            if (isDuplicateEcho)
            {
                Log($"[AnatomyPlayModeController] OnMasterInputChanged: ignoring duplicate echo raw='{evt.newValue}' ...");
                SetMasterValue(_masterInputPrevValue);
                RenderLetterBoxesFromMaster();
                return; // still armed — keep guarding until a non-matching event arrives
            }
            _pendingEchoRaw = null; // this event is genuinely new; stop guarding

            string filtered = FilterMasterInputValue(evt.newValue);
            bool forceFieldRewrite = false;

            // A box was tapped (see SelectBoxForEdit) and is still waiting
            // for its overwrite character. _masterInput is never visibly
            // focused with a real on-screen cursor (see the class comment
            // above BuildLetterBoxes), so there's no cursor position to
            // trust - and on mobile there's no reliable length
            // relationship to _masterInputPrevValue either: the
            // Blur()+Focus() cycle used to redirect the keyboard back to
            // _masterInput after a box tap (see _letterRow's
            // PointerDownEvent handler in WireUi) severs the native
            // keyboard's connection to whatever text was already in the
            // field, so the very next ChangeEvent can arrive containing
            // ONLY the character(s) typed since that reset - e.g. raw="d"
            // even though _masterInputPrevValue is 7 characters long.
            // Comparing filtered.Length against _masterInputPrevValue.Length
            // (as if this were always "one longer than before") silently
            // fails that comparison and falls through to treating the
            // reset fragment as the WHOLE new guess, wiping out every
            // letter that wasn't the one just typed. The only thing we can
            // trust here is the actual last character that just arrived -
            // splice THAT into the selected box's position on top of
            // _masterInputPrevValue instead. An empty raw value (nothing
            // typed since the reset - e.g. backspace with nothing to
            // delete) just cancels the pending selection and falls through
            // to normal sequential editing.
            if (_selectedEditSeq >= 0)
            {
                if (filtered.Length > 0)
                {
                    char typed = filtered[filtered.Length - 1]; // the just-typed letter
                    var sb = new StringBuilder(_masterInputPrevValue);
                    if (_selectedEditSeq < sb.Length)
                        sb[_selectedEditSeq] = typed; // overwrite - the box already had a letter.
                    else
                        sb.Append(typed); // selected box was the next empty one - same as normal append.
                    filtered = sb.ToString();
                    if (filtered.Length > _editableBoxIndices.Count)
                        filtered = filtered.Substring(0, _editableBoxIndices.Count);
                    Log($"[AnatomyPlayModeController] OnMasterInputChanged: spliced '{typed}' into seq {_selectedEditSeq}, result='{filtered}'.");

                    // Arm the echo guard: the keyboard reliably re-fires this
                    // exact raw fragment one more time right after this event
                    // (see the field comment above _pendingEchoRaw) - without
                    // this, that next event would be mistaken for ordinary
                    // typing and wipe out the guess we just rebuilt.
                    _pendingEchoRaw = evt.newValue;
                }
                else
                {
                    filtered = _masterInputPrevValue; // nothing typed - keep the existing guess intact.
                    Log("[AnatomyPlayModeController] OnMasterInputChanged: a box was selected but nothing was typed (raw came back empty) - selection cancelled without an overwrite.");
                }

                ClearBoxSelection(); // one overwrite consumed (or cancelled) - back to normal typing.
                forceFieldRewrite = true; // field only holds the raw fragment - resync it to the spliced guess.
            }

            // PERFORMANCE / DROPPED-KEYSTROKE FIX: on a phone, every write into
            // _masterInput (SetValueWithoutNotify + SelectRange, see
            // SetMasterValue) is pushed into the native TouchScreenKeyboard
            // and restarts the IME's input connection - characters typed in
            // the meantime are silently lost when typing fast. So on ordinary
            // typing we leave the field alone and only record the logical
            // value; the field is rewritten only when it really disagrees with
            // it (junk characters, overflow past the box count) or when a
            // box overwrite/cancel just happened. Case differences alone
            // don't count: the field is hidden, so "cat" vs "CAT" is
            // irrelevant and _masterInputPrevValue is what the boxes show.
            string raw = evt.newValue ?? string.Empty;
            if (forceFieldRewrite || !string.Equals(raw, filtered, StringComparison.OrdinalIgnoreCase))
                SetMasterValue(filtered);
            else
                _masterInputPrevValue = filtered;

            RenderLetterBoxesFromMaster();
        }

        private string FilterMasterInputValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                if (char.IsLetter(ch))
                    sb.Append(char.ToUpperInvariant(ch));
            }

            if (sb.Length > _editableBoxIndices.Count)
                sb.Length = _editableBoxIndices.Count;

            return sb.ToString();
        }

        // Pushes _masterInput's current content out to the display-only
        // boxes: _masterInput.value[i] belongs to the box at
        // _editableBoxIndices[i]. Also highlights whichever box is next in
        // line to be typed into. Called after every keystroke and every
        // hint reveal.
        private void RenderLetterBoxesFromMaster()
        {
            string value = _masterInputPrevValue ?? string.Empty;

            for (int seq = 0; seq < _editableBoxIndices.Count; seq++)
            {
                var box = _letterFields[_editableBoxIndices[seq]];

                // Only touch boxes whose text actually changed: each
                // TextField.SetValueWithoutNotify re-generates the text mesh,
                // and doing that for EVERY box on EVERY keystroke is wasted
                // work on a phone (the guess can be 20+ boxes long).
                string want = seq < value.Length ? value[seq].ToString() : string.Empty;
                if (box.value != want)
                    box.SetValueWithoutNotify(want);

                bool active = seq == value.Length;
                if (box.ClassListContains("letter-box-active") != active)
                    box.EnableInClassList("letter-box-active", active);
            }
        }

        // Fills and locks the box for the hinted DisplayName index so it
        // can't be typed over, then highlights it - see OnHintClicked.
        // Also splices this box OUT of _editableBoxIndices/_masterInput's
        // string (removing its slot rather than leaving a gap), so a hint
        // can land on any not-yet-revealed box - already guessed (even
        // wrongly) or still blank - without leaving _masterInput's
        // remaining characters pointing at the wrong boxes afterward.
        private void RevealLetterField(int displayNameIndex, char c)
        {
            int fieldIndex = _letterFieldSourceIndex.IndexOf(displayNameIndex);
            if (fieldIndex < 0 || fieldIndex >= _letterFields.Count) return;

            int seq = _editableBoxIndices.IndexOf(fieldIndex);
            if (seq >= 0 && _masterInput != null)
            {
                string value = _masterInputPrevValue ?? string.Empty;
                if (seq < value.Length)
                    value = value.Remove(seq, 1);

                _editableBoxIndices.RemoveAt(seq);
                // Same +1 headroom as BuildLetterBoxes - see the comment
                // there for why capping this exactly at the box count
                // breaks the tap-to-overwrite path once the guess is full.
                _masterInput.maxLength = _editableBoxIndices.Count + 1;
                Log($"[AnatomyPlayModeController] RevealLetterField: box {fieldIndex} revealed, {_editableBoxIndices.Count} editable box(es) remain, _masterInput.maxLength set to {_masterInput.maxLength}.");
                SetMasterValue(value);

                // This box's slot in _editableBoxIndices is gone, so any
                // pending tap-to-overwrite selection may now point at the
                // wrong box (or be out of range entirely) - cancel it
                // rather than risk overwriting the wrong letter.
                ClearBoxSelection();
            }

            var field = _letterFields[fieldIndex];
            field.SetValueWithoutNotify(char.ToUpperInvariant(c).ToString());
            field.isReadOnly = true;
            field.AddToClassList("letter-box-revealed");
            field.pickingMode = PickingMode.Ignore; // no longer selectable - see BuildLetterBoxes.

            RenderLetterBoxesFromMaster();
        }

        // Empties every box that wasn't filled in by a hint, for another
        // attempt after an incorrect guess - see HandleIncorrectAnswer.
        // Hint-revealed boxes were already spliced out of
        // _editableBoxIndices by RevealLetterField, so clearing
        // _masterInput can't touch them.
        private void ClearUnrevealedLetterFields()
        {
            SetMasterValue(string.Empty);
            ClearBoxSelection();
            RenderLetterBoxesFromMaster();
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

        // Re-applies every hint letter already revealed for `key` before
        // this visit - read from AnatomyPlayModeLocalStorage, which by the
        // time this runs already reflects whatever's been merged down from
        // Firebase too (see AnatomyPlayModeSyncService.RequestFullSync).
        // Called right after BuildLetterBoxes for every still-unanswered
        // structure, so backing out and reselecting the same structure, or
        // closing and reopening the app/tab entirely, shows exactly the
        // letters already earned instead of a blank row. A structure with
        // no hints used yet simply gets nothing restored - identical to
        // how the letter row looked before this existed.
        //
        // Resets _revealedIndices/_currentHints first (rather than relying
        // on OnStructureSelected's earlier reset) so this method is safe
        // to treat as the single source of truth for "what's revealed
        // right now" - it fully re-derives that state from storage rather
        // than assuming it starts empty.
        private void RestoreRevealedHints(string key, string displayName)
        {
            _revealedIndices.Clear();
            _currentHints = 0;

            if (localStorage == null) return;

            foreach (int index in localStorage.GetRevealedIndices(key))
            {
                if (index < 0 || index >= displayName.Length || char.IsWhiteSpace(displayName[index]))
                    continue; // stale/out-of-range data - skip rather than crash the letter row.

                _revealedIndices.Add(index);
                _currentHints++;
                RevealLetterField(index, displayName[index]);
            }
        }

        // ===== Hints =====

        private void OnHintClicked()
        {
            // No hints during a formal assessment - the button is hidden
            // entirely in ActivatePlayMode, this is just a hard backstop.
            if (_isBaselineMode) return;

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

            if (candidates.Count == 0) return; // fully revealed already - never spend a daily hint on this.

            // Per-system daily hint limit (3 per AnatomySystem per student
            // per day - see AnatomyPlayModeLocalStorage.TryUseHint). Checked
            // only once we know a hint would actually reveal something -
            // if the student is already at the limit for the currently
            // active system, no hint is granted and no letter is revealed.
            if (localStorage == null || !localStorage.TryUseHint(CurrentStudentId, _screen.CurrentSystem, out var hintRecord))
            {
                // TryUseHint only fails for one reason: today's per-system
                // hint limit is already used up (see its doc comment) - so
                // getting here always means "no hints left", never some
                // other kind of failure. Tell the student when they'll get
                // more, since the button no longer explains itself by going
                // disabled/greyed-out (see RefreshHintButtonState).
                ShowNoHintsPopup();
                RefreshHintButtonState();
                return;
            }

            // Pick a random not-yet-revealed position rather than always
            // the leftmost one, so hints don't just fill the word in
            // left-to-right order.
            int pickedIndex = candidates[UnityEngine.Random.Range(0, candidates.Count)];

            _revealedIndices.Add(pickedIndex);
            _currentHints++;
            _hintsUsedTotal++;

            RevealLetterField(pickedIndex, displayName[pickedIndex]);

            // Fire-and-forget sync, same pattern as SaveAnswer's direct
            // Firebase call elsewhere in this class - the hint was already
            // saved locally by TryUseHint above, so a failed/offline sync
            // here never blocks or loses the hint; AnatomyPlayModeSyncService
            // retries it later from GetPendingHintUses().
            firebase?.SyncHintUse(hintRecord, _ => { });

            // Persist WHICH letter this hint revealed (not just that a hint
            // was spent) - separate from hintRecord above, which only
            // counts toward the daily per-system limit. This is what lets
            // RestoreRevealedHints bring the letter row back exactly as
            // it's left, whether the student backs out and reselects this
            // structure or closes and reopens the app entirely. Same
            // fire-and-forget sync pattern as SyncHintUse: saved locally
            // first (always succeeds), Firebase attempted after but never
            // required - a failed/offline sync just leaves it 'pending'
            // for AnatomyPlayModeSyncService to retry later.
            if (localStorage != null)
            {
                var revealedRecord = localStorage.SaveRevealedIndex(CurrentStudentId, _currentQuestion.boneName, pickedIndex);
                firebase?.SyncRevealedHint(revealedRecord, _ => { });
            }

            RefreshHintButtonState();
        }

        // Re-evaluates whether the Hint button should be enabled for the
        // currently active system, and updates its label accordingly.
        // Called after every hint use, and whenever a new question is set
        // up (OnStructureSelected) so a student who already hit today's
        // limit on this system sees "No more hints" immediately on
        // opening a new, unanswered question - not only after tapping
        // Hint and being denied.
        private void RefreshHintButtonState()
        {
            if (_hintButton == null || _screen == null || localStorage == null) return;

            bool limitReached = localStorage.GetHintCountToday(_screen.CurrentSystem) >= AnatomyPlayModeLocalStorage.MaxHintsPerSystemPerDay;
            // Update the existing child label, and make sure the button's own
            // text stays empty so nothing is drawn on top of it.
            if (_hintButtonLabel != null)
                _hintButtonLabel.text = limitReached ? "No more hints" : "Hint";
            if (!string.IsNullOrEmpty(_hintButton.text))
                _hintButton.text = string.Empty;

            // Deliberately left enabled even at the limit (unlike the old
            // SetEnabled(!limitReached)) - a disabled button can't be
            // tapped, so it could never explain itself. Leaving it enabled
            // lets OnHintClicked's TryUseHint failure path show
            // ShowNoHintsPopup() with the reset time instead of the button
            // just going dead with no explanation.
            _hintButton.SetEnabled(true);
        }

        // Builds and shows the "no hints left today" popup, filling in the
        // exact local time hints reset - hints reset at local midnight
        // (see AnatomyPlayModeLocalStorage.GetHintCountToday, which compares
        // DateTime.Now.Date), so that's the time shown here.
        private void ShowNoHintsPopup()
        {
            if (_noHintsPanel == null || _noHintsLabel == null) return;

            DateTime nextResetLocal = DateTime.Now.Date.AddDays(1);
            _noHintsLabel.text =
                $"You've used all your hints for this system today. They'll refresh at {nextResetLocal:h:mm tt} ({nextResetLocal:MMM d}).";
            _noHintsPanel.RemoveFromClassList("hidden");
        }

        private void HideNoHintsPanel()
        {
            _noHintsPanel?.AddToClassList("hidden");
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

            int questionPoints = pointsPerCorrectAnswer;

            _screen.SetInfoPanelTitle(entry.displayName);
            _screen.SetInfoPanelDescription($"Correct! +{questionPoints} points.");
            SetGuessUiEnabled(false);
            _letterRow?.AddToClassList("hidden");

            if (_isBaselineMode)
            {
                // Separate counters, and deliberately NO SaveCorrectAnswer/
                // CleanupRevealedHints call - a baseline attempt must never
                // touch the student's real Play Mode points, completed-
                // structures list, or Firestore/local-storage records (see
                // the class-level comment on _isBaselineMode). _completedKeys
                // is still used here since CheckForCompletion/UpdateProgressLabel
                // already key off it and it was cleared fresh in ActivatePlayMode.
                _baselineCorrectCount++;
                _baselinePoints += questionPoints;
                _completedKeys.Add(info.boneName);

                _onBaselineStructureIdentified?.Invoke(info.boneName);
                UpdateProgressLabel();
                CheckForCompletion();
                return;
            }

            _totalPoints += questionPoints;
            _correctAnswers++;
            _completedKeys.Add(info.boneName);
            CleanupRevealedHints(info.boneName);

            SaveCorrectAnswer(info.boneName, entry.displayName, questionPoints);

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
        private void SaveCorrectAnswer(string key, string displayName, int pointsEarned)
        {
            string studentId = CurrentStudentId;
            if (localStorage == null || string.IsNullOrEmpty(studentId))
            {
                // No offline storage available (or no signed-in student) -
                // fall back to the original direct-to-Firebase save so Play
                // Mode still works, just without offline/restore support.
                firebase?.SaveAnswer(key, displayName, true, _currentHints, pointsEarned);
                return;
            }

            var record = localStorage.SaveAnswer(
                studentId, key, displayName, true, _currentHints, pointsEarned);

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

        // Once a structure is correctly answered, its revealed-hint
        // letters are no longer needed - it's never asked again, so
        // there's nothing left for RestoreRevealedHints to restore.
        // Removes the local records immediately (always succeeds,
        // regardless of connectivity) and best-effort deletes their
        // Firestore docs too, so the remote collection doesn't grow
        // forever with data for structures that are already done. A
        // failed/offline delete is harmless: MergeRemoteRevealedHint
        // already skips restoring hints for any key this device has
        // correctly answered, so an orphaned doc is silently ignored
        // rather than causing stale letters to reappear later.
        private void CleanupRevealedHints(string key)
        {
            if (localStorage == null) return;

            var removed = localStorage.ClearRevealedHints(key);
            if (firebase == null) return;

            foreach (var record in removed)
                firebase.DeleteRevealedHint(record, _ => { });
        }

        private void HandleIncorrectAnswer()
        {
            if (!_isBaselineMode)
                _incorrectAnswers++;

            if (_screen.TryGetBoneDatabaseEntry(_currentQuestion, out var entry))
            {
                _screen.SetInfoPanelDescription("Not quite - try again.");

                // Baseline attempts never write to the real Play Mode
                // Firestore log - see the class-level comment on
                // _isBaselineMode.
                if (!_isBaselineMode)
                    firebase?.SaveAnswer(_currentQuestion.boneName, entry.displayName, false, _currentHints, 0);
            }

            ClearUnrevealedLetterFields();
            _masterInput?.Focus();
            // Structure stays visible, another attempt is allowed - guess UI stays enabled.
        }

        // ===== Progress / completion =====

        private void UpdateProgressLabel()
        {
            if (_progressLabel == null) return;
            int total = _isBaselineMode ? _baselineTargetKeys.Count : _screen.AllBoneData.Count;
            _progressLabel.text = $"Progress: {_completedKeys.Count} / {total}";
        }

        private void CheckForCompletion()
        {
            if (_isBaselineMode)
            {
                int baselineTotal = _baselineTargetKeys.Count;
                if (baselineTotal == 0 || _baselineCorrectCount < baselineTotal) return;

                // No completion panel here - BaselineAssessmentController owns
                // what the student sees next (it records the attempt, then
                // navigates away) via the callback below. DeactivatePlayMode
                // is NOT called here: the controller decides when to leave
                // Play Mode, same as normal completion leaves the panel up
                // until the student closes it themselves.
                var callback = _onBaselineCompleted;
                int correctCount = _baselineCorrectCount;
                int points = _baselinePoints;
                _onBaselineCompleted = null; // fire exactly once
                callback?.Invoke(correctCount, baselineTotal, points);
                return;
            }

            int total = _screen.AllBoneData.Count;
            if (total == 0 || _completedKeys.Count < total) return;

            if (_completionPanel != null && _completionLabel != null)
            {
                _completionLabel.text =
                    $"ANATOMY COMPLETE!\nYou identified {_completedKeys.Count} / {total} structures\n" +
                    $"Total Points: {_totalPoints}";
                _completionPanel.RemoveFromClassList("hidden");
            }
        }

        private void HideCompletionPanel()
        {
            _completionPanel?.AddToClassList("hidden");
        }
    }
}
