using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Flow.Models;

namespace Flow.Services;

public sealed record QuickAddResult(
    bool IsRoutine,
    string Title,
    List<DayOfWeek> Days,
    Priority Priority,
    DateOnly? Due,
    TimeOnly? DueTime);

/// <summary>
/// 한 줄 입력을 할 일 또는 루틴으로 해석한다.
/// 예) "내일 오후 3시 보고서 제출 !1"  → 내일 15:00 마감, 최우선 할 일
///     "매주 월수금 운동 30분"          → 월·수·금 반복 루틴
/// </summary>
public static partial class QuickAddParser
{
    private const string DayChars = "월화수목금토일";

    private static readonly Dictionary<char, DayOfWeek> DayMap = new()
    {
        ['월'] = DayOfWeek.Monday,
        ['화'] = DayOfWeek.Tuesday,
        ['수'] = DayOfWeek.Wednesday,
        ['목'] = DayOfWeek.Thursday,
        ['금'] = DayOfWeek.Friday,
        ['토'] = DayOfWeek.Saturday,
        ['일'] = DayOfWeek.Sunday
    };

    [GeneratedRegex(@"(?:^|\s)[!pP]([1-4])(?=\s|$)")]
    private static partial Regex PriorityPattern();

    [GeneratedRegex(@"매주\s*((?:[월화수목금토일]|요일|[\s,·、])+)")]
    private static partial Regex WeeklyPattern();

    // 아래 날짜·시간 패턴들은 모두 앞뒤로 낱말 경계를 요구한다.
    // 그러지 않으면 "오전 11~12시 사이" 의 "12시", "2시간 작업" 의 "2시" 처럼
    // 다른 낱말 속을 파고들어 제목에서 글자를 훔쳐간다.
    [GeneratedRegex(@"매일|평일|주말|매주")]
    private static partial Regex RoutineKeywordPattern();

    [GeneratedRegex(@"^\s*([월화수목금토일](?:[월화수목금토일]|요일|[\s,·、])*)\s")]
    private static partial Regex BareDaysPattern();

    /// <summary>
    /// "화목", "월수금" 처럼 요일 글자가 두 개 이상 붙어 하나의 낱말을 이루는 경우.
    /// 앞뒤가 공백이어야 하므로 "수목원"의 "수목"은 걸리지 않는다.
    /// </summary>
    [GeneratedRegex(@"(?:^|\s)([월화수목금토일]{2,})(?=\s)")]
    private static partial Regex MultiDayPattern();

    [GeneratedRegex(@"(?:^|\s)(오늘|내일|모레|글피|다음\s*주)(?:에|까지|부터|경|쯤)?(?=$|\s|[,.·、])")]
    private static partial Regex RelativeDatePattern();

    [GeneratedRegex(@"(?:^|\s)(\d{1,2})\s*월\s*(\d{1,2})\s*일(?:에|까지|부터|경|쯤)?(?=$|\s|[,.·、])")]
    private static partial Regex MonthDayPattern();

    [GeneratedRegex(@"(?:^|\s)(\d{1,2})\s*[/.]\s*(\d{1,2})(?:에|까지|부터|경|쯤)?(?=$|\s|[,.·、])")]
    private static partial Regex SlashDatePattern();

    [GeneratedRegex(@"(?:^|\s)([월화수목금토일])요일(?:에|까지|부터|경|쯤)?(?=$|\s|[,.·、])")]
    private static partial Regex WeekdayPattern();

    [GeneratedRegex(@"(?:^|\s)(\d{1,2})\s*일(?:에|까지|부터|경|쯤)?(?=$|\s|[,.·、])")]
    private static partial Regex DayOfMonthPattern();

    [GeneratedRegex(@"(?:^|\s)(오전|아침|오후|저녁|밤)?\s*(\d{1,2})\s*시\s*(?:(\d{1,2})\s*분|(반))?(?:에|까지|부터|경|쯤)?(?=$|\s|[,.·、])")]
    private static partial Regex KoreanTimePattern();

    [GeneratedRegex(@"(?:^|\s)([01]?\d|2[0-3]):([0-5]\d)(?:에|까지|부터|경|쯤)?(?=$|\s|[,.·、])")]
    private static partial Regex ClockTimePattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ExtraSpacePattern();

    /// <param name="assumeRoutine">
    /// 루틴 탭에서 입력할 때 참. "매일" 같은 말을 안 붙여도 루틴으로 만들고,
    /// "월수금 운동"처럼 요일만 앞에 적은 것도 읽는다.
    /// </param>
    /// <param name="now">
    /// 지금 시각. 주면 "9시"처럼 오전·오후를 안 적은 시각을 아직 오지 않은 쪽으로 읽는다.
    /// 안 주면 적힌 숫자 그대로 읽는다.
    /// </param>
    public static QuickAddResult Parse(string input, DateOnly today, bool assumeRoutine = false,
        TimeOnly? now = null)
    {
        var text = input ?? "";
        var priority = Priority.None;
        var days = new List<DayOfWeek>();
        var isRoutine = false;
        DateOnly? due = null;
        TimeOnly? time = null;

        // 오전·오후를 적지 않은 맨 숫자인지. 맨 숫자만 뜻이 두 개다.
        var bareHour = false;

        text = Consume(text, PriorityPattern(), m =>
        {
            priority = m.Groups[1].Value switch
            {
                "1" => Priority.High,
                "2" => Priority.Medium,
                "3" => Priority.Low,
                _ => Priority.None
            };
        });

        text = Consume(text, WeeklyPattern(), m =>
        {
            isRoutine = true;
            days.AddRange(ParseDayTokens(m.Groups[1].Value));
        });

        var sawRoutineKeyword = isRoutine;
        text = Consume(text, RoutineKeywordPattern(), m =>
        {
            isRoutine = true;
            if (sawRoutineKeyword) return;

            switch (m.Value)
            {
                case "평일":
                    days.AddRange(new[]
                    {
                        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                        DayOfWeek.Thursday, DayOfWeek.Friday
                    });
                    break;
                case "주말":
                    days.AddRange(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });
                    break;
            }
        });

