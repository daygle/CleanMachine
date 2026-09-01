using Microsoft.UI.Xaml;

namespace CleanMachine.Windows;

public partial class App : Application
{
    private BackgroundAgent? _agent;
    private CancellationTokenSource? _agentCts;

    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();

        var settings = await AppSettings.LoadAsync();
        if (settings.BackgroundAgentEnabled)
            StartBackgroundAgent(settings);
    }

    public void StartBackgroundAgent(AppSettings? settings = null)
    {
        StopBackgroundAgent();
        _agentCts = new CancellationTokenSource();
        _agent = new BackgroundAgent(
            onBrowserExit: async token =>
            {
                if (settings is null)
                    settings = await AppSettings.LoadAsync(token);
                if (settings.CleanOnBrowserExit)
                {
                    var cleanup = new BrowserCleanupService();
                    var targets = await cleanup.ScanAsync(
                        settings.ProtectedBrowsers, token: token);
                    await cleanup.CleanAsync(targets, requireBrowsersClosed: false, token);
                }
            });
        _ = _agent.RunAsync(_agentCts.Token);
    }

    public void StopBackgroundAgent()
    {
        _agentCts?.Cancel();
        _agent?.Dispose();
        _agent = null;
        _agentCts = null;
    }
}
