using System;
using System.Collections.Generic;
using System.Linq;
using Flow.Models;
using Flow.Services;

namespace Flow.ViewModels;

/// <summary>
/// 반복 창에서 고르는 값과 그것으로 만드는 규칙. UI 를 모른다(시험 대상).
///
/// 지킨 것 둘:
/// - 주기를 안 바꾸고 저장하면 첫 차례도 그대로다. 밀려서 남아 있는 항목이 창을 열었다 닫았다고 사라지거나,
///   격주의 주가 뒤집히면 안 된다.
/// - '남기기'는 종류의 기본값을 따라가되(매일 · 매주는 지나감, 드문 것은 남음), 사람이 스위치를 한 번 누른 뒤에는 따라가지 않는다.
/// </summary>
public sealed class RepeatEditorState
{
    public const int TabDaily = 0;
    public const int TabWeekly = 1;
    public const int TabMonthly = 2;
    public const int TabYearly = 3;
    public const int TabEveryDays = 4;

    private const int MaxEvery = 99;

    private static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };

    private readonly DateOnly _today;

    /// <summary>창을 열 때의 규칙. 주기가 그대로인지 견줄 때 쓴다. 모르는 규칙으로 열었으면 null.</summary>
    private readonly RepeatRule? _start;

    private readonly SortedSet<int> _days = new();
    private readonly SortedSet<int> _monthDays = new();

    // 간격은 탭마다 따로 기억한다. 3일마다를 보다가 매주로 넘어갔다고 3주마다가 되면 안 된다.
    private int _weekEvery = 1;
    private int _monthEvery = 1;
    private int _dayEvery = 3;

    private int _month;
    private int _day;

    /// <summary>첫 차례를 기본 자리에서 몇 차례 옮겼는지.</summary>
    private int _shift;

    private bool _stickyTouched;

    /// <summary>할 일을 루틴으로 옮길 때, 그리고 반복 정보를 잃은 루틴에 다시 정할 때의 시작: 매월, 오늘 날짜, 남기기.</summary>
    public static RepeatRule DefaultFor(DateOnly today)
    {
        var rule = new RepeatRule { Unit = RepeatUnit.MonthDates, Sticky = true };
        rule.MonthDays.Add(today.Day);
        return rule;
    }

    public RepeatEditorState(RepeatRule start, DateOnly today)
    {
        _today = today;

        // 고르지 않은 탭도 눌렀을 때 오늘에 맞는 값으로 시작한다
        _days.Add((int)today.DayOfWeek);
        _monthDays.Add(today.Day);
        Nth = (today.Day - 1) / 7 + 1;
        NthDay = (int)today.DayOfWeek;
        _month = today.Month;
        _day = today.Day;

        if (start.Unit != RepeatUnit.Daily && !RepeatEngine.IsKnown(start))
        {
            // 더 새 버전이 만든 규칙이거나 깨진 값. 읽을 수 있는 척하지 않고 기본값에서 시작한다.
            start = DefaultFor(today);
            _start = null;
        }
        else
        {
            _start = start.Clone();
        }

        Sticky = start.Unit != RepeatUnit.Daily && start.Sticky;

        // 이미 기본값과 다르게 정해 둔 '남기기'는 그 사람이 고른 것이다. 탭을 옮겨도 따라가지 않는다.
        _stickyTouched = start.Unit != RepeatUnit.Daily
                         && start.Sticky != RepeatEngine.DefaultSticky(start.Unit, start.Every);

        switch (start.Unit)
        {
            case RepeatUnit.Daily:
                Tab = TabDaily;
                break;

            case RepeatUnit.Weekly:
                Tab = TabWeekly;
                _weekEvery = Clamp(start.Every, 1);
                _days.Clear();
                foreach (var day in start.Days) _days.Add(day);
                break;

            case RepeatUnit.EveryDays:
                Tab = TabEveryDays;
                _dayEvery = Clamp(start.Every, 2);
                break;

            case RepeatUnit.MonthDates:
                Tab = TabMonthly;
                _monthEvery = Clamp(start.Every, 1);
                _monthDays.Clear();
                foreach (var day in start.MonthDays.Where(d => d < RepeatUnit.LastDay)) _monthDays.Add(day);
                LastDay = start.MonthDays.Contains(RepeatUnit.LastDay);
                break;

            case RepeatUnit.MonthNth:
                Tab = TabMonthly;
                _monthEvery = Clamp(start.Every, 1);
                ByNth = true;
                Nth = start.Nth;
                NthDay = start.NthDay;
                break;

            default:
                Tab = TabYearly;
                _month = start.Month;
                _day = start.Day;
                break;
        }
    }

    public int Tab { get; private set; }

    /// <summary>지금 탭의 간격. 매일 · 매년에는 없다(1).</summary>
    public int Every
    {
        get => Tab switch
        {
            TabWeekly => _weekEvery,
            TabMonthly => _monthEvery,
            TabEveryDays => _dayEvery,
            _ => 1
        };
        set
        {
            switch (Tab)
            {
                case TabWeekly: _weekEvery = Clamp(value, 1); break;
                case TabMonthly: _monthEvery = Clamp(value, 1); break;
                case TabEveryDays: _dayEvery = Clamp(value, 2); break;   // 1일마다는 매일이다
                default: return;
            }
            Changed();
            FollowDefaultSticky();
        }
    }

    /// <summary>매월을 날짜가 아니라 'n번째 요일'로 고르는지.</summary>
    public bool ByNth { get; private set; }

    public int Nth { get; private set; }
    public int NthDay { get; private set; }
    public bool LastDay { get; private set; }
    public bool Sticky { get; private set; }

    /// <summary>매일에는 '남기기'가 뜻이 없다 — 내일 또 온다.</summary>
    public bool ShowSticky => Tab != TabDaily;

    public int Month
    {
        get => _month;
        set
        {
            _month = Math.Clamp(value, 1, 12);
            _day = Math.Min(_day, DaysIn(_month));
            Changed();
        }
    }

    public int Day
    {
        get => _day;
        set
        {
            _day = Math.Clamp(value, 1, DaysIn(_month));
            Changed();
        }
    }

    /// <summary>그 달의 가장 긴 날수. 2월은 29일까지 받는다(평년에는 28일에 온다).</summary>
    private static int DaysIn(int month) => DateTime.DaysInMonth(2028, month);

    private static int Clamp(int value, int min) => Math.Clamp(value, min, MaxEvery);

    public bool IsDayOn(int day) => _days.Contains(day);
    public bool IsMonthDayOn(int day) => _monthDays.Contains(day);

    public void SetTab(int tab)
    {
        if (tab is < TabDaily or > TabEveryDays || tab == Tab) return;
        Tab = tab;
        Changed();
        FollowDefaultSticky();
    }

    public void ToggleDay(int day)
    {
        if (day is < 0 or > 6) return;
        if (!_days.Remove(day)) _days.Add(day);
        Changed();
    }

    public void SetWeekdays() => SetDays(1, 2, 3, 4, 5);

    public void SetWeekend() => SetDays(6, 0);

    private void SetDays(params int[] days)
    {
        _days.Clear();
        foreach (var day in days) _days.Add(day);
        Changed();
    }

    public void ToggleMonthDay(int day)
    {
        if (day is < 1 or > 31) return;
        if (!_monthDays.Remove(day)) _monthDays.Add(day);
        Changed();
    }

    public void ToggleLastDay()
    {
        LastDay = !LastDay;
        Changed();
    }

    public void SetByNth(bool byNth)
    {
        if (ByNth == byNth) return;
        ByNth = byNth;
        Changed();
    }

    public void SetNth(int nth)
    {
        if (nth is < 1 or > RepeatUnit.LastWeek) return;
        Nth = nth;
        Changed();
    }

    public void SetNthDay(int day)
    {
        if (day is < 0 or > 6) return;
        NthDay = day;
        Changed();
    }

    /// <summary>사람이 스위치를 눌렀다. 이 뒤로는 종류를 바꿔도 기본값을 따라가지 않는다.</summary>
    public void SetSticky(bool sticky)
    {
        Sticky = sticky;
        _stickyTouched = true;
    }

    /// <summary>일정을 정하는 값이 바뀌었다. 옮겨 둔 첫 차례는 처음 자리로.</summary>
    private void Changed() => _shift = 0;

    /// <summary>
    /// 종류(탭)나 간격이 바뀌면 '남기기'가 그 종류의 기본값을 따라간다 — 매주는 지나감, 격주 · 매월은 남음.
    /// 요일이나 날짜를 고치는 것으로는 바뀌지 않는다: 남기도록 해 둔 매주 월요일에 수요일을 더했다고
    /// 말없이 '지나감'이 되면 그 루틴은 더는 남지 않는다.
    /// </summary>
    private void FollowDefaultSticky()
    {
        if (!_stickyTouched) Sticky = RepeatEngine.DefaultSticky(Unit, Every);
    }

    private int Unit => Tab switch
    {
        TabDaily => RepeatUnit.Daily,
        TabWeekly => RepeatUnit.Weekly,
        TabMonthly => ByNth ? RepeatUnit.MonthNth : RepeatUnit.MonthDates,
        TabYearly => RepeatUnit.Yearly,
        _ => RepeatUnit.EveryDays
    };

    /// <summary>첫 차례를 한 차례씩 앞뒤로. 오늘보다 앞으로는 가지 않는다.</summary>
    public void ShiftFirst(int steps)
    {
        if (steps == 0 || !CanSave || Tab == TabDaily) return;

        var now = Build();
        var basis = now.Clone();
        if (_shift == 0) basis.Anchor = FirstDate ?? now.Anchor;   // 지켜 둔 옛 첫 차례가 아니라 눈에 보이는 다음 차례에서 옮긴다

        if (RepeatEngine.ShiftFirst(basis, Math.Sign(steps), _today).Anchor != basis.Anchor) _shift += Math.Sign(steps);
    }

    public bool CanShiftBack => CanShift(-1);
    public bool CanShiftForward => CanShift(+1);

    private bool CanShift(int direction)
    {
        if (!CanSave || Tab == TabDaily) return false;

        var basis = Build();
        if (_shift == 0) basis.Anchor = FirstDate ?? basis.Anchor;
        return RepeatEngine.ShiftFirst(basis, direction, _today).Anchor != basis.Anchor;
    }

    public bool CanSave => RepeatEngine.IsValid(Draft());

    /// <summary>첫 차례를 뺀 고른 값.</summary>
    private RepeatRule Draft()
    {
        var rule = new RepeatRule { Unit = Unit, Every = Every, Sticky = Unit != RepeatUnit.Daily && Sticky };

        switch (rule.Unit)
        {
            case RepeatUnit.Weekly:
                rule.Days.AddRange(_days.OrderBy(d => (d + 6) % 7));
                break;

            case RepeatUnit.MonthDates:
                rule.MonthDays.AddRange(_monthDays);
                if (LastDay) rule.MonthDays.Add(RepeatUnit.LastDay);
                break;

            case RepeatUnit.MonthNth:
                rule.Nth = Nth;
                rule.NthDay = NthDay;
                break;

            case RepeatUnit.Yearly:
                rule.Month = _month;
                rule.Day = _day;
                break;
        }

        return rule;
    }

    /// <summary>저장할 규칙. 고른 값이 비었으면(요일 없음 · 날짜 없음) 첫 차례 없이 돌려준다 — CanSave 가 거짓이다.</summary>
    public RepeatRule Build()
    {
        var rule = Draft();
        if (rule.Unit == RepeatUnit.Daily || !RepeatEngine.IsKnown(rule)) return rule;

        // 주기가 그대로면 지금 일정을 지킨다.
        if (_start is { } start && start.Anchor != default && RepeatEngine.SameCadence(start, rule))
        {
            if (_shift == 0)
            {
                // 지나가던 반복에 '남기기'를 켜는 것만은 오늘부터 센다. 옛 첫 차례를 두면
                // 그동안 지나간 차례가 한꺼번에 "밀림 300일"로 붙는다.
                rule.Anchor = rule.Sticky && !start.Sticky
                    ? RepeatEngine.Next(start, _today) ?? start.Anchor
                    : start.Anchor;
                return rule;
            }

            // 옮길 때는 실제 다음 차례에서부터 센다(격주의 '이번 주 / 다음 주').
            rule.Anchor = RepeatEngine.Next(start, _today) ?? RepeatEngine.WithFirst(rule, _today).Anchor;
        }
        else
        {
            rule = RepeatEngine.WithFirst(rule, _today);
        }

        return _shift == 0 ? rule : RepeatEngine.ShiftFirst(rule, _shift, _today);
    }

    /// <summary>오늘 이후 처음 오는 차례. 저장할 수 없는 값이면 null.</summary>
    public DateOnly? FirstDate => CanSave ? RepeatEngine.Next(Build(), _today) : null;

    /// <summary>"오늘" 또는 "12월 25일 (금)".</summary>
    public string FirstText
    {
        get
        {
            if (Tab == TabDaily) return "오늘";
            if (FirstDate is not { } first) return "";

            return first == _today ? "오늘" : $"{first.Month}월 {first.Day}일 ({DayNames[(int)first.DayOfWeek]})";
        }
    }
}
