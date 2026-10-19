# GitHub issue coverage

Reviewed the repository's four open bug reports and the closed allow-list request,
including all comments and the public diagnostic logs attached to #2 and #4.
This describes code and regression coverage; it does not claim that every reporter's
DAC or Apple Music installation has been retested.

| Issue | Result and limits |
|---|---|
| [#1 — Bit depth stuck at 16 bit](https://github.com/jordanmgibson/WindowsLosslessSwitcher/issues/1) | The upstream fix is already present: packed/padded 24-bit PCM, 24 valid bits in a 32-bit container, and positioned/unspecified channel masks are probed. Existing tests cover these layouts and property-store valid-bit parsing. The reporter's FiiO/JDS hardware has not been retested here. |
| [#2 — Same-rate transition blanking](https://github.com/jordanmgibson/WindowsLosslessSwitcher/issues/2) | Immediately available cache/native/local results are evaluated before muting. Matching formats never apply a device change, pause, seek, or mute. Consecutive cached same-format tracks have regression coverage. A first lookup that needs the network can still be muted when intro replay is enabled; that is the full-intro preparation policy. Turning replay off keeps lookup audible. |
| [#3 — Album alternates between 48 and 96 kHz](https://github.com/jordanmgibson/WindowsLosslessSwitcher/issues/3) | Unknown-rate fallback no longer changes the endpoint to a guessed 44.1/48 kHz. Catalog search allows album-specific queries to beat a same-title/artist result from another edition. Tests cover both protections. The report contains no track examples or logs, so its exact original cause is unconfirmed. Different tracks can legitimately have different formats; the app does not force an album-wide rate. |
| [#4 — Wrong reported sample rate](https://github.com/jordanmgibson/WindowsLosslessSwitcher/issues/4) | The attached log's Japanese/romanized artist mismatch is reproduced and fixed with the verified equivalence 苺りなはむ ↔ ICHIGORINAHAMU. Matching compares complete canonical credit sets, including reordered credits, while retaining the score threshold and rejecting unrelated names. The reported Japanese and Chinese metadata have deterministic manifest/storefront tests. Other native-player/web-master differences still require per-recording native correction; the app does not inspect Apple's encrypted native stream. |
| [#7 — Allowed sample rates](https://github.com/jordanmgibson/WindowsLosslessSwitcher/issues/7) | Already shipped upstream and confirmed by the reporter. The settings UI and existing allow-list selection/sanitization tests are retained. |

## Catalog identity and cache behavior

The artist alias above is confirmed by the artist's label's
[official profile](https://www.jvcmusic.co.jp/-/Profile/A027029.html) and
[official artist page](https://www.jvcmusic.co.jp/-/Artist/A027029.html).
It applies only to a complete artist credit. No general Japanese-to-Latin guess
or global lowering of the catalog matching threshold is used. Other unverified
artist aliases remain unresolved rather than being guessed.

The catalog resolver version is now 3, invalidating results and cached no-matches
produced by the older matching rules. Otherwise an old negative entry could hide
the artist-match fix for 30 days. Native format corrections live in settings and
are preserved.

When the track rate is unknown, the UI says **Undetermined** and keeps the actual
device format visible. It no longer says the fallback rate matches the track.

## Live catalog check

An isolated lookup using the modified resolver, without starting playback or
touching user settings, resolved both reported metadata cases against Apple's
live catalog:

- Taiwan: 心、妄想色に / Sān-Z, HOYO-MiX, BPM15Q & 苺りなはむ →
  **24-bit / 48 kHz**, catalog song `1887463315`.
- China: 我没有远方 / 罗琦 / 随心所欲 → **16-bit / 44.1 kHz**,
  catalog song `1630325502`.

These are catalog-manifest results. They do not measure the native player's
encrypted stream or establish the output of the reporters' DACs.

## Remaining verification

Manual testing should cover FiiO/JDS/TEAC and other reported DACs, first uncached
plays versus cached replays, same-rate album transitions, mixed-rate albums,
pause/resume, quick skips, and native-player format differences. For #3, exact
recordings and a current diagnostic export are needed to confirm its original
symptom rather than assuming every track should be 96 kHz.
