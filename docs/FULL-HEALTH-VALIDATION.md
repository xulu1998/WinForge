# Full Health Validation (Phase 16 Stage 16.1)

## Purpose

Stage 16.1 proves that a real WinForge-generated customized Windows ISO works
END-TO-END:

```
Profile → Customize state → validated BuildPlan → Apply → COMMIT WIM → build ISO
→ VMware installation → OOBE → desktop → post-install health validation
```

The first profile under validation is **Balanced** only (safest primary, already
real-Apply validated, exercises AppX + registry customization, minimal
confounding variables). Lightweight and destructive CBS/Capability work are
deliberately NOT first.

## Validation levels (ADR-084 boundary)

| Level | Meaning | Evidence required |
| --- | --- | --- |
| **WorkflowValidated** | The toolchain runs end-to-end in an isolated/discard-only context against real media | Structural BuildPlan validation + real offline Apply with read-back + cleanup (Phase 15) |
| **VmInstallValidated** | A WinForge-produced ISO actually boots in a VM and Windows installs | ISO generated → VM Setup boots → Windows installed → OOBE → desktop (no health report required) |
| **FullHealthValidated** | The installed OS passes structured post-install health validation | `VmInstallValidated` + `full-health-report.json` with `fullHealthValidated: true` (no critical servicing/security/network/shell failures) |

Phase 15 delivered **WorkflowValidated** for the Profile Execution / Apply
pipeline (real 25H2 Index 4: six primaries structurally validated; Balanced
10/10 and DedicatedGaming 20/20 real offline Apply executed + read-back
Verified; mounts discarded). Phase 16 targets **FullHealthValidated** for
Balanced. **A booting ISO alone never earns FullHealthValidated** — the health
report must complete with no critical failures.

## Balanced ISO output

Deterministic output (never the repo, never the source ISO):

- Default: `Documents\WinForge\WinForge-Balanced-Win11-25H2-Pro-zh-CN-x64.iso`
- Override: `--iso-out <dir>` / `--iso-name <name>`
- Report carries: output path, size, SHA-256, source media identity, selected
  index, profile, BuildPlan count, selected-execution count.

## Commit mode safety

The Phase 16 commit path is EXPLICIT and separate from the Phase 15
discard-only apply path:

- `--apply-profile` (Stage 15.4) never commits — it always discards.
- `--commit-profile` (Stage 16.1) is the ONLY way to commit + build an ISO.
  The two flags are mutually exclusive in the CLI.
- Before committing: the pre-commit read-back gate requires EVERY attempted
  operation Verified; the commit-mode ownership guard requires session-owned
  paths AND the authoritative DISM mount inventory to show ONLY the owned mount
  (an unknown mount aborts the run).
- The source ISO is never written; the working WIM is workspace-owned; the
  output ISO is written only to the user-chosen output directory with
  `BuildOverwritePolicy.Fail` (deterministic — no silent overwrite).

## Pre-commit / post-commit verification

- **Pre-commit** (mounted image): re-verify all selected Balanced operations —
  AppX absence, machine registry, Default-User registry, service if selected.
  Any failure → DO NOT commit.
- **Post-commit** (committed WIM re-opened after commit + unmount): re-mount the
  committed image and independently re-verify representative changes persisted
  (removed AppX still absent, machine policy registry persisted, Default User
  registry persisted, DISM metadata query succeeds).

## ISO structure validation

The produced ISO is verified by the production `BuildVerifier` (output exists
and non-empty, final install.wim present and queryable with the expected
edition/index, no WIM remains mounted) plus an explicit structure check in the
commit report: `boot\etfsboot.com`, `efi\microsoft\boot\efisys.bin` (UEFI),
`sources\boot.wim`, `sources\install.wim`, `setup.exe`. ISO validity is never
inferred from the oscdimg exit code alone.

## VMware validation (manual checklist — VMware Workstation Pro)

1. New VM: **Windows 11 x64**, UEFI firmware (`UEFI` — not BIOS), **vTPM
   enabled** (Windows 11 requirement; Workstation 16/17: VM Settings → Advanced
   or add `Trusted Platform Module` device), **4 GB RAM minimum (8 GB
   recommended)**, **2+ vCPU**, **60 GB+ virtual disk** (thin provisioning OK),
   attach the WinForge Balanced ISO to the virtual CD/DVD.
2. Boot → Windows Setup launches → select the target disk → installation
   completes → first reboot succeeds.
3. OOBE completes → local/Microsoft account path behaves normally per stock
   Windows (no OOBE bypass changes are introduced in this stage).
4. Desktop is reached with no setup crash/loop.
5. Run the health validator inside the VM (Administrator):

```powershell
# copy scripts\Validate-WinForgeInstallation.ps1 + scripts\balanced-expected-state.json into the VM
powershell -ExecutionPolicy Bypass -File Validate-WinForgeInstallation.ps1 `
    -ProfileId Balanced -MediaId "WinForge-Balanced-Win11-25H2-Pro-zh-CN-x64.iso" `
    -IsoSha256 "<host-computed SHA-256 of the ISO>" `
    -OutputPath "$env:USERPROFILE\Desktop\full-health-report.json"
```

6. Upload `full-health-report.json` to the repo (`F:\Projects\WinForge\.tmp\phase16-real\`).

## Health report schema (`full-health-report.json`)

Status vocabulary: `Pass | Warning | Fail | NotTested` (no binary false
confidence). Sections: `media`, `profile`, `windowsIdentity`, `bootAndShell`,
`devices`, `network`, `servicing`, `windowsUpdate`, `security`,
`storeAndAppPlatform`, `profileExpectedChanges`, plus `overallStatus`,
`warnings`, `failures`, `fullHealthValidated`. The host-side
`HealthReportParser` re-aggregates statuses authoritatively
(Fail > Warning > NotTested > Pass) and recomputes `fullHealthValidated`.

### Balanced expected-state checks (profileExpectedChanges)

- AppX absent: Feedback Hub, Phone Link, Solitaire (`balanced-expected-state.json`).
- Machine registry: `AdvertisingInfo\Enabled = 0`,
  `CloudContent\DisableWindowsConsumerFeatures = 1`,
  `DataCollection\DoNotShowFeedbackNotifications = 1`,
  `CloudContent\DisableWindowsSpotlightFeatures = 1`.
- Default User registry (via `reg load` of `C:\Users\Default\NTUSER.DAT` —
  never the host HKCU): `Explorer\Advanced\Start_ShowRecommended = 0`,
  `Start_ShowRecent = 0`.

## FullHealthValidated gate (Balanced)

All of the following must hold — `fullHealthValidated: true` in the report:

- ISO generated successfully (commit report `Committed = true`, ISO structure
  validated, SHA-256 recorded)
- VM Setup booted; Windows installed; OOBE completed; desktop reached
- health report completed (schema-valid)
- no critical failures: `bootAndShell`, `servicing`, `security`, `network`
  sections Pass (actually tested — NotTested blocks), no section Fail

Warnings (offline VM, unactivated Windows, missing VMware audio) do NOT block
validation but must be reviewed. Other profiles keep their current validation
level until they pass the same gate.
