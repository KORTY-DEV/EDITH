using Edith.Core.Memory;

namespace Edith.Core.Tests;

public sealed class NotesIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-notes-" + Guid.NewGuid().ToString("N"));

    public NotesIndexTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "vault", "20_EDITH"));
        Directory.CreateDirectory(Path.Combine(_dir, "vault", "99_ARCHIVE"));
        Directory.CreateDirectory(Path.Combine(_dir, "vault", ".obsidian"));
        File.WriteAllText(Path.Combine(_dir, "vault", "20_EDITH", "EDITH — Задачи.md"), """
            ---
            type: task-list
            ---

            # Задачи E.D.I.T.H.

            ## Голос

            - [ ] Проверить стоп-слово во время длинного ответа
            - [ ] Настроить паузу после «Эдит»

            ## Память

            Итоги разговоров собираются в фоне, см. [[EDITH — Главная|главную]].
            """);
        File.WriteAllText(Path.Combine(_dir, "vault", "99_ARCHIVE", "old.md"), "# Старьё\n\nсекретный архив про задачи");
        File.WriteAllText(Path.Combine(_dir, "vault", ".obsidian", "app.md"), "# служебное\n\nзадачи");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private NotesIndex NewIndex() => new(Path.Combine(_dir, "notes.db"));
    private string Vault => Path.Combine(_dir, "vault");

    [Fact]
    public void Split_UsesHeadings_DropsFrontmatter_AndFlattensWikilinks()
    {
        List<NoteChunk> chunks = NotesIndex.Split("Задачи", "x.md", File.ReadAllText(Path.Combine(Vault, "20_EDITH", "EDITH — Задачи.md"))).ToList();

        Assert.Equal(["Голос", "Память"], chunks.Select(c => c.Heading));
        Assert.DoesNotContain(chunks, c => c.Text.Contains("type: task-list"));
        Assert.Contains("главную", chunks[1].Text);
        Assert.DoesNotContain("[[", chunks[1].Text);
    }

    [Fact]
    public void Search_FindsByInflectedWords_OnlyInChosenFolders()
    {
        NotesIndex index = NewIndex();
        Assert.Equal(1, index.Refresh(Vault, ["20_EDITH"]));

        IReadOnlyList<NoteChunk> found = index.Search("какие задачи по стоп слову", 4);

        Assert.NotEmpty(found);
        Assert.Equal("Голос", found[0].Heading);
        Assert.DoesNotContain(found, c => c.Text.Contains("секретный архив"));
        Assert.Empty(index.Search("секретный архив", 4));
    }

    [Fact]
    public void Refresh_IsIncremental_AndFollowsEditsAndDeletes()
    {
        NotesIndex index = NewIndex();
        string note = Path.Combine(Vault, "20_EDITH", "EDITH — Задачи.md");

        Assert.Equal(1, index.Refresh(Vault, ["20_EDITH"]));
        Assert.Equal(0, index.Refresh(Vault, ["20_EDITH"]));

        File.AppendAllText(note, "\n\n## Браузер\n\nСделать чтение сайтов через Playwright.\n");
        File.SetLastWriteTimeUtc(note, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(1, index.Refresh(Vault, ["20_EDITH"]));
        Assert.Equal("Браузер", index.Search("Playwright", 2).Single().Heading);

        File.Delete(note);
        Assert.Equal(1, index.Refresh(Vault, ["20_EDITH"]));
        Assert.Empty(index.Search("Playwright", 2));
        Assert.Equal(0, index.FileCount);
    }

    [Fact]
    public void ReadNote_FindsByTitlePart()
    {
        NotesIndex index = NewIndex();
        index.Refresh(Vault, ["20_EDITH"]);

        string? note = index.ReadNote("задачи edith");

        Assert.NotNull(note);
        Assert.Contains("стоп-слово", note);
        Assert.Null(index.ReadNote("несуществующая заметка про космос"));
    }
}
