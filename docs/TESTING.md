# Testing Strategy

WinForge performs destructive, system-level operations. Testing must be layered
and rigorous.

## Test Levels

### Unit Tests
- Fast, isolated tests of Core logic (models, validation, Build Plan, presets).
- Run in CI on every push. No Windows image or admin rights required.
- Project: `WinForge.Core.Tests`.

### Integration Tests
- Exercise Core + Infrastructure boundaries with mocked or sandboxed platform
  calls where full admin/Windows features are unavailable.
- Validate service wiring, configuration loading, and error handling.

### Windows Image Tests
- Operate against real (or representative) WIM/ESD images in a controlled
  environment.
- Cover inspection, mount/unmount, component changes, and rebuild.
- Require Windows with DISM/ADK available; run on dedicated agents.

### VM Installation Tests
- Install the rebuilt ISO into a virtual machine and verify it boots and behaves
  as intended.
- Required before any formal release.

## Key Principle

> **A successful build does NOT mean the generated ISO is usable.**

Compiling and even producing an ISO file only proves the tool ran. Correctness
must be proven by actually installing the image.

## Release Gate

A formal release MUST pass real Windows installation testing (VM or physical)
plus all earlier test levels. No exceptions.
