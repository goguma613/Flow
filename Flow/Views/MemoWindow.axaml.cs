using System;
using System.ComponentModel;
using Avalonia;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Flow.Models;
using Flow.Services;
using Flow.ViewModels;

namespace Flow.Views;

/// <summary>
/// 메모 창. 위젯이 처음 메모를 열 때 만들고, 닫으면 통째로 버린다(다시 열면 새로 만든다).
/// 목록과 훑기는 위젯 것을 같이 쓰므로, 닫을 때 위젯 이벤트에서 반드시 떼어 내야 창이 풀려난다.
/// 위젯처럼 테두리 없는 둥근 판이라, 옮기기·크기 조절·최소화·최대화·닫기를 직접 단다.
/// </summary>
public partial class MemoWindow : Window
{
    /// <summary>끌거나 크기를 바꾸는 동안 매 픽셀 저장하지 않도록 모아 두는 시간. 위젯 창과 같다.</summary>
    private static readonly TimeSpan PlacementSaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly MainViewModel _main;
    private readonly MemoEditorViewModel _vm;
    private readonly DispatcherTimer _placementSaveTimer;
    private readonly TextBox? _writer;
    private readonly TextBox? _tagBox;
    private readonly Border? _frame;
    private readonly Control? _titleBar;
    private readonly Control? _grips;
    private readonly Button? _maximizeButton;
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
        _tagBox = this.FindControl<TextBox>("TagBox");
        _frame = this.FindControl<Border>("Frame");
        _titleBar = this.FindControl<Control>("TitleBar");
        _grips = this.FindControl<Control>("Grips");
        _maximizeButton = this.FindControl<Button>("MaximizeButton");

        var readArea = this.FindControl<Panel>("ReadArea");
        // 체크 칸(버튼)은 눌림을 스스로 처리하므로 여기까지 오지 않는다. 나머지 글 자리는 쓰기 모드로.
        if (readArea is not null) readArea.PointerPressed += OnReadAreaPressed;
        var readTitle = this.FindControl<Control>("ReadTitleText");
        if (readTitle is not null) readTitle.PointerPressed += OnReadAreaPressed;

        if (_tagBox is not null) _tagBox.KeyDown += OnTagBoxKeyDown;

        // 사용법 판 바깥(어둡게 덮은 곳)을 누르면 닫는다. 판 안을 누른 것은 여기까지 오지 않는다.
        var helpScrim = this.FindControl<Border>("HelpScrim");
        if (helpScrim is not null)
            helpScrim.PointerPressed += (_, e) =>
            {
                _vm.CloseHelpCommand.Execute(null);
                e.Handled = true;
            };

