using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SectionCutter
{
    public class RayCasting
    {
        // Function to check if two line segments intersect and calculate the intersection point
        public static bool Intersect(double x1, double y1, double x2, double y2,
                                     double x3, double y3, double x4, double y4,
                                     out double xi, out double yi)
        {
            // Calculate the denominator for parametric equations
            double denominator = (y4 - y3) * (x2 - x1) - (x4 - x3) * (y2 - y1);

            // Check if the line segments are parallel or coincident
            if (denominator == 0)
            {
                xi = 0;
                yi = 0;
                return false;
            }

            // Calculate the parameters for each line segment
            double ua = ((x4 - x3) * (y1 - y3) - (y4 - y3) * (x1 - x3)) / denominator;
            double ub = ((x2 - x1) * (y1 - y3) - (y2 - y1) * (x1 - x3)) / denominator;

            // Check if the intersection point lies within both line segments
            if (ua >= 0 && ua <= 1 && ub >= 0 && ub <= 1)
            {
                // Calculate the intersection point
                xi = x1 + ua * (x2 - x1);
                yi = y1 + ua * (y2 - y1);
                return true;
            }

            xi = 0;
            yi = 0;
            return false;
        }

        // ============================
        // CHANGED: Robust vertical-ray vs segment intersection
        // - Your ray in local UV is always u = constant (vertical in UV).
        // - When vector = (0,1), many edges become vertical too => denominator == 0 in Intersect()
        // - This function correctly handles vertical AND collinear cases.
        // ============================
        private static bool IntersectVerticalRayWithSegment(
            double uRay,
            double vRayMin,
            double vRayMax,
            double u1, double v1,
            double u2, double v2,
            out double xi, out double yi)
        {
            const double EPS = 1e-9;
            xi = yi = 0;

            // Normalize ray v-range
            double rvMin = Math.Min(vRayMin, vRayMax);
            double rvMax = Math.Max(vRayMin, vRayMax);

            // Segment is (nearly) vertical in u
            if (Math.Abs(u2 - u1) < EPS)
            {
                // Not on the same u => no intersection
                if (Math.Abs(uRay - u1) >= EPS)
                    return false;

                // Collinear overlap in v
                double svMin = Math.Min(v1, v2);
                double svMax = Math.Max(v1, v2);

                double ovMin = Math.Max(rvMin, svMin);
                double ovMax = Math.Min(rvMax, svMax);

                if (ovMax + EPS < ovMin)
                    return false;

                // Choose a representative intersection point (midpoint of overlap)
                xi = uRay;
                yi = (ovMin + ovMax) * 0.5;
                return true;
            }

            // Segment not vertical: solve for t where u(t) = uRay
            double t = (uRay - u1) / (u2 - u1);
            if (t < -EPS || t > 1 + EPS)
                return false;

            double vAt = v1 + t * (v2 - v1);

            // Check vAt within ray segment range
            if (vAt + EPS < rvMin || vAt - EPS > rvMax)
                return false;

            xi = uRay;
            yi = vAt;
            return true;
        }

        // Function Takes a start point, a List of all lines that need to be checked (inc. openings) and will return the quantity of crosses
        // and the location of crosses. These crosses will be used to generate section cuts
        public static bool RayCast(MyPoint startPoint, MyPoint endPoint, GlobalCoordinateSystem gcs,
                                   List<List<Line>> checkLines, out int countCrosses, ref List<MyPoint> xingPoints)
        {
            countCrosses = 0;

            double uStart = startPoint.X;

            // ============================
            // NOTE: You are constructing a "vertical" ray in UV by keeping u constant.
            // CHANGED: we keep this, but the intersection routine now handles vertical/collinear edges correctly.
            // ============================
            double vStart = startPoint.Y - 1;
            double wStart = startPoint.Z;

            double uEnd = endPoint.X;       // usually same as uStart in your usage
            double vEnd = endPoint.Y + 1;

            List<MyPoint> xingPntLocal = new List<MyPoint>();

            for (int i = 0; i < checkLines.Count(); i++)
            {
                for (int j = 0; j < checkLines[i].Count(); j++)
                {
                    double u1 = checkLines[i][j].startPoint.X;
                    double v1 = checkLines[i][j].startPoint.Y;
                    double u2 = checkLines[i][j].endPoint.X;
                    double v2 = checkLines[i][j].endPoint.Y;

                    // ============================
                    // CHANGED: replace generic segment-segment Intersect with robust vertical-ray intersection
                    // ============================
                    bool cross = IntersectVerticalRayWithSegment(
                        uStart, vStart, vEnd,
                        u1, v1, u2, v2,
                        out double xi, out double yi);

                    if (cross)
                    {
                        countCrosses += 1;
                        MyPoint crsPnt = new MyPoint(new List<double> { xi, yi, wStart });
                        xingPntLocal.Add(crsPnt);
                    }
                }
            }

            // ============================
            // CHANGED: guard against empty / insufficient intersections (prevents Max/Min crash)
            // ============================
            if (xingPntLocal.Count < 2)
            {
                // Nothing to add; no valid section cut segment
                return false;
            }

            double Vmax = xingPntLocal.Max(x => x.Y);
            double Vmin = xingPntLocal.Min(x => x.Y);

            MyPoint crsLocal1 = new MyPoint(new List<double> { uStart, Vmin - 0.25, wStart });
            MyPoint crsLocal2 = new MyPoint(new List<double> { uStart, Vmax + 0.25, wStart });

            crsLocal1.loc_to_glo(gcs);
            crsLocal2.loc_to_glo(gcs);

            //convert results back to global coordinates, plug these values into ETABs
            MyPoint gloPoint1 = new MyPoint(new List<double> { crsLocal1.GlobalCoords[0], crsLocal1.GlobalCoords[1], crsLocal1.GlobalCoords[2] });
            MyPoint gloPoint2 = new MyPoint(new List<double> { crsLocal2.GlobalCoords[0], crsLocal2.GlobalCoords[1], crsLocal2.GlobalCoords[2] });

            //returns the max and min v crossings from the algorithm
            xingPoints.Add(gloPoint1);
            xingPoints.Add(gloPoint2);

            // ============================
            // CHANGED: return true when we actually found a valid cut segment
            // ============================
            return true;
        }
    }
}
