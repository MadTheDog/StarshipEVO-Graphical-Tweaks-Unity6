# Unity 6 performance notes

These observations use SEVO 26w44a, Steam build 26394979, on Unity 6000.6.6f1
with Direct3D 11. The tests use an isolated solo world.

| Feature | Unity 6 observation | What it may save |
|---|---|---|
| Renderer optimisation | A live run recorded 42,504 skipped empty-pass dispatches alongside 115,233 issued dispatches. | Visibility work for passes with zero elements. |
| Ambient occlusion optimisation | The live camera had one owned AO attachment. Native lifecycle tests covered duplicate registration and removal. | Redundant AO work when duplicate or stale attachments would otherwise run. |
| Decal CPU optimisation | Available as an experimental option. | Some off-screen CPU work during rapid turns. Idle CPU cost can rise. |
| Sun shadow resolution | Native resolution setters and reset were exercised. | GPU shadow-map work at lower resolutions. |
| Baked horizon removal and neutral sheen | The in-game change uses the existing transparent draw. | Both options reuse that draw with a different reflection texture. |

The dispatch counters measure the number of calls. A controlled FPS comparison is
still pending for this build, including rapid camera turns with 10× decals.
Scene complexity, settings and hardware determine how these changes affect FPS.
