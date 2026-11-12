using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Winisland.Services;

namespace Winisland;

// Invoked through the existing --resource-test isolation path. Workloads use
// fixed file-backed fixtures and the actual compiled services and controls.
internal static class PerformanceVerification
{
    internal static async Task<int> RunAsync(App app, string[] args)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var index = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(index >= 0 && index + 1 < args.Length
            ? args[index + 1] : "artifacts/performance-verification");
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        var workloads = new List<object>();
        var passed = false;
        string? error = null;
        void Check(bool condition, string name)
        {
            checks.Add(new { check = name, passed = condition });
            if (!condition) throw new InvalidOperationException(name);
        }
        try
        {
            Check(UpdateService.IsPrivateBuild, "Performance candidates have embedded private-build metadata");
            var idle = Path.Combine(output, "idle");
            Check(await ResourceVerification.RunAsync(app, new[] { "--resource-test", "--output", idle }) == 0,
                "Natural idle resource workload and first-use panels pass");
            Environment.SetEnvironmentVariable("WINISLAND_DATA_DIR", Path.Combine(output, "workload-data"));
            workloads.Add(await RenderingPerformanceVerification.RunAsync(output, Check));
            workloads.Add(await UploadPerformanceVerification.RunAsync(output, Check));
            workloads.Add(await NativeHostPerformanceVerification.RunAsync(output, Check));
            passed = true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            passed, error, desktop = BackgroundVerification.CurrentDesktop,
            privateBuild = UpdateService.IsPrivateBuild, checks, workloads,
            note = "Fixed controlled workloads on the mandatory private desktop. Idle report is separate. "
                + "Compare identical verifier bytes and publish settings; no forced collection in workload timings. "
                + "These fixture measurements do not represent all personal media, browser or upload conditions."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
