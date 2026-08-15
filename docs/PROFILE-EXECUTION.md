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

## Stage 15.2 — Unified candidate stream + real-media plan accounting (ADR-095)

### Why the first real capture diverged from the fixture

The Stage 15.2 real `profile-plans.json` (757 objects) showed fixture-level
differentiation did NOT hold on real media. Root causes (all fixed):

1. **757 → 674 accounting gap** — RealCapture fed ONLY deep-classified inventory
   subjects. The missing 83 = 79 Unknown (no deep knowledge) + 4 curated-but-
   not-deep AppX. They were silently dropped with no bucket. FIX: exact
   `ProfileInventoryAccounting` — every object lands in exactly one bucket
   (EvaluatedForProfile / CuratedOutsideDeepInventory / ExcludedUnknown /
   ExcludedUnsupportedSource / ExcludedFilteredDuplicate / ExcludedNotApplicable /
   ExcludedOther); invariant `Total = evaluated + exclusions` is asserted.
   Authoritative 757: evaluated 678 (645 deep + 33 curated), unknown 79 → balanced.
2. **byOperationType counted inventory, not operations** — every profile showed
   AppX 40 / Capability 387 / CbsPackage 149 / OptionalFeature 98 (= 674). FIX:
   `ByOperationType`/`PlanChangesByOperationType` now counts EXECUTABLE changes
   (AutoApply + Recommend) only; inventory source counts live in
   `ProfileInventoryAccounting.BySource` (InventoryBySource).
3. **Non-inventory optimization layer missing** — Office trims
   (AdvertisingId/TailoredExperiences/…), Balanced privacy/UI trims, Developer
   telemetry/explorer trims target registry/privacy/personalization/service
   definitions. RealCapture had none of them → Office changeCount = 0, Balanced 3,
   Developer 6. FIX: `ProfileCandidateService.BuildCandidates` builds ONE stream
   of inventory subjects + optimization-definition subjects, deduplicated by
   canonical Phase 12-style operation identity. Office now produces a meaningful
   conservative delta (>0, privacy + consumer trims); Balanced gets a real
   baseline; Developer includes registry/privacy actions.
4. **Gaming == DedicatedGaming** — the only policy divergence (Moderate media
   optional) was invisible because default-optional items show as Optional in
   both, and the deep-only stream never hit either profile's overrides. FIX:
   DedicatedGaming policy gains `WiderMinimalSteer` — Low cloud integration
   (OneDrive) → automatic (Low, curated, supported); Moderate
   productivity/communication → RECOMMEND (never automatic); Moderate media →
   optional. The DedicatedGaming catalog now carries the SAME trims as Gaming PC
   (its 7-keep-only list made Dedicated LESS aggressive once the non-inventory
   layer arrived). Real-like stream result: Gaming changes=28, DedicatedGaming=30
   with exactly TWO policy-driven extra changes (AppX|OneDrive|AutoApply,
   AppX|Teams|Recommend) — real semantics, no fake differences.

### changeCount semantics (one definition everywhere)

`changeCount = AutoApply + Recommended`, and both dispositions are by-construction
executable (the matrix blocks unsupported changes and unsupported "optional"
suggestions). Optional / Kept / Blocked never count as changes. The same report
drives fixture tests, RealCapture `profile-plans.json`, the UI preview and the
Review plan — one source of truth.

### RealCapture profile-plans.json v2

Per profile:

```json
{
  "profileId": "Gaming",
  "inventoryAccounting": { "totalInventory": 757, "evaluatedForProfile": 645,
    "curatedOutsideDeepInventory": 33, "excludedUnknownKnowledge": 79,
    "excludedUnsupportedSource": 0, "excludedFilteredDuplicate": 0,
    "excludedNotApplicable": 0, "excludedOther": 0, "bySource": {...} },
  "decisionCounts": { "autoApply": 22, "recommended": 6, "optional": 42,
    "kept": 35, "blocked": 29, "notApplicable": 0 },
  "planChanges": { "total": 28, "byOperationType": { "AppX": 13, "Privacy": 7, ... } },
  "semanticActionKeys": ["AppX|OneDrive|AutoApply", ...],
  "keptHighlights": [...], "blockedHighlights": [...]
}
```

