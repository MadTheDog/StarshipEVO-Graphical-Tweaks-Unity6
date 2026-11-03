# Unity 6 performance notes

These results use Sal’s Graphical Tweaks v2.0.0 with SEVO 26w44a, Steam build
26394979, on Unity 6000.6.6f1 and Direct3D 11.

## Ship benchmark

Three manual passes used the same detailed ship in an isolated solo session at
2560 × 1440 on an NVIDIA GeForce RTX 3070 Laptop GPU. V-Sync and the FPS cap
were off. Native graphics settings matched across all three runs.
[Workshop ship](https://steamcommunity.com/sharedfiles/filedetails/?id=2406891182)

| Run | Shadow distance / resolution / sharp range | Decals | Tweaks | Average FPS | 1% low FPS | P95 / P99 frame time |
|---|---|---|---|---:|---:|---:|
| 1 · Vanilla MAX baseline | 150 units / Game default / Game default | 1× | Optimisations and glass tweaks off | 72.4 | 58.6 | 16.0 / 16.5 ms |
| 2 · Tweaks, vanilla ranges | 150 units / Game default / Game default | 1× | Optimisations and glass tweaks on | 74.3 | 59.2 | 15.8 / 16.3 ms |
| 3 · Extended ranges | 1500 units / 4096 px / 15 units | 10× | Same as Run 2 | 69.9 | 45.6 | 18.3 / 19.4 ms |

The v2.0.0 plugin and local timing harness were loaded for all three runs.
Runs 2 and 3 enabled renderer optimisation, AO optimisation, experimental decal
CPU optimisation, glass sunlight occlusion, seamless glass and baked horizon
removal. Neutral sheen was off throughout.

Run 2 averaged **2.6% higher FPS** than Run 1 and recorded **42,637 skipped
empty-pass dispatches** over the full capture. This comparison covers the
combined change. Run 3 averaged **6.0% lower FPS** than Run 2, with a **22.9%
lower 1% low**. Shadow distance, shadow resolution, sharp shadow range and
decal range all changed together in Run 3.

## Measurement and scope

- The timing harness recorded consecutive game frame intervals. It excluded
  out-of-focus frames and the first frame after refocusing from the FPS summary.
  The three runs contributed 6,000, 5,673 and 5,090 focused frames, respectively.
- Average FPS is frame count divided by elapsed time. The 1% low is the FPS
  equivalent of the mean interval of the slowest 1% of frames.
- Unity’s separate CPU and GPU timing feature was disabled in this game build.
  The frame intervals measure total frame time. The dispatch counters measure
  how many calls the renderer issued or skipped.
- The figures describe one manual pass per configuration on one ship. Individual
  feature costs, repeated routes and isolated rapid mouse-look at 10× decals
  remain open.
