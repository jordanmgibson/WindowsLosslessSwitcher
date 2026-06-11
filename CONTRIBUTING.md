# Contributing

Thanks for your interest in improving Windows Lossless Switcher. Bug reports, device reports, and pull requests are all welcome.

## Reporting bugs

1. Search [existing issues](https://github.com/jordanmgibson/WindowsLosslessSwitcher/issues) first.
2. Open a new issue using the bug report template.
3. Include your Windows version, audio device (DAC/AVR/interface model), Apple Music for Windows version, and what you were playing when the problem occurred.
4. Attach a diagnostics export — available from the app window — after checking it for anything you consider personal. It contains the per-operation log the app writes locally and is usually the fastest path to a diagnosis.

For suspected security problems, do not open a public issue — see [SECURITY.md](SECURITY.md).

## Suggesting features

Open an issue with the feature request template. Keep in mind the app is deliberately tray-first and minimal: features that add background reliability or device compatibility tend to fit better than UI surface area.

## Adding a tested device

If the app works with your setup, add a row to the "Devices tested" table in [README.md](README.md) and open a pull request. Include your CPU architecture, Windows version, and audio device model.

## Development setup

1. Install the .NET 8 SDK on Windows.
2. Clone the repository.
3. Build and test from the repository root:

```powershell
dotnet build .\WindowsLosslessSwitcher.sln
dotnet test .\WindowsLosslessSwitcher.sln
```

Run the app from Visual Studio or with:

```powershell
dotnet run --project .\src\WindowsLosslessSwitcher.csproj
```

[DEVELOPMENT.md](DEVELOPMENT.md) covers the architecture, the switching flow, the resolver pipeline, and how to implement a new format resolver.

## Guidelines

- Keep changes focused and small when possible.
- Preserve the tray-first UX and the existing settings model unless the change intentionally expands it.
- Add or update tests for behavior changes; the suite runs without audio hardware.
- Prefer targeted comments that explain constraints over inline commentary that restates the code.
- Do not check build output (`bin/`, `obj/`, `artifacts/`, `.tmp/`, `.tools/`) into the repository.

## Pull requests

- Explain the user-visible behavior change.
- Mention any Apple Music, media session (GSMTC), or Core Audio assumptions your change relies on.
- Include test coverage notes and any manual verification steps you ran.

## Release and updater notes (maintainers)

- The updater and the GitHub Actions workflows target `jordanmgibson/WindowsLosslessSwitcher`. Keep `WlsGitHubOwner` and `WlsGitHubRepository` in `src/WindowsLosslessSwitcher.csproj` aligned with `src/Services/ReleaseRepositoryOptions.cs`.
- For local testing against a fork, override the update target with the `WLS_GITHUB_OWNER` and `WLS_GITHUB_REPOSITORY` environment variables.
- Releases are built by the tag-triggered workflow (`v*` tags) as self-contained builds for `win-x64` and `win-arm64`, packaged into per-architecture Velopack channels. Keep channel names, asset names, and the updater's asset selection in sync when changing packaging.
- Do not hand-edit or delete runtime files from publish output; size reductions should come from publish-mode changes.
- Trimming stays disabled for this WPF/NAudio app unless a full validation pass proves it safe.
