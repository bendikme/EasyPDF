using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPDF.Pdf;
using EasyPDF.Services;
using EasyPDF.Viewer;
using EasyPDF.Views;
using static Program;
using Point = System.Windows.Point;

/// <summary>The window and its page view, driven the way a user drives them.</summary>
internal static class ViewerChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("A document opens, renders and fills in the header", OpensAndRenders),
        ("Hovering a link shows where it goes", HoverShowsTarget),
        ("Internal links jump to their place; Back and Forward return", InternalLinks),
        ("Web links open; script and program links are refused", ExternalLinks),
        ("Links to other PDFs open them at the named place", LinksToOtherPdfs),
        ("A jump into a file that is still opening waits for it", JumpWaitsForOpening),
        ("Buttons that go somewhere are followed when clicked", ButtonClicks),
        ("Named-action links move through the document; the others run window commands", NamedActionLinks),
        ("A click made before the links are read is still followed", EarlyClick),
        ("Quadrilateral links are clickable only on their words", QuadClicks),
        ("Links work on rotated pages, in a rotated view and zoomed in", LinksEverywhere),
        ("Outline entries go to their place", OutlineNavigation),
        ("Dragging selects text; double-click selects a word", Selection),
        ("Dragging past a turned page selects to its end as written", SelectionRotated),
        ("A jump to a narrow page after scrolling right on a wide one shows it", JumpAfterWideScroll),
        ("Search finds every match and steps through them", SearchInWindow),
        ("Zooming keeps the point under the pointer still", ZoomAnchor),
        ("Pages and highlights are redrawn for a new zoom at once", RedrawnForNewLayout),
        ("Fit to width fills the window without a horizontal scroll bar", FitWidth),
        ("Keyboard and the page box move through the document", Keyboard),
        ("The last page of 2000 renders, and the place is remembered", LongDocument),
        ("Tile memory stays within its budget while scrolling", MemoryBudget),
        ("A changed file is reloaded at the same place", ReloadOnChange),
        ("A protected document asks for its password", PasswordFlow),
        ("A file that cannot be opened says why", OpenFailure),
        ("The same file opens once, in one window", OpenOnce),
        ("Sidebar thumbnails render", Thumbnails),
        ("The header folds into the more menu as the window narrows", HeaderFolds),
        ("The header's controls and text are centred in the strip in sight", HeaderCentred),
        ("Printing draws text and form fields", PrintsToDeviceContext),
        ("Every dialog and menu builds, in both languages", DialogsAndMenus),
        ("Full screen hides the chrome and gives the window back", FullScreen),
        ("Closed windows and documents are released", WindowsAreReleased),
    ];

    // ===================================================================== helpers

    private static MainWindow ShowWindow(string? file = null, double width = 1100, double height = 820)
    {
        var window = App.OpenEmptyWindow(null);
        window.Width = width;
        window.Height = height;
        Pump(30);
        if (file is not null)
        {
            var path = Path.IsPathFullyQualified(file) ? file : Fixture(file);
            Check(Await(window.OpenAsync(path)), $"{file} opened");
            WaitForScreen(window);
        }
        return window;
    }

    private static void WaitForScreen(MainWindow window)
    {
        try
        {
            PumpUntil(() => window.PageView.IsScreenComplete() && !window.PageView.Tiles.IsBusy, "the screen to render", 20000);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} {window.PageView.DescribeScreen()}");
        }
    }

    private static PageInfo Info(MainWindow window, int page)
    {
        window.PageView.RequestPageInfo(page, JobPriority.Interactive);
        PumpUntil(() => window.PageView.InfoFor(page) is { MoreLinksPending: null }, $"page {page + 1}'s links");
        return window.PageView.InfoFor(page)!;
    }

    /// <summary>The view point at the middle of a PyMuPDF-style rectangle on a page.</summary>
    private static Point At(MainWindow window, int page, double x0, double y0, double x1, double y1, double w = 612, double h = 792) =>
        window.PageView.ViewPointOf(page, (x0 + x1) / 2 / w, (y0 + y1) / 2 / h);

    /// <summary>
    /// A point that should have been brought to the top of the view is there - unless the view
    /// could not scroll that far because the document ends first.
    /// </summary>
    private static void AtTop(PdfView view, Point point, string what)
    {
        var layout = view.Layout!;
        var atEnd = view.Offset.Y >= layout.ExtentHeight - view.ViewportPixelsHeight - 1;
        Check(point.Y is > -1 and < 40 || (atEnd && point.Y > 0 && point.Y < view.ViewportPixelsHeight / view.Dpi), $"{what}: the place is at {point.Y} (at the end: {atEnd})");
    }

    private static void Click(MainWindow window, Point point)
    {
        var view = window.PageView;
        view.PointerMove(point);
        view.PointerDown(point, 1, shift: false);
        view.PointerUp(point);
        Pump(10);
    }

    private static List<string> CaptureShell()
    {
        var opened = new List<string>();
        MainWindow.ShellOpen = target =>
        {
            opened.Add(target);
            return true;
        };
        return opened;
    }

    private static List<string> CaptureNotices()
    {
        var notices = new List<string>();
        MainWindow.NoticeSink = (title, message) => notices.Add($"{title}: {message}");
        return notices;
    }

    /// <summary>
    /// Dark pixels in the tile images the visible pages draw, counting only the parts inside the
    /// viewport. (Nothing on screen can be captured in a disconnected session, so the check reads
    /// what WPF is told to draw - the images and where they go - rather than the screen.)
    /// </summary>
    private static int InkOnScreen(MainWindow window)
    {
        var view = window.PageView;
        var viewport = new Rect(0, 0, view.ViewportPixelsWidth, view.ViewportPixelsHeight);
        var ink = 0;
        foreach (var (_, offset, drawing) in view.PageDrawings())
        {
            if (drawing is null) continue;
            foreach (var image in Images(drawing))
            {
                var placed = image.Rect;
                placed.Offset(offset);
                if (!placed.IntersectsWith(viewport) || image.ImageSource is not BitmapSource bitmap) continue;
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] + pixels[i + 1] + pixels[i + 2] < 300) ink++;
            }
        }
        return ink;
    }

    private static IEnumerable<ImageDrawing> Images(Drawing drawing)
    {
        if (drawing is ImageDrawing image) yield return image;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
            foreach (var found in Images(child))
                yield return found;
    }

    // ===================================================================== checks

    private static void OpensAndRenders()
    {
        var window = ShowWindow("links.pdf");
        var view = window.PageView;
        Equal(view.PageCount, 3, "pages");
        Equal(view.CurrentPage, 0, "current page");
        Check(window.Title.StartsWith("links.pdf", StringComparison.Ordinal), $"title: {window.Title}");
        Equal(window.BrandTitle.Text, "links.pdf", "brand shows the file");
        Equal(window.PageBox.Text, "1", "page box");
        Equal(window.PageCountText.Text, "of 3", "page count");
        Check(window.StartPage.Visibility != Visibility.Visible, "start page hidden");
        Check(view.Tiles.CachedCount > 0, "tiles rendered");

        var ink = InkOnScreen(window);
        Check(ink > 500, $"the page shows text: {ink} dark pixels");

        // Every tile of the first page sits on the 512-pixel grid of the page, at whole pixels.
        var (_, offset, drawing) = view.PageDrawings().Single(d => d.Page == 0);
        Check(offset.X == Math.Round(offset.X) && offset.Y == Math.Round(offset.Y), $"page at whole pixels: {offset}");
        foreach (var image in Images(drawing!))
        {
            var r = image.Rect;
            Check(r.X % 512 == 0 && r.Y % 512 == 0 && r.Width <= 512 && r.Height <= 512, $"tile rectangle {r}");
            var bitmap = (BitmapSource)image.ImageSource;
            Check(bitmap.PixelWidth == (int)r.Width && bitmap.PixelHeight == (int)r.Height, $"tile drawn 1:1: {bitmap.PixelWidth}x{bitmap.PixelHeight} in {r}");
        }
    }

    private static void HoverShowsTarget()
    {
        var window = ShowWindow("links.pdf");
        Info(window, 0);
        var view = window.PageView;
        LinkTarget? hovered = null;
        view.LinkHovered += (_, e) => hovered = e.Target;

        view.PointerMove(At(window, 0, 70, 106, 200, 124));
        Equal(hovered, new UriTarget("https://example.com/docs?a=1&b=2"), "hovered link");
        Equal(view.Cursor, Cursors.Hand, "hand over a link");
        Check(window.StatusPill.Visibility == Visibility.Visible && window.StatusText.Text == "https://example.com/docs?a=1&b=2", "status shows the address");

        view.PointerMove(At(window, 0, 70, 166, 200, 184));
        Equal(window.StatusText.Text, "Page 3", "status shows the page of an internal link");

        view.PointerMove(At(window, 0, 400, 600, 500, 700));
        Equal(hovered, null, "off the link");
        Check(window.StatusPill.Visibility != Visibility.Visible, "status hidden");
        Equal(view.Cursor, Cursors.Arrow, "arrow on empty paper");

        view.PointerMove(At(window, 0, 72, 55, 120, 72));
        Equal(view.Cursor, Cursors.IBeam, "I-beam over text");
    }

    private static void InternalLinks()
    {
        var window = ShowWindow("links.pdf");
        Info(window, 0);
        var view = window.PageView;

        Click(window, At(window, 0, 70, 166, 200, 184));
        PumpUntil(() => view.CurrentPage == 2, "the jump to page 3");
        // XYZ top=500 is 292pt down page 3: that point is now near the top of the view.
        AtTop(view, view.ViewPointOf(2, 72 / 612.0, 292 / 792.0), "the XYZ destination");
        Check(view.History.CanGoBack, "Back is possible");

        view.GoBack();
        Pump(10);
        Equal(view.CurrentPage, 0, "Back returns to page 1");
        Near(view.Offset.Y, 0, 1, "Back returns to the very top");
        view.GoForward();
        Pump(10);
        Equal(view.CurrentPage, 2, "Forward goes to page 3 again");
        view.GoBack();
        Pump(10);

        Click(window, At(window, 0, 70, 196, 220, 214));
        PumpUntil(() => view.CurrentPage == 1, "the named destination");

        Info(window, 1);
        Click(window, At(window, 1, 70, 106, 220, 124));
        PumpUntil(() => view.CurrentPage == 0, "the link back to page 1");
    }

    private static void NamedActionLinks()
    {
        var window = ShowWindow("links-objstm.pdf");
        var view = window.PageView;
        view.GoToPage(1, record: false);
        Pump(10);
        Info(window, 1);

        view.PointerMove(At(window, 1, 70, 586, 240, 604));
        Equal(window.StatusText.Text, "Next page", "hovering names the action");
        Equal(view.Cursor, Cursors.Hand, "hand over a named-action link");
        Equal(view.BuildContextMenu(new NamedActionTarget(NamedAction.NextPage, 1)).Items.OfType<System.Windows.Controls.MenuItem>().Count(), 4, "no 'copy link' for a command");

        Click(window, At(window, 1, 70, 586, 240, 604));
        PumpUntil(() => view.CurrentPage == 2, "next page");
        Info(window, 2);

        Click(window, At(window, 2, 70, 586, 240, 604));
        PumpUntil(() => view.CurrentPage == 0, "first page");
        Near(view.Offset.Y, 0, 1, "the very top");
        view.GoBack();
        PumpUntil(() => view.CurrentPage == 2, "Back from the first page");

        Click(window, At(window, 2, 70, 616, 240, 634));
        PumpUntil(() => view.CurrentPage == 1, "previous page");
        Click(window, At(window, 1, 70, 616, 240, 634));
        PumpUntil(() => view.CurrentPage == 2, "last page");
        Click(window, At(window, 2, 70, 646, 240, 664));
        PumpUntil(() => view.CurrentPage == 1, "the document's own Back button");

        // Commands that are the window's.
        window.Follow(new NamedActionTarget(NamedAction.Find, 1));
        PumpUntil(() => window.SearchBox.IsKeyboardFocusWithin, "Find focuses the search box");
        window.Follow(new NamedActionTarget(NamedAction.FullScreen, 1));
        PumpUntil(() => window.IsFullScreen, "full screen");
        window.Follow(new NamedActionTarget(NamedAction.FullScreen, 1));
        PumpUntil(() => !window.IsFullScreen, "and back");
    }

    private static void ExternalLinks()
    {
        var window = ShowWindow("links.pdf");
        Info(window, 0);
        var shell = CaptureShell();
        var notices = CaptureNotices();

        Click(window, At(window, 0, 70, 106, 200, 124));
        Click(window, At(window, 0, 70, 226, 200, 244));
        Equal(string.Join(" ", shell), "https://example.com/docs?a=1&b=2 mailto:support@example.com", "web and mail opened");

        shell.Clear();
        Click(window, At(window, 0, 70, 376, 240, 394));
        Click(window, At(window, 0, 70, 406, 240, 424));
        Click(window, At(window, 0, 70, 346, 240, 364));
        Equal(shell.Count, 0, "nothing reached the shell");
        Equal(notices.Count, 2, "the script address and the program were refused with a message");
        Check(notices.All(n => n.StartsWith("This link was not opened", StringComparison.Ordinal)), string.Join("; ", notices));

        // The address found in the text.
        var web = Info(window, 0).Links.Single(l => l.FromText);
        Click(window, window.PageView.ViewPointOf(0, (web.Bounds.X0 + web.Bounds.X1) / 2, (web.Bounds.Y0 + web.Bounds.Y1) / 2));
        Check(shell.Count == 1 && shell[0].StartsWith("https://example.org/plain", StringComparison.Ordinal), $"web link from text: {string.Join(" ", shell)}");
    }

    private static void LinksToOtherPdfs()
    {
        var window = ShowWindow("links.pdf");
        Info(window, 0);
        var other = Fixture("other.pdf");

        MainWindow? OtherWindow() => App.DocumentWindows.FirstOrDefault(w => w.Document?.FilePath == other);

        Click(window, At(window, 0, 70, 316, 240, 334));
        PumpUntil(() => OtherWindow() is { } w && w.PageView.CurrentPage == 1, "other.pdf at its named destination", 20000);
        var second = OtherWindow()!;
        WaitForScreen(second);
        AtTop(second.PageView, second.PageView.ViewPointOf(1, 72 / 612.0, 372 / 792.0), "the named destination in the other file");

        // The other file is open already: its window is used, now at page 2's top.
        second.PageView.GoToPage(0);
        Click(window, At(window, 0, 70, 286, 220, 304));
        PumpUntil(() => second.PageView.CurrentPage == 1, "other.pdf page 2");
        Equal(App.DocumentWindows.Count(w => w.Document?.FilePath == other), 1, "still one window for other.pdf");

        // The launch action and the relative URI both open (bring forward) the same file.
        Click(window, At(window, 0, 70, 256, 220, 274));
        Click(window, At(window, 0, 70, 496, 240, 514));
        Pump(50);
        Equal(App.DocumentWindows.Count(w => w.Document?.FilePath == other), 1, "launch and relative links reuse it");
        Equal(App.DocumentWindows.Count(), 2, "two windows in all");

        // A URI with a fragment: the page it names, then the named place.
        second.PageView.GoToPage(0);
        Pump(10);
        Click(window, At(window, 0, 70, 526, 240, 544));
        PumpUntil(() => second.PageView.CurrentPage == 1, "other.pdf#page=2");
        second.PageView.GoToPage(0);
        Pump(10);
        Click(window, At(window, 0, 70, 556, 240, 574));
        PumpUntil(() => second.PageView.CurrentPage == 1, "other.pdf#nameddest=intro");
        AtTop(second.PageView, second.PageView.ViewPointOf(1, 72 / 612.0, 372 / 792.0), "the named place, by URI");
    }

    private static void ButtonClicks()
    {
        var shell = CaptureShell();
        var window = ShowWindow("buttons.pdf");
        var view = window.PageView;
        Info(window, 0);

        view.PointerMove(At(window, 0, 70, 100, 200, 120));
        Equal(window.StatusText.Text, "Page 3", "hovering a button shows where it goes");
        Equal(view.Cursor, Cursors.Hand, "a hand over it");

        Click(window, At(window, 0, 70, 100, 200, 120));
        PumpUntil(() => view.CurrentPage == 2, "the button's jump");
        view.GoBack();
        PumpUntil(() => view.CurrentPage == 0, "Back from it");

        Click(window, At(window, 0, 70, 140, 200, 160));
        Equal(shell.SingleOrDefault(), "https://example.com/button", "a button's web address opens");

        // A button into another PDF opens it at the page the button names.
        var other = Fixture("other.pdf");
        Click(window, At(window, 0, 70, 180, 200, 200));
        PumpUntil(() => App.DocumentWindows.FirstOrDefault(w => w.Document?.FilePath == other) is { } w && w.PageView.CurrentPage == 1, "other.pdf at page 2", 20000);

        // And at a place whose name is not ASCII.
        var second = App.DocumentWindows.First(w => w.Document?.FilePath == other);
        second.PageView.GoToPage(0);
        Pump(10);
        Click(window, At(window, 0, 70, 540, 200, 560));
        PumpUntil(() => second.PageView.CurrentPage == 1, "other.pdf at slutt-\u00f8");
    }

    /// <summary>A jump into a file still opening in another window waits for it; a reload of the source does not lose it.</summary>
    private static void JumpWaitsForOpening()
    {
        var window = ShowWindow("links.pdf");
        Info(window, 0);
        var other = Fixture("other.pdf");

        // The engine is held up, so other.pdf is still opening when the second link is clicked.
        using var gate = new ManualResetEventSlim(false);
        var blocker = EasyPDF.Pdf.PdfEngine.Instance.Run(JobPriority.Interactive, () => gate.Wait(10000));
        Click(window, At(window, 0, 70, 256, 220, 274));   // launch: opens other.pdf, no place
        Click(window, At(window, 0, 70, 316, 240, 334));   // remote named: other.pdf at "intro"
        Check(App.DocumentWindows.Count(w => w.Shows(other)) == 1, "one window opening other.pdf");
        gate.Set();
        Await(blocker);
        PumpUntil(() => App.DocumentWindows.FirstOrDefault(w => w.Document?.FilePath == other) is { } w && w.PageView.CurrentPage == 1,
            "the jump waited for the file to open", 20000);
    }

    private static void EarlyClick()
    {
        var shell = CaptureShell();
        var window = ShowWindow("links.pdf");
        var point = At(window, 0, 70, 106, 200, 124);

        // The engine is held up (as by a heavy page) and the page's links are not read yet.
        using var gate = new ManualResetEventSlim(false);
        var busy = PdfEngine.Instance.Run(JobPriority.Interactive, () => gate.Wait(10000));
        window.PageView.ForgetPageInfo();
        window.PageView.PointerDown(point, 1, false);
        window.PageView.PointerUp(point);
        Pump(50);
        Equal(shell.Count, 0, "nothing is followed before the links are known");

        gate.Set();
        Await(busy);
        PumpUntil(() => shell.Count == 1, "the early click to be followed");
        Equal(shell[0], "https://example.com/docs?a=1&b=2", "the early click's link");

        // A press that turns into a drag before the links arrive follows nothing.
        shell.Clear();
        gate.Reset();
        busy = PdfEngine.Instance.Run(JobPriority.Interactive, () => gate.Wait(10000));
        window.PageView.ForgetPageInfo();
        window.PageView.PointerDown(point, 1, false);
        window.PageView.PointerMove(new Point(point.X + 30, point.Y + 30));
        window.PageView.PointerUp(new Point(point.X + 30, point.Y + 30));
        gate.Set();
        Await(busy);
        Pump(100);
        Equal(shell.Count, 0, "a drag is not a click");
    }

    private static void QuadClicks()
    {
        var window = ShowWindow("links.pdf");
        Info(window, 0);
        var shell = CaptureShell();
        Click(window, window.PageView.ViewPointOf(0, 250 / 612.0, 446 / 792.0));
        Click(window, window.PageView.ViewPointOf(0, 100 / 612.0, 461 / 792.0));
        Equal(shell.Count, 0, "beside and between the quads");
        Click(window, window.PageView.ViewPointOf(0, 100 / 612.0, 446 / 792.0));
        Click(window, window.PageView.ViewPointOf(0, 100 / 612.0, 476 / 792.0));
        Equal(string.Join(" ", shell), "https://example.com/quad https://example.com/quad", "on each quad");
    }

    private static void LinksEverywhere()
    {
        var shell = CaptureShell();

        // A page with /Rotate 90.
        var mixed = ShowWindow("mixed.pdf");
        mixed.PageView.GoToPage(2);
        WaitForScreen(mixed);
        var link = Info(mixed, 2).Links.Single(l => l.Target is UriTarget { Uri: "https://example.com/rotated" });
        var centre = ((link.Bounds.X0 + link.Bounds.X1) / 2, (link.Bounds.Y0 + link.Bounds.Y1) / 2);
        Click(mixed, mixed.PageView.ViewPointOf(2, centre.Item1, centre.Item2));
        Equal(shell.LastOrDefault(), "https://example.com/rotated", "link on a /Rotate page");

        // The same page with the view turned, each way.
        foreach (var turns in new[] { 1, 1, 1 })
        {
            shell.Clear();
            mixed.PageView.Rotate(turns);
            Pump(120);
            mixed.PageView.BringIntoView(2, link.Bounds);
            WaitForScreen(mixed);
            Click(mixed, mixed.PageView.ViewPointOf(2, centre.Item1, centre.Item2));
            Equal(shell.LastOrDefault(), "https://example.com/rotated", $"link in a view turned {mixed.PageView.Rotation} quarter(s)");
        }

        // Zoomed far in, scrolled across to the link.
        var links = ShowWindow("links.pdf");
        Info(links, 0);
        links.PageView.SetZoom(6);
        Pump(120);
        var uri = new PageRect(70 / 612f, 106 / 792f, 200 / 612f, 124 / 792f);
        links.PageView.BringIntoView(0, uri);
        WaitForScreen(links);
        shell.Clear();
        Click(links, At(links, 0, 70, 106, 200, 124));
        Equal(shell.SingleOrDefault(), "https://example.com/docs?a=1&b=2", "link at 600%");

        links.PageView.SetZoom(0.25);
        Pump(120);
        WaitForScreen(links);
        shell.Clear();
        Click(links, At(links, 0, 70, 106, 200, 124));
        Equal(shell.SingleOrDefault(), "https://example.com/docs?a=1&b=2", "link at 25%");
    }

    private static void OutlineNavigation()
    {
        var window = ShowWindow("links.pdf");
        window.SetSidebar(true, remember: false);
        PumpUntil(() => window.OutlineTree.ItemsSource is System.Windows.Data.ListCollectionView { SourceCollection: OutlineNode[] { Length: 5 } }, "the outline");
        var nodes = (OutlineNode[])((System.Windows.Data.ListCollectionView)window.OutlineTree.ItemsSource).SourceCollection;
        Equal(string.Join("|", nodes.Select(n => $"{n.Title}:{n.PageLabel}")), "Introduction:1|Chapter 2:2|Chapter 3:3|Website:|Other intro:", "outline entries");
        Check(window.OutlineTree.Visibility == Visibility.Visible, "the outline shows for a document that has one");

        window.FollowOutline(nodes[2]);
        PumpUntil(() => window.PageView.CurrentPage == 2, "Chapter 3");
        window.FollowOutline(nodes[1].Children[0]);
        PumpUntil(() => window.PageView.CurrentPage == 1, "Section 2.1");

        var shell = CaptureShell();
        window.FollowOutline(nodes[3]);
        Equal(shell.SingleOrDefault(), "https://example.com/toc", "a web entry opens its address");

        // An entry into another PDF opens it at the place it names.
        var other = Fixture("other.pdf");
        window.FollowOutline(nodes[4]);
        PumpUntil(() => App.DocumentWindows.FirstOrDefault(w => w.Document?.FilePath == other) is { } w && w.PageView.CurrentPage == 1, "other.pdf at the entry's place", 20000);
    }

    private static string Selected(MainWindow window) => Await(window.PageView.GetSelectedTextAsync());

    private static void Selection()
    {
        var window = ShowWindow("search.pdf");
        var view = window.PageView;
        var text = Info(window, 0).Text;
        var all = text.GetText(0, text.Count - 1);

        Point CharPoint(int index, bool right)
        {
            var box = text.Boxes[index];
            return view.ViewPointOf(0, right ? box.X1 - (box.Width * 0.2) : box.X0 + (box.Width * 0.2), (box.Y0 + box.Y1) / 2);
        }

        var start = all.IndexOf("alpha", StringComparison.Ordinal);
        var end = all.IndexOf("gamma", StringComparison.Ordinal) + "gamma".Length - 1;
        view.PointerMove(CharPoint(start, false));
        view.PointerDown(CharPoint(start, false), 1, false);
        view.PointerMove(CharPoint(start + 3, true));
        view.PointerMove(CharPoint(end, true));
        view.PointerUp(CharPoint(end, true));
        Check(view.HasSelection, "dragging selected");
        Equal(Selected(window), "alpha beta Alpha gamma", "selected text");

        var beta = all.IndexOf("beta", StringComparison.Ordinal);
        view.PointerDown(CharPoint(beta + 1, false), 2, false);
        view.PointerUp(CharPoint(beta + 1, false));
        Equal(Selected(window), "beta", "double-click selects the word");

        view.PointerDown(CharPoint(beta + 1, false), 1, false);
        view.PointerUp(CharPoint(beta + 1, false));
        Check(!view.HasSelection, "a click clears the selection");

        view.SelectAll();
        var everything = Selected(window);
        Check(everything.Contains("alphabet", StringComparison.Ordinal) && everything.Contains("page two alpha", StringComparison.Ordinal), $"select all: {everything}");

        // Dragging from the first page down onto the second.
        view.ClearSelection();
        view.PointerDown(CharPoint(start, false), 1, false);
        var info2 = Info(window, 1);
        var last = info2.Text.Boxes.Select((b, i) => (b, i)).Last(p => !p.b.IsEmpty);
        var onTwo = view.ViewPointOf(1, last.b.X1, (last.b.Y0 + last.b.Y1) / 2);
        view.PointerMove(onTwo);
        view.PointerUp(onTwo);
        var across = Selected(window);
        Check(across.StartsWith("alpha beta", StringComparison.Ordinal) && across.TrimEnd().EndsWith("page two alpha", StringComparison.Ordinal), $"across pages: {across}");
    }

    private static void SearchInWindow()
    {
        var window = ShowWindow("search.pdf");
        window.SearchBox.Text = "alpha";
        PumpUntil(() => window.Search is { IsComplete: true }, "the search");
        Pump(20);
        Equal(window.SearchStatus.Text, "1 of 6", "status after searching");
        Equal(window.Search!.CurrentMatch!.PageIndex, 0, "first match");

        for (var i = 0; i < 5; i++) window.MoveMatch(+1);
        Equal(window.SearchStatus.Text, "6 of 6", "the last match");
        Equal(window.PageView.CurrentPage, 1, "the last match is on page 2");
        window.MoveMatch(+1);
        Equal(window.SearchStatus.Text, "1 of 6", "wraps to the first");
        window.MoveMatch(-1);
        Equal(window.SearchStatus.Text, "6 of 6", "and back");

        // A new search starts at the reader's page: from page 2 its first match is the last of three.
        window.SetSearchOptions(matchCase: true, wholeWord: true);
        PumpUntil(() => window.Search is { IsComplete: true }, "the narrower search");
        Equal(window.SearchStatus.Text, "3 of 3", "match case and whole words, from page 2");
        window.PageView.GoToPage(0);
        window.StartSearch(immediate: true);
        PumpUntil(() => window.Search is { IsComplete: true }, "the search from page 1");
        Equal(window.SearchStatus.Text, "1 of 3", "from page 1");

        window.SearchBox.Text = "nothing like this";
        PumpUntil(() => window.Search is { IsComplete: true, Query: "nothing like this" }, "a search with no matches", 5000);
        Equal(window.SearchStatus.Text, "No matches", "no matches");

        window.SearchBox.Text = string.Empty;
        Pump(20);
        Check(window.Search is null && window.SearchTools.Visibility != Visibility.Visible, "clearing the box ends the search");
    }

    private static void ZoomAnchor()
    {
        var window = ShowWindow(BigPdf);
        var view = window.PageView;
        view.GoToPage(10);
        WaitForScreen(window);

        foreach (var (factor, anchor) in new[] { (2.0, new Point(300, 200)), (1.5, new Point(700, 300)), (0.5, new Point(120, 500)), (3.0, new Point(600, 100)) })
        {
            var before = DocPoint(view, anchor);
            var wideBefore = view.Layout!.ExtentWidth > view.ViewportPixelsWidth;
            view.SetZoom(view.Zoom * factor, anchor);
            var after = DocPoint(view, anchor);
            var wideAfter = view.Layout!.ExtentWidth > view.ViewportPixelsWidth;
            Equal(after.Page, before.Page, $"same page under the pointer after zooming {factor}x");
            var tolerance = 1.5 / Math.Min(view.Layout.Width(after.Page), view.Layout.Height(after.Page));
            // Across, the point can only stay put while the pages are wider than the view;
            // narrower pages are centred instead.
            if (wideBefore && wideAfter) Near(after.X, before.X, tolerance, $"x under the pointer after {factor}x");
            Near(after.Y, before.Y, tolerance, $"y under the pointer after {factor}x");
        }
        WaitForScreen(window);

        static (int Page, double X, double Y) DocPoint(PdfView view, Point p)
        {
            var layout = view.Layout!;
            var x = Math.Round(view.Offset.X) + (p.X * view.Dpi);
            var y = Math.Round(view.Offset.Y) + (p.Y * view.Dpi);
            var page = layout.PageAt(y);
            return (page, (x - layout.Left(page)) / layout.Width(page), (y - layout.Top(page)) / layout.Height(page));
        }
    }

    private static void RedrawnForNewLayout()
    {
        var window = ShowWindow("links.pdf");
        window.SearchBox.Text = "page";
        PumpUntil(() => window.Search is { IsComplete: true, Matches.Count: > 0 }, "the search");
        var view = window.PageView;
        var match = window.Search!.Matches[0];

        foreach (var change in new Action[] { () => view.SetZoom(0.5), () => view.Rotate(1), () => view.SetZoom(1.7), () => window.Width = 700 })
        {
            change();
            Pump(); // no waiting for tiles: what is drawn must already fit the new layout
            var layout = view.Layout!;
            foreach (var (page, drawnFor, overlay) in view.PageOverlays())
            {
                Check(ReferenceEquals(drawnFor, layout), $"page {page + 1} still drawn for an old layout");
                if (page != match.PageIndex) continue;
                Check(overlay is not null, "the match's page has highlights");
                var (x, y, w, h) = layout.ToPagePixels(page, match.Rects[0]);
                var bounds = overlay!.Bounds;
                Check(bounds.IntersectsWith(new Rect(x, y, w, h)), $"the highlight {bounds} is not over the match at {new Rect(x, y, w, h)}");
                Check(bounds.Width < layout.Width(page) / 2.0, $"highlights fit the page: {bounds}");
            }
        }
    }

    private static void FitWidth()
    {
        foreach (var width in new[] { 700.0, 1100, 1600 })
        {
            var window = ShowWindow("links.pdf", width);
            var view = window.PageView;
            view.SetMode(ZoomMode.FitWidth);
            Pump(120);
            var layout = view.Layout!;
            Check(layout.ExtentWidth == view.ViewportPixelsWidth, $"no horizontal scrolling at {width}: {layout.ExtentWidth} vs {view.ViewportPixelsWidth}");
            Check(layout.Width(0) + (2 * layout.Margin) >= view.ViewportPixelsWidth - 2, $"the page fills the width at {width}");
            Equal(window.FitButton.Tag, "on", "fit button shows the mode");

            view.SetMode(ZoomMode.FitPage);
            Pump(120);
            layout = view.Layout!;
            Check(layout.Height(0) + (2 * layout.Margin) <= view.ViewportPixelsHeight, $"a whole page fits at {width}");
            window.Close();
        }
    }

    private static void Keyboard()
    {
        var window = ShowWindow(BigPdf);
        var view = window.PageView;
        var top = view.Offset.Y;
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        PumpUntil(() => view.Offset.Y > top + (view.ViewportPixelsHeight / 2), "page down");
        view.HandleKey(Key.End, ModifierKeys.None);
        Pump(10);
        Equal(view.CurrentPage, 1999, "End goes to the last page");
        view.HandleKey(Key.Home, ModifierKeys.None);
        Pump(10);
        Equal(view.CurrentPage, 0, "Home goes to the first");
        view.HandleKey(Key.Right, ModifierKeys.None);
        Pump(10);
        Equal(view.CurrentPage, 1, "Right goes to the next page");

        window.FocusPageBox();
        window.PageBox.Text = "1234";
        window.PageBox.RaiseEvent(new KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window.PageBox)!, 0, Key.Enter)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
        });
        Pump(10);
        Equal(view.CurrentPage, 1233, "the page box goes to page 1234");
        Check(view.History.CanGoBack, "going to a page can be undone");

        window.PageBox.Text = "999999";
        window.PageBox.RaiseEvent(new KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window.PageBox)!, 0, Key.Enter)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
        });
        Pump(10);
        Equal(view.CurrentPage, 1999, "a page past the end goes to the last");
    }

    private static void LongDocument()
    {
        var window = ShowWindow(BigPdf);
        var view = window.PageView;
        var sw = Stopwatch.StartNew();
        view.GoToPage(1999);
        WaitForScreen(window);
        Console.WriteLine($"     jump to page 2000 and render: {sw.ElapsedMilliseconds} ms");
        Equal(view.CurrentPage, 1999, "on the last page");
        Equal(window.PageBox.Text, "2000", "page box");

        view.GoToPage(1233);
        WaitForScreen(window);
        window.Close();
        Pump(20);

        var again = ShowWindow(BigPdf);
        Equal(again.PageView.CurrentPage, 1233, "reopened where it was left");
    }

    private static void MemoryBudget()
    {
        var window = ShowWindow(BigPdf, 1400, 1000);
        var view = window.PageView;
        view.SetZoom(2);
        Pump(120);
        WaitForScreen(window);

        long peak = 0;
        var screenBytes = (long)view.ViewportPixelsWidth * view.ViewportPixelsHeight * 4;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++)
        {
            view.ScrollTo(view.Offset.X, view.Offset.Y + view.ViewportPixelsHeight, animate: false);
            WaitForScreen(window);
            peak = Math.Max(peak, view.Tiles.CachedBytes);
        }
        var perScreen = sw.ElapsedMilliseconds / 60.0;
        Console.WriteLine($"     60 screens at 200%: {perScreen:0} ms each; tile memory peak {peak >> 20} MiB of a {view.Tiles.Budget >> 20} MiB budget");
        Check(peak <= view.Tiles.Budget + (2 * screenBytes), $"tile memory {peak >> 20} MiB over budget {view.Tiles.Budget >> 20} MiB");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"     process after scrolling: {process.PrivateMemorySize64 >> 20} MiB private, managed heap {GC.GetTotalMemory(false) >> 20} MiB");
    }

    private static void ReloadOnChange()
    {
        var path = Path.Combine(Temp, "changing.pdf");
        File.Copy(Fixture("links.pdf"), path, overwrite: true);
        var window = ShowWindow(path);
        Equal(window.PageView.PageCount, 3, "first version");
        window.PageView.GoToPage(1);
        Pump(20);

        // Written the way a build tool writes: truncated, then filled.
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            var bytes = File.ReadAllBytes(Fixture("mixed.pdf"));
            stream.Write(bytes, 0, bytes.Length / 2);
            stream.Flush();
            Thread.Sleep(150);
            stream.Write(bytes, bytes.Length / 2, bytes.Length - (bytes.Length / 2));
        }

        PumpUntil(() => window.PageView.PageCount == 5, "the reload", 15000);
        Equal(window.PageView.CurrentPage, 1, "the place is kept");
        Check(window.Document!.FilePath == Path.GetFullPath(path), "same file");
        Check(window.PageView.History.CanGoBack, "Back still leads where it did");

        // A search in the box runs again on the new version - marking its matches, not moving the reader.
        window.SearchBox.Text = "Mixed page 5";
        window.StartSearch(immediate: true);
        PumpUntil(() => window.Search is { IsComplete: true }, "the search");
        PumpUntil(() => window.PageView.CurrentPage == 4, "a search the user starts goes to its match");
        window.PageView.GoToPage(1);
        Pump(10);
        var before = window.Search;

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            stream.Write(File.ReadAllBytes(Fixture("mixed.pdf")));
        PumpUntil(() => window.Search is { IsComplete: true, Matches.Count: 1 } s && !ReferenceEquals(s, before), "the search after the reload", 15000);
        Pump(20);
        Equal(window.PageView.CurrentPage, 1, "the reader is not moved by the search running again");
        window.MoveMatch(+1);
        PumpUntil(() => window.PageView.CurrentPage == 4, "stepping to the match goes there");
    }

    private static void SelectionRotated()
    {
        var window = ShowWindow("search.pdf");
        var view = window.PageView;
        var text = Info(window, 0).Text;
        var all = text.GetText(0, text.Count - 1);
        var beta = all.IndexOf("beta", StringComparison.Ordinal);

        string DragFromBetaTo(Func<Point> outside)
        {
            var box = text.Boxes[beta];
            var start = view.ViewPointOf(0, box.X0 + (box.Width * 0.2), (box.Y0 + box.Y1) / 2);
            view.PointerDown(start, 1, false);
            var end = outside();
            view.PointerMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2));
            view.PointerMove(end);
            view.PointerUp(end);
            Pump(5);
            return Selected(window);
        }

        // Upright: below the page is the end of its text.
        var upright = DragFromBetaTo(() => { var p = view.ViewPointOf(0, 0.5, 1); return new Point(p.X, p.Y + 6); });
        Check(upright.StartsWith("beta", StringComparison.Ordinal) && upright.Length > 10, $"upright: {upright}");

        // Upside down, the page's end is at the top of the screen: above it selects to the end too.
        view.Rotate(2);
        Pump(10);
        view.ScrollTo(0, 0, animate: false);
        Pump(10);
        var turned = DragFromBetaTo(() => { var p = view.ViewPointOf(0, 0.5, 1); return new Point(p.X, p.Y - 6); });
        Equal(turned, upright, "turned half way, the same words");
    }

    private static void JumpAfterWideScroll()
    {
        var window = ShowWindow("mixed.pdf");
        var view = window.PageView;
        var widthDip = view.ViewportPixelsWidth / view.Dpi;
        bool InView(int page)
        {
            var left = view.ViewPointOf(page, 0, 0.5).X;
            var right = view.ViewPointOf(page, 1, 0.5).X;
            return right > 0 && left < widthDip && (right - left > widthDip || (left >= -1 && right <= widthDip + 1));
        }

        view.GoToPage(4);
        Pump(10);
        Check(view.Layout!.Width(4) > view.ViewportPixelsWidth, "the last page is wider than the window");
        view.ScrollTo(double.MaxValue, view.Offset.Y, animate: false);
        Pump(10);
        Check(view.Offset.X > 0, "scrolled right on the wide page");

        view.GoToPage(0);
        Pump(10);
        Check(InView(0), $"page 1 is in view after the jump (offset {view.Offset.X})");
        view.GoBack();
        Pump(10);
        Check(InView(4) && view.CurrentPage == 4, "Back returns to the wide page");
    }

    private static void PasswordFlow()
    {
        var prompts = new List<bool>();
        MainWindow.PasswordPrompt = (_, wrongBefore) =>
        {
            prompts.Add(wrongBefore);
            return prompts.Count == 1 ? "nope" : "secret";
        };
        var window = ShowWindow("encrypted.pdf");
        Equal(string.Join(",", prompts), "False,True", "asked, then told it was wrong");
        Check(window.HasDocument, "opened with the right password");

        var cancelled = App.OpenEmptyWindow(null);
        MainWindow.PasswordPrompt = (_, _) => null;
        Check(!Await(cancelled.OpenAsync(Fixture("encrypted.pdf"))), "cancelling does not open");
        Check(!cancelled.HasDocument && cancelled.StartPage.Visibility == Visibility.Visible, "the start page shows");
    }

    private static void OpenFailure()
    {
        var notices = CaptureNotices();
        var window = App.OpenEmptyWindow(null);
        Check(!Await(window.OpenAsync(Fixture("notpdf.pdf"))), "not a PDF");
        Check(notices.Single().Contains("is not a PDF document", StringComparison.Ordinal), notices.Single());
        Check(window.StartPage.Visibility == Visibility.Visible, "start page");

        Check(Await(window.OpenAsync(Fixture("links.pdf"))), "a good file after a bad one");
        notices.Clear();
        Check(!Await(window.OpenAsync(Fixture("garbage.pdf"))), "damaged");
        Check(notices.Single().Contains("damaged", StringComparison.Ordinal), notices.Single());
        Check(window.HasDocument && window.Document!.FileName == "links.pdf", "the open document stays when another fails");
    }

    private static void OpenOnce()
    {
        var path = Fixture("links.pdf");
        App.Open(path, null);
        PumpUntil(() => App.DocumentWindows.Any(w => w.HasDocument), "the first open");
        App.Open(path, null);
        App.Open(path.ToUpperInvariant(), null);
        Pump(50);
        Equal(App.DocumentWindows.Count(w => w.DocumentPath is not null), 1, "one window has the file");
    }

    private static void Thumbnails()
    {
        var window = ShowWindow("links.pdf");
        window.SetSidebar(true, remember: false);
        window.ShowPagesInSidebar();
        PumpUntil(() => window.Thumbnails is { Count: 3 }, "three thumbnails", 10000);
        Equal(window.ThumbnailList.Items.Count, 3, "thumbnail items");
        Equal(window.ThumbnailList.SelectedIndex, 0, "the current page is selected");
        window.PageView.GoToPage(2);
        Pump(20);
        Equal(window.ThumbnailList.SelectedIndex, 2, $"the strip follows the page (current page {window.PageView.CurrentPage}, {window.PageView.DescribeScreen()})");
    }

    private static void HeaderFolds()
    {
        var window = ShowWindow("links.pdf", 1300);
        Check(window.PrintButton.Visibility == Visibility.Visible && window.OpenLabel.Visibility == Visibility.Visible, "everything shows when wide");

        window.Width = 640;
        Pump(60);
        Check(window.PrintButton.Visibility != Visibility.Visible, "print folds away when narrow");
        Check(window.OpenLabel.Visibility != Visibility.Visible, "the open label goes first");
        Check(window.SearchArea.ActualWidth >= 160, $"the search box stays usable: {window.SearchArea.ActualWidth}");
        Check(window.PagePanel.IsVisible && window.ZoomButton.IsVisible, "page and zoom always show");

        window.Width = 1300;
        Pump(60);
        Check(window.PrintButton.Visibility == Visibility.Visible, "back when wide again");
    }

    private static void HeaderCentred()
    {
        var window = ShowWindow("links.pdf", 1300);
        var header = window.Header;

        // The strip in sight: below the border Windows 11 draws over the window's top pixel, above
        // the header's own line.
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleY;
        var top = WindowTitleBar.BorderCoversEdge ? 1 / scale : 0;
        Near(header.Padding.Top, top, 1e-9, "the header clears the window's border");
        // Exact at a whole scale; at 125% or 150% layout rounding can leave half a pixel either way.
        var tolerance = scale % 1 == 0 ? 1e-9 : 0.5 / scale;
        var bottom = header.ActualHeight - header.BorderThickness.Bottom;
        Near(bottom - top, 42, tolerance, "the strip's height");
        var middle = (top + bottom) / 2;

        // Text in a box on the baseline of the text beside it. A TextBlock's line starts at its top,
        // and WPF draws every baseline on a whole pixel.
        int Baseline(FrameworkElement text, double lineTop) =>
            (int)Math.Round((text.TranslatePoint(new Point(0, lineTop), header).Y
                + (System.Windows.Documents.TextElement.GetFontFamily(text).Baseline * System.Windows.Documents.TextElement.GetFontSize(text))) * scale);
        int BoxBaseline(System.Windows.Controls.TextBox box) => Baseline(box, box.GetRectFromCharacterIndex(0).Top);
        Equal(BoxBaseline(window.SearchBox), Baseline(window.SearchPlaceholder, 0), "typed text on the placeholder's baseline");
        Equal(BoxBaseline(window.PageBox), Baseline(window.PageCountText, 0), "the page number on the baseline of \"of N\"");

        // With the search tools showing too.
        window.SearchBox.Text = "page";
        PumpUntil(() => window.Search is { IsComplete: true }, "search");
        Pump(30);
        Check(window.SearchTools.IsVisible, "the search tools show");

        FrameworkElement[] boxes =
        [
            window.SearchBox, window.OpenButton, window.ToolbarSeparator, window.SidebarButton, window.PageBox, window.ZoomButton,
            window.FitButton, window.PagesButton, window.RotateButton, window.PrintButton, window.MoreButton, window.MaximizeButton,
        ];
        foreach (var element in boxes.Concat(Descendants(header).OfType<IconPath>()).Where(e => e.IsVisible))
        {
            var centre = element.TranslatePoint(new Point(0, element.ActualHeight / 2), header).Y;
            Near(centre, middle, tolerance, $"{Describe(element)} centred");
        }

        static string Describe(FrameworkElement element)
        {
            DependencyObject? node = element;
            while (node is FrameworkElement { Name.Length: 0 } or FrameworkElement { TemplatedParent: not null }) node = VisualTreeHelper.GetParent(node);
            return node is FrameworkElement named && named != element ? $"an icon in {named.Name}" : element.Name;
        }
    }

    private static void DialogsAndMenus()
    {
        foreach (var language in new[] { "en", "nb" })
        {
            UiLanguage.Apply(language);
            try
            {
                var window = ShowWindow("links.pdf");
                Check(window.BuildZoomMenu().Items.Count == 11, "the zoom menu");
                Check(window.BuildPageDisplayMenu().Items.Count == 9, "the page display menu");
                var more = window.BuildMoreMenu();
                Check(more.Items.Count > 10, "the more menu");
                var link = Info(window, 0).Links[0].Target;
                Check(window.PageView.BuildContextMenu(link).Items.Count == 6, "the context menu over a link");
                Check(window.PageView.BuildContextMenu(null).Items.Count == 5, "the context menu elsewhere");

                // Folded: the toolbar's buttons appear at the top of the more menu.
                window.Width = 640;
                Pump(60);
                Check(window.BuildMoreMenu().Items.Count > more.Items.Count, "folded buttons are in the more menu");

                foreach (var file in new[] { "links.pdf", "form.pdf", "encrypted.pdf", "a4.pdf" })
                {
                    var doc = Open(file, file == "encrypted.pdf" ? "secret" : null);
                    var properties = new PropertiesDialog(doc);
                    Check(properties.Rows.Children.Count >= 10, $"{file}: properties rows");
                    properties.Close();
                    Await(doc.CloseAsync());
                }

                new NoticeDialog("Title", "Message").Close();
                new ConfirmDialog("Title", "Message", "https://example.com", "Open").Close();
                new PasswordDialog("file.pdf", wrongBefore: true).Close();
            }
            finally
            {
                UiLanguage.Apply("en");
            }
        }
    }

    private static void FullScreen()
    {
        var window = ShowWindow("links.pdf");
        var before = new Rect(window.Left, window.Top, window.Width, window.Height);
        var header = window.HeaderRow.Height.Value;
        window.SetSidebar(true, remember: false);

        window.ToggleFullScreen();
        Pump(50);
        Check(window.IsFullScreen, "in full screen");
        Equal(window.HeaderRow.Height.Value, 0.0, "header hidden");
        Check(!window.IsSidebarOpen, "sidebar hidden");
        Check(window.ResizeMode == ResizeMode.NoResize, "no resizing in full screen");

        window.ToggleFullScreen();
        Pump(50);
        Check(!window.IsFullScreen, "out of full screen");
        Equal(window.HeaderRow.Height.Value, header, "header back");
        Check(window.IsSidebarOpen, "sidebar back");
        Equal(new Rect(window.Left, window.Top, window.Width, window.Height), before, "the window is back where it was");
        WaitForScreen(window);
    }

    private static unsafe void PrintsToDeviceContext()
    {
        foreach (var (file, x0, y0, x1, y1, what) in new[]
                 {
                     ("links.pdf", 72, 52, 300, 76, "text"),
                     ("form.pdf", 76, 104, 296, 128, "a form field's value"),
                 })
        {
            var doc = Open(file);
            var ink = GdiCapture.RenderThroughDc(612, 792, hdc => doc.Engine.Invoke(() => doc.PrintPage(hdc, 0, 0, 0, 612, 792, 0)), x0, y0, x1, y1);
            Check(ink > 40, $"printing draws {what}: {ink} dark pixels");
            Await(doc.CloseAsync());
        }
    }

    private static void WindowsAreReleased()
    {
        var refs = OpenAndClose();
        for (var i = 0; i < 5 && refs.Any(r => r.Reference.IsAlive); i++)
        {
            Pump(50);
            PumpIdle();
            // Something through the engine, so its thread holds no finished job.
            Await(PdfEngine.Instance.Run(JobPriority.Interactive, () => 0));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        var alive = refs.Where(r => r.Reference.IsAlive).Select(r => r.What).ToArray();
        if (alive.Length > 0)
        {
            Console.WriteLine(HeapRoots.Explain("EasyPDF.Pdf.PdfDocument"));
            Console.WriteLine(HeapRoots.Explain("EasyPDF.Views.MainWindow"));
        }
        Check(alive.Length == 0, $"{alive.Length} of {refs.Count} objects survived closing: {string.Join(", ", alive)}");

        static List<(WeakReference Reference, string What)> OpenAndClose()
        {
            var refs = new List<(WeakReference, string)>();
            for (var i = 0; i < 3; i++)
            {
                var window = ShowWindow(i == 1 ? "search.pdf" : i == 2 ? "buttons.pdf" : "links.pdf");
                window.SetSidebar(true, remember: false);
                window.ShowPagesInSidebar();
                window.SearchBox.Text = "a";
                Pump(400);
                var document = window.Document!;

                // Closed in the middle of things: a page turned one at a time (its number fading), a
                // link's destination still flashing, the recent list built.
                window.SetPageDisplay(i == 1 ? null : i == 2 ? new PageGrid(2, 2) : PageGrid.One);
                window.PageView.HandleKey(Key.PageDown, ModifierKeys.None);
                window.PageView.RequestPageInfo(window.PageView.CurrentPage, JobPriority.Interactive);
                PumpUntil(() => window.PageView.InfoFor(window.PageView.CurrentPage) is not null, "the page's links");
                if (window.PageView.InfoFor(window.PageView.CurrentPage)!.Links.FirstOrDefault(l => l.Target is GoToTarget) is { } link)
                    window.PageView.Activate(link.Target);
                window.BuildRecentMenu();
                Pump(30);
                refs.Add((new WeakReference(window), $"window {i}"));
                refs.Add((new WeakReference(window.PageView), $"view {i}"));
                refs.Add((new WeakReference(document), $"document {i}"));
                window.Close();
                PumpUntil(() => document.IsClosed, "the document to close");
            }
            return refs;
        }
    }
}
