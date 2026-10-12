using Edith.Core.Reminders;

namespace Edith.Core.Tests;

public sealed class ReminderEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 23, 50, 0, TimeSpan.FromHours(4));

    [Fact]
    public void BareTimeOfDay_IsNotAReminderText()
    {
        Assert.False(ReminderParser.TryParse("напомни в 7 вечера", Now, out _));
        Assert.False(ReminderParser.TryParse("напомни сегодня в 8 утра", Now, out _));
    }

    [Fact]
    public void EveningHour_WithText_IsInTheEvening()
    {
        Assert.True(ReminderParser.TryParse("напомни мне позвонить в 5 вечера", Now, out ReminderCommand c));
        Assert.Equal(17, c.DueAt!.Value.Hour);
        Assert.Equal("позвонить", c.Text);
    }

    [Fact]
    public void Days_AndTwoWordMinutes_AreUnderstood()
    {
        Assert.True(ReminderParser.TryParse("напомни через два дня про оплату", Now, out ReminderCommand days));
        Assert.Equal(Now.AddDays(2), days.DueAt);
        Assert.True(ReminderParser.TryParse("напомни через двадцать пять минут выпить воды", Now, out ReminderCommand mins));
        Assert.Equal(Now.AddMinutes(25), mins.DueAt);
        Assert.Equal("выпить воды", mins.Text);
    }
}

public sealed class MidnightNoonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 23, 50, 0, TimeSpan.FromHours(4));

    [Fact]
    public void MidnightAndNoon_AreUnderstood()
    {
        Assert.True(ReminderParser.TryParse("напомни в полночь проверить сервер", Now, out ReminderCommand night));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(4)), night.DueAt);
        Assert.True(ReminderParser.TryParse("напомни завтра в полдень позвонить в банк", Now, out ReminderCommand noon));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(4)), noon.DueAt);
        Assert.True(Edith.Core.Power.ShutdownCommandParser.TryParse("выключи компьютер в полночь", Now, out var shutdown));
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(4)), shutdown.At);
    }
}

public sealed class SentenceSplitterAbbreviationTests
{
    [Fact]
    public void ThousandRoubles_IsNotTwoSentences()
    {
        var splitter = new Edith.Core.Brain.SentenceSplitter();
        var parts = splitter.Add("Видеокарта сейчас стоит около 300 тыс. руб. в магазинах города. Это дорого. ").ToList();
        Assert.Equal("Видеокарта сейчас стоит около 300 тыс. руб. в магазинах города.", parts[0]);
    }
}
