using System;
using System.Collections.Generic;
using System.Linq;
using Anatomia3D.UI;
using Unity.Loading;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Student's one-time Pretest/Posttest baseline assessment: the fixed set of
    /// structures selected per AnatomySystem (Skeletal, Muscular, Cardiovascular)
    /// in Assets/Resources/PretestPosttestQuestions.json - all three models shown
    /// TOGETHER in one session (see PrepareCombinedSession/StartSession), each one
    /// reduced to only its selected structures.
    ///
    /// All three at once is affordable BECAUSE of that reduction, not in spite of
    /// it: AnatomyScreenController builds bone data plus a MeshCollider per mesh
    /// piece for whatever it is given, so three FULL models in one open would lag
    /// badly - three models' worth of five structures each is 15 pieces total.
    /// SetCombinedSystems + SetStructureFilter do that together, before the screen
    /// opens; the other structures are never built, so there is nothing heavy to
    /// hide afterward.
    ///
    /// The session is answered through AnatomyPlayModeController's restricted
    /// "baseline mode" (tap the model yourself, type the name, no hints - see
    /// AnatomyPlayModeController.StartBaselineAssessment); once every structure is
    /// identified the result is recorded through BaselineAssessmentService.
    ///
    /// Contains NO 3D-interaction/guessing logic itself - all of that is reused
    /// from AnatomyPlayModeController exactly the way AnatomyQuizHighlightController
    /// and AnatomyTeacherSelectionController already reuse it for their own
    /// purposes. This class only: loads the fixed question set, splits it into
    /// per-system phases, starts each phase's restricted session in turn, and
    /// decides what happens on completion.
    ///
    /// Attach to the SAME GameObject as AnatomyScreenController/
    /// AnatomyPlayModeController (same requirement those two already have).
    /// Triggered via UIManager.ShowStudentAnatomyScreenForBaselineAssessment,
    /// which prepares the combined systems + structure filter before the screen
    /// opens and then follows the same "Request...OnOpen, consumed once
    /// WireUi/GetComponent is ready" pattern AnatomyPlayModeController/
    /// AnatomyQuizHighlightController use.
    /// </summary>
    [RequireComponent(typeof(AnatomyScreenController))]
    public class BaselineAssessmentController : MonoBehaviour
    {
        private const string QuestionsResourcePath = "PretestPosttestQuestions"; // Assets/Resources/PretestPosttestQuestions.json

        // The systems shown together for the whole assessment. Order matters
        // only in that the first one becomes the camera/orbit anchor - see
        // AnatomyScreenController.ResolveCombinedSystems. Every question
        // curated per AnatomySystemKey in the JSON is used; there is no cap on
        // question count, since what keeps this light is the per-model
        // structure filter, not how many questions there are.
        private static readonly AnatomySystem[] SessionSystemOrder =
        {
            AnatomySystem.Skeletal,
            AnatomySystem.Muscular,
            AnatomySystem.Cardiovascular
        };

        private static readonly Dictionary<string, AnatomySystem> AnatomySystemKeyMap =
            new Dictionary<string, AnatomySystem>(StringComparer.OrdinalIgnoreCase)
            {
                { "skeletal", AnatomySystem.Skeletal },
                { "muscular", AnatomySystem.Muscular },
                { "cardiovascular", AnatomySystem.Cardiovascular }
            };

        private AnatomyScreenController _screen;
        private AnatomyPlayModeController _playMode;
        private Button _finishButton;
        private VisualElement _confirmPanel;
        private Label _confirmMessage;
        private Button _confirmYesButton;
        private Button _confirmCancelButton;

        private bool _pendingActivate;
        private BaselineAssessmentType _pendingType;

        // Every question in this session, across all three systems at once -
        // what OnBaselineStructureSelected looks up a tapped structure's
        // question text against, and what the target structure list handed to
        // AnatomyPlayModeController is built from.
        private List<QuestionRecordJson> _sessionQuestions;
        private BaselineAssessmentType _sessionType;

        // How many structures this session asks about (15 as curated), kept so
        // the Finish confirmation can report progress before
        // AnatomyPlayModeController's onCompleted has fired.
        private int _sessionTotal;

        // Every structure identified correctly so far THIS SESSION - kept as a
        // field (not local) so OnBaselineStructureSelected
        // can look up each tapped structure's question text (and skip
        // already-answered ones) without re-loading the JSON on every tap,
        // and so the Finish confirmation can report whole-session progress.
        private readonly HashSet<string> _answeredKeys = new HashSet<string>();
        private string _originalSubtitleText;

        [Serializable]
        private class QuestionRecordJson
        {
            public string QuestionText;
            public string QuestionTypeSlug;
            public List<string> Options;
            public string CorrectAnswer;
            public string Difficulty;
            public int Points;
            public string AnatomySystemKey;
            public string AnatomySystemDisplayName;
            public string StructureKey;
            public string StructureDisplayName;
        }

        [Serializable]
        private class QuestionRecordListWrapper
        {
            public List<QuestionRecordJson> questions;
        }

        // Cached across the whole session (and across sessions) so
        // PrepareCombinedSession - which runs from UIManager before the screen
        // is even enabled, and again on a later posttest - doesn't re-read and
        // re-parse the JSON every time.
        private List<QuestionRecordJson> _cachedQuestions;

        private void Awake()
        {
            _screen = GetComponent<AnatomyScreenController>();
            _playMode = GetComponent<AnatomyPlayModeController>();
        }

        /// <summary>Brings up all three systems' models at once
        /// (AnatomyScreenController.SetCombinedSystems) with each one reduced to only
        /// the structures this assessment asks about (SetStructureFilter) - the 5 per
        /// system, 15 in total, curated in PretestPosttestQuestions.json.
        ///
        /// Called by UIManager.ShowStudentAnatomyScreenForBaselineAssessment immediately
        /// BEFORE ShowScreen re-enables the controller, for exactly the same reason
        /// SetAnatomySystem is called there: AnatomyScreenController.OnEnable is what
        /// walks the models, registers every structure and generates a MeshCollider per
        /// mesh piece, so both the combined list and the filter have to be in place
        /// first. Three FULL models built in one open is precisely the lag this avoids;
        /// filtered, all three together cost less than one unfiltered model did.
        ///
        /// Safe to call on a disabled GameObject (before Awake has ever run), hence the
        /// GetComponent fallback - same pattern RequestBaselineAssessmentOnOpen relies
        /// on.</summary>
        public void PrepareCombinedSession()
        {
            if (_screen == null) _screen = GetComponent<AnatomyScreenController>();
            if (_screen == null) return;

            if (_cachedQuestions == null || _cachedQuestions.Count == 0)
                _cachedQuestions = LoadQuestions();

            var keys = _cachedQuestions?
                .Where(q => AnatomySystemKeyMap.ContainsKey(q.AnatomySystemKey ?? string.Empty))
                .Select(q => q.StructureKey)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToList();

            // Only systems that actually have curated questions are brought up -
            // a model with nothing to identify on it is just a model the student
            // can't do anything with.
            var systems = new List<AnatomySystem>();
            if (_cachedQuestions != null)
            {
                foreach (var system in SessionSystemOrder)
                {
                    bool hasQuestions = _cachedQuestions.Any(q =>
                        AnatomySystemKeyMap.TryGetValue(q.AnatomySystemKey ?? string.Empty, out var mapped) && mapped == system);

                    if (hasQuestions) systems.Add(system);
                }
            }

            if (keys == null || keys.Count == 0 || systems.Count == 0)
            {
                // Nothing to scope to - leave the screen in its normal
                // one-system, unfiltered state rather than opening an empty
                // model; BeginAssessment logs the real error.
                _screen.ClearStructureFilter();
                _screen.ClearCombinedSystems();
                return;
            }

            Debug.Log($"[BaselineAssessmentController] PrepareCombinedSession: {systems.Count} model(s) shown together, " +
                      $"restricted to {keys.Count} structure(s).");
            _screen.SetCombinedSystems(systems);
            _screen.SetStructureFilter(keys);
        }

        /// <summary>Called by UIManager.ShowStudentAnatomyScreenForBaselineAssessment
        /// right after the Anatomy Screen opens. Safe to call before the screen's
        /// components have finished their own setup - consumed lazily from Update,
        /// same reasoning as AnatomyPlayModeController's _pendingAutoStart (the
        /// screen's OnScreenReady/WireUi sequence runs a frame after ShowScreen
        /// returns).</summary>
        public void RequestBaselineAssessmentOnOpen(BaselineAssessmentType type)
        {
            _pendingType = type;
            _pendingActivate = true;
        }

        private void Update()
        {
            if (_pendingActivate)
            {
                if (_playMode == null) _playMode = GetComponent<AnatomyPlayModeController>();
                if (_playMode == null) return;

                _pendingActivate = false;
                BeginAssessment(_pendingType);
            }
        }

        private void BeginAssessment(BaselineAssessmentType type)
        {
            var questions = _cachedQuestions != null && _cachedQuestions.Count > 0
                ? _cachedQuestions
                : (_cachedQuestions = LoadQuestions());
            if (questions == null || questions.Count == 0)
            {
                Debug.LogError("[BaselineAssessmentController] No questions loaded from " +
                                $"Resources/{QuestionsResourcePath}.json - cannot start {type}.");
                return;
            }

            // Every question whose AnatomySystemKey maps to a real system, in
            // JSON order - one flat session across all three models rather than
            // one slice per model, since all three are on screen together.
            _sessionQuestions = questions
                .Where(q => AnatomySystemKeyMap.ContainsKey(q.AnatomySystemKey ?? string.Empty))
                .ToList();

            if (_sessionQuestions.Count == 0)
            {
                Debug.LogError("[BaselineAssessmentController] None of the questions in " +
                                $"Resources/{QuestionsResourcePath}.json have a recognized " +
                                "AnatomySystemKey (skeletal/muscular/cardiovascular) - cannot start " +
                                $"{type}.");
                return;
            }

            _sessionType = type;
            _sessionTotal = _sessionQuestions.Count;
            _answeredKeys.Clear();

            StartSession();
        }

        /// <summary>Starts the one and only session. By the time this runs the
        /// Anatomy Screen is already showing all three systems' models, each
        /// restricted to this assessment's structures - see
        /// PrepareCombinedSession, which UIManager called before the screen
        /// opened. This hands that same structure list to
        /// AnatomyPlayModeController so tapping any of them, on any of the three
        /// models, is answerable.</summary>
        private void StartSession()
        {
            var targetKeys = _sessionQuestions
                .Select(q => q.StructureKey)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToList();

            // Every open of this screen clones a fresh UXML tree, so anything
            // queried from a previous session's tree is stale - null them so
            // ShowFinishButton/WireConfirmPanel below re-query the current one
            // instead of silently no-op'ing against dead references.
            _finishButton = null;
            _confirmPanel = null;
            _confirmMessage = null;
            _confirmYesButton = null;
            _confirmCancelButton = null;

            // No way to back out of a mandatory assessment, and a clear label so
            // the student knows this is a formal Pretest/Posttest, not normal
            // Play Mode. Both are restored in OnAssessmentCompleted, since this
            // same Anatomy Screen GameObject is reused later for Explore/Play
            // Mode/Quiz - leftover state here would otherwise bleed into those.
            _screen.SetBackButtonVisible(false);
            SetSubtitle(_sessionType);
            ShowFinishButton();

            // -= before += since this can run again on the same persistent
            // GameObject (pretest, then later posttest). Added here (after
            // _playMode.StartBaselineAssessment below subscribes its own
            // OnStructureSelected handler during Awake/OnEnable, which already
            // happened before this ever runs) so this handler sits later in the
            // invocation list and its InfoPanel text wins.
            _screen.OnStructureSelected -= OnBaselineStructureSelected;
            _screen.OnStructureSelected += OnBaselineStructureSelected;

            _playMode.StartBaselineAssessment(
                targetKeys,
                onStructureIdentified: structureKey => _answeredKeys.Add(structureKey),
                onCompleted: (correctCount, totalCount, points) =>
                    OnAssessmentCompleted(_sessionType, correctCount, _sessionTotal, points));
        }

        /// <summary>Fires for every structure tap (hotspot / search / mesh-tap),
        /// same as AnatomyPlayModeController.OnStructureSelected which it runs
        /// right after. That handler already opened the InfoPanel with generic
        /// copy ("Identify this structure" / "Type each letter, or use a
        /// hint."); this overwrites the description with this structure's
        /// actual Pretest/Posttest question text, so the student sees exactly
        /// what's being asked right where they're about to type their answer -
        /// instead of a separate label elsewhere on screen. Already-answered
        /// structures are left showing PlayModeController's own "Already
        /// identified. Great work!" message untouched.</summary>
        private void OnBaselineStructureSelected(AnatomyScreenController.BoneInfo info)
        {
            if (info == null || _sessionQuestions == null || _answeredKeys.Contains(info.boneName)) return;

            var question = _sessionQuestions.FirstOrDefault(q =>
                BoneDatabaseService.NormalizeKey(q.StructureKey) == BoneDatabaseService.NormalizeKey(info.boneName));
            if (question == null || string.IsNullOrEmpty(question.QuestionText)) return;

            _screen.SetInfoPanelDescription(question.QuestionText);
        }

        private void ShowFinishButton()
        {
            if (_finishButton == null)
                _finishButton = GetRoot()?.Q<Button>("BaselineFinishButton");

            if (_finishButton == null) return;

            // -= before += since BeginAssessment can run twice on this same
            // persistent GameObject (pretest, then later posttest) - without
            // this, a second session would double-fire OnFinishClicked per tap.
            _finishButton.clicked -= OnFinishClicked;
            _finishButton.clicked += OnFinishClicked;
            _finishButton.RemoveFromClassList("hidden");

            WireConfirmPanel();
        }

        private void WireConfirmPanel()
        {
            var root = GetRoot();
            if (_confirmPanel == null) _confirmPanel = root?.Q<VisualElement>("BaselineFinishConfirmPanel");
            if (_confirmMessage == null) _confirmMessage = root?.Q<Label>("BaselineFinishConfirmMessage");
            if (_confirmYesButton == null) _confirmYesButton = root?.Q<Button>("BaselineFinishConfirmYesButton");
            if (_confirmCancelButton == null) _confirmCancelButton = root?.Q<Button>("BaselineFinishConfirmCancelButton");

            if (_confirmYesButton != null)
            {
                _confirmYesButton.clicked -= OnConfirmFinishClicked;
                _confirmYesButton.clicked += OnConfirmFinishClicked;
            }

            if (_confirmCancelButton != null)
            {
                _confirmCancelButton.clicked -= HideConfirmPanel;
                _confirmCancelButton.clicked += HideConfirmPanel;
            }
        }

        /// <summary>Opens the "Finish Now?" confirmation instead of finishing
        /// immediately - a single accidental tap on Finish would otherwise submit
        /// a possibly-incomplete pretest/posttest with no way to undo it.</summary>
        private void OnFinishClicked()
        {
            if (_confirmPanel == null)
            {
                Debug.LogWarning("[BaselineAssessmentController] OnFinishClicked: BaselineFinishConfirmPanel not wired - cannot show confirm dialog.");
                return;
            }

            Debug.Log("[BaselineAssessmentController] Finish tapped - showing confirm dialog.");
            int answered = _answeredKeys.Count;
            int total = _sessionTotal;
            if (_confirmMessage != null)
            {
                _confirmMessage.text = answered < total
                    ? $"You've identified {answered} of {total} structures. Finishing now submits this as your final score and can't be undone."
                    : "Submit your results now? This can't be undone.";
            }

            _confirmPanel.RemoveFromClassList("hidden");
        }

        private void HideConfirmPanel()
        {
            _confirmPanel?.AddToClassList("hidden");
        }

        private void OnConfirmFinishClicked()
        {
            Debug.Log("[BaselineAssessmentController] Finish Now confirmed - calling FinishBaselineAssessmentEarly.");
            HideConfirmPanel();

            // FinishBaselineAssessmentEarly fires the same onCompleted callback a
            // natural completion would, just with the partial counts - so it
            // lands in OnAssessmentCompleted and gets recorded as an honest
            // partial score.
            _playMode.FinishBaselineAssessmentEarly();
        }

        private VisualElement GetRoot()
        {
            var uiDocument = GetComponent<UIDocument>();
            return uiDocument != null ? uiDocument.rootVisualElement : null;
        }

        /// <summary>Sets the header's AppSubtitle to e.g. "SKELETAL SYSTEM -
        /// PRETEST" - AnatomyScreenController.SetAnatomySystem already set it to
        /// plain "SKELETAL SYSTEM" a moment earlier (see ResolveAnatomySystem),
        /// this appends the assessment type on top of that. Restored back to the
        /// plain system name in OnAssessmentCompleted for the same reason the
        /// Back button and mode label are restored - this GameObject gets reused
        /// for ordinary Explore/Play Mode afterward.</summary>
        private void SetSubtitle(BaselineAssessmentType type)
        {
            var subtitleLabel = GetRoot()?.Q<Label>("AppSubtitle");
            if (subtitleLabel == null) return;

            _originalSubtitleText = subtitleLabel.text;
            string suffix = type == BaselineAssessmentType.Pretest ? "PRETEST" : "POSTTEST";
            subtitleLabel.text = $"{_originalSubtitleText} - {suffix}";
        }

        private List<QuestionRecordJson> LoadQuestions()
        {
            var jsonAsset = Resources.Load<TextAsset>(QuestionsResourcePath);
            if (jsonAsset == null)
            {
                Debug.LogError($"[BaselineAssessmentController] Could not find Resources/{QuestionsResourcePath}.json.");
                return null;
            }

            var wrapper = JsonUtility.FromJson<QuestionRecordListWrapper>(jsonAsset.text);
            return wrapper?.questions;
        }

        private void OnAssessmentCompleted(BaselineAssessmentType type, int correctCount, int totalCount, int points)
        {
            Debug.Log($"[BaselineAssessmentController] OnAssessmentCompleted({type}): {correctCount}/{totalCount}, {points} pts.");

            // Restore the screen chrome this class changed in BeginAssessment,
            // before this GameObject gets reused for Explore/Play Mode/Quiz -
            // otherwise a later visit would incorrectly show no Back button, a
            // leftover question in the InfoPanel, or the wrong AppSubtitle.
            // Unsubscribing OnBaselineStructureSelected matters most here -
            // OnStructureSelected fires for every tap app-wide, and _sessionQuestions/
            // _answeredKeys aren't cleared, so without this a later Explore/Play
            // Mode session would keep overwriting the InfoPanel with stale
            // pretest/posttest question text.
            //
            // ExitPlayMode() belongs in this same cleanup block: it's what
            // actually lifts the isolation that restricted the models to just
            // this session's selected structures. Without it, opening Explore 3D
            // -> whichever system was the anchor right after finishing a
            // pretest/posttest would still show only those few structures
            // instead of the full model.
            _screen.SetBackButtonVisible(true);
            _screen.OnStructureSelected -= OnBaselineStructureSelected;
            _playMode.ExitPlayMode();

            // Hide the models BEFORE anything else touches them. The model
            // camera renders these roots whether or not this screen's UI is
            // showing, so leaving them active means all three full systems keep
            // being rendered behind the dashboard the student just landed on -
            // which is what made finishing lag.
            _screen.HideActiveModels();

            // With the models already hidden, restoring the renderers/colliders
            // the filter switched off changes nothing on screen, so it is
            // deferred to the next open rather than spent on this frame - the
            // student is mid-navigation and the attempt is about to be written
            // to Firebase. ClearCombinedSystems puts the screen back to one
            // model per open; without it, opening Explore 3D right after
            // finishing would bring up all three systems at once.
            _screen.ClearStructureFilter(deferRestore: true);
            _screen.ClearCombinedSystems();
            _finishButton?.AddToClassList("hidden");
            _confirmPanel?.AddToClassList("hidden");
            var subtitleLabel = GetRoot()?.Q<Label>("AppSubtitle");
            if (subtitleLabel != null && !string.IsNullOrEmpty(_originalSubtitleText))
                subtitleLabel.text = _originalSubtitleText;

            if (BaselineAssessmentService.Instance == null)
            {
                Debug.LogError("[BaselineAssessmentController] No BaselineAssessmentService in the scene - " +
                                "result cannot be recorded. Navigating away without saving.");
                NavigateAfterCompletion(type);
                return;
            }

            BaselineAssessmentService.Instance.RecordAttempt(type, correctCount, totalCount, points, success =>
            {
                Debug.Log($"[BaselineAssessmentController] RecordAttempt({type}) callback: success={success}. Navigating.");
                if (!success)
                {
                    // Either Firebase failed, or (per RecordAttempt's one-shot rule)
                    // this assessment type was already completed - either way, the
                    // student already finished the on-screen questions, so still
                    // move them forward rather than stranding them on the Anatomy
                    // Screen with no way out.
                    Debug.LogWarning($"[BaselineAssessmentController] Failed to record {type} attempt " +
                                      "(or it was already completed).");
                }

                NavigateAfterCompletion(type);
            });

            
            //reset camera position and rotation to default values after the assessment is completed
            _screen.OnResetClicked();



        }

        private void NavigateAfterCompletion(BaselineAssessmentType type)
        {
            Debug.Log($"[BaselineAssessmentController] NavigateAfterCompletion({type}).");
            if (type == BaselineAssessmentType.Pretest)
                UIManager.Instance.ShowStudentDashboardSkipBaselineGate();
            else
                UIManager.Instance.ShowStudentProgress();
        }

      
     
    }
}
