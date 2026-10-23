using System.Runtime.InteropServices;

namespace EasyPDF.Interop;

/// <summary>
/// The parts of PDFium's C API the viewer uses (bblanchon.PDFium.Win32 157.0.8086). Every
/// signature is blittable, so the calls marshal nothing. PDFium is not thread-safe: every one of
/// these is called from <see cref="Pdf.PdfEngine"/>'s thread and nowhere else.
/// </summary>
internal static unsafe partial class Pdfium
{
    private const string Lib = "pdfium";

    // ---------------------------------------------------------------- errors

    public const int ErrSuccess = 0;
    public const int ErrUnknown = 1;
    public const int ErrFile = 2;
    public const int ErrFormat = 3;
    public const int ErrPassword = 4;
    public const int ErrSecurity = 5;
    public const int ErrPage = 6;

    // ---------------------------------------------------------------- render flags

    public const int FlagAnnot = 0x01;
    public const int FlagLcdText = 0x02;
    public const int FlagPrinting = 0x800;

    public const int BitmapBgrx = 3;
    public const int BitmapBgra = 4;

    // ---------------------------------------------------------------- actions and views

    public const uint ActionUnsupported = 0;
    public const uint ActionGoTo = 1;
    public const uint ActionRemoteGoTo = 2;
    public const uint ActionUri = 3;
    public const uint ActionLaunch = 4;
    public const uint ActionEmbeddedGoTo = 5;

    public const uint ViewUnknown = 0;
    public const uint ViewXyz = 1;
    public const uint ViewFit = 2;
    public const uint ViewFitH = 3;
    public const uint ViewFitV = 4;
    public const uint ViewFitR = 5;
    public const uint ViewFitB = 6;
    public const uint ViewFitBH = 7;
    public const uint ViewFitBV = 8;

    public const uint MatchCase = 0x1;
    public const uint MatchWholeWord = 0x2;

    // ---------------------------------------------------------------- structs

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryConfig
    {
        public int Version;
        public nint UserFontPaths;
        public nint Isolate;
        public uint V8EmbedderSlot;
    }

    /// <summary>FPDF_FILEACCESS. On Windows "unsigned long" is 32 bits, so this reaches 4 GiB.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileAccess
    {
        public uint FileLength;
        public delegate* unmanaged<nint, uint, byte*, uint, int> GetBlock;
        public nint Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix
    {
        public float A, B, C, D, E, F;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeF
    {
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QuadPointsF
    {
        public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
    }

    /// <summary>FPDF_SYSTEMTIME, returned by value from FFI_GetLocalTime.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    // ---------------------------------------------------------------- library and documents

    [LibraryImport(Lib)]
    public static partial void FPDF_InitLibraryWithConfig(LibraryConfig* config);

    [LibraryImport(Lib)]
    public static partial nint FPDF_LoadCustomDocument(FileAccess* access, byte* password);

    [LibraryImport(Lib)]
    public static partial nint FPDF_LoadDocument(byte* utf8Path, byte* password);

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetPageSizeByIndexF(nint document, int index, SizeF* size);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetFileVersion(nint document, int* version);

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetMetaText(nint document, byte* tag, void* buffer, uint length);

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetPageLabel(nint document, int index, void* buffer, uint length);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetSecurityHandlerRevision(nint document);

    // ---------------------------------------------------------------- pages

    [LibraryImport(Lib)]
    public static partial nint FPDF_LoadPage(nint document, int index);

    [LibraryImport(Lib)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, int* deviceX, int* deviceY);

    [LibraryImport(Lib)]
    public static partial int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, double* pageX, double* pageY);

    // ---------------------------------------------------------------- rendering

    [LibraryImport(Lib)]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Lib)]
    public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmapWithMatrix(nint bitmap, nint page, Matrix* matrix, RectF* clipping, int flags);

    [LibraryImport(Lib)]
    public static partial int FPDF_RenderPage(nint hdc, nint page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags);

    // ---------------------------------------------------------------- forms

    [LibraryImport(Lib)]
    public static partial nint FPDFDOC_InitFormFillEnvironment(nint document, void* formInfo);

    [LibraryImport(Lib)]
    public static partial void FPDFDOC_ExitFormFillEnvironment(nint form);

    [LibraryImport(Lib)]
    public static partial void FORM_OnAfterLoadPage(nint page, nint form);

    [LibraryImport(Lib)]
    public static partial void FORM_OnBeforeClosePage(nint page, nint form);

