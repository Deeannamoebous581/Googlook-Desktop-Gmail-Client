using System;
using System.Diagnostics;
using System.IO;

namespace Googlook.Services;

/// <summary>
/// Opens a URL in Google Chrome using a per-account isolated profile (Chrome's
/// <c>--user-data-dir</c>), so each Googlook account maps to a separate Chrome login —
/// the same isolation the embedded WebView2 profiles gave, but in real Chrome, which is
/// far more reliable than embedding a browser surface. Falls back to the system default
/// browser if Chrome isn't installed.
/// </summary>
public static class ChromeLauncher
{
    /// <summary>Returns a short human-readable status describing what happened.</summary>
    public static string Launch(string url, string? profileDir)
    {
        if (string.IsNullOrWhiteSpace(url)) return "No address to open.";

        var chrome = FindChrome();
        if (chrome is not null)
        {
            var args = "--new-window ";
            if (!string.IsNullOrWhiteSpace(profileDir))
            {
                try { Directory.CreateDirectory(profileDir); } catch { }
                args += $"--user-data-dir=\"{profileDir}\" ";
            }
            args += $"\"{url}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = chrome,
                Arguments = args,
                UseShellExecute = false,
            });
            return "Opened in Chrome with this account's isolated profile.";
        }

        // Chrome not found — use whatever the OS considers the default browser.
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        return "Chrome not found — opened in your default browser (no per-account isolation).";
    }

    public static bool ChromeAvailable => FindChrome() is not null;

    private static string? FindChrome()
    {
        foreach (var candidate in new[]
        {
            @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
            @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
            @"%LocalAppData%\Google\Chrome\Application\chrome.exe",
        })
        {
            var full = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
