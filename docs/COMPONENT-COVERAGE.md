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


## Stage 14.3 — exact accounting + elevated capture workflow (2026-08-13)

- **Exactness replaces estimation.** `CoverageAccountingService` (Core) computes exact per-source
  coverage from production data: every raw object lands in EXACTLY ONE bucket
  (Curated | KnownDeep | Heuristic | Unknown); Protected is a property count (subset of known,
  matcher-protected reported separately); per-source slices reconcile to the total; heuristic
  classification never inflates knowledge coverage (`CoverageRatio` = (Curated+KnownDeep)/Total;
  `TotalClassifiedRatio` adds heuristic). Buckets are exported per object so no number is opaque.
- **Elevated capture CLI** `tools/WinForge.RealCapture` (must run as Administrator): runs the EXACT
  production pipeline (inspect → export selected index → mount → production DISM discovery →
  matcher → DeepComponentClassifier → coverage accounting → UnknownFamilyAnalyzer top-30) and
  writes `inventory-summary.json`, `inventory-items.json`, `unknown-items.json`,
  `unknown-families.json`, `coverage-by-source.json`, `gaming-candidates.json` +
  `real-derived-families.json` to `.tmp/phase14-real/`, then unmounts/discards and cleans up.
  Source ISO read-only. Output only under `.tmp/phase14-real/`.
- **Exact real numbers are captured ONLY by that elevated run** — no estimate is published as a
  real number. Until then the Stage 14.2 family-level estimate (~≥60% known) remains labeled an
  estimate, and Stage 14.3 stays `IMPLEMENTATION READY — REAL-DESKTOP ELEVATED VALIDATION REQUIRED`.
- Real-derived regression fixture `tests/fixtures/25H2-Pro-zhCN-component-families.json`
  (version/arch/language/host-path-stripped representatives) is refreshable from the CLI output.

## Stage 14.3 — FIRST ELEVATED RealCapture run: EXACT REAL NUMBERS (2026-08-14)

The elevated capture ran successfully on the real desktop as Administrator (source ISO
`Win11_25H2_Chinese_Simplified_x64_v2.iso`, Windows 11 Pro, index 4, x64, build 26200, zh-CN).
These are exact production numbers, not estimates:

| Source | Total | Curated | Protected | Known | Heuristic | Unknown |
| --- | --- | --- | --- | --- | --- | --- |
| AppX | 47 | 22 | 3 | 10 | 0 | 15 |
| Capability | 425 | 2 | 3 | 8 | 0 | **415** |
| CbsPackage | 149 | 0 | 27 | 106 | 0 | 43 |
| OptionalFeature | 136 | 8 | 4 | 77 | 0 | 51 |
| **Total** | **757** | **32** | **37** | **201** | **0** | **524** |

Knowledge coverage: **30.78%** (Curated+KnownDeep)/Total. Protected = property count (subset of
known; matcher-protected reported separately in the JSON). No double counting.

**Accounting boundary (do not mis-compare):** the production discovery supports exactly
AppX / Capability / OptionalFeature / CbsPackage. Service, ScheduledTask, Driver, Language,
WinRecovery and SystemApp are explicitly **NotSupported** providers. The Stage 14.3b total of 757
is therefore NOT mechanically comparable to the historical Phase 11 count of 758 (a different,
partly provider-mapped accounting). Documented in ADR-091.

## Stage 14.3b — real Unknown debt reduction (implementation ready; second capture pending)

- Six Language capability families classified: Basic (123) / Handwriting (89) / TextToSpeech (49) /
  OCR (35) / Fonts (24) / Speech (17) — **337 objects** now carry family knowledge
  (Function=Language, Moderate, ProfileDependent, Sensitive; one family per role, locale identity
  preserved on each object). No heuristic entries added.
- Family analyzer refined: `microsoft.windows.*` capabilities split into semantic subfamilies
  (Console.Legacy / Ethernet.Client.Intel / Ethernet.Client.Realtek / Wifi.Client.* …);
  `Package_for_*` CBS identities keep their semantic middle (dotnetrollup / kb / rollupfix).
- High-confidence real CBS (Licenses/Kernel/FodMetadataServicing/OneCore-DirectX/SenseClient/Hello/
  VBSCRIPT/OpenSSH/Notepad/Wallpaper) and small features (Braille/WirelessDisplay/AzureArc/
  AppServerClient/ProjFS/embedded lockdown·filter·UWF) classified conservatively; KNOWN ≠ REMOVABLE.
