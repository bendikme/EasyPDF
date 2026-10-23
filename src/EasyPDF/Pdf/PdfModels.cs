namespace EasyPDF.Pdf;

/// <summary>
/// A rectangle on a page in page fractions: (0,0) is the top left corner of the page as it is
/// displayed (with the page's own /Rotate applied, but not the viewer's rotation) and (1,1) the
/// bottom right. Fractions make every geometry independent of zoom and screen resolution.
/// </summary>
public readonly record struct PageRect(float X0, float Y0, float X1, float Y1)
{
    public float Width => X1 - X0;

    public float Height => Y1 - Y0;

    public bool IsEmpty => !(X1 > X0) || !(Y1 > Y0);

    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;

    /// <summary>The rectangle with its corners in order, whatever order they were given in.</summary>
    public static PageRect FromCorners(double ax, double ay, double bx, double by) =>
        new((float)Math.Min(ax, bx), (float)Math.Min(ay, by), (float)Math.Max(ax, bx), (float)Math.Max(ay, by));

    public PageRect Union(PageRect other) =>
        IsEmpty ? other : other.IsEmpty ? this
            : new(Math.Min(X0, other.X0), Math.Min(Y0, other.Y0), Math.Max(X1, other.X1), Math.Max(Y1, other.Y1));

    /// <summary>Squared distance from a point to the rectangle; zero inside it.</summary>
    public double DistanceSquared(double x, double y, double yWeight = 1)
    {
        var dx = x < X0 ? X0 - x : x > X1 ? x - X1 : 0;
        var dy = y < Y0 ? Y0 - y : y > Y1 ? y - Y1 : 0;
        return (dx * dx) + (dy * dy * yWeight * yWeight);
    }
}

/// <summary>A page's size in points (1/72 inch), as displayed: width and height already swapped for a page with /Rotate 90 or 270.</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>How a destination wants the page fitted. Only the position is honoured: a link never changes the user's zoom.</summary>
public enum DestinationFit
{
    None,
    Xyz,
    Fit,
    FitH,
    FitV,
    FitR,
}

/// <summary>
/// A place in the document as the PDF states it: a page and, optionally, a point on it in PDF
/// page space. The point is turned into page fractions only when the destination is used,
/// because that needs the target page loaded.
/// </summary>
public sealed record PdfDestination(int PageIndex, DestinationFit Fit = DestinationFit.None, double? PageX = null, double? PageY = null,
    double? PageRight = null, double? PageBottom = null, PdfMark? Mark = null);

/// <summary>
/// The element on a destination's page that the link points at, as the document marks it after
/// the jump (EPLAN's cross-references do, by script): a rectangle in the page's own coordinates
/// as it is shown - PDF space itself on a page that is not turned.
/// </summary>
public readonly record struct PdfMark(double X0, double Y0, double X1, double Y1);

/// <summary>A destination resolved for display: page and position in page fractions, and the area it names (/FitR).</summary>
public readonly record struct ResolvedDestination(int PageIndex, double? X, double? Y, PageRect? Area = null);

/// <summary>What following a link does.</summary>
public abstract record LinkTarget;

/// <summary>Jumps within this document.</summary>
public sealed record GoToTarget(PdfDestination Destination) : LinkTarget;

/// <summary>Opens a URI - a web page, a mail address, anything the shell knows.</summary>
public sealed record UriTarget(string Uri) : LinkTarget;

/// <summary>Opens a file, possibly another PDF, named relative to this one.</summary>
public sealed record LaunchTarget(string Path) : LinkTarget;

/// <summary>
/// Opens another PDF at a destination inside it: <paramref name="Destination"/> (its page counted
/// in the other file) or <paramref name="DestinationName"/> when the link was read whole; else the
/// destination is read when followed, with the other document open (it can be a name only that
/// one knows), from the action it came from: link <paramref name="LinkOrdinal"/> on page
/// <paramref name="PageIndex"/>, or outline entry <paramref name="OutlineIndex"/> (counted depth first).
/// </summary>
public sealed record RemoteGoToTarget(string Path, int PageIndex, int LinkOrdinal, int OutlineIndex = -1,
    PdfDestination? Destination = null, string? DestinationName = null) : LinkTarget;

