using System;

namespace Flow.Services;

/// <summary>업데이트 확인을 할지 말지. 순수 함수(시험 대상).</summary>
public enum UpdateCheckAction
{
    /// <summary>아무것도 하지 않는다.</summary>
    Skip,

    /// <summary>이미 받아 둔 새 버전이 있다. 다시 받지 않고 '준비됨'을 보인다.</summary>
    ShowReady,

    /// <summary>확인하고, 새 버전이 있으면 내려받는다.</summary>
    Check,
}

public static class UpdatePolicy
{
    /// <summary>30분 확인에서 내려받기가 실패하면 이만큼은 30분 확인을 쉰다. 다음 실행 때는 다시 해 본다.</summary>
    public static readonly TimeSpan PeriodicPauseAfterFailure = TimeSpan.FromHours(6);

    /// <param name="manual">설정의 '지금 확인'.</param>
    /// <param name="periodic">켜 둔 동안의 30분 확인.</param>
    /// <param name="busy">확인·내려받기가 이미 도는 중.</param>
    /// <param name="ready">새 버전을 이미 받아 두었다.</param>
    /// <param name="periodicPausedUntil">30분 확인을 쉬는 끝 시각.</param>
    public static UpdateCheckAction Decide(bool manual, bool periodic, bool autoUpdate, bool busy, bool ready,
        DateTime now, DateTime? periodicPausedUntil)
    {
        // 자동 확인을 꺼 뒀으면 아무 것도 하지 않는다. 직접 누른 경우는 그래도 확인한다.
        if (!manual && !autoUpdate) return UpdateCheckAction.Skip;
        if (busy) return UpdateCheckAction.Skip;

        // 받아 둔 것은 다시 받지 않는다. 직접 눌렀으면 준비됐다고 다시 알려 준다.
        if (ready) return manual ? UpdateCheckAction.ShowReady : UpdateCheckAction.Skip;

        if (periodic && periodicPausedUntil is { } until && now < until) return UpdateCheckAction.Skip;
        return UpdateCheckAction.Check;
    }

    /// <summary>확인을 마친 뒤 30분 확인을 쉴 끝 시각. 30분 확인의 내려받기가 실패했을 때만 새로 정한다.</summary>
    public static DateTime? PauseAfter(bool periodic, bool downloadFailed, DateTime now, DateTime? current)
        => periodic && downloadFailed ? now + PeriodicPauseAfterFailure : current;
}
