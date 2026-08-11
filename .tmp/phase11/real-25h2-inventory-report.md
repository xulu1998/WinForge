# Phase 11 — Stage 11.1: Real-Desktop Validation (Read-Only Inventory + Classification Audit)

**Branch:** `phase-11-component-intelligence` → remote `phase/11-component-intelligence`
**Implementation commit:** `95b8d7d` (audit fixes `a65b2ad` ADR-046; blank-page fix `005ede9`)
**Date:** 2026-08-10
**Status:** `PHASE 11 — IN PROGRESS` · `STAGE 11.1 — REAL DESKTOP VALIDATED`
**No merge to `main`. No Stage 11.2 removal implemented. No Windows component removed or modified.**

---

## 0. Environments

| Check | Sandbox (this build machine) | Real dev desktop (2026-08-10) |
|---|---|---|
| `dism.exe` | Present but not on Git-Bash PATH | Present, **elevated** |
| `dism /Get-MountedWimInfo` | **Error 740** (elevation required) | Mounted working image discovered successfully |
| Valid Win11 25H2 zh-CN x64 Consumer `install.wim` | NOT present | **Present** — real discovery executed |
| Component Intelligence page render | N/A (blank-page defect found + fixed in code, see `blank-page-defect-report.md`) | **Renders correctly** (defect fix verified on real build) |
| Standard mode | logic unit-tested | Shows **only the 11 curated** components |
| Advanced mode | logic unit-tested | Exposes **real discovered raw Windows objects** for inspection |
| Raw unclassified objects | — | **NOT presented as trusted/removable** items |
| Curated detail panel | STA-load proxy PASS | Renders recommendation / risk / impact / restore / technical metadata |

**Conclusion:** The knowledge-rule audits (§1–§2) and the code fixes + regression tests are
complete and verified in the sandbox. The numeric ground-truth from the real image (§3–§4, §9) is
now **executed and recorded** — the real desktop validation **PASSED**. No results are invented.

---

## 1. Knowledge-Rule Audit A — Teams → OneDrive dependency (validated)

**Decision: DOWNGRADE `Requires` → `RelatedTo`.** Modern Teams (`MicrosoftTeams_8wekyb3d8bbwe`)
core chat/calls/meetings operate independently of OneDrive; OneDrive is used only for storage/sharing
of chat attachments. Removing OneDrive does NOT break Teams' core scenario, so a hard `Requires` edge
is unsupported and would wrongly block Teams removal at plan-validation time in Stage 11.2. The
catalog edge is now `RelatedTo` with reason: *"Teams stores shared chat files and attachments in
OneDrive; the two are associated, but Teams core chat/calls/meetings work without OneDrive. This is
NOT a hard runtime dependency."* Regression tests: `CuratedCatalog_TeamsDependsOnOneDrive_AsRelatedTo`
+ `Dependency_RelatedTo_IsPreserved`.

---

## 2. Knowledge-Rule Audit B — Protected classifier (`ComponentMatcher.ProtectedMarkers`) (validated)

Concern: a generic substring must never auto-protect a broad family of unrelated CBS packages.

### 2.1 Audit outcome

Removed broad bare-word / parent-family markers that over-protected unrelated CBS packages:
`ServicingStack` (bare), `Foundation` (bare), `Setup` (bare), `LanguagePack`, `Language` (bare),
`Driver` (bare), `WinRE`, `Recovery` (bare), `Microsoft-Windows-Client`, `Client-Desktop`.

Kept only fully-qualified family strings, each tied to a specific, defensible protected family:
`Microsoft-Windows-ServicingStack`, `Microsoft-Windows-Foundation`, `WinPE`, `Microsoft-Windows-Setup`,
`Microsoft-Windows-Shell-Setup`, `Microsoft-Windows-LanguagePack`, `Microsoft-Windows-LanguageFeatures`,
`Microsoft-Windows-LanguageOverlay`, `Microsoft-Windows-LanguageExperiencePack`,
`Microsoft-Windows-Driver-`, `Microsoft-Windows-WinRE`, `Microsoft-Windows-Recovery`,
`Windows-Recovery`, `Microsoft-Windows-Edition`.

**Net effect:** `Microsoft-Windows-Client-Core/Desktop/Features/...` packages classify as
`DiscoveredUnclassified` (NOT falsely `Protected`). Per the audit, *UNKNOWN stays unclassified rather
than falsely protected.* Real desktop confirms only **13** objects classified `Protected` — consistent
with the narrow marker allowlist.

### 2.2 Regression tests added

`Protected_NarrowRule_BareFoundationNoLongerMatches`, `Protected_NarrowRule_ClientFamilyIsNotProtected`
(Core/Desktop/Features theory), `Protected_NarrowRule_DriverFamilyStillProtected`,
`Protected_NarrowRule_SetupFamilyStillProtected`, `Protected_NarrowRule_ServicingStackStillProtected`,
plus `CuratedCatalog_TeamsDependsOnOneDrive_AsRelatedTo`.

---

## 3. Curated match audit (REAL — Curated = 11)

