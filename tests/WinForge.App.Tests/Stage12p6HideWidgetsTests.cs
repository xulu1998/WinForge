using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Localization;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the Phase 12 real-desktop defect: applying 「隐藏小组件按钮」
/// (SetOfflineRegistryValue on the OfflineDefaultUser hive) failed with
/// "Attempted to perform an unauthorized operation" (UnauthorizedAccessException)
/// while the sibling 「任务栏搜索仅显示图标」 (TaskbarSearch, same hive and same
/// Explorer\Advanced key path) succeeded.
///
/// Root cause: the target Explorer\Advanced\TaskbarDa lives in the protected
/// Explorer subtree of the Default User template — its template ACLs reject
/// offline writes (the sibling only succeeded because TaskbarSearch already
/// exists in the template). The fix (Case D — policy-based offline-safe
/// equivalent): HideTaskbarWidgets now targets the official user policy branch
/// Software\Policies\Microsoft\Dsh → EnableWebContent = 0 (Windows 11 25H2
/// supported mechanism). WinForge NEVER takes ownership or rewrites ACLs.
/// Plus: localized apply summary with succeeded/failed counts and a visible
/// failed-operation panel; partial apply is never silently treated as success.
/// </summary>
public class Stage12p6HideWidgetsTests
{
    // ---- 1 + 5. exact model: HideTaskbarWidgets uses the Dsh policy branch;
    //        the sibling TaskbarSearch keeps its existing-value target ----

    [Fact]
    public void HideTaskbarWidgets_Targets_Dsh_Policy_Branch()
    {
        var def = new OptimizationCatalog().GetEntries().Single(e => e.Id == "HideTaskbarWidgets");
        var t = def.RegistryTargets.Single();
        Assert.Equal(OptimizationScope.OfflineDefaultUser, def.Scope);
        Assert.Equal("DEFAULT_USER", t.Hive);
        Assert.Equal(@"Software\Policies\Microsoft\Dsh", t.KeyPath);
        Assert.Equal("EnableWebContent", t.ValueName);
        Assert.Equal(OfflineRegistryValueKind.DWord, t.ValueKind);
        Assert.Equal("0", t.RecommendedData);
    }

    [Fact]
    public void TaskbarSearch_Sibling_Still_Targets_Explorer_Advanced()
    {
        var def = new OptimizationCatalog().GetEntries().Single(e => e.Id == "TaskbarSearchIcon");
        var t = def.RegistryTargets.Single();
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", t.KeyPath);
        Assert.Equal("TaskbarSearch", t.ValueName);
    }

    // ---- 2. the failing (old) target is gone from the model ----

    [Fact]
    public void Old_Protected_Explorer_TaskbarDa_Target_Is_Removed()
    {
        var all = new OptimizationCatalog().GetEntries().SelectMany(e => e.RegistryTargets).ToList();
        Assert.DoesNotContain(all, t => t.ValueName == "TaskbarDa");
        Assert.DoesNotContain(all, t => t.KeyPath.Contains(@"Explorer\Advanced", StringComparison.Ordinal)
            && t.ValueName == "TaskbarDa");
    }

    // ---- 3. the corrected target executes successfully through the real engine
    //        (fake offline registry, mounted-workspace contract path) ----

    [Fact]
    public async Task Corrected_Target_Executes_Successfully()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf12_hw_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mount = Path.Combine(root, "mount");
            Directory.CreateDirectory(Path.Combine(mount, "Users", "Default"));
            File.WriteAllBytes(Path.Combine(mount, "Users", "Default", "NTUSER.DAT"), new byte[8]);

            var workspace = new ImageServicingWorkspace
            {
                WorkingDirectory = root,
                MountDirectory = mount,
                WorkingImagePath = Path.Combine(root, "image", "install.wim"),
                State = ServicingWorkspaceState.Mounted,
            };
            var runner = new FakeProcessRunner
            {
                Responder = req => req.Arguments.Contains("/Get-MountedImageInfo")
                    ? new ProcessResult { ExitCode = 0, StandardOutput = $"Mount Dir : {mount}\n" }
                    : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
            };
            var registry = new FakeOfflineRegistryService();
            var service = new WindowsCustomizationExecutionService(
                runner, registry, new InMemoryLoggerService(), new FakeMountIdentityValidator { SessionMatches = true, WithinMount = true });

            var def = new OptimizationCatalog().GetEntries().Single(e => e.Id == "HideTaskbarWidgets");
            var t = def.RegistryTargets.Single();
            var op = new CustomizationOperation
            {
                OperationId = "opt|HideTaskbarWidgets|0",
                OperationType = CustomizationOperationType.SetOfflineRegistryValue,
                DisplayName = "隐藏小组件按钮",
                RegistryHive = t.Hive,
                RegistryKeyPath = t.KeyPath,
                RegistryValueName = t.ValueName,
                RegistryValueKind = t.ValueKind,
                RegistryValueData = t.RecommendedData,
                Scope = def.Scope,
                Risk = RiskClass.Safe,
                IsSelected = true,
            };
            var plan = new CustomizationPlan();
            plan.AddOperation(op);
            plan.Validate();

