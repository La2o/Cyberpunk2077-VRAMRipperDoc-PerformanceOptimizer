using System.Text;

namespace VramRipperdoc;

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
        // Cooked 2K+ textures under new paths: something for the budget plan to halve.
        var huge = new ArchiveWriter(); int hugeCount = 0; ArchiveEntry? firstHuge = null;
        foreach (var e in src.Entries)
        {
            if (hugeCount >= 6) break;
            var segs = src.SegmentsOf(e).ToList();
            if (segs.Count != 2) continue;
            var head = src.ReadSegment(segs[0]);
            if (head == null || head[0] != 'C') continue;
            var c = new Cr2wFile(head);
            if (c.RootClass != "CBitmapTexture") continue;
            int bi = c.Exports.FindIndex(x => x.ClassName == "rendRenderTextureBlobPC");
            if (bi < 0 || c.ReadExport(bi).Get("header") is not Dictionary<string, object?> h || h.Get("sizeInfo") is not Dictionary<string, object?> si) continue;
            if (Convert.ToInt32(si.Get("width") ?? 0) < 2048) continue;
            firstHuge ??= e;
            huge.Add(src, e, Fnv1a64.Hash($@"hugemod\textures\t{hugeCount++}.xbm"));
        }
        huge.Write(Path.Combine(modDir, "HugeTextures.archive"));

        // A mesh override whose first import points at a file that doesn't exist anywhere.
        if (meshWithTex != null)
        {
            var segs = src.SegmentsOf(meshWithTex).ToList();
            var head = src.ReadSegment(segs[0])!;
            var c = new Cr2wFile(head);
            var target = Encoding.UTF8.GetBytes(c.Imports[0]);
            int pos = head.AsSpan().IndexOf(target);
            head[pos + target.Length - 5] = (byte)'q'; // ..._d.xbm -> ..._q.xbm style typo
            TextureShrinker.Rechecksum(head);
            var raw = new List<(byte[], uint)> { (Oodle.CompressKark(head)!, (uint)head.Length) };
            raw.AddRange(segs.Skip(1).Select(s => (src.ReadRaw(s), s.Size)));
            var bw = new ArchiveWriter();
            bw.Add(new WriteEntry(meshWithTex.Hash, meshWithTex.Timestamp, meshWithTex.NumInlineBufferSegments, raw, Array.Empty<ulong>(),
                ArchiveWriter.FileSha1(head, segs.Skip(1).Select(s => src.ReadRaw(s)))));
            bw.Write(Path.Combine(modDir, "#BrokenRef.archive"));

            // An environment-style retexture: a replaced file that points at a NEW texture path.
            // The new texture must count as "normal play" (always loaded), not as a new item.
            var newPath = Encoding.UTF8.GetString(head, pos, target.Length);
            var rp = new ArchiveWriter();
            rp.Add(new WriteEntry(meshWithTex.Hash, meshWithTex.Timestamp, meshWithTex.NumInlineBufferSegments, raw, Array.Empty<ulong>(),
                ArchiveWriter.FileSha1(head, segs.Skip(1).Select(s => src.ReadRaw(s)))));
            rp.Add(src, firstHuge ?? big[0], Fnv1a64.Hash(newPath));
            rp.Write(Path.Combine(outRoot, "retexture", "archive", "pc", "mod", "RetexturePack.archive"));
        }

        // Vortex deployment manifest + an MO2 instance, as those managers write them.
        File.WriteAllText(Path.Combine(outRoot, "vortex.deployment.json"), """
        {"instance":"test","version":1,"deploymentMethod":"hardlink_activator","gameId":"cyberpunk2077","files":[
          {"relPath":"archive\\pc\\mod\\HD_Textures.archive","source":"HD Reworked Project-7652-1-4-1700000000","target":"","time":1700000000},
          {"relPath":"archive\\pc\\mod\\#PriorityPatch.archive","source":"HD Reworked Project-7652-1-4-1700000000","target":"","time":1700000000},
          {"relPath":"archive\\pc\\mod\\MeshPack.archive","source":"Manual Install","target":"","time":1700000000}]}
        """);
        var mo2 = Path.Combine(outRoot, "_mo2");
        Directory.CreateDirectory(Path.Combine(mo2, "profiles", "Default"));
        File.WriteAllText(Path.Combine(mo2, "ModOrganizer.ini"), "[General]\ngameName=Cyberpunk 2077\nselected_profile=@ByteArray(Default)\n");
        File.WriteAllText(Path.Combine(mo2, "profiles", "Default", "modlist.txt"), "# managed by MO2\n+Better Skies\n-Disabled Mod\n");
        var mo2Mod = Path.Combine(mo2, "mods", "Better Skies");
        var sky = new ArchiveWriter(); foreach (var e in big.Skip(5).Take(2)) sky.Add(src, e, Fnv1a64.Hash($@"skies\t{e.Hash:x}.xbm"));
        sky.Write(Path.Combine(mo2Mod, "archive", "pc", "mod", "BetterSkies.archive"));
        File.WriteAllText(Path.Combine(mo2Mod, "meta.ini"), "[General]\nmodid=9999\nversion=1.0\n");
        var dis = new ArchiveWriter(); dis.Add(src, big[0]); dis.Write(Path.Combine(mo2, "mods", "Disabled Mod", "archive", "pc", "mod", "Disabled.archive"));

        File.WriteAllBytes(Path.Combine(modDir, "zz_Broken.archive"), Encoding.ASCII.GetBytes("RDAR\x0c\0\0\0garbage-that-is-not-an-index"));
        var rm = new ArchiveWriter(); foreach (var e in meshes.Take(2)) rm.Add(src, e);
        rm.Write(Path.Combine(outRoot, "mods", "TestRedmod", "archives", "TestRedmod.archive"));
        Console.WriteLine($"fixtures written to {outRoot}");
        return 0;
    }
}