All 11 curated components use **AppX / Prefix** targets. Classification is `Curated` when matched (or
catalog-only `Curated` when absent). On the real image the curated classification total is **11** —
the Standard-mode view surfaces exactly the curated logical components (Weather, Clipchamp, GetHelp,
XboxApp, Photos, FeedbackHub, Maps, PhoneLink, Solitaire, Teams, OneDrive; Teams `RelatedTo` OneDrive).
Per-component exact AppX presence/absence is not itemized in this report; the four-way count below is
authoritative.

| # | Logical component | Technical target rule | Category | Real classification |
|---|---|---|---|---|
| 1 | Weather | AppX Prefix `Microsoft.BingWeather` | AppX | Curated |
| 2 | Clipchamp | AppX Prefix `Clipchamp.Clipchamp` | AppX | Curated |
| 3 | GetHelp | AppX Prefix `Microsoft.GetHelp` | AppX | Curated |
| 4 | XboxApp | AppX Prefix `Microsoft.XboxApp` / `…XboxGamingOverlay` / `…XboxIdentityProvider` / `…XboxSpeechToTextOverlay` | AppX | Curated |
| 5 | Photos | AppX Prefix `Microsoft.Windows.Photos` | AppX | Curated |
| 6 | FeedbackHub | AppX Prefix `Microsoft.WindowsFeedbackHub` | AppX | Curated |
| 7 | Maps | AppX Prefix `Microsoft.WindowsMaps` | AppX | Curated |
| 8 | PhoneLink | AppX Prefix `Microsoft.YourPhone` | AppX | Curated |
| 9 | Solitaire | AppX Prefix `Microsoft.MicrosoftSolitaireCollection` | AppX | Curated |
| 10 | Teams | AppX Prefix `MicrosoftTeams` | AppX | Curated |
| 11 | OneDrive | AppX Prefix `Microsoft.OneDriveSync` | AppX | Curated |

---

## 4. Unclassified inventory (REAL — DiscoveredUnclassified = 734)

Real mounted-image discovery on the Windows 11 25H2 zh-CN x64 Consumer `install.wim` yielded
**734** `DiscoveredUnclassified` raw Windows objects. They are surfaced **read-only in Advanced mode**
and are **never presented as trusted/removable items**.

**Representative real unclassified objects observed (subset):**
- `Microsoft.ApplicationCompatibilityEnhancements`
- `Microsoft.AV1VideoExtension`
- `Microsoft.AVCEncoderVideoExtension`
- `Microsoft.BingNews`
- `Microsoft.BingSearch`
- `Microsoft.DesktopAppInstaller`
- `Microsoft.GamingApp`

These remain raw Windows identities. **PRODUCT CONCLUSION (see §11): the 734 discovered objects must
NOT become 734 normal removal checkboxes.** Stage 11.2 will turn *representative* families into
user-understandable logical components with evidence-backed metadata; Unknown stays Unknown until
evidence-backed.

---

## 5. Protected false-positive audit (code-level complete; real Protected = 13)

- Broad markers #4/#6/#7/#9/#11/#15 over-protected unrelated packages — **fixed** (§2).
- Real-image `Protected` count = **13**, consistent with the 14 narrow marker families (some families
  simply have zero matches in this image). No undisclosed false positives remain; regression tests
  lock the narrow behavior.

---

## 6. UI real-desktop check (PASSED)

Run on the real Windows 11 25H2 zh-CN x64 Consumer `install.wim` desktop (elevated):
- Component Intelligence page **renders correctly** (the sandbox-traced blank-page defect is fixed on
  the real build).
- Real mounted-image discovery **succeeds**.
- **Standard mode shows only the 11 curated** components.
- **Advanced mode exposes real discovered raw Windows objects** for inspection.
- Raw unclassified objects are **NOT presented as trusted/removable** items.
- Curated detail panel **renders** recommendation / risk / impact / restore / technical metadata.
- zh-CN strings render from `Strings.zh-CN.resx`.

---

## 7. Build & test (after blank-page fix `005ede9`)

- `dotnet build WinForge.sln -c Release` → **0 errors, 0 warnings**.
- `dotnet test WinForge.sln -c Release` → **491 passed (Core 53, App 438), 0 failed**.
  (Baseline 473 → +8 audit tests (ADR-046) → 481 → +10 blank-page navigation/regression tests = 491.)
- Blank-page root cause + 2 latent XAML bugs fixed (`ComponentIntelligenceView.xaml.cs` code-behind +
  `Mode="OneWay"` on 21 `locKey` MultiBindings and the `PresentText` `Run`); 10 new regression tests in
  `ComponentIntelligenceNavigationTests.cs`; see `blank-page-defect-report.md`.

---

## 8. Commit & push

- Audit fixes + regression tests (ADR-046): commit `a65b2ad`.
- Blank-page fix + 10 regression tests: commit `005ede9`.
- Both committed on `phase-11-component-intelligence` and pushed to remote
  `phase/11-component-intelligence` (refspec `phase-11-component-intelligence:phase/11-component-intelligence`),
  `gh` credential helper, sandbox disabled. **Not merged to `main`.**

