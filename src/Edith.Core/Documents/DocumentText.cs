using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Edith.Core.Documents;

public sealed record DocumentContent(string Path, string Text, int Pages);

/// <summary>Plain text out of PDF, DOCX and text files. Local, CPU only, no Office needed.</summary>
public static class DocumentText
{
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".txt", ".md", ".csv", ".log", ".rtf" };

    /// <summary>Documents bigger than this are cut: a voice answer never needs a whole book.</summary>
    public const int MaxChars = 400_000;

    public static DocumentContent Extract(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        (string text, int pages) = extension switch
        {
            ".pdf" => Pdf(path),
            ".docx" => (Docx(path), 0),
            ".rtf" => (Rtf(File.ReadAllText(path)), 0),
            _ => (File.ReadAllText(path), 0),
        };

        text = Regex.Replace(text.Replace("\r\n", "\n"), @"[ \t\u00A0]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        if (text.Length > MaxChars)
            text = text[..MaxChars];

        return new DocumentContent(path, text, pages);
    }

    private static (string Text, int Pages) Pdf(string path)
    {
        using PdfDocument document = PdfDocument.Open(path);
        var text = new StringBuilder();
        foreach (Page page in document.GetPages())
        {
            text.AppendLine(ContentOrderTextExtractor.GetText(page));
            text.AppendLine();
            if (text.Length > MaxChars)
                break;
        }

        return (text.ToString(), document.NumberOfPages);
    }

    private static string Docx(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("В файле нет word/document.xml — это не DOCX.");

        using Stream stream = entry.Open();
        var xml = new XmlDocument();
        xml.Load(stream);

        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

        var text = new StringBuilder();
        foreach (XmlNode paragraph in xml.SelectNodes("//w:body//w:p", ns)!)
        {
            foreach (XmlNode node in paragraph.SelectNodes(".//w:t|.//w:tab|.//w:br", ns)!)
            {
                text.Append(node.LocalName switch
                {
                    "t" => node.InnerText,
                    "tab" => "\t",
                    _ => "\n",
                });
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Rtf(string rtf)
    {
        // Enough for notes saved from WordPad: drop control words and groups, keep the text.
        string text = Regex.Replace(rtf, @"\\par[d]?", "\n");
        text = Regex.Replace(text, @"\\'[0-9a-f]{2}", "");
        text = Regex.Replace(text, @"\\[a-z]+-?\d* ?|[{}]", "");
        return text;
    }
}
