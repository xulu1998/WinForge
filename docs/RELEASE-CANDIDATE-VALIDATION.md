# Release-Candidate Validation (Phase 17)

Phase 17 hardens WinForge for an actual release-candidate workflow. It does NOT
expand the destructive scope (CBS packages, Capability execution, Driver/Service/
ScheduledTask/SystemApp/WinRecovery removal remain future phases — ADR-086 and
Stage 17.10). The priority is reproducible release artifacts, non-overwriting
validation evidence, remaining-profile validation preparation, a truthful
release-confidence matrix, user-facing RC readiness, and deterministic
diagnostics/recovery.

## 17.1 — Validation artifact archive

Single-file overwrite artifacts (`profile-commit-validation.json`,
`full-health-report.json`) are replaced by deterministic archival:

```
.tmp\validation\
  latest.json                       <- pointer to the most recent run (never deletes history)
  <runId>\                          <- e.g. 20260816-130500-<short-sha>-Balanced
    manifest.json                   <- full ValidationArtifactRun metadata
    <profile>-expected-state.json
    profile-plan.json               <- plan snapshot (canonical + selected keys, counts)
    profile-commit-validation.json  <- when --commit chained
    bundle\                         <- portable FullHealth bundle (17.7)
```

Every run records: runId, timestamp (UTC), source ISO path (+ SHA when
available), profile, Windows index, edition, language, architecture, WinForge
commit SHA, generated ISO path + SHA-256, validation level, result status,
pipeline phase reached, and the archived files. `latest.json` is the ONLY
mutable entry — history is never overwritten, and task artifacts never land in
`F:\` root (they live under the repo `.tmp\validation`).

## 17.2 — Release validation manifest

`ReleaseValidationManifestService` produces a machine-readable manifest
(`release-validation-manifest.json` shape) that summarizes every built-in
profile with boolean, evidence-gated levels:

| Profile | WorkflowValidated | VmInstallValidated | FullHealthValidated |
| --- | --- | --- | --- |
| Balanced | ✓ (Phase 15) | ✓ (Phase 16) | **✓ (Phase 16, real VM)** |
| Gaming | ✓ (Phase 15) | — | — |
| DedicatedGaming | ✓ (Phase 15) | ✓ (Phase 16) | **✓ (Phase 16, real VM)** |
| Developer | ✓ (Phase 15) | — | — |
| Office | ✓ (Phase 15) | — | — |
| Lightweight | ✓ (Phase 15) | — | — |

Each entry also carries last-validated commit, source Windows build, BuildPlan
count, selected count, ISO SHA-256 when VM-validated, health-report reference,
warnings and validation debt. The levels live in a single static evidence table
and are deliberately boolean flags — a profile can never accidentally claim a
higher level than demonstrated. **This is evidence, not marketing.**

## 17.3 — Remaining profile expected states

`scripts/<profile>-expected-state.json` now exists for all six primaries. The
four Phase-17 files (Gaming/Developer/Office/Lightweight) were derived ONLY
from the real Phase 15 plan evidence (profile-plans.json `AutoApply` rows +
profile-buildplans.json canonical keys), cross-checked 1:1:

| Profile | Selected | AppX | Registry | Services |
| --- | --- | --- | --- | --- |
| Gaming | 19 | 10 | 9 | 0 |
| Developer | 18 | 6 | 12 | 0 |
| Office | 10 | 4 | 6 | 0 |
| Lightweight | 24 | 6 | 13 | 5 |

Recommend-only candidates (Containers, WSL, DevHome, OneDriveSync, HyperV,
DiagTrack, …) are excluded. Registry scopes are explicit
(OfflineMachine/CurrentUserEffective). `ProfileExpectedState.ServicesDisabled`
(Phase 17) supports the Lightweight service expectations; the health script
verifies `StartMode = Disabled` (absent = already-satisfied). The
`ExpectedStateBuilder` regenerates the document from the FINAL selected plan
operations so it can never drift from the executable plan.

## 17.4 — Six-profile delta audit

`ProfileDeltaAuditService` emits a machine-readable comparison: common selected
keys, per-profile exclusive keys, operation-type distribution, Recommend-only
rows, and convergence warnings. Real evidence (Phase 15):

- Balanced 16/10 (3 appx · 6 reg · 1 svc) vs Gaming 25/19 (10 appx · 9 reg) —
  materially different.
- Gaming 19 ≠ DedicatedGaming 20 (11 appx · 9 reg) — different.
- Developer 21/18 (6 appx · 12 reg — the only primary selecting
  ShowHiddenFiles/ShowFileExtensions) — meaningful developer-specific delta.
- Office 17/10 (4 appx · 6 reg) — NOT a no-op, and deliberately lighter.
- Lightweight 38/24 (6 appx · 13 reg · **5 services**) — materially the
  strongest general-purpose profile; the only primary with service changes.
- No two primaries converge (each has a distinct type mix; the audit also
  detects exact set equality programmatically).

## 17.5 — Release safety invariants

`ReleaseSafetyInvariantSet` defines the invariants every built-in profile must
preserve: Defender (WinDefend/SecurityHealthService/wscsvc + SecHealthUI),
Firewall (mpssvc/BFE/SharedAccess), Windows Update (wuauserv/UsoSvc/
WaaSMedicSvc), Store (WindowsStore/StorePurchaseApp), App Installer
(DesktopAppInstaller/winget), boot shell (ShellExperienceHost/StartMenu),
servicing stack (CBS/Capability/TrustedInstaller), network stack (Dhcp/Dnscache/
NlaSvc + drivers), display/input, recovery (WinRE), no host HKCU writes, no
unknown-mount discard, no host registry contamination. `CheckPlan` enforces the
invariants against the executable plan (key prefixes + protected services) and
participates in the test suite — a future profile change that touches any
protected component fails deterministically.

## 17.6 — Profile validation runner

`WinForge.RealCapture --validation-run <ProfileId> [--commit] [--bundle-dir <dir>]`
orchestrates a full profile validation run through the PRODUCTION pipeline:

profile plan → expected-state resolution (from the SELECTED operations only) →
run archive under `.tmp\validation\<runId>\` → (optional `--commit`: chain the
commit + ISO build and archive its evidence) → portable FullHealth bundle →
discard-only cleanup. No VMware UI, no OOBE automation, no second ISO build
engine. `--commit` intent is explicit and mutually exclusive with the other
modes; an unknown mount is never discarded.

## 17.7 — Portable FullHealth input bundle

Each run generates a small portable bundle (no credentials, no large ISO
duplication): `Validate-WinForgeInstallation.ps1`,
`<profile>-expected-state.json`, `validation-manifest.json` and a `README.txt`
whose health-check command ALREADY contains the exact arguments:

```
powershell -ExecutionPolicy Bypass -File Validate-WinForgeInstallation.ps1 `
  -ProfileId <ProfileId> -MediaId "WinForge-<Profile>-Win11-25H2-Pro-zh-CN-x64.iso" `
  -ExpectedJson <profile>-expected-state.json -IsoSha256 "<sha>" `
  -OutputPath "$env:USERPROFILE\Desktop\full-health-report.json"
```

