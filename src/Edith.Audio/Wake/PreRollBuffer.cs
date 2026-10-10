namespace Edith.Audio.Wake;

// Кольцевой буфер последних секунд аудио (float, 16 kHz mono).
// Живёт только в памяти: на диск ничего не пишется.
//
// Нужен, чтобы после срабатывания custom wake отдать Vosk
// начало произнесения ("эдит открой ..."), которое уже прошло
// через детектор, и получить текст команды одной фразой.
public sealed class PreRollBuffer
{
    private readonly float[] _ring;
    private readonly object _sync =
        new();

    private long _total;
    private long _validFrom;

    public PreRollBuffer(
        int capacitySamples)
    {
        if (capacitySamples <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacitySamples));
        }

        _ring =
            new float[capacitySamples];
    }

    public int Capacity =>
        _ring.Length;

    // Абсолютная позиция "сейчас" (монотонно растёт,
    // Clear её не сбрасывает).
    public long TotalWritten
    {
        get
        {
            lock (_sync)
            {
                return _total;
            }
        }
    }


    public void Write(
        ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;

        lock (_sync)
        {
            // Если пришло больше ёмкости — важен только хвост.
            long skipped = 0;

            if (samples.Length > _ring.Length)
            {
                skipped =
                    samples.Length - _ring.Length;

                samples =
                    samples[^_ring.Length..];
            }

            long writePosition =
                _total + skipped;

            int head =
                (int)(writePosition %
                      _ring.Length);

            int first =
                Math.Min(
                    samples.Length,
                    _ring.Length - head);

            samples[..first]
                .CopyTo(
                    _ring.AsSpan(head));

            if (first < samples.Length)
            {
                samples[first..]
                    .CopyTo(
                        _ring.AsSpan());
            }

            _total =
                writePosition +
                samples.Length;
        }
    }


    // Копирует аудио с абсолютной позиции `start` до "сейчас".
    // Слишком ранний start обрезается до доступного окна.
    // Возвращает число скопированных samples
    // (не больше dest.Length: берётся самый свежий хвост).
    public int CopySince(
        long start,
        Span<float> dest)
    {
        lock (_sync)
        {
            long earliest =
                Math.Max(
                    Math.Max(
                        _validFrom,
                        _total - _ring.Length),
                    0);

            long from =
                Math.Clamp(
                    start,
                    earliest,
                    _total);

            long count =
                _total - from;

            if (count > dest.Length)
            {
                from +=
                    count - dest.Length;

                count =
                    dest.Length;
            }

            if (count <= 0)
                return 0;

            int tail =
                (int)(from % _ring.Length);

            int first =
                (int)Math.Min(
                    count,
                    _ring.Length - tail);

            _ring.AsSpan(tail, first)
                .CopyTo(dest);

            if (first < count)
            {
                _ring.AsSpan(
                        0,
                        (int)count - first)
                    .CopyTo(
                        dest[first..]);
            }

            return (int)count;
        }
    }


    // Забывает всё накопленное (граница session):
    // старое аудио больше не может попасть в новую wake-фразу.
    public void Clear()
    {
        lock (_sync)
        {
            _validFrom =
                _total;
        }
    }
}
