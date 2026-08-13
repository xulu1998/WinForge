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
- **Customize **Apps tab** = knowledge-backed decision surface (App).** The `ComponentKnowledgeView`
  engine is **repurposed as the Apps tab** (passed as the tab `Content`; App.xaml's implicit `DataType`
  DataTemplate renders it — no duplicate View/ViewModel); the former separate "Component Knowledge" tab
  is **removed** (ADR-048). The Apps row shows 选择 | 名称 | 作用 | 建议 | 风险 (category dropped from
  the row, kept in hover/detail); raw Windows package identity is hidden from the row and hover card and
  shown **only** in the collapsed detail / Advanced / the CI page. The view presents a default
  **decision-oriented sort** (RecommendedRemove→OptionalRemove→UsuallyKeep→AdvancedOnly→NeverRemove,
  then risk/category/name), filters, a compact hover quick card, and **direct master–detail row
  selection** (ADR-050: clicking any row opens/switches the right-side detail panel; the checkbox only
  toggles plan inclusion, so inspection and removal stay independent and keyboard-accessible via Enter),
  conservative Protected/Unknown UX with explicit block reasons, official-vs-community evidence, and
  deterministic "why" captions. **No automatic destructive selection** — the tab informs only; the user's
  actual plan changes happen through the existing selection→plan flow (the same `appx|` op-ids
  `ComponentsViewModel` uses). The left-rail 组件智能/Component Intelligence page is repositioned as the
  advanced **高级组件检查器 / Component Inspector** inspection surface (raw identities shown only there /
  in detail / Advanced).
  **ADR-049 real-desktop fix:** `Rebuild()` filters to `Curated && RawItems.Count > 0` (only
  present-in-image curated appear; catalog-only/absent excluded; empty before discovery); the view is a
  two-column layout (list + empty-state in Col 0, detail side panel in Col 1) with the detail
  `ContentControl` collapsed (`NullToVis` on `ActiveDetail`) when no detail is selected, so the list is
  never squeezed; an explicit empty-state replaces any empty detail card; `CustomizeStepViewModel
  .DiscoverCommand` is a unified read-only pass running BOTH Components and CI knowledge discovery (one
  button, no duplicate destructive servicing).
  **ADR-050 master–detail:** the per-row 详情 button is removed; `ListView.MouseLeftButtonUp` +
  `KeyDown` route to `ShowDetailCommand` while `CheckBox` clicks are isolated; the active row gets a
  distinct highlight via `IsActiveDetail`; closing detail never touches removal selection.
- **Tests.** Stage 11.2 added 39 `ComponentKnowledgeStage11p2Tests` (Parts A–M); the UX rework
  (ADR-048) added/updated 25 regression tests; the real-desktop defect fix (ADR-049) added 8 + updated
  7 (unified non-destructive discovery, present-only curated, empty-state, detail-collapse, zh-CN
  captions, CI inspector unchanged); full suite **556 pass (Core 53, App 503), 0 errors, 0 warnings
  (Release)**.

### Stage 11.3 — Customize coverage expansion + Personalization activation (ADR-051..054)

- **Operation taxonomy (Core, ADR-051).** `OptimizationAction` (Remove / Disable / Configure / Service /
  Feature), `OptimizationMechanism` (RemoveProvisionedAppx, DisableOptionalFeature, RemoveCapability,
  ServiceStartup, RegistryPolicy, ExplorerPreference, StartPreference, TaskbarPreference, PrivacyPolicy,
  SystemPolicy, VisualPreference, …) and `OptimizationScope` are carried as **data** on
  `CustomizationOperation` (`ActionKind`/`Mechanism`/`Scope`/`ReversalKey`/`RestoreValueData`) — views
  never branch on mechanism; the execution engine still branches on the concrete `OperationType`. New
  `DisableOptionalFeature` + `RemoveCapability` types are validated by `CustomizationPlan.ClassifyBase`
  and executed via DISM with a `FeatureConfigPolicy` allowlist guard (capabilities deliberately not
  offered in the first tranche).
