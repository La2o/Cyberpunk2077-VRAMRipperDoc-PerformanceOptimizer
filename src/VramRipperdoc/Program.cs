using System.Diagnostics;
using VramRipperdoc;
using static VramRipperdoc.Setup;

const string Usage = """
VRAM RipperDoc - Performance Optimizer
Ranks your Cyberpunk 2077 asset mods by the VRAM and geometry they add, and plans a budget.
Most players only need the double-click shortcuts next to this exe (see START HERE.txt).

Usage: VramRipperdoc [options]
 App window (default when started without options)
  --gui                     Open the app window (also takes --game, --mod-root, --mo2, --out, --budget, --vram, --gpu, --settings)
  --cli                     Scan in this console instead, like the shortcuts do
 Scan (read-only)
  --game <dir>              Cyberpunk 2077 folder (auto-detected for Steam/GOG/Epic or when run from inside it; otherwise asked for once)
  --budget <GB>             VRAM your mods may add during normal play (default: from your GPU and in-game settings)
  --vram <GB> [--gpu <name>]  Your graphics card's VRAM (default: detected)
  --settings <file>         The game's UserSettings.json (default: detected)
  --out <dir>               Where to write reports (default: next to the exe)
  --mo2 <instance dir>      Read mods from a Mod Organizer 2 instance (auto-detected, or asked for once)
  --hashes <file>           Optional list of depot paths (e.g. WolvenKit's archivehashes.csv) for nicer names
  --top <n>                 Rows to show in the console (default 25)
  --no-vanilla              Skip comparing against the base game archives (faster, less accurate)
  --no-open                 Don't open the HTML report (or Explorer) when done
  --json                    Print the JSON report to stdout instead of the table
 Apply the plan (optional)
  --make-budget-archive     Write ONE extra archive with the planned textures shrunk (your mods are untouched)
  --include-new-items       Also shrink new clothing, cars and props to 2K (they only cost VRAM while used)
  --new-items-to 1k         ... to 1K instead (softer up close; fine on small screens like the Steam Deck)
  --threads <n>             Worker threads for writing the fix (default: CPU cores - 1, max 8; 2 under MO2)
  --remove-budget-archive   Delete it again
 Measure real VRAM
  --live [--label <name>] [--seconds <n>]   Record the game's actual VRAM use while you play (Ctrl+C to stop)
 Linux and Steam Deck: use the shortcuts in the Linux folder (they run this exe through Wine or Proton).
 Community (opt-in)
  --share                   Write an anonymous per-mod cost file you can submit on GitHub (in parts for big mod lists)
  --community [file|url]    Show other players' reported costs (default: the project's GitHub list)
""";

var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "no-vanilla", "no-open", "json", "help", "pause", "make-budget-archive", "include-new-items", "remove-budget-archive", "live", "share", "ask-mo2", "ask-game", "ask", "stress-all", "test-names", "test-share", "test-paths", "as-wine", "gui", "cli" };
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
if (opts.ContainsKey("as-wine")) Platform.IsWine = true; // test hook: behave as under Wine on Linux

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
    return HeavyFixture.Make(FindGame(null)!, heavyOut, int.Parse(opts.GetValueOrDefault("count") ?? "300"), int.Parse(opts.GetValueOrDefault("offset") ?? "0"));
if (opts.ContainsKey("test-names")) return NameTest.Run();
if (opts.ContainsKey("test-share")) return ShareTest.Run();
if (opts.ContainsKey("test-paths")) return PathTest.Run();
if (opts.TryGetValue("make-classes", out var classesOut) && classesOut != null)
    return ClassFixture.Make(FindGame(opts.GetValueOrDefault("game"))!, classesOut, int.Parse(opts.GetValueOrDefault("count") ?? "3"));
if (opts.TryGetValue("class-stats", out var every) && every != null) return ClassStats.Run(FindGame(opts.GetValueOrDefault("game"))!, int.Parse(every));
if (opts.TryGetValue("test-epic", out var epicDir) && epicDir != null)
{
    foreach (var dir in EpicInstallLocations(epicDir)) Console.WriteLine($"{dir}  game={IsGame(dir)}");
    return 0;
}
if (opts.TryGetValue("make-fixtures", out var fixturesOut) && fixturesOut != null)
    return Fixtures.Make(FindGame(opts.GetValueOrDefault("game")) ?? throw new Exception("game not found"), fixturesOut);
if (opts.TryGetValue("test-gui", out var guiTestRoot) && guiTestRoot != null) return GuiTest.Run(guiTestRoot, opts);

// Double-clicked (no options) or --gui: the app window. Every other option is the command line, as before.
if (!opts.ContainsKey("cli") && (args.Length == 0 || opts.ContainsKey("gui"))) return GuiApp.Run(opts);

var outDir = opts.GetValueOrDefault("out") ?? AppContext.BaseDirectory;
bool quiet = opts.ContainsKey("json");
void Log(string s) { if (!quiet) Console.Error.WriteLine(s); }

