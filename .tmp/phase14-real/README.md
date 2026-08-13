# Phase 14 Stage 14.3 — Elevated Real Inventory Capture Output

This directory receives the EXACT real-media inventory results. It is intentionally
EMPTY until the elevated capture runs — nothing here is fabricated or estimated.

## What the user must run (ONE command, as Administrator)

The build sandbox cannot elevate (DISM returns Error 740), so Stage 14.3 requires a
single elevated run on the real desktop:

```powershell
# 1) Build the tool (or use the already-built exe):
dotnet build tools\WinForge.RealCapture\WinForge.RealCapture.csproj -c Release

# 2) Run it from an ELEVATED (Administrator) PowerShell/terminal:
& tools\WinForge.RealCapture\bin\Release\net8.0-windows\WinForge.RealCapture.exe `
    --iso "C:\Users\xulu1998\Downloads\Win11_25H2_Chinese_Simplified_x64_v2.iso" `
    --index 4
```

The CLI runs the EXACT production pipeline (inspection → export selected index → mount →
production DISM discovery → matcher → DeepComponentClassifier → exact coverage accounting →
top-30 Unknown families) and writes:

- `inventory-summary.json` — exact totals (Total/Curated/Protected/KnownDeep/Heuristic/Unknown)
  + per-category DISM status + exact coverage ratios
- `inventory-items.json` — every raw object with its exclusive classification bucket
- `unknown-items.json` — every remaining Unknown identity
- `unknown-families.json` — top 30 ranked Unknown family clusters (rank/family/source/count/
  representatives/normalized key/reason)
- `coverage-by-source.json` — per-source slices (total/curated/protected/known/heuristic/unknown)
- `gaming-candidates.json` — gaming-relevant candidates with deep knowledge
- `real-derived-families.json` — stable, version/path-stripped fixture (copy to
  `tests/fixtures/25H2-Pro-zhCN-component-families.json` to refresh the regression fixture)

The source ISO is NEVER modified (export + discard only); the tool cleans up mounts and the
temporary workspace afterwards (`--no-cleanup` retains them for inspection).
