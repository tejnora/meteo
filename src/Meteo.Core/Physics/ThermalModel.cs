using Meteo.Core.Geo;
using Meteo.Core.Model;

namespace Meteo.Core.Physics;

/// <summary>Thermal diagnostics of one hour. Fine fields are on <see cref="Grid"/> (1 km), convergence on <see cref="CoarseGrid"/>.</summary>
public sealed class ThermalFields
{
    public required GridDefinition Grid { get; init; }
    public required GridDefinition CoarseGrid { get; init; }
    /// <summary>Convective velocity scale W* (m/s) – typical climb of the thermal core before glider sink.</summary>
    public required float[] WStar { get; init; }
    /// <summary>Top of dry thermals (m AMSL).</summary>
    public required float[] Top { get; init; }
    /// <summary>Cumulus base (m AMSL), NaN where thermals are blue.</summary>
    public required float[] CloudBase { get; init; }
    /// <summary>Usable thermal height (m AMSL): cloud base where cumulus forms, otherwise the dry top. NaN = no usable thermals.</summary>
    public required float[] Ceiling { get; init; }
    /// <summary>1 = cumulus thermals, 0 = blue thermals (smooth transition 0..1).</summary>
    public required float[] Cumulus { get; init; }
    /// <summary>Risk (0..1) that cumulus overdevelop into showers / thunderstorms.</summary>
    public required float[] Overdevelopment { get; init; }
    /// <summary>Risk (0..1) that cumulus spread out into a stratocumulus deck and shade the ground.</summary>
    public required float[] Spreading { get; init; }
    /// <summary>Lee (sink) intensity 0..1 behind terrain obstacles for the boundary-layer wind.</summary>
    public required float[] Lee { get; init; }
    /// <summary>Boundary-layer convergence (1/s, positive = convergence) on the coarse grid.</summary>
    public required float[] Convergence { get; init; }
    /// <summary>Surface sensible heat flux after slope / land-cover downscaling (W/m²).</summary>
    public required float[] HeatFlux { get; init; }
    /// <summary>Solar energy reaching the terrain (W/m², mean of the step): direct + diffuse after cloud attenuation, on the real slope.</summary>
    public required float[] Radiation { get; init; }
    /// <summary>Length of the averaging step of the fluxes (1 h, or 3 h for ICON-EU beyond +78 h).</summary>
    public required int StepHours { get; init; }
}

/// <summary>
/// Downscales model fields to 1 km and diagnoses thermals:
/// <list type="number">
/// <item>Sun on the slope: direct radiation is re-projected from the horizontal onto the mean terrain normal of each 1 km cell
/// (aspect + inclination), diffuse radiation is weighted by the sky view; cloud shading is already in the model radiation.</item>
/// <item>Surface: absorbed energy uses land-cover albedo; the share that heats the air uses a land-cover efficiency
/// (forest &lt; meadow &lt; field &lt; rock/town, water = 0) relative to the 5 km mean, because the model flux already includes the mean land use.</item>
/// <item>Thermal strength: Deardorff W* = (g/θ · H/(ρ c_p) · z_i)^(1/3) with z_i = thermal top − local terrain.</item>
/// <item>Top / base: dry convection top from the model (htop_dc), cumulus base from the model shallow-convection base or the
/// lifting condensation level 125 m·(T−Td); cumulus where the base lies below the thermal top.</item>
/// </list>
/// These are heuristics comparable to BLIPMAP/RASP-style products, not a high-resolution model.
/// </summary>
public sealed class ThermalModel
{
    private const double G = 9.81, Cp = 1005;
    private readonly GridDefinition _coarse;
    private readonly SurfaceData _s;
    private readonly double[] _lat, _lon;          // fine cell centres
    private readonly float[] _alb5, _eff5;          // 5 km box means of fine albedo / efficiency

