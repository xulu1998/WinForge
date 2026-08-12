using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Stage 11.1 orchestrator tests for <see cref="ComponentIntelligenceViewModel"/>.
/// Verifies the prototype seeds catalog-only curated rows, gates discovery on a
/// mounted workspace, populates / filters entries, and rebuilds (preserving the
/// selected row) when the active language changes. No DISM, no real mount — a stub
/// <see cref="IComponentIntelligenceService"/> supplies the raw inventory.
/// </summary>
public class ComponentIntelligenceViewModelTests
{
    // Localization fake that (a) mirrors the real service by returning the key when
    // unresolved and (b) can actually raise CultureChanged so the rebuild path is
    // exercised.
    private sealed class RaisingFakeLoc : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en");
        public event EventHandler? CultureChanged;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }
        public string this[string key] => key;
        public bool Contains(string key) => true;
        public void SetCulture(CultureInfo culture) => CurrentCulture = culture;
        public void RaiseCultureChanged() => CultureChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private sealed class StubService : IComponentIntelligenceService
    {
        public ComponentInventory Result { get; set; } = new ComponentInventory();
        public int CallCount { get; private set; }

        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private static (ComponentIntelligenceViewModel vm, AppState state, StubService svc, RaisingFakeLoc loc) Build()
    {
        var state = new AppState();
        var logger = new InMemoryLoggerService();
        var loc = new RaisingFakeLoc();
        var svc = new StubService();
        var catalog = new CuratedComponentCatalog();
        var vm = new ComponentIntelligenceViewModel(state, logger, svc, catalog, loc);
        return (vm, state, svc, loc);
    }

    private static ComponentInventory MakeDiscoveredInventory() => new ComponentInventory
    {
        Discovered = true,
        Cancelled = false,
        Categories = new List<CategoryDiscoveryResult>
        {
            new CategoryDiscoveryResult
            {
                Category = ComponentCategory.AppX,
                Status = InventoryStatus.Success,
                Items = new List<IRawInventoryItem>
                {
                    new RawAppxPackage
                    {
                        Category = ComponentCategory.AppX,
                        RawIdentity = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
                        DisplayName = "Microsoft.BingWeather",
                        State = "Provisioned"
                    },
                    new RawAppxPackage
                    {
                        Category = ComponentCategory.AppX,
                        RawIdentity = "Contoso.Fabrikam_8wekyb3d8bbwe",
                        DisplayName = "Fabrikam",
                        State = "Provisioned"
                    }
                }
            }
        }
    };

    // ---- Construction / seeding ----

    [Fact]
    public void Constructor_SeedsCatalogOnlyCuratedRows_AndIsNotYetDiscovered()
    {
        var (vm, _, _, _) = Build();

        Assert.True(vm.StandardMode);
        Assert.Equal(22, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.True(e.IsCurated));
        Assert.False(vm.HasInventory);
        Assert.Equal("ComponentIntelligence.NoImage", vm.StatusMessage);
        Assert.False(vm.CanDiscover); // no mounted workspace
    }

    // ---- CanDiscover gating ----

    [Fact]
    public void CanDiscover_TrueOnlyWhenWorkspaceMounted()
    {
        var (vm, state, _, _) = Build();

        Assert.False(vm.CanDiscover);
        Assert.False(vm.DiscoverCommand.CanExecute(null));

        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };

        Assert.True(vm.IsMounted);
        Assert.True(vm.CanDiscover);
        Assert.True(vm.DiscoverCommand.CanExecute(null));
    }

    [Fact]
    public async Task DiscoverAsync_NotMounted_ReturnsEarlyWithoutCallingService()
    {
        var (vm, _, svc, _) = Build();

        Assert.False(vm.CanDiscover);
        await vm.DiscoverAsync();

        Assert.Equal(0, svc.CallCount);
        Assert.False(vm.HasInventory);
    }

    // ---- Discovery populates + classifies ----

    [Fact]
    public async Task DiscoverAsync_PopulatesEntries_AndSetsCounts()
    {
        var (vm, state, svc, _) = Build();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };
        svc.Result = MakeDiscoveredInventory();

        await vm.DiscoverAsync();

        Assert.True(vm.HasInventory);
        Assert.Equal(22, vm.CuratedCount);   // BingWeather matched + 21 catalog-only
        Assert.Equal(1, vm.UnclassifiedCount);
        Assert.Equal(0, vm.ProtectedCount);
        Assert.Equal(0, vm.UnsupportedCount);

        // Standard mode shows only curated rows.
        Assert.Equal(22, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.True(e.IsCurated));
        Assert.NotEqual("ComponentIntelligence.NoImage", vm.StatusMessage);
    }

    [Fact]
    public async Task DiscoverAsync_AdvancedMode_ShowsUnclassifiedRows()
    {
        var (vm, state, svc, _) = Build();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };
        svc.Result = MakeDiscoveredInventory();
        vm.StandardMode = false;

        await vm.DiscoverAsync();

        Assert.Equal(23, vm.Entries.Count); // 22 curated + 1 unclassified
        Assert.Contains(vm.Entries, e => !e.IsCurated);
    }

    [Fact]
    public async Task StandardModeToggle_RebuildsEntryList()
    {
        var (vm, state, svc, _) = Build();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };
        svc.Result = MakeDiscoveredInventory();

        await vm.DiscoverAsync();
        Assert.Equal(22, vm.Entries.Count); // standard

        vm.StandardMode = false;
        Assert.Equal(23, vm.Entries.Count); // advanced

        vm.StandardMode = true;
        Assert.Equal(22, vm.Entries.Count); // back to standard
    }

    [Fact]
    public async Task DiscoverAsync_Cancelled_SetsCancelledStatus()
    {
        var (vm, state, svc, _) = Build();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };
        svc.Result = new ComponentInventory
        {
            Discovered = true,
            Cancelled = true,
            Categories = new List<CategoryDiscoveryResult>()
        };

        await vm.DiscoverAsync();

        Assert.Equal("ComponentIntelligence.StatusCancelled", vm.StatusMessage);
    }

    // ---- Culture switch rebuild ----

    [Fact]
    public void CultureChanged_RebuildsAndPreservesSelectedLogicalId()
    {
        var (vm, _, _, loc) = Build();

        // Pick a non-first row so we prove the selection is genuinely restored.
        var target = vm.Entries[^1];
        var id = target.Entry.LogicalId;
        vm.SelectedEntry = target;
        Assert.Equal(id, vm.SelectedEntry!.Entry.LogicalId);

        loc.RaiseCultureChanged();

        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal(id, vm.SelectedEntry!.Entry.LogicalId);
        // Count is stable across the rebuild (catalog-only curated seed).
        Assert.Equal(22, vm.Entries.Count);
    }
}
