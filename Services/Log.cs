using System;
using System.IO;

namespace Googlook.Services;

/// <summary>
/// Tiny best-effort error log at %AppData%\Googlook\googlook.log. Lets a prototype
/// crash or misbehave *visibly* (there's a file to read) instead of dying silently.
/// Logging itself must never throw or the cure would be worse than the disease.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Googlook", "googlook.log");

    public static void Error(string context, Exception ex) =>
        Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {context}: {ex}");

    public static void Info(string message) =>
        Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

    private static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // Cap growth: start over once the log passes ~512 KB.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    File.WriteAllText(LogPath, string.Empty);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* never let logging break the app */ }
    }
}