/// <summary>Developer in-game test: halves up to N vanilla 2K+ character textures into a removable budget archive.</summary>
public static class IngameTest
{
    public static int Make(string game, int count, bool copyOnly = false)
    {
        Oodle.TryLoad(new[] { Path.Combine(game, "bin", "x64", "oo2ext_7_win64.dll") });
        var w = new ArchiveWriter();
        long before = 0, after = 0; int skipped = 0;
        var seen = new HashSet<ulong>();
        var sources = new[] { "basegame_3_nightcity", "basegame_4_appearance", "basegame_1_engine" }
            .Select(n => new RdarArchive(Path.Combine(game, "archive", "pc", "content", n + ".archive"))).ToList();
        foreach (var src in sources)
        foreach (var e in src.Entries)
        {
            if (!seen.Add(e.Hash)) continue;
            if (w.Count >= count) break;
            var segs = src.SegmentsOf(e).ToList();
            if (segs.Count != 2 || segs[1].Size < 4 << 20) continue; // 2K BC7 and up
            var head = src.ReadSegment(segs[0]);
            if (head == null || head[0] != 'C' || new Cr2wFile(head).RootClass != "CBitmapTexture") continue;
            var buf = src.ReadSegment(segs[1])!;
            if (copyOnly) { w.Add(src, e); before += buf.Length; after += buf.Length; continue; }
            var r = TextureShrinker.Shrink(head, buf, 1, out _);
            if (r == null) { skipped++; continue; }
            var deps = Enumerable.Range((int)e.DepsStart, (int)(e.DepsEnd - e.DepsStart)).Select(i => src.Dependencies[i]).ToList();
            w.Add(new WriteEntry(e.Hash, e.Timestamp, e.NumInlineBufferSegments, new[] { (r.HeaderRaw, r.HeaderSize), (r.BufferRaw, r.BufferSize) }, deps, r.Sha1));
            before += buf.Length; after += r.BufferSize;
        }
        foreach (var s in sources) s.Dispose();
        var path = Path.Combine(game, "archive", "pc", "mod", copyOnly ? $"!{Scanner.BudgetArchiveMarker}_COPYTEST.archive" : $"!{Scanner.BudgetArchiveMarker}_INGAME_TEST.archive");
        w.Write(path);
        Console.WriteLine($"{w.Count} textures halved ({skipped} skipped): {Report.Size(before)} -> {Report.Size(after)}");
        Console.WriteLine($"Wrote {path} ({Report.Size(new FileInfo(path).Length)}). Remove with --remove-budget-archive.");
        return 0;
    }
}

