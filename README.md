# VRAM RipperDoc - Performance Optimizer

**Find out which of your Cyberpunk 2077 texture and mesh mods are eating your VRAM, fix the worst offenders with one command, and measure the result in-game.**

Script profilers show what redscript and CET mods cost. Nothing showed what *asset* mods (`.archive` files) cost. VRAM RipperDoc scans every archive mod you have installed and tells you:

- **Added VRAM per mod:** the GPU data of each texture and mesh, minus what the vanilla file it replaces already used
- **Geometry:** LOD0 triangles, missing LODs, "N× vanilla triangles"
- **Texture problems:** 4K/8K, no mipmaps, uncompressed formats, "16× vanilla pixels"
- **Dead weight:** files that lose a load-order conflict and never load, files identical to vanilla, duplicate copies across mods
- **Problems:** truncated or corrupt archives, and **missing references** (a mod points at a file that isn't installed, usually a missing requirement)
- **Possibly unused files:** new textures and meshes nothing references
- **Which installed mod each archive belongs to,** read from Vortex or Mod Organizer 2, with a link to its Nexus page

Then it:

- Suggests a **VRAM budget** from your GPU and your in-game settings (resolution, texture quality, RT/PT, frame generation)
- Lists which textures to halve to fit that budget, and can **apply the plan** as one separate, removable archive (`--make-budget-archive`)
- **Measures real VRAM** while you play (`--live`), so you can compare before and after
- Shows **what changed since your last scan**, and gives you a **copy-paste summary** for support threads

The scan is **read-only**. The only thing that ever writes to your game folder is `--make-budget-archive`, which adds one extra file and never modifies your mods. Delete that file and everything is back as it was.

See [COMPARISON.md](COMPARISON.md) for how this differs from Ultra+, FrameFlow, RedProfiler, Universal Texture Downscaler and others.

## Install

**Vortex / MO2:** install the zip like any other mod. It lands in `<Cyberpunk 2077>\tools\VRAM RipperDoc\`.

**Manual:** extract the zip into your Cyberpunk 2077 folder (the one that contains `bin` and `archive`).

Requirements: Windows 10/11 x64. No .NET install is needed (the exe is self-contained). The tool reads and writes compressed data with the game's own `oo2ext_7_win64.dll`. Oodle is not bundled.

## Use

Double-click **`tools\VRAM RipperDoc\VramRipperdoc.exe`**. It finds the game, scans, prints a summary and opens `VRAM-RipperDoc-Report.html`.

```
 Scan (default, read-only)
  --game <dir>              Cyberpunk 2077 folder (auto-detected for Steam/GOG/Epic, or when run from inside it)
  --budget <GB>             VRAM your mods may add (default: from your GPU and in-game settings)
  --out <dir>               Where to write reports (default: next to the exe)
  --mo2 <instance dir>      Read mods from a Mod Organizer 2 instance (auto-detected when possible)
  --hashes <file>           Optional list of depot paths (e.g. WolvenKit's archivehashes.csv) for nicer names
  --top <n>                 Rows to show in the console (default 25)
  --no-vanilla              Skip comparing against the base game archives (faster, less accurate)
  --no-open                 Don't open the HTML report when done
  --json                    Print the JSON report to stdout instead of the table
 Apply the plan (optional)
  --make-budget-archive     Write ONE extra archive with the planned textures halved (your mods are untouched)
  --remove-budget-archive   Delete it again
 Measure real VRAM
  --live [--label <name>] [--seconds <n>]   Record the game's actual VRAM use while you play (Ctrl+C to stop)
 Community (opt-in)
  --share                   Write an anonymous per-mod cost file you can submit on GitHub
  --community [file|url]    Show other players' reported costs (default: the project's GitHub list)
```

### A typical session

1. Run `VramRipperdoc.exe`. Check the report: the top rows are your heaviest mods, and the red counts are flags worth reading.
2. Load a save and stand somewhere busy, then run `VramRipperdoc.exe --live --label before` in a second window. Play a minute and close the game.
3. Run `VramRipperdoc.exe --make-budget-archive`.
4. Load the same save and run `VramRipperdoc.exe --live --label after`. The monitor prints a before/after table.
5. Don't like it? Run `VramRipperdoc.exe --remove-budget-archive`.

### Reading the report

| Column | Meaning |
|---|---|
| **+VRAM** | Estimated GPU memory this archive adds if everything in it is on screen. Negative means it is lighter than vanilla. |
| **Tex / Mesh** | Texture and mesh counts in the archive |
| **Tris LOD0** | Triangles at full detail, summed over meshes that actually load |
| **Overrides** | Files that replace a vanilla file |
| **Never loads** | Files another archive wins in load order (pure dead weight) |
| **Missing** | Hard references to files that exist nowhere: in vanilla or in any installed mod |
| **Flags** | Files with a warning (click the row to see them) |

"Group by installed mod" adds up all archives of one Vortex/MO2 mod. "Copy summary" puts a short Markdown table on your clipboard for Nexus, Reddit or Discord.

## How the budget archive works

For each planned texture, RipperDoc drops the top mip level. The remaining mips are the game's own pre-computed smaller versions, so nothing is re-encoded. It then fixes every size, offset and checksum in the file and writes it to `archive/pc/mod/!RipperDoc_Budget.archive`. The name is chosen so it loads before your mods, and it's added to the top of `modlist.txt` if you use one (with a backup).

The writer follows the rules vanilla archives use, all verified against real game files: index CRC64/XZ, header and table CRC32s, a SHA1 per file, the file table sorted by hash, and the index and file size aligned to 4 KiB pages. The scanner can't see game logic, so character mods with mesh-matched textures still look the same, just at half resolution.

### Tested in game (Cyberpunk 2077 2.5, RTX 3070 8 GB, 1080p, RT on)

- The game started, loaded a save and rendered correctly with a 148 MB test budget archive that halved 360 vanilla 2K+ textures (2.17 GB → 556 MB of texture data). There were no crashes, missing textures or corruption.
- Same save and spot, steady VRAM over the last 60 s: **4.62 GB vanilla → 4.50 GB** with the archive. Only part of those 360 textures was on screen in that Badlands scene, so the saving depends on what you are looking at. A heavy texture modlist saves more, which is why `--live` is there to measure it.
- Two writer bugs (unsorted file table, unaligned index) crashed the game at startup during development. Both were fixed and re-verified in game before release.

## How the scan works

1. Reads the game's load order: `archive/pc/mod` in ASCII order (or `modlist.txt`), then `mods/*/archives` (REDmod), plus MO2 mods that live outside the game folder. The first archive wins a conflict.
2. Parses each RDAR archive index and decompresses each file's CR2W header with the game's Oodle DLL.
3. **Textures:** reads `rendRenderTextureBlobPC` (size, mips, slices) and the compression. The uncompressed buffer size is the GPU footprint.
4. **Meshes:** reads `rendRenderMeshBlob` chunk infos (index counts and LOD masks) for triangles and LODs.
5. Indexes the vanilla `content` and `ep1` archives (about 550k files) to compute the delta for overrides, compare with vanilla resolution and triangles, spot identical copies, and tell which references the mod *added*. That last part means vanilla's own dead links never count as missing.
6. Rebuilds file paths from the import tables and embedded buffers of every scanned file, and from ArchiveXL `.xl`, TweakXL and CET files.

Validation on real game data:
- 34,380 textures and 21,410 meshes parsed with 0 failures.
- 0 false "missing reference" reports across 55k vanilla files.
- 3,500 vanilla headers re-checksummed byte-for-byte.
- 1,131 textures shrunk and re-verified.

## Roadmap

- RED4ext live tracker that shows which assets are loaded in the current scene.
- Optional downscaling of *new* textures by more than one level, and per-mod opt-out.

## Build

```
dotnet publish src/VramRipperdoc -c Release -o dist/bin
```

Or run `build.ps1` to also make the Nexus/Vortex-ready zip.

Developer self-tests:
- `--make-fixtures <dir>` builds fake mods from real game files: conflicts, overrides, new assets, a broken reference, a corrupt archive, REDmod, Vortex and MO2. Scan them with `--mod-root <dir> --mo2 <dir>/_mo2`.
- `--verify <archive>` checks the checksum conventions.
- `--verify-shrink <archive>` shrinks textures and re-verifies them.

## License

MIT. Not affiliated with CD PROJEKT RED. Cyberpunk 2077 is a trademark of CD PROJEKT S.A.
