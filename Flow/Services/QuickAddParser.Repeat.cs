using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// 빠른 추가 문장에서 매월 · 격주 · n일마다 · 매년 같은 반복을 읽는 부분.
///
/// 반복 말은 앞뒤로 낱말 경계를 요구한다. 그러지 않으면 "매월당 방문", "격주간지 읽기",
/// "3일마다가스카" 처럼 제목 속 글자를 반복으로 읽어 버린다.
/// </summary>
public static partial class QuickAddParser
{
    // 머리말: 어떤 반복인지
    [GeneratedRegex(@"(?:^|\s)(매월|매달|(\d{1,2})개월마다|분기마다|반기마다)(?=\s|$)")]
    private static partial Regex MonthlyHeadPattern();

    [GeneratedRegex(@"(?:^|\s)(격주|(\d{1,2})주마다)(?=\s|$)")]
    private static partial Regex WeekIntervalHeadPattern();

    [GeneratedRegex(@"(?:^|\s)(\d{1,3})일마다(?=\s|$)")]
    private static partial Regex EveryDaysPattern();

    [GeneratedRegex(@"(?:^|\s)매년(?=\s|$)")]
    private static partial Regex YearlyHeadPattern();

    // 머리말 바로 뒤에 오는 말: 언제인지. 모두 머리말 끝에서부터만 맞춘다.
    /// <summary>
    /// "25일" · "10일 25일" · "10일, 말일" · "10,25일" · "1, 15, 말일".
    /// '일'을 뗀 맨 숫자는 쉼표(· 、)로 이어져 있고 끝이 "n일"이나 "말일"일 때만 날짜다 —
    /// "매월 5,000원 저축" · "매월 10일 3개 주문"의 숫자는 제목이다.
    /// </summary>
    [GeneratedRegex(@"^\s*((?:(?:\d{1,2}\s*일|말일)(?:\s*[,·、]\s*|\s+)|\d{1,2}\s*[,·、]\s*)*(?:\d{1,2}\s*일|말일))(?=\s|$)")]
    private static partial Regex MonthDatesTail();

    [GeneratedRegex(@"^\s*(첫째|둘째|셋째|넷째|마지막)\s*(?:주\s*)?([월화수목금토일])(?:요일)?(?=\s|$)")]
    private static partial Regex MonthNthTail();

    /// <summary>
    /// "월요일" · "화목" · "월 수 금". 낱말 하나가 통째로 요일 글자여야 한다 —
    /// "격주 일정 확인"의 "일정"은 요일이 아니다.
    /// </summary>
    [GeneratedRegex(@"^\s*((?:[월화수목금토일]+(?:요일)?)(?:[\s,·、]+[월화수목금토일]+(?:요일)?)*)(?=\s|$)")]
    private static partial Regex WeekDaysTail();

    [GeneratedRegex(@"^\s*(\d{1,2})\s*(?:월\s*(\d{1,2})\s*일|[/.]\s*(\d{1,2}))(?=\s|$)")]
    private static partial Regex YearDateTail();

    [GeneratedRegex(@"\d{1,2}")]
    private static partial Regex NumberPattern();

    private static readonly string[] NthWords = { "첫째", "둘째", "셋째", "넷째", "마지막" };

    /// <summary>
    /// 문장에서 반복을 읽어 내고 그 말을 문장에서 뗀다. 반복 말이 없거나 값이 말이 안 되면
    /// null 을 돌려주고 문장을 그대로 둔다(그러면 지금까지의 규칙이 읽는다).
    /// 읽은 반복은 '남기기'가 켜져 있고, 첫 차례는 오늘 이후 가장 가까운 날이다.
    /// </summary>
    private static RepeatRule? ParseRepeat(ref string text, DateOnly today)
    {
        var rule = ReadMonthly(ref text, today)
                   ?? ReadWeekInterval(ref text, today)
                   ?? ReadEveryDays(ref text, today)
                   ?? ReadYearly(ref text, today);

        return rule;
    }

