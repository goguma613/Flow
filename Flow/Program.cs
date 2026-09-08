using System;
using Avalonia;
using Avalonia.Media;
using Flow.Services;

namespace Flow;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 토스트를 누르면 Windows 가 이 exe 를 다시 실행한다.
        // 그 두 번째 프로세스가 같은 data.json 을 열면 서로의 저장을 덮어쓴다.
        // 먼저 떠 있는 창을 불러내고 조용히 물러난다.
        if (!SingleInstance.TryClaim())
        {
            SingleInstance.SummonExisting();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

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
