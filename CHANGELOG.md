# Changelog

All notable user-visible changes to WinForge are documented here.
Format based on [Keep a Changelog](https://keepachangelog.com/).

## Phase 14 — Stage 14.2: Real-media family expansion + coverage (2026-08-13)

- Real source confirmed: `C:\Users\xulu1998\Downloads\Win11_25H2_Chinese_Simplified_x64_v2.iso`
  (install.wim 7.58 GB, extracted read-only). The build sandbox is NON-ELEVATED — DISM returns
  Error 740, so a fresh per-object scan is not possible here; the real baseline uses the Phase 11
  elevated real-desktop scan (Curated 11 + DiscoveredUnclassified 734 + Protected 13 = 758).
- Family expansion: `DeepComponentCatalogData` 108 → **145 curated entries** (+22 CBS family rules,
  +15 hardware/driver rules). CBS semantics introduced with conservative floors Risk≥Moderate,
  Protection≥Sensitive, never RecommendedRemove; hardware families → RecommendedKeep/ProfileDependent.
- `UnknownFamilyAnalyzer` (ranked family debt) + enhanced `ClassificationCoverageMetrics`
  (Curated/KnownDeep/Protected/Heuristic/Unknown, per source, no double counting) + restrained
  `CoverageSummaryText` on the Component Intelligence surface.
- Language/architecture/resource/version variants covered; version suffix changes do not alter
  classification; cross-entry pattern/alias collisions eliminated and guarded.
- docs/COMPONENT-COVERAGE.md (honest scan-capability note; ≥60% family-level coverage estimate;
  exact per-object numbers require an elevated scan; CBS safety policy; classified ≠ removable).
  **892 tests pass (Core 53, App 839), 0 errors, 0 warnings** — incl. 47 Stage14 tests.

## Phase 14 — Stage 14.1: Deep Component Coverage & Classification (2026-08-13)

- Durable taxonomy + classification layer (ADR-085): discovery / knowledge / planning kept SEPARATE.
  `ComponentFunctionCategory` (25 top-level functional categories) distinct from discovery source
  kind; `DeepComponentKnowledge` (canonical id, display name, purpose, category, risk, recommendation,
  protection, profile tag, dependencies, confidence); risk model Low/Moderate/High/Critical;
  recommendation RecommendedRemove/OptionalRemove/RecommendedKeep/RequiredKeep/ProfileDependent;
  confidence Curated/KnownPattern/KnownFamily/Heuristic (heuristic NEVER lowers risk below Moderate or
  protection below Sensitive).
- `ComponentNormalizer` (strips ~/_ tokens, versions, .neutral; collision guard) + first-batch
  `DeepComponentCatalogData` with **108 curated family entries** (Gaming/Xbox, consumer inbox, phone,
  search/Bing, widgets/Spotlight/ads, diagnostics/telemetry, printing, virtualization, dev tools,
  remote access, media, cloud, Store/runtime infrastructure, security, servicing/recovery,
  accessibility) — protection flags for servicing/update/CBS/Defender/boot/WinRE/App Installer/Store/
  WebView2/runtimes/DirectX/shell.
- Gaming profile foundation metadata (Gaming PC vs Dedicated Gaming distinction; keep-list; candidate
  safe cleanup; no placebo tweaks) — matches docs/PHASE14-INPUT.md.
- UI: the existing Customize Apps/Windows Components knowledge surface now also ingests CLASSIFIED
  DiscoveredUnclassified rows (name/purpose/recommendation/risk via deep catalog) — raw ids stop
  dominating; unknown stays clearly labeled. Master-detail behavior preserved.
- Coverage metrics model (total/curated/protected/classified/unknown, per-source) with Unknown kept
  visible as debt. **845 tests pass (Core 53, App 792), 0 errors, 0 warnings** — incl. 21 new
  Stage14DeepClassificationTests (taxonomy/normalization/alias/family/risk/rec/confidence/profile/
  protected/unknown/deterministic/collision/fallback/heuristic/Gaming-safety/metrics).

## Phase 13 — CLOSEOUT: VM INSTALL VALIDATED + merged (2026-08-13)

- Phase 13 baseline VM validation ACCEPTED (ADR-084): WinForge-generated ISO (25H2 Pro zh-CN x64
  WIM, SHA-256 21311def…cae98) boots in Hyper-V Gen-2 UEFI, Setup launches, Windows 11 Pro installs,
  first reboot succeeds, OOBE proceeds, desktop reached → Level = **VmInstallValidated**.
- Validation-level semantics introduced (WorkflowValidated / VmInstallValidated / FullHealthValidated):
  `ValidationResult.AllPhasesPassed` now evaluates the phase set required by the declared Level only.
  Phase 13 = VmInstallValidated; deeper post-install health checks (Windows Update / Defender / Store /
  DISM ScanHealth / recovery) are intentionally NOT required now — the safe customization surface
  (AppX / selected services / privacy / personalization / safe registry) does no aggressive CBS /
  driver / servicing-stack removal. FullHealthValidated becomes mandatory in later phases.
- docs/COMPATIBILITY.md: 25H2 Pro zh-CN row → **VM INSTALL VALIDATED** (not FullHealthValidated);
  en-US Pro / Home / Education / Enterprise / 24H2 / ESD / SWM stay pending; ARM64 unsupported.
- Validation record: `validation/25H2-Pro-zh-CN-x64-20260813-0720.{json,md}` — Evidence=RealVmValidation,
  Level=VmInstallValidated, PASS on IsoVerification/VmBoot/Setup/Oobe/Desktop, deeper phases
  "not performed" (never fabricated). **824 tests pass (Core 53, App 771), 0 errors, 0 warnings**.

## Phase 13 — Real VM validation preparation + Phase 14 input (2026-08-13)

- Located the most recent generated ISO: `C:\Users\xulu1998\Documents\WinForge\WinForge_Windows_11_专业版_20260812-1915.iso`
  (8,177,487,872 bytes, SHA-256 `21311def83217ae42a3c867b309957f2d50e1d5d4d231052bc851fa6751cae98`,
  25H2 build 26200 zh-CN x64 WIM, Professional index 4, produced during Phase 12 validation).
- Real VM installation validation CANNOT run from the sandbox → nothing is fabricated. Prepared:
  `docs/VM-VALIDATION.md` (Hyper-V Gen-2 UEFI creation commands + 25-item acceptance checklist +
  report-fill steps; platform-neutral) and the validation report template
  `validation/25H2-Pro-zh-CN-x64-20260813-0720.{json,md}` (Evidence=NotRecorded; not validated until
  all phases PASS with Evidence=RealVmValidation). docs/COMPATIBILITY.md row stays pending.
- Phase 14 input note recorded (`docs/PHASE14-INPUT.md`): real user feedback — the 游戏优先 Gaming
  profile is too conservative (Phone Link / Bing / Paint / Solitaire / Get Help / Weather / Feedback
  Hub etc. not auto-selected). Phase 14 should keep Gaming safe-but-meaningful, distinguish
  Gaming PC vs cybercafe-like minimal, keep Terminal/Notepad/Calc/App Installer/Store/Gaming
  Services/WebView2/runtimes/update stack/Defender, and evaluate safe cleanup of Phone Link,
  Solitaire, Get Help, consumer content, Tips, Spotlight, Feedback Hub, Weather, ads, Bing
  integration. **823 tests pass (Core 53, App 770), 0 errors, 0 warnings**.

## Phase 13 — Preflight final cleanup + edition/language/index display fixes (2026-08-12)

- Diagnostic root cause CONFIRMED on real desktop (category A): runtime Source step renders
  `SourceView` (App.xaml SourceStepTemplate); `ImageView.xaml` is legacy. The compatibility row is now
  in SourceView ONLY — all diagnostic instrumentation removed (PHASE13-COMPAT-DIAG marker, commit
  badge, CompatibilityDebugText UI, unconditional debug row); ImageView carries NO compatibility UI.
- Real-desktop display defects fixed:
  - `Compat.Edition.` leak: the localization service returns the KEY for missing entries, and an
    empty EditionId produced the truncated key. `L()` now falls back instead of leaking raw keys
    (plus `LocalizeEditionName` empty-id guard); anti-leak tests cover every known edition in zh+en.
  - `Lang=?`: profile DefaultLanguage can be null on real DISM; language now resolves
    SelectedEdition.DefaultLanguage → profile.DefaultLanguage → AvailableLanguages[0] → never "?".
  - `Index=none`: the row now shows `Index <n>` only from the CURRENT SelectedEdition
    (Professional → Index 4, Home → Index 1), refreshing instantly on selection — no re-detect.
- Final compact row: 兼容性 `Windows 11 25H2 · 专业版 · x64 · zh-CN · WIM · Index 4 · ✓ 支持`
  (media-level: release · arch · lang · format · status; edition + index appended on selection).
- STA render tests serialized via a shared `WpfRenderLock.Sync` (Stage13 classes + Stage11p2 RunSta)
  — parallel STA threads race WPF static state (Application/InputManager/HwndSource) and caused
  intermittent failures. Full suite **823 pass (Core 53, App 770), 0 errors, 0 warnings** (OutDir,
  run twice; in-place build blocked while WinForge.App.exe is running — user closes it first).

## Phase 13 — Compatibility UI diagnostic instrumentation (2026-08-12)

- Real desktop STILL shows no compatibility status after two fixes → deterministic diagnostics
  added instead of another speculative UI redesign.
- ROOT CAUSE (category A): the wizard renders the Source step via `SourceView` (App.xaml
  `SourceStepTemplate` → `views:SourceView DataContext="{Binding Content}"`) — `ImageView.xaml` is a
  LEGACY view never referenced by any template. All earlier compatibility UI (two rounds) was added
  to ImageView.xaml, so the running executable never showed it.
- Diagnostic commit adds (to BOTH SourceView and ImageView): literal marker
  `PHASE13-COMPAT-DIAG · <commit>` (no binding/visibility), always-bound `CompatibilityDebugText`
  (never empty: `Profile=False` before evaluation, full dump after), and the compatibility row bound
  WITHOUT any Visibility condition. `ImageViewModel.CompatibilityDebugText` + `NotifyCompatibility`
  logging (`PHASE13 COMPAT: ...`) record the full runtime chain per evaluation/notify.
- Compiled BAML verified to contain the marker (new build). Tests: Stage13DiagnosticMarkerTests (6)
  — marker in both views; DebugText never empty; 26200 fixture → Profile=True full dump
  (Status/Release/Build/Format/Arch/Lang/Index); no-Visibility bound row; real SourceView WPF render
  shows marker + debug + compatibility row. Full suite **814 pass (Core 53, App 761), 0 errors,
  0 warnings**. Diagnostic marker to be removed once root cause is confirmed on desktop.

## Phase 13 — Compatibility preflight UX simplified (2026-08-12)

- Product decision: retire the separate standalone compatibility card (still invisible on real
  desktop) — compatibility is a STATUS/SAFETY SIGNAL, not a second technical section. Integrated ONE
  compact row into the existing 「Windows information」 area, gated by HasCompatibilityProfile.
- Row format: 「Windows 11 25H2 专业版 · x64 · WIM · ✓ 支持」. Media-level facts (release · arch ·
  format · status) render immediately after Detect, BEFORE any edition is selected (no dependency on
  SelectedEdition); selecting an edition appends the localized edition name (Home → 家庭版/Core,
  Pro → 专业版/Professional…) and refreshes instantly — no second Detect.
- Status marks stay honest (ADR-074/080): ✓ 支持 for the workflow-validated baseline (never
  「完整验证通过」 while VM validation is pending); ⚠ 尚未完整验证 (future build); ⚠ 仅检查支持
  (ESD/SWM); ✕ 当前不支持 (blockers, red reason below). Dead surface removed: CompatibilityDetailsText
  deleted; one model, one status, one compact rendering. Full suite **808 pass (Core 53, App 755),
  0 errors, 0 warnings**.

## Phase 13 — Compatibility preflight UI real-desktop blocker FIXED (2026-08-12)

- Real desktop: after 检测 on Win11_25H2_Chinese_Simplified_x64_v2.iso (10.0.26200, zh-CN, Pro index 4)
  the legacy inspection data showed but the Phase 13 compatibility card was COMPLETELY absent.
- Root cause (ADR-079): the profile setter used SetField, which only notified "CompatibilityProfile",
  but the View binds to DERIVED properties (CompatibilityStatusText / CompatibilityDetailsText /
  HasCompatibility*) — those were never notified, so the section stayed empty. The earlier notify
  block was wired into the SelectedEdition setter (which runs BEFORE the profile is assigned and
  clears the selection), so it never fired for the freshly evaluated profile.
- Fix: CompatibilityProfile setter + SelectedEdition setter both call NotifyCompatibility() for all
  derived properties; the section is gated by HasCompatibilityProfile (hidden pre-detection) with
  details ALWAYS visible once detected (not behind an advanced page); status wording stays honest —
  "支持" (workflow-validated baseline; VM install validation still pending, never "完整验证通过").
  Edition/index/language details reflect the user-selected edition and refresh immediately on
  selection change (no re-detect): e.g. Home → Core facts, Pro → Professional + Index 4.
- Regression: Stage13PreflightUiTests (10) — real-like 26200 Pro zh-CN fixture → profile + Supported;
  VM receives it after detection; REAL WPF render shows the section with status + details;
  Build 26200 / x64 / zh-CN / Wim / Index 4 render; edition switching refreshes; warning / blocker /
  not-detected states; zh-CN + en-US. Full suite **808 pass (Core 53, App 755), 0 errors, 0 warnings**.

## Phase 13 — Compatibility & Real-World Validation Matrix (2026-08-12, foundation)

- Compatibility model (CompatibilityStatus/Severity/ImageCompatibilityProfile/Findings),
  release classifier (24H2/25H2/unknown-newer/older), edition capability facts (Sandbox/Hyper-V/
  RDP host per edition), language stable-identity policy, WIM/ESD/SWM detection, multi-index
  enumeration + selected-index persistence, conservative media classification, split-WIM
  read-only marking.
- Compatibility preflight surfaced after ISO inspection (concise release · status + blockers);
  blocking vs warning rules; unknown-future-build graceful degradation (SupportedWithWarnings).
- Validation matrix data model + initial targets (Tier A/B/C) + JSON/Markdown report export
  (validation/) with strict Validated-vs-Automated evidence separation.
- Safety invariants (catalog assertions): Update infrastructure, Defender, core drivers, Store
  never disabled/removed by standard recommendations.
- docs/COMPATIBILITY.md + ADR-073..078. **798 tests pass (Core 53, App 745), 0 errors, 0 warnings
  (Release)** — incl. 31 new Stage13CompatibilityTests (fixtures + rules + invariants + report).

## Phase 12 CLOSEOUT — Workspace Lifecycle & Disk Safety (2026-08-12)

**STATUS: PHASE 12 — COMPLETED · REAL DESKTOP VALIDATION — PASSED · MERGED TO `main` (--no-ff).**
All seven stages (12.1 lifecycle manifests + disk safety, 12.2 Finish cleanup + workspace-root settings,
12.3 Review validation UX, 12.4 plan normalization/dedupe, 12.5 Build→Finish state sync, 12.6 Hide Widgets
Dsh-policy fix + apply-result UX, 12.7 one authoritative creation root) are REAL-DESKTOP VALIDATED on
Windows 11 25H2. Original incident resolved: ~30 stale workspaces + temp ≈ 249 GB → deterministic lifecycle
with authoritative DISM mount safety, CurrentRoot-only creation (KnownRoots scan/recover/clean-only), safe
Finish/Discard cleanup, ISO preserved, Storage cleanup UI with owning-root display, no repeated-run leak.
Non-blocking follow-ups recorded (ADR-072). Final suite baseline: 767 tests (Core 53, App 714), 0 errors,
0 warnings (Release).

## [Unreleased]

### Added (Phase 12 — Workspace Lifecycle & Disk Safety — phase/12-workspace-lifecycle)

- **Workspace lifecycle (ADR-062).** Every workspace now has a durable `workspace.json` manifest and an
  explicit lifecycle state (Created → Prepared → Mounted → Committed → BuildCheckpoint → Completed, plus
  FailedDisposable / Cancelled / Cleaned). The servicing service records transitions automatically, and a
  completed build records its final ISO path.
- **Cleanup safety (ADR-063).** The live DISM mount registration is authoritative: actively mounted
  workspaces are never deleted, a mount-query failure fails closed, needs-remount workspaces are surfaced
  for recovery, and recoverable checkpoints are retained. Cleanup handles ReadOnly/System/Hidden files,
  reports reclaimed space, and records exact leftovers on partial failure.
- **Output vs temp (ADR-064).** Final ISOs now default to `Documents\WinForge` and are never treated as
  disposable temp data. Cleanup only ever touches the WinForge workspace root.
- **Storage surface.** Settings → 存储 scans all workspaces (temp / recoverable / active / disposable),
  shows safe-cleanup candidates (including 旧版残留 legacy leftovers), and cleans with one click.
- **Disk guard (ADR-065).** Builds estimate required space (working WIM + media staging + final ISO +
  safety margin) and stop with a clear message before the drive fills up.
- **Incident regression.** A repeated-workflow test proves disposable workspaces no longer accumulate
  across sessions (the ~249 GB stale-workspace incident becomes impossible under normal use).

### Fixed (Phase 12 Stage 12.7 — workspace root not honored: shadow C: workspace leak)

- **Real-desktop:** CurrentRoot=F://WinForgeWorkspaces, but a ~6.9 GB workspace (wf-a9bac38c7259) kept
  reappearing under the OLD C: default root after Finish (F: became empty).
- **Root cause (SPLIT workspace):** `WorkspacePathProvider` was registered with a standalone default
  root (%LOCALAPPDATA%\WinForge\Workspaces) and NEVER consulted
  `IWorkspaceRootSettingsService.CurrentRoot`. Prepare created the SERVICING data under C: while the
  lifecycle MANIFEST went to the configured F: root (lifecycle.WorkspaceRoot uses CurrentRoot) — two
  same-id directories. Finish cleaned by manifest (F: shell) and LEAKED the C: data (no manifest).
- **Fix (ADR-071):** `WorkspacePathProvider` now resolves the CURRENT root at runtime
  (fixed test override → current root → platform default); Bootstrapper wires it to the settings
  service so EVERY new workflow service (Prepare/Apply/Commit/Export/checkpoint/Build) creates under
  CurrentRoot only. KnownRoots remain scan/recover/clean-only — never a creation destination.
  Finish now cleans the one unified workspace (manifest + data together); the final ISO (outside the
  workspace) survives. Storage candidates display their OWNING root (RootPath) so a size is never
  mistaken for the current root.
- **Regression:** Stage12p7WorkspaceRootTests (9) — current root is the only creation root; old
  KnownRoot scanned but never created into; changing root C→F affects new workflows live; recovery
  respects original root; no duplicate/shadow workspace across roots; Storage candidate shows source
  root; repeated workflows do not grow the old root; full incident reproduction
  (KnownRoots=[C,F], CurrentRoot=F → F-only creation, F cleaned, C untouched, ISO preserved).
  Full suite **767 pass (Core 53, App 714), 0 errors, 0 warnings**.

### Fixed (Phase 12 Stage 12.6 — Hide Widgets offline registry UnauthorizedAccessException + apply-result UX)

- **Real-desktop:** applying 「隐藏小组件按钮」(SetOfflineRegistryValue, OfflineDefaultUser) failed with
  "Attempted to perform an unauthorized operation" (UnauthorizedAccessException) while the sibling
  「任务栏搜索仅显示图标」 (TaskbarSearch, same hive, same Explorer\Advanced key) succeeded.
- **Root cause:** the old target Explorer\Advanced\TaskbarDa lives in the PROTECTED Explorer subtree
  of the Default User template — its template ACLs reject offline writes (the sibling only worked
  because TaskbarSearch already exists in the template). Execution: EnsureKeyPath fell into
  CreateSubKey on an existing-but-read-only key and surfaced the bare exception.
- **Fix (Case D — policy-based offline-safe equivalent; ADR-070):** HideTaskbarWidgets now targets
  the official user-policy branch Software\Policies\Microsoft\Dsh → EnableWebContent = 0
  (Windows 11 25H2 supported mechanism). `EnsureKeyPath` now probes read-only keys and raises an
  explicit, contextual "read-only template ACL" failure instead of a bare UnauthorizedAccessException
  (WinForge NEVER takes ownership / rewrites ACLs on offline user keys).
- **Apply-result UX:** localized summary 「应用完成：{0} 项成功，{1} 项失败。」/ "Application
  completed: {0} succeeded, {1} failed." plus a visible failed-operations panel (name + reason,
  expandable). Per-operation outcomes are now written back from the execution SNAPSHOT onto the
  LIVE plan (previously the frozen-snapshot execution left live operations Pending, so the failure
  panel could never populate). Partial apply is never silently treated as full success.
