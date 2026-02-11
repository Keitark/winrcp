# Repository Guidelines

## Project Structure & Module Organization
This repository is currently minimal. As implementation is added, use:
- `src/` for application or library code
- `tests/` for automated tests (mirror `src/` when possible)
- `assets/` for static files and fixtures
- `scripts/` for developer automation
- `docs/` for architecture notes and decisions

## Build, Test, and Development Commands
No build toolchain is configured yet. When adding one, expose one-command entry points and update this file in the same PR.

Recommended baseline:
- `make dev` or `npm run dev`: run locally
- `make test` or `npm test`: run full tests
- `make lint` or `npm run lint`: run static checks
- `git status`: verify pending changes before commit

## Coding Style & Naming Conventions
- Use 4-space indentation unless language tooling requires otherwise.
- Naming defaults: files/directories `kebab-case`, classes/types `PascalCase`, functions/variables language-idiomatic (`snake_case` or `camelCase`).
- Keep functions single-purpose and avoid deep nesting.
- Adopt formatter/linter tooling early (for example: `ruff`/`black` or `eslint`/`prettier`).

## Testing Guidelines
- Put tests in `tests/`.
- Use names such as `test_<module>.py` or `<module>.spec.ts`.
- Add tests for every bug fix and non-trivial feature.
- Prefer deterministic tests; mock time/network where needed.

## Workflow (Issue/Branch/PR)
- Create or update an issue before non-trivial work.
- Issue title formats:
  - `[Bug][<Area>] <component>: <symptom>`
  - `[Feat][<Area>] <component>: <capability>`
  - `[Chore] <component>: <change>`
- Required issue sections: Background/Goal, Current behavior, Expected behavior, Acceptance criteria (and Steps to reproduce for bugs).
- Branch names: `fix/<issue>-<slug>`, `feat/<issue>-<slug>`, `chore/<issue>-<slug>`.
- PR title format: `<type>(<scope>): <summary> (#<issue>)`.
- Use `Fixes #<issue>` only for complete delivery; otherwise use `Refs #<issue>`.
- Do not merge until acceptance criteria are met.

## Commit & Pull Request Guidelines
- Use Conventional Commits (for example: `feat: add parser`, `fix: handle empty config`).
- PRs must include linked issue/task, test evidence (command + result), and impact/risk notes.
- For UI/CLI changes, attach screenshots or representative logs.
- Never pass issue/PR bodies as escaped `\n` one-liners; use real multi-line Markdown and verify rendered output with `gh issue view` or `gh pr view`.
