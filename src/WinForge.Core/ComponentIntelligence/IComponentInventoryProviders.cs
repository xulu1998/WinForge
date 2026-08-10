using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Contract for a single-category inventory provider. Each Stage 11.1 discovery
/// category and each planned later category has a typed interface derived from
/// this, so later stages can fulfill a stable contract without touching the
/// orchestrator's shape.
/// </summary>
public interface ICategoryInventoryProvider
{
    /// <summary>The category this provider enumerates.</summary>
    ComponentCategory Category { get; }

    /// <summary>Enumerates the category's raw items from the mounted image.</summary>
    Task<CategoryDiscoveryResult> EnumerateAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken);
}

// ---- Stage 11.1 implemented providers (Infrastructure supplies concrete types) ----

public interface IAppxInventoryProvider : ICategoryInventoryProvider { }
public interface ICapabilityInventoryProvider : ICategoryInventoryProvider { }
public interface IOptionalFeatureInventoryProvider : ICategoryInventoryProvider { }
public interface ICbsPackageInventoryProvider : ICategoryInventoryProvider { }

// ---- Designed for later stages: interfaces exist, no Infrastructure impl yet ----

public interface IServiceInventoryProvider : ICategoryInventoryProvider { }
public interface IScheduledTaskInventoryProvider : ICategoryInventoryProvider { }
public interface IDriverInventoryProvider : ICategoryInventoryProvider { }
public interface ILanguageInventoryProvider : ICategoryInventoryProvider { }
public interface IWinRecoveryInventoryProvider : ICategoryInventoryProvider { }
public interface ISystemAppInventoryProvider : ICategoryInventoryProvider { }
