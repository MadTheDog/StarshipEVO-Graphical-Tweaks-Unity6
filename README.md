# Sal's Graphical Tweaks v 2.0.1 — Unity 6

---

## Short introduction

Client-side graphics controls, glass fixes and rendering optimisations for Starship EVO. This edition targets **SEVO 26w45a, Steam build 26451593, on Unity 6000.6.6f1**.

---

## What it does

Find the controls under **Options → Graphics**. Changes are saved between sessions.

| Feature | What changes | Performance |
|---|---|---|
| Shadow distance | Adjust the full reach of sun shadows from 1 to 1500 world units. Includes a slider, numeric input and reset. | Longer ranges can soften nearby shadows and lower FPS. |
| Sun shadow resolution | Choose Game default, 512, 1024, 2048 or 4096 px. | Lower values reduce shadow-map work and make edges less crisp. |
| Sharp shadow range | Concentrate detail in the nearest shadow map. Smaller ranges sharpen a smaller area. | Uses the existing shadow maps. |
| Decal visibility | Extend the visibility of small decals with a 1–10× multiplier. | Higher values can increase work in decal-heavy views. |
| Glass sunlight occlusion | Reduces direct glare on glass where the sun is blocked and the pane has shadow coverage. | Adds a lighting correction pass for eligible glass. |
| Seamless glass | Hides covered internal faces across eleven borderless glass shapes, including supported tetra and hepta joins. Also handles matching pipe end caps. | Large window collections still need benchmarking. |
| Remove baked glass horizon | Clears the moving blue band from SEVO’s baked reflection on nearby glass. | Reuses the native transparent draw. |
| Neutral glass sheen | Adds a neutral reflection with Off, Low, Medium and High presets. Requires baked horizon removal. | Reuses the native transparent draw. |
| Renderer optimisation | Skips GPU visibility work for empty render passes. | Savings depend on how many passes are empty. |
| Ambient occlusion optimisation | Clears duplicate AO passes and stale work after AO is disabled. | Reduces redundant work. |
| Decal CPU optimisation (experimental) | Skips some off-screen decal work during quick camera turns. Off by default. | May reduce CPU work while turning. Idle CPU use can rise. |

### What was tested

**v2.0.1 / 26w45a, Steam build 26451593:**

- Startup, normal exit, graphics menu rebuilds, numeric callbacks and resets in an isolated creative world.
- Native shadow distance, cascade split and resolution setters; queued decal range changes; renderer dispatch activity and AO attachment ownership.
- All five native feature contracts passed. The live glass catalog admitted all twelve supported shapes, and a 300-block glass snapshot completed with no pending updates or reported faults.
- 224 GPU comparison cases passed against the updated game's transparent pixel shaders using the released mod shader bundle.
- Regression checks covered unrelated assembly and asset changes, incompatible methods and layouts, shader changes and independent feature rejection.

[Compatibility checks and the 6 November recheck](docs/COMPATIBILITY.md).

Cockpit visual comparisons and FPS measurements have not been repeated on 26w45a.
The earlier **v2.0.0 / 26w44a** tests covered blocked sunlight glare, rounded
glass corners, the moving blue band, neutral sheen and 10× decals. A player
visual check confirmed seamless transparent pipe joins on that version.

The **v2.0.0 / 26w44a** benchmark on one detailed ship at 1440p averaged **72.4 FPS** at
Vanilla MAX, **74.3 FPS** with the tweaks enabled at vanilla ranges, and
**69.9 FPS** with 1500-unit shadows and 10× decals. The runs changed groups of
settings together. [Settings, 1% lows and test method](docs/PERFORMANCE.md).

---

## Installation requirements

- **Starship EVO 26w45a**, Steam build **26451593**, Unity **6000.6.6f1**.
- **64-bit Windows**, the game's **Mono** player and **Direct3D 11**.
- **BepInEx 5 for Windows x64**. Tested with [5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).
- For glass sunlight occlusion, use sun shadows and the game’s HDR/Linear rendering path.

---

## How to install

1. Close Starship EVO and back up any world you plan to use.
2. In Steam, open **Starship EVO → Manage → Browse local files**. This is the folder containing `Starship EVO.exe`.
3. Extract BepInEx 5 for Windows x64 beside the executable. Launch SEVO once, then close it to generate the BepInEx configuration.
4. In `BepInEx/config/BepInEx.cfg`, set the existing `HideManagerGameObject` key under `[Chainloader]` to `true`.
5. Download `Sals-Graphical-Tweaks-Unity6-2.0.1.zip` from [Releases](https://github.com/MadTheDog/StarshipEVO-Graphical-Tweaks-Unity6/releases/latest).
6. Extract the ZIP’s `BepInEx` folder into the game folder. The files should land here:

   ```text
   Starship EVO/
   ├── Starship EVO.exe
   └── BepInEx/
       └── plugins/
           └── GraphicalTweaks/
               ├── GraphicalTweaks.dll
               └── graphicaltweaks-glass.bundle
   ```

7. Launch SEVO and open **Options → Graphics**. The mod’s settings are at the bottom.

When updating, close the game and replace both files together. Keep one installed copy of Graphical Tweaks. The mod saves settings in `BepInEx/config/local.sevo.graphicaltweaks.cfg`.

## How to uninstall

1. Close Starship EVO.
2. Delete `BepInEx/plugins/GraphicalTweaks/`.
3. To clear the mod’s saved preferences, delete `BepInEx/config/local.sevo.graphicaltweaks.cfg`.

---

## Notes

- Back up an existing world before trying the mod. The tweaks apply to local rendering and client preferences.
- Multiplayer, sustained remote building and stress tests with many grids or windows are pending.
- Native features check their methods, fields, buffer layouts and relevant shader data once at startup. A failed check disables the affected feature and logs its reason. Unrelated assembly changes can pass these checks; future releases still need testing.
- Shadow coverage and quality affect the sunlight correction. Decal range depends on projected size, receiver depth and tile limits.
- Unofficial mod, unaffiliated with Moonfire Entertainment.
- [Building from source](docs/DEVELOPMENT.md).

---

## License

Copyright © 2026 Sal. Released under [MIT-0](LICENSE). Use, modify, redistribute or sell it freely. Credit to **Sal** is appreciated and optional.
