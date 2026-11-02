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

    /// <summary>
    /// Settings-aware budget: VRAM minus ~0.6 GB for Windows/overlays minus the estimated vanilla need,
    /// never below 0.25 GB. Falls back to <see cref="SuggestedModBudget"/> when settings are unknown.
    /// </summary>
    public static (long Budget, string Why) SuggestBudget(long vram, GameSettings? s)
    {
        if (s == null || s.Width == 0)
            return (SuggestedModBudget(vram), "rule of thumb for your VRAM (game settings not found)");
        long vanilla = s.EstimatedVanillaBytes();
        long budget = Math.Max(256L << 20, vram - (600L << 20) - vanilla);
        return (budget, $"{Report.Size(vram)} VRAM - ~{Report.Size(vanilla)} vanilla at {s.Describe()} - 0.6 GB for Windows");
    }
}

public sealed record PlanStep(ModReport Mod, AssetInfo Asset, int Halvings, long Saves);

public sealed class BudgetPlan
{
    public long Budget;
    /// <summary>VRAM mods add during normal play (replaced game files and what they pull in).</summary>
    public long Current;
    /// <summary>VRAM of new items (clothing, cars, props) that only load when used.</summary>
    public long WhenUsed;
    public long AfterPlan;
    public bool IncludeNew;
    public long NewItemSaves;
    public List<PlanStep> Steps = new();
    public List<(ModReport Mod, long Saves, int Count)> PerMod = new();
    public bool Reachable => AfterPlan <= Budget;

    /// <summary>
    /// Greedy plan over always-loaded textures: repeatedly halve whichever saves the most VRAM
    /// (each halving removes ~75% of it), never below 1024 px, until the budget fits.
    /// With <paramref name="includeNew"/>, new items' 4K+ textures are also halved once (to 2K):
    /// they only cost VRAM while used, so they never count against the budget itself.
    /// Original archives are never touched; the plan is applied as a separate archive.
    /// </summary>
    public static BudgetPlan Build(ScanResult r, long budget, bool includeNew = false)
    {
        var plan = new BudgetPlan { Budget = budget, Current = r.AlwaysDelta, WhenUsed = r.WhenUsedDelta, IncludeNew = includeNew };
        long current = plan.Current;
        var candidates = r.Mods.SelectMany(m => m.Assets.Where(a => a.Kind == AssetKind.Texture && a.Loads && a.AlwaysLoaded && a.MaxDim >= 2048 && a.DeltaGpu > 0)
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

        if (includeNew)
            foreach (var m in r.Mods)
                foreach (var a in m.Assets.Where(a => a.Kind == AssetKind.Texture && a.Loads && !a.AlwaysLoaded && a.MaxDim >= 4096))
                {
                    long save = a.GpuBytes * 3 / 4;
                    plan.Steps.Add(new PlanStep(m, a, 1, save));
                    plan.NewItemSaves += save;
                }

        plan.Steps = plan.Steps.OrderByDescending(p => p.Saves).ToList();
        plan.PerMod = plan.Steps.GroupBy(p => p.Mod).Select(g => (g.Key, g.Sum(x => x.Saves), g.Count())).OrderByDescending(x => x.Item2).ToList();
        plan.AfterPlan = current;
        return plan;
    }
}
