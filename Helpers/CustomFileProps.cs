using System.Collections.Generic;
using System.IO;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Provides the authoritative list of DWG custom file properties managed by IWC CAD Tools.
    ///
    /// Properties can be loaded from an external CSV (key,column) or from the
    /// hard-coded defaults.  The CSV takes precedence when it exists and is valid.
    ///
    /// CSV format (no header row):
    ///   PropertyName,DatasetColumnName
    ///   IWC_ProjNo,              ← empty column → uses built-in logic
    ///   IWC_Architect,Architect  ← maps to project dataset column
    /// </summary>
    public static class CustomFileProps
    {
        // ---------------------------------------------------------------------------
        // In-code defaults
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Returns the default property map.
        /// KEY  = DWG custom property name.
        /// VALUE = dataset column name to source the value from
        ///         (empty string = use built-in logic in <see cref="Core.IWCFile"/>).
        /// </summary>
        public static Dictionary<string, string> GetDefaults() =>
            new()
            {
                // Core project fields (populated via built-in logic)
                ["IWC_ProjNo"]   = "",
                ["IWC_ProjName"] = "",
                ["IWC_ID"]       = "",

                // Series / section fields
                ["IWC_SeriesNo"]   = "SeriesNo",
                ["IWC_SeriesID"]   = "SeriesID",
                ["IWC_SeriesName"] = "SeriesName",

                // Project contact fields sourced from dataset columns
                ["IWC_Architect"]  = "Architect",
                ["IWC_Contractor"] = "Contractor",
                ["IWC_PMINI"]      = "PMINI",
            };

        // ---------------------------------------------------------------------------
        // CSV loader
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Loads the property map from a CSV file.
        /// Lines that are blank or start with <c>#</c> are ignored.
        /// Falls back to <see cref="GetDefaults"/> if the file does not exist.
        /// </summary>
        public static Dictionary<string, string> LoadFromCsv(string csvPath)
        {
            if (!File.Exists(csvPath))
                return GetDefaults();

            var dict = new Dictionary<string, string>();
            foreach (var raw in File.ReadAllLines(csvPath))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;

                var parts = line.Split(',', 2);
                var key   = parts[0].Trim();
                var val   = parts.Length > 1 ? parts[1].Trim() : string.Empty;

                if (!string.IsNullOrEmpty(key))
                    dict[key] = val;
            }

            return dict.Count > 0 ? dict : GetDefaults();
        }
    }
}
