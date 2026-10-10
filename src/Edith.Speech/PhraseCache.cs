using System.Security.Cryptography;
using System.Text;

namespace Edith.Speech;

/// <summary>
/// Ready-made voice for the short phrases EDITH says again and again ("Хорошо.", "Секунду.", "Запомнила."):
/// they are rendered once and played from disk, so they start at once and need no synthesis.
/// The key holds the voice, the speed and the exact text sent to the synthesizer, so a changed
/// voice or speed simply misses the cache instead of playing a stale recording.
/// </summary>
public static class PhraseCache
{
    public const string Folder = @"C:\EDITH\data\phrases";

    public static string PathFor(string voice, double speed, string speechText)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{voice}|{speed:F2}|{speechText}"));
        return Path.Combine(Folder, Convert.ToHexString(hash)[..20] + ".wav");
    }

    /// <summary>Copies the recording to <paramref name="output"/> (the player deletes its file afterwards).</summary>
    public static bool TryCopy(string voice, double speed, string speechText, string output)
    {
        try
        {
            string path = PathFor(voice, speed, speechText);
            if (!File.Exists(path))
                return false;

            File.Copy(path, output, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Short fixed replies and acknowledgements; dynamic answers are never cached.</summary>
    public static readonly string[] Phrases =
    [
        "Слушаю.", "Да?", "Слушаю вас.", "Я вас слушаю.", "Готово.", "Хорошо.", "Конечно.", "Принято.", "Понятно.",
        "Секунду.", "Одну секунду.", "Минутку.", "Сейчас посмотрю.", "Сейчас проверю.", "Сейчас найду.", "Ищу информацию.",
        "Запомнила.", "Хорошо, запомнила.", "Хорошо, я это забыла.", "Отменила.", "Отменено.", "Хорошо, молчу.", "Остановилась.",
        "Не поняла, повторите, пожалуйста.", "Не расслышала, повторите, пожалуйста.", "Не получилось.", "Не нашла.",
        "Ничего нового пока не заметила.", "Не смогла этого сделать.", "Локальный ИИ не успел ответить.",
        "Доброе утро.", "Добрый день.", "Добрый вечер.", "Доброй ночи.",
        "Открываю браузер.", "Открываю Диспетчер задач.", "Открываю Telegram.", "Открываю Opera GX.", "Открываю проводник.",
        "Слушаю, сэр.", "Да, сэр?", "К вашим услугам.", "К вашим услугам, сэр.", "Как пожелаете, сэр.", "Как пожелаете.", "Будет исполнено.",
        "Будет исполнено, сэр.", "Одну секунду, сэр.", "Сию минуту, сэр.", "Сию минуту.", "Всё готово, сэр.", "Готово, сэр.", "Разумеется, сэр.",
        "Принято, сэр.", "Боюсь, это не удалось.", "Боюсь, я не расслышала. Повторите, пожалуйста, сэр.", "Как скажете, сэр.", "Слушаю вас, сэр.",
        "Доброе утро, сэр.", "Добрый день, сэр.", "Добрый вечер, сэр.", "Доброй ночи, сэр.", "Рада вас слышать, сэр.", "С возвращением, сэр.",
        "Пожалуйста.", "Всегда пожалуйста.", "Рада помочь.", "Нужна помощь — зовите.",
    ];
}
