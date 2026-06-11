using System;
using System.Collections;
using Autodesk.AutoCAD.DatabaseServices;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Stores and retrieves AutoCAD Table <see cref="ObjectId"/> handles
    /// in the drawing's custom file properties, keyed by a caller-supplied string.
    /// This allows commands like Insert/Update Hardware Table to locate the
    /// previously inserted table without requiring the user to re-select it.
    /// </summary>
    public static class TableReferenceHelper
    {
        public const string HardwareTableKey = "IWC_HardwareTableId";
        public const string MaterialTableKey  = "IWC_MaterialTableId";
        public const string MetalTableKey     = "IWC_MetalTableId";

        public const string HardwareTableFormatKey = "IWC_HardwareTableFormat";
        public const string MaterialTableFormatKey = "IWC_MaterialTableFormat";
        public const string MetalTableFormatKey     = "IWC_MetalTableFormat";

        public const string TableFormatWide       = "Wide";
        public const string TableFormatTitleblock = "Titleblock";

        // ---------------------------------------------------------------------------
        // Store
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Writes the hex handle of <paramref name="tableId"/> into the DWG
        /// custom property identified by <paramref name="propertyKey"/>.
        /// </summary>
        public static void StoreTableReference(Database db, ObjectId tableId,
            string propertyKey = HardwareTableKey)
        {
            AcadFilePropHelper.SetCustomProperty(propertyKey, tableId.Handle.ToString());
        }

        /// <summary>
        /// Stores the selected table layout format for later update/auto-refresh.
        /// </summary>
        public static void StoreTableFormat(string formatKey, string format)
        {
            AcadFilePropHelper.SetCustomProperty(formatKey, format);
        }

        /// <summary>
        /// Retrieves the selected table layout format. Older drawings default to Wide.
        /// </summary>
        public static string RetrieveTableFormat(string formatKey)
        {
            var value = AcadFilePropHelper.GetCustomProperty(formatKey);
            return string.Equals(value, TableFormatTitleblock, StringComparison.OrdinalIgnoreCase)
                ? TableFormatTitleblock
                : TableFormatWide;
        }

        // ---------------------------------------------------------------------------
        // Retrieve
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Reads the stored hex handle and resolves it back to an <see cref="ObjectId"/>.
        /// Returns <see cref="ObjectId.Null"/> if the property is missing or invalid.
        /// </summary>
        public static ObjectId RetrieveTableReference(Database db,
            string propertyKey = HardwareTableKey)
        {
            var handleStr = AcadFilePropHelper.GetCustomProperty(propertyKey);
            if (string.IsNullOrWhiteSpace(handleStr))
                return ObjectId.Null;

            try
            {
                var handle = new Handle(Convert.ToInt64(handleStr, 16));
                return db.GetObjectId(false, handle, 0);
            }
            catch
            {
                return ObjectId.Null;
            }
        }
    }
}
