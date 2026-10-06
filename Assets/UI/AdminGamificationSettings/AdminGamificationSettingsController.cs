using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for AdminGamificationSettings.uxml. Attach to the same
    /// GameObject as UIManager (it uses RequireComponent(UIDocument) like the
    /// other screen controllers, and UIManager finds it via GetComponent).
    ///
    /// Responsibilities:
    ///  - Wires up the back button and "Save Changes" button
    ///  - Points Configuration: Easy/Medium/Hard question point values, kept
    ///    in sync with the Preview card as the fields change
    ///  - Badges: renders the current badge list, "Add Badge" opens a modal
    ///    with name/points fields, a click-to-pick preset icon grid (project icon
    ///    PNGs, no emoji) and an optional "Choose Image" upload to Cloudinary -
    ///    the resulting URL is stored on the badge and saved to Firestore (iconUrl);
    ///    each row has a delete (trash) button. Badges only apply to the
    ///    classroom(s) the signed-in admin/teacher owns.
    ///  - Level Progression: read-only. Levels are global and fixed across
    ///    every classroom, so there's no add/edit/delete here - this screen
    ///    just displays the current global thresholds (loaded via SetLevels)
    ///    for reference. A student's level comes from their total points
    ///    added up across every classroom they're enrolled in.
    ///  - Applies the green->blue gradient at runtime to the header, the two
    ///    "Add" buttons and the two modal submit buttons, plus a pale
    ///    green->blue gradient to the Preview card background (USS can't do
    ///    linear-gradient)
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///
    /// Loads current settings from AdminGamificationService.FetchSettings() on
    /// enable, and OnSaveChangesClicked() persists points config + badges via
    /// AdminGamificationService.SaveSettings() (levels are intentionally left
    /// out - see that method's doc comment).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminGamificationSettingsController : MonoBehaviour
    {
        /// <summary>Plain data for a single row in the "Badges" list.</summary>
        public struct BadgeData
        {
            /// <summary>Firestore badge id. Empty for a badge just created in this
            /// session - AdminGamificationService.SaveSettings() will mint one.
            /// Populated when loaded from the backend so re-saving an existing
            /// badge doesn't mint a *new* id and orphan students' earned-badge
            /// records (students/{uid}.badgesEarned and .../badgeAwards both key
            /// off this id).</summary>
            public string BadgeId;
            public string Name;
            public int PointsRequired;
            /// <summary>Preset icon key (see BadgeIcons.PresetKeys). Used when IconUrl is empty.</summary>
            public string IconKey;
            /// <summary>Cloudinary secure_url of a teacher-uploaded image. Wins over IconKey when set.</summary>
            public string IconUrl;

            public BadgeData(string name, int pointsRequired, string iconKey, string iconUrl = "", string badgeId = "")
            {
                BadgeId = badgeId;
                Name = name;
                PointsRequired = pointsRequired;
                IconKey = iconKey;
                IconUrl = iconUrl;
            }
        }

        /// <summary>Plain data for a single row in the "Level Progression" list.</summary>
        public struct LevelData
        {
            public int LevelNumber;
            public string Title;
            public int PointsRequired;

            public LevelData(int levelNumber, string title, int pointsRequired)
            {
                LevelNumber = levelNumber;
                Title = title;
                PointsRequired = pointsRequired;
            }
        }

        [Header("Gradient colors (matches AdminDashboard: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Preview card pale gradient")]
        [SerializeField] private Color previewBackgroundStart = new Color(0.906f, 0.973f, 0.949f); // pale mint
        [SerializeField] private Color previewBackgroundEnd = new Color(0.918f, 0.949f, 0.984f);   // pale blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        // Preset icon keys shared with the student screens (Backend/BadgeIcons.cs).
        private static string[] IconChoices => BadgeIcons.PresetKeys;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;
        private Texture2D _addBadgeButtonGradientTexture;
        private Texture2D _badgeModalSubmitGradientTexture;
        private Texture2D _previewGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private Button _saveChangesButton;

        // Shown while Save Changes is in flight - see the reusable
        // LoadingOverlay class (built entirely in code, no matching .uxml/.uss
        // needed). Rebuilt fresh every OnEnable rather than reused, since
        // UIManager.ShowScreen clones a brand new UXML tree on every visit to
        // this screen and an overlay parented into the old tree would already
        // be gone. Same pattern as StudentEditProfileController.
        private LoadingOverlay _loading;

        // Leave-without-saving confirm dialog
        private VisualElement _leaveDialogOverlay;
        private Button _leaveDialogCancelButton;
        private Button _leaveDialogConfirmButton;

        private TextField _easyPointsField;
        private TextField _mediumPointsField;
        private TextField _hardPointsField;
        private Label _easyPointsError;
        private Label _mediumPointsError;
        private Label _hardPointsError;

        private Button _addBadgeButton;
        private VisualElement _badgesEmptyLabel;
        private VisualElement _badgesList;

        private VisualElement _levelsGlobalTag;
        private VisualElement _levelsEmptyLabel;
        private VisualElement _levelsList;

        private VisualElement _previewCard;
        private Label _previewEasyValue;
        private Label _previewMediumValue;
        private Label _previewHardValue;
        private Label _progressionSummaryLabel;

        // Add Badge modal
        private VisualElement _addBadgeModalOverlay;
        private Button _addBadgeCloseButton;
        private Button _addBadgeCancelButton;
        private Button _addBadgeSubmitButton;
        private TextField _badgeNameField;
        private Label _badgeNameError;
        private TextField _badgePointsField;
        private Label _badgePointsError;
        private VisualElement _badgeIconGrid;
        private Label _addBadgeStatusLabel;
        private string _selectedBadgeIcon;
        private readonly List<VisualElement> _badgeIconGridItems = new List<VisualElement>();

        // Optional custom badge image (picked from the gallery, uploaded to Cloudinary on "Add Badge")
        private VisualElement _badgeCustomPreview;
        private Button _badgeUploadButton;
        private Button _badgeRemoveImageButton;
        private byte[] _pendingBadgeImageBytes;
        private Texture2D _pendingBadgeTexture;
        private bool _isUploadingBadge;

        private readonly List<BadgeData> _currentBadges = new List<BadgeData>
        {
            new BadgeData("Beginner", 100, "medal"),
            new BadgeData("Quiz Master", 500, "trophy"),
            new BadgeData("Anatomist", 1000, "brain"),
            new BadgeData("Expert", 2000, "shield"),
        };

        /// <summary>Snapshot of _currentBadges as of the last load/save, used by
        /// HasUnsavedChanges() to detect an added/removed/edited badge that
        /// hasn't been persisted yet. Kept in sync with _currentBadges in
        /// SetBadges() (on load) and OnSaveChangesClicked()'s success callback.</summary>
        private List<BadgeData> _savedBadges = new List<BadgeData>();

        /// <summary>
        /// Placeholder shown until the real global level thresholds are loaded
        /// from the backend via SetLevels(). This screen never mutates this
        /// list through user input - it's display-only.
        /// </summary>
        private readonly List<LevelData> _currentLevels = new List<LevelData>
        {
            new LevelData(1, "Novice", 0),
            new LevelData(2, "Learner", 100),
            new LevelData(3, "Student", 300),
            new LevelData(4, "Scholar", 600),
            new LevelData(5, "Expert", 1000),
            new LevelData(6, "Master", 1500),
            new LevelData(7, "Guru", 2100),
            new LevelData(8, "Legend", 2800),
        };

        private bool _realDataReceived;

        // Last values pushed through SetPointsConfiguration() - the text fields
        // above get destroyed and re-queried fresh every OnEnable (UI is rebuilt
        // each time), so without this a re-enable that skips LoadSettings() would
        // show blank/default fields instead of what was actually loaded/saved.
        // Matches AdminGamificationService.DefaultPointsAndBadges() (Easy=1,
        // Medium=2, Hard=5) - the same fallback used server-side for a teacher
        // with no saved config yet - so this screen's placeholder never disagrees
        // with what a brand-new teacher's quizzes actually award.
        private int _lastEasyPoints = 1;
        private int _lastMediumPoints = 2;
        private int _lastHardPoints = 5;

        private void OnEnable()
        {
            Debug.Log("[AdminGamificationSettingsController] OnEnable called");

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
                Debug.LogError("[AdminGamificationSettingsController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();

            // Rebuilt against THIS open's freshly-cloned tree - see the
            // _loading field comment for why a previous open's instance can't
            // be reused here.
            _loading?.Dispose();
            _loading = new LoadingOverlay(_screenRoot);

            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshBadgesUI();
            RefreshLevelsUI();
            RefreshPreview();
            SetPointsConfiguration(_lastEasyPoints, _lastMediumPoints, _lastHardPoints);

            // Seed the "saved" badge snapshot from the mock defaults so
            // HasUnsavedChanges() doesn't false-positive before LoadSettings'
            // real fetch resolves (SetBadges() re-syncs it once that lands).
            if (_savedBadges.Count == 0) _savedBadges = new List<BadgeData>(_currentBadges);

            CloseAddBadgeModal();
            _leaveDialogOverlay?.AddToClassList("hidden");

            // First time this screen opens this session -> fetch. Badges are
            // already patched locally on add/remove (see OnAddBadgeSubmitClicked
            // / the remove handler), and levels are fixed/read-only from this
            // screen (see LoadSettings' doc comment), so a re-enable (e.g.
            // switching tabs elsewhere and coming back) can just repaint from
            // cache via RefreshBadgesUI()/RefreshLevelsUI()/RefreshPreview()
            // above instead of re-fetching. Same pattern as
            // StudentAchievementsController.
            if (!_realDataReceived)
            {
                LoadSettings();
            }
        }

        /// <summary>
        /// Pulls the current points config / badges / (read-only) levels from
        /// AdminGamificationService.FetchSettings() and pushes them into the
        /// UI via the same SetPointsConfiguration/SetBadges/SetLevels public
        /// API a caller outside this controller would use. Called on enable;
        /// the mock defaults above render first so the screen isn't empty
        /// while this fetch is in flight.
        /// </summary>
        private void LoadSettings()
        {
            if (AdminGamificationService.Instance == null)
            {
                Debug.LogWarning("[AdminGamificationSettingsController] AdminGamificationService.Instance is null - " +
                    "showing built-in defaults only.");
                return;
            }

            AdminGamificationService.Instance.FetchSettings(settings =>
            {
                _realDataReceived = true;

                SetPointsConfiguration(settings.EasyPoints, settings.MediumPoints, settings.HardPoints);

                var badges = settings.Badges.ConvertAll(b => new BadgeData(b.Name, b.PointsRequired, b.IconKey, b.IconUrl, b.BadgeId));
                SetBadges(badges);

                var levels = settings.Levels.ConvertAll(l => new LevelData(l.LevelNumber, l.Title, l.PointsRequired));
                SetLevels(levels);
            });
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            _loading?.Dispose();

            if (_headerGradientTexture != null) { Destroy(_headerGradientTexture); _headerGradientTexture = null; }
            if (_addBadgeButtonGradientTexture != null) { Destroy(_addBadgeButtonGradientTexture); _addBadgeButtonGradientTexture = null; }
            if (_badgeModalSubmitGradientTexture != null) { Destroy(_badgeModalSubmitGradientTexture); _badgeModalSubmitGradientTexture = null; }
            if (_previewGradientTexture != null) { Destroy(_previewGradientTexture); _previewGradientTexture = null; }
            ClearPendingBadgeImage();
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _saveChangesButton?.UnregisterCallback<ClickEvent>(OnSaveChangesClicked);
            _leaveDialogCancelButton?.UnregisterCallback<ClickEvent>(OnLeaveDialogCancelClicked);
            _leaveDialogConfirmButton?.UnregisterCallback<ClickEvent>(OnLeaveDialogConfirmClicked);

            _easyPointsField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _mediumPointsField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _hardPointsField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _badgePointsField?.UnregisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _easyPointsField?.UnregisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _mediumPointsField?.UnregisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _hardPointsField?.UnregisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);

            _addBadgeButton?.UnregisterCallback<ClickEvent>(OnAddBadgeClicked);
            _addBadgeCloseButton?.UnregisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeCancelButton?.UnregisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeSubmitButton?.UnregisterCallback<ClickEvent>(OnAddBadgeSubmitClicked);
            _badgeUploadButton?.UnregisterCallback<ClickEvent>(OnChooseBadgeImageClicked);
            _badgeRemoveImageButton?.UnregisterCallback<ClickEvent>(OnRemoveBadgeImageClicked);

            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[AdminGamificationSettingsController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _backButton = _screenRoot.Q<Button>("back-button");
            _saveChangesButton = _screenRoot.Q<Button>("save-changes-button");

            _leaveDialogOverlay = _screenRoot.Q<VisualElement>("leave-dialog-overlay");
            _leaveDialogCancelButton = _screenRoot.Q<Button>("leave-dialog-cancel-button");
            _leaveDialogConfirmButton = _screenRoot.Q<Button>("leave-dialog-confirm-button");

            _easyPointsField = _screenRoot.Q<TextField>("easy-points-field");
            _mediumPointsField = _screenRoot.Q<TextField>("medium-points-field");
            _hardPointsField = _screenRoot.Q<TextField>("hard-points-field");
            _easyPointsError = _screenRoot.Q<Label>("easy-points-error");
            _mediumPointsError = _screenRoot.Q<Label>("medium-points-error");
            _hardPointsError = _screenRoot.Q<Label>("hard-points-error");

            _addBadgeButton = _screenRoot.Q<Button>("add-badge-button");
            _badgesEmptyLabel = _screenRoot.Q<VisualElement>("badges-empty-label");
            _badgesList = _screenRoot.Q<VisualElement>("badges-list");

            _levelsGlobalTag = _screenRoot.Q<VisualElement>("levels-global-tag");
            _levelsEmptyLabel = _screenRoot.Q<VisualElement>("levels-empty-label");
            _levelsList = _screenRoot.Q<VisualElement>("levels-list");

            _previewCard = _screenRoot.Q<VisualElement>("preview-card");
            _previewEasyValue = _screenRoot.Q<Label>("preview-easy-value");
            _previewMediumValue = _screenRoot.Q<Label>("preview-medium-value");
            _previewHardValue = _screenRoot.Q<Label>("preview-hard-value");
            _progressionSummaryLabel = _screenRoot.Q<Label>("progression-summary-label");

            _addBadgeModalOverlay = _screenRoot.Q<VisualElement>("add-badge-modal-overlay");
            _addBadgeCloseButton = _screenRoot.Q<Button>("add-badge-close-button");
            _addBadgeCancelButton = _screenRoot.Q<Button>("add-badge-cancel-button");
            _addBadgeSubmitButton = _screenRoot.Q<Button>("add-badge-submit-button");
            _badgeNameField = _screenRoot.Q<TextField>("badge-name-field");
            _badgeNameError = _screenRoot.Q<Label>("badge-name-error");
            _badgePointsField = _screenRoot.Q<TextField>("badge-points-field");
            _badgePointsError = _screenRoot.Q<Label>("badge-points-error");
            _badgeIconGrid = _screenRoot.Q<VisualElement>("badge-icon-grid");
            _addBadgeStatusLabel = _screenRoot.Q<Label>("add-badge-status-label");
            _badgeCustomPreview = _screenRoot.Q<VisualElement>("badge-custom-preview");
            _badgeUploadButton = _screenRoot.Q<Button>("badge-upload-image-button");
            _badgeRemoveImageButton = _screenRoot.Q<Button>("badge-remove-image-button");

            BuildBadgeIconGrid();

            Debug.Log($"[AdminGamificationSettingsController] Found badges list: {_badgesList != null}, levels list: {_levelsList != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _saveChangesButton?.RegisterCallback<ClickEvent>(OnSaveChangesClicked);
            _leaveDialogCancelButton?.RegisterCallback<ClickEvent>(OnLeaveDialogCancelClicked);
            _leaveDialogConfirmButton?.RegisterCallback<ClickEvent>(OnLeaveDialogConfirmClicked);

            // Digit filters registered before the preview-refresh callbacks so
            // field.value is already corrected by the time the preview reads it.
            _easyPointsField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _mediumPointsField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _hardPointsField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _badgePointsField?.RegisterCallback<ChangeEvent<string>>(OnNumericFieldChanged);
            _easyPointsField?.RegisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _mediumPointsField?.RegisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _hardPointsField?.RegisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);

            _addBadgeButton?.RegisterCallback<ClickEvent>(OnAddBadgeClicked);
            _addBadgeCloseButton?.RegisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeCancelButton?.RegisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeSubmitButton?.RegisterCallback<ClickEvent>(OnAddBadgeSubmitClicked);
            _badgeUploadButton?.RegisterCallback<ClickEvent>(OnChooseBadgeImageClicked);
            _badgeRemoveImageButton?.RegisterCallback<ClickEvent>(OnRemoveBadgeImageClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push existing points configuration values in (e.g. loaded from backend).</summary>
        public void SetPointsConfiguration(int easyPoints, int mediumPoints, int hardPoints)
        {
            _lastEasyPoints = easyPoints;
            _lastMediumPoints = mediumPoints;
            _lastHardPoints = hardPoints;

            if (_easyPointsField != null) _easyPointsField.SetValueWithoutNotify(easyPoints.ToString());
            if (_mediumPointsField != null) _mediumPointsField.SetValueWithoutNotify(mediumPoints.ToString());
            if (_hardPointsField != null) _hardPointsField.SetValueWithoutNotify(hardPoints.ToString());
            RefreshPreview();
        }

        /// <summary>Replace the current badge list (e.g. loaded from backend).</summary>
        public void SetBadges(List<BadgeData> badges)
        {
            _currentBadges.Clear();
            if (badges != null) _currentBadges.AddRange(badges);
            _savedBadges = new List<BadgeData>(_currentBadges);
            RefreshBadgesUI();
            RefreshPreview();
        }

        /// <summary>Replace the current level list (e.g. loaded from backend).</summary>
        public void SetLevels(List<LevelData> levels)
        {
            _currentLevels.Clear();
            if (levels != null) _currentLevels.AddRange(levels);
            RefreshLevelsUI();
            RefreshPreview();
        }

        // ---------------- Points Configuration ----------------

        private void OnPointsFieldChanged(ChangeEvent<string> evt) => RefreshPreview();

        private void RefreshPreview()
        {
            int easy = ParsePointsOrDefault(_easyPointsField, 10);
            int medium = ParsePointsOrDefault(_mediumPointsField, 20);
            int hard = ParsePointsOrDefault(_hardPointsField, 30);

            if (_previewEasyValue != null) _previewEasyValue.text = easy.ToString();
            if (_previewMediumValue != null) _previewMediumValue.text = medium.ToString();
            if (_previewHardValue != null) _previewHardValue.text = hard.ToString();

            if (_progressionSummaryLabel != null)
            {
                int levelCount = _currentLevels?.Count ?? 0;
                int badgeCount = _currentBadges?.Count ?? 0;
                string levelWord = levelCount == 1 ? "level" : "levels";
                string badgeWord = badgeCount == 1 ? "badge" : "badges";
                _progressionSummaryLabel.text =
                    $"Students will progress through {levelCount} {levelWord} and can earn {badgeCount} unique {badgeWord}.";
            }
        }

        private static int ParsePointsOrDefault(TextField field, int fallback)
        {
            if (field != null && int.TryParse(field.value, out int value))
            {
                return value;
            }
            return fallback;
        }

        /// <summary>Live keystroke filter so a points TextField only ever holds a whole
        /// number - without this a teacher can type letters/symbols and, since this
        /// screen otherwise silently falls back to a default on save, never find out
        /// what they typed wasn't used. Same shared pattern as
        /// AdminQuizManagementController.OnNumericFieldChanged.</summary>
        private void OnNumericFieldChanged(ChangeEvent<string> evt)
        {
            var field = evt.target as TextField;
            if (field == null) return;

            string digitsOnly = new string((evt.newValue ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digitsOnly != evt.newValue)
            {
                field.SetValueWithoutNotify(digitsOnly);
            }
        }

        /// <summary>Validates a points TextField against [min, max] and shows/clears its
        /// error label. Returns the parsed value via <paramref name="value"/> when valid.
        /// Used on Save/Add Badge so a bad value blocks the action instead of silently
        /// falling back to a default.</summary>
        private bool ValidatePointsField(TextField field, Label errorLabel, string fieldName, int min, int max, out int value)
        {
            string raw = field?.value?.Trim();

            if (!int.TryParse(raw, out value))
            {
                SetError(errorLabel, $"Enter {fieldName} as a whole number.");
                return false;
            }

            if (value < min || value > max)
            {
                SetError(errorLabel, $"{fieldName} must be between {min} and {max}.");
                return false;
            }

            ClearError(errorLabel);
            return true;
        }

        // ---------------- Badges list ----------------

        private void RefreshBadgesUI()
        {
            bool hasBadges = _currentBadges != null && _currentBadges.Count > 0;

            _badgesEmptyLabel?.EnableInClassList("hidden", hasBadges);
            _badgesList?.EnableInClassList("hidden", !hasBadges);

            if (_badgesList == null) return;

            _badgesList.Clear();

            if (!hasBadges) return;

            foreach (var badge in _currentBadges)
            {
                _badgesList.Add(BuildBadgeRow(badge));
            }
        }

        private VisualElement BuildBadgeRow(BadgeData badge)
        {
            var row = new VisualElement();
            row.AddToClassList("item-row");

            var iconCircle = new VisualElement();
            iconCircle.AddToClassList("item-icon-circle");
            iconCircle.Add(BadgeIconView.Create(badge.IconKey, badge.IconUrl, 44));

            var textCol = new VisualElement();
            textCol.AddToClassList("item-text-col");
            var nameLabel = new Label(badge.Name);
            nameLabel.AddToClassList("item-title-label");
            var subtitleLabel = new Label($"Requires {badge.PointsRequired:N0} points");
            subtitleLabel.AddToClassList("item-subtitle-label");
            textCol.Add(nameLabel);
            textCol.Add(subtitleLabel);

            var deleteButton = new Button(() => OnDeleteBadgeClicked(badge));
            deleteButton.AddToClassList("item-delete-button");
            var deleteArt = new VisualElement { pickingMode = PickingMode.Ignore };
            deleteArt.AddToClassList("item-delete-art");
            deleteButton.Add(deleteArt);

            row.Add(iconCircle);
            row.Add(textCol);
            row.Add(deleteButton);

            return row;
        }

        private void OnDeleteBadgeClicked(BadgeData badge)
        {
            Debug.Log($"[AdminGamificationSettingsController] Deleting badge '{badge.Name}'.");
            _currentBadges.Remove(badge);
            RefreshBadgesUI();
            RefreshPreview();
        }

        // ---------------- Levels list ----------------

        private void RefreshLevelsUI()
        {
            bool hasLevels = _currentLevels != null && _currentLevels.Count > 0;

            _levelsEmptyLabel?.EnableInClassList("hidden", hasLevels);
            _levelsList?.EnableInClassList("hidden", !hasLevels);

            if (_levelsList == null) return;

            _levelsList.Clear();

            if (!hasLevels) return;

            foreach (var level in _currentLevels)
            {
                _levelsList.Add(BuildLevelRow(level));
            }
        }

        /// <summary>Read-only row - levels are global/fixed, so there's no delete button here.</summary>
        private VisualElement BuildLevelRow(LevelData level)
        {
            var row = new VisualElement();
            row.AddToClassList("item-row");

            var textCol = new VisualElement();
            textCol.AddToClassList("item-text-col");
            var titleLabel = new Label($"Level {level.LevelNumber} - {level.Title}");
            titleLabel.AddToClassList("item-title-label");
            var subtitleLabel = new Label($"{level.PointsRequired:N0} points required");
            subtitleLabel.AddToClassList("item-subtitle-label");
            textCol.Add(titleLabel);
            textCol.Add(subtitleLabel);

            row.Add(textCol);

            return row;
        }

        // ---------------- Add Badge modal ----------------

        private void BuildBadgeIconGrid()
        {
            if (_badgeIconGrid == null) return;

            _badgeIconGrid.Clear();
            _badgeIconGridItems.Clear();

            foreach (var icon in IconChoices)
            {
                string capturedIcon = icon;

                var item = new Button(() => OnBadgeIconPicked(capturedIcon));
                item.AddToClassList("icon-grid-item");

                item.Add(BadgeIconView.Create(icon, null, 52));

                _badgeIconGrid.Add(item);
                _badgeIconGridItems.Add(item);
            }

            OnBadgeIconPicked(IconChoices[0]);
        }

        private void OnBadgeIconPicked(string icon)
        {
            // Picking a preset replaces any custom image chosen earlier.
            ClearPendingBadgeImage();
            _selectedBadgeIcon = icon;

            for (int i = 0; i < _badgeIconGridItems.Count; i++)
            {
                bool selected = i < IconChoices.Length && IconChoices[i] == icon;
                _badgeIconGridItems[i].EnableInClassList("icon-grid-item-selected", selected);
            }
        }

        private void OnAddBadgeClicked(ClickEvent evt) => OpenAddBadgeModal();

        private void OpenAddBadgeModal()
        {
            if (_badgeNameField != null) _badgeNameField.value = string.Empty;
            if (_badgePointsField != null) _badgePointsField.value = "0";
            ClearError(_badgeNameError);
            ClearError(_badgePointsError);
            SetStatus(_addBadgeStatusLabel, string.Empty);
            OnBadgeIconPicked(IconChoices[0]);

            _addBadgeModalOverlay?.RemoveFromClassList("hidden");
        }

        private void CloseAddBadgeModal()
        {
            ClearPendingBadgeImage();
            _addBadgeModalOverlay?.AddToClassList("hidden");
        }

        private void OnAddBadgeCancelClicked(ClickEvent evt)
        {
            if (_isUploadingBadge) return; // let the upload finish first
            CloseAddBadgeModal();
        }

        private void OnAddBadgeSubmitClicked(ClickEvent evt)
        {
            if (_isUploadingBadge) return;

            string name = _badgeNameField?.value?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                SetError(_badgeNameError, "Please enter a badge name");
                return;
            }

            ClearError(_badgeNameError);

            if (!ValidatePointsField(_badgePointsField, _badgePointsError, "Points required", 0, 100000, out int points))
            {
                return;
            }

            string icon = string.IsNullOrEmpty(_selectedBadgeIcon) ? IconChoices[0] : _selectedBadgeIcon;

            // Preset icon -> nothing to upload.
            if (_pendingBadgeImageBytes == null)
            {
                AddBadgeAndClose(name, points, icon, string.Empty);
                return;
            }

            // Custom image -> upload to Cloudinary first, then keep the returned link on the badge.
            // The link itself is written to Firestore (badges.{id}.iconUrl) by Save Changes,
            // like every other badge edit on this screen.
            var uploader = CloudinaryAvatarUploadService.Instance;
            string ownerUid = AdminAuthService.Instance?.CurrentAdmin?.Uid;
            if (uploader == null || string.IsNullOrEmpty(ownerUid))
            {
                SetStatus(_addBadgeStatusLabel, "Image upload isn't available right now. Pick a preset icon or try again later.");
                return;
            }

            _isUploadingBadge = true;
            _addBadgeSubmitButton?.SetEnabled(false);
            SetStatus(_addBadgeStatusLabel, "Uploading image...");

            uploader.UploadBadgeIcon(_pendingBadgeImageBytes, ownerUid, (ok, url) =>
            {
                _isUploadingBadge = false;
                _addBadgeSubmitButton?.SetEnabled(true);

                if (!ok || string.IsNullOrEmpty(url))
                {
                    SetStatus(_addBadgeStatusLabel, "Could not upload the image. Check your connection and try again.");
                    return;
                }

                AddBadgeAndClose(name, points, icon, url);
            });
        }

        private void AddBadgeAndClose(string name, int points, string iconKey, string iconUrl)
        {
            _currentBadges.Add(new BadgeData(name, points, iconKey, iconUrl));
            RefreshBadgesUI();
            RefreshPreview();
            CloseAddBadgeModal();
        }

        // ---------------- Custom badge image ----------------
        //
        // Same NativeGallery picker the Edit Profile screens use for avatars. The
        // image is downscaled to 512px max and kept as PNG so transparency survives.
        // It is only uploaded when the teacher taps "Add Badge" (see above), so
        // picking and then cancelling never leaves an orphan in Cloudinary.
        // NativeGallery's picker doesn't open in the Unity Editor - test on a device.

        private void OnChooseBadgeImageClicked(ClickEvent evt)
        {
            if (_isUploadingBadge || NativeGallery.IsMediaPickerBusy()) return;

            NativeGallery.GetImageFromGallery(path =>
            {
                if (string.IsNullOrEmpty(path)) return; // teacher cancelled the picker

                Texture2D picked = NativeGallery.LoadImageAtPath(path, maxSize: 512, markTextureNonReadable: false);
                if (picked == null)
                {
                    Debug.LogWarning($"[AdminGamificationSettingsController] Could not load image at '{path}'.");
                    SetStatus(_addBadgeStatusLabel, "Could not load that image. Please try a different one.");
                    return;
                }

                SetPendingBadgeImage(picked);
            }, "Select a badge image", "image/*");
        }

        private void SetPendingBadgeImage(Texture2D texture)
        {
            ClearPendingBadgeImage();

            _pendingBadgeTexture = texture; // this controller owns it now - destroyed in ClearPendingBadgeImage
            _pendingBadgeImageBytes = texture.EncodeToPNG();

            if (_badgeCustomPreview != null)
            {
                _badgeCustomPreview.style.backgroundImage = new StyleBackground(Background.FromTexture2D(texture));
                _badgeCustomPreview.AddToClassList("badge-custom-preview-active");
            }

            _badgeRemoveImageButton?.RemoveFromClassList("hidden");

            // No preset counts as selected while a custom image is chosen.
            foreach (var item in _badgeIconGridItems) item.RemoveFromClassList("icon-grid-item-selected");

            SetStatus(_addBadgeStatusLabel, string.Empty);
        }

        private void ClearPendingBadgeImage()
        {
            if (_badgeCustomPreview != null)
            {
                _badgeCustomPreview.style.backgroundImage = StyleKeyword.Null;
                _badgeCustomPreview.RemoveFromClassList("badge-custom-preview-active");
            }

            _badgeRemoveImageButton?.AddToClassList("hidden");

            if (_pendingBadgeTexture != null) { Destroy(_pendingBadgeTexture); _pendingBadgeTexture = null; }
            _pendingBadgeImageBytes = null;
        }

        private void OnRemoveBadgeImageClicked(ClickEvent evt)
        {
            if (_isUploadingBadge) return;
            OnBadgeIconPicked(string.IsNullOrEmpty(_selectedBadgeIcon) ? IconChoices[0] : _selectedBadgeIcon);
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            if (HasUnsavedChanges())
            {
                _leaveDialogOverlay?.RemoveFromClassList("hidden");
                return;
            }

            NavigateBackToDashboard();
        }

        private void NavigateBackToDashboard()
        {
            Debug.Log("[AdminGamificationSettingsController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        private void OnLeaveDialogCancelClicked(ClickEvent evt)
        {
            _leaveDialogOverlay?.AddToClassList("hidden");
        }

        private void OnLeaveDialogConfirmClicked(ClickEvent evt)
        {
            _leaveDialogOverlay?.AddToClassList("hidden");
            NavigateBackToDashboard();
        }

        /// <summary>True when the points fields or badge list differ from what's
        /// actually been saved (_lastEasy/Medium/HardPoints and _savedBadges,
        /// both kept in sync with the backend in SetBadges()/the Save success
        /// callback). Drives the leave-without-saving confirm dialog on Back.</summary>
        private bool HasUnsavedChanges()
        {
            if (PointsFieldDiffersFromSaved(_easyPointsField, _lastEasyPoints)) return true;
            if (PointsFieldDiffersFromSaved(_mediumPointsField, _lastMediumPoints)) return true;
            if (PointsFieldDiffersFromSaved(_hardPointsField, _lastHardPoints)) return true;

            if (_currentBadges.Count != _savedBadges.Count) return true;
            for (int i = 0; i < _currentBadges.Count; i++)
            {
                if (!_currentBadges[i].Equals(_savedBadges[i])) return true;
            }

            return false;
        }

        /// <summary>A field that currently can't even be parsed (mid-edit, empty,
        /// letters before the digit filter catches up) counts as changed too -
        /// there's nothing valid there yet to consider "saved".</summary>
        private static bool PointsFieldDiffersFromSaved(TextField field, int savedValue)
        {
            if (field == null) return false;
            if (!int.TryParse(field.value?.Trim(), out int current)) return true;
            return current != savedValue;
        }

        private void OnSaveChangesClicked(ClickEvent evt)
        {
            Debug.Log("[AdminGamificationSettingsController] Save Changes tapped.");

            if (AdminGamificationService.Instance == null)
            {
                Debug.LogWarning("[AdminGamificationSettingsController] AdminGamificationService.Instance is null - can't save.");
                return;
            }

            bool easyValid = ValidatePointsField(_easyPointsField, _easyPointsError, "Easy question points", 1, 1000, out int easyPoints);
            bool mediumValid = ValidatePointsField(_mediumPointsField, _mediumPointsError, "Medium question points", 1, 1000, out int mediumPoints);
            bool hardValid = ValidatePointsField(_hardPointsField, _hardPointsError, "Hard question points", 1, 1000, out int hardPoints);

            if (!easyValid || !mediumValid || !hardValid)
            {
                Debug.LogWarning("[AdminGamificationSettingsController] Save blocked - one or more points fields are invalid.");
                return;
            }

            var badgeEntries = _currentBadges.ConvertAll(b => new AdminGamificationService.BadgeEntry
            {
                BadgeId = b.BadgeId,
                Name = b.Name,
                IconKey = b.IconKey,
                IconUrl = b.IconUrl,
                PointsRequired = b.PointsRequired
            });

            _saveChangesButton?.SetEnabled(false);
            ShowLoadingOverlay();

            // Note: no levels argument - levels are global & fixed and are never
            // written from this screen (see AdminGamificationService.SaveSettings).
            AdminGamificationService.Instance.SaveSettings(easyPoints, mediumPoints, hardPoints, badgeEntries, (success, error) =>
            {
                if (success)
                {
                    _lastEasyPoints = easyPoints;
                    _lastMediumPoints = mediumPoints;
                    _lastHardPoints = hardPoints;
                    _savedBadges = new List<BadgeData>(_currentBadges);
                }

                HideLoadingOverlay();
                _saveChangesButton?.SetEnabled(true);

                OnSaveSettingsResult(success, error);
            });
        }

        private void OnSaveSettingsResult(bool success, string error)
        {
            if (success)
            {
                Debug.Log("[AdminGamificationSettingsController] Gamification settings saved.");
            }
            else
            {
                Debug.LogWarning($"[AdminGamificationSettingsController] Save failed: {error}");
            }
        }

        private void ShowLoadingOverlay()
        {
            _loading?.Show("Saving changes...");
        }

        private void HideLoadingOverlay()
        {
            _loading?.Hide();
        }

        // ---------------- Helpers ----------------

        private void SetError(Label label, string message)
        {
            if (label == null) return;
            label.text = message;
            label.RemoveFromClassList("hidden");
        }

        private void ClearError(Label label)
        {
            if (label == null) return;
            label.text = string.Empty;
            label.AddToClassList("hidden");
        }

        private void SetStatus(Label label, string message)
        {
            if (label == null) return;
            label.text = message;
            if (string.IsNullOrEmpty(message))
                label.AddToClassList("hidden");
            else
                label.RemoveFromClassList("hidden");
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradients (USS has no linear-gradient) ----------------

        private void ApplyGradients()
        {
            if (!AnatomiaTheme.UseGradientChrome) return; // minimalist theme: flat chrome, see Theme/AnatomiaTheme.cs

            if (_header != null)
            {
                if (_headerGradientTexture != null) Destroy(_headerGradientTexture);
                _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
            }

            if (_addBadgeButton != null)
            {
                if (_addBadgeButtonGradientTexture != null) Destroy(_addBadgeButtonGradientTexture);
                _addBadgeButtonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _addBadgeButton.style.backgroundImage = new StyleBackground(_addBadgeButtonGradientTexture);
            }

            if (_addBadgeSubmitButton != null)
            {
                if (_badgeModalSubmitGradientTexture != null) Destroy(_badgeModalSubmitGradientTexture);
                _badgeModalSubmitGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _addBadgeSubmitButton.style.backgroundImage = new StyleBackground(_badgeModalSubmitGradientTexture);
            }

            if (_previewCard != null)
            {
                if (_previewGradientTexture != null) Destroy(_previewGradientTexture);
                _previewGradientTexture = BuildGradientTexture(previewBackgroundStart, previewBackgroundEnd);
                _previewCard.style.backgroundImage = new StyleBackground(_previewGradientTexture);
            }
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "AdminGamificationSettingsGradientTexture"
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