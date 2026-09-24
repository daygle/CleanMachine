using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class RegistryCarePage : Page
{
    private readonly RegistryCareService _service = new();
    // Source of truth for selection: each finding's left-hand checkbox. The right
    // detail card mirrors these, so the left list stays authoritative.
    private readonly List<(RegistryFinding Finding, CheckBox Box)> _findingBoxes = [];
    private readonly Dictionary<string, List<RegistryFinding>> _shownByCategory = [];
    private IReadOnlyList<RegistryBackup> _lastBackups = [];
    private IReadOnlyList<RegistryFinding> _findings = [];
    private string? _detailCategory;

    public RegistryCarePage()
    {
        InitializeComponent();
        // Analyze automatically when the page is opened.
        Loaded += (_, _) => Scan_Click(this, new RoutedEventArgs());
        UpdateBackupsLink();
    }

    /// <summary>Keeps the Backups link's label showing how many restore-point
    /// .reg files currently exist, so a successful backup is visible without
    /// opening the folder.</summary>
    private void UpdateBackupsLink()
    {
        var count = RegistryCareService.CountBackups();
        BackupsLinkText.Text = count == 1 ? "Backups (1 file)" : $"Backups ({count} files)";
    }

    private void BackupsLink_Click(object sender, RoutedEventArgs e)
        => ((MainWindow)App.MainWindow!).Navigate<BackupsPage>();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        RestoreButton.Visibility = Visibility.Collapsed;
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        DetailPanel.Children.Clear();
        DetailHeadline.Text = "Analyzing...";
        DetailSubHeadline.Text = "Scanning the registry read-only.";
        StatusText.Text = "Scanning the registry read-only.";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        try
        {
            var review = await _service.ScanAsync();
            _findings = review.Findings;
            RenderFindings();
            if (_findings.Count > 0)
                DetailSubHeadline.Text = "Scan complete. Review the findings, select items, then clean.";
        }
        catch (Exception ex)
        {
            DetailHeadline.Text = "Scan failed";
            DetailSubHeadline.Text = "No cleanup was performed.";
            StatusText.Text = ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            CleanButton.IsEnabled = _findings.Count > 0;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Builds the left category list: one expander per finding category with
    /// its findings as tickable rows. By default only findings eligible for automatic
    /// cleaning are shown; Show All also lists ineligible ones, greyed out.</summary>
    private void RenderFindings()
    {
        FindingsPanel.Children.Clear();
        _findingBoxes.Clear();
        _shownByCategory.Clear();
        _detailCategory = null;
        DetailBackButton.Visibility = Visibility.Collapsed;

        if (_findings.Count == 0)
        {
            DetailHeadline.Text = "No issues found";
            DetailSubHeadline.Text = "The registry scan found no low-risk cleanup opportunities.";
            DetailGroupBadge.Visibility = Visibility.Collapsed;
            SetChips("ELIGIBLE", "0", "SELECTED", "0", "TOTAL", "0");
            StatusText.Text = "The registry scan found no low-risk cleanup opportunities.";
            return;
        }

        var eligibleTotal = _findings.Count(RegistryCareService.IsCleanable);
        var ineligibleTotal = _findings.Count - eligibleTotal;

        // Show All only reveals findings that are NOT eligible for automatic cleaning.
        // When every finding is already eligible there is nothing extra to show, so
        // disable the toggle (and explain why) instead of leaving it looking broken.
        ShowAllCheck.IsEnabled = ineligibleTotal > 0;
        ToolTipService.SetToolTip(ShowAllCheck, ineligibleTotal > 0
            ? $"Also list {ineligibleTotal} finding(s) not eligible for automatic cleaning"
            : "All findings are eligible for cleaning - nothing extra to show");
        var showAll = ShowAllCheck.IsChecked == true && ineligibleTotal > 0;

        foreach (var group in _findings.GroupBy(f => f.Category).OrderBy(g => g.Key))
        {
            var items = (showAll ? group : group.Where(RegistryCareService.IsCleanable)).ToList();
            if (items.Count == 0) continue;
            _shownByCategory[group.Key] = items;
            FindingsPanel.Children.Add(BuildCategoryExpander(group.Key, items));
        }

        StatusText.Text = eligibleTotal == 0
            ? "Issues were found, but none are eligible for automatic cleaning. Tick Show All to review them."
            : ineligibleTotal == 0
                ? $"{eligibleTotal} item(s) can be safely cleaned. Untick anything you want to keep. (All findings are eligible, so Show All has nothing extra to reveal.)"
                : showAll
                    ? $"{eligibleTotal} item(s) can be safely cleaned; {ineligibleTotal} ineligible finding(s) are shown greyed out. Untick anything you want to keep."
                    : $"{eligibleTotal} item(s) can be safely cleaned. Untick anything you want to keep, or tick Show All to review {ineligibleTotal} ineligible finding(s).";

        // Show the first category's detail straight away so the right side is never blank.
        var firstCategory = _shownByCategory.Keys.FirstOrDefault();
        if (firstCategory is not null)
            ShowCategoryDetail(firstCategory);
        else
        {
            DetailHeadline.Text = "Analysis complete";
            DetailSubHeadline.Text = "Tick Show All to review ineligible findings.";
            SetChips("ELIGIBLE", "0", "SELECTED", "0", "TOTAL", "0");
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_findings.Count > 0) RenderFindings();
    }

    /// <summary>Ticks or clears every cleanable finding currently shown; findings that
    /// aren't eligible for cleaning are disabled and left untouched.</summary>
    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var value = SelectAllCheck.IsChecked == true;
        foreach (var (_, box) in _findingBoxes)
            if (box.IsEnabled)
                box.IsChecked = value;
    }

    private Expander BuildCategoryExpander(string category, IReadOnlyList<RegistryFinding> findings)
    {
        var eligible = findings.Count(RegistryCareService.IsCleanable);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58))
        });
        header.Children.Add(new TextBlock
        {
            Text = category,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{eligible}/{findings.Count}",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69))
        });

        var expander = new Expander
        {
            Header = header,
            IsExpanded = eligible > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var content = new StackPanel { Spacing = 2 };
        foreach (var finding in findings)
        {
            var cleanable = RegistryCareService.IsCleanable(finding);
            var box = new CheckBox
            {
                IsChecked = cleanable,
                IsEnabled = cleanable,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            _findingBoxes.Add((finding, box));

            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(new TextBlock
            {
                Text = RegistryCareService.DisplayName(finding),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            text.Children.Add(new TextBlock
            {
                Text = cleanable
                    ? $"{finding.Confidence}% confidence"
                    : $"{finding.Confidence}% confidence (not eligible)",
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });

            var detailsButton = new Button
            {
                Content = text,
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            detailsButton.Click += (_, _) => ShowFindingDetail(category, finding);
            ToolTipService.SetToolTip(detailsButton, $"{finding.Hive}\\{finding.Path}");

            var row = new Grid { MinHeight = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(box, 0);
            Grid.SetColumn(detailsButton, 1);
            row.Children.Add(box);
            row.Children.Add(detailsButton);
            content.Children.Add(row);
        }

        expander.Content = content;
        expander.Expanding += (_, _) => ShowCategoryDetail(category);
        return expander;
    }

    /// <summary>Category-level detail: summary chips plus one framed row per finding,
    /// each mirroring the left checkbox so the left list stays authoritative.</summary>
    private void ShowCategoryDetail(string category)
    {
        if (!_shownByCategory.TryGetValue(category, out var findings)) return;
        _detailCategory = category;
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Visible;
        DetailGroupBadgeText.Text = "REGISTRY";
        DetailHeadline.Text = category;
        DetailSubHeadline.Text = "Click a finding to see its registry location.";

        var eligible = findings.Count(RegistryCareService.IsCleanable);
        var selected = _findingBoxes.Count(x => findings.Contains(x.Finding) && x.Box.IsChecked == true);
        SetChips("ELIGIBLE", eligible.ToString(), "SELECTED", selected.ToString(), "TOTAL", findings.Count.ToString());

        DetailPanel.Children.Clear();
        foreach (var finding in findings)
        {
            var cleanable = RegistryCareService.IsCleanable(finding);
            var sourceBox = _findingBoxes.FirstOrDefault(x => ReferenceEquals(x.Finding, finding)).Box;

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var mirror = new CheckBox
            {
                IsChecked = sourceBox?.IsChecked == true,
                IsEnabled = cleanable,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            mirror.Checked += (_, _) => { if (sourceBox is not null) sourceBox.IsChecked = true; };
            mirror.Unchecked += (_, _) => { if (sourceBox is not null) sourceBox.IsChecked = false; };
            Grid.SetColumn(mirror, 0);
            row.Children.Add(mirror);

            var body = new Button
            {
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(8)
            };
            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(new TextBlock
            {
                Text = RegistryCareService.DisplayName(finding),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            label.Children.Add(new TextBlock
            {
                Text = cleanable ? finding.Reason : $"{finding.Reason} (not eligible)",
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            body.Content = label;
            body.Click += (_, _) => ShowFindingDetail(category, finding);
            ToolTipService.SetToolTip(body, $"{finding.Hive}\\{finding.Path}");
            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            DetailPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Child = row
            });
        }
    }

    /// <summary>Finding-level detail: the registry location and why it was flagged.</summary>
    private void ShowFindingDetail(string category, RegistryFinding finding)
    {
        _detailCategory = category;
        DetailBackButton.Visibility = Visibility.Visible;
        DetailGroupBadge.Visibility = Visibility.Visible;
        DetailGroupBadgeText.Text = "REGISTRY";
        DetailHeadline.Text = RegistryCareService.DisplayName(finding);
        DetailSubHeadline.Text = category;

        var cleanable = RegistryCareService.IsCleanable(finding);
        SetChips("CONFIDENCE", $"{finding.Confidence}%", "STATUS", cleanable ? "Eligible" : "Not eligible", "HIVE", finding.Hive);

        DetailPanel.Children.Clear();
        DetailPanel.Children.Add(DetailRow("Key", $"{finding.Hive}\\{finding.Path}"));
        if (!string.IsNullOrEmpty(finding.ValueName))
            DetailPanel.Children.Add(DetailRow("Value", finding.ValueName));
        DetailPanel.Children.Add(DetailRow("Reason", finding.Reason));
        DetailPanel.Children.Add(DetailRow("Eligibility", cleanable
            ? "Eligible for automatic cleaning (low risk, high confidence, backed up first)."
            : "Not eligible for automatic cleaning - shown for review only."));
    }

    private static Border DetailRow(string label, string value) => new()
    {
        Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
        BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 10, 6),
        Child = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = label.ToUpperInvariant(), FontSize = 9, CharacterSpacing = 40,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89)) },
                new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)) }
            }
        }
    };

    private void SetChips(string l1, string v1, string l2, string v2, string l3, string v3)
    {
        ChipLabel1.Text = l1; ChipValue1.Text = v1;
        ChipLabel2.Text = l2; ChipValue2.Text = v2;
        ChipLabel3.Text = l3; ChipValue3.Text = v3;
    }

    private void DetailBack_Click(object sender, RoutedEventArgs e)
    {
        if (_detailCategory is { } category) ShowCategoryDetail(category);
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _findingBoxes
            .Where(x => x.Box.IsChecked == true)
            .Select(x => x.Finding)
            .ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "Nothing is ticked. Tick at least one item to clean.";
            return;
        }

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        DetailPanel.Children.Clear();
        DetailHeadline.Text = "Cleaning...";
        DetailSubHeadline.Text = "Step 1 of 2: Creating a verified registry backup.";
        StatusText.Text = "Cleaning in progress: creating a verified backup before making changes...";
        try
        {
            var result = await _service.PrepareReviewAsync(selected);
            _lastBackups = result.Backups;

            if (result.Backups.Count == 0)
            {
                DetailHeadline.Text = "Cleaning stopped";
                DetailSubHeadline.Text = "";
                StatusText.Text = string.IsNullOrWhiteSpace(result.BackupFailure)
                    ? "No backup could be created, so nothing was cleaned. The backup is required as a restore point."
                    : $"No backup could be created, so nothing was cleaned. {result.BackupFailure}";
                return;
            }

            DetailSubHeadline.Text = "Step 2 of 2: Removing the selected findings.";
            StatusText.Text = "Cleaning in progress: removing selected registry findings...";
            var progress = new Progress<CleanupProgress>(p =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
                StatusText.Text = $"Cleaning in progress: {p.Phase} ({p.Completed:N0}/{p.Total:N0})";
            });
            var clean = await _service.CleanAsync(result, progress: progress);
            await RecordManualCleanupAsync(result, clean);
            var backupNote = $"{result.Backups.Count} backup file(s) saved under " +
                             RegistryCareService.BackupsDirectory;
            UpdateBackupsLink();

            DetailHeadline.Text = clean.Removed == 0 && clean.Skipped.Count == 0
                ? "Nothing needed cleaning"
                : "Cleaning complete";
            DetailSubHeadline.Text = backupNote + ".";
            SetChips("REMOVED", clean.Removed.ToString(), "SKIPPED", clean.Skipped.Count.ToString(), "BACKUPS", result.Backups.Count.ToString());
            StatusText.Text = clean.Removed == 0 && clean.Skipped.Count == 0
                ? $"No registry values were changed. {backupNote}."
                : $"Cleaned {clean.Removed} registry item(s); {clean.Skipped.Count} skipped. {backupNote}.";

            RestoreButton.Visibility = Visibility.Visible;

            // Re-scan so the left list reflects what was cleaned, then restore the
            // completion report because RenderFindings repopulates the detail card.
            try
            {
                var review = await _service.ScanAsync();
                _findings = review.Findings;
                RenderFindings();
            }
            catch { /* refresh is best-effort; the completion report still stands */ }

            DetailBackButton.Visibility = Visibility.Collapsed;
            DetailGroupBadge.Visibility = Visibility.Collapsed;
            DetailPanel.Children.Clear();
            foreach (var finding in clean.Cleaned ?? [])
                DetailPanel.Children.Add(BuildResultRow(finding, skipped: false, reason: null));

            if ((clean.Cleaned?.Count ?? 0) == 0)
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = "No registry findings were cleaned. Skipped findings are not shown.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                    Margin = new Thickness(0, 8, 0, 0)
                });

            DetailHeadline.Text = clean.Removed == 0 && clean.Skipped.Count == 0
                ? "Cleaning complete - nothing changed"
                : "Cleaning complete";
            DetailSubHeadline.Text = $"{clean.Removed:N0} removed, {clean.Skipped.Count:N0} skipped. {backupNote}.";
            SetChips("REMOVED", clean.Removed.ToString(), "SKIPPED", clean.Skipped.Count.ToString(), "BACKUPS", result.Backups.Count.ToString());
            StatusText.Text = clean.Removed == 0 && clean.Skipped.Count == 0
                ? $"Complete: no registry values were changed. {backupNote}."
                : $"Complete: cleaned {clean.Removed:N0} registry item(s); {clean.Skipped.Count:N0} skipped. {backupNote}.";
        }
        catch (Exception ex)
        {
            DetailHeadline.Text = "Cleaning failed";
            DetailSubHeadline.Text = "No further cleanup is running.";
            StatusText.Text = ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            CleanButton.IsEnabled = _findings.Count > 0;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task RecordManualCleanupAsync(RegistryReview review, RegistryCleanResult result)
    {
        await new CleanupStatsStore().RecordAsync(result.Removed, 0);
        try
        {
            var byCategory = (result.Cleaned ?? [])
                .GroupBy(f => f.Category)
                .Select(g => new CleanupCategoryResult(g.Key, g.Count(), 0))
                .ToArray();
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                "Registry Care",
                $"Manual clean - removed {result.Removed:N0} registry item(s), {result.Skipped.Count:N0} skipped.",
                ActivityStore.BreakdownLines(byCategory)));
        }
        catch
        {
            // Activity history is diagnostic; cleanup completion must not be
            // reported as failed when the history file is unavailable.
        }
    }

    private static Border BuildResultRow(RegistryFinding finding, bool skipped, string? reason)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 26 };
        row.Children.Add(new FontIcon
        {
            Glyph = skipped ? "\xE711" : "\xE73E", // Cancel : CheckMark
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
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
        return new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Child = row
        };
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBackups.Count == 0) { StatusText.Text = "No backup available to restore."; return; }

        var confirm = new ContentDialog
        {
            Title = "Restore registry backups?",
            Content = $"This will re-import {_lastBackups.Count} backup file(s) from {_lastBackups[0].CreatedAt.ToLocalTime():g}. Continue?",
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
