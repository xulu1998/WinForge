# Project Status

> Authoritative live status of the WinForge project. Every Agent must update
> this file after completing a task.

## Summary

| Field | Value |
|-------|-------|
| Project | WinForge |
| Version | 0.1.0-alpha |
| Current Phase | Phase 2 — ISO Inspection (IN PROGRESS) |
| Current Milestone | Phase 2 — ISO Inspection / Step 2.2 (implemented on `feature/iso-inspection`; pending real Windows ISO desktop validation) |
| Repository | https://github.com/xulu1998/WinForge |
| Platform | Windows 11 |
| Framework | .NET 8 (WPF, MVVM) |

## Progress

### Completed
- Phase 0 — Project Governance (governance docs, roadmap, agent memory system)
- Phase 1 — Application Foundation (solution, WPF shell, MVVM infra, DI, logging, error handling, Core smoke tests)
- Phase 1 headless automated validation completed (WinForge.App.Tests boot / navigation / logging integration test)
- Phase 1 real Windows desktop GUI validation completed (user-confirmed on a real Windows desktop)
- Phase 2 Step 2.1 — Read-only ISO layout inspection (ISO native file picker, file validation, read-only mount, Windows ISO candidate detection: `\boot` + `\sources` + `install.wim`/`install.esd`, WIM/ESD type, async inspection, UI busy/error state, logging, guaranteed/cancellation-safe dismount, friendly UI errors). 19 automated tests added. **Real Windows 11 25H2 (zh-CN, x64, Consumer ISO, install.wim) desktop validation completed** (mount → inspect → dismount verified by user via application logs). Accepted and merged to `main` on 2026-08-08.
- Phase 2 Step 2.2 — Windows image metadata & edition inspection (WIM/ESD index, edition name/description, architecture, Windows version, build, edition ID, installation type, languages; read-only via `dism.exe /English /Get-WimInfo`; `IProcessRunner` abstraction; combined mount→layout→metadata→dismount session preserving ADR-015; Image page "Windows information" + editions list; edition selection → `IAppState.SelectedEdition`). Implemented on `feature/iso-inspection`; automated tests pass (0 errors, 0 warnings, 100% executed). **Pending real Windows ISO desktop validation** — NOT yet COMPLETED.

### In Progress
- Phase 2 — ISO Inspection: **Step 2.1 accepted and merged to `main`** (2026-08-08). **Step 2.2 implemented, pending real Windows ISO desktop validation** (do not mark COMPLETED until the Windows 11 25H2 zh-CN x64 Consumer `install.wim` metadata inspection is confirmed by the user on a Windows desktop). Next after Step 2.2: Step 2.3.

### Next
- Phase 2 — ISO Inspection: remaining steps (WIM index / edition / version inspection) follow after Step 2.1 is accepted.

