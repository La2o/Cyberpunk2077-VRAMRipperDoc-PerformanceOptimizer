using System.Security.Cryptography;

namespace VramRipperdoc;

/// <summary>Developer probe: checks which checksum conventions real game files use.</summary>
public static class Verify
{
    public static int Run(string archivePath, string oodle, int samples)
    {
        Oodle.TryLoad(new[] { oodle });
        using var a = new RdarArchive(archivePath);
        var hits = new Dictionary<string, int>();
        void Hit(string k, bool ok) { hits[k + (ok ? " OK" : " FAIL")] = hits.GetValueOrDefault(k + (ok ? " OK" : " FAIL")) + 1; }

        // Index CRC candidates
        using (var fs = File.OpenRead(archivePath))
        {
            var br = new BinaryReader(fs);
            fs.Position = 8; long ip = (long)br.ReadUInt64(); uint isz = br.ReadUInt32();
            fs.Position = ip; var idx = br.ReadBytes((int)isz);
            ulong stored = BitConverter.ToUInt64(idx, 8);
            var after = idx.AsSpan(16);
            var fileTableSize = BitConverter.ToUInt32(idx, 4);
            Console.WriteLine($"index size={isz} fileTableSize={fileTableSize} stored crc={stored:X16}");
            foreach (var (name, span) in new[] { ("after16", idx[16..]), ("all-zeroed", Zero(idx, 8, 8)), ("from8", idx[8..]), ("after16 len=fts", idx[16..Math.Min(idx.Length, 8 + (int)fileTableSize)]) })
            {
                Console.WriteLine($"  {name}: ecma={Crc64.ComputeEcma(span):X16} iso={Crc64.ComputeIso(span):X16} ecmaNoInv={Crc64.ComputeEcma(span, false):X16}");
            }
        }

        int n = 0;
        foreach (var e in a.Entries)
        {
            if (n >= samples) break;
            var segs = a.SegmentsOf(e).ToList();
            var head = a.ReadSegment(segs[0]);
            if (head == null || head[0] != 'C') continue;
            n++;
            Hit("first segment compressed", segs[0].ZSize != segs[0].Size);
            for (int i = 1; i < segs.Count; i++) Hit("buffer segment compressed", segs[i].ZSize != segs[i].Size);

            uint objectsEnd = U32(head, 24), buffersEnd = U32(head, 28), crc = U32(head, 32);
            Hit("objectsEnd == first segment size", objectsEnd == head.Length);
            Hit("buffersEnd == total size", buffersEnd == segs.Sum(s => (long)s.Size));

            // header crc candidates
            Hit("hdrcrc crc32(0..160 crc zeroed)", crc == Crc32.Compute(Zero(head[..160], 32, 4)));
            Hit("hdrcrc crc32(0..160 crc=0xDEADBEEF)", crc == Crc32.Compute(Set(head[..160], 32, 0xDEADBEEF)));

            var tables = new (uint Off, uint Count, uint Crc)[10];
            for (int i = 0; i < 10; i++) tables[i] = (U32(head, 40 + i * 12), U32(head, 44 + i * 12), U32(head, 48 + i * 12));
            int[] entrySize = { 1, 8, 8, 24, 24, 24, 24, 0, 0, 0 };
            for (int t = 0; t < 7; t++)
            {
                if (tables[t].Count == 0) continue;
                int len = (int)tables[t].Count * entrySize[t];
                Hit($"table{t} crc32", tables[t].Crc == Crc32.Compute(head.AsSpan((int)tables[t].Off, len)));
            }
            // exports
            for (int i = 0; i < tables[4].Count; i++)
            {
                int p = (int)tables[4].Off + i * 24;
                uint size = U32(head, p + 8), off = U32(head, p + 12), ecrc = U32(head, p + 20);
                Hit("export crc32(data)", ecrc == Crc32.Compute(head.AsSpan((int)off, (int)size)));
            }
            // buffers
            for (int i = 0; i < tables[5].Count; i++)
            {
                int p = (int)tables[5].Off + i * 24;
                uint boff = U32(head, p + 8), disk = U32(head, p + 12), mem = U32(head, p + 16), bcrc = U32(head, p + 20);
                if (1 + i >= segs.Count) { Hit("buffer has own segment", false); continue; }
                var seg = segs[1 + i];
                Hit("buffer diskSize == seg.ZSize", disk == seg.ZSize);
                Hit("buffer memSize == seg.Size", mem == seg.Size);
                var raw = a.ReadRaw(seg);
                var dec = a.ReadSegment(seg)!;
                Hit("buffer crc32(uncompressed)", bcrc == Crc32.Compute(dec));
                Hit("buffer crc32(raw segment)", bcrc == Crc32.Compute(raw));
                Hit("buffer offset == running", true);
            }
            // sha1
            var all = new MemoryStream();
            foreach (var s in segs) all.Write(a.ReadSegment(s)!);
            var rawAll = new MemoryStream();
            foreach (var s in segs) rawAll.Write(a.ReadRaw(s));
            Hit("sha1(uncompressed file)", SHA1.HashData(all.ToArray()).SequenceEqual(e.Sha1));
            Hit("sha1(raw segments)", SHA1.HashData(rawAll.ToArray()).SequenceEqual(e.Sha1));
            Hit("sha1 all zero", e.Sha1.All(b => b == 0));
        }
        foreach (var kv in hits.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,6}  {kv.Key}");
        Verify2.Run(a, samples);
        return 0;
    }

    static uint U32(byte[] d, int p) => BitConverter.ToUInt32(d, p);
    static byte[] Zero(byte[] d, int p, int n) { var c = (byte[])d.Clone(); Array.Clear(c, p, n); return c; }
    static byte[] Set(byte[] d, int p, uint v) { var c = (byte[])d.Clone(); BitConverter.GetBytes(v).CopyTo(c, p); return c; }
}