- **Offline registry / Default-User targeting (ADR-052).** `OfflineHivePaths` maps `DEFAULT_USER` to
  `<mount>\Users\Default\NTUSER.DAT` (loaded under `WinForge_DEFAULT_USER` via the existing `RegLoadKey`
  service) so user-scope personalization targets **new users of the offline image**; machine-scope
  entries use SOFTWARE/SYSTEM. The host user's HKCU is never touched. Every entry records its scope +
  the Windows/default restore value.
- **Shared knowledge surface (Part L).** The Windows Components tab reuses `ComponentKnowledgeViewModel`
  with a capability/optional-feature category filter over the composite catalog
  (`CompositeComponentCatalog` = `CuratedComponentCatalog` + generated `WindowsFeaturesCatalog`).
  Services / Privacy / System / Personalization share ONE `OptimizationKnowledgeViewModel` +
  `OptimizationKnowledgeView` (master–detail, checkbox isolation, action-appropriate captions) over the
  generated `OptimizationCatalog`. First tranche: Windows Components 12 · Services 12 (11 reviewed +
  RpcSs informational) · Privacy 11 · System 10 · Personalization 14 (Start/Search + Taskbar + Explorer +
  Lock screen/Desktop + Appearance). The Personalization tab replaces the Experience / Coming Soon tab
  (ADR-054). **OpenSSH Client/Server are modeled as CAPABILITIES** (`OpenSSH.Client~~~~0.0.1.0` /
  `OpenSSH.Server~~~~0.0.1.0`, Microsoft-documented capability identities, mechanism
  `RemoveCapability`) — they resolve through the Capability inventory, stay visible when present, but
  their checkbox is disabled with "当前版本暂不支持应用" until capability execution is reviewed
  (display eligibility ≠ execution eligibility, ADR-055).
- **Review plan (Part S).** `PlanReviewViewModel` lists every selected change with its exact action type
  (移除/禁用/配置/服务/功能), category, offline scope, and revert contract; per-action totals.
- **Tests.** `Stage11p3Tests` covers content validation (name/purpose/recommendation/risk/provenance,
  no Unknown/Experimental leak, community-never-promotes, service allowlist pin, feature-policy pin),
  offline safety (host HKCU never targeted, DEFAULT_USER path), operation mapping per mechanism,
  build/edition gating, post-install-only blocking, core-service blocking, Review action types, reversal
  round-trip, DISM feature-disable execution, capability skip, OpenSSH-via-capability-inventory
  resolution. Full suite **591 pass (Core 53, App 538), 0 errors, 0 warnings (Release)**.

### Phase 11 — Stage 11.4: Scenario Profile / Recommended Configuration Engine (ADR-057..060)

- **Profile model (Core `Profiles/`).** `ProfileDefinition` (Id, DisplayNameKey/DescriptionKey/IconKey,
  Scenarios, `RecommendationOverrides` TargetId+Intent Keep/Trim+ReasonKey+Tier, `RequiredCapabilities`,
  `PreferredCapabilities`, `AvoidedComponents`, CompatibilityRules) — targets are ALWAYS logical
  WinForge ids, never raw package names. The generated `ProfileCatalog` (Infrastructure) supplies 7
  reviewed profiles: Balanced / Gaming / Developer / Office / Lightweight / DedicatedMinimal / Custom.
  Profiles RECOMMEND; they never silently remove. Multi-select is supported (Gaming+Developer, …);
  Custom is exclusive.
- **Recommendation engine (Core, pure).** `RecommendationEngine.Evaluate(RecommendationInput,
  RecommendationContext)` computes `EffectiveRecommendation` (Level/IsPresent/IsApplySupported/Risk/
  WasOverridden/WasProfileDriven/HasConflict/ReasonKeys/SourceRuleIds/Conflicts) SEPARATELY from the
  definition default (never mutated). Documented precedence (ADR-058): Critical safety constraint >
  explicit user override > required dependency (Requires/RecommendsKeeping → a present profile-required
  id) > profile requirement (RequiredCapabilities ∩ present ids) > scenario override (KEEP beats TRIM,
  conflict recorded visibly with `RecommendationConflict`) > component default. Reason keys are
  deterministic + localized (Part F — no runtime AI prose). Shared levels map to action-aware captions
  (Apps 推荐移除 / Feature 推荐禁用 / Privacy 建议关闭 / Service 建议保持默认, Part L).
