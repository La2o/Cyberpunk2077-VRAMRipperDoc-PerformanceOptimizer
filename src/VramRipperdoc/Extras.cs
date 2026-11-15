using System.Text;
using System.Text.Json;

namespace VramRipperdoc;

/// <summary>Scan history: compares this scan with the previous one ("what changed since last time").</summary>
public static class History
{
    public sealed record Entry(string Name, long Delta, int Textures, int Meshes, long Tris, int Flagged);

    /// <summary>How VRAM is counted; scans made under another rule aren't compared. 2: only GPU data (v0.4.0).</summary>
    const int Counting = 2;

    public static List<string> SaveAndDiff(ScanResult r, string outDir)
    {
        var dir = Path.Combine(outDir, "history");
        Directory.CreateDirectory(dir);
        var previous = Directory.GetFiles(dir, "scan-*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        var current = r.Mods.Where(m => m.Error == null).Select(m => new Entry(m.Name, m.GpuDelta, m.Textures, m.Meshes, m.Tris0, m.FlagCount)).ToList();

        using (var fs = File.Create(Path.Combine(dir, $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.json")))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject(); w.WriteNumber("counting", Counting); w.WriteNumber("total", r.TotalDelta); w.WriteStartArray("mods");
            foreach (var e in current)
            {
                w.WriteStartObject(); w.WriteString("name", e.Name); w.WriteNumber("delta", e.Delta); w.WriteNumber("textures", e.Textures);
                w.WriteNumber("meshes", e.Meshes); w.WriteNumber("tris", e.Tris); w.WriteNumber("flagged", e.Flagged); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        foreach (var old in Directory.GetFiles(dir, "scan-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(20)) File.Delete(old);
        if (previous == null) return new();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(previous));
            if (!doc.RootElement.TryGetProperty("counting", out var c) || c.GetInt32() != Counting)
                return new() { "Not compared with your last scan: since v0.4.0 only data that goes to the graphics card counts as VRAM (animations and other data are listed apart), so the numbers changed." };
            long prevTotal = doc.RootElement.GetProperty("total").GetInt64();
            var prev = doc.RootElement.GetProperty("mods").EnumerateArray()
                .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("delta").GetInt64());
            var lines = new List<string>();
            var when = File.GetLastWriteTime(previous);
            lines.Add($"Since last scan ({when:yyyy-MM-dd HH:mm}): total {Report.Size(prevTotal)} -> {Report.Size(r.TotalDelta)} ({Sign(r.TotalDelta - prevTotal)})");
            foreach (var e in current.Where(e => !prev.ContainsKey(e.Name)).OrderByDescending(e => e.Delta))
                lines.Add($"  + {e.Name}: {Report.Size(e.Delta)}");
            foreach (var name in prev.Keys.Where(n => current.All(c => c.Name != n)))
                lines.Add($"  - {name}: was {Report.Size(prev[name])}");
            foreach (var e in current.Where(e => prev.TryGetValue(e.Name, out var d) && Math.Abs(d - e.Delta) >= 1 << 20))
                lines.Add($"  ~ {e.Name}: {Report.Size(prev[e.Name])} -> {Report.Size(e.Delta)}");
            return lines;
        }
        catch { return new(); }
    }

    static string Sign(long d) => (d >= 0 ? "+" : "") + Report.Size(d);
}

/// <summary>Short Markdown summary to paste into a Nexus/Reddit/Discord support thread.</summary>
public static class Summary
{
    public static string Markdown(ScanResult r, BudgetPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**VRAM RipperDoc v{Report.Version}** · {r.Mods.Count} archive mods · **{Report.Size(r.AlwaysDelta)}** extra in normal play · {Report.Size(r.WhenUsedDelta)} in new items (only when used)");
        if (r.Gpu != null) sb.AppendLine($"GPU: {r.Gpu.Name} ({Report.Size(r.Gpu.VramBytes)})" + (r.Settings != null ? $" · {r.Settings.Describe()}" : ""));
        sb.AppendLine($"Room for mods on top of the game: {Report.Size(plan.Budget)} ({r.BudgetWhy}) → {(plan.Current <= plan.Budget ? "fits" : $"{Report.Size(plan.Current - plan.Budget)} too much")}");
        if (r.BudgetArchive != null) sb.AppendLine($"Budget archive active: `{Path.GetFileName(r.BudgetArchive)}`" + (r.FixCheck is { Stale: true } f ? $" (out of date: {f.Describe()})" : ""));
        sb.AppendLine();
        sb.AppendLine("| Mod | Normal play | When used | Tex | Mesh | Flags |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        // One row per installed mod: a mod with several archives would otherwise show up several times under the same name.
        var rows = r.Mods.Where(m => m.Error == null).GroupBy(m => m.Package ?? m.Name)
            .Select(g => (Name: g.Count() > 1 ? $"{g.Key} ({g.Count()} archives)" : g.Key, Always: g.Sum(m => m.AlwaysDelta), WhenUsed: g.Sum(m => m.WhenUsedDelta),
                Total: g.Sum(m => m.GpuDelta), Tex: g.Sum(m => m.Textures), Mesh: g.Sum(m => m.Meshes), Flags: g.Sum(m => m.FlagCount)));
        foreach (var m in rows.OrderByDescending(m => m.Always).ThenByDescending(m => m.Total).Take(10))
            sb.AppendLine($"| {m.Name} | {Report.Size(m.Always)} | {Report.Size(m.WhenUsed)} | {m.Tex} | {m.Mesh} | {m.Flags} |");
        var broken = r.Mods.Where(m => m.Error != null).ToList();
        if (broken.Count > 0) sb.AppendLine().AppendLine("**Broken archives:** " + string.Join(", ", broken.Select(b => $"`{b.Name}`")));
        var missing = r.Mods.Where(m => m.Missing > 0).ToList();
        if (missing.Count > 0) sb.AppendLine().AppendLine("**Missing references (requirement not installed?):** " + string.Join(", ", missing.Select(m => $"`{m.Name}` ({m.Missing})")));
        return sb.ToString();
    }
}

/// <summary>
/// Opt-in community costs: --share writes an anonymous file (no paths, no user names) that can be
/// submitted on GitHub; --community reads the collected list to show other players' numbers.
/// </summary>
public static class Community
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/La2o/Cyberpunk2077-VRAMRipperDoc-PerformanceOptimizer/main/community/costs.json";

    /// <summary>
    /// GitHub rejects an issue or comment longer than 65,536 characters, and the issue form adds its own headings
    /// around the pasted file, so every part stays below this.
    /// </summary>
    public const int MaxPartChars = 60_000;

    /// <summary>
    /// Writes VRAM-RipperDoc-Share.json, or -1-of-N.json, -2-of-N.json ... when one post can't hold every mod.
    /// One mod per line keeps it readable; each part is complete JSON in the same shape. Returns the files written.
    /// </summary>
    public static List<string> WriteShare(ScanResult r, string outDir)
    {
        foreach (var old in Directory.GetFiles(outDir, "VRAM-RipperDoc-Share*.json")) File.Delete(old);  // parts from an earlier, larger scan

        // Unescaped so non-English archive names stay readable (and short) instead of becoming \uXXXX.
        var options = new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var lines = new List<string>();
        foreach (var m in r.Mods.Where(m => m.Error == null && m.Assets.Count > 0))
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, options))
            {
                w.WriteStartObject();
                w.WriteString("archive", Path.GetFileName(m.File));
                w.WriteNumber("disk", m.DiskSize);
                if (m.NexusId != null) w.WriteNumber("nexusId", m.NexusId.Value);
                if (m.Package != null) w.WriteString("package", m.Package);
                w.WriteNumber("delta", m.GpuDelta); w.WriteNumber("textures", m.Textures); w.WriteNumber("meshes", m.Meshes);
                w.WriteNumber("tris", m.Tris0); w.WriteNumber("flagged", m.FlagCount);
                w.WriteEndObject();
            }
            lines.Add(Encoding.UTF8.GetString(ms.ToArray()));
        }

        // Room for the header with the longest possible "part" value, the closing "]}" and the line breaks.
        int room = MaxPartChars - Header("99/99").Length - 8;
        var parts = new List<List<string>> { new() };
        int used = 0;
        foreach (var line in lines)
        {
            if (parts[^1].Count > 0 && used + line.Length + 2 > room) { parts.Add(new()); used = 0; }
            parts[^1].Add(line);
            used += line.Length + 2;
        }

        var written = new List<string>();
        for (int i = 0; i < parts.Count; i++)
        {
            var name = parts.Count == 1 ? "VRAM-RipperDoc-Share.json" : $"VRAM-RipperDoc-Share-{i + 1}-of-{parts.Count}.json";
            var text = Header(parts.Count == 1 ? null : $"{i + 1}/{parts.Count}") + "\n" + string.Join(",\n", parts[i]) + "\n]}\n";
            var path = Path.Combine(outDir, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            written.Add(path);
        }
        return written;
    }

    static string Header(string? part) =>
        $"{{\"version\":1,\"tool\":\"{Report.Version}\"," + (part != null ? $"\"part\":\"{part}\"," : "") + "\"mods\":[";

    public sealed record Known(string Archive, long Disk, int? NexusId, long Delta);

    public static List<Known> Load(string source, Action<string> log)
    {
        try
        {
            string json;
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                json = http.GetStringAsync(source).GetAwaiter().GetResult();
            }
            else json = File.ReadAllText(source);
            using var doc = JsonDocument.Parse(json);
            var list = doc.RootElement.GetProperty("mods").EnumerateArray().Select(e => new Known(
                e.GetProperty("archive").GetString() ?? "", e.TryGetProperty("disk", out var d) ? d.GetInt64() : 0,
                e.TryGetProperty("nexusId", out var n) ? n.GetInt32() : null, e.GetProperty("delta").GetInt64())).ToList();
            log($"Community list: {list.Count} report(s).");
            return list;
        }
        catch (Exception ex) { log($"! Community list unavailable: {ex.Message}"); return new(); }
    }

    public static void Annotate(ScanResult r, List<Known> known)
    {
        foreach (var m in r.Mods)
        {
            var name = Path.GetFileName(m.File);
            var hits = known.Where(k => string.Equals(k.Archive, name, StringComparison.OrdinalIgnoreCase) && (k.Disk == m.DiskSize || m.NexusId != null && k.NexusId == m.NexusId)).ToList();
            if (hits.Count == 0) continue;
            var median = hits.Select(h => h.Delta).OrderBy(x => x).ElementAt(hits.Count / 2);
            m.CommunityNote = $"{hits.Count} community report(s), median {Report.Size(median)}";
        }
    }
}