- **Regression:** Stage12p6HideWidgetsTests (10) — model targets Dsh policy; sibling TaskbarSearch
  unchanged; old TaskbarDa target gone; corrected target executes successfully through the engine
  (fake offline registry); no ACL-rewrite code exists; localized summary counts; failed item + reason
  visible; partial-failure state deterministic (CompletedWithErrors unlocks Build with the panel
  shown). Binding audit gains PlanReviewView.ApplyFailed render case. Full suite
  **758 pass (Core 53, App 705), 0 errors, 0 warnings**.

### Fixed (Phase 12 Stage 12.5 — build completed but wizard step stayed InProgress / Finish disabled)

- **Real-desktop:** full build succeeded (构建完成 / 100% / 已完成, final 7.62 GB ISO at
  Documents\WinForge) but the top stepper still showed 构建镜像 · 进行中 and 完成 stayed disabled.
- **Root cause (two defects in WorkflowViewModel):** (1) `OnBuildChanged` refreshed CanFinish on
  `BuildStepViewModel.CurrentStage` changes but never called `RecomputeStates()`, so the Build step
  stayed `Current` (InProgress) forever — the step graph was only recomputed from app-state changes;
  (2) it raised FinishCommand via an `is RelayCommand` type check, but `FinishCommand` is an
  `AsyncRelayCommand` — so `RaiseCanExecuteChanged()` was NEVER invoked and the 完成 button stayed
  disabled even though `CanFinish` (IsFinalStep && CurrentStage == Completed) was true.
- **Fix:** `OnBuildChanged` now calls `RecomputeStates()` on `CurrentStage` changes; the Build step
  branch maps a CURRENT step whose `_build.CurrentStage == Completed` to `Completed`
  (构建镜像 → 已完成) instead of `Current`; both `RecomputeStates` and the refresh path raise
  FinishCommand via the correct `AsyncRelayCommand` type. Finish gating unchanged (NotStarted /
  Failed / Cancelled / Verifying-failed all keep Finish disabled — one coherent source of truth:
  `CanFinish => IsFinalStep && _build.CurrentStage == BuildState.Completed`). Stage 12.2 Finish
  cleanup untouched (authoritative DISM check, ISO preserved, reclaimed bytes reported, Home nav).
- **Regression:** `Stage12p5BuildFinishStateTests` (9) — Build start → step InProgress; success →
  step Completed + Finish enabled immediately + CanExecuteChanged fires; failure / cancellation /
  verification-failure keep Finish disabled; success survives navigate-back-and-forward; Finish
  cleanup still deletes the completed workspace while the final ISO path survives; Finish returns
  Home; zh/en StepState strings. Full suite **748 pass (Core 53, App 695), 0 errors, 0 warnings**.

### Fixed (Phase 12 Stage 12.4 — plan compiler emitted duplicate identical registry operations)

- **Real-desktop report:** after the Stage 12.3 validation-UX fix, the validator correctly surfaced
  "Duplicate operations target the same change: reg|SOFTWARE\Policies\Microsoft\Windows\CloudContent|
  DisableWindowsSpotlightFeatures" and Apply stayed disabled — but the duplicate should never reach
  the validator: the PLAN COMPILER emitted two identical physical operations.
- **Exact sources:** Privacy "Windows 聚焦内容" (`SpotlightFeatures`, OptimizationCatalog) and
  Personalization "Windows 聚焦（锁屏内容）" (`DisableSpotlight`) both compile to the SAME mutation —
  `SetOfflineRegistryValue` on SOFTWARE\Policies\Microsoft\Windows\CloudContent
  \DisableWindowsSpotlightFeatures = DWord 1, Scope OfflineMachine. Only the OperationId differed
  (`opt|SpotlightFeatures|0` vs `opt|DisableSpotlight|0`), so ConflictKey collided and the validator
  (correctly) flagged it.
