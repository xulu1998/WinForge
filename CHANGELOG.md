# Changelog

All notable user-visible changes to WinForge are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased]

### Added (Phase 2 Step 2.2 — Windows image metadata & editions)
- Step 2.2 reads read-only metadata from the install image (`install.wim` /
  `install.esd`) found under the mounted ISO: WIM/ESD indexes, edition name,
  edition description, architecture, Windows version, Windows build, edition ID,
  installation type, and languages — without mounting, modifying, or servicing
  the image.
- Core contract `IWindowsImageMetadataService` returning a structured
  `WindowsImageMetadataResult` (top-level version/build/architecture/languages
  plus a per-index `WindowsEditionInfo` list). All fields are nullable so data
  WinForge cannot reliably read stays `null` rather than being guessed; the UI
  decides between "Not detected" and "Mixed".
- Infrastructure `WindowsImageMetadataService` queries the image read-only with
  `dism.exe /Get-ImageInfo /ImageFile:"<path>" /English` in **two stages**: one
  enumeration query (no `/Index`) that reliably returns each index's `Index` /
  `Name` / `Description`, followed by one per-index detail query
  (`/Get-ImageInfo /ImageFile:"<path>" /Index:<n> /English`) for **every**
  enumerated index that supplies `Architecture`, `Version`/`Build`, `Edition Id`,
  `Installation`, and `Languages`/`Default Language`. The two parses are split to
  match (`DismImageInfoParser.ParseImageList` for enumeration,
  `DismImageInfoParser.ParseImageDetails` for per-index detail) and merged by
  index.
  Both are key-based, tolerant of unknown / future / reordered fields, and never
  parse by fixed column position.
- `IProcessRunner` abstraction (Core) with `ProcessRequest` / `ProcessResult`
  DTOs and an Infrastructure `WindowsProcessRunner` (`System.Diagnostics.Process`)
  implementation; keeps Core free of any `Process` dependency and makes DISM
  invocation fully testable with a fake.
- High-level inspection session in `WindowsIsoInspectionService` now mounts the
  ISO, inspects the layout (Step 2.1), reads the install-image metadata (Step
  2.2) **while the ISO is still mounted**, then always dismounts — preserving the
  ADR-015 cancellation-safe cleanup. The ViewModel never coordinates mount
  lifecycle.
- Image page shows a "Windows information" section (Windows Version, Build,
  Architecture, Language) and an editions `ListView`; selecting an edition writes
  `IAppState.SelectedEdition` (status only — no extraction/mount/modify), and the
  Home page "Windows Edition" tile reflects the selection.
- 16 new automated tests (parser, service via fake process runner, orchestrator
  lifecycle, ViewModel/Home selection) covering single/multi-index WIM, ESD,
  Home+Pro enumeration, architecture/version/build/language parsing, malformed
  and empty output, non-zero DISM exit, cancellation, unknown/reordered fields,
  edition selection, and guaranteed dismount after metadata failure. All Step
  2.1 tests are retained.

### Fixed (Phase 2 Step 2.2 — two-stage metadata query correctness)
- Real-DISM correctness gap: a single `dism.exe /Get-ImageInfo` (no `/Index`) only
  reliably reports per-index `Index` / `Name` / `Description` (and `Size`). The
  detailed fields — `Architecture`, `Version`/`Build`, `Edition Id`,
  `Installation`, `Languages`, `Default Language` — are returned **only** by a
  per-index query (`/Get-ImageInfo /ImageFile:"..." /Index:<n> /English`). Step 2.2
  now runs the enumeration query once, then one detail query for **every**
  enumerated index (index numbers are not assumed sequential and are not assumed
  to map to a specific edition such as Home/Pro), and merges the results by index.
  `DismImageInfoParser` was split into `ParseImageList` (enumeration fields only)
  and `ParseImageDetails` (full per-index detail). Failure semantics: if the
  enumeration query fails the whole result is `Failed`; if a single per-index
  detail query fails, that edition keeps its enumerated data, its detailed fields
  stay `null`, and `WindowsEditionInfo.DetailStatus` records `Failed` (logged, not
  shown raw) so the UI never silently pretends full metadata arrived. Added a
  per-edition `DetailStatus` / `DetailErrorMessage` and a `DefaultLanguage` field.
  Version/build is parsed structurally and never fabricated (no invented
  servicing/UBR segment). The guaranteed dismount (ADR-015) and read-only safety
  are unchanged. Tests were expanded with realistic two-stage fixtures and now
  assert the exact command sequence (enumeration without `/Index`, then one
  `/Index:n` per index) via a recording fake process runner.

