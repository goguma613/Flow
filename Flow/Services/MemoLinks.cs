using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;

namespace Flow.Services;

// 루틴·할 일과 메모의 연결, 하루가 바뀔 때의 매일 초기화. 순수 규칙이고 UI 패키지를 참조하지 않는다.

/// <summary>연결 메뉴의 메모 한 줄. 제목은 줄 머리 기호를 뗀 것.</summary>
public sealed record MemoChoice(Guid Id, string Title);

public static class MemoLinks
{
    /// <summary>
    /// 연결 메뉴에 놓을 메모들. 메모 탭과 같은 순서(고정 먼저, 그다음 최근).
    /// 제목은 메모 탭처럼 줄 머리 기호(# · 1. · - · [ ])를 뗀 것, 비면 "제목 없음".
    /// 읽지 못한 메모(잠겼거나 덜 받아진 파일)는 제목을 믿을 수 없어 뺀다.
    /// </summary>
    public static IReadOnlyList<MemoChoice> Choices(IEnumerable<MemoSummary> memos)
        => MemoQuery.Filter(memos.Where(m => !m.Unreadable), null, null, _ => null)
            .Select(m => new MemoChoice(m.Id, TitleOf(m)))
            .ToList();

    private static string TitleOf(MemoSummary memo)
    {
        var title = MemoFormat.PlainTitle(memo.Title);
        return string.IsNullOrWhiteSpace(title) ? MemoFormat.Untitled : title;
    }

    /// <summary>
    /// 그 메모를 가리키는 루틴·할 일. "루틴 · 제목" 다음 "할 일 · 제목", 각각 목록 순서.
    /// 지난 날 끝낸 할 일도 연결이 남아 있으면 넣는다.
    /// </summary>
    public static IReadOnlyList<string> LinkedItems(AppData data, Guid memoId)
    {
        var result = new List<string>();
        foreach (var routine in data.Routines.Where(r => r.MemoId == memoId).OrderBy(r => r.Order))
            result.Add("루틴 · " + routine.Title);
        foreach (var task in data.Tasks.Where(t => t.MemoId == memoId).OrderBy(t => t.Order))
            result.Add("할 일 · " + task.Title);
        return result;
    }

    /// <summary>연결이 있고 그 메모가 지금 목록(휴지통 제외)에 있는지. 없으면 📝 를 그리지 않는다(연결 값은 둔다).</summary>
    public static bool IsAlive(Guid? memoId, ISet<Guid> liveIds)
        => memoId is { } id && liveIds.Contains(id);

    /// <summary>
    /// 하루가 바뀐 자리의 매일 초기화. 열린 메모(openId)가 오늘 풀어야 할 매일 초기화 메모일 때만 먼저 저장하게 하고(flush) —
    /// 상관없는 메모를 쓰던 사람을 쓰기 모드에서 빼내지 않게 —, 그래도 쓰는 중으로 들고 있는 메모(held)는 이번에 건너뛴다.
    /// 건너뛴 메모는 결과의 Pending 에 남아 같은 날 다시 해 본다(그때는 flush 없이 ResetDaily 만).
    /// </summary>
    public static DailyResetResult ResetAtRollover(MemoStore store, DateOnly today, Action? flush,
        Func<Guid?>? openId, Func<Guid, bool>? held)
    {
        if (flush is not null && openId?.Invoke() is { } open && store.IsDailyDue(open, today)) flush();
        return store.ResetDaily(today, held);
    }
}
