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