`semanticActionKeys` lets you verify MEANING (which real actions differ between
profiles), not just counts. Exact real-media numbers require the elevated capture
command; the fixture numbers above are plan-validation only.

## Stage 15.2b — Real Dedicated-Gaming differentiation fix (ADR-095 addendum)

### What the first v2 real validation left open

Accounting (757 = 678 evaluated + 79 unknown), byOperationType and Office were
accepted. But on REAL media **Gaming PC == Dedicated Gaming** (auto 19 / rec 6 /
opt 72 / kept 565 / blk 62 / changes 25, identical semanticActionKeys). Two
wiring defects:

1. **Curated-only subjects bypassed the gaming policy.** The planner only
   dispatched the policy when `subject.DeepKnowledge != null`. The real image's
   CuratedOutsideDeep objects (OneDrive-class consumer/cloud AppX) have no deep
   entry, so the DedicatedGaming `WiderMinimalSteer` never ran on them → both
   profiles fell to defaults → identical plans.
2. **Extra-scenario profiles were never selected.** The planner hard-coded
   `SelectedProfiles = [primary]`, so the extras' data-driven Keep overrides
   (XboxGipSvc/XblAuthManager/XboxNetApiSvc/…) were dead — Lightweight could
   auto-disable Xbox services even with the Xbox/Game Pass extra enabled.

### Fixes

- **Policy dispatch for curated subjects**: the gaming policy now runs for
  curated-only subjects via a synthesized knowledge view (curated
  Recommendation/Risk → policy semantics; function/tag Unknown → NoOpinion
  unless curated keep semantics applies). Curated items no longer bypass policy.
- **DedicatedGaming curated intent** (profile data): OneDrive + OneDriveSync →
  Trim (Medium → **Recommend**, never auto), Clipchamp → Trim (Low curated
  supported → AutoApply). Dev Home's difference is POLICY-layer (Moderate +
  DeveloperTool → Recommend) so a curated Low Dev Home is never auto-removed.
  Gaming PC keeps these optional/kept — **convenience preserved, unchanged**.
- **Extras wiring**: `GenerateDelta`/`BuildPlan` join the matching ExtraScenario
  profiles into SelectedProfiles. Extras now override profile minimalism for ANY
  primary: Lightweight + Xbox/Game Pass ON → XblAuthManager/XboxGipSvc/
  XboxNetApiSvc upgraded to Keep (removed from the executable plan); without the
  extra they remain AutoApply `ConfigureOfflineService` startup-type changes
  (restorable — NON-destructive, documented; no behavior change).

### Real-like result (plan validation only)

| Profile | auto | rec | opt | kept | blk | changes |
| --- | --- | --- | --- | --- | --- | --- |
| Gaming PC | 22 | 6 | 42 | 35 | 29 | 28 |
| Dedicated Gaming | 23 | 10 | 37 | 35 | 29 | 33 |

Dedicated-only semantic actions: `AppX|OneDrive|AutoApply`,
`AppX|Teams|Recommend`, `AppX|DevHome|Recommend`, `RegistryPolicy|OneDriveSync|Recommend`
— real, safe, explainable product differences (no fake counts; no Gaming PC
changes). Final real-media numbers come from the elevated capture retest.

## Stage 15.3 — Validated Profile BuildPlan as single Apply source (ADR-096)

### The real-stream blocker and its root cause

Stage 15.2 accepted the plan summaries, but `BuildPlan` could FAIL SAFE (return null) on the real
stream. The validator correctly rejected operations built WITHOUT their execution payload:

- **Service ops** had no `ServiceName`/`ServiceStartType` → all collapsed to `ConflictKey "svc|"` →
  duplicate operations.
- **Registry ops** had no hive/path/value → `MissingTarget`.
- **Component ops** had the logical id instead of the real package identity.

The OptimizationCatalog data was ALREADY clean (verified): every service def has a canonical
ServiceName on the ADR-030 allowlist; ActivityHistory targets the valid offline policy key
`SOFTWARE\Policies\Microsoft\Windows\System\EnableActivityHistory` DWORD 0. The defect was the plan
construction mapping, not the data.

