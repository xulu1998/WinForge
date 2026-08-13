# WinForge Compatibility Matrix (Phase 13)

> **Version:** Phase 13 foundation (2026-08-12). This document is the durable,
> non-marketing record of what WinForge knows about Windows images and what has
> actually been validated. Statuses below are strict (ADR-074): **Validated**
> means real media + real WinForge workflow + generated-ISO VM install
> validation. "Automated compatibility coverage" means synthetic fixtures only —
> that is never called "Validated".

## Terminology

| Term | Meaning |
| --- | --- |
| **Validated** | Real ISO → real WinForge workflow → generated ISO → VM install validation (full phase list passed). |
| **Supported with warnings** | Usable, but at least one non-blocking finding applies (e.g. future build, modified media). |
| **Experimental** | Known to work in part; not yet validated end-to-end. |
| **Unsupported** | The pipeline cannot safely handle this (e.g. ARM64 today, SWM servicing). |
| **Automated compatibility coverage** | Synthetic fixtures pass in CI — explicitly NOT "validated". |

## Validated

| Target | Release | Edition | Language | Arch | Format | Date | Evidence / Level |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 25H2-Pro-zh-CN-x64 | Windows 11 25H2 | Professional | zh-CN | x64 | install.wim | 2026-08-13 | **VM INSTALL VALIDATED** (Level = `VmInstallValidated`, ADR-084): real workflow + generated ISO + Hyper-V Gen-2 UEFI install — boot / Setup / image install / reboot / OOBE / desktop all PASS. See `validation/25H2-Pro-zh-CN-x64-20260813-0720.{json,md}`. |

> This row is **VM INSTALL VALIDATED** — NOT `FullHealthValidated`. Deeper
> post-install health checks (Windows Update / Defender / Store / DISM ScanHealth
> / recovery) are intentionally out of Phase 13 scope: the current safe
> customization surface (AppX / selected services / privacy / personalization /
> safe registry) does not perform aggressive CBS / driver / servicing-stack
> removal. `FullHealthValidated` becomes mandatory once component removal is
> substantially more aggressive (later phases). Other editions/languages are NOT
> validated by this row.

## Supported with warnings

- Future Windows 11 builds (newer than the validated matrix): degraded to
  `SupportedWithWarnings` automatically — proceed with a conservative
  configuration; never blocked blindly (ADR-076).
- ESD-based media: inspected read-only; servicing converts the selected index
  to a working WIM (the conversion path is not yet validated on real media).

## Experimental / Pending validation

| Target | Notes |
| --- | --- |
| 25H2 Pro en-US x64 | Tier A next target; fixture coverage exists (not validated). |
| 25H2 Home / Education / Enterprise x64 | Tier B; edition capability facts modeled; media validation pending. |
| 24H2 Pro zh-CN / en-US x64 | Tier C. |
| ESD-based media | Tier C. |
| Multi-index Consumer / Business media | Tier C; multi-index enumeration + index persistence covered by fixtures. |

## Unsupported / Not yet supported

- **ARM64** — the current pipeline requires x64; detected as a BLOCKING finding.
- **Split WIM (install.swm + install2.swm …)** — detected and surfaced as
  read-only inspection only; servicing a split image is not supported (no
  silent failure during Prepare).
- **Missing boot.wim / missing install image / nonstandard media structure** —
  surfaced as blocking findings before any destructive work.

## Media classification

WinForge never claims cryptographic "official Microsoft ISO" verification.
Media structure that matches a standard Windows installation layout is labeled
**“媒体结构符合标准 Windows 安装介质”** (`MicrosoftOfficialLike`); deviations are
flagged `ModifiedMedia` with a warning.

## Language policy (Stage 13.4)

Baseline targets: zh-CN, en-US. Matching uses stable identities only (package
identity, capability identity, feature identity, service name, registry path,
AppX identity) — localized display strings are never identifiers. Language
affects display/explanation only.

## Safety invariants (Stages 13.15–13.18)

Automated catalog assertions guarantee standard recommendations never:

- disable essential Windows Update infrastructure (wuauserv, BITS, UsoSvc, WaaSMedicSvc);
- disable Defender (WinDefend, SecurityHealthService);
- remove core driver packages (storage / USB / networking / display base);
- remove Microsoft Store infrastructure (Store, StoreExperienceHost, App Installer/DesktopAppInstaller).

A future "advanced" mode that changes any of these must never be part of safe
automatic recommendations.

## Validation record format

Reports are written by `ValidationReportWriter` as JSON + Markdown to
`validation/<target>-<timestamp>.json|.md` (see ADR-075). Giant binaries are
never stored in Git — provenance metadata only.

## Reproducibility

A successful ISO build records provenance metadata (SHA-256, file size, build
timestamp, source image metadata, WinForge version/commit, profile summary).
This is provenance, not a byte-for-byte reproducibility claim.
