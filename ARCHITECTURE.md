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


## Phase 14 — Stage 14.1: Deep Component Classification (2026-08-13)

Discovery / knowledge / planning are separate layers (ADR-085). `DeepComponentClassifier` maps raw
discovered identities onto `DeepComponentKnowledge` via `DeepComponentCatalogData` (108 curated
family entries): exact alias (KnownPattern), normalized family containment (KnownFamily), else null
(Unknown stays visible). Heuristic-classified entries can never present as Low risk or unprotected.
`ComponentNormalizer` strips ~/_ tokens, versions and .neutral with a collision guard. Risk
(Low/Moderate/High/Critical), recommendation (RecommendedRemove/OptionalRemove/RecommendedKeep/
RequiredKeep/ProfileDependent/Unknown), protection (None/Sensitive/Protected) and profile tags drive
the Customize knowledge surface: classified DiscoveredUnclassified rows now show name/purpose/
recommendation/risk instead of raw identifiers. No removal execution in this stage.

## Phase 14 — Stage 14.2: Real-media family expansion (2026-08-13)

DeepComponentCatalogData expanded to 145 curated entries: 22 CBS family rules (conservative:
Risk ≥ Moderate unless explicitly known, Protection ≥ Sensitive, never RecommendedRemove) and 15
hardware/driver family rules (RecommendedKeep/ProfileDependent). UnknownFamilyAnalyzer clusters
unclassified identities into ranked families for the debt report. ClassificationCoverageMetrics
distinguishes Curated / KnownDeep / Protected / Heuristic / Unknown per discovery source without
double counting. The build sandbox is non-elevated (DISM Error 740) — the real-media baseline is
the Phase 11 elevated scan; an exact per-object re-scan requires elevation (see docs/COMPONENT-
COVERAGE.md). Classification remains strictly separate from removal planning.

## Phase 14 — Stage 14.3: Elevated real capture + Gaming Profile 2.0 (2026-08-13)

**Real inventory capture (Part A, ADR-087).** `tools/WinForge.RealCapture` is a console CLI
(`requireAdministrator` manifest) that runs the EXACT production pipeline elevated:
`WindowsIsoInspectionService` → `ImageWorkspaceFactory` → `ImageServicingService` (export selected
index to a working WIM, source ISO read-only) → mount → `WindowsComponentIntelligenceService`
(production DISM discovery) → `ComponentMatcher` → `DeepComponentClassifier` →
`CoverageAccountingService` → `UnknownFamilyAnalyzer` (top-30) → 6 JSON exports + a stable
`real-derived-families.json` fixture under `.tmp/phase14-real/` → unmount/discard + ISO dismount +
workspace cleanup. `CoverageAccountingService` (Core) is the exact accounting engine: one exclusive
bucket per object (Curated | KnownDeep | Heuristic | Unknown), Protected as a property count
(MatcherProtected reported separately), per-source slices that reconcile, and a heuristic-excluded
knowledge ratio. No parallel fake discovery exists anywhere. The stage stays
`IMPLEMENTATION READY — REAL-DESKTOP ELEVATED VALIDATION REQUIRED` until the user runs the CLI as
Administrator and the exact real numbers are captured.

**Gaming Profile 2.0 (Part C, ADR-088/089/090).** The recommendation pipeline is
Inventory → Deep Knowledge → Profile Policy → Candidate → Safety Gate → Plan:

- `IGamingProfilePolicy` (`GamingPcPolicy`, `DedicatedGamingPolicy`) consumes
  `DeepComponentKnowledge` (Function/Risk/RecommendationKind/Protection/ProfileTag/DependencyTags)
  + selected extras → `GamingVerdict` (KeepForCompatibility / AutoRemoveCandidate /
  OptionalRemoveCandidate / NoOpinion) with deterministic reason keys.
- `ProfileSafetyGate` is the FINAL authority: Protected/Critical/High block; Moderate is
  optional-only; Low + curated knowledge may auto-recommend; heuristic never auto; unsupported and
  user-overridden items are never acted on. Blocked candidates stay visible with the gate reason.
- `GamingProfileEvaluationService` (Core, pure) runs the policy+gate and aggregates the
  user-facing summary (recommended / kept-for-compatibility / optional counts + bounded examples).
