# Phase 14 — Input Notes: Gaming Profile Direction (2026-08-13)

> **Source:** real user feedback on the current 「游戏优先 / Gaming-first」 profile.
> Recorded during Phase 13 closeout prep — Phase 14 should implement, not this phase.

## Problem

The current Gaming profile is too conservative: the observed Apps list includes many
non-gaming items that are NOT auto-selected for cleanup:

- Phone Link
- Bing / Web search integration
- Paint
- Solitaire
- Get Help
- Weather
- Feedback Hub
- (and similar consumer content)

## Product direction

The Gaming profile must remain **safe for personal gaming PCs**, but it should perform
**meaningful cleanup**. It must NOT become equivalent to a cybercafe / kiosk image.

## Future Phase 14 distinction

| Mode | Intent |
| --- | --- |
| **Gaming PC** (default Gaming profile) | Safe, meaningful cleanup on a personal gaming machine — keeps utility + store + game dependencies + update/Defender. |
| **Dedicated Gaming / Cybercafe-like minimal** (future option) | Minimal system — explicit, advanced, NOT part of the safe automatic Gaming profile. |

## Keep (never auto-remove in Gaming profile)

- Terminal / PowerShell
- Notepad, Calculator
- App Installer (winget) and its relationship
- Microsoft Store / Gaming Services / Xbox dependencies **when needed**
- WebView2 / VC++ runtimes / DirectX / anti-cheat dependencies
- Windows Update stack (wuauserv, BITS, UsoSvc, WaaSMedicSvc — ADR-078 invariant)
- Defender (unless an explicit advanced mode; never in safe automatic recommendations)

## Candidate safe Gaming cleanup (Phase 14 evaluation)

- Phone Link
- Solitaire (Microsoft Solitaire Collection)
- Get Help
- Consumer content (consumer experiences)
- Tips (Get Started)
- Spotlight / lock-screen content
- Feedback Hub
- Weather
- Advertising / tailored experiences (registry: `AdvertisingInfo`, `TailoredExperiencesWithDiagnosticData`)
- Bing / Web search integration (where safe and reversible)

## Constraints

- Edition/language/architecture gating must use the Phase 13 compatibility facts
  (edition-gated operations are never shown as universally valid).
- Reversible with documented Restore paths.
- Update infrastructure + Defender + Store/driver dependencies stay protected
  (SafetyInvariantCatalog assertions must keep passing).
- Cybercafe-like minimal mode, if ever built, is a SEPARATE explicit option —
  never part of the safe automatic Gaming recommendation.
