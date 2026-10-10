using System.IO.Compression;
using Edith.Core.Documents;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Edith.Core.Tests;

public sealed class DocumentFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "edith-docs-" + Guid.NewGuid().ToString("N"));

    public DocumentFixture()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Downloads", "uni"));

        WriteDocx(
            Path.Combine(Root, "Downloads", "uni", "University_Admission_Guide.docx"),
            "University admission guide 2027.",
            "Application deadline: 15 March 2027. Late applications are not accepted.",
            "Tuition fee is 4500 EUR per year.",
            "The campus has a modern library and a sports centre.");

        File.WriteAllText(
            Path.Combine(Root, "Downloads", "список покупок.txt"),
            "Молоко и хлеб.\n\nКупить батарейки до пятницы.\n\nЗабрать посылку.");

        var pdf = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = pdf.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = pdf.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddText("Contract for services.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        page.AddText("Payment due by 01.10.2026.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        File.WriteAllBytes(Path.Combine(Root, "Downloads", "contract.pdf"), pdf.Build());
    }

    public IEnumerable<string> Directories() => [Path.Combine(Root, "Downloads")];

    private static void WriteDocx(string path, params string[] paragraphs)
    {
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        ZipArchiveEntry entry = zip.CreateEntry("word/document.xml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""");
        foreach (string paragraph in paragraphs)
            writer.Write($"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(paragraph)}</w:t></w:r></w:p>");
        writer.Write("</w:body></w:document>");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public sealed class DocumentTests(DocumentFixture docs) : IClassFixture<DocumentFixture>
{
    [Fact]
    public void Extract_ReadsDocxPdfAndText()
    {
        string downloads = docs.Directories().Single();

        Assert.Contains("Tuition fee is 4500 EUR", DocumentText.Extract(Path.Combine(downloads, "uni", "University_Admission_Guide.docx")).Text);
        DocumentContent pdf = DocumentText.Extract(Path.Combine(downloads, "contract.pdf"));
        Assert.Contains("Payment due by 01.10.2026", pdf.Text);
        Assert.Equal(1, pdf.Pages);
        Assert.Contains("батарейки", DocumentText.Extract(Path.Combine(downloads, "список покупок.txt")).Text);
    }

    [Theory]
    [InlineData("университет который я скачивал", "University_Admission_Guide.docx")]
    [InlineData("договор", "contract.pdf")]
    [InlineData("список покупок", "список покупок.txt")]
    public void Finder_MatchesRussianSpeechToFileNames(string spoken, string file)
    {
        Assert.Equal(file, Path.GetFileName(DocumentFinder.Find(spoken, docs.Directories()).FirstOrDefault()?.Path));
    }

    [Fact]
    public void Finder_NothingForUnrelatedNames()
    {
        Assert.Empty(DocumentFinder.Find("налоговая декларация", docs.Directories()));
    }

    [Theory]
    [InlineData("срокам", "Application deadline: 15 March 2027")]
    [InlineData("стоимость", "Tuition fee is 4500 EUR")]
    public void Passages_AnswerTheQuestion(string question, string expected)
    {
        string text = DocumentText.Extract(Path.Combine(docs.Directories().Single(), "uni", "University_Admission_Guide.docx")).Text;

        IReadOnlyList<string> passages = DocumentQuery.Passages(text, question);

        Assert.Contains(passages, p => p.Contains(expected));
        Assert.DoesNotContain(passages, p => p.Contains("library"));
    }

    [Fact]
    public void Passages_WordStartsOnly_NoFalseHits()
    {
        Assert.Empty(DocumentQuery.Passages("Готовые сценарии уже внутри программы.", "цена"));
    }

    [Fact]
    public void Assistant_FindAskThenFollowUp()
    {
        var assistant = new DocumentAssistant(docs.Directories);

        Assert.True(DocumentCommandParser.TryParse("найди PDF про университет, который я скачивал, и расскажи, что там по срокам", out DocumentCommand ask));
        DocumentAnswer first = assistant.Handle(ask);
        Assert.Contains("15 March 2027", first.Reply);
        Assert.EndsWith("University_Admission_Guide.docx", first.DocumentPath);

        Assert.True(DocumentCommandParser.TryParse("а что там про стоимость", out DocumentCommand followUp));
        Assert.Equal("", followUp.Name);
        Assert.Contains("4500 EUR", assistant.Handle(followUp).Reply);

        Assert.True(DocumentCommandParser.TryParse("о чем этот документ", out DocumentCommand overview));
        Assert.StartsWith("«University_Admission_Guide.docx» начинается так: University admission guide 2027.", assistant.Handle(overview).Reply);
    }

    [Fact]
    public void Assistant_WithoutContext_AsksWhichDocument()
    {
        var assistant = new DocumentAssistant(docs.Directories);
        Assert.True(DocumentCommandParser.TryParse("а что там про стоимость", out DocumentCommand followUp));

        Assert.StartsWith("Не поняла, о каком документе речь", assistant.Handle(followUp).Reply);
    }

    [Theory]
    [InlineData("найди документ про договор", DocumentCommandKind.Find, "договор", "")]
    [InlineData("что в документе договор про оплату", DocumentCommandKind.Ask, "договор", "оплату")]
    [InlineData("прочитай файл список покупок", DocumentCommandKind.Overview, "список покупок", "")]
    public void Parser(string phrase, DocumentCommandKind kind, string name, string question)
    {
        Assert.True(DocumentCommandParser.TryParse(phrase, out DocumentCommand command));
        Assert.Equal((kind, name, question), (command.Kind, command.Name, command.Question));
    }

    [Theory]
    [InlineData("найди видеокарту подешевле")]
    [InlineData("что такое pdf")]
    [InlineData("открой дискорд")]
    public void Parser_IgnoresOtherPhrases(string phrase)
    {
        Assert.False(DocumentCommandParser.TryParse(phrase, out _));
    }
}
