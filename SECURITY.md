# Security policy / Güvenlik politikası

Tezgah scans folders it did not create, runs other people's code on request and deletes files. Security reports are
taken seriously. / Tezgah, kendi oluşturmadığı klasörleri tarar, istek üzerine başkalarının kodunu çalıştırır ve dosya
siler; güvenlik bildirimleri öncelikle ele alınır.

## Supported versions / Desteklenen sürümler

| Version | Supported |
| ------- | --------- |
| Latest release (0.1.x) and `main` | Yes |
| Older releases | No — please update first |

## Reporting a vulnerability / Açık bildirme

**Please do not open a public issue.** Use GitHub private vulnerability reporting:

1. Go to the repository's **Security** tab → **Report a vulnerability**
   (<https://github.com/fneziraslantas-tech/Tezgah/security/advisories/new>).
2. Describe the impact, affected version/commit and steps to reproduce. A crafted sample repository (as a zip or a
   script that creates it) is the most useful proof of concept.

You will get an acknowledgement within 7 days. Fixes are developed in a private advisory fork, released, and then the
advisory is published with credit to the reporter (unless you prefer otherwise).

**Lütfen herkese açık issue açmayın.** Depodaki **Security** sekmesi → **Report a vulnerability** ile özel bildirim
gönderin; etkiyi, sürümü ve yeniden üretme adımlarını yazın. 7 gün içinde yanıt verilir; düzeltme yayımlandıktan sonra
duyuru, isterseniz adınızla birlikte yayımlanır.

## Scope

In scope: anything that lets a scanned folder, repository, registry entry, URL or console output make Tezgah run code,
open something, leak credentials or delete/modify data the user did not explicitly choose; bypasses of the trust
prompt or of the typed confirmation; integrity of the release pipeline.

Out of scope: code you explicitly trusted and ran (Tezgah runs it with your user rights by design), attacks that need
an already compromised user account or administrator rights, and denial of service by filling the disk.

Tezgah runs as the current user (`asInvoker`); it never asks for elevation itself. A vendor uninstaller may request it
through its own UAC prompt.

## Threat model

Every scanned folder is treated as untrusted input: its files, its `.git` directory and config, its package manifests,
README links and the output of anything it runs.

| Threat | Mitigations in Tezgah |
| ------ | --------------------- |
| **Untrusted repository code runs** (a cloned repo's `npm run dev`, `setup.py`, build scripts) | Workspace trust: `RunService` refuses to start any profile of an untrusted item (`UntrustedItemException`). The trust prompt shows the exact command, the folder and the remote; the remote is marked *verified* only when Tezgah cloned the folder itself. Trust is bound to the folder's identity (creation time): a folder deleted and re-created at the same path, a fresh clone or a folder that re-appears loses it. Only profiles the catalog knows can run, and the catalog's copy of the command is what runs. `git pull`/`fetch` (which honour repo settings such as `core.sshCommand`) are also gated behind trust. |
| **Command injection** through names, paths or arguments from manifests | No shell is ever used to start a profile, tool or uninstaller: arguments go through `ProcessStartInfo.ArgumentList`. Only `.exe/.com/.cmd/.bat` targets are accepted. Uninstall strings from the registry are parsed into program + arguments (`UninstallStringParser`) instead of being handed to a shell. Clone URLs are limited to https/ssh; option injection (`--upload-pack=`), `ext::`/`fd::`/`file::` transports, local paths and `file://` are refused. |
| **BatBadBut** (cmd.exe re-parsing arguments of `.cmd/.bat` targets such as `npm.cmd`, `gradlew.bat`; CVE-2024-24576 class) | `CmdArgumentGuard` does not try to escape: it only builds a command line when every argument consists of cmd-inert characters (paths with spaces are quoted; `" % ! ^ & \| < > ( )` and control characters are rejected and the run is refused). cmd.exe is taken from System32 and started with `/d /v:off /s /c` (no AutoRun, no delayed expansion). |
| **PATH / current-directory planting** (a repo ships `git.exe`, `node.cmd`…) | Bare program names are resolved against absolute `PATH` entries only, never the working directory. Tool, uninstaller and discovery commands also set `NoDefaultCurrentDirectoryInExePath=1`, so cmd.exe shims cannot resolve bare names from the folder. Known limit: runs of *trusted* projects do not set it (scripts legitimately call local helpers by bare name), which only matters for code the user already trusted. |
| **Code execution through git config during a scan** (`core.fsmonitor`, hooks, filter drivers, `diff.external`, `core.pager`…) | Repositories the user has not trusted are scanned without starting any git process: their state is read from the `.git` files directly (this is being rolled out on `main`). Every git process Tezgah does start carries command-scope overrides via `GIT_CONFIG_COUNT` that beat repo config and includes (`core.fsmonitor=false`, `core.hooksPath=NUL`, `core.pager=cat`, empty `core.askPass`/`diff.external`, `protocol.ext/fd.allow=never`, `protocol.file.allow=user`, no submodule recursion, `safe.bareRepository=explicit`…), neutralises every filter driver for read-only calls, runs from a neutral working directory with `GIT_CEILING_DIRECTORIES`, never prompts for credentials and never bypasses `safe.directory`. |
| **NTLM hash leak via UNC paths** (`gitdir:`, `commondir`, `objects/info/alternates`, `include.path` pointing at `\\attacker\share`) | `GitRepoGuard` resolves all of those before git runs and refuses the repository (no git process) unless every location is a fully qualified path on a fixed, removable or RAM drive — never a share, mapped network drive or device path. `PathGuard` refuses UNC/device paths for every file action and `ShellOpener` never opens `file:` URLs or shares. |
| **Deletion outside the item** (crafted registry `InstallLocation`, shortcut targets, `..` tricks) | One `PathGuard` for every filesystem action, deny by default: relative, UNC, device, ADS, trailing dot/space and `..` paths are refused before normalisation; 8.3 names are expanded and the final path is asked from the OS. The Windows directory, Tezgah's own folders, the user profile root, Desktop/Documents, other users' profiles and the install roots themselves are protected; shared vendor folders under Program Files are never deleted. Installed-app leftovers are opt-in and offered only with ownership evidence (the app's own uninstaller lives there, or a vendor folder under an install root), are re-checked after the vendor uninstaller ran and skipped when they look like user data. Shortcuts are removed only when their canonical target is inside the item. |
| **Junctions, symlinks and TOCTOU** (a folder swapped for a link between planning and deletion) | Any symlink, junction, mount point or unknown reparse tag in the chain from the drive root to the target refuses the delete. `FileRemover` re-runs the guard immediately before every action, on the thread that performs it. Permanent deletion never follows links (link entries are removed, not their targets) and first renames the folder to a tombstone on the same volume, so a folder in use fails with nothing deleted. Folder size measurement does not follow links either. |
| **Loss of unsaved work** | Before removing a repository Tezgah checks uncommitted and untracked changes, stashes, every local branch without a remote copy (no upstream, ahead, gone upstream), linked worktrees, nested repositories and git-ignored files that may exist only there (`.env`, keys, local databases). Any of these makes the plan *Danger*: the item's name must be typed. The state is re-read right before execution; new unsaved work refuses the run and asks for a new plan. Deleted folders go to the Recycle Bin by default. |
| **Forged or replayed uninstall plans** | Only plans produced by the same `UninstallService` instance are executed, from an immutable snapshot taken at planning time (the plan object handed to the UI is never trusted). Blocked plans never run, each plan runs at most once, steps stop at the first failing mandatory step, and the catalog entry is removed last. An audit record (`%LOCALAPPDATA%\Tezgah\audit.log`, JSON lines) is written *before* anything is touched — if it cannot be written nothing is deleted — and another one with the outcome. |
| **Output flooding and terminal escape sequences** | ANSI/VT sequences (CSI, OSC, single-character escapes) and control characters are stripped before display. Lines are capped at 16 K characters, consoles keep at most 5,000 lines, the UI takes output in bounded batches every ~50 ms with a "lines skipped" marker, captured output of short commands is capped at 1 M characters while the rest is drained, and at most 10 local server URLs are remembered per run (only `localhost`/loopback URLs become chips). |
| **Opening URLs and folders** (`homepage` in `package.json`, README links, git remotes) | `ShellOpener` is the only place that uses `UseShellExecute`. URLs must be absolute `http`/`https`, without credentials or control characters, at most 4,096 characters — no `file:`, `ms-*:`, `search-ms:` or other protocol handlers. Folders must pass the path guard and are opened with a trailing separator so a name like `tool.exe` cannot launch a program. Windows Terminal never sees the folder path on its command line. |
| **Supply chain in CI and releases** | GitHub Actions are pinned to full commit SHAs, checkouts use `persist-credentials: false`, workflows default to `contents: read`. NuGet restores in locked mode against committed `packages.lock.json` files with NuGet audit (all, low) plus an explicit vulnerable-package check; Dependabot keeps packages and actions current. gitleaks scans history for secrets and CodeQL (`security-extended`) runs on every push, PR and weekly. Releases are built and tested in a read-only job; only a separate job without build steps can write, and it publishes the zip with a SHA-256 file and a build provenance attestation. |

If you find a gap between this table and the code, that itself is worth a report.
