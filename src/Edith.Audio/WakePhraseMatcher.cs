namespace Edith.Audio;

public static class WakePhraseMatcher
{
    private static readonly HashSet<string> StrongWakeWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "эдит",
            "едит",
            "эди",
            "еди"
        };


    // Единственная реально зафиксированная в live diagnostics
    // ошибка короткого "Эдит". Это не самостоятельный wake.
    private static readonly HashSet<string> WeakWakeWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "едет"
        };


    private static readonly HashSet<string> LeadingFillers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "эй",
            "ну",
            "слушай"
        };


    private static readonly HashSet<string> CommandStarters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "сколько",

            "какая",
            "какой",
            "какое",
            "какие",
            "который",

            "открой",
            "открывай",
            "запусти",
            "закрой",

            "найди",
            "поищи",
            "загугли",

            "скажи",
            "покажи",
            "проверь",

            "включи",
            "выключи",

            "сделай",
            "создай",
            "удали",
            "установи",

            "перезапусти",
            "останови",

            "кто",
            "что",
            "где",
            "когда",
            "как",
            "почему",
            "зачем",

            "погода",
            "температура",
            "время",

            "новости",
            "курс",

            "повтори",
            "еще",

            "напомни",

            "можно",
            "нужно",

            "мне",
            "давай"
        };


    public static bool TryFindWake(
        string phrase,
        out string observedWake)
    {
        observedWake =
            string.Empty;

        string[] words =
            Split(phrase);

        if (words.Length == 0)
            return false;


        int wakeIndex =
            0;


        if (LeadingFillers.Contains(
                words[0]))
        {
            wakeIndex = 1;
        }


        if (wakeIndex >=
            words.Length)
        {
            return false;
        }


        string candidate =
            words[wakeIndex];


        // ---------------------------------------------
        // Нормальный wake.
        // Может работать даже отдельно:
        //
        // "Эдит"
        // ---------------------------------------------

        if (StrongWakeWords.Contains(
                candidate))
        {
            observedWake =
                candidate;

            return true;
        }


        return false;
    }


    public static bool TryExtractCommand(
        string phrase,
        out string command)
    {
        command =
            string.Empty;

        string[] words =
            Split(phrase);

        if (words.Length == 0)
            return false;


        int wakeIndex =
            0;


        if (LeadingFillers.Contains(
                words[0]))
        {
            wakeIndex =
                1;
        }


        if (wakeIndex >=
            words.Length)
        {
            return false;
        }


        string candidate =
            words[wakeIndex];


        bool matched =
            StrongWakeWords.Contains(
                candidate);


        if (!matched)
            return false;


        int tail =
            wakeIndex + 1;


        if (tail <
            words.Length)
        {
            command =
                string.Join(
                    ' ',
                    words[tail..])
                .Trim();
        }


        return true;
    }


    // Для custom-детектора: wake уже ПОДТВЕРЖДЁН акустической моделью,
    // поэтому это не поиск wake в тексте, а отделение того, что
    // Vosk услышал на месте "Эдит", от самой команды.
    //
    //   "эдит открой блокнот"  -> "открой блокнот"
    //   "идет открой блокнот"  -> "открой блокнот"   (Vosk ошибся в слове)
    //   "и дом открой блокнот" -> "дом открой блокнот" (неизбежная потеря)
    //   "открой блокнот"       -> "открой блокнот"   (Vosk слово пропустил)
    //   "эдит" / "едет"        -> ""                 (только wake)
    //
    // Никаких aliases в детектор это не добавляет: решение "был ли wake"
    // здесь уже принято моделью.
    public static string ExtractCommandAfterConfirmedWake(
        string phrase)
    {
        string[] words =
            Split(phrase);

        if (words.Length == 0)
            return string.Empty;


        // Pre-roll может захватить чужую речь перед "Эдит"
        // (телевизор): если чёткое "эдит" есть в первых словах,
        // команда — это всё, что после него.
        int strong =
            -1;

        for (int i = 0;
             i < Math.Min(words.Length, 5);
             i++)
        {
            if (StrongWakeWords.Contains(
                    words[i]))
            {
                strong = i;
                break;
            }
        }

        if (strong >= 0)
        {
            int after =
                strong + 1;

            while (after < words.Length &&
                   StrongWakeWords.Contains(
                       words[after]))
            {
                after++;
            }

            return after >= words.Length
                ? string.Empty
                : string.Join(
                        ' ',
                        words[after..])
                    .Trim();
        }


        int index =
            0;

        if (LeadingFillers.Contains(
                words[0]))
        {
            index = 1;
        }


        if (index >= words.Length)
            return string.Empty;


        string first =
            words[index];

        if (IsWakeLikeToken(first))
        {
            index++;

            while (index < words.Length &&
                   StrongWakeWords.Contains(
                       words[index]))
            {
                index++;
            }
        }
        else if (!CommandStarters.Contains(
                     first))
        {
            // Первое слово — искажённое "Эдит".
            index++;
        }


        if (index >= words.Length)
            return string.Empty;


        return string.Join(
                ' ',
                words[index..])
            .Trim();
    }


    public static bool IsWakeLikeToken(
        string word)
    {
        word =
            Normalize(word);

        return
            StrongWakeWords.Contains(word) ||
            WeakWakeWords.Contains(word);
    }

    // Vosk на живом MCHOSE-потоке иногда транскрибирует отдельное
    // «Эдит» как «едет». Это НЕ общий wake: вызывающая session
    // policy разрешает его только короткое время после своего ответа.
    public static bool IsIsolatedWeakWake(string phrase)
    {
        string[] words = Split(phrase);
        return words.Length == 1 && WeakWakeWords.Contains(words[0]);
    }


    private static string[] Split(
        string phrase)
    {
        phrase =
            Normalize(phrase);

        if (string.IsNullOrWhiteSpace(
                phrase))
        {
            return [];
        }


        return phrase.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
    }


    private static string Normalize(
        string text)
    {
        return text
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');
    }
}