### Fixed (Phase 2 Step 2.2 — DISM Error 87: use /Get-ImageInfo)
- Real desktop validation (2026-08-08): initial Step 2.2 desktop validation
  exposed DISM exit code 87 ("The parameter is incorrect") because the
  implementation invoked `dism.exe /English /Get-WimInfo /ImageFile:"..."`, an
  incorrect command combination for the Windows 11 DISM command line. Corrected to
  the documented Windows 11 syntax: `dism.exe /Get-ImageInfo /ImageFile:"<path>" /English`
  for enumeration and `dism.exe /Get-ImageInfo /ImageFile:"<path>" /Index:<n> /English`
  for the per-index detail query. `/ImageFile:` is kept (not changed to
  `/WimFile`). The two-stage design (enumeration → collect indexes → per-index
  detail → merge by index) is retained. The parser type was renamed
  `DismWimInfoParser` → `DismImageInfoParser` and all references/tests updated. A
  regression test asserts production arguments never contain `/Get-WimInfo`. Step
  2.2 remains IMPLEMENTED / PENDING REAL DESKTOP RE-VALIDATION — NOT COMPLETED.

### Status (Phase 2 Step 2.2)
- Step 2.2 is implemented on `feature/iso-inspection` and passes the automated
  test suite (0 errors, 0 warnings, 100% tests executed and passing). It is
  **pending real Windows ISO desktop RE-validation** (the Windows 11 25H2 zh-CN x64
  Consumer `install.wim` target) and therefore is NOT yet marked COMPLETED.
- `WindowsImageType` / architecture / version / build / language parsing is now
  implemented; the real-desktop mount→inspect→dismount→metadata cycle still
  requires user confirmation on a Windows machine.

## [0.1.0-alpha] — 2026-08-08

### Added
- Project governance documents: README, ROADMAP, PROJECT_STATUS, ARCHITECTURE,
  DECISIONS, AGENTS, CHANGELOG.
- Product, testing, and Windows compatibility documentation under `docs/`.
- Phase 0 (Project Governance) completed; roadmap defined through Phase 12.
- Architecture decision records ADR-001 … ADR-009.
- **Phase 1 — Application Foundation:**
  - `WinForge.sln` with `WinForge.App` (WPF), `WinForge.Core`,
    `WinForge.Infrastructure`, and `WinForge.Core.Tests` (plus
    `WinForge.App.Tests` for headless boot verification).
  - WPF navigation shell: left rail with Home, Image, Components, Experience,
    Privacy, System, Build, Logs, Settings; Home and Image implemented, the rest
    show a "Coming soon" page.
  - MVVM infrastructure (`ViewModelBase`, `RelayCommand`, `AsyncRelayCommand`),
    `INavigationService` / `NavigationService`, and `IAppState` / `AppState`.
  - Core domain models: `WindowsImageInfo`, `WindowsEditionInfo`, `BuildPlan`
    (skeletons; no real ISO inspection yet).
  - Logging abstraction (`ILoggerService`, `LogEntry`, `LogLevel`) with an
    in-memory implementation in Infrastructure; live Logs page.
  - Dependency injection via `Microsoft.Extensions.DependencyInjection`.
  - Process-wide error handling (`AppDomain.UnhandledException`,
    `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`).
  - `*.iso` file picker that validates existence and records the path in
    `AppState` (no mount / DISM / inspection).

### Notes
- Phase 1 is foundation only. No DISM, ISO mounting, WIM handling, debloating,
  privacy, or build behavior is implemented (those are future phases).
- The WPF window must be rendered on a Windows desktop; headless environments
  verify startup wiring via `WinForge.App.Tests`.

### Fixed (merge-readiness)
- Logging thread-safety: `InMemoryLoggerService` (Infrastructure) now stores
  entries in a lock-guarded `List<LogEntry>` and returns a snapshot from
  `Entries`; it no longer uses a WPF `ObservableCollection` and has no Dispatcher
  dependency, so it is safe to call from background threads/Tasks. `LogsViewModel`
  keeps the WPF `ObservableCollection` and marshals `EntryAdded` to the UI thread
  via `SynchronizationContext` (ADR-014). Added `LoggerThreadSafetyTests`
  (concurrent logging, event delivery, capacity under load).
