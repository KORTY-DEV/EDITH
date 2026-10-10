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
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Wikimedia asks every client to introduce itself.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EDITH/1.0 (+https://github.com/KORTY-DEV/EDITH)");
        return client;
    }

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

    /// <summary>
    /// Finds a real picture of something on Wikimedia Commons (free images, no key): the best match that is a photo or drawing
    /// at least 500 px wide. The model must never invent a link: this is where links come from.
    /// </summary>
    public static async Task<(byte[]? Bytes, string Caption, string Reason)> FindAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            string url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=10&gsrsearch=" +
                         Uri.EscapeDataString(query) + "&prop=imageinfo&iiprop=url%7Cmime%7Csize&iiurlwidth=900";
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync(url, cancellationToken));
            if (!document.RootElement.TryGetProperty("query", out System.Text.Json.JsonElement q) || !q.TryGetProperty("pages", out System.Text.Json.JsonElement pages))
                return (null, "", "по этому запросу картинок нет");

            var candidates = new List<(int Order, string Title, string Thumb)>();
            foreach (System.Text.Json.JsonProperty page in pages.EnumerateObject())
            {
                System.Text.Json.JsonElement p = page.Value;
                if (!p.TryGetProperty("imageinfo", out System.Text.Json.JsonElement info) || info.GetArrayLength() == 0)
                    continue;
                System.Text.Json.JsonElement i = info[0];
                string mime = i.TryGetProperty("mime", out System.Text.Json.JsonElement m) ? m.GetString() ?? "" : "";
                int width = i.TryGetProperty("width", out System.Text.Json.JsonElement w) ? w.GetInt32() : 0;
                if (mime is not ("image/jpeg" or "image/png") || width < 500 || !i.TryGetProperty("thumburl", out System.Text.Json.JsonElement t))
                    continue;
                candidates.Add((p.TryGetProperty("index", out System.Text.Json.JsonElement ix) ? ix.GetInt32() : 99,
                    p.TryGetProperty("title", out System.Text.Json.JsonElement title) ? title.GetString() ?? "" : "", t.GetString() ?? ""));
            }

            foreach ((int _, string title, string thumb) in candidates.OrderBy(c => c.Order))
            {
                (byte[]? bytes, _) = await LoadAsync(thumb, cancellationToken);
                if (bytes is not null)
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(title.Replace("File:", ""));
                    return (bytes, name + " · Wikimedia Commons", "");
                }
            }

            return (null, "", "подходящих картинок не нашлось");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, "", "не удалось поискать картинку: " + ex.Message);
        }
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
