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

    private readonly ObservableCollection<ClipItem> _items = new();
    private readonly DispatcherTimer _searchTimer;
    private ClipFilter _filter = ClipFilter.All;
    private bool _hasMore;
    private bool _menuOpen;
    private IntPtr _prevForeground;
    private readonly RadioButton[] _chips;

    /// <summary>Don't hide on focus loss (snapshot / profile mode).</summary>
    public bool KeepOpen { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        _chips = new[] { FAll, FText, FLinks, FImages, FFiles, FPinned };

        _searchTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Reload(); };

        Deactivated += (_, _) => { if (!_menuOpen && !KeepOpen) HidePopup(); };
        SourceInitialized += (_, _) => SetupNativeWindow();
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScroll));
    }

    private void SetupNativeWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // tool window: no Alt+Tab entry; rounded corners and instant show/hide (our own animation is shorter)
        var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr((ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));
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
        ShowActivated = true;
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
        Body.Opacity = 0;
        Show();
        ForceForeground();
        Activate();
        Keyboard.Focus(Search);
        UpdateDust();

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
        Hide();
        // drop the items (and their thumbnails) while hidden; the next open reloads anyway
        _items.Clear();
        UpdateDust();
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

    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
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
        _items.Clear();
        foreach (var i in page) _items.Add(i);
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

    private bool PutOnClipboard(ClipItem item, bool plain, string? overrideText = null)
    {
        App.Storage.LoadFull(item);
        if (!ClipboardWriter.Set(item, plain, overrideText)) return false;
        App.Monitor.IgnoreOwnChange();
        if (App.Settings.MoveUsedToTop) App.Storage.Touch(item.Id);
        return true;
    }

    private async void Paste(ClipItem item, bool plain, string? overrideText = null)
    {
        if (!PutOnClipboard(item, plain, overrideText)) return;

        var target = _prevForeground;
        HidePopup();
        if (!CanPasteInto(target)) return; // it's on the clipboard; nothing to paste into

        Native.SetForegroundWindow(target);
        // wait for the switch instead of a fixed pause: usually a frame or two
        var sw = Stopwatch.StartNew();
        while (Native.GetForegroundWindow() != target && sw.ElapsedMilliseconds < 250)
            await Task.Delay(4);
        await Task.Delay(12); // let the target restore focus to its text field
        InputSender.SendPaste();
    }

    private void CopyOnly(ClipItem item)
    {
        if (PutOnClipboard(item, false)) HidePopup();
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
                    Process.Start(new ProcessStartInfo(item.ImagePath) { UseShellExecute = true });
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
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                if (Search.Text.Length > 0) Search.Text = "";
                else HidePopup();
                e.Handled = true;
                return;
            case Key.Down: Move(1); e.Handled = true; return;
            case Key.Up: Move(-1); e.Handled = true; return;
            case Key.PageDown: Move(6); e.Handled = true; return;
            case Key.PageUp: Move(-6); e.Handled = true; return;
            case Key.Enter:
                if (Selected is { } s) Paste(s, shift);
                e.Handled = true;
                return;
            case Key.Tab:
                CycleFilter(shift ? -1 : 1);
                e.Handled = true;
                return;
            case Key.Delete when Selected is { } d && Search.CaretIndex >= Search.Text.Length && Search.SelectionLength == 0:
                DeleteItem(d);
                e.Handled = true;
                return;
            case Key.H when ctrl && Selected is { } h:
                ToggleHidden(h);
                e.Handled = true;
                return;
            case Key.P when ctrl && Selected is { } p:
                TogglePin(p);
                e.Handled = true;
                return;
            case Key.C when ctrl && Search.SelectionLength == 0 && Selected is { } c:
                CopyOnly(c);
                e.Handled = true;
                return;
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
                e.Handled = true;
            }
        }
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
        Keyboard.Focus(Search);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = Search.Text.Length == 0;
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!IsVisible) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        Search.Text = "";
        Keyboard.Focus(Search);
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

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(src) is not null) return;
        if (FindAncestor<ScrollBar>(src) is not null) return;
        if (FindAncestor<ListBoxItem>(src)?.DataContext is ClipItem item)
        {
            e.Handled = true;
            Paste(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem { Secret: true } item) ToggleReveal(item);
        Keyboard.Focus(Search);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) TogglePin(item);
        Keyboard.Focus(Search);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) DeleteItem(item);
        Keyboard.Focus(Search);
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
        _menuOpen = true;
    }

    private void ItemMenu_Closed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
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
            case ClipKind.Image when !item.Secret: Add("Открыть изображение", "", () => Open(item)); break;
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
