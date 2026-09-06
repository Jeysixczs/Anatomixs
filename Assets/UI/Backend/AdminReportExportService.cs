using Anatomia3D.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;


namespace Anatomia3D.Backend
{
    /// <summary>
    /// Turns AdminAnalyticsReportsController's in-memory report data into an
    /// actual file on disk:
    ///   - ExportCsv(): a CSV file. Opens directly in Excel/Sheets/Numbers -
    ///     no xlsx library dependency needed for "Export to Excel".
    ///   - ExportPdf(): a minimal hand-built PDF (Helvetica/Helvetica-Bold,
    ///     a colored header banner, ruled section headings, zebra-striped
    ///     tables, footer with page numbers, automatic page breaks). No PDF
    ///     library exists in this project, so this writes raw PDF syntax
    ///     directly rather than pulling in a new dependency for one export
    ///     button. It won't reproduce the on-screen dashboard exactly
    ///     (no charts/cards) - it's a clean, printable tabular report with
    ///     the same information.
    ///
    /// Pure file I/O, no Firebase/UI - same separation
    /// AnatomyPlayModeLocalStorage keeps for its own local file. Files are
    /// written under Application.temporaryCachePath/Reports/ - this class
    /// only needs the file to exist long enough to hand its path to
    /// NativeFilePicker.ExportFile (see AdminAnalyticsReportsController's
    /// OnExportExcelClicked/OnExportPdfClicked), which pops the real
    /// iOS/Android share/save dialog and copies the file to wherever the
    /// teacher picks. Once that hand-off happens the local copy here is
    /// disposable - it's OS-clearable cache, not meant to be a permanent
    /// on-device archive of past reports.
    /// </summary>
    public static class AdminReportExportService
    {
        /// <summary>Everything a report needs, gathered by the caller (e.g.
        /// AdminAnalyticsReportsController) from its own _current* fields plus the raw
        /// overview numbers it received from QuizService.FetchClassroomOverviewStats.</summary>
        [Serializable]
        public class ReportExportData
        {
            public string ClassroomName;
            public string DateRangeLabel;

            public int ActiveUsers;
            public string ActiveUsersDelta;
            public float AvgScorePercent;
            public int QuizzesDone;
            public string QuizzesDoneDelta;
            public int CompletionPercent;
            public string CompletionDelta;

            public List<AdminAnalyticsReportsController.TopPerformer> TopPerformers = new List<AdminAnalyticsReportsController.TopPerformer>();
            public AdminAnalyticsReportsController.StudentActivitySummary StudentActivity;
            public List<AdminAnalyticsReportsController.ScoreTrendEntry> ScoreTrend = new List<AdminAnalyticsReportsController.ScoreTrendEntry>();
            public List<AdminAnalyticsReportsController.TopicPerformanceEntry> TopicPerformance = new List<AdminAnalyticsReportsController.TopicPerformanceEntry>();
            public List<AdminAnalyticsReportsController.MistakeEntry> Mistakes = new List<AdminAnalyticsReportsController.MistakeEntry>();
            public List<AdminAnalyticsReportsController.RecommendationEntry> Recommendations = new List<AdminAnalyticsReportsController.RecommendationEntry>();

            // Quiz Scores section - populated only when the teacher has a quiz selected in
            // the classroom's quiz export picker (AdminAnalyticsReportsController.
            // FetchSelectedQuizScoreRows). QuizScoreQuizTitle null/empty means "no quiz
            // selected" - ExportCsv/ExportPdf both skip the section entirely in that case,
            // rather than printing an empty "Quiz Scores - " heading.
            public string QuizScoreQuizTitle;
            public List<QuizService.StudentQuizScoreEntry> QuizScoreRows = new List<QuizService.StudentQuizScoreEntry>();
        }

        private const string ReportsFolderName = "Reports";

        // Cache, not persistentDataPath - see the class summary above. This is a
        // scratch handoff location for NativeFilePicker.ExportFile, not a
        // permanent archive.
        private static string ReportsDirectory => Path.Combine(Application.temporaryCachePath, ReportsFolderName);

        private static string BuildFileName(ReportExportData data, string extension)
        {
            string classroom = string.IsNullOrEmpty(data.ClassroomName) ? "Classroom" : data.ClassroomName;
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return $"AnatomiaAnalytics_{Sanitize(classroom)}_{timestamp}.{extension}";
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "Report";
        }

