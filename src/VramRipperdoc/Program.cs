using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VramRipperdoc;

const string Usage = """
VRAM RipperDoc - Performance Optimizer
Ranks your Cyberpunk 2077 asset mods by the VRAM and geometry they add, and plans a budget.

Usage: VramRipperdoc [options]
  --game <dir>       Cyberpunk 2077 folder (auto-detected for Steam/GOG/Epic, or when run from inside it)
  --budget <GB>      VRAM your mods may add on top of vanilla (default: suggested from your GPU)
  --out <dir>        Where to write VRAM-RipperDoc-Report.html/.json (default: next to the exe)
  --hashes <file>    Optional list of depot paths (e.g. WolvenKit's archivehashes.csv) for nicer names
  --top <n>          Rows to show in the console (default 25)
  --no-vanilla       Skip comparing against the base game archives (faster, less accurate)
  --no-open          Don't open the HTML report when done
  --json             Print the JSON report to stdout instead of the table
""";

var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (!a.StartsWith("--")) { opts["game"] = a; continue; }
    var key = a[2..];
    bool flag = key is "no-vanilla" or "no-open" or "json" or "help" or "pause";
    opts[key] = flag ? "1" : i + 1 < args.Length ? args[++i] : null;
}
if (opts.ContainsKey("help")) { Console.WriteLine(Usage); return 0; }

if (opts.TryGetValue("make-fixtures", out var fixturesOut) && fixturesOut != null)
    return Fixtures.Make(FindGame(opts.GetValueOrDefault("game")) ?? throw new Exception("game not found"), fixturesOut);

bool quiet = opts.ContainsKey("json");
void Log(string s) { if (!quiet) Console.Error.WriteLine(s); }

var game = FindGame(opts.GetValueOrDefault("game"));
if (game == null)
{
    Console.Error.WriteLine("Could not find Cyberpunk 2077. Pass it with --game \"D:\\Games\\Cyberpunk 2077\".");
    Pause();
    return 2;
}
Log($"VRAM RipperDoc v{typeof(Report).Assembly.GetName().Version?.ToString(3)} - scanning {game}");

ScanResult result;
try
{
    result = new Scanner(new ScanOptions
    {
        GameDir = game,
        ModRoot = opts.GetValueOrDefault("mod-root"),
        HashList = opts.GetValueOrDefault("hashes"),
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

long budget = opts.TryGetValue("budget", out var b) && double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var gb)
    ? (long)(gb * (1L << 30))
    : GpuInfo.SuggestedModBudget(result.Gpu?.VramBytes ?? 8L << 30);
var plan = BudgetPlan.Build(result, budget);

if (quiet)
{
    using var stdout = Console.OpenStandardOutput();
    Report.WriteJson(result, plan, stdout);
    return 0;
}

Report.PrintConsole(result, plan, int.TryParse(opts.GetValueOrDefault("top"), out var top) ? top : 25);
var outDir = opts.GetValueOrDefault("out") ?? AppContext.BaseDirectory;
Directory.CreateDirectory(outDir);
var html = Path.Combine(outDir, "VRAM-RipperDoc-Report.html");
var json = Path.Combine(outDir, "VRAM-RipperDoc-Report.json");
using (var fs = File.Create(json)) Report.WriteJson(result, plan, fs);
Report.WriteHtml(result, plan, html);
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
