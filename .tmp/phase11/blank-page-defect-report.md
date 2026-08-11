# PHASE 11 — STAGE 11.1 — REAL DESKTOP DEFECT REPORT
## Component Intelligence page completely blank after real discovery

- **Report date:** 2026-08-10
- **Branch:** `phase-11-component-intelligence` (remote `phase/11-component-intelligence`)
- **Commit:** `005ede9`
- **Final status:** `PHASE 11 — IN PROGRESS` / `STAGE 11.1 — PENDING REAL DESKTOP REVIEW`

---

### 1. Defect
Component Intelligence page renders **completely blank** after real discovery: the utility rail
works, but the content area is white — no list, no detail, no controls, no Discover button, no
empty-state text, **and no error**. This is NOT a "never ran discovery" case; it fails even after a
real discovery pass.

### 2. Exact root cause
`src/WinForge.App/Views/ComponentIntelligenceView.xaml` had **no `.xaml.cs` code-behind**. The
generated `ComponentIntelligenceView.g.cs` defines `InitializeComponent()` but **no constructor calls
it** (contrast `HomeView`, which has `HomeView.xaml.cs`). So `new ComponentIntelligenceView()` never
invokes `InitializeComponent()`, the BAML is never loaded, and `Content` stays `null` — the page
renders as an empty `Border`. This is the silent, exception-free blank page seen on the real desktop.

### 3. How it was proven (no guessing)
Traced the full chain Navigation → `MainViewModel` → `PageKey.ComponentIntelligence` → `ActiveView`
→ `ComponentIntelligenceViewModel` instance → `ComponentIntelligenceView` DataContext → Items/filtered
view → XAML visibility/layout/binding → rendered controls. A direct-view probe showed
`Content == null` immediately after `new ComponentIntelligenceView()`; after the fix `Content == DockPanel`
with **79 visible TextBlocks**. No random `Refresh` calls were added.

### 4. Real data?
**No numeric real-desktop inventory was produced in-sandbox.** There is no valid Windows 11 25H2
zh-CN x64 Consumer `install.wim` available, and DISM requires elevation (→ Error 740). The blank was
reproduced **deterministically** via code inspection + the direct-view `Content==null` probe, which is
independent of discovery data — the page is blank regardless of how many components were discovered.

### 5. High-risk #1 — ViewModel instance identity
**RULED OUT.** `ActiveView` is `ComponentIntelligenceViewModel` **and is the same singleton instance**
as the discovered VM (`ActiveView == ciVm singleton` → `True`). No "discovery on one instance,
navigation shows another" mismatch. `Bootstrapper` registers it as a singleton; `ResolveUtility`
returns that same instance.

### 6. High-risk #2 — Navigation mapping
**VERIFIED CORRECT.** `PageKey.ComponentIntelligence` → `MainViewModel.ResolveUtility` returns
`_componentIntelligence`; `App.xaml` defines the DataTemplate
`DataType={x:Type vm:ComponentIntelligenceViewModel}` → `<views:ComponentIntelligenceView/>`.
Navigation was never broken.

### 7. High-risk #3 — XAML load / runtime binding
**THIS IS WHERE THE ROOT CAUSE LIVES.** Missing code-behind → BAML not loaded. Once that is fixed,
two **latent binding bugs** (below) would have thrown; both are fixed too.

### 8. Latent bug A — `locKey` MultiBinding TwoWay
Inside compiled `DataTemplate`s (ItemTemplate / ContentTemplate / HeaderTemplate), a
`Binding Source=...` with no `Path` defaults to `TwoWay`, which is invalid →
"Two-way binding requires Path or XPath". Fixed by adding `Mode="OneWay"` to all **21** `locKey`
MultiBindings in `ComponentIntelligenceView.xaml`.

### 9. Latent bug B — `Run.Text` TwoWay
`<Run Text="{Binding PresentText}">` defaults to `TwoWay`; `PresentText` is a **read-only** getter →
"Cannot TwoWay/OneWayToSource bind to read-only property". Fixed: `Mode="OneWay"`.

