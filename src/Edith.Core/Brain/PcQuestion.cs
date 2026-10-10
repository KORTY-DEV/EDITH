using System.Text.RegularExpressions;

namespace Edith.Core.Brain;

/// <summary>
/// Questions about this PC's live state. A 4B model does not reliably call tools for them
/// and invents answers ("Windows 10…"), so the facts are gathered deterministically first.
/// </summary>
public static class PcQuestion
{
    private static readonly Regex Disk = new(@"\bдиск|места\b|место на|свободн\w* мест|хранилищ|ssd|hdd", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Memory = new(@"оперативк|\bозу\b|\bram\b|памят", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Processes = new(@"процесс|что (?:сейчас )?(?:ест|жр[её]т|грузит|нагружа)|кто (?:ест|жр[её]т|грузит)|тормоз|нагруз\w* (?:на )?(?:процессор|цп)|\bцп\b|процессор", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Gpu = new(@"видеокарт|\bgpu\b|видеопамят|vram|температур\w* (?:видеокарт|процессор|компьютер|пк|железа)",RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AboutThisPc = new(
        @"сколько|свободн|заня|\bест\b|жр[её]т|грузит|нагруз|температур|состояни|как (?:там|себя чувствует)|что с\b|провер|покажи|скажи|какая|какой",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // General knowledge questions ("что такое SSD", "чем отличается") are not about this PC.
    private static readonly Regex General = new(@"что такое|чем отлича|разниц|как работает|зачем нужн|почему|посоветуй|какой лучше|какая лучше|купить|стои[тм]|цен[аеуы]|дорог|дешев|самая|самый|лучш|\bтоп\b|новинк|вышл|выйдет|в рф|в росси|в магазин|на рынке|рейтинг|сравни|обзор", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Tools to run for the question, in order; empty when it is not about this PC.</summary>
    public static IReadOnlyList<string> Tools(string question)
    {
        string text = question.ToLowerInvariant().Replace('ё', 'е');
        if (General.IsMatch(text) || !AboutThisPc.IsMatch(text))
            return [];

        var tools = new List<string>();
        if (Disk.IsMatch(text) || (Memory.IsMatch(text) && !text.Contains("видеопамят")))
            tools.Add("system_snapshot");
        if (Processes.IsMatch(text))
            tools.Add("processes");
        if (Gpu.IsMatch(text))
            tools.Add("gpu_status");

        return tools;
    }
}
