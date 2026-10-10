using Edith.App;
using Edith.Core.Documents;
using Edith.Core.Persona;

namespace Edith.App.Tests;

public sealed class DocumentRouteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-docroute-" + Guid.NewGuid().ToString("N"));

    public DocumentRouteTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "university_guide.txt"),
            "Welcome to the university.\n\nApplication deadline is 15 March 2027.\n\nTuition fee is 4500 EUR per year.");
    }

    private ResponseEngine Engine() => new(
        Path.Combine(_dir, "memory.json"),
        () => [],
        () => new PersonaProfile(),
        documents: new DocumentAssistant(() => [_dir]));

    [Fact]
    public async Task BrainOff_AnswersWithPassages_ThenFollowUpUsesSameDocument()
    {
        ResponseEngine engine = Engine();

        string first = await engine.HandleAsync("найди документ про университет и расскажи что там по срокам");
        Assert.Equal("Documents", engine.LastRoute);
        Assert.Contains("15 March 2027", first);

        string followUp = await engine.HandleAsync("а что там про стоимость");
        Assert.Equal("Documents", engine.LastRoute);
        Assert.Contains("4500 EUR", followUp);
    }

    [Fact]
    public async Task FollowUpWithoutRecentDocument_IsNotTakenByDocuments()
    {
        ResponseEngine engine = Engine();

        await engine.HandleAsync("а что там про цену");

        Assert.NotEqual("Documents", engine.LastRoute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
