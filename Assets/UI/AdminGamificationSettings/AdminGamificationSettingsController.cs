using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

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
    ///    with name/points/icon-emoji fields and a click-to-pick icon grid;
    ///    each row has a delete (trash) button
    ///  - Level Progression: renders the current level list, "Add Level"
    ///    opens a modal with number/title/points fields; each row has a
    ///    delete (trash) button
    ///  - Applies the green->blue gradient at runtime to the header, the two
    ///    "Add" buttons and the two modal submit buttons, plus a pale
    ///    green->blue gradient to the Preview card background (USS can't do
    ///    linear-gradient)
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///
    /// Hook up your real persistence call inside OnSaveChangesClicked() - e.g.
    /// call into your existing AdminGamificationService / backend here.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AdminGamificationSettingsController : MonoBehaviour
    {
        /// <summary>Plain data for a single row in the "Badges" list.</summary>
        public struct BadgeData
        {
            public string Name;
            public int PointsRequired;
            public string IconEmoji;

            public BadgeData(string name, int pointsRequired, string iconEmoji)
            {
                Name = name;
                PointsRequired = pointsRequired;
                IconEmoji = iconEmoji;
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

        private static readonly string[] IconChoices =
        {
            "\U0001F3C6", // 🏆 trophy
            "\U0001F31F", // 🌟 star
            "\u2B50",     // ⭐ star2
            "\U0001F396", // 🎖️ medal
            "\U0001F3C5", // 🏅 medal2
            "\U0001F451", // 👑 crown
            "\U0001F48E", // 💎 diamond
            "\U0001F3AF", // 🎯 target
            "\U0001F680", // 🚀 rocket
            "\U0001F525", // 🔥 fire
            "\U0001F44D", // 👍 thumbs up
            "\U0001F9E0", // 🧠 brain
        };

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;
        private Texture2D _addBadgeButtonGradientTexture;
        private Texture2D _addLevelButtonGradientTexture;
        private Texture2D _badgeModalSubmitGradientTexture;
        private Texture2D _levelModalSubmitGradientTexture;
        private Texture2D _previewGradientTexture;

        private VisualElement _header;
        private Button _backButton;
        private Button _saveChangesButton;

        private TextField _easyPointsField;
        private TextField _mediumPointsField;
        private TextField _hardPointsField;

        private Button _addBadgeButton;
        private VisualElement _badgesEmptyLabel;
        private VisualElement _badgesList;

        private Button _addLevelButton;
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
        private VisualElement _badgeIconGrid;
        private Label _addBadgeStatusLabel;
        private string _selectedBadgeIcon;
        private readonly List<VisualElement> _badgeIconGridItems = new List<VisualElement>();

        // Add Level modal
        private VisualElement _addLevelModalOverlay;
        private Button _addLevelCloseButton;
        private Button _addLevelCancelButton;
        private Button _addLevelSubmitButton;
        private TextField _levelNumberField;
        private TextField _levelTitleField;
        private Label _levelTitleError;
        private TextField _levelPointsField;
        private Label _addLevelStatusLabel;

        private readonly List<BadgeData> _currentBadges = new List<BadgeData>
        {
            new BadgeData("Beginner", 100, "\U0001F31F"),
            new BadgeData("Quiz Master", 500, "\U0001F3C6"),
            new BadgeData("Anatomist", 1000, "\U0001F9E0"),
            new BadgeData("Expert", 2000, "\U0001F451"),
        };

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
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshBadgesUI();
            RefreshLevelsUI();
            RefreshPreview();

            CloseAddBadgeModal();
            CloseAddLevelModal();
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (_headerGradientTexture != null) { Destroy(_headerGradientTexture); _headerGradientTexture = null; }
            if (_addBadgeButtonGradientTexture != null) { Destroy(_addBadgeButtonGradientTexture); _addBadgeButtonGradientTexture = null; }
            if (_addLevelButtonGradientTexture != null) { Destroy(_addLevelButtonGradientTexture); _addLevelButtonGradientTexture = null; }
            if (_badgeModalSubmitGradientTexture != null) { Destroy(_badgeModalSubmitGradientTexture); _badgeModalSubmitGradientTexture = null; }
            if (_levelModalSubmitGradientTexture != null) { Destroy(_levelModalSubmitGradientTexture); _levelModalSubmitGradientTexture = null; }
            if (_previewGradientTexture != null) { Destroy(_previewGradientTexture); _previewGradientTexture = null; }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _saveChangesButton?.UnregisterCallback<ClickEvent>(OnSaveChangesClicked);

            _easyPointsField?.UnregisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _mediumPointsField?.UnregisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _hardPointsField?.UnregisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);

            _addBadgeButton?.UnregisterCallback<ClickEvent>(OnAddBadgeClicked);
            _addBadgeCloseButton?.UnregisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeCancelButton?.UnregisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeSubmitButton?.UnregisterCallback<ClickEvent>(OnAddBadgeSubmitClicked);

            _addLevelButton?.UnregisterCallback<ClickEvent>(OnAddLevelClicked);
            _addLevelCloseButton?.UnregisterCallback<ClickEvent>(OnAddLevelCancelClicked);
            _addLevelCancelButton?.UnregisterCallback<ClickEvent>(OnAddLevelCancelClicked);
            _addLevelSubmitButton?.UnregisterCallback<ClickEvent>(OnAddLevelSubmitClicked);

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

            _easyPointsField = _screenRoot.Q<TextField>("easy-points-field");
            _mediumPointsField = _screenRoot.Q<TextField>("medium-points-field");
            _hardPointsField = _screenRoot.Q<TextField>("hard-points-field");

            _addBadgeButton = _screenRoot.Q<Button>("add-badge-button");
            _badgesEmptyLabel = _screenRoot.Q<VisualElement>("badges-empty-label");
            _badgesList = _screenRoot.Q<VisualElement>("badges-list");

            _addLevelButton = _screenRoot.Q<Button>("add-level-button");
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
            _badgeIconGrid = _screenRoot.Q<VisualElement>("badge-icon-grid");
            _addBadgeStatusLabel = _screenRoot.Q<Label>("add-badge-status-label");

            _addLevelModalOverlay = _screenRoot.Q<VisualElement>("add-level-modal-overlay");
            _addLevelCloseButton = _screenRoot.Q<Button>("add-level-close-button");
            _addLevelCancelButton = _screenRoot.Q<Button>("add-level-cancel-button");
            _addLevelSubmitButton = _screenRoot.Q<Button>("add-level-submit-button");
            _levelNumberField = _screenRoot.Q<TextField>("level-number-field");
            _levelTitleField = _screenRoot.Q<TextField>("level-title-field");
            _levelTitleError = _screenRoot.Q<Label>("level-title-error");
            _levelPointsField = _screenRoot.Q<TextField>("level-points-field");
            _addLevelStatusLabel = _screenRoot.Q<Label>("add-level-status-label");

            BuildBadgeIconGrid();

            Debug.Log($"[AdminGamificationSettingsController] Found badges list: {_badgesList != null}, levels list: {_levelsList != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _saveChangesButton?.RegisterCallback<ClickEvent>(OnSaveChangesClicked);

            _easyPointsField?.RegisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _mediumPointsField?.RegisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);
            _hardPointsField?.RegisterCallback<ChangeEvent<string>>(OnPointsFieldChanged);

            _addBadgeButton?.RegisterCallback<ClickEvent>(OnAddBadgeClicked);
            _addBadgeCloseButton?.RegisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeCancelButton?.RegisterCallback<ClickEvent>(OnAddBadgeCancelClicked);
            _addBadgeSubmitButton?.RegisterCallback<ClickEvent>(OnAddBadgeSubmitClicked);

            _addLevelButton?.RegisterCallback<ClickEvent>(OnAddLevelClicked);
            _addLevelCloseButton?.RegisterCallback<ClickEvent>(OnAddLevelCancelClicked);
            _addLevelCancelButton?.RegisterCallback<ClickEvent>(OnAddLevelCancelClicked);
            _addLevelSubmitButton?.RegisterCallback<ClickEvent>(OnAddLevelSubmitClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push existing points configuration values in (e.g. loaded from backend).</summary>
        public void SetPointsConfiguration(int easyPoints, int mediumPoints, int hardPoints)
        {
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
            var iconLabel = new Label(string.IsNullOrEmpty(badge.IconEmoji) ? "\U0001F3C6" : badge.IconEmoji);
            iconLabel.AddToClassList("item-icon-emoji");
            iconCircle.Add(iconLabel);

            var textCol = new VisualElement();
            textCol.AddToClassList("item-text-col");
            var nameLabel = new Label(badge.Name);
            nameLabel.AddToClassList("item-title-label");
            var subtitleLabel = new Label($"Requires {badge.PointsRequired:N0} points");
            subtitleLabel.AddToClassList("item-subtitle-label");
            textCol.Add(nameLabel);
            textCol.Add(subtitleLabel);

            var deleteButton = new Button(() => OnDeleteBadgeClicked(badge)) { text = "\U0001F5D1" };
            deleteButton.AddToClassList("item-delete-button");
            deleteButton.Q<Label>()?.AddToClassList("item-delete-icon");

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

            var deleteButton = new Button(() => OnDeleteLevelClicked(level)) { text = "\U0001F5D1" };
            deleteButton.AddToClassList("item-delete-button");
            deleteButton.Q<Label>()?.AddToClassList("item-delete-icon");

            row.Add(textCol);
            row.Add(deleteButton);

            return row;
        }

        private void OnDeleteLevelClicked(LevelData level)
        {
            Debug.Log($"[AdminGamificationSettingsController] Deleting level {level.LevelNumber} ('{level.Title}').");
            _currentLevels.Remove(level);
            RefreshLevelsUI();
            RefreshPreview();
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

                var emojiLabel = new Label(icon);
                emojiLabel.AddToClassList("icon-grid-item-emoji");
                item.Add(emojiLabel);

                _badgeIconGrid.Add(item);
                _badgeIconGridItems.Add(item);
            }

            OnBadgeIconPicked(IconChoices[0]);
        }

        private void OnBadgeIconPicked(string icon)
        {
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
            SetStatus(_addBadgeStatusLabel, string.Empty);
            OnBadgeIconPicked(IconChoices[0]);

            _addBadgeModalOverlay?.RemoveFromClassList("hidden");
        }

        private void CloseAddBadgeModal()
        {
            _addBadgeModalOverlay?.AddToClassList("hidden");
        }

        private void OnAddBadgeCancelClicked(ClickEvent evt) => CloseAddBadgeModal();

        private void OnAddBadgeSubmitClicked(ClickEvent evt)
        {
            string name = _badgeNameField?.value?.Trim();

            if (string.IsNullOrEmpty(name))
            {
                SetError(_badgeNameError, "Please enter a badge name");
                return;
            }

            ClearError(_badgeNameError);

            int points = ParsePointsOrDefault(_badgePointsField, 0);
            string icon = string.IsNullOrEmpty(_selectedBadgeIcon) ? IconChoices[0] : _selectedBadgeIcon;

            _currentBadges.Add(new BadgeData(name, points, icon));
            RefreshBadgesUI();
            RefreshPreview();

            // TODO: replace with your real persistence call, e.g.:
            // AdminGamificationService.Instance.CreateBadge(name, points, icon, OnCreateBadgeResult);

            CloseAddBadgeModal();
        }

        // ---------------- Add Level modal ----------------

        private void OnAddLevelClicked(ClickEvent evt) => OpenAddLevelModal();

        private void OpenAddLevelModal()
        {
            int nextLevelNumber = 1;
            foreach (var level in _currentLevels)
            {
                if (level.LevelNumber >= nextLevelNumber) nextLevelNumber = level.LevelNumber + 1;
            }

            if (_levelNumberField != null) _levelNumberField.value = nextLevelNumber.ToString();
            if (_levelTitleField != null) _levelTitleField.value = string.Empty;
            if (_levelPointsField != null) _levelPointsField.value = "0";
            ClearError(_levelTitleError);
            SetStatus(_addLevelStatusLabel, string.Empty);

            _addLevelModalOverlay?.RemoveFromClassList("hidden");
        }

        private void CloseAddLevelModal()
        {
            _addLevelModalOverlay?.AddToClassList("hidden");
        }

        private void OnAddLevelCancelClicked(ClickEvent evt) => CloseAddLevelModal();

        private void OnAddLevelSubmitClicked(ClickEvent evt)
        {
            string title = _levelTitleField?.value?.Trim();

            if (string.IsNullOrEmpty(title))
            {
                SetError(_levelTitleError, "Please enter a level title");
                return;
            }

            ClearError(_levelTitleError);

            int levelNumber = ParsePointsOrDefault(_levelNumberField, _currentLevels.Count + 1);
            int points = ParsePointsOrDefault(_levelPointsField, 0);

            _currentLevels.Add(new LevelData(levelNumber, title, points));
            RefreshLevelsUI();
            RefreshPreview();

            // TODO: replace with your real persistence call, e.g.:
            // AdminGamificationService.Instance.CreateLevel(levelNumber, title, points, OnCreateLevelResult);

            CloseAddLevelModal();
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[AdminGamificationSettingsController] Navigating back to admin dashboard");
            UIManager.Instance.ShowAdminDashboard();
        }

        private void OnSaveChangesClicked(ClickEvent evt)
        {
            Debug.Log("[AdminGamificationSettingsController] Save Changes tapped.");

            // TODO: replace with your real persistence call, e.g.:
            // AdminGamificationService.Instance.SaveGamificationSettings(
            //     easyPoints, mediumPoints, hardPoints, _currentBadges, _currentLevels, OnSaveResult);
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

            if (_addLevelButton != null)
            {
                if (_addLevelButtonGradientTexture != null) Destroy(_addLevelButtonGradientTexture);
                _addLevelButtonGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _addLevelButton.style.backgroundImage = new StyleBackground(_addLevelButtonGradientTexture);
            }

            if (_addBadgeSubmitButton != null)
            {
                if (_badgeModalSubmitGradientTexture != null) Destroy(_badgeModalSubmitGradientTexture);
                _badgeModalSubmitGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _addBadgeSubmitButton.style.backgroundImage = new StyleBackground(_badgeModalSubmitGradientTexture);
            }

            if (_addLevelSubmitButton != null)
            {
                if (_levelModalSubmitGradientTexture != null) Destroy(_levelModalSubmitGradientTexture);
                _levelModalSubmitGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
                _addLevelSubmitButton.style.backgroundImage = new StyleBackground(_levelModalSubmitGradientTexture);
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
