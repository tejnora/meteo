using Meteo.Core.Geo;
using Meteo.Core.Physics;
using Meteo.Core.Vector;
using Xunit;

namespace Meteo.Core.Tests;

public class FlightPlanTests
{
    private const int N = 12; // 12 × 12 km, symbol lattice 6 km → 2 × 2 symbols

    private static ThermalFields Fields(float wstar = 2.5f, float ceiling = 2000, float cumulus = 1, float spreading = 0, float overdev = 0, float lee = 0,
        float cover = 0.3f, float storm = 0)
    {
        var g = new GridDefinition(0, 0, 1000, N, N);
        float[] F(float v) => Enumerable.Repeat(v, N * N).ToArray();
        return new ThermalFields
        {
            Grid = g, CoarseGrid = new GridDefinition(0, 0, 6000, 2, 2),
            WStar = F(wstar), Top = F(ceiling), CloudBase = F(ceiling), Ceiling = F(ceiling), Cumulus = F(cumulus),
            Overdevelopment = F(overdev), Spreading = F(spreading), Lee = F(lee), CumulusCover = F(cover), Storm = F(storm),
            Convergence = new float[4], HeatFlux = F(200), Radiation = F(700), StepHours = 1,
        };
    }

    private static float[] Elevation(float e = 400) => Enumerable.Repeat(e, N * N).ToArray();

    [Fact]
    public void QualityIsWStarWithoutPenalties()
    {
        Assert.Equal(2.5f, LayerBuilder.FlightQualityAt(Fields(), 0, 400), 3);
    }

    [Fact]
    public void LowCeilingLeeAndSpreadingReduceQuality()
    {
        float good = LayerBuilder.FlightQualityAt(Fields(), 0, 400);
        Assert.True(LayerBuilder.FlightQualityAt(Fields(ceiling: 800), 0, 400) < good);   // 400 m usable height
        Assert.True(LayerBuilder.FlightQualityAt(Fields(lee: 0.75f), 0, 400) < good);
        Assert.True(LayerBuilder.FlightQualityAt(Fields(spreading: 0.8f), 0, 400) < good);
        Assert.Equal(0, LayerBuilder.FlightQualityAt(Fields(ceiling: float.NaN), 0, 400));
    }

    [Theory]
    [InlineData(1f, 0f, 0f, SymbolKind.Cumulus)]
    [InlineData(1f, 0.7f, 0f, SymbolKind.Stratocumulus)]
    [InlineData(1f, 0.7f, 0.8f, SymbolKind.Congestus)]      // overdevelopment risk wins over spreading
    public void SymbolsFollowTheDominantCloudType(float cumulus, float spreading, float overdev, SymbolKind expected)
    {
        var layer = new LayerBuilder(new GridDefinition(0, 0, 6000, 2, 2)).BuildFlightPlan(Fields(cumulus: cumulus, spreading: spreading, overdev: overdev), Elevation(), "");
        Assert.Equal(4, layer.Symbols.Count);
        Assert.All(layer.Symbols, s => Assert.Equal(expected, s.Kind));
        Assert.Equal(6000, layer.SymbolSpacing);
    }

    [Fact]
    public void BlueThermalsGetTheBlueSymbol()
    {
        var layer = new LayerBuilder(new GridDefinition(0, 0, 6000, 2, 2)).BuildFlightPlan(Fields(cumulus: 0), Elevation(), "");
        Assert.All(layer.Symbols, s => Assert.Equal(SymbolKind.Blue, s.Kind));
        Assert.Empty(new LayerBuilder(new GridDefinition(0, 0, 6000, 2, 2)).BuildFlightPlan(Fields(cumulus: 0, ceiling: float.NaN), Elevation(), "").Symbols);
        Assert.NotEmpty(layer.Polygons); // quality fill is still there
    }

    [Theory]
    [InlineData(0.7f, 1)]   // showers
    [InlineData(1f, 2)]     // thunderstorm
    public void ModelPrecipitationGivesCumulonimbus(float storm, int amount)
    {
        var layer = new LayerBuilder(new GridDefinition(0, 0, 6000, 2, 2)).BuildFlightPlan(Fields(overdev: 0.8f, storm: storm), Elevation(), "");
        Assert.All(layer.Symbols, s => { Assert.Equal(SymbolKind.Cumulonimbus, s.Kind); Assert.Equal(amount, s.Amount); });
    }

    [Theory]
    [InlineData(0.1f, 1)]
    [InlineData(0.3f, 2)]
    [InlineData(0.5f, 3)]
    public void CumulusAmountFollowsCover(float cover, int amount)
    {
        var layer = new LayerBuilder(new GridDefinition(0, 0, 6000, 2, 2)).BuildFlightPlan(Fields(cover: cover), Elevation(), "");
        Assert.All(layer.Symbols, s => { Assert.Equal(SymbolKind.Cumulus, s.Kind); Assert.Equal(amount, s.Amount); });
    }
}
