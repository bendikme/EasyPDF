using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using EasyPDF.Pdf;
using EasyPDF.Services;
using EasyPDF.Viewer;
using EasyPDF.Views;
using static Program;
using Point = System.Windows.Point;

/// <summary>One page at a time, the mark on a link's destination, the recent list, slow shares, memory.</summary>
internal static class FeatureChecks
{
    public static IEnumerable<(string, Action)> All() =>
    [
        ("One page at a time: keys, wheel and scroll bar turn pages, at once, and show which", SinglePageView),
        ("One page at a time: links, search and Back stay on one page; the choice is remembered", SinglePageNavigation),
        ("A followed link marks its destination for a moment: its line, its area or its page", LinkTargetFlashes),
        ("Pinned files stay, missing ones are marked, and the list empties", RecentListPinsAndMarks),
        ("A share that does not answer holds up neither the window nor PDFium", SlowSharesNeverHang),
        ("Opening and closing documents again and again leaves nothing behind", NoMemoryLeftBehind),
    ];

    private static MainWindow ShowWindow(string fixture)
    {
        var window = App.OpenEmptyWindow(null);
        Check(Await(window.OpenAsync(Fixture(fixture))), $"{fixture} opens");
        PumpUntil(() => window.PageView.IsScreenComplete() && !window.PageView.Tiles.IsBusy, "the screen to render", 20000);
        return window;
    }

    private static Point At(MainWindow window, int page, double x0, double y0, double x1, double y1, double w = 612, double h = 792) =>
        window.PageView.ViewPointOf(page, (x0 + x1) / 2 / w, (y0 + y1) / 2 / h);

    private static void Click(MainWindow window, Point point)
    {
        var view = window.PageView;
        view.PointerMove(point);
        view.PointerDown(point, 1, shift: false);
        view.PointerUp(point);
        Pump(10);
    }

    private static PageInfo Info(MainWindow window, int page)
    {
        window.PageView.RequestPageInfo(page, JobPriority.Interactive);
        PumpUntil(() => window.PageView.InfoFor(page) is { MoreLinksPending: null }, $"page {page + 1}'s links");
        return window.PageView.InfoFor(page)!;
    }

    private static string Shown(PdfView view) => string.Join(",", view.PageDrawings().Select(d => d.Page).OrderBy(p => p));

