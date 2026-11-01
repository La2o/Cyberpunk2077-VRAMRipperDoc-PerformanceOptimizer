using Microsoft.Win32;

namespace VramRipperdoc;

public sealed record GpuInfo(string Name, long VramBytes)
{
    /// <summary>Reads dedicated VRAM from the display-adapter registry keys (WMI caps this at 4 GB).</summary>
    public static GpuInfo? Detect()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            GpuInfo? best = null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k == null) continue;
                long mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
                    int i => (uint)i, _ => 0,
                };
                var name = k.GetValue("DriverDesc") as string ?? "GPU";
                if (mem > (best?.VramBytes ?? 0)) best = new GpuInfo(name, mem);
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>
    /// Rule-of-thumb VRAM headroom for mods, after the vanilla game (~8-9.5 GB at high settings with
    /// RT off, per Environment Textures Overhaul's measurements). Players should verify in-game.
    /// </summary>
    public static long SuggestedModBudget(long vram)
    {
        double gb = vram / (double)(1L << 30);
        double budget = gb switch { < 7 => 0.25, < 9 => 0.5, < 11 => 1.0, < 13 => 2.0, < 17 => 4.0, < 21 => 7.0, _ => 12.0 };
        return (long)(budget * (1L << 30));
    }
}

public sealed record PlanStep(ModReport Mod, AssetInfo Asset, int Halvings, long Saves);

public sealed class BudgetPlan
{
    public long Budget;
    public long Current;
    public long AfterPlan;
    public List<PlanStep> Steps = new();
    public List<(ModReport Mod, long Saves, int Count)> PerMod = new();
    public bool Reachable => AfterPlan <= Budget;

    /// <summary>
    /// Greedy plan: repeatedly halve the resolution of whichever texture saves the most VRAM
    /// (each halving removes ~75% of it), never below 1024 px. Textures in mods are only
    /// suggested here; originals are never touched.
    /// </summary>
    public static BudgetPlan Build(ScanResult r, long budget)
    {
        var plan = new BudgetPlan { Budget = budget, Current = r.TotalDelta };
        long current = plan.Current;
        var candidates = r.Mods.SelectMany(m => m.Assets.Where(a => a.Kind == AssetKind.Texture && a.ShadowedBy == null && a.MaxDim >= 2048 && a.DeltaGpu > 0)
            .Select(a => (Mod: m, Asset: a))).ToList();
        var state = candidates.ToDictionary(c => c.Asset, c => (c.Mod, Halvings: 0, Gpu: c.Asset.GpuBytes));
        var pq = new PriorityQueue<AssetInfo, long>();
        foreach (var c in candidates) pq.Enqueue(c.Asset, -(c.Asset.GpuBytes * 3 / 4));

        while (current > budget && pq.TryDequeue(out var asset, out _))
        {
            var s = state[asset];
            long save = s.Gpu * 3 / 4;
            // Don't count savings below what vanilla already costs for an override.
            if (asset.OverridesBase) save = Math.Min(save, Math.Max(0, s.Gpu - asset.BaseGpuBytes));
            if (save <= 0) continue;
            current -= save;
            s = (s.Mod, s.Halvings + 1, s.Gpu - save);
            state[asset] = s;
            if ((asset.MaxDim >> s.Halvings) >= 2048) pq.Enqueue(asset, -(s.Gpu * 3 / 4));
        }
        foreach (var (asset, s) in state.Where(kv => kv.Value.Halvings > 0))
            plan.Steps.Add(new PlanStep(s.Mod, asset, s.Halvings, asset.GpuBytes - s.Gpu));
        plan.Steps = plan.Steps.OrderByDescending(p => p.Saves).ToList();
        plan.PerMod = plan.Steps.GroupBy(p => p.Mod).Select(g => (g.Key, g.Sum(x => x.Saves), g.Count())).OrderByDescending(x => x.Item2).ToList();
        plan.AfterPlan = current;
        return plan;
    }
}
