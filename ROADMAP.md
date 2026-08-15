# WinForge Roadmap

Phased development plan for WinForge. Each phase records its **Status**,
**Goal**, **Scope**, **Deliverables**, and **Acceptance Criteria**.

## Status vocabulary

- `NOT STARTED` — not begun
- `IN PROGRESS` — actively being worked on
- `BLOCKED` — cannot proceed (dependency / external blocker)
- `COMPLETED` — done and verified

---

## Phase 0 — Project Governance

- **Status:** COMPLETED
- **Goal:** Establish long-term project governance, development roadmap, and the
  Agent persistent memory system.
- **Scope:** Repository documentation, structure, and contributor/agent rules.
  No application functionality.
- **Deliverables:**
  - README.md (updated)
  - ROADMAP.md
  - PROJECT_STATUS.md
  - ARCHITECTURE.md
  - DECISIONS.md (ADR-001 … ADR-009)
  - AGENTS.md
  - CHANGELOG.md
  - docs/PRODUCT.md
  - docs/TESTING.md
  - docs/WINDOWS-COMPATIBILITY.md
- **Acceptance Criteria:**
  - All governance documents exist and are internally consistent.
  - AGENTS.md defines the mandatory pre-change and post-task rules.
  - ROADMAP records Phase 0 as COMPLETED and Phase 1 as NOT STARTED.
  - PROJECT_STATUS.md reflects Phase 0 completion and points to Phase 1.

---

## Phase 1 — Application Foundation

- **Status:** COMPLETED
- **Goal:** Scaffold the .NET 8 / WPF solution and base MVVM infrastructure.
- **Scope:** Solution and project creation, dependency injection, navigation
  shell, core domain interfaces, Build Plan model skeleton, logging foundation,
  unit test project.
- **Deliverables:**
  - `WinForge.App` (WPF), `WinForge.Core`, `WinForge.Infrastructure`,
    `WinForge.Core.Tests` projects
  - MVVM base (ViewModelBase, RelayCommand, navigation service)
  - Core domain interfaces and Build Plan model skeleton
  - Infrastructure project placeholder (no platform calls yet)
  - Logging abstraction in Core
- **Acceptance Criteria:**
  - Solution builds from a clean checkout.
  - `WinForge.App` launches an empty navigation shell.
  - `WinForge.Core` has no reference to App or Infrastructure.
  - `WinForge.Core.Tests` runs and is green (at least smoke tests).

- **Status:** COMPLETED (formally accepted and merged to `main`; tagged
  `v0.1.0-alpha` on 2026-08-08). Validation covered both headless automated
  tests (`WinForge.App.Tests`) and real Windows desktop GUI verification
  (user-confirmed).

---

## Phase 2 — ISO Inspection

- **Status:** COMPLETED (Step 2.1 and Step 2.2 accepted and merged to `main` on 2026-08-08; real Windows 11 25H2 zh-CN x64 Consumer `install.wim` desktop validation PASSED — Phase 2 acceptance criteria met). No Step 2.3 is defined; Phase 2 comprises only Step 2.1 and Step 2.2.
- **Goal:** Inspect an official Microsoft Windows 11 ISO non-destructively.
- **Scope:** ISO metadata reading, edition enumeration, architecture/language
  detection, install.wim / install.esd detection.
- **Deliverables:**
  - `IIsoInspectionService` (Core) + DISM/ISO-backed implementation (Infrastructure)
  - App "Image" page skeleton showing inspection results
- **Acceptance Criteria:**
  - Opening an official ISO lists editions, architecture, languages, and image
    type without modifying the source.

### Step 2.1 — Read-only layout inspection (2026-08-08)
- **Status:** COMPLETED (merged to `main` on 2026-08-08)
- Implemented: `IIsoInspectionService` + `WindowsIsoInspectionService` (Core + Infrastructure), `IIsoMountService` + `WindowsIsoMountService` (read-only `Mount-DiskImage`/`Dismount-DiskImage` via PowerShell, always dismounted), `IFilePicker`/`WindowsFilePicker`, and `ImageViewModel` `SelectIsoCommand`/`InspectIsoCommand` (async, busy/error states).
- Detection: a Windows ISO **candidate** is `\sources` + `\boot` + `install.wim`/`install.esd`. No WIM/ESD content parsing, no edition/version recognition yet.
- 15 automated tests added (9 inspection-logic via fake mount, 6 ViewModel). No DISM servicing, no registry, no ISO modification. Real Windows 11 25H2 (zh-CN, x64, Consumer ISO, install.wim) desktop mount/inspect/dismount validation completed (user-confirmed via application logs).

### Step 2.2 — Windows image metadata and edition inspection (COMPLETED)
- **Status:** COMPLETED and merged to `main` on 2026-08-08. **Real Windows desktop
  validation of the two-stage `/Get-ImageInfo` flow PASSED** (Windows 11 25H2
  Chinese Simplified x64 Consumer `install.wim`: ISO mounted, `install.wim`
  detected, `/Get-ImageInfo` enumeration of 6 indexes, per-index detail queries,
  Windows Version `10.0.26200`, Build `26200`, Architecture `x64`, Language `zh-CN`,
  localized Chinese edition names, guaranteed dismount). Both real-desktop defects
  found during validation are fixed and revalidated: (1) initial `/Get-WimInfo`
  caused DISM exit 87 → corrected to `/Get-ImageInfo`; (2) trailing DISM footer
  `The operation completed successfully.` was parsed as language `The` → fixed via
  `TryNormalizeLanguageTag` (UI now shows `zh-CN` only). Automated tests: 60/60
  (Core 6, App 54), 0 errors, 0 warnings.
- Implemented: `IWindowsImageMetadataService` + `WindowsImageMetadataResult` (Core);
  `WindowsImageMetadataService` (`dism.exe /Get-ImageInfo /ImageFile:... /English`, read-only, no
  WIM mount) + pure `DismImageInfoParser` + `IProcessRunner`/`WindowsProcessRunner`
  (Infrastructure); `WindowsIsoInspectionService` extended into a single
  mount → layout → metadata → dismount session (ADR-015 preserved, ADR-016 added);
  Image page "Windows information" + editions `ListView`; edition selection →
  `IAppState.SelectedEdition` (status only, no extraction/mount/modify).
- Reads WIM/ESD index, edition name/description, architecture, Windows version,
  build, edition ID, installation type, and languages; top-level version/build/
  architecture/languages reported only when every edition agrees (otherwise the
  UI shows "Mixed"). `DismImageInfoParser` parses the two DISM stages
  (`ParseImageList` / `ParseImageDetails`) and validates language tags with
  `TryNormalizeLanguageTag` (BCP-47-like; rejects footer prose such as
  "The operation completed successfully."). Automated tests clean (0 errors, 0
  warnings, 100% passing) using fakes plus real-footer-shape regression tests.
- Not part of Step 2.1. Step 2.1 inspects only the on-disk ISO directory
  layout (`\boot`, `\sources`, `install.wim`/`install.esd`); it does not open
  or parse the WIM/ESD content.

---

## Phase 3 — WIM Engine

- **Status:** IN PROGRESS (Step 3.1 **COMPLETED** — real-desktop validation PASSED 2026-08-09; Step 3.2 **COMPLETED** — real-desktop validation PASSED 2026-08-09 on Windows 11 25H2 zh-CN x64 Consumer `install.wim`; Step 3.3 **COMPLETED** — real-desktop validation PASSED 2026-08-09 on `feature/offline-customization` after ADR-026–ADR-031 re-run; no Step 3.4 / Phase 4 mount engine in this scope)
- **Goal:** WIM / ESD image handling via documented Microsoft mechanisms.
- **Scope:** Enumerate images, read image info, export ESD → WIM, index selection.
- **Deliverables:**
  - `IWimService` (Core) + DISM-backed implementation (Infrastructure)
- **Acceptance Criteria:**
  - Enumerate images in install.wim / install.esd.
  - Export an ESD image to WIM without data loss.

