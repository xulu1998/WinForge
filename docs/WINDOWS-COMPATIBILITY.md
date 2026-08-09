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
| Windows 11 25H2 | 26200 | x64 | zh-CN | Consumer (install.wim) | Tested | Untested | Tested | Untested | Untested | Untested | ISO layout (Step 2.1) + WIM metadata inspection (Step 2.2) **Tested** on real desktop 2026-08-08: two-stage `/Get-ImageInfo` flow validated (6 indexes 家庭版/家庭单语言版/教育版/专业版/专业教育版/专业工作站版, version 10.0.26200, build 26200, x64, localized zh-CN edition names, guaranteed dismount, Language `zh-CN` with footer prose correctly rejected). Both real-desktop findings fixed and revalidated: DISM exit 87 (`/Get-WimInfo`→`/Get-ImageInfo`) and language footer `The` (`TryNormalizeLanguageTag`). |

> No Windows version is claimed as `Supported` yet. All rows above are
> `Untested` for columns not yet validated through the testing strategy in
> docs/TESTING.md. Windows 11 25H2 `Inspection` is `Tested` for **ISO-layout
> inspection (Step 2.1)** and for the **two-stage WIM metadata read (Step 2.2)**:
> on a real Windows 11 25H2 (zh-CN, x64, Consumer ISO, `install.wim`) desktop on
> 2026-08-08 the app mounted the ISO read-only, ran `dism.exe /Get-ImageInfo` to
> enumerate 6 indexes (家庭版/家庭单语言版/教育版/专业版/专业教育版/专业工作站版),
> queried each index for detail (version `10.0.26200`, build `26200`, x64,
> localized Chinese edition names), and dismounted. The run exposed two real-DISM
> findings, both now fixed and **revalidated** on the same release: (1) DISM exit
> code 87 from the initial `/Get-WimInfo` verb → corrected to the documented
> `/Get-ImageInfo`; (2) a trailing DISM footer (`The operation completed
> successfully.`) parsed as the language `The` → fixed via `TryNormalizeLanguageTag`
> (BCP-47-like validator) with clean `Languages` section termination; the UI now
> shows `zh-CN` only. Step 2.2 is **ACCEPTED** (2026-08-08) with the corrected
> language output confirmed on a real desktop. This does **not** claim `Supported`
> for the OS. ESD (`install.esd`) metadata parsing is implemented but still
> `Untested` on a real desktop.

## How to update

When a release is verified, change its status cells to `Testing` then
`Supported`, and add a Note with the verification commit/date.
