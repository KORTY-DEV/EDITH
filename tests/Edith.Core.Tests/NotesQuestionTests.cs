using Edith.Core.Brain;

namespace Edith.Core.Tests;

public sealed class NotesQuestionTests
{
    [Theory]
    [InlineData("что у меня записано в заметках про эдит", true)]
    [InlineData("какие у меня сейчас задачи по улей", true)]
    [InlineData("что по иду", true)]
    [InlineData("на чем я остановился в улье", true)]
    [InlineData("посмотри в обсидиане что я планировал", true)]
    [InlineData("какие у меня предметы в идэу", true)]
    [InlineData("открой обсидиан", false)]
    [InlineData("какая погода в самаре", false)]
    [InlineData("что такое эдит", false)]
    [InlineData("как у тебя дела", false)]
    [InlineData("сколько стоит видеокарта", false)]
    public void Matches(string question, bool expected) =>
        Assert.Equal(expected, NotesQuestion.Matches(question));

    [Fact]
    public void Synonyms_MapRussianSpeechToTheLatinNamesInTheNotes()
    {
        Assert.Equal(["uley"], NotesQuestion.Synonyms("что по улею"[..^1] + "й"));
        Assert.Equal(["edith", "ideu"], NotesQuestion.Synonyms("эдит и иду"));
        Assert.Empty(NotesQuestion.Synonyms("погода"));
    }
}
