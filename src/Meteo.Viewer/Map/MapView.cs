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
    private readonly DrawingVisual _relief = new(), _meteo = new(), _borders = new(), _arrows = new();
    private double _scale = double.NaN; // pixels per metre
    private Point _center;              // world point at the control centre
    private Point? _dragStart;
    private Point _dragCenter;

    public MapView()
    {
        _visuals = new VisualCollection(this) { _relief, _meteo, _borders, _arrows };
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => { if (double.IsNaN(_scale)) Fit(); else ViewChanged(zoomChanged: false); };
    }

    // ---- dependency properties ----------------------------------------------------------------------

    public static readonly DependencyProperty ReliefLayerProperty = Dp(nameof(ReliefLayer), (m, _) => m.RenderRelief());
    public static readonly DependencyProperty MeteoLayerProperty = Dp(nameof(MeteoLayer), (m, _) => { m.RenderMeteo(); m.RenderArrows(); });
    public static readonly DependencyProperty BorderLayerProperty = Dp(nameof(BorderLayer), (m, _) => m.RenderBorders());
    public static readonly DependencyProperty ShowArrowsProperty = DependencyProperty.Register(nameof(ShowArrows), typeof(bool), typeof(MapView),
        new PropertyMetadata(true, (d, _) => ((MapView)d).RenderArrows()));
    public static readonly DependencyProperty WorldExtentProperty = DependencyProperty.Register(nameof(WorldExtent), typeof(Rect), typeof(MapView),
        new PropertyMetadata(Rect.Empty, (d, _) => ((MapView)d).Fit()));
    public static readonly DependencyProperty PointerWorldProperty = DependencyProperty.Register(nameof(PointerWorld), typeof(Point?), typeof(MapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public VectorLayer? ReliefLayer { get => (VectorLayer?)GetValue(ReliefLayerProperty); set => SetValue(ReliefLayerProperty, value); }
    public VectorLayer? MeteoLayer { get => (VectorLayer?)GetValue(MeteoLayerProperty); set => SetValue(MeteoLayerProperty, value); }
    public VectorLayer? BorderLayer { get => (VectorLayer?)GetValue(BorderLayerProperty); set => SetValue(BorderLayerProperty, value); }
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
        if (zoomChanged) { RenderMeteo(); RenderBorders(); }
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
        foreach (var p in layer.Polygons.Where(p => p.Stroke != null))
        {
            var pen = new Pen(NtsToWpf.Brush(p.Stroke!), p.StrokeWidth / _scale);
            if (p.Dashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            dc.DrawGeometry(null, pen, NtsToWpf.Convert(p.Geometry));
        }
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
