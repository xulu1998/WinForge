using WinForge.Core.Compatibility;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.Compatibility;

/// <summary>
/// Thin Infrastructure adapter over the Core <see cref="CompatibilityRuleEngine"/>.
/// Keeps the rule logic platform-agnostic and unit-testable.
/// </summary>
public sealed class ImageCompatibilityService : IImageCompatibilityService
{
    private readonly CompatibilityRuleEngine _engine;

    public ImageCompatibilityService(CompatibilityRuleEngine? engine = null)
        => _engine = engine ?? new CompatibilityRuleEngine();

    public ImageCompatibilityProfile Evaluate(IsoInspectionResult inspection)
        => _engine.Evaluate(inspection);
}
