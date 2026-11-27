using Meteo.Core.Geo;
using Meteo.Core.Model;
using Meteo.Core.Physics;

namespace Meteo.Core.Tests;

public class SolarPositionTests
{
    [Fact]
    public void SummerSolsticeNoonInPragueHasExpectedElevation()
    {
        // Max elevation at 50.09° N on 21 June ≈ 90 − 50.09 + 23.44 = 63.35°, sun due south.
        double best = 90, bestAz = 0;
        for (int m = 9 * 60; m < 13 * 60; m++)
        {
            var (z, az) = SolarPosition.Compute(new DateTime(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc).AddMinutes(m), 50.09, 14.42);
            if (z < best) { best = z; bestAz = az; }
        }
        Assert.InRange(90 - best, 63.0, 63.7);
        Assert.InRange(bestAz, 178, 182);
    }

    [Fact]
    public void MorningSunIsInTheEast()
    {
        var (z, az) = SolarPosition.Compute(new DateTime(2026, 6, 21, 5, 0, 0, DateTimeKind.Utc), 50.09, 14.42);
        Assert.InRange(az, 60, 110);
        Assert.InRange(90 - z, 15, 35);
    }
}

public class ThermalModelTests
{
    // Coarse grid 4x4 cells of 5 km, fine grid 20x20 cells of 1 km over the same area.
    // placed over central Bohemia so the sun position is realistic
    private static readonly GridDefinition Coarse = new(450_000, 5_540_000, 5000, 4, 4);
    private static readonly GridDefinition Fine = new(450_000, 5_540_000, 1000, 20, 20);

    private static SurfaceData Surface(Func<int, int, (float Elev, float NE, float NN, float NU)> terrain, Func<int, int, float>? eff = null)
    {
        int n = Fine.Nx * Fine.Ny;
        var s = new SurfaceData
        {
            Grid = Fine, Elevation = new float[n], NormalE = new float[n], NormalN = new float[n], NormalU = new float[n],
            Albedo = Enumerable.Repeat(0.2f, n).ToArray(), Efficiency = new float[n], Water = new float[n],
        };
        for (int j = 0; j < Fine.Ny; j++)
            for (int i = 0; i < Fine.Nx; i++)
            {
                int k = j * Fine.Nx + i;
                var t = terrain(i, j);
                s.Elevation[k] = t.Elev; s.NormalE[k] = t.NE; s.NormalN[k] = t.NN; s.NormalU[k] = t.NU;
                s.Efficiency[k] = eff?.Invoke(i, j) ?? 1f;
            }
        return s;
    }

    private static ForecastHour Hour(Action<Dictionary<string, float[]>>? tweak = null)
    {
        int n = Coarse.Nx * Coarse.Ny;
        float[] F(float v) => Enumerable.Repeat(v, n).ToArray();
        var f = new Dictionary<string, float[]>
        {
            [FieldNames.ModelTerrain] = F(300), [FieldNames.Temperature2m] = F(24), [FieldNames.Dewpoint2m] = F(8),
            [FieldNames.MixingHeight] = F(1500), [FieldNames.DryConvTop] = F(1800), [FieldNames.CuBase] = F(float.NaN),
            [FieldNames.CuFraction] = F(0), [FieldNames.Cape] = F(0), [FieldNames.CloudLow] = F(0),
            [FieldNames.ShowerPrecip] = F(0), [FieldNames.Thunder] = F(0),
            [FieldNames.SensibleHeat] = F(200), [FieldNames.RadDirect] = F(600), [FieldNames.RadDiffuse] = F(120),
            [FieldNames.WindU10] = F(0), [FieldNames.WindV10] = F(0),
        };
        tweak?.Invoke(f);
        // noon in summer: sun from the south, ~60° high
        return new ForecastHour { ValidUtc = new DateTime(2026, 6, 21, 11, 30, 0, DateTimeKind.Utc), Model = "test", RunUtc = default, Fields = f };
    }

    private static readonly float Flat = 0f;

    [Fact]
    public void SouthFacingSlopeGetsStrongerThermalsThanNorthFacing()
    {
        // left half tilted 25° to the south, right half 25° to the north
        float s = (float)Math.Sin(25 * Math.PI / 180), c = (float)Math.Cos(25 * Math.PI / 180);
        var surf = Surface((i, j) => i < 10 ? (400f, 0f, -s, c) : (400f, 0f, s, c));
        var f = new ThermalModel(Coarse, surf).Compute(Hour(), Array.Empty<int>());
        float south = f.WStar[10 * Fine.Nx + 3], north = f.WStar[10 * Fine.Nx + 16];
        Assert.True(south > north * 1.1, $"south {south} north {north}");
        Assert.True(f.HeatFlux[10 * Fine.Nx + 3] > f.HeatFlux[10 * Fine.Nx + 16]);
    }

