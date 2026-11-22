using System.Text;

namespace VramRipperdoc;

/// <summary>
/// Which files each appearance (variant) of a mesh needs. A car with ten paint jobs ships ten sets of 4K textures but
/// only shows one at a time, so the scanner can count the dearest appearance instead of all of them.
///
/// On-disk layout (checked on the game's own meshes, see MeshVariantsTest):
/// - CMesh.appearances: array of handles, 1-based export index, to meshMeshAppearance exports (name CName, chunkMaterials
///   array of CName). Properties left at their default are not written: a missing name is the empty CName.
/// - CMesh.materialEntries: array of CMeshMaterialEntry structs {name, index Uint16 = 0, isLocalInstance Bool = false}.
///   chunkMaterials pick an entry by name; index counts within the external list or within the local list.
/// - externalMaterials (raRef) / preloadExternalMaterials (rRef): Uint16 per element, 1-based import index (0 = none).
/// - Local materials either as localMaterialBuffer {rawData DataBuffer (0x80000000 | 1-based buffer index),
///   rawDataHeaders [{offset, size}]} where each chunk of the buffer is a standalone CR2W file holding one
///   CMaterialInstance with its own import table, or as preloadLocalMaterialInstances / localMaterialInstances: handles
///   to CMaterialInstance exports of the mesh itself, whose refs go through the mesh's import table.
/// - CMaterialInstance: baseMaterial rRef, then after the property list's terminator its parameter values:
///   Uint32 count, then per value Uint32 size (counting itself), Uint16 name, Uint16 type, value bytes.
/// </summary>
public static class MeshVariants
{
    /// <summary>One appearance of a mesh and every depot path its materials reference.</summary>
    public sealed record Variant(string Name, IReadOnlyList<string> Paths);

    /// <param name="c">The parsed header of a .mesh file (root class CMesh).</param>
    /// <param name="head">The same header's raw uncompressed bytes (the file's first segment).</param>
    /// <param name="readBuffer">Returns buffer i (0-based, uncompressed) of the same file, or null if unavailable.</param>
    /// <returns>null when the file is not a mesh, has fewer than 2 appearances, or ANYTHING about how its
    /// appearances map to materials was not fully understood. Never throws.</returns>
    public static IReadOnlyList<Variant>? Read(Cr2wFile c, byte[] head, Func<int, byte[]?> readBuffer) => Read(c, head, readBuffer, null);

    /// <summary>What the self-test wants to know about one Read call.</summary>
    internal sealed class Diag
    {
        public string? Reason;           // why null was returned
        public string Form = "";         // which material lists the mesh has
        public int Appearances;
        /// <summary>Sizes of the external and local lists, and the entry indices pointing into each.</summary>
        public int ExtCount, LocalCount;
        public HashSet<int> ExtIndices = new(), LocalIndices = new();
        /// <summary>Appearances whose every material is an embedded CR2W with an empty import table (refers to nothing).</summary>
        public HashSet<int> EmptyAppearances = new();
        /// <summary>Per used local rawData material: paths found by walking its values vs. the chunk's own non-empty imports.</summary>
        public List<(IReadOnlyList<string> Paths, IReadOnlyList<string> ChunkImports)> RawMaterials = new();
    }

    internal static IReadOnlyList<Variant>? Read(Cr2wFile c, byte[] head, Func<int, byte[]?> readBuffer, Diag? d)
    {
        try { return ReadCore(c, head, readBuffer, d); }
        catch (Exception ex) { if (d != null) d.Reason = "exception: " + ex.GetType().Name; return null; }
    }

    static IReadOnlyList<Variant>? Fail(Diag? d, string reason) { if (d != null) d.Reason = reason; return null; }

    readonly record struct Prop(string Name, string Type, int Start, int End);

