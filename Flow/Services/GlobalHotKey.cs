using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;

namespace Flow.Services;

/// <summary>
/// 전용 스레드 하나에서 전역 단축키를 듣는다.
/// hWnd 없이 RegisterHotKey 를 호출하면 WM_HOTKEY 가 그 스레드의 메시지 큐로 들어오므로
/// 숨은 창을 만들 필요가 없다. 유휴 시에는 GetMessage 에서 블로킹되어 CPU를 쓰지 않는다.
///
/// 조합을 바꾸는 것도 이 스레드에서 해야 한다 — 등록이 스레드에 매여 있기 때문이다.
/// 그래서 <see cref="Rebind"/> 는 직접 등록하지 않고 그 스레드에 쪽지를 던진다.
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;

    /// <summary>조합을 바꾸라는 쪽지. WM_APP 위쪽은 앱이 마음대로 써도 되는 자리다.</summary>
    private const int WmRebind = 0x0400 + 0xB0;

    private const uint ModNoRepeat = 0x4000;
    private const int HotKeyId = 0xB0B0;

    private readonly Action _callback;
    private readonly Action<HotKeyCombo?, bool>? _report;
    private readonly ManualResetEventSlim _queueReady = new(false);

    private readonly Thread _thread;
    private uint _threadId;
    private bool _bound;
    private bool _disposed;

    /// <param name="callback">단축키가 눌렸을 때. UI 스레드에서 불린다.</param>
    /// <param name="initial">처음 걸 조합. null 이면 걸지 않는다.</param>
    /// <param name="report">등록 결과. (조합, 성공 여부)로 UI 스레드에서 불린다.</param>
    public GlobalHotKey(Action callback, HotKeyCombo? initial, Action<HotKeyCombo?, bool>? report = null)
    {
        _callback = callback;
        _report = report;
        Current = initial;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Flow.HotKey"
        };
        _thread.Start();
    }

    /// <summary>지금 걸려 있길 바라는 조합. null 이면 안 쓰는 상태.</summary>
    public HotKeyCombo? Current { get; private set; }

    /// <summary>등록에 성공했는지. 다른 앱이 선점했다면 false.</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>
    /// 조합을 바꾼다. null 을 주면 아예 떼어 놓는다 (새 조합을 잡는 동안에도 쓴다 —
    /// 걸려 있는 채로는 바로 그 조합을 다시 누를 수가 없기 때문이다).
    /// 결과는 생성자에 준 report 로 돌아온다.
    /// </summary>
    public void Rebind(HotKeyCombo? combo)
    {
        Current = combo;

        if (_disposed) return;
        if (!_queueReady.Wait(TimeSpan.FromSeconds(2))) return;

        PostThreadMessage(_threadId, WmRebind, (UIntPtr)(combo?.Modifiers ?? 0), (IntPtr)(combo?.VirtualKey ?? 0));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        // RegisterHotKey 전에 메시지 큐가 존재해야 한다. PeekMessage 한 번이면 만들어진다.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _queueReady.Set();

        Bind(Current?.Modifiers ?? 0, Current?.VirtualKey ?? 0);

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmRebind)
                {
                    Bind((uint)message.WParam, (uint)message.LParam);
                    continue;
                }

                if (message.Message != WmHotkey) continue;

                Dispatcher.UIThread.Post(_callback);
            }
        }
        finally
        {
            Unbind();
        }
    }

    /// <summary>
    /// 실제 등록. 반드시 이 스레드에서만 부른다.
    /// 등록에 실패해도 메시지 고리는 계속 돈다 — 여기서 빠져나가 버리면
    /// 조합이 남에게 선점됐을 때 다른 조합으로 바꿀 길까지 사라진다.
    /// </summary>
    private void Bind(uint modifiers, uint virtualKey)
    {
        Unbind();

        var wanted = virtualKey != 0;
        var ok = wanted && RegisterHotKey(IntPtr.Zero, HotKeyId, modifiers | ModNoRepeat, virtualKey);

        _bound = ok;
        IsRegistered = ok;

        if (_report is null) return;

        var combo = Current;
        Dispatcher.UIThread.Post(() => _report(combo, ok || !wanted));
    }

    private void Unbind()
    {
        if (!_bound) return;

        UnregisterHotKey(IntPtr.Zero, HotKeyId);
        _bound = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
