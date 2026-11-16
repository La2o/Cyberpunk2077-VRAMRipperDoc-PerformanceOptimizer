using System.Diagnostics;
using System.Text.Json;

namespace VramRipperdoc;

/// <summary>
/// The exe's side of the app window (docs/gui-api.md): what the page shows (state), the jobs it can start (one at a time,
/// on a background thread) and every /api call. The HTTP part is <see cref="GuiServer"/>; the work itself is the same
/// code the command line runs (<see cref="Setup"/>, <see cref="Fix"/>, <see cref="LiveMonitor"/>).
/// </summary>
public sealed class GuiSession
{
    public sealed record Answer(int Status, byte[] Body);

    /// <summary>A running job. The page reads it often, so its fields are plain values the job thread keeps up to date.</summary>
    sealed class Job
    {
        public required string Kind;
        public volatile string Phase = "";
        public volatile string Message = "";
        public int Done, Total;
        public readonly DateTime StartedAt = DateTime.UtcNow;
        public double? EtaSeconds;
        public LiveMonitor.Recorder? Live;
        /// <summary>Brings the fields up to date from the work's own counters (fix status, live recorder) before they're read.</summary>
        public volatile Action? Refresh;
    }

    sealed record Finished(string Kind, bool Ok, string Message, DateTime At, Action<Utf8JsonWriter>? Details = null);

    readonly object _gate = new();
    readonly IReadOnlyDictionary<string, string?> _opts;
    readonly string _settingsFile;
    string? _game, _mo2;
    List<string> _keepSharp;
    ScanResult? _result;
    long _budget;
    byte[]? _report;
    Job? _busy;
    bool _undoing;
    Finished? _last;
    int _version;
    volatile bool _quit;
    volatile bool _windowClosing;

    public string OutDir { get; }
    public bool QuitRequested => _quit;
    /// <summary>The page said its window is going away. A reload says the same, so the event stream coming back clears it.</summary>
    public bool WindowClosing { get => _windowClosing; set => _windowClosing = value; }

    /// <param name="settingsFile">RipperDoc.ini (a different one for the self-test).</param>
    public GuiSession(IReadOnlyDictionary<string, string?> opts, string? settingsFile = null)
    {
        _opts = opts;
        _settingsFile = settingsFile ?? Setup.SettingsFile;
        OutDir = opts.GetValueOrDefault("out") ?? AppContext.BaseDirectory;
        _game = Setup.FindGame(opts.GetValueOrDefault("game"), Setup.ReadSetting(_settingsFile, "game"));
        _mo2 = opts.GetValueOrDefault("mo2") ?? Setup.ReadSetting(_settingsFile, "mo2");
        _keepSharp = KeepSharp.Load(_settingsFile);
    }

    string? ModRootOf(string? game) => _opts.GetValueOrDefault("mod-root") ?? game;

    /// <summary>Goes up whenever the state changes in a way the page should fetch again.</summary>
    public int Version { get { lock (_gate) return _version; } }
    public bool Busy { get { lock (_gate) return _busy != null || _undoing; } }

    void Changed() { lock (_gate) { _version++; Monitor.PulseAll(_gate); } }

    /// <summary>Waits until the state changes (or the time is up), for the event stream.</summary>
    public void WaitForChange(int seen, int milliseconds) { lock (_gate) if (_version == seen) Monitor.Wait(_gate, milliseconds); }

    /// <summary>The running job as JSON, or null when nothing runs.</summary>
    public byte[]? BusyJson()
    {
        Job? j;
        lock (_gate) j = _busy;
        return j == null ? null : Json(w => WriteJob(w, j));
    }

