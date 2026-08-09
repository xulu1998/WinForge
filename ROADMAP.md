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

- **Status:** NOT STARTED
- **Goal:** Rebuild a customized Windows ISO.
- **Scope:** Apply plan to mounted image, unmount, rebuild ISO preserving original
  structure.
- **Deliverables:**
  - `IBuildService` (Core) + Infrastructure implementation
- **Acceptance Criteria:**
  - Produces a bootable ISO whose structure matches the source except for the
    intended customizations.

---

## Phase 11 — Safety & Validation

- **Status:** NOT STARTED
- **Goal:** Validation, logging, and recoverability.
- **Scope:** Pre/post build validation, structured logs, dry-run, rollback on
  failure.
- **Deliverables:**
  - Validation rules (Core), logging, rollback coordinator
- **Acceptance Criteria:**
  - Build plan is validated before apply; full operation log produced; failed
    builds roll back to a recoverable state.

---

## Phase 12 — Release

- **Status:** NOT STARTED
- **Goal:** Package and publish a release.
- **Scope:** Release build, documentation, changelog, distribution.
- **Deliverables:**
  - Release artifacts, updated docs, signed/versioned package
- **Acceptance Criteria:**
  - Release criteria met (including a real Windows installation test — see
    docs/TESTING.md); changelog updated.