        // 요일 두 글자 이상이 한 낱말로 나오면 어느 탭에서든 반복 루틴으로 본다.
        // "화목 재고 점검"이 제목에 "화목"을 달고 할 일로 들어가면 안 된다.
        if (!isRoutine)
        {
            text = Consume(text, MultiDayPattern(), m =>
            {
                isRoutine = true;
                days.AddRange(ParseDayTokens(m.Groups[1].Value));
            });
        }

        if (assumeRoutine && !isRoutine)
        {
            isRoutine = true;
            text = Consume(text, BareDaysPattern(), m => days.AddRange(ParseDayTokens(m.Groups[1].Value)));
        }

        text = Consume(text, KoreanTimePattern(), m =>
        {
            var hour = int.Parse(m.Groups[2].Value);
            var minute = m.Groups[4].Success ? 30 : m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            var meridiem = m.Groups[1].Value;

            if (meridiem is "오후" or "저녁" or "밤" && hour < 12) hour += 12;
            else if (meridiem is "오전" or "아침" && hour == 12) hour = 0;
            else if (meridiem.Length == 0) bareHour = true;

            if (hour <= 23 && minute <= 59) time = new TimeOnly(hour, minute);
        });

        if (time is null)
        {
            text = Consume(text, ClockTimePattern(), m =>
                time = new TimeOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }

        if (!isRoutine) text = ParseDate(text, today, ref due);

        if (due is null && time is not null && !isRoutine) due = today;

        // 밤 9시에 "9시 22분"이라고 적은 사람은 아침 9시를 뜻하지 않았다.
        // 오전·오후를 안 적었고 그대로 읽으면 이미 지난 오늘이 될 때만 오후로 옮긴다.
        //
        // 루틴은 옮기지 않는다. 내일 아침에 다시 오므로 오전 9시도 멀쩡한 뜻이고,
        // 밤에 아침 루틴을 적는 일이 그 반대보다 훨씬 흔하다.
        if (bareHour && !isRoutine && now is { } current
            && time is { Hour: >= 1 and <= 11 } bare && due == today && bare < current)
        {
            time = bare.AddHours(12);
        }

        var title = ExtraSpacePattern().Replace(text, " ").Trim(' ', ',', '·', '、');

        return new QuickAddResult(
            isRoutine,
            title,
            days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList(),
            priority,
            due,
            time);
    }

    private static string ParseDate(string text, DateOnly today, ref DateOnly? due)
    {
        DateOnly? found = null;

        var result = Consume(text, RelativeDatePattern(), m =>
        {
            found = m.Groups[1].Value.Replace(" ", "") switch
            {
                "오늘" => today,
                "내일" => today.AddDays(1),
                "모레" => today.AddDays(2),
                "글피" => today.AddDays(3),
                "다음주" => today.AddDays(7),
                _ => today
            };
        });
        if (found is not null) { due = found; return result; }

        result = Consume(text, MonthDayPattern(), m =>
            found = BuildDate(today, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        if (found is not null) { due = found; return result; }

        result = Consume(text, SlashDatePattern(), m =>
            found = BuildDate(today, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        if (found is not null) { due = found; return result; }

        result = Consume(text, WeekdayPattern(), m =>
        {
            if (!DayMap.TryGetValue(m.Groups[1].Value[0], out var target)) return;
            var delta = ((int)target - (int)today.DayOfWeek + 7) % 7;
            found = today.AddDays(delta);
        });
        if (found is not null) { due = found; return result; }

        result = Consume(text, DayOfMonthPattern(), m =>
        {
            var day = int.Parse(m.Groups[1].Value);
            if (day is < 1 or > 31) return;

            var candidate = SafeDate(today.Year, today.Month, day);
            if (candidate is null || candidate.Value < today)
            {
                var next = today.AddMonths(1);
                candidate = SafeDate(next.Year, next.Month, day);
            }
            found = candidate;
        });
        if (found is not null) due = found;

        return result;
    }

    private static DateOnly? BuildDate(DateOnly today, int month, int day)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;

        var candidate = SafeDate(today.Year, month, day);
        if (candidate is not null && candidate.Value < today)
            candidate = SafeDate(today.Year + 1, month, day);

        return candidate;
    }

    private static DateOnly? SafeDate(int year, int month, int day)
        => day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;

    private static IEnumerable<DayOfWeek> ParseDayTokens(string raw)
        => raw.Replace("요일", " ")
              .Where(c => DayChars.Contains(c))
              .Select(c => DayMap[c]);

    /// <summary>패턴이 맞으면 콜백을 호출하고 해당 구간을 공백으로 치환한다.</summary>
    private static string Consume(string text, Regex pattern, Action<Match> onMatch)
    {
        var match = pattern.Match(text);
        if (!match.Success) return text;

        onMatch(match);
        return text.Remove(match.Index, match.Length).Insert(match.Index, " ");
    }
}
