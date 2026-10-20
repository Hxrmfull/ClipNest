using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest;

public partial class MainWindow : Window
{
    private const int PageSize = 60;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWCP_ROUND = 2;

    private readonly ClipList _items = new();
    private ClipItem? _pressedItem;
    private readonly DispatcherTimer _searchTimer;
    private ClipFilter _filter = ClipFilter.All;
    private bool _hasMore;
    private long _shownAt;
    private IntPtr _prevForeground;
    /// <summary>Shown like the built-in clipboard: without taking the focus. The app keeps its caret,
    /// keys reach us through the keyboard hook, a pick pastes at once.</summary>
    private bool _passive;
    private readonly DispatcherTimer _caretBlink;
    private readonly RadioButton[] _chips;
    private readonly OutsideClickWatcher _outside;
    private readonly Native.WinEventProc _foregroundProc; // kept alive for the GC
    private IntPtr _foregroundHook;

    /// <summary>Don't hide on focus loss (snapshot / profile mode).</summary>
    public bool KeepOpen { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        _chips = new[] { FAll, FText, FLinks, FImages, FFiles, FPinned };

        _searchTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Reload(); };
        _caretBlink = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(530) };
        _caretBlink.Tick += (_, _) => FakeCaret.Opacity = FakeCaret.Opacity > 0 ? 0 : 1;
        App.Hook.PopupKeyPressed += OnPopupKey;
        SearchBox.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // a click in the search field switches to normal editing (caret, selection, Ctrl+A…)
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;
            if (_passive) EnterActiveMode();
            // already in normal mode but the keyboard went missing (the beeping state): take it back on click
            else if (Native.GetFocus() != new WindowInteropHelper(this).Handle) { ForceForeground(); TakeKeyboardFocus(); }
        };

        Deactivated += (_, _) => { if (!ItemMenu.IsOpen && !KeepOpen) OnFocusLost(IntPtr.Zero); };
        // Deactivated never comes if Windows didn't really give us the foreground (or took it back quietly):
        // then a click on the desktop or the Start menu left the popup open until it was clicked again.
        // So also close on any click outside and on any other app taking the foreground.
        _outside = new OutsideClickWatcher(Dispatcher);
        _outside.OutsideClick += () => { if (!KeepOpen) HidePopup(); };
        _foregroundProc = (_, _, hwnd, _, _, _, _) => { if (!KeepOpen && hwnd != IntPtr.Zero) OnFocusLost(hwnd); };
        SourceInitialized += (_, _) => SetupNativeWindow();
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScroll));
    }

    private void SetupNativeWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // tool window: no Alt+Tab entry; rounded corners and instant show/hide (our own animation is shorter)
        var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        // no activation on click either: like the built-in panel, the app being typed in keeps its focus
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
            new IntPtr((ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW));
        int round = DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int off = 1;
        Native.DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref off, sizeof(int));
        Theme.ApplyTitleBar(this);
    }

    /// <summary>Called once at startup: creates the window and runs layout and templates off-screen,
    /// so the first Win+V doesn't pay for it.</summary>
    public void Warmup()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        ShowActivated = false;
        Reload();
        Show();
        UpdateLayout();
        Hide();
        _items.Clear();
    }

    // ---------- show / hide ----------

    public void ShowPopup()
    {
        if (IsVisible)
        {
            HidePopup();
            return;
        }

        _prevForeground = Native.GetForegroundWindow();
        if (Search.Text.Length > 0) Search.Text = "";
        _filter = ClipFilter.All;
        FAll.IsChecked = true;
        Reload();
        UpdatePausedState();

        PositionNearCaret();
        _shownAt = Environment.TickCount64;
        Body.Opacity = 0;
        // opened from the tray there is no text field to keep focused: then work as a normal window
        _passive = !KeepOpen && CanPasteInto(_prevForeground);
        Show();
        if (_passive)
        {
            App.Hook.CapturePopupKeys = true;
            ShowFakeCaret(true);
        }
        else
        {
            EnterActiveMode();
        }
        UpdateDust();
        if (IsVisible) WatchOutside(true); // not if focus was already lost while showing

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
        ContentShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
    }

    /// <summary>For --screencheck: open at a fixed screen position (physical pixels).</summary>
    public void ShowPopupAt(int x, int y)
    {
        ShowPopup();
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public void HidePopup()
    {
        if (!IsVisible) return;
        App.Hook.CapturePopupKeys = false;
        _searchTimer.Stop();
        if (ItemMenu.IsOpen) ItemMenu.IsOpen = false;
        ShowFakeCaret(false);
        WatchOutside(false);
        Hide();
        // drop the items (and their thumbnails) while hidden; the next open reloads anyway
        _items.Clear();
        UpdateDust();
    }

    /// <summary>Right after Win+V, Windows sometimes hands the foreground to something else for a moment
    /// (seen with a text field focused: the text input host reacts to the Win key). Closing then made it look
    /// as if the popup never opened, so during the first moments the popup takes the foreground back instead.
    /// A real click outside always closes it (see <see cref="OutsideClickWatcher"/>).</summary>
    private void OnFocusLost(IntPtr thief)
    {
        if (!IsVisible) return;
        var thiefWindow = thief != IntPtr.Zero ? thief : Native.GetForegroundWindow();
        // Start and Windows search are always the user's own doing: close, as for any other app
        if (Environment.TickCount64 - _shownAt < 500 && !IsStartOrSearch(thiefWindow))
        {
            Log.Write("focus taken right after opening by " + Describe(thief != IntPtr.Zero ? thief : Native.GetForegroundWindow()) + "; taking it back");
            var target = _passive ? _prevForeground : new WindowInteropHelper(this).Handle;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!IsVisible || Native.GetForegroundWindow() == target) return;
                BringToForeground(target);
                if (!_passive)
                {
                    Activate();
                    Keyboard.Focus(Search);
                }
            });
            return;
        }
        HidePopup();
    }

    private static readonly string[] StartHosts = { "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost" };

    private static bool IsStartOrSearch(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return StartHosts.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";
        var sb = new StringBuilder(128);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        string name = "?";
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
        return $"{name} [{sb}]";
    }

    private void WatchOutside(bool on)
    {
        if (on)
        {
            _outside.Start();
            if (_foregroundHook == IntPtr.Zero)
                _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                    _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        }
        else
        {
            _outside.Stop();
            if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    private bool _dustRunning;
    private readonly TranslateTransform _dust1 = new(), _dust2 = new();

    /// <summary>Runs the two shared spoiler-dust clocks only while a secret is in the list and the window is shown.
    /// A running animation keeps WPF's render loop ticking, even for a hidden window.</summary>
    private void UpdateDust()
    {
        bool want = IsVisible && _items.Any(i => i.Secret);
        if (want == _dustRunning) return;
        _dustRunning = want;
        TranslateTransform t1 = _dust1, t2 = _dust2;
        if (want)
        {
            Drift(t1, TranslateTransform.XProperty, 0, -40, 3.2);
            Drift(t1, TranslateTransform.YProperty, 0, -40, 4.6);
            Drift(t2, TranslateTransform.XProperty, 0, 56, 5.0);
            Drift(t2, TranslateTransform.YProperty, 0, 56, 3.8);
        }
        else
        {
            foreach (var t in new[] { t1, t2 })
            {
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }
    }

    private void Dust1_Loaded(object sender, RoutedEventArgs e) => ((UIElement)sender).RenderTransform = _dust1;
    private void Dust2_Loaded(object sender, RoutedEventArgs e) => ((UIElement)sender).RenderTransform = _dust2;

    private static void Drift(TranslateTransform t, DependencyProperty p, double from, double to, double seconds)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(a, 24); // a slow drift; 24 fps is plenty and halves the render-loop cost of 60
        t.BeginAnimation(p, a);
    }

    public void UpdatePausedState() =>
        PausedBadge.Visibility = App.Monitor.Paused ? Visibility.Visible : Visibility.Collapsed;

    private void ForceForeground() => BringToForeground(new WindowInteropHelper(this).Handle);

    /// <summary>Makes a window the foreground one: ours for search editing, or the app to paste into.</summary>
    private static void BringToForeground(IntPtr hwnd)
    {
        // usually allowed outright: this process got the last input (the hook's key or a click on the popup).
        // AttachThreadInput is only the fallback, as it can leave the activation state of both threads confused.
        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd) return;
        var fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        if (fg != IntPtr.Zero && fgThread != me)
        {
            Native.AttachThreadInput(fgThread, me, true);
            Native.SetForegroundWindow(hwnd);
            Native.AttachThreadInput(fgThread, me, false);
        }
        else
        {
            Native.SetForegroundWindow(hwnd);
        }
    }

    /// <summary>Places the window under the text caret if the app exposes it, otherwise near the mouse.
    /// Works in physical pixels so mixed-DPI setups land correctly.</summary>
    private void PositionNearCaret()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();

        Native.POINT anchor;
        int anchorTop;
        if (TryGetCaret(out var caret, out var caretTop))
        {
            anchor = caret;
            anchorTop = caretTop;
        }
        else
        {
            Native.GetCursorPos(out anchor);
            anchorTop = anchor.Y;
        }

        var mon = Native.MonitorFromPoint(anchor, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        double scale = 1;
        if (Native.GetDpiForMonitor(mon, 0, out var dpiX, out _) == 0) scale = dpiX / 96.0;

        int w = (int)(Width * scale), h = (int)(Height * scale), gap = (int)(6 * scale);
        var work = mi.rcWork;

        int x = anchor.X;
        int y = anchor.Y + gap;
        if (y + h > work.Bottom) y = anchorTop - h - gap; // no room below: open above the caret
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - w - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - h - gap));

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private static bool TryGetCaret(out Native.POINT pt, out int top)
    {
        pt = default; top = 0;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint tid = Native.GetWindowThreadProcessId(fg, out _);
        var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(tid, ref gti) || gti.hwndCaret == IntPtr.Zero) return false;
        var r = gti.rcCaret;
        if (r.Left == 0 && r.Top == 0 && r.Bottom == 0) return false;
        var p = new Native.POINT { X = r.Left, Y = r.Bottom };
        Native.ClientToScreen(gti.hwndCaret, ref p);
        pt = p;
        top = p.Y - (r.Bottom - r.Top);
        return true;
    }

    // ---------- data ----------

    private void Reload(long? selectId = null)
    {
        var page = App.Storage.Query(Search.Text, _filter, 0, PageSize);
        _items.ReplaceAll(page);
        _hasMore = page.Count == PageSize;
        Renumber();

        int sel = 0;
        if (selectId is not null)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Id == selectId) { sel = i; break; }
        }
        Select(_items.Count > 0 ? sel : -1);
        UpdateEmptyState();
        UpdateCount();
        UpdateDust();
    }

    private void LoadMore()
    {
        if (!_hasMore) return;
        var page = App.Storage.Query(Search.Text, _filter, _items.Count, PageSize);
        foreach (var i in page) _items.Add(i);
        _hasMore = page.Count == PageSize;
        UpdateDust();
    }

    private void Renumber()
    {
        for (int i = 0; i < _items.Count; i++) _items[i].Index = i;
    }

    private void UpdateCount() =>
        CountText.Text = Ru.Plural(App.Storage.Count(), "запись", "записи", "записей");

    private void UpdateEmptyState()
    {
        bool empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        if (!string.IsNullOrWhiteSpace(Search.Text))
        {
            EmptyGlyph.Text = "";
            EmptyTitle.Text = "Ничего не найдено";
            EmptyText.Text = "Попробуйте другой запрос или фильтр.";
        }
        else if (_filter == ClipFilter.Pinned)
        {
            EmptyGlyph.Text = "";
            EmptyTitle.Text = "Нет закреплённых";
            EmptyText.Text = "Закреплённые записи не удаляются при очистке. Ctrl+P — закрепить.";
        }
        else
        {
            EmptyGlyph.Text = "";
            EmptyTitle.Text = "История пуста";
            EmptyText.Text = "Скопируйте что-нибудь — оно появится здесь.";
        }
    }

    public void OnItemCaptured(ClipItem item, bool isNew)
    {
        if (IsVisible) Reload(Selected?.Id);
    }

    private void OnListScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 400) LoadMore();
    }

    // ---------- selection ----------

    private ClipItem? Selected => List.SelectedItem as ClipItem;

    private void Select(int index)
    {
        if (index < 0 || _items.Count == 0)
        {
            List.SelectedIndex = -1;
            return;
        }
        index = Math.Clamp(index, 0, _items.Count - 1);
        if (index >= _items.Count - 3) LoadMore();
        List.SelectedIndex = index;
        List.ScrollIntoView(_items[index]);
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        Select(Math.Max(0, List.SelectedIndex) + delta);
    }

    // ---------- actions ----------

    private static readonly string[] ShellClasses =
        { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" };

    /// <summary>Opened from the tray, the "previous window" is the taskbar: there's nothing to paste into.</summary>
    private static bool CanPasteInto(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return false;
        var sb = new StringBuilder(64);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return !ShellClasses.Contains(sb.ToString());
    }

    private async Task<bool> PutOnClipboard(ClipItem item, bool plain, string? overrideText = null)
    {
        App.Storage.LoadFull(item);
        if (!await ClipboardWriter.SetAsync(item, plain, overrideText))
        {
            Log.Write("paste: the clipboard stayed locked by another program");
            return false;
        }
        App.Monitor.IgnoreOwnChange();
        if (App.Settings.MoveUsedToTop) App.Storage.Touch(item.Id);
        if (item.Secret && App.Settings.ClearSecretSeconds > 0) ClearLater(App.Settings.ClearSecretSeconds);
        return true;
    }

    /// <summary>Takes a pasted secret off the system clipboard after a while, unless something else was copied since.</summary>
    private static void ClearLater(int seconds)
    {
        uint seq = Native.GetClipboardSequenceNumber();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (Native.GetClipboardSequenceNumber() != seq) return;
            try
            {
                Clipboard.Clear();
                App.Monitor.IgnoreOwnChange();
            }
            catch { }
        };
        t.Start();
    }

    private static string ViewDir => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipNest-view");

    /// <summary>Images are stored encrypted, so "open" hands the viewer a temporary decrypted copy, deleted again
    /// after a couple of minutes (and on exit).</summary>
    private static string? WriteViewCopy(ClipItem item)
    {
        if (item.ImagePath is null || !System.IO.File.Exists(item.ImagePath)) return null;
        System.IO.Directory.CreateDirectory(ViewDir);
        var file = System.IO.Path.Combine(ViewDir, System.IO.Path.GetFileNameWithoutExtension(item.ImagePath) + ".png");
        System.IO.File.WriteAllBytes(file, Secrets.ReadImage(item.ImagePath));
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        t.Tick += (_, _) => { t.Stop(); try { System.IO.File.Delete(file); } catch { } };
        t.Start();
        return file;
    }

    public static void CleanViewCopies()
    {
        try { if (System.IO.Directory.Exists(ViewDir)) System.IO.Directory.Delete(ViewDir, true); } catch { }
    }

    private async void Paste(ClipItem item, bool plain, string? overrideText = null)
    {
        var target = _prevForeground;
        // close at once: for a big image the clipboard data is still being prepared in the background
        HidePopup();
        if (!await PutOnClipboard(item, plain, overrideText)) return;
        if (!CanPasteInto(target)) return; // it's on the clipboard; nothing to paste into

        // shown without focus, the app still has it, caret and all: paste right away, like the built-in panel.
        // Only after search editing (or a drag) does the focus have to go back first.
        if (Native.GetForegroundWindow() != target)
        {
            BringToForeground(target);
            var sw = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() != target && sw.ElapsedMilliseconds < 300)
                await Task.Delay(4);
            await Task.Delay(25); // let the app put the focus back into its text field
        }
        InputSender.SendPaste();
    }

    private async void CopyOnly(ClipItem item)
    {
        HidePopup();
        await PutOnClipboard(item, false);
    }

    private void PasteTransformed(ClipItem item, Func<string, string> transform)
    {
        App.Storage.LoadFull(item);
        if (item.Text is { } text) Paste(item, true, transform(text));
    }

    /// <summary>Ctrl+H: opens or closes the spoiler of a secret item; hides an ordinary one as a secret.</summary>
    private void ToggleHidden(ClipItem item)
    {
        if (item.Secret) ToggleReveal(item);
        else MarkSecret(item, true);
    }

    private static void ToggleReveal(ClipItem item)
    {
        if (!item.Revealed) App.Storage.LoadFull(item); // secrets have no stored preview
        item.Revealed = !item.Revealed;
    }

    private void MarkSecret(ClipItem item, bool secret)
    {
        try
        {
            App.Storage.SetSecret(item, secret);
        }
        catch (Exception ex)
        {
            Log.Write("secret: " + ex);
        }
        Reload(item.Id);
    }

    /// <summary>For --profile: how long a list reload takes for the given search.</summary>
    public double TimeReload(string search)
    {
        Search.Text = search;
        _searchTimer.Stop();
        var sw = Stopwatch.StartNew();
        Reload();
        UpdateLayout();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>For --snapshot: opens the first spoiler so both states can be checked.</summary>
    public void RevealFirstSecret()
    {
        if (_items.FirstOrDefault(i => i.Secret) is { } s) ToggleReveal(s);
    }

    /// <summary>OCR pass over an image copied before scanning was on (or with it off).</summary>
    private async void RescanImage(ClipItem item)
    {
        if (item.ImagePath is not { } path) return;
        int w = item.Width, h = item.Height;
        try
        {
            var scan = await Task.Run(async () => await ImageScanner.ScanAsync(ClipboardMonitor.DecodeBgra(Secrets.ReadImage(path)), w, h));
            if (scan is null) return;
            App.Storage.ApplyScan(item, scan);
            Log.Write($"rescan: {scan.Secrets.Count} secret(s), whole={scan.WholeImage}, {scan.Milliseconds:0} ms");
        }
        catch (Exception ex)
        {
            Log.Write("rescan: " + ex.Message);
        }
        if (IsVisible) Reload(item.Id);
    }

    private void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        App.Storage.SetPinned(item.Id, item.Pinned);
        Reload(item.Id);
    }

    private void DeleteItem(ClipItem item)
    {
        int idx = _items.IndexOf(item);
        App.Storage.Delete(item.Id);
        ThumbCache.Forget(item.ImagePath);
        _items.Remove(item);
        Renumber();
        Select(Math.Min(idx, _items.Count - 1));
        UpdateEmptyState();
        UpdateCount();
    }

    private void Open(ClipItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case ClipKind.Link:
                    App.Storage.LoadFull(item);
                    Process.Start(new ProcessStartInfo(item.Text!.Trim()) { UseShellExecute = true });
                    break;
                case ClipKind.Image when item.ImagePath is not null && !item.Secret:
                    if (WriteViewCopy(item) is { } copy) Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
                    break;
                case ClipKind.Files when item.FileList.Length > 0:
                    Process.Start("explorer.exe", $"/select,\"{item.FileList[0]}\"");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("open: " + ex.Message);
        }
        HidePopup();
    }

    // ---------- input ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleKey(key, mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift))) e.Handled = true;
    }

    /// <summary>Keys taken by the hook while the popup is shown without focus.</summary>
    private void OnPopupKey(PopupKey k)
    {
        if (!IsVisible || !_passive) return;
        if (ItemMenu.IsOpen)
        {
            if (k.Vk == Native.VK_ESCAPE) ItemMenu.IsOpen = false;
            return;
        }
        if (k.Char != '\0')
        {
            SetSearchText(Search.Text + k.Char);
            return;
        }
        if (k.Ctrl && k.Vk == 'V')
        {
            PasteIntoSearch();
            return;
        }
        var key = KeyInterop.KeyFromVirtualKey(k.Vk);
        switch (key)
        {
            case Key.Back:
                if (Search.Text.Length > 0) SetSearchText(k.Ctrl ? "" : Search.Text[..^1]);
                return;
            case Key.Home: Select(0); return;
            case Key.End: Select(_items.Count - 1); return;
            case Key.Left or Key.Right: return;
        }
        HandleKey(key, k.Ctrl, k.Shift);
    }

    private bool HandleKey(Key key, bool ctrl, bool shift)
    {
        // typed and pressed Enter (or an arrow) before the 90 ms search debounce ran: act on the results
        // of what is in the box now, not on the list from before
        if (key != Key.Escape && _searchTimer.IsEnabled)
        {
            _searchTimer.Stop();
            Reload();
        }
        switch (key)
        {
            case Key.Escape:
                if (Search.Text.Length > 0) Search.Text = "";
                else HidePopup();
                return true;
            case Key.Down: Move(1); return true;
            case Key.Up: Move(-1); return true;
            case Key.PageDown: Move(6); return true;
            case Key.PageUp: Move(-6); return true;
            case Key.Enter:
                if (Selected is { } s) Paste(s, shift);
                return true;
            case Key.Tab:
                CycleFilter(shift ? -1 : 1);
                return true;
            case Key.Delete when Selected is { } d && Search.CaretIndex >= Search.Text.Length && Search.SelectionLength == 0:
                DeleteItem(d);
                return true;
            case Key.H when ctrl && Selected is { } h:
                ToggleHidden(h);
                return true;
            case Key.P when ctrl && Selected is { } p:
                TogglePin(p);
                return true;
            case Key.C when ctrl && Search.SelectionLength == 0 && Selected is { } c:
                CopyOnly(c);
                return true;
        }

        if (ctrl)
        {
            int n = key switch
            {
                >= Key.D1 and <= Key.D9 => key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
                _ => -1,
            };
            if (n >= 0 && n < _items.Count)
            {
                Paste(_items[n], shift);
                return true;
            }
        }
        return false;
    }

    /// <summary>Leaves the focus-less mode: the popup takes the focus so the search field can be edited normally.</summary>
    private void EnterActiveMode()
    {
        ForceForeground();
        if (Native.GetForegroundWindow() != new WindowInteropHelper(this).Handle)
        {
            // Windows refused (it does, e.g., while an elevated app is in front). Going on without the focus and
            // without the hook left typing going nowhere; instead keep (or start) taking keys through the hook.
            // The next click into the search box tries again.
            Log.Write("search: Windows didn't hand the popup the focus; typing goes through the keyboard hook");
            _passive = true;
            if (!KeepOpen) App.Hook.CapturePopupKeys = true; // never take the keyboard in dev/profile runs
            ShowFakeCaret(true);
            return;
        }
        _passive = false;
        App.Hook.CapturePopupKeys = false;
        ShowFakeCaret(false);
        Activate();
        TakeKeyboardFocus();
        Search.CaretIndex = Search.Text.Length;
    }

    /// <summary>Gives the search box the real (Win32) keyboard focus. A no-activate window that was brought to the
    /// front can end up active with no focus at all: then every key arrives as Alt+key, Windows beeps at each one,
    /// nothing is typed, and WPF, still believing the box is focused, ignores further clicks.</summary>
    private void TakeKeyboardFocus()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Native.GetFocus() != hwnd)
        {
            Native.SetFocus(hwnd);
            // WPF skips focusing an element it thinks already has the focus: make it start over
            if (Keyboard.FocusedElement == Search) Keyboard.ClearFocus();
        }
        Keyboard.Focus(Search);
        if (Native.GetFocus() != hwnd) Log.Write("search: the popup is active but Windows didn't give it the keyboard focus");
    }

    /// <summary>Search edits that arrive through the hook: the caret goes to the end (so Del, scrolling and
    /// the drawn caret agree with what was typed).</summary>
    private void SetSearchText(string text)
    {
        Search.Text = text;
        Search.CaretIndex = text.Length;
    }

    /// <summary>Ctrl+V while the popup has no focus: the clipboard text becomes (part of) the search
    /// instead of being pasted into the app behind the popup.</summary>
    private void PasteIntoSearch()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { return; }
        var line = text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        if (line.Length > 200) line = line[..200];
        if (line.Length > 0) SetSearchText(Search.Text + line);
    }

    /// <summary>Without focus the search box draws no caret, so a blinking one is drawn after the text.</summary>
    private void ShowFakeCaret(bool on)
    {
        _caretBlink.Stop();
        FakeCaret.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        FakeCaret.Opacity = 1;
        PlaceFakeCaret();
        _caretBlink.Start();
    }

    private void PlaceFakeCaret()
    {
        if (FakeCaret.Visibility != Visibility.Visible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // an unfocused text box doesn't scroll to follow its caret: keep the end of a long query in view
            Search.ScrollToHorizontalOffset(double.MaxValue);
            Search.UpdateLayout();
            var r = Search.GetRectFromCharacterIndex(Search.Text.Length);
            double x = r.IsEmpty || double.IsInfinity(r.X) || double.IsNaN(r.X) ? 2 : r.X;
            x = Math.Clamp(x, 0, Math.Max(0, Search.ActualWidth - FakeCaret.Width));
            FakeCaret.Margin = new Thickness(x, 0, 0, 0);
            FakeCaret.Opacity = 1;
            if (_caretBlink.IsEnabled) { _caretBlink.Stop(); _caretBlink.Start(); }
        });
    }

    private void FocusSearch()
    {
        if (!_passive) Keyboard.Focus(Search);
    }

    private void CycleFilter(int dir)
    {
        int cur = Array.FindIndex(_chips, c => c.IsChecked == true);
        _chips[(cur + dir + _chips.Length) % _chips.Length].IsChecked = true;
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_chips is null) return; // fires during InitializeComponent
        _filter = sender switch
        {
            _ when sender == FText => ClipFilter.Text,
            _ when sender == FLinks => ClipFilter.Links,
            _ when sender == FImages => ClipFilter.Images,
            _ when sender == FFiles => ClipFilter.Files,
            _ when sender == FPinned => ClipFilter.Pinned,
            _ => ClipFilter.All,
        };
        if (IsVisible) Reload();
        FocusSearch();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = Search.Text.Length == 0;
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PlaceFakeCaret();
        if (!IsVisible) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        Search.Text = "";
        FocusSearch();
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private ClipItem? CardUnder(MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(src) is not null || FindAncestor<ScrollBar>(src) is not null) return null;
        return FindAncestor<ListBoxItem>(src)?.DataContext as ClipItem;
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _pressedItem = CardUnder(e);

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = CardUnder(e);
        var pressed = _pressedItem;
        _pressedItem = null;
        // a click is press and release on the same card (not a press on one and a release on another)
        if (item is null || !ReferenceEquals(item, pressed)) return;
        e.Handled = true;
        // shown without focus, WPF doesn't see modifier keys: ask the keyboard itself
        bool shift = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
        Paste(item, shift);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem { Secret: true } item) ToggleReveal(item);
        FocusSearch();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) TogglePin(item);
        FocusSearch();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) DeleteItem(item);
        FocusSearch();
    }

    /// <summary>The window is dragged by the grab strip or any empty spot; buttons, chips, the search field
    /// and the list keep their own clicks.</summary>
    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (e.Handled || FindAncestor<ListBox>(src) is not null || FindAncestor<ButtonBase>(src) is not null) return;
        if (IsInside(src, SearchBox)) return; // handled by SearchBox.PreviewMouseLeftButtonDown
        try { DragMove(); } catch (InvalidOperationException) { }
        FocusSearch();
    }

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        while (d is not null)
        {
            if (d == ancestor) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
        App.Current.OpenSettings();
    }

    // ---------- context menu ----------

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ClipItem ?? Selected;
        if (item is null)
        {
            e.Handled = true;
            return;
        }
        List.SelectedItem = item;
        BuildMenu(item);
    }

    private void ItemMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (_passive) return; // never had the focus: nothing to restore, and inactive is normal here
        if (IsVisible && !IsActive) HidePopup();
        else Keyboard.Focus(Search);
    }

    private void BuildMenu(ClipItem item)
    {
        ItemMenu.Items.Clear();

        MenuItem Add(string header, string glyph, Action action, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, Tag = glyph, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => action();
            ItemMenu.Items.Add(mi);
            return mi;
        }

        Add("Вставить", "", () => Paste(item, false), "Enter");
        if (item.HasRichFormat || item.IsFiles)
            Add("Вставить как текст", "", () => Paste(item, true), "Shift+Enter");
        Add("Копировать", "", () => CopyOnly(item), "Ctrl+C");
        ItemMenu.Items.Add(new Separator());
        Add(item.Pinned ? "Открепить" : "Закрепить", item.Pinned ? "" : "", () => TogglePin(item), "Ctrl+P");
        if (item.Secret)
        {
            Add(item.Revealed ? "Скрыть" : "Показать", item.Revealed ? "" : "", () => ToggleReveal(item), "Ctrl+H");
            Add("Это не секрет", "", () => MarkSecret(item, false));
        }
        else
        {
            Add("Скрыть как секрет", "", () => MarkSecret(item, true), "Ctrl+H");
        }
        switch (item.Kind)
        {
            case ClipKind.Link: Add("Открыть в браузере", "", () => Open(item)); break;
            case ClipKind.Image when !item.Secret:
                Add("Открыть изображение", "", () => Open(item));
                if (ImageScanner.Available) Add("Проверить на секреты", "\uE721", () => RescanImage(item));
                break;
            case ClipKind.Files: Add("Показать в проводнике", "", () => Open(item)); break;
        }

        if (item.IsTextual && !item.Secret)
        {
            ItemMenu.Items.Add(new Separator());
            Add("Вставить ВЕРХНИМ РЕГИСТРОМ", "", () => PasteTransformed(item, t => t.ToUpperInvariant()));
            Add("Вставить нижним регистром", "", () => PasteTransformed(item, t => t.ToLowerInvariant()));
            Add("Вставить без лишних пробелов", "", () => PasteTransformed(item, CollapseSpaces));
            if ((item.PreviewText ?? "").Contains('\n'))
                Add("Вставить в одну строку", "", () => PasteTransformed(item, t => Regex.Replace(t.Trim(), @"\s*\r?\n\s*", " ")));
        }

        ItemMenu.Items.Add(new Separator());
        var del = Add("Удалить", "", () => DeleteItem(item), "Del");
        del.Foreground = (Brush)FindResource("Danger");
    }

    private static string CollapseSpaces(string s)
    {
        var lines = s.Trim().Split('\n').Select(l => Regex.Replace(l.TrimEnd('\r').Trim(), "[ \t ]{2,}", " "));
        return string.Join(Environment.NewLine, lines);
    }
}
