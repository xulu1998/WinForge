using System.IO;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// Locates the Windows ADK <c>oscdimg.exe</c> tool from the documented ADK
/// install locations. If oscdimg is absent the build pipeline refuses to start
/// and the UI shows the required, friendly message (it does NOT fake ISO
/// creation).
/// </summary>
public sealed class AdkToolLocator : IAdkToolLocator
{
    // Candidate install roots for the Windows Assessment and Deployment Kit.
    private static readonly string[] CandidateRoots =
    {
        @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools",
        @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools",
        @"C:\Program Files (x86)\Windows Kits\8.1\Assessment and Deployment Kit\Deployment Tools",
        @"C:\Program Files\Windows Kits\8.1\Assessment and Deployment Kit\Deployment Tools"
    };

    private static readonly string[] CandidateArchitectures = { "amd64", "x86" };

    public string? FindOscdimg()
    {
        foreach (var root in CandidateRoots)
        {
            foreach (var arch in CandidateArchitectures)
            {
                var candidate = Path.Combine(root, arch, "Oscdimg", "oscdimg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public bool IsAvailable() => FindOscdimg() is not null;
}