/// <summary>Shows the 3D model of a 3D annotation: the one at <paramref name="Annotation"/> in the page's /Annots.</summary>
public sealed record ModelTarget(int PageIndex, int Annotation) : LinkTarget;

/// <summary>The viewer commands a document can name in a link (/S /Named).</summary>
public enum NamedAction
{
    NextPage,
    PrevPage,
    FirstPage,
    LastPage,
    GoBack,
    GoForward,
    GoToPage,
    Find,
    Print,
    FullScreen,
}

/// <summary>Runs one of the viewer's own commands. Next and previous page count from the link's own page.</summary>
public sealed record NamedActionTarget(NamedAction Action, int SourcePage) : LinkTarget
{
    /// <summary>The command a name stands for; null for names the viewer does not run (Close, Quit, SaveAs...).</summary>
    public static NamedActionTarget? From(string name, int sourcePage)
    {
        NamedAction? action = name switch
        {
            "NextPage" => NamedAction.NextPage,
            "PrevPage" => NamedAction.PrevPage,
            "FirstPage" => NamedAction.FirstPage,
            "LastPage" => NamedAction.LastPage,
            "GoBack" => NamedAction.GoBack,
            "GoForward" => NamedAction.GoForward,
            "GoToPage" => NamedAction.GoToPage,
            "Find" => NamedAction.Find,
            "Print" => NamedAction.Print,
            "FullScreen" => NamedAction.FullScreen,
            _ => null,
        };
        return action is { } a ? new NamedActionTarget(a, sourcePage) : null;
    }
}

/// <summary>A link annotation or a web address found in the page text.</summary>
public sealed class PdfLink
{
    public PdfLink(IReadOnlyList<PageRect> areas, LinkTarget target, bool fromText)
    {
        Areas = areas;
        Target = target;
        FromText = fromText;
        Bounds = areas.Aggregate(default(PageRect), (all, r) => all.Union(r));
    }

    /// <summary>The clickable areas: the link rectangle, or its quadrilaterals when it has them.</summary>
    public IReadOnlyList<PageRect> Areas { get; }

    public PageRect Bounds { get; }

    public LinkTarget Target { get; }

    /// <summary>A web address recognised in the text rather than a link the author made.</summary>
    public bool FromText { get; }

    public bool HitTest(double x, double y, double slack)
    {
        if (x < Bounds.X0 - slack || x > Bounds.X1 + slack || y < Bounds.Y0 - slack || y > Bounds.Y1 + slack) return false;
        foreach (var area in Areas)
        {
            if (x >= area.X0 - slack && x <= area.X1 + slack && y >= area.Y0 - slack && y <= area.Y1 + slack) return true;
        }
        return false;
    }
}

/// <summary>
/// The characters on a page, in reading order as PDFium sees it, with their boxes. Spaces and
/// line breaks PDFium adds between words and lines are included (with empty boxes), so the
/// characters joined are exactly the page's text.
/// </summary>
public sealed class PageText
{
    public static readonly PageText Empty = new([], []);

    public PageText(int[] codePoints, PageRect[] boxes)
    {
        CodePoints = codePoints;
        Boxes = boxes;
    }

    public int[] CodePoints { get; }

    public PageRect[] Boxes { get; }

    public int Count => CodePoints.Length;

    public bool IsWordCharacter(int index)
    {
        if ((uint)index >= (uint)CodePoints.Length) return false;
        var cp = CodePoints[index];

        // PDFium hands a character beyond the BMP (a math letter, an emoji) over as two UTF-16
        // halves; each half is what the pair is.
        if (cp is >= 0xD800 and <= 0xDBFF && index + 1 < CodePoints.Length && CodePoints[index + 1] is >= 0xDC00 and <= 0xDFFF)
            cp = char.ConvertToUtf32((char)cp, (char)CodePoints[index + 1]);
        else if (cp is >= 0xDC00 and <= 0xDFFF && index > 0 && CodePoints[index - 1] is >= 0xD800 and <= 0xDBFF)
            cp = char.ConvertToUtf32((char)CodePoints[index - 1], (char)cp);

        if (cp is '_' or '\'' or '’') return true;
        if (!System.Text.Rune.IsValid(cp)) return false;
        var rune = new System.Text.Rune(cp);
        return System.Text.Rune.IsLetterOrDigit(rune) || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark;
    }

