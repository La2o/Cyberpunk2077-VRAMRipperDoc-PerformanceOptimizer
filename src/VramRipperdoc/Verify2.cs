using System.Security.Cryptography;
namespace VramRipperdoc;
public static class Verify2
{
    public static void Run(RdarArchive a, int samples)
    {
        var hits = new Dictionary<string, int>();
        void Hit(string k, bool ok) { var key = k + (ok ? " OK" : " FAIL"); hits[key] = hits.GetValueOrDefault(key) + 1; }
        int n = 0;
        foreach (var e in a.Entries)
        {
            if (n >= samples) break;
            var segs = a.SegmentsOf(e).ToList();
            var head = a.ReadSegment(segs[0]);
            if (head == null || head[0] != 'C') continue;
            n++;
            uint objectsEnd = U(head, 24), buffersEnd = U(head, 28);
            Hit("buffersEnd == objectsEnd + sum(zsize)", buffersEnd == objectsEnd + segs.Skip(1).Sum(s => (long)s.ZSize));
            var ms = new MemoryStream(); ms.Write(head); foreach (var s in segs.Skip(1)) ms.Write(a.ReadRaw(s));
            Hit("sha1(head + raw buffers)", SHA1.HashData(ms.ToArray()).SequenceEqual(e.Sha1));
            uint t3off = U(head, 40 + 3 * 12), t3cnt = U(head, 44 + 3 * 12), t3crc = U(head, 48 + 3 * 12);
            foreach (int es in new[] { 8, 12, 16, 20 }) if (t3cnt > 0) Hit($"table3 crc entry={es}", t3crc == Crc32.Compute(head.AsSpan((int)t3off, (int)t3cnt * es)));
            uint t4off = U(head, 40 + 4 * 12), t4cnt = U(head, 44 + 4 * 12);
            for (int i = 0; i < t4cnt; i++)
            {
                int p = (int)t4off + i * 24;
                uint size = U(head, p + 8), off = U(head, p + 12), tmpl = U(head, p + 16), ecrc = U(head, p + 20);
                if (n <= 2 && i < 3) Console.WriteLine($"export {i}: size={size} off={off} tmpl={tmpl:X8} crc={ecrc:X8} c32={Crc32.Compute(head.AsSpan((int)off, (int)size)):X8} c32c={Crc32.ComputeC(head.AsSpan((int)off, (int)size)):X8} entry={Convert.ToHexString(head, p, 24)}");
                Hit("export crc==0", ecrc == 0);
                Hit("export tmpl==0", tmpl == 0);
            }
        }
        foreach (var kv in hits.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,6}  {kv.Key}");
    }
    static uint U(byte[] d, int p) => BitConverter.ToUInt32(d, p);
}
