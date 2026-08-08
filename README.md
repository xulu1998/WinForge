# WinForge

> Build Windows your way.

**An independent Windows image customization tool.**

WinForge lets you take an official Microsoft Windows 11 ISO and create a
customized Windows installation image through a graphical interface — without
relying on or copying any third-party debloat projects.

> ⚠️ **Status: 0.1.0-alpha — Project Governance**
> Application functionality has not started yet. This repository currently
> contains project governance and planning documents only.

## Project Status

| | |
|---|---|
| Version | 0.1.0-alpha |
| Current Phase | Phase 0 — Project Governance (COMPLETED) |
| Next | Phase 1 — Application Foundation |
| Platform | Windows 11 |
| Framework | .NET 8 (WPF) |

See [`PROJECT_STATUS.md`](PROJECT_STATUS.md) for the authoritative status.

## What WinForge Does

- Inspect official Microsoft Windows 11 ISO images
- Select Windows editions
- Handle WIM / ESD images with safe mount/unmount
- Customize components, remove applications, tune the Windows experience
- Configure privacy, OOBE, and hardware requirements
- Optimize, rebuild, validate, and log the resulting image
- Save and reuse **Presets** (configuration data, not separate code paths)

WinForge is a **Windows Image Customization Platform**, not merely a debloater.

## Technology

- **Language:** C#
- **Framework:** .NET 8
- **UI:** WPF with MVVM
- **Architecture:** `WinForge.App` → `WinForge.Core` ← `WinForge.Infrastructure`

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for details.

## Documentation

| Document | Purpose |
|----------|---------|
| [`ROADMAP.md`](ROADMAP.md) | Phased development plan (Phase 0–12) |
| [`PROJECT_STATUS.md`](PROJECT_STATUS.md) | Live project status |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Technical architecture & layering |
| [`DECISIONS.md`](DECISIONS.md) | Architecture decision records (ADRs) |
| [`AGENTS.md`](AGENTS.md) | Rules for AI coding agents |
| [`CHANGELOG.md`](CHANGELOG.md) | User-visible changes |
| [`docs/PRODUCT.md`](docs/PRODUCT.md) | Product direction |
| [`docs/TESTING.md`](docs/TESTING.md) | Test strategy |
| [`docs/WINDOWS-COMPATIBILITY.md`](docs/WINDOWS-COMPATIBILITY.md) | Windows compatibility matrix |

## Independence & Licensing Note

WinForge is implemented independently. It does **not** copy, derive from, or
redistribute any other Windows customization/debloat tool (including but not
limited to tiny11builder) — no source code, file structure, PowerShell,
function/variable names, README, XML, assets, or implementation approaches.
Technical choices are based on publicly documented Microsoft Windows deployment
APIs and documentation.

This project is currently **unlicensed** (all rights reserved). No open-source
license file is included at this time.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`AGENTS.md`](AGENTS.md).