### Maintenance
- 2026-08-08 — **Phase 2 Step 2.2 implemented on `feature/iso-inspection` (pending real ISO desktop validation).** Added `IWindowsImageMetadataService` + `WindowsImageMetadataResult` + `WindowsEditionInfo` (extended) in Core; `WindowsImageMetadataService` (DISM `/English /Get-WimInfo`) + pure `DismWimInfoParser` + `IProcessRunner`/`WindowsProcessRunner` in Infrastructure; extended `WindowsIsoInspectionService` into a single mount→layout→metadata→dismount session (ADR-015 preserved). Image page shows Windows information + editions list; edition selection writes `IAppState.SelectedEdition`. 16 new automated tests added (parser, service via fake process runner, orchestrator lifecycle, ViewModel/Home). `dotnet build`/`dotnet test -c Release` verified clean (0 errors, 0 warnings, 100% tests executed and passing). Not merged to `main`, no new tag; Step 2.2 NOT COMPLETED until real-desktop validation.
- 2026-08-08 — **Phase 2 Step 2.1 accepted and merged to `main`.** All Step 2.1 commits from `feature/iso-inspection` (`57c975e`, `66978df`) merged into `main` via a `--no-ff` merge commit; pre-merge and post-merge `dotnet build`/`dotnet test` verified clean (0 errors, 0 warnings, 29/29 tests passing). Real Windows 11 25H2 (zh-CN, x64, Consumer ISO, install.wim) desktop validation completed (mount → inspect → dismount confirmed by user logs). `v0.1.0-alpha` unchanged; Step 2.2 NOT STARTED; `feature/iso-inspection` retained for Step 2.2.
- 2026-08-08 — **Phase 1 merge-readiness fix (logging thread-safety).** `InMemoryLoggerService` now uses a lock-guarded `List<LogEntry>` with a thread-safe snapshot `Entries` and no WPF dependency; `LogsViewModel` marshals background-thread log events to the UI thread via `SynchronizationContext` (ADR-014). Phase 1 remains **COMPLETED**; Phase 2 is **NOT STARTED**. This was a fix only — no Phase 2 functionality was added.
- 2026-08-08 — **Phase 1 merge-readiness fix (read-only WPF binding).** The Image page `TextBox` for `ImageViewModel.FileDisplay` used the default `TwoWay` mode against a getter-only property, throwing at runtime on real Windows desktops. Changed to `Mode=OneWay` (no setter added to `FileDisplay`). Audited all Phase 1 XAML; this was the only default-TwoWay control bound to a read-only property. Added `ImageBindingRegressionTests`. Phase 1 remains **COMPLETED**; Phase 2 is **NOT STARTED**.
- 2026-08-08 — **Phase 1 formally accepted and merged to `main`.** All Phase 1 commits from `feature/application-foundation` (`ac18789`, `89009bb`, `f2f919d`) merged into `main` via a `--no-ff` merge commit; pre-merge and post-merge `dotnet build`/`dotnet test` verified clean (0 errors, 0 warnings, 10/10 tests passing). Annotated tag `v0.1.0-alpha` created and pushed. `feature/application-foundation` retained for history. Phase 1 remains **COMPLETED**; Phase 2 is **NOT STARTED**.
- 2026-08-08 — **Phase 2 — ISO Inspection / Step 2.1 (read-only inspection).** On `feature/iso-inspection`: added Core model/enums (`IsoInspectionResult`, `IsoDetectedType`, `InstallImageType`, `IsoInspectionStatus`) and contracts (`IIsoInspectionService`, `IIsoMountService`); Infrastructure `WindowsIsoInspectionService` (file validation + read-only mount + layout detection, always dismounts) and `WindowsIsoMountService` (`Mount-DiskImage`/`Dismount-DiskImage` via PowerShell, base64-encoded script, safe cleanup). App `IFilePicker`/`WindowsFilePicker`, `ImageViewModel` refactored to `SelectIsoCommand`/`InspectIsoCommand` (async, busy/error states, no UI-thread mount). Added 15 automated tests (9 inspection-logic + 6 ViewModel). Phase 2 is **IN PROGRESS**; not merged, no new tag (v0.1.0-alpha unchanged).
- 2026-08-08 — **Phase 2 Step 2.1 merge-readiness fix (cancellation-safe ISO cleanup).** `WindowsIsoInspectionService` now tracks `mountAttempted` and always attempts a best-effort `Dismount-DiskImage` in the `finally` block using `CancellationToken.None`, so cancellation or a failure that occurs before the mounted root is obtained can never leave an ISO mounted (ADR-015). `OperationCanceledException` is re-thrown rather than swallowed by successful cleanup; other failures surface as a `Failed` result. `WindowsIsoMountService` dismount is safe when the image is not mounted (`-ErrorAction SilentlyContinue`). User-facing `ErrorMessage` is now a generic, friendly string — raw PowerShell/HRESULT/command detail is retained only in `ILoggerService`. Added 4 cancellation/cleanup tests; fixed `ImageBindingRegressionTests` XAML path resolution to be independent of build-output redirection (`[CallerFilePath]`). Phase 2 is **IN PROGRESS**; Step 2.1 remains **pending merge**; no new tag.

### Known Issues
- WIM index / edition / version / architecture / language parsing is implemented (Step 2.2) but the end-to-end mount → inspect → dismount → **metadata** cycle on a real Windows ISO is **not yet validated on a Windows desktop** — automated tests use fakes. Until the user confirms the real Windows 11 25H2 zh-CN x64 Consumer `install.wim` metadata read, Step 2.2 stays NOT COMPLETED.
- Real Windows 11 25H2 (zh-CN, x64, Consumer ISO, install.wim) desktop validation of the read-only mount/inspect/dismount cycle **was completed** (Step 2.1); the user confirmed the full flow via application logs. Windows **version** compatibility is still not claimed as `Supported` — only ISO-layout `Inspection` is `Tested` for that release (see docs/WINDOWS-COMPATIBILITY.md).

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
