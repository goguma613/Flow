using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Models;
using Flow.Services;

namespace Flow.ViewModels;

public sealed partial class HeatCell(DateOnly date, double rate, bool isToday) : ObservableObject
{
    public DateOnly Date { get; } = date;
    public double Rate { get; } = rate;
    public bool IsToday { get; } = isToday;

    public int Level => Rate switch
    {
        <= 0 => 0,
        < 0.34 => 1,
        < 0.67 => 2,
        < 1 => 3,
        _ => 4
    };

    public bool Level0 => Level == 0;
    public bool Level1 => Level == 1;
    public bool Level2 => Level == 2;
    public bool Level3 => Level == 3;
    public bool Level4 => Level == 4;

    public string Tooltip => $"{Date.Month}/{Date.Day} · 달성 {Math.Round(Rate * 100)}%";
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 분 경계를 얼마나 지나서 깨울지. 0으로 두면 타이머가 살짝 이르게 깨어
    /// 아직 그 분이 되지 않았다고 판정하고 다음 분까지 통째로 놓친다.
    /// </summary>
    private static readonly TimeSpan TickGuard = TimeSpan.FromMilliseconds(150);

    private const int HeatmapDays = 28;

    /// <summary>켜 둔 채로 며칠 지나는 경우를 위한 재확인 주기. 시작할 때는 이와 무관하게 한 번 본다.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(20);

    /// <summary>
    /// 창이 뜨고 자리를 잡을 때까지만 기다린다.
    /// 확인 자체는 배경에서 도는 작은 요청이라 시작을 늦추지 않는다.
    /// </summary>
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// 진행 표시를 최소 이만큼은 띄워 둔다.
    /// 회선이 빠르면 47MB가 2초 만에 끝나는데, 그대로면 번쩍하고 사라져 고장처럼 보인다.
    /// </summary>
    private static readonly TimeSpan MinimumBusyDisplay = TimeSpan.FromSeconds(1.6);

    private readonly DataStore _store;
    private readonly UpdateService _updates = new();
    private DispatcherTimer? _updateTimer;
    private DateTime _busyStartedAt;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _busyTimer;

    private AppData _data;
    private DateOnly _today;

    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string _weekdayText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _hasStatus;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _quickAddText = "";
    [ObservableProperty] private string _quickAddHint = "";
    [ObservableProperty] private string _quickAddPlaceholder = "빠른 추가 — 내일 3시 회의 !1";
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _isHelpOpen;

    /// <summary>창을 직접 작게 줄였을 때. 머리말을 접어 목록에 자리를 내준다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChromeVisible))]
    [NotifyPropertyChangedFor(nameof(BottomVisible))]
    [NotifyPropertyChangedFor(nameof(ShowBottomHint))]
    [NotifyPropertyChangedFor(nameof(TabRow))]
    [NotifyPropertyChangedFor(nameof(ListRowSpan))]
    [NotifyPropertyChangedFor(nameof(ShowRoutineLabel))]
    [NotifyPropertyChangedFor(nameof(ShowTaskLabel))]
    [NotifyPropertyChangedFor(nameof(ShowAllRoutineLabel))]
    [NotifyPropertyChangedFor(nameof(ShowUpcomingLabel))]
    private bool _isCompact;

    /// <summary>컴팩트일 때 마우스가 창 위에 있거나 입력칸에 커서가 놓인 상태.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChromeVisible))]
    private bool _compactRevealed;

