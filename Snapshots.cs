using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Services;

namespace ClipNest;

/// <summary>Developer tooling, not used in normal runs.
/// `--snapshot &lt;dir&gt;` renders the popup and settings in both themes to PNG;
/// `--profile &lt;file&gt;` measures open latency, CPU and memory. Both exit when done.</summary>
internal static class Snapshots
{
    public static async void Run(MainWindow popup, string dir)
    {
        Directory.CreateDirectory(dir);
        popup.KeepOpen = true;
        foreach (var (mode, name) in new[] { (ThemeMode.Dark, "dark"), (ThemeMode.Light, "light") })
        {
            Theme.Apply(mode);
            popup.ShowPopup();
            await Task.Delay(800);
            Save(popup.Shell, Path.Combine(dir, $"popup-{name}.png"), Color.FromRgb(0x5A, 0x64, 0x78));
            popup.RevealFirstSecret();
            await Task.Delay(700);
            Save(popup.Shell, Path.Combine(dir, $"popup-{name}-revealed.png"), Color.FromRgb(0x5A, 0x64, 0x78));
            popup.HidePopup();

            var sw = new SettingsWindow();
            sw.Show();
            await Task.Delay(500);
            Save((FrameworkElement)sw.Content, Path.Combine(dir, $"settings-{name}.png"), ((SolidColorBrush)sw.Background).Color);
            sw.Close();
        }
        App.Current.Quit();
    }