- The engine consumes ONLY post-gate decisions via `RecommendationInput.GamingDecision`
  (`RecommendationEngine` tier after requirement/dependency and extra-scenario overrides, before
  the default); user overrides (Part K) stay authoritative; `Gaming` primary = **Gaming PC**, new
  `DedicatedGaming` primary = **Dedicated Gaming** (never aliases).
- App wiring: `CustomizeStepViewModel.PushGamingContext` derives kind+extras from the selected
  profiles; `ComponentKnowledgeViewModel.GetGamingDecision` feeds each knowledge row;
  `ProfileViewModel.GamingSummaryText` renders the localized summary (8 primary profiles total).
- Extras materially influence decisions (Xbox/Game Pass, WSL/Docker, print/scan, touch/pen,
  Remote Desktop force their ecosystems to keep). No placebo tweaks (HPET/BCD/tick/memory/
  pagefile/cargo-cult, Defender/Windows Update disabling, servicing-stack removal — all forbidden).

## Phase 14 — Stage 14.3b: Real Unknown debt reduction + analyzer refinement (2026-08-14)

First elevated RealCapture run SUCCEEDED (real desktop Administrator): exact 757-object inventory,
Unknown 524, coverage 30.78% (see docs/COMPONENT-COVERAGE.md; accounting boundary = the four
supported providers only, ADR-091). Six Language capability families (337 objects) added to
`DeepComponentCatalogData` (145→177) with conservative semantics (Language/Moderate/ProfileDependent/
Sensitive); `LanguageCapabilityMetadata` parses role/locale and recognizes the image default
language (metadata only). `ComponentNormalizer` now preserves `Package_for_<sem>_<num>` CBS semantics
(dotnetrollup/kb/rollupfix). `UnknownFamilyAnalyzer.FamilyOf` keeps up to five semantic segments for
`microsoft.windows.*` dotted capabilities (dropping generic trailing role words). High-confidence
real CBS + small features classified conservatively; KNOWN != REMOVABLE; embedded lockdown/filter/UWF
features never auto-removable by Gaming. Gaming policies keep ALL language capabilities. Zero
heuristic entries added. Second elevated capture required for exact new metrics.

## Phase 14 — Stage 14.3c: FINAL high-confidence long-tail classification (2026-08-14)

The SECOND elevated RealCapture run SUCCEEDED (real desktop Administrator) and VALIDATED Stage 14.3b:
**Total 757 · Curated 32 · Protected 51 · KnownDeep 591 · Heuristic 0 · Unknown 134 · coverage 82.30%**
(AppX 22/3/10/15 · Capability 2/3/348/75 · CBS 0/41/148/1 · OptionalFeature 8/4/85/43 — exact table in
docs/COMPONENT-COVERAGE.md). Stage 14.3c then classified ONLY high-confidence long-tail families:

- **Driver capability families** (`Microsoft.Windows.Wifi.Client.*` and
  `Microsoft.Windows.Ethernet.Client.*`): vendor-family catalog records (never per driver model),
  Networking/High/RecommendedKeep/Sensitive — both Gaming profiles keep them.
- **Critical system items**: DirectXConfigurationDatabase (RuntimeDependency/Critical/RequiredKeep/
  GamingRelevant), SecHealthUi (Security/Critical/Protected), FodMetadataPackage
  (Servicing/Critical/Protected), OnecoreStorageManagement (SystemCore/High/Keep), HelloFaceCapability
  (Security/High/ProfileDependent) — none enable removal.
- **7 media codec AppX** (HEIF/HEVC/MPEG-2/RAW/VP9/WebMedia/WebP): Media/Low/ProfileDependent —
  Gaming PC never auto-strips codecs (optional-only); no removal-support expansion.
- **User-facing AppX**: Outlook + Office Hub (Low/ConsumerContent — Gaming PC auto only with supported
  AppX removal, gate blocks otherwise); Dev Home (Developer/DeveloperTool — Developer profile Keep
  override + curated catalog 22→23, Gaming optional-only); ApplicationCompatibilityEnhancements
  (SystemCore/High/RecommendedKeep, AppX + CBS).
