using System;
using System.Linq;
using System.Reflection;

namespace Winisland.Services;

// Spotify client IDs are public identifiers. The app owner supplies one at
// build time; account holders only grant permission in Spotify's sign-in page.
internal static class SpotifyAppRegistration
{
    internal static string ClientId { get; } = NormalizeClientId(typeof(App).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "SpotifyClientId")?.Value);

    internal const string UnavailableMessage = "Spotify account sign-in isn't available in this build. Local Spotify controls still work.";

    internal static string NormalizeClientId(string? value)
    {
        var clientId = value?.Trim() ?? "";
        return clientId.Length == 32 && clientId.All(char.IsAsciiHexDigit) ? clientId : "";
    }
}
