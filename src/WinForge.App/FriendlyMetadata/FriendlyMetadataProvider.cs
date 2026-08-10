using System;
using System.Globalization;
using WinForge.Core.Services;

namespace WinForge.App.FriendlyMetadata;

/// <summary>
/// Default <see cref="IFriendlyMetadataProvider"/>. Maps a small, curated set of
/// well-known technical identifiers (the trusted service allowlist and common
/// inbox Appx packages) to localized friendly names defined in
/// <c>Resources/Strings.resx</c>. Matching is conservative so an unknown or
/// version-suffixed identifier still resolves to a real, verifiable name rather
/// than a fabricated one.
/// </summary>
public sealed class FriendlyMetadataProvider : IFriendlyMetadataProvider
{
    private readonly ILocalizationService _localization;

    public FriendlyMetadataProvider(ILocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    public string GetServiceFriendlyName(string serviceName)
    {
        var key = ServiceKey(serviceName);
        return key is not null ? _localization[key + ".Name"] : (serviceName ?? string.Empty);
    }

    public string GetServiceDescription(string serviceName)
    {
        var key = ServiceKey(serviceName);
        return key is not null ? _localization[key + ".Desc"] : string.Empty;
    }

    public string GetAppFriendlyName(string packageName)
    {
        var key = AppKey(packageName);
        return key is not null ? _localization[key + ".Name"] : (packageName ?? string.Empty);
    }

    public string GetAppDescription(string packageName)
    {
        var key = AppKey(packageName);
        return key is not null ? _localization[key + ".Desc"] : string.Empty;
    }

    private static string? ServiceKey(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        var lower = serviceName!.ToLowerInvariant();
        if (lower.Contains("diagtrack")) return "Svc.DiagTrack";
        if (lower.Contains("wersvc")) return "Svc.WerSvc";
        if (lower.Contains("pcasvc")) return "Svc.PcaSvc";
        return null;
    }

    private static string? AppKey(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        // Exact first, then known prefixes (package identities are frequently
        // suffixed with a publisher/architecture token, e.g. _8wekyb3d8bbwe).
        return packageName! switch
        {
            "Microsoft.BingWeather" => "App.Microsoft.BingWeather",
            "Microsoft.GetHelp" => "App.Microsoft.GetHelp",
            "Microsoft.WindowsCamera" => "App.Microsoft.WindowsCamera",
            "Microsoft.XboxApp" => "App.Microsoft.XboxApp",
            _ when packageName!.StartsWith("Microsoft.BingWeather", StringComparison.OrdinalIgnoreCase) => "App.Microsoft.BingWeather",
            _ when packageName!.StartsWith("Microsoft.GetHelp", StringComparison.OrdinalIgnoreCase) => "App.Microsoft.GetHelp",
            _ when packageName!.StartsWith("Microsoft.WindowsCamera", StringComparison.OrdinalIgnoreCase) => "App.Microsoft.WindowsCamera",
            _ when packageName!.StartsWith("Microsoft.XboxApp", StringComparison.OrdinalIgnoreCase) => "App.Microsoft.XboxApp",
            _ => null
        };
    }
}
