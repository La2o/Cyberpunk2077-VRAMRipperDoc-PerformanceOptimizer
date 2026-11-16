using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VramRipperdoc;

/// <summary>
/// Measures the game's real dedicated VRAM use with Windows' "GPU Process Memory" performance
/// counters (the same source as Task Manager). External and read-only: nothing is injected.
/// </summary>
public static class LiveMonitor
{
    public sealed record Session(string Label, DateTime Start, double Seconds, long Peak, long P95, long Steady, long GpuTotalPeak, bool BudgetArchive, int Samples);

    const uint PDH_FMT_LARGE = 0x400;
    const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint fmt, ref uint bufSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr q);

    sealed class Query : IDisposable
    {
        readonly IntPtr _q, _proc, _adapter;
        public Query()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) throw new InvalidOperationException("Performance counters unavailable");
            if (PdhAddEnglishCounterW(_q, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _proc) != 0)
                throw new InvalidOperationException("GPU Process Memory counters unavailable (needs Windows 10 1709+ and a WDDM 2.x driver)");
            PdhAddEnglishCounterW(_q, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _adapter);
            PdhCollectQueryData(_q);
        }

        public (long Process, long Adapter) Sample(IReadOnlyCollection<int> pids)
        {
            PdhCollectQueryData(_q);
            var tags = pids.Select(p => $"pid_{p}_").ToList();
            long proc = Read(_proc).Where(x => tags.Any(t => x.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase))).Sum(x => x.Value);
            long adapter = _adapter == IntPtr.Zero ? 0 : Read(_adapter).Select(x => x.Value).DefaultIfEmpty().Max();
            return (proc, adapter);
        }

        static List<(string Name, long Value)> Read(IntPtr counter)
        {
            var list = new List<(string, long)>();
            uint size = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out uint count, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return list;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out count, buf) != 0) return list;
                // PDH_FMT_COUNTERVALUE_ITEM_W: { wchar* name; { uint status; (pad); long value } } = 24 bytes on x64
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * 24;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    list.Add((name, Marshal.ReadInt64(item + 16)));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        public void Dispose() => PdhCloseQuery(_q);
    }

    /// <summary>Why measuring can't start here, or null when it can (without piped samples).</summary>
    public static string? Unavailable() =>
        Platform.IsWine ? "On Linux, measure with \"3 - Measure VRAM in game.sh\" in the Linux folder."
        : !OperatingSystem.IsWindows() ? "Live monitoring needs Windows." : null;

    /// <summary>
    /// A measuring session as it runs: read by the app window without blocking, and stopped by <see cref="Stop"/>
    /// (the console's Ctrl+C does the same).
    /// </summary>
    public sealed class Recorder
    {
        volatile bool _stop;
        long _game, _peak, _gpu, _ms;
        /// <summary>"waiting" for the game to start, then "measuring".</summary>
        public volatile string Phase = "waiting";
        public void Stop() => _stop = true;
        public bool Stopping => _stop;
        public long Game => Interlocked.Read(ref _game);
        public long Peak => Interlocked.Read(ref _peak);
        public long GpuTotal => Interlocked.Read(ref _gpu);
        public double Seconds => Interlocked.Read(ref _ms) / 1000.0;

        internal void Sample(TimeSpan elapsed, long game, long peak, long gpu)
        {
            Interlocked.Exchange(ref _game, game); Interlocked.Exchange(ref _peak, peak);
            Interlocked.Exchange(ref _gpu, gpu); Interlocked.Exchange(ref _ms, (long)elapsed.TotalMilliseconds);
        }
    }

    /// <param name="samplesIn">
    /// Linux: "3 - Measure VRAM in game.sh" waits for the game itself (Wine can't see Proton's processes) and pipes one
    /// "&lt;game VRAM bytes&gt; &lt;GPU total bytes&gt;" line per second; the session ends when the pipe closes.
    /// </param>
    public static int Run(string processName, string label, int seconds, string outDir, bool budgetArchiveActive, TextReader? samplesIn = null)
    {
        var rec = new Recorder();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; rec.Stop(); };
        if (samplesIn == null && Unavailable() is { } why) { Console.Error.WriteLine(why); return 2; }
        var session = Record(processName, label, seconds, outDir, budgetArchiveActive, samplesIn, rec, Console.WriteLine,
            (elapsed, proc, peak, adapter) =>
            {
                if (!Console.IsOutputRedirected)
                    Console.Write($"\r  {elapsed:hh\\:mm\\:ss}  game VRAM {Report.Size(proc),9}  peak {Report.Size(peak),9}  GPU total {Report.Size(adapter),9}   ");
            },
            out bool started, out var csvPath, out var history);
        if (!started) return 1;
        Console.WriteLine();
        if (session == null) { Console.WriteLine("No samples collected."); return 1; }

        Console.WriteLine($"Session \"{label}\": {session.Samples} samples, peak {Report.Size(session.Peak)}, 95th pct {Report.Size(session.P95)}, steady (last 60 s) {Report.Size(session.Steady)}");
        Console.WriteLine($"CSV: {Platform.Shown(csvPath!)}");
        PrintComparison(history);
        return 0;
    }

    /// <summary>
    /// Waits for the game (unless samples are piped in), then records until <paramref name="rec"/> is stopped, the game
    /// closes or <paramref name="seconds"/> pass. Writes the CSV and adds the session to VRAM-RipperDoc-Live.json.
    /// <paramref name="started"/> is false when it was stopped before the game started (nothing written then).
    /// Returns null when no samples came in.
    /// </summary>
    public static Session? Record(string processName, string label, int seconds, string outDir, bool budgetArchiveActive, TextReader? samplesIn,
        Recorder rec, Action<string> say, Action<TimeSpan, long, long, long>? onSample, out bool started, out string? csvPath, out List<Session> history)
    {
        started = false; csvPath = null; history = new();
        Query? q = null;
        Func<(long Process, long Adapter)?> next;
        string what;
        if (samplesIn != null)
        {
            what = $"{processName}.exe";
            next = () =>
            {
                while (samplesIn.ReadLine() is { } line)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[0], out var proc) && long.TryParse(parts[1], out var adapter)) return (proc, adapter);
                }
                return null;
            };
        }
        else
        {
            q = new Query();
            say($"Waiting for {processName}.exe ... (Ctrl+C to stop)");
            Process? p = null;
            while (!rec.Stopping && (p = Process.GetProcessesByName(processName).FirstOrDefault()) == null) Thread.Sleep(1000);
            if (p == null) { q.Dispose(); return null; }
            var game = p;
            what = $"{processName}.exe (pid {game.Id})";
            next = () =>
            {
                if (game.HasExited) return null;
                Thread.Sleep(1000);
                return q.Sample(Process.GetProcessesByName(processName).Select(x => x.Id).ToList());
            };
        }
        using var pdh = q;
        started = true;
        rec.Phase = "measuring";

        Directory.CreateDirectory(outDir);
        var start = DateTime.Now;
        csvPath = Path.Combine(outDir, $"VRAM-Live-{Sanitize(label)}-{start:yyyyMMdd-HHmmss}.csv");
        using var csv = new StreamWriter(csvPath);
        csv.WriteLine("seconds,process_dedicated_bytes,adapter_dedicated_bytes");
        var samples = new List<long>();
        long adapterPeak = 0, peak = 0;
        var sw = Stopwatch.StartNew();
        say($"Measuring {what} as \"{label}\"{(budgetArchiveActive ? " [budget archive active]" : "")}. Play normally; stop with Ctrl+C or by closing the game.");
        while (!rec.Stopping && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds) && next() is { } sample)
        {
            var (proc, adapter) = sample;
            if (proc <= 0) continue;
            samples.Add(proc);
            peak = Math.Max(peak, proc);
            adapterPeak = Math.Max(adapterPeak, adapter);
            csv.WriteLine($"{sw.Elapsed.TotalSeconds:0},{proc},{adapter}");
            rec.Sample(sw.Elapsed, proc, peak, adapter);
            onSample?.Invoke(sw.Elapsed, proc, peak, adapter);
        }
        if (samples.Count == 0) return null;

        var sorted = samples.OrderBy(x => x).ToList();
        var session = new Session(label, start, sw.Elapsed.TotalSeconds, sorted[^1], sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))],
            (long)samples.TakeLast(60).Average(), adapterPeak, budgetArchiveActive, samples.Count);
        history = LoadSessions(outDir);
        history.Add(session);
        SaveSessions(outDir, history);
        return session;
    }

    /// <summary>"Change vs "before": peak -1.25 GB, steady -1.13 GB" (b is the newer session).</summary>
    public static string Compare(Session a, Session b) =>
        $"Change vs \"{a.Label}\": peak {Report.Size(b.Peak - a.Peak)}, steady {Report.Size(b.Steady - a.Steady)}";

    public static void PrintComparison(List<Session> history)
    {
        if (history.Count < 2) { Console.WriteLine("Run again after changing mods (e.g. with --label after) to compare."); return; }
        Console.WriteLine();
        Console.WriteLine($"{"Session",-22} {"When",-17} {"Peak",10} {"95th pct",10} {"Steady",10}  Budget archive");
        foreach (var s in history.TakeLast(6))
            Console.WriteLine($"{s.Label,-22} {s.Start,-17:yyyy-MM-dd HH:mm} {Report.Size(s.Peak),10} {Report.Size(s.P95),10} {Report.Size(s.Steady),10}  {(s.BudgetArchive ? "yes" : "no")}");
        Console.WriteLine(Compare(history[^2], history[^1]) + " (load the same save and stand still for the fairest comparison)");
    }

    static string SessionsPath(string dir) => Path.Combine(dir, "VRAM-RipperDoc-Live.json");

    public static List<Session> LoadSessions(string dir)
    {
        try
        {
            var p = SessionsPath(dir);
            if (!File.Exists(p)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.EnumerateArray().Select(e => new Session(
                e.GetProperty("label").GetString() ?? "", e.GetProperty("start").GetDateTime(), e.GetProperty("seconds").GetDouble(),
                e.GetProperty("peak").GetInt64(), e.GetProperty("p95").GetInt64(), e.GetProperty("steady").GetInt64(),
                e.GetProperty("gpuTotalPeak").GetInt64(), e.GetProperty("budgetArchive").GetBoolean(), e.GetProperty("samples").GetInt32())).ToList();
        }
        catch { return new(); }
    }

    static void SaveSessions(string dir, List<Session> sessions)
    {
        using var fs = File.Create(SessionsPath(dir));
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartArray();
        foreach (var s in sessions)
        {
            w.WriteStartObject();
            WriteSession(w, s);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>A session's fields as VRAM-RipperDoc-Live.json stores them (the app window shows the same).</summary>
    public static void WriteSession(Utf8JsonWriter w, Session s)
    {
        w.WriteString("label", s.Label); w.WriteString("start", s.Start); w.WriteNumber("seconds", Math.Round(s.Seconds));
        w.WriteNumber("peak", s.Peak); w.WriteNumber("p95", s.P95); w.WriteNumber("steady", s.Steady);
        w.WriteNumber("gpuTotalPeak", s.GpuTotalPeak); w.WriteBoolean("budgetArchive", s.BudgetArchive); w.WriteNumber("samples", s.Samples);
    }

    static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
