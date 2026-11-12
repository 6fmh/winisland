using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Winisland.Services;

public enum UpdateState { Idle, Checking, Available, UpToDate, Downloading, Verifying, Installing, Restarting, Canceled, Error, PrivateBuild }

public sealed class UpdateService : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly GitHubReleaseClient _client;
    private readonly Version _currentVersion;
    private readonly string _downloadDirectory;
    private readonly Func<UpdatePackage, Task> _install;
    private readonly bool _updatesEnabled;
    internal static bool IsPrivateBuild => typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(attribute => attribute.Key == "WinislandPrivateBuild" && bool.TryParse(attribute.Value, out var value) && value);
    private const string PrivateBuildMessage = "Public updates are disabled for this private beta build.";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _download;
    private AppRelease? _release;
    private bool _checking, _downloading, _installing, _disposed;
    private Task? _automatic;
    public string InstalledVersion => _currentVersion.ToString(3);
    public string AvailableVersion => _release?.Version.ToString(3) ?? "";
    public UpdateState State { get; private set; }
    public string Title => HasUpdate ? $"Winisland {AvailableVersion}" : State switch
    {
        UpdateState.PrivateBuild => "Private beta",
        UpdateState.UpToDate => "You're up to date",
        _ => "Software update"
    };
    public string Subtitle => HasUpdate ? $"{DownloadSize} · Installed {InstalledVersion}" : $"Winisland {InstalledVersion}";
    private string DownloadSize => _release is null ? "" : _release.Installer.Size >= 1_000_000
        ? $"{_release.Installer.Size / 1_000_000.0:0.#} MB" : $"{Math.Max(1, Math.Ceiling(_release.Installer.Size / 1000.0)):0} KB";
    public string EmptyStateDescription => State switch
    {
        UpdateState.PrivateBuild => "This build keeps your current version.",
        UpdateState.UpToDate => "You're running the latest public release.",
        UpdateState.Error => "Check for updates again when you're ready.",
        _ => "New public releases are checked automatically."
    };
    public string Notes => !_updatesEnabled ? "This private beta build stays on your current version." :
        HasUpdate && string.IsNullOrWhiteSpace(_release!.Notes) ? "No release notes were provided. You can view this release on GitHub." :
        _release?.Notes ?? "New releases are checked automatically. You can also check here anytime.";
    public bool NotesTruncated => _release?.NotesTruncated == true;
    public Uri? ReleaseUrl => _release is null ? null : new($"https://github.com/{GitHubReleaseClient.Repository}/releases/tag/{_release.Tag}");
    public string InstallButtonLabel => State switch
    {
        UpdateState.Downloading => "Downloading…",
        UpdateState.Verifying => "Verifying…",
        UpdateState.Installing => "Installing…",
        UpdateState.Restarting => "Restarting…",
        _ => "Update and restart"
    };
    public string CheckButtonLabel => State == UpdateState.Checking ? "Checking…" : HasUpdate ? "Check again" : "Check for updates";
    public bool HasUpdate => _release is not null;
    public bool IsBusy => _checking || _downloading || _installing;
    public bool IsDownloading => _downloading;
    public bool CanInstall => HasUpdate && !IsBusy && State != UpdateState.Restarting;
    public bool CanCheck => _updatesEnabled && !IsBusy && State != UpdateState.Restarting;
    public bool CanCancel => _downloading && !_installing;
    public double Progress { get; private set; }
    public string Message { get; private set; } = "Ready to check for updates.";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;
    public event EventHandler? InstallStarted;

    public UpdateService(Dispatcher dispatcher) : this(dispatcher, new GitHubReleaseClient(), GitHubReleaseClient.CurrentVersion,
        Path.Combine(SettingsStore.DirectoryPath, "updates"), UpdateInstaller.LaunchAsync, updatesEnabled: !IsPrivateBuild)
    {
        if (!_updatesEnabled) return;
        try
        {
            var path = Path.Combine(SettingsStore.DirectoryPath, "update-result.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8192) return;
            using var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (result.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (result.RootElement.TryGetProperty("success", out var success) && success.ValueKind == System.Text.Json.JsonValueKind.False
                && result.RootElement.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                Message = message.GetString() ?? "The last update couldn't finish. Try again.";
                State = UpdateState.Error;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }
    internal UpdateService(Dispatcher dispatcher, GitHubReleaseClient client, Version currentVersion, string downloadDirectory,
        Func<UpdatePackage, Task> install, bool updatesEnabled = true)
    {
        _dispatcher = dispatcher; _client = client; _currentVersion = GitHubReleaseClient.Normalize(currentVersion);
        _downloadDirectory = downloadDirectory; _install = install; _updatesEnabled = updatesEnabled;
        if (!_updatesEnabled) { Message = PrivateBuildMessage; State = UpdateState.PrivateBuild; }
    }

    public void StartAutomaticChecks()
    {
        if (_updatesEnabled && !_disposed) _automatic ??= AutomaticChecksAsync();
    }
    private async Task AutomaticChecksAsync()
    {
        try
        {
            // Startup begins this after first paint; network work stays asynchronous.
            while (!_disposed)
            {
                await CheckAsync();
                await Task.Delay(TimeSpan.FromMinutes(30), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task CheckAsync()
    {
        if (!CanCheck || _disposed || !await _operation.WaitAsync(0)) return;
        try
        {
            _checking = true; State = UpdateState.Checking; Message = "Checking for updates…"; Notify();
            var release = await _client.GetLatestAsync(_lifetime.Token);
            if (_disposed) return;
            _release = release is not null && release.Version > _currentVersion ? release : null;
            State = HasUpdate ? UpdateState.Available : UpdateState.UpToDate;
            Message = HasUpdate ? "Winisland will restart after installation." : "We'll check again automatically.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        { State = UpdateState.Error; Message = ex is InvalidOperationException ? ex.Message : "Couldn't check for updates. Check your connection and try again."; }
        finally { _checking = false; _operation.Release(); Notify(); }
    }

    public async Task InstallAsync()
    {
        if (!CanInstall || _disposed || _release is null || !await _operation.WaitAsync(0)) return;
        var directory = Path.Combine(_downloadDirectory, Guid.NewGuid().ToString("N"));
        UpdatePackage? package = null;
        var handedOff = false;
        try
        {
            _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _downloading = true; State = UpdateState.Downloading; Progress = 0; Message = "Downloading update…"; Notify();
            var progress = new Progress<double>(value =>
            {
                if (!_downloading || _disposed || _download?.IsCancellationRequested != false) return;
                Progress = value; State = value >= 100 ? UpdateState.Verifying : UpdateState.Downloading;
                Message = value >= 100 ? "Verifying download…" : "Downloading update…"; Notify();
            });
            package = await _client.DownloadAsync(_release, directory, progress, _download.Token);
            _download.Token.ThrowIfCancellationRequested();
            _downloading = false; _installing = true; State = UpdateState.Installing;
            Progress = 100; Message = "Installing update… Winisland will restart."; Notify();
            await _install(package);
            handedOff = true;
            State = UpdateState.Restarting; Message = "Restarting Winisland…";
            InstallStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        { State = UpdateState.Canceled; Message = _lifetime.IsCancellationRequested ? "" : "Download canceled. You can update later."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { State = UpdateState.Error; Message = ex is InvalidOperationException ? ex.Message : "Couldn't update Winisland. Check your connection and try again."; }
        finally
        {
            if (!handedOff && package is not null)
                try { File.Delete(package.InstallerPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _download?.Dispose(); _download = null;
            _downloading = _installing = false;
            _operation.Release(); Notify();
        }
    }

    public void CancelDownload() => _download?.Cancel();
    private void Notify()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(Notify); return; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _download?.Cancel(); _client.Dispose();
    }
}
