using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Flow.Models;

namespace Flow.Services;

/// <summary>쓰기 칸을 고친 결과. 선택이 없으면 SelectionStart == SelectionEnd 가 커서 자리다.</summary>
public readonly record struct MemoEdit(string Text, int SelectionStart, int SelectionEnd)
{
    public int Caret => SelectionEnd;
}

/// <summary>
/// 메모 쓰기 칸의 줄 고치기(서식 단추·Enter 이어 쓰기). 순수 함수이고 UI 를 모른다(시험 대상).
/// 줄 기호는 MemoFormat.Classify 와 같은 규칙으로 읽는다. 원래 줄바꿈(\r\n·\n)은 그대로 두고,
/// 새로 넣는 줄바꿈은 글에 \r\n 이 있으면 \r\n, 아니면 \n 이다.
/// </summary>
public static class MemoEditing
{
    private static readonly Regex Heading2Rx = new(@"^##\s+", RegexOptions.Compiled);
    private static readonly Regex Heading1Rx = new(@"^#\s+", RegexOptions.Compiled);
    private static readonly Regex CheckRx = new(@"^(?<i>[ \t]*)(?<d>-\s+)?\[[ xX]?\]\s?", RegexOptions.Compiled);
    private static readonly Regex NumberedRx = new(@"^(?<i>[ \t]*)(?<n>\d{1,3})\.\s+", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^(?<i>[ \t]*)(?<b>[-*])\s+", RegexOptions.Compiled);
    private static readonly Regex IndentRx = new(@"^[ \t]*", RegexOptions.Compiled);

    // ───────────────────────── 제목칸 · 본문칸 (쓰기 모드)

    /// <summary>
    /// 본문을 쓰기 화면의 제목칸과 본문칸으로 나눈 것. 파일은 그대로 '첫 줄이 제목'이고, 나누기는 화면에서만 한다
    /// (휴대폰 · 예전 메모와 그대로 맞는다). Mark 는 첫 줄 앞의 큰 소제목 기호("# "), Gap 은 제목과 본문 사이 줄바꿈
    /// ("\n" 또는 빈 줄을 둔 "\n\n", 본문이 없으면 ""). Join(Split(x)) == x.
    /// </summary>
    public readonly record struct TitleParts(string Mark, string Title, string Gap, string Rest);

    private static readonly Regex TitleMarkRx = new(@"^#[ \t]+", RegexOptions.Compiled);

    /// <summary>본문(줄바꿈은 \n 으로 맞춘 것)을 제목칸 · 본문칸으로 나눈다.</summary>
    public static TitleParts SplitTitle(string body)
    {
        var at = body.IndexOf('\n');
        var first = at < 0 ? body : body[..at];
        var mark = TitleMarkRx.Match(first) is { Success: true } m ? m.Value : "";
        var title = first[mark.Length..];
        if (at < 0) return new TitleParts(mark, title, "", "");

        var after = body[(at + 1)..];
        return after.StartsWith('\n')
            ? new TitleParts(mark, title, "\n\n", after[1..])
            : new TitleParts(mark, title, "\n", after);
    }

    /// <summary>
    /// 제목칸 · 본문칸을 다시 본문 하나로. 제목칸에 붙여 넣은 줄바꿈은 띄어쓰기로 바꾼다(제목은 한 줄).
    /// 제목만 있던 메모에 본문을 처음 쓰면 사이를 줄바꿈 하나로 잇는다.
    /// </summary>
    public static string JoinTitle(TitleParts parts)
    {
        var title = parts.Title.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        var rest = MemoFormat.Newlines(parts.Rest);
        var gap = rest.Length > 0 && parts.Gap.Length == 0 ? "\n" : parts.Gap;
        return parts.Mark + title + gap + rest;
    }

    /// <summary>줄 머리. Length 는 들여쓰기까지 포함한 기호 전체 길이. 기호가 없으면 Kind 가 Text·Blank 이고 Length 는 들여쓰기.</summary>
    private readonly record struct Head(MemoLineKind Kind, string Indent, int Length, int Number, string Prefix);

    /// <summary>줄 하나(줄바꿈 제외)의 시작 자리와 길이.</summary>
    private readonly record struct Span(int Start, int Length)
    {
        public int End => Start + Length;
    }

    // ───────────────────────── 서식 단추 (쓰기 모드)

    /// <summary>
    /// 커서 줄(선택했으면 걸친 줄들)에 그 종류의 기호를 켜고 끈다.
    /// 고른 줄이 모두 이미 그 종류면 기호를 떼고, 아니면 다른 기호를 이 기호로 바꿔 붙인다.
    /// 여러 줄을 골랐을 때 빈 줄은 건너뛴다. 번호는 위쪽 목록의 다음 번호부터 이어 매긴다.
    /// </summary>
    public static MemoEdit Toggle(string text, int selectionStart, int selectionEnd, MemoLineKind kind)
    {
        text ??= "";
        if (!IsFormat(kind)) return new MemoEdit(text, selectionStart, selectionEnd);

        var s = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var e = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
        var lines = SplitLines(text);

        var first = LineAt(lines, s);
        var last = LineAt(lines, e);
        // 다음 줄 맨 앞까지 고른 것은 그 줄을 고른 것이 아니다.
        if (last > first && e == lines[last].Start) last--;

        var targets = new List<int>();
        for (var i = first; i <= last; i++)
            if (first == last || !string.IsNullOrWhiteSpace(LineText(text, lines[i]))) targets.Add(i);
        if (targets.Count == 0) targets.Add(first);

        var remove = targets.All(i => HeadOf(LineText(text, lines[i])).Kind == kind);
        var number = kind == MemoLineKind.Numbered ? NextNumber(text, lines, targets[0], IndentOf(text, lines[targets[0]])) : 0;

        // 줄마다 머리를 바꾼다: (줄 번호, 옛 머리 길이, 새 머리)
        var changes = new Dictionary<int, (int OldLength, string NewHead)>();
        foreach (var i in targets)
        {
            var head = HeadOf(LineText(text, lines[i]));
            string newHead;
            if (remove) newHead = head.Indent;
            else if (kind is MemoLineKind.Heading1 or MemoLineKind.Heading2) newHead = MarkerOf(kind, 0);
            else newHead = head.Indent + MarkerOf(kind, number++);
            changes[i] = (head.Length, newHead);
        }

        var sb = new StringBuilder(text.Length + 16);
        var newStarts = new int[lines.Count];
        var pos = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // 앞 줄의 줄바꿈까지 그대로 옮긴다.
            sb.Append(text, pos, line.Start - pos);
            newStarts[i] = sb.Length;
            if (changes.TryGetValue(i, out var c))
            {
                sb.Append(c.NewHead);
                sb.Append(text, line.Start + c.OldLength, line.Length - c.OldLength);
            }
            else sb.Append(text, line.Start, line.Length);
            pos = line.End;
        }
        sb.Append(text, pos, text.Length - pos);

        int Map(int at, bool keepLineStart)
        {
            var i = LineAt(lines, at);
            var col = Math.Min(at - lines[i].Start, lines[i].Length);
            if (!changes.TryGetValue(i, out var c)) return newStarts[i] + col;
            // 줄 맨 앞에서 시작한 선택은 새 기호까지 품게 줄 맨 앞에 둔다.
            if (keepLineStart && col == 0) return newStarts[i];
            if (col >= c.OldLength) return newStarts[i] + c.NewHead.Length + (col - c.OldLength);
            // 기호 안에 있던 커서는 새 기호 뒤로.
            return newStarts[i] + c.NewHead.Length;
        }

        var hasSelection = s != e;
        var newStart = Map(s, hasSelection);
        var newEnd = hasSelection ? Map(e, false) : newStart;
        // 원래 방향(뒤에서 앞으로 고른 것)을 지킨다.
        return selectionStart <= selectionEnd
            ? new MemoEdit(sb.ToString(), newStart, newEnd)
            : new MemoEdit(sb.ToString(), newEnd, newStart);
    }

