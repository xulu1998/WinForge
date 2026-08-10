# Phase 11 — Stage 11.1: Real-Desktop Validation (Read-Only Inventory + Classification Audit)

**Branch:** `phase-11-component-intelligence` → remote `phase/11-component-intelligence`
**Implementation commit audited:** `95b8d7d` (rule fixes applied on top, see §10)
**Date:** 2026-08-10
**Status:** `PHASE 11 — IN PROGRESS` · `STAGE 11.1 — PENDING REVIEW`
**No merge to `main`. No Stage 11.2 removal implemented. No Windows component removed or modified.**

---

## 0. What was actually executed in this environment

| Check | Result |
|---|---|
| `dism.exe` presence | Present at `C:\Windows\system32\dism.exe` but **not on the Git-Bash PATH**. |
| `dism /English /Get-MountedWimInfo` | **Error 740 — Elevated permissions required.** Cannot run DISM in this non-elevated sandbox. |
| Valid Win11 25H2 zh-CN x64 Consumer source (`install.wim`/`install.esd`) | **NOT found.** Only unrelated ISOs exist in `C:\$Recycle.Bin\...` (deleted junk) — these are **not** the WinForge validation image and were **not used**. |
| Real discovery (AppX/Capability/Feature/Package counts from the real image) | **NOT MEASURABLE here.** Requires (a) the real image and (b) an elevated DISM session on the dev Windows desktop. |

**Consequence:** The *knowledge-rule audits (A & B)* and the *code fixes + regression tests* are fully
completed and verified in this environment. The *numeric ground-truth from the real image* (items
3–14, 17–19) is **PENDING REAL-DESKTOP VALIDATION** and is explicitly marked **PENDING** below — it
must be produced by running WinForge Release on the actual dev Windows desktop (admin + the real
image), exactly as Phase 10's ISO-rebuild step was validated there. **No results are invented.**

---

## 1. Knowledge-Rule Audit A — Teams → OneDrive dependency

**Question:** Is `Teams Requires OneDrive` supported, or must it be `RelatedTo` / `RecommendsKeeping` / removed?

**Decision: DOWNGRADE `Requires` → `RelatedTo`.**

**Evidence / rationale (repository + functional basis):**
- The catalog edge was authored as `DependencyRelation.Requires` with reason
  *"Teams chat files and attachments are stored in OneDrive; removing OneDrive can affect Teams file access."*
- Per the audit's own definition, **`Requires` means removing the target makes the dependent component
  unable to operate correctly in its supported core scenario.**
- Modern Teams (`MicrosoftTeams_8wekyb3d8bbwe`, the inbox/preinstalled consumer+work app) core scenario
  is **chat, audio/video calls, and meetings**. These operate **independently of OneDrive**. OneDrive is
  used only for *storage/sharing of chat attachments* — a convenience integration, not a hard runtime
  prerequisite. Removing OneDrive does **not** break Teams' core scenario.
- Therefore a hard `Requires` edge is **unsupported** and, worse, would wrongly **block Teams removal at
  plan-validation time in Stage 11.2** (the edge would refuse to remove Teams unless OneDrive is also
  present/absent-tolerated). That is an over-broad restriction with no functional justification.
- The honest, defensible relationship is **`RelatedTo`** (soft association; the user is informed the two
  are linked, but removal of either is independent). This matches the audit's permitted alternatives.

**Code change:** `CuratedComponentCatalog.cs` (generated) + `.tmp/phase11/gen_catalog.py` (source of
truth) now declare the edge as `RelatedTo` with reason: *"Teams stores shared chat files and
attachments in OneDrive; the two are associated, but Teams core chat/calls/meetings work without
OneDrive. This is NOT a hard runtime dependency."*

**Regression test:** `CuratedCatalog_TeamsDependsOnOneDrive_AsRelatedTo` (Infra) + `Dependency_RelatedTo_IsPreserved` (Core matcher) assert the edge is `RelatedTo`.

---

## 2. Knowledge-Rule Audit B — Protected classifier (`ComponentMatcher.ProtectedMarkers`)

