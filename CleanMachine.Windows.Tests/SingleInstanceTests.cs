using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Tests for the single-instance guard that keeps one GUI process alive
/// and lets the installer/uninstaller shut it down. A unique scope per run keeps
/// the tests isolated from a live CleanMachine instance (and from each other).</summary>
public sealed class SingleInstanceTests
{
    private static string Scope() => $"test-{Guid.NewGuid():N}";

    [Fact]
    public void SecondAcquireFailsWhileFirstHoldsTheMutex()
    {
        var scope = Scope();
        using var first = SingleInstance.TryAcquire(scope);
        Assert.NotNull(first);

        // A second process acquiring the same name must see the instance as taken.
        Assert.Null(SingleInstance.TryAcquire(scope));
    }

    [Fact]
    public void AcquireSucceedsAgainAfterTheFirstReleases()
    {
        var scope = Scope();
        var first = SingleInstance.TryAcquire(scope);
        Assert.NotNull(first);
        first!.Dispose(); // instance exited

        using var second = SingleInstance.TryAcquire(scope);
        Assert.NotNull(second);
    }

    [Fact]
    public void ScopedNamesDifferFromTheLiveAppNames()
    {
        // Tests must never touch the live app's kernel objects.
        var scope = Scope();
        Assert.NotEqual(SingleInstance.MutexName, SingleInstance.Name(SingleInstance.MutexName, scope));
        Assert.Equal(SingleInstance.MutexName, SingleInstance.Name(SingleInstance.MutexName));
    }

    [Fact]
    public void ActivateSignalIsObservedByTheOwningInstance()
    {
        var scope = Scope();
        using var events = SingleInstance.TryCreateEvents(scope);
        Assert.NotNull(events);

        var observed = 0;
        var listener = new Thread(() =>
        {
            var index = WaitHandle.WaitAny([events!.Shutdown, events.Activate]);
            Interlocked.Exchange(ref observed, index);
        });
        listener.Start();

        // Signal the exact event the listener waits on (the scoped activate event).
        events!.Activate.Set();

        Assert.True(listener.Join(TimeSpan.FromSeconds(5)), "listener did not finish");
        Assert.Equal(1, Volatile.Read(ref observed));
    }

    [Fact]
    public void ShutdownSignalIsObservedByTheOwningInstance()
    {
        var scope = Scope();
        using var events = SingleInstance.TryCreateEvents(scope);
        Assert.NotNull(events);

        var observed = false;
        var listener = new Thread(() => observed = events!.Shutdown.WaitOne(TimeSpan.FromSeconds(5)));
        listener.Start();

        events!.Shutdown.Set();

        Assert.True(listener.Join(TimeSpan.FromSeconds(5)), "listener did not finish");
        Assert.True(observed, "shutdown event was not observed");
    }

    [Fact]
    public void TryCreateEventsWithScopeReturnsDistinctUnsignaledHandles()
    {
        using var events = SingleInstance.TryCreateEvents(Scope());
        Assert.NotNull(events);
        Assert.False(ReferenceEquals(events!.Shutdown, events.Activate));
        // Neither event starts signaled.
        Assert.False(events.Shutdown.WaitOne(0));
        Assert.False(events.Activate.WaitOne(0));
    }
}
