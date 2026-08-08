using WinForge.Core.Models;
using Xunit;

namespace WinForge.Core.Tests;

public class BuildPlanTests
{
    [Fact]
    public void BuildPlan_CanBeInstantiated()
    {
        var plan = new BuildPlan();

        Assert.NotNull(plan);
        Assert.NotNull(plan.Settings);
        Assert.Empty(plan.Settings);
    }
}
