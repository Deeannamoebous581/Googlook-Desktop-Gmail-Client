using System;
using System.IO;

namespace Googlook.Services;

/// <summary>
/// Resolves the on-disk profile folder for an account. Each account gets its own
/// directory under %AppData%\Googlook\profiles\{id}; pointing a WebView2 at it
/// gives that account a fully isolated cookie/session/cache store, so ten Google
/// logins (Drive / Gemini / My Account) never bleed into each other.
/// </summary>
public static class BrowserProfile
{
    public static string DirFor(string accountId)
    {
        var id = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Googlook", "profiles", id);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