        /// <summary>Deletes a file previously returned by ExportCsv/ExportPdf. Call this
        /// once NativeFilePicker.ExportFile's callback fires (success or not) - the
        /// hand-off is done either way, so there's no reason to leave the cache copy
        /// around. Best-effort: a failure here is logged, not thrown, since it only
        /// means a harmless leftover in Application.temporaryCachePath rather than any
        /// loss of data the teacher actually asked for.</summary>
        public static void CleanUpExportedFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AdminReportExportService] Could not delete temp export file '{path}': {e.Message}");
            }
        }

        private static void AppendCsvRow(StringBuilder sb, params string[] fields)
        {
            sb.AppendLine(string.Join(",", fields.Select(EscapeCsvField)));
        }

        private static string EscapeCsvField(string field)
        {
            field = field ?? string.Empty;
            bool needsQuotes = field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r");
            if (!needsQuotes) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        // ==================================================================
        // PDF export (hand-built, no external library)
        //
        // Visual language: a colored header banner (title/classroom/date range
        // + page number) repeated on every page, bold ruled section headings,
        // and zebra-striped, column-aligned tables for anything tabular
        // (Overview, Student Activity, Top Performers, Score Trend, Mistakes,
        // Quiz Scores). Recommendations stay as wrapped prose since they aren't
        // tabular. A footer rule + "generated at" timestamp + page count closes
        // out every page. Still just Helvetica/Helvetica-Bold, ASCII only, no
        // images/compression - drawn with raw PDF path-fill ('re'/'f') and text
        // ('BT'/'Tm'/'Tj') operators so it needs no external PDF library.
        // Non-ASCII characters (e.g. accented names) will render as '?' - swap
        // in a WinAnsiEncoding font if that ever matters.
        // ==================================================================

        private const float PageWidth = 612f;   // US Letter, in points
        private const float PageHeight = 792f;
        private const float MarginX = 48f;
        private const float MarginTop = 60f;
        private const float MarginBottom = 48f;
        private const float BodyFontSize = 10f;
        private const float HeadingFontSize = 12f;
        private const float TableFontSize = 9f;
        private const float LineHeight = 14f;
        private const int MaxCharsPerLine = 95; // rough wrap width for 10pt Helvetica at this margin

        private const float BannerHeight = 46f;
        private const float BannerTopGap = 14f;  // gap between banner and first body line
        private const float FooterHeight = 26f;  // space reserved above the footer rule
        private const float RuleGap = 4f;        // gap between a ruled line's text and its rule

        private static readonly float ContentWidth = PageWidth - (2 * MarginX);

        private readonly struct PdfColor
        {
            public readonly float R, G, B;
            public PdfColor(float r, float g, float b) { R = r; G = g; B = b; }
        }

        private static readonly PdfColor BannerColor = new PdfColor(0.11f, 0.24f, 0.43f);   // navy
        private static readonly PdfColor RuleColor = new PdfColor(0.75f, 0.75f, 0.75f);      // light gray
        private static readonly PdfColor ZebraColor = new PdfColor(0.95f, 0.95f, 0.95f);     // near-white gray
        private static readonly PdfColor WhiteColor = new PdfColor(1f, 1f, 1f);
        private static readonly PdfColor BlackColor = new PdfColor(0f, 0f, 0f);
        private static readonly PdfColor MutedTextColor = new PdfColor(0.45f, 0.45f, 0.45f);

        /// <summary>One line (or table row) of the flattened, paginated report body.
        /// Either Text (a plain/heading line) or Columns (a table header/data row) is
        /// set, never both.</summary>
        private class PdfLine
        {
            public string Text;
            public string[] Columns;
            public float[] ColumnX;
            public float[] ColumnMaxWidth;
            public bool Bold;
            public float FontSize = BodyFontSize;
            public bool Shaded;
            public bool RuleAfter;
            public float SpacingBefore;
        }

        /// <summary>Column layout for one table: header labels plus each column's x
        /// offset and max width (both in points, relative to MarginX).</summary>
        private class TableSpec
        {
            public string[] Headers;
            public float[] ColumnX;
            public float[] ColumnWidth;
        }

        private static readonly TableSpec OverviewTable = new TableSpec
        {
            Headers = new[] { "Metric", "Value" },
            ColumnX = new[] { 0f, 200f },
            ColumnWidth = new[] { 190f, 110f },
        };

        private static readonly TableSpec StudentActivityTable = new TableSpec
        {
            Headers = new[] { "Metric", "Value" },
            ColumnX = new[] { 0f, 220f },
            ColumnWidth = new[] { 210f, 260f },
        };

        private static readonly TableSpec TopPerformersTable = new TableSpec
        {
            Headers = new[] { "#", "Name", "Quizzes", "Points", "Level" },
            ColumnX = new[] { 0f, 34f, 270f, 350f, 430f },
            ColumnWidth = new[] { 30f, 226f, 70f, 70f, 80f },
        };

        private static readonly TableSpec ScoreTrendTable = new TableSpec
        {
            Headers = new[] { "Quiz / Topic", "Avg Score" },
            ColumnX = new[] { 0f, 400f },
            ColumnWidth = new[] { 390f, 116f },
        };

        private static readonly TableSpec MistakesTable = new TableSpec
        {
            Headers = new[] { "Question", "Category", "Errors" },
            ColumnX = new[] { 0f, 320f, 440f },
            ColumnWidth = new[] { 310f, 110f, 76f },
        };

        private static readonly TableSpec QuizScoresTable = new TableSpec
        {
            Headers = new[] { "Student", "Score", "Percent", "Status" },
            ColumnX = new[] { 0f, 230f, 310f, 390f },
            ColumnWidth = new[] { 220f, 70f, 70f, 126f },
        };

        /// <summary>Writes a simple multi-page PDF report. Returns the absolute file path on
        /// success, or null on failure (see the logged error).</summary>
        public static string ExportPdf(ReportExportData data)
        {
            try
            {
                Directory.CreateDirectory(ReportsDirectory);
                string path = Path.Combine(ReportsDirectory, BuildFileName(data, "pdf"));

                var lines = BuildPdfLines(data);
                var pages = PaginateLines(lines);
                byte[] pdfBytes = BuildPdfBytes(pages, data);

                File.WriteAllBytes(path, pdfBytes);
                Debug.Log($"[AdminReportExportService] Wrote PDF report to '{path}'.");
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[AdminReportExportService] Failed to export PDF: {e.Message}");
                return null;
            }
        }

        private static PdfLine Heading(string text, float spacingBefore = 14f) => new PdfLine
        {
            Text = text,
            Bold = true,
            FontSize = HeadingFontSize,
            RuleAfter = true,
            SpacingBefore = spacingBefore,
        };

        private static PdfLine Body(string text, bool bold = false, float spacingBefore = 0f) => new PdfLine
        {
            Text = text,
            Bold = bold,
            FontSize = BodyFontSize,
            SpacingBefore = spacingBefore,
        };

        /// <summary>Appends a bold ruled header row followed by zebra-striped data rows
        /// (or a single "(none)" line when there's no data) for one table section.</summary>
        private static void AddTable(List<PdfLine> lines, TableSpec spec, IEnumerable<string[]> rows)
        {
            lines.Add(new PdfLine
            {
                Columns = spec.Headers,
                ColumnX = spec.ColumnX,
                ColumnMaxWidth = spec.ColumnWidth,
                Bold = true,
                FontSize = TableFontSize,
                RuleAfter = true,
                SpacingBefore = 10f,
            });

            var rowList = rows as IList<string[]> ?? rows.ToList();
            if (rowList.Count == 0)
            {
                lines.Add(Body("  (none)", spacingBefore: 2f));
                return;
            }

            for (int i = 0; i < rowList.Count; i++)
            {
                lines.Add(new PdfLine
                {
                    Columns = rowList[i],
                    ColumnX = spec.ColumnX,
                    ColumnMaxWidth = spec.ColumnWidth,
                    FontSize = TableFontSize,
                    Shaded = i % 2 == 1,
                });
            }
        }

        private static List<PdfLine> BuildPdfLines(ReportExportData data)
        {
            var lines = new List<PdfLine>();

            // Title/classroom/date/generated-at live in the banner and footer now
            // (drawn per-page in BuildPageContentStream), so the body starts
            // straight at the first section.

            lines.Add(Heading("Overview", spacingBefore: 0f));
            AddTable(lines, OverviewTable, new[]
            {
                new[] { "Active Students", data.ActiveUsers.ToString() },
                new[] { "Avg Score", $"{data.AvgScorePercent}%" },
                new[] { "Participated", data.QuizzesDone.ToString() },
                new[] { "Completion", $"{data.CompletionPercent}%" },
            });

            lines.Add(Heading("Student Activity"));
            AddTable(lines, StudentActivityTable, new[]
            {
                new[] { "Total Students", data.StudentActivity.TotalStudents.ToString() },
                new[] { "Active Students", data.StudentActivity.ActiveThisMonth.ToString() },
                new[] { "Average Level", data.StudentActivity.AverageLevel.ToString("0.0", CultureInfo.InvariantCulture) },
                new[] { "Average Points", data.StudentActivity.AveragePoints.ToString() },
            });

            lines.Add(Heading("Top Performers"));
            var performerRows = new List<string[]>();
            for (int i = 0; i < data.TopPerformers.Count; i++)
            {
                var p = data.TopPerformers[i];
                performerRows.Add(new[] { (i + 1).ToString(), p.Name, p.QuizzesCompleted.ToString(), p.Points.ToString(), p.Level.ToString() });
            }
            AddTable(lines, TopPerformersTable, performerRows);

            lines.Add(Heading("Score Trend (by quiz)"));
            var trendRows = new List<string[]>();
            for (int i = 0; i < data.ScoreTrend.Count; i++)
            {
                var s = data.ScoreTrend[i];
                string label = i < data.TopicPerformance.Count ? $"{s.Label} ({data.TopicPerformance[i].Topic})" : s.Label;
                trendRows.Add(new[] { label, $"{s.ScorePercent:0}%" });
            }
            AddTable(lines, ScoreTrendTable, trendRows);

            lines.Add(Heading("Common Incorrect Answers"));
            AddTable(lines, MistakesTable, data.Mistakes.Select(m => new[] { m.Question, m.Category, m.Errors.ToString() }));

            lines.Add(Heading("Recommendations"));
            bool firstRec = true;
            foreach (var r in data.Recommendations)
            {
                lines.Add(Body(r.Title, bold: true, spacingBefore: firstRec ? 8f : 10f));
                firstRec = false;
                foreach (var wrapped in WrapText($"[{r.Topic}] {r.Description}", MaxCharsPerLine))
                    lines.Add(Body("  " + wrapped));
            }

            if (!string.IsNullOrEmpty(data.QuizScoreQuizTitle))
            {
                lines.Add(Heading($"Quiz Scores - {data.QuizScoreQuizTitle}"));
                AddTable(lines, QuizScoresTable, data.QuizScoreRows.Select(row =>
                {
                    string score = row.Attempted ? $"{row.ScoreCorrect}/{row.ScoreTotal}" : "-";
                    string percent = row.Attempted ? $"{row.PercentScore:0}%" : "-";
                    string status = row.Attempted ? "Attempted" : "Not attempted";
                    return new[] { row.StudentName, score, percent, status };
                }));
            }

            return lines;
        }

        private static IEnumerable<string> WrapText(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) { yield return ""; yield break; }

            var words = text.Split(' ');
            var current = new StringBuilder();

            foreach (var word in words)
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }

            if (current.Length > 0) yield return current.ToString();
        }

        /// <summary>Packs lines into pages, honoring each line's SpacingBefore. Usable
        /// height already accounts for the per-page banner and footer chrome, which are
        /// drawn separately in BuildPageContentStream and don't consume body lines.</summary>
        private static List<List<PdfLine>> PaginateLines(List<PdfLine> lines)
        {
            float usableHeight = PageHeight - BannerHeight - BannerTopGap - MarginBottom - FooterHeight;

            var pages = new List<List<PdfLine>>();
            var current = new List<PdfLine>();
            float used = 0f;

            foreach (var line in lines)
            {
                float height = LineHeight + line.SpacingBefore;

                if (used + height > usableHeight && current.Count > 0)
                {
                    pages.Add(current);
                    current = new List<PdfLine>();
                    used = 0f;
                    height = LineHeight; // no spacing-before penalty at the top of a fresh page
                }

                current.Add(line);
                used += height;
            }

            if (current.Count > 0) pages.Add(current);
            if (pages.Count == 0) pages.Add(new List<PdfLine>());
            return pages;
        }

        // Builds the raw PDF byte stream: two Font objects (regular/bold), one Pages
        // object, and one Page + one content-stream object per page, plus the xref
        // table/trailer the PDF spec requires.
        private static byte[] BuildPdfBytes(List<List<PdfLine>> pages, ReportExportData data)
        {
            const int catalogObjNum = 1;
            const int pagesObjNum = 2;
            const int fontRegularObjNum = 3;
            const int fontBoldObjNum = 4;
            int nextObjNum = 5;

            var pageObjNums = new List<int>();
            var contentObjNums = new List<int>();
            var pageContents = new List<string>();

            for (int i = 0; i < pages.Count; i++)
            {
                pageObjNums.Add(nextObjNum++);
                contentObjNums.Add(nextObjNum++);
                pageContents.Add(BuildPageContentStream(pages[i], data, i + 1, pages.Count));
            }

            var objectsByNumber = new SortedDictionary<int, string>
            {
                [catalogObjNum] = $"{catalogObjNum} 0 obj\n<< /Type /Catalog /Pages {pagesObjNum} 0 R >>\nendobj\n",
                [pagesObjNum] = $"{pagesObjNum} 0 obj\n<< /Type /Pages /Kids [{string.Join(" ", pageObjNums.Select(n => $"{n} 0 R"))}] /Count {pageObjNums.Count} >>\nendobj\n",
                [fontRegularObjNum] = $"{fontRegularObjNum} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
                [fontBoldObjNum] = $"{fontBoldObjNum} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>\nendobj\n",
            };

            for (int i = 0; i < pageObjNums.Count; i++)
            {
                int pageNum = pageObjNums[i];
                int contentNum = contentObjNums[i];
                string content = pageContents[i];
                int contentLength = Encoding.ASCII.GetByteCount(content);

                objectsByNumber[pageNum] =
                    $"{pageNum} 0 obj\n" +
                    $"<< /Type /Page /Parent {pagesObjNum} 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] " +
                    $"/Resources << /Font << /F1 {fontRegularObjNum} 0 R /F2 {fontBoldObjNum} 0 R >> >> /Contents {contentNum} 0 R >>\n" +
                    $"endobj\n";

                objectsByNumber[contentNum] =
                    $"{contentNum} 0 obj\n<< /Length {contentLength} >>\nstream\n{content}endstream\nendobj\n";
            }

            var sb = new StringBuilder();
            sb.Append("%PDF-1.4\n");

            var offsets = new List<int>();
            foreach (var kvp in objectsByNumber)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
                sb.Append(kvp.Value);
            }

            int xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
            int totalObjects = objectsByNumber.Count + 1; // +1 for the free-list head (object 0)

            sb.Append($"xref\n0 {totalObjects}\n");
            sb.Append("0000000000 65535 f \n");
            foreach (var offset in offsets)
                sb.Append($"{offset:D10} 00000 n \n");

            sb.Append("trailer\n");
            sb.Append($"<< /Size {totalObjects} /Root {catalogObjNum} 0 R >>\n");
            sb.Append("startxref\n");
            sb.Append(xrefOffset);
            sb.Append("\n%%EOF");

            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>Draws one page: the banner (title/classroom/date/page number), the
        /// body lines (with zebra-striped rows and ruled headings), and the footer
        /// (rule + generated timestamp + page count).</summary>
        private static string BuildPageContentStream(List<PdfLine> lines, ReportExportData data, int pageIndex, int totalPages)
        {
            var sb = new StringBuilder();

            // --- Banner ---
            sb.Append(SetFillColor(BannerColor));
            sb.Append($"0 {F(PageHeight - BannerHeight)} {F(PageWidth)} {F(BannerHeight)} re f\n");

            sb.Append(SetFillColor(WhiteColor));
            sb.Append("BT\n");
            sb.Append("/F2 15 Tf\n");
            sb.Append(TmAt(MarginX, PageHeight - (BannerHeight / 2f) - 5f));
            sb.Append($"({EscapePdfString("Anatomia3D Analytics Report")}) Tj\n");

            sb.Append("/F1 9 Tf\n");
            string classroomLine = (string.IsNullOrEmpty(data.ClassroomName) ? "All Classrooms" : data.ClassroomName) +
                (string.IsNullOrEmpty(data.DateRangeLabel) ? "" : $"   |   {data.DateRangeLabel}");
            sb.Append(TmAt(MarginX, PageHeight - (BannerHeight / 2f) - 19f));
            sb.Append($"({EscapePdfString(classroomLine)}) Tj\n");

            string pageLabel = $"Page {pageIndex} of {totalPages}";
            float pageLabelWidth = EstimateTextWidth(pageLabel, 9f, false);
            sb.Append(TmAt(PageWidth - MarginX - pageLabelWidth, PageHeight - (BannerHeight / 2f) - 5f));
            sb.Append($"({EscapePdfString(pageLabel)}) Tj\n");
            sb.Append("ET\n");

            // --- Body ---
            float y = PageHeight - BannerHeight - BannerTopGap;
            sb.Append(SetFillColor(BlackColor));

            foreach (var line in lines)
            {
                y -= line.SpacingBefore;

                if (line.Shaded)
                {
                    sb.Append(SetFillColor(ZebraColor));
                    sb.Append($"{F(MarginX - 4f)} {F(y - 3.5f)} {F(ContentWidth + 8f)} {F(LineHeight)} re f\n");
                    sb.Append(SetFillColor(BlackColor));
                }

                sb.Append("BT\n");
                sb.Append(line.Bold ? "/F2 " : "/F1 ");
                sb.Append(F(line.FontSize));
                sb.Append(" Tf\n");

                if (line.Columns != null)
                {
                    for (int c = 0; c < line.Columns.Length; c++)
                    {
                        string cell = TruncateToWidth(line.Columns[c], line.ColumnMaxWidth[c], line.FontSize, line.Bold);
                        sb.Append(TmAt(MarginX + line.ColumnX[c], y));
                        sb.Append($"({EscapePdfString(cell)}) Tj\n");
                    }
                }
                else
                {
                    sb.Append(TmAt(MarginX, y));
                    sb.Append($"({EscapePdfString(line.Text)}) Tj\n");
                }
                sb.Append("ET\n");

                if (line.RuleAfter)
                {
                    sb.Append(SetFillColor(RuleColor));
                    sb.Append($"{F(MarginX)} {F(y - RuleGap)} {F(ContentWidth)} 0.75 re f\n");
                    sb.Append(SetFillColor(BlackColor));
                }

                y -= LineHeight;
            }

            // --- Footer ---
            sb.Append(SetFillColor(RuleColor));
            sb.Append($"{F(MarginX)} {F(MarginBottom - 4f)} {F(ContentWidth)} 0.75 re f\n");

            sb.Append(SetFillColor(MutedTextColor));
            sb.Append("BT\n/F1 8 Tf\n");
            sb.Append(TmAt(MarginX, MarginBottom - 16f));
            sb.Append($"({EscapePdfString($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}")}) Tj\n");

            string pageFooter = $"{pageIndex} / {totalPages}";
            float pageFooterWidth = EstimateTextWidth(pageFooter, 8f, false);
            sb.Append(TmAt(PageWidth - MarginX - pageFooterWidth, MarginBottom - 16f));
            sb.Append($"({EscapePdfString(pageFooter)}) Tj\n");
            sb.Append("ET\n");

            return sb.ToString();
        }

        private static string SetFillColor(PdfColor c) =>
            $"{F(c.R)} {F(c.G)} {F(c.B)} rg\n";

        private static string TmAt(float x, float y) =>
            $"1 0 0 1 {F(x)} {F(y)} Tm\n";

        /// <summary>Formats a float with an invariant '.' decimal separator - required by
        /// the PDF spec regardless of the device's current culture (some locales format
        /// floats with a comma, which would otherwise corrupt the content stream).</summary>
        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Rough Helvetica width estimate (points) for right-aligning short
        /// strings like page numbers. Not exact-glyph precise, but close enough for
        /// layout since no PDF library is available to measure real glyph widths.</summary>
        private static float EstimateTextWidth(string s, float fontSize, bool bold)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            float avgCharWidth = fontSize * (bold ? 0.60f : 0.52f);
            return s.Length * avgCharWidth;
        }

        /// <summary>Truncates a table cell so it doesn't run into the next column, using
        /// the same rough width estimate as EstimateTextWidth.</summary>
        private static string TruncateToWidth(string s, float maxWidthPts, float fontSize, bool bold)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

            float avgCharWidth = fontSize * (bold ? 0.60f : 0.52f);
            int maxChars = Mathf.Max(1, Mathf.FloorToInt(maxWidthPts / avgCharWidth));
            if (s.Length <= maxChars) return s;
            if (maxChars <= 3) return s.Substring(0, maxChars);
            return s.Substring(0, maxChars - 3) + "...";
        }

        private static string EscapePdfString(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        }
    }
}