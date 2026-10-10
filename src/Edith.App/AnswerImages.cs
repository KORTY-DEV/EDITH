using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Edith.App;

/// <summary>
/// Pictures for the answer window: a file on this PC or an https link (shown as is), or one drawn by the cloud.
/// Nothing is saved or changed; the picture only goes onto the screen.
/// </summary>
public static class AnswerImages
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    private static readonly Regex PathInText = new(@"[A-Za-z]:\\[^\s""<>|*?]+?\.(?:png|jpe?g|gif|webp|bmp)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Raised on any thread when a picture should appear in the answer window.</summary>
    public static event Action<byte[], string>? Arrived;

    public static void Show(byte[] bytes, string caption) => Arrived?.Invoke(bytes, caption);

    /// <summary>An existing picture file named in an answer ("C:\...\chart.png"), if any.</summary>
    public static string? PathIn(string text)
    {
        foreach (Match m in PathInText.Matches(text))
            if (File.Exists(m.Value))
                return m.Value;
        return null;
    }

    /// <summary>Loads a picture from a local file or an https link; null with a reason when it cannot be shown.</summary>
    public static async Task<(byte[]? Bytes, string Reason)> LoadAsync(string source, CancellationToken cancellationToken)
    {
        source = source.Trim().Trim('"');
        try
        {
            if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using HttpResponseMessage response = await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (null, $"ссылка не открылась (HTTP {(int)response.StatusCode})");
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    return (null, "по ссылке не картинка");
                if (response.Content.Headers.ContentLength is > 8_000_000)
                    return (null, "картинка слишком большая");
                return (await response.Content.ReadAsByteArrayAsync(cancellationToken), "");
            }

            if (!Extensions.Contains(Path.GetExtension(source).ToLowerInvariant()))
                return (null, "это не картинка (png, jpg, gif, webp, bmp)");
            if (!File.Exists(source))
                return (null, "такого файла нет");
            if (new FileInfo(source).Length > 15_000_000)
                return (null, "файл слишком большой");
            return (await File.ReadAllBytesAsync(source, cancellationToken), "");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return (null, ex.Message);
        }
    }
}
