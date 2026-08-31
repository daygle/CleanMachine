using Microsoft.UI.Xaml;

namespace CleanMachine.Windows;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    private BackgroundAgent? _agent;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            _agent = new BackgroundAgent(); _ = _agent.RunAsync();
        }
        MainWindow = new MainWindow(); MainWindow.Activate();
    }
}