- **Workflow state (`RecommendationContextService`, App singleton).** Selected profile ids + user
  overrides + present logical ids. Part Q: a NEW image workspace resets the session
  (`ResetForNewWorkflow`); a fresh workflow defaults to NO profile (pure manual mode) — a previous
  aggressive profile is never silently reused on a new ISO.
- **Customize shell (`CustomizeStepViewModel` / `CustomizeView`, final compact layout).** The view is a
  5-row Grid (Auto×4 + star): compact header (title 20px + subtitle + scan/已选 N 项 on ONE row —
  measured 77px, ~45% smaller than before), the two-panel profile area, and a star-height TabControl so
  the component list always consumes the majority of the viewport (measured 372px of 700px → ~7 rows at
  1200×700). `ProfileView` is a real two-column Grid: primary profile radio cards (LEFT, 3 equal columns,
  ~46px cards, ~75% width) vs extra scenarios (RIGHT, 5 compact checkboxes, ~25% width), followed by a
  single-line summary + actions row. No horizontal scrollbars; the recommendation engine is untouched by
  this layout work (Part L).
- **Profile UI (`ProfileViewModel` / `ProfileView`, embedded at the top of Customize).** ONE primary
  profile as mutually-exclusive radio cards in a 3-column equal-width Grid (Part 1 rework) + optional
  EXTRA scenarios as independent checkboxes (Part 2); compact panel (STA-pinned ≤ ~260 px) with the
  Customize tabs/list as the PRIMARY surface. Primary action = 采用推荐选择; 查看方案详情 groups
  decisions by bucket with per-tab breakdown (Part 11); 重新采用推荐 is conditional; every
  profile-driven row shows 配置建议: <profile> → <caption> (Part 13). Primary action = 采用推荐选择 (the ONLY
  selection-changing action); 查看推荐详情 is a demoted secondary link; 重新采用推荐 is conditionally
  visible only after adoption AND divergence (manual change or profile-set change); Custom means "no
  profile-driven overrides" (returns to catalog defaults, preserves manual selections); the Discover
  button flips to 重新扫描 once discovery completed. Profile cards
  (multi-select; en/zh), summary metrics (建议精简/按需确认/建议保留/冲突, computed from real present
  items only — Part O/P), non-destructive 查看推荐方案 preview grouped 推荐执行/建议保留/需要确认/
  冲突·阻止, 采用推荐选择 (the ONLY selection-changing action — eligibility: present + apply-supported +
  Risk==Low + no conflict + not overridden, Part J) and 重新应用推荐 (same eligibility; overrides
  excluded, Part K). Manual toggles mark user overrides via `IRecommendationSubject` rows; adopt uses
  `SetSelectedForAdoption` which never marks overrides.
- **Final flow (2026-08-12): profile selection IS the adoption.** Selecting a primary
  profile (or an extra scenario) immediately runs `ApplyProfileSelections` — Part J eligibility
  unchanged — and the "采用推荐选择" button / AdoptCommand are removed. `RecommendationContextService`
  records Profile-managed ids (`IsProfileManaged`); `ProfileViewModel` re-applies Profile-managed rows
  on profile switches (adds new, drops no-longer-recommended Profile-managed rows) and NEVER touches
  user overrides; Custom preserves the plan; conditional `RestoreCommand` ("恢复此配置推荐") clears
  overrides and recalculates. The recommendation detail is a Customize overlay with an explicit
  close/back (tabs, selections, profile preserved). The header count reads the shared plan via
  `PlanSync.PlanChanged`. Rows show `SelectionOriginText` (由「X」自动选择 / 手动选择).
