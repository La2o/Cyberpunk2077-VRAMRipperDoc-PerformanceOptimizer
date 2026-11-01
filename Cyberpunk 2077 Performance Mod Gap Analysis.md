# Cyberpunk 2077 Performance Mod Gap Analysis

Nov 1, 2026 · @La2o

## Recommendation

Build a per-mod graphics budget tool that shows which installed asset mods use the most VRAM and geometry, then builds a lighter version. Skip another INI tweak, crowd scaler or script optimizer, because those areas are already crowded.

Nexus blocked most direct page reads, so this review relies mainly on search snippets. Discord and unlisted tools were not checked, so confirm the gap with the modding community before building.

## What's already covered

Engine tuning, automatic scaling, script profiling, script load and frame generation all have existing options.

| Area | Existing option | What it does |
| --- | --- | --- |
| Engine and path tracing tuning | [Ultra+](https://www.nexusmods.com/cyberpunk2077/mods/10490) | Tweaks path tracing, frame timing, memory and stutter |
| Engine and path tracing tuning | [OAI Tweaks Suite](https://www.nexusmods.com/cyberpunk2077/mods/23257) | Targets 1% lows and CPU load |
| Engine and path tracing tuning | [PinkStone PathTracing](https://en.gamegpu.com/news/igry/mod-pinkstone-pathtracing-povyshaet-fps-v-cyberpunk-2077-na-videokartakh-radeon) | Trades ray and bounce counts for FPS on Radeon |
| Automatic scaling | [FrameFlow CPU](https://www.nexusmods.com/cyberpunk2077/mods/33726?tab=posts) | Adjusts crowd density based on performance |
| Automatic scaling | [FrameFlow Graphics](https://www.nexusmods.com/cyberpunk2077/mods/33724) | Manages dynamic resolution |
| Automatic scaling | [Adaptive Graphics Quality](https://www.nexusmods.com/cyberpunk2077/mods/2920) | Switches presets by context, such as combat or driving |
| Finding slow scripts | [RedProfiler](https://www.nexusmods.com/cyberpunk2077/mods/34223) | Measures redscript and CET time per mod |
| Finding slow scripts | [G's TOTAL Profiler](https://github.com/giorgioherma/Gs-Cyberpunk2077TotalProfiler) | Links script activity to frame times |
| Reducing script load | [0-Engine enhanced mods](https://www.nexusmods.com/cyberpunk2077/mods/28443) | Replaces per-frame checks with timers and events |
| Reducing script load | [EXRC Runtime Controller](https://www.nexusmods.com/cyberpunk2077/mods/28727) | Adjusts Lua garbage collection at runtime |
| Frame generation | [Update 2.5](https://finalweapon.net/2026/10/21/cyberpunk-2077-update-2-5-patch-notes/) | Adds DLSS 4.5 Dynamic Multi Frame Generation |

## The gap: asset mods

Script profilers don't show what texture and mesh mods cost. [Advanced Crash Reporter](https://www.nexusmods.com/cyberpunk2077/mods/34403) has a similar blind spot: if no mod appears in the call stack, it says an archive mod may be responsible.

There are signs this cost matters:

- **Texture cost:** [Environment Textures Overhaul](https://www.nexusmods.com/cyberpunk2077/mods/13372) reports 9.4 GB of VRAM in vanilla and 10.9 GB with its 4K version.
- **Heavy modlists:** [one user with 152 mods](https://nexusmods.com/cyberpunk2077/mods/13024) reported drops that disappeared with only 20–40 mods installed. That is a single report, not a controlled test.
- **Mesh and texture bloat:** the [modding wiki](https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-creators-theory/3d-modelling/on-4k-textures-and-high-poly-meshes) warns that oversized assets can fill VRAM and cause stutter, while dense meshes can hurt ray tracing.

The closest tools don't solve this. [Universal Texture Downscaler](https://www.nexusmods.com/site/mods/2298) caps textures globally at load time, and [CP2077 Texture Merger](https://www.nexusmods.com/cyberpunk2077/mods/32311) picks the better version when mods conflict. No tool found ranks each mod's graphics cost and fixes only the worst offenders.

## What we'd build

The tool would ship in three stages, starting with a read-only scanner.

1. **Scanner:** read `archive/pc/mod` using WolvenKit and rank each mod by estimated VRAM, texture size, triangle count, missing LODs and overridden files that are never used.
2. **Budget mode:** let the player choose a target, such as 10 GB, then generate a separate lighter archive. Mesh-matched character mods would be flagged rather than changed automatically, and the originals would stay untouched.
3. **Live tracker (later):** add a RED4ext plugin showing which assets actually load in a scene. This is the riskiest part because it may need updates after game patches.

## Risks

The main risk is that the gain may be limited to players who are short on VRAM.

- **Limited audience:** the benefit is mainly for VRAM-limited players. ETO's author says there is no performance loss if you aren't VRAM limited. The likely audience is 8–16 GB cards with heavy texture or clothing mods.
- **Estimates aren't real usage:** the scanner shows what is installed, not what is on screen, so before-and-after benchmarks are needed.
- **Unproven gain:** test one heavy modlist before building the full tool. If the FPS or VRAM improvement is small, drop the idea.

## Next step

Start with the scanner on a real install. It needs the Cyberpunk folder connected through the desktop app, plus the test PC's GPU and rough mod count.

## Sources

- [Cyberpunk Ultra+](https://www.nexusmods.com/cyberpunk2077/mods/10490)
- [OAI Tweaks Suite](https://www.nexusmods.com/cyberpunk2077/mods/23257)
- [PinkStone PathTracing](https://en.gamegpu.com/news/igry/mod-pinkstone-pathtracing-povyshaet-fps-v-cyberpunk-2077-na-videokartakh-radeon)
- [FrameFlow CPU](https://www.nexusmods.com/cyberpunk2077/mods/33726?tab=posts)
- [FrameFlow Graphics](https://www.nexusmods.com/cyberpunk2077/mods/33724)
- [Adaptive Graphics Quality](https://www.nexusmods.com/cyberpunk2077/mods/2920)
- [RedProfiler](https://www.nexusmods.com/cyberpunk2077/mods/34223)
- [G's TOTAL Profiler](https://github.com/giorgioherma/Gs-Cyberpunk2077TotalProfiler)
- [0-Engine enhanced mods](https://www.nexusmods.com/cyberpunk2077/mods/28443)
- [EXRC Runtime Controller](https://www.nexusmods.com/cyberpunk2077/mods/28727)
- [Update 2.5 patch notes](https://finalweapon.net/2026/10/21/cyberpunk-2077-update-2-5-patch-notes/)
- [Advanced Crash Reporter](https://www.nexusmods.com/cyberpunk2077/mods/34403)
- [Environment Textures Overhaul](https://www.nexusmods.com/cyberpunk2077/mods/13372)
- [AMD SMT patcher comments](https://nexusmods.com/cyberpunk2077/mods/13024)
- [Modding wiki: 4K textures and high-poly meshes](https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-creators-theory/3d-modelling/on-4k-textures-and-high-poly-meshes)
- [Universal Texture Downscaler](https://www.nexusmods.com/site/mods/2298)
- [CP2077 Texture Merger](https://www.nexusmods.com/cyberpunk2077/mods/32311)
