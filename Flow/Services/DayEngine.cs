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

    /// <summary>
    /// 매일 · 요일보다 넓은 반복이 살아 있는 루틴인지.
    /// data.json 이 기준이다 — Archived 가 아니면 규칙이 달려 있어도 옛 방식이다.
    /// </summary>
    public static bool HasRule(Routine routine)
        => routine.Archived && routine.Repeat is { } rule && RepeatEngine.IsKnown(rule);

    /// <summary>
    /// 반복 정보를 잃었거나 아직 못 받았거나 더 새 버전이 만든 것.
    /// 오늘에는 내놓지 않고, 루틴 탭에 "반복 정보 없음"으로 남긴다. 매일로 바꾸지도 지우지도 않는다.
    /// </summary>
    public static bool IsRepeatMissing(Routine routine) => routine.Archived && !HasRule(routine);

    /// <summary>못 하면 할 때까지 오늘에 남는 항목인지.</summary>
    public static bool IsSticky(Routine routine) => HasRule(routine) && routine.Repeat!.Sticky;

    /// <summary>
    /// 해당 날짜가 이 루틴의 차례인지. 옛 방식은 Days 가 비어 있으면 매일.
    /// 밀려서 남아 있는 날은 차례가 아니다 — 그것은 <see cref="IsDue"/> 가 답한다.
    /// 알림과 지난 날 기록은 이쪽을 본다.
    /// </summary>
    public static bool IsScheduled(Routine routine, DateOnly date)
    {
        if (HasRule(routine)) return RepeatEngine.OccursOn(routine.Repeat!, date);

        return !routine.Archived && (routine.Days.Count == 0 || routine.Days.Contains(date.DayOfWeek));
    }

    /// <summary>
    /// 남는 항목이 아직 못 끝낸 가장 이른 차례. 남는 항목이 아니거나 다 끝냈으면 null.
    /// 끝낸 날(LastDoneDate) 뒤로 온 차례가 있느냐로 판단한다 — 체크 한 번이 그때까지의 차례를 모두 끝낸다.
    /// </summary>
    public static DateOnly? DueSince(Routine routine, DateOnly today)
        => IsSticky(routine) ? RepeatEngine.PendingSince(routine.Repeat!, routine.LastDoneDate, today) : null;

    /// <summary>
    /// 오늘 화면에 내놓을 루틴인지. 남는 항목은 못 끝낸 차례가 있거나 오늘 끝낸 것,
    /// 그 밖은 오늘이 차례인 것.
    /// </summary>
    public static bool IsDue(Routine routine, DateOnly today)
    {
        if (!IsSticky(routine)) return IsScheduled(routine, today);

        // 오늘 끝낸 것은 체크된 채 남아 있어야 다시 눌러 풀 수 있다
        return DueSince(routine, today).HasValue || (routine.DoneToday && routine.LastDoneDate == today);
    }

    /// <summary>남는 항목이 며칠 밀렸는지. 차례 날 당일은 0.</summary>
    public static int OverdueDays(Routine routine, DateOnly today)
        => DueSince(routine, today) is { } since ? today.DayNumber - since.DayNumber : 0;

    /// <summary><paramref name="from"/> 을 포함해 그 뒤의 첫 차례. 반복 정보가 없으면 null.</summary>
    public static DateOnly? NextDay(Routine routine, DateOnly from)
    {
        if (HasRule(routine)) return RepeatEngine.Next(routine.Repeat!, from);
        if (routine.Archived) return null;

        for (var i = 0; i < 7; i++)
        {
            var day = from.AddDays(i);
            if (IsScheduled(routine, day)) return day;
        }
        return null;
    }

    /// <summary><paramref name="from"/> 직전의 예정일. 없으면 null.</summary>
    public static DateOnly? PreviousScheduledDay(Routine routine, DateOnly from)
    {
        // 매월 · 매년은 14일 안에 직전 차례가 없다. 규칙이 스스로 거슬러 찾는다.
        if (HasRule(routine)) return RepeatEngine.Previous(routine.Repeat!, from);

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

        // 반복 정보가 없는 루틴은 언제가 차례인지 모른다. 기록을 만들지 않는다.
        if (IsRepeatMissing(routine)) return;

        routine.StreakSnapshot = routine.Streak;
        routine.LastDoneSnapshot = routine.LastDoneDate;

        if (IsSticky(routine))
        {
            // 남는 항목에는 연속 기록이 없다. 끝낸 날만 적으면 그때까지의 차례가 모두 끝난다.
            routine.LastDoneDate = today;
            routine.DoneToday = true;
            return;
        }

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
                if (IsSticky(routine))
                {
                    // 남는 항목은 끝낸 날에만 센다. 차례 날 못 했다고 그날을 깎지 않고,
                    // 연속 기록도 없으니 끊을 것이 없다.
                    if (routine.LastDoneDate == day)
                    {
                        scheduled++;
                        done++;
                    }
                    continue;
                }

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
        // 남는 항목은 오늘 끝냈을 때만 센다 — 정산(Rollover)이 지난 날을 세는 방식과 같아야
        // 날이 넘어갈 때 히트맵 칸의 값이 바뀌지 않는다.
        var stickyDone = data.Routines.Count(r => IsSticky(r) && r.DoneToday && r.LastDoneDate == today);
        var scheduled = stickyDone + data.Routines.Count(r => !IsSticky(r) && IsScheduled(r, today));
        var done = stickyDone + data.Routines.Count(r => !IsSticky(r) && IsScheduled(r, today) && r.DoneToday);
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
