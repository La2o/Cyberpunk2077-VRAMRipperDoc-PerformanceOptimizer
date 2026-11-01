using System.Runtime.InteropServices;
using System.Text;

namespace VramRipperdoc;

public readonly record struct Segment(ulong Offset, uint ZSize, uint Size);

public sealed class ArchiveEntry
{
    public ulong Hash;
    public long Timestamp;
    public uint NumInlineBufferSegments;
    public uint SegmentsStart;
    public uint SegmentsEnd;
    public uint DepsStart;
    public uint DepsEnd;
    public byte[] Sha1 = Array.Empty<byte>();
}

/// <summary>Reader for REDengine 4 "RDAR" .archive files (version 12).</summary>
public sealed class RdarArchive : IDisposable
{
    public const uint Magic = 0x52414452; // "RDAR"
    public string Path { get; }
    public uint Version { get; private set; }
    public List<ArchiveEntry> Entries { get; } = new();
    public List<Segment> Segments { get; } = new();
    public List<ulong> Dependencies { get; } = new();
    readonly FileStream _fs;

    public RdarArchive(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        ReadIndex();
    }

    void ReadIndex()
    {
        var br = new BinaryReader(_fs, Encoding.ASCII, leaveOpen: true);
        if (_fs.Length < 40) throw new InvalidDataException("File too small to be an archive");
        if (br.ReadUInt32() != Magic) throw new InvalidDataException("Not an RDAR archive");
        Version = br.ReadUInt32();
        ulong indexPos = br.ReadUInt64();
        uint indexSize = br.ReadUInt32();
        if (indexPos == 0 || indexPos + indexSize > (ulong)_fs.Length)
            throw new InvalidDataException("Archive index points outside the file (truncated download?)");

        _fs.Position = (long)indexPos;
        br.ReadUInt32(); // file table offset (8)
        br.ReadUInt32(); // file table size
        br.ReadUInt64(); // crc
        uint fileCount = br.ReadUInt32();
        uint segCount = br.ReadUInt32();
        uint depCount = br.ReadUInt32();

        for (int i = 0; i < fileCount; i++)
        {
            var e = new ArchiveEntry
            {
                Hash = br.ReadUInt64(),
                Timestamp = br.ReadInt64(),
                NumInlineBufferSegments = br.ReadUInt32(),
                SegmentsStart = br.ReadUInt32(),
                SegmentsEnd = br.ReadUInt32(),
                DepsStart = br.ReadUInt32(),
                DepsEnd = br.ReadUInt32(),
                Sha1 = br.ReadBytes(20),
            };
            Entries.Add(e);
        }
        for (int i = 0; i < segCount; i++)
            Segments.Add(new Segment(br.ReadUInt64(), br.ReadUInt32(), br.ReadUInt32()));
        for (int i = 0; i < depCount; i++)
            Dependencies.Add(br.ReadUInt64());
    }

    public IEnumerable<Segment> SegmentsOf(ArchiveEntry e)
    {
        for (uint i = e.SegmentsStart; i < e.SegmentsEnd; i++) yield return Segments[(int)i];
    }

    /// <summary>Total uncompressed size of the file.</summary>
    public long SizeOf(ArchiveEntry e) => SegmentsOf(e).Sum(s => (long)s.Size);

    /// <summary>Uncompressed bytes of every segment after the first (the CR2W buffers: texture/mesh data).</summary>
    public long BufferBytesOf(ArchiveEntry e) => SegmentsOf(e).Skip(1).Sum(s => (long)s.Size);

    public byte[] ReadRaw(Segment s)
    {
        var buf = new byte[s.ZSize];
        _fs.Position = (long)s.Offset;
        _fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>Reads and decompresses one segment. Returns null if it is Oodle-compressed and Oodle isn't available.</summary>
    public byte[]? ReadSegment(Segment s)
    {
        var raw = ReadRaw(s);
        if (s.ZSize == s.Size) return raw;
        return Oodle.DecompressKark(raw, (int)s.Size);
    }

    public void Dispose() => _fs.Dispose();
}

public static class Fnv1a64
{
    public static ulong Hash(string path)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (byte b in Encoding.UTF8.GetBytes(path.ToLowerInvariant().Replace('/', '\\')))
        {
            h ^= b;
            h *= 0x100000001B3;
        }
        return h;
    }
}

/// <summary>Oodle (Kraken) via the game's own oo2ext_7_win64.dll — we never ship Oodle ourselves.</summary>
public static unsafe class Oodle
{
    // OodleLZ_Decompress(src, srcLen, dst, dstLen, fuzzSafe, checkCrc, verbosity, decBufBase, decBufSize,
    //                    callback, callbackUser, decoderMem, decoderMemSize, threadPhase)
    static delegate* unmanaged<byte*, long, byte*, long, int, int, int, long, long, long, long, long, long, int, long> _decompress;
    public static bool Available => _decompress != null;
    public static string? LoadedFrom { get; private set; }

    public static bool TryLoad(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out var lib)) continue;
            if (!NativeLibrary.TryGetExport(lib, "OodleLZ_Decompress", out var fn)) continue;
            _decompress = (delegate* unmanaged<byte*, long, byte*, long, int, int, int, long, long, long, long, long, long, int, long>)fn;
            LoadedFrom = path;
            return true;
        }
        return false;
    }

    public static byte[]? DecompressKark(byte[] raw, int size)
    {
        if (_decompress == null) return null;
        int start = 0;
        if (raw.Length >= 8 && raw[0] == 'K' && raw[1] == 'A' && raw[2] == 'R' && raw[3] == 'K') start = 8;
        if (start >= raw.Length) return null;
        var output = new byte[size];
        long n;
        fixed (byte* src = &raw[start])
        fixed (byte* dst = output)
            n = _decompress(src, raw.Length - start, dst, size, 1, 0, 0, 0, 0, 0, 0, 0, 0, 3);
        return n == size ? output : null;
    }
}
