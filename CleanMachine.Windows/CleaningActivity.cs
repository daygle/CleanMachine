namespace CleanMachine.Windows;

/// <summary>Reference-counted "a cleanup is running now" flag shared by every
/// cleaning path: manual page cleans, Quick Clean, scheduled/idle/startup runs,
/// browser-exit monitoring, Recycle Bin auto-empty, secure delete and drive wipe.
/// Each path wraps its work in <c>using CleaningActivity.Begin()</c>; overlapping
/// cleans keep the state active until the last scope ends. <see cref="Changed"/>
/// fires only on the 0-&gt;1 and 1-&gt;0 transitions, on whichever thread crosses
/// them, so subscribers must marshal to their own thread (MainWindow hops to the
/// UI queue before animating the tray icon).</summary>
internal static class CleaningActivity
{
    private static int _active;

    /// <summary>Raised with true when the first scope opens and false when the
    /// last one closes.</summary>
    internal static event Action<bool>? Changed;

    /// <summary>True while at least one <see cref="Begin"/> scope is open.</summary>
    internal static bool IsActive => Volatile.Read(ref _active) > 0;

    /// <summary>Marks a cleanup as running until the returned scope is disposed.
    /// Dispose is idempotent, so double-disposal can never underflow the count.</summary>
    internal static IDisposable Begin()
    {
        if (Interlocked.Increment(ref _active) == 1)
            Changed?.Invoke(true);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            if (Interlocked.Decrement(ref _active) == 0)
                Changed?.Invoke(false);
        }
    }
}
