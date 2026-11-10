using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Winisland.Services;

public sealed class UpdateService : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly GitHubReleaseClient _client;
    private readonly Version _currentVersion;
    private readonly string _downloadDirectory;
    private readonly Func<UpdatePackage, Task> _install;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _download;
    private AppRelease? _release;
    private bool _checking, _downloading, _installing, _disposed;
    private Task? _automatic;
    public string InstalledVersion => _currentVersion.ToString(3);
    public string AvailableVersion => _release?.Version.ToString(3) ?? "";
    public string Title => HasUpdate ? $"Winisland {AvailableVersion} is available" : "Software update";
    public string Notes => _release?.Notes ?? "New releases are checked automatically. You can also check here anytime.";
    public bool HasUpdate => _release is not null;
    public bool IsBusy => _checking || _downloading || _installing;
    public bool IsDownloading => _downloading;
    public bool CanInstall => HasUpdate && !IsBusy;
    public bool CanCheck => !IsBusy;
    public bool CanCancel => _downloading && !_installing;
    public double Progress { get; private set; }
    public string Message { get; private set; } = "Ready to check for updates.";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;
    public event EventHandler? InstallStarted;

    public UpdateService(Dispatcher dispatcher) : this(dispatcher, new GitHubReleaseClient(), GitHubReleaseClient.CurrentVersion,
        Path.Combine(SettingsStore.DirectoryPath, "updates"), UpdateInstaller.LaunchAsync)
    {
        try
        {
            var path = Path.Combine(SettingsStore.DirectoryPath, "update-result.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8192) return;
            using var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (result.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (result.RootElement.TryGetProperty("success", out var success) && success.ValueKind == System.Text.Json.JsonValueKind.False
                && result.RootElement.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
                Message = message.GetString() ?? "The last update couldn't finish. Try again.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }
    internal UpdateService(Dispatcher dispatcher, GitHubReleaseClient client, Version currentVersion, string downloadDirectory, Func<UpdatePackage, Task> install)
    { _dispatcher = dispatcher; _client = client; _currentVersion = GitHubReleaseClient.Normalize(currentVersion); _downloadDirectory = downloadDirectory; _install = install; }

    public void StartAutomaticChecks() => _automatic ??= AutomaticChecksAsync();
    private async Task AutomaticChecksAsync()
    {
        try
        {
            // First paint and sign-in work never wait for the network.
            await Task.Delay(TimeSpan.FromSeconds(10), _lifetime.Token);
            while (!_disposed)
            {
                await CheckAsync();
                await Task.Delay(TimeSpan.FromHours(4), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task CheckAsync()
    {
        if (_disposed || !await _operation.WaitAsync(0)) return;
        try
        {
            _checking = true; Message = "Checking for updates…"; Notify();
            var release = await _client.GetLatestAsync(_lifetime.Token);
            if (_disposed) return;
            _release = release is not null && release.Version > _currentVersion ? release : null;
            Message = HasUpdate ? "Ready to download and install." : $"You're up to date · Winisland {InstalledVersion}.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        { Message = ex is InvalidOperationException ? ex.Message : "Couldn't check for updates. Check your connection and try again."; }
        finally { _checking = false; _operation.Release(); Notify(); }
    }

    public async Task InstallAsync()
    {
        if (_disposed || _release is null || !await _operation.WaitAsync(0)) return;
        var directory = Path.Combine(_downloadDirectory, Guid.NewGuid().ToString("N"));
        UpdatePackage? package = null;
        var handedOff = false;
        try
        {
            _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _downloading = true; Progress = 0; Message = "Downloading update…"; Notify();
            var progress = new Progress<double>(value =>
            {
                if (!_downloading || _disposed || _download?.IsCancellationRequested != false) return;
                Progress = value; Message = value >= 100 ? "Verifying download…" : "Downloading update…"; Notify();
            });
            package = await _client.DownloadAsync(_release, directory, progress, _download.Token);
            _download.Token.ThrowIfCancellationRequested();
            _downloading = false; _installing = true; Progress = 100; Message = "Installing update… Winisland will restart."; Notify();
            await _install(package);
            handedOff = true;
            InstallStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        { Message = _lifetime.IsCancellationRequested ? "" : "Download canceled. You can update later."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { Message = ex is InvalidOperationException ? ex.Message : "Couldn't update Winisland. Check your connection and try again."; }
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
