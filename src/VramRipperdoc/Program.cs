using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VramRipperdoc;

const string Usage = """
VRAM RipperDoc - Performance Optimizer
Ranks your Cyberpunk 2077 asset mods by the VRAM and geometry they add, and plans a budget.
Most players only need the double-click shortcuts next to this exe (see START HERE.txt).

Usage: VramRipperdoc [options]
 Scan (default, read-only)
  --game <dir>              Cyberpunk 2077 folder (auto-detected for Steam/GOG/Epic or when run from inside it; otherwise asked for once)
  --budget <GB>             VRAM your mods may add during normal play (default: from your GPU and in-game settings)
  --out <dir>               Where to write reports (default: next to the exe)
  --mo2 <instance dir>      Read mods from a Mod Organizer 2 instance (auto-detected, or asked for once)
  --hashes <file>           Optional list of depot paths (e.g. WolvenKit's archivehashes.csv) for nicer names
  --top <n>                 Rows to show in the console (default 25)
  --no-vanilla              Skip comparing against the base game archives (faster, less accurate)
  --no-open                 Don't open the HTML report (or Explorer) when done
  --json                    Print the JSON report to stdout instead of the table
 Apply the plan (optional)
  --make-budget-archive     Write ONE extra archive with the planned textures halved (your mods are untouched)
  --include-new-items       Also halve 4K+ textures of new clothing, cars and props (they only cost VRAM while used)
  --threads <n>             Worker threads for writing the fix (default: CPU cores - 1, max 8; 2 under MO2)
  --remove-budget-archive   Delete it again
 Measure real VRAM
  --live [--label <name>] [--seconds <n>]   Record the game's actual VRAM use while you play (Ctrl+C to stop)
 Community (opt-in)
  --share                   Write an anonymous per-mod cost file you can submit on GitHub
  --community [file|url]    Show other players' reported costs (default: the project's GitHub list)
""";

var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "no-vanilla", "no-open", "json", "help", "pause", "make-budget-archive", "include-new-items", "remove-budget-archive", "live", "share", "ask-mo2", "ask-game", "ask", "stress-all", "test-names" };
var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (!a.StartsWith("--")) { opts["game"] = a; continue; }
    var key = a[2..];
    if (flags.Contains(key)) opts[key] = "1";
    else if (key == "community") opts[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : Community.DefaultUrl;
    else opts[key] = i + 1 < args.Length ? args[++i] : null;
}
if (opts.ContainsKey("help")) { Console.WriteLine(Usage); return 0; }

DisableQuickEdit();
// A crash should leave a message, not a silently closed window.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var msg = $"VRAM RipperDoc crashed: {e.ExceptionObject}";
    try { Console.Error.WriteLine(msg); File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "VRAM-RipperDoc-Crash.log"), $"{DateTime.Now}: {msg}{Environment.NewLine}"); } catch { }
};

// Developer self-tests (not part of the user-facing help).
if (opts.TryGetValue("verify", out var verifyArchive) && verifyArchive != null)
    return Verify.Run(verifyArchive, Path.Combine(FindGame(null)!, "bin", "x64", "oo2ext_7_win64.dll"), int.Parse(opts.GetValueOrDefault("samples") ?? "200"));
if (opts.TryGetValue("verify-shrink", out var shrinkArchive) && shrinkArchive != null)
{
    Oodle.TryLoad(new[] { Path.Combine(FindGame(null)!, "bin", "x64", "oo2ext_7_win64.dll") });
    return ShrinkTest.Run(shrinkArchive, int.Parse(opts.GetValueOrDefault("samples") ?? "300"));
}
if (opts.TryGetValue("ingame-test", out var ingameCount) && ingameCount != null)
    return IngameTest.Make(FindGame(opts.GetValueOrDefault("game"))!, int.Parse(ingameCount), opts.ContainsKey("copy-only"));
if (opts.TryGetValue("make-heavy", out var heavyOut) && heavyOut != null)
    return HeavyFixture.Make(FindGame(null)!, heavyOut, int.Parse(opts.GetValueOrDefault("count") ?? "300"));
