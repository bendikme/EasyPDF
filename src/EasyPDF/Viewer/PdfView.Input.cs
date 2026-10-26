using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;
using Point = System.Windows.Point;

namespace EasyPDF.Viewer;

public sealed class LinkEventArgs : EventArgs
{
    public LinkEventArgs(LinkTarget? target) => Target = target;

    /// <summary>The link under the pointer, or null when it left the link.</summary>
    public LinkTarget? Target { get; }
}

public sealed partial class PdfView
{
    /// <summary>How far the pointer may move during a click before it becomes a drag.</summary>
    private const double DragThreshold = 4;

    private enum Gesture
    {
        None,
        Pressed,
        Selecting,
        Panning,
    }

    private Gesture _gesture;
    private Point _pressPoint;
    private Point _lastPointer;
    private bool _pointerInside;
    private PdfLink? _pressLink;
    private bool _pressOnText;
    private int _pressPage = -1;
    private double _panStartX;
    private double _panStartY;
    private LinkTarget? _hoverTarget;
    private DispatcherTimer? _edgeScroll;

    /// <summary>Raised when the pointer moves onto a link or off it.</summary>
    public event EventHandler<LinkEventArgs>? LinkHovered;

    /// <summary>Raised when a link that leaves this document is clicked: a URI, a file, another PDF.</summary>
    public event EventHandler<LinkEventArgs>? LinkActivated;

    private void InitializeInput()
    {
        IsManipulationEnabled = true;
        Cursor = Cursors.Arrow;
    }

    /// <summary>Where a view point (DIPs) falls in the document: page, and fractions of the page as the PDF draws it.</summary>
    private (int Page, double X, double Y, bool Inside)? DocumentPoint(Point point)
    {
        if (_layout is null || _document is null) return null;
        var (page, gx, gy) = PointAt(point.X * _dpi, point.Y * _dpi);
        var inside = gx is >= 0 and <= 1 && gy is >= 0 and <= 1;
        var (x, y) = PageLayout.Unrotate(Math.Clamp(gx, 0, 1), Math.Clamp(gy, 0, 1), _rotation);
        return (page, x, y, inside);
    }

    /// <summary>Where a point on a page (fractions, as the PDF draws the page) is in the view, in DIPs.</summary>
    internal Point ViewPointOf(int page, double x, double y)
    {
        var layout = _layout ?? throw new InvalidOperationException("No document is laid out.");
        var (gx, gy) = PageLayout.Rotate(x, y, _rotation);
        var px = layout.Left(page) + (gx * layout.Width(page)) - Math.Round(_offsetX);
        var py = layout.Top(page) + (gy * layout.Height(page)) - Math.Round(_offsetY);
        return new Point(px / _dpi, py / _dpi);
    }

    /// <summary>The scroll position in device pixels, for tests.</summary>
    internal (double X, double Y) Offset => (_offsetX, _offsetY);

    internal double Dpi => _dpi;

    internal int ViewportPixelsWidth => _viewportWidth;

    internal int ViewportPixelsHeight => _viewportHeight;

    /// <summary>A few screen pixels, in page fractions across and down: the slack for hitting a link or a letter.</summary>
    private (double X, double Y) Slack(int page, double pixels)
    {
        var layout = _layout!;
        var w = (_rotation & 1) == 0 ? layout.Width(page) : layout.Height(page);
        var h = (_rotation & 1) == 0 ? layout.Height(page) : layout.Width(page);
        return (pixels * _dpi / w, pixels * _dpi / h);
    }

    private double Aspect(int page)
    {
        var size = _document!.PageSizes[page];
        return size.Height / size.Width;
    }

    // ===================================================================== pointer

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _pointerInside = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointerInside = false;
        if (_gesture == Gesture.None) SetHover(null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMove(e.GetPosition(this));
    }

    /// <summary>The pointer moved to <paramref name="point"/> (view coordinates). The mouse and tests both come here.</summary>
    internal void PointerMove(Point point)
    {
        _lastPointer = point;

        switch (_gesture)
        {
            case Gesture.Pressed:
                if ((point - _pressPoint).Length < DragThreshold) return;
                SetHover(null);
                _pendingClick = null;
                if (_pressOnText) BeginSelecting();
                else BeginPanning();
                PointerMove(point);
                return;

            case Gesture.Selecting:
                ExtendSelection(point);
                UpdateEdgeScroll(point);
                return;

            case Gesture.Panning:
                var delta = point - _pressPoint;
                ScrollTo(_panStartX - (delta.X * _dpi), _panStartY - (delta.Y * _dpi), animate: false);
                return;

            default:
                UpdateHover(point);
                return;
        }
    }