- **Fix (normalization layer, validator NOT weakened):** `CustomizationOperation` gains a canonical
  effective-target identity — registry SCOPE + normalized hive + normalized key path + normalized
  value name (case-insensitive, separator-normalized, scope is part of identity so OfflineMachine and
  OfflineDefaultUser never merge) — and a mutation-semantics comparison (operation type + value kind +
  normalized data; DWord/QWord compared numerically so "1" == "0x1" == "01"). `CustomizationPlan
  .AddOperation` now MERGES identical effective registry changes into ONE physical operation,
  retaining provenance (every originating definition id / operation id in `SourceDefinitionIds`);
  semantically DIFFERENT mutations of the same target stay as two operations and remain blocking
  conflicts. `ConflictKey` now includes the scope, so the validator never false-positives across
  scopes. `OptimizationKnowledgeItem` records `Definition.Id` as operation provenance.
- **Regression:** `Stage12p4PlanNormalizationTests` (15) — identical dedupe to 1, different value
  stays blocking, different scope / DefaultUser-vs-machine never merge, provenance retains both
  sources, totals reflect deduped executable count, validator still rejects AppX duplicate, true
  conflict UI visible, real DisableWindowsSpotlightFeatures target dedupes + validates, Gaming /
  Lightweight / Developer selection sets compile + validate, manual true conflict blocked, numeric
  and path-case normalization. Full suite **739 pass (Core 53, App 686), 0 errors, 0 warnings**.

### Fixed (Phase 12 Stage 12.3 — REAL DESKTOP BLOCKER: Review 校验计划 no-op / Apply disabled)

- **Root cause:** 「校验计划」gave no visible response and 「应用到已挂载镜像」stayed disabled.
  (1) A successful validation set the plan Validated but showed NO success feedback, so the run
  looked dead; (2) a FAILED validation (blocking issues such as duplicate/conflicting operations —
  e.g. two selected ops sharing a ConflictKey) kept displaying 「没有校验警告」 because
  `PlanReviewViewModel.Warnings` was replaced without notifying the derived `HasWarnings`, so the
  real blocking reason stayed invisible while Apply (keyed on `Plan.Status == Validated`) stayed
  disabled; (3) a throwing validator was swallowed silently. Next staying disabled until Apply is
  EXECUTED is the intended contract (the Apply step is NotAvailable before execution succeeds).
- **Fix:** `ValidatePlan` now sets explicit localized outcome state — `ValidationPassed` /
  `ValidationMessage` / `HasValidationFailure` — shown as green success
  「计划校验通过，可以应用修改。」or red blocking failure 「计划校验失败：N 个阻塞问题…」;
  Warnings notifies HasWarnings; exceptions surface the exact error (localized, logged, never
  silent); ApplyCommand/NextCommand re-evaluate immediately (no timing hacks). New resx keys
  Review.ValidatePassed/ValidateFailed/ValidateError (zh + en).
- **Regression:** `Stage12p3ReviewValidationTests` (14) cover: plan exists, ValidateCommand
  executes through the ICommand surface, Validated state set, visible success feedback (real resx),
  Apply disabled before / enabled immediately after validation, non-blocking unselected issue,
  blocking duplicate keeps Apply disabled + shows reason, validation exception surfaces error +
  logged, CanExecuteChanged fires, mounted-state gating, zh/en strings. WPF binding audit gains
  PlanReviewView.Validated and PlanReviewView.ValidationFailed render cases (0 binding errors).

### Fixed (Phase 12 Stage 12.2 — REAL DESKTOP BLOCKER: Storage page Run.Text exception)

- **Root cause:** the Settings → 存储 usage line was composed from multiple `<Run>` inlines
  (including `Run.Text` MultiBindings). Compile-time checks passed (legal XAML) and the render
  smoke test missed it because the scanned-state StackPanel was still Collapsed at layout time, so
  WPF skipped the Inlines. On the real desktop the moment the scanned state became visible WPF
  materialized the inlines during layout and threw while setting
  'System.Windows.Documents.Run.Text' — repeated global error dialogs.
- **Fix:** the usage line is now a single stable `TextBlock.Text` binding to a fully formatted,
  localized `StorageViewModel.UsageSummaryText` (active · recoverable · disposable). `StorageView.xaml`
  no longer contains any `Run`/`Inline`. Storage labels (`ActiveLabel`/`RecoverableLabel`/
  `DisposableLabel`) moved into the view model with the same resx keys.
- **Second defect found & fixed while testing:** `CleanAsync` set 已清理 X but immediately reset it
  via the follow-up `ScanAsync()`. `ScanAsync(clearResultText:)` now keeps the cleanup result line
  visible (only user-initiated scans reset it).
- **Regression:** `StorageViewRenderRegressionTests` (9 new, zh-CN + en-US) build the real
  `StorageViewModel` against the real resx service, activate the scanned state, force a full layout
  pass, and assert every display state (root, free space, low-space, validation error, cleanup
  result) renders without exception; a guard test asserts StorageView.xaml never contains `<Run`
  again. The WPF binding audit's Storage cases now also activate the scanned/error states.

### Added (Phase 12 Stage 12.2 — Finish cleanup + workspace-root settings)

- **Workspace-root settings.** Settings → 存储 now shows the temporary workspace location with free space;
  [更改位置] / [恢复默认位置] persist across restarts, reject invalid/unwritable roots and block the change
  while an image is mounted. Old roots are remembered so cleanup still finds leftovers there.
- **Finish auto-cleanup.** Completing a build now cleans the completed workspace automatically (final ISO is
  always preserved; recoverable checkpoints are retained). The Build step reports 已清理临时文件：X GB,
  or 保留恢复数据：X GB, or 部分未能清理 with [立即重试清理]. A cleanup failure never fails the build.
- **Discard auto-cleanup.** Unmount/Discard cleans the disposable workspace in the background.

### Phase 11 — COMPLETED (2026-08-12)

- **Phase 11 (Component Intelligence Foundation) is complete and merged to `main`** — real-desktop
  validation passed on Windows 11 25H2 zh-CN x64 Consumer: profile selection applies safe
  recommendations immediately, the selected count updates, user overrides stay protected, the
  recommendation-detail overlay opens and returns correctly, all six Customize tabs + Review work, and
  Gaming / Developer / Lightweight produce meaningful differences.
- **Non-blocking follow-up (ADR-061):** the Custom profile currently disables Extra Scenarios; a future
  polish will let extras act as keep/recommendation hints in Custom mode without enabling a primary preset.

### Added (Phase 11 Stage 11.4 — Scenario Profile / Recommended Configuration Engine — phase/11-component-intelligence)
- **Recommended configuration selector at the top of Customize.** Pick one or more usage scenarios
  (均衡推荐 / 游戏优先 / 开发工作站 / 办公稳定 / 轻量系统 / 专用精简 / 自定义, multi-select; Custom is
  exclusive) and every tab's recommendation badges + "为什么" change accordingly — with NO checkbox
  changing automatically. A summary line shows 当前推荐配置 + 建议精简 / 按需确认 / 建议保留 / 存在冲突
  counts derived from real present items only.
- **Deterministic, explainable recommendations.** The engine computes effective recommendations from
  component knowledge + scenario rules + real image contents + risk + dependency constraints, with
  documented precedence (safety > your manual choice > required dependency > profile requirement >
  scenario override > component default). Conflicts between profiles (e.g. 轻量系统 trims
  virtualization vs 开发工作站 requires it) are resolved visibly — KEEP wins — with a reason.
  Every reason is a deterministic localized key, never runtime AI prose.
- **Non-destructive preview + safe adoption.** 查看推荐方案 shows what WOULD be selected, grouped
  推荐执行 / 建议保留 / 需要确认 / 冲突·阻止. Only 采用推荐选择 changes selections, and only for
  present, apply-supported, low-risk (Risk==Low), conflict-free items — High/Critical, blocked,
  incompatible, unsupported-apply and conflicted rows always stay manual. 重新应用推荐 re-runs the
  same rules.
- **Your manual choices are never overwritten.** After adopting, toggling any checkbox manually marks
  it as a user override; switching profiles or re-applying recommendations leaves it untouched.
- **Per-workflow state.** The chosen profile + overrides belong to the current workflow only; a new
  image starts clean with no profile selected (manual mode).
- **Compact profile panel (UX refinement).** The selector is a single compact row of profile cards
  (name + short subtitle); 采用推荐选择 is the one clear primary action, 查看推荐详情 is a light
  secondary link, and 重新采用推荐 only appears after you adopt and then diverge (manual change or
  profile switch). 自定义 disables profile overrides (back to catalog defaults) while keeping your
  manual selections. The Discover button becomes 重新扫描 after discovery, and the component lists
  keep the majority of the page.
- **Profile selection is now the adoption.** Picking a primary profile (or an extra scenario)
  immediately applies its safe recommended selections — the separate "采用推荐选择" button is gone.
  Profile-managed selections track provenance and update when you switch profiles; your manual
  choices are never silently overwritten (an explicit 恢复此配置推荐 appears only when an override
  exists). 查看推荐详情 opens as an overlay with × 返回自定义, and 已选 N 项 updates instantly.
- **Final compact Customize layout.** The header (title + subtitle + scan/已选 N 项) is one compact
  block; the profile panel is a two-column grid (primary profile cards LEFT ~75%, extra scenarios RIGHT
  ~25%) with a single-line summary + actions row; the tab control takes the star height so the component
  list is the primary work surface (~7 rows visible at 1200×700). The recommendation engine is unchanged.
- **Primary profile + extra scenarios (rework).** 主要用途 is now ONE mutually-exclusive radio choice
  (3-column aligned cards: 均衡推荐/游戏优先/开发工作站/办公稳定/轻量系统/专用精简/自定义); 额外需求
  are independent checkboxes (Xbox/Game Pass, WSL/Docker, 打印扫描, 触屏/手写, 远程桌面) combined by
  the same engine. Every primary profile now produces a meaningful multi-tab configuration
  (查看方案详情 groups 推荐自动执行 / 需要确认 / 建议保留 / 冲突·不可执行 with per-tab counts), and
  each changed recommendation explains itself: 配置建议: 游戏优先 → 建议保留.

### Added (Phase 11 Stage 11.3 — Customize coverage expansion + Personalization activation — phase/11-component-intelligence)
- **Personalization tab is live (no longer "Coming Soon").** The sixth Customize tab now shows 14
  reviewed controls across Start/Search (hide Recommended / recently added apps), Taskbar (hide
  Widgets / Task View, search as icon), Explorer (show file extensions / hidden files, open to This PC,
  Quick access recents/frequent), Lock screen/Desktop (Windows Spotlight) and Appearance (dark mode,
  transparency, animations). Each row shows purpose, recommendation, risk, offline scope and how to
  revert; user-scope settings apply to NEW users of the offline image (Default User profile), never the
  host registry.
- **Windows Components tab is now knowledge-backed.** Optional features (Hyper-V, Windows Sandbox,
  WSL, Virtual Machine Platform, legacy Media Player, IPP/scanning/XPS, PowerShell 2.0 engine,
  Hypervisor Platform) and capabilities (OpenSSH Client/Server — `OpenSSH.Client~~~~0.0.1.0` /
  `OpenSSH.Server~~~~0.0.1.0`, per Microsoft docs managed via Get/Add-WindowsCapability) are shown
  with human purpose/recommendation/risk, dependency notes and exact DISM targets. Supported feature
  rows add a strongly typed "feature disable" to the plan; capability rows are visible for knowledge
  but their checkbox is disabled with "当前版本暂不支持应用" until capability execution is reviewed.
- **Services / Privacy / System tabs now share one knowledge surface.** Services lists 11 reviewed
  services (proposed startup changes only for allowlisted ones; core services shown as informational
  and blocked); Privacy and System gained reviewed registry-policy controls (input personalization,
  speech model updates, location, Find My Device, feedback prompts, Spotlight content; Delivery
  Optimization, device metadata, remote assistance, hibernation, Windows AI/Recall data analysis,
  OneDrive sync, web print drivers).
- **Review plan now names the exact action.** Every selected change is listed with its action type
  (移除/禁用/配置/服务/功能), category, offline scope and revert contract before Apply.
- **Coverage matrix.** `.tmp/phase11/stage11.3-coverage-matrix.md` documents every candidate
  (implemented/deferred/rejected/unsupported) with provenance and reason.

