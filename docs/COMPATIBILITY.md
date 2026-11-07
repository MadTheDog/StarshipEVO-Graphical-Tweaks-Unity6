# Compatibility review — 6 November 2026

**Target: Steam build ID 26451593 / 26w45a, Unity 6000.6.6f1.**

The Steam client's available-branch metadata lists 26451593 for `unity6`.
The installed manifest independently reports that build and target build,
a completed update and depot manifest `2594540325782390880`. The release name
is verified by the developer's [26w45a announcement](https://github.com/tsunamayo/Starship-EVO/issues/8106).
The engine version is recorded in the native asset header.

The v2.0.1 implementation already accepts this build. This review required no
runtime code change or version bump.

## Automated verification

- All 18 suites passed; the plugin built against the installed 26451593
  assemblies with zero warnings and errors.
- A local static audit linked the production catalog and fingerprint code.
  All **353 contracts** passed: renderer 31, AO 4, decal visibility 76,
  decal projection 55 and glass bootstrap 187. The native transparent pass
  also matched the approved shader contract.
- Changing only the actual assembly's MVID in memory still admitted every
  feature. Changing a required renderer signature rejected only the renderer;
  restoring the signature admitted every feature again. No mutated assembly
  was written to disk or executed.
- The same static audit passed against the retained **26394979 / 26w44a**
  fixture. This does not establish v2.0.1 runtime behavior on that older build.
- The portable suites include 19 native-contract regression checks and 30
  runtime-lifetime checks. Run them through the normal
  [source build](DEVELOPMENT.md#build-and-package).

## Compatibility behavior

Native checks are cached at startup and scoped to each feature's required
methods, fields, constants, layouts and relevant shader programs. Assembly
identity, metadata token, shader object ID and local program blob index changes
can pass when the required contracts remain intact. Failed checks reject the
affected feature and log the reason.

Live glass geometry, material, buffer, snapshot and resource-ownership checks
remain in place. Method fingerprints are conservative: a relevant code change
can require review even if it preserves behavior. Future releases need checking.

## Runtime and performance limits

This review launched no game. The earlier 5 November 26w45a run checked
startup, menu callbacks and resets, rendering hooks, AO ownership, the
twelve-shape glass catalog and a current 300-block glass snapshot. Its
raster capture contained no supported glass samples, so it did not establish
visual quality. The separately recorded 224 synthetic GPU comparisons passed
against the updated native pixel shaders.

Fresh cockpit visual comparisons, multiplayer, large-world stress tests and
FPS measurements remain pending. The [published FPS measurements](PERFORMANCE.md)
belong to **v2.0.0 / 26w44a**; this review claims no additional FPS gain.

## Starship Keel coexistence

On 7 November 2026, the released **Graphical Tweaks v2.0.1** passed live
graphics checks alongside **Starship Keel v0.1.2** in audit mode. The isolated
windowed solo run used **Steam build ID 26451593 / 26w45a**, Unity 6000.6.6f1,
Windows x64 Mono, Direct3D 11 and BepInEx 5.4.23.5. The official SDK and runtime
archives matched their published SHA-256 checksums. Graphical Tweaks uses its
standalone BepInEx entry point, with Keel as an optional companion.
[Keel v0.1.2 release](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.2)

- Both plugins loaded. Seventeen native smoke checks and nine numeric and reset
  callbacks passed, and all thirteen graphics menu rows settled correctly.
- Renderer dispatches, one owned AO attachment on the player camera and native
  glass draw integration were observed. The glass catalog admitted twelve shapes,
  and the current snapshot tracked 278 blocks with zero pending updates or
  reported faults.
- Native save-on-exit hit the same Windows `File.Replace` error recorded in the
  5 November pre-Keel run. Clean save-on-exit remains unverified in the isolated
  fixture.

Coverage is Keel audit mode in solo play. Functional registration, cockpit
visual comparisons, multiplayer and FPS measurements remain outside this run's
coverage.