### Step 3.1 — WIM workspace & image selection foundation (2026-08-08)
- **Status:** COMPLETED (real-desktop validation PASSED 2026-08-09 on Windows 11 25H2 zh-CN x64 Consumer `install.wim`; not yet merged to `main`). WinForge.App now declares `requireAdministrator` in its embedded manifest (ADR-018) because the Phase 2 DISM enumeration returns exit code 740 when launched without elevation. Step 3.2 is IMPLEMENTED (see below).
- **Goal:** Convert the Phase 2 ISO selection + selected edition into a **durable** `ImageWorkspace` descriptor that survives ISO dismount and never persists a temporary mounted drive letter.
- **Scope (this step, read-only):**
  - Core model `ImageWorkspace` (durable fields: `SourceIsoPath`, `ImageRelativePath` e.g. `sources\install.wim`, `ImageType`, `SelectedIndex`, `SelectedEditionName`, `Architecture`, `Version`, `Build`, `Languages`) plus `ImageWorkspaceStatus` (`NotReady` / `Ready` / `Invalid`).
  - `IImageWorkspaceFactory` (Core) + `ImageWorkspaceFactory` (Infrastructure) — builds a `ImageWorkspaceBuildResult` from `IsoInspectionResult` + `WindowsEditionInfo` with validation rules (missing ISO path / unknown image type / failed metadata / no selected edition / selected index not present in inspected editions → NotReady or Invalid). The relative path is derived (`sources\install.wim` / `sources\install.esd`), never copied from a temp mount root.
  - `IWimService` (Core) + `WimService` (Infrastructure) — read-only Step 3.1 responsibilities only: `ValidateWorkspace(ImageWorkspace)` and `ResolveSelectedImage(ImageWorkspace)` (returns a `SelectedImageContext` carrying the durable source identifiers for a future Phase 3 op to acquire its own temporary source-access session). No DISM export/mount/apply/capture; no image modification.
  - `IAppState.CurrentImageWorkspace` — the application maintains a durable selected-image workspace; selecting an edition (or changing it) creates/updates it, and selecting a new ISO resets it before the new ISO is ready (no stale indexes from a previous ISO).
  - Image page "Selected image / Workspace" section bound to the durable descriptor (Edition, Index, Image, Architecture, Build, Status, original ISO source). No temporary `G:\` path is ever shown; no export/mount/build buttons.
  - ADR-017 records that durable descriptors store ISO path + relative install-image path + selected index and never persist temporary mounted drive letters.
- **Out of scope this step:** ESD → WIM export (Step 3.2), WIM mount (Phase 4), any image modification. The Phase 2 mount → inspect → metadata → dismount session (ADR-015) is unchanged and remains strictly read-only.

### Step 3.2 — Offline WIM servicing lifecycle (2026-08-09)
- **Status:** **COMPLETED** — real-desktop validation PASSED 2026-08-09 on Windows 11 25H2 (Chinese Simplified, x64, Consumer Editions, `install.wim`): source index 4 → isolated working WIM index 1 export succeeded; isolated working-image strategy validated; mount verified against real Windows filesystem contents; unmount/discard verified; `dism /Get-MountedWimInfo` reported no mounted images afterward; remount lifecycle passed; an active mount cannot be silently orphaned (ISO re-inspection / edition re-selection refused while Mounted); original ISO / `install.wim` / `install.esd` never modified. 133 automated tests pass (Core 21, App 112), 0 errors, 0 warnings. Merged to `main` via `--no-ff`.
- **Goal:** Prepare an isolated, WinForge-owned working image from the selected source edition, mount it for later customization phases, discard an unmount, and validate/recover a servicing session — all without ever modifying the original ISO or its `install.wim`/`install.esd`.
- **Scope (this step):**
  - Core model `ImageServicingWorkspace` (durable: `SourceIsoPath`, `SourceImageRelativePath`, `SourceImageType`, `SelectedIndex`, `SelectedEditionName`, `Architecture`, `Build`, WinForge-owned `WorkingDirectory`/`WorkingImagePath`/`MountDirectory`, `WorkingImageType` — always WIM, `WorkingIndex` — always 1, `State`, `CreatedAt`, `LastError`). A selected source index N maps to a standalone working image whose own index is 1.
  - `ServicingWorkspaceState` state machine (`NotPrepared`/`Preparing`/`Prepared`/`Mounting`/`Mounted`/`Unmounting`/`Completed`/`Failed`) + `ServicingHealth` (`Prepared`/`Mounted`/`Stale`/`Failed`/`Invalid`) + `ServicingResult`.
  - `IImageServicingService` (Core) + `ImageServicingService` (Infrastructure) — DISM `/Export-Image` (source index N → standalone WIM index 1), `/Mount-Image` (working image only), `/Unmount-Image /Discard`, and `/Get-MountedImageInfo` registration verification. The source install image is read from a transient read-only ISO mount that is always released.
  - `IWorkspacePathProvider` (`%LOCALAPPDATA%\WinForge\Workspaces\<id>\image` + `mount`) addressed by a safe id segment; `IWorkspaceSafeDelete` proves a target is strictly inside the workspace before any deletion (refuses drive/profile/repo roots).
  - `IAppState.CurrentServicingWorkspace` + `ImageViewModel` prepare/mount/unmount commands with state-aware `Can*` guards; an active mount REFUSES ISO re-inspection and edition re-selection (explanatory `BlockedMessage`). New Image page "Working image" section shows status/source edition/index/working image/working dir/mount dir/error.
  - Post-export validation uses the per-index `/Get-ImageInfo /Index:1` detail query (the index-less enumeration query does not report Architecture/Build).
- **Out of scope this step:** Any image customization (package/component/Appx/registry tweaks), ISO build/rebuild, `boot.wim` servicing, commit-on-unmount (unmount always discards), and Phase 4 mount-engine work. The working image is prepared solely so Phase 4+ can mount and customize it. Step 3.3 = **NOT STARTED**.

### Step 3.3 — Offline customization plan & execution engine (2026-08-09)
- **Status:** **COMPLETED** — real-desktop validation PASSED 2026-08-09 on `feature/offline-customization` (sections A–Y complete; 259 automated tests pass — Core 37, App 222, 0 errors, 0 warnings, Release). Re-run after ADR-026–ADR-031 confirmed: 47 Appx discovered with exact `PackageName`; 699 services with only DiagTrack/WerSvc/PcaSvc configurable; non-allowlisted packages non-selectable; a 3-op plan (remove `Microsoft.BingWeather`, disable `DiagTrack`, advertising-ID off) applied → 3 succeeded / 0 failed with independent DISM/SYSTEM/SOFTWARE hive verification; original ISO untouched. First real-desktop validation exposed three defects (Appx = 0, services = 0, non-allowlisted package selectable), all fixed (ADR-026/ADR-027). A **re-investigation** of the services=0 defect (real-desktop evidence proved the SYSTEM hive file exists and is readable, so the cause was not a missing/wrong path) hardened the load/enumeration path and added diagnostics (ADR-028). A **second re-investigation** of the Appx=0 defect (independently reproduced: `dism /English /Get-ProvisionedAppxPackages` succeeds and returns many packages, yet WinForge reported 0) confirmed the live parser already accepts the real single-word `PackageName` header, but the doc comment / fixtures / historical ADR text still referenced the invented "Deployment package name"; the removal identity is the exact `PackageName` (full name_version_arch_~_publisher-hash), `DisplayName` is display-only, and the four-way outcome contract (found / genuine-zero / command-failure / unrecognized) is now explicit (ADR-029). A **third re-validation** confirmed the GOOD results (47 Appx, 149 packages, 699 services discovered; non-approved packages correctly disabled) but exposed a NEW safety defect: the Components page exposed a disableable checkbox for **every one of the 699 discovered service records** — including kernel/file-system drivers and performance/provider entries. ADR-030 introduces the service-inventory safety boundary: only the trusted allowlist (`DiagTrack`/`WerSvc`/`PcaSvc`) is configurable; the ~699 driver/protected records are classified `Protected` and hidden/non-selectable by default in the UI. A **fourth real-desktop defect** (a 3-op plan reported "3 succeeded" but the advertising-ID value was absent from the offline `SOFTWARE` hive): the trusted `privacy.advertising-id` definition targeted the wrong key (`Advertising\Id` instead of `AdvertisingInfo`), and `ApplyRegistry` reported success on no-throw with no read-back verification. ADR-031 fixes the definition, adds an independent read-back verification contract (existence + type + value; delete verifies absence), and a `NormalizeKeyPath` guard against a duplicated hive-base prefix. All of this was done **without** merging to `main` or marking validation passed. **PENDING real-desktop validation — RE-RUN required after the fixes** (no real Windows mount/registry/package mutation has yet been confirmed green). Not merged to `main`; no new tag.
- **Goal:** Let the user declaratively choose, validate, and apply a curated set of safe offline customizations to the isolated working image produced by Step 3.2 — without ever touching the host OS, the source ISO, or arbitrary packages/registry/commands.
- **Scope (this step):**
  - **Core declarative model (platform-agnostic, no DISM/Win32):** `CustomizationPlan` (lifecycle `Draft` → `Validated` → `Executing` → `Completed`/`CompletedWithErrors`/`Failed`/`Cancelled`), `CustomizationOperation` (OperationType, TargetIdentifier, registry/service targets, `RiskClass`, `ExecutionOrder`, `ValidationResult`), `CustomizationCategory`/`CustomizationOperationType`/`RiskClass`/`ServiceStartType` enums, `CustomizationResult` (computed `Success = FailedOperations == 0`, `TotalOperations`/`Succeeded`/`FailedOperations`/`CriticalFailure`/`Summary`), and `DiscoveryInventory` (Appx, packages, offline services, trusted privacy/system definitions). `Validate()` surfaces Duplicate/Conflict/Unsupported/MissingTarget issues; `FreezeForExecution()` snapshots selected ops and locks the live plan (ADR-020).
  - **Discovery services:** exact-identity provisioned-Appx enumeration (`DismAppxParser`, matches the real DISM single-word `PackageName`/`DisplayName` headers and tolerates the legacy spaced forms), package enumeration with allowlist-gated `Removable` classification driven by the single `PackageRemovalPolicy` (`DismPackageParser`), offline registry via Win32 `RegLoadKey`/`RegUnLoadKey` under a WinForge-owned hive name (`OfflineRegistryService`, always unloaded in `finally`, **and now enables `SeRestorePrivilege`/`SeBackupPrivilege` so hive load succeeds on a real elevated session**, ADR-023), service enumeration from the mounted image's `SYSTEM` hive, and a trusted `CustomizationDefinitionProvider` (5 Privacy + 3 System registry settings, 3 recommended service-start changes: DiagTrack/WerSvc/PcaSvc → Disabled). Discovery refuses to run when the mount session does not match (ADR-024). `DiscoveryInventory` now reports per-source success/failure status so a DISM/registry failure is surfaced, never silently zero (ADR-026). **ADR-028:** `OfflineRegistryService` now logs the full load/unload lifecycle (redacted hive path, WinForge-owned temp HKLM name, `SeRestore`/`SeBackup` enablement incl. `ERROR_NOT_ALL_ASSIGNED`, `RegLoadKey`/`RegUnLoadKey` return codes, resolved ControlSet, service count) and an **empty `ControlSet00x\Services` enumeration is treated as `ServiceStatus = Failed`** — never a misleading "0 services" success — closing the last silent-zero path.
  - **Execution orchestrator (`WindowsCustomizationExecutionService`):** pre-execution critical-stop guard (Mounted + `MatchesSession` + DISM-registered + `Validated` → else `CriticalFailure`); frozen-snapshot execution in defined order (registry 0 → services 1 → appx 2 → packages 3 → files 4 → scheduled tasks 5); per-operation status; package-removal allowlist (non-allowlisted `Removable` packages are `Skipped`, never removed, ADR-022); missing service → `Skipped`; **leaves the image mounted** (no commit/unmount, ADR-025).
  - **UI (Components / Privacy / System / Plan pages):** each page is backed by discovery data; selections add/remove declarative plan operations (`PlanSync`); explicit `Validate` then `Apply` flow; `PlanReviewViewModel` shows op totals, warnings, progress, and a result summary. Navigation wired via a new `Plan` `PageKey` and four `DataTemplate`s in `App.xaml`.
  - **Safety guards (defense-in-depth, ADR-021/ADR-024):** only the mounted working image is targeted; `MountIdentityValidator.IsWithinMount` confines paths; no host path / source-ISO root / arbitrary command / registry / filesystem delete is ever issued; execution verifies mount identity and refuses session mismatch; `AsyncRelayCommand.CanExecuteChanged` is raised after state changes (the Step 3.2 real-desktop defect pattern is avoided in the new command paths).
  - **Tests (259 total, CI-safe — no ISO / admin / internet):** DismAppxParser (11), discovery service (12, incl. service-inventory classification), execution service (13, incl. guard/allowlist/skip/hive-never-touched), safety (16: registry safety, mount validator, definition provider), service-inventory safety (12, ADR-030), offline-registry contract (17, ADR-031), view models (11), Core model lifecycle/validation (16), plus the updated headless boot test. xUnit 2.5.3 with fakes for `IProcessRunner`/registry/definition/mount-identity/discovery/execution.
- **Out of scope this step:** Any host-OS mutation, source-ISO modification, arbitrary package/registry deletion, image commit/unmount (owned by Step 3.2), ISO rebuild (Phase 10), and any new Phase 4 mount-engine work. Reverting applied customizations is done by discarding the working image (Step 3.2), not by an in-engine undo.

---

## Phase 3.5 — UX Workflow Refactor + Localization Foundation

- **Status:** **COMPLETED** — real-desktop validation PASSED 2026-08-09 on Windows 11 25H2 (Chinese
  Simplified, x64, Consumer Editions, `install.wim`); merged to `main` via `--no-ff`. Built on top of
  the completed Step 3.3 customization engine (no engine changes — workflow code contains no DISM).
- **Goal:** Provide a guided, gated primary workflow (Wizard/Stepper) over Step 3.3, plus an
  English / Simplified-Chinese localization foundation with runtime switching and persistence.
- **Scope:**
  - Sequential 6-step Stepper replacing the left feature-list nav: Source / Prepare / Customize /
    Review / Apply / Build (zh: 选择镜像 / 准备镜像 / 自定义 / 审核计划 / 应用修改 / 构建镜像).
  - Step states (NotAvailable / Available / Current / Completed / RequiresAttention) computed purely
    from `IAppState`; gated Back/Next + direct-step skip-guard; source-change / dirty / mounted /
    executing safety (ADR-032).
  - Utility rail (Home / Logs / Settings / About) separate from the workflow; legacy deep-links
    translated onto the matching step (ADR-033).
  - Localization: neutral `Strings.resx` + `Strings.zh-CN.resx` satellite; `ResourceManagerLocalizationService`
    exposed as `Loc`; `LocKeyMultiConverter` (re-evaluates on key + culture); `ILocalizationService`
    in Core; runtime `SetCulture` with `ILanguageSettingsStore` persistence and English fallback
    (ADR-034, ADR-035).
  - Friendly metadata: `FriendlyMetadataProvider` + `ISelectableItem` show localized names but always
    preserve the immutable technical id; `ServiceConfigPolicy` still gates configurable services
    (ADR-036).
  - `ComponentsViewModel` selection→plan resync re-entrancy guard (ADR-037).
- **Deliverables:**
  - `WinForge.App/Workflow/` (`WorkflowViewModel`, `WorkflowStep`, `WorkflowStepState`,
    `WorkflowStepViewModel`, `IWorkflowNavigator`).
  - `WinForge.App/Localization/` (`ResourceManagerLocalizationService`, `LocalizationBootstrap`,
    `InMemoryLanguageSettingsStore`, `FileLanguageSettingsStore`); `WinForge.Core/Services/`
    (`ILocalizationService`, `ILanguageSettingsStore`).
  - `WinForge.App/FriendlyMetadata/` (`FriendlyMetadataProvider`, `IFriendlyMetadataProvider`).
  - `WinForge.App/Resources/` (`Strings.resx`, `Strings.zh-CN.resx`),
    `WinForge.App/Converters/WorkflowConverters.cs`.
  - Wizard Views: `WizardView`, `SourceView`, `PrepareView`, `CustomizeView`,
    `ComponentListTabView`, `ApplyView`, `BuildView`; `AboutView`, `SettingsView`;
    `WizardStepTemplateSelector`.
  - Step VMs: `CustomizeStepViewModel`, `BuildStepViewModel`; `SettingsViewModel`, `AboutViewModel`.
  - Customize tabs: Apps / Windows Components / Services / Privacy / System / Experience.
- **Acceptance Criteria:**
  - The app opens into the 6-step Wizard; Review / Apply / Build are unreachable until prerequisites
    are met; Customize is unreachable until the image is mounted.
  - Switching language in Settings applies instantly and persists across launches; zh-CN strings render.
  - Friendly service/app labels show the canonical technical id; unapproved services remain
    non-selectable.
  -   `dotnet build` / `dotnet test -c Release` clean: **362 pass (Core 37, App 325), 0 errors,
    0 warnings**.
- **Out of scope:** Any ISO rebuild in Build (honest placeholder — Build/ISO export is the next
  development phase, **NOT STARTED**); new Phase 4 mount-engine work; new customization categories
  beyond what Step 3.3 shipped.

---

## Phase 4 — Mount Engine

- **Status:** NOT STARTED
- **Goal:** Safe mount and unmount of WIM/ESD images.
- **Scope:** Mount to directory, commit/discard, reference counting, crash-safe
  cleanup.
- **Deliverables:**
  - `IMountService` (Core) + DISM-backed implementation (Infrastructure)
- **Acceptance Criteria:**
  - Mount an image to a directory; unmount with commit and discard.
  - No orphaned mounts after process crash / forced exit.

---

## Phase 5 — Configuration Engine

- **Status:** NOT STARTED
- **Goal:** Build Plan configuration model covering all customization areas.
- **Scope:** Components, experience, privacy, OOBE, hardware requirements;
  validation; serialization; preset loading.
- **Deliverables:**
  - `BuildPlan` model and validators (Core)
  - Preset (configuration data) model
- **Acceptance Criteria:**
  - A Build Plan validates against schema and serializes to/from disk.
  - Presets are pure configuration data, not separate execution paths.

---

## Phase 6 — Application Debloat

- **Status:** NOT STARTED
- **Goal:** Remove built-in applications per the Build Plan.
- **Scope:** Provisioned/AppX package removal via documented DISM/PowerShell;
  allow/block lists driven by configuration.
- **Deliverables:**
  - `IDebloatService` (Core) + Infrastructure implementation
- **Acceptance Criteria:**
  - Removes packages specified by the plan; reversible/auditable via preset.

---

## Phase 7 — Windows Experience

- **Status:** NOT STARTED
- **Goal:** Windows experience customization, including the "Windows 10 Inspired"
  experience.
- **Scope:** Native policies/settings appliers for taskbar, Start, explorer, etc.
- **Deliverables:**
  - `IExperienceService` (Core) + Infrastructure appliers
- **Acceptance Criteria:**
  - Applies native Windows settings per plan.
  - "Windows 10 Inspired" uses only native policies; never silently bundles
    third-party shell-replacement software.

---

## Phase 8 — Privacy

- **Status:** NOT STARTED
- **Goal:** Privacy configuration.
- **Scope:** Telemetry, diagnostics, advertising, location, etc. via documented
  policies.
- **Deliverables:**
  - `IPrivacyService` (Core) + Infrastructure appliers
- **Acceptance Criteria:**
  - Applies privacy settings per preset; changes are documented in logs.

---

## Phase 9 — System Tweaks

- **Status:** NOT STARTED
- **Goal:** Hardware requirement configuration and system tweaks.
- **Scope:** TPM / Secure Boot / RAM / CPU requirement options, safe defaults,
  optional performance tweaks.
- **Deliverables:**
  - `ISystemTweakService` (Core) + Infrastructure appliers
- **Acceptance Criteria:**
  - Hardware-requirement options are configurable and off by default where they
    reduce safety.

---

## Phase 10 — Build Engine

- **Status:** **COMPLETED** — real-desktop validation PASSED 2026-08-10 on Windows 11 25H2 (Chinese
  Simplified, x64, Consumer Editions, `install.wim`); merged to `main` via `--no-ff` on 2026-08-10. Replaces the
  honest placeholder Build step (ADR-032) with a real, safe ISO-rebuild pipeline. Key safety/UX
  properties delivered: resumable post-commit build checkpoint (skip Commit/Export when the durable
  `install.wim` already exists; the committed/exported artifact is retained on a post-commit failure
  so the next run resumes without re-Apply); destination-only ReadOnly/System/Hidden attribute
  normalization (the build copy clears those attributes, never the source) with deterministic
  media-tree cleanup; automatic Commit + unmount semantics (`/Unmount-Image /Commit`, then the image
  is gone); and a completion-gated final-step **Finish → Home** navigation that preserves the ISO
  (ADR-044). Phase 10 added ≈48 automated tests (orchestrator + component unit + ViewModel +
  shell-level navigation integration); total suite **440 pass (Core 37, App 403), 0 errors, 0
  warnings (Release)**, all CI-safe (no ISO / admin / internet).
- **Goal:** Rebuild a customized Windows ISO from the isolated, customized working image.
- **Scope:** Commit the working image, export a clean install.wim, copy the original media tree and
  replace the payload, build a dual-boot (BIOS+UEFI) ISO with oscdimg, verify independently, and
  surface the full lifecycle in the Build UI. The source ISO is never modified.
- **Deliverables:**
  - `IBuildService` (Core) + `ImageBuildService` (Infrastructure) — 6-phase state machine
    (Preflight → CommittingImage → ExportingImage → PreparingMedia → BuildingIso → Verifying →
    Completed/Failed/Cancelled), writing an atomic `build.recovery.json` for crash recovery.
  - `IImageServicingService.CommitUnmountAsync` (DISM `/Unmount-Image /Commit`) — the build commits
    the working image; `/Discard` is never used on the build path (ADR-039).
  - `IWimExporter` + `DismWimExporter` (DISM `/Export-Image`, `/Compress:max /CheckIntegrity`) —
    clean install.wim; ESD sources normalized to a WIM at index 1 (ADR-040).
  - `IIsoMediaPreparer` + `IsoMediaPreparer` — read-only copy of the original media tree; replaces
    `sources\install.wim` (WIM) or deletes `sources\install.esd` and writes `sources\install.wim`
    (ESD); validates the dual-boot files exist (ADR-040).
  - `IBootableIsoBuilder` + `OscdimgIsoBuilder` + `OscdimgArgumentBuilder` — Windows ADK `oscdimg.exe`,
    dual-boot args `-bootdata:2#p0,e,b"<etfsboot.com>"#pEF,e,b"<efisys.bin>" -m -o -u2 -udfver102`;
    fails fast and clearly when the ADK is missing or a boot file is absent — never fakes an ISO
    (ADR-041).
  - `IBuildVerifier` + `BuildVerifier` — independent re-check (output exists + size, install.wim
    present, no mounted WIM, expected edition/index present); a failed verification makes the build
    fail (ADR-043).
  - `IAdkToolLocator` + `FakeAdkToolLocator`/`MissingAdkToolLocator` — ADK detection; UI surfaces
    `AdkMissing` before build.
  - `BuildStepViewModel` (App) — gated (`CanBuild` requires Applied + Mounted + ADK + non-empty
    paths), default file name `WinForge_<Edition>_<yyyyMMdd-HHmm>.iso` (spaces→`_`), explicit
    overwrite policy (default `GenerateUniqueName`), cancellable command, terminal state + log +
    output path/size surfaced from `BuildResult`; success transitions the workspace Mounted→Prepared
    (ADR-042).
  - Core request/result DTOs: `BuildRequest`, `BuildResult`, `BuildState`, `BuildProgress`,
    `BuildFileName`, `WimExport*`, `MediaPrepare*`, `IsoBuild*`, `BuildVerification*`,
    `BuildRecoveryState`.
