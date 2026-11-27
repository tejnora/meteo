using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Meteo.Core.Vector;

namespace Meteo.Viewer.Map;

/// <summary>
/// Vector map: relief, meteo layer, borders and wind arrows. World coordinates are UTM metres (y up).
/// Filled layers are drawn once in world coordinates and moved by a transform; strokes and arrows,
/// whose size is in screen pixels, are redrawn when the view changes.
/// Mouse: wheel = zoom, drag = pan, double click = fit.
/// </summary>
public sealed class MapView : FrameworkElement
{
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _relief = new(), _meteo = new(), _borders = new(), _symbols = new(), _track = new(), _arrows = new();
    private double _scale = double.NaN; // pixels per metre
    private Point _center;              // world point at the control centre
    private Point? _dragStart;
    private Point _dragCenter;

    public MapView()
    {
        _visuals = new VisualCollection(this) { _relief, _meteo, _borders, _symbols, _track, _arrows };
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => { if (double.IsNaN(_scale)) Fit(); else ViewChanged(zoomChanged: false); };
    }

    // ---- dependency properties ----------------------------------------------------------------------

    public static readonly DependencyProperty ReliefLayerProperty = Dp(nameof(ReliefLayer), (m, _) => m.RenderRelief());
    public static readonly DependencyProperty MeteoLayerProperty = Dp(nameof(MeteoLayer), (m, _) => { m.RenderMeteo(); m.RenderSymbols(); m.RenderArrows(); });
    public static readonly DependencyProperty BorderLayerProperty = Dp(nameof(BorderLayer), (m, _) => m.RenderBorders());
    public static readonly DependencyProperty TrackLayerProperty = Dp(nameof(TrackLayer), (m, _) => m.RenderTrack());
    public static readonly DependencyProperty ShowArrowsProperty = DependencyProperty.Register(nameof(ShowArrows), typeof(bool), typeof(MapView),
        new PropertyMetadata(true, (d, _) => ((MapView)d).RenderArrows()));
    public static readonly DependencyProperty WorldExtentProperty = DependencyProperty.Register(nameof(WorldExtent), typeof(Rect), typeof(MapView),
        new PropertyMetadata(Rect.Empty, (d, _) => ((MapView)d).Fit()));
    public static readonly DependencyProperty PointerWorldProperty = DependencyProperty.Register(nameof(PointerWorld), typeof(Point?), typeof(MapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public VectorLayer? ReliefLayer { get => (VectorLayer?)GetValue(ReliefLayerProperty); set => SetValue(ReliefLayerProperty, value); }
    public VectorLayer? MeteoLayer { get => (VectorLayer?)GetValue(MeteoLayerProperty); set => SetValue(MeteoLayerProperty, value); }
    public VectorLayer? BorderLayer { get => (VectorLayer?)GetValue(BorderLayerProperty); set => SetValue(BorderLayerProperty, value); }
    /// <summary>Flight track drawn above the meteo layer (line widths in pixels).</summary>
    public VectorLayer? TrackLayer { get => (VectorLayer?)GetValue(TrackLayerProperty); set => SetValue(TrackLayerProperty, value); }
    public bool ShowArrows { get => (bool)GetValue(ShowArrowsProperty); set => SetValue(ShowArrowsProperty, value); }
    /// <summary>World rectangle shown on "fit" (UTM metres).</summary>
    public Rect WorldExtent { get => (Rect)GetValue(WorldExtentProperty); set => SetValue(WorldExtentProperty, value); }
    /// <summary>World coordinate under the mouse pointer (null when outside).</summary>
    public Point? PointerWorld { get => (Point?)GetValue(PointerWorldProperty); set => SetValue(PointerWorldProperty, value); }

    private static DependencyProperty Dp(string name, Action<MapView, DependencyPropertyChangedEventArgs> changed) =>
        DependencyProperty.Register(name, typeof(VectorLayer), typeof(MapView),
            new PropertyMetadata(null, (d, e) => changed((MapView)d, e)));

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    // ---- view transform -------------------------------------------------------------------------------

    private Matrix WorldToScreen
    {
        get
        {
            var m = Matrix.Identity;
            m.Translate(-_center.X, -_center.Y);
            m.Scale(_scale, -_scale);
            m.Translate(ActualWidth / 2, ActualHeight / 2);
            return m;
        }
    }

    private Point ScreenToWorld(Point p) =>
        new(_center.X + (p.X - ActualWidth / 2) / _scale, _center.Y - (p.Y - ActualHeight / 2) / _scale);

    public void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || WorldExtent.IsEmpty) return;
        var e = WorldExtent;
        _scale = Math.Min(ActualWidth / e.Width, ActualHeight / e.Height) * 0.98;
        _center = new Point(e.X + e.Width / 2, e.Y + e.Height / 2);
        ViewChanged(zoomChanged: true);
    }

