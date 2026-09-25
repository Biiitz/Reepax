using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Update;

/// <summary>
/// Converts GitHub release markdown notes / changelogs into a polished,
/// theme-aware WPF FlowDocument with headings, code chips, syntax blocks,
/// bullet/numbered lists, quotes, dividers, and clickable hyperlinks.
/// </summary>
public static class MarkdownDocumentRenderer
{
    private enum InlineType
    {
        BoldItalic,
        Bold,
        Italic,
        Strikethrough,
        InlineCode,
        Link
    }

    private struct InlineMatch
    {
        public int Index;
        public int Length;
        public InlineType Type;
        public string Text;
        public string? Url;
    }

    private static readonly Regex _linkRegex = new(@"\[([^\]]+)\]\((https?:\/\/[^\s\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex _autoLinkRegex = new(@"(?<![\(\[=\w])(https?:\/\/[^\s<>\(\)""\\]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _codeRegex = new(@"(?<!`)`([^`\r\n]+)`(?!`)", RegexOptions.Compiled);
    private static readonly Regex _boldItalicRegex = new(@"(?:\*\*\*|___)(?!\s)(.+?)(?<!\s)(?:\*\*\*|___)", RegexOptions.Compiled);
    private static readonly Regex _boldRegex = new(@"(?:\*\*|__)(?!\s)(.+?)(?<!\s)(?:\*\*|__)", RegexOptions.Compiled);
    private static readonly Regex _italicRegex = new(@"(?<![\*\w])(?:\*|_)(?!\s)(.+?)(?<!\s)(?:\*|_)(?![\*\w])", RegexOptions.Compiled);
    private static readonly Regex _strikeRegex = new(@"~~(?!\s)(.+?)(?<!\s)~~", RegexOptions.Compiled);
    private static readonly Regex _mentionRegex = new(@"(?<=^|[\s\(])@([a-zA-Z0-9\-]+)(?=[\s\)\.,;:!?]|$)", RegexOptions.Compiled);
    private static readonly Regex _issueRegex = new(@"(?<=^|[\s\(])#(\d+)(?=[\s\)\.,;:!?]|$)", RegexOptions.Compiled);
    private static readonly Regex _emojiShortcodeRegex = new(@":([a-zA-Z0-9_\+\-]+):", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> _emojiMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Git, Releases & Development
        ["rocket"] = "🚀",
        ["sparkles"] = "✨",
        ["sparkle"] = "❇️",
        ["bug"] = "🐛",
        ["tada"] = "🎉",
        ["fire"] = "🔥",
        ["zap"] = "⚡",
        ["package"] = "📦",
        ["art"] = "🎨",
        ["memo"] = "📝",
        ["pencil"] = "✏️",
        ["pencil2"] = "✏️",
        ["hammer"] = "🔨",
        ["wrench"] = "🔧",
        ["gear"] = "⚙️",
        ["recycle"] = "♻️",
        ["lock"] = "🔒",
        ["unlock"] = "🔓",
        ["bulb"] = "💡",
        ["warning"] = "⚠️",
        ["construction"] = "🚧",
        ["rotating_light"] = "🚨",
        ["ambulance"] = "🚑",
        ["shield"] = "🛡️",
        ["link"] = "🔗",
        ["bookmark"] = "🔖",
        ["mag"] = "🔍",
        ["mag_right"] = "🔎",
        ["globe_with_meridians"] = "🌐",
        ["page_facing_up"] = "📄",
        ["card_file_box"] = "🗃️",
        ["boom"] = "💥",
        ["collision"] = "💥",

        // Checks, Crosses & Math
        ["white_check_mark"] = "✅",
        ["heavy_check_mark"] = "✔️",
        ["check"] = "✔️",
        ["x"] = "❌",
        ["negative_squared_cross_mark"] = "❎",
        ["heavy_plus_sign"] = "➕",
        ["heavy_minus_sign"] = "➖",

        // Badges, Stars & Arrows
        ["arrow_up"] = "⬆️",
        ["arrow_down"] = "⬇️",
        ["arrow_right"] = "➡️",
        ["arrow_left"] = "⬅️",
        ["fast_forward"] = "⏩",
        ["rewind"] = "⏪",
        ["new"] = "🆕",
        ["free"] = "🆓",
        ["cool"] = "🆒",
        ["ok"] = "🆗",
        ["sos"] = "🆘",
        ["up"] = "🆙",
        ["star"] = "⭐",
        ["star2"] = "🌟",
        ["glowing_star"] = "🌟",
        ["100"] = "💯",

        // Reactions & Gestures
        ["+1"] = "👍",
        ["thumbsup"] = "👍",
        ["-1"] = "👎",
        ["thumbsdown"] = "👎",
        ["clap"] = "👏",
        ["wave"] = "👋",
        ["pray"] = "🙏",
        ["muscle"] = "💪",
        ["ok_hand"] = "👌",
        ["point_up"] = "☝️",
        ["point_down"] = "👇",
        ["point_left"] = "👈",
        ["point_right"] = "👉",
        ["raised_hands"] = "🙌",
        ["eyes"] = "👀",
        ["eye"] = "👁️",

        // Faces & Moods
        ["smile"] = "😄",
        ["smiley"] = "😃",
        ["grinning"] = "😀",
        ["blush"] = "😊",
        ["wink"] = "😉",
        ["heart_eyes"] = "😍",
        ["kissing_heart"] = "😘",
        ["relaxed"] = "☺️",
        ["neutral_face"] = "😐",
        ["expressionless"] = "😑",
        ["unamused"] = "😒",
        ["sweat_smile"] = "😅",
        ["sweat"] = "😓",
        ["disappointed"] = "😞",
        ["worried"] = "😟",
        ["angry"] = "😠",
        ["rage"] = "😡",
        ["cry"] = "😢",
        ["sob"] = "😭",
        ["joy"] = "😂",
        ["rofl"] = "🤣",
        ["sunglasses"] = "😎",
        ["thinking"] = "🤔",
        ["thinking_face"] = "🤔",
        ["confused"] = "😕",
        ["flushed"] = "😳",
        ["scream"] = "😱",
        ["sleeping"] = "😴",
        ["mask"] = "😷",
        ["nerd_face"] = "🤓",
        ["nerd"] = "🤓",
        ["shrug"] = "🤷",
        ["facepalm"] = "🤦",
        ["clown_face"] = "🤡",
        ["zany_face"] = "🤪",
        ["partying_face"] = "🥳",
        ["exploding_head"] = "🤯",
        ["hot_face"] = "🥵",
        ["cold_face"] = "🥶",
        ["pleading_face"] = "🥺",

        // Hearts
        ["heart"] = "❤️",
        ["red_heart"] = "❤️",
        ["green_heart"] = "💚",
        ["blue_heart"] = "💙",
        ["purple_heart"] = "💜",
        ["yellow_heart"] = "💛",
        ["black_heart"] = "🖤",
        ["white_heart"] = "🤍",
        ["broken_heart"] = "💔",
        ["sparkling_heart"] = "💖",

        // Devices & System
        ["computer"] = "🖥️",
        ["desktop_computer"] = "🖥️",
        ["laptop"] = "💻",
        ["iphone"] = "📱",
        ["mobile_phone"] = "📱",
        ["cd"] = "💿",
        ["dvd"] = "📀",
        ["floppy_disk"] = "💾",
        ["camera"] = "📷",
        ["video_camera"] = "📹",
        ["tv"] = "📺",
        ["radio"] = "📻",
        ["key"] = "🔑",
        ["hourglass"] = "⌛",
        ["hourglass_flowing_sand"] = "⏳",
        ["stopwatch"] = "⏱️",
        ["clock"] = "🕒",
        ["calendar"] = "📅",
        ["bar_chart"] = "📊",
        ["chart_with_upwards_trend"] = "📈",
        ["chart_with_downwards_trend"] = "📉",
        ["speech_balloon"] = "💬",
        ["thought_balloon"] = "💭",
        ["mega"] = "📣",
        ["megaphone"] = "📣",
        ["loudspeaker"] = "📢",
        ["triangular_flag_on_post"] = "🚩",
        ["checkered_flag"] = "🏁",
        ["label"] = "🏷️",
        ["moneybag"] = "💰",
        ["gem"] = "💎",
        ["gift"] = "🎁",
        ["balloon"] = "🎈",
        ["crystal_ball"] = "🔮",
        ["books"] = "📚",
        ["book"] = "📖",
        ["closed_book"] = "📕",
        ["open_file_folder"] = "📂",
        ["file_folder"] = "📁",
        ["clipboard"] = "📋",
        ["inbox_tray"] = "📥",
        ["outbox_tray"] = "📤",
        ["heavy_dollar_sign"] = "💲",
        ["wheelchair"] = "♿",
        ["bento"] = "🍱",
        ["bell"] = "🔔",
        ["no_bell"] = "🔕",
        ["pin"] = "📌",
        ["pushpin"] = "📌",
        ["round_pushpin"] = "📍",
        ["paperclip"] = "📎",
        ["scissors"] = "✂️",
        ["skull"] = "💀",
        ["ghost"] = "👻",
        ["alien"] = "👽",
        ["robot"] = "🤖",
        ["poop"] = "💩",
        ["hankey"] = "💩",
        ["coffee"] = "☕",
        ["beer"] = "🍺",
        ["beers"] = "🍻",
        ["tools"] = "🛠️",
        ["scroll"] = "📜",
        ["door"] = "🚪",
        ["green_circle"] = "🟢",
        ["red_circle"] = "🔴",
        ["white_circle"] = "⚪",
        ["black_circle"] = "⚫",
        ["large_blue_circle"] = "🔵",
        ["yellow_circle"] = "🟡",
        ["purple_circle"] = "🟣",
        ["orange_circle"] = "🟠",
        ["soon"] = "🔜",
        ["top"] = "🔝",
        ["end"] = "🔚",
        ["on"] = "🔛",
        ["back"] = "🔙",
        ["test_tube"] = "🧪",
        ["microscope"] = "🔬",
        ["information_source"] = "ℹ️",
        ["question"] = "❓",
        ["grey_question"] = "❔",
        ["grey_exclamation"] = "❕",
        ["exclamation"] = "❗",
        ["bangbang"] = "‼️",
        ["interrobang"] = "⁉️",
        ["speaker"] = "🔈",
        ["sound"] = "🔉",
        ["loud_sound"] = "🔊",
        ["mute"] = "🔇",
        ["battery"] = "🔋",
        ["plug"] = "🔌",
        ["alarm_clock"] = "⏰",
        ["timer_clock"] = "⏲️",
        ["stop_button"] = "⏹️",
        ["play_button"] = "▶️",
        ["pause_button"] = "⏸️",
        ["repeat"] = "🔁",
        ["repeat_one"] = "🔂",
        ["twisted_rightwards_arrows"] = "🔀",
        ["heavy_multiplication_x"] = "✖️",
        ["heavy_division_sign"] = "➗",
        ["arrow_upper_right"] = "↗️",
        ["arrow_lower_right"] = "↘️",
        ["arrow_lower_left"] = "↙️",
        ["arrow_upper_left"] = "↖️",
        ["arrows_counterclockwise"] = "🔄",
        ["arrows_clockwise"] = "🔃",
        ["left_right_arrow"] = "↔️",
        ["arrow_up_down"] = "↕️",
        ["confetti_ball"] = "🎊",
        ["trophy"] = "🏆",
        ["first_place_medal"] = "🥇",
        ["second_place_medal"] = "🥈",
        ["third_place_medal"] = "🥉",
        ["medal_sports"] = "🏅",
        ["reminder_ribbon"] = "🎗️",
        ["flag_white"] = "🏳️",
        ["flag_black"] = "🏴",
        ["rainbow_flag"] = "🏳️‍🌈",
        ["pirate_flag"] = "🏴‍☠️"
    };

    public static readonly FontFamily EmojiFontFamily = new("Segoe UI Emoji, Segoe UI Symbol");

    private static readonly Regex _emojiDetectRegex = new(
        @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF]|[\u2300-\u23FF]|[\u2B50-\u2B55]|[\u2190-\u21FF]|[\u2934-\u2935]|[\u3030\u303D\u3297\u3299\u00A9\u00AE\u203C\u2049\u2122\u2139\u25AA\u25AB\u25B6\u25C0\u25FB-\u25FE]|[\uFE00-\uFE0F]|\u200D|\u20E3)+",
        RegexOptions.Compiled);

    public static string ReplaceEmojiShortcodes(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(':'))
            return text;

        return _emojiShortcodeRegex.Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            if (_emojiMap.TryGetValue(key, out var emoji))
            {
                return emoji;
            }
            return m.Value;
        });
    }

    /// <summary>
    /// Preprocesses and normalizes common HTML tags and emoji shortcodes found in GitHub releases into Markdown equivalents.
    /// </summary>
    public static string NormalizeHtmlTags(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input ?? string.Empty;

        var text = ReplaceEmojiShortcodes(input);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(?:strong|b)>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(?:em|i)>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<kbd>([^<]+)</kbd>", "`$1`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?code>", "`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<summary>(.*?)</summary>", "\n**$1**\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?details>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<a\s+[^>]*href=['""]([^'""]+)['""][^>]*>(.*?)</a>", "[$2]($1)", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);

        return text;
    }

    /// <summary>
    /// Parses a markdown string into a styled WPF FlowDocument.
    /// </summary>
    public static FlowDocument CreateFlowDocument(string? markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(8, 6, 8, 6),
            ColumnWidth = double.PositiveInfinity,
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI, sans-serif"),
            FontSize = 12
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");

        if (string.IsNullOrWhiteSpace(markdown))
        {
            var emptyP = new Paragraph(new Run(Loc.Get("UpdateDialog_NoChangelog")))
            {
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 4)
            };
            emptyP.SetResourceReference(Paragraph.ForegroundProperty, "TextSecondaryBrush");
            doc.Blocks.Add(emptyP);
            return doc;
        }

        var normalized = NormalizeHtmlTags(markdown);
        var lines = normalized.Replace("\r\n", "\n").Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();

            // 1. Fenced code block (``` or ~~~)
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                var fence = trimmed.Substring(0, 3);
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].Trim().StartsWith(fence))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing fence

                doc.Blocks.Add(CreateCodeBlock(string.Join(Environment.NewLine, codeLines)));
                continue;
            }

            // 2. Empty line
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                i++;
                continue;
            }

            // 3. Horizontal rule (---, ***, ___)
            if (Regex.IsMatch(trimmed, @"^(?:-{3,}|\*{3,}|_{3,})$"))
            {
                doc.Blocks.Add(CreateHorizontalRule());
                i++;
                continue;
            }

            // 4. Headings (#, ##, ###, ####, #####, ######)
            var headingMatch = Regex.Match(rawLine, @"^(#{1,6})\s+(.+)$");
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Value.Length;
                string headingText = headingMatch.Groups[2].Value.Trim();
                doc.Blocks.Add(CreateHeading(level, headingText));
                i++;
                continue;
            }

            // 4b. GFM Markdown Table (| col 1 | col 2 |)
            if (trimmed.Contains("|") && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                var tableBlock = ParseTable(lines, ref i);
                if (tableBlock != null)
                {
                    doc.Blocks.Add(tableBlock);
                    continue;
                }
            }

            // 5. Blockquote or GitHub Alert (> ...)
            if (trimmed.StartsWith(">"))
            {
                var quoteLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    var qLine = lines[i].TrimStart();
                    qLine = qLine.StartsWith("> ") ? qLine.Substring(2) : qLine.Substring(1);
                    quoteLines.Add(qLine);
                    i++;
                }

                var fullQuote = string.Join(" ", quoteLines).Trim();
                var alertMatch = Regex.Match(fullQuote, @"^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(.*)$", RegexOptions.IgnoreCase);
                if (alertMatch.Success)
                {
                    string alertType = alertMatch.Groups[1].Value.ToUpperInvariant();
                    string alertContent = alertMatch.Groups[2].Value.Trim();
                    doc.Blocks.Add(CreateGitHubAlert(alertType, alertContent));
                }
                else
                {
                    doc.Blocks.Add(CreateBlockquote(fullQuote));
                }
                continue;
            }

            // 6. List items (bullet or numbered)
            var listMatch = Regex.Match(rawLine, @"^(\s*)(?:([\*\-\+\•])|(\d+)\.)\s+(.+)$");
            if (listMatch.Success)
            {
                int indentSpaces = listMatch.Groups[1].Value.Length;
                int indentLevel = Math.Min(indentSpaces / 2, 4);
                bool isNumbered = listMatch.Groups[3].Success;
                string marker = isNumbered ? listMatch.Groups[3].Value + "." : GetBulletSymbol(indentLevel);
                string itemText = listMatch.Groups[4].Value;

                doc.Blocks.Add(CreateListItemParagraph(indentLevel, marker, itemText));
                i++;
                continue;
            }

            // 7. Normal paragraph (collect lines until another block or blank line)
            var paraLines = new List<string> { rawLine.Trim() };
            i++;
            while (i < lines.Length)
            {
                var nextTrim = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(nextTrim) ||
                    nextTrim.StartsWith("```") ||
                    nextTrim.StartsWith("~~~") ||
                    nextTrim.StartsWith("#") ||
                    nextTrim.StartsWith(">") ||
                    Regex.IsMatch(nextTrim, @"^(?:-{3,}|\*{3,}|_{3,})$") ||
                    Regex.IsMatch(lines[i], @"^(\s*)(?:[\*\-\+\•]|\d+\.)\s+") ||
                    (nextTrim.Contains("|") && i + 1 < lines.Length && IsTableSeparator(lines[i + 1])))
                {
                    break;
                }
                paraLines.Add(nextTrim);
                i++;
            }

            doc.Blocks.Add(CreateParagraph(string.Join(" ", paraLines)));
        }

        return doc;
    }

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.Contains("-") || !trimmed.Contains("|")) return false;
        return Regex.IsMatch(trimmed, @"^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?$");
    }

    private static Table? ParseTable(string[] lines, ref int index)
    {
        var headerLine = lines[index].Trim();
        var separatorLine = lines[index + 1].Trim();

        var headerCols = SplitTableRow(headerLine);
        var separatorCols = SplitTableRow(separatorLine);

        if (headerCols.Count == 0 || separatorCols.Count == 0)
            return null;

        var alignments = new List<TextAlignment>();
        foreach (var sep in separatorCols)
        {
            var s = sep.Trim();
            bool left = s.StartsWith(":");
            bool right = s.EndsWith(":");
            if (left && right)
                alignments.Add(TextAlignment.Center);
            else if (right)
                alignments.Add(TextAlignment.Right);
            else
                alignments.Add(TextAlignment.Left);
        }

        while (alignments.Count < headerCols.Count)
            alignments.Add(TextAlignment.Left);

        index += 2; // skip header and separator

        var dataRows = new List<List<string>>();
        while (index < lines.Length)
        {
            var line = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("|"))
                break;

            dataRows.Add(SplitTableRow(line));
            index++;
        }

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 8),
            BorderThickness = new Thickness(1),
            FontSize = 11.5
        };
        table.SetResourceReference(Table.BorderBrushProperty, "BorderDarkBrush");

        for (int c = 0; c < headerCols.Count; c++)
        {
            table.Columns.Add(new TableColumn());
        }

        var rowGroup = new TableRowGroup();

        // Header Row
        var headerRow = new TableRow();
        headerRow.SetResourceReference(TableRow.BackgroundProperty, "BgDarkBrush");
        for (int c = 0; c < headerCols.Count; c++)
        {
            var cellPara = CreateCellParagraph(headerCols[c], isHeader: true, alignments[c]);
            var cell = new TableCell(cellPara)
            {
                BorderThickness = new Thickness(0, 0, c < headerCols.Count - 1 ? 1 : 0, 1),
                Padding = new Thickness(8, 5, 8, 5)
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "BorderDarkBrush");
            headerRow.Cells.Add(cell);
        }
        rowGroup.Rows.Add(headerRow);

        // Data Rows
        for (int r = 0; r < dataRows.Count; r++)
        {
            var dataRow = new TableRow();
            if (r % 2 == 1)
            {
                dataRow.SetResourceReference(TableRow.BackgroundProperty, "BgCardBrush");
            }

            var rowValues = dataRows[r];
            for (int c = 0; c < headerCols.Count; c++)
            {
                string text = c < rowValues.Count ? rowValues[c] : string.Empty;
                var cellPara = CreateCellParagraph(text, isHeader: false, alignments[c]);
                var cell = new TableCell(cellPara)
                {
                    BorderThickness = new Thickness(0, 0, c < headerCols.Count - 1 ? 1 : 0, r < dataRows.Count - 1 ? 1 : 0),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "BorderDarkBrush");
                dataRow.Cells.Add(cell);
            }
            rowGroup.Rows.Add(dataRow);
        }

        table.RowGroups.Add(rowGroup);
        return table;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        var cols = trimmed.Split('|');
        var result = new List<string>(cols.Length);
        foreach (var col in cols)
        {
            result.Add(col.Trim());
        }
        return result;
    }

    private static Paragraph CreateCellParagraph(string text, bool isHeader, TextAlignment align)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0),
            TextAlignment = align,
            LineHeight = 16
        };
        if (isHeader)
        {
            p.FontWeight = FontWeights.Bold;
            p.SetResourceReference(Paragraph.ForegroundProperty, "TextPrimaryBrush");
        }
        else
        {
            p.SetResourceReference(Paragraph.ForegroundProperty, "TextSecondaryBrush");
        }
        AddFormattedInlines(p.Inlines, text);
        return p;
    }

    private static Paragraph CreateHeading(int level, string text)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, Math.Max(4, 12 - level * 2), 0, 3)
        };

        switch (level)
        {
            case 1:
                p.FontSize = 15;
                p.FontWeight = FontWeights.Bold;
                p.BorderThickness = new Thickness(0, 0, 0, 1);
                p.SetResourceReference(Paragraph.BorderBrushProperty, "BorderDarkBrush");
                p.Padding = new Thickness(0, 0, 0, 4);
                p.SetResourceReference(Paragraph.ForegroundProperty, "TextPrimaryBrush");
                break;
            case 2:
                p.FontSize = 13.5;
                p.FontWeight = FontWeights.SemiBold;
                p.BorderThickness = new Thickness(0, 0, 0, 1);
                p.SetResourceReference(Paragraph.BorderBrushProperty, "BorderDarkBrush");
                p.Padding = new Thickness(0, 0, 0, 3);
                p.SetResourceReference(Paragraph.ForegroundProperty, "TextPrimaryBrush");
                break;
            case 3:
                p.FontSize = 12.5;
                p.FontWeight = FontWeights.SemiBold;
                p.SetResourceReference(Paragraph.ForegroundProperty, "AccentBlueBrush");
                break;
            default:
                p.FontSize = 12;
                p.FontWeight = FontWeights.SemiBold;
                p.SetResourceReference(Paragraph.ForegroundProperty, "TextSecondaryBrush");
                break;
        }

        AddFormattedInlines(p.Inlines, text);
        return p;
    }

    private static Paragraph CreateBlockquote(string text)
    {
        var p = new Paragraph
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 4, 0, 6),
            FontStyle = FontStyles.Italic
        };
        p.SetResourceReference(Paragraph.BorderBrushProperty, "AccentBlueBrush");
        p.SetResourceReference(Paragraph.ForegroundProperty, "TextSecondaryBrush");

        AddFormattedInlines(p.Inlines, text);
        return p;
    }

    private static Paragraph CreateGitHubAlert(string alertType, string content)
    {
        Color alertColor;
        string icon;
        string title;

        switch (alertType)
        {
            case "TIP":
                alertColor = Color.FromRgb(34, 197, 94); // Green
                icon = "💡";
                title = "Tip";
                break;
            case "IMPORTANT":
                alertColor = Color.FromRgb(168, 85, 247); // Purple
                icon = "❗";
                title = "Important";
                break;
            case "WARNING":
                alertColor = Color.FromRgb(234, 179, 8); // Yellow / Amber
                icon = "⚠️";
                title = "Warning";
                break;
            case "CAUTION":
                alertColor = Color.FromRgb(239, 68, 68); // Red
                icon = "🛑";
                title = "Caution";
                break;
            case "NOTE":
            default:
                alertColor = Color.FromRgb(59, 130, 246); // Blue
                icon = "ℹ️";
                title = "Note";
                break;
        }

        var p = new Paragraph
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = new SolidColorBrush(alertColor),
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(0, 4, 0, 6),
            LineHeight = 18
        };

        AppendEmojiAwareRuns(p.Inlines, $"{icon} ", weight: FontWeights.Normal, foreground: new SolidColorBrush(alertColor));
        AppendEmojiAwareRuns(p.Inlines, $"{title}\n", weight: FontWeights.Bold, foreground: new SolidColorBrush(alertColor));

        AddFormattedInlines(p.Inlines, content);
        return p;
    }

    private static Paragraph CreateListItemParagraph(int indentLevel, string marker, string text)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(indentLevel * 16 + 4, 1.5, 0, 1.5),
            LineHeight = 18
        };

        // Check for GitHub task list checkbox: [ ] or [x]
        var taskMatch = Regex.Match(text, @"^\[([ xX])\]\s*(.*)$");
        if (taskMatch.Success)
        {
            bool isChecked = taskMatch.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
            text = taskMatch.Groups[2].Value;

            var checkRun = new Run(isChecked ? "☑ " : "☐ ")
            {
                FontFamily = EmojiFontFamily,
                FontWeight = FontWeights.Normal,
                FontSize = 12.5
            };
            if (isChecked)
            {
                checkRun.SetResourceReference(TextElement.ForegroundProperty, "AccentBlueBrush");
            }
            else
            {
                checkRun.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
            }
            p.Inlines.Add(checkRun);
        }
        else
        {
            var markerRun = new Run(marker + " ")
            {
                FontWeight = FontWeights.SemiBold
            };
            markerRun.SetResourceReference(TextElement.ForegroundProperty, "AccentBlueBrush");
            p.Inlines.Add(markerRun);
        }

        AddFormattedInlines(p.Inlines, text);
        return p;
    }

    private static string GetBulletSymbol(int indentLevel)
    {
        return indentLevel switch
        {
            0 => "•",
            1 => "◦",
            _ => "▪"
        };
    }

    private static Paragraph CreateHorizontalRule()
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            FontSize = 1,
            LineHeight = 1
        };
        p.SetResourceReference(Paragraph.BorderBrushProperty, "BorderDarkBrush");
        return p;
    }

    private static Paragraph CreateCodeBlock(string code)
    {
        var p = new Paragraph
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
            FontSize = 11.5,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 6),
            LineHeight = 16
        };
        p.SetResourceReference(Paragraph.BackgroundProperty, "BgDarkBrush");
        p.SetResourceReference(Paragraph.BorderBrushProperty, "BorderDarkBrush");
        p.SetResourceReference(Paragraph.ForegroundProperty, "TextPrimaryBrush");

        var lines = code.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            p.Inlines.Add(new Run(lines[i]));
            if (i < lines.Length - 1)
            {
                p.Inlines.Add(new LineBreak());
            }
        }

        return p;
    }

    private static Paragraph CreateParagraph(string text)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 4),
            LineHeight = 18
        };
        p.SetResourceReference(Paragraph.ForegroundProperty, "TextPrimaryBrush");
        AddFormattedInlines(p.Inlines, text);
        return p;
    }

    public static void AddFormattedInlines(InlineCollection inlines, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var allMatches = new List<InlineMatch>();

        // 1. Markdown Links [text](url)
        foreach (Match m in _linkRegex.Matches(text))
        {
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.Link,
                Text = m.Groups[1].Value,
                Url = m.Groups[2].Value
            });
        }

        // 2. Inline Code `code`
        foreach (Match m in _codeRegex.Matches(text))
        {
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.InlineCode,
                Text = m.Groups[1].Value
            });
        }

        // 3. Bold + Italic ***text*** or ___text___
        foreach (Match m in _boldItalicRegex.Matches(text))
        {
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.BoldItalic,
                Text = m.Groups[1].Value
            });
        }

        // 4. Bold **text** or __text__
        foreach (Match m in _boldRegex.Matches(text))
        {
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.Bold,
                Text = m.Groups[1].Value
            });
        }

        // 5. Italic *text* or _text_
        foreach (Match m in _italicRegex.Matches(text))
        {
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.Italic,
                Text = m.Groups[1].Value
            });
        }

        // 6. Strikethrough ~~text~~
        foreach (Match m in _strikeRegex.Matches(text))
        {
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.Strikethrough,
                Text = m.Groups[1].Value
            });
        }

        // 7. Autolinks https://...
        foreach (Match m in _autoLinkRegex.Matches(text))
        {
            var rawUrl = m.Groups[1].Value;
            var cleanUrl = rawUrl.TrimEnd('.', ',', ';', ':', '!', '?');
            var length = m.Length - (rawUrl.Length - cleanUrl.Length);
            if (length > 0)
            {
                allMatches.Add(new InlineMatch
                {
                    Index = m.Index,
                    Length = length,
                    Type = InlineType.Link,
                    Text = cleanUrl,
                    Url = cleanUrl
                });
            }
        }

        // 8. GitHub Mentions @username
        foreach (Match m in _mentionRegex.Matches(text))
        {
            var user = m.Groups[1].Value;
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.Link,
                Text = "@" + user,
                Url = "https://github.com/" + user
            });
        }

        // 9. GitHub Issue / PR references #123
        foreach (Match m in _issueRegex.Matches(text))
        {
            var num = m.Groups[1].Value;
            allMatches.Add(new InlineMatch
            {
                Index = m.Index,
                Length = m.Length,
                Type = InlineType.Link,
                Text = "#" + num,
                Url = "https://github.com/Biiitz/Reepax/issues/" + num
            });
        }

        // Sort by index, resolve non-overlapping matches
        var validMatches = new List<InlineMatch>();
        int lastEnd = 0;
        foreach (var match in allMatches.OrderBy(m => m.Index))
        {
            if (match.Index >= lastEnd)
            {
                validMatches.Add(match);
                lastEnd = match.Index + match.Length;
            }
        }

        int cursor = 0;
        foreach (var m in validMatches)
        {
            if (m.Index > cursor)
            {
                AppendEmojiAwareRuns(inlines, text.Substring(cursor, m.Index - cursor), resourceKey: "TextPrimaryBrush");
            }

            switch (m.Type)
            {
                case InlineType.BoldItalic:
                    AppendEmojiAwareRuns(inlines, m.Text, weight: FontWeights.Bold, style: FontStyles.Italic, resourceKey: "TextPrimaryBrush");
                    break;

                case InlineType.Bold:
                    AppendEmojiAwareRuns(inlines, m.Text, weight: FontWeights.Bold, resourceKey: "TextPrimaryBrush");
                    break;

                case InlineType.Italic:
                    AppendEmojiAwareRuns(inlines, m.Text, style: FontStyles.Italic, resourceKey: "TextPrimaryBrush");
                    break;

                case InlineType.Strikethrough:
                    AppendEmojiAwareRuns(inlines, m.Text, decorations: TextDecorations.Strikethrough, resourceKey: "TextSecondaryBrush");
                    break;

                case InlineType.InlineCode:
                    var code = new Run(m.Text)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
                        FontSize = 11.2,
                        FontWeight = FontWeights.SemiBold
                    };
                    code.SetResourceReference(TextElement.ForegroundProperty, "AccentBlueBrush");
                    inlines.Add(code);
                    break;

                case InlineType.Link:
                    if (Uri.TryCreate(m.Url, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink
                        {
                            NavigateUri = uri,
                            Cursor = Cursors.Hand,
                            ToolTip = uri.AbsoluteUri
                        };
                        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBlueBrush");
                        AppendEmojiAwareRuns(link.Inlines, m.Text, resourceKey: "AccentBlueBrush");
                        link.RequestNavigate += (s, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn($"[Markdown] Failed to open link {e.Uri}: {ex.Message}");
                            }
                            e.Handled = true;
                        };
                        inlines.Add(link);
                    }
                    else
                    {
                        AppendEmojiAwareRuns(inlines, m.Text, resourceKey: "TextPrimaryBrush");
                    }
                    break;
            }

            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
        {
            AppendEmojiAwareRuns(inlines, text.Substring(cursor), resourceKey: "TextPrimaryBrush");
        }
    }

    public static void AppendEmojiAwareRuns(
        InlineCollection inlines,
        string text,
        FontWeight? weight = null,
        FontStyle? style = null,
        TextDecorationCollection? decorations = null,
        Brush? foreground = null,
        string? resourceKey = null)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var matches = _emojiDetectRegex.Matches(text);
        if (matches.Count == 0)
        {
            var run = new Run(text);
            if (weight.HasValue) run.FontWeight = weight.Value;
            if (style.HasValue) run.FontStyle = style.Value;
            if (decorations != null) run.TextDecorations = decorations;
            if (foreground != null) run.Foreground = foreground;
            else if (resourceKey != null) run.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
            inlines.Add(run);
            return;
        }

        int lastIdx = 0;
        foreach (Match match in matches)
        {
            if (match.Index > lastIdx)
            {
                var nonEmojiPart = text.Substring(lastIdx, match.Index - lastIdx);
                var textRun = new Run(nonEmojiPart);
                if (weight.HasValue) textRun.FontWeight = weight.Value;
                if (style.HasValue) textRun.FontStyle = style.Value;
                if (decorations != null) textRun.TextDecorations = decorations;
                if (foreground != null) textRun.Foreground = foreground;
                else if (resourceKey != null) textRun.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
                inlines.Add(textRun);
            }

            var emojiPart = match.Value;
            var emojiRun = new Run(emojiPart)
            {
                FontFamily = EmojiFontFamily,
                FontWeight = FontWeights.Normal, // Important: Normal weight guarantees Segoe UI Emoji resolution without bold fallback failure
                FontStyle = FontStyles.Normal
            };
            if (decorations != null) emojiRun.TextDecorations = decorations;
            if (foreground != null) emojiRun.Foreground = foreground;
            else if (resourceKey != null) emojiRun.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
            inlines.Add(emojiRun);

            lastIdx = match.Index + match.Length;
        }

        if (lastIdx < text.Length)
        {
            var remaining = text.Substring(lastIdx);
            var textRun = new Run(remaining);
            if (weight.HasValue) textRun.FontWeight = weight.Value;
            if (style.HasValue) textRun.FontStyle = style.Value;
            if (decorations != null) textRun.TextDecorations = decorations;
            if (foreground != null) textRun.Foreground = foreground;
            else if (resourceKey != null) textRun.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
            inlines.Add(textRun);
        }
    }
}
