using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;

namespace Flow.Services;

public readonly record struct RolloverResult(bool Changed, int DaysElapsed)
{
    public static readonly RolloverResult None = new(false, 0);
}

/// <summary>
/// 날짜 롤오버 엔진. 순수 함수로만 구성되어 있어 시간을 주입해 테스트할 수 있다.
/// 실제 자정이 아니라 <see cref="AppSettings.DayStartHour"/> 를 기준으로 "논리적 날짜"를 계산한다.
/// </summary>
/// <summary>할 일 하나가 어느 화면에 놓이는지.</summary>
public enum TaskBucket
{
    /// <summary>오늘 화면. 오늘까지인 것, 지난 것, 그리고 날짜를 안 정한 것.</summary>
    Today,

    /// <summary>예정 화면. 날짜가 정해져 있고 아직 오지 않은 것.</summary>
    Upcoming,

    /// <summary>오늘 끝낸 것.</summary>
    DoneToday,

    /// <summary>지난 날 끝낸 것. 보관만 하고 어느 화면에도 내놓지 않는다.</summary>
    Archived
}

public static class DayEngine
{
    /// <summary>
    /// 할 일이 어느 화면에 놓이는지 정한다.
    ///
    /// 날짜를 안 정한 것은 '예정'이 아니다. 예정은 날짜가 정해진 앞일을 뜻하는데,
    /// 날짜 없는 것은 아무 날에도 걸려 있지 않다. 그런 것을 예정에 넣으면
    /// 기본 화면인 오늘에서는 흔적조차 안 보여 그대로 잊힌다.
    /// </summary>
    public static TaskBucket BucketOf(TaskItem task, DateOnly today)
    {
        if (task.Done) return task.CompletedDate == today ? TaskBucket.DoneToday : TaskBucket.Archived;

        return task.Due is { } due && due > today ? TaskBucket.Upcoming : TaskBucket.Today;
    }

    /// <summary>루틴 히스토리 보관 기간.</summary>
    private const int HistoryRetentionDays = 370;

    /// <summary>이전 예정일을 거슬러 찾을 최대 범위. 요일 루틴은 최대 7일 간격이므로 넉넉하다.</summary>
    private const int ScheduleLookbackDays = 14;

    /// <summary>
    /// 지금 이 순간이 속한 "논리적 날짜".
    /// dayStartHour 가 4면 9월 8일 새벽 2시는 아직 9월 7일로 취급한다.
    /// </summary>
    public static DateOnly LogicalDate(DateTime now, int dayStartHour)
        => DateOnly.FromDateTime(now.AddHours(-dayStartHour));

    /// <summary>다음으로 날짜가 바뀌는 정확한 시각.</summary>
    /// <summary>
    /// 논리적 날짜 안에서 어떤 시각이 가리키는 실제 순간. LogicalDate 를 뒤집은 것이다.
    /// 하루 시작(기본 04시)보다 이른 시각은 다음 달력일에 온다 —
    /// 논리적으로 9월 8일인 02:00 은 달력으로는 9월 9일 새벽 2시다.
    /// </summary>
    public static DateTime AtLogicalTime(DateOnly logicalDate, TimeOnly time, int dayStartHour)
    {
        var wall = logicalDate.ToDateTime(time);
        return time.Hour < dayStartHour ? wall.AddDays(1) : wall;
    }

    public static DateTime NextRolloverAt(DateTime now, int dayStartHour)
    {
        var candidate = now.Date.AddHours(dayStartHour);
        return candidate <= now ? candidate.AddDays(1) : candidate;
    }

    /// <summary>해당 날짜에 이 루틴이 예정되어 있는지. Days 가 비어 있으면 매일.</summary>
    public static bool IsScheduled(Routine routine, DateOnly date)
        => !routine.Archived && (routine.Days.Count == 0 || routine.Days.Contains(date.DayOfWeek));

    /// <summary><paramref name="from"/> 직전의 예정일. 없으면 null.</summary>
    public static DateOnly? PreviousScheduledDay(Routine routine, DateOnly from)
    {
        for (var i = 1; i <= ScheduleLookbackDays; i++)
        {
            var d = from.AddDays(-i);
            if (IsScheduled(routine, d)) return d;
        }
        return null;
    }

