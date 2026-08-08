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
| Windows 11 25H2 | — | x64 | zh-CN | Consumer (install.wim) | Untested | Untested | Tested | Untested | Untested | Untested | ISO layout inspection tested on real desktop 2026-08-08; WIM/ESD content parsing is Step 2.2 (not done) |

> No Windows version is claimed as `Supported` yet. All rows above are
> `Untested` until validated through the testing strategy in docs/TESTING.md.
> Windows 11 25H2 `Inspection` is `Tested` for **ISO-layout inspection only**
> (Step 2.1): the app mounted the ISO read-only, detected the Windows ISO
> candidate layout, and dismounted — verified on a real Windows 11 25H2
> (zh-CN, x64, Consumer ISO, `install.wim`) desktop on 2026-08-08. This does
> **not** claim `Supported` for the OS, and does not cover WIM index / edition /
> version / architecture / language parsing (Step 2.2). The exact build number
> was not recorded during the test.

## How to update

When a release is verified, change its status cells to `Testing` then
`Supported`, and add a Note with the verification commit/date.
