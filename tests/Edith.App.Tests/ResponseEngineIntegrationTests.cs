using Edith.App;

namespace Edith.App.Tests;

/// <summary>Small real-orchestration checks: raw STT enters the same engine as live App.</summary>
public sealed class ResponseEngineIntegrationTests
{
    [Theory]
    [InlineData("аш ди ди")]
    [InlineData("аж ди ди")]
    public async Task RawSttTechnicalAlias_UsesDeterministicSsdHddRoute(string heardHdd)
    {
        var engine = new ResponseEngine(Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"));

        string response = await engine.HandleAsync($"объясни мне коротко чем отличается от {heardHdd}");

        Assert.Equal("DirectFastPath", engine.LastRoute);
        Assert.Contains("SSD", response);
        Assert.Contains("HDD", response);
    }

    [Fact]
    public async Task MemorySaveThenRecall_PreservesStructuredPreferenceWording()
    {
        string path = Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json");
        try
        {
            var engine = new ResponseEngine(path);
            Assert.Equal("Запомнила.", await engine.HandleAsync("запомни мой любимый цвет — синий"));

            string response = await engine.HandleAsync("что ты помнишь обо мне");

            Assert.Equal("MemoryRecall", engine.LastRoute);
            Assert.Equal("Помню, что твой любимый цвет — синий.", response);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IdentityQuestion_UsesDirectFeminineIdentityRoute()
    {
        var engine = new ResponseEngine(Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"));

        string response = await engine.HandleAsync("как тебя зовут");

        Assert.Equal("IdentityFastPath", engine.LastRoute);
        Assert.Equal("Я Эдит.", response);
    }
}