            var result = await service.ExecuteAsync(plan, workspace, null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(CustomizationOperationStatus.Succeeded, op.ExecutionStatus);
            var written = registry.Values.Keys.Single();
            Assert.Contains(@"WinForge_DEFAULT_USER|Software\Policies\Microsoft\Dsh|EnableWebContent", written);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    // ---- 4. no ACL ownership hack exists in the offline registry writer ----

    [Fact]
    public void OfflineRegistryService_Never_Rewrites_Acls()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "WinForge.Infrastructure", "Customization", "OfflineRegistryService.cs"));
        Assert.DoesNotContain("SetAccessControl", src, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAccessControl", src, StringComparison.Ordinal);
        Assert.DoesNotContain("TakeOwnership", src, StringComparison.Ordinal);
        Assert.DoesNotContain("SetOwner", src, StringComparison.Ordinal);
        // The explicit read-only error path exists (contextual message, no ACL mutation).
        Assert.Contains("read-only template ACL", src, StringComparison.Ordinal);
    }

    // ---- 6 + 7 + 8 + 10. apply result UX: localized summary + visible failures ----

    [Theory]
    [InlineData("en-US", "Application completed: 18 succeeded, 1 failed.")]
    [InlineData("zh-CN", "应用完成：18 项成功，1 项失败。")]
    public async Task Apply_Summary_Shows_Counts_And_Failed_Item_Is_Visible(string culture, string expectedSummary)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var rm = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(PlanReviewViewModel).Assembly);
        var loc = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
        loc.SetCulture(CultureInfo.GetCultureInfo(culture));

        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var plan = PlanSync.EnsureDraftPlan(state);
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "opt|HideTaskbarWidgets|0",
            OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            DisplayName = "隐藏小组件按钮",
            RegistryHive = "DEFAULT_USER", RegistryKeyPath = @"Software\Policies\Microsoft\Dsh",
            RegistryValueName = "EnableWebContent", RegistryValueKind = OfflineRegistryValueKind.DWord,
            RegistryValueData = "0", Scope = OptimizationScope.OfflineDefaultUser,
            Risk = RiskClass.Safe, IsSelected = true,
        });
        for (var i = 0; i < 18; i++)
        {
            plan.AddOperation(new CustomizationOperation
            {
                OperationId = "ok-" + i,
                OperationType = CustomizationOperationType.SetOfflineRegistryValue,
                DisplayName = "OK " + i,
                RegistryHive = "SOFTWARE", RegistryKeyPath = @"Microsoft\K" + i, RegistryValueName = "V",
                RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "1",
                Risk = RiskClass.Safe, IsSelected = true,
            });
        }
        plan.Validate();

        // Simulate the real engine's per-operation outcome: one failed op.
        var failedOp = plan.SelectedOperations.First(o => o.OperationId == "opt|HideTaskbarWidgets|0");
        failedOp.ExecutionStatus = CustomizationOperationStatus.FailedRecoverable;
        failedOp.ErrorDetails = "Attempted to perform an unauthorized operation.";

        var fake = new FakeCustomizationExecutionService
        {
            Result = new CustomizationResult { TotalOperations = 19, Succeeded = 18, FailedOperations = 1 },
        };
        var vm = new PlanReviewViewModel(state, new InMemoryLoggerService(), fake, loc);
        vm.ApplyCommand.CanExecuteChanged += (_, _) => { };
        Assert.True(vm.ApplyCommand.CanExecute(null));
        await vm.ApplyAsync();

        // 7. localized summary with counts (never the raw English engine text).
        Assert.Equal(expectedSummary, vm.ResultSummary);
        // 6. the exact failed operation + reason surfaces.
        Assert.True(vm.HasFailedOperations);
        var item = vm.FailedOperations.Single();
        Assert.Equal("隐藏小组件按钮", item.DisplayName);
        Assert.Contains("unauthorized", item.Reason, StringComparison.OrdinalIgnoreCase);
        // 8. visible in UI state (expanded panel).
        Assert.True(vm.FailedItemsExpanded);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void All_Succeeded_Summary_Is_Localized(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var rm = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(PlanReviewViewModel).Assembly);
        var loc = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
        loc.SetCulture(CultureInfo.GetCultureInfo(culture));

        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var plan = PlanSync.EnsureDraftPlan(state);
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "op-1", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            DisplayName = "A", RegistryHive = "SOFTWARE", RegistryKeyPath = @"Microsoft\K",
            RegistryValueName = "V", RegistryValueKind = OfflineRegistryValueKind.DWord,
            RegistryValueData = "1", Risk = RiskClass.Safe, IsSelected = true,
        });
        plan.Validate();

        var vm = new PlanReviewViewModel(state, new InMemoryLoggerService(), new FakeCustomizationExecutionService(), loc);
        vm.ApplyAsync().GetAwaiter().GetResult();

        Assert.False(vm.HasFailedOperations);
        Assert.Contains(culture == "zh-CN" ? "全部成功" : "all", vm.ResultSummary, StringComparison.Ordinal);
    }

    // ---- 9. partial-failure workflow semantics stay deterministic:
    //        CompletedWithErrors still unlocks Apply/Build (visible as incomplete,
    //        never silently full success) ----

    [Fact]
    public void Partial_Failure_State_Is_Deterministic()
    {
        var h = new Stage12p5BuildFinishStateTestsHarness();

        // Mark the single selected operation as failed (real engine behavior),
        // then apply with a partial-failure result.
        var failedOp = h.Review.Plan!.SelectedOperations.Single();
        failedOp.ExecutionStatus = CustomizationOperationStatus.FailedRecoverable;
        failedOp.ErrorDetails = "boom";
        h.Execution.Result = new CustomizationResult { TotalOperations = 1, Succeeded = 0, FailedOperations = 1 };
        h.Review.ApplyAsync().GetAwaiter().GetResult();

        // Deterministic contract: CompletedWithErrors (visible, never silent success).
        Assert.Equal(CustomizationExecutionState.CompletedWithErrors, h.State.CustomizationExecutionState);
        Assert.True(h.Review.HasFailedOperations);
        // Review completes; the Apply (commit) step unlocks; Next works — the user
        // may proceed AFTER seeing the failures (which are shown on the page).
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[3].State);
        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[4].State);
        Assert.True(h.Wf.NextCommand.CanExecute(null));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinForge.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}

