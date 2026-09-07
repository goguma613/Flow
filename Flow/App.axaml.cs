using System;
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

            _hotKey = new GlobalHotKey(() => _window?.SummonToFront());

            desktop.ShutdownRequested += (_, _) =>
            {
                _hotKey?.Dispose();
                _viewModel?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => _window?.SummonToFront();

    private void OnTrayOpen(object? sender, EventArgs e) => _window?.SummonToFront();

    private void OnTrayExit(object? sender, EventArgs e) => _window?.RequestExit();
}
