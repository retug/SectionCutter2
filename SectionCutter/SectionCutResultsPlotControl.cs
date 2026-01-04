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


            // Draw cuts + diagrams
            if (Cuts != null)
            {
                foreach (var c in Cuts)
                {
                    if (c == null) continue;

                    bool isSelected = !string.IsNullOrWhiteSpace(SelectedCutName) &&
                                      string.Equals(c.Name, SelectedCutName, StringComparison.OrdinalIgnoreCase);

                    bool isHover = ReferenceEquals(c, _hoverCut);

                    var pen = isHover ? hoverPen : isSelected ? selectedPen : cutPen;

                    dc.DrawLine(pen, c.A, c.B);

                    // Diagram line perpendicular at midpoint
                    var mid = new Point((c.A.X + c.B.X) * 0.5, (c.A.Y + c.B.Y) * 0.5);

                    var dir = new Vector(c.B.X - c.A.X, c.B.Y - c.A.Y);
                    if (dir.Length < 1e-9) continue;
                    dir.Normalize();

                    // Perp direction
                    var perp = new Vector(-dir.Y, dir.X);

                    double diagramLen = c.Value * ValueScale;
                    var end = mid + perp * diagramLen;

                    dc.DrawLine(diagramLen >= 0 ? diagramPen : diagramPenNeg, mid, end);

                    // Small end cap
                    double cap = 4.0 * invScale;
                    var capDir = perp;
                    if (capDir.Length > 1e-9) capDir.Normalize();
                    var capPerp = new Vector(-capDir.Y, capDir.X);

                    dc.DrawLine(diagramLen >= 0 ? diagramPen : diagramPenNeg,
                        end - capPerp * cap,
                        end + capPerp * cap);
                }
            }

            dc.Pop(); // transform

            // Hover tooltip (screen-space)
            if (_hoverCut != null)
            {
                string text =
                    $"{_hoverCut.Name}\n" +
                    $"Length: {_hoverCut.Length:0.###}\n" +
                    $"Value: {_hoverCut.Value:0.###}";

                DrawTooltip(dc, text, _lastMouse);
            }

            dc.Pop(); // clip
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
