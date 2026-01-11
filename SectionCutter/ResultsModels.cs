using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace SectionCutter.ViewModels
{
    /// <summary>
    /// Represents an ETABS load case with analysis status.
    /// Status == 4 is typically "results available" (matches your WinForms behavior).
    /// </summary>
    public class LoadCaseItem
    {
        public string Name { get; set; }
        public int Status { get; set; }

        public bool HasResults => Status == 4;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Row model for the Results DataGrid.
    /// </summary>
    public class SectionCutResultRow
    {
        public string Name { get; set; }

        public double Shear { get; set; }
        public double Moment { get; set; }
        public double Axial { get; set; }

        public double Length { get; set; }
        public double UnitShear { get; set; }
        public double ChordForce { get; set; }
    }

    public class ResultCutPlotItem
    {
        public string Name { get; set; } = "";
        public Point A { get; set; }
        public Point B { get; set; }

        public double Value { get; set; }
        public double Length { get; set; }
    }
}
