using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class RegistryCarePage : Page
{
    private readonly RegistryCareService _service = new();
    private readonly List<CheckBox> _findingBoxes = new();
    private IReadOnlyList<RegistryBackup> _lastBackups = [];
    private IReadOnlyList<RegistryFinding> _findings = [];

    public RegistryCarePage()
    {
        InitializeComponent();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        RestoreButton.Visibility = Visibility.Collapsed;
        ReportPanel.Children.Clear();
        ReportHeadline.Text = "Analyzing…";
        StatusText.Text = "Scanning the registry read-only.";
        Progress.Visibility = Visibility.Visible;
        try
        {
            var review = await _service.ScanAsync();
            _findings = review.Findings;
            FindingsPanel.Children.Clear();
            _findingBoxes.Clear();

            if (_findings.Count == 0)
            {
                ReportHeadline.Text = "No issues found.";
                StatusText.Text = "The registry scan found no low-risk cleanup opportunities.";
                return;
            }

            foreach (var group in _findings.GroupBy(f => f.Category).OrderBy(g => g.Key))
                FindingsPanel.Children.Add(BuildCategoryGroup(group.Key, group.ToList()));

            var eligible = _findings.Count(RegistryCareService.IsCleanable);
            ReportHeadline.Text = $"Analysis complete — {_findings.Count} issue(s) found.";
            StatusText.Text = eligible > 0
                ? $"{eligible} item(s) can be safely cleaned. Untick anything you want to keep."
                : "Issues were found, but none are eligible for automatic cleaning.";
        }
        catch (Exception ex)
        {
            ReportHeadline.Text = "Analysis failed.";
            StatusText.Text = ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            CleanButton.IsEnabled = _findings.Count > 0;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private StackPanel BuildCategoryGroup(string category, IReadOnlyList<RegistryFinding> findings)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 2) };

        var master = new CheckBox
        {
            Content = $"{category} ({findings.Count})",
            IsChecked = findings.Any(RegistryCareService.IsCleanable),
            MinHeight = 26,
            FontWeight = global::Microsoft.UI.Text.FontWeights.SemiBold
        };
        master.Checked += (_, _) => SetGroupChecked(findings, true);
        master.Unchecked += (_, _) => SetGroupChecked(findings, false);
        panel.Children.Add(master);

        foreach (var item in findings)
        {
            var cleanable = RegistryCareService.IsCleanable(item);
            var box = new CheckBox
            {
                IsChecked = cleanable,
                IsEnabled = cleanable,
                MinHeight = 28,
                Content = new StackPanel { Spacing = 0 },
                Tag = item
            };
            var stack = (StackPanel)box.Content;
            stack.Children.Add(new TextBlock
            {
                Text = RegistryCareService.DisplayName(item),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            stack.Children.Add(new TextBlock
            {
                Text = cleanable
                    ? $"{item.Confidence}% confidence · {item.Reason}"
                    : $"{item.Confidence}% confidence · {item.Reason} (not eligible)",
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            _findingBoxes.Add(box);
            panel.Children.Add(box);
        }
        return panel;
    }

    private void SetGroupChecked(IReadOnlyList<RegistryFinding> findings, bool value)
    {
        foreach (var box in _findingBoxes)
        {
            if (box.Tag is RegistryFinding f && findings.Contains(f) && box.IsEnabled)
                box.IsChecked = value;
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _findingBoxes
            .Where(x => x.IsChecked == true)
            .Select(x => (RegistryFinding)x.Tag!)
            .ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "Nothing is ticked. Tick at least one item to clean.";
            return;
        }

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        ReportPanel.Children.Clear();
        ReportHeadline.Text = "Cleaning…";
        try
        {
            var result = await _service.PrepareReviewAsync(selected);
            _lastBackups = result.Backups;

            if (result.Backups.Count == 0)
            {
                ReportHeadline.Text = "Cleaning stopped.";
                StatusText.Text = "No backup could be created, so nothing was cleaned. The backup is required as a restore point.";
                return;
            }

            var clean = await _service.CleanAsync(result);
            var backupNote = $"{result.Backups.Count} backup file(s) saved under " +
                             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Backups");

            ReportHeadline.Text = clean.Removed == 0 && clean.Skipped.Count == 0
                ? "Nothing needed cleaning."
                : "Cleaning complete.";
            StatusText.Text = clean.Removed == 0 && clean.Skipped.Count == 0
                ? $"No registry values were changed. {backupNote}."
                : $"Cleaned {clean.Removed} registry item(s); {clean.Skipped.Count} skipped. {backupNote}.";

            foreach (var finding in result.Findings)
            {
                var issue = clean.Skipped.FirstOrDefault(s =>
                    finding.Path.Contains(s.Path, StringComparison.OrdinalIgnoreCase) ||
                    s.Path.Contains(finding.Path, StringComparison.OrdinalIgnoreCase));
                ReportPanel.Children.Add(BuildResultRow(finding, issue is not null, issue?.Reason));
            }
            RestoreButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ReportHeadline.Text = "Cleaning failed.";
            StatusText.Text = ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            CleanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private static StackPanel BuildResultRow(RegistryFinding finding, bool skipped, string? reason)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 26 };
        row.Children.Add(new FontIcon
        {
            Glyph = skipped ? "\xE711" : "\xE73E", // Cancel : CheckMark
            FontSize = 13,
            Foreground = new SolidColorBrush(skipped
                ? global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D)
                : global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58))
        });
        row.Children.Add(new TextBlock
        {
            Text = RegistryCareService.DisplayName(finding),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        if (skipped)
        {
            row.Children.Add(new TextBlock
            {
                Text = reason ?? "skipped",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D))
            });
        }
        return row;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBackups.Count == 0) { StatusText.Text = "No backup available to restore."; return; }

        var confirm = new ContentDialog
        {
            Title = "Restore registry backups?",
            Content = $"This will re-import {_lastBackups.Count} backup file(s) from {_lastBackups[0].CreatedAt:g}. Continue?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var restored = 0;
            var failures = new List<string>();
            foreach (var backup in _lastBackups)
            {
                try { await RegistryCareService.RestoreBackupAsync(backup); restored++; }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(backup.FilePath)}: {ex.Message}"); }
            }
            StatusText.Text = failures.Count == 0
                ? $"All {restored} registry backup(s) restored successfully."
                : $"Restored {restored} of {_lastBackups.Count}. Failures: {string.Join("; ", failures)}";
        }
        catch (Exception ex) { StatusText.Text = $"Restore failed: {ex.Message}"; }
    }
}
