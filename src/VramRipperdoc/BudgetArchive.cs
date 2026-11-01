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

    public static Outcome Build(ScanResult r, BudgetPlan plan, string modRoot, Action<string> log)
    {
        if (!Oodle.Available) throw new InvalidOperationException("Oodle (bin/x64/oo2ext_7_win64.dll) is required to write archives.");
        var dir = System.IO.Path.Combine(modRoot, "archive", "pc", "mod");
        Directory.CreateDirectory(dir);
        Remove(modRoot, quiet: true);

        var writer = new ArchiveWriter();
        var skipped = new List<string>();
        long saved = 0;
        foreach (var group in plan.Steps.GroupBy(s => s.Mod.File))
        {
            using var a = new RdarArchive(group.Key);
            var byHash = a.Entries.ToDictionary(e => e.Hash);
            foreach (var step in group)
            {
                string label = $"{step.Mod.Name}: {step.Asset.DisplayName}";
                if (!byHash.TryGetValue(step.Asset.Hash, out var e)) { skipped.Add($"{label} (not found)"); continue; }
                var segs = a.SegmentsOf(e).ToList();
                if (segs.Count != 2) { skipped.Add($"{label} (unusual layout)"); continue; }
                var head = a.ReadSegment(segs[0]);
                var buf = a.ReadSegment(segs[1]);
                if (head == null || buf == null) { skipped.Add($"{label} (unreadable)"); continue; }
                TextureShrinker.Result? res;
                string? why;
                try { res = TextureShrinker.Shrink(head, buf, step.Halvings, out why); }
                catch (Exception ex) { res = null; why = ex.Message; }
                if (res == null) { skipped.Add($"{label} ({why})"); continue; }
                var deps = Enumerable.Range((int)e.DepsStart, (int)(e.DepsEnd - e.DepsStart)).Select(i => a.Dependencies[i]).ToList();
                writer.Add(new WriteEntry(e.Hash, e.Timestamp, e.NumInlineBufferSegments,
                    new[] { (res.HeaderRaw, res.HeaderSize), (res.BufferRaw, res.BufferSize) }, deps, res.Sha1));
                saved += buf.Length - res.BufferSize;
            }
        }

        // Load before every other archive so our smaller copies win, without renaming anyone's files.
        var others = r.Mods.Where(m => !m.Source.StartsWith("REDmod")).Select(m => System.IO.Path.GetFileName(m.File)).ToList();
        var name = $"!{Scanner.BudgetArchiveMarker}.archive";
        while (others.Any(o => string.CompareOrdinal(o, name) <= 0)) name = "!" + name;
        var path = System.IO.Path.Combine(dir, name);
        if (writer.Count == 0) { log("Nothing to write: no plan steps could be applied."); return new Outcome(path, 0, 0, skipped); }
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
        return new Outcome(path, writer.Count, saved, skipped);
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