- **Acceptance Criteria:**
  - Produces a bootable ISO whose structure matches the source except for the intended
    customizations.
  - The original source ISO / `install.wim` / `install.esd` is never modified.
  - Commit failure stops the build (no ISO, workspace recoverable).
  - Missing ADK or missing boot files fail fast and clearly; no fake ISO.
  - ESD and WIM sources both yield a WIM payload at index 1.
  - A crashed build is detected and cleaned before the next run.
  - **Final-step UX:** when Build is the current final step, a completed build shows a localized,
    completion-gated **Finish** (enabled on `BuildState == Completed`, hidden otherwise); Failed /
    Cancelled builds stay on Build and never present a successful Finish; Finish navigates Workflow →
    Home, preserves the generated ISO and logs, and never calls `Application.Shutdown()` (ADR-044).
  - **Resumable post-commit checkpoint:** a failure after Commit/Export retains the durable
    `install.wim` and the export artifact so the next run resumes without re-Apply; only the dirty
    media tree + partial output are discarded.
  - **Destination-only attribute normalization:** the build copy clears ReadOnly/System/Hidden on
    files it writes (notably `autorun.inf`) and cleans the media tree deterministically, while the
    source media is untouched.
- **Real-desktop validation (PASSED, 2026-08-10):** run on Windows 11 25H2 (Chinese Simplified, x64,
  Consumer Editions, `install.wim`): (1) Build entered correctly after Apply; (2) the working image
  was committed and the WIM automatically unmounted; (3) a clean `install.wim` was exported; (4)
  `PreparingMedia` copied the original media tree and replaced the payload; (5) the ReadOnly/System/
  Hidden `autorun.inf` defect was fixed (no `UnauthorizedAccessException`); (6) `oscdimg.exe` built a
  dual-boot ISO; (7) `BuildVerifier` confirmed the output ISO + `sources\install.wim` + expected
  edition/index and that no WIM remained mounted; (8) the build reached 100% / Completed; (9) the
  Finish button was visible + enabled and **navigated Workflow → Home**; (10) the application
  remained running, the generated ISO stayed intact, no extra manual dismount was required, and no
  stale mounted WIM remained. Phase 10 is therefore marked **COMPLETED**.

