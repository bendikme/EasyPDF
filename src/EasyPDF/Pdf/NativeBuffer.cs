using System.Runtime.InteropServices;

namespace EasyPDF.Pdf;

/// <summary>
/// A reusable block of native memory for PDFium to render into. One lives on the engine thread
/// and only grows, so rendering allocates nothing after the first few frames; it is handed back
/// to the system when it has been much larger than needed for a while.
/// </summary>
internal sealed unsafe class NativeBuffer : IDisposable
{
    private byte* _data;
    private nuint _size;
    private int _smallUses;

    public byte* Get(nuint size)
    {
        if (size > _size)
        {
            Release();
            // Round up to 1 MiB so a slowly growing request does not reallocate every time.
            var rounded = (size + 0xFFFFF) & ~(nuint)0xFFFFF;
            _data = (byte*)NativeMemory.Alloc(rounded);
            _size = rounded;
            _smallUses = 0;
        }
        else if (size * 4 < _size && _size > 16 * 1024 * 1024)
        {
            // A big buffer used for small jobs for a while (after zooming out) is given back.
            if (++_smallUses > 64)
            {
                Release();
                return Get(size);
            }
        }
        else
        {
            _smallUses = 0;
        }

        return _data;
    }

    public void Dispose() => Release();

    private void Release()
    {
        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }
        _size = 0;
    }
}
