using System.Collections.Generic;
using SectionCutter.ViewModels;

namespace SectionCutter
{
    public interface IEtabsResultsService
    {
        List<LoadCaseItem> GetLoadCasesWithStatus();

        /// <summary>
        /// Reads ETABS table "Section Cut Forces - Analysis" for the selected load case.
        /// Returns raw records that include F1, F2, M3 for each section cut name.
        /// </summary>
        List<SectionCutForceRecord> GetSectionCutForces(string loadCaseName);
    }

    public class SectionCutForceRecord
    {
        public string SectionCutName { get; set; }
        public string LoadCase { get; set; }
        public double F1 { get; set; } // shear
        public double F2 { get; set; } // axial
        public double M3 { get; set; } // moment
    }
}
