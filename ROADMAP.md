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

- **Status:** NOT STARTED
- **Goal:** Inspect an official Microsoft Windows 11 ISO non-destructively.
- **Scope:** ISO metadata reading, edition enumeration, architecture/language
  detection, install.wim / install.esd detection.
- **Deliverables:**
  - `IIsoInspectionService` (Core) + DISM/ISO-backed implementation (Infrastructure)
  - App "Image" page skeleton showing inspection results
- **Acceptance Criteria:**
  - Opening an official ISO lists editions, architecture, languages, and image
    type without modifying the source.

---

## Phase 3 — WIM Engine

- **Status:** NOT STARTED
- **Goal:** WIM / ESD image handling via documented Microsoft mechanisms.
- **Scope:** Enumerate images, read image info, export ESD → WIM, index selection.
- **Deliverables:**
  - `IWimService` (Core) + DISM-backed implementation (Infrastructure)
- **Acceptance Criteria:**
  - Enumerate images in install.wim / install.esd.
  - Export an ESD image to WIM without data loss.

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
