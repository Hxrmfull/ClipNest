using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClipNest.Services;

namespace ClipNest;

/// <summary>The spoiler over a hidden item, as in Telegram: <see cref="SpoilerMess"/> dust in the shape of the lines
/// of <see cref="Text"/> (made-up text of a plausible length: the real one is never drawn while hidden), or over the
/// whole area when there is no text (an image). Revealed, the <see cref="Target"/> shows through a circle that grows
/// from the mouse (or from the left edge, for a key press) while the dust gives way.</summary>
public sealed class SpoilerView : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(SpoilerView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight), typeof(double),
        typeof(SpoilerView), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(SpoilerView),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(SpoilerView),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(nameof(IsRevealed), typeof(bool),
        typeof(SpoilerView), new PropertyMetadata(false, (d, _) => ((SpoilerView)d).Apply(animate: true)));

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(UIElement),
        typeof(SpoilerView), new PropertyMetadata(null, (d, e) => ((SpoilerView)d).TargetChanged((UIElement?)e.OldValue)));

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public bool IsRevealed { get => (bool)GetValue(IsRevealedProperty); set => SetValue(IsRevealedProperty, value); }
    /// <summary>The real content (the text), hidden while this spoiler is. Images stay visible: blurred, under the dust.</summary>
    public UIElement? Target { get => (UIElement?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private FormattedText? _text;

    public SpoilerView()
    {
        IsHitTestVisible = false;
        // a recycled card: the state of the item it shows now, without animating the change
        IsVisibleChanged += (_, _) => Apply(animate: false);
        DataContextChanged += (_, _) => Apply(animate: false);
    }

    private bool Hiding => IsVisible && !IsRevealed;

    private void TargetChanged(UIElement? old)
    {
        if (old is not null) Release(old);
        Apply(animate: false);
    }

    private static void Release(UIElement target)
    {
        target.BeginAnimation(OpacityProperty, null);
        target.ClearValue(OpacityProperty);
        target.OpacityMask = null;
    }

    private void Apply(bool animate)
    {
        var target = Target;
        BeginAnimation(OpacityProperty, null);
        OpacityMask = null;
        if (target is not null)
        {
            target.BeginAnimation(OpacityProperty, null);
            target.OpacityMask = null;
        }
        if (!IsVisible)
        {
            // not a secret (or not on screen): leave the content alone
            if (target is not null) Release(target);
            Opacity = 1;
            return;
        }
        if (Hiding)
        {
            if (target is not null) target.Opacity = 0;
            if (animate) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            else Opacity = 1;
            return;
        }
        if (target is not null) target.Opacity = 1;
        if (!animate || ActualWidth <= 0)
        {
            Opacity = 0;
            return;
        }
        Reveal(target);
    }

    /// <summary>A circle grows from where the reveal was asked for: the content inside it, the dust outside.</summary>
    private void Reveal(UIElement? target)
    {
        var origin = Origin();
        // what is to be uncovered: the dust and the text itself (not the whole width its block is stretched to)
        var area = new Rect(RenderSize);
        if (target is FrameworkElement fe) area.Union(new Rect(0, 0, Math.Min(fe.ActualWidth, fe.DesiredSize.Width), fe.ActualHeight));
        double radius = 0;
        foreach (var c in new[] { area.TopLeft, area.TopRight, area.BottomLeft, area.BottomRight })
            radius = Math.Max(radius, (c - origin).Length);
        radius = radius / 0.8 + 4; // the soft edge (the outer fifth of the circle) passes the far corner too
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(radius * 1.6, 350, 600));
        var grow = new DoubleAnimation(0, radius, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };

        var dust = Mask(origin, inside: false);
        OpacityMask = dust;
        dust.BeginAnimation(RadialGradientBrush.RadiusXProperty, grow);
        dust.BeginAnimation(RadialGradientBrush.RadiusYProperty, grow);
        if (target is not null)
        {
            var content = Mask(origin, inside: true);
            target.OpacityMask = content;
            content.BeginAnimation(RadialGradientBrush.RadiusXProperty, grow);
            content.BeginAnimation(RadialGradientBrush.RadiusYProperty, grow);
        }
        var done = new DoubleAnimation(1, 1, duration);
        done.Completed += (_, _) =>
        {
            if (Hiding) return; // hidden again meanwhile
            OpacityMask = null;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            if (Target is { } t) t.OpacityMask = null;
        };
        BeginAnimation(OpacityProperty, done);
    }

    /// <summary>The mouse, if the reveal came from a click on this card; otherwise the left edge.</summary>
    private Point Origin()
    {
        for (DependencyObject? d = this; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem { IsMouseOver: true }) return System.Windows.Input.Mouse.GetPosition(this);
        return new Point(0, ActualHeight / 2);
    }

    /// <summary>Opaque inside the circle (<paramref name="inside"/>) or outside of it, with a soft edge.</summary>
    private static RadialGradientBrush Mask(Point origin, bool inside)
    {
        Color a = inside ? Colors.Black : Colors.Transparent, b = inside ? Colors.Transparent : Colors.Black;
        return new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = origin,
            GradientOrigin = origin,
            RadiusX = 0,
            RadiusY = 0,
            GradientStops = { new GradientStop(a, 0), new GradientStop(a, 0.8), new GradientStop(b, 1) },
        };
    }

    protected override Size MeasureOverride(Size available)
    {
        _text = null;
        if (string.IsNullOrEmpty(Text)) return default; // an image: whatever area it is given
        var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (!double.IsNaN(LineHeight)) ft.LineHeight = LineHeight;
        if (!double.IsInfinity(available.Width) && available.Width > 0) ft.MaxTextWidth = available.Width;
        _text = ft;
        return new Size(Math.Min(ft.WidthIncludingTrailingWhitespace, double.IsInfinity(available.Width) ? double.MaxValue : available.Width), ft.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_text is null)
        {
            dc.DrawRectangle(SpoilerMess.ImageBrush, null, new Rect(RenderSize));
            return;
        }
        // the line boxes of the made-up text, as Telegram fills the lines of the hidden words
        if (_text.BuildHighlightGeometry(new Point(0, 0)) is { } lines) dc.DrawGeometry(SpoilerMess.TextBrush, null, lines);
    }
}
