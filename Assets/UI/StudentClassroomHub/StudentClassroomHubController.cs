using System.Collections.Generic;
using Anatomia3D.Backend;
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
        private bool _realDataReceived;

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

            // First time this screen opens this session -> fetch. After that,
            // RefreshClassroomsUI()/SetHeaderStats() above already repainted from
            // cache, so a re-enable (e.g. switching tabs elsewhere and coming back)
            // doesn't need another Firestore read. Same pattern as
            // StudentAchievementsController.
            if (!_realDataReceived)
            {
                LoadClassroomsFromBackend();
            }
        }

        // ---------------- Loading real data ----------------

        private void LoadClassroomsFromBackend()
        {
            bool sessionReady = PlayerSessionManager.Instance != null && PlayerSessionManager.Instance.IsLoggedIn;

            if (ClassroomService.Instance != null && sessionReady)
            {
                ClassroomService.Instance.FetchMyClassrooms(OnClassroomsFetched);
                return;
            }

            Debug.LogWarning("[StudentClassroomHubController] ClassroomService not ready or no student signed in.");

            if (useMockDataUntilWired && _currentClassrooms.Count == 0 && !_realDataReceived)
            {
                LoadMockClassroom();
            }
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

            // SetClassrooms() above flips _realDataReceived to true; reset it since
            // this was mock data, so a later real SetClassrooms([]) call still shows
            // the empty state instead of being mistaken for "no data yet".
            _realDataReceived = false;
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

        /// <summary>Push real values into the three glass stat cards + header subtitle in the header.</summary>
        public void SetHeaderStats(int classroomCount, int totalClassmates, int quizzesAvailable)
        {
            _lastClassroomCount = classroomCount;
            _lastTotalClassmates = totalClassmates;
            _lastQuizzesAvailable = quizzesAvailable;

            if (_headerSubtitleLabel != null)
                _headerSubtitleLabel.text = $"You're enrolled in {classroomCount} classroom{(classroomCount == 1 ? "" : "s")}";

            if (_classroomsCountLabel != null) _classroomsCountLabel.text = classroomCount.ToString("N0");
            if (_classmatesCountLabel != null) _classmatesCountLabel.text = totalClassmates.ToString("N0");
            if (_quizzesAvailableCountLabel != null) _quizzesAvailableCountLabel.text = quizzesAvailable.ToString("N0");
        }

        /// <summary>Push the student's enrolled classrooms into "My Classrooms". Pass an empty/null list to show the empty state.</summary>
        public void SetClassrooms(List<ClassroomSummary> classrooms)
        {
            _currentClassrooms = classrooms ?? new List<ClassroomSummary>();
            _realDataReceived = true;
            RefreshClassroomsUI();
        }

        /// <summary>Call after something changes which classrooms this student is
        /// enrolled in (e.g. StudentClassroomController.OnJoinResult right after a
        /// successful join) so the NEXT time this screen opens, OnEnable's
        /// first-load check in RefreshClassroomsUI()/LoadClassroomsFromBackend()
        /// doesn't skip the fetch and show a stale list missing the new
        /// classroom. Cheap no-op if the screen is currently visible - the next
        /// OnEnable already has to fully re-init anyway.</summary>
        public void InvalidateClassrooms()
        {
            _realDataReceived = false;
        }

        // ---------------- My Classrooms ----------------

        private void RefreshClassroomsUI()
        {
            bool hasClassrooms = _currentClassrooms != null && _currentClassrooms.Count > 0;

            _classroomsEmptyState?.EnableInClassList("hidden", hasClassrooms);
            _classroomsList?.EnableInClassList("hidden", !hasClassrooms);

            if (_classroomsList == null) return;

            _classroomsList.Clear();

            if (!hasClassrooms) return;

            foreach (var classroom in _currentClassrooms)
            {
                _classroomsList.Add(BuildClassroomCard(classroom));
            }
        }

        private VisualElement BuildClassroomCard(ClassroomSummary classroom)
        {
            var card = new VisualElement();
            card.AddToClassList("classroom-card");
            if (classroom.IsArchived) card.AddToClassList("classroom-card-archived");

            var topRow = new VisualElement();
            topRow.AddToClassList("classroom-card-top-row");

            var nameLabel = new Label(classroom.Name);
            nameLabel.AddToClassList("classroom-name-label");

            var codeBadge = new VisualElement();
            codeBadge.AddToClassList("classroom-code-badge");
            var codeLabel = new Label(classroom.Code);
            codeLabel.AddToClassList("classroom-code-badge-label");
            codeBadge.Add(codeLabel);

            topRow.Add(nameLabel);
            topRow.Add(codeBadge);


            card.Add(topRow);

            var teacherLabel = new Label($"Taught by {classroom.TeacherName}");
            teacherLabel.AddToClassList("classroom-teacher-label");
            card.Add(teacherLabel);

            var bottomRow = new VisualElement();
            bottomRow.AddToClassList("classroom-card-bottom-row");

            var studentsRow = new VisualElement();
            studentsRow.AddToClassList("classroom-students-row");
            var studentsIcon = new VisualElement();
            studentsIcon.AddToClassList("classroom-students-icon");
            var studentsLabel = new Label($"{classroom.StudentCount} student{(classroom.StudentCount == 1 ? "" : "s")}");
            studentsLabel.AddToClassList("classroom-students-label");
            studentsRow.Add(studentsIcon);
            studentsRow.Add(studentsLabel);

            var viewClassroomButton = new Button(() => OnViewClassroomClicked(classroom))
            {
                text = classroom.IsArchived ? "Archived" : "View Classroom"
            };
            viewClassroomButton.AddToClassList("view-classroom-button");
            if (classroom.IsArchived)
            {
                viewClassroomButton.AddToClassList("view-classroom-button-disabled");
                viewClassroomButton.SetEnabled(false);
            }

            bottomRow.Add(studentsRow);
            bottomRow.Add(viewClassroomButton);
            card.Add(bottomRow);

            // Locked cards don't route into StudentClassroomDetail at all - no whole-card
            // tap, and the button above is disabled - so an archived classroom is fully
            // non-interactive here rather than just visually marked.
            if (!classroom.IsArchived)
            {
                card.RegisterCallback<ClickEvent>(_ => OnViewClassroomClicked(classroom));
            }

            return card;
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