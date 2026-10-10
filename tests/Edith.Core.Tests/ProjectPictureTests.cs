using Edith.Core.Library;

namespace Edith.Core.Tests;

public sealed class ProjectPictureTests
{
    [Theory]
    [InlineData("Закрытие Slice 5 для EPURE", "", "Slice 5")]
    [InlineData("Верификация S4-P3", "", "Slice 4")]
    [InlineData("Работа над слайсом 3", "", "Slice 3")]
    [InlineData("Подготовка", "Начат этап 2 проекта", "Этап 2")]
    [InlineData("Документация Operations Handbook", "ничего про этапы", "Без выделенного этапа")]
    public void StageOf_FindsTheStageNamedInTheText(string title, string summary, string expected) =>
        Assert.Equal(expected, ProjectPicture.StageOf(title, summary).Label);

    [Fact]
    public void Render_GroupsByStage_InOrder_AndLinksTheNotes()
    {
        var notes = new[]
        {
            new PictureNote("2026-10-09 EPURE — Slice 5 итог", new DateOnly(2026, 10, 9), "Slice 5 итог", "Закрыт P3.", ["не проверен P4"]),
            new PictureNote("2026-09-10 EPURE — Slice 4 старт", new DateOnly(2026, 9, 10), "Slice 4 старт", "Начат четвёртый срез.", []),
            new PictureNote("2026-09-12 EPURE — Прочее", new DateOnly(2026, 9, 12), "Прочее", "Разное.", []),
        };

        string page = ProjectPicture.Render("EPURE", notes, "Идёт пятый срез.", "[[Хроника]]");

        Assert.True(page.IndexOf("### Slice 4", StringComparison.Ordinal) < page.IndexOf("### Slice 5", StringComparison.Ordinal));
        Assert.True(page.IndexOf("### Slice 5", StringComparison.Ordinal) < page.IndexOf("### Без выделенного этапа", StringComparison.Ordinal));
        Assert.Contains("[[2026-10-09 EPURE — Slice 5 итог|Slice 5 итог]]", page);
        Assert.Contains("Идёт пятый срез.", page);
        Assert.Contains("- не проверен P4", page);
        Assert.Contains("по 3 подробным записям", page);
    }
}
