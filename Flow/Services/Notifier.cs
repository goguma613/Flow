using System;
using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Flow.Services;

/// <summary>
/// Windows 알림. 창이 트레이에 숨어 있을 때만 쓴다.
///
/// 창이 보이는 동안에는 목록의 물든 줄이 이미 같은 말을 하고 있으므로,
/// 화면 구석에 또 띄우면 같은 말을 두 번 하는 것이고 시선만 엉뚱한 데로 간다.
/// </summary>
public static class Notifier
{
    // SHQueryUserNotificationState 가 돌려주는 값들.
    private const int RunningD3dFullScreen = 3;
    private const int PresentationMode = 4;
    private const int AcceptsNotifications = 5;
    private const int QuietTime = 6;
    private const int RunningWindowsStoreApp = 7;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// 지금 방해해도 되는 때인지 Windows 에 물어본다.
    /// 전체화면 게임·발표 중·집중 지원 중이면 띄우지 않는다.
    ///
    /// 못 띄웠다고 그 알림이 사라지지는 않는다 — 목록의 줄은 물든 채로 남아 있어서,
    /// 게임에서 빠져나오는 순간 거기 있다. 그래서 큐도 재알림도 필요 없다.
    /// </summary>
    public static bool CanInterrupt()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return true;

            return state switch
            {
                RunningD3dFullScreen or PresentationMode or QuietTime or RunningWindowsStoreApp => false,
                AcceptsNotifications => true,

                // 모르는 값이면 띄우는 쪽으로 둔다. 알림을 놓치는 것이 더 나쁘다.
                _ => true
            };
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static Action? _onActivated;
    private static bool _hooked;
    private static int _serial;

    /// <summary>알림마다 다른 이름. 64자 제한이 있어 짧게 만든다.</summary>
    private static string NextTag() => $"r{DateTime.Now.Ticks:x}{_serial++:x}";

    /// <summary>
    /// 알림을 한 번 띄운다. 실패하면 false — 부른 쪽은 그때 처리 완료로 적지 않는다.
    /// </summary>
    public static bool Show(string title, string body, bool sound)
    {
        try
        {
            EnsureHooked();

            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(body);

            // 소리를 끈다고 알림 자체가 사라지지는 않는다. 조용히 뜰 뿐이다.
            if (!sound) builder.AddAudio(new ToastAudio { Silent = true });

            builder.Show(toast =>
            {
                // Windows 는 (Tag, Group) 쌍을 알림의 신원으로 본다.
                // Group 만 주고 Tag 를 비워 두면 매번 같은 신원이 되어, 두 번째부터는
                // 새 알림이 아니라 '기존 것의 갱신'으로 처리된다.
                // 갱신은 배너를 띄우지 않고 알림 센터 항목만 조용히 바꾼다.
                // 알림마다 다른 Tag 를 줘야 매번 배너가 뜬다.
                toast.Tag = NextTag();
                toast.Group = "flow-reminder";
            });

            return true;
        }
        catch (Exception)
        {
            // 알림 권한이 없거나 플랫폼이 거절한 경우. 앱이 멈출 이유는 없다.
            return false;
        }
    }

    /// <summary>
    /// 알림을 눌렀을 때 돌아올 자리를 기억해 둔다. 여기서는 아직 아무것도 등록하지 않는다.
    ///
    /// 등록을 미루는 이유: 툴킷에 손을 대는 순간 HKCU 에 이 exe 경로로 앱이 등록된다.
    /// 그 등록은 exe 경로에서 나오므로 파일을 옮기면 옛 등록이 쓰레기로 남는다.
    /// 트레이를 안 쓰는 사람은 알림을 한 번도 안 띄우니 흔적도 남지 않아야 한다.
    ///
    /// 누른 알림이 앱을 다시 실행시키면 SingleInstance 가 그것을 받아
    /// 이미 떠 있는 창을 앞으로 불러낸다.
    /// </summary>
    public static void HookActivation(Action onActivated) => _onActivated = onActivated;

    /// <summary>첫 알림을 띄우기 직전에 한 번만 등록한다.</summary>
    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;

        try
        {
            if (_onActivated is { } callback) ToastNotificationManagerCompat.OnActivated += _ => callback();
        }
        catch (Exception)
        {
            // 걸지 못해도 알림 자체는 뜬다.
        }
    }

    /// <summary>종료할 때 알림 센터에 남은 우리 알림을 치운다.</summary>
    public static void Cleanup()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception)
        {
            // 못 치워도 그만이다.
        }
    }
}
