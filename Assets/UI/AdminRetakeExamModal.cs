using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    /// <summary>
    /// "Allow Retake" dialog for one quiz in one classroom. Built entirely in code
    /// with inline styles (no UXML/USS changes needed). Opened from the Quizzes tab of
    /// AdminClassroomDetailController.
    ///
    /// Styled to match the student-side "Quiet Glass" theme (AnatomiaTheme.uss tokens:
    /// purple accent, ink neutrals, hairline borders, soft semantic tints, pill buttons,
    /// 40px card corners). No emoji/text glyphs: every symbol is a PNG from Assets/UI/Icons,
    /// applied through the .rt-icon-* classes in AdminClassroomDetail.uss (this modal is
    /// hosted on the Admin Classroom Detail screen) and tinted from code.
    ///
    /// Layout: white header (icon tile + title + quiz name + close chip) / scrolling body /
    /// sticky footer (status message + Cancel + Allow Retake).
    ///
    /// Body:
    ///  - "Needs a retake": ONLY students QuizService.FetchRetakeStatuses says failed the
    ///    quiz with 0 attempts remaining. Each is a tappable card with a check box, avatar,
    ///    attempts summary and a red score pill. A "Select all" row sits above them.
    ///  - "Retake status": read-only cards for students who already have a retake
    ///    (Assigned / In progress / Passed + score). Everyone else is not shown.
    ///  - "Retake settings" (only when someone can be selected): +/- stepper for extra
    ///    attempts, deadline chips (3 / 7 / 14 / 30 days / None) and a live summary line.
    ///
    /// Public API is unchanged: AdminRetakeExamModal.Show(host, classroomId, quizId, quizTitle, onChanged).
    /// </summary>
    public static class AdminRetakeExamModal
    {
        // ---------------- palette (mirrors the student theme tokens) ----------------
        private static readonly Color Ink = new Color(22 / 255f, 24 / 255f, 38 / 255f);               // --ink
        private static readonly Color Muted = new Color(84 / 255f, 90 / 255f, 110 / 255f);            // --ink-2
        private static readonly Color Faint = new Color(139 / 255f, 145 / 255f, 163 / 255f);          // --ink-3
        private static readonly Color Line = new Color(22 / 255f, 24 / 255f, 38 / 255f, 0.07f);       // --line
        private static readonly Color LineStrong = new Color(22 / 255f, 24 / 255f, 38 / 255f, 0.13f); // --line-strong
        private static readonly Color Surface = new Color(22 / 255f, 24 / 255f, 38 / 255f, 0.045f);   // --sunken

        private static readonly Color Accent = new Color(91 / 255f, 76 / 255f, 219 / 255f);           // --accent
        private static readonly Color AccentSoft = new Color(91 / 255f, 76 / 255f, 219 / 255f, 0.11f);// --accent-soft
        private static readonly Color Success = new Color(28 / 255f, 135 / 255f, 94 / 255f);          // --success
        private static readonly Color SuccessSoft = new Color(28 / 255f, 135 / 255f, 94 / 255f, 0.12f);
        private static readonly Color Danger = new Color(204 / 255f, 64 / 255f, 64 / 255f);           // --danger
        private static readonly Color DangerSoft = new Color(204 / 255f, 64 / 255f, 64 / 255f, 0.09f);
        private static readonly Color Amber = new Color(190 / 255f, 126 / 255f, 24 / 255f);           // --warn
        private static readonly Color AmberSoft = new Color(190 / 255f, 126 / 255f, 24 / 255f, 0.13f);

        private const int MinExtraAttempts = 1;
        private const int MaxExtraAttempts = 10;
        private const int DefaultExtraAttempts = 1;
        private const int DefaultDeadlineDays = 1;

        /// <param name="host">Element to overlay the dialog on (the screen root).</param>
        /// <param name="onChanged">Called after a retake was created so the caller can refresh.</param>
        public static void Show(VisualElement host, string classroomId, string quizId, string quizTitle, Action onChanged)
        {
            if (host == null || QuizService.Instance == null) return;

            // ---------- backdrop ----------
            var overlay = new VisualElement { name = "retake-exam-overlay" };
            overlay.style.position = Position.Absolute;
            overlay.style.left = overlay.style.top = overlay.style.right = overlay.style.bottom = 0;
            overlay.style.backgroundColor = new Color(22 / 255f, 24 / 255f, 38 / 255f, 0.55f);
            overlay.style.justifyContent = Justify.Center;
            overlay.style.alignItems = Align.Center;

            void Close() => overlay.RemoveFromHierarchy();

            // Tap outside the card to dismiss.
            overlay.RegisterCallback<ClickEvent>(evt => { if (evt.target == overlay) Close(); });

            // ---------- card ----------
            var card = new VisualElement();
            card.style.width = Length.Percent(92);
            card.style.maxHeight = Length.Percent(88);
            card.style.backgroundColor = Color.white;
            card.style.overflow = Overflow.Hidden; // clips the header band to the rounded corners
            Radius(card, 40);
            Border(card, 1, Line);
            overlay.Add(card);

            // ---------- header ----------
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.flexShrink = 0;
            header.style.backgroundColor = Color.white;
            header.style.borderBottomWidth = 2;
            header.style.borderBottomColor = Line;
            Padding(header, 32, 36);

            var iconTile = new VisualElement();
            iconTile.style.width = iconTile.style.height = 88;
            iconTile.style.flexShrink = 0;
            iconTile.style.backgroundColor = AccentSoft;
            iconTile.style.alignItems = Align.Center;
            iconTile.style.justifyContent = Justify.Center;
            Radius(iconTile, 30);
            iconTile.Add(MakeIcon("rt-icon-reset", 46, Accent));
            header.Add(iconTile);

            var titles = new VisualElement();
            titles.style.flexGrow = 1;
            titles.style.flexShrink = 1;
            titles.style.marginLeft = 24;
            titles.Add(MakeLabel("Allow Retake", 42, FontStyle.Bold, Ink));
            titles.Add(MakeLabel(quizTitle, 26, FontStyle.Normal, Faint, 4, 0));
            header.Add(titles);

            var closeButton = new Button(Close);
            ResetButton(closeButton);
            closeButton.style.width = closeButton.style.height = 72;
            closeButton.style.flexShrink = 0;
            closeButton.style.backgroundColor = Surface;
            closeButton.style.justifyContent = Justify.Center;
            closeButton.style.alignItems = Align.Center;
            Radius(closeButton, 36);
            closeButton.Add(MakeIcon("rt-icon-close", 32, Muted));
            header.Add(closeButton);

            card.Add(header);

            // ---------- scrolling body ----------
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;
            scroll.contentContainer.style.paddingLeft = scroll.contentContainer.style.paddingRight = 36;
            scroll.contentContainer.style.paddingTop = 32;
            scroll.contentContainer.style.paddingBottom = 24;
            card.Add(scroll);

            var loading = MakeLabel("Loading students...", 28, FontStyle.Normal, Muted, 24, 24);
            loading.style.unityTextAlign = TextAnchor.MiddleCenter;
            scroll.Add(loading);

            // ---------- sticky footer (filled in once data loads) ----------
            var footer = new VisualElement();
            footer.style.flexShrink = 0;
            footer.style.display = DisplayStyle.None;
            footer.style.backgroundColor = Color.white;
            footer.style.borderTopWidth = 2;
            footer.style.borderTopColor = Line;
            Padding(footer, 24, 36);
            card.Add(footer);

            host.Add(overlay);

            QuizService.Instance.FetchRetakeStatuses(classroomId, quizId, (quiz, rows) =>
            {
                loading.RemoveFromHierarchy();

                if (quiz == null)
                {
                    scroll.Add(MakeMessageState("rt-icon-close", DangerSoft, Danger,
                        "Could not load results",
                        "Something went wrong while loading this quiz's results. Please try again."));

                    var closeOnly = MakePrimaryOrSecondaryButton("Close", false, Close);
                    footer.Add(closeOnly);
                    footer.style.display = DisplayStyle.Flex;
                    return;
                }

                BuildBody(scroll, footer, quiz, rows, classroomId, quizId, Close, onChanged);
            });
        }

        private static void BuildBody(ScrollView scroll, VisualElement footer, QuizService.QuizRecord quiz,
            List<QuizService.RetakeStudentRow> rows, string classroomId, string quizId, Action close, Action onChanged)
        {
            var eligible = rows.Where(r => r.State == QuizService.RetakeState.Eligible).ToList();
            var others = rows.Where(r => r.State != QuizService.RetakeState.Eligible).ToList();

            var selected = new HashSet<string>();
            var setters = new Dictionary<string, Action<bool>>();

            int extraAttempts = DefaultExtraAttempts;
            int deadlineDays = DefaultDeadlineDays; // 0 = no deadline

            var statusLabel = MakeLabel("", 26, FontStyle.Normal, Danger, 0, 14);
            statusLabel.style.display = DisplayStyle.None;

            var createButton = MakePrimaryOrSecondaryButton("Allow Retake", true, null);

            // Elements the selection callbacks need to refresh.
            VisualElement selectAllRow = null, selectAllBox = null;
            VisualElement selectAllMark = null;
            Label selectAllCount = null;
            Label attemptsValueLabel = null, summaryLabel = null;
            var deadlineChips = new List<(Button button, int days)>();

            // ---------- refresh helpers ----------
            void RefreshSettings()
            {
                if (attemptsValueLabel != null) attemptsValueLabel.text = extraAttempts.ToString();

                foreach (var (button, days) in deadlineChips) StyleChip(button, days == deadlineDays);

                if (summaryLabel != null)
                {
                    string who = selected.Count > 0
                        ? $"{selected.Count} selected {(selected.Count == 1 ? "student gets" : "students get")}"
                        : "Each selected student gets";
                    string attemptsText = $"{extraAttempts} extra attempt{(extraAttempts == 1 ? "" : "s")}";
                    string dueText = deadlineDays > 0 ? $", due in {deadlineDays} day{(deadlineDays == 1 ? "" : "s")}" : ", with no deadline";
                    summaryLabel.text = $"{who} {attemptsText}{dueText}. Earlier attempts and best scores stay unchanged.";
                }
            }

            void OnSelectionChanged()
            {
                bool all = eligible.Count > 0 && selected.Count == eligible.Count;
                if (selectAllRow != null) SetCheckVisual(selectAllRow, selectAllBox, selectAllMark, all, Surface);
                if (selectAllCount != null) selectAllCount.text = $"{selected.Count} of {eligible.Count} selected";

                createButton.text = selected.Count > 0 ? $"Allow Retake ({selected.Count})" : "Allow Retake";
                createButton.SetEnabled(selected.Count > 0);
                createButton.style.opacity = selected.Count > 0 ? 1f : 0.4f;

                RefreshSettings();
            }

            // ---------- empty state ----------
            if (eligible.Count == 0 && others.Count == 0)
            {
                scroll.Add(MakeMessageState("rt-icon-check", SuccessSoft, Success,
                    "No retakes needed",
                    "A student appears here only after failing this quiz with no attempts left."));
            }

            // ---------- students who need a retake ----------
            if (eligible.Count > 0)
            {
                scroll.Add(MakeSectionHeader("Needs a retake", eligible.Count, 0, Danger, DangerSoft));

                // Select all
                selectAllRow = MakeRowContainer(Surface);
                selectAllBox = MakeCheckBox(out selectAllMark);
                selectAllRow.Add(selectAllBox);

                var selectAllText = new VisualElement();
                selectAllText.style.flexGrow = 1;
                selectAllText.style.marginLeft = 20;
                selectAllText.Add(MakeLabel("Select all", 30, FontStyle.Bold, Ink));
                selectAllCount = MakeLabel($"0 of {eligible.Count} selected", 24, FontStyle.Normal, Muted, 2, 0);
                selectAllText.Add(selectAllCount);
                selectAllRow.Add(selectAllText);

                selectAllRow.RegisterCallback<ClickEvent>(_ =>
                {
                    bool selectEveryone = selected.Count != eligible.Count;
                    foreach (var kvp in setters) kvp.Value(selectEveryone);
                    OnSelectionChanged();
                });
                scroll.Add(selectAllRow);

                foreach (var row in eligible)
                {
                    var r = row;
                    var rowEl = MakeRowContainer(Color.white);
                    var box = MakeCheckBox(out var mark);
                    rowEl.Add(box);

                    var avatar = MakeAvatar(r.StudentName, DangerSoft, Danger, r.StudentId);
                    avatar.style.marginLeft = 20;
                    rowEl.Add(avatar);

                    var info = new VisualElement();
                    info.style.flexGrow = 1;
                    info.style.flexShrink = 1;
                    info.style.marginLeft = 20;
                    info.Add(MakeLabel(r.StudentName, 30, FontStyle.Bold, Ink));

                    string sub = $"{r.OriginalAttemptsUsed}/{r.AttemptLimit} attempts used";
                    if (r.RetakesGiven > 0) sub += " \u00B7 failed previous retake";
                    info.Add(MakeLabel(sub, 24, FontStyle.Normal, Muted, 2, 0));
                    rowEl.Add(info);

                    var scorePill = MakePill($"{r.OriginalBestPercent:0}%", DangerSoft, Danger);
                    scorePill.style.marginLeft = 12;
                    rowEl.Add(scorePill);

                    void Set(bool on)
                    {
                        if (on) selected.Add(r.StudentId); else selected.Remove(r.StudentId);
                        SetCheckVisual(rowEl, box, mark, on, Color.white);
                    }

                    setters[r.StudentId] = Set;
                    rowEl.RegisterCallback<ClickEvent>(_ =>
                    {
                        Set(!selected.Contains(r.StudentId));
                        OnSelectionChanged();
                    });

                    SetCheckVisual(rowEl, box, mark, false, Color.white);
                    scroll.Add(rowEl);
                }
            }

            // ---------- read-only retake status ----------
            if (others.Count > 0)
            {
                scroll.Add(MakeSectionHeader("Retake status", others.Count, eligible.Count > 0 ? 28 : 0, Muted, Surface));

                foreach (var row in others)
                {
                    bool passed = row.State == QuizService.RetakeState.Passed;

                    Color soft, strong;
                    string pillText, sub;

                    if (passed)
                    {
                        soft = SuccessSoft; strong = Success;
                        pillText = $"Passed {row.RetakeBestPercent:0}%";
                        sub = "Retake completed";
                    }
                    else if (row.RetakeStarted)
                    {
                        soft = AmberSoft; strong = Amber;
                        pillText = "In progress";
                        sub = $"Best so far {row.RetakeBestPercent:0}%";
                    }
                    else
                    {
                        soft = Surface; strong = Muted;
                        pillText = "Assigned";
                        sub = "Not started yet";
                    }

                    if (!passed && row.RetakeDeadlineUtc.HasValue)
                        sub += $" \u00B7 due {row.RetakeDeadlineUtc.Value.ToLocalTime().ToString("MMM d, h:mm tt")}";

                    var rowEl = MakeRowContainer(Color.white);
                    rowEl.style.borderTopColor = rowEl.style.borderBottomColor =
                        rowEl.style.borderLeftColor = rowEl.style.borderRightColor = Line;

                    rowEl.Add(MakeAvatar(row.StudentName, soft == Surface ? Line : soft, strong, row.StudentId));

                    var info = new VisualElement();
                    info.style.flexGrow = 1;
                    info.style.flexShrink = 1;
                    info.style.marginLeft = 20;
                    info.Add(MakeLabel(row.StudentName, 30, FontStyle.Bold, Ink));
                    info.Add(MakeLabel(sub, 24, FontStyle.Normal, Muted, 2, 0));
                    rowEl.Add(info);

                    var pill = MakePill(pillText, soft == Surface ? Line : soft, strong);
                    pill.style.marginLeft = 12;
                    rowEl.Add(pill);

                    scroll.Add(rowEl);
                }
            }

            // ---------- retake settings (only when someone can be selected) ----------
            if (eligible.Count > 0)
            {
                scroll.Add(MakeSectionHeader("Retake settings", -1, 28, Muted, Surface));

                var settingsCard = new VisualElement();
                settingsCard.style.backgroundColor = Surface;
                Radius(settingsCard, 32);
                Padding(settingsCard, 28, 28);

                // Extra attempts stepper
                var attemptsRow = new VisualElement();
                attemptsRow.style.flexDirection = FlexDirection.Row;
                attemptsRow.style.alignItems = Align.Center;

                attemptsRow.Add(MakeIconTile("rt-icon-attempt", Accent, AccentSoft));

                var attemptsText = new VisualElement();
                attemptsText.style.flexGrow = 1;
                attemptsText.style.flexShrink = 1;
                attemptsText.style.marginLeft = 20;
                attemptsText.Add(MakeLabel("Extra attempts", 30, FontStyle.Bold, Ink));
                attemptsText.Add(MakeLabel("On top of the attempts already used", 24, FontStyle.Normal, Muted, 2, 0));
                attemptsRow.Add(attemptsText);

                var minus = MakeStepButton(false);
                attemptsValueLabel = MakeLabel(extraAttempts.ToString(), 40, FontStyle.Bold, Ink);
                attemptsValueLabel.style.width = 88;
                attemptsValueLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                var plus = MakeStepButton(true);

                minus.clicked += () => { extraAttempts = Mathf.Max(MinExtraAttempts, extraAttempts - 1); RefreshSettings(); };
                plus.clicked += () => { extraAttempts = Mathf.Min(MaxExtraAttempts, extraAttempts + 1); RefreshSettings(); };

                attemptsRow.Add(minus);
                attemptsRow.Add(attemptsValueLabel);
                attemptsRow.Add(plus);
                settingsCard.Add(attemptsRow);

                // Divider
                var divider = new VisualElement();
                divider.style.height = 2;
                divider.style.backgroundColor = Line;
                divider.style.marginTop = divider.style.marginBottom = 24;
                settingsCard.Add(divider);

                // Deadline chips
                var deadlineHeader = new VisualElement();
                deadlineHeader.style.flexDirection = FlexDirection.Row;
                deadlineHeader.style.alignItems = Align.Center;
                deadlineHeader.style.marginBottom = 20;
                deadlineHeader.Add(MakeIconTile("rt-icon-deadline", Accent, AccentSoft));

                var deadlineText = new VisualElement();
                deadlineText.style.flexGrow = 1;
                deadlineText.style.flexShrink = 1;
                deadlineText.style.marginLeft = 20;
                deadlineText.Add(MakeLabel("Deadline", 30, FontStyle.Bold, Ink));
                deadlineText.Add(MakeLabel("How long students have to finish the retake", 24, FontStyle.Normal, Muted, 2, 0));
                deadlineHeader.Add(deadlineText);
                settingsCard.Add(deadlineHeader);

                var chipRow = new VisualElement();
                chipRow.style.flexDirection = FlexDirection.Row;
                chipRow.style.flexWrap = Wrap.Wrap;

                var options = new (string label, int days)[]
                {
                    ("1 day", 1), ("2 days", 2), ("3 days", 3), ("7 days", 7), ("14 days", 14), ("30 days", 30), ("No deadline", 0),
                };

                foreach (var (label, days) in options)
                {
                    int d = days;
                    var chip = new Button(() => { deadlineDays = d; RefreshSettings(); }) { text = label };
                    ResetButton(chip);
                    chip.style.height = 76;
                    chip.style.fontSize = 26;
                    chip.style.unityFontStyleAndWeight = FontStyle.Bold;
                    chip.style.paddingLeft = chip.style.paddingRight = 32;
                    chip.style.marginRight = 12;
                    chip.style.marginBottom = 12;
                    Radius(chip, 38);
                    deadlineChips.Add((chip, d));
                    chipRow.Add(chip);
                }
                settingsCard.Add(chipRow);

                scroll.Add(settingsCard);

                // Live summary
                summaryLabel = MakeLabel("", 24, FontStyle.Normal, Muted, 16, 0);
                scroll.Add(summaryLabel);
            }

            // ---------- footer ----------
            footer.Add(statusLabel);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;

            var cancel = MakePrimaryOrSecondaryButton(eligible.Count > 0 ? "Cancel" : "Close", false, close);
            cancel.style.flexGrow = 1;
            cancel.style.flexBasis = 0;
            buttons.Add(cancel);

            if (eligible.Count > 0)
            {
                createButton.style.flexGrow = 1.4f;
                createButton.style.flexBasis = 0;
                createButton.style.marginLeft = 16;

                createButton.clicked += () =>
                {
                    statusLabel.text = "";
                    statusLabel.style.display = DisplayStyle.None;

                    var config = new QuizService.RetakeConfig
                    {
                        ExtraAttempts = extraAttempts,
                        IsDeadlineEnabled = deadlineDays > 0,
                        DeadlineUtc = deadlineDays > 0 ? DateTime.UtcNow.AddDays(deadlineDays) : (DateTime?)null
                    };

                    createButton.SetEnabled(false);
                    createButton.text = "Saving...";

                    QuizService.Instance.GrantRetake(classroomId, quizId, selected.ToList(), config, (ok, error) =>
                    {
                        if (!ok)
                        {
                            statusLabel.text = error ?? "Could not save the retake.";
                            statusLabel.style.display = DisplayStyle.Flex;
                            OnSelectionChanged();
                            return;
                        }

                        onChanged?.Invoke();
                        close();
                    });
                };
                buttons.Add(createButton);
            }

            footer.Add(buttons);
            footer.style.display = DisplayStyle.Flex;

            OnSelectionChanged();
        }

        // ---------------- building blocks ----------------

        /// <summary>Big rounded row used for the "Select all" row and every student row.</summary>
        private static VisualElement MakeRowContainer(Color background)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = background;
            row.style.marginBottom = 14;
            Padding(row, 22, 24);
            Radius(row, 30);
            Border(row, 2, Line);
            return row;
        }

        private static VisualElement MakeCheckBox(out VisualElement mark)
        {
            var box = new VisualElement();
            box.style.width = box.style.height = 52;
            box.style.flexShrink = 0;
            box.style.alignItems = Align.Center;
            box.style.justifyContent = Justify.Center;
            Radius(box, 18);
            Border(box, 3, LineStrong);
            box.style.backgroundColor = Color.white;

            mark = MakeIcon("rt-icon-check", 34, Color.white);
            mark.style.display = DisplayStyle.None;
            box.Add(mark);
            return box;
        }

        private static void SetCheckVisual(VisualElement row, VisualElement box, VisualElement mark, bool on, Color offRowBackground)
        {
            box.style.backgroundColor = on ? Accent : Color.white;
            SetBorderColor(box, on ? Accent : LineStrong);
            mark.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;

            row.style.backgroundColor = on ? AccentSoft : offRowBackground;
            SetBorderColor(row, on ? Accent : Line);
        }

        private static VisualElement MakeAvatar(string name, Color background, Color textColor, string studentId = null)
        {
            string initial = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();

            var avatar = new VisualElement();
            avatar.style.width = avatar.style.height = 76;
            avatar.style.flexShrink = 0;
            avatar.style.backgroundColor = background;
            avatar.style.alignItems = Align.Center;
            avatar.style.justifyContent = Justify.Center;
            Radius(avatar, 38);
            var initialLabel = MakeLabel(initial, 32, FontStyle.Bold, textColor);
            avatar.Add(initialLabel);

            // Profile picture (students/{uid}.avatarUrl); initials stay as the fallback.
            StudentAvatarLoader.Apply(avatar, initialLabel, studentId);
            return avatar;
        }

        private static Label MakePill(string text, Color background, Color textColor)
        {
            var pill = MakeLabel(text, 24, FontStyle.Bold, textColor);
            pill.style.backgroundColor = background;
            pill.style.paddingTop = pill.style.paddingBottom = 8;
            pill.style.paddingLeft = pill.style.paddingRight = 20;
            pill.style.flexShrink = 0;
            pill.style.whiteSpace = WhiteSpace.NoWrap;
            pill.style.unityTextAlign = TextAnchor.MiddleCenter;
            Radius(pill, 24);
            return pill;
        }

        /// <summary>Small-caps caption (same look as the student drawer/filter captions) + optional count badge.</summary>
        /// <param name="count">Pass -1 to omit the count badge.</param>
        private static VisualElement MakeSectionHeader(string title, int count, float top, Color badgeText, Color badgeBackground)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = top;
            row.style.marginBottom = 16;

            var caption = MakeLabel(title.ToUpperInvariant(), 24, FontStyle.Bold, Faint);
            caption.style.letterSpacing = 2;
            row.Add(caption);

            if (count >= 0)
            {
                var badge = MakePill(count.ToString(), badgeBackground, badgeText);
                badge.style.marginLeft = 14;
                badge.style.paddingTop = badge.style.paddingBottom = 4;
                row.Add(badge);
            }
            return row;
        }

        /// <summary>Centered icon + title + body text, used for the empty and error states.</summary>
        private static VisualElement MakeMessageState(string iconClass, Color circleColor, Color iconColor, string title, string body)
        {
            var wrap = new VisualElement();
            wrap.style.alignItems = Align.Center;
            Padding(wrap, 40, 24);

            var circle = new VisualElement();
            circle.style.width = circle.style.height = 132;
            circle.style.backgroundColor = circleColor;
            circle.style.alignItems = Align.Center;
            circle.style.justifyContent = Justify.Center;
            Radius(circle, 66);
            circle.Add(MakeIcon(iconClass, 64, iconColor));
            wrap.Add(circle);

            var t = MakeLabel(title, 34, FontStyle.Bold, Ink, 28, 0);
            t.style.unityTextAlign = TextAnchor.MiddleCenter;
            wrap.Add(t);

            var b = MakeLabel(body, 26, FontStyle.Normal, Muted, 10, 0);
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
            wrap.Add(b);
            return wrap;
        }

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

        private static Button MakePrimaryOrSecondaryButton(string text, bool primary, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            ResetButton(b);
            b.style.height = 104;
            b.style.fontSize = 32;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            Radius(b, 52);
            b.style.backgroundColor = primary ? Accent : Color.white;
            b.style.color = primary ? Color.white : Ink;
            Border(b, primary ? 0 : 2, LineStrong);
            b.style.justifyContent = Justify.Center;
            return b;
        }

        /// <summary>Round +/- button. The symbol is drawn from bars (no text glyph, so no font dependence).</summary>
        private static Button MakeStepButton(bool plus)
        {
            var b = new Button();
            ResetButton(b);
            b.style.width = b.style.height = 76;
            b.style.flexShrink = 0;
            b.style.backgroundColor = Color.white;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            Radius(b, 38);
            Border(b, 2, LineStrong);

            var symbol = new VisualElement { pickingMode = PickingMode.Ignore };
            symbol.style.width = symbol.style.height = 30;
            symbol.style.alignItems = Align.Center;
            symbol.style.justifyContent = Justify.Center;

            var horizontal = new VisualElement { pickingMode = PickingMode.Ignore };
            horizontal.style.position = Position.Absolute;
            horizontal.style.width = 30;
            horizontal.style.height = 5;
            horizontal.style.backgroundColor = Accent;
            Radius(horizontal, 3);
            symbol.Add(horizontal);

            if (plus)
            {
                var vertical = new VisualElement { pickingMode = PickingMode.Ignore };
                vertical.style.position = Position.Absolute;
                vertical.style.width = 5;
                vertical.style.height = 30;
                vertical.style.backgroundColor = Accent;
                Radius(vertical, 3);
                symbol.Add(vertical);
            }

            b.Add(symbol);
            return b;
        }

        /// <summary>PNG icon from Assets/UI/Icons. The image comes from the .rt-icon-* classes in
        /// AdminClassroomDetail.uss; size and tint are set here.</summary>
        private static VisualElement MakeIcon(string iconClass, float size, Color tint)
        {
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("rt-icon");
            icon.AddToClassList(iconClass);
            icon.style.width = icon.style.height = size;
            icon.style.flexShrink = 0;
            icon.style.unityBackgroundImageTintColor = tint;
            return icon;
        }

        /// <summary>Soft rounded tile with a centered icon (same treatment as the student drawer items).</summary>
        private static VisualElement MakeIconTile(string iconClass, Color tint, Color background)
        {
            var tile = new VisualElement();
            tile.style.width = tile.style.height = 68;
            tile.style.flexShrink = 0;
            tile.style.backgroundColor = background;
            tile.style.alignItems = Align.Center;
            tile.style.justifyContent = Justify.Center;
            Radius(tile, 22);
            tile.Add(MakeIcon(iconClass, 34, tint));
            return tile;
        }

        private static void StyleChip(Button chip, bool selected)
        {
            chip.style.backgroundColor = selected ? Accent : Color.white;
            chip.style.color = selected ? Color.white : Ink;
            Border(chip, 2, selected ? Accent : LineStrong);
        }

        /// <summary>Unity's default Button carries margins/borders/padding that fight inline sizing.</summary>
        private static void ResetButton(Button b)
        {
            b.style.marginTop = b.style.marginBottom = b.style.marginLeft = b.style.marginRight = 0;
            b.style.paddingTop = b.style.paddingBottom = b.style.paddingLeft = b.style.paddingRight = 0;
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
        }

        // ---------------- style shorthands ----------------

        private static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = e.style.borderTopRightRadius =
                e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = r;
        }

        private static void Border(VisualElement e, float width, Color color)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth = e.style.borderLeftWidth = e.style.borderRightWidth = width;
            SetBorderColor(e, color);
        }

        private static void SetBorderColor(VisualElement e, Color color)
        {
            e.style.borderTopColor = e.style.borderBottomColor = e.style.borderLeftColor = e.style.borderRightColor = color;
        }

        private static void Padding(VisualElement e, float vertical, float horizontal)
        {
            e.style.paddingTop = e.style.paddingBottom = vertical;
            e.style.paddingLeft = e.style.paddingRight = horizontal;
        }
    }
}
