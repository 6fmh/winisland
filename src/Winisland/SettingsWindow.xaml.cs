using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Winisland.Services;
using Forms = System.Windows.Forms;

namespace Winisland;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly UploadCredentialStore _credentials;
    private readonly PlayerVolumeService? _playerVolume;
    private bool _loading = true;
    private bool _connectingSpotify;
    private bool _connectingSpotifyAccount;
    private string _selectedPage = "general";
    private readonly Dictionary<string, double> _pageOffsets = new();
    private readonly CancellationTokenSource _spotifySignIn = new();
    public event EventHandler? OpenNotchRequested;
    public event EventHandler? UpdatesRequested;
    private sealed record MonitorOption(string Device, string Label);

    public SettingsWindow(SettingsStore settings, UploadCredentialStore? credentials = null, PlayerVolumeService? playerVolume = null)
    {
        _settings = settings;
        _credentials = credentials ?? new UploadCredentialStore();
        _playerVolume = playerVolume;
        InitializeComponent();
        AboutVersionLabel.Text = $"Winisland {GitHubReleaseClient.CurrentVersion.ToString(3)} · Native Windows app · Open source";
        Width = Math.Min(950, SystemParameters.WorkArea.Width - 20);
        Height = Math.Min(740, SystemParameters.WorkArea.Height - 20);
        PreviewCard.SizeChanged += (_, _) => PreviewCard.Clip = new RectangleGeometry(new Rect(0, 0, PreviewCard.ActualWidth, PreviewCard.ActualHeight), 14, 14);
        Populate();
        SelectPage("general");
        RefreshUploadConnection();
        RefreshPlayerConnection();
        SpotifyClientIdInput.Text = _playerVolume?.SpotifyAccount?.ClientId ?? "";
        if (_playerVolume is not null) _playerVolume.Changed += OnPlayerConnectionChanged;
        Activated += (_, _) => RefreshPlayerConnection();
        Closed += (_, _) =>
        {
            _spotifySignIn.Cancel(); _spotifySignIn.Dispose();
            if (_playerVolume is not null) _playerVolume.Changed -= OnPlayerConnectionChanged;
        };
    }

    private void Populate()
    {
        _loading = true;
        var current = _settings.Current;
        HoverToggle.IsChecked = current.ExpandOnHover;
        FullscreenToggle.IsChecked = current.HideInFullscreen;
        StartupToggle.IsChecked = current.StartWithWindows;
        LiveToggle.IsChecked = current.ShowLiveActivity;
        MotionToggle.IsChecked = current.ReducedMotion;
        HudToggle.IsChecked = current.ShowVolumeHud;
        NotificationsToggle.IsChecked = current.ShowNotifications;
        WidthSlider.Value = current.NotchWidth;
        HeightSlider.Value = current.NotchHeight;
        MonitorCombo.Items.Clear();
        var index = 0;
        var selected = 0;
        foreach (var screen in Forms.Screen.AllScreens)
        {
            MonitorCombo.Items.Add(new MonitorOption(screen.DeviceName, $"Display {index + 1}" + (screen.Primary ? " · primary" : "")));
            if (screen.DeviceName == current.MonitorDevice || (current.MonitorDevice == "" && screen.Primary)) selected = index;
            index++;
        }
        MonitorCombo.SelectedIndex = selected;
        UpdatePreview();
        _loading = false;
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            _settings.Update(_settings.Current with
            {
                ExpandOnHover = HoverToggle.IsChecked == true,
                HideInFullscreen = FullscreenToggle.IsChecked == true,
                StartWithWindows = StartupToggle.IsChecked == true,
                ShowLiveActivity = LiveToggle.IsChecked == true,
                ReducedMotion = MotionToggle.IsChecked == true,
                ShowVolumeHud = HudToggle.IsChecked == true,
                ShowNotifications = NotificationsToggle.IsChecked == true,
                NotchWidth = WidthSlider.Value,
                NotchHeight = HeightSlider.Value,
                MonitorDevice = (MonitorCombo.SelectedItem as MonitorOption)?.Device ?? ""
            });
            SetSaveStatus("Changes saved automatically");
        }
        catch (Exception ex)
        {
            Log.Error("Save settings", ex);
            SetSaveStatus("Couldn't save changes. Please try again.", error: true);
            Populate();
        }
    }

    private void UpdatePreview()
    {
        if (WidthLabel is null || HeightLabel is null) return;
        WidthLabel.Text = $"{WidthSlider.Value:0} DIPs";
        HeightLabel.Text = $"{HeightSlider.Value:0} DIPs";
        NotchPreview.Width = WidthSlider.Value; NotchPreview.Height = HeightSlider.Value;
    }
    private void OnSettingChanged(object sender, RoutedEventArgs e) => Save();
    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { UpdatePreview(); Save(); }
    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e) => Save();
    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        try { _settings.Update(new IslandSettings { HasLaunched = true, StartWithWindows = _settings.Current.StartWithWindows }); Populate(); SetSaveStatus("Defaults restored. Startup preference kept."); }
        catch (Exception ex) { Log.Error("Restore settings", ex); SetSaveStatus("Couldn't restore settings. Please try again.", error: true); }
    }
    private void SetSaveStatus(string message, bool error = false)
    {
        SaveStatus.Text = message;
        SaveStatus.Foreground = error ? new SolidColorBrush(Color.FromRgb(185, 45, 35)) : (Brush)FindResource("Secondary");
        SaveStatus.ToolTip = message;
    }
    private void OnNavClick(object sender, RoutedEventArgs e) => SelectPage((string)((Button)sender).Tag);
    internal void SelectPage(string page)
    {
        if (page is not ("general" or "appearance" or "activities" or "players" or "uploads" or "about")) return;
        _pageOffsets[_selectedPage] = PageScroll.VerticalOffset;
        _selectedPage = page;
        GeneralPanel.Visibility = page == "general" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        ActivitiesPanel.Visibility = page == "activities" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        UploadsPanel.Visibility = page == "uploads" ? Visibility.Visible : Visibility.Collapsed;
        PlayersPanel.Visibility = page == "players" ? Visibility.Visible : Visibility.Collapsed;
        PreviewCard.Visibility = page is "general" or "appearance" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { GeneralNav, AppearanceNav, ActivitiesNav, PlayersNav, UploadsNav, AboutNav })
            button.SetValue(Selector.IsSelectedProperty, (string)button.Tag == page);
        PageTitle.Text = page switch { "appearance" => "Appearance", "activities" => "Activities", "players" => "Player controls", "uploads" => "Uploads", "about" => "About Winisland", _ => "General" };
        if (page == "players") RefreshPlayerConnection();
        PageScroll.UpdateLayout();
        PageScroll.ScrollToVerticalOffset(_pageOffsets.GetValueOrDefault(page));
    }
    private void OnPlayerConnectionChanged(object? sender, EventArgs args) => RefreshPlayerConnection();
    private void RefreshPlayerConnection()
    {
        if (BrowserConnectionStatus is null) return;
        var connected = _playerVolume?.BrowserConnected == true;
        BrowserConnectionStatus.Text = connected ? "Browser companion connected" : "Browser companion not connected";
        BrowserConnectionDot.Fill = new SolidColorBrush(connected ? Color.FromRgb(40, 180, 85) : Color.FromRgb(160, 160, 168));
        ConnectSpotifyButton.IsEnabled = _playerVolume is not null && !_connectingSpotify;
        var account = _playerVolume?.SpotifyAccount;
        ConnectSpotifyAccountButton.IsEnabled = account is not null && !_connectingSpotifyAccount;
        DisconnectSpotifyAccountButton.IsEnabled = account?.IsConnected == true && !_connectingSpotifyAccount;
        SpotifyClientIdInput.IsEnabled = !_connectingSpotifyAccount;
        if (!_connectingSpotifyAccount) SpotifyAccountStatus.Text = account?.Status ?? "Account connection unavailable in this session";
    }
    private async void OnConnectSpotify(object sender, RoutedEventArgs args)
    {
        if (_playerVolume is null || _connectingSpotify) return;
        _connectingSpotify = true;
        ConnectSpotifyButton.IsEnabled = false;
        SpotifySetupStatus.Text = "Connecting to Spotify…";
        try
        {
            var connected = await _playerVolume.PrepareSpotifyAccessibilityAsync();
            if (IsLoaded)
                SpotifySetupStatus.Text = connected
                    ? "Spotify's local controls connected. Connect your account below for 1% volume steps."
                    : "Open Spotify and try again.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (IsLoaded) SpotifySetupStatus.Text = "Couldn't connect to Spotify. Open the app and try again.";
        }
        finally
        {
            _connectingSpotify = false;
            if (IsLoaded) RefreshPlayerConnection();
        }
    }
    private async void OnConnectSpotifyAccount(object sender, RoutedEventArgs args)
    {
        var account = _playerVolume?.SpotifyAccount;
        if (account is null || _connectingSpotifyAccount) return;
        _connectingSpotifyAccount = true;
        RefreshPlayerConnection();
        SpotifyAccountStatus.Text = "Finish signing in in your browser. This window can stay open.";
        string? error = null;
        try { await account.ConnectAsync(SpotifyClientIdInput.Text, _spotifySignIn.Token); }
        catch (OperationCanceledException) { error = "Sign-in timed out or was canceled. Try signing in again."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { error = ex.Message; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { error = "Couldn't connect. Check your internet connection and Spotify app settings, then try again."; }
        finally
        {
            _connectingSpotifyAccount = false;
            if (IsLoaded)
            {
                RefreshPlayerConnection();
                if (error is not null) SpotifyAccountStatus.Text = error;
            }
        }
    }

    private void OnDisconnectSpotifyAccount(object sender, RoutedEventArgs args)
    {
        try { _playerVolume?.SpotifyAccount?.Disconnect(); RefreshPlayerConnection(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SpotifyAccountStatus.Text = "Couldn't remove the saved connection. Try restarting Winisland."; }
    }

    private void OnOpenSpotifyDashboard(object sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo("https://developer.spotify.com/dashboard") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { SpotifyAccountStatus.Text = "Open developer.spotify.com/dashboard in your browser."; }
    }
    private void OnOpenBrowserCompanion(object sender, RoutedEventArgs args)
    {
        try
        {
            BrowserIntegration.EnsureInstalled(Environment.ProcessPath!);
            if (!Directory.Exists(BrowserIntegration.ExtensionDirectory))
            {
                PlayerSetupStatus.Text = "The companion folder is missing. Rebuild or reinstall Winisland to restore it.";
                return;
            }
            Process.Start(new ProcessStartInfo(BrowserIntegration.ExtensionDirectory) { UseShellExecute = true });
            PlayerSetupStatus.Text = "In Comet, choose Load unpacked and select this folder. Then reload your media tab.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { PlayerSetupStatus.Text = "Couldn't open the companion folder. Try restarting Winisland."; }
    }
    private void OnOpenCometExtensions(object sender, RoutedEventArgs args)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Perplexity", "Comet", "Application", "comet.exe");
        if (!File.Exists(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Perplexity", "Comet", "Application", "comet.exe");
        try
        {
            if (!File.Exists(path)) { PlayerSetupStatus.Text = "In Comet, enter chrome://extensions in the address bar."; return; }
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            start.ArgumentList.Add("chrome://extensions");
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception) { PlayerSetupStatus.Text = "In Comet, enter chrome://extensions in the address bar."; }
    }
    private void RefreshUploadConnection()
    {
        DisconnectUploadButton.IsEnabled = _credentials.IsConnected;
        UploadServerInput.Text = _credentials.ServerUrl;
        UploadStatus.Text = _credentials.IsConnected ? $"Connected to {new Uri(_credentials.ServerUrl).Host} · encrypted for your Windows account." : "Choose your upload server. Files stay on your shelf until you connect.";
    }
    private void OnSaveUploadToken(object sender, RoutedEventArgs e)
    {
        try
        {
            if (UploadServerInput.Text.Trim() == _credentials.ServerUrl && UploadTokenInput.Password.Length == 0 && _credentials.IsConnected)
                _credentials.Save(_credentials.Connection);
            else _credentials.Save(UploadServerInput.Text, UploadTokenInput.Password);
            UploadTokenInput.Clear(); RefreshUploadConnection();
        }
        catch (Exception ex) { UploadStatus.Text = ex is ArgumentException ? ex.Message : "Windows couldn't save the token. Please try again."; }
    }
    private void OnImportUploadToken(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "ShareX uploader (*.sxcu)|*.sxcu", Title = "Import upload server" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 1024 * 1024) throw new ArgumentException("That uploader configuration is too large.");
            _credentials.Save(UploadCredentialStore.ImportShareX(File.ReadAllText(picker.FileName)));
            UploadTokenInput.Clear(); RefreshUploadConnection();
        }
        catch (Exception ex) { UploadStatus.Text = ex is ArgumentException ? ex.Message : "Couldn't import that file. Export a fresh ShareX uploader from your server."; }
    }
    private void OnDisconnectUpload(object sender, RoutedEventArgs e)
    {
        try { _credentials.Disconnect(); UploadTokenInput.Clear(); RefreshUploadConnection(); }
        catch (Exception) { UploadStatus.Text = "Windows couldn't remove the saved token. Please try again."; }
    }
    private void OnTitleDrag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnOpenNotchClick(object sender, RoutedEventArgs e) { OpenNotchRequested?.Invoke(this, EventArgs.Empty); Close(); }
    private void OnQuitClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    private void OnReferenceClick(object sender, RoutedEventArgs e) => OpenLink("https://github.com/TheBoredTeam/boring.notch", SaveStatus);
    private void OnUpdatesClick(object sender, RoutedEventArgs e) => UpdatesRequested?.Invoke(this, EventArgs.Empty);
    private void OnSourceClick(object sender, RoutedEventArgs e) => OpenLink("https://github.com/6fmh/winisland", SaveStatus);
    private static void OpenLink(string address, TextBlock status)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Open settings link", ex);
            status.Text = "Couldn't open your browser. Please try again.";
        }
    }
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }
}