    /// <summary>컴팩트일 때 아래쪽 묶음(탭·입력칸)이 올라와 있는 상태.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BottomVisible))]
    [NotifyPropertyChangedFor(nameof(ShowBottomHint))]
    private bool _compactBottomRevealed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateNotice))]
    private bool _updateReady;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>확인·내려받기가 진행 중일 때만 켜진다. 위쪽에 진행 줄을 띄운다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateNotice))]
    private bool _isUpdateBusy;
    [ObservableProperty] private bool _isUpdateDownloading;
    [ObservableProperty] private string _updateBusyText = "";
    [ObservableProperty] private string _updatePercentText = "";
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _backupSummary = "";
    [ObservableProperty] private bool _isRestoreOpen;
    [ObservableProperty] private bool _hasBackups;
    [ObservableProperty] private bool _showCompleted;

    /// <summary>오늘 화면 아래에서 예정 목록을 펼쳐 놓았는지.</summary>
    [ObservableProperty] private bool _showUpcomingOnToday;
    [ObservableProperty] private string _completedHeader = "";
    [ObservableProperty] private bool _hasCompleted;
    [ObservableProperty] private string _upcomingHeader = "";
    [ObservableProperty] private bool _todayIsEmpty;
    [ObservableProperty] private string _streakSummary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoutineLabel))]
    private bool _hasTodayRoutines;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTaskLabel))]
    private bool _hasTodayTasks;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpcomingLabel))]
    private bool _hasUpcoming;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllRoutineLabel))]
    private bool _hasRoutines;
    [ObservableProperty] private bool _upcomingIsEmpty;

    public MainViewModel()
    {
        _store = new DataStore();
        _data = _store.Load();
        _today = DayEngine.LogicalDate(DateTime.Now, _data.Settings.DayStartHour);

        var result = DayEngine.Rollover(_data, _today);
        DayEngine.SyncToday(_data, _today);
        _store.RequestSave(_data);

        // 고정 주기로 돌면 앱을 켠 시점에 따라 분 경계와 어긋난다.
        // 21:22 알림이 21:22:28에 울리는 식인데, 사람은 그걸 늦다고 느낀다.
        // 알림 시각은 늘 분 단위이므로 분 경계 바로 뒤로 맞춘다 —
        // 늦는 느낌이 사라지고, 깨는 횟수는 분당 2번에서 1번으로 오히려 줄어든다.
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            ScheduleNextTick();
            CheckRollover();
            FireDueReminders(DateTime.Now);
        };

        ScheduleNextTick();
        _timer.Start();

        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _busyTimer.Tick += (_, _) =>
        {
            _busyTimer.Stop();
            IsUpdateBusy = false;
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            HasStatus = false;
        };

        // exe 위치가 바뀌었을 수 있으니 자동 시작 등록을 다시 맞춘다.
        if (_data.Settings.RunAtStartup) StartupService.SetEnabled(true);

        // 처음 켠 사람에게는 사용법을 먼저 보여준다.
        IsHelpOpen = _store.StartedFresh;

        UpdateService.CleanUpAfterUpdate();
        ScheduleUpdateCheck();
        BackUpIfDue();
        RefreshBackupSummary();

        RebuildAll();
        if (result.Changed) Announce(result.DaysElapsed == 1
            ? "새 하루 시작 · 루틴을 초기화했습니다"
            : $"{result.DaysElapsed}일치를 정산하고 초기화했습니다");
    }

    public ObservableCollection<RoutineRow> TodayRoutines { get; } = [];
    public ObservableCollection<RoutineRow> AllRoutines { get; } = [];
    public ObservableCollection<TaskRow> TodayTasks { get; } = [];
    public ObservableCollection<TaskRow> CompletedTasks { get; } = [];
    public ObservableCollection<TaskRow> UpcomingTasks { get; } = [];
    public ObservableCollection<HeatCell> Heatmap { get; } = [];
    public ObservableCollection<BackupRow> Backups { get; } = [];

    /// <summary>
    /// 울렸는데 아직 처리되지 않은 항목들. 파일에 남기지 않는다 —
    /// 앱을 껐다 켜면 사라지지만, 그때는 목록에 마감 시각이 그대로 보인다.
    /// </summary>
    private readonly HashSet<Guid> _ringing = [];

    public AppSettings Settings => _data.Settings;

    /// <summary>새 버전을 받아 적용했을 때. 창에서 앱을 다시 시작시킨다.</summary>
    public event Action? RestartRequested;

    public string AppVersionText => $"버전 {UpdateService.CurrentText}";

    public bool AutoUpdate
    {
        get => _data.Settings.AutoUpdate;
        set
        {
            if (_data.Settings.AutoUpdate == value) return;
            _data.Settings.AutoUpdate = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public string DataPath => DataStore.FilePath;

    public bool AutoBackup
    {
        get => _data.Settings.AutoBackup;
        set
        {
            if (_data.Settings.AutoBackup == value) return;
            _data.Settings.AutoBackup = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool IsTodayTab => SelectedTab == 0;
    public bool IsRoutineTab => SelectedTab == 1;
    public bool IsUpcomingTab => SelectedTab == 2;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsTodayTab));
        OnPropertyChanged(nameof(IsRoutineTab));
        OnPropertyChanged(nameof(IsUpcomingTab));

        QuickAddPlaceholder = value switch
        {
            1 => "루틴 추가 — 월수금 운동 30분",
            2 => "예정 추가 — 9/15 정산 마감",
            _ => "빠른 추가 — 내일 3시 회의 !1"
        };

        // 탭이 바뀌면 같은 문장도 다르게 해석되므로 미리보기를 다시 만든다.
        UpdateQuickAddHint(QuickAddText);
    }

    /// <summary>
    /// 머리말 버튼을 지금 보여줄지. 보통 모드에서는 늘 보이고,
    /// 컴팩트에서는 마우스가 창 위에 있을 때만 보인다.
    /// 버튼은 머리말에 비워 둔 자리에 뜨므로 목록을 가리지 않는다.
    /// </summary>
    public bool ChromeVisible => !IsCompact || CompactRevealed;

    /// <summary>
    /// 탭과 입력칸을 지금 보여줄지.
    /// 이 둘은 목록을 덮으므로 컴팩트에서는 아래쪽 끝에 다가갔을 때만 올라온다.
    /// 목록 위에서 떠 버리면 누르려던 항목을 가려 체크를 막는다.
    /// </summary>
    public bool BottomVisible => !IsCompact || CompactBottomRevealed;

    /// <summary>아래쪽에 뭔가 숨어 있다는 것을 알려 주는 손잡이.</summary>
    public bool ShowBottomHint => IsCompact && !CompactBottomRevealed;

    /// <summary>탭이 놓이는 줄. 컴팩트에서는 아래쪽 묶음으로 내려간다.</summary>
    public int TabRow => IsCompact ? 4 : 1;

    /// <summary>
    /// 컴팩트에서는 목록이 아래쪽 줄들까지 덮는다.
    /// 그래야 탭과 입력칸이 나타나고 사라져도 목록의 크기가 그대로다.
    /// </summary>
    public int ListRowSpan => IsCompact ? 4 : 1;

    /// <summary>업데이트 소식이 있을 때만 그 자리를 차지한다.</summary>
    public bool HasUpdateNotice => IsUpdateBusy || UpdateReady;

    public bool ShowRoutineLabel => HasTodayRoutines && !IsCompact;
    public bool ShowTaskLabel => HasTodayTasks && !IsCompact;
    public bool ShowAllRoutineLabel => HasRoutines && !IsCompact;
    public bool ShowUpcomingLabel => HasUpcoming && !IsCompact;

    /// <summary>설정에 저장되는 컴팩트 모드. 창을 작게 줄여 자동으로 걸린 것과는 별개다.</summary>
    public bool CompactMode
    {
        get => _data.Settings.CompactMode;
        set
        {
            if (_data.Settings.CompactMode == value) return;
            _data.Settings.CompactMode = value;
            OnPropertyChanged();
            Persist();
            CompactModeChanged?.Invoke();
        }
    }

    /// <summary>창 쪽에서 실제 접힘 여부를 다시 계산하도록 알린다.</summary>
    public event Action? CompactModeChanged;

    [RelayCommand]
    private void ToggleCompact() => CompactMode = !CompactMode;

    public bool RemindersEnabled
    {
        get => _data.Settings.RemindersEnabled;
        set
        {
            if (_data.Settings.RemindersEnabled == value) return;
            _data.Settings.RemindersEnabled = value;
            OnPropertyChanged();
            Persist();

            // 끄면 물든 줄도 같이 걷는다. 끈 기능이 화면에 남아 있으면 안 된다.
            if (!value)
            {
                _ringing.Clear();
                RefreshRinging();
            }
        }
    }

    public bool ReminderSound
    {
        get => _data.Settings.ReminderSound;
        set
        {
            if (_data.Settings.ReminderSound == value) return;
            _data.Settings.ReminderSound = value;
            OnPropertyChanged();
            Persist();
        }
    }

    /// <summary>설정 화면에 "다음 알림 09:00"을 적어 기능이 살아 있음을 보인다.</summary>
    public string NextReminderText
    {
        get
        {
            if (!_data.Settings.RemindersEnabled) return "꺼짐";
            if (ReminderEngine.NextAt(_data, DateTime.Now) is not { } next) return "예정된 알림 없음";

            var days = DateOnly.FromDateTime(next).DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
            var when = days switch
            {
                0 => "오늘",
                1 => "내일",
                _ => $"{next.Month}/{next.Day}"
            };

            return $"다음 {when} {next:HH:mm}";
        }
    }

    public bool AlwaysOnTop
    {
        get => _data.Settings.AlwaysOnTop;
        set
        {
            if (_data.Settings.AlwaysOnTop == value) return;
            _data.Settings.AlwaysOnTop = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool CarryOverIncomplete
    {
        get => _data.Settings.CarryOverIncomplete;
        set
        {
            if (_data.Settings.CarryOverIncomplete == value) return;
            _data.Settings.CarryOverIncomplete = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool RunAtStartup
    {
        get => _data.Settings.RunAtStartup;
        set
        {
            if (_data.Settings.RunAtStartup == value) return;
            _data.Settings.RunAtStartup = value;
            StartupService.SetEnabled(value);
            OnPropertyChanged();
            Persist();
        }
    }

    public double IdleOpacity
    {
        get => _data.Settings.IdleOpacity;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), 0.35, 1.0);
            if (Math.Abs(_data.Settings.IdleOpacity - clamped) < 0.001) return;
            _data.Settings.IdleOpacity = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOpacityFull));
            OnPropertyChanged(nameof(IsOpacityHigh));
            OnPropertyChanged(nameof(IsOpacityMid));
            OnPropertyChanged(nameof(IsOpacityLow));
            Persist();
        }
    }

    public int DayStartHour
    {
        get => _data.Settings.DayStartHour;
        set
        {
            var clamped = Math.Clamp(value, 0, 23);
            if (_data.Settings.DayStartHour == clamped) return;
            _data.Settings.DayStartHour = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DayStartHourText));
            Persist();
            CheckRollover();
        }
    }

    public string DayStartHourText => $"{_data.Settings.DayStartHour:00}:00";

    private static readonly double[] OpacityPresets = [1.00, 0.92, 0.80, 0.65];

    /// <summary>저장된 값이 프리셋과 정확히 일치하지 않아도(옛 버전 등) 가장 가까운 칩을 켜 준다.</summary>
    private double NearestOpacityPreset
        => OpacityPresets.MinBy(preset => Math.Abs(preset - IdleOpacity));

    public bool IsOpacityFull => Near(NearestOpacityPreset, 1.00);
    public bool IsOpacityHigh => Near(NearestOpacityPreset, 0.92);
    public bool IsOpacityMid => Near(NearestOpacityPreset, 0.80);
    public bool IsOpacityLow => Near(NearestOpacityPreset, 0.65);

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.005;

    [RelayCommand]
    private void SetIdleOpacity(string value)
    {
        if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            IdleOpacity = parsed;
    }

    [RelayCommand]
    private void StepDayStartHour(string delta)
    {
        if (!int.TryParse(delta, out var step)) return;
        DayStartHour = (DayStartHour + step + 24) % 24;
    }

    /// <summary>다음 분 경계 바로 뒤에 깨도록 간격을 다시 잡는다.</summary>
    private void ScheduleNextTick()
    {
        var now = DateTime.Now;
        var intoMinute = TimeSpan.FromSeconds(now.Second) + TimeSpan.FromMilliseconds(now.Millisecond);

        _timer.Interval = TimeSpan.FromMinutes(1) - intoMinute + TickGuard;
    }

    /// <summary>매 분, 그리고 창이 활성화될 때마다 날짜가 넘어갔는지 확인한다.</summary>
    public void CheckRollover()
    {
        var current = DayEngine.LogicalDate(DateTime.Now, _data.Settings.DayStartHour);
        if (current == _today)
        {
            DayEngine.SyncToday(_data, _today);
            return;
        }

        _today = current;
        var result = DayEngine.Rollover(_data, current);
        DayEngine.SyncToday(_data, current);
        _store.RequestSave(_data);

        _ringing.Clear();
        ReminderEngine.ClearStaleSnoozes(_data, DateTime.Now);

        BackUpIfDue();
        RefreshBackupSummary();
        RebuildAll();

        if (result.Changed)
        {
            Announce(result.DaysElapsed == 1
                ? "새 하루 시작 · 루틴을 초기화했습니다"
                : $"{result.DaysElapsed}일치를 정산하고 초기화했습니다");
        }
    }

    /// <summary>
    /// 알림이 새로 울렸다. 창을 밝힐지, 창이 숨어 있으니 Windows 알림을 띄울지는 창이 정한다.
    /// 인자는 머리말에 적는 것과 같은 한 줄.
    /// </summary>
    public event Action<string>? ReminderFired;

    /// <summary>Windows 알림에 소리를 낼지.</summary>
    public bool WantsReminderSound => _data.Settings.ReminderSound;

    /// <summary>
    /// 지금 알려야 할 것을 꺼내 화면에 물들인다.
    /// 유예를 넘긴 것은 엔진이 아예 내놓지 않으므로 여기서 걸러낼 것이 없다.
    /// </summary>
    private void FireDueReminders(DateTime now)
    {
        var pending = ReminderEngine.Pending(_data, now);
        if (pending.Count == 0) return;

        foreach (var item in pending)
        {
            ReminderEngine.MarkHandled(_data, item);
            _ringing.Add(item.OwnerId);
        }

        _ringingTitle = pending[0].Title;

        Persist();
        RefreshRinging();

        ReminderFired?.Invoke(pending.Count == 1
            ? pending[0].Title
            : $"{pending[0].Title} 외 {pending.Count - 1}건");
    }

    /// <summary>울리는 항목에 표시를 입힌다. 목록을 다시 만들 때마다 부른다.</summary>
    private void RefreshRinging()
    {
        foreach (var row in TodayRoutines) Mark(row, row.Model.Id);
        foreach (var row in AllRoutines) Mark(row, row.Model.Id);
        foreach (var row in TodayTasks) Mark(row, row.Model.Id);
        foreach (var row in UpcomingTasks) Mark(row, row.Model.Id);

        OnPropertyChanged(nameof(HasRinging));
        RefreshRingingHeader();

        void Mark(RowBase row, Guid id)
        {
            row.IsRinging = _ringing.Contains(id);
            row.RingText = row.IsRinging ? "지금" : "";
        }
    }

    public bool HasRinging => _ringing.Count > 0;

    /// <summary>처리했으니 물을 뺀다. 완료·미루기·끄기 모두 여기로 모인다.</summary>
    private void StopRinging(Guid id)
    {
        if (!_ringing.Remove(id)) return;

        RefreshRinging();
    }

    /// <summary>잠깐 뒤에 다시 알린다.</summary>
    public void SnoozeReminder(RowBase row, Guid id, bool isRoutine)
    {
        if (!ReminderEngine.Snooze(_data, id, isRoutine, DateTime.Now, _data.Settings.SnoozeMinutes)) return;

        StopRinging(id);
        Persist();
        Announce($"{_data.Settings.SnoozeMinutes}분 뒤에 다시 알립니다");
    }

    /// <summary>이 항목의 알림을 끈다. 시각은 남겨 두어 언제였는지는 계속 보인다.</summary>
    public void MuteReminder(Guid id, bool isRoutine)
    {
        if (isRoutine)
        {
            foreach (var routine in _data.Routines)
            {
                if (routine.Id == id) routine.Remind = false;
            }
        }
        else
        {
            foreach (var task in _data.Tasks)
            {
                if (task.Id == id) task.Remind = false;
            }
        }

        StopRinging(id);
        Persist();
        RebuildAll();
        Announce("알림을 껐습니다");
    }

    // ───────────────────────── 업데이트

    private void ScheduleUpdateCheck()
    {
        // 실행할 때마다 한 번 확인한다. 시작을 늦추지 않도록 잠깐만 미룬다.
        var startup = new DispatcherTimer { Interval = UpdateCheckDelay };
        startup.Tick += (_, _) =>
        {
            startup.Stop();
            _ = RunUpdateCheckAsync(manual: false);
        };
        startup.Start();

        // 껐다 켜지 않고 며칠씩 두는 경우를 위해 하루에 한 번 더 본다.
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += (_, _) => _ = RunUpdateCheckAsync(manual: false);
        _updateTimer.Start();
    }

    [RelayCommand]
    private Task CheckForUpdates() => RunUpdateCheckAsync(manual: true);

    private async Task RunUpdateCheckAsync(bool manual)
    {
        // 자동 확인을 꺼 뒀으면 아무 것도 하지 않는다. 직접 누른 경우는 그래도 확인한다.
        if (!manual && !_data.Settings.AutoUpdate) return;
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        UpdateStatus = "확인 중…";

        _busyTimer.Stop();
        IsUpdateBusy = true;
        _busyStartedAt = DateTime.Now;
        IsUpdateDownloading = false;
        UpdateProgress = 0;
        UpdatePercentText = "";
        UpdateBusyText = "새 버전이 있는지 확인하는 중…";

        var progress = new Progress<double>(value =>
        {
            // 내려받기가 시작된 순간부터 진행 줄을 보여준다.
            IsUpdateDownloading = true;
            UpdateProgress = value;
            UpdatePercentText = $"{Math.Round(value * 100)}%";
            UpdateBusyText = "새 버전을 내려받는 중…";
        });

        var found = await _updates.CheckAndStageAsync(progress).ConfigureAwait(true);
        await HoldBusyDisplayAsync().ConfigureAwait(true);

        _data.Settings.LastUpdateCheck = DateTime.Now;
        Persist();

        IsCheckingUpdate = false;
        IsUpdateDownloading = false;

        if (found is not null)
        {
            UpdateReady = true;
            UpdateText = $"새 버전 {found.Major}.{found.Minor}.{found.Build} 준비됨";
            UpdateStatus = UpdateText;

            // 준비되면 아래쪽 초록 막대가 대신 알려준다.
            IsUpdateBusy = false;
            Announce(UpdateText);
            return;
        }

        if (_updates.LastError is { Length: > 0 })
        {
            UpdateStatus = "확인하지 못했습니다";
            UpdateBusyText = "업데이트를 확인하지 못했습니다";
            _busyTimer.Start();
            if (manual) Announce("업데이트를 확인하지 못했습니다");
            return;
        }

        UpdateStatus = "최신 버전입니다";
        UpdateBusyText = "최신 버전입니다";
        _busyTimer.Start();
        if (manual) Announce("최신 버전입니다");
    }

    /// <summary>진행 표시가 눈에 남을 만큼은 유지한다.</summary>
    private async Task HoldBusyDisplayAsync()
    {
        var remaining = MinimumBusyDisplay - (DateTime.Now - _busyStartedAt);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ApplyUpdate()
    {
        FlushNow();

        // 새 버전에 문제가 있어도 되돌릴 수 있게, 갈아타기 직전 상태를 남긴다.
        if (_data.Settings.AutoBackup) BackupService.Create("update");

        if (_updates.ApplyAndRestart()) RestartRequested?.Invoke();
        else UpdateStatus = "적용하지 못했습니다 — 파일이 잠겨 있는지 확인하세요";
    }

    // ───────────────────────── 백업

    /// <summary>하루에 한 번만 남긴다. 같은 날 여러 번 켜도 하나뿐이다.</summary>
    private void BackUpIfDue()
    {
        if (!_data.Settings.AutoBackup) return;
        if (_data.Settings.LastBackupDate == _today) return;

        _store.Flush();
        if (BackupService.Create() is null) return;

        _data.Settings.LastBackupDate = _today;
        Persist();
    }

    private void RefreshBackupSummary()
    {
        var (count, newest) = BackupService.Summary();

        HasBackups = count > 0;
        BackupSummary = count == 0
            ? "백업 없음"
            : $"{count}개 · 최근 {newest:M월 d일 HH:mm}";
    }

    [RelayCommand]
    private void ToggleRestore()
    {
        IsRestoreOpen = !IsRestoreOpen;
        if (!IsRestoreOpen) return;

        IsSettingsOpen = false;
        IsHelpOpen = false;
        RebuildBackupList();
    }

    private void RebuildBackupList()
    {
        Backups.Clear();
        foreach (var entry in BackupService.List()) Backups.Add(new BackupRow(entry, this));
    }

    /// <summary>
    /// 고른 백업으로 되돌리고, 앱을 끄지 않고 그 자리에서 다시 읽어들인다.
    /// </summary>
    public void RestoreFrom(BackupRow row)
    {
        _store.Flush();

        if (!BackupService.Restore(row.Entry.Path))
        {
            Announce("되돌리지 못했습니다");
            return;
        }

        _data = _store.Load();
        _today = DayEngine.LogicalDate(DateTime.Now, _data.Settings.DayStartHour);
        DayEngine.Rollover(_data, _today);
        DayEngine.SyncToday(_data, _today);
        _store.RequestSave(_data);

        RaiseSettingsChanged();
        RebuildAll();
        RefreshBackupSummary();
        RebuildBackupList();

        IsRestoreOpen = false;
        Announce($"{row.WhenText} 상태로 되돌렸습니다");
    }

    /// <summary>설정 값들은 _data 를 직접 보므로, 데이터를 갈아끼우면 다시 읽으라고 알려야 한다.</summary>
    private void RaiseSettingsChanged()
    {
        OnPropertyChanged(nameof(AlwaysOnTop));
        OnPropertyChanged(nameof(CompactMode));
        OnPropertyChanged(nameof(RemindersEnabled));
        OnPropertyChanged(nameof(ReminderSound));
        OnPropertyChanged(nameof(NextReminderText));
        OnPropertyChanged(nameof(CarryOverIncomplete));
        OnPropertyChanged(nameof(RunAtStartup));
        OnPropertyChanged(nameof(AutoUpdate));
        OnPropertyChanged(nameof(AutoBackup));
        OnPropertyChanged(nameof(IdleOpacity));
        OnPropertyChanged(nameof(IsOpacityFull));
        OnPropertyChanged(nameof(IsOpacityHigh));
        OnPropertyChanged(nameof(IsOpacityMid));
        OnPropertyChanged(nameof(IsOpacityLow));
        OnPropertyChanged(nameof(DayStartHour));
        OnPropertyChanged(nameof(DayStartHourText));
    }

    [RelayCommand]
    private void BackUpNow()
    {
        _store.Flush();

        if (BackupService.Create("manual") is null)
        {
            Announce("백업하지 못했습니다");
            return;
        }

        _data.Settings.LastBackupDate = _today;
        Persist();
        RefreshBackupSummary();
        Announce("백업했습니다");
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(DataStore.Directory);

    [RelayCommand]
    private void OpenBackupFolder()
    {
        System.IO.Directory.CreateDirectory(BackupService.Directory);
        OpenFolder(BackupService.Directory);
    }

    private void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            Announce("폴더를 열지 못했습니다");
        }
    }

    public void Persist() => _store.RequestSave(_data);

    public void FlushNow() => _store.Flush();

    /// <summary>오늘 예정된 루틴만 체크할 수 있다. 화·목 루틴을 월요일에 체크하면 연속 기록이 망가진다.</summary>
    public bool CanToggleRoutine(RoutineRow row) => DayEngine.IsScheduled(row.Model, _today);

    public void SetRoutineDone(RoutineRow row, bool done)
    {
        if (!CanToggleRoutine(row)) return;

        if (done) DayEngine.CompleteRoutine(row.Model, _today);
        else DayEngine.UncompleteRoutine(row.Model);

        if (done) StopRinging(row.Model.Id);

        SyncRoutineRows(row.Model);
        DayEngine.SyncToday(_data, _today);
        Persist();
        RefreshProgress();
        RefreshHeatmap();
        RefreshStreakSummary();
    }

    /// <summary>같은 루틴을 가리키는 행이 '오늘' 목록과 '루틴' 목록에 따로 있으므로 둘 다 갱신한다.</summary>
    private void SyncRoutineRows(Routine model)
    {
        foreach (var row in TodayRoutines) if (ReferenceEquals(row.Model, model)) row.Sync();
        foreach (var row in AllRoutines) if (ReferenceEquals(row.Model, model)) row.Sync();
    }

    public void SetTaskDone(TaskRow row, bool done)
    {
        row.Model.Done = done;
        row.Model.CompletedDate = done ? _today : null;

        if (done) StopRinging(row.Model.Id);

        row.Sync(_today);
        DayEngine.SyncToday(_data, _today);
        Persist();

        RebuildTasks();
        RefreshProgress();
        RefreshHeatmap();
    }

    public void CyclePriority(TaskRow row)
    {
        row.Model.Priority = row.Model.Priority switch
        {
            Priority.None => Priority.High,
            Priority.High => Priority.Medium,
            Priority.Medium => Priority.Low,
            _ => Priority.None
        };

        row.Sync(_today);
        Persist();
        RebuildTasks();
    }

    public void DeleteRoutine(RoutineRow row)
    {
        _data.Routines.Remove(row.Model);
        Persist();
        RebuildAll();
    }

    public void DeleteTask(TaskRow row)
    {
        _data.Tasks.Remove(row.Model);
        Persist();
        RebuildTasks();
        RefreshProgress();
    }

    [RelayCommand]
    private void QuickAdd()
    {
        var input = QuickAddText?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        var parsed = QuickAddParser.Parse(input, _today, IsRoutineTab, TimeOnly.FromDateTime(DateTime.Now));
        var title = string.IsNullOrWhiteSpace(parsed.Title) ? input : parsed.Title;

        if (parsed.IsRoutine)
        {
            _data.Routines.Add(new Routine
            {
                Title = title,
                Days = parsed.Days,
                Order = _data.Routines.Count,

                // 시각을 굳이 적었다는 것 자체가 "이 시각이 중요하다"는 뜻이다.
                Time = parsed.DueTime,
                Remind = parsed.DueTime.HasValue
            });
            Announce($"루틴 추가 · {(parsed.Days.Count == 0 ? "매일" : string.Join("", parsed.Days.Select(ShortDay)))}");
        }
        else
        {
            // 날짜를 안 적었으면 비워 둔다. 날짜 없는 것은 오늘 화면에 놓이므로
            // 눈앞에서 사라지지 않고, 정한 적 없는 마감을 놓쳤다며 밀린 일수가 붙지도 않는다.
            // 다만 '예정' 탭에서 적은 것까지 오늘로 보내면 방금 추가한 게 사라져 보인다.
            var due = parsed.Due ?? (IsUpcomingTab ? _today.AddDays(1) : (DateOnly?)null);

            _data.Tasks.Add(new TaskItem
            {
                Title = title,
                Priority = parsed.Priority,
                Due = due,
                DueTime = parsed.DueTime,
                Remind = parsed.DueTime.HasValue,
                CreatedDate = _today,
                Order = _data.Tasks.Count
            });
            Announce($"할 일 추가 · {FormatDue(due, parsed.DueTime)}");
        }

        QuickAddText = "";
        Persist();
        RebuildAll();
    }

    [RelayCommand]
    private void ToggleCompleted() => ShowCompleted = !ShowCompleted;

    partial void OnIsCompactChanged(bool value)
    {
        foreach (var row in TodayRoutines) row.IsCompact = value;
        foreach (var row in AllRoutines) row.IsCompact = value;
        foreach (var row in TodayTasks) row.IsCompact = value;
        foreach (var row in CompletedTasks) row.IsCompact = value;
        foreach (var row in UpcomingTasks) row.IsCompact = value;
    }

    [RelayCommand]
    private void ToggleUpcomingOnToday() => ShowUpcomingOnToday = !ShowUpcomingOnToday;

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
        if (!IsSettingsOpen) return;

        OnPropertyChanged(nameof(NextReminderText));

        IsHelpOpen = false;
        IsRestoreOpen = false;
    }

    [RelayCommand]
    private void ToggleHelp()
    {
        IsHelpOpen = !IsHelpOpen;
        if (IsHelpOpen) IsSettingsOpen = false;
    }

    [RelayCommand]
    private void SelectTab(string index)
    {
        if (int.TryParse(index, out var value)) SelectedTab = value;
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        _data.Tasks.RemoveAll(t => t.Done);
        Persist();
        RebuildTasks();
        RefreshProgress();
    }

    partial void OnQuickAddTextChanged(string value) => UpdateQuickAddHint(value);

    private void UpdateQuickAddHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            QuickAddHint = "";
            return;
        }

        // 미리보기와 실제 추가가 같은 '지금'을 봐야 적힌 것과 들어간 것이 어긋나지 않는다.
        var parsed = QuickAddParser.Parse(value, _today, IsRoutineTab, TimeOnly.FromDateTime(DateTime.Now));
        var parts = new List<string>();

        if (parsed.IsRoutine)
            parts.Add(parsed.Days.Count == 0 ? "매일 반복" : $"매주 {string.Join("", parsed.Days.Select(ShortDay))}");
        else
            parts.Add(FormatDue(parsed.Due ?? (IsUpcomingTab ? _today.AddDays(1) : (DateOnly?)null), parsed.DueTime));

        // 자동으로 붙되, 붙는다는 것을 적는 순간 눈으로 보게 한다.
        // 그리고 안 울릴 것에는 붙이지 않는다 — 붙여 놓고 조용하면 그게 더 나쁘다.
        if (parsed.DueTime is { } hintTime) parts.Add(WillRing(parsed, hintTime) ? "알림" : "이미 지난 시각");

        if (parsed.Priority != Priority.None)
            parts.Add(parsed.Priority switch
            {
                Priority.High => "최우선",
                Priority.Medium => "보통",
                _ => "낮음"
            });

        QuickAddHint = string.Join(" · ", parts);
    }

    private void RebuildAll()
    {
        RebuildHeader();
        RebuildRoutines();
        RebuildTasks();
        RefreshProgress();
        RefreshHeatmap();
    }

    private void RebuildHeader()
    {
        var date = _today.ToDateTime(TimeOnly.MinValue);
        var korean = CultureInfo.GetCultureInfo("ko-KR");

        DateText = $"{_today.Year}년 {_today.Month}월 {_today.Day}일";
        WeekdayText = korean.DateTimeFormat.GetDayName(date.DayOfWeek);
    }

    private void RebuildRoutines()
    {
        var byOrder = _data.Routines.OrderBy(r => r.Order).ToList();

        AllRoutines.Clear();
        TodayRoutines.Clear();

        foreach (var routine in byOrder)
        {
            AllRoutines.Add(new RoutineRow(routine, this));
            if (DayEngine.IsScheduled(routine, _today)) TodayRoutines.Add(new RoutineRow(routine, this));
        }

        HasRoutines = AllRoutines.Count > 0;
        HasTodayRoutines = TodayRoutines.Count > 0;
        RefreshStreakSummary();
    }

    private void RebuildTasks()
    {
        TodayTasks.Clear();
        CompletedTasks.Clear();
        UpcomingTasks.Clear();

        var ordered = _data.Tasks
            .OrderByDescending(t => (int)t.Priority)
            .ThenBy(t => t.Due ?? DateOnly.MaxValue)
            .ThenBy(t => t.Order)
            .ToList();

        foreach (var task in ordered)
        {
            var row = new TaskRow(task, this, _today);

            switch (DayEngine.BucketOf(task, _today))
            {
                case TaskBucket.DoneToday: CompletedTasks.Add(row); break;
                case TaskBucket.Upcoming: UpcomingTasks.Add(row); break;
                case TaskBucket.Today: TodayTasks.Add(row); break;
                // Archived — 지난 날 완료한 것. 보관만 하고 어느 목록에도 넣지 않는다.
            }
        }

        RefreshRinging();

        HasCompleted = CompletedTasks.Count > 0;
        CompletedHeader = $"완료됨 {CompletedTasks.Count}개";
        HasTodayTasks = TodayTasks.Count > 0;
        HasUpcoming = UpcomingTasks.Count > 0;

        DateOnly? soonest = null;
        foreach (var row in UpcomingTasks)
        {
            var due = row.Model.Due;
            if (due.HasValue && (soonest is null || due.Value < soonest.Value)) soonest = due.Value;
        }

        UpcomingHeader = soonest is { } next
            ? $"예정 {UpcomingTasks.Count}개 · 가장 빠른 것 {next:M월 d일}"
            : $"예정 {UpcomingTasks.Count}개";
        UpcomingIsEmpty = UpcomingTasks.Count == 0;
        TodayIsEmpty = TodayRoutines.Count == 0 && TodayTasks.Count == 0 && CompletedTasks.Count == 0;
    }

    private void RefreshStreakSummary()
    {
        var best = _data.Routines.Count == 0 ? 0 : _data.Routines.Max(r => r.Streak);
        StreakSummary = best > 1 ? $"최고 연속 {best}일 진행 중" : "연속 기록을 쌓아보세요";
    }

    private void RefreshProgress()
    {
        var routineTotal = TodayRoutines.Count;
        var routineDone = TodayRoutines.Count(r => r.IsDone);
        var taskTotal = TodayTasks.Count + CompletedTasks.Count;
        var taskDone = CompletedTasks.Count;

        var total = routineTotal + taskTotal;
        var done = routineDone + taskDone;

        Progress = total == 0 ? 0 : (double)done / total;
        ProgressText = total == 0 ? "오늘 항목 없음" : $"{done} / {total}";
    }

    private void RefreshHeatmap()
    {
        var rates = DayEngine.RecentRates(_data, _today, HeatmapDays);

        Heatmap.Clear();
        foreach (var (date, rate) in rates) Heatmap.Add(new HeatCell(date, rate, date == _today));
    }

    private void Announce(string message)
    {
        StatusText = message;
        HasStatus = true;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>
    /// 알림 머리말. 6초 뒤에 지워지는 보통 안내와 달리 처리할 때까지 남는다.
    /// 목록을 아래로 굴려 물든 줄이 화면 밖에 있어도 여기서 알 수 있다.
    /// </summary>
    private void RefreshRingingHeader()
    {
        if (_ringing.Count == 0)
        {
            if (_ringingHeader) HasStatus = false;
            _ringingHeader = false;
            return;
        }

        _statusTimer.Stop();
        _ringingHeader = true;
        HasStatus = true;

        StatusText = _ringingTitle.Length > 0 && _ringing.Count == 1
            ? $"지금 · {_ringingTitle}"
            : $"지금 · 알림 {_ringing.Count}건";
    }

    private bool _ringingHeader;
    private string _ringingTitle = "";

    private static string ShortDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "월",
        DayOfWeek.Tuesday => "화",
        DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목",
        DayOfWeek.Friday => "금",
        DayOfWeek.Saturday => "토",
        _ => "일"
    };

    /// <summary>
    /// 지금 적고 있는 것이 실제로 울릴지.
    ///
    /// 밤 9시에 "오후 3시 회의"라고 적으면 오늘 15시가 되어 이미 지난 시각이다.
    /// 날짜를 지어내지 않는 대신, 안 울린다는 것을 적는 순간 보여 준다.
    /// 루틴은 다음 예정일에 다시 오므로 지난 시각이어도 언젠가 울린다.
    /// </summary>
    private bool WillRing(QuickAddResult parsed, TimeOnly time)
    {
        if (parsed.IsRoutine) return true;

        var due = parsed.Due ?? (IsUpcomingTab ? _today.AddDays(1) : _today);
        var at = DayEngine.AtLogicalTime(due, time, _data.Settings.DayStartHour);

        return (DateTime.Now - at).TotalMinutes <= _data.Settings.MissedGraceMinutes;
    }

    /// <summary>날짜가 없으면 어디에 놓이는지를 알려 준다. 빈칸으로 두면 어디로 갔는지 알 수 없다.</summary>
    private string FormatDue(DateOnly? due, TimeOnly? time)
    {
        if (due is not { } value) return time.HasValue ? $"오늘 목록 {time.Value:HH:mm}" : "오늘 목록";

        var delta = value.DayNumber - _today.DayNumber;
        var label = delta switch
        {
            0 => "오늘",
            1 => "내일",
            2 => "모레",
            > 0 and < 7 => $"{delta}일 뒤",
            _ => $"{value.Month}/{value.Day}"
        };

        return time.HasValue ? $"{label} {time.Value:HH:mm}" : label;
    }

    public void Dispose()
    {
        _timer.Stop();
        _statusTimer.Stop();
        _busyTimer.Stop();
        _updateTimer?.Stop();
        _store.Dispose();
    }
}
