using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class UpdateVerification
{
    private const string VersionText = "1.2.3";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("MZ" + new string('p', 160000));
    private static string Hash => Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private static string InstallerName(string version, string runtime) => $"Winisland-Setup-{version}-{runtime}.exe";
    private static byte[] Checksum(string version, string runtime) => Encoding.UTF8.GetBytes($"{Hash}  {InstallerName(version, runtime)}\n");
    private static string ReleaseJson(string version = VersionText, string? runtime = null)
    {
        runtime ??= GitHubReleaseClient.Runtime;
        var name = InstallerName(version, runtime);
        object Asset(string filename, byte[] bytes) => new { name = filename,
            browser_download_url = $"https://github.com/6fmh/winisland/releases/download/v{version}/{filename}", size = bytes.Length,
            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        return JsonSerializer.Serialize(new { tag_name = "v" + version, draft = false, prerelease = false,
            body = "A little better, every release.\n\nFaster startup and improvements to your notch.",
            assets = new[] { Asset(name, Payload), Asset(name + ".sha256", Checksum(version, runtime)) } });
    }
    private sealed class Fixture : HttpMessageHandler
    {
        internal string Metadata = ReleaseJson();
        internal byte[] Bytes = Payload;
        internal bool Pause, Missing;
        internal readonly List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri == GitHubReleaseClient.LatestReleaseUrl)
                return Task.FromResult(new HttpResponseMessage(Missing ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                    { Content = new StringContent(Metadata, Encoding.UTF8, "application/json") });
            if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Checksum(VersionText, GitHubReleaseClient.Runtime)) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = Pause ? new StreamContent(new PausingStream(Bytes)) : new ByteArrayContent(Bytes) });
        }
    }
    private sealed class PausingStream : MemoryStream
    {
        private bool _read;
        internal PausingStream(byte[] bytes) : base(bytes) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        {
            if (_read) await Task.Delay(Timeout.Infinite, cancellation);
            _read = true;
            return await base.ReadAsync(buffer, cancellation);
        }
    }

    internal static async Task RunAsync(App app, string output, Action<bool, string> check)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var fixtureDirectory = Path.Combine(output, "update-fixture");
        Directory.CreateDirectory(fixtureDirectory);
        using (var client = new GitHubReleaseClient(new HttpClient(new Fixture())))
        {
            var release = await client.GetLatestAsync(CancellationToken.None);
            check(release?.Version == new Version(1, 2, 3) && release.Installer.Name == InstallerName(VersionText, GitHubReleaseClient.Runtime),
                "GitHub updater selects the exact stable installer for this PC's architecture");
        }
        foreach (var invalid in new[] { ReleaseJson().Replace("github.com/6fmh/", "github.com/someone-else/"),
            ReleaseJson().Replace("v1.2.3\"", "v1.2.3-beta\""), ReleaseJson().Replace("sha256:", "md5:"), "[]" })
        {
            var refused = false;
            try { using var document = JsonDocument.Parse(invalid); GitHubReleaseClient.Parse(document.RootElement, GitHubReleaseClient.Runtime); }
            catch (InvalidOperationException) { refused = true; }
            check(refused, "Updater rejects foreign assets, unsupported tags and invalid release details");
        }
        using (var document = JsonDocument.Parse(ReleaseJson(runtime: "win-arm64")))
            check(GitHubReleaseClient.Parse(document.RootElement, "win-arm64")!.Installer.Name.EndsWith("-win-arm64.exe"), "ARM64 update selection uses its own installer");
        foreach (var metadata in new[] { ReleaseJson("0.2.0"), ReleaseJson().Replace("\"draft\":false", "\"draft\":true"),
            ReleaseJson().Replace("\"prerelease\":false", "\"prerelease\":true") })
        {
            using var service = new UpdateService(app.Dispatcher, new GitHubReleaseClient(new HttpClient(new Fixture { Metadata = metadata })), new Version(0, 4, 0), fixtureDirectory,
                _ => throw new InvalidOperationException("No update should install"));
            await service.CheckAsync();
            check(!service.HasUpdate && !service.CanInstall, "Older, draft and prerelease builds never offer a downgrade or install");
        }
        using (var service = new UpdateService(app.Dispatcher, new GitHubReleaseClient(new HttpClient(new Fixture { Missing = true })), new Version(0, 4, 0), fixtureDirectory,
            _ => throw new InvalidOperationException("Missing release should not install")))
        {
            await service.CheckAsync();
            check(!service.IsBusy && !service.HasUpdate && service.Message.Contains("up to date"), "A repository without a published release completes its check without an error");
        }
        var tampered = Payload.ToArray(); tampered[^1] ^= 1;
        var tamperFixture = new Fixture { Bytes = tampered };
        var installs = 0;
        using (var service = new UpdateService(app.Dispatcher, new GitHubReleaseClient(new HttpClient(tamperFixture)), new Version(0, 4, 0), fixtureDirectory,
            _ => { installs++; return Task.CompletedTask; }))
        {
            await service.CheckAsync(); await service.InstallAsync();
            check(installs == 0 && service.HasUpdate && service.CanInstall && service.Message.Contains("failed verification")
                && !Directory.EnumerateFiles(fixtureDirectory, "*.partial", SearchOption.AllDirectories).Any(),
                "A tampered installer is never launched, partial bytes are removed, and the update can be retried");
            tamperFixture.Bytes = Payload;
            await service.InstallAsync();
            check(installs == 1, "A successful retry installs only after the entire download passes checksum verification");
        }

        var uiFixture = new Fixture { Pause = true };
        UpdatePackage? handedOff = null;
        var uiInstalls = 0;
        using var updates = new UpdateService(app.Dispatcher, new GitHubReleaseClient(new HttpClient(uiFixture)), new Version(0, 4, 0), fixtureDirectory,
            package => { handedOff = package; uiInstalls++; return Task.CompletedTask; });
        var settings = new SettingsStore();
        var saved = settings.Current;
        settings.Update(saved with { ReducedMotion = true, HideInFullscreen = false, ShowLiveActivity = false });
        using var media = new MediaService(app.Dispatcher);
        using var audio = new AudioService();
        using var focus = new FocusTimer();
        using var volume = new PlayerVolumeService(app.Dispatcher, Array.Empty<IPlayerVolumeBackend>());
        var notch = new NotchWindow(settings, media, audio, focus, new FileShelf(), playerVolume: volume, updates: updates);
        try
        {
            notch.Show(); notch.StopPollingForTest();
            await updates.CheckAsync(); await Task.Delay(80); notch.UpdateLayout();
            var tab = (RadioButton)notch.FindName("UpdateTab");
            check(tab.Visibility == Visibility.Visible && ((TextBlock)notch.FindName("CompactLabel")).Text == "Update"
                && ((FrameworkElement)notch.FindName("CompactActivity")).IsVisible && !notch.IsActive,
                "An available release appears on the closed notch without activation even when regular live activities are off");
            Capture(notch, Path.Combine(output, "notch-update-available.png"));
            notch.OpenUpdates(); notch.UpdateLayout();
            var panel = (FrameworkElement)notch.FindName("UpdatePanel");
            var install = (Button)notch.FindName("InstallUpdateButton");
            var cancel = (Button)notch.FindName("CancelUpdateButton");
            check(panel.IsVisible && tab.IsChecked == true && install.IsEnabled && AutomationProperties.GetName(tab).Contains(VersionText),
                "Opening the update tab shows the available version and enabled Update and restart action");
            foreach (var width in new[] { 160.0, 190.0, 320.0 })
            {
                settings.Update(settings.Current with { NotchWidth = width }); notch.UpdateLayout();
                var camera = (FrameworkElement)notch.FindName("HeaderCameraSpace");
                var origin = tab.TranslatePoint(new Point(), camera);
                check(origin.X + tab.ActualWidth <= .5, "The extra update tab stays outside the black camera space at every supported notch width");
            }
            settings.Update(settings.Current with { NotchWidth = 190 }); notch.UpdateLayout();
            Capture(notch, Path.Combine(output, "notch-update-tab.png"));
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => updates.Progress > 0);
            check(updates.IsDownloading && !install.IsEnabled && cancel.IsVisible && ((ProgressBar)notch.FindName("UpdateProgress")).Value > 0,
                "The actual update button begins downloading, disables duplicate installs and displays byte progress with Cancel");
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(uiFixture.Requests.Count(url => url.EndsWith(".exe", StringComparison.Ordinal)) == 1, "Repeated update clicks cannot start a second installer download");
            Capture(notch, Path.Combine(output, "notch-update-downloading.png"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => !updates.IsBusy);
            check(uiInstalls == 0 && updates.CanInstall && updates.Message.Contains("canceled"), "Cancel stops the real update-button operation and leaves the release available for later");
            uiFixture.Pause = false;
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => uiInstalls == 1 && !updates.IsBusy);
            check(handedOff is not null && handedOff.Sha256 == Hash && File.ReadAllBytes(handedOff.InstallerPath).SequenceEqual(Payload),
                "The actual update-button retry hands off the verified installer and expected release version");
        }
        finally { notch.CloseForShutdown(); settings.Update(saved); }

        var cache = Path.Combine(SettingsStore.DirectoryPath, "updates", Guid.NewGuid().ToString("N"));
        var application = Environment.ProcessPath!;
        var plan = new UpdatePlan(new UpdatePackage(Path.Combine(cache, InstallerName(VersionText, GitHubReleaseClient.Runtime)), Hash, VersionText),
            Environment.ProcessId, DateTime.UtcNow.Ticks, application, Path.GetDirectoryName(application)!);
        UpdateInstaller.ValidatePlan(plan, cache);
        var start = UpdateInstaller.InstallerStart(plan, Path.Combine(cache, "installer.log"));
        var restart = UpdateInstaller.RestartStart(application);
        check(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden
            && start.ArgumentList.Contains("/VERYSILENT") && start.ArgumentList.Contains("/SUPPRESSMSGBOXES") && start.ArgumentList.Contains("/NORESTART")
            && start.ArgumentList.Contains("/UPDATE=1") && start.ArgumentList.Contains("/DIR=" + plan.InstallDirectory)
            && !restart.UseShellExecute && restart.CreateNoWindow && restart.ArgumentList.Single() == "--background",
            "Verified installer handoff upgrades the current directory silently, preserves preferences and restarts in the background");
        var escaped = false;
        try { UpdateInstaller.ValidatePlan(plan with { Package = plan.Package with { InstallerPath = Path.Combine(output, InstallerName(VersionText, GitHubReleaseClient.Runtime)) } }, cache); }
        catch (InvalidOperationException) { escaped = true; }
        check(escaped, "An updater plan cannot launch an installer outside its own staged cache directory");
        var forbidden = false;
        try { await UpdateInstaller.LaunchAsync(plan.Package); } catch (InvalidOperationException) { forbidden = true; }
        check(forbidden, "Background verification cannot launch a real installer or update the user's running app");

        var xml = XDocument.Parse(StartupRegistration.BuildTaskXml(@"C:\A & B\Winisland.exe", "S-1-5-21-123"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => xml.Descendants(ns + name).Single().Value;
        check(Value("Delay") == "PT0S" && Value("LogonType") == "InteractiveToken" && Value("RunLevel") == "LeastPrivilege"
            && Value("RunOnlyIfNetworkAvailable") == "false" && Value("DisallowStartIfOnBatteries") == "false"
            && Value("Command") == @"C:\A & B\Winisland.exe" && Value("Arguments") == "--background",
            "Sign-in task has no delay, network or battery gate, requests no elevation and preserves paths containing spaces and XML characters");

        var expectedVersion = Environment.GetEnvironmentVariable("WINISLAND_RELEASE_TEST_VERSION");
        var outcomePath = Path.Combine(SettingsStore.DirectoryPath, "update-result.json");
        var oldOutcome = File.Exists(outcomePath) ? File.ReadAllBytes(outcomePath) : null;
        try
        {
            File.WriteAllText(outcomePath, "[]");
            using (var service = new UpdateService(app.Dispatcher))
                check(!service.IsBusy && service.Message.Contains("Ready"), "A corrupt update-outcome cache never blocks app startup");
            File.WriteAllText(outcomePath, JsonSerializer.Serialize(new { success = false, message = "Setup could not finish. Try again." }));
            using (var service = new UpdateService(app.Dispatcher))
                check(service.Message == "Setup could not finish. Try again.", "A failed helper installation remains visible as inline retry guidance on the next launch");
        }
        finally { if (oldOutcome is null) File.Delete(outcomePath); else File.WriteAllBytes(outcomePath, oldOutcome); }
        if (expectedVersion is not null)
        {
            using var client = new GitHubReleaseClient();
            var release = await client.GetLatestAsync(CancellationToken.None) ?? throw new InvalidOperationException("Published release is missing.");
            check(release.Version.ToString(3) == expectedVersion, "The public GitHub latest-release endpoint serves the requested release");
            var package = await client.DownloadAsync(release, Path.Combine(output, "release-download", Guid.NewGuid().ToString("N")), null, CancellationToken.None);
            var expectedHash = Environment.GetEnvironmentVariable("WINISLAND_RELEASE_TEST_SHA256");
            check(expectedHash is not null && package.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase),
                "The production updater downloads and verifies the exact installer published by this release task");
            await File.WriteAllTextAsync(Path.Combine(output, "release-verification.json"), JsonSerializer.Serialize(new
            { repository = GitHubReleaseClient.Repository, release.Tag, installer = release.Installer.Url, package.Sha256, bytes = new FileInfo(package.InstallerPath).Length, installed = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("Updater verification timed out.");
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
