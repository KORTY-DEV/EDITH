using Edith.Core.Library;

namespace Edith.Core.Tests;

public sealed class LibrarianTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edith-lib-" + Guid.NewGuid().ToString("N"));

    public LibrarianTests() => Directory.CreateDirectory(Path.Combine(_root, "vault"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static SessionTranscript Session(string id, string project = "EDITH", int turns = 8) =>
        new(id, "agent", "Агент", project, new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
            "C:/x/" + id + ".jsonl",
            Enumerable.Range(0, turns).Select(i => new SessionTurn(i % 2 == 0 ? "user" : "assistant", $"реплика {i} про Slice 5 и тесты")).ToList());

    private Librarian Make(Func<string, string, CancellationToken, Task<string?>> complete) =>
        new(Path.Combine(_root, "vault"), [new LibraryProject("EDITH", "20_EDITH", ["edith"])], Path.Combine(_root, "data"), complete, commit: false);

    [Fact]
    public async Task Session_BecomesAFullNote_ChronicleLine_AndIsNotDocumentedTwice()
    {
        int calls = 0;
        var librarian = Make((system, user, _) =>
        {
            calls++;
            return Task.FromResult<string?>(system.StartsWith("По подробным", StringComparison.Ordinal)
                ? """{"title":"Доработка Slice 5","summary":"Сделали пятый срез и прогнали тесты."}"""
                : "## Что просил хозяин\nДоделать срез.\n\n## Открытые вопросы и риски\n- не проверен P4\n");
        });

        IReadOnlyList<string> first = await librarian.RunAsync([Session("abc")], 3, default);
        IReadOnlyList<string> second = await librarian.RunAsync([Session("abc")], 3, default);

        string note = Assert.Single(first);
        Assert.Empty(second);
        Assert.Contains("20_EDITH/Сессии AI/", note);
        string text = File.ReadAllText(Path.Combine(_root, "vault", note.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("# Доработка Slice 5", text);
        Assert.Contains("status: unverified", text);
        Assert.Contains("не проверен P4", text);
        string chronicle = File.ReadAllText(Path.Combine(_root, "vault", "20_EDITH", Librarian.ChronicleName));
        Assert.Contains("[[2026-10-09 EDITH — Доработка Slice 5|Доработка Slice 5]]", chronicle);
        Assert.True(File.Exists(Path.Combine(_root, "vault", "20_EDITH", ProjectPicture.FileName)));
    }

    [Fact]
    public async Task BusyModel_PausesTheSession_AndTheNextRunResumesWithoutRepeating()
    {
        int calls = 0;
        var librarian = Make((_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(calls == 2 ? null : "## Что просил хозяин\nX\n");
        });
        SessionTranscript big = Session("big", turns: 8) with { Turns = Enumerable.Range(0, 40).Select(i => new SessionTurn("user", new string('я', 700))).ToList() };

        Assert.Empty(await librarian.RunAsync([big], 1, default)); // paused on the second part
        int afterFirst = calls;
        IReadOnlyList<string> done = await librarian.RunAsync([big], 1, default);

        Assert.Single(done);
        Assert.True(calls - afterFirst < Librarian.Chunks(big).Count + 2, "the first part must not be asked again");
    }

    [Fact]
    public async Task MayWorkFalse_StopsBeforeAnyModelCall()
    {
        int calls = 0;
        var librarian = Make((_, _, _) => { calls++; return Task.FromResult<string?>("## x\ny"); });

        IReadOnlyList<string> result = await librarian.RunAsync([Session("a")], 1, default, mayWork: () => false);

        Assert.Empty(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Chunks_StayWithinBudget_AndHugeSessionsAreThinned()
    {
        SessionTranscript huge = Session("h") with { Turns = Enumerable.Range(0, 3000).Select(i => new SessionTurn("user", new string('к', 400) + i)).ToList() };

        List<string> chunks = Librarian.Chunks(huge);

        Assert.InRange(chunks.Count, 2, Librarian.MaxChunks + 1);
        Assert.All(chunks, c => Assert.True(c.Length < Librarian.ChunkChars * 1.2));
    }

    [Theory]
    [InlineData("Доработка Slice 5", true)]
    [InlineData("<external_apps_open_page>{\"page_id\":null}", false)]
    [InlineData("a_b_c_d_e", false)]
    [InlineData("see https://x.y/z", false)]
    [InlineData("ab", false)]
    public void GoodTitle_RejectsToolDumps(string title, bool expected) => Assert.Equal(expected, Librarian.GoodTitle(title));

    [Fact]
    public void Clean_StripsThinkingAndFences_AndKeepsFromTheFirstHeading()
    {
        string cleaned = Librarian.Clean("<think>рассуждения</think>\n```markdown\nВот запись:\n## Что просил хозяин\nX\n```");

        Assert.StartsWith("## Что просил хозяин", cleaned);
        Assert.DoesNotContain("рассуждения", cleaned);
        Assert.DoesNotContain("```", cleaned);
    }

    [Theory]
    [InlineData("C:/u/agent/projects/p/9329c057-3aa0-4dca-a31d-8c3204b005ea.jsonl", "9329c057-3aa0-4dca-a31d-8c3204b005ea")]
    [InlineData("C:/u/agent2/sessions/2026/10/09/rollout-2026-10-09T21-21-22-01a121af-0abc-77d0-9453-e149b24c74be.jsonl", "01a121af-0abc-77d0-9453-e149b24c74be")]
    public void IdFromFile_ForBothSources(string path, string expected) => Assert.Equal(expected, SessionSources.IdFromFile(path));

    [Fact]
    public void SessionReader_ReadsWordsOnly_DropsCodeToolsAndSecrets()
    {
        string file = Path.Combine(_root, "s.jsonl");
        File.WriteAllLines(file,
        [
            """{"type":"user","cwd":"C:/EDITH","sessionId":"s1","timestamp":"2026-10-09T10:00:00Z","message":{"role":"user","content":"Сделай стриминг ответа. password: hunter2"}}""",
            """{"type":"assistant","timestamp":"2026-10-09T10:01:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Готово.\n```cs\nvar x = 1;\n```"},{"type":"tool_use","name":"Bash","input":{}}]}}""",
            """{"type":"user","timestamp":"2026-10-09T10:02:00Z","message":{"role":"user","content":[{"type":"tool_result","content":"огромный лог"}]}}""",
        ]);

        SessionTranscript? t = SessionReader.ReadMessages(file, "agent", "Агент");

        Assert.NotNull(t);
        Assert.Equal("EDITH", t!.Project);
        Assert.Equal(2, t.Turns.Count);
        Assert.Contains("[скрыто]", t.Turns[0].Text);
        Assert.DoesNotContain("hunter2", t.Turns[0].Text);
        Assert.Contains("[код]", t.Turns[1].Text);
        Assert.DoesNotContain("var x", t.Turns[1].Text);
    }
}
