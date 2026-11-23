using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Models;
using Flow.Services;

namespace Flow.ViewModels;

/// <summary>반복 창의 고르는 칸 하나(요일 · 날짜 · 몇째). 눌러서 켜고 끈다.</summary>
public sealed partial class RepeatChoice : ObservableObject
{
    private readonly Action<int> _pick;

    [ObservableProperty] private bool _isOn;

    public RepeatChoice(int value, string label, Action<int> pick)
    {
        Value = value;
        Label = label;
        _pick = pick;
    }

    public int Value { get; }
    public string Label { get; }

    [RelayCommand]
    private void Pick() => _pick(Value);
}

/// <summary>
/// 반복 창. 위젯 안에 뜨는 판이고, 규칙은 <see cref="RepeatEditorState"/> 가 맡는다 —
/// 여기는 그 값을 화면에 내놓고 누른 것을 넘기기만 한다.
/// </summary>
public sealed partial class RepeatEditorViewModel : ObservableObject
{
    private static readonly string[] DayNames = { "일", "월", "화", "수", "목", "금", "토" };
    private static readonly int[] MondayFirst = { 1, 2, 3, 4, 5, 6, 0 };
    private static readonly string[] NthNames = { "첫째", "둘째", "셋째", "넷째", "마지막" };

    private readonly RepeatEditorState _state;
    private readonly Action<RepeatRule> _save;
    private readonly Action _close;

    public RepeatEditorViewModel(RepeatRule start, DateOnly today, string title, Action<RepeatRule> save, Action close)
    {
        _state = new RepeatEditorState(start, today);
        _save = save;
        _close = close;
        Title = title;

        WeekDays = MondayFirst.Select(d => new RepeatChoice(d, DayNames[d], day => Apply(() => _state.ToggleDay(day)))).ToList();
        MonthDays = Enumerable.Range(1, 31).Select(d => new RepeatChoice(d, d.ToString(), day => Apply(() => _state.ToggleMonthDay(day)))).ToList();
        NthChoices = Enumerable.Range(1, 5).Select(n => new RepeatChoice(n, NthNames[n - 1], nth => Apply(() => _state.SetNth(nth)))).ToList();
        NthDays = MondayFirst.Select(d => new RepeatChoice(d, DayNames[d], day => Apply(() => _state.SetNthDay(day)))).ToList();

        Refresh();
    }

    /// <summary>무엇의 반복을 정하는지. 판 제목 아래에 흐리게 적는다.</summary>
    public string Title { get; }

    public IReadOnlyList<RepeatChoice> WeekDays { get; }
    public IReadOnlyList<RepeatChoice> MonthDays { get; }
    public IReadOnlyList<RepeatChoice> NthChoices { get; }
    public IReadOnlyList<RepeatChoice> NthDays { get; }

    public bool IsDailyTab => _state.Tab == RepeatEditorState.TabDaily;
    public bool IsWeeklyTab => _state.Tab == RepeatEditorState.TabWeekly;
    public bool IsMonthlyTab => _state.Tab == RepeatEditorState.TabMonthly;
    public bool IsYearlyTab => _state.Tab == RepeatEditorState.TabYearly;
    public bool IsEveryDaysTab => _state.Tab == RepeatEditorState.TabEveryDays;

    /// <summary>간격 칸이 있는 탭인지(매주 · 매월 · n일마다).</summary>
    public bool HasEvery => IsWeeklyTab || IsMonthlyTab || IsEveryDaysTab;

    public int Every => _state.Every;

    /// <summary>간격 숫자 뒤에 붙는 말.</summary>
    public string EveryUnit => IsWeeklyTab ? "주마다" : IsMonthlyTab ? "개월마다" : "일마다";

    public bool ByNth => _state.ByNth;
    public bool ByDate => !_state.ByNth;

    /// <summary>매월 탭의 날짜 고르기 / 요일 고르기.</summary>
    public bool ShowMonthDates => IsMonthlyTab && !_state.ByNth;
    public bool ShowMonthNth => IsMonthlyTab && _state.ByNth;