### Fixed (Phase 11 Stage 11.3 — OpenSSH capability correction — ADR-056)
- **OpenSSH Client/Server are CAPABILITIES, not Optional Features.** Per Microsoft official docs they
  are managed through Get-WindowsCapability / Add-WindowsCapability with identities
  `OpenSSH.Client~~~~0.0.1.0` / `OpenSSH.Server~~~~0.0.1.0` — they never appear in DISM `/Get-Features`.
  The Windows Components catalog now targets the Capability inventory with exact capability identities
  (mechanism `RemoveCapability`), and both names are removed from the feature allowlist. When present
  on an image they stay visible for knowledge, but their checkbox is disabled with "当前版本暂不支持
  应用" and selecting them is a no-op — no Apply operation silently Skips. Feature-shaped
  "OpenSSH.Client" / "OpenSSH.Server" raw items no longer match these definitions.

### Added (Phase 10 — Build / ISO Export — feature/iso-build)
- **Build / ISO Export pipeline (ADR-038):** replaces the honest placeholder Build step with a real,
  safe ISO-rebuild engine. `IBuildService` (Core) + `ImageBuildService` (Infrastructure) orchestrate
  an explicit state machine `Preflight → CommittingImage → ExportingImage → PreparingMedia →
  BuildingIso → Verifying → Completed/Failed/Cancelled`; the terminal `BuildState` is the single
  authority for success. An atomic `build.recovery.json` (written via a `.partial` file renamed into
  place) records the in-flight phase + paths for crash recovery.
- **Commit (not discard) on build (ADR-039):** the pipeline calls `IImageServicingService
  .CommitUnmountAsync` (DISM `/Unmount-Image /Commit`) so the customized working image is persisted
  into the final ISO; `/Discard` is never used on the build path, and a commit failure stops the build
  (no ISO, workspace left recoverable).
- **Clean WIM export + ESD normalization (ADR-040):** `IWimExporter`/`DismWimExporter` run
  `DISM /Export-Image /Compress:max /CheckIntegrity` to a clean `install.wim`; `IIsoMediaPreparer`/
  `IsoMediaPreparer` copy the original ISO media tree read-only and replace the payload — overwriting
  `sources\install.wim` for a WIM source, or deleting `sources\install.esd` and writing
  `sources\install.wim` for an ESD source (always index 1).
- **Bootable ISO via oscdimg, never faked (ADR-041):** `IBootableIsoBuilder`/`OscdimgIsoBuilder`/
  `OscdimgArgumentBuilder` use the Windows ADK `oscdimg.exe` with dual-boot args
  `-bootdata:2#p0,e,b"<etfsboot.com>"#pEF,e,b"<efisys.bin>" -m -o -u2 -udfver102`. The builder fails
  fast and clearly when the ADK is missing (`IAdkToolLocator`) or a boot file is absent — it never
  fabricates a non-bootable ISO; the UI surfaces `AdkMissing` before build.
- **Independent verification + recovery (ADR-043):** `IBuildVerifier`/`BuildVerifier` re-check the
  output independently (ISO exists + size, `sources\install.wim` present, no WIM mounted via
  `dism /Get-MountedImageInfo`, expected edition/index via `dism /Get-ImageInfo`); a failed
  verification makes the build fail. `DetectInterruptedBuildAsync`/`CleanupInterruptedBuildAsync` let
  the next run clean a crashed workspace before starting.
- **Build UI (ADR-042):** `BuildStepViewModel` derives every input from `IAppState`, gates `CanBuild`
  (Applied + Mounted + ADK + non-empty output dir/file), defaults the file name to
  `WinForge_<Edition>_<yyyyMMdd-HHmm>.iso` (edition spaces normalized to `_`, illegal chars
  sanitized), uses an explicit overwrite policy (default `GenerateUniqueName`), runs a cancellable
  `AsyncRelayCommand`, and surfaces the terminal state + output path/size + log from `BuildResult`
  (the final stage/log are pinned from the result so async progress delivery cannot leave the UI
  mid-state). Success transitions the workspace Mounted → Prepared.
- **≈35 new automated tests** for Phase 10 (pipeline orchestrator, component unit tests for
  `OscdimgArgumentBuilder`/`OscdimgIsoBuilder`/`IsoMediaPreparer`/`DismWimExporter`/`BuildVerifier`,
  and `BuildStepViewModel`). Total suite: **397 pass (Core 37, App 360), 0 errors, 0 warnings
  (Release)**, all CI-safe (no ISO / admin / internet). Covers: success path + source-untouched +
  workspace-cleaned; ADK-missing preflight; missing source ISO / working image; empty required field;
  commit/export/media/ISO-tool/verify failures; cancellation → Cancelled; output conflict (Fail vs
  GenerateUniqueName); ESD-source coverage; dual-boot arg assembly; missing-boot-file and
  missing-ADK ToolMissing; ViewModel defaults/gating/success/cancel.

### Status (Phase 10 — Build / ISO Export — COMPLETED)
- **COMPLETED / REAL DESKTOP VALIDATION PASSED** (2026-08-10). Built on Step 3.2/3.3 — the pipeline
  contains no UI-side DISM/oscdimg calls (coordination only; the actual tools live behind Core
  interfaces in Infrastructure). ADR-038…ADR-044. Merged to `main` via `--no-ff` on 2026-08-10.
- **Automated suite: 440 pass (Core 37, App 403), 0 errors, 0 warnings (Release).** Real-desktop
  validation PASSED on Windows 11 25H2 (Chinese Simplified, x64, Consumer Editions, `install.wim`):
  Build entered after Apply; working image committed + automatically unmounted; clean install.wim
  exported; media tree copied + payload replaced; ReadOnly/System/Hidden `autorun.inf` defect fixed;
  oscdimg dual-boot ISO built; `BuildVerifier` confirmed the output ISO + `sources\install.wim` +
  expected edition/index and that no WIM remained mounted; build reached 100% / Completed; Finish
  navigates Workflow → Home; app stays running; ISO preserved. Phase 10 is therefore **COMPLETED**.

### Fixed (Phase 10 — autorun.inf ReadOnly/System/Hidden unlock + resumable checkpoint, `cb0165a`)
- **Real-desktop defect:** the build's `PreparingMedia` step failed with
  `UnauthorizedAccessException: Access to the path 'autorun.inf' is denied.` at ~60% because
  `WindowsFileSystem.CopyFile` used `File.Copy`, which preserves the **mounted ISO's** ReadOnly
  attribute and then cannot overwrite a ReadOnly destination. The fix clears ReadOnly/System/Hidden
  on the **build copy only** (never the source) in `CopyFile`/`MoveFile`/`DeleteFile`/
  `DeleteDirectory`, adds deterministic media-tree cleanup via `DeleteTreeHandlingReadOnlyAttributes`,
  and makes `IsoMediaPreparer` use a fresh per-build directory with precise failure logging
  (source/dest/attrs/op/exception).
- **Resumable post-commit checkpoint:** `ImageBuildService` now skips Commit/Export when the durable
  `install.wim` already exists; a committed/exported artifact is retained on a post-commit failure so
  the next run resumes without re-Apply; only the dirty media tree + partial output are discarded.
  `BuildStepViewModel.HasBuildCheckpoint` keeps `CanBuild` usable after Commit (image unmounted)
  without re-Apply.
- Tests: added `WindowsFileSystemTests` (real IO, exact `autorun.inf` repro) + `IsoMediaPreparerTests`
  (fake mount) + extended orchestrator/ViewModel tests (10 defect regression cases). Total **420 pass
  (Core 37, App 383), 0 errors, 0 warnings (Release)** at this fix.

### Added (Phase 10 — final-step Finish UX polish, `912522c`)
- **Final-step UX:** when Build is the current final wizard step, the disabled "Next" is hidden and a
  localized, completion-gated **Finish** (`Nav.Finish` = Finish / 完成) is shown instead — enabled
  only when `BuildState == Completed`. Failed/Cancelled builds stay on Build and never present a
  successful Finish. Clicking Finish ends the session cleanly via `INavigationService.NavigateTo
  (PageKey.Home)` and **never deletes the generated ISO**.
- **Open output folder:** `IFileLauncher` + `WindowsFileLauncher` (swallows shell exceptions so it is
  headless/test-safe) back a new `OpenOutputFolderCommand` / `CanOpenOutputFolder` in
  `BuildStepViewModel`; a localized `Build.OpenOutputFolder` (Open output folder / 打开输出文件夹)
  button in `BuildView.xaml` opens the folder containing the ISO and is enabled only when the output
  exists. Localization is via `Loc[key]` + resx — no hard-coded language checks.
- Tests: added `WizardFinishButtonTests` (7 cases). Total **427 pass (Core 37, App 390), 0 errors, 0
  warnings (Release)** at this fix.

### Fixed (Phase 10 — Finish navigation defect; wizard surface routed through INavigationService, `084dafb`)
- **Real-desktop defect:** Finish was visible + enabled but clicking it did nothing. Root cause:
  `INavigationService.CurrentPage` initialized to `PageKey.Home` and was **never updated when the
  wizard was shown** — `MainViewModel` set `ActiveView = _workflow` directly, bypassing the navigation
  service. So `Finish()` → `NavigateTo(Home)` short-circuited (already "Home"), `CurrentPageChanged`
  never fired, `OnNavigated` never ran, and the wizard stayed visible. The wizard surface and the
  utility navigation were desynced coordinators.
- **Fix:** `PageKey.Workflow` added (Core enum); `MainViewModel` now shows the wizard via
  `NavigateTo(PageKey.Workflow)` (constructor + rail button + commands) and `OnNavigated` handles
  `PageKey.Workflow` without resetting the step. `CurrentPage` always matches the visible surface, so
  `Finish()` → `NavigateTo(Home)` is a real Workflow → Home transition: `ActiveView = HomeViewModel`,
  `IsWorkflowActive = false`. `WorkflowViewModel.Finish()` itself is unchanged. No
  `Application.Shutdown()`; ISO/logs/workspace untouched.
- Tests: added `WizardFinishNavigationTests` (13 shell-level integration cases driving the real
  `MainViewModel` + real `NavigationService` — exactly-once Home navigation, shell shows HomeView,
  wizard no longer current, ISO/logs preserved, no dismount/remount, failed/cancelled cannot Finish,
  zh-CN/en-US identical); `AppBootTests` updated for the new startup navigation log. Total **440 pass
  (Core 37, App 403), 0 errors, 0 warnings (Release)**.

### Added (UX Workflow Refactor + Localization Foundation — feature/wizard-localization)
- **Wizard/Stepper primary workflow (ADR-032):** the left feature-list nav is replaced by a gated
  6-step Stepper — Source → Prepare → Customize → Review → Apply → Build (zh: 选择镜像 → 准备镜像 →
  自定义 → 审核计划 → 应用修改 → 构建镜像). Step state (NotAvailable / Available / Current / Completed /
  RequiresAttention) is derived purely from `IAppState` by `WorkflowViewModel.RecomputeStates()`;
  `CanGoNext` / `CanGoBack` and `CanGoToStep` (skip-guard) enforce prerequisites; source-change /
  mounted / executing safety guards protect the plan. Source and Prepare share one `ImageViewModel`.
- **Utility vs workflow navigation (ADR-033):** Home / Logs / Settings / About are a separate rail in
  `MainViewModel`; legacy `INavigationService` deep-links are translated onto the matching step
  (Image→Source, Components/Privacy/System/Experience→Customize, Plan→Review, Build→Build).
- **Localization architecture (ADR-034):** all user-facing strings moved to `Strings.resx` (neutral)
  with a `Strings.zh-CN.resx` satellite (parity tested). `ResourceManagerLocalizationService` is
  exposed to XAML as `Loc`; `LocKeyMultiConverter` re-evaluates bindings on both key and culture
  change; `ILocalizationService` lives in Core so non-UI code can localize.
- **Runtime language switching + persistence (ADR-035):** `SetCulture` updates the thread +
  ResourceManager and raises `PropertyChanged` / `CultureChanged` for a live switch;
  `ILanguageSettingsStore` (InMemory + File) persists the choice; `LocalizationBootstrap.Initialize`
  applies the saved culture and falls back to a shipped language (en / zh-CN) on an invalid saved
  value. English is the ultimate fallback for missing keys and the default culture.
- **Friendly metadata preserves technical ids (ADR-036):** `FriendlyMetadataProvider` +
  `ISelectableItem` show localized service/app names while always displaying and operating on the
  immutable technical id (e.g. `DiagTrack`, `Microsoft.BingWeather`); `ServiceConfigPolicy` still gates
  which services are configurable. Unknown identifiers return the raw name (no fabrication).
