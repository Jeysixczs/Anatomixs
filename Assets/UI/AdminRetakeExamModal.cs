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
    /// Layout: colored header band (title + quiz name + close) / scrolling body /
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
        // ---------------- palette ----------------
        private static readonly Color Ink = new Color(0.13f, 0.15f, 0.14f);
        private static readonly Color Muted = new Color(0.45f, 0.49f, 0.48f);
        private static readonly Color Line = new Color(0.89f, 0.91f, 0.90f);
        private static readonly Color LineStrong = new Color(0.78f, 0.81f, 0.80f);
        private static readonly Color Surface = new Color(0.96f, 0.97f, 0.97f);

        private static readonly Color Accent = new Color(0.13f, 0.55f, 0.42f);
        private static readonly Color AccentSoft = new Color(0.90f, 0.96f, 0.93f);
        private static readonly Color Danger = new Color(0.80f, 0.20f, 0.25f);
        private static readonly Color DangerSoft = new Color(0.99f, 0.92f, 0.93f);
        private static readonly Color Amber = new Color(0.72f, 0.45f, 0.05f);
        private static readonly Color AmberSoft = new Color(1.00f, 0.95f, 0.85f);

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
            overlay.style.backgroundColor = new Color(0.05f, 0.08f, 0.07f, 0.6f);
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
            overlay.Add(card);

            // ---------- header band ----------
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.flexShrink = 0;
            header.style.backgroundColor = Accent;
            Padding(header, 32, 36);

            var iconCircle = new VisualElement();
            iconCircle.style.width = iconCircle.style.height = 88;
            iconCircle.style.flexShrink = 0;
            iconCircle.style.backgroundColor = new Color(1, 1, 1, 0.20f);
            iconCircle.style.alignItems = Align.Center;
            iconCircle.style.justifyContent = Justify.Center;
            Radius(iconCircle, 44);
            iconCircle.Add(MakeLabel("\u21BB", 50, FontStyle.Bold, Color.white));
            header.Add(iconCircle);

            var titles = new VisualElement();
            titles.style.flexGrow = 1;
            titles.style.flexShrink = 1;
            titles.style.marginLeft = 24;
            titles.Add(MakeLabel("Allow Retake", 42, FontStyle.Bold, Color.white));
            titles.Add(MakeLabel(quizTitle, 26, FontStyle.Normal, new Color(1, 1, 1, 0.85f), 4, 0));
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
                    scroll.Add(MakeMessageState("!", DangerSoft, Danger,
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
            Label selectAllMark = null, selectAllCount = null;
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
                scroll.Add(MakeMessageState("\u2713", AccentSoft, Accent,
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

                    var avatar = MakeAvatar(r.StudentName, DangerSoft, Danger);
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
                        soft = AccentSoft; strong = Accent;
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

                    rowEl.Add(MakeAvatar(row.StudentName, soft == Surface ? Line : soft, strong));

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
                Radius(settingsCard, 28);
                Padding(settingsCard, 28, 28);

                // Extra attempts stepper
                var attemptsRow = new VisualElement();
                attemptsRow.style.flexDirection = FlexDirection.Row;
                attemptsRow.style.alignItems = Align.Center;

                var attemptsText = new VisualElement();
                attemptsText.style.flexGrow = 1;
                attemptsText.style.flexShrink = 1;
                attemptsText.Add(MakeLabel("Extra attempts", 30, FontStyle.Bold, Ink));
                attemptsText.Add(MakeLabel("On top of the attempts already used", 24, FontStyle.Normal, Muted, 2, 0));
                attemptsRow.Add(attemptsText);

                var minus = MakeStepButton("\u2212");
                attemptsValueLabel = MakeLabel(extraAttempts.ToString(), 40, FontStyle.Bold, Ink);
                attemptsValueLabel.style.width = 96;
                attemptsValueLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                var plus = MakeStepButton("+");

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
                settingsCard.Add(MakeLabel("Deadline", 30, FontStyle.Bold, Ink));
                settingsCard.Add(MakeLabel("How long students have to finish the retake", 24, FontStyle.Normal, Muted, 2, 16));

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
            Radius(row, 26);
            Border(row, 2, Line);
            return row;
        }

        private static VisualElement MakeCheckBox(out Label mark)
        {
            var box = new VisualElement();
            box.style.width = box.style.height = 52;
            box.style.flexShrink = 0;
            box.style.alignItems = Align.Center;
            box.style.justifyContent = Justify.Center;
            Radius(box, 16);
            Border(box, 3, LineStrong);
            box.style.backgroundColor = Color.white;

            mark = MakeLabel("\u2713", 34, FontStyle.Bold, Color.white);
            mark.style.display = DisplayStyle.None;
            box.Add(mark);
            return box;
        }

        private static void SetCheckVisual(VisualElement row, VisualElement box, Label mark, bool on, Color offRowBackground)
        {
            box.style.backgroundColor = on ? Accent : Color.white;
            SetBorderColor(box, on ? Accent : LineStrong);
            mark.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;

            row.style.backgroundColor = on ? AccentSoft : offRowBackground;
            SetBorderColor(row, on ? Accent : Line);
        }

        private static VisualElement MakeAvatar(string name, Color background, Color textColor)
        {
            string initial = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();

            var avatar = new VisualElement();
            avatar.style.width = avatar.style.height = 76;
            avatar.style.flexShrink = 0;
            avatar.style.backgroundColor = background;
            avatar.style.alignItems = Align.Center;
            avatar.style.justifyContent = Justify.Center;
            Radius(avatar, 38);
            avatar.Add(MakeLabel(initial, 32, FontStyle.Bold, textColor));
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

        /// <param name="count">Pass -1 to omit the count badge.</param>
        private static VisualElement MakeSectionHeader(string title, int count, float top, Color badgeText, Color badgeBackground)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = top;
            row.style.marginBottom = 16;

            row.Add(MakeLabel(title, 32, FontStyle.Bold, Ink));

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
        private static VisualElement MakeMessageState(string glyph, Color circleColor, Color glyphColor, string title, string body)
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
            circle.Add(MakeLabel(glyph, 68, FontStyle.Bold, glyphColor));
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
            Radius(b, 26);
            b.style.backgroundColor = primary ? Accent : Color.white;
            b.style.color = primary ? Color.white : Ink;
            Border(b, primary ? 0 : 2, LineStrong);
            return b;
        }

        private static Button MakeStepButton(string glyph)
        {
            var b = new Button { text = glyph };
            ResetButton(b);
            b.style.width = b.style.height = 76;
            b.style.flexShrink = 0;
            b.style.fontSize = 40;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = Accent;
            b.style.backgroundColor = Color.white;
            Radius(b, 38);
            Border(b, 2, LineStrong);
            return b;
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