    // ───────────────────────── 서식 단추 (읽기 모드)

    /// <summary>본문 끝에 그 기호로 시작하는 새 줄을 붙이고 커서를 맨 끝에 둔다. 끝이 줄바꿈이 아니면 줄바꿈부터.</summary>
    public static MemoEdit Append(string text, MemoLineKind kind)
    {
        text ??= "";
        if (!IsFormat(kind)) return new MemoEdit(text, text.Length, text.Length);

        var body = text;
        if (body.Length > 0 && body[^1] is not ('\n' or '\r')) body += NewLineOf(text);

        var lines = SplitLines(body);
        var number = kind == MemoLineKind.Numbered ? NextNumber(body, lines, lines.Count - 1, "") : 0;
        var result = body + MarkerOf(kind, number);
        return new MemoEdit(result, result.Length, result.Length);
    }

    // ───────────────────────── Enter 이어 쓰기

    /// <summary>
    /// 체크·번호·점 줄에서 Enter. 커서 뒤 글을 새 줄로 넘기며 같은 기호(번호는 다음 번호, 들여쓰기 그대로)를 붙인다.
    /// 기호만 있고 글이 없는 줄이면 기호를 지워 목록을 끝낸다.
    /// 목록 줄이 아니거나 커서가 기호 앞·안에 있으면 null(보통 줄바꿈).
    /// </summary>
    public static MemoEdit? ContinueList(string text, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        var lines = SplitLines(text);
        var line = lines[LineAt(lines, caret)];
        var lineText = LineText(text, line);
        var head = HeadOf(lineText);
        if (head.Kind is not (MemoLineKind.Check or MemoLineKind.Numbered or MemoLineKind.Bullet)) return null;

        var col = caret - line.Start;
        if (col < head.Length || col > line.Length) return null;

        if (string.IsNullOrWhiteSpace(lineText[head.Length..]))
        {
            // 빈 항목: 기호를 지우고 목록을 끝낸다. 줄은 빈 줄로 남는다.
            var cleared = text[..line.Start] + text[line.End..];
            return new MemoEdit(cleared, line.Start, line.Start);
        }

        // 체크는 빈 칸, 번호는 다음 번호, 점은 같은 점. 들여쓰기는 그대로.
        var next = head.Kind == MemoLineKind.Numbered
            ? head.Indent + Math.Min(head.Number + 1, 999) + ". "
            : head.Prefix;
        var insert = NewLineOf(text) + next;
        var result = text[..caret] + insert + text[caret..];
        var at = caret + insert.Length;
        return new MemoEdit(result, at, at);
    }

