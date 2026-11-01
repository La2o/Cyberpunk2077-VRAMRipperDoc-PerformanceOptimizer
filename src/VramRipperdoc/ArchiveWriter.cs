using System.Security.Cryptography;
using System.Text;

namespace VramRipperdoc;

/// <summary>One file to write: its segments as stored on disk (first = CR2W header, rest = buffers).</summary>
public sealed record WriteEntry(ulong Hash, long Timestamp, uint NumInlineBufferSegments,
    IReadOnlyList<(byte[] Raw, uint Size)> Segments, IReadOnlyList<ulong> Dependencies, byte[] Sha1);

/// <summary>
/// Writes RDAR v12 archives the way the game's own archives are laid out (header padded to 0xAC,
/// index CRC64/XZ over the tables, per-file SHA1 over header + stored buffers).
/// </summary>
public sealed class ArchiveWriter
{
    readonly List<WriteEntry> _files = new();

    public int Count => _files.Count;
    public void Add(WriteEntry e) => _files.Add(e);

    /// <summary>Copies a file unchanged (optionally under a new hash) from another archive.</summary>
    public void Add(RdarArchive src, ArchiveEntry e, ulong? newHash = null)
    {
        var segs = src.SegmentsOf(e).Select(s => (src.ReadRaw(s), s.Size)).ToList();
        var deps = Enumerable.Range((int)e.DepsStart, (int)(e.DepsEnd - e.DepsStart)).Select(i => src.Dependencies[i]).ToList();
        _files.Add(new WriteEntry(newHash ?? e.Hash, e.Timestamp, e.NumInlineBufferSegments, segs, deps, e.Sha1));
    }

    /// <summary>SHA1 the game stores for a file: uncompressed CR2W header followed by the buffers as stored.</summary>
    public static byte[] FileSha1(byte[] header, IEnumerable<byte[]> storedBuffers)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(header);
        foreach (var b in storedBuffers) sha.AppendData(b);
        return sha.GetHashAndReset();
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            var bw = new BinaryWriter(fs, Encoding.ASCII);
            bw.Write(new byte[0xAC]);
            var entries = new List<(WriteEntry E, uint SegStart, uint SegEnd, uint DepStart, uint DepEnd)>();
            var segs = new List<Segment>();
            var deps = new List<ulong>();
            // The game binary-searches the file table, so entries must be sorted by hash (as in vanilla archives).
            foreach (var f in _files.OrderBy(f => f.Hash))
            {
                uint s0 = (uint)segs.Count, d0 = (uint)deps.Count;
                foreach (var (raw, size) in f.Segments)
                {
                    segs.Add(new Segment((ulong)fs.Position, (uint)raw.Length, size));
                    bw.Write(raw);
                }
                deps.AddRange(f.Dependencies);
                entries.Add((f, s0, (uint)segs.Count, d0, (uint)deps.Count));
            }

            var tables = new MemoryStream();
            var tw = new BinaryWriter(tables);
            tw.Write((uint)entries.Count); tw.Write((uint)segs.Count); tw.Write((uint)deps.Count);
            foreach (var x in entries)
            {
                tw.Write(x.E.Hash); tw.Write(x.E.Timestamp); tw.Write(x.E.NumInlineBufferSegments);
                tw.Write(x.SegStart); tw.Write(x.SegEnd); tw.Write(x.DepStart); tw.Write(x.DepEnd);
                tw.Write(x.E.Sha1.Length == 20 ? x.E.Sha1 : new byte[20]);
            }
            foreach (var s in segs) { tw.Write(s.Offset); tw.Write(s.ZSize); tw.Write(s.Size); }
            foreach (var d in deps) tw.Write(d);
            var tableBytes = tables.ToArray();

            // Vanilla archives start the index on a 4 KiB page and pad the file to whole pages.
            Pad(bw, 4096);
            long indexPos = fs.Position;
            bw.Write(8u);                                   // file table offset
            bw.Write((uint)(tableBytes.Length + 8));        // file table size (crc + tables)
            bw.Write(Crc64.ComputeEcma(tableBytes));        // CRC-64/XZ, verified against vanilla archives
            bw.Write(tableBytes);
            long indexSize = fs.Position - indexPos;
            Pad(bw, 4096);
            long end = fs.Position;
            fs.Position = 0;
            bw.Write(RdarArchive.Magic); bw.Write(12u); bw.Write((ulong)indexPos); bw.Write((uint)indexSize);
            bw.Write(0ul); bw.Write(0u); bw.Write((ulong)end);
        }
        File.Move(tmp, path, overwrite: true);
    }

    static void Pad(BinaryWriter bw, int alignment)
    {
        long rem = bw.BaseStream.Position % alignment;
        if (rem != 0) bw.Write(new byte[alignment - rem]);
    }
}