    static IReadOnlyList<Variant>? ReadCore(Cr2wFile c, byte[] head, Func<int, byte[]?> readBuffer, Diag? d)
    {
        if (c.RootClass != "CMesh") return Fail(d, "not a mesh");
        var root = ExportBody(c, head, 0, out _);
        if (root == null) return Fail(d, "unreadable mesh header");
        Prop? P(string name) { foreach (var p in root) if (p.Name == name) return p; return null; }

        // Cheap exit first: most meshes have a single appearance.
        var appProp = P("appearances");
        if (appProp is not { } ap || ap.Type != "array:handle:meshMeshAppearance" || ap.End - ap.Start < 4) return Fail(d, "fewer than 2 appearances");
        var handles = IntArray(head, ap, 4);
        if (handles == null) return Fail(d, "bad appearances array");
        if (handles.Count < 2) return Fail(d, "fewer than 2 appearances");
        if (d != null) d.Appearances = handles.Count;

        // The material lists. Each entry's index counts within ONE of them, so a mesh that fills two lists of the same
        // kind (external or local) is ambiguous and not handled.
        var ext = RefArray(head, P("externalMaterials"), "array:raRef:IMaterial");
        var preExt = RefArray(head, P("preloadExternalMaterials"), "array:rRef:IMaterial");
        var locH = IntArrayOf(head, P("localMaterialInstances"), "array:handle:IMaterial");
        var preLocH = IntArrayOf(head, P("preloadLocalMaterialInstances"), "array:handle:IMaterial");
        if (ext == null || preExt == null || locH == null || preLocH == null) return Fail(d, "bad material list");
        var rawHeaders = new List<(int Offset, int Size)>();
        int rawBuffer = -1;
        if (P("localMaterialBuffer") is { } lmb)
        {
            if (lmb.Type != "meshMeshMaterialBuffer") return Fail(d, "unknown localMaterialBuffer type");
            var body = Body(c, head, lmb.Start, lmb.End, out _);
            if (body == null) return Fail(d, "bad localMaterialBuffer");
            foreach (var f in body)
                switch (f.Name)
                {
                    case "rawData" when f.Type == "DataBuffer" && f.End - f.Start == 4:
                        rawBuffer = (int)(BitConverter.ToUInt32(head, f.Start) & 0xFFFF) - 1; break;
                    case "rawDataHeaders" when f.Type == "array:meshLocalMaterialHeader":
                        { var hs = Headers(c, head, f); if (hs == null) return Fail(d, "bad rawDataHeaders"); rawHeaders = hs; break; }
                    // Materials kept as handles inside the buffer struct: not seen in game files, so index meaning unconfirmed.
                    case "materials" when f.Type.StartsWith("array:") && f.End - f.Start >= 4 && BitConverter.ToUInt32(head, f.Start) == 0: break;
                    case "materials": return Fail(d, "localMaterialBuffer.materials (unconfirmed form)");
                    default: return Fail(d, "unknown localMaterialBuffer field " + f.Name);
                }
        }
        bool anyExt = ext.Count > 0 || preExt.Count > 0, rawLocal = rawHeaders.Count > 0, hdrLocal = locH.Count > 0 || preLocH.Count > 0;
        if (d != null)
            d.Form = (anyExt, rawLocal, hdrLocal) switch
            {
                (_, true, true) => "mixed local forms",
                (true, false, false) => "external only",
                (false, true, false) => "rawData local",
                (false, false, true) => "header-export local",
                (true, true, false) => "mixed external + rawData",
                (true, false, true) => "mixed external + header-export",
                _ => "no materials",
            };
        if (ext.Count > 0 && preExt.Count > 0) return Fail(d, "both external lists filled");
        if ((rawLocal ? 1 : 0) + (locH.Count > 0 ? 1 : 0) + (preLocH.Count > 0 ? 1 : 0) > 1) return Fail(d, "several local forms");
        var extList = ext.Count > 0 ? ext : preExt;
        var hdrList = locH.Count > 0 ? locH : preLocH;
        if (d != null) { d.ExtCount = extList.Count; d.LocalCount = rawLocal ? rawHeaders.Count : hdrList.Count; }

        // Material entries by name.
        if (P("materialEntries") is not { } me || me.Type != "array:CMeshMaterialEntry") return Fail(d, "no materialEntries");
        var entries = new Dictionary<string, (bool Local, int Index)>();
        {
            if (me.End - me.Start < 4) return Fail(d, "bad materialEntries");
            uint n = BitConverter.ToUInt32(head, me.Start);
            int p = me.Start + 4;
            for (uint i = 0; i < n; i++)
            {
                var body = Body(c, head, p, me.End, out int after);
                if (body == null) return Fail(d, "bad materialEntries");
                p = after;
                string? name = null; int index = 0; bool local = false;
                foreach (var f in body)
                    switch (f.Name)
                    {
                        case "name" when f.Type == "CName" && f.End - f.Start == 2: name = c.Name(BitConverter.ToUInt16(head, f.Start)); break;
                        case "index" when f.Type == "Uint16" && f.End - f.Start == 2: index = BitConverter.ToUInt16(head, f.Start); break;
                        case "isLocalInstance" when f.Type == "Bool" && f.End - f.Start == 1: local = head[f.Start] != 0; break;
                        default: return Fail(d, "unknown material entry field " + f.Name);
                    }
                name ??= "";
                // Two entries with one name: which one the game picks is not known, so only allow exact duplicates.
                if (entries.TryGetValue(name, out var had) && had != (local, index)) return Fail(d, "duplicate material name");
                entries[name] = (local, index);
                (local ? d?.LocalIndices : d?.ExtIndices)?.Add(index);
            }
            if (p != me.End) return Fail(d, "bad materialEntries");
        }

        // Appearances: name + chunkMaterials -> entries. chunkMaterials holds one name per render chunk; the game's own
        // meshes never list fewer, and what the engine draws for a chunk without a name is unknown, so that is not handled.
        int blob = c.Exports.FindIndex(x => x.ClassName == "rendRenderMeshBlob");
        if (blob < 0 || c.ReadExport(blob).GetValueOrDefault("header") is not Dictionary<string, object?> blobHeader
            || blobHeader.GetValueOrDefault("renderChunkInfos") is not List<object?> chunkInfos) return Fail(d, "render chunk count unknown");
        var apps = new List<(string Name, List<(bool Local, int Index)> Mats)>();
        foreach (int h in handles)
        {
            int ex = h - 1;
            if (ex < 0 || ex >= c.Exports.Count) return Fail(d, "bad appearance handle");
            if (c.Exports[ex].ClassName != "meshMeshAppearance") return Fail(d, "appearance is " + c.Exports[ex].ClassName);
            var body = ExportBody(c, head, ex, out _);
            if (body == null) return Fail(d, "unreadable appearance");
            string name = ""; var mats = new List<(bool, int)>(); int named = 0;
            foreach (var f in body)
                if (f.Name == "name" && f.Type == "CName" && f.End - f.Start == 2) name = c.Name(BitConverter.ToUInt16(head, f.Start));
                else if (f.Name == "chunkMaterials")
                {
                    if (f.Type != "array:CName") return Fail(d, "unknown chunkMaterials type");
                    var ids = IntArray(head, f, 2);
                    if (ids == null) return Fail(d, "bad chunkMaterials");
                    named = ids.Count;
                    foreach (var id in ids)
                    {
                        if (!entries.TryGetValue(c.Name(id), out var en)) return Fail(d, "unresolved chunk material name");
                        if (!mats.Contains(en)) mats.Add(en);
                    }
                }
            if (named < chunkInfos.Count) return Fail(d, "appearance names fewer materials than render chunks");
            apps.Add((name, mats));
        }

        // Paths per used material (parsed once each). Local materials that no appearance uses are never read.
        var cache = new Dictionary<(bool, int), List<string>>();
        var emptySource = new HashSet<(bool, int)>();
        byte[]? raw = null;
        foreach (var (_, mats) in apps)
            foreach (var m in mats)
            {
                if (cache.ContainsKey(m)) continue;
                var paths = new List<string>();
                if (!m.Local)
                {
                    if (m.Index >= extList.Count) return Fail(d, "external index out of range");
                    if (!AddImport(c.Imports, extList[m.Index], paths)) return Fail(d, "external material ref out of range");
                }
                else if (rawLocal)
                {
                    if (m.Index >= rawHeaders.Count) return Fail(d, "local index out of range");
                    if (raw == null)
                    {
                        if (rawBuffer < 0 || rawBuffer >= c.Buffers.Count) return Fail(d, "bad rawData buffer index");
                        raw = readBuffer(rawBuffer);
                        if (raw == null) return Fail(d, "rawData buffer unreadable");
                    }
                    var (off, size) = rawHeaders[m.Index];
                    if (off < 0 || size < 4 || (long)off + size > raw.Length) return Fail(d, "rawData chunk out of range");
                    var chunk = raw.AsSpan(off, size).ToArray();
                    if (chunk[0] != 'C' || chunk[1] != 'R' || chunk[2] != '2' || chunk[3] != 'W') return Fail(d, "rawData chunk is not CR2W");
                    Cr2wFile sub;
                    try { sub = new Cr2wFile(chunk); } catch { return Fail(d, "rawData chunk unreadable"); }
                    if (sub.Exports.Count == 0 || sub.RootClass != "CMaterialInstance") return Fail(d, "local material is " + sub.RootClass);
                    if (!MaterialPaths(sub, chunk, 0, paths, strict: false)) return Fail(d, "local material unreadable");
                    d?.RawMaterials.Add((paths.ToList(), sub.Imports.Where(s => s.Length > 0).Distinct().ToList()));
                    // The chunk is a file of its own: everything it refers to is in its import table, whatever the value types.
                    foreach (var s in sub.Imports) if (s.Length > 0 && !paths.Contains(s)) paths.Add(s);
                    if (sub.Imports.Count == 0) emptySource.Add(m);
                }
                else if (hdrList.Count > 0)
                {
                    if (m.Index >= hdrList.Count) return Fail(d, "local index out of range");
                    int ex = hdrList[m.Index] - 1;
                    if (ex < 0 || ex >= c.Exports.Count) return Fail(d, "bad local material handle");
                    if (c.Exports[ex].ClassName != "CMaterialInstance") return Fail(d, "local material is " + c.Exports[ex].ClassName);
                    if (!MaterialPaths(c, head, ex, paths, strict: true)) return Fail(d, "local material unreadable or has refs not followed");
                }
                else return Fail(d, "local material without local list");
                cache[m] = paths;
            }

        var result = new List<Variant>();
        foreach (var (name, mats) in apps)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var m in mats) foreach (var p in cache[m]) if (seen.Add(p)) list.Add(p);
            if (d != null && mats.Count > 0 && mats.All(emptySource.Contains)) d.EmptyAppearances.Add(result.Count);
            result.Add(new Variant(name, list));
        }
        return result;
    }

    /// <summary>
    /// A CMaterialInstance export's base material and every resource its parameter values point to, as written in the
    /// file's import table. False when the export's bytes do not walk cleanly to their end. With <paramref name="strict"/>
    /// it is also false when a value could hold a ref this code does not follow: a path left out of one appearance's list
    /// while another appearance lists it would wrongly read as "not needed here". (Embedded CR2W materials add their whole
    /// import table afterwards, so they need no such care.)
    /// </summary>
    static bool MaterialPaths(Cr2wFile f, byte[] data, int ex, List<string> paths, bool strict)
    {
        var props = ExportBody(f, data, ex, out int p);
        if (props == null) return false;
        var x = f.Exports[ex];
        int end = (int)(x.DataOffset + x.DataSize);
        foreach (var pr in props)
            if (pr.Name == "baseMaterial" && IsRef(pr.Type) && pr.End - pr.Start == 2) { if (!AddImport(f.Imports, BitConverter.ToUInt16(data, pr.Start), paths)) return false; }
            else if (strict && (pr.Name == "baseMaterial" || pr.Type.Contains("Ref:") || pr.Type.Contains("handle:"))) return false;
        if (p == end) return true; // no parameter values
        if (p + 4 > end) return false;
        uint n = BitConverter.ToUInt32(data, p); p += 4;
        for (uint i = 0; i < n; i++)
        {
            if (p + 8 > end) return false;
            int size = BitConverter.ToInt32(data, p);
            if (size < 8 || size > end - p) return false;
            string type = f.Name(BitConverter.ToUInt16(data, p + 6));
            if (IsRef(type) && size == 10) { if (!AddImport(f.Imports, BitConverter.ToUInt16(data, p + 8), paths)) return false; }
            else if (strict && !PlainTypes.Contains(type)) return false;
            p += size;
        }
        return p == end;
    }

    // Parameter value types that cannot hold a resource ref. The game's own materials use Float, Color, Vector4 and refs.
    static readonly HashSet<string> PlainTypes = new() { "Float", "Double", "Bool", "Color", "Vector2", "Vector3", "Vector4", "CName",
        "Int8", "Uint8", "Int16", "Uint16", "Int32", "Uint32", "Int64", "Uint64", "Matrix", "Quaternion", "EulerAngles" };
    static bool IsRef(string type) => type.StartsWith("rRef:") || type.StartsWith("raRef:");

    /// <summary>Adds the path of a 1-based import index (0 = no resource). False when the index is outside the table.</summary>
    static bool AddImport(List<string> imports, int oneBased, List<string> paths)
    {
        if (oneBased == 0) return true;
        if (oneBased > imports.Count) return false;
        var s = imports[oneBased - 1];
        if (s.Length > 0 && !paths.Contains(s)) paths.Add(s);
        return true;
    }

    static List<Prop>? ExportBody(Cr2wFile f, byte[] data, int ex, out int after)
    {
        after = 0;
        var x = f.Exports[ex];
        if ((long)x.DataOffset + x.DataSize > data.Length || x.DataSize == 0) return null;
        return Body(f, data, (int)x.DataOffset, (int)(x.DataOffset + x.DataSize), out after);
    }

    /// <summary>
    /// A class or struct body: one leading byte, then (name Uint16, type Uint16, size Uint32 counting itself, value)
    /// until a 0 name. Null when anything runs past <paramref name="end"/>; <paramref name="after"/> follows the 0 name.
    /// </summary>
    static List<Prop>? Body(Cr2wFile f, byte[] d, int p, int end, out int after)
    {
        after = end;
        if (p < 0 || end > d.Length || p >= end) return null;
        var list = new List<Prop>();
        p++;
        while (true)
        {
            if (p == end) { after = p; return list; }
            if (p + 2 > end) return null;
            ushort n = BitConverter.ToUInt16(d, p); p += 2;
            if (n == 0) { after = p; return list; }
            if (p + 6 > end) return null;
            ushort t = BitConverter.ToUInt16(d, p);
            int size = BitConverter.ToInt32(d, p + 2) - 4;
            p += 6;
            if (size < 0 || size > end - p) return null;
            list.Add(new Prop(f.Name(n), f.Name(t), p, p + size));
            p += size;
        }
    }

    /// <summary>A Uint32 count followed by fixed-size integers that fill the value exactly; null otherwise.</summary>
    static List<int>? IntArray(byte[] d, Prop p, int width)
    {
        if (p.End - p.Start < 4) return null;
        long n = BitConverter.ToUInt32(d, p.Start);
        if (4 + n * width != p.End - p.Start) return null;
        var list = new List<int>((int)n);
        for (int i = 0, q = p.Start + 4; i < n; i++, q += width)
            list.Add(width == 2 ? BitConverter.ToUInt16(d, q) : BitConverter.ToInt32(d, q));
        return list;
    }

    // A missing property is an empty list; a property of an unexpected type or size is null (not understood).
    static List<int>? RefArray(byte[] d, Prop? p, string type) => p is not { } v ? new() : v.Type == type ? IntArray(d, v, 2) : null;
    static List<int>? IntArrayOf(byte[] d, Prop? p, string type) => p is not { } v ? new() : v.Type == type ? IntArray(d, v, 4) : null;

    static List<(int, int)>? Headers(Cr2wFile c, byte[] d, Prop p)
    {
        if (p.End - p.Start < 4) return null;
        uint n = BitConverter.ToUInt32(d, p.Start);
        var list = new List<(int, int)>();
        int q = p.Start + 4;
        for (uint i = 0; i < n; i++)
        {
            var body = Body(c, d, q, p.End, out int after);
            if (body == null) return null;
            q = after;
            long off = 0, size = 0;
            foreach (var f in body)
                if (f.Type != "Uint32" || f.End - f.Start != 4) return null;
                else if (f.Name == "offset") off = BitConverter.ToUInt32(d, f.Start);
                else if (f.Name == "size") size = BitConverter.ToUInt32(d, f.Start);
                else return null;
            if (off > int.MaxValue || size > int.MaxValue) return null;
            list.Add(((int)off, (int)size));
        }
        return q == p.End ? list : null;
    }
}

