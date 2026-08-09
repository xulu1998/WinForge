namespace WinForge.App.FriendlyMetadata;

/// <summary>
/// Resolves a human-friendly display name (and description) for a technical
/// Windows identifier — an offline service name or a provisioned Appx package
/// identity. Friendly names are localized through <see cref="WinForge.Core.Services.ILocalizationService"/>
/// and must NEVER replace the underlying technical identifier, which is what the
/// customization engine targets (ADR: friendly metadata vs immutable identifiers).
/// Unknown identifiers return the identifier itself so the UI always shows a real,
/// verifiable name.
/// </summary>
public interface IFriendlyMetadataProvider
{
    /// <summary>Friendly, localized name for a service (falls back to the raw name).</summary>
    string GetServiceFriendlyName(string serviceName);

    /// <summary>Localized description for a service (empty when unknown).</summary>
    string GetServiceDescription(string serviceName);

    /// <summary>Friendly, localized name for an Appx package (falls back to the raw identity).</summary>
    string GetAppFriendlyName(string packageName);

    /// <summary>Localized description for an Appx package (empty when unknown).</summary>
    string GetAppDescription(string packageName);
}
