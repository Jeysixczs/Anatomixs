using Anatomia3D.UI;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;


// The anatomy systems this one reusable Anatomy Screen can display. Adding a
// new system means adding a new enum value here and a matching
// AnatomySystemConfig entry in the Inspector - never a new screen/controller.
public enum AnatomySystem
{
    Skeletal,
    Muscular,
    Cardiovascular
}

// Associates one AnatomySystem with the 3D model and database JSON that
// should become active when that system is selected. Assigned in the
// Inspector on AnatomyScreenController.anatomySystems - see the class
// comment there for what each field maps to.
[System.Serializable]
public class AnatomySystemConfig
{
    public AnatomySystem system;

    [Tooltip("Root Transform of this system's 3D model - parent of all its per-part FBX pieces, exactly the same role skeletonRoot already plays for the skeletal model. Becomes the active skeletonRoot whenever this system is selected.")]
    public Transform modelRoot;

    [Tooltip("This system's database JSON (TextAsset), same format as BoneDatabase.json (a dictionary of part-name -> displayName/baseName/description). Becomes the active boneDatabaseJson whenever this system is selected.")]
    public TextAsset databaseJson;

    [Tooltip("Optional. Text shown in the header subtitle (AppSubtitle) while this system is active, e.g. 'SKELETAL SYSTEM'. Leave blank to auto-generate from the system name.")]
    public string subtitleText;
}

[RequireComponent(typeof(UIDocument))]
public class AnatomyScreenController : MonoBehaviour
{
    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;

    [Header("Bone Outline")]
    [Tooltip("Builds/clears the selected bone's mesh-silhouette outline (see BoneOutlineController). Sizes the outline from the bone's actual combined renderer bounds - never from transform.localScale or a fixed size.")]
    [SerializeField] private BoneOutlineController boneOutlineController;

    [System.Serializable]
    public class BoneInfo
    {
        public string boneName;      // exact GameObject name - this IS the BoneDatabase.json key/ID used to look up this bone's data. Never matched by displayName or baseName.
        public string title;
        public string baseName;
        [TextArea] public string description;
        public AudioClip audioClip;

        // Resolved at runtime from skeletonRoot by matching child GameObject
        // name to boneName (see OnEnable). Not set in the Inspector.
        [System.NonSerialized] public Transform worldBone;

        // Rotation this bone had when first resolved (used to fully restore
        // it on Reset) and the rotation the single-finger swipe gesture is
        // currently driving it toward (see UpdateSingleFingerGesture /
        // Update). Smoothing the actual transform toward targetRotation
        // every frame, rather than snapping it directly, is what makes the
        // swipe feel smooth instead of jumpy.
        [System.NonSerialized] public Quaternion originalRotation;
        [System.NonSerialized] public Quaternion targetRotation;
    }

    [Header("Bone Data")]
    [Tooltip("BoneDatabase.json imported as a TextAsset. The single source of truth for every bone's display name and description - no bone names are hardcoded in this script. When 'Anatomy Systems' below has an entry for the currently selected system, this is overwritten with that entry's Database Json every time the screen opens - assign per-system databases there instead of here.")]
    [SerializeField] private TextAsset boneDatabaseJson;

    [Header("Anatomy Systems")]
    [Tooltip("One entry per selectable anatomy system (Skeletal / Muscular / Cardiovascular). Whichever system was selected on the Student Explore 3D card list (see StudentExplore3dController/UIManager.ShowStudentAnatomyScreen) has its Model Root and Database Json copied into skeletonRoot/boneDatabaseJson above every time this screen opens - see ResolveAnatomySystem. Every OTHER entry's Model Root is deactivated so no previous system's model stays visible.")]
    [SerializeField] private List<AnatomySystemConfig> anatomySystems = new List<AnatomySystemConfig>();

    [Tooltip("Which system loads if the screen is opened without a system having been selected first (e.g. testing the scene directly in the Editor). Normal play always goes through SetAnatomySystem via UIManager.ShowStudentAnatomyScreen(system).")]
    [SerializeField] private AnatomySystem defaultAnatomySystem = AnatomySystem.Skeletal;

    // Set by SetAnatomySystem (called from UIManager.ShowStudentAnatomyScreen
    // BEFORE this controller's GameObject/component is (re-)enabled) so
    // ResolveAnatomySystem knows which system to load the instant OnEnable
    // runs - see the class-level lifecycle note on ResolveAnatomySystem.
    private AnatomySystem _pendingSystem = AnatomySystem.Skeletal;
    private bool _hasPendingSystem;
    private AnatomySystem _currentSystem;

    // Called by UIManager.ShowStudentAnatomyScreen(AnatomySystem) - see
    // UIManager.cs - before this controller is (re-)enabled, so the correct
    // model/database is already known the moment OnEnable resolves it.
    public void SetAnatomySystem(AnatomySystem system)
    {
        _pendingSystem = system;
        _hasPendingSystem = true;
    }

    // Read-only access to the currently active system, resolved by
    // ResolveAnatomySystem in OnEnable. AnatomyPlayModeController reads
    // this to scope the per-system daily hint limit (see
    // AnatomyPlayModeLocalStorage.TryUseHint) to whichever system the
    // student is actually looking at right now.
    public AnatomySystem CurrentSystem => _currentSystem;

    // Loaded once in OnEnable from boneDatabaseJson (see PopulateBoneDataFromSkeleton).
    // boneData is built at runtime too, one entry per direct child of
    // skeletonRoot, with its title/description resolved from
    // boneDatabaseJson - so adding, renaming, or removing a bone only ever
    // requires editing the 3D model and BoneDatabase.json, never this script.
    private readonly BoneDatabaseService _boneDatabaseService = new BoneDatabaseService();
    private readonly List<BoneInfo> boneData = new List<BoneInfo>();

    // Every bone Transform found under skeletonRoot during
    // PopulateBoneDataFromSkeleton, keyed by its exact GameObject name.
    // Built with a single recursive walk of the whole subtree - NOT just
    // skeletonRoot's direct children - because Transform.Find(name) (with
    // no '/') only searches direct children and silently returns null for
    // anything nested under an extra grouping node, which would otherwise
    // leave every BoneInfo.worldBone null and prevent any collider from
    // ever being created (breaking mesh-tap selection entirely).
    private readonly Dictionary<string, Transform> _boneTransformsByName = new Dictionary<string, Transform>();

    // ===== Combined systems (show more than one model at once) =====
    // Normally exactly one anatomy system's model is active and skeletonRoot
    // points at it (see ResolveAnatomySystem). When this list is non-empty,
    // every system in it is activated TOGETHER and treated as one model:
    // bones are collected from all of their roots, all of their databases are
    // loaded (additively), the camera frames all of them, and taps can land on
    // any of them. skeletonRoot still points at the FIRST system in the list,
    // since it remains the orbit/back-navigation anchor.
    //
    // Used by BaselineAssessmentController so the Pretest/Posttest can show
    // Skeletal + Muscular + Cardiovascular at the same time. This is only
    // affordable together with a structure filter (below) - a combined open
    // with no filter would build all three models in full.
    //
    // Set BEFORE this controller is enabled, same lifecycle requirement as
    // SetAnatomySystem/SetStructureFilter.
    private readonly List<AnatomySystem> _combinedSystems = new List<AnatomySystem>();

    // The model roots this open is actually working across - one entry
    // (skeletonRoot) in the normal single-system case, one per combined system
    // otherwise. Rebuilt by ResolveAnatomySystem on every open; everything that
    // used to walk skeletonRoot directly (bone collection, filter visibility,
    // auto-framing, back-navigation teardown) walks this instead.
    private readonly List<Transform> _activeRoots = new List<Transform>();

    /// <summary>Shows every listed system's model simultaneously on the next
    /// open, instead of just one. Must be called BEFORE this controller is
    /// enabled - see _combinedSystems. Pass null or an empty list for the
    /// normal one-system-at-a-time behavior.</summary>
    public void SetCombinedSystems(IEnumerable<AnatomySystem> systems)
    {
        _combinedSystems.Clear();
        if (systems == null) return;

        foreach (var system in systems)
            if (!_combinedSystems.Contains(system))
                _combinedSystems.Add(system);
    }

    /// <summary>Back to one active system per open. Callers that set combined
    /// systems are responsible for clearing them once their mode ends, or a
    /// later Explore/Play Mode visit would still bring up all three models.</summary>
    public void ClearCombinedSystems() => _combinedSystems.Clear();

    public bool HasCombinedSystems => _combinedSystems.Count > 0;

    // ===== Structure filter (Pretest/Posttest "load only these structures") =====
    // When non-empty, this model is treated as if it contained ONLY these
    // structures: PopulateBoneDataFromSkeleton registers nothing else (so
    // EnsureBoneCollider never generates the hundreds of non-convex
    // MeshColliders that make opening a full model lag), and
    // ApplyStructureFilterVisibility switches off every other piece's
    // renderer/collider so only these are drawn and tappable.
    //
    // Set by BaselineAssessmentController (via UIManager) BEFORE this
    // controller is re-enabled, for the same reason SetAnatomySystem is -
    // OnEnable is what builds boneData, so a filter arriving afterward
    // would be too late to save the work. Keys are normalized through
    // BoneDatabaseService.NormalizeKey, matching how every other lookup
    // in this class compares structure names.
    private HashSet<string> _structureFilter;

    // Exactly the renderers/colliders ApplyStructureFilterVisibility turned
    // off, so ClearStructureFilter can restore those and only those - never
    // blanket-enabling pieces that were already off for some other reason
    // (Hide, Isolate, a previous mode).
    private readonly List<Renderer> _filterHiddenRenderers = new List<Renderer>();
    private readonly List<Collider> _filterHiddenColliders = new List<Collider>();

    // Set by ClearStructureFilter(deferRestore: true): the lists above are kept
    // as-is and re-enabled at the START of the next OnEnable instead of right
    // now (see RestorePendingFilterVisibility). Re-enabling hundreds of
    // renderers is work the frame a mandatory assessment is being submitted
    // does not need to do - especially since the models are being hidden at the
    // same moment anyway, so nothing would render differently for it.
    private bool _filterRestorePending;

    /// <summary>Restricts this model to just the named structures for the next
    /// (and every subsequent) open, until ClearStructureFilter is called. Must
    /// be called BEFORE this controller is enabled - see _structureFilter.
    /// Passing null or an empty set is the same as clearing the filter.</summary>
    public void SetStructureFilter(IEnumerable<string> structureKeys)
    {
        ClearStructureFilter();
        if (structureKeys == null) return;

        var keys = new HashSet<string>();
        foreach (var key in structureKeys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            keys.Add(BoneDatabaseService.NormalizeKey(key));
        }

        if (keys.Count == 0) return;
        _structureFilter = keys;
    }

    /// <summary>Lifts the restriction and restores every renderer/collider
    /// ApplyStructureFilterVisibility switched off. Callers that set a filter
    /// are responsible for clearing it once their mode ends - otherwise the
    /// next Explore/Play Mode visit to that same system would still show only
    /// those few structures.</summary>
    /// <param name="deferRestore">Pass true to hand the restore work to the next
    /// open instead of doing it here - the filter stops applying immediately
    /// either way, but the renderers/colliders it switched off are only switched
    /// back on at the start of the next OnEnable. Use this when leaving the
    /// screen (the models are about to be hidden, so nothing is visibly
    /// different) and the current frame is already busy - see
    /// BaselineAssessmentController.OnAssessmentCompleted.</param>
    public void ClearStructureFilter(bool deferRestore = false)
    {
        _structureFilter = null;

        if (deferRestore)
        {
            _filterRestorePending = _filterHiddenRenderers.Count > 0 || _filterHiddenColliders.Count > 0;
            return;
        }

        RestoreFilterVisibility();
    }

    // The actual restore, shared by ClearStructureFilter and
    // RestorePendingFilterVisibility. Only ever touches what
    // ApplyStructureFilterVisibility itself switched off.
    private void RestoreFilterVisibility()
    {
        foreach (var rend in _filterHiddenRenderers)
            if (rend != null) rend.enabled = true;
        foreach (var col in _filterHiddenColliders)
            if (col != null) col.enabled = true;

        _filterHiddenRenderers.Clear();
        _filterHiddenColliders.Clear();
        _filterRestorePending = false;
    }

    // Runs first thing in OnEnable, before ResolveAnatomySystem/
    // PopulateBoneDataFromSkeleton - so a deferred restore is always paid off
    // before this open decides what to build, and a fresh filter for THIS open
    // is never confused with the previous one's leftovers.
    private void RestorePendingFilterVisibility()
    {
        if (!_filterRestorePending) return;
        RestoreFilterVisibility();
    }

    /// <summary>Deactivates every model root this open was working across - the
    /// same thing OnBackClicked does, exposed for modes that navigate away
    /// WITHOUT going through the Back button (see
    /// BaselineAssessmentController.OnAssessmentCompleted). The model camera
    /// renders these roots whether or not this screen's own UI is showing, so a
    /// mode that leaves them active leaves three full models being rendered
    /// behind whatever screen comes next.</summary>
    public void HideActiveModels()
    {
        foreach (var root in _activeRoots)
        {
            if (root != null) root.gameObject.SetActive(false);
        }
    }

    public bool HasStructureFilter => _structureFilter != null && _structureFilter.Count > 0;

    // Shown in the Info Panel for a clicked bone whose GameObject name has
    // no matching entry in BoneDatabase.json.
    private const string BoneInfoNotAvailableText = "Bone information not available.";

    private VisualElement _root;

    // ===== 3D Model View =====
    // A dedicated camera renders skeletonRoot (on its own culling-mask layer)
    // into modelRenderTexture, which is displayed as BodyArea's background
    // image. The camera orbits around skeletonRoot's position (see
    // ApplyOrbitCamera below); the model itself stays static. Bone hotspot
    // buttons are repositioned every frame from each BoneInfo.worldBone's
    // projected screen position, so they track the camera as it orbits/zooms.
    [Header("3D Model View")]
    [SerializeField] private Camera modelCamera;
    [SerializeField] private RenderTexture modelRenderTexture;
    [SerializeField] private Transform skeletonRoot; // parent of all per-bone FBX pieces; the orbit pivot
    [SerializeField] private float rotateSpeed = 0.3f;
    [SerializeField] private float zoomSpeed = 0.02f;
    [SerializeField] private Vector2 zoomDistanceRange = new Vector2(1.5f, 6f);
    // Extra breathing room around the model when auto-framing on start, as a
    // multiplier on the tightest distance that fits all bone renderers in
    // view. 1.0 = bones touch the frame edges; higher = more margin.
    [SerializeField] private float autoFramePadding = 1.12f;

    // ===== Selected-bone focus/zoom =====
    // Selecting a bone (see OnBoneClicked / OnBoneColliderClicked) smoothly
    // moves the camera in to frame just that bone, using the exact same
    // "fit the bounds into the vertical FOV" math as the whole-skeleton
    // auto-frame above (see ComputeSkeletonBounds/autoFramePadding), just
    // computed from the one bone's Renderer.bounds instead of the whole
    // skeleton's. Only the camera moves - see FocusOnSelectedBone; the
    // bone's own transform is never touched.
    [Header("Selected Bone Focus/Zoom")]
    [Tooltip("Multiplier on the tightest distance that fits the selected bone's bounds into the camera's vertical FOV - 1.0 hugs the bone tightly, higher values back the camera off further. Same role as autoFramePadding above, but tunable separately so focusing on a bone doesn't have to use the same padding as the initial whole-skeleton view.")]
    [SerializeField] private float selectedBoneZoomDistance = 2.0f;
    [Tooltip("How long (seconds) the camera takes to smoothly move/zoom to a re-tapped selected bone.")]
    [SerializeField] private float selectedBoneZoomDuration = 0.5f;
    [Tooltip("Closest the focus camera is ever allowed to get to the selected bone, regardless of how small the bone's bounds are - prevents clipping through tiny bones.")]
    [SerializeField] private float selectedBoneMinZoomDistance = 0.5f;
    [Tooltip("Farthest the focus camera is ever allowed to sit from the selected bone, regardless of how large the bone's bounds are.")]
    [SerializeField] private float selectedBoneMaxZoomDistance = 4f;

    // Drives the focus animation across selectedBoneZoomDuration seconds
    // (see Update()/FocusOnSelectedBone) - separate from the generic
    // _targetPanOffset/_targetOrbitDistance SmoothDamp chase used for
    // gestures, since that has no fixed duration and reuses a much
    // snappier smoothing time (gestureSmoothTime) tuned for finger input,
    // not a deliberate camera move.
    private bool _isFocusingBone;
    private float _focusElapsed;
    private Vector3 _focusStartPanOffset;
    private Vector3 _focusTargetPanOffset;
    private float _focusStartDistance;
    private float _focusTargetDistance;

    // World-space centroid of all bone renderers' bounds, computed once in
    // OnEnable. The orbit camera always looks at this point, not
    // skeletonRoot.position directly — keeps framing correct even if the
    // model's pivot isn't centered on the geometry.
    private Vector3 _orbitPivot;

    private VisualElement _bodyArea;
    private BoneInfo _selectedBone;
    private readonly Stack<System.Action> _undoStack = new Stack<System.Action>();

    // ===== Play Mode integration hook =====
    // Fired at the very end of SelectStructure - after info panel, camera
    // focus, outline, isolate-sync and hide-mode have already run - so a
    // subscriber (AnatomyPlayModeController) always observes the fully
    // settled selection state, the same one the player sees on screen.
    // AnatomyScreenController never knows Play Mode exists beyond this
    // one event and the public accessors below it in the class.
    public event System.Action<BoneInfo> OnStructureSelected;

    // Fires at the very end of every OnEnable pass - i.e. every single time
    // this screen (re)opens with a freshly cloned UXML tree, not just the
    // first time this component's own OnEnable ever ran. AnatomyPlayModeController
    // subscribes to this once and re-queries/re-wires its own UI (letter boxes,
    // hint/submit, Isolate Answered, etc.) from the handler - its own
    // MonoBehaviour OnEnable is not a reliable re-wiring hook, since UIManager
    // only toggles THIS controller's enabled flag when the screen is shown,
    // never AnatomyPlayModeController's.
    public event System.Action OnScreenReady;

    // ===== Teacher structure-selection integration hook =====
    // Optional override for where the Back button navigates. Null (default) keeps
    // the normal behavior - back to Student Explore 3D. A sibling controller (see
    // AnatomyTeacherSelectionController) sets this while its own teacher-only
    // selection mode is active, so Back returns to Admin Quiz Management instead -
    // same "this class stays completely agnostic of the caller" approach already
    // used for OnStructureSelected/OnScreenReady above. The sibling controller is
    // responsible for clearing this back to null once its mode ends.
    public System.Action BackNavigationOverride;