### Fixes

- **BuildPlan complete payload mapping** — reuses the live-app conventions
  (`svc:|opt:|feat:|appx:|cap:|pkg:`); curated rows keep their discovered identity; every op
  records SourceDefinitionIds provenance.
- **OptimizationDefinitionValidator** (Core, reusable) — MissingTechnicalTarget /
  MissingRegistryTarget / MissingServiceName / MissingFeatureName / UnsupportedExecution /
  InvalidValue / DuplicateCanonicalIdentity; run in catalog tests, inside BuildPlan (fail safe),
  and in PlanCapture. Duplicate detection scoped to non-mergeable identities (registry duplicates
  like SpotlightFeatures/DisableSpotlight are legal and merge in the plan — Phase 12).
- **All six primaries now produce non-null validated BuildPlans** (real-derived stream):

| Profile | delta | planOps | selected(auto) | validated |
| --- | --- | --- | --- | --- |
| Balanced | 16 | 16 | 9 | ✓ |
| Gaming PC | 24 | 24 | 17 | ✓ |
| Dedicated Gaming | 27 | 27 | 18 | ✓ |
| Developer | 20 | 20 | 17 | ✓ |
| Office | 17 | 17 | 9 | ✓ |
| Lightweight | 27 | 27 | 23 | ✓ |

*planOps == deltaCount (every difference from the delta is explainable: Recommend ops
present-unselected, canonical dedup merges, manual overrides excluded — never mysterious loss).

- **Profile → Customize → Review → Apply**: one shared `CustomizationPlan` (PlanSync) is the single
  authoritative state. `IsAdoptEligible` now requires `WasProfileDriven`, aligning the preview's
  "Automatic changes" count with the Review's selected count; curated defaults stay Recommended.
  Extras affect the ACTUAL executable plan (Lightweight + Xbox keeps the Xbox services; WSL/Print/
  Remote keep their ecosystems). Apply reuses the existing Phase 12 executor and failure UX.
- **PlanCapture** (`profile-buildplans.json`): structural validation per profile — deltaCount,
  buildPlanOperationCount, selectedOperationCount, validationPassed, validationErrors,
  operationsByType, canonicalOperationKeys. Nothing is applied or built.

## Stage 15.3b — Optional Feature canonical aggregation (ADR-096 addendum)

### Real structural validation exposed false "duplicate change plans"

The first REAL BuildPlan validation (Win11 25H2 Pro zh-CN x64, Administrator RealCapture) passed
four primaries (Balanced 16/16, Gaming 25/25, Developer 21/21, Office 17/17) and FAILED the
virtualization/media-heavy profiles:

| Profile | delta | validation error |
| --- | --- | --- |
| DedicatedGaming | 33 | Duplicate change plan for 'Containers' (4 change entries). |
| Lightweight | 38 | Duplicate change plan for 'HyperV' (9 change entries). |
| DedicatedMinimal | 44 | MediaPlayer x2, HyperV x9 |

These were NOT true duplicates. Root cause (verified against the real 757-object capture — **zero**
raw-identity duplicates): the deep catalog maps MULTIPLE genuinely distinct Windows OptionalFeature
names to ONE profile-facing family id:

- `Containers` → `Containers`, `Containers-HNS`, `Containers-SDN`, `Containers-Server-For-Application-Guard`
  (4 real DISM features)
- `HyperV` → `HyperV-Guest-KernelInt`, `HyperV-KernelInt-VirtualDevice`, `Microsoft-Hyper-V`,
  `Microsoft-Hyper-V-All`, `Microsoft-Hyper-V-Hypervisor`, `Microsoft-Hyper-V-Management-Clients`,
  `Microsoft-Hyper-V-Management-PowerShell`, `Microsoft-Hyper-V-Services`, `Microsoft-Hyper-V-Tools-All`
  (9 real DISM features)
- `MediaPlayer` → `Microsoft.ZuneMusic` AppX + `WindowsMediaPlayer` OptionalFeature
  (Capability + 7 CBS packages are NotSupported → blocked, never planned)

