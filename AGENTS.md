# AGENTS.md — Rules for AI Coding Agents

This file is authoritative for any AI agent working in the WinForge repository.
If it conflicts with other instructions, this file and the other governance
docs win.

## Before making any code change

1. Read `AGENTS.md`.
2. Read `PROJECT_STATUS.md`.
3. Read `ROADMAP.md`.
4. Read `ARCHITECTURE.md`.
5. Read `DECISIONS.md`.
6. Run `git status`.
7. Determine the active Phase.
8. Never implement a future Phase unless explicitly requested.

## After completing a development task

1. Review changed files.
2. Run the build.
3. Run relevant tests.
4. Fix failures.
5. Update `PROJECT_STATUS.md`.
6. Update `ROADMAP.md` when a Phase status changes.
7. Update `CHANGELOG.md` for user-visible changes.
8. Record new architectural decisions in `DECISIONS.md`.
9. Show a `git diff` summary.
10. Commit only when explicitly instructed **or** when the current task
    explicitly requires a commit.

## Prohibited

- Changing the technology stack without approval.
- Implementing a future Phase ahead of schedule.
- Introducing large dependencies without approval.
- Deleting tests to make the build pass.
- Hiding build/test failures.
- Executing DISM directly in the UI (WPF Views/ViewModels).
- Using third-party modified Windows ISOs as official compatibility targets.
- Copying implementation code from other Windows customization/debloat tools
  (including tiny11builder).

## Notes

- Keep Core platform-agnostic: no App or Infrastructure references from Core.
- Treat Presets as configuration data, not separate code paths.
- When unsure about scope, prefer asking over guessing.