    private static void Wheel(PdfView view, int delta) =>
        view.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });

    // ===================================================================== one page at a time

    private static void SinglePageView()
    {
        var window = ShowWindow("links.pdf");
        var view = window.PageView;
        window.SetSinglePage(true);
        Pump(10);
        Check(view.Grid == PageGrid.One, "one page at a time");
        Equal(Shown(view), "0", "only the first page is there");
        Check(!window.PageIndicatorShown, "switching shows no page number");

        // Fitting the width, a page is taller than the window: Down scrolls it first.
        var layout = view.Layout!;
        var bottom = layout.SlotBottom(0) - view.ViewportPixelsHeight;
        Check(bottom > layout.SlotTop(0), "the page is taller than the window");
        for (var i = 0; i < 200 && view.CurrentPage == 0; i++)
        {
            var before = view.Offset.Y;
            view.HandleKey(Key.Down, ModifierKeys.None);
            if (view.CurrentPage != 0)
            {
                Near(before, bottom, 1, "the page was scrolled to its foot before the next one came");
                break;
            }
            Pump(400); // the scroll glides to its place
            Equal(Shown(view), "0", "scrolling within the page shows no other");
        }

        // The turn is at once: no scrolling animation in between.
        Equal(view.CurrentPage, 1, "Down at the foot turns the page");
        Near(view.Offset.Y, view.Layout!.SlotTop(1), 0.5, "the next page from its top, at once");
        Equal(Shown(view), "1", "and only it");
        PumpUntil(() => window.PageIndicatorShown, "the page number");
        Equal(window.PageIndicatorText, "2 of 3", "which page, of how many");

        // Up at the top goes back to the foot of the page before; Page Up to its top.
        view.HandleKey(Key.Up, ModifierKeys.None);
        Equal(view.CurrentPage, 0, "Up at the top turns back");
        Near(view.Offset.Y, view.Layout!.SlotBottom(0) - view.ViewportPixelsHeight, 0.5, "to the foot of the page before");
        view.HandleKey(Key.Right, ModifierKeys.None);
        Equal(view.CurrentPage, 1, "Right is the next page");
        view.HandleKey(Key.PageUp, ModifierKeys.None);
        Equal(view.CurrentPage, 0, "Page Up at the top turns back");
        Near(view.Offset.Y, view.Layout!.SlotTop(0), 0.5, "to the top of the page before");
        view.HandleKey(Key.End, ModifierKeys.None);
        Equal(view.CurrentPage, 2, "End is the last page");
        view.HandleKey(Key.Home, ModifierKeys.None);
        Equal(view.CurrentPage, 0, "Home is the first");

        // A whole page in the window: the wheel turns pages, a notch each, at once.
        view.SetMode(ZoomMode.FitPage);
        Pump(20);
        Check(view.Layout!.SlotBottom(0) - view.ViewportPixelsHeight <= view.Layout.SlotTop(0) + 0.5, "the page fits");
        Wheel(view, -120);
        Equal(view.CurrentPage, 1, "a notch down is the next page");
        Near(view.Offset.Y, view.Layout!.SlotTop(1), 0.5, "at once");
        Thread.Sleep(200);
        Wheel(view, 120);
        Equal(view.CurrentPage, 0, "a notch up, the page before");

        // A touchpad's small deltas add up to a notch, and pages turn no faster than one a moment.
        Thread.Sleep(200);
        for (var i = 0; i < 10; i++) Wheel(view, -30);
        Equal(view.CurrentPage, 1, "a stream of small deltas turns one page");

        // The scroll bar jumps to the page it is dragged to.
        view.SetVerticalOffset(view.ExtentHeight * 0.95);
        Equal(view.CurrentPage, 2, "the scroll bar at the end is the last page");
        Equal(Shown(view), "2", "and only it");

        // Back to continuous.
        window.SetSinglePage(false);
        Pump(20);
        Check(!view.IsFixed && view.CurrentPage == 2, "continuous again, on the same page");
    }

    private static void SinglePageNavigation()
    {
        var path = Fixture("links.pdf");
        var window = ShowWindow("links.pdf");
        var view = window.PageView;
        window.SetSinglePage(true);
        Pump(10);
        Info(window, 0);

        // A link to a place on page 3 shows page 3, and nothing of the others.
        Click(window, At(window, 0, 70, 166, 200, 184));
        PumpUntil(() => view.CurrentPage == 2, "the link's page");
        Equal(Shown(view), "2", "only the page linked to");
        view.GoBack();
        Equal(view.CurrentPage, 0, "Back to the page the link was on");
        Equal(Shown(view), "0", "and only it");

        // A search match on another page brings that page.
        window.SearchBox.Text = "Chapter 3";
        window.StartSearch(immediate: true);
        PumpUntil(() => view.CurrentPage == 2, "the match's page");
        Equal(Shown(view), "2", "only the match's page");

        // The choice is the document's, and the default for documents opened from now on.
        Check(SettingsStore.Current.Settings.Grid == "1x1", "the default for new documents");
        window.Close();
        Pump(20);
        Check(SettingsStore.Current.FindRecent(path) is { Grid: "1x1" }, "remembered for the document");
        var again = ShowWindow("links.pdf");
        Check(again.PageView.Grid == PageGrid.One, "it opens one page at a time again");
        SettingsStore.Current.Settings.Grid = null;
    }

    // ===================================================================== the mark on a destination

    private static void LinkTargetFlashes()
    {
        var window = ShowWindow("links.pdf");
        var view = window.PageView;
        Info(window, 0);

        // "Go to page 3" names a point above the line "Target of the page 3 link" (y 300, PDF 492).
        Click(window, At(window, 0, 70, 166, 200, 184));
        PumpUntil(() => view.Flashing is { Page: 2 } f && f.Area != new PageRect(0, 0, 1, 1), "the line at the destination is marked");
        var line = view.Flashing!.Value.Area;
        Check(line.Y0 < 300 / 792.0 && line.Y1 > 292 / 792.0 && line.Height < 0.05, $"the line of text: {line}");
        Check(line.X0 < 80 / 612.0 && line.X1 > 150 / 612.0, $"from its start: {line}");
        PumpUntil(() => view.PageOverlays().Single(o => o.Page == 2).Overlay is { Bounds.IsEmpty: false }, "a frame drawn on the page", 2000);
        PumpUntil(() => view.Flashing is null, "and gone after a moment", 5000);

        // Back and Forward do not mark anything.
        view.GoBack();
        Pump(20);
        Check(view.Flashing is null, "Back marks nothing");

        // A link to an area (/FitR 60 280 400 320) marks the area.
        view.GoToPage(1);
        Info(window, 1);
        Click(window, At(window, 1, 318, 106, 470, 124));
        PumpUntil(() => view.Flashing is { Page: 2 }, "the area is marked");
        var area = view.Flashing!.Value.Area;
        Near(area.X0, 60 / 612.0, 0.002, "area left");
        Near(area.X1, 400 / 612.0, 0.002, "area right");
        Near(area.Y0, (792 - 320) / 792.0, 0.002, "area top");
        Near(area.Y1, (792 - 280) / 792.0, 0.002, "area bottom");

        // A button that names a page alone (as EPLAN's do) marks the page.
        var buttons = ShowWindow("buttons.pdf");
        Info(buttons, 0);
        Click(buttons, At(buttons, 0, 70, 100, 200, 120));
        PumpUntil(() => buttons.PageView.Flashing is { Page: 2 } f && f.Area == new PageRect(0, 0, 1, 1), "the whole page is marked");
    }

    // ===================================================================== the recent list

    private static void RecentListPinsAndMarks()
    {
        var store = SettingsStore.Current;
        var present = Fixture("links.pdf");
        var other = Fixture("other.pdf");
        var gone = Path.Combine(Temp, "gone.pdf");
        var noDrive = "YXVUTSRPONMLKJIHGF".Select(c => $"{c}:\\").First(root => !Directory.Exists(root)) + @"away\offline.pdf";
        var share = @"\\no-such-server.invalid\share\far.pdf";
        foreach (var path in new[] { present, other, gone, noDrive, share })
            store.Remember(new RecentFile { Path = path, OpenedUtc = DateTime.UtcNow });
        store.SetPinned(gone, true);
        store.SetPinned(share, true);

        var window = App.OpenEmptyWindow(null);
        var sw = Stopwatch.StartNew();
        var items = window.RecentItems(10);
        Check(sw.ElapsedMilliseconds < 100, $"the list is shown without waiting for any file ({sw.ElapsedMilliseconds} ms)");
        Equal(string.Join("|", items.Take(2).Select(i => Path.GetFileName(i.Path))), "far.pdf|gone.pdf", "pinned files first");
        Check(items.Take(2).All(i => i.Pinned) && items.Skip(2).All(i => !i.Pinned), "pinned, then the rest");

        RecentItem Item(string path) => window.RecentItems(10).Single(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        PumpUntil(() => Item(gone).When == "Not found", "a missing pinned file is marked", 10000);
        PumpUntil(() => Item(noDrive).When == "Not available", "a file on a drive that is not there is marked unavailable", 10000);
        PumpUntil(() => Item(share).When == "Not available", "a file on a share that is not there too", 15000);
        Check(!Item(present).IsAway && !Item(other).IsAway, "files that are there are not marked");
        Check(Item(gone).IsAway && Item(gone).Fade < 1, "a file that is gone is faded");

        // Opening a pinned file that is gone keeps it on the list; an unpinned one goes.
        MainWindow.NoticeSink = (_, _) => { };
        Check(!Await(window.OpenAsync(gone)), "a file that is gone does not open");
        Check(store.FindRecent(gone) is { Pinned: true }, "a pinned file that is gone stays");

        // Clearing empties the list, but for the pinned files.
        window.ShowStartPage();
        Check(window.ClearRecentButton.Visibility == Visibility.Visible, "Clear is offered");
        store.ClearRecent();
        Equal(string.Join("|", store.Ordered().Select(r => Path.GetFileName(r.Path))), "far.pdf|gone.pdf", "only the pinned files are left");
        Check(window.ClearRecentButton.Visibility != Visibility.Visible, "nothing more to clear");

        // The list is in the More menu too, with a way to clear it.
        var menu = window.BuildRecentMenu();
        Equal(menu.Items.Count, 4, "two files, a separator and Clear");

        // Unpinned, a file can be cleared away.
        store.SetPinned(gone, false);
        store.ClearRecent();
        Equal(store.Ordered().Count, 1, "the file unpinned is cleared away");

        // The pin button pins and unpins.
        store.Remember(new RecentFile { Path = present, OpenedUtc = DateTime.UtcNow });
        Check(store.FindRecent(present) is { Pinned: false }, "not pinned yet");
        store.SetPinned(present, true);
        store.Remember(new RecentFile { Path = present, OpenedUtc = DateTime.UtcNow });
        Check(store.FindRecent(present) is { Pinned: true }, "opening a pinned file again keeps it pinned");
    }

    // ===================================================================== slow shares

    private static void SlowSharesNeverHang()
    {
        // A share that answers nothing for four seconds, as a dead server does for much longer.
        const string slow = @"\\slow-server\share\";
        FileAvailability.AskOverride = path =>
        {
            if (path.StartsWith(slow, StringComparison.OrdinalIgnoreCase)) Thread.Sleep(4000);
            return File.Exists(path) ? Availability.Present : Availability.Missing;
        };
        PdfDocument.BeforeOpenFile = path =>
        {
            if (path.StartsWith(slow, StringComparison.OrdinalIgnoreCase)) Thread.Sleep(4000);
        };

        var store = SettingsStore.Current;
        foreach (var name in new[] { "a.pdf", "b.pdf" }) store.Remember(new RecentFile { Path = slow + name, OpenedUtc = DateTime.UtcNow });
        store.Remember(new RecentFile { Path = Fixture("links.pdf"), OpenedUtc = DateTime.UtcNow });

        // The list: shown at once; the local file answers while the share is still silent.
        var window = App.OpenEmptyWindow(null);
        var sw = Stopwatch.StartNew();
        window.ShowStartPage();
        Check(sw.ElapsedMilliseconds < 200, $"the start page shows at once ({sw.ElapsedMilliseconds} ms)");
        PumpUntil(() => FileAvailability.Get(Fixture("links.pdf")) == Availability.Present, "the local file answers first", 3000);
        PumpUntil(() => window.RecentItems(10).Count(i => i.When == "Not available") == 2, "the silent share is unavailable after a while", 6000);

        // Opening a file there: the window keeps working, and so does PDFium for everything else.
        sw.Restart();
        var opening = window.OpenAsync(slow + "a.pdf");
        Check(sw.ElapsedMilliseconds < 100, $"opening returns at once ({sw.ElapsedMilliseconds} ms)");
        var other = ShowWindow("links.pdf");
        Check(other.PageView.IsScreenComplete(), "another document opens and renders meanwhile");
        sw.Restart();
        Await(PdfEngine.Instance.Run(JobPriority.Interactive, () => 0));
        Check(sw.ElapsedMilliseconds < 200, "PDFium's thread is free");

        // The window can even be closed while it waits.
        window.Close();
        Pump(20);
        Check(!Await(opening, 15000), "the open, given up, reports nothing opened");
        FileAvailability.AskOverride = null;
        PdfDocument.BeforeOpenFile = null;
    }

    // ===================================================================== memory

    /// <summary>
    /// Every kind of document - links, buttons read from the file, an encrypted one read through
    /// PDFium's copy, a form, a form too big to load - opened, read, marked, turned and closed, a
    /// dozen times over: every document object goes, and the process ends where it began.
    /// </summary>
    private static void NoMemoryLeftBehind()
    {
        var huge = BigPdfWriter.WriteForm(Path.Combine(Temp, "memory-form.pdf"), pages: 10, buttonsPerPage: 600);
        var files = new[] { Fixture("links.pdf"), Fixture("buttons.pdf"), Fixture("buttons-encrypted.pdf"), Fixture("form.pdf"), Fixture("mixed.pdf"), Fixture("links-objstm.pdf"), huge };
        MainWindow.PasswordPrompt = (_, _) => "secret";
        MainWindow.NoticeSink = (_, _) => { };
        var window = App.OpenEmptyWindow(null);

        PageGrid?[] displays = [null, PageGrid.One, new PageGrid(2, 2)];

        List<WeakReference> Round(PageGrid? display)
        {
            var documents = new List<WeakReference>();
            foreach (var file in files)
            {
                Check(Await(window.OpenAsync(file), 30000), $"{Path.GetFileName(file)} opens");
                var view = window.PageView;
                PumpUntil(() => view.IsScreenComplete() && !view.Tiles.IsBusy, "the screen", 20000);
                window.SetPageDisplay(display);
                Info(window, 0);
                if (view.InfoFor(0)?.Links.FirstOrDefault(l => l.Target is GoToTarget) is { } link)
                    view.Activate(link.Target);
                view.HandleKey(Key.PageDown, ModifierKeys.None);
                Pump(30);
                documents.Add(new WeakReference(window.Document!));
                window.CloseDocument();
            }
            return documents;
        }

        // Managed bytes alive, and native bytes (private less what the GC has committed): one exact,
        // the other noisy with what earlier checks left for the system to take back.
        (long Managed, long Native) Settled()
        {
            for (var i = 0; i < 3; i++)
            {
                // Idle, as the app is between whiles: WPF tidies its tables of weak events then.
                Pump(50);
                PumpIdle();
                Await(PdfEngine.Instance.Run(JobPriority.Interactive, () => 0));
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            var managed = GC.GetTotalMemory(true);
            return (managed, ProcessMemory.Private() - GC.GetGCMemoryInfo().TotalCommittedBytes);
        }

        // Caches and pools fill in the first rounds; after that, round on round, nothing may grow.
        for (var i = 0; i < 6; i++) Round(displays[i % 3]);
        var start = Settled();
        // EASYPDF_LEAK_CENSUS=1: which kinds of object grow over sixteen rounds, and what holds them.
        if (Environment.GetEnvironmentVariable("EASYPDF_LEAK_CENSUS") == "1")
        {
            var first = HeapRoots.Census();
            for (var i = 0; i < 18; i++) Round(displays[i % 3]);
            Settled();
            var second = HeapRoots.Census();
            foreach (var (type, now) in second.OrderByDescending(kv => kv.Value.Bytes - first.GetValueOrDefault(kv.Key).Bytes).Take(25))
            {
                var then = first.GetValueOrDefault(type);
                if (now.Count - then.Count <= 0) continue;
                Console.WriteLine($"     {now.Count - then.Count,7} more {type} ({(now.Bytes - then.Bytes) / 1024} KB)");
            }
            Console.WriteLine(HeapRoots.Explain("System.Windows.Data.ListCollectionView", 4));
            Console.WriteLine(HeapRoots.Explain("EasyPDF.Views.RecentItem", 2));
        }
        List<WeakReference> last = [];
        for (var i = 0; i < 9; i++) last = Round(displays[i % 3]);
        var middle = Settled();
        for (var i = 0; i < 9; i++) last = Round(displays[i % 3]);
        var end = Settled();

        var alive = last.Count(r => r.IsAlive);
        if (alive > 0) Console.WriteLine(HeapRoots.Explain("EasyPDF.Pdf.PdfDocument"));
        Check(alive == 0, $"{alive} of {last.Count} closed documents are still held");
        Console.WriteLine($"     managed {start.Managed >> 10} KB, after 9 rounds {middle.Managed >> 10} KB, after 18 {end.Managed >> 10} KB; " +
                          $"native {start.Native >> 20} MB, {middle.Native >> 20} MB, {end.Native >> 20} MB");
        Check(end.Managed - middle.Managed < 1L << 20, $"managed memory grows round on round: {(middle.Managed - start.Managed) >> 10} KB, then {(end.Managed - middle.Managed) >> 10} KB");
        Check(end.Native - middle.Native < 32L << 20, $"native memory grows round on round: {(middle.Native - start.Native) >> 20} MB, then {(end.Native - middle.Native) >> 20} MB");
        window.Close();
        SettingsStore.Current.Settings.Grid = null;
    }
}
