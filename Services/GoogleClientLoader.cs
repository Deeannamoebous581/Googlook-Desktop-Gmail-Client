using System;
using System.IO;
using System.Text.Json;
using Googlook.Models;

namespace Googlook.Services;

/// <summary>
/// Pulls the Google OAuth client id/secret into the config. Preference order:
///   1. values already in the (encrypted) config,
///   2. a client_secret .json downloaded from Google Cloud Console and dropped at
///      %AppData%\Googlook\google_client.json,
///   3. GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET environment variables.
/// This avoids building a settings form just to get the app talking to Gmail.
/// </summary>
public static class GoogleClientLoader
{
    public static string DefaultJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Googlook", "google_client.json");

    /// <summary>Fills config.GoogleClientId/Secret if they're empty. Returns true if credentials are present afterwards.</summary>
    public static bool Ensure(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.GoogleClientId) &&
            !string.IsNullOrWhiteSpace(config.GoogleClientSecret))
            return true;

        if (TryFromJson(DefaultJsonPath, out var id, out var secret) ||
            TryFromEnv(out id, out secret))
        {
            config.GoogleClientId = id;
            config.GoogleClientSecret = secret;
        }

        return !string.IsNullOrWhiteSpace(config.GoogleClientId) &&
               !string.IsNullOrWhiteSpace(config.GoogleClientSecret);
    }

    private static bool TryFromJson(string path, out string id, out string secret)
    {
        id = secret = "";
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            // Google wraps the client under "installed" (desktop) or "web".
            var node = root.TryGetProperty("installed", out var inst) ? inst
                     : root.TryGetProperty("web", out var web) ? web
                     : root;
            id = node.GetProperty("client_id").GetString() ?? "";
            secret = node.GetProperty("client_secret").GetString() ?? "";
            return id.Length > 0 && secret.Length > 0;
        }
        catch { return false; }
    }

    private static bool TryFromEnv(out string id, out string secret)
    {
        id = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? "";
        secret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? "";
        return id.Length > 0 && secret.Length > 0;
    }
}
