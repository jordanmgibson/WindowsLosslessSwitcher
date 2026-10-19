# Development Guide

This document explains the internal architecture of Windows Lossless Switcher for contributors and maintainers.

## UI verification

After the Release build and regression tests, run the isolated WPF harness:

```powershell
dotnet run --project .\tests\WindowsLosslessSwitcher.UiVerification\WindowsLosslessSwitcher.UiVerification.csproj --configuration Release
```

It loads compiled application views without starting the app or its audio services,
checks intro/correction bindings, layout bounds, focus resources, and appearance
palettes, and writes PNG previews under `artifacts/ui-verification`. Preview data
is synthetic. CI also runs this harness. See [UI_DESIGN.md](UI_DESIGN.md) for its
scope and the remaining manual checks.

## Architecture Overview

The app is a tray-first WPF application built on .NET 8. It uses no dependency injection container — all services are composed manually in `App.xaml.cs`. This keeps the startup graph explicit and traceable at a glance.

```
App.xaml.cs
 ├─ AppleMusicTrackSource       — watches Apple Music via Windows GSMTC APIs
 ├─ SwitchingCoordinator        — orchestrates resolution → muted live switch → rebuild → optional intro replay
 │   ├─ ResolverChain           — runs resolvers in priority order until one succeeds
 │   │   ├─ FormatCacheResolver         — returns persisted catalog results before network lookup
 │   │   ├─ AppleMusicCatalogResolver   — queries Apple Music catalog for exact format
 │   │   ├─ LocalDeviceMaxResolver      — local files: reads the true format from the PlayCache file
 │   │   │   └─ PlayCacheTrackFormatReader — locates and reads Apple Music's cached media files
 │   │   └─ TierFallbackResolver        — terminal safety net; never drives a live switch
 │   └─ CoreAudioEndpointController    — reads/writes shared-mode device formats, peak meter,
 │                                       endpoint mute, and per-process audio session state
 ├─ TrayIconHost                — system tray icon and context menu
 ├─ AppUpdater                  — checks GitHub Releases and manages Velopack update lifecycle
 └─ MainWindowViewModel         — binds SwitchingCoordinator status to the settings UI
```

---

## Key Data Flow

### 1. Track detection (`AppleMusicTrackSource`)

`AppleMusicTrackSource` subscribes to the [Global System Media Transport Controls (GSMTC)](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager) session manager. When Apple Music changes the active track, `MediaPropertiesChanged` fires and a `TrackSnapshot` is published via the `ITrackSource.TrackChanged` event.

Two debounce mechanisms prevent spurious events:

- **Placeholder debounce** — Apple Music briefly emits `"Connecting…"` while resolving the track. If metadata looks like a placeholder, publishing is delayed 2 seconds to allow the real track to arrive first.
- **Session loss debounce** — The GSMTC session can transiently disappear during Apple Music restarts. Detaching is deferred 3 seconds to avoid needlessly dropping the session during a blip.

### 2. Format resolution (`ResolverChain`)

`SwitchingCoordinator` first checks saved native format corrections for the normalized title, artist, and album. A matching valid correction bypasses the catalog/cache chain. Otherwise, `ResolverChain` iterates resolvers in order, returning the first non-null result. `FormatCacheResolver` first checks the versioned, storefront-specific catalog cache. On a miss, the catalog resolver acts as the authoritative cloud-vs-local test: a confident catalog match (exact title and artist) means the track is an Apple Music catalog track and we use its exact manifest format. When the catalog does not confidently match — local-library files, or any track Apple Music can't identify — the chain falls through to `LocalDeviceMaxResolver`, which reads the actual format from the track's PlayCache file. This avoids a local file that merely shares metadata with a catalog track being forced to the cloud format, because only an exact title+artist match clears the catalog's acceptance threshold.

