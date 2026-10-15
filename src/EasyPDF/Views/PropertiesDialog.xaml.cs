using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using EasyPDF.Pdf;
using EasyPDF.Resources;
using EasyPDF.Services;

namespace EasyPDF.Views;

/// <summary>What the file and its information dictionary say about the document.</summary>
public partial class PropertiesDialog : Window
{
    public PropertiesDialog(PdfDocument document)
    {
        InitializeComponent();

        var meta = document.Metadata;
        HeadingText.Text = meta.Title.Length > 0 ? meta.Title : document.FileName;
        AppVersion.Text = $"{AppIdentity.Name} {AppIdentity.Version}";

        var first = document.PageSizes[0];
        Add(Strings.Properties_File, document.FileName);
        Add(Strings.Properties_Folder, Path.GetDirectoryName(document.FilePath) ?? string.Empty);
        Add(Strings.Properties_Size, UiLanguage.Format(Strings.Properties_SizeBytes, MainWindow.FormatSize(document.FileLength), document.FileLength));
        Add(Strings.Properties_Pages, document.PageCount.ToString("N0", UiLanguage.Culture));
        Add(Strings.Properties_PageSize, DescribePageSize(first));
        Add(Strings.Properties_Version, meta.FileVersion > 0 ? UiLanguage.Format("PDF {0}.{1}", meta.FileVersion / 10, meta.FileVersion % 10) : string.Empty);
        Add(Strings.Properties_DocTitle, meta.Title);
        Add(Strings.Properties_Author, meta.Author);
        Add(Strings.Properties_Subject, meta.Subject);
        Add(Strings.Properties_Keywords, meta.Keywords);
        Add(Strings.Properties_Creator, meta.Creator);
        Add(Strings.Properties_Producer, meta.Producer);
        Add(Strings.Properties_Created, FormatPdfDate(meta.CreationDate));
        Add(Strings.Properties_Modified, FormatPdfDate(meta.ModificationDate));
        Add(Strings.Properties_Encrypted, meta.IsEncrypted ? Strings.Common_Yes : Strings.Common_No);
        Add(Strings.Properties_Form, meta.HasForm ? Strings.Common_Yes : Strings.Common_No);
    }

    private void Add(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var row = Rows.RowDefinitions.Count;
        if (Rows.ColumnDefinitions.Count == 0)
        {
            Rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
            Rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var caption = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 4, 16, 4),
            FontSize = (double)FindResource("FontSizeSmall"),
            FontWeight = FontWeights.SemiBold,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        Grid.SetRow(caption, row);
        Rows.Children.Add(caption);

        var text = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 3, 0, 3),
            TextWrapping = TextWrapping.Wrap,
            IsTabStop = false,
        };
        text.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        Rows.Children.Add(text);
    }

    private static string DescribePageSize(PageSize size)
    {
        var mmW = size.Width / 72 * 25.4;
        var mmH = size.Height / 72 * 25.4;
        var name = KnownSize(mmW, mmH);
        var dims = UiLanguage.Format(Strings.Properties_PageSizeValue, mmW, mmH, size.Width / 72, size.Height / 72);
        return name is null ? dims : $"{name} — {dims}";
    }

    private static string? KnownSize(double w, double h)
    {
        var (a, b) = (Math.Min(w, h), Math.Max(w, h));
        foreach (var (name, sw, sh) in new[]
                 {
                     ("A3", 297.0, 420.0), ("A4", 210.0, 297.0), ("A5", 148.0, 210.0), ("Letter", 215.9, 279.4),
                     ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8),
                 })
        {
            if (Math.Abs(a - sw) < 2 && Math.Abs(b - sh) < 2) return name;
        }
        return null;
    }

    /// <summary>A PDF date - D:YYYYMMDDHHmmSS+HH'mm' with everything after the year optional - in local time.</summary>
    internal static string FormatPdfDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var m = Regex.Match(raw.Trim(), @"^(?:D:)?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?\s*(Z|[+\-]\d{2}'?\d{2}?'?)?", RegexOptions.CultureInvariant);
        if (!m.Success) return raw;

        int Part(int i, int fallback) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : fallback;
        try
        {
            var local = new DateTime(Part(1, 1), Math.Clamp(Part(2, 1), 1, 12), 1, 0, 0, 0, DateTimeKind.Unspecified);
            local = local.AddDays(Math.Clamp(Part(3, 1), 1, DateTime.DaysInMonth(local.Year, local.Month)) - 1)
                .AddHours(Math.Clamp(Part(4, 0), 0, 23)).AddMinutes(Math.Clamp(Part(5, 0), 0, 59)).AddSeconds(Math.Clamp(Part(6, 0), 0, 59));

            var zone = m.Groups[7].Value.Replace("'", string.Empty, StringComparison.Ordinal);
            if (zone.Length > 0)
            {
                var offset = TimeSpan.Zero;
                if (zone != "Z")
                {
                    var hours = int.Parse(zone.Substring(1, 2), CultureInfo.InvariantCulture);
                    var minutes = zone.Length >= 5 ? int.Parse(zone.Substring(3, 2), CultureInfo.InvariantCulture) : 0;
                    offset = new TimeSpan(hours, minutes, 0);
                    if (zone[0] == '-') offset = -offset;
                }
                local = new DateTimeOffset(local, offset).LocalDateTime;
            }
            return local.ToString("g", UiLanguage.Culture);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return raw;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
