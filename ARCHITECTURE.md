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

## UX Workflow & Localization (Phase 3.5)

The Wizard/Stepper is the primary application surface; it orchestrates the existing Step 3.3
customization engine without adding any DISM/Win32 code.

### Workflow layer (App)
- `WinForge.App/Workflow/` holds `WorkflowViewModel` (derives 6 step states purely from
  `IAppState`), `WorkflowStep` / `WorkflowStepState` / `WorkflowStepViewModel`, and
  `IWorkflowNavigator`. There is **no DISM** in this layer — availability is a function of app
  state (image ready, mounted, plan validated, plan executing).
- Navigation is gated: `CanGoNext` / `CanGoBack` follow the current step's state, and
  `CanGoToStep` refuses a `NotAvailable` target or any jump that would skip an earlier
  `NotAvailable` step. Source-change / mounted / executing guards protect the plan (ADR-032).
  Utility pages (Home / Logs / Settings / About) live on a separate rail in `MainViewModel`
  and never disturb step state (ADR-033).

### Localization layer
- User-facing strings live in `WinForge.App/Resources/Strings.resx` (neutral) plus a
  `Strings.zh-CN.resx` satellite. `ResourceManagerLocalizationService` (App) wraps a
  `ResourceManager` and is exposed to XAML as `Loc`; `LocKeyMultiConverter` re-evaluates a
  binding on both a key change and a culture change (ADR-034).
- `ILocalizationService` is defined in **Core** (`WinForge.Core/Services`) so non-UI code can
  localize. `SetCulture` updates the thread + ResourceManager and raises `PropertyChanged` /
  `CultureChanged` for a live switch; `ILanguageSettingsStore` persists the choice and
  `LocalizationBootstrap.Initialize` applies the saved culture with an English fallback
  (ADR-035).
- `FriendlyMetadataProvider` (`WinForge.App/FriendlyMetadata`) maps a trusted allowlist to
  localized `.resx` keys while `ISelectableItem` always preserves the immutable technical id;
  `ServiceConfigPolicy` (Core, ADR-030) remains the single source of truth for which services
  are configurable (ADR-036).

See DECISIONS.md ADR-032 through ADR-037 for the full rationale.

## Build / ISO Export Engine (Phase 10)

Phase 10 turns the isolated, customized working image (produced by Step 3.2/3.3) into a bootable
Windows ISO. The Build step is no longer an honest placeholder: a single orchestrator coordinates
commit → export → media copy → ISO build → verification, and the Build UI reflects the full
lifecycle.

### Layering

```
WinForge.App
  └─ ViewModels:  BuildStepViewModel (derives inputs from IAppState, gates CanBuild,
                  cancellable AsyncRelayCommand, surfaces terminal state + log + output)

WinForge.Core  (platform-agnostic — no DISM, no oscdimg, no WPF)
  ├─ Models:      BuildRequest, BuildResult, BuildState, BuildProgress, BuildFileName,
  │               WimExport*, MediaPrepare*, IsoBuild*, BuildVerification*, BuildRecoveryState
  └─ Services:    IBuildService, IWimExporter, IIsoMediaPreparer, IBootableIsoBuilder,
                  IBuildVerifier, IAdkToolLocator, IImageServicingService (CommitUnmountAsync)

WinForge.Infrastructure  (Windows only)
  ├─ ImageBuildService       (6-phase state machine; atomic build.recovery.json)
  ├─ DismWimExporter         (DISM /Export-Image, /Compress:max /CheckIntegrity)
  ├─ IsoMediaPreparer        (read-only media-tree copy + payload replace, dual-boot validate)
  ├─ OscdimgIsoBuilder       (Windows ADK oscdimg.exe; dual-boot args)
  ├─ OscdimgArgumentBuilder  (pure dual-boot command assembler)
  ├─ BuildVerifier           (independent DISM re-check)
  └─ ImageServicingService   (CommitUnmountAsync — DISM /Unmount-Image /Commit)
```

### Safety boundaries

- **Source is never modified.** The original ISO is read (a transient read-only mount), and the
  final `install.wim` is written into a WinForge-owned build workspace; the committed working image
  is the only thing DISM mutates (ADR-019/ADR-039).
