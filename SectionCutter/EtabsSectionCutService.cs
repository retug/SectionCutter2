using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ETABSv1;

namespace SectionCutter
{
    /// <summary>
    /// ETABS-based implementation of ISectionCutService.
    /// This class contains the logic previously in runAnalysis_Click,
    /// refactored into a reusable service method that:
    /// - Computes local coordinates
    /// - Builds section cut quads
    /// - Writes the "Section Cut Definitions" ETABS table
    /// - Returns a SectionCutSet + SectionCutSlice list
    /// - Writes/updates SectionCut.json next to the ETABS model (optional)
    /// </summary>
    public class EtabsSectionCutService : ISectionCutService
    {
        private readonly cSapModel _sapModel;
        private readonly SectionCutJsonStore _jsonStore; // optional

        public EtabsSectionCutService(cSapModel sapModel, SectionCutJsonStore jsonStore = null)
        {
            _sapModel = sapModel ?? throw new ArgumentNullException(nameof(sapModel));
            _jsonStore = jsonStore ?? throw new ArgumentNullException(nameof(jsonStore));
        }

        public SectionCutSet CreateEtabsSectionCuts(SectionCut definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (string.IsNullOrWhiteSpace(definition.StartNodeId))
                throw new InvalidOperationException("SectionCut.StartNodeId is not set.");

            if (definition.AreaIds == null || definition.AreaIds.Count == 0)
                throw new InvalidOperationException("SectionCut.AreaIds is empty. Select at least one area.");

            if (definition.NumberOfCuts <= 0)
                throw new InvalidOperationException("SectionCut.NumberOfCuts must be greater than zero.");

            // 1) Set ETABS units based on definition.Units
            var units = GetUnitsFromDefinition(definition.Units);
            _sapModel.SetPresentUnits(units);

            // 2) Reference point = coordinates of StartNodeId
            double X = 0, Y = 0, Z = 0;
            int ret = _sapModel.PointObj.GetCoordCartesian(definition.StartNodeId, ref X, ref Y, ref Z);
            if (ret != 0)
                throw new InvalidOperationException($"Failed to get coordinates for point {definition.StartNodeId} from ETABS.");

            var refPointList = new List<double> { X, Y, Z };

            // Direction vector from user inputs
            var vector = new List<double>
            {
                definition.XVector,
                definition.YVector,
                0.0
            };

            // 3) Build global/local coordinate system
            var gcs = new GlobalCoordinateSystem(refPointList, vector);

            // 4) Build polygon geometry for all selected areas
            var etabsAreaPointsLocal = new List<ETABS_Point>();      // all local points (for U/V bounds)
            var areaLineListLocal = new List<List<Line>>();          // lines in local coords
            var areaLineListGlobal = new List<List<Line>>();         // lines in global (if needed)

            foreach (var areaId in definition.AreaIds)
            {
                BuildAreaGeometry(areaId, gcs, etabsAreaPointsLocal, areaLineListLocal, areaLineListGlobal);
            }

            if (!etabsAreaPointsLocal.Any())
                throw new InvalidOperationException("No area points were found for the selected areas.");

            // 5) Find U/V bounds in local coordinates
            double Umax = etabsAreaPointsLocal.Max(p => p.X);
            double Umin = etabsAreaPointsLocal.Min(p => p.X);
            double Vmax = etabsAreaPointsLocal.Max(p => p.Y);
            double Vmin = etabsAreaPointsLocal.Min(p => p.Y);

            // 6) Section cut plane Z coordinates using HeightAbove / HeightBelow
            //    refPoint Z is the reference elevation
            double refZ = Z;
            double bottomZ = refZ - definition.HeightBelow;
            double topZ = refZ + definition.HeightAbove;

            // 7) Angle about Z for the section cut orientation
            double angleDeg;
            if (Math.Abs(definition.YVector) < 1e-8)
            {
                angleDeg = 90.0;
            }
            else
            {
                // Use Atan2 (correct) instead of Atan(x/y)*180*pi (incorrect)
                angleDeg = Math.Atan2(definition.XVector, definition.YVector) * 180.0 / Math.PI;
            }

            // 8) Generate U positions for the slices (skip exactly at bounds)
            int nCuts = definition.NumberOfCuts;
            var uValues = Linspace(Umin + 1.0, Umax - 1.0, nCuts);


            // 9) For ETABS table data and in-memory slice models
            var etabsSectionCutData = new List<List<string>>();
            var slices = new List<SectionCutSlice>();

            // NEW: collect global XY cut endpoints for JSON plotting
            var cutSegmentsXY = new List<SectionCutCutSegmentXY>();

            int counter = 0;

            foreach (double u in uValues)
            {
                // Ray from Vmin -> Vmax at this U, spanning full height in local coords
                var tempPoint1 = new List<double> { u, Vmin, bottomZ };
                var tempPoint2 = new List<double> { u, Vmax, topZ };

                var sectionPoint1 = new MyPoint(tempPoint1);
                var sectionPoint2 = new MyPoint(tempPoint2);

                var xingPoints = new List<MyPoint>();

                // Ray cast against local area edges
                RayCasting.RayCast(sectionPoint1, sectionPoint2, gcs, areaLineListLocal, out int countCrosses, ref xingPoints);

                if (countCrosses < 2)
                {
                    // No proper intersection; skip this slice
                    continue;
                }

                // Use first two intersection points
                var p0 = xingPoints[0];
                var p1 = xingPoints[1];

                double length = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));

