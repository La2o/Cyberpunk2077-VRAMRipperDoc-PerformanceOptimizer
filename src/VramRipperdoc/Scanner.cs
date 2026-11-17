using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace VramRipperdoc;

public enum AssetKind { Texture, Mesh, Other }

public sealed class AssetInfo
{
    public ulong Hash;
    public string? Path;
    public string Class = "";
    public AssetKind Kind = AssetKind.Other;
    public long DiskBytes;
    /// <summary>Uncompressed buffer bytes the renderer uploads to the GPU (see <see cref="Cr2wFile.RenderBufferIndices"/>).</summary>
    public long GpuBytes;
    /// <summary>The file's other buffer data (animations, appearances, world data...): system RAM, not VRAM.</summary>
    public long DataBytes;
    public int Width, Height, Mips, Slices;
    public string? Compression;
    public long Tris0, Verts0;
    public int LodCount;
    public bool OverridesBase;
    public bool IdenticalToVanilla;
    public long BaseGpuBytes;
    public int BaseWidth, BaseHeight;
    public long BaseTris0;
    public string? ShadowedBy;
    public byte[] Sha1 = Array.Empty<byte>();
    /// <summary>Hard references (CR2W imports) this file adds that the vanilla version didn't have.</summary>
    public List<string> NewImports = new();
    public List<string> MissingImports = new();
    /// <summary>Hashes of every file this one points to (imports + paths in embedded buffers).</summary>
    public List<ulong> RefHashes = new();
    /// <summary>
    /// Loaded during normal play: replaces a game file, or is pulled in by one (e.g. a new texture used by a
    /// replaced material). Everything else (new clothing, cars, props) only costs VRAM when it is used.
    /// </summary>
    public bool AlwaysLoaded;
    public List<string> Flags = new();

    /// <summary>VRAM this asset adds compared to running without the mod.</summary>
    public long DeltaGpu => ShadowedBy != null ? 0 : OverridesBase ? GpuBytes - BaseGpuBytes : GpuBytes;
    public string DisplayName => Path ?? $"0x{Hash:X16}";
    public int MaxDim => Math.Max(Width, Height);
    public bool Loads => ShadowedBy == null;
}

public sealed class ModReport
{
    public string Name = "";
    public string File = "";
    public string Source = "";
    public int Priority;
    public long DiskSize;
    public string? Error;
    /// <summary>Installed mod this archive belongs to (from Vortex/MO2), if known.</summary>
    public string? Package;
    public int? NexusId;
    public string? Manager;
    public string? CommunityNote;
    public List<AssetInfo> Assets = new();

    public long GpuDelta => Assets.Sum(a => a.DeltaGpu);
    public long AlwaysDelta => Assets.Where(a => a.AlwaysLoaded).Sum(a => a.DeltaGpu);
    public long WhenUsedDelta => Assets.Where(a => !a.AlwaysLoaded).Sum(a => a.DeltaGpu);
    public long TextureDelta => Assets.Where(a => a.Kind == AssetKind.Texture).Sum(a => a.DeltaGpu);
    public long MeshDelta => Assets.Where(a => a.Kind == AssetKind.Mesh).Sum(a => a.DeltaGpu);
    public int Textures => Assets.Count(a => a.Kind == AssetKind.Texture);
    public int Meshes => Assets.Count(a => a.Kind == AssetKind.Mesh);
    public long Tris0 => Assets.Where(a => a.Loads).Sum(a => a.Tris0);
    public int Shadowed => Assets.Count(a => !a.Loads);
    public long ShadowedDisk => Assets.Where(a => !a.Loads).Sum(a => a.DiskBytes);
    public int Overrides => Assets.Count(a => a.OverridesBase);
    public int Missing => Assets.Where(a => a.Loads).Sum(a => a.MissingImports.Count);
    public int FlagCount => Assets.Count(a => a.Flags.Count > 0 && a.Loads);
    /// <summary>Data that loads into system RAM, not VRAM (e.g. a lipsync archive full of animations).</summary>
    public long DataBytes => Assets.Where(a => a.Loads).Sum(a => a.DataBytes);
}