/// <summary>Developer speed test: a fake "new clothing" mod made of N real 4K textures under new paths.</summary>
public static class HeavyFixture
{
    public static int Make(string game, string outRoot, int count)
    {
        Oodle.TryLoad(new[] { Path.Combine(game, "bin", "x64", "oo2ext_7_win64.dll") });
        var w = new ArchiveWriter();
        int n = 0;
        var sources = new[] { "basegame_4_appearance", "basegame_3_nightcity" }
            .Select(name => new RdarArchive(Path.Combine(game, "archive", "pc", "content", name + ".archive"))).ToList();
        var picks = new List<(RdarArchive Src, ArchiveEntry E)>();
        foreach (var src in sources)
            foreach (var e in src.Entries)
            {
                var segs = src.SegmentsOf(e).ToList();
                if (segs.Count != 2 || segs[1].Size < 16 << 20) continue; // 4K BC7 and up
                var head = src.ReadSegment(segs[0]);
                if (head == null || head[0] != 'C' || new Cr2wFile(head).RootClass != "CBitmapTexture") continue;
                picks.Add((src, e));
            }
        // Vanilla has few 4K textures, so reuse them under different paths to reach the requested count.
        for (int i = 0; picks.Count > 0 && n < count; i++)
        {
            var (src, e) = picks[i % picks.Count];
            w.Add(src, e, Fnv1a64.Hash($@"heavymod\textures\t{n++}.xbm"));
        }
        foreach (var s in sources) s.Dispose();
        w.Write(Path.Combine(outRoot, "archive", "pc", "mod", "HeavyOutfits.archive"));
        Console.WriteLine($"{n} 4K textures written to {outRoot}");
        return 0;
    }
}

/// <summary>Developer self-test for the budget archive's file name (must sort before every other archive).</summary>
public static class NameTest
{
    public static int Run()
    {
        var cases = new List<string[]>
        {
            new[] { "###-PreemHair.archive", "B_Hanako.archive", "ETO_2K.archive" },
            new[] { "!!!!!!!!Photomode.archive", "#Hair.archive" },
            new[] { "! Hairs.archive", "basegame.archive" },          // hung v0.2.3-0.2.5
            new[] { " LeadingSpace.archive", "!x.archive" },           // hung v0.2.3-0.2.5
            new[] { "!!! !Weird.archive", "!!!!!!!.archive" },
            new[] { "!RipperDoc_Budget.archive" },                    // equal name (old fix left in a list)
            Array.Empty<string>(),
        };
        int bad = 0;
        foreach (var others in cases)
        {
            var name = BudgetArchive.NameThatLoadsFirst(others);
            bool ok = name.Contains(Scanner.BudgetArchiveMarker) && name.EndsWith(".archive") && others.All(o => string.CompareOrdinal(name, o) < 0);
            // Would the v0.2.5 loop have finished? (cap at 10,000 iterations)
            string old = $"!{Scanner.BudgetArchiveMarker}.archive"; int iter = 0;
            while (others.Any(o => string.CompareOrdinal(o, old) <= 0) && iter < 10_000) { old = "!" + old; iter++; }
            Console.WriteLine($"{(ok ? "OK " : "BAD")} [{string.Join(", ", others)}] -> \"{name}\"   (old loop: {(iter >= 10_000 ? "NEVER ENDS" : "ok")})");
            if (!ok) bad++;
        }
        return bad == 0 ? 0 : 1;
    }
}
