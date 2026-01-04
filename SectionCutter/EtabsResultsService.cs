using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ETABSv1;
using SectionCutter.ViewModels;

namespace SectionCutter
{
    public class EtabsResultsService : IEtabsResultsService
    {
        private readonly cSapModel _sapModel;

        public EtabsResultsService(cSapModel sapModel)
        {
            _sapModel = sapModel ?? throw new ArgumentNullException(nameof(sapModel));
        }

        public List<LoadCaseItem> GetLoadCasesWithStatus()
        {
            int num = 0;
            string[] names = null;
            _sapModel.LoadCases.GetNameList(ref num, ref names);

            int nStatus = 0;
            string[] caseNames = null;
            int[] status = null;
            _sapModel.Analyze.GetCaseStatus(ref nStatus, ref caseNames, ref status);

            var statusMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (caseNames != null && status != null)
            {
                for (int i = 0; i < Math.Min(caseNames.Length, status.Length); i++)
                    statusMap[caseNames[i]] = status[i];
            }

            var items = new List<LoadCaseItem>();
            if (names != null)
            {
                foreach (var n in names)
                {
                    statusMap.TryGetValue(n, out int st);
                    items.Add(new LoadCaseItem { Name = n, Status = st });
                }
            }

            return items;
        }

        public List<SectionCutForceRecord> GetSectionCutForces(string loadCaseName)
        {
            if (string.IsNullOrWhiteSpace(loadCaseName))
                return new List<SectionCutForceRecord>();

            // Ensure ETABS outputs this case in the DB table (matches your Form1.cs workflow)
            _sapModel.Results.Setup.DeselectAllCasesAndCombosForOutput();
            _sapModel.Results.Setup.SetCaseSelectedForOutput(loadCaseName);

            const string tableKey = "Section Cut Forces - Analysis";

            string[] fieldKeyList = null;
            string groupName = "All";
            int tableVersion = 0;
            string[] fieldsIncluded = null;
            int numRecs = 0;
            string[] tableData = null;

            int ret = _sapModel.DatabaseTables.GetTableForDisplayArray(
                tableKey,
                ref fieldKeyList,
                groupName,
                ref tableVersion,
                ref fieldsIncluded,
                ref numRecs,
                ref tableData);

            if (ret != 0 || fieldsIncluded == null || tableData == null || numRecs <= 0)
                return new List<SectionCutForceRecord>();

            int nf = fieldsIncluded.Length;

            int idxCut = FindField(fieldsIncluded, "SectionCut", "Section Cut", "Cut");
            int idxCase = FindField(fieldsIncluded, "LoadCase", "Load Case", "Case");
            int idxF1 = FindField(fieldsIncluded, "F1");
            int idxF2 = FindField(fieldsIncluded, "F2");
            int idxM3 = FindField(fieldsIncluded, "M3");

            if (idxCut < 0 || idxCase < 0 || idxF1 < 0 || idxF2 < 0 || idxM3 < 0)
                return new List<SectionCutForceRecord>();

            var results = new List<SectionCutForceRecord>();

            for (int r2 = 0; r2 < numRecs; r2++)
            {
                int b = r2 * nf;
                if (b + nf > tableData.Length) break;

                string caseName = tableData[b + idxCase] ?? "";
                if (!caseName.Equals(loadCaseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rec = new SectionCutForceRecord
                {
                    SectionCutName = tableData[b + idxCut],
                    LoadCase = caseName,
                    F1 = ParseDouble(tableData[b + idxF1]),
                    F2 = ParseDouble(tableData[b + idxF2]),
                    M3 = ParseDouble(tableData[b + idxM3])
                };

                results.Add(rec);
            }

            return results;
        }

        private static int FindField(string[] fields, params string[] options)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                foreach (var opt in options)
                {
                    if (string.Equals(fields[i], opt, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static double ParseDouble(string s)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return v;

            return 0.0;
        }
    }
}
