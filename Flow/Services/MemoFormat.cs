using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 메모 글 ↔ 메모 모양을 오가는 순수 함수 모음. 파일·시계·UI 를 모른다.
/// 본문은 원래 줄바꿈을 그대로 둔다. 같은 글인지 가릴 때만 RevOf 가 줄바꿈을 맞춘다.
/// </summary>
public static class MemoFormat
{
    public const string Untitled = "제목 없음";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly Regex LineBreak = new(@"(\r\n|\n|\r)", RegexOptions.Compiled);
    private static readonly Regex TagRx = new(@"(?<=^|\s)#([^\s#]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Heading1Rx = new(@"^#\s+", RegexOptions.Compiled);
    private static readonly Regex Heading2Rx = new(@"^##\s+", RegexOptions.Compiled);
    private static readonly Regex CheckRx = new(@"^(?<pre>(?:-\s+)?\[)(?<c>[ xX])\](?<sp>\s?)", RegexOptions.Compiled);
    private static readonly Regex NumberedRx = new(@"^(?<n>\d{1,3}\.)\s+", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^[-*]\s+", RegexOptions.Compiled);
    private static readonly Regex ReservedRx = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ───────────────────────── 파일 글

    /// <summary>머리말이 없거나 깨졌으면(닫는 --- 없음, id 가 Guid 아님) 전체를 본문으로 보고 fallbackId 를 쓴다.</summary>
    public static MemoDocument Parse(string fileText, Guid fallbackId)
    {
        var none = new MemoDocument(fallbackId, false, false, false, null, null,
            Array.Empty<KeyValuePair<string, string>>(), fileText);

        if (ReadLine(fileText, 0, out var pos) != "---") return none;

        var header = new List<string>();
        while (true)
        {
            if (pos >= fileText.Length) return none;
            var line = ReadLine(fileText, pos, out pos);
            if (line == "---") break;
            header.Add(line);
        }

        Guid? id = null;
        bool pinned = false, daily = false;
        DateOnly? reset = null, deleted = null;
        var extra = new List<KeyValuePair<string, string>>();

        foreach (var line in header)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "id": if (Guid.TryParse(value, out var g)) id = g; break;
                case "pinned": pinned = value == "true"; break;
                case "daily": daily = value == "true"; break;
                case "reset": reset = ParseDate(value); break;
                case "deleted": deleted = ParseDate(value); break;
                default: extra.Add(new KeyValuePair<string, string>(key, value)); break;
            }
        }

        if (id is null) return none;
        return new MemoDocument(id.Value, true, pinned, daily, reset, deleted, extra, fileText[pos..]);
    }

