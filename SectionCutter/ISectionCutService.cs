using System;

namespace SectionCutter
{
    /// <summary>
    /// Service responsible for creating section cuts in ETABS and
    /// returning the corresponding SectionCutSet model.
    /// </summary>
    public interface ISectionCutService
    {
        /// <summary>
        /// Creates section cuts in the ETABS model based on the supplied definition
        /// and returns a SectionCutSet representing the generated cuts.
        /// </summary>
        SectionCutSet CreateEtabsSectionCuts(SectionCut definition);
        // NEW:
        void DeleteEtabsSectionCutsByPrefix(string prefix);
    }
}