---

## Phase 11 — Component Intelligence Foundation

- **Status:** COMPLETED — **REAL DESKTOP VALIDATION PASSED** (2026-08-12) — **MERGED TO `main`** via `--no-ff` (merge commit recorded in PROJECT_STATUS; branch `phase/11-component-intelligence` kept until merge/push verified). Stage 11.1/11.2/11.3/11.4 all REAL DESKTOP VALIDATED. Non-blocking follow-up (ADR-061): allow Extra Scenarios in Custom mode as keep/recommendation hints without a primary preset.
- **Goal:** Teach WinForge to *explain* Windows components to ordinary users — WHAT a
  component is, WHETHER they need it, WHAT breaks if removed, HOW risky, and whether it is
  restorable — without ever offering a destructive removal in Stage 11.1. Separate the
  DISCOVERED WINDOWS OBJECT (raw DISM identity) from the COMPONENT DEFINITION (human knowledge).
- **Scope (Stage 11.1 — Full Offline Component Inventory + User Decision Model):**
  - Core `ComponentMatcher` (pure classification, no DISM): maps raw items onto a curated
    catalog, classifies the rest, collapses multi-target components, surfaces catalog-only rows.
  - Four-way classification: `Curated` / `DiscoveredUnclassified` / `Protected` / `Unsupported`.
  - Read-only DISM discovery (AppX / Capabilities / Optional Features / CBS packages); six
    further categories (Service / ScheduledTask / Driver / Language / WinRecovery / SystemApp)
    designed but not yet implemented (reported `NotSupported`).
  - Curated catalog of well-understood inbox components (generated by `gen_catalog.py`);
    `Unknown` preferred over invented (localized `Component.Unknown`).
  - App: `ComponentIntelligenceViewModel` (Standard = curated only, Advanced = also raw),
    `ComponentListItem`, `ComponentIntelligenceView` detail prototype; additive rail entry.
  - **No destructive removal, no DISM write** in this stage.
