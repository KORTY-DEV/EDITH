using System.Diagnostics;
using System.Text;

namespace Edith.Audio.Wake;

public interface IWakeDiagnostics : IDisposable
{
    void ReportEngine(
        string engineLabel,
        string requested,
        string reason,
        IReadOnlyList<string> notes);

    void ReportModelStatus(
        string text);

    void ReportScore(
        float score,
        float threshold);

    void ReportDetection(
        WakeDetection detection);

    void ReportError(
        string context,
        string message);

    void ReportHybrid(WakeHybridStatus status)
    {
    }

    void ReportWakeV3(WakeV3Status status)
    {
    }
}


public static class WakeDiagnosticFiles
{
    public const string Engine =
        "wake-engine.txt";

    public const string ModelStatus =
        "wake-model-status.txt";

    public const string LastScore =
        "wake-last-score.txt";

    public const string LastDetection =
        "wake-last-detection.txt";

    public const string Error =
        "wake-error.txt";

    public const string Hybrid =
        "wake-hybrid.txt";

    public const string V3 =
        "wake-v3.txt";
}


// Решает, когда score "значимый" и его стоит записать на диск.
// Никакого raw audio, никаких записей на каждый кадр:
//
//   - молчание и шум (score < floor) не пишутся вообще;
//   - внутри всплеска пишется пик, не чаще раза в секунду;
//   - конец всплеска пишет итоговый пик;
//   - жёсткий лимит: не чаще одной записи в 250 ms.
public sealed class WakeScoreThrottle
{
    public const float MeaningfulFloor = 0.10f;

    private const long HardMinIntervalMs = 250;
    private const long BurstIntervalMs = 1000;

    private float _peak;
    private bool _dirty;
    private bool _inBurst;
    private bool _ended;
    private long _lastWriteMs =
        long.MinValue / 2;

    public bool TryReport(
        float score,
        long nowMs,
        out float peak)
    {
        peak = 0;

        if (!float.IsFinite(score))
            return false;

        if (score >= MeaningfulFloor)
        {
            _peak =
                Math.Max(_peak, score);

            _dirty = true;
            _inBurst = true;
        }
        else if (_inBurst)
        {
            _inBurst = false;
            _ended = true;
        }

        if (!_dirty)
        {
            // Всплеск уже целиком записан — забываем его пик.
            if (_ended)
            {
                _peak = 0;
                _ended = false;
            }

            return false;
        }

        if (nowMs - _lastWriteMs <
            HardMinIntervalMs)
        {
            return false;
        }

        // Внутри всплеска — раз в секунду, по концу — сразу.
        if (!_ended &&
            nowMs - _lastWriteMs <
            BurstIntervalMs)
        {
            return false;
        }

        peak = _peak;

        _lastWriteMs = nowMs;
        _dirty = false;

        if (_ended)
        {
            _peak = 0;
            _ended = false;
        }

        return true;
    }
}


// Записывает diagnostics в отдельном потоке:
// ни один вызов не делает file I/O в аудио-потоке.
// Очередь ограничена: хранится только последнее значение
// для каждого файла (5 файлов максимум).
public sealed class WakeDiagnostics : IWakeDiagnostics
{
    private readonly string _directory;
    private readonly Func<long> _clockMs;

    private readonly object _gate =
        new();

    private readonly Dictionary<string, string> _pending =
        new();

    private readonly SemaphoreSlim _signal =
        new(0);

    private readonly CancellationTokenSource _cts =
        new();

    private readonly Task _worker;

    private readonly WakeScoreThrottle _throttle =
        new();

    private bool _busy;
    private bool _disposed;

