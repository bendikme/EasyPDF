using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace EasyPDF.Views;

/// <summary>
/// A line icon: a figure on the 24-unit icon grid (a 20-unit live area inside 2 units of padding),
/// stroked with round ends in the text colour of whatever it sits in - so it follows a button's
/// hover, pressed and disabled colours by itself. It does what a Viewbox around a Path with a
/// binding to the button's Foreground does, as one element with no binding.
/// </summary>
public sealed class IconPath : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconPath),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The text colour, inherited like a TextBlock's: a button's template sets it on its content.</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconPath),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Line width in icon units, scaled with the icon as a Viewbox would.</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(IconPath),
        new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _pen;

    public IconPath()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty || e.Property == StrokeThicknessProperty) _pen = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is not { } data) return;
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;

        _pen ??= MakePen();
        var scale = side / 20.0;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale,
            ((ActualWidth - side) / 2) - (2 * scale), ((ActualHeight - side) / 2) - (2 * scale)));
        dc.DrawGeometry(null, _pen, data);
        dc.Pop();
    }

    private Pen MakePen()
    {
        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }
}
