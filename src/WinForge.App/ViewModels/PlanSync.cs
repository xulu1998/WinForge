using System.Linq;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Small helper that keeps the shared <see cref="CustomizationPlan"/> in
/// <see cref="IAppState.CurrentCustomizationPlan"/> in sync with UI selection
/// toggles across the Components / Privacy / System pages. A single stable
/// <c>OperationId</c> per target guarantees the same change is never added twice
/// and is cleanly removed on deselect.
/// </summary>
public static class PlanSync
{
    /// <summary>
    /// Returns a draft plan, creating a fresh one when none exists or the existing
    /// plan has already started / finished (so further editing always works
    /// against a mutable Draft).
    /// </summary>
    public static CustomizationPlan EnsureDraftPlan(IAppState appState)
    {
        var plan = appState.CurrentCustomizationPlan;
        if (plan is null || plan.Status != CustomizationPlanStatus.Draft)
        {
            plan = new CustomizationPlan();
            appState.CurrentCustomizationPlan = plan;
        }

        return plan;
    }

    /// <summary>
    /// Adds the operation (built by <paramref name="buildOp"/>) when selected and
    /// not already present, or removes it when deselected. Never throws for a
    /// frozen plan — it resets to a fresh draft first.
    /// </summary>
    public static void Toggle(
        IAppState appState,
        string operationId,
        bool selected,
        System.Func<CustomizationOperation> buildOp)
    {
        var plan = EnsureDraftPlan(appState);
        var existing = plan.Operations.FirstOrDefault(o => o.OperationId == operationId);

        if (selected)
        {
            if (existing is null)
            {
                var op = buildOp();
                op.IsSelected = true;

                // Safety gate (defense in depth): never add an operation whose
                // underlying item is protected or unsupported. The classification
                // layer should already prevent the UI from offering such an item,
                // but this guarantees a non-allowlisted / protected package (or
                // any other unsafe target) cannot be injected into the plan even
                // if the call is made directly.
                if (op.Risk is RiskClass.Protected or RiskClass.Unsupported)
                {
                    return;
                }

                plan.AddOperation(op);
            }
            else if (!existing.IsSelected)
            {
                plan.SetSelected(operationId, true);
            }
        }
        else if (existing is not null)
        {
            plan.RemoveOperation(operationId);
        }
    }
}