    public Answer Handle(string method, string path, byte[] body)
    {
        JsonElement? doc = null;
        if (method == "POST" && body.Length > 0)
            try { using var d = JsonDocument.Parse(body); doc = d.RootElement.Clone(); }
            catch (JsonException) { return Error(400, "That request couldn't be read (it isn't valid JSON)."); }
        var routes = new Dictionary<string, (string Method, Func<Answer> Run)>
        {
            ["/api/state"] = ("GET", () => new Answer(200, StateJson())),
            ["/api/game"] = ("POST", () => SetGame(doc)),
            ["/api/mo2"] = ("POST", () => SetMo2(doc)),
            ["/api/keep-sharp"] = ("POST", () => SetKeepSharp(doc)),
            ["/api/scan"] = ("POST", StartScan),
            ["/api/plan"] = ("POST", () => Plan(doc)),
            ["/api/fix"] = ("POST", () => StartFix(doc)),
            ["/api/undo"] = ("POST", Undo),
            ["/api/live/start"] = ("POST", () => StartLive(doc)),
            ["/api/live/stop"] = ("POST", StopLive),
            ["/api/open"] = ("POST", () => Open(doc)),
            ["/api/quit"] = ("POST", Quit),
            ["/api/bye"] = ("POST", () => { _windowClosing = true; return Ok(_ => { }); }),
        };
        if (!routes.TryGetValue(path, out var route)) return Error(404, "There's no such call.");
        if (route.Method != method) return Error(405, $"Use {route.Method} for {path}.");
        return route.Run();
    }

    // ---- State ---------------------------------------------------------------------------------------------------

