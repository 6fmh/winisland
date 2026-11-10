using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Winisland.Services;

public sealed record UploadConfiguration
{
    public string ServerUrl { get; init; } = "";
    public string Token { get; init; } = "";
    public string FileFormName { get; init; } = "file";
    public string UrlPath { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Arguments { get; init; } = new();
}

public sealed class UploadCredentialStore
{
    private readonly string _connectionPath;
    public UploadConfiguration Connection { get; private set; } = new();
    public string ServerUrl => Connection.ServerUrl;
    internal string Token => Connection.Token;
    public bool IsConnected => ServerUrl.Length > 0;
    public event EventHandler? Changed;

    public UploadCredentialStore() : this(SettingsStore.DirectoryPath) { }
    internal UploadCredentialStore(string directoryPath)
    {
        _connectionPath = Path.Combine(directoryPath, "upload-connection.bin");
        // Legacy token-only credentials have no chosen server. Never infer a
        // destination for them or send them to a newly selected server.
        try
        {
            if (File.Exists(_connectionPath))
            {
                if (new FileInfo(_connectionPath).Length > 65536) throw new CryptographicException("Saved upload connection exceeds its size limit.");
                var json = WindowsCredentialProtection.Protect(File.ReadAllBytes(_connectionPath), false);
                Connection = Validate(JsonSerializer.Deserialize<UploadConfiguration>(json) ?? throw new ArgumentException("Invalid upload connection."));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or JsonException)
        { Log.Error("Load protected upload connection", ex); }
    }

    public void Save(string serverUrl, string token) => Save(new UploadConfiguration { ServerUrl = serverUrl, Token = ValidateToken(token) });
    public void Save(UploadConfiguration configuration)
    {
        var validated = Validate(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(_connectionPath)!);
        var temporary = _connectionPath + ".tmp";
        File.WriteAllBytes(temporary, WindowsCredentialProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(validated), true));
        File.Move(temporary, _connectionPath, true);
        Connection = validated;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        try
        {
            File.Delete(_connectionPath);
            File.Delete(Path.Combine(Path.GetDirectoryName(_connectionPath)!, "upload-token.bin"));
        }
        catch (DirectoryNotFoundException) { }
        Connection = new();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string ValidateToken(string token)
    {
        var value = token.Trim();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
        if (value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Enter a token without line breaks, up to 4096 characters.");
        return value;
    }

    internal static UploadConfiguration Validate(UploadConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration.ServerUrl?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("Enter the full HTTPS upload URL for your server.");
        static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
        if (!ValidName(configuration.FileFormName)) throw new ArgumentException("The uploader's file field is invalid.");
        if (configuration.UrlPath is null || configuration.UrlPath.Length > 256
            || configuration.UrlPath.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("The uploader's JSON URL path is invalid.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var arguments = new Dictionary<string, string>();
        foreach (var pair in configuration.Headers ?? throw new ArgumentException("The uploader's headers are invalid."))
        {
            if (headers.Count >= 24 || !ValidName(pair.Key) || pair.Value is null || pair.Value.Length > 4096
                || pair.Value.Any(char.IsControl) || pair.Value.Contains('$') || pair.Value.Contains('{'))
                throw new ArgumentException("Use static request headers without line breaks or ShareX variables.");
            if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Winisland manages the upload Host and Content headers.");
            headers[pair.Key] = pair.Value;
        }
        foreach (var pair in configuration.Arguments ?? throw new ArgumentException("The uploader's form fields are invalid."))
        {
            if (arguments.Count >= 24 || !ValidName(pair.Key) || pair.Key == configuration.FileFormName
                || pair.Value is null || pair.Value.Length > 4096 || pair.Value.Contains('$') || pair.Value.Contains('{'))
                throw new ArgumentException("Use static multipart form fields without ShareX variables.");
            arguments[pair.Key] = pair.Value;
        }
        var result = configuration with { ServerUrl = endpoint.AbsoluteUri, Token = ValidateToken(configuration.Token ?? ""), Headers = headers, Arguments = arguments };
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > 32768) throw new ArgumentException("That upload connection is too large.");
        return result;
    }

    public static UploadConfiguration ImportShareX(string json)
    {
        if (json.Length > 1024 * 1024) throw new ArgumentException("That uploader configuration is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static string? Read(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!string.Equals(Read(root, "RequestMethod"), "POST", StringComparison.OrdinalIgnoreCase) || Read(root, "Body") != "MultipartFormData")
            throw new ArgumentException("Choose a ShareX uploader that sends a multipart POST to an HTTPS server.");
        static Dictionary<string, string> ReadMap(JsonElement root, string name)
        {
            var result = new Dictionary<string, string>();
            if (!root.TryGetProperty(name, out var map)) return result;
            if (map.ValueKind != JsonValueKind.Object) throw new ArgumentException($"The uploader's {name} must contain text values.");
            foreach (var pair in map.EnumerateObject())
            {
                if (pair.Value.ValueKind != JsonValueKind.String) throw new ArgumentException($"The uploader's {name} must contain text values.");
                result[pair.Name] = pair.Value.GetString()!;
            }
            return result;
        }
        var url = Read(root, "URL") ?? "";
        var path = "";
        if (url.Length > 0)
        {
            if (url.StartsWith("$json:", StringComparison.Ordinal) && url.EndsWith('$')) path = url[6..^1];
            else if (url.StartsWith("{json:", StringComparison.Ordinal) && url.EndsWith('}')) path = url[6..^1];
            else if (url.StartsWith('[') && url.EndsWith(')') && url.IndexOf("](", StringComparison.Ordinal) is var split && split >= 0)
            {
                var expression = url[(split + 2)..^1];
                if (expression.StartsWith("$json:", StringComparison.Ordinal) && expression.EndsWith('$')) path = expression[6..^1];
                else throw new ArgumentException("This uploader's URL expression is unsupported. Use a JSON URL field such as $json:url$.");
            }
            else throw new ArgumentException("This uploader's URL expression is unsupported. Use a JSON URL field such as $json:url$.");
        }
        return Validate(new UploadConfiguration
        {
            ServerUrl = Read(root, "RequestURL") ?? "", FileFormName = Read(root, "FileFormName") ?? "",
            Headers = ReadMap(root, "Headers"), Arguments = ReadMap(root, "Arguments"), UrlPath = path
        });
    }
}
