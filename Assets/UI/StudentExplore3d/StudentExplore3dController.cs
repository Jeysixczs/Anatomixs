using System;
using Anatomia3D.Backend;
using Anatomia3D.UI;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class StudentExplore3dController : MonoBehaviour
{
    [Header("Gradient colors (matches the mock)")]
    [SerializeField] private Color gradientStart = new Color(0.145f, 0.388f, 0.925f); // blue
    [SerializeField] private Color gradientEnd = new Color(0.086f, 0.737f, 0.475f);   // green
    [SerializeField] private bool diagonalGradient = true;
    [SerializeField, Range(2, 256)] private int gradientTextureResolution = 64;

    [Header("Sync Progress")]
    [Tooltip("Optional. Drives the Sync Progress button/status label. Leave " +
             "unassigned to hide sync entirely - the button/label are simply " +
             "left in their default UXML state and never wired.")]
    [SerializeField] private AnatomyPlayModeSyncService syncService;

    private UIDocument _document;
    private VisualElement _root;
    private VisualElement _screenRoot;
    private Texture2D _headerGradientTexture;

    private VisualElement _header;

    // ===== Card image fades =====
    // Left-edge white->transparent fade drawn over each card's body-system
    // photo (see .card-image-fade in StudentExplore3d.uss). One shared
    // horizontal gradient texture is generated and reused across all three,
    // since the fade itself is identical - only the photo underneath it
    // differs per card.
    private VisualElement _skeletalImageFade;
    private VisualElement _muscularImageFade;
    private VisualElement _cardiovascularImageFade;
    private Texture2D _cardImageFadeTexture;

    private Button _backButton;
    private Label _headerTitle;

    // ===== Play Mode entry point =====
    private Button _playModeEntryButton;
    private VisualElement _playModePickerBanner;
    private Button _playModePickerCancelButton;
    private VisualElement _cardList;

    // True from the moment play-mode-entry-button is tapped until either a
    // card is chosen or Cancel is pressed. While true, the NEXT card tap
    // launches that system's Anatomy Screen already in Play Mode instead
    // of the normal Explore Mode. There is no separate picker UI - the
    // existing card list doubles as the picker (see WireAnatomySystemCards).
    private bool _playModePickingArmed;

    // ===== Sync Progress =====
    private Button _syncProgressButton;
    private Label _syncTitleLabel;
    private Label _syncStatusLabel;
    private VisualElement _syncStatusIconBox;
    private Image _syncStatusIcon;
    private Image _syncIcon;

    // Icons are loaded by name from Resources/Icons at runtime (see
    // LoadSyncIcon below) rather than baked into the UXML, so drop the
    // corresponding .png/.jpg files into Assets/Resources/Icons/ with these
    // exact names:
    //   sync_icon_cloud      - static left-hand icon
    //   sync_status_synced   - right badge, synced state
    //   sync_status_pending  - right badge, pending state
    //   sync_status_failed   - right badge, failed state
    //   sync_status_syncing  - right badge, syncing state
    //   sync_status_offline  - right badge, offline state
    private const string SyncIconResourceFolder = "Icons/";

    // USS classes swapped onto _syncStatusIconBox per sync state (see
    // .sync-status-icon-* rules in StudentExplore3d.uss). The badge color is
    // now carried by the icon image itself, but the classes are kept in case
    // the box background/border still needs to vary per state.
    private static readonly string[] SyncStatusIconClasses =
    {
        "sync-status-icon-synced",
        "sync-status-icon-pending",
        "sync-status-icon-failed",
        "sync-status-icon-syncing",
        "sync-status-icon-offline"
    };


    private void OnEnable()
    {
        Debug.Log("[StudentExplore3dController] OnEnable called");

        // Get the root from UIManager
        if (UIManager.Instance != null)
        {
            var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
            if (uiDocument != null)
            {
                _root = uiDocument.rootVisualElement;
            }
        }

        // Fallback: use this component's document
        if (_root == null)
        {
            if (_document == null) _document = GetComponent<UIDocument>();
            if (_document != null) _root = _document.rootVisualElement;
        }

        if (_root == null)
        {
            Debug.LogError("[StudentExplore3dController] Root is null!");
            return;
        }

        QueryElements();
        WireCallbacks();
        ApplyHeaderGradient();
        ApplyCardImageFades();
    }

    private void OnDisable()
    {
        if (syncService != null)
            syncService.OnStatusChanged -= HandleSyncStatusChanged;

        if (_syncProgressButton != null) _syncProgressButton.clicked -= OnSyncProgressButtonClicked;

        if (_screenRoot == null) return;

        if (_backButton != null) _backButton.clicked -= OnBackButtonClicked;
        if (_playModeEntryButton != null) _playModeEntryButton.clicked -= OnPlayModeEntryButtonClicked;
        if (_playModePickerCancelButton != null) _playModePickerCancelButton.clicked -= OnPlayModePickerCancelClicked;

        if (_headerGradientTexture != null)
        {
            Destroy(_headerGradientTexture);
            _headerGradientTexture = null;
        }

        if (_cardImageFadeTexture != null)
        {
            Destroy(_cardImageFadeTexture);
            _cardImageFadeTexture = null;
        }
    }

    private void QueryElements()
    {
        // Find the screen-root wrapper (matches the name used in the UXML)
        _screenRoot = _root.Q<VisualElement>("screen-root");

        // If no wrapper, use root directly
        if (_screenRoot == null)
        {
            Debug.LogWarning("[StudentExplore3dController] screen-root not found, using root directly");
            _screenRoot = _root;
        }

        _header = _screenRoot.Q<VisualElement>("header");

        _backButton = _screenRoot.Q<Button>("back-button");
        _headerTitle = _screenRoot.Q<Label>("header-title");

        _playModeEntryButton = _screenRoot.Q<Button>("play-mode-entry-button");
        _playModePickerBanner = _screenRoot.Q<VisualElement>("play-mode-picker-banner");
        _playModePickerCancelButton = _screenRoot.Q<Button>("play-mode-picker-cancel");
        _cardList = _screenRoot.Q<VisualElement>("card-list");

        _syncProgressButton = _screenRoot.Q<Button>("sync-progress-button");
        _syncTitleLabel = _screenRoot.Q<Label>("sync-title-label");
        _syncStatusLabel = _screenRoot.Q<Label>("sync-status-label");
        _syncStatusIconBox = _screenRoot.Q<VisualElement>("sync-status-icon-box");
        _syncStatusIcon = _screenRoot.Q<Image>("sync-status-icon");
        _syncIcon = _screenRoot.Q<Image>("sync-icon");

        // Static left-hand cloud icon never changes with sync state, so it's
        // set once here instead of in HandleSyncStatusChanged.
        if (_syncIcon != null) _syncIcon.image = LoadSyncIcon("sync_icon_cloud");

        _skeletalImageFade = _screenRoot.Q<VisualElement>("skeletal-image-fade");
        _muscularImageFade = _screenRoot.Q<VisualElement>("muscular-image-fade");
        _cardiovascularImageFade = _screenRoot.Q<VisualElement>("cardiovascular-image-fade");

        Debug.Log($"[StudentExplore3dController] Found back button: {_backButton != null}, header: {_header != null}, play mode entry button: {_playModeEntryButton != null}, sync button: {_syncProgressButton != null}");
    }

    private void WireCallbacks()
    {
        if (_backButton != null)
        {
            // Use the Button's built-in .clicked event (same reliable input path as
            // the anatomy-system cards) instead of manually registering ClickEvent.
            _backButton.clicked -= OnBackButtonClicked;
            _backButton.clicked += OnBackButtonClicked;
        }

        WireAnatomySystemCards();
        WirePlayModeEntry();
        WireSyncProgress();
    }

    // ===== Sync Progress =====
    //
    // Follows the same wire-on-every-OnEnable pattern as everything else on
    // this screen (see WirePlayModeEntry) - ShowScreen clones a fresh UI
    // tree every visit, so this always runs against this visit's real
    // sync-progress-button/sync-status-label, never a stale one.
    private void WireSyncProgress()
    {
        if (_syncProgressButton != null)
        {
            _syncProgressButton.clicked -= OnSyncProgressButtonClicked;
            _syncProgressButton.clicked += OnSyncProgressButtonClicked;
        }

        if (syncService == null)
        {
            // No sync service assigned - leave the button/label exactly as
            // authored in the UXML rather than guessing at a state. This is
            // the #1 cause of "the sync button never updates" - it fails
            // silently otherwise, so make it loud instead.
            Debug.LogWarning("[StudentExplore3dController] Sync Service is not assigned in the Inspector - " +
                              "the sync button/label will stay static and never update. " +
                              "Assign the AnatomyPlayModeSyncService from the persistent Bootstrap GameObject.");
            return;
        }

        Debug.Log($"[StudentExplore3dController] Wiring sync UI to service instance {syncService.GetInstanceID()} " +
                  $"(singleton Instance is {(AnatomyPlayModeSyncService.Instance != null ? AnatomyPlayModeSyncService.Instance.GetInstanceID().ToString() : "null")})" +
                  (AnatomyPlayModeSyncService.Instance != null && syncService.GetInstanceID() != AnatomyPlayModeSyncService.Instance.GetInstanceID()
                      ? " - MISMATCH: this is not the live singleton, events from the real sync service will never reach this UI!"
                      : ""));

        syncService.OnStatusChanged -= HandleSyncStatusChanged;
        syncService.OnStatusChanged += HandleSyncStatusChanged;

        // Auto-sync trigger: "Student Explore 3D opens" (plan section 8).
        // Paint whatever's already known immediately (no network wait), then
        // kick off a real full sync (upload pending + download/merge) in
        // the background - HandleSyncStatusChanged repaints again the
        // moment that finishes.
        syncService.RefreshStatus();
        syncService.RequestFullSync();
    }

    private void OnSyncProgressButtonClicked()
    {
        Debug.Log("[StudentExplore3dController] Sync Progress button clicked " +
                   $"(syncService assigned: {syncService != null}, online: {(syncService != null ? syncService.IsOnline.ToString() : "n/a")})");

        if (syncService == null) return;

        if (!syncService.IsOnline)
        {
            // Never lose or delete local data for pressing this while
            // offline - just tell the student why nothing happened.
            SetSyncTitleText("No internet connection");
            SetSyncStatusText("Your progress is saved locally and will sync when you're online.");
            SetSyncStatusIcon("sync_status_offline", "sync-status-icon-offline");
            return;
        }

        syncService.RequestFullSync();
    }

    private void HandleSyncStatusChanged(PlayModeSyncState state, int pendingCount)
    {
        Debug.Log($"[StudentExplore3dController] Sync status changed -> {state} (pending: {pendingCount})");

        switch (state)
        {
            case PlayModeSyncState.Syncing:
                SetSyncTitleText("Syncing...");
                SetSyncStatusText("Uploading your progress");
                SetSyncStatusIcon("sync_status_syncing", "sync-status-icon-syncing");
                SetSyncProgressButtonEnabled(false);
                break;

            case PlayModeSyncState.Offline:
                SetSyncTitleText("Sync Progress");
                SetSyncStatusText("Offline - progress is saved on this device");
                SetSyncStatusIcon("sync_status_offline", "sync-status-icon-offline");
                SetSyncProgressButtonEnabled(true);
                break;

            case PlayModeSyncState.Pending:
                SetSyncTitleText("Sync Progress");
                SetSyncStatusText(
                    pendingCount == 1 ? "1 answer waiting to sync" : $"{pendingCount} answers waiting to sync");
                SetSyncStatusIcon("sync_status_pending", "sync-status-icon-pending");
                SetSyncProgressButtonEnabled(true);
                break;

            case PlayModeSyncState.Failed:
                SetSyncTitleText("Sync failed");
                SetSyncStatusText("Tap to retry");
                SetSyncStatusIcon("sync_status_failed", "sync-status-icon-failed");
                SetSyncProgressButtonEnabled(true);
                break;

            case PlayModeSyncState.Synced:
                SetSyncTitleText("Sync Progress");
                SetSyncStatusText(FormatLastSyncedText());
                SetSyncStatusIcon("sync_status_synced", "sync-status-icon-synced");
                SetSyncProgressButtonEnabled(true);
                break;

            case PlayModeSyncState.Idle:
            default:
                // Nothing evaluated yet - before RefreshStatus/RequestFullSync
                // publish anything real. Don't claim synced/up-to-date state
                // we don't actually know yet.
                SetSyncTitleText("Sync Progress");
                SetSyncStatusText("Checking sync status...");
                SetSyncStatusIcon("sync_status_syncing", "sync-status-icon-syncing");
                SetSyncProgressButtonEnabled(false);
                break;
        }
    }

    // Builds an accurate "Last synced: X ago" string from
    // syncService.LastSuccessfulSyncUtc - which is only ever set the moment
    // a real upload+download actually completed successfully (see
    // AnatomyPlayModeSyncService.MarkSyncSucceeded), never inferred just
    // because nothing happens to be pending right now. Falls back to "Up to
    // date" if this device has no recorded successful sync yet (e.g. a
    // fresh install where PendingCount is 0 because nothing's been played).
    private string FormatLastSyncedText()
    {
        if (syncService == null || syncService.LastSuccessfulSyncUtc == null)
            return "Up to date";

        var elapsed = DateTime.UtcNow - syncService.LastSuccessfulSyncUtc.Value;

        if (elapsed < TimeSpan.FromSeconds(45)) return "Last synced: Just now";
        if (elapsed < TimeSpan.FromMinutes(2)) return "Last synced: 1 minute ago";
        if (elapsed < TimeSpan.FromHours(1)) return $"Last synced: {(int)elapsed.TotalMinutes} minutes ago";
        if (elapsed < TimeSpan.FromHours(2)) return "Last synced: 1 hour ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"Last synced: {(int)elapsed.TotalHours} hours ago";
        if (elapsed < TimeSpan.FromDays(2)) return "Last synced: yesterday";
        if (elapsed < TimeSpan.FromDays(30)) return $"Last synced: {(int)elapsed.TotalDays} days ago";

        return $"Last synced: {syncService.LastSuccessfulSyncUtc.Value.ToLocalTime():MMM d}";
    }

    private void SetSyncTitleText(string text)
    {
        if (_syncTitleLabel != null) _syncTitleLabel.text = text;
    }

    private void SetSyncStatusText(string text)
    {
        if (_syncStatusLabel != null) _syncStatusLabel.text = text;
    }

    // Swaps the image shown in the right-hand circular badge (loaded from
    // Resources/Icons/<iconResourceName>) and its state class (one of
    // SyncStatusIconClasses) to match the current sync state.
    private void SetSyncStatusIcon(string iconResourceName, string activeClass)
    {
        if (_syncStatusIcon != null) _syncStatusIcon.image = LoadSyncIcon(iconResourceName);

        if (_syncStatusIconBox == null) return;

        foreach (var cls in SyncStatusIconClasses)
        {
            if (cls == activeClass) _syncStatusIconBox.AddToClassList(cls);
            else _syncStatusIconBox.RemoveFromClassList(cls);
        }
    }

    // Loads a sync icon by name from Assets/Resources/Icons/<name>.
    // Logs a warning (instead of throwing) if the file hasn't been dropped
    // in yet, so a missing icon just shows blank rather than crashing.
    private Texture2D LoadSyncIcon(string name)
    {
        var texture = Resources.Load<Texture2D>(SyncIconResourceFolder + name);
        if (texture == null)
        {
            Debug.LogWarning($"[StudentExplore3dController] Missing sync icon 'Assets/Resources/Icons/{name}' " +
                              "- place an image with this exact name in that folder.");
        }
        return texture;
    }

    private void SetSyncProgressButtonEnabled(bool enabled)
    {
        if (_syncProgressButton != null) _syncProgressButton.SetEnabled(enabled);
    }

    // Play Mode's entry point lives here (Student Explore 3D), not on the
    // Anatomy Screen toolbar - there's no loaded 3D model/selection system
    // to play against until a system has been chosen. Tapping this button
    // just arms "picking" mode: the banner appears and the next card tap
    // (handled in WireAnatomySystemCards) launches
    // UIManager.ShowStudentAnatomyScreen(system, startInPlayMode: true)
    // instead of the normal Explore Mode call. There's no separate picker
    // popup - the card list already lists the three systems, so it doubles
    // as the picker rather than duplicating it.
    private void WirePlayModeEntry()
    {
        if (_playModeEntryButton != null)
        {
            _playModeEntryButton.clicked -= OnPlayModeEntryButtonClicked;
            _playModeEntryButton.clicked += OnPlayModeEntryButtonClicked;
        }

        if (_playModePickerCancelButton != null)
        {
            _playModePickerCancelButton.clicked -= OnPlayModePickerCancelClicked;
            _playModePickerCancelButton.clicked += OnPlayModePickerCancelClicked;
        }

        // Always start disarmed - ShowScreen clones a fresh UI tree each
        // time this screen opens, so nothing carries over from a previous
        // visit, but make sure the visuals match that (banner hidden,
        // entry button not in its "active" state).
        SetPlayModePickingArmed(false);
    }

    private void OnPlayModeEntryButtonClicked()
    {
        // Tapping the button again while armed cancels, same as Cancel.
        SetPlayModePickingArmed(!_playModePickingArmed);
    }

    private void OnPlayModePickerCancelClicked()
    {
        SetPlayModePickingArmed(false);
    }

    private void SetPlayModePickingArmed(bool armed)
    {
        _playModePickingArmed = armed;

        if (_playModePickerBanner != null)
        {
            if (armed) _playModePickerBanner.RemoveFromClassList("hidden");
            else _playModePickerBanner.AddToClassList("hidden");
        }

        if (_playModeEntryButton != null)
        {
            if (armed)
            {
                _playModeEntryButton.AddToClassList("play-mode-entry-button-active");
                _playModeEntryButton.text = "✕ Cancel";
            }
            else
            {
                _playModeEntryButton.RemoveFromClassList("play-mode-entry-button-active");
                _playModeEntryButton.text = "▶ Play";
            }
        }

        // Ring every card so it's obvious tapping one right now starts a
        // game instead of opening Explore Mode.
        if (_cardList != null)
        {
            var cards = _cardList.Query<Button>(className: "card").ToList();
            foreach (var card in cards)
            {
                if (armed) card.AddToClassList("card-play-armed");
                else card.RemoveFromClassList("card-play-armed");
            }
        }
    }

    // Connects each existing Card List card (see StudentExplore3d.uxml -
    // "card-list") to the matching AnatomySystem, so tapping it opens the
    // one reusable Anatomy Screen loaded with that system's model/database
    // (see UIManager.ShowStudentAnatomyScreen(AnatomySystem) /
    // AnatomyScreenController.ResolveAnatomySystem). Cards aren't
    // individually named in the UXML, so each is matched by the same
    // icon-skeletal/icon-muscular/icon-cardio class its icon-box already
    // carries (see StudentExplore3d.uss) rather than by list position -
    // reordering the cards in the UXML can't silently mis-wire them. Safe
    // to call every OnEnable: ShowScreen clones a brand-new UI tree each
    // time the screen opens, so there are never stale elements left with a
    // callback registered twice.
    //
    // Each card also doubles as the Play Mode picker: if
    // _playModePickingArmed is true when a card is tapped, that tap opens
    // the system in Play Mode instead of Explore Mode, and disarms picking
    // mode again either way.
    private void WireAnatomySystemCards()
    {
        if (_screenRoot == null)
        {
            Debug.LogError("[AnatomyNav] WireAnatomySystemCards: _screenRoot is NULL, cannot wire cards.");
            return;
        }


        var cards = _screenRoot.Query<Button>(className: "card").ToList();




        foreach (var card in cards)
        {
            AnatomySystem system;
            if (card.Q<VisualElement>(className: "icon-skeletal") != null)
                system = AnatomySystem.Skeletal;
            else if (card.Q<VisualElement>(className: "icon-muscular") != null)
                system = AnatomySystem.Muscular;
            else if (card.Q<VisualElement>(className: "icon-cardio") != null)
                system = AnatomySystem.Cardiovascular;
            else
            {
                Debug.LogWarning($"[AnatomyNav] Card '{card.name}' matched .card but has no known icon class - skipping.");
                continue; // not one of the three anatomy-system cards (e.g. none - skip)
            }

            card.clicked += () =>
            {
                bool playMode = _playModePickingArmed;
                SetPlayModePickingArmed(false);

                try
                {
                    UIManager.Instance.ShowStudentAnatomyScreen(system, startInPlayMode: playMode);
                    Debug.Log($"[AnatomyNav] ShowStudentAnatomyScreen({system}, startInPlayMode: {playMode}) called successfully.");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[AnatomyNav] EXCEPTION in ShowStudentAnatomyScreen({system}): {e}");

                }
            };
        }
    }

    private void OnBackButtonClicked()
    {

        UIManager.Instance.ShowStudentDashboard();
    }

    private void ApplyHeaderGradient()
    {
        if (_header == null) return;

        if (_headerGradientTexture != null)
        {
            Destroy(_headerGradientTexture);
        }

        int size = Mathf.Max(2, gradientTextureResolution);
        int height = diagonalGradient ? size : 1;

        _headerGradientTexture = new Texture2D(size, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "HeaderGradientTexture"
        };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float t = diagonalGradient
                    ? (x + y) / (float)(size - 1 + height - 1)
                    : x / (float)(size - 1);
                _headerGradientTexture.SetPixel(x, y, Color.Lerp(gradientStart, gradientEnd, t));
            }
        }
        _headerGradientTexture.Apply();

        _header.style.backgroundColor = new StyleColor(StyleKeyword.Null);
        _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
        _header.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(Length.Percent(100), Length.Percent(100)));
        _header.style.backgroundRepeat = new StyleBackgroundRepeat(new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat));
    }

    // Builds the left-edge white->transparent horizontal gradient used by
    // .card-image-fade (see StudentExplore3d.uss) and applies it to all
    // three card fades. USS alone can't express a linear-gradient(), so this
    // generates a small 1D texture once - opaque white at x=0, alpha fading
    // to 0 at the right edge - and stretches it across each fade element.
    //
    // Previously this method didn't exist at all, so the fades never got a
    // background-image: only their USS background-color (opaque white)
    // painted, covering the body-system photo underneath completely. That
    // background-color has to stay fully transparent in USS now, since a
    // fade element's background-color and background-image are composited
    // together onto its own box before that box is drawn over whatever is
    // behind it - an opaque color there would hide the photo regardless of
    // what this gradient does.
    private void ApplyCardImageFades()
    {
        if (_cardImageFadeTexture == null)
        {
            int size = Mathf.Max(2, gradientTextureResolution);
            _cardImageFadeTexture = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "CardImageFadeTexture"
            };

            for (int x = 0; x < size; x++)
            {
                float t = x / (float)(size - 1);
                float alpha = 1f - t; // opaque white at left, transparent at right
                _cardImageFadeTexture.SetPixel(x, 0, new Color(1f, 1f, 1f, alpha));
            }
            _cardImageFadeTexture.Apply();
        }

        ApplyCardImageFade(_skeletalImageFade);
        ApplyCardImageFade(_muscularImageFade);
        ApplyCardImageFade(_cardiovascularImageFade);
    }

    private void ApplyCardImageFade(VisualElement fade)
    {
        if (fade == null || _cardImageFadeTexture == null) return;

        fade.style.backgroundColor = new StyleColor(Color.clear);
        fade.style.backgroundImage = new StyleBackground(_cardImageFadeTexture);
        fade.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(Length.Percent(100), Length.Percent(100)));
        fade.style.backgroundRepeat = new StyleBackgroundRepeat(new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat));
    }
}