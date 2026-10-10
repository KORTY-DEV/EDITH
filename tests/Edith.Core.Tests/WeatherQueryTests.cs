using Edith.Core;

namespace Edith.Core.Tests;

public sealed class WeatherQueryTests
{
    [Theory]
    [InlineData("какая погода на ближайшие семь дней в самаре", "самаре", 7, false)]
    [InlineData("ближайшая погода в самаре на семь дней", "самаре", 7, false)]
    [InlineData("какая погода в самаре на ближайшие семь дней", "самаре", 7, false)]
    [InlineData("какая сегодня погода в самаре", "самаре", 0, false)]
    [InlineData("погода в нижнем новгороде на неделю", "нижнем новгороде", 7, false)]
    [InlineData("какая погода завтра в казани", "казани", 1, true)]
    [InlineData("погода на 3 дня", "Самара", 3, false)]
    [InlineData("погода москва", "москва", 0, false)]
    public void Parses(string command, string city, int days, bool fromTomorrow) =>
        Assert.Equal(new WeatherQuery(city, days, fromTomorrow), WeatherQuery.Parse(command));

    [Theory]
    [InlineData("самаре", "самара")]
    [InlineData("казани", "казань")]
    [InlineData("нижнем новгороде", "нижний новгород")]
    public void CityCandidates_IncludeNominative(string city, string nominative) =>
        Assert.Contains(nominative, WeatherQuery.CityCandidates(city));
}
