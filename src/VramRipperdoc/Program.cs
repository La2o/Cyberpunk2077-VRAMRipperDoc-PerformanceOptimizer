using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VramRipperdoc;

const string Usage = """
VRAM RipperDoc - Performance Optimizer
Ranks your Cyberpunk 2077 asset mods by the VRAM and geometry they add, and plans a budget.

Usage: VramRipperdoc [options]
 Scan (default, read-only)
  --game <dir>              Cyberpunk 2077 folder (auto-detected for Steam/GOG/Epic, or when run from inside it)
  --budget <GB>             VRAM your mods may add (default: from your GPU and in-game settings)
  --out <dir>               Where to write reports (default: next to the exe)
  --mo2 <instance dir>      Read mods from a Mod Organizer 2 instance (auto-detected when possible)
  --hashes <file>           Optional list of depot paths (e.g. WolvenKit's archivehashes.csv) for nicer names
  --top <n>                 Rows to show in the console (default 25)
  --no-vanilla              Skip comparing against the base game archives (faster, less accurate)
  --no-open                 Don't open the HTML report when done
  --json                    Print the JSON report to stdout instead of the table
 Apply the plan (optional)
  --make-budget-archive     Write ONE extra archive with the planned textures halved (your mods are untouched)
  --remove-budget-archive   Delete it again
 Measure real VRAM
  --live [--label <name>] [--seconds <n>]   Record the game's actual VRAM use while you play (Ctrl+C to stop)
 Community (opt-in)
  --share                   Write an anonymous per-mod cost file you can submit on GitHub
  --community [file|url]    Show other players' reported costs (default: the project's GitHub list)
""";

var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "no-vanilla", "no-open", "json", "help", "pause", "make-budget-archive", "remove-budget-archive", "live", "share" };
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
if (opts.TryGetValue("make-fixtures", out var fixturesOut) && fixturesOut != null)
    return Fixtures.Make(FindGame(opts.GetValueOrDefault("game")) ?? throw new Exception("game not found"), fixturesOut);

var outDir = opts.GetValueOrDefault("out") ?? AppContext.BaseDirectory;
bool quiet = opts.ContainsKey("json");
void Log(string s) { if (!quiet) Console.Error.WriteLine(s); }

var game = FindGame(opts.GetValueOrDefault("game"));
if (game == null)
{
    Console.Error.WriteLine("Could not find Cyberpunk 2077. Pass it with --game \"D:\\Games\\Cyberpunk 2077\".");
    Pause();
    return 2;
}
var modRoot = opts.GetValueOrDefault("mod-root") ?? game;

if (opts.ContainsKey("remove-budget-archive"))
{
    if (!BudgetArchive.Remove(modRoot)) Console.WriteLine("No budget archive found.");
    return 0;
}
if (opts.ContainsKey("live"))
    return LiveMonitor.Run(opts.GetValueOrDefault("process") ?? "Cyberpunk2077", opts.GetValueOrDefault("label") ?? "session",
        int.TryParse(opts.GetValueOrDefault("seconds"), out var secs) ? secs : 0, outDir, BudgetArchive.FindExisting(modRoot) != null);

Log($"VRAM RipperDoc v{Report.Version} - scanning {game}");
ScanResult result;
try
{
    result = new Scanner(new ScanOptions
    {
        GameDir = game,
        ModRoot = opts.GetValueOrDefault("mod-root"),
        HashList = opts.GetValueOrDefault("hashes"),
        Mo2Instance = opts.GetValueOrDefault("mo2"),
        CompareVanilla = !opts.ContainsKey("no-vanilla"),
        Log = Log,
    }).Scan();
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
var plan = BudgetPlan.Build(result, budget);

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
Report.PrintConsole(result, plan, int.TryParse(opts.GetValueOrDefault("top"), out var top) ? top : 25);

if (opts.ContainsKey("make-budget-archive"))
{
    Console.WriteLine();
    if (plan.Steps.Count == 0) Console.WriteLine("Budget archive: nothing to do (already within budget, or no texture over 2K to halve).");
    else
    {
        var outcome = BudgetArchive.Build(result, plan, modRoot, Log);
        Console.WriteLine($"Budget archive: {outcome.Path}");
        Console.WriteLine($"  {outcome.Textures} texture(s) halved, ~{Report.Size(outcome.SavedBytes)} less VRAM when they are on screen.");
        foreach (var s in outcome.Skipped.Take(10)) Console.WriteLine($"  skipped {s}");
        Console.WriteLine("  Undo: delete that file, or run with --remove-budget-archive.");
        result.BudgetArchive = outcome.Path;
    }
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
    try { Process.Start(new ProcessStartInfo(html) { UseShellExecute = true }); } catch { }
Pause();
return 0;

void Pause()
{
    // Double-clicked (no args): keep the window open so the summary can be read.
    if ((args.Length == 0 || opts.ContainsKey("pause")) && !Console.IsInputRedirected) { Console.WriteLine("Press any key to close."); Console.ReadKey(true); }
}

static string? FindGame(string? hint)
{
    static bool IsGame(string? d) => d != null && Directory.Exists(Path.Combine(d, "archive", "pc", "content"));
    if (hint != null) return IsGame(hint) ? Path.GetFullPath(hint) : null;

    // Installed as a mod: <game>/tools/VRAM RipperDoc/VramRipperdoc.exe
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (IsGame(d.FullName)) return d.FullName;
    for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null; d = d.Parent)
        if (IsGame(d.FullName)) return d.FullName;

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
        candidates.Add(@"C:\Program Files\Epic Games\Cyberpunk 2077");
        candidates.Add(@"C:\GOG Games\Cyberpunk 2077");
    }
    var found = candidates.FirstOrDefault(IsGame);
    return found == null ? null : Path.GetFullPath(found);
}
