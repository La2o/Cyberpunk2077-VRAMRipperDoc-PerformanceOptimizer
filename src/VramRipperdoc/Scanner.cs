using System.Collections.Concurrent;

namespace VramRipperdoc;

public enum AssetKind { Texture, Mesh, Other }

public sealed class AssetInfo
{
    public ulong Hash;
    public string? Path;
    public string Class = "";
    public AssetKind Kind = AssetKind.Other;
    public long DiskBytes;
    /// <summary>Uncompressed buffer bytes: for textures/meshes this is what gets uploaded to the GPU.</summary>
    public long GpuBytes;
    public int Width, Height, Mips, Slices;
    public string? Compression;
    public long Tris0, Verts0;
    public int LodCount;
    public bool OverridesBase;
    public long BaseGpuBytes;
    public int BaseWidth, BaseHeight;
    public long BaseTris0;
    public string? ShadowedBy;
    public List<string> Flags = new();

    /// <summary>VRAM this asset adds compared to running without the mod.</summary>
    public long DeltaGpu => ShadowedBy != null ? 0 : OverridesBase ? GpuBytes - BaseGpuBytes : GpuBytes;
    public string DisplayName => Path ?? $"0x{Hash:X16}";
    public int MaxDim => Math.Max(Width, Height);
}

public sealed class ModReport
{
    public string Name = "";
    public string File = "";
    public string Source = "";
    public int Priority;
    public long DiskSize;
    public string? Error;
    public List<AssetInfo> Assets = new();

    public long GpuDelta => Assets.Sum(a => a.DeltaGpu);
    public long TextureDelta => Assets.Where(a => a.Kind == AssetKind.Texture).Sum(a => a.DeltaGpu);
    public long MeshDelta => Assets.Where(a => a.Kind == AssetKind.Mesh).Sum(a => a.DeltaGpu);
    public int Textures => Assets.Count(a => a.Kind == AssetKind.Texture);
    public int Meshes => Assets.Count(a => a.Kind == AssetKind.Mesh);
    public long Tris0 => Assets.Where(a => a.ShadowedBy == null).Sum(a => a.Tris0);
    public int Shadowed => Assets.Count(a => a.ShadowedBy != null);
    public long ShadowedDisk => Assets.Where(a => a.ShadowedBy != null).Sum(a => a.DiskBytes);
    public int Overrides => Assets.Count(a => a.OverridesBase);
    public int FlagCount => Assets.Count(a => a.Flags.Count > 0 && a.ShadowedBy == null);
}

public sealed class ScanResult
{
    public string GameDir = "";
    public List<ModReport> Mods = new();
    public int BaseFileCount;
    public bool OodleLoaded;
    public GpuInfo? Gpu;
    public TimeSpan Elapsed;
    public long TotalDelta => Mods.Sum(m => m.GpuDelta);
}

public sealed class ScanOptions
{
    public required string GameDir;
    /// <summary>Folder laid out like the game root (archive/pc/mod, mods/) to read mods from. Defaults to GameDir.</summary>
    public string? ModRoot;
    public string? HashList;
    public bool CompareVanilla = true;
    public Action<string>? Log;
}

public sealed class Scanner
{
    readonly ScanOptions _o;
    readonly ConcurrentDictionary<ulong, string> _names = new();
    readonly List<RdarArchive> _base = new();
    readonly Dictionary<ulong, (int Archive, int Entry)> _baseIndex = new();

    public Scanner(ScanOptions o) => _o = o;
    void Log(string s) => _o.Log?.Invoke(s);