    public ThermalModel(GridDefinition coarse, SurfaceData surface)
    {
        _coarse = coarse;
        _s = surface;
        var g = surface.Grid;
        int n = g.Nx * g.Ny;
        _lat = new double[n]; _lon = new double[n];
        for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                var (lon, lat) = UtmProjection.Zone33.Inverse(g.CenterX(i), g.CenterY(j));
                _lat[j * g.Nx + i] = lat; _lon[j * g.Nx + i] = lon;
            }
        int r = (int)Math.Round(coarse.CellSize / g.CellSize / 2); // half window → 5 km box
        _alb5 = BoxMean(surface.Albedo, g, r);
        _eff5 = BoxMean(surface.Efficiency, g, r);
    }

    public GridDefinition Grid => _s.Grid;
    public SurfaceData Surface => _s;

    public ThermalFields Compute(ForecastHour hour, IReadOnlyList<int> windHeights)
    {
        var g = _s.Grid;
        int n = g.Nx * g.Ny, nc = _coarse.Nx * _coarse.Ny;
        float[] C(string name) => hour.Get(name) ?? Fill(nc, float.NaN);

        var hsurf = C(FieldNames.ModelTerrain);
        var t2 = C(FieldNames.Temperature2m);
        var td2 = C(FieldNames.Dewpoint2m);
        var mh = C(FieldNames.MixingHeight);
        var htop = C(FieldNames.DryConvTop);
        var cuBaseM = C(FieldNames.CuBase);
        var cuFrac = C(FieldNames.CuFraction);
        var cape = C(FieldNames.Cape);
        var clcl = C(FieldNames.CloudLow);
        var shower = C(FieldNames.ShowerPrecip);
        var thunder = C(FieldNames.Thunder);

        // ---- coarse diagnostics ----
        var topC = new float[nc];
        var baseC = new float[nc];
        var cuC = new float[nc];
        var odC = new float[nc];
        var spC = new float[nc];
        for (int k = 0; k < nc; k++)
        {
            float gnd = hsurf[k];
            float top = float.IsFinite(htop[k]) && htop[k] > gnd + 30 ? htop[k]
                      : float.IsFinite(mh[k]) ? gnd + Math.Min(mh[k], 500) : gnd;   // no dry convection diagnosed → shallow mixing only
            topC[k] = top;

            float lcl = gnd + 125f * Math.Max(0, t2[k] - td2[k]);
            bool modelCu = float.IsFinite(cuBaseM[k]) && cuFrac[k] > 0.2f;
            float bas = modelCu ? cuBaseM[k] : lcl;
            float cu = modelCu ? 1f : Math.Clamp((top - lcl) / 200f, 0f, 1f); // LCL below the thermal top → cumulus
            baseC[k] = bas;
            cuC[k] = cu;

            float od = Math.Clamp((cape[k] - 100f) / 700f, 0f, 1f) * (0.5f + 0.5f * cu);
            if (shower[k] > 0.1f) od = Math.Max(od, 0.7f);
            if (thunder[k] > 0f) od = 1f;
            odC[k] = float.IsFinite(od) ? od : 0f;

            float sp = cu * Math.Clamp((clcl[k] - 30f) / 50f, 0f, 1f);
            spC[k] = float.IsFinite(sp) ? sp : 0f;
        }

        // Boundary-layer mean wind (map oriented) from the 10 m wind and the levels inside the mixed layer.
        var (ubl, vbl) = BoundaryLayerWind(hour, windHeights, hsurf, topC);
        var conv = Convergence(ubl, vbl);

        // ---- fine (1 km) fields ----
        var shf = Up(C(FieldNames.SensibleHeat));
        var dir = Up(C(FieldNames.RadDirect));
        var dif = Up(C(FieldNames.RadDiffuse));
        var t2f = Up(t2);
        var hsurfF = Up(hsurf);
        var topF0 = Up(topC);
        var baseF = Up(baseC);
        var cuF = Up(cuC);
        var odF = Up(odC);
        var spF = Up(spC);
        var uF = Up(ubl);
        var vF = Up(vbl);

        // sun in the middle of the averaging hour of the fluxes
        var sunTime = hour.ValidUtc.AddMinutes(-30 * hour.StepHours);
        var wstar = new float[n];
        var topF = new float[n];
        var ceiling = new float[n];
        var cloudBase = new float[n];
        var heat = new float[n];
        var radiation = new float[n];
        for (int k = 0; k < n; k++)
        {
            float elev = _s.Elevation[k];
            if (!float.IsFinite(elev) || !float.IsFinite(shf[k])) { wstar[k] = topF[k] = ceiling[k] = cloudBase[k] = heat[k] = radiation[k] = float.NaN; continue; }

            var (zen, az) = SolarPosition.Compute(sunTime, _lat[k], _lon[k]);
            double cosZ = Math.Cos(zen * Math.PI / 180);
            double factor = 0;
            double difSlope = Math.Max(0, dif[k]) * (1 + _s.NormalU[k]) / 2; // sky view of the slope
            radiation[k] = (float)difSlope;
            if (cosZ > 0.03)
            {
                var (se, sn, su) = SolarPosition.Vector(zen, az);
                double cosInc = _s.NormalE[k] * se + _s.NormalN[k] * sn + _s.NormalU[k] * su;
                double dni = Math.Min(dir[k] / Math.Max(cosZ, 0.08), 1100);
                double dirSlope = dni * Math.Max(0, cosInc);
                // energy reaching the terrain: model radiation (already reduced by all cloud layers) projected on the slope
                radiation[k] = (float)(dirSlope + difSlope);
                double absSlope = (1 - _s.Albedo[k]) * (dirSlope + difSlope);
                double absFlat = (1 - _alb5[k]) * (dir[k] + dif[k]);
                factor = absFlat > 5 ? Math.Clamp(absSlope / absFlat, 0, 2.5) : 1;
            }
            double effRatio = _eff5[k] > 0.05 ? _s.Efficiency[k] / _eff5[k] : 0;
            double h = Math.Max(0, shf[k]) * factor * effRatio;
            heat[k] = (float)h;

            // thermal top follows the terrain partially (model orography is smooth at 2–7 km)
            float top = topF0[k] + 0.3f * (elev - hsurfF[k]);
            top = Math.Max(top, elev);
            topF[k] = top;
            double zi = top - elev;
            double theta = t2f[k] + 273.15;
            double rho = 1.225 * Math.Exp(-elev / 8500.0);
            double w = zi > 0 && h > 0 ? Math.Cbrt(G / theta * h / (rho * Cp) * zi) : 0;
            wstar[k] = (float)w;

            bool cumulus = cuF[k] >= 0.5f && baseF[k] > elev + 100;
            cloudBase[k] = cumulus ? baseF[k] : float.NaN;
            float ceil = cumulus ? baseF[k] : top;
            ceiling[k] = w >= 0.5 && ceil > elev + 150 ? ceil : float.NaN;
        }

        var lee = LeeIndex(uF, vF);

        return new ThermalFields
        {
            Grid = g, CoarseGrid = _coarse,
            WStar = wstar, Top = topF, CloudBase = cloudBase, Ceiling = ceiling, Cumulus = cuF,
            Overdevelopment = odF, Spreading = spF, Lee = lee, Convergence = conv, HeatFlux = heat, Radiation = radiation,
            StepHours = hour.StepHours,
        };
    }

    /// <summary>Last local hour with usable thermals (W* ≥ threshold and low overdevelopment risk); NaN = none that day.</summary>
    /// <summary>
    /// Daily solar energy reaching the terrain (kWh/m²): sum of step means × step length. With the default daytime
    /// hours 08–19 the steps cover 07:00–19:00 (each value is the mean of the preceding step).
    /// </summary>
    public static float[] DailyEnergy(IEnumerable<ThermalFields> hours)
    {
        float[]? sum = null;
        foreach (var f in hours)
        {
            sum ??= new float[f.Radiation.Length];
            for (int k = 0; k < sum.Length; k++)
                sum[k] += float.IsFinite(f.Radiation[k]) ? f.Radiation[k] * f.StepHours / 1000f : float.NaN;
        }
        return sum ?? Array.Empty<float>();
    }

    public static float[] ThermalEnd(IEnumerable<(int LocalHour, ThermalFields Fields)> hours, float minWStar = 1.0f)
    {
        float[]? end = null;
        foreach (var (hr, f) in hours.OrderBy(h => h.LocalHour))
        {
            end ??= Fill(f.WStar.Length, float.NaN);
            for (int k = 0; k < end.Length; k++)
                if (f.WStar[k] >= minWStar && f.Overdevelopment[k] < 0.6f) end[k] = hr;
        }
        return end ?? Array.Empty<float>();
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private (float[] U, float[] V) BoundaryLayerWind(ForecastHour hour, IReadOnlyList<int> heights, float[] hsurf, float[] top)
    {
        int nc = hsurf.Length;
        var u = new float[nc]; var v = new float[nc];
        var u10 = hour.Get(FieldNames.WindU10); var v10 = hour.Get(FieldNames.WindV10);
        for (int k = 0; k < nc; k++)
        {
            double su = 0, sv = 0; int cnt = 0;
            if (u10 != null && v10 != null && float.IsFinite(u10[k])) { su += u10[k]; sv += v10[k]; cnt++; }
            float upper = Math.Max(top[k], hsurf[k] + 500);
            foreach (var h in heights)
            {
                if (h < hsurf[k] || h > upper) continue;
                var uh = hour.Get(FieldNames.WindU(h)); var vh = hour.Get(FieldNames.WindV(h));
                if (uh == null || vh == null || !float.IsFinite(uh[k])) continue;
                su += uh[k]; sv += vh[k]; cnt++;
            }
            u[k] = cnt > 0 ? (float)(su / cnt) : float.NaN;
            v[k] = cnt > 0 ? (float)(sv / cnt) : float.NaN;
        }
        return (u, v);
    }

    /// <summary>−div(V) by central differences, lightly smoothed (1/s).</summary>
    private float[] Convergence(float[] u, float[] v)
    {
        int nx = _coarse.Nx, ny = _coarse.Ny;
        double d = 2 * _coarse.CellSize;
        var c = Fill(nx * ny, float.NaN);
        for (int j = 1; j < ny - 1; j++)
            for (int i = 1; i < nx - 1; i++)
            {
                int k = j * nx + i;
                double div = (u[k + 1] - u[k - 1]) / d + (v[k + nx] - v[k - nx]) / d;
                c[k] = (float)-div;
            }
        return BoxMean(c, _coarse, 1);
    }

    /// <summary>
    /// Lee index: looking upwind up to 8 km, the steepest angle under which higher terrain shelters the cell,
    /// scaled by wind speed (no lee below 4 m/s, full effect from 12 m/s).
    /// </summary>
    private float[] LeeIndex(float[] u, float[] v)
    {
        var g = _s.Grid;
        int n = g.Nx * g.Ny;
        var lee = new float[n];
        for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                int k = j * g.Nx + i;
                double sp = Math.Sqrt(u[k] * u[k] + v[k] * v[k]);
                float e0 = _s.Elevation[k];
                if (!(sp > 4) || !float.IsFinite(e0)) continue;
                double dx = -u[k] / sp, dy = -v[k] / sp; // upwind direction
                double best = 0;
                for (int s = 1; s <= 8; s++)
                {
                    double dist = s * 1000.0;
                    float e = SampleFine(g.CenterX(i) + dx * dist, g.CenterY(j) + dy * dist);
                    if (!float.IsFinite(e)) break;
                    best = Math.Max(best, (e - e0) / dist);
                }
                lee[k] = (float)(Math.Clamp(best / 0.08, 0, 1) * Math.Clamp((sp - 4) / 8, 0, 1));
            }
        return lee;
    }

    private float SampleFine(double x, double y)
    {
        var g = _s.Grid;
        double fx = (x - g.X0) / g.CellSize - 0.5, fy = (y - g.Y0) / g.CellSize - 0.5;
        int i = (int)Math.Round(fx), j = (int)Math.Round(fy);
        if (i < 0 || j < 0 || i >= g.Nx || j >= g.Ny) return float.NaN;
        return _s.Elevation[j * g.Nx + i];
    }

    /// <summary>Bilinear upsampling coarse → fine; missing corners are skipped.</summary>
    private float[] Up(float[] coarse)
    {
        var g = _s.Grid; var c = _coarse;
        var r = new float[g.Nx * g.Ny];
        for (int j = 0; j < g.Ny; j++)
        {
            double fy = (g.CenterY(j) - c.Y0) / c.CellSize - 0.5;
            int j0 = Math.Clamp((int)Math.Floor(fy), 0, c.Ny - 2);
            double ty = Math.Clamp(fy - j0, 0, 1);
            for (int i = 0; i < g.Nx; i++)
            {
                double fx = (g.CenterX(i) - c.X0) / c.CellSize - 0.5;
                int i0 = Math.Clamp((int)Math.Floor(fx), 0, c.Nx - 2);
                double tx = Math.Clamp(fx - i0, 0, 1);
                double s = 0, w = 0;
                void Add(int ii, int jj, double wt)
                {
                    float val = coarse[jj * c.Nx + ii];
                    if (float.IsFinite(val) && wt > 0) { s += val * wt; w += wt; }
                }
                Add(i0, j0, (1 - tx) * (1 - ty)); Add(i0 + 1, j0, tx * (1 - ty));
                Add(i0, j0 + 1, (1 - tx) * ty); Add(i0 + 1, j0 + 1, tx * ty);
                r[j * g.Nx + i] = w > 0 ? (float)(s / w) : float.NaN;
            }
        }
        return r;
    }

    private static float[] BoxMean(float[] a, GridDefinition g, int r)
    {
        var res = new float[a.Length];
        for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                double s = 0; int n = 0;
                for (int jj = Math.Max(0, j - r); jj <= Math.Min(g.Ny - 1, j + r); jj++)
                    for (int ii = Math.Max(0, i - r); ii <= Math.Min(g.Nx - 1, i + r); ii++)
                    {
                        float v = a[jj * g.Nx + ii];
                        if (float.IsFinite(v)) { s += v; n++; }
                    }
                res[j * g.Nx + i] = n > 0 ? (float)(s / n) : float.NaN;
            }
        return res;
    }

    private static float[] Fill(int n, float v) { var a = new float[n]; Array.Fill(a, v); return a; }
}
