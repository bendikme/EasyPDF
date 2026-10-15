using System.Runtime.InteropServices;

namespace EasyPDF.Services;

/// <summary>Tells Explorer that file associations changed: the installer has just registered EasyPDF for PDF files.</summary>
internal static partial class ShellNotify
{
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    public static void AssociationsChanged()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Explorer finds out at the next sign-in instead.
        }
    }
}
