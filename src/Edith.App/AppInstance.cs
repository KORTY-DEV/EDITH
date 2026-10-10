using System.Threading;

namespace Edith.App;

/// <summary>
/// Single running EDITH plus two named signals: "show" (a second start opens the panel of the
/// running one) and "exit" (scripts/Install-Edith.ps1 closes EDITH before replacing its files).
/// </summary>
public static class AppInstance
{
    private const string MutexName = @"Local\EDITH.App.Instance";
    public const string ShowEventName = @"Local\EDITH.App.Show";
    public const string ExitEventName = @"Local\EDITH.App.Exit";

    private static Mutex? _mutex;
    private static bool _owned;

    public static bool TryAcquire(bool wait)
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            _owned = _mutex.WaitOne(wait ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous EDITH died without releasing it: the mutex is ours now.
            _owned = true;
        }

        return _owned;
    }

    public static void Release()
    {
        if (_owned)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _owned = false;
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    public static void SignalShow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? handle))
            {
                handle.Set();
                handle.Dispose();
            }
        }
        catch
        {
        }
    }

    public static void ListenForSignals(Action show, Action exit)
    {
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() =>
        {
            WaitHandle[] handles = [showEvent, exitEvent];
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                if (which == 0)
                    show();
                else
                {
                    exit();
                    return;
                }
            }
        })
        { IsBackground = true, Name = "EDITH signals" }.Start();
    }
}
