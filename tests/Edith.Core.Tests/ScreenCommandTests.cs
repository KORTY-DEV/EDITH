using Edith.Core.Screen;

namespace Edith.Core.Tests;

public sealed class ScreenCommandTests
{
    [Theory]
    [InlineData("что на экране", ScreenCommandKind.Read, "")]
    [InlineData("Эдит, прочитай ошибку", ScreenCommandKind.Read, "")]
    [InlineData("что написано в окне?", ScreenCommandKind.Read, "")]
    [InlineData("что за ошибка", ScreenCommandKind.Read, "")]
    [InlineData("нажми кнопку «Сохранить»", ScreenCommandKind.Click, "сохранить")]
    [InlineData("нажми на да", ScreenCommandKind.Click, "да")]
    [InlineData("кликни вкладку настройки", ScreenCommandKind.Click, "настройки")]
    public void Parses(string phrase, ScreenCommandKind kind, string argument)
    {
        Assert.True(ScreenCommandParser.TryParse(phrase, out ScreenCommand command));
        Assert.Equal((kind, argument), (command.Kind, command.Argument));
    }

    [Fact]
    public void Type_KeepsOriginalCase()
    {
        Assert.True(ScreenCommandParser.TryParse("напечатай Привет, Паша!", out ScreenCommand command));
        Assert.Equal((ScreenCommandKind.Type, "Привет, Паша!"), (command.Kind, command.Argument));
    }

    [Theory]
    [InlineData("прочитай страницу")]
    [InlineData("открой телеграм")]
    [InlineData("что ты делала")]
    public void Ignores(string phrase)
    {
        Assert.False(ScreenCommandParser.TryParse(phrase, out _));
    }

    [Theory]
    [InlineData("Удалить", true)]
    [InlineData("Отправить", true)]
    [InlineData("Оплатить заказ", true)]
    [InlineData("Delete file", true)]
    [InlineData("Сохранить", false)]
    [InlineData("Отмена", false)]
    [InlineData("Далее", false)]
    public void RiskyButtons(string name, bool risky)
    {
        Assert.Equal(risky, UiTargets.IsRisky(name));
    }

    [Fact]
    public void Best_PrefersExactThenPrefixThenStemThenContains()
    {
        UiElementInfo[] elements =
        [
            new("Сохранить как…", "MenuItem"),
            new("Сохранить", "Button"),
            new("Настройки", "TabItem"),
            new("Открыть последний файл", "Hyperlink"),
        ];

        Assert.Equal("Сохранить", UiTargets.Best(elements, "сохранить")?.Name);
        Assert.Equal("Сохранить как…", UiTargets.Best(elements, "сохранить как")?.Name);
        Assert.Equal("Настройки", UiTargets.Best(elements, "настройках")?.Name);
        Assert.Equal("Открыть последний файл", UiTargets.Best(elements, "последний файл")?.Name);
        Assert.Null(UiTargets.Best(elements, "печать"));
    }

    [Fact]
    public void JoinText_DedupesAndBounds()
    {
        string text = UiTargets.JoinText(["Ошибка", "  Ошибка ", "Файл не найден:\n  C:\\x.txt", "a", new string('z', 5000)], maxChars: 100);

        Assert.StartsWith("Ошибка\nФайл не найден: C:\\x.txt\n", text);
        Assert.True(text.Length <= 100);
    }
}
