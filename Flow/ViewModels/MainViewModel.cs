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
    /// <summary>날짜 변경 확인 주기. DateTime 비교 한 번이라 유휴 부하가 사실상 없다.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private const int HeatmapDays = 28;

    /// <summary>켜 둔 채로 며칠 지나는 경우를 위한 재확인 주기. 시작할 때는 이와 무관하게 한 번 본다.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(20);

    /// <summary>시작하자마자 네트워크를 건드리지 않도록 잠깐 미룬다.</summary>
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(20);

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
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>확인·내려받기가 진행 중일 때만 켜진다. 위쪽에 진행 줄을 띄운다.</summary>
    [ObservableProperty] private bool _isUpdateBusy;
    [ObservableProperty] private bool _isUpdateDownloading;
    [ObservableProperty] private string _updateBusyText = "";
    [ObservableProperty] private string _updatePercentText = "";
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _backupSummary = "";
    [ObservableProperty] private bool _showCompleted;
    [ObservableProperty] private string _completedHeader = "";
    [ObservableProperty] private bool _hasCompleted;
    [ObservableProperty] private bool _todayIsEmpty;
    [ObservableProperty] private string _streakSummary = "";
    [ObservableProperty] private bool _hasTodayRoutines;
    [ObservableProperty] private bool _hasTodayTasks;
    [ObservableProperty] private bool _hasUpcoming;
    [ObservableProperty] private bool _hasRoutines;
    [ObservableProperty] private bool _upcomingIsEmpty;

    public MainViewModel()
    {
        _store = new DataStore();
        _data = _store.Load();
        _today = DayEngine.LogicalDate(DateTime.Now, _data.Settings.DayStartHour);

        var result = DayEngine.Rollover(_data, _today);
        DayEngine.SyncToday(_data, _today);
        _store.RequestSave(_data);

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => CheckRollover();
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

    /// <summary>30초마다, 그리고 창이 활성화될 때마다 날짜가 넘어갔는지 확인한다.</summary>
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

        BackupSummary = count == 0
            ? "백업 없음"
            : $"{count}개 · 최근 {newest:M월 d일 HH:mm}";
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

        var parsed = QuickAddParser.Parse(input, _today, IsRoutineTab);
        var title = string.IsNullOrWhiteSpace(parsed.Title) ? input : parsed.Title;

        if (parsed.IsRoutine)
        {
            _data.Routines.Add(new Routine
            {
                Title = title,
                Days = parsed.Days,
                Order = _data.Routines.Count
            });
            Announce($"루틴 추가 · {(parsed.Days.Count == 0 ? "매일" : string.Join("", parsed.Days.Select(ShortDay)))}");
        }
        else
        {
            // 날짜를 안 적었으면 보고 있는 탭에 맞춘다.
            // '예정' 탭에서 추가한 항목이 오늘로 들어가 눈앞에서 사라지면 안 된다.
            var due = parsed.Due ?? (IsUpcomingTab ? _today.AddDays(1) : _today);

            _data.Tasks.Add(new TaskItem
            {
                Title = title,
                Priority = parsed.Priority,
                Due = due,
                DueTime = parsed.DueTime,
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

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
        if (IsSettingsOpen) IsHelpOpen = false;
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

        var parsed = QuickAddParser.Parse(value, _today, IsRoutineTab);
        var parts = new List<string>();

        if (parsed.IsRoutine)
            parts.Add(parsed.Days.Count == 0 ? "매일 반복" : $"매주 {string.Join("", parsed.Days.Select(ShortDay))}");
        else
            parts.Add(FormatDue(parsed.Due ?? (IsUpcomingTab ? _today.AddDays(1) : _today), parsed.DueTime));

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

            if (task.Done)
            {
                // 지난 날 완료한 항목은 보관만 하고 오늘 화면에는 넣지 않는다.
                if (task.CompletedDate == _today) CompletedTasks.Add(row);
            }
            else if (!task.Due.HasValue || task.Due.Value > _today) UpcomingTasks.Add(row);
            else TodayTasks.Add(row);
        }

        HasCompleted = CompletedTasks.Count > 0;
        CompletedHeader = $"완료됨 {CompletedTasks.Count}개";
        HasTodayTasks = TodayTasks.Count > 0;
        HasUpcoming = UpcomingTasks.Count > 0;
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

    private string FormatDue(DateOnly due, TimeOnly? time)
    {
        var delta = due.DayNumber - _today.DayNumber;
        var label = delta switch
        {
            0 => "오늘",
            1 => "내일",
            2 => "모레",
            > 0 and < 7 => $"{delta}일 뒤",
            _ => $"{due.Month}/{due.Day}"
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
