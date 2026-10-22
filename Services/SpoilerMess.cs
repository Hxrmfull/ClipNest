using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipNest.Services;

/// <summary>Telegram-style spoiler dust. The whole animation is drawn once, in the background, as a 2 s loop of
/// 60 frames of a 128 px tile: thousands of tiny particles that fade in, shine and fade out while drifting. At runtime
/// one shared brush per kind just switches to the next frame 30 times a second, and every spoiler on screen tiles it
/// over its own area, so the cost doesn't grow with the number of spoilers. It runs only while the popup shows one.
/// The approach and the numbers follow Telegram Desktop (lib_ui, ui/effects/spoiler_mess.cpp, GPL-3.0).</summary>
public static class SpoilerMess
{
    public const int FramesCount = 60;
    public const int FrameMs = 33;
    /// <summary>The tile's size in device-independent pixels.</summary>
    public const double TileDip = 128;

    /// <summary>One kind of dust. Speed: how far a particle drifts over its whole life, in DIPs.</summary>
    public sealed record Kind(int FadeInMs, int ShownMs, int FadeOutMs, double SizeMin, double SizeMax,
                              double SpeedMin, double SpeedMax, int Sprites, int Particles, byte Darken);

    /// <summary>Text: dense and slow. Images: sparser, faster, no pause between fading in and out, a slight darkening.</summary>
    public static readonly Kind TextKind = new(200, 200, 200, 1.5, 2.0, 4, 8, 5, 9000, 0);
    public static readonly Kind ImageKind = new(300, 0, 300, 1.5, 2.0, 10, 20, 5, 3000, 32);

    /// <summary>What every text spoiler paints with (in the theme's text colour), and every image spoiler (white).</summary>
    public static ImageBrush TextBrush { get; } = NewBrush();
    public static ImageBrush ImageBrush { get; } = NewBrush();

    private static byte[][]? _textMasks, _imageMasks;
    private static BitmapSource[]? _textFrames, _imageFrames;
    private static double _scale;
    private static Task? _making;
    private static DispatcherTimer? _timer;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static int _shown = -1;

    /// <summary>How long the last making of the frames took (both kinds), for --spoilerbench.</summary>
    public static double MakeMs { get; private set; }

    private static ImageBrush NewBrush() => new()
    {
        TileMode = TileMode.Tile,
        Stretch = Stretch.Fill,
        ViewportUnits = BrushMappingMode.Absolute,
        Viewport = new Rect(0, 0, TileDip, TileDip),
    };

