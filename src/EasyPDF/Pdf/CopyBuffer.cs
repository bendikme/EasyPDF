using System.Runtime.InteropServices;
using EasyPDF.Interop;

namespace EasyPDF.Pdf;

/// <summary>
/// Somewhere for PDFium to write a copy of a document: address space reserved up front and
/// committed as the copy grows, so it is one contiguous block without ever being copied to grow,
/// and handed back to the system whole the moment it is disposed. Engine thread.
/// </summary>
internal sealed unsafe class CopyBuffer : IDisposable
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const nuint CommitStep = 1 << 20;

    private byte* _base;
    private readonly nuint _reserved;
    private nuint _committed;
    private GCHandle _self;
    private Pdfium.FileWrite* _write;

    private CopyBuffer(byte* address, nuint reserved)
    {
        _base = address;
        _reserved = reserved;
        _self = GCHandle.Alloc(this);
        _write = (Pdfium.FileWrite*)NativeMemory.AllocZeroed((nuint)sizeof(Pdfium.FileWrite));
        _write->Version = 1;
        _write->WriteBlock = &WriteBlock;
        _write->Context = GCHandle.ToIntPtr(_self);
    }

    public byte* Data => _base;

    public int Length { get; private set; }

    /// <summary>What to hand FPDF_SaveAsCopy.</summary>
    public Pdfium.FileWrite* Writer => _write;

    /// <summary>A buffer that can grow to <paramref name="capacity"/> bytes; null when there is not that much address space.</summary>
    public static CopyBuffer? Reserve(int capacity)
    {
        var size = ((nuint)capacity + CommitStep - 1) & ~(CommitStep - 1);
        var address = VirtualAlloc(null, size, MemReserve, PageReadWrite);
        return address == null ? null : new CopyBuffer((byte*)address, size);
    }

    private bool Append(byte* data, uint size)
    {
        if (_base == null) return false;
        var end = (nuint)Length + size;
        if (end > _reserved || end > int.MaxValue) return false; // too big: PDFium stops writing

        if (end > _committed)
        {
            var to = Math.Min(_reserved, (end + CommitStep - 1) & ~(CommitStep - 1));
            if (VirtualAlloc(_base + _committed, to - _committed, MemCommit, PageReadWrite) == null) return false;
            _committed = to;
        }

        Buffer.MemoryCopy(data, _base + Length, size, size);
        Length = (int)end;
        return true;
    }

    public void Dispose()
    {
        if (_base != null)
        {
            VirtualFree(_base, 0, MemRelease);
            _base = null;
        }
        if (_write != null)
        {
            NativeMemory.Free(_write);
            _write = null;
        }
        if (_self.IsAllocated) _self.Free();
    }

    [UnmanagedCallersOnly]
    private static int WriteBlock(Pdfium.FileWrite* self, void* data, uint size)
    {
        // Nothing may escape into native code: an exception here would end the process.
        try
        {
            if (size == 0) return 1;
            if (data == null || GCHandle.FromIntPtr(self->Context).Target is not CopyBuffer buffer) return 0;
            return buffer.Append((byte*)data, size) ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(void* address, nuint size, uint type);
}