    /// <summary>언제나 머리말을 쓴다. 머리말은 \r\n, 본문은 들어온 그대로.</summary>
    public static string Serialize(MemoDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("---\r\n");
        sb.Append("id: ").Append(doc.Id.ToString("D")).Append("\r\n");
        if (doc.Pinned) sb.Append("pinned: true\r\n");
        if (doc.Daily) sb.Append("daily: true\r\n");
        if (doc.Reset is { } r) sb.Append("reset: ").Append(r.ToString(DateFormat, CultureInfo.InvariantCulture)).Append("\r\n");
        if (doc.Deleted is { } d) sb.Append("deleted: ").Append(d.ToString(DateFormat, CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var kv in doc.Extra) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        sb.Append("---\r\n");
        sb.Append(doc.Body);
        return sb.ToString();
    }

    private static DateOnly? ParseDate(string s)
        => DateOnly.TryParseExact(s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>pos 부터 한 줄을 줄바꿈 없이 돌려주고, next 는 줄바꿈 다음 자리다.</summary>
    private static string ReadLine(string text, int pos, out int next)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' }, pos);
        if (end < 0) { next = text.Length; return text[pos..]; }
        next = end + 1;
        if (text[end] == '\r' && next < text.Length && text[next] == '\n') next++;
        return text[pos..end];
    }

    // ───────────────────────── 본문에서 뽑기

    /// <summary>본문의 첫 비어 있지 않은 줄(앞뒤 공백 제외). 없으면 "제목 없음".</summary>
    public static string TitleOf(string body)
    {
        foreach (var line in LineBreak.Split(body))
            if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
        return Untitled;
    }

    /// <summary>나온 순서, 중복 없이, # 뺀 이름.</summary>
    public static IReadOnlyList<string> TagsOf(string body)
        => TagRx.Matches(body).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>본문을 줄바꿈 \n 으로 맞춘 SHA-256 의 소문자 16진 앞 16자.</summary>
    public static string RevOf(string body)
    {
        var normalized = body.Replace("\r\n", "\n").Replace('\r', '\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    /// <summary>제목 줄 다음 본문을 공백 하나로 접어 앞 80자.</summary>
    public static string PreviewOf(string body)
    {
        var parts = LineBreak.Split(body).Where(p => !LineBreak.IsMatch(p)).ToList();
        var first = parts.FindIndex(p => !string.IsNullOrWhiteSpace(p));
        if (first < 0) return "";
        var rest = string.Join(" ", parts.Skip(first + 1));
        var folded = Regex.Replace(rest, @"\s+", " ").Trim();
        if (folded.Length <= 80) return folded;
        var cut = folded[..80];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    // ───────────────────────── 줄 분류

    public static IReadOnlyList<MemoLine> Lines(string body)
    {
        var result = new List<MemoLine>();
        var parts = LineBreak.Split(body);
        for (var i = 0; i < parts.Length; i += 2)
            result.Add(Classify(i / 2, parts[i]));
        return result;
    }

    public static MemoLine Classify(int index, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new MemoLine(index, MemoLineKind.Blank, "", null, false);

        // ## 를 # 보다 먼저 본다
        var h2 = Heading2Rx.Match(line);
        if (h2.Success) return new MemoLine(index, MemoLineKind.Heading2, line[h2.Length..], null, false);
        var h1 = Heading1Rx.Match(line);
        if (h1.Success) return new MemoLine(index, MemoLineKind.Heading1, line[h1.Length..], null, false);

        var c = CheckRx.Match(line);
        if (c.Success)
            return new MemoLine(index, MemoLineKind.Check, line[c.Length..], null, c.Groups["c"].Value != " ");

        var n = NumberedRx.Match(line);
        if (n.Success) return new MemoLine(index, MemoLineKind.Numbered, line[n.Length..], n.Groups["n"].Value, false);

        var b = BulletRx.Match(line);
        if (b.Success) return new MemoLine(index, MemoLineKind.Bullet, line[b.Length..], null, false);

        return new MemoLine(index, MemoLineKind.Text, line, null, false);
    }

    // ───────────────────────── 체크 줄 고치기 (줄바꿈 문자는 그대로 둔다)

    /// <summary>그 줄이 체크 줄이 아니면 본문 그대로.</summary>
    public static string SetChecked(string body, int lineIndex, bool isChecked)
    {
        var parts = LineBreak.Split(body);
        var at = lineIndex * 2;
        if (lineIndex < 0 || at >= parts.Length) return body;
        var changed = SetMark(parts[at], isChecked);
        if (changed is null) return body;
        parts[at] = changed;
        return string.Concat(parts);
    }

    public static string UncheckAll(string body)
    {
        var parts = LineBreak.Split(body);
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = SetMark(parts[i], false) ?? parts[i];
        return string.Concat(parts);
    }

    private static string? SetMark(string line, bool isChecked)
    {
        var m = CheckRx.Match(line);
        if (!m.Success) return null;
        var c = m.Groups["c"];
        return line[..c.Index] + (isChecked ? "x" : " ") + line[(c.Index + 1)..];
    }

    // ───────────────────────── 이름·id

    /// <summary>제목에서 확장자 없는 파일 이름을 만든다.</summary>
    public static string FileStem(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title)
        {
            if (ch < 32 || "\\/:*?\"<>|".IndexOf(ch) >= 0) continue;
            sb.Append(ch);
        }

        var name = sb.ToString().Trim().Trim('.').Trim();
        if (name.Length > 60)
        {
            name = name[..60];
            if (char.IsHighSurrogate(name[^1])) name = name[..^1];
            name = name.Trim().Trim('.').Trim();
        }

        // 윈도우는 CON.x 도 장치 이름으로 보므로 첫 점 앞까지 검사한다
        var dot = name.IndexOf('.');
        var head = (dot < 0 ? name : name[..dot]).Trim();
        if (name.Length == 0 || ReservedRx.IsMatch(head)) return "메모";
        return name;
    }

    /// <summary>경로(소문자)의 MD5 로 만든 결정적 Guid. 머리말 없는 파일의 임시 id.</summary>
    public static Guid IdFromPath(string relativePath)
    {
        var key = relativePath.Replace('\\', '/').ToLowerInvariant();
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>첫 비어 있지 않은 줄 끝에 붙인다. 본문이 비면 "제목 없음" + suffix.</summary>
    public static string AppendToTitle(string body, string suffix)
    {
        var parts = LineBreak.Split(body);
        for (var i = 0; i < parts.Length; i += 2)
        {
            if (string.IsNullOrWhiteSpace(parts[i])) continue;
            parts[i] += suffix;
            return string.Concat(parts);
        }
        return Untitled + suffix;
    }
}
