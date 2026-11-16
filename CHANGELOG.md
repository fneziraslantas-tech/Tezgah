# Changelog

> Bu değişiklik günlüğü İngilizce tutulur. / This changelog is kept in English.

All notable changes to Tezgah are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.1] - 2026-11-16

First public release. Windows x64, self-contained single-file build (`Tezgah-v0.1.1-win-x64.zip`) with a SHA-256
file and a GitHub build provenance attestation. The executable is not code-signed.

> `v0.1.0` was tagged, but its release build failed and nothing was published. `0.1.1` is the first release.

### Added

- Scanning of git repositories and project folders (Node, Python, .NET, Rust, Go, Java, Docker, static web, scripts),
  installed apps (Windows registry) and package-manager tools (pipx, npm -g, cargo, dotnet tool, scoop, go install).
- One-click run of detected and user-defined commands with a live console, stdin and clickable local server URLs.
- Clean stop: every run lives in a Job Object, so stopping it (or closing Tezgah) ends all child processes.
- Free space: delete regenerable folders such as `node_modules`, `.venv`, `bin/obj`.
- Full removal of a project or app (folder, vendor uninstaller, shortcuts, virtualenvs, verified leftovers) from a
  plan that is shown first and confirmed.
- Git status: branch, ahead/behind, unsaved changes, stashes, unpushed branches and worktrees.
- Search, filters, tags, notes and cloning; Turkish / English UI, light / dark theme, full keyboard use.

### Security

- Pre-release hardening from an internal multi-agent adversarial audit (3 rounds, each finding fixed with a regression
  test): workspace trust before any project code runs, untrusted repositories inspected without starting git,
  `GitRepoGuard` before any git run, a single `PathGuard` for every file action, typed confirmation for removals with
  unsaved work, an audit log written before anything is deleted, and the Recycle Bin as the default delete target.
  See [SECURITY.md](SECURITY.md).
- Fixes for all 8 leads that the external-method audit
  ([docs/security-audit-2026-11.md](docs/security-audit-2026-11.md)) left as `needs_validation`:
  - Permanent delete descends by handle, so a junction swapped in during the delete cannot redirect it (#5).
  - Multiline project-detector regexes are linear and time-bounded, so a crafted manifest cannot stall a scan (#6).
  - Discovery does not read files through links, and git pathname config values (including `:(optional)` /
    `%(prefix)` forms and values reached through a link in the worktree) are refused when they lead to a network
    share (#8).
  - Trust is bound to the command shown in the trust prompt, anchored to the folder's file id instead of its creation
    time, and a stale "cloned by Tezgah" mark is dropped when an untrusted folder is replaced (#9). Folders trusted
    with earlier builds are asked about once more.
- Uninstall stops only the processes shown in the plan, and the free-space dialog warns when deletion is permanent
  (#7).

### Fixed

- Release pipeline: publish no longer fails on the SDK-implicit `ILLink.Tasks` package; CI now runs a publish smoke
  test on every pull request (#10).
- Timing-dependent tests made robust under machine load (#2, #3, #4).

### Changed

- Test tooling updated: coverlet.collector 10.1.1, Microsoft.NET.Test.Sdk 18.12.0, xunit.runner.visualstudio 4.0.1
  (#1).

[Unreleased]: https://github.com/fneziraslantas-tech/Tezgah/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/fneziraslantas-tech/Tezgah/releases/tag/v0.1.1
