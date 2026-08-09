using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Windows implementation of <see cref="ICustomizationDiscoveryService"/>
/// (Step 3.3 sections C, D, E, G, H). It inspects the mounted offline working
/// image through official offline servicing mechanisms and returns structured
/// candidate items — never raw DISM text.
///
/// <para>
/// <list type="bullet">
///   <item><description>Provisioned Appx packages via <c>dism /Get-ProvisionedAppxPackages</c> (exact identity only).</description></item>
///   <item><description>Windows packages via <c>dism /Get-Packages</c>, classified for safe-removal gating.</description></item>
///   <item><description>Offline services via the SYSTEM hive (real current Start values, correct ControlSet).</description></item>
///   <item><description>Trusted Privacy / System registry definitions from the provider.</description></item>
/// </list>
/// </para>
///
/// <para>It tolerates missing / renamed / edition / build differences: a failed
/// DISM call or absent hive yields a partial inventory rather than a crash, and a
/// workspace that is not a usable mounted session returns an empty (undiscovered)
/// inventory.</para>
/// </summary>
public sealed class WindowsCustomizationDiscoveryService : ICustomizationDiscoveryService
{
    private readonly IProcessRunner _processRunner;
    private readonly IOfflineRegistryService _registry;
    private readonly ICustomizationDefinitionProvider _definitions;
    private readonly ILoggerService _logger;
    private readonly IMountIdentityValidator _validator;

    public WindowsCustomizationDiscoveryService(
        IProcessRunner processRunner,
        IOfflineRegistryService registry,
        ICustomizationDefinitionProvider definitions,
        ILoggerService logger,
        IMountIdentityValidator validator)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<DiscoveryInventory> DiscoverAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
    {
        _logger.Info("Customization: discovery started.");
        if (workspace is null || !_validator.MatchesSession(workspace) ||
            string.IsNullOrEmpty(workspace.MountDirectory))
        {
            _logger.Warning("Customization: discovery skipped — workspace is not a usable mounted session.");
            return new DiscoveryInventory { Discovered = false };
        }

        var mountDir = workspace.MountDirectory!;

        // --- Appx packages ---
        var (appx, appxStatus, appxError) = await DiscoverAppxAsync(mountDir, cancellationToken);

        // --- Windows packages ---
        var (packages, pkgStatus, pkgError) = await DiscoverPackagesAsync(mountDir, cancellationToken);

        // --- Offline services (SYSTEM hive) ---
        var (services, svcStatus, svcError) = DiscoverServices(workspace);

        // --- Trusted registry definitions (Privacy + System) ---
        var settings = new List<DiscoveredRegistrySetting>();
        settings.AddRange(_definitions.GetPrivacySettings());
        settings.AddRange(_definitions.GetSystemSettings());
        _logger.Info($"Customization: surfaced {settings.Count} trusted registry definition(s).");

        return new DiscoveryInventory
        {
            Discovered = true,
            AppxPackages = appx,
            WindowsPackages = packages,
            Services = services,
            RegistrySettings = settings,
            AppxStatus = appxStatus,
            AppxError = appxError,
            PackageStatus = pkgStatus,
            PackageError = pkgError,
            ServiceStatus = svcStatus,
            ServiceError = svcError
        };
    }

    private async Task<(List<DiscoveredAppxPackage>, DiscoverySourceStatus, string?)> DiscoverAppxAsync(
        string mountDir, CancellationToken cancellationToken)
    {
        try
        {
            var run = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-ProvisionedAppxPackages", mountDir, cancellationToken);
            var items = DismAppxParser.Parse(run.StandardOutput);
            _logger.Info($"Customization: discovered {items.Count} provisioned Appx package(s).");
            return (new List<DiscoveredAppxPackage>(items), DiscoverySourceStatus.Success, null);
        }
        catch (Exception ex)
        {
            // A DISM failure or unrecognized/localized output must surface as an
            // error — never as a successful "0 apps discovered".
            _logger.Error($"Customization: Appx discovery failed: {ex.Message}");
            return (new List<DiscoveredAppxPackage>(), DiscoverySourceStatus.Failed, ex.Message);
        }
    }