public sealed class ScanResult
{
    public string GameDir = "";
    public List<ModReport> Mods = new();
    public int BaseFileCount;
    public bool OodleLoaded;
    public GpuInfo? Gpu;
    public GameSettings? Settings;
    public string? BudgetArchive;
    public List<string> ManagerNotes = new();
    public string BudgetWhy = "";
    public List<string> HistoryDiff = new();
    public TimeSpan Elapsed;
    public long TotalDelta => Mods.Sum(m => m.GpuDelta);
    public long AlwaysDelta => Mods.Sum(m => m.AlwaysDelta);
    public long WhenUsedDelta => Mods.Sum(m => m.WhenUsedDelta);
    public long DataBytes => Mods.Sum(m => m.DataBytes);
    /// <summary>Whether an existing budget fix still matches the installed mods (null when there is no fix).</summary>
    public BudgetArchive.FixCheck? FixCheck;
}

public sealed class ScanOptions
{
    public required string GameDir;
    /// <summary>Folder laid out like the game root (archive/pc/mod, mods/) to read mods from. Defaults to GameDir.</summary>
    public string? ModRoot;
    public string? HashList;
    public string? Mo2Instance;
    public bool CompareVanilla = true;
    public Action<string>? Log;
    /// <summary>Called as archives finish: (done, total).</summary>
    public Action<int, int>? Progress;
    /// <summary>Called when a stage starts: ("vanilla", 0) while reading the game's own files, then ("scanning", archives).</summary>
    public Action<string, int>? Phase;
}

public sealed record ArchiveSource(string Path, string Source, string? Package = null, int? NexusId = null, string? Manager = null);

public sealed class Scanner
{
    public const string BudgetArchiveMarker = "RipperDoc_Budget";
    static readonly Regex PathRx = new(@"[A-Za-z0-9_\-\\/\.]{3,}\.(xbm|mesh|mi|mt|ent|app|mlsetup|mlmask|morphtarget|inkatlas|anims|rig|physicalscene|gradient|texarray|cubemap|envprobe|hp|mltemplate)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    readonly ScanOptions _o;
    readonly ConcurrentDictionary<ulong, string> _names = new();
    readonly ConcurrentDictionary<ulong, byte> _referenced = new();
    readonly List<RdarArchive> _base = new();
    readonly Dictionary<ulong, (int Archive, int Entry)> _baseIndex = new();
    /// <summary>Files RED4ext plugins bring in their own folders (ArchiveXL's Bundle): they exist in game, but aren't mods.</summary>
    readonly HashSet<ulong> _frameworkFiles = new();
    /// <summary>Paths named in .xl files. ArchiveXL makes some of them exist (resource link and copy targets).</summary>
    readonly HashSet<ulong> _xlPaths = new();
    static readonly Regex XlPathRx = new(@"[A-Za-z0-9_\-\.\\/]*[\\/][A-Za-z0-9_\-\.\\/]*\.[A-Za-z0-9_]{2,16}", RegexOptions.Compiled);

    public Scanner(ScanOptions o) => _o = o;
    void Log(string s) => _o.Log?.Invoke(s);

    /// <summary>
    /// Archives in the order the game loads them: archive/pc/mod (modlist.txt first if present, then
    /// ordinal/ASCII name order), followed by REDmod archives. Earlier entries win file conflicts.
    /// RipperDoc's own budget archive is left out so the plan is always computed on the real mods.
    /// </summary>
    public static List<ArchiveSource> DiscoverLoadOrder(string gameDir, IReadOnlyList<ArchiveSource>? extra = null)
    {
        var result = new List<ArchiveSource>();
        var modDir = System.IO.Path.Combine(gameDir, "archive", "pc", "mod");
        var files = (Directory.Exists(modDir) ? Directory.GetFiles(modDir, "*.archive", SearchOption.TopDirectoryOnly) : Array.Empty<string>())
            .Select(f => new ArchiveSource(f, "archive/pc/mod")).ToList();
        // Mod-manager archives that aren't deployed into the game folder (MO2 virtual file system).
        foreach (var x in extra ?? Array.Empty<ArchiveSource>())
            if (!files.Any(f => string.Equals(System.IO.Path.GetFileName(f.Path), System.IO.Path.GetFileName(x.Path), StringComparison.OrdinalIgnoreCase)))
                files.Add(x);
        files = files.Where(f => !System.IO.Path.GetFileName(f.Path).Contains(BudgetArchiveMarker))
            .OrderBy(f => System.IO.Path.GetFileName(f.Path), StringComparer.Ordinal).ToList();
        var modlist = System.IO.Path.Combine(modDir, "modlist.txt");
        if (File.Exists(modlist))
        {
            var ordered = new List<ArchiveSource>();
            foreach (var name in File.ReadAllLines(modlist).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            {
                var match = files.FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f.Path), name, StringComparison.OrdinalIgnoreCase));
                if (match != null && !ordered.Contains(match)) ordered.Add(match);
            }
            ordered.AddRange(files.Where(f => !ordered.Contains(f)));
            files = ordered;
        }
        result.AddRange(files);
        var redmodDir = System.IO.Path.Combine(gameDir, "mods");
        if (Directory.Exists(redmodDir))
        {
            foreach (var mod in Directory.GetDirectories(redmodDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var arch = System.IO.Path.Combine(mod, "archives");
                if (!Directory.Exists(arch)) continue;
                foreach (var f in Directory.GetFiles(arch, "*.archive").OrderBy(f => f, StringComparer.Ordinal))
                    result.Add(new ArchiveSource(f, "REDmod: " + System.IO.Path.GetFileName(mod), System.IO.Path.GetFileName(mod)));
            }
        }
        return result;
    }

