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
            var results = new List<SectionCutForceRecord>();

            if (string.IsNullOrWhiteSpace(loadCaseName))
                return results;

            // 1) Make sure output is selected (ETABS requirement)
            _sapModel.Results.Setup.DeselectAllCasesAndCombosForOutput();
            _sapModel.Results.Setup.SetCaseSelectedForOutput(loadCaseName);

            // 2) Pull the "Section Cut Forces - Analysis" table
            const string tableKey = "Section Cut Forces - Analysis";

            string[] fieldKeyList = null;
            string groupName = "All";
            int tableVersion = 1; // important: 1 matches WinForms behavior reliably
            string[] fieldKeysIncluded = null;
            int numberRecords = 0;
            string[] tableData = null;

            int ret = _sapModel.DatabaseTables.GetTableForDisplayArray(
                tableKey,
                ref fieldKeyList,
                groupName,
                ref tableVersion,
                ref fieldKeysIncluded,
                ref numberRecords,
                ref tableData);

            if (ret != 0 || fieldKeysIncluded == null || tableData == null || numberRecords <= 0)
                return results;

            int nf = fieldKeysIncluded.Length;

            // Expect 15 fields like you showed, but don’t hard-crash; just require the ones we need.
            int idxSectionCut = IndexOfField(fieldKeysIncluded, "SectionCut");
            int idxOutputCase = IndexOfField(fieldKeysIncluded, "OutputCase");
            int idxF1 = IndexOfField(fieldKeysIncluded, "F1");
            int idxF2 = IndexOfField(fieldKeysIncluded, "F2");
            int idxF3 = IndexOfField(fieldKeysIncluded, "F3");
            int idxM3 = IndexOfField(fieldKeysIncluded, "M3");

            if (idxSectionCut < 0 || idxOutputCase < 0 || idxF1 < 0 || idxF2 < 0 || idxF3 < 0 || idxM3 < 0)
            {
                // In case ETABS changes headers; surface what's actually in this table.
                throw new Exception(
                    "Required fields not found in ETABS table.\n" +
                    $"Fields:\n - {string.Join("\n - ", fieldKeysIncluded)}");
            }

            // 3) Walk records in blocks of nf and filter by OutputCase == loadCaseName
            // numberRecords is the number of rows; tableData length should be numberRecords * nf
            for (int r = 0; r < numberRecords; r++)
            {
                int baseIdx = r * nf;

                string outputCase = tableData[baseIdx + idxOutputCase];

                if (!string.Equals(outputCase, loadCaseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string cutName = tableData[baseIdx + idxSectionCut];

                // If you want to ignore blanks / non-cuts:
                if (string.IsNullOrWhiteSpace(cutName))
                    continue;

                double f1 = ParseDouble(tableData[baseIdx + idxF1]);
                double f2 = ParseDouble(tableData[baseIdx + idxF2]);
                double f3 = ParseDouble(tableData[baseIdx + idxF3]);
                double m3 = ParseDouble(tableData[baseIdx + idxM3]);

                results.Add(new SectionCutForceRecord
                {
                    SectionCutName = cutName,
                    OutputCase = outputCase,
                    F1 = f1,
                    F2 = f2,
                    F3 = f3,
                    M3 = m3
                });
            }

            return results;
        }

        private static int IndexOfField(string[] fields, string name)
        {
            if (fields == null) return -1;
            for (int i = 0; i < fields.Length; i++)
            {
                if (string.Equals(fields[i]?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static double ParseDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0.0;

            // table strings can come back with "" literally or empty
            s = s.Trim();
            if (s == "\"\"" || s == "\"") return 0.0;

            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double v))
                return v;

            // fallback to current culture if ETABS is localized
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out v))
                return v;

            return 0.0;
        }
    }
}
