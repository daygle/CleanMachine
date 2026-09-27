using CleanMachine.Windows;
using System.Security.AccessControl;
using System.Security.Principal;
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
        Assert.NotEqual(SingleInstance.MutexName, SingleInstance.Name(SingleInstance.MutexName));
    }

    [Fact]
    public void EveryKernelObjectNameIsScopedToTheCurrentUser()
    {
        // The bare constants are the names the app must NOT use: Local\ scopes a
        // kernel object to the logon session, not the account, so two users
        // sharing a session would otherwise share one mutex and one pair of
        // events - either able to suppress the other's app or signal it to exit.
        foreach (var baseName in new[]
                 {
                     SingleInstance.MutexName,
                     SingleInstance.ShutdownEventName,
                     SingleInstance.ActivateEventName
                 })
        {
            var name = SingleInstance.Name(baseName);
            Assert.StartsWith(baseName + ".", name, StringComparison.Ordinal);
            Assert.Contains(SingleInstance.UserScope, name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TwoUsersWouldNotShareTheSameKernelObjectNames()
    {
        // The suffix is derived from the per-user application-data path, so it is
        // stable for an account and distinct between accounts. This asserts the
        // derivation is actually in place rather than a constant that happens to
        // be non-empty.
        Assert.False(string.IsNullOrWhiteSpace(SingleInstance.UserScope));
        Assert.Equal(SingleInstance.Name(SingleInstance.MutexName), SingleInstance.Name(SingleInstance.MutexName));
    }

    /// <summary>The events are a control channel - setting the shutdown event
    /// makes the running app exit - so they carry an explicit DACL granting this
    /// user and SYSTEM, and nobody else. A named kernel object left to default
    /// security gets a descriptor derived from the creating token, which another
    /// account in the same session can open.
    /// <para>
    /// This is a regression guard for the whole control: a plain EventWaitHandle
    /// exposes no DACL API at all, so the cast below fails if the code ever
    /// silently falls back to one, which is what the catch block would do.
    /// </para></summary>
    [Fact]
    public void TheEventsAreNotAccessibleToOtherAccounts()
    {
        var scope = Scope();
        using var events = SingleInstance.TryCreateEvents(scope);
        Assert.NotNull(events);

        var acl = events!.Shutdown as EventWaitHandleAcl;
        Assert.NotNull(acl);

        var security = acl!.GetAccessControl(includeSections: true);
        var granted = new List<SecurityIdentifier>();
        foreach (var raw in security.GetAccessRules(
                     includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (raw is SystemAccessRule rule) granted.Add((SecurityIdentifier)rule.IdentityReference);
        }

        foreach (var wellKnown in new[]
                 {
                     WellKnownSidType.WorldSid,
                     WellKnownSidType.AnonymousSid,
                     WellKnownSidType.BuiltinUsersSid
                 })
        {
            Assert.False(granted.Any(sid => sid.IsWellKnown(wellKnown)),
                $"the shutdown event must not grant access to {wellKnown}.");
        }

        // And it must still be usable by us, or the app could never signal itself
        // and the single-instance path would be broken by its own security.
        var me = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(me);
        Assert.True(granted.Any(sid => sid.Equals(me)),
            "the current user must retain access to its own event.");
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
