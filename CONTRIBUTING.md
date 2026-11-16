# Contributing / Katkı

Thanks for helping. Issues and pull requests may be written in Turkish or English. /
Katkın için teşekkürler; issue ve PR'lar Türkçe ya da İngilizce olabilir.

## Before you start

- **Security issues:** never as a public issue — see [SECURITY.md](SECURITY.md).
- **Bugs:** include the Tezgah version (`F1` → About), Windows version, steps to reproduce and, if relevant, the log
  (`F1` → *Open log folder*, `%LOCALAPPDATA%\Tezgah\logs`). Remove personal paths you do not want to share.
- **Larger changes:** open an issue first so we can agree on the approach.

## Build and test

```powershell
dotnet build            # warnings are errors, analyzers are on
dotnet test             # unit, UI and end-to-end tests; all must pass
dotnet run --project src/Tezgah.App -- --demo   # try the UI with sample data
```

**SDK:** `global.json` pins the .NET SDK to the 10.0.3xx feature band (10.0.302 or a later patch, `rollForward:
latestPatch`); CI installs exactly that version. A newer feature band (10.0.4xx) is deliberately not accepted: the
self-contained publish pulls SDK-implicit packages such as `Microsoft.NET.ILLink.Tasks` whose version follows the SDK,
so CI, the release build and local builds should use the same SDK. Moving to a new band is a one-line PR to
`global.json` (CI then proves restore, tests and the publish smoke test on it).

**Versions:** `Directory.Build.props` carries a `-dev` version for local builds. A release takes its version only from
the `vX.Y.Z` tag (`release.yml` validates the tag and passes `-p:Version`), and CI checks that the version passed to
`dotnet publish` is the one in `Tezgah.exe`. After a release, bump the `-dev` version to the next patch.

## Guidelines

- **Layers:** `Tezgah.Core` has no UI dependency; the WPF app talks to it only through `Tezgah.Core.Abstractions`.
  View-models never use WPF types — windows, dialogs and shell actions go through `IDialogService` / `IShellService`.
- **Safety first:** anything that starts a process, opens a URL/folder or deletes files must go through the existing
  guards (`ProcessLaunch` / `CmdArgumentGuard`, `ShellOpener`, `PathGuard` / `FileRemover`) and come with tests,
  including a hostile-input case.
- **Text:** user-facing strings in Core use `L.T("Türkçe", "English")`; in the app they live in
  `Resources/Strings.tr.xaml` and `Strings.en.xaml` with identical keys (a test checks this).
- **Logo:** the logo is defined once in `src/Tezgah.App/Views/Logo.cs`. After changing it run
  `pwsh -File src/Tezgah.App/Assets/make-icon.ps1` to regenerate `tezgah.ico` and `docs/logo.svg` (a test checks they match).
- **Screenshots:** Debug builds can render them: `Tezgah.exe --screenshot docs/screenshot-demo.png --theme light
  --screenshot-about docs/screenshot-about.png` (always uses demo data).
- Keep pull requests small and focused, with a clear description of *why*. New behaviour needs tests.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