    /// <summary>`--screencheck &lt;dir&gt;`: shows the real popup over a plain backdrop window of our own and captures
    /// that screen area, to check what WPF rendering can't show: the system's rounded corners, border and shadow.</summary>
    public static async void ScreenCheck(MainWindow popup, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            popup.KeepOpen = true;
            var backdrop = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x84, 0x96)), ShowActivated = false,
                Left = 0, Top = 0, Width = 10, Height = 10,
            };
            backdrop.Show();
            var bh = new System.Windows.Interop.WindowInteropHelper(backdrop).Handle;
            const int bx = 120, by = 80, bw = 760, bh2 = 860;
            Native.SetWindowPos(bh, IntPtr.Zero, bx, by, bw, bh2, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            foreach (var (mode, name) in new[] { (ThemeMode.Dark, "dark"), (ThemeMode.Light, "light") })
            {
                Theme.Apply(mode);
                popup.ShowPopupAt(bx + 60, by + 60);
                await Task.Delay(1200);
                using (var bmp = new System.Drawing.Bitmap(bw, bh2))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(bx, by, 0, 0, new System.Drawing.Size(bw, bh2));
                    bmp.Save(Path.Combine(dir, $"screen-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                popup.HidePopup();
                await Task.Delay(300);
            }
            backdrop.Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        finally
        {
            App.Current.Quit();
        }
    }

    /// <summary>`--layoutcheck &lt;dir&gt;`: lays the popup out for a grid of monitors, sizes, scales and states, off-screen
    /// and without taking the foreground, and writes to report.txt every element that is cut off, sticks out of the
    /// window or runs into a neighbour; sheet-*.png show a picture of the main cases (and of every bad one).</summary>
    public static async void LayoutCheck(MainWindow popup, string dir)
    {
        var report = new StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
            var monitors = new (string name, int w, int h, double dpi)[]
            {
                ("2560x1440", 2560, 1392, 1), ("1920x1080", 1920, 1032, 1), ("1366x768", 1366, 728, 1),
                ("1920x1080@150%", 1920, 1032, 1.5), ("1280x720@150%", 1280, 680, 1.5),
            };
            int[] scales = { 80, 90, 100, 110, 125 };
            var sizes = new (int w, int h)[] { (420, 600), (400, 380), (800, 500), (420, 1000), (1200, 900), (3000, 3000), (555, 665) };
            string[] states = { "list", "images", "search", "empty", "sizing" };
            var s = App.Settings;
            int cases = 0, badCases = 0;
            var noTab = new SortedSet<string>();
            var narrowing = new List<double>();
            string drift = "";
            // the texts differ in length a lot from language to language: every layout in each of them
            foreach (var (language, lang) in new[] { (UiLanguage.Russian, "ru"), (UiLanguage.English, "en"), (UiLanguage.Chinese, "zh") })
            {
            Loc.Apply(language);
            var longestCount = Loc.Count(1_234_567, "Items");
            foreach (var (mode, theme) in new[] { (ThemeMode.Dark, "dark"), (ThemeMode.Light, "light") })
            {
                Theme.Apply(mode);
                foreach (var state in states)
                {
                    var seen = new HashSet<string>();
                    var shots = new List<(string label, string file)>();
                    foreach (var mon in monitors)
                    foreach (var scale in scales)
                    foreach (var size in sizes)
                    {
                        bool shot = (mon.name == "1920x1080" && (size == (420, 600) || size == (400, 380)))
                                    || (mon.name == "2560x1440" && scale == 100 && size != (420, 600) && size != (400, 380))
                                    || (mon.name == "1280x720@150%" && scale == 100 && size == (420, 600));
                        if (theme == "light" && !shot) continue; // the geometry doesn't depend on the theme
                        s.PopupScale = scale;
                        (s.PopupWidth, s.PopupHeight) = size;
                        var work = new Native.RECT { Right = mon.w, Bottom = mon.h };
                        bool first = !popup.IsVisible;
                        double before = popup.List.ActualWidth;
                        var lay = Stopwatch.StartNew();
                        popup.ShowForLayoutCheck(work, mon.dpi);
                        popup.UpdateLayout();
                        if (!first && popup.List.ActualWidth < before - 0.5) narrowing.Add(lay.Elapsed.TotalMilliseconds);
                        if (first) await SetState(popup, state);
                        popup.CountText.Text = longestCount;
                        popup.FitFooter();
                        await Settle(popup);

                        // many monitor/size pairs end up as the same layout: check each one once
                        var key = $"{popup.Width:0.#}x{popup.Height:0.#}@{popup.AppliedScale:0.###}";
                        if (!seen.Add(key) && !shot) continue;
                        cases++;
                        var found = new List<string>();
                        Inspect(popup, state, found);
                        // a preview confirmed without dragging must save exactly the size it was opened with
                        var (bw, bh) = popup.BaseSize();
                        double sc = popup.AppliedScale;
                        bool wFree = Math.Abs(popup.Width - size.w * sc) < 1, hFree = Math.Abs(popup.Height - size.h * sc) < 1; // not cut to the screen
                        if ((wFree && Math.Round(bw) != size.w) || (hFree && Math.Round(bh) != size.h))
                            found.Add($"a confirmed preview would save {Math.Round(bw)}x{Math.Round(bh)} instead of {size.w}x{size.h}");
                        if (size == (555, 665) && scale == 90 && mon.name == "2560x1440" && state == "list" && theme == "dark")
                            drift = $"555x665 at 90%: worked back from the window {Math.Round(popup.ActualWidth / sc)}x{Math.Round(popup.ActualHeight / sc)}, kept {Math.Round(bw)}x{Math.Round(bh)}";
                        string label = $"{lang} {theme} {state} {mon.name} {scale}% {size.w}x{size.h} -> {popup.Width:0}x{popup.Height:0} (x{popup.AppliedScale:0.###})";
                        if (!popup.TabHint.IsVisible) noTab.Add($"{lang} {popup.Width / popup.AppliedScale:0}x{popup.Height / popup.AppliedScale:0}@{popup.AppliedScale:0.##}");
                        if (found.Count > 0)
                        {
                            badCases++;
                            report.AppendLine("BAD  " + label);
                            foreach (var f in found.Distinct()) report.AppendLine("       " + f);
                        }
                        if ((shot || found.Count > 0) && shots.Count < 40)
                        {
                            var file = Path.Combine(dir, $"{lang}-{theme}-{state}-{shots.Count:00}.png");
                            Save(popup.Root, file, Color.FromRgb(0x5A, 0x64, 0x78), 1);
                            shots.Add(((found.Count > 0 ? "BAD " : "") + $"{mon.name} {scale}% {size.w}x{size.h}", file));
                        }
                    }
                    popup.SizingOverlay.Visibility = Visibility.Collapsed;
                    popup.PausedBadge.Visibility = Visibility.Collapsed;
                    popup.HidePopup();
                    Sheet(shots, Path.Combine(dir, $"sheet-{lang}-{theme}-{state}.png"));
                }
            }
            foreach (var (mode, theme) in new[] { (ThemeMode.Dark, "dark"), (ThemeMode.Light, "light") })
            {
                Theme.Apply(mode);
                foreach (var width in new[] { 560, 460 })
                foreach (var sizing in new[] { false, true })
                {
                    var sw = new SettingsWindow
                    {
                        ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -12000, Top = -12000, Width = width,
                    };
                    sw.Show();
                    if (sizing)
                    {
                        sw.SizeByMouse.Content = Loc.T("SizeDone");
                        sw.SizeHint.Text = Loc.T("SizeHintSizing");
                    }
                    await Task.Delay(300);
                    sw.UpdateLayout();
                    var content = (FrameworkElement)sw.Content;
                    var found = new List<string>();
                    CutOff(content, found);
                    var win = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                    foreach (var e in new FrameworkElement[] { sw.Size80, sw.Size125, sw.ResetSize, sw.SizeByMouse, sw.SizeHint, sw.LangRu, sw.LangZh })
                        if (!Inflate(win).Contains(Bounds(e, content))) found.Add($"outside the window: {Describe(e)}");
                    cases++;
                    string label = $"{lang} {theme} settings {width} wide{(sizing ? ", sizing" : "")}";
                    if (found.Count > 0)
                    {
                        badCases++;
                        report.AppendLine("BAD  " + label);
                        foreach (var f in found.Distinct()) report.AppendLine("       " + f);
                    }
                    Save(content, Path.Combine(dir, $"{lang}-{theme}-settings-{width}{(sizing ? "-sizing" : "")}.png"), ((SolidColorBrush)sw.Background).Color, 1);
                    sw.Close();
                }
            }
            }
            report.Insert(0, $"layouts checked: {cases}, with problems: {badCases}{Environment.NewLine}"
                + $"layout after making the list narrower (cards re-laid out): median {(narrowing.Count > 0 ? Median(narrowing) : 0):0.0} ms, max {(narrowing.Count > 0 ? narrowing.Max() : 0):0.0} ms over {narrowing.Count}{Environment.NewLine}"
                + $"{drift}{Environment.NewLine}"
                + $"the Tab hint made way for a 7-digit count at (language size at 100% @ scale): {string.Join(", ", noTab)}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            report.AppendLine("layout check failed: " + ex);
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
            App.Current.Quit();
        }
    }

    private static async Task SetState(MainWindow p, string state)
    {
        p.Search.Text = "";
        p.FAll.IsChecked = true;
        p.SizingOverlay.Visibility = p.PausedBadge.Visibility = Visibility.Collapsed;
        switch (state)
        {
            case "images": p.FImages.IsChecked = true; break;
            case "search":
                p.Search.Text = "строка"; // typed text: the clear button shows; with the paused badge the row is fullest
                p.PausedBadge.Visibility = Visibility.Visible;
                break;
            case "empty": p.Search.Text = "zzzzqqqq"; break;
            case "sizing":
                p.SizingOverlay.Visibility = Visibility.Visible;
                break;
        }
        await Task.Delay(700); // the debounced search, then the thumbnails decoding in the background
    }

    /// <summary>Lets the layout, the list's virtualization and newly shown thumbnails catch up.</summary>
    private static async Task Settle(MainWindow p)
    {
        for (int i = 0; i < 40; i++)
        {
            p.UpdateLayout();
            await p.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            bool pending = Descendants(p.List).OfType<System.Windows.Controls.Image>()
                .Any(img => img.IsVisible && img.Source is null && img.DataContext is Models.ClipItem { IsImage: true });
            if (!pending && i >= 1) return;
            await Task.Delay(25);
        }
    }

    private static void Inspect(MainWindow p, string state, List<string> found)
    {
        var root = p.Root;
        var win = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        if (Math.Abs(root.ActualWidth - p.Width) > 1 || Math.Abs(root.ActualHeight - p.Height) > 1)
            found.Add($"window {p.Width:0}x{p.Height:0}, content laid out at {root.ActualWidth:0}x{root.ActualHeight:0}");
        double s = p.AppliedScale;
        if (root.ActualWidth / s < MainWindow.MinWidth100 - 1 || root.ActualHeight / s < MainWindow.MinHeight100 - 1)
            found.Add($"below the minimum: {root.ActualWidth / s:0}x{root.ActualHeight / s:0} at 100%");

        CutOff(root, found);

        // nothing drawn past the window's edges (it would be cut off by the window itself)
        var drawn = ContentBounds(root);
        if (drawn.Right > win.Right + 1 || drawn.Bottom > win.Bottom + 1 || drawn.Left < -1 || drawn.Top < -1)
        {
            var culprit = Descendants(root).OfType<FrameworkElement>()
                .Where(c => c.IsVisible && !InsideClip(c, root) && !Inflate(win).Contains(Bounds(c, root))).LastOrDefault();
            found.Add($"drawn outside the window ({drawn.Left:0},{drawn.Top:0} {drawn.Width:0}x{drawn.Height:0} in {win.Width:0}x{win.Height:0})"
                + (culprit is null ? "" : $": {Describe(culprit)} at {Bounds(culprit, root)}"));
        }

        // 2. the controls that must be fully on screen
        var must = new List<FrameworkElement> { p.SearchBox, p.SettingsButton, p.FAll, p.FText, p.FLinks, p.FImages, p.FFiles, p.FPinned, p.FooterHints, p.CountText };
        must.AddRange(new FrameworkElement[] { p.Placeholder, p.ClearSearch, p.PausedBadge, p.EmptyGlyph, p.EmptyTitle, p.EmptyText, p.SizingBanner }.Where(e => e.IsVisible));
        foreach (var e in must)
        {
            if (!e.IsVisible) { found.Add($"not shown: {Describe(e)}"); continue; }
            var r = Bounds(e, root);
            if (r.Left < -0.5 || r.Top < -0.5 || r.Right > win.Right + 0.5 || r.Bottom > win.Bottom + 0.5)
                found.Add($"outside the window ({r.Left:0},{r.Top:0} {r.Width:0}x{r.Height:0} in {win.Width:0}x{win.Height:0}): {Describe(e)}");
        }
        var chipsPanel = (FrameworkElement)VisualTreeHelper.GetParent(p.FAll);
        var panel = Bounds(chipsPanel, root);
        foreach (var chip in new FrameworkElement[] { p.FAll, p.FText, p.FLinks, p.FImages, p.FFiles, p.FPinned })
            if (Bounds(chip, root).Right > panel.Right + 0.5) found.Add($"chip past the row's end: {Describe(chip)}");

        // 3. neighbours that must not run into each other
        var count = Bounds(p.CountText, root);
        var countText = new FormattedText(p.CountText.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(p.CountText.FontFamily, p.CountText.FontStyle, p.CountText.FontWeight, p.CountText.FontStretch), p.CountText.FontSize,
            Brushes.Black, null, TextOptions.GetTextFormattingMode(p), VisualTreeHelper.GetDpi(p).PixelsPerDip);
        if (countText.WidthIncludingTrailingWhitespace > p.CountText.ActualWidth + 0.5)
            found.Add($"footer: the item count is trimmed ({countText.WidthIncludingTrailingWhitespace:0.#} px of text in {p.CountText.ActualWidth:0.#})");
        var hints = Bounds(p.FooterHints, root);
        if (count.Right > hints.Left + 0.5)
            found.Add($"footer: the item count (to {count.Right:0}) runs into the key hints (from {hints.Left:0})");
        if (p.SizingBanner.IsVisible && Bounds(p.SizingBanner, root).Bottom > Bounds(p.List, root).Top + 30 * s)
            found.Add($"sizing: the banner covers the cards ({Bounds(p.SizingBanner, root).Bottom - Bounds(p.List, root).Top:0} px of them)");
        if (p.EmptyTitle.IsVisible && Bounds(p.EmptyText, root).Bottom > Bounds(p.List, root).Bottom + 0.5)
            found.Add("empty state: text below the list area");

        // 4. cards: everything within its card, and room for at least a couple of them
        var list = Bounds(p.List, root);
        if (list.Height / s < 120) found.Add($"list area only {list.Height / s:0} px high");
        var viewport = Descendants(p.List).OfType<System.Windows.Controls.ScrollContentPresenter>().First();
        var view = Bounds(viewport, root);
        foreach (var item in Descendants(p.List).OfType<System.Windows.Controls.ListBoxItem>().Where(i => i.IsVisible))
        {
            var card = Bounds(item, root);
            if (card.Bottom < list.Top || card.Top > list.Bottom) continue; // realized but scrolled out of view
            if (card.Right > view.Right + 0.5)
            {
                var widest = Descendants(viewport).OfType<System.Windows.Controls.ListBoxItem>().OrderByDescending(i => i.DesiredSize.Width).First();
                found.Add($"card wider than the list by {card.Right - view.Right:0.#} px: {Describe(item)} (widest wanted: {widest.DesiredSize.Width:0.#} by {(widest.DataContext as Models.ClipItem)?.Preview?.Split((char)10)[0]})");
            }
            foreach (var fe in Descendants(item).OfType<FrameworkElement>())
            {
                if (!fe.IsVisible || fe.Name is "actions" || InsideClip(fe, item)) continue;
                var r = Bounds(fe, root);
                if (r.Width < 0.5) continue;
                if (r.Right > card.Right + 0.5 || r.Left < card.Left - 0.5)
                    found.Add($"sticks out of its card by {Math.Max(r.Right - card.Right, card.Left - r.Left):0.#} px: {Describe(fe)}");
            }
            if (item.DataContext is Models.ClipItem { IsImage: true } ci)
            {
                var img = Descendants(item).OfType<System.Windows.Controls.Image>().FirstOrDefault(i => i.Name == "img");
                if (img is not null && img.ActualWidth * img.ActualHeight > 0 && Math.Min(img.ActualWidth, img.ActualHeight) < Math.Min(3, Math.Min(ci.Width, ci.Height)))
                    found.Add($"image preview collapsed to {img.ActualWidth:0.#}x{img.ActualHeight:0.#}: {ci.Width}x{ci.Height}");
            }
        }
    }

    private static bool Clips(DependencyObject d) => d is UIElement { ClipToBounds: true } or System.Windows.Controls.ScrollContentPresenter;

    private static bool InsideClip(DependencyObject d, DependencyObject stop)
    {
        for (var c = VisualTreeHelper.GetParent(d); c is not null && c != stop; c = VisualTreeHelper.GetParent(c))
            if (Clips(c)) return true;
        return false;
    }

    /// <summary>What a visual and its descendants draw, in its own coordinates; a clipping descendant (a scroll
    /// viewport, ClipToBounds) counts only with its own box, as nothing it holds can show outside of it.</summary>
    private static Rect ContentBounds(Visual v)
    {
        var r = VisualTreeHelper.GetContentBounds(v);
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(v); i < n; i++)
        {
            if (VisualTreeHelper.GetChild(v, i) is not Visual c || c is UIElement { IsVisible: false }) continue;
            var cb = Clips(c) && c is UIElement u ? new Rect(u.RenderSize) : ContentBounds(c);
            if (!cb.IsEmpty) r.Union(c.TransformToAncestor(v).TransformBounds(cb));
        }
        return r;
    }

    /// <summary>Anything the layout had to cut: it got less room than its content needs.</summary>
    private static void CutOff(FrameworkElement root, List<string> found)
    {
        foreach (var fe in Descendants(root).OfType<FrameworkElement>())
        {
            if (!fe.IsVisible || fe.ClipToBounds || fe is System.Windows.Controls.ScrollContentPresenter) continue;
            // an element laid out bigger than its slot keeps its size and is clipped to the slot by this clip
            if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(fe) is not { } layoutClip) continue;
            // capped on purpose: a card's text shows up to four lines, an image preview up to 150 px
            if (fe.ActualHeight >= fe.MaxHeight - 0.5 || fe.ActualWidth >= fe.MaxWidth - 0.5) continue;
            var content = ContentBounds(fe);
            if (content.IsEmpty) continue;
            var clip = layoutClip.Bounds;
            double over = Math.Max(Math.Max(content.Right - clip.Right, content.Bottom - clip.Bottom), Math.Max(clip.Left - content.Left, clip.Top - content.Top));
            if (over > 1)
            {
                var size = new Rect(clip.X - 1, clip.Y - 1, clip.Width + 2, clip.Height + 2);
                var culprit = Descendants(fe).OfType<FrameworkElement>()
                    .Where(c => c.IsVisible && !InsideClip(c, fe) && !size.Contains(c.TransformToAncestor(fe).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight))))
                    .LastOrDefault();
                found.Add($"cut off by {over:0.#} px: {Describe(fe)}" + (culprit is null ? "" : $"; sticking out: {Describe(culprit)}"));
            }
        }

    }

    private static Rect Inflate(Rect r) { r.Inflate(1, 1); return r; }

    private static Rect Bounds(FrameworkElement e, Visual root) =>
        e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }

    private static string Describe(FrameworkElement e)
    {
        var path = new List<string>();
        for (DependencyObject? c = e; c is not null && path.Count < 4; c = VisualTreeHelper.GetParent(c))
            if (c is FrameworkElement { Name.Length: > 0 } f) path.Add(f.Name);
        string text = e switch
        {
            System.Windows.Controls.TextBlock t => t.Text,
            System.Windows.Controls.ContentControl { Content: string c } => c,
            _ => "",
        };
        if (text.Length > 40) text = text[..40] + "…";
        return $"{e.GetType().Name} {string.Join(" < ", path)} \"{text}\" {e.ActualWidth:0.#}x{e.ActualHeight:0.#}";
    }

    /// <summary>The pictures of one state side by side, each scaled down to a cell, with its case under it.</summary>
    private static void Sheet(List<(string label, string file)> shots, string path)
    {
        if (shots.Count == 0) return;
        const int cell = 360, cols = 4, labelH = 22;
        var imgs = shots.Select(sh =>
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(sh.file);
            b.EndInit();
            b.Freeze();
            return (sh.label, bmp: (BitmapSource)b);
        }).ToList();
        int rows = (imgs.Count + cols - 1) / cols;
        var rowH = new double[rows];
        for (int i = 0; i < imgs.Count; i++)
        {
            var b = imgs[i].bmp;
            double k = Math.Min(1, cell / (double)b.PixelWidth);
            rowH[i / cols] = Math.Max(rowH[i / cols], Math.Min(b.PixelHeight * k, 900) + labelH);
        }
        double total = rowH.Sum() + 10 * rows;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x3C)), null, new Rect(0, 0, cols * (cell + 10), total));
            double y = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols && r * cols + c < imgs.Count; c++)
                {
                    var (label, b) = imgs[r * cols + c];
                    double k = Math.Min(Math.Min(1, cell / (double)b.PixelWidth), 900.0 / b.PixelHeight);
                    double x = c * (cell + 10);
                    dc.DrawImage(b, new Rect(x, y, b.PixelWidth * k, b.PixelHeight * k));
                    var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 12, label.StartsWith("BAD") ? Brushes.OrangeRed : Brushes.White, 1);
                    dc.DrawText(ft, new Point(x, y + b.PixelHeight * k + 3));
                }
                y += rowH[r] + 10;
            }
        }
        var rtb = new RenderTargetBitmap(cols * (cell + 10), (int)Math.Ceiling(total), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static async void Profile(MainWindow popup, string file)
    {
        try { await ProfileCore(popup, file); }
        catch (Exception ex) { File.WriteAllText(file, "profile failed: " + ex); }
        finally { App.Current.Quit(); }
    }

    private static async Task ProfileCore(MainWindow popup, string file)
    {
        popup.KeepOpen = true;
        var sb = new StringBuilder();
        await Task.Delay(1500); // let startup settle

        double first = await MeasureOpen(popup);
        popup.HidePopup();
        await Task.Delay(400);
        var opens = new List<double>();
        for (int i = 0; i < 15; i++)
        {
            opens.Add(await MeasureOpen(popup));
            popup.HidePopup();
            await Task.Delay(200);
        }
        sb.AppendLine($"open first frame: first={first:0.0} ms, median={Median(opens):0.0} ms, max={opens.Max():0.0} ms");

        popup.ShowPopup();
        await Task.Delay(3000); // thumbnails decode in the background right after opening
        var (cpuVisible, fps) = await Cpu(5000);
        sb.AppendLine($"visible idle: cpu={cpuVisible:0.0}% of one core");

        foreach (var q in new[] { "a", "ст", "github", "zzzzqqq" })
        {
            var times = Enumerable.Range(0, 5).Select(_ => popup.TimeReload(q)).ToList();
            sb.AppendLine($"search '{q}': median={Median(times):0.00} ms");
        }
        var clear = Enumerable.Range(0, 5).Select(_ => popup.TimeReload("")).ToList();
        sb.AppendLine($"reload (no search): median={Median(clear):0.00} ms");

        popup.HidePopup();
        await Task.Delay(3000);
        var (cpuHidden, _) = await Cpu(5000);
        var (cpuHidden2, _) = await Cpu(5000);
        await Task.Delay(10000);
        var (cpuHidden3, _) = await Cpu(10000);
        // the first window can still include .NET recompiling hot methods (tiered JIT) after the searches above
        sb.AppendLine($"hidden idle: cpu={cpuHidden:0.00}% then {cpuHidden2:0.00}%, after 20 s {cpuHidden3:0.00}%");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        sb.AppendLine($"memory: working set={proc.WorkingSet64 / 1048576.0:0} MB, private={proc.PrivateMemorySize64 / 1048576.0:0} MB, managed={GC.GetTotalMemory(false) / 1048576.0:0} MB");
        sb.AppendLine($"items in db: {App.Storage.Count()}");

        File.WriteAllText(file, sb.ToString());
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    /// <summary>Time from the show call until the second Rendering tick, i.e. the first frame has been composed.</summary>
    private static Task<double> MeasureOpen(MainWindow popup)
    {
        var tcs = new TaskCompletionSource<double>();
        var sw = Stopwatch.StartNew();
        int frames = 0;
        EventHandler? h = null;
        h = (_, _) =>
        {
            if (++frames < 2) return;
            CompositionTarget.Rendering -= h;
            tcs.TrySetResult(sw.Elapsed.TotalMilliseconds);
        };
        CompositionTarget.Rendering += h;
        popup.ShowPopup();
        return tcs.Task;
    }

    /// <summary>CPU of this process over the interval. (No CompositionTarget.Rendering here:
    /// merely subscribing to it makes WPF render every frame and would skew the result.)</summary>
    private static async Task<(double cpu, double fps)> Cpu(int ms)
    {
        using var proc = Process.GetCurrentProcess();
        var t0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        await Task.Delay(ms);
        proc.Refresh();
        var used = (proc.TotalProcessorTime - t0).TotalMilliseconds;
        return (used / sw.Elapsed.TotalMilliseconds * 100, double.NaN);
    }

    private static void Save(FrameworkElement el, string path, Color backdrop, double scale = 1.5)
    {
        var size = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, size);
        var rtb = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        // the element itself, not a VisualBrush of it: a brush stretches whatever its content bounds are to the box
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
