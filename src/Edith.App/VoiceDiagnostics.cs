using System.Globalization;
using System.IO;
using Edith.Audio;

namespace Edith.App;

/// <summary>
/// Best-effort text diagnostics of the voice session in C:\EDITH\data\diagnostics.
/// A diagnostics write must never break the voice pipeline, so every failure is swallowed.
/// </summary>
internal static class VoiceDiagnostics
{
    public const string Directory = @"C:\EDITH\data\diagnostics";

    /// <summary>Overwrites <paramref name="fileName"/> with a timestamp line followed by <paramref name="lines"/>.</summary>
    public static void WriteSnapshot(string fileName, params string[] lines) =>
        WriteRaw(fileName, DateTimeOffset.Now.ToString("O") + Environment.NewLine + string.Join(Environment.NewLine, lines));

    /// <summary>Overwrites <paramref name="fileName"/> with <paramref name="content"/> as is (in the background).</summary>
    public static void WriteRaw(string fileName, string content) =>
        Queue.Writer.TryWrite((Path.Combine(Directory, fileName), content, false));

    // Diagnostics used to be written synchronously on the audio and UI threads (15-20 files
    // per interaction). One background writer keeps them off the voice path entirely.
    private static readonly System.Threading.Channels.Channel<(string Path, string Content, bool Append)> Queue =
        StartWriter();

    private static System.Threading.Channels.Channel<(string, string, bool)> StartWriter()
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<(string, string, bool)>(
            new System.Threading.Channels.BoundedChannelOptions(2000)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

        var thread = new Thread(() =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
            }
            catch
            {
            }

            foreach ((string path, string content, bool append) in channel.Reader.ReadAllAsync().ToBlockingEnumerable())
            {
                try
                {
                    if (append)
                        File.AppendAllText(path, content);
                    else
                        File.WriteAllText(path, content);
                }
                catch
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "edith-diagnostics",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return channel;
    }

    public static void VoiceEvent(string name, string details) =>
        WriteSnapshot("voice-" + name + ".txt", details);

    public static void FullPhrase(long session, string phrase) =>
        WriteSnapshot("last-full-utterance.txt", $"session={session}", phrase);

    public static void Transcript(
        long session,
        CommandTranscript transcript,
        DateTimeOffset speechStartedAt,
        double speechDurationMilliseconds,
        double sttLatencyMilliseconds) =>
        WriteSnapshot(
            "last-transcript.txt",
            $"session={session}",
            $"speech-start={speechStartedAt:O}",
            $"raw-transcript={transcript.RawText}",
            $"normalized-transcript={transcript.NormalizedText}",
            $"stt-engine={transcript.Engine}",
            $"confidence={transcript.Confidence?.ToString("F3", CultureInfo.InvariantCulture) ?? "unknown"}",
            $"speech-duration-ms={speechDurationMilliseconds:F0}",
            $"stt-latency-ms={sttLatencyMilliseconds:F0}");

    public static void AppendInteractionTrace(long session, string section, string value) =>
        Queue.Writer.TryWrite((
            Path.Combine(Directory, "interaction-trace.txt"),
            DateTimeOffset.Now.ToString("O") + $"; session={session}; {section}:" + Environment.NewLine + value.Trim() + Environment.NewLine,
            true));
}
