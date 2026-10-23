using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace EasyPDF.Services;

/// <summary>
/// Mends EasyPDF shortcuts whose icon file is gone. Installers up to 1.0.2 gave the Start menu
/// shortcut the icon Windows Installer caches in %APPDATA%\Microsoft\Installer\{ProductCode};
/// when that copy disappears the shortcut shows a blank page, and so does a taskbar pin, which is
/// a copy of the shortcut that no later install touches. Newer installers leave the icon to the
/// exe; this points the shortcuts already out there at it too.
///
/// Only shortcuts that start this very EasyPDF.exe and name an icon file that does not exist are
/// changed, and only their icon. Runs once per start, in the background; any failure is logged
/// and forgotten - an icon is never worth bothering the user about.
/// </summary>
internal static class ShortcutIcons
{
    public static void Repair()
    {
        if (!Updater.IsInstalled || Environment.ProcessPath is not { } exe) return;

        try
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var folders = new[]
            {
                Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"),
                Path.Combine(programs, "DYNATEC"),
            };

            var mended = false;
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var link in Directory.EnumerateFiles(folder, "*.lnk"))
                    mended |= RepairOne(link, exe);
            }

            // The taskbar keeps the blank icon it drew until Windows' icon cache is refreshed.
            if (mended) ShellNotify.AssociationsChanged();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Looking over the EasyPDF shortcuts failed.", ex);
        }
    }

    private static bool RepairOne(string link, string exe)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid, throwOnError: true)!);
            var shellLink = (IShellLinkW)instance!;
            var file = (IPersistFile)instance!;
            file.Load(link, StgmRead);

            var target = new StringBuilder(MaxPath);
            shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            if (!SamePath(target.ToString(), exe)) return false;

            var icon = new StringBuilder(MaxPath);
            shellLink.GetIconLocation(icon, icon.Capacity, out _);
            var iconPath = Environment.ExpandEnvironmentVariables(icon.ToString());
            // No icon of its own (the exe's is shown), or one that is there: nothing to mend.
            if (iconPath.Length == 0 || File.Exists(iconPath)) return false;

            shellLink.SetIconLocation(exe, 0);
            file.Save(link, true);
            ShellNotify.ItemUpdated(link);
            AppLog.Info($"Shortcut icon mended: {link} (was {icon}).");
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException)
        {
            AppLog.Debug_($"The shortcut {link} was left as it is: {ex.Message}");
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private const int MaxPath = 1024;
    private const int StgmRead = 0;
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
