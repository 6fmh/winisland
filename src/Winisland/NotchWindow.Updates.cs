using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

public partial class NotchWindow
{
    private readonly UpdateService? _updates;
    private bool _showUpdateTab;
    private double _updatePanelHeight = NotchMetrics.UpdateCompactHeight;
    internal Action<Uri>? OpenUpdateLinkForTest { get; set; }
    private void InitializeUpdates()
    {
        UpdatePanel.DataContext = _updates;
        UpdateNotes.LinkRequested += (_, args) => OpenUpdateLink(args.Uri);
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
        var height = available ? NotchMetrics.UpdateExpandedHeight : NotchMetrics.UpdateCompactHeight;
        var heightChanged = _updatePanelHeight != height;
        _updatePanelHeight = height;
        UpdateStateGlyph.Kind = _updates?.State switch
        {
            UpdateState.UpToDate => NotchIconKind.CheckCircle,
            UpdateState.PrivateBuild => NotchIconKind.Pin,
            _ => NotchIconKind.Download
        };
        UpdateStateGlyph.Foreground = (Brush)FindResource(_updates?.State == UpdateState.UpToDate ? "SystemGreen"
            : _updates?.State == UpdateState.PrivateBuild ? "Muted" : "ActivityBlue");
        UpdateStatus.Foreground = (Brush)FindResource(_updates?.State == UpdateState.Error ? "UpdateError" : "Muted");
        UpdateTab.Visibility = available || _showUpdateTab ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateTab.ToolTip = available ? $"Winisland {_updates!.AvailableVersion} is available · Ctrl+7" : "Software update · Ctrl+7";
        AutomationProperties.SetName(UpdateTab, available ? $"Update to Winisland {_updates!.AvailableVersion}" : "Software update");
        if (heightChanged && _selectedTab == "updates")
        {
            UpdateExpandedLayout();
            if (_expanded) AnimateSize(ExpandedPanelWidth, ExpandedPanelHeight);
        }
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
    private void OnViewUpdateRelease(object sender, RoutedEventArgs e)
    {
        if (_updates?.ReleaseUrl is { } uri) OpenUpdateLink(uri);
    }
    private void OpenUpdateLink(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0) return;
        try
        {
            if (BackgroundVerification.IsWorker)
            {
                BackgroundVerification.RequirePrivateDesktop();
                if (OpenUpdateLinkForTest is null) throw new InvalidOperationException("Browser launches are disabled during background verification.");
                OpenUpdateLinkForTest(uri);
                return;
            }
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Open update release link", ex);
            const string message = "Couldn't open your browser. Please try again.";
            UpdateStatus.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty, message);
            UpdateStatus.SetCurrentValue(AutomationProperties.NameProperty, message);
            UpdateStatus.Foreground = (Brush)FindResource("UpdateError");
        }
    }
}
