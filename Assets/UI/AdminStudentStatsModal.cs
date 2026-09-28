using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Two-step modal opened from the "Total Students" (#active-users-card) card on the
    /// Admin Analytics screen. Built entirely in code with inline styles (no UXML/USS
    /// changes), same approach as AdminRetakeExamModal.
    ///
    ///  Step 1 - Student list: every student in the selected classroom (name, level,
    ///           points). Tap a student to open their stats.
    ///  Step 2 - Student stats: Level / Points / Quizzes Completed / Avg Score tiles plus
    ///           Play Mode progress (Skeletal / Muscular / Cardiovascular bars), quiz
    ///           completion rate (completed / passed) and the
    ///           student's best score on each quiz published to the classroom.
    ///           A back arrow returns to the list.
    ///
    /// Usage: AdminStudentStatsModal.Show(host, classroomId, students, quizzes);
    /// </summary>
    public static class AdminStudentStatsModal
    {
        private static readonly Color Ink = new Color(0.13f, 0.15f, 0.14f);
        private static readonly Color Muted = new Color(0.45f, 0.49f, 0.48f);
        private static readonly Color Line = new Color(0.89f, 0.91f, 0.90f);
        private static readonly Color Surface = new Color(0.96f, 0.97f, 0.97f);
        private static readonly Color Accent = new Color(0.13f, 0.55f, 0.42f);
        private static readonly Color AccentSoft = new Color(0.90f, 0.96f, 0.93f);
        private static readonly Color Danger = new Color(0.80f, 0.20f, 0.25f);
        private static readonly Color DangerSoft = new Color(0.99f, 0.92f, 0.93f);
        private static readonly Color Amber = new Color(0.72f, 0.45f, 0.05f);
        private static readonly Color AmberSoft = new Color(1.00f, 0.95f, 0.85f);

        /// <param name="host">Element to overlay the dialog on (the screen root).</param>
        /// <param name="classroomId">Selected classroom (used for the per-quiz scores).</param>
        /// <param name="students">Roster from AdminClassroomService.FetchClassroomAnalytics.</param>
        /// <param name="quizzes">Quizzes published to the classroom (may be empty).</param>
        /// <param name="openStudentId">Optional. When set (and found in students), the modal opens
        /// straight on that student's stats instead of the list; the back arrow still goes to the list.</param>
        public static void Show(VisualElement host, string classroomId,
            List<AdminClassroomService.StudentStat> students, List<QuizService.QuizRecord> quizzes,
            string openStudentId = null)
        {
            if (host == null) return;

            students = students ?? new List<AdminClassroomService.StudentStat>();
            quizzes = quizzes ?? new List<QuizService.QuizRecord>();

            // ---------- backdrop ----------
            var overlay = new VisualElement { name = "student-stats-overlay" };
            overlay.style.position = Position.Absolute;
            overlay.style.left = overlay.style.top = overlay.style.right = overlay.style.bottom = 0;
            overlay.style.backgroundColor = new Color(0.05f, 0.08f, 0.07f, 0.6f);
            overlay.style.justifyContent = Justify.Center;
            overlay.style.alignItems = Align.Center;

            void Close() => overlay.RemoveFromHierarchy();
            overlay.RegisterCallback<ClickEvent>(evt => { if (evt.target == overlay) Close(); });

            // ---------- card ----------
            var card = new VisualElement();
            card.style.width = Length.Percent(92);
            card.style.maxHeight = Length.Percent(88);
            card.style.backgroundColor = Color.white;
            card.style.overflow = Overflow.Hidden;
            Radius(card, 40);
            overlay.Add(card);

            // ---------- header band ----------
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.flexShrink = 0;
            header.style.backgroundColor = Accent;
            Padding(header, 32, 36);

            var backButton = new Button { text = "\u2190" };
            ResetButton(backButton);
            backButton.style.width = backButton.style.height = 72;
            backButton.style.flexShrink = 0;
            backButton.style.fontSize = 36;
            backButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            backButton.style.color = Color.white;
            backButton.style.backgroundColor = new Color(1, 1, 1, 0.20f);
            backButton.style.marginRight = 24;
            backButton.style.display = DisplayStyle.None;
            Radius(backButton, 36);
            header.Add(backButton);

            var titles = new VisualElement();
            titles.style.flexGrow = 1;
            titles.style.flexShrink = 1;
            var titleLabel = MakeLabel("Students", 42, FontStyle.Bold, Color.white);
            var subtitleLabel = MakeLabel("", 26, FontStyle.Normal, new Color(1, 1, 1, 0.85f), 4, 0);
            titles.Add(titleLabel);
            titles.Add(subtitleLabel);
            header.Add(titles);

            var closeButton = new Button(Close) { text = "\u2715" };
            ResetButton(closeButton);
            closeButton.style.width = closeButton.style.height = 72;
            closeButton.style.flexShrink = 0;
            closeButton.style.fontSize = 32;
            closeButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            closeButton.style.color = Color.white;
            closeButton.style.backgroundColor = new Color(1, 1, 1, 0.20f);
            Radius(closeButton, 36);
            header.Add(closeButton);

            card.Add(header);

            // ---------- scrolling body ----------
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;
            scroll.contentContainer.style.paddingLeft = scroll.contentContainer.style.paddingRight = 36;
            scroll.contentContainer.style.paddingTop = 32;
            scroll.contentContainer.style.paddingBottom = 32;
            card.Add(scroll);

            host.Add(overlay);

            // Guards against a slow per-quiz fetch landing after the user already went back
            // to the list (or opened a different student).
            int viewToken = 0;

            void ShowList()
            {
                viewToken++;
                backButton.style.display = DisplayStyle.None;
                titleLabel.text = "Students";
                subtitleLabel.text = students.Count == 1 ? "1 student enrolled" : $"{students.Count} students enrolled";
                scroll.Clear();
                scroll.scrollOffset = Vector2.zero;

                if (students.Count == 0)
                {
                    var empty = MakeLabel("No students have joined this classroom yet.", 28, FontStyle.Normal, Muted, 24, 24);
                    empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                    scroll.Add(empty);
                    return;
                }

                foreach (var student in students.OrderBy(s => s.Name ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    var captured = student;
                    scroll.Add(BuildStudentRow(captured, () => ShowDetail(captured)));
                }
            }

            void ShowDetail(AdminClassroomService.StudentStat student)
            {
                int token = ++viewToken;
                backButton.style.display = DisplayStyle.Flex;
                titleLabel.text = string.IsNullOrEmpty(student.Name) ? "Student" : student.Name;
                subtitleLabel.text = "Student stats";
                scroll.Clear();
                scroll.scrollOffset = Vector2.zero;

                // Avatar + name banner
                var banner = new VisualElement();
                banner.style.flexDirection = FlexDirection.Row;
                banner.style.alignItems = Align.Center;
                banner.style.marginBottom = 28;
                banner.Add(MakeAvatar(student.Name, 110, student.StudentId));
                var bannerText = new VisualElement();
                bannerText.style.marginLeft = 24;
                bannerText.style.flexShrink = 1;
                bannerText.Add(MakeLabel(string.IsNullOrEmpty(student.Name) ? "Student" : student.Name, 38, FontStyle.Bold, Ink));
                bannerText.Add(MakeLabel($"Level {student.Level}", 26, FontStyle.Normal, Muted, 4, 0));
                banner.Add(bannerText);
                scroll.Add(banner);

                // 2x2 stat tiles
                var row1 = TileRow();
                row1.Add(MakeTile("Level", student.Level.ToString(), AccentSoft, Accent));
                row1.Add(MakeTile("Points", student.Points.ToString("N0"), AmberSoft, Amber));
                scroll.Add(row1);

                var row2 = TileRow();
                row2.Add(MakeTile("Quizzes Completed", student.QuizzesCompleted.ToString(), AccentSoft, Accent));
                var (scoreBg, scoreFg) = ScoreColors(student.AvgScorePercent, student.QuizzesCompleted > 0);
                row2.Add(MakeTile("Avg Score",
                    student.QuizzesCompleted > 0 ? $"{Mathf.RoundToInt(student.AvgScorePercent)}%" : "-",
                    scoreBg, scoreFg));
                scroll.Add(row2);

                // Anatomy Play Mode progress: Skeletal / Muscular / Cardiovascular
                AddPlayModeProgress(scroll, student.StudentId, () => token == viewToken);

                // Quiz sections (filled in once the student's attempts load)
                if (quizzes.Count == 0 || string.IsNullOrEmpty(classroomId) || QuizService.Instance == null)
                {
                    scroll.Add(MakeLabel("No quizzes are published to this classroom yet.", 26, FontStyle.Normal, Muted, 8, 0));
                    return;
                }

                var loading = MakeLabel("Loading progress...", 26, FontStyle.Normal, Muted, 8, 0);
                scroll.Add(loading);

                QuizService.Instance.FetchStudentAttemptsInClassroom(classroomId, student.StudentId, attempts =>
                {
                    if (token != viewToken) return; // user navigated away
                    loading.RemoveFromHierarchy();
                    BuildProgressSections(scroll, quizzes, attempts ?? new List<QuizService.StudentAttemptRow>());
                });
            }

            backButton.clicked += ShowList;
            ShowList();

            if (!string.IsNullOrEmpty(openStudentId))
            {
                var preselected = students.FirstOrDefault(x => x.StudentId == openStudentId);
                if (preselected != null) ShowDetail(preselected);
            }
        }

        // ---------------- Play Mode progress ----------------

        private static readonly (AnatomySystem System, string Label)[] PlayModeSystems =
        {
            (AnatomySystem.Skeletal, "Skeletal"),
            (AnatomySystem.Muscular, "Muscular"),
            (AnatomySystem.Cardiovascular, "Cardiovascular")
        };

        /// <summary>Adds the "Play Mode Progress" card: one bar per anatomy system showing
        /// the percentage of structures completed (counts are deliberately not shown), computed exactly like the student's own
        /// Progress screen (see StudentProgressController.CalculatePerformanceBySystem):
        /// the student's correctly-answered Play Mode keys intersected with
        /// AnatomyScreenController.GetSelectableStructureKeys(system).</summary>
        private static void AddPlayModeProgress(ScrollView scroll, string studentId, Func<bool> stillCurrent)
        {
            scroll.Add(MakeLabel("Play Mode Progress", 32, FontStyle.Bold, Ink, 16, 12));

            var card = new VisualElement();
            card.style.backgroundColor = Surface;
            card.style.marginBottom = 8;
            Padding(card, 24, 26);
            Radius(card, 28);
            scroll.Add(card);

            var loading = MakeLabel("Loading Play Mode progress...", 26, FontStyle.Normal, Muted);
            card.Add(loading);

            if (AdminClassroomService.Instance == null || AnatomyScreenController.Instance == null)
            {
                loading.text = "Play Mode progress isn't available on this screen.";
                return;
            }

            AdminClassroomService.Instance.FetchStudentPlayModeKeys(studentId, keys =>
            {
                if (!stillCurrent()) return; // user navigated away

                card.Clear();

                if (keys == null)
                {
                    card.Add(MakeLabel("Couldn't load Play Mode progress.", 26, FontStyle.Normal, Muted));
                    return;
                }

                for (int i = 0; i < PlayModeSystems.Length; i++)
                {
                    var (system, label) = PlayModeSystems[i];

                    var structureKeys = AnatomyScreenController.Instance.GetSelectableStructureKeys(system);
                    int total = structureKeys.Count;
                    int completed = 0;
                    foreach (var key in keys)
                        if (structureKeys.Contains(key)) completed++;

                    float percent = total > 0 ? completed * 100f / total : 0f;
                    string detail = total > 0 ? $"{Mathf.RoundToInt(percent)}%" : "-";

                    var row = MakeProgressRow(label, detail, percent, Accent);
                    if (i > 0) row.style.marginTop = 24;
                    card.Add(row);
                }
            });
        }

        // ---------------- progress sections ----------------

        private static void BuildProgressSections(ScrollView scroll, List<QuizService.QuizRecord> quizzes,
            List<QuizService.StudentAttemptRow> attempts)
        {
            // Best attempt per quiz (highest percent) - the same "best attempt" rule
            // QuizService.FetchQuizScoresForClassroom uses for the export.
            var bestByQuiz = attempts
                .Where(a => !string.IsNullOrEmpty(a.QuizId))
                .GroupBy(a => a.QuizId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Percent).First());

            // ---- Quiz completion ----
            int total = quizzes.Count;
            int attempted = quizzes.Count(q => bestByQuiz.ContainsKey(q.QuizId));
            int passed = quizzes.Count(q => bestByQuiz.TryGetValue(q.QuizId, out var b) && b.Passed);
            float completionPct = total > 0 ? attempted * 100f / total : 0f;
            float passPct = total > 0 ? passed * 100f / total : 0f;

            scroll.Add(MakeLabel("Quiz Completion", 32, FontStyle.Bold, Ink, 16, 12));

            var completionCard = new VisualElement();
            completionCard.style.backgroundColor = Surface;
            Padding(completionCard, 24, 26);
            Radius(completionCard, 28);
            completionCard.style.marginBottom = 8;
            completionCard.Add(MakeProgressRow("Completed", $"{attempted}/{total}  \u2022  {Mathf.RoundToInt(completionPct)}%",
                completionPct, Accent));
            var passRow = MakeProgressRow("Passed", $"{passed}/{total}  \u2022  {Mathf.RoundToInt(passPct)}%", passPct, Amber);
            passRow.style.marginTop = 20;
            completionCard.Add(passRow);
            scroll.Add(completionCard);

            // ---- Quiz scores ----
            scroll.Add(MakeLabel("Quiz Scores", 32, FontStyle.Bold, Ink, 24, 12));
            foreach (var q in quizzes)
            {
                bestByQuiz.TryGetValue(q.QuizId, out var best);
                scroll.Add(BuildQuizScoreRow(q.Title, best));
            }
        }

        /// <summary>Label + value on one line with a rounded progress bar underneath.</summary>
        private static VisualElement MakeProgressRow(string label, string valueText, float percent, Color barColor)
        {
            var wrap = new VisualElement();

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.justifyContent = Justify.SpaceBetween;
            top.style.alignItems = Align.Center;

            var name = MakeLabel(label, 28, FontStyle.Bold, Ink);
            name.style.flexShrink = 1;
            var value = MakeLabel(valueText, 24, FontStyle.Normal, Muted);
            value.style.flexShrink = 0;
            value.style.marginLeft = 16;
            top.Add(name);
            top.Add(value);
            wrap.Add(top);

            var track = new VisualElement();
            track.style.height = 16;
            track.style.marginTop = 12;
            track.style.backgroundColor = Line;
            track.style.overflow = Overflow.Hidden;
            Radius(track, 8);

            var fill = new VisualElement();
            fill.style.height = Length.Percent(100);
            fill.style.width = new Length(Mathf.Clamp(percent, 0f, 100f), LengthUnit.Percent);
            fill.style.backgroundColor = barColor;
            Radius(fill, 8);
            track.Add(fill);

            wrap.Add(track);
            return wrap;
        }

        // ---------------- builders ----------------

        private static VisualElement BuildStudentRow(AdminClassroomService.StudentStat s, Action onClick)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = Surface;
            row.style.marginBottom = 16;
            Padding(row, 24, 28);
            Radius(row, 28);
            Border(row, 2, Line);

            row.Add(MakeAvatar(s.Name, 84, s.StudentId));

            var info = new VisualElement();
            info.style.flexGrow = 1;
            info.style.flexShrink = 1;
            info.style.marginLeft = 22;
            info.Add(MakeLabel(string.IsNullOrEmpty(s.Name) ? "Student" : s.Name, 32, FontStyle.Bold, Ink));
            info.Add(MakeLabel($"Level {s.Level}  \u2022  {s.Points:N0} pts", 24, FontStyle.Normal, Muted, 4, 0));
            row.Add(info);

            row.Add(MakeLabel("\u203A", 52, FontStyle.Bold, Muted));

            row.RegisterCallback<ClickEvent>(_ => onClick());
            row.RegisterCallback<PointerDownEvent>(_ => row.style.backgroundColor = AccentSoft);
            row.RegisterCallback<PointerUpEvent>(_ => row.style.backgroundColor = Surface);
            row.RegisterCallback<PointerLeaveEvent>(_ => row.style.backgroundColor = Surface);
            return row;
        }

        private static VisualElement BuildQuizScoreRow(string title, QuizService.StudentAttemptRow r)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = Surface;
            row.style.marginBottom = 12;
            Padding(row, 20, 26);
            Radius(row, 24);

            var info = new VisualElement();
            info.style.flexGrow = 1;
            info.style.flexShrink = 1;
            info.Add(MakeLabel(title, 28, FontStyle.Bold, Ink));

            bool attempted = r != null;
            info.Add(MakeLabel(attempted ? $"{r.ScoreCorrect}/{r.ScoreTotal} correct" : "Not attempted yet",
                23, FontStyle.Normal, Muted, 4, 0));
            row.Add(info);

            var (bg, fg) = ScoreColors(attempted ? r.Percent : 0, attempted);
            var pill = new VisualElement();
            pill.style.backgroundColor = bg;
            pill.style.flexShrink = 0;
            pill.style.marginLeft = 16;
            Padding(pill, 8, 20);
            Radius(pill, 24);
            pill.Add(MakeLabel(attempted ? $"{Mathf.RoundToInt(r.Percent)}%" : "-", 28, FontStyle.Bold, fg));
            row.Add(pill);
            return row;
        }

        private static VisualElement TileRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 16;
            return row;
        }

        private static VisualElement MakeTile(string caption, string value, Color bg, Color fg)
        {
            var tile = new VisualElement();
            tile.style.flexGrow = 1;
            tile.style.flexBasis = 0;
            tile.style.backgroundColor = bg;
            tile.style.marginLeft = tile.style.marginRight = 8;
            Padding(tile, 26, 24);
            Radius(tile, 28);
            tile.Add(MakeLabel(value, 52, FontStyle.Bold, fg));
            tile.Add(MakeLabel(caption, 24, FontStyle.Normal, Muted, 6, 0));
            return tile;
        }

        /// <summary>Round student avatar (profile picture if the student has one, initials
        /// otherwise). Shared with the Student Activity card on the Analytics screen.</summary>
        public static VisualElement CreateAvatar(string name, float size, string studentId = null)
            => MakeAvatar(name, size, studentId);

        private static VisualElement MakeAvatar(string name, float size, string studentId = null)
        {
            var avatar = new VisualElement();
            avatar.style.width = avatar.style.height = size;
            avatar.style.flexShrink = 0;
            avatar.style.backgroundColor = Accent;
            avatar.style.alignItems = Align.Center;
            avatar.style.justifyContent = Justify.Center;
            avatar.style.overflow = Overflow.Hidden;
            Radius(avatar, size / 2f);

            string initial = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();
            var initialLabel = MakeLabel(initial, Mathf.RoundToInt(size * 0.45f), FontStyle.Bold, Color.white);
            avatar.Add(initialLabel);

            // Profile picture (students/{uid}.avatarUrl); initials stay as the fallback.
            StudentAvatarLoader.Apply(avatar, initialLabel, studentId);

            return avatar;
        }

        private static (Color bg, Color fg) ScoreColors(float percent, bool hasScore)
        {
            if (!hasScore) return (Surface, Muted);
            if (percent >= 75f) return (AccentSoft, Accent);
            if (percent >= 50f) return (AmberSoft, Amber);
            return (DangerSoft, Danger);
        }

        // ---------------- style helpers ----------------

        private static Label MakeLabel(string text, int size, FontStyle style, Color color, float top = 0, float bottom = 0)
        {
            var l = new Label(text);
            l.style.fontSize = size;
            l.style.unityFontStyleAndWeight = style;
            l.style.color = color;
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginTop = top;
            l.style.marginBottom = bottom;
            l.style.marginLeft = l.style.marginRight = 0;
            l.style.paddingTop = l.style.paddingBottom = l.style.paddingLeft = l.style.paddingRight = 0;
            return l;
        }

        private static void ResetButton(Button b)
        {
            b.style.marginTop = b.style.marginBottom = b.style.marginLeft = b.style.marginRight = 0;
            b.style.paddingTop = b.style.paddingBottom = b.style.paddingLeft = b.style.paddingRight = 0;
            b.style.borderTopWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = b.style.borderRightWidth = 0;
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
        }

        private static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = e.style.borderTopRightRadius =
                e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = r;
        }

        private static void Border(VisualElement e, float width, Color color)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth = e.style.borderLeftWidth = e.style.borderRightWidth = width;
            e.style.borderTopColor = e.style.borderBottomColor = e.style.borderLeftColor = e.style.borderRightColor = color;
        }

        private static void Padding(VisualElement e, float vertical, float horizontal)
        {
            e.style.paddingTop = e.style.paddingBottom = vertical;
            e.style.paddingLeft = e.style.paddingRight = horizontal;
        }
    }
}