    /// <summary>
    /// Archives in the order the game loads them: archive/pc/mod (modlist.txt first if present, then
    /// ordinal/ASCII name order), followed by REDmod archives. Earlier entries win file conflicts.
    /// </summary>
    public static List<(string Path, string Source)> DiscoverLoadOrder(string gameDir)
    {
        var result = new List<(string, string)>();
        var modDir = System.IO.Path.Combine(gameDir, "archive", "pc", "mod");
        if (Directory.Exists(modDir))
        {
            var files = Directory.GetFiles(modDir, "*.archive", SearchOption.TopDirectoryOnly)
                .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal).ToList();
            var modlist = System.IO.Path.Combine(modDir, "modlist.txt");
            if (File.Exists(modlist))
            {
                var listed = File.ReadAllLines(modlist).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                var ordered = new List<string>();
                foreach (var name in listed)
                {
                    var match = files.FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !ordered.Contains(match)) ordered.Add(match);
                }
                ordered.AddRange(files.Where(f => !ordered.Contains(f)));
                files = ordered;
            }
            result.AddRange(files.Select(f => (f, "archive/pc/mod")));
        }
        var redmodDir = System.IO.Path.Combine(gameDir, "mods");
        if (Directory.Exists(redmodDir))
        {
            foreach (var mod in Directory.GetDirectories(redmodDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var arch = System.IO.Path.Combine(mod, "archives");
                if (!Directory.Exists(arch)) continue;
                foreach (var f in Directory.GetFiles(arch, "*.archive").OrderBy(f => f, StringComparer.Ordinal))
                    result.Add((f, "REDmod: " + System.IO.Path.GetFileName(mod)));
            }
        }
        return result;
    }

    public ScanResult Scan()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new ScanResult { GameDir = _o.GameDir };

        res.OodleLoaded = Oodle.Available || Oodle.TryLoad(new[]
        {
            System.IO.Path.Combine(_o.GameDir, "bin", "x64", "oo2ext_7_win64.dll"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "oo2ext_7_win64.dll"),
        });
        if (!res.OodleLoaded) Log("! Oodle not found (bin/x64/oo2ext_7_win64.dll): sizes only, no texture/mesh details.");

        if (_o.HashList != null) LoadHashList(_o.HashList);
        if (_o.CompareVanilla) res.BaseFileCount = LoadBaseIndex();

        var order = DiscoverLoadOrder(_o.ModRoot ?? _o.GameDir);
        Log($"Found {order.Count} mod archive(s).");
        var mods = new ModReport[order.Count];
        var archives = new RdarArchive?[order.Count];

        Parallel.For(0, order.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var (path, source) = order[i];
            var mr = new ModReport
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                File = path, Source = source, Priority = i,
                DiskSize = new FileInfo(path).Length,
            };
            mods[i] = mr;
            try
            {
                var a = new RdarArchive(path);
                archives[i] = a;
                foreach (var e in a.Entries) mr.Assets.Add(Analyze(a, e));
            }
            catch (Exception ex) { mr.Error = ex.Message; }
        });

        // Conflicts: the first archive in load order that contains a hash wins; later copies never load.
        var winner = new Dictionary<ulong, ModReport>();
        foreach (var m in mods)
            foreach (var a in m.Assets)
            {
                if (winner.TryGetValue(a.Hash, out var w)) { a.ShadowedBy = w.Name; a.Flags.Add($"never loads: {w.Name} wins this file"); }
                else winner[a.Hash] = m;
            }

        // Resolve names now that every file's imports have been collected, then flag.
        foreach (var m in mods)
            foreach (var a in m.Assets)
            {
                if (a.Path == null && _names.TryGetValue(a.Hash, out var p)) a.Path = p;
                AddFlags(a);
            }

        foreach (var a in archives) a?.Dispose();
        foreach (var b in _base) b.Dispose();
        res.Mods = mods.ToList();
        res.Gpu = GpuInfo.Detect();
        res.Elapsed = sw.Elapsed;
        return res;
    }

    int LoadBaseIndex()
    {
        var dirs = new[] { "content", "ep1" }.Select(d => System.IO.Path.Combine(_o.GameDir, "archive", "pc", d)).Where(Directory.Exists);
        foreach (var f in dirs.SelectMany(d => Directory.GetFiles(d, "*.archive")).OrderBy(f => f, StringComparer.Ordinal))
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

    AssetInfo Analyze(RdarArchive a, ArchiveEntry e)
    {
        var info = new AssetInfo
        {
            Hash = e.Hash,
            DiskBytes = a.SegmentsOf(e).Sum(s => (long)s.ZSize),
            GpuBytes = a.BufferBytesOf(e),
        };
        ReadDetails(a, e, info, collectNames: true);

        if (_baseIndex.TryGetValue(e.Hash, out var b))
        {
            info.OverridesBase = true;
            var ba = _base[b.Archive];
            var be = ba.Entries[b.Entry];
            info.BaseGpuBytes = ba.BufferBytesOf(be);
            if (info.Kind != AssetKind.Other)
            {
                var vanilla = new AssetInfo();
                lock (ba) ReadDetails(ba, be, vanilla, collectNames: false);
                info.BaseWidth = vanilla.Width; info.BaseHeight = vanilla.Height; info.BaseTris0 = vanilla.Tris0;
            }
        }
        return info;
    }

    void ReadDetails(RdarArchive a, ArchiveEntry e, AssetInfo info, bool collectNames)
    {
        if (e.SegmentsEnd <= e.SegmentsStart) return;
        byte[]? head;
        try { head = a.ReadSegment(a.Segments[(int)e.SegmentsStart]); } catch { return; }
        if (head == null || head.Length < 4 || head[0] != 'C' || head[1] != 'R') return;
        Cr2wFile c;
        try { c = new Cr2wFile(head); } catch { return; }
        info.Class = c.RootClass;
        if (collectNames)
            foreach (var imp in c.Imports)
                if (imp.Length > 0) _names.TryAdd(Fnv1a64.Hash(imp), imp);

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
    }

    static void AddFlags(AssetInfo a)
    {
        if (a.ShadowedBy != null) return;
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
