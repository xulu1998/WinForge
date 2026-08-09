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
- **Decision:** Read metadata with `dism.exe /English /Get-WimInfo`, the
  documented read-only image query (no `Mount-Image`, no servicing). Because the
  host UI language may not be English, `/English` is mandatory so the parsed
  fields are stable. The query is performed in **two stages** because a single
  `/Get-WimInfo` call without `/Index` only reliably returns per-index
  `Index` / `Name` / `Description` (and `Size`); the detailed fields —
  `Architecture`, `Version`/`Build`, `Edition Id`, `Installation`, and
  `Languages`/`Default Language` — are reported **only** by a per-index query
  (`/Get-WimInfo /ImageFile:"..." /Index:<n>`). `WindowsImageMetadataService`
  therefore (A) runs the enumeration query once, then (B) runs one detail query
  for **every** enumerated index (index numbers are not assumed sequential and are
  not assumed to map to a specific edition), and merges the two by index. If
  enumeration fails the whole result is `Failed`; if a single per-index detail
  query fails, that edition keeps its enumerated `Index`/`Name`/`Description`, its
  detailed fields stay `null`, and its `DetailStatus` is set to `Failed` (logged,
  not shown raw) — WinForge never silently pretends full metadata arrived. Parsing
  is split to match: `DismWimInfoParser.ParseImageList` reads only the reliable
  enumeration fields, and `DismWimInfoParser.ParseImageDetails` reads the full
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