    /// <summary>Re-evaluates what is under a resting pointer, after the page under it changed or its links arrived.</summary>
    private void RefreshPointer()
    {
        if (_gesture == Gesture.None && _pointerInside && IsMouseOver) UpdateHover(Mouse.GetPosition(this));
    }

    private void UpdateHover(Point point)
    {
        if (DocumentPoint(point) is not { Inside: true } hit)
        {
            SetHover(null);
            Cursor = Cursors.Arrow;
            return;
        }

        var info = InfoFor(hit.Page);
        if (info is null)
        {
            RequestPageInfo(hit.Page, JobPriority.Interactive);
            SetHover(null);
            Cursor = Cursors.Arrow;
            return;
        }

        var (sx, sy) = Slack(hit.Page, 1);
        if (info.LinkAt(hit.X, hit.Y, Math.Max(sx, sy)) is { } link)
        {
            SetHover(link.Target);
            Cursor = Cursors.Hand;
            return;
        }

        SetHover(null);
        var (tx, ty) = Slack(hit.Page, 2);
        Cursor = CharAt(info.Text, hit.X, hit.Y, tx, ty) >= 0 ? Cursors.IBeam : Cursors.Arrow;
    }

    private void SetHover(LinkTarget? target)
    {
        if (Equals(_hoverTarget, target)) return;
        _hoverTarget = target;
        LinkHovered?.Invoke(this, new LinkEventArgs(target));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        e.Handled = true;
        if (PointerDown(e.GetPosition(this), e.ClickCount, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) CaptureMouse();
    }

    /// <summary>The left button went down. Returns whether a gesture began that wants the pointer captured.</summary>
    internal bool PointerDown(Point point, int clickCount, bool shift)
    {
        if (_document is null) return false;
        StopAnimation();
        _pendingClick = null;

        if (clickCount >= 2)
        {
            SelectWordAt(point, line: clickCount >= 3);
            return false;
        }

        _pressPoint = point;
        _lastPointer = point;
        _pressLink = null;
        _pressOnText = false;
        _pressInfoMissing = false;
        _pressPage = -1;

        if (DocumentPoint(point) is { Inside: true } hit)
        {
            _pressPage = hit.Page;
            if (InfoFor(hit.Page) is { } info)
            {
                var (sx, sy) = Slack(hit.Page, 1);
                _pressLink = info.LinkAt(hit.X, hit.Y, Math.Max(sx, sy));
                var (tx, ty) = Slack(hit.Page, 2);
                _pressOnText = CharAt(info.Text, hit.X, hit.Y, tx, ty) >= 0;

                if (shift && _selectionAnchor is not null)
                {
                    _gesture = Gesture.Selecting;
                    ExtendSelection(point);
                    return true;
                }
            }
            else
            {
                // Clicked before the page's links were read: they are fetched now, and the click
                // is decided when they arrive.
                _pressInfoMissing = true;
                RequestPageInfo(hit.Page, JobPriority.Interactive);
            }
        }

        _gesture = Gesture.Pressed;
        return true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (PointerUp(e.GetPosition(this))) e.Handled = true;
    }

    /// <summary>The left button came up. Returns whether it completed a click.</summary>
    internal bool PointerUp(Point point)
    {
        var gesture = _gesture;
        var link = _pressLink;
        var missing = _pressInfoMissing;
        EndPointerGesture();

        var click = gesture == Gesture.Pressed;
        if (click)
        {
            if (link is not null)
            {
                Activate(link.Target, _pressPage);
            }
            else
            {
                ClearSelection();
                if (missing && DocumentPoint(_pressPoint) is { Inside: true } hit)
                {
                    if (InfoFor(hit.Page) is not null) ClickLinkAt(hit.Page, hit.X, hit.Y);
                    else _pendingClick = (hit.Page, hit.X, hit.Y);
                }
            }
        }

        UpdateHover(point);
        return click;
    }

    /// <summary>A click whose page had no links read yet, waiting for them.</summary>
    private (int Page, double X, double Y)? _pendingClick;

    private bool _pressInfoMissing;

    private void ClickLinkAt(int page, double x, double y)
    {
        if (InfoFor(page) is not { } info) return;
        var (sx, sy) = Slack(page, 1);
        if (info.LinkAt(x, y, Math.Max(sx, sy)) is { } link) Activate(link.Target, page);
    }

    private void CompletePendingClick(int page)
    {
        if (_pendingClick is not { } click || click.Page != page) return;
        _pendingClick = null;
        ClickLinkAt(click.Page, click.X, click.Y);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture != Gesture.None) EndPointerGesture();
    }

