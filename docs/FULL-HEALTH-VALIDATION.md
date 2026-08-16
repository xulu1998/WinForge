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


## Stage 16.1a - Health-check correctness fixes (ADR-098 addendum)

### First real Balanced VM result (recorded)

The first real Balanced end-to-end validation SUCCEEDED through the desktop:
the customized ISO booted in VMware, installed Windows 11 Pro 25H2 build 26200,
completed OOBE and reached the desktop. Commit evidence
`profile-commit-validation.json`: 16 BuildPlan ops / 10 selected / 10 executed /
10 read-back Verified, committed = true, postCommitVerified = true, ISO
structure validated, cleanup discard+workspace succeeded. The first
`full-health-report.json` then failed overall ONLY on three checks that manual
validation proved to be health-check LOGIC defects:

| Check | First report | Manual truth | Verdict |
| --- | --- | --- | --- |
| sfcVerifyOnly | "did not pass (exit 0): \0; \0; \0" | sfc /verifyonly exit 0, "Windows 资源保护未找到任何完整性冲突。" | SFC healthy - script decode/logic defect |
| defaultUser_Start_ShowRecent | missing in Users\Default\NTUSER.DAT | HKCU Start_ShowRecent = 0x0 | customization worked - wrong check target |
| defaultUser_Start_ShowRecommended | missing in Users\Default\NTUSER.DAT | HKCU Start_ShowRecommended = 0x0 | customization worked - wrong check target |

No Windows reinstall was required. Balanced is NOT yet formally
FullHealthValidated - the corrected report must pass first.

### SFC /verifyonly verdict semantics (corrected)

- The verdict is EXIT-CODE authoritative and locale-independent: exit 0 = no
  integrity violations (sfc /verifyonly never repairs, so 0 means clean). The
  localized success marker ("did not find any integrity violations" /
  "\u672a\u627e\u5230\u4efb\u4f55\u5b8c\u6574\u6027\u51b2\u7a81") is used ONLY as corroborating evidence.
- Native output (sfc.exe emits UTF-16LE with a LOW NUL ratio on Chinese
  Windows) is captured via cmd /c file redirection and decoded by a
  candidate-scoring decoder: UTF-16 BOM -> strict UTF-8 -> UTF-16LE (low
  NUL-density heuristic) -> system ANSI; the candidate with the fewest U+FFFD
  wins; NULs are stripped. A successful run can never be failed by capture
  artifacts; genuine failures still Fail. /scannow is never run automatically.
- The C# `SfcVerifyOnlyEvaluator` (Infrastructure/Health) pins this rule; the
  script mirrors it.

### Post-install Default-User semantics (two validation phases)

- IMAGE validation (pre-commit / post-commit WIM): OfflineDefaultUser is
  verified in `Users\Default\NTUSER.DAT` - UNCHANGED and still required.
- INSTALLED-OS validation (full health): settings whose purpose is to seed the
  OOBE-created user's profile are verified in the EFFECTIVE current-user hive
  (HKCU), because Windows/OOBE legitimately consumes the seeded template value
  into the created user's profile. The post-OOBE template is NOT required to
  retain the seed.
- Scope is EXPLICIT per registry expectation (nothing silently reinterpreted):
  `OfflineMachine` (HKLM) / `CurrentUserEffective` (HKCU) /
  `DefaultUserTemplate` (Users\Default\NTUSER.DAT). Balanced: 4 machine
  policies = OfflineMachine; Start_ShowRecommended / Start_ShowRecent =
  CurrentUserEffective. A missing or unknown scope rejects the expected-state
  file.

### Unicode / mojibake

The first report JSON contained mojibake (UTF-8 em dash mis-decoded as GBK).
Root cause: the .ps1 had no UTF-8 BOM, so PowerShell 5.1 parsed it as the
system ANSI code page and mangled non-ASCII string literals. The script is now
pure-ASCII (except the required Chinese SFC marker) with a UTF-8 BOM, and a
test pins the file encoding. Report JSON is valid UTF-8 Unicode.

### FullHealthValidated gate (corrected, ADR-098)

`fullHealthValidated = true` iff: no section Fail anywhere AND the critical
sections (bootAndShell, servicing, security, network) are actually tested
(not NotTested) with no failing check. Warnings - including the real-VM HTTPS
TLS-trust Warning on a VM whose IP/DNS fundamentals Pass - do NOT block.


## Stage 16.1b - FullHealth REQUIRED-vs-OPTIONAL gate (ADR-098 addendum)

### Second real Balanced report (recorded)

The corrected 16.1a script produced a ZERO-failure report: bootAndShell,
devices, security, windowsUpdate, storeAndAppPlatform, profileExpectedChanges
all Pass; windowsIdentity Warning only because activation is Notification
(report-only); network Warning only on HTTPS TLS-trust (IP/DNS Pass);
servicing: dismCheckHealth Pass + sfcVerifyOnly Pass + dismScanHealth
NotTested. The remaining false-negative was the GATE: the optional
DISM /ScanHealth NotTested dragged `servicing.status` to NotTested and blocked
FullHealthValidated despite zero failures.

### Required vs optional model

