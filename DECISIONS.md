# Architecture Decision Records

ADR format: **Status**, **Context**, **Decision**, **Consequences**.
All decisions are `ACCEPTED` unless noted.

---

## ADR-001: Use C# + .NET 8 + WPF

- **Status:** ACCEPTED
- **Context:** WinForge is a Windows-only desktop tool with a rich graphical
  interface for image customization.
- **Decision:** Build with C# on .NET 8 using WPF for the desktop UI.
- **Consequences:** Native Windows integration, long-term support from
  Microsoft, and a mature XAML UI stack. Requires the .NET 8 runtime/SDK on the
  dev and target machines.

## ADR-002: Use MVVM

- **Status:** ACCEPTED
- **Context:** The UI needs to stay testable and separated from business logic
  as features grow.
- **Decision:** Adopt the Model-View-ViewModel pattern.
- **Consequences:** ViewModels are unit-testable; UI and logic are decoupled.
  Adds some boilerplate (bindings, commands).

## ADR-003: Separate App / Core / Infrastructure

- **Status:** ACCEPTED
- **Context:** Platform APIs (DISM, registry, ISO) are easy to leak into the UI
  and make testing hard.
- **Decision:** Three runtime projects — App (UI), Core (domain + interfaces),
  Infrastructure (Windows implementations) — plus WinForge.Core.Tests.
- **Consequences:** Clear layering and testability; Core stays platform-agnostic
  and depends on nothing but itself.

## ADR-004: Microsoft original Windows ISO as supported input

- **Status:** ACCEPTED
- **Context:** The product must start from a trustworthy, unmodified source.
- **Decision:** Officially support original Microsoft Windows 11 ISO images as
  input. Third-party modified ISOs are NOT official compatibility targets.
- **Consequences:** Predictable, documented behavior; avoids distributing or
  relying on modified images.

## ADR-005: Use documented Microsoft deployment mechanisms

- **Status:** ACCEPTED
- **Context:** Image operations are sensitive and must be reliable and
  supportable.
- **Decision:** Prefer documented Microsoft deployment APIs/tooling (DISM,
  Windows ADK, supported PowerShell modules) wherever practical.
- **Consequences:** Lower risk of breakage across Windows updates; easier to
  support and audit.

## ADR-006: Independent implementation, no copying

- **Status:** ACCEPTED
- **Context:** Several Windows customization/debloat projects exist (e.g.
  tiny11builder). Reusing their code/structure would create licensing and
  originality problems.
- **Decision:** WinForge is implemented independently. Do not copy source code,
  file structure, PowerShell, function/variable names, README, XML, assets, or
  implementation approaches from tiny11builder or other debloat projects.
- **Consequences:** Full ownership of the codebase; must design our own
  approach using public Microsoft documentation.

## ADR-007: Presets are configuration data

- **Status:** ACCEPTED
- **Context:** Users want quick starting points (Recommended, Windows 10
  Inspired, Gaming, Privacy, Minimal, Custom).
- **Decision:** Presets are configuration data consumed by the Build Plan, not
  separate hard-coded execution paths.
- **Consequences:** Adding/changing a preset is a data change, not a code
  branch; prevents divergence and bugs.

## ADR-008: Safety and recoverability first

- **Status:** ACCEPTED
- **Context:** Image customization can render a system unbootable.
- **Decision:** Safety and recoverability take priority over maximum debloating.
- **Consequences:** Defaults favor stability; operations support validation,
  logging, dry-run, and rollback (Phase 11).

## ADR-009: Windows 10 Inspired uses native policies only

- **Status:** ACCEPTED
- **Context:** The "Windows 10 Inspired" experience is popular but risks pulling
  in third-party shell replacements.
- **Decision:** The Windows 10 Inspired experience must prefer native Windows
  policies/settings and must NOT silently bundle third-party shell-replacement
  software.
- **Consequences:** Safer, more supportable result; stays within documented
  Windows behavior.

## ADR-010: Composition root uses Microsoft.Extensions.DependencyInjection

- **Status:** ACCEPTED
- **Context:** The App needs a small, clear way to wire view models and services
  without a service-locator anti-pattern.
- **Decision:** Use `Microsoft.Extensions.DependencyInjection` for the composition
  root (`Bootstrapper`). View models are injected directly into `MainViewModel`;
  Core interfaces are bound to Infrastructure implementations here. Core itself
  never references the DI container.
- **Consequences:** A tiny, official Microsoft dependency; explicit and
  testable wiring. No service-locator calls scattered through the UI.

## ADR-011: MVVM base lives in the App (WPF) project, not Core

- **Status:** ACCEPTED
- **Context:** `RelayCommand` / `AsyncRelayCommand` implement
  `System.Windows.Input.ICommand`, which is only available in a WPF-aware
  project. Core must stay platform-agnostic (net8.0, no WPF reference).
- **Decision:** `ViewModelBase`, `RelayCommand`, and `AsyncRelayCommand` are
  implemented in `WinForge.App` (net8.0-windows). Core remains free of any WPF
  dependency and exposes only `INotifyPropertyChanged`-based state.
- **Consequences:** Core stays unit-testable without WPF; command types bind
  natively to WPF controls. View models remain in the App project.

## ADR-012: Default logging is an in-memory ring buffer

- **Status:** ACCEPTED
- **Context:** The app needs logs visible live in the UI without a heavy
  third-party logging framework.
- **Decision:** Provide `ILoggerService` in Core and implement an in-memory,
  bounded (`ObservableCollection`-backed) logger in Infrastructure
  (`InMemoryLoggerService`). File/ETW sinks can replace it later without
  touching Core or the UI.
- **Consequences:** No external logging dependency; live Logs page works
  out of the box. Entries are capped to avoid unbounded memory use.

## ADR-013: Headless boot test verifies startup wiring

- **Status:** ACCEPTED
- **Context:** WinForge is a WPF app; CI/sandboxes have no display, so the
  window cannot be rendered there. "The app starts" must still be verifiable.
- **Decision:** Add `WinForge.App.Tests` with a headless integration test that
  builds the real DI container, resolves the navigation shell, navigates between
  pages, mutates `AppState`, and asserts logging works — without creating a
  window.
- **Consequences:** Repeatable proof that startup wiring is correct in
  headless environments; the actual WPF window is confirmed by a developer on a
  Windows desktop.

## ADR-014: Logging is thread-safe and WPF-agnostic in Infrastructure

- **Status:** ACCEPTED
- **Context:** `ILoggerService` is called from background work (future DISM /
  Process workers, async Tasks). The original `InMemoryLoggerService` stored
  entries in an `ObservableCollection<T>` and the `LogsViewModel` bound to it
  from the UI thread. A background thread mutating a WPF-bound
  `ObservableCollection` throws a cross-thread exception, and Infrastructure
  should not carry a WPF collection dependency just for logging.
- **Decision:** Keep the logging store in Infrastructure as a lock-guarded plain
  `List<LogEntry>`. `Entries` returns a point-in-time snapshot (`ToArray()`);
  `Log()` is safe from any thread; `EntryAdded` is raised on the calling thread
  (outside the lock, to avoid re-entrancy). The WPF `ObservableCollection<T>`
  lives only in `LogsViewModel`, which seeds from the snapshot and marshals
  `EntryAdded` to the UI thread via the captured `SynchronizationContext`. Core
  and Infrastructure contain no WPF Dispatcher / `Application.Current` reference.
- **Consequences:** Background logging can never corrupt or directly mutate the
  UI-bound collection; no UI-thread assumption leaks into Infrastructure. The
  cost is a snapshot copy per `Entries` read, which is negligible for a log view
  and keeps the boundary clean.

## ADR-015: Cancellation-safe ISO cleanup (Step 2.1)

- **Status:** ACCEPTED
- **Context:** `WindowsIsoInspectionService` mounts a Windows ISO read-only via
  `WindowsIsoMountService` and must always dismount it. Two cleanup hazards were
  identified: (1) the `finally` dismount used the *original* `CancellationToken`,
  so a cancellation that had already fired could cancel cleanup and leave the ISO
  mounted; (2) `MountReadOnlyAsync` could be cancelled/killed after
  `Mount-DiskImage` succeeded but before it returned the mounted root, leaving
  `mountedRoot == null` and the `finally` dismount (guarded by `mountedRoot !=
  null`) would never run.
