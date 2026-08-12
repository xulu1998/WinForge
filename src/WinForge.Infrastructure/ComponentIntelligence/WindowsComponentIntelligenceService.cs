using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.ComponentIntelligence;

/// <summary>
/// Windows implementation of <see cref="IComponentIntelligenceService"/> (Stage
/// 11.1). It inspects the mounted offline working image through official DISM
/// offline servicing and returns structured raw inventory — never raw DISM text.
///
/// <para>
/// Read-only discovery is implemented for:
/// <list type="bullet">
///   <item><description>Provisioned AppX packages (<c>/Get-ProvisionedAppxPackages</c>).</description></item>
///   <item><description>Windows Capabilities (<c>/Get-Capabilities</c>).</description></item>
///   <item><description>Windows Optional Features (<c>/Get-Features</c>).</description></item>
///   <item><description>Windows CBS / OS packages (<c>/Get-Packages</c>).</description></item>
/// </list>
/// </para>
///
/// <para>The remaining categories (Services, Scheduled Tasks, Drivers, Languages,
/// WinRE, system apps) are reported as <see cref="InventoryStatus.NotSupported"/>
/// because their provider interfaces are designed but not yet implemented.</para>
///
/// <para>Tolerates missing / renamed / edition / build differences: a failed DISM
/// call yields a per-category <see cref="InventoryStatus.Failed"/> with an error
/// message rather than a crash or a silent empty inventory. Cancellation aborts
/// the pass and returns <see cref="ComponentInventory.Cancelled"/> = true.</para>
/// </summary>
public sealed class WindowsComponentIntelligenceService : IComponentIntelligenceService
{
    private readonly IProcessRunner _processRunner;
    private readonly ILoggerService _logger;
    private readonly IMountIdentityValidator _validator;

    public WindowsComponentIntelligenceService(
        IProcessRunner processRunner,
        ILoggerService logger,
        IMountIdentityValidator validator)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<ComponentInventory> DiscoverAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var categories = new List<CategoryDiscoveryResult>();

