using Anatomia3D.UI;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Student quiz gameplay -> Image-Based question's "View on 3D Model" button:
    /// highlights and focuses the camera on the exact structure a teacher picked for
    /// that question (see AnatomyTeacherSelectionController on the authoring side),
    /// WITHOUT revealing its name - the student still has to type the answer back on
    /// the quiz card (StudentQuizGameplayController). Read-only: there is no "select
    /// a structure" step here, and free exploration (rotate/zoom/tap other
    /// structures) is left exactly as Explore Mode already allows, which is fine -
    /// none of it can hand the student the answer, because every selection made
    /// while this mode is active gets its Info Panel text masked the same way (see
    /// HandleStructureSelected). The Info Panel also gets a "Locate Structure" button
    /// (QuizHighlightControls/LocateStructureButton in AnatomyScreen.uxml) so a
    /// student who has wandered off exploring - or whose Info Panel is now showing a
    /// different structure they tapped out of curiosity - can jump straight back to
    /// the actual target without leaving and re-entering from the quiz card.
    ///
    /// Reuses the same public API AnatomyPlayModeController and
    /// AnatomyTeacherSelectionController already reuse:
    ///   - SelectAndFocusStructure / AllBoneData (resolve + highlight the structure)
    ///   - OnStructureSelected (mask the panel on every tap, not just the first)
    ///   - SetInfoPanelTitle / SetInfoPanelDescription (the actual masking)
    ///   - BackNavigationOverride (Back returns to the quiz, not Student Explore 3D)
    ///
    /// Attach to the same GameObject as AnatomyScreenController (same as those two).
    /// </summary>
    [RequireComponent(typeof(AnatomyScreenController))]
    public class AnatomyQuizHighlightController : MonoBehaviour
    {
        private const string MaskedTitle = "Identify this structure";
        private const string MaskedDescription = "Go back to the quiz and type its name there.";

        private AnatomyScreenController _screen;
        private VisualElement _root;

        private bool _isHighlightModeActive;
        private bool _uiWired;

        // Pending/consume pair, same reasoning as AnatomyPlayModeController's
        // _pendingAutoStart and AnatomyTeacherSelectionController's _pendingActivate -
        // UIManager's onReady callback can run before WireUi has, since WireUi is
        // scheduled a frame after OnScreenReady fires.
        private bool _pendingActivate;
        private string _pendingStructureKey;

        // The question's actual target, resolved once in ActivateHighlightMode and
        // reused by the Locate button - re-resolving by key on every click would work
        // too, but keeping the BoneInfo means Locate still works even if two
        // structures somehow shared a very similar name.
        private AnatomyScreenController.BoneInfo _targetStructure;

        // ===== UI refs (queried from the same root AnatomyScreenController uses) =====
        private VisualElement _quizHighlightControls;
        private Button _locateStructureButton;
        private VisualElement _audioRow;

        private void OnEnable()
        {
            _screen = GetComponent<AnatomyScreenController>();

            // Same lifecycle note as the other two reuse-controllers: UIManager only
            // toggles AnatomyScreenController's `enabled` flag per visit, never this
            // component's - per-visit wiring belongs in HandleScreenReady, not here.
            _screen.OnStructureSelected -= HandleStructureSelected;
            _screen.OnStructureSelected += HandleStructureSelected;
            _screen.OnScreenReady -= HandleScreenReady;
            _screen.OnScreenReady += HandleScreenReady;

            HandleScreenReady();
        }

        private void OnDisable()
        {
            if (_screen != null)
            {
                _screen.OnStructureSelected -= HandleStructureSelected;
                _screen.OnScreenReady -= HandleScreenReady;
            }

            if (_locateStructureButton != null)
                _locateStructureButton.clicked -= OnLocateStructureClicked;

            if (_isHighlightModeActive)
                DeactivateHighlightMode();

            _pendingActivate = false;
        }

        private void HandleScreenReady()
        {
            _isHighlightModeActive = false;
            _uiWired = false;
            _targetStructure = null;

            if (_screen != null)
                _screen.BackNavigationOverride = null;

            var uiDocument = GetComponent<UIDocument>();
            _root = uiDocument != null ? uiDocument.rootVisualElement : null;
            if (_root == null) return;

            _root.schedule.Execute(WireUi).ExecuteLater(0);
        }

        private void WireUi()
        {
            _quizHighlightControls = _root.Q<VisualElement>("QuizHighlightControls");
            _locateStructureButton = _root.Q<Button>("LocateStructureButton");
            _audioRow = _root.Q<VisualElement>("AudioRow");

            if (_quizHighlightControls == null || _locateStructureButton == null)
            {
                Debug.LogWarning("[AnatomyQuizHighlightController] 'QuizHighlightControls'/" +
                                  "'LocateStructureButton' not found in UXML - the Locate button " +
                                  "cannot be shown. See AnatomyScreen.uxml.");
            }

            _quizHighlightControls?.AddToClassList("hidden");

            if (_locateStructureButton != null)
            {
                _locateStructureButton.clicked -= OnLocateStructureClicked;
                _locateStructureButton.clicked += OnLocateStructureClicked;
            }

            _uiWired = true;
            TryConsumePendingActivation();
        }

        /// <summary>Called by UIManager.ShowStudentAnatomyScreenForQuizHighlight right
        /// after this screen opens from the quiz card's "View on 3D Model" button -
        /// the only place this mode is ever started from.</summary>
        public void RequestHighlightModeOnOpen(string structureKey)
        {
            _pendingStructureKey = structureKey;
            _pendingActivate = true;
            TryConsumePendingActivation();
        }

        private void TryConsumePendingActivation()
        {
            if (!_pendingActivate || !_uiWired || _isHighlightModeActive) return;
            _pendingActivate = false;
            ActivateHighlightMode(_pendingStructureKey);
        }

        private void ActivateHighlightMode(string structureKey)
        {
            _isHighlightModeActive = true;

            // Back returns to the in-progress quiz attempt (not Student Explore 3D)
            // for as long as this mode is active - cleared in HandleScreenReady/OnDisable.
            if (_screen != null)
                _screen.BackNavigationOverride = () => UIManager.Instance.ShowStudentQuizGameplayResume();

            // No skipping to the answer via search - same rationale Play Mode already
            // uses SetSearchBarVisible(false) for (see that method's own doc comment).
            _screen?.SetSearchBarVisible(false);
            _audioRow?.AddToClassList("hidden");
            _quizHighlightControls?.RemoveFromClassList("hidden");

            // Structure names/GameObject keys are consistent case in practice, but
            // matching case-insensitively costs nothing and avoids a "stale/missing
            // structure" false alarm over nothing but a casing mismatch.
            _targetStructure = _screen?.AllBoneData
                .FirstOrDefault(b => b != null && string.Equals(b.boneName, structureKey, StringComparison.OrdinalIgnoreCase));

            if (_targetStructure == null)
            {
                Debug.LogWarning($"[AnatomyQuizHighlightController] No structure named '{structureKey}' " +
                                  "found on the current model - the question's saved StructureKey may be stale.");
                _locateStructureButton?.SetEnabled(false);
                return;
            }

            _locateStructureButton?.SetEnabled(true);
            LocateTargetStructure();
        }

        private void DeactivateHighlightMode()
        {
            _isHighlightModeActive = false;
            _targetStructure = null;

            if (_screen != null)
            {
                _screen.BackNavigationOverride = null;
                _screen.SetSearchBarVisible(true);
            }

            _quizHighlightControls?.AddToClassList("hidden");
            _audioRow?.RemoveFromClassList("hidden");
        }

        // Re-selects and re-focuses the camera on the question's actual target -
        // used both for the initial highlight and every time the student taps
        // "Locate Structure" afterwards, so wandering off (rotating away, tapping a
        // different structure, minimizing the panel) is always recoverable without
        // leaving this screen.
        private void LocateTargetStructure()
        {
            if (_targetStructure == null || _screen == null) return;
            _screen.SelectAndFocusStructure(_targetStructure);
            // Fires OnStructureSelected, which HandleStructureSelected (below)
            // immediately masks - nothing further needed here.
        }

        private void OnLocateStructureClicked()
        {
            if (!_isHighlightModeActive) return;
            LocateTargetStructure();
        }

        // Fires for the initial highlight AND for every subsequent tap the student
        // makes while exploring (a different structure, or the same one again) -
        // masking here every time, rather than only right after
        // ActivateHighlightMode, is what keeps any structure's real name from ever
        // reaching the Info Panel while this mode is active.
        private void HandleStructureSelected(AnatomyScreenController.BoneInfo info)
        {
            if (!_isHighlightModeActive || _screen == null) return;

            _screen.SetInfoPanelTitle(MaskedTitle);
            _screen.SetInfoPanelDescription(MaskedDescription);
        }
    }
}
