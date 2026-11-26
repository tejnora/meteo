using System.Text;
using System.Text.Json;

namespace Meteo.Validation;

/// <summary>Self-contained HTML report (data embedded, charts drawn as inline SVG by plain JS, works offline).</summary>
public static class Report
{
    public static void Write(ValidationContext ctx)
    {
        string Csv(string name) { var p = ctx.PathFor("report", name); return File.Exists(p) ? File.ReadAllText(p) : ""; }
        var data = new Dictionary<string, string>
        {
            ["days"] = Csv("days.csv"), ["hourly"] = Csv("satellite_hourly.csv"), ["overall"] = Csv("overall.csv"),
            ["bins"] = Csv("bins.csv"), ["thermals"] = Csv("thermals.csv"),
            ["generated"] = DateTime.Now.ToString("d. M. yyyy HH:mm"),
        };
        var json = JsonSerializer.Serialize(data).Replace("</", "<\\/");
        var path = ctx.PathFor("report", "report.html");
        File.WriteAllText(path, Template.Replace("/*DATA*/null", json), new UTF8Encoding(false));
        Console.WriteLine($"Report: {path}");
    }

    private const string Template = """
<!doctype html>
<html lang="cs">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Ověření předpovědi 2026</title>
<style>
/* layout: one reading column, figures full width, tables scroll inside their own box */
:root {
  --bg: #f7f8f6; --panel: #ffffff; --fg: #1d2421; --muted: #5d6963; --line: #d9dfdb;
  --fc: #2f6db5; --obs: #d9822b; --ok: #2e8540; --warn: #b7791f; --bad: #c0392b; --grid: #e6eae7;
  --sans: "Segoe UI", system-ui, -apple-system, sans-serif; --mono: ui-monospace, "Cascadia Mono", Consolas, monospace;
}
@media (prefers-color-scheme: dark) {
  :root { --bg: #151a18; --panel: #1d2421; --fg: #e3e8e5; --muted: #9aa8a1; --line: #33403a;
          --fc: #6aa3e8; --obs: #f0a35a; --ok: #5cbf72; --warn: #e0b04a; --bad: #ef6b5b; --grid: #2a332f; color-scheme: dark; }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.55 var(--sans); }
main { max-width: 980px; margin: 0 auto; padding: 32px 16px 64px; display: grid; gap: 28px; }
h1 { font-size: 28px; margin: 0; text-wrap: balance; }
h2 { font-size: 19px; margin: 0 0 4px; text-wrap: balance; }
p { margin: 0; max-width: 70ch; }
.muted { color: var(--muted); }
.lede { font-size: 17px; }
.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 12px; }
.kpi { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 14px 16px; display: grid; gap: 2px; }
.kpi b { font-size: 26px; font-variant-numeric: tabular-nums; font-weight: 600; }
.kpi span { color: var(--muted); font-size: 13px; }
section { display: grid; gap: 10px; }
.fig { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 12px; position: relative; min-width: 0; }
.fig svg { display: block; width: 100%; height: auto; }
.legend { display: flex; gap: 16px; flex-wrap: wrap; font-size: 13px; color: var(--muted); }
.legend i { display: inline-block; width: 12px; height: 12px; border-radius: 2px; margin-right: 6px; vertical-align: -1px; }
.tip { position: absolute; pointer-events: none; background: var(--panel); border: 1px solid var(--line); border-radius: 6px;
       padding: 6px 9px; font-size: 13px; box-shadow: 0 4px 14px rgba(0,0,0,.15); white-space: nowrap; }
.scroll { overflow-x: auto; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; }
table { border-collapse: collapse; width: 100%; font-size: 14px; font-variant-numeric: tabular-nums; }
th, td { padding: 7px 10px; text-align: right; border-bottom: 1px solid var(--line); white-space: nowrap; }
th:first-child, td:first-child { text-align: left; }
th { color: var(--muted); font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; }
tr:last-child td { border-bottom: 0; }
.pill { display: inline-block; padding: 1px 8px; border-radius: 99px; font-size: 12px; font-weight: 600; }
.pill.ok { background: color-mix(in srgb, var(--ok) 18%, transparent); color: var(--ok); }
.pill.warn { background: color-mix(in srgb, var(--warn) 20%, transparent); color: var(--warn); }
.pill.bad { background: color-mix(in srgb, var(--bad) 18%, transparent); color: var(--bad); }
select { font: inherit; padding: 4px 8px; border-radius: 6px; border: 1px solid var(--line); background: var(--panel); color: var(--fg); }
.axis text { fill: var(--muted); font-size: 11px; }
.axis line, .axis path { stroke: var(--grid); }
details { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 12px 16px; }
summary { cursor: pointer; font-weight: 600; }
details ul { margin: 8px 0 0; padding-left: 20px; display: grid; gap: 6px; max-width: 75ch; }
code { font-family: var(--mono); font-size: 13px; }
</style>
</head>
<body>
<main>
  <header style="display:grid;gap:8px">
    <h1>Ověření předpovědi 2026</h1>
    <p class="lede" id="lede"></p>
    <p class="muted" id="meta"></p>
  </header>

  <div class="kpis" id="kpis"></div>

  <section>
    <h2>Sluneční energie za den: předpověď vs. satelit</h2>
    <p class="muted">Průměr ~350 bodů nad ČR, hodiny 07–19 SELČ. Ukazuje, jestli předpověď trefila slunečný nebo zatažený den.</p>
    <div class="legend"><span><i style="background:var(--fc)"></i>předpověď (ICON-EU 00Z)</span><span><i style="background:var(--obs)"></i>satelit (EUMETSAT)</span></div>
    <div class="fig" id="energy"></div>
  </section>

  <section>
    <h2>Průběh záření během dne</h2>
    <p class="muted">Globální záření, průměr přes ČR, hodinové průměry. <label>Den: <select id="daysel"></select></label></p>
    <div class="fig" id="hourly"></div>
  </section>

  <section>
    <h2>Stoupání z IGC vs. předpovězené W*</h2>
    <p class="muted" id="scatterNote"></p>
    <div class="legend"><span><i style="background:var(--muted);opacity:.35"></i>jednotlivá stoupání</span><span><i style="background:var(--fc)"></i>medián (den × hodina × oblast 50 km)</span><span><i style="background:var(--obs)"></i>regresní přímka</span></div>
    <div class="fig" id="scatter"></div>
  </section>

  <section>
    <h2>Dny s lety: dostup a konec termiky</h2>
    <p class="muted">Vrchol = 90. percentil vrcholů stoupání. Konec = poslední stoupání ≥ 1 m/s u nejpozdější čtvrtiny letů (piloti často přistanou dřív, je to dolní odhad).</p>
    <div class="scroll"><table id="igcTable"></table></div>
  </section>

  <section>
    <h2>Záření po dnech</h2>
    <div class="scroll"><table id="satTable"></table></div>
  </section>

  <details>
    <summary>Metodika a omezení</summary>
    <ul>
      <li>Předpověď přehrána skutečným kódem aplikace (<code>Meteo.Core</code>: dekódování GRIB, převzorkování, termický model) z archivu souborů DWD na source.coop (dynamical.org). Použit běh <b>ICON-EU 00 UTC</b> v den letu, tedy předpověď dostupná ráno. ICON-D2 se nearchivuje, v produkci jsou dny 1–2 z ICON-D2 (2,2 km), takže výsledky pro D2 mohou být lepší.</li>
      <li>Satelit: Open-Meteo satellite radiation archive (EUMETSAT), hodinový průměr předchozí hodiny, stejná konvence jako aplikace. Prostorové r = korelace mezi body v rámci hodiny, tj. jestli je oblačnost na správném místě.</li>
      <li>IGC z cpska.cz (limit 20 stažení za 24 h). Stoupání = kroužení ≥ 60 s se ziskem ≥ 100 m, rychlost = lineární regrese výšky. Úseky s běžícím motorem (ENL/MOP) vyřazeny. Výška GNSS převedena z elipsoidu na nadmořskou (+46 m geoid).</li>
      <li>W* je rychlost jádra termiky před odečtením opadání kluzáku, proto se čeká stoupání ≈ W* − 0,5 až 1 m/s.</li>
      <li>Report vygeneroval <code>tools/Meteo.Validation analyze</code>.</li>
    </ul>
  </details>
</main>

<script>
const RAW = /*DATA*/null;
const NS = "http://www.w3.org/2000/svg";
function csv(text) {
  const lines = text.trim().split(/\r?\n/).filter(Boolean);
  if (lines.length < 2) return [];
  const head = lines[0].split(",");
  return lines.slice(1).map(l => { const c = l.split(","); const o = {}; head.forEach((h, i) => o[h] = c[i] ?? ""); return o; });
}
const num = v => v === "" || v == null ? NaN : +v;
const fmt = (v, d = 0) => isFinite(v) ? v.toLocaleString("cs-CZ", { minimumFractionDigits: d, maximumFractionDigits: d }) : "–";
const dayLabel = s => { const [, m, d] = s.split("-"); return `${+d}. ${+m}.`; };
const el = (tag, attrs = {}, parent) => { const e = document.createElementNS(NS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); if (parent) parent.appendChild(e); return e; };
const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

const days = csv(RAW.days), hourly = csv(RAW.hourly), bins = csv(RAW.bins), thermals = csv(RAW.thermals);
const overall = csv(RAW.overall)[0] || {};
const igcDays = days.filter(d => d.thermals !== "");

// ---- header ----
const r = num(overall.sat_r), bias = num(overall.sat_bias), gap = num(overall.bin_wstar_minus_climb);
document.getElementById("lede").textContent =
  `Záření z předpovědi sedí se satelitem s korelací ${fmt(r, 2)}, ale je v průměru o ${fmt(Math.abs(bias))} W/m² ${bias < 0 ? "nižší" : "vyšší"}. ` +
  (isFinite(gap) ? `Naměřená stoupání jsou v průměru o ${fmt(gap, 2)} m/s slabší než předpovězené W*, což odpovídá opadání kluzáku.` : "");
document.getElementById("meta").textContent =
  `${days.length} dnů (květen–srpen 2026), ${fmt(num(overall.sat_n))} porovnání se satelitem, ${fmt(num(overall.thermals))} stoupání z ${fmt(num(overall.flights))} letů (${igcDays.length} dny s IGC). Vygenerováno ${RAW.generated}.`;

const kpis = [
  [fmt(r, 2), "korelace záření se satelitem (hodinově)"],
  [`${bias > 0 ? "+" : "−"}${fmt(Math.abs(bias))} W/m²`, "průměrná odchylka záření"],
  [isFinite(gap) ? `${fmt(gap, 2)} m/s` : "–", "W* minus naměřené stoupání"],
  [overall.fit_climb_eq ? overall.fit_climb_eq.replace("stoupání ≈ ", "") : "–", "regrese: stoupání ≈"],
];
const kbox = document.getElementById("kpis");
for (const [v, l] of kpis) { const d = document.createElement("div"); d.className = "kpi"; const b = document.createElement("b"); b.textContent = v; const s = document.createElement("span"); s.textContent = l; d.append(b, s); kbox.append(d); }

// ---- tooltip helper ----
function tipFor(box) {
  const t = document.createElement("div"); t.className = "tip"; t.hidden = true; box.appendChild(t);
  return {
    show(ev, text) { t.textContent = text; t.hidden = false; const b = box.getBoundingClientRect();
      let x = ev.clientX - b.left + 12, y = ev.clientY - b.top - 30; x = Math.min(x, b.width - t.offsetWidth - 4); t.style.left = Math.max(4, x) + "px"; t.style.top = Math.max(4, y) + "px"; },
    hide() { t.hidden = true; }
  };
}
function frame(box, height, m) {
  box.querySelectorAll("svg,.tip").forEach(n => n.remove());
  const W = Math.max(320, box.clientWidth - 24), H = height;
  const svg = el("svg", { viewBox: `0 0 ${W} ${H}`, role: "img" }, box);
  return { svg, W, H, x0: m.l, x1: W - m.r, y0: H - m.b, y1: m.t };
}
function yAxis(f, ymax, step, unit) {
  const g = el("g", { class: "axis" }, f.svg);
  for (let v = 0; v <= ymax + 1e-9; v += step) {
    const y = f.y0 - (f.y0 - f.y1) * v / ymax;
    el("line", { x1: f.x0, x2: f.x1, y1: y, y2: y }, g);
    const t = el("text", { x: f.x0 - 6, y: y + 4, "text-anchor": "end" }, g); t.textContent = fmt(v, step < 1 ? 1 : 0) + (v === 0 ? "" : unit);
  }
}

// ---- energy per day ----
function drawEnergy() {
  const box = document.getElementById("energy");
  const f = frame(box, 260, { l: 44, r: 8, t: 10, b: 28 });
  f.svg.setAttribute("aria-label", "Denní sluneční energie, předpověď a satelit, po dnech");
  const ymax = Math.ceil(Math.max(...days.flatMap(d => [num(d.energy_fc), num(d.energy_obs)])) );
  yAxis(f, ymax, 2, "");
  const tip = tipFor(box);
  const bw = (f.x1 - f.x0) / days.length;
  days.forEach((d, i) => {
    const x = f.x0 + i * bw;
    [["energy_fc", "--fc", 0], ["energy_obs", "--obs", 1]].forEach(([k, c, j]) => {
      const v = num(d[k]); const h = (f.y0 - f.y1) * v / ymax;
      const rect = el("rect", { x: x + bw * (0.14 + 0.36 * j), y: f.y0 - h, width: bw * 0.34, height: h, rx: 2, fill: css(c) }, f.svg);
      rect.addEventListener("mousemove", ev => tip.show(ev, `${dayLabel(d.day)} · předpověď ${fmt(num(d.energy_fc), 2)}, satelit ${fmt(num(d.energy_obs), 2)} kWh/m²`));
      rect.addEventListener("mouseleave", () => tip.hide());
    });
    const t = el("text", { x: x + bw / 2, y: f.H - 8, "text-anchor": "middle", class: "axis" }, f.svg); t.textContent = dayLabel(d.day);
    t.setAttribute("fill", css("--muted")); t.setAttribute("font-size", "11");
  });
  const u = el("text", { x: 4, y: 12, fill: css("--muted"), "font-size": "11" }, f.svg); u.textContent = "kWh/m²";
}

// ---- hourly radiation for one day ----
const sel = document.getElementById("daysel");
days.forEach(d => sel.add(new Option(dayLabel(d.day) + " 2026", d.day)));
sel.value = days.some(d => d.day === "2026-05-26") ? "2026-05-26" : days[0]?.day;
sel.addEventListener("change", drawHourly);
function drawHourly() {
  const box = document.getElementById("hourly");
  const rows = hourly.filter(h => h.day === sel.value);
  const f = frame(box, 260, { l: 44, r: 12, t: 10, b: 28 });
  f.svg.setAttribute("aria-label", "Hodinové globální záření, předpověď a satelit");
  const ymax = 1000; yAxis(f, ymax, 200, "");
  const xs = i => f.x0 + (f.x1 - f.x0) * i / Math.max(1, rows.length - 1);
  const ys = v => f.y0 - (f.y0 - f.y1) * v / ymax;
  rows.forEach((h, i) => { if (i % 2 === 0) { const t = el("text", { x: xs(i), y: f.H - 8, "text-anchor": "middle", fill: css("--muted"), "font-size": "11" }, f.svg); t.textContent = h.local; } });
  for (const [k, c] of [["fc", "--fc"], ["obs", "--obs"]]) {
    el("polyline", { points: rows.map((h, i) => `${xs(i)},${ys(num(h[k]))}`).join(" "), fill: "none", stroke: css(c), "stroke-width": 2.5 }, f.svg);
    rows.forEach((h, i) => el("circle", { cx: xs(i), cy: ys(num(h[k])), r: 3, fill: css(c) }, f.svg));
  }
  const tip = tipFor(box);
  rows.forEach((h, i) => {
    const w = (f.x1 - f.x0) / Math.max(1, rows.length - 1);
    const hit = el("rect", { x: xs(i) - w / 2, y: f.y1, width: w, height: f.y0 - f.y1, fill: "transparent" }, f.svg);
    hit.addEventListener("mousemove", ev => tip.show(ev, `${h.local} · předpověď ${h.fc} W/m², satelit ${h.obs} W/m²`));
    hit.addEventListener("mouseleave", () => tip.hide());
  });
  const u = el("text", { x: 4, y: 12, fill: css("--muted"), "font-size": "11" }, f.svg); u.textContent = "W/m²";
}

// ---- scatter climb vs W* ----
function drawScatter() {
  const box = document.getElementById("scatter");
  document.getElementById("scatterNote").textContent = thermals.length
    ? `${thermals.length} stoupání. Šedá čára = stoupání rovné W*. Korelace pro mediány skupin r = ${fmt(num(overall.r_bin_climb_wstar), 2)}, pro jednotlivá stoupání r = ${fmt(num(overall.r_thermal_climb_wstar), 2)}.`
    : "Zatím žádná IGC data.";
  const f = frame(box, 320, { l: 44, r: 12, t: 10, b: 36 });
  f.svg.setAttribute("aria-label", "Bodový graf naměřeného stoupání proti předpovězenému W*");
  const max = 4;
  yAxis(f, max, 1, "");
  const xs = v => f.x0 + (f.x1 - f.x0) * Math.min(v, max) / max, ys = v => f.y0 - (f.y0 - f.y1) * Math.min(v, max) / max;
  for (let v = 0; v <= max; v++) { const t = el("text", { x: xs(v), y: f.y0 + 16, "text-anchor": "middle", fill: css("--muted"), "font-size": "11" }, f.svg); t.textContent = v; }
  const xl = el("text", { x: (f.x0 + f.x1) / 2, y: f.H - 4, "text-anchor": "middle", fill: css("--muted"), "font-size": "12" }, f.svg); xl.textContent = "předpovězené W* (m/s)";
  const yl = el("text", { x: 4, y: 12, fill: css("--muted"), "font-size": "11" }, f.svg); yl.textContent = "stoupání m/s";
  el("line", { x1: xs(0), y1: ys(0), x2: xs(max), y2: ys(max), stroke: css("--muted"), "stroke-dasharray": "4 4", "stroke-width": 1 }, f.svg);
  for (const t of thermals) el("circle", { cx: xs(num(t.wstar)), cy: ys(num(t.climb)), r: 2.2, fill: css("--muted"), "fill-opacity": .3 }, f.svg);
  const m = /([\d.]+)\s*·\s*W\*\s*([+−-])\s*([\d.]+)/.exec(overall.fit_climb_eq || "");
  if (m) { const a = +m[1], b = (m[2] === "+" ? 1 : -1) * +m[3]; const xa = Math.max(0, -b / a);
    el("line", { x1: xs(xa), y1: ys(a * xa + b), x2: xs(max), y2: ys(a * max + b), stroke: css("--obs"), "stroke-width": 2 }, f.svg); }
  const tip = tipFor(box);
  for (const b of bins) {
    const c = el("circle", { cx: xs(num(b.wstar_median)), cy: ys(num(b.climb_median)), r: 3 + Math.sqrt(num(b.n)), fill: css("--fc"), "fill-opacity": .75, stroke: css("--panel"), "stroke-width": 1 }, f.svg);
    c.addEventListener("mousemove", ev => tip.show(ev, `${dayLabel(b.day)} ${b.hour}:00 · ${b.n} stoupání · medián ${fmt(num(b.climb_median), 1)} m/s, W* ${fmt(num(b.wstar_median), 1)} m/s`));
    c.addEventListener("mouseleave", () => tip.hide());
  }
}

// ---- tables ----
function table(id, cols, rows) {
  const t = document.getElementById(id); t.replaceChildren();
  const tr = t.createTHead().insertRow(); cols.forEach(c => { const th = document.createElement("th"); th.textContent = c[0]; tr.appendChild(th); });
  const tb = t.createTBody();
  rows.forEach(r => { const row = tb.insertRow(); cols.forEach(c => { const cell = row.insertCell(); const v = c[1](r); if (v instanceof Node) cell.appendChild(v); else cell.textContent = v; }); });
  if (!rows.length) { const row = tb.insertRow(); const c = row.insertCell(); c.colSpan = cols.length; c.textContent = "Zatím žádná data."; }
}
const pill = (text, cls) => { const s = document.createElement("span"); s.className = "pill " + cls; s.textContent = text; return s; };
const minutes = s => { if (!s || s === "–") return NaN; const [h, m] = s.split(":"); return +h * 60 + +m; };
table("igcTable", [
  ["Den", d => dayLabel(d.day)],
  ["Lety", d => d.flights], ["Stoupání", d => d.thermals],
  ["Medián stoupání", d => fmt(num(d.climb_median), 2) + " m/s"],
  ["Medián W*", d => fmt(num(d.wstar_median), 2) + " m/s"],
  ["Vrchol p90", d => fmt(num(d.top_p90)) + " m"],
  ["Dostup předpověď", d => fmt(num(d.fc_ceiling_median)) + " m"],
  ["Rozdíl", d => { const v = num(d.fc_ceiling_median) - num(d.top_p90); return pill((v > 0 ? "+" : "−") + fmt(Math.abs(v)) + " m", Math.abs(v) < 200 ? "ok" : Math.abs(v) < 400 ? "warn" : "bad"); }],
  ["Konec pozorovaný", d => d.obs_end_local || "–"],
  ["Konec (W* ≥ 1)", d => { const v = minutes(d.fc_end_local) - minutes(d.obs_end_local); return pill(d.fc_end_local || "–", Math.abs(v) <= 45 ? "ok" : Math.abs(v) <= 90 ? "warn" : "bad"); }],
  ["Konec (W* ≥ 1,5)", d => { const v = minutes(d.fc_end15_local) - minutes(d.obs_end_local); return pill(d.fc_end15_local || "–", Math.abs(v) <= 45 ? "ok" : Math.abs(v) <= 90 ? "warn" : "bad"); }],
], igcDays);
table("satTable", [
  ["Den", d => dayLabel(d.day)],
  ["Odchylka", d => { const v = num(d.sat_bias); return pill((v > 0 ? "+" : "−") + fmt(Math.abs(v)) + " W/m²", Math.abs(v) < 40 ? "ok" : Math.abs(v) < 70 ? "warn" : "bad"); }],
  ["r hodinově", d => fmt(num(d.sat_r), 2)],
  ["r prostorově", d => { const v = num(d.sat_spatial_r); return pill(fmt(v, 2), v >= 0.5 ? "ok" : v >= 0.3 ? "warn" : "bad"); }],
  ["Energie předpověď", d => fmt(num(d.energy_fc), 2) + " kWh/m²"],
  ["Energie satelit", d => fmt(num(d.energy_obs), 2) + " kWh/m²"],
  ["W* max (průměr ČR)", d => fmt(num(d.fc_wstar_max_hour_mean), 1) + " m/s"],
  ["Dostup 13 h (průměr ČR)", d => fmt(num(d.fc_ceiling_13h_mean)) + " m"],
], days);

function drawAll() { drawEnergy(); drawHourly(); drawScatter(); }
drawAll();
let rt; addEventListener("resize", () => { clearTimeout(rt); rt = setTimeout(drawAll, 150); });
matchMedia("(prefers-color-scheme: dark)").addEventListener("change", drawAll);
</script>
</body>
</html>
""";
}
