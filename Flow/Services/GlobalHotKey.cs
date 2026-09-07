using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;

namespace Flow.Services;

/// <summary>
/// 전용 스레드 하나에서 전역 단축키를 듣는다.
/// hWnd 없이 RegisterHotKey 를 호출하면 WM_HOTKEY 가 그 스레드의 메시지 큐로 들어오므로
/// 숨은 창을 만들 필요가 없다. 유휴 시에는 GetMessage 에서 블로킹되어 CPU를 쓰지 않는다.
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;
    private const int HotKeyId = 0xB0B0;

    private readonly Action _callback;
    private readonly Thread _thread;
    private uint _threadId;
    private bool _disposed;

    public GlobalHotKey(Action callback)
    {
        _callback = callback;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Flow.HotKey"
        };
        _thread.Start();
    }

    /// <summary>등록에 성공했는지. 다른 앱이 선점했다면 false.</summary>
    public bool IsRegistered { get; private set; }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        // RegisterHotKey 전에 메시지 큐가 존재해야 한다. PeekMessage 한 번이면 만들어진다.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

        IsRegistered = RegisterHotKey(IntPtr.Zero, HotKeyId, ModControl | ModAlt | ModNoRepeat, VkSpace);
        if (!IsRegistered) return;

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message != WmHotkey) continue;
                Dispatcher.UIThread.Post(_callback);
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HotKeyId);
        }
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
