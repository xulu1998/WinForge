# Architecture

## Desktop Stack

| Concern | Choice |
|---------|--------|
| Language | C# |
| Framework | .NET 8 |
| UI | WPF |
| Pattern | MVVM |

## Solution Structure

```
WinForge.App            WPF Views, ViewModels, navigation, UI
WinForge.Core           Domain models, interfaces, configuration, Build Plan,
                        validation, core business logic
WinForge.Infrastructure Windows platform implementations (DISM, WIM, ESD, ISO,
                        Registry, PowerShell, Windows ADK, File System, Process)
WinForge.Core.Tests     Unit / integration tests for Core
```

## Responsibilities

### WinForge.App
- WPF Views and ViewModels
- Navigation between pages
- UI binding and user interaction
- Calls Core interfaces only

### WinForge.Core
- Domain models (editions, images, build plan, presets)
- Service interfaces (`IIsoInspectionService`, `IWimService`, `IMountService`, …)
- Configuration and validation
- Build Plan model and orchestration logic
- **Must not** reference App or Infrastructure

### WinForge.Infrastructure
- Concrete Windows platform implementations behind Core interfaces
- Future areas: DISM, WIM, ESD, ISO, Registry, PowerShell, Windows ADK,
  File System, Process Execution
- References Core only

## Dependency Direction

```
WinForge.App  ──▶  WinForge.Core  ◀──  WinForge.Infrastructure
```

- `App -> Core`
- `Infrastructure -> Core`
- `Core` must NOT depend on `App`
- `Core` must NOT depend on `Infrastructure`
- WPF Views/ViewModels must NOT call DISM (or any platform API) directly;
  they go through Core interfaces implemented by Infrastructure.

## Runtime Requirements

- **Administrator elevation.** `WinForge.App.exe` embeds an application manifest that
  declares `requestedExecutionLevel level="requireAdministrator"` (uiAccess false). A
  normal launch triggers the Windows UAC prompt. Elevation is required because the
  Phase 2 DISM image enumeration (`dism.exe /Get-ImageInfo`) returns exit code 740
  (ERROR_ELEVATION_REQUIRED) when run without an elevated token. The requirement is
  declarative (in the EXE manifest), not enforced by application code (see DECISIONS.md
  ADR-018).

## Principles

- Platform-specific code lives only in Infrastructure.
- Core expresses *what* to do via interfaces; Infrastructure provides *how*.
- Presets are configuration data consumed by Core, never separate code paths.
- Safety and recoverability are first-class (see DECISIONS.md ADR-008).

## Offline Customization Engine (Step 3.3)

WinForge lets the user declaratively choose, validate, and apply a curated set of safe
offline customizations to the isolated working image produced by Step 3.2 (ADR-019). The
design keeps *what* (the plan) strictly separate from *how* (execution), and confines every
mutation to the mounted working image.

### Layering

```
WinForge.App
  ├─ Views:        ComponentsView, PrivacyView, SystemView, PlanReviewView
  ├─ ViewModels:   Components/…/PlanReview (selection → plan ops via PlanSync)
  └─ App.xaml:     PageKey.Plan + 4 DataTemplates

WinForge.Core  (platform-agnostic — no DISM, no Win32, no WPF)
  ├─ Models:       CustomizationPlan, CustomizationOperation, CustomizationResult,
  │                DiscoveryInventory, enums (Category/OperationType/Risk/ServiceStartType/…)
  └─ Services:     ICustomizationDiscoveryService, ICustomizationExecutionService,
                   IOfflineRegistryService, ICustomizationDefinitionProvider,
                   IMountIdentityValidator

WinForge.Infrastructure  (Windows only)
  ├─ DismAppxParser, DismPackageParser        (exact-identity / allowlist-gated)
  ├─ OfflineRegistryService                   (RegLoadKey/RegUnLoadKey, WinForge_<BASE>)
  ├─ MountIdentityValidator                   (path confinement + session binding)
  ├─ WindowsCustomizationDiscoveryService     (DISM + offline hive enumeration)
  ├─ WindowsCustomizationExecutionService     (guarded, ordered, frozen-snapshot)
  └─ CustomizationDefinitionProvider          (trusted Privacy/System definitions)
```

### Plan model (Core)

- `CustomizationPlan` has a strict lifecycle: `Draft → Validated → Executing →
  Completed / CompletedWithErrors / Failed / Cancelled`. It can be edited only while `Draft`.
- `Validate()` recomputes `OperationValidationResult` for every operation (Duplicate /
  Conflict / Unsupported / MissingTarget) and only marks the plan `Validated` when there are
  no blocking issues **and** at least one operation is selected. It returns the human-readable
  issue list (empty on success).
- `FreezeForExecution()` snapshots the *selected* operations into a locked, execution-safe
  copy and transitions the live plan to `Executing`, so it can no longer be edited mid-run.
- `CustomizationResult.Success` is **computed** (`FailedOperations == 0`); it is never
  assigned. The result also carries `TotalOperations` / `Succeeded` / `FailedOperations` /
  `CriticalFailure` / `Summary`.

### Execution safety (defense-in-depth)

- A pre-run **critical-stop guard** fails the whole result as `CriticalFailure` unless the
  workspace is `Mounted`, the mount session matches (`MatchesSession`), DISM registered the
  mount, and the plan is `Validated`.
- `MountIdentityValidator.IsWithinMount` confines every path to the mount root — no host path,
  no source-ISO root, and the engine never issues an arbitrary command / registry / filesystem
  delete.
- Provisioned Appx are removed only by **exact** DISM deployment package name; package removal
  is gated by a small explicit allowlist — anything else is `Skipped` at execution, never
  removed. Missing services are `Skipped`.
- Offline registry edits load the image's hive file under `HKLM\WinForge_<BASE>` (validated
  name) and **always unload it in a `finally` block**; only `SOFTWARE`/`SYSTEM`/`DEFAULT` may
  be loaded, and host hives are never touched.
- Execution **leaves the image mounted** (no commit/unmount — that belongs to Step 3.2), and
  `IAppState` tracks a "dirty" flag so the UI can warn before discarding a customized working
  image. Reverting is done by discarding the working image and re-preparing, preserving a
  clean baseline.

See DECISIONS.md ADR-020 through ADR-025 for the full rationale.