- **Commit, never discard.** The build commits the working image (`/Unmount-Image /Commit`); a commit
  failure stops the build with no ISO and leaves the workspace recoverable (ADR-039).
- **ESD → WIM normalization.** For an ESD source the original `install.esd` is removed and a WIM is
  written at index 1, so Windows Setup reads the payload correctly (ADR-040).
- **No fake ISO.** `oscdimg.exe` is required; a missing ADK or a missing boot file (`boot\etfsboot.com`,
  `efi\microsoft\boot\efisys.bin`) fails the build fast and clearly — the builder never emits a
  non-bootable ISO (ADR-041).
- **Independent verification.** `BuildVerifier` re-checks the output with the real DISM tooling; a
  failed verification makes the build fail, so "success" means a genuinely valid ISO (ADR-043).
- **Crash recovery.** `ImageBuildService` writes `build.recovery.json` atomically; the next run
  detects and cleans a leftover workspace before starting (ADR-043).

See DECISIONS.md ADR-038 through ADR-043 for the full rationale.

## Component Intelligence (Phase 11)

Phase 11 teaches WinForge to *explain* a discovered Windows component to an ordinary user — WHAT it
is, WHETHER they need it, WHAT breaks if removed, HOW risky, and whether it is restorable — while
keeping the discovered Windows object strictly separate from the human knowledge entry. Stage 11.1 is
**read-only discovery + classification + explanation; it never removes anything and never writes to
DISM**.

### Layering

```
WinForge.App
  └─ ViewModels:  ComponentIntelligenceViewModel (Standard=curated only / Advanced=also raw;
                  CanDiscover gated on a mounted workspace; discover → build classified entries)
  └─ ViewModels:  ComponentListItem (presentational wrapper; resolves human text via Loc on every
                  read; Unknown preferred over invented)
  └─ Views:       ComponentIntelligenceView (list + detail prototype; collapsed technical details)
  └─ Converters:  RecommendationToColorConverter, RiskToColorConverter

WinForge.Core  (platform-agnostic — no DISM, no WPF)
  ├─ Models:      ComponentDefinition, ComponentInventory, ComponentInventoryEntry,
  │               IRawInventoryItem (+ RawAppxPackage/RawCapability/RawOptionalFeature/RawCbsPackage),
  │               ComponentMatcher, ComponentCategory, ComponentClassification, enums
  │               (RecommendationLevel, RiskLevel, RemovalSupport, RestoreSupport, SavingsConfidence,
  │               ComponentScenario, DependencyRelation, MatchMethod, InventoryStatus)
  └─ Services:    IComponentIntelligenceService, IComponentCatalogProvider

WinForge.Infrastructure  (Windows only)
  ├─ WindowsComponentIntelligenceService  (read-only orchestrator; 4 DISM passes + 6 NotSupported)
  ├─ AppxInventoryParser / CapabilityInventoryParser /
  │   OptionalFeatureInventoryParser / CbsPackageInventoryParser
  └─ CuratedComponentCatalog  (generated by .tmp/phase11/gen_catalog.py)
```

### Classification & safety boundaries

- **DISCOVERED OBJECT ≠ COMPONENT DEFINITION.** `IRawInventoryItem` is exactly what DISM reported;
  `ComponentDefinition` is WinForge-authored human knowledge. A raw item is `Curated` **only** when a
  catalog `TechnicalTarget` matches it (prefix / contains / exact). `ComponentMatcher` (Core, pure) is
  the single place that performs this mapping (ADR-045).
- **Four-way classification.** `Curated` (understood) · `DiscoveredUnclassified` (present, not yet
  classified) · `Protected` (system-critical / servicing-sensitive — never offered) · `Unsupported`
  (present but not serviced this stage, e.g. Services, Scheduled Tasks, Drivers, Languages, WinRE,
  System Apps).
- **Unknown over invented.** Every `ComponentListItem` getter falls back to the localized
  `Component.Unknown` caption ("Unknown" / "尚未确认") rather than guessing a description, risk, or
  saving. The UI shows that caption visibly.
- **Read-only.** Stage 11.1 implements four DISM enumerations and tolerates per-source failure; the
  six not-yet-implemented categories are reported `NotSupported` (never silently dropped). No removal,
  no DISM write, no image mutation.