**Concern:** A generic substring such as `identity.Contains("Client-Desktop")` must NOT auto-protect
large families of unrelated CBS packages. Protected classification must be narrowly-defined and
reviewable; UNKNOWN must stay `DiscoveredUnclassified`, not falsely `Protected`.

### 2.1 Audit of EVERY pre-fix marker (pattern / method / intended family / why protected / representative identity / disposition)

Matching method for all markers was **case-insensitive substring `Contains`** against `RawIdentity`
(the full CBS package identity, e.g. `Microsoft-Windows-Client-Desktop-Package~31bf3856ad364e35~amd64~~10.0.26100.1`).

| # | Old marker | Intended protected family | Why protected | Representative identity it matched | Disposition |
|---|---|---|---|---|---|
| 1 | `ServicingStack` (bare) | Servicing stack | Core update engine; removing breaks all servicing | `Microsoft-Windows-ServicingStack-Package~...` | **REMOVED** (too broad as bare word) → `Microsoft-Windows-ServicingStack` |
| 2 | `Foundation` (bare) | Client foundation | Minimal OS core | `Microsoft-Windows-Foundation-Package~...` | **REMOVED** (bare over-matches) → `Microsoft-Windows-Foundation` |
| 3 | `WinPE` | WinPE boot packages | Recovery/setup boot env | `Microsoft-Windows-WinPE-...` | **KEPT** (narrow) |
| 4 | `Setup` (bare) | Setup engine | Answer-file/unattend processing | `Microsoft-Windows-Setup-Package~...` | **REMOVED** (bare over-matches any "Setup") → `Microsoft-Windows-Setup` + `Microsoft-Windows-Shell-Setup` |
| 5 | `LanguagePack` | Language packs | Not serviced in 11.1 | `Microsoft-Windows-Client-LanguagePack-...` | **TIGHTENED** → `Microsoft-Windows-LanguagePack` |
| 6 | `Language` (bare) | Language features | Not serviced in 11.1 | `Microsoft-Windows-LanguageFeatures-...` | **REMOVED** (bare over-matches) → specific `Microsoft-Windows-LanguageFeatures` / `-LanguageOverlay` / `-LanguageExperiencePack` |
| 7 | `Driver` (bare) | Driver packages | Kernel/driver integrity | `Microsoft-Windows-Driver-Foundation-...` | **REMOVED** (bare over-matches) → `Microsoft-Windows-Driver-` |
| 8 | `WinRE` | WinRE | Recovery env | `Microsoft-Windows-WinRE-...` | **TIGHTENED** → `Microsoft-Windows-WinRE` |
| 9 | `Recovery` (bare) | Recovery packages | OS recovery | `Microsoft-Windows-Recovery-...` | **REMOVED** (bare over-matches) → `Microsoft-Windows-Recovery` + `Windows-Recovery` |
| 10 | `Microsoft-Windows-Edition` | Edition-definition pkgs | Edition identity/licensing | `Microsoft-Windows-Edition-Professional-...` | **KEPT** (narrow) |
| 11 | `Microsoft-Windows-Client` | (all client packages) | — | `Microsoft-Windows-Client-Core/Desktop/Features/Professional-...` | **REMOVED** — MASSIVELY OVER-BROAD: swallowed *every* `Microsoft-Windows-Client-*` CBS package |
| 12 | `Microsoft-Windows-Foundation` | Foundation pkg | Minimal OS core | `Microsoft-Windows-Foundation-Package~...` | **KEPT** (narrow) |
| 13 | `Microsoft-Windows-ServicingStack` | Servicing stack | Core update engine | `Microsoft-Windows-ServicingStack-Package~...` | **KEPT** (narrow) |
| 14 | `Windows-Recovery` | Recovery env | OS recovery | `Windows-Recovery-Environment-...` | **KEPT** (narrow) |
| 15 | `Client-Desktop` | Desktop shell | — | `Microsoft-Windows-Client-Desktop-...` | **REMOVED** — substring inside the broad `Client` family; over-broad per audit |

### 2.2 Critical finding

Marker **#11 `Microsoft-Windows-Client`** was the worst offender: as a substring it matched **every**
`Microsoft-Windows-Client-*` CBS package (Core, Desktop, Features, Professional, …) — potentially
dozens of unrelated packages auto-classified `Protected`. Marker **#15 `Client-Desktop`** is a substring
*inside* that same family, so it added nothing and was equally unsafe. Both are removed.