    [Fact]
    public void ForestAndWaterReduceThermalsComparedToFields()
    {
        // fields (1.0) with a 2 km forest strip (0.6) and a 1 km pond (0); effects are relative to the 5 km surroundings
        var surf = Surface((i, j) => (400f, Flat, Flat, 1f), (i, j) => i == 4 ? 0f : i is 10 or 11 ? 0.6f : 1f);
        var f = new ThermalModel(Coarse, surf).Compute(Hour(), Array.Empty<int>());
        float water = f.WStar[10 * Fine.Nx + 4], forest = f.WStar[10 * Fine.Nx + 10], field = f.WStar[10 * Fine.Nx + 16];
        Assert.Equal(0f, water);
        Assert.True(field > forest && forest > 0, $"field {field} forest {forest}");
    }

    [Fact]
    public void WStarMatchesDeardorffFormulaOnFlatUniformTerrain()
    {
        var surf = Surface((i, j) => (300f, Flat, Flat, 1f));
        var f = new ThermalModel(Coarse, surf).Compute(Hour(), Array.Empty<int>());
        int k = 10 * Fine.Nx + 10;
        double zi = 1800 - 300, theta = 24 + 273.15, rho = 1.225 * Math.Exp(-300 / 8500.0);
        double expected = Math.Cbrt(9.81 / theta * 200 / (rho * 1005) * zi);
        Assert.Equal(expected, f.WStar[k], 1);   // ≈ 2.1 m/s
        Assert.Equal(1800f, f.Top[k], 0);
    }

    [Fact]
    public void DryAirGivesBlueThermalsMoistAirGivesCumulus()
    {
        var surf = Surface((i, j) => (300f, Flat, Flat, 1f));
        var model = new ThermalModel(Coarse, surf);
        int k = 10 * Fine.Nx + 10;

        // T−Td = 16 K → LCL 300 + 2000 = 2300 m above the 1800 m thermal top → blue
        var blue = model.Compute(Hour(), Array.Empty<int>());
        Assert.True(float.IsNaN(blue.CloudBase[k]));
        Assert.Equal(1800f, blue.Ceiling[k], 0);

        // T−Td = 8 K → LCL 1300 m below the top → cumulus with base ≈ 1300 m
        var cu = model.Compute(Hour(d => d[FieldNames.Dewpoint2m] = Enumerable.Repeat(16f, 16).ToArray()), Array.Empty<int>());
        Assert.Equal(1300f, cu.CloudBase[k], 0);
        Assert.Equal(1300f, cu.Ceiling[k], 0);
    }

    [Fact]
    public void ModelCloudBaseFarAboveTheThermalsIsNotACumulusBase()
    {
        var surf = Surface((i, j) => (300f, Flat, Flat, 1f));
        var model = new ThermalModel(Coarse, surf);
        int k = 10 * Fine.Nx + 10;
        int n = Coarse.Nx * Coarse.Ny;

        // elevated convection with a base at 9 km: dry air (LCL above the top) → blue thermals up to the 1800 m top
        var high = model.Compute(Hour(d =>
        {
            d[FieldNames.CuBase] = Enumerable.Repeat(9000f, n).ToArray();
            d[FieldNames.CuFraction] = Enumerable.Repeat(1f, n).ToArray();
        }), Array.Empty<int>());
        Assert.True(float.IsNaN(high.CloudBase[k]));
        Assert.Equal(1800f, high.Ceiling[k], 0);

        // a model base just below the thermal top is a real cumulus base
        var cu = model.Compute(Hour(d =>
        {
            d[FieldNames.CuBase] = Enumerable.Repeat(1700f, n).ToArray();
            d[FieldNames.CuFraction] = Enumerable.Repeat(1f, n).ToArray();
        }), Array.Empty<int>());
        Assert.Equal(1700f, cu.CloudBase[k], 0);
    }

