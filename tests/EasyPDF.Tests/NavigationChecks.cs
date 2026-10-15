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
        ("The sidebar's scroll bar sits beside the selected row, not over it", SidebarScrollBarBesideRows),
        ("A grid of pages: sheets turn whole, pages sit in their cells, the cover stands alone", PageGrids),
        ("A grid of any size from the picker, remembered, and from settings written before grids", GridPickerAndSettings),
        ("Shift+Down/Up turns at once and faster and faster while held", FastFlipping),
        ("A turn shows a sheet already drawn, fading the one before out", SmoothTurns),
        ("The trail of jumps: back and forward, several places at once, folded when long", JumpTrail),
        ("A page is on the trail once: back and forth, the same page, a page scrolled to", TrailHoldsEachPageOnce),
        ("Several pages at a time, every key turns the whole sheet, however large it is", KeysTurnWholeSheets),
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
        PumpUntil(() => window.ThumbnailList.Items.Count == 3, "the thumbnails");
        var normalThumb = ((ThumbnailItem)window.ThumbnailList.Items[0]!).Width;
        var normalPadding = (Thickness)window.FindResource("ThumbnailRowPadding");

        window.SetCompactSidebar(true);
        Pump(50);
        Check(window.IsSidebarCompact, "compact");
        Check((double)window.FindResource("NavigatorRowHeight") < normalRow, "outline rows are shorter");
        Equal(window.CompactButton.Tag, (object)"on", "the button shows it is on");
        PumpUntil(() => window.ThumbnailList.Items.Count == 3 && ((ThumbnailItem)window.ThumbnailList.Items[0]!).Width < normalThumb * 0.75,
            "smaller thumbnails");
        var padding = (Thickness)window.FindResource("ThumbnailRowPadding");
        Check(padding.Top + padding.Bottom < normalPadding.Top + normalPadding.Bottom, "closer together");
        Check(SettingsStore.Current.Settings.SidebarCompact, "remembered");

        var again = ShowWindow(Fixture("links.pdf"));
        Check(again.IsSidebarCompact, "a new window is compact too");
        again.SetCompactSidebar(false);
        Check(!SettingsStore.Current.Settings.SidebarCompact, "and normal again");
    }

    private static void SidebarScrollBarBesideRows()
    {
        var window = ShowWindow(Pages30());
        window.SetSidebar(true, remember: false);

        // Thirty thumbnails: the list scrolls, the bar shows, and the selected row stops short of it.
        window.ShowPagesInSidebar();
        window.PageView.GoToPage(3, record: false);
        PumpUntil(() => window.ThumbnailList.SelectedIndex == 3 && window.ThumbnailList.ItemContainerGenerator.ContainerFromIndex(3) is ListBoxItem { IsVisible: true },
            "page 4 selected in the list");
        Pump(50);
        Beside(window.ThumbnailList, (ListBoxItem)window.ThumbnailList.ItemContainerGenerator.ContainerFromIndex(3), "the selected thumbnail");

        // An outline of eighty entries: it scrolls too.
        var outlined = ShowWindow(WriteLongOutline());
        outlined.SetSidebar(true, remember: false);
        outlined.ShowOutlineInSidebar();
        PumpUntil(() => outlined.OutlineTree.Items.Count > 0, "the outline");
        Pump(100);
        var first = (TreeViewItem)outlined.OutlineTree.ItemContainerGenerator.ContainerFromIndex(0);
        first.IsSelected = true;
        Pump(50);
        Beside(outlined.OutlineTree, first, "the selected outline entry");

        static void Beside(FrameworkElement list, FrameworkElement row, string what)
        {
            var bar = Descendants(list).OfType<System.Windows.Controls.Primitives.ScrollBar>()
                .Single(b => b.Orientation == Orientation.Vertical);
            Check(bar.IsVisible, $"{what}: the list scrolls, so its bar shows");
            var highlight = Descendants(row).OfType<Border>().First(b => b.Name == "Bd");
            var rowRight = highlight.TransformToAncestor(list).Transform(new Point(highlight.ActualWidth, 0)).X;
            var barLeft = bar.TransformToAncestor(list).Transform(new Point(0, 0)).X;
            Check(rowRight <= barLeft + 0.5, $"{what}: its highlight ends at {rowRight:0.#}, the bar begins at {barLeft:0.#}");
        }
    }

    /// <summary>Three pages and an outline of eighty entries, one after another, each to a page.</summary>
    private static string WriteLongOutline()
    {
        const int entries = 80;
        var path = Path.Combine(Temp, "long-outline.pdf");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new SortedDictionary<int, long>();
        void Raw(string text) => stream.Write(Encoding.Latin1.GetBytes(text));
        void Obj(int n, string body)
        {
            offsets[n] = stream.Position;
            Raw($"{n} 0 obj\n{body}\nendobj\n");
        }

        const int firstItem = 10;
        Raw("%PDF-1.7\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R /PageMode /UseOutlines >>");
        Obj(2, "<< /Type /Pages /Kids [4 0 R 5 0 R 6 0 R] /Count 3 >>");
        Obj(3, $"<< /Type /Outlines /First {firstItem} 0 R /Last {firstItem + entries - 1} 0 R /Count {entries} >>");
        for (var page = 0; page < 3; page++) Obj(4 + page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        for (var i = 0; i < entries; i++)
        {
            var n = firstItem + i;
            var links = (i > 0 ? $" /Prev {n - 1} 0 R" : string.Empty) + (i < entries - 1 ? $" /Next {n + 1} 0 R" : string.Empty);
            Obj(n, $"<< /Title (Section {i + 1}) /Parent 3 0 R{links} /Dest [{4 + (i % 3)} 0 R /Fit] >>");
        }

        var xref = stream.Position;
        var size = offsets.Keys.Max() + 1;
        Raw($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var n = 1; n < size; n++) Raw(offsets.TryGetValue(n, out var at) ? $"{at:D10} 00000 n \n" : "0000000000 65535 f \n");
        Raw($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
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

        // A page gone to: the sheet with it, and it is the current page, not the sheet's first.
        window.SetPageDisplay(new PageGrid(3, 3));
        view.GoToPage(4, record: false);
        Equal(view.CurrentPage, 4, "a page gone to on the sheet in view is the current page");
        view.GoToPage(20);
        WaitForScreen(window);
        Equal(Shown(view), "18,19,20,21,22,23,24,25,26", "the sheet with page 21");
        Equal(view.CurrentPage, 20, "page 21 is the current page");
        view.GoBack();
        Equal(view.CurrentSheet.First, 0, "Back: the sheet it was left from");
        Equal(view.CurrentPage, 4, "and the page it was on there");
        view.HandleKey(Key.PageDown, ModifierKeys.None);
        Equal(view.CurrentPage, 9, "a turn: the next sheet's first page");

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

        // A link on a page less in view than the one above it: the place left is the link's page.
        view.SetZoom(0.5);
        var layout = view.Layout!;
        var linkY = layout.Top(1) + (115 / 792.0 * layout.Height(1));
        view.ScrollTo(view.Offset.X, linkY - (0.8 * view.ViewportPixelsHeight), animate: false);
        Pump(20);
        Equal(view.CurrentPage, 0, "the page above is the one most in view");
        Info(window, 1);
        Click(window, view.ViewPointOf(1, 394 / 612.0, 115 / 792.0));
        PumpUntil(() => view.Flashing is { Page: 2 }, "the link on page 2 followed");
        Equal(view.History.BackEntries[^1].ShownPage, 1, "the place left is named by the link's page");
        Check(Trail(window).EndsWith("2 [3]", StringComparison.Ordinal), $"the trail ends with it: {Trail(window)}");

        // Pointed at, a place shows its page and the part of the document it is in.
        var place = window.TrailChips.Children.OfType<Button>().First(b => b.Content is TextBlock { Text: "2" });
        var preview = (StackPanel)place.ToolTip;
        Check(preview.Children.OfType<ThumbnailImage>().Any(), "a picture of the page");
        Check(preview.Children.OfType<TextBlock>().Any(t => t.Text == window.SectionOf(1)), "and its part of the document");

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

    private static void TrailHoldsEachPageOnce()
    {
        // The history alone.
        var history = new NavigationHistory();
        ViewPosition At(int page) => new(page, 0, page);
        string Pages() => string.Join(" ", history.BackEntries.Select(e => e.ShownPage)) + " | " +
                          string.Join(" ", history.ForwardEntries.Reverse().Select(e => e.ShownPage));

        // Back and forth between two pages by links: two places, not a growing pile.
        history.Record(At(0));
        history.Arrived(7);
        history.Record(At(7));
        history.Arrived(0);
        Equal(Pages(), "7 | ", "0 to 7 and back by a link: the place left is 7, and 0 is here");
        history.Record(At(0));
        history.Arrived(7);
        Equal(Pages(), "0 | ", "and again: still one place behind");

        // A link to somewhere on the page being read adds nothing.
        history.Record(At(7));
        history.Arrived(7);
        Equal(Pages(), "0 | ", "a jump within the page");

        // A page already on the trail, reached again by a link: it is here now, not behind too.
        history.Clear();
        foreach (var (from, to) in new[] { (1, 5), (5, 9), (9, 5) })
        {
            history.Record(At(from));
            history.Arrived(to);
        }
        Equal(Pages(), "1 9 | ", "1, 5, 9, then 5 again: behind are 1 and 9");

        // Going back several at once passes visits on: none of them doubles up.
        history.Clear();
        foreach (var (from, to) in new[] { (1, 2), (2, 3), (3, 4) })
        {
            history.Record(At(from));
            history.Arrived(to);
        }
        var landed = history.Go(-3, At(4))!.Value;
        history.Arrived(landed.ShownPage);
        Equal(Pages(), " | 2 3 4", "three back from 4: 1 here, 2, 3 and 4 ahead");
        history.Record(At(1));
        history.Arrived(3);
        Equal(Pages(), "1 | ", "a new jump drops what was ahead");

        // Back from a page scrolled to by hand that has a visit already goes past that visit.
        history.Clear();
        foreach (var (from, to) in new[] { (1, 4), (4, 8) })
        {
            history.Record(At(from));
            history.Arrived(to);
        }
        Equal(history.StepsAway(-1, 4), -2, "on 4 by hand, Back goes to 1");
        Equal(history.StepsAway(-1, 8), -1, "on 8, Back goes to 4");
        Equal(history.StepsAway(1, 8), 0, "nothing ahead");

        // In the window: page 1 to 3 by a link, back to 1 by page 3's "first page" button.
        var window = ShowWindow(Fixture("links.pdf"));
        var view = window.PageView;
        Info(window, 0);
        Click(window, view.ViewPointOf(0, 135 / 612.0, 175 / 792.0));
        PumpUntil(() => view.CurrentPage == 2, "page 3");
        Equal(Trail(window), "1 [3]", "the jump");
        Info(window, 2);
        Click(window, view.ViewPointOf(2, 120 / 612.0, 595 / 792.0));
        PumpUntil(() => view.CurrentPage == 0, "back on page 1 by its button");
        Equal(Trail(window), "3 [1]", "page 1 is here, not behind as well");
        Click(window, view.ViewPointOf(0, 135 / 612.0, 175 / 792.0));
        PumpUntil(() => view.CurrentPage == 2, "page 3 again");
        Equal(Trail(window), "1 [3]", "back and forth: still two places");

        // Scrolled by hand onto a page with a visit: one place for it, and Back goes past it.
        view.GoToPage(1);
        Equal(Trail(window), "1 3 [2]", "on to page 2");
        view.GoToPage(2, record: false);
        Pump(20);
        Equal(Trail(window), "1 [3]", "back on 3 without a jump: its visit is this place");
        Check(((string)window.TrailBackButton.ToolTip).Contains('1'), $"Back leads to page 1: {window.TrailBackButton.ToolTip}");
        view.GoBack();
        Equal(view.CurrentPage, 0, "Back: page 1, past the visit to the page already shown");
        // Page 2 was left without a jump, so it is not a place; page 3 is ahead once.
        Equal(Trail(window), "[1] 3", "and ahead, each page once");
    }

    private static void KeysTurnWholeSheets()
    {
        var window = ShowWindow(Pages30());
        var view = window.PageView;

        // Two stacked, fitted to the width: the sheet is far taller than the window.
        window.SetPageDisplay(new PageGrid(1, 2));
        view.SetMode(ZoomMode.FitWidth);
        WaitForScreen(window);
        Check(view.Layout!.SlotBottom(0) - view.ViewportPixelsHeight > view.Layout.SlotTop(0) + 100, "the sheet is taller than the window");
        foreach (var (key, start) in new[] { (Key.Down, 2), (Key.Down, 4), (Key.Up, 2), (Key.PageDown, 4), (Key.PageUp, 2), (Key.Space, 4), (Key.Right, 6), (Key.Left, 4) })
        {
            view.HandleKey(key, ModifierKeys.None);
            Equal(view.CurrentSheet, (start, start + 1), $"{key}: pages {start + 1} and {start + 2}");
            Near(view.Offset.Y, view.Layout!.SlotTop(start), 0.5, $"{key}: the sheet from its top, not shifted");
        }

        // Zoomed in on a grid wider than the window: Right and Left still turn it.
        window.SetPageDisplay(new PageGrid(3, 1));
        view.SetZoom(view.Zoom * 2.5);
        Pump(30);
        Check(view.Layout!.ExtentWidth > view.ViewportPixelsWidth, "the sheet is wider than the window");
        var first = view.CurrentSheet.First;
        view.HandleKey(Key.Right, ModifierKeys.None);
        Equal(view.CurrentSheet.First, first + 3, "Right: the next three pages");
        view.HandleKey(Key.Left, ModifierKeys.None);
        Equal(view.CurrentSheet.First, first, "Left: back three");

        // Ctrl+Down still scrolls within a tall sheet; one page at a time keeps scrolling a tall page.
        window.SetPageDisplay(new PageGrid(1, 2));
        view.SetMode(ZoomMode.FitWidth);
        Pump(30);
        var top = view.Offset.Y;
        var sheet = view.CurrentSheet;
        view.HandleKey(Key.Down, ModifierKeys.Control);
        Pump(400);
        Check(view.Offset.Y > top && view.CurrentSheet == sheet, $"Ctrl+Down scrolls within the sheet: {top} to {view.Offset.Y}, {sheet} to {view.CurrentSheet}");
        window.SetPageDisplay(PageGrid.One);
        view.SetMode(ZoomMode.FitWidth);
        view.GoToPage(0, record: false);
        Pump(30);
        view.HandleKey(Key.Down, ModifierKeys.None);
        Pump(400);
        Check(view.CurrentPage == 0 && view.Offset.Y > view.Layout!.SlotTop(0), "one page at a time, Down scrolls a tall page first");
    }

    private static string Trail(MainWindow window) =>
        string.Join(" ", window.TrailChipsShown().Select(c => c.Here ? $"[{c.Text}]" : c.Text));
}
