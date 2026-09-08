using System;
using System.Collections.Generic;
using Flow.Models;

namespace Flow.Services;

/// <summary>알려야 할 한 건. 어떻게 보여줄지는 화면 쪽이 정한다.</summary>
/// <param name="ForDate">어느 논리적 날짜 몫인지. 같은 것을 두 번 알리지 않게 하는 열쇠다.</param>
/// <param name="ScheduledAt">원래 울렸어야 할 벽시계 시각.</param>
/// <param name="LateMinutes">얼마나 늦었는지(분). 0이면 정시. 화면에 "10분 지남"으로 쓴다.</param>
public readonly record struct PendingReminder(
    Guid OwnerId,
    bool IsRoutine,
    string Title,
    DateOnly ForDate,
    DateTime ScheduledAt,
    int LateMinutes);

/// <summary>
/// 언제 알려야 하는지를 정하는 규칙. DayEngine 과 같은 규율을 따른다 —
/// 지금 시각을 인자로 받고, Avalonia 를 모르며, 조회와 상태 변경을 분리한다.
///
/// 설계에서 지킨 것 하나: 반복 규칙을 여기에 두지 않는다.
/// 루틴은 Days 가, 할 일은 Due 가 이미 '언제'를 알고 있다.
/// 요일 규칙이 두 군데로 갈라지면 언젠가 반드시 어긋난다.
/// </summary>
public static class ReminderEngine
{
    /// <summary>이 루틴이 그 논리적 날짜에 알릴 시각. 그 날 예정이 아니거나 시각이 없으면 null.</summary>
    public static DateTime? OccurrenceOn(Routine routine, DateOnly date, int dayStartHour)
    {
        if (!routine.Remind || routine.Archived) return null;
        if (routine.Time is not { } time) return null;
        if (!DayEngine.IsScheduled(routine, date)) return null;

        return DayEngine.AtLogicalTime(date, time, dayStartHour);
    }

    /// <summary>이 할 일이 그 논리적 날짜에 알릴 시각. 마감일이 그 날이 아니거나 시각이 없으면 null.</summary>
    public static DateTime? OccurrenceOn(TaskItem task, DateOnly date, int dayStartHour)
    {
        if (!task.Remind) return null;
        if (task.Due != date) return null;
        if (task.DueTime is not { } time) return null;

        return DayEngine.AtLogicalTime(date, time, dayStartHour);
    }

    /// <summary>
    /// 지금 처리해야 할 것 전부. 없으면 빈 목록이고 아무것도 할당하지 않는다 —
    /// 30초마다 도는 자리라 아무 일도 없을 때 값이 싸야 한다.
    /// </summary>
    public static IReadOnlyList<PendingReminder> Pending(AppData data, DateTime now)
    {
        if (!data.Settings.RemindersEnabled) return [];

        var hour = data.Settings.DayStartHour;
        var today = DayEngine.LogicalDate(now, hour);

        // 어제 몫도 본다. 하루가 04시에 넘어가므로 03:50 알림은 04:05 에 아직 유예 안이다.
        var yesterday = today.AddDays(-1);
        var grace = Math.Max(0, data.Settings.MissedGraceMinutes);

        List<PendingReminder>? found = null;

        foreach (var routine in data.Routines)
        {
            // 오늘 이미 해낸 일로 잔소리하지 않는다.
            if (routine.DoneToday) continue;

            // 미뤄 둔 것은 원래 시각을 다시 보지 않는다. 그 몫은 이미 한 번 나왔다.
            // 다만 유예를 한참 넘긴 미루기는 없는 셈 치고 평소 규칙으로 넘어간다 —
            // 그러지 않으면 어제 미뤄 둔 것이 오늘 알림을 통째로 막아 버린다.
            if (IsLiveSnooze(routine.RemindSnoozedUntil, now, grace))
            {
                AddSnoozed(ref found, routine.Id, true, routine.Title,
                    routine.RemindSnoozedUntil!.Value, today, now, grace);
                continue;
            }

            AddDue(ref found, routine.Id, true, routine.Title, routine.RemindHandled,
                OccurrenceOn(routine, yesterday, hour), yesterday, now, grace);
            AddDue(ref found, routine.Id, true, routine.Title, routine.RemindHandled,
                OccurrenceOn(routine, today, hour), today, now, grace);
        }

        foreach (var task in data.Tasks)
        {
            if (task.Done) continue;

            if (IsLiveSnooze(task.RemindSnoozedUntil, now, grace))
            {
                AddSnoozed(ref found, task.Id, false, task.Title,
                    task.RemindSnoozedUntil!.Value, today, now, grace);
                continue;
            }

            AddDue(ref found, task.Id, false, task.Title, task.RemindHandled,
                OccurrenceOn(task, yesterday, hour), yesterday, now, grace);
            AddDue(ref found, task.Id, false, task.Title, task.RemindHandled,
                OccurrenceOn(task, today, hour), today, now, grace);
        }

        return (IReadOnlyList<PendingReminder>?)found ?? [];
    }

    /// <summary>아직 살아 있는 미루기인지. 시각이 안 됐거나, 됐어도 유예 안이면 살아 있다.</summary>
    private static bool IsLiveSnooze(DateTime? until, DateTime now, int grace)
        => until is { } value && (now - value).TotalMinutes <= grace;