if (opts.ContainsKey("test-names")) return NameTest.Run();
if (opts.TryGetValue("test-epic", out var epicDir) && epicDir != null)
{
    foreach (var dir in EpicInstallLocations(epicDir)) Console.WriteLine($"{dir}  game={IsGame(dir)}");
    return 0;
}
if (opts.TryGetValue("make-fixtures", out var fixturesOut) && fixturesOut != null)
    return Fixtures.Make(FindGame(opts.GetValueOrDefault("game")) ?? throw new Exception("game not found"), fixturesOut);

var outDir = opts.GetValueOrDefault("out") ?? AppContext.BaseDirectory;
bool quiet = opts.ContainsKey("json");
void Log(string s) { if (!quiet) Console.Error.WriteLine(s); }

// Remember folders the user gave us once (game, MO2), so the shortcuts keep working.
var settingsFile = Path.Combine(AppContext.BaseDirectory, "RipperDoc.ini");

// The tool doesn't have to live inside the game folder: it can be unzipped anywhere.
var game = FindGame(opts.GetValueOrDefault("game"), ReadSetting(settingsFile, "game"));
if (game == null && !quiet && (!Console.IsInputRedirected || opts.ContainsKey("ask-game"))) // --ask-game: test hook
{
    game = AskForGame();
    if (game != null)
    {
        WriteSetting(settingsFile, "game", game);
        Console.WriteLine("Saved: next time your game is found automatically.");
    }
}
if (game == null)
{
    Console.Error.WriteLine("Could not find Cyberpunk 2077. Pass it with --game \"D:\\Games\\Cyberpunk 2077\".");
    Pause();
    return 2;
}
var modRoot = opts.GetValueOrDefault("mod-root") ?? game;

var mo2 = opts.GetValueOrDefault("mo2") ?? ReadSetting(settingsFile, "mo2");
// Where an MO2 user's fix was saved last time (a real MO2 mod folder, outside the virtual game folder).
var savedFixDir = ReadSetting(settingsFile, "fixdir");
// Everywhere else a fix can be, so Undo finds it however it was made (savedFixDir alone misses MO2 fixes, see KnownFixDirs).
var fixDirs = BudgetArchive.KnownFixDirs(savedFixDir,
    new[] { mo2, ModManagers.RunningMo2Instance() }.Concat(ModManagers.AllMo2Instances()), AppContext.BaseDirectory);

