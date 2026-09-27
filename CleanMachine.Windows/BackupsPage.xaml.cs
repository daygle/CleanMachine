using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class BackupsPage : Page
{
    public BackupsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadBackups();
    }

    private void LoadBackups()
    {
        var backups = RegistryCareService.ListBackups();
        BackupsPanel.Children.Clear();

        var count = backups.Count;
        var totalBytes = backups.Sum(b => File.Exists(b.FilePath) ? new FileInfo(b.FilePath).Length : 0);
        CountText.Text = $"{count} {(count == 1 ? "backup" : "backups")}";
        SizeText.Text = $"{AppNotifications.FormatBytes(totalBytes)} total";

        if (count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            FooterText.Text = "Run a registry clean to create restore points here.";
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        foreach (var backup in backups)
            BackupsPanel.Children.Add(BuildBackupCard(backup));
        FooterText.Text = $"Stored in {RegistryCareService.BackupsDirectory}.";
    }

    private Border BuildBackupCard(RegistryBackup backup)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44, GridUnitType.Pixel) }); // icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // info
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // actions

        var iconBorder = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE4, 0xF0, 0xF3)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE8B7",
                FontSize = 14,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(iconBorder, 0);

        var size = File.Exists(backup.FilePath) ? new FileInfo(backup.FilePath).Length : 0;
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = BackupScope(backup),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var details = $"{backup.CreatedAt.ToLocalTime():MMM d, yyyy - h:mm tt}   \u2022   {AppNotifications.FormatBytes(size)}";
        if (!backup.Verified) details += "   \u2022   not restorable";
        info.Children.Add(new TextBlock
        {
            Text = details,
            FontSize = 11,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
        });
        Grid.SetColumn(info, 1);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(new Button
        {
            Content = "Restore",
            FontSize = 12,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        });
        actions.Children.Add(new Button
        {
            Content = "Delete",
            FontSize = 12,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        });
        var restoreButton = (Button)actions.Children[0];
        var deleteButton = (Button)actions.Children[1];
        // Only a file the app can prove it exported is restorable. Offering the
        // button on an unrecognized file and failing at the last moment would be
        // worse than not offering it at all.
        restoreButton.IsEnabled = backup.Verified;
        if (!backup.Verified)
            ToolTipService.SetToolTip(restoreButton,
                "This file is not a restore point CleanMachine created, so it cannot be imported.");
        restoreButton.Click += async (_, _) => await RestoreBackupAsync(backup);
        deleteButton.Click += async (_, _) => await DeleteBackupAsync(backup);
        Grid.SetColumn(actions, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        var card = new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1),
            Child = grid
        };
        grid.Margin = new Thickness(14, 8, 10, 8);
        ToolTipService.SetToolTip(card, backup.FilePath);
        return card;
    }

    private async Task RestoreBackupAsync(RegistryBackup backup)
    {
        // Defence in depth: RestoreBackupAsync enforces this too, but a stale
        // card must not be able to start an import the user was never offered.
        if (!backup.Verified)
        {
            FooterText.Text = "This file is not a restore point CleanMachine created, so it cannot be restored from here.";
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Restore registry backup?",
            Content = $"'{Path.GetFileName(backup.FilePath)}' will be re-imported into the current user's registry (HKCU), restoring the values it saved from HKCU\\{backup.KeyRoot} ({backup.CreatedAt.ToLocalTime():MMM d, yyyy - h:mm tt}). Continue?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await RegistryCareService.RestoreBackupAsync(backup);
            FooterText.Text = $"Restored '{Path.GetFileName(backup.FilePath)}' into your user registry.";
            await new ActivityStore().AddAsync(new ActivityEntry(DateTimeOffset.UtcNow,
                "Registry Backup Restored", $"Restored {Path.GetFileName(backup.FilePath)} from the backups folder."));
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Restore failed: {ex.Message}";
        }
    }

    private async Task DeleteBackupAsync(RegistryBackup backup)
    {
        var confirm = new ContentDialog
        {
            Title = "Delete registry backup?",
            Content = $"'{Path.GetFileName(backup.FilePath)}' will be permanently deleted. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            RegistryCareService.DeleteBackup(backup);
            await new ActivityStore().AddAsync(new ActivityEntry(DateTimeOffset.UtcNow,
                "Registry Backup Deleted", $"Deleted {Path.GetFileName(backup.FilePath)} from the backups folder."));
            LoadBackups();
            FooterText.Text = $"Deleted '{Path.GetFileName(backup.FilePath)}'.";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Delete failed: {ex.Message}";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = RegistryCareService.BackupsDirectory;
        if (!Directory.Exists(directory))
        {
            FooterText.Text = "The backups folder doesn't exist yet. Run a registry clean to create it.";
            return;
        }
        try
        {
            // Explorer is the only way to show a plain folder here; LaunchFolderAsync
            // needs a StorageFolder and cannot open the virtualized package path.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Could not open the backups folder: {ex.Message}";
        }
    }

    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ClassesRoot = @"Software\Classes";

    /// <summary>A readable label for one backup file.
    /// <para>
    /// For a provenanced restore point this is decoded from the registry key root
    /// the app recorded when it exported the file. It used to be decoded from the
    /// file's <i>name</i>, which is the single thing about a planted file its
    /// writer fully controls: a file called
    /// <c>registry-uninstall-20991231-235959.reg</c> dropped into a backups folder
    /// was displayed as a real "Uninstall entries" restore point dated years from
    /// now, and the user was invited to import it. A file the app cannot account
    /// for now says so instead of borrowing a label it did not earn.
    /// </para></summary>
    private static string BackupScope(RegistryBackup backup)
    {
        if (!backup.Verified || string.IsNullOrWhiteSpace(backup.KeyRoot))
            return "Unrecognized file";

        var root = backup.KeyRoot;
        if (root.Equals(UninstallRoot, StringComparison.OrdinalIgnoreCase))
            return "Uninstall entries";
        if (root.StartsWith(ClassesRoot + @"\.", StringComparison.OrdinalIgnoreCase))
        {
            var extension = root[(ClassesRoot.Length + 2)..];
            return extension.Contains('\\') ? "Registry entries" : "File association: ." + extension;
        }
        var leaf = root[(root.LastIndexOf('\\') + 1)..];
        return string.IsNullOrEmpty(leaf) ? "Registry entries" : ToWords(leaf);
    }

    private static string ToWords(string value) => string.Join(' ',
        value.Split('-', StringSplitOptions.RemoveEmptyEntries)
             .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}