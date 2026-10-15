using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace EasyPDF.Views;

/// <summary>
/// The app icon - the family's rounded square, in green rather than Remote Desktop Manager's blue
/// so the two are told apart at a glance on the taskbar, with a page that has a folded corner -
/// drawn for one exact pixel size. The .ico is made from these drawings and the windows show the
/// same ones, so the icon is identical on the taskbar, in Explorer and in the app.
///
/// Straight lines sit on whole pixels so nothing is smeared across two; 16 and 24, the sizes
/// Windows shows most, are placed by hand. Only the square's corners and the fold's diagonal are
/// anti-aliased.
/// </summary>
public static class AppIconArt
{
    private static readonly Color Top = Color.FromRgb(0x2F, 0xC2, 0x6B);
    private static readonly Color Bottom = Color.FromRgb(0x0A, 0x7A, 0x3D);

    private static readonly Brush Glyph = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush Lines = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));

    /// <summary>Draws the icon into the square from (0,0) to (<paramref name="px"/>, <paramref name="px"/>), in pixels.</summary>
    public static void Draw(DrawingContext dc, int px)
    {
        if (px <= 0) return;

        if (px < 16)
        {
            dc.PushTransform(new ScaleTransform(px / 16.0, px / 16.0));
            Draw16(dc);
            dc.Pop();
        }
        else if (px < 20)
        {
            var offset = (px - 16) / 2;
            dc.PushTransform(new TranslateTransform(offset, offset));
            Draw16(dc);
            dc.Pop();
        }
        else if (px is >= 24 and <= 26)
        {
            var offset = (px - 24) / 2;
            dc.PushTransform(new TranslateTransform(offset, offset));
            Draw24(dc);
            dc.Pop();
        }
        else
        {
            DrawLarge(dc, px);
        }
    }

    private static void Draw16(DrawingContext dc)
    {
        Square(dc, 16, inset: 1, radius: 3);
        // Page: columns 4-11, rows 3-12, one pixel thick, corner folded over three pixels.
        Fill(dc, 4, 3, 5, 13);     // left
        Fill(dc, 4, 12, 12, 13);   // bottom
        Fill(dc, 11, 6, 12, 13);   // right, below the fold
        Fill(dc, 4, 3, 9, 4);      // top, up to the fold
        Triangle(dc, 9, 3, 12, 6, 9, 6);
        Fill(dc, 6, 8, 10, 9, Lines);
        Fill(dc, 6, 10, 10, 11, Lines);
    }

    private static void Draw24(DrawingContext dc)
    {
        Square(dc, 24, inset: 1, radius: 5);
        // Page: columns 6-17, rows 4-19, two pixels thick, corner folded over five.
        Fill(dc, 6, 4, 8, 20);
        Fill(dc, 6, 18, 18, 20);
        Fill(dc, 16, 9, 18, 20);
        Fill(dc, 6, 4, 13, 6);
        Triangle(dc, 13, 4, 18, 9, 13, 9);
        Fill(dc, 10, 11, 14, 12, Lines);
        Fill(dc, 10, 13, 14, 14, Lines);
        Fill(dc, 10, 15, 13, 16, Lines);
    }

    private static void DrawLarge(DrawingContext dc, int s)
    {
        var inset = Math.Max(1, (int)Math.Round(s * 0.04));
        Square(dc, s, inset, (s - (2 * inset)) * 0.24);

        var w = Math.Max(2, (int)Math.Round(s * 0.07));
        var left = (int)Math.Round(s * 0.27);
        var right = s - left;
        var top = (int)Math.Round(s * 0.19);
        var bottom = s - top;
        var fold = (int)Math.Round((right - left) * 0.40);

        Fill(dc, left, top, left + w, bottom);
        Fill(dc, left, bottom - w, right, bottom);
        Fill(dc, right - w, top + fold, right, bottom);
        Fill(dc, left, top, right - fold, top + w);
        Triangle(dc, right - fold, top, right, top + fold, right - fold, top + fold);

        // Three lines of "text", the last one shorter.
        var line = Math.Max(1, (int)Math.Round(w * 0.6));
        var x0 = left + (2 * w);
        var x1 = right - (2 * w);
        var gap = Math.Max(line + 1, (int)Math.Round(s * 0.085));
        var y = top + fold + gap - line;
        for (var i = 0; i < 3 && y + line <= bottom - (2 * w); i++, y += gap)
            Fill(dc, x0, y, i == 2 ? x0 + ((x1 - x0) * 2 / 3) : x1, y + line, Lines);
    }

    private static void Square(DrawingContext dc, int size, int inset, double radius)
    {
        var fill = new LinearGradientBrush(Top, Bottom, new Point(0, 0), new Point(1, 1));
        fill.Freeze();
        var extent = size - (2 * inset);
        dc.DrawRoundedRectangle(fill, null, new Rect(inset, inset, extent, extent), radius, radius);
    }

    private static void Fill(DrawingContext dc, double x0, double y0, double x1, double y1, Brush? brush = null) =>
        dc.DrawRectangle(brush ?? Glyph, null, new Rect(x0, y0, x1 - x0, y1 - y0));

    private static void Triangle(DrawingContext dc, double ax, double ay, double bx, double by, double cx, double cy)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(ax, ay), true, true);
            ctx.LineTo(new Point(bx, by), false, false);
            ctx.LineTo(new Point(cx, cy), false, false);
        }
        g.Freeze();
        dc.DrawGeometry(Glyph, null, g);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Shows the app icon at the element's size, drawn for the exact number of device pixels it covers.</summary>
public sealed class AppIcon : FrameworkElement
{
    public AppIcon()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var side = Math.Min(ActualWidth, ActualHeight);
        var px = (int)Math.Round(side * dpi.DpiScaleX);
        if (px <= 0) return;

        dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
        AppIconArt.Draw(dc, px);
        dc.Pop();
    }
}
