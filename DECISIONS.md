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
