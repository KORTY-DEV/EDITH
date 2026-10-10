using Edith.Core;

namespace Edith.Core.Tests;

public sealed class ConfigTextTests
{
    private const string Json = """
        {
          // comment stays
          "enabled": true,
          "model": "qwen3:8b",
          "normal": { "keepAlive": "3h", "numCtx": 4096 },
          "gaming": { "enabled": true, "keepAlive": "5m" },
          "threshold": 0.60
        }
        """;

    [Fact]
    public void Get_ReadsNestedAndTopLevel()
    {
        Assert.Equal("qwen3:8b", ConfigText.Get(Json, "model"));
        Assert.Equal("5m", ConfigText.Get(Json, "gaming.keepAlive"));
        Assert.Equal("3h", ConfigText.Get(Json, "keepAlive"));
        Assert.Equal(0.6, ConfigText.GetNumber(Json, "threshold"));
        Assert.Null(ConfigText.Get(Json, "missing"));
        Assert.Equal(@"C:\EDITH\x.txt", ConfigText.Get("""{ "p": "C:\\EDITH\\x.txt" }""", "p"));
    }

    [Fact]
    public void Set_ChangesOnlyThatValue()
    {
        string changed = ConfigText.Set(Json, "gaming.enabled", false);
        changed = ConfigText.Set(changed, "threshold", 0.7);
        changed = ConfigText.Set(changed, "normal.keepAlive", "8h");

        Assert.Contains("// comment stays", changed);
        Assert.Equal("true", ConfigText.Get(changed, "enabled"));
        Assert.Equal("false", ConfigText.Get(changed, "gaming.enabled"));
        Assert.Equal("8h", ConfigText.Get(changed, "keepAlive"));
        Assert.Equal("5m", ConfigText.Get(changed, "gaming.keepAlive"));
        Assert.Contains("\"threshold\": 0.7", changed);
        Assert.Equal(Json, ConfigText.Set(Json, "missing", 1));
    }
}