### 10. High-risk #4 — Exception handling / silent swallow
**RULED OUT as the cause.** The real defect threw **no exception** — it just produced a null-Content
blank page. The two latent binding bugs (8, 9) *would* have thrown once BAML loaded; they are now
fixed. A regression guard now asserts construction renders a populated tree or throws — never a
silent blank.

### 11. High-risk #5 — Discovery state
**RULED OUT as the presentation cause.** The VM ctor seeds **11 curated rows** from the catalog, so
the page is never blank even at zero discovery. The blank was purely the missing code-behind.

### 12. Exact fix — code-behind (THE fix)
Created `src/WinForge.App/Views/ComponentIntelligenceView.xaml.cs`:
```csharp
public partial class ComponentIntelligenceView : UserControl
{
    public ComponentIntelligenceView() => InitializeComponent();
}
```

### 13. Exact fix — XAML
`Mode="OneWay"` added to all 21 `locKey` MultiBindings and to the `PresentText` `Run.Text` binding in
`ComponentIntelligenceView.xaml`.

### 14. DataContext
The view's `DataContext` is the **navigated `ComponentIntelligenceViewModel` instance** (same
singleton). Locked by a regression test using `Assert.Same`.

### 15. Empty-state behavior (zero discovery)
The shell still renders: title, intro, Standard-mode `CheckBox`, Discover `Button`, and the
status/empty-state text. `StatusMessage` = `ComponentIntelligence.NoImage`
(en: "Mount a working image to inventory real components. Showing the curated catalog only." /
zh-CN: "请挂载工作映像以清点真实组件。当前仅显示策展目录。"). **Page is NOT blank.**

### 16. Post-discovery UI
List (`Entries`), Standard/Advanced filtering, and the detail panel all render through the now-loaded
BAML.

### 17. Tests added (regression coverage for this defect)
`tests/WinForge.App.Tests/ComponentIntelligenceNavigationTests.cs` — **10 tests** tracing the full
chain: (1) nav produces a visible, non-blank view; (2) DataContext is the intended VM instance;
(3) same VM instance stays active after navigate-away/back; (4) static shell present at zero results;
(5) zero-result state is visible, not blank; (6) populated discovery results produce visible, bound
list items; (7) Standard mode shows only curated rows; (8) Advanced mode shows unclassified rows;
(9) view construction cannot silently degrade to blank; (10) shell text loads under zh-CN and en-US.
`ComponentIntelligenceXamlLoadRegressionTests` strengthened: the 3 existing load tests now also assert
`Content != null` (root-cause guard).

### 18. Test total / pass
**491 pass** (WinForge.App.Tests **438** + WinForge.Core.Tests **53**), **0 fail**.
(10 new + 3 strengthened = 13 new regression assertions for this defect.)

### 19. Build result
`dotnet build WinForge.sln -c Release` → **0 errors, 0 warnings**.

### 20. Commit SHA
`005ede9` on local branch `phase-11-component-intelligence`.

### 21. Push result
Pushed to `origin phase/11-component-intelligence`
(refspec `phase-11-component-intelligence:phase/11-component-intelligence`):
`a65b2ad..005ede9`. **NOT merged to `main`** (per instruction).

### 22. Branch / merge policy
Only `phase/11-component-intelligence` was committed and pushed. No merge to `main`. The genuine
real-desktop discovery pass remains PENDING.

### 23. Final status
`PHASE 11 — IN PROGRESS` / `STAGE 11.1 — PENDING REAL DESKTOP REVIEW`.
The blank-page defect is **root-caused and fixed** with regression coverage. The remaining real-desktop
step — executing an actual discovery pass against a Windows 11 25H2 zh-CN x64 Consumer `install.wim`
(DISM elevation required) — is still to be performed on a real desktop before Stage 11.1 can be marked
reviewed.