- **Tests.** 39 `Stage11p4Tests` (model round-trip, multi-scenario combination, precedence incl.
  dependency-keep/override/safety, Gaming/Developer/Office/Lightweight rule sets against the real
  catalog, conflict resolution with reasons, preview/adopt/high-risk/override UX, en/zh localization,
  profile change never mutates the plan); ProfileView added to the WPF binding audit. Full suite
  **662 pass (Core 53, App 609), 0 errors, 0 warnings (Release)**.

## Phase 12 — Workspace Lifecycle & Disk Safety (2026-08-12)

`IWorkspaceLifecycleManager` (Core contract; DISM-backed `WorkspaceLifecycleManager` in Infrastructure) owns
durable `workspace.json` manifests (explicit lifecycle states + transition log). Every cleanup decision is
guarded by the LIVE `/Get-MountedImageInfo` registration (fail closed on query failure; a registered mount —
or any mount nested in the workspace dir — is never deleted). Discard / failed-disposable / completed-with-
output workspaces are cleanup candidates; recoverable checkpoints and completed-without-output workspaces
are retained. Cleanup strips ReadOnly/System/Hidden, reports reclaimed bytes, and records exact leftover
paths on partial failure. The final ISO defaults to `Documents\WinForge` (user output — never a cleanup
target; temp is strictly the workspace root). `BuildStepViewModel` marks Completed + FinalOutputPath and
runs a conservative disk-space guard (`DiskSpaceEstimator`) before building. Settings hosts a **Storage**
surface (`StorageViewModel`/`StorageView`): async, cancellable workspace scan grouped into temp /
recoverable / active / disposable totals with a safe-cleanup preview and one-click 清理临时文件, plus the
workspace-root editor (change/restore default, validation, active-mount block, persisted via
`workspace-roots.json`); multi-root cleanup discovery scans every known root (Part G). Stage 12.2:
`WorkflowViewModel.FinishAsync` runs the Finish auto-cleanup (authoritative DISM-safe; final ISO preserved,
recoverable checkpoints retained, reclaimed bytes reported on the Build step with [立即重试清理] on partial
failure — a cleanup failure is a WARNING, never a build failure); a successful Unmount/Discard auto-cleans
the disposable workspace in the background (Part E).

## Phase 11 — COMPLETED (2026-08-12)

Stage 11.1–11.4 all passed real-desktop validation on the Windows 11 25H2 zh-CN x64 Consumer image and
Phase 11 was merged to `main` via a `--no-ff` merge. The final Stage 11.4 flow: selecting a profile is
the adoption (safe recommendations apply immediately; `ApplyProfileSelections` with Part J eligibility);
`RecommendationContextService` tracks Profile-managed ids vs user overrides; switching profiles
re-applies Profile-managed rows while user overrides always win; Custom preserves the plan; conditional
恢复此配置推荐 clears overrides; the recommendation detail is a Customize overlay with explicit
close/back; the header count reads the shared plan (`PlanSync.PlanChanged`); rows expose
`SelectionOriginText`. One non-blocking follow-up is recorded (ADR-061): allow Extra Scenarios in
Custom mode as keep/recommendation hints without a primary preset.

See DECISIONS.md ADR-045 / ADR-047 / ADR-048 / ADR-049 / ADR-051 / ADR-052 / ADR-053 / ADR-054 / ADR-057 / ADR-058 / ADR-059 / ADR-060 / ADR-061 for the full rationale.


## Phase 12 — Final Architecture (closeout, 2026-08-12)

All Stages 12.1–12.7 are REAL-DESKTOP VALIDATED (Windows 11 25H2) and MERGED to `main`.

1. **Workspace lifecycle manifests** — every workspace persists `workspace.json`
   (`IWorkspaceLifecycleManager` / `WorkspaceLifecycleManager`): explicit states
   Created→Preparing→Mounted→…→Completed/FailedDisposable/Cancelled/Cleaned with a transition log;
   the servicing service transitions on Prepare/Mount/UnmountDiscard/UnmountCommitted/PrepareFailed,
   the build view marks Completed + FinalOutputPath. Manifest and data live in the SAME directory
   (Stage 12.7 unification).