    [Fact]
    public void LeeAppearsDownwindOfARidge()
    {
        // ridge 600 m high at column 8, wind from the west (blowing east, +x) at 10 m/s
        var surf = Surface((i, j) => (i == 8 ? 900f : 300f, Flat, Flat, 1f));
        var hour = Hour(d =>
        {
            d[FieldNames.WindU10] = Enumerable.Repeat(10f, 16).ToArray();
            d[FieldNames.WindV10] = Enumerable.Repeat(0f, 16).ToArray();
        });
        var f = new ThermalModel(Coarse, surf).Compute(hour, Array.Empty<int>());
        float leeSide = f.Lee[10 * Fine.Nx + 10], windward = f.Lee[10 * Fine.Nx + 6];
        Assert.True(leeSide > 0.5f, $"lee {leeSide}");
        Assert.Equal(0f, windward);
    }

    [Fact]
    public void ConvergingWindIsDetected()
    {
        var surf = Surface((i, j) => (300f, Flat, Flat, 1f));
        var hour = Hour(d =>
        {
            // westerly on the left, easterly on the right → convergence in the middle
            d[FieldNames.WindU10] = Enumerable.Range(0, 16).Select(k => k % 4 < 2 ? 5f : -5f).ToArray();
            d[FieldNames.WindV10] = Enumerable.Repeat(0f, 16).ToArray();
        });
        var f = new ThermalModel(Coarse, surf).Compute(hour, Array.Empty<int>());
        Assert.True(f.Convergence[1 * Coarse.Nx + 1] > 0.5e-3f, $"conv {f.Convergence[5]}");
    }
}

public class SolarEnergyTests
{
    private static readonly GridDefinition Coarse = new(450_000, 5_540_000, 5000, 4, 4);
    private static readonly GridDefinition Fine = new(450_000, 5_540_000, 1000, 20, 20);

    private static ThermalFields Compute(float tiltSouthDeg, float direct, float diffuse, string model = "ICON-D2", int lead = 12)
    {
        int n = Fine.Nx * Fine.Ny;
        float s = (float)Math.Sin(tiltSouthDeg * Math.PI / 180), c = (float)Math.Cos(tiltSouthDeg * Math.PI / 180);
        var surf = new SurfaceData
        {
            Grid = Fine, Elevation = Enumerable.Repeat(400f, n).ToArray(), NormalE = new float[n],
            NormalN = Enumerable.Repeat(-s, n).ToArray(), NormalU = Enumerable.Repeat(c, n).ToArray(),
            Albedo = Enumerable.Repeat(0.2f, n).ToArray(), Efficiency = Enumerable.Repeat(1f, n).ToArray(), Water = new float[n],
        };
        float[] F(float v) => Enumerable.Repeat(v, 16).ToArray();
        var valid = new DateTime(2026, 6, 21, 11, 30, 0, DateTimeKind.Utc);
        var hour = new ForecastHour
        {
            ValidUtc = valid, RunUtc = valid.AddHours(-lead), Model = model,
            Fields =
            {
                [FieldNames.ModelTerrain] = F(400), [FieldNames.Temperature2m] = F(20), [FieldNames.Dewpoint2m] = F(5),
                [FieldNames.MixingHeight] = F(1500), [FieldNames.DryConvTop] = F(1800), [FieldNames.SensibleHeat] = F(150),
                [FieldNames.RadDirect] = F(direct), [FieldNames.RadDiffuse] = F(diffuse),
            },
        };
        return new ThermalModel(Coarse, surf).Compute(hour, Array.Empty<int>());
    }

    [Fact]
    public void FlatTerrainReceivesModelRadiation()
    {
        var f = Compute(0, 600, 150);
        Assert.Equal(750f, f.Radiation[210], 0); // direct + diffuse, clouds already included by the model
    }

    [Fact]
    public void OvercastGivesOnlyDiffuseAndSouthSlopeGetsMoreSun()
    {
        Assert.Equal(120f, Compute(0, 0, 120).Radiation[210], 0);
        Assert.True(Compute(30, 600, 150).Radiation[210] > Compute(0, 600, 150).Radiation[210]);
        Assert.True(Compute(-30, 600, 150).Radiation[210] < Compute(0, 600, 150).Radiation[210]);
    }

    [Fact]
    public void DailyEnergySumsStepsTimesLength()
    {
        var hourly = Compute(0, 600, 150);                        // 750 W/m² for 1 h
        var threeHourly = Compute(0, 600, 150, "ICON-EU", 84);    // 750 W/m² for 3 h
        Assert.Equal(3, threeHourly.StepHours);
        var kwh = ThermalModel.DailyEnergy(new[] { hourly, threeHourly });
        Assert.Equal(0.75f * 4, kwh[210], 2);
    }
}
