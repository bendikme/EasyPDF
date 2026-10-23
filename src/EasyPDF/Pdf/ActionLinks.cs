using System.Globalization;
using System.Text.RegularExpressions;

namespace EasyPDF.Pdf;

/// <summary>
/// A link PDFium leaves out, as read by <see cref="ActionLinks"/>: its rectangle in PDF page
/// space, and where it goes - a target, or a named destination of this document for PDFium to
/// look up.
/// </summary>
/// <param name="Mark">What a jump marks on the page it lands on (see <see cref="PdfMark"/>), for a link to a named destination.</param>
/// <param name="Annotation">A link annotation PDFium reads too: this is the same link, with what PDFium could not tell.</param>
internal sealed record RawLink(double X0, double Y0, double X1, double Y1, LinkTarget? Target, byte[]? DestinationName = null,
    PdfMark? Mark = null, bool Annotation = false);

/// <summary>
/// The links of a page that PDFium's link API does not report: buttons whose action goes
/// somewhere - the cross-references of an EPLAN schematic are thousands of these - and link
/// annotations with named actions (next page, back...), which PDFium cannot read at all.
/// </summary>
internal static class ActionLinks
{
    private const int MaxAnnotations = 20_000;
    private const int FlagHidden = 1 << 1;
    private const int FlagNoView = 1 << 5;
    private const int FieldReadOnly = 1 << 0;

    /// <summary>
    /// The page's links PDFium leaves out, and how many entries its /Annots has - for the caller
    /// to check against PDFium's own count that both read the same page. Where its 3D
    /// annotations are in /Annots is added to <paramref name="models"/>, as every annotation's
    /// kind is read anyway.
    /// </summary>
    public static List<RawLink> ForPage(PdfObjectReader pdf, int pageIndex, out int annotationCount, List<int>? models = null)
    {
        pdf.Begin();
        annotationCount = 0;
        var links = new List<RawLink>();
        if (pdf.Page(pageIndex) is not { } page || pdf.Array(page.GetValueOrDefault("Annots")) is not { } annotations) return links;
        annotationCount = annotations.Count;

        for (var index = 0; index < annotations.Count && index < MaxAnnotations; index++)
        {
            if (pdf.Dict(annotations[index]) is not { } annotation) continue;
            var subtype = pdf.Name(annotation.GetValueOrDefault("Subtype"));
            if (subtype == "3D") models?.Add(index);
            if (subtype is not ("Link" or "Widget")) continue;
            if (pdf.Number(annotation.GetValueOrDefault("F")) is { } flags && ((int)flags & (FlagHidden | FlagNoView)) != 0) continue;

            // PDFium reads every other kind of link annotation. Of widgets, only buttons are
            // clicked to go somewhere - not read-only ones - and a button's action can be its field's.
            Dictionary<string, object?>? action;
            if (subtype == "Link")
            {
                // A named action, which PDFium cannot read; or a jump that marks where it lands, which it cannot tell.
                action = pdf.Dict(annotation.GetValueOrDefault("A"));
                if (action is null) continue;
                var s = pdf.Name(action.GetValueOrDefault("S"));
                if (s != "Named" && !(s == "GoTo" && MarkOf(pdf, action) is not null)) continue;
            }
            else
            {
                if (Inherited(pdf, annotation, "FT") is not PdfName { Value: "Btn" }) continue;
                if (pdf.Number(Inherited(pdf, annotation, "Ff")) is { } fieldFlags && ((int)fieldFlags & FieldReadOnly) != 0) continue;
                action = pdf.Dict(Inherited(pdf, annotation, "A"));
                if (action is null) continue;
            }
            var kind = pdf.Name(action.GetValueOrDefault("S"));

            if (pdf.Array(annotation.GetValueOrDefault("Rect")) is not { Count: 4 } rect) continue;
            var r = new double[4];
            var ok = true;
            for (var i = 0; i < 4 && ok; i++)
            {
                if (pdf.Number(rect[i]) is { } v) r[i] = v;
                else ok = false;
            }
            if (!ok) continue;
            double x0 = Math.Min(r[0], r[2]), x1 = Math.Max(r[0], r[2]);
            double y0 = Math.Min(r[1], r[3]), y1 = Math.Max(r[1], r[3]);
            if (x1 - x0 <= 0 || y1 - y0 <= 0) continue;

            var link = Target(pdf, action, kind, pageIndex, x0, y0, x1, y1);
            if (link is not null) links.Add(subtype == "Link" && kind == "GoTo" ? link with { Annotation = true } : link);
        }
        return links;
    }