The user copies the bundle into the VM and runs the README command — no manual
reconstruction of SHA/profile arguments.

## 17.8 — RC status UI / diagnostics (audit)

Audit of the existing wizard (no redesign, direction preserved):

- Source step already shows the selected ISO and Windows edition/index
  (SourceView.xaml; ImageView.xaml is legacy and unused at runtime).
- Customize/Review steps show per-profile decisions (AutoApply/Recommend/
  Optional/Kept/Blocked) with exact change counts; recommendations are shown as
  suggestions and are never applied without selection — the review step makes
  "recommendations not automatically applied" explicit.
- Build destination is deterministic and shown at commit time
  (`Documents\WinForge\WinForge-<Profile>-...iso`), with success/failure
  surfaced in the plan outcome panel.
- Validation/release-confidence status is exposed through the artifacts and the
  release validation manifest rather than raw implementation jargon; the
  in-VM health report carries the human-readable overallStatus.

No risky UI code changes were made in this stage; the audit notes are recorded
here so RC-specific polish (e.g. a one-line validation-level badge in the
Review step) can be scheduled as a follow-up without destabilizing the wizard.

## 17.9 — Failure recovery (formalized)

- **Stale workspace / Needs Remount / interrupted commit or ISO build:** the run
  manifest records `phase` + `resultStatus` (Prepared/Interrupted/Failed/
  Succeeded); `ListInterruptedRuns()` surfaces every interrupted/failed run from
  the archive for diagnosis. Recovery never auto-discards anything.
- **Unknown mounted WIM:** never discarded — authoritative
  `dism /Get-MountedImageInfo` inventory + session ownership guard only (Phase 12
  UnmountDiscardAsync unchanged).
- **oscdimg missing / insufficient disk space:** surfaced as explicit build
  failures with the failing phase recorded; nothing is reported successful.
- **Artifact archive collision:** impossible by construction — runIds are
  UTC-timestamp + commit-SHA + profile unique; `latest.json` is the only mutable
  entry.
- **Invalid expected-state schema:** `ProfileExpectedStateParser` rejects the
  file (null) and the health script reports `expectedState` NotTested with the
  path — a bad file can never produce a false Pass.

## 17.10 — Explicitly deferred destructive scope

Destructive CBS package removal, broad Capability execution, Driver removal,
aggressive Service removal, ScheduledTask removal, SystemApp removal and
WinRecovery removal remain future phases. Phase 17 is release hardening, not
deeper debloat — the safety invariants above make any accidental expansion
deterministically fail the test suite.
