using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinForge.Core.WorkspaceLifecycle;

namespace WinForge.Infrastructure.WorkspaceLifecycle;

/// <summary>
/// JSON-persisted workspace root settings (Stage 12.2 Part A/G). Stores the
/// current root plus every known previous root under
/// <c>%LOCALAPPDATA%\WinForge\workspace-roots.json</c> so a root change never
/// orphans old workspaces from cleanup discovery.
/// </summary>
public sealed class WorkspaceRootSettingsService : IWorkspaceRootSettingsService
{
    private readonly string _settingsPath;
    private readonly List<string> _knownRoots;
    private string _currentRoot;

    public WorkspaceRootSettingsService(string? settingsPath = null)
    {
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinForge",
                "workspace-roots.json")
            : settingsPath!;
        _currentRoot = WorkspaceRootValidator.DefaultRoot();
        _knownRoots = new List<string> { _currentRoot };
        Load();
    }

    public string CurrentRoot => _currentRoot;

    public IReadOnlyList<string> KnownRoots => _knownRoots;

    public bool ValidateRoot(string candidate, out string? errorKey)
    {
        errorKey = null;
        if (!WorkspaceRootValidator.IsAcceptablePath(candidate))
        {
            errorKey = "Storage.Root.Invalid";
            return false;
        }

        try
        {
            Directory.CreateDirectory(candidate);
            var probe = Path.Combine(candidate, ".wf_probe_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            errorKey = "Storage.Root.NotWritable";
            return false;
        }
    }

    public bool SetCurrentRoot(string candidate, out string? errorKey)
    {
        if (!ValidateRoot(candidate, out errorKey))
        {
            return false;
        }

        var normalized = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalized, _currentRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return true; // already the current root
        }

        _currentRoot = normalized;
        if (!_knownRoots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _knownRoots.Add(normalized);
        }

        Save();
        return true;
    }

    public void RestoreDefault()
    {
        _currentRoot = WorkspaceRootValidator.DefaultRoot();
        if (!_knownRoots.Contains(_currentRoot, StringComparer.OrdinalIgnoreCase))
        {
            _knownRoots.Add(_currentRoot);
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            if (doc.RootElement.TryGetProperty("CurrentRoot", out var current) &&
                current.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(current.GetString()))
            {
                _currentRoot = current.GetString()!;
            }

            if (doc.RootElement.TryGetProperty("KnownRoots", out var known) &&
                known.ValueKind == JsonValueKind.Array)
            {
                var roots = known.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                    .Select(e => e.GetString()!)
                    .ToList();
                if (roots.Count > 0)
                {
                    _knownRoots.Clear();
                    _knownRoots.AddRange(roots);
                }
            }

            if (!_knownRoots.Contains(_currentRoot, StringComparer.OrdinalIgnoreCase))
            {
                _knownRoots.Insert(0, _currentRoot);
            }
        }
        catch
        {
            // corrupt/absent settings -> defaults; never crash startup
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var payload = new
            {
                CurrentRoot = _currentRoot,
                KnownRoots = _knownRoots,
            };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch
        {
            // best effort — a failed persist must not break the workflow
        }
    }
}
