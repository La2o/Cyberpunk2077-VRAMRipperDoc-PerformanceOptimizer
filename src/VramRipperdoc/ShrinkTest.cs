namespace VramRipperdoc;

/// <summary>Developer self-test for the checksum rules and the texture shrinker on real files.</summary>
public static class ShrinkTest
{
    public static int Run(string archivePath, int samples)
    {
        using var a = new RdarArchive(archivePath);
        int identOk = 0, identBad = 0, shrunk = 0, skipped = 0, bad = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var e in a.Entries)
        {
            if (shrunk + skipped >= samples) break;
            var segs = a.SegmentsOf(e).ToList();
            var head = a.ReadSegment(segs[0]);
            if (head == null || head[0] != 'C') continue;
            var c = new Cr2wFile(head);
            if (c.RootClass != "CBitmapTexture") continue;

            // Identity: recomputing CRCs of an untouched header must reproduce it exactly.
            var copy = (byte[])head.Clone();
            TextureShrinker.Rechecksum(copy);
            if (copy.AsSpan().SequenceEqual(head)) identOk++; else identBad++;

            if (segs.Count != 2) { skipped++; reasons["segments!=2"] = reasons.GetValueOrDefault("segments!=2") + 1; continue; }
            var buf = a.ReadSegment(segs[1])!;
            var before = Measure(c);
            if (before.W < 256) { skipped++; reasons["small"] = reasons.GetValueOrDefault("small") + 1; continue; }
            var r = TextureShrinker.Shrink(head, buf, 1, out var why);
            if (r == null) { skipped++; reasons[why!] = reasons.GetValueOrDefault(why!) + 1; continue; }

            // Re-read the result exactly like the game/our scanner would.
            var newHead = Oodle.DecompressKark(r.HeaderRaw, (int)r.HeaderSize)!;
            var nc = new Cr2wFile(newHead);
            var after = Measure(nc);
            var newBuf = r.BufferRaw.Length == r.BufferSize ? r.BufferRaw : Oodle.DecompressKark(r.BufferRaw, (int)r.BufferSize)!;
            var check = (byte[])newHead.Clone(); TextureShrinker.Rechecksum(check);
            bool ok = after.W == before.W / 2 && after.H == before.H / 2 && after.Mips == before.Mips - 1
                && after.DataSize == newBuf.Length && newBuf.AsSpan().SequenceEqual(buf.AsSpan((int)(buf.Length - newBuf.Length)))
                && after.Offsets.Count == after.Mips && after.Offsets[0] == 0
                && after.Offsets.Zip(before.Offsets.Skip(1), (x, y) => y - x).Distinct().Count() == 1
                && check.AsSpan().SequenceEqual(newHead)
                && nc.Buffers[0].MemSize == newBuf.Length && nc.Buffers[0].DiskSize == r.BufferRaw.Length
                && BitConverter.ToUInt32(newHead, 24) == newHead.Length;
            if (ok) shrunk++; else { bad++; if (bad <= 3) Console.WriteLine($"BAD {e.Hash:X16}: {before} -> {after}"); }
        }
        Console.WriteLine($"identity rechecksum: {identOk} ok, {identBad} differ");
        Console.WriteLine($"shrink: {shrunk} verified, {bad} bad, {skipped} skipped");
        foreach (var kv in reasons) Console.WriteLine($"  skipped {kv.Value}: {kv.Key}");
        return bad == 0 && identBad == 0 ? 0 : 1;
    }

    record Info(int W, int H, int Mips, long DataSize, List<long> Offsets)
    {
        public override string ToString() => $"{W}x{H} mips={Mips} data={DataSize} offs=[{string.Join(",", Offsets.Take(4))}]";
    }

    static Info Measure(Cr2wFile c)
    {
        int bi = c.Exports.FindIndex(x => x.ClassName == "rendRenderTextureBlobPC");
        var h = (Dictionary<string, object?>)c.ReadExport(bi)["header"]!;
        var s = (Dictionary<string, object?>)h["sizeInfo"]!;
        var t = (Dictionary<string, object?>)h["textureInfo"]!;
        var offs = ((List<object?>)h["mipMapInfo"]!).Cast<Dictionary<string, object?>>()
            .Select(m => m.Get("placement") is Dictionary<string, object?> p && p.Get("offset") is { } o ? Convert.ToInt64(o) : 0L).ToList();
        return new Info(Convert.ToInt32(s["width"]), Convert.ToInt32(s["height"]), Convert.ToInt32(t["mipCount"]), Convert.ToInt64(t["textureDataSize"]), offs);
    }
}
