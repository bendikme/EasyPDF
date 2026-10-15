using System.Runtime.InteropServices;
using EasyPDF.Interop;

namespace EasyPDF.Pdf;

/// <summary>
/// The FPDF_FORMFILLINFO PDFium wants before it will draw form fields. The viewer only shows
/// forms - nothing is typed into them - so every callback is a harmless no-op. Each one is still
/// provided, because PDFium is entitled to call any version-1 callback, and calling a null one
/// would bring the process down.
/// </summary>
internal static unsafe class FormCallbacks
{
    /// <summary>Generous: the struct is ~140 bytes in version 1; the rest stays zero.</summary>
    private const int StructSize = 1024;

    public static void* Create()
    {
        var info = (byte*)NativeMemory.AllocZeroed(StructSize);
        *(int*)info = 1; // version

        var slots = (nint*)(info + sizeof(nint)); // after the int and its padding
        var i = 0;
        slots[i++] = (nint)(delegate* unmanaged<void*, void>)&Release;
        slots[i++] = (nint)(delegate* unmanaged<void*, nint, double, double, double, double, void>)&Invalidate;
        slots[i++] = (nint)(delegate* unmanaged<void*, nint, double, double, double, double, void>)&OutputSelectedRect;
        slots[i++] = (nint)(delegate* unmanaged<void*, int, void>)&SetCursor;
        slots[i++] = (nint)(delegate* unmanaged<void*, int, nint, int>)&SetTimer;
        slots[i++] = (nint)(delegate* unmanaged<void*, int, void>)&KillTimer;
        slots[i++] = (nint)(delegate* unmanaged<void*, Pdfium.SystemTime>)&GetLocalTime;
        slots[i++] = (nint)(delegate* unmanaged<void*, void>)&OnChange;
        slots[i++] = (nint)(delegate* unmanaged<void*, nint, int, nint>)&GetPage;
        slots[i++] = (nint)(delegate* unmanaged<void*, nint, nint>)&GetCurrentPage;
        slots[i++] = (nint)(delegate* unmanaged<void*, nint, int>)&GetRotation;
        slots[i++] = (nint)(delegate* unmanaged<void*, byte*, void>)&ExecuteNamedAction;
        slots[i++] = (nint)(delegate* unmanaged<void*, ushort*, uint, int, void>)&SetTextFieldFocus;
        slots[i++] = (nint)(delegate* unmanaged<void*, byte*, void>)&DoUriAction;
        slots[i++] = (nint)(delegate* unmanaged<void*, int, int, float*, int, void>)&DoGoToAction;
        // m_pJsPlatform stays null: this PDFium has no JavaScript.
        return info;
    }

    [UnmanagedCallersOnly] private static void Release(void* self) { }

    [UnmanagedCallersOnly] private static void Invalidate(void* self, nint page, double l, double t, double r, double b) { }

    [UnmanagedCallersOnly] private static void OutputSelectedRect(void* self, nint page, double l, double t, double r, double b) { }

    [UnmanagedCallersOnly] private static void SetCursor(void* self, int type) { }

    [UnmanagedCallersOnly] private static int SetTimer(void* self, int elapse, nint callback) => 0;

    [UnmanagedCallersOnly] private static void KillTimer(void* self, int id) { }

    [UnmanagedCallersOnly]
    private static Pdfium.SystemTime GetLocalTime(void* self)
    {
        var now = DateTime.Now;
        return new Pdfium.SystemTime
        {
            Year = (ushort)now.Year,
            Month = (ushort)now.Month,
            DayOfWeek = (ushort)now.DayOfWeek,
            Day = (ushort)now.Day,
            Hour = (ushort)now.Hour,
            Minute = (ushort)now.Minute,
            Second = (ushort)now.Second,
            Milliseconds = (ushort)now.Millisecond,
        };
    }

    [UnmanagedCallersOnly] private static void OnChange(void* self) { }

    [UnmanagedCallersOnly] private static nint GetPage(void* self, nint document, int index) => 0;

    [UnmanagedCallersOnly] private static nint GetCurrentPage(void* self, nint document) => 0;

    [UnmanagedCallersOnly] private static int GetRotation(void* self, nint page) => 0;

    [UnmanagedCallersOnly] private static void ExecuteNamedAction(void* self, byte* name) { }

    [UnmanagedCallersOnly] private static void SetTextFieldFocus(void* self, ushort* value, uint length, int focus) { }

    [UnmanagedCallersOnly] private static void DoUriAction(void* self, byte* uri) { }

    [UnmanagedCallersOnly] private static void DoGoToAction(void* self, int page, int zoomMode, float* position, int count) { }
}