- **Deliverables (Stage 11.1):** `ComponentMatcher` + `ComponentInventory`/`ComponentDefinition`
  models + 4 parsers + `WindowsComponentIntelligenceService` + `CuratedComponentCatalog` +
  `ComponentIntelligenceViewModel`/`ComponentListItem`/`ComponentIntelligenceView` + color
  converters + localization keys + STA XAML regression. **491 tests pass (Core 53, App 438), 0 errors/warnings.**
- **Acceptance Criteria (Stage 11.1):** Ordinary-user view shows human name + short description +
  recommendation + risk + scenarios + keep-if/remove-if/impact + restoration + collapsed technical
  details; missing knowledge visibly says "Unknown / 尚未确认"; Standard mode hides raw objects;
  no image mutation; Phase-10 behavior unchanged; build 0/0; all tests green.
- **Deliverables (Stage 11.2 — Component Knowledge Import + Catalog Expansion + Customize
  Integration):** knowledge-provenance model (`KnowledgeSource`/`KnowledgeClaim`/`ScenarioRecommendation`,
  `KnowledgeClaimKind.Fact` vs `Recommendation` deliberately separated so a community removal script
  never becomes WinForge "RecommendedRemove"); offline import pipeline (`KnowledgeImportPipeline` +
  `IKnowledgeSourceAdapter`s MicrosoftOfficial / WindowsImageDiscovery / Win11DebloatCommunity /
  WinForgeCurated — candidates never auto-promote to Curated, community `CommunityProposal` never
  promotes to `EffectiveRecommendation`); catalog expansion **11 → 22** well-understood components
  (`CuratedComponentCatalog`, regenerated by `.tmp/phase11/gen_catalog.py` — AV1/AVC video extensions,
  Bing News/Search, Calculator, Notepad, Paint, Terminal, To Do, Quick Assist, Desktop App Installer,
  and an "Xbox / Gaming" grouping of 9 AppX identities with a Gaming→UsuallyKeep scenario override;
  each carries provenance claims); and the Customize **Apps tab** — the knowledge-backed decision
  surface — which **repurposes** the `ComponentKnowledgeViewModel`/`ComponentKnowledgeItem`/
  `ComponentKnowledgeView` engine (passed as the tab `Content`; App.xaml's implicit `DataType`
  DataTemplate renders it, no duplicate View/ViewModel) and reuses `ComponentIntelligenceViewModel`'s
  classified inventory: human 名称/作用/建议/风险 badges, decision-oriented default sort, filters,
  compact hover quick card, and **direct master–detail row selection** (ADR-050: click any row →
  open/switch the right-side detail panel; the per-row 详情 button is removed; the checkbox only
  toggles plan inclusion, so inspection and removal stay independent), conservative Protected/Unknown
  UX with explicit block reasons, official-vs-community evidence, deterministic "why" captions,
  **no automatic destructive selection**. The former separate "Component Knowledge" tab is **removed**
  (ADR-048). The left-rail 组件智能/Component Intelligence page is repositioned as the advanced
  **高级组件检查器 / Component Inspector** inspection surface (raw identities shown only there / in
  detail / Advanced). **556 automated tests pass (Core 53, App 503), 0 errors, 0 warnings (Release).**
  ADR-045/ADR-046/ADR-047/ADR-048/ADR-049/ADR-050.
- **Acceptance Criteria (Stage 11.2):** catalog regenerated with no duplicate resx keys (idempotent
  generator, exactly 284 keys); knowledge provenance never elevates community opinion to
  RecommendedRemove; the Customize Apps tab (knowledge-backed decision surface) renders with no
  XAML/binding crash; default sort places RecommendedRemove first then by risk/name; Standard mode
  hides raw Windows package identity from the row/hover card (shown only in detail / Advanced / CI
  page); clicking a row opens/switches the detail panel without changing the plan; no automatic
  destructive selection on load; build 0/0; all tests green.
- **Next (Stage 11.2 — PENDING REAL DESKTOP REVIEW):** Re-run the real Windows 11 25H2 zh-CN x64
  Consumer enumeration and a real-desktop validation pass of the Customize Apps tab (knowledge-backed
  decision surface) — render (选择 | 名称 | 作用 | 建议 | 风险), click any row → open/switch the
  detail panel, hover quick card, detail (keyboard/touch accessible, no selection change), sort/filter,
  official-vs-community evidence,
  raw identity hidden in Standard mode, no automatic destructive selection — before marking Phase 11
  COMPLETE. Raw Windows identities remain
  discovered independently from curated WinForge logical components; **Unknown stays Unknown until
  evidence-backed**. **Do NOT start with deep CBS removal. Do NOT expose Protected items for removal.
  Do NOT infer dependencies without evidence.** After Stage 11.2 real-desktop validation passes,
  consider Stage 11.3 (proposed): wire the Apps knowledge surface into the actual CustomizationPlan
  selection model so a user's keep/remove decision from the knowledge surface flows into the plan with
  explicit, provenance-backed confirmation — see DECISIONS ADR-047/ADR-048. Phase 11 remains IN
  PROGRESS; NOT merged to `main`.