    /// <summary>A field's entry, which a widget may take from the fields above it.</summary>
    private static object? Inherited(PdfObjectReader pdf, Dictionary<string, object?> annotation, string key)
    {
        var node = annotation;
        for (var depth = 0; depth < 32 && node is not null; depth++)
        {
            if (node.TryGetValue(key, out var value) && pdf.Resolve(value) is { } found) return found;
            node = pdf.Dict(node.GetValueOrDefault("Parent"));
        }
        return null;
    }

    private static RawLink? Target(PdfObjectReader pdf, Dictionary<string, object?> action, string? kind, int pageIndex, double x0, double y0, double x1, double y1)
    {
        switch (kind)
        {
            case "GoTo":
            {
                var mark = MarkOf(pdf, action);
                var dest = pdf.Resolve(action.GetValueOrDefault("D"));
                if (dest is List<object?> explicitDest)
                {
                    var target = pdf.PageIndexOf(explicitDest.Count > 0 ? explicitDest[0] : null);
                    return target < 0 ? null : new RawLink(x0, y0, x1, y1, new GoToTarget(Destination(pdf, explicitDest, target) with { Mark = mark }));
                }
                var name = NameBytes(dest);
                return name is null ? null : new RawLink(x0, y0, x1, y1, null, name, mark);
            }

            case "URI":
            {
                var uri = (pdf.Resolve(action.GetValueOrDefault("URI")) as PdfString)?.Lenient.Trim();
                if (string.IsNullOrEmpty(uri)) return null;

                // A relative address is relative to the document's /URI /Base, as PDFium reads it.
                if (uri.IndexOf(':') <= 0 && pdf.Dict(pdf.Catalog?.GetValueOrDefault("URI")) is { } uriDict &&
                    pdf.Resolve(uriDict.GetValueOrDefault("Base")) is PdfString { Bytes.Length: > 0 } baseUri)
                {
                    uri = baseUri.Lenient.Trim() + uri;
                }
                return new RawLink(x0, y0, x1, y1, new UriTarget(uri));
            }

            case "Launch":
            {
                var file = FileName(pdf, action.GetValueOrDefault("F")) ?? FileName(pdf, pdf.Dict(action.GetValueOrDefault("Win"))?.GetValueOrDefault("F"));
                return string.IsNullOrWhiteSpace(file) ? null : new RawLink(x0, y0, x1, y1, new LaunchTarget(file));
            }

            case "GoToR":
            {
                var file = FileName(pdf, action.GetValueOrDefault("F"));
                if (string.IsNullOrWhiteSpace(file)) return null;
                var dest = pdf.Resolve(action.GetValueOrDefault("D"));
                PdfDestination? place = null;
                string? name = null;
                if (dest is List<object?> { Count: > 0 } explicitDest && pdf.Number(explicitDest[0]) is { } page && page >= 0 && page < int.MaxValue)
                    place = Destination(pdf, explicitDest, (int)page);
                else if (dest is PdfName or PdfString)
                    name = dest is PdfName n ? n.Value : ((PdfString)dest).Text;
                return new RawLink(x0, y0, x1, y1, new RemoteGoToTarget(file, pageIndex, -1, -1, place, name));
            }

            case "Named":
            {
                var name = pdf.Name(action.GetValueOrDefault("N"));
                return name is not null && NamedActionTarget.From(name, pageIndex) is { } named ? new RawLink(x0, y0, x1, y1, named) : null;
            }

            default:
                // JavaScript, forms, sounds, layers: nothing a viewer follows.
                return null;
        }
    }

