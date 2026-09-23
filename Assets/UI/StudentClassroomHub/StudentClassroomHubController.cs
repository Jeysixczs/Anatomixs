using System;
using System.Collections.Generic;
using Anatomia3D.Backend;
using Firebase.Firestore;
using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Backend for StudentClassroomHub.uxml ("My Classrooms"). Attach to the
    /// same GameObject as UIManager (it uses RequireComponent(UIDocument) like
    /// the other screen controllers, and UIManager finds it via GetComponent).
    ///
    /// This is the student-side counterpart to AdminDashboard's "My Classrooms"
    /// section - same glass-stat-card header, same classroom-card list pattern -
    /// but scoped to the classrooms THIS student has joined. A student can be
    /// enrolled in multiple classrooms at once, so this screen simply lists all
    /// of them; tapping a card opens StudentClassroomDetail (Classmates / Quizzes
    /// / Leaderboard) for that specific classroom via
    /// UIManager.ShowStudentClassroomDetail().
    ///
    /// Responsibilities:
    ///  - Wires up the back button and the two "Join a Classroom" entry points
    ///    (header button + empty-state button), both routing to the existing
    ///    StudentClassroom join-code screen
    ///  - Applies the green->blue gradient to the header at runtime
    ///  - Loads the student's real classrooms from ClassroomService and builds
    ///    the "My Classrooms" list at runtime (no classrooms yet -> empty state)
    ///  - A simple "compact" breakpoint toggle for smaller phone screens
    ///  - Exposes SetHeaderStats() / SetClassrooms() so other code can still
    ///    push values in directly if needed
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class StudentClassroomHubController : MonoBehaviour
    {
        /// <summary>Plain data for a single row in the "My Classrooms" list.</summary>
        public struct ClassroomSummary
        {
            public string ClassroomId;
            public string Name;
            public string Code;
            public string TeacherName;
            public int StudentCount;

            /// <summary>When true, BuildClassroomCard() renders this card locked - "Archived"
            /// badge, disabled "View Classroom" button, no tap-to-open - so the student never
            /// navigates into StudentClassroomDetail for it. (That screen also blocks entry
            /// itself if somehow reached - see StudentClassroomDetailController.
            /// LoadClassroomContent() - this is the friendlier, "don't even let them tap it"
            /// layer on top of that.)</summary>
            public bool IsArchived;

            public ClassroomSummary(string classroomId, string name, string code, string teacherName, int studentCount, bool isArchived = false)
            {
                ClassroomId = classroomId;
                Name = name;
                Code = code;
                TeacherName = teacherName;
                StudentCount = studentCount;
                IsArchived = isArchived;
            }
        }

        [Header("Gradient colors (matches Join Classroom / Quiz Selection: green -> blue)")]
        [SerializeField] private Color gradientStart = new Color(0.086f, 0.737f, 0.463f); // green
        [SerializeField] private Color gradientEnd = new Color(0.145f, 0.388f, 0.922f);   // blue

        [Header("Compact breakpoint (px, reference is 1080x1920)")]
        [SerializeField] private int compactWidthThreshold = 900;

        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _screenRoot;

        private Texture2D _headerGradientTexture;

        private VisualElement _header;
        private Label _headerSubtitleLabel;
        private Button _backButton;

        private Label _classroomsCountLabel;
        private Label _classmatesCountLabel;
        private Label _quizzesAvailableCountLabel;

        private Button _joinClassroomHeaderButton;
        private Button _joinFirstClassroomButton;

        private VisualElement _classroomsEmptyState;
        private VisualElement _classroomsList;

        [Header("Preview / Mock Data")]
        [Tooltip("Falls back to a sample classroom only if ClassroomService/PlayerSessionManager aren't ready yet (e.g. testing this screen in isolation).")]
        [SerializeField] private bool useMockDataUntilWired = true;

        private List<ClassroomSummary> _currentClassrooms = new List<ClassroomSummary>();

        /// <summary>Live query subscription started in OnEnable, stopped in OnDisable -
        /// see ClassroomService.ListenToMyClassrooms. Replaces the old "fetch once per
        /// session" flag: since the listener is bounded to this screen's visible
        /// lifetime anyway, there's no separate cache-invalidation flag to manage
        /// (StudentClassroomController.OnJoinResult's InvalidateStudentClassroomHub()
        /// call is now a safe no-op, kept for source compatibility).</summary>
        private ListenerRegistration _classroomsListener;

        /// <summary>"You're Offline" overlay with Retry / Go back to Dashboard - shown
        /// when this screen is opened offline, and toggled live if the connection drops
        /// or comes back while it's open. Rebuilt every OnEnable (see OfflineOverlay's
        /// own doc comment on why - CloneTree wipes the whole screen tree on every
        /// UIManager.ShowScreen()).</summary>
        private OfflineOverlay _offlineOverlay;

        /// <summary>Element refs for one already-built classroom card, keyed by
        /// classroomId, so a later update can patch labels in place instead of
        /// destroying and recreating the card GameObject.</summary>
        private class ClassroomCardRefs
        {
            public VisualElement Card;
            public Label NameLabel;
            public Label CodeLabel;
            public Label TeacherLabel;
            public Label StudentsLabel;
            public Button ViewButton;
            public ClassroomSummary LastSummary;

            // Kept so ApplyCardContent can unregister the previous closure before
            // wiring a new one, instead of stacking a new handler on every update.
            public Action CachedButtonHandler;
            public EventCallback<ClickEvent> CachedCardHandler;
        }

        private readonly Dictionary<string, ClassroomCardRefs> _classroomCardsById = new Dictionary<string, ClassroomCardRefs>();

        // Last values pushed through SetHeaderStats() - RefreshClassroomsUI() only
        // repaints the classroom list, so OnEnable needs these to restore the three
        // glass stat cards + subtitle too when it's skipping a fresh fetch.
        private int _lastClassroomCount;
        private int _lastTotalClassmates;
        private int _lastQuizzesAvailable;

        private void OnEnable()
        {
            Debug.Log("[StudentClassroomHubController] OnEnable called");

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
                Debug.LogError("[StudentClassroomHubController] Root is null!");
                return;
            }

            UnregisterCallbacks();

            QueryElements();
            ApplyHeaderGradient();
            WireCallbacks();
            UpdateResponsiveLayout();

            RefreshClassroomsUI();
            SetHeaderStats(_lastClassroomCount, _lastTotalClassmates, _lastQuizzesAvailable);

            // Screen tree was just rebuilt (see class doc) - rebuild the overlay on top
            // of it and re-subscribe (guard against a double-subscribe if OnEnable ever
            // runs twice without OnDisable in between).
            _offlineOverlay?.Dispose();
            _offlineOverlay = new OfflineOverlay(_screenRoot, OnOfflineRetry, OnOfflineGoToDashboard);

            NetworkStatusMonitor.OnConnectivityChanged -= OnConnectivityStatusChanged;
            NetworkStatusMonitor.OnAppResumed -= HandleAppResumed;
            NetworkStatusMonitor.OnConnectivityChanged += OnConnectivityStatusChanged;
            NetworkStatusMonitor.OnAppResumed += HandleAppResumed;

            if (NetworkStatusMonitor.IsOnline)
            {
                StartClassroomsListener();
            }
            else
            {
                // Scenario 1: student opens "My Classrooms" while already offline -
                // show the overlay instead of starting (and immediately failing) the
                // live listener.
                Debug.Log("[StudentClassroomHubController] Offline on enable - showing offline overlay instead of starting the classrooms listener.");
                _offlineOverlay.Show();
            }
        }

        // ---------------- Offline handling ----------------

        /// <summary>Scenario 2: student is already viewing this screen and the
        /// connection drops or comes back - see NetworkStatusMonitor.</summary>
        private void OnConnectivityStatusChanged(bool isOnline)
        {
            if (_offlineOverlay == null) return;

            if (!isOnline)
            {
                Debug.Log("[StudentClassroomHubController] Connection lost - showing offline overlay.");
                _offlineOverlay.Show();
            }
            else if (_offlineOverlay.IsVisible)
            {
                Debug.Log("[StudentClassroomHubController] Connection restored - hiding offline overlay and reloading classrooms.");
                _offlineOverlay.Hide();
                StartClassroomsListener();
            }
        }
        /// <summary>App came back from the background (NetworkStatusMonitor.OnAppResumed, which
        /// only fires while online). Re-attach the live classrooms listener so the list, the
        /// classmate counts and the quiz counts are current, and clear the offline overlay if it
        /// was showing.</summary>
        private void HandleAppResumed(float secondsAway)
        {
            if (_screenRoot == null) return;

            if (_offlineOverlay != null && _offlineOverlay.IsVisible) _offlineOverlay.Hide();
            StartClassroomsListener();
        }


        private void OnOfflineRetry()
        {
            Debug.Log("[StudentClassroomHubController] Offline overlay Retry tapped while back online - reloading classrooms.");
            StartClassroomsListener();
        }

        private void OnOfflineGoToDashboard()
        {
            Debug.Log("[StudentClassroomHubController] Offline overlay - returning to dashboard.");
            UIManager.Instance?.ShowStudentDashboard();
        }

        // ---------------- Loading real data ----------------

        private void StartClassroomsListener()
        {
            StopClassroomsListener();

            bool sessionReady = PlayerSessionManager.Instance != null && PlayerSessionManager.Instance.IsLoggedIn;

            if (ClassroomService.Instance != null && sessionReady)
            {
                _classroomsListener = ClassroomService.Instance.ListenToMyClassrooms(OnClassroomsFetched);
                return;
            }

            Debug.LogWarning("[StudentClassroomHubController] ClassroomService not ready or no student signed in.");

            if (useMockDataUntilWired && _currentClassrooms.Count == 0)
            {
                LoadMockClassroom();
            }
        }

        private void StopClassroomsListener()
        {
            _classroomsListener?.Stop();
            _classroomsListener = null;
        }

        private void OnClassroomsFetched(List<ClassroomService.ClassroomRecord> records)
        {
            var summaries = new List<ClassroomSummary>();
            int totalClassmates = 0;

            foreach (var record in records)
            {
                summaries.Add(new ClassroomSummary(
                    record.ClassroomId,
                    record.Name,
                    record.Code,
                    record.TeacherName,
                    record.StudentCount,
                    record.IsArchived));

                // Exclude the current student from the "classmates" count.
                totalClassmates += Mathf.Max(0, record.StudentCount - 1);
            }

            // TODO: quizzesAvailable needs a QuizService (not built yet) to count
            // quizzes scoped to these classrooms + global quizzes. Wire it in once
            // QuizService.FetchAvailableQuizCount() exists.
            SetHeaderStats(classroomCount: summaries.Count, totalClassmates: totalClassmates, quizzesAvailable: 0);
            SetClassrooms(summaries);
        }

        /// <summary>Fills the hub with one sample classroom (matches "Anatomy 101 - Section A" in the mocks) for preview purposes.</summary>
        private void LoadMockClassroom()
        {
            SetHeaderStats(classroomCount: 1, totalClassmates: 4, quizzesAvailable: 1);

            SetClassrooms(new List<ClassroomSummary>
            {
                new ClassroomSummary(
                    classroomId: "mock-anatomy-101",
                    name: "Anatomy 101 - Section A",
                    code: "YG7XYYHJ",
                    teacherName: "Dr. Smith",
                    studentCount: 4)
            });
        }

        private void OnDisable()
        {
            UnregisterCallbacks();
            StopClassroomsListener();

            NetworkStatusMonitor.OnConnectivityChanged -= OnConnectivityStatusChanged;
            NetworkStatusMonitor.OnAppResumed -= HandleAppResumed;
            _offlineOverlay?.Dispose();
            _offlineOverlay = null;

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
                _headerGradientTexture = null;
            }
        }

        private void UnregisterCallbacks()
        {
            if (_screenRoot == null) return;

            _backButton?.UnregisterCallback<ClickEvent>(OnBackClicked);
            _joinClassroomHeaderButton?.UnregisterCallback<ClickEvent>(OnJoinClassroomClicked);
            _joinFirstClassroomButton?.UnregisterCallback<ClickEvent>(OnJoinClassroomClicked);
            _screenRoot.UnregisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
        }

        private void QueryElements()
        {
            _screenRoot = _root.Q<VisualElement>("screen-root");

            if (_screenRoot == null)
            {
                Debug.LogWarning("[StudentClassroomHubController] screen-root not found, using root directly");
                _screenRoot = _root;
            }

            _header = _screenRoot.Q<VisualElement>("header");
            _headerSubtitleLabel = _screenRoot.Q<Label>("header-subtitle-label");
            _backButton = _screenRoot.Q<Button>("back-button");

            _classroomsCountLabel = _screenRoot.Q<Label>("classrooms-count-label");
            _classmatesCountLabel = _screenRoot.Q<Label>("classmates-count-label");
            _quizzesAvailableCountLabel = _screenRoot.Q<Label>("quizzes-available-count-label");

            _joinClassroomHeaderButton = _screenRoot.Q<Button>("join-classroom-header-button");
            _joinFirstClassroomButton = _screenRoot.Q<Button>("join-first-classroom-button");

            _classroomsEmptyState = _screenRoot.Q<VisualElement>("classrooms-empty-state");
            _classroomsList = _screenRoot.Q<VisualElement>("classrooms-list");

            Debug.Log($"[StudentClassroomHubController] Found classrooms list: {_classroomsList != null}, empty state: {_classroomsEmptyState != null}");
        }

        private void WireCallbacks()
        {
            _backButton?.RegisterCallback<ClickEvent>(OnBackClicked);
            _joinClassroomHeaderButton?.RegisterCallback<ClickEvent>(OnJoinClassroomClicked);
            _joinFirstClassroomButton?.RegisterCallback<ClickEvent>(OnJoinClassroomClicked);

            if (_screenRoot != null)
            {
                _screenRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            }
        }

        // ---------------- Public API ----------------

        /// <summary>Push real values into the three glass stat cards + header subtitle in the header.
        /// Only actually writes a label whose text changed - Firestore listener callbacks fire on
        /// every snapshot (including the local echo of this device's own writes), so most calls here
        /// carry identical numbers to what's already on screen.</summary>
        public void SetHeaderStats(int classroomCount, int totalClassmates, int quizzesAvailable)
        {
            _lastClassroomCount = classroomCount;
            _lastTotalClassmates = totalClassmates;
            _lastQuizzesAvailable = quizzesAvailable;

            SetLabelIfChanged(_headerSubtitleLabel, $"You're enrolled in {classroomCount} classroom{(classroomCount == 1 ? "" : "s")}");
            SetLabelIfChanged(_classroomsCountLabel, classroomCount.ToString("N0"));
            SetLabelIfChanged(_classmatesCountLabel, totalClassmates.ToString("N0"));
            SetLabelIfChanged(_quizzesAvailableCountLabel, quizzesAvailable.ToString("N0"));
        }

        private static void SetLabelIfChanged(Label label, string text)
        {
            if (label == null || label.text == text) return;
            label.text = text;
        }

        /// <summary>Push the student's enrolled classrooms into "My Classrooms". Pass an empty/null list to show the empty state.</summary>
        public void SetClassrooms(List<ClassroomSummary> classrooms)
        {
            _currentClassrooms = classrooms ?? new List<ClassroomSummary>();
            RefreshClassroomsUI();
        }

        /// <summary>Kept for source compatibility with StudentClassroomController.OnJoinResult's
        /// InvalidateStudentClassroomHub() call. No longer needs to do anything: the live listener
        /// started in OnEnable (see ClassroomService.ListenToMyClassrooms) already reflects a new
        /// join the moment Firestore's transaction commits, whether or not this screen happens to
        /// be open at the time.</summary>
        public void InvalidateClassrooms()
        {
        }

        // ---------------- My Classrooms ----------------

        /// <summary>Diffs _currentClassrooms against the cards already on screen instead of
        /// clearing and rebuilding the whole list: removed classrooms are torn down, unchanged
        /// ones aren't touched at all, changed ones get their labels patched in place, and only
        /// genuinely new ones get a freshly-built card. Keeps GameObject churn proportional to
        /// what actually changed rather than to the size of the list.</summary>
        private void RefreshClassroomsUI()
        {
            bool hasClassrooms = _currentClassrooms != null && _currentClassrooms.Count > 0;

            _classroomsEmptyState?.EnableInClassList("hidden", hasClassrooms);
            _classroomsList?.EnableInClassList("hidden", !hasClassrooms);

            if (_classroomsList == null) return;

            var incomingIds = new HashSet<string>();

            if (hasClassrooms)
            {
                for (int i = 0; i < _currentClassrooms.Count; i++)
                {
                    var summary = _currentClassrooms[i];
                    incomingIds.Add(summary.ClassroomId);

                    if (_classroomCardsById.TryGetValue(summary.ClassroomId, out var refs))
                    {
                        UpdateClassroomCard(refs, summary);
                    }
                    else
                    {
                        refs = BuildClassroomCard(summary);
                        _classroomCardsById[summary.ClassroomId] = refs;
                    }

                    // Keep list order in sync with _currentClassrooms - Insert() on an
                    // already-parented element just moves it, so unaffected cards
                    // elsewhere in the list aren't touched.
                    if (_classroomsList.IndexOf(refs.Card) != i)
                    {
                        _classroomsList.Insert(i, refs.Card);
                    }
                }
            }

            // Remove cards for classrooms that are no longer in the list (e.g. the
            // teacher removed this student, or - defensively - an id disappeared).
            List<string> staleIds = null;
            foreach (var id in _classroomCardsById.Keys)
            {
                if (!incomingIds.Contains(id))
                {
                    (staleIds ??= new List<string>()).Add(id);
                }
            }

            if (staleIds != null)
            {
                foreach (var id in staleIds)
                {
                    _classroomCardsById[id].Card.RemoveFromHierarchy();
                    _classroomCardsById.Remove(id);
                }
            }
        }

        private ClassroomCardRefs BuildClassroomCard(ClassroomSummary classroom)
        {
            var card = new VisualElement();
            card.AddToClassList("classroom-card");

            var topRow = new VisualElement();
            topRow.AddToClassList("classroom-card-top-row");

            var nameLabel = new Label();
            nameLabel.AddToClassList("classroom-name-label");

            var codeBadge = new VisualElement();
            codeBadge.AddToClassList("classroom-code-badge");
            var codeLabel = new Label();
            codeLabel.AddToClassList("classroom-code-badge-label");
            codeBadge.Add(codeLabel);

            topRow.Add(nameLabel);
            topRow.Add(codeBadge);

            card.Add(topRow);

            var teacherLabel = new Label();
            teacherLabel.AddToClassList("classroom-teacher-label");
            card.Add(teacherLabel);

            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("classroom-card-bottom-row");

            var studentsRow = new VisualElement();
            studentsRow.AddToClassList("classroom-students-row");
            var studentsIcon = new VisualElement();
            studentsIcon.AddToClassList("classroom-students-icon");
            var studentsLabel = new Label();
            studentsLabel.AddToClassList("classroom-students-label");
            studentsRow.Add(studentsIcon);
            studentsRow.Add(studentsLabel);

            var viewClassroomButton = new Button();
            viewClassroomButton.AddToClassList("view-classroom-button");

            bottomRow.Add(studentsRow);
            bottomRow.Add(viewClassroomButton);
            card.Add(bottomRow);

            var refs = new ClassroomCardRefs
            {
                Card = card,
                NameLabel = nameLabel,
                CodeLabel = codeLabel,
                TeacherLabel = teacherLabel,
                StudentsLabel = studentsLabel,
                ViewButton = viewClassroomButton
            };

            ApplyCardContent(refs, classroom);
            return refs;
        }

        /// <summary>Patches an existing card's labels/handlers in place. Called both right
        /// after a card is first built and on every later update - ApplyCardContent itself
        /// only writes a field whose value actually changed.</summary>
        private void UpdateClassroomCard(ClassroomCardRefs refs, ClassroomSummary classroom)
        {
            ApplyCardContent(refs, classroom);
        }

        private void ApplyCardContent(ClassroomCardRefs refs, ClassroomSummary classroom)
        {
            var last = refs.LastSummary;
            bool isFirstPaint = string.IsNullOrEmpty(last.ClassroomId);

            if (isFirstPaint || last.Name != classroom.Name)
                SetLabelIfChanged(refs.NameLabel, classroom.Name);

            if (isFirstPaint || last.Code != classroom.Code)
                SetLabelIfChanged(refs.CodeLabel, classroom.Code);

            if (isFirstPaint || last.TeacherName != classroom.TeacherName)
                SetLabelIfChanged(refs.TeacherLabel, $"Taught by {classroom.TeacherName}");

            if (isFirstPaint || last.StudentCount != classroom.StudentCount)
                SetLabelIfChanged(refs.StudentsLabel, $"{classroom.StudentCount} student{(classroom.StudentCount == 1 ? "" : "s")}");

            if (isFirstPaint || last.IsArchived != classroom.IsArchived)
            {
                refs.Card.EnableInClassList("classroom-card-archived", classroom.IsArchived);
                refs.ViewButton.text = classroom.IsArchived ? "Locked" : "View Classroom";
                refs.ViewButton.EnableInClassList("view-classroom-button-disabled", classroom.IsArchived);
                refs.ViewButton.SetEnabled(!classroom.IsArchived);

                // Click handlers capture `classroom` by value, so they need re-wiring
                // whenever the summary they close over changes (archived state, or any
                // other field a handler might reference later).
                if (refs.CachedButtonHandler != null) refs.ViewButton.clicked -= refs.CachedButtonHandler;
                if (refs.CachedCardHandler != null) refs.Card.UnregisterCallback(refs.CachedCardHandler);

                refs.CachedButtonHandler = () => OnViewClassroomClicked(classroom);
                refs.ViewButton.clicked += refs.CachedButtonHandler;

                if (!classroom.IsArchived)
                {
                    refs.CachedCardHandler = _ => OnViewClassroomClicked(classroom);
                    refs.Card.RegisterCallback<ClickEvent>(refs.CachedCardHandler);
                }
                else
                {
                    refs.CachedCardHandler = null;
                }
            }

            refs.LastSummary = classroom;
        }

        // ---------------- Button handlers ----------------

        private void OnBackClicked(ClickEvent evt)
        {
            Debug.Log("[StudentClassroomHubController] Navigating back to dashboard");
            UIManager.Instance.ShowStudentDashboard();
        }

        private void OnJoinClassroomClicked(ClickEvent evt)
        {
            Debug.Log("[StudentClassroomHubController] Join Classroom tapped");
            UIManager.Instance.ShowJoinClassroom();
        }

        private void OnViewClassroomClicked(ClassroomSummary classroom)
        {
            // Defense in depth: the button/card are already disabled and non-clickable
            // for archived classrooms (see BuildClassroomCard()), but guard here too in
            // case this is ever called directly.
            if (classroom.IsArchived)
            {
                Debug.Log($"[StudentClassroomHubController] Ignored tap on archived classroom '{classroom.Name}' ({classroom.Code}).");
                return;
            }

            Debug.Log($"[StudentClassroomHubController] Opening classroom '{classroom.Name}' ({classroom.Code}).");
            UIManager.Instance.ShowStudentClassroomDetail(classroom.ClassroomId, classroom.Name, classroom.TeacherName);
        }

        // ---------------- Responsive layout ----------------

        private void OnRootGeometryChanged(GeometryChangedEvent evt) => UpdateResponsiveLayout();

        private void UpdateResponsiveLayout()
        {
            if (_screenRoot == null) return;
            bool compact = _screenRoot.resolvedStyle.width > 0 && _screenRoot.resolvedStyle.width < compactWidthThreshold;
            _screenRoot.EnableInClassList("compact", compact);
        }

        // ---------------- Header Gradient (USS has no linear-gradient) ----------------

        private void ApplyHeaderGradient()
        {
            if (_header == null) return;

            if (_headerGradientTexture != null)
            {
                Destroy(_headerGradientTexture);
            }

            _headerGradientTexture = BuildGradientTexture(gradientStart, gradientEnd);
            _header.style.backgroundImage = new StyleBackground(_headerGradientTexture);
        }

        private Texture2D BuildGradientTexture(Color start, Color end)
        {
            const int size = 64;
            var tex = new Texture2D(size, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "StudentClassroomHubHeaderGradientTexture"
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