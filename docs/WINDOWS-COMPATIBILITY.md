# Windows Compatibility Matrix

Track which Windows releases WinForge supports for each operation.

## Status values

- `Planned` — intended, not yet worked on
- `Untested` — code may exist but not verified on this release
- `Testing` — actively being validated
- `Supported` — verified and supported
- `Unsupported` — known not to work / out of scope

## Matrix

| Windows Release | Build | Arch | Lang | ISO Type | install.wim | install.esd | Inspection | Mount | Build | VM Install | Notes |
|-----------------|-------|------|------|----------|-------------|-------------|------------|-------|-------|------------|-------|
| Windows 11 24H2 | 26100 | x64  | en-US | — | Untested | Untested | Untested | Untested | Untested | Untested | Not yet verified |
| Windows 11 23H2 | 22631 | x64  | en-US | — | Untested | Untested | Untested | Untested | Untested | Untested | Not yet verified |
| Windows 11 22H2 | 22621 | x64  | en-US | — | Untested | Untested | Untested | Untested | Untested | Untested | Not yet verified |
| Windows 11 25H2 | 26200 | x64 | zh-CN | Consumer (install.wim) | Tested | Untested | Tested | Untested | Untested | Untested | ISO layout + WIM metadata inspection tested on real desktop 2026-08-08; two-stage `/Get-ImageInfo` flow validated (6 indexes, version 10.0.26200, build 26200, x64, localized zh-CN edition names, guaranteed dismount); trailing DISM footer language-parsing defect found and fixed (pending one final real-desktop re-validation) |

> No Windows version is claimed as `Supported` yet. All rows above are
> `Untested` for columns not yet validated through the testing strategy in
> docs/TESTING.md. Windows 11 25H2 `Inspection` is `Tested` for **ISO-layout
> inspection (Step 2.1)** and for the **two-stage WIM metadata read (Step 2.2)**:
> on a real Windows 11 25H2 (zh-CN, x64, Consumer ISO, `install.wim`) desktop on
> 2026-08-08 the app mounted the ISO read-only, ran `dism.exe /Get-ImageInfo` to
> enumerate 6 indexes (家庭版/家庭单语言版/教育版/专业版/专业教育版/专业工作站版),
> queried each index for detail (version `10.0.26200`, build `26200`, x64,
> localized Chinese edition names), and dismounted. The run **exposed a trailing
> DISM footer language-parsing defect** (footer prose `The operation completed
> successfully.` was parsed as the language `The`); it is fixed via
> `TryNormalizeLanguageTag` (BCP-47-like validator) with clean section
> termination, and needs **one final real-desktop re-validation** of the corrected
> language output before Step 2.2 is marked COMPLETED. This does **not** claim
> `Supported` for the OS. ESD (`install.esd`) metadata parsing is implemented but
> still `Untested` on a real desktop.

## How to update

When a release is verified, change its status cells to `Testing` then
`Supported`, and add a Note with the verification commit/date.