    private static RepeatRule? ReadMonthly(ref string text, DateOnly today)
    {
        var head = MonthlyHeadPattern().Match(text);
        if (!head.Success) return null;

        var word = head.Groups[1].Value;
        var every = word switch
        {
            "분기마다" => 3,
            "반기마다" => 6,
            _ when head.Groups[2].Success => int.Parse(head.Groups[2].Value),
            _ => 1
        };
        if (every < 1) return null;

        var rule = new RepeatRule { Every = every, Sticky = true };
        var rest = text[(head.Index + head.Length)..];
        var tailLength = 0;

        if (MonthNthTail().Match(rest) is { Success: true } nth)
        {
            rule.Unit = RepeatUnit.MonthNth;
            rule.Nth = Array.IndexOf(NthWords, nth.Groups[1].Value) + 1;
            rule.NthDay = (int)DayMap[nth.Groups[2].Value[0]];
            tailLength = nth.Length;
        }
        else if (MonthDatesTail().Match(rest) is { Success: true } dates)
        {
            rule.Unit = RepeatUnit.MonthDates;
            if (dates.Groups[1].Value.Contains("말일")) rule.MonthDays.Add(RepeatUnit.LastDay);

            foreach (Match number in NumberPattern().Matches(dates.Groups[1].Value))
            {
                var day = int.Parse(number.Value);
                if (day is < 1 or > 31) return null;   // "매월 45일" — 반복으로 읽지 않는다
                if (!rule.MonthDays.Contains(day)) rule.MonthDays.Add(day);
            }
            tailLength = dates.Length;
        }
        else
        {
            // "매월 정산" — 날짜를 안 적었으면 오늘 날짜(일)에 온다
            rule.Unit = RepeatUnit.MonthDates;
            rule.MonthDays.Add(today.Day);
        }

        rule = RepeatEngine.WithFirst(rule, today);

        // 분기 · 반기는 달력에 맞춘다(1·4·7·10월, 1·7월). 다른 달에 맞추려면 반복 창에서 첫 차례를 옮긴다.
        if (word is "분기마다" or "반기마다")
        {
            for (var i = 0; i < 64 && (rule.Anchor.Month - 1) % every != 0; i++)
                rule = RepeatEngine.ShiftFirst(rule, 1, today);
        }

        text = Cut(text, head.Index, head.Length + tailLength);
        return rule;
    }

    private static RepeatRule? ReadWeekInterval(ref string text, DateOnly today)
    {
        var head = WeekIntervalHeadPattern().Match(text);
        if (!head.Success) return null;

        var every = head.Groups[2].Success ? int.Parse(head.Groups[2].Value) : 2;
        if (every < 1) return null;

        var rule = new RepeatRule { Unit = RepeatUnit.Weekly, Every = every, Sticky = true };
        var rest = text[(head.Index + head.Length)..];
        var tailLength = 0;

        if (WeekDaysTail().Match(rest) is { Success: true } tail)
        {
            rule.Days.AddRange(ParseDayTokens(tail.Groups[1].Value).Select(d => (int)d).Distinct());
            tailLength = tail.Length;
        }
        else
        {
            // "격주 회의" — 요일을 안 적었으면 오늘 요일
            rule.Days.Add((int)today.DayOfWeek);
        }

        text = Cut(text, head.Index, head.Length + tailLength);
        return RepeatEngine.WithFirst(rule, today);
    }

    private static RepeatRule? ReadEveryDays(ref string text, DateOnly today)
    {
        var match = EveryDaysPattern().Match(text);
        if (!match.Success) return null;

        var every = int.Parse(match.Groups[1].Value);
        if (every < 2) return null;   // 1일마다는 매일이다. "매일"이라고 적게 둔다

        text = Cut(text, match.Index, match.Length);
        return RepeatEngine.WithFirst(new RepeatRule { Unit = RepeatUnit.EveryDays, Every = every, Sticky = true }, today);
    }

    private static RepeatRule? ReadYearly(ref string text, DateOnly today)
    {
        var head = YearlyHeadPattern().Match(text);
        if (!head.Success) return null;

        var rule = new RepeatRule { Unit = RepeatUnit.Yearly, Month = today.Month, Day = today.Day, Sticky = true };
        var rest = text[(head.Index + head.Length)..];
        var tailLength = 0;

        if (YearDateTail().Match(rest) is { Success: true } tail)
        {
            var month = int.Parse(tail.Groups[1].Value);
            var day = int.Parse(tail.Groups[2].Success ? tail.Groups[2].Value : tail.Groups[3].Value);

            // 2월 29일은 받는다(평년엔 28일에 온다). 2월 30일 같은 날은 없다.
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2028, month)) return null;

            rule.Month = month;
            rule.Day = day;
            tailLength = tail.Length;
        }

        text = Cut(text, head.Index, head.Length + tailLength);
        return RepeatEngine.WithFirst(rule, today);
    }

    private static string Cut(string text, int index, int length)
        => text.Remove(index, length).Insert(index, " ");
}
