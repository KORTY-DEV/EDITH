using Edith.Core.Brain;

namespace Edith.Core.Tests;

public sealed class AnswerStreamTests
{
    [Fact]
    public void Splitter_CutsAtSentenceEnds_KeepsShortFragmentsTogether()
    {
        var splitter = new SentenceSplitter();
        var sentences = new List<string>();

        foreach (string piece in new[] { "Сегодня в Самаре ", "двадцать градусов. ", "Лучше взять ", "куртку, т. е. ", "что-то тёплое! А", " вечером дождь." })
            sentences.AddRange(splitter.Add(piece));

        Assert.Equal(["Сегодня в Самаре двадцать градусов.", "Лучше взять куртку, т. е. что-то тёплое!"], sentences);
        Assert.Equal("А вечером дождь.", splitter.Flush());
    }

    [Fact]
    public void Splitter_DoesNotCutDecimals()
    {
        var splitter = new SentenceSplitter();

        IReadOnlyList<string> done = splitter.Add("Температура 3.5 градуса и ветер 4.2 метра. Дальше");

        Assert.Equal(["Температура 3.5 градуса и ветер 4.2 метра."], done);
    }

    [Fact]
    public void StreamReader_AssemblesTextAndFunctionCall()
    {
        var reader = new GigaChatStreamReader();
        Assert.Equal("Откр", reader.Add("""{"choices":[{"delta":{"role":"assistant","content":"Откр"}}]}"""));
        Assert.Equal("ываю.", reader.Add("""{"choices":[{"delta":{"content":"ываю."}}]}"""));
        reader.Add("""{"choices":[{"delta":{"function_call":{"name":"Open_App","arguments":{"name":"vk"}}},"finish_reason":"function_call"}],"usage":{"prompt_tokens":10,"completion_tokens":3}}""");
        reader.Add("[DONE]");

        var reply = reader.Build();

        Assert.Equal("Открываю.", reply.Content);
        Assert.Equal("open_app", reply.ToolCalls.Single().Name);
        Assert.Equal("vk", reply.ToolCalls[0].Arguments.GetProperty("name").GetString());
        Assert.Equal(10, reply.PromptTokens);
        Assert.True(reader.Finished);
    }

    [Fact]
    public void StreamReader_ArgumentsAsText_AreParsed()
    {
        var reader = new GigaChatStreamReader();
        reader.Add("""{"choices":[{"delta":{"function_call":{"name":"search_web","arguments":"{\"query\":"}}}]}""");
        reader.Add("""{"choices":[{"delta":{"function_call":{"arguments":"\"цена\"}"}}}]}""");

        Assert.Equal("цена", reader.Build().ToolCalls.Single().Arguments.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("Нужно что-то ещё?", true)]
    [InlineData("Если хотите, могу рассказать подробнее.", true)]
    [InlineData("Сегодня двадцать градусов.", false)]
    public void IsOffer(string sentence, bool expected) =>
        Assert.Equal(expected, ReplyPolish.IsOffer(sentence));
}
