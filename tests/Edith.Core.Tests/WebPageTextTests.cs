using Edith.Core;
using Edith.Core.Brain;

namespace Edith.Core.Tests;

public sealed class WebPageTextTests
{
    [Fact]
    public void Extract_DropsMarkup_PutsPricesFirst()
    {
        const string html = "<html><head><title>x</title><script>var a=1;</script></head><body>" +
                            "<h1>Видеокарты</h1><p>Обзор рынка</p><div>RTX 5090 32GB — 289 990 ₽</div></body></html>";

        string text = WebPageText.Extract(html);

        Assert.StartsWith("[цена] RTX 5090 32GB — 289 990 ₽", text);
        Assert.Contains("Обзор рынка", text);
        Assert.DoesNotContain("var a", text);
    }

    [Theory]
    [InlineData("скажи какая сейчас самая дорогая видеокарта в рф")]
    [InlineData("сколько стоит rtx 5090")]
    [InlineData("какая видеокарта лучше для игр")]
    public void MarketQuestions_AreNotAboutThisPc(string question) =>
        Assert.Empty(PcQuestion.Tools(question));

    [Theory]
    [InlineData("какая температура видеокарты", "gpu_status")]
    [InlineData("сколько свободно места на дисках", "system_snapshot")]
    public void ThisPcQuestions_StillGetFacts(string question, string tool) =>
        Assert.Contains(tool, PcQuestion.Tools(question));
}
