using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;

namespace Meteo.Core.Export;

/// <summary>GeoJSON (de)serialisation helpers based on NetTopologySuite.IO.GeoJSON4STJ.</summary>
public static class GeoJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(false);
    public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(true);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var o = new JsonSerializerOptions { WriteIndented = indented };
        o.Converters.Add(new GeoJsonConverterFactory());
        return o;
    }

    public static FeatureCollection ReadFeatures(Stream s) =>
        JsonSerializer.Deserialize<FeatureCollection>(s, Options) ?? new FeatureCollection();

    public static FeatureCollection ReadFeatures(string json) =>
        JsonSerializer.Deserialize<FeatureCollection>(json, Options) ?? new FeatureCollection();

    public static string Write(FeatureCollection fc, bool indented = false) =>
        JsonSerializer.Serialize(fc, indented ? IndentedOptions : Options);

    public static void Write(FeatureCollection fc, Stream s) => JsonSerializer.Serialize(s, fc, Options);

    public static Feature Feature(Geometry g, params (string Key, object? Value)[] props)
    {
        var t = new AttributesTable();
        foreach (var (k, v) in props) t.Add(k, v);
        return new Feature(g, t);
    }
}