- Read-only binding crash on the Image page: the `TextBox` showing
  `ImageViewModel.FileDisplay` used the default `TwoWay` mode; because
  `FileDisplay` is getter-only, WPF threw at runtime. The binding is now
  `Mode=OneWay`. Audited all Phase 1 XAML — this was the only default-TwoWay
  control bound to a read-only property. Added `ImageBindingRegressionTests`
  (read-only property + `Mode=OneWay` XAML assertion, no display device needed).

### Fixed (merge-readiness, Phase 2 Step 2.1)
- Cancellation-safe ISO cleanup: `WindowsIsoInspectionService` now tracks whether
  a mount was attempted and always runs a best-effort `Dismount-DiskImage` in its
  `finally` block using `CancellationToken.None`. An ISO can no longer be left
  mounted when inspection is cancelled or fails before the mounted root is
  obtained (ADR-015). `OperationCanceledException` is re-thrown so cancellation
  is never swallowed by successful cleanup.
- `WindowsIsoMountService.DismountAsync` is safe when the image is not mounted
  (`-ErrorAction SilentlyContinue`), so best-effort cleanup after a cancelled
  mount never surfaces a spurious error.
- User-facing error sanitization: `IsoInspectionResult.ErrorMessage` shown by the
  UI is now a generic, friendly string; raw PowerShell errors, HRESULT codes,
  command text, and internal exception detail are retained only in `ILoggerService`.
- Added 4 cancellation/cleanup tests (`IsoInspectionTests`) for: cancellation
  after mount still attempts dismount, cleanup uses a non-cancellable token,
  inspection failure after mount still dismounts, and successful inspection
  dismounts exactly once.
- Fixed `ImageBindingRegressionTests` XAML path resolution to use the compile-time
  source path (`[CallerFilePath]`), so it executes correctly regardless of build
  output redirection.

### Accepted (Phase 1 formal acceptance)
- Phase 1 accepted and merged to `main` on 2026-08-08 (annotated tag
  `v0.1.0-alpha`).
- Validated scope: WPF application shell, MVVM infrastructure, dependency
  injection, navigation service, logging foundation, `AppState`, and ISO file
  selection.
- Merge-readiness fixes included in the accepted baseline: thread-safe logging
  (ADR-014) and read-only WPF binding fix on the Image page.
- Verification: real Windows desktop GUI validation confirmed by the user, in
  addition to the existing headless automated test suite (`WinForge.App.Tests`).
- `dotnet build` / `dotnet test` verified clean: 0 errors, 0 warnings, 10/10
  tests passing. Phase 2 (ISO Inspection) remains NOT STARTED.

### Accepted (Phase 2 Step 2.1 — read-only ISO layout inspection)
- Phase 2 Step 2.1 accepted and merged to `main` on 2026-08-08 via a `--no-ff`
  merge commit (Step 2.1 commits `57c975e`, `66978df` from
  `feature/iso-inspection`). Pre-merge and post-merge `dotnet build` /
  `dotnet test` verified clean: 0 errors, 0 warnings, 29/29 tests passing.
- Delivered capabilities (Step 2.1): ISO native file picker; file basic
  validation; read-only ISO mount; Windows ISO candidate detection
  (`\boot` + `\sources` + `install.wim`/`install.esd`); WIM/ESD type
  recognition; async inspection; UI busy/error state; logging; guaranteed
  dismount; cancellation-safe cleanup (ADR-015); friendly UI error messages
  with raw technical detail retained only in `ILoggerService`; fake-mount
  unit tests; no DISM servicing, no registry, no ISO modification.
- Real Windows 11 25H2 (Chinese Simplified, x64, Consumer ISO, `install.wim`)
  desktop validation completed: the user confirmed the full mount → inspect →
  dismount cycle via application logs. Headless automated tests also pass,
  including cancellation-safe cleanup.
- Step 2.1 does **not** read WIM index, edition, Windows version, build,
  architecture, or language — those belong to Step 2.2. `v0.1.0-alpha` is
  unchanged; the next tag (`v0.2.0-alpha`) is deferred until Phase 2 completes.
