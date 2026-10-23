using System.Runtime.InteropServices;

namespace EasyPDF.Services;

/// <summary>
/// Tells Explorer what changed under it: file associations (the installer has just registered
/// EasyPDF for PDF files, or a shortcut's icon was mended) or one file.
/// </summary>
internal static partial class ShellNotify
{
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNF_IDLIST = 0;
    private const uint SHCNF_PATHW = 5;

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

    /// <summary>Explorer redraws one file - a shortcut, say, whose icon changed.</summary>
    public static void ItemUpdated(string path)
    {
        var text = Marshal.StringToHGlobalUni(path);
        try
        {
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, text, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }
}