- **Deliverables (Stage 11.3 — Customize Coverage Expansion + Personalization Activation +
  Optimization Knowledge Matrix, ADR-051/ADR-052/ADR-053/ADR-054 — REAL DESKTOP VALIDATED 2026-08-12,
  incl. ADR-055 defect fix + ADR-056 OpenSSH capability correction):** coverage-first — the coverage
  matrix `.tmp/phase11/stage11.3-coverage-matrix.md` records every candidate (Id / tab / name /
  mechanism / target / applicability / provenance / reversibility / recommendation / risk /
  compatibility / status Implemented/Deferred/Rejected/Unsupported). Operation taxonomy
  (`OptimizationAction`/`OptimizationMechanism`/`OptimizationScope` carried as data on
  `CustomizationOperation`; new `DisableOptionalFeature` + `RemoveCapability` operation types with a
  `FeatureConfigPolicy` allowlist; `CustomizationCategory.Personalization`). Offline registry /
  Default-User targeting (`OfflineHivePaths.DEFAULT_USER` → `<mount>\Users\Default\NTUSER.DAT`;
  host HKCU never touched). Knowledge reuse: the Windows Components tab reuses the Stage 11.2
  `ComponentKnowledgeViewModel` engine (capability/optional-feature category filter); Services /
  Privacy / System / Personalization share ONE `OptimizationKnowledgeViewModel` engine over the
  generated `OptimizationCatalog`. First tranche implemented counts: Windows Components **12**,
  Services **12** (11 reviewed + 1 core informational), Privacy **11**, System **10**,
  Personalization **14** (Coming Soon removed; Start/Search + Taskbar + Explorer + Lock screen/Desktop
  + Appearance groups). Review lists every selected change with its exact action type + scope + revert
  contract. **591 automated tests pass (Core 53, App 538), 0 errors, 0 warnings (Release).**
  ADR-051/ADR-052/ADR-053/ADR-054/ADR-055 (real-desktop defect fix: the unified Discover now
  refreshes BOTH the Apps and Windows Components knowledge tabs from one DISM pass; execution
  eligibility is separated from display eligibility so capability / not-yet-allowlisted rows stay
  visible with a disabled checkbox and "当前版本暂不支持应用"; OpenSSH Client/Server are modeled as
  CAPABILITIES `OpenSSH.Client~~~~0.0.1.0` / `OpenSSH.Server~~~~0.0.1.0` per Microsoft docs, never as
  OptionalFeature FeatureNames).
- **Next (Stage 11.4 — IMPLEMENTED / PENDING REAL DESKTOP REVIEW):** real Windows 11 25H2 zh-CN x64
  Consumer validation of the recommended configuration engine. Stage 11.3 is **REAL DESKTOP VALIDATED**
  (2026-08-12). Stage 11.4 (ADR-057..060): the profile selector at the top of Customize ("What kind of
  Windows are you building?"), 7 reviewed profiles (Balanced/Gaming/Developer/Office/Lightweight/
  DedicatedMinimal/Custom; multi-select), the pure `RecommendationEngine` with documented precedence
  (safety > user override > required dependency > profile requirement > scenario override > component
  default), visible conflict resolution (KEEP wins with a reason), category-aware captions, the
  non-destructive 查看推荐方案 preview, and 采用推荐选择 limited to present + low-risk +
  apply-supported + conflict-free + non-overridden items. **637 automated tests pass (Core 53,
  App 584), 0 errors, 0 warnings (Release).** Do NOT mark Phase 11 complete and do NOT start Profiles
  beyond this stage until the expanded Customize coverage and the profile engine are reviewed and
  accepted on the real image (Part S: Balanced/Gaming/Developer/Lightweight; record recommendation
  changes, counts, keeps/trims, conflicts; adopt → only eligible items selected; manual override
  survives profile change; Review reflects final selections; no Apply/build during first pass).

---

## Phase 15 — Profile Execution & Meaningful Optimization

- **Status:** IN PROGRESS — **STAGES 15.1 + 15.2 + 15.2b + 15.3 COMPLETE (2026-08-15)** · **STAGES 15.4
  + 15.4a IMPLEMENTATION READY (2026-08-15)** — BALANCED REAL APPLY RETEST REQUIRED; branch
  `phase/15-profile-execution`; NOT merged.
- **Stage 15.1 — Profile Execution & Safe Execution Matrix (ADR-094):** profiles now produce
  clearly different, supported execution plans. Core `ProfileExecutionMatrix` (AutoApply /
  Recommend / Optional / Keep / Blocked / NotApplicable from knowledge + risk + protection +
  confidence + execution support — never raw identity strings); `ExecutionSupportMatrix`
  (auditable: AppX removal / registry policy / privacy / personalization / OptionalFeature
  disable supported; service config conditional; Capability / CBS / Driver removal NOT
  supported — KNOWN != REMOVABLE); `ProfileExecutionService` (inventory → engine + gaming
  policy → matrix → `ProfileDeltaReport` → validated `CustomizationPlan`); `ProfilePlanValidator`
  (remove+keep / duplicates / dependency-required / unsupported / protected); extras materially
  change plans (Xbox/WSL/Print/Touch/Remote — regression-tested); manual overrides authoritative;
  localized per-profile preview UI (Automatic/Recommended/Optional/Kept + bounded highlights);
  deterministic six-profile comparison over the real-derived fixture (plan validation only):
  Balanced auto=8 · Gaming PC auto=18 · Dedicated Gaming auto=8 · Developer auto=15 ·
  Office auto=5 · Lightweight auto=24 (Lightweight most active but safe — no CBS/driver/
  servicing removal). `WinForge.RealCapture` exports `profile-plans.json`. **1150 tests
  (Core 53, App 1097), 0 err/0 warn (Release, ordinary in-place).**
- **Stage 15.2 — Real profile differentiation + plan accounting fix (implementation ready,
  ADR-095):** the real 25H2 profile-plans capture exposed fixture-blind problems and fixed all
  of them. (1) **757→674 accounting explained + fixed**: `ProfileCandidateService` builds ONE
  unified candidate stream — inventory objects (deep → curated → explicit exclusion bucket) +
  non-inventory optimization definitions — with exact `ProfileInventoryAccounting`
  (Total = evaluated + every exclusion; 757 = 678 evaluated + 79 Unknown, no unexplained loss).
  (2) **byOperationType fixed**: now counts EXECUTABLE changes (AutoApply+Recommend) only;
  inventory source counts are `InventoryBySource` (`ProfileInventoryAccounting.BySource`);
  changeCount = AutoApply+Recommended everywhere (fixture / RealCapture / UI / Review).
  (3) **non-inventory layer integrated**: registry/privacy/personalization/service definitions
  now participate — Office changeCount 0→22 (meaningful conservative delta: privacy + consumer
  trims, keeps printing/OneDrive/Teams), Balanced 3→17 baseline, Developer 6→24 with registry/
  privacy actions. (4) **Gaming != DedicatedGaming on real media**: DedicatedGaming policy
  `WiderMinimalSteer` (Low cloud→auto, Moderate productivity/communication→recommend,
  Moderate media→optional) + Dedicated catalog now carries the same trims as Gaming PC; real-like
  stream: Gaming changes=28 vs DedicatedGaming=30 with exactly two policy-driven semantic
  actions (OneDrive auto, Teams recommend). Dedup by canonical Phase 12 operation identity;
  unsupported "optional" now Blocked; UI preview rebuilt on the SAME GenerateDelta report.
  `profile-plans.json` upgraded to v2 (inventoryAccounting / decisionCounts / planChanges /
  semanticActionKeys / keptHighlights / blockedHighlights). **1162 tests (Core 53, App 1109),
  0 err/0 warn (Release, ordinary in-place).**
- **Stage 15.2 REAL-DESKTOP VALIDATION REQUIRED**: rerun the elevated RealCapture CLI
  (same command as Phase 14) and review the new `profile-plans.json` only.
