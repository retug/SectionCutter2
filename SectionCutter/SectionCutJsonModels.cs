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
    /// </summary>
    public class SectionCutJsonStore
    {
        private const string JsonFileName = "SectionCut.json";
        private readonly cSapModel _sapModel;

        public SectionCutJsonStore(cSapModel sapModel)
        {
            _sapModel = sapModel ?? throw new ArgumentNullException(nameof(sapModel));
        }

        // -----------------------------
        // File path helpers
        // -----------------------------

        public string GetJsonPathForCurrentModel()
        {
            // ETABS API: GetModelFilename(true) returns full path (if model has been saved).
            var modelPath = _sapModel.GetModelFilename(true);
            if (string.IsNullOrWhiteSpace(modelPath))
                return null;

            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return null;

            return Path.Combine(dir, JsonFileName);
        }

        // -----------------------------
        // Public Load APIs
        // -----------------------------

        /// <summary>
        /// Loads the entire JSON root (list of sets). Returns false if not found or invalid.
        /// If a legacy single-object JSON is found, it will be migrated into the list format.
        /// </summary>
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

                // Try new format first
                try
                {
                    var parsedRoot = JsonSerializer.Deserialize<SectionCutJsonRoot>(json, JsonOptions());
                    if (parsedRoot?.Sets != null)
                    {
                        // Clean null entries if any
                        parsedRoot.Sets = parsedRoot.Sets.Where(s => s != null).ToList();
                        root = parsedRoot;
                        return true;
                    }
                }
                catch
                {
                    // ignore, try legacy
                }

                // Try legacy single-object format and migrate
                try
                {
                    var legacy = JsonSerializer.Deserialize<SectionCutJsonData>(json, JsonOptions());
                    if (legacy != null && !string.IsNullOrWhiteSpace(legacy.SectionCutPrefix))
                    {
                        var migrated = new SectionCutJsonRoot
                        {
                            Sets = new List<SectionCutJsonData> { legacy }
                        };

                        SaveRoot(migrated); // migrate in place
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
                // If json is malformed, don't crash the plugin.
                root = null;
                return false;
            }
        }

        /// <summary>
        /// Loads a saved set by prefix. Returns false if not found or if the json file is missing/invalid.
        /// </summary>
        public bool TryLoadByPrefix(string prefix, out SectionCutJsonData set)
        {
            set = null;

            if (string.IsNullOrWhiteSpace(prefix))
                return false;

            if (!TryLoadRoot(out var root) || root?.Sets == null)
                return false;

            set = root.Sets.FirstOrDefault(s =>
                s != null && string.Equals(s.SectionCutPrefix, prefix, StringComparison.OrdinalIgnoreCase));

            return set != null;
        }

        /// <summary>
        /// Returns all saved prefixes (distinct, sorted). Empty list if no file/invalid file.
        /// </summary>
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
        // Public Save APIs
        // -----------------------------

        /// <summary>
        /// Save/Update behavior outcome.
        /// </summary>
        public enum SaveSetResult
        {
            Added,
            Updated,
            DuplicateSignature,
            DuplicatePrefix
        }

        /// <summary>
        /// Adds a new set or updates an existing one (by prefix).
        ///
        /// Duplicate detection:
        /// - DuplicateSignature: same StartNodeId + same AreaIds (order-independent) + same vector (rounded tolerance).
        ///   This is flagged even if the prefix is different.
        /// - DuplicatePrefix: prefix already exists and updateIfPrefixExists == false.
        ///
        /// Caller can decide whether to allow overwriting by prefix.
        /// </summary>
        public SaveSetResult SaveOrUpdateSet(SectionCut definition, IEnumerable<string> openingIds, bool updateIfPrefixExists)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.SectionCutPrefix))
                throw new ArgumentException("SectionCutPrefix is required.", nameof(definition));

            // Build the new set payload
            var newSet = new SectionCutJsonData
            {
                StartNodeId = definition.StartNodeId,
                AreaIds = (definition.AreaIds ?? new List<string>()).ToList(),
                OpeningIds = (openingIds ?? Array.Empty<string>()).ToList(),
                XVector = definition.XVector,
                YVector = definition.YVector,
                SectionCutPrefix = definition.SectionCutPrefix,
                SavedUtc = DateTime.UtcNow
            };

            var path = GetJsonPathForCurrentModel();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "ETABS model does not have a valid file path. Save the ETABS model first so SectionCut.json can be written next to it.");

            // Load existing or create new root
            if (!TryLoadRoot(out var root) || root == null)
                root = new SectionCutJsonRoot();

            root.Sets ??= new List<SectionCutJsonData>();

            // Normalize comparisons
            var newSig = MakeSignature(newSet);

            // (1) Duplicate signature check (even if prefix differs)
            var sigDup = root.Sets.FirstOrDefault(s => s != null && MakeSignature(s) == newSig);
            if (sigDup != null)
                return SaveSetResult.DuplicateSignature;

            // (2) Prefix check
            var prefixExisting = root.Sets.FirstOrDefault(s =>
                s != null && string.Equals(s.SectionCutPrefix, newSet.SectionCutPrefix, StringComparison.OrdinalIgnoreCase));

            if (prefixExisting != null)
            {
                if (!updateIfPrefixExists)
                    return SaveSetResult.DuplicatePrefix;

                // Update existing set in-place
                prefixExisting.StartNodeId = newSet.StartNodeId;
                prefixExisting.AreaIds = newSet.AreaIds ?? new List<string>();
                prefixExisting.OpeningIds = newSet.OpeningIds ?? new List<string>();
                prefixExisting.XVector = newSet.XVector;
                prefixExisting.YVector = newSet.YVector;
                prefixExisting.SavedUtc = DateTime.UtcNow;

                SaveRoot(root);
                return SaveSetResult.Updated;
            }

            // (3) Add new set
            root.Sets.Add(newSet);
            SaveRoot(root);
            return SaveSetResult.Added;
        }

        // -----------------------------
        // ETABS existence check
        // -----------------------------

        /// <summary>
        /// Checks the current ETABS model for any section cut definition names
        /// that start with the provided prefix.
        /// </summary>
        public List<string> GetExistingSectionCutNamesByPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return new List<string>();

            string tableKey = "Section Cut Definitions";
            string[] fieldKeyList = null;
            string groupName = "All";
            int tableVersion = 1;
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
                return new List<string>();

            int numFields = fieldKeysIncluded.Length;
            int nameIndex = Array.FindIndex(fieldKeysIncluded, k => string.Equals(k, "Name", StringComparison.OrdinalIgnoreCase));
            if (nameIndex < 0)
                return new List<string>();

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int r = 0; r < numberRecords; r++)
            {
                int baseIdx = r * numFields;
                if (baseIdx + nameIndex >= tableData.Length) break;

                var name = tableData[baseIdx + nameIndex];
                if (!string.IsNullOrWhiteSpace(name) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }

            return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // -----------------------------
        // Internal save helpers
        // -----------------------------

        private void SaveRoot(SectionCutJsonRoot root)
        {
            var path = GetJsonPathForCurrentModel();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "ETABS model does not have a valid file path. Save the ETABS model first so SectionCut.json can be written next to it.");

            root ??= new SectionCutJsonRoot();
            root.Sets ??= new List<SectionCutJsonData>();

            // Remove nulls + de-dupe by prefix (keep most recent if duplicates somehow exist)
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

            // Signature is based on AreaIds only (openings are derived from selection and can vary).
            // If you want openings to be part of the uniqueness test, include them here as well.
            var areas = (s.AreaIds ?? new List<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

            // round vectors to avoid tiny float diffs
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

    /// <summary>
    /// JSON root payload. Stores a list of saved sets.
    /// </summary>
    public class SectionCutJsonRoot
    {
        public List<SectionCutJsonData> Sets { get; set; } = new List<SectionCutJsonData>();
    }

    /// <summary>
    /// JSON payload that stores the inputs used to create section cuts.
    /// </summary>
    public class SectionCutJsonData
    {
        public string StartNodeId { get; set; }
        public List<string> AreaIds { get; set; } = new List<string>();
        public List<string> OpeningIds { get; set; } = new List<string>();
        public double XVector { get; set; }
        public double YVector { get; set; }
        public string SectionCutPrefix { get; set; }
        public DateTime SavedUtc { get; set; }
    }
}