- **ComponentsViewModel re-entrancy fix (ADR-037):** a `_suppressPlanResync` flag prevents a selection
  toggle's plan mutation from re-entering `ResyncSelections()` and cancelling itself; external plan
  resets (e.g. source change) still re-sync; `DiscoverCommand.CanExecuteChanged` is raised explicitly
  after every state transition (no `CommandManager.RequerySuggested`).
- **Customize tabs:** Apps / Windows Components / Services / Privacy / System / Experience. Review
  reshapes the Plan; Apply is the execution UX; Build is an honest placeholder (no fake ISO rebuild).
- **≈100 new automated tests** across WORKFLOW / COMMANDS / LOCALIZATION / SERVICE / APP / REGRESSION
  (the workflow + localization foundation added 49–50; the seven real-desktop defect fixes added the
  rest as targeted regression suites). Total suite: **362 pass (Core 37, App 325), 0 errors, 0 warnings (Release)**, all
  CI-safe (no ISO / admin / internet). Covers: wizard initial state + readiness transitions + skip-guard;
  command can-execute without auto-requery (explicit raise); localization bootstrap init + zh-CN switch
  + parity; friendly service/app metadata + `ServiceConfigPolicy` gating; and regression
  (ImageViewModel falls back without localization, workflow never auto-advances, Apply hidden until
  Validated, source-change clears plan + discovery unless Executing).

### Status (UX Workflow Refactor + Localization Foundation — COMPLETED, real-desktop validation PASSED)
- **IMPLEMENTED / REAL DESKTOP VALIDATED / COMPLETED** (2026-08-09). Built on Step 3.3 — workflow
  code contains no DISM. ADR-032…ADR-037. Merged to `main` via `--no-ff` (feature history preserved,
  no squash).
- **Real-desktop acceptance chain (all PASS) on Windows 11 25H2 zh-CN x64 Consumer `install.wim`:** (1)
  Source ISO selection + ISO inspection; (2) Prepare isolated working image; (3) Mount working WIM;
  (4) Mounted → Next enables; (5) Customize page loads; (6) all Customize tabs load with no XAML/
  binding crash; (7) runtime localization / zh-CN UI works; (8) discovery + selections work; (9)
  selecting 1 valid customization enables Review/Next; (10) Review page loads; (11) plan validation
  succeeds; (12) Apply to mounted image succeeds; (13) execution success advances Wizard state
  (Review→Completed, Apply→Available, Next→enabled); (14) Build placeholder reachable; (15) Build/ISO
  export intentionally NOT implemented (honest placeholder, not a validation failure); (16) working
  image cleanly unmounted/discarded, no mounted WIM remains.
- **Total suite: 362 pass (Core 37, App 325), 0 errors, 0 warnings (Release).** Feature branch carried
  seven real-desktop defect fixes: ISO mount robustness / valid Get-Volume resolution (`7f90d48`/
  `4360612`), servicing workspace nested notification (`28b8bb5`), `CustomizationPlan` nested
  notification (`06de8f9`), `Run.Text` MultiBinding `Mode=OneWay` (`f1e4370`), read-only binding `OneWay`
  audit (`9f68f97`), `ErrorDialogGuard` (`bed2e73`), Review/Apply gating on execution success (`7da38dd`).
- **Next phase: Build/ISO Export — NOT STARTED.** Build/ISO export is intentionally NOT implemented
  (the Build step is an honest placeholder; no ISO-rebuild engine exists yet — roadmap Phase 10).

### Added (Phase 3 Step 3.3 — offline customization plan & execution engine)
- Declarative, platform-agnostic customization model in Core (no DISM/Win32):
  `CustomizationPlan` with a strict lifecycle (`Draft` → `Validated` → `Executing` →
  `Completed`/`CompletedWithErrors`/`Failed`/`Cancelled`); `CustomizationOperation`
  (OperationType, exact TargetIdentifier, registry/service targets, `RiskClass`,
  `ExecutionOrder`, `ValidationResult`); `CustomizationResult` (computed
  `Success = FailedOperations == 0`, plus `TotalOperations`/`Succeeded`/`FailedOperations`/
  `CriticalFailure`/`Summary`); `DiscoveryInventory` (Appx, packages, offline services,
  trusted privacy/system definitions); and typed enums (`CustomizationCategory`,
  `CustomizationOperationType`, `RiskClass`, `ServiceStartType`, `OperationValidationResult`).
  `Validate()` recomputes issues (Duplicate/Conflict/Unsupported/MissingTarget) and only
  marks `Validated` with no blocking issues and ≥1 selected op; `FreezeForExecution()`
  snapshots selected ops and locks the live plan (ADR-020).
- Discovery services (Infrastructure, behind Core interfaces): `DismAppxParser` enumerates
  provisioned Appx by exact DISM "Deployment package name"; `DismPackageParser` enumerates
  packages and classifies `Removable` only for an explicit allowlist
  (`InternetExplorer-Optional`, `Printing-XPSServices`, `Xps-Document-Writer`) — everything
  else is `Protected`; `OfflineRegistryService` loads/unloads offline hives via Win32
  `RegLoadKey`/`RegUnLoadKey` under a WinForge-owned `HKLM\WinForge_<BASE>` name (always
  unloaded in `finally`); `MountIdentityValidator` confines paths to the mount and binds the
  session; `WindowsCustomizationDiscoveryService` enumerates services from the mounted
  image's `SYSTEM` hive and merges a trusted `CustomizationDefinitionProvider` (5 Privacy + 3
  System registry settings, 3 recommended service-start changes: DiagTrack/WerSvc/PcaSvc →
  Disabled); discovery refuses to run on a session mismatch (ADR-021–ADR-024).
- Execution orchestrator (`WindowsCustomizationExecutionService`): a pre-run critical-stop
  guard fails the whole result as `CriticalFailure` unless the workspace is `Mounted`, the
  mount session matches, DISM registered the mount, and the plan is `Validated`; execution
  runs a frozen snapshot in defined order (registry 0 → services 1 → appx 2 → packages 3 →
  files 4 → scheduled tasks 5); per-operation status; non-allowlisted `Removable` packages
  are `Skipped` (never removed); a missing service is `Skipped`; and the image is **left
  mounted** (no commit/unmount — owned by Step 3.2). `IAppState` carries a "dirty" flag so
  the UI can warn before discarding a customized working image (ADR-022, ADR-025).
- UI: Components / Privacy / System / Plan pages backed by discovery; selection toggles
  declarative plan operations (`PlanSync`); an explicit `Validate` then `Apply` flow;
  `PlanReviewViewModel` shows operation totals, warnings, progress, and a result summary.
  A new `Plan` `PageKey` and four `DataTemplate`s (`App.xaml`) wire navigation; the new
  `BooleanToVisibilityInverseConverter` supports conditional UI. Every operation is confined
  to the mounted working image — no host path, source-ISO root, or arbitrary command/
  registry/filesystem delete is ever issued; `AsyncRelayCommand.CanExecuteChanged` is raised
  after state changes (the Step 3.2 real-desktop defect pattern is avoided).
- 206 automated tests pass (Core 37, App 169), 0 errors, 0 warnings (Release), and are
  CI-safe: no ISO, no administrator, no internet. Coverage: DismAppxParser (11), discovery
  service (6), execution service (13 — guard/allowlist/skip/hive-never-touched), safety
  (16 — registry safety, mount validator, definition provider), view models (11),
  Core model lifecycle/validation (16), and the updated headless boot test. xUnit 2.5.3 with
  fakes for `IProcessRunner`/registry/definition/mount-identity/discovery/execution.

### Fixed (Phase 3 Step 3.3 — real-desktop validation defect fixes, ADR-026/ADR-027)
- **DEFECT 1 — provisioned-Appx discovery returned 0:** `DismAppxParser` only matched the
  invented multi-word key "Deployment package name"; real `dism /Get-ProvisionedAppxPackages
  /English` emits single-word `PackageName`/`DisplayName` headers. The parser now matches the
  real headers (and tolerates the legacy spaced forms). Separately, `RunDismAsync` discarded
  the DISM exit code and stderr, so a DISM failure or unexpected/localized output was
  indistinguishable from a genuine zero; it now checks the exit code and stderr and rejects
  unrecognized output, and `DiscoveryInventory` reports per-source `Success`/`Failed` status.
- **DEFECT 2 — offline service discovery returned 0:** `RegLoadKey`/`RegUnLoadKey` require
  `SeRestorePrivilege`/`SeBackupPrivilege`, which are present in an elevated token but disabled
  by default; `OfflineRegistryService` now enables them before each call. A failed hive load /
  enumeration is now surfaced as `ServiceStatus = Failed` instead of a silent "0 services".
- **DEFECT 3 — unsafe package selection:** the removal allowlist is now the single
  `PackageRemovalPolicy` source of truth, enforced at discovery classification (non-allowlisted
  packages become `Protected` → not selectable in the UI), plan validation (`RecomputeValidation`
  flags `Protected` selected ops as `Unsupported`; `PlanSync` also refuses to add them directly),
  and execution (final `Skipped` guard). A non-allowlisted package (e.g.
  `Microsoft-OneCore-ApplicationModel-Sync-Desktop-…`) can no longer be selected or removed.
- 220 automated tests pass (Core 37, App 183), 0 errors, 0 warnings (Release), all CI-safe.
  Step 3.3 remains **PENDING real-desktop validation — re-run required**; not merged to `main`.

### Fixed (Phase 3 Step 3.3 — offline-services silent-zero re-investigation, ADR-028)
- **DEFECT 2 re-investigation (silent "0 services"):** real-desktop evidence confirmed the
  SYSTEM hive file exists and is readable (`<mount>\Windows\System32\Config\SYSTEM`, 9,175,040
  bytes), so the cause was **not** a missing/wrong hive path. The residual silent-zero path was
  a *successfully loaded* hive whose resolved `ControlSet00x\Services` enumeration returned
  empty, which previously returned `Success` with 0 items. `DiscoverServices` now treats an
  empty Services enumeration as `ServiceStatus = Failed` (never a misleading "0 services").
- **Diagnostics:** `OfflineRegistryService` now depends on `ILoggerService` and logs the full
  load/unload lifecycle — redacted hive file path, the WinForge-owned temporary HKLM name,
  `SeRestorePrivilege`/`SeBackupPrivilege` enablement outcome (incl. `ERROR_NOT_ALL_ASSIGNED`),
  `RegLoadKey`/`RegUnLoadKey` return codes, the resolved ControlSet, and the service count. The
  mount-root prefix is redacted (`<mount>`) and no host-registry data is logged, preserving the
  host-system safety boundary.
- 221 automated tests pass (Core 37, App 184), 0 errors, 0 warnings (Release), all CI-safe
  (added `Discover_SurfacesEmptyServicesEnumeration_AsError_NotSilentZero`). Step 3.3 remains
  **PENDING real-desktop validation — re-run required**; not merged to `main`.

### Fixed (Phase 3 Step 3.3 — Appx removal-identity / fixture fidelity, ADR-029)
- **DEFECT 1 confirmation (reproduced independently on a real mounted image):** `dism /English
  /Image:<mount> /Get-ProvisionedAppxPackages` **succeeds** and returns many packages, yet
  WinForge reported "Discovered 0 app(s)". The remaining mismatch: the Step 3.3 report described
  the parser as keying on the invented multi-word "Deployment package name", while the real
  `/English` output uses the single-word `PackageName` header (`DisplayName` listed first).
  The live parser already accepted `PackageName`, but the doc comment, fixtures, and historical
  ADR text still referenced the synthetic key.
- **Fix:** parser **doc comment** corrected to state the real single-word `PackageName`/
  `DisplayName` headers; `DismAppxParserTests.Sample` and
  `WindowsCustomizationDiscoveryServiceTests.AppxOut` now contain REAL DISM output copied from the
  desktop test (Clipchamp, BingWeather, Windows.Photos). Removal identity is the exact
  `PackageName` (full `name_version_arch_~_publisher-hash`) end-to-end
  (`DismAppxParser` → `ComponentsViewModel.SyncAppx.TargetIdentifier` →
  `/Remove-ProvisionedAppxPackage /PackageName:"…"`); `DisplayName` is display-only and a block
  without `PackageName` is dropped (never keyed by `DisplayName`). The four-way outcome contract
  is explicit: valid+found → `Success(N)`; valid+genuine-zero → `Success(0)` (legitimate); command
  failure → `Failed`; unrecognized output → `Failed`.