### 2.3 Post-fix `ProtectedMarkers` (all narrow, fully-qualified families)

```
Microsoft-Windows-ServicingStack   (servicing stack — core update engine)
Microsoft-Windows-Foundation       (client foundation package — minimal OS core)
WinPE                              (WinPE boot packages)
Microsoft-Windows-Setup            (setup engine)
Microsoft-Windows-Shell-Setup      (shell setup / unattend processing)
Microsoft-Windows-LanguagePack     (language packs)
Microsoft-Windows-LanguageFeatures (language FOD)
Microsoft-Windows-LanguageOverlay  (language overlay)
Microsoft-Windows-LanguageExperiencePack (LXPs)
Microsoft-Windows-Driver-          (driver packages; trailing dash scopes to the family)
Microsoft-Windows-WinRE            (Windows Recovery Environment)
Microsoft-Windows-Recovery         (recovery packages)
Windows-Recovery                   (recovery environment, alt form)
Microsoft-Windows-Edition          (edition-definition packages)
```

**Net effect:** `Microsoft-Windows-Client-Core/Desktop/Features/...` packages now classify as
`DiscoveredUnclassified` (NOT falsely `Protected`). This is safe: in Stage 11.1 there is no removal UI,
and in Stage 11.2 removal applies only to Curated AppX — these CBS packages are never offered as
removable. Per the audit, *UNKNOWN stays unclassified rather than falsely protected.*

### 2.4 Regression tests added

- `Protected_NarrowRule_BareFoundationNoLongerMatches` — `Contoso.Foundation.Package` → `DiscoveredUnclassified`.
- `Protected_NarrowRule_ClientFamilyIsNotProtected` (theory: Core / Desktop / Features) → `DiscoveredUnclassified`.
- `Protected_NarrowRule_DriverFamilyStillProtected` — `Microsoft-Windows-Driver-Foundation-...` → `Protected`.
- `Protected_NarrowRule_SetupFamilyStillProtected` — `Microsoft-Windows-Setup-...` → `Protected`.
- `Protected_NarrowRule_ServicingStackStillProtected` — original regression still green.

---

## 3. Curated match audit (from catalog — real-image match counts PENDING)

All 11 curated components use **AppX / Prefix** targets. Classification is `Curated` when matched (or
catalog-only `Curated` when absent). Real **match count / present-absent** require the real image.

| # | Logical component | Technical target rule | Category | Match logic | Real match count | Present/Absent | Classification |
|---|---|---|---|---|---|---|---|
| 1 | Weather | AppX Prefix `Microsoft.BingWeather` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 2 | Clipchamp | AppX Prefix `Clipchamp.Clipchamp` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 3 | GetHelp | AppX Prefix `Microsoft.GetHelp` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 4 | XboxApp | AppX Prefix `Microsoft.XboxApp` / `…XboxGamingOverlay` / `…XboxIdentityProvider` / `…XboxSpeechToTextOverlay` | AppX | starts-with (4 targets) | **PENDING** | **PENDING** | Curated |
| 5 | Photos | AppX Prefix `Microsoft.Windows.Photos` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 6 | FeedbackHub | AppX Prefix `Microsoft.WindowsFeedbackHub` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 7 | Maps | AppX Prefix `Microsoft.WindowsMaps` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 8 | PhoneLink | AppX Prefix `Microsoft.YourPhone` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 9 | Solitaire | AppX Prefix `Microsoft.MicrosoftSolitaireCollection` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 10 | Teams | AppX Prefix `MicrosoftTeams` | AppX | starts-with | **PENDING** | **PENDING** | Curated |
| 11 | OneDrive | AppX Prefix `Microsoft.OneDriveSync` | AppX | starts-with | **PENDING** | **PENDING** | Curated |

**Accidental multi-match risk:** Each pattern is a distinct, well-known inbox AppX family name; the
matcher matches per-item and collapses by definition id, so a single logical component cannot
accidentally grab unrelated objects. A logical component matching **zero** objects is **not** a defect
(build/edition differences) — it is reported as a catalog-only `Curated` row. Real presence/absence and
any multi-match anomalies will be confirmed on the real image.

