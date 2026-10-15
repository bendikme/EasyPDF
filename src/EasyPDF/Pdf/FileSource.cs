using System.IO;
using System.Runtime.InteropServices;
using EasyPDF.Interop;
using Microsoft.Win32.SafeHandles;

namespace EasyPDF.Pdf;

/// <summary>
/// Feeds a file to PDFium on demand. PDFium reads only what it needs - the cross-reference table,
/// then each page as it is shown - so a 2 GB file opens as fast as a small one and never sits in
/// memory. The file is opened sharing read, write and delete, so it is never locked: whatever
/// produces it (a LaTeX build, a download) can replace it while it is open, and the viewer
/// reloads it. Reads are positional, so nothing here has a file pointer to keep straight.
/// </summary>
internal sealed unsafe class FileSource : IDisposable, IByteSource
{
    /// <summary>PDFium's custom file access is 32-bit on Windows; larger files go through FPDF_LoadDocument.</summary>
    public const long CustomAccessLimit = uint.MaxValue;

    private readonly SafeFileHandle _handle;
    private GCHandle _self;
    private Pdfium.FileAccess* _access;

    private FileSource(SafeFileHandle handle, long length)
    {
        _handle = handle;
        Length = length;
        _self = GCHandle.Alloc(this);
        _access = (Pdfium.FileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(Pdfium.FileAccess));
        _access->FileLength = (uint)length;
        _access->GetBlock = &GetBlock;
        _access->Param = GCHandle.ToIntPtr(_self);
    }

    public long Length { get; }

    /// <summary>Set once a read failed or came up short: the file changed or vanished under PDFium.</summary>
    public bool ReadFailed { get; private set; }

    public Pdfium.FileAccess* Access => _access;

    /// <summary>Opens <paramref name="path"/> for reading without locking it.</summary>
    public static FileSource Open(string path)
    {
        var handle = File.OpenHandle(
            path,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);

        try
        {
            var length = RandomAccess.GetLength(handle);
            return new FileSource(handle, length);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The first bytes of the file, or fewer when it is shorter; empty when it cannot be read.</summary>
    public byte[] ReadHead(int max)
    {
        try
        {
            var head = new byte[(int)Math.Min(max, Length)];
            var read = RandomAccess.Read(_handle, head, 0);
            return read == head.Length ? head : head.AsSpan(0, Math.Max(0, read)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return [];
        }
    }

    /// <summary>For <see cref="PdfObjectReader"/>: a positional read, short only at the end, 0 when the file cannot be read.</summary>
    public int ReadAt(long position, Span<byte> buffer)
    {
        if (_handle.IsClosed || position < 0) return 0;
        var done = 0;
        try
        {
            while (done < buffer.Length)
            {
                var read = RandomAccess.Read(_handle, buffer[done..], position + done);
                if (read <= 0) break;
                done += read;
            }
            return done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_access != null)
        {
            NativeMemory.Free(_access);
            _access = null;
        }

        if (_self.IsAllocated) _self.Free();
        _handle.Dispose();
    }

    [UnmanagedCallersOnly]
    private static int GetBlock(nint param, uint position, byte* buffer, uint size)
    {
        // Nothing may escape into native code: an exception here would end the process.
        try
        {
            if (GCHandle.FromIntPtr(param).Target is not FileSource source) return 0;
            return source.Read(position, buffer, size) ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool Read(long position, byte* buffer, uint size)
    {
        if (_handle.IsClosed) return false;

        long done = 0;
        while (done < size)
        {
            var chunk = (int)Math.Min(size - done, 1 << 30);
            int read;
            try
            {
                read = RandomAccess.Read(_handle, new Span<byte>(buffer + done, chunk), position + done);
            }
            catch (IOException)
            {
                ReadFailed = true;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                ReadFailed = true;
                return false;
            }

            if (read <= 0)
            {
                // The file is shorter than when it was opened: it is being rewritten.
                ReadFailed = true;
                return false;
            }

            done += read;
        }

        return true;
    }
}
