namespace VramRipperdoc;

/// <summary>
/// Halves a cooked texture (.xbm) by dropping its top mip levels. The remaining mips are the
/// game's own pre-computed ones, so no re-encoding happens and quality equals the game's
/// "lower texture quality" path for that one file. Only plain 2D textures are handled.
/// </summary>
public static class TextureShrinker
{
    // A parsed property with the byte positions needed to patch it in place.
    sealed class Prop
    {
        public string Name = "", Type = "";
        public int SizePos;          // position of the u32 size field
        public int ValueStart, ValueEnd;
        public List<Prop>? Fields;   // struct value
        public List<(int Start, int End, List<Prop>? Fields)>? Elements; // array value
        public int CountPos;
        public Prop? Field(string n) => Fields?.FirstOrDefault(f => f.Name == n);
    }

    public sealed record Result(byte[] HeaderRaw, uint HeaderSize, byte[] BufferRaw, uint BufferSize, byte[] Sha1, int NewWidth, int NewHeight);

    /// <summary>Returns null (and a reason) when the texture isn't a safe candidate.</summary>
    public static Result? Shrink(byte[] head, byte[] buffer, int dropMips, out string? reason)
    {
        reason = null;
        var c = new Cr2wFile(head);
        int blobIdx = c.Exports.FindIndex(x => x.ClassName == "rendRenderTextureBlobPC");
        if (c.RootClass != "CBitmapTexture" || blobIdx < 0) { reason = "not a plain 2D texture"; return null; }
        if (c.Buffers.Count != 1 || BitConverter.ToUInt32(head, 44 + 6 * 12) != 0) { reason = "unexpected buffer layout"; return null; }

        var root = ParseExport(c, head, 0);
        var blob = ParseExport(c, head, blobIdx);
        var header = blob.FirstOrDefault(p => p.Name == "header");
        var sizeInfo = header?.Field("sizeInfo");
        var texInfo = header?.Field("textureInfo");
        var mipInfo = header?.Field("mipMapInfo");
        if (header?.Fields == null || sizeInfo == null || texInfo == null || mipInfo?.Elements == null) { reason = "texture header not understood"; return null; }

        int width = (int)ReadNum(head, sizeInfo.Field("width")), height = (int)ReadNum(head, sizeInfo.Field("height"));
        int mipCount = (int)ReadNum(head, texInfo.Field("mipCount"), 1);
        int sliceCount = (int)ReadNum(head, texInfo.Field("sliceCount"), 1);
        long dataSize = ReadNum(head, texInfo.Field("textureDataSize"));
        if (sliceCount != 1 || sizeInfo.Field("depth") is { } d && ReadNum(head, d) > 1) { reason = "arrays/volumes not supported"; return null; }
        if (mipInfo.Elements.Count != mipCount || mipCount <= dropMips) { reason = "not enough mip levels"; return null; }
        if (dataSize != buffer.Length) { reason = "texture data size mismatch"; return null; }

        var keep = mipInfo.Elements[dropMips];
        long cut = ReadNum(head, keep.Fields?.FirstOrDefault(f => f.Name == "placement")?.Fields?.FirstOrDefault(f => f.Name == "offset"));
        if (cut <= 0 || cut >= buffer.Length) { reason = "mip placement not understood"; return null; }
        var newData = buffer[(int)cut..];

        // 1) Patch fixed-size values in place (positions stay valid until we remove bytes below).
        var h = (byte[])head.Clone();
        int newW = Math.Max(1, width >> dropMips), newH = Math.Max(1, height >> dropMips);
        WriteNum(h, sizeInfo.Field("width"), newW);
        WriteNum(h, sizeInfo.Field("height"), newH);
        WriteNum(h, texInfo.Field("mipCount"), mipCount - dropMips);
        WriteNum(h, texInfo.Field("textureDataSize"), newData.Length);
        if (texInfo.Field("sliceSize") is { } ss) WriteNum(h, ss, newData.Length);
        // The resource's own width/height describe the cooked size when they match it.
        var rw = root.FirstOrDefault(p => p.Name == "width"); var rh = root.FirstOrDefault(p => p.Name == "height");
        if (rw != null && rh != null && ReadNum(head, rw) == width && ReadNum(head, rh) == height)
        { WriteNum(h, rw, newW); WriteNum(h, rh, newH); }
        for (int i = dropMips; i < mipInfo.Elements.Count; i++)
        {
            var off = mipInfo.Elements[i].Fields?.FirstOrDefault(f => f.Name == "placement")?.Fields?.FirstOrDefault(f => f.Name == "offset");
            if (off != null) WriteNum(h, off, ReadNum(head, off) - cut);
        }
        BitConverter.GetBytes((uint)(mipCount - dropMips)).CopyTo(h, mipInfo.CountPos);

        // 2) Remove the dropped mipMapInfo elements and fix every size/offset after them.
        int removeAt = mipInfo.Elements[0].Start, removeLen = keep.Start - removeAt;
        AddU32(h, mipInfo.SizePos, -removeLen);
        AddU32(h, header.SizePos, -removeLen);
        var outHead = new byte[h.Length - removeLen];
        Array.Copy(h, 0, outHead, 0, removeAt);
        Array.Copy(h, removeAt + removeLen, outHead, removeAt, h.Length - removeAt - removeLen);
        FixTables(outHead, blobIdx, removeAt, removeLen);

        // 3) New buffer, stored compressed like the game's, then buffer table + header CRCs.
        var stored = Oodle.CompressKark(newData) is { } z && z.Length < newData.Length ? z : newData;
        int bufEntry = (int)U32(outHead, 40 + 5 * 12);
        uint objectsEnd = U32(outHead, 24);
        Put(outHead, bufEntry + 8, objectsEnd);                 // offset (buffers follow the objects)
        Put(outHead, bufEntry + 12, (uint)stored.Length);       // disk size
        Put(outHead, bufEntry + 16, (uint)newData.Length);      // memory size
        Put(outHead, bufEntry + 20, Crc32.Compute(stored));     // crc of stored bytes
        Put(outHead, 28, (uint)(objectsEnd + stored.Length));   // buffersEnd
        Rechecksum(outHead);

        var headRaw = Oodle.CompressKark(outHead) ?? throw new InvalidOperationException("Oodle compression unavailable");
        return new Result(headRaw, (uint)outHead.Length, stored, (uint)newData.Length,
            ArchiveWriter.FileSha1(outHead, new[] { stored }), newW, newH);
    }