    private void EndPointerGesture()
    {
        _gesture = Gesture.None;
        _pressLink = null;
        StopEdgeScroll();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (Cursor == Cursors.SizeAll) Cursor = Cursors.Arrow;
    }

    private void BeginPanning()
    {
        _gesture = Gesture.Panning;
        _panStartX = _offsetX;
        _panStartY = _offsetY;
        Cursor = Cursors.SizeAll;
    }

    private void BeginSelecting()
    {
        _gesture = Gesture.Selecting;
        if (DocumentPoint(_pressPoint) is { } hit && InfoFor(hit.Page) is { } info)
        {
            var caret = CaretAt(info.Text, hit.X, hit.Y, Aspect(hit.Page));
            SetSelection(new TextPoint(hit.Page, caret), new TextPoint(hit.Page, caret));
        }
        Cursor = Cursors.IBeam;
    }

    private void ExtendSelection(Point point)
    {
        if (_selectionAnchor is null || DocumentPoint(point) is not { } hit) return;

        var info = InfoFor(hit.Page);
        if (info is null)
        {
            RequestPageInfo(hit.Page, JobPriority.Interactive);
            return;
        }

        // Above the page's own top (in the gap, or past the edge of the view) selects from its
        // start, below its bottom to its end - its top and bottom as the page is written, whichever
        // way the view turns it. Beside it, the nearest character.
        var (_, shownX, shownY) = PointAt(point.X * _dpi, point.Y * _dpi);
        var (_, pageY) = PageLayout.Unrotate(shownX, shownY, _rotation);
        var caret = pageY < 0 ? 0
            : pageY > 1 ? info.Text.Count
            : CaretAt(info.Text, hit.X, hit.Y, Aspect(hit.Page));
        SetSelection(_selectionAnchor, new TextPoint(hit.Page, caret));
    }

    private void SelectWordAt(Point point, bool line)
    {
        if (DocumentPoint(point) is not { Inside: true } hit || InfoFor(hit.Page) is not { } info) return;
        var (tx, ty) = Slack(hit.Page, 2);
        var index = CharAt(info.Text, hit.X, hit.Y, tx, ty);
        if (index < 0) return;

        var text = info.Text;
        int start = index, end = index;
        if (line)
        {
            var row = text.Boxes[index];
            while (start > 0 && SameRow(text.Boxes[start - 1], row)) start--;
            while (end < text.Count - 1 && SameRow(text.Boxes[end + 1], row)) end++;
        }
        else if (text.IsWordCharacter(index))
        {
            while (start > 0 && text.IsWordCharacter(start - 1)) start--;
            while (end < text.Count - 1 && text.IsWordCharacter(end + 1)) end++;
        }
        SetSelection(new TextPoint(hit.Page, start), new TextPoint(hit.Page, end + 1));

        static bool SameRow(PageRect box, PageRect row) =>
            box.IsEmpty || Math.Min(box.Y1, row.Y1) - Math.Max(box.Y0, row.Y0) >= 0.5 * Math.Min(box.Height, row.Height);
    }