- **No Phase-10 regression.** Navigation is a single additive `PageKey.ComponentIntelligence` rail
  entry; the Build/ISO Export behavior is untouched.
- **Real-desktop validation (2026-08-10, Windows 11 25H2 zh-CN x64 Consumer `install.wim`).** The
  architecture goal is confirmed: raw Windows identities are discovered independently from curated
  WinForge logical components. Real classification counts: **Curated 11 · DiscoveredUnclassified 734 ·
  Protected 13 · Unsupported 0**. **PRODUCT CONCLUSION — the 734 discovered objects must NOT become
  734 normal removal checkboxes.** The `DiscoveredUnclassified` raw objects remain exactly that — raw
  Windows identities, surfaced read-only in Advanced mode and never presented as trusted/removable.
  Stage 11.2 (Component Catalog Expansion + Knowledge Import + Customize Integration) is **IMPLEMENTED**
  (2026-08-10, PENDING REAL DESKTOP REVIEW). It progressively turns *representative* families into
  user-understandable logical components with evidence-backed purpose / risk / keep-if / remove-if /
  impact / restore; Unknown stays Unknown until evidence-backed. No deep CBS removal; Protected never
  exposed for removal; no inferred dependencies. Catalog grew **11 → 22** well-understood components
  (AV1/AVC video extensions, Bing News/Search, Calculator, Notepad, Paint, Terminal, To Do, Quick
  Assist, Desktop App Installer, and an "Xbox / Gaming" grouping of 9 AppX identities with a
  Gaming→UsuallyKeep scenario override); `CuratedComponentCatalog` regenerated by the idempotent
  `.tmp/phase11/gen_catalog.py` (exactly 284 resx keys, no duplicate-key warnings).

### Stage 11.2 — Knowledge provenance, import pipeline, and Customize integration

- **Knowledge-provenance model (Core, platform-agnostic).** `KnowledgeSource` (Curated /
  MicrosoftOfficial / WindowsImageDiscovery / Community), `KnowledgeClaim` (with `KnowledgeClaimKind
  .Fact` vs `Recommendation`), and `ScenarioRecommendation` carry per-claim provenance. `Fact` is a
  verified, non-opinion statement; `Recommendation` is an opinionated remove/keep guidance. The two
  are **deliberately separated** so a community removal script can never become a WinForge
  "RecommendedRemove" (ADR-047).
- **Offline import pipeline (Core).** `KnowledgeImportPipeline` ingests `IKnowledgeSourceAdapter`s —
  `MicrosoftOfficialAdapter`, `WindowsImageDiscoveryAdapter`, `Win11DebloatCommunityAdapter`,
  `WinForgeCuratedAdapter`. Candidates **never** auto-promote to `Curated`; a community
  `CommunityProposal` is **never** promoted to `EffectiveRecommendation` (it stays informational and is
  surfaced only as community evidence). Merge is de-duplicated; `Deprecated` entries are excluded.
- **Shared knowledge engine.** `ComponentIntelligenceViewModel` (Standard = curated only, Advanced =
  also raw) is the single discovery + classification engine; `ComponentKnowledgeViewModel` reuses its
  already-classified `Inventory` (single discovery — no double work). `ComponentKnowledgeItem` is the
  presentational wrapper for the Customize tab.
- **Customize "Component Knowledge" tab (App).** `ComponentKnowledgeView` presents human
  name/category/recommendation/risk badges, a default **usefulness sort** (RecommendedRemove first,
  then by risk/name), filters, a compact hover quick card, click-for-detail **without changing
  selection**, conservative Protected/Unknown UX with explicit block reasons, official-vs-community
  evidence, and deterministic "why" captions. **No automatic destructive selection** — the tab informs
  only; the user's actual plan changes happen through the existing selection→plan flow. Component
  Intelligence remains the advanced inspection surface.
- **Tests.** 39 new `ComponentKnowledgeStage11p2Tests` (Parts A–M) plus the curated-count updates to
  the Stage 11.1 suite; full suite **530 pass (Core 53, App 477), 0 errors, 0 warnings (Release)**.

See DECISIONS.md ADR-045 for the full rationale.
