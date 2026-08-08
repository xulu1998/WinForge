# Changelog

All notable user-visible changes to WinForge are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/).

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
