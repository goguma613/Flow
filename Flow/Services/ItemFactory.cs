using System;
using System.Collections.Generic;
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
    {
        var routine = new Routine
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            Days = parsed.Days,
            Order = order,

            // 시각을 굳이 적었다는 것 자체가 "이 시각이 중요하다"는 뜻이다.
            Time = parsed.DueTime,
            Remind = parsed.DueTime.HasValue
        };

        // "매월 25일 정산" 처럼 문장에서 읽은 반복
        if (parsed.Repeat is { } rule) SetRepeat(routine, rule);

        return routine;
    }

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

    /// <summary>
    /// 빠른 추가를 읽은 것에 루틴 탭의 칩에서 골라 둔 반복을 입힌다. 문장에 반복 말이 없을 때만 —
    /// "매월 25일 정산" · "매일 물" 처럼 문장이 반복을 말하면 문장이 이긴다.
    /// </summary>
    /// <param name="parsed">루틴 탭에서 읽은 결과(assumeRoutine).</param>
    /// <param name="chip">칩에서 골라 둔 반복. 안 골랐으면 null.</param>
    public static QuickAddResult WithChip(QuickAddResult parsed, string input, RepeatRule? chip, DateOnly today)
    {
        if (chip is null || parsed.Repeat is not null) return parsed;

        // "월 운동" 처럼 루틴 탭에서만 읽히는 맨 요일도 문장이 말한 반복이다.
        // 칩을 입히면 요일은 제목에서 떼어진 채 버려진다.
        if (parsed.Days.Count > 0) return parsed;

        // 루틴 탭 밖에서 읽어도 루틴이면 문장에 반복 말(매일 · 매주 · 화목 …)이 있는 것이다
        if (QuickAddParser.Parse(input, today).IsRoutine) return parsed;

        if (RepeatEngine.IsLegacy(chip)) return parsed with { Days = RepeatEngine.LegacyDays(chip) };

        // 칩을 골라 둔 채 날이 넘어갔으면 첫 차례를 오늘 이후로 당긴다 — 만들자마자 "밀림"이 붙으면 안 된다
        var rule = chip.Clone();
        if (rule.Anchor < today && RepeatEngine.Next(rule, today) is { } next) rule.Anchor = next;

        return parsed with { Repeat = rule, Days = new List<DayOfWeek>() };
    }

    /// <summary>
    /// 루틴의 반복을 바꾼다.
    ///
    /// 매일 · 지나가는 매주 요일은 지금까지처럼 data.json 의 Days 만 쓴다(Archived 아님, 규칙 없음).
    /// 그 밖의 반복은 규칙을 달고 Archived 로 표시한다 — 옛 버전이 그 루틴을 건드리지 못하게 하는 표시이고,
    /// 규칙 파일은 저장할 때 RepeatStore 가 쓴다.
    ///
    /// 주기가 달라졌으면 연속 기록은 0 부터 다시 센다(다른 일정이라 이어 셀 수 없다).
    /// '남기기'나 첫 차례만 바꾼 것은 같은 주기로 본다.
    /// </summary>
    public static void SetRepeat(Routine routine, RepeatRule choice)
    {
        // 바꾸기 전의 주기. 반복 정보를 잃은 루틴은 견줄 것이 없으니 '달라짐'으로 친다.
        RepeatRule? before = DayEngine.HasRule(routine) ? routine.Repeat
            : routine.Archived ? null
            : RepeatEngine.FromLegacy(routine.Days);

        var sameCadence = before is not null && RepeatEngine.SameCadence(before, choice);

        if (RepeatEngine.IsLegacy(choice))
        {
            routine.Days = RepeatEngine.LegacyDays(choice);
            routine.Archived = false;
            routine.Repeat = null;
        }
        else
        {
            // 고른 쪽(반복 창)이 뒤에 값을 고쳐도 따라 바뀌지 않게 사본을 단다
            routine.Days = new List<DayOfWeek>();
            routine.Archived = true;
            routine.Repeat = choice.Clone();
        }

        if (!sameCadence)
        {
            routine.Streak = 0;
            routine.StreakSnapshot = 0;
        }
    }

    /// <summary>
    /// 할 일을 루틴으로 옮긴다. Id · 제목 · 연결한 메모 · 알림 시각이 따라간다.
    /// 중요도 · 마감일 · 밀린 일수는 루틴에 없는 값이라 버린다.
    /// </summary>
    public static Routine ToRoutine(TaskItem task, RepeatRule choice, int order)
    {
        var routine = new Routine
        {
            Id = task.Id,
            Title = task.Title,
            MemoId = task.MemoId,
            Order = order,
            Time = task.DueTime,
            Remind = task.Remind && task.DueTime.HasValue
        };

        SetRepeat(routine, choice);
        return routine;
    }

    /// <summary>
    /// 루틴을 할 일로 옮긴다. 날짜 없는 할 일(오늘 화면)이 되고,
    /// 알림 시각이 있으면 마감이 오늘 그 시각인 할 일이 된다.
    /// </summary>
    public static TaskItem ToTask(Routine routine, DateOnly today, int order)
    {
        var task = new TaskItem
        {
            Id = routine.Id,
            Title = routine.Title,
            MemoId = routine.MemoId,
            CreatedDate = today,
            Order = order
        };

        if (routine.Time is { } time)
        {
            task.Due = today;
            task.DueTime = time;
            task.Remind = routine.Remind;
        }

        return task;
    }
}