if (opts.ContainsKey("remove-budget-archive"))
{
    var removed = BudgetArchive.Remove(modRoot, extraDirs: fixDirs);
    if (removed.Count > 0)
    {
        Console.WriteLine("The budget fix was removed. Everything is back as it was.");
        if (removed.Any(f => f.Contains(Mo2FixModName, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine("(In Mod Organizer 2 you can now also delete the empty \"" + Mo2FixModName + "\" mod.)");
    }
    else
    {
        Console.WriteLine("No budget fix found, so there was nothing to remove.");
        Console.WriteLine("(Mod Organizer 2: if a \"" + Mo2FixModName + "\" mod is still in MO2's list, untick or delete it there.)");
    }
    return 0;
}
if (opts.ContainsKey("live"))
    return LiveMonitor.Run(opts.GetValueOrDefault("process") ?? "Cyberpunk2077", opts.GetValueOrDefault("label") ?? "session",
        int.TryParse(opts.GetValueOrDefault("seconds"), out var secs) ? secs : 0, outDir, BudgetArchive.FindExisting(modRoot, fixDirs) != null);

Log($"VRAM RipperDoc v{Report.Version} - scanning {game}");
bool interactive = !quiet && (!Console.IsInputRedirected || opts.ContainsKey("ask-mo2")); // --ask-mo2: test hook

int lastPct = -1;
var progressLock = new object();
void ShowProgress(int done, int total)
{
    lock (progressLock)
    {
        if (quiet || total < 20) return;
        int pct = done * 100 / total;
        if (pct == lastPct && done != total) return;
        lastPct = pct;
        Console.Error.Write($"\r  Scanning mods: {done:N0} / {total:N0} ({pct}%)   ");
        if (done == total) Console.Error.WriteLine();
    }
}

ScanResult Scan(string? mo2Instance) => new Scanner(new ScanOptions
{
    GameDir = game,
    ModRoot = opts.GetValueOrDefault("mod-root"),
    HashList = opts.GetValueOrDefault("hashes"),
    Mo2Instance = mo2Instance,
    CompareVanilla = !opts.ContainsKey("no-vanilla"),
    Log = Log,
    Progress = ShowProgress,
}).Scan();

ScanResult result;
try
{
    result = Scan(mo2);
    // Nothing found in the game folder: MO2 keeps mods outside it. Ask once, then remember.
    if (result.Mods.Count == 0 && mo2 == null && interactive && opts.GetValueOrDefault("mod-root") == null && !UnderMo2())
    {
        var picked = AskForMo2();
        if (picked != null)
        {
            lastPct = -1;
            result = Scan(picked);
            if (result.Mods.Count > 0)
            {
                WriteSetting(settingsFile, "mo2", picked);
                Console.WriteLine("Saved: next time your MO2 mods are found automatically.");
            }
            else Console.WriteLine("No mods found there either. Pick the folder that contains ModOrganizer.ini.");
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Scan failed: " + ex.Message);
    Pause();
    return 1;
}

long budget;
if (opts.TryGetValue("budget", out var b) && double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var gb))
{
    budget = (long)(gb * (1L << 30));
    result.BudgetWhy = "set with --budget";
}
else (budget, result.BudgetWhy) = GpuInfo.SuggestBudget(result.Gpu?.VramBytes ?? 8L << 30, result.Settings);
result.BudgetArchive ??= BudgetArchive.FindExisting(modRoot, fixDirs);
var plan = BudgetPlan.Build(result, budget, includeNew: opts.ContainsKey("include-new-items"));
if (opts.ContainsKey("stress-all")) // developer stress test: halve every loaded 2K+ texture
    plan.Steps = result.Mods.SelectMany(m => m.Assets.Where(a => a.Kind == AssetKind.Texture && a.Loads && a.MaxDim >= 2048).Select(a => new PlanStep(m, a, 1, a.GpuBytes * 3 / 4))).ToList();

if (opts.TryGetValue("community", out var communitySource) && communitySource != null)
    Community.Annotate(result, Community.Load(communitySource, Log));

if (quiet)
{
    using var stdout = Console.OpenStandardOutput();
    Report.WriteJson(result, plan, stdout);
    return 0;
}

Directory.CreateDirectory(outDir);
result.HistoryDiff = History.SaveAndDiff(result, outDir);
bool makingFix = opts.ContainsKey("make-budget-archive");
if (!makingFix) Report.PrintConsole(result, plan, int.TryParse(opts.GetValueOrDefault("top"), out var top) ? top : 25);

string? openAfter = null;
if (makingFix)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 72));
    bool asked = false;
    if (opts.ContainsKey("ask") && !quiet)
    {
        // Show what each choice involves (counts, size, rough time) before asking.
        asked = true;
        var normal = BudgetPlan.Build(result, budget, includeNew: false);
        var newSteps = BudgetPlan.Build(result, budget, includeNew: true).Steps.Where(s => !s.Asset.AlwaysLoaded).ToList();
        var chosen = new List<PlanStep>();
        if (normal.Current <= normal.Budget)
            Console.WriteLine($"Normal play: your mods add about {Report.Size(normal.Current)} on top of the game; your card has room for {Report.Size(normal.Budget)}. Nothing to fix there.");
        else if (normal.Steps.Count == 0)
            Console.WriteLine($"Normal play: your mods add {Report.Size(normal.Current - normal.Budget)} more than your card has room for, but no texture is big enough to halve. Swap the heaviest mod for a lighter version instead.");
        else
        {
            Console.WriteLine($"Normal play: your mods add about {Report.Size(normal.Current)} on top of the game, but your card only has room for {Report.Size(normal.Budget)} ({Report.Size(normal.Current - normal.Budget)} too much).");
            Console.WriteLine($"  Fix: halve {normal.Steps.Count:N0} texture(s), saving about {Report.Size(normal.Steps.Sum(s => s.Saves))}. {Describe(normal.Steps)}");
            if (Ask("Halve these textures?")) chosen.AddRange(normal.Steps);
        }
        if (normal.Current > normal.Budget && Report.SettingsTips(result) is { Length: > 0 } tips)
            Console.WriteLine($"  Lower settings also give mods more room (estimates): {tips}.");
        Console.WriteLine();
        if (newSteps.Count == 0)
            Console.WriteLine("New clothing, cars and props: none have 4K textures. Nothing to shrink there.");
        else
        {
            Console.WriteLine($"New clothing, cars and props: {newSteps.Count:N0} texture(s) are 4K. They only use VRAM while you wear, drive or spawn them.");
            Console.WriteLine($"  Shrinking them to 2K saves up to {Report.Size(newSteps.Sum(s => s.Saves))} while they're in use. {Describe(newSteps)}");
            if (Ask("Also shrink these to 2K?")) chosen.AddRange(newSteps);
        }
        plan.Steps = chosen;
        Console.WriteLine();
    }
    if (plan.Steps.Count == 0)
    {
        if (asked) Console.WriteLine("Nothing was changed.");
        else
        {
            Console.WriteLine(plan.Current <= plan.Budget
                ? "Nothing to fix: during normal play your mods already fit in your card's VRAM."
                : "Nothing to fix: none of your mods' textures are big enough to halve (2K or larger).");
            if (!plan.IncludeNew && result.WhenUsedDelta > 0)
                Console.WriteLine("New clothing, cars and props were left alone (add --include-new-items to shrink their 4K textures too).");
        }
    }
    else
    {
        // Through MO2, writing into the virtual game folder can hang at the very end (seen in user logs),
        // so the fix becomes a real MO2 mod folder instead. --mo2-output <instance> forces this (testing).
        bool viaMo2 = UnderMo2() || opts.ContainsKey("mo2-output");
        string? targetDir = null;
        bool mo2ModFolder = false;
        if (viaMo2)
        {
            var inst = opts.GetValueOrDefault("mo2-output") ?? mo2 ?? ModManagers.RunningMo2Instance();
            var modsDir = inst != null ? ModManagers.Mo2ModsDir(inst) : null;
            mo2ModFolder = modsDir != null;
            var root = modsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VRAM RipperDoc");
            targetDir = Path.Combine(root, Mo2FixModName, "archive", "pc", "mod");
        }
        int threads = int.TryParse(opts.GetValueOrDefault("threads"), out var t) && t > 0 ? t
            : viaMo2 ? 2 // MO2's virtual file system is safest with little concurrent file I/O
            : Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        Console.WriteLine($"Writing the fix: {plan.Steps.Count:N0} texture(s). Leave this window open until it says DONE.");
        Console.WriteLine("  (Don't click inside this window while it works: Windows pauses programs while text is being selected.)");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var status = new BudgetArchive.BuildStatus();
        using var fixLog = OpenLog(Path.Combine(outDir, "VRAM-RipperDoc-Fix.log"));
        void Trace(string s) { lock (fixLog) fixLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
        Trace($"v{Report.Version} game={game} mo2={viaMo2} threads={threads} steps={plan.Steps.Count}");

        // Progress runs on its own thread and only reads counters, so a blocked console can never stall the work.
        var buildDone = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            int lastDone = -1; var lastChange = DateTime.UtcNow; bool warned = false;
            while (!buildDone.Wait(500))
            {
                int done = Volatile.Read(ref status.Done), total = Math.Max(1, status.Total);
                if (done != lastDone) { lastDone = done; lastChange = DateTime.UtcNow; warned = false; }
                string eta = "";
                if (done >= 3 && done < total)
                {
                    var left = TimeSpan.FromSeconds(started.Elapsed.TotalSeconds / done * (total - done));
                    eta = left.TotalMinutes >= 1 ? $" · about {Math.Ceiling(left.TotalMinutes)} min left" : $" · about {Math.Max(1, Math.Ceiling(left.TotalSeconds))} s left";
                }
                bool saving = status.Phase == "saving";
                Console.Write(saving
                    ? $"\r  Saving the fix file...                                              "
                    : $"\r  Writing fix: {done:N0} / {total:N0} textures ({done * 100 / total}%){eta}        ");
                if (!warned && DateTime.UtcNow - lastChange > TimeSpan.FromSeconds(60))
                {
                    warned = true;
                    var slow = status.InFlight.OrderBy(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
                    Console.WriteLine();
                    Console.WriteLine(saving || slow.Count == 0
                        ? "  Still saving the fix file (all textures are done)."
                        : $"  Still working on: {string.Join("; ", slow)}");
                    Console.WriteLine("  (If this never moves on, close the window and post VRAM-RipperDoc-Fix.log on the mod page.)");
                    Trace($"watchdog: no progress for 60 s; phase={status.Phase}; in flight: " + string.Join("; ", status.InFlight.Keys));
                }
            }
            Console.WriteLine($"\r  Writing fix: {status.Done:N0} / {status.Total:N0} textures (100%), saved.                         ");
        }) { IsBackground = true };
        ui.Start();
        BudgetArchive.Outcome outcome;
        Trace($"output: {targetDir ?? "game folder"}");
        try { outcome = BudgetArchive.Build(result, plan, modRoot, Log, status, threads, Trace, targetDir, fixDirs); }
        finally { buildDone.Set(); ui.Join(2000); }
        if (targetDir != null) WriteSetting(settingsFile, "fixdir", targetDir);
        Trace($"finished: {outcome.Textures} written, {outcome.Skipped.Count} skipped, {started.Elapsed.TotalSeconds:0}s");
        Console.WriteLine($"  Finished in {started.Elapsed.TotalSeconds:0}s.");
        Console.WriteLine();
        result.BudgetArchive = outcome.Path;
        Console.WriteLine($"DONE: {outcome.Textures} texture(s) halved, saving up to {Report.Size(outcome.SavedBytes)} of VRAM.");
        Console.WriteLine();
        Console.WriteLine("The fix is ONE new file (your mods were not changed):");
        Console.WriteLine($"  {outcome.Path}");
        if (outcome.Skipped.Count > 0) Console.WriteLine($"({outcome.Skipped.Count} texture(s) were skipped because their format can't be shrunk safely.)");
        Console.WriteLine();
        if (mo2ModFolder)
        {
            Console.WriteLine($"MOD ORGANIZER 2: the fix was saved as a new MO2 mod called \"{Mo2FixModName}\".");
            Console.WriteLine("  1. In MO2, press F5 (or the Refresh button) so it shows up in the left list.");
            Console.WriteLine("  2. Tick its checkbox to enable it. That's it; start the game from MO2 as usual.");
            Console.WriteLine("  To undo: untick it, or double-click \"Undo budget fix.bat\".");
        }
        else if (viaMo2)
        {
            Console.WriteLine("MOD ORGANIZER 2: your MO2 mods folder wasn't found, so the fix was saved as a ready-made mod folder:");
            Console.WriteLine($"  {Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(outcome.Path))))}");
            Console.WriteLine("  Copy that folder into MO2's mods folder (or zip it and use MO2's \"Install a new mod from an archive\"), then enable it.");
        }
        else Console.WriteLine("Start the game as usual. To undo, double-click \"Undo budget fix.bat\".");
        openAfter = outcome.Path;
    }
    Console.WriteLine(new string('=', 72));
}

if (opts.ContainsKey("share"))
{
    var sharePath = Path.Combine(outDir, "VRAM-RipperDoc-Share.json");
    Community.WriteShare(result, sharePath);
    Console.WriteLine($"Share file (anonymous: archive names and numbers only): {sharePath}");
    Console.WriteLine("Submit it at https://github.com/La2o/Cyberpunk2077-VRAMRipperDoc-PerformanceOptimizer/issues/new?template=vram-cost.yml");
}

var html = Path.Combine(outDir, "VRAM-RipperDoc-Report.html");
var json = Path.Combine(outDir, "VRAM-RipperDoc-Report.json");
using (var fs = File.Create(json)) Report.WriteJson(result, plan, fs);
Report.WriteHtml(result, plan, html);
File.WriteAllText(Path.Combine(outDir, "VRAM-RipperDoc-Summary.md"), Summary.Markdown(result, plan));
Console.WriteLine();
Console.WriteLine($"Report: {html}");
if (!opts.ContainsKey("no-open") && OperatingSystem.IsWindows())
    try
    {
        // After a fix, show the new file in Explorer; after a scan, open the report.
        if (openAfter != null && File.Exists(openAfter)) Process.Start("explorer.exe", $"/select,\"{openAfter}\"");
        else if (!makingFix) Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
    }
    catch { }
Pause();
return 0;

void Pause()
{
    // Double-clicked (no args): keep the window open so the summary can be read.
    if ((args.Length == 0 || opts.ContainsKey("pause")) && !Console.IsInputRedirected) { Console.WriteLine("Press any key to close."); Console.ReadKey(true); }
}

static StreamWriter OpenLog(string path)
{
    try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); return new StreamWriter(path, append: false) { AutoFlush = true }; }
    catch { return StreamWriter.Null; }
}

