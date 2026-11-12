using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
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
    private static string ReleaseJson(string version = VersionText, string? runtime = null, string? notes = null)
    {
        runtime ??= GitHubReleaseClient.Runtime;
        var name = InstallerName(version, runtime);
        object Asset(string filename, byte[] bytes) => new { name = filename,
            browser_download_url = $"https://github.com/6fmh/winisland/releases/download/v{version}/{filename}", size = bytes.Length,
            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        return JsonSerializer.Serialize(new { tag_name = "v" + version, draft = false, prerelease = false,
            body = notes ?? "A little better, every release.\n\nFaster startup and improvements to your notch.",
            assets = new[] { Asset(name, Payload), Asset(name + ".sha256", Checksum(version, runtime)) } });
    }
    private sealed class Fixture : HttpMessageHandler
    {
        internal string Metadata = ReleaseJson();
        internal byte[] Bytes = Payload;
        internal bool Pause, Missing;
        internal bool FailCheck;
        internal TaskCompletionSource<bool>? CheckGate;
        internal readonly List<string> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri == GitHubReleaseClient.LatestReleaseUrl)
            {
                if (CheckGate is not null) await CheckGate.Task.WaitAsync(cancellation);
                return new HttpResponseMessage(FailCheck ? HttpStatusCode.ServiceUnavailable : Missing ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                    { Content = new StringContent(Metadata, Encoding.UTF8, "application/json") };
            }
            if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Checksum(VersionText, GitHubReleaseClient.Runtime)) };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = Pause ? new StreamContent(new PausingStream(Bytes)) : new ByteArrayContent(Bytes) };
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
            check(!service.IsBusy && !service.HasUpdate && service.State == UpdateState.UpToDate, "A repository without a published release completes its check without an error");
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

        await VerifyPresentationAsync(app, output, settings, media, audio, focus, volume, check);

        // Exercise both native appearances with a newer stable release ready in
        // the fixture. A private beta must never ask for it or show its badge.
        foreach (var dynamicIsland in new[] { false, true })
        {
            var privateFixture = new Fixture();
            var privateInstalls = 0;
            using var privateUpdates = new UpdateService(app.Dispatcher, new GitHubReleaseClient(new HttpClient(privateFixture)),
                new Version(0, 4, 0), fixtureDirectory, _ => { privateInstalls++; return Task.CompletedTask; }, updatesEnabled: false);
            settings.Update(saved with { UseDynamicIsland = dynamicIsland, ReducedMotion = true, HideInFullscreen = false, ShowLiveActivity = false });
            var privateNotch = new NotchWindow(settings, media, audio, focus, new FileShelf(), playerVolume: volume, updates: privateUpdates);
            try
            {
                privateNotch.StopPollingForTest(); privateNotch.Show(); privateNotch.UpdateLayout();
                privateUpdates.StartAutomaticChecks();
                await privateUpdates.CheckAsync(); await privateUpdates.InstallAsync();
                check(typeof(UpdateService).GetField("_automatic", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(privateUpdates) is null
                    && privateFixture.Requests.Count == 0 && privateInstalls == 0 && !privateUpdates.IsBusy && !privateUpdates.HasUpdate
                    && !privateUpdates.CanCheck && !privateUpdates.CanInstall,
                    "Private beta schedules no automatic checks and makes no network or installer calls, even when a newer public release exists");
                check(((FrameworkElement)privateNotch.FindName("UpdateTab")).Visibility == Visibility.Collapsed
                    && ((FrameworkElement)privateNotch.FindName("UpdateBadge")).Visibility == Visibility.Collapsed
                    && ((TextBlock)privateNotch.FindName("CompactLabel")).Text != "Update",
                    $"Private beta hides the public Update indicator and tab in {(dynamicIsland ? "Dynamic Island" : "MacBook notch")} appearance");
                privateNotch.OpenUpdates(); privateNotch.UpdateLayout();
                ((Button)privateNotch.FindName("CheckUpdatesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)privateNotch.FindName("InstallUpdateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(40);
                check(((FrameworkElement)privateNotch.FindName("UpdatePanel")).IsVisible
                    && !((Button)privateNotch.FindName("CheckUpdatesButton")).IsEnabled
                    && !((Button)privateNotch.FindName("InstallUpdateButton")).IsEnabled
                    && privateUpdates.Message.Contains("private beta") && privateFixture.Requests.Count == 0 && privateInstalls == 0,
                    "Opening Software update on a private beta explains disabled public updates and cannot check or install through the actual buttons");
                Capture(privateNotch, Path.Combine(output, dynamicIsland ? "private-beta-island-updates.png" : "private-beta-notch-updates.png"));
            }
            finally { privateNotch.CloseForShutdown(); settings.Update(saved); }
        }

        var buildMetadata = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "WinislandPrivateBuild");
        using (var productionUpdates = new UpdateService(app.Dispatcher))
        {
            check(bool.TryParse(buildMetadata.Value, out var privateBuild) && privateBuild == UpdateService.IsPrivateBuild
                && productionUpdates.CanCheck == !privateBuild,
                $"Production updater honors embedded private-build metadata ({buildMetadata.Value}) without changing settings or the version");
            if (UpdateService.IsPrivateBuild)
            {
                productionUpdates.StartAutomaticChecks(); await productionUpdates.CheckAsync(); await productionUpdates.InstallAsync();
                check(!productionUpdates.HasUpdate && !productionUpdates.CanInstall && !productionUpdates.IsBusy
                    && productionUpdates.Message.Contains("private beta"), "The actual private-build production updater remains disabled on all entry points");
            }
        }

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
                check(!service.IsBusy && (UpdateService.IsPrivateBuild ? service.Message.Contains("private beta") : service.Message.Contains("Ready")),
                    "A corrupt update-outcome cache never blocks app startup");
            File.WriteAllText(outcomePath, JsonSerializer.Serialize(new { success = false, message = "Setup could not finish. Try again." }));
            using (var service = new UpdateService(app.Dispatcher))
                check(UpdateService.IsPrivateBuild ? service.Message.Contains("private beta") : service.Message == "Setup could not finish. Try again.",
                    "Public builds retain failed-install retry guidance; private builds keep their disabled-update explanation");
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
    private const string MarkdownNotes = """
        ## Improvements
        - **Media controls** stay close while you read what's new.
        - Longer song titles and device names wrap cleanly without covering the update controls. This entry deliberately spans multiple lines to check that the bullet and its continuation remain aligned.
          - *Reduced motion* keeps transitions calm.
        - Notifications return to the compact notch after you dismiss them.

        ## Fixes
        1. Restore the notch after display changes and sleep.
        2. Keep your saved settings when installing an update.
        3. Use `Ctrl+7` to return to Software update.

        ## More information
        Read the [release guide](https://github.com/6fmh/winisland/releases/tag/v1.2.3) for the full details.

        ### Keyboard and accessibility
        Controls have visible keyboard focus. Status updates remain readable while the changelog scrolls, and the update button stays available below the notes.

        ```text
        Winisland-Setup-1.2.3-win-x64.exe
        ```
        """;

    private static async Task VerifyPresentationAsync(App app, string output, SettingsStore settings, MediaService media,
        AudioService audio, FocusTimer focus, PlayerVolumeService volume, Action<bool, string> check)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var folder = Path.Combine(output, "update-ui");
        Directory.CreateDirectory(folder);
        var saved = settings.Current;
        var observations = new List<object>();
        var checks = new List<object>();
        var passed = true;
        var completed = false;
        void Verify(bool condition, string description)
        { passed &= condition; checks.Add(new { passed = condition, description }); check(condition, description); }
        try
        {
            foreach (var dynamicIsland in new[] { false, true })
            {
                var appearance = dynamicIsland ? "island" : "notch";
                var fixture = new Fixture { FailCheck = true, CheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                var installGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var installs = 0;
                using var service = new UpdateService(app.Dispatcher, new GitHubReleaseClient(new HttpClient(fixture)), new Version(0, 4, 0),
                    Path.Combine(folder, appearance + "-downloads"), async _ => { installs++; await installGate.Task; });
                settings.Update(saved with { UseDynamicIsland = dynamicIsland, ReducedMotion = true, HideInFullscreen = false,
                    ExpandOnHover = false, ShowLiveActivity = false, NotchWidth = 190, NotchHeight = 32 });
                var window = new NotchWindow(settings, media, audio, focus, new FileShelf(), playerVolume: volume, updates: service);
                var links = new List<Uri>();
                window.OpenUpdateLinkForTest = links.Add;
                try
                {
                    window.StopPollingForTest(); window.Show(); window.OpenUpdates();
                    var install = (Button)window.FindName("InstallUpdateButton");
                    var cancel = (Button)window.FindName("CancelUpdateButton");
                    var recheck = (Button)window.FindName("CheckUpdatesButton");
                    var notes = (ReleaseNotesView)window.FindName("UpdateNotes");
                    var title = (TextBlock)window.FindName("UpdateTitle");
                    var status = (TextBlock)window.FindName("UpdateStatus");
                    var surface = (FrameworkElement)window.FindName("Island");
                    await Settle();
                    Verify(service.State == UpdateState.Checking && !recheck.IsEnabled && !install.IsVisible
                        && status.Text.Contains("Checking") && ((FrameworkElement)window.FindName("UpdateNotesSection")).Visibility == Visibility.Collapsed,
                        $"{appearance}: a pending real check shows stable loading feedback without old notes or an install offer");
                    CaptureState("checking");
                    fixture.CheckGate.SetResult(true);
                    await WaitUntil(() => !service.IsBusy); await Settle();
                    Verify(service.State == UpdateState.Error && recheck.IsEnabled && !install.IsVisible
                        && status.Text.Contains("connection") && Contrast(status.Foreground) >= 4.5,
                        $"{appearance}: a failed check wraps its readable error and leaves the real retry action enabled");
                    CaptureState("error");
                    fixture.FailCheck = false; fixture.Missing = true;
                    recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntil(() => service.State == UpdateState.UpToDate); await Settle();
                    Verify(title.Text.Contains("up to date") && !install.IsVisible && recheck.IsEnabled
                        && Math.Abs(surface.ActualHeight - NotchMetrics.UpdateCompactHeight - (dynamicIsland ? 16 : 0)) < .5,
                        $"{appearance}: no available release produces the shorter, up-to-date presentation with no install action");
                    CaptureState("up-to-date");
                    fixture.Missing = false; fixture.Metadata = ReleaseJson(notes: MarkdownNotes);
                    recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntil(() => service.HasUpdate && !service.IsBusy); await Settle();
                    var text = new TextRange(notes.Document.ContentStart, notes.Document.ContentEnd).Text;
                    var inlines = DocumentInlines(notes.Document.Blocks).ToArray();
                    Verify(title.Text == "Winisland 1.2.3" && ((TextBlock)window.FindName("UpdateSubtitle")).Text.Contains("Installed 0.4.0")
                        && install.IsVisible && install.IsEnabled && notes.IsReadOnly && notes.IsDocumentEnabled
                        && text.Contains("Media controls") && text.Contains("Ctrl+7") && !text.Contains("**") && !text.Contains("## Improvements")
                        && notes.Document.Blocks.OfType<System.Windows.Documents.List>().Any(list => list.MarkerStyle == TextMarkerStyle.Decimal)
                        && inlines.OfType<Span>().Any(span => span.FontWeight == FontWeights.SemiBold)
                        && inlines.OfType<Run>().Any(run => run.FontFamily.Source == "Consolas"),
                        $"{appearance}: the native changelog renders headings, emphasis, hanging lists and code as readable selectable text");
                    Verify(Contrast(notes.Foreground) >= 4.5 && notes.FontSize >= 13 && install.ActualHeight >= 44
                        && recheck.ActualHeight >= 44 && ((Button)window.FindName("FullReleaseNotesButton")).ActualHeight >= 44
                        && AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite,
                        $"{appearance}: notes contrast, text size, action hit regions and polite status announcements meet the design contract");
                    var pinned = Bounds(install, window);
                    Verify(notes.ExtentHeight > notes.ViewportHeight + 10 && notes.ViewportHeight >= 80,
                        $"{appearance}: long notes have a usable scroll viewport");
                    notes.ScrollToEnd(); await Settle();
                    Verify(notes.VerticalOffset > 0 && Bounds(install, window) == pinned && install.IsVisible,
                        $"{appearance}: scrolling long release notes leaves the update action fixed and visible");
                    CaptureState("scrolled");
                    notes.ScrollToHome(); await Settle();
                    var scrollbar = VisualDescendants<ScrollBar>(notes).Single(bar => bar.Orientation == Orientation.Vertical && bar.IsVisible);
                    scrollbar.ApplyTemplate();
                    var track = (Track)scrollbar.Template.FindName("PART_Track", scrollbar);
                    observations.Add(new { appearance, stage = "scrollbar", width = scrollbar.ActualWidth, requestedWidth = scrollbar.Width,
                        minimumWidth = scrollbar.MinWidth, thumbMinimum = track.Thumb.MinHeight,
                        source = DependencyPropertyHelper.GetValueSource(scrollbar, FrameworkElement.WidthProperty).BaseValueSource.ToString() });
                    Verify(scrollbar.ActualWidth <= 8 && track.Thumb.MinHeight >= 24,
                        $"{appearance}: the notes use a thin dark native scrollbar with a usable thumb");
                    track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                    track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 24) { RoutedEvent = Thumb.DragDeltaEvent });
                    track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 24, false) { RoutedEvent = Thumb.DragCompletedEvent });
                    await Settle();
                    Verify(notes.VerticalOffset > 0 && Bounds(install, window) == pinned,
                        $"{appearance}: actual control-local thumb dragging scrolls the notes while preserving the fixed action");
                    notes.ScrollToHome(); await Settle();
                    window.Activate(); notes.Focus();
                    var moved = notes.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Verify(moved && ReferenceEquals(Keyboard.FocusedElement, recheck),
                        $"{appearance}: Tab traverses from the notes to the update controls");
                    install.Focus(); install.ApplyTemplate();
                    var focusBorder = (Border)install.Template.FindName("UpdateButtonSurface", install);
                    Verify(install.IsKeyboardFocused && focusBorder.BorderBrush is SolidColorBrush { Color: { B: 255, G: 145, R: 0 } },
                        $"{appearance}: the focused install action has the visible blue keyboard outline");
                    CaptureState("keyboard-focus");
                    Keyboard.ClearFocus();
                    foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        var bounds = Bounds((FrameworkElement)window.FindName("UpdatePanel"), surface);
                        var children = new FrameworkElement[] { (FrameworkElement)window.FindName("UpdateHeader"), notes, status, install, recheck };
                        Verify(children.All(child => { var rect = Bounds(child, surface); return rect.Left >= 24 && rect.Right <= surface.ActualWidth - 24
                                && rect.Top >= 32 && rect.Bottom <= surface.ActualHeight - 10; }),
                            $"{appearance}: title, notes and pinned controls stay inset from the housing at {scale * 100:0}% raster DPI");
                        var artifact = $"{appearance}-available-{scale * 100:0}.png";
                        CaptureReview(window, Path.Combine(folder, artifact), scale);
                        observations.Add(new { appearance, stage = "available", scale, artifact, width = surface.ActualWidth, height = surface.ActualHeight, bounds,
                            notesViewport = notes.ViewportHeight, title = title.Text, status = status.Text });
                    }
                    var fullRelease = (Button)window.FindName("FullReleaseNotesButton");
                    fullRelease.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var link = inlines.OfType<Hyperlink>().Single();
                    link.RaiseEvent(new RequestNavigateEventArgs(link.NavigateUri, "") { RoutedEvent = Hyperlink.RequestNavigateEvent });
                    Verify(links.Count == 2 && links.All(uri => uri.AbsoluteUri == "https://github.com/6fmh/winisland/releases/tag/v1.2.3"),
                        $"{appearance}: full-release and actual inline links route through the isolated local browser callback");
                    window.OpenUpdateLinkForTest = _ => throw new InvalidOperationException("Fixture browser unavailable.");
                    fullRelease.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Verify(status.Text.Contains("Couldn't open your browser"), $"{appearance}: a browser-open failure is reported inline");
                    window.OpenUpdateLinkForTest = links.Add;
                    recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(() => !service.IsBusy); await Settle();
                    Verify(status.Text == service.Message && AutomationProperties.GetName(status) == service.Message,
                        $"{appearance}: a later operation restores the live status bindings after a browser-open failure");
                    fixture.Metadata = ReleaseJson(notes: MarkdownNotes + "\n\n" + new string('x', 6500));
                    recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(() => !service.IsBusy); await Settle();
                    Verify(service.NotesTruncated && ((FrameworkElement)window.FindName("UpdateNotesLimitHint")).IsVisible
                        && fullRelease.IsVisible && fullRelease.IsEnabled,
                        $"{appearance}: bounded release bodies clearly disclose the preview and retain the full-release link");
                    CaptureState("partial");
                    fixture.Metadata = ReleaseJson(notes: "");
                    recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(() => !service.IsBusy); await Settle();
                    Verify(service.HasUpdate && install.IsEnabled && new TextRange(notes.Document.ContentStart, notes.Document.ContentEnd).Text.Contains("No release notes"),
                        $"{appearance}: missing release notes explain the gap while keeping the available update usable");
                    CaptureState("missing-notes");
                    fixture.Metadata = ReleaseJson(notes: MarkdownNotes + "\n\n[Local file](file:///C:/private.txt)\n\n![Remote image](https://example.com/image.png)");
                    recheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(() => !service.IsBusy); await Settle();
                    Verify(DocumentInlines(notes.Document.Blocks).OfType<Hyperlink>().All(item => item.NavigateUri.Scheme == "https")
                        && !DocumentInlines(notes.Document.Blocks).OfType<InlineUIContainer>().Any(),
                        $"{appearance}: release text cannot create a file link or load a remote image into the document");
                    fixture.Pause = true; notes.ScrollToVerticalOffset(40); await Settle();
                    var document = notes.Document; var offset = notes.VerticalOffset;
                    install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntil(() => service.Progress > 0); await Settle();
                    Verify(service.State == UpdateState.Downloading && cancel.IsVisible && cancel.ActualHeight >= 44
                        && !install.IsEnabled && ((ProgressBar)window.FindName("UpdateProgress")).IsVisible
                        && ReferenceEquals(document, notes.Document) && notes.VerticalOffset >= offset - .5,
                        $"{appearance}: actual byte progress and Cancel appear without rebuilding or jumping the changelog");
                    CaptureState("downloading");
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntil(() => !service.IsBusy); await Settle();
                    Verify(service.State == UpdateState.Canceled && install.IsEnabled && !cancel.IsVisible && status.Text.Contains("canceled"),
                        $"{appearance}: cancel leaves a readable outcome and an enabled update retry");
                    CaptureState("canceled");
                    fixture.Pause = false;
                    install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntil(() => service.State == UpdateState.Installing); await Settle();
                    Verify(installs == 1 && !install.IsEnabled && !cancel.IsVisible && !recheck.IsEnabled && status.Text.Contains("Installing"),
                        $"{appearance}: verified installer handoff has explicit installing feedback and disables competing actions");
                    CaptureState("installing");
                    installGate.SetResult(true);
                    await WaitUntil(() => service.State == UpdateState.Restarting && !service.IsBusy); await Settle();
                    var requests = fixture.Requests.Count;
                    install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await service.CheckAsync();
                    Verify(!install.IsEnabled && !recheck.IsEnabled && installs == 1 && fixture.Requests.Count == requests
                        && install.Content.ToString() == "Restarting…",
                        $"{appearance}: completed handoff clearly shows restarting and cannot begin another update or check");
                    CaptureState("restarting");

                    async Task Settle() { await Task.Delay(35); window.UpdateLayout(); }
                    void CaptureState(string stage)
                    {
                        var artifact = $"{appearance}-{stage}.png";
                        CaptureReview(window, Path.Combine(folder, artifact), 1);
                        observations.Add(new { appearance, stage, artifact, state = service.State.ToString(), title = title.Text, status = status.Text,
                            width = surface.ActualWidth, height = surface.ActualHeight, install = install.IsEnabled, check = recheck.IsEnabled });
                    }
                }
                finally { installGate.TrySetResult(true); fixture.CheckGate?.TrySetResult(true); window.CloseForShutdown(); }
            }
            completed = true;
        }
        finally
        {
            settings.Update(saved);
            await File.WriteAllTextAsync(Path.Combine(folder, "runtime.json"), JsonSerializer.Serialize(new
            { passed = passed && completed, timestamp = DateTime.UtcNow, desktop = BackgroundVerification.CurrentDesktop, checks, observations }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static IEnumerable<Inline> DocumentInlines(BlockCollection blocks)
    {
        foreach (var block in blocks)
            if (block is Paragraph paragraph)
            { foreach (var inline in Walk(paragraph.Inlines)) yield return inline; }
            else if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var inline in DocumentInlines(item.Blocks)) yield return inline;
        static IEnumerable<Inline> Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            { yield return inline; if (inline is Span span) foreach (var nested in Walk(span.Inlines)) yield return nested; }
        }
    }
    private static IEnumerable<T> VisualDescendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index);
            if (child is T match) yield return match;
            foreach (var nested in VisualDescendants<T>(child)) yield return nested;
        }
    }
    private static Rect Bounds(FrameworkElement element, Visual relative) => new(element.TranslatePoint(new Point(), (UIElement)relative), element.RenderSize);
    private static double Contrast(Brush brush)
    {
        if (brush is not SolidColorBrush solid) return 0;
        static double Linear(byte channel) { var value = channel / 255.0; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return (.2126 * Linear(solid.Color.R) + .7152 * Linear(solid.Color.G) + .0722 * Linear(solid.Color.B) + .05) / .05;
    }
    private static void CaptureReview(Window window, string path, double scale)
    {
        window.UpdateLayout();
        var width = window.ActualWidth; var height = window.ActualHeight;
        var native = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        native.Render(window);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(174, 179, 187)), null, new Rect(0, 0, width, height));
            context.DrawImage(native, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(native.PixelWidth, native.PixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
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
