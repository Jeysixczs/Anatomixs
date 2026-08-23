using Anatomia3D.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Admin Quiz Management -> Image-Based question authoring: lets a teacher pick
    /// the exact 3D structure that becomes a question's correct answer.
    ///
    /// This script owns ONLY the teacher-selection UI/state (the "SELECT THIS
    /// STRUCTURE" button and which structure is currently highlighted). It never
    /// duplicates selection, camera-focus, or database-lookup logic - all of that
    /// stays in AnatomyScreenController and is reused through its small public API,
    /// the same way AnatomyPlayModeController reuses it:
    ///   - OnStructureSelected (event)
    ///   - TryGetBoneDatabaseEntry
    ///   - BackNavigationOverride
    ///
    /// Attach to the SAME GameObject as AnatomyScreenController (the one with the
    /// UIDocument) so it can query the same UXML tree for the Info Panel's teacher
    /// button. Teacher Selection Mode is switched on only from Admin Quiz
    /// Management's Image-Based system cards (see RequestTeacherSelectionModeOnOpen /
    /// UIManager.ShowStudentAnatomyScreenForTeacherSelection) - there is no way to
    /// reach it from the normal Student Explore 3D flow, and it never changes
    /// anything about that flow.
    ///
    /// Identity rules (do not deviate - see AdminQuizManagementController's plan doc):
    ///   - BoneInfo.boneName / BoneDatabaseEntry.boneId = structureKey (internal id)
    ///   - BoneDatabaseEntry.displayName                = structureDisplayName,
    ///                                                     which becomes CorrectAnswer
    ///   - BoneDatabaseEntry.baseName                    = NEVER used here
    /// </summary>
    [RequireComponent(typeof(AnatomyScreenController))]
    public class AnatomyTeacherSelectionController : MonoBehaviour
    {
        /// <summary>Everything Admin Quiz Management needs to save an Image-Based
        /// question, handed back the instant the teacher taps "SELECT THIS
        /// STRUCTURE".</summary>
        public class TeacherStructureSelectionResult
        {
            public string SystemKey;            // e.g. "Skeletal"
            public string SystemDisplayName;     // e.g. "Skeletal System"
            public string StructureKey;          // e.g. "Femur.l" - internal id only
            public string StructureDisplayName;  // e.g. "Left Femur" - the answer
        }

        /// <summary>Fired the moment the teacher confirms a structure. Subscribers
        /// (AdminQuizManagementController) should treat this as the final word - the
        /// screen navigates back to Admin Quiz Management right after this fires.</summary>
        public event System.Action<TeacherStructureSelectionResult> OnTeacherStructureSelected;

        private AnatomyScreenController _screen;
        private VisualElement _root;

        private bool _isSelectionModeActive;
        private bool _uiWired;

        // Set by RequestTeacherSelectionModeOnOpen (called from UIManager right after
        // Admin Quiz Management opens this screen for a specific system). WireUi runs
        // one frame after OnEnable, so the request may arrive before this screen's UI
        // is wired - same pending/consume pattern AnatomyPlayModeController uses for
        // _pendingAutoStart.
        private bool _pendingActivate;
        private AnatomySystem _pendingSystem;
        private string _activeSystemKey;

        // The structure currently highlighted in the 3D view, resolved via
        // AnatomyScreenController.TryGetBoneDatabaseEntry - null until the teacher
        // taps a structure. Never auto-picked; see the plan's section 2/12.
        private AnatomyScreenController.BoneInfo _highlightedStructure;
        private BoneDatabaseEntry _highlightedEntry;

        // ===== UI refs (queried from the same root AnatomyScreenController uses) =====
        private VisualElement _teacherSelectionControls;
        private Button _selectStructureButton;
        private VisualElement _audioRow;
        private Label _teacherSelectionModeLabel;

        public bool IsSelectionModeActive => _isSelectionModeActive;

        private void OnEnable()
        {
            _screen = GetComponent<AnatomyScreenController>();

            // Same lifecycle note as AnatomyPlayModeController: UIManager only
            // toggles AnatomyScreenController's `enabled` flag on each screen visit,
            // never this component's - so all per-visit wiring must happen from
            // OnScreenReady, not from this OnEnable running again.
            _screen.OnStructureSelected -= HandleStructureSelected;
            _screen.OnStructureSelected += HandleStructureSelected;
            _screen.OnScreenReady -= HandleScreenReady;
            _screen.OnScreenReady += HandleScreenReady;

            // Cover the screen instance already active right now too.
            HandleScreenReady();
        }

        private void OnDisable()
        {
            if (_screen != null)
            {
                _screen.OnStructureSelected -= HandleStructureSelected;
                _screen.OnScreenReady -= HandleScreenReady;
            }

            if (_selectStructureButton != null)
                _selectStructureButton.clicked -= OnSelectStructureClicked;

            if (_isSelectionModeActive)
                DeactivateSelectionMode();

            _pendingActivate = false;
        }

        // Re-wires this script's UI against whatever UXML tree AnatomyScreenController
        // just finished building - called every time OnScreenReady fires (i.e. every
        // time the Anatomy Screen opens), not just once.
        private void HandleScreenReady()
        {
            _isSelectionModeActive = false;
            _highlightedStructure = null;
            _highlightedEntry = null;
            _uiWired = false;

            if (_screen != null)
                _screen.BackNavigationOverride = null;

            var uiDocument = GetComponent<UIDocument>();
            _root = uiDocument != null ? uiDocument.rootVisualElement : null;
            if (_root == null) return;

            _root.schedule.Execute(WireUi).ExecuteLater(0);
        }

        private void WireUi()
        {
            _teacherSelectionControls = _root.Q<VisualElement>("TeacherSelectionControls");
            _selectStructureButton = _root.Q<Button>("TeacherSelectStructureButton");
            _audioRow = _root.Q<VisualElement>("AudioRow");
            _teacherSelectionModeLabel = _root.Q<Label>("TeacherSelectionModeLabel");

            if (_teacherSelectionControls == null || _selectStructureButton == null)
            {
                Debug.LogWarning("[AnatomyTeacherSelectionController] 'TeacherSelectionControls'/" +
                                  "'TeacherSelectStructureButton' not found in UXML - the teacher " +
                                  "structure picker cannot be shown. See AnatomyScreen.uxml.");
            }

            if (_teacherSelectionControls != null)
                _teacherSelectionControls.AddToClassList("hidden");

            if (_teacherSelectionModeLabel != null)
                _teacherSelectionModeLabel.AddToClassList("hidden");

            if (_selectStructureButton != null)
            {
                _selectStructureButton.clicked -= OnSelectStructureClicked;
                _selectStructureButton.clicked += OnSelectStructureClicked;
                _selectStructureButton.SetEnabled(false);
            }

            _uiWired = true;
            TryConsumePendingActivation();
        }

        // ===== Teacher Selection Mode on/off =====

        /// <summary>Called by UIManager.ShowStudentAnatomyScreenForTeacherSelection right
        /// after this screen was opened from Admin Quiz Management's Image-Based system
        /// cards - the only place Teacher Selection Mode is ever started from. Safe to
        /// call before WireUi has run (it always is - WireUi is scheduled a frame after
        /// ShowScreen completes): the request is queued and only actually applied once
        /// _uiWired confirms WireUi has queried this visit's real UI elements.</summary>
        public void RequestTeacherSelectionModeOnOpen(AnatomySystem system)
        {
            _pendingSystem = system;
            _pendingActivate = true;
            TryConsumePendingActivation();
        }

        private void TryConsumePendingActivation()
        {
            if (!_pendingActivate || !_uiWired || _isSelectionModeActive) return;
            _pendingActivate = false;
            ActivateSelectionMode(_pendingSystem);
        }

        private void ActivateSelectionMode(AnatomySystem system)
        {
            _isSelectionModeActive = true;
            _activeSystemKey = system.ToString();
            _highlightedStructure = null;
            _highlightedEntry = null;

            // Back returns to Admin Quiz Management (not Student Explore 3D) for the
            // whole time this mode is active - cleared in HandleScreenReady/OnDisable.
            if (_screen != null)
                _screen.BackNavigationOverride = ReturnToAdminQuizManagement;

            // Searching by name or listening to the audio guide have no place in the
            // teacher picker - same treatment Play Mode gives them for its own reasons.
            _screen?.SetSearchBarVisible(false);
            _audioRow?.AddToClassList("hidden");

            _teacherSelectionControls?.RemoveFromClassList("hidden");
            _teacherSelectionModeLabel?.RemoveFromClassList("hidden");
            _selectStructureButton?.SetEnabled(false);
        }

        private void DeactivateSelectionMode()
        {
            _isSelectionModeActive = false;

            if (_screen != null)
            {
                _screen.BackNavigationOverride = null;
                _screen.SetSearchBarVisible(true);
            }

            _teacherSelectionControls?.AddToClassList("hidden");
            _teacherSelectionModeLabel?.AddToClassList("hidden");
            _audioRow?.RemoveFromClassList("hidden");
        }

        private void ReturnToAdminQuizManagement()
        {
            DeactivateSelectionMode();
            UIManager.Instance.ShowAdminQuizManagement();
        }

        // ===== Structure selection =====

        private void HandleStructureSelected(AnatomyScreenController.BoneInfo info)
        {
            if (!_isSelectionModeActive || info == null) return;

            if (!_screen.TryGetBoneDatabaseEntry(info, out var entry) || entry == null)
            {
                Debug.LogWarning($"[AnatomyTeacherSelectionController] No BoneDatabase entry for " +
                                  $"'{info.boneName}' - cannot use it as a question answer.");
                _highlightedStructure = null;
                _highlightedEntry = null;
                _selectStructureButton?.SetEnabled(false);
                return;
            }

            _highlightedStructure = info;
            _highlightedEntry = entry;
            _selectStructureButton?.SetEnabled(true);
        }

        private void OnSelectStructureClicked()
        {
            if (!_isSelectionModeActive || _highlightedStructure == null || _highlightedEntry == null) return;

            var result = new TeacherStructureSelectionResult
            {
                SystemKey = _activeSystemKey,
                SystemDisplayName = $"{_activeSystemKey} System",
                StructureKey = _highlightedStructure.boneName,
                StructureDisplayName = _highlightedEntry.displayName,
            };

            OnTeacherStructureSelected?.Invoke(result);

            ReturnToAdminQuizManagement();
        }
    }
}
