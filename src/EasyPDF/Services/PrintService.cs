using System.Drawing.Printing;
using System.Runtime.InteropServices;
using EasyPDF.Pdf;

namespace EasyPDF.Services;

/// <summary>
/// Prints a document. Windows' own print dialog chooses the printer and the pages; each page is
/// then drawn by PDFium straight into the printer's device context - as vectors and text, not a
/// picture, so it prints as sharp as the printer can - scaled to fit the printable area and
/// turned to match the paper. Printing runs on its own thread; the window stays usable.
/// </summary>
internal static class PrintService
{
    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;

    /// <summary>Pages with form fields print as pictures; this sharp is enough for print and keeps spooling quick.</summary>
    private const int BitmapDpi = 300;

    /// <summary>
    /// Shows the print dialog and prints. Returns the page count asked for, or 0 when cancelled.
    /// The pages are printed from a document of the print's own, opened from the same file: the
    /// window may reload or close its document while the printer is still busy.
    /// <paramref name="describe"/> words why the file could not be opened again.
    /// </summary>
    public static int Print(nint ownerHandle, PdfDocument document, string? password, int currentPage, Action<string> report,
        Func<OpenResult, string> describe)
    {
        var printDocument = new PrintDocument
        {
            DocumentName = document.FileName,
            PrintController = new StandardPrintController(),
        };
        var settings = printDocument.PrinterSettings;
        settings.MinimumPage = 1;
        settings.MaximumPage = document.PageCount;
        settings.FromPage = 1;
        settings.ToPage = document.PageCount;

        using (var dialog = new System.Windows.Forms.PrintDialog
               {
                   Document = printDocument,
                   AllowSomePages = true,
                   AllowCurrentPage = true,
                   AllowSelection = false,
                   UseEXDialog = true,
               })
        {
            if (dialog.ShowDialog(new OwnerWindow(ownerHandle)) != System.Windows.Forms.DialogResult.OK)
            {
                printDocument.Dispose();
                return 0;
            }
        }

        var pages = settings.PrintRange switch
        {
            PrintRange.SomePages => Enumerable.Range(
                Math.Clamp(settings.FromPage, 1, document.PageCount) - 1,
                Math.Max(0, Math.Clamp(settings.ToPage, 1, document.PageCount) - Math.Clamp(settings.FromPage, 1, document.PageCount) + 1)).ToArray(),
            PrintRange.CurrentPage => [Math.Clamp(currentPage, 0, document.PageCount - 1)],
            _ => Enumerable.Range(0, document.PageCount).ToArray(),
        };
        if (pages.Length == 0)
        {
            printDocument.Dispose();
            return 0;
        }

        var path = document.FilePath;
        var thread = new Thread(() =>
        {
            PdfDocument? own = null;
            try
            {
                var opened = PdfDocument.OpenAsync(path, password).GetAwaiter().GetResult();
                if (opened.Document is not { } copy)
                {
                    report(UiLanguage.Format(Resources.Strings.Print_Failed, describe(opened)));
                    return;
                }
                own = copy;

                // The file may have changed since the dialog counted its pages.
                var printable = pages.Where(p => p < copy.PageCount).ToArray();
                if (printable.Length == 0)
                {
                    report(UiLanguage.Format(Resources.Strings.Print_PageFailed, pages[0] + 1));
                    return;
                }

                var next = 0;
                var failed = -1;
                printDocument.PrintPage += (_, e) =>
                {
                    var page = printable[next];
                    report(UiLanguage.Format(Resources.Strings.Print_Progress, next + 1, printable.Length));
                    var ok = DrawPage(copy, page, e);
                    next++;
                    e.HasMorePages = ok && next < printable.Length;
                    if (ok) return;
                    failed = page;
                    e.Cancel = true;
                };

                printDocument.Print();
                report(failed >= 0
                    ? UiLanguage.Format(Resources.Strings.Print_PageFailed, failed + 1)
                    : Resources.Strings.Print_Done);
            }
            catch (Exception ex)
            {
                AppLog.Error("Printing failed.", ex);
                report(UiLanguage.Format(Resources.Strings.Print_Failed, ex.Message));
            }
            finally
            {
                printDocument.Dispose();
                own?.CloseAsync().Wait();
            }
        })
        {
            IsBackground = true,
            Name = "EasyPDF.Print",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return pages.Length;
    }

    private static bool DrawPage(PdfDocument document, int page, PrintPageEventArgs e)
    {
        if (document.IsClosed || e.Graphics is null) return false;

        var hdc = e.Graphics.GetHdc();
        try
        {
            // The device context starts at the printable area's corner, in printer pixels.
            var areaWidth = GetDeviceCaps(hdc, HorzRes);
            var areaHeight = GetDeviceCaps(hdc, VertRes);
            var dpiX = GetDeviceCaps(hdc, LogPixelsX);
            var dpiY = GetDeviceCaps(hdc, LogPixelsY);
            if (areaWidth <= 0 || areaHeight <= 0 || dpiX <= 0 || dpiY <= 0) return false;

            var (x, y, width, height, rotate) = Placement(document.PageSizes[page], areaWidth, areaHeight, dpiX, dpiY);
            return document.Engine.Invoke(() => document.PrintPage(hdc, page, x, y, width, height, rotate, BitmapDpi, dpiX));
        }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
        }
    }

    /// <summary>
    /// Where a page goes in the printable area: turned a quarter when it is landscape on portrait
    /// paper (or the reverse), shrunk to fit if it is too big - never enlarged - and centred.
    /// </summary>
    internal static (int X, int Y, int Width, int Height, int Rotate) Placement(PageSize size, int areaWidth, int areaHeight, int dpiX, int dpiY)
    {
        var landscapePage = size.Width > size.Height;
        var landscapePaper = areaWidth > areaHeight;
        var rotate = landscapePage != landscapePaper ? 1 : 0;

        var (w, h) = rotate == 1 ? (size.Height, size.Width) : (size.Width, size.Height);
        var naturalWidth = w / 72.0 * dpiX;
        var naturalHeight = h / 72.0 * dpiY;
        var scale = Math.Min(1, Math.Min(areaWidth / naturalWidth, areaHeight / naturalHeight));

        var width = Math.Max(1, (int)Math.Round(naturalWidth * scale));
        var height = Math.Max(1, (int)Math.Round(naturalHeight * scale));
        return ((areaWidth - width) / 2, (areaHeight - height) / 2, width, height, rotate);
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint hdc, int index);

    private sealed class OwnerWindow : System.Windows.Forms.IWin32Window
    {
        public OwnerWindow(nint handle) => Handle = handle;

        public nint Handle { get; }
    }
}
