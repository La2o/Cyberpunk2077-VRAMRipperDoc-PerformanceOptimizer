using System.Diagnostics;

namespace VramRipperdoc;

/// <summary>
/// Writing and removing the budget fix, as the command line and the app window both do it: where the file goes,
/// how many threads, the fix log, the time estimate, and the "what happened, what next" sentences.
/// </summary>
public static class Fix
{
    /// <summary>Where the fix is written. Dir null = the game's archive\pc\mod.</summary>
    public sealed record Target(bool ViaMo2, string? Dir, bool Mo2ModFolder);

    /// <summary>
    /// Through MO2, writing into the virtual game folder can hang at the very end (seen in user logs),
    /// so the fix becomes a real MO2 mod folder instead. <paramref name="mo2Output"/> (--mo2-output) forces this (testing).
    /// </summary>
    public static Target PickTarget(string? mo2Output, string? mo2)
    {
        bool viaMo2 = Setup.UnderMo2() || mo2Output != null;
        if (!viaMo2) return new Target(false, null, false);
        var inst = mo2Output ?? mo2 ?? ModManagers.RunningMo2Instance();
        var modsDir = inst != null ? ModManagers.Mo2ModsDir(inst) : null;
        var root = modsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VRAM RipperDoc");
        return new Target(true, Path.Combine(root, Program.Mo2FixModName, "archive", "pc", "mod"), modsDir != null);
    }

    public static int Threads(string? arg, bool viaMo2) =>
        int.TryParse(arg, out var t) && t > 0 ? t
        : viaMo2 ? 2 // MO2's virtual file system is safest with little concurrent file I/O
        : Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    /// <summary>Rough time to write these steps: ~120 MB/s of source texture per worker thread (measured on a mid-range desktop).</summary>
    public static double Seconds(IReadOnlyCollection<PlanStep> steps)
    {
        double srcMb = steps.Sum(s => (double)s.Asset.GpuBytes) / (1 << 20);
        int workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        return srcMb / (120.0 * workers) + steps.Count * 0.01;
    }

    /// <summary>"About 240 MB to write, under a minute": size scales the halved data by each texture's compression ratio.</summary>
    public static string Describe(IReadOnlyCollection<PlanStep> steps)
    {
        long bytes = BudgetArchive.EstimateBytes(steps);
        double seconds = Seconds(steps);
        string time = seconds < 60 ? "under a minute" : $"roughly {Math.Ceiling(seconds / 60)} minute(s)";
        return $"About {Report.Size(bytes)} to write, {time}.";
    }

    /// <summary>Time left, once a few textures are done to go by.</summary>
    public static TimeSpan? Eta(TimeSpan elapsed, int done, int total) =>
        done >= 3 && done < total ? TimeSpan.FromSeconds(elapsed.TotalSeconds / done * (total - done)) : null;

    /// <summary>What to do now that the fix exists. <paramref name="app"/>: said in the app window instead of the console.</summary>
    public static List<string> NextSteps(Target t, string fixPath, bool app)
    {
        string undo = app ? "use Undo in VRAM RipperDoc" : "double-click \"Undo budget fix.bat\"";
        if (t.Mo2ModFolder)
            return new()
            {
                $"MOD ORGANIZER 2: the fix was saved as a new MO2 mod called \"{Program.Mo2FixModName}\".",
                "  1. In MO2, press F5 (or the Refresh button) so it shows up in the left list.",
                "  2. Tick its checkbox to enable it. That's it; start the game from MO2 as usual.",
                $"  To undo: untick it, or {undo}.",
            };
        if (t.ViaMo2)
            return new()
            {
                "MOD ORGANIZER 2: your MO2 mods folder wasn't found, so the fix was saved as a ready-made mod folder:",
                $"  {Platform.Shown(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(fixPath))))!)}",
                "  Copy that folder into MO2's mods folder (or zip it and use MO2's \"Install a new mod from an archive\"), then enable it.",
            };
        return new() { $"Start the game as usual. To undo, {undo}." };
    }

    /// <summary>What Undo did, for <paramref name="removed"/> = the files <see cref="BudgetArchive.Remove"/> deleted.</summary>
    public static List<string> RemovedMessages(List<string> removed) => removed.Count > 0
        ? new List<string> { "The budget fix was removed. Everything is back as it was." }
            .Concat(removed.Any(f => f.Contains(Program.Mo2FixModName, StringComparison.OrdinalIgnoreCase))
                ? new[] { "(In Mod Organizer 2 you can now also delete the empty \"" + Program.Mo2FixModName + "\" mod.)" } : Array.Empty<string>()).ToList()
        : new List<string>
        {
            "No budget fix found, so there was nothing to remove.",
            "(Mod Organizer 2: if a \"" + Program.Mo2FixModName + "\" mod is still in MO2's list, untick or delete it there.)",
        };

    static StreamWriter OpenLog(string path)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); return new StreamWriter(path, append: false) { AutoFlush = true }; }
        catch { return StreamWriter.Null; }
    }

    /// <summary>
    /// One fix being written: its progress (read by the console's progress thread or the app window without blocking
    /// the workers) and VRAM-RipperDoc-Fix.log, which players post when a fix hangs.
    /// </summary>
    public sealed class Writer : IDisposable
    {
        public readonly BudgetArchive.BuildStatus Status = new();
        public readonly Stopwatch Started = Stopwatch.StartNew();
        public readonly string LogPath;
        readonly StreamWriter _log;

        public Writer(string outDir)
        {
            LogPath = Path.Combine(outDir, "VRAM-RipperDoc-Fix.log");
            _log = OpenLog(LogPath);
        }

        public void Trace(string s) { lock (_log) _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

        /// <summary>Writes the fix and remembers an MO2 fix folder in RipperDoc.ini (so Undo finds it).</summary>
        public BudgetArchive.Outcome Build(ScanResult r, BudgetPlan plan, string game, string modRoot, Target t, int threads,
            IEnumerable<string> fixDirs, string settingsFile, Action<string> log)
        {
            Trace($"v{Report.Version} game={game} mo2={t.ViaMo2} threads={threads} steps={plan.Steps.Count}");
            Trace($"output: {t.Dir ?? "game folder"}");
            var outcome = BudgetArchive.Build(r, plan, modRoot, log, Status, threads, Trace, t.Dir, fixDirs);
            if (t.Dir != null) Setup.WriteSetting(settingsFile, "fixdir", t.Dir);
            Trace($"finished: {outcome.Textures} written, {outcome.Skipped.Count} skipped, {Started.Elapsed.TotalSeconds:0}s");
            return outcome;
        }

        public void Dispose() => _log.Dispose();
    }
}
