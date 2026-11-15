using System.Security.Cryptography;
using System.Text.Json;

namespace VramRipperdoc;

/// <summary>
/// Stage 2: writes the budget plan as ONE extra archive that loads before the mods it slims down.
/// Original archives are never modified; deleting the budget archive restores everything.
/// </summary>
public static class BudgetArchive
{
    public sealed record Outcome(string Path, int Textures, long SavedBytes, List<string> Skipped);

    /// <summary>
    /// A small JSON file inside the fix archive: each texture it replaces and the SHA1 of the mod file it was made from,
    /// so a later scan can tell when mods changed under it. The game never asks for this path, so it never loads it.
    /// </summary>
    public static readonly ulong ManifestHash = Fnv1a64.Hash(@"ripperdoc\budget_fix_manifest.json");

    /// <summary>
    /// Whether a fix still matches the installed mods. Changed: the mod file a texture was made from is different now
    /// (the mod was updated, or another mod now wins), so the fix shows an old version. Removed: no installed mod has
    /// that file any more, so the fix brings back a texture of a mod you removed. Fixes made by v0.3.1 or older have no
    /// manifest, so for them only removals can be found.
    /// </summary>
    public sealed record FixCheck(int Textures, List<string> Changed, List<string> Removed, bool HasManifest)
    {
        public bool Stale => Changed.Count > 0 || Removed.Count > 0;

        public string Describe() => string.Join(" and ", new[]
        {
            Changed.Count > 0 ? $"{Changed.Count:N0} of its {Textures:N0} textures come from mods that were updated since" : null,
            Removed.Count > 0 ? $"{Removed.Count:N0} come from mods you removed" : null,
        }.OfType<string>());
    }

