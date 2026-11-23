using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using Meteo.Core.Geo;
using Meteo.Core.Vector;

namespace Meteo.Core.Export;

/// <summary>Exports a <see cref="VectorLayer"/> as GeoJSON (RFC 7946, WGS84 lon/lat) for web clients.</summary>
public static class GeoJsonExporter
{
    private static readonly GeometryFactory Factory = new();

    public static FeatureCollection ToFeatureCollection(VectorLayer layer, bool wgs84 = true)
    {
        var fc = new FeatureCollection();
        // rendering tolerates touching rings; GeoJSON consumers get repaired, valid geometries
        Geometry Tx(Geometry g)
        {
            if (!g.IsValid) g = NetTopologySuite.Geometries.Utilities.GeometryFixer.Fix(g);
            return wgs84 ? ToWgs84(g) : g;
        }

        foreach (var p in layer.Polygons)
            fc.Add(GeoJson.Feature(Tx(p.Geometry), ("kind", "polygon"), ("fill", p.Fill), ("stroke", p.Stroke),
                ("strokeWidth", p.StrokeWidth), ("dashed", p.Dashed), ("value", double.IsNaN(p.Value) ? null : p.Value),
                ("opacity", layer.Opacity)));
        foreach (var l in layer.Lines)
            fc.Add(GeoJson.Feature(Tx(l.Geometry), ("kind", "line"), ("stroke", l.Stroke), ("strokeWidth", l.Width), ("dashed", l.Dashed)));
        foreach (var a in layer.Arrows)
            fc.Add(GeoJson.Feature(Tx(Factory.CreatePoint(new Coordinate(a.X, a.Y))), ("kind", "wind"),
                ("speedKmh", Math.Round(a.SpeedKmh, 1)), ("fromDeg", Math.Round(a.FromDirection, 0)), ("u", a.U), ("v", a.V)));
        return fc;
    }

    public static string ToGeoJson(VectorLayer layer, bool wgs84 = true, bool indented = false) =>
        GeoJson.Write(ToFeatureCollection(layer, wgs84), indented);

    private static Geometry ToWgs84(Geometry g)
    {
        var copy = g.Copy();
        copy.Apply(new InverseFilter());
        copy.GeometryChanged();
        return copy;
    }

    private sealed class InverseFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;
        public void Filter(CoordinateSequence seq, int i)
        {
            var (lon, lat) = UtmProjection.Zone33.Inverse(seq.GetX(i), seq.GetY(i));
            seq.SetX(i, Math.Round(lon, 5));
            seq.SetY(i, Math.Round(lat, 5));
        }
    }
}