// Remember folders the user gave us once (game, MO2), so the shortcuts keep working.
var settingsFile = SettingsFile;
var keepSharp = KeepSharp.Set(KeepSharp.Load(settingsFile));

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
// Everywhere a fix can be (the saved MO2 fix folder, MO2 instances...), so Undo finds it however it was made.
var fixDirs = FixDirs(settingsFile, mo2);

if (opts.ContainsKey("remove-budget-archive"))
{
    var removed = BudgetArchive.Remove(modRoot, extraDirs: fixDirs);
    foreach (var line in Fix.RemovedMessages(removed)) Console.WriteLine(line);
    return 0;
}
if (opts.ContainsKey("live"))
    return LiveMonitor.Run(opts.GetValueOrDefault("process") ?? "Cyberpunk2077", opts.GetValueOrDefault("label") ?? "session",
        int.TryParse(opts.GetValueOrDefault("seconds"), out var secs) ? secs : 0, outDir, BudgetArchive.FindExisting(modRoot, fixDirs) != null,
        samplesIn: opts.GetValueOrDefault("samples") == "-" ? Console.In : null);

Log($"VRAM RipperDoc v{Report.Version} - scanning {Platform.Shown(game)}");
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

long budget = Budget(result, opts);
CheckFix(result, modRoot, fixDirs);
int newItemsMax = opts.GetValueOrDefault("new-items-to")?.Trim().ToLowerInvariant() is "1k" or "1024" ? 1024 : 2048;
var plan = BudgetPlan.Build(result, budget, includeNew: opts.ContainsKey("include-new-items"), newItemsMax, keepSharp);
int keptSharp = result.Mods.Select(KeepSharp.NameOf).Where(keepSharp.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count();
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
if (!makingFix)
{
    Report.PrintConsole(result, plan, int.TryParse(opts.GetValueOrDefault("top"), out var top) ? top : 25);
    if (keepSharp.Count > 0) Console.WriteLine(KeepSharp.Line(keptSharp));
}

string? openAfter = null;
if (makingFix)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 72));
    if (keepSharp.Count > 0) Console.WriteLine(KeepSharp.Line(keptSharp));
    bool asked = false;
    if (opts.ContainsKey("ask") && !quiet)
    {
        // Show what each choice involves (counts, size, rough time) before asking.
        asked = true;
        var normal = BudgetPlan.Build(result, budget, includeNew: false, keepSharp: keepSharp);
        List<PlanStep> NewItems(int max) => BudgetPlan.Build(result, budget, includeNew: true, max, keepSharp).Steps.Where(s => !s.Asset.AlwaysLoaded).ToList();
        var to2k = NewItems(2048);
        var to1k = NewItems(1024);
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
        if (to1k.Count == 0)
            Console.WriteLine("New clothing, cars and props: none have textures of 2K or larger. Nothing to shrink there.");
        else
        {
            // 2K first; 1K is offered on top, since it's softer up close (fine on small screens like the Steam Deck's).
            List<PlanStep>? pick = null;
            if (to2k.Count > 0)
            {
                Console.WriteLine($"New clothing, cars and props: {to2k.Count:N0} texture(s) are 4K or larger. They only use VRAM while you wear, drive or spawn them.");
                Console.WriteLine($"  Shrinking them to 2K saves up to {Report.Size(to2k.Sum(s => s.Saves))} while they're in use. {Describe(to2k)}");
                if (Ask("Also shrink these to 2K?")) pick = to2k;
            }
            else Console.WriteLine($"New clothing, cars and props: {to1k.Count:N0} texture(s) are 2K. They only use VRAM while you wear, drive or spawn them.");
            if (to2k.Count == 0 || pick != null)
            {
                long more = to1k.Sum(s => s.Saves) - (pick?.Sum(s => s.Saves) ?? 0);
                Console.WriteLine($"  Going to 1K ({to1k.Count:N0} texture(s), 2K ones included) saves {(pick != null ? "another" : "up to")} {Report.Size(more)}. "
                    + $"They look softer up close; on small screens like the Steam Deck's that's hard to see. {Describe(to1k)}");
                if (Ask(pick != null ? "Go further, to 1K instead?" : "Shrink these to 1K?")) pick = to1k;
            }
            if (pick != null) chosen.AddRange(pick);
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
                Console.WriteLine("New clothing, cars and props were left alone (add --include-new-items to shrink them to 2K, or also --new-items-to 1k).");
        }
    }
    else
    {
        var target = Fix.PickTarget(opts.GetValueOrDefault("mo2-output"), mo2);
        int threads = Fix.Threads(opts.GetValueOrDefault("threads"), target.ViaMo2);
        Console.WriteLine($"Writing the fix: {plan.Steps.Count:N0} texture(s). Leave this window open until it says DONE.");
        Console.WriteLine("  (Don't click inside this window while it works: Windows pauses programs while text is being selected.)");
        using var fix = new Fix.Writer(outDir);
        var started = fix.Started;
        var status = fix.Status;
        void Trace(string s) => fix.Trace(s);

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
                if (Fix.Eta(started.Elapsed, done, total) is { } left)
                    eta = left.TotalMinutes >= 1 ? $" · about {Math.Ceiling(left.TotalMinutes)} min left" : $" · about {Math.Max(1, Math.Ceiling(left.TotalSeconds))} s left";
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
        try { outcome = fix.Build(result, plan, game, modRoot, target, threads, fixDirs, settingsFile, Log); }
        finally { buildDone.Set(); ui.Join(2000); }
        Console.WriteLine($"  Finished in {started.Elapsed.TotalSeconds:0}s.");
        Console.WriteLine();
        result.BudgetArchive = outcome.Path;
        Console.WriteLine($"DONE: {outcome.Textures} texture(s) shrunk, saving up to {Report.Size(outcome.SavedBytes)} of VRAM.");
        Console.WriteLine();
        Console.WriteLine("The fix is ONE new file (your mods were not changed):");
        Console.WriteLine($"  {Platform.Shown(outcome.Path)}");
        if (outcome.Skipped.Count > 0) Console.WriteLine($"({outcome.Skipped.Count} texture(s) were skipped because their format can't be shrunk safely.)");
        Console.WriteLine();
        foreach (var line in Fix.NextSteps(target, outcome.Path, app: false)) Console.WriteLine(line);
        openAfter = outcome.Path;
    }
    Console.WriteLine(new string('=', 72));
}

