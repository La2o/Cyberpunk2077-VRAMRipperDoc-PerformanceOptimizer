using System.Text;

namespace VramRipperdoc;

public sealed record Cr2wExport(string ClassName, uint DataOffset, uint DataSize, uint ParentId);
public sealed record Cr2wBuffer(uint Flags, uint Index, uint Offset, uint DiskSize, uint MemSize);

/// <summary>
/// Minimal reader for REDengine 4 CR2W resource headers. Only the parts needed to measure
/// textures and meshes are decoded; unknown values are skipped using their serialized size.
/// </summary>
public sealed class Cr2wFile
{
    public uint Version;
    public List<string> Names = new();
    /// <summary>Depot paths this file references (textures, materials, meshes...). Used to put names on hashes.</summary>
    public List<string> Imports = new();
    public List<ushort> ImportFlags = new();
    public List<Cr2wExport> Exports = new();
    public List<Cr2wBuffer> Buffers = new();
    readonly byte[] _data;

    public Cr2wFile(byte[] data)
    {
        _data = data;
        var br = new BinaryReader(new MemoryStream(data));
        if (br.ReadUInt32() != 0x57325243) throw new InvalidDataException("Not CR2W"); // "CR2W"
        Version = br.ReadUInt32();
        br.ReadUInt32(); // flags
        br.ReadUInt64(); // timestamp
        br.ReadUInt32(); // build version
        br.ReadUInt32(); // objects end
        br.ReadUInt32(); // buffers end
        br.ReadUInt32(); // crc32
        br.ReadUInt32(); // num chunks
        var tables = new (uint Offset, uint Count)[10];
        for (int i = 0; i < 10; i++) { tables[i] = (br.ReadUInt32(), br.ReadUInt32()); br.ReadUInt32(); }

        uint strOff = tables[0].Offset;
        br.BaseStream.Position = tables[1].Offset;
        for (int i = 0; i < tables[1].Count; i++)
        {
            uint off = br.ReadUInt32(); br.ReadUInt32();
            Names.Add(ReadCString(strOff + off));
        }
        br.BaseStream.Position = tables[2].Offset;
        for (int i = 0; i < tables[2].Count; i++)
        {
            uint off = br.ReadUInt32(); br.ReadUInt16(); ushort flags = br.ReadUInt16();
            Imports.Add(ReadCString(strOff + off));
            ImportFlags.Add(flags);
        }
        br.BaseStream.Position = tables[4].Offset;
        for (int i = 0; i < tables[4].Count; i++)
        {
            ushort cls = br.ReadUInt16(); br.ReadUInt16();
            uint parent = br.ReadUInt32(); uint size = br.ReadUInt32(); uint off = br.ReadUInt32();
            br.ReadUInt32(); br.ReadUInt32();
            Exports.Add(new Cr2wExport(Name(cls), off, size, parent));
        }
        br.BaseStream.Position = tables[5].Offset;
        for (int i = 0; i < tables[5].Count; i++)
        {
            Buffers.Add(new Cr2wBuffer(br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32()));
            br.ReadUInt32(); // crc32
        }
    }

    string ReadCString(uint pos)
    {
        int end = (int)pos;
        while (end < _data.Length && _data[end] != 0) end++;
        return Encoding.UTF8.GetString(_data, (int)pos, end - (int)pos);
    }

    public string Name(int idx) => idx >= 0 && idx < Names.Count ? Names[idx] : $"#{idx}";

    public string RootClass => Exports.Count > 0 ? Exports[0].ClassName : "";

    /// <summary>Reads the top-level properties of an export into a loose tree (Dictionary / List / primitives).</summary>
    public Dictionary<string, object?> ReadExport(int index)
    {
        var ex = Exports[index];
        if (ex.DataOffset + ex.DataSize > _data.Length) return new();
        var r = new PropReader(this, _data, (int)ex.DataOffset, (int)(ex.DataOffset + ex.DataSize));
        return r.ReadClassBody(leadingZero: true);
    }

    sealed class PropReader
    {
        readonly Cr2wFile _f; readonly byte[] _d; int _p; readonly int _end;
        public PropReader(Cr2wFile f, byte[] d, int start, int end) { _f = f; _d = d; _p = start; _end = end; }

        ushort U16() { var v = BitConverter.ToUInt16(_d, _p); _p += 2; return v; }
        uint U32() { var v = BitConverter.ToUInt32(_d, _p); _p += 4; return v; }

        public Dictionary<string, object?> ReadClassBody(bool leadingZero)
        {
            var props = new Dictionary<string, object?>();
            if (leadingZero) _p++;
            while (_p + 2 <= _end)
            {
                ushort nameId = U16();
                if (nameId == 0) break;
                ushort typeId = U16();
                uint size = U32() - 4;
                int valueEnd = _p + (int)size;
                if (valueEnd > _end || valueEnd < _p) break;
                string name = _f.Name(nameId), type = _f.Name(typeId);
                object? val = null;
                try { val = ReadValue(type, valueEnd); } catch { /* unknown layout: skip */ }
                props[name] = val;
                _p = valueEnd;
            }
            return props;
        }

        object? ReadValue(string type, int limit)
        {
            switch (type)
            {
                case "Bool": case "Uint8": return _d[_p++];
                case "Int8": return (sbyte)_d[_p++];
                case "Uint16": return U16();
                case "Int16": { var v = BitConverter.ToInt16(_d, _p); _p += 2; return v; }
                case "Uint32": return U32();
                case "Int32": { var v = BitConverter.ToInt32(_d, _p); _p += 4; return v; }
                case "Float": { var v = BitConverter.ToSingle(_d, _p); _p += 4; return v; }
                case "Uint64": { var v = BitConverter.ToUInt64(_d, _p); _p += 8; return v; }
                case "CName": return _f.Name(U16());
            }
            if (type.StartsWith("handle:") || type.StartsWith("whandle:"))
            { var v = BitConverter.ToInt32(_d, _p); _p += 4; return v; }
            if (type.StartsWith("array:"))
            {
                string inner = type[6..];
                uint count = U32();
                var list = new List<object?>();
                for (int i = 0; i < count && _p < limit; i++) list.Add(ReadValue(inner, limit));
                return list;
            }
            if (type.StartsWith('['))
            {
                int close = type.IndexOf(']');
                string inner = type[(close + 1)..];
                uint count = U32();
                var list = new List<object?>();
                for (int i = 0; i < count && _p < limit; i++) list.Add(ReadValue(inner, limit));
                return list;
            }
            if (IsStructType(type))
            {
                var sub = new PropReader(_f, _d, _p, limit);
                var body = sub.ReadClassBody(leadingZero: true);
                _p = sub._p;
                return body;
            }
            // Enums, buffers, refs, strings, etc.: caller skips by size. Enums are a CName index.
            if (limit - _p == 2) return _f.Name(U16());
            return null;
        }

        // Structures we descend into; everything else non-primitive is skipped by size.
        static bool IsStructType(string t) => t.StartsWith("rend") || t.StartsWith("STexture") || t.StartsWith("Gpu")
            || t is "meshMeshParamGpuChunks" or "CMeshMaterialEntry" or "Box" or "Vector4";
    }
}
