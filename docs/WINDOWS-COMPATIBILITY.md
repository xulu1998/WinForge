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

> No Windows version is claimed as `Supported` yet. All rows above are
> `Untested` until validated through the testing strategy in docs/TESTING.md.

## How to update

When a release is verified, change its status cells to `Testing` then
`Supported`, and add a Note with the verification commit/date.