    /// <summary>Recomputes table CRCs and the header CRC (rules verified on vanilla files).</summary>
    public static void Rechecksum(byte[] h)
    {
        int[] entrySize = { 1, 8, 8, 16, 24, 24 };
        for (int t = 0; t < entrySize.Length; t++)
        {
            uint off = U32(h, 40 + t * 12), count = U32(h, 44 + t * 12);
            if (count > 0) Put(h, 48 + t * 12, Crc32.Compute(h.AsSpan((int)off, (int)(count * entrySize[t]))));
        }
        Put(h, 32, 0xDEADBEEF);
        Put(h, 32, Crc32.Compute(h.AsSpan(0, 160)));
    }

    static void FixTables(byte[] h, int blobIdx, int removeAt, int removeLen)
    {
        for (int t = 0; t < 10; t++)
        {
            uint off = U32(h, 40 + t * 12);
            if (U32(h, 44 + t * 12) > 0 && off > removeAt) Put(h, 40 + t * 12, (uint)(off - removeLen));
        }
        int expTable = (int)U32(h, 40 + 4 * 12), expCount = (int)U32(h, 44 + 4 * 12);
        for (int i = 0; i < expCount; i++)
        {
            int p = expTable + i * 24;
            if (i == blobIdx) Put(h, p + 8, (uint)(U32(h, p + 8) - removeLen));
            uint dataOff = U32(h, p + 12);
            if (dataOff > removeAt) Put(h, p + 12, (uint)(dataOff - removeLen));
        }
        Put(h, 24, (uint)(U32(h, 24) - removeLen)); // objectsEnd
    }

    static List<Prop> ParseExport(Cr2wFile c, byte[] d, int idx)
    {
        var ex = c.Exports[idx];
        int p = (int)ex.DataOffset;
        return ParseBody(c, d, ref p, (int)(ex.DataOffset + ex.DataSize));
    }

    static List<Prop> ParseBody(Cr2wFile c, byte[] d, ref int p, int end)
    {
        var list = new List<Prop>();
        p++; // leading zero byte
        while (p + 2 <= end)
        {
            ushort nameId = BitConverter.ToUInt16(d, p);
            if (nameId == 0) { p += 2; break; }
            var prop = new Prop { Name = c.Name(nameId), Type = c.Name(BitConverter.ToUInt16(d, p + 2)), SizePos = p + 4 };
            uint size = U32(d, p + 4);
            prop.ValueStart = p + 8;
            prop.ValueEnd = p + 4 + (int)size;
            ParseValue(c, d, prop);
            list.Add(prop);
            p = prop.ValueEnd;
        }
        return list;
    }

    static void ParseValue(Cr2wFile c, byte[] d, Prop prop)
    {
        string t = prop.Type;
        if (IsStruct(t))
        {
            int q = prop.ValueStart;
            prop.Fields = ParseBody(c, d, ref q, prop.ValueEnd);
        }
        else if (t.StartsWith("array:") && IsStruct(t[6..]))
        {
            prop.CountPos = prop.ValueStart;
            uint n = U32(d, prop.ValueStart);
            int q = prop.ValueStart + 4;
            prop.Elements = new();
            for (int i = 0; i < n && q < prop.ValueEnd; i++)
            {
                int s = q;
                var f = ParseBody(c, d, ref q, prop.ValueEnd);
                prop.Elements.Add((s, q, f));
            }
        }
    }

    static bool IsStruct(string t) => t.StartsWith("rendRenderTextureBlob");

    static long ReadNum(byte[] d, Prop? p, long def = 0) => p == null ? def : p.Type switch
    {
        "Uint8" => d[p.ValueStart],
        "Uint16" => BitConverter.ToUInt16(d, p.ValueStart),
        "Uint32" => U32(d, p.ValueStart),
        "Int32" => BitConverter.ToInt32(d, p.ValueStart),
        _ => def,
    };

    static void WriteNum(byte[] d, Prop? p, long v)
    {
        if (p == null) return;
        switch (p.Type)
        {
            case "Uint8": d[p.ValueStart] = (byte)v; break;
            case "Uint16": BitConverter.GetBytes((ushort)v).CopyTo(d, p.ValueStart); break;
            case "Uint32": case "Int32": BitConverter.GetBytes((uint)v).CopyTo(d, p.ValueStart); break;
            default: throw new InvalidDataException($"unexpected type {p.Type} for {p.Name}");
        }
    }

    static uint U32(byte[] d, int p) => BitConverter.ToUInt32(d, p);
    static void Put(byte[] d, int p, uint v) => BitConverter.GetBytes(v).CopyTo(d, p);
    static void AddU32(byte[] d, int p, int delta) => Put(d, p, (uint)(U32(d, p) + delta));
}