    public bool IsLastDayOn => _state.LastDay;

    public string MonthText => $"{_state.Month}월";
    public string DayText => $"{_state.Day}일";

    public bool ShowSticky => _state.ShowSticky;

    /// <summary>스위치. 사람이 누른 것만 '누름'으로 친다 — 화면을 맞추느라 값이 바뀐 것은 아니다.</summary>
    public bool Sticky
    {
        get => _state.Sticky;
        set
        {
            if (value == _state.Sticky) return;
            Apply(() => _state.SetSticky(value));
        }
    }

    /// <summary>지금 고른 반복을 한 줄로. "매월 25일" · "격주 월". 고른 값이 비었으면 무엇이 비었는지.</summary>
    public string Summary => _state.CanSave
        ? RepeatEngine.Describe(_state.Build())
        : IsWeeklyTab ? "요일을 골라 주세요" : "날짜를 골라 주세요";

    public bool ShowFirst => _state.CanSave && !IsDailyTab;
    public string FirstText => _state.FirstText;
    public bool CanShiftBack => _state.CanShiftBack;
    public bool CanShiftForward => _state.CanShiftForward;

    public bool CanSave => _state.CanSave;

    [RelayCommand]
    private void SelectTab(string index)
    {
        if (int.TryParse(index, out var tab)) Apply(() => _state.SetTab(tab));
    }

    [RelayCommand]
    private void EveryUp() => Apply(() => _state.Every++);

    [RelayCommand]
    private void EveryDown() => Apply(() => _state.Every--);

    [RelayCommand]
    private void Weekdays() => Apply(_state.SetWeekdays);

    [RelayCommand]
    private void Weekend() => Apply(_state.SetWeekend);

    [RelayCommand]
    private void PickByDate() => Apply(() => _state.SetByNth(false));

    [RelayCommand]
    private void PickByNth() => Apply(() => _state.SetByNth(true));

    [RelayCommand]
    private void ToggleLastDay() => Apply(_state.ToggleLastDay);

    // 월 · 일은 끝에서 처음으로 돈다(12월 다음은 1월). 열두 번 누르는 것보다 한 번 뒤로 가는 편이 빠르다.
    [RelayCommand]
    private void MonthUp() => Apply(() => _state.Month = _state.Month == 12 ? 1 : _state.Month + 1);

    [RelayCommand]
    private void MonthDown() => Apply(() => _state.Month = _state.Month == 1 ? 12 : _state.Month - 1);

    [RelayCommand]
    private void DayUp() => Apply(() =>
    {
        var before = _state.Day;
        _state.Day = before + 1;
        if (_state.Day == before) _state.Day = 1;
    });

    [RelayCommand]
    private void DayDown() => Apply(() => _state.Day = _state.Day == 1 ? 31 : _state.Day - 1);

    [RelayCommand]
    private void ShiftBack() => Apply(() => _state.ShiftFirst(-1));

    [RelayCommand]
    private void ShiftForward() => Apply(() => _state.ShiftFirst(+1));

    [RelayCommand]
    private void Save()
    {
        if (!_state.CanSave) return;
        _save(_state.Build());
    }

    [RelayCommand]
    private void Cancel() => _close();

    private void Apply(Action change)
    {
        change();
        Refresh();
    }

    private void Refresh()
    {
        foreach (var choice in WeekDays) choice.IsOn = _state.IsDayOn(choice.Value);
        foreach (var choice in MonthDays) choice.IsOn = _state.IsMonthDayOn(choice.Value);
        foreach (var choice in NthChoices) choice.IsOn = _state.Nth == choice.Value;
        foreach (var choice in NthDays) choice.IsOn = _state.NthDay == choice.Value;

        // 값 하나가 여러 칸을 바꾼다(탭 → 간격 말 · 남기기 · 첫 차례 …). 통째로 다시 읽게 한다.
        OnPropertyChanged(string.Empty);
    }
}