    // Fired at the end of OnResetClicked, after it has already re-enabled every
    // bone's renderer/collider (Reset's normal, correct behavior for ordinary
    // Explore/Play Mode). A sibling controller running a restricted mode that
    // isolates the model down to a fixed subset (currently only
    // BaselineAssessmentController's pretest/posttest) subscribes here to
    // re-apply that isolation immediately after - otherwise a student tapping
    // Reset mid-test would suddenly see and be able to tap the entire skeleton.
    // Same "this class stays completely agnostic of the caller" approach as
    // BackNavigationOverride; the subscriber is responsible for unsubscribing
    // once its mode ends.
    public System.Action AfterReset;

    // Whether Isolate is currently active, so a second tap of the same
    // button toggles it back off instead of stacking another isolate on
    // top. _isolateUndo is the exact same delegate handed to PushUndo when
    // isolating - kept separately so toggling off can invoke it directly
    // (and pop it off _undoStack if it's still the top entry) rather than
    // duplicating the restore logic.
    private bool _isIsolated;
    private bool _isHideModeActive;
    private System.Action _isolateUndo;

    // Orbit camera state: the camera moves around _orbitPivot (plus
    // _panOffset - see below) on a sphere defined by yaw/pitch/distance,
    // always looking at the pivot. Neither skeletonRoot nor the bone
    // meshes themselves ever move or rotate.
    private float _orbitYaw;
    private float _orbitPitch;
    private float _orbitDistance;
    private const float MinPitch = -80f;
    private const float MaxPitch = 80f;

    private float _initialYaw;
    private float _initialPitch;
    private float _initialDistance;

    [Header("Touch Gesture Controls")]
    [Tooltip("Degrees the selected bone rotates per pixel of single-finger swipe.")]
    [SerializeField] private float boneRotateSensitivity = 0.25f;
    [Tooltip("World units the camera pans per pixel of two-finger parallel movement, scaled by current zoom distance so the pan speed feels consistent whether zoomed in or out.")]
    [SerializeField] private float cameraPanSensitivity = 0.0025f;
    [Tooltip("How strongly a two-finger pinch/spread changes zoom distance per pixel of finger-spread change. Independent of cameraPanSensitivity and of the mouse-wheel zoomSpeed above.")]
    [SerializeField] private float pinchZoomSensitivity = 0.02f;
    [Tooltip("Smoothing time (seconds) for zoom, pan, and bone-rotation. Lower = snappier/more direct, higher = smoother/more lag.")]
    [SerializeField] private float gestureSmoothTime = 0.08f;

    // Every currently-touching finger's last known position, keyed by
    // pointerId. The Count of this dictionary is what tells single-finger
    // gestures (orbit/bone-rotate) and two-finger gestures (pinch/pan) apart
    // - see OnBodyAreaPointerDown/Move/Up.
    private readonly Dictionary<int, Vector2> _activePointers = new Dictionary<int, Vector2>();

    private bool _isOrbiting;      // single finger, no bone selected: orbit the camera
    private bool _isSwipingBone;   // single finger, a bone selected: rotate that bone
    private bool _isTwoFingerGesture;

    private float _pinchPrevDistance;
    private Vector2 _twoFingerPrevMidpoint;

    // ===== Bone collider tap-to-select =====
    // Separate from the hotspot-button click system (OnBoneClicked) below -
    // this lets a tap land directly on the bone mesh itself in the 3D view,
    // via a physics raycast through modelCamera, rather than only on the
    // small tracked hotspot button. A single-finger press only counts as a
    // "tap" (not the start of an orbit/bone-swipe drag) if it lifts again
    // before moving more than TapMovementThreshold pixels.
    [Header("Bone Collider Tap")]
    [Tooltip("Layers the tap raycast will hit. Should match whatever layer the bone meshes are on (e.g. AnatomyModel).")]
    [SerializeField] private LayerMask boneRaycastLayerMask = ~0;
    private const float TapMovementThreshold = 10f;
    private Vector2 _singleFingerDownPos;
    private bool _singleFingerMightBeTap;
    private readonly Dictionary<Collider, BoneInfo> _infoByCollider = new Dictionary<Collider, BoneInfo>();

    // The exact GameObject the current selection resolved to: hit.collider.gameObject
    // for a direct mesh tap (see TryPickBoneAt), or info.worldBone.gameObject
    // for hotspot/search selections (which have no raycast hit to point
    // at). Kept as a plain, direct reference specifically so nothing about
    // outlining ever has to re-derive "what got clicked" from a name or
    // database-ID lookup - it's always exactly this GameObject.
    private GameObject selectedGameObject;

    // Every Transform that is itself SOME bone's worldBone (i.e. every
    // value in _boneTransformsByName), built alongside it in
    // PopulateBoneDataFromSkeleton. Used by EnsureBoneCollider to tell
    // "a mesh piece that genuinely belongs to this bone" apart from "a
    // mesh piece that belongs to a separately registered descendant
    // bone" - see IsOwnedByAnotherBone.
    private readonly HashSet<Transform> _boneTransformSet = new HashSet<Transform>();

    // Gesture-driven targets that _orbitDistance / _panOffset smoothly chase
    // every frame in Update() (via Mathf/Vector3.SmoothDamp), instead of
    // snapping straight to each raw pointer-move delta.
    private float _targetOrbitDistance;
    private float _orbitDistanceVelocity; // SmoothDamp internal state

    // Accumulated two-finger-pan offset, added on top of the auto-framed
    // _orbitPivot (see ApplyOrbitCamera). Reset back to zero by the Reset
    // button, same as yaw/pitch/distance.
    private Vector3 _panOffset;
    private Vector3 _targetPanOffset;
    private Vector3 _panOffsetVelocity; // SmoothDamp internal state

    // Info panel refs
    private VisualElement _infoPanel;
    private Label _titleLabel;
    private Label _descriptionLabel;
    private Button _closeButton;
    private Button _minimizeButton;
    private Button _playButton;
    private Button _stopButton;
    private Label _audioLabel;
    private VisualElement _audioRow;
    private VisualElement _audioIcon;      // small speaker-badge to the left of "AUDIO GUIDE"
    private Label _audioIconLabel;
    private VisualElement _audioControlsTray; // white pill container the Play/Stop buttons sit inside

    // Audio row responsive sizing: the play/stop buttons, their glyph font
    // size, the gap between them, the icon badge, the "AUDIO GUIDE" label,
    // the button tray, and the row's own padding all scale with the info
    // panel's current on-screen WIDTH (see UpdateAudioRowScale) instead of
    // sitting at one fixed pixel size - the panel can be anywhere from
    // infoPanelMinSize.x up to audioRowReferenceMaxWidth (drag-resized by
    // the player, or clamped narrower on a small screen), and a 460px-wide
    // panel has no room for the same 60px buttons and 32/40px padding a
    // 900px-wide panel does. The *Max values below are what
    // .audio-row/.audio-icon/.audio-btn/.audio-label in the USS start out
    // at - the controller only ever scales them down from there, never up
    // past the USS/Inspector authored size.
    [Header("Audio Row Responsive Sizing")]
    [SerializeField] private float audioButtonSizeMax = 60f;
    // 44px, not 40px: keeps Play/Stop at (or above) the ~44pt comfortable
    // mobile touch-target size even at the panel's narrowest, rather than
    // letting the button shrink purely for visual proportion.
    [SerializeField] private float audioButtonSizeMin = 44f;
    [SerializeField] private float audioButtonFontSizeMax = 22f;
    [SerializeField] private float audioButtonFontSizeMin = 15f;
    [SerializeField] private float audioButtonSpacingMax = 14f; // gap between Play and Stop
    [SerializeField] private float audioButtonSpacingMin = 8f;
    [SerializeField] private float audioLabelFontSizeMax = 20f;
    [SerializeField] private float audioLabelFontSizeMin = 14f;

    // Speaker-badge to the left of the label (echoes the header's
    // .info-icon badge treatment).
    [SerializeField] private float audioIconSizeMax = 36f;
    [SerializeField] private float audioIconSizeMin = 26f;
    [SerializeField] private float audioIconFontSizeMax = 18f;
    [SerializeField] private float audioIconFontSizeMin = 13f;

    // White pill tray around the Play/Stop buttons. Its border-radius is
    // NOT one of these - it's always computed as exactly half the tray's
    // own resolved height (buttonSize + 2*trayPadding) in
    // UpdateAudioRowScale, so it stays a true pill at every scale instead
    // of drifting out of sync with a separately-lerped radius value.
    [SerializeField] private float audioControlsTrayPaddingMax = 6f;
    [SerializeField] private float audioControlsTrayPaddingMin = 3f;

    // Row padding, left/top: purely cosmetic breathing room, free to
    // shrink all the way down alongside the buttons - the row should
    // hug its own content on this side, not carry dead space.
    [SerializeField] private float audioRowPaddingLeftMax = 26f;
    [SerializeField] private float audioRowPaddingLeftMin = 16f;
    [SerializeField] private float audioRowPaddingTopMax = 14f;
    [SerializeField] private float audioRowPaddingTopMin = 10f;

    // Row padding, right/bottom: this is what keeps the button tray clear
    // of the resize-handle grip (see .resize-handle - a 32px box inset 4px
    // from the panel's bottom-right corner = a 36px clearance floor), so
    // unlike every other value on this row it is NOT allowed to scale all
    // the way down to some small cosmetic minimum - audioRowPaddingRightMin/
    // BottomMin sit exactly at that 36px floor and stay constant across the
    // whole size range; only the couple of px above the floor (…Max)
    // breathes with panel width. This is functional clearance, not
    // unused space - shrink it further and the Stop button starts
    // fighting the resize grip for clicks in the corner.
    [SerializeField] private float audioRowPaddingRightMax = 38f;
    [SerializeField] private float audioRowPaddingRightMin = 36f;
    [SerializeField] private float audioRowPaddingBottomMax = 38f;
    [SerializeField] private float audioRowPaddingBottomMin = 36f;

    // Panel width at/above which the audio row sits at its full Max size.
    // Matches .info-panel's max-width in the USS - keep the two in sync so
    // the row reaches full size exactly when the panel reaches its own
    // largest resizable width, not before or after.
    [SerializeField] private float audioRowReferenceMaxWidth = 900f;

    // Description scrolling refs. _bodyScrollView is typed as ScrollView
    // (not just VisualElement) so UpdateScrollHint can read scrollOffset /
    // contentContainer / contentViewport directly.
    private ScrollView _bodyScrollView;
    private VisualElement _scrollHint;

    // Title auto-fit: UI Toolkit has no built-in "shrink to fit" font size
    // or line-clamp, so long bone names (e.g. "Right Fourth Metacarpal
    // Bone") are fit to TitleMaxLines lines by measuring text at
    // decreasing font sizes (see FitTitleLabel). The full displayName from
    // BoneDatabase.json is always shown in the end - shrinking the font
    // is the only trade-off FitTitleLabel makes; it never truncates or
    // drops words to make the name fit.
    private const float TitleFontSizeMax = 34f;
    private const float TitleFontSizeMin = 22f; // never go smaller than this - stays readable
    private const float TitleFontSizeStep = 1f;
    private const int TitleMaxLines = 2;
    private const float TitleLineHeightMultiplier = 1.2f; // approximate line height for this font
    private string _pendingTitleText;

    private bool _isMinimized;
    private Vector2 _panelSizeBeforeMinimize;

    // Info panel drag / resize
    private VisualElement _dragHandle;   // the header — dragging moves the panel
    private VisualElement _resizeHandle; // bottom-right grip — dragging resizes the panel

    private bool _isDraggingPanel;
    private Vector2 _dragPointerStart;
    private Vector2 _panelPosAtDragStart;

    // When minimized, the minimize button becomes the entire circle and is
    // set to PickingMode.Ignore so presses land on Header (see
    // ToggleMinimize / OnPanelDragPointerDown), starting a potential drag.
    // This tracks that case so PointerUp can tell a real drag apart from a
    // simple tap and restore the panel on tap, same as clicking it would
    // have done in the expanded header.
    private bool _dragStartedOnMinimizedButton;
    private const float MinimizedDragClickThreshold = 24f;

    private bool _isResizingPanel;
    private Vector2 _resizePointerStart;
    private Vector2 _panelSizeAtResizeStart;

    // Fallback size, only used before the UIDocument has laid out (root
    // resolvedStyle not valid yet). Once the panel is enabled, dragging and
    // resizing clamp against the ROOT's actual resolved size instead, so it
    // always matches the real on-screen panel space -- even if the device's
    // aspect ratio doesn't match the 1080x1920 reference resolution (Scale
    // With Screen Size + Match 0.5 will letterbox/stretch differently per
    // device, so a hardcoded 1080x1920 clamp would leave parts of the real
    // screen unreachable, or let the panel drift past the visible edge).
    [Header("Info Panel Drag/Resize")]
    [SerializeField] private Vector2 canvasSize = new Vector2(1080f, 1920f);
    // Must match .info-panel's min-width/min-height in the USS - if this
    // floor is smaller, the resize-handle math thinks it successfully
    // shrank the panel below what USS actually allows, and every
    // subsequent delta is computed from a size the panel was never really
    // at (the header/audio-row/body then look squeezed or misaligned even
    // though the drag itself reported success).
    [SerializeField] private Vector2 infoPanelMinSize = new Vector2(460f, 400f);

    // How much of the panel must stay on-screen (in panel-space points)
    // when it's dragged past an edge. The panel CAN be dragged mostly off
    // the visible screen, but this sliver always stays reachable so the
    // player can never drag it somewhere they can't grab it back from.
    [SerializeField] private float infoPanelEdgeVisibleMargin = 80f;