- **Tests added:** `RemovalIdentity_IsExactPackageName_NotDisplayName`,
  `IsRecognizedOutput_True_ForGenuineZeroWithBanner`,
  `Discover_GenuineZeroAppx_IsSuccess_NotFailed` (and the Appx `Sample`/`AppxOut` fixtures
  replaced with real output). 224 automated tests pass (Core 37, App 187), 0 errors, 0 warnings
  (Release), all CI-safe. Step 3.3 remains **PENDING real-desktop validation — re-run required**;
  not merged to `main`.

### Fixed (Phase 3 Step 3.3 — service-inventory safety boundary, ADR-030)
- **NEW safety defect (real-desktop re-validation):** the Components page exposed a disableable
  checkbox for **every one of the 699 discovered service records** — including kernel / file-system
  drivers, performance and provider entries (`.NET CLR Data`, `.NET Data Provider for Oracle`,
  `.NET Memory Cache 4.0`, …) and other low-level `SYSTEM\ControlSet00x\Services` sub-keys. The
  prior code classified every discovered service as `RiskClass.Removable` unconditionally, so
  discovery success was wrongly treated as "safe to disable". The fix separates **DISCOVERED**
  from **USER-CONFIGURABLE**.
- **New `ServiceClass` enum** (`Unknown`/`Driver`/`Protected`/`Configurable`/`RecommendedConfigurable`)
  and new `ServiceConfigPolicy` (single source of truth, in Core) — only the trusted allowlist
  `DiagTrack` / `WerSvc` / `PcaSvc` may be reconfigured; a unit test pins it to
  `CustomizationDefinitionProvider`'s recommended service changes. `DiscoverServices` now reads each
  service `Type`: driver types → `Driver` (protected); allowlisted Win32 services →
  `RecommendedConfigurable`; everything else → `Protected`.
