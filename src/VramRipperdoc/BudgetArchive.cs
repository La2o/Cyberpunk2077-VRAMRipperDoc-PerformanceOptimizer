namespace VramRipperdoc;

/// <summary>
/// Stage 2: writes the budget plan as ONE extra archive that loads before the mods it slims down.
/// Original archives are never modified; deleting the budget archive restores everything.
/// </summary>
public static class BudgetArchive
{
    public sealed record Outcome(string Path, int Textures, long SavedBytes, List<string> Skipped);

    public static string? FindExisting(string modRoot)
    {
        var dir = System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, $"*{Scanner.BudgetArchiveMarker}*.archive").FirstOrDefault() : null;
    }

    /// <summary>Rough size of the fix file: halved texture data, scaled by how well each texture compressed in its mod.</summary>
    public static long EstimateBytes(IEnumerable<PlanStep> steps) => steps.Sum(s =>
        (long)((s.Asset.GpuBytes >> (2 * s.Halvings)) * Math.Clamp(s.Asset.GpuBytes > 0 ? (double)s.Asset.DiskBytes / s.Asset.GpuBytes : 1, 0.05, 1.0)));

    /// <summary>
    /// Writes the plan. Textures are shrunk in parallel (Oodle (de)compression is the slow part);
    /// reads from each source archive are serialized, and data streams straight to disk.
    /// </summary>
    public static Outcome Build(ScanResult r, BudgetPlan plan, string modRoot, Action<string> log, Action<int, int>? progress = null)
    {
        if (!Oodle.Available) throw new InvalidOperationException("Oodle (bin/x64/oo2ext_7_win64.dll) is required to write archives.");
        var dir = System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        Directory.CreateDirectory(dir);
        Remove(modRoot, quiet: true);

        var sources = new Dictionary<string, (RdarArchive A, Dictionary<ulong, ArchiveEntry> ByHash)>(StringComparer.OrdinalIgnoreCase);
        var skipped = new System.Collections.Concurrent.ConcurrentBag<string>();
        long saved = 0;
        int done = 0, total = plan.Steps.Count;
        using var writer = new ArchiveWriter(dir);
        try
        {
            foreach (var file in plan.Steps.Select(s => s.Mod.File).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { var a = new RdarArchive(file); sources[file] = (a, a.Entries.ToDictionary(e => e.Hash)); }
                catch (Exception ex) { skipped.Add($"{System.IO.Path.GetFileName(file)} ({ex.Message})"); }
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 8) };
            Parallel.ForEach(plan.Steps.OrderBy(s => s.Mod.File, StringComparer.OrdinalIgnoreCase), options, step =>
            {
                string label = $"{step.Mod.Name}: {step.Asset.DisplayName}";
                try
                {
                    if (!sources.TryGetValue(step.Mod.File, out var src) || !src.ByHash.TryGetValue(step.Asset.Hash, out var e)) { skipped.Add($"{label} (not found)"); return; }
                    var segs = src.A.SegmentsOf(e).ToList();
                    if (segs.Count != 2) { skipped.Add($"{label} (unusual layout)"); return; }
                    byte[] rawHead, rawBuf;
                    lock (src.A) { rawHead = src.A.ReadRaw(segs[0]); rawBuf = src.A.ReadRaw(segs[1]); }
                    var head = segs[0].ZSize == segs[0].Size ? rawHead : Oodle.DecompressKark(rawHead, (int)segs[0].Size);
                    var buf = segs[1].ZSize == segs[1].Size ? rawBuf : Oodle.DecompressKark(rawBuf, (int)segs[1].Size);
                    if (head == null || buf == null) { skipped.Add($"{label} (unreadable)"); return; }
                    var res = TextureShrinker.Shrink(head, buf, step.Halvings, out var why);
                    if (res == null) { skipped.Add($"{label} ({why})"); return; }
                    var deps = Enumerable.Range((int)e.DepsStart, (int)(e.DepsEnd - e.DepsStart)).Select(i => src.A.Dependencies[i]).ToList();
                    writer.Add(new WriteEntry(e.Hash, e.Timestamp, e.NumInlineBufferSegments,
                        new[] { (res.HeaderRaw, res.HeaderSize), (res.BufferRaw, res.BufferSize) }, deps, res.Sha1));
                    Interlocked.Add(ref saved, buf.Length - res.BufferSize);
                }
                catch (Exception ex) { skipped.Add($"{label} ({ex.Message})"); }
                finally { progress?.Invoke(Interlocked.Increment(ref done), total); }
            });
        }
        finally
        {
            foreach (var s in sources.Values) s.A.Dispose();
        }

        // Load before every other archive so our smaller copies win, without renaming anyone's files.
        var others = r.Mods.Where(m => !m.Source.StartsWith("REDmod")).Select(m => System.IO.Path.GetFileName(m.File)).ToList();
        var name = $"!{Scanner.BudgetArchiveMarker}.archive";
        while (others.Any(o => string.CompareOrdinal(o, name) <= 0)) name = "!" + name;
        var path = System.IO.Path.Combine(dir, name);
        if (writer.Count == 0) { log("Nothing to write: no plan steps could be applied."); return new Outcome(path, 0, 0, skipped.ToList()); }
        int count = writer.Count;
        writer.Write(path);

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

    public static bool Remove(string modRoot, bool quiet = false)
    {
        var dir = System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        if (!Directory.Exists(dir)) return false;
        bool any = false;
        foreach (var f in Directory.GetFiles(dir, $"*{Scanner.BudgetArchiveMarker}*.archive"))
        {
            File.Delete(f);
            any = true;
            if (!quiet) Console.WriteLine($"Removed {f}");
        }
        // Leftovers from an interrupted run.
        foreach (var f in Directory.GetFiles(dir, "RipperDoc-*.part")) try { File.Delete(f); } catch { }
        var modlist = System.IO.Path.Combine(dir, "modlist.txt");
        if (File.Exists(modlist))
        {
            var lines = File.ReadAllLines(modlist);
            var kept = lines.Where(l => !l.Contains(Scanner.BudgetArchiveMarker)).ToArray();
            if (kept.Length != lines.Length) File.WriteAllLines(modlist, kept);
        }
        return any;
    }
}