    private async Task<(List<DiscoveredWindowsPackage>, DiscoverySourceStatus, string?)> DiscoverPackagesAsync(
        string mountDir, CancellationToken cancellationToken)
    {
        try
        {
            var run = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-Packages", mountDir, cancellationToken);
            var items = DismPackageParser.Parse(run.StandardOutput);
            _logger.Info($"Customization: discovered {items.Count} Windows package(s).");
            return (new List<DiscoveredWindowsPackage>(items), DiscoverySourceStatus.Success, null);
        }
        catch (Exception ex)
        {
            _logger.Error($"Customization: package discovery failed: {ex.Message}");
            return (new List<DiscoveredWindowsPackage>(), DiscoverySourceStatus.Failed, ex.Message);
        }
    }

    private (IReadOnlyList<DiscoveredOfflineService>, DiscoverySourceStatus, string?) DiscoverServices(
        ImageServicingWorkspace workspace)
    {
        var hiveFile = OfflineHivePaths.GetHiveFilePath(workspace, "SYSTEM");
        if (hiveFile is null || !System.IO.File.Exists(hiveFile))
        {
            const string msg = "SYSTEM hive not found in the mounted image; no offline services discovered.";
            _logger.Error($"Customization: service discovery failed: {msg}");
            return (Array.Empty<DiscoveredOfflineService>(), DiscoverySourceStatus.Failed, msg);
        }

        var services = new List<DiscoveredOfflineService>();
        var hiveName = OfflineHivePaths.GetWinForgeHiveName("SYSTEM");
        OfflineHiveHandle? handle = null;
        try
        {
            handle = _registry.LoadHive(hiveFile, hiveName);

            var current = ReadCurrentControlSet(handle);
            var servicesRoot = $"ControlSet{current:D3}\\Services";

            foreach (var name in _registry.EnumSubKeys(handle, servicesRoot))
            {
                var key = $"{servicesRoot}\\{name}";
                var startValue = _registry.GetValue(handle, key, "Start");
                var display = _registry.GetValue(handle, key, "DisplayName") ?? name;
                var start = int.TryParse(startValue, out var s)
                    ? s
                    : (int)ServiceStartType.Manual;

                services.Add(new DiscoveredOfflineService
                {
                    ServiceName = name,
                    DisplayName = display,
                    CurrentStartValue = start,
                    Risk = RiskClass.Removable
                });
            }

            _logger.Info($"Customization: discovered {services.Count} offline service(s).");
            return (services, DiscoverySourceStatus.Success, null);
        }
        catch (Exception ex)
        {
            // A failed hive load / enumeration must surface as an error, never as
            // a silent "0 services discovered".
            _logger.Error($"Customization: service discovery failed: {ex.Message}");
            return (services, DiscoverySourceStatus.Failed, ex.Message);
        }
        finally
        {
            if (handle is not null)
            {
                _registry.UnloadHive(handle);
            }
        }
    }

    private int ReadCurrentControlSet(OfflineHiveHandle handle)
    {
        var raw = _registry.GetValue(handle, "Select", "Current");
        if (int.TryParse(raw, out var current) && current >= 1)
        {
            return current;
        }

        // Fallback to ControlSet001 if the Select value is absent.
        return 1;
    }

    /// <summary>
    /// Runs DISM offline and enforces two invariants that the previous
    /// implementation ignored: (1) a non-zero exit code is a hard failure, and
    /// (2) output that is not recognizable as DISM output (e.g. a localized
    /// response when <c>/English</c> was not honoured) is treated as a failure
    /// rather than an empty-but-successful discovery. The mount path is redacted
    /// from any error text so no sensitive filesystem location is logged.
    /// </summary>
    private async Task<ProcessResult> RunDismAsync(
        string arguments, string mountDir, CancellationToken cancellationToken)
    {
        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = arguments
        }, cancellationToken);

        if (run.ExitCode != 0)
        {
            var detail = RedactMountPath(run.StandardError, mountDir);
            throw new InvalidOperationException(
                $"DISM failed (exit {run.ExitCode}) for '{arguments}'. {detail}".Trim());
        }

        var recognized = arguments.Contains("/Get-ProvisionedAppxPackages", StringComparison.OrdinalIgnoreCase)
            ? DismAppxParser.IsRecognizedOutput(run.StandardOutput)
            : DismPackageParser.IsRecognizedOutput(run.StandardOutput);

        if (!recognized)
        {
            throw new InvalidOperationException(
                $"DISM returned output that could not be parsed as expected (localized or unexpected format) for '{arguments}'.");
        }

        return run;
    }

    private static string RedactMountPath(string text, string mountDir)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(mountDir))
        {
            return text;
        }

        return text.Replace(mountDir, "<mount>", StringComparison.OrdinalIgnoreCase);
    }
}