    public WakeDiagnostics(
        string directory,
        Func<long>? clockMs = null)
    {
        _directory =
            directory;

        _clockMs =
            clockMs ??
            (() =>
                (long)(Stopwatch.GetTimestamp() *
                       1000.0 /
                       Stopwatch.Frequency));

        _worker =
            Task.Factory.StartNew(
                WorkerLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
    }


    public void ReportEngine(
        string engineLabel,
        string requested,
        string reason,
        IReadOnlyList<string> notes)
    {
        var text =
            new StringBuilder();

        text.AppendLine(engineLabel);
        text.AppendLine(Stamp());
        text.AppendLine($"requested={requested}");
        text.AppendLine($"reason={reason}");

        foreach (string note in notes)
        {
            text.AppendLine(
                $"note={note}");
        }

        Enqueue(
            WakeDiagnosticFiles.Engine,
            text.ToString());
    }


    public void ReportModelStatus(
        string text)
    {
        Enqueue(
            WakeDiagnosticFiles.ModelStatus,
            Stamp() +
            Environment.NewLine +
            text);
    }


    public void ReportScore(
        float score,
        float threshold)
    {
        bool write;
        float peak;

        lock (_gate)
        {
            write =
                _throttle.TryReport(
                    score,
                    _clockMs(),
                    out peak);
        }

        if (!write)
            return;

        Enqueue(
            WakeDiagnosticFiles.LastScore,
            Stamp() +
            Environment.NewLine +
            "score=" + peak.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            Environment.NewLine +
            "threshold=" + threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }


    public void ReportDetection(
        WakeDetection detection)
    {
        Enqueue(
            WakeDiagnosticFiles.LastDetection,
            detection.At.ToString("O") +
            Environment.NewLine +
            $"engine={detection.Engine}" +
            Environment.NewLine +
            (detection.Engine == VoskWakeDetector.EngineName
                ? "score=n/a (text match)"
                : "score=" + detection.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
    }


    public void ReportError(
        string context,
        string message)
    {
        Enqueue(
            WakeDiagnosticFiles.Error,
            Stamp() +
            Environment.NewLine +
            $"context={context}" +
            Environment.NewLine +
            message);
    }


    public void ReportHybrid(WakeHybridStatus status)
    {
        Enqueue(
            WakeDiagnosticFiles.Hybrid,
            Stamp() + Environment.NewLine +
            "source=" + status.Source + Environment.NewLine +
            "reason=" + status.Reason + Environment.NewLine +
            "vosk-token=" + status.VoskToken + Environment.NewLine +
            "vosk-accepted=" + status.VoskAccepted + Environment.NewLine +
            "custom-accepted=" + status.CustomAccepted + Environment.NewLine +
            "weak-vosk-custom-accepted=" + status.WeakVoskCustomAccepted + Environment.NewLine +
            "custom-only-rejected=" + status.CustomOnlyRejected + Environment.NewLine +
            "both-observed=" + status.BothObserved + Environment.NewLine +
            "duplicate-suppressed=" + status.DuplicateSuppressed + Environment.NewLine +
            "custom-max-score=" + status.CustomMaxScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine +
            "custom-consecutive-hits=" + status.CustomConsecutiveHits + Environment.NewLine +
            "vad-speech-seen=" + status.VadSpeechSeen);
    }

    public void ReportWakeV3(WakeV3Status status)
    {
        Enqueue(
            WakeDiagnosticFiles.V3,
            Stamp() + Environment.NewLine +
            "stage1-score=" + status.LastStage1Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine +
            "verifier-score=" + status.LastVerifierScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine +
            "decision=" + status.LastDecision + Environment.NewLine +
            "reason=" + status.LastReason + Environment.NewLine +
            "stage1-candidates=" + status.Stage1Candidates + Environment.NewLine +
            "verifier-accepted=" + status.VerifierAccepted + Environment.NewLine +
            "verifier-rejected=" + status.VerifierRejected + Environment.NewLine +
            "latency-ms=" + status.LastVerifierMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }


    // Для тестов и корректного завершения:
    // ждёт, пока всё записано.
    public void Flush(
        TimeSpan? timeout = null)
    {
        var deadline =
            DateTime.UtcNow +
            (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_pending.Count == 0 &&
                    !_busy)
                {
                    return;
                }
            }

            Thread.Sleep(5);
        }
    }


    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        Flush(TimeSpan.FromSeconds(2));

        _cts.Cancel();

        try
        {
            _worker.Wait(
                TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _signal.Dispose();
        _cts.Dispose();
    }


    private void Enqueue(
        string file,
        string text)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _pending[file] =
                text;
        }

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }


    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            KeyValuePair<string, string>[] batch;

            lock (_gate)
            {
                if (_pending.Count == 0)
                    continue;

                batch =
                    _pending.ToArray();

                _pending.Clear();

                _busy = true;
            }

            try
            {
                Directory.CreateDirectory(
                    _directory);

                foreach (var item in batch)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(
                                _directory,
                                item.Key),
                            item.Value);
                    }
                    catch
                    {
                        // Diagnostics не должны ронять wake.
                    }
                }
            }
            catch
            {
            }
            finally
            {
                lock (_gate)
                {
                    _busy = false;
                }
            }
        }
    }


    private static string Stamp() =>
        DateTimeOffset.Now.ToString("O");
}
