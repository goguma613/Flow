using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>
/// 메모 창. 위젯이 처음 메모를 열 때 만들고, 닫으면 통째로 버린다(다시 열면 새로 만든다).
/// 목록과 훑기는 위젯 것을 같이 쓰므로, 닫을 때 위젯 이벤트에서 반드시 떼어 내야 창이 풀려난다.
/// </summary>
public partial class MemoWindow : Window
{
    /// <summary>끌거나 크기를 바꾸는 동안 매 픽셀 저장하지 않도록 모아 두는 시간. 위젯 창과 같다.</summary>
    private static readonly TimeSpan PlacementSaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly MainViewModel _main;
    private readonly MemoEditorViewModel _vm;
    private readonly DispatcherTimer _placementSaveTimer;
    private readonly TextBox? _writer;
    private bool _placementRestored;

    /// <summary>위젯이 끝나며 닫는 중. 이때만은 저장하지 못한 글이 있어도 닫는다(한 번 저장은 해 본다).</summary>
    private bool _exiting;

    // 디자이너용. 실제로는 아래 생성자를 쓴다.
    public MemoWindow() : this(null!) { }

    public MemoWindow(MainViewModel main)
    {
        InitializeComponent();

        _main = main;
        if (main is null)
        {
            _vm = null!;
            _placementSaveTimer = new DispatcherTimer();
            return;
        }

        _vm = new MemoEditorViewModel(main);
        DataContext = _vm;

        // 보이기 전에 크기를 맞춰야 가운데 놓기가 그 크기로 계산된다.
        var settings = main.Settings;
        if (settings.MemoWindowWidth >= MinWidth) Width = settings.MemoWindowWidth;
        if (settings.MemoWindowHeight >= MinHeight) Height = settings.MemoWindowHeight;

        _writer = this.FindControl<TextBox>("Writer");

        var readArea = this.FindControl<Panel>("ReadArea");
        // 체크 칸(버튼)은 눌림을 스스로 처리하므로 여기까지 오지 않는다. 나머지 글 자리는 쓰기 모드로.
        if (readArea is not null) readArea.PointerPressed += OnReadAreaPressed;

        _placementSaveTimer = new DispatcherTimer { Interval = PlacementSaveDelay };
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        PositionChanged += (_, _) => SchedulePlacementSave();
        Resized += (_, _) => SchedulePlacementSave();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        main.MemosChanged += OnMemosChanged;
        main.MemoFlushRequested += OnFlushRequested;

        // 단축키는 입력칸이 삼키기 전에 먼저 본다.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
    }

    public void OpenMemo(Guid id) => _vm?.Open(id);

    /// <summary>위젯이 끝날 때. 저장을 시도하고 막지 않고 닫는다.</summary>
    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    private void OnMemosChanged() => _vm.OnMemosChanged();

    private void OnFlushRequested()
    {
        _vm.TakeWriterText(_writer?.Text);
        _vm.PrepareForRestore();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MemoEditorViewModel.IsEditing) || !_vm.IsEditing) return;

        // 쓰기 칸이 보이게 된 다음에야 포커스가 간다.
        Dispatcher.UIThread.Post(() =>
        {
            if (_writer is null || !_writer.IsVisible) return;
            _writer.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnReadAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_vm.IsTrashView) return;
        _vm.EnterEditing();
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_vm.IsEditing) LeaveEditingSoon();
            else _vm.EnterEditing();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _vm.IsEditing)
        {
            LeaveEditingSoon();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 한글을 조합하던 중이면 그 글자는 이 키 다음에야 쓰기 칸에 들어온다.
    /// 바로 읽기 모드로 바꾸면 그 글자를 잃으므로, 조합이 끝난 뒤로 미루고 쓰기 칸의 글을 직접 받아 둔다.
    /// </summary>
    private void LeaveEditingSoon()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _vm.TakeWriterText(_writer?.Text);
            _vm.LeaveEditing();
        }, DispatcherPriority.Background);
    }

    // ───────────────────────── 닫기

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _placementSaveTimer.Stop();
        SavePlacement();

        if (_vm is not null)
        {
            _vm.TakeWriterText(_writer?.Text);
            _vm.Flush();

            // 저장 못 한 글은 버리지 않는다. 사람이 닫을 때는 창을 남기고 띠로 알린다.
            // 앱이 끝날 때(종료·업데이트·Windows 끄기)만은 한 번 저장해 보고 그대로 닫는다.
            var exiting = _exiting
                          || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
            if (_vm.HasUnsavedText && !exiting)
            {
                e.Cancel = true;
                _vm.RefuseClose();
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is null) return;

        _placementSaveTimer.Stop();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _main.MemosChanged -= OnMemosChanged;
        _main.MemoFlushRequested -= OnFlushRequested;
        _vm.Dispose();
    }

    // ───────────────────────── 위치·크기

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestorePlacement();
    }

    private void RestorePlacement()
    {
        if (_placementRestored || _main is null) return;

        var settings = _main.Settings;
        if (settings.MemoWindowLeft is { } left && settings.MemoWindowTop is { } top)
        {
            var target = new PixelPoint((int)left, (int)top);

            // 어느 모니터에도 걸치지 않으면(모니터를 뺐을 때) 처음처럼 가운데에 둔다.
            if (Screens.ScreenFromPoint(target) is not null) Position = target;
            else CenterOnScreen();
        }

        _placementRestored = true;
    }

    private void CenterOnScreen()
    {
        var area = Screens.Primary?.WorkingArea;
        if (area is not { } work) return;

        var width = (int)(Width * DesktopScaling);
        var height = (int)(Height * DesktopScaling);
        Position = new PixelPoint(work.X + Math.Max(0, (work.Width - width) / 2),
            work.Y + Math.Max(0, (work.Height - height) / 2));
    }

    private void SchedulePlacementSave()
    {
        if (!_placementRestored) return;

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacement()
    {
        if (_main is null || !_placementRestored || !IsVisible || WindowState != WindowState.Normal) return;

        var settings = _main.Settings;
        settings.MemoWindowLeft = Position.X;
        settings.MemoWindowTop = Position.Y;
        settings.MemoWindowWidth = Width;
        settings.MemoWindowHeight = Height;

        // 창 위치는 이 PC 것이다. data.json 이 아니라 기기별 파일에만 쓴다.
        _main.PersistDevice();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