    // ───────────────────────── 줄 읽기

    private static bool IsFormat(MemoLineKind kind)
        => kind is MemoLineKind.Heading1 or MemoLineKind.Heading2 or MemoLineKind.Numbered
            or MemoLineKind.Bullet or MemoLineKind.Check;

    private static string MarkerOf(MemoLineKind kind, int number) => kind switch
    {
        MemoLineKind.Heading1 => "# ",
        MemoLineKind.Heading2 => "## ",
        MemoLineKind.Numbered => Math.Max(1, number) + ". ",
        MemoLineKind.Bullet => "- ",
        MemoLineKind.Check => "[ ] ",
        _ => "",
    };

    /// <summary>MemoFormat.Classify 와 같은 순서로 줄 머리를 읽는다(## → # → 체크 → 번호 → 점).</summary>
    private static Head HeadOf(string line)
    {
        var indent = IndentRx.Match(line).Value;
        if (string.IsNullOrWhiteSpace(line)) return new Head(MemoLineKind.Blank, indent, indent.Length, 0, "");

        var h2 = Heading2Rx.Match(line);
        if (h2.Success) return new Head(MemoLineKind.Heading2, "", h2.Length, 0, "## ");
        var h1 = Heading1Rx.Match(line);
        if (h1.Success) return new Head(MemoLineKind.Heading1, "", h1.Length, 0, "# ");

        var c = CheckRx.Match(line);
        if (c.Success)
        {
            // 이어 쓸 때는 빈 칸으로. "- [ ] " 꼴이면 그 꼴을 지킨다.
            var prefix = c.Groups["i"].Value + (c.Groups["d"].Success ? "- " : "") + "[ ] ";
            return new Head(MemoLineKind.Check, c.Groups["i"].Value, c.Length, 0, prefix);
        }

        var n = NumberedRx.Match(line);
        if (n.Success)
            return new Head(MemoLineKind.Numbered, n.Groups["i"].Value, n.Length, int.Parse(n.Groups["n"].Value),
                n.Groups["i"].Value + n.Groups["n"].Value + ". ");

        var b = BulletRx.Match(line);
        if (b.Success)
            return new Head(MemoLineKind.Bullet, b.Groups["i"].Value, b.Length, 0, b.Groups["i"].Value + b.Groups["b"].Value + " ");

        return new Head(MemoLineKind.Text, indent, indent.Length, 0, "");
    }

    /// <summary>
    /// 그 줄 바로 위로 이어진 목록(체크·번호·점 줄)에서 같은 들여쓰기의 번호 줄을 찾아 그다음 번호. 없으면 1.
    /// 빈 줄·글·소제목을 만나면 목록이 끊긴 것으로 본다.
    /// </summary>
    private static int NextNumber(string text, List<Span> lines, int index, string indent)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var head = HeadOf(LineText(text, lines[i]));
            if (head.Kind is not (MemoLineKind.Check or MemoLineKind.Numbered or MemoLineKind.Bullet)) break;
            if (head.Kind == MemoLineKind.Numbered && head.Indent == indent) return Math.Min(head.Number + 1, 999);
        }
        return 1;
    }

    private static string IndentOf(string text, Span line)
    {
        var head = HeadOf(LineText(text, line));
        return head.Kind is MemoLineKind.Heading1 or MemoLineKind.Heading2 ? "" : head.Indent;
    }

    private static string NewLineOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    private static string LineText(string text, Span line) => text.Substring(line.Start, line.Length);

    /// <summary>줄바꿈(\r\n·\n·\r)으로 나눈 줄들. 글이 비어도 줄 하나는 있다.</summary>
    private static List<Span> SplitLines(string text)
    {
        var list = new List<Span>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('\r' or '\n')) continue;
            list.Add(new Span(start, i - start));
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        list.Add(new Span(start, text.Length - start));
        return list;
    }

    /// <summary>그 자리가 들어 있는 줄. 줄바꿈 위의 자리는 그 앞 줄로 친다.</summary>
    private static int LineAt(List<Span> lines, int pos)
    {
        var lo = 0;
        var hi = lines.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lines[mid].Start <= pos) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}
