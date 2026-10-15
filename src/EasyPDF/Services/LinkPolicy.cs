using System.IO;
using System.Runtime.InteropServices;

namespace EasyPDF.Services;

/// <summary>What following a link out of a document will do, decided before anything is opened.</summary>
public enum LinkAction
{
    /// <summary>A web page or a mail address: opened straight away, as a browser would.</summary>
    OpenWeb,

    /// <summary>Another PDF: opened in EasyPDF.</summary>
    OpenPdf,

    /// <summary>A document or another kind of address: opened by its program after the user agrees.</summary>
    AskThenOpen,

    /// <summary>A program or script, or an address that runs code: never opened.</summary>
    Refuse,

    /// <summary>A file that does not exist.</summary>
    Missing,
}

/// <param name="IsPdf">The target is a PDF, which EasyPDF opens itself once allowed.</param>
/// <param name="Fragment">For a PDF named by a URI, what followed its "#": "page=3", "nameddest=intro", "intro".</param>
public readonly record struct LinkDecision(LinkAction Action, string Target, bool IsPdf = false, string? Fragment = null);

/// <summary>
/// Decides how to follow a link that leaves the document. A PDF can name any address and any
/// file, so the rules are those of a careful browser: web and mail links open; other PDFs open
/// here; documents and other kinds of address open only once the user has seen what they are;
/// programs, scripts and script addresses never run.
/// </summary>
public static partial class LinkPolicy
{
    private static readonly HashSet<string> WebSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>Schemes that run code, smuggle content or reach into Windows: refused outright.</summary>
    private static readonly HashSet<string> ScriptSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data", "jscript", "livescript", "about", "res", "ms-msdt", "search-ms", "search", "ms-search",
        "ms-officecmd", "shell", "ms-appinstaller", "ms-its", "its", "mk", "hcp", "ms-help", "ms-cxh", "ms-cxh-full", "ms-excel",
        "ms-word", "ms-powerpoint", "ms-access", "ms-visio", "ms-project", "ms-publisher", "ms-infopath", "ms-spd",
    };

    /// <summary>Files Windows would run rather than open; anything Windows itself calls dangerous is refused too.</summary>
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".vbs", ".vbe",
        ".vb", ".js", ".jse", ".wsf", ".wsh", ".wsc", ".ws", ".sct", ".msi", ".msp", ".mst", ".scr", ".pif", ".lnk", ".url",
        ".website", ".hta", ".cpl", ".msc", ".jar", ".reg", ".inf", ".dll", ".ocx", ".sys", ".drv", ".application", ".appref-ms",
        ".gadget", ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms", ".sh", ".py", ".pyw", ".pyc",
        ".appx", ".appxbundle", ".msix", ".msixbundle", ".appinstaller", ".chm", ".hlp", ".scf", ".shb", ".shs", ".rdp",
        ".theme", ".themepack", ".deskthemepack", ".diagcab", ".diagcfg", ".diagpkg", ".iso", ".img", ".vhd", ".vhdx",
        ".mof", ".msh", ".msh1", ".msh2", ".mshxml", ".msh1xml", ".msh2xml", ".xbap", ".xll", ".ade", ".adp", ".mde", ".mda",
        ".crt", ".cer", ".der", ".ins", ".isp", ".its", ".job", ".ksh", ".csh", ".pl", ".rb", ".mcf", ".plg", ".prf",
    };

    /// <summary>A URI from a link annotation or found in the text.</summary>
    public static LinkDecision ForUri(string uri, string? documentPath)
    {
        var text = (uri ?? string.Empty).Trim();
        if (text.Length == 0 || text.Any(char.IsControl)) return new LinkDecision(LinkAction.Refuse, text);

        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme.Length > 1)
        {
            if (WebSchemes.Contains(parsed.Scheme)) return new LinkDecision(LinkAction.OpenWeb, parsed.AbsoluteUri);
            if (ScriptSchemes.Contains(parsed.Scheme)) return new LinkDecision(LinkAction.Refuse, text);
            if (parsed.IsFile) return WithFragment(ForFile(parsed.LocalPath, documentPath), parsed.Fragment);
            return new LinkDecision(LinkAction.AskThenOpen, parsed.AbsoluteUri);
        }

        // "www.example.com" and friends: a web address without its scheme.
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate("https://" + text, UriKind.Absolute, out var web))
        {
            return new LinkDecision(LinkAction.OpenWeb, web.AbsoluteUri);
        }

        if (text.Contains('@') && !text.Contains(' ') && !text.Contains('/') &&
            Uri.TryCreate("mailto:" + text, UriKind.Absolute, out var mail))
        {
            return new LinkDecision(LinkAction.OpenWeb, mail.AbsoluteUri);
        }

        // Anything else is a reference relative to the document, read as a browser reads one:
        // "My%20File.pdf" is "My File.pdf", and "other.pdf#page=3" is page 3 of other.pdf.
        var hash = text.IndexOf('#');
        var fragment = hash >= 0 ? text[(hash + 1)..] : null;
        var reference = hash >= 0 ? text[..hash] : text;
        if (reference.Contains('%')) reference = Uri.UnescapeDataString(reference);
        if (reference.Length == 0) return new LinkDecision(LinkAction.Refuse, text);
        return WithFragment(ForFile(reference, documentPath), fragment);
    }

    private static LinkDecision WithFragment(LinkDecision decision, string? fragment)
    {
        fragment = fragment?.TrimStart('#');
        if (string.IsNullOrEmpty(fragment) || !decision.IsPdf) return decision;
        return decision with { Fragment = Uri.UnescapeDataString(fragment) };
    }

    /// <summary>A file named by a launch or go-to-remote action, or a relative link.</summary>
    public static LinkDecision ForFile(string fileSpec, string? documentPath)
    {
        var path = ResolvePath(fileSpec, documentPath);
        if (path is null) return new LinkDecision(LinkAction.Refuse, fileSpec);

        // Only a drive path ("C:\...") or a plain network path ("\\server\share\...") is followed.
        // Device and object-manager paths (\\?\, \\.\, \??\ and the like) can reach anything - a
        // network server included - so they are refused before anything looks at them.
        var isDrive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var isNetwork = path.Length > 2 && path[0] == '\\' && path[1] == '\\' && path[2] is not ('?' or '.' or '\\');
        if (!isDrive && !isNetwork) return new LinkDecision(LinkAction.Refuse, path);

        // A colon after the drive names an alternate data stream: "notes.pdf:payload" is not the
        // file it shows. Nor is "calc.exe." - Windows drops trailing dots and spaces.
        if (path.IndexOf(':', isDrive ? 2 : 0) >= 0 || path.EndsWith('.') || path.EndsWith(' '))
            return new LinkDecision(LinkAction.Refuse, path);

        var extension = Path.GetExtension(path);
        var isPdf = string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isPdf && (Executable.Contains(extension) || IsDangerous(extension))) return new LinkDecision(LinkAction.Refuse, path);

        // Merely looking at a network path makes Windows sign in to that server, handing it the
        // user's credentials - so a network path, or a drive mapped to one, is not touched until
        // the user agrees.
        if (isNetwork || IsNetworkDrive(path)) return new LinkDecision(LinkAction.AskThenOpen, path, isPdf);

        if (Directory.Exists(path)) return new LinkDecision(LinkAction.AskThenOpen, path);
        if (!File.Exists(path)) return new LinkDecision(LinkAction.Missing, path);
        if (isPdf) return new LinkDecision(LinkAction.OpenPdf, path, IsPdf: true);
        return new LinkDecision(LinkAction.AskThenOpen, path);
    }

    /// <summary>
    /// A PDF file specification as a Windows path: forward slashes, "/C/dir/file" for "C:\dir\file",
    /// "file:" URIs, and paths relative to the document's own folder.
    /// </summary>
    public static string? ResolvePath(string fileSpec, string? documentPath)
    {
        var spec = (fileSpec ?? string.Empty).Trim().Trim('"');
        if (spec.Length == 0 || spec.Any(c => char.IsControl(c) || c == '\0')) return null;

        if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(spec, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
            spec = fileUri.LocalPath;

        spec = spec.Replace('/', '\\');

        // The PDF form of an absolute path: "\C\dir\file" (from "/C/dir/file").
        if (spec.Length >= 3 && spec[0] == '\\' && char.IsAsciiLetter(spec[1]) && spec[2] == '\\')
            spec = $"{spec[1]}:{spec[2..]}";

        try
        {
            if (Path.IsPathFullyQualified(spec)) return Path.GetFullPath(spec);
            var baseDir = documentPath is null ? null : Path.GetDirectoryName(documentPath);
            if (baseDir is null) return null;
            return Path.GetFullPath(Path.Combine(baseDir, spec.TrimStart('\\')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Whether Windows counts files of this type as unsafe to open (its own list, and policy).</summary>
    private static bool IsDangerous(string extension)
    {
        if (extension.Length < 2) return false;
        try
        {
            return AssocIsDangerous(extension);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool IsNetworkDrive(string path)
    {
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssocIsDangerous(string association);
}