- **Capabilities/features**: Console.Legacy, WebDriver, MathRecognizer, Wallpapers.Extended,
  App.WirelessDisplay.Connect, ClientForNFS-Infrastructure, DataCenterBridging,
  DirectoryServices-ADAM-Client, HostGuardian, LegacyComponents — conservative ProfileDependent or
  kept; HostGuardian never Low-risk auto.
- Deep catalog 177→203 (+27, zero heuristic); guard test forbids broad namespace fallback patterns.
- **1105 tests (Core 53, App 1052), 0 err/0 warn (Release, ordinary in-place)**. FINAL THIRD elevated
  capture required for exact new metrics (no asserted percentage).

## Phase 14 — CLOSEOUT: COMPLETED + MERGED (2026-08-14)

THIRD elevated RealCapture run SUCCEEDED (real desktop Administrator) — **Phase 14 ACCEPTED**.
FINAL AUTHORITATIVE EXACT numbers: **Total 757 · Curated 33 · Protected 53 · KnownDeep 645 ·
Heuristic 0 · Unknown 79 · knowledge coverage 89.56%** across the currently supported discovery
providers (AppX 47: 23/4/21/3 · Capability 425: 2/3/385/38 · CbsPackage 149: 0/42/149/0 — **CBS
149/149 = 100% known** · OptionalFeature 136: 8/4/90/38). Precise scope: the boundary is the four
supported providers; Service/ScheduledTask/Driver/Language/WinRecovery/SystemApp are NotSupported —
89.56% is never described as "89.56% of all Windows components". Real validation history: 30.78%
(201/524) → 82.30% (591/134) → **89.56% (645/79)**; the ≥60% Stage 14.2 estimate is superseded.

The remaining **79 Unknown entries are ACCEPTED as explicit technical debt** (ADR-093): CBS complete,
AppX long-tail near-complete (3 unknown), remaining Capability/OptionalFeature debt is low-frequency
long-tail mostly singletons (Quick Assist/CrossDevice, MSIX tooling, MSMQ, MultiPoint, NFS admin,
legacy IrDA/RIP, RSAT subfeatures, printing subfeatures, Recall, misc enterprise/legacy). Zero
Unknown is NOT a product requirement; no broad catch-all classifier was added (forbidden vanity
patterns remain forbidden).

**Gaming Profile 2.0 ACCEPTED**: Gaming PC (safe personal gaming optimization while retaining normal
Windows usefulness) vs Dedicated Gaming (more minimal OPTIONAL recommendations, health/
compatibility-first, not kiosk). Safety confirmed: Protected/Critical/High never auto-remove;
heuristic never auto; Known ≠ Removable; GamingRelevant ≠ SafeToRemove; dependency/extras keeps have
final authority; manual overrides authoritative; NO placebo gaming tweaks; Defender enabled; Windows
Update enabled; servicing protected; Store/Gaming Services/runtime/DirectX protected where required.

**Future work moved OUT of Phase 14** (no Stage 14.4): Service/Driver/ScheduledTask/SystemApp/
WinRecovery discovery + classification; deeper dependency resolution; destructive CBS/driver removal
execution; aggressive Lightweight/Dedicated execution; FullHealthValidated after deeper destructive
customization. Phase 14 ends here and was merged to `main` via `--no-ff` (2026-08-14); the
`phase/14-deep-component-classification` branch is retained. **1105 tests (Core 53, App 1052),
0 err/0 warn (Release, ordinary in-place, pre- and post-merge).**

## Phase 15 — Stage 15.1: Profile Execution & Safe Execution Matrix (2026-08-14)

Profiles produce clearly different, supported execution plans (ADR-094).
`ProfileExecutionMatrix` (Core, pure) maps EffectiveRecommendation + risk + protection +
confidence + execution support to an explicit disposition (AutoApply / Recommend / Optional /
Keep / Blocked / NotApplicable); AutoApply requires Low risk, non-heuristic, profile-driven,
execution-supported. `ExecutionSupportMatrix` is the auditable boundary — AppX removal,
registry policy, privacy, personalization, OptionalFeature disable supported; service config
conditional; Capability/CBS/Driver removal NOT supported (KNOWN != REMOVABLE). 
`ProfileExecutionService` runs inventory → engine (+ gaming policy verdicts, pre-gate so keeps
surface; matrix re-applies the safety gate) → `ProfileDeltaReport` → validated
`CustomizationPlan` (Phase 12 operation identity/dedup). `ProfilePlanValidator` fails safe on
remove+keep / duplicates / dependency-required / unsupported / protected. Extras materially
change plans; manual overrides authoritative; localized per-profile preview UI added
(ProfileViewModel.ProfilePreviewText + ProfileView). Deterministic six-profile comparison over
the real-derived fixture; `WinForge.RealCapture` exports `profile-plans.json`. **1150 tests
(Core 53, App 1097), 0 err/0 warn (Release, ordinary in-place).**

