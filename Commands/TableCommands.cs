using System.Data;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Core;
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
        /// Refreshes every tagged IWC material, hardware, and metal AutoCAD table in the active drawing.
        ///
        /// Tables are now found by entity-level BOM tags instead of a single drawing custom property handle.
        /// This allows copied tables, multiple inserted tables, and tables placed inside block definitions
        /// to all update from the same current drawing project/dash data.
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

                    string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
                    string? dashDisplay = ResolveDashFromPropertiesNoPrompt();

                    anyUpdated |= UpdateAllMaterialTables(db, ed, quiet, dashId, dashDisplay);
                    anyUpdated |= UpdateAllHardwareTables(db, ed, quiet, dashDisplay);
                    anyUpdated |= UpdateAllMetalTables(db, ed, quiet, dashId, dashDisplay);

                    if (anyUpdated && !quiet)
                        ed.WriteMessage("\nIWC drawing tables updated.\n");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nIWC automatic table update failed — {ex.Message}\n");
            }
        }

        private static bool UpdateAllMaterialTables(Database db, Editor ed, bool quiet, string? dashId, string? dashDisplay)
        {
            var tables = BomTableTagHelper.FindBomTables(db, BomTableTagHelper.BomTableKind.Material);
            if (tables.Count == 0)
            {
                if (!quiet) ed.WriteMessage("\nNo tagged IWC material tables found.\n");
                return false;
            }

            if (string.IsNullOrWhiteSpace(dashId))
            {
                if (!quiet) ed.WriteMessage("\nNo current DashID found in drawing properties for material table refresh.\n");
                return false;
            }

            var data = QueryMaterialTable(dashId);
            if (data == null || data.Rows.Count == 0)
            {
                if (!quiet) ed.WriteMessage($"\nNo materials found for DashID {dashId}.\n");
                return false;
            }

            bool anyUpdated = false;
            foreach (var tableInfo in tables)
            {
                anyUpdated |= TryUpdateBomTable(
                    db,
                    tableInfo.ObjectId,
                    data,
                    AcadTableHelper.MaterialCols,
                    BuildTableTitle("MATERIALS TABLE", dashDisplay ?? dashId),
                    "material",
                    ed,
                    quiet,
                    tableInfo.Kind,
                    tableInfo.Format);
            }

            if (anyUpdated && !quiet)
                ed.WriteMessage($"\nUpdated {tables.Count} material table(s).\n");

            return anyUpdated;
        }

        private static bool UpdateAllHardwareTables(Database db, Editor ed, bool quiet, string? dashDisplay)
        {
            var tables = BomTableTagHelper.FindBomTables(db, BomTableTagHelper.BomTableKind.Hardware);
            if (tables.Count == 0)
            {
                if (!quiet) ed.WriteMessage("\nNo tagged IWC hardware tables found.\n");
                return false;
            }

            if (string.IsNullOrWhiteSpace(dashDisplay))
            {
                if (!quiet) ed.WriteMessage("\nNo current project-dash display value found for hardware table refresh.\n");
                return false;
            }

            var data = QueryHardwareTable(dashDisplay);
            if (data == null || data.Rows.Count == 0)
            {
                if (!quiet) ed.WriteMessage($"\nNo hardware found for Dash {dashDisplay}.\n");
                return false;
            }

            bool anyUpdated = false;
            foreach (var tableInfo in tables)
            {
                anyUpdated |= TryUpdateBomTable(
                    db,
                    tableInfo.ObjectId,
                    data,
                    AcadTableHelper.HardwareCols,
                    BuildTableTitle("HARDWARE TABLE", dashDisplay),
                    "hardware",
                    ed,
                    quiet,
                    tableInfo.Kind,
                    tableInfo.Format);
            }

            if (anyUpdated && !quiet)
                ed.WriteMessage($"\nUpdated {tables.Count} hardware table(s).\n");

            return anyUpdated;
        }

        private static bool UpdateAllMetalTables(Database db, Editor ed, bool quiet, string? dashId, string? dashDisplay)
        {
            var tables = BomTableTagHelper.FindBomTables(db, BomTableTagHelper.BomTableKind.Metal);
            if (tables.Count == 0)
            {
                if (!quiet) ed.WriteMessage("\nNo tagged IWC metal tables found.\n");
                return false;
            }

            if (string.IsNullOrWhiteSpace(dashId))
            {
                if (!quiet) ed.WriteMessage("\nNo current DashID found in drawing properties for metal table refresh.\n");
                return false;
            }

            var data = QueryMetalTable(dashId);
            if (data == null || data.Rows.Count == 0)
            {
                if (!quiet) ed.WriteMessage($"\nNo metal parts found for DashID {dashId}.\n");
                return false;
            }

            bool anyUpdated = false;
            foreach (var tableInfo in tables)
            {
                anyUpdated |= TryUpdateAcadTable(
                    db,
                    tableInfo.ObjectId,
                    data,
                    AcadTableHelper.MetalCols,
                    BuildTableTitle("METAL PARTS TABLE", dashDisplay ?? dashId),
                    "metal",
                    ed,
                    quiet);
            }

            if (anyUpdated && !quiet)
                ed.WriteMessage($"\nUpdated {tables.Count} metal table(s).\n");

            return anyUpdated;
        }

        private static bool TryUpdateBomTable(
            Database db,
            ObjectId tableId,
            DataTable data,
            AcadTableHelper.ColumnSpec[] columns,
            string titleText,
            string tableName,
            Editor ed,
            bool quiet,
            BomTableTagHelper.BomTableKind kind,
            string format)
        {
            if (!BomTableTagHelper.IsTitleblock(format))
                return TryUpdateAcadTable(db, tableId, data, columns, titleText, tableName, ed, quiet);

            if (data.Rows.Count == 0)
            {
                if (!quiet) ed.WriteMessage($"\nNo {tableName} data found for this dash.\n");
                return false;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var acadTable = tr.GetObject(tableId, OpenMode.ForWrite, false) as Table;
            if (acadTable == null)
            {
                if (!quiet) ed.WriteMessage($"\nTagged {tableName} table was not found.\n");
                return false;
            }

            AcadTableHelper.ApplyPreferredTableStyle(acadTable, db, tr);
            if (kind == BomTableTagHelper.BomTableKind.Hardware)
                AcadTableHelper.UpdateTitleblockHardwareTable(acadTable, data, "IWC HARDWARE:");
            else
                AcadTableHelper.UpdateTitleblockMaterialTable(acadTable, data, "IWC MATERIAL:");

            tr.Commit();

            if (!quiet) ed.WriteMessage($"\n{System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tableName)} table updated.\n");
            return true;
        }

        private static bool TryUpdateAcadTable(Database db, ObjectId tableId, DataTable data, AcadTableHelper.ColumnSpec[] columns, string titleText, string tableName, Editor ed, bool quiet)
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
                if (!quiet) ed.WriteMessage($"\nTagged {tableName} table was not found.\n");
                return false;
            }

            AcadTableHelper.ApplyPreferredTableStyle(acadTable, db, tr);
            AcadTableHelper.UpdateTable(acadTable, data, columns, titleText);
            tr.Commit();

            if (!quiet) ed.WriteMessage($"\n{System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tableName)} table updated.\n");
            return true;
        }

        private static string BuildTableTitle(string tableName, string? dashDisplay)
        {
            return string.IsNullOrWhiteSpace(dashDisplay)
                ? tableName
                : $"{tableName}: {dashDisplay.Trim()}";
        }

        private static string? ResolveDashFromPropertiesNoPrompt()
        {
            // Prefer the already-loaded per-document project context when the command
            // is launched from the palette. This keeps the Insert Hdw Table workflow
            // consistent with Insert Material Table and avoids prompting when the
            // current drawing/palette already knows the active project and dash.
            string? dashFromContext = ResolveDashFromCurrentProjectContext();
            if (!string.IsNullOrWhiteSpace(dashFromContext))
                return dashFromContext;

            string? projNum = AcadFilePropHelper.GetCustomProperty(DwgPropertyStore.KeyProjNum);
            string? seriesNo = AcadFilePropHelper.GetCustomProperty(DwgPropertyStore.KeyDashNum);

            string? dash = BuildDashDisplay(projNum, seriesNo);
            if (!string.IsNullOrWhiteSpace(dash))
                return dash;

            // Some drawings have the stable project/dash IDs saved but not the
            // display numbers. Resolve the dash display from the database so the
            // hardware stored procedure can run without prompting.
            string? projectIdText = AcadFilePropHelper.GetCustomProperty(DwgPropertyStore.KeyProjectId);
            string? dashIdText = AcadFilePropHelper.GetCustomProperty(DwgPropertyStore.KeyDashId);
            if (!int.TryParse(dashIdText, out int dashId) || dashId <= 0)
                return null;

            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();
                using var cmd = new SqlCommand(@"
                    SELECT TOP 1 IDNum, Dash_Num
                    FROM (
                        SELECT 0 AS SortOrder, IDNum, Dash_Num
                        FROM dbo.Proj_DashCompileReportActive
                        WHERE DashID = @dashId
                        UNION ALL
                        SELECT 1 AS SortOrder, IDNum, Dash_Num
                        FROM dbo.Proj_DashCompileReport
                        WHERE DashID = @dashId
                    ) d
                    ORDER BY SortOrder;", conn.OpenConn);
                cmd.Parameters.AddWithValue("@dashId", dashId);

                using var rdr = cmd.ExecuteReader();
                if (rdr.Read())
                    return BuildDashDisplay(rdr["IDNum"]?.ToString(), rdr["Dash_Num"]?.ToString());
            }
            catch
            {
                // Quiet fallback; callers can still prompt if needed.
            }

            // Last-chance fallback: if IWC_ProjNo was missing but IWC_ID is present,
            // resolve the project number separately and combine it with IWC_SeriesNo.
            if (!string.IsNullOrWhiteSpace(seriesNo) && int.TryParse(projectIdText, out int projectId) && projectId > 0)
            {
                try
                {
                    using var conn = new IWCConn();
                    conn.DBConnect();
                    using var cmd = new SqlCommand("SELECT TOP 1 IDNum FROM dbo.Proj WHERE ID = @projectId;", conn.OpenConn);
                    cmd.Parameters.AddWithValue("@projectId", projectId);
                    var resolvedProjNum = cmd.ExecuteScalar()?.ToString();
                    return BuildDashDisplay(resolvedProjNum, seriesNo);
                }
                catch
                {
                    // Quiet fallback; callers can still prompt if needed.
                }
            }

            return null;
        }

        private static string? ResolveDashFromCurrentProjectContext()
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return null;

                var svc = ProjectContextService.GetOrCreate(doc);
                if (!svc.HasDash) return null;

                string? projectNumber = svc.Project?.IdNum;
                if (string.IsNullOrWhiteSpace(projectNumber))
                    projectNumber = svc.Dash?.ProjectIdNum;

                string? dashNumber = svc.Dash?.DashNum;
                string? dash = BuildDashDisplay(projectNumber, dashNumber);
                if (!string.IsNullOrWhiteSpace(dash))
                    return dash;

                // If the dash number already includes the project prefix, use it as-is.
                if (!string.IsNullOrWhiteSpace(dashNumber) && dashNumber.Contains('-'))
                    return dashNumber.Trim();
            }
            catch
            {
                // Context may not be available when the command is run outside the palette.
            }

            return null;
        }

        private static string? BuildDashDisplay(string? projNum, string? seriesNo)
        {
            if (string.IsNullOrWhiteSpace(projNum) || string.IsNullOrWhiteSpace(seriesNo))
                return null;

            projNum = projNum.Trim();
            seriesNo = seriesNo.Trim();

            // Legacy 3-digit dash numbers should still display as 4 digits,
            // but valid IWC dash numbers are not limited to four characters.
            // Examples: 5884-0510 and 5834-50202 are both valid display values
            // for the hardware stored procedure.
            if (seriesNo.Length < 4 && seriesNo.All(char.IsDigit))
                seriesNo = seriesNo.PadLeft(4, '0');

            var dash = $"{projNum}-{seriesNo}";
            return IsValidDashDisplay(dash) ? dash : null;
        }

        private static bool IsValidDashDisplay(string? dash)
        {
            if (string.IsNullOrWhiteSpace(dash)) return false;

            dash = dash.Trim();
            int hyphenIndex = dash.IndexOf('-');
            if (hyphenIndex <= 0 || hyphenIndex >= dash.Length - 1)
                return false;

            string projectPart = dash[..hyphenIndex];
            string dashPart = dash[(hyphenIndex + 1)..];

            // Keep validation intentionally light.  Hardware lookup uses the
            // project-dash display string, and dash numbers can be more than
            // four characters.  Reject only clearly malformed values.
            return projectPart.Length >= 1 && dashPart.Length >= 1;
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

            // Match the material-table workflow: use the current drawing's saved
            // project/dash properties first and only prompt if this DWG is not linked.
            string? dash = ResolveDashFromPropertiesNoPrompt() ?? ResolveDash(ed);
            if (dash == null) return;

            string? format = PromptBomTableFormat(ed);
            if (format == null) return;

            var ppr = ed.GetPoint(new PromptPointOptions("\nSelect insertion point for hardware table:"));
            if (ppr.Status != PromptStatus.OK) return;

            var data = QueryHardwareTable(dash);
            if (data == null || data.Rows.Count == 0)
            { ed.WriteMessage($"\nNo hardware found for Dash {dash}."); return; }

            var table = string.Equals(format, BomTableTagHelper.FormatTitleblock, System.StringComparison.OrdinalIgnoreCase)
                ? AcadTableHelper.BuildTitleblockHardwareTable(data, "IWC HARDWARE:")
                : AcadTableHelper.BuildTable(data, AcadTableHelper.HardwareCols, BuildTableTitle("HARDWARE TABLE", dash));

            InsertAndTagTable(doc, table, ppr.Value, BomTableTagHelper.BomTableKind.Hardware, format);
            ed.WriteMessage("\nHardware table inserted.");
        }

        [CommandMethod("IWCUpdateHardwareTable")]
        public static void UpdateHardwareTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            string? dash = ResolveDashFromPropertiesNoPrompt() ?? ResolveDash(ed);
            if (dash == null) return;

            using (doc.LockDocument())
            {
                bool updated = UpdateAllHardwareTables(db, ed, quiet: false, dashDisplay: dash);
                if (!updated)
                    ed.WriteMessage("\nNo hardware tables were updated.\n");
            }
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

            string? format = PromptBomTableFormat(ed);
            if (format == null) return;

            var ppr = ed.GetPoint(new PromptPointOptions("\nSelect insertion point for material table:"));
            if (ppr.Status != PromptStatus.OK) return;

            var data = QueryMaterialTable(dashId);
            if (data == null || data.Rows.Count == 0)
            { ed.WriteMessage($"\nNo materials found for DashID {dashId}."); return; }

            var table = string.Equals(format, BomTableTagHelper.FormatTitleblock, System.StringComparison.OrdinalIgnoreCase)
                ? AcadTableHelper.BuildTitleblockMaterialTable(data, "IWC MATERIAL:")
                : AcadTableHelper.BuildTable(data, AcadTableHelper.MaterialCols, BuildTableTitle("MATERIALS TABLE", ResolveDashFromPropertiesNoPrompt() ?? dashId));

            InsertAndTagTable(doc, table, ppr.Value, BomTableTagHelper.BomTableKind.Material, format);
            ed.WriteMessage("\nMaterial table inserted.");
        }

        [CommandMethod("IWCUpdateMaterialTable")]
        public static void UpdateMaterialTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            if (string.IsNullOrEmpty(dashId))
                dashId = Prompt(ed, "Enter Section ID for DashID:");
            if (string.IsNullOrEmpty(dashId)) return;

            string? dashDisplay = ResolveDashFromPropertiesNoPrompt();

            using (doc.LockDocument())
            {
                bool updated = UpdateAllMaterialTables(db, ed, quiet: false, dashId: dashId, dashDisplay: dashDisplay);
                if (!updated)
                    ed.WriteMessage("\nNo material tables were updated.\n");
            }
        }

        // =========================================================================
        // METAL TABLE
        // =========================================================================

        [CommandMethod("IWCInsertMetalTable")]
        public static void InsertMetalTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            if (string.IsNullOrEmpty(dashId))
                dashId = Prompt(ed, "Enter Section ID for DashID:");
            if (string.IsNullOrEmpty(dashId)) return;

            var ppr = ed.GetPoint(new PromptPointOptions("\nSelect insertion point for metal table:"));
            if (ppr.Status != PromptStatus.OK) return;

            var data = QueryMetalTable(dashId);
            if (data == null || data.Rows.Count == 0)
            { ed.WriteMessage($"\nNo metal parts found for DashID {dashId}."); return; }

            var table = AcadTableHelper.BuildTable(data, AcadTableHelper.MetalCols, BuildTableTitle("METAL PARTS TABLE", ResolveDashFromPropertiesNoPrompt() ?? dashId));
            InsertAndTagTable(doc, table, ppr.Value, BomTableTagHelper.BomTableKind.Metal, BomTableTagHelper.FormatWide);
            ed.WriteMessage("\nMetal table inserted.");
        }

        [CommandMethod("IWCUpdateMetalTable")]
        public static void UpdateMetalTable()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application
                          .DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            if (string.IsNullOrEmpty(dashId))
                dashId = Prompt(ed, "Enter Section ID for DashID:");
            if (string.IsNullOrEmpty(dashId)) return;

            string? dashDisplay = ResolveDashFromPropertiesNoPrompt();

            using (doc.LockDocument())
            {
                bool updated = UpdateAllMetalTables(db, ed, quiet: false, dashId: dashId, dashDisplay: dashDisplay);
                if (!updated)
                    ed.WriteMessage("\nNo metal tables were updated.\n");
            }
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

        private static DataTable? QueryMetalTable(string dashId)
        {
            using var conn = new IWCConn();
            conn.DBConnect();
            using var cmd = new SqlCommand(@"
                SELECT Mtl_PrtNo, Mtl_PrtDesc, Mtl_Finish, Mtl_Material,
                       Mtl_Length, Mtl_Width, Mtl_Height, Mtl_Thk,
                       Mtl_Qty, Mtl_QtyUnits, Mtl_ShtReference, Mtl_Notes
                FROM dbo.Proj_Mtl
                WHERE DashID = @id
                ORDER BY Mtl_PrtNo, ID;", conn.OpenConn);
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

            string? dash = BuildDashDisplay(projNum, seriesNo);
            if (!IsValidDashDisplay(dash))
            {
                dash = Prompt(ed, "Enter Project-Series Number (format XXXX-YYYY or XXXX-YYYYY):") ?? string.Empty;
                if (!IsValidDashDisplay(dash)) return null;
            }
            return dash;
        }

        private static void InsertAndTagTable(
            Autodesk.AutoCAD.ApplicationServices.Document doc,
            Table table,
            Point3d insPt,
            BomTableTagHelper.BomTableKind kind,
            string format)
        {
            var db = doc.Database;
            using var tr = db.TransactionManager.StartTransaction();
            AcadTableHelper.ApplyPreferredTableStyle(table, db, tr);

            table.Position = insPt;
            var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            btr.AppendEntity(table);
            tr.AddNewlyCreatedDBObject(table, true);

            BomTableTagHelper.TagTable(table, tr, kind, format);

            tr.Commit();
        }

        private static string? PromptBomTableFormat(Editor ed)
        {
            var options = new PromptKeywordOptions("\nSelect table format [Titleblock/Wide] <Wide>:");
            options.Keywords.Add("Titleblock");
            options.Keywords.Add("Wide");
            options.Keywords.Default = "Wide";
            options.AllowNone = true;

            var result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.None || string.IsNullOrWhiteSpace(result.StringResult))
                return BomTableTagHelper.FormatWide;

            if (result.Status != PromptStatus.OK)
                return null;

            return result.StringResult.Equals("Titleblock", System.StringComparison.OrdinalIgnoreCase)
                ? BomTableTagHelper.FormatTitleblock
                : BomTableTagHelper.FormatWide;
        }

        private static string? Prompt(Editor ed, string message)
        {
            var res = ed.GetString(new PromptStringOptions($"\n{message}") { AllowSpaces = true });
            return res.Status == PromptStatus.OK ? res.StringResult : null;
        }
    }
}
