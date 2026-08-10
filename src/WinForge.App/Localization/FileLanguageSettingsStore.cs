using System;
using System.IO;
using System.Text.Json;
using WinForge.Core.Services;

namespace WinForge.App.Localization;

/// <summary>
/// Persists the chosen culture as a tiny JSON file under
/// <c>%LOCALAPPDATA%/WinForge/language.json</c>. Failures are swallowed so a
/// read-only or missing directory never crashes startup or a language switch.
/// </summary>
public sealed class FileLanguageSettingsStore : ILanguageSettingsStore
{
    private readonly string _filePath;

    public FileLanguageSettingsStore(string? filePath = null)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = Path.Combine(dir, "WinForge");
        try
        {
            Directory.CreateDirectory(baseDir);
        }
        catch
        {
            // best-effort
        }

        _filePath = filePath ?? Path.Combine(baseDir, "language.json");
    }

    public string? LoadCulture()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var text = File.ReadAllText(_filePath);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("culture", out var el) ? el.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public void SaveCulture(string cultureName)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(new { culture = cultureName }));
        }
        catch
        {
            // Persistence is best-effort; the in-memory switch still applies for the session.
        }
    }
}