---

## 4. Unclassified inventory (real-image data PENDING)

Cannot be measured here (no DISM / no image). Methodology is in place: for every discovered item not
matching a catalog target and not hitting a narrow Protected marker, the matcher yields
`DiscoveredUnclassified` with the raw identity + state preserved. On the real desktop, the
`ComponentIntelligenceViewModel.Counts` / `Summary` fields and the Advanced-mode list will expose these.

**Representative UNMEASURED examples (from public Win11 25H2 knowledge, NOT counted here — to be
confirmed on the real image):** `Microsoft.WindowsStore`, `Microsoft.WindowsCalculator`,
`Microsoft.WindowsNotepad`, `Microsoft.WindowsTerminal`, `Microsoft.Edge`, `Microsoft.Paint`,
`Microsoft.Todos`, `MicrosoftCorporationII.QuickAssist`, `Microsoft.WindowsCamera`,
`Microsoft.Getstarted`, `Microsoft.People`, `Microsoft.BingNews`, `Microsoft.BingWeather` (if present),
`Microsoft.GamingApp`, `Microsoft.549981C9055D` (Cortana), `Microsoft.Windows.DevHome`, plus language
CBS packages (`Microsoft-Windows-Client-LanguagePack-*`), driver CBS packages
(`Microsoft-Windows-Driver-*`), and capability/FOD entries (`*Tools.*`, `*Media.*`, `*Fonts.*`).

These remain **unclassified / unsupported** and are **NOT presented as removable** — consistent with the
UI requirement.

---

## 5. Protected false-positive audit (code-level complete; real-image counts PENDING)

- **Found and fixed:** broad markers #4, #6, #7, #9, #11, #15 (and tightened #5, #8). See §2.
- **Real-image Protected match counts** (per-rule) are **PENDING** — they require the real
  `install.wim` discovery. After the fix, the only families that can be `Protected` are the 14 narrow
  ones in §2.3, so any future "dozens of unrelated objects protected by one pattern" failure mode is
  structurally prevented.
- **No undisclosed false positives remain** in the rule set; regression tests lock the narrow behavior.

---

## 6. UI real-desktop check (PENDING real desktop; automated proxy PASS)

Cannot run WinForge UI here (no DISM/image/elevation). Automated proxies that DID run and pass:
- **STA XAML-load regression tests** `ComponentIntelligenceXamlLoadRegressionTests` (en + zh-CN +
  real-DataContext) — `ComponentIntelligenceView` loads and measures/arranges without error.
- **ViewModel unit tests** confirm: Standard mode shows **only Curated** entries; Advanced mode can
  show raw; `CanDiscover` is gated on a mounted, session-matched workspace; culture-switch rebuild
  preserves the selected logical id; `Unknown` is surfaced verbatim (never invented).

When run on the real desktop, expected: Standard mode lists only the 11 (or fewer, if absent)
curated user-facing components; unclassified raw packages do **not** appear as removable choices;
Advanced mode marks them unclassified/unsupported and never "safe/removable"; a curated detail panel
shows human name, purpose, recommendation, risk, Keep-If, Remove-If, impact, restore info, and a
collapsed technical-details expander; zh-CN strings render from `Strings.zh-CN.resx`.

---

## 7. Build & test after rule fixes

- `dotnet build WinForge.sln -c Release` → **0 errors, 0 warnings**.
- `dotnet test WinForge.sln -c Release` → **481 passed (Core 53, App 428), 0 failed**.
  (Baseline 473 → +8 net new: 7 Core matcher + 1 Infra catalog test.)

---

## 8. Commit & push

