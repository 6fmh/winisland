using System;
using System.Linq;
using System.Diagnostics;
using Winisland.Services;

namespace Winisland;

internal static class Program
{
    internal static readonly Stopwatch StartupClock = Stopwatch.StartNew();
    [STAThread]
    private static int Main(string[] args)
    {
        StartupClock.Start();
        // Run before WPF creates a dispatcher or HWND. A hidden launch alone
        // cannot keep subsequently shown WPF windows off the user's desktop.
        BackgroundVerification.SuppressSystemPrompts();
        if (args.Length == 2 && args[0] == "--apply-update") return UpdateInstaller.RunAsync(args[1]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--configure-startup") return StartupRegistration.Configure(args[1]);
        if (BackgroundVerification.IsInvocation(args))
        {
            try
            {
                if (!args.Contains("--verification-worker"))
                    return BackgroundVerification.Run(args);
                BackgroundVerification.RequirePrivateDesktop();
                // DWM throttles presentation on inactive desktops. Software
                // rendering keeps verification clocks and PNG rendering local.
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }
            catch (Exception error)
            {
                BackgroundVerification.WriteFailure(args, error);
                return 1;
            }
        }
        else if (args.Contains("--verification-worker")) return 1;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
