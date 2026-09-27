using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

public sealed partial class AboutPage : Page
{
    private static readonly Brush IdleBrush = BrushFromHex("#00000000"); // fully transparent
    private static readonly Brush HoverBrush = BrushFromHex("#F3F8F5");
    private static readonly Brush PressedBrush = BrushFromHex("#EAF4EE");

    private bool _rowFeedbackAttached;

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = "Version " + ResolveVersion();
        Loaded += (_, _) => AttachRowFeedback();
    }

    /// <summary>The version shown in Settings > About, i.e. the one the user can
    /// quote in a bug report. The MSIX package version is authoritative for a
    /// Store install because the release workflow stamps it per submission; the
    /// assembly version is the fallback for an unpackaged run off the repo.</summary>
    private static string ResolveVersion()
    {
        try
        {
            var v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            // Not running as a packaged app, so Package.Current throws.
        }

        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commit sha>" build metadata the SDK appends.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var value = uint.Parse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber);
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            (byte)(value >> 16), (byte)(value >> 8), (byte)value));
    }

    /// <summary>Hover/press tint for the link rows. Attached on Loaded rather than
    /// in the constructor because the buttons are named in markup and only exist
    /// after InitializeComponent.</summary>
    private void AttachRowFeedback()
    {
        // Loaded fires again if the frame recycles this page, which would stack a
        // second set of handlers and double-apply the hover tint.
        if (_rowFeedbackAttached) return;
        _rowFeedbackAttached = true;

        foreach (var button in new[] { SponsorsRow, SourceRow, PrivacyRow, IssueRow })
        {
            button.PointerEntered += (s, _) => { var b = (Button)s; b.Background = HoverBrush; };
            button.PointerExited += (s, _) => { var b = (Button)s; b.Background = IdleBrush; };
            button.PointerPressed += (s, _) => { var b = (Button)s; b.Background = PressedBrush; };
            button.PointerReleased += (s, _) => { var b = (Button)s; b.Background = HoverBrush; };
        }
    }

    /// <summary>Opens a project link in the user's browser. CleanMachine itself
    /// makes no network connections - it hands the URL to the shell and the
    /// browser does the talking - which is what keeps the no-network privacy
    /// claim true while still offering the links.</summary>
    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser, or the shell refused the URL. The link is decorative
            // here, so a silent no-op beats a crash on a cosmetic action.
        }
    }
}
