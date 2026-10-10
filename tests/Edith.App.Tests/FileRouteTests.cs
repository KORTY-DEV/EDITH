using Edith.App;
using Edith.Core.Files;
using Edith.Core.Persona;

namespace Edith.App.Tests;

public sealed class FileRouteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edith-fileroute-" + Guid.NewGuid().ToString("N"));

    private sealed class Bin : IRecycleBin
    {
        public void Recycle(string path) => File.Delete(path);
    }

    [Fact]
    public async Task SortAsksFirst_ExecutesOnConfirm_UndoPutsBack()
    {
        string downloads = Path.Combine(_root, "Downloads");
        string documents = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(documents);
        string pdf = Path.Combine(downloads, "guide.pdf");
        File.WriteAllText(pdf, "x");
        File.SetLastWriteTime(pdf, DateTime.Now.AddDays(-1));

        var ops = new FileOps(new FileSafety([downloads, documents, Path.Combine(_root, "Pictures")]), new Bin());
        var sorter = new DownloadsSorter(documents, Path.Combine(_root, "Pictures"), Path.Combine(_root, "Videos"), Path.Combine(_root, "Music"), downloads);
        Func<Task<string>>? pending = null;
        var engine = new ResponseEngine(
            Path.Combine(_root, "memory.json"),
            () => [],
            () => new PersonaProfile(),
            requestPermission: (summary, _, execute) => { pending = execute; return $"Нужно подтверждение: {summary}."; },
            files: ops,
            sorter: sorter);

        string ask = await engine.HandleAsync("разбери загрузки");
        Assert.Equal("Files", engine.LastRoute);
        Assert.Equal("В загрузках 1 файл: 1 документ — в Документы. Нужно подтверждение: разобрать загрузки.", ask);
        Assert.True(File.Exists(pdf));

        Assert.StartsWith("Готово: 1 файл перемещено.", await pending!());
        Assert.False(File.Exists(pdf));

        Assert.StartsWith("Вернула как было: 1", await engine.HandleAsync("верни как было"));
        Assert.True(File.Exists(pdf));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
