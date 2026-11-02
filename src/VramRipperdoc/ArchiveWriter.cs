using System.Security.Cryptography;
using System.Text;

namespace VramRipperdoc;

/// <summary>One file to write: its segments as stored on disk (first = CR2W header, rest = buffers).</summary>
public sealed record WriteEntry(ulong Hash, long Timestamp, uint NumInlineBufferSegments,
    IReadOnlyList<(byte[] Raw, uint Size)> Segments, IReadOnlyList<ulong> Dependencies, byte[] Sha1);

/// <summary>
/// Writes RDAR v12 archives the way the game's own archives are laid out (header padded to 0xAC,
/// index CRC64/XZ over the tables, per-file SHA1 over header + stored buffers, file table sorted by
/// hash, index and file size aligned to 4 KiB). File data is streamed to a temporary file as it is
/// added, so multi-GB archives don't need that much memory. <see cref="Add(WriteEntry)"/> is thread-safe.
/// </summary>
public sealed class ArchiveWriter : IDisposable
{
    sealed record Meta(ulong Hash, long Timestamp, uint NumInlineBufferSegments, Segment[] Segments, IReadOnlyList<ulong> Dependencies, byte[] Sha1);

    readonly List<Meta> _files = new();
    readonly string _workDir;
    readonly object _lock = new();
    FileStream? _data;
    string? _tempPath;

    /// <param name="workDir">Where to stage data; use the destination folder to avoid a cross-drive copy.</param>
    public ArchiveWriter(string? workDir = null) => _workDir = workDir ?? Path.GetTempPath();

    public int Count { get { lock (_lock) return _files.Count; } }

    public void Add(WriteEntry e)
    {
        lock (_lock)
        {
            if (_data == null)
            {
                Directory.CreateDirectory(_workDir);
                _tempPath = Path.Combine(_workDir, $"RipperDoc-{Guid.NewGuid():N}.part");
                _data = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
                _data.Write(new byte[0xAC]);
            }
            var segs = new Segment[e.Segments.Count];
            for (int i = 0; i < segs.Length; i++)
            {
                var (raw, size) = e.Segments[i];
                segs[i] = new Segment((ulong)_data.Position, (uint)raw.Length, size);
                _data.Write(raw);
            }
            _files.Add(new Meta(e.Hash, e.Timestamp, e.NumInlineBufferSegments, segs, e.Dependencies, e.Sha1));
        }
    }

    /// <summary>Copies a file unchanged (optionally under a new hash) from another archive.</summary>
    public void Add(RdarArchive src, ArchiveEntry e, ulong? newHash = null)
    {
        var segs = src.SegmentsOf(e).Select(s => (src.ReadRaw(s), s.Size)).ToList();
        var deps = Enumerable.Range((int)e.DepsStart, (int)(e.DepsEnd - e.DepsStart)).Select(i => src.Dependencies[i]).ToList();
        Add(new WriteEntry(newHash ?? e.Hash, e.Timestamp, e.NumInlineBufferSegments, segs, deps, e.Sha1));
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
        lock (_lock)
        {
            if (_data == null) throw new InvalidOperationException("No files were added.");
            var fs = _data;
            var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
            fs.Position = fs.Length;

            var tables = new MemoryStream();
            var tw = new BinaryWriter(tables);
            int segCount = _files.Sum(f => f.Segments.Length), depCount = _files.Sum(f => f.Dependencies.Count);
            tw.Write((uint)_files.Count); tw.Write((uint)segCount); tw.Write((uint)depCount);
            var segs = new List<Segment>(segCount);
            var deps = new List<ulong>(depCount);
            // The game binary-searches the file table, so entries must be sorted by hash (as in vanilla archives).
            foreach (var f in _files.OrderBy(f => f.Hash))
            {
                uint s0 = (uint)segs.Count, d0 = (uint)deps.Count;
                segs.AddRange(f.Segments);
                deps.AddRange(f.Dependencies);
                tw.Write(f.Hash); tw.Write(f.Timestamp); tw.Write(f.NumInlineBufferSegments);
                tw.Write(s0); tw.Write((uint)segs.Count); tw.Write(d0); tw.Write((uint)deps.Count);
                tw.Write(f.Sha1.Length == 20 ? f.Sha1 : new byte[20]);
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
            bw.Flush();
            fs.Dispose();
            _data = null;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.Move(_tempPath!, path, overwrite: true);
            _tempPath = null;
        }
    }

    static void Pad(BinaryWriter bw, int alignment)
    {
        long rem = bw.BaseStream.Position % alignment;
        if (rem != 0) bw.Write(new byte[alignment - rem]);
    }

    /// <summary>Removes the staging file if <see cref="Write"/> was never reached.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _data?.Dispose();
            _data = null;
            if (_tempPath != null) try { File.Delete(_tempPath); } catch { }
            _tempPath = null;
        }
    }
}
