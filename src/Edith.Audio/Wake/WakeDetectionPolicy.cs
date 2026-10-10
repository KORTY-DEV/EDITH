namespace Edith.Audio.Wake;

// Чистая логика решения "это wake" по потоку score.
// Без времени/аудио/ONNX внутри, поэтому полностью тестируется.
//
//  - threshold:          score >= threshold считается hit;
//  - minConsecutiveHits: нужно N hit подряд (score кадр = 80 ms);
//  - cooldown:           после срабатывания wake молчит cooldownMs;
//  - latch:              после срабатывания нужен хотя бы один
//                        score ниже threshold, иначе одно и то же
//                        длинное произнесение выстрелит второй раз
//                        сразу после окончания cooldown.
public sealed class WakeDetectionPolicy
{
    private readonly float _threshold;
    private readonly int _minConsecutiveHits;
    private readonly long _cooldownMs;

    private int _hits;
    private bool _latched;
    private long _cooldownUntilMs;
    private bool _hasCooldown;

    public WakeDetectionPolicy(
        float threshold,
        int minConsecutiveHits,
        int cooldownMs)
    {
        if (!(threshold > 0.0f &&
              threshold <= 1.0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold));
        }

        if (minConsecutiveHits < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minConsecutiveHits));
        }

        if (cooldownMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cooldownMs));
        }

        _threshold =
            threshold;

        _minConsecutiveHits =
            minConsecutiveHits;

        _cooldownMs =
            cooldownMs;
    }

    public float Threshold =>
        _threshold;

    public int ConsecutiveHits =>
        _hits;


    // nowMs — монотонное время в миллисекундах.
    // true = wake сработал на этом score.
    /// <param name="mayFire">
    /// False while a second stage (the V3 verifier) has not vouched for this run yet: the hit
    /// still counts towards <c>minConsecutiveHits</c>, but the wake is not reported.
    /// </param>
    public bool Evaluate(
        float score,
        long nowMs,
        bool mayFire = true)
    {
        // NaN/Infinity от испорченной модели — не hit.
        bool hit =
            float.IsFinite(score) &&
            score >= _threshold;

        if (!hit)
        {
            _hits = 0;
            _latched = false;

            return false;
        }

        // Cooldown: hit не накапливаем, иначе сразу
        // после окончания cooldown wake выстрелит на
        // "хвосте" того же слова.
        if (_hasCooldown &&
            nowMs < _cooldownUntilMs)
        {
            _hits = 0;

            return false;
        }

        if (_latched)
        {
            // Score всё ещё выше порога после прошлого
            // срабатывания: это то же самое произнесение.
            return false;
        }

        _hits++;

        if (_hits < _minConsecutiveHits || !mayFire)
        {
            return false;
        }

        _hits = 0;
        _latched = true;
        _hasCooldown = true;
        _cooldownUntilMs =
            nowMs + _cooldownMs;

        return true;
    }


    // Сброс накопленных hit. Cooldown сохраняется:
    // Reset на границе session не должен разрешать второй
    // wake от того же произнесения.
    public void Reset(
        bool clearCooldown = false)
    {
        _hits = 0;

        // latch тоже держим: после Reset аудио-окно уже
        // очищено, поэтому latch снимется при первом же
        // score ниже threshold (то есть сразу).
        _latched = false;

        if (clearCooldown)
        {
            _hasCooldown = false;
            _cooldownUntilMs = 0;
        }
    }
}