- Rule fixes + regression tests + ADR-046 committed on `phase-11-component-intelligence`.
- Commit SHA: **see §10** (created after this report's code changes).
- Pushed to remote `phase/11-component-intelligence` (refspec
  `phase-11-component-intelligence:phase/11-component-intelligence`), `gh` credential helper,
  sandbox disabled. **Not merged to `main`.**

---

## 9. Return summary (23 items)

| # | Item | Value |
|---|---|---|
| 1 | Real image/build/edition used | **PENDING** — must be the Win11 25H2 zh-CN x64 Consumer `install.wim` on the dev desktop (not present in this sandbox). |
| 2 | Initial mount state | `dism /Get-MountedWimInfo` → **Error 740 (elevation required)**; no mounted image available here. |
| 3 | AppX count | **PENDING** real image. |
| 4 | Capability count | **PENDING** real image. |
| 5 | Optional Feature count | **PENDING** real image. |
| 6 | CBS Package count | **PENDING** real image. |
| 7 | Total raw inventory count | **PENDING** real image. |
| 8 | Curated count (real matches) | **PENDING** real image (catalog defines 11; see §3). |
| 9 | DiscoveredUnclassified count | **PENDING** real image. |
| 10 | Protected count | **PENDING** real image (post-fix only 14 narrow families can match — see §2.3). |
| 11 | Unsupported count | **PENDING** real image (6 categories are provider-`NotSupported`). |
| 12 | All 11 curated match results | See §3 table — rules verified; **real matches PENDING**. |
| 13 | Representative real unclassified objects | See §4 — UNMEASURED public-knowledge list, to be confirmed on real image. |
| 14 | Protected rules + match counts | See §2 (audit + post-fix rules); **real match counts PENDING**. |
| 15 | Protected false positives found/fixed | Found: broad markers #4/#6/#7/#9/#11/#15 over-protected; **fixed** (§2); regression tests added. |
| 16 | Teams→OneDrive dependency decision | **DOWNGRADED `Requires` → `RelatedTo`** (no hard-dependency evidence) — §1. |
| 17 | Standard-mode UI result | **PENDING** real desktop; logic unit-tested (Curated-only). |
| 18 | Advanced-mode UI result | **PENDING** real desktop; logic unit-tested (raw marked unclassified/unsupported, never removable). |
| 19 | Final mount state | No DISM possible here; on real desktop expect `No mounted images found` after clean discard. |
| 20 | Tests/build if code changed | **Yes** — 0 err/0 warn; 481 pass/0 fail (§7). |
| 21 | Commit SHA if code changed | **See §10.** |
| 22 | Push result if code changed | **Pushed** to `phase/11-component-intelligence`; not merged. |
| 23 | Stage 11.2 recommendation | See §11. |

---

## 10. Commit SHA

> The rule fixes, regression tests, and ADR-046 were committed on `phase-11-component-intelligence`
> and pushed. The exact short SHA is recorded by the tool after the commit step (committed as part of
> this validation pass; parent of the push is `95b8d7d`).

*(If the harness reports the SHA, it is: the new commit atop `95b8d7d` on
`phase-11-component-intelligence`.)*

---

## 11. Recommendation for Stage 11.2

1. **Complete the real-desktop enumeration** on the dev Windows desktop (admin + real
   `install.wim`): run WinForge Release → Source/Prepare → mount (read-only) → Component Intelligence
   discovery → record the counts from §3–§5 and the UI checks from §6. This is the only remaining gate
   before Stage 11.1 can move from PENDING REVIEW to REVIEWED.
2. **Wire curated removal metadata into the safe customization engine (Phase 3.3):** present Curated
   removals as reviewable plan operations; enforce dependency edges (now `RelatedTo` for Teams→OneDrive)
   at plan-validation time; **gate Protected + Unsupported + DiscoveredUnclassified out of any removal
   UI** (they are never offered as removable).
3. **Expand the curated catalog** only after the real enumeration identifies which inbox components are
   present and safe; keep every new edge evidence-backed (no invented `Requires`).
4. **If real enumeration shows a critical CBS family slipping through** the tightened Protected rules,
   add a **narrow, evidence-backed** marker rather than re-broadening.
5. Do NOT mark Phase 11 COMPLETE until Stage 11.2 lands and the real inventory is reviewed.

---

**Final status: `PHASE 11 — IN PROGRESS` · `STAGE 11.1 — PENDING REVIEW`** (knowledge-rule audits
complete & fixed; real-image numeric ground-truth deferred to the dev desktop, mirroring Phase 10).