    /// <summary>While selecting with the pointer past an edge, the view keeps scrolling toward it.</summary>
    private void UpdateEdgeScroll(Point point)
    {
        var outside = point.Y < 0 || point.Y > RenderSize.Height || point.X < 0 || point.X > RenderSize.Width;
        if (!outside)
        {
            StopEdgeScroll();
            return;
        }

        if (_edgeScroll is not null) return;
        _edgeScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (_gesture != Gesture.Selecting)
            {
                StopEdgeScroll();
                return;
            }
            var p = Mouse.GetPosition(this);
            double dy = p.Y < 0 ? p.Y : p.Y > RenderSize.Height ? p.Y - RenderSize.Height : 0;
            double dx = p.X < 0 ? p.X : p.X > RenderSize.Width ? p.X - RenderSize.Width : 0;
            ScrollBy(Math.Clamp(dx, -60, 60) * _dpi * 0.5, Math.Clamp(dy, -60, 60) * _dpi * 0.5, animate: false);
            ExtendSelection(p);
        }, Dispatcher);
        _edgeScroll.Start();
    }

    private void StopEdgeScroll()
    {
        _edgeScroll?.Stop();
        _edgeScroll = null;
    }

    /// <summary>Follows a link: inside the document here, anything else through <see cref="LinkActivated"/>.</summary>
    /// <param name="fromPage">The page the link is on, when it was clicked there: what the trail of jumps calls the place left.</param>
    public void Activate(LinkTarget target, int fromPage = -1)
    {
        if (target is GoToTarget go)
        {
            _ = NavigateAsync(go.Destination, flash: true, fromPage: fromPage);
            return;
        }
        if (target is NamedActionTarget named && RunNamedAction(named)) return;
        LinkActivated?.Invoke(this, new LinkEventArgs(target));
    }

    /// <summary>The named actions that move through the document. Find, print and the others are the window's.</summary>
    private bool RunNamedAction(NamedActionTarget named)
    {
        if (_document is not { } document) return true;
        var from = (uint)named.SourcePage < (uint)document.PageCount ? named.SourcePage : _currentPage;
        switch (named.Action)
        {
            // A sheet at a time, the next sheet: the next page may be beside this one already.
            case NamedAction.NextPage when _layout is { Fixed: true } sheets:
                if (sheets.SheetOf(from) + 1 < sheets.SheetCount) GoToPage(sheets.FirstOfSheet(sheets.SheetOf(from) + 1), fromPage: from);
                return true;
            case NamedAction.PrevPage when _layout is { Fixed: true } sheets:
                if (sheets.SheetOf(from) > 0) GoToPage(sheets.FirstOfSheet(sheets.SheetOf(from) - 1), fromPage: from);
                return true;
            case NamedAction.NextPage:
                if (from + 1 < document.PageCount) GoToPage(from + 1, fromPage: from);
                return true;
            case NamedAction.PrevPage:
                if (from > 0) GoToPage(from - 1, fromPage: from);
                return true;
            case NamedAction.FirstPage:
                GoToPage(0, fromPage: from);
                return true;
            case NamedAction.LastPage:
                GoToPage(document.PageCount - 1, fromPage: from);
                return true;
            case NamedAction.GoBack:
                GoBack();
                return true;
            case NamedAction.GoForward:
                GoForward();
                return true;
            default:
                return false;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        switch (e.ChangedButton)
        {
            case MouseButton.XButton1:
                GoBack();
                e.Handled = true;
                break;
            case MouseButton.XButton2:
                GoForward();
                e.Handled = true;
                break;
            case MouseButton.Middle:
                Focus();
                break;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_document is null) return;
        e.Handled = true;

        var point = e.GetPosition(this);
        LinkTarget? link = null;
        if (DocumentPoint(point) is { Inside: true } hit && InfoFor(hit.Page) is { } info)
        {
            var (sx, sy) = Slack(hit.Page, 1);
            link = info.LinkAt(hit.X, hit.Y, Math.Max(sx, sy))?.Target;
        }
        OpenContextMenu(link);
    }

    // ===================================================================== wheel

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        if (_document is null) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // A notch is 10%; a touchpad pinch sends many small deltas that add up the same way.
            var factor = Math.Pow(1.1, e.Delta / 120.0);
            SetZoom(_zoom * factor, e.GetPosition(this));
            return;
        }

        var step = WheelStep() * -e.Delta / 120.0;
        // A real wheel clicks in whole notches and glides; a touchpad sends fine deltas and follows the fingers.
        var notch = e.Delta % 120 == 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ScrollBy(step, 0, animate: notch);
        else if (_layout is { Fixed: true }) WheelOnePageAtATime(e.Delta, step, notch);
        else ScrollBy(0, step, animate: notch);
    }

    private int _edgeWheel;
    private long _lastFlip;

    /// <summary>
    /// A sheet at a time, a notch of the wheel turns a sheet - at once, however tall the sheet and
    /// however fast the wheel spins. A touchpad follows the fingers through a sheet taller than the
    /// window; at its edge, a notch's worth more turns to the next, and the pages turn no faster than
    /// one every 180 ms, so one swipe does not run through a dozen.
    /// </summary>
    private void WheelOnePageAtATime(int delta, double step, bool notch)
    {
        var direction = delta < 0 ? 1 : -1;
        if (notch && delta != 0)
        {
            _edgeWheel = 0;
            FlipPage(direction, steps: Math.Abs(delta) / 120);
            return;
        }

        if (CanScrollWithinPage(direction))
        {
            _edgeWheel = 0;
            ScrollBy(0, step, animate: notch);
            return;
        }

        if (Math.Sign(_edgeWheel) != direction) _edgeWheel = 0;
        _edgeWheel += direction * Math.Abs(delta);
        var now = Environment.TickCount64;
        if (Math.Abs(_edgeWheel) < 120 || now - _lastFlip < 180) return;
        _edgeWheel = 0;
        _lastFlip = now;
        FlipPage(direction, fromBottom: direction < 0);
    }

    /// <summary>A horizontal wheel or a sideways touchpad swipe (WM_MOUSEHWHEEL; WPF does not route it).</summary>
    public void OnHorizontalWheel(int delta)
    {
        if (_document is null) return;
        ScrollBy(WheelStep() * delta / 120.0, 0, animate: delta % 120 == 0);
    }

    private double WheelStep()
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines <= 0) return PageStep();
        return lines * 36 * _dpi;
    }

    // ===================================================================== keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>The reading keys: scrolling, paging, start and end. Returns whether the key was one.</summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_document is null || modifiers.HasFlag(ModifierKeys.Alt)) return false;

        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var wide = _layout is { } l && l.ExtentWidth > _viewportWidth;
        // Several pages at a time, every key that moves on turns the whole sheet - as many pages
        // as are shown - even when the sheet is taller or wider than the window: scrolling it a
        // little would only shift the same pages about.
        var sheets = _layout is { Fixed: true, Grid.PerSheet: > 1 };

        switch (key)
        {
            case Key.Down when shift && !ctrl:
                FastFlip(1);
                return true;
            case Key.Up when shift && !ctrl:
                FastFlip(-1);
                return true;
            case Key.Down or Key.Right or Key.PageDown when sheets && !ctrl:
                FlipPage(1);
                return true;
            case Key.Up or Key.Left or Key.PageUp when sheets && !ctrl:
                FlipPage(-1);
                return true;
            case Key.Space when sheets:
                FlipPage(shift ? -1 : 1);
                return true;
            case Key.Down:
                LineDown();
                return true;
            case Key.Up:
                LineUp();
                return true;
            case Key.Right when wide && !ctrl:
                ScrollBy(LineStep, 0, animate: true);
                return true;
            case Key.Left when wide && !ctrl:
                ScrollBy(-LineStep, 0, animate: true);
                return true;
            case Key.Right when IsFixed:
                FlipPage(1);
                return true;
            case Key.Left when IsFixed:
                FlipPage(-1);
                return true;
            case Key.Right:
                GoToPage(_currentPage + 1, record: false);
                return true;
            case Key.Left:
                GoToPage(_currentPage - 1, record: false);
                return true;
            case Key.PageDown:
                PageDown();
                return true;
            case Key.PageUp:
                PageUp();
                return true;
            case Key.Space:
                if (shift) PageUp();
                else PageDown();
                return true;
            case Key.Home:
                ScrollTo(0, 0, animate: false);
                return true;
            case Key.End:
                ScrollTo(0, double.MaxValue, animate: false);
                return true;
            case Key.Escape when HasSelection:
                ClearSelection();
                return true;
            default:
                return false;
        }
    }

    // ===================================================================== touch

    private bool _manipulationMoved;

    private double _manipulationStartY;

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.All;
        _manipulationMoved = false;
        StopAnimation();
        _manipulationStartY = _offsetY;
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        e.Handled = true;
        if (_document is null) return;

        var delta = e.DeltaManipulation;
        if (Math.Abs(delta.Scale.X - 1) > 0.001)
        {
            _manipulationMoved = true;
            SetZoom(_zoom * delta.Scale.X, e.ManipulationOrigin);
        }

        if (Math.Abs(delta.Translation.X) + Math.Abs(delta.Translation.Y) > 0)
        {
            if ((e.CumulativeManipulation.Translation).Length > DragThreshold) _manipulationMoved = true;
            ScrollBy(-delta.Translation.X * _dpi, -delta.Translation.Y * _dpi, animate: false);
        }
    }

    protected override void OnManipulationInertiaStarting(ManipulationInertiaStartingEventArgs e)
    {
        base.OnManipulationInertiaStarting(e);
        e.TranslationBehavior.DesiredDeceleration = 10.0 * 96.0 / (1000.0 * 1000.0);
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        e.Handled = true;
        if (_document is null) return;

        // A sheet at a time, a swipe up or down that the sheet could not follow turns to the next.
        if (_manipulationMoved && _layout is { Fixed: true })
        {
            var swipe = e.TotalManipulation.Translation;
            if (Math.Abs(swipe.Y) > 60 && Math.Abs(swipe.Y) > Math.Abs(swipe.X) && Math.Abs(_offsetY - _manipulationStartY) < Px(4))
                FlipPage(swipe.Y < 0 ? 1 : -1);
            return;
        }
        if (_manipulationMoved) return;

        // A tap: follow a link under it - once the page's links are read, if they are not yet.
        if (DocumentPoint(e.ManipulationOrigin) is not { Inside: true } hit) return;
        if (InfoFor(hit.Page) is { } info)
        {
            var (sx, sy) = Slack(hit.Page, 6);
            if (info.LinkAt(hit.X, hit.Y, Math.Max(sx, sy)) is { } link) Activate(link.Target, hit.Page);
        }
        else
        {
            _pendingClick = (hit.Page, hit.X, hit.Y);
            RequestPageInfo(hit.Page, JobPriority.Interactive);
        }
    }

    // ===================================================================== context menu

    private void OpenContextMenu(LinkTarget? link) => BuildContextMenu(link).IsOpen = true;

    internal ContextMenu BuildContextMenu(LinkTarget? link)
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var copy = new MenuItem { Header = Strings.View_Copy, InputGestureText = "Ctrl+C", IsEnabled = HasSelection };
        copy.Click += async (_, _) => await CopySelectionAsync().ConfigureAwait(true);
        menu.Items.Add(copy);

        if (link is ModelTarget model)
        {
            var show = new MenuItem { Header = Strings.View_ShowModel };
            show.Click += (_, _) => Activate(model);
            menu.Items.Add(show);
        }
        else if (link is not (null or NamedActionTarget) && LinkText(link) is { Length: > 0 } address)
        {
            var copyLink = new MenuItem { Header = link is GoToTarget ? Strings.View_CopyLinkPage : Strings.View_CopyLink };
            copyLink.Click += (_, _) => ClipboardText.TrySet(address);
            menu.Items.Add(copyLink);
        }

        var all = new MenuItem { Header = Strings.View_SelectAll, InputGestureText = "Ctrl+A" };
        all.Click += (_, _) => SelectAll();
        menu.Items.Add(all);

        menu.Items.Add(new Separator());

        var back = new MenuItem { Header = Strings.View_Back, InputGestureText = "Alt+Left", IsEnabled = History.CanGoBack };
        back.Click += (_, _) => GoBack();
        menu.Items.Add(back);

        var forward = new MenuItem { Header = Strings.View_Forward, InputGestureText = "Alt+Right", IsEnabled = History.CanGoForward };
        forward.Click += (_, _) => GoForward();
        menu.Items.Add(forward);

        return menu;
    }

    /// <summary>Copies the selection to the clipboard. Returns whether anything was copied.</summary>
    public async Task<bool> CopySelectionAsync()
    {
        if (!HasSelection) return false;
        var text = await GetSelectedTextAsync().ConfigureAwait(true);
        return text.Length > 0 && ClipboardText.TrySet(text);
    }

    /// <summary>A link's target as text: the address, the file, or the page it goes to.</summary>
    public static string LinkText(LinkTarget target) => target switch
    {
        UriTarget u => u.Uri,
        LaunchTarget l => l.Path,
        RemoteGoToTarget r => r.Path,
        GoToTarget g => UiLanguage.Format(Strings.View_LinkPage, g.Destination.PageIndex + 1),
        ModelTarget => Strings.View_LinkModel,
        NamedActionTarget n => n.Action switch
        {
            NamedAction.NextPage => Strings.View_Action_NextPage,
            NamedAction.PrevPage => Strings.View_Action_PrevPage,
            NamedAction.FirstPage => Strings.View_Action_FirstPage,
            NamedAction.LastPage => Strings.View_Action_LastPage,
            NamedAction.GoBack => Strings.View_Back,
            NamedAction.GoForward => Strings.View_Forward,
            NamedAction.GoToPage => Strings.View_Action_GoToPage,
            NamedAction.Find => Strings.View_Action_Find,
            NamedAction.Print => Strings.Main_Print,
            NamedAction.FullScreen => Strings.More_FullScreen,
            _ => string.Empty,
        },
        _ => string.Empty,
    };
}
