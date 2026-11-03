# Building from source

Player installation is in the README. Source builds need .NET SDK 8, Unity
Editor 6000.6.6f1 with Windows build support, the supported SEVO installation
and the BepInEx 5 core assemblies. Use your local game installation for build
references.

## Build the shader bundle

```powershell
./tools/Build-Shaders.ps1 -EditorPath 'C:/Path/To/Unity/Editor/Unity.exe'
```

This builds the mod’s own shaders in `dist/GlassRendering` and records the
Editor version in `build-info.txt`.

## Build and package

```powershell
./tools/Build.ps1 -GameDirectory 'C:/Path/To/Starship EVO'
```

The build finds BepInEx core under the given game folder. Use
`-LoaderDirectory` to point it at a separate BepInEx core directory. The
portable behavior suites run by default. `-NoRestore` reuses restored
dependencies. `-SkipTests` is available for documentation-only iterations.

The output directory defaults to `dist/GraphicalTweaks`. It contains the two
plugin files under `BepInEx/plugins/GraphicalTweaks`, alongside the README,
license and checksums. Several production source files are linked from
`tests/`, so keep the source tree structure intact.