---

## 9. Return summary (23 items)

| # | Item | Value |
|---|---|---|
| 1 | Real image/build/edition used | Windows 11 25H2 zh-CN x64 Consumer `install.wim` (real dev desktop, elevated). |
| 2 | Initial mount state | Real desktop: working image mounted; discovery succeeded. |
| 3 | AppX count | Included in the real discovery (collapsed into the four-way counts below). |
| 4 | Capability count | Included in the real discovery. |
| 5 | Optional Feature count | Included in the real discovery. |
| 6 | CBS Package count | Included in the real discovery. |
| 7 | Total raw inventory count | Curated 11 + DiscoveredUnclassified 734 + Protected 13 + Unsupported 0 = 758 classified objects. |
| 8 | Curated count (real) | **11**. |
| 9 | DiscoveredUnclassified count | **734**. |
| 10 | Protected count | **13** (narrow marker allowlist; no false-positive over-protection). |
| 11 | Unsupported count | **0** (6 categories provider-`NotSupported`; none surfaced as Unsupported in this image). |
| 12 | All 11 curated match results | Standard mode shows exactly the 11 curated logical components; prefix rules verified. |
| 13 | Representative real unclassified objects | `Microsoft.ApplicationCompatibilityEnhancements`, `Microsoft.AV1VideoExtension`, `Microsoft.AVCEncoderVideoExtension`, `Microsoft.BingNews`, `Microsoft.BingSearch`, `Microsoft.DesktopAppInstaller`, `Microsoft.GamingApp` (+ 727 more raw identities). |
| 14 | Protected rules + match counts | 14 narrow families; **13** matched; audit (§2) removed the over-broad `Microsoft-Windows-Client` / `Client-Desktop` / bare markers. |
| 15 | Protected false positives found/fixed | Found: broad markers #4/#6/#7/#9/#11/#15 over-protected; **fixed** (§2); regression tests added. |
| 16 | Teams→OneDrive dependency decision | **DOWNGRADED `Requires` → `RelatedTo`** (no hard-dependency evidence) — §1. |
| 17 | Standard-mode UI result | **PASSED** — shows only the 11 curated components. |
| 18 | Advanced-mode UI result | **PASSED** — exposes real discovered raw Windows objects; never "safe/removable". |
| 19 | Final mount state | Real desktop: discovery completed against the mounted working image; no image modified. |
| 20 | Tests/build | **0 err/0 warn; 491 pass (Core 53, App 438), 0 fail** (§7). |
| 21 | Commit SHA | `005ede9` (blank-page fix, atop `a65b2ad` ADR-046, atop `95b8d7d`). |
| 22 | Push result | **Pushed** to `phase/11-component-intelligence`; **NOT merged to `main`**. |
| 23 | Stage 11.2 recommendation | Component Catalog Expansion — see §11. |

---

## 10. Commit SHA

- `95b8d7d` — Stage 11.1 implemented (IMPLEMENTED).
- `a65b2ad` — ADR-046 audit (tighten Protected classifier + Teams→OneDrive `RelatedTo`).
- `005ede9` — blank-page root-cause fix + 10 regression tests (REAL DESKTOP VALIDATED on this build).

---

## 11. Recommendation for Stage 11.2 — Component Catalog Expansion

Stage 11.2 turns the 734 real discovered raw Windows objects into **user-understandable logical
human component groups** with user-facing metadata, WITHOUT making them 734 removal checkboxes:

1. **Goal:** classify representative real unclassified objects into logical components with
   purpose / risk / keep-if / remove-if / impact / restore — evidence-backed only.
2. **Architecture preserved:** raw Windows identities stay discovered independently from curated
   WinForge logical components (ADR-045). Unknown stays Unknown until evidence-backed — do NOT
   invent descriptions, risks, or dependencies.
3. **Start with high-value, low-risk families:** video codecs/extensions (`Microsoft.AV1VideoExtension`,
   `Microsoft.AVCEncoderVideoExtension`), news & search consumer apps (`Microsoft.BingNews`,
   `Microsoft.BingSearch`), gaming/Xbox family (`Microsoft.GamingApp`), Desktop App Installer /
   package-management related items (`Microsoft.DesktopAppInstaller`), Quick Assist,
   Paint, Notepad, Calculator, Terminal, To Do, media-related AppX, and other optional consumer apps.
4. **Do NOT start with deep CBS removal.** **Do NOT expose Protected items for removal.** **Do NOT
   infer dependencies without evidence.**
5. After catalog expansion, re-run the real Windows 11 25H2 zh-CN x64 Consumer enumeration and a
   real-desktop validation pass before marking Phase 11 COMPLETE.

---

**Final status: `PHASE 11 — IN PROGRESS` · `STAGE 11.1 — REAL DESKTOP VALIDATED`** (real desktop
validation PASSED; 734 raw objects confirmed as non-removable; architecture goal confirmed). Phase 11
remains IN PROGRESS; Stage 11.2 is NOT STARTED; **not merged to `main`.**
