using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Flow.Services;

/// <summary>
/// 같은 데이터 폴더를 두 프로세스가 동시에 쓰지 못하게 막는다.
///
/// 필요해진 계기는 토스트다. 알림을 누르면 Windows 가 등록된 exe 를 다시 실행하는데,
/// 그 두 번째 프로세스가 같은 data.json 을 열면 서로의 저장을 덮어쓴다.
/// exe 를 실수로 두 번 켰을 때도 같은 일이 벌어지고 있었다.
///
/// 자물쇠 이름에 데이터 폴더를 섞는 이유: FLOW_DATA_DIR 로 폴더를 갈라 두면
/// 시험용 사본과 실제 사용본이 서로를 막지 않아야 한다. 둘은 정말 남남이다.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _summon;
    private static Thread? _listener;

    /// <summary>
    /// 이 폴더의 주인 자리를 차지한다. 이미 주인이 있으면 false.
    /// </summary>
    public static bool TryClaim()
    {
        try
        {
            _mutex = new Mutex(true, $"Local\\Flow.Instance.{Key()}", out var mine);
            if (mine) return true;

            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (Exception)
        {
            // 자물쇠를 못 만들었다고 앱을 못 켜게 하지는 않는다.
            return true;
        }
    }

    /// <summary>먼저 떠 있는 창을 앞으로 불러낸다. 두 번째 프로세스가 물러나기 직전에 부른다.</summary>
    public static void SummonExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting($"Local\\Flow.Summon.{Key()}", out var handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch (Exception)
        {
            // 못 불러도 그냥 물러난다. 두 개가 뜨는 것보다는 낫다.
        }
    }

    /// <summary>
    /// 다른 프로세스가 부르면 실행할 일을 걸어 둔다.
    /// 전용 스레드 하나를 쓰지만 거의 언제나 잠들어 있어 부하가 없다.
    /// </summary>
    public static void OnSummon(Action callback)
    {
        try
        {
            _summon = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\Flow.Summon.{Key()}");
        }
        catch (Exception)
        {
            return;
        }

        _listener = new Thread(() =>
        {
            while (_summon is { } handle)
            {
                try
                {
                    if (handle.WaitOne()) callback();
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Flow.Summon"
        };

        _listener.Start();
    }

    public static void Release()
    {
        _summon?.Dispose();
        _summon = null;

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>데이터 폴더에서 뽑은 짧고 안정적인 이름.</summary>
    private static string Key()
    {
        var directory = DataStore.Directory.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(directory));
        return Convert.ToHexString(hash, 0, 8);
    }
}