Every check now carries `requiredForFullHealth` (JSON-exposed; omitted =
required). The gate is required-based, never "worst status of every check":

| Check | Requirement | Real VM |
| --- | --- | --- |
| DISM /CheckHealth | REQUIRED | Pass |
| sfc /verifyonly | REQUIRED | Pass |
| DISM /ScanHealth | OPTIONAL | NotTested (non-blocking) |
| DHCP/IP + DNS | REQUIRED (network) | Pass |
| HTTPS connectivity | OPTIONAL (TLS-trust Warning non-blocking) | Warning |
| activation | OPTIONAL (report-only) | Warning |
| edition/build/arch/language/boot | REQUIRED | Pass |
| Any Fail | BLOCKS (conservative) | none |

Section display status = worst of REQUIRED checks; overallStatus = honest
worst of all required checks + any Warning/Fail optional check; an optional
NotTested never drags overall down and never blocks the gate. `failures = []`
alone is NOT sufficient - an untested REQUIRED check still blocks.

Expected final real result after retest: `overallStatus = Warning`,
`failures = []`, `fullHealthValidated = true`.

## Stage 16.1 COMPLETE — Balanced FullHealthValidated (2026-08-16, real evidence)

| Evidence | Result |
| --- | --- |
| Media | Win11 Pro 25H2 zh-CN x64 (ISO index 4), VMware Workstation Pro |
| Build | 26200.8037 |
| Commit evidence | profile-commit-validation.json — 16 BuildPlan ops / 10 selected / 10 executed / 10 read-back Verified; committed, post-commit verified, ISO structure validated |
| Final health report | failures = [] · overallStatus = Warning · fullHealthValidated = true |
| Required gates | identity ✓ · boot/shell ✓ · devices ✓ · DHCP/IP ✓ · DNS ✓ · DISM CheckHealth ✓ · SFC /verifyonly ✓ · Windows Update components ✓ · Security ✓ · Defender ✓ · Firewall ✓ · Store/runtime platform ✓ · Balanced expected-state ✓ |
| Allowed non-blockers | activation Notification (report-only) · HTTPS TLS-trust Warning (IP/DNS Pass) · optional DISM ScanHealth = NotTested |

Balanced is formally **FullHealthValidated** (ADR-084 top level). This does NOT extend to any other
profile — each profile must earn it with its own installed-OS evidence.

### Non-blocking environment observations (recorded, not product defects)

- One early Windows Setup failure was NOT reproducible and had no retained log; the subsequent clean
  install succeeded.
- The VMware guest occasionally black-screened after idle / sleep behavior.
- Windows Terminal once produced 0xD000003A; PowerShell remained usable through the console host.
- None of these currently provide evidence of WinForge image corruption; none is classified as a
  Passed product defect, and none is hidden.

## Stage 16.2 — DedicatedGaming full-health validation prep (ADR-098 addendum)

Validation profile: **DedicatedGaming** (same source ISO, index 4 — Win11 Pro x64 zh-CN), on a NEW
VMware VM (UEFI / Secure Boot / vTPM / 8 GB RAM / 2 vCPU / 64 GB disk / NAT). Never installed over
the validated Balanced VM.

**Expected-state design** (`scripts/dedicated-gaming-expected-state.json`) — built ONLY from the real
selected-only apply plan (33 BuildPlan ops / 20 selected / 20 executed + read-back Verified):

- 11 provisioned AppX removals: BingSearch, BingWeather, Clipchamp, FeedbackHub, GetHelp, OfficeHub,
  BingNews, OutlookForWindows, PhoneLink (YourPhone), Solitaire, WebExperience.
- 5 OfflineMachine registry values (HKLM after install): DisableSoftLanding=1,
  AdvertisingInfo\Enabled=0, DoNotShowFeedbackNotifications=1, DisableWindowsSpotlightFeatures=1,
  DisableWindowsConsumerFeatures=1.
- 4 CurrentUserEffective registry values (HKCU of the OOBE-created user, seeded via Default-User):
  Start_ShowRecent=0, Start_ShowRecommended=0, EnableWebContent=0 (Dsh), TaskbarSearch=1 (search
  icon only).

**Recommend-only candidates (Containers, WSL, DevHome, OneDriveSync, ...) were NOT executed in the
real plan and are intentionally NOT expected states** — BuildPlan candidates != SelectedOperations
is preserved in the expected-state contract. The health engine is not forked: the same
`Validate-WinForgeInstallation.ps1` runs with `-ProfileId DedicatedGaming -ExpectedJson
dedicated-gaming-expected-state.json`.

**Preserved-platform requirements (DedicatedGaming is NOT a kiosk profile):** Defender, firewall,
Windows Update, Microsoft Store + App Installer/winget, DirectX/runtime platform, network, display,
input, servicing, boot/recovery health must all remain healthy — enforced by the existing
security/windowsUpdate/storeAndAppPlatform/devices/servicing/boot sections.

**Acceptance (mirrors Balanced):** ISO build/commit verified → VM Setup completes → OOBE → desktop →
health report generated → all required checks + profile expected-state checks Pass →
fullHealthValidated = true; warnings only per ADR-098 rules.
