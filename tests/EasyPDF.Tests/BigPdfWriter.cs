using System.IO;
using System.Text;

/// <summary>
/// Writes a plain, valid PDF of many text pages: one shared font, one small content stream per
/// page, a classic cross-reference table. Big enough to exercise scrolling, search and memory.
/// </summary>
internal static class BigPdfWriter
{
    /// <param name="namedLinkPage">A page that gets a "next page" named-action link and a JavaScript link, or -1.</param>
    /// <param name="padding">Bytes of comment added to each page's content, to make the file big.</param>
    public static string Write(string path, int pages, int[] needles, int namedLinkPage = -1, int padding = 0)
    {
        var needleSet = needles.ToHashSet();
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();
        var latin = Encoding.Latin1;

        void Raw(string s)
        {
            var bytes = latin.GetBytes(s);
            stream.Write(bytes);
        }

        void Obj(int number, string body)
        {
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = stream.Position;
            Raw($"{number} 0 obj\n{body}\nendobj\n");
        }

        Raw("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + (2 * i)} 0 R"));
        Obj(2, $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>");
        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < pages; i++)
        {
            var content = new StringBuilder();
            content.Append($"BT /F1 18 Tf 72 720 Td (Page {i + 1} heading) Tj ET\n");
            for (var line = 0; line < 30; line++)
            {
                var word = needleSet.Contains(i) && line == 10 ? "needle" : "lorem";
                content.Append($"BT /F1 10 Tf 72 {682 - (line * 20)} Td ({word} ipsum dolor sit amet line {line + 1} on page {i + 1}) Tj ET\n");
            }
            for (var written = 0; written < padding; written += 64) content.Append('%').Append('x', 62).Append((char)10);
            var data = content.ToString();
            var annots = i == namedLinkPage
                ? " /Annots [<< /Type /Annot /Subtype /Link /Rect [70 600 240 620] /Border [0 0 0] /A << /S /Named /N /NextPage >> >> << /Type /Annot /Subtype /Link /Rect [70 560 240 580] /A << /S /JavaScript /JS (1) >> >>]"
                : string.Empty;
            Obj(4 + (2 * i), $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (2 * i)} 0 R{annots} >>");
            Obj(5 + (2 * i), $"<< /Length {latin.GetByteCount(data)} >>\nstream\n{data}endstream");
        }

        var xref = stream.Position;
        Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Raw($"{offset:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;
    }

    /// <summary>
    /// A form as EPLAN writes one: every page covered in push buttons that draw nothing, each a
    /// field of its own, each jumping to the next page (the last to the first).
    /// </summary>
    /// <param name="brokenTable">The file ends pointing at no cross-reference: PDFium rebuilds it by scanning; nothing else reads the file.</param>
    public static string WriteForm(string path, int pages, int buttonsPerPage, bool brokenTable = false)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();
        var latin = Encoding.Latin1;
        void Raw(string s) => stream.Write(latin.GetBytes(s));
        void Obj(int number, string body)
        {
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = stream.Position;
            Raw($"{number} 0 obj\n{body}\nendobj\n");
        }

        // 1 catalog, 2 pages, 3 font, 4 form; then per page: page, content; then the buttons.
        int PageObj(int i) => 5 + (2 * i);
        var firstButton = 5 + (2 * pages);
        int ButtonObj(int page, int b) => firstButton + (page * buttonsPerPage) + b;

        Raw("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        Obj(2, $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{PageObj(i)} 0 R"))}] /Count {pages} >>");
        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var fields = new StringBuilder();
        for (var p = 0; p < pages; p++)
            for (var b = 0; b < buttonsPerPage; b++) fields.Append(ButtonObj(p, b)).Append(" 0 R ");
        Obj(4, $"<< /Fields [{fields}] >>");

        var columns = (int)Math.Ceiling(Math.Sqrt(buttonsPerPage));
        for (var p = 0; p < pages; p++)
        {
            var annots = string.Join(" ", Enumerable.Range(0, buttonsPerPage).Select(b => $"{ButtonObj(p, b)} 0 R"));
            Obj(PageObj(p), $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {PageObj(p) + 1} 0 R /Annots [{annots}] >>");
            var content = $"BT /F1 24 Tf 72 740 Td (Form page {p + 1}) Tj ET";
            Obj(PageObj(p) + 1, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        for (var p = 0; p < pages; p++)
        {
            for (var b = 0; b < buttonsPerPage; b++)
            {
                // A grid of small buttons below the heading: button 0 at the top left.
                double x = 40 + ((b % columns) * (530.0 / columns)), y = 700 - ((b / columns) * (650.0 / columns));
                Obj(ButtonObj(p, b), string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (b{p}x{b}) /F 4 /P {PageObj(p)} 0 R " +
                    $"/Rect [{x:0.##} {y - 8:0.##} {x + 12:0.##} {y:0.##}] /A << /S /GoTo /D [{PageObj((p + 1) % pages)} 0 R /Fit] >> >>"));
            }
        }

        var xref = stream.Position;
        Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Raw($"{offset:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{(brokenTable ? 0 : xref)}\n%%EOF\n");
        return path;
    }
}
