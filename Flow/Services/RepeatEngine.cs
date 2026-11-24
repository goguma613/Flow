using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 반복 규칙의 차례 계산. 순수 함수뿐이라 날짜를 넣어 시험할 수 있다.
/// 화면도 파일도 모른다.
/// </summary>
public static class RepeatEngine
{
    /// <summary>앞뒤로 차례를 찾아 걸을 한계. 매년 반복도 넉넉히 덮는다.</summary>
    private const int WalkLimitDays = 366 * 20;

    private static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };
    private static readonly string[] NthNames = { "", "첫째", "둘째", "셋째", "넷째", "마지막" };

    /// <summary>월요일부터 센 요일 순서.</summary>
    private static int MondayFirst(int day) => (day + 6) % 7;

    /// <summary>
    /// 파일에 담을 수 있고 이 버전이 계산할 줄 아는 규칙인지.
    /// 거짓이면 "더 새 버전이 만든 것이거나 덜 받아진 것"으로 보고 읽기만 한다.
    /// </summary>
    public static bool IsKnown(RepeatRule r)
    {
        // 더 새 판이 적은 규칙은 같은 숫자를 다른 뜻으로 쓸 수 있다. 아는 척하지 않는다.
        if (r.Version > RepeatRule.CurrentVersion) return false;
        if (r.Every < 1) return false;

        return r.Unit switch
        {
            RepeatUnit.Weekly => r.Days.Count > 0 && r.Days.All(d => d is >= 0 and <= 6),
            RepeatUnit.EveryDays => true,
            RepeatUnit.MonthDates => r.MonthDays.Count > 0 && r.MonthDays.All(d => d is >= 1 and <= RepeatUnit.LastDay),
            RepeatUnit.MonthNth => r.Nth is >= 1 and <= RepeatUnit.LastWeek && r.NthDay is >= 0 and <= 6,
            RepeatUnit.Yearly => r.Month is >= 1 and <= 12 && r.Day is >= 1 and <= 31,
            _ => false
        };
    }

    /// <summary>반복 창의 저장 단추. 매일은 늘 저장할 수 있다.</summary>
    public static bool IsValid(RepeatRule r) => r.Unit == RepeatUnit.Daily || IsKnown(r);

    /// <summary>간격과 첫 차례를 빼고, 요일 · 날짜 조건만 맞는 날인지.</summary>
    private static bool IsCandidate(RepeatRule r, DateOnly date)
    {
        switch (r.Unit)
        {
            case RepeatUnit.Weekly:
                return r.Days.Contains((int)date.DayOfWeek);

            case RepeatUnit.EveryDays:
                return true;

            case RepeatUnit.MonthDates:
            {
                // 그 달에 없는 날짜(31일, 말일 32)는 말일로 당긴다.
                // 당긴 값끼리 겹쳐도 '그날인가'만 묻기 때문에 한 번만 온다.
                var length = DateTime.DaysInMonth(date.Year, date.Month);
                foreach (var day in r.MonthDays)
                    if (Math.Min(day, length) == date.Day) return true;
                return false;
            }

            case RepeatUnit.MonthNth:
            {
                if ((int)date.DayOfWeek != r.NthDay) return false;
                if (r.Nth == RepeatUnit.LastWeek)
                    return date.Day + 7 > DateTime.DaysInMonth(date.Year, date.Month);
                return (date.Day - 1) / 7 + 1 == r.Nth;
            }

            case RepeatUnit.Yearly:
                // 2월 29일은 평년에 28일로 당긴다.
                return date.Month == r.Month
                       && date.Day == Math.Min(r.Day, DateTime.DaysInMonth(date.Year, r.Month));

            default:
                return false;
        }
    }

    /// <summary>첫 차례부터 세어 간격이 맞는 주 · 달 · 날인지.</summary>
    private static bool IsOnInterval(RepeatRule r, DateOnly date)
    {
        if (r.Every <= 1) return true;

        long steps = r.Unit switch
        {
            // 주는 월요일에 시작한다 — 첫 차례가 화요일이어도 그 주 일요일까지가 한 묶음이다.
            RepeatUnit.Weekly => (WeekStart(date) - WeekStart(r.Anchor)) / 7,
            RepeatUnit.EveryDays => date.DayNumber - r.Anchor.DayNumber,
            RepeatUnit.MonthDates or RepeatUnit.MonthNth
                => (date.Year * 12 + date.Month) - (r.Anchor.Year * 12 + r.Anchor.Month),
            RepeatUnit.Yearly => date.Year - r.Anchor.Year,
            _ => 0
        };

        return steps % r.Every == 0;
    }

    private static int WeekStart(DateOnly date) => date.DayNumber - MondayFirst((int)date.DayOfWeek);

    /// <summary>그 날이 차례인지. 첫 차례보다 앞선 날은 늘 아니다.</summary>
    public static bool OccursOn(RepeatRule r, DateOnly date)
        => IsKnown(r) && date >= r.Anchor && IsCandidate(r, date) && IsOnInterval(r, date);

    /// <summary><paramref name="from"/> 을 포함해 그 뒤의 첫 차례.</summary>
    public static DateOnly? Next(RepeatRule r, DateOnly from)
    {
        if (!IsKnown(r)) return null;

        var day = from < r.Anchor ? r.Anchor : from;
        for (var i = 0; i < WalkLimitDays && day < DateOnly.MaxValue; i++, day = day.AddDays(1))
            if (OccursOn(r, day)) return day;

        return null;
    }

    /// <summary><paramref name="before"/> 앞의 마지막 차례. 첫 차례보다 앞으로는 가지 않는다.</summary>
    public static DateOnly? Previous(RepeatRule r, DateOnly before)
    {
        if (!IsKnown(r) || before <= r.Anchor) return null;

        var day = before.AddDays(-1);
        for (var i = 0; i < WalkLimitDays && day >= r.Anchor; i++)
        {
            if (OccursOn(r, day)) return day;
            if (day == DateOnly.MinValue) break;
            day = day.AddDays(-1);
        }

        return null;
    }

    /// <summary>
    /// <paramref name="lastDone"/> 뒤부터 <paramref name="today"/> 까지에서 가장 이른 차례.
    /// '남는' 항목이 아직 남아 있는지, 그리고 언제부터 밀렸는지가 이것이다.
    /// </summary>
    public static DateOnly? PendingSince(RepeatRule r, DateOnly? lastDone, DateOnly today)
    {
        var from = lastDone is { } done && done < DateOnly.MaxValue ? done.AddDays(1) : r.Anchor;
        var next = Next(r, from);
        return next is { } day && day <= today ? day : null;
    }

    /// <summary>
    /// 첫 차례를 오늘 이후 가장 가까운 후보 날로 잡은 사본.
    /// 후보 날은 간격을 무시하고 요일 · 날짜 조건만 맞는 날이다 — 간격은 첫 차례부터 세므로
    /// 첫 차례를 정하는 데는 쓸 수 없다.
    /// </summary>
    public static RepeatRule WithFirst(RepeatRule draft, DateOnly today)
    {
        var copy = draft.Clone();
        copy.Anchor = CandidateFrom(copy, today, +1) ?? today;
        return copy;
    }

    /// <summary>첫 차례를 후보 날 단위로 앞뒤로 옮긴 사본. 오늘보다 앞으로는 가지 않는다.</summary>
    public static RepeatRule ShiftFirst(RepeatRule r, int steps, DateOnly today)
    {
        var copy = r.Clone();

        for (var i = 0; i < Math.Abs(steps); i++)
        {
            var next = steps > 0
                ? CandidateFrom(copy, copy.Anchor.AddDays(1), +1)
                : CandidateFrom(copy, copy.Anchor.AddDays(-1), -1);

            if (next is not { } day || day < today) break;
            copy.Anchor = day;
        }

        return copy;
    }

    /// <summary><paramref name="from"/> 을 포함해 한 방향으로 걸으며 만나는 첫 후보 날.</summary>
    private static DateOnly? CandidateFrom(RepeatRule r, DateOnly from, int direction)
    {
        if (r.Unit == RepeatUnit.Daily) return from;
        if (!IsKnown(r)) return null;

        var day = from;
        for (var i = 0; i < WalkLimitDays; i++)
        {
            if (IsCandidate(r, day)) return day;
            if (direction > 0 ? day == DateOnly.MaxValue : day == DateOnly.MinValue) break;
            day = day.AddDays(direction);
        }

        return null;
    }

    /// <summary>
    /// 파일 없이 data.json 의 Days 만으로 담기는 반복인지: 매일, 그리고 지나가는 매주 요일.
    /// 지금까지의 루틴이 모두 여기 든다 — 업데이트해도 한 글자도 바뀌지 않게 하는 갈림이다.
    /// </summary>
    public static bool IsLegacy(RepeatRule r)
        => r.Unit == RepeatUnit.Daily || (r.Unit == RepeatUnit.Weekly && r.Every == 1 && !r.Sticky);

    /// <summary>옛 방식의 Days 값. 매일(요일 일곱 개 포함)은 빈 목록이다.</summary>
    public static List<DayOfWeek> LegacyDays(RepeatRule r)
    {
        if (r.Unit != RepeatUnit.Weekly) return new List<DayOfWeek>();

        var days = SortedDays(r);
        return days.Count == 7 ? new List<DayOfWeek>() : days.Select(d => (DayOfWeek)d).ToList();
    }

    /// <summary>옛 방식 루틴의 반복을 규칙 모양으로. 반복 창이 지금 값을 보여 줄 때 쓴다.</summary>
    public static RepeatRule FromLegacy(IReadOnlyList<DayOfWeek> days)
    {
        if (days.Count == 0) return new RepeatRule { Unit = RepeatUnit.Daily };

        var rule = new RepeatRule { Unit = RepeatUnit.Weekly };
        rule.Days.AddRange(days.Select(d => (int)d).Distinct().OrderBy(MondayFirst));
        return rule;
    }

    /// <summary>못 했을 때 남길지의 기본값. 매일 · 매주 요일은 습관이라 지나가고, 드문 것은 남는다.</summary>
    public static bool DefaultSticky(int unit, int every)
        => unit != RepeatUnit.Daily && !(unit == RepeatUnit.Weekly && every <= 1);

    /// <summary>'남기기'만 빼고 같은지(첫 차례 포함).</summary>
    public static bool SameSchedule(RepeatRule a, RepeatRule b)
        => SameCadence(a, b) && a.Anchor == b.Anchor;

    /// <summary>
    /// 첫 차례와 '남기기'를 빼고 같은지. 연속 기록을 이어 셀 수 있는지의 기준이다 —
    /// 매주 월요일에 '남기기'만 켠 것은 같은 주기, 매월 25일로 바꾼 것은 다른 주기.
    /// </summary>
    public static bool SameCadence(RepeatRule a, RepeatRule b) => CadenceKey(a) == CadenceKey(b);

    /// <summary>그 종류가 쓰는 칸만 모은 글. 안 쓰는 칸에 남은 값이 견주기를 흐리지 않게 한다.</summary>
    private static string CadenceKey(RepeatRule r) => r.Unit switch
    {
        RepeatUnit.Daily => "d",
        // 요일 일곱 개인 매주는 매일과 같은 날에 온다
        RepeatUnit.Weekly when r.Every <= 1 && SortedDays(r).Count == 7 => "d",
        RepeatUnit.Weekly => $"w{r.Every}:{string.Join(",", SortedDays(r))}",
        RepeatUnit.EveryDays => r.Every <= 1 ? "d" : $"e{r.Every}",
        RepeatUnit.MonthDates => $"m{r.Every}:{string.Join(",", r.MonthDays.Distinct().OrderBy(d => d))}",
        RepeatUnit.MonthNth => $"n{r.Every}:{r.Nth}:{r.NthDay}",
        RepeatUnit.Yearly => $"y{r.Every}:{r.Month}/{r.Day}",
        _ => $"?{r.Unit}"
    };

    private static List<int> SortedDays(RepeatRule r)
        => r.Days.Where(d => d is >= 0 and <= 6).Distinct().OrderBy(MondayFirst).ToList();

    /// <summary>줄과 칩에 보이는 글. "매월 25일" · "격주 월" · "3개월마다 둘째 화".</summary>
    public static string Describe(RepeatRule r)
    {
        if (r.Unit == RepeatUnit.Daily) return "매일";
        if (!IsKnown(r)) return "반복 정보 없음";

        switch (r.Unit)
        {
            case RepeatUnit.Weekly:
            {
                var days = SortedDays(r);
                var names = string.Join(" ", days.Select(d => DayNames[d]));
                if (r.Every == 1) return days.Count == 7 ? "매일" : names;
                return r.Every == 2 ? $"격주 {names}" : $"{r.Every}주마다 {names}";
            }

            case RepeatUnit.EveryDays:
                return r.Every == 1 ? "매일" : $"{r.Every}일마다";

            case RepeatUnit.MonthDates:
                return $"{MonthPrefix(r)} {MonthDaysText(r)}";

            case RepeatUnit.MonthNth:
                return $"{MonthPrefix(r)} {NthNames[r.Nth]} {DayNames[r.NthDay]}";

            default:
                return r.Every == 1 ? $"매년 {r.Month}/{r.Day}" : $"{r.Every}년마다 {r.Month}/{r.Day}";
        }
    }

    private static string MonthPrefix(RepeatRule r) => r.Every == 1 ? "매월" : $"{r.Every}개월마다";

    /// <summary>"25일" · "10·25일" · "말일" · "10일·말일".</summary>
    private static string MonthDaysText(RepeatRule r)
    {
        var dates = r.MonthDays.Where(d => d < RepeatUnit.LastDay).Distinct().OrderBy(d => d).ToList();
        var last = r.MonthDays.Contains(RepeatUnit.LastDay);

        if (dates.Count == 0) return "말일";

        var text = string.Join("·", dates) + "일";
        return last ? text + "·말일" : text;
    }
}
