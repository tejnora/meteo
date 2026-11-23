# Meteo ČR – plachtařská předpověď

Knihovna `Meteo.Core` (.NET 10) stáhne data modelů DWD ICON, převzorkuje je na mřížku 5 × 5 km nad ČR (UTM 33N)
a vyrobí **vektorové** vrstvy (polygony izoploch, šipky větru) pro denní hodiny (výchozí 08–19 místního času) na 5 dní.
`Meteo.Viewer` je WPF prohlížeč. Výstup jde exportovat do GeoJSON (WGS84) pro budoucí web.

## Struktura

| Projekt | Obsah |
|---|---|
| `src/Meteo.Core` | DLL: GRIB2 dekodér (simple packing + CCSDS/AEC), DWD klient, převzorkování, interpolace větru, izoplochy, styly, cache, GeoJSON, `ForecastService` |
| `src/Meteo.Viewer` | WPF: mapa (zoom/posun), výběr dne, hodiny a vrstvy, legenda, hodnota pod kurzorem, export |
| `tools/Meteo.GeoPrep` | Jednorázová příprava reliéfu a hranic → `src/Meteo.Core/Resources/*.geojson` (vložené do DLL) |
| `tests/Meteo.Core.Tests` | xUnit testy (vč. reálných vzorků GRIB z DWD) |

## Spuštění

```
dotnet test
dotnet run --project src/Meteo.Viewer       # pak „Aktualizovat předpověď“
dotnet run --project tools/Meteo.GeoPrep    # jen při změně reliéfu/hranic (stáhne ~100 MB DEM)
```

## Použití knihovny

```csharp
using var svc = new ForecastService();          // ForecastOptions: výšky větru, denní hodiny, cache…
if (!svc.LoadCached()) await svc.UpdateAsync(progress);
var day = svc.AvailableDays[0];
VectorLayer layer = svc.GetLayer(LayerKind.Wind(1500), day, 13)!;
string geojson = ForecastService.ToGeoJson(layer);
```

## Data a zpracování

- **Den 1–2: ICON-D2** (2,2 km, do +48 h), **den 3–5: ICON-EU** (6,5 km; do +78 h po hodině, dál jen po 3 h – dny 4–5 mají termíny cca 08, 11, 14, 17 h a záření/srážky jsou průměrem 3 h). ICON-D2 nepokrývá východ mřížky
  (~18,4° v. d. a dál, tj. i část Moravy) – tyto buňky se doplňují z ICON-EU pro stejný čas.
- Vrstvy: oblačnost nízká/střední/vysoká (`clcl/clcm/clch`, plošný průměr), přeháňky (hodinová srážka `tot_prec`
  tam, kde `ww` hlásí přeháňky/bouřky; bouřky jako obrys), vítr 10 m a ve výškách 500–3000 m n. m.
  (modelové hladiny + `hhl`, pod terénem se nevykresluje).
- Pro vítr se berou modelové hladiny s rozestupem ≥ 250 m (u země hustší). Proti všem hladinám je odchylka
  RMS ≤ 1,8 km/h a stahuje se zhruba polovina dat. `ForecastOptions.MinLevelSpacing = 0` = všechny hladiny.
- Cache: `%LOCALAPPDATA%\Meteo\cache` (jen zpracovaná 5km pole, GRIB se neukládá).

## Licence dat

DWD Open Data (ICON) – uvádět zdroj „Deutscher Wetterdienst“. Copernicus GLO-90 DEM © DLR e.V. / Airbus, poskytnuto
v rámci programu Copernicus. Hranice: Natural Earth (public domain).
