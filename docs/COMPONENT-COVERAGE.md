# WinForge Component Coverage (Phase 14 — Stage 14.2)

## Real media source

- ISO: `C:\Users\xulu1998\Downloads\Win11_25H2_Chinese_Simplified_x64_v2.iso` (8,543,608,832 B;
  `sources\install.wim` = 7,578,937,450 B) — **never modified**.
- Target: Windows 11 25H2 · Build 26200 · zh-CN · x64 · Consumer · install.wim · **Professional (Index 4)**.

## Scan capability (honest)

The build sandbox runs **non-elevated**: `DISM` returns **Error 740** for every operation
(`/Get-WimInfo`, mount, enumeration) — the real per-object inventory CANNOT be re-executed here.
The numeric baseline therefore uses the **real desktop scan recorded in Phase 11**
(`.tmp/phase11/real-25h2-inventory-report.md`, 2026-08-10, elevated real desktop, same image family):

| Class | Real count |
| --- | --- |
| Curated | 11 |
| DiscoveredUnclassified | **734** |
| Protected | 13 |
| Unsupported | 0 |
| Total | 758 |

## Stage 14.2 classification expansion (family semantics)

`DeepComponentCatalogData` grew from 108 → **145 curated family entries** (+37):
- **22 CBS family rules** (Printing, Language, Client-Core, Foundation, Edition, Setup, Shell, WMI,
  Networking, Search, Media, Defender, Servicing, WinRE, Hyper-V, Containers, Remote Desktop, IE,
  TabletPC, Developer, Enterprise) — conservative: Risk ≥ Moderate, Protection ≥ Sensitive,
  never RecommendedRemove (explicitly known optional families like IE-Optional are Moderate).
- **15 hardware/driver family rules** (Bluetooth, Wi-Fi, Ethernet, USB, Storage, Graphics, Camera,
  Audio, SmartCard, Biometrics, Touch/Pen, Sensors, MobileBroadband, generic Driver, Client-Drivers)
  — RecommendedKeep / ProfileDependent, Risk High/Critical. **No removal execution** in this stage.

## Coverage estimate (family-level, NOT a fabricated per-object scan)

The real 734 unclassified objects are dominated by CBS packages (~75–85% on typical 25H2 Consumer
media), AppX (~8–12%), capabilities/optional features (~5–8%), services (~3%). Applying the 145
family rules to the real family distribution conservatively classifies **an estimated ≥60% of the
real inventory as KNOWN** (mostly `KNOWN + Protected` / `KNOWN + Sensitive` — understanding, not
removal). **Exact per-object before/after requires an elevated scan** (runbook: `docs/VM-VALIDATION.md`
mount flow; then `DeepComponentClassifier` + `ClassificationCoverageMetrics` produce exact numbers).

## Unknown debt strategy

Unknown stays **visible** (metrics + Component Intelligence summary). Top remaining Unknown groups on
real 25H2 media are expected to be: versioned non-family CBS packages, language-resource variants,
and rare driver packages — Stage 14.3 expands those families with evidence.

## CBS safety policy

- CBS default: **Risk ≥ High unless explicitly known otherwise** (our family floor is Moderate for
  documented optional families); **Protection ≥ Sensitive**.
- Classified ≠ removable. No mass CBS removal / driver stripping / servicing / Defender / Update /
  WinRE / Store-framework / runtime / shell removal added in this stage.

## Why classified ≠ removable

Classification (knowledge) is separate from planning (removal). A component can be KNOWN + High +
Protected + RequiredKeep. Removal support is a later decision gated by protection, risk,
dependencies and profile — never by classification alone.
