using System;
using System.IO;
using System.Text.Json;
using WinForge.Core.WorkspaceLifecycle;

namespace WinForge.Infrastructure.WorkspaceLifecycle;

/// <summary>
/// Reads/writes the durable <c>workspace.json</c> manifest inside each wf-*
/// workspace directory. Corrupt JSON surfaces as null (legacy/unknown handling
/// takes over — never an unsafe deletion).
/// </summary>
public static class WorkspaceManifestStore
{
    private const string ManifestFileName = "workspace.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string ManifestPath(string workspaceDirectory)
        => Path.Combine(workspaceDirectory, ManifestFileName);

    public static bool Exists(string workspaceDirectory) => File.Exists(ManifestPath(workspaceDirectory));

    public static WorkspaceManifest? TryLoad(string workspaceDirectory)
    {
        try
        {
            var path = ManifestPath(workspaceDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WorkspaceManifest>(json, Options);
        }
        catch
        {
            return null; // corrupt manifest → unknown, never unsafe
        }
    }

    public static bool TrySave(string workspaceDirectory, WorkspaceManifest manifest)
    {
        try
        {
            var json = JsonSerializer.Serialize(manifest, Options);
            File.WriteAllText(ManifestPath(workspaceDirectory), json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
