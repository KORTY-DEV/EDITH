using Edith.Core.Brain;
using Edith.Core.Resources;

namespace Edith.Core.Tests;

public sealed class OllamaPsTests
{
    private const string Ps = """
        {"models":[
          {"name":"qwen3.5:4b","size":3600000000,"size_vram":3565158400},
          {"name":"llama3.1:8b","size":5000000000,"size_vram":4718592000},
          {"name":"ministral-3:3b","size":2500000000,"size_vram":0}
        ]}
        """;

    [Fact]
    public void OnlyEdithModelsCountAsEdithVram_ForeignOllamaModelsDoNot()
    {
        IReadOnlyList<OllamaLoadedModel> loaded = OllamaPs.Parse(Ps);

        Assert.Equal(3, loaded.Count);
        Assert.Equal(3400, OllamaPs.EdithVramMb(loaded, ["qwen3.5:4b", "ministral-3:3b"]));
    }

    [Fact]
    public void EdithModel_IsNotForeignLoad_ButAnotherModelIs()
    {
        // 8 GB card: EDITH's qwen (3.4 GB) + someone's llama (4.5 GB) — only EDITH's part is hers.
        var gpu = new GpuSnapshot("GTX 1070", 20, 7800, 8192);
        int own = OllamaPs.EdithVramMb(OllamaPs.Parse(Ps), ["qwen3.5:4b"]);

        ResourcePlan withOwnCounted = ResourcePolicy.Decide(false, gpu, new ResourceSettings { RouterVramMb = 3500, HeavyVramMb = 6000 }, own);
        ResourcePlan onlyEdithLoaded = ResourcePolicy.Decide(false, new GpuSnapshot("GTX 1070", 20, 3900, 8192), new ResourceSettings { RouterVramMb = 3500, HeavyVramMb = 6000 }, own);

        Assert.Equal("gpu-shared", withOwnCounted.Reason);
        Assert.Equal("gpu-free", onlyEdithLoaded.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"models":null}""")]
    public void Garbage_IsEmpty(string json)
    {
        Assert.Empty(OllamaPs.Parse(json));
    }

    [Fact]
    public void UntaggedNamesMeanLatest()
    {
        Assert.Equal(1, OllamaPs.EdithVramMb([new OllamaLoadedModel("mistral:latest", 1024 * 1024)], ["mistral"]));
    }
}

public sealed class CreativeRequestTests
{
    [Theory]
    [InlineData("расскажи мне любой стих александра сергеевича пушкина", true)]
    [InlineData("сочини сказку про кота", true)]
    [InlineData("расскажи анекдот", true)]
    [InlineData("какая температура видеокарты", false)]
    [InlineData("что такое ssd", false)]
    public void Detects(string text, bool expected)
    {
        Assert.Equal(expected, CreativeRequest.IsCreative(text));
    }
}

public sealed class PersonaLanguageFeminineTests
{
    [Theory]
    [InlineData("Рад быть твоим помощником.", "Рада быть твоим помощником.")]
    [InlineData("Я помню тебя. Рад помочь!", "Я помню тебя. Рада помочь!")]
    [InlineData("Буду рад помочь.", "Буду рада помочь.")]
    [InlineData("Он рад тебя видеть.", "Он рад тебя видеть.")]
    [InlineData("Ты рад?", "Ты рад?")]
    [InlineData("Я понял, открываю.", "Я поняла, открываю.")]
    public void FixesEdithsOwnGender_LeavesOthers(string input, string expected)
    {
        Assert.Equal(expected, PersonaLanguage.Apply(input));
    }
}