        try
        {
            _logger.Info("ComponentIntelligence: discovery started.");
            if (workspace is null || !_validator.MatchesSession(workspace) ||
                string.IsNullOrEmpty(workspace.MountDirectory))
            {
                _logger.Warning("ComponentIntelligence: discovery skipped — workspace is not a usable mounted session.");
                return new ComponentInventory { Discovered = false };
            }

            var mountDir = workspace.MountDirectory!;

            cancellationToken.ThrowIfCancellationRequested();
            categories.Add(await DiscoverAppXAsync(mountDir, cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            categories.Add(await DiscoverCapabilitiesAsync(mountDir, cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            categories.Add(await DiscoverFeaturesAsync(mountDir, cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            categories.Add(await DiscoverPackagesAsync(mountDir, cancellationToken));

            // E-J — designed but not implemented this stage.
            categories.Add(NotSupported(ComponentCategory.Service));
            categories.Add(NotSupported(ComponentCategory.ScheduledTask));
            categories.Add(NotSupported(ComponentCategory.Driver));
            categories.Add(NotSupported(ComponentCategory.Language));
            categories.Add(NotSupported(ComponentCategory.WinRecovery));
            categories.Add(NotSupported(ComponentCategory.SystemApp));

            _logger.Info("ComponentIntelligence: discovery completed.");
            return new ComponentInventory { Discovered = true, Categories = categories };
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("ComponentIntelligence: discovery cancelled.");
            return new ComponentInventory { Discovered = true, Cancelled = true, Categories = categories };
        }
    }

    private async Task<CategoryDiscoveryResult> DiscoverAppXAsync(string mountDir, CancellationToken ct)
    {
        try
        {
            var run = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-ProvisionedAppxPackages", mountDir, ct);
            if (!AppxInventoryParser.IsRecognizedOutput(run.StandardOutput))
            {
                throw new InvalidOperationException("DISM returned output that could not be parsed as expected for AppX.");
            }

            var items = AppxInventoryParser.Parse(run.StandardOutput).Cast<IRawInventoryItem>().ToList();
            _logger.Info($"ComponentIntelligence: discovered {items.Count} provisioned AppX package(s).");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.AppX,
                Status = InventoryStatus.Success,
                Items = items
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error($"ComponentIntelligence: AppX discovery failed: {ex.Message}");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.AppX,
                Status = InventoryStatus.Failed,
                Error = ex.Message
            };
        }
    }

    private async Task<CategoryDiscoveryResult> DiscoverCapabilitiesAsync(string mountDir, CancellationToken ct)
    {
        try
        {
            var run = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-Capabilities", mountDir, ct);
            if (!CapabilityInventoryParser.IsRecognizedOutput(run.StandardOutput))
            {
                throw new InvalidOperationException("DISM returned output that could not be parsed as expected for Capabilities.");
            }

            var items = CapabilityInventoryParser.Parse(run.StandardOutput).Cast<IRawInventoryItem>().ToList();
            _logger.Info($"ComponentIntelligence: discovered {items.Count} capability/capabilities.");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.Capability,
                Status = InventoryStatus.Success,
                Items = items
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error($"ComponentIntelligence: capability discovery failed: {ex.Message}");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.Capability,
                Status = InventoryStatus.Failed,
                Error = ex.Message
            };
        }
    }

    private async Task<CategoryDiscoveryResult> DiscoverFeaturesAsync(string mountDir, CancellationToken ct)
    {
        try
        {
            var run = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-Features", mountDir, ct);
            if (!OptionalFeatureInventoryParser.IsRecognizedOutput(run.StandardOutput))
            {
                throw new InvalidOperationException("DISM returned output that could not be parsed as expected for Optional Features.");
            }

            var items = OptionalFeatureInventoryParser.Parse(run.StandardOutput).Cast<IRawInventoryItem>().ToList();
            _logger.Info($"ComponentIntelligence: discovered {items.Count} optional feature(s).");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.OptionalFeature,
                Status = InventoryStatus.Success,
                Items = items
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error($"ComponentIntelligence: optional-feature discovery failed: {ex.Message}");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.OptionalFeature,
                Status = InventoryStatus.Failed,
                Error = ex.Message
            };
        }
    }

    private async Task<CategoryDiscoveryResult> DiscoverPackagesAsync(string mountDir, CancellationToken ct)
    {
        try
        {
            var run = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-Packages", mountDir, ct);
            if (!CbsPackageInventoryParser.IsRecognizedOutput(run.StandardOutput))
            {
                throw new InvalidOperationException("DISM returned output that could not be parsed as expected for Packages.");
            }

            var items = CbsPackageInventoryParser.Parse(run.StandardOutput).Cast<IRawInventoryItem>().ToList();
            _logger.Info($"ComponentIntelligence: discovered {items.Count} Windows package(s).");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.CbsPackage,
                Status = InventoryStatus.Success,
                Items = items
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Error($"ComponentIntelligence: package discovery failed: {ex.Message}");
            return new CategoryDiscoveryResult
            {
                Category = ComponentCategory.CbsPackage,
                Status = InventoryStatus.Failed,
                Error = ex.Message
            };
        }
    }

    private static CategoryDiscoveryResult NotSupported(ComponentCategory category)
    {
        return new CategoryDiscoveryResult { Category = category, Status = InventoryStatus.NotSupported };
    }

    private async Task<ProcessResult> RunDismAsync(string arguments, string mountDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = arguments
        }, ct);

        if (run.ExitCode != 0)
        {
            var detail = RedactMountPath(run.StandardError, mountDir);
            throw new InvalidOperationException(
                $"DISM failed (exit {run.ExitCode}) for '{arguments}'. {detail}".Trim());
        }

        return run;
    }

    private static string RedactMountPath(string text, string mountDir)
    {
        return string.IsNullOrEmpty(text) || string.IsNullOrEmpty(mountDir)
            ? text
            : text.Replace(mountDir, "<mount>", StringComparison.OrdinalIgnoreCase);
    }
}
