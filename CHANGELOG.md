# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0-beta.6] - 2026-07-30

### Added

- Successful catalog matches are cached locally (`format-cache.json`), so
  repeat plays of the same track switch instantly without a network lookup.
  The cache is capped, versioned, scoped to the Apple Music storefront, and
  re-verified in the background on a configurable refresh interval
  (`formatCacheRefreshDays`); a "Clear catalog cache" button in the app
  window resets it. Contributed by @memoz.
- A toast notifies when a background re-check finds a track's format changed
  ("applies next playback").
- New `allowedSampleRates` setting: an allow-list of rates your physical
  hardware accepts. Resolved rates are clamped to the list (preferring the
  same 44.1/48 family) before being applied — for chains where a virtual
  cable or ASIO bridge between the switched endpoint and the real DAC
  reports rates the hardware can't lock to (#7). Proposed and prototyped
  by @mg42ns.

### Changed

- Tracks no resolver can identify now fall back to a conservative
  24-bit / 44.1 kHz instead of a format guessed from the Apple Music
  audio-quality tier (previously Hi-Res Lossless → 24/192, Lossless → 24/48).
  The tier only describes the account's ceiling, not the playing track's real
  rate; the old guess upsampled redbook tracks and misreported the rate as
  resolved. A toast (when enabled) now says the rate was undetermined.
- Catalog lookups use the Apple Music storefront matching the Windows region
  (previously always "us"), overridable with the new `appleMusicStorefront`
  setting. Invalid or unusable region codes fall back to "us".
- Catalog matching considers per-track performer credits and album-part
  metadata, improving matches for compilations and multi-artist albums.
- Local ALAC files are resolved to their true bit depth read from the ALAC
  magic cookie (the container's legacy field frequently claims 16-bit for
  24-bit Hi-Res files), and lossy local files apply the lowest supported
  device depth instead of inflating to the device maximum.
- Verbose per-operation diagnostics (catalog search attempts, playback
  probes) are now actually gated by `enableVerboseDiagnostics`; the setting
  previously had no effect.

### Fixed

- Update dialogs now show the full version including the prerelease suffix
  (previously "Version 1.0.0 is available" when offering 1.0.0-beta.N).

## [1.0.0-beta.1] - 2026-06-11

### Changed

- Format switches are now applied LIVE, under Apple Music's running render
  stream, instead of pausing playback first. Windows invalidates the active
  stream on the format change and Apple's media agent rebuilds it on its own
  within a few seconds — no pause, drain, or resume commands at all. Pausing
  first was the root cause of "zombie" playback: a stream invalidated while
  dormant is silently reused on resume and never renders again. The live
  switch works identically for same-family and cross-family (44.1↔48 kHz)
  changes.
- A track that needs a switch starts with a few muted seconds while the audio
  pipeline rebuilds, then plays from that point at the new format. The track
  is deliberately NOT restarted to recover the intro: the extra transition a
  restart creates is exactly the state a quick skip turns into a wedged
  renderer.
- Local files are resolved to the actual sample rate and bit depth read from
  Apple Music's cached media file. Replayed cache files (no active download)
  are now identified by the file Apple Music holds open, or one written right
  at track start — local tracks that previously fell back to "format unknown"
  now resolve and switch.
- Tracks no resolver can identify fall back to a format derived from the
  Apple Music audio-quality setting (e.g. Hi-Res Lossless → 24/192) and that
  format is applied, so the resolved and applied formats always match — the
  device no longer just stays wherever the previous track left it, and the
  old device-max jumps (e.g. 24/48 → 24/384) are gone entirely.
- UI status updates no longer run synchronously on the switching pipeline,
  removing up to half a second of wrong-format audio at each track change.
- App data (settings.json and logs) moved from `%LOCALAPPDATA%\WindowsLosslessSwitcher`
  to `%APPDATA%\WindowsLosslessSwitcher`; existing files are migrated
  automatically on startup. The old location is the Velopack install root, and
  a data folder created there by a dev or portable run made the installer
  report the app as already installed.

### Fixed

- Playback no longer dies after a format switch, and recovery no longer skips
  over strings of songs. The live-switch pipeline removes the zombie cause
  outright; switches into a wedged renderer are skipped to prevent cascades.
- The first moments of a track no longer play at the previous track's format:
  the target device is muted within milliseconds of a track change and unmuted
  once audio is confirmed at the new format. A user-set mute is never touched.
- Song endings are no longer cut short by the duration of the format switch:
  after the rebuilt stream produces audio, one muted pause/play realigns the
  audio with the timeline so Apple Music recomputes its end-of-track schedule
  from the current position.
- Switching never overrides a pause the user issued while a track was being
  resolved or while the rebuilt stream was coming up; a track detected while
  playback is paused gets its format applied silently.
- Audio confirmations read Apple's media agent session peak rather than the
  endpoint master meter, so audio playing from other applications on the same
  device can never mask a stall or falsely confirm a recovery.
- Stalled playback is detected and revived no matter when it happens: every
  track is health-checked at processing time, and a continuous watchdog samples
  playback every few seconds between tracks. Recovery escalates from a
  pause/play nudge to a skip-next, and — when failures repeat within a short
  window, the signature of Apple Music's renderer breaking down entirely — to
  an automatic Apple Music restart with a fresh media agent. The restart is
  bounded by a 10-minute cooldown and can be disabled with the
  `RestartAppleMusicOnPlaybackFailure` setting. Apple Music cannot restore its
  play queue across restarts, so the app says clearly when Play must be
  pressed after one.

## [0.1.0]

Initial release.

### Added

- Automatic shared-mode format switching for Apple Music for Windows, driven by
  Windows media session (GSMTC) track changes.
- Layered resolver pipeline: exact Apple Music catalog matching, device-max
  format for local files and unmatched tracks, and a tier-based fallback.
- Pause/confirm/drain orchestration around format changes: playback is paused,
  confirmed paused, and the endpoint confirmed silent before the device format
  is switched, then playback resumes.
- Default-device or pinned-device targeting, with bit-depth switching and
  closest-multiple sample-rate matching options.
- Tray-first UI with status display, diagnostics export, and optional switch
  toasts.
- Velopack-based installer with in-app update checks against GitHub Releases,
  plus portable packages for `win-x64` and `win-arm64`.
