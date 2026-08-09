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
        var appx = new List<DiscoveredAppxPackage>();
        try
        {
            var appxOut = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-ProvisionedAppxPackages", cancellationToken);
            appx.AddRange(DismAppxParser.Parse(appxOut));
            _logger.Info($"Customization: discovered {appx.Count} provisioned Appx package(s).");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Customization: Appx discovery failed: {ex.Message}");
        }

        // --- Windows packages ---
        var packages = new List<DiscoveredWindowsPackage>();
        try
        {
            var pkgOut = await RunDismAsync(
                $"/English /Image:\"{mountDir}\" /Get-Packages", cancellationToken);
            packages.AddRange(DismPackageParser.Parse(pkgOut));
            _logger.Info($"Customization: discovered {packages.Count} Windows package(s).");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Customization: package discovery failed: {ex.Message}");
        }

        // --- Offline services (SYSTEM hive) ---
        var services = DiscoverServices(workspace);

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
            RegistrySettings = settings
        };
    }

    private IReadOnlyList<DiscoveredOfflineService> DiscoverServices(ImageServicingWorkspace workspace)
    {
        var hiveFile = OfflineHivePaths.GetHiveFilePath(workspace, "SYSTEM");
        if (hiveFile is null || !System.IO.File.Exists(hiveFile))
        {
            _logger.Warning("Customization: SYSTEM hive not found; no offline services discovered.");
            return Array.Empty<DiscoveredOfflineService>();
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
        }
        catch (Exception ex)
        {
            _logger.Warning($"Customization: service discovery failed: {ex.Message}");
        }
        finally
        {
            if (handle is not null)
            {
                _registry.UnloadHive(handle);
            }
        }

        return services;
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

    private async Task<string> RunDismAsync(string arguments, CancellationToken cancellationToken)
    {
        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = arguments
        }, cancellationToken);

        return run.StandardOutput ?? string.Empty;
    }
}
