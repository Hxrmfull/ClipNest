using System.Windows;
using System.Windows.Media;

namespace ClipNest;

/// <summary>Clips an element to a rounded rectangle that follows its size.
/// Border.CornerRadius only rounds the border itself; its content (an image) still has sharp corners.</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue > 0) el.SizeChanged += OnSizeChanged;
        Apply(el);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement el)
    {
        var r = GetRadius(el);
        if (r <= 0 || el.ActualWidth <= 0 || el.ActualHeight <= 0)
        {
            el.Clip = null;
            return;
        }
        var clip = new RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), r, r);
        clip.Freeze();
        el.Clip = clip;
    }
}
