using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VramRipperdoc;

/// <summary>
/// Starting the app window: the local server, an Edge or Chrome app-mode window (else the default browser), and the
/// rule that closing the window closes the tool. The console stays visible (minimized) so it can always be closed.
/// </summary>
public static class GuiApp
{
    /// <summary>No event stream for this long (and no job running): the window was closed, so the tool stops.</summary>
    public static readonly TimeSpan IdleQuit = TimeSpan.FromSeconds(20);
    /// <summary>The page said goodbye as its window closed: only long enough for a reload to reconnect.</summary>
    public static readonly TimeSpan ClosedQuit = TimeSpan.FromSeconds(4);
    /// <summary>The first window gets longer to connect: a cold browser start can be slow.</summary>
    public static readonly TimeSpan FirstConnect = TimeSpan.FromSeconds(60);

    public static int Run(IReadOnlyDictionary<string, string?> opts)
    {
        var session = new GuiSession(opts);
        int port = int.TryParse(opts.GetValueOrDefault("gui-port"), out var p) && p is > 0 and < 65536 ? p : 0;
        GuiServer server;
        try { server = new GuiServer(session, port, NewToken(opts.GetValueOrDefault("gui-token"))); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"VRAM RipperDoc couldn't start its window ({ex.Message}). The double-click shortcuts next to the exe still work.");
            return 1;
        }
        Console.WriteLine($"VRAM RipperDoc v{Report.Version} runs in its own window; this console belongs to it.");
        Console.WriteLine("Closing this console closes VRAM RipperDoc.");
        bool open = !opts.ContainsKey("no-open");
        Console.WriteLine($"{(open ? "If the window didn't open, open" : "Open")} this address in your browser: {server.Url}");
        if (open)
        {
            OpenWindow(server.Url);
            MinimizeConsole();
        }
        WaitUntilDone(session, server);
        return 0;
    }

    /// <summary>The token from --gui-token, else 128 random bits as hex.</summary>
    public static string NewToken(string? given) =>
        given is { Length: > 0 } ? given : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Blocks until Quit was asked or the window is gone; a running job always finishes first.</summary>
    public static void WaitUntilDone(GuiSession session, GuiServer server)
    {
        while (true)
        {
            session.WaitForChange(session.Version, 500);
            if (session.Busy) continue;
            if (session.QuitRequested) break;
            if (server.Streams == 0 && server.Idle > (!server.EverConnected ? FirstConnect : session.WindowClosing ? ClosedQuit : IdleQuit)) break;
        }
        server.Stop();
    }

    /// <summary>Edge or Chrome as an app window (no tabs or address bar), else the default browser. Under Wine, the Linux browser.</summary>
    static void OpenWindow(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows() && !Platform.IsWine)
            {
                var roots = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                    .Select(Environment.GetFolderPath).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var exe in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" })
                    foreach (var root in roots)
                    {
                        var path = Path.Combine(root, exe);
                        if (!File.Exists(path)) continue;
                        var psi = new ProcessStartInfo(path) { UseShellExecute = false };
                        psi.ArgumentList.Add($"--app={url}");
                        psi.ArgumentList.Add("--window-size=1280,880"); // ignored when the browser is already running: the page then sizes its window itself
                        Process.Start(psi);
                        return;
                    }
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { } // the address is in the console for this case
    }

    /// <summary>Minimizes the console, but only one this process owns (double-clicked), never a terminal the player started us from.</summary>
    static void MinimizeConsole()
    {
        if (!OperatingSystem.IsWindows() || Platform.IsWine) return;
        try
        {
            var procs = new uint[4];
            if (GetConsoleProcessList(procs, (uint)procs.Length) != 1) return;
            var h = GetConsoleWindow();
            if (h != IntPtr.Zero) ShowWindow(h, 6); // SW_MINIMIZE
        }
        catch { }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