    /// <summary>Runs the animation (and has the frames made, the first time or for a new scale) or stops it.
    /// <paramref name="scale"/>: device pixels per DIP where the spoilers are, so the dots stay sharp.</summary>
    public static void Run(bool on, double scale)
    {
        if (!on)
        {
            _timer?.Stop();
            return;
        }
        scale = Math.Max(1, Math.Ceiling(scale * 4) / 4); // in quarter steps: no remake for a rounding difference
        if (_making is null || scale != _scale)
        {
            _scale = scale;
            int size = (int)Math.Round(TileDip * scale);
            var dispatcher = Dispatcher.CurrentDispatcher;
            _making = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var text = Generate(TextKind, size, scale, 1);
                var image = Generate(ImageKind, size, scale, 2);
                MakeMs = sw.Elapsed.TotalMilliseconds;
                dispatcher.BeginInvoke(() =>
                {
                    if (scale != _scale) return; // made for a scale that has changed meanwhile
                    (_textMasks, _imageMasks) = (text, image);
                    Colorize();
                    _shown = -1;
                    Tick();
                });
            });
        }
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(FrameMs) };
            _timer.Tick += (_, _) => Tick();
        }
        _timer.Start();
    }

    /// <summary>The theme changed: the text dust takes the new text colour.</summary>
    public static void ThemeChanged()
    {
        if (_textMasks is null) return;
        Colorize();
        _shown = -1;
        Tick();
    }

    private static void Colorize()
    {
        var text = Application.Current.Resources["TextPrimary"] is SolidColorBrush b ? b.Color : Colors.White;
        _textFrames = ToBitmaps(_textMasks!, text, 0);
        _imageFrames = ToBitmaps(_imageMasks!, Colors.White, ImageKind.Darken);
    }

    private static void Tick()
    {
        if (_textFrames is null || _imageFrames is null) return;
        int i = (int)(Clock.ElapsedMilliseconds / FrameMs % FramesCount);
        if (i == _shown) return;
        _shown = i;
        TextBrush.ImageSource = _textFrames[i];
        ImageBrush.ImageSource = _imageFrames[i];
    }

    private static BitmapSource[] ToBitmaps(byte[][] masks, Color color, byte darken)
    {
        int size = (int)Math.Sqrt(masks[0].Length);
        var result = new BitmapSource[masks.Length];
        var px = new byte[size * size * 4];
        for (int f = 0; f < masks.Length; f++)
        {
            var m = masks[f];
            for (int i = 0; i < m.Length; i++)
            {
                // premultiplied: the dot's colour over the darkening (black at `darken`)
                int a = m[i], back = darken * (255 - a) / 255;
                px[i * 4] = (byte)(color.B * a / 255);
                px[i * 4 + 1] = (byte)(color.G * a / 255);
                px[i * 4 + 2] = (byte)(color.R * a / 255);
                px[i * 4 + 3] = (byte)(a + back);
            }
            var bmp = BitmapSource.Create(size, size, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32, null, px, size * 4);
            bmp.Freeze();
            result[f] = bmp;
        }
        return result;
    }

    private struct Particle
    {
        public int Start, Sprite;
        public double X, Y, Dx, Dy;
    }

    /// <summary>The loop as coverage masks, one byte per pixel and frame: every particle starts at its own moment over
    /// the loop, lives FadeIn + Shown + FadeOut, drifts in a random direction and wraps around the tile's edges.</summary>
    public static byte[][] Generate(Kind kind, int size, double scale, int seed)
    {
        var random = new Random(seed);
        int life = kind.FadeInMs + kind.ShownMs + kind.FadeOutMs, loop = FramesCount * FrameMs;
        var particles = new Particle[kind.Particles];
        for (int i = 0; i < particles.Length; i++)
        {
            // a direction around the circle, a speed between min and max: the distance covered over the life
            double angle = random.NextDouble() * 2 * Math.PI;
            double speed = (kind.SpeedMin + (kind.SpeedMax - kind.SpeedMin) * random.NextDouble()) * scale / life;
            particles[i] = new Particle
            {
                Start = (int)((long)i * loop / particles.Length),
                Sprite = random.Next(kind.Sprites),
                X = random.NextDouble() * size,
                Y = random.NextDouble() * size,
                Dx = Math.Cos(angle) * speed,
                Dy = Math.Sin(angle) * speed,
            };
        }
        var sprites = new float[kind.Sprites][];
        int spriteSize = 2 + (int)Math.Ceiling(kind.SizeMax * scale);
        for (int i = 0; i < kind.Sprites; i++) sprites[i] = Sprite(kind, i, spriteSize, scale);

        var frames = new byte[FramesCount][];
        var cover = new float[size * size];
        for (int f = 0; f < FramesCount; f++)
        {
            Array.Clear(cover);
            int now = f * FrameMs;
            foreach (ref readonly var p in particles.AsSpan())
            {
                // the particle's own time, also one loop earlier: a life that began at the end of the loop goes on at its start
                Paint(p, now - p.Start);
                Paint(p, now + loop - p.Start);
            }
            var bytes = new byte[size * size];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Round(Math.Clamp(cover[i], 0, 1) * 255);
            frames[f] = bytes;
        }
        return frames;

        void Paint(in Particle p, int t)
        {
            if (t <= 0 || t >= life) return;
            double opacity = t < kind.FadeInMs ? t / (double)kind.FadeInMs
                : t > life - kind.FadeOutMs ? (life - t) / (double)kind.FadeOutMs
                : 1;
            int x0 = Wrap((int)Math.Round(p.X + p.Dx * t), size), y0 = Wrap((int)Math.Round(p.Y + p.Dy * t), size);
            var s = sprites[p.Sprite];
            for (int sy = 0; sy < spriteSize; sy++)
            {
                int row = Wrap(y0 + sy, size) * size;
                for (int sx = 0; sx < spriteSize; sx++)
                {
                    float a = s[sy * spriteSize + sx] * (float)opacity;
                    if (a <= 0) continue;
                    ref float d = ref cover[row + Wrap(x0 + sx, size)];
                    d = a + d * (1 - a); // "over": dots pile up into brighter ones, never past full
                }
            }
        }
    }

    private static int Wrap(int v, int size) => ((v % size) + size) % size;

    /// <summary>A rounded dot: the middle sprite round, those before it wider, those after it taller.</summary>
    private static float[] Sprite(Kind kind, int index, int size, double scale)
    {
        int middle = kind.Sprites / 2;
        double min = kind.SizeMin * scale, delta = (kind.SizeMax - kind.SizeMin) * scale;
        double w = index < middle ? min + delta * (middle - index) / middle : min;
        double h = index > middle ? min + delta * (index - middle) / (kind.Sprites - 1 - middle) : min;
        double r = min / 2;
        var result = new float[size * size];
        const int n = 8; // 8x8 samples per pixel for smooth edges
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < n; sy++)
                    for (int sx = 0; sx < n; sx++)
                    {
                        double px = x + (sx + 0.5) / n - 1, py = y + (sy + 0.5) / n - 1; // the dot starts at (1, 1)
                        if (InRoundedRect(px, py, w, h, r)) inside++;
                    }
                result[y * size + x] = inside / (float)(n * n);
            }
        return result;
    }

    private static bool InRoundedRect(double x, double y, double w, double h, double r)
    {
        if (x < 0 || y < 0 || x > w || y > h) return false;
        double cx = Math.Clamp(x, r, w - r), cy = Math.Clamp(y, r, h - r);
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
    }
}