- **The second elevated run** (same command) must now be executed to capture the exact new metrics
  (Unknown expected to fall materially from 524).

## Stage 14.3b — VALIDATED: SECOND elevated RealCapture EXACT NUMBERS (2026-08-14)

The second elevated run (same real desktop, Administrator, same ISO/index) validated Stage 14.3b.
Exact production numbers:

| Source | Total | Curated | Protected | Known | Heuristic | Unknown |
| --- | --- | --- | --- | --- | --- | --- |
| AppX | 47 | 22 | 3 | 10 | 0 | 15 |
| Capability | 425 | 2 | 3 | 348 | 0 | 75 |
| CbsPackage | 149 | 0 | 41 | 148 | 0 | 1 |
| OptionalFeature | 136 | 8 | 4 | 85 | 0 | 43 |
| **Total** | **757** | **32** | **51** | **591** | **0** | **134** |

Knowledge coverage: **82.30%** ((Curated + KnownDeep)/Total). Unknown fell from 524 → **134**
(the 337 Language objects + high-confidence CBS/feature families from 14.3b). Same accounting
boundary (four providers; Service/Driver/etc. NotSupported — ADR-091).

## Stage 14.3c — FINAL high-confidence long-tail classification (implementation ready; THIRD capture pending)

- Wi-Fi / Ethernet driver capability families (vendor-family rules, not per-model): Networking/High/
  RecommendedKeep/Sensitive — never auto-removed by Gaming.
- Critical system items: DirectX.Configuration.Database, SecHealthUI, FodMetadata-Package,
  Onecore.StorageManagement, Hello.Face (see ADR-092 for exact semantics).
- 7 media codec AppX classified (Media/Low/ProfileDependent) — Gaming PC never auto-strips codecs.
- Outlook/Office Hub (supported-removal-gated Gaming recommendations), Dev Home (Developer keep +
  curated 22→23, Gaming optional), ApplicationCompatibilityEnhancements (AppX + CBS, High/Keep).
- Clear capabilities + high-confidence features (Console.Legacy/WebDriver/MathRecognizer/Wallpapers.
  Extended/NFS/DCB/ADAM/HostGuardian/LegacyComponents) — conservative, never Low-risk auto.
- Deep catalog 177→203 (+27, zero heuristic); no broad namespace fallback rules.
- **The THIRD elevated run** (same command) must now produce the exact new metrics (Unknown expected
  to fall materially from 134 — no percentage asserted).

## Phase 14 — FINAL: THIRD elevated RealCapture AUTHORITATIVE NUMBERS (2026-08-14)

Phase 14 is ACCEPTED at these exact numbers (real desktop, Administrator, same ISO/index):

| Source | Total | Curated | Protected | Known | Heuristic | Unknown |
| --- | --- | --- | --- | --- | --- | --- |
| AppX | 47 | 23 | 4 | 21 | 0 | 3 |
| Capability | 425 | 2 | 3 | 385 | 0 | 38 |
| CbsPackage | 149 | 0 | 42 | **149** | 0 | **0** |
| OptionalFeature | 136 | 8 | 4 | 90 | 0 | 38 |
| **Total** | **757** | **33** | **53** | **645** | **0** | **79** |

Knowledge coverage: **89.56%** across the CURRENTLY SUPPORTED discovery providers
(AppX / Capability / OptionalFeature / CbsPackage). Service / ScheduledTask / Driver / Language /
WinRecovery / SystemApp remain NotSupported — this is NOT "89.56% of all Windows components".

**CBS coverage is complete: 149 / 149 known.**

Real validation history (family-based architecture on real 25H2 media):
30.78% (KnownDeep 201, Unknown 524) → 82.30% (591, 134) → **89.56% (645, 79)**. The ≥60% Stage 14.2
estimate was superseded by real captures and is no longer presented as a validated metric.

The remaining **79 Unknown are ACCEPTED explicit technical debt** (ADR-093) — CBS complete, AppX
long-tail near-complete (3), Capability/OptionalFeature remainder is low-frequency long-tail (mostly
singletons: Quick Assist/CrossDevice, MSIX tooling, MSMQ, MultiPoint, NFS admin, legacy IrDA/RIP,
RSAT subfeatures, printing subfeatures, Recall, misc enterprise/legacy). Zero Unknown is NOT a
product requirement; no broad catch-all classifier was added.