        HookChrome();

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
        // 사용법 판이 떠 있으면 Esc 는 판만 닫는다(쓰기 모드는 그대로).
        if (e.Key == Key.Escape && _vm.IsHelpOpen)
        {
            _vm.IsHelpOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_vm.IsEditing) LeaveEditingSoon();
            else _vm.EnterEditing();
            e.Handled = true;
            return;
        }

        // 태그 칸의 Esc 는 적던 태그를 지운다(아래 OnTagBoxKeyDown). 쓰기 모드를 빠지지 않는다.
        if (e.Key == Key.Escape && _vm.IsEditing && !IsInTagBox(e.Source))
        {
            LeaveEditingSoon();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) ContinueListOnEnter(e);
    }

    // ───────────────────────── 서식 단추 · Enter 이어 쓰기

    /// <summary>
    /// 서식 단추. 쓰는 중이면 커서 줄(고른 줄들)의 기호를 켜고 끈다.
    /// 읽는 중이면 쓰기 모드로 바꾸고 본문 끝에 그 기호로 시작하는 새 줄을 붙인다.
    /// 단추는 포커스를 가져가지 않아(Focusable=False) 쓰기 칸의 선택이 그대로 남는다.
    /// </summary>
    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || !Enum.TryParse<MemoLineKind>(name, out var kind)) return;
        if (_vm is null || _writer is null || _vm.IsTrashView || !_vm.HasMemo) return;

        if (!_vm.IsEditing)
        {
            var appended = MemoEditing.Append(_vm.Body, kind);
            _vm.EnterEditing();
            if (!_vm.IsEditing) return;
            ApplyWriterEdit(appended);
            FocusWriterSoon(appended);
            return;
        }

        // 한글을 조합하던 중이면 그 글자를 먼저 들여보낸다. 마우스로 단추를 눌러서는 조합이 끝나지 않으므로
        // 입력기에 '조합 끝내기'를 청하고, 그 글자가 쓰기 칸에 들어온 뒤(Background)에 글과 커서를 다시 읽는다.
        if (IsComposing(_writer))
        {
            CompleteComposition();
            Dispatcher.UIThread.Post(() => ToggleWriterLine(kind), DispatcherPriority.Background);
            return;
        }

        ToggleWriterLine(kind);
    }

    private void ToggleWriterLine(MemoLineKind kind)
    {
        if (_vm is null || _writer is null || !_vm.IsEditing || !_writer.IsVisible) return;

        var text = _writer.Text ?? "";
        var edit = MemoEditing.Toggle(text, _writer.SelectionStart, _writer.SelectionEnd, kind);
        ApplyWriterEdit(edit);
        _writer.Focus();
    }

    /// <summary>
    /// 입력기에 조합 중인 글자를 확정하라고 청한다(Windows IMM). 확정된 글자는 보통 입력처럼 쓰기 칸에 들어온다.
    /// 못 하면(다른 OS·입력기 없음) 아무것도 하지 않는다.
    /// </summary>
    private void CompleteComposition()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        try
        {
            var himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero) return;
            ImmNotifyIME(himc, NiCompositionStr, CpsComplete, 0);
            ImmReleaseContext(hwnd, himc);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private const int NiCompositionStr = 0x15;
    private const int CpsComplete = 0x1;

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmNotifyIME(IntPtr himc, int action, int index, int value);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);

    /// <summary>
    /// 체크·번호·점 줄에서 Enter: 다음 줄에 같은 기호를 붙인다. 기호만 있는 줄이면 기호를 지워 목록을 끝낸다.
    /// 한글 조합 중(쓰기 칸에 조합 글자가 떠 있음)에는 건드리지 않는다. Shift+Enter 는 보통 줄바꿈이다.
    /// </summary>
    private void ContinueListOnEnter(KeyEventArgs e)
    {
        if (_vm is null || _writer is null || !_vm.IsEditing || !_writer.IsKeyboardFocusWithin) return;
        if (IsComposing(_writer) || _writer.SelectionStart != _writer.SelectionEnd) return;

        if (MemoEditing.ContinueList(_writer.Text ?? "", _writer.CaretIndex) is not { } edit) return;
        ApplyWriterEdit(edit);
        e.Handled = true;
    }

    /// <summary>
    /// 고친 글을 쓰기 칸에 넣는다. 바인딩을 거쳐 EditText → 저장 규칙(자동 저장)으로 그대로 간다.
    /// </summary>
    private void ApplyWriterEdit(MemoEdit edit)
    {
        if (_writer is null) return;
        _writer.SetCurrentValue(TextBox.TextProperty, edit.Text);
        _writer.CaretIndex = edit.SelectionEnd;
        if (edit.SelectionStart != edit.SelectionEnd)
        {
            _writer.SelectionStart = edit.SelectionStart;
            _writer.SelectionEnd = edit.SelectionEnd;
        }
    }

    /// <summary>쓰기 칸이 보이게 된 뒤 포커스를 주고 커서를 다시 맞춘다(보이기 전엔 포커스가 가지 않는다).</summary>
    private void FocusWriterSoon(MemoEdit edit)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_writer is null || !_writer.IsVisible) return;
            _writer.Focus();
            var length = (_writer.Text ?? "").Length;
            _writer.CaretIndex = Math.Min(edit.Caret, length);
        }, DispatcherPriority.Background);
    }

    /// <summary>입력기가 아직 글자를 조합하고 있는지. TextBox 도 이때는 키를 처리하지 않는다.</summary>
    private static bool IsComposing(TextBox box)
    {
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        return presenter is not null && !string.IsNullOrEmpty(presenter.PreeditText);
    }

    // ───────────────────────── 태그 칸

    private bool IsInTagBox(object? source)
    {
        for (var node = source as Visual; node is not null; node = node.GetVisualParent())
            if (node == _tagBox) return true;
        return false;
    }

    private void OnTagBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tagBox is null || IsComposing(_tagBox)) return;

        if (e.Key == Key.Enter)
        {
            _vm.CommitTagInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.TagInput = "";
            e.Handled = true;
        }
    }

    // ───────────────────────── 창 테두리: 옮기기 · 크기 · 최소화 · 최대화 · 닫기

    private void HookChrome()
    {
        if (_titleBar is not null) _titleBar.PointerPressed += OnTitleBarPressed;

        var minimize = this.FindControl<Button>("MinimizeButton");
        if (minimize is not null) minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        if (_maximizeButton is not null) _maximizeButton.Click += (_, _) => ToggleMaximize();
        var close = this.FindControl<Button>("CloseButton");
        // 닫기는 OnClosing 을 거친다: 저장 못 한 글이 있으면 닫지 않고 띠로 알린다.
        if (close is not null) close.Click += (_, _) => Close();

        HookResizeGrip("GripTop", WindowEdge.North);
        HookResizeGrip("GripBottom", WindowEdge.South);
        HookResizeGrip("GripLeft", WindowEdge.West);
        HookResizeGrip("GripRight", WindowEdge.East);
        HookResizeGrip("GripTopLeft", WindowEdge.NorthWest);
        HookResizeGrip("GripTopRight", WindowEdge.NorthEast);
        HookResizeGrip("GripBottomLeft", WindowEdge.SouthWest);
        HookResizeGrip("GripBottomRight", WindowEdge.SouthEast);
    }

    private void HookResizeGrip(string name, WindowEdge edge)
    {
        var grip = this.FindControl<Border>(name);
        if (grip is null) return;

        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (WindowState != WindowState.Normal) return;
            BeginResizeDrag(edge, e);
            e.Handled = true;
        };
    }

    /// <summary>제목 줄을 끌면 창을 옮기고, 두 번 누르면 최대화·되돌리기. 단추 위에서는 하지 않는다.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        for (var node = e.Source as Visual; node is not null && node != _titleBar; node = node.GetVisualParent())
        {
            if (node is Button) return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) UpdateChromeForState();
    }

    /// <summary>최대화하면 모서리를 펴고 손잡이를 숨기며, 단추 그림을 '되돌리기'로 바꾼다.</summary>
    private void UpdateChromeForState()
    {
        var maximized = WindowState == WindowState.Maximized;
        _frame?.Classes.Set("maximized", maximized);
        if (_grips is not null) _grips.IsVisible = WindowState == WindowState.Normal;
        if (_maximizeButton is not null)
        {
            _maximizeButton.Classes.Set("restore", maximized);
            ToolTip.SetTip(_maximizeButton, maximized ? "이전 크기로" : "최대화");
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
            // 그래도 못 했으면 글을 이 PC 의 따로 둔 폴더에 남긴다. 끝내기는 막지 않는다.
            var exiting = _exiting
                          || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
            if (_vm.HasUnsavedText)
            {
                if (exiting) _vm.SaveUnsavedCopy();
                else
                {
                    e.Cancel = true;
                    _vm.RefuseClose();
                }
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

            // 제목 줄(잡고 끌 곳)이 어느 화면 안에도 없으면(모니터를 뺐을 때) 처음처럼 가운데에 둔다.
            // 창 테두리가 없어 제목 줄이 화면 밖이면 옮길 길이 없다.
            if (TitleBarVisibleAt(target)) Position = target;
            else CenterOnScreen();
        }

        _placementRestored = true;
    }

    /// <summary>창 왼쪽 위가 target 일 때 제목 줄 앞부분(16px 들어가서 120×30)이 어느 화면의 작업 영역 안에 다 들어오는지.</summary>
    private bool TitleBarVisibleAt(PixelPoint target)
        => Screens.All.Any(screen =>
        {
            var scale = screen.Scaling;
            var strip = new PixelRect(target.X + (int)(16 * scale), target.Y + (int)(6 * scale),
                (int)(120 * scale), (int)(30 * scale));
            return screen.WorkingArea.Contains(strip);
        });

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