    public ScanResult Scan()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new ScanResult { GameDir = _o.GameDir };
        var modRoot = _o.ModRoot ?? _o.GameDir;

        res.OodleLoaded = Oodle.Available || Oodle.TryLoad(new[]
        {
            System.IO.Path.Combine(_o.GameDir, "bin", "x64", "oo2ext_7_win64.dll"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "oo2ext_7_win64.dll"),
        });
        if (!res.OodleLoaded) Log("! Oodle not found (bin/x64/oo2ext_7_win64.dll): sizes only, no texture/mesh details.");

        if (_o.HashList != null) LoadHashList(_o.HashList);
        _o.Phase?.Invoke("vanilla", 0);
        if (_o.CompareVanilla) { Log("Reading the game's own file list (a few seconds)..."); res.BaseFileCount = LoadBaseIndex(); }

        var managers = ModManagers.Detect(_o.GameDir, modRoot, _o.Mo2Instance, Log);
        res.ManagerNotes = managers.Notes;
        // Everywhere a mod can put files the game or ArchiveXL reads: the mod folder, the game, and MO2's mod folders.
        var roots = new[] { modRoot, _o.GameDir }.Concat(managers.ExtraRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_o.CompareVanilla && IndexFrameworkArchives(roots) is var (files, plugins) && files > 0)
            res.ManagerNotes.Add($"Also checked {files:N0} file(s) that RED4ext plugins bring along ({string.Join(", ", plugins)}), so mods using them don't look incomplete.");
        var order = DiscoverLoadOrder(modRoot, managers.ExtraArchives);
        res.BudgetArchive = BudgetArchive.FindExisting(modRoot);
        Log($"Found {order.Count} mod archive(s)." + (order.Count > 300 ? " Big mod lists take a few minutes; progress is shown below." : ""));
        _o.Phase?.Invoke("scanning", order.Count);
        int done = 0;
        var mods = new ModReport[order.Count];
        var archives = new RdarArchive?[order.Count];

