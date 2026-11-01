using System.Text;

namespace VramRipperdoc;

/// <summary>Writes RDAR archives by copying already-compressed segments from another archive.</summary>
public sealed class ArchiveWriter
{
    readonly List<(ulong Hash, RdarArchive Src, ArchiveEntry Entry)> _files = new();
    public void Add(RdarArchive src, ArchiveEntry e, ulong? newHash = null) => _files.Add((newHash ?? e.Hash, src, e));

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        var bw = new BinaryWriter(fs, Encoding.ASCII);
        fs.Position = 0xAC;
        var entries = new List<(ulong Hash, ArchiveEntry E, uint SegStart, uint SegEnd, uint DepStart, uint DepEnd)>();
        var segs = new List<Segment>();
        var deps = new List<ulong>();
        foreach (var (hash, src, e) in _files.OrderBy(f => f.Hash))
        {
            uint s0 = (uint)segs.Count, d0 = (uint)deps.Count;
            foreach (var s in src.SegmentsOf(e))
            {
                var raw = src.ReadRaw(s);
                segs.Add(new Segment((ulong)fs.Position, s.ZSize, s.Size));
                bw.Write(raw);
            }
            for (uint i = e.DepsStart; i < e.DepsEnd; i++) deps.Add(src.Dependencies[(int)i]);
            entries.Add((hash, e, s0, (uint)segs.Count, d0, (uint)deps.Count));
        }
        long indexPos = fs.Position;
        bw.Write(8u);
        bw.Write((uint)(entries.Count * 56 + segs.Count * 16 + deps.Count * 8 + 20));
        bw.Write(0ul);
        bw.Write((uint)entries.Count); bw.Write((uint)segs.Count); bw.Write((uint)deps.Count);
        foreach (var x in entries)
        {
            bw.Write(x.Hash); bw.Write(x.E.Timestamp); bw.Write(x.E.NumInlineBufferSegments);
            bw.Write(x.SegStart); bw.Write(x.SegEnd); bw.Write(x.DepStart); bw.Write(x.DepEnd); bw.Write(x.E.Sha1);
        }
        foreach (var s in segs) { bw.Write(s.Offset); bw.Write(s.ZSize); bw.Write(s.Size); }
        foreach (var d in deps) bw.Write(d);
        long indexSize = fs.Position - indexPos;
        long end = fs.Position;
        fs.Position = 0;
        bw.Write(RdarArchive.Magic); bw.Write(12u); bw.Write((ulong)indexPos); bw.Write((uint)indexSize);
        bw.Write(0ul); bw.Write(0u); bw.Write((ulong)end);
    }
}

/// <summary>Builds a fake game folder of test mods from real game files (developer self-test).</summary>
public static class Fixtures
{
    public static int Make(string game, string outRoot)
    {
        Oodle.TryLoad(new[] { Path.Combine(game, "bin", "x64", "oo2ext_7_win64.dll") });
        using var src = new RdarArchive(Path.Combine(game, "archive", "pc", "content", "basegame_4_appearance.archive"));
        var big = new List<ArchiveEntry>(); var meshes = new List<ArchiveEntry>(); var small = new List<ArchiveEntry>();
        ArchiveEntry? meshWithTex = null; var meshTexDeps = new List<ArchiveEntry>();
        var byHash = src.Entries.ToDictionary(e => e.Hash);
        foreach (var e in src.Entries)
        {
            if (big.Count >= 14 && meshes.Count >= 10 && meshWithTex != null) break;
            var head = src.ReadSegment(src.Segments[(int)e.SegmentsStart]);
            if (head == null || head[0] != 'C') continue;
            Cr2wFile c;
            try { c = new Cr2wFile(head); } catch { continue; }
            if (c.RootClass == "CBitmapTexture")
            {
                var w = c.ReadExport(0).Get("width") is uint u ? u : 0;
                if (w >= 2048 && big.Count < 14) big.Add(e); else if (w <= 512 && small.Count < 5) small.Add(e);
            }
            else if (c.RootClass == "CMesh")
            {
                if (meshes.Count < 10) meshes.Add(e);
                if (meshWithTex == null)
                {
                    var found = c.Imports.Select(Fnv1a64.Hash).Where(byHash.ContainsKey).Select(h => byHash[h]).ToList();
                    if (found.Count >= 2) { meshWithTex = e; meshTexDeps = found; }
                }
            }
        }
        Console.WriteLine($"picked {big.Count} big textures, {meshes.Count} meshes, mesh-with-imports={(meshWithTex != null)} ({meshTexDeps.Count} deps)");

        var modDir = Path.Combine(outRoot, "archive", "pc", "mod");
        if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);

        var hd = new ArchiveWriter(); foreach (var e in big) hd.Add(src, e); hd.Write(Path.Combine(modDir, "HD_Textures.archive"));
        var pr = new ArchiveWriter(); foreach (var e in big.Take(3)) pr.Add(src, e); pr.Write(Path.Combine(modDir, "#PriorityPatch.archive"));
        var mp = new ArchiveWriter(); foreach (var e in meshes) mp.Add(src, e);
        if (meshWithTex != null) { if (!meshes.Contains(meshWithTex)) mp.Add(src, meshWithTex); foreach (var d in meshTexDeps.DistinctBy(d => d.Hash)) mp.Add(src, d); }
        mp.Write(Path.Combine(modDir, "MeshPack.archive"));
        // Brand-new assets (not in vanilla): same data under made-up paths.
        var na = new ArchiveWriter();
        for (int i = 0; i < 4 && i < big.Count; i++) na.Add(src, big[^(i + 1)], Fnv1a64.Hash($@"mymod\textures\new_{i}.xbm"));
        na.Write(Path.Combine(modDir, "NewOutfit.archive"));
        File.WriteAllBytes(Path.Combine(modDir, "zz_Broken.archive"), Encoding.ASCII.GetBytes("RDAR\x0c\0\0\0garbage-that-is-not-an-index"));
        var rm = new ArchiveWriter(); foreach (var e in meshes.Take(2)) rm.Add(src, e);
        rm.Write(Path.Combine(outRoot, "mods", "TestRedmod", "archives", "TestRedmod.archive"));
        Console.WriteLine($"fixtures written to {outRoot}");
        return 0;
    }
}
