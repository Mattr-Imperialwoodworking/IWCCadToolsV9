using System.Collections;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Single, authoritative helper for reading and writing DWG custom file properties.
    ///
    /// Replaces:
    ///   - GetCustomProperty.cs  (AcadFilePropHelper)
    ///   - FilePropSafe.cs       (CloneProps / Clean)
    ///   - The duplicated SetAcadCustomProperties method in IWCDash
    /// </summary>
    public static class AcadFilePropHelper
    {
        // ---------------------------------------------------------------------------
        // READ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Returns the value of a DWG custom file property, or <see langword="null"/>
        /// if the property does not exist or no document is active.
        /// </summary>
        public static string? GetCustomProperty(string propertyName)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return null;

            var en = doc.Database.SummaryInfo.CustomProperties as IDictionaryEnumerator;
            if (en == null) return null;

            while (en.MoveNext())
            {
                var entry = (DictionaryEntry)en.Entry;
                if (entry.Key?.ToString() == propertyName)
                    return entry.Value?.ToString();
            }
            return null;
        }

        // ---------------------------------------------------------------------------
        // WRITE (single property)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Adds or updates a single DWG custom file property in the active document.
        /// Locks the document before writing to avoid eLockViolation.
        /// </summary>
        public static void SetCustomProperty(string propertyName, string? value)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            {
                var db      = doc.Database;
                var builder = new DatabaseSummaryInfoBuilder(db.SummaryInfo);
                var dic     = builder.CustomPropertyTable;
                var safe    = Sanitize(value);

                if (dic.Contains(propertyName))
                    dic[propertyName] = safe;
                else
                    dic.Add(propertyName, safe);

                db.SummaryInfo = builder.ToDatabaseSummaryInfo();
            }
        }

        // ---------------------------------------------------------------------------
        // WRITE (bulk upsert)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Ensures all keys in <paramref name="props"/> exist as custom file
        /// properties, adding any that are missing with the value <c>"NA"</c>.
        /// Does not overwrite existing values.
        /// </summary>
        public static void EnsurePropertiesExist(IEnumerable<string> props)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            {
                var db      = doc.Database;
                var builder = new DatabaseSummaryInfoBuilder(db.SummaryInfo);
                var dic     = builder.CustomPropertyTable;
                bool changed = false;

                foreach (var key in props)
                {
                    if (!dic.Contains(key))
                    {
                        dic.Add(key, "NA");
                        changed = true;
                    }
                }

                if (changed)
                    db.SummaryInfo = builder.ToDatabaseSummaryInfo();
            }
        }

        /// <summary>
        /// Performs a bulk upsert of all entries in <paramref name="values"/>.
        /// Existing properties are overwritten; missing ones are added.
        /// </summary>
        public static void SetCustomProperties(IDictionary<string, string> values)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            {
                var db      = doc.Database;
                var builder = new DatabaseSummaryInfoBuilder(db.SummaryInfo);
                var dic     = builder.CustomPropertyTable;

                foreach (var kvp in values)
                {
                    var safe = Sanitize(kvp.Value);
                    if (dic.Contains(kvp.Key))
                        dic[kvp.Key] = safe;
                    else
                        dic.Add(kvp.Key, safe);
                }

                db.SummaryInfo = builder.ToDatabaseSummaryInfo();
            }
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Strips CR/LF characters and trims whitespace.
        /// Returns <c>"NA"</c> for null or empty values.
        /// </summary>
        public static string Sanitize(string? value)
        {
            var s = value?.Replace("\r", " ").Replace("\n", " ").Trim() ?? string.Empty;
            return string.IsNullOrEmpty(s) ? "NA" : s;
        }

        /// <summary>
        /// Clones all current custom properties into a <see cref="Hashtable"/>.
        /// Useful when building a staged-update pattern.
        /// </summary>
        public static Hashtable CloneCurrentProps(DatabaseSummaryInfo summary)
        {
            var ht = new Hashtable();
            var en = summary.CustomProperties as IDictionaryEnumerator;
            if (en == null) return ht;

            while (en.MoveNext())
            {
                var de = (DictionaryEntry)en.Entry;
                ht[de.Key] = de.Value;
            }
            return ht;
        }
    }
}
