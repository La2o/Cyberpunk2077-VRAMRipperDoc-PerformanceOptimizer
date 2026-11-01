# How VRAM RipperDoc differs from other performance mods

Most Cyberpunk 2077 performance mods tune the **engine** (INI and path-tracing settings), **scale** quality at runtime, or profile **scripts**. None of them measure what your **texture and mesh mods** cost. That is the gap VRAM RipperDoc fills.

It does not compete with the tools below. Run it alongside them.

## At a glance

| | What it looks at | Per-mod blame? | Changes your game? | Where VRAM RipperDoc differs |
|---|---|---|---|---|
| **VRAM RipperDoc** | `.archive` asset mods: textures, meshes, conflicts, missing references, real in-game VRAM | **Yes:** per archive, per installed mod and per file | Scan is read-only; the optional budget archive is one separate, deletable file | — |
| [Ultra+](https://www.nexusmods.com/cyberpunk2077/mods/10490) | Engine/path-tracing config, frame timing, stutter | No | Yes (engine settings) | Ultra+ tunes the engine. RipperDoc shows which *asset mods* eat the VRAM that those settings have to work with. |
| [OAI Tweaks Suite](https://www.nexusmods.com/cyberpunk2077/mods/23257) | Engine tweaks for 1% lows and CPU load | No | Yes | Targets CPU and frame pacing. RipperDoc targets GPU memory from mods. |
| [PinkStone PathTracing](https://en.gamegpu.com/news/igry/mod-pinkstone-pathtracing-povyshaet-fps-v-cyberpunk-2077-na-videokartakh-radeon) | Ray/bounce counts (Radeon) | No | Yes | Trades lighting quality for FPS. RipperDoc trades only the mod assets you choose. |
| [FrameFlow CPU](https://www.nexusmods.com/cyberpunk2077/mods/33726) / [FrameFlow Graphics](https://www.nexusmods.com/cyberpunk2077/mods/33724) | Crowd density / dynamic resolution at runtime | No | Yes (runtime scaling) | They react to low FPS after it happens. RipperDoc finds the asset cause before you launch. |
| [Adaptive Graphics Quality](https://www.nexusmods.com/cyberpunk2077/mods/2920) | Switches presets by context (combat, driving) | No | Yes | Lowers *everything*. RipperDoc points at the specific files worth lowering. |
| [RedProfiler](https://www.nexusmods.com/cyberpunk2077/mods/34223) / [G's TOTAL Profiler](https://github.com/giorgioherma/Gs-Cyberpunk2077TotalProfiler) | Redscript/CET script time per mod | Yes, for **scripts** | No | Script profilers can't see `.archive` assets. RipperDoc is the asset-side equivalent. |
| [0-Engine enhanced mods](https://www.nexusmods.com/cyberpunk2077/mods/28443) / [EXRC Runtime Controller](https://www.nexusmods.com/cyberpunk2077/mods/28727) | Script load (per-frame checks, Lua GC) | No | Yes | CPU and script load, not VRAM. |
| [Advanced Crash Reporter](https://www.nexusmods.com/cyberpunk2077/mods/34403) | Crash call stacks | Only when a mod is in the stack | No | When no mod is in the stack, it can only say an archive mod *may* be responsible. RipperDoc names the heavy, conflicting or broken archives. |
| [Universal Texture Downscaler](https://www.nexusmods.com/site/mods/2298) | All textures, capped globally at load time | No | Yes (every texture) | A global cap blurs vanilla and good mods too. RipperDoc ranks per mod, so you only shrink the worst offenders. |
| [CP2077 Texture Merger](https://www.nexusmods.com/cyberpunk2077/mods/32311) | Texture conflicts between mods | Per conflict | Yes (merged output) | Picks the *better* texture. RipperDoc reports the *cost* of every texture and mesh, plus conflicts whose losing copy never loads. |
| Update 2.5 DLSS 4.5 Multi Frame Generation | Frame generation | — | — | Generated frames don't free VRAM. Frame generation itself uses more of it, which makes a mod VRAM budget *more* relevant. |

## What only VRAM RipperDoc does (from what we could find)

1. **Per-mod VRAM ranking.** Each archive's GPU data (texture mips and mesh vertex/index buffers) is measured, and the cost of the vanilla file it replaces is subtracted. A 4K retexture that replaces a 2K vanilla texture shows the *extra* cost, not the full size.
2. **Geometry.** LOD0 triangle counts, LOD counts and "N× vanilla triangles" per mesh, so dense clothing and body mods without LODs stand out (these also hurt ray tracing).
3. **Conflict waste.** It replays the game's load order (`archive/pc/mod` in ASCII order or `modlist.txt`, then REDmod). Files that lose a conflict are marked *never loads*: disk space and install clutter with zero effect.
4. **Readable names.** Archives store only hashes. RipperDoc rebuilds paths from the import tables inside the mods' own files, without needing an external hash database.
5. **Budget plan.** It suggests a mod budget from your GPU's VRAM (or you set one with `--budget`), then lists exactly which textures to halve to fit, with the saving for each.
6. **Broken archive and missing-requirement detection.** Truncated or corrupt `.archive` files are named, as are mods that reference files that aren't installed (a missing requirement). Only references the mod *added* count, so vanilla's own dead links never cause false alarms.
7. **Selective, reversible fix.** `--make-budget-archive` halves only the planned textures by dropping their top mip, using the game's own smaller mips with no re-encoding. It goes into one extra archive that loads first. Your mods are untouched, and deleting that file undoes it. This was tested in game on 2.5.
8. **Real measurements.** `--live` records the game's actual dedicated VRAM using Windows performance counters (the same source as Task Manager). It compares sessions so you see before and after, not just estimates.
9. **Knows your setup.** It reads your Vortex or MO2 install to group archives by mod and link their Nexus pages, and reads your in-game settings to size the budget.
10. **Housekeeping.** It finds files identical to vanilla (they do nothing), duplicate copies across mods, and likely unused leftovers. It shows what changed since the last scan and gives you a copy-paste summary for support threads.

## Honest limits

- **Estimates, not live usage.** The game streams assets, so a 4K texture you never see costs nothing in practice. The numbers are an upper bound for "everything this mod adds is on screen". Benchmark before and after any change.
- **Biggest win on 8–16 GB cards** with heavy texture or clothing mods. If you aren't VRAM-limited you won't gain FPS (the Environment Textures Overhaul author says the same about ETO).
- **The budget archive halves textures; it doesn't touch meshes.** Dense meshes are flagged for you to swap or remove. A live per-asset tracker (RED4ext) is still on the roadmap.
- The comparison table above is based on each mod's public description. Nexus blocked most direct page reads during research, so tell us if something is out of date.
