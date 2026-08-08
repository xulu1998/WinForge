# Project Status

> Authoritative live status of the WinForge project. Every Agent must update
> this file after completing a task.

## Summary

| Field | Value |
|-------|-------|
| Project | WinForge |
| Version | 0.1.0-alpha |
| Current Phase | Phase 2 — ISO Inspection (IN PROGRESS) |
| Current Milestone | Phase 2 — ISO Inspection / Step 2.1 |
| Repository | https://github.com/xulu1998/WinForge |
| Platform | Windows 11 |
| Framework | .NET 8 (WPF, MVVM) |

## Progress

### Completed
- Phase 0 — Project Governance (governance docs, roadmap, agent memory system)
- Phase 1 — Application Foundation (solution, WPF shell, MVVM infra, DI, logging, error handling, Core smoke tests)
- Phase 1 headless automated validation completed (WinForge.App.Tests boot / navigation / logging integration test)
- Phase 1 real Windows desktop GUI validation completed (user-confirmed on a real Windows desktop)

### In Progress
- Phase 2 — ISO Inspection / **Step 2.1** (read-only, non-destructive): user selects a `.iso` via the native picker; the app validates the file and inspects the on-disk layout through a read-only mount (`WindowsIsoMountService` → `Mount-DiskImage`, always dismounted) to detect a Windows ISO candidate (`\sources` + `\boot` + `install.wim`/`install.esd`). No DISM servicing, WIM parsing, edition/version recognition, mount/extraction pipeline, or registry access. Step 2.1 is implemented on `feature/iso-inspection`; not yet merged to `main`, not tagged.

### Next
- Phase 2 — ISO Inspection: remaining steps (WIM index / edition / version inspection) follow after Step 2.1 is accepted.

### Maintenance
- 2026-08-08 — **Phase 1 merge-readiness fix (logging thread-safety).** `InMemoryLoggerService` now uses a lock-guarded `List<LogEntry>` with a thread-safe snapshot `Entries` and no WPF dependency; `LogsViewModel` marshals background-thread log events to the UI thread via `SynchronizationContext` (ADR-014). Phase 1 remains **COMPLETED**; Phase 2 is **NOT STARTED**. This was a fix only — no Phase 2 functionality was added.
- 2026-08-08 — **Phase 1 merge-readiness fix (read-only WPF binding).** The Image page `TextBox` for `ImageViewModel.FileDisplay` used the default `TwoWay` mode against a getter-only property, throwing at runtime on real Windows desktops. Changed to `Mode=OneWay` (no setter added to `FileDisplay`). Audited all Phase 1 XAML; this was the only default-TwoWay control bound to a read-only property. Added `ImageBindingRegressionTests`. Phase 1 remains **COMPLETED**; Phase 2 is **NOT STARTED**.
- 2026-08-08 — **Phase 1 formally accepted and merged to `main`.** All Phase 1 commits from `feature/application-foundation` (`ac18789`, `89009bb`, `f2f919d`) merged into `main` via a `--no-ff` merge commit; pre-merge and post-merge `dotnet build`/`dotnet test` verified clean (0 errors, 0 warnings, 10/10 tests passing). Annotated tag `v0.1.0-alpha` created and pushed. `feature/application-foundation` retained for history. Phase 1 remains **COMPLETED**; Phase 2 is **NOT STARTED**.
- 2026-08-08 — **Phase 2 — ISO Inspection / Step 2.1 (read-only inspection).** On `feature/iso-inspection`: added Core model/enums (`IsoInspectionResult`, `IsoDetectedType`, `InstallImageType`, `IsoInspectionStatus`) and contracts (`IIsoInspectionService`, `IIsoMountService`); Infrastructure `WindowsIsoInspectionService` (file validation + read-only mount + layout detection, always dismounts) and `WindowsIsoMountService` (`Mount-DiskImage`/`Dismount-DiskImage` via PowerShell, base64-encoded script, safe cleanup). App `IFilePicker`/`WindowsFilePicker`, `ImageViewModel` refactored to `SelectIsoCommand`/`InspectIsoCommand` (async, busy/error states, no UI-thread mount). Added 15 automated tests (9 inspection-logic + 6 ViewModel). Phase 2 is **IN PROGRESS**; not merged, no new tag (v0.1.0-alpha unchanged).
- 2026-08-08 — **Phase 2 Step 2.1 merge-readiness fix (cancellation-safe ISO cleanup).** `WindowsIsoInspectionService` now tracks `mountAttempted` and always attempts a best-effort `Dismount-DiskImage` in the `finally` block using `CancellationToken.None`, so cancellation or a failure that occurs before the mounted root is obtained can never leave an ISO mounted (ADR-015). `OperationCanceledException` is re-thrown rather than swallowed by successful cleanup; other failures surface as a `Failed` result. `WindowsIsoMountService` dismount is safe when the image is not mounted (`-ErrorAction SilentlyContinue`). User-facing `ErrorMessage` is now a generic, friendly string — raw PowerShell/HRESULT/command detail is retained only in `ILoggerService`. Added 4 cancellation/cleanup tests; fixed `ImageBindingRegressionTests` XAML path resolution to be independent of build-output redirection (`[CallerFilePath]`). Phase 2 is **IN PROGRESS**; Step 2.1 remains **pending merge**; no new tag.

### Known Issues
- ISO inspection (Step 2.1) detects Windows ISO *candidates* by on-disk directory layout only. WIM index / edition / version / architecture parsing is **not** implemented yet — it is a later Phase 2 step.
- Real Windows desktop GUI validation of the read-only mount/dismount cycle (including mid-mount cancellation) requires a physical Windows ISO and was **not** performed in this automated (headless) environment; headless automated validation passed (incl. cancellation-safe cleanup unit tests). Windows version compatibility is still not claimed (see docs/WINDOWS-COMPATIBILITY.md).

### Blocked
- _(none)_

## Verification

| Field | Value |
|-------|-------|
| Last Verified Baseline Commit | 2bcae63e9217714e9d55ad0265ed99569423ed20 |
| Last Updated | 2026-08-08 |

> **Baseline commit policy:** `Last Verified Baseline Commit` is the most recent
> confirmed-stable commit *before* the current phase's work, not necessarily the
> current HEAD. It is updated when a phase is accepted, pointing at the stable
> point the phase started from. Do not record a commit's own SHA here.
