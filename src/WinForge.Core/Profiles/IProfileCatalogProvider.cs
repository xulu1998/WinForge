using System.Collections.Generic;

namespace WinForge.Core.Profiles;

/// <summary>
/// Supplies the reviewed usage-scenario profiles. Implementations are pure data
/// (no platform dependencies), so this interface lives in Core.
/// </summary>
public interface IProfileCatalogProvider
{
    IReadOnlyList<ProfileDefinition> GetProfiles();
}
