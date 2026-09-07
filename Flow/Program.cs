using System;
using Avalonia;
using Avalonia.Media;

namespace Flow;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 340px짜리 거의 정적인 패널이다. GPU 컨텍스트(ANGLE)를 띄우면
            // 스레드와 메모리만 늘고 얻는 게 없다.
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            })
            .WithInterFont()
            .LogToTrace();
}
