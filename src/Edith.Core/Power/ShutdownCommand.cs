using System.Text.RegularExpressions;
using Edith.Core.Time;

namespace Edith.Core.Power;

public enum ShutdownCommandKind
{
    ScheduleAt,
    AfterAgent,
    Cancel,
    Status
}

public sealed record ShutdownCommand(ShutdownCommandKind Kind, DateTimeOffset? At = null);

/// <summary>
/// Voice phrases about switching the PC off later. Deliberately narrow: only "выключи
/// компьютер через/в ..." and "... когда агент закончит", plus cancel and status.
/// An immediate "выключи компьютер" is not recognized here on purpose.
/// </summary>
public static class ShutdownCommandParser
{
    private const string Pc = @"(?:компьютер|комп|пк|пэка|систему)";
    private const string Verb = @"(?:выключи|выключить|отключи|заверши работу)";

    private static readonly Regex Cancel = new(
        @"(?:отмени|отменить|останови|не надо)\s+(?:выключение|отключение)|не\s+выключай(?:\s+" + Pc + ")?",
        RegexOptions.Compiled);

    private static readonly Regex Status = new(
        @"(?:когда|во сколько)\s+(?:ты\s+)?(?:выключишь|выключится|будет выключение|выключение)|запланировано\s+(?:ли\s+)?выключение",
        RegexOptions.Compiled);

    // "когда агент закончит": the owner's own names for the agent come from config/agent-names.txt.
    private static readonly Regex AfterAgentRegex = new(
        Verb + @"\s+" + Pc + @".*\bкогда\s+(?:" + LocalLists.Alternation(["агент", "ии", "нейросеть", "ассистент"], "agent-names.txt") + @")\s*(?:код|code)?\s*(?:закончит|завершит|доделает|отработает)",
        RegexOptions.Compiled);

    private static readonly Regex In = new(
        Verb + @"\s+" + Pc + @"\s+" + SpokenTime.DelayPattern,
        RegexOptions.Compiled);

    private static readonly Regex At = new(
        Verb + @"\s+" + Pc + @"\s+" + SpokenTime.AtPattern,
        RegexOptions.Compiled);

    public static bool TryParse(string text, DateTimeOffset now, out ShutdownCommand command)
    {
        string input = SpokenTime.Normalize(text);
        command = new ShutdownCommand(ShutdownCommandKind.Status);

        if (Cancel.IsMatch(input))
        {
            command = new ShutdownCommand(ShutdownCommandKind.Cancel);
            return true;
        }

        if (Status.IsMatch(input))
        {
            command = new ShutdownCommand(ShutdownCommandKind.Status);
            return true;
        }

        if (AfterAgentRegex.IsMatch(input))
        {
            command = new ShutdownCommand(ShutdownCommandKind.AfterAgent);
            return true;
        }

        Match inMatch = In.Match(input);
        if (inMatch.Success &&
            SpokenTime.TryDelay(inMatch, out TimeSpan delay, TimeSpan.FromHours(24)) &&
            delay >= TimeSpan.FromMinutes(1))
        {
            command = new ShutdownCommand(ShutdownCommandKind.ScheduleAt, now + delay);
            return true;
        }

        Match atMatch = At.Match(input);
        if (atMatch.Success && SpokenTime.TryAt(atMatch, now, out DateTimeOffset at))
        {
            command = new ShutdownCommand(ShutdownCommandKind.ScheduleAt, at);
            return true;
        }

        return false;
    }
}