2. **Authoritative DISM mount safety** — the live `/Get-MountedImageInfo` registration is the
   authority: a registered mount (or any mount nested in the workspace dir) is never deleted;
   mount-query failure fails closed; NeedsRemount surfaced for recovery.
3. **CurrentRoot vs KnownRoots** — `IWorkspaceRootSettingsService.CurrentRoot` is the ONLY creation
   root (`WorkspacePathProvider` resolves it live; fixed test override wins, then current root, then
   platform default). `KnownRoots` (historical, including old roots) are scanned / recovered /
   cleaned only — NEVER a creation destination.
4. **Safe cleanup policy** — ReadOnly/System/Hidden stripped before delete; reclaimed bytes reported;
   exact leftover paths recorded on partial failure; retried later; recoverable checkpoints and
   completed-without-output workspaces retained (minimal-retention rule).
5. **Finish / Discard automatic cleanup** — Finish runs the authoritative DISM-safe cleanup of the
   completed workspace (ISO preserved, bytes reported, failure is a warning); a successful
   Unmount/Discard cleans the disposable workspace in the background.
6. **Final ISO vs temporary artifacts** — final ISO defaults to `Documents\WinForge` (user output,
   never a cleanup target); disposable temp is strictly the workspace root.
7. **Disk-space guard** — `DiskSpaceEstimator` (Prepare ≈ WIM×4+2GiB; Build ≈ WIM+media+ISO+2GiB)
   blocks before Prepare/Build with a localized insufficient-space message.
8. **Storage cleanup UI** — Settings → 存储: async scan of wf-* workspaces grouped into temp /
   recoverable / active / disposable totals, safe-cleanup preview, one-click 清理临时文件, legacy
   leftovers flagged 旧版残留, workspace-root editor (change/restore, validation, active-mount
   block, persisted), and every cleanup candidate displays its OWNING ROOT.
9. **Canonical plan-operation normalization** — `CustomizationOperation.CanonicalRegistryTarget()`
   (scope + normalized hive/key/value-name) + mutation-semantics comparison; `CustomizationPlan
   .AddOperation` merges identical effective registry changes into one physical operation with
   provenance (`SourceDefinitionIds`); true conflicts remain validator-blocking; `ConflictKey`
   includes scope.
10. **Apply partial-failure reporting** — localized summary 应用完成：{0} 项成功，{1} 项失败。plus a
    visible failed-operations panel (name + reason); per-operation outcomes are written back from the
    execution snapshot onto the live plan; partial apply is never silently treated as success.
11. **Build→Finish state synchronization** — `WorkflowViewModel.OnBuildChanged` recomputes the step
    graph on `CurrentStage` changes; a CURRENT Build step with `CurrentStage == Completed` maps to
    Completed; FinishCommand refreshes via the correct AsyncRelayCommand type; Finish gating stays
    one source of truth (`CanFinish`).
12. **Shadow-workspace root split fix** — the split (servicing data under a standalone default root
    vs manifest under CurrentRoot) is eliminated by the live-resolving path provider; no duplicate
    workspace ids across roots; repeated workflows do not grow old-root disk usage.

Non-blocking follow-ups (ADR-072): periodic long-run disk-space checks; recoverable-checkpoint
minimization; conservative startup auto-cleanup (Storage/Finish/Discard cover safe paths); Finish
cleanup synchronous-wait UX for large deletes; Custom profile + Extra Scenarios polish (ADR-061).


## Phase 13 — Closeout (2026-08-13)

Validation levels (ADR-084): `WorkflowValidated` (inspection→ISO verification) ·
`VmInstallValidated` (generated ISO boots; Setup/install/reboot/OOBE/desktop PASS) ·
`FullHealthValidated` (everything incl. Windows Update / Defender / Store / DISM ScanHealth /
recovery). `ValidationResult.AllPhasesPassed` evaluates only the phase set required by the declared
Level — never overclaims. Phase 13 baseline = VmInstallValidated (25H2 Pro zh-CN x64 WIM);
FullHealthValidated becomes mandatory only when component-removal coverage becomes substantially
more aggressive. See docs/COMPATIBILITY.md + validation/ records.
