using System.Text.Json;

namespace VramRipperdoc;

/// <summary>
/// The graphics settings that move VRAM the most, read from the game's UserSettings.json
/// (read-only). Used to estimate how much VRAM vanilla needs before mods.
/// </summary>
public sealed class GameSettings
{
    public int Width, Height;
    public string TextureQuality = "High";
    public bool RayTracing, PathTracing, FrameGen;
    public string? Preset;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json");

    public static GameSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in doc.RootElement.GetProperty("data").EnumerateArray())
                if (group.TryGetProperty("options", out var opts))
                    foreach (var o in opts.EnumerateArray())
                        if (o.TryGetProperty("name", out var n) && o.TryGetProperty("value", out var v))
                            values.TryAdd(n.GetString()!, v.Clone());

            string Str(string k) => values.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Bool(string k) => values.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.True;
            bool On(string k) { var s = Str(k); return s.Length > 0 && !s.Equals("Off", StringComparison.OrdinalIgnoreCase); }

            var s = new GameSettings
            {
                TextureQuality = Str("TextureQuality") is { Length: > 0 } tq ? tq : "High",
                PathTracing = Bool("RayTracedPathTracing"),
                Preset = Str("QuickPresets"),
                FrameGen = Bool("DLSSFrameGen") || On("FrameGeneration") || On("DLSS_MultiFrameGeneration")
                    || Bool("FSR3_FrameGeneration") || Bool("FSR4_FrameGeneration") || Bool("XESS_FrameGeneration")
                    || On("FSR3_FrameGeneration") || On("FSR4_FrameGeneration") || On("XESS_FrameGeneration"),
            };
            s.RayTracing = Bool("RayTracing") || s.PathTracing;
            var res = Str("Resolution").Split('x');
            if (res.Length == 2 && int.TryParse(res[0], out var w) && int.TryParse(res[1], out var h)) { s.Width = w; s.Height = h; }
            return s;
        }
        catch { return null; }
    }

    /// <summary>
    /// Rule-of-thumb vanilla VRAM for these settings. Anchored on public measurements (Environment
    /// Textures Overhaul: ~9.4 GB vanilla at high settings); verify with --live in your own game.
    /// </summary>
    public long EstimatedVanillaBytes()
    {
        double mp = Width * (double)Height / 1e6;
        double gb = mp <= 2.1 ? 6.0 : mp <= 3.7 ? 6.8 : mp <= 5.0 ? 7.5 : 8.5; // 1080p / 1440p / UW / 4K
        gb += TextureQuality.ToLowerInvariant() switch { "low" => -1.5, "medium" => -0.8, _ => 0 };
        gb += PathTracing ? 2.0 : RayTracing ? 1.0 : 0;
        if (FrameGen) gb += 0.5;
        return (long)(gb * (1L << 30));
    }

    /// <summary>Setting changes that give mods more room, with the VRAM each frees by the same rule of thumb.</summary>
    public List<(string Change, long Frees)> RoomTips()
    {
        static long Gb(double v) => (long)(v * (1L << 30));
        var tips = new List<(string, long)>();
        switch (TextureQuality.ToLowerInvariant())
        {
            case "low": break;
            case "medium": tips.Add(("Texture Quality Low", Gb(0.7))); break;
            default: tips.Add(("Texture Quality Medium", Gb(0.8))); break;
        }
        if (PathTracing) tips.Add(("path tracing off (ray tracing can stay on)", Gb(1.0)));
        else if (RayTracing) tips.Add(("ray tracing off", Gb(1.0)));
        if (FrameGen) tips.Add(("frame generation off", Gb(0.5)));
        return tips;
    }

    public string Describe() =>
        $"{(Width > 0 ? $"{Width}x{Height}" : "unknown resolution")}, {TextureQuality} textures"
        + (PathTracing ? ", path tracing" : RayTracing ? ", ray tracing" : "") + (FrameGen ? ", frame generation" : "");
}
