using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ETABSv1;

namespace SectionCutter
{
    /// <summary>
    /// Persists SectionCut sets to a JSON file named "SectionCut.json" in the same folder
    /// as the currently-open ETABS model.
    ///
    /// Responsibilities:
    /// - Determine ETABS model folder
    /// - Save/Update a LIST of SectionCut sets (each identified by a prefix)
    /// - Load the full list (if present)
    /// - Load a specific saved set by prefix
    /// - Detect duplicates (same start node + areas + vector) and warn the caller
    /// - Provide a lightweight check for whether section cuts already exist in the ETABS model
    ///   for a prefix (by reading the "Section Cut Definitions" database table).
    ///
    /// Backward compatibility:
    /// - If an older "single object" JSON file is found, it is automatically migrated into the new list format.
    /// NEW: each saved set can now store the global XY cut endpoints directly for plotting:
    /// - CutSegmentsXY: list of (Name, X1,Y1,X2,Y2)
    /// </summary>
    public class SectionCutJsonStore
    {
        private const string JsonFileName = "SectionCut.json";
        private readonly cSapModel _sapModel;

        public SectionCutJsonStore(cSapModel sapModel)
        {
            _sapModel = sapModel ?? throw new ArgumentNullException(nameof(sapModel));
        }

        public string GetJsonPathForCurrentModel()
        {
            var modelPath = _sapModel.GetModelFilename(true);
            if (string.IsNullOrWhiteSpace(modelPath))
                return null;

            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return null;

            return Path.Combine(dir, JsonFileName);
        }

        // -----------------------------
        // Load
        // -----------------------------

        public bool TryLoadRoot(out SectionCutJsonRoot root)
        {
            root = null;

            var path = GetJsonPathForCurrentModel();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return false;

                // New list format
                try
                {
                    var parsedRoot = JsonSerializer.Deserialize<SectionCutJsonRoot>(json, JsonOptions());
                    if (parsedRoot?.Sets != null)
                    {
                        parsedRoot.Sets = parsedRoot.Sets.Where(s => s != null).ToList();
                        root = parsedRoot;
                        return true;
                    }
                }
                catch
                {
                    // ignore, try legacy
                }

                // Legacy single-object format migration
                try
                {
                    var legacy = JsonSerializer.Deserialize<SectionCutJsonData>(json, JsonOptions());
                    if (legacy != null && !string.IsNullOrWhiteSpace(legacy.SectionCutPrefix))
                    {
                        var migrated = new SectionCutJsonRoot
                        {
                            Sets = new List<SectionCutJsonData> { legacy }
                        };

                        SaveRoot(migrated);
                        root = migrated;
                        return true;
                    }
                }
                catch
                {
                    // ignore
                }

                return false;
            }
            catch
            {
                root = null;
                return false;
            }
        }

        public bool TryLoadByPrefix(string prefix, out SectionCutJsonData set)
        {
            set = null;

            if (string.IsNullOrWhiteSpace(prefix))
                return false;

            if (!TryLoadRoot(out var root) || root?.Sets == null)
                return false;

            set = root.Sets.FirstOrDefault(s =>s != null && string.Equals(s.SectionCutPrefix, prefix, StringComparison.OrdinalIgnoreCase));

            return set != null;
        }

