using System;
using System.Collections.Generic;
using System.Linq;
using Anatomia3D.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Student's one-time Pretest/Posttest baseline assessment: a fixed set of
    /// Skeletal structures (loaded from Assets/Resources/PretestPosttestQuestions.json),
    /// answered through AnatomyPlayModeController's restricted "baseline mode"
    /// (tap the model yourself, type the name, no hints - see
    /// AnatomyPlayModeController.StartBaselineAssessment), then recorded through
    /// BaselineAssessmentService.
    ///
    /// Contains NO 3D-interaction/guessing logic itself - all of that is reused
    /// from AnatomyPlayModeController exactly the way AnatomyQuizHighlightController
    /// and AnatomyTeacherSelectionController already reuse it for their own
    /// purposes. This class only: loads the fixed question set, starts the
    /// restricted session, and decides what happens on completion.
    ///
    /// Attach to the SAME GameObject as AnatomyScreenController/
    /// AnatomyPlayModeController (same requirement those two already have).
    /// Only ever triggered via UIManager.ShowStudentAnatomyScreenForBaselineAssessment,
    /// the same "Request...OnOpen, consumed once WireUi/GetComponent is ready"
    /// pattern AnatomyPlayModeController/AnatomyQuizHighlightController use.
    /// </summary>
    [RequireComponent(typeof(AnatomyScreenController))]
    public class BaselineAssessmentController : MonoBehaviour
    {
        private const string QuestionsResourcePath = "PretestPosttestQuestions"; // Assets/Resources/PretestPosttestQuestions.json

        // Pretest/Posttest is a short fixed-length check, not a full survey
        // of every structure in the question bank - only the first 10
        // questions (in the JSON file's own order) are used per session.
        // Applied in BeginAssessment right after LoadQuestions, before
        // anything else reads _questions, so every downstream consumer
        // (targetKeys, progress totals, OnBaselineStructureSelected's
        // per-structure question lookup) only ever sees these 10.
        private const int MaxQuestionsPerAssessment = 10;

        private AnatomyScreenController _screen;
        private AnatomyPlayModeController _playMode;
        private Button _finishButton;
        private VisualElement _confirmPanel;
        private Label _confirmMessage;
        private Button _confirmYesButton;
        private Button _confirmCancelButton;

        private bool _pendingActivate;
        private BaselineAssessmentType _pendingType;

        // Current session's questions and progress - kept as fields (not locals)
        // so OnBaselineStructureSelected can look up each tapped structure's
        // question text (and skip already-answered ones) without re-loading
        // the JSON on every tap.
        private List<QuestionRecordJson> _questions;
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

        private void Awake()
        {
            _screen = GetComponent<AnatomyScreenController>();
            _playMode = GetComponent<AnatomyPlayModeController>();
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
            if (!_pendingActivate) return;
            if (_playMode == null) _playMode = GetComponent<AnatomyPlayModeController>();
            if (_playMode == null) return;

            _pendingActivate = false;
            BeginAssessment(_pendingType);
        }

        private void BeginAssessment(BaselineAssessmentType type)
        {
            _questions = LoadQuestions();
            if (_questions == null || _questions.Count == 0)
            {
                Debug.LogError("[BaselineAssessmentController] No questions loaded from " +
                                $"Resources/{QuestionsResourcePath}.json - cannot start {type}.");
                return;
            }

            if (_questions.Count > MaxQuestionsPerAssessment)
                _questions = _questions.Take(MaxQuestionsPerAssessment).ToList();

            _answeredKeys.Clear();

            var targetKeys = _questions.Select(q => q.StructureKey).Where(k => !string.IsNullOrEmpty(k)).ToList();

            // Always Skeletal for the pretest/posttest, per the fixed question set -
            // set explicitly rather than trusting whatever system the screen last
            // happened to be on.
            _screen.SetAnatomySystem(AnatomySystem.Skeletal);

            // No way to back out of a mandatory assessment, and a clear label so
            // the student knows this is a formal Pretest/Posttest, not normal
            // Play Mode. Both are restored in OnAssessmentCompleted, since this
            // same Anatomy Screen GameObject is reused later for Explore/Play
            // Mode/Quiz - leftover state here would otherwise bleed into those.
            _screen.SetBackButtonVisible(false);
            SetSubtitle(type);
            ShowFinishButton();

            // -= before += since BeginAssessment can run twice on this same
            // persistent GameObject (pretest, then later posttest). Added
            // here (after _playMode.StartBaselineAssessment below subscribes
            // its own OnStructureSelected handler during Awake/OnEnable,
            // which already happened before this ever runs) so this handler
            // sits later in the invocation list and its InfoPanel text wins.
            _screen.OnStructureSelected -= OnBaselineStructureSelected;
            _screen.OnStructureSelected += OnBaselineStructureSelected;

            _playMode.StartBaselineAssessment(
                targetKeys,
                onStructureIdentified: structureKey => _answeredKeys.Add(structureKey),
                onCompleted: (correctCount, totalCount, points) =>
                    OnAssessmentCompleted(type, correctCount, totalCount, points));
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
            if (info == null || _questions == null || _answeredKeys.Contains(info.boneName)) return;

            var question = _questions.FirstOrDefault(q =>
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
            int total = _questions?.Count ?? 0;
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
            // OnStructureSelected fires for every tap app-wide, and _questions/
            // _answeredKeys aren't cleared, so without this a later Explore/Play
            // Mode session would keep overwriting the InfoPanel with stale
            // pretest/posttest question text.
            //
            // ExitPlayMode() belongs in this same cleanup block: it's what
            // actually lifts the isolation that restricted the model to just
            // this assessment's 10 structures. Without it, opening Explore 3D
            // -> Skeletal right after finishing a pretest/posttest would still
            // show only those 10 bones instead of the full skeleton.
            _screen.SetBackButtonVisible(true);
            _screen.OnStructureSelected -= OnBaselineStructureSelected;
            _playMode.ExitPlayMode();
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
