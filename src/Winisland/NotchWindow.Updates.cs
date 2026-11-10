using System;
using System.Windows;
using System.Windows.Automation;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private readonly UpdateService? _updates;
    private bool _showUpdateTab;
    private void InitializeUpdates()
    {
        UpdatePanel.DataContext = _updates;
        if (_updates is not null) _updates.Changed += OnUpdateChanged;
        RefreshUpdates();
    }
    private void OnUpdateChanged(object? sender, EventArgs args)
    {
        RefreshUpdates(); UpdateCameraSpace(); UpdateCompact();
    }
    private void RefreshUpdates()
    {
        var available = _updates?.HasUpdate == true;
        UpdateTab.Visibility = available || _showUpdateTab ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateTab.ToolTip = available ? $"Winisland {_updates!.AvailableVersion} is available · Ctrl+7" : "Software update · Ctrl+7";
        AutomationProperties.SetName(UpdateTab, available ? $"Update to Winisland {_updates!.AvailableVersion}" : "Software update");
        if (!_expanded && available)
        {
            AutomationProperties.SetName(CompactActivity, "Winisland update available. Open the Software update tab to install.");
            AutomationProperties.SetLiveSetting(CompactActivity, AutomationLiveSetting.Polite);
        }
        else if (!available)
        {
            AutomationProperties.SetName(CompactActivity, "");
            AutomationProperties.SetLiveSetting(CompactActivity, AutomationLiveSetting.Off);
        }
    }
    public void OpenUpdates()
    {
        if (_updates is null) return;
        _showUpdateTab = true; RefreshUpdates(); UpdateCameraSpace();
        SetTab("updates"); Expand(true);
        if (!_updates.HasUpdate) _ = _updates.CheckAsync();
    }
    private void OnUpdateTab(object sender, RoutedEventArgs e) => SetTab("updates");
    private async void OnCheckUpdates(object sender, RoutedEventArgs e) { if (_updates is not null) await _updates.CheckAsync(); }
    private async void OnInstallUpdate(object sender, RoutedEventArgs e) { if (_updates is not null) await _updates.InstallAsync(); }
    private void OnCancelUpdate(object sender, RoutedEventArgs e) => _updates?.CancelDownload();
}