        Parallel.For(0, order.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var src = order[i];
            var info = managers.Lookup(src.Path);
            var mr = new ModReport
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(src.Path),
                File = src.Path, Source = src.Source, Priority = i,
                DiskSize = new FileInfo(src.Path).Length,
                Package = info?.Package ?? src.Package, NexusId = info?.NexusId ?? src.NexusId, Manager = info?.Manager ?? src.Manager,
            };
            mods[i] = mr;
            try
            {
                var a = new RdarArchive(src.Path);
                archives[i] = a;
                foreach (var e in a.Entries) mr.Assets.Add(Analyze(a, e));
            }
            catch (Exception ex) { mr.Error = ex.Message; }
            _o.Progress?.Invoke(Interlocked.Increment(ref done), order.Count);
        });

        // Conflicts: the first archive in load order that contains a hash wins; later copies never load.
        var winner = new Dictionary<ulong, ModReport>();
        foreach (var m in mods)
            foreach (var a in m.Assets)
            {
                if (winner.TryGetValue(a.Hash, out var w)) { a.ShadowedBy = w.Name; a.Flags.Add($"never loads: {w.Name} wins this file"); }
                else winner[a.Hash] = m;
            }

        // Always loaded = replaces a game file, or is reachable from one through references
        // (a replaced material pointing at a new texture). New items reached only from ArchiveXL /
        // TweakXL definitions load when worn, spawned or visited.
        var loadedByHash = mods.SelectMany(m => m.Assets).Where(a => a.Loads).ToDictionary(a => a.Hash);
        var queue = new Queue<AssetInfo>(loadedByHash.Values.Where(a => a.OverridesBase));
        foreach (var a in queue) a.AlwaysLoaded = true;
        while (queue.TryDequeue(out var cur))
            foreach (var h in cur.RefHashes)
                if (loadedByHash.TryGetValue(h, out var next) && !next.AlwaysLoaded)
                {
                    next.AlwaysLoaded = true;
                    queue.Enqueue(next);
                }

        ScanTextReferences(roots);
        var dynamicArchives = ArchivesWithDynamicXl(roots);
        var existing = new HashSet<ulong>(winner.Keys);
        var bySha = new Dictionary<string, (ModReport Mod, AssetInfo Asset)>();

        foreach (var m in mods)
            foreach (var a in m.Assets)
            {
                if (a.Path == null && _names.TryGetValue(a.Hash, out var p)) a.Path = p;
                if (!a.Loads) continue;
                AddFlags(a);

                // Hard references that point nowhere (only ones the mod added, so vanilla's own dead links don't count).
                // A file exists when a mod, the game or a plugin's bundle has it, or when an .xl file creates it.
                if (_o.CompareVanilla)
                    foreach (var imp in a.NewImports)
                    {
                        if (imp.IndexOfAny(new[] { '*', '{', '}', '$', '%' }) >= 0) continue;
                        var h = Fnv1a64.Hash(imp);
                        if (!existing.Contains(h) && !_baseIndex.ContainsKey(h) && !_frameworkFiles.Contains(h) && !_xlPaths.Contains(h))
                            a.MissingImports.Add(imp);
                    }
                if (a.MissingImports.Count > 0)
                    a.Flags.Add($"references {a.MissingImports.Count} missing file(s): {string.Join(", ", a.MissingImports.Take(3))}{(a.MissingImports.Count > 3 ? ", ..." : "")}");

                if (a.IdenticalToVanilla) a.Flags.Add("identical to vanilla (does nothing)");

                // Same bytes under a different path in a loaded file: VRAM paid twice when both are used.
                if (a.Kind != AssetKind.Other && a.GpuBytes >= 256 * 1024 && a.Sha1.Length == 20)
                {
                    var key = Convert.ToHexString(a.Sha1);
                    if (bySha.TryGetValue(key, out var first) && first.Asset.Hash != a.Hash)
                        a.Flags.Add($"duplicate of {first.Asset.DisplayName} in {first.Mod.Name} ({Report.Size(a.GpuBytes)} paid twice if both load)");
                    else bySha.TryAdd(key, (m, a));
                }

                // New texture/mesh nothing points at: likely leftover (or spawned by a script we can't see).
                if (!a.OverridesBase && a.Kind != AssetKind.Other && !_referenced.ContainsKey(a.Hash) && !dynamicArchives.Contains(m.Name))
                    a.Flags.Add("possibly unused: nothing references this new file");
            }

        foreach (var a in archives) a?.Dispose();
        foreach (var b in _base) b.Dispose();
        res.Mods = mods.ToList();
        res.Gpu = GpuInfo.Detect();
        res.Settings = GameSettings.Load();
        res.Elapsed = sw.Elapsed;
        return res;
    }

    int LoadBaseIndex()
    {
        // The game's own folders: content and ep1 (Phantom Liberty) first, then any other one a game update adds.
        var pc = System.IO.Path.Combine(_o.GameDir, "archive", "pc");
        var dirs = (Directory.Exists(pc) ? Directory.GetDirectories(pc) : Array.Empty<string>())
            .Where(d => !System.IO.Path.GetFileName(d).Equals("mod", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => System.IO.Path.GetFileName(d).ToLowerInvariant() switch { "content" => 0, "ep1" => 1, _ => 2 })
            .ThenBy(d => d, StringComparer.Ordinal);
        foreach (var f in dirs.SelectMany(d => Directory.GetFiles(d, "*.archive").OrderBy(f => f, StringComparer.Ordinal)))
        {
            try
            {
                var a = new RdarArchive(f);
                int ai = _base.Count;
                _base.Add(a);
                for (int i = 0; i < a.Entries.Count; i++) _baseIndex.TryAdd(a.Entries[i].Hash, (ai, i));
            }
            catch (Exception ex) { Log($"! Skipping base archive {System.IO.Path.GetFileName(f)}: {ex.Message}"); }
        }
        Log($"Indexed {_baseIndex.Count:N0} vanilla files from {_base.Count} archives.");
        return _baseIndex.Count;
    }

    void LoadHashList(string path)
    {
        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(',')[0].Trim().Trim('"');
            if (p.Length == 0 || !p.Contains('\\') && !p.Contains('/')) continue;
            _names.TryAdd(Fnv1a64.Hash(p), p.Replace('/', '\\'));
            n++;
        }
        Log($"Loaded {n:N0} names from hash list.");
    }

    /// <summary>
    /// Archives RED4ext plugins keep in their own folder: ArchiveXL loads its Bundle folder, and its API lets other
    /// plugins add theirs. Their files exist in game, so a mod pointing at them isn't missing anything.
    /// </summary>
    (int Files, List<string> Plugins) IndexFrameworkArchives(IEnumerable<string> roots)
    {
        var plugins = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var dir = System.IO.Path.Combine(root, "red4ext", "plugins");
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.archive", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var f in files)
                try
                {
                    using var a = new RdarArchive(f);
                    foreach (var e in a.Entries) _frameworkFiles.Add(e.Hash);
                    plugins.Add(System.IO.Path.GetRelativePath(dir, f).Split(System.IO.Path.DirectorySeparatorChar)[0]);
                }
                catch (Exception ex) { Log($"! Skipping plugin archive {System.IO.Path.GetFileName(f)}: {ex.Message}"); }
        }
        return (_frameworkFiles.Count, plugins.ToList());
    }

    /// <summary>
    /// Paths named in ArchiveXL (.xl), TweakXL (.yaml/.tweak) and CET (.lua) files count as references. Every path an
    /// .xl file names also counts as existing: resource link and copy targets only exist through ArchiveXL.
    /// </summary>
    void ScanTextReferences(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var places = new (string Dir, string[] Patterns)[]
            {
                (System.IO.Path.Combine(root, "archive", "pc", "mod"), new[] { "*.xl" }),
                (System.IO.Path.Combine(root, "mods"), new[] { "*.xl", "*.yaml", "*.yml", "*.tweak" }),
                (System.IO.Path.Combine(root, "red4ext", "plugins"), new[] { "*.xl" }),
                (System.IO.Path.Combine(root, "r6", "tweaks"), new[] { "*.yaml", "*.yml", "*.tweak" }),
                (System.IO.Path.Combine(root, "bin", "x64", "plugins", "cyber_engine_tweaks", "mods"), new[] { "*.lua" }),
            };
            foreach (var (dir, patterns) in places)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var pat in patterns)
                    foreach (var f in SafeFiles(dir, pat))
                        try
                        {
                            var text = File.ReadAllText(f);
                            foreach (Match m in PathRx.Matches(text)) AddReference(m.Value);
                            if (pat == "*.xl") foreach (Match m in XlPathRx.Matches(text)) _xlPaths.Add(Fnv1a64.Hash(m.Value));
                        }
                        catch { }
            }
        }
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Where(f => new FileInfo(f).Length < 8 << 20).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Archives whose .xl uses substitutions ({gender}, *): their file references can't be resolved statically.</summary>
    static HashSet<string> ArchivesWithDynamicXl(IEnumerable<string> roots)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var dir = System.IO.Path.Combine(root, "archive", "pc", "mod");
            if (!Directory.Exists(dir)) continue;
            foreach (var xl in Directory.GetFiles(dir, "*.xl"))
                try
                {
                    var text = File.ReadAllText(xl);
                    if (text.Contains('{') || text.Contains('*'))
                        set.Add(System.IO.Path.GetFileNameWithoutExtension(xl).Split('.')[0]);
                }
                catch { }
        }
        return set;
    }

    ulong AddReference(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        var h = Fnv1a64.Hash(p);
        _referenced.TryAdd(h, 0);
        _names.TryAdd(h, p);
        return h;
    }

    AssetInfo Analyze(RdarArchive a, ArchiveEntry e)
    {
        var info = new AssetInfo
        {
            Hash = e.Hash,
            DiskBytes = a.SegmentsOf(e).Sum(s => (long)s.ZSize),
            Sha1 = e.Sha1,
        };
        var imports = ReadDetails(a, e, info, collectNames: true);

        if (_baseIndex.TryGetValue(e.Hash, out var b))
        {
            info.OverridesBase = true;
            var ba = _base[b.Archive];
            var be = ba.Entries[b.Entry];
            info.IdenticalToVanilla = be.Sha1.AsSpan().SequenceEqual(e.Sha1);
            var vanilla = new AssetInfo();
            List<(string, ushort)> baseImports;
            lock (ba) baseImports = ReadDetails(ba, be, vanilla, collectNames: false);
            info.BaseGpuBytes = vanilla.GpuBytes; // same rule as the mod's file, so a replaced animation costs 0 either way
            info.BaseWidth = vanilla.Width; info.BaseHeight = vanilla.Height; info.BaseTris0 = vanilla.Tris0;
            var known = new HashSet<string>(baseImports.Select(i => i.Item1), StringComparer.OrdinalIgnoreCase);
            info.NewImports = imports.Where(i => IsHardImport(i.Item2) && !known.Contains(i.Item1)).Select(i => i.Item1).ToList();
        }
        else info.NewImports = imports.Where(i => IsHardImport(i.Item2)).Select(i => i.Item1).ToList();
        return info;
    }

    // Import flags: 4 = soft (optional), 8 = embedded, 16 = in-place. Only the rest must exist on disk.
    static bool IsHardImport(ushort flags) => (flags & (4 | 8 | 16)) == 0;

    List<(string Path, ushort Flags)> ReadDetails(RdarArchive a, ArchiveEntry e, AssetInfo info, bool collectNames)
    {
        var imports = new List<(string, ushort)>();
        // Until the header says otherwise (or when it can't be read, e.g. without Oodle), all buffer data counts as VRAM.
        info.GpuBytes = a.BufferBytesOf(e);
        info.DataBytes = 0;
        if (e.SegmentsEnd <= e.SegmentsStart) return imports;
        byte[]? head;
        try { head = a.ReadSegment(a.Segments[(int)e.SegmentsStart]); } catch { return imports; }
        if (head == null || head.Length < 4 || head[0] != 'C' || head[1] != 'R') return imports;
        Cr2wFile c;
        try { c = new Cr2wFile(head); } catch { return imports; }
        info.Class = c.RootClass;
        // Texture files are texture data through and through. Anything else counts only the buffers its render blobs
        // point to: a lipsync archive full of animations, or an appearance or world sector file, mostly sits in RAM.
        if (c.RootClass is not ("CBitmapTexture" or "CTextureArray" or "CCubeTexture" or "CReflectionProbeDataResource"))
        {
            var buffers = a.SegmentsOf(e).Skip(1).ToList();
            try
            {
                if (c.RenderBufferIndices() is { } gpu && c.Buffers.Count == buffers.Count)
                {
                    long all = info.GpuBytes;
                    info.GpuBytes = gpu.Where(i => i >= 0 && i < buffers.Count).Sum(i => (long)buffers[i].Size);
                    info.DataBytes = all - info.GpuBytes;
                }
            }
            catch { /* unknown layout: keep counting everything */ }
        }
        imports.AddRange(c.Imports.Zip(c.ImportFlags));
        if (collectNames)
        {
            foreach (var imp in c.Imports)
                if (imp.Length > 0) info.RefHashes.Add(AddReference(imp));
            // Meshes, appearances and entities keep more references in small embedded buffers.
            if (c.RootClass != "CBitmapTexture" && c.RootClass != "CTextureArray")
                foreach (var s in a.SegmentsOf(e).Skip(1).Where(s => s.Size <= 1 << 20))
                    try
                    {
                        var buf = a.ReadSegment(s);
                        if (buf != null) foreach (Match m in PathRx.Matches(Encoding.Latin1.GetString(buf))) info.RefHashes.Add(AddReference(m.Value));
                    }
                    catch { }
        }

        try
        {
            int texBlob = c.Exports.FindIndex(x => x.ClassName == "rendRenderTextureBlobPC");
            int meshBlob = c.Exports.FindIndex(x => x.ClassName == "rendRenderMeshBlob");
            if (texBlob >= 0 && c.RootClass is "CBitmapTexture" or "CTextureArray" or "CCubeTexture" or "CReflectionProbeDataResource")
            {
                info.Kind = AssetKind.Texture;
                var root = c.ReadExport(0);
                if (root.Get("setup") is Dictionary<string, object?> setup) info.Compression = setup.Get("compression") as string;
                var header = c.ReadExport(texBlob).Get("header") as Dictionary<string, object?>;
                var size = header?.Get("sizeInfo") as Dictionary<string, object?>;
                var ti = header?.Get("textureInfo") as Dictionary<string, object?>;
                info.Width = ToInt(size?.Get("width") ?? root.Get("width"));
                info.Height = ToInt(size?.Get("height") ?? root.Get("height"));
                info.Mips = ToInt(ti?.Get("mipCount"), 1);
                info.Slices = ToInt(ti?.Get("sliceCount"), 1);
                info.Compression ??= "TCM_None";
            }
            else if (meshBlob >= 0)
            {
                info.Kind = AssetKind.Mesh;
                var header = c.ReadExport(meshBlob).Get("header") as Dictionary<string, object?>;
                if (header?.Get("renderChunkInfos") is List<object?> chunks)
                {
                    int lodBits = 0;
                    foreach (var ch in chunks.OfType<Dictionary<string, object?>>())
                    {
                        int mask = ToInt(ch.Get("lodMask"), 1);
                        lodBits |= mask;
                        if ((mask & 1) != 0)
                        {
                            info.Tris0 += ToLong(ch.Get("numIndices")) / 3;
                            info.Verts0 += ToLong(ch.Get("numVertices"));
                        }
                    }
                    info.LodCount = System.Numerics.BitOperations.PopCount((uint)lodBits);
                }
                if (header?.Get("renderLODs") is List<object?> lods) info.LodCount = Math.Max(info.LodCount, lods.Count);
            }
        }
        catch { /* keep sizes; details are best-effort */ }
        return imports;
    }

    static void AddFlags(AssetInfo a)
    {
        if (a.Kind == AssetKind.Texture)
        {
            if (a.MaxDim >= 8192) a.Flags.Add("8K texture");
            else if (a.MaxDim >= 4096) a.Flags.Add("4K texture");
            if (a.Mips <= 1 && a.MaxDim >= 2048) a.Flags.Add("no mipmaps (full-res at any distance)");
            if (a.Compression == "TCM_None" && a.MaxDim >= 1024) a.Flags.Add("uncompressed (4x+ the VRAM of BC)");
            if (a.OverridesBase && a.BaseWidth > 0 && (long)a.Width * a.Height >= 4L * a.BaseWidth * a.BaseHeight)
                a.Flags.Add($"{(long)a.Width * a.Height / ((long)a.BaseWidth * a.BaseHeight)}x vanilla pixels ({a.BaseWidth}x{a.BaseHeight} -> {a.Width}x{a.Height})");
        }
        else if (a.Kind == AssetKind.Mesh)
        {
            if (a.LodCount <= 1 && a.Tris0 >= 100_000) a.Flags.Add("no LODs (full detail at any distance)");
            if (a.Tris0 >= 250_000) a.Flags.Add($"very dense mesh ({a.Tris0:N0} tris)");
            if (a.OverridesBase && a.BaseTris0 > 0 && a.Tris0 >= 3 * a.BaseTris0)
                a.Flags.Add($"{(double)a.Tris0 / a.BaseTris0:0.#}x vanilla triangles");
        }
    }

    static int ToInt(object? o, int def = 0) => o switch { uint u => (int)u, int i => i, ushort s => s, byte b => b, _ => def };
    static long ToLong(object? o) => o switch { uint u => u, int i => i, ushort s => s, byte b => b, ulong ul => (long)ul, _ => 0 };
}

static class DictExt
{
    public static object? Get(this Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) ? v : null;
}
