using System.Text;
using System.Text.Json;

namespace VramRipperdoc;

public static class Report
{
    public static string Version => typeof(Report).Assembly.GetName().Version?.ToString(3) ?? "0";

    public static string Size(long b)
    {
        string sign = b < 0 ? "-" : "";
        double v = Math.Abs(b);
        return v >= 1L << 30 ? $"{sign}{v / (1L << 30):0.00} GB" : v >= 1 << 20 ? $"{sign}{v / (1 << 20):0.0} MB" : v >= 1024 ? $"{sign}{v / 1024:0} KB" : $"{sign}{v:0} B";
    }

    public static void PrintConsole(ScanResult r, BudgetPlan plan, int top)
    {
        Console.WriteLine();
        Console.WriteLine($"Game:     {r.GameDir}");
        if (r.Gpu != null) Console.WriteLine($"GPU:      {r.Gpu.Name} ({Size(r.Gpu.VramBytes)})");
        if (r.Settings != null) Console.WriteLine($"Settings: {r.Settings.Describe()}");
        Console.WriteLine($"Mods:     {r.Mods.Count} archive(s), scanned in {r.Elapsed.TotalSeconds:0.0}s");
        if (r.BudgetArchive != null) Console.WriteLine($"Budget archive active: {Path.GetFileName(r.BudgetArchive)} (numbers below are for your mods without it)");
        Console.WriteLine($"Added VRAM (estimate, everything loaded): {Size(r.TotalDelta)}");
        Console.WriteLine();
        Console.WriteLine($"{"#",3} {"Mod archive",-40} {"+VRAM",10} {"Tex",5} {"Mesh",5} {"Tris LOD0",11} {"Dead",5} {"Miss",5} {"Flags",5}");
        Console.WriteLine(new string('-', 98));
        if (r.Mods.Count == 0) Console.WriteLine("    No .archive mods found in archive/pc/mod or mods/*/archives. Nothing to optimize.");
        foreach (var m in r.Mods.OrderByDescending(m => m.GpuDelta).Take(top))
        {
            string name = m.Name.Length > 40 ? m.Name[..37] + "..." : m.Name;
            if (m.Error != null) { Console.WriteLine($"{m.Priority,3} {name,-40} ERROR: {m.Error}"); continue; }
            Console.WriteLine($"{m.Priority,3} {name,-40} {Size(m.GpuDelta),10} {m.Textures,5} {m.Meshes,5} {m.Tris0,11:N0} {m.Shadowed,5} {m.Missing,5} {m.FlagCount,5}");
            if (m.Package != null && m.Package != m.Name) Console.WriteLine($"    {m.Manager}: {m.Package}{(m.NexusId != null ? $" (nexusmods.com/cyberpunk2077/mods/{m.NexusId})" : "")}");
        }
        Console.WriteLine();
        Console.WriteLine($"Budget for mods: {Size(plan.Budget)}  ({r.BudgetWhy})");
        Console.WriteLine($"  -> {(plan.Current <= plan.Budget ? "OK, within budget." : plan.Reachable ? $"over by {Size(plan.Current - plan.Budget)}; halving {plan.Steps.Count} texture(s) gets you to {Size(plan.AfterPlan)}." : $"over by {Size(plan.Current - plan.Budget)}; textures alone can't fix it (best case {Size(plan.AfterPlan)}). Consider removing a heavy mod.")}");
        foreach (var (mod, saves, count) in plan.PerMod.Take(10))
            Console.WriteLine($"   - {mod.Name}: halve {count} texture(s), saves {Size(saves)}");
        if (plan.Steps.Count > 0 && r.BudgetArchive == null)
            Console.WriteLine("  Apply it with --make-budget-archive (adds one separate archive; your mods are not modified).");
        foreach (var l in r.HistoryDiff) Console.WriteLine(l);
    }

    public static void WriteJson(ScanResult r, BudgetPlan plan, Stream s, int maxAssetsPerMod = int.MaxValue)
    {
        using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("tool", "VRAM RipperDoc - Performance Optimizer");
        w.WriteString("version", Version);
        w.WriteString("generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        w.WriteString("gameDir", r.GameDir);
        w.WriteBoolean("oodle", r.OodleLoaded);
        w.WriteNumber("vanillaFiles", r.BaseFileCount);
        if (r.Gpu != null) { w.WriteString("gpu", r.Gpu.Name); w.WriteNumber("gpuVram", r.Gpu.VramBytes); }
        if (r.Settings != null) w.WriteString("settings", r.Settings.Describe());
        if (r.BudgetArchive != null) w.WriteString("budgetArchive", Path.GetFileName(r.BudgetArchive));
        w.WriteNumber("totalDelta", r.TotalDelta);
        w.WriteString("summary", Summary.Markdown(r, plan));
        w.WriteStartArray("history"); foreach (var l in r.HistoryDiff) w.WriteStringValue(l); w.WriteEndArray();
        w.WriteStartArray("managerNotes"); foreach (var l in r.ManagerNotes) w.WriteStringValue(l); w.WriteEndArray();
        w.WriteStartObject("budget");
        w.WriteNumber("budget", plan.Budget); w.WriteString("why", r.BudgetWhy);
        w.WriteNumber("current", plan.Current); w.WriteNumber("afterPlan", plan.AfterPlan);
        w.WriteStartArray("steps");
        foreach (var p in plan.Steps)
        {
            w.WriteStartObject();
            w.WriteString("mod", p.Mod.Name); w.WriteString("asset", p.Asset.DisplayName);
            w.WriteNumber("from", p.Asset.MaxDim); w.WriteNumber("to", p.Asset.MaxDim >> p.Halvings); w.WriteNumber("saves", p.Saves);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteStartArray("mods");
        foreach (var m in r.Mods)
        {
            w.WriteStartObject();
            w.WriteString("name", m.Name); w.WriteString("file", m.File); w.WriteString("source", m.Source);
            if (m.Package != null) w.WriteString("package", m.Package);
            if (m.NexusId != null) w.WriteNumber("nexusId", m.NexusId.Value);
            if (m.Manager != null) w.WriteString("manager", m.Manager);
            if (m.CommunityNote != null) w.WriteString("community", m.CommunityNote);
            w.WriteNumber("priority", m.Priority); w.WriteNumber("disk", m.DiskSize);
            if (m.Error != null) w.WriteString("error", m.Error);
            w.WriteNumber("delta", m.GpuDelta); w.WriteNumber("texDelta", m.TextureDelta); w.WriteNumber("meshDelta", m.MeshDelta);
            w.WriteNumber("textures", m.Textures); w.WriteNumber("meshes", m.Meshes); w.WriteNumber("tris", m.Tris0);
            w.WriteNumber("overrides", m.Overrides); w.WriteNumber("shadowed", m.Shadowed); w.WriteNumber("shadowedDisk", m.ShadowedDisk);
            w.WriteNumber("missing", m.Missing); w.WriteNumber("flagged", m.FlagCount);
            w.WriteStartArray("assets");
            foreach (var a in m.Assets.OrderByDescending(a => a.Flags.Count > 0 && a.Loads && a.MissingImports.Count > 0)
                         .ThenByDescending(a => Math.Abs(a.DeltaGpu)).ThenByDescending(a => a.Flags.Count).Take(maxAssetsPerMod))
            {
                if (a.Kind == AssetKind.Other && a.Flags.Count == 0 && a.GpuBytes < 64 * 1024) continue;
                w.WriteStartObject();
                w.WriteString("name", a.DisplayName);
                w.WriteString("kind", a.Kind.ToString());
                w.WriteString("class", a.Class);
                w.WriteNumber("gpu", a.GpuBytes); w.WriteNumber("delta", a.DeltaGpu);
                if (a.Kind == AssetKind.Texture)
                {
                    w.WriteNumber("w", a.Width); w.WriteNumber("h", a.Height); w.WriteNumber("mips", a.Mips);
                    w.WriteString("fmt", a.Compression);
                }
                if (a.Kind == AssetKind.Mesh) { w.WriteNumber("tris", a.Tris0); w.WriteNumber("lods", a.LodCount); }
                w.WriteBoolean("override", a.OverridesBase);
                if (a.ShadowedBy != null) w.WriteString("shadowedBy", a.ShadowedBy);
                if (a.MissingImports.Count > 0) { w.WriteStartArray("missing"); foreach (var x in a.MissingImports) w.WriteStringValue(x); w.WriteEndArray(); }
                w.WriteStartArray("flags"); foreach (var f in a.Flags) w.WriteStringValue(f); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteHtml(ScanResult r, BudgetPlan plan, string path)
    {
        var ms = new MemoryStream();
        WriteJson(r, plan, ms, maxAssetsPerMod: 300);
        // Safe to embed: escape "</" so the JSON can't close the script tag.
        string json = Encoding.UTF8.GetString(ms.ToArray()).Replace("</", "<\\/");
        File.WriteAllText(path, HtmlTemplate.Replace("/*DATA*/null", json), new UTF8Encoding(false));
    }

    const string HtmlTemplate = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>VRAM RipperDoc Report</title>
<style>
:root{--bg:#0b0d12;--panel:#141821;--line:#262c3a;--fg:#e6e8ee;--dim:#8b93a7;--acc:#fcee0a;--hot:#ff4d6d;--ok:#3ddc97;--cy:#2de2e6}
@media (prefers-color-scheme: light){:root:not([data-theme=dark]){--bg:#f6f7f9;--panel:#fff;--line:#dde1e8;--fg:#14161c;--dim:#5b6375;--acc:#8a7d00;--hot:#c8102e;--ok:#0a8a5a;--cy:#007c80}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,Segoe UI,sans-serif}
main{max-width:1200px;margin:0 auto;padding:24px 16px 64px}a{color:var(--cy)}
h1{font-size:22px;margin:0 0 4px;letter-spacing:.5px}h1 b{color:var(--acc)}.sub{color:var(--dim);margin:0 0 20px}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin-bottom:20px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:12px}.card .k{color:var(--dim);font-size:12px;text-transform:uppercase;letter-spacing:.06em}.card .v{font-size:22px;font-weight:600;margin-top:4px;font-variant-numeric:tabular-nums}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:14px;margin-bottom:20px;overflow-x:auto}
h2{font-size:15px;margin:0 0 10px}table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
th,td{padding:7px 8px;border-bottom:1px solid var(--line);text-align:right;white-space:nowrap}th{color:var(--dim);font-weight:500;cursor:pointer;user-select:none;font-size:12px}
th:first-child,td:first-child,td.l,th.l{text-align:left}tr.mod{cursor:pointer}tr.mod:hover{background:color-mix(in srgb,var(--acc) 8%,transparent)}
.bar{height:6px;background:var(--line);border-radius:3px;min-width:80px}.bar i{display:block;height:100%;background:var(--acc);border-radius:3px}
.tag{display:inline-block;font-size:11px;padding:1px 6px;border-radius:10px;border:1px solid var(--hot);color:var(--hot);margin:1px 4px 1px 0;white-space:nowrap}
.tag.dead{border-color:var(--dim);color:var(--dim)}.neg{color:var(--ok)}.hot{color:var(--hot)}
tr.detail td{background:var(--bg);text-align:left;white-space:normal}tr.detail table td{font-size:12.5px}
.name{max-width:520px;overflow:hidden;text-overflow:ellipsis;font-family:ui-monospace,Consolas,monospace;font-size:12px}
.ok{color:var(--ok)}.warn{color:var(--hot)}.note{color:var(--dim);font-size:12.5px}
input{background:var(--bg);color:var(--fg);border:1px solid var(--line);border-radius:6px;padding:6px 8px;width:100%;max-width:340px;margin:0 10px 10px 0}
button{background:var(--bg);color:var(--fg);border:1px solid var(--line);border-radius:6px;padding:6px 10px;cursor:pointer;font:inherit}button:hover{border-color:var(--acc)}
.tools{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-bottom:6px}.tools input{margin:0}
pre{white-space:pre-wrap;font:12px ui-monospace,Consolas,monospace;color:var(--dim);margin:0}
</style></head><body><main>
<h1><b>VRAM RipperDoc</b> &mdash; Performance Optimizer</h1><p class="sub" id="sub"></p>
<div class="cards" id="cards"></div>
<div class="panel" id="budget"></div>
<div class="panel" id="hist" hidden></div>
<div class="panel"><h2>Mods by added VRAM</h2>
<div class="tools"><input id="q" placeholder="Filter mods or files..."><label class="note"><input type="checkbox" id="grp" style="width:auto;margin:0 4px 0 0">Group by installed mod</label><button id="copy">Copy summary for a support post</button></div>
<table id="mods"></table>
<p class="note">+VRAM is an estimate: the GPU data of every texture and mesh the archive adds, minus what the vanilla file it replaces already used. Files another archive wins are counted as 0. The game streams assets, so real usage depends on what is on screen; measure it with <code>--live</code>. Click a row for its heaviest and flagged files.</p></div>
</main><script>
const D=/*DATA*/null;
const $=s=>document.querySelector(s),esc=s=>String(s).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const sz=b=>{const s=b<0?'-':'',v=Math.abs(b);return v>=2**30?s+(v/2**30).toFixed(2)+' GB':v>=2**20?s+(v/2**20).toFixed(1)+' MB':v>=1024?s+(v/1024).toFixed(0)+' KB':s+v+' B'};
$('#sub').textContent=`${D.mods.length} mod archive(s) · ${D.gameDir} · ${D.generated} · v${D.version}`+(D.settings?` · ${D.settings}`:'')+(D.oodle?'':' · Oodle missing: sizes only');
const flagged=D.mods.reduce((a,m)=>a+m.flagged,0),dead=D.mods.reduce((a,m)=>a+m.shadowedDisk,0),miss=D.mods.reduce((a,m)=>a+m.missing,0);
$('#cards').innerHTML=[['Added VRAM (est.)',sz(D.totalDelta)],['GPU',D.gpu?sz(D.gpuVram):'unknown'],['Mod budget',sz(D.budget.budget)],['Flagged files',flagged.toLocaleString()],['Missing references',miss.toLocaleString()],['Dead weight (never loads)',sz(dead)]]
 .map(([k,v])=>`<div class="card"><div class="k">${k}</div><div class="v">${v}</div></div>`).join('');
const B=D.budget;let bh=`<h2>Budget plan</h2>`;
if(D.budgetArchive)bh+=`<p class="ok">Budget archive active: <code>${esc(D.budgetArchive)}</code>. Delete it (or run <code>--remove-budget-archive</code>) to undo.</p>`;
if(B.current<=B.budget)bh+=`<p class="ok">Your mods add about ${sz(B.current)}, within the ${sz(B.budget)} budget. No changes needed.</p>`;
else{bh+=`<p class="warn">Mods add about ${sz(B.current)}, ${sz(B.current-B.budget)} over the ${sz(B.budget)} budget.</p>`;
 bh+=B.afterPlan<=B.budget?`<p>Halving ${B.steps.length} texture(s) brings it to <b>${sz(B.afterPlan)}</b>. Apply with <code>--make-budget-archive</code>: it writes one extra archive and never modifies your mods.</p>`:`<p>Halving every texture over 2K only reaches ${sz(B.afterPlan)}. Consider removing or swapping a heavy mod (top of the table).</p>`;
 if(B.steps.length)bh+=`<table><tr><th class="l">Mod</th><th class="l">File</th><th>Now</th><th>Target</th><th>Saves</th></tr>`+B.steps.slice(0,40).map(s=>`<tr><td class="l">${esc(s.mod)}</td><td class="l name" title="${esc(s.asset)}">${esc(s.asset)}</td><td>${s.from}px</td><td>${s.to}px</td><td>${sz(s.saves)}</td></tr>`).join('')+'</table>'+(B.steps.length>40?`<p class="note">+${B.steps.length-40} more in the JSON report</p>`:'');}
$('#budget').innerHTML=bh+`<p class="note">Budget: ${esc(B.why)}. Override with <code>--budget &lt;GB&gt;</code>.</p>`;
if(D.history.length||D.managerNotes.length){$('#hist').hidden=false;$('#hist').innerHTML=`<h2>Changes &amp; sources</h2><pre>${esc([...D.managerNotes,...D.history].join('\n'))}</pre>`;}
const cols=[['priority','#'],['name','Mod archive','l'],['delta','+VRAM'],['bar',''],['textures','Tex'],['meshes','Mesh'],['tris','Tris LOD0'],['overrides','Overrides'],['shadowed','Never loads'],['missing','Missing'],['flagged','Flags']];
let sortK='delta',sortD=-1,open=new Set();
function groups(){const g=new Map();for(const m of D.mods){const k=m.package||m.name;if(!g.has(k))g.set(k,{name:k,package:m.package,nexusId:m.nexusId,manager:m.manager,priority:m.priority,source:m.source,disk:0,delta:0,textures:0,meshes:0,tris:0,overrides:0,shadowed:0,missing:0,flagged:0,assets:[],archives:[]});
 const x=g.get(k);for(const f of['disk','delta','textures','meshes','tris','overrides','shadowed','missing','flagged'])x[f]+=m[f];x.assets.push(...m.assets);x.archives.push(m.name);if(m.error)x.error=m.error;}
 for(const x of g.values())x.assets.sort((a,b)=>Math.abs(b.delta)-Math.abs(a.delta));return[...g.values()];}
function render(){const q=$('#q').value.toLowerCase(),list=$('#grp').checked?groups():D.mods,max=Math.max(1,...list.map(m=>m.delta));
 const rows=list.filter(m=>!q||m.name.toLowerCase().includes(q)||(m.package||'').toLowerCase().includes(q)||m.assets.some(a=>a.name.toLowerCase().includes(q))).sort((a,b)=>(a[sortK]>b[sortK]?1:a[sortK]<b[sortK]?-1:0)*sortD);
 let h='<tr>'+cols.map(([k,t,c])=>`<th class="${c||''}" data-k="${k}">${t}${sortK===k?(sortD<0?' ▾':' ▴'):''}</th>`).join('')+'</tr>';
 for(const m of rows){const link=m.nexusId?` · <a href="https://www.nexusmods.com/cyberpunk2077/mods/${m.nexusId}" target="_blank" rel="noopener">Nexus #${m.nexusId}</a>`:'';
  const pkg=m.package&&m.package!==m.name?` · ${esc(m.manager||'')}: ${esc(m.package)}`:'';
  h+=`<tr class="mod" data-n="${esc(m.name)}"><td>${m.priority}</td><td class="l">${esc(m.name)}${m.error?` <span class="tag">${esc(m.error)}</span>`:''}<div class="note">${esc(m.source)} · ${sz(m.disk)} on disk${m.archives?` · ${m.archives.length} archive(s)`:pkg}${link}${m.community?` · ${esc(m.community)}`:''}</div></td><td class="${m.delta<0?'neg':m.delta>2**30?'hot':''}">${sz(m.delta)}</td><td><div class="bar"><i style="width:${Math.max(0,m.delta)/max*100}%"></i></div></td><td>${m.textures}</td><td>${m.meshes}</td><td>${m.tris.toLocaleString()}</td><td>${m.overrides}</td><td>${m.shadowed}</td><td class="${m.missing?'hot':''}">${m.missing}</td><td class="${m.flagged?'hot':''}">${m.flagged}</td></tr>`;
  if(open.has(m.name)){h+=`<tr class="detail"><td colspan="${cols.length}"><table><tr><th class="l">File</th><th class="l">Kind</th><th>Size</th><th>GPU</th><th>+VRAM</th><th class="l">Notes</th></tr>`+
   m.assets.slice(0,60).map(a=>`<tr><td class="l name" title="${esc(a.name)}">${esc(a.name)}</td><td class="l">${esc(a.class||a.kind)}</td><td>${a.kind==='Texture'?`${a.w}×${a.h} ${a.mips}mip ${esc((a.fmt||'').replace('TCM_',''))}`:a.kind==='Mesh'?`${a.tris.toLocaleString()} tris · ${a.lods} LOD`:''}</td><td>${sz(a.gpu)}</td><td class="${a.delta<0?'neg':''}">${sz(a.delta)}</td><td class="l">${a.override?'<span class="tag dead">replaces vanilla</span>':''}${a.flags.map(f=>`<span class="tag ${a.shadowedBy?'dead':''}">${esc(f)}</span>`).join('')}</td></tr>`).join('')+
   `</table>${m.assets.length>60?`<p class="note">${m.assets.length-60} more in the JSON report</p>`:''}</td></tr>`;}}
 if(!rows.length)h+=`<tr><td class="l" colspan="${cols.length}">${D.mods.length?'No mods match the filter.':'No .archive mods found in archive/pc/mod or mods/*/archives.'}</td></tr>`;
 $('#mods').innerHTML=h;}
$('#mods').addEventListener('click',e=>{if(e.target.closest('a'))return;const th=e.target.closest('th[data-k]');if(th&&!th.closest('tr.detail')){const k=th.dataset.k==='bar'?'delta':th.dataset.k;sortD=sortK===k?-sortD:-1;sortK=k;return render();}
 const tr=e.target.closest('tr.mod');if(tr){const n=tr.dataset.n;open.has(n)?open.delete(n):open.add(n);render();}});
$('#q').addEventListener('input',render);$('#grp').addEventListener('change',()=>{open.clear();render();});
$('#copy').addEventListener('click',async()=>{try{await navigator.clipboard.writeText(D.summary);$('#copy').textContent='Copied ✓';}catch{prompt('Copy this summary:',D.summary);}});
render();
</script></body></html>
""";
}
