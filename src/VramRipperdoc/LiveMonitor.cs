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

    public static int Run(string processName, string label, int seconds, string outDir, bool budgetArchiveActive)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Live monitoring needs Windows."); return 2; }
        using var q = new Query();
        bool stop = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };

        Console.WriteLine($"Waiting for {processName}.exe ... (Ctrl+C to stop)");
        Process? p = null;
        while (!stop && (p = Process.GetProcessesByName(processName).FirstOrDefault()) == null) Thread.Sleep(1000);
        if (p == null) return 1;

        Directory.CreateDirectory(outDir);
        var start = DateTime.Now;
        var csvPath = Path.Combine(outDir, $"VRAM-Live-{Sanitize(label)}-{start:yyyyMMdd-HHmmss}.csv");
        using var csv = new StreamWriter(csvPath);
        csv.WriteLine("seconds,process_dedicated_bytes,adapter_dedicated_bytes");
        var samples = new List<long>();
        long adapterPeak = 0;
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Measuring {processName}.exe (pid {p.Id}) as \"{label}\"{(budgetArchiveActive ? " [budget archive active]" : "")}. Play normally; stop with Ctrl+C or by closing the game.");
        while (!stop && !p.HasExited && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
        {
            Thread.Sleep(1000);
            var pids = Process.GetProcessesByName(processName).Select(x => x.Id).ToList();
            var (proc, adapter) = q.Sample(pids);
            if (proc <= 0) continue;
            samples.Add(proc);
            adapterPeak = Math.Max(adapterPeak, adapter);
            csv.WriteLine($"{sw.Elapsed.TotalSeconds:0},{proc},{adapter}");
            if (!Console.IsOutputRedirected)
                Console.Write($"\r  {sw.Elapsed:hh\\:mm\\:ss}  game VRAM {Report.Size(proc),9}  peak {Report.Size(samples.Max()),9}  GPU total {Report.Size(adapter),9}   ");
        }
        Console.WriteLine();
        if (samples.Count == 0) { Console.WriteLine("No samples collected."); return 1; }

        var sorted = samples.OrderBy(x => x).ToList();
        var session = new Session(label, start, sw.Elapsed.TotalSeconds, sorted[^1], sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))],
            (long)samples.TakeLast(60).Average(), adapterPeak, budgetArchiveActive, samples.Count);
        var history = LoadSessions(outDir);
        history.Add(session);
        SaveSessions(outDir, history);

        Console.WriteLine($"Session \"{label}\": {samples.Count} samples, peak {Report.Size(session.Peak)}, 95th pct {Report.Size(session.P95)}, steady (last 60 s) {Report.Size(session.Steady)}");
        Console.WriteLine($"CSV: {csvPath}");
        PrintComparison(history);
        return 0;
    }

    public static void PrintComparison(List<Session> history)
    {
        if (history.Count < 2) { Console.WriteLine("Run again after changing mods (e.g. with --label after) to compare."); return; }
        Console.WriteLine();
        Console.WriteLine($"{"Session",-22} {"When",-17} {"Peak",10} {"95th pct",10} {"Steady",10}  Budget archive");
        foreach (var s in history.TakeLast(6))
            Console.WriteLine($"{s.Label,-22} {s.Start,-17:yyyy-MM-dd HH:mm} {Report.Size(s.Peak),10} {Report.Size(s.P95),10} {Report.Size(s.Steady),10}  {(s.BudgetArchive ? "yes" : "no")}");
        var a = history[^2]; var b = history[^1];
        Console.WriteLine($"Change vs \"{a.Label}\": peak {Report.Size(b.Peak - a.Peak)}, steady {Report.Size(b.Steady - a.Steady)} (load the same save and stand still for the fairest comparison)");
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
            w.WriteString("label", s.Label); w.WriteString("start", s.Start); w.WriteNumber("seconds", Math.Round(s.Seconds));
            w.WriteNumber("peak", s.Peak); w.WriteNumber("p95", s.P95); w.WriteNumber("steady", s.Steady);
            w.WriteNumber("gpuTotalPeak", s.GpuTotalPeak); w.WriteBoolean("budgetArchive", s.BudgetArchive); w.WriteNumber("samples", s.Samples);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
