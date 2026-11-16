using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace VramRipperdoc;

/// <summary>
/// Developer self-test of the app window's server (--test-gui &lt;fixture mod root&gt; --game &lt;game&gt; [--out &lt;dir&gt;]):
/// starts it in this process on a free port and drives every call the page uses with HttpClient. Writes a fix into the
/// fixture's archive\pc\mod and removes it again, so point it at a copy of a fixture, never at the game.
/// </summary>
public static class GuiTest
{
    public static int Run(string modRoot, IReadOnlyDictionary<string, string?> cli)
    {
        int bad = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK " : "BAD")} {what}"); if (!ok) bad++; }
        var game = Setup.FindGame(cli.GetValueOrDefault("game"));
        if (game == null) { Console.WriteLine("BAD game not found (pass --game)"); return 1; }
        modRoot = Path.GetFullPath(modRoot);
        if (Setup.IsGame(modRoot)) { Console.WriteLine("BAD the mod root is a game folder; use a copy of a fixture"); return 1; }
        bool ownOut = cli.GetValueOrDefault("out") == null;
        var outDir = Path.GetFullPath(cli.GetValueOrDefault("out") ?? Path.Combine(Path.GetTempPath(), $"RipperDoc-GuiTest-{Environment.ProcessId}"));
        Directory.CreateDirectory(outDir);
        var ini = Path.Combine(outDir, "RipperDoc-GuiTest.ini");
        File.Delete(ini);
        BudgetArchive.Remove(modRoot, quiet: true); // a fix left over from an earlier run
        var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            { ["game"] = game, ["mod-root"] = modRoot, ["out"] = outDir, ["budget"] = cli.GetValueOrDefault("budget") ?? "0.25" };
        const string token = "0123456789abcdef0123456789abcdef";

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };
        using var streamHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        int port = 0;
        HttpResponseMessage Call(HttpMethod method, string path, string? json = null, bool withToken = true, string? host = null, byte[]? raw = null)
        {
            var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
            if (withToken) req.Headers.Add("X-RipperDoc-Token", token);
            if (host != null) req.Headers.Host = host;
            if (raw != null) req.Content = new ByteArrayContent(raw);
            else if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return http.Send(req);
        }
        JsonElement Body(HttpResponseMessage r) { using var d = JsonDocument.Parse(r.Content.ReadAsStringAsync().Result); return d.RootElement.Clone(); }
        JsonElement State() => Body(Call(HttpMethod.Get, "/api/state"));
        int Status(HttpResponseMessage r) => (int)r.StatusCode;
        JsonElement Post(string path, string json, out int status) { var r = Call(HttpMethod.Post, path, json); status = Status(r); return Body(r); }
        JsonElement WaitIdle()
        {
            var until = DateTime.UtcNow.AddMinutes(10);
            while (true)
            {
                var s = State();
                if (s.GetProperty("busy").ValueKind == JsonValueKind.Null || DateTime.UtcNow > until) return s;
                Thread.Sleep(300);
            }
        }
        (ConcurrentQueue<JsonElement> Jobs, ConcurrentQueue<string> Names, CancellationTokenSource Stop) Listen()
        {
            var jobs = new ConcurrentQueue<JsonElement>(); var names = new ConcurrentQueue<string>(); var cts = new CancellationTokenSource();
            var connected = new ManualResetEventSlim();
            Task.Run(async () =>
            {
                try
                {
                    using var resp = await streamHttp.GetAsync($"http://127.0.0.1:{port}/api/events?t={token}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    using var sr = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
                    connected.Set();
                    string? ev = null;
                    while (await sr.ReadLineAsync(cts.Token) is { } line)
                    {
                        if (line.StartsWith("event: ")) { ev = line[7..]; names.Enqueue(ev); }
                        else if (line.StartsWith("data: ") && ev == "job") { using var d = JsonDocument.Parse(line[6..]); jobs.Enqueue(d.RootElement.Clone()); }
                    }
                }
                catch { }
                finally { connected.Set(); }
            });
            connected.Wait(10_000);
            return (jobs, names, cts);
        }
        (GuiSession Session, GuiServer Server, Task Done) Start()
        {
            var session = new GuiSession(opts, ini);
            var server = new GuiServer(session, 0, token);
            port = server.Port;
            return (session, server, Task.Run(() => GuiApp.WaitUntilDone(session, server)));
        }
        bool Quits(Task done)
        {
            int status = Status(Call(HttpMethod.Post, "/api/quit", "{}"));
            bool stopped = status == 200 && done.Wait(10_000);
            bool refused;
            try { using var r = Call(HttpMethod.Get, "/api/state"); refused = false; } catch (HttpRequestException) { refused = true; }
            return stopped && refused;
        }
        string Plan(string newItems) => $"{{\"normal\": true, \"newItems\": \"{newItems}\"}}";
        static bool Has(JsonElement perMod, string mod) => perMod.EnumerateArray().Any(m => m.GetProperty("mod").GetString() == mod);

        try
        {
            // Mod names can hold almost anything; RipperDoc.ini must give them back unchanged.
            var odd = new List<string> { "HD_Textures", "A|B", "100% Better", " leading space", "trailing space ", "tab\there", "Ünïcödé 赛博朋克", "%7C literal", "=equals=" };
            KeepSharp.Save(ini, odd);
            Setup.WriteSetting(ini, "game", "x"); // other settings written later don't disturb it
            Check(KeepSharp.Load(ini).SequenceEqual(odd), $"keep-sharp names survive RipperDoc.ini: {File.ReadAllLines(ini).First(l => l.StartsWith("keepsharp="))}");
            File.Delete(ini);

            // ---- First session: page, rules, scan, plans, keep-sharp, live, quit.
            var a = Start();
            var page = http.Send(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/?t={token}"));
            Check(Status(page) == 200 && page.Content.Headers.ContentType?.MediaType == "text/html" && page.Content.ReadAsStringAsync().Result.Contains("<"),
                $"page served with the token (port {port})");
            Check(Status(http.Send(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/"))) == 403, "page refused without the token");
            Check(Status(http.Send(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/?t=wrong"))) == 403, "page refused with a wrong token");
            Check(Status(Call(HttpMethod.Get, "/api/state", host: $"attacker.example:{port}")) == 403, "wrong Host refused");
            Check(Status(Call(HttpMethod.Get, "/api/state", withToken: false)) == 403, "API refused without the token");
            Check(Status(Call(HttpMethod.Get, "/api/events?t=wrong", withToken: false)) == 403, "event stream refused with a wrong token");
            Check(Status(Call(HttpMethod.Get, "/api/nothing")) == 404 && Status(Call(HttpMethod.Get, "/api/scan")) == 405, "unknown call 404, wrong method 405");
            Check(Status(Call(HttpMethod.Post, "/api/keep-sharp", raw: new byte[(1 << 20) + 1])) == 413, "body over 1 MB refused");
            Check(Status(Call(HttpMethod.Post, "/api/plan", "not json")) == 400, "bad JSON refused");

            var s = State();
            Check(s.GetProperty("version").GetString() == Report.Version && s.GetProperty("game").GetProperty("found").GetBoolean()
                && s.GetProperty("busy").ValueKind == JsonValueKind.Null && s.GetProperty("report").ValueKind == JsonValueKind.Null,
                $"state: v{s.GetProperty("version").GetString()}, game {s.GetProperty("game").GetProperty("path").GetString()}, idle, no report yet");
            Post("/api/plan", Plan("none"), out int st);
            Check(st == 400, "plan before a scan: 400");

            var ev = Listen();
            Check(Status(Call(HttpMethod.Post, "/api/scan", "{}")) == 202, "scan started (202)");
            Check(Status(Call(HttpMethod.Post, "/api/scan", "{}")) == 409, "second scan while one runs: 409");
            // As the page does it: fetch the state on each "changed" event until the job is gone.
            bool byEvents = false;
            for (int seen = 0; !byEvents && seen < 10_000;)
            {
                var names = ev.Names.ToArray();
                if (seen == names.Length) { Thread.Sleep(100); if (a.Done.IsCompleted) break; continue; }
                if (names[seen++] != "changed") continue;
                var now = State();
                byEvents = now.GetProperty("busy").ValueKind == JsonValueKind.Null && now.GetProperty("lastJob").ValueKind == JsonValueKind.Object
                    && now.GetProperty("lastJob").GetProperty("kind").GetString() == "scan";
            }
            Check(byEvents, "scan finished (seen through the event stream)");
            s = WaitIdle();
            var last = s.GetProperty("lastJob");
            Check(last.GetProperty("kind").GetString() == "scan" && last.GetProperty("ok").GetBoolean(), $"scan finished (polling state): {last.GetProperty("message").GetString()}");
            var phases = ev.Jobs.Select(j => j.GetProperty("phase").GetString()).Distinct().ToList();
            Check(ev.Jobs.Any(j => j.GetProperty("kind").GetString() == "scan") && ev.Names.Contains("changed"),
                $"event stream: {ev.Jobs.Count} job event(s) (phases {string.Join(", ", phases)}), {ev.Names.Count(n => n == "changed")} changed");
            var report = s.GetProperty("report");
            int mods = report.ValueKind == JsonValueKind.Object ? report.GetProperty("mods").GetArrayLength() : 0;
            Check(mods > 0 && last.GetProperty("details").GetProperty("mods").GetInt32() == mods, $"report present with {mods} mods");
            Check(File.Exists(Path.Combine(outDir, "VRAM-RipperDoc-Report.html")) && File.Exists(Path.Combine(outDir, "VRAM-RipperDoc-Report.json"))
                && File.Exists(Path.Combine(outDir, "VRAM-RipperDoc-Summary.md")) && Directory.GetFiles(Path.Combine(outDir, "history")).Length > 0,
                "report files and history written to the out folder");

            var none = Post("/api/plan", Plan("none"), out int s0);
            var p2k = Post("/api/plan", Plan("2k"), out int s2);
            var p1k = Post("/api/plan", Plan("1k"), out int s1);
            int Count(JsonElement p, string part) => p.GetProperty(part).GetProperty("count").GetInt32();
            Check(s0 == 200 && s2 == 200 && s1 == 200, "plan for newItems none, 2k, 1k");
            Check(Count(none, "chosen") == Count(none, "normal") && Count(none, "chosen") == Count(p2k, "normal"),
                $"plan none: {Count(none, "chosen")} texture(s), normal play only (budget {none.GetProperty("budget").GetInt64() >> 20} MB, mods add {none.GetProperty("current").GetInt64() >> 20} MB)");
            Check(Count(p2k, "chosen") == Count(p2k, "normal") + Count(p2k, "newItems2k"), $"plan 2k: {Count(p2k, "chosen")} = normal + {Count(p2k, "newItems2k")} new items");
            Check(Count(p1k, "chosen") == Count(p1k, "normal") + Count(p1k, "newItems1k") && Count(p1k, "newItems1k") >= Count(p1k, "newItems2k"),
                $"plan 1k: {Count(p1k, "chosen")} = normal + {Count(p1k, "newItems1k")} new items (1K takes at least as many as 2K)");
            Post("/api/plan", "{\"newItems\": \"4k\"}", out st);
            Check(st == 400, "plan with newItems 4k refused");

            var perMod = p1k.GetProperty("chosen").GetProperty("perMod");
            var keep = perMod.EnumerateArray().First().GetProperty("mod").GetString()!;
            s = Post("/api/keep-sharp", JsonSerializerFree(keep), out st);
            Check(st == 200 && s.GetProperty("keepSharp").EnumerateArray().Any(k => k.GetString() == keep), $"keep sharp set: \"{keep}\"");
            var kept = Post("/api/plan", Plan("1k"), out _);
            Check(!Has(kept.GetProperty("chosen").GetProperty("perMod"), keep) && kept.GetProperty("keptSharp").GetProperty("mods").GetInt32() == 1
                && kept.GetProperty("keptSharp").GetProperty("textures").GetInt32() > 0 && Count(kept, "chosen") < Count(p1k, "chosen"),
                $"keep sharp removes \"{keep}\" from the plan ({Count(p1k, "chosen")} -> {Count(kept, "chosen")} textures, {kept.GetProperty("keptSharp").GetProperty("textures").GetInt32()} kept sharp)");
            var keptReport = State().GetProperty("report");
            var keptArchives = keptReport.GetProperty("mods").EnumerateArray()
                .Where(m => (m.TryGetProperty("package", out var pk) ? pk.GetString() : m.GetProperty("name").GetString()) == keep)
                .Select(m => m.GetProperty("name").GetString()).ToHashSet();
            Check(keptArchives.Count > 0 && !keptReport.GetProperty("budget").GetProperty("steps").EnumerateArray().Any(x => keptArchives.Contains(x.GetProperty("mod").GetString())),
                $"report's plan follows keep sharp ({keptArchives.Count} archive(s) of \"{keep}\" left out)");

            if (s.GetProperty("live").GetProperty("available").GetBoolean())
            {
                Check(Status(Call(HttpMethod.Post, "/api/live/start", "{\"label\": \"guitest\"}")) == 202, "live measuring started");
                var busy = State().GetProperty("busy");
                Check(busy.ValueKind == JsonValueKind.Object && busy.GetProperty("kind").GetString() == "live" && busy.GetProperty("canStop").GetBoolean()
                    && busy.GetProperty("phase").GetString() == "waiting", "live job waits for the game and can be stopped");
                Check(Status(Call(HttpMethod.Post, "/api/live/stop", "{}")) == 200, "live stop answered");
                last = WaitIdle().GetProperty("lastJob");
                Check(last.GetProperty("kind").GetString() == "live" && !last.GetProperty("ok").GetBoolean(), $"live stopped before the game started: {last.GetProperty("message").GetString()}");
            }
            ev.Stop.Cancel();
            Check(Quits(a.Done), "quit stops the first server");

            // ---- Second session object, same RipperDoc.ini: keep-sharp survives; fix, rescan, undo.
            var b = Start();
            ev = Listen();
            s = State();
            Check(s.GetProperty("keepSharp").EnumerateArray().Any(k => k.GetString() == keep), "keep sharp survives a restart of the session");
            Call(HttpMethod.Post, "/api/scan", "{}");
            last = WaitIdle().GetProperty("lastJob");
            Check(last.GetProperty("kind").GetString() == "scan" && last.GetProperty("ok").GetBoolean(), "scan in the new session");
            var plan = Post("/api/plan", Plan("1k"), out _);
            Check(!Has(plan.GetProperty("chosen").GetProperty("perMod"), keep) && Count(plan, "chosen") == Count(kept, "chosen"), "plan after the restart still keeps it sharp");

            Check(Status(Call(HttpMethod.Post, "/api/fix", Plan("1k"))) == 202, $"fix started ({Count(plan, "chosen")} textures)");
            Check(Status(Call(HttpMethod.Post, "/api/scan", "{}")) == 409 && Status(Call(HttpMethod.Post, "/api/undo", "{}")) == 409, "scan and undo while the fix runs: 409");
            s = WaitIdle();
            last = s.GetProperty("lastJob");
            var details = last.GetProperty("details");
            var fixPath = details.TryGetProperty("path", out var fp) ? fp.GetString() : null;
            Check(last.GetProperty("kind").GetString() == "fix" && last.GetProperty("ok").GetBoolean() && fixPath != null && File.Exists(fixPath)
                && details.GetProperty("textures").GetInt32() > 0 && details.GetProperty("nextSteps").GetArrayLength() > 0,
                $"fix finished: {last.GetProperty("message").GetString()}");
            Check(fixPath != null && Path.GetDirectoryName(fixPath)!.Equals(Path.Combine(modRoot, "archive", "pc", "mod"), StringComparison.OrdinalIgnoreCase)
                && s.GetProperty("fix").GetProperty("found").GetBoolean(), "fix file is in the mod root's archive\\pc\\mod");
            Check(s.GetProperty("report").TryGetProperty("budgetArchive", out _), "report shows the fix right after it was made");
            Check(ev.Jobs.Any(j => j.GetProperty("kind").GetString() == "fix"), $"event stream showed the fix job ({string.Join(", ", ev.Jobs.Where(j => j.GetProperty("kind").GetString() == "fix").Select(j => j.GetProperty("phase").GetString()).Distinct())})");

            Call(HttpMethod.Post, "/api/scan", "{}");
            report = WaitIdle().GetProperty("report");
            Check(report.TryGetProperty("budgetArchive", out var ba) && ba.GetString() == Path.GetFileName(fixPath)
                && report.TryGetProperty("budgetFix", out var bf) && !bf.GetProperty("stale").GetBoolean(), "second scan: fix active and not stale");

            var undo = Post("/api/undo", "{}", out st);
            Check(st == 200 && undo.GetProperty("removed").EnumerateArray().Any(r => r.GetString() == fixPath) && !File.Exists(fixPath),
                $"undo removed the fix: {undo.GetProperty("message").GetString()}");
            s = State();
            Check(!s.GetProperty("report").TryGetProperty("budgetArchive", out _) && !s.GetProperty("fix").GetProperty("found").GetBoolean()
                && s.GetProperty("lastJob").GetProperty("kind").GetString() == "undo", "state after undo: no fix");
            ev.Stop.Cancel();
            Check(Quits(b.Done), "quit stops the second server");
        }
        catch (Exception ex) { Check(false, $"crashed: {ex}"); }
        finally
        {
            BudgetArchive.Remove(modRoot, quiet: true);
            if (ownOut) try { Directory.Delete(outDir, true); } catch { }
        }
        Console.WriteLine(bad == 0 ? "All GUI server checks passed." : $"{bad} GUI server check(s) failed.");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>{"mods": [name]} without the reflection-based serializer (the exe is trimmed).</summary>
    static string JsonSerializerFree(string mod) => Encoding.UTF8.GetString(GuiSession.Json(w =>
    {
        w.WriteStartObject(); w.WriteStartArray("mods"); w.WriteStringValue(mod); w.WriteEndArray(); w.WriteEndObject();
    }));
}
