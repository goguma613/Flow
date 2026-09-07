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
using Flow.ViewModels;

namespace Flow.Views;

public partial class MainWindow : Window
{
    /// <summary>창을 끌거나 크기를 바꾸는 동안 매 픽셀마다 저장하지 않도록 잠깐 모아두는 시간.</summary>
    private static readonly TimeSpan PlacementSaveDelay = TimeSpan.FromMilliseconds(500);

    private const double DefaultWidth = 340;

    /// <summary>이보다 낮게 줄이면 머리말을 접는다. 내용을 볼 자리가 없어지기 때문이다.</summary>
    private const double CompactHeight = 400;

    private readonly DispatcherTimer _placementSaveTimer;
    private Control? _headerArea;
    private TextBox? _quickAddBox;
    private bool _placementRestored;

    public MainWindow()
    {
        InitializeComponent();

        _headerArea = this.FindControl<Control>("HeaderArea");
        _quickAddBox = this.FindControl<TextBox>("QuickAddBox");

        if (_headerArea is not null) _headerArea.PointerPressed += OnHeaderPressed;
        if (_quickAddBox is not null) _quickAddBox.KeyDown += OnQuickAddKeyDown;

        var pin = this.FindControl<Button>("PinButton");
        if (pin is not null) pin.Click += (_, _) => Toggle(vm => vm.AlwaysOnTop = !vm.AlwaysOnTop);

        var minimize = this.FindControl<Button>("MinimizeButton");
        if (minimize is not null) minimize.Click += (_, _) => HideToTray();

        var close = this.FindControl<Button>("CloseButton");
        if (close is not null) close.Click += (_, _) => RequestExit();

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

        PointerEntered += (_, _) => Opacity = 1.0;
        PointerExited += (_, _) => ApplyIdleOpacity();
        Activated += (_, _) => ViewModel?.CheckRollover();
        Deactivated += (_, _) => ApplyIdleOpacity();
        KeyDown += OnWindowKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // 새 실행 파일로 갈아탄 뒤에는 이 프로세스가 물러나야 한다.
        if (ViewModel is { } vm) vm.RestartRequested += () => Dispatcher.UIThread.Post(ShutdownForUpdate);
    }

    private void ShutdownForUpdate()
    {
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
    /// 내용에 맞춰 높이가 정해질 때는 접지 않는다. 그때는 이미 딱 맞는 크기다.
    /// 사용자가 일부러 작게 줄였을 때만 머리말을 접어 목록 자리를 만든다.
    /// </summary>
    private void UpdateCompactMode()
    {
        if (ViewModel is not { } vm) return;

        vm.IsCompact = vm.Settings.WindowSizedByUser && Height < CompactHeight;
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
            vm.Persist();
            vm.FlushNow();
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

        vm.Persist();
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

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (ViewModel is not { } vm) return;

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
