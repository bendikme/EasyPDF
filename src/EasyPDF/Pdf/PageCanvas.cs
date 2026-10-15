using System.Runtime.InteropServices;

namespace EasyPDF.Pdf;

/// <summary>
/// Pixels of one page, rendered in the page's own coordinates: <see cref="At"/>(x, y) is page
/// pixel (x, y) at the scale the page was laid out at, whatever part of the page was drawn.
///
/// PDFium positions glyphs by rounding, and the rounding of a coordinate can come out
/// differently when a region is rendered at a different offset - so two tiles rendered at their
/// own offsets can disagree by a pixel along the edge they share, which shows as a broken glyph.
/// Rendering every region of a page with the same origin (offset zero) into a buffer the size of
/// the whole page, clipped to the region wanted, makes all of a page's tiles agree exactly. The
/// buffer is virtual memory: only the rows actually drawn are ever touched, so a page-sized
/// buffer costs no more physical memory than the region itself.
/// </summary>
internal sealed unsafe class PageCanvas : IDisposable
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    private void* _memory;
    private readonly bool _virtual;

    private PageCanvas(void* memory, bool isVirtual, byte* origin, int stride)
    {
        _memory = memory;
        _virtual = isVirtual;
        Origin = origin;
        Stride = stride;
    }

    /// <summary>Where page pixel (0, 0) is. Only pixels of the region that was drawn may be read.</summary>
    public byte* Origin { get; }

    public int Stride { get; }

    public byte* At(int x, int y) => Origin + ((long)y * Stride) + ((long)x * 4);

    /// <summary>A page-sized virtual buffer, committed but untouched; null when the address space cannot be had.</summary>
    public static PageCanvas? AllocatePage(int pageWidth, int pageHeight)
    {
        // The layout keeps pages within PageLayout.MaxPagePixels a side; a stride must fit an int.
        if (pageWidth <= 0 || pageHeight <= 0 || pageWidth > int.MaxValue / 4) return null;
        var stride = pageWidth * 4;
        // One spare row, so a tile's pixels can be read as rows x stride from its own corner.
        var bytes = (nuint)((long)stride * (pageHeight + 1));
        var memory = VirtualAlloc(null, bytes, MemReserve | MemCommit, PageReadWrite);
        return memory == null ? null : new PageCanvas(memory, true, (byte*)memory, stride);
    }

    /// <summary>A buffer for just a region, used for pages too large for a page-sized buffer.</summary>
    public static PageCanvas AllocateRegion(int x, int y, int width, int height, out byte* region, out int stride)
    {
        stride = width * 4;
        var memory = (byte*)NativeMemory.Alloc((nuint)((long)stride * (height + 1)));
        region = memory;
        // The origin is where page pixel (0, 0) would be; only the region's own pixels are ever read.
        return new PageCanvas(memory, false, memory - (((long)y * stride) + ((long)x * 4)), stride);
    }

    public void Dispose()
    {
        if (_memory == null) return;
        if (_virtual) VirtualFree(_memory, 0, MemRelease);
        else NativeMemory.Free(_memory);
        _memory = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(void* address, nuint size, uint type);
}