        public List<string> GetAllPrefixes()
        {
            if (!TryLoadRoot(out var root) || root?.Sets == null)
                return new List<string>();

            return root.Sets
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SectionCutPrefix))
                .Select(s => s.SectionCutPrefix.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // -----------------------------
        // Save
        // -----------------------------

        public enum SaveSetResult
        {
            Added,
            Updated,
            DuplicateSignature,
            DuplicatePrefix
        }

        /// <summary>
        /// Saves a set. NEW: accepts cutSegmentsXY (global XY endpoints) for plotting later.
        /// </summary>
        public SaveSetResult SaveOrUpdateSet(
            SectionCut definition,
            IEnumerable<string> openingIds,
            IEnumerable<SectionCutCutSegmentXY> cutSegmentsXY,
            bool updateIfPrefixExists)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.SectionCutPrefix))
                throw new ArgumentException("SectionCutPrefix is required.", nameof(definition));

            var newSet = new SectionCutJsonData
            {
                StartNodeId = definition.StartNodeId,
                AreaIds = (definition.AreaIds ?? new List<string>()).ToList(),
                OpeningIds = (openingIds ?? Array.Empty<string>()).ToList(),
                XVector = definition.XVector,
                YVector = definition.YVector,
                SectionCutPrefix = definition.SectionCutPrefix,
                SavedUtc = DateTime.UtcNow,
                CutSegmentsXY = (cutSegmentsXY ?? Array.Empty<SectionCutCutSegmentXY>()).ToList()
            };

            var path = GetJsonPathForCurrentModel();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "ETABS model does not have a valid file path. Save the ETABS model first so SectionCut.json can be written next to it.");

            if (!TryLoadRoot(out var root) || root == null)
                root = new SectionCutJsonRoot();

            root.Sets ??= new List<SectionCutJsonData>();

            // Duplicate signature (start node + areas + vector)
            var newSig = MakeSignature(newSet);
            var sigDup = root.Sets.FirstOrDefault(s => s != null && MakeSignature(s) == newSig);
            if (sigDup != null)
                return SaveSetResult.DuplicateSignature;

            // Prefix check
            var prefixExisting = root.Sets.FirstOrDefault(s =>
                s != null && string.Equals(s.SectionCutPrefix, newSet.SectionCutPrefix, StringComparison.OrdinalIgnoreCase));

            if (prefixExisting != null)
            {
                if (!updateIfPrefixExists)
                    return SaveSetResult.DuplicatePrefix;

                prefixExisting.StartNodeId = newSet.StartNodeId;
                prefixExisting.AreaIds = newSet.AreaIds ?? new List<string>();
                prefixExisting.OpeningIds = newSet.OpeningIds ?? new List<string>();
                prefixExisting.XVector = newSet.XVector;
                prefixExisting.YVector = newSet.YVector;
                prefixExisting.SavedUtc = DateTime.UtcNow;

                // NEW: update cut endpoints too
                prefixExisting.CutSegmentsXY = newSet.CutSegmentsXY ?? new List<SectionCutCutSegmentXY>();

                SaveRoot(root);
                return SaveSetResult.Updated;
            }

            root.Sets.Add(newSet);
            SaveRoot(root);
            return SaveSetResult.Added;
        }

        private void SaveRoot(SectionCutJsonRoot root)
        {
            var path = GetJsonPathForCurrentModel();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "ETABS model does not have a valid file path. Save the ETABS model first so SectionCut.json can be written next to it.");

            root ??= new SectionCutJsonRoot();
            root.Sets ??= new List<SectionCutJsonData>();

            root.Sets = root.Sets
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SectionCutPrefix))
                .GroupBy(s => s.SectionCutPrefix.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.SavedUtc).First())
                .ToList();

            var json = JsonSerializer.Serialize(root, JsonOptions());
            File.WriteAllText(path, json);
        }

        private static string MakeSignature(SectionCutJsonData s)
        {
            string start = (s.StartNodeId ?? "").Trim();

            var areas = (s.AreaIds ?? new List<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

            double vx = Math.Round(s.XVector, 6);
            double vy = Math.Round(s.YVector, 6);

            return $"{start}|A=[{string.Join(",", areas)}]|V=({vx},{vy})";
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }
    }

    public class SectionCutJsonRoot
    {
        public List<SectionCutJsonData> Sets { get; set; } = new List<SectionCutJsonData>();
    }

    public class SectionCutJsonData
    {
        public string StartNodeId { get; set; }
        public List<string> AreaIds { get; set; } = new List<string>();
        public List<string> OpeningIds { get; set; } = new List<string>();
        public double XVector { get; set; }
        public double YVector { get; set; }
        public string SectionCutPrefix { get; set; }
        public DateTime SavedUtc { get; set; }

        // NEW: stored global XY endpoints of each cut for plotting after load
        public List<SectionCutCutSegmentXY> CutSegmentsXY { get; set; } = new List<SectionCutCutSegmentXY>();
    }

    /// <summary>
    /// Stored global XY cut endpoints (for plotting).
    /// Name is optional but helps with debugging and display.
    /// </summary>
    public class SectionCutCutSegmentXY
    {
        public string Name { get; set; }  // e.g. "L1X-0003"
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }
}
