using System.Data;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using Microsoft.Data.SqlClient;

// Disambiguate: Autodesk.AutoCAD.DatabaseServices also defines DataTable
using DataTable = System.Data.DataTable;
using DataSet   = System.Data.DataSet;

namespace IWCCadToolsV9.Commands
{
    /// <summary>
    /// AutoCAD commands for inserting and updating the Hardware and Material tables.
    ///
    /// Consolidates V8's HardwareTableCommands and MaterialTableCommands.
    /// Duplicate PromptInput, InsertTableIntoDrawingAndStoreRef, and UpdateAcadMatTable
    /// methods have been removed; all table operations delegate to <see cref="AcadTableHelper"/>.
    /// Connection strings are resolved through <see cref="IWCConn"/> (never hardcoded here).
    /// </summary>
    public static class TableCommands
    {
        /// <summary>
        /// Refreshes existing IWC material and hardware AutoCAD tables in the active drawing.
        /// This is used by the drawing lifecycle startup path so users do not need to run
        /// IWCUpdateMaterialTable or IWCUpdateHardwareTable manually after opening a linked DWG.
        /// Only tables that have previously been inserted and stored through TableReferenceHelper
        /// are updated; missing table references are ignored.
        /// </summary>
        public static void AutoUpdateExistingTablesInActiveDocument(bool quiet = true)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                using (doc.LockDocument())
                {
                    bool anyUpdated = false;

                    var materialTableId = TableReferenceHelper.RetrieveTableReference(db, TableReferenceHelper.MaterialTableKey);
                    if (!materialTableId.IsNull)
                    {
                        string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
                        if (!string.IsNullOrWhiteSpace(dashId))
                        {
                            var data = QueryMaterialTable(dashId);
                            if (data != null)
                            {
                                anyUpdated |= TryUpdateAcadTable(db, materialTableId, data, AcadTableHelper.MaterialCols, "material", ed, quiet);
                            }
                        }
                    }

                    var hardwareTableId = TableReferenceHelper.RetrieveTableReference(db, TableReferenceHelper.HardwareTableKey);
                    if (!hardwareTableId.IsNull)
                    {
                        string? dash = ResolveDashFromPropertiesNoPrompt();
                        if (!string.IsNullOrWhiteSpace(dash))
                        {
                            var data = QueryHardwareTable(dash);
                            if (data != null)
                            {
                                anyUpdated |= TryUpdateAcadTable(db, hardwareTableId, data, AcadTableHelper.HardwareCols, "hardware", ed, quiet);
                            }
                        }
                    }

                    if (anyUpdated && !quiet)
                        ed.WriteMessage("\nIWC drawing tables updated.\n");
                }
            }
            catch (System.Exception ex)
            {
                if (!quiet)
                    ed.WriteMessage($"\nIWC automatic table update failed — {ex.Message}\n");
                else
                    ed.WriteMessage($"\nIWC automatic table update failed — {ex.Message}\n");
            }
        }

        private static bool TryUpdateAcadTable(Database db, ObjectId tableId, DataTable data, AcadTableHelper.ColumnSpec[] columns, string tableName, Editor ed, bool quiet)
        {
            if (data.Rows.Count == 0)
            {
                if (!quiet) ed.WriteMessage($"\nNo {tableName} data found for this dash.\n");
                return false;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var acadTable = tr.GetObject(tableId, OpenMode.ForWrite, false) as Table;
            if (acadTable == null)
            {
                if (!quiet) ed.WriteMessage($"\nStored {tableName} table reference was not found.\n");
                return false;
            }

            AcadTableHelper.UpdateTable(acadTable, data, columns);
            tr.Commit();

            if (!quiet) ed.WriteMessage($"\n{System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tableName)} table updated.\n");
            return true;
        }

        private static string? ResolveDashFromPropertiesNoPrompt()
        {
            string? projNum = AcadFilePropHelper.GetCustomProperty("IWC_ProjNo");
            string? seriesNo = AcadFilePropHelper.GetCustomProperty("IWC_SeriesNo");

            if (string.IsNullOrWhiteSpace(projNum) || string.IsNullOrWhiteSpace(seriesNo))
                return null;

            projNum = projNum.Trim();
            seriesNo = seriesNo.Trim();
            if (seriesNo.Length < 4)
                seriesNo = seriesNo.PadLeft(4, '0');

            var dash = $"{projNum}-{seriesNo}";
            return dash.Length == 9 ? dash : null;
        }

        // =========================================================================
        // HARDWARE TABLE
        // =========================================================================

        [CommandMethod("IWCInsertHardwareTable")]
        public static void InsertHardwareTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            string? dash = ResolveDash(ed);
            if (dash == null) return;

            var ppr = ed.GetPoint(new PromptPointOptions("\nSelect insertion point for hardware table:"));
            if (ppr.Status != PromptStatus.OK) return;

            var data = QueryHardwareTable(dash);
            if (data == null || data.Rows.Count == 0)
            { ed.WriteMessage($"\nNo hardware found for Dash {dash}."); return; }

            var table = AcadTableHelper.BuildTable(data, AcadTableHelper.HardwareCols);
            InsertAndStoreRef(doc, table, ppr.Value, TableReferenceHelper.HardwareTableKey);
            ed.WriteMessage("\nHardware table inserted.");
        }

        [CommandMethod("IWCUpdateHardwareTable")]
        public static void UpdateHardwareTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;
            var db  = doc.Database;

            var tableId = TableReferenceHelper.RetrieveTableReference(db, TableReferenceHelper.HardwareTableKey);
            if (tableId.IsNull) { ed.WriteMessage("\nNo hardware table reference found."); return; }

            string? dash = ResolveDash(ed);
            if (dash == null) return;

            var data = QueryHardwareTable(dash);
            if (data == null || data.Rows.Count == 0)
            { ed.WriteMessage($"\nNo hardware found for Dash {dash}."); return; }

            using var tr = db.TransactionManager.StartTransaction();
            var acadTable = tr.GetObject(tableId, OpenMode.ForWrite) as Table;
            if (acadTable == null) { ed.WriteMessage("\nHardware table not found."); return; }

            AcadTableHelper.UpdateTable(acadTable, data, AcadTableHelper.HardwareCols);
            tr.Commit();
            ed.WriteMessage("\nHardware table updated.");
        }

        // =========================================================================
        // MATERIAL TABLE
        // =========================================================================

        [CommandMethod("IWCInsertMaterialTable")]
        public static void InsertMaterialTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            if (string.IsNullOrEmpty(dashId))
                dashId = Prompt(ed, "Enter Section ID for DashID:");
            if (string.IsNullOrEmpty(dashId)) return;

            var ppr = ed.GetPoint(new PromptPointOptions("\nSelect insertion point for material table:"));
            if (ppr.Status != PromptStatus.OK) return;

            var data = QueryMaterialTable(dashId);
            if (data == null || data.Rows.Count == 0)
            { ed.WriteMessage($"\nNo materials found for DashID {dashId}."); return; }

            var table = AcadTableHelper.BuildTable(data, AcadTableHelper.MaterialCols);
            InsertAndStoreRef(doc, table, ppr.Value, TableReferenceHelper.MaterialTableKey);
            ed.WriteMessage("\nMaterial table inserted.");
        }

        [CommandMethod("IWCUpdateMaterialTable")]
        public static void UpdateMaterialTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;
            var db  = doc.Database;

            var tableId = TableReferenceHelper.RetrieveTableReference(db, TableReferenceHelper.MaterialTableKey);
            if (tableId.IsNull) { ed.WriteMessage("\nNo material table reference found."); return; }

            string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            if (string.IsNullOrEmpty(dashId))
                dashId = Prompt(ed, "Enter Section ID for DashID:");
            if (string.IsNullOrEmpty(dashId)) return;

            var data = QueryMaterialTable(dashId);
            if (data == null || data.Rows.Count == 0)
            { ed.WriteMessage($"\nNo materials found for DashID {dashId}."); return; }

            using var tr = db.TransactionManager.StartTransaction();
            var acadTable = tr.GetObject(tableId, OpenMode.ForWrite) as Table;
            if (acadTable == null) { ed.WriteMessage("\nMaterial table not found."); return; }

            AcadTableHelper.UpdateTable(acadTable, data, AcadTableHelper.MaterialCols);
            tr.Commit();
            ed.WriteMessage("\nMaterial table updated.");
        }

        // =========================================================================
        // Database queries
        // =========================================================================

        private static DataTable? QueryHardwareTable(string dash)
        {
            using var conn = new IWCConn();
            conn.DBConnect();
            using var cmd = new SqlCommand("dbo.DashSeriesHardwareList", conn.OpenConn)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@DashIDNum", dash);
            using var da = new SqlDataAdapter(cmd);
            var ds = new DataSet();
            da.Fill(ds);
            return ds.Tables.Count > 0 ? ds.Tables[0] : null;
        }

        private static DataTable? QueryMaterialTable(string dashId)
        {
            using var conn = new IWCConn();
            conn.DBConnect();
            using var cmd = new SqlCommand(
                "SELECT MatNo, MatDesc, MatApprove, MatGroup FROM dbo.Dash_Mat_Compile WHERE DashID = @id",
                conn.OpenConn);
            cmd.Parameters.AddWithValue("@id", dashId);
            using var da = new SqlDataAdapter(cmd);
            var ds = new DataSet();
            da.Fill(ds);
            return ds.Tables.Count > 0 ? ds.Tables[0] : null;
        }

        // =========================================================================
        // Shared helpers
        // =========================================================================

        /// <summary>
        /// Resolves the "XXXX-YYYY" dash identifier from file properties or user prompts.
        /// Returns null if the user cancels or the format is invalid.
        /// </summary>
        private static string? ResolveDash(Editor ed)
        {
            string? projNum  = AcadFilePropHelper.GetCustomProperty("IWC_ProjNo");
            string? seriesNo = AcadFilePropHelper.GetCustomProperty("IWC_SeriesNo");

            if (string.IsNullOrEmpty(projNum) || string.IsNullOrEmpty(seriesNo))
            {
                ed.WriteMessage("\nProject or Series Number not set in file properties.");
                projNum  = Prompt(ed, "Enter Project Number (XXXX):");
                if (projNum == null) return null;
                seriesNo = Prompt(ed, "Enter Series Number (YYYY):");
                if (seriesNo == null) return null;
            }

            if (seriesNo.Length < 4)
                seriesNo = seriesNo.PadLeft(4, '0');

            string dash = $"{projNum}-{seriesNo}";
            if (dash.Length != 9)
            {
                dash = Prompt(ed, "Enter Project-Series Number (format XXXX-YYYY):") ?? string.Empty;
                if (dash.Length != 9) return null;
            }
            return dash;
        }

        private static void InsertAndStoreRef(
            Autodesk.AutoCAD.ApplicationServices.Document doc,
            Table table, Point3d insPt, string refKey)
        {
            var db = doc.Database;
            using var tr = db.TransactionManager.StartTransaction();
            table.Position = insPt;
            var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            btr.AppendEntity(table);
            tr.AddNewlyCreatedDBObject(table, true);
            TableReferenceHelper.StoreTableReference(db, table.ObjectId, refKey);
            tr.Commit();
        }

        private static string? Prompt(Editor ed, string message)
        {
            var res = ed.GetString(new PromptStringOptions($"\n{message}") { AllowSpaces = true });
            return res.Status == PromptStatus.OK ? res.StringResult : null;
        }
    }
}