// Windows "QuickEdit": clicking in a console window starts a text selection and pauses every
// program writing to it until Esc/Enter. Players click windows; switch it off for this process.
static void DisableQuickEdit()
{
    if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return;
    try
    {
        var h = NativeConsole.GetStdHandle(-10); // STD_INPUT_HANDLE
        if (NativeConsole.GetConsoleMode(h, out uint mode))
            NativeConsole.SetConsoleMode(h, (mode & ~0x0040u) | 0x0080u); // clear QUICK_EDIT, set EXTENDED_FLAGS
    }
    catch { }
}

// Y/N question; works with a keyboard or piped input.
bool Ask(string question)
{
    while (true)
    {
        Console.Write($"{question} [Y/N] ");
        string? answer;
        if (Console.IsInputRedirected) answer = Console.ReadLine();
        else { var k = Console.ReadKey(intercept: true); answer = k.KeyChar.ToString(); }
        if (answer == null) { Console.WriteLine("N"); return false; }
        answer = new string(answer.Where(char.IsLetter).ToArray()).ToUpperInvariant(); // ignores BOMs and stray characters
        if (answer.StartsWith('Y')) { Console.WriteLine("Y"); return true; }
        if (answer.StartsWith('N')) { Console.WriteLine("N"); return false; }
        if (!Console.IsInputRedirected) Console.WriteLine();
    }
}

