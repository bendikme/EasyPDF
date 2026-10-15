using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPDF.Pdf;
using EasyPDF.Services;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace EasyPDF.Views;

/// <summary>
/// Page thumbnails for one document, rendered when a thumbnail first comes into view and kept
/// for a while. They render below the pages on screen, so the sidebar never slows reading.
/// </summary>
public sealed class ThumbnailSource
{
    private const int CacheSize = 400;

    private readonly Dictionary<int, BitmapSource> _cache = new();
    private readonly LinkedList<int> _order = new();
    private readonly Dictionary<int, LinkedListNode<int>> _nodes = new();
    private readonly Dictionary<(int Page, int Width, int Height), Task<BitmapSource?>> _loading = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _wanted = new();

    public ThumbnailSource(PdfDocument document, int rotation)
    {
        Document = document;
        Rotation = rotation;
    }

    public PdfDocument Document { get; }

    public int Rotation { get; }

    /// <summary>Thumbnails held. For tests.</summary>
    internal int Count => _cache.Count;

    /// <summary>A thumbnail on screen says it wants its page; one scrolled away says it no longer does.</summary>
    public void Want(int page) => _wanted.AddOrUpdate(page, 1, (_, n) => n + 1);

    public void Unwant(int page)
    {
        if (_wanted.AddOrUpdate(page, 0, (_, n) => n - 1) <= 0) _wanted.TryRemove(page, out _);
    }

    public BitmapSource? TryGet(int page, int width, int height)
    {
        if (!_cache.TryGetValue(page, out var bitmap) || bitmap.PixelWidth != width || bitmap.PixelHeight != height) return null;
        if (_nodes.TryGetValue(page, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
        return bitmap;
    }

    /// <summary>The thumbnail of a page at a pixel size, rendering it if it is not held.</summary>
    public Task<BitmapSource?> GetAsync(int page, int width, int height)
    {
        if (TryGet(page, width, height) is { } held) return Task.FromResult<BitmapSource?>(held);
        var key = (page, width, height);
        if (_loading.TryGetValue(key, out var running)) return running;

        var task = Render();
        _loading[key] = task;
        return task;

        async Task<BitmapSource?> Render()
        {
            try
            {
                var bitmap = await Document.RenderPageAsync(page, width, height, Rotation, JobPriority.Thumbnail,
                    () => _wanted.ContainsKey(page)).ConfigureAwait(true);
                if (bitmap is not null) Store(page, bitmap);
                return bitmap;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"The thumbnail of page {page + 1} could not be drawn.", ex);
                return null;
            }
            finally
            {
                _loading.Remove(key);
            }
        }
    }

    private void Store(int page, BitmapSource bitmap)
    {
        _cache[page] = bitmap;
        if (_nodes.TryGetValue(page, out var existing)) _order.Remove(existing);
        _nodes[page] = _order.AddFirst(page);

        while (_cache.Count > CacheSize && _order.Last is { } last)
        {
            _order.RemoveLast();
            _nodes.Remove(last.Value);
            _cache.Remove(last.Value);
        }
    }
}

/// <summary>One page's thumbnail: paper with a soft edge, and the page drawn on it once rendered.</summary>
public sealed class ThumbnailImage : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ThumbnailSource), typeof(ThumbnailImage),
        new FrameworkPropertyMetadata(null, (d, _) => ((ThumbnailImage)d).Refresh()));

    public static readonly DependencyProperty PageIndexProperty = DependencyProperty.Register(
        nameof(PageIndex), typeof(int), typeof(ThumbnailImage),
        new FrameworkPropertyMetadata(-1, (d, _) => ((ThumbnailImage)d).Refresh()));

    private static readonly Brush Edge = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));

    private BitmapSource? _bitmap;
    private int _requestId;
    private (ThumbnailSource Source, int Page)? _wanting;

    public ThumbnailImage()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) =>
        {
            _requestId++;
            StopWanting();
        };
    }

    private void StopWanting()
    {
        if (_wanting is { } w) w.Source.Unwant(w.Page);
        _wanting = null;
    }

    public ThumbnailSource? Source
    {
        get => (ThumbnailSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int PageIndex
    {
        get => (int)GetValue(PageIndexProperty);
        set => SetValue(PageIndexProperty, value);
    }

    private (int Width, int Height) PixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Max(1, Math.Round(Width * dpi.DpiScaleX)), (int)Math.Max(1, Math.Round(Height * dpi.DpiScaleY)));
    }

    private void Refresh()
    {
        var id = ++_requestId;
        _bitmap = null;
        InvalidateVisual();
        StopWanting();

        if (!IsLoaded || Source is not { } source || PageIndex < 0 || double.IsNaN(Width) || double.IsNaN(Height)) return;
        var (w, h) = PixelSize();
        if (source.TryGet(PageIndex, w, h) is { } held)
        {
            _bitmap = held;
            return;
        }

        source.Want(PageIndex);
        _wanting = (source, PageIndex);
        _ = Load();

        async Task Load()
        {
            var bitmap = await source.GetAsync(PageIndex, w, h).ConfigureAwait(true);
            if (id != _requestId) return;
            _bitmap = bitmap;
            InvalidateVisual();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Refresh();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // A recycled container gets its page and its size one after the other: the bitmap is for both.
        if (e.Property == WidthProperty || e.Property == HeightProperty) Refresh();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(RenderSize);
        dc.DrawRectangle(Edge, null, new Rect(-1, -1, rect.Width + 2, rect.Height + 2));
        dc.DrawRectangle(Brushes.White, null, rect);
        if (_bitmap is not null) dc.DrawImage(_bitmap, rect);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
