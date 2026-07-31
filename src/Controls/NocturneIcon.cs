using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace WindowsLosslessSwitcher.Controls;

/// <summary>
/// Renders a Phosphor icon geometry (256×256 canvas) scaled to the element size and filled with
/// <see cref="Foreground"/>. A plain FrameworkElement render keeps the cost of the many small
/// icons in the redesigned surfaces negligible (no template, no visual tree per icon).
/// </summary>
public sealed class NocturneIcon : FrameworkElement
{
    // All bundled Phosphor glyphs are authored on a 256-unit viewBox.
    private const double CanvasSize = 256.0;

    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry),
        typeof(Geometry),
        typeof(NocturneIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Shares TextElement.Foreground so icons inherit their host's color (nav item, caption
    // button hover states) exactly like text does; explicit local values still win.
    public static readonly DependencyProperty ForegroundProperty =
        System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(
            typeof(NocturneIcon),
            new FrameworkPropertyMetadata(
                Brushes.White,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var geometry = Geometry;
        if (geometry is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / CanvasSize;
        drawingContext.PushTransform(new TranslateTransform(
            (ActualWidth - CanvasSize * scale) / 2,
            (ActualHeight - CanvasSize * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(Foreground, null, geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