// "About 240 MB to write, under a minute": size scales the halved data by each texture's compression ratio;
// time assumes ~120 MB/s of source texture per worker thread (measured on a mid-range desktop).
static string Describe(IReadOnlyCollection<PlanStep> steps)
{
    long bytes = BudgetArchive.EstimateBytes(steps);
    double srcMb = steps.Sum(s => (double)s.Asset.GpuBytes) / (1 << 20);
    int workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
    double seconds = srcMb / (120.0 * workers) + steps.Count * 0.01;
    string time = seconds < 60 ? "under a minute" : $"roughly {Math.Ceiling(seconds / 60)} minute(s)";
    return $"About {Report.Size(bytes)} to write, {time}.";
}

string? AskForMo2()
{
    Console.WriteLine();
    Console.WriteLine("No mods were found in your game folder.");
    var found = ModManagers.AllMo2Instances();
    if (found.Count > 0)
    {
        Console.WriteLine("If you use Mod Organizer 2, pick your setup:");
        for (int i = 0; i < found.Count; i++) Console.WriteLine($"  {i + 1}) {found[i]}");
        Console.Write("Type its number, paste a different MO2 folder, or press Enter to skip: ");
    }
    else Console.Write("If you use Mod Organizer 2, paste the folder that contains ModOrganizer.ini (or press Enter to skip): ");
    var answer = ReadPath();
    if (string.IsNullOrEmpty(answer)) return null;
    if (int.TryParse(answer, out var n) && n >= 1 && n <= found.Count) return found[n - 1];
    if (File.Exists(answer) && Path.GetFileName(answer).Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)) answer = Path.GetDirectoryName(answer)!;
    return Directory.Exists(answer) ? answer : null;
}

