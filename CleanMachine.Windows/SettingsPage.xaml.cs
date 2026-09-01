using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class SettingsPage : Page
{
    private AppSettings _settings = new();

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();
        AgentToggle.IsChecked = _settings.BackgroundAgentEnabled;
        CleanToggle.IsChecked = _settings.CleanOnBrowserExit;
        UpdateCheckToggle.IsChecked = _settings.CheckForUpdatesAutomatically;
        WipeMethodCombo.SelectedIndex = _settings.SecureDeleteMethod switch
        {
            WipeMethod.Dod522022M => 1,
            WipeMethod.Dod522022MEce => 2,
            WipeMethod.PeterGutmann => 3,
            WipeMethod.Custom => 4,
            _ => 0
        };
        ExclusionsBox.Text = string.Join("\n", _settings.ExcludedPaths);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundAgentEnabled = AgentToggle.IsChecked == true;
        _settings.CleanOnBrowserExit = CleanToggle.IsChecked == true;
        _settings.CheckForUpdatesAutomatically = UpdateCheckToggle.IsChecked == true;
        _settings.SecureDeleteMethod = WipeMethodCombo.SelectedIndex switch
        {
            1 => WipeMethod.Dod522022M,
            2 => WipeMethod.Dod522022MEce,
            3 => WipeMethod.PeterGutmann,
            4 => WipeMethod.Custom,
            _ => WipeMethod.SimpleZeroFill
        };
        _settings.ExcludedPaths = ExclusionsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _settings.SaveAsync();

        try
        {
            StartupRegistration.SetEnabled(
                _settings.BackgroundAgentEnabled,
                Environment.ProcessPath ?? string.Empty);
        }
        catch { /* startup registration is best-effort */ }

        StatusText.Text = "Settings saved.";
    }
}