    private static void AddSnoozed(
        ref List<PendingReminder>? into, Guid id, bool isRoutine, string title,
        DateTime until, DateOnly today, DateTime now, int grace)
    {
        if (now < until) return;

        (into ??= []).Add(new PendingReminder(
            id, isRoutine, title, today, until, (int)(now - until).TotalMinutes));
    }

    private static void AddDue(
        ref List<PendingReminder>? into, Guid id, bool isRoutine, string title,
        DateOnly? handled, DateTime? occurrence, DateOnly date, DateTime now, int grace)
    {
        if (handled == date) return;
        if (occurrence is not { } at || at > now) return;

        // 유예를 넘긴 것은 아예 내놓지 않는다. 따로 기록해 둘 필요도 없다 —
        // 다음 틱에서도 똑같이 만료로 판정되어 그냥 지나간다.
        // (기록해 두려다 매일 루틴이 '어제 몫'으로 매 틱 걸리는 버그가 났었다.)
        var late = (int)(now - at).TotalMinutes;
        if (late > grace) return;

        (into ??= []).Add(new PendingReminder(id, isRoutine, title, date, at, late));
    }

    /// <summary>
    /// 그 몫을 처리 완료로 적는다. 이것을 적지 않으면 다음 틱에서 같은 것이 또 나온다.
    /// 화면에 띄우는 데 실패했다면 부르지 말 것 — 다음 틱에 다시 시도하고,
    /// 유예를 넘기면 그때 저절로 조용해진다.
    /// </summary>
    public static void MarkHandled(AppData data, PendingReminder pending)
    {
        if (pending.IsRoutine)
        {
            if (FindRoutine(data, pending.OwnerId) is not { } routine) return;

            routine.RemindHandled = pending.ForDate;
            routine.RemindSnoozedUntil = null;
            return;
        }

        if (FindTask(data, pending.OwnerId) is not { } task) return;

        task.RemindHandled = pending.ForDate;
        task.RemindSnoozedUntil = null;
    }

    /// <summary>
    /// 나중에 다시 알리도록 미룬다.
    /// 오늘 몫도 함께 처리 완료로 적는다 — 미뤘는데 원래 시각으로 또 나오면 안 된다.
    /// </summary>
    public static bool Snooze(AppData data, Guid ownerId, bool isRoutine, DateTime now, int minutes)
    {
        var until = now.AddMinutes(Math.Max(1, minutes));
        var today = DayEngine.LogicalDate(now, data.Settings.DayStartHour);

        if (isRoutine)
        {
            if (FindRoutine(data, ownerId) is not { } routine) return false;

            routine.RemindHandled = today;
            routine.RemindSnoozedUntil = until;
            return true;
        }

        if (FindTask(data, ownerId) is not { } task) return false;

        task.RemindHandled = today;
        task.RemindSnoozedUntil = until;
        return true;
    }

    /// <summary>
    /// 유예를 한참 넘긴 미루기를 치운다. 사흘 전에 미뤄 둔 것이 오늘 부활하면 안 된다.
    /// 날짜가 넘어간 뒤와 앱을 켤 때 부른다.
    /// </summary>
    public static int ClearStaleSnoozes(AppData data, DateTime now)
    {
        var grace = Math.Max(0, data.Settings.MissedGraceMinutes);
        var cleared = 0;

        foreach (var routine in data.Routines)
        {
            if (!IsStale(routine.RemindSnoozedUntil, now, grace)) continue;

            routine.RemindSnoozedUntil = null;
            cleared++;
        }

        foreach (var task in data.Tasks)
        {
            if (!IsStale(task.RemindSnoozedUntil, now, grace)) continue;

            task.RemindSnoozedUntil = null;
            cleared++;
        }

        return cleared;
    }

    private static bool IsStale(DateTime? until, DateTime now, int grace)
        => until is { } value && (now - value).TotalMinutes > grace;

    /// <summary>
    /// 다음으로 울릴 시각. 없으면 null.
    /// 설정 화면에 "다음 알림 09:00"을 보여줘 기능이 살아 있다는 신호로 쓴다.
    /// 일주일까지만 내다본다 — 그보다 먼 것은 어차피 화면에 적을 일이 없다.
    /// </summary>
    public static DateTime? NextAt(AppData data, DateTime now)
    {
        if (!data.Settings.RemindersEnabled) return null;

        var hour = data.Settings.DayStartHour;
        var today = DayEngine.LogicalDate(now, hour);
        DateTime? best = null;

        foreach (var routine in data.Routines)
        {
            if (routine.DoneToday) continue;

            Consider(routine.RemindSnoozedUntil);
            for (var i = 0; i <= 7; i++) Consider(OccurrenceOn(routine, today.AddDays(i), hour));
        }

        foreach (var task in data.Tasks)
        {
            if (task.Done) continue;

            Consider(task.RemindSnoozedUntil);
            for (var i = 0; i <= 7; i++) Consider(OccurrenceOn(task, today.AddDays(i), hour));
        }

        return best;

        void Consider(DateTime? candidate)
        {
            if (candidate is not { } at || at <= now) return;
            if (best is null || at < best.Value) best = at;
        }
    }

    private static Routine? FindRoutine(AppData data, Guid id)
    {
        foreach (var routine in data.Routines)
        {
            if (routine.Id == id) return routine;
        }

        return null;
    }

    private static TaskItem? FindTask(AppData data, Guid id)
    {
        foreach (var task in data.Tasks)
        {
            if (task.Id == id) return task;
        }

        return null;
    }
}
