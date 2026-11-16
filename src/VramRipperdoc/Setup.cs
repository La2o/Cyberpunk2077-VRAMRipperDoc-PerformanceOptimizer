using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VramRipperdoc;

/// <summary>
/// What the command line and the app window both need before and after a scan: finding the game, the remembered
/// folders in RipperDoc.ini, Mod Organizer 2, the budget and where a fix may be. One copy, so the two can't drift.
/// </summary>
public static class Setup
{
    /// <summary>Folders the player gave us once (game, MO2), the keep-sharp list and where an MO2 fix went.</summary>
    public static string SettingsFile => Path.Combine(AppContext.BaseDirectory, "RipperDoc.ini");

    public static string? ReadSetting(string file, string key)
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

    public static void WriteSetting(string file, string key, string value)
    {
        try
        {
            var lines = File.Exists(file) ? File.ReadAllLines(file).Where(l => !l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
            lines.Add($"{key}={value}");
            File.WriteAllLines(file, lines);
        }
        catch { }
    }

    public static bool IsGame(string? d) => d != null && Directory.Exists(Path.Combine(d, "archive", "pc", "content"));

    public static string? FindGame(string? hint, string? saved = null)
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

    public static List<string> EpicInstallLocations(string manifestDir)
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

    /// <summary>The game folder from what the player picked: the folder itself, the game's exe or any folder inside it.</summary>
    public static string? GameFrom(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return null;
        try
        {
            for (var d = new DirectoryInfo(File.Exists(answer) ? Path.GetDirectoryName(answer)! : answer); d != null; d = d.Parent)
                if (IsGame(d.FullName)) return d.FullName;
        }
        catch { }
        return null;
    }

    public const string NotTheGame = "That doesn't look like the Cyberpunk 2077 folder (there's no archive\\pc\\content inside).";

    // MO2 runs tools through its virtual file system (usvfs); new files then land in "Overwrite".
    public static bool UnderMo2()
    {
        try { return Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>Everywhere a fix can be besides the game folder, so Undo finds it however it was made (see <see cref="BudgetArchive.KnownFixDirs"/>).</summary>
    public static List<string> FixDirs(string settingsFile, string? mo2) =>
        BudgetArchive.KnownFixDirs(ReadSetting(settingsFile, "fixdir"),
            new[] { mo2, ModManagers.RunningMo2Instance() }.Concat(ModManagers.AllMo2Instances()), AppContext.BaseDirectory);

    /// <summary>
    /// The room mods have during normal play. The Linux shortcuts pass what Wine can't see (the real GPU, and the
    /// settings file inside the game's Proton prefix); --budget overrides the estimate.
    /// </summary>
    public static long Budget(ScanResult result, IReadOnlyDictionary<string, string?> opts)
    {
        if (opts.TryGetValue("vram", out var vramArg) && double.TryParse(vramArg, System.Globalization.CultureInfo.InvariantCulture, out var vramGb) && vramGb > 0)
            result.Gpu = new GpuInfo(opts.GetValueOrDefault("gpu") ?? result.Gpu?.Name ?? "GPU", (long)(vramGb * (1L << 30)));
        if (opts.GetValueOrDefault("settings") is { } settingsArg)
            result.Settings = GameSettings.Load(Platform.FromUser(settingsArg)) ?? result.Settings;

        long budget;
        if (opts.TryGetValue("budget", out var b) && double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var gb))
        {
            budget = (long)(gb * (1L << 30));
            result.BudgetWhy = "set with --budget";
        }
        else (budget, result.BudgetWhy) = GpuInfo.SuggestBudget(result.Gpu?.VramBytes ?? 8L << 30, result.Settings);
        return budget;
    }

    /// <summary>Finds an existing fix (the scan only looks in the game folder) and whether it still matches the mods.</summary>
    public static void CheckFix(ScanResult result, string modRoot, IEnumerable<string> fixDirs)
    {
        result.BudgetArchive ??= BudgetArchive.FindExisting(modRoot, fixDirs);
        if (result.BudgetArchive != null) result.FixCheck = BudgetArchive.Check(result.BudgetArchive, result);
    }
}

/// <summary>
/// Mods the player wants left at full quality: the fix never shrinks their textures. Stored in RipperDoc.ini as one
/// "keepsharp=" line; a mod is named by its installed package (Vortex/MO2/REDmod) if it has one, else its archive name.
/// </summary>
public static class KeepSharp
{
    const string Key = "keepsharp";

    /// <summary>The name a mod is kept sharp by.</summary>
    public static string NameOf(ModReport m) => m.Package ?? m.Name;

    public static HashSet<string> Set(IEnumerable<string>? names) => new(names ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    public static List<string> Load(string settingsFile) => Decode(Setup.ReadSetting(settingsFile, Key));

    public static void Save(string settingsFile, IEnumerable<string> names) => Setup.WriteSetting(settingsFile, Key, Encode(names));

    /// <summary>Names joined with '|'. Mod names can hold almost anything, so '%', '|', control characters and spaces at either end are written as %XX.</summary>
    public static string Encode(IEnumerable<string> names) => string.Join("|", Clean(names).Select(n =>
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n.Length; i++)
        {
            char c = n[i];
            bool edgeSpace = char.IsWhiteSpace(c) && (i == 0 || i == n.Length - 1);
            if (c is '%' or '|' || c < ' ' || c == '\x7f') sb.Append($"%{(int)c:X2}");
            else if (edgeSpace) sb.Append(Uri.EscapeDataString(c.ToString())); // RipperDoc.ini values are trimmed when read
            else sb.Append(c);
        }
        return sb.ToString();
    }));

    public static List<string> Decode(string? value) =>
        value == null ? new() : Clean(value.Split('|').Select(Uri.UnescapeDataString));

    /// <summary>No empty names, no duplicates (names are compared like Windows file names), first spelling wins.</summary>
    public static List<string> Clean(IEnumerable<string?> names)
    {
        var seen = Set(null);
        return names.OfType<string>().Where(n => n.Trim().Length > 0 && seen.Add(n)).Take(10_000).ToList();
    }

    /// <summary>The one line the command line shows when the list isn't empty (<paramref name="count"/>: listed mods that are installed).</summary>
    public static string Line(int count) => $"Keeping {count} mod(s) sharp: the fix never shrinks their textures (chosen in the app window).";
}
