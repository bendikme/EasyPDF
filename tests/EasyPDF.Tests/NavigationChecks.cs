using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyPDF.Pdf;
using EasyPDF.Services;
using EasyPDF.Viewer;
using EasyPDF.Views;
using static Program;
using Point = System.Windows.Point;

/// <summary>Marked destinations, the compact sidebar, grids of pages, fast and smooth turns, and the trail of jumps.</summary>
internal static class NavigationChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("A jump that marks where it lands (EPLAN's highlight script) marks that element, not the page", MarkedDestinations),
        ("The sidebar's compact rows fit more in, and are remembered", CompactSidebar),
        ("A grid of pages: sheets turn whole, pages sit in their cells, the cover stands alone", PageGrids),
        ("A grid of any size from the picker, remembered, and from settings written before grids", GridPickerAndSettings),
        ("Shift+Down/Up turns at once and faster and faster while held", FastFlipping),
        ("A turn shows a sheet already drawn, fading the one before out", SmoothTurns),
        ("The trail of jumps: back and forward, several places at once, folded when long", JumpTrail),
    ];

    private static MainWindow ShowWindow(string path)
    {
        var window = App.OpenEmptyWindow(null);
        Check(Await(window.OpenAsync(path)), $"{Path.GetFileName(path)} opens");
        WaitForScreen(window);
        return window;
    }

    private static void WaitForScreen(MainWindow window) =>
        PumpUntil(() => window.PageView.IsScreenComplete() && !window.PageView.Tiles.IsBusy, "the screen to render", 20000);

    private static string Shown(PdfView view) => string.Join(",", view.PageDrawings().Select(d => d.Page).OrderBy(p => p));

    private static PageInfo Info(MainWindow window, int page)
    {
        window.PageView.RequestPageInfo(page, JobPriority.Interactive);
        PumpUntil(() => window.PageView.InfoFor(page) is { MoreLinksPending: null }, $"page {page + 1}'s links");
        return window.PageView.InfoFor(page)!;
    }

    private static void Click(MainWindow window, Point point)
    {
        var view = window.PageView;
        view.PointerMove(point);
        view.PointerDown(point, 1, shift: false);
        view.PointerUp(point);
        Pump(10);
    }

    /// <summary>The middle of a rectangle in PDF points on a page, in the view.</summary>
    private static Point At(MainWindow window, int page, double x0, double y0, double x1, double y1, double w = 612, double h = 792) =>
        window.PageView.ViewPointOf(page, (x0 + x1) / 2 / w, 1 - ((y0 + y1) / 2 / h));

    private static string Pages30()
    {
        var path = Path.Combine(Temp, "pages30.pdf");
        return File.Exists(path) ? path : BigPdfWriter.Write(path, 30, [], namedLinkPage: 5);
    }

    // ===================================================================== marks

    /// <summary>
    /// Three jumps from page 1, as EPLAN writes them: a link annotation and a push button (its
    /// script in a stream) to page 3, a link to page 2, which is turned a quarter. Each goes to a
    /// page as a whole and then runs highlight(this.pageNum, [...]) on the element it refers to.
    /// </summary>
    private static string WriteMarks()
    {
        var path = Path.Combine(Temp, "marks.pdf");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new SortedDictionary<int, long>();
        void Raw(string s) => stream.Write(Encoding.Latin1.GetBytes(s));
        void Obj(int n, string body)
        {
            offsets[n] = stream.Position;
            Raw($"{n} 0 obj\n{body}\nendobj\n");
        }

        Raw("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R] >> /Names << /JavaScript << /Names [(highlight) 20 0 R] >> >> >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");
        const string font = "/Resources << /Font << /F1 13 0 R >> >>";
        Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 6 0 R /Annots [8 0 R 10 0 R 9 0 R] >>");
        Obj(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate 90 {font} /Contents 7 0 R >>");
        Obj(5, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {font} /Contents 7 0 R >>");
        const string one = "BT /F1 12 Tf 72 600 Td (To the motor) Tj ET BT /F1 12 Tf 72 500 Td (To the switch) Tj ET BT /F1 12 Tf 72 400 Td (To the turned page) Tj ET";
        Obj(6, $"<< /Length {one.Length} >>\nstream\n{one}\nendstream");
        const string other = "BT /F1 12 Tf 72 700 Td (A page of elements) Tj ET";
        Obj(7, $"<< /Length {other.Length} >>\nstream\n{other}\nendstream");
        Obj(8, @"<< /Type /Annot /Subtype /Link /Rect [70 595 250 615] /Border [0 0 0] /A << /S /GoTo /D [5 0 R /Fit] /Next << /S /JavaScript /JS (highlight\(this.pageNum, [60, 300, 300, 330]\);) >> >> >>");
        Obj(9, @"<< /Type /Annot /Subtype /Link /Rect [70 395 250 415] /Border [0 0 0] /A << /S /GoTo /D [4 0 R /Fit] /Next [<< /S /JavaScript /JS (highlight\(this.pageNum, [100, 50, 200, 80]\);) >>] >> >>");
        Obj(10, "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (b1) /Rect [70 495 250 515] /P 3 0 R /A << /S /GoTo /D [5 0 R /Fit] /Next 11 0 R >> >>");
        Obj(11, "<< /S /JavaScript /JS 12 0 R >>");
        const string script = "highlight(this.pageNum, [400, 100, 500, 140]);";
        Obj(12, $"<< /Length {script.Length} >>\nstream\n{script}\nendstream");
        Obj(13, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Obj(20, @"<< /S /JavaScript /JS (function highlight\(page, rect\) { }) >>");

        var xref = stream.Position;
        var size = offsets.Keys.Max() + 1;
        Raw($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var n = 1; n < size; n++) Raw(offsets.TryGetValue(n, out var at) ? $"{at:D10} 00000 n \n" : "0000000000 65535 f \n");
        Raw($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    private static void MarkedDestinations()
    {
        var path = WriteMarks();
        var motor = PageRect.FromCorners(60 / 612.0, (792 - 330) / 792.0, 300 / 612.0, (792 - 300) / 792.0);
        var button = PageRect.FromCorners(400 / 612.0, (792 - 140) / 792.0, 500 / 612.0, (792 - 100) / 792.0);
        // The turned page as shown is 792 wide and 612 tall; the mark is from its lower left corner.
        var turned = PageRect.FromCorners(100 / 792.0, 1 - (80 / 612.0), 200 / 792.0, 1 - (50 / 612.0));

        void Same(PageRect actual, PageRect expected, string what)
        {
            Near(actual.X0, expected.X0, 0.003, $"{what}: left");
            Near(actual.Y0, expected.Y0, 0.003, $"{what}: top");
            Near(actual.X1, expected.X1, 0.003, $"{what}: right");
            Near(actual.Y1, expected.Y1, 0.003, $"{what}: bottom");
        }

        // The engine: each link knows what it marks, and resolving it gives the area.
        var doc = Open(path);
        var info = Await(doc.LoadPageInfoAsync(0, JobPriority.Interactive))!;
        for (var i = 0; i < 10 && info.MoreLinksPending is { } pending; i++)
        {
            Await(pending);
            info = Await(doc.LoadPageInfoAsync(0, JobPriority.Interactive))!;
        }
        PdfLink LinkAt(double x, double y) => info.LinkAt(x / 612, 1 - (y / 792), 0.002) ?? throw new InvalidOperationException($"no link at {x},{y}");
        var marks = new[] { (LinkAt(160, 605), motor, 2, "the link"), (LinkAt(160, 505), button, 2, "the button"), (LinkAt(160, 405), turned, 1, "the link to the turned page") };
        foreach (var (link, expected, page, what) in marks)
        {
            Check(link.Target is GoToTarget { Destination.Mark: not null }, $"{what} knows what it marks");
            var resolved = Await(doc.ResolveAsync(((GoToTarget)link.Target).Destination))!.Value;
            Equal(resolved.PageIndex, page, $"{what}: the page");
            Check(resolved.Area is not null, $"{what}: an area");
            Same(resolved.Area!.Value, expected, what);
        }
        Equal(info.Links.Count, 3, "each link once - the annotations PDFium read are not doubled");
        Await(doc.CloseAsync());

        // The view: the frame goes round the element, which is brought into sight.
        var window = ShowWindow(path);
        var view = window.PageView;
        Info(window, 0);
        foreach (var (x, y, expected, page, what) in new[] { (160.0, 605.0, motor, 2, "the link"), (160.0, 505.0, button, 2, "the button"), (160.0, 405.0, turned, 1, "the turned page") })
        {
            view.GoToPage(0, record: false);
            Pump(20);
            Click(window, At(window, 0, x - 1, y - 1, x + 1, y + 1));
            PumpUntil(() => view.Flashing is { } f && f.Page == page, $"{what}: marked");
            var area = view.Flashing!.Value.Area;
            Check(area != new PageRect(0, 0, 1, 1), $"{what}: not the whole page");
            Same(area, expected, $"{what} on screen");
            var middle = view.ViewPointOf(page, (area.X0 + area.X1) / 2, (area.Y0 + area.Y1) / 2);
            Check(middle.X > 0 && middle.Y > 0 && middle.X < view.ActualWidth && middle.Y < view.ActualHeight, $"{what}: in sight ({middle})");
        }
    }

    // ===================================================================== compact sidebar

    private static void CompactSidebar()
    {
        var window = ShowWindow(Fixture("links.pdf"));
        window.SetSidebar(true, remember: false);
        window.ShowPagesInSidebar();
        Pump(50);
        Check(!window.IsSidebarCompact, "normal rows to begin with");
        var normalRow = (double)window.FindResource("NavigatorRowHeight");
        PumpUntil(() => window.ThumbnailList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement { ActualHeight: > 0 }, "a thumbnail row");
        var normalThumb = ((FrameworkElement)window.ThumbnailList.ItemContainerGenerator.ContainerFromIndex(0)).ActualHeight;

        window.SetCompactSidebar(true);
        Pump(50);
        Check(window.IsSidebarCompact, "compact");
        Check((double)window.FindResource("NavigatorRowHeight") < normalRow, "outline rows are shorter");
        Equal(window.CompactButton.Tag, (object)"on", "the button shows it is on");
        PumpUntil(() => window.ThumbnailList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement { ActualHeight: > 0 } row && row.ActualHeight < normalThumb * 0.75,
            "thumbnail rows are smaller");
        Check(SettingsStore.Current.Settings.SidebarCompact, "remembered");

        var again = ShowWindow(Fixture("links.pdf"));
        Check(again.IsSidebarCompact, "a new window is compact too");
        again.SetCompactSidebar(false);
        Check(!SettingsStore.Current.Settings.SidebarCompact, "and normal again");
    }

    // ===================================================================== grids

    private static void PageGrids()
    {
        var window = ShowWindow(Pages30());
        var view = window.PageView;
        Equal(window.BuildPageDisplayMenu().Items.Count, 9, "continuous, seven presets and any grid");

        // Four at a time: fitted whole, two across, two down.
        window.SetPageDisplay(new PageGrid(2, 2));
        WaitForScreen(window);
        var layout = view.Layout!;
        Check(layout.Fixed && view.Mode == ZoomMode.FitPage, "a grid that would not fit across is fitted whole");
        Equal(Shown(view), "0,1,2,3", "the first four pages");
        Equal(view.CurrentSheet, (0, 3), "the sheet");
        Near(layout.Top(1), layout.Top(0), 0.5, "the second page beside the first");
        Check(layout.Left(1) >= layout.Left(0) + layout.Width(0), "to its right");
        Check(layout.Top(2) >= layout.Bottom(0), "the third below the first");
        Near(layout.Left(2), layout.Left(0), 0.5, "under it");
        Check(layout.Bottom(3) - view.Offset.Y <= view.ViewportPixelsHeight && layout.Left(1) + layout.Width(1) <= view.ViewportPixelsWidth, "all four in sight");
        Check(view.ExtentWidth <= view.ViewportWidth + 0.5, "no sideways scrolling");

        // A turn is a whole sheet, and says which pages these are.
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        Equal(Shown(view), "4,5,6,7", "Page Down: the next four");
        Equal(view.CurrentPage, 4, "the current page is the sheet's first");
        PumpUntil(() => window.PageIndicatorShown, "the pages shown");
        Equal(window.PageIndicatorText, "5\u20138 of 30", "which pages, of how many");
        view.HandleKey(Key.Right, ModifierKeys.None);
        Equal(Shown(view), "8,9,10,11", "Right: the next sheet");
        view.HandleKey(Key.Left, ModifierKeys.None);
        view.HandleKey(Key.Up, ModifierKeys.None);
        Equal(Shown(view), "0,1,2,3", "Left and Up: back");
        view.HandleKey(Key.End, ModifierKeys.None);
        Equal(Shown(view), "28,29", "End: the last sheet, two pages on it");

        // A page in the grid is clicked where it is: page 6's "next page" link turns to the next sheet.
        view.GoToPage(5);
        Pump(20);
        Info(window, 5);
        Click(window, At(window, 5, 70, 600, 240, 620));
        PumpUntil(() => view.CurrentSheet.First == 8, "the named action on page 6 turns the sheet");

        // Zoomed into one page of the sheet, that page is the current one.
        view.SetZoom(view.Zoom * 2.2, view.ViewPointOf(10, 0.5, 0.5));
        Pump(30);
        Equal(view.CurrentPage, 10, "zoomed into the third page of the sheet, it is the current page");

        // Two at a time, the cover alone: page 1 on the right, then pairs.
        window.SetPageDisplay(new PageGrid(2, 1, coverAlone: true));
        view.SetMode(ZoomMode.FitPage);
        view.GoToPage(0);
        WaitForScreen(window);
        layout = view.Layout!;
        Equal(Shown(view), "0", "the cover alone");
        Check(layout.Left(0) > view.ViewportPixelsWidth / 2 - layout.Gap, "on the right, where a book's cover is");
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        Equal(Shown(view), "1,2", "then the pages in pairs");
        Check(layout.Left(1) + layout.Width(1) <= layout.Left(2), "side by side");

        // Two stacked.
        window.SetPageDisplay(new PageGrid(1, 2));
        WaitForScreen(window);
        Equal(Shown(view), "0,1", "two stacked: the sheet with page 2 is pages 1 and 2");
        Check(view.Layout!.Top(1) > view.Layout.Bottom(0), "one above the other");

        // A search match, a link and the outline: the sheet with the page.
        window.SetPageDisplay(new PageGrid(3, 3));
        view.GoToPage(20);
        WaitForScreen(window);
        Equal(Shown(view), "18,19,20,21,22,23,24,25,26", "the sheet with page 21");
        view.GoBack();
        Equal(view.CurrentSheet.First, 0, "Back: the sheet it was left from");

        // Continuous again: one column, on the same page.
        window.SetPageDisplay(null);
        Pump(20);
        Check(!view.IsFixed && !view.Layout!.Fixed, "continuous again");
    }

    private static void GridPickerAndSettings()
    {
        var path = Pages30();
        var window = ShowWindow(path);
        var view = window.PageView;

        // The picker: four across, three down.
        var menu = window.BuildPageDisplayMenu();
        var other = menu.Items.OfType<MenuItem>().Last();
        var picker = (GridPicker)((MenuItem)other.Items[0]!).Header;
        Check(picker.Shown is null, "continuous: nothing chosen");
        picker.Pick(new PageGrid(4, 3));
        WaitForScreen(window);
        Equal(view.Grid, new PageGrid(4, 3), "the grid picked");
        Equal(Shown(view), string.Join(",", Enumerable.Range(0, 12)), "twelve pages at a time");
        Check(window.BuildPageDisplayMenu().Items.OfType<MenuItem>().Last().IsChecked, "not a preset: 'other' is checked");

        // Remembered for the document and for new ones.
        Equal(SettingsStore.Current.Settings.Grid, "4x3", "the default for new documents");
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        window.Close();
        Pump(20);
        Check(SettingsStore.Current.FindRecent(path) is { Grid: "4x3" }, "remembered for the document");
        var again = ShowWindow(path);
        Equal(again.PageView.Grid, new PageGrid(4, 3), "it opens in the grid again");
        Equal(again.PageView.CurrentSheet.First, 12, "on the sheet it was left at");
        again.Close();
        var fresh = ShowWindow(Fixture("links.pdf"));
        Equal(fresh.PageView.Grid, new PageGrid(4, 3), "a document opened for the first time takes the grid");
        fresh.Close();
        SettingsStore.Current.Settings.Grid = null;

        // Grids as text: what is remembered reads back, and nothing else does.
        foreach (var grid in PageGrid.Presets.Append(new PageGrid(6, 4)))
            Equal(PageGrid.Parse(grid.ToString()), grid, $"{grid} reads back");
        foreach (var bad in new[] { "", "x", "0x1", "7x1", "1x5", "2x", "ax2", "2x2cc", "-1x2" })
            Check(PageGrid.Parse(bad) is null, $"'{bad}' is no grid");

        // Settings from before grids: "one page at a time" is the one-page grid.
        SettingsStore.Current.Flush();
        var dir = Path.Combine(Temp, "legacy-settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{ "singlePage": true, "recent": [ { "path": "C:\\a.pdf", "singlePage": true }, { "path": "C:\\b.pdf" }, { "path": "C:\\c.pdf", "grid": "9x9" } ] }""");
        try
        {
            var legacy = SettingsStore.Load(dir);
            Equal(legacy.Settings.Grid, "1x1", "the default: one page");
            Equal(legacy.FindRecent(@"C:\a.pdf")?.Grid, "1x1", "a file left one page at a time");
            Equal(legacy.FindRecent(@"C:\b.pdf")?.Grid, null, "a file left continuous");
            Equal(legacy.FindRecent(@"C:\c.pdf")?.Grid, null, "a grid that cannot be is none");
        }
        finally
        {
            // Where the harness keeps the checks' settings.
            SettingsStore.Load(Temp);
        }
    }

    // ===================================================================== fast flipping

    private static void FastFlipping()
    {
        var window = ShowWindow(Pages30());
        var view = window.PageView;
        window.SetPageDisplay(PageGrid.One);
        view.SetMode(ZoomMode.FitWidth);
        WaitForScreen(window);
        Check(view.Layout!.SlotBottom(0) - view.ViewportPixelsHeight > view.Layout.SlotTop(0), "the page is taller than the window");

        // Shift+Down: the next page at once, though this one could still scroll.
        view.HandleKey(Key.Down, ModifierKeys.Shift);
        Equal(view.CurrentPage, 1, "the next page");
        Near(view.Offset.Y, view.Layout!.SlotTop(1), 0.5, "from its top, at once");
        Thread.Sleep(350);

        // Held down: one page at a time at first, then two, five...
        for (var i = 0; i < 10; i++) view.HandleKey(Key.Down, ModifierKeys.Shift);
        Equal(view.CurrentPage, 1 + 6 + 8, "six one at a time, then four of two");
        for (var i = 0; i < 10; i++) view.HandleKey(Key.Down, ModifierKeys.Shift);
        Equal(view.CurrentPage, 29, "faster still, to the end and no further");
        Thread.Sleep(350);
        view.HandleKey(Key.Up, ModifierKeys.Shift);
        Equal(view.CurrentPage, 28, "let go and pressed again: one page");
        PumpUntil(() => window.PageIndicatorShown, "the page number shows");

        // Continuous: the same, page by page, and the page number shows.
        window.SetPageDisplay(null);
        view.GoToPage(0, record: false);
        Pump(20);
        Thread.Sleep(350);
        view.HandleKey(Key.Down, ModifierKeys.Shift);
        Equal(view.CurrentPage, 1, "continuous: the next page");
        Near(view.Offset.Y, view.Layout!.SlotTop(1), 0.5, "from its top");
        view.HandleKey(Key.Down, ModifierKeys.Shift);
        PumpUntil(() => window.PageIndicatorShown, "continuous, turned fast: the page number shows");
        Equal(window.PageIndicatorText, "3 of 30", "which page");
    }

    // ===================================================================== smooth turns

    private static void SmoothTurns()
    {
        var window = ShowWindow(Pages30());
        var view = window.PageView;
        window.SetPageDisplay(PageGrid.One);
        view.SetMode(ZoomMode.FitPage);
        WaitForScreen(window);

        // Once the screen is drawn, the pages either side are drawn too, as a turn will show them.
        PumpUntil(() => view.HasTilesAtScale(1) && !view.Tiles.IsBusy, "the next page drawn ahead");
        Thread.Sleep(300);
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        Check(view.IsScreenComplete(), "the page turned to is complete at once: " + view.DescribeScreen());
        if (PdfView.TurnsFade)
        {
            Check(view.TurnFading, "the page turned from fades out");
            PumpUntil(() => !view.TurnFading, "and is gone soon", 1000);
        }
        PumpUntil(() => view.HasTilesAtScale(0) && view.HasTilesAtScale(2) && !view.Tiles.IsBusy, "both neighbours drawn");
        Thread.Sleep(300);
        view.HandleKey(Key.PageUp, ModifierKeys.None);
        Check(view.IsScreenComplete(), "back: complete at once too");

        // Turns in quick succession cut over: nothing smears.
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        Check(!view.TurnFading, "a quick second turn does not fade");

        // A grid's neighbours too.
        window.SetPageDisplay(new PageGrid(2, 2));
        WaitForScreen(window);
        PumpUntil(() => Enumerable.Range(view.CurrentSheet.Last + 1, 4).All(view.HasTilesAtScale) && !view.Tiles.IsBusy,
            "the next sheet drawn ahead");
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        Check(view.IsScreenComplete(), "the next sheet complete at once");

        // Memory stays within its budget for all that.
        Check(view.Tiles.CachedBytes <= view.Tiles.Budget * 5 / 4, $"tiles within budget: {view.Tiles.CachedBytes >> 20} of {view.Tiles.Budget >> 20} MB");
    }

    // ===================================================================== the trail

    private static void JumpTrail()
    {
        var window = ShowWindow(Fixture("links.pdf"));
        var view = window.PageView;
        Check(!window.TrailShown, "no trail before a jump");
        Info(window, 0);

        // A link: from page 1 to page 3.
        Click(window, window.PageView.ViewPointOf(0, 135 / 612.0, 175 / 792.0));
        PumpUntil(() => view.CurrentPage == 2, "the link's page");
        Check(window.TrailShown, "the trail shows");
        Equal(Trail(window), "1 [3]", "where it came from, and here");

        // Back along the trail, and forward again.
        window.TrailBackButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Equal(view.CurrentPage, 0, "back to the page the link was on");
        Equal(Trail(window), "[1] 3", "the page ahead stays on the trail");
        window.TrailForwardButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Equal(view.CurrentPage, 2, "forward again");

        // "Here" follows the reader.
        view.GoToPage(1, record: false);
        Pump(20);
        Equal(Trail(window), "1 [2]", "here is where the reader is");

        // Hidden until the next jump; Alt+Left still works.
        window.TrailCloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(!window.TrailShown, "hidden");
        view.GoToPage(2);
        Check(window.TrailShown, "back with the next jump");

        // The places are named by the outline.
        PumpUntil(() => window.SectionOf(2) is not null, "the outline read");

        // A long trail folds: three places either side of here, the rest in a menu.
        var many = ShowWindow(Pages30());
        var v = many.PageView;
        foreach (var page in new[] { 5, 10, 15, 20, 25, 29 }) v.GoToPage(page);
        Pump(10);
        Equal(Trail(many), "\u2026 16 21 26 [30]", "folded: the earliest in a menu");
        v.GoBy(-5);
        Equal(v.CurrentPage, 5, "five places back at once");
        Equal(Trail(many), "1 [6] 11 16 21 \u2026", "and the rest ahead");
        Equal(many.TrailPlaces().Count, 7, "every place still on the trail");
        v.GoBy(5);
        Equal(v.CurrentPage, 29, "and five forward");
        v.GoToPage(0);
        Equal(many.TrailPlaces().Count(p => p.Steps > 0), 0, "a new jump drops what was ahead");
        many.CloseDocument();
        Check(!many.TrailShown, "no trail without a document");
    }

    private static string Trail(MainWindow window) =>
        string.Join(" ", window.TrailChipsShown().Select(c => c.Here ? $"[{c.Text}]" : c.Text));
}
