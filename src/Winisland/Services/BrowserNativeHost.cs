using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Winisland.Services;

public static class BrowserNativeHost
{
    public static bool IsHostInvocation(string[] arguments) => arguments.Any(a => a == "--browser-host"
        || a.Equals("chrome-extension://" + BrowserIntegration.ExtensionId + "/", StringComparison.Ordinal));

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (!IsHostInvocation(arguments)) return 1;
        using var lifetime = new CancellationTokenSource();
        using var connection = new NamedPipeClientStream(".", BrowserVolumeBridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await connection.ConnectAsync(connectTimeout.Token);
            // These are process-owned standard handles. A Console read can
            // ignore cancellation; disposing it here can wait for browser input.
            // The short-lived host process closes its standard handles on exit.
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            var toApp = RelayAsync(input, connection, lifetime.Token, FindBrowserAncestor());
            var toBrowser = RelayAsync(connection, output, lifetime.Token, "");
            await Task.WhenAny(toApp, toBrowser);
            lifetime.Cancel(); connection.Dispose();
            try { await Task.WhenAll(toApp, toBrowser).WaitAsync(TimeSpan.FromMilliseconds(750)); }
            catch (Exception ex) when (ex is IOException or JsonException or DecoderFallbackException or InvalidOperationException or OperationCanceledException or ObjectDisposedException or TimeoutException) { }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or DecoderFallbackException or InvalidOperationException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Native messaging stdout must contain framed JSON only. Browser
            // disconnections are normal and never write private page data.
            return 1;
        }
    }

    private static async Task RelayAsync(Stream input, Stream output, CancellationToken cancellationToken, string nativeBrowser)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await NativeMessagingProtocol.ReadAsync(input, cancellationToken);
            if (message is null) return;
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 8 });
            if (nativeBrowser.Length > 0)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out var kind)
                    && kind.ValueKind == JsonValueKind.String && kind.GetString() == "hello")
                    message = JsonSerializer.Serialize(new { type = "hello", browser = nativeBrowser });
            }
            await NativeMessagingProtocol.WriteAsync(output, message, cancellationToken);
        }
    }

    private static string FindBrowserAncestor()
    {
        try
        {
            var processId = Environment.ProcessId;
            for (var depth = 0; depth < 6; depth++)
            {
                using var process = Process.GetProcessById(processId);
                var name = BrowserVolumeBridge.BrowserNameForSource(process.ProcessName);
                if (name.Length > 0) return name;
                if (NtQueryInformationProcess(process.Handle, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) break;
                processId = basic.ParentId.ToInt32();
                if (processId <= 0) break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation { public IntPtr Reserved, Peb, Reserved2, Reserved3, Id, ParentId; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out ProcessBasicInformation information, int size, out int returnedLength);
}
