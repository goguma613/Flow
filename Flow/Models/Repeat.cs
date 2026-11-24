using System;
using System.Collections.Generic;

namespace Flow.Models;

/// <summary>
/// 매일 · 요일보다 넓은 반복 하나. 데이터 폴더의 repeats\&lt;루틴 Id&gt;.json 에 하나씩 담긴다.
///
/// 칸이 모두 숫자인 까닭: 모르는 enum 문자열 하나가 파일 전체를 못 읽게 만든 전례가 있다.
/// 모르는 숫자는 "더 새 버전이 만든 것"으로 읽기만 하고 건드리지 않으면 된다.
///
/// 끝낸 날은 여기 두지 않는다 — data.json 의 그 루틴 LastDoneDate 가 맡는다.
/// 그래서 이 파일은 반복을 만들거나 바꿀 때만 쓰인다.
/// </summary>
public sealed class RepeatRule
{
    /// <summary>이 버전이 읽고 쓰는 판. 파일의 Version 이 이보다 크면 더 새 버전이 만든 것이라 읽기만 한다.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary><see cref="RepeatUnit"/> 의 값. 1 매주 · 2 n일마다 · 3 매월 날짜 · 4 매월 n번째 요일 · 5 매년.</summary>
    public int Unit { get; set; }

    /// <summary>간격. 2 면 격주 · 두 달마다 · 이틀마다.</summary>
    public int Every { get; set; } = 1;

    /// <summary>매주의 요일. 0 일 … 6 토(DayOfWeek 와 같은 숫자).</summary>
    public List<int> Days { get; set; } = new();

    /// <summary>매월의 날짜. 1–31, 말일은 32. 그 달에 없는 날짜는 말일로 당겨진다.</summary>
    public List<int> MonthDays { get; set; } = new();

    /// <summary>몇째 주인지. 1–4, 마지막은 5.</summary>
    public int Nth { get; set; }

    /// <summary>n번째 요일의 요일. 0 일 … 6 토.</summary>
    public int NthDay { get; set; }

    /// <summary>매년의 월 · 일.</summary>
    public int Month { get; set; }
    public int Day { get; set; }

    /// <summary>
    /// 첫 차례 날짜. 그 자체가 차례이고, 이보다 앞선 날은 차례가 아니다.
    /// 간격이 2 이상이면 어느 주 · 어느 달부터 세는지를 이 날이 정한다.
    /// </summary>
    public DateOnly Anchor { get; set; }

    /// <summary>못 하면 할 때까지 오늘에 남는지.</summary>
    public bool Sticky { get; set; }

    public RepeatRule Clone() => new()
    {
        Version = Version,
        Unit = Unit,
        Every = Every,
        Days = new List<int>(Days),
        MonthDays = new List<int>(MonthDays),
        Nth = Nth,
        NthDay = NthDay,
        Month = Month,
        Day = Day,
        Anchor = Anchor,
        Sticky = Sticky
    };
}

/// <summary>RepeatRule 의 숫자 값. enum 으로 두지 않는다(파일에 문자열로 나가면 안 된다).</summary>
public static class RepeatUnit
{
    /// <summary>화면에서만 쓴다. 매일은 파일 없이 data.json 의 빈 Days 로 남는다.</summary>
    public const int Daily = 0;
    public const int Weekly = 1;
    public const int EveryDays = 2;
    public const int MonthDates = 3;
    public const int MonthNth = 4;
    public const int Yearly = 5;

    /// <summary>MonthDays 안의 '말일'.</summary>
    public const int LastDay = 32;

    /// <summary>Nth 의 '마지막 주'.</summary>
    public const int LastWeek = 5;
}
