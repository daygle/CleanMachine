using System.Diagnostics;

namespace CleanMachine.Windows;

public sealed class BackgroundAgent : IDisposable
{
    private static readonly string[] BrowserProcessNames = ["chrome", "msedge", "firefox"];

    // Canonical setting ids keyed by process name, so per-browser monitoring
    // settings map from whichever process name was seen.
    private static readonly Dictionary<string, string> ProcessToBrowser = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "chrome",
        ["msedge"] = "edge",
        ["firefox"] = "firefox"
    };

    private readonly TimeSpan _pollInterval;
    private readonly Func<string, CancellationToken, Task> _onBrowserExit;
    private readonly Func<CancellationToken, Task>? _onTick;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, HashSet<int>> _knownPids = new(StringComparer.OrdinalIgnoreCase);
    public bool IsRunning { get; private set; }

    public BackgroundAgent(
        TimeSpan? pollInterval = null,
        Func<string, CancellationToken, Task>? onBrowserExit = null,
        Func<CancellationToken, Task>? onTick = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _onBrowserExit = onBrowserExit ?? ((_, token) => new CleanupService().CleanSelectedBrowsersAsync(token));
        _onTick = onTick;
        _timer = new PeriodicTimer(_pollInterval);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        IsRunning = true;
        try
        {
            while (await _timer.WaitForNextTickAsync(linked.Token))
            {
                // Current PIDs grouped by browser process name.
                var current = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (BrowserProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!current.TryGetValue(process.ProcessName, out var set))
                                current[process.ProcessName] = set = [];
                            set.Add(process.Id);
                        }
                    }
                    catch { }
                    finally { process.Dispose(); }
                }

                // A browser "exited" when it had processes last tick and none now.
                // Each browser is detected independently, so closing Chrome while
                // Edge keeps running still fires Chrome's exit action.
                var exited = new List<string>();
                foreach (var name in BrowserProcessNames)
                {
                    _knownPids.TryGetValue(name, out var known);
                    var wasRunning = known is { Count: > 0 };
                    var stillRunning = current.TryGetValue(name, out var nowSet) && nowSet.Count > 0;
                    if (wasRunning && !stillRunning &&
                        ProcessToBrowser.TryGetValue(name, out var browser))
                        exited.Add(browser);
                    _knownPids[name] = current.TryGetValue(name, out var set) ? set : [];
                }

                foreach (var browser in exited)
                    await _onBrowserExit(browser, linked.Token);

                if (_onTick is not null)
                    await _onTick(linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { IsRunning = false; }
    }

    public void Dispose() { _shutdown.Cancel(); _timer.Dispose(); _shutdown.Dispose(); }
}
