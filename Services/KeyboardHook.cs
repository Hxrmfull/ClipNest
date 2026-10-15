using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace ClipNest.Services;

/// <summary>Low-level keyboard hook that takes over Win+V. RegisterHotKey can't be used:
/// Explorer already owns that combination.
/// The hook lives on its own thread with its own message loop: Windows calls it for every key
/// press system-wide and waits for the answer, so it must never queue behind UI work.</summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // kept alive so the GC doesn't collect the callback
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private IntPtr _hook;
    private bool _swallowing;
    private volatile bool _enabled = true;

    public event Action? WinVPressed;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public KeyboardHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClipNest.KeyboardHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(2000);
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        _ready.Set();

        // hook callbacks are delivered while this thread waits in GetMessage
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode == Native.VK_V && k.dwExtraInfo != Native.InjectedMarker)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;

                // a key-up we never saw (secure desktop, lock screen) must not leave V swallowed
                if (_swallowing && down && !Win()) _swallowing = false;

                if (_enabled && down && !_swallowing && Win() && !Ctrl() && !Alt() && !Shift())
                {
                    _swallowing = true;
                    InputSender.SendMaskKey();
                    _dispatcher.BeginInvoke(DispatcherPriority.Send, () => WinVPressed?.Invoke());
                    return new IntPtr(1);
                }
                if (_swallowing)
                {
                    if (up) _swallowing = false;
                    return new IntPtr(1); // autorepeat and the key-up of a swallowed V
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Win() => (Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 || (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0;
    private static bool Ctrl() => (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
    private static bool Alt() => (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
    private static bool Shift() => (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
