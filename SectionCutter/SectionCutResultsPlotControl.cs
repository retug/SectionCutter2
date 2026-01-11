using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SectionCutter
{
    /// <summary>
    /// Plot control for Review Results:
    /// - Draws Area polygons + Opening polygons
    /// - Draws section cut lines
    /// - Draws a "result diagram" perpendicular to each cut at its midpoint
    /// - Supports pan/zoom and hover tooltip
    ///
    /// This is intentionally based on the same core mechanics as SectionCutPreviewControl
    /// (view matrix, constant pixel thickness, and hard clipping to bounds). :contentReference[oaicite:1]{index=1}
    /// </summary>
    public class SectionCutResultsPlotControl : FrameworkElement
    {


        // ----------------------------
        // Dependency Properties
        // ----------------------------
        public static readonly DependencyProperty PlotBackgroundProperty =
            DependencyProperty.Register(
        nameof(PlotBackground),
        typeof(Brush),
        typeof(SectionCutResultsPlotControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
            FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush PlotBackground
        {
            get => (Brush)GetValue(PlotBackgroundProperty);
            set => SetValue(PlotBackgroundProperty, value);
        }

        public static readonly DependencyProperty AreasProperty =
            DependencyProperty.Register(
                nameof(Areas),
                typeof(ObservableCollection<PointCollection>),
                typeof(SectionCutResultsPlotControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public ObservableCollection<PointCollection> Areas
        {
            get => (ObservableCollection<PointCollection>)GetValue(AreasProperty);
            set => SetValue(AreasProperty, value);
        }

        public static readonly DependencyProperty OpeningsProperty =
            DependencyProperty.Register(
                nameof(Openings),
                typeof(ObservableCollection<PointCollection>),
                typeof(SectionCutResultsPlotControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public ObservableCollection<PointCollection> Openings
        {
            get => (ObservableCollection<PointCollection>)GetValue(OpeningsProperty);
            set => SetValue(OpeningsProperty, value);
        }

        public static readonly DependencyProperty CutsProperty =
            DependencyProperty.Register(
                nameof(Cuts),
                typeof(ObservableCollection<SectionCutter.ViewModels.ResultCutPlotItem>),
                typeof(SectionCutResultsPlotControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public ObservableCollection<SectionCutter.ViewModels.ResultCutPlotItem> Cuts
        {
            get => (ObservableCollection<SectionCutter.ViewModels.ResultCutPlotItem>)GetValue(CutsProperty);
            set => SetValue(CutsProperty, value);
        }


        public static readonly DependencyProperty ValueScaleProperty =
            DependencyProperty.Register(
                nameof(ValueScale),
                typeof(double),
                typeof(SectionCutResultsPlotControl),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// Visual scaling multiplier for result diagram length.
        /// You will tune this in Step 5 once real results are plotted.
        /// </summary>
        public double ValueScale
        {
            get => (double)GetValue(ValueScaleProperty);
            set => SetValue(ValueScaleProperty, value);
        }

        public static readonly DependencyProperty SelectedCutNameProperty =
            DependencyProperty.Register(
                nameof(SelectedCutName),
                typeof(string),
                typeof(SectionCutResultsPlotControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// Optional: highlight a selected cut (e.g., from DataGrid selection).
        /// </summary>
        public string SelectedCutName
        {
            get => (string)GetValue(SelectedCutNameProperty);
            set => SetValue(SelectedCutNameProperty, value);
        }

        public static readonly DependencyProperty DiagramLabelProperty =
    DependencyProperty.Register(
        nameof(DiagramLabel),
        typeof(string),
        typeof(SectionCutResultsPlotControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public string DiagramLabel
        {
            get => (string)GetValue(DiagramLabelProperty);
            set => SetValue(DiagramLabelProperty, value);
        }

        public static readonly DependencyProperty ValueUnitsProperty =
            DependencyProperty.Register(
                nameof(ValueUnits),
                typeof(string),
                typeof(SectionCutResultsPlotControl),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public string ValueUnits
        {
            get => (string)GetValue(ValueUnitsProperty);
            set => SetValue(ValueUnitsProperty, value);
        }

        public static readonly DependencyProperty LengthUnitsProperty =
            DependencyProperty.Register(
                nameof(LengthUnits),
                typeof(string),
                typeof(SectionCutResultsPlotControl),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public string LengthUnits
        {
            get => (string)GetValue(LengthUnitsProperty);
            set => SetValue(LengthUnitsProperty, value);
        }

        // ----------------------------
        // View / interaction state (pan/zoom)
        // ----------------------------
        private Matrix _view = Matrix.Identity;
        private bool _isPanning;
        private Point _panStartMouse;
        private Point _panStartTranslation;

        // Hover state
        private SectionCutter.ViewModels.ResultCutPlotItem _hoverCut;
        private Point _lastMouse;

        // Rendering constants (screen-space)
        private const double BaseStrokePx = 2.0;
        private const double HoverHitPx = 10.0;

        


        public SectionCutResultsPlotControl()
        {
            Focusable = true;

            Loaded += (_, __) =>
            {
                // Start with a fit-to-content if possible
                FitToContent();
            };

            SizeChanged += (_, __) =>
            {
                // Keep it fitted on first display / resize (simple + stable)
                // If you want "don't refit after user pans" later, we can add a flag.
                if (!_isPanning)
                    FitToContent();
            };

            MouseWheel += OnMouseWheelZoom;
            MouseDown += OnMouseDown;
            MouseUp += OnMouseUp;
            MouseMove += OnMouseMove;
        }

        // ----------------------------
        // Public helpers
        // ----------------------------
        public void FitToContent()
        {
            var bounds = ComputeWorldBounds();
            if (bounds.IsEmpty || ActualWidth < 10 || ActualHeight < 10)
            {
                _view = Matrix.Identity;
                InvalidateVisual();
                return;
            }

            // Add a small padding in world space
            double pad = Math.Max(bounds.Width, bounds.Height) * 0.05;
            var padded = new Rect(bounds.X - pad, bounds.Y - pad, bounds.Width + 2 * pad, bounds.Height + 2 * pad);

            double sx = ActualWidth / padded.Width;
            double sy = ActualHeight / padded.Height;
            double s = Math.Min(sx, sy);

            // Center padded bounds into control
            var m = Matrix.Identity;
            m.Translate(-padded.X, -padded.Y);
            m.Scale(s, s);

            // After scale, compute translation to center
            var scaledW = padded.Width * s;
            var scaledH = padded.Height * s;
            double tx = (ActualWidth - scaledW) * 0.5;
            double ty = (ActualHeight - scaledH) * 0.5;
            m.Translate(tx, ty);

            _view = m;
            InvalidateVisual();
        }

        public void CenterOnCut(string cutName)
        {
            if (string.IsNullOrWhiteSpace(cutName) || Cuts == null) return;
            var cut = Cuts.FirstOrDefault(c => string.Equals(c.Name, cutName, StringComparison.OrdinalIgnoreCase));
            if (cut == null) return;

            var mid = new Point((cut.A.X + cut.B.X) * 0.5, (cut.A.Y + cut.B.Y) * 0.5);

            // Keep current scale; just translate so midpoint is at center of control
            double s = GetCurrentScale();
            var screenMid = _view.Transform(mid);

            double dx = (ActualWidth * 0.5) - screenMid.X;
            double dy = (ActualHeight * 0.5) - screenMid.Y;

            _view.Translate(dx, dy);
            InvalidateVisual();
        }

        private static Point MidPoint(Point a, Point b)
        {
            return new Point((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
        }

        private static Point ParallelSamplePoint(Point a, Point b, double value, double scale)
        {
            Vector dir = b - a;
            if (dir.Length < 1e-9) return MidPoint(a, b);

            dir.Normalize(); // PARALLEL direction

            Point mid = MidPoint(a, b);
            return mid + dir * (value * scale);
        }


        // ----------------------------
        // Rendering
        // ----------------------------
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            // hard clip to bounds (prevents drawing outside the panel)
            // matches your desired behavior and how the preview control works :contentReference[oaicite:2]{index=2}
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

            // Background
            dc.DrawRectangle(PlotBackground ?? Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));


            // World->screen transform
            dc.PushTransform(new MatrixTransform(_view));

            double invScale = 1.0 / Math.Max(GetCurrentScale(), 1e-9);
            double strokeWorld = BaseStrokePx * invScale;

            // Pens/brushes
            var areaFill = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            var areaEdge = new Pen(new SolidColorBrush(Color.FromRgb(160, 160, 160)), strokeWorld);

            var openingFill = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var openingEdge = new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 200)), strokeWorld);

            var cutPen = new Pen(new SolidColorBrush(Color.FromRgb(30, 30, 30)), strokeWorld);

            var selectedPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 140, 105)), strokeWorld * 1.5);
            var hoverPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 140, 105)), strokeWorld * 2.0);

            var diagramPen = new Pen(new SolidColorBrush(Color.FromRgb(60, 120, 220)), strokeWorld);
            var diagramPenNeg = new Pen(new SolidColorBrush(Color.FromRgb(220, 80, 80)), strokeWorld);

            // Draw areas
            if (Areas != null)
            {
                foreach (var poly in Areas)
                {
                    if (poly == null || poly.Count < 3) continue;
                    dc.DrawGeometry(areaFill, areaEdge, ToGeometry(poly));
                }
            }

            // Draw openings
            if (Openings != null)
            {
                foreach (var poly in Openings)
                {
                    if (poly == null || poly.Count < 3) continue;
                    dc.DrawGeometry(openingFill, openingEdge, ToGeometry(poly));
                }
            }

            // Draw cuts (actual section cut lines)
            if (Cuts != null)
            {
                foreach (var c in Cuts)
                {
                    if (c == null) continue;

                    bool isSelected = !string.IsNullOrWhiteSpace(SelectedCutName) &&
                                      string.Equals(c.Name, SelectedCutName, StringComparison.OrdinalIgnoreCase);

                    bool isHover = ReferenceEquals(c, _hoverCut);

                    var pen = isHover ? hoverPen : isSelected ? selectedPen : cutPen;

                    // physical cut line
                    dc.DrawLine(pen, c.A, c.B);
                }

                // Draw the results curve PARALLEL to the cuts, with shaded area to the 0-line
                DrawParallelResultsPlot(dc, diagramPen, diagramPenNeg, invScale);
            }



            dc.Pop(); // transform
            // Plot label (screen-space)
            if (!string.IsNullOrWhiteSpace(DiagramLabel))
            {
                DrawPlotLabel(dc, DiagramLabel, new Point(12, 8));
            }

            // Hover tooltip (screen-space)
            if (_hoverCut != null)
            {
                var lenU = string.IsNullOrWhiteSpace(LengthUnits) ? "" : $" {LengthUnits}";
                var valU = string.IsNullOrWhiteSpace(ValueUnits) ? "" : $" {ValueUnits}";

                // Prefer using DiagramLabel’s component name (e.g., "Shear (kip)") for tooltip heading
                string valueLabel = "Value";
                if (!string.IsNullOrWhiteSpace(DiagramLabel))
                {
                    // If DiagramLabel is "Shear (kip)", use "Shear"
                    valueLabel = DiagramLabel.Split('(')[0].Trim();
                }

                string text =
                    $"{_hoverCut.Name}\n" +
                    $"Length: {_hoverCut.Length:0.###}{lenU}\n" +
                    $"{valueLabel}: {_hoverCut.Value:0.###}{valU}";

                DrawTooltip(dc, text, _lastMouse);
            }

            dc.Pop(); // clip
        }

        private void DrawParallelResultsPlot(
    DrawingContext dc,
    Pen diagramPenPos,
    Pen diagramPenNeg,
    double invScale)
        {
            if (Cuts == null || Cuts.Count < 2) return;

            var ordered = OrderCutsForPolyline(Cuts);
            if (ordered.Count < 2) return;

            var basePts = new List<Point>(ordered.Count);
            var curvePts = new List<Point>(ordered.Count);

            foreach (var c in ordered)
            {
                var mid = MidPoint(c.A, c.B);
                basePts.Add(mid);

                // PARALLEL offset point (value * scale along cut direction)
                curvePts.Add(ParallelSamplePoint(c.A, c.B, c.Value, ValueScale));
            }

            // --- Fill area between 0-line (basePts) and curve (curvePts)
            var areaGeom = new StreamGeometry();
            using (var ctx = areaGeom.Open())
            {
                ctx.BeginFigure(basePts[0], isFilled: true, isClosed: true);

                // along base from start -> end
                for (int i = 1; i < basePts.Count; i++)
                    ctx.LineTo(basePts[i], isStroked: true, isSmoothJoin: false);

                // back along curve from end -> start
                for (int i = curvePts.Count - 1; i >= 0; i--)
                    ctx.LineTo(curvePts[i], isStroked: true, isSmoothJoin: false);
            }
            areaGeom.Freeze();

            // light fill like your example
            var fillBrush = new SolidColorBrush(Color.FromArgb(60, 60, 120, 220));
            dc.DrawGeometry(fillBrush, null, areaGeom);

            // --- Draw the 0-line
            var zeroGeom = new StreamGeometry();
            using (var ctx = zeroGeom.Open())
            {
                ctx.BeginFigure(basePts[0], isFilled: false, isClosed: false);
                for (int i = 1; i < basePts.Count; i++)
                    ctx.LineTo(basePts[i], isStroked: true, isSmoothJoin: false);
            }
            zeroGeom.Freeze();

            var zeroPen = new Pen(new SolidColorBrush(Color.FromRgb(160, 160, 160)), 1.5 * invScale);
            dc.DrawGeometry(null, zeroPen, zeroGeom);

            // --- Draw curve segments (pos vs neg)
            for (int i = 0; i < curvePts.Count - 1; i++)
            {
                var c0 = ordered[i];
                var c1 = ordered[i + 1];

                // choose color based on average sign between the two
                double avg = 0.5 * (c0.Value + c1.Value);
                var pen = avg >= 0 ? diagramPenPos : diagramPenNeg;

                dc.DrawLine(pen, curvePts[i], curvePts[i + 1]);
            }

            // optional: little nodes on curve
            double r = 3.0 * invScale;
            for (int i = 0; i < curvePts.Count; i++)
            {
                var pen = ordered[i].Value >= 0 ? diagramPenPos : diagramPenNeg;
                dc.DrawEllipse(pen.Brush, null, curvePts[i], r, r);
            }
        }


        private static Geometry ToGeometry(PointCollection pts)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(pts[0], true, true);
                ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            geo.Freeze();
            return geo;
        }

        private static List<ViewModels.ResultCutPlotItem> OrderCutsForPolyline(IEnumerable<ViewModels.ResultCutPlotItem> cuts)
        {
            var list = cuts?.Where(c => c != null).ToList() ?? new List<ViewModels.ResultCutPlotItem>();
            if (list.Count <= 2) return list;

            // Midpoints
            var mids = list.Select(c => MidPoint(c.A, c.B)).ToList();

            double minX = mids.Min(p => p.X), maxX = mids.Max(p => p.X);
            double minY = mids.Min(p => p.Y), maxY = mids.Max(p => p.Y);

            Vector axis = (maxX - minX) >= (maxY - minY)
                ? new Vector(1, 0)   // mostly horizontal
                : new Vector(0, 1);  // mostly vertical

            // Sort by projection onto chosen axis
            return list
                .OrderBy(c =>
                {
                    var m = MidPoint(c.A, c.B);
                    return (m.X * axis.X) + (m.Y * axis.Y);
                })
                .ToList();
        }

        private void DrawTooltip(DrawingContext dc, string text, Point screenPt)
        {
            // offset from mouse
            double x = screenPt.X + 12;
            double y = screenPt.Y + 12;

            var ft = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var pad = 6.0;
            var rect = new Rect(x, y, ft.Width + pad * 2, ft.Height + pad * 2);

            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromRgb(180, 180, 180)), 1),
                rect,
                6, 6);

            dc.DrawText(ft, new Point(x + pad, y + pad));
        }

        private void DrawPlotLabel(DrawingContext dc, string text, Point screenPt)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                13,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            // white “chip” behind text, like your other headers
            var padX = 6.0;
            var padY = 3.0;
            var r = new Rect(screenPt.X, screenPt.Y, ft.Width + 2 * padX, ft.Height + 2 * padY);

            dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), 1), r, 6, 6);
            dc.DrawText(ft, new Point(screenPt.X + padX, screenPt.Y + padY));
        }


        // ----------------------------
        // Interaction
        // ----------------------------
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();

            _lastMouse = e.GetPosition(this);

            // Right mouse pan (matches common CAD-like behavior)
            if (e.RightButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _panStartMouse = _lastMouse;
                _panStartTranslation = new Point(_view.OffsetX, _view.OffsetY);
                CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.ChangedButton == MouseButton.Right)
            {
                _isPanning = false;
                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            _lastMouse = e.GetPosition(this);

            if (_isPanning)
            {
                Vector delta = _lastMouse - _panStartMouse;
                _view.OffsetX = _panStartTranslation.X + delta.X;
                _view.OffsetY = _panStartTranslation.Y + delta.Y;
                InvalidateVisual();
                return;
            }

            // Hover detection
            _hoverCut = HitTestCuts(_lastMouse);
            InvalidateVisual();
        }

        private void OnMouseWheelZoom(object sender, MouseWheelEventArgs e)
        {
            var mouse = e.GetPosition(this);

            double zoom = e.Delta > 0 ? 1.10 : 1.0 / 1.10;

            // Zoom about mouse position (screen)
            _view.ScaleAt(zoom, zoom, mouse.X, mouse.Y);

            InvalidateVisual();
            e.Handled = true;
        }

        // ----------------------------
        // Hit testing
        // ----------------------------
        private SectionCutter.ViewModels.ResultCutPlotItem HitTestCuts(Point mouseScreen)
        {
            if (Cuts == null || Cuts.Count == 0) return null;

            // screen-space distance to segment
            double best = double.MaxValue;
            SectionCutter.ViewModels.ResultCutPlotItem bestCut = null;

            foreach (var c in Cuts)
            {
                if (c == null) continue;

                Point aS = _view.Transform(c.A);
                Point bS = _view.Transform(c.B);

                double d = DistancePointToSegment(mouseScreen, aS, bS);
                if (d < best)
                {
                    best = d;
                    bestCut = c; // ✅ now types match
                }
            }

            return best <= HoverHitPx ? bestCut : null;
        }


        private static double DistancePointToSegment(Point p, Point a, Point b)
        {
            Vector ab = b - a;
            Vector ap = p - a;

            double ab2 = ab.X * ab.X + ab.Y * ab.Y;
            if (ab2 < 1e-9) return (p - a).Length;

            double t = (ap.X * ab.X + ap.Y * ab.Y) / ab2;
            t = Math.Max(0, Math.Min(1, t));

            Point proj = a + ab * t;
            return (p - proj).Length;
        }

        // ----------------------------
        // World bounds
        // ----------------------------
        private Rect ComputeWorldBounds()
        {
            Rect r = Rect.Empty;

            void UnionPts(PointCollection pc)
            {
                if (pc == null || pc.Count == 0) return;
                foreach (var p in pc)
                {
                    if (r.IsEmpty) r = new Rect(p, new Size(0, 0));
                    else r.Union(p);
                }
            }

            if (Areas != null)
                foreach (var p in Areas) UnionPts(p);

            if (Openings != null)
                foreach (var p in Openings) UnionPts(p);


            if (Cuts != null)
            {
                foreach (var c in Cuts)
                {
                    if (c == null) continue;
                    if (r.IsEmpty) r = new Rect(c.A, new Size(0, 0));
                    r.Union(c.A);
                    r.Union(c.B);
                }
            }

            return r;
        }

        private double GetCurrentScale()
        {
            // assume uniform scaling in matrix (true for our usage)
            return Math.Sqrt(_view.M11 * _view.M11 + _view.M12 * _view.M12);
        }
    }
}
