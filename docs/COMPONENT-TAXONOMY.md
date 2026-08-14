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

## Stage 14.2 update (2026-08-13)

- **CBS family rules** (22): conservative floors Risk ≥ Moderate, Protection ≥ Sensitive, never
  RecommendedRemove; explicit optional families (e.g. IE-Optional) are Moderate/Sensitive.
- **Hardware/driver families** (15): Bluetooth, Wi-Fi, Ethernet, USB, Storage, Graphics, Camera,
  Audio, SmartCard, Biometrics, Touch/Pen, Sensors, MobileBroadband, generic driver, client drivers —
  RecommendedKeep/ProfileDependent, High/Critical; classification only, no removal.
- **Unknown debt analysis**: `UnknownFamilyAnalyzer.Cluster` ranks remaining Unknown families.
- **Coverage metrics**: `ClassificationCoverageMetrics` (Curated / KnownDeep / Protected / Heuristic /
  Unknown, per source, no double counting) + restrained UI `CoverageSummaryText`.
- **Language/architecture/resource/version variants** resolve to the same family (normalizer +
  tests); cross-entry pattern/alias collisions are eliminated and guarded.
- Real-media scan limitation: non-elevated sandbox (DISM 740) — baseline from Phase 11 elevated
  scan; see docs/COMPONENT-COVERAGE.md.

## Stage 14.3 update (2026-08-13)

- **Gaming Profile 2.0 policy layer** (ADR-088/089/090): `GamingPcPolicy` + `DedicatedGamingPolicy`
  consume the taxonomy fields (Function / Risk / RecommendationKind / Protection / ProfileTag /
  DependencyTags) plus the selected extras and produce candidate verdicts
  (KeepForCompatibility / AutoRemoveCandidate / OptionalRemoveCandidate / NoOpinion) with
  deterministic reason keys. `ProfileSafetyGate` has FINAL authority: Protected/Critical/High block,
  Moderate is optional-only, Low + curated knowledge may auto-recommend, heuristic never auto,
  unsupported and user-overridden items are never acted on. Verdicts flow into the plan only as
  post-gate decisions (`RecommendationInput.GamingDecision`, engine tier after requirement/
  dependency and extra-scenario overrides, before the default).
- Two distinct profiles, never aliases: **Gaming PC** (Gaming primary; Low-risk consumer cleanup +
  §8 keep list + optional-only "never assume" set) and **Dedicated Gaming** (new primary; same keep
  list, wider OPTIONAL set — moderate consumer/media families become user-confirmed suggestions).
  No placebo tweaks: HPET/BCD/tick/memory/pagefile/cargo-cult registry tweaks, Defender or Windows
  Update disabling, servicing-stack removal are all forbidden.
- Every recommendation reason is a localization resource key (en + zh-CN), never runtime AI prose.

## Stage 14.3b update (2026-08-14, ADR-091)

- **Six real Language capability families** (Basic/Handwriting/TextToSpeech/OCR/Fonts/Speech —
  337 objects on the real 25H2 media): one semantic family per ROLE, never per locale; each
  inventory object retains its exact locale identity. Function=Language, Moderate,
  ProfileDependent, Sensitive. `LanguageCapabilityMetadata` parses family/locale and recognizes
  the image default language (metadata only — no destructive language stripping; "not zh-CN" is
  never inferred as safe automatic removal).
- **Family analyzer granularity**: dotted `microsoft.windows.*` capability families keep up to five
  semantic segments (trailing generic role words like "wlansvc"/"client" dropped) — Console.Legacy,
  Ethernet.Client.Intel, Ethernet.Client.Realtek, Wifi.Client.* are distinct.
- **Package_for_* CBS semantics**: `ComponentNormalizer` preserves the semantic middle
  (DotNetRollup→dotnetrollup, KBxxxx→kb, RollupFix→rollupfix); classified Critical/Protected/
  RequiredKeep (servicing/runtime servicing) — never removable.
- **High-confidence real CBS + small features** classified conservatively (see COMPONENT-COVERAGE);
  KNOWN ≠ REMOVABLE; embedded lockdown/filter/UWF features are Enterprise/High/RecommendedKeep and
  never automatic Gaming removals. Gaming policies keep ALL language capabilities.
