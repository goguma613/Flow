using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Flow.Services;
using Flow.ViewModels;
using Flow.Views;

namespace Flow;

public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private GlobalHotKey? _hotKey;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 창을 숨겨도 앱이 종료되지 않도록 한다. 종료는 트레이 메뉴나 닫기 버튼으로만.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _viewModel = new MainViewModel();
            _window = new MainWindow { DataContext = _viewModel };
            desktop.MainWindow = _window;

            _hotKey = new GlobalHotKey(
                () => _window?.SummonToFront(),
                _viewModel.HotKey,
                (combo, ok) => _viewModel?.ReportHotKeyRegistered(combo, ok));

            // 조합을 바꾸면 등록을 다시 걸고, 트레이 메뉴에 적힌 글자도 따라간다.
            _viewModel.HotKeyChanged = combo =>
            {
                _hotKey?.Rebind(combo);
                RefreshTrayHeader();
            };

            RefreshTrayHeader();

            // 두 번째 실행이나 토스트 클릭으로 부르면 이미 떠 있는 창이 앞으로 나온다.
            SingleInstance.OnSummon(() =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _window?.SummonToFront()));

            desktop.ShutdownRequested += (_, _) =>
            {
                _hotKey?.Dispose();
                _viewModel?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 트레이 메뉴의 '열기' 옆에 지금 조합을 적어 둔다.
    /// 바꿔 놓고도 여기만 옛 조합이 남아 있으면, 그 말을 믿고 눌렀다가 아무 일도 안 일어난다.
    ///
    /// 저장된 값을 보는 이유: 새 조합을 잡는 동안에는 잠시 등록을 떼어 두는데,
    /// 그때 넘어오는 null 까지 따라가면 메뉴 글자가 깜빡인다.
    /// </summary>
    private void RefreshTrayHeader()
    {
        var item = TrayIcon.GetIcons(this)?
            .FirstOrDefault()?.Menu?.Items
            .OfType<NativeMenuItem>()
            .FirstOrDefault();

        if (item is null) return;

        var combo = _viewModel?.HotKey;
        item.Header = combo is null ? "열기" : $"열기  ({combo.Saved})";
    }

    private void OnTrayClicked(object? sender, EventArgs e) => _window?.SummonToFront();

    private void OnTrayOpen(object? sender, EventArgs e) => _window?.SummonToFront();

    private void OnTrayExit(object? sender, EventArgs e) => _window?.RequestExit();
}
