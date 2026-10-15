using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EasyPDF.Pdf;
using Color = System.Windows.Media.Color;

namespace EasyPDF.Viewer;

/// <summary>
/// After a link is followed, the place it leads to is marked for a moment so the eye finds it: a
/// red frame that blinks three times and fades. It goes round the element the document marks
/// itself (an EPLAN cross-reference names the button that refers back - see PdfMark), the line of
/// text the destination points at, the area it names (a /FitR destination), or else the whole page.
/// </summary>
public sealed partial class PdfView
{
    private const int FlashMilliseconds = 2400;

    private static readonly Brush FlashFill = Frozen(new SolidColorBrush(Color.FromArgb(0x1C, 0xE5, 0x26, 0x2E)));
    private static readonly Brush FlashStroke = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x26, 0x2E)));

    private DispatcherTimer? _flashTimer;
    private long _flashStart;
    private int _flashPage = -1;
    private ResolvedDestination _flashTarget;
    private PageRect? _flashArea;
    private double _flashOpacity;

    /// <summary>The page and area being marked, while they are. For tests.</summary>
    internal (int Page, PageRect Area)? Flashing => _flashPage >= 0 && _flashArea is { } area ? (_flashPage, area) : null;

    private static readonly PageRect WholePage = new(0, 0, 1, 1);

    /// <summary>Marks the place a link led to on <paramref name="page"/>.</summary>
    private void Flash(int page, ResolvedDestination target)
    {
        StopFlash();
        _flashPage = page;
        _flashTarget = target;
        _flashArea = target.Area ?? (target.X is null && target.Y is null ? WholePage : null);

        // The line of text at the place needs the page's text, which may still be on its way.
        if (_flashArea is null)
        {
            if (InfoFor(page) is { } info) _flashArea = AreaAt(info.Text, target);
            else RequestPageInfo(page, JobPriority.Interactive);
        }

        _flashStart = Environment.TickCount64;
        if (_flashTimer is null)
        {
            _flashTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
            _flashTimer.Tick += (_, _) => OnFlashTick();
        }
        _flashTimer.Start();
        OnFlashTick();
    }

    private void OnFlashTick()
    {
        if (_flashPage < 0)
        {
            _flashTimer?.Stop();
            return;
        }

        var elapsed = Environment.TickCount64 - _flashStart;
        if (elapsed >= FlashMilliseconds)
        {
            StopFlash();
            return;
        }

        var hadArea = _flashArea is not null;
        if (_flashArea is null && InfoFor(_flashPage) is { } info) _flashArea = AreaAt(info.Text, _flashTarget);
        // Text that does not come in time: a band across the page where the place is.
        if (_flashArea is null && elapsed > 500) _flashArea = Band(_flashTarget);

        var opacity = FlashOpacity(elapsed);
        if (opacity == _flashOpacity && hadArea == (_flashArea is not null)) return;
        _flashOpacity = opacity;
        if (_visuals.TryGetValue(_flashPage, out var visual)) DrawOverlay(visual);
    }

    /// <summary>On, off, on, off, on - then fading, in tenths so it is not redrawn more than it shows.</summary>
    private static double FlashOpacity(long ms) => ms switch
    {
        < 300 => 1,
        < 450 => 0,
        < 750 => 1,
        < 900 => 0,
        < 1800 => 1,
        < FlashMilliseconds => Math.Round((1 - ((ms - 1800) / (double)(FlashMilliseconds - 1800))) * 10) / 10,
        _ => 0,
    };

    private void StopFlash()
    {
        _flashTimer?.Stop();
        var page = _flashPage;
        _flashPage = -1;
        _flashArea = null;
        _flashOpacity = 0;
        if (page >= 0 && _visuals.TryGetValue(page, out var visual)) DrawOverlay(visual);
    }

    /// <summary>Draws the frame on the overlay of its page, when it is showing.</summary>
    private void DrawFlash(DrawingContext dc, PageLayout layout, int page)
    {
        if (page != _flashPage || _flashArea is not { } area || _flashOpacity <= 0) return;

        var thickness = Math.Max(2, Px(2.5));
        var (x, y, w, h) = layout.ToPagePixels(page, area);
        Rect frame;
        if (area == WholePage)
        {
            // Round the page, inside its edge.
            frame = new Rect(thickness / 2.0, thickness / 2.0, Math.Max(1, w - thickness), Math.Max(1, h - thickness));
        }
        else
        {
            var pad = Px(4);
            frame = new Rect(x - pad, y - pad, w + (2 * pad), h + (2 * pad));
        }

        var pen = new Pen(FlashStroke, thickness);
        pen.Freeze();
        dc.PushOpacity(_flashOpacity);
        dc.DrawRectangle(area == WholePage ? null : FlashFill, pen, frame);
        dc.Pop();
    }

    /// <summary>
    /// The line of text a destination points at: the first line at or just below its top (a
    /// destination usually sits a little above the heading it names), from its left when it
    /// gives one. Null when there is no text near it.
    /// </summary>
    internal static PageRect? AreaAt(PageText text, ResolvedDestination target)
    {
        var top = target.Y ?? 0;
        var left = target.X;
        var best = -1;
        for (var i = 0; i < text.Count; i++)
        {
            var box = text.Boxes[i];
            if (box.IsEmpty || text.CodePoints[i] is ' ' or '\t' or '\r' or '\n') continue;
            if (box.Y1 <= top - 0.002 || box.Y0 > top + 0.12) continue;
            if (left is { } x && box.X1 < x - 0.01) continue;
            if (best < 0 || box.Y0 < text.Boxes[best].Y0 - 0.002 || (Math.Abs(box.Y0 - text.Boxes[best].Y0) <= 0.002 && box.X0 < text.Boxes[best].X0))
                best = i;
        }
        if (best < 0) return null;

        // The whole line: every character sharing most of its height.
        var row = text.Boxes[best];
        var line = row;
        for (var i = 0; i < text.Count; i++)
        {
            var box = text.Boxes[i];
            if (box.IsEmpty || text.CodePoints[i] is ' ' or '\t' or '\r' or '\n') continue;
            if (left is { } x && box.X1 < x - 0.01) continue;
            if (Math.Min(box.Y1, row.Y1) - Math.Max(box.Y0, row.Y0) >= 0.5 * Math.Min(box.Height, row.Height)) line = line.Union(box);
        }
        return line;
    }

    /// <summary>Where there is no text: a band across the page at the destination's height.</summary>
    private static PageRect Band(ResolvedDestination target)
    {
        if (target.Y is not { } top) return WholePage;
        var y0 = Math.Clamp(top, 0, 0.97);
        return PageRect.FromCorners(0.04, y0, 0.96, Math.Min(1, y0 + 0.03));
    }
}
