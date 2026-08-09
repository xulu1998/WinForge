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