## Phase 15 — Stage 15.2: Unified profile candidate stream + real plan accounting (2026-08-14)

Real 25H2 profile-plans capture exposed four fixture-blind problems, all fixed (ADR-095):
`ProfileCandidateService` builds ONE unified candidate stream — inventory objects (deep →
curated → explicit exclusion bucket) + non-inventory optimization definitions — deduplicated by
canonical Phase 12-style operation identity, with exact `ProfileInventoryAccounting`
(Total = evaluated + every exclusion; 757 = 678 + 79, asserted, no double counting).
`ProfileDeltaReport.ByOperationType` now counts EXECUTABLE changes only (AutoApply+Recommend);
inventory source counts are `ProfileInventoryAccounting.BySource`; changeCount is defined once.
The registry/privacy/personalization/service layer participates in plans (Office 0→22, Balanced
3→17, Developer 6→24). Gaming vs DedicatedGaming differ on real media via `WiderMinimalSteer`
(Low cloud→auto, Moderate productivity/communication→recommend, Moderate media→optional) plus
aligned Dedicated catalog trims — real-like stream Gaming 28 vs Dedicated 30 (two policy actions).
Unsupported "optional" is Blocked. UI preview rebuilt on the SAME GenerateDelta report (one source
of truth). RealCapture exports profile-plans.json v2 (inventoryAccounting / decisionCounts /
planChanges / semanticActionKeys / keptHighlights / blockedHighlights). **1162 tests (Core 53,
App 1109), 0 err/0 warn (Release, ordinary in-place).**

## Phase 15 — Stage 15.3: Validated Profile BuildPlan as single Apply source (2026-08-15)

`BuildPlan` now constructs operations with COMPLETE execution payloads (service name + start type,
registry hive/path/value/kind/data + restore, feature/package identity; `svc:|opt:|feat:|appx:|cap:|
pkg:` conventions, SourceDefinitionIds provenance) — the real-stream fail-safe blocker was ops built
without payloads, which the validator correctly rejected (catalog data was already clean:/nActivityHistory has a valid offline policy target; service identities canonical + allowlisted). New
reusable `OptimizationDefinitionValidator` detects MissingTechnicalTarget / MissingRegistryTarget /
MissingServiceName / MissingFeatureName / UnsupportedExecution / InvalidValue /
DuplicateCanonicalIdentity (duplicate check scoped to non-mergeable identities; registry duplicates
like SpotlightFeatures/DisableSpotlight merge in the plan — Phase 12). All six primaries produce
non-null validated BuildPlans on the real-derived stream. Profile → Customize → Review → Apply uses
ONE shared CustomizationPlan; `IsAdoptEligible` requires `WasProfileDriven` (preview auto == Review
selected); manual overrides authoritative; extras affect the actual executable plan; Apply reuses the
Phase 12 executor; PlanCapture writes profile-buildplans.json (structural only). **REAL STRUCTURAL
VALIDATION PASSED (2026-08-15)** — all six primaries validationPassed == true (Balanced 16, Gaming
25, DedicatedGaming 33, Developer 21, Office 17, Lightweight 38), empty validationErrors. **1181
tests (Core 53, App 1128), 0 err/0 warn (Release, ordinary in-place).**

## Phase 15 — Stage 15.3b: Optional Feature canonical aggregation (2026-08-15)

