using Edith.Core.Brain;
using Edith.Core.Files;

namespace Edith.Core.Tests;

public sealed class RussianNumbersTests
{
    [Theory]
    [InlineData(0, "ноль")]
    [InlineData(1, "один")]
    [InlineData(12, "двенадцать")]
    [InlineData(21, "двадцать один")]
    [InlineData(284, "двести восемьдесят четыре")]
    [InlineData(1000, "одна тысяча")]
    [InlineData(2022, "две тысячи двадцать два")]
    [InlineData(45000, "сорок пять тысяч")]
    [InlineData(1_000_000, "один миллион")]
    public void ToWords(long n, string words)
    {
        Assert.Equal(words, RussianNumbers.ToWords(n));
    }

    [Theory]
    [InlineData("В загрузках 284 файла", "В загрузках двести восемьдесят четыре файла")]
    [InlineData("Напомню в 18:30", "Напомню в восемнадцать тридцать")]
    [InlineData("в 09:05", "в девять ноль пять")]
    [InlineData("свободно 12,6 ГБ", "свободно двенадцать и шесть ГБ")]
    [InlineData("загрузка 45%", "загрузка сорок пять процентов")]
    [InlineData("без цифр", "без цифр")]
    public void Verbalize(string text, string spoken)
    {
        Assert.Equal(spoken, RussianNumbers.Verbalize(text));
    }

    [Fact]
    public void ForSpeech_SpeaksNumbers_AndKeepsLongAnswers()
    {
        Assert.Equal("Готово: три файла.", VoiceText.ForSpeech("Готово: 3 файла."));

        string poem = string.Join(" ", Enumerable.Repeat("Я помню чудное мгновенье, передо мной явилась ты.", 8));
        Assert.True(VoiceText.ForSpeech(poem).Length > 300);
    }
}

public sealed class PcQuestionTests
{
    [Theory]
    [InlineData("сколько свободно места на дисках и что сейчас ест больше всего памяти", new[] { "system_snapshot", "processes" })]
    [InlineData("какая температура видеокарты", new[] { "gpu_status" })]
    [InlineData("сколько занято оперативки", new[] { "system_snapshot" })]
    [InlineData("что грузит процессор", new[] { "processes" })]
    public void Detects(string question, string[] tools)
    {
        Assert.Equal(tools, PcQuestion.Tools(question));
    }

    [Theory]
    [InlineData("что такое ssd")]
    [InlineData("чем отличается ssd от hdd")]
    [InlineData("какая температура на улице")]
    [InlineData("расскажи стих пушкина")]
    [InlineData("я занимаюсь разработкой проекта")]
    public void Ignores(string question)
    {
        Assert.Empty(PcQuestion.Tools(question));
    }
}

public sealed class FileQueryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-fq-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parse_NeedsPlaceAndKind()
    {
        Assert.True(FileQuery.TryParse("найди в загрузках пдф файлы и скажи какой самый свежий", out FileQueryRequest pdf));
        Assert.Equal("PDF", pdf.KindName);
        Assert.Contains(".pdf", pdf.Extensions);

        Assert.True(FileQuery.TryParse("какие картинки на рабочем столе", out FileQueryRequest pictures));
        Assert.Equal("на рабочем столе", pictures.FolderName);

        Assert.False(FileQuery.TryParse("найди pdf про университет", out _));
        Assert.False(FileQuery.TryParse("разбери загрузки", out _));
    }

    [Fact]
    public void Answer_ListsNewestFirst()
    {
        Directory.CreateDirectory(_dir);
        DateTime now = new(2026, 9, 25, 12, 0, 0);
        File.WriteAllText(Path.Combine(_dir, "old.pdf"), "x");
        File.SetLastWriteTime(Path.Combine(_dir, "old.pdf"), now.AddDays(-30));
        File.WriteAllText(Path.Combine(_dir, "new.pdf"), "x");
        File.SetLastWriteTime(Path.Combine(_dir, "new.pdf"), now.AddHours(-1));
        File.WriteAllText(Path.Combine(_dir, "photo.png"), "x");

        string answer = FileQuery.Answer(new FileQueryRequest(_dir, "в загрузках", new HashSet<string> { ".pdf" }, "PDF"), now);

        Assert.StartsWith("В загрузках 2 файла. Самые свежие: «new» (сегодня), «old» (", answer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
