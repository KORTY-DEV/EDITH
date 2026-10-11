using System.Text.Json;

namespace Edith.Core;

/// <summary>
/// Reads a data file that EDITH also rewrites (memories, reminders, queues). A read that fails must never end as "empty list, then the
/// next save overwrites everything": a locked file is retried, an unreadable one is copied aside before an empty start.
/// </summary>
public static class SafeJsonFile
{
    public static T Load<T>(string? path, Func<T> empty)
    {
        if (path is null || !File.Exists(path))
            return empty();

        string? text = null;
        for (int attempt = 0; attempt < 4 && text is null; attempt++)
        {
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(60 * (attempt + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return KeepAside<T>(path, empty);
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            return empty();

        try
        {
            return JsonSerializer.Deserialize<T>(text) ?? empty();
        }
        catch (JsonException)
        {
            return KeepAside<T>(path, empty);
        }
    }

    private static T KeepAside<T>(string path, Func<T> empty)
    {
        try
        {
            string copy = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (!File.Exists(copy))
                File.Copy(path, copy);
        }
        catch
        {
            // Nothing more can be done; the original stays where it is.
        }

        return empty();
    }
}
