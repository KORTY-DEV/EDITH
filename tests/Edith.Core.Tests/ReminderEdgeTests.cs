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