    private double FitScale => WorldExtent.IsEmpty ? 1 : Math.Min(ActualWidth / WorldExtent.Width, ActualHeight / WorldExtent.Height);

    private void ViewChanged(bool zoomChanged)
    {
        if (double.IsNaN(_scale)) return;
        var t = new MatrixTransform(WorldToScreen);
        t.Freeze();
        _relief.Transform = t;
        _meteo.Transform = t;
        _borders.Transform = t;
        _track.Transform = t;
        if (zoomChanged) { RenderMeteo(); RenderBorders(); RenderTrack(); }
        RenderSymbols();
        RenderArrows();
    }

    // ---- rendering ---------------------------------------------------------------------------------------

    private void RenderRelief()
    {
        using var dc = _relief.RenderOpen();
        DrawFills(dc, ReliefLayer);
    }

    private void RenderMeteo()
    {
        using var dc = _meteo.RenderOpen();
        var layer = MeteoLayer;
        if (layer is null || double.IsNaN(_scale)) return;
        _meteo.Opacity = layer.Opacity;
        DrawFills(dc, layer);
        foreach (var p in layer.Polygons.Where(p => p.Hatch != null))
            dc.DrawGeometry(HatchBrush(p.Hatch!, p.HatchSpacing, p.HatchBackslash), null, NtsToWpf.Convert(p.Geometry));
        foreach (var p in layer.Polygons.Where(p => p.Stroke != null))
        {
            var pen = new Pen(NtsToWpf.Brush(p.Stroke!), p.StrokeWidth / _scale);
            if (p.Dashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            dc.DrawGeometry(null, pen, NtsToWpf.Convert(p.Geometry));
        }
    }

    /// <summary>Diagonal hatching with a constant on-screen spacing (the meteo visual is drawn in world metres).</summary>
    private Brush HatchBrush(string color, double spacingPx, bool backslash)
    {
        double tile = spacingPx / _scale;
        var pen = new Pen(NtsToWpf.Brush(color), 1.4 / _scale);
        var lines = new GeometryGroup();
        // diagonal across the tile plus the two corner pieces so neighbouring tiles join seamlessly;
        // (backslash = the mirrored direction; the world y axis points up, so the direction on screen is flipped)
        Point P(double x, double y) => backslash ? new Point(x, tile - y) : new Point(x, y);
        lines.Children.Add(new LineGeometry(P(0, tile), P(tile, 0)));
        lines.Children.Add(new LineGeometry(P(-tile / 2, tile / 2), P(tile / 2, -tile / 2)));
        lines.Children.Add(new LineGeometry(P(tile / 2, tile * 1.5), P(tile * 1.5, tile / 2)));
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, lines))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, tile, tile), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, tile, tile), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private static void DrawFills(DrawingContext dc, VectorLayer? layer)
    {
        if (layer is null) return;
        foreach (var p in layer.Polygons)
            if (p.Fill != null) dc.DrawGeometry(NtsToWpf.Brush(p.Fill), null, NtsToWpf.Convert(p.Geometry));
    }

    private void RenderBorders()
    {
        using var dc = _borders.RenderOpen();
        var layer = BorderLayer;
        if (layer is null || double.IsNaN(_scale)) return;
        foreach (var l in layer.Lines)
        {
            var geom = NtsToWpf.Convert(l.Geometry);
            var halo = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), (l.Width + 2) / _scale);
            halo.Freeze();
            var pen = new Pen(NtsToWpf.Brush(l.Stroke), l.Width / _scale) { LineJoin = PenLineJoin.Round };
            if (l.Dashed) pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            pen.Freeze();
            if (!l.Dashed) dc.DrawGeometry(null, halo, geom);
            dc.DrawGeometry(null, pen, geom);
        }
    }

    private void RenderTrack()
    {
        using var dc = _track.RenderOpen();
        var layer = TrackLayer;
        if (layer is null || double.IsNaN(_scale)) return;
        foreach (var l in layer.Lines)
        {
            var pen = new Pen(NtsToWpf.Brush(l.Stroke), l.Width / _scale) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, NtsToWpf.Convert(l.Geometry));
        }
    }

    private static readonly Pen ArrowPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(25, 25, 25)), 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static readonly Pen ArrowHalo = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static Pen Frozen(Pen p) { p.Freeze(); return p; }

    private void RenderArrows()
    {
        using var dc = _arrows.RenderOpen();
        var layer = MeteoLayer;
        if (layer is null || !ShowArrows || layer.Arrows.Count == 0 || double.IsNaN(_scale)) return;

        var m = WorldToScreen;
        // Thin arrows out when zoomed out so they do not overlap.
        double spacingPx = ArrowSpacing(layer) * _scale;
        int skip = spacingPx < 18 ? 2 : 1;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            int idx = 0;
            foreach (var a in layer.Arrows)
            {
                if (skip > 1 && (idx++ % skip) != 0) continue;
                var p = m.Transform(new Point(a.X, a.Y));
                if (p.X < -40 || p.Y < -40 || p.X > ActualWidth + 40 || p.Y > ActualHeight + 40) continue;
                double speed = a.SpeedKmh;
                if (speed < 0.5) { continue; }
                double len = Math.Clamp(8 + speed * 0.55, 9, 42);
                // direction the air moves to, in screen coordinates (y down)
                var d = new Vector(a.U, -a.V);
                d.Normalize();
                var tail = p - d * len / 2;
                var head = p + d * len / 2;
                var nrm = new Vector(-d.Y, d.X);
                double hs = Math.Min(6, len * 0.35);
                ctx.BeginFigure(tail, false, false);
                ctx.LineTo(head, true, true);
                ctx.BeginFigure(head - d * hs + nrm * hs * 0.6, false, false);
                ctx.LineTo(head, true, true);
                ctx.LineTo(head - d * hs - nrm * hs * 0.6, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, ArrowHalo, geo);
        dc.DrawGeometry(null, ArrowPen, geo);
    }

    // ---- cloud symbols (constant screen size) ---------------------------------------------------------------

    /// <summary>A pre-built symbol in screen pixels centred on (0,0): merged silhouette + optional details on top.</summary>
    private sealed record SymbolTemplate(Geometry Body, Brush Fill, Pen Outline, (Geometry Geom, Brush? Fill, Pen? Pen)[] Details);

    private static readonly Dictionary<(SymbolKind, int), SymbolTemplate> Templates = BuildTemplates();

    private static Brush ToBrush(string hex) { var b = NtsToWpf.Brush(hex); if (!b.IsFrozen && b.CanFreeze) b.Freeze(); return b; }

    private static Dictionary<(SymbolKind, int), SymbolTemplate> BuildTemplates()
    {
        var cuFill = ToBrush(Core.Styling.ColorScale.CumulusSymbolFill);
        var cuPen = Frozen(new Pen(ToBrush(Core.Styling.ColorScale.CumulusSymbolStroke), 1.3) { LineJoin = PenLineJoin.Round });
        var scPen = Frozen(new Pen(ToBrush(Core.Styling.ColorScale.StratocumulusSymbolStroke), 1.3) { LineJoin = PenLineJoin.Round });
        var none = Array.Empty<(Geometry, Brush?, Pen?)>();

        var t = new Dictionary<(SymbolKind, int), SymbolTemplate>();
        // cumulus: 1–3 small clouds according to the amount
        (double X, double Y, double Scale)[][] groups =
        {
            new[] { (0.0, 0.0, 0.85) },
            new[] { (-4.5, 2.0, 0.75), (4.5, -2.0, 0.75) },
            new[] { (-6.5, 3.0, 0.68), (6.5, 3.0, 0.68), (0.0, -3.5, 0.68) },
        };
        for (int amount = 1; amount <= 3; amount++)
        {
            Geometry body = Geometry.Empty;
            foreach (var (x, y, sc) in groups[amount - 1]) body = Union(body, Cumulus(x, y, 9 * sc));
            t[(SymbolKind.Cumulus, amount)] = new(Freeze(body), cuFill, cuPen, none);
        }

        var stratocumulus = Stratocumulus(9, scPen);
        // blue thermals: a small ring – "thermals here, but no clouds to mark them"
        var blueRing = Freeze(new EllipseGeometry(new Point(0, 0), 4.5, 4.5));
        var blueFill = new SolidColorBrush(Color.FromArgb(170, 0xFF, 0xFF, 0xFF)); blueFill.Freeze();
        var bluePen = Frozen(new Pen(ToBrush(Core.Styling.ColorScale.BlueThermalSymbol), 2.0));
        for (int amount = 1; amount <= 3; amount++) t[(SymbolKind.Blue, amount)] = new(blueRing, blueFill, bluePen, none);
        var congestus = Congestus(11, cuFill, cuPen);
        var showers = Cumulonimbus(12, thunder: false);
        var storm = Cumulonimbus(12, thunder: true);
        for (int amount = 1; amount <= 3; amount++)
        {
            t[(SymbolKind.Stratocumulus, amount)] = stratocumulus;
            t[(SymbolKind.Congestus, amount)] = congestus;
            t[(SymbolKind.Cumulonimbus, amount)] = amount >= 2 ? storm : showers;
        }
        return t;
    }

    private static Geometry Union(Geometry a, Geometry b) => a.IsEmpty() ? b : Geometry.Combine(a, b, GeometryCombineMode.Union, null);
    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

    private static Geometry Ellipses(double s, params (double X, double Y, double Rx, double Ry)[] e)
    {
        Geometry g = Geometry.Empty;
        foreach (var (x, y, rx, ry) in e) g = Union(g, new EllipseGeometry(new Point(x * s, y * s), rx * s, ry * s));
        return g;
    }

    private static Geometry Polygon(double s, params (double X, double Y)[] pts)
    {
        var sg = new StreamGeometry();
        using (var c = sg.Open())
        {
            c.BeginFigure(new Point(pts[0].X * s, pts[0].Y * s), true, true);
            c.PolyLineTo(pts.Skip(1).Select(p => new Point(p.X * s, p.Y * s)).ToList(), true, true);
        }
        return sg;
    }

    /// <summary>Fair-weather cumulus: three puffs on a flat base, centred at (cx, cy).</summary>
    private static Geometry Cumulus(double cx, double cy, double s)
    {
        var g = Union(Ellipses(s, (-0.55, 0.1, 0.45, 0.4), (0.55, 0.12, 0.42, 0.38), (-0.05, -0.25, 0.55, 0.55)),
            new RectangleGeometry(new Rect(-0.95 * s, 0.05 * s, 1.9 * s, 0.45 * s), 0.2 * s, 0.2 * s));
        var moved = g.Clone();
        moved.Transform = new TranslateTransform(cx, cy);
        return moved;
    }

    /// <summary>Stratocumulus: a wide grey deck – a back row of larger lumps behind a front row of smaller ones, darker underneath.</summary>
    private static SymbolTemplate Stratocumulus(double s, Pen outline)
    {
        var body = Ellipses(s,
            (-0.75, -0.3, 0.44, 0.4), (-0.02, -0.38, 0.46, 0.42), (0.72, -0.28, 0.42, 0.38),             // back row
            (-1.08, 0.05, 0.36, 0.3), (-0.36, 0.02, 0.38, 0.32), (0.36, 0.03, 0.38, 0.32), (1.08, 0.06, 0.34, 0.29)); // front row
        body = Union(body, new RectangleGeometry(new Rect(-1.38 * s, 0.05 * s, 2.76 * s, 0.36 * s), 0.16 * s, 0.16 * s));
        body.Freeze();
        var fill = new LinearGradientBrush(Color.FromRgb(0xCF, 0xD5, 0xDC), Color.FromRgb(0x84, 0x8E, 0x9A), 90);
        fill.Freeze();
        return new SymbolTemplate(body, fill, outline, Array.Empty<(Geometry, Brush?, Pen?)>());
    }

    /// <summary>
    /// Cumulus congestus: a broad "cauliflower" tower – wide flat base, bulky lumpy body and an asymmetric lumpy top
    /// (no narrow neck), still white because there is no precipitation yet; a grey shadow along the base.
    /// </summary>
    private static SymbolTemplate Congestus(double s, Brush fill, Pen outline)
    {
        var body = Ellipses(s,
            (-0.72, 0.36, 0.38, 0.32), (0.0, 0.3, 0.48, 0.4), (0.72, 0.36, 0.38, 0.32),     // base row
            (-0.5, -0.08, 0.42, 0.42), (0.12, -0.18, 0.48, 0.46), (0.62, -0.02, 0.34, 0.34), // body
            (-0.3, -0.55, 0.4, 0.38), (0.28, -0.62, 0.4, 0.4),                               // upper body
            (-0.1, -0.98, 0.32, 0.27), (0.38, -0.92, 0.24, 0.22), (-0.48, -0.82, 0.2, 0.18)); // lumpy top
        body = Union(body, new RectangleGeometry(new Rect(-1.08 * s, 0.36 * s, 2.16 * s, 0.32 * s), 0.12 * s, 0.12 * s));
        body.Freeze();
        var shadow = Freeze(new RectangleGeometry(new Rect(-0.98 * s, 0.5 * s, 1.96 * s, 0.13 * s), 0.06 * s, 0.06 * s));
        var shadowFill = new SolidColorBrush(Color.FromRgb(0xC5, 0xCE, 0xD8));
        shadowFill.Freeze();
        return new SymbolTemplate(body, fill, outline, new (Geometry, Brush?, Pen?)[] { (shadow, shadowFill, null) });
    }

    /// <summary>Cumulonimbus: towering body with a flat anvil, dark gradient and rain streaks; a lightning bolt for thunderstorms.</summary>
    private static SymbolTemplate Cumulonimbus(double s, bool thunder)
    {
        var body = Ellipses(s,
            (-0.62, 0.32, 0.42, 0.36), (0.62, 0.32, 0.42, 0.36), (0.0, 0.18, 0.55, 0.45),   // base puffs
            (-0.28, -0.25, 0.42, 0.45), (0.3, -0.3, 0.4, 0.42),                              // tower
            (0.0, -0.62, 0.36, 0.34));
        body = Union(body, new RectangleGeometry(new Rect(-1.0 * s, 0.3 * s, 2.0 * s, 0.4 * s), 0.15 * s, 0.15 * s));
        // anvil: flat top spreading downwind (to the right) with a thin trailing edge
        body = Union(body, Polygon(s, (-0.75, -0.95), (1.35, -1.12), (1.2, -0.95), (0.4, -0.72), (-0.35, -0.7)));
        body = Union(body, Ellipses(s, (-0.55, -0.88, 0.28, 0.12)));
        body.Freeze();

        var fill = new LinearGradientBrush(Color.FromRgb(0x8C, 0x97, 0xA6), Color.FromRgb(0x2B, 0x31, 0x3B), 90);
        fill.Freeze();
        var outline = Frozen(new Pen(ToBrush(Core.Styling.ColorScale.CumulonimbusSymbolStroke), 1.3) { LineJoin = PenLineJoin.Round });

        var bolt = Freeze(Polygon(s, (0.18, 0.05), (-0.22, 0.7), (0.02, 0.7), (-0.18, 1.3), (0.38, 0.52), (0.12, 0.52), (0.36, 0.05)));
        var boltPen = Frozen(new Pen(ToBrush(Core.Styling.ColorScale.CumulonimbusSymbolStroke), 0.9) { LineJoin = PenLineJoin.Round });

        var rain = new StreamGeometry();
        using (var c = rain.Open())
            foreach (var x in thunder ? new[] { -0.75, -0.5, 0.6, 0.85 } : new[] { -0.6, -0.3, 0.0, 0.3, 0.6 })
            {
                c.BeginFigure(new Point(x * s, 0.82 * s), false, false);
                c.LineTo(new Point((x - 0.14) * s, 1.18 * s), true, false);
            }
        rain.Freeze();
        var rainPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xD9)), 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

        return new SymbolTemplate(body, fill, outline, thunder
            ? new (Geometry, Brush?, Pen?)[] { (rain, null, rainPen), (bolt, ToBrush(Core.Styling.ColorScale.LightningFill), boltPen) }
            : new (Geometry, Brush?, Pen?)[] { (rain, null, rainPen) });
    }

    private static readonly Pen SymbolHalo = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 3.0) { LineJoin = PenLineJoin.Round });

    private void RenderSymbols()
    {
        using var dc = _symbols.RenderOpen();
        var layer = MeteoLayer;
        if (layer is null || layer.Symbols.Count == 0 || double.IsNaN(_scale)) return;

        var m = WorldToScreen;
        // keep every n-th lattice point so that neighbouring symbols are at least a minimum distance apart;
        // big storm symbols are sparser – the dashed outline shows the whole area and the map stays readable
        double spacingPx = layer.SymbolSpacing * _scale;
        // step on the symbol lattice (in lattice cells) giving at least minPx on screen; a sparse step is a multiple
        // of the dense one so the sparse symbols are a subset of the dense ones
        int Every(double minPx) => Math.Clamp((int)Math.Ceiling(minPx / Math.Max(spacingPx, 1e-6)), 1, 256);
        int Multiple(int dense, double minPx) => dense * Math.Max(1, (int)Math.Ceiling(minPx / (spacingPx * dense)));
        // adaptive density: uniform areas get a sparse lattice, where the cloud type or amount changes
        // the symbols are drawn densely (the boundary is what matters for planning)
        int denseCloud = Every(40), denseStorm = Every(52);
        int sparseCloud = Multiple(denseCloud, 75), sparseStorm = Multiple(denseStorm, 95);
        var byCell = new Dictionary<(int, int), MapSymbol>(layer.Symbols.Count);
        foreach (var s in layer.Symbols) byCell[(s.I, s.J)] = s;

        bool Differs(MapSymbol s, int step, bool amountToo)
        {
            for (int dj = -step; dj <= step; dj += step)
                for (int di = -step; di <= step; di += step)
                {
                    if (di == 0 && dj == 0) continue;
                    if (!byCell.TryGetValue((s.I + di, s.J + dj), out var n) || n.Kind != s.Kind || (amountToo && n.Amount != s.Amount)) return true;
                }
            return false;
        }
        bool On(MapSymbol s, int step) => s.I % step == 0 && s.J % step == 0;

        // storms last so they stay on top
        foreach (var sym in layer.Symbols.OrderBy(s => s.Kind))
        {
            bool storm = sym.Kind is SymbolKind.Cumulonimbus or SymbolKind.Congestus;
            int dense = storm ? denseStorm : denseCloud, sparse = storm ? sparseStorm : sparseCloud;
            int medium = Math.Min(sparse, dense * 2);
            if (sparse % medium != 0) medium = sparse; // keep the lattices nested
            if (!On(sym, dense)) continue;
            // full density where the cloud type changes (or clouds end), half density where only the Cu amount changes
            bool show = On(sym, sparse) || Differs(sym, dense, amountToo: false) || (On(sym, medium) && Differs(sym, medium, amountToo: true));
            if (!show) continue;
            var p = m.Transform(new Point(sym.X, sym.Y));
            if (p.X < -30 || p.Y < -30 || p.X > ActualWidth + 30 || p.Y > ActualHeight + 30) continue;
            var t = Templates[(sym.Kind, Math.Clamp(sym.Amount, 1, 3))];
            dc.PushTransform(new TranslateTransform(Math.Round(p.X), Math.Round(p.Y)));
            dc.DrawGeometry(null, SymbolHalo, t.Body);
            dc.DrawGeometry(t.Fill, t.Outline, t.Body);
            foreach (var (geom, fill, pen) in t.Details) dc.DrawGeometry(fill, pen, geom);
            dc.Pop();
        }
    }

    private static double ArrowSpacing(VectorLayer layer)
    {
        if (layer.Arrows.Count < 2) return 15_000;
        var a = layer.Arrows[0];
        return layer.Arrows.Skip(1).Select(b => Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y)).Where(d => d > 0).DefaultIfEmpty(15_000).Min();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // hit-testable background
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    // ---- interaction ----------------------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (double.IsNaN(_scale)) return;
        var pos = e.GetPosition(this);
        var before = ScreenToWorld(pos);
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        _scale = Math.Clamp(_scale * factor, FitScale * 0.5, FitScale * 25);
        var after = ScreenToWorld(pos);
        _center = new Point(_center.X + before.X - after.X, _center.Y + before.Y - after.Y);
        ViewChanged(zoomChanged: true);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2) { Fit(); return; }
        _dragStart = e.GetPosition(this);
        _dragCenter = _center;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_dragStart is { } start && !double.IsNaN(_scale))
        {
            var d = pos - start;
            _center = new Point(_dragCenter.X - d.X / _scale, _dragCenter.Y + d.Y / _scale);
            ViewChanged(zoomChanged: false);
        }
        if (!double.IsNaN(_scale)) PointerWorld = ScreenToWorld(pos);
    }

    protected override void OnMouseLeave(MouseEventArgs e) => PointerWorld = null;
}
