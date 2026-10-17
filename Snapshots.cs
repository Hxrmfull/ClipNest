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

    private static void Save(FrameworkElement el, string path, Color backdrop)
    {
        const double scale = 1.5;
        var size = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, size);
            dc.DrawRectangle(new VisualBrush(el), null, size);
        }
        var rtb = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
