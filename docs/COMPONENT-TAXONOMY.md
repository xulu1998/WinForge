# WinForge Component Taxonomy (Phase 14 — Stage 14.1)

> Durable classification layer for the deep component coverage work. Safety > removal count;
> heuristic classification NEVER silently becomes a removal rule.

## Layer separation (must not collapse)

| Layer | Question | Owner |
| --- | --- | --- |
| **Discovery** | WHAT EXISTS | `ComponentInventoryEntry` / `IRawInventoryItem` (read-only DISM providers) |
| **Knowledge** | WHAT IT MEANS | `DeepComponentKnowledge` + `DeepComponentClassifier` + `DeepComponentCatalogData` |
| **Planning** | WHAT TO DO | `OptimizationDefinition` / plan compiler / profiles |

Discovery never mutates inventory; knowledge never plans removal by itself.

## Top-level functional categories (`ComponentFunctionCategory`)

Communication · Gaming · Media · Productivity · Developer · Networking · RemoteAccess ·
PrintingScanning · Input · Accessibility · Security · Servicing · DiagnosticsTelemetry ·
CloudIntegration · Search · ShellExperience · HardwareSupport · Virtualization · Enterprise ·
LegacyCompatibility · Language · Recovery · StoreInfrastructure · RuntimeDependency · SystemCore ·
Unknown.

(Discovery SOURCE kind stays separate: `ComponentCategory` = AppX / Capability / OptionalFeature /
CbsPackage / Service / …)

## Risk model (`ComponentRiskLevel`)

| Level | Meaning |
| --- | --- |
| Low | normally safe optional consumer functionality |
| Moderate | safe only when the related feature is not needed |
| High | removal can break significant Windows functionality or dependencies |
| Critical | system/security/servicing/runtime infrastructure — protected by default |

"Unused by Gaming profile" ≠ "safe to remove".

## Recommendation model (`ComponentRecommendationKind`)

RecommendedRemove · OptionalRemove · RecommendedKeep · RequiredKeep · ProfileDependent · Unknown.
User-facing wording is localized (`Deep.Rec.*`). Examples: Phone Link → OptionalRemove (Gaming,
when unused); Store/Gaming Services → RequiredKeep when ecosystem enabled; Print/Hyper-V →
ProfileDependent; Defender/servicing → RequiredKeep.

## Confidence / provenance (`ClassificationConfidence`)

Curated (hand-reviewed) · KnownPattern (exact identifier/alias) · KnownFamily (normalized family
pattern) · Heuristic (inferred — NEVER a removal rule by itself; risk floors at Moderate and
protection at Sensitive). A catalog entry explicitly marked Heuristic stays Heuristic regardless of
how it matched.

## Normalization (`ComponentNormalizer`)

Strips `~` tokens (package hashes), `_` tokens (arch/language/neutral/publisher), version numbers,
and `.neutral` — so families classify without hundreds of duplicate entries. Deliberately
conservative: `FindCollision` guards against unrelated packages colliding; tests assert deterministic
canonicalization.

## Safety boundary

Protected groups (never auto-removed): servicing stack, Windows Update infrastructure
(wuauserv/BITS/UsoSvc/WaaSMedicSvc), CBS servicing, WinSxS-critical packages, Defender/security
platform, boot components, WinRE/recovery, App Installer/winget, Store infrastructure, WebView2,
VC/runtime/framework dependencies, DirectX/gaming runtimes, shell/core login infrastructure.
Stage 14.1 implements NO aggressive removal.

## Gaming PC vs Dedicated Gaming

- **Gaming PC** (normal profile): safe-but-meaningful cleanup — Phone Link, Solitaire, Get Help,
  Weather, Feedback Hub, Tips, consumer content, Spotlight, ads/tailored experiences, widgets/news,
  Bing web integration.
- **Dedicated Gaming / cybercafe-like minimal**: a SEPARATE explicit option, never part of safe
  automatic recommendations.
- KEEP by default: Terminal, Notepad, Calculator, App Installer/winget, Store, Gaming Services/Xbox
  dependencies when ecosystem enabled, WebView2, VC runtimes, DirectX, Defender, Windows Update,
  servicing stack. NO placebo tweaks (HPET/BCD/memory/Defender-disable folklore).

## Unknown strategy

Unknown stays **visible as technical debt** — metrics never hide it. Stage 14.1 first batch classifies
108 known families; the remaining unknown set is progressively reviewed in later stages.
