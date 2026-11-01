# VRAM RipperDoc - Performance Optimizer

**Find out which of your Cyberpunk 2077 texture and mesh mods are eating your VRAM, and what to cut to fit your GPU.**

Script profilers show what redscript and CET mods cost. Nothing showed what *asset* mods (`.archive` files) cost, until now. VRAM RipperDoc scans every archive mod you have installed and ranks them by:

- **Added VRAM:** the GPU data of each texture and mesh, minus what the vanilla file it replaces already used
- **Geometry:** LOD0 triangles, missing LODs, "N× vanilla triangles"
- **Texture problems:** 4K/8K, no mipmaps, uncompressed formats, "16× vanilla pixels"
- **Dead weight:** files that lose a load-order conflict and never load
- **Broken archives:** truncated or corrupt downloads

Then it suggests a **VRAM budget** for your GPU and lists exactly which textures to halve to fit it.

It is **read-only**: nothing in your game folder is changed.

See [COMPARISON.md](COMPARISON.md) for how this differs from Ultra+, FrameFlow, RedProfiler, Universal Texture Downscaler and others.

## Install

**Vortex / MO2:** install the zip like any other mod. It lands in `<Cyberpunk 2077>\tools\VRAM RipperDoc\`.

**Manual:** extract the zip into your Cyberpunk 2077 folder (the one that contains `bin` and `archive`).

Requirements: Windows 10/11 x64. No .NET install is needed (the exe is self-contained). The tool uses the game's own `oo2ext_7_win64.dll` to read compressed headers. Oodle is not bundled.

## Use

Double-click **`tools\VRAM RipperDoc\VramRipperdoc.exe`**. It finds the game, scans, prints a summary and opens `VRAM-RipperDoc-Report.html`.

```
VramRipperdoc.exe [options]
  --game <dir>       Cyberpunk 2077 folder (auto-detected for Steam/GOG/Epic, or when run from inside it)
  --budget <GB>      VRAM your mods may add on top of vanilla (default: suggested from your GPU)
  --out <dir>        Where to write the HTML/JSON report (default: next to the exe)
  --hashes <file>    Optional list of depot paths (e.g. WolvenKit's archivehashes.csv) for nicer names
  --top <n>          Rows to show in the console (default 25)
  --no-vanilla       Skip comparing against the base game archives (faster, less accurate)
  --no-open          Don't open the HTML report when done
  --json             Print the JSON report to stdout (for scripts and mod managers)
```

### Reading the report

| Column | Meaning |
|---|---|
| **+VRAM** | Estimated GPU memory this archive adds if everything in it is on screen. Negative means it is lighter than vanilla. |
| **Tex / Mesh** | Texture and mesh counts in the archive |
| **Tris LOD0** | Triangles at full detail, summed over meshes that actually load |
| **Overrides** | Files that replace a vanilla file |
| **Never loads** | Files another archive wins in load order (pure dead weight) |
| **Flags** | Files with a warning such as 4K+, no mipmaps, uncompressed, no LODs or very dense |

Click a mod to see its heaviest files.

### Applying the plan

v0.1 is advisory. To apply a suggestion:
1. Many big texture mods ship 1K/2K variants. Reinstall the lower one for the mods at the top of the plan.
2. Or open the archive in [WolvenKit](https://wiki.redmodding.org/wolvenkit), export the listed `.xbm`, downscale and re-import.
3. Re-run RipperDoc and compare, then benchmark in game.

## How it works

1. Reads the game's load order: `archive/pc/mod` in ASCII order (or `modlist.txt`), then `mods/*/archives` (REDmod). The first archive wins a conflict.
2. Parses each RDAR archive index (hashes plus segment sizes) and decompresses only each file's CR2W header with the game's Oodle DLL.
3. **Textures:** reads `rendRenderTextureBlobPC` (size, mip count, slices) and the `CBitmapTexture` compression. The uncompressed buffer size is the GPU footprint.
4. **Meshes:** reads `rendRenderMeshBlob` chunk infos (index counts and LOD masks) for triangles and LODs. Vertex and index buffer size is the GPU footprint.
5. Indexes the vanilla `content` and `ep1` archives (around 550k files, under a second) to compute the delta for overrides and to compare resolution and triangles with vanilla.
6. Rebuilds file paths from the CR2W import tables of every scanned file (FNV-1a64 of the depot path).

Tested on game version 2.x archives: 34,380 textures and 21,410 meshes from `basegame_4_appearance` and `basegame_1_engine` parsed with 0 failures. A 8.7 GB scan takes about 40 s.

## Roadmap

- **Stage 2, budget mode:** generate a separate, lighter `zz_RipperDoc_Budget.archive` with the planned textures downscaled (originals untouched; mesh-matched character mods flagged, not changed).
- **Stage 3, live tracker:** a RED4ext plugin that shows which assets actually load in the current scene.

## Build

```
dotnet publish src/VramRipperdoc -c Release -o dist/bin
```

Developer self-test: `VramRipperdoc --make-fixtures <dir>` builds fake mod archives from real game files: conflicts, overrides, new assets, a corrupt archive and a REDmod. Then run `VramRipperdoc --mod-root <dir>`.

## License

MIT. Not affiliated with CD PROJEKT RED. Cyberpunk 2077 is a trademark of CD PROJEKT S.A.