// A typed, pasted or dragged-in path: without the quotes Explorer adds, and without the BOM piped input can start with.
static string? ReadPath() => Console.ReadLine()?.Trim().Trim('﻿').Trim().Trim('"');

// MO2 runs tools through its virtual file system (usvfs); new files then land in "Overwrite".
static bool UnderMo2()
{
    try { return Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase)); }
    catch { return false; }
}

static string? ReadSetting(string file, string key)
{
    try
    {
        if (!File.Exists(file)) return null;
        var line = File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        var v = line?[(key.Length + 1)..].Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
    catch { return null; }
}

static void WriteSetting(string file, string key, string value)
{
    try
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file).Where(l => !l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
        lines.Add($"{key}={value}");
        File.WriteAllLines(file, lines);
    }
    catch { }
}

static bool IsGame(string? d) => d != null && Directory.Exists(Path.Combine(d, "archive", "pc", "content"));

static string? FindGame(string? hint, string? saved = null)
{
    if (hint != null) return IsGame(hint) ? Path.GetFullPath(hint) : null;

    // Installed as a mod: <game>/tools/VRAM RipperDoc/VramRipperdoc.exe
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (IsGame(d.FullName)) return d.FullName;
    for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null; d = d.Parent)
        if (IsGame(d.FullName)) return d.FullName;
    // Unzipped somewhere else: the game folder the player gave us before.
    if (IsGame(saved)) return Path.GetFullPath(saved!);

    var candidates = new List<string>();
    if (OperatingSystem.IsWindows())
    {
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            foreach (var root in new[] { steam, @"C:\Program Files (x86)\Steam" }.Where(r => r != null))
            {
                var vdf = Path.Combine(root!, "steamapps", "libraryfolders.vdf");
                candidates.Add(Path.Combine(root!, "steamapps", "common", "Cyberpunk 2077"));
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        candidates.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "Cyberpunk 2077"));
            }
            foreach (var key in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1423049311", @"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\1423049311" })
                if (Registry.GetValue(key, "path", null) is string gog) candidates.Add(gog);
        }
        catch { }
        // Epic: the launcher's install manifests say where each game is, wherever it was installed.
        candidates.AddRange(EpicInstallLocations(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests")));
        candidates.Add(@"C:\Program Files\Epic Games\Cyberpunk 2077");
        candidates.Add(@"C:\GOG Games\Cyberpunk 2077");
    }
    var found = candidates.FirstOrDefault(IsGame);
    return found == null ? null : Path.GetFullPath(found);
}

