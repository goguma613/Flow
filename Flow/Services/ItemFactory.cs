using System;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 빠른 추가로 읽어낸 한 줄을 실제 항목으로 만든다.
/// PC 입력칸과 폰에서 온 추가가 같은 규칙을 따르도록 한곳에 둔다 —
/// 한쪽만 고치면 "PC로 적으면 알림이 붙는데 폰으로 적으면 안 붙는" 식으로 어긋난다.
/// </summary>
public static class ItemFactory
{
    public static Routine CreateRoutine(QuickAddResult parsed, string title, int order, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            Days = parsed.Days,
            Order = order,

            // 시각을 굳이 적었다는 것 자체가 "이 시각이 중요하다"는 뜻이다.
            Time = parsed.DueTime,
            Remind = parsed.DueTime.HasValue
        };

    public static TaskItem CreateTask(QuickAddResult parsed, string title, DateOnly? due, DateOnly created,
        int order, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            Priority = parsed.Priority,
            Due = due,
            DueTime = parsed.DueTime,
            Remind = parsed.DueTime.HasValue,
            CreatedDate = created,
            Order = order
        };

    /// <summary>파서가 제목을 다 먹어 버렸으면(예: "내일 3시") 적은 그대로를 제목으로 쓴다.</summary>
    public static string TitleOf(QuickAddResult parsed, string input)
        => string.IsNullOrWhiteSpace(parsed.Title) ? input : parsed.Title;
}
