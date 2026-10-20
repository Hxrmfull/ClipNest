using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace ClipNest.Services;

/// <summary>A key the popup took while it is shown without focus (see <see cref="KeyboardHook.CapturePopupKeys"/>).</summary>
public readonly record struct PopupKey(int Vk, char Char, bool Ctrl, bool Shift);

/// <summary>Low-level keyboard hook. It takes over Win+V (RegisterHotKey can't: Explorer owns that combination)
/// and, while the popup is open without focus, takes the keys meant for it: the popup works like the built-in
/// clipboard panel, the app you were typing in keeps its focus and caret, so pasting is instant.
/// The hook lives on its own thread with its own message loop: Windows calls it for every key press
/// system-wide and waits for the answer, so it must never queue behind UI work.</summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // kept alive so the GC doesn't collect the callback
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HashSet<uint> _swallowedDown = new(); // hook thread only: their key-ups are swallowed too
    private readonly byte[] _keyState = new byte[256];
    private readonly StringBuilder _charBuf = new(8);
    private uint _threadId;
    private IntPtr _hook;
    private bool _swallowing;
    private volatile bool _enabled = true;
    private volatile bool _capture;
    private long _posted;      // hook thread: keys handed to the UI
    private long _acked;       // UI thread (Interlocked): keys the UI has taken
    private long _queueSince;  // hook thread: when the oldest key not yet taken was handed over
    private bool _stalled;     // hook thread: the UI fell behind; keys pass through until it catches up
    private const int StuckMs = 1500;

    public event Action? WinVPressed;
    public event Action<PopupKey>? PopupKeyPressed;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>While true, navigation keys, Enter/Esc/Tab/Del/Backspace, the popup's Ctrl shortcuts and printable
    /// characters are swallowed and raised as <see cref="PopupKeyPressed"/> on the UI thread.</summary>
    public bool CapturePopupKeys
    {
        get => _capture;
        set => _capture = value;
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
            if (k.dwExtraInfo != Native.InjectedMarker)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;

                if (k.vkCode == Native.VK_V)
                {
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

                if (down && _capture)
                {
                    // safety: if the UI thread stops taking keys, the popup would swallow typing system-wide.
                    // Keys then pass through until the UI has caught up, and capturing resumes by itself
                    // (switching it off for good left the popup open with typing going to the app behind it).
                    long now = Environment.TickCount64;
                    if (Interlocked.Read(ref _acked) == _posted)
                    {
                        _queueSince = now;
                        _stalled = false;
                    }
                    else if (!_stalled && now - _queueSince > StuckMs)
                    {
                        _stalled = true;
                        _dispatcher.BeginInvoke(() => Log.Write("popup didn't take keys for 1.5 s; passing keys through until it catches up"));
                    }
                }
                if (down && _capture && !_stalled && TryCapture(k, out var key))
                {
                    _swallowedDown.Add(k.vkCode);
                    _posted++;
                    _dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                    {
                        Interlocked.Increment(ref _acked);
                        PopupKeyPressed?.Invoke(key);
                    });
                    return new IntPtr(1);
                }
                // the app never saw the key-down, so it must not get a lone key-up either (even after the popup closed)
                if (up && _swallowedDown.Remove(k.vkCode)) return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Decides on the hook thread, without asking the UI, whether a key belongs to the popup.</summary>
    private bool TryCapture(Native.KBDLLHOOKSTRUCT k, out PopupKey key)
    {
        key = default;
        if (Win()) return false; // Win+arrows and other system shortcuts stay with Windows
        bool ctrl = Ctrl(), alt = Alt(), shift = Shift();
        bool altGr = ctrl && alt && (Native.GetAsyncKeyState(Native.VK_RMENU) & 0x8000) != 0;
        int vk = (int)k.vkCode;

        switch (vk)
        {
            case Native.VK_UP or Native.VK_DOWN or Native.VK_PRIOR or Native.VK_NEXT or Native.VK_HOME or Native.VK_END
                or Native.VK_LEFT or Native.VK_RIGHT or Native.VK_RETURN or Native.VK_ESCAPE or Native.VK_TAB
                or Native.VK_DELETE or Native.VK_BACK:
                if (alt && !altGr) return false;
                key = new PopupKey(vk, '\0', ctrl, shift);
                return true;
        }

        if (ctrl && !altGr)
        {
            // the popup's own shortcuts; everything else with Ctrl goes to the app as usual
            bool mine = vk is 'P' or 'H' or 'C' or 'V' or (>= '1' and <= '9') or (>= Native.VK_NUMPAD1 and <= Native.VK_NUMPAD9);
            if (mine) key = new PopupKey(vk, '\0', true, shift);
            return mine;
        }
        if (alt && !altGr) return false;

        char c = ToChar(k, shift, altGr);
        if (c == '\0' || char.IsControl(c)) return false;
        key = new PopupKey(vk, c, false, shift);
        return true;
    }

    /// <summary>The character the key types in the layout of the app being typed in (Russian, Polish with AltGr…).</summary>
    private char ToChar(Native.KBDLLHOOKSTRUCT k, bool shift, bool altGr)
    {
        Array.Clear(_keyState);
        if (shift) _keyState[Native.VK_SHIFT] = 0x80;
        if (altGr) { _keyState[Native.VK_CONTROL] = 0x80; _keyState[Native.VK_MENU] = 0x80; }
        if ((Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0) _keyState[Native.VK_CAPITAL] = 0x01;
        var fg = Native.GetForegroundWindow();
        var hkl = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(fg, out _));
        _charBuf.Clear();
        // flag 4: don't touch the keyboard state (dead keys) of the system
        int n = Native.ToUnicodeEx(k.vkCode, k.scanCode, _keyState, _charBuf, _charBuf.Capacity, 4, hkl);
        return n == 1 ? _charBuf[0] : '\0';
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