- **Decision:** A mount is tracked with a `mountAttempted` flag set before the
  mount call; once set, the `finally` block always attempts a best-effort
  `Dismount-DiskImage` using `CancellationToken.None` (never the caller's token),
  so cleanup can never be cancelled. `WindowsIsoMountService.DismountAsync` uses
  `-ErrorAction SilentlyContinue`, so dismounting an image that is not mounted is
  a safe no-op. If the mount returns without a usable root, inspection fails
  (post-mount failure) and still triggers cleanup. `OperationCanceledException`
  is re-thrown after cleanup so cancellation is never swallowed by a successful
  dismount; other failures surface as a `Failed` result. User-facing
  `ErrorMessage` is a generic, friendly string; raw PowerShell/HRESULT/command
  detail is logged only via `ILoggerService`.
- **Consequences:** An ISO can never be left mounted due to cancellation or a
  mid-mount failure, regardless of whether the caller's token is signalled.
  Cleanup is observable and unit-testable with a fake `IIsoMountService` that
  records the token used. The fix adds no new dependencies and keeps
  Infrastructure free of WPF.

## ADR-016: Read-only Windows image metadata via DISM (Step 2.2)

- **Status:** ACCEPTED
- **Context:** Step 2.2 must read WIM/ESD image indexes, editions, architecture,
  Windows version, build, edition ID, installation type, and languages — but the
  operation must remain strictly read-only. The image must NOT be mounted,
  modified, exported, or serviced, and the inspection must occur while the ISO is
  still mounted (Step 2.1's dismount would otherwise make the install image
  unreachable). The platform call must stay behind a Core abstraction so it is
  unit-testable without Windows/DISM and the UI never coordinates mount lifecycle.
- **Decision:** Read metadata with `dism.exe /Get-ImageInfo /ImageFile:"<path>" /English`,
  the documented Windows 11 read-only image query (no `Mount-Image`, no
  servicing). `/ImageFile:` is used (not `/WimFile`). Because the host UI language
  may not be English, `/English` is mandatory so the parsed fields are stable. The
  query is performed in **two stages** because a single
  `/Get-ImageInfo /ImageFile:"..."` call without `/Index` only reliably returns
  per-index `Index` / `Name` / `Description` (and `Size`); the detailed fields —
  `Architecture`, `Version`/`Build`, `Edition Id`, `Installation`, and
  `Languages`/`Default Language` — are reported **only** by a per-index query
  (`/Get-ImageInfo /ImageFile:"..." /Index:<n> /English`). `WindowsImageMetadataService`
  therefore (A) runs the enumeration query once, then (B) runs one detail query
  for **every** enumerated index (index numbers are not assumed sequential and are
  not assumed to map to a specific edition), and merges the two by index. If
  enumeration fails the whole result is `Failed`; if a single per-index detail
  query fails, that edition keeps its enumerated `Index`/`Name`/`Description`, its
  detailed fields stay `null`, and its `DetailStatus` is set to `Failed` (logged,
  not shown raw) — WinForge never silently pretends full metadata arrived. Parsing
  is split to match: `DismImageInfoParser.ParseImageList` reads only the reliable
  enumeration fields, and `DismImageInfoParser.ParseImageDetails` reads the full
  detail for one index. Both are pure functions of the captured text, key-based,
  tolerant of unknown / future / reordered fields, and never slice fixed columns;
  empty or index-less output yields a `Failed` result, not an exception. Process
  execution is abstracted behind `IProcessRunner` (Core) with `ProcessRequest` /
  `ProcessResult` DTOs; Infrastructure's `WindowsProcessRunner` uses
  `System.Diagnostics.Process` (no window, captured stdout/stderr, cancellation by
  killing the child). `IWindowsImageMetadataService` returns a
  `WindowsImageMetadataResult` — environmental failures (missing tooling, non-zero
  exit, corrupt image) are surfaced as `Failed` with a friendly message, while
  only cancellation propagates as `OperationCanceledException`. The original Step
  2.1 orchestrator (`WindowsIsoInspectionService`) is extended into a single
  high-level session: mount → layout inspection → install-image metadata
  inspection (while still mounted) → guaranteed dismount (ADR-015 preserved). The
  ViewModel consumes the combined `IsoInspectionResult.ImageMetadata` and never
  touches mounting, DISM, or `Process`.
- **Consequences:** ESD and WIM are handled identically by DISM (no ESD→WIM
  conversion, no image modification). Core stays platform-agnostic and fully
  testable via fakes; parsing and invocation are independently unit-tested. Top-
  level version/build/architecture/languages are reported only when every edition
  agrees — otherwise the fields stay `null` and the UI shows "Mixed" rather than
  guessing from the first index. Raw DISM stderr / HRESULT is never shown to the
  user, only logged.
- **Correction (2026-08-08):** Real desktop validation of Step 2.2 exposed DISM
  exit code 87 because the original implementation invoked `dism.exe /English
  /Get-WimInfo /ImageFile:"..."` — an incorrect command combination for the
  Windows 11 DISM command line. The active, documented command is now `dism.exe
  /Get-ImageInfo /ImageFile:"<path>" /English` (enumeration) and `dism.exe
  /Get-ImageInfo /ImageFile:"<path>" /Index:<n> /English` (per-index detail);
  `/ImageFile:` is kept (not `/WimFile`). The parser type was renamed
  `DismWimInfoParser` → `DismImageInfoParser`. The two-stage design is unchanged.
  Step 2.2 remains IMPLEMENTED / PENDING REAL DESKTOP RE-VALIDATION — NOT COMPLETED.
- **Correction (2026-08-08, language parsing):** The same real desktop run that
  validated the two-stage flow also exposed a parser defect: `ParseImageDetails`
  blindly took the first whitespace token of any non-key line inside the
  `Languages` section, so DISM's trailing footer `The operation completed
  successfully.` was added to the language list as `The`. `ExtractLanguage` was
  replaced by `TryNormalizeLanguageTag`, a conservative BCP-47-like validator that
  accepts only a 2–3 letter primary subtag followed by ≥1 hyphenated region/script/
  variant subtag (e.g. `en-US`, `zh-CN`, `pt-BR`, `sr-Latn-RS`), strips a trailing
  `(Default)` annotation before validation, and rejects arbitrary prose. The
  `Languages` section now terminates on the first non-language, non-blank, non-key
  line, so future DISM footer prose cannot leak in. Step 2.2 remains IMPLEMENTED /
  PENDING FINAL LANGUAGE-PARSER RE-VALIDATION — NOT COMPLETED.

- **Closure (2026-08-08):** Phase 2 — ISO Inspection is formally **COMPLETED**. The
  final Step 2.2 real-desktop re-validation PASSED on Windows 11 25H2 (Chinese
  Simplified, x64, Consumer Editions, `install.wim`): Language was `zh-CN` only
  (footer prose `The operation completed successfully.` correctly rejected),
  confirming the language-parsing fix. The two prior `NOT COMPLETED` notes above are
  therefore superseded. Phase 2 acceptance criteria are met: official Microsoft ISO
  inspected non-destructively, image type detected, editions enumerated, architecture
  detected, languages detected, and real Windows 11 25H2 desktop validation passed;
  Step 2.1 and Step 2.2 are both merged to `main`. There is no Step 2.3 in the
  roadmap — Phase 2 comprises only Step 2.1 and Step 2.2 — and none is invented. Next
  phase: Phase 3 — WIM Engine (NOT STARTED).

## ADR-017: Durable image-source descriptors never persist temporary mounted drive letters

- **Status:** ACCEPTED
- **Context:** Step 3.1 builds a durable `ImageWorkspace` from a Phase 2 ISO
  inspection. Phase 2 mounts the ISO read-only to a temporary drive (e.g. `G:\`),
  reads its layout and image metadata **while mounted**, then always dismounts
  (ADR-015). The temporary mount root therefore disappears as soon as inspection
  ends — it is not a stable, re-openable location and must never become part of
  durable application state.
- **Decision:** A durable descriptor stores exactly three location-identifying
  pieces — the **original ISO path** (`SourceIsoPath`, e.g.
  `F:\ISOs\Win11.iso`), the image's **relative path inside the ISO**
  (`ImageRelativePath`, e.g. `sources\install.wim` / `sources\install.esd`), and the
  **selected index**. The relative path is *derived* from the detected install-image
  type, never copied from a temporary mount root. No temporary mounted drive letter
  is stored, shown, or reconstructed. Future Phase 3/4 operations that need the
  install image will acquire their own short-lived source-access session (remount
  the ISO, resolve `SourceIsoPath` + `ImageRelativePath`, target `SelectedIndex`),
  just as Phase 2 owns its own mount/dismount lifecycle.
- **Consequences:** The workspace remains valid after the original ISO is dismounted
  and even after the host machine reboots; there is no dangling `G:\…` reference. The
  Phase 2 mount → inspect → metadata → dismount session (ADR-015) is unchanged and
  stays strictly read-only. UI/state equality tests can assert the durable descriptor
  contains no temporary drive letter.

## ADR-018: WinForge.App requires administrator elevation

- **Status:** ACCEPTED
- **Context:** Step 3.1 real-desktop validation on a Windows 11 25H2 (zh-CN, x64,
  Consumer `install.wim`) ISO confirmed the Phase 2 DISM inspection path
  (`dism.exe /Get-ImageInfo`) fails with **DISM exit code 740**
  (ERROR_ELEVATION_REQUIRED) when `WinForge.App.exe` is launched without
  administrator rights — the application then reports "The Windows image could
  not be read." Running the same executable as Administrator succeeds. The image
  enumeration therefore requires an elevated token; a non-elevated launch can
  never complete ISO inspection.
- **Decision:** `WinForge.App.exe` declares the elevation requirement directly in
  its embedded application manifest (`src/WinForge.App/app.manifest`) via
  `<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />`,
  wired through `<ApplicationManifest>app.manifest</ApplicationManifest>` in
  `WinForge.App.csproj`. A normal launch (double-click / launcher) therefore
  triggers the standard Windows UAC prompt. No self-elevation process spawn, no
  PowerShell, no UAC suppression, and no custom token logic is used — the EXE
  itself states the requirement. A focused regression test
  (`AppManifestElevationTests`) reads the SOURCE manifest and asserts
  `level="requireAdministrator"`, so the setting cannot silently disappear in a
  future edit.
- **Consequences:** ISO inspection (and any future DISM-backed Phase 3/4 operation)
  runs with the privileges it needs. The elevation policy is declarative and lives
  in the EXE manifest, keeping it out of application code and reviewable in source
  control. Standard-user launches are prompted for admin credentials via UAC; the
  app does not attempt to bypass that. ADR-015 (guaranteed dismount) and ADR-016
  (read-only DISM) are unaffected.

## ADR-019: Servicing uses a WinForge-owned isolated working image; the source is never modified

- **Status:** ACCEPTED
- **Context:** Phase 3 Step 3.2 prepares an offline servicing workspace so later
  phases can mount and customize a Windows edition. The original ISO and its
  `install.wim`/`install.esd` must remain untouched (ADR-004/ADR-006). A user's
  selected source index N (inside the original install image) must be copied to an
  isolated, WinForge-owned working image whose own index is 1, and only that working
  image is ever mounted or serviced.
- **Decision:**
  - `ImageServicingService.PrepareWorkingImageAsync` uses `dism.exe /Export-Image`
    to export ONLY the selected source index into a new standalone WIM (always a
    WIM, even from an ESD source) under
    `%LOCALAPPDATA%\WinForge\Workspaces\<id>\image\install.wim`. The export reads the
    source from a transient read-only ISO mount that is always released in a `finally`
    block.
  - The working image is mounted (`/Mount-Image`) and unmounted (`/Unmount-Image
    /Discard`) only at its dedicated, empty `…\mount` directory; the source image is
    never mounted. Mount success is never trusted on exit code alone — registration
    is verified via `/Get-MountedImageInfo`, and a mount that is not registered is
    best-effort unmounted and marked `Failed`.
  - `IWorkspacePathProvider` addresses each workspace by a safe id segment (validated
    as `[A-Za-z0-9_-]{1,120}`), so a workspace id can never contain separators that
    escape its folder. `IWorkspaceSafeDelete` proves a deletion target is strictly
    inside the WinForge-owned workspace root and refuses drive roots, user-profile
    roots, the repository root, and the workspace root itself — cleanup can never
    recursively remove anything outside the workspace.
  - An active mount REFUSES ISO re-inspection and edition re-selection (the ViewModel
    sets a `BlockedMessage`); the session is only invalidated by an explicit unmount.
  - Working-image post-export validation uses the per-index
    `/Get-ImageInfo /Index:1` detail query (NOT the index-less enumeration query),
    because only the detail query reports Architecture/Version/Build. The original
    code used the index-less query and would have always failed validation — fixed
    during Step 3.2.
- **Consequences:** The original ISO/install image is immutable; all DISM mutation
  targets a WinForge-owned copy. Crash recovery (`ValidateServicingWorkspaceAsync`)
  classifies a session as Prepared / Mounted / Stale / Invalid / Failed by comparing
  stored state against real files and DISM mount registration. ADR-017 (no temp
  mount drive persisted) and ADR-018 (elevation) remain in force. Unmount always
  discards — commit-on-unmount is out of scope for Step 3.2.
- **Validation (2026-08-09, real Windows 11 25H2 zh-CN x64 Consumer `install.wim`):**
  source index 4 → isolated working WIM index 1 export succeeded; the isolated
  working-image strategy is validated; mount verified against real Windows filesystem
  contents (`Windows` / `Program Files` / `Program Files (x86)` / `Users` / `PerfLogs`);
  unmount/discard verified; `dism /English /Get-MountedWimInfo` reported no mounted
  images afterward; remount lifecycle passed; an active mount cannot be silently
  orphaned (ISO re-inspection / edition re-selection refused while Mounted); the
  original ISO / `install.wim` / `install.esd` were never modified. Step 3.2 =
  **COMPLETED**; Step 3.3 = **NOT STARTED**.

## ADR-020: Declarative offline customization plan (Step 3.3)

- **Status:** ACCEPTED
- **Context:** Step 3.3 lets users choose offline-image customizations (remove
  provisioned Appx, remove removable packages, set trusted privacy/system
  registry values, disable a small set of safe services) for the isolated working
  image produced by Step 3.2. Those choices must be validated and reviewed *before*
  anything is changed, and the change set must be auditable.
- **Decision:** A declarative, platform-agnostic `CustomizationPlan` (Core) describes
  **WHAT** to change: an ordered list of `CustomizationOperation` records, each with
  `OperationType`, `TargetIdentifier`, a registry target (`RegistryHive` /
  `RegistryKeyPath` / `RegistryValueName` / `RegistryValueKind` / `RegistryValueData`),
  a service target (`ServiceName` / `ServiceStartType`), a `RiskClass`, an
  `ExecutionOrder`, and a `ValidationResult`. The plan enforces a strict lifecycle:
  `Draft` → `Validated` → `Executing` → `Completed` / `CompletedWithErrors` / `Failed` /
  `Cancelled`. `Validate()` recomputes per-operation validation and only marks the plan
  `Validated` when there are no blocking issues (Duplicate / Conflict / Unsupported /
  MissingTarget) **and** at least one operation is selected; it returns the human-readable
  issue list (empty on success). `FreezeForExecution()` snapshots the selected operations
  into a locked, execution-safe copy and transitions the live plan to `Executing`, so it
  can no longer be edited mid-run. Core performs **no** mutation — Infrastructure executes
  the plan.
- **Consequences:** The UI can preview and validate the full change set before any change
  is made; execution operates on a frozen, immutable snapshot; the plan is fully
  inspectable and testable without Windows/DISM (fakes). Validation issues are surfaced to
  the user rather than silently skipped.

## ADR-021: Offline-only boundary — customizations target only the mounted working image

- **Status:** ACCEPTED
- **Context:** WinForge runs elevated (ADR-018). A defect in a customization engine could
  mutate the live host operating system. Step 3.3 must confine every change to the
  isolated, mounted working image produced by Step 3.2 (ADR-019), which lives under a
  WinForge-owned workspace and is never the host's running OS.
- **Decision:** Every customization operation targets an object reachable **only** through
  the mounted working image: offline registry hives loaded from files under the mount
  (`…\Windows\System32\config\*`), provisioned Appx / packages enumerated from the mounted
  image via DISM, services read from the mounted image's `SYSTEM` hive, and files strictly
  under the mount root. No operation ever touches the host's live registry (the running
  OS `HKLM` / `HKCU`), host services, host files outside the mount, or the source ISO. The
  execution service refuses to run unless the workspace is `Mounted` and the mount session
  matches (ADR-024).
- **Consequences:** The host OS is structurally protected — there is no code path that
  addresses the live host. Only the WinForge-owned working copy is ever changed.

## ADR-022: Exact-identity operations (no fuzzy / ambiguous targeting)

- **Status:** ACCEPTED
- **Context:** Removing the wrong package or Appx, or editing the wrong registry key, can
  break the offline image or open a security hole. Substring/wildcard matching against
  package or key names is dangerous and unpredictable across Windows builds.
- **Decision:** Provisioned Appx removal is identified by the **exact** DISM "Deployment
  package name" (fully-qualified package identity) parsed from
  `dism /Get-ProvisionedAppxPackages` — never by substring or wildcard. Package removal is
  gated by an **explicit, small allowlist** (`InternetExplorer-Optional`,
  `Printing-XPSServices`, `Xps-Document-Writer`); any package **not** on the allowlist is
  **SKIPPED at execution** (never removed), regardless of UI selection, and its operation
  is marked `Skipped`. Registry and service operations each target a single documented
  key/value or service name. Discovery enumerates exact identities and the UI presents them
  so the user selects by identity, not by guess.
- **Consequences:** Only known-safe, explicitly listed packages can ever be removed; a
  mislabeled or ambiguous selection cannot destroy the image. The allowlist is intentionally
  tiny and grows only via a reviewed change, not via user free-text.

## ADR-023: Offline hive lifecycle — WinForge-owned names, always unloaded

- **Status:** ACCEPTED
- **Context:** Offline registry editing loads a hive file from the mounted image (e.g.
  `…\Windows\System32\config\SOFTWARE`) into the host's `HKLM` under a temporary key,
  edits it, then must unload. Loading under a host-owned key name risks colliding with real
  host hives; failing to unload leaks host-registry handles for the life of the process.
- **Decision:** `OfflineRegistryService.LoadHive` validates the requested hive name against
  `^WinForge_[A-Za-z0-9_]{1,80}$` and the file's existence, then loads under
  `HKLM\<WinForge_BASE>` via the Win32 `RegLoadKey` P/Invoke. **All** hive access is wrapped
  so `UnloadHive` runs in a `finally` block (and sets `IsLoaded = false` there) — a hive is
  never left loaded. Only the known bases `SOFTWARE` / `SYSTEM` / `DEFAULT`
  (`OfflineHivePaths.IsKnownBase`) may be loaded; host hives are never touched. `SetValue` /
  `DeleteValue` / `GetValue` / `EnumSubKeys` operate only under the loaded handle.
- **Consequences:** No host-hive name collision, no leaked registry handles, and offline
  edits are isolated to the working image and always released — even when an operation fails
  or throws.

## ADR-024: Host-system safety guards (path confinement + session binding + pre-execution critical stop)

- **Status:** ACCEPTED
- **Context:** Multiple independent isolation layers are needed; no single guard is
  sufficient to keep the host safe when the tool runs elevated.
- **Decision:** Execution is preceded by a **critical-stop guard** that fails the whole
  `CustomizationResult` as `CriticalFailure` unless **all** hold: (a) the servicing
  workspace `State` is `Mounted`; (b) `MountIdentityValidator.MatchesSession` is true (the
  mount directory and the working image both live under the workspace's `WorkingDirectory`
  for the same session); (c) `MountIsRegisteredAsync` confirms DISM registered the mount;
  (d) the plan `Status` is `Validated`. `MountIdentityValidator.IsWithinMount` refuses any
  path that is not strictly under the mount root — no host path, no source-ISO root, and no
  arbitrary command / registry / filesystem delete is ever issued by the engine. Operations
  classified `Protected` / `Unsupported` are blocked at validation; the package allowlist
  (ADR-022) prevents non-allowlisted removals; discovery refuses to run when
  `MatchesSession` is false.
- **Consequences:** Defense-in-depth: a session mismatch, a missing/unregistered mount, or
  an unvalidated plan cannot cause host changes, and the engine never emits an unconstrained
  filesystem/registry/command operation.

## ADR-025: Dirty / commit-discard separation — Step 3.3 applies but does not unmount or commit

- **Status:** ACCEPTED
- **Context:** Step 3.2 owns the prepare / mount / unmount / discard lifecycle (ADR-019).
  Step 3.3 must apply customizations to the mounted working image without taking over that
  lifecycle or silently committing changes the user did not choose to keep.
- **Decision:** Execution applies the (validated, frozen) operations to the mounted working
  image and then **LEAVES THE IMAGE MOUNTED** — it issues no `/Unmount-Image`, no `/Commit`,
  and no `/Discard`. The plan transitions `Executing` → `Completed` / `CompletedWithErrors`;
  `IAppState` tracks a "dirty" flag (customizations were applied to the working image this
  session) so the UI can warn before an unmount/discard. Reverting applied changes is **out
  of scope** for Step 3.3 — the user reverts by discarding the working image (Step 3.2
  unmount/discard) and re-preparing, which preserves a clean, auditable baseline.
  `CustomizationResult` records per-operation status, `FailedOperations`, and a
  human-readable `Summary`; the structured log records what was applied.
- **Consequences:** A clean ownership boundary between Step 3.2 (mount lifecycle) and Step
  3.3 (apply); no silent commit; the user always decides whether to keep or discard applied
  customizations, and the conservative default (discard the working image) is always
  available.

## ADR-026: Real-desktop validation defects — discovery must surface failure, not silence it

- **Status:** ACCEPTED
- **Context:** The first real-desktop validation of Step 3.3 (genuinely mounted Windows 11
  Professional working image) exposed three defects: (1) provisioned-Appx discovery returned
  **0 apps**; (2) offline service discovery returned **0 services**; (3) a non-allowlisted
  package (`Microsoft-OneCore-ApplicationModel-Sync-Desktop-…`) was user-selectable. Root
  causes found and fixed:
  - **DEFECT 1 (Appx = 0):** `DismAppxParser` only recognised the invented multi-word key
    "Deployment package name"; real `dism /Get-ProvisionedAppxPackages /English` emits
    **single-word** headers `PackageName` / `DisplayName`. Mismatched keys dropped every
    identity. Separately, `RunDismAsync` discarded the DISM exit code and stderr, so a DISM
    failure or unexpected/localized output was indistinguishable from a genuine zero.
  - **DEFECT 2 (Services = 0):** `RegLoadKey`/`RegUnLoadKey` require `SeRestorePrivilege` /
    `SeBackupPrivilege`, which are present in an elevated token but **disabled by default**.
    Without enabling them, hive load fails on a real elevated session. The prior code also
    swallowed any registry exception and returned an empty inventory ("0 services").
  - **DEFECT 3 (unsafe selection):** see ADR-027.
- **Decision:**
  1. `DismAppxParser` / `DismPackageParser` now accept both the real single-word headers
     (`PackageName`, `DisplayName`) and the legacy spaced forms, and each exposes
     `IsRecognizedOutput` to detect genuine DISM output (English banner or a known key).
  2. `WindowsCustomizationDiscoveryService.RunDismAsync` **checks the DISM exit code and
     stderr** and throws on failure; it also throws when output is not recognizable (e.g.
     `/English` not honoured → localized text). The mount path is redacted from any logged
     error so no sensitive filesystem location leaks.
  3. `DiscoveryInventory` now carries per-source `AppxStatus` / `PackageStatus` /
     `ServiceStatus` (`Success` / `Failed` / `NotAttempted`) plus an error string. A failed
     DISM call or a failed offline hive load is reported as `Failed` — **never** collapsed
     into a misleading "0 discovered". `ComponentsViewModel` surfaces these errors in the
     status message.
  4. `OfflineRegistryService` enables `SeRestorePrivilege` / `SeBackupPrivilege` on the
     process token (best effort) before each `RegLoadKey` / `RegUnLoadKey`.
  5. `WindowsCustomizationDiscoveryService.DiscoverServices` reports a hive-load / enumeration
     failure as `ServiceStatus = Failed` (the SYSTEM hive path, `Select\Current` resolution,
     and `ControlSet00x\Services` enumeration are unchanged and correct).
- **Consequences:** "Success with zero items" is now provably distinct from "command/registry
  failure". The UI can tell the user exactly which source failed and why, instead of showing a
  false "0". RegLoadKey works on a real elevated session, so offline service discovery returns
  the genuine service set. Step 3.3 real-desktop validation remains **PENDING** (these fixes
  are code-level; they must be re-validated on a real mounted image).

## ADR-027: One package-removal policy governs discovery, validation, and execution

- **Status:** ACCEPTED
- **Context:** DEFECT 3 — a non-allowlisted Windows package reached the UI as selectable
  (classified `Removable` merely because it was a "Feature"), even though execution would have
  `Skipped` it. The safety policy was split: the UI offered something execution would refuse,
  which is exactly the mismatch to avoid.
- **Decision:** The removal allowlist now lives in a single source of truth,
  `PackageRemovalPolicy` (`AllowedPackageMarkers`: `Microsoft-Windows-InternetExplorer-Optional`,
  `Microsoft-Windows-Printing-XPSServices`, `Microsoft-Xps-Document-Writer`). The **same**
  policy is enforced at three layers:
  1. **Discovery / classification** — `DismPackageParser.DeriveRisk` returns `Protected` for
     any package not on the allowlist, so it is **not selectable** in the UI (checkbox
     `IsEnabled = CanSelect = false`).
  2. **Plan validation** — `CustomizationPlan.RecomputeValidation` already flags a `Protected`
     selected operation as `Unsupported`, which blocks `Validate()`; `PlanSync.Toggle` also
     refuses to add a `Protected`/`Unsupported` operation even if called directly.
  3. **Execution** — `WindowsCustomizationExecutionService` retains `PackageRemovalPolicy`
     as the final defense-in-depth guard (a non-allowlisted operation is `Skipped`).
  Everything not on the allowlist (language, core, driver, servicing-stack, OneCore, edition
  packages) is `Protected` at every layer. Allowlisted packages remain `Removable` and
  selectable only because policy explicitly approves them.
- **Consequences:** The UI can never offer — and the plan can never carry — a package removal
  that execution would refuse. The policy is defined once and referenced thrice, eliminating
  the classification/execution mismatch. Step 3.3 real-desktop validation remains **PENDING**.

## ADR-028: Offline service discovery must never report a silent "0 services"

- **Status:** ACCEPTED
- **Context:** Re-investigation of DEFECT 2 after real-desktop evidence confirmed the SYSTEM
  hive file is present and readable (9,175,040 bytes at
  `<mount>\Windows\System32\Config\SYSTEM`). That rules out the originally-suspected "missing /
  wrong hive path" cause. The remaining silent-zero risk was a **successfully-loaded** hive
  whose resolved `ControlSet00x\Services` enumeration returned empty — `DiscoverServices` then
  returned `DiscoverySourceStatus.Success` with 0 items, collapsing a mis-resolved / unexpected
  hive structure into a misleading success.
- **Decision:** Two changes close the gap:
  1. **Empty-Services guard** — after resolving the ControlSet and enumerating
     `ControlSet00x\Services`, an empty result is now treated as `DiscoverySourceStatus.Failed`
     with an explicit error, never a successful "0 services". A real Windows SYSTEM hive always
     has service sub-keys under the current ControlSet, so an empty enumeration indicates a
     mis-resolved ControlSet or unexpected structure that must surface.
  2. **Diagnostics** — `OfflineRegistryService` now takes `ILoggerService` and logs the
     full hive-load / unload lifecycle so any real-desktop failure is observable: the (redacted)
     hive file path, the WinForge-owned temporary HKLM name, `SeRestorePrivilege` /
     `SeBackupPrivilege` enablement outcome (including `ERROR_NOT_ALL_ASSIGNED`), the
     `RegLoadKey` and `RegUnLoadKey` return codes, the resolved ControlSet, and the service
     count. The mount-root prefix is redacted (`<mount>`) and no host-registry data is logged,
     preserving the host-system safety boundary.
- **Consequences:** A load or enumeration failure (privilege, return code, missing Services
  sub-key) always surfaces as an explicit discovery error. Combined with the prior
  `ServiceStatus = Failed` on hive-load throw, "0 services" can no longer masquerade as success.
  Step 3.3 real-desktop validation remains **PENDING** — RE-RUN required after this change.

## ADR-029: Appx removal identity must be the exact PackageName, never DisplayName

- **Status:** ACCEPTED
- **Context:** Independent real-desktop reproduction of DEFECT 1 confirmed `dism /English
  /Image:<mount> /Get-ProvisionedAppxPackages` **succeeds** and returns many provisioned
  packages, yet WinForge reported "Discovered 0 app(s)". The root mismatch: the Step 3.3 report
  described the parser as keying on the invented multi-word "Deployment package name", whereas the
  real `/English` output uses the **single-word `PackageName`** header (with `DisplayName` listed
  first). The live code already accepted `PackageName`, but the parser **doc comment**, the unit
  **fixtures**, and the historical ADR text still referenced the synthetic key — so the contract
  was ambiguous. The user also required an explicit guarantee that the destructive operation
  targets the exact `PackageName` identity (e.g.
  `Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt`), **never** the friendly `DisplayName`
  (`Clipchamp.Clipchamp`), and that four outcomes stay distinct: (a) valid command + packages
  found → `Success(N)`; (b) valid command + genuinely zero packages → `Success(0)` (legitimate,
  NOT an error); (c) command failure (exit ≠ 0) → `Failed`; (d) unrecognized/localized output →
  `Failed`. An unrecognized real DISM format must never silently become a successful empty
  inventory.
- **Decision:**
  1. **Fixtures reflect reality** — `DismAppxParserTests.Sample` and
     `WindowsCustomizationDiscoveryServiceTests.AppxOut` now contain REAL DISM output copied from
     the desktop test (Clipchamp, BingWeather, Windows.Photos): single-word `DisplayName` then
     `PackageName` headers, with `PackageName` as the full identity.
  2. **Identity is PackageName end-to-end** — `DismAppxParser` extracts `PackageName`;
     `ComponentsViewModel.SyncAppx` sets `TargetIdentifier = Package.PackageName`;
     `WindowsCustomizationExecutionService` issues
     `/Remove-ProvisionedAppxPackage /PackageName:"{TargetIdentifier}"`. `DisplayName` is
     display-only. A block lacking `PackageName` is dropped (never keyed by `DisplayName`).
  3. **Four-way outcome contract** — `RunDismAsync` enforces it: non-zero exit → throw →
     `Failed`; exit 0 but unrecognized output → throw → `Failed`; exit 0 + recognized output
     (English banner or a recognized key) → `Success`, with `Parse` yielding N or 0. Genuine
     zero (banner present, no `PackageName` blocks) is `Success(0)`, explicitly distinct from a
     parser failure.
- **Consequences:** The Appx discovery contract is unambiguous and proven by tests
  (`RemovalIdentity_IsExactPackageName_NotDisplayName`,
  `IsRecognizedOutput_True_ForGenuineZeroWithBanner`, `Discover_GenuineZeroAppx_IsSuccess_NotFailed`).
  A real Windows run will now discover the mounted image's provisioned packages by exact identity.
  Step 3.3 real-desktop validation remains **PENDING** — RE-RUN required after this change.

## ADR-030: Service inventory must separate DISCOVERED from USER-CONFIGURABLE

- **Status:** ACCEPTED
- **Context:** A real-desktop re-validation of Step 3.3 reported the GOOD results (47 Appx, 149
  packages, 699 services discovered; non-approved packages correctly disabled) but surfaced a NEW
  safety defect: the Components page exposed a **disableable checkbox for every one of the 699
  discovered service records**, including kernel / file-system drivers, performance and provider
  entries (e.g. `.NET CLR Data`, `.NET Data Provider for Oracle`, `.NET Memory Cache 4.0`),
  and other low-level `SYSTEM\ControlSet00x\Services` sub-keys. The UI's claim "Selecting a service
  disables it in the offline image" is unsafe: **discovery success does not mean every discovered
  key is safe to reconfigure.** The existing discovery code classified every service as
  `RiskClass.Removable` unconditionally, so all 699 became selectable.
- **Decision:**
  1. **New `ServiceClass` enum** (`Unknown`, `Driver`, `Protected`, `Configurable`,
     `RecommendedConfigurable`) separates what was merely *discovered* from what is
     *user-configurable* by this step.
  2. **New `ServiceConfigPolicy`** (single source of truth, in Core so discovery, plan validation,
     and execution all share it) — only `DiagTrack`, `WerSvc`, `PcaSvc` (the existing trusted
     `CustomizationDefinitionProvider` recommended changes) may be reconfigured. A unit test pins
     the policy's markers to the provider's recommended service names so they cannot drift.
  3. **Classification inspects `Type`** — `WindowsCustomizationDiscoveryService.DiscoverServices`
     reads each service's `Type`; kernel / file-system / adapter driver types
     (`SERVICE_KERNEL_DRIVER` 0x1, `SERVICE_FILE_SYSTEM_DRIVER` 0x2, `ADAPTER` 0x4,
     `RECOGNIZED_DRIVER` 0x8) are classified `Driver` (protected); Win32 services that match the
     trusted allowlist become `RecommendedConfigurable`; everything else is `Protected`. The
     recommended start type is taken from the trusted definition.
  4. **UI gating** — `ServiceSelectionItem.CanSelect` is driven by `ServiceClass` (only
     `RecommendedConfigurable`/`Configurable` are selectable); non-selectable entries show a short
     reason ("Kernel / file-system driver…", "Not an approved service…", "Unknown service type…").
     `ComponentsViewModel` shows **only configurable services by default**; a `ShowProtectedEntries`
     toggle reveals the protected/system entries read-only. The status message reports the true
     discovered total and how many are hidden.
  5. **Three-layer guard** — (a) `PlanSync.Toggle` refuses to add a `ConfigureOfflineService`
     operation for any unapproved service name even if `Risk` is crafted to `Removable`; (b)
     `CustomizationPlan.ClassifyBase` flags an unapproved service op `Unsupported`, so plan
     validation rejects it; (c) `WindowsCustomizationExecutionService.ApplyService` retains a final
     `Skipped` guard for any non-allowlisted service that somehow reaches execution. The host
     SYSTEM-hive boundary (`IsWithinMount` + `OfflineHivePaths`) remains intact.
- **Consequences:** The Components page will no longer expose 699 arbitrary disableable service
  entries — only the trusted allowlist is configurable, and the hundreds of driver / kernel /
  protected records are discovered for diagnostics but hidden and non-selectable. 12 regression
  tests pin the boundary. Step 3.3 real-desktop validation remains **PENDING** — RE-RUN required
  after this change; on the real desktop the service count shown should drop from 699 to the small
  trusted-allowlist set.

## ADR-031: Offline registry writes must be verified by read-back, never trusted on no-throw

- **Status:** ACCEPTED
- **Context:** A real-desktop run of a 3-operation plan (remove Microsoft.BingWeather, disable
  DiagTrack, turn off advertising ID) reported "3 succeeded, 0 failed", yet an independent
  `reg.exe` check found the advertising-ID value **absent** from the offline `SOFTWARE` hive
  (`reg query …\AdvertisingInfo /v Enabled` → "The system was unable to find the specified
  registry key or value"). BingWeather removal and DiagTrack disable were independently confirmed
  correct; only the registry write was silently wrong. Two distinct root causes were identified:
  1. **Wrong key path in the trusted definition** — `privacy.advertising-id` targeted
     `Microsoft\Windows\CurrentVersion\Advertising\Id`, but the real Windows key is
     `Microsoft\Windows\CurrentVersion\AdvertisingInfo` with value `Enabled` directly under it. The
     write *succeeded at the wrong location*, so the expected path was empty.
  2. **Weak success contract** — `WindowsCustomizationExecutionService.ApplyRegistry` returned
     `Succeeded` solely because `OfflineRegistryService.SetValue` / `DeleteValue` did not throw.
     There was **no post-write read-back verification** of existence, type, or value, so a write to
     the wrong location (or any silent non-persistence) was reported as success.
  - The reporter's hypothesis of a duplicated `SOFTWARE\SOFTWARE` prefix
    (`HKLM\WinForge_SOFTWARE\SOFTWARE\Microsoft\…`) was **investigated and ruled out**: the write
    chain passes `op.RegistryKeyPath` **relative to the loaded hive root** (`HKLM\WinForge_SOFTWARE`),
    so no duplicated base was ever produced. A guard was added anyway so the class of bug cannot
    arise in future.
- **Decision:**
  1. **Fix the definition** — `privacy.advertising-id` →
     `Microsoft\Windows\CurrentVersion\AdvertisingInfo` (value `Enabled`, `DWord` `0`). All other
     5 Privacy + 3 System definitions were audited and are correctly relative to the `SOFTWARE` hive
     root with no `SOFTWARE\` prefix (hive-prefix consistent).
  2. **Verify-and-report contract** — after every `SetOfflineRegistryValue`, the engine performs an
     independent `OfflineRegistryService.ReadValue` read-back and confirms the value **exists**, has
     the **requested `OfflineRegistryValueKind`**, and **equals the requested data**; otherwise the
     operation is reported `FailedRecoverable`. `DeleteOfflineRegistryValue` verifies the value is
     **absent** afterward (delete-no-op → `Failed`). The production `OfflineRegistryService` also
     self-verifies on write/delete (throws `InvalidOperationException` on any mismatch) as
     defense-in-depth; `ReadValue` returns `{ Exists = false }` for a missing key/value.
  3. **Path-prefix guard** — new `OfflineHivePaths.NormalizeKeyPath(hiveBase, keyPath)` strips a
     leading `HKLM\` designator and any leading hive-base segment (`SOFTWARE\`, `SYSTEM\`) so a key
     path is always strictly relative to the loaded hive root. Applied in both the execution engine
     and the production registry service; idempotent for already-relative paths.
  4. **Host isolation preserved** — write targets remain confined to the mounted workspace via
     `OfflineHivePaths` + `IMountIdentityValidator`; an unknown/absolute hive base (e.g. `HKLM`) is
     rejected before any write; `SafeHiveNameRegex` and `Validate` (".." / leading `\`) remain.
- **Consequences:** A registry operation can no longer be reported successful when an independent
  read-back would fail. The advertising-ID value now targets the real Windows key and will be
  confirmed present (DWORD `0`) after apply. 17 new regression tests (`OfflineRegistryContractTests`)
  pin the contract: root-relative mapping for both hives, no duplicated `SOFTWARE\` prefix, DWORD &
  String read-back verification, create-missing-subkey, write-failure / wrong-value / wrong-type /
  persists-but-read-back-missing all → Failed, delete-then-verify-absent (+ delete-no-op → Failed),
  host-style hive base rejected, path-outside-mount rejected, the real definition maps to the correct
  location, and "never report success when read-back would fail". Total suite: **259** tests
  (Core 37, App 222), 0 errors, 0 warnings (Release). Step 3.3 real-desktop validation remains
  **PENDING** — RE-RUN required after this change.

## ADR-032: The Wizard (sequential Stepper) is the primary workflow surface

- **Status:** ACCEPTED
- **Context:** Through Phase 1 the shell used a left "feature list" navigation
  (Home / Image / Components / Experience / Privacy / System / Build / Logs / Settings). With
  Step 3.3 the offline customization engine is complete, but free navigation among feature pages
  invites misconfiguration — e.g. applying before validating, or customizing before the working
  image is mounted. A guided, gated path is safer and matches how users actually build an image.
- **Decision:**
  1. The primary surface is a 6-step **Stepper**: Source → Prepare → Customize → Review → Apply →
     Build (zh: 选择镜像 → 准备镜像 → 自定义 → 审核计划 → 应用修改 → 构建镜像). The left feature-list
     nav is retired as the primary control.
  2. Each step carries a `WorkflowStepState` (NotAvailable / Available / Current / Completed /
     RequiresAttention) computed **purely from `IAppState`** by
     `WorkflowViewModel.RecomputeStates()` — the workflow code contains **no DISM** (ADR-020…ADR-025
     safety boundaries stay in Infrastructure/execution).
  3. Navigation is gated: `CanGoNext`/`CanGoBack` derive from the current step's state;
     `CanGoToStep(step)` refuses a target that is `NotAvailable` **or** that would skip an earlier
     `NotAvailable` step (skip-guard), so users cannot jump ahead past an unmet prerequisite.
  4. Safety guards: a source change invalidates `CurrentCustomizationPlan` + `DiscoveredInventory`
     unless the plan is `Executing`; an active mount blocks source/prepare back-navigation; an
     `Executing` plan blocks source change.
  5. Step content is plain MVVM — Views are selected by `WizardStepTemplateSelector` data templates.
     Source and Prepare deliberately host the **same** `ImageViewModel` instance (selection vs
     servicing), keeping one source of truth for the selected image.
- **Consequences:** The app opens into the Workflow; Review/Apply/Build are unreachable until
  their prerequisites are met, and Customize is unreachable until the image is mounted. ~21 test
  facts across the WORKFLOW / COMMANDS suites pin the initial state, readiness transitions, the
  skip-guard, source-change invalidation, and the "never auto-advances Current" invariant. Status:
  **IMPLEMENTED / REAL DESKTOP VALIDATED / COMPLETED** (2026-08-09); merged to `main` via `--no-ff`.
  ADR-033…ADR-037 share this COMPLETED status.

## ADR-033: Utility navigation is separate from the workflow

- **Status:** ACCEPTED
- **Context:** Home / Logs / Settings / About are orthogonal to the customization workflow and must
  always be reachable without disturbing step state or gating. Promoting them to workflow steps
  would pollute the step-state machine and the review/apply guard rails.
- **Decision:**
  1. `MainViewModel` hosts the `WorkflowViewModel` as the primary surface and a small **utility
     rail** of `NavItem`s (Home / Logs / Settings / About) resolved via
     `ResolveUtility(PageKey)`. `IsWorkflowActive` toggles between the workflow surface and the
     utility surface; the left rail shows one "Workflow" button plus the utility items.
  2. The legacy `INavigationService.CurrentPageChanged` event is **translated** onto the new shell
     so old deep-links still work: utility pages (Home/Logs/Settings/About) are shown directly; the
     old feature pages jump into the matching workflow step — `Image`→Source,
     `Components`/`Privacy`/`System`/`Experience`→Customize, `Plan`→Review, `Build`→Build — via
     `_workflow.GoToStep(...)`.
  3. Workflow state is preserved across utility navigation: opening Settings/About/Logs and
     returning leaves the current step, plan, and mount untouched.
- **Consequences:** Step gating is never bypassed by the utility rail, and deep links from the
  Home page continue to drive the correct step. The removed feature-list nav no longer competes
  with the stepper as the primary control.

## ADR-034: Localization architecture — neutral .resx + zh-CN satellite, ResourceManager, Loc key

- **Status:** ACCEPTED
- **Context:** The app must ship English and Simplified Chinese with a runtime switch. Hard-coded
  English strings in XAML/code would make that impossible and would exclude zh-CN users.
- **Decision:**
  1. Every user-facing string lives in `Strings.resx` (neutral, en). `Strings.zh-CN.resx` is a
     satellite that **mirrors every key** with a non-empty value (parity is deterministic and is
     asserted by a test).
  2. `ResourceManagerLocalizationService` (App) wraps a `ResourceManager`; the live service is
     exposed to XAML as `Application.Current.Resources["Loc"]` (an `ILocalizationService`).
  3. `LocKeyMultiConverter` (`locKey`, an `IMultiValueConverter`) takes a key **and** the Loc
     service and returns the localized string, so a binding re-evaluates on BOTH a key change and a
     culture change. All shell/workflow strings bind through it as a `MultiBinding`.
  4. `ILocalizationService` is defined in **Core** (`WinForge.Core.Services`) so non-UI code can
     localize without an App dependency (e.g. `ImageViewModel.L(key, fallback)`).
  5. The indexer falls back **English → key** when a resource is missing, so an untranslated key is
     always visible rather than blank.
- **Consequences:** Strings are centralized; XAML never hard-codes text; adding a language means
  adding a satellite + a store entry. A parity test asserts zh-CN has a non-empty value for every
  en key, and a switch test asserts the live Loc returns the zh-CN value after `SetCulture`.

## ADR-035: Runtime language switching + persistence + English fallback

- **Status:** ACCEPTED
- **Context:** Users must be able to switch display language at runtime (Settings page) and have the
  choice persist across launches, defaulting to a known-safe language.
- **Decision:**
  1. `ILocalizationService.SetCulture(CultureInfo)` updates the current thread's
     `CurrentCulture`/`CurrentUICulture` and the `ResourceManager`, then raises
     `PropertyChanged("Item[]")` and `CultureChanged` so every `locKey` binding refreshes live —
     no restart required.
  2. `ILanguageSettingsStore` (Core) persists the choice (`SaveCulture` / `LoadCulture`); both an
     `InMemoryLanguageSettingsStore` (tests) and a `FileLanguageSettingsStore` (app) are provided.
  3. `LocalizationBootstrap.Initialize(service, store)` loads the saved culture (or a default),
     resolves it against the supported set, and applies it. An unknown or unsupported saved value
     resolves to a **shipped** language (`en` or `zh-CN`) — never a fabricated culture.
  4. English is the ultimate fallback for any missing key (ADR-034) **and** the ultimate default
     culture when nothing is saved.
- **Consequences:** Switching language in Settings applies instantly app-wide; the choice survives
  a restart. A test pins "invalid saved culture resolves to a supported language", and a bootstrap
  test pins "Initialize uses the saved culture".

## ADR-036: Friendly metadata preserves the immutable technical identifier

- **Status:** ACCEPTED
- **Context:** Users need human-readable names for services and apps, but the customization engine
  and the logs must keep the **exact technical identifier** (e.g. `DiagTrack`,
  `Microsoft.BingWeather`) — never the friendly name — as the operation target (see ADR-029: the
  destructive identity must be the exact PackageName/ServiceName).
- **Decision:**
  1. `ISelectableItem` carries both `FriendlyName` (display) and `TechnicalId` (the immutable
     identifier). The UI shows the friendly name **and** always shows the technical id (e.g. a
     sub-line), and the plan operation is built from `TechnicalId`.
  2. `FriendlyMetadataProvider` maps a trusted allowlist to `.resx` keys
     (`Svc.DiagTrack.Name`/`Svc.DiagTrack.Desc`, `App.Microsoft.BingWeather.Name`/`…Desc`, …). An
     unknown identifier returns the **raw name** (no fabrication, no silent translation). Lookups
     are case-insensitive and tolerate version-suffixed identities.
  3. `ServiceConfigPolicy` (Core, ADR-030) remains the single source of truth for *which* services
     are configurable; friendly metadata is presentation-only and never relaxes that boundary.
  4. Localized friendly metadata is provided for en + zh-CN through the same resx foundation
     (ADR-034), so a zh-CN user still sees the canonical `DiagTrack` id while reading a Chinese
     description.
- **Consequences:** Users see e.g. "Diagnostic Tracking Service (DiagTrack)" — readable yet
  auditable — while execution targets `DiagTrack`. Tests cover known/unknown identifiers,
  case-insensitivity, version-suffixed ids, and zh-CN localized friendly text.

## ADR-037: ComponentsViewModel selection→plan resync must be re-entrancy-safe

- **Status:** ACCEPTED
- **Context:** Toggling a selection calls `PlanSync.Toggle`, which mutates
  `IAppState.CurrentCustomizationPlan` and raises `PropertyChanged`. `ComponentsViewModel
  .OnAppStateChanged` then runs `ResyncSelections()`, which would reset the **just-toggled** item
  back to the (still empty) plan state — leaving the checkbox and the plan out of sync and breaking
  deselect.
- **Decision:**
  1. A `_suppressPlanResync` flag guards `OnSelectionChanged`: while a selection toggle is applying
     its plan change, `ResyncSelections()` is skipped, because the UI item is already the source of
     truth in that path.
  2. `OnAppStateChanged` only calls `ResyncSelections()` when the plan change did **not** originate
     from a local toggle, so external plan resets (e.g. a source change clearing the plan) still
     re-sync the checkboxes.
  3. `Refresh()` raises `CanExecuteChanged` on `DiscoverCommand` after every state transition,
     continuing the ADR-025 / Step 3.2 pattern of **explicit** command notification (no
     `CommandManager.RequerySuggested`).
- **Consequences:** Selection state and the plan stay consistent; the first selection no longer
  self-cancels; source-change resets still reflect in the UI; the Discover button enables/disables
  on the live `CanDiscover` predicate via an explicit raise.

## ADR-038: Build / ISO Export pipeline — one orchestrator with an explicit terminal-state machine

- **Status:** ACCEPTED
- **Context:** Phase 10 must turn the isolated, customized working image (produced by Step 3.2/3.3)
  into a bootable Windows ISO. The Build Wizard step was an honest placeholder (ADR-032), so no ISO-
  rebuild engine existed. The pipeline must be cancellable, must **never** modify the source ISO, and
  must report a single authoritative terminal state (Completed / Failed / Cancelled) — success must
  never be derived from a per-stage flag or from a non-zero exit code that was "close enough".
- **Decision:** `IBuildService` (Core) + `ImageBuildService` (Infrastructure) is the single
  orchestrator. It drives an explicit state machine through
  `Preflight → CommittingImage → ExportingImage → PreparingMedia → BuildingIso → Verifying →
  Completed / Failed / Cancelled`, delegating to focused sub-services behind Core interfaces:
  `IImageServicingService.CommitUnmountAsync` (commit the customized working image), `IWimExporter`
  (export to a clean install.wim), `IIsoMediaPreparer` (copy the original media tree and replace the
  payload), `IBootableIsoBuilder` (oscdimg), and `IBuildVerifier` (independent verification). The
  terminal `BuildState` is the **authority** for success: `BuildResult.Success` is true **only** when
  the ISO was produced, verified, and (if needed) moved to the final path. The orchestrator records
  the in-flight phase + key paths to `build.recovery.json` via a `.partial` file that is atomically
  renamed into place, so a crash leaves a detectable recovery record.
- **Consequences:** The pipeline is unit-testable end-to-end via fakes; cancellation can stop at any
  `await`; the source ISO is read-only (ADR-004/ADR-019); the UI can never show success for a
  failed/cancelled build. The orchestrator contains no DISM/oscdimg calls itself — only coordination.

## ADR-039: Build commits the working image — it never discards

- **Status:** ACCEPTED
- **Context:** Step 3.3 applied customizations to the mounted working image and **left it mounted**
  (ADR-025); Step 3.2 unmounts with `/Discard`. Build is the moment those customizations must be
  persisted into the final ISO. An unmount/discard at build time would silently throw away the user's
  customizations — exactly the data loss ADR-008 forbids.
- **Decision:** The build path calls `IImageServicingService.CommitUnmountAsync` (DISM
  `/Unmount-Image /Commit`). If the commit fails, the build **stops immediately** — no ISO is produced
  and the workspace is left recoverable (the working image and log remain for diagnosis). `/Discard`
  is **never** used on the build path. Because the commit targets only the WinForge-owned working
  image, ADR-019 (the source ISO is never modified) still holds.
- **Consequences:** User customizations are never silently lost; a commit failure is a hard stop with
  a clear error rather than a silent discarding; the working image lifecycle is owned by the build
  orchestrator at this one boundary (Step 3.2 still owns prepare/mount/unmount/discard during
  servicing).

## ADR-040: Export to a clean install.wim; ESD sources normalized to WIM at index 1

- **Status:** ACCEPTED
- **Context:** The committed working image must become the media payload. A working WIM produced by
  Step 3.2 already sits at index 1, but the final `install.wim` should be a fresh, optimized image
  rather than a reused servicing WIM. For an **ESD** source the original `install.esd` must be
  replaced by a WIM so Windows Setup reads the payload correctly.
- **Decision:** `DismWimExporter` runs
  `DISM /Export-Image /SourceImageFile:<working> /SourceIndex:<workingIndex> /DestinationImageFile:<clean.wim> /Compress:max /CheckIntegrity`.
  The pipeline then passes the clean WIM to `IIsoMediaPreparer`, which copies the original ISO media
  tree **read-only** and replaces the payload: for a WIM source it overwrites `sources\install.wim`;
  for an ESD source it **deletes** `sources\install.esd` and writes `sources\install.wim`. The final
  install.wim is always at index 1. The export verifies the destination file exists before reporting
  success.
- **Consequences:** The rebuilt ISO is structurally faithful to the source; both WIM and ESD sources
  yield a WIM payload at index 1; the original `install.esd` is never carried into the output. The
  source ISO is untouched (the media tree copy is written to a WinForge-owned build workspace).

## ADR-041: ISO creation uses oscdimg (Windows ADK); dual-boot, never faked

- **Status:** ACCEPTED
- **Context:** The output must be a bootable ISO supporting both legacy BIOS and UEFI. The Windows
  ADK `oscdimg.exe` is the documented Microsoft tool for building a UDF/ISO-9660 image with a
  boot catalog. When the ADK is missing the build must **clearly report the requirement** rather than
  fabricate a non-bootable ISO.
- **Decision:** `IBootableIsoBuilder` (Core) + `OscdimgIsoBuilder` (Infrastructure). `IAdkToolLocator
  .FindOscdimg()` resolves the ADK path; if it is missing, `OscdimgIsoBuilder` returns `ToolMissing`
  and the pipeline aborts the build with a clear "ADK required" message (the UI surfaces `AdkMissing`
  up front, before the user can start). The dual-boot command is assembled by
  `OscdimgArgumentBuilder.Build`:
  `-bootdata:2#p0,e,b"<etfsboot.com>"#pEF,e,b"<efisys.bin>" -m -o -u2 -udfver102 "<mediaRoot>" "<outputIso>"`.
  The builder verifies `boot\etfsboot.com` and `efi\microsoft\boot\efisys.bin` exist in the media
  tree **before** invoking oscdimg; if either is missing the build fails with a clear error and never
  produces a non-bootable ISO. The output file's existence is re-checked after the tool runs.
- **Consequences:** The bootable output is real and verifiable; there is no fake/placeholder ISO; a
  missing ADK or a missing boot file fails fast and clearly; Core stays testable via a fake builder.

## ADR-042: Build UI — gated, stateful, never silently overwrites

- **Status:** ACCEPTED
- **Context:** The user picks the output directory and file name and must see live progress, the
  terminal state, the log, and the final path/size. An accidental overwrite of an existing ISO (or a
  previous build's output) must never happen silently, and the build must be cancellable.
- **Decision:** `BuildStepViewModel` derives every input from `IAppState` (working image, mount dir,
  source edition). `CanBuild` requires Applied + Mounted + not building + ADK present + non-empty
  output dir **and** file name. The default file name is
  `WinForge_<Edition>_<yyyyMMdd-HHmm>.iso` with the edition segment's spaces normalized to `_` and
  illegal filename characters sanitized (ADR-safe, never a silent overwrite). Overwrite behavior is
  explicit via `BuildOverwritePolicy` (Fail / GenerateUniqueName / Overwrite; default
  `GenerateUniqueName`). A cancellable `AsyncRelayCommand` drives `IBuildService`; `CancelCommand`
  cancels the `CancellationTokenSource`. The terminal `BuildState` and `OutputPath` /
  `OutputSizeBytes` / `LogText` flow from `BuildResult`, and the final stage is pinned explicitly
  (progress events are delivered asynchronously via `Progress<T>`, so the last one can arrive after
  the result). On success the servicing workspace transitions `Mounted → Prepared` (the image is now
  committed/unmounted); on cancel/ failure it reports the matching terminal state and discards any
  partial output.
- **Consequences:** The UI is always truthful about state; the user controls overwrite behavior;
  cancellation is deterministic; success/failure/cancel are mutually exclusive and never misreported.

## ADR-043: Independent build verification + interrupted-build recovery

- **Status:** ACCEPTED
- **Context:** A build that reports success must be independently trustworthy, not merely "the tool
  exited 0". And a crashed build must not leave a stale workspace that blocks or corrupts the next
  run.
- **Decision:** `IBuildVerifier` (Core) + `BuildVerifier` (Infrastructure) re-checks the output
  **independently of the builder**: the output ISO exists with non-zero size; `sources\install.wim`
  is present in the media tree; no WIM is still mounted (`dism /Get-MountedImageInfo` reports no mount
  directory); and the expected edition/index is present (`dism /Get-ImageInfo`). If verification
  fails, `BuildResult.Success = false` and the terminal state is `Failed` — the orchestrator never
  reports success on a failed verification. `ImageBuildService` also writes `build.recovery.json`
  (atomic `.partial` rename) recording the in-flight phase + key paths; `DetectInterruptedBuildAsync`
  + `CleanupInterruptedBuildAsync` let the next run detect and clean a leftover workspace before
  starting, so a crash never blocks or corrupts a subsequent build.
- **Consequences:** "Success" means a genuinely valid ISO; crash recovery is automatic and observable.
  Verification uses the real DISM tooling (not the builder's own memory), so a tool-level mismatch is
  caught. The recovery file is written atomically so it is never observed half-written.

## ADR-044: Final Build step — completion-gated Finish → Home, with a single navigation coordinator

- **Status:** ACCEPTED
- **Context:** Phase 10's Build step is the final wizard step. Two real-desktop defects surfaced and
  were closed here: (1) the final step still showed a **disabled "Next"** after a successful build
  (the wizard's `NextCommand`/`CanGoNext` is always false on the last step, so it rendered a dead
  button); (2) clicking the new Finish button did **nothing visible** — `WorkflowViewModel.Finish()`
  called `INavigationService.NavigateTo(PageKey.Home)`, but `INavigationService.CurrentPage` had
  initialized to `Home` and was never updated when the wizard was shown (the shell set
  `ActiveView = _workflow` directly, bypassing the navigation service). The two "coordinators" — the
  wizard surface driven directly by `MainViewModel`, and the utility pages driven via
  `INavigationService` — were desynced, so a Home→Home navigation short-circuited and the wizard never
  disappeared.
- **Decision:**
  1. **One coordinator.** `INavigationService` is the single source of truth for the visible surface.
     A new `PageKey.Workflow` (Core enum) makes the wizard a first-class navigation destination.
     `MainViewModel` no longer sets `ActiveView`/`IsWorkflowActive` directly for the workflow — it
     shows the wizard via `NavigateTo(PageKey.Workflow)` (constructor + rail button + commands) and
     `OnNavigated` handles `PageKey.Workflow` by showing the workflow without resetting the step.
     Utility pages (Home/Logs/Settings/About) continue to route through `NavigateTo(page)`.
  2. **Completion-gated Finish.** On the final step, the disabled "Next" is hidden and a localized
     **Finish** (`Nav.Finish` = `Finish` / `完成`) is shown, enabled only when
     `BuildState == Completed`. `CanFinish = IsFinalStep && BuildState == Completed`, so
     NotStarted/Building → unavailable; Completed → enabled; Failed/Cancelled → stay on Build and
     never present a successful Finish.
  3. **Finish is a clean navigation, never a teardown.** `WorkflowViewModel.Finish()` calls
     `_navigation?.NavigateTo(PageKey.Home)`. It **never** calls `Application.Shutdown()`, never
     deletes or dismounts the generated ISO, never touches logs or the workspace, and performs no
     remount/dismount. The app remains running so the user can view logs, open the output folder,
     start another workflow, or change settings.
  4. **Localization stays key-based.** All new strings (`Nav.Finish`, `Build.OpenOutputFolder`) live
     in `Strings.resx` + `Strings.zh-CN.resx` and bind through `Loc[key]`; no hard-coded language
     checks. `Open output folder` (`打开输出文件夹`) opens the folder containing the ISO and is
     enabled only when the output exists (`IFileLauncher` + `WindowsFileLauncher`, which swallows
     shell exceptions so it is headless/test-safe).
- **Consequences:** Finish is now a real, observable Workflow → Home transition (the shell's
  `ActiveView` becomes `HomeViewModel` and `IsWorkflowActive = false`), proven by shell-level
  integration tests that drive the real `MainViewModel` + real `NavigationService` (asserting the
  navigation fires exactly once, HomeView becomes current, the ISO and logs remain, and no
  dismount/remount occurs). Failed/Cancelled builds cannot Finish; zh-CN and en-US behavior is
  identical. The wizard and utility navigation are unified under one coordinator, eliminating the
  class of "navigated but nothing changed" desync defect. Status: **IMPLEMENTED / REAL DESKTOP
  VALIDATED / COMPLETED** (2026-08-10); merged to `main` via `--no-ff` on 2026-08-10.

## ADR-045: Component Intelligence — separate the DISCOVERED WINDOWS OBJECT from the COMPONENT DEFINITION

- **Status:** ACCEPTED (implemented in Stage 11.1; **REAL DESKTOP VALIDATED** 2026-08-10; not yet merged to `main`)
- **Context:** WinForge today shows package/app names (TechnicalTarget identities) to advanced
  users. Ordinary users cannot answer the four questions that matter: **WHAT is this component,
  WHETHER I need it, WHAT breaks if I remove it, HOW risky is removal, and whether it is
  restorable.** Phase 11 ("Component Intelligence Foundation") begins by teaching WinForge to
  *explain* components for ordinary users without ever offering a destructive removal in this
  stage. Two failure modes must be avoided: (1) **over-claiming** — presenting an invented
  description/risk for a component WinForge does not actually understand; (2) **conflating** the
  raw Windows object (a package/capability/feature/CBS package identity discovered by DISM) with
  the human-facing knowledge entry (a curated `ComponentDefinition`).
- **Decision:**
  1. **Two distinct models.** `IRawInventoryItem` (and its `RawAppxPackage` / `RawCapability` /
     `RawOptionalFeature` / `RawCbsPackage` subclasses) is the *discovered Windows object* — a
     typed capture of exactly what DISM reported (identity, display name, version, state, plus
     category-specific fields). `ComponentDefinition` is the *human knowledge entry* — a stable,
     WinForge-authored, localized description of WHAT/WHETHER/IMPACT/RISK/RESTORATION, plus
     `TechnicalTarget`(s) that map it onto raw identities. The UI depends on the logical
     `ComponentDefinition`, never on the raw identity.
  2. **Pure classification in Core.** `ComponentMatcher.BuildInventoryEntries(raw, catalog)`
     (Core, platform-agnostic, no DISM) maps raw items onto curated definitions, classifies the
     rest, collapses multiple raw identities that belong to one logical component, and surfaces
     curated definitions absent from the image as catalog-only rows. A raw item becomes
     `Curated` **only** when a catalog definition's `TechnicalTarget` actually matches it.
  3. **Four-way classification.** `ComponentClassification` = `Curated` (understood, human
     description + risk exist) · `DiscoveredUnclassified` (Windows object exists, WinForge has not
     classified it) · `Protected` (system-critical / servicing-sensitive / permanent — never
     offered) · `Unsupported` (present but WinForge does not service it this stage, e.g. Services,
     Scheduled Tasks, Drivers, Languages, WinRE, System Apps). `Protected` is driven by an explicit
     identity-marker allowlist (ServicingStack / Foundation / WinPE / Setup / Client-Desktop / …);
     `Unsupported` is driven by the explicit category allowlist for not-yet-serviced kinds.
  4. **Unknown is preferred over invented.** Every `ComponentListItem` getter that would otherwise
     guess resolves through `ILocalizationService` and falls back to the localized
     `Component.Unknown` caption (en "Unknown" / zh-CN "尚未确认"). Savings/risk/removal are left at
     `Unknown`/`None` rather than fabricated. The UI visibly says "Unknown / 尚未确认" for any
     missing field.
  5. **Read-only discovery only (Stage 11.1).** Four DISM enumerations are implemented and tolerant
     of per-source failure: `AppxInventoryParser` (`/Get-ProvisionedAppxPackages`),
     `CapabilityInventoryParser` (`/Get-Capabilities`), `OptionalFeatureInventoryParser`
     (`/Get-Features`), `CbsPackageInventoryParser` (`/Get-Packages`). Six further categories
     (Service / ScheduledTask / Driver / Language / WinRecovery / SystemApp) have *designed but
     not-yet-implemented* provider interfaces and are reported as `InventoryStatus.NotSupported`
     (never silently dropped). **No removal, no servicing, no DISM write** in this stage — the
     orchestrator `IComponentIntelligenceService.DiscoverAsync` returns structured
     `ComponentInventory` and never mutates the image.
  6. **Standard vs Advanced modes.** `ComponentIntelligenceViewModel.StandardMode` (default true)
     shows only `Curated` rows to ordinary users; Advanced mode additionally reveals raw discovered
     objects (read-only). Navigation is a single additive `PageKey.ComponentIntelligence` rail entry
     — no Phase-10 behavior is modified.
  7. **Localization stays key-based.** All new strings (`ComponentIntelligence.*`, `Comp.*`,
     `Component.*`, `Recommendation.*`, `Risk.*`, `Dependency.*`, `Removal.*`, `Restore.*`,
     `Savings.*`, `Classification.*`, `Category.*`, `ComponentScenario.*`) live in
     `Strings.resx` + `Strings.zh-CN.resx`; the curated catalog is generated by
     `.tmp/phase11/gen_catalog.py` from a `SHARED` table so regenerated keys stay in sync.
- **Consequences:** WinForge can now present an ordinary user with a human name, a short
  description, a recommendation (🟢 safe-remove / 🔵 optional / 🟠 usually-keep / 🟡 advanced-only /
  🔴 never-remove), a risk level, relevant scenarios, keep-if / remove-if / impact guidance,
  restoration info, and (collapsed) technical details — while NEVER hiding uncertainty. The 11
  initial curated components (Weather, Clipchamp, GetHelp, XboxApp, Photos, FeedbackHub, Maps,
  PhoneLink, Solitaire, Teams, OneDrive; Teams `RelatedTo` OneDrive — downgraded from `Requires`
  by ADR-046) are well-understood inbox AppX.
  The architecture (Core pure matcher + Infrastructure DISM parsers + App ViewModel/View, no DISM in
  ViewModels) preserves the layering rules. **491 automated tests pass (Core 53, App 438), 0 errors,
  0 warnings (Release)** — including Core matcher facts, Infrastructure parser/orchestrator tests
  (FakeProcessRunner / FakeMountIdentityValidator), ViewModel tests (discovery populates/filters,
  CanDiscover gating, culture-switch rebuild), and STA XAML-load regression tests for
  `ComponentIntelligenceView` (en + zh-CN + real-DataContext). Status: **REAL DESKTOP VALIDATED**
  (2026-08-10); on branch `phase/11-component-intelligence`; **NOT merged to `main`**; **Phase 11 is
  IN PROGRESS** (do not mark Phase 11 complete until Stage 11.2 lands). **Real Windows 11 25H2 zh-CN
  x64 Consumer ground-truth enumeration PASSED** (the blank-page defect is fixed; the page renders
  correctly): Curated **11**, DiscoveredUnclassified **734**, Protected **13**, Unsupported **0**.
  Representative real unclassified objects observed: `Microsoft.ApplicationCompatibilityEnhancements`,
  `Microsoft.AV1VideoExtension`, `Microsoft.AVCEncoderVideoExtension`, `Microsoft.BingNews`,
  `Microsoft.BingSearch`, `Microsoft.DesktopAppInstaller`, `Microsoft.GamingApp`. **PRODUCT
  CONCLUSION: the 734 discovered raw objects must NOT become 734 normal removal checkboxes** — they
  remain raw Windows identities, surfaced read-only in Advanced mode; Stage 11.2 (Component Catalog
  Expansion, NOT STARTED) turns representative families into user-understandable logical components
  with evidence-backed purpose/risk/keep-if/remove-if/impact/restore, Unknown staying Unknown until
  evidence-backed; no deep CBS removal; Protected never exposed for removal; no inferred
  dependencies.

---

## ADR-046: Stage 11.1 read-only audit — tighten Protected classifier & downgrade Teams→OneDrive

- **Status:** **IMPLEMENTED / PENDING REVIEW** (2026-08-10); on branch `phase/11-component-intelligence`;
  **NOT merged to `main`**.
- **Context:** Stage 11.1 passed architecture/build/tests, but the read-only audit of the two
  knowledge rules surfaced defects in `ComponentMatcher.ProtectedMarkers` and the curated
  `Teams → OneDrive` edge. The audit's governing rule: a generic substring must never auto-protect a
  broad family of unrelated CBS packages, and dependency edges must not be invented — "Requires" means
  removing the target breaks the dependent's supported core scenario.
- **Decision A — Teams → OneDrive downgraded `Requires` → `RelatedTo`:**
  - Evidence: modern Teams (`MicrosoftTeams_8wekyb3d8bbwe`) core chat / calls / meetings operate
    independently of OneDrive; OneDrive is used only for file storage/sharing of chat attachments.
    Removing OneDrive does NOT make Teams unable to operate its core scenario, so a hard `Requires`
    edge is unsupported and would wrongly block Teams removal at plan-validation time in Stage 11.2.
  - Action: `CuratedComponentCatalog.cs` and the generator `.tmp/phase11/gen_catalog.py` now declare
    the edge as `RelatedTo` with a reason stating it is NOT a hard runtime dependency.
- **Decision B — Protected classifier tightened to narrow, reviewable markers:**
  - Removed broad bare-word / parent-family markers that over-protected unrelated CBS packages:
    `ServicingStack` (bare), `Foundation` (bare), `Setup` (bare), `LanguagePack`, `Language` (bare),
    `Driver` (bare), `WinRE`, `Recovery` (bare), `Microsoft-Windows-Client`, `Client-Desktop`.
  - Kept only fully-qualified family strings, each tied to a specific, defensible protected family:
    `Microsoft-Windows-ServicingStack`, `Microsoft-Windows-Foundation`, `WinPE`,
    `Microsoft-Windows-Setup`, `Microsoft-Windows-Shell-Setup`, `Microsoft-Windows-LanguagePack`,
    `Microsoft-Windows-LanguageFeatures`, `Microsoft-Windows-LanguageOverlay`,
    `Microsoft-Windows-LanguageExperiencePack`, `Microsoft-Windows-Driver-`, `Microsoft-Windows-WinRE`,
    `Microsoft-Windows-Recovery`, `Windows-Recovery`, `Microsoft-Windows-Edition`.
  - Rationale: `Microsoft-Windows-Client` previously swept in EVERY `Microsoft-Windows-Client-*`
    package (Core, Desktop, Features, Professional, …) as Protected; `Client-Desktop` is a substring
    inside that same family. Per the audit, such objects must stay `DiscoveredUnclassified` rather
    than be falsely Protected — and in Stage 11.1/11.2 they are never offered as removable anyway
    (removal UI applies only to Curated AppX), so tightening is safe. If real-desktop validation
    proves a specific client sub-family is genuinely critical and currently slips through, add a
    narrow, evidence-backed marker rather than re-broadening.
  - Regression coverage added: `Protected_NarrowRule_BareFoundationNoLongerMatches`,
    `Protected_NarrowRule_ClientFamilyIsNotProtected` (Core/Desktop/Features theory),
    `Protected_NarrowRule_DriverFamilyStillProtected`, `Protected_NarrowRule_SetupFamilyStillProtected`,
    `Protected_NarrowRule_ServicingStackStillProtected`, plus `CuratedCatalog_TeamsDependsOnOneDrive_AsRelatedTo`.
- **Consequences:** 8 net-new tests (Core 53, App 428 → 481 total, 0 fail). **Build 0 errors / 0
  warnings (Release).** The classifier now protects only narrowly-scoped, reviewable families; the
  curated Teams relationship is honestly `RelatedTo`. Real-image numeric ground-truth is now
  **RECORDED** (Protected rule match counts + unclassified examples) — see
  `.tmp/phase11/real-25h2-inventory-report.md`: Curated **11**, DiscoveredUnclassified **734**,
  Protected **13**, Unsupported **0**; the 734 raw objects stay raw Windows identities, never removal
  checkboxes. Status: **REAL DESKTOP VALIDATED**; Stage 11.1 is REAL DESKTOP VALIDATED; Phase 11
  remains IN PROGRESS (Stage 11.2 NOT STARTED, not merged to `main`).

## ADR-047: Knowledge provenance — separate FACT from RECOMMENDATION; community opinion never auto-promotes to RecommendedRemove

- **Status:** **IMPLEMENTED** (2026-08-10); on branch `phase/11-component-intelligence`; **NOT merged
  to `main`**; Stage 11.2 PENDING REAL DESKTOP REVIEW.
- **Context:** Stage 11.2 imports curated + official + community knowledge to make components
  actionable for ordinary users. The risk: a community "debloat" list (e.g. Win11Debloat) is an
  *opinion* that a component is removable; if imported as a WinForge `RecommendedRemove` fact, WinForge
  would assertively tell users to remove something on the basis of an unvetted third-party script. That
  conflates evidence-backed WinForge curation with community opinion and creates a false-authority UX.
  Additionally, mixing a verified `Fact` (e.g. "Xbox Game Bar is a gaming capture tool") with a
  `Recommendation` (e.g. "you may remove it") in one field makes the UI unable to show *why* a
  recommendation exists or let the user weigh official vs community evidence.
- **Decision A — `Fact` and `Recommendation` are distinct claim kinds.** `KnowledgeClaim.Kind` is
  `Fact` or `Recommendation`. A `Fact` is a verified, non-opinion statement about the component
  (identity, purpose, what breaks if removed, restore availability). A `Recommendation` is opinionated
  remove/keep guidance carrying a `KnowledgeSource`. The UI surfaces `Fact`s as established knowledge
  and `Recommendation`s as guidance tagged by source. A `Recommendation` can **never** be upgraded into
  a `Fact`.
- **Decision B — community opinion never becomes WinForge `RecommendedRemove`.** `KnowledgeImportPipeline`
  ingests community adapters (`Win11DebloatCommunityAdapter`) as `CommunityProposal` candidates. A
  `CommunityProposal` is **never** promoted into `EffectiveRecommendation` and is **never** auto-promoted
  to `Curated`. It is surfaced only as *community evidence* in the Customize Component Knowledge tab
  (informational, clearly sourced). Only `WinForgeCurated` / `MicrosoftOfficial` / `WindowsImageDiscovery`
  claims may drive an official `EffectiveRecommendation` after review. Candidates never auto-promote to
  `Curated`; `Deprecated` entries are excluded on merge; merge is de-duplicated by target.
- **Decision C — provenance is per-claim and surfaced in the UI.** Every `Recommendation` carries its
  `KnowledgeSource` (Curated / MicrosoftOfficial / WindowsImageDiscovery / Community). The Customize
  Component Knowledge tab renders official-vs-community evidence separately and shows deterministic
  "why" captions, so the user can weigh sources. The Component Intelligence page remains the advanced
  inspection surface; the Customize tab is the ordinary-user primary path.
- **Consequences:** 39 new `ComponentKnowledgeStage11p2Tests` guard the separation (community
  `EffectiveRecommendation = Unknown` is not elevated; `PromoteToCurated` rejects community; candidates
  never auto-Curated; merge de-dup; `Deprecated` excluded). The product can never assert a removal it
  doesn't itself stand behind. Full suite **530 pass (Core 53, App 477), 0 errors, 0 warnings (Release)**.
  Stage 11.2 PENDING REAL DESKTOP REVIEW; Phase 11 remains IN PROGRESS; NOT merged to `main`.
  remains IN PROGRESS (Stage 11.2 NOT STARTED, not merged to `main`).

## ADR-048: Stage 11.2 UX rework — Component Intelligence is the BACKEND; the Apps tab is the PRIMARY decision surface; separate "Component Knowledge" tab removed

- **Status:** **IMPLEMENTED** (2026-08-10); on branch `phase/11-component-intelligence`; **NOT merged
  to `main`**; Stage 11.2 UX REWORK IMPLEMENTED; PENDING REAL DESKTOP REVIEW.
- **Context:** Real-desktop review of Stage 11.2 found the separate Customize "组件知识 / Component
  Knowledge" tab redundant and poorly presented: it duplicated the Component Intelligence data in a
  second, lower-quality surface and made the user leave the component list to decide whether to remove
  something. The requirements instead are: (1) keep Component Intelligence / knowledge as the **backend
  intelligence layer** for Customize, not a second tab; (2) the Apps tab must be the decision surface
  with columns 名称 | 作用 | 建议 | 风险, a hover quick card, and an explicit ⓘ 详情 detail
  view — the user makes the removal decision **without leaving the list**; (3) **hide raw Windows
  package identity** (e.g. `Microsoft.AV1VideoExtension_2.0.6.0_neutral_...8wekyb3d8bbwe`) in STANDARD
  mode (only hover card / detail panel / Advanced / the CI page may show it); (4) STANDARD mode shows
  only `Curated` logical components — never expose `DiscoveredUnclassified` / `Protected` / `Unsupported`
  / `Unknown` as removable rows, and never convert the 734 raw objects into 734 checkboxes; (5) reuse
  the existing knowledge engine rather than throw it away; (6) decision-oriented relabeling and sort.
- **Decision A — the knowledge engine is reused, not deleted.** `ComponentKnowledgeViewModel` /
  `ComponentKnowledgeView` / `ComponentKnowledgeItem` (the knowledge-backed curated table: sort, filter,
  hover card, detail panel) are the **single source of the curated UX**. They are repurposed as the
  Customize **Apps tab** by passing the shared `ComponentKnowledgeViewModel` singleton as the tab's
  `Content`. App.xaml already maps that VM → `ComponentKnowledgeView` via an implicit `DataType`
  DataTemplate, so no duplicate View/ViewModel is created. The former `CustomizeTabKind.Knowledge` enum
  value and the `CustomizeStepViewModel.Knowledge` property are removed; the Apps tab is index 0.
- **Decision B — the Apps row is 选择 | 名称 | 作用 | 建议 | 风险 + 详情 + 阻塞原因.** `ComponentKnowledgeView`
  row reordered accordingly; the standalone `Category` column is dropped from the row (category still
  appears in the hover card and the detail panel). Raw identity is absent from the row and hover card;
  it appears **only** in the collapsed `RawIdentities` detail section — satisfying "hide raw identity in
  standard mode". Selection flows to the plan via the same `appx|` op-ids `ComponentsViewModel` uses, so
  App selection drives the same `RemoveProvisionedAppx` plan operation.
- **Decision C — the left-rail "组件智能 / Component Intelligence" page is repositioned as
  "高级组件检查器 / Component Inspector"** (`Nav.ComponentIntelligence` + `ComponentIntelligence.Title`
  relabeled; `PageKey.ComponentIntelligence` unchanged to preserve navigation tests). It is the
  **advanced** inspection surface (raw, Advanced-only) that still shows raw identities in its collapsed
  Expander — consistent with "CI = raw/Advanced inspection". It is no longer framed as the primary
  ordinary-user decision surface.
- **Decision D — decision-oriented recommendation relabeling + sort.** `Recommendation.*` captions:
  `RecommendedRemove`→**推荐精简 / Recommended trim**, `OptionalRemove`→**按需精简 / Trim if wanted**,
  `UsuallyKeep`→**建议保留 / Recommended keep**, `AdvancedOnly`→**高级选项 / Advanced only**,
  `NeverRemove`→**不可移除 / Do not remove** (Unknown unchanged). Compact filter captions align
  (全部 / 推荐精简 / 按需精简 / 建议保留 / 高级选项 / 不可移除). Default sort = recommendation → risk →
  category → name; badges carry color **and** text so meaning never relies on color alone.
- **Decision E — progressive integration, no fake intelligence, foundation preserved.** Knowledge is
  integrated first for 应用 / Windows 组件 where the catalog is modeled; tabs not yet modeled (Windows
  components / Services) keep their current discovery-backed behavior and are **not** faked as
  intelligent. `ScenarioRecommendation` is preserved for future Profiles (not built now; no auto-select).
- **Consequences:** 25 regression tests guard the rework (no `Customize.Tab.Knowledge` header; Apps tab
  `Content` is the **same** `ComponentKnowledgeViewModel` instance via `Assert.Same`; raw identity hidden
  from curated `DisplayName`; `ShowDetailCommand` sets `ActiveDetail` without changing `IsSelected`;
  App selection toggles an `appx|` remove plan op; Windows-component selection toggles a `pkg|` remove
  plan op; sort/filter/hover/blocked/evidence/localization). Full suite **534 pass (Core 53, App 481),
  0 errors, 0 warnings (Release)**. Stage 11.2 UX REWORK IMPLEMENTED; PENDING REAL DESKTOP REVIEW; Phase 11
  remains IN PROGRESS; NOT merged to `main`.

## ADR-049: Stage 11.2 real-desktop defect — Apps tab shows only PRESENT-in-image curated; detail panel collapses when no selection; ONE unified discovery

- **Status:** **IMPLEMENTED** (2026-08-11); on branch `phase/11-component-intelligence`; **NOT merged
  to `main`**; Stage 11.2 — PENDING REAL DESKTOP REVIEW (re-validation).
- **Context:** Real-desktop validation of the ADR-048 rework (commit `c341926`) found the Customize →
  Apps tab rendered incorrectly: the decision list was invisible and a large, mostly-empty detail
  panel (with a stray "×" and labels but no values) occupied the page. Three root causes:
  (1) **Data** — `ComponentMatcher.BuildInventoryEntries` marks catalog-only definitions as `Curated`
  even with `raw=null`, so `ComponentKnowledgeViewModel.Rebuild()` (filtered to `Curated`) listed all
  22 catalog definitions including ones absent from the image — violating "only curated PRESENT in the
  real image". (2) **Layout** — the detail `ContentControl` had a `ContentTemplate` but `Content=null`
  (no `ActiveDetail`); WPF still renders the template with a null DataContext, and the tall empty
  template (Row `Auto`) squeezed the ListView (Row `*`) to zero height — the "×" was the detail's
  close button rendering with no detail. (3) **Discovery UX** — the Customize top "发现组件" button was
  bound to `components.DiscoverCommand` (Components discovery only); it did NOT trigger Component
  Intelligence discovery, so the Apps knowledge VM stayed empty even after the obvious Discover click.
  The in-tab Discover button was a separate, second discovery system — the user had to discover twice.
- **Decision A — Apps tab shows only curated components PRESENT in the image.** `ComponentKnowledgeViewModel
  .Rebuild()` now filters to `Classification == Curated && entry.RawItems.Count > 0`. Before discovery
  (raw=null) the list is empty (empty-state); after discovery only matched curated (present) appear.
  The matcher itself is unchanged — the Component Intelligence inspection surface (Stage 11.1) still
  seeds catalog-only rows so users can see what WinForge understands.
- **Decision B — detail panel collapses entirely when no detail is selected.** `ComponentKnowledgeView`
  is restructured to a two-column layout: Col 0 (`*`) = the decision list + an empty-state overlay;
  Col 1 (`Auto`) = the detail **side panel**, with `Visibility="{Binding ActiveDetail, NullToVis}"`
  so it is `Collapsed` (zero width) when `ActiveDetail == null`. The list is never squeezed. Opening
  detail (ⓘ) shows the side panel; closing (✕) collapses it. The detail never toggles removal.
- **Decision C — explicit empty state, never an empty detail card.** New `IsEmpty` + `EmptyStateText`
  VM properties + `Knowledge.EmptyAwaitDiscovery` ("请先发现当前映像中的组件。") /
  `Knowledge.EmptyNoCurated` ("尚未发现可展示的已审核组件。") resx keys. The empty-state overlay shows
  when `Items.Count == 0`.
- **Decision D — ONE unified, read-only discovery.** `CustomizeStepViewModel.DiscoverCommand` is now a
  unified `AsyncRelayCommand` that runs `Components.DiscoverAsync()` (Apps/Windows components/Services)
  AND `_knowledge.DiscoverAsync()` (CI knowledge discovery + Rebuild). The duplicate in-tab Discover
  button is removed — one button at the Customize level populates every tab. Both passes are read-only;
  no destructive servicing is duplicated (discovery never adds plan operations).
- **Decision E — color/visibility was a symptom, not a separate bug.** The "white/invisible"
  recommendation/risk labels were the null-DataContext detail panel (converter returned Gray, captions
  empty). Collapsing the detail when null eliminates the symptom; for real items the badges render
  colored backgrounds + localized captions (white text on saturated color = visible).
- **Consequences:** 8 new ADR-049 regression tests + 7 updated tests (unified discovery populates both
  & is non-destructive; present-curated visible / absent excluded; empty-state after no curated
  matches; clear-detail; Component Inspector still shows catalog-only; 6 tabs unchanged; STA list
  non-zero height + detail Collapsed when null + Visible when opened; zh-CN captions resolve). Full
  suite **556 pass (Core 53, App 503), 0 errors, 0 warnings (Release)**. Stage 11.2 PENDING REAL
  DESKTOP REVIEW; Phase 11 remains IN PROGRESS; NOT merged to `main`.

## ADR-050: Stage 11.2 master–detail — REMOVE per-row Details button; row CLICK opens/switches the detail panel

- **Status:** IMPLEMENTED (2026-08-11, branch `phase/11-component-intelligence`); **PENDING REAL
  DESKTOP REVIEW**; NOT merged to `main`.
- **Context:** Real-desktop review (and the product decision in this task) concluded the per-row
  "详情 / Details" button is redundant and forces horizontal reach. The preferred interaction is
  direct master–detail: **click any row → open/switch the right-side detail panel**; the checkbox
  only toggles plan inclusion. Two independent states must never be conflated:
  `RemovalSelected` (plan) vs `ActiveDetail` / `DetailSelected` (inspection).
- **Decision A — the per-row Details button is removed.** `ComponentKnowledgeView`'s row
  `ItemTemplate` no longer renders a Details `Button`. The standard Apps row is exactly
  **选择 | 名称 | 作用 | 建议 | 风险** (checkbox + name + purpose + recommendation badge + risk badge).
  The only remaining button is the detail panel's **×** (close), outside the list.
- **Decision B — row click drives the detail panel.** `ComponentKnowledgeView` attaches
  `MouseLeftButtonUp` + `KeyDown` to the `ListView`; the handler resolves the owning
  `ComponentKnowledgeItem` from the event source and invokes `ShowDetailCommand`. Clicks that
  originate on the `CheckBox` are ignored (`IsWithinCheckBox`), so removal selection stays
  independent. Enter on a focused row opens/switches detail; Space stays on the checkbox (removal).
- **Decision C — the two states are fully independent.** `ActiveDetail` setter refreshes each row's
  `IsActiveDetail` flag (via `RefreshActiveDetailFlags`); `RemovalSelected` (`IsSelected`) is only
  touched by the checkbox. Closing detail (`×` / ClearDetailCommand) sets `ActiveDetail = null` and
  leaves selections intact. The "currently inspected" row gets a distinct background highlight
  (`#E8F0FE` via a `DataTrigger` on `IsActiveDetail`) — visually separate from the checkbox's own
  checked state.
- **Decision D — deterministic filter interaction.** `ApplyFilter()` closes `ActiveDetail` when the
  open item leaves the visible filtered set (selection survives). Blocked rows keep an inspectable
  detail (block reason shown in the detail panel via `StringNullOrEmptyToVisibilityConverter`); the
  checkbox is disabled. `VirtualizingStackPanel.IsVirtualizing="False"` + `ScrollViewer.
  HorizontalScrollBarVisibility="Disabled"` guarantee no horizontal scroll at normal width.
- **Consequences:** 14 new ADR-050 regression tests (details button removed; row click opens/switches
  detail; panel stays open; row click does not change removal; checkbox changes removal only; checkbox
  does not change detail; checked survives inspect; × clears detail only; selections survive close;
  active highlight distinct; blocked row opens detail; blocked checkbox disabled; filter + detail;
  active closes when filtered out; Enter opens detail; Enter does not toggle removal; no horizontal
  scroll). Full suite **556 pass (Core 53, App 503), 0 errors, 0 warnings (Release)**. Stage 11.2
  PENDING REAL DESKTOP REVIEW; Phase 11 remains IN PROGRESS; NOT merged to `main`.


## ADR-051: Stage 11.3 optimization operation taxonomy (REMOVE / DISABLE / CONFIGURE / SERVICE / FEATURE)

- **Context:** the plan model only expressed removal semantics, but Customize must cover three distinct
  kinds of optimization (REMOVE, DISABLE/CONFIGURE, PERSONALIZE) that all share the same knowledge
  surface (purpose/recommendation/risk/impact/restore/evidence/compatibility). Every non-removal change
  was being forced through `RemoveProvisionedAppx`, so Review could not say what would actually happen.
- **Decision:**
  - Core `OptimizationAction` (Remove / Disable / Configure / Service / Feature), `OptimizationMechanism`
    (RemoveProvisionedAppx, DisableOptionalFeature, RemoveCapability, ServiceStartup, ExplorerPreference,
    StartPreference, TaskbarPreference, PrivacyPolicy, SystemPolicy, VisualPreference, …) and
    `OptimizationScope` (Part J) are carried as DATA on `CustomizationOperation` (`ActionKind` /
    `Mechanism` / `Scope` / `ReversalKey` / `RestoreValueData`) — views never branch on mechanism
    specifics; the execution engine still branches on the concrete `OperationType`.
  - Two new operation types: `DisableOptionalFeature` (DISM `/Disable-Feature`) and `RemoveCapability`
    (DISM `/Remove-Capability`), validated in `CustomizationPlan.ClassifyBase` (TargetIdentifier
    required) and executed with a `FeatureConfigPolicy` allowlist defense-in-depth guard (mirrors
    `ServiceConfigPolicy` / `PackageRemovalPolicy`). Capabilities are intentionally NOT in the first
    tranche allowlist.
  - `CustomizationCategory.Personalization` added; `FreezeForExecution` clones the new fields.
  - The Review step lists every selected change with its exact action type + scope + revert contract
    (Part S); `PlanReviewViewModel.Operations` + per-action totals.
  - The Windows Components tab reuses the Stage 11.2 knowledge engine
    (`ComponentKnowledgeViewModel`) with a capability/optional-feature category filter; Services /
    Privacy / System / Personalization share ONE catalog-driven engine
    (`OptimizationKnowledgeViewModel` + `OptimizationCatalog`).
- **Consequences:** `FeatureConfigPolicy` is pinned to the Windows Features catalog by a test; the
  service allowlist grew (reviewed services only) and the ADR-030 pinning test now asserts the original
  trusted set stays allowed; Review never says "remove" for a disable/configure (action-appropriate
  captions `Opt.Recommendation.<Action>.<Level>`).

## ADR-052: Offline registry / Default-User targeting — never the host HKCU

- **Context:** user-level personalization (show file extensions, dark mode, taskbar prefs) lives in
  HKCU, which does not exist in an offline image. Writing the HOST user's HKCU while intending to
  customize the image is forbidden.
- **Decision:**
  - Every catalog entry states its `OptimizationScope` explicitly: `OfflineMachine` (SOFTWARE/SYSTEM
    hives), `OfflineDefaultUser` (the offline Default User profile), `ProvisionedApp`, `MountedImageFeature`,
    `PostInstallRequired` / `UnsupportedOffline` (never applied to the image — shown as not selectable
    with a reason).
  - `OfflineHivePaths` gains a `DEFAULT_USER` base mapping to `<mount>\Users\Default\NTUSER.DAT`,
    loaded under the WinForge-owned `WinForge_DEFAULT_USER` name via the existing `RegLoadKey` service.
    A test pins the file resolution and a catalog-wide test asserts every registry target hive is
    SOFTWARE / SYSTEM / DEFAULT_USER (never a host HKCU / HKEY path).
  - Reversibility is recorded per value (`RestoreValueData` = the Windows/default value WinForge would
    restore; the generic revert contract is shown in Review/detail).
- **Consequences:** Personalization user-scope controls target new users via the Default User profile;
  post-install-only ideas are deferred in the coverage matrix instead of being silently claimed.

## ADR-053: Customize coverage policy — VERIFIED / COMMUNITY_REFERENCE / UNKNOWN

- **Context:** "coverage expansion" must not mean blindly copying registry tweaks from the web. A
  tweak being used by another debloat script is not evidence.
- **Decision:**
  - Every catalog entry is one of: VERIFIED (officially documented mechanism or validated against the
    real target image), COMMUNITY_REFERENCE (useful community evidence exists, informational only —
    never promoted to RecommendedRemove / RecommendedDisable), or UNKNOWN (not offered). Unknown stays
    Unknown.
  - Standard mode shows only reviewed, standard-visible, evidence-backed entries; Unknown /
    Experimental / DiscoveredUnclassified never appear beside safe options (Part M).
  - Direction targets per tab are ceilings, not quotas: quality and correctness override quantity, and
    a thin-but-defensible tab is reported honestly in the coverage matrix
    (`.tmp/phase11/stage11.3-coverage-matrix.md` — every candidate with status Implemented / Deferred /
    Rejected / Unsupported and a reason).
  - Rejected up front: timer-resolution folklore, BCD hacks, memory myths, disabling Defender, placebo
    perf tweaks (Part G).
- **Consequences:** first tranche = 12 Windows Components, 12 Services (11 reviewed + 1 core-info),
  11 Privacy, 10 System, 14 Personalization; weaker-evidence candidates are documented as Deferred.

## ADR-054: Personalization activation — Coming Soon removed

- **Context:** the Personalization surface was a "Coming Soon" placeholder; a Profile/Gaming/Office/
  Developer recommendation engine has no value until Customize has a meaningful surface, and
  Personalization is mandatory for Stage 11.3.
- **Decision:**
  - The Customize **Personalization** tab replaces the Experience / Coming Soon tab (sixth tab,
  header `Customize.Tab.Personalization`). It is the shared catalog-driven knowledge surface
  (`OptimizationKnowledgeViewModel` + `OptimizationCatalog`) — no duplicate knowledge implementation.
  - First tranche ships 14 reviewed entries covering Start/Search (hide Recommended / recently added),
  Taskbar (Widgets, search-as-icon, Task View), Explorer (file extensions, hidden files, open-to-This-PC,
  Quick access recents/frequent), Lock screen/Desktop (Windows Spotlight), and Appearance (dark mode,
  transparency, animations) — each with scope + revert in the detail panel.
  - User-scope entries use the OfflineDefaultUser strategy (ADR-052); Spotlight uses the documented
    machine policy. Unsupported/deferred personalization ideas (Phone Link in Start, Desktop Spotlight,
    Start layout pinning) are recorded in the coverage matrix, not faked.
- **Consequences:** Personalization no longer displays Coming Soon; tests assert the tab type, the
  five required groups, and the DEFAULT_USER strategy of the show-file-extensions operation.


## ADR-055: Stage 11.3 real-desktop defect — unified Discover must refresh BOTH knowledge tabs

- **Context:** on the real Windows 11 25H2 image, after one unified "发现组件" action the Customize
  **Windows Components tab showed ZERO rows** and the "请先发现当前映像中的组件。" await-discovery
  empty state, while Apps / Services / Privacy / System / Personalization all worked. The 12 reviewed
  logical components were implemented but invisible.
- **Root cause (traced, not guessed):** `CustomizeStepViewModel.DiscoverAllAsync()` ran the single
  Component Intelligence discovery and refreshed only the **Apps** knowledge VM
  (`_knowledge.DiscoverAsync()`). The **Windows Components** knowledge VM is a separate
  `ComponentKnowledgeViewModel` instance over the SAME classified inventory — its `Rebuild()` ran once
  in the constructor against the pre-discovery (catalog-only, `Discovered=false`) inventory and was
  never re-run, so `HasInventory` stayed false (hence the await-discovery text) and `Items` stayed
  empty. Matching rules, identities, parsers, and state handling were all correct.
- **Decision:**
  - `ComponentKnowledgeViewModel.RefreshFromInventory()` rebuilds a tab from the already-classified
    shared inventory WITHOUT re-running DISM.
  - `CustomizeStepViewModel.DiscoverAllAsync()` now calls
    `_componentsKnowledge.RefreshFromInventory()` after `_knowledge.DiscoverAsync()` — one DISM pass
    refreshes Apps AND Windows Components together (ADR-049's single-pass guarantee preserved).
  - Execution eligibility separated from display eligibility: `ComponentKnowledgeItem.IsApplySupported`
    gates SELECTION for OptionalFeature/Capability rows on `FeatureConfigPolicy`; a capability (or a
    not-yet-allowlisted feature) row stays VISIBLE for knowledge with its checkbox disabled and an
    explicit reason (`Opt.ApplyUnsupported` "当前版本暂不支持应用") instead of being hidden or silently
    Skipped at Apply.
- **Consequences:** 6 new regression tests (`Stage11p3ComponentsTabDefectTests`: OptionalFeature maps
  to a Windows Components row; Disabled feature stays visible; AppX filter cannot affect the
  Components tab; execution allowlist does not gate visibility; unsupported apply shows a blocked
  reason + disabled checkbox; unified Discover populates Apps + Components together; catalog targets
  pinned to the documented 25H2 `/Get-Features` identities). Full suite **591 pass (Core 53, App 538),
  0 errors, 0 warnings (Release)**.

## ADR-056: OpenSSH Client/Server are CAPABILITIES, not OptionalFeature FeatureNames

- **Context:** the first-tranche Windows Components catalog modeled OpenSSH Client / OpenSSH Server
  with `Category = OptionalFeature` and DISM FeatureNames `OpenSSH.Client` / `OpenSSH.Server`. Microsoft
  official documentation confirms both are Windows **CAPABILITIES** managed through
  Get-WindowsCapability / Add-WindowsCapability with the identities `OpenSSH.Client~~~~0.0.1.0` /
  `OpenSSH.Server~~~~0.0.1.0` — they never appear in `/Get-Features`.
- **Decision:**
  - `TechnicalTargets` for OpenSshClient / OpenSshServer now use `Category = Capability`,
    `MatchMethod.Exact`, patterns `OpenSSH.Client~~~~0.0.1.0` / `OpenSSH.Server~~~~0.0.1.0`, mechanism
    `RemoveCapability`. They resolve through the Capability inventory only — a feature-shaped
    "OpenSSH.Client" raw item must NOT match (pinned by test).
  - `OpenSSH.Client` / `OpenSSH.Server` removed from `FeatureConfigPolicy.AllowedFeatureNames`.
  - Because capability execution is intentionally unsupported in this tranche (ADR-051), the rows stay
    VISIBLE when present, their checkbox is disabled, the detail shows "当前版本暂不支持应用"
    (`Opt.ApplyUnsupported`), and selection is a no-op — no Apply operation that silently Skips.
  - Documentation/report wording corrected (CHANGELOG, ARCHITECTURE, coverage matrix, ROADMAP).
- **Consequences:** regression tests assert the capability identity resolution, the
  never-OptionalFeature rule, the blocked-apply UX, and the catalog/allowlist pin; full suite
  **591 pass (Core 53, App 538), 0 errors, 0 warnings (Release)**.


## ADR-057: Stage 11.4 profile model — logical-id priorities, never raw package names

- **Context:** Customize needed to answer "what kind of Windows are you building?" with
  deterministic, explainable recommendations across all six tabs, while Stage 11.3 only had
  per-item curated defaults. Hard-coded deletion lists per profile were explicitly rejected.
- **Decision:**
  - First-class `ProfileDefinition` (Core): Id, DisplayNameKey/DescriptionKey/IconKey,
    Scenarios, `RecommendationOverrides` (TargetId + Intent Keep/Trim + ReasonKey + Tier),
    `RequiredCapabilities`, `PreferredCapabilities`, `AvoidedComponents`, CompatibilityRules.
    Targets are ALWAYS stable logical WinForge ids — never raw Windows package names.
  - Initial evidence-backed set of 7 profiles (generated by `gen_stage113.py` → `ProfileCatalog`):
    Balanced / Gaming / Developer / Office / Lightweight / DedicatedMinimal / Custom.
    Gaming keeps Xbox/codecs/services/Game DVR and trims unrelated consumer + telemetry;
    Developer REQUIRES Wsl/VirtualMachinePlatform/HyperV/HypervisorPlatform/Terminal/
    DesktopAppInstaller/OpenSshClient (present-gated); Office keeps OneDrive/printing/scanning/
    meetings/remote-assist; Lightweight trims consumer/telemetry/virtualization/background/UI
    suggestions; DedicatedMinimal is more aggressive but critical changes stay manual; Custom
    is exclusive (selecting it clears presets; selecting a preset clears Custom).
  - Profiles RECOMMEND. Nothing is selected into the plan until the user explicitly adopts
    (ADR-060). Multi-select combinations (Gaming+Developer, Office+Developer, …) are supported.
- **Consequences:** all rules reference only items already modeled in Stage 11.3; no invented
  rules for unknown components (Part N). 7 profiles, 59 implemented Stage 11.3 candidates.

## ADR-058: Recommendation precedence — deterministic, documented, visible

- **Context:** profile combinations must resolve deterministically; "KEEP wins when another
  selected scenario requires the component" needed an exact, testable ordering.
- **Decision:** the engine (`RecommendationEngine`, Core, pure) evaluates every item with the
  documented precedence (Part D):
  1. Critical safety constraint (Critical risk / Removal.Blocked / NeverRemove) → Blocked,
     wins over EVERYTHING including user overrides;
  2. Explicit user keep preference (user override, Part K) → wins over all profile rules;
  3. Required dependency (a kept component has a Requires/RecommendsKeeping edge to this item);
  4. Profile requirement (RequiredCapabilities ∩ present ids);
  5. Scenario recommendation override (overrides / preferred / avoided; KEEP beats TRIM at this
     tier and the conflict is recorded — never silent);
  6. Component default recommendation (unchanged curated default).
- **Consequences:** `EffectiveRecommendation` (Level/IsPresent/IsApplySupported/Risk/
  WasOverridden/WasProfileDriven/HasConflict/ReasonKeys/SourceRuleIds/Conflicts) is computed
  SEPARATELY — the definition's default is never mutated. Reason keys are deterministic and
  localized (Part F — no runtime AI prose).

## ADR-059: User override semantics — manual choices survive recalculation

- **Context:** once a user manually toggles a checkbox after adopting recommendations, profile
  recalculation must not silently overwrite the explicit choice ("do not surprise the user").
- **Decision:**
  - Any MANUAL toggle of a knowledge row marks a user override in the workflow-scoped
    `RecommendationContextService` (`SetUserOverride`). Programmatic adoption
    (`SetSelectedForAdoption`) never marks overrides, so Adopt/Reapply can run repeatedly.
  - Overridden rows show `WasOverridden` + the "你已手动更改——推荐不会覆盖你的选择" reason; the
    auto-selection predicate (Part J) excludes them, so neither Adopt nor Reapply touches them.
  - "重新应用推荐" re-runs adoption with the same eligibility — user choices simply stay put.
- **Consequences:** persisted within the active workflow only (singleton session state); a NEW
  image workspace resets profiles AND overrides (`ResetForNewWorkflow`), so a previous aggressive
  profile is never silently reused on a new ISO. Default for a fresh workflow: no profile selected
  (pure manual mode, identical to Stage 11.3 behavior).

## ADR-060: Safe recommendation adoption — explicit action, strict eligibility

- **Context:** "采用推荐选择" is the ONLY path that may update checkboxes/plan selections, and it
  must never auto-select something risky, unsupported, or in conflict.
- **Decision:**
  - Preview ("查看推荐方案") is non-destructive: it groups candidates into 推荐执行 / 建议保留 /
    需要确认 / 冲突·阻止 and changes nothing.
  - Adoption eligibility (Part J) is: present + apply-supported + effective level is a
    recommended change (RecommendRemove/RecommendDisable/RecommendSet) + Risk == Low +
    no conflict + not a user override. High/Critical, AdvancedOnly, NeverRemove, Protected,
    incompatible, unsupported-apply, and conflicted items stay manual.
  - The preview shows WHAT would be selected before the user commits; the profile selector itself
    NEVER changes a checkbox (badges and "为什么" change only).
- **Consequences:** a capability row (apply unsupported this tranche) is visible for knowledge but
  never adoptable; a High-risk trim (e.g. VirtualMachinePlatform under Lightweight) is visible but
  never auto-selected; Review then reflects the final explicit selections.



## ADR-061: Phase 11 closeout — MERGED TO main; recorded non-blocking Custom + Extra Scenarios follow-up

- **Context:** Stage 11.4 passed real-desktop validation on the Windows 11 25H2 zh-CN x64 Consumer image
  (2026-08-12): profile selection works, safe recommended selections apply immediately, the selected
  count updates correctly, user overrides remain protected, the recommendation-detail overlay opens and
  returns correctly, Customize state is preserved, Gaming/Developer/Lightweight produce meaningful
  differences, the profile layout is usable with the component list still visible, and all six Customize
  tabs plus Review work without binding/runtime defects. Phase 11 is therefore closed out and merged to
  `main` via a `--no-ff` merge (merge commit recorded in PROJECT_STATUS).
- **Decision:**
  - PHASE 11 — COMPLETED; Stage 11.1/11.2/11.3/11.4 — REAL DESKTOP VALIDATED; MERGED TO MAIN.
  - One NON-BLOCKING follow-up is recorded (explicitly out of scope for Phase 11 completion):
    **Custom profile currently disables the Extra Scenarios panel** (extras require an active primary
    preset). Future polish: allow Extra Scenarios in Custom mode as recommendation/keep HINTS — they may
    advise keeps (e.g. "WSL / Docker" keeps virtualization) without enabling a primary Profile preset and
    without auto-applying anything. This is a UX/enablement refinement, not a correctness defect.
- **Consequences:** Phase 11 work is complete and merged; the Custom + Extra Scenarios hint mode is a
  candidate for a later phase (tracked in ROADMAP follow-ups). No changes were made to the recommendation
  engine for this closeout.


## ADR-062: Workspace lifecycle — durable manifest + explicit state (Phase 12)

- **Context:** A real-desktop incident leaked ~249 GB (≈30 stale `wf-*` workspaces × ~6.81 GB + temp output)
  because workspaces had no durable lifecycle and nothing ever cleaned them. Product decision: WinForge must
  own a deterministic workspace lifecycle; users must never be required to delete AppData folders manually.
- **Decision:**
  - Every workspace persists a `workspace.json` manifest (WorkspaceId / CreatedAt / LastUsedAt / CurrentState /
    SourceIsoPath / WorkingWimPath / MountPath / IsMountedKnown / HasBuildCheckpoint / FinalOutputPath /
    RecoveryRequired / CanDeleteSafely / WinForgeVersion / RetentionReason / transition log).
  - Lifecycle states: Created → Preparing/Prepared → Mounted → Customized → Committed → BuildCheckpoint →
    Completed; terminal: FailedDisposable / FailedRecoverable / Cancelled / Orphaned / Cleaning / Cleaned.
  - The servicing service transitions the manifest on Prepare/Mount/UnmountDiscard/UnmountCommitted/PrepareFailed;
    the build view marks Completed + FinalOutputPath after a successful build.
  - Cleanup classification NEVER trusts directory existence alone: the live DISM mount registration is
    authoritative (`/Get-MountedImageInfo`), and a query failure fails closed (no deletion decisions).
- **Consequences:** pre-Phase-12 workspaces (no manifest) are classified LegacyUnknown; they are offered as
  cleanup candidates in the Storage UI but deletion still re-checks DISM. Cleanup bytes are measured and
  reported; partial failures record the exact leftover path and are retryable.

## ADR-063: Cleanup safety policy — active mounts and recoverable checkpoints never auto-deleted

- **Context:** deleting the wrong workspace can destroy an active mount or a resumable build checkpoint.
- **Decision:**
  - A workspace is NEVER deleted when DISM registers its mount path (or any mount nested under the workspace
    dir — covers legacy workspaces without manifests); the deletion is refused with a clear result.
  - NeedsRemount (manifest expects a mount but DISM does not register it) → classified Recoverable; a recovery
    action is surfaced, never a silent delete.
  - Mount-query failure → fail closed: classification Unknown and cleanup refused.
  - Recoverable states (BuildCheckpoint / FailedRecoverable / Completed-without-output) are retained and
    excluded from cleanup candidates. Completed-with-recorded-FinalOutputPath is disposable (output preserved
    outside the workspace).
  - Cleanup strips ReadOnly/System/Hidden before deleting; a partial failure never claims success (leftover
    path recorded, retry later).
- **Consequences:** the 249 GB incident class of leak becomes impossible under normal use — every
  discarded/failed-disposable/completed workflow is a cleanup candidate that the Storage UI removes in one
  action, and the repeated-workflow regression test proves non-accumulation.

## ADR-064: Output vs temp separation — user ISO is never disposable

- **Context:** the default final-ISO destination was `%LOCALAPPDATA%\Temp\WinForge\Output`, which treated a
  user-created ISO as disposable temp data and blurred cleanup boundaries.
- **Decision:**
  - Final ISO output defaults to `Documents\WinForge` (fallback: temp Output only when the profile documents
    folder is unavailable) and is user-visible/configurable in the Build page.
  - Cleanup operates ONLY on WinForge-owned temp (workspace root); `FinalOutputPath` (or any user output) is
    never a cleanup target — verified by test.
- **Consequences:** temp vs output are strictly separated by artifact classification, not by "created by
  WinForge".

## ADR-065: Disk-space guard — conservative estimates block before Prepare/Build

- **Context:** builds can need tens of GB; waiting until the system drive reaches zero corrupts outputs.
- **Decision:**
  - `DiskSpaceEstimator` (pure, testable): Prepare ≈ working WIM × 4 (unpacked mount) + 2 GiB margin;
    Build ≈ working WIM + media staging (≈ source ISO) + final ISO + 2 GiB margin.
  - BuildStepViewModel checks free space on the output drive before starting and blocks with a localized
    "需要约 X GB 可用；当前 Y GB" message when insufficient.
- **Consequences:** operations stop before filling the drive; estimates are conservative by construction.


## ADR-066: Stage 12.2 — configurable workspace root + Finish/Discard auto-cleanup

- **Context:** two product gaps remained before real-desktop validation: users with small C: SSDs could not
  relocate the workspace root, and completed/discarded workspaces still required a manual Storage visit.
- **Decision:**
  - `IWorkspaceRootSettingsService` (persisted `workspace-roots.json`): current root + known previous roots.
    Root changes affect NEW workflows only; existing workspaces are never moved; an actively mounted session
    blocks the change; candidate roots are validated (drive/profile roots rejected, creatable+writable probe);
    a low-free-space drive shows a warning (not a block). Cleanup/orphan scanning covers ALL known roots
    (Part G) so old roots are never orphaned.
  - `WorkflowViewModel.FinishAsync` runs the authoritative DISM-safe cleanup of the completed workspace:
    final ISO preserved, recoverable checkpoints retained (minimal-retention), reclaimed bytes reported on the
    Build step; a partial failure is a WARNING with an explicit [立即重试清理] — never a build failure.
  - A successful Unmount/Discard auto-cleans the disposable workspace in the background (failures surface in
    the Storage UI).
- **Consequences:** repeated Prepare→Customize→Build→Finish (or →Discard) cycles no longer accumulate stale
  workspaces and never require manual AppData cleanup.


## ADR-068: Plan compiler normalization — identical registry operations merge, true conflicts stay

- **Context:** real desktop produced "Duplicate operations target the same change:
  reg|...CloudContent|DisableWindowsSpotlightFeatures" — two independent customization items
  (Privacy `SpotlightFeatures` "Windows 聚焦内容", Personalization `DisableSpotlight`
  "Windows 聚焦（锁屏内容）") compile to the exact same registry mutation. The validator was right;
  the plan COMPILER was wrong to emit two identical physical operations.
- **Decision:**
  - `CustomizationOperation.CanonicalRegistryTarget()` = SCOPE + normalized hive + normalized key
    path + normalized value name (case-insensitive, '/'↔'\' separators; scope is identity —
    OfflineMachine and OfflineDefaultUser never merge even with identical key text).
  - `HasSameEffectiveChangeAs()` compares mutation semantics: operation type + registry value kind +
    normalized data (DWord/QWord numeric equivalence: "1" == "0x1" == "01"; others case-insensitive).
  - `CustomizationPlan.AddOperation` merges identical effective changes into ONE physical operation
    (first wins, provenance merged); semantically DIFFERENT mutations of the same target remain two
    operations and stay validator-blocking. The validator is NOT weakened and still flags any
    duplicate that bypasses normalization as an internal-plan defect.
  - `ConflictKey` includes the scope so the validator cannot false-positive across scopes.
  - Provenance: `SourceDefinitionIds` retains every originating definition/operation id; the
    customization VM records `Definition.Id` on every generated operation.
- **Consequences:** identical recommendation intents produce one executable operation; Review counts
  reflect executable operations; true conflicts (same target, different value) still block Apply with
  visible warnings; rollback/explainability data keeps all sources.


## ADR-069: Build→Finish workflow state synchronization

- **Context:** real desktop: the build pipeline completed successfully (verified ISO, 100%,
  已完成) but the wizard stepper kept 构建镜像 · 进行中 and 完成 stayed disabled. BuildStep internal
  state was Completed; the wizard never learned about it.
- **Root cause:** `WorkflowViewModel.OnBuildChanged` (a) refreshed `CanFinish` but never called
  `RecomputeStates()`, so the Build step's `State` never flipped from `Current` to `Completed`; and
  (b) refreshed FinishCommand via `if (FinishCommand is RelayCommand finish)` — but FinishCommand is
  an `AsyncRelayCommand`, so `RaiseCanExecuteChanged()` was NEVER called and the button stayed
  disabled even though `CanFinish` (IsFinalStep && CurrentStage == Completed) was already true.
- **Decision:** `OnBuildChanged` on `CurrentStage` changes now calls `RecomputeStates()`; the Build
  step maps a CURRENT step with `_build.CurrentStage == Completed` to `Completed`; command refresh
  uses the correct `AsyncRelayCommand` type in both `RecomputeStates` and the refresh path. Finish
  gating keeps ONE source of truth (`CanFinish`); NotStarted / Failed / Cancelled / verification
  failure all keep Finish disabled. Stage 12.2 Finish cleanup is untouched (authoritative DISM
  mount check, final ISO preserved, reclaimed bytes reported, navigate Home).
- **Consequences:** at the moment the build log reaches "Build completed" the stepper flips to
  已完成, 完成 enables immediately (no navigation/restart/rescan), Finish cleanup runs, ISO stays,
  Home returns.


## ADR-070: Hide Widgets offline registry target — policy-based equivalent + apply-result UX

- **Context:** real desktop: 「隐藏小组件按钮」 (OfflineDefaultUser SetOfflineRegistryValue) threw
  UnauthorizedAccessException while the sibling 「任务栏搜索仅显示图标」 (same hive, same
  Explorer\Advanced key) succeeded.
- **Root cause:** the old target Explorer\Advanced\TaskbarDa sits in the PROTECTED Explorer subtree
  of the Default User template; template ACLs reject offline writes (the sibling worked only because
  TaskbarSearch already exists in the template). EnsureKeyPath fell through to CreateSubKey on the
  existing-but-read-only key and surfaced the bare exception.
- **Decision (Case D):**
  - HideTaskbarWidgets now targets the official USER POLICY branch
    Software\Policies\Microsoft\Dsh → EnableWebContent = 0 (Windows 11 25H2 supported mechanism),
    written into the offline Default User NTUSER.DAT. Policy branches are offline-writable by design.
  - EnsureKeyPath probes read-only keys and raises an explicit contextual "read-only template ACL"
    error (never forces writes, never takes ownership, never rewrites ACLs).
  - Apply result UX: localized summary 应用完成：{0} 项成功，{1} 项失败。 and a visible
    failed-operations panel (name + reason). Execution writes per-operation outcomes back from the
    frozen snapshot onto the LIVE plan so the panel can populate.
  - Partial apply semantics: CompletedWithErrors unlocks Apply/Build but is NEVER presented as full
    success — the failure panel is shown and the user proceeds only after seeing it.
- **Consequences:** the operation remains offline-supported (policy branch), siblings unaffected, no
  ACL hacks; failed operations are visible with exact reasons on the Review page.


## ADR-071: One authoritative creation root (CurrentRoot) — shadow workspace leak fix

- **Context:** real desktop: CurrentRoot=F://WinForgeWorkspaces, yet a ~6.9 GB workspace
  (wf-a9bac38c7259) kept reappearing under the old C: default root after Finish emptied F:.
- **Root cause:** WorkspacePathProvider was registered standalone (default
  %LOCALAPPDATA%\WinForge\Workspaces) and never consulted IWorkspaceRootSettingsService.CurrentRoot.
  ImageServicingService created SERVICING data under C: while WorkspaceLifecycleManager wrote the
  MANIFEST under the configured F: root (its WorkspaceRoot property reads CurrentRoot) — two same-id
  directories. Finish cleaned by manifest (F: shell) and leaked the C: data (manifest-less).
- **Decision:**
  - WorkspacePathProvider resolves the CURRENT root at runtime (fixed override → current root →
    platform default), wired in Bootstrapper to the settings service. Every new workflow creation
    path (Prepare/Apply/Commit/Export/checkpoint/Build) then lands under CurrentRoot only.
  - KnownRoots are historical: scanned / recovered / cleaned, NEVER a creation destination.
  - Finish cleans the single unified workspace (manifest + data together); the final ISO (outside
    the workspace) survives; recoverable checkpoints retained only as required.
  - Storage candidates display their owning RootPath so a size is never attributed to the current
    root by mistake.
- **Consequences:** no shadow/split workspaces; C:/old-root disk usage stays flat across repeated
  workflows; root changes affect all new workflows immediately (provider reads live).


## ADR-072: Phase 12 closeout — status, architecture confirmation, non-blocking follow-ups

- **Status:** PHASE 12 — COMPLETED; REAL DESKTOP VALIDATION — PASSED (Windows 11 25H2); MERGED TO
  `main` via `--no-ff` (branch `phase/12-workspace-lifecycle` retained). Original incident
  (~30 stale workspaces + temp ≈ 249 GB) RESOLVED by the lifecycle architecture.
- **Architecture confirmed (12 items):** lifecycle manifests; DISM-authoritative mount safety;
  CurrentRoot-only creation vs KnownRoots scan/recover/clean; safe cleanup policy; Finish/Discard
  auto-cleanup; final-ISO vs temp separation; disk-space guard; Storage cleanup UI; canonical
  plan-operation normalization; Apply partial-failure reporting; Build→Finish state sync;
  shadow-workspace root split fix.
- **Non-blocking follow-ups (do NOT reopen Phase 12):**
  1. long-running operation periodic disk-space checks are less comprehensive than pre-operation checks;
  2. recoverable checkpoint minimization may be improved further;
  3. startup automatic cleanup stays conservative — Storage UI / Finish / Discard already provide safe
     cleanup paths;
  4. Finish cleanup may synchronously wait while deleting a large workspace; future UX may improve
     progress/cancellation;
  5. Custom profile + Extra Scenarios polish remains a separate Phase 11 follow-up (ADR-061).
