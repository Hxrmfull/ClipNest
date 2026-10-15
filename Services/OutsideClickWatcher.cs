using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace ClipNest.Services;

/// <summary>Reports a mouse click outside ClipNest while the popup is open.
/// Deactivated alone isn't enough: when Windows doesn't hand the popup real foreground status
/// (or takes it back quietly), a click on the desktop or the Start button never deactivates it.
/// A low-level mouse hook, installed only while the popup is shown, sees every click.
/// Like <see cref="KeyboardHook"/>, it lives on its own thread so mouse input never waits for the UI.</summary>
public sealed class OutsideClickWatcher : IDisposable
{
    private const uint MsgInstall = Native.WM_APP + 1, MsgRemove = Native.WM_APP + 2;

    private readonly Native.LowLevelKeyboardProc _proc; // same signature as LowLevelMouseProc; kept alive for the GC
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly uint _pid = (uint)Environment.ProcessId;
    private uint _threadId;
    private IntPtr _hook;

    public event Action? OutsideClick;

    public OutsideClickWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClipNest.MouseHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(2000);
    }

    public void Start() => Post(MsgInstall);
    public void Stop() => Post(MsgRemove);

    private void Post(uint msg)
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, msg, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        // GetMessage creates the thread's message queue; PostThreadMessage needs it
        Native.PostThreadMessage(_threadId, 0, IntPtr.Zero, IntPtr.Zero);
        _ready.Set();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == MsgInstall && _hook == IntPtr.Zero)
            {
                _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) Log.Write("mouse hook failed: " + Marshal.GetLastWin32Error());
            }
            else if (msg.message == MsgRemove && _hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
        {
            var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var root = Native.GetAncestor(Native.WindowFromPoint(m.pt), Native.GA_ROOT);
            Native.GetWindowThreadProcessId(root, out var pid);
            // our own windows (the popup, its context menu, tooltips, settings) don't count
            if (pid != _pid) _dispatcher.BeginInvoke(() => OutsideClick?.Invoke());
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
