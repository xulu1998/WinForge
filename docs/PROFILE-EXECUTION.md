# Profile Execution & Safe Execution Matrix (Phase 15 Stage 15.1, ADR-094)

Profiles must produce clearly different Windows images — Balanced, Gaming PC,
Dedicated Gaming, Developer, Office and Lightweight must NOT end up with nearly
the same plan. But differentiation comes only from real, explainable, SUPPORTED
changes: no placebo optimization, no unsafe "debloat everything".

This document is the canonical description of the Stage 15.1 execution layer.

## 1. The execution matrix (ProfileExecutionMatrix)

The matrix converts profile knowledge into an explicit disposition per item:

| Disposition | Meaning |
| --- | --- |
| AutoApply | Applied automatically (safe, curated, Low-risk, supported, profile-driven) |
| Recommend | Recommended change — user confirms via 采用推荐 / adopt |
| Optional | Optional, user-confirmed suggestion |
| Keep | Kept for compatibility (requirement / dependency / extras / protection / runtime) |
| Blocked | Blocked by safety or missing execution support — never in an executable plan |
| NotApplicable | Absent / unknown / no steer |

The PRIMARY decision mechanism is knowledge + risk + protection + confidence +
execution support — never raw Windows identity strings. The matrix consumes the
engine's profile-aware `EffectiveRecommendation` plus the item's
`ComponentProtectionLevel`, `ClassificationConfidence`, and execution support.

Deterministic rules (re-verified from Phase 14, unchanged):

- Protected → Keep (never acted on)
- Critical → Blocked (never)
- High → never AutoApply (Recommend at most, §11)
- Low + curated/known + supported + profile-driven → AutoApply
- Heuristic → never AutoApply (Recommend at most)
- Unsupported execution → Blocked (Known != Removable, ADR-086/093)
- ManualReview → Optional (engine reason preserved, e.g. Dedicated's media suggestion)
- User override → authoritative; never auto-applied; survives profile changes

## 2. Execution support matrix (ExecutionSupportMatrix) — auditable honesty

Recommendation is SEPARATE from ExecutionSupport. What WinForge can actually
execute on an offline mounted image TODAY:

| Operation type | Support | Notes |
| --- | --- | --- |
| AppX removal (`RemoveProvisionedAppx`) | Supported | validated path |
| Registry policy (`SetOfflineRegistryValue` / `DeleteOfflineRegistryValue`) | Supported | Phase 12 validated |
| Privacy settings | Supported | registry-backed |
| Personalization | Supported | registry-backed |
| OptionalFeature disable (`DisableOptionalFeature`) | Supported | mounted-image DISM |
| Service configuration (`ConfigureOfflineService`) | Conditional | allowlisted services only |
| Capability removal (`RemoveCapability`) | **NotSupported** | execution path not reviewed |
| CBS package removal (`RemovePackage`) | **NotSupported** | no destructive CBS removal |
| Driver removal | **NotSupported** | no operation type |
| Scheduled-task disable | Conditional/Not | only when robust support exists |

Classification NEVER promotes itself into execution capability. The matrix is
the auditable statement of that boundary; blocked types are never placed in an
executable plan.

## 3. Profile intents (the six primaries)

- **Balanced** — safe general-purpose cleanup, minimal surprise: low-risk
  consumer cleanup + privacy + conservative app cleanup; no aggressive feature
  stripping.
- **Gaming PC** — normal personal gaming PC: low-risk consumer trims (Phone
  Link, Solitaire, Get Help, Feedback Hub, consumer suggestions, Spotlight
  consumer content, ads/tailored experiences, widgets/news, web search where
  supported, Outlook/Office Hub where execution is safely supported), Dev Home
  optional; Xbox/Game Pass ecosystem retained when requested; Store/Gaming
  Services/runtime/DirectX kept; drivers/network/audio/input kept. No
  HPET/BCD/timer/memory mythology.
- **Dedicated Gaming** — more minimal gaming-focused Windows, still
  health/compatibility-first: wider OPTIONAL set (moderate consumer/media
  apps), never kiosk, never Defender/Windows Update/servicing/runtime/
  network/audio/input/GPU stripping, no arbitrary CBS removal.
- **Developer** — clean dev Windows: keeps Terminal/Notepad/App Installer
  (winget)/Store/Dev Home/WSL-Docker-HyperV stack (when extra enabled)/OpenSSH/
  WebDriver/networking/runtimes; trims unrelated consumer content.
- **Office** — stable productivity workstation: keeps printing/scanning,
  Office integration, Outlook where selected, OneDrive when relevant, mainstream
  codecs, Remote Desktop when extra enabled, security/update infrastructure;
  trims gaming consumer content/Xbox consumer apps (when not ecosystem-
  required)/Dev Home/developer-only optional features/consumer suggestions.
- **Lightweight** — small, clean, general-purpose Windows that is FINALLY
  meaningfully different but still safe: more consumer AppX cleanup, optional
  media/cloud/printing/remote/legacy features, widgets/news/search web
  integration, consumer personalization — but NO arbitrary CBS deletion, no
  driver stripping, no servicing/security/runtime removal, no shell/login
  removal, no destructive action merely because a component is known.

## 4. Extras must change the plan

Xbox/Game Pass → retain Gaming Services/Xbox ecosystem; WSL/Docker → retain
WSL + VirtualMachinePlatform + Hyper-V dependencies; Print/Scan → retain
print/scanner components; Touch/Pen → retain input infrastructure; Remote
Desktop → retain the RDP stack. If a profile's plan does NOT materially change
when an extra is toggled, that is a bug (regression-tested).

## 5. Profile delta report (ProfileDeltaReport)

For each primary profile over the same real image, deterministic:
AutoApply / Recommended / Optional / Kept / Blocked / NotApplicable counts,
plus an operation-type breakdown (AppX / Capability / OptionalFeature /
Service / RegistryPolicy / Privacy / Personalization / …). This is the PROOF
that profiles differ by semantic operations, not by display strings.

## 6. Plan validator (ProfilePlanValidator)

Generated plans are validated before they may execute: remove+keep conflicts on
the same logical id, duplicate change plans, dependency-required removals,
unsupported execution, protected attempts, and the Phase 12 operation-level
duplicate/conflict detection (canonical registry-target identity) are all
detected. Profile generation FAILS SAFE — any issue keeps the plan from
becoming executable.

## 7. Why meaningful differences matter more than operation counts

A profile with fewer meaningful operations is better than one filled with
speculative tweaks, unsafe deletions, redundant registry changes or placebo
performance settings. No vanity counts: every profile difference is
explainable. Unknown stays visible; known != removable.
