using System.Text.Json;
using System.Text.RegularExpressions;

namespace VramRipperdoc;

public sealed record PackageInfo(string Package, int? NexusId, string Manager);

/// <summary>
/// Maps archive files back to the installed mod (and Nexus mod id) using Vortex's deployment
/// manifest or a Mod Organizer 2 instance. Read-only; nothing here touches the managers' data.
/// </summary>
public sealed class ModManagers
{
    readonly Dictionary<string, PackageInfo> _byFile = new(StringComparer.OrdinalIgnoreCase);
    public List<ArchiveSource> ExtraArchives { get; } = new();
    public List<string> Notes { get; } = new();

    // Vortex staging folders are named "<name>-<nexusId>-<version>-<timestamp>".
    static readonly Regex VortexName = new(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>.+)-(?<ts>\d{9,11})$", RegexOptions.Compiled);

    public PackageInfo? Lookup(string archivePath) => _byFile.GetValueOrDefault(Path.GetFileName(archivePath));

    public static ModManagers Detect(string gameDir, string modRoot, string? mo2Instance, Action<string> log)
    {
        var mm = new ModManagers();
        try { mm.ReadVortex(modRoot); } catch (Exception ex) { log($"! Vortex manifest unreadable: {ex.Message}"); }
        try
        {
            var instances = mo2Instance != null ? new List<string> { mo2Instance } : FindMo2Instances(gameDir);
            foreach (var inst in instances) mm.ReadMo2(inst, gameDir, explicitInstance: mo2Instance != null);
        }
        catch (Exception ex) { log($"! MO2 instance unreadable: {ex.Message}"); }
        foreach (var n in mm.Notes) log(n);
        return mm;
    }

    void ReadVortex(string root)
    {
        var candidates = new[] { root, Path.Combine(root, "archive", "pc", "mod") }
            .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "vortex.deployment*.json"));
        int n = 0;
        foreach (var manifest in candidates)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("files", out var files)) continue;
            foreach (var f in files.EnumerateArray())
            {
                var rel = f.TryGetProperty("relPath", out var r) ? r.GetString() : null;
                var source = f.TryGetProperty("source", out var s) ? s.GetString() : null;
                if (rel == null || source == null || !rel.EndsWith(".archive", StringComparison.OrdinalIgnoreCase)) continue;
                var m = VortexName.Match(source);
                var info = m.Success
                    ? new PackageInfo(m.Groups["name"].Value, int.Parse(m.Groups["id"].Value), "Vortex")
                    : new PackageInfo(source, null, "Vortex");
                _byFile[Path.GetFileName(rel)] = info;
                n++;
            }
        }
        if (n > 0) Notes.Add($"Vortex: matched {n} archive(s) to installed mods.");
    }

    static List<string> FindMo2Instances(string gameDir)
    {
        var list = new List<string>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (!Directory.Exists(root)) return list;
        foreach (var inst in Directory.GetDirectories(root))
        {
            var ini = Path.Combine(inst, "ModOrganizer.ini");
            if (!File.Exists(ini)) continue;
            var text = File.ReadAllText(ini);
            if (text.Contains("Cyberpunk 2077", StringComparison.OrdinalIgnoreCase)) list.Add(inst);
        }
        return list;
    }

    void ReadMo2(string instance, string gameDir, bool explicitInstance)
    {
        var iniPath = Path.Combine(instance, "ModOrganizer.ini");
        var ini = File.Exists(iniPath) ? ParseIni(File.ReadAllLines(iniPath)) : new();
        string Unwrap(string? v) => v == null ? "" : Regex.Replace(v, @"^@ByteArray\((.*)\)$", "$1").Replace(@"\\", @"\").Replace('/', '\\');

        var gamePath = Unwrap(ini.GetValueOrDefault("gamePath"));
        if (!explicitInstance && gamePath.Length > 0 &&
            !string.Equals(Path.GetFullPath(gamePath).TrimEnd('\\'), Path.GetFullPath(gameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return; // an instance for a different copy of the game

        var baseDir = Unwrap(ini.GetValueOrDefault("base_directory"));
        if (baseDir.Length == 0) baseDir = instance;
        var modsDir = Unwrap(ini.GetValueOrDefault("mod_directory")).Replace("%BASE_DIR%", baseDir);
        if (modsDir.Length == 0) modsDir = Path.Combine(baseDir, "mods");
        var profile = Unwrap(ini.GetValueOrDefault("selected_profile"));
        if (profile.Length == 0) profile = "Default";
        var modlist = Path.Combine(baseDir, "profiles", profile, "modlist.txt");
        if (!Directory.Exists(modsDir) || !File.Exists(modlist)) return;

        // modlist.txt: first line = highest priority; '+' enabled, '-' disabled.
        int n = 0, extra = 0;
        foreach (var line in File.ReadAllLines(modlist))
        {
            if (!line.StartsWith('+')) continue;
            var mod = line[1..].Trim();
            var dir = Path.Combine(modsDir, mod);
            var archDir = Path.Combine(dir, "archive", "pc", "mod");
            if (!Directory.Exists(archDir)) continue;
            var meta = Path.Combine(dir, "meta.ini");
            int? id = null;
            if (File.Exists(meta) && ParseIni(File.ReadAllLines(meta)).TryGetValue("modid", out var mid) && int.TryParse(mid, out var parsed) && parsed > 0) id = parsed;
            foreach (var f in Directory.GetFiles(archDir, "*.archive"))
            {
                var name = Path.GetFileName(f);
                if (_byFile.ContainsKey(name)) continue; // higher-priority mod already provides this file name
                _byFile[name] = new PackageInfo(mod, id, "MO2");
                ExtraArchives.Add(new ArchiveSource(f, "MO2: " + mod, mod, id, "MO2"));
                n++;
            }
            extra++;
        }
        if (n > 0) Notes.Add($"MO2 ({Path.GetFileName(instance)}, profile {profile}): {n} archive(s) from {extra} enabled mod(s).");
    }

    static Dictionary<string, string> ParseIni(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            int eq = l.IndexOf('=');
            if (eq <= 0 || l.StartsWith('[') || l.StartsWith(';')) continue;
            d.TryAdd(l[..eq].Trim(), l[(eq + 1)..].Trim());
        }
        return d;
    }
}
