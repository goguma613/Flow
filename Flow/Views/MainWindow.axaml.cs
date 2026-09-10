using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Flow.Services;
using Flow.ViewModels;

namespace Flow.Views;

public partial class MainWindow : Window
{
    /// <summary>창을 끌거나 크기를 바꾸는 동안 매 픽셀마다 저장하지 않도록 잠깐 모아두는 시간.</summary>
    private static readonly TimeSpan PlacementSaveDelay = TimeSpan.FromMilliseconds(500);

    private const double DefaultWidth = 340;

    /// <summary>이보다 낮게 줄이면 컴팩트로 넘어간다. 머리말을 다 그릴 자리가 없어지기 때문이다.</summary>
    private const double CompactHeight = 400;

    /// <summary>스쳐 지나가는 마우스에는 반응하지 않도록 이만큼 머물러야 펼친다.</summary>
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>아래쪽 묶음을 부르는 자리. 창 맨 아래 이만큼 안으로 들어오면 올라온다.</summary>
    private const double BottomTriggerZone = 26;

    /// <summary>
    /// 곧바로 내리면 탭을 겨누다 살짝 위로 벗어났을 때 사라져 버린다.
    /// 잠깐 기다렸다가 내린다.
    /// </summary>
    private static readonly TimeSpan BottomHideDelay = TimeSpan.FromMilliseconds(220);

    /// <summary>알림이 울렸을 때 창이 밝아져 있는 시간. 딱 한 번만, 반복하지 않는다.</summary>
    private static readonly TimeSpan PulseHold = TimeSpan.FromSeconds(1.4);

    private readonly DispatcherTimer _placementSaveTimer;
    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _revealTimer;
    private readonly DispatcherTimer _bottomHideTimer;
    private Control? _headerArea;
    private Control? _tabStrip;
    private TextBox? _quickAddBox;
    private bool _placementRestored;
    private bool _pointerInside;
    private Point _pointer;

    public MainWindow()
    {
        InitializeComponent();

        _headerArea = this.FindControl<Control>("HeaderArea");
        _tabStrip = this.FindControl<Control>("TabStrip");
        _quickAddBox = this.FindControl<TextBox>("QuickAddBox");

        if (_headerArea is not null) _headerArea.PointerPressed += OnHeaderPressed;
        if (_quickAddBox is not null)
        {
            _quickAddBox.KeyDown += OnQuickAddKeyDown;

            // 적는 도중에 입력칸이 사라지면 안 된다. 커서가 들어오고 나갈 때마다 다시 판단한다.
            _quickAddBox.GotFocus += (_, _) => UpdateReveal();
            _quickAddBox.LostFocus += (_, _) => UpdateReveal();
        }

        var pin = this.FindControl<Button>("PinButton");
        if (pin is not null) pin.Click += (_, _) => Toggle(vm => vm.AlwaysOnTop = !vm.AlwaysOnTop);

        var minimize = this.FindControl<Button>("MinimizeButton");
        if (minimize is not null) minimize.Click += (_, _) => HideToTray();

        var close = this.FindControl<Button>("CloseButton");
        if (close is not null) close.Click += (_, _) => RequestExit();

        var changeFolder = this.FindControl<Button>("ChangeFolderButton");
        if (changeFolder is not null) changeFolder.Click += async (_, _) => await ChangeDataFolder();

        var resetSize = this.FindControl<Button>("ResetSizeButton");
        if (resetSize is not null) resetSize.Click += (_, _) => ResetToAutoSize();

        HookResizeGrip("ResizeRight", WindowEdge.East);
        HookResizeGrip("ResizeBottom", WindowEdge.South);
        HookResizeGrip("ResizeCorner", WindowEdge.SouthEast);

        var corner = this.FindControl<Border>("ResizeCorner");
        if (corner is not null) corner.DoubleTapped += (_, _) => ResetToAutoSize();

        _placementSaveTimer = new DispatcherTimer { Interval = PlacementSaveDelay };
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
            ViewModel?.FlushNow();
        };

