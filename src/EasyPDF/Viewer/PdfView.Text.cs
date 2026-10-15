using System.Windows;
using System.Windows.Media;
using EasyPDF.Pdf;
using EasyPDF.Services;
using Color = System.Windows.Media.Color;

namespace EasyPDF.Viewer;

/// <summary>A position between two characters of a page: before character <see cref="Caret"/>.</summary>
public readonly record struct TextPoint(int Page, int Caret) : IComparable<TextPoint>
{
    public int CompareTo(TextPoint other) =>
        Page != other.Page ? Page.CompareTo(other.Page) : Caret.CompareTo(other.Caret);
}

public sealed partial class PdfView
{
    /// <summary>Pages whose links and text are kept: they are what hovering and selecting need.</summary>
    private const int PageInfoCacheSize = 48;

    private static readonly Brush SearchBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xC4, 0x00)));
    private static readonly Brush CurrentMatchBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x7A, 0x00)));
    private static readonly Pen CurrentMatchPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x00)), 1.5);

    private readonly Dictionary<int, PageInfo> _info = new();
    private readonly Dictionary<int, long> _infoStamp = new();
    private readonly HashSet<int> _infoLoading = new();
    private long _infoClock;
    private int _infoGeneration;

    private readonly Dictionary<int, List<SearchMatch>> _matchesByPage = new();
    private SearchMatch? _currentMatch;

    private TextPoint? _selectionAnchor;
    private TextPoint? _selectionFocus;

    /// <summary>Raised when text is selected or the selection goes.</summary>
    public event EventHandler? SelectionChanged;

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private void ResetText()
    {
        _infoGeneration++;
        _pendingClick = null;
        _info.Clear();
        _infoStamp.Clear();
        _infoLoading.Clear();
        _matchesByPage.Clear();
        _currentMatch = null;
        var hadSelection = HasSelection;
        _selectionAnchor = _selectionFocus = null;
        if (hadSelection) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===================================================================== page info

    /// <summary>Forgets every page's links and text, as if none had been read yet. For tests.</summary>
    internal void ForgetPageInfo()
    {
        _infoGeneration++;
        _info.Clear();
        _infoStamp.Clear();
        _infoLoading.Clear();
    }

    /// <summary>The links and text of a page, if they have been read.</summary>
    internal PageInfo? InfoFor(int page)
    {
        if (!_info.TryGetValue(page, out var info)) return null;
        _infoStamp[page] = ++_infoClock;
        return info;
    }

    /// <summary>
    /// Asks for a page's links and text; <see cref="OnPageInfo"/> follows when they arrive. With
    /// <paramref name="refresh"/>, what is known is read again (and kept until the new arrives).
    /// </summary>
    internal void RequestPageInfo(int page, int priority, bool refresh = false)
    {
        var document = _document;
        if (document is null || (uint)page >= (uint)document.PageCount) return;
        if ((!refresh && _info.ContainsKey(page)) || !_infoLoading.Add(page)) return;

        var generation = _infoGeneration;
        _ = LoadInfo();

        async Task LoadInfo()
        {
            PageInfo? info = null;
            try
            {
                info = await document.LoadPageInfoAsync(page, priority).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Reading the links and text of page {page + 1} failed.", ex);
            }

            if (generation != _infoGeneration) return;
            _infoLoading.Remove(page);
            if (info is null)
            {
                if (_pendingClick?.Page == page) _pendingClick = null;
                return;
            }
            StoreInfo(info);
            OnPageInfo(page);
            if (info.MoreLinksPending is { } pending) await RereadWhenReady(pending).ConfigureAwait(true);
        }

        async Task RereadWhenReady(Task pending)
        {
            try
            {
                await pending.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Reading the document's named actions failed.", ex);
                return;
            }
            if (generation == _infoGeneration && ReferenceEquals(document, _document) && _info.ContainsKey(page))
                RequestPageInfo(page, JobPriority.PageInfo, refresh: true);
        }
    }

    private void StoreInfo(PageInfo info)
    {
        _info[info.PageIndex] = info;
        _infoStamp[info.PageIndex] = ++_infoClock;
        if (_info.Count <= PageInfoCacheSize) return;

        // Forget the longest unused page that is neither on screen nor part of the selection.
        var oldest = -1;
        var oldestStamp = long.MaxValue;
        foreach (var (page, stamp) in _infoStamp)
        {
            if (_visiblePages.Contains(page) || IsSelectionEnd(page)) continue;
            if (stamp < oldestStamp)
            {
                oldest = page;
                oldestStamp = stamp;
            }
        }
        if (oldest >= 0)
        {
            _info.Remove(oldest);
            _infoStamp.Remove(oldest);
        }
    }

    private bool IsSelectionEnd(int page) =>
        _selectionAnchor?.Page == page || _selectionFocus?.Page == page;

    private void RequestVisiblePageInfo()
    {
        if (_firstVisible < 0) return;
        for (var page = _firstVisible; page <= _lastVisible; page++) RequestPageInfo(page, JobPriority.PageInfo);
    }

    private void OnPageInfo(int page)
    {
        if (_visuals.TryGetValue(page, out var visual) && SelectionTouches(page)) DrawOverlay(visual);
        CompletePendingClick(page);
        RefreshPointer();
    }

    // ===================================================================== search highlights

    /// <summary>Removes every search highlight.</summary>
    public void ClearSearch()
    {
        if (_matchesByPage.Count == 0 && _currentMatch is null) return;
        var pages = _matchesByPage.Keys.ToArray();
        _matchesByPage.Clear();
        _currentMatch = null;
        foreach (var page in pages)
            if (_visuals.TryGetValue(page, out var visual)) DrawOverlay(visual);
    }

    /// <summary>Highlights one page's matches as the search finds them.</summary>
    public void AddSearchMatches(IReadOnlyList<SearchMatch> matches)
    {
        HashSet<int>? pages = null;
        foreach (var match in matches)
        {
            if (!_matchesByPage.TryGetValue(match.PageIndex, out var list)) _matchesByPage[match.PageIndex] = list = new List<SearchMatch>();
            list.Add(match);
            (pages ??= new HashSet<int>()).Add(match.PageIndex);
        }
        if (pages is null) return;
        foreach (var page in pages)
            if (_visuals.TryGetValue(page, out var visual)) DrawOverlay(visual);
    }

    /// <summary>Makes a match current and scrolls it into view.</summary>
    /// <summary>Marks the current match, and scrolls to it unless <paramref name="reveal"/> is false.</summary>
    public void ShowMatch(SearchMatch? match, bool reveal = true)
    {
        var previous = _currentMatch;
        _currentMatch = match;
        if (previous is not null && _visuals.TryGetValue(previous.PageIndex, out var a)) DrawOverlay(a);
        if (match is null) return;
        if (_visuals.TryGetValue(match.PageIndex, out var b)) DrawOverlay(b);
        if (reveal) BringIntoView(match.PageIndex, match.Bounds);
    }

    // ===================================================================== selection

    public bool HasSelection =>
        _selectionAnchor is { } a && _selectionFocus is { } f && a.CompareTo(f) != 0;

    private (TextPoint Start, TextPoint End)? OrderedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionFocus is not { } f || a.CompareTo(f) == 0) return null;
        return a.CompareTo(f) < 0 ? (a, f) : (f, a);
    }

    private bool SelectionTouches(int page) =>
        OrderedSelection() is { } s && page >= s.Start.Page && page <= s.End.Page;

    public void SelectAll()
    {
        if (_document is null) return;
        SetSelection(new TextPoint(0, 0), new TextPoint(_document.PageCount - 1, int.MaxValue));
    }

    public void ClearSelection()
    {
        if (_selectionAnchor is null && _selectionFocus is null) return;
        SetSelection(null, null);
    }

    private void SetSelection(TextPoint? anchor, TextPoint? focus)
    {
        var before = OrderedSelection();
        _selectionAnchor = anchor;
        _selectionFocus = focus;
        var after = OrderedSelection();
        if (before == after) return;

        // Only the pages either selection covers change.
        foreach (var visual in _visuals.Values)
        {
            var page = visual.Page;
            var was = before is { } b && page >= b.Start.Page && page <= b.End.Page;
            var now = after is { } n && page >= n.Start.Page && page <= n.End.Page;
            if (was || now) DrawOverlay(visual);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The selected text, read from the document. Empty when nothing is selected.</summary>
    public async Task<string> GetSelectedTextAsync()
    {
        if (_document is not { } document || OrderedSelection() is not { } s) return string.Empty;

        var (startPage, startChar) = (s.Start.Page, s.Start.Caret);
        var (endPage, endChar) = (s.End.Page, s.End.Caret - 1);
        if (s.End.Caret <= 0)
        {
            // Ends before the first character of a page: the page before is the last one taken.
            endPage--;
            endChar = -1;
        }
        else if (s.End.Caret == int.MaxValue)
        {
            endChar = -1;
        }
        if (endPage < startPage) return string.Empty;

        try
        {
            return await document.GetTextAsync(startPage, startChar, endPage, endChar).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Reading the selected text failed.", ex);
            return string.Empty;
        }
    }

    /// <summary>The caret nearest a point on a page: before or after the closest character.</summary>
    private static int CaretAt(PageText text, double x, double y, double aspect)
    {
        if (text.Count == 0) return 0;

        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < text.Count; i++)
        {
            var box = text.Boxes[i];
            if (box.IsEmpty) continue;
            // Distance in page units; vertical distance counts more, so the caret stays on the line pointed at.
            var dx = x < box.X0 ? box.X0 - x : x > box.X1 ? x - box.X1 : 0;
            var dy = (y < box.Y0 ? box.Y0 - y : y > box.Y1 ? y - box.Y1 : 0) * aspect * 3;
            var d = (dx * dx) + (dy * dy);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
                if (d == 0) break;
            }
        }
        if (best < 0) return 0;

        var hit = text.Boxes[best];
        return x > (hit.X0 + hit.X1) / 2 ? best + 1 : best;
    }

    /// <summary>The character whose box holds the point, or -1.</summary>
    private static int CharAt(PageText text, double x, double y, double slackX, double slackY)
    {
        for (var i = 0; i < text.Count; i++)
        {
            var box = text.Boxes[i];
            if (box.IsEmpty) continue;
            if (x >= box.X0 - slackX && x <= box.X1 + slackX && y >= box.Y0 - slackY && y <= box.Y1 + slackY) return i;
        }
        return -1;
    }

    /// <summary>The characters of a range merged into one rectangle per line.</summary>
    internal static List<PageRect> LineRects(PageText text, int from, int to)
    {
        var rects = new List<PageRect>();
        from = Math.Max(0, from);
        to = Math.Min(text.Count - 1, to);
        PageRect line = default;
        for (var i = from; i <= to; i++)
        {
            var box = text.Boxes[i];
            if (box.IsEmpty) continue;
            if (line.IsEmpty)
            {
                line = box;
                continue;
            }

            var overlap = Math.Min(line.Y1, box.Y1) - Math.Max(line.Y0, box.Y0);
            var sameLine = overlap >= 0.5 * Math.Min(line.Height, box.Height) && box.X0 >= line.X0 - line.Height;
            if (sameLine)
            {
                line = line.Union(box);
            }
            else
            {
                rects.Add(line);
                line = box;
            }
        }
        if (!line.IsEmpty) rects.Add(line);
        return rects;
    }

    private Brush SelectionBrush()
    {
        var accent = ThemeService.Current?.GetColor("AccentColor", Color.FromRgb(0x2A, 0x94, 0xFF)) ?? Color.FromRgb(0x2A, 0x94, 0xFF);
        var brush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        brush.Freeze();
        return brush;
    }

    // ===================================================================== overlay

    private void DrawOverlay(PageVisual visual)
    {
        var layout = _layout;
        using var dc = visual.Overlay.RenderOpen();
        if (layout is null || visual.Page < 0 || visual.Page >= layout.Count) return;
        var page = visual.Page;

        if (_matchesByPage.TryGetValue(page, out var matches))
        {
            foreach (var match in matches)
            {
                var current = ReferenceEquals(match, _currentMatch);
                foreach (var rect in match.Rects)
                {
                    var (x, y, w, h) = layout.ToPagePixels(page, rect);
                    var r = Inflate(x, y, w, h, Px(1));
                    dc.DrawRectangle(current ? CurrentMatchBrush : SearchBrush, current ? CurrentMatchPen : null, r);
                }
            }
        }

        if (OrderedSelection() is { } s && page >= s.Start.Page && page <= s.End.Page && InfoFor(page) is { } info)
        {
            var from = page == s.Start.Page ? s.Start.Caret : 0;
            var to = page == s.End.Page ? (s.End.Caret == int.MaxValue ? info.Text.Count - 1 : s.End.Caret - 1) : info.Text.Count - 1;
            if (to >= from)
            {
                var brush = SelectionBrush();
                foreach (var rect in LineRects(info.Text, from, to))
                {
                    var (x, y, w, h) = layout.ToPagePixels(page, rect);
                    dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
                }
            }
        }
        else if (SelectionTouches(page) && InfoFor(page) is null)
        {
            RequestPageInfo(page, JobPriority.PageInfo);
        }

        DrawFlash(dc, layout, page);
    }

    private static Rect Inflate(double x, double y, double w, double h, double by) =>
        new(x - by, y - by, w + (2 * by), h + (2 * by));
}
