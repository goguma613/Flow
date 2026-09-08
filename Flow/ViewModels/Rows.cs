using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Models;
using Flow.Services;

namespace Flow.ViewModels;

public abstract partial class RowBase : ObservableObject
{
    [ObservableProperty] private bool _isEditing;

    /// <summary>컴팩트 모드에서는 곁다리(연속일수·삭제 버튼)를 접고 줄 높이를 줄인다.</summary>
    [ObservableProperty] private bool _isCompact;

    /// <summary>
    /// 알림이 울려서 아직 처리되지 않은 상태. 처리할 때까지 물든 채로 남는다 —
    /// 신호가 사라지지 않으면 창 가림 감지도, 재알림도 필요 없다.
    /// </summary>
    [ObservableProperty] private bool _isRinging;

    /// <summary>물든 줄의 오른쪽에 적는 말. "지금" 또는 "10분 지남".</summary>
    [ObservableProperty] private string _ringText = "";

    partial void OnIsCompactChanged(bool value) => OnCompactChanged();

    partial void OnIsRingingChanged(bool value) => OnCompactChanged();

    /// <summary>컴팩트 여부에 따라 값이 달라지는 속성이 있으면 여기서 다시 알린다.</summary>
    protected virtual void OnCompactChanged()
    {
    }

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
        IsCompact = owner.IsCompact;
        Sync();
    }

    protected override void OnCompactChanged()
    {
        OnPropertyChanged(nameof(ShowStreak));
        OnPropertyChanged(nameof(HasReminder));
        OnPropertyChanged(nameof(ShowTime));
        OnPropertyChanged(nameof(TimeText));
    }

    public Routine Model { get; }

    public int Streak => Model.Streak;
    public bool HasStreak => Model.Streak > 1;

    /// <summary>연속일수 불꽃은 컴팩트에서 접는다. 제목을 읽는 데 방해가 된다.</summary>
    /// <remarks>울리는 동안에는 그 자리를 알림 표시에 내준다. 둘이 겹치면 시끄럽다.</remarks>
    public bool ShowStreak => HasStreak && !IsCompact && !IsRinging;

    /// <summary>알림이 걸려 있는지. 우클릭 메뉴의 '알림 끄기'가 이걸 본다.</summary>
    public bool HasReminder => Model.Remind;

    public string TimeText => Model.Time is { } time ? time.ToString("HH:mm") : "";

    /// <summary>
    /// 시각은 늘 보인다. 걸어 둔 시각을 확인할 방법이 없으면 알림을 믿기 어렵다.
    /// 울리는 동안에는 그 자리에 "지금"이 들어가므로 비켜 준다.
    /// </summary>
    public bool ShowTime => Model.Time.HasValue && !IsRinging;

    [RelayCommand]
    private void Snooze() => _owner.SnoozeReminder(this, Model.Id, true);

    [RelayCommand]
    private void Mute() => _owner.MuteReminder(Model.Id, true);
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
        OnPropertyChanged(nameof(ShowStreak));
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
        IsCompact = owner.IsCompact;
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

    /// <summary>울리는 동안에는 마감 뱃지 자리를 알림 표시가 쓴다.</summary>
    public bool ShowDue => HasDue && !IsRinging;

    /// <summary>알림이 걸려 있는지. 우클릭 메뉴의 '알림 끄기'가 이걸 본다.</summary>
    public bool HasReminder => Model.Remind;

    protected override void OnCompactChanged()
    {
        OnPropertyChanged(nameof(ShowDue));
        OnPropertyChanged(nameof(HasReminder));
    }

    [RelayCommand]
    private void Snooze() => _owner.SnoozeReminder(this, Model.Id, false);

    [RelayCommand]
    private void Mute() => _owner.MuteReminder(Model.Id, false);
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

public sealed partial class BackupRow : ObservableObject
{
    private readonly MainViewModel _owner;

    [ObservableProperty] private bool _isConfirming;

    public BackupRow(BackupEntry entry, MainViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public BackupEntry Entry { get; }

    public string WhenText => Entry.CreatedAt.ToString("M월 d일 (ddd) HH:mm");

    public string DetailText => $"루틴 {Entry.Routines} · 할 일 {Entry.Tasks}";

    public bool HasTag => Entry.Tag.Length > 0;

    public string TagText => Entry.Tag switch
    {
        "update" => "업데이트 직전",
        "manual" => "직접 만듦",
        "before-restore" => "되돌리기 직전",
        _ => Entry.Tag
    };

    [RelayCommand]
    private void Ask() => IsConfirming = true;

    [RelayCommand]
    private void Cancel() => IsConfirming = false;

    [RelayCommand]
    private void Confirm() => _owner.RestoreFrom(this);
}