- **Stage 15.3 — Validated Profile BuildPlan as single Apply source (implementation ready,
  ADR-096):** the real-stream blocker (BuildPlan failing safe on malformed operations) is fixed at
  the ROOT: `BuildPlan` now maps complete execution payloads (service name + start type, registry
  hive/path/value, feature/package identity; `svc:|opt:|feat:|appx:|cap:|pkg:` conventions) with
  SourceDefinitionIds provenance; the OptimizationCatalog data was verified already clean
  (ActivityHistory has the valid offline policy target; all service identities canonical +
  allowlisted). New reusable `OptimizationDefinitionValidator` (MissingTechnicalTarget /
  MissingRegistryTarget / MissingServiceName / MissingFeatureName / UnsupportedExecution /
  InvalidValue / DuplicateCanonicalIdentity) runs in catalog tests, inside BuildPlan, and in
  PlanCapture. ALL SIX primaries now produce non-null validated BuildPlans on the real-derived
  stream (Balanced 16/9 · Gaming 24/17 · DedicatedGaming 27/18 · Developer 20/17 · Office 17/9 ·
  Lightweight 27/23 ops/selected). Profile → Customize → Review → Apply flows through ONE shared
  CustomizationPlan (IsAdoptEligible aligned to WasProfileDriven → preview auto count == Review
  selected count); manual overrides authoritative; extras affect the actual executable plan
  (Lightweight+Xbox keeps the Xbox services); Apply reuses the Phase 12 executor; PlanCapture
  writes profile-buildplans.json (structural validation only). **REAL STRUCTURAL VALIDATION
  PASSED (2026-08-15)**: all six primaries validationPassed == true on Win11 25H2 Pro zh-CN x64
  Index 4 (Balanced 16, Gaming 25, DedicatedGaming 33, Developer 21, Office 17, Lightweight 38),
  empty validationErrors, conflict-free canonical keys. **1181 tests (Core 53, App 1128),
  0 err/0 warn.**
- **Stage 15.3b — Optional Feature canonical aggregation (COMPLETE, ADR-096 addendum):** the real
  structural validation FAILED DedicatedGaming 'Containers' (4), Lightweight 'HyperV' (9) and
  DedicatedMinimal (MediaPlayer x2, HyperV x9) because the deep catalog maps MULTIPLE genuinely
  distinct DISM features to ONE profile-facing family id (HyperV x9, Containers x4, MediaPlayer =
  ZuneMusic AppX + WindowsMediaPlayer; zero raw-identity duplicates on real media). Fixed WITHOUT
  weakening the validator: `ProfileExecutionItem.ExecutableIdentity` carries the actual DISM
  FeatureName while `LogicalId` stays the semantic family; new `ProfilePlanAggregator` merges true
  same-executable candidates BEFORE final plan validation (provenance union via SourceDefinitionIds,
  keep-wins precedence, AutoApply>Recommend, conflicting executable states fail explicit); the
  validator's duplicate-change check now keys on the executable identity so distinct real features
  stay distinct executable operations. Count reconciliation: deltaCount (semantic) vs
  buildPlanOperationCount + mergedDuplicateCount + mergeGroups in profile-buildplans.json. Offline
  re-validation over the captured inventory passes ALL SEVEN primaries (16/25/33/21/17/38/44).
  **1192 tests (Core 53, App 1139), 0 err/0 warn.**
- **Stage 15.4 — Real Offline Apply Validation (implementation ready, ADR-097):** proves
  profile-generated BuildPlans EXECUTE safely against a real mounted 25H2 image and that results are
  INDEPENDENTLY READ BACK. `WinForge.RealCapture --apply-profile <Id>` (Balanced and DedicatedGaming
  first — one profile per invocation): inspect ISO (read-only) → export selected index to an isolated
  workspace → mount → final validated BuildPlan → execute ONLY SelectedOperations (AutoApply) →
  read-back verification (AppX `/Get-ProvisionedAppxPackages` absence; OptionalFeature
  `/Get-FeatureInfo` exact State; offline service reads the mounted SYSTEM hive Start; offline
  registry reads hive/path/name/kind/data; OfflineDefaultUser → `Users\Default\NTUSER.DAT` — never
  host HKCU) → `profile-apply-validation.json` (buildPlanOperationCount/selectedOperationCount/
  attempted/succeeded/failed/skipped/validationPassed + per-op canonicalKey/operationType/
  expectedAction/executionStatus/verificationStatus/verificationDetail + mountCleanup) → DISCARD the
  mount (authoritative `/Get-MountedImageInfo`; unknown mounts never discarded) → clean the
  workspace. Deterministic already-satisfied pre-check skips; per-op failures recorded exactly, no
  silent success; failed mount cleanup is a BLOCKER. Recommend-only rows (Containers/WSL) are never
  executed. **FIRST REAL BALANCED APPLY (recorded):** mount/discovery/hive-access/cleanup PASSED;
  aborted at offline-registry PRECHECK with "The specified registry key does not exist." — see
  Stage 15.4a. **1225 tests (Core 53, App 1172), 0 err/0 warn.**
- **Stage 15.4a — Offline registry precheck: missing key semantics (implementation ready,
  ADR-097 addendum):** the first real Balanced apply proved mount/workspace safety but exposed
  precheck absence semantics: .NET 8 `RegistryKey.GetValueKind` throws `IOException` (not
  `ArgumentException`) when a VALUE is absent from an existing key, and `ReadValue` let it escape →
  the whole profile aborted. Fixed: `ReadValue` returns `Exists=false` for that expected-absence
  case; precheck then reports "operation required" for missing key/value/different value and
  `AlreadySatisfied` only for a matching value; POST-EXECUTION missing stays `VerificationFailed`
  (separate semantics). The executor already creates missing subkey paths (unchanged);
  `OfflineDefaultUser` → `<mount>\Users\Default\NTUSER.DAT`, never host HKCU. Structured
  diagnostics: report gains `failureStage`/`failedCanonicalKey`/`error` and survives preflight
  failure (cleanup always runs). **1225 tests (Core 53, App 1172), 0 err/0 warn.** BALANCED REAL
  APPLY RETEST REQUIRED (`--apply-profile Balanced` only; DedicatedGaming NOT yet).

## Phase 14 — Deep Component Coverage & Classification (COMPLETED — 89.56% real-media coverage)

- **Status:** COMPLETE — **PHASE 14 ACCEPTED (2026-08-14)** — REAL COMPONENT COVERAGE VALIDATION
  **PASSED** at **89.56%** semantic knowledge coverage across the currently supported discovery
  providers (AppX/Capability/OptionalFeature/CbsPackage; Service/Driver/ScheduledTask/Language/
  WinRecovery/SystemApp NotSupported). THIRD elevated RealCapture (real Administrator) FINAL EXACT
  numbers: **757 total · Curated 33 · Protected 53 · KnownDeep 645 · Heuristic 0 · Unknown 79**;
  CBS **149/149** known; 79 Unknown ACCEPTED as explicit long-tail technical debt (ADR-093). Three
  real captures: 30.78% → 82.30% → **89.56%**. Gaming Profile 2.0 COMPLETED + ACCEPTED. Merged to
  `main` via `--no-ff` (2026-08-14); branch `phase/14-deep-component-classification` retained.
- **Stage 14.1 delivered:** taxonomy + classification layer (discovery/knowledge/planning separate);
  ComponentFunctionCategory; DeepComponentKnowledge (risk/recommendation/protection/profile/
  confidence); ComponentNormalizer + collision guard; first-batch DeepComponentCatalogData
  (**108 curated families**); protected groups; Gaming profile foundation metadata (Gaming PC vs
  Dedicated Gaming; no placebo tweaks); UI integration (classified discovered rows in Apps/Windows
  Components knowledge tabs); coverage metrics with Unknown kept visible. **845 tests, 0 err/0 warn.**
- **Stage 14.2 (complete):** real-media family expansion — 145 curated entries (+22 CBS +15 hardware
  family rules), UnknownFamilyAnalyzer, enhanced coverage metrics, restrained UI summary,
  docs/COMPONENT-COVERAGE.md. **892 tests, 0 err/0 warn.**
- **Stage 14.3 (implementation ready; elevated validation pending):** (A) `tools/WinForge.RealCapture`
  elevated capture CLI — EXACT production pipeline (inspect→export→mount→DISM discovery→matcher→
  DeepComponentClassifier→`CoverageAccountingService` no-double-count per-source buckets→top-30
  Unknown families→6 JSON exports to `.tmp/phase14-real/`→cleanup); Core exact accounting service;
  real-derived stable fixture `tests/fixtures/25H2-Pro-zhCN-component-families.json` + validator.
  (C) **Gaming Profile 2.0 engine**: knowledge-driven pipeline Inventory→Deep Knowledge→Profile
  Policy→Candidate→Safety Gate→Plan (ADR-088/089/090); `GamingPcPolicy` + `DedicatedGamingPolicy`
  (distinct primaries); `ProfileSafetyGate` final authority; extras influence keeps; §8 keep list;
  no placebo tweaks; deterministic localized reasons; localized Gaming summary in the profile panel;
  manual overrides authoritative; `Gaming`=Gaming PC + new `DedicatedGaming` primary. **975 tests
  (Core 53, App 922), 0 err/0 warn (Release; ordinary in-place build+test pass).**