| Resolver | Strategy | Confidence |
|---|---|---|
| `FormatCacheResolver` | Returns a cached catalog result immediately. Entries are invalidated when catalog resolver semantics change and stale entries are verified in the background. | `Exact` |
| `AppleMusicCatalogResolver` | Searches the Apple Music catalog API by track metadata, then fetches the HLS manifest for the best match to read `SAMPLE-RATE` and `BIT-DEPTH` attributes directly. Only a high-confidence match (exact title + exact artist) is accepted; weaker matches return `null` so the track is treated as local. | `Exact` |
| `LocalDeviceMaxResolver` | Runs after the catalog. Locates the track's file in Apple Music's PlayCache via `PlayCacheTrackFormatReader` (TagLibSharp) and resolves to the file's true sample rate, with the best bit depth the device supports at that rate. Files are matched three ways, in confidence order: the in-progress download folder named after the track, the file Apple Music currently holds OPEN (replays of completed cache files), and a file written right at track start. When no file can be identified, it returns `null` and the chain falls through to the tier fallback — switching beyond the file's own rate gains nothing in shared mode, and the extreme device-max jumps it used to make (e.g. 24/48 → 24/384) destabilized Apple Music's renderer. | `Exact` |
| `TierFallbackResolver` | Terminal marker: the audio-quality preference does not determine a track's actual format. The coordinator retains the endpoint format and reports Undetermined; the legacy numeric fields are not applied or displayed as a detected rate. | `Tier` |

### 3. Format switching (`SwitchingCoordinator`)

`SwitchingCoordinator` processes one track at a time behind a `SemaphoreSlim`. Each incoming track cancels the previous one (generation-based cancellation). The full switching cycle has these stages:

```
TrackChanged event received
  │
  ├─ Acquire switch lock and transfer an inherited endpoint mute
  ├─ Select target device (default or pinned)
  ├─ Intro replay on? → mute pending lookup; inspect synchronous results before muting
  ├─ Resolve saved native correction, or use ResolverChain
  ├─ Unknown rate? → retain the endpoint format and report Undetermined
  ├─ Evaluate device capabilities; immediately known active formats need no mute or restart
  ├─ Paused? → defer the device change until a Playing event or watchdog recheck
  ├─ Require the media agent's render stream to remain active for 1.5 seconds
  │   └─ Never sustains? → skip the device change and check playback health
  ├─ Ensure the live device-format change is muted, preserving user-owned mute
  ├─ Apply and verify the endpoint format; wait for real audio in the rebuilt stream
  ├─ Realign with a short pause; optionally restart the same recording at 0:00
  │   ├─ Confirm seek by identity and timeline position
  │   └─ Previous fallback requires the same paused recording at ≥5 seconds
  ├─ Unmute before Play; if no safe restart is confirmed, keep the current position
  ├─ Rebuild unconfirmed? → existing bounded recovery and escalation ladder
  └─ Between tracks, the watchdog detects stalled render streams and missed resumes
```

With intro replay on, preparation is muted before format resolution so the listener
does not hear the opening and then a deliberate restart. With replay off, resolution
and render-stream startup remain audible and only the actual device change is muted.
`RewindAfterFormatSwitch` defaults to on; unchanged-format tracks and later watchdog
recoveries keep their position. A successful rewind is limited to once per track
generation. Cancellation transfers coordinator-owned pauses to the successor;
shutdown releases them before disposing the media-command session.

Following the default device queries that endpoint directly. Pinned selection still
enumerates available devices and preserves the missing-device result.

The format change still happens under a live renderer. Changing a dormant Apple
media-agent stream can leave it silent on resume, so paused changes are deferred.
The optional pause/restart occurs after the live switch and renderer rebuild.
These checks depend on Windows GSMTC state and per-process audio-session metering;
radio/autoplay sessions may disable both seeking and Previous.

### 4. Windows audio API (`CoreAudioEndpointController`)

`CoreAudioEndpointController` wraps NAudio's WASAPI bindings and a hand-rolled `PolicyConfigInterop` P/Invoke layer. On first use for a given device, it probes all candidate sample rate / bit-depth combinations via exclusive-mode `IsFormatSupported` and caches the results. Format application goes through `PolicyConfigInterop.SetDeviceFormat` (the undocumented `IPolicyConfig` COM interface), which is the same mechanism used by Windows' own audio control panel.

Beyond format management it exposes three runtime signals the coordinator depends on:

- **Master peak meter** (`GetMasterPeakValue`) — confirms silence before a switch and real audio after a resume. Reads pre-mute, so the gates keep working while the device is muted.
- **Endpoint mute** (`GetMasterMute` / `TrySetMasterMute`) — silences the pre-switch window. The coordinator never toggles a mute the user set themselves, transfers mute ownership across superseding tracks, and unmutes on every exit path including disposal.
- **Per-process audio session state** (`IsProcessSessionActive`) — reports whether Apple's media agent has a *running* render stream on the device, which drives the sustained-activity gate and the rebuild wait described above.