/// <summary>Developer self-test of MeshVariants over the game's own meshes.</summary>
public static class MeshVariantsTest
{
    /// <summary>Developer self-test over the game's own meshes; returns 0 when all checks pass.</summary>
    public static int Run(string gameDir, int every)
    {
        Oodle.TryLoad(new[] { Path.Combine(gameDir, "bin", "x64", "oo2ext_7_win64.dll") });
        if (!Oodle.Available) { Console.WriteLine("Oodle not found."); return 1; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int meshes = 0, multi = 0, handled = 0, failures = 0, rawFull = 0, rawPartial = 0, exactIdx = 0, emptyOk = 0;
        var reasons = new Dictionary<string, int>();
        long readTicks = 0;
        int hdrMeshes = 0, hdrLeft = 0, hdrClaimed = 0;
        var hdrLeftExamples = new List<string>();
        var samples = new List<(byte[] Head, byte[]?[] Buffers)>();
        var chunkVsRender = new Dictionary<string, int>();
        var forms = new Dictionary<string, (int Seen, int Handled)>();
        var examples = new List<string>();
        bool vehicleExample = false;
        var exampleForms = new HashSet<string>();
        void Bad(string what) { failures++; if (failures <= 20) Console.WriteLine("FAIL " + what); }

        var archives = new[] { "content", "ep1" }.Select(s => Path.Combine(gameDir, "archive", "pc", s)).Where(Directory.Exists)
            .SelectMany(dir => Directory.GetFiles(dir, "*.archive"))
            .Where(f => !Path.GetFileName(f).StartsWith("lang_") && !Path.GetFileName(f).StartsWith("audio_"));
        foreach (var file in archives)
        {
            using var a = new RdarArchive(file);
            string arch = Path.GetFileNameWithoutExtension(file);
            foreach (var e in a.Entries)
            {
                if (e.SegmentsEnd - e.SegmentsStart < 2) continue; // a mesh always has its geometry buffer
                byte[]? head;
                try { head = a.ReadSegment(a.Segments[(int)e.SegmentsStart]); } catch { continue; }
                if (head == null || head.Length < 4 || head[0] != 'C' || head[1] != 'R') continue;
                Cr2wFile c;
                try { c = new Cr2wFile(head); } catch { continue; }
                if (c.RootClass != "CMesh" || meshes++ % every != 0) continue;
                var segs = a.SegmentsOf(e).Skip(1).ToList();
                var buffers = new Dictionary<int, byte[]?>();
                byte[]? Buf(int i)
                {
                    if (i < 0 || i >= segs.Count || segs.Count != c.Buffers.Count) return null;
                    if (!buffers.TryGetValue(i, out var b)) buffers[i] = b = a.ReadSegment(segs[i]);
                    return b;
                }
                var d = new MeshVariants.Diag();
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var v = MeshVariants.Read(c, head, Buf, d);
                readTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                if (d.Reason == "fewer than 2 appearances" || d.Reason == "not a mesh") continue;
                multi++;
                (int Seen, int Handled) fs = forms.TryGetValue(d.Form, out var fv) ? fv : (0, 0);
                if (v == null)
                {
                    var r = d.Reason ?? "(no reason)";
                    reasons[r] = reasons.GetValueOrDefault(r) + 1;
                    forms[d.Form] = (fs.Seen + 1, fs.Handled);
                    continue;
                }
                handled++;
                forms[d.Form] = (fs.Seen + 1, fs.Handled + 1);
                if (handled % 7 == 0 && samples.Count < 300) samples.Add((head, Enumerable.Range(0, segs.Count).Select(Buf).ToArray()));
                string id = $"{arch} {e.Hash:X16}";
                if (v.Count != d.Appearances) Bad($"{id}: {v.Count} variants for {d.Appearances} appearances");

                // Hard check 1: every path is literally an import of the mesh or of an embedded material. The embedded
                // import tables are found independently: every CR2W file inside any of the mesh's buffers.
                var allowed = new HashSet<string>(c.Imports, StringComparer.Ordinal);
                if (v.SelectMany(x => x.Paths).Any(p => !allowed.Contains(p)))
                    for (int i = 0; i < segs.Count; i++)
                    {
                        var b = segs.Count == c.Buffers.Count ? Buf(i) : a.ReadSegment(segs[i]);
                        if (b == null) continue;
                        for (int p = 0; p + 4 <= b.Length; p++)
                            if (b[p] == 'C' && b[p + 1] == 'R' && b[p + 2] == '2' && b[p + 3] == 'W')
                                try { allowed.UnionWith(new Cr2wFile(b[p..]).Imports); } catch { }
                    }
                foreach (var x in v)
                    foreach (var p in x.Paths)
                        if (p.Length == 0 || !allowed.Contains(p)) Bad($"{id}: appearance '{x.Name}' path not in any import table: [{p}]");

                // Hard check 2: an appearance with chunk materials has at least one path, unless every material it uses is an
                // embedded CMaterialInstance with an empty import table (it can refer to nothing). The chunkMaterials count
                // is re-read independently through the loose ReadExport tree.
                var counts = ChunkMaterialCounts(c);
                for (int i = 0; i < v.Count && i < counts.Count; i++)
                    if (counts[i] > 0 && v[i].Paths.Count == 0 && d.EmptyAppearances.Contains(i)) emptyOk++;
                    else if (counts[i] > 0 && v[i].Paths.Count == 0) Bad($"{id}: appearance '{v[i].Name}' has {counts[i]} chunk materials but no paths");

                // Does every appearance name a material for every render chunk? (Fewer could mean an engine fallback.)
                int blob = c.Exports.FindIndex(x => x.ClassName == "rendRenderMeshBlob");
                if (blob >= 0 && c.ReadExport(blob).GetValueOrDefault("header") is Dictionary<string, object?> hdr && hdr.GetValueOrDefault("renderChunkInfos") is List<object?> rci)
                    foreach (var n in counts) { var k = n == rci.Count ? "equal" : n < rci.Count ? (n == 0 ? "none" : "fewer") : "more"; chunkVsRender[k] = chunkVsRender.GetValueOrDefault(k) + 1; }
                else chunkVsRender["no chunk info"] = chunkVsRender.GetValueOrDefault("no chunk info") + 1;

                // Each list is indexed 0..n-1 by its own entries (not one index across both lists).
                if (d.ExtIndices.SetEquals(Enumerable.Range(0, d.ExtCount)) && d.LocalIndices.SetEquals(Enumerable.Range(0, d.LocalCount))) exactIdx++;
                foreach (var (paths, imports) in d.RawMaterials)
                    if (imports.All(paths.Contains)) rawFull++; else rawPartial++;

                // Header-export materials have no import table of their own to compare with: list the mesh imports that
                // no appearance claims (unused materials, embedded textures...), they count as shared.
                if (d.Form.Contains("header-export"))
                {
                    hdrMeshes++;
                    var claimed = v.SelectMany(x => x.Paths).ToHashSet();
                    var left = c.Imports.Where(i => i.Length > 0 && !claimed.Contains(i)).Distinct().ToList();
                    if (left.Count > 0) { hdrLeft++; foreach (var x in left) if (hdrLeftExamples.Count < 8) hdrLeftExamples.Add(x); }
                    hdrClaimed += claimed.Count(p => !p.EndsWith(".mi") && !p.EndsWith(".mt") && !p.EndsWith(".remt"));
                }

                // Examples: meshes whose appearances need different files, one per material form plus one car body.
                var sets = v.Select(x => new HashSet<string>(x.Paths)).ToList();
                if (examples.Count < 5 && sets.Skip(1).Any(s => !s.SetEquals(sets[0])))
                {
                    bool vehicle = v.Any(x => x.Paths.Any(p => p.StartsWith(@"base\vehicles\") && p.Contains(@"\entities\meshes\")));
                    if (vehicle ? vehicleExample : !exampleForms.Add(d.Form) || (!vehicleExample && examples.Count == 4)) continue;
                    vehicleExample |= vehicle;
                    var sb = new StringBuilder($"{id} ({d.Form}, {v.Count} appearances){(vehicle ? " [vehicle]" : "")}\n");
                    int shown = 0;
                    for (int i = 0; i < v.Count && shown < 4; i++)
                    {
                        var only = v[i].Paths.Where(p => !sets.Where((_, k) => k != i).Any(s => s.Contains(p))).ToList();
                        if (only.Count == 0) continue;
                        shown++;
                        sb.Append($"    '{v[i].Name}' ({v[i].Paths.Count} paths), only here:\n");
                        foreach (var p in only.Take(4)) sb.Append($"        {p}\n");
                        if (only.Count > 4) sb.Append($"        ... {only.Count - 4} more\n");
                    }
                    examples.Add(sb.ToString());
                }
            }
        }

        Console.WriteLine($"Meshes seen (every {every}th CMesh): {meshes / every + (meshes % every > 0 ? 1 : 0)} of {meshes}; with 2+ appearances: {multi}");
        Console.WriteLine($"  handled: {handled}, returned null: {multi - handled}");
        foreach (var (r, n) in reasons.OrderByDescending(kv => kv.Value)) Console.WriteLine($"    {n,6}  {r}");
        Console.WriteLine("Material forms (seen / handled):");
        foreach (var (f, (s, h)) in forms.OrderByDescending(kv => kv.Value.Seen)) Console.WriteLine($"    {s,6} / {h,-6} {f}");
        Console.WriteLine($"Appearances with chunk materials but no paths because their embedded materials have empty import tables: {emptyOk}");
        Console.WriteLine("Appearances' chunkMaterials count vs. render chunks: " + string.Join(", ", chunkVsRender.Select(kv => $"{kv.Key} {kv.Value}")));
        Console.WriteLine($"Handled meshes whose entries index exactly 0..n-1 of the external and of the local list: {exactIdx} of {handled}");
        Console.WriteLine($"Embedded (rawData) materials used by appearances: {rawFull + rawPartial}; walking their values found every import: {rawFull}, missed some: {rawPartial} (Read adds the whole import table either way)");
        Console.WriteLine($"Header-export meshes: {hdrMeshes}; claimed non-material paths (textures etc.): {hdrClaimed}; meshes with imports no appearance claims: {hdrLeft}");
        foreach (var x in hdrLeftExamples) Console.WriteLine("    e.g. " + x);
        Console.WriteLine($"Time inside Read for all {meshes / every + (meshes % every > 0 ? 1 : 0)} sampled meshes, buffer reads included: {readTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");

        // Malformed files: random bytes changed in the header and the embedded material buffer must never make Read throw.
        var rng = new Random(2077);
        int calls = 0, survived = 0;
        foreach (var (h0, b0) in samples)
            for (int k = 0; k < 30; k++)
            {
                var h = (byte[])h0.Clone();
                var bufs = b0.Select(b => b == null ? null : (byte[])b.Clone()).ToArray();
                for (int n = rng.Next(1, 6); n > 0; n--) h[rng.Next(160, h.Length)] = (byte)rng.Next(256);
                if (k % 2 == 1) foreach (var b in bufs) if (b != null && b.Length < 1 << 20) for (int n = rng.Next(1, 6); n > 0; n--) b[rng.Next(b.Length)] = (byte)rng.Next(256);
                Cr2wFile fc;
                try { fc = new Cr2wFile(h); } catch { continue; }
                calls++;
                try { var r = MeshVariants.Read(fc, h, i => i >= 0 && i < bufs.Length ? bufs[i] : throw new IndexOutOfRangeException()); if (r != null) survived++; }
                catch (Exception ex) { Bad("Read threw on a damaged file: " + ex.GetType().Name); }
            }
        Console.WriteLine($"Damaged-file runs: {calls} on {samples.Count} meshes, none threw; still answered {survived} (unharmed parts), null {calls - survived}");

        Console.WriteLine("Examples of appearances with different files:");
        foreach (var x in examples) Console.Write("  " + x);
        Console.WriteLine($"Hard-check failures: {failures}   ({sw.Elapsed.TotalSeconds:0}s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>chunkMaterials count per appearance, in the mesh's appearance order (via the loose ReadExport tree).</summary>
    static List<int> ChunkMaterialCounts(Cr2wFile c)
    {
        var list = new List<int>();
        if (c.ReadExport(0).GetValueOrDefault("appearances") is not List<object?> hs) return list;
        foreach (var h in hs)
            list.Add(h is int i && i >= 1 && i <= c.Exports.Count && c.ReadExport(i - 1).GetValueOrDefault("chunkMaterials") is List<object?> cm ? cm.Count : 0);
        return list;
    }
}