The PlanValidator grouped change entries by the SEMANTIC family id (`LogicalId`) and rejected
distinct real features as duplicates.

### Semantic candidate identity vs executable DISM feature identity

Two identities now coexist on every item:

- **Semantic identity** (`LogicalId` — the canonical family id): drives profile intent matching,
  keep overrides, gaming policy, extras, the delta report keys and the preview. A Trim/Keep intent
  for family "HyperV" applies to all 9 members.
- **Executable identity** (`ProfileExecutionItem.ExecutableIdentity`): the ACTUAL name sent to DISM
  (raw FeatureName / package identity / service name). The final plan's canonical key
  (`feat:|pkg:|svc:|…`) is built from THIS, so distinct real features sharing a family stay
  distinct executable operations.

Executable identity rule:
- Component subjects → the raw inventory identity (the DISM FeatureName / package identity).
- ServiceStartup definitions → `ServiceName`; WindowsComponents definitions → `TargetIdentifier`.
- Registry/Privacy/Personalization definitions → the definition id (each definition is one semantic
  candidate; overlapping registry mutations still merge at plan level via the existing Phase 12
  behavior — unchanged).

### Aggregation boundary (`ProfilePlanAggregator`, runs BEFORE final validation)

`BuildPlan` now aggregates the delta report's items by executable canonical key
(`OperationType|ExecutableIdentity`) BEFORE constructing operations and BEFORE `ProfilePlanValidator`
runs. N semantic candidates resolving to the SAME executable operation collapse into ONE operation
(the validator is NOT weakened — true duplicates must be resolved before it sees the plan, and it
still rejects any that are not).

- Distinct real features stay distinct: HyperV x9 → 9 operations; Containers x4 → 4 operations;
  MediaPlayer → `appx|…ZuneMusic` + `feat|WindowsMediaPlayer`. The virtualization ecosystem is NOT
  collapsed into one feature.
- The validator's duplicate-change check now groups by the EXECUTABLE key; its remove/keep conflict
  check stays semantic (a family-level Keep protects the whole family).

### Conflict precedence (documented, deterministic)

1. **Keep wins over removal** at the semantic level: if any item for a `LogicalId` is kept, every
   change candidate for that `LogicalId` is dropped (`DroppedKeepWins` — RequiredKeep / Protected /
   explicit user override / profile keep all take precedence over removal, mirroring the Safety Gate).
2. **AutoApply > Recommend**: within one executable target, an automatic intent is the deterministic
   superset of a user-confirmed suggestion.
3. **Conflicting requested executable states** (Remove vs Disable vs Configure) for the same target
   are NEVER silently merged — an explicit "Conflicting executable intents" issue fails validation.

### Provenance preservation

When N candidates merge, the operation keeps `SourceDefinitionIds` = the ordered, distinct union of
every absorbed candidate's source identity (raw feature/package identity for inventory objects,
definition id for optimization definitions) — the same behavior as the existing registry merge.
`MergeGroups` additionally records `CanonicalKey`, `SourceCount` and the semantic source keys
(`OpType|LogicalId|Disposition`) for full traceability.

### Count reconciliation

- `deltaCount` = SEMANTIC change entries (AutoApply + Recommend in the delta report) — unchanged.
- `buildPlanOperationCount` = EXECUTABLE operations after canonical aggregation.
- `mergedDuplicateCount` = semantic candidates absorbed into merges (0 on real media — no true
  same-executable duplicates exist in the 25H2 capture).
- `mergeGroups` = per-group diagnostics. Every difference between the two counts is EXPLICITLY
  accounted for — no mysterious loss. Post-fix real structural results (offline re-validation over
  the captured inventory, plan validation only):

| Profile | delta | planOps | validated |
| --- | --- | --- | --- |
| Balanced | 16 | 16 | ✓ |
| Gaming PC | 25 | 25 | ✓ |
| Dedicated Gaming | 33 | 33 | ✓ |
| Developer | 21 | 21 | ✓ |
| Office | 17 | 17 | ✓ |
| Lightweight | 38 | 38 | ✓ |
| DedicatedMinimal | 44 | 44 | ✓ |

