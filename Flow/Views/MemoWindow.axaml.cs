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

    /// <summary>저장하지 못한 글이 있는데 닫으려 하면 한 번은 막고 띠로 알린다. 두 번째는 닫는다.</summary>
    private bool _closeWarned;

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
        _closeWarned = true;
        Close();
    }

    private void OnMemosChanged() => _vm.OnMemosChanged();

    private void OnFlushRequested() => _vm.PrepareForRestore();

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
            _vm.ToggleEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _vm.IsEditing)
        {
            _vm.LeaveEditing();
            e.Handled = true;
        }
    }

    // ───────────────────────── 닫기

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _placementSaveTimer.Stop();
        SavePlacement();
        _vm?.Flush();

        // 사람이 닫는데 저장을 못 했으면 한 번은 멈춰 세운다. 띠가 이미 '저장하지 못했습니다'를 보이고 있다.
        if (_vm is { HasUnsavedText: true } && !_closeWarned
            && e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            _closeWarned = true;
            e.Cancel = true;
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
