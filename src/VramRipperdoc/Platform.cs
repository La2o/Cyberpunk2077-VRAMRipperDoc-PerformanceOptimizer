using System.Runtime.InteropServices;

namespace VramRipperdoc;

/// <summary>
/// Linux and Steam Deck: the Linux shortcuts run this Windows exe through Wine or Proton, because the game's Oodle
/// (needed to read archives) only exists as a Windows DLL. Wine shows the Linux file system as drive Z:, so Linux
/// paths that players type or drag in are translated, and paths shown to them are translated back.
/// </summary>
public static class Platform
{
    /// <summary>Settable by the developer test hook --as-wine.</summary>
    public static bool IsWine { get; set; } = DetectWine();

    static bool DetectWine()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return NativeLibrary.TryLoad("ntdll.dll", out var h) && NativeLibrary.TryGetExport(h, "wine_get_version", out _); }
        catch { return false; }
    }

    /// <summary>
    /// A typed, pasted or dragged-in path: without the quotes Explorer and Linux terminals add, without the BOM piped
    /// input can start with, and under Wine with Linux paths ("/home/...", "file:///home/...") moved to drive Z:.
    /// </summary>
    public static string? FromUser(string? input) => FromUser(input, IsWine);

    public static string? FromUser(string? input, bool wine)
    {
        if (input == null) return null;
        var s = input.Trim().Trim('\uFEFF').Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'')) s = s[1..^1];
        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) s = Uri.UnescapeDataString(s[7..]);
        if (wine && s.StartsWith('/')) s = "Z:" + s.Replace('/', '\\');
        return s;
    }

    /// <summary>A path as the player knows it: under Wine, Z:\home\... is shown as /home/....</summary>
    public static string Shown(string path) => Shown(path, IsWine);

    public static string Shown(string path, bool wine) =>
        wine && path.Length >= 3 && char.ToUpperInvariant(path[0]) == 'Z' && path[1] == ':' && path[2] == '\\' ? path[2..].Replace('\\', '/') : path;
}
