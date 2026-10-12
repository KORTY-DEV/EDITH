using Edith.Core.Brain;

namespace Edith.App.Tests;

public sealed class VoiceTextTests
{
    [Theory]
    [InlineData("Результат 😀 отличный 👍! Готово ✅", "Результат отличный! Готово")]
    [InlineData("Проверьте [здесь](https://x.y).", "Проверьте здесь.")]
    [InlineData("<think>рассуждение</think>Итог готов.", "Итог готов.")]
    [InlineData("1. Откройте меню\n2. Нажмите кнопку\n3. Готово", "Откройте меню. Нажмите кнопку. Готово")]
    [InlineData("Температура +16°C, ветер 3 м/с.", "Температура плюс шестнадцать градусов, ветер три метра в секунду.")]
    [InlineData("Версия 1.0.7 вышла 10.10.2026.", "Версия один точка ноль точка семь вышла десятого октября две тысячи двадцать шестого года.")]
    [InlineData("Дорого, т.е. нужно подождать.", "Дорого, то есть нужно подождать.")]
    [InlineData("Стоит 3 тыс. руб. сейчас.", "Стоит три тысячи рублей сейчас.")]
    public void ForSpeech_CleansWhatTheEarCannotUse(string written, string spoken) => Assert.Equal(spoken, VoiceText.ForSpeech(written));

    [Theory]
    [InlineData("Файл лежит в C:/EDITH/config.", "Файл лежит в диск си, Эдит, конфиг.")]
    [InlineData("Откройте C:\\EDITH\\config\\a.json сейчас", "Откройте диск си, Эдит, конфиг, а точка джсон сейчас")]
    [InlineData("Он живёт в г. Самара на ул. Мира, д. 5, кв. 12.", "Он живёт в городе Самара на улица Мира, дом пять, квартира двенадцать.")]
    [InlineData("Это было в 2026 г. Он приехал.", "Это было в две тысячи двадцать шестого года. Он приехал.")]
    [InlineData("Погода из г. Москва.", "Погода из города Москва.")]
    public void PathsAndPlaces(string written, string spoken) => Assert.Equal(spoken, VoiceText.ForSpeech(written));

    [Fact]
    public void CodeBlocks_AreNotReadAloud() =>
        Assert.DoesNotContain("print", VoiceText.ForSpeech("Вот код: ```python\nprint(1)\n``` готово."));
}
