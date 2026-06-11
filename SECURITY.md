# Security Policy

## Supported versions

The latest release on the [Releases page](https://github.com/jordanmgibson/WindowsLosslessSwitcher/releases) is the supported version. Older releases do not receive security fixes.

## Reporting a vulnerability

Do not open a public GitHub issue for a suspected security problem.

Private vulnerability reporting is enabled for this repository. Report through the Security tab, or directly at:

<https://github.com/jordanmgibson/WindowsLosslessSwitcher/security/advisories/new>

When reporting, please:

- Describe the impact clearly.
- Include reproduction steps.
- Note whether the issue requires local access, user interaction, or a malicious update source.
- Redact anything personal from logs or screenshots before attaching them.

Reports are handled on a best-effort basis by a solo maintainer; you will get a response in the advisory thread.

## Scope notes

A few facts that help judge impact:

- The app makes outbound connections to exactly two services, both over HTTPS: Apple Music's public web catalog (`music.apple.com` / `amp-api.music.apple.com`) for track format lookups, and GitHub (`api.github.com` / release downloads) for update checks via Velopack.
- There is no telemetry. Settings and diagnostics logs are written only to `%APPDATA%\WindowsLosslessSwitcher\` on the local machine.
- The updater is the most security-relevant surface: installed builds download and apply update packages from this repository's GitHub Releases. Releases are currently unsigned.
- The app requires no elevation; it changes audio device formats through standard Windows Core Audio APIs.