- **Stage 14.3b (implementation ready; second capture required, ADR-091):** six real Language
  capability families classified (Basic/Handwriting/TextToSpeech/OCR/Fonts/Speech — 337 objects,
  one family per role, locale identity preserved; Function=Language/Moderate/ProfileDependent/
  Sensitive; `LanguageCapabilityMetadata` for target-language prep, NO destructive stripping);
  family analyzer refined (microsoft.windows.* → up to 5 semantic segments; Console.Legacy/
  Ethernet.Client.Intel/Realtek/Wifi.Client.* distinct); `Package_for_*` CBS semantics preserved
  by the normalizer (DotNetRollup/KB/RollupFix distinct, Critical/Protected/RequiredKeep);
  high-confidence real CBS (Licenses/Kernel/FodMetadataServicing Critical+Protected; OneCore-DirectX
  kept for Gaming; SenseClient/Hello Security; VBSCRIPT Legacy; OpenSSH ProfileDependent; Notepad
  Productivity) + small features (Braille/WirelessDisplay/AzureArc/AppServerClient/ProjFS; embedded
  lockdown/filter/UWF High/RecommendedKeep — never auto Gaming removal). Catalog 145→177; ZERO
  heuristic entries added. Gaming policies keep ALL languages. **1030 tests (Core 53, App 977),
  0 err/0 warn (Release, ordinary in-place).**
- **Stage 14.3c (implementation ready; FINAL third capture required, ADR-092):** high-confidence
  long-tail classification — Wi-Fi/Ethernet driver capability families by vendor-family rule
  (Networking/High/RecommendedKeep/Sensitive; NEVER auto-removed by Gaming); critical system items
  (DirectX.Configuration.Database RuntimeDependency/Critical/RequiredKeep/GamingRelevant;
  SecHealthUI Security/Critical/Protected; Microsoft-Windows-FodMetadata-Package
  Servicing/Critical/Protected; Onecore.StorageManagement SystemCore/High; Hello.Face
  Security/High/ProfileDependent); 7 media codec AppX (Media/Low/ProfileDependent — Gaming PC never
  auto-strips codecs, optional-only); Outlook/Office Hub (Low+ConsumerContent, Gaming PC auto only
  when supported AppX removal exists, gate blocks otherwise); Dev Home (Developer/DeveloperTool —
  Developer profile KEEP override, Gaming optional-only, curated catalog 22→23); Application
  Compatibility Enhancements (SystemCore/High/RecommendedKeep, AppX + CBS); Console.Legacy/WebDriver/
  MathRecognizer/Wallpapers.Extended/App.WirelessDisplay.Connect capabilities; ClientForNFS/
  DataCenterBridging/ADAM-Client/HostGuardian/LegacyComponents features (ProfileDependent, never
  Low-risk auto). Deep catalog 177→203 (+27, ZERO heuristic). No broad namespace fallback rules.
  **1105 tests (Core 53, App 1052), 0 err/0 warn (Release, ordinary in-place).**
- **POST-PHASE 14 (moved out of Phase 14, NOT a Stage 14.4):** Service/Driver/ScheduledTask/SystemApp/WinRecovery discovery + classification; deeper dependency resolution; destructive CBS/driver removal execution; aggressive Lightweight/Dedicated profile execution; FullHealthValidated requirement after deeper destructive customization. The remaining 79 Unknown (mostly singletons: Quick Assist/CrossDevice, MSIX tooling, MSMQ, MultiPoint, NFS admin, legacy IrDA/RIP, RSAT subfeatures, printing subfeatures, Recall, misc enterprise/legacy) are ACCEPTED technical debt — no Stage 14.3d to chase a higher percentage.
  from the real top-30 report; per-object dependency resolution; removal execution only after high
  classification coverage + protection gating.

## Phase 13 — Compatibility & Real-World Validation Matrix

- **Status:** **COMPLETED (2026-08-13)** — COMPATIBILITY FOUNDATION + REAL MEDIA + VM INSTALL
  VALIDATION all PASSED; merged to `main` via `--no-ff` (branch `phase/13-compatibility-matrix`
  retained). Baseline: 25H2 Pro zh-CN x64 WIM → VM INSTALL VALIDATED (Level = VmInstallValidated,
  ADR-084); deeper FullHealthValidated deferred to aggressive component-removal phases.
- **Goal:** turn the single-image proof into a formal compatibility + real-world validation
  system: detect release/build/edition/language/architecture/format/media BEFORE destructive
  work; durable matrix; VM install validation; clear user-facing status; future releases
  regression-testable against the matrix.
- **Implemented (Stage 13.x foundation):** compatibility model + rule engine; release classifier
  (24H2/25H2/unknown-newer/older) with graceful future-build degradation; edition capability
  facts; language stable-identity policy; WIM/ESD/SWM detection; multi-index enumeration +
  index persistence; media classification (never overclaims "official"); compatibility preflight
  UI after ISO inspection; blocking-vs-warning rules; validation matrix model + initial targets
  (Tier A/B/C); JSON/Markdown validation report export with Validated-vs-Automated separation;
  safety invariants (Update/Defender/drivers/Store) asserted; synthetic fixtures; docs/
  COMPATIBILITY.md; ADR-073..078.
- **Acceptance (remaining):** baseline VM validation record (25H2 Pro zh-CN x64, real ISO →
  workflow → generated ISO → VM install: UEFI boot/Setup/OOBE/desktop/Update/Defender/Store/
  recovery) must be created before Phase 13 close; Tier A en-US then Tier B/C incrementally.

## Phase 12 — Workspace Lifecycle & Disk Safety

- **Status:** **COMPLETED — REAL DESKTOP VALIDATION — PASSED** (2026-08-12; Stages 12.1–12.7 all
  COMPLETED; merged to `main` via `--no-ff`; branch `phase/12-workspace-lifecycle` retained).
- **Goal:** a deterministic, safe workspace lifecycle — repeated Select ISO → Prepare → Customize →
  Apply/Cancel → Build → Finish must never accumulate hundreds of GB of stale files (ADR-062..066).
- **Scope (Stage 12.1 — lifecycle + disk safety, IMPLEMENTED):** durable `workspace.json` manifests +
  explicit lifecycle states; DISM-authoritative mount safety (fail closed); discard/build-completed →
  cleanup-eligible; recoverable checkpoints retained; startup orphan/legacy classification (旧版残留 offered,
  never bulk-deleted); Settings Storage UI (async scan + safe-cleanup preview + one-click 清理临时文件);
  output/temp separation (final ISO → `Documents\WinForge`); conservative disk-space guards before
  Prepare/Build; attribute-aware cleanup with partial-failure reporting.
- **Scope (Stage 12.2 — Finish cleanup + workspace-root settings, IMPLEMENTED):** workspace-root editor in
  Settings → 存储 (change/restore default, validation, active-mount block, persisted across restart,
  low-space warning); multi-root cleanup discovery (old roots never orphaned); Finish auto-cleanup with
  ISO-preserved / recoverable-retained / partial-with-retry reporting (cleanup failure is a warning, never a
  build failure); Discard auto-cleanup in the background.
- **Acceptance:** 701 tests pass (0 errors / 0 warnings); real-desktop validation on Windows 11 25H2:
  Prepare→Discard leaves no mount; Prepare→Apply→Build→Finish preserves the ISO and drops temp usage;
  a forced recoverable failure keeps only the checkpoint; a stale disposable workspace is detected, sized
  and cleaned from the UI; workspace-root change applies to new workflows only and cleanup still finds old
  roots. Do NOT mark Phase 12 complete until real-desktop validation passes.

## Phase 13 — Release

- **Status:** NOT STARTED
- **Goal:** Package and publish a release.
- **Scope:** Release build, documentation, changelog, distribution.
- **Deliverables:**
  - Release artifacts, updated docs, signed/versioned package
- **Acceptance Criteria:**
  - Release criteria met (including a real Windows installation test — see
    docs/TESTING.md); changelog updated.
