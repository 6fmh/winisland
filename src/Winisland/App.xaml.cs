using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Winisland.Services;
using Forms = System.Windows.Forms;

namespace Winisland;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    private EventWaitHandle? _settingsSignal;
    private RegisteredWaitHandle? _settingsRegistration;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private SettingsStore? _settings;
    private MediaService? _media;
    private AudioService? _audio;
    private PlayerVolumeService? _playerVolume;
    private ShelfUploadService? _uploads;
    private FocusTimer? _focus;
    private UpdateService? _updates;
    private bool _exiting;
    private NotchWindow? _notch;
    private SettingsWindow? _settingsWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (BrowserNativeHost.IsHostInvocation(e.Args))
        {
            Shutdown(await BrowserNativeHost.RunAsync(e.Args)); return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--unregister-browser-host")
        {
            try { BrowserIntegration.Unregister(Environment.ProcessPath!); Shutdown(0); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
            { Log.Error("Unregister browser companion", ex); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--audio-test-host")
        {
            Shutdown(await AudioVerification.RunHostAsync(e.Args[1])); return;
        }
        if (BackgroundVerification.IsInvocation(e.Args))
        {
            DispatcherUnhandledException += (_, args) =>
            {
                BackgroundVerification.WriteFailure(e.Args, args.Exception);
                args.Handled = true;
                Shutdown(1);
            };
        }
        if (e.Args.Contains("--control-center-test"))
        {
            Shutdown(await ControlCenterVerification.RunStandaloneAsync(this, e.Args)); return;
        }
        if (e.Args.Contains("--startup-test"))
        {
            Shutdown(await StartupVerification.RunAsync(this, e.Args)); return;
        }
        if (e.Args.Contains("--self-test"))
        {
            var code = await SmokeTests.RunAsync(this, e.Args);
            Shutdown(code); return;
        }
        DispatcherUnhandledException += (_, args) => { Log.Error("Unhandled UI error", args.Exception); args.Handled = true; ShowNotification("Winisland encountered a problem. Details are saved in the local log."); };
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Winisland.OpenSettings");
        _instance = new Mutex(true, @"Local\Winisland.Desktop", out _ownsMutex);
        if (!_ownsMutex)
        {
            if (!e.Args.Contains("--background")) _settingsSignal.Set();
            Shutdown(); return;
        }
        _settings = new SettingsStore();
        _media = new MediaService(Dispatcher);
        _focus = new FocusTimer();
        _audio = new AudioService();
        _playerVolume = new PlayerVolumeService(Dispatcher, deferInitialization: true);
        var shelf = new FileShelf();
        _uploads = new ShelfUploadService(shelf, new UploadCredentialStore());
        _updates = new UpdateService(Dispatcher);
        _updates.InstallStarted += (_, _) => Shutdown();
        _notch = new NotchWindow(_settings, _media, _audio, _focus, shelf, _uploads, _playerVolume, _updates);
        _notch.SettingsRequested += (_, _) => OpenSettings();
        _notch.UploadSettingsRequested += (_, _) => { OpenSettings(); _settingsWindow?.SelectPage("uploads"); };
        _notch.PlayerSettingsRequested += (_, _) => { OpenSettings(); _settingsWindow?.SelectPage("players"); };
        _notch.Notification += (_, message) => ShowNotification(message);
        _notch.ContentRendered += OnFirstNotchRendered;
        MainWindow = _notch;
        _notch.Show();
        _settingsRegistration = ThreadPool.RegisterWaitForSingleObject(_settingsSignal,
            (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, false);
    }

    private async void OnFirstNotchRendered(object? sender, EventArgs e)
    {
        if (_notch is null || _settings is null || _media is null) return;
        _notch.ContentRendered -= OnFirstNotchRendered;
        _updates?.StartAutomaticChecks();
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        if (_exiting) return;
        CreateTray();
        var firstLaunch = !_settings.Current.HasLaunched;
        if (firstLaunch)
        {
            try { _settings.Update(_settings.Current with { HasLaunched = true }); }
            catch (Exception ex) { Log.Error("Save first launch", ex); }
        }
        var args = Environment.GetCommandLineArgs();
        if ((firstLaunch && !args.Contains("--background")) || args.Contains("--settings")) OpenSettings();
        var mediaStartup = _media.InitializeAsync();
        await Task.WhenAll(mediaStartup, Task.Run(() =>
        {
            try { BrowserIntegration.EnsureInstalled(Environment.ProcessPath!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Log.Error("Set up browser companion", ex); }
            if (_settings.Current.StartWithWindows)
                try { StartupRegistration.EnsureEnabled(); }
                catch (Exception ex) { Log.Error("Refresh sign-in task", ex); }
        }));
    }

    private void CreateTray()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Open notch       Ctrl+Alt+N", null, (_, _) => _notch?.Expand(true));
        _trayMenu.Items.Add("Settings             Ctrl+Alt+S", null, (_, _) => OpenSettings());
        _trayMenu.Items.Add("Check for updates", null, (_, _) => _notch?.OpenUpdates());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Quit Winisland", null, (_, _) => Shutdown());
        _tray = new Forms.NotifyIcon
        {
            Text = "Winisland · your little desktop island",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private void OpenSettings()
    {
        if (_settings is null) return;
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.WindowState = WindowState.Normal; _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, _uploads?.Credentials, _playerVolume);
        _settingsWindow.OpenNotchRequested += (_, _) => _notch?.Expand(true);
        _settingsWindow.UpdatesRequested += (_, _) => { _settingsWindow?.Close(); _notch?.OpenUpdates(); };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }
    private void ShowNotification(string message)
    {
        if (_tray is not null) _tray.ShowBalloonTip(4000, "Winisland", message, Forms.ToolTipIcon.Info);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _updates?.Dispose();
        _settingsWindow?.Close(); _notch?.CloseForShutdown(); _uploads?.Dispose(); _media?.Dispose(); _audio?.Dispose(); _playerVolume?.Dispose(); _focus?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.Dispose(); }
        _trayMenu?.Dispose();
        _settingsRegistration?.Unregister(null);
        _settingsSignal?.Dispose();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