- **UI:** `ServiceSelectionItem.CanSelect` is driven by `ServiceClass`; non-selectable entries show
  a short reason ("Kernel / file-system driver…", "Not an approved service…", "Unknown service
  type…"). `ComponentsViewModel` shows **only configurable services by default**; a
  `ShowProtectedEntries` toggle reveals the protected/system entries read-only. Status message
  reports the true discovered total and hidden count.
- **Three-layer guard:** `PlanSync.Toggle` refuses unapproved service ids; `CustomizationPlan
  .ClassifyBase` flags unapproved service ops `Unsupported` (rejected by validation);
  `WindowsCustomizationExecutionService.ApplyService` retains a final `Skipped` guard; host SYSTEM
  hive boundary (`IsWithinMount` + `OfflineHivePaths`) intact.
- **Tests added (12):** driver/fs-driver/Win32-non-allowlisted/unknown classification, DiagTrack/
  WerSvc/PcaSvc configurable, UI hides protected entries, PlanSync refusal, plan-validation
  rejection, execution backstop, host-hive safety, and policy↔provider sync. 242 automated tests
  pass (Core 37, App 205), 0 errors, 0 warnings (Release), all CI-safe. Step 3.3 remains **PENDING
  real-desktop validation — re-run required**; not merged to `main`.

### Fixed (Phase 3 Step 3.3 — offline registry write success contract, ADR-031)
- **Real-desktop defect:** a 3-operation plan (remove BingWeather, disable DiagTrack, turn off
  advertising ID) reported "3 succeeded, 0 failed", yet independent `reg.exe` verification found
  the advertising-ID value **absent** from the offline `SOFTWARE` hive. Investigation showed two
  distinct root causes:
  1. **Wrong key path in the trusted definition** — `privacy.advertising-id` used
     `Microsoft\Windows\CurrentVersion\Advertising\Id` but the real Windows key is
     `Microsoft\Windows\CurrentVersion\AdvertisingInfo` (value `Enabled` directly under it). The
     write *succeeded at the wrong location*, so the expected path was empty.
  2. **Weak success contract** — `ApplyRegistry` returned `Succeeded` merely because `SetValue` /
     `DeleteValue` did not throw. There was **no post-write read-back verification** of existence,
     type, or value, so a write to the wrong location (or any silent non-persistence) was reported
     as success.
  - The hypothesized "duplicated `SOFTWARE\SOFTWARE` prefix" was **investigated and ruled out**: the
    write chain passes `op.RegistryKeyPath` **relative to the loaded hive root** (`HKLM\WinForge_SOFTWARE`),
    so no `WinForge_SOFTWARE\SOFTWARE\…` was ever produced. A guard was still added so the class of
    bug can never occur (see below).
- **Fixed definition path:** `privacy.advertising-id` → `Microsoft\Windows\CurrentVersion\AdvertisingInfo`
  (value `Enabled`, `DWord` `0`). All other 5 Privacy + 3 System definitions were audited and are
  correctly relative to the `SOFTWARE` hive root with no `SOFTWARE\` prefix (hive-prefix consistent).
- **Strengthened success contract:** after every `SetOfflineRegistryValue`, the engine now performs an
  independent read-back and confirms the value **exists**, has the **requested registry type**, and
  **equals the requested data** — otherwise the operation is reported `FailedRecoverable`. The same
  applies to `DeleteOfflineRegistryValue` (the value must be **absent** afterward). The production
  `OfflineRegistryService` also self-verifies on write/delete (throws on mismatch) as defense-in-depth.
- **Path-prefix guard:** new `OfflineHivePaths.NormalizeKeyPath` strips a leading `HKLM\` designator and
  any leading hive-base segment (`SOFTWARE\`, `SYSTEM\`) so a key path is always strictly relative to the
  loaded hive root — a stray `SOFTWARE\`/`HKLM\SOFTWARE\` prefix can no longer duplicate the hive base.
  Applied in both the execution engine and the production registry service.
- **Host isolation preserved:** write targets are still confined to the mounted workspace via
  `OfflineHivePaths` + `IMountIdentityValidator`; an unknown/absolute hive base (e.g. `HKLM`) is rejected
  before any write; the `SafeHiveNameRegex` / `Validate` ".." checks remain.
- **Tests added (17):** `OfflineRegistryContractTests` — SOFTWARE/SYSTEM root-relative mapping; no
  duplicated `SOFTWARE\SOFTWARE` prefix; DWORD & String write verified by read-back; create-missing-subkey;
  write-failure / write-persists-but-read-back-missing / wrong-value / wrong-type all → Failed; delete
  then verify absent (and delete-no-op → Failed); host-style hive base rejected; path-outside-mount
  rejected; the real `privacy.advertising-id` definition maps to the correct offline location; an
  operation is never reported success when the read-back would fail. 259 automated tests pass
  (Core 37, App 222), 0 errors, 0 warnings (Release), all CI-safe. Step 3.3 remains **PENDING
  real-desktop validation — re-run required**; not merged to `main`.

### Status (Phase 3 Step 3.3 — desktop validation PASSED)
- Step 3.3 real-desktop validation **PASSED** (2026-08-09) on a real Windows 11 25H2
  (Chinese Simplified, x64, Consumer Editions, `install.wim`) ISO: a mounted working image
  was discovered and customized. Observed successful run:
  - Provisioned-Appx discovery returned 47 packages with exact `PackageName` identity
    (independent `dism /Get-ProvisionedAppxPackages` inventory confirmed the same set).
  - Offline service discovery returned 699 services; only DiagTrack/WerSvc/PcaSvc were
    exposed as configurable (696 protected/driver entries hidden, no silent-zero).
  - Non-allowlisted packages (e.g. `Microsoft-OneCore-ApplicationModel-Sync-Desktop-…`)
    were non-selectable; protected packages clearly marked not permitted for removal.
  - Selected plan: remove `Microsoft.BingWeather`, disable `DiagTrack`, turn off advertising
    ID. Validation passed; Apply → **3 succeeded, 0 failed**.
  - Independent DISM confirmed `Microsoft.BingWeather` removed from the mounted image.
  - Offline `SYSTEM` hive confirmed `DiagTrack\Start = 0x4`; offline `SOFTWARE` hive confirmed
    `AdvertisingInfo\Enabled = 0x0` (ADR-031 corrected path + read-back contract verified).
  - Verification hives `WF_VERIFY_SYSTEM` / `WF_VERIFY_SOFTWARE` unloaded cleanly; subsequent
    queries confirmed both absent.
  - After validation the mounted image was cleaned up; `dism /English /Get-MountedWimInfo`
    reported **"No mounted images found."**
  - The original ISO / original `install.wim` were **not modified**.
- Step 3.3 = **COMPLETED**; Desktop Validation = **PASSED**; ready to merge to `main`
  (259/259 automated tests pass — Core 37, App 222, 0 errors, 0 warnings, Release). No Step 3.4
  / Phase 4 mount-engine work was started.

### Added (Phase 3 Step 3.1 — WIM workspace & image selection foundation)
- Introduces the durable selected-image foundation for Phase 3. Converts a Phase 2
  `IsoInspectionResult` + selected `WindowsEditionInfo` into a durable
  `ImageWorkspace` descriptor that survives ISO dismount: it stores the original
  ISO path (`SourceIsoPath`) and the image's **relative** path inside the ISO
  (`ImageRelativePath`, e.g. `sources\install.wim` / `sources\install.esd`), never a
  temporary mounted drive letter. Durable fields: `ImageType`, `SelectedIndex`,
  `SelectedEditionName`, `Architecture`, `Version`, `Build`, `Languages`.
- Core contracts: `IImageWorkspaceFactory` (`BuildWorkspace` → `ImageWorkspaceBuildResult`
  with `ImageWorkspaceStatus` `NotReady`/`Ready`/`Invalid` and structured issues) and
  `IWimService` (read-only Step 3.1 responsibilities: `ValidateWorkspace` and
  `ResolveSelectedImage` → `SelectedImageContext`). Both implemented in Infrastructure
  as pure, read-only logic (no DISM export/mount/apply/capture; no image modification).
- `IAppState.CurrentImageWorkspace` holds the durable selected-image workspace. The
  Image page builds/updates it when an edition is selected or changed, and clears it
  when a new ISO is inspected — so a stale selected index from a previous ISO can never
  survive. A new "Selected image" section shows Edition / Index / Image / Architecture /
  Build / Status / Source (original `.iso`); no temporary mount drive is ever shown.
- 30 new automated tests (Core 5 + App 25) cover valid WIM/ESD workspace creation,
  original-ISO source, relative path, no temp-drive persistence, preserved index/
  edition/metadata, NotReady (no selection / failed metadata / missing ISO / unknown
  type), Invalid (selected index not in metadata), edition change, new-ISO reset,
  failed inspection, Home consistency, and `IWimService` validation/resolve. All prior
  Step 2.1/2.2 tests (mount/dismount, ADR-015 cleanup) are retained. Total 92/92 pass
  (Core 12, App 80), 0 errors, 0 warnings.
- ADR-017 records that durable descriptors store ISO path + relative install-image
  path + selected index and never persist temporary mounted drive letters.

### Added (Phase 3 Step 3.2 — offline WIM servicing lifecycle)
- Introduces the durable offline servicing foundation for Phase 3. A new
  `ImageServicingWorkspace` descriptor captures the selected edition's source
  identifiers (mirroring `ImageWorkspace`) plus WinForge-owned servicing state:
  `WorkingDirectory` (`%LOCALAPPDATA%\WinForge\Workspaces\<id>`), `WorkingImagePath`
  (`…\image\install.wim`), `MountDirectory` (`…\mount`), `WorkingImageType` (always
  WIM), `WorkingIndex` (always 1), and a `ServicingWorkspaceState` lifecycle
  (`NotPrepared`/`Preparing`/`Prepared`/`Mounting`/`Mounted`/`Unmounting`/`Completed`/`Failed`)
  with a `ServicingHealth` classification and a `ServicingResult` outcome.
- Core contract `IImageServicingService` (`PrepareWorkingImageAsync` /
  `MountAsync` / `UnmountDiscardAsync` / `ValidateServicingWorkspaceAsync`) and
  Infrastructure `ImageServicingService` (DISM `/Export-Image` source index N →
  standalone WIM index 1, `/Mount-Image` working image only, `/Unmount-Image /Discard`,
  `/Get-MountedImageInfo` registration verification). The original ISO and its
  `install.wim`/`install.esd` are never modified — export reads a transient
  read-only ISO mount that is always released; the working image lives under a
  WinForge-owned workspace.
- `IWorkspacePathProvider` addresses workspaces by a safe id segment (no path
  separators can escape the folder); `IWorkspaceSafeDelete` proves a target is
  strictly inside the workspace before any deletion and refuses drive/profile/repo
  roots. Working-image post-export validation uses the per-index
  `/Get-ImageInfo /Index:1` detail query so edition/architecture/build are checked.
- `IAppState.CurrentServicingWorkspace` + `ImageViewModel` prepare/mount/unmount
  commands with state-aware `Can*` guards. An active mount REFUSES ISO re-inspection
  and edition re-selection (explanatory `BlockedMessage`) instead of destroying the
  session. A new Image page "Working image" section shows status, source
  edition/index, working image, working directory, mount directory, and any error.
- 35 new automated tests (Core 10 + App 25): model/state, WIM+ESD export, post-export
  validation (success, non-zero exit, edition/arch mismatch, missing source), mount
  guards (not-prepared, missing image, DISM-success-but-not-registered), unmount
  (discard, no-op when not mounted, DISM failure), stale/missing/invalid recovery,
  and ViewModel guards. Total **127/127 pass (Core 21, App 106), 0 errors, 0 warnings**.

### Fixed (Phase 3 Step 3.2 — prepare-command enablement)
- Real-desktop defect: the "Prepare working image" command stayed greyed out even
  when a Ready selected image existed. `AsyncRelayCommand` only re-evaluates
  `CanExecute` when it raises `CanExecuteChanged`; it does **not** hook
  `CommandManager.RequerySuggested`. The ViewModel raised `PropertyChanged` on the
  `Can*` properties, but a Button bound to the command only listens to the command's
  `CanExecuteChanged` event — so the cached disabled state was never refreshed after
  ISO inspection + edition selection flipped `CanPrepareWorkingImage` to true. The
  `CanPrepareWorkingImage` property was already correct; the command notification
  path was missing. Fix: `ImageViewModel.Refresh()` now raises `CanExecuteChanged`
  on the prepare / mount / unmount commands after every state transition (inspection,
  edition selection, `CurrentImageWorkspace` replacement, servicing state changes).
- Aligned `CanPrepareWorkingImage` to the approved state machine: a `Prepared`
  session now disables Prepare (re-prepare is no longer allowed; Mount is the next
  step). `Mounted` already disabled it. `Failed`/`NotPrepared`/`null` still allow it.
- 6 new regression tests drive the real sequence (no image → inspect → select
  edition → Ready) and assert the command's `CanExecuteChanged` actually fires — the
  exact mechanism WPF uses to enable the button — plus edition-change refresh,
  new-ISO invalidation, busy-disable, and Prepared/Mounted disable. Total
  **133/133 pass (Core 21, App 112), 0 errors, 0 warnings** (Release).

### Status (Phase 3 Step 3.2 — desktop validation PASSED)
- Step 3.2 real-desktop validation **PASSED** (2026-08-09) on a real Windows 11 25H2
  (Chinese Simplified, x64, Consumer Editions, `install.wim`) ISO:
  `Win11_25H2_Chinese_Simplified_x64_v2.iso`, selected edition `Windows 11 专业版`
  (source index 4). Observed successful lifecycle:
  - ISO inspection succeeded; Selected Image became **Ready**.
  - Prepare working image succeeded; source index 4 was exported into an isolated
    single-index working `install.wim` (working index = 1) under
    `%LOCALAPPDATA%\WinForge\Workspaces\<workspace-id>\` — the **isolated working-WIM
    strategy is validated**.
  - The original ISO / original `install.wim` were **not modified**.
  - Mount image succeeded; the real mount directory contained the genuine Windows
    filesystem (`Windows`, `Program Files`, `Program Files (x86)`, `Users`, `PerfLogs`,
    `etc.`); DISM confirmed the image was genuinely mounted.
  - Unmount & discard changes succeeded; UI returned to **Prepared**.
  - `dism /English /Get-MountedWimInfo` reported **"No mounted images found."** — the
    mount directory was empty after unmount; **no orphaned active mount remained**.
  - The repeated Mount → Unmount lifecycle passed.
  - The mounted-state source/edition switching safety guard passed (an active mount
    refuses ISO re-inspection and edition re-selection).
- Step 3.2 = **COMPLETED**; Desktop Validation = **PASSED**; Step 3.3 = **NOT STARTED**.
  No package/component/tweak/customization work was implemented. Total **133/133 pass
  (Core 21, App 112), 0 errors, 0 warnings** (Release).

### Changed (Phase 3 Step 3.1 — application elevation)
- `WinForge.App` now declares `requestedExecutionLevel level="requireAdministrator"`
  (uiAccess false) in its embedded application manifest (`src/WinForge.App/app.manifest`,
  wired via `<ApplicationManifest>` in `WinForge.App.csproj`). A normal launch now
  triggers the Windows UAC prompt for administrator rights. This is declarative only —
  no self-elevation process spawn, PowerShell, or UAC suppression is used (ADR-018).
- Reason (real desktop validation, 2026-08-09): the Phase 2 DISM image enumeration
  (`dism.exe /Get-ImageInfo`) fails with **DISM exit code 740** (ERROR_ELEVATION_REQUIRED)
  when `WinForge.App.exe` is launched without elevation; the same executable run as
  Administrator succeeds. Elevation is therefore required for ISO inspection and any
  future DISM-backed operation.

### Status (Phase 3 Step 3.1 — desktop validation PASSED)
- Step 3.1 real-desktop validation PASSED (2026-08-09) on a real Windows 11 25H2
  (Chinese Simplified, x64, Consumer Editions, `install.wim`) ISO: `install.wim`
  detected, 6 editions enumerated (Windows Version `10.0.26200`, Build `26200`,
  Architecture `x64`, Language `zh-CN`), edition `Windows 11 专业版` (index 4) selected →
  `ImageWorkspace` status **Ready**; Source remained the original `.iso`; the UI showed
  `Image: install.wim` / `Source: <iso filename>` with **no temporary mount drive**
  displayed or persisted (ADR-017 confirmed). The durable `ImageWorkspace.ImageRelativePath`
  is the full normalized `sources\install.wim` (the UI intentionally presents only the
  filename). Added 2 regression tests (Core 1, App 1): `AppManifestElevationTests` asserts
  the source manifest requires `requireAdministrator`; a Core test asserts the model retains
  the full relative path. Step 3.1 = **COMPLETED** (not yet merged to `main`); Step 3.2
  NOT STARTED; no WIM mount/export/modification implemented.

### Added (Phase 2 Step 2.2 — Windows image metadata & editions)
- Step 2.2 reads read-only metadata from the install image (`install.wim` /
  `install.esd`) found under the mounted ISO: WIM/ESD indexes, edition name,
  edition description, architecture, Windows version, Windows build, edition ID,
  installation type, and languages — without mounting, modifying, or servicing
  the image.
- Core contract `IWindowsImageMetadataService` returning a structured
  `WindowsImageMetadataResult` (top-level version/build/architecture/languages
  plus a per-index `WindowsEditionInfo` list). All fields are nullable so data
  WinForge cannot reliably read stays `null` rather than being guessed; the UI
  decides between "Not detected" and "Mixed".
- Infrastructure `WindowsImageMetadataService` queries the image read-only with
  `dism.exe /Get-ImageInfo /ImageFile:"<path>" /English` in **two stages**: one
  enumeration query (no `/Index`) that reliably returns each index's `Index` /
  `Name` / `Description`, followed by one per-index detail query
  (`/Get-ImageInfo /ImageFile:"<path>" /Index:<n> /English`) for **every**
  enumerated index that supplies `Architecture`, `Version`/`Build`, `Edition Id`,
  `Installation`, and `Languages`/`Default Language`. The two parses are split to
  match (`DismImageInfoParser.ParseImageList` for enumeration,
  `DismImageInfoParser.ParseImageDetails` for per-index detail) and merged by
  index.
  Both are key-based, tolerant of unknown / future / reordered fields, and never
  parse by fixed column position.
- `IProcessRunner` abstraction (Core) with `ProcessRequest` / `ProcessResult`
  DTOs and an Infrastructure `WindowsProcessRunner` (`System.Diagnostics.Process`)
  implementation; keeps Core free of any `Process` dependency and makes DISM
  invocation fully testable with a fake.
- High-level inspection session in `WindowsIsoInspectionService` now mounts the
  ISO, inspects the layout (Step 2.1), reads the install-image metadata (Step
  2.2) **while the ISO is still mounted**, then always dismounts — preserving the
  ADR-015 cancellation-safe cleanup. The ViewModel never coordinates mount
  lifecycle.
- Image page shows a "Windows information" section (Windows Version, Build,
  Architecture, Language) and an editions `ListView`; selecting an edition writes
  `IAppState.SelectedEdition` (status only — no extraction/mount/modify), and the
  Home page "Windows Edition" tile reflects the selection.
- 16 new automated tests (parser, service via fake process runner, orchestrator
  lifecycle, ViewModel/Home selection) covering single/multi-index WIM, ESD,
  Home+Pro enumeration, architecture/version/build/language parsing, malformed
  and empty output, non-zero DISM exit, cancellation, unknown/reordered fields,
  edition selection, and guaranteed dismount after metadata failure. All Step
  2.1 tests are retained.

### Fixed (Phase 2 Step 2.2 — two-stage metadata query correctness)
- Real-DISM correctness gap: a single `dism.exe /Get-ImageInfo` (no `/Index`) only
  reliably reports per-index `Index` / `Name` / `Description` (and `Size`). The
  detailed fields — `Architecture`, `Version`/`Build`, `Edition Id`,
  `Installation`, `Languages`, `Default Language` — are returned **only** by a
  per-index query (`/Get-ImageInfo /ImageFile:"..." /Index:<n> /English`). Step 2.2
  now runs the enumeration query once, then one detail query for **every**
  enumerated index (index numbers are not assumed sequential and are not assumed
  to map to a specific edition such as Home/Pro), and merges the results by index.
  `DismImageInfoParser` was split into `ParseImageList` (enumeration fields only)
  and `ParseImageDetails` (full per-index detail). Failure semantics: if the
  enumeration query fails the whole result is `Failed`; if a single per-index
  detail query fails, that edition keeps its enumerated data, its detailed fields
  stay `null`, and `WindowsEditionInfo.DetailStatus` records `Failed` (logged, not
  shown raw) so the UI never silently pretends full metadata arrived. Added a
  per-edition `DetailStatus` / `DetailErrorMessage` and a `DefaultLanguage` field.
  Version/build is parsed structurally and never fabricated (no invented
  servicing/UBR segment). The guaranteed dismount (ADR-015) and read-only safety
  are unchanged. Tests were expanded with realistic two-stage fixtures and now
  assert the exact command sequence (enumeration without `/Index`, then one
  `/Index:n` per index) via a recording fake process runner.

### Fixed (Phase 2 Step 2.2 — DISM Error 87: use /Get-ImageInfo)
- Real desktop validation (2026-08-08): initial Step 2.2 desktop validation
  exposed DISM exit code 87 ("The parameter is incorrect") because the
  implementation invoked `dism.exe /English /Get-WimInfo /ImageFile:"..."`, an
  incorrect command combination for the Windows 11 DISM command line. Corrected to
  the documented Windows 11 syntax: `dism.exe /Get-ImageInfo /ImageFile:"<path>" /English`
  for enumeration and `dism.exe /Get-ImageInfo /ImageFile:"<path>" /Index:<n> /English`
  for the per-index detail query. `/ImageFile:` is kept (not changed to
  `/WimFile`). The two-stage design (enumeration → collect indexes → per-index
  detail → merge by index) is retained. The parser type was renamed
  `DismWimInfoParser` → `DismImageInfoParser` and all references/tests updated. A
  regression test asserts production arguments never contain `/Get-WimInfo`. Step
  2.2 remains IMPLEMENTED / PENDING REAL DESKTOP RE-VALIDATION — NOT COMPLETED.

### Fixed (Phase 2 Step 2.2 — DISM language footer parsing defect)
- Real desktop validation (2026-08-08) on the Windows 11 25H2 zh-CN x64 Consumer
  `install.wim` **succeeded** for the full two-stage flow: `/Get-ImageInfo`
  enumeration, 6 real indexes (家庭版/家庭单语言版/教育版/专业版/专业教育版/
  专业工作站版), per-index detailed queries, Version `10.0.26200`, Build `26200`,
  Architecture `x64`, and guaranteed ISO dismount. It exposed one parser defect:
  because `DismImageInfoParser` blindly took the first whitespace token of any
  non-key line inside the `Languages` section, DISM's trailing footer
  `The operation completed successfully.` was parsed as the language `The`
  (UI showed `zh-CN, The`). `ExtractLanguage` was replaced by
  `TryNormalizeLanguageTag`, a conservative BCP-47-like validator: only a 2–3
  letter primary subtag followed by at least one hyphenated region/script/variant
  subtag (`en-US`, `zh-CN`, `pt-BR`, `sr-Latn-RS`) is accepted; a trailing
  `(Default)` annotation is stripped before validation. The `Languages` section now
  **terminates** as soon as a non-language, non-blank, non-key line is seen, so
  future DISM footer prose can never leak in. Regression tests assert
  `Languages == ["zh-CN"]` (not `["zh-CN","The"]`) and `["en-US","fr-CA"]` against
  the exact real-footer shape, plus rejection of arbitrary prose. Step 2.2 remains
  IMPLEMENTED / PENDING FINAL LANGUAGE-PARSER RE-VALIDATION — NOT COMPLETED.

### Status (Phase 2 Step 2.2)
- Step 2.2 has been accepted and merged to `main` (2026-08-08). It passes the
  automated test suite (0 errors, 0 warnings, 60/60 tests executed and passing:
  Core 6, App 54). A real Windows desktop run on the Windows 11 25H2 zh-CN x64
  Consumer `install.wim` validated the full two-stage `/Get-ImageInfo` flow: ISO
  mount, `install.wim` detection, enumeration of 6 indexes, per-index detail
  queries, Windows Version `10.0.26200`, Build `26200`, Architecture `x64`, Language
  `zh-CN` (footer prose correctly rejected), localized Chinese edition names, and
  guaranteed dismount. Both real-desktop findings (DISM exit 87, language footer
  `The`) were fixed and revalidated.   `v0.1.0-alpha` is unchanged; `feature/iso-inspection` is retained (history).
  Step 2.2 is the final step of Phase 2 — there is no Step 2.3; Phase 3 — WIM
  Engine remains NOT STARTED.

### Accepted (Phase 2 Step 2.2 — Windows image metadata & editions)
- Phase 2 Step 2.2 accepted and merged to `main` on 2026-08-08 via a `--no-ff`
  merge commit (`feature/iso-inspection`, commits `a8f27ef`, `ec3df91`,
  `2b5f848`, `929d399`). Pre-merge and post-merge `dotnet build` / `dotnet test`
  verified clean: 0 errors, 0 warnings, 60/60 tests passing (Core 6, App 54).
- Real Windows 11 25H2 (Chinese Simplified, x64, Consumer Editions, `install.wim`)
  desktop validation PASSED: ISO mounted, `install.wim` detected, `/Get-ImageInfo`
  enumeration succeeded (6 indexes: 家庭版/家庭单语言版/教育版/专业版/专业教育版/
  专业工作站版), per-index detailed queries succeeded, Windows Version
  `10.0.26200`, Build `26200`, Architecture `x64`, Language `zh-CN`, localized
  Chinese edition names populated, ISO dismounted. The previous language bug
  (`zh-CN, The`) is fixed (UI shows `zh-CN` only).
- Both real-desktop findings from the metadata-inspection validation are fixed and
  revalidated: (1) initial `/Get-WimInfo` caused DISM exit code 87 → corrected to
  the documented `dism.exe /Get-ImageInfo /ImageFile:"<path>" /English` (enumeration)
  and `... /Index:<n> /English` (per-index detail); (2) the trailing DISM footer
  `The operation completed successfully.` was parsed as language `The` → fixed via
  `TryNormalizeLanguageTag` (BCP-47-like validator, clean `Languages` section
  termination). Added regression tests for both.
- Delivered capabilities (Step 2.2): read-only WIM/ESD metadata (index, edition
  name/description, architecture, Windows version, build, edition ID, installation
  type, languages, default language) via two-stage `dism.exe /Get-ImageInfo`
  (read-only, no WIM mount/servicing); `IProcessRunner` abstraction keeping Core
  free of `Process`; combined mount→layout→metadata→dismount session preserving
  ADR-015; Image page "Windows information" + editions list; edition selection →
  `IAppState.SelectedEdition`. 60 automated tests (parser, service via fake process
  runner, orchestrator lifecycle, ViewModel/Home selection).
- Step 2.2 does not extract, mount, modify, or service the image. `v0.1.0-alpha`
  remains the current release tag; the next tag (`v0.2.0-alpha`) is NOT created in
  the Phase 2 closure task (tagging deferred pending release policy). ESD
  (`install.esd`) metadata parsing is implemented but still `Untested` on a real
  desktop.

### Accepted (Phase 2 — ISO Inspection formal closure)
- Phase 2 — ISO Inspection is formally **COMPLETED** (2026-08-08). It consists of
  exactly two steps — Step 2.1 (read-only ISO layout inspection) and Step 2.2
  (Windows image metadata & edition inspection) — both accepted and merged to
  `main`. No Step 2.3 is defined in ROADMAP.md and none is invented.
- Phase 2 acceptance criteria satisfied: an official Microsoft Windows 11 ISO was
  inspected non-destructively; image type (`install.wim`) detected; editions
  enumerated (6 indexes on the validated 25H2 zh-CN x64 Consumer ISO); architecture
  detected (`x64`); languages detected (`zh-CN`, with DISM footer prose rejected via
  `TryNormalizeLanguageTag`); and real Windows 11 25H2 desktop validation PASSED.
- Both real-desktop findings from the Step 2.2 validation are fixed and revalidated:
  (1) DISM exit code 87 from `/Get-WimInfo` → corrected to documented
  `dism.exe /Get-ImageInfo`; (2) trailing DISM footer `The operation completed
  successfully.` parsed as language `The` → fixed via `TryNormalizeLanguageTag`.
- `v0.1.0-alpha` remains unchanged; `v0.2.0-alpha` is NOT created in this closure
  (tagging deferred pending release policy). Next phase: Phase 3 — WIM Engine
  (NOT STARTED). `feature/iso-inspection` is retained (history). Automated tests:
  60/60 passing (Core 6, App 54), 0 errors, 0 warnings.

## [0.1.0-alpha] — 2026-08-08

### Added
- Project governance documents: README, ROADMAP, PROJECT_STATUS, ARCHITECTURE,
  DECISIONS, AGENTS, CHANGELOG.
- Product, testing, and Windows compatibility documentation under `docs/`.
- Phase 0 (Project Governance) completed; roadmap defined through Phase 12.
- Architecture decision records ADR-001 … ADR-009.
- **Phase 1 — Application Foundation:**
  - `WinForge.sln` with `WinForge.App` (WPF), `WinForge.Core`,
    `WinForge.Infrastructure`, and `WinForge.Core.Tests` (plus
    `WinForge.App.Tests` for headless boot verification).
  - WPF navigation shell: left rail with Home, Image, Components, Experience,
    Privacy, System, Build, Logs, Settings; Home and Image implemented, the rest
    show a "Coming soon" page.
  - MVVM infrastructure (`ViewModelBase`, `RelayCommand`, `AsyncRelayCommand`),
    `INavigationService` / `NavigationService`, and `IAppState` / `AppState`.
  - Core domain models: `WindowsImageInfo`, `WindowsEditionInfo`, `BuildPlan`
    (skeletons; no real ISO inspection yet).
  - Logging abstraction (`ILoggerService`, `LogEntry`, `LogLevel`) with an
    in-memory implementation in Infrastructure; live Logs page.
  - Dependency injection via `Microsoft.Extensions.DependencyInjection`.
  - Process-wide error handling (`AppDomain.UnhandledException`,
    `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`).
  - `*.iso` file picker that validates existence and records the path in
    `AppState` (no mount / DISM / inspection).

### Notes
- Phase 1 is foundation only. No DISM, ISO mounting, WIM handling, debloating,
  privacy, or build behavior is implemented (those are future phases).
- The WPF window must be rendered on a Windows desktop; headless environments
  verify startup wiring via `WinForge.App.Tests`.

### Fixed (merge-readiness)
- Logging thread-safety: `InMemoryLoggerService` (Infrastructure) now stores
  entries in a lock-guarded `List<LogEntry>` and returns a snapshot from
  `Entries`; it no longer uses a WPF `ObservableCollection` and has no Dispatcher
  dependency, so it is safe to call from background threads/Tasks. `LogsViewModel`
  keeps the WPF `ObservableCollection` and marshals `EntryAdded` to the UI thread
  via `SynchronizationContext` (ADR-014). Added `LoggerThreadSafetyTests`
  (concurrent logging, event delivery, capacity under load).
- Read-only binding crash on the Image page: the `TextBox` showing
  `ImageViewModel.FileDisplay` used the default `TwoWay` mode; because
  `FileDisplay` is getter-only, WPF threw at runtime. The binding is now
  `Mode=OneWay`. Audited all Phase 1 XAML — this was the only default-TwoWay
  control bound to a read-only property. Added `ImageBindingRegressionTests`
  (read-only property + `Mode=OneWay` XAML assertion, no display device needed).

### Fixed (merge-readiness, Phase 2 Step 2.1)
- Cancellation-safe ISO cleanup: `WindowsIsoInspectionService` now tracks whether
  a mount was attempted and always runs a best-effort `Dismount-DiskImage` in its
  `finally` block using `CancellationToken.None`. An ISO can no longer be left
  mounted when inspection is cancelled or fails before the mounted root is
  obtained (ADR-015). `OperationCanceledException` is re-thrown so cancellation
  is never swallowed by successful cleanup.
- `WindowsIsoMountService.DismountAsync` is safe when the image is not mounted
  (`-ErrorAction SilentlyContinue`), so best-effort cleanup after a cancelled
  mount never surfaces a spurious error.
- User-facing error sanitization: `IsoInspectionResult.ErrorMessage` shown by the
  UI is now a generic, friendly string; raw PowerShell errors, HRESULT codes,
  command text, and internal exception detail are retained only in `ILoggerService`.
- Added 4 cancellation/cleanup tests (`IsoInspectionTests`) for: cancellation
  after mount still attempts dismount, cleanup uses a non-cancellable token,
  inspection failure after mount still dismounts, and successful inspection
  dismounts exactly once.
- Fixed `ImageBindingRegressionTests` XAML path resolution to use the compile-time
  source path (`[CallerFilePath]`), so it executes correctly regardless of build
  output redirection.

### Accepted (Phase 1 formal acceptance)
- Phase 1 accepted and merged to `main` on 2026-08-08 (annotated tag
  `v0.1.0-alpha`).
- Validated scope: WPF application shell, MVVM infrastructure, dependency
  injection, navigation service, logging foundation, `AppState`, and ISO file
  selection.
- Merge-readiness fixes included in the accepted baseline: thread-safe logging
  (ADR-014) and read-only WPF binding fix on the Image page.
- Verification: real Windows desktop GUI validation confirmed by the user, in
  addition to the existing headless automated test suite (`WinForge.App.Tests`).
- `dotnet build` / `dotnet test` verified clean: 0 errors, 0 warnings, 10/10
  tests passing. Phase 2 (ISO Inspection) remains NOT STARTED.

### Accepted (Phase 2 Step 2.1 — read-only ISO layout inspection)
- Phase 2 Step 2.1 accepted and merged to `main` on 2026-08-08 via a `--no-ff`
  merge commit (Step 2.1 commits `57c975e`, `66978df` from
  `feature/iso-inspection`). Pre-merge and post-merge `dotnet build` /
  `dotnet test` verified clean: 0 errors, 0 warnings, 29/29 tests passing.
- Delivered capabilities (Step 2.1): ISO native file picker; file basic
  validation; read-only ISO mount; Windows ISO candidate detection
  (`\boot` + `\sources` + `install.wim`/`install.esd`); WIM/ESD type
  recognition; async inspection; UI busy/error state; logging; guaranteed
  dismount; cancellation-safe cleanup (ADR-015); friendly UI error messages
  with raw technical detail retained only in `ILoggerService`; fake-mount
  unit tests; no DISM servicing, no registry, no ISO modification.
- Real Windows 11 25H2 (Chinese Simplified, x64, Consumer ISO, `install.wim`)
  desktop validation completed: the user confirmed the full mount → inspect →
  dismount cycle via application logs. Headless automated tests also pass,
  including cancellation-safe cleanup.
- Step 2.1 does **not** read WIM index, edition, Windows version, build,
  architecture, or language — those belong to Step 2.2. `v0.1.0-alpha` is
  unchanged; the next tag (`v0.2.0-alpha`) is deferred until Phase 2 completes.