    // Live panel-space size of the whole screen, in the same units as
    // style.left/top/width/height (i.e. UI Toolkit points, already
    // accounting for PanelSettings scaling). Falls back to the serialized
    // canvasSize if the root hasn't been laid out yet.
    private Vector2 ScreenSize
    {
        get
        {
            if (_root == null) return canvasSize;
            float w = _root.resolvedStyle.width;
            float h = _root.resolvedStyle.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f) return canvasSize;
            return new Vector2(w, h);
        }
    }

    // Search refs
    private TextField _searchField;
    private Label _searchPlaceholder;
    private Button _searchClearButton;
    private VisualElement _searchBar;
    private ScrollView _searchResults;

    // Toolbar refs
    private Button _resetButton;
    private Button _isolateButton;
    private Button _hideButton;
    private Label _hideLabel;
    private Button _undoButton;
    private Button _backButton;

    // Controls guide (floating "how to use the controls" help) refs
    private Button _controlsGuideButton;
    private VisualElement _controlsGuideOverlay;
    private VisualElement _controlsGuideBackdrop;
    private Button _controlsGuideCloseButton;
    private Button _controlsGuideGotItButton;

    private readonly List<VisualElement> _boneElements = new List<VisualElement>();
    private readonly Dictionary<VisualElement, BoneInfo> _dataByElement = new Dictionary<VisualElement, BoneInfo>();

    // Singleton, same convention as FirebaseBootstrap/QuizService/
    // AnatomyPlayModeLocalStorage - lets other screens (e.g.
    // StudentProgressController) query GetSelectableStructureCount/Keys
    // below without needing this screen to be open. Set in Awake (not
    // OnEnable) because modelRoot/databaseJson data for every configured
    // AnatomySystemConfig is available the whole time this component's
    // GameObject exists, regardless of which system is currently active or
    // whether this screen is presently shown.
    public static AnatomyScreenController Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
    }

    private void OnEnable()
    {
        _root = GetComponent<UIDocument>().rootVisualElement;

        // Pay off any restore the previous mode deferred (see
        // ClearStructureFilter) before anything below inspects or rebuilds the
        // model's renderers.
        RestorePendingFilterVisibility();

        // Must run before anything below reads skeletonRoot/boneDatabaseJson
        // (PopulateBoneDataFromSkeleton, the 3D Model View auto-frame block,
        // etc.) - see ResolveAnatomySystem's own comment for why the
        // selected-system assignment has to happen this early.
        ResolveAnatomySystem();
        ResetRuntimeState();

        //low
        

        // --- Info panel ---
        _infoPanel = _root.Q<VisualElement>("InfoPanel");
        _titleLabel = _root.Q<Label>("TitleLabel");
        _descriptionLabel = _root.Q<Label>("DescriptionLabel");
        _closeButton = _root.Q<Button>("CloseButton");
        _minimizeButton = _root.Q<Button>("MinimizeButton");
        _playButton = _root.Q<Button>("PlayButton");
        _stopButton = _root.Q<Button>("StopButton");
        _audioLabel = _root.Q<Label>("AudioLabel");
        _audioRow = _root.Q<VisualElement>("AudioRow");
        _audioIcon = _root.Q<VisualElement>("AudioIcon");
        _audioIconLabel = _root.Q<Label>("AudioIconLabel");
        _audioControlsTray = _root.Q<VisualElement>("AudioControls");

        _closeButton.clicked += HideInfoPanel;
        _minimizeButton.clicked += ToggleMinimize;
        _playButton.clicked += PlayAudio;
        _stopButton.clicked += StopAudio;

        // --- Description scrolling / "scroll for more" hint ---
        _bodyScrollView = _root.Q<ScrollView>("Body");
        _scrollHint = _root.Q<VisualElement>("ScrollHint");

        // Re-check whenever the content's measured height changes (new
        // description text, font/wrap changes, panel resize) or the
        // viewport itself changes size (panel resize, drag-resize handle,
        // orientation change) - either can flip whether the text overflows.
        _bodyScrollView.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => UpdateScrollHint());
        _bodyScrollView.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => UpdateScrollHint());
        // Re-check as the player scrolls, so the hint disappears once
        // they've actually reached the bottom of a long description.
        if (_bodyScrollView.verticalScroller != null)
            _bodyScrollView.verticalScroller.valueChanged += _ => UpdateScrollHint();

        // Re-fit the title any time the width available to it changes -
        // this fires on first layout, on window/orientation/resolution
        // changes (the panel is percent-sized, so its width tracks the
        // screen), and when the player drags the resize handle. Guarded by
        // a width-only check since height changes as a RESULT of us
        // changing font-size/wrapping and must not re-trigger a fit pass.
        _titleLabel.RegisterCallback<GeometryChangedEvent>(evt =>
        {
            if (Mathf.Approximately(evt.newRect.width, evt.oldRect.width)) return;
            FitTitleLabel();
        });

       
        

        // Re-scale the audio row any time the PANEL's own width changes -
        // covers first layout, the resize-handle drag, restoring from
        // minimized, and ClampPanelToScreen ever adjusting the width, all
        // through this one callback (see UpdateAudioRowScale). Watching
        // _infoPanel's width directly (rather than _audioRow's) is what
        // lets this fire even while the row itself is display:none
        // (minimized), so it's already correctly sized the moment the
        // panel expands again.
        _infoPanel.RegisterCallback<GeometryChangedEvent>(evt =>
        {
            if (Mathf.Approximately(evt.newRect.width, evt.oldRect.width)) return;
            UpdateAudioRowScale();
        });

        // The panel's left/top/width/height only start out as percentages
        // of the screen (see the .info-panel comment in the USS) - once
        // it's been shown, drag/resize switch it to explicit pixel values,
        // which do NOT automatically track a later orientation or
        // resolution change. Re-clamp it back into view any time the root
        // itself resizes (device rotated, Game View resized, etc.) so it
        // can never end up stranded off-screen or taller than the screen.
        _root.RegisterCallback<GeometryChangedEvent>(evt =>
        {
            if (Mathf.Approximately(evt.newRect.width, evt.oldRect.width) &&
                Mathf.Approximately(evt.newRect.height, evt.oldRect.height))
                return;
            ClampPanelToScreen();
        });

        // --- Info panel drag (header) ---
        _dragHandle = _root.Q<VisualElement>("Header");
        // USS has no native gradient syntax, so the header's green->blue
        // wash (see the reference mock) is painted by hand here instead of
        // via .header's background-color. generateVisualContent re-runs
        // automatically on layout/repaint, so this stays correct across
        // resizes/orientation changes without any extra bookkeeping.
        _dragHandle.generateVisualContent += DrawHeaderGradient;
        _dragHandle.RegisterCallback<PointerDownEvent>(OnPanelDragPointerDown);
        _dragHandle.RegisterCallback<PointerMoveEvent>(OnPanelDragPointerMove);
        _dragHandle.RegisterCallback<PointerUpEvent>(OnPanelDragPointerUp);
        _dragHandle.RegisterCallback<PointerCaptureOutEvent>(OnPanelDragPointerCaptureOut);

        // --- Info panel resize (bottom-right grip) ---
        _resizeHandle = _root.Q<VisualElement>("ResizeHandle");
        _resizeHandle.RegisterCallback<PointerDownEvent>(OnPanelResizePointerDown);
        _resizeHandle.RegisterCallback<PointerMoveEvent>(OnPanelResizePointerMove);
        _resizeHandle.RegisterCallback<PointerUpEvent>(OnPanelResizePointerUp);
        _resizeHandle.RegisterCallback<PointerCaptureOutEvent>(OnPanelResizePointerCaptureOut);

        // --- Search ---
        _searchField = _root.Q<TextField>("SearchField");
        _searchPlaceholder = _root.Q<Label>("SearchPlaceholder");
        _searchClearButton = _root.Q<Button>("SearchClearButton");
        _searchBar = _root.Q<VisualElement>("SearchBar");
        _searchResults = _root.Q<ScrollView>("SearchResults");

        _searchField.RegisterValueChangedCallback(OnSearchChanged);
        _searchClearButton.clicked += ClearSearch;

        // Keep the dropdown pinned directly under the search bar across
        // first layout, orientation changes, and any resolution change -
        // TopBar's height isn't fixed (depends on its text content), so
        // this can't be a static CSS offset (see PositionSearchResults).
        _searchBar.RegisterCallback<GeometryChangedEvent>(_ => PositionSearchResults());
        _searchBar.schedule.Execute(PositionSearchResults).ExecuteLater(0);

        // --- Toolbar ---
        _resetButton = _root.Q<Button>("ResetButton");
        _isolateButton = _root.Q<Button>("IsolateButton");
        _hideButton = _root.Q<Button>("HideButton");
        _hideLabel = _root.Q<Label>("unity-hidelabel");
        _undoButton = _root.Q<Button>("UndoButton");
        _backButton = _root.Q<Button>("BackButton");

        _resetButton.clicked += OnResetClicked;
        _isolateButton.clicked += OnIsolateClicked;
        _hideButton.clicked += OnHideClicked;
        _backButton.clicked += OnBackClicked;

        // Hide mode starts off (_isHideModeActive defaults to false) -
        // match the label to that from the start rather than showing the
        // static "Hide" text from the UXML until the first toggle.
        if (_hideLabel != null)
            _hideLabel.text = "Hide Off";

        // --- Controls guide (floating "how to use the BottomToolbar" help) ---
        _controlsGuideButton = _root.Q<Button>("ControlsGuideButton");
        _controlsGuideOverlay = _root.Q<VisualElement>("ControlsGuideOverlay");
        _controlsGuideBackdrop = _root.Q<VisualElement>("ControlsGuideBackdrop");
        _controlsGuideCloseButton = _root.Q<Button>("ControlsGuideCloseButton");
        _controlsGuideGotItButton = _root.Q<Button>("ControlsGuideGotItButton");

        if (_controlsGuideButton != null) _controlsGuideButton.clicked += OnControlsGuideButtonClicked;
        if (_controlsGuideBackdrop != null) _controlsGuideBackdrop.RegisterCallback<ClickEvent>(OnControlsGuideCloseRequested);
        if (_controlsGuideCloseButton != null) _controlsGuideCloseButton.clicked += CloseControlsGuide;
        if (_controlsGuideGotItButton != null) _controlsGuideGotItButton.clicked += CloseControlsGuide;

        // Force-hidden every time the screen (re)opens, in case a previous
        // session somehow left it visible (e.g. this GameObject re-enabled
        // without OnDisable ever running its close-side cleanup).
        CloseControlsGuide();

        // --- Bone data (BoneDatabase.json) ---
        PopulateBoneDataFromSkeleton();

        // --- Bone hotspots ---
        _boneElements.Clear();
        _dataByElement.Clear();
        foreach (var info in boneData)
        {
            // UI hotspot button (tracked screen-space overlay) and the 3D
            // bone/collider (mesh-tap selection) are resolved independently
            // - one being missing must never skip the other. They used to
            // be coupled behind a single `continue` here, which meant a
            // missing hotspot element silently disabled EnsureBoneCollider
            // too, breaking mesh-tap selection entirely even though it has
            // nothing to do with the UXML hotspot elements.
           

            // Resolve the matching bone piece on the 3D model, if wired up.
            // Looked up from _boneTransformsByName (built by a full
            // recursive walk in PopulateBoneDataFromSkeleton) rather than
            // skeletonRoot.Find(info.boneName), which only searches direct
            // children and would silently miss anything nested deeper.
            if (skeletonRoot != null)
            {
                _boneTransformsByName.TryGetValue(info.boneName, out Transform boneTransform);
                if (boneTransform == null)
                    Debug.LogWarning($"AnatomyScreenController: no bone Transform named '{info.boneName}' found under skeletonRoot.");
                info.worldBone = boneTransform;
                if (boneTransform != null)
                {
                    info.originalRotation = boneTransform.rotation;
                    info.targetRotation = boneTransform.rotation;
                    EnsureBoneCollider(info);
                }
            }
            else
            {
                Debug.LogWarning("[AnatomyScreenController] skeletonRoot is not assigned in the Inspector - no bone colliders will be created, so mesh-tap selection can't work.");
            }
        }

        // Must run after the loop above (it needs every BoneInfo.worldBone
        // resolved) and before the auto-frame block below.
        ApplyStructureFilterVisibility();

        // --- 3D model view ---
        _bodyArea = _root.Q<VisualElement>("BodyArea");
        if (modelRenderTexture != null)
            _bodyArea.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(modelRenderTexture));

        if (modelCamera != null && skeletonRoot != null)
        {
            Bounds bounds = ComputeSkeletonBounds();
            // _orbitPivot = bounds.center;
            _orbitPivot = new Vector3(0f, 1f, 0f);
            // Distance at which a sphere of this radius exactly fills the
            // camera's vertical field of view, then padded out a bit so the
            // model isn't touching the frame edges.
            float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            float fitDistance = radius / Mathf.Sin(modelCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float desiredDistance = fitDistance * autoFramePadding;

            // zoomDistanceRange is meant to be the user's min/max *zoom* limits,
            // not a hard cap on the auto-computed starting distance. If it was
            // tuned for a different model/import scale than what's actually in
            // skeletonRoot right now, clamping desiredDistance into it would put
            // the camera inside or right against the mesh (nothing visible) or
            // absurdly far away. Widen the range to guarantee it contains the
            // distance the model actually needs, keeping whatever extra zoom
            // headroom was configured.
            zoomDistanceRange.x = Mathf.Min(zoomDistanceRange.x, desiredDistance * 0.5f);
            zoomDistanceRange.y = Mathf.Max(zoomDistanceRange.y, desiredDistance * 1.5f);

            _initialDistance = Mathf.Clamp(desiredDistance, zoomDistanceRange.x, zoomDistanceRange.y);

            // Keep whatever yaw/pitch angle the camera was manually placed
            // at in the scene — auto-framing only corrects the pivot and
            // distance, not the viewing angle you chose.
            Vector3 offset = modelCamera.transform.position - _orbitPivot;
            if (offset.sqrMagnitude > 0.0001f)
            {
                float offsetDist = offset.magnitude;
                _initialYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                _initialPitch = Mathf.Asin(Mathf.Clamp(offset.y / offsetDist, -1f, 1f)) * Mathf.Rad2Deg;
            }
            else
            {
                _initialYaw = 0f;
                _initialPitch = 10f; // slight downward angle as a sane default
            }

            _orbitYaw = _initialYaw;
            _orbitPitch = _initialPitch;
            _orbitDistance = _initialDistance;
            _targetOrbitDistance = _initialDistance;
            _panOffset = Vector3.zero;
            _targetPanOffset = Vector3.zero;
            _isFocusingBone = false;
            ApplyOrbitCamera();
        }

        _bodyArea.RegisterCallback<PointerDownEvent>(OnBodyAreaPointerDown);
        _bodyArea.RegisterCallback<PointerMoveEvent>(OnBodyAreaPointerMove);
        _bodyArea.RegisterCallback<PointerUpEvent>(OnBodyAreaPointerUp);
        _bodyArea.RegisterCallback<PointerCaptureOutEvent>(OnBodyAreaPointerCaptureOut);
        _bodyArea.RegisterCallback<WheelEvent>(OnZoomWheel);

        _undoButton.clicked += OnUndoClicked;
        _undoButton.SetEnabled(false);

        // Prime the audio row at whatever size the panel resolves to
        // first - belt-and-braces alongside the GeometryChangedEvent
        // listener above, same "fallback for the very first open this
        // session" reasoning as SetTitleText's own extra scheduled call.
        UpdateAudioRowScale();
        _infoPanel.schedule.Execute(UpdateAudioRowScale).ExecuteLater(0);

        HideInfoPanel();

        // Fired last, after every step above has finished, so any subscriber
        // (AnatomyPlayModeController) that queries the UXML tree in response
        // always sees the screen in its fully-initialized state.
        OnScreenReady?.Invoke();
    }

    private void Update()
    {
        if (modelCamera == null || _bodyArea == null) return;

        // Smoothly chase the gesture-driven zoom/pan targets every frame,
        // independent of how often pointer-move events actually fire (and
        // therefore independent of frame rate) - this is what makes pinch
        // and two-finger pan feel smooth rather than snapping straight to
        // each raw touch delta. While a bone-focus animation is running
        // (see FocusOnSelectedBone), it drives _orbitDistance/_panOffset
        // itself instead, over its own selectedBoneZoomDuration - any new
        // touch input (OnBodyAreaPointerDown) or scroll-zoom (OnZoomWheel)
        // cancels it immediately so manual control always wins.
        if (_isFocusingBone)
        {
            _focusElapsed += Time.deltaTime;
            float duration = Mathf.Max(selectedBoneZoomDuration, 0.0001f);
            float t = Mathf.Clamp01(_focusElapsed / duration);
            float eased = t * t * (3f - 2f * t); // smoothstep - gentle ease in/out, not linear

            _panOffset = Vector3.Lerp(_focusStartPanOffset, _focusTargetPanOffset, eased);
            _orbitDistance = Mathf.Lerp(_focusStartDistance, _focusTargetDistance, eased);

            if (t >= 1f)
            {
                _isFocusingBone = false;
                _panOffset = _focusTargetPanOffset;
                _orbitDistance = _focusTargetDistance;
            }
        }
        else
        {
            _orbitDistance = Mathf.SmoothDamp(_orbitDistance, _targetOrbitDistance, ref _orbitDistanceVelocity, gestureSmoothTime);
            _panOffset = Vector3.SmoothDamp(_panOffset, _targetPanOffset, ref _panOffsetVelocity, gestureSmoothTime);
        }
        ApplyOrbitCamera();

        // Smoothly rotate the selected bone toward wherever the swipe
        // gesture has driven it. Only the selected bone needs this - every
        // other bone just sits at whatever rotation it was left at.
        if (_selectedBone?.worldBone != null)
        {
            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(gestureSmoothTime, 0.0001f));
            _selectedBone.worldBone.rotation = Quaternion.Slerp(_selectedBone.worldBone.rotation, _selectedBone.targetRotation, t);
        }

        // Keep bone hotspot buttons glued to their bone's projected screen
        // position, so they track the model as it's rotated/zoomed.
        float areaWidth = _bodyArea.resolvedStyle.width;
        float areaHeight = _bodyArea.resolvedStyle.height;
        if (float.IsNaN(areaWidth) || float.IsNaN(areaHeight) || areaWidth <= 0f || areaHeight <= 0f) return;

        foreach (var el in _boneElements)
        {
            var info = _dataByElement[el];
            if (info.worldBone == null) continue;

            Vector3 vp = modelCamera.WorldToViewportPoint(info.worldBone.position);
            bool inFrontOfCamera = vp.z > 0f;
            el.style.display = inFrontOfCamera ? DisplayStyle.Flex : DisplayStyle.None;
            if (!inFrontOfCamera) continue;

            // UI Toolkit's Y axis is top-down; viewport Y is bottom-up.
            float x = vp.x * areaWidth;
            float y = (1f - vp.y) * areaHeight;

            el.style.left = x - el.resolvedStyle.width * 0.5f;
            el.style.top = y - el.resolvedStyle.height * 0.5f;
        }
    }

    private void OnDisable()
    {
        _closeButton.clicked -= HideInfoPanel;
        _minimizeButton.clicked -= ToggleMinimize;
        _playButton.clicked -= PlayAudio;
        _stopButton.clicked -= StopAudio;

        _dragHandle.UnregisterCallback<PointerDownEvent>(OnPanelDragPointerDown);
        _dragHandle.UnregisterCallback<PointerMoveEvent>(OnPanelDragPointerMove);
        _dragHandle.UnregisterCallback<PointerUpEvent>(OnPanelDragPointerUp);
        _dragHandle.UnregisterCallback<PointerCaptureOutEvent>(OnPanelDragPointerCaptureOut);

        _resizeHandle.UnregisterCallback<PointerDownEvent>(OnPanelResizePointerDown);
        _resizeHandle.UnregisterCallback<PointerMoveEvent>(OnPanelResizePointerMove);
        _resizeHandle.UnregisterCallback<PointerUpEvent>(OnPanelResizePointerUp);
        _resizeHandle.UnregisterCallback<PointerCaptureOutEvent>(OnPanelResizePointerCaptureOut);

        _searchField.UnregisterValueChangedCallback(OnSearchChanged);
        _searchClearButton.clicked -= ClearSearch;
        _resetButton.clicked -= OnResetClicked;
        _isolateButton.clicked -= OnIsolateClicked;
        _hideButton.clicked -= OnHideClicked;
        _backButton.clicked -= OnBackClicked;
        _undoButton.clicked -= OnUndoClicked;

        if (_controlsGuideButton != null) _controlsGuideButton.clicked -= OnControlsGuideButtonClicked;
        if (_controlsGuideBackdrop != null) _controlsGuideBackdrop.UnregisterCallback<ClickEvent>(OnControlsGuideCloseRequested);
        if (_controlsGuideCloseButton != null) _controlsGuideCloseButton.clicked -= CloseControlsGuide;
        if (_controlsGuideGotItButton != null) _controlsGuideGotItButton.clicked -= CloseControlsGuide;

        if (_bodyArea != null)
        {
            _bodyArea.UnregisterCallback<PointerDownEvent>(OnBodyAreaPointerDown);
            _bodyArea.UnregisterCallback<PointerMoveEvent>(OnBodyAreaPointerMove);
            _bodyArea.UnregisterCallback<PointerUpEvent>(OnBodyAreaPointerUp);
            _bodyArea.UnregisterCallback<PointerCaptureOutEvent>(OnBodyAreaPointerCaptureOut);
            _bodyArea.UnregisterCallback<WheelEvent>(OnZoomWheel);
        }
    }

    // ===== Anatomy system selection =====

    // Copies the selected system's Model Root / Database Json (from
    // anatomySystems) into skeletonRoot/boneDatabaseJson - the same two
    // fields every other method in this file already reads - and makes sure
    // only that one system's model is active. Called first thing in
    // OnEnable, before PopulateBoneDataFromSkeleton or the 3D Model View
    // auto-frame block run, so there is never a frame where the controller
    // initializes against the previous system's model/database and only
    // picks up the new one afterward (see the class-level "IMPORTANT UNITY
    // LIFECYCLE REQUIREMENT" this satisfies).
    private void ResolveAnatomySystem()
    {
        _currentSystem = _hasPendingSystem ? _pendingSystem : defaultAnatomySystem;
        // Consumed - if this screen is ever re-enabled without going through
        // SetAnatomySystem again (e.g. Editor testing), it falls back to
        // defaultAnatomySystem rather than silently repeating a stale value.
        _hasPendingSystem = false;

        // Combined mode: every listed system stays active and the first one
        // becomes the anchor (skeletonRoot/_currentSystem), so anything that
        // still needs a single "current" system - the per-system hint limit,
        // back navigation - has a sane one. See _combinedSystems.
        if (_combinedSystems.Count > 0)
        {
            ResolveCombinedSystems();
            return;
        }

        AnatomySystemConfig config = anatomySystems.Find(c => c != null && c.system == _currentSystem);
        if (config == null)
        {
            Debug.LogWarning($"[AnatomyScreenController] No 'Anatomy Systems' entry configured for '{_currentSystem}' - leaving skeletonRoot/boneDatabaseJson as currently assigned in the Inspector.");
        }
        else
        {
            if (config.modelRoot != null) skeletonRoot = config.modelRoot;
            if (config.databaseJson != null) boneDatabaseJson = config.databaseJson;
        }

        // Only the selected system's model may ever be active - deactivate
        // every other configured model root so switching systems never
        // leaves a previous system's model (or its colliders/renderers)
        // visible or interactable underneath the new one.
        foreach (var c in anatomySystems)
        {
            if (c == null || c.modelRoot == null) continue;
            c.modelRoot.gameObject.SetActive(c == config);
        }

        _activeRoots.Clear();
        if (skeletonRoot != null) _activeRoots.Add(skeletonRoot);

        // Header subtitle mirrors whichever system is now active (UXML
        // ships with a static "SKELETAL SYSTEM" placeholder - see
        // AnatomyScreen.uxml's AppSubtitle label).
        var subtitleLabel = _root?.Q<Label>("AppSubtitle");
        if (subtitleLabel != null)
        {
            subtitleLabel.text = config != null && !string.IsNullOrEmpty(config.subtitleText)
                ? config.subtitleText
                : _currentSystem.ToString().ToUpperInvariant() + " SYSTEM";
        }
    }

    // The combined-systems counterpart of the block above: activates EVERY
    // listed system's model root (deactivating any that aren't listed), points
    // skeletonRoot/_currentSystem/boneDatabaseJson at the first of them as the
    // anchor, and records all of them in _activeRoots so bone collection,
    // filter visibility and auto-framing cover the whole set. The databases
    // themselves are merged in LoadActiveDatabases, called from
    // PopulateBoneDataFromSkeleton.
    private void ResolveCombinedSystems()
    {
        _activeRoots.Clear();

        var configs = new List<AnatomySystemConfig>();
        foreach (var system in _combinedSystems)
        {
            var match = anatomySystems.Find(c => c != null && c.system == system);
            if (match == null || match.modelRoot == null)
            {
                Debug.LogWarning($"[AnatomyScreenController] Combined systems included '{system}' but no " +
                                  "'Anatomy Systems' entry with a Model Root is configured for it - skipping it.");
                continue;
            }
            configs.Add(match);
        }

        foreach (var c in anatomySystems)
        {
            if (c == null || c.modelRoot == null) continue;
            c.modelRoot.gameObject.SetActive(configs.Contains(c));
        }

        if (configs.Count == 0)
        {
            Debug.LogError("[AnatomyScreenController] Combined systems mode was requested but none of the listed " +
                            "systems are configured - nothing will be shown.");
            return;
        }

        foreach (var c in configs)
            _activeRoots.Add(c.modelRoot);

        var anchor = configs[0];
        _currentSystem = anchor.system;
        skeletonRoot = anchor.modelRoot;
        if (anchor.databaseJson != null) boneDatabaseJson = anchor.databaseJson;

        var combinedSubtitle = _root?.Q<Label>("AppSubtitle");
        if (combinedSubtitle != null) combinedSubtitle.text = "ALL SYSTEMS";

        Debug.Log($"[AnatomyScreenController] ResolveCombinedSystems: {configs.Count} model(s) active simultaneously " +
                  $"(anchor '{anchor.system}').");
    }

    // Loads the database JSON for every active root - just the one in the
    // normal single-system case, or all of the combined systems' databases
    // merged together (additively, first one wins on a shared key) so a bone
    // from ANY visible model still resolves to a title/description.
    private void LoadActiveDatabases()
    {
        if (_combinedSystems.Count == 0)
        {
            if (boneDatabaseJson == null)
                Debug.LogWarning("[AnatomyScreenController] boneDatabaseJson is not assigned - the Info Panel will show fallback text for every bone.");
            else
                _boneDatabaseService.Load(boneDatabaseJson.text);
            return;
        }

        bool loadedAny = false;
        for (int i = 0; i < _combinedSystems.Count; i++)
        {
            var config = anatomySystems.Find(c => c != null && c.system == _combinedSystems[i]);
            if (config == null || config.databaseJson == null)
            {
                Debug.LogWarning($"[AnatomyScreenController] No Database Json configured for combined system " +
                                  $"'{_combinedSystems[i]}' - its structures will show fallback info text.");
                continue;
            }

            // additive for every load after the first, so each system's
            // database adds to the set rather than wiping the previous one.
            _boneDatabaseService.Load(config.databaseJson.text, additive: loadedAny);
            loadedAny = true;
        }

        if (!loadedAny)
            Debug.LogWarning("[AnatomyScreenController] No databases could be loaded for the combined systems - the Info Panel will show fallback text for every structure.");
    }

    // Clears every piece of runtime state that belongs to whichever
    // model/database was previously active, so switching anatomy systems
    // (or simply reopening this screen) never leaves stale selection,
    // undo history, isolate/hide state, or collider lookups pointing at the
    // previous system's bones. boneData/_boneTransformsByName are cleared
    // separately, at the top of PopulateBoneDataFromSkeleton, since that's
    // where they're immediately rebuilt.
    private void ResetRuntimeState()
    {
        _undoStack.Clear();
        _isIsolated = false;
        _isolateUndo = null;
        _isHideModeActive = false;
        _selectedBone = null;
        _infoByCollider.Clear();
        _isFocusingBone = false;
    }

    // ===== Bone data (BoneDatabase.json) =====

    // Builds boneData at runtime: one BoneInfo per bone Transform found
    // anywhere under skeletonRoot (see _boneTransformsByName above for why
    // this has to be a full recursive walk, not just direct children), with
    // its display title and description resolved by matching that
    // Transform's GameObject name against BoneDatabase.json (via
    // BoneDatabaseService, which handles extra whitespace, a "(Clone)"
    // suffix, and case differences).
    //
    // A descendant is only treated as a "bone" if it either matches an
    // entry in BoneDatabase.json or carries its own mesh (MeshFilter with a
    // real sharedMesh) - this skips purely organizational/group nodes
    // (empty parents, armature roots, etc.) that aren't bones and have no
    // data, while still catching every real bone (matched or not) and every
    // meshless bone that IS in the JSON (e.g. sinus cavities).
    //
    // Must run before the hotspot/collider wiring loop below, since that
    // loop iterates boneData and looks up _boneTransformsByName.
    private void PopulateBoneDataFromSkeleton()
    {
        boneData.Clear();
        _boneTransformsByName.Clear();
        _boneTransformSet.Clear();

        LoadActiveDatabases();

        if (_activeRoots.Count == 0)
        {
            Debug.LogWarning("[AnatomyScreenController] No active model root - no bones to populate.");
            return;
        }

        // Every active root, not just skeletonRoot - in combined-systems mode
        // that's all three models' pieces collected into the one boneData list,
        // which is what lets a tap on any of them resolve normally.
        var descendants = new List<Transform>();
        foreach (var root in _activeRoots)
        {
            if (root == null) continue;
            CollectDescendants(root, descendants);
        }

        foreach (var t in descendants)
        {
            string rawName = t.name;

            // A structure filter is active (Pretest/Posttest) - this model is
            // only allowed to contribute the handful of structures actually
            // being asked about. Skipping the rest here is what keeps the
            // assessment light: nothing else gets a BoneInfo, so nothing else
            // reaches EnsureBoneCollider's per-mesh MeshCollider generation,
            // the search index, or the per-frame hotspot loop in Update.
            if (_structureFilter != null && !_structureFilter.Contains(BoneDatabaseService.NormalizeKey(rawName)))
                continue;

            bool found = _boneDatabaseService.TryGetEntry(rawName, out BoneDatabaseEntry entry);

            var meshFilter = t.GetComponent<MeshFilter>();
            bool hasOwnMesh = meshFilter != null && meshFilter.sharedMesh != null;

            // Not a recognized bone and not a mesh piece of its own -
            // almost certainly an organizational/group node, skip it.
            if (!found && !hasOwnMesh) continue;

            if (_boneTransformsByName.ContainsKey(rawName))
            {
                Debug.LogWarning($"[AnatomyScreenController] Duplicate bone GameObject name '{rawName}' under skeletonRoot - keeping the first one found.");
                continue;
            }

            if (!found)
                Debug.LogWarning($"[AnatomyScreenController] No BoneDatabase.json entry matches bone GameObject '{rawName}' - it will show fallback info.");

            _boneTransformsByName[rawName] = t;
            _boneTransformSet.Add(t);
            boneData.Add(new BoneInfo
            {
                boneName = rawName,
                title = found ? entry.displayName : rawName,
                baseName = found ? entry.baseName : string.Empty,
                description = found ? entry.description : BoneInfoNotAvailableText
            });
        }


        Debug.Log($"[AnatomyScreenController] PopulateBoneDataFromSkeleton: found {boneData.Count} bone(s) under skeletonRoot.");

        // The database and the model are populated from two different
        // sources (JSON keys vs. GameObject names under skeletonRoot), so a
        // count mismatch between BoneDatabaseService.Count and
        // boneData.Count doesn't by itself say WHICH entries are missing
        // from the model - line 952 above already warns about the reverse
        // case (a GameObject with no JSON entry). This closes the loop by
        // walking every JSON entry and reporting any that never matched a
        // GameObject anywhere under skeletonRoot.
        //
        // Compare on the same normalized footing BoneDatabaseService itself
        // uses (case/whitespace/"(Clone)" insensitive) - _boneTransformsByName
        // is keyed by the raw GameObject name, so a direct raw-to-raw
        // comparison here would wrongly flag entries that only differ by
        // case or stray whitespace as "missing".
        // The full database-coverage audit below is meaningless while a
        // structure filter is active - practically every entry is "missing"
        // by design then, so it would log hundreds of warnings about
        // structures nobody asked for. Audit the filter itself instead: a
        // filtered key that matched no GameObject means that structure simply
        // won't be drawn or answerable, which is worth one clear warning
        // rather than a silently short assessment.
        if (_structureFilter != null)
        {
            foreach (var key in _structureFilter)
            {
                bool matched = false;
                foreach (var info in boneData)
                {
                    if (BoneDatabaseService.NormalizeKey(info.boneName) != key) continue;
                    matched = true;
                    break;
                }

                if (!matched)
                    Debug.LogWarning($"[AnatomyScreenController] Filtered structure '{key}' has no matching GameObject under " +
                                      $"'{skeletonRoot.name}' - it will not be shown or selectable. Check the StructureKey spelling " +
                                      "against the model's GameObject names.");
            }

            return;
        }

        var matchedNormalizedKeys = new HashSet<string>();
        foreach (var rawName in _boneTransformsByName.Keys)
            matchedNormalizedKeys.Add(BoneDatabaseService.NormalizeKey(rawName));

        int unmatchedCount = 0;
        foreach (var entry in _boneDatabaseService.AllEntries)
        {
            if (!matchedNormalizedKeys.Contains(BoneDatabaseService.NormalizeKey(entry.boneId)))
            {
                unmatchedCount++;
                Debug.LogWarning($"[AnatomyScreenController] BoneDatabase.json entry '{entry.boneId}' has no matching GameObject under skeletonRoot - it will never be selectable in this system's model.");
            }
        }
        if (unmatchedCount > 0)
            Debug.LogWarning($"[AnatomyScreenController] {unmatchedCount} BoneDatabase.json entrie(s) had no matching GameObject in the model (database has {_boneDatabaseService.Count} entries, model matched {boneData.Count}).");

    }

    // Recursively collects every descendant Transform of root (children,
    // grandchildren, etc.) into result. Used instead of Transform.Find,
    // which only searches direct children when given a plain name.
    private static void CollectDescendants(Transform root, List<Transform> result)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            result.Add(child);
            CollectDescendants(child, result);
        }
    }

    // Full "Root/Parent/Child" hierarchy path of a Transform, for debug
    // logging only - lets a selection/outline mismatch be diagnosed
    // straight from the console log without needing the Scene hierarchy
    // open.
    private static string GetHierarchyPath(Transform t)
    {
        if (t == null) return "<null>";
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent)
            path = p.name + "/" + path;
        return path;
    }

    // ===== Bone click / info panel =====

    // Adds a MeshCollider to the bone's actual render mesh (not worldBone
    // itself, which is often just a parent transform) so it can be hit by
    // the tap raycast in TryPickBoneAt. Skipped if a collider is already
    // present, so this is safe to call repeatedly (e.g. if OnEnable runs
    // again). Non-convex is fine here since this collider is raycast-only -
    // no Rigidbody involved, so the convex-required physics restrictions
    // don't apply.
    private void EnsureBoneCollider(BoneInfo info)
    {
        // BUG FIX: this used to call GetComponentInChildren<MeshFilter>()
        // (singular) - which returns the FIRST MeshFilter found ANYWHERE in
        // info.worldBone's subtree, with no regard for whether that mesh
        // actually belongs to a different, separately-registered bone
        // nested underneath it (e.g. info.worldBone is a group node like
        // "Cranium" and the first mesh found is really part of
        // "Parietal bone.r", which already has its own BoneInfo). Tapping
        // that mesh would then resolve to the WRONG bone - exactly the
        // "GameObject A clicked, GameObject B outlined" bug. Walk every
        // mesh piece instead, and only claim the ones that genuinely
        // belong to THIS bone (see IsOwnedByAnotherBone).
        var meshFilters = info.worldBone.GetComponentsInChildren<MeshFilter>();
        if (meshFilters.Length == 0)
        {
          //  Debug.Log($"[AnatomyScreenController] EnsureBoneCollider: no MeshFilter found under '{info.boneName}' - tap-to-select won't work for this bone.");
            return;
        }

        int registered = 0;
        foreach (var meshFilter in meshFilters)
        {
            if (meshFilter.sharedMesh == null) continue;

            if (IsOwnedByAnotherBone(meshFilter.transform, info))
            {
             //   Debug.Log($"[AnatomyScreenController] EnsureBoneCollider: skipping mesh '{meshFilter.name}' under '{info.boneName}' - it belongs to a separately registered bone, not this one.");
                continue;
            }

            var go = meshFilter.gameObject;
            var collider = go.GetComponent<Collider>();
            if (collider == null)
            {
                var meshCollider = go.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = meshFilter.sharedMesh;
                meshCollider.convex = false;
                collider = meshCollider;
            }

            // BUG FIX: this assignment used to be unconditional, so if two
            // bones' EnsureBoneCollider calls ever resolved to the SAME
            // collider, whichever ran last silently won - meaning which
            // bone a tap selected could depend on hierarchy/processing
            // order rather than on what was actually clicked. With the
            // ownership check above this should no longer be reachable,
            // but refuse the overwrite anyway rather than ever silently
            // reassigning an existing mapping.
            if (_infoByCollider.TryGetValue(collider, out var existingInfo) && existingInfo != info)
            {
                Debug.LogError($"[AnatomyScreenController] EnsureBoneCollider: collider on '{go.name}' is already registered to bone '{existingInfo.boneName}' - refusing to reassign it to '{info.boneName}'. This indicates two BoneInfo entries resolved to the same GameObject; check for overlapping/duplicate bone hierarchy.");
                continue;
            }

            _infoByCollider[collider] = info;
            registered++;
            //Debug.Log($"[AnatomyScreenController] EnsureBoneCollider: registered collider on '{go.name}' (layer '{LayerMask.LayerToName(go.layer)}') for bone '{info.boneName}'.");

            // A collider on a layer boneRaycastLayerMask doesn't include is
            // registered fine here but can never be hit by TryPickBoneAt's
            // raycast - taps on it will silently do nothing. This mismatch is
            // easy to introduce in the Inspector (layer renumbered, mask left
            // at its old value, etc.), so call it out explicitly instead of
            // leaving it to look like a script bug.
            if ((boneRaycastLayerMask.value & (1 << go.layer)) == 0)
            {
                Debug.LogWarning($"[AnatomyScreenController] '{go.name}' is on layer '{LayerMask.LayerToName(go.layer)}' (index {go.layer}), which is NOT included in boneRaycastLayerMask (value {boneRaycastLayerMask.value}) - tapping this bone will never register. Add '{LayerMask.LayerToName(go.layer)}' to the Bone Raycast Layer Mask field on AnatomyScreenController in the Inspector.");
            }
        }

        if (registered == 0)
        {
            Debug.Log($"[AnatomyScreenController] EnsureBoneCollider: no collider registered for '{info.boneName}' - every mesh piece under it either belongs to another bone or had no sharedMesh. Mesh-tap selection won't work for this bone; hotspot-button selection is unaffected.");
        }
    }

    // True if pieceTransform - or any ancestor of it, stopping at (and not
    // including) info.worldBone itself - is registered as a DIFFERENT
    // bone's worldBone. This is the ownership check that keeps a group
    // node (e.g. "Cranium") from claiming a mesh piece that really belongs
    // to one of its own more-specific descendant bones (e.g.
    // "Parietal bone.r"), which is what let the wrong bone get selected
    // before.
    private bool IsOwnedByAnotherBone(Transform pieceTransform, BoneInfo info)
    {
        for (Transform t = pieceTransform; t != null && t != info.worldBone; t = t.parent)
        {
            if (_boneTransformSet.Contains(t))
                return true;
        }
        return false;
    }

    // Standalone version of the click handlers below: given just a raw bone
    // GameObject name (not a pre-resolved BoneInfo from boneData), looks it
    // up directly in BoneDatabase.json via _boneDatabaseService and fills
    // the Info Panel. Useful for selection paths that only have a name to
    // go on (e.g. an external picking system, a test harness, or a bone
    // that isn't wired into boneData/worldBone at all). Falls back to the
    // same "not available" text as PopulateBoneDataFromSkeleton if the name
    // has no matching entry, so the panel is never left blank.
    public void ShowBoneInfoByName(string rawBoneName)
    {
        // If this name also happens to match a fully-wired boneData entry
        // (with a worldBone/collider), reuse that exact BoneInfo (and select
        // it, so toolbar actions like Isolate/Hide/Reset keep working).
        // Otherwise build a throwaway BoneInfo carrying just the name, so
        // DisplayBoneInfo still performs the same exact-key JSON lookup.
        _selectedBone = _boneTransformsByName.ContainsKey(rawBoneName)
            ? boneData.Find(b => b.boneName == rawBoneName)
            : new BoneInfo { boneName = rawBoneName };

        DisplayBoneInfo(_selectedBone);
        if (audioSource != null)
            audioSource.clip = _selectedBone?.audioClip;

        // Stay collapsed if the panel was minimized - the bone info is
        // still updated underneath, but only tapping the minimized circle
        // (ToggleMinimize via Header's tap-to-restore) should expand it.
        if (_isMinimized) return;

        _infoPanel.RemoveFromClassList("hidden");
        _infoPanel.schedule.Execute(ClampPanelToScreen).ExecuteLater(0);
    }

    // Clicked Bone Name -> "Fourth metacarpal bone.r" -> search BoneDatabase.json
    // using that EXACT string as the key -> displayName/baseName/description
    // -> Info Panel. info.boneName was captured directly from the clicked
    // GameObject's .name in PopulateBoneDataFromSkeleton and is never
    // altered, so this call always searches by the exact GameObject name -
    // NEVER by displayName or baseName. The .r/.l suffix on the GameObject
    // name is what's preserved end-to-end here, which is what keeps
    // "Fourth metacarpal bone.r" and "Fourth metacarpal bone.l" resolving
    // to their own distinct JSON entries instead of colliding.
    //
    // Re-queries _boneDatabaseService directly (rather than trusting the
    // info.title/info.description cached at populate time) so the search-
    // by-exact-key behavior is guaranteed on every click, not just once at
    // startup.
    // Single entry point for setting the Info Panel title. Stores the full
    // text and resets to the max font size, then schedules the actual fit
    // pass for right after the next layout - on the same frame the panel
    // becomes visible (or the text changes while already visible),
    // _titleLabel.resolvedStyle.width isn't reliably valid yet.
    private void SetTitleText(string text)
    {
        _pendingTitleText = text ?? string.Empty;
        _titleLabel.style.fontSize = TitleFontSizeMax;
        // Deliberately NOT setting _titleLabel.text here. This is called
        // while the Info Panel is still display:none (DisplayBoneInfo runs
        // before RemoveFromClassList("hidden")), so resolvedStyle.width is
        // 0/invalid at this exact instant. Painting the full text against
        // that invalid width is what caused the garbled/overlapping title
        // bug - the label would lay out as if it had ~0px to work with,
        // wrap to dozens of 1-character lines, and get all of them clipped
        // on top of each other by overflow:hidden. Only FitTitleLabel ever
        // assigns .text now, and only once it has confirmed a real width.
        FitTitleLabel(); // succeeds immediately for every click after the first (panel already laid out)
        _titleLabel.schedule.Execute(FitTitleLabel).ExecuteLater(0); // fallback for the very first open this session
    }

    // Shrinks the title's font size step by step (TitleFontSizeMax down to
    // TitleFontSizeMin) until it wraps to TitleMaxLines lines or fewer at
    // the width actually allocated to it. The full displayName is always
    // shown in the end - it is NEVER cut down to a partial name. If it
    // still doesn't fit within TitleMaxLines lines even at the minimum
    // readable size, the label is simply allowed to wrap past
    // TitleMaxLines (the panel/body layout below it just gets a little
    // less room) rather than dropping any words. Also called whenever the
    // title's available width changes (see the GeometryChangedEvent
    // registration in OnEnable), so this stays correct across panel
    // resizes, drags, and any screen resolution/orientation/aspect ratio.
    private void FitTitleLabel()
    {
        if (string.IsNullOrEmpty(_pendingTitleText)) return;

        float availableWidth = _titleLabel.resolvedStyle.width;
        if (availableWidth <= 0f || float.IsNaN(availableWidth))
        {
            // Layout hasn't resolved a real width yet (panel just went
            // display:none -> visible this frame). Leave the label blank
            // rather than painting anything against an invalid width - the
            // GeometryChangedEvent handler re-runs this the instant a real
            // width is resolved, same frame in practice.
            _titleLabel.text = string.Empty;
            return;
        }

        string text = _pendingTitleText;

        for (float size = TitleFontSizeMax; size >= TitleFontSizeMin; size -= TitleFontSizeStep)
        {
            if (CountWrappedLines(text, availableWidth, size) <= TitleMaxLines)
            {
                _titleLabel.style.fontSize = size;
                _titleLabel.text = text;
                return;
            }
        }

        // Even at the smallest readable size the full name wraps past
        // TitleMaxLines lines - show it in full anyway (extra lines)
        // rather than ever truncating the displayName.
        _titleLabel.style.fontSize = TitleFontSizeMin;
        _titleLabel.text = text;
    }

    private int CountWrappedLines(string text, float availableWidth, float fontSize)
    {
        _titleLabel.style.fontSize = fontSize;
        Vector2 measured = _titleLabel.MeasureTextSize(
            text, availableWidth, VisualElement.MeasureMode.AtMost, float.MaxValue, VisualElement.MeasureMode.Undefined);
        float lineHeight = fontSize * TitleLineHeightMultiplier;
        return Mathf.Max(1, Mathf.CeilToInt(measured.y / lineHeight - 0.01f));
    }

    private void DisplayBoneInfo(BoneInfo info)
    {
        string rawBoneName = info.boneName; // exact GameObject name = the JSON key/ID

        if (_boneDatabaseService.TryGetEntry(rawBoneName, out BoneDatabaseEntry entry))
        {
            SetTitleText(entry.displayName);
            _descriptionLabel.text = entry.description;
        }
        else
        {
            Debug.LogWarning($"[AnatomyScreenController] DisplayBoneInfo: no BoneDatabase.json entry for exact key '{rawBoneName}'.");
            SetTitleText(rawBoneName);
            _descriptionLabel.text = BoneInfoNotAvailableText;
        }

        // New bone -> always start reading from the top, and re-evaluate
        // whether the hint is needed once the new text has actually laid
        // out (same "wait one frame" reasoning as SetTitleText/FitTitleLabel:
        // contentContainer's resolvedStyle.height isn't valid until after
        // this frame's layout pass).
        _bodyScrollView.scrollOffset = Vector2.zero;
        _descriptionLabel.schedule.Execute(UpdateScrollHint).ExecuteLater(0);
    }

    // Shows/hides the "Scroll for more" affordance: visible only while the
    // description is actually taller than the visible body area AND the
    // player hasn't already scrolled to (near) the bottom. Re-run from
    // several triggers (new description text, panel resize, orientation
    // change, and every scroll) rather than computed once, since any of
    // those can flip whether the text currently overflows.
    private void UpdateScrollHint()
    {
        if (_scrollHint == null || _bodyScrollView == null) return;

        float contentHeight = _bodyScrollView.contentContainer.resolvedStyle.height;
        float viewportHeight = _bodyScrollView.contentViewport.resolvedStyle.height;
        if (float.IsNaN(contentHeight) || float.IsNaN(viewportHeight))
        {
            _scrollHint.AddToClassList("hidden");
            return;
        }

        const float slack = 2f; // avoid flicker from sub-pixel rounding
        bool overflows = contentHeight > viewportHeight + slack;
        bool atBottom = _bodyScrollView.scrollOffset.y >= (contentHeight - viewportHeight - slack);

        _scrollHint.EnableInClassList("hidden", !overflows || atBottom);
    }

    // Where a SelectStructure() call originated from. The workflow itself
    // (info panel, camera focus, outline, hide-mode) is identical for every
    // source - the source only decides what goes into audioSource.clip
    // below, which is the one place hotspot-tap and mesh-tap ever differed.
    private enum BoneSelectionSource
    {
        Hotspot,       // tracked hotspot button (OnBoneClicked) - also used by search, see OnSearchResultSelected
        MeshCollider   // direct 3D mesh tap (OnBoneColliderClicked), via TryPickBoneAt
    }

    // ===== Central anatomy-selection workflow =====
    // Every way of selecting a structure - search (OnSearchResultSelected),
    // UI hotspot (OnBoneClicked), and direct mesh-collider tap
    // (OnBoneColliderClicked) - funnels into this one method instead of
    // each keeping its own copy of the selection steps. Consolidates what
    // used to be two near-duplicate method bodies (OnBoneClicked /
    // OnBoneColliderClicked) that only ever differed in what they put in
    // audioSource.clip.
    //
    //   Search / Hotspot / Mesh collider
    //                 |
    //         SelectStructure()
    //                 |
    //   selected structure -> info panel -> camera focus -> outline
    //                 -> hide/isolation -> audio/TTS clip
    private void SelectStructure(BoneInfo info, BoneSelectionSource source)
    {
        Debug.Log($"[AnatomyScreenController] SelectStructure ({source}): '{info.boneName}'");

        StopAudio();

        _selectedBone = info;
        DisplayBoneInfo(info);

        // Only the mesh-tap path has no associated AudioClip - a raw tap on
        // the 3D mesh isn't tied to a specific clip the way the tracked
        // hotspot button (and, via OnBoneClicked, search) is.
        if (audioSource != null)
            audioSource.clip = (source == BoneSelectionSource.MeshCollider) ? null : info.audioClip;

        // Stay collapsed if the panel was minimized - the bone info is
        // still updated underneath, but only tapping the minimized circle
        // (ToggleMinimize via Header's tap-to-restore) should expand it.
        if (!_isMinimized)
        {
            _infoPanel.RemoveFromClassList("hidden");
            _infoPanel.schedule.Execute(ClampPanelToScreen).ExecuteLater(0);
        }

        // Every selection (not just a re-tap) now focuses/zooms the camera
        // onto the bone - see FocusOnSelectedBone. Done BEFORE the outline
        // is built below, so its bounds calculation only ever sees the
        // bone's real meshes, never the outline's own (slightly expanded)
        // copies of them.
        FocusOnSelectedBone();

        // Rebuild the outline against the EXACT GameObject that was
        // selected - selectedGameObject (set just above the SelectStructure
        // call, in OnBoneClicked/OnBoneColliderClicked) is the single
        // source of truth for "what got clicked": hit.collider.gameObject
        // for a mesh tap, info.worldBone.gameObject for hotspot/search.
        // This is deliberately NOT info.worldBone - info can describe a
        // parent/group bone whose subtree also contains other, separately
        // selectable structures nested under it (e.g. distinct named veins
        // parented under another vein's transform in the FBX hierarchy);
        // outlining info.worldBone via a recursive child search used to
        // pull those unrelated siblings' renderers in too, which is exactly
        // the "GameObject A clicked, GameObject B outlined" bug. Falling
        // back to info.worldBone only covers the (should-be-impossible)
        // case where selectedGameObject somehow never got set.
        Transform outlineTarget = selectedGameObject != null ? selectedGameObject.transform : info.worldBone;
        if (boneOutlineController != null)
        {
            Debug.Log("[AnatomyScreenController] SelectStructure: " +
                      $"selectedGameObject='{(selectedGameObject != null ? selectedGameObject.name : "<none>")}', " +
                      $"info.worldBone='{(info.worldBone != null ? info.worldBone.name : "<none>")}' -> " +
                      $"Final GameObject receiving the outline='{(outlineTarget != null ? outlineTarget.name : "<none>")}' " +
                      $"(HierarchyPath='{GetHierarchyPath(outlineTarget)}').");
            boneOutlineController.SetSelectedBone(outlineTarget);
        }

        // Isolate is active - keep it in sync with whichever structure is
        // now selected (see ApplyIsolateVisibility), so switching selection
        // via search/hotspot/mesh-tap while isolated isolates the NEW
        // structure instead of silently leaving the previous one as the
        // only visible/tappable bone.
        if (_isIsolated)
            ApplyIsolateVisibility();

        // Hide mode is active - tapping a bone is what actually hides it
        // (see OnHideClicked). This also clears the outline just built
        // above, since it would otherwise be left around a now-invisible
        // bone.
        if (_isHideModeActive)
            HideBone(info);

        OnStructureSelected?.Invoke(info);
    }

    // Hotspot-button tap (see the click wiring around PopulateBoneDataFromSkeleton)
    // and search selection (OnSearchResultSelected calls this directly) both
    // go through here.
    private void OnBoneClicked(BoneInfo info)
    {
        // No raycast hit exists on this path (hotspot button / search) -
        // the closest thing to "the exact GameObject" is the bone's own
        // worldBone GameObject itself.
        selectedGameObject = info.worldBone != null ? info.worldBone.gameObject : null;
        SelectStructure(info, BoneSelectionSource.Hotspot);
    }

    // Tap-on-mesh version of OnBoneClicked above: fires from a direct tap
    // on the bone's 3D collider (see TryPickBoneAt) rather than the tracked
    // hotspot button. Same exact-key BoneDatabase.json lookup as
    // OnBoneClicked via DisplayBoneInfo, minus the audio clip (none is
    // associated with the mesh-tap path) - see SelectStructure.
    // hitGameObject is the exact GameObject the raycast in TryPickBoneAt
    // hit - kept as-is in selectedGameObject rather than re-derived from
    // info, so the ground truth of "what was clicked" is never lost even
    // if info ends up describing a whole multi-part bone.
    private void OnBoneColliderClicked(BoneInfo info, GameObject hitGameObject)
    {
        selectedGameObject = hitGameObject;
        SelectStructure(info, BoneSelectionSource.MeshCollider);
    }

    // Raycasts from modelCamera through the tapped point (in BodyArea-local
    // UI coordinates) into the 3D scene, using the same viewport-space
    // conversion the hotspot buttons use in reverse (see the WorldToViewportPoint
    // block in Update()). A hit against a registered bone collider selects
    // that bone; a miss (empty space, or a collider not in boneData) does
    // nothing rather than deselecting - only the toolbar/close button
    // clears a selection.
    private void TryPickBoneAt(Vector2 localPos)
    {
        Debug.Log($"[AnatomyScreenController] TryPickBoneAt: tap at local {localPos}");

        if (modelCamera == null || _bodyArea == null)
        {
            Debug.Log("[AnatomyScreenController] TryPickBoneAt: aborted - modelCamera or _bodyArea is null.");
            return;
        }

        float areaWidth = _bodyArea.resolvedStyle.width;
        float areaHeight = _bodyArea.resolvedStyle.height;
        if (float.IsNaN(areaWidth) || float.IsNaN(areaHeight) || areaWidth <= 0f || areaHeight <= 0f)
        {
            Debug.Log($"[AnatomyScreenController] TryPickBoneAt: aborted - BodyArea not laid out yet (width={areaWidth}, height={areaHeight}).");
            return;
        }

        float u = localPos.x / areaWidth;
        float v = 1f - (localPos.y / areaHeight); // UI Toolkit Y is top-down; viewport Y is bottom-up

        Ray ray = modelCamera.ViewportPointToRay(new Vector3(u, v, 0f));
        Debug.Log($"[AnatomyScreenController] TryPickBoneAt: viewport=({u:F3},{v:F3}) ray origin={ray.origin} dir={ray.direction} layerMask={boneRaycastLayerMask.value}");

        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, boneRaycastLayerMask))
        {
            // hit.collider.gameObject is the ONE ground truth for "what did
            // the user actually click" - everything below is diagnostic
            // detail about that same GameObject, never a substitute for it.
            GameObject hitGameObject = hit.collider.gameObject;
            var hitRenderer = hitGameObject.GetComponent<Renderer>();
            string meshName = "<none>";
            if (hitRenderer is SkinnedMeshRenderer skinnedRenderer && skinnedRenderer.sharedMesh != null)
                meshName = skinnedRenderer.sharedMesh.name;
            else
            {
                var hitMeshFilter = hitGameObject.GetComponent<MeshFilter>();
                if (hitMeshFilter != null && hitMeshFilter.sharedMesh != null)
                    meshName = hitMeshFilter.sharedMesh.name;
            }

            Debug.Log("[AnatomyScreenController] TryPickBoneAt: " +
                      $"Clicked GameObject='{hitGameObject.name}', " +
                      $"Hit Collider GameObject='{hit.collider.gameObject.name}', " +
                      $"Renderer GameObject='{(hitRenderer != null ? hitRenderer.gameObject.name : "<none>")}', " +
                      $"Mesh='{meshName}', " +
                      $"HierarchyPath='{GetHierarchyPath(hitGameObject.transform)}' " +
                      $"at {hit.point}.");

            if (_infoByCollider.TryGetValue(hit.collider, out var info))
            {
                Debug.Log($"[AnatomyScreenController] TryPickBoneAt: resolved collider -> bone '{info.boneName}' " +
                          $"(worldBone='{(info.worldBone != null ? info.worldBone.name : "<null>")}') - " +
                          $"Final GameObject receiving the outline='{(info.worldBone != null ? info.worldBone.name : "<none>")}'.");
                OnBoneColliderClicked(info, hitGameObject);
            }
            else
            {
                Debug.Log($"[AnatomyScreenController] TryPickBoneAt: hit collider is NOT registered in _infoByCollider - it isn't one of the boneData meshes, or EnsureBoneCollider never ran for it.");
            }
        }
        else
        {
            Debug.Log("[AnatomyScreenController] TryPickBoneAt: ray hit nothing (missed the model entirely, or matched no collider on boneRaycastLayerMask).");
        }
    }

    private void HideInfoPanel()
    {
        StopAudio();
        _infoPanel?.AddToClassList("hidden");

        // Fully clear minimized state too, not just visually hide the
        // panel. Otherwise (e.g. after Reset) _isMinimized would stay
        // true while "hidden" is also set, and the next bone click's
        // `if (!_isMinimized)` guard would never remove "hidden" again -
        // the panel would be stuck invisible. Hiding should always
        // return the panel to a clean, non-minimized baseline.
        if (_isMinimized && _infoPanel != null)
        {
            _isMinimized = false;
            _infoPanel.RemoveFromClassList("minimized");
            _infoPanel.style.width = _panelSizeBeforeMinimize.x;
            _infoPanel.style.height = _panelSizeBeforeMinimize.y;
            if (_minimizeButton != null)
                _minimizeButton.pickingMode = PickingMode.Position;
        }

        if (boneOutlineController != null)
            boneOutlineController.ClearOutline();
    }

    // Keeps the info panel's actual on-screen box inside the visible
    // screen. Needed because left/top/width/height only start out
    // percent-based (see the .info-panel comment in the USS) - the moment
    // the player drags or resizes it, those become fixed pixel values that
    // no longer track the screen. Without this, rotating a device from
    // portrait to landscape (or resizing the Unity Game View) could leave
    // the panel partly or entirely off-screen, or taller than the screen
    // has room for. Reuses the same edge-margin/min-size rules as the
    // manual drag/resize handlers so the behavior stays consistent however
    // the panel got into its current position/size.
    private void ClampPanelToScreen()
    {
        if (_infoPanel == null || _infoPanel.ClassListContains("hidden") || _isMinimized) return;

        Vector2 screen = ScreenSize;
        float panelWidth = _infoPanel.resolvedStyle.width;
        float panelHeight = _infoPanel.resolvedStyle.height;
        float left = _infoPanel.resolvedStyle.left;
        float top = _infoPanel.resolvedStyle.top;

        if (float.IsNaN(panelWidth) || float.IsNaN(panelHeight) ||
            float.IsNaN(left) || float.IsNaN(top) ||
            panelWidth <= 0f || panelHeight <= 0f)
            return;

        // On a very short screen (e.g. a phone in landscape), infoPanelMinSize's
        // height floor can end up taller than the screen itself has room for.
        // Cap the panel's own height first so the clamp below has a realistic
        // box to work with, rather than one guaranteed to force it off-screen.
        float maxPanelHeight = Mathf.Max(infoPanelMinSize.y, screen.y - infoPanelEdgeVisibleMargin * 0.5f);
        if (panelHeight > maxPanelHeight)
        {
            panelHeight = maxPanelHeight;
            _infoPanel.style.height = panelHeight;
        }

        float minLeft = -(panelWidth - infoPanelEdgeVisibleMargin);
        float maxLeft = screen.x - infoPanelEdgeVisibleMargin;
        float minTop = -(panelHeight - infoPanelEdgeVisibleMargin);
        float maxTop = screen.y - infoPanelEdgeVisibleMargin;

        float clampedLeft = Mathf.Clamp(left, minLeft, maxLeft);
        float clampedTop = Mathf.Clamp(top, minTop, maxTop);

        if (!Mathf.Approximately(clampedLeft, left)) _infoPanel.style.left = clampedLeft;
        if (!Mathf.Approximately(clampedTop, top)) _infoPanel.style.top = clampedTop;
    }

    // ===== Info panel: minimize / restore =====

    private void ToggleMinimize()
    {
        _isMinimized = !_isMinimized;

        if (_isMinimized)
        {
            // Remember the current size so we can restore it exactly,
            // then hand width/height over to the .minimized USS rule,
            // which collapses the whole panel into a small round button.
            // Inline styles always win over stylesheet rules in UI
            // Toolkit, so the inline width/height set by drag-resize
            // must be cleared (not just overwritten) for the circle
            // size in USS to actually apply.
            _panelSizeBeforeMinimize = new Vector2(_infoPanel.resolvedStyle.width, _infoPanel.resolvedStyle.height);
            _infoPanel.AddToClassList("minimized");
            _infoPanel.style.width = StyleKeyword.Null;
            _infoPanel.style.height = StyleKeyword.Null;


            // The minimize button now visually fills the whole circle, and
            // the circle needs to be draggable. Rather than letting the
            // press land on the Button (which captures the pointer for its
            // own click handling via its internal Clickable manipulator,
            // then have Header steal that capture back) we just take the
            // button out of picking entirely. The press then targets
            // Header directly, so OnPanelDragPointerDown/Move/Up run with
            // no capture race at all. The button stays visible — only
            // pointer hit-testing is disabled — and tap-to-restore is
            // still handled by Header via _dragStartedOnMinimizedButton.
            _minimizeButton.pickingMode = PickingMode.Ignore;
        }
        else
        {
            _infoPanel.RemoveFromClassList("minimized");
            _infoPanel.style.width = _panelSizeBeforeMinimize.x;
            _infoPanel.style.height = _panelSizeBeforeMinimize.y;

            _minimizeButton.pickingMode = PickingMode.Position; // re-enable its own clicks

            // The remembered pre-minimize size may no longer fit if the
            // screen was rotated/resized while minimized.
            _infoPanel.schedule.Execute(ClampPanelToScreen).ExecuteLater(0);
        }
    }

    // ===== Audio row: responsive sizing =====

    // Scales the Play/Stop buttons (diameter + glyph font size + gap
    // between them), the pill tray they sit in, the icon badge, the
    // "AUDIO GUIDE" label's font size, and the row's own padding, all
    // linearly with the info panel's current resolved width. t=0 at
    // infoPanelMinSize.x (the panel's absolute narrowest), t=1 at
    // audioRowReferenceMaxWidth (the panel's widest resizable width) - so
    // the whole module stays proportional at any panel size instead of any
    // one piece going stale, and the row always occupies only the space
    // its content actually needs. Row right/bottom padding is the one
    // exception - it only breathes within a couple of px above a fixed
    // clearance floor, since that space is what keeps the button tray
    // clear of the resize-handle grip (see the audioRowPadding*Min
    // comments above) at every size.
    private void UpdateAudioRowScale()
    {
        if (_infoPanel == null || _playButton == null || _stopButton == null) return;

        float panelWidth = _infoPanel.resolvedStyle.width;
        if (float.IsNaN(panelWidth) || panelWidth <= 0f) return;

        float t = Mathf.InverseLerp(infoPanelMinSize.x, audioRowReferenceMaxWidth, panelWidth);
        t = Mathf.Clamp01(t);

        float buttonSize = Mathf.Lerp(audioButtonSizeMin, audioButtonSizeMax, t);
        float buttonFontSize = Mathf.Lerp(audioButtonFontSizeMin, audioButtonFontSizeMax, t);
        float buttonSpacing = Mathf.Lerp(audioButtonSpacingMin, audioButtonSpacingMax, t);
        float labelFontSize = Mathf.Lerp(audioLabelFontSizeMin, audioLabelFontSizeMax, t);
        float iconSize = Mathf.Lerp(audioIconSizeMin, audioIconSizeMax, t);
        float iconFontSize = Mathf.Lerp(audioIconFontSizeMin, audioIconFontSizeMax, t);
        float trayPadding = Mathf.Lerp(audioControlsTrayPaddingMin, audioControlsTrayPaddingMax, t);

        ApplyAudioButtonSize(_playButton, buttonSize, buttonFontSize);
        ApplyAudioButtonSize(_stopButton, buttonSize, buttonFontSize);
        _playButton.style.marginRight = buttonSpacing; // gap to the Stop button (see .play-btn in the USS)

        if (_audioLabel != null)
            _audioLabel.style.fontSize = labelFontSize;

        if (_audioIcon != null)
        {
            _audioIcon.style.width = iconSize;
            _audioIcon.style.height = iconSize;
            float iconRadius = iconSize * 0.5f;
            _audioIcon.style.borderTopLeftRadius = iconRadius;
            _audioIcon.style.borderTopRightRadius = iconRadius;
            _audioIcon.style.borderBottomLeftRadius = iconRadius;
            _audioIcon.style.borderBottomRightRadius = iconRadius;
        }
        if (_audioIconLabel != null)
            _audioIconLabel.style.fontSize = iconFontSize;

        if (_audioControlsTray != null)
        {
            _audioControlsTray.style.paddingLeft = trayPadding;
            _audioControlsTray.style.paddingRight = trayPadding;
            _audioControlsTray.style.paddingTop = trayPadding;
            _audioControlsTray.style.paddingBottom = trayPadding;

            // A true pill at every scale: radius = half the tray's own
            // height, which is exactly buttonSize + 2*trayPadding (the
            // buttons are the tray's only, full-height content). Computed
            // here rather than measured from resolvedStyle so it's correct
            // the same frame the padding/button size above are applied,
            // with no one-frame lag waiting for layout to catch up.
            float trayHeight = buttonSize + trayPadding * 2f;
            float trayRadius = trayHeight * 0.5f;
            _audioControlsTray.style.borderTopLeftRadius = trayRadius;
            _audioControlsTray.style.borderTopRightRadius = trayRadius;
            _audioControlsTray.style.borderBottomLeftRadius = trayRadius;
            _audioControlsTray.style.borderBottomRightRadius = trayRadius;
        }

        if (_audioRow != null)
        {
            _audioRow.style.paddingLeft = Mathf.Lerp(audioRowPaddingLeftMin, audioRowPaddingLeftMax, t);
            _audioRow.style.paddingTop = Mathf.Lerp(audioRowPaddingTopMin, audioRowPaddingTopMax, t);
            _audioRow.style.paddingRight = Mathf.Lerp(audioRowPaddingRightMin, audioRowPaddingRightMax, t);
            _audioRow.style.paddingBottom = Mathf.Lerp(audioRowPaddingBottomMin, audioRowPaddingBottomMax, t);
        }
    }

    // Applies a diameter to an audio button, keeping it perfectly circular
    // (border radius = half the diameter) at every size - USS's single
    // `border-radius: 30px` shorthand has no one-property equivalent on
    // IStyle, so all four corners are set explicitly here.
    private static void ApplyAudioButtonSize(Button button, float diameter, float fontSize)
    {
        if (button == null) return;

        button.style.width = diameter;
        button.style.height = diameter;
        button.style.fontSize = fontSize;

        float radius = diameter * 0.5f;
        button.style.borderTopLeftRadius = radius;
        button.style.borderTopRightRadius = radius;
        button.style.borderBottomLeftRadius = radius;
        button.style.borderBottomRightRadius = radius;
    }

    private void PlayAudio()
    {
        // Manual only - the player decides whether they want to hear it,
        // so this only fires from the Play button click, never
        // automatically on bone selection. Reads the currently-selected
        // bone's description text aloud via the device's own offline TTS
        // engine (see OfflineTextToSpeech) rather than playing a
        // pre-recorded audioClip.
        if (_selectedBone == null) return;
        OfflineTextToSpeech.Speak(_selectedBone.description);
    }

    private void StopAudio()
    {
        OfflineTextToSpeech.Stop();
    }

    // ===== Header gradient paint =====

    // Left edge of the header wash (matches the panel's existing solid
    // green so this reads as a continuation of the app's accent color,
    // not an unrelated new hue).
    private static readonly Color HeaderGradientStart = new Color(34f / 255f, 197f / 255f, 130f / 255f);
    // Right edge - blue, per the reference mock.
    private static readonly Color HeaderGradientEnd = new Color(59f / 255f, 130f / 255f, 246f / 255f);

    // Paints a left-to-right green->blue gradient across the header's own
    // rect. USS's background-color only accepts a single flat color, so a
    // gradient has to be drawn directly into the header's mesh instead -
    // this runs on layout/repaint automatically because it's registered
    // via generateVisualContent, so it never goes stale on resize/rotate.
    private void DrawHeaderGradient(MeshGenerationContext mgc)
    {
        Rect r = _dragHandle.contentRect;
        if (r.width <= 0f || r.height <= 0f) return;

        var mesh = mgc.Allocate(4, 6);

        // Two triangles covering the full header rect, left verts colored
        // with the start color and right verts with the end color -
        // UIToolkit interpolates vertex colors across the triangle for us,
        // which is what produces the smooth horizontal blend.
        mesh.SetNextVertex(new Vertex
        {
            position = new Vector3(0, 0, Vertex.nearZ),
            tint = HeaderGradientStart
        });
        mesh.SetNextVertex(new Vertex
        {
            position = new Vector3(r.width, 0, Vertex.nearZ),
            tint = HeaderGradientEnd
        });
        mesh.SetNextVertex(new Vertex
        {
            position = new Vector3(r.width, r.height, Vertex.nearZ),
            tint = HeaderGradientEnd
        });
        mesh.SetNextVertex(new Vertex
        {
            position = new Vector3(0, r.height, Vertex.nearZ),
            tint = HeaderGradientStart
        });

        mesh.SetNextIndex(0);
        mesh.SetNextIndex(1);
        mesh.SetNextIndex(2);
        mesh.SetNextIndex(0);
        mesh.SetNextIndex(2);
        mesh.SetNextIndex(3);
    }

    // ===== Info panel: drag to move =====

    private void OnPanelDragPointerDown(PointerDownEvent evt)
    {
        // Don't start a drag when the pointer actually landed on the close
        // or minimize buttons (they're children of the header) — let their
        // clicks go through instead of being swallowed by the drag. This
        // only applies while expanded; while minimized the minimize button
        // is set to PickingMode.Ignore (see ToggleMinimize), so every press
        // on the circle lands on Header itself and always starts a drag.
        if (!_isMinimized && (evt.target == _closeButton || evt.target == _minimizeButton)) return;

        _dragStartedOnMinimizedButton = _isMinimized;
        _isDraggingPanel = true;
        _dragPointerStart = evt.position;
        _panelPosAtDragStart = new Vector2(_infoPanel.resolvedStyle.left, _infoPanel.resolvedStyle.top);
        _dragHandle.CapturePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void OnPanelDragPointerMove(PointerMoveEvent evt)
    {
        if (!_isDraggingPanel) return;

        Vector2 delta = (Vector2)evt.position - _dragPointerStart;
        float panelWidth = _infoPanel.resolvedStyle.width;
        float panelHeight = _infoPanel.resolvedStyle.height;
        Vector2 screen = ScreenSize;

        // Unlike before, the panel is allowed to travel past the screen
        // edges (left/top can go negative, right/bottom can exceed the
        // screen size). Only `infoPanelEdgeVisibleMargin` px of the panel
        // is required to stay on-screen, so it's always draggable back.
        float minLeft = -(panelWidth - infoPanelEdgeVisibleMargin);
        float maxLeft = screen.x - infoPanelEdgeVisibleMargin;
        float minTop = -(panelHeight - infoPanelEdgeVisibleMargin);
        float maxTop = screen.y - infoPanelEdgeVisibleMargin;

        float newLeft = Mathf.Clamp(_panelPosAtDragStart.x + delta.x, minLeft, maxLeft);
        float newTop = Mathf.Clamp(_panelPosAtDragStart.y + delta.y, minTop, maxTop);

        _infoPanel.style.left = newLeft;
        _infoPanel.style.top = newTop;
        evt.StopPropagation();
    }


    private void OnPanelDragPointerUp(PointerUpEvent evt)
    {
        if (!_isDraggingPanel) return;
        _isDraggingPanel = false;
        _dragHandle.ReleasePointer(evt.pointerId);

        // Capturing the pointer above (to drag the minimized circle) steals
        // it away from the minimize button, so the button's own `clicked`
        // event never fires while minimized. If the pointer barely moved,
        // treat it as a tap instead and restore the panel.
        if (_dragStartedOnMinimizedButton)
        {
            float distance = Vector2.Distance(_dragPointerStart, evt.position);
            if (distance < MinimizedDragClickThreshold)
                ToggleMinimize();
        }
        _dragStartedOnMinimizedButton = false;

        evt.StopPropagation();
    }

    private void OnPanelDragPointerCaptureOut(PointerCaptureOutEvent evt)
    {
        _isDraggingPanel = false;
        _dragStartedOnMinimizedButton = false;
    }

    // ===== Info panel: drag bottom-right grip to resize =====

    private void OnPanelResizePointerDown(PointerDownEvent evt)
    {
        _isResizingPanel = true;
        _resizePointerStart = evt.position;
        _panelSizeAtResizeStart = new Vector2(_infoPanel.resolvedStyle.width, _infoPanel.resolvedStyle.height);
        _resizeHandle.CapturePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void OnPanelResizePointerMove(PointerMoveEvent evt)
    {
        if (!_isResizingPanel) return;

        Vector2 delta = (Vector2)evt.position - _resizePointerStart;
        float panelLeft = _infoPanel.resolvedStyle.left;
        float panelTop = _infoPanel.resolvedStyle.top;
        Vector2 screen = ScreenSize;

        float maxWidth = Mathf.Max(infoPanelMinSize.x, screen.x - panelLeft);
        float maxHeight = Mathf.Max(infoPanelMinSize.y, screen.y - panelTop);

        float newWidth = Mathf.Clamp(_panelSizeAtResizeStart.x + delta.x, infoPanelMinSize.x, maxWidth);
        float newHeight = Mathf.Clamp(_panelSizeAtResizeStart.y + delta.y, infoPanelMinSize.y, maxHeight);

        _infoPanel.style.width = newWidth;
        _infoPanel.style.height = newHeight;
        evt.StopPropagation();
    }

    private void OnPanelResizePointerUp(PointerUpEvent evt)
    {
        if (!_isResizingPanel) return;
        _isResizingPanel = false;
        _resizeHandle.ReleasePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void OnPanelResizePointerCaptureOut(PointerCaptureOutEvent evt)
    {
        _isResizingPanel = false;
    }

    // ===== 3D model touch gestures =====
    //
    // One finger: rotates the selected bone in place (if one is selected),
    // or orbits the camera (if none is). Two fingers: pinch/spread zooms,
    // moving both fingers together pans. Mouse wheel (desktop/editor) still
    // zooms too, via OnZoomWheel further down.
    //
    // _activePointers.Count is the single source of truth for which mode
    // is active - it's rebuilt from PointerDown/Move/Up/CaptureOut, and any
    // change in finger count ends whatever gesture was running so a lifted
    // or newly-added finger can never be misread as a continuation of the
    // previous gesture (see OnBodyAreaPointerUp/CaptureOut).
    //
    // Buttons/search bar/info panel/toolbar are all siblings of BodyArea in
    // the UXML, not descendants of it, so UI Toolkit's event bubbling never
    // routes their pointer events through these handlers in the first
    // place - the only thing that can land directly on BodyArea besides the
    // model itself is a bone hotspot button, guarded below.

    private void OnBodyAreaPointerDown(PointerDownEvent evt)
    {
        Debug.Log($"[AnatomyScreenController] OnBodyAreaPointerDown: target={evt.target}, pointerId={evt.pointerId}, position={evt.position}");

        if (evt.target is Button)
        {
            Debug.Log("[AnatomyScreenController] OnBodyAreaPointerDown: aborted - target is a Button (bone hotspot press).");
            return;
        }
        if (modelCamera == null)
        {
            Debug.Log("[AnatomyScreenController] OnBodyAreaPointerDown: aborted - modelCamera is null.");
            return;
        }

        _activePointers[evt.pointerId] = evt.position;
        _bodyArea.CapturePointer(evt.pointerId);

        // Any new touch takes manual control immediately - don't let an
        // in-progress bone-focus animation keep fighting the player's own
        // orbit/pan/swipe.
        _isFocusingBone = false;

        if (_activePointers.Count == 1)
        {
            BeginSingleFingerGesture();
            _singleFingerDownPos = evt.localPosition;
            _singleFingerMightBeTap = true;
        }
        else if (_activePointers.Count == 2)
        {
            BeginTwoFingerGesture();
            _singleFingerMightBeTap = false; // a 2nd finger joined - this was never a single tap
        }
        // 3rd+ finger: ignored, doesn't disturb an in-progress two-finger gesture.

        evt.StopPropagation();
    }

    private void OnBodyAreaPointerMove(PointerMoveEvent evt)
    {
        if (!_activePointers.ContainsKey(evt.pointerId)) return;

        Vector2 delta = (Vector2)evt.position - _activePointers[evt.pointerId];
        _activePointers[evt.pointerId] = evt.position;

        if (_activePointers.Count == 1)
        {
            UpdateSingleFingerGesture(delta);
            if (_singleFingerMightBeTap
                && Vector2.Distance(evt.localPosition, _singleFingerDownPos) > TapMovementThreshold)
            {
                _singleFingerMightBeTap = false;
            }
        }
        else if (_activePointers.Count == 2)
            UpdateTwoFingerGesture();

        evt.StopPropagation();
    }

    private void OnBodyAreaPointerUp(PointerUpEvent evt)
    {
        if (!_activePointers.ContainsKey(evt.pointerId)) return;

        bool wasSingleFingerTap = _activePointers.Count == 1 && _singleFingerMightBeTap;
        Vector2 tapPos = _singleFingerDownPos;
        Debug.Log($"[AnatomyScreenController] OnBodyAreaPointerUp: activePointers={_activePointers.Count}, mightBeTap={_singleFingerMightBeTap}, wasSingleFingerTap={wasSingleFingerTap}");

        _activePointers.Remove(evt.pointerId);
        _bodyArea.ReleasePointer(evt.pointerId);
        EndAllGestures();

        if (wasSingleFingerTap)
            TryPickBoneAt(tapPos);

        evt.StopPropagation();
    }

    private void OnBodyAreaPointerCaptureOut(PointerCaptureOutEvent evt)
    {
        _activePointers.Remove(evt.pointerId);
        EndAllGestures();
    }

    private void EndAllGestures()
    {
        // A finger count change (2->1, 1->0, etc.) always ends whatever was
        // running. A fresh gesture only starts on the next PointerDownEvent
        // - this is what stops a remaining finger's stale reference point
        // from causing a sudden jump when the other finger lifts.
        _isOrbiting = false;
        _isSwipingBone = false;
        _isTwoFingerGesture = false;
        _singleFingerMightBeTap = false;
    }

    private void BeginSingleFingerGesture()
    {
        // Single-finger swipe always orbits the camera around the whole
        // model, regardless of whether a bone is currently selected -
        // selecting a bone (via hotspot click or mesh tap) no longer
        // switches the gesture into per-bone rotation.
        if (skeletonRoot != null)
        {
            _isOrbiting = true;
            _isSwipingBone = false;
        }
    }

    private void UpdateSingleFingerGesture(Vector2 delta)
    {
        if (_isSwipingBone && _selectedBone?.worldBone != null)
        {
            // Rotate around camera-relative world axes (rather than the
            // bone's own local axes) so a horizontal swipe always spins the
            // bone about world-up and a vertical swipe always tips it
            // "away from the camera", regardless of the bone's own
            // orientation or how the camera is currently angled. This is
            // accumulated into targetRotation and smoothed onto the actual
            // transform in Update(), not applied directly here.
            Vector3 camRight = modelCamera.transform.right;
            Quaternion rot = _selectedBone.targetRotation;
            rot = Quaternion.AngleAxis(delta.x * boneRotateSensitivity, Vector3.up) * rot;
            rot = Quaternion.AngleAxis(delta.y * boneRotateSensitivity, camRight) * rot;
            _selectedBone.targetRotation = rot;
        }
        else if (_isOrbiting)
        {
            _orbitYaw += delta.x * rotateSpeed;
            _orbitPitch = Mathf.Clamp(_orbitPitch + delta.y * rotateSpeed, MinPitch, MaxPitch);
        }
    }

    private void BeginTwoFingerGesture()
    {
        _isOrbiting = false;
        _isSwipingBone = false;
        _isTwoFingerGesture = true;

        var positions = new List<Vector2>(_activePointers.Values);
        _pinchPrevDistance = Vector2.Distance(positions[0], positions[1]);
        _twoFingerPrevMidpoint = (positions[0] + positions[1]) * 0.5f;
    }

    private void UpdateTwoFingerGesture()
    {
        if (!_isTwoFingerGesture) return;

        var positions = new List<Vector2>(_activePointers.Values);
        float currentDistance = Vector2.Distance(positions[0], positions[1]);
        Vector2 currentMidpoint = (positions[0] + positions[1]) * 0.5f;

        float pinchDelta = currentDistance - _pinchPrevDistance;
        Vector2 panDelta = currentMidpoint - _twoFingerPrevMidpoint;
        _pinchPrevDistance = currentDistance;
        _twoFingerPrevMidpoint = currentMidpoint;

        // Pan and zoom are applied independently every frame, each driven
        // by its own raw delta, rather than picking only one of them per
        // frame based on which motion happened to be larger. That either/or
        // comparison used to "lock" the gesture: moving two fingers roughly
        // along the axis of their own separation (e.g. two fingers placed
        // left/right and swiped left/right) naturally produces a bit of
        // spurious spread/pinch every frame from ordinary human finger
        // imprecision, and that spurious pinchDelta would keep beating the
        // genuine panDelta on that axis for the rest of the gesture - so
        // panning along that particular direction silently stopped working
        // until the fingers lifted and a new gesture began. Applying both
        // continuously means the midpoint's X and Y are always free to pan
        // the camera the moment they move, in any direction, while a real
        // pinch/spread still zooms - each is governed only by its own delta
        // this frame, with no cross-frame "mode" to get stuck in.
        Vector3 worldPan = (-panDelta.x * modelCamera.transform.right + panDelta.y * modelCamera.transform.up)
            * cameraPanSensitivity * Mathf.Max(_orbitDistance, 0.01f);
        _targetPanOffset += worldPan;

        // Fingers spreading apart (positive delta) = zoom in = smaller
        // orbit distance; pinching together = zoom out.
        _targetOrbitDistance = Mathf.Clamp(
            _targetOrbitDistance - pinchDelta * pinchZoomSensitivity,
            zoomDistanceRange.x, zoomDistanceRange.y);
    }

    private void OnZoomWheel(WheelEvent evt)
    {
        if (modelCamera == null || skeletonRoot == null) return;

        // Manual scroll-zoom also takes over from an in-progress focus
        // animation immediately, same reasoning as OnBodyAreaPointerDown.
        _isFocusingBone = false;

        _targetOrbitDistance = Mathf.Clamp(_targetOrbitDistance + evt.delta.y * zoomSpeed, zoomDistanceRange.x, zoomDistanceRange.y);
        evt.StopPropagation();
    }

    // Places the camera on a sphere of radius _orbitDistance around
    // (_orbitPivot + _panOffset) at (_orbitYaw, _orbitPitch), then looks at
    // that point. Standard spherical-to-cartesian orbit camera math, called
    // once per frame from Update() after zoom/pan smoothing is applied.
    private void ApplyOrbitCamera()
    {
        float yawRad = _orbitYaw * Mathf.Deg2Rad;
        float pitchRad = _orbitPitch * Mathf.Deg2Rad;

        Vector3 dir = new Vector3(
            Mathf.Sin(yawRad) * Mathf.Cos(pitchRad),
            Mathf.Sin(pitchRad),
            Mathf.Cos(yawRad) * Mathf.Cos(pitchRad));

        // If the auto-frame block in OnEnable never ran (e.g. modelCamera or
        // skeletonRoot wasn't assigned yet), _orbitDistance defaults to 0f,
        // which places the camera exactly at the pivot — inside the model,
        // seeing nothing, until the user happens to scroll-zoom. Never let
        // it collapse to zero.
        float distance = _orbitDistance > 0.01f ? _orbitDistance : zoomDistanceRange.x;
        Vector3 pivot = _orbitPivot + _panOffset;

        modelCamera.transform.position = pivot + dir * distance;
        modelCamera.transform.LookAt(pivot, Vector3.up);
    }

    // Selecting a bone (see OnBoneClicked /
    // OnBoneColliderClicked) calls this to smoothly move the camera in on
    // just that bone: same pivot+distance orbit model as everything else
    // (see ApplyOrbitCamera), just handed a different pivot (the bone's
    // own bounds center, via _panOffset) and a tighter distance, animated
    // over selectedBoneZoomDuration instead of snapped or SmoothDamp-
    // chased. Yaw/pitch are left exactly where they are - this only moves
    // the camera toward the bone and zooms in, it never re-orients the
    // viewing angle. The bone's own Transform is never written to.
    private void FocusOnSelectedBone()
    {
        if (modelCamera == null || _selectedBone?.worldBone == null) return;

        // Combined bounds of every real renderer under the bone - not just
        // the first one GetComponentInChildren happens to find - so bones
        // made of several mesh pieces are framed by their true full size.
        // Same computation BoneOutlineController uses to size the outline,
        // via the shared static helper. Falls back to a small nominal
        // bounds around the bone's own position if it has no real geometry
        // to measure, so focusing still lands roughly in the right place
        // rather than doing nothing.


        if (!BoneOutlineController.TryComputeCombinedBounds(_selectedBone.worldBone, out Bounds bounds))
            bounds = new Bounds(_selectedBone.worldBone.position, Vector3.one * 0.1f);




        // Same "fit the bounds into the vertical FOV" math as the initial
        // whole-skeleton auto-frame in OnEnable, just fed one bone's
        // radius and its own configurable padding multiplier instead of
        // the whole skeleton's.
        float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
        float fitDistance = radius / Mathf.Sin(modelCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float desiredDistance = Mathf.Clamp(
            fitDistance * selectedBoneZoomDistance,
            selectedBoneMinZoomDistance,
            selectedBoneMaxZoomDistance);
        Debug.Log($"[Focus] selectedBoneZoomDistance={selectedBoneZoomDistance}, fitDistance={fitDistance:F3}, raw={fitDistance * selectedBoneZoomDistance:F3}, clamped={desiredDistance:F3} (min={selectedBoneMinZoomDistance}, max={selectedBoneMaxZoomDistance})");

        // pivot = _orbitPivot + _panOffset (see ApplyOrbitCamera), so to
        // make the *effective* pivot equal the bone's center, the pan
        // offset needs to land on (bone center - the base skeleton pivot).
        Vector3 targetPanOffset = bounds.center - _orbitPivot;

        _focusStartPanOffset = _panOffset;
        _focusTargetPanOffset = targetPanOffset;
        _focusStartDistance = _orbitDistance;
        _focusTargetDistance = desiredDistance;
        _focusElapsed = 0f;
        _isFocusingBone = true;

        // Keep the gesture-driven targets in sync with where the focus
        // animation is heading, so if the player grabs the view right as
        // (or just after) the animation finishes, the very next
        // SmoothDamp step continues from the same place instead of
        // snapping back toward a stale pre-focus target.
        _targetPanOffset = targetPanOffset;
        _targetOrbitDistance = desiredDistance;
    }

    // Encapsulates every bone renderer's world-space bounds into one box, so
    // the camera can frame the model regardless of where skeletonRoot's own
    // pivot happens to sit relative to the actual geometry.
    private Bounds ComputeSkeletonBounds()
    {
        var renderers = new List<Renderer>();
        foreach (var root in _activeRoots)
        {
            if (root == null) continue;
            renderers.AddRange(root.GetComponentsInChildren<Renderer>());
        }

        // Pass 1: collect every renderer with real (non-zero) geometry.
        var candidates = new List<Renderer>(renderers.Count);
        foreach (var r in renderers)
        {
            if (r.bounds.size.sqrMagnitude < 0.0001f)
            {
                Debug.Log($"[AnatomyScreenController] skipping degenerate bounds: {r.name} center={r.bounds.center}");
                continue;
            }
            candidates.Add(r);
        }

        if (candidates.Count == 0)
        {
            Debug.LogWarning($"[AnatomyScreenController] ComputeSkeletonBounds: no renderers with real geometry found under '{skeletonRoot.name}' - falling back to a 1x1x1 box at {skeletonRoot.position}.");
            return new Bounds(skeletonRoot.position, Vector3.one);
        }

        // Pass 2: find a robust "typical" center. A plain average is itself
        // vulnerable to a single far-flung outlier, so use the median of each
        // axis instead - it barely moves even if one bone's Transform is
        // broken and sitting thousands of units away.
        var xs = new List<float>(candidates.Count);
        var ys = new List<float>(candidates.Count);
        var zs = new List<float>(candidates.Count);
        foreach (var r in candidates)
        {
            xs.Add(r.bounds.center.x);
            ys.Add(r.bounds.center.y);
            zs.Add(r.bounds.center.z);
        }
        xs.Sort(); ys.Sort(); zs.Sort();
        Vector3 medianCenter = new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);

        // Pass 3: reject any renderer whose bounds sit implausibly far from
        // that median - this is exactly the case that silently blew out the
        // combined box before (small, valid-looking size, but positioned way
        // outside the actual model), so we now name-and-shame it instead of
        // letting Encapsulate() quietly absorb it.
        const float OutlierMultiplier = 25f;
        var distances = new List<float>(candidates.Count);
        foreach (var r in candidates) distances.Add(Vector3.Distance(r.bounds.center, medianCenter));
        var sortedDistances = new List<float>(distances);
        sortedDistances.Sort();
        float medianDistance = sortedDistances[sortedDistances.Count / 2];
        // Guard against a degenerate (near-zero) medianDistance on tiny/dense
        // models so the multiplier doesn't reject everything.
        float threshold = Mathf.Max(medianDistance * OutlierMultiplier, 5f);

        Bounds bounds = default;
        bool hasBounds = false;

        for (int i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i];
            if (distances[i] > threshold)
            {
                Debug.LogWarning($"[AnatomyScreenController] ComputeSkeletonBounds: rejecting outlier bone '{r.name}' - center={r.bounds.center}, distance={distances[i]:F2} from median {medianCenter} (threshold {threshold:F2}). This bone's Transform is very likely broken/mispositioned - check it in the model root hierarchy.");
                continue;
            }

            if (!hasBounds)
            {
                bounds = r.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        Debug.Log($"[AnatomyScreenController] ComputeSkeletonBounds for '{skeletonRoot.name}': " +
                  $"center={bounds.center}, size={bounds.size}, extentsMagnitude={bounds.extents.magnitude:F2}, " +
                  $"renderers={candidates.Count}");

        return bounds;
    }
    // ===== Search =====

    private void OnSearchChanged(ChangeEvent<string> evt)
    {
        string query = evt.newValue?.Trim().ToLowerInvariant() ?? "";

        // SearchPlaceholder isn't present in this UXML (TextField's own
        // placeholder-text attribute covers that already) - guarded rather
        // than assumed, so a null Q<Label> lookup here can't throw on every
        // keystroke and take the whole search feature down with it.
        if (_searchPlaceholder != null)
            _searchPlaceholder.style.display = string.IsNullOrEmpty(query) ? DisplayStyle.Flex : DisplayStyle.None;
        _searchClearButton.EnableInClassList("hidden", string.IsNullOrEmpty(query));

        foreach (var el in _boneElements)
        {
            var info = _dataByElement[el];
            bool matches = string.IsNullOrEmpty(query) || info.title.ToLowerInvariant().Contains(query);
            el.EnableInClassList("bone-dimmed", !matches);
        }

        UpdateSearchResults(query);
    }

    // Rebuilds the SearchResults dropdown from the current (already
    // lowercased/trimmed) query. Matches against every bone in boneData -
    // the full BoneDatabase-backed list, not just the ones with a UXML
    // hotspot element - via a case-insensitive Contains() on the display
    // name, so "rib" matches "Thoracic Cage (Ribs)", "fem" matches
    // "Femur", "skull" matches any skull-related bone, etc. Hidden
    // entirely while the query is empty; shows "No bones found" as a
    // non-interactive row when there are zero matches.
    private void UpdateSearchResults(string query)
    {
        if (_searchResults == null) return;

        _searchResults.contentContainer.Clear();

        if (string.IsNullOrEmpty(query))
        {
            _searchResults.AddToClassList("hidden");
            return;
        }

        var matches = boneData.FindAll(info =>
            !string.IsNullOrEmpty(info.title) && info.title.ToLowerInvariant().Contains(query));
        matches.Sort((a, b) => string.Compare(a.title, b.title, System.StringComparison.OrdinalIgnoreCase));

        if (matches.Count == 0)
        {
            var empty = new Label("No bones found");
            empty.AddToClassList("search-results-empty");
            _searchResults.Add(empty);
        }
        else
        {
            foreach (var info in matches)
            {
                var item = new Button(() => OnSearchResultSelected(info)) { text = info.title };
                item.AddToClassList("search-result-item");
                _searchResults.Add(item);
            }
        }

        PositionSearchResults();
        _searchResults.RemoveFromClassList("hidden");
    }

    // Measures SearchBar's actual resolved bottom edge and sets
    // SearchResults' style.top from it, converted into SearchResults'
    // own parent's local space. Needed because SearchResults now lives as
    // the LAST child of Screen (see the UXML comment) rather than nested
    // directly under SearchBar, specifically so it always paints/picks in
    // front of BodyArea - a plain static CSS offset can't reach across
    // that gap, and TopBar's height isn't fixed anyway (depends on its
    // text content), so it couldn't be a hardcoded px value even if it
    // were still nested.
    private void PositionSearchResults()
    {
        if (_searchResults == null || _searchBar == null) return;
        var parent = _searchResults.parent;
        if (parent == null) return;

        Vector2 bottomLeftWorld = _searchBar.LocalToWorld(new Vector2(0f, _searchBar.resolvedStyle.height));
        Vector2 bottomLeftLocal = parent.WorldToLocal(bottomLeftWorld);
        if (float.IsNaN(bottomLeftLocal.y)) return;

        _searchResults.style.top = bottomLeftLocal.y + 8f; // small gap below the field
    }

    // Fires when the player taps a row in the SearchResults dropdown.
    private void OnSearchResultSelected(BoneInfo info)
    {
        // 1. Close the search results - always, regardless of whether this
        // bone has a resolved worldBone below.
        _searchResults.AddToClassList("hidden");
        _searchResults.contentContainer.Clear();

        if (info.worldBone != null)
        {
            // 2-6: OnBoneClicked already finds/selects the bone, builds its
            // outline highlight, focuses/zooms the camera onto it, and
            // opens the Info Panel with its name, description, and audio
            // controls - the exact same path a hotspot-button tap uses, so
            // a search-selected bone behaves identically to a tapped one.
            OnBoneClicked(info);
        }
        else
        {
            // BoneDatabase.json entry exists (it's in boneData/matched the
            // search) but this bone has no counterpart under skeletonRoot -
            // there's nothing to select/highlight or focus the camera on,
            // so just show what info is available.
            Debug.LogWarning($"[AnatomyScreenController] OnSearchResultSelected: '{info.boneName}' has no resolved worldBone - showing Info Panel without camera focus/highlight.");
            ShowBoneInfoByName(info.boneName);
        }
    }

    private void ClearSearch()
    {
        _searchField.SetValueWithoutNotify("");
        OnSearchChanged(ChangeEvent<string>.GetPooled("", ""));
    }

    // ===== Toolbar actions =====

    public void OnResetClicked()
    {
        ClearSearch();
        HideInfoPanel();

        // Clear any inline left/top/width/height set by manual drag or
        // resize (and cleared-to-a-pixel-value by minimize/restore above),
        // so the panel falls back to its default percent-based position
        // and size from the .info-panel USS rule instead of reopening
        // wherever it was last left.
        if (_infoPanel != null)
        {
            _infoPanel.style.left = StyleKeyword.Null;
            _infoPanel.style.top = StyleKeyword.Null;
            _infoPanel.style.width = StyleKeyword.Null;
            _infoPanel.style.height = StyleKeyword.Null;
        }

        foreach (var info in boneData)
        {
            foreach (var rend in GetBoneRenderers(info))
                rend.enabled = true;

            if (info.worldBone != null)
            {
                info.worldBone.rotation = info.originalRotation;
                info.targetRotation = info.originalRotation;
            }
        }

        // Restore the Mesh Collider's enabled state for every bone, in case any were disabled by Hide or Isolate.
        foreach (var info in boneData)
        {
            foreach (var rend in GetBoneRenderers(info))
            {
                var col = rend.GetComponent<Collider>();
                if (col != null) col.enabled = true;
            }
        }

        if (modelCamera != null && skeletonRoot != null)
        {
            _isFocusingBone = false;
            _orbitYaw = _initialYaw;
            _orbitPitch = _initialPitch;
            _orbitDistance = _initialDistance;
            _targetOrbitDistance = _initialDistance;
            _panOffset = Vector3.zero;
            _targetPanOffset = Vector3.zero;
            ApplyOrbitCamera();
        }

        _selectedBone = null;
        _undoStack.Clear();
        _undoButton.SetEnabled(false);
        _undoButton.AddToClassList("toolbar-btn-disabled");

        _isIsolated = false;
        _isolateUndo = null;
        _isolateButton.RemoveFromClassList("toolbar-btn-active");

        _isHideModeActive = false;
        _hideButton.RemoveFromClassList("toolbar-btn-active");
        if (_hideLabel != null) _hideLabel.text = "Hide Off";

        // Lets a restricted mode (currently only BaselineAssessmentController's
        // pretest/posttest) re-apply whatever it had isolated the model down to,
        // since everything above just unconditionally re-enabled every bone's
        // renderer/collider. Same "public hook another controller sets" pattern
        // as BackNavigationOverride - AnatomyScreenController itself has no idea
        // what baseline mode is.
        AfterReset?.Invoke();
    }

    private void OnIsolateClicked()
    {
        if (_isIsolated)
        {
            // Second tap: toggle back off. Restore every bone's visibility
            // (and the outline, if a bone is still selected) by invoking
            // the exact same delegate that was pushed onto the undo stack.
            if (_isolateUndo != null)
            {
                // If nothing else was pushed since isolating, this is still
                // the top of the undo stack - pop it so global Undo doesn't
                // later re-run a restore that's already been done.
                if (_undoStack.Count > 0 && _undoStack.Peek() == _isolateUndo)
                {
                    _undoStack.Pop();
                    bool hasMore = _undoStack.Count > 0;
                    _undoButton.SetEnabled(hasMore);
                    _undoButton.EnableInClassList("toolbar-btn-disabled", !hasMore);
                }

                _isolateUndo.Invoke();
                _isolateUndo = null;
            }

            _isIsolated = false;
            _isolateButton.RemoveFromClassList("toolbar-btn-active");
            return;
        }

        if (_selectedBone?.worldBone == null) return;

        // Remember every bone's current visibility AND collider state so
        // this can be undone as a single step, then show/enable only the
        // selected bone. Colliders are captured (and later restored) right
        // alongside renderers - see ApplyIsolateVisibility for why a
        // collider has to be disabled too, not just the renderer.
        var prevRendererStates = new Dictionary<Renderer, bool>();
        var prevColliderStates = new Dictionary<Collider, bool>();
        foreach (var info in boneData)
        {
            foreach (var rend in GetBoneRenderers(info))
            {
                prevRendererStates[rend] = rend.enabled;

                var col = rend.GetComponent<Collider>();
                if (col != null) prevColliderStates[col] = col.enabled;
            }
        }

        ApplyIsolateVisibility();

        // With every other bone hidden there's nothing left for the
        // outline to distinguish the selected bone from - clear it, same
        // reasoning as OnHideClicked/HideInfoPanel.
        if (boneOutlineController != null)
            boneOutlineController.ClearOutline();

        var isolatedBone = _selectedBone;
        _isolateUndo = () =>
        {
            foreach (var kv in prevRendererStates)
                kv.Key.enabled = kv.Value;
            foreach (var kv in prevColliderStates)
                kv.Key.enabled = kv.Value;

            // Restore the outline too, but only if this bone is still selected.
            if (boneOutlineController != null && _selectedBone == isolatedBone && isolatedBone.worldBone != null)
                boneOutlineController.SetSelectedBone(isolatedBone.worldBone);
        };

        PushUndo(_isolateUndo);
        _isIsolated = true;
        _isolateButton.AddToClassList("toolbar-btn-active");
    }

    // Re-applies isolate's "only the selected structure is visible/tappable"
    // rule to whichever bone is currently _selectedBone. Called both when
    // Isolate is first switched on above, and from SelectStructure whenever
    // the selection changes while isolate is still active - so picking a
    // different structure via search/hotspot/mesh-tap while isolated moves
    // the isolation to the NEW structure instead of leaving the previous
    // one as the only visible bone.
    //
    // Disables the collider alongside the renderer for every non-selected
    // bone - not just the renderer - for the same reason HideBone does:
    // the raycast in TryPickBoneAt hits a GameObject's Collider
    // independently of its Renderer, so a renderer-only disable still
    // leaves an "isolated-out" bone tappable even though it's invisible.
    //
    // Never touches prevRendererStates/prevColliderStates (the snapshot
    // from before isolate was switched on, used to fully restore on
    // toggle-off) - it only flips current enabled state, so the eventual
    // restore is unaffected by however many different bones got isolated
    // in between.
    // Every Renderer that actually belongs to this bone - a bone can be made
    // of more than one mesh piece (see EnsureBoneCollider's own "BUG FIX"
    // comment above for why that matters for colliders), so anything that
    // shows/hides a bone has to walk all of them, not just the first one
    // GetComponentInChildren happens to find. Empty (never null) if the bone
    // has no world Transform.
    private static Renderer[] GetBoneRenderers(BoneInfo info)
    {
        return info?.worldBone != null
            ? info.worldBone.GetComponentsInChildren<Renderer>(true)
            : new Renderer[0];
    }

    private void ApplyIsolateVisibility()
    {
        foreach (var info in boneData)
        {
            bool visible = (info == _selectedBone);
            foreach (var rend in GetBoneRenderers(info))
            {
                rend.enabled = visible;

                var col = rend.GetComponent<Collider>();
                if (col != null) col.enabled = visible;
            }
        }
    }

    // Toggles "hide mode" on/off. Turning it on/off never hides anything by
    // itself - it only changes what tapping a bone does afterward (see
    // HideBone, called from OnBoneClicked/OnBoneColliderClicked). The bone
    // that was already selected before this toggle stays visible until the
    // player actually taps a bone while the mode is active.
    private void OnHideClicked()
    {
        _isHideModeActive = !_isHideModeActive;

        if (_isHideModeActive)
        {
            _hideButton.AddToClassList("toolbar-btn-active");
            if (_hideLabel != null) _hideLabel.text = "Hide On";
        }
        else
        {
            _hideButton.RemoveFromClassList("toolbar-btn-active");
            if (_hideLabel != null) _hideLabel.text = "Hide Off";
        }
    }

    // Actually hides a bone's renderer. Called from the bone-click handlers
    // while hide mode is active - see OnHideClicked above.
    private void HideBone(BoneInfo info)
    {
        if (info?.worldBone == null) return;

        var renderers = GetBoneRenderers(info);
        if (renderers.Length == 0) return;

        // The raycast in TryPickBoneAt hits a piece's Collider independently
        // of its Renderer - disabling only the renderer left the mesh
        // invisible but still tappable. Disable both, for every mesh piece
        // this bone is made of, so a hidden bone can no longer be selected
        // via mesh tap.
        var hiddenBone = info;
        var prevRendererStates = new Dictionary<Renderer, bool>();
        var prevColliderStates = new Dictionary<Collider, bool>();
        foreach (var rend in renderers)
        {
            prevRendererStates[rend] = rend.enabled;
            rend.enabled = false;

            var col = rend.GetComponent<Collider>();
            if (col == null) continue;
            prevColliderStates[col] = col.enabled;
            col.enabled = false;
        }


        // The outline was being built from this bone's renderer; leaving it
        // in place after the renderer is disabled left a stale/degenerate
        // outline (rendering solid black) around an invisible bone.
        if (boneOutlineController != null)
            boneOutlineController.ClearOutline();

        if (_selectedBone == hiddenBone)
        {
            _selectedBone = null;
            HideInfoPanel();



            _isFocusingBone = false;
            _targetPanOffset = _panOffset;
            _targetOrbitDistance = _orbitDistance;

        }


        PushUndo(() =>
        {
            foreach (var kv in prevRendererStates)
                kv.Key.enabled = kv.Value;
            foreach (var kv in prevColliderStates)
                kv.Key.enabled = kv.Value;

            // Restore the outline too, but only if this bone is still selected.
            if (boneOutlineController != null && _selectedBone == hiddenBone && hiddenBone.worldBone != null)
                boneOutlineController.SetSelectedBone(hiddenBone.worldBone);
        });
    }

    private void OnUndoClicked()
    {
        if (_undoStack.Count == 0) return;
        _undoStack.Pop().Invoke();

        bool hasMore = _undoStack.Count > 0;
        _undoButton.SetEnabled(hasMore);
        _undoButton.EnableInClassList("toolbar-btn-disabled", !hasMore);
    }

    private void PushUndo(System.Action undo)
    {
        _undoStack.Push(undo);
        _undoButton.SetEnabled(true);
        _undoButton.RemoveFromClassList("toolbar-btn-disabled");
    }

    // ===================================================================
    // ===== Play Mode integration API ==================================
    // ===================================================================
    // Everything below is the ONLY surface AnatomyPlayModeController is
    // allowed to touch on this class. It never reaches into boneData,
    // _selectedBone, the toolbar buttons, or the isolate/hide undo state
    // directly - see AnatomyPlayModeController.cs's own header comment.

    /// <summary>Every structure on the currently active anatomy system,
    /// in the same order PopulateBoneDataFromSkeleton built them. Read-only -
    /// Play Mode never adds, removes, or reorders entries here.</summary>
    public IReadOnlyList<BoneInfo> AllBoneData => boneData;

    /// <summary>Looks up a structure's BoneDatabase.json entry (displayName,
    /// baseName, description) by its exact GameObject name - the same
    /// exact-key lookup DisplayBoneInfo uses. DisplayName is the answer
    /// Play Mode should guess against; baseName must never be used for
    /// that.</summary>
    public bool TryGetBoneDatabaseEntry(BoneInfo info, out BoneDatabaseEntry entry)
    {
        entry = null;
        if (info == null) return false;
        return _boneDatabaseService.TryGetEntry(info.boneName, out entry);
    }

    // ===================================================================
    // ===== Cross-system structure lookup (Student Progress Tracker) ====
    // ===================================================================
    // Everything below exists so StudentProgressController's Performance
    // Panel can compute "completed / actual total" per anatomy system
    // WITHOUT this screen being open and without requiring `system` to be
    // the currently active one. Every configured AnatomySystemConfig's
    // modelRoot Transform hierarchy exists in the scene the whole time
    // this component's GameObject does (ResolveAnatomySystem only
    // SetActive(false)s the inactive ones - it never destroys them), so
    // the exact same matching rules PopulateBoneDataFromSkeleton uses for
    // the active system can be re-run here, on demand, for any system.
    // This deliberately mutates no runtime state (boneData,
    // _boneTransformsByName, etc.) - it's a pure read.

    /// <summary>Every structure name that is actually selectable for
    /// `system` - i.e. every BoneDatabase.json entry for that system with
    /// a matching GameObject (or a GameObject with its own mesh) anywhere
    /// under that system's configured modelRoot. Uses the identical
    /// matching/dedup rules as PopulateBoneDataFromSkeleton, so this is
    /// always the same "total" Play Mode itself reaches 100% against for
    /// `system` - never a raw BoneDatabase.json entry count, which can
    /// differ from what's actually reachable in the model (see the
    /// unmatched-entry warning in PopulateBoneDataFromSkeleton). Returns
    /// an empty set if `system` has no configured entry, or its
    /// modelRoot/databaseJson aren't assigned.</summary>
    public HashSet<string> GetSelectableStructureKeys(AnatomySystem system)
    {
        var result = new HashSet<string>();

        var config = anatomySystems.Find(c => c != null && c.system == system);
        if (config == null || config.modelRoot == null || config.databaseJson == null)
        {
            Debug.LogWarning($"[AnatomyScreenController] GetSelectableStructureKeys: no usable 'Anatomy Systems' entry for '{system}' - returning an empty set.");
            return result;
        }

        var systemDatabase = new BoneDatabaseService();
        systemDatabase.Load(config.databaseJson.text);

        var descendants = new List<Transform>();
        CollectDescendants(config.modelRoot, descendants);

        foreach (var t in descendants)
        {
            string rawName = t.name;
            if (result.Contains(rawName)) continue; // duplicate GameObject name - keep only the first, same rule PopulateBoneDataFromSkeleton uses.

            bool found = systemDatabase.TryGetEntry(rawName, out _);
            var meshFilter = t.GetComponent<MeshFilter>();
            bool hasOwnMesh = meshFilter != null && meshFilter.sharedMesh != null;

            if (!found && !hasOwnMesh) continue; // organizational/group node, not a real structure.

            result.Add(rawName);
        }

        return result;
    }

    /// <summary>How many structures are actually selectable for `system` -
    /// GetSelectableStructureKeys(system).Count. This is the denominator
    /// StudentProgressController's Performance Panel uses internally for
    /// its percentage - it must never be shown to the student directly.</summary>
    public int GetSelectableStructureCount(AnatomySystem system) => GetSelectableStructureKeys(system).Count;

    /// <summary>Overwrites the Info Panel's title through the same
    /// auto-fit path (FitTitleLabel) normal bone titles use, so Play
    /// Mode's blanks/answer text sizes exactly like DisplayBoneInfo's does.</summary>
    public void SetInfoPanelTitle(string text) => SetTitleText(text);

    /// <summary>Overwrites the Info Panel's description/body text. Play
    /// Mode uses this for guessing blanks, hint progress, and
    /// correct/incorrect feedback instead of the bone's real description.</summary>
    public void SetInfoPanelDescription(string text)
    {
        if (_descriptionLabel != null) _descriptionLabel.text = text;
    }

    /// <summary>Applies (or removes) the .play-mode-start-pos USS class on
    /// the Info Panel, which overrides its default top (58%) with 50% for
    /// the moment Play Mode first opens it. Left/width/height are
    /// untouched. Call with true once when Play Mode activates, and with
    /// false when it deactivates so Explore Mode's Info Panel goes back to
    /// falling through to the normal .info-panel default (or wherever the
    /// player last dragged it, same as OnResetClicked's StyleKeyword.Null
    /// behavior).</summary>
    public void SetInfoPanelPlayModeStartPosition(bool active)
    {
        if (_infoPanel == null) return;

        if (active) _infoPanel.AddToClassList("play-mode-start-pos");
        else _infoPanel.RemoveFromClassList("play-mode-start-pos");
    }

    /// <summary>Turns off Explore Mode's Isolate Selected Bone and Hide
    /// Mode if either is currently active, via their own existing
    /// toggle-off paths (so the undo stack / renderer-collider state they
    /// each own is restored correctly). Call this when Play Mode is
    /// switched on, so its own Isolate Answered visibility never has to
    /// fight with Explore Mode's isolate/hide state.</summary>
    public void ExitExploreOnlyModes()
    {
        if (_isHideModeActive) OnHideClicked();
        if (_isIsolated) OnIsolateClicked();
    }

    /// <summary>Shows or hides the Explore Mode search bar (and, when
    /// hiding, closes any open results dropdown and clears the typed
    /// query via ClearSearch). Play Mode has no use for looking a
    /// structure up by name - that would let a player skip straight to
    /// the answer - so this is called with false when Play Mode switches
    /// on and true when it switches off.</summary>
    public void SetSearchBarVisible(bool visible)
    {
        if (_searchBar != null)
            _searchBar.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        if (!visible)
            ClearSearch();
    }

    /// <summary>Selects and focuses the camera on a specific structure
    /// exactly as if the player had tapped its hotspot button - builds
    /// the outline highlight, focuses/zooms the camera, opens the Info
    /// Panel, and fires OnStructureSelected so Play Mode picks it up as
    /// the current question. Falls back to ShowBoneInfoByName (no
    /// camera focus) for a structure with no resolved worldBone, same
    /// as OnSearchResultSelected does. Used by Play Mode's "Unanswered"
    /// toolbar button to jump straight to a structure the player hasn't
    /// identified yet.</summary>
    public void SelectAndFocusStructure(BoneInfo info)
    {
        if (info == null) return;

        if (info.worldBone != null)
            OnBoneClicked(info);
        else
            ShowBoneInfoByName(info.boneName);
    }

    /// <summary>Applies (or clears) Play Mode's "Isolate Answered"
    /// visibility rule: every structure for which isAnswered(info) is
    /// true stays visible/tappable, every other structure is hidden -
    /// same renderer+collider enable/disable ApplyIsolateVisibility uses
    /// for Isolate Selected Bone, just keyed off the predicate instead of
    /// _selectedBone. Deliberately independent of _undoStack/_isolateUndo -
    /// Isolate Answered is a Play Mode concept and was never meant to be
    /// undoable through Explore Mode's Undo button. Passing active=false
    /// restores every structure to visible/tappable.</summary>
    /// <summary>Switches off the renderer and collider of every piece of the
    /// active model that isn't part of a filtered-in structure, so a filtered
    /// open draws (and can be tapped on) only those structures. Runs once per
    /// open from OnEnable; a no-op when no filter is set.
    ///
    /// boneData has already been narrowed to the filtered structures by
    /// PopulateBoneDataFromSkeleton, so "keep" is simply every renderer in a
    /// registered bone's subtree. The colliders being switched off here are
    /// ones an EARLIER unfiltered open of this same model already generated -
    /// this open doesn't create any for them, and disabling them is what stops
    /// a tap on a hidden structure from still selecting it.</summary>
    private void ApplyStructureFilterVisibility()
    {
        _filterHiddenRenderers.Clear();
        _filterHiddenColliders.Clear();

        if (_structureFilter == null || _activeRoots.Count == 0) return;

        var keep = new HashSet<Renderer>();
        foreach (var info in boneData)
        {
            if (info.worldBone == null) continue;
            foreach (var rend in info.worldBone.GetComponentsInChildren<Renderer>(true))
                keep.Add(rend);
        }

        foreach (var root in _activeRoots)
        {
            if (root == null) continue;

            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                if (keep.Contains(rend)) continue;

                if (rend.enabled)
                {
                    rend.enabled = false;
                    _filterHiddenRenderers.Add(rend);
                }

                var col = rend.GetComponent<Collider>();
                if (col != null && col.enabled)
                {
                    col.enabled = false;
                    _filterHiddenColliders.Add(col);
                }
            }
        }

        Debug.Log($"[AnatomyScreenController] ApplyStructureFilterVisibility: showing {boneData.Count} filtered structure(s) " +
                  $"across {_activeRoots.Count} active model(s), hid {_filterHiddenRenderers.Count} other renderer(s).");
    }

    public void SetIsolateAnsweredActive(bool active, System.Func<BoneInfo, bool> isAnswered)
    {
        foreach (var info in boneData)
        {
            bool visible = !active || (isAnswered != null && isAnswered(info));
            foreach (var rend in GetBoneRenderers(info))
            {
                rend.enabled = visible;

                var col = rend.GetComponent<Collider>();
                if (col != null) col.enabled = visible;
            }
        }
    }

    /// <summary>Hides/shows the top bar's Back button entirely - used by
    /// BaselineAssessmentController so a student can't exit the mandatory
    /// Pretest/Posttest by backing out mid-test. Unlike BackNavigationOverride
    /// (which only redirects where Back goes), this removes the option
    /// completely. Callers are responsible for setting this back to true
    /// once their restricted mode ends, the same way every other mode
    /// restores whatever screen chrome it changed.</summary>
    public void SetBackButtonVisible(bool visible)
    {
        if (_backButton == null) return;
        _backButton.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void OnBackClicked()
    {
        //reset the camera and bone selection before going back
        OnResetClicked();

        // hide the active fbx
        HideActiveModels();

        if (BackNavigationOverride != null)
            BackNavigationOverride.Invoke();
        else
            UIManager.Instance.ShowStudentExplore3d();
    }

    public void ResetView() => OnResetClicked();

    // ===== Controls guide (floating "how to use the BottomToolbar" help) =====

    private void OnControlsGuideButtonClicked() => OpenControlsGuide();

    // Clicking anywhere on the dimmed backdrop dismisses the guide, same as
    // the explicit close/"Got it" buttons - a click that reaches the
    // backdrop can only have missed the card itself, since the card sits on
    // top of it and stops the event there.
    private void OnControlsGuideCloseRequested(ClickEvent evt) => CloseControlsGuide();

    private void OpenControlsGuide()
    {
        _controlsGuideOverlay?.RemoveFromClassList("hidden");
    }

    private void CloseControlsGuide()
    {
        _controlsGuideOverlay?.AddToClassList("hidden");
    }
}