---

## Implementing a New Format Resolver

Create a class that implements `IFormatResolver` (`src/Abstractions/IFormatResolver.cs`):

```csharp
public interface IFormatResolver
{
    string Name { get; }
    Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken);
}
```

- Return `null` when your resolver cannot determine the format. The chain will fall through to the next resolver.
- Return a `ResolvedAudioFormat` when you have a result. Set `ResolutionConfidence.Exact` if you resolved from the actual stream manifest, or `ResolutionConfidence.Tier` if you mapped from a bit-rate approximation.
- Do not throw for transient failures (network errors, API timeouts). Catch and return `null` so the fallback chain continues.

Register your resolver in `App.xaml.cs` by adding it to the array passed to `ResolverChain`:

```csharp
var resolverChain = new ResolverChain([
    new AppleMusicCatalogResolver(logger),
    new LocalDeviceMaxResolver(audioEndpointController, new PlayCacheTrackFormatReader(paths, logger), logger),
    new YourNewResolver(logger),       // <── insert here at the priority you want
    new TierFallbackResolver(paths, plistReader, logger),
]);
```

---

## Update System

The app ships two update paths depending on how it was installed:

| Install type | Update mechanism |
|---|---|
| Installer (`.exe`) | [Velopack](https://velopack.io/) manages in-process update download and restart-to-apply. The app calls `UpdateManager.CheckForUpdatesAsync()` against a `GithubSource` pointing at the release channel. |
| Portable (`.zip`) | The app queries `GET /repos/{owner}/{repo}/releases/latest` on the GitHub API. When a newer version is found, it surfaces a button that opens the browser to the portable download URL for the correct architecture. |

The GitHub owner and repository are embedded in the assembly at build time via `WlsGitHubOwner` and `WlsGitHubRepository` in the `.csproj`. Override them at runtime with the `WLS_GITHUB_OWNER` and `WLS_GITHUB_REPOSITORY` environment variables for local testing against a fork.

---

## Running Tests

```powershell
dotnet test .\tests\WindowsLosslessSwitcher.Tests\WindowsLosslessSwitcher.Tests.csproj
```

The test suite uses xUnit. Tests that need to control timing or inject dependencies use the `internal` constructors exposed by `[assembly: InternalsVisibleTo("WindowsLosslessSwitcher.Tests")]` in `src/Properties/AssemblyInfo.cs`.

Tests that exercise Windows audio or GSMTC APIs are integration tests that require a real Windows session; they are not included in the automated suite. Behavior that can be isolated (resolver logic, session selection, track normalization, scoring) is covered by unit tests.

---

## Apple Music Catalog Token

`AppleMusicCatalogResolver` obtains a short-lived developer JWT by:

1. Fetching `https://music.apple.com/{storefront}/browse`
2. Locating the main JavaScript bundle via a `src="/assets/index*.js"` pattern
3. Extracting the first JWT-shaped string (`eyJ…`) from the bundle

This token is an Apple Music web-player credential embedded in the public bundle. It is not a secret and rotates on Apple's schedule. The resolver caches it until 5 minutes before its `exp` claim and re-fetches automatically. If Apple changes the bundle structure, this scraping logic will need updating — search for `GetDeveloperTokenAsync` in `AppleMusicCatalogResolver.cs`.

---

## Diagnostics

`DiagnosticsLogger` writes timestamped entries to `%APPDATA%\WindowsLosslessSwitcher\logs\`. App data deliberately lives under `%APPDATA%` rather than `%LOCALAPPDATA%`: the Velopack installer owns `%LOCALAPPDATA%\WindowsLosslessSwitcher` as its install root, and a data folder created there by a dev/portable run makes Setup.exe think the app is already installed. `AppDataPaths` defines both locations and migrates legacy data on startup. The "Export diagnostics" tray menu item zips the current log files for easy sharing. Each format-apply operation records before/after device formats, the verification source, and the number of polling attempts, making it straightforward to diagnose why a switch did or did not produce the expected result without needing to attach a debugger.
