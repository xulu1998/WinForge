using System.Collections.Generic;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Supplies the curated, <b>trusted</b> set of offline customization definitions
/// for the Privacy and System pages (Step 3.3 sections H, I, G). Every definition
/// is generated only by WinForge — never from arbitrary UI input — so each
/// resulting operation has a known, documented, offline-safe target (machine
/// policy hive, no signed-in user, no cloud/account policy, no internet folklore).
///
/// <para>Definitions target the offline image's SOFTWARE / SYSTEM hives reachable
/// through the mounted working image. Recommended values are conservative and
/// reversible.</para>
/// </summary>
public sealed class CustomizationDefinitionProvider : ICustomizationDefinitionProvider
{
    // Stable, documented machine-policy registry settings. Hive "SOFTWARE" maps to
    // the offline image's \Windows\System32\config\SOFTWARE during execution.
    private static readonly IReadOnlyList<DiscoveredRegistrySetting> PrivacySettings = new[]
    {
        Setting("privacy.advertising-id", "Turn off advertising ID",
            "Disables the per-device advertising ID used for targeted content.",
            "SOFTWARE", @"Microsoft\Windows\CurrentVersion\Advertising\Id", "Enabled",
            OfflineRegistryValueKind.DWord, "0", CustomizationCategory.Privacy),
        Setting("privacy.tailored-experiences", "Disable tailored experiences",
            "Stops Windows using diagnostics data to offer personalized tips and recommendations.",
            "SOFTWARE", @"Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures",
            OfflineRegistryValueKind.DWord, "1", CustomizationCategory.Privacy),
        Setting("privacy.activity-history", "Disable activity history",
            "Stops collecting and uploading local activity history to the cloud.",
            "SOFTWARE", @"Policies\Microsoft\Windows\System", "EnableActivityHistory",
            OfflineRegistryValueKind.DWord, "0", CustomizationCategory.Privacy),
        Setting("privacy.web-search", "Disable web search in Start",
            "Keeps Start menu search local instead of querying Bing/web results.",
            "SOFTWARE", @"Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions",
            OfflineRegistryValueKind.DWord, "1", CustomizationCategory.Privacy),
        Setting("privacy.app-diagnostics", "Disable app launch tracking",
            "Disables storing of which apps are launched (Start menu telemetry).",
            "SOFTWARE", @"Policies\Microsoft\Windows\AppCompat", "AllowTelemetry",
            OfflineRegistryValueKind.DWord, "0", CustomizationCategory.Privacy),
    };

    private static readonly IReadOnlyList<DiscoveredRegistrySetting> SystemSettings = new[]
    {
        Setting("system.gamedvr", "Disable Game DVR background recording",
            "Prevents the Game DVR service from recording in the background.",
            "SOFTWARE", @"Policies\Microsoft\Windows\GameDVR", "AllowGameDVR",
            OfflineRegistryValueKind.DWord, "0", CustomizationCategory.System),
        Setting("system.cortana", "Disable Cortana (consumer)",
            "Turns off the consumer Cortana integration in the offline image.",
            "SOFTWARE", @"Policies\Microsoft\Windows\Windows Search", "AllowCortana",
            OfflineRegistryValueKind.DWord, "0", CustomizationCategory.System),
        Setting("system.tips", "Disable Windows Spotlight / tips",
            "Suppresses cloud-backed tips and Spotlight content on the lock screen.",
            "SOFTWARE", @"Policies\Microsoft\Windows\CloudContent", "DisableSoftLanding",
            OfflineRegistryValueKind.DWord, "1", CustomizationCategory.System),
    };

    // Trusted service changes. Only services that are safe to disable are offered;
    // each maps to a documented offline SYSTEM-hive Start value.
    private static readonly IReadOnlyList<DiscoveredOfflineService> RecommendedServiceChanges = new[]
    {
        Service("DiagTrack", "Connected User Experiences and Telemetry",
            ServiceStartType.Disabled),
        Service("WerSvc", "Windows Error Reporting Service",
            ServiceStartType.Disabled),
        Service("PcaSvc", "Program Compatibility Assistant Service",
            ServiceStartType.Disabled),
    };

    public IReadOnlyList<DiscoveredRegistrySetting> GetPrivacySettings() => PrivacySettings;

    public IReadOnlyList<DiscoveredRegistrySetting> GetSystemSettings() => SystemSettings;

    public IReadOnlyList<DiscoveredOfflineService> GetRecommendedServiceChanges() => RecommendedServiceChanges;

    private static DiscoveredRegistrySetting Setting(
        string id, string title, string description, string hive, string key, string value,
        OfflineRegistryValueKind kind, string data, CustomizationCategory category) => new()
    {
        SettingId = id,
        Category = category,
        Title = title,
        Description = description,
        Hive = hive,
        KeyPath = key,
        ValueName = value,
        ValueKind = kind,
        RecommendedData = data,
        Risk = RiskClass.Safe
    };

    private static DiscoveredOfflineService Service(
        string name, string display, ServiceStartType start) => new()
    {
        ServiceName = name,
        DisplayName = display,
        CurrentStartValue = (int)ServiceStartType.Automatic,
        RecommendedStartType = start,
        // Trusted, recommended service change — the only user-configurable class.
        ServiceKind = ServiceClass.RecommendedConfigurable,
        Risk = RiskClass.Removable
    };
}
