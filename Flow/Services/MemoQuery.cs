using System;
using System.Collections.Generic;
using System.Linq;

namespace Flow.Services;

// 메모 목록의 검색·태그·정렬. 순수 함수이고 UI 패키지를 참조하지 않는다.

public static class MemoQuery
{
    private const StringComparison Cmp = StringComparison.CurrentCultureIgnoreCase;

    /// <summary>
    /// 검색어를 공백으로 나눈 낱말이 모두 (제목 또는 본문)에 들어 있는 메모. 대소문자는 무시한다.
    /// '#' 로 시작하는 낱말과 tag 인자는 태그 조건이다(앞의 '#' 는 떼고 비교).
    /// 정렬은 고정 먼저, 그다음 Updated 내림차순, 같으면 제목.
    /// bodyOf 는 제목만으로 낱말이 다 맞지 않을 때에만 부른다(파일을 읽을 수 있다).
    /// </summary>
    public static IReadOnlyList<MemoSummary> Filter(IEnumerable<MemoSummary> memos, string? search, string? tag,
        Func<Guid, string?> bodyOf)
    {
        var words = new List<string>();
        var wantedTags = new List<string>();
        foreach (var raw in (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('#'))
            {
                var t = raw.TrimStart('#');
                if (t.Length > 0) wantedTags.Add(t);
            }
            else words.Add(raw);
        }
        var tagArg = (tag ?? "").Trim().TrimStart('#');
        if (tagArg.Length > 0) wantedTags.Add(tagArg);

        var result = new List<MemoSummary>();
        foreach (var m in memos)
        {
            if (!wantedTags.All(t => m.Tags.Any(x => string.Equals(x.TrimStart('#'), t, Cmp)))) continue;

            var rest = words.Where(w => m.Title.IndexOf(w, Cmp) < 0).ToList();
            if (rest.Count > 0)
            {
                var body = bodyOf(m.Id) ?? "";
                if (!rest.All(w => body.IndexOf(w, Cmp) >= 0)) continue;
            }
            result.Add(m);
        }

        return result
            .OrderByDescending(m => m.Pinned)
            .ThenByDescending(m => m.Updated)
            .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>모든 태그(대소문자 무시로 합침). 메모 수 많은 순, 같으면 이름순.</summary>
    public static IReadOnlyList<string> AllTags(IEnumerable<MemoSummary> memos)
    {
        var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var m in memos)
        {
            foreach (var t in m.Tags.Select(x => x.TrimStart('#')).Where(x => x.Length > 0)
                         .Distinct(StringComparer.CurrentCultureIgnoreCase))
            {
                counts[t] = counts.GetValueOrDefault(t) + 1;
                names.TryAdd(t, t);
            }
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => names[kv.Key], StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => names[kv.Key])
            .ToList();
    }
}