    public static void CompleteRoutine(Routine routine, DateOnly today)
    {
        if (routine.DoneToday) return;

        routine.StreakSnapshot = routine.Streak;
        routine.LastDoneSnapshot = routine.LastDoneDate;

        var previous = PreviousScheduledDay(routine, today);
        routine.Streak = previous.HasValue && routine.LastDoneDate == previous.Value
            ? routine.Streak + 1
            : 1;

        routine.LastDoneDate = today;
        routine.DoneToday = true;
        if (routine.Streak > routine.BestStreak) routine.BestStreak = routine.Streak;
    }

    public static void UncompleteRoutine(Routine routine)
    {
        if (!routine.DoneToday) return;

        routine.Streak = routine.StreakSnapshot;
        routine.LastDoneDate = routine.LastDoneSnapshot;
        routine.DoneToday = false;
    }

    /// <summary>
    /// 마지막 정산일부터 오늘까지를 하루씩 결산하고, 루틴 체크를 해제하고, 미완료 할 일을 이월한다.
    /// PC가 며칠간 꺼져 있었어도 한 번의 호출로 그 기간 전체를 정산한다.
    /// </summary>
    public static RolloverResult Rollover(AppData data, DateOnly today)
    {
        if (data.LastLogicalDate == default)
        {
            data.LastLogicalDate = today;
            return RolloverResult.None;
        }

        if (today <= data.LastLogicalDate) return RolloverResult.None;

        var daysElapsed = today.DayNumber - data.LastLogicalDate.DayNumber;

        for (var day = data.LastLogicalDate; day < today; day = day.AddDays(1))
        {
            var scheduled = 0;
            var done = 0;

            foreach (var routine in data.Routines)
            {
                if (!IsScheduled(routine, day)) continue;
                scheduled++;

                if (routine.LastDoneDate == day) done++;
                else routine.Streak = 0;
            }

            var tasksDone = data.Tasks.Count(t => t.Done && t.CompletedDate == day);

            UpsertHistory(data, new DayRecord
            {
                Date = day,
                RoutinesScheduled = scheduled,
                RoutinesDone = done,
                TasksDone = tasksDone
            });
        }

        foreach (var routine in data.Routines)
        {
            routine.DoneToday = false;
            routine.StreakSnapshot = routine.Streak;
            routine.LastDoneSnapshot = routine.LastDoneDate;
        }

        if (data.Settings.CarryOverIncomplete)
        {
            foreach (var task in data.Tasks)
            {
                if (task.Done || !task.Due.HasValue || task.Due.Value >= today) continue;
                task.CarryOverCount += today.DayNumber - task.Due.Value.DayNumber;
                task.Due = today;
            }
        }

        var retentionCutoff = today.AddDays(-Math.Max(0, data.Settings.CompletedRetentionDays));
        data.Tasks.RemoveAll(t => t.Done && t.CompletedDate.HasValue && t.CompletedDate.Value < retentionCutoff);

        var historyCutoff = today.AddDays(-HistoryRetentionDays);
        data.History.RemoveAll(h => h.Date < historyCutoff);

        data.LastLogicalDate = today;
        return new RolloverResult(true, daysElapsed);
    }

    /// <summary>오늘 진행 중인 결산을 히스토리에 미리 반영해 히트맵이 실시간으로 갱신되게 한다.</summary>
    public static void SyncToday(AppData data, DateOnly today)
    {
        var scheduled = data.Routines.Count(r => IsScheduled(r, today));
        var done = data.Routines.Count(r => IsScheduled(r, today) && r.DoneToday);
        var tasksDone = data.Tasks.Count(t => t.Done && t.CompletedDate == today);

        UpsertHistory(data, new DayRecord
        {
            Date = today,
            RoutinesScheduled = scheduled,
            RoutinesDone = done,
            TasksDone = tasksDone
        });
    }

    private static void UpsertHistory(AppData data, DayRecord record)
    {
        var index = data.History.FindIndex(h => h.Date == record.Date);
        if (index >= 0) data.History[index] = record;
        else data.History.Add(record);
    }

    /// <summary>최근 <paramref name="days"/> 일의 달성률(0-1). 히트맵용, 오래된 날짜부터 반환.</summary>
    public static IReadOnlyList<(DateOnly Date, double Rate)> RecentRates(AppData data, DateOnly today, int days)
    {
        var byDate = data.History.ToDictionary(h => h.Date);
        var result = new List<(DateOnly, double)>(days);

        for (var i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            if (byDate.TryGetValue(date, out var record) && record.RoutinesScheduled > 0)
                result.Add((date, (double)record.RoutinesDone / record.RoutinesScheduled));
            else
                result.Add((date, 0));
        }

        return result;
    }
}