static List<string> EpicInstallLocations(string manifestDir)
{
    var found = new List<string>();
    try
    {
        foreach (var item in Directory.GetFiles(manifestDir, "*.item"))
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(item));
                if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } dir) found.Add(dir);
            }
            catch { }
    }
    catch { }
    return found;
}

string? AskForGame()
{
    Console.WriteLine();
    Console.WriteLine("Couldn't find Cyberpunk 2077 by itself.");
    Console.Write("Drag your Cyberpunk 2077 folder (the one with \"bin\" and \"archive\") into this window, or paste its path, then press Enter: ");
    var answer = ReadPath();
    if (string.IsNullOrEmpty(answer)) return null;
    try
    {
        // Also accept the game's exe or any folder inside the game.
        for (var d = new DirectoryInfo(File.Exists(answer) ? Path.GetDirectoryName(answer)! : answer); d != null; d = d.Parent)
            if (IsGame(d.FullName)) return d.FullName;
    }
    catch { }
    Console.WriteLine("That doesn't look like the Cyberpunk 2077 folder (there's no archive\\pc\\content inside).");
    return null;
}

static class NativeConsole
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int nStdHandle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

static partial class Program
{
    /// <summary>Name of the MO2 mod folder the fix is saved as when run through Mod Organizer 2.</summary>
    public const string Mo2FixModName = "VRAM RipperDoc Budget Fix";
}
