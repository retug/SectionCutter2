using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ETABSv1;

namespace SectionCutter
{
    /// <summary>
    /// Persists SectionCut inputs to a JSON file named "SectionCut.json" in the same folder
    /// as the currently-open ETABS model.
    ///
    /// Responsibilities:
    /// - Determine ETABS model folder
    /// - Save the latest SectionCut definition (including AreaIds + OpeningIds)
    /// - Load the saved definition (if present)
    /// - Provide a lightweight check for whether section cuts already exist in the ETABS model
    ///   for the saved prefix (by reading the "Section Cut Definitions" database table).
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
            // ETABS API: GetModelFilename(true) returns full path (if model has been saved).
            var modelPath = _sapModel.GetModelFilename(true);
            if (string.IsNullOrWhiteSpace(modelPath))
                return null;

            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return null;

            return Path.Combine(dir, JsonFileName);
        }

        public bool TryLoad(out SectionCutJsonData data)
        {
            data = null;
            var path = GetJsonPathForCurrentModel();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                var json = File.ReadAllText(path);
                data = JsonSerializer.Deserialize<SectionCutJsonData>(json, JsonOptions());
                return data != null;
            }
            catch
            {
                // If json is malformed, don't crash the plugin.
                data = null;
                return false;
            }
        }

        public void Save(SectionCut definition, IEnumerable<string> openingIds)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            var path = GetJsonPathForCurrentModel();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "ETABS model does not have a valid file path. Save the ETABS model first so SectionCut.json can be written next to it.");

            var data = new SectionCutJsonData
            {
                StartNodeId = definition.StartNodeId,
                AreaIds = (definition.AreaIds ?? new List<string>()).ToList(),
                OpeningIds = (openingIds ?? Array.Empty<string>()).ToList(),
                XVector = definition.XVector,
                YVector = definition.YVector,
                SectionCutPrefix = definition.SectionCutPrefix,
                SavedUtc = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(data, JsonOptions());
            File.WriteAllText(path, json);
        }

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

            return names.OrderBy(x => x).ToList();
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
