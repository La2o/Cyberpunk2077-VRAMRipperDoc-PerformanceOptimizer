using System.Text;
using System.Text.Json;

namespace VramRipperdoc;

/// <summary>Scan history: compares this scan with the previous one ("what changed since last time").</summary>
public static class History
{
    public sealed record Entry(string Name, long Delta, int Textures, int Meshes, long Tris, int Flagged);

    public static List<string> SaveAndDiff(ScanResult r, string outDir)
    {
        var dir = Path.Combine(outDir, "history");
        Directory.CreateDirectory(dir);
        var previous = Directory.GetFiles(dir, "scan-*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        var current = r.Mods.Where(m => m.Error == null).Select(m => new Entry(m.Name, m.GpuDelta, m.Textures, m.Meshes, m.Tris0, m.FlagCount)).ToList();

        using (var fs = File.Create(Path.Combine(dir, $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.json")))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject(); w.WriteNumber("total", r.TotalDelta); w.WriteStartArray("mods");
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
        sb.AppendLine($"Budget: {Report.Size(plan.Budget)} → {(plan.Current <= plan.Budget ? "within budget" : $"over by {Report.Size(plan.Current - plan.Budget)}")}");
        if (r.BudgetArchive != null) sb.AppendLine($"Budget archive active: `{Path.GetFileName(r.BudgetArchive)}`");
        sb.AppendLine();
        sb.AppendLine("| Mod | Normal play | When used | Tex | Mesh | Flags |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var m in r.Mods.Where(m => m.Error == null).OrderByDescending(m => m.AlwaysDelta).ThenByDescending(m => m.GpuDelta).Take(10))
            sb.AppendLine($"| {m.Package ?? m.Name} | {Report.Size(m.AlwaysDelta)} | {Report.Size(m.WhenUsedDelta)} | {m.Textures} | {m.Meshes} | {m.FlagCount} |");
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

    public static void WriteShare(ScanResult r, string path)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteNumber("version", 1);
        w.WriteString("tool", Report.Version);
        w.WriteStartArray("mods");
        foreach (var m in r.Mods.Where(m => m.Error == null && m.Assets.Count > 0))
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
        w.WriteEndArray();
        w.WriteEndObject();
    }

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
