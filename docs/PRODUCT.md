# Product Definition — WinForge

## Positioning

**WinForge is a Windows Image Customization Platform.**

It is not merely a "Windows debloater." WinForge is a graphical tool that takes
an official Microsoft Windows 11 ISO as input and produces a customized Windows
installation image the user controls — *Build Windows your way.*

## Input

- Official Microsoft Windows 11 ISO (the only officially supported input).

## Long-term Capabilities

- Windows ISO inspection
- Windows edition selection
- WIM / ESD image handling
- Safe mount / unmount
- Component customization
- Application removal
- Windows experience customization
- Windows 10 inspired experience
- Privacy configuration
- OOBE configuration
- Hardware requirement configuration
- Windows image optimization
- ISO rebuilding
- Validation
- Logging
- Presets

## Future Primary Pages

- **Home** — overview, recent projects, quick actions
- **Image** — ISO inspection and edition selection
- **Components** — component customization
- **Experience** — Windows experience settings
- **Privacy** — privacy configuration
- **System** — system tweaks and hardware requirements
- **Build** — build plan review and ISO rebuild
- **Logs** — operation logs and validation results
- **Settings** — application settings and preset management

## Future Presets

- Recommended
- Windows 10 Inspired
- Gaming
- Privacy
- Minimal
- Custom

### Preset rule

Presets are **configuration combinations only**. They must never be implemented
as separate, hard-coded execution paths. Every preset feeds the same Build Plan
engine.

## Independence

WinForge is implemented independently and does not copy any other Windows
customization/debloat project (see DECISIONS.md ADR-006).