    /// <summary>The text of characters <paramref name="start"/> to <paramref name="end"/> inclusive.</summary>
    public string GetText(int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(CodePoints.Length - 1, end);
        if (end < start) return string.Empty;

        var sb = new System.Text.StringBuilder(end - start + 1);
        for (var i = start; i <= end; i++) AppendCodePoint(sb, CodePoints[i]);
        return sb.ToString();
    }

    internal static void AppendCodePoint(System.Text.StringBuilder sb, int cp)
    {
        if (cp == 0) return;
        if (cp is < 0 or > 0x10FFFF || cp is >= 0xD800 and <= 0xDFFF) sb.Append('�');
        else if (cp <= 0xFFFF) sb.Append((char)cp);
        else sb.Append(char.ConvertFromUtf32(cp));
    }
}

/// <summary>What the viewer needs of one page beyond its pixels: links and text.</summary>
public sealed class PageInfo
{
    public PageInfo(int pageIndex, IReadOnlyList<PdfLink> links, PageText text, Task? moreLinksPending = null)
    {
        PageIndex = pageIndex;
        Links = links;
        Text = text;
        MoreLinksPending = moreLinksPending;
    }

    public int PageIndex { get; }

    /// <summary>
    /// Set when the page may have links PDFium leaves out (buttons, named actions) that are still
    /// being read for the document; once it completes, reading the page again gives them.
    /// </summary>
    public Task? MoreLinksPending { get; }

    /// <summary>Author links first, then web addresses found in the text; the first hit wins.</summary>
    public IReadOnlyList<PdfLink> Links { get; }

    public PageText Text { get; }

    public PdfLink? LinkAt(double x, double y, double slack = 0)
    {
        foreach (var link in Links)
        {
            if (link.HitTest(x, y, slack)) return link;
        }
        return null;
    }
}

/// <summary>An entry in the document outline (bookmarks).</summary>
public sealed class OutlineItem
{
    public OutlineItem(string title, LinkTarget? target, bool startsOpen, IReadOnlyList<OutlineItem> children)
    {
        Title = title;
        Target = target;
        StartsOpen = startsOpen;
        Children = children;
    }

    public string Title { get; }

    public LinkTarget? Target { get; }

    public bool StartsOpen { get; }

    public IReadOnlyList<OutlineItem> Children { get; }

    /// <summary>The page this entry goes to, or -1 when it goes nowhere in this document.</summary>
    public int PageIndex => Target is GoToTarget g ? g.Destination.PageIndex : -1;
}

/// <summary>One occurrence of the search text.</summary>
public sealed class SearchMatch
{
    public SearchMatch(int pageIndex, int charIndex, int charCount, IReadOnlyList<PageRect> rects)
    {
        PageIndex = pageIndex;
        CharIndex = charIndex;
        CharCount = charCount;
        Rects = rects;
        Bounds = rects.Aggregate(default(PageRect), (all, r) => all.Union(r));
    }

    public int PageIndex { get; }

    public int CharIndex { get; }

    public int CharCount { get; }

    public IReadOnlyList<PageRect> Rects { get; }

    public PageRect Bounds { get; }
}

/// <summary>The document information dictionary and a few facts about the file.</summary>
public sealed record PdfMetadata(
    string Title,
    string Author,
    string Subject,
    string Keywords,
    string Creator,
    string Producer,
    string CreationDate,
    string ModificationDate,
    int FileVersion,
    bool IsEncrypted,
    bool HasForm);

public enum OpenFailure
{
    None,
    NotFound,
    AccessDenied,
    PasswordRequired,
    WrongPassword,
    NotPdf,
    Damaged,
    UnsupportedSecurity,
    Empty,
    Unknown,
}

/// <summary>Why a document could not be opened, or the document.</summary>
public sealed record OpenResult(PdfDocument? Document, OpenFailure Failure, string? Detail = null)
{
    public bool Succeeded => Document is not null;
}