if (opts.ContainsKey("share"))
{
    var shareFiles = Community.WriteShare(result, outDir);
    const string shareUrl = "https://github.com/La2o/Cyberpunk2077-VRAMRipperDoc-PerformanceOptimizer/issues/new?template=vram-cost.yml";
    if (shareFiles.Count == 1)
    {
        Console.WriteLine($"Share file (anonymous: archive names and numbers only): {Platform.Shown(shareFiles[0])}");
        Console.WriteLine($"Submit it at {shareUrl}");
    }
    else
    {
        Console.WriteLine($"Share files (anonymous: archive names and numbers only), in {shareFiles.Count} parts because GitHub limits the size of one post:");
        foreach (var f in shareFiles) Console.WriteLine($"  {Platform.Shown(f)}");
        Console.WriteLine($"Submit part 1 at {shareUrl}");
        Console.WriteLine("then paste each other part as a new comment on that same issue.");
    }
}

var html = Report.WriteFiles(result, plan, outDir);
Console.WriteLine();
Console.WriteLine($"Report: {Platform.Shown(html)}");
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
    // Started without options (now only --cli on its own): keep the window open so the summary can be read.
    bool bare = args.All(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase));
    if ((bare || opts.ContainsKey("pause")) && !Console.IsInputRedirected)
    {
        Console.WriteLine(Platform.IsWine ? "Press Enter to close." : "Press any key to close.");
        if (Platform.IsWine) Console.ReadLine(); else Console.ReadKey(true);
    }
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
    // Under Wine single key presses aren't reliable in a Linux terminal, so the answer is typed and ends with Enter.
    bool lineInput = Console.IsInputRedirected || Platform.IsWine;
    bool echoed = lineInput && !Console.IsInputRedirected; // the terminal already shows what was typed
    while (true)
    {
        Console.Write($"{question} [Y/N]{(echoed ? " then Enter" : "")} ");
        string? answer;
        if (lineInput) answer = Console.ReadLine();
        else { var k = Console.ReadKey(intercept: true); answer = k.KeyChar.ToString(); }
        if (answer == null) { Console.WriteLine("N"); return false; }
        answer = new string(answer.Where(char.IsLetter).ToArray()).ToUpperInvariant(); // ignores BOMs and stray characters
        if (answer.StartsWith('Y')) { if (!echoed) Console.WriteLine("Y"); return true; }
        if (answer.StartsWith('N')) { if (!echoed) Console.WriteLine("N"); return false; }
        if (!lineInput) Console.WriteLine();
    }
}

// "About 240 MB to write, under a minute" (the app window shows the same estimate).
static string Describe(IReadOnlyCollection<PlanStep> steps) => Fix.Describe(steps);

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

static string? ReadPath() => Platform.FromUser(Console.ReadLine());

string? AskForGame()
{
    Console.WriteLine();
    Console.WriteLine("Couldn't find Cyberpunk 2077 by itself.");
    Console.Write("Drag your Cyberpunk 2077 folder (the one with \"bin\" and \"archive\") into this window, or paste its path, then press Enter: ");
    var answer = ReadPath();
    if (string.IsNullOrEmpty(answer)) return null;
    // Also accept the game's exe or any folder inside the game.
    if (GameFrom(answer) is { } dir) return dir;
    Console.WriteLine(NotTheGame);
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