Real structural validation exposed OptionalFeature "duplicate change plans" that were NOT true
duplicates (DedicatedGaming Containers x4, Lightweight HyperV x9, DedicatedMinimal MediaPlayer x2 +
HyperV x9): the deep catalog maps MULTIPLE genuinely distinct DISM features to ONE profile-facing
family id. `ProfileExecutionItem.ExecutableIdentity` now carries the ACTUAL DISM FeatureName while
`LogicalId` stays the semantic family; the new `ProfilePlanAggregator` (Core) merges true
same-executable candidates BEFORE final plan validation — provenance union (SourceDefinitionIds),
keep-wins precedence, AutoApply>Recommend, conflicting executable states fail explicit. The
validator's duplicate-change check keys on the EXECUTABLE identity (distinct real features stay
distinct executable operations); the remove/keep conflict check stays semantic. Count
reconciliation: deltaCount (semantic) vs buildPlanOperationCount + mergedDuplicateCount +
mergeGroups. **1192 tests (Core 53, App 1139), 0 err/0 warn.**

## Phase 15 — Stage 15.4: Real Offline Apply Validation (2026-08-15, ADR-097)

`WinForge.RealCapture --apply-profile <ProfileId>` proves profile BuildPlans EXECUTE safely against
a real mounted image (structural validation ≠ execution validation). The harness reuses the
production pipeline (inspect → export → mount → discovery → unified candidate stream → BuildPlan)
then: (1) validates the plan; (2) `ProfileApplyValidationService` (Infrastructure) pre-checks every
selected operation for deterministic already-satisfied skips, executes ONLY `SelectedOperations`
via the existing Phase 12 executor, and independently verifies every succeeded operation by
read-back; (3) `OfflineApplyVerifier` reads back AppX absence (`/Get-ProvisionedAppxPackages`),
OptionalFeature exact State (`/Get-FeatureInfo` + `DismFeatureStateParser`), the mounted SYSTEM hive
`Start` value, and the mounted registry hive (hive/path/name/kind/data; `OfflineDefaultUser` →
`Users\Default\NTUSER.DAT` — never host HKCU); (4) writes `profile-apply-validation.json` (§3
schema); (5) ALWAYS discards the workspace-owned mount (authoritative `/Get-MountedImageInfo` —
unknown mounts are never discarded) and cleans the workspace — a failed discard is a BLOCKER.
Report models live in Core (`ApplyValidationModels.cs`); ownership checks reuse
`MountIdentityValidator`; workspace ownership + read-back + report logic are covered by 22 Stage15f
tests. **1214 tests (Core 53, App 1161), 0 err/0 warn.**

## Phase 15 — Stage 15.4a: Offline registry precheck — missing key semantics (2026-08-15, ADR-097 addendum)

