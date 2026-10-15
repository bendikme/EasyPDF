using System.Runtime.InteropServices;
using System.Windows;

namespace EasyPDF.Services;

/// <summary>
/// Puts text on the clipboard. Another program can hold the clipboard open for a moment (clipboard
/// managers, remote desktop), and WPF then throws; a few quick retries ride that out.
/// </summary>
public static class ClipboardText
{
    public static bool TrySet(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        AppLog.Warn("The clipboard stayed busy; nothing was copied.");
        return false;
    }
}