                // Build quad points in local coordinates
                var listPoint1 = new List<double> { p0.X, p0.Y, bottomZ };
                var listPoint2 = new List<double> { p0.X, p0.Y, topZ };
                var listPoint3 = new List<double> { p1.X, p1.Y, topZ };
                var listPoint4 = new List<double> { p1.X, p1.Y, bottomZ };

                // Section cut name, incorporate prefix
                string namePrefix = string.IsNullOrWhiteSpace(definition.SectionCutPrefix)
                    ? string.Empty
                    : definition.SectionCutPrefix;

                string name = $"{namePrefix}{counter.ToString().PadLeft(4, '0')}";

                // NEW: store global XY segment endpoints for plotting later
                cutSegmentsXY.Add(new SectionCutCutSegmentXY
                {
                    Name = name,
                    X1 = p0.X,
                    Y1 = p0.Y,
                    X2 = p1.X,
                    Y2 = p1.Y
                });

                // Build ETABS table records (4 rows per section cut)
                var row1 = new List<string>
                {
                    name, "Quads", "All", "Analysis", "Default",
                    angleDeg.ToString(),  // RotAboutZ
                    "0",                  // RotAboutY
                    "0",                  // RotAboutX
                    "Top or Right or Positive3",
                    "1",                  // NumQuads
                    "1",                  // QuadNum
                    "1",                  // PointNum
                    listPoint1[0].ToString(),
                    listPoint1[1].ToString(),
                    listPoint1[2].ToString(),
                    "1"                   // GUID placeholder
                };

                var row2 = new List<string>
                {
                    name, null, null, null, null,
                    null, null, null, null, null,
                    "1",                  // QuadNum
                    "2",                  // PointNum
                    listPoint2[0].ToString(),
                    listPoint2[1].ToString(),
                    listPoint2[2].ToString(),
                    null
                };

                var row3 = new List<string>
                {
                    name, null, null, null, null,
                    null, null, null, null, null,
                    "1",
                    "3",
                    listPoint3[0].ToString(),
                    listPoint3[1].ToString(),
                    listPoint3[2].ToString(),
                    null
                };

                var row4 = new List<string>
                {
                    name, null, null, null, null,
                    null, null, null, null, null,
                    "1",
                    "4",
                    listPoint4[0].ToString(),
                    listPoint4[1].ToString(),
                    listPoint4[2].ToString(),
                    null
                };

                etabsSectionCutData.Add(row1);
                etabsSectionCutData.Add(row2);
                etabsSectionCutData.Add(row3);
                etabsSectionCutData.Add(row4);

                // Build in-memory slice model
                var slice = new SectionCutSlice(
                    index: counter,
                    name: name,
                    localStartU: p0.X,
                    localStartV: p0.Y,
                    localEndU: p1.X,
                    localEndV: p1.Y,
                    length: length
                );

                slices.Add(slice);
                counter++;
            }

            // 10) Push data to ETABS "Section Cut Definitions" table
            if (etabsSectionCutData.Count > 0)
            {
                WriteSectionCutTableToEtabs(etabsSectionCutData);
            }