    byte[] StateJson()
    {
        // File and process lookups first, outside the lock, so a slow disk never holds up a running job.
        string? game, mo2;
        lock (_gate) { game = _game; mo2 = _mo2; }
        var detected = ModManagers.AllMo2Instances();
        bool underMo2 = Setup.UnderMo2();
        var sessions = LiveMonitor.LoadSessions(OutDir);
        var why = LiveMonitor.Unavailable();
        var root = ModRootOf(game);
        var fix = root != null ? BudgetArchive.FindExisting(root, Setup.FixDirs(_settingsFile, mo2)) : null;
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("version", Report.Version);
            w.WriteString("platform", Platform.IsWine ? "wine" : "windows");
            w.WriteString("toolDir", Platform.Shown(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)));
            w.WriteString("outDir", Platform.Shown(Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutDir))));
            lock (_gate)
            {
                w.WriteStartObject("game");
                WritePath(w, "path", _game);
                w.WriteBoolean("found", _game != null);
                w.WriteEndObject();
                w.WriteStartObject("mo2");
                WritePath(w, "instance", _mo2);
                w.WriteStartArray("detected"); foreach (var d in detected) w.WriteStringValue(Platform.Shown(d)); w.WriteEndArray();
                w.WriteBoolean("underMo2", underMo2);
                w.WriteEndObject();
                w.WritePropertyName("busy");
                if (_busy != null) WriteJob(w, _busy); else w.WriteNullValue();
                w.WritePropertyName("lastJob");
                if (_last is { } f)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", f.Kind); w.WriteBoolean("ok", f.Ok); w.WriteString("message", f.Message);
                    w.WriteString("finishedAt", f.At.ToString("o"));
                    w.WriteStartObject("details"); f.Details?.Invoke(w); w.WriteEndObject();
                    w.WriteEndObject();
                }
                else w.WriteNullValue();
                w.WritePropertyName("report");
                if (_report != null) w.WriteRawValue(_report, skipInputValidation: true); else w.WriteNullValue();
                w.WriteStartArray("keepSharp"); foreach (var k in _keepSharp) w.WriteStringValue(k); w.WriteEndArray();
            }
            w.WriteStartObject("fix");
            WritePath(w, "path", fix);
            w.WriteBoolean("found", fix != null);
            w.WriteEndObject();
            w.WriteStartObject("live");
            w.WriteBoolean("available", why == null);
            w.WriteString("why", why ?? "");
            w.WriteStartArray("sessions");
            foreach (var s in sessions) { w.WriteStartObject(); LiveMonitor.WriteSession(w, s); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    static void WriteJob(Utf8JsonWriter w, Job j)
    {
        j.Refresh?.Invoke();
        w.WriteStartObject();
        w.WriteString("kind", j.Kind);
        w.WriteString("phase", j.Phase);
        w.WriteString("message", j.Message);
        w.WriteNumber("done", Volatile.Read(ref j.Done)); w.WriteNumber("total", Volatile.Read(ref j.Total));
        w.WriteString("startedAt", j.StartedAt.ToString("o"));
        if (j.EtaSeconds is { } eta) w.WriteNumber("etaSeconds", Math.Ceiling(eta)); else w.WriteNull("etaSeconds");
        w.WriteBoolean("canStop", j.Live != null);
        if (j.Live is { } live)
        {
            w.WriteStartObject("live");
            w.WriteNumber("seconds", Math.Round(live.Seconds)); w.WriteNumber("game", live.Game);
            w.WriteNumber("peak", live.Peak); w.WriteNumber("gpuTotal", live.GpuTotal);
            w.WriteEndObject();
        }
        else w.WriteNull("live");
        w.WriteEndObject();
    }

    static void WritePath(Utf8JsonWriter w, string name, string? path)
    {
        if (path != null) w.WriteString(name, Platform.Shown(path)); else w.WriteNull(name);
    }

    /// <summary>The report as the page gets it: Report.WriteJson, at most 300 files per mod like the HTML report.</summary>
    static byte[] ReportJson(ScanResult r, BudgetPlan plan)
    {
        using var ms = new MemoryStream();
        Report.WriteJson(r, plan, ms, maxAssetsPerMod: 300);
        return ms.ToArray();
    }

    /// <summary>After keep-sharp, a fix or an Undo: the report's plan and fix status change with them. Call with the lock held.</summary>
    void RefreshReport()
    {
        if (_result == null) return;
        _report = ReportJson(_result, BudgetPlan.Build(_result, _budget, keepSharp: KeepSharp.Set(_keepSharp)));
    }

    // ---- Folders and keep-sharp ----------------------------------------------------------------------------------

    static string? Str(JsonElement? doc, string name) =>
        doc is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    Answer SetGame(JsonElement? doc)
    {
        var picked = Platform.FromUser(Str(doc, "path"));
        if (string.IsNullOrEmpty(picked)) return Error(400, "Pick your Cyberpunk 2077 folder.");
        var dir = Setup.GameFrom(picked);
        if (dir == null) return Error(400, Setup.NotTheGame);
        lock (_gate)
        {
            if (_busy != null || _undoing) return Busy409();
            Setup.WriteSetting(_settingsFile, "game", dir);
            if (!string.Equals(_game, dir, StringComparison.OrdinalIgnoreCase)) { _result = null; _report = null; } // that scan was of another game
            _game = dir;
        }
        Changed();
        return new Answer(200, StateJson());
    }

    Answer SetMo2(JsonElement? doc)
    {
        string? dir = null;
        if (doc is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("path", out var v) && v.ValueKind != JsonValueKind.Null)
        {
            dir = Platform.FromUser(v.ValueKind == JsonValueKind.String ? v.GetString() : null);
            if (string.IsNullOrEmpty(dir)) return Error(400, "Pick the Mod Organizer 2 folder (the one with ModOrganizer.ini).");
            if (File.Exists(dir) && Path.GetFileName(dir).Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
            if (!File.Exists(Path.Combine(dir, "ModOrganizer.ini")))
                return Error(400, "That folder has no ModOrganizer.ini. Pick the Mod Organizer 2 folder that contains it.");
            dir = Path.GetFullPath(dir);
        }
        lock (_gate)
        {
            if (_busy != null || _undoing) return Busy409();
            Setup.WriteSetting(_settingsFile, "mo2", dir ?? "");
            _mo2 = dir;
        }
        Changed();
        return new Answer(200, StateJson());
    }

    Answer SetKeepSharp(JsonElement? doc)
    {
        if (doc is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array
            || mods.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.String))
            return Error(400, "Send the mods to keep sharp as {\"mods\": [\"name\", ...]}.");
        var list = KeepSharp.Clean(mods.EnumerateArray().Select(m => m.GetString()));
        lock (_gate)
        {
            KeepSharp.Save(_settingsFile, list);
            _keepSharp = list;
            RefreshReport();
        }
        Changed();
        return new Answer(200, StateJson());
    }

    // ---- Jobs ----------------------------------------------------------------------------------------------------

    /// <summary>Starts <paramref name="work"/> on its own thread unless a job runs. A failure ends as lastJob.ok=false, never as a dead server.</summary>
    bool TryStart(Job job, Func<Finished> work, string failed)
    {
        lock (_gate)
        {
            if (_busy != null || _undoing) return false;
            _busy = job;
        }
        Changed();
        new Thread(() =>
        {
            Finished f;
            try { f = work(); }
            catch (Exception ex)
            {
                f = new Finished(job.Kind, false, $"{failed}: {ex.Message}", DateTime.UtcNow);
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "VRAM-RipperDoc-Crash.log"), $"{DateTime.Now}: {job.Kind} failed: {ex}{Environment.NewLine}"); } catch { }
            }
            job.Refresh = null;
            lock (_gate) { _busy = null; _last = f; }
            Changed();
        }) { IsBackground = true, Name = "RipperDoc " + job.Kind }.Start();
        return true;
    }

    Answer StartScan()
    {
        string? game;
        lock (_gate) game = _game;
        if (game == null) return Error(400, "Pick your Cyberpunk 2077 folder first.");
        var job = new Job { Kind = "scan", Phase = "vanilla", Message = "Reading the game's own file list..." };
        return TryStart(job, () => RunScan(job), "Scan failed") ? Accepted() : Busy409();
    }

    Finished RunScan(Job job)
    {
        var sw = Stopwatch.StartNew();
        string game; string? mo2; HashSet<string> keep;
        lock (_gate) { game = _game!; mo2 = _mo2; keep = KeepSharp.Set(_keepSharp); }
        Stopwatch? scanning = null;
        job.Refresh = () =>
        {
            int done = Volatile.Read(ref job.Done), total = Volatile.Read(ref job.Total);
            if (job.Phase == "scanning") job.Message = $"Scanning mods: {done:N0} / {total:N0}";
            job.EtaSeconds = scanning != null ? Fix.Eta(scanning.Elapsed, done, total)?.TotalSeconds : null;
        };
        var result = new Scanner(new ScanOptions
        {
            GameDir = game,
            ModRoot = _opts.GetValueOrDefault("mod-root"),
            HashList = _opts.GetValueOrDefault("hashes"),
            Mo2Instance = mo2,
            CompareVanilla = !_opts.ContainsKey("no-vanilla"),
            Phase = (phase, total) =>
            {
                if (phase == "scanning") scanning = Stopwatch.StartNew();
                Volatile.Write(ref job.Total, total);
                job.Phase = phase;
            },
            Progress = (done, total) =>
            {
                // Archives finish in parallel, so counts can arrive out of order.
                for (int cur; (cur = Volatile.Read(ref job.Done)) < done && Interlocked.CompareExchange(ref job.Done, done, cur) != cur;) { }
                Volatile.Write(ref job.Total, total);
            },
        }).Scan();

        long budget = Setup.Budget(result, _opts);
        Setup.CheckFix(result, ModRootOf(game)!, Setup.FixDirs(_settingsFile, mo2));
        var plan = BudgetPlan.Build(result, budget, keepSharp: keep);
        string? notSaved = null;
        try
        {
            Directory.CreateDirectory(OutDir);
            result.HistoryDiff = History.SaveAndDiff(result, OutDir);
            Report.WriteFiles(result, plan, OutDir);
        }
        catch (Exception ex) { notSaved = $" The report files couldn't be saved in {Platform.Shown(OutDir)} ({ex.Message})."; } // the window still shows it
        lock (_gate)
        {
            _result = result; _budget = budget;
            RefreshReport();
        }
        double seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        string message = $"Scanned {result.Mods.Count:N0} mod archive(s) in {seconds:0.0} s.";
        if (result.Mods.Count == 0 && mo2 == null && _opts.GetValueOrDefault("mod-root") == null)
            message = "No mods were found in your game folder. If you use Mod Organizer 2, pick its folder and scan again.";
        message += notSaved;
        return new Finished("scan", true, message, DateTime.UtcNow, w => { w.WriteNumber("mods", result.Mods.Count); w.WriteNumber("seconds", seconds); });
    }

    /// <summary>FixOptions: { "normal": true, "newItems": "none" | "2k" | "1k" }. newMax 0 = leave new items alone.</summary>
    static string? ReadOptions(JsonElement? doc, out bool normal, out int newMax)
    {
        normal = true; newMax = 0;
        if (doc is not { ValueKind: JsonValueKind.Object } o) return doc == null ? null : "Send the choices as {\"normal\": true, \"newItems\": \"none\"}.";
        if (o.TryGetProperty("normal", out var n))
        {
            if (n.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "\"normal\" must be true or false.";
            normal = n.GetBoolean();
        }
        if (o.TryGetProperty("newItems", out var ni))
        {
            switch (ni.ValueKind == JsonValueKind.String ? ni.GetString()?.ToLowerInvariant() : null)
            {
                case "none": break;
                case "2k": newMax = 2048; break;
                case "1k": newMax = 1024; break;
                default: return "\"newItems\" must be \"none\", \"2k\" or \"1k\".";
            }
        }
        return null;
    }

    sealed record Choice(BudgetPlan Normal, List<PlanStep> To2k, List<PlanStep> To1k, List<PlanStep> Chosen, int KeptMods, int KeptTextures);

    /// <summary>The same choices the console's "2 - Apply budget fix" asks about, for these FixOptions.</summary>
    static Choice Choose(ScanResult r, long budget, HashSet<string> keep, bool normal, int newMax)
    {
        var plan = BudgetPlan.Build(r, budget, includeNew: false, keepSharp: keep);
        var p2k = BudgetPlan.Build(r, budget, includeNew: true, 2048, keep);
        var p1k = BudgetPlan.Build(r, budget, includeNew: true, 1024, keep);
        static List<PlanStep> NewItems(BudgetPlan p) => p.Steps.Where(s => !s.Asset.AlwaysLoaded).ToList();
        var to2k = NewItems(p2k);
        var to1k = NewItems(p1k);
        var chosen = (normal ? plan.Steps : new()).Concat(newMax == 2048 ? to2k : newMax == 1024 ? to1k : new()).ToList();
        var kept = (normal ? plan.KeptSharp : new())
            .Concat(newMax == 0 ? new() : (newMax == 2048 ? p2k : p1k).KeptSharp.Where(k => !k.Asset.AlwaysLoaded).ToList()).ToList();
        return new Choice(plan, to2k, to1k, chosen,
            kept.Select(k => KeepSharp.NameOf(k.Mod)).Distinct(StringComparer.OrdinalIgnoreCase).Count(), kept.Select(k => k.Asset).Distinct().Count());
    }

    bool Snapshot(out ScanResult r, out long budget, out HashSet<string> keep)
    {
        lock (_gate)
        {
            r = _result!; budget = _budget; keep = KeepSharp.Set(_keepSharp);
            return _result != null;
        }
    }

    Answer Plan(JsonElement? doc)
    {
        if (ReadOptions(doc, out bool normal, out int newMax) is { } bad) return Error(400, bad);
        if (!Snapshot(out var r, out long budget, out var keep)) return Error(400, "Scan your mods first.");
        var c = Choose(r, budget, keep, normal, newMax);
        static void Count(Utf8JsonWriter w, string name, List<PlanStep> steps)
        {
            w.WriteStartObject(name); w.WriteNumber("count", steps.Count); w.WriteNumber("saves", steps.Sum(s => s.Saves)); w.WriteEndObject();
        }
        return Ok(w =>
        {
            w.WriteNumber("budget", c.Normal.Budget); w.WriteNumber("current", c.Normal.Current); w.WriteNumber("afterPlan", c.Normal.AfterPlan);
            w.WriteBoolean("fits", c.Normal.Reachable);
            Count(w, "normal", c.Normal.Steps);
            Count(w, "newItems2k", c.To2k);
            Count(w, "newItems1k", c.To1k);
            w.WriteStartObject("chosen");
            w.WriteNumber("count", c.Chosen.Count); w.WriteNumber("saves", c.Chosen.Sum(s => s.Saves));
            w.WriteNumber("writeBytes", BudgetArchive.EstimateBytes(c.Chosen)); w.WriteNumber("seconds", Math.Ceiling(Fix.Seconds(c.Chosen)));
            w.WriteStartArray("perMod");
            foreach (var g in c.Chosen.GroupBy(s => KeepSharp.NameOf(s.Mod), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(s => s.Saves)))
            {
                w.WriteStartObject(); w.WriteString("mod", g.Key); w.WriteNumber("count", g.Count()); w.WriteNumber("saves", g.Sum(s => s.Saves)); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("keptSharp"); w.WriteNumber("mods", c.KeptMods); w.WriteNumber("textures", c.KeptTextures); w.WriteEndObject();
        });
    }

    Answer StartFix(JsonElement? doc)
    {
        if (ReadOptions(doc, out bool normal, out int newMax) is { } bad) return Error(400, bad);
        if (!Snapshot(out var r, out long budget, out var keep)) return Error(400, "Scan your mods first.");
        if (Busy) return Busy409();
        var c = Choose(r, budget, keep, normal, newMax);
        if (c.Chosen.Count == 0) return Error(400, "Nothing to shrink with these choices, so no fix was made.");
        var job = new Job { Kind = "fix", Phase = "shrinking", Message = $"Shrinking textures: 0 / {c.Chosen.Count:N0}", Total = c.Chosen.Count };
        return TryStart(job, () => RunFix(job, r, budget, keep, c.Chosen), "The fix failed") ? Accepted() : Busy409();
    }

    Finished RunFix(Job job, ScanResult r, long budget, HashSet<string> keep, List<PlanStep> chosen)
    {
        string game; string? mo2;
        lock (_gate) { game = _game!; mo2 = _mo2; }
        var modRoot = ModRootOf(game)!;
        var target = Fix.PickTarget(_opts.GetValueOrDefault("mo2-output"), mo2);
        int threads = Fix.Threads(_opts.GetValueOrDefault("threads"), target.ViaMo2);
        using var fix = new Fix.Writer(OutDir); // no fix log when the out folder can't be written; the fix itself still works
        job.Refresh = () =>
        {
            var st = fix.Status;
            int done = Volatile.Read(ref st.Done), total = Math.Max(1, st.Total);
            bool saving = st.Phase == "saving";
            job.Phase = st.Phase;
            Volatile.Write(ref job.Done, done); Volatile.Write(ref job.Total, st.Total);
            job.Message = saving ? "Saving the fix file..." : $"Shrinking textures: {done:N0} / {total:N0}";
            job.EtaSeconds = saving ? null : Fix.Eta(fix.Started.Elapsed, done, total)?.TotalSeconds;
        };
        var plan = BudgetPlan.Build(r, budget, keepSharp: keep);
        plan.Steps = chosen;
        var outcome = fix.Build(r, plan, game, modRoot, target, threads, Setup.FixDirs(_settingsFile, mo2), _settingsFile, _ => { });
        job.Refresh = null;
        if (outcome.Textures == 0)
            return new Finished("fix", false, "Nothing was written: none of the chosen textures could be shrunk safely.", DateTime.UtcNow,
                w => { w.WriteNumber("textures", 0); w.WriteNumber("skipped", outcome.Skipped.Count); });

        var check = BudgetArchive.Check(outcome.Path, r);
        lock (_gate)
        {
            if (ReferenceEquals(_result, r)) { r.BudgetArchive = outcome.Path; r.FixCheck = check; RefreshReport(); }
        }
        try { Report.WriteFiles(r, BudgetPlan.Build(r, budget, keepSharp: keep), OutDir); } catch { } // so "Open report" shows the fix as active
        var next = Fix.NextSteps(target, outcome.Path, app: true).Select(l => l.Trim()).ToList();
        string message = $"Done: {outcome.Textures:N0} texture(s) shrunk, saving up to {Report.Size(outcome.SavedBytes)} of VRAM."
            + (outcome.Skipped.Count > 0 ? $" {outcome.Skipped.Count:N0} texture(s) were skipped because their format can't be shrunk safely." : "");
        return new Finished("fix", true, message, DateTime.UtcNow, w =>
        {
            w.WriteString("path", Platform.Shown(outcome.Path));
            w.WriteNumber("textures", outcome.Textures); w.WriteNumber("saved", outcome.SavedBytes); w.WriteNumber("skipped", outcome.Skipped.Count);
            w.WriteBoolean("mo2Mod", target.Mo2ModFolder);
            w.WriteStartArray("nextSteps"); foreach (var l in next) w.WriteStringValue(l); w.WriteEndArray();
            w.WriteStartArray("skippedFiles"); foreach (var s in outcome.Skipped.Take(100)) w.WriteStringValue(s); w.WriteEndArray();
        });
    }

    Answer Undo()
    {
        string? game, mo2;
        lock (_gate)
        {
            if (_busy != null || _undoing) return Busy409();
            if (_game == null) return Error(400, "Pick your Cyberpunk 2077 folder first.");
            _undoing = true; game = _game; mo2 = _mo2;
        }
        try
        {
            var removed = BudgetArchive.Remove(ModRootOf(game)!, quiet: true, extraDirs: Setup.FixDirs(_settingsFile, mo2));
            var message = string.Join(" ", Fix.RemovedMessages(removed));
            var shown = removed.Select(Platform.Shown).ToList();
            lock (_gate)
            {
                if (_result != null) { _result.BudgetArchive = null; _result.FixCheck = null; RefreshReport(); }
                _last = new Finished("undo", true, message, DateTime.UtcNow, w => { w.WriteStartArray("removed"); foreach (var f in shown) w.WriteStringValue(f); w.WriteEndArray(); });
            }
            return Ok(w => { w.WriteStartArray("removed"); foreach (var f in shown) w.WriteStringValue(f); w.WriteEndArray(); w.WriteString("message", message); });
        }
        catch (Exception ex)
        {
            var message = $"Couldn't remove the fix: {ex.Message} If the game is running, close it and try again.";
            lock (_gate) _last = new Finished("undo", false, message, DateTime.UtcNow);
            return Error(500, message);
        }
        finally
        {
            lock (_gate) _undoing = false;
            Changed();
        }
    }

    Answer StartLive(JsonElement? doc)
    {
        if (LiveMonitor.Unavailable() is { } why) return Error(400, why);
        var label = Str(doc, "label")?.Trim();
        if (string.IsNullOrEmpty(label)) label = "session";
        string? game, mo2;
        lock (_gate) { game = _game; mo2 = _mo2; }
        var rec = new LiveMonitor.Recorder();
        var job = new Job { Kind = "live", Phase = "waiting", Message = "Waiting for Cyberpunk 2077 to start...", Live = rec };
        job.Refresh = () =>
        {
            job.Phase = rec.Phase;
            job.Message = rec.Phase == "waiting" ? "Waiting for Cyberpunk 2077 to start..."
                : $"Measuring: the game uses {Report.Size(rec.Game)} of VRAM (peak {Report.Size(rec.Peak)}).";
        };
        return TryStart(job, () =>
        {
            var root = ModRootOf(game);
            bool fixActive = root != null && BudgetArchive.FindExisting(root, Setup.FixDirs(_settingsFile, mo2)) != null;
            var s = LiveMonitor.Record(_opts.GetValueOrDefault("process") ?? "Cyberpunk2077", label, 0, OutDir, fixActive, null, rec, _ => { }, null,
                out bool started, out var csv, out var history);
            if (!started) return new Finished("live", false, "Stopped before the game started, so nothing was measured.", DateTime.UtcNow);
            if (s == null) return new Finished("live", false, "No measurements came in before the game closed.", DateTime.UtcNow);
            string? compare = history.Count >= 2 ? LiveMonitor.Compare(history[^2], history[^1]) : null;
            string message = $"Measured \"{label}\": peak {Report.Size(s.Peak)}, steady {Report.Size(s.Steady)}." + (compare != null ? $" {compare}." : "");
            return new Finished("live", true, message, DateTime.UtcNow, w =>
            {
                LiveMonitor.WriteSession(w, s);
                if (compare != null) w.WriteString("compare", compare);
                if (csv != null) w.WriteString("csv", Platform.Shown(csv));
            });
        }, "Measuring failed") ? Accepted() : Busy409();
    }

    Answer StopLive()
    {
        LiveMonitor.Recorder? rec;
        lock (_gate) rec = _busy?.Live;
        if (rec == null) return Ok(_ => { });
        rec.Stop();
        // Answer once the session is saved (a sample takes about a second), so the next state already has it.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            lock (_gate) if (_busy?.Live != rec) break;
            Thread.Sleep(100);
        }
        return Ok(_ => { });
    }

    // ---- Open and quit -------------------------------------------------------------------------------------------

    Answer Open(JsonElement? doc)
    {
        string? game, mo2;
        lock (_gate) { game = _game; mo2 = _mo2; }
        try
        {
            switch (Str(doc, "what"))
            {
                case "report":
                    var html = Path.Combine(OutDir, "VRAM-RipperDoc-Report.html");
                    if (!File.Exists(html)) return Error(400, "There's no report yet: scan your mods first.");
                    ShellOpen(html);
                    break;
                case "folder":
                    ShellOpen(AppContext.BaseDirectory);
                    break;
                case "fix":
                    var root = ModRootOf(game);
                    var fix = root != null ? BudgetArchive.FindExisting(root, Setup.FixDirs(_settingsFile, mo2)) : null;
                    if (fix == null) return Error(400, "There's no budget fix right now.");
                    if (OperatingSystem.IsWindows() && !Platform.IsWine) Process.Start("explorer.exe", $"/select,\"{fix}\"");
                    else ShellOpen(Path.GetDirectoryName(fix)!);
                    break;
                case "fixlog":
                    var log = Path.Combine(OutDir, "VRAM-RipperDoc-Fix.log");
                    if (!File.Exists(log)) return Error(400, "There's no fix log yet: it's written while a fix is made.");
                    ShellOpen(log);
                    break;
                default:
                    return Error(400, "\"what\" must be \"report\", \"folder\", \"fix\" or \"fixlog\".");
            }
        }
        catch (Exception ex) { return Error(500, $"Couldn't open it: {ex.Message}"); }
        return Ok(_ => { });
    }

    static void ShellOpen(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    Answer Quit()
    {
        _quit = true;
        Changed();
        return Ok(_ => { });
    }

    // ---- JSON ----------------------------------------------------------------------------------------------------

    public static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) write(w);
        return ms.ToArray();
    }

    static Answer Ok(Action<Utf8JsonWriter> fields) => new(200, Json(w => { w.WriteStartObject(); fields(w); w.WriteEndObject(); }));
    static Answer Accepted() => new(202, Json(w => { w.WriteStartObject(); w.WriteEndObject(); }));
    static Answer Busy409() => Error(409, "Wait until the current job is finished.");
    public static Answer Error(int status, string message) => new(status, Json(w => { w.WriteStartObject(); w.WriteString("error", message); w.WriteEndObject(); }));
}
