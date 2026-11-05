# Changelog

## 2.0.1

- Updated compatibility for SEVO 26w45a, Steam build 26451593.
- Replaced Unity6 assembly-ID gates with checks of each native feature's
  methods, fields and buffer layouts.
- Scoped glass shader validation to the transparent pass and its executable
  D3D11 programs. Other voxel shader passes can change independently.
- Added regression checks for unrelated assembly and asset-ID changes and
  incompatible method, layout and transparent-program changes.

## 2.0.0

- Ported the graphics settings, rendering optimisations and glass features to
  SEVO 26w44a on Unity 6000.6.6f1.
- Fixed sunlight correction showing through opaque hulls and updated the
  rounded glass corner.
- Added supported transparent pipe end-cap removal.
- Added baked glass horizon removal and optional neutral sheen.
- Expanded the Graphics tooltips with tradeoffs and dependencies.