        PositionChanged += (_, _) => SchedulePlacementSave();
        Resized += (_, _) =>
        {
            SchedulePlacementSave();
            UpdateCompactMode();
        };

        _revealTimer = new DispatcherTimer { Interval = RevealDelay };
        _revealTimer.Tick += (_, _) =>
        {
            _revealTimer.Stop();
            UpdateReveal();
        };

        _pulseTimer = new DispatcherTimer { Interval = PulseHold };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseTimer.Stop();
            if (!_pointerInside) ApplyIdleOpacity();
        };

        _bottomHideTimer = new DispatcherTimer { Interval = BottomHideDelay };
        _bottomHideTimer.Tick += (_, _) =>
        {
            _bottomHideTimer.Stop();
            if (ViewModel is { } vm) vm.CompactBottomRevealed = false;
        };

        PointerEntered += (_, _) =>
        {
            Opacity = 1.0;
            _pointerInside = true;
            _revealTimer.Start();
        };
        PointerMoved += (_, e) =>
        {
            _pointer = e.GetPosition(this);
            UpdateBottom();
        };
        PointerExited += (_, _) =>
        {
            _pointerInside = false;
            _revealTimer.Stop();
            ApplyIdleOpacity();
            UpdateReveal();
        };
        Activated += (_, _) => ViewModel?.OnActivated();
        Deactivated += (_, _) => ApplyIdleOpacity();
        // 단축키는 입력칸에 커서가 있어도 먹어야 한다. TextBox 가 삼키기 전에 먼저 잡는다.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        KeyDown += OnWindowKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (ViewModel is not { } vm) return;

        // 새 실행 파일로 갈아탄 뒤에는 이 프로세스가 물러나야 한다.
        vm.RestartRequested += () => Dispatcher.UIThread.Post(ShutdownForUpdate);
        vm.ReminderFired += OnReminderFired;
        vm.FolderMissing += OnFolderMissing;
        // 알림을 눌렀을 때 돌아올 자리를 먼저 걸어 둔다. 첫 알림보다 앞서야 한다.
        Notifier.HookActivation(() => Dispatcher.UIThread.Post(SummonToFront));
        vm.CompactModeChanged += OnCompactModeChanged;

        UpdateCompactMode();
    }

    /// <summary>
    /// 알림이 울렸다. 창이 보이면 한 번 밝히는 것이 전부다 —
    /// 앞으로 끌어오지도, 포커스를 뺏지도, 트레이에서 나오지도 않는다.
    ///
    /// 창이 숨어 있을 때만 Windows 알림을 쓴다. 그때는 물든 줄을 아무도 못 보기 때문이다.
    /// 못 띄워도 줄은 물든 채 남아 있어서, 창을 다시 열면 거기 있다.
    /// </summary>
    private void OnReminderFired(string title)
    {
        if (IsWindowShowing())
        {
            Opacity = 1.0;
            _pulseTimer.Stop();
            _pulseTimer.Start();
            return;
        }

        if (!Notifier.CanInterrupt()) return;

        Notifier.Show(title, "Flow · 지금 할 일", ViewModel?.WantsReminderSound ?? true);
    }

    /// <summary>지금 화면에서 볼 수 있는 상태인지. 트레이로 숨겼거나 최소화면 아니다.</summary>
    private bool IsWindowShowing() => IsVisible && WindowState != WindowState.Minimized;

    /// <summary>
    /// 저장 폴더를 바꾼다. 구글 드라이브처럼 동기화되는 폴더를 가리키면
    /// 그 폴더를 함께 보는 다른 PC와 같은 내용을 쓰게 된다.
    ///
    /// 바꾼 뒤에는 앱을 다시 켠다. 저장 위치는 앱이 뜰 때 한 번 정해지는 값이라,
    /// 돌아가는 중에 갈아끼우면 어중간한 상태가 생긴다.
    /// </summary>
    private async Task ChangeDataFolder()
    {
        if (ViewModel is not { } vm) return;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "데이터를 둘 폴더를 고르세요",
            AllowMultiple = false
        });

        if (picked.Count == 0) return;

        var target = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(target)) return;

        if (string.Equals(target.TrimEnd('\\'), DataStore.Directory.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            vm.Announce("이미 그 폴더를 쓰고 있습니다");
            return;
        }

        // 옮기기 전에 지금 것을 확실히 디스크에 내려놓는다.
        vm.FlushNow();

        var result = DataLocation.Prepare(target);
        if (result == MoveResult.Failed)
        {
            vm.Announce("그 폴더에는 쓸 수 없습니다");
            return;
        }

        if (!DataLocation.Remember(target))
        {
            vm.Announce("저장 위치를 기억하지 못했습니다");
            return;
        }

        RestartToApplyLocation();
    }

    /// <summary>
    /// 새 저장 위치로 다시 켠다.
    /// 자물쇠를 먼저 놓아야 새로 뜨는 쪽이 자기가 주인이 될 수 있다.
    /// </summary>
    private void RestartToApplyLocation()
    {
        ViewModel?.FlushNow();
        SaveWindowPlacement();

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        SingleInstance.Release();

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 못 켰으면 그냥 종료된다. 다시 실행하면 새 위치로 뜬다.
        }

        ShutdownForUpdate();
    }

    /// <summary>
    /// 저장 폴더가 한참째 안 온다. 창이 보이면 이미 띠로 말하고 있으니 그것으로 충분하고,
    /// 트레이에 숨어 있을 때만 Windows 알림으로 알린다. 이때가 놓치면 위험한 경우다.
    /// </summary>
    private void OnFolderMissing(string folder)
    {
        if (IsWindowShowing()) return;
        if (!Notifier.CanInterrupt()) return;

        Notifier.Show("저장 폴더를 찾지 못했습니다",
            "클라우드가 켜질 때까지 아무것도 저장하지 않습니다", ViewModel?.WantsReminderSound ?? true);
    }

    private void ShutdownForUpdate()    {
        if (Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>트레이나 단축키로 다시 불러낼 때 쓴다.</summary>
    public void SummonToFront()
    {
        if (!IsVisible) Show();

        WindowState = WindowState.Normal;
        Activate();
        Opacity = 1.0;

        Dispatcher.UIThread.Post(() =>
        {
            _quickAddBox?.Focus();
            ViewModel?.CheckRollover();
        }, DispatcherPriority.Background);
    }

    public void HideToTray()
    {
        SaveWindowPlacement();
        ViewModel?.FlushNow();
        Hide();
        TrimWorkingSet();
    }

    public void RequestExit()
    {
        SaveWindowPlacement();
        ViewModel?.FlushNow();

        if (Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestoreWindowPlacement();
        UpdateCompactMode();
        ApplyIdleOpacity();
    }

    /// <summary>
    /// 설정으로 켰거나, 사용자가 일부러 작게 줄여 머리말을 다 그릴 자리가 없을 때 접는다.
    /// 내용에 맞춰 높이가 정해질 때는 접지 않는다. 그때는 이미 딱 맞는 크기다.
    /// </summary>
    private void UpdateCompactMode()
    {
        if (ViewModel is not { } vm) return;

        vm.IsCompact = vm.CompactMode || (vm.Settings.WindowSizedByUser && Height < CompactHeight);
        UpdateReveal();
    }

    private void OnCompactModeChanged()
    {
        if (ViewModel is not { } vm) return;

        if (vm.CompactMode)
        {
            // 컴팩트는 고정된 작은 창을 전제한다. 내용에 맞춰 높이가 따라다니면
            // 겹쳐 뜬 입력칸이 창을 밀어내며 들썩인다.
            SizeToContent = SizeToContent.Manual;
            vm.Settings.WindowSizedByUser = true;
        }
        else if (Height < CompactHeight)
        {
            // 껐는데 창이 너무 낮으면 자동 규칙이 곧바로 다시 접어 버려서
            // 아무 일도 안 일어난 것처럼 보인다. 먼저 자리를 만들어 준다.
            Height = CompactHeight;
        }

        UpdateCompactMode();
    }

    /// <summary>컴팩트에서 머리말 버튼을 지금 보여줄지 다시 판단한다.</summary>
    private void UpdateReveal()
    {
        if (ViewModel is not { } vm) return;

        vm.CompactRevealed = _pointerInside || _quickAddBox?.IsFocused == true;
        UpdateBottom();
    }

    /// <summary>
    /// 탭과 입력칸은 목록을 덮는다. 목록 위에서 떠 버리면 누르려던 항목이 가려져
    /// 체크를 할 수 없다. 그래서 창 맨 아래에 다가갔을 때만 올린다.
    /// </summary>
    private void UpdateBottom()
    {
        if (ViewModel is not { } vm) return;

        if (!vm.IsCompact)
        {
            _bottomHideTimer.Stop();
            vm.CompactBottomRevealed = false;
            return;
        }

        // 적는 중에는 마우스가 어디에 있든 내리지 않는다.
        var wanted = _quickAddBox?.IsFocused == true
                     || (_pointerInside && _pointer.Y >= Bounds.Height - CurrentBottomZone(vm));

        if (wanted)
        {
            _bottomHideTimer.Stop();
            vm.CompactBottomRevealed = true;
        }
        else if (vm.CompactBottomRevealed && !_bottomHideTimer.IsEnabled)
        {
            _bottomHideTimer.Start();
        }
    }

    /// <summary>
    /// 내려가 있을 때는 얇은 자리만으로 부른다.
    /// 올라와 있을 때는 묶음 위를 지나도 유지되도록 묶음 전체를 자리로 친다.
    /// </summary>
    private double CurrentBottomZone(MainViewModel vm)
    {
        if (!vm.CompactBottomRevealed || _tabStrip is null) return BottomTriggerZone;

        var top = _tabStrip.TranslatePoint(new Point(0, 0), this);
        if (top is not { } point) return BottomTriggerZone;

        return Math.Max(BottomTriggerZone, Bounds.Height - point.Y + 10);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _placementSaveTimer.Stop();
        SaveWindowPlacement();
        ViewModel?.FlushNow();
        base.OnClosing(e);
    }

    // ───────────────────────── 크기 조절

    private void HookResizeGrip(string name, WindowEdge edge)
    {
        var grip = this.FindControl<Border>(name);
        if (grip is null) return;

        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            // 내용에 맞춰 높이를 정하는 동안에는 끌어도 곧바로 되돌아간다. 먼저 수동으로 바꾼다.
            SizeToContent = SizeToContent.Manual;
            if (ViewModel is { } vm) vm.Settings.WindowSizedByUser = true;

            UpdateCompactMode();
            BeginResizeDrag(edge, e);
        };
    }

    /// <summary>다시 내용에 맞는 높이로. 모서리를 두 번 클릭하거나 설정에서 실행한다.</summary>
    private void ResetToAutoSize()
    {
        Width = DefaultWidth;
        SizeToContent = SizeToContent.Height;

        if (ViewModel is { } vm)
        {
            vm.IsCompact = false;
            vm.Settings.WindowSizedByUser = false;
            vm.Settings.WindowWidth = DefaultWidth;
            vm.PersistDevice();
        }
    }

    // ───────────────────────── 위치·크기 저장

    private void SchedulePlacementSave()
    {
        if (!_placementRestored) return;

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void RestoreWindowPlacement()
    {
        if (_placementRestored || ViewModel is not { } vm) return;

        var settings = vm.Settings;

        if (settings.WindowSizedByUser)
        {
            SizeToContent = SizeToContent.Manual;
            Width = Math.Clamp(settings.WindowWidth, MinWidth, MaxWidth);
            if (settings.WindowHeight >= MinHeight) Height = Math.Min(settings.WindowHeight, MaxHeight);
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            Width = DefaultWidth;
        }

        var primary = Screens.Primary?.WorkingArea;

        if (settings.WindowLeft is not { } savedLeft || settings.WindowTop is not { } savedTop)
        {
            if (primary is { } area)
            {
                Position = new PixelPoint(
                    area.X + area.Width - (int)(Width * DesktopScaling) - 32,
                    area.Y + 48);
            }

            _placementRestored = true;
            return;
        }

        var target = new PixelPoint((int)savedLeft, (int)savedTop);

        // 어느 모니터에도 걸치지 않을 때만 되돌린다.
        // (보조 모니터에 둔 창을 주 모니터로 끌어오면 안 된다)
        if (Screens.ScreenFromPoint(target) is null && primary is { } work)
        {
            target = new PixelPoint(work.X + work.Width - (int)(Width * DesktopScaling) - 32, work.Y + 48);
        }

        Position = target;
        _placementRestored = true;
    }

    private void SaveWindowPlacement()
    {
        if (ViewModel is not { } vm || !IsVisible) return;

        vm.Settings.WindowLeft = Position.X;
        vm.Settings.WindowTop = Position.Y;

        if (vm.Settings.WindowSizedByUser)
        {
            vm.Settings.WindowWidth = Width;
            vm.Settings.WindowHeight = Height;
        }

        // 창 위치는 이 PC 것이다. data.json 을 쓰면 클라우드가 매번 파일을 올린다.
        vm.PersistDevice();
    }

    // ───────────────────────── 이름 바꾸기

    private void OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box) return;

        // 이 이벤트는 행이 만들어질 때 한 번만 발생한다. 그 시점의 편집 상자는 아직 숨어 있고,
        // 숨은 컨트롤에는 포커스가 가지 않는다. 그래서 '보이게 되는 순간'을 따로 지켜본다.
        box.GetObservable(IsVisibleProperty).Subscribe(new AnonymousObserver<bool>(visible =>
        {
            if (!visible) return;

            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Background);
        }));
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: RowBase row }) return;

        switch (e.Key)
        {
            case Key.Enter:
                row.CommitRename();
                e.Handled = true;
                break;
            case Key.Escape:
                row.CancelRename();
                e.Handled = true;
                break;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: RowBase row }) row.CommitRename();
    }

    // ───────────────────────── 그 외

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 헤더 안의 버튼(고정·설정·숨기기·종료)을 누른 것이라면 창을 끌지 않는다.
        for (var node = e.Source as Visual; node is not null && node != _headerArea; node = node.GetVisualParent())
        {
            if (node is Button) return;
        }

        BeginMoveDrag(e);
    }

    private void OnQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        ViewModel?.QuickAddCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Ctrl 조합 단축키. 포커스가 어디에 있든 창이 먼저 본다.</summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.ToggleCompactCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // 컴팩트에서는 탭이 숨어 있으니 손으로도 넘길 수 있어야 한다.
        var tab = e.Key switch
        {
            Key.D1 => "0",
            Key.D2 => "1",
            Key.D3 => "2",
            _ => null
        };

        if (tab is null) return;

        vm.SelectTabCommand.Execute(tab);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key != Key.Escape) return;

        if (vm.IsRestoreOpen)
        {
            vm.IsRestoreOpen = false;
            e.Handled = true;
        }
        else if (vm.IsHelpOpen)
        {
            vm.IsHelpOpen = false;
            e.Handled = true;
        }
        else if (vm.IsSettingsOpen)
        {
            vm.IsSettingsOpen = false;
            e.Handled = true;
        }
    }

    private void Toggle(Action<MainViewModel> action)
    {
        if (ViewModel is { } vm) action(vm);
    }

    private void ApplyIdleOpacity()
    {
        if (ViewModel is not { } vm) return;
        if (IsPointerOver || IsActive) return;

        Opacity = vm.IdleOpacity;
    }

    /// <summary>
    /// 트레이에 숨은 동안 쓰지 않는 물리 메모리를 OS에 돌려준다.
    /// 다시 필요해지면 알아서 되가져오므로 상주 위젯에서 흔히 쓰는 방식이다.
    /// </summary>
    private static void TrimWorkingSet()
    {
        try
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch (Exception)
        {
            // 실패해도 동작에는 영향이 없다.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, int min, int max);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