The first real Balanced apply proved mount/workspace/cleanup safety but exposed precheck absence
semantics: .NET 8 `RegistryKey.GetValueKind` throws `IOException` ("The specified registry key does
not exist.") — not `ArgumentException` — when a VALUE is absent from an existing key, and
`OfflineRegistryService.ReadValue` let it escape, aborting the whole profile. `ReadValue` now also
catches `IOException` around `GetValueKind` → `Exists=false` (expected absence): precheck returns
"operation required" for missing key/value/different value and `AlreadySatisfied` only for a matching
value; POST-EXECUTION missing stays `VerificationFailed` (separate semantics). Genuine infrastructure
failures (hive load / corrupt hive / access denied) still throw at `LoadHive`/`OpenSubKey` — no
weakening. The executor already creates missing subkey paths (`EnsureKeyPath`/`CreateSubKey`,
offline-hive APIs only) — unchanged. `OfflineApplyVerifier` reads `OfflineDefaultUser` from
`<mount>\Users\Default\NTUSER.DAT`, never host HKCU. `ProfileApplyValidationReport` gains
`failureStage`/`failedCanonicalKey`/`error` and `ProfileApplyValidationService` returns a structured
report on any phase failure, so `profile-apply-validation.json` survives a preflight failure and the
CLI cleanup always runs. **1225 tests (Core 53, App 1172), 0 err/0 warn.** BALANCED REAL APPLY
RETEST REQUIRED.

## Phase 15 — COMPLETE: Profile Execution & Meaningful Optimization (accepted 2026-08-15)

Phase 15 delivered the profile execution pipeline end-to-end and validated it on REAL media:
`ProfileExecutionMatrix` (AutoApply/Recommend/Optional/Keep/Blocked/NotApplicable from knowledge +
risk + protection + confidence + execution support), `ExecutionSupportMatrix` (AppX/registry/
privacy/personalization/OptionalFeature supported; Capability/CBS/Driver not), `ProfileCandidateService`
(unified inventory deep→curated→exclusion + optimization-definition candidate stream with exact
accounting), `ProfileDeltaReport`, `ProfilePlanValidator`, `ProfilePlanAggregator` (canonical
EXECUTABLE operation identity = actual DISM FeatureName; same-target candidates merge with
provenance; distinct real features stay distinct), `OptimizationDefinitionValidator`, extras
semantic overrides, manual-override authority, and the single authoritative path
Profile → Customize → Review → BuildPlan → Apply. `WinForge.RealCapture --apply-profile <Id>`
executes ONLY SelectedOperations on an isolated exported+mounted WIM and independently READ BACK
every applied change (AppX absence, exact OptionalFeature State, mounted SYSTEM hive Start, offline
registry hive kind+data, OfflineDefaultUser → `Users\Default\NTUSER.DAT`), then discards the mount
(authoritative `/Get-MountedImageInfo`; unknown mounts never discarded) and cleans the workspace.

Real validation (Win11 25H2 Pro zh-CN x64, ISO index 4): all six primaries structurally validated
(16/25/33/21/17/38, validationPassed == true, empty validationErrors); Balanced real offline Apply
10/10 executed + Verified; DedicatedGaming 20/20 executed + Verified (Recommend-only Containers/WSL
never executed — candidates ≠ selected proven); cleanup discard+workspace succeeded. Validation
level per ADR-084: real-image pipeline validation — NOT six full ISO installs, NOT VM
FullHealthValidated. MERGED TO `main` via `--no-ff`; branch `phase/15-profile-execution` retained.
**1225 tests (Core 53, App 1172), 0 err/0 warn.**

## Phase 16 — Stage 16.1: Balanced end-to-end ISO + VM Full-Health Validation prep (2026-08-15, ADR-098)

`WinForge.RealCapture --commit-profile <Id>` is the EXPLICIT commit/build mode (mutually exclusive
with the discard-only `--apply-profile`): after the same selected-only apply + read-back, the
pre-commit gate (every attempted op Verified) and a commit-mode ownership guard (session-owned
paths + the authoritative `dism /Get-MountedImageInfo` inventory — an UNKNOWN registered mount
aborts the run) gate the COMMIT. The commit + ISO build reuse the PRODUCTION `ImageBuildService`
(commit → export → media preparation → oscdimg → independent `BuildVerifier` verification →
atomic rename; `BuildOverwritePolicy.Fail`, deterministic output). The COMMITTED WIM is then
re-opened (re-mounted into a second workspace-owned mount dir) and every attempted op is
independently re-verified — the strongest persistence proof — and the ISO is structurally checked
(boot/etfsboot.com, efi/microsoft/boot/efisys.bin, sources/boot.wim, sources/install.wim,
setup.exe) with path/size/streaming-SHA-256 metadata into `profile-commit-validation.json`. The
source ISO is never modified. The in-VM `scripts/Validate-WinForgeInstallation.ps1` collects
structured `full-health-report.json` (Pass/Warning/Fail/NotTested; media/profile/windowsIdentity/
bootAndShell/devices/network/servicing/windowsUpdate/security/storeAndAppPlatform/
profileExpectedChanges; DISM CheckHealth + sfc /verifyonly non-destructive; activation REPORT
ONLY; offline-VM warnings distinct from failures); the host-side `HealthReportParser` re-aggregates
authoritatively (Fail > Warning > NotTested > Pass) and recomputes `fullHealthValidated`, so a
hand-edited or buggy script can never report a false Pass. ADR-084 levels (WorkflowValidated /
VmInstallValidated / FullHealthValidated) are documented in docs/FULL-HEALTH-VALIDATION.md —
FullHealthValidated requires installed-OS evidence: ISO generated + VM Setup booted + Windows
installed + OOBE completed + desktop reached + health report completed with no critical
servicing/security/network/shell failures. **1243 tests (Core 53, App 1190), 0 err/0 warn.**
