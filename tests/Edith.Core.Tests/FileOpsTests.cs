using Edith.Core.Files;
using Edith.Core.Runtime;

namespace Edith.Core.Tests;

public sealed class FileOpsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edith-files-" + Guid.NewGuid().ToString("N"));
    private string Downloads => Path.Combine(_root, "Downloads");
    private string Documents => Path.Combine(_root, "Documents");
    private string Pictures => Path.Combine(_root, "Pictures");
    private string Outside => Path.Combine(_root, "System");

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public void Recycle(string path)
        {
            Recycled.Add(path);
            File.Delete(path); // the real one moves it to the bin
        }
    }

    public FileOpsTests()
    {
        foreach (string dir in new[] { Downloads, Documents, Pictures, Outside })
            Directory.CreateDirectory(dir);
    }

    private string Old(string path, string content = "x")
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, DateTime.Now.AddHours(-2));
        return path;
    }

    private FileOps Ops(FakeRecycleBin? bin = null, EventBus? bus = null) =>
        new(new FileSafety([Downloads, Documents, Pictures]), bin ?? new FakeRecycleBin(),
            Path.Combine(_root, "file-ops.json"), bus);

    private DownloadsSorter Sorter() =>
        new(Documents, Pictures, Path.Combine(_root, "Videos"), Path.Combine(_root, "Music"), Downloads);

    [Fact]
    public void Safety_AllowsOnlyInsideUserFolders()
    {
        var safety = new FileSafety([Downloads]);

        Assert.True(safety.CanWrite(Path.Combine(Downloads, "a.txt")));
        Assert.True(safety.CanWrite(Path.Combine(Downloads, "sub", "a.txt")));
        Assert.False(safety.CanWrite(Downloads));
        Assert.False(safety.CanWrite(Path.Combine(Outside, "a.txt")));
        Assert.False(safety.CanWrite(Downloads + "Evil\\a.txt"));
        Assert.False(safety.CanWrite(Path.Combine(Downloads, "..", "System", "a.txt")));
    }

    [Fact]
    public void Move_NeverOverwrites_AndUndoRestoresAcrossRestart()
    {
        string a = Old(Path.Combine(Downloads, "отчёт.pdf"), "new");
        Old(Path.Combine(Documents, "отчёт.pdf"), "existing");

        FileResult result = Ops().Move("test", [new FileMove(a, Path.Combine(Documents, "отчёт.pdf"))]);

        Assert.True(result.Ok);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(Documents, "отчёт.pdf")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Documents, "отчёт (2).pdf")));

        FileResult undo = Ops().UndoLast();
        Assert.StartsWith("Вернула как было: 1", undo.Message);
        Assert.Equal("new", File.ReadAllText(a));
        Assert.False(File.Exists(Path.Combine(Documents, "отчёт (2).pdf")));
        Assert.StartsWith("Нечего возвращать", Ops().UndoLast().Message);
    }

    [Fact]
    public void Move_RefusesOutsideAllowedFolders()
    {
        string system = Old(Path.Combine(Outside, "config.ini"));

        FileResult result = Ops().Move("test", [new FileMove(system, Path.Combine(Documents, "config.ini"))]);

        Assert.False(result.Ok);
        Assert.True(File.Exists(system));
        Assert.Null(result.Batch);
    }

    [Fact]
    public void Recycle_UsesTheBin_NeverOutside_AndUndoPointsToTheBin()
    {
        var bin = new FakeRecycleBin();
        string setup = Old(Path.Combine(Downloads, "setup.exe"));
        string system = Old(Path.Combine(Outside, "important.dll"));

        FileResult result = Ops(bin).Recycle("test", [setup, system]);

        Assert.Equal([setup], bin.Recycled);
        Assert.True(File.Exists(system));
        Assert.Contains("корзин", Ops(bin).UndoLast().Message);
    }

    [Fact]
    public void Sorter_PlansByType_SkipsFreshPartialHiddenAndFolders()
    {
        Old(Path.Combine(Downloads, "guide.pdf"));
        Old(Path.Combine(Downloads, "photo.JPG"));
        Old(Path.Combine(Downloads, "setup.exe"));
        Old(Path.Combine(Downloads, "data.zip"));
        Old(Path.Combine(Downloads, "unknown.xyz"));
        Old(Path.Combine(Downloads, "movie.mkv.crdownload"));
        File.WriteAllText(Path.Combine(Downloads, "fresh.pdf"), "downloading just now");
        string hidden = Old(Path.Combine(Downloads, "desktop.ini"));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(Downloads, "Моя папка"));

        DownloadsSorter sorter = Sorter();
        SortPlan plan = sorter.Plan(Downloads);

        Assert.Equal(4, plan.Moves.Count);
        Assert.Equal(4, plan.Skipped);
        Assert.Contains(plan.Moves, m => m.To == Path.Combine(Documents, "Из загрузок", "guide.pdf"));
        Assert.Contains(plan.Moves, m => m.To == Path.Combine(Pictures, "Из загрузок", "photo.JPG"));
        Assert.Contains(plan.Moves, m => m.To == Path.Combine(Downloads, "Установщики", "setup.exe"));
        Assert.Equal(
            "В загрузках 4 файла: 1 документ — в Документы, 1 картинка — в Изображения, 1 архив — в папку Архивы в Загрузках, 1 установщик — в папку Установщики в Загрузках. Ещё 4 не трогаю.",
            sorter.Describe(plan));
    }

    [Fact]
    public void SortThenUndo_RoundTrip()
    {
        Old(Path.Combine(Downloads, "guide.pdf"));
        Old(Path.Combine(Downloads, "photo.png"));
        FileOps ops = Ops();

        FileResult sorted = ops.Move("разобрать загрузки", Sorter().Plan(Downloads).Moves);
        Assert.Equal("Готово: 2 файла перемещено.", sorted.Message);
        Assert.Empty(Directory.GetFiles(Downloads));

        ops.UndoLast();
        Assert.Equal(2, Directory.GetFiles(Downloads).Length);
        Assert.False(Directory.Exists(Path.Combine(Documents, "Из загрузок")));
    }

    [Fact]
    public void Installers_AreFoundInDownloadsAndItsInstallersFolder_ExceptFresh()
    {
        Directory.CreateDirectory(Path.Combine(Downloads, "Установщики"));
        string a = Old(Path.Combine(Downloads, "a.exe"));
        string b = Old(Path.Combine(Downloads, "Установщики", "b.msi"));
        File.WriteAllText(Path.Combine(Downloads, "fresh.exe"), "x");

        Assert.Equal([a, b], Sorter().Installers().Order());
    }

    [Theory]
    [InlineData("разбери загрузки", FileCommandKind.SortDownloads)]
    [InlineData("Эдит, наведи порядок в загрузках", FileCommandKind.SortDownloads)]
    [InlineData("удали установщики из загрузок", FileCommandKind.RecycleInstallers)]
    [InlineData("верни как было", FileCommandKind.Undo)]
    [InlineData("отмени разбор загрузок", FileCommandKind.Undo)]
    public void Parser(string phrase, FileCommandKind kind)
    {
        Assert.True(FileCommandParser.TryParse(phrase, out FileCommandKind parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData("удали файл отчёт")]
    [InlineData("верни мне деньги")]
    [InlineData("открой загрузки")]
    public void Parser_Ignores(string phrase)
    {
        Assert.False(FileCommandParser.TryParse(phrase, out _));
    }

    [Theory]
    [InlineData(1, "файл")]
    [InlineData(2, "файла")]
    [InlineData(5, "файлов")]
    [InlineData(11, "файлов")]
    [InlineData(21, "файл")]
    [InlineData(23, "файла")]
    public void Plural(int n, string word)
    {
        Assert.Equal(word, FileOps.Files(n));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
