using Edith.Core;
using Edith.Core.Brain;

namespace Edith.Core.Tests;

public sealed class ReplyPolishTests
{
    [Theory]
    [InlineData("Следю за компьютером, проверяю, всё ли в порядке. Нужно что-то ещё?", "Слежу за компьютером, проверяю, всё ли в порядке.")]
    [InlineData("Знаю, что твою маму зовут Татьяна. Если нужно — могу добавить больше деталей.", "Знаю, что твою маму зовут Татьяна.")]
    [InlineData("Это Пушкин. Если хочешь, могу продолжить или рассказать о нём подробнее.", "Это Пушкин.")]
    [InlineData("всё как обычно.", "Всё как обычно.")]
    [InlineData("Вы работаете над Улей. Что-нибудь добавить?", "Вы работаете над Улей.")]
    [InlineData("Готово. Хотите, чтобы я рассказала подробнее?", "Готово.")]
    [InlineData("Нужно что-то ещё?", "Нужно что-то ещё?")]
    [InlineData("Если завтра дождь, возьми зонт.", "Если завтра дождь, возьми зонт.")]
    public void Polish(string raw, string expected) =>
        Assert.Equal(expected, ReplyPolish.Apply(raw));

    [Theory]
    [InlineData("Это стих А.С. Пушкина.", "Это стих Пушкина.")]
    [InlineData("Занято 1,5 ГБ и 2,5 ГБ.", "Занято полтора гигабайта и два с половиной гигабайта.")]
    [InlineData("Грузит Uley Core.", "Грузит Улей коре.")]
    public void Speech(string raw, string expected) =>
        Assert.Equal(expected, VoiceText.ForSpeech(raw));
}
