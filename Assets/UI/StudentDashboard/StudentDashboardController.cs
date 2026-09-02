using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class StudentDashboardController : MonoBehaviour
    {
        [Header("Gradient colors (matches the login screen)")]
        [SerializeField] private Color gradientStart = new Color(0.557f, 0.176f, 0.886f);
        [SerializeField] private Color gradientEnd = new Color(0.878f, 0.129f, 0.541f);

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private VisualElement _header;
        private VisualElement _joinclassroomcard;

        private Button _profile;
        private Label _profileInitialsLabel;
        private Button _notificationButton;
        private Label _studentNameLabel;

        // Cloudinary avatar state for #profile-button - same pattern as
        // StudentProfileController's #avatar: show the photo if AvatarUrl is
        // set, otherwise fall back to initials. Cached so repeated
        // PopulateDashboard() calls (OnEnable, OnStudentProfileChanged) don't
        // re-download the same image.
        private string _loadedAvatarUrl;
        private Texture2D _avatarTexture;
        private Coroutine _avatarLoadRoutine;

        private Label _currentLevelLabel;
        private Label _nextLevelLabel;
        private VisualElement _progressFill;
        private Label _pointsToNextLabel;

        private Label _quizzesValueLabel;
        private Label _levelValueLabel;
        private Label _pointsValueLabel;

        private Button _explore3DButton;
        private Button _classroomHubButton;
        private Button _badgesButton;
        private Button _progressButton;
        private Button _joinClassroomButton;

        private VisualElement _recentActivityList;
        private Button _recentActivityViewAllButton;

        private VisualElement _recentActivityViewAllOverlay;
        private Button _recentActivityViewAllCloseButton;
        private VisualElement _recentActivityViewAllList;
        private VisualElement _recentActivityViewAllNoResults;

        // Tracked so it can be destroyed before building a new one and on
        // OnDisable, the same pattern StudentProgressController's
        // _headerGradientTexture already uses - otherwise every OnEnable
        // (this screen is re-opened constantly via the Back button) leaks
        // another 64x1 Texture2D that never gets freed.
        private Texture2D _gradientTexture;

        // ---------------- Recent Activity cache ----------------
        // Recent Activity used to be re-fetched from Firestore (2 reads) and fully
        // rebuilt (Clear() + re-Instantiate every row) on every single OnEnable, even
        // when nothing had changed since the last time this screen was open. Now:
        //  - _cachedActivity holds the last-known-good merged list, so a re-open just
        //    repaints from memory (RenderRecentActivity is now a cheap diff - see below).
        //  - _activityDirty tracks whether anything that could change this feed has
        //    happened since the last fetch (a quiz submitted, or a classroom joined).
        //    A plain re-open of the dashboard (e.g. Back button from Progress) does NOT
        //    set this, so it does NOT re-hit the network.
        private bool _activityLoaded;
        private bool _activityDirty = true;
        private readonly List<ActivityEntry> _cachedActivity = new List<ActivityEntry>();
        private readonly Dictionary<string, ActivityRowRefs> _activityRowsByKey = new Dictionary<string, ActivityRowRefs>();

        /// <summary>Whoever _cachedActivity (and the rows currently on screen) belong to.
        /// Compared by reference against PlayerSessionManager.CurrentStudent on every
        /// OnEnable/profile-change so a different student signing in on the same device
        /// - without this GameObject being destroyed in between - can never paint from
        /// the previous student's cached Recent Activity. See
        /// InvalidateActivityCacheIfStudentChanged().</summary>
        private PlayerSessionManager.StudentProfile _cachedActivityStudent;

        private void OnEnable()
        {
            Debug.Log("[StudentDashboardController] OnEnable called");

            if (_document == null)
            {
                _document = GetComponent<UIDocument>();
            }

            // Get the root from UIManager's document
            if (UIManager.Instance != null)
            {
                var uiDocument = UIManager.Instance.GetComponent<UIDocument>();
                if (uiDocument != null)
                {
                    _root = uiDocument.rootVisualElement;
                }
            }

            // Fallback: use this component's document
            if (_root == null && _document != null)
            {
                _root = _document.rootVisualElement;
            }

            if (_root == null)
            {
                Debug.LogError("[StudentDashboardController] Root is null!");
                return;
            }

            // Unregister old callbacks first
            UnregisterCallbacks();

            QueryElements();
            ApplyGradients();
            WireCallbacks();
            UpdateResponsiveLayout();
            PopulateDashboard();

            // Must run before the cache-paint below: if a different student signed in
            // since _cachedActivity was last populated, this clears it out so we never
            // instantly repaint someone else's Recent Activity.
            InvalidateActivityCacheIfStudentChanged();

            // Repaint instantly from whatever we already have cached (no blank flash,
            // no GameObject churn), then only hit the network if something that could
            // affect this feed has actually happened since the last fetch.
            if (_activityLoaded)
            {
                RenderRecentActivity(_cachedActivity);
            }

            if (!_activityLoaded || _activityDirty)
            {
                PopulateRecentActivity();
            }

            // Stay subscribed while this screen is visible so a points/level/badge
            // change (optimistic, from another device, or a teacher edit) repaints
            // the header stats without needing this screen to be closed and reopened.
            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
                PlayerSessionManager.Instance.OnStudentProfileChanged += OnStudentProfileChanged;
            }
        }

        private void OnDisable()
        {
            UnregisterCallbacks();

            if (PlayerSessionManager.Instance != null)
            {
                PlayerSessionManager.Instance.OnStudentProfileChanged -= OnStudentProfileChanged;
            }

            // Clean up gradient texture
            if (_gradientTexture != null)
            {
                Destroy(_gradientTexture);
                _gradientTexture = null;
            }

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
                _avatarLoadRoutine = null;
            }

            if (_avatarTexture != null)
            {
                Destroy(_avatarTexture);
                _avatarTexture = null;
            }
        }

        private void OnStudentProfileChanged(PlayerSessionManager.StudentProfile student)
        {
            // PopulateDashboard() already reads straight from
            // PlayerSessionManager.CurrentStudent (which is what just changed) and
            // only touches label text / a fill width - no GameObject churn.
            PopulateDashboard();

            // Usually this event means the same student just submitted a quiz, but if it
            // ever fires because a different student signed in instead, this makes sure
            // the stale cache/rows get thrown out rather than mixed with the new student's
            // data.
            InvalidateActivityCacheIfStudentChanged();

            // A profile change here always means a quiz was just submitted (points/
            // quizzesCompleted/badgesEarned only move that way), which is exactly a
            // new Recent Activity row - refresh that feed too instead of waiting for
            // this screen to be closed and reopened.
            MarkActivityDirty();
            PopulateRecentActivity();
        }

        /// <summary>Call whenever something outside this screen could change the Recent
        /// Activity feed (currently: StudentClassroomController after a successful
        /// classroom join). Just flips a flag - the actual re-fetch happens next time
        /// this screen becomes active, same as StudentClassroomHubController's classroom
        /// listener already does its own live refresh instead of polling.</summary>
        public void MarkActivityDirty()
        {
            _activityDirty = true;
        }

        /// <summary>Clears _cachedActivity, the rows built from it, and the View All
        /// overlay whenever PlayerSessionManager.CurrentStudent no longer matches whoever
        /// the cache currently belongs to (_cachedActivityStudent) - e.g. Student A signs
        /// out and Student B signs in on the same device while this GameObject stays
        /// alive. Reference equality is enough here: PlayerSessionManager mutates the
        /// same StudentProfile instance in place for point/level changes on the same
        /// student (see ApplyQuizAttemptResult), so the reference only ever changes when
        /// the signed-in student actually changes. Safe/cheap to call on every OnEnable
        /// and profile-change - it's a no-op reference compare for the common case where
        /// the student hasn't changed.</summary>
        private void InvalidateActivityCacheIfStudentChanged()
        {
            var currentStudent = PlayerSessionManager.Instance?.CurrentStudent;
            if (ReferenceEquals(currentStudent, _cachedActivityStudent)) return;

            _cachedActivityStudent = currentStudent;
            _cachedActivity.Clear();
            _activityLoaded = false;
            _activityDirty = true;

            foreach (var refs in _activityRowsByKey.Values) refs.Item.RemoveFromHierarchy();
            _activityRowsByKey.Clear();
            _recentActivityList?.Clear();

            _recentActivityViewAllList?.Clear();
            _recentActivityViewAllButton?.AddToClassList("hidden");
            CloseViewAllActivityOverlay();
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _profile?.UnregisterCallback<ClickEvent>(OnProfileClicked);
            _notificationButton?.UnregisterCallback<ClickEvent>(OnNotificationClicked);
            _explore3DButton?.UnregisterCallback<ClickEvent>(OnExplore3DClicked);
            _classroomHubButton?.UnregisterCallback<ClickEvent>(OnClassroomHubClicked);
            _badgesButton?.UnregisterCallback<ClickEvent>(OnBadgesClicked);
            _progressButton?.UnregisterCallback<ClickEvent>(OnProgressClicked);
            _joinClassroomButton?.UnregisterCallback<ClickEvent>(OnJoinClassroomClicked);
            _recentActivityViewAllButton?.UnregisterCallback<ClickEvent>(OnViewAllActivityClicked);
            _recentActivityViewAllCloseButton?.UnregisterCallback<ClickEvent>(OnCloseViewAllActivityClicked);
            _recentActivityViewAllOverlay?.UnregisterCallback<ClickEvent>(OnViewAllActivityOverlayBackdropClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {

            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentDashboardController] screen-root not found, using root directly");
                _screenRoot = _root;
            }
            else
            {
                Debug.Log("[StudentDashboardController] Found screen-root wrapper");
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _joinclassroomcard = _screenRoot.Q<VisualElement>("join-classroom-icon-box");

            _profile = _screenRoot.Q<Button>("profile-button");
            _profileInitialsLabel = _screenRoot.Q<Label>("profile-initials-label");
            _notificationButton = _screenRoot.Q<Button>("notification-button");
            _studentNameLabel = _screenRoot.Q<Label>("student-name-label");

            _currentLevelLabel = _screenRoot.Q<Label>("current-level-label");
            _nextLevelLabel = _screenRoot.Q<Label>("next-level-label");
            _progressFill = _screenRoot.Q<VisualElement>("progress-fill");
            _pointsToNextLabel = _screenRoot.Q<Label>("points-to-next-label");

            _quizzesValueLabel = _screenRoot.Q<Label>("quizzes-value-label");
            _levelValueLabel = _screenRoot.Q<Label>("level-value-label");
            _pointsValueLabel = _screenRoot.Q<Label>("points-value-label");

            _explore3DButton = _screenRoot.Q<Button>("explore-3d-button");
            _classroomHubButton = _screenRoot.Q<Button>("classroom-hub-button");
            _badgesButton = _screenRoot.Q<Button>("badges-button");
            _progressButton = _screenRoot.Q<Button>("progress-button");
            _joinClassroomButton = _screenRoot.Q<Button>("join-classroom-button");

            _recentActivityList = _screenRoot.Q<VisualElement>("recent-activity-list");
            _recentActivityViewAllButton = _screenRoot.Q<Button>("recent-activity-view-all-button");

            _recentActivityViewAllOverlay = _screenRoot.Q<VisualElement>("recent-activity-view-all-overlay");
            _recentActivityViewAllCloseButton = _screenRoot.Q<Button>("recent-activity-view-all-close-button");
            _recentActivityViewAllList = _screenRoot.Q<VisualElement>("recent-activity-view-all-list");
            _recentActivityViewAllNoResults = _screenRoot.Q<VisualElement>("recent-activity-view-all-no-results");

            if (_joinclassroomcard == null || _joinClassroomButton == null)
            {
                Debug.LogWarning("[StudentDashboardController] 'join-classroom-icon-box'/'join-classroom-button' " +
                    "not found in UXML - the join-classroom gradient will be skipped for whichever is missing.");
            }

            Debug.Log($"[StudentDashboardController] Found Explore3D: {_explore3DButton != null}, Header: {_header != null}");
        }

        private void WireCallbacks()
        {
            if (_profile != null) _profile.RegisterCallback<ClickEvent>(OnProfileClicked);
            if (_notificationButton != null) _notificationButton.RegisterCallback<ClickEvent>(OnNotificationClicked);
            if (_explore3DButton != null) _explore3DButton.RegisterCallback<ClickEvent>(OnExplore3DClicked);
            if (_classroomHubButton != null) _classroomHubButton.RegisterCallback<ClickEvent>(OnClassroomHubClicked);
            if (_badgesButton != null) _badgesButton.RegisterCallback<ClickEvent>(OnBadgesClicked);
            if (_progressButton != null) _progressButton.RegisterCallback<ClickEvent>(OnProgressClicked);
            if (_joinClassroomButton != null) _joinClassroomButton.RegisterCallback<ClickEvent>(OnJoinClassroomClicked);
            _recentActivityViewAllButton?.RegisterCallback<ClickEvent>(OnViewAllActivityClicked);
            _recentActivityViewAllCloseButton?.RegisterCallback<ClickEvent>(OnCloseViewAllActivityClicked);
            _recentActivityViewAllOverlay?.RegisterCallback<ClickEvent>(OnViewAllActivityOverlayBackdropClicked);

            // Re-evaluate the compact layout whenever the panel is resized
            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Data loading ----------------

        /// <summary>Pulls the signed-in student's profile from PlayerSessionManager's
        /// cache and the level thresholds from AdminGamificationService, then feeds
        /// SetStudentData(). Call whenever the dashboard is shown. No longer forces a
        /// students/{uid} re-fetch on every open - PlayerSessionManager.CurrentStudent
        /// is kept current as soon as anything actually changes it (see
        /// PlayerSessionManager.ApplyQuizAttemptResult, called right after a quiz
        /// attempt is submitted), so the cached copy is already accurate here.</summary>
        private void PopulateDashboard()
        {
            var student = PlayerSessionManager.Instance?.CurrentStudent;
            if (student == null)
            {
                Debug.LogWarning("[StudentDashboardController] No signed-in student found - " +
                    "showing the dashboard with placeholder data. Was this screen opened without " +
                    "going through login/create-account first?");
                return;
            }

            //bool offline = Application.internetReachability == NetworkReachability.NotReachable;

            //if (AdminGamificationService.Instance == null || offline)
            //{
            //    // Same fallback that already exists for "service not ready" -
            //    // now also used offline, so cached Level/TotalPoints/QuizzesCompleted
            //    // paint immediately instead of waiting on a Firestore call that
            //    // may never resolve with no connection.
            //    SetStudentData(student.FullName, student.Level, student.Level, 0f, 0,
            //                   student.QuizzesCompleted, student.TotalPoints);
            //    return;
            //}


            if (AdminGamificationService.Instance == null)
            {
                Debug.LogWarning("[StudentDashboardController] AdminGamificationService.Instance is null - " +
                    "falling back to raw points/quiz stats without level-progress math.");
                SetStudentData(student.FullName, student.Level, student.Level, 0f, 0, student.QuizzesCompleted, student.TotalPoints);
                ApplyAvatar(student.FullName, student.AvatarUrl);
                return;
            }

            AdminGamificationService.Instance.FetchSettings(settings =>
            {
                var progress = AdminGamificationService.ComputeLevelProgress(settings, student.TotalPoints);

                SetStudentData(
                    studentName: student.FullName,
                    currentLevel: progress.level,
                    nextLevel: progress.nextLevel,
                    levelProgress01: progress.progress01,
                    pointsToNextLevel: progress.pointsToNext,
                    quizzesCompleted: student.QuizzesCompleted,
                    totalPoints: student.TotalPoints
                );

                ApplyAvatar(student.FullName, student.AvatarUrl);
            });
        }

        public void SetStudentData(
            string studentName,
            int currentLevel,
            int nextLevel,
            float levelProgress01,
            int pointsToNextLevel,
            int quizzesCompleted,
            int totalPoints)
        {
            // AdminGamificationService.ComputeLevelProgress returns nextLevel == currentLevel
            // (and pointsToNextLevel == 0) when the student has no level above their current
            // one - that's the signal a max level has been reached, rather than a separate flag.
            bool maxLevelReached = nextLevel <= currentLevel;

            if (_studentNameLabel != null) _studentNameLabel.text = studentName;
            if (_currentLevelLabel != null) _currentLevelLabel.text = $"Level {currentLevel}";

            if (_nextLevelLabel != null)
            {
                _nextLevelLabel.EnableInClassList("hidden", maxLevelReached);
                if (!maxLevelReached) _nextLevelLabel.text = $"Level {currentLevel + 1}";
            }

            if (_progressFill != null) _progressFill.style.width = new Length(Mathf.Clamp01(levelProgress01) * 100f, LengthUnit.Percent);

            if (_pointsToNextLabel != null)
            {
                _pointsToNextLabel.text = maxLevelReached
                    ? "Maximum level reached!"
                    : $"{pointsToNextLevel} points to next level";
            }

            if (_quizzesValueLabel != null) _quizzesValueLabel.text = quizzesCompleted.ToString();
            if (_levelValueLabel != null) _levelValueLabel.text = currentLevel.ToString();
            if (_pointsValueLabel != null) _pointsValueLabel.text = totalPoints.ToString();
        }

        // ---------------- Avatar (Cloudinary) ----------------
        // Same approach as StudentProfileController's #avatar: show the
        // student's Cloudinary photo if AvatarUrl is set, otherwise fall back
        // to initials. Skips re-downloading when avatarUrl hasn't changed
        // since the last successful load (PopulateDashboard runs on every
        // OnEnable and OnStudentProfileChanged).

        private void ApplyAvatar(string studentName, string avatarUrl)
        {
            if (_profile == null) return;

            if (_profileInitialsLabel != null)
                _profileInitialsLabel.text = GetInitials(studentName);

            if (string.IsNullOrEmpty(avatarUrl))
            {
                ShowInitialsAvatar();
                return;
            }

            if (avatarUrl == _loadedAvatarUrl && _avatarTexture != null)
            {
                // Already showing this exact image - nothing to do.
                return;
            }

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
            }
            _avatarLoadRoutine = StartCoroutine(LoadAvatarImage(avatarUrl));
        }

        private void ShowInitialsAvatar()
        {
            _profile.style.backgroundImage = StyleKeyword.Null;
            if (_profileInitialsLabel != null)
                _profileInitialsLabel.style.display = DisplayStyle.Flex;

            if (_avatarLoadRoutine != null)
            {
                StopCoroutine(_avatarLoadRoutine);
                _avatarLoadRoutine = null;
            }

            if (_avatarTexture != null)
            {
                Destroy(_avatarTexture);
                _avatarTexture = null;
            }
            _loadedAvatarUrl = null;
        }

        private IEnumerator LoadAvatarImage(string avatarUrl)
        {
            using (var request = UnityWebRequestTexture.GetTexture(avatarUrl))
            {
                yield return request.SendWebRequest();

                _avatarLoadRoutine = null;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[StudentDashboardController] Could not load Cloudinary avatar '{avatarUrl}': {request.error}");
                    // Leave whatever's currently showing (initials, most likely)
                    // rather than blanking the avatar out over a transient network hiccup.
                    yield break;
                }

                if (_avatarTexture != null)
                {
                    Destroy(_avatarTexture);
                }

                _avatarTexture = DownloadHandlerTexture.GetContent(request);
                _loadedAvatarUrl = avatarUrl;

                if (_profile == null) yield break; // screen may have been disabled while the request was in flight.

                _profile.style.backgroundImage = new StyleBackground(_avatarTexture);
                ApplyCoverBackground(_profile);

                // Image fills the circle now - the initials fallback underneath
                // would otherwise show through any transparent corners.
                if (_profileInitialsLabel != null)
                    _profileInitialsLabel.style.display = DisplayStyle.None;
            }
        }

        // unityBackgroundScaleMode is obsolete (deprecated in favor of the CSS-style
        // background-* properties) - this is the ScaleAndCrop-equivalent combination:
        // fill the element, keep aspect ratio, crop overflow, centered.
        private static void ApplyCoverBackground(VisualElement element)
        {
            element.style.backgroundPositionX = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundPositionY = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundRepeat = new StyleBackgroundRepeat(new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat));
            element.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Cover));
        }

        private static string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "?";

            var parts = fullName.Trim().Split(' ');
            if (parts.Length == 1) return parts[0].Substring(0, Mathf.Min(2, parts[0].Length)).ToUpper();

            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        /// <summary>Pulls this student's merged quiz/badge/classroom-join activity from
        /// QuizService.FetchRecentActivity() + ClassroomService.FetchRecentJoins()
        /// (2 reads) and diff-renders the Recent Activity list. Only called from
        /// OnEnable when there's no cache yet or _activityDirty is set (see
        /// MarkActivityDirty/OnStudentProfileChanged) - a plain re-open of an
        /// already-loaded dashboard reuses _cachedActivity instead of re-fetching.</summary>
        private void PopulateRecentActivity()
        {
            if (_recentActivityList == null) return;

            if (PlayerSessionManager.Instance?.CurrentStudent == null)
            {
                // No signed-in student yet - PopulateDashboard() already warns about
                // this case, so just leave the placeholder markup from the .uxml alone.
                return;
            }

            FetchMergedActivity(recentActivityMaxItems, merged =>
            {
                if (merged.Count > recentActivityMaxItems)
                {
                    merged.RemoveRange(recentActivityMaxItems, merged.Count - recentActivityMaxItems);
                }

                _cachedActivity.Clear();
                _cachedActivity.AddRange(merged);
                _activityLoaded = true;
                _activityDirty = false;

                RenderRecentActivity(_cachedActivity);
            });
        }

        /// <summary>Fetches QuizService.FetchRecentActivity() + ClassroomService.FetchRecentJoins()
        /// (2 reads), merges them into one timeline, and hands the sorted result back via
        /// `onComplete`. Shared by the dashboard's capped preview (PopulateRecentActivity,
        /// recentActivityMaxItems) and the "View All" overlay's much larger fetch
        /// (FetchAllRecentActivity, recentActivityViewAllMaxItems) so both read from the
        /// same merge/sort logic instead of drifting apart.</summary>
        private void FetchMergedActivity(int maxItems, Action<List<ActivityEntry>> onComplete)
        {
            if (QuizService.Instance == null || ClassroomService.Instance == null)
            {
                Debug.LogWarning("[StudentDashboardController] QuizService/ClassroomService.Instance is null - cannot load Recent Activity.");
                onComplete(new List<ActivityEntry>());
                return;
            }

            var merged = new List<ActivityEntry>();
            int pending = 2;

            void OnPartComplete()
            {
                pending--;
                if (pending > 0) return;

                merged.Sort((a, b) => b.OccurredAt.CompareTo(a.OccurredAt));
                onComplete(merged);
            }

            QuizService.Instance.FetchRecentActivity(activities =>
            {
                foreach (var a in activities)
                {
                    merged.Add(new ActivityEntry
                    {
                        // DocId is only set on the newer quizAttempts/badgeAwards reads -
                        // fall back to a title+timestamp composite for anything older so
                        // this always has a stable dedup/diff key.
                        Key = !string.IsNullOrEmpty(a.DocId) ? "quiz:" + a.DocId : $"quiz:{a.Title}:{a.OccurredAt.ToDateTime().Ticks}",
                        Title = a.Title,
                        PointsDelta = a.PointsDelta,
                        OccurredAt = a.OccurredAt.ToDateTime(),
                        IconColor = GetQuizActivityColor(a.Type)
                    });
                }
                OnPartComplete();
            }, maxItems: maxItems);

            ClassroomService.Instance.FetchRecentJoins(joins =>
            {
                foreach (var j in joins)
                {
                    merged.Add(new ActivityEntry
                    {
                        Key = "join:" + j.ClassroomId,
                        Title = $"Joined '{j.ClassroomName}'",
                        PointsDelta = 0,
                        OccurredAt = j.JoinedAt.ToDateTime(),
                        IconColor = classroomJoinedColor
                    });
                }
                OnPartComplete();
            }, maxItems: maxItems);
        }

        /// <summary>Called when "View All" is opened. The dashboard's own cache
        /// (_cachedActivity) is capped to recentActivityMaxItems for the inline preview,
        /// so re-using it here would silently cut the overlay off at the same small cap.
        /// Instead this does its own fetch with a much higher cap so View All actually
        /// shows the student's full recent history.</summary>
        private void FetchAllRecentActivity()
        {
            if (PlayerSessionManager.Instance?.CurrentStudent == null) return;

            FetchMergedActivity(recentActivityViewAllMaxItems, RefreshActivityViewAllList);
        }

        /// <summary>Display-only shape a Recent Activity row is built from, after
        /// merging QuizService.ActivityRecord and ClassroomService.ClassroomJoinRecord
        /// (two different backend types that don't otherwise share a common shape).</summary>
        private struct ActivityEntry
        {
            /// <summary>Stable identity for this row, independent of its position in the
            /// list - lets RenderRecentActivity() tell "same event, unchanged" apart from
            /// "genuinely new/removed" instead of diffing by index.</summary>
            public string Key;
            public string Title;
            public int PointsDelta;
            public DateTime OccurredAt;
            public Color IconColor;
        }

        /// <summary>Element refs for one row, plus the entry last painted into it - lets
        /// ApplyActivityItemContent() skip writing a field that hasn't actually changed.</summary>
        private class ActivityRowRefs
        {
            public VisualElement Item;
            public VisualElement Icon;
            public Label TitleLabel;
            public Label TimeLabel;
            public Label PointsLabel;
            public ActivityEntry LastEntry;
            public bool HasPointsLabel;
        }

        /// <summary>Cap used for the dashboard's own inline preview fetch/cache
        /// (_cachedActivity). Kept small since only the first MaxDashboardActivityItems
        /// of it are ever shown inline anyway.</summary>
        private const int recentActivityMaxItems = 8;

        /// <summary>Cap used only for the "View All" overlay's own fetch
        /// (FetchAllRecentActivity) - deliberately much higher than
        /// recentActivityMaxItems so View All isn't silently limited to the same small
        /// window as the dashboard preview.</summary>
        private const int recentActivityViewAllMaxItems = 200;

        /// <summary>Max activity rows shown inline on the dashboard before they're
        /// truncated - "View All" (now always visible beside the section title whenever
        /// there's any activity) is what shows the rest.</summary>
        private const int MaxDashboardActivityItems = 5;
        private static readonly Color classroomJoinedColor = new Color(0.851f, 0.467f, 0.024f); // rgb(217,119,6)

        /// <summary>Diffs `entries` against the rows already on screen (keyed by
        /// ActivityEntry.Key) instead of clearing and re-Instantiate-ing everything:
        /// unchanged rows are left completely untouched, changed rows get their labels
        /// patched in place, and only genuinely new/removed events cause a GameObject to
        /// be created or destroyed. Reordering reuses Insert() on the existing element,
        /// which just moves it in the hierarchy rather than rebuilding it.</summary>
        private void RenderRecentActivity(List<ActivityEntry> entries)
        {
            if (_recentActivityList == null) return;

            bool hasAnyEntries = entries != null && entries.Count > 0;

            _recentActivityViewAllButton?.EnableInClassList("hidden", !hasAnyEntries);

            // If the feed emptied out while the overlay happened to be open, close it
            // rather than leave it showing a stale list.
            if (!hasAnyEntries) CloseViewAllActivityOverlay();

            // Only the first MaxDashboardActivityItems entries are diffed/rendered inline -
            // "View All" fetches its own, much less capped list separately (see
            // FetchAllRecentActivity), so it isn't limited to what's cached here.
            var inlineEntries = hasAnyEntries && entries.Count > MaxDashboardActivityItems
                ? entries.GetRange(0, MaxDashboardActivityItems)
                : entries;

            bool hasEntries = inlineEntries != null && inlineEntries.Count > 0;

            if (!hasEntries)
            {
                foreach (var refs in _activityRowsByKey.Values) refs.Item.RemoveFromHierarchy();
                _activityRowsByKey.Clear();

                if (_recentActivityList.Q<Label>(className: "activity-empty-label") == null)
                {
                    _recentActivityList.Clear();
                    var empty = new Label("No recent activity yet - complete a quiz to get started!");
                    empty.AddToClassList("activity-time");
                    empty.AddToClassList("activity-empty-label");
                    _recentActivityList.Add(empty);
                }

                SyncViewAllOverlayWithLatestData(entries);
                return;
            }

            // The empty-state label (if any) doesn't belong once we have real rows.
            var emptyLabel = _recentActivityList.Q<Label>(className: "activity-empty-label");
            emptyLabel?.RemoveFromHierarchy();

            var incomingKeys = new HashSet<string>();

            for (int i = 0; i < inlineEntries.Count; i++)
            {
                var entry = inlineEntries[i];
                incomingKeys.Add(entry.Key);
                bool isLast = i == inlineEntries.Count - 1;

                if (!_activityRowsByKey.TryGetValue(entry.Key, out var refs))
                {
                    refs = BuildActivityItem(entry, isLast);
                    _activityRowsByKey[entry.Key] = refs;
                }
                else
                {
                    ApplyActivityItemContent(refs, entry, isLast);
                }

                // Keep list order in sync with `inlineEntries` - Insert() on an already-parented
                // element just moves it, so unaffected rows elsewhere aren't touched.
                if (_recentActivityList.IndexOf(refs.Item) != i)
                {
                    _recentActivityList.Insert(i, refs.Item);
                }
            }

            // Drop rows for events that fell off the inline feed (aged out, or now only
            // reachable via View All since the cap dropped them).
            List<string> staleKeys = null;
            foreach (var key in _activityRowsByKey.Keys)
            {
                if (!incomingKeys.Contains(key)) (staleKeys ??= new List<string>()).Add(key);
            }
            if (staleKeys != null)
            {
                foreach (var key in staleKeys)
                {
                    _activityRowsByKey[key].Item.RemoveFromHierarchy();
                    _activityRowsByKey.Remove(key);
                }
            }

            SyncViewAllOverlayWithLatestData(entries);
        }

        /// <summary>Keeps the View All overlay's contents current whenever the dashboard's
        /// activity data changes. If the overlay is already open, it's showing a fuller,
        /// uncapped fetch (see FetchAllRecentActivity) - refresh it with another full
        /// fetch instead of overwriting it with the dashboard's small capped cache. If
        /// it's closed, just reseed it from the cache so it paints instantly the next
        /// time it's opened, before that open's own fetch resolves.</summary>
        private void SyncViewAllOverlayWithLatestData(List<ActivityEntry> fallbackEntries)
        {
            bool overlayOpen = _recentActivityViewAllOverlay != null && !_recentActivityViewAllOverlay.ClassListContains("hidden");
            if (overlayOpen)
            {
                FetchAllRecentActivity();
            }
            else
            {
                RefreshActivityViewAllList(fallbackEntries);
            }
        }

        /// <summary>Rebuilds recent-activity-view-all-list from the given activity feed.
        /// No diffing here - it's only (re)built on open, on a fresh View All fetch
        /// completing, or when the dashboard's own data changes, so a full rebuild is
        /// cheap.</summary>
        private void RefreshActivityViewAllList(List<ActivityEntry> entries)
        {
            if (_recentActivityViewAllList == null) return;

            var source = entries ?? new List<ActivityEntry>();
            bool hasResults = source.Count > 0;

            _recentActivityViewAllNoResults?.EnableInClassList("hidden", hasResults);

            _recentActivityViewAllList.Clear();
            for (int i = 0; i < source.Count; i++)
            {
                bool isLast = i == source.Count - 1;
                var refs = BuildActivityItem(source[i], isLast);
                _recentActivityViewAllList.Add(refs.Item);
            }
        }

        private ActivityRowRefs BuildActivityItem(ActivityEntry entry, bool isLast)
        {
            var item = new VisualElement();
            item.AddToClassList("activity-item");

            var icon = new VisualElement();
            icon.AddToClassList("activity-icon");
            item.Add(icon);

            var content = new VisualElement();
            content.AddToClassList("activity-content");

            var title = new Label();
            title.AddToClassList("activity-title");
            content.Add(title);

            var time = new Label();
            time.AddToClassList("activity-time");
            content.Add(time);

            item.Add(content);

            var refs = new ActivityRowRefs
            {
                Item = item,
                Icon = icon,
                TitleLabel = title,
                TimeLabel = time
            };

            ApplyActivityItemContent(refs, entry, isLast);
            return refs;
        }

        /// <summary>Patches an existing row's visuals in place, only touching fields that
        /// differ from what was last painted. Called both right after a row is first
        /// built and on every later diff pass.</summary>
        private void ApplyActivityItemContent(ActivityRowRefs refs, ActivityEntry entry, bool isLast)
        {
            var last = refs.LastEntry;
            bool isFirstPaint = string.IsNullOrEmpty(last.Key);

            if (isFirstPaint || last.Title != entry.Title) refs.TitleLabel.text = entry.Title;

            // Relative time text ("5 minutes ago") is a function of wall-clock time, not
            // just the cached timestamp, so it's always safe/cheap to refresh on a real
            // diff pass even if OccurredAt itself hasn't changed.
            refs.TimeLabel.text = FormatRelativeTime(entry.OccurredAt);

            if (isFirstPaint || last.IconColor != entry.IconColor) refs.Icon.style.backgroundColor = entry.IconColor;

            if (isFirstPaint || last.PointsDelta != entry.PointsDelta)
            {
                if (entry.PointsDelta != 0)
                {
                    if (!refs.HasPointsLabel)
                    {
                        refs.PointsLabel = new Label();
                        refs.PointsLabel.AddToClassList("activity-points");
                        refs.Item.Add(refs.PointsLabel);
                        refs.HasPointsLabel = true;
                    }
                    refs.PointsLabel.text = $"+{entry.PointsDelta}";
                }
                else if (refs.HasPointsLabel)
                {
                    refs.PointsLabel.RemoveFromHierarchy();
                    refs.PointsLabel = null;
                    refs.HasPointsLabel = false;
                }
            }

            refs.Item.EnableInClassList("activity-item-last", isLast);
            refs.LastEntry = entry;
        }

        private static Color GetQuizActivityColor(QuizService.ActivityType type)
        {
            switch (type)
            {
                case QuizService.ActivityType.BadgeEarned: return new Color(0.576f, 0.2f, 0.918f);   // rgb(147,51,234) - matches the mockup's badge-earned dot
                case QuizService.ActivityType.QuizCompleted:
                default: return new Color(0.145f, 0.388f, 0.922f);                                    // rgb(37,99,235) - matches the mockup's quiz-completed dot
            }
        }

        /// <summary>Timestamp.ToDateTime() returns UTC - compare against UtcNow, not Now.</summary>
        private static string FormatRelativeTime(DateTime occurredAtUtc)
        {
            var span = DateTime.UtcNow - occurredAtUtc;

            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")} ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
            if (span.TotalDays < 2) return "Yesterday";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
            return occurredAtUtc.ToLocalTime().ToString("MMM d");
        }

        // ---------------- Button handlers ----------------

        private void OnProfileClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentProfile();
        }

        private void OnNotificationClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentNotifications(UIManager.Instance.ShowStudentDashboard);
        }

        private void OnExplore3DClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentExplore3d();
        }

        private void OnClassroomHubClicked(ClickEvent evt)
        {
            
            UIManager.Instance.ShowStudentClassroomHub();
        }

        private void OnBadgesClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentAchievements();
        }

        private void OnProgressClicked(ClickEvent evt)
        {
            UIManager.Instance.ShowStudentProgress();
        }

        private void OnJoinClassroomClicked(ClickEvent evt)
        {

            UIManager.Instance.ShowJoinClassroom();

        }

        private void OnViewAllActivityClicked(ClickEvent evt)
        {
            // Paint instantly with whatever's already cached (usually the same rows as
            // the dashboard preview) so the overlay never opens blank, then replace it
            // with a fresh, much less capped fetch so View All shows the student's full
            // recent history rather than being limited to the dashboard's small cache.
            RefreshActivityViewAllList(_cachedActivity);
            _recentActivityViewAllOverlay?.RemoveFromClassList("hidden");
            FetchAllRecentActivity();
        }

        private void OnCloseViewAllActivityClicked(ClickEvent evt)
        {
            CloseViewAllActivityOverlay();
        }

        /// <summary>Tapping the dimmed backdrop closes the overlay, same as the close
        /// button - but only when the tap actually landed on the backdrop itself, not on
        /// the card or anything inside it (ClickEvent bubbles up from children).</summary>
        private void OnViewAllActivityOverlayBackdropClicked(ClickEvent evt)
        {
            if (evt.target == _recentActivityViewAllOverlay)
            {
                CloseViewAllActivityOverlay();
            }
        }

        private void CloseViewAllActivityOverlay()
        {
            _recentActivityViewAllOverlay?.AddToClassList("hidden");
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Gradient ----------------

        private void ApplyGradients()
        {
            if (_header == null) return;

            if (_gradientTexture != null)
            {
                Destroy(_gradientTexture);
            }

            _gradientTexture = BuildGradientTexture(gradientStart, gradientEnd, true);
            _header.style.backgroundImage = new StyleBackground(_gradientTexture);

            // Both are logged as missing (not fatal) in QueryElements() - guard here so a
            // missing card/button just skips its gradient instead of throwing and aborting
            // the rest of OnEnable() (WireCallbacks/PopulateDashboard/PopulateRecentActivity
            // would otherwise never run).
            if (_joinclassroomcard != null) _joinclassroomcard.style.backgroundImage = new StyleBackground(_gradientTexture);
            if (_joinClassroomButton != null) _joinClassroomButton.style.backgroundImage = new StyleBackground(_gradientTexture);
        }

        private Texture2D BuildGradientTexture(Color start, Color end, bool horizontal)
        {
            const int size = 64;
            var tex = new Texture2D(horizontal ? size : 1, horizontal ? 1 : size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int i = 0; i < size; i++)
            {
                float t = i / (float)(size - 1);
                Color c = Color.Lerp(start, end, t);
                if (horizontal) tex.SetPixel(i, 0, c);
                else tex.SetPixel(0, i, c);
            }

            tex.Apply();
            return tex;
        }
    }
}
