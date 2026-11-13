namespace Qaqh_App.Controls.Markdown;

/// <summary>markdown 块级 AST（轻量子集：标题/段落/围栏代码/列表/引用/分隔线）。</summary>
public abstract record MdBlock;

public sealed record MdHeading(int Level, MdInline Inline) : MdBlock;

public sealed record MdParagraph(MdInline Inline) : MdBlock;

public sealed record MdCodeFence(string Language, string Text) : MdBlock;

public sealed record MdListItem(bool Ordered, int Index, MdInline Inline) : MdBlock;

public sealed record MdQuote(MdInline Inline) : MdBlock;

public sealed record MdRule : MdBlock;

/// <summary>行内 AST：纯文本 + 粗体/斜体/行内代码/链接。</summary>
public abstract record MdInline;

public sealed record MdText(string Value) : MdInline;

public sealed record MdBold(MdInline Inner) : MdInline;

public sealed record MdItalic(MdInline Inner) : MdInline;

public sealed record MdInlineCode(string Code) : MdInline;

public sealed record MdLink(string Text, string Url) : MdInline;

public sealed record MdSequence(IReadOnlyList<MdInline> Items) : MdInline;

/// <summary>
/// 轻量 markdown 解析器（行扫描 + 行内递归下降）。覆盖聊天流出现频率最高的
/// 结构；表格等扩展语法按纯文本段落呈现。
/// </summary>
public static class MarkdownParser
{
    public static IReadOnlyList<MdBlock> ParseBlocks(string markdown)
    {
        var blocks = new List<MdBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph, blocks);
                var language = line[3..].Trim();
                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    body.Add(lines[i]);
                    i++;
                }
                i++; // 收尾 ```（缺失也接受：流式中段）
                blocks.Add(new MdCodeFence(language, string.Join("\n", body)));
                continue;
            }
            var heading = HeadingLevel(line);
            if (heading > 0)
            {
                FlushParagraph(paragraph, blocks);
                blocks.Add(new MdHeading(heading, ParseInline(line[heading..].TrimStart())));
                i++;
                continue;
            }
            if (line.TrimEnd() is "---" or "***" or "___")
            {
                FlushParagraph(paragraph, blocks);
                blocks.Add(new MdRule());
                i++;
                continue;
            }
            if (line.StartsWith('>') && (line.Length == 1 || line[1] is ' ' or '>'))
            {
                FlushParagraph(paragraph, blocks);
                var quote = new List<string> { line[1..].TrimStart() };
                i++;
                while (i < lines.Length && lines[i].StartsWith('>') && (lines[i].Length == 1 || lines[i][1] is ' ' or '>'))
                {
                    quote.Add(lines[i][1..].TrimStart());
                    i++;
                }
                blocks.Add(new MdQuote(ParseInline(string.Join(" ", quote.Where(q => q.Length > 0)))));
                continue;
            }
            if (TryListMarker(line, out var ordered, out var index, out var content))
            {
                FlushParagraph(paragraph, blocks);
                blocks.Add(new MdListItem(ordered, index, ParseInline(content)));
                i++;
                // 连续列表项归并
                while (i < lines.Length && TryListMarker(lines[i], out ordered, out index, out content))
                {
                    blocks.Add(new MdListItem(ordered, index, ParseInline(content)));
                    i++;
                }
                continue;
            }
            if (line.Trim().Length == 0)
            {
                FlushParagraph(paragraph, blocks);
                i++;
                continue;
            }
            paragraph.Add(line);
            i++;
        }
        FlushParagraph(paragraph, blocks);
        return blocks;
    }

    private static void FlushParagraph(List<string> lines, List<MdBlock> blocks)
    {
        if (lines.Count == 0)
        {
            return;
        }
        var inline = ParseInline(string.Join('\n', lines).Trim());
        blocks.Add(new MdParagraph(inline));
        lines.Clear();
    }

    private static int HeadingLevel(string line)
    {
        if (line.Length == 0 || line[0] != '#')
        {
            return 0;
        }
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }
        return level <= 6 && (level == line.Length || line[level] == ' ') ? level : 0;
    }

    private static bool TryListMarker(string line, out bool ordered, out int index, out string content)
    {
        ordered = false;
        index = 0;
        content = "";
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            content = trimmed[2..];
            return true;
        }
        var dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && trimmed[..dot].All(char.IsAsciiDigit) && trimmed[..dot].Length <= 4)
        {
            ordered = true;
            index = int.Parse(trimmed[..dot]);
            content = trimmed[(dot + 2)..];
            return true;
        }
        return false;
    }

    // ── 行内 ──────────────────────────────────────────────────────────

    public static MdInline ParseInline(string text)
    {
        var items = ParseInlineRange(text, 0, text.Length);
        return items.Count switch
        {
            0 => new MdText(""),
            1 => items[0],
            _ => new MdSequence(items),
        };
    }

    private static List<MdInline> ParseInlineRange(string text, int start, int end)
    {
        var items = new List<MdInline>();
        var plainStart = start;
        for (int i = start; i < end; i++)
        {
            switch (text[i])
            {
                case '`' when FindClosing(text, i + 1, end, '`') is { } codeEnd:
                {
                    PushPlain(items, text, plainStart, i);
                    items.Add(new MdInlineCode(text[(i + 1)..codeEnd]));
                    i = codeEnd;
                    plainStart = i + 1;
                    break;
                }
                case '*' or '_' when i + 1 < end && text[i + 1] == text[i]:
                {
                    if (FindClosing(text, i + 2, end, text[i]) is { } close)
                    {
                        PushPlain(items, text, plainStart, i);
                        items.Add(new MdBold(ParseInline(text[(i + 2)..close])));
                        i = close;
                        plainStart = i + 1;
                    }
                    break;
                }
                case '*' or '_' when IsEmphasisStart(text, i):
                {
                    if (FindClosing(text, i + 1, end, text[i]) is { } close)
                    {
                        PushPlain(items, text, plainStart, i);
                        items.Add(new MdItalic(ParseInline(text[(i + 1)..close])));
                        i = close;
                        plainStart = i + 1;
                    }
                    break;
                }
                case '[' when FindMatching(text, i + 1, end, ']', ']') is { } close
                    && close + 1 < end && text[close + 1] == '('
                    && FindMatching(text, close + 2, end, ')', ')') is { } urlEnd:
                {
                    PushPlain(items, text, plainStart, i);
                    items.Add(new MdLink(text[(i + 1)..close], text[(close + 2)..urlEnd]));
                    i = urlEnd;
                    plainStart = i + 1;
                    break;
                }
            }
        }
        PushPlain(items, text, plainStart, end);
        return items;
    }

    private static void PushPlain(List<MdInline> items, string text, int start, int end)
    {
        if (end > start)
        {
            items.Add(new MdText(text[start..end]));
        }
    }

    private static bool IsEmphasisStart(string text, int i) =>
        // 单字符强调：前一位不能是字母数字（排除 snake_case / a*b 乘号误判），
        // 后一位不能是空白。
        (i == 0 || !(char.IsLetterOrDigit(text[i - 1]) && (i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))))
        && i + 1 < text.Length
        && !char.IsWhiteSpace(text[i + 1]);

    private static int? FindClosing(string text, int from, int end, char marker)
    {
        for (int i = from; i < end; i++)
        {
            if (text[i] == marker)
            {
                if (marker == '`')
                {
                    return i;
                }
                // 强调收尾：其后不能紧跟字母数字（排除 **a*b* 之类的粘连误判）。
                if (i + 1 >= end || !char.IsLetterOrDigit(text[i + 1]))
                {
                    return i;
                }
            }
            else if (text[i] == '\n' && marker != '`')
            {
                return null; // 强调不跨行；行内代码允许跨行
            }
        }
        return null;
    }

    private static int? FindMatching(string text, int from, int end, char open, char close)
    {
        int depth = 0;
        for (int i = from; i < end; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
            {
                if (depth == 0)
                {
                    return i;
                }
                depth--;
            }
        }
        return null;
    }
}