    [LibraryImport(Lib)]
    public static partial void FPDF_FFLDraw(nint form, nint bitmap, nint page, int startX, int startY, int sizeX,
        int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetFormType(nint document);


    // ---------------------------------------------------------------- saving a copy

    public const uint SaveNoIncremental = 1 << 1;
    public const uint SaveRemoveSecurity = 1 << 2;

    /// <summary>FPDF_FILEWRITE, followed by our own context: PDFium passes the struct back to WriteBlock.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileWrite
    {
        public int Version;
        public delegate* unmanaged<FileWrite*, void*, uint, int> WriteBlock;
        public nint Context;
    }

    [LibraryImport(Lib)]
    public static partial int FPDF_SaveAsCopy(nint document, FileWrite* write, uint flags);

    // ---------------------------------------------------------------- outline, actions, destinations

    [LibraryImport(Lib)]
    public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(Lib)]
    public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [LibraryImport(Lib)]
    public static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint length);

    [LibraryImport(Lib)]
    public static partial int FPDFBookmark_GetCount(nint bookmark);

    [LibraryImport(Lib)]
    public static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport(Lib)]
    public static partial nint FPDFBookmark_GetAction(nint bookmark);

    [LibraryImport(Lib)]
    public static partial uint FPDFAction_GetType(nint action);

    [LibraryImport(Lib)]
    public static partial nint FPDFAction_GetDest(nint document, nint action);

    /// <summary>A named destination by its name, a NUL-terminated UTF-8 string.</summary>
    [LibraryImport(Lib)]
    public static partial nint FPDF_GetNamedDestByName(nint document, byte* name);

    [LibraryImport(Lib)]
    public static partial uint FPDFAction_GetFilePath(nint action, void* buffer, uint length);

    [LibraryImport(Lib)]
    public static partial uint FPDFAction_GetURIPath(nint document, nint action, void* buffer, uint length);

    [LibraryImport(Lib)]
    public static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);

    [LibraryImport(Lib)]
    public static partial uint FPDFDest_GetView(nint dest, uint* numParams, float* parameters);

    [LibraryImport(Lib)]
    public static partial int FPDFDest_GetLocationInPage(nint dest, int* hasX, int* hasY, int* hasZoom,
        float* x, float* y, float* zoom);

    // ---------------------------------------------------------------- link annotations

    [LibraryImport(Lib)]
    public static partial int FPDFLink_Enumerate(nint page, int* startPos, nint* link);

    [LibraryImport(Lib)]
    public static partial nint FPDFLink_GetDest(nint document, nint link);

    [LibraryImport(Lib)]
    public static partial nint FPDFLink_GetAction(nint link);

    [LibraryImport(Lib)]
    public static partial int FPDFLink_GetAnnotRect(nint link, RectF* rect);

    // ---------------------------------------------------------------- annotations

    public const int AnnotWidget = 20;
    public const int AnnotThreeD = 25;

    /// <summary>How many entries the page's /Annots has.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport(Lib)]
    public static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Lib)]
    public static partial int FPDFAnnot_GetSubtype(nint annot);

    [LibraryImport(Lib)]
    public static partial void FPDFPage_CloseAnnot(nint annot);

    [LibraryImport(Lib)]
    public static partial int FPDFAnnot_GetRect(nint annot, RectF* rect);

    /// <summary>The page's /Rotate in quarter turns, 0-3.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetRotation(nint page);

    /// <summary>How many document-level scripts the document has (its /Names /JavaScript).</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFDoc_GetJavaScriptActionCount(nint document);

    [LibraryImport(Lib)]
    public static partial int FPDFLink_CountQuadPoints(nint link);

    [LibraryImport(Lib)]
    public static partial int FPDFLink_GetQuadPoints(nint link, int index, QuadPointsF* quad);

    // ---------------------------------------------------------------- text

    [LibraryImport(Lib)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Lib)]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFText_CountChars(nint textPage);

    [LibraryImport(Lib)]
    public static partial uint FPDFText_GetUnicode(nint textPage, int index);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetLooseCharBox(nint textPage, int index, RectF* rect);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right,
        double* bottom, double* top);

    [LibraryImport(Lib)]
    public static partial int FPDFText_IsGenerated(nint textPage, int index);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetText(nint textPage, int start, int count, ushort* result);

    [LibraryImport(Lib)]
    public static partial int FPDFText_CountRects(nint textPage, int start, int count);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetRect(nint textPage, int index, double* left, double* top, double* right,
        double* bottom);

    [LibraryImport(Lib)]
    public static partial nint FPDFText_FindStart(nint textPage, ushort* findWhat, uint flags, int startIndex);

    [LibraryImport(Lib)]
    public static partial int FPDFText_FindNext(nint search);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetSchResultIndex(nint search);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetSchCount(nint search);

    [LibraryImport(Lib)]
    public static partial void FPDFText_FindClose(nint search);

    [LibraryImport(Lib)]
    public static partial nint FPDFLink_LoadWebLinks(nint textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFLink_CountWebLinks(nint webLinks);

    [LibraryImport(Lib)]
    public static partial int FPDFLink_GetURL(nint webLinks, int index, ushort* buffer, int length);

    [LibraryImport(Lib)]
    public static partial int FPDFLink_CountRects(nint webLinks, int index);

    [LibraryImport(Lib)]
    public static partial int FPDFLink_GetRect(nint webLinks, int index, int rectIndex, double* left, double* top,
        double* right, double* bottom);

    [LibraryImport(Lib)]
    public static partial void FPDFLink_CloseWebLinks(nint webLinks);
}