            // 11) Save inputs to SectionCut.json (next to the ETABS model), including opening ids
            //     Opening IDs are those area objects for which AreaObj.GetOpening(...) == true.
            if (_jsonStore != null)
            {
                var openingIds = new List<string>();

                foreach (var areaId in definition.AreaIds ?? new List<string>())
                {
                    bool isOpening = false;
                    int r2 = _sapModel.AreaObj.GetOpening(areaId, ref isOpening);
                    if (r2 == 0 && isOpening)
                        openingIds.Add(areaId);
                }

                // NEW: persist the set including cut XY endpoints
                var saveResult = _jsonStore.SaveOrUpdateSet(definition, openingIds, cutSegmentsXY, updateIfPrefixExists: true);

                if (saveResult == SectionCutJsonStore.SaveSetResult.DuplicateSignature)
                {
                    MessageBox.Show(
                        "This section cut set already exists (same start node, areas, and vector).\n" +
                        "Cuts were created in ETABS, but the JSON set was not duplicated.",
                        "Duplicate Set",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }


                else if (saveResult == SectionCutJsonStore.SaveSetResult.DuplicatePrefix)
                {
                    // Prefix already exists
                    MessageBox.Show(
                        $"A saved set with prefix \"{definition.SectionCutPrefix}\" already exists.\n" +
                        "Choose a different prefix (or allow overwrite if you want that behavior).",
                        "Duplicate Prefix",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

            }

            // Return the SectionCutSet for MVVM / later plotting
            var set = new SectionCutSet(definition, slices);
            return set;
        }

        #region Helper methods

        private eUnits GetUnitsFromDefinition(string units)
        {
            if (string.IsNullOrWhiteSpace(units))
                return eUnits.kip_ft_F;

            units = units.Trim().ToLowerInvariant();

            if (units.Contains("kip") && units.Contains("ft"))
                return eUnits.kip_ft_F;

            if (units.Contains("kn") && (units.Contains("m") || units.Contains("meter")))
                return eUnits.kN_m_C;

            // Default fallback
            return eUnits.kip_ft_F;
        }

        /// <summary>
        /// Uses ETABS AreaObj + PointObj to get polygon points for an area,
        /// converts them to local coordinates, builds line segments,
        /// and accumulates them in the supplied collections.
        /// </summary>
        private void BuildAreaGeometry(
            string areaId,
            GlobalCoordinateSystem gcs,
            List<ETABS_Point> allLocalPoints,
            List<List<Line>> areaLineListLocal,
            List<List<Line>> areaLineListGlobal)
        {
            int numberPoints = 0;
            string[] pointNames = null;

            int ret = _sapModel.AreaObj.GetPoints(areaId, ref numberPoints, ref pointNames);
            if (ret != 0 || pointNames == null || numberPoints == 0)
                return;

            var localPointsForArea = new List<MyPoint>();
            var globalPointsForArea = new List<MyPoint>();

            double X = 0, Y = 0, Z = 0;

            // Collect points
            for (int i = 0; i < numberPoints; i++)
            {
                string ptName = pointNames[i];
                _sapModel.PointObj.GetCoordCartesian(ptName, ref X, ref Y, ref Z);

                var globalPointCoords = new List<double> { X, Y, Z };
                var globalMyPoint = new MyPoint(globalPointCoords)
                {
                    X = X,
                    Y = Y,
                    Z = Z
                };

                // Convert to local coordinates
                var localMyPoint = new MyPoint(globalPointCoords);
                localMyPoint.glo_to_loc(gcs);

                localMyPoint.X = localMyPoint.LocalCoords[0];
                localMyPoint.Y = localMyPoint.LocalCoords[1];
                localMyPoint.Z = localMyPoint.LocalCoords[2];


                var localEtabsPoint = new ETABS_Point
                {
                    X = localMyPoint.LocalCoords[0],
                    Y = localMyPoint.LocalCoords[1],
                    Z = localMyPoint.LocalCoords[2]
                };
                allLocalPoints.Add(localEtabsPoint);

                // Store for line-building
                localPointsForArea.Add(localMyPoint);
                globalPointsForArea.Add(globalMyPoint);
            }

            // Build closed polygon lines (local + global)
            var localLines = new List<Line>();
            var globalLines = new List<Line>();

            for (int i = 0; i < localPointsForArea.Count; i++)
            {
                int j = (i + 1) % localPointsForArea.Count;

                var localLine = new Line
                {
                    startPoint = localPointsForArea[i],
                    endPoint = localPointsForArea[j]
                };
                localLines.Add(localLine);

                var globalLine = new Line
                {
                    startPoint = globalPointsForArea[i],
                    endPoint = globalPointsForArea[j]
                };
                globalLines.Add(globalLine);
            }

            areaLineListLocal.Add(localLines);
            areaLineListGlobal.Add(globalLines);
        }

        /// <summary>
        /// Helper to generate N equally spaced values between start and end, inclusive.
        /// </summary>
        private List<double> Linspace(double start, double end, int n)
        {
            var result = new List<double>();
            if (n <= 1)
            {
                result.Add(start);
                return result;
            }

            double step = (end - start) / (n - 1);
            for (int i = 0; i < n; i++)
            {
                result.Add(start + i * step);
            }

            return result;
        }

        /// <summary>
        /// Writes the prepared section cut records into the ETABS database table.
        /// </summary>
        private void WriteSectionCutTableToEtabs(List<List<string>> newRows)
        {
            if (newRows == null || newRows.Count == 0)
                return;

            string tableKey = "Section Cut Definitions";

            // Field names (must match what ETABS expects for this table)
            string[] fieldKeys = new string[]
            {
        "Name", "DefinedBy", "Group", "ResultType", "ResultLoc",
        "RotAboutZ", "RotAboutY", "RotAboutX",
        "ElementSide", "NumQuads", "QuadNum", "PointNum",
        "QuadX", "QuadY", "QuadZ", "GUID"
            };

            // ----------------------------
            // 1) Read existing table rows
            // ----------------------------
            string[] fieldKeyList = null;
            string groupName = "All";
            int tableVersion = 1;
            string[] fieldKeysIncluded = null;
            int existingRecords = 0;
            string[] existingData = null;

            int ret = _sapModel.DatabaseTables.GetTableForDisplayArray(
                tableKey,
                ref fieldKeyList,
                groupName,
                ref tableVersion,
                ref fieldKeysIncluded,
                ref existingRecords,
                ref existingData);

            // If ETABS can't read it, we fall back to "write only new"
            // (but do not crash; still allows creation in a fresh model)
            var combinedRows = new List<List<string>>();

            // Normalize new rows (ensure correct column count)
            int cols = fieldKeys.Length;
            var normalizedNew = newRows
                .Where(r => r != null)
                .Select(r => r.Count == cols ? r : PadOrTrim(r, cols))
                .ToList();

            // ----------------------------
            // 2) Build a key for duplicate protection
            //    (Name + QuadNum + PointNum) is usually enough
            // ----------------------------
            static string MakeKey(List<string> row)
            {
                string name = row[0] ?? "";
                string quadNum = row[10] ?? "";
                string pointNum = row[11] ?? "";
                return $"{name}||{quadNum}||{pointNum}";
            }

            var newKeys = new HashSet<string>(normalizedNew.Select(MakeKey), StringComparer.OrdinalIgnoreCase);

            if (ret == 0 && existingRecords > 0 && existingData != null && fieldKeysIncluded != null)
            {
                // We must map the "included" fields to our fieldKeys order
                // so we can re-write the full table with a consistent schema.
                var idxMap = BuildIndexMap(fieldKeysIncluded, fieldKeys);

                int nf = fieldKeysIncluded.Length;

                for (int r = 0; r < existingRecords; r++)
                {
                    int b = r * nf;
                    if (b + nf > existingData.Length) break;

                    var row = new List<string>(cols);

                    for (int c = 0; c < cols; c++)
                    {
                        int src = idxMap[c];
                        row.Add(src >= 0 ? existingData[b + src] : "");
                    }

                    // Skip any existing rows that collide with what we’re adding
                    // (prevents re-adding same Name/Quad/Point)
                    if (!newKeys.Contains(MakeKey(row)))
                        combinedRows.Add(row);
                }
            }

            // ----------------------------
            // 3) Append new rows
            // ----------------------------
            combinedRows.AddRange(normalizedNew);

            // Flatten row-wise
            string[] tableData = combinedRows.SelectMany(r => r).ToArray();
            int numberRecords = combinedRows.Count;

            // ----------------------------
            // 4) Write back combined table
            // ----------------------------
            tableVersion = 1; // safe default for editing

            ret = _sapModel.DatabaseTables.SetTableForEditingArray(
                tableKey,
                ref tableVersion,
                ref fieldKeys,
                numberRecords,
                ref tableData);

            if (ret != 0)
                throw new InvalidOperationException("Failed to set ETABS section cut table for editing.");

            bool fillImportLog = true;
            int numFatalErrors = 0;
            int numErrorMsgs = 0;
            int numWarnMsgs = 0;
            int numInfoMsgs = 0;
            string importLog = string.Empty;

            ret = _sapModel.DatabaseTables.ApplyEditedTables(
                fillImportLog,
                ref numFatalErrors,
                ref numErrorMsgs,
                ref numWarnMsgs,
                ref numInfoMsgs,
                ref importLog);

            if (ret != 0 || numFatalErrors > 0)
            {
                throw new InvalidOperationException(
                    $"ETABS ApplyEditedTables failed. FatalErrors={numFatalErrors}, Errors={numErrorMsgs}, Log={importLog}");
            }
        }

        private static List<string> PadOrTrim(List<string> row, int cols)
        {
            var r = row.ToList();
            if (r.Count > cols) r = r.Take(cols).ToList();
            while (r.Count < cols) r.Add("");
            return r;
        }

        private static int[] BuildIndexMap(string[] included, string[] desired)
        {
            // returns for each desired column, the index in included (or -1)
            var map = new int[desired.Length];
            for (int i = 0; i < desired.Length; i++)
            {
                map[i] = Array.FindIndex(included, k => string.Equals(k, desired[i], StringComparison.OrdinalIgnoreCase));
            }
            return map;
        }

        #endregion
    }
}
