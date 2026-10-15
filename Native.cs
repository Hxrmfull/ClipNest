using System;
using System.Runtime.InteropServices;

namespace ClipNest;

internal static class Native
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
    public const uint WM_APP = 0x8000;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56;
    /// <summary>Unassigned virtual key, used to stop the Start menu from opening after Win+V.</summary>
    public const int VK_MASK = 0xE8;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint INPUT_KEYBOARD = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>Marker put in dwExtraInfo of keys we inject ourselves, so our hook ignores them.</summary>
    public static readonly IntPtr InjectedMarker = new(0x434C4E53);

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public const uint WM_QUIT = 0x0012;
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }
}