    /// <summary>
    /// What a jump marks on the page it lands on. EPLAN's cross-references end in a script,
    /// "highlight(this.pageNum, [x0, y0, x1, y1])", which frames the element the reference is to
    /// for a few seconds - the button that refers back, and what it names. The script is not run:
    /// only that call, a function of the page landed on and four numbers, is read from it.
    /// </summary>
    internal static PdfMark? MarkOf(PdfObjectReader pdf, Dictionary<string, object?> action)
    {
        var next = pdf.Resolve(action.GetValueOrDefault("Next"));
        var chain = next is List<object?> list ? list : [next];
        foreach (var item in chain.Take(8))
        {
            if (pdf.Dict(item) is not { } script || pdf.Name(script.GetValueOrDefault("S")) != "JavaScript") continue;
            if (pdf.Text(script.GetValueOrDefault("JS"), 64 * 1024) is not { } text) continue;
            Match match;
            try
            {
                match = MarkCall.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            if (!match.Success) continue;

            var n = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (!double.TryParse(match.Groups[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !double.IsFinite(n[i])) return null;
            }
            var mark = new PdfMark(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
            return mark.X1 > mark.X0 && mark.Y1 > mark.Y0 ? mark : null;
        }
        return null;
    }

    /// <summary>name(this.pageNum, [a, b, c, d]) - with a time limit, as it runs over text from the file.</summary>
    private static readonly Regex MarkCall = new(
        @"[A-Za-z_$][\w$]*\s*\(\s*this\s*\.\s*pageNum\s*,\s*\[\s*([-+]?\d*\.?\d+)\s*,\s*([-+]?\d*\.?\d+)\s*,\s*([-+]?\d*\.?\d+)\s*,\s*([-+]?\d*\.?\d+)\s*\]\s*\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>An explicit destination: [page /XYZ left top zoom], [page /Fit], [page /FitH top] and the rest.</summary>
    private static PdfDestination Destination(PdfObjectReader pdf, List<object?> dest, int page)
    {
        double? At(int i) => i < dest.Count ? pdf.Number(dest[i]) : null;
        return pdf.Name(dest.Count > 1 ? dest[1] : null) switch
        {
            "XYZ" => new PdfDestination(page, DestinationFit.Xyz, At(2), At(3)),
            "Fit" or "FitB" => new PdfDestination(page, DestinationFit.Fit),
            "FitH" or "FitBH" when At(2) is { } top => new PdfDestination(page, DestinationFit.FitH, null, top),
            "FitV" or "FitBV" when At(2) is { } left => new PdfDestination(page, DestinationFit.FitV, left, null),
            "FitR" when At(2) is { } left && At(5) is { } top => new PdfDestination(page, DestinationFit.FitR, left, top, At(4), At(3)),
            _ => new PdfDestination(page, DestinationFit.None),
        };
    }

    /// <summary>A named destination's name as the document spells it, for PDFium to look up.</summary>
    private static byte[]? NameBytes(object? dest) => dest switch
    {
        PdfName n when n.Value.Length > 0 => System.Text.Encoding.Latin1.GetBytes(n.Value),
        PdfString s when s.Bytes.Length > 0 => s.Bytes,
        _ => null,
    };

    /// <summary>A file specification: a string, or a dictionary with /UF (Unicode) or /F, or a platform's own entry.</summary>
    private static string? FileName(PdfObjectReader pdf, object? spec)
    {
        switch (pdf.Resolve(spec))
        {
            case PdfString s:
                return s.Lenient.Trim();
            case Dictionary<string, object?> dict:
                if (pdf.Resolve(dict.GetValueOrDefault("UF")) is PdfString unicode && unicode.Bytes.Length > 0) return unicode.Text.Trim();
                foreach (var key in new[] { "F", "DOS", "Unix", "Mac" })
                {
                    if (pdf.Resolve(dict.GetValueOrDefault(key)) is PdfString file && file.Bytes.Length > 0) return file.Lenient.Trim();
                }
                return null;
            default:
                return null;
        }
    }
}
