using System.Runtime.InteropServices;

namespace EasyPDF.Pdf;

/// <summary>Sends rendered pixels to a GDI device context (a printer), stretched to fill a rectangle.</summary>
internal static unsafe class Gdi
{
    private const uint DibRgbColors = 0;
    private const uint SrcCopy = 0x00CC0020;
    private const int HalfTone = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    /// <summary>Draws a top-down 32-bit BGRx image into the rectangle (x, y, width, height) of <paramref name="hdc"/>.</summary>
    public static bool Blit(nint hdc, int x, int y, int width, int height, byte* pixels, int pixelWidth, int pixelHeight)
    {
        if (width <= 0 || height <= 0) return true;
        var header = new BitmapInfoHeader
        {
            Size = sizeof(BitmapInfoHeader),
            Width = pixelWidth,
            Height = -pixelHeight,
            Planes = 1,
            BitCount = 32,
        };
        SetStretchBltMode(hdc, HalfTone);
        SetBrushOrgEx(hdc, 0, 0, 0);
        return StretchDIBits(hdc, x, y, width, height, 0, 0, pixelWidth, pixelHeight, pixels, ref header, DibRgbColors, SrcCopy) != 0;
    }

    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(nint hdc, int xDest, int yDest, int destWidth, int destHeight, int xSrc, int ySrc,
        int srcWidth, int srcHeight, byte* bits, ref BitmapInfoHeader info, uint usage, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(nint hdc, int x, int y, nint previous);
}
