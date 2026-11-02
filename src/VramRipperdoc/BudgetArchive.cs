namespace VramRipperdoc;

/// <summary>
/// Stage 2: writes the budget plan as ONE extra archive that loads before the mods it slims down.
/// Original archives are never modified; deleting the budget archive restores everything.
/// </summary>
public static class BudgetArchive
{
    public sealed record Outcome(string Path, int Textures, long SavedBytes, List<string> Skipped);

    public static string? FindExisting(string modRoot, string? extraDir = null)
    {
        foreach (var dir in new[] { System.IO.Path.Combine(modRoot, "archive", "pc", "mod"), extraDir })
            if (dir != null && Directory.Exists(dir) && Directory.GetFiles(dir, $"*{Scanner.BudgetArchiveMarker}*.archive").FirstOrDefault() is { } f) return f;
        return null;
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
        BuildStatus? status = null, int threads = 0, Action<string>? trace = null, string? targetDir = null)
    {
        if (!Oodle.Available) throw new InvalidOperationException("Oodle (bin/x64/oo2ext_7_win64.dll) is required to write archives.");
        // targetDir: where the archive goes (MO2 users get a real MO2 mod folder instead of the virtual game folder).
        var dir = targetDir ?? System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        Directory.CreateDirectory(dir);
        Remove(modRoot, quiet: true, extraDir: targetDir);
        status ??= new BuildStatus();
        status.Total = plan.Steps.Count;
        if (threads <= 0) threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        trace?.Invoke($"build: {plan.Steps.Count} textures, {threads} thread(s), output {dir}");

        var sources = new Dictionary<string, (RdarArchive A, Dictionary<ulong, ArchiveEntry> ByHash)>(StringComparer.OrdinalIgnoreCase);
        var skipped = new System.Collections.Concurrent.ConcurrentBag<string>();
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
            foreach (var s in sources.Values) s.A.Dispose();
        }

        // Load before every other archive so our smaller copies win, without renaming anyone's files.
        var others = r.Mods.Where(m => !m.Source.StartsWith("REDmod")).Select(m => System.IO.Path.GetFileName(m.File)).ToList();
        var name = $"!{Scanner.BudgetArchiveMarker}.archive";
        while (others.Any(o => string.CompareOrdinal(o, name) <= 0)) name = "!" + name;
        var path = System.IO.Path.Combine(dir, name);
        if (writer.Count == 0) { log("Nothing to write: no plan steps could be applied."); return new Outcome(path, 0, 0, skipped.ToList()); }
        int count = writer.Count;
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
    /// Deletes the budget archive from the game's mod folder and from <paramref name="extraDir"/>
    /// (where it goes for MO2 users), plus leftovers of interrupted runs.
    /// </summary>
    public static bool Remove(string modRoot, bool quiet = false, string? extraDir = null)
    {
        var gameDir = System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        bool any = false;
        foreach (var dir in new[] { gameDir, extraDir }.Where(d => d != null && Directory.Exists(d)).Cast<string>())
        {
            foreach (var f in Directory.GetFiles(dir, $"*{Scanner.BudgetArchiveMarker}*.archive"))
            {
                File.Delete(f);
                any = true;
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
        return any;
    }
}