/// <summary>Re-host of the Stage 12.5 wizard harness (steps + gating).</summary>
internal sealed class Stage12p5BuildFinishStateTestsHarness
{
    public AppState State { get; } = new();
    public WorkflowViewModel Wf { get; }
    public PlanReviewViewModel Review { get; }
    public FakeCustomizationExecutionService Execution { get; }

    public Stage12p5BuildFinishStateTestsHarness()
    {
        var logger = new InMemoryLoggerService();
        State.CurrentImageWorkspace = new ImageWorkspace();
        State.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            SelectedEditionName = "Windows 11 Pro",
            WorkingDirectory = @"C:\wf\ws",
            MountDirectory = @"C:\wf\ws\mount",
            WorkingImagePath = @"C:\wf\ws\image\install.wim",
            SourceImageRelativePath = @"sources\install.wim",
            WorkingIndex = 1,
            State = ServicingWorkspaceState.Mounted,
        };
        State.CustomizationExecutionState = CustomizationExecutionState.Completed;
        var plan = PlanSync.EnsureDraftPlan(State);
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "op-1", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "AppA", DisplayName = "App A", Risk = RiskClass.Removable, IsSelected = true,
        });
        plan.Validate();

        var discovery = new FakeCustomizationDiscoveryService
        {
            Inventory = new DiscoveryInventory
            {
                Discovered = true,
                AppxPackages = new[]
                {
                    new DiscoveredAppxPackage { PackageName = "AppA", DisplayName = "App A", Risk = RiskClass.Removable },
                },
                WindowsPackages = Array.Empty<DiscoveredWindowsPackage>(),
                Services = Array.Empty<DiscoveredOfflineService>(),
            }
        };
        var defs = new FakeCustomizationDefinitionProvider();
        var image = new ImageViewModel(State, logger,
            new WorkflowAndCommandTests.FakeInspection(),
            new WorkflowAndCommandTests.FakeFilePicker(),
            new WorkflowAndCommandTests.FakeWorkspaceFactory(),
            new WorkflowAndCommandTests.FakeWimService(),
            new FakeImageServicingService());
        var components = new ComponentsViewModel(State, logger, discovery, defs);
        var knowledge = ComponentKnowledgeTestFactory.Make(State, logger);
        var customize = new CustomizeStepViewModel(components, knowledge,
            ComponentKnowledgeTestFactory.MakeComponentsKnowledge(State, logger),
            ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.Services),
            ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.Privacy),
            ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.System),
            ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.Personalization));
        Execution = new FakeCustomizationExecutionService();
        Review = new PlanReviewViewModel(State, logger, Execution);
        var build = new BuildStepViewModel(State, new FakeBuildService(), new FakeFileSystem(),
            new WorkflowAndCommandTests.FakeFilePicker(), new FakeAdkToolLocator(), logger, new FakeLocalizationService());
        Wf = new WorkflowViewModel(State, image, customize, Review, build);
    }
}
