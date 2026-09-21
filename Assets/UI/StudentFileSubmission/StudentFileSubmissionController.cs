using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Anatomia3D.Backend;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentFileSubmission.uxml - the student side of a
    /// File Submission assignment. Attach to the same GameObject as UIManager,
    /// exactly like the other screen controllers; UIManager finds it with
    /// GetComponent and drives it via ShowStudentFileSubmission().
    ///
    /// This screen is only ever reached for a quiz whose `submissionType` is
    /// "file" - StudentClassroomDetailController routes those cards here instead
    /// of to StudentQuizGameplayController. Everything else about the classroom
    /// flow (which quizzes are published, deadlines, attempts) is unchanged.
    ///
    /// Attempt/deadline rules are NOT reimplemented here: they come from
    /// FileSubmissionService.CheckSubmitEligibility, which reads the same
    /// `maxAttempts` / `deadline` fields on the quiz doc that
    /// QuizService.CheckAttemptEligibility does.
    ///
    /// Requires the free "Native File Picker for Android &amp; iOS" asset
    /// (github.com/yasirkula/UnityNativeFilePicker), which this project already
    /// uses for report exports in AdminAnalyticsReportsController.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentFileSubmissionController : MonoBehaviour
    {
        [Header("Header gradient (blue -> indigo)")]
        [SerializeField] private Color gradientStart = new Color(0.231f, 0.510f, 0.965f);
        [SerializeField] private Color gradientEnd = new Color(0.373f, 0.275f, 0.898f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;
        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private VisualElement _statusPill;
        private Label _statusPillLabel;
        private Label _titleLabel;
        private Label _subtitleLabel;

        private Label _instructionsLabel;
        private Label _dueValue;
        private Label _pointsValue;
        private Label _attemptsValue;
        private Label _maxSizeValue;
        private Label _acceptedValue;

        private VisualElement _uploadCard;
        private Button _chooseFileButton;
        private VisualElement _selectedFilePanel;
        private Label _selectedFileName;
        private Label _selectedFileSize;
        private Button _clearFileButton;
        private Label _errorLabel;
        private Button _submitButton;

        private VisualElement _blockedCard;
        private Label _blockedLabel;

        private VisualElement _submittedCard;
        private VisualElement _submittedList;

        private VisualElement _workingOverlay;
        private Label _workingLabel;
        private ScrollView _scroll;

        // ---------------- State ----------------

        private string _classroomId;
        private string _classroomName;
        private string _instructorName;
        private string _quizId;
        private QuizService.QuizRecord _quiz;
        private FileSubmissionConfig _config = FileSubmissionConfig.Default();
        private FileSubmissionService.SubmitEligibility _eligibility;
        private List<FileSubmissionService.SubmissionRecord> _submissions = new List<FileSubmissionService.SubmissionRecord>();

        private string _pendingFilePath;
        private string _pendingFileName;
        private long _pendingFileSize;
        private bool _isSubmitting;
        private bool _clampingScroll;

        // ==================================================================
        // Lifecycle
        // ==================================================================

        private void OnEnable()
        {
            if (_document == null) _document = GetComponent<UIDocument>();

            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null) _root = uiDocument.rootVisualElement;
            }

            if (_root == null && _document != null) _root = _document.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[StudentFileSubmissionController] Root is null!");
                return;
            }

            UnregisterCallbacks();
            QueryElements();
            WireCallbacks();
            ApplyGradients();
            UpdateResponsiveLayout();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("file-submission-root");
            _header = _root.Q<VisualElement>("fs-header");
            _backButton = _root.Q<Button>("fs-back-button");
            _statusPill = _root.Q<VisualElement>("fs-status-pill");
            _statusPillLabel = _root.Q<Label>("fs-status-pill-label");
            _titleLabel = _root.Q<Label>("fs-title-label");
            _subtitleLabel = _root.Q<Label>("fs-subtitle-label");

            _instructionsLabel = _root.Q<Label>("fs-instructions-label");
            _dueValue = _root.Q<Label>("fs-due-value");
            _pointsValue = _root.Q<Label>("fs-points-value");
            _attemptsValue = _root.Q<Label>("fs-attempts-value");
            _maxSizeValue = _root.Q<Label>("fs-max-size-value");
            _acceptedValue = _root.Q<Label>("fs-accepted-value");

            _uploadCard = _root.Q<VisualElement>("fs-upload-card");
            _chooseFileButton = _root.Q<Button>("fs-choose-file-button");
            _selectedFilePanel = _root.Q<VisualElement>("fs-selected-file-panel");
            _selectedFileName = _root.Q<Label>("fs-selected-file-name");
            _selectedFileSize = _root.Q<Label>("fs-selected-file-size");
            _clearFileButton = _root.Q<Button>("fs-clear-file-button");
            _errorLabel = _root.Q<Label>("fs-error-label");
            _submitButton = _root.Q<Button>("fs-submit-button");

            _blockedCard = _root.Q<VisualElement>("fs-blocked-card");
            _blockedLabel = _root.Q<Label>("fs-blocked-label");

            _submittedCard = _root.Q<VisualElement>("fs-submitted-card");
            _submittedList = _root.Q<VisualElement>("fs-submitted-list");

            _workingOverlay = _root.Q<VisualElement>("fs-working-overlay");
            _workingLabel = _root.Q<Label>("fs-working-label");

            _scroll = _root.Q<ScrollView>("fs-scroll");
            // Set in code rather than relying on the UXML attribute (see
            // ClampScroll below for why Clamped alone is still not enough).
            if (_scroll != null) _scroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _chooseFileButton?.RegisterCallback<ClickEvent>(OnChooseFileClicked);
            _clearFileButton?.RegisterCallback<ClickEvent>(OnClearFileClicked);
            _submitButton?.RegisterCallback<ClickEvent>(OnSubmitClicked);
            _screenRoot?.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            if (_scroll != null)
            {
                _scroll.verticalScroller.valueChanged += OnScrollChanged;
                _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(OnScrollGeometryChanged);
                _scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(OnScrollGeometryChanged);
            }
        }

        private void UnregisterCallbacks()
        {
            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _chooseFileButton?.UnregisterCallback<ClickEvent>(OnChooseFileClicked);
            _clearFileButton?.UnregisterCallback<ClickEvent>(OnClearFileClicked);
            _submitButton?.UnregisterCallback<ClickEvent>(OnSubmitClicked);
            _screenRoot?.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);

            if (_scroll != null)
            {
                _scroll.verticalScroller.valueChanged -= OnScrollChanged;
                _scroll.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnScrollGeometryChanged);
                _scroll.contentViewport.UnregisterCallback<GeometryChangedEvent>(OnScrollGeometryChanged);
            }
        }

        // ==================================================================
        // Scroll clamp
        // ==================================================================

        private void OnScrollChanged(float _) => ClampScroll();

        private void OnScrollGeometryChanged(GeometryChangedEvent evt) => ClampScroll();

        /// <summary>
        /// Keeps the scroll offset inside [0, contentHeight - viewportHeight].
        /// When the content is SHORTER than the viewport the scroller's range goes
        /// negative/inverted, and the ScrollView (even in Clamped mode) then lets
        /// the content be dragged DOWN, leaving blank space above the first card.
        /// Forcing the offset back into the real range pins short content to the
        /// top, and still lets long content scroll normally.
        /// </summary>
        private void ClampScroll()
        {
            if (_scroll == null || _clampingScroll) return;

            var viewport = _scroll.contentViewport;
            var content = _scroll.contentContainer;
            if (viewport == null || content == null) return;

            float maxScroll = Mathf.Max(0f, content.layout.height - viewport.layout.height);
            Vector2 offset = _scroll.scrollOffset;
            float clampedY = Mathf.Clamp(offset.y, 0f, maxScroll);
            if (Mathf.Approximately(offset.y, clampedY)) return;

            _clampingScroll = true;
            try { _scroll.scrollOffset = new Vector2(offset.x, clampedY); }
            finally { _clampingScroll = false; }
        }

        // ==================================================================
        // Load
        // ==================================================================

        /// <summary>Entry point, called by UIManager.ShowStudentFileSubmission()
        /// once the UXML has been cloned in.</summary>
        public void LoadAssignment(string classroomId, string quizId, string classroomName, string instructorName)
        {
            _classroomId = classroomId;
            _classroomName = classroomName;
            _instructorName = instructorName;
            _quizId = quizId;
            ClearPendingFile();
            SetError(null);
            ShowWorking("Loading assignment...");

            if (QuizService.Instance == null || FileSubmissionService.Instance == null)
            {
                HideWorking();
                ShowBlocked("This assignment can't be opened right now. Please try again in a moment.");
                return;
            }

            QuizService.Instance.FetchQuiz(quizId, (ok, error, quiz) =>
            {
                if (!ok || quiz == null)
                {
                    HideWorking();
                    ShowBlocked(error ?? "Could not load this assignment.");
                    return;
                }

                _quiz = quiz;
                _config = quiz.FileConfig ?? FileSubmissionConfig.Default();
                ApplyAssignmentInfo();
                RefreshEligibilityAndHistory();
            });
        }

        private void RefreshEligibilityAndHistory()
        {
            FileSubmissionService.Instance.CheckSubmitEligibility(_classroomId, _quizId, (ok, error, eligibility) =>
            {
                _eligibility = eligibility;

                FileSubmissionService.Instance.FetchMySubmissions(_classroomId, _quizId, submissions =>
                {
                    HideWorking();
                    _submissions = submissions ?? new List<FileSubmissionService.SubmissionRecord>();
                    ApplyEligibility(ok, error);
                    RefreshSubmittedList();
                });
            });
        }

        private void ApplyAssignmentInfo()
        {
            if (_titleLabel != null) _titleLabel.text = _quiz.Title;
            if (_subtitleLabel != null) _subtitleLabel.text = "File Submission";

            if (_instructionsLabel != null)
            {
                _instructionsLabel.text = string.IsNullOrWhiteSpace(_quiz.Instructions)
                    ? "Your teacher did not add instructions for this assignment."
                    : _quiz.Instructions;
            }

            if (_dueValue != null)
            {
                _dueValue.text = _quiz.IsDeadlineEnabled && _quiz.DeadlineUtc.HasValue
                    ? _quiz.DeadlineUtc.Value.ToLocalTime().ToString("MMMM d, yyyy, h:mm tt")
                    : "No deadline";
            }

            if (_pointsValue != null) _pointsValue.text = _quiz.PointsPossible.ToString();
            if (_maxSizeValue != null) _maxSizeValue.text = $"{_config.MaxFileSizeMB} MB";
            if (_acceptedValue != null) _acceptedValue.text = _config.DisplayExtensions();
        }

        private void ApplyEligibility(bool checkOk, string checkError)
        {
            int used = _eligibility?.AttemptsUsed ?? _submissions.Count;
            int max = _eligibility?.MaxAttempts ?? _quiz?.MaxAttempts ?? 0;

            if (_attemptsValue != null)
            {
                _attemptsValue.text = max > 0 ? $"{used} of {max}" : $"{used} (unlimited)";
            }

            bool canSubmit = checkOk && _eligibility != null && _eligibility.CanSubmit;

            if (!canSubmit)
            {
                string reason = _eligibility?.BlockReason ?? checkError ?? "You can no longer submit to this assignment.";
                ShowBlocked(reason);
            }
            else
            {
                _blockedCard?.AddToClassList("hidden");
                _uploadCard?.RemoveFromClassList("hidden");
                RefreshSubmitButtonState();
            }

            // Header pill mirrors the latest submission's state at a glance.
            var latest = _submissions.FirstOrDefault();
            if (_statusPill != null && _statusPillLabel != null)
            {
                if (latest == null)
                {
                    _statusPill.AddToClassList("hidden");
                }
                else
                {
                    _statusPill.RemoveFromClassList("hidden");
                    _statusPillLabel.text = latest.IsReviewed ? "Reviewed" : "Submitted";
                }
            }
        }

        private void ShowBlocked(string message)
        {
            _uploadCard?.AddToClassList("hidden");
            _blockedCard?.RemoveFromClassList("hidden");
            if (_blockedLabel != null) _blockedLabel.text = message;
        }

        // ==================================================================
        // Submitted history
        // ==================================================================

        private void RefreshSubmittedList()
        {
            if (_submittedList == null) return;

            _submittedList.Clear();
            bool hasAny = _submissions.Count > 0;
            _submittedCard?.EnableInClassList("hidden", !hasAny);
            if (!hasAny) return;

            foreach (var submission in _submissions)
            {
                _submittedList.Add(BuildSubmissionRow(submission));
            }
        }

        private VisualElement BuildSubmissionRow(FileSubmissionService.SubmissionRecord submission)
        {
            var row = new VisualElement();
            row.AddToClassList("fs-submitted-row");

            var headerRow = new VisualElement();
            headerRow.AddToClassList("fs-submitted-row-header");

            var fileLabel = new Label(submission.FileName);
            fileLabel.AddToClassList("fs-submitted-file");

            var statusTag = new Label(submission.IsReviewed ? "Reviewed" : "Submitted");
            statusTag.AddToClassList("fs-status-tag");
            statusTag.AddToClassList(submission.IsReviewed ? "fs-status-tag--reviewed" : "fs-status-tag--submitted");

            headerRow.Add(fileLabel);
            headerRow.Add(statusTag);
            row.Add(headerRow);

            string submittedAt = submission.SubmittedAtUtc.ToLocalTime().ToString("MMMM d, yyyy, h:mm tt");
            var meta = new Label($"Attempt {submission.AttemptNumber} \u2022 {FileSubmissionConfig.FormatSize(submission.FileSize)} \u2022 {submittedAt}");
            meta.AddToClassList("fs-submitted-meta");
            row.Add(meta);

            if (submission.IsReviewed)
            {
                int possible = _quiz?.PointsPossible ?? 0;
                var score = new Label(possible > 0
                    ? $"Score: {submission.Score} / {possible}"
                    : $"Score: {submission.Score}");
                score.AddToClassList("fs-submitted-meta");
                row.Add(score);

                if (!string.IsNullOrWhiteSpace(submission.Feedback))
                {
                    var feedback = new Label($"Teacher feedback: {submission.Feedback}");
                    feedback.AddToClassList("fs-submitted-feedback");
                    row.Add(feedback);
                }
            }

            var openButton = new Button(() => OnOpenSubmissionClicked(submission)) { text = "Open my file" };
            openButton.AddToClassList("fs-download-button");
            row.Add(openButton);

            return row;
        }

        private void OnOpenSubmissionClicked(FileSubmissionService.SubmissionRecord submission)
        {
            if (!NetworkStatusMonitor.IsOnline)
            {
                SetError("You need an internet connection to open this file.");
                return;
            }

            ShowWorking("Opening your file...");

            R2FileUploadService.Instance.DownloadSubmission(submission.StorageKey, (ok, error, bytes) =>
            {
                HideWorking();

                if (!ok || bytes == null)
                {
                    SetError(error ?? "Could not open that file.");
                    return;
                }

                // Cache, then hand off to the OS - the same pattern
                // AdminAnalyticsReportsController uses for report exports.
                string path = Path.Combine(Application.temporaryCachePath, SafeFileName(submission.FileName));
                try
                {
                    File.WriteAllBytes(path, bytes);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StudentFileSubmissionController] Could not cache the downloaded file: {e.Message}");
                    SetError("Could not open that file on this device.");
                    return;
                }

                NativeFilePicker.ExportFile(path, success =>
                {
                    if (!success) Debug.Log("[StudentFileSubmissionController] File export cancelled.");
                });
            });
        }

        // ==================================================================
        // Pick a file
        // ==================================================================

        private void OnChooseFileClicked(ClickEvent evt)
        {
            if (_isSubmitting) return;

            SetError(null);

            if (NativeFilePicker.IsFilePickerBusy()) return;

            // Filter the native picker down to exactly what the teacher allowed.
            // ConvertExtensionToFileType gives the MIME type on Android and the UTI
            // on iOS, so nothing here is hard-coded to PDF.
            var allowedTypes = (_config.AllowedExtensions ?? new List<string>())
                .Select(e => NativeFilePicker.ConvertExtensionToFileType(FileSubmissionConfig.NormalizeExtension(e)))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToArray();

            NativeFilePicker.PickFile(path =>
            {
                if (string.IsNullOrEmpty(path)) return; // student cancelled

                string fileName = Path.GetFileName(path);
                long size;

                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StudentFileSubmissionController] Could not read '{path}': {e.Message}");
                    SetError("Could not read that file. Please choose a different one.");
                    return;
                }

                string problem = _config.Validate(fileName, size);
                if (problem != null)
                {
                    ClearPendingFile();
                    SetError(problem);
                    RefreshSubmitButtonState();
                    return;
                }

                _pendingFilePath = path;
                _pendingFileName = fileName;
                _pendingFileSize = size;

                if (_selectedFileName != null) _selectedFileName.text = fileName;
                if (_selectedFileSize != null) _selectedFileSize.text = FileSubmissionConfig.FormatSize(size);
                _selectedFilePanel?.RemoveFromClassList("hidden");

                SetError(null);
                RefreshSubmitButtonState();
            }, allowedTypes.Length > 0 ? allowedTypes : null);
        }

        private void OnClearFileClicked(ClickEvent evt)
        {
            if (_isSubmitting) return;
            ClearPendingFile();
            SetError(null);
            RefreshSubmitButtonState();
        }

        private void ClearPendingFile()
        {
            _pendingFilePath = null;
            _pendingFileName = null;
            _pendingFileSize = 0;
            _selectedFilePanel?.AddToClassList("hidden");
            if (_selectedFileName != null) _selectedFileName.text = string.Empty;
            if (_selectedFileSize != null) _selectedFileSize.text = string.Empty;
        }

        private void RefreshSubmitButtonState()
        {
            bool ready = !_isSubmitting
                && !string.IsNullOrEmpty(_pendingFilePath)
                && _eligibility != null
                && _eligibility.CanSubmit;

            _submitButton?.SetEnabled(ready);
            _chooseFileButton?.SetEnabled(!_isSubmitting);
        }

        // ==================================================================
        // Submit
        // ==================================================================

        private void OnSubmitClicked(ClickEvent evt)
        {
            if (_isSubmitting || string.IsNullOrEmpty(_pendingFilePath) || _quiz == null) return;

            // File bytes cannot be queued offline the way Firestore writes can -
            // the upload needs a real connection, so say so plainly rather than
            // appearing to accept the submission.
            if (!NetworkStatusMonitor.IsOnline)
            {
                SetError(R2FileUploadService.OfflineMessage);
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(_pendingFilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StudentFileSubmissionController] Could not read the picked file: {e.Message}");
                SetError("Could not read that file. Please choose it again.");
                return;
            }

            // Re-validate against the teacher's rules using the bytes we actually
            // have, in case the file changed between picking and submitting.
            string problem = _config.Validate(_pendingFileName, bytes.LongLength);
            if (problem != null)
            {
                SetError(problem);
                return;
            }

            _isSubmitting = true;
            SetError(null);
            RefreshSubmitButtonState();
            ShowWorking("Uploading your file...");

            string submissionId = FileSubmissionService.Instance.NewSubmissionId();
            string mimeType = FileSubmissionConfig.MimeTypeFor(FileSubmissionConfig.ExtensionOf(_pendingFileName));

            R2FileUploadService.Instance.UploadSubmission(
                _classroomId, _quizId, submissionId, _pendingFileName, mimeType, bytes,
                (uploadOk, uploadError, uploadResult) =>
                {
                    if (!uploadOk || uploadResult == null)
                    {
                        _isSubmitting = false;
                        HideWorking();
                        SetError(uploadError ?? "Upload failed. Please try again.");
                        RefreshSubmitButtonState();
                        return;
                    }

                    SetWorkingMessage("Recording your submission...");

                    FileSubmissionService.Instance.SubmitFile(
                        _quiz, _classroomId, submissionId, _pendingFileName, mimeType,
                        uploadResult.FileSize > 0 ? uploadResult.FileSize : bytes.LongLength,
                        uploadResult.StorageKey,
                        (recordOk, recordError, record) =>
                        {
                            _isSubmitting = false;

                            if (!recordOk)
                            {
                                HideWorking();
                                SetError(recordError ?? "Could not record your submission.");
                                RefreshSubmitButtonState();
                                return;
                            }

                            ClearPendingFile();
                            RefreshEligibilityAndHistory();
                        });
                });
        }

        // ==================================================================
        // Small helpers
        // ==================================================================

        private void OnBackClicked(ClickEvent evt)
        {
            if (_isSubmitting) return;
            // Back always returns to the Quizzes tab of the classroom this
            // assignment was opened from, carrying the same name/instructor the
            // classroom screen was already showing.
            UIManager.Instance.ShowStudentClassroomDetailOnQuizzesTab(_classroomId, _classroomName, _instructorName);
        }

        private void SetError(string message)
        {
            if (_errorLabel == null) return;

            if (string.IsNullOrEmpty(message))
            {
                _errorLabel.text = string.Empty;
                _errorLabel.AddToClassList("hidden");
                return;
            }

            _errorLabel.text = message;
            _errorLabel.RemoveFromClassList("hidden");
        }

        private void ShowWorking(string message)
        {
            SetWorkingMessage(message);
            _workingOverlay?.RemoveFromClassList("hidden");
        }

        private void SetWorkingMessage(string message)
        {
            if (_workingLabel != null) _workingLabel.text = message;
        }

        private void HideWorking() => _workingOverlay?.AddToClassList("hidden");

        private static string SafeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "submission";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        private void ApplyGradients()
        {
            if (_header == null) return;

            if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
            _headerGradientTexture = CreateHorizontalGradient(gradientStart, gradientEnd);
            _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
        }

        private static Texture2D CreateHorizontalGradient(Color start, Color end, int width = 128)
        {
            var texture = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int x = 0; x < width; x++)
            {
                texture.SetPixel(x, 0, Color.Lerp(start, end, x / (float)(width - 1)));
            }

            texture.Apply();
            return texture;
        }
    }
}