    public static FixCheck? Check(string fixPath, ScanResult r)
    {
        try
        {
            using var a = new RdarArchive(fixPath);
            // The scan leaves the fix out, so these are the files the game would load without it.
            var winners = new Dictionary<ulong, (ModReport Mod, AssetInfo Asset)>();
            foreach (var m in r.Mods.Where(m => m.Error == null))
                foreach (var x in m.Assets.Where(x => x.Loads)) winners.TryAdd(x.Hash, (m, x));
            Dictionary<ulong, (string Sha1, string Name)>? manifest = null;
            if (a.Entries.FirstOrDefault(e => e.Hash == ManifestHash) is { } me && a.ReadSegment(a.Segments[(int)me.SegmentsStart]) is { } bytes)
            {
                using var doc = JsonDocument.Parse(bytes);
                manifest = doc.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
                    f => Convert.ToUInt64(f.GetProperty("hash").GetString(), 16),
                    f => (f.GetProperty("sha1").GetString() ?? "", f.GetProperty("name").GetString() ?? ""));
            }
            var changed = new List<string>();
            var removed = new List<string>();
            int textures = 0;
            foreach (var e in a.Entries.Where(e => e.Hash != ManifestHash))
            {
                textures++;
                (string Sha1, string Name) made = manifest != null && manifest.TryGetValue(e.Hash, out var v) ? v : ("", "");
                string name = made.Name.Length > 0 ? made.Name : winners.TryGetValue(e.Hash, out var known) ? known.Asset.DisplayName : $"0x{e.Hash:X16}";
                if (!winners.TryGetValue(e.Hash, out var now)) removed.Add(name);
                else if (made.Sha1.Length > 0 && !made.Sha1.Equals(Convert.ToHexString(now.Asset.Sha1), StringComparison.OrdinalIgnoreCase))
                    changed.Add($"{name} ({now.Mod.Name})");
            }
            return new FixCheck(textures, changed, removed, manifest != null);
        }
        catch { return null; }
    }

    static WriteEntry ManifestEntry(IEnumerable<(ulong Hash, string Sha1, string Name, string Archive, int Halvings)> files)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("tool", Report.Version);
            w.WriteString("created", DateTime.UtcNow.ToString("o"));
            w.WriteStartArray("files");
            foreach (var f in files.OrderBy(f => f.Hash))
            {
                w.WriteStartObject();
                w.WriteString("hash", $"{f.Hash:X16}"); w.WriteString("sha1", f.Sha1); w.WriteString("name", f.Name);
                w.WriteString("archive", f.Archive); w.WriteNumber("halvings", f.Halvings);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var bytes = ms.ToArray();
        return new WriteEntry(ManifestHash, DateTime.UtcNow.ToFileTimeUtc(), 0, new[] { (bytes, (uint)bytes.Length) }, Array.Empty<ulong>(), SHA1.HashData(bytes));
    }

    public static string? FindExisting(string modRoot, IEnumerable<string>? extraDirs = null)
    {
        foreach (var dir in (extraDirs ?? Enumerable.Empty<string>()).Prepend(System.IO.Path.Combine(modRoot, "archive", "pc", "mod")))
            if (Directory.Exists(dir) && Directory.GetFiles(dir, $"*{Scanner.BudgetArchiveMarker}*.archive").FirstOrDefault() is { } f) return f;
        return null;
    }

    /// <summary>
    /// Every folder outside the game's archive\pc\mod where a fix may be: the last saved one, the
    /// "VRAM RipperDoc Budget Fix" mod and the Overwrite folder (v0.2.4 and older) of each MO2 instance we
    /// know, and the Documents fallback. RipperDoc.ini can't be relied on alone: through MO2 it is written
    /// to Overwrite, so an Undo double-clicked outside MO2 never sees it.
    /// </summary>
    public static List<string> KnownFixDirs(string? savedFixDir, IEnumerable<string?> mo2Instances, string toolDir)
    {
        var dirs = new List<string>();
        void Add(string? d) { if (d != null && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d); }
        static string FixMod(string modsDir) => System.IO.Path.Combine(modsDir, Program.Mo2FixModName, "archive", "pc", "mod");
        Add(savedFixDir);
        foreach (var inst in mo2Instances.Append(ModManagers.Mo2InstanceAround(toolDir)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ModManagers.Mo2ModsDir(inst) is { } mods) Add(FixMod(mods));
            if (ModManagers.Mo2OverwriteDir(inst) is { } overwrite) Add(System.IO.Path.Combine(overwrite, "archive", "pc", "mod"));
        }
        // Installed as an MO2 mod (<mods>\<this mod>\tools\VRAM RipperDoc): the fix mod is a sibling of ours.
        if (new DirectoryInfo(toolDir).Parent?.Parent?.Parent is { } siblingMods) Add(FixMod(siblingMods.FullName));
        Add(FixMod(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VRAM RipperDoc")));
        return dirs;
    }

    /// <summary>Rough size of the fix file: halved texture data, scaled by how well each texture compressed in its mod.</summary>
    public static long EstimateBytes(IEnumerable<PlanStep> steps) => steps.Sum(s =>
        (long)((s.Asset.GpuBytes >> (2 * s.Halvings)) * Math.Clamp(s.Asset.GpuBytes > 0 ? (double)s.Asset.DiskBytes / s.Asset.GpuBytes : 1, 0.05, 1.0)));

    /// <summary>Live state of a build, read by the UI thread (progress, watchdog) without blocking workers.</summary>
    public sealed class BuildStatus
    {
        public int Done;
        public int Total;
        public volatile string Phase = "shrinking";
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> InFlight = new();
    }

    const string StagingPattern = "RipperDoc-*.ripperdoc-tmp";

    /// <summary>
    /// Writes the plan. Textures are shrunk in parallel (Oodle (de)compression is the slow part);
    /// reads from each source archive are serialized, data streams straight to disk, and the amount
    /// of texture data held in memory at once is capped so 8K textures can't exhaust RAM.
    /// Workers never touch the console: progress is read from <paramref name="status"/> by the caller.
    /// </summary>
    public static Outcome Build(ScanResult r, BudgetPlan plan, string modRoot, Action<string> log,
        BuildStatus? status = null, int threads = 0, Action<string>? trace = null, string? targetDir = null, IEnumerable<string>? otherFixDirs = null)
    {
        if (!Oodle.Available) throw new InvalidOperationException("Oodle (bin/x64/oo2ext_7_win64.dll) is required to write archives.");
        // targetDir: where the archive goes (MO2 users get a real MO2 mod folder instead of the virtual game folder).
        var dir = targetDir ?? System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        Directory.CreateDirectory(dir);
        // Only one fix may exist: drop older ones wherever they are (game folder, MO2 mod, Overwrite).
        Remove(modRoot, quiet: true, extraDirs: (otherFixDirs ?? Enumerable.Empty<string>()).Append(dir));
        status ??= new BuildStatus();
        status.Total = plan.Steps.Count;
        if (threads <= 0) threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        trace?.Invoke($"build: {plan.Steps.Count} textures, {threads} thread(s), output {dir}");

        var sources = new Dictionary<string, (RdarArchive A, Dictionary<ulong, ArchiveEntry> ByHash)>(StringComparer.OrdinalIgnoreCase);
        var skipped = new System.Collections.Concurrent.ConcurrentBag<string>();
        var made = new System.Collections.Concurrent.ConcurrentBag<(ulong, string, string, string, int)>();
        long saved = 0;
        // Cap on uncompressed texture bytes being processed at once (one 8K BC7 texture alone is ~90 MB).
        const long memoryCap = 1536L << 20;
        long inFlightBytes = 0;
        var memGate = new object();
        // Stage next to the destination: the final step is then a same-folder rename, never a cross-drive copy.
        using var writer = new ArchiveWriter(dir);
        try
        {
            foreach (var file in plan.Steps.Select(s => s.Mod.File).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { var a = new RdarArchive(file); sources[file] = (a, a.Entries.ToDictionary(e => e.Hash)); }
                catch (Exception ex) { skipped.Add($"{System.IO.Path.GetFileName(file)} ({ex.Message})"); trace?.Invoke($"cannot open {file}: {ex}"); }
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
            Parallel.ForEach(plan.Steps.OrderBy(s => s.Mod.File, StringComparer.OrdinalIgnoreCase), options, step =>
            {
                string label = $"{step.Mod.Name}: {step.Asset.DisplayName}";
                long need = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (!sources.TryGetValue(step.Mod.File, out var src) || !src.ByHash.TryGetValue(step.Asset.Hash, out var e)) { skipped.Add($"{label} (not found)"); trace?.Invoke($"skip {label}: not found"); return; }
                    var segs = src.A.SegmentsOf(e).ToList();
                    if (segs.Count != 2) { skipped.Add($"{label} (unusual layout)"); trace?.Invoke($"skip {label}: unusual layout ({segs.Count} segments)"); return; }

                    need = segs[0].Size + 2L * segs[1].Size + segs[1].ZSize;
                    lock (memGate)
                    {
                        while (inFlightBytes > 0 && inFlightBytes + need > memoryCap) Monitor.Wait(memGate);
                        inFlightBytes += need;
                    }
                    status.InFlight[label] = DateTime.UtcNow;
                    trace?.Invoke($"start {label} ({segs[1].Size >> 20} MB)");

                    byte[] rawHead, rawBuf;
                    lock (src.A) { rawHead = src.A.ReadRaw(segs[0]); rawBuf = src.A.ReadRaw(segs[1]); }
                    var head = segs[0].ZSize == segs[0].Size ? rawHead : Oodle.DecompressKark(rawHead, (int)segs[0].Size);
                    var buf = segs[1].ZSize == segs[1].Size ? rawBuf : Oodle.DecompressKark(rawBuf, (int)segs[1].Size);
                    if (head == null || buf == null) { skipped.Add($"{label} (unreadable)"); trace?.Invoke($"skip {label}: unreadable"); return; }
                    var res = TextureShrinker.Shrink(head, buf, step.Halvings, out var why);
                    if (res == null) { skipped.Add($"{label} ({why})"); trace?.Invoke($"skip {label}: {why}"); return; }
                    var deps = Enumerable.Range((int)e.DepsStart, (int)(e.DepsEnd - e.DepsStart)).Select(i => src.A.Dependencies[i]).ToList();
                    writer.Add(new WriteEntry(e.Hash, e.Timestamp, e.NumInlineBufferSegments,
                        new[] { (res.HeaderRaw, res.HeaderSize), (res.BufferRaw, res.BufferSize) }, deps, res.Sha1));
                    made.Add((e.Hash, Convert.ToHexString(e.Sha1), step.Asset.DisplayName, System.IO.Path.GetFileName(step.Mod.File), step.Halvings));
                    Interlocked.Add(ref saved, buf.Length - res.BufferSize);
                    trace?.Invoke($"done  {label} in {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex) { skipped.Add($"{label} ({ex.Message})"); trace?.Invoke($"error {label}: {ex}"); }
                finally
                {
                    status.InFlight.TryRemove(label, out _);
                    if (need > 0) lock (memGate) { inFlightBytes -= need; Monitor.PulseAll(memGate); }
                    Interlocked.Increment(ref status.Done);
                }
            });
        }
        finally
        {
            trace?.Invoke("all textures processed; closing source archives");
            foreach (var s in sources.Values) s.A.Dispose();
        }

        // Load before every other archive so our smaller copies win, without renaming anyone's files.
        var others = r.Mods.Where(m => !m.Source.StartsWith("REDmod")).Select(m => System.IO.Path.GetFileName(m.File));
        var name = NameThatLoadsFirst(others);
        trace?.Invoke($"file name: {name}");
        var path = System.IO.Path.Combine(dir, name);
        if (writer.Count == 0) { log("Nothing to write: no plan steps could be applied."); return new Outcome(path, 0, 0, skipped.ToList()); }
        int count = writer.Count;
        writer.Add(ManifestEntry(made));
        status.Phase = "saving";
        trace?.Invoke($"saving {path}");
        writer.Write(path);
        trace?.Invoke("saved");

        var modlist = System.IO.Path.Combine(dir, "modlist.txt");
        if (File.Exists(modlist))
        {
            var bak = modlist + ".ripperdoc.bak";
            if (!File.Exists(bak)) File.Copy(modlist, bak);
            var lines = File.ReadAllLines(modlist).Where(l => !l.Contains(Scanner.BudgetArchiveMarker)).ToList();
            lines.Insert(0, name);
            File.WriteAllLines(modlist, lines);
            log($"Added {name} to the top of modlist.txt (backup: modlist.txt.ripperdoc.bak).");
        }
        return new Outcome(path, count, saved, skipped.ToList());
    }

    /// <summary>
    /// A file name that sorts (ordinal, like the game's load order) before every name in <paramref name="others"/>.
    /// Always terminates: copy the smallest name's leading characters up to its first one above '!',
    /// then put '!' there. (v0.2.3–0.2.5 prepended '!' in a loop, which never ended when a mod's name
    /// had a space or other low character right after its leading '!'s, e.g. "! Hairs.archive".)
    /// </summary>
    public static string NameThatLoadsFirst(IEnumerable<string> others)
    {
        string baseName = $"!{Scanner.BudgetArchiveMarker}.archive";
        string? min = null;
        foreach (var o in others)
            if (min == null || string.CompareOrdinal(o, min) < 0) min = o;
        if (min == null || string.CompareOrdinal(baseName, min) < 0) return baseName;
        int i = 0;
        while (i < min.Length && min[i] <= '!') i++;
        if (i < min.Length) return min[..i] + baseName; // '!' at position i is lower than min[i]
        // min is made only of characters <= '!' (not a real archive name); fall back to the lowest legal start.
        return " " + baseName;
    }

    /// <summary>
    /// Deletes the budget archive from the game's mod folder and from <paramref name="extraDirs"/>
    /// (see <see cref="KnownFixDirs"/>), plus leftovers of interrupted runs. Returns the files removed.
    /// </summary>
    public static List<string> Remove(string modRoot, bool quiet = false, IEnumerable<string>? extraDirs = null)
    {
        var gameDir = System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        var removed = new List<string>();
        foreach (var dir in (extraDirs ?? Enumerable.Empty<string>()).Prepend(gameDir).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var f in Directory.GetFiles(dir, $"*{Scanner.BudgetArchiveMarker}*.archive"))
            {
                File.Delete(f);
                removed.Add(f);
                if (!quiet) Console.WriteLine($"Removed {f}");
            }
            // Leftovers from an interrupted run.
            foreach (var f in Directory.GetFiles(dir, StagingPattern).Concat(Directory.GetFiles(dir, "RipperDoc-*.part"))) try { File.Delete(f); } catch { }
        }
        try { foreach (var f in Directory.GetFiles(System.IO.Path.GetTempPath(), StagingPattern)) File.Delete(f); } catch { }
        var modlist = System.IO.Path.Combine(gameDir, "modlist.txt");
        if (File.Exists(modlist))
        {
            var lines = File.ReadAllLines(modlist);
            var kept = lines.Where(l => !l.Contains(Scanner.BudgetArchiveMarker)).ToArray();
            if (kept.Length != lines.Length) File.WriteAllLines(modlist, kept);
        }
        return removed;
    }
}