`profile-buildplans.json` now also reports `semanticChangeCount`, `mergedDuplicateCount`,
`mergeGroupCount`, `droppedKeepWins` and per-profile `mergeGroups` (diagnostic only).

*Stage 15.3 ACCEPTED (2026-08-15): the final Administrator RealCapture retest reproduced this
structural result on the mounted ISO — all six primaries validationPassed == true.*

## Stage 15.4 — Real Offline Apply Validation (ADR-097)

### Structural validation ≠ execution validation

Stage 15.3 proved every primary profile's BuildPlan validates STRUCTURALLY on real media (non-null,
conflict-free canonical keys). That is not proof the plan EXECUTES: nothing had run a
profile-generated plan against a real mounted image, and the executor's AppX/feature branches treat
a DISM exit code as success. Stage 15.4 validates EXECUTION with independent read-back.

### Validation profiles

Two representative primaries first (one per CLI invocation — inspect the report before the next):

- **Balanced** — covers AppX removal, offline registry, offline service configuration (16 BuildPlan
  ops, 10 selected structurally).
- **DedicatedGaming** — adds OptionalFeature disable (33 BuildPlan ops, 20 selected structurally).

Lightweight is deliberately NOT the first destructive validation (aggressive virtualization
removals).

### Workflow (discard-only, isolated)

`WinForge.RealCapture --apply-profile <ProfileId>`:

1. inspect ISO (read-only input) → 2. export the selected WIM index into a workspace-owned working
   WIM → 3. mount → 4. generate the final validated BuildPlan (same unified candidate stream as
   PlanCapture) → 5. execute ONLY `SelectedOperations` (AutoApply) → 6. independent read-back
   verification → 7. `profile-apply-validation.json` → 8. DISCARD the mount (authoritative
   `/Get-MountedImageInfo`; an unknown mount is never discarded) → 9. clean the workspace. The
   source ISO/WIM is never modified.

### Selected-only execution

`ProfileApplyValidationService` pre-checks every selected operation (deterministic already-satisfied
skip: target already in the requested state → deselected + `Skipped/AlreadySatisfied`, nothing
applied), then executes ONLY the remaining selected operations through the existing Phase 12
executor. Recommend-only rows (e.g. Containers/WSL in DedicatedGaming) are NEVER executed. The
report carries `buildPlanOperationCount` (candidates) AND `selectedOperationCount`/`attempted`
(executed) so the separation is provable.

### Read-back verification (exit code alone is never success)

- **AppX** (`RemoveProvisionedAppx`): re-query `/Get-ProvisionedAppxPackages` → package absent =
  `Verified`; still present = `VerificationFailed`. Already absent before execution =
  `AlreadySatisfied`.
- **OptionalFeature** (`DisableOptionalFeature`): `/Get-FeatureInfo` → exact returned State
  recorded (`Disabled`, `DisabledWithPayloadRemoved`, …); any non-disabled state = failure.
- **Offline service** (`ConfigureOfflineService`): read the MOUNTED SYSTEM hive `Start` value —
  never the host service state.
- **Offline registry** (`SetOfflineRegistryValue`): read the mounted hive and confirm hive + path +
  value name + type + data. `OfflineDefaultUser` maps to `Users\Default\NTUSER.DAT` inside the
  mount — host HKCU is never touched.

### Failure handling & cleanup

Per-operation failures are recorded exactly (`executionStatus` + `verificationDetail`); the run
continues only where safe; the profile is NOT reported successful when any operation failed or
failed verification. Cleanup always runs after a validation attempt; a failed mount cleanup is a
BLOCKER that stops further profile validation. `mountCleanup.{discardSucceeded,
workspaceCleanupSucceeded}` is reported per run.

### Report schema (`profile-apply-validation.json`)

```
profileId, buildPlanOperationCount, selectedOperationCount, attempted, succeeded, failed,
skipped, validationPassed
operations[]: canonicalKey, operationType, expectedAction, executionStatus,
              verificationStatus, verificationDetail
mountCleanup: discardSucceeded, workspaceCleanupSucceeded
```
