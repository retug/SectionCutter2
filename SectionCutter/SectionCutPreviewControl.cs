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
    /// WPF plot control for previewing:
    /// - Area polygons (filled)
    /// - Opening polygons (filled)
    /// - Section cut line segments
    ///
    /// Coordinates are expected in local U/V space (2D).
    /// Includes pan (left-drag) + zoom (mouse wheel) + zoom-to-fit (double middle-click).
    ///
    /// IMPORTANT:
    /// If there is no real data (Areas/Openings/Cuts all empty), OnRender early-returns and draws nothing.
    /// </summary>
    public class SectionCutPreviewControl : FrameworkElement
    {
        // --------------------------
        // Dependency Properties
        // --------------------------

        public ObservableCollection<PointCollection> Areas
        {
            get => (ObservableCollection<PointCollection>)GetValue(AreasProperty);
            set => SetValue(AreasProperty, value);
        }

        public static readonly DependencyProperty AreasProperty =
            DependencyProperty.Register(
                nameof(Areas),
                typeof(ObservableCollection<PointCollection>),
                typeof(SectionCutPreviewControl),
                new FrameworkPropertyMetadata(
                    new ObservableCollection<PointCollection>(),
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnDataChanged));

        public ObservableCollection<PointCollection> Openings
        {
            get => (ObservableCollection<PointCollection>)GetValue(OpeningsProperty);
            set => SetValue(OpeningsProperty, value);
        }

        public static readonly DependencyProperty OpeningsProperty =
            DependencyProperty.Register(
                nameof(Openings),
                typeof(ObservableCollection<PointCollection>),
                typeof(SectionCutPreviewControl),
                new FrameworkPropertyMetadata(
                    new ObservableCollection<PointCollection>(),
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnDataChanged));

        public ObservableCollection<Segment> Cuts
        {
            get => (ObservableCollection<Segment>)GetValue(CutsProperty);
            set => SetValue(CutsProperty, value);
        }

        public static readonly DependencyProperty CutsProperty =
            DependencyProperty.Register(
                nameof(Cuts),
                typeof(ObservableCollection<Segment>),
                typeof(SectionCutPreviewControl),
                new FrameworkPropertyMetadata(
                    new ObservableCollection<Segment>(),
                    FrameworkPropertyMetadataOptions.AffectsRender,
                    OnDataChanged));

        public Brush PlotBackground
        {
            get => (Brush)GetValue(PlotBackgroundProperty);
            set => SetValue(PlotBackgroundProperty, value);
        }

        public static readonly DependencyProperty PlotBackgroundProperty =
            DependencyProperty.Register(
                nameof(PlotBackground),
                typeof(Brush),
                typeof(SectionCutPreviewControl),
                new FrameworkPropertyMetadata(
                    Brushes.Transparent,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        // --------------------------
        // Internal View State
        // --------------------------

        private Matrix _viewMatrix = Matrix.Identity;

        private bool _isPanning;
        private Point _lastMouse;

        private bool _autoFitOnFirstValidData = true;

        public SectionCutPreviewControl()
        {
            Focusable = true;

            Loaded += (_, __) =>
            {
                // Don't force fit if there is no data.
                if (HasAnyData())
                    FitToContent();
            };

            // Pan/zoom interactions
            MouseWheel += OnMouseWheel;
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
        }

        // --------------------------
        // Rendering
        // --------------------------

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            // Clip to this control’s bounds so panning/zooming never draws outside
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

            if (!HasAnyData()) return;

            // Background
            dc.DrawRectangle(PlotBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));
            

            // Draw with view transform
            dc.PushTransform(new MatrixTransform(_viewMatrix));

            // ✅ NEW: pixel snapping in device space reduces flicker on zoom
            var guidelines = new GuidelineSet();
            guidelines.GuidelinesX.Add(0);
            guidelines.GuidelinesY.Add(0);
            dc.PushGuidelineSet(guidelines);

            DrawPolygons(dc);
            DrawCuts(dc);

            dc.Pop();
        }

        private void DrawPolygons(DrawingContext dc)
        {
            // Keep styling simple. You can tune later.
            var areaFill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 215));
            var areaPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)), 0.02);

            var openingFill = new SolidColorBrush(Color.FromArgb(45, 220, 0, 0));
            var openingPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 220, 0, 0)), 0.02);

            if (Areas != null)
            {
                foreach (var poly in Areas.Where(p => p != null && p.Count >= 3))
                {
                    var geom = PolyToGeometry(poly);
                    dc.DrawGeometry(areaFill, areaPen, geom);
                }
            }

            if (Openings != null)
            {
                foreach (var poly in Openings.Where(p => p != null && p.Count >= 3))
                {
                    var geom = PolyToGeometry(poly);
                    dc.DrawGeometry(openingFill, openingPen, geom);
                }
            }
        }

        private void DrawCuts(DrawingContext dc)
        {
            if (Cuts == null || Cuts.Count == 0) return;

            var orange = (SolidColorBrush)new BrushConverter().ConvertFromString("#ff8c69");
            orange.Freeze();

            // screen constant thickness (~2 px)
            double scaleX = Math.Abs(_viewMatrix.M11);
            if (scaleX < 1e-9) scaleX = 1;

            double thicknessWorld = 2.0 / scaleX;

            var pen = new Pen(orange, thicknessWorld)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            foreach (var seg in Cuts)
            {
                dc.DrawLine(pen, seg.A, seg.B);
            }
        }


        private static Geometry PolyToGeometry(PointCollection poly)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(poly[0], isFilled: true, isClosed: true);
                for (int i = 1; i < poly.Count; i++)
                    ctx.LineTo(poly[i], isStroked: true, isSmoothJoin: true);
            }
            g.Freeze();
            return g;
        }

        // --------------------------
        // Data / Fit-to-content
        // --------------------------

        private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (SectionCutPreviewControl)d;

            // Auto-fit once when first real data arrives
            if (ctrl._autoFitOnFirstValidData && ctrl.HasAnyData() && ctrl.ActualWidth > 0 && ctrl.ActualHeight > 0)
            {
                ctrl._autoFitOnFirstValidData = false;
                ctrl.FitToContent();
            }
        }

        private bool HasAnyData()
        {
            bool hasAreas = Areas != null && Areas.Any(p => p != null && p.Count >= 3);
            bool hasOpenings = Openings != null && Openings.Any(p => p != null && p.Count >= 3);
            bool hasCuts = Cuts != null && Cuts.Count > 0;

            return hasAreas || hasOpenings || hasCuts;
        }

        public void FitToContent()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                _viewMatrix = Matrix.Identity;
                InvalidateVisual();
                return;
            }

            var bounds = GetWorldBounds();
            if (bounds == null || bounds.Value.IsEmpty)
            {
                _viewMatrix = Matrix.Identity;
                InvalidateVisual();
                return;
            }

            Rect b = bounds.Value;

            // Padding
            const double marginFrac = 0.08;

            double worldW = Math.Max(b.Width, 1e-6);
            double worldH = Math.Max(b.Height, 1e-6);

            double usableW = ActualWidth * (1.0 - 2.0 * marginFrac);
            double usableH = ActualHeight * (1.0 - 2.0 * marginFrac);

            double scale = Math.Min(usableW / worldW, usableH / worldH);

            // Center in world coordinates
            double cx = b.X + b.Width / 2.0;
            double cy = b.Y + b.Height / 2.0;

            // Build view transform:
            // - scale
            // - flip Y (so +Y is "up" visually)
            // - translate to screen center
            var m = Matrix.Identity;
            m.Scale(scale, -scale);

            double tx = (ActualWidth / 2.0) - (cx * scale);
            double ty = (ActualHeight / 2.0) + (cy * scale);

            m.Translate(tx, ty);

            _viewMatrix = m;
            InvalidateVisual();
        }

        private Rect? GetWorldBounds()
        {
            var pts = new List<Point>();

            if (Areas != null)
            {
                foreach (var poly in Areas.Where(p => p != null))
                    pts.AddRange(poly);
            }

            if (Openings != null)
            {
                foreach (var poly in Openings.Where(p => p != null))
                    pts.AddRange(poly);
            }

            if (Cuts != null)
            {
                foreach (var seg in Cuts)
                {
                    pts.Add(seg.A);
                    pts.Add(seg.B);
                }
            }

            var valid = pts.Where(p =>
                !double.IsNaN(p.X) && !double.IsInfinity(p.X) &&
                !double.IsNaN(p.Y) && !double.IsInfinity(p.Y)).ToList();

            if (valid.Count == 0)
                return null;

            double minX = valid.Min(p => p.X);
            double maxX = valid.Max(p => p.X);
            double minY = valid.Min(p => p.Y);
            double maxY = valid.Max(p => p.Y);

            return new Rect(new Point(minX, minY), new Point(maxX, maxY));
        }

        // --------------------------
        // Interaction: Pan & Zoom
        // --------------------------

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!HasAnyData())
                return;

            var posScreen = e.GetPosition(this);
            double zoomFactor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;

            // Convert screen -> world
            Matrix inv = _viewMatrix;
            if (!inv.HasInverse) return;
            inv.Invert();

            Point posWorld = inv.Transform(posScreen);

            // Zoom around cursor (world point)
            Matrix m = _viewMatrix;
            m.Translate(-posWorld.X, -posWorld.Y);
            m.Scale(zoomFactor, zoomFactor);
            m.Translate(posWorld.X, posWorld.Y);

            _viewMatrix = m;
            InvalidateVisual();
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!HasAnyData())
                return;

            // Double middle click => fit
            if (e.ChangedButton == MouseButton.Middle && e.ClickCount == 2)
            {
                FitToContent();
                return;
            }

            // Pan with left mouse drag
            if (e.ChangedButton == MouseButton.Left)
            {
                _isPanning = true;
                _lastMouse = e.GetPosition(this);
                CaptureMouse();
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning || e.LeftButton != MouseButtonState.Pressed)
                return;

            var cur = e.GetPosition(this);
            Vector delta = cur - _lastMouse;

            Matrix m = _viewMatrix;
            m.Translate(delta.X, delta.Y);
            _viewMatrix = m;

            _lastMouse = cur;
            InvalidateVisual();
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isPanning = false;
                ReleaseMouseCapture();
            }
        }

        // --------------------------
        // Segment helper type
        // --------------------------

        public readonly struct Segment
        {
            public Segment(Point a, Point b)
            {
                A = a;
                B = b;
            }

            public Point A { get; }
            public Point B { get; }
        }
    }
}
