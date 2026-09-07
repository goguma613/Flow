using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Models;

namespace Flow.ViewModels;

public abstract partial class RowBase : ObservableObject
{
    [ObservableProperty] private bool _isEditing;

    /// <summary>모델에서 뷰로 값을 밀어 넣는 동안 변경 콜백이 되돌아 실행되는 것을 막는다.</summary>
    protected bool Suppressed { get; set; }

    /// <summary>이름 편집을 끝내고 값을 확정한다. (Enter · 포커스 이탈)</summary>
    public abstract void CommitRename();

    /// <summary>편집을 취소하고 원래 이름으로 되돌린다. (Esc)</summary>
    public abstract void CancelRename();
}

public sealed partial class RoutineRow : RowBase
{
    private static readonly string[] ShortDayNames = ["일", "월", "화", "수", "목", "금", "토"];

    private readonly MainViewModel _owner;

    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _title = "";

    public RoutineRow(Routine model, MainViewModel owner)
    {
        Model = model;
        _owner = owner;
        Sync();
    }

    public Routine Model { get; }

    public int Streak => Model.Streak;
    public bool HasStreak => Model.Streak > 1;
    public bool IsEveryDay => Model.IsEveryDay;

    /// <summary>오늘 해야 하는 루틴인지. 아닌 날에는 체크할 수 없다.</summary>
    public bool IsScheduledToday => _owner.CanToggleRoutine(this);

    public string DaysText => Model.IsEveryDay
        ? "매일"
        : string.Join(" ", Model.Days
            .OrderBy(d => ((int)d + 6) % 7)
            .Select(d => ShortDayNames[(int)d]));

    public void Sync()
    {
        Suppressed = true;
        Title = Model.Title;
        IsDone = Model.DoneToday;
        Suppressed = false;

        OnPropertyChanged(nameof(Streak));
        OnPropertyChanged(nameof(HasStreak));
        OnPropertyChanged(nameof(DaysText));
        OnPropertyChanged(nameof(IsEveryDay));
        OnPropertyChanged(nameof(IsScheduledToday));
    }

    partial void OnIsDoneChanged(bool value)
    {
        if (Suppressed) return;
        _owner.SetRoutineDone(this, value);
    }

    partial void OnTitleChanged(string value)
    {
        if (Suppressed || string.IsNullOrWhiteSpace(value)) return;
        Model.Title = value.Trim();
        _owner.Persist();
    }

    /// <summary>행 전체가 클릭 대상이다. 작은 원을 정확히 노릴 필요가 없다.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (!IsScheduledToday) return;
        IsDone = !IsDone;
    }

    [RelayCommand]
    private void Delete() => _owner.DeleteRoutine(this);

    private string? _titleBeforeEdit;

    [RelayCommand]
    private void BeginRename()
    {
        _titleBeforeEdit = Model.Title;
        IsEditing = true;
    }

    public override void CommitRename()
    {
        _titleBeforeEdit = null;
        IsEditing = false;
        RestoreTitleFromModel();
    }

    public override void CancelRename()
    {
        if (_titleBeforeEdit is not null)
        {
            Model.Title = _titleBeforeEdit;
            _titleBeforeEdit = null;
            _owner.Persist();
        }

        IsEditing = false;
        RestoreTitleFromModel();
    }

    /// <summary>빈 제목으로 저장하려 한 경우 등, 화면 값을 모델 값으로 되돌린다.</summary>
    private void RestoreTitleFromModel()
    {
        Suppressed = true;
        Title = Model.Title;
        Suppressed = false;
    }
}

public sealed partial class TaskRow : RowBase
{
    private readonly MainViewModel _owner;

    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _title = "";

    public TaskRow(TaskItem model, MainViewModel owner, DateOnly today)
    {
        Model = model;
        _owner = owner;
        Today = today;
        Sync(today);
    }

    public TaskItem Model { get; }
    public DateOnly Today { get; private set; }

    public bool IsHigh => Model.Priority == Priority.High;
    public bool IsMedium => Model.Priority == Priority.Medium;
    public bool IsLow => Model.Priority == Priority.Low;

    public bool IsOverdue => !Model.Done && Model.Due.HasValue && Model.Due.Value < Today;
    public bool IsDueToday => Model.Due.HasValue && Model.Due.Value == Today;
    public bool HasDue => Model.Due.HasValue;
    public bool HasCarryOver => Model.CarryOverCount > 0 && !Model.Done;
    public string CarryOverText => $"밀림 {Model.CarryOverCount}일";

    public string DueText
    {
        get
        {
            if (!Model.Due.HasValue) return "";

            var due = Model.Due.Value;
            var delta = due.DayNumber - Today.DayNumber;
            var label = delta switch
            {
                < 0 => "지남",
                0 => "오늘",
                1 => "내일",
                2 => "모레",
                < 7 => $"{delta}일 뒤",
                _ => $"{due.Month}/{due.Day}"
            };

            return Model.DueTime.HasValue
                ? $"{label} {Model.DueTime.Value:HH:mm}"
                : label;
        }
    }

    public void Sync(DateOnly today)
    {
        Today = today;

        Suppressed = true;
        Title = Model.Title;
        IsDone = Model.Done;
        Suppressed = false;

        OnPropertyChanged(nameof(IsHigh));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsLow));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(IsDueToday));
        OnPropertyChanged(nameof(HasDue));
        OnPropertyChanged(nameof(HasCarryOver));
        OnPropertyChanged(nameof(CarryOverText));
        OnPropertyChanged(nameof(DueText));
    }

    partial void OnIsDoneChanged(bool value)
    {
        if (Suppressed) return;
        _owner.SetTaskDone(this, value);
    }

    partial void OnTitleChanged(string value)
    {
        if (Suppressed || string.IsNullOrWhiteSpace(value)) return;
        Model.Title = value.Trim();
        _owner.Persist();
    }

    /// <summary>행 전체가 클릭 대상이다. 작은 원을 정확히 노릴 필요가 없다.</summary>
    [RelayCommand]
    private void Toggle() => IsDone = !IsDone;

    [RelayCommand]
    private void Delete() => _owner.DeleteTask(this);

    [RelayCommand]
    private void CyclePriority() => _owner.CyclePriority(this);

    private string? _titleBeforeEdit;

    [RelayCommand]
    private void BeginRename()
    {
        _titleBeforeEdit = Model.Title;
        IsEditing = true;
    }

    public override void CommitRename()
    {
        _titleBeforeEdit = null;
        IsEditing = false;
        RestoreTitleFromModel();
    }

    public override void CancelRename()
    {
        if (_titleBeforeEdit is not null)
        {
            Model.Title = _titleBeforeEdit;
            _titleBeforeEdit = null;
            _owner.Persist();
        }

        IsEditing = false;
        RestoreTitleFromModel();
    }

    private void RestoreTitleFromModel()
    {
        Suppressed = true;
        Title = Model.Title;
        Suppressed = false;
    }
}
