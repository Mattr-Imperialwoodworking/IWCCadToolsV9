using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Core;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// AutoCAD commands that create tables from database content.
    /// </summary>
    public static class IWCTableCommands
    {
        // -----------------------------------------------------------------------
        // IWC_COMPLIST — insert Dash Component List table
        // -----------------------------------------------------------------------

        /// <summary>
        /// Queries the database for component dashes that belong to the currently
        /// active dash (series), then inserts a formatted two-column AutoCAD table
        /// (Dash_Num | Dash_Desc) at the user-picked insertion point.
        ///
        /// Table layout (dimensions are in plotted inches; multiplied by DIMSCALE):
        ///   Row 0  : "DASH COMPONENT LIST" title — merged, light-grey fill, 1/4" high
        ///   Rows 1+ : Dash_Num (3/4" col) | Dash_Desc (3-1/8" col), 3/8" high each
        /// </summary>
        [CommandMethod("IWC_COMPLIST")]
        public static void InsertDashComponentList()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            // --- Validate project context ---
            var svc = ProjectContextService.GetOrCreate(doc);
            if (!svc.HasProject)
            {
                ed.WriteMessage("\nIWC: No active project — run IWC_RELOAD first.\n");
                return;
            }
            if (!svc.HasDash)
            {
                ed.WriteMessage("\nIWC: No active dash selected — select a dash via Change Project.\n");
                return;
            }

            int projId = svc.Project!.Id;
            int dashId = svc.Dash!.DashId;
            string dashNum = svc.Dash.DashNum ?? dashId.ToString();

            // --- Query component dashes ---
            List<(string Num, string Desc)> components;
            try
            {
                components = FetchComponentDashes(projId, dashId);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nIWC: Failed to query component dashes — {ex.Message}\n");
                return;
            }

            if (components.Count == 0)
            {
                ed.WriteMessage($"\nIWC: No component dashes found for dash {dashNum}.\n");
                return;
            }

            // --- Prompt for insertion point ---
            var ppo = new PromptPointOptions(
                $"\nSpecify insertion point for Dash Component List ({components.Count} component(s)): ");
            var ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return;
            var insPt = new Point3d(ppr.Value.X, ppr.Value.Y, 0);

            // --- Compute model-space sizes from DIMSCALE ---
            double scale = 1.0;
            try { scale = Convert.ToDouble(Application.GetSystemVariable("DIMSCALE")); }
            catch { }
            if (scale <= 0) scale = 1.0;

            double colNumWidth  = 0.75  * scale;   // 3/4"  — Dash_Num column
            double colDescWidth = 3.125 * scale;   // 3-1/8" — Dash_Desc column
            double titleHeight  = 0.25  * scale;   // 1/4"  — title row
            double dataHeight   = 0.375 * scale;   // 3/8"  — data rows
            double textHeight   = 0.125 * scale;   // 1/8"  — text size

            // --- Create and insert the table ---
            using var tr = db.TransactionManager.StartTransaction();
            var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            var table = new Table();
            table.SetDatabaseDefaults(db);
            table.TableStyle = db.Tablestyle;

            // Rows = 1 title + N data rows; Columns = 2
            int totalRows = components.Count + 1;
            table.SetSize(totalRows, 2);

            // Column widths
            table.SetColumnWidth(0, colNumWidth);
            table.SetColumnWidth(1, colDescWidth);

            // --- Title row (row 0) ---
            table.SetRowHeight(0, titleHeight);
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
            SetCell(table, 0, 0,
                text: "DASH COMPONENT LIST",
                height: textHeight,
                align: CellAlignment.MiddleCenter,
                bgColor: Autodesk.AutoCAD.Colors.Color.FromRgb(211, 211, 211));

            // --- Data rows ---
            for (int i = 0; i < components.Count; i++)
            {
                int row = i + 1;
                table.SetRowHeight(row, dataHeight);

                SetCell(table, row, 0,
                    text: components[i].Num,
                    height: textHeight,
                    align: CellAlignment.MiddleCenter);

                SetCell(table, row, 1,
                    text: components[i].Desc,
                    height: textHeight,
                    align: CellAlignment.MiddleLeft);
            }

            table.Position = insPt;
            table.GenerateLayout();

            btr.AppendEntity(table);
            tr.AddNewlyCreatedDBObject(table, true);
            tr.Commit();

            ed.WriteMessage(
                $"\nIWC: Inserted DASH COMPONENT LIST for dash {dashNum}" +
                $" — {components.Count} component(s).\n");
        }

        // -----------------------------------------------------------------------
        // Data access
        // -----------------------------------------------------------------------

        private static List<(string Num, string Desc)> FetchComponentDashes(
            int projId, int parentDashId)
        {
            var list = new List<(string, string)>();

            using var conn = IWCConn.GetSqlConnection();
            conn.Open();

            // Query active component dashes whose Dash_Parent matches the
            // currently active series dash.
            using var cmd = new SqlCommand(@"
                SELECT Dash_Num, Dash_Desc
                FROM   dbo.Proj_DashCompileReportActive
                WHERE  Proj_ID    = @pid
                  AND  Dash_Parent = @parent
                  AND  (Act_Void = 0 OR Act_Void IS NULL)
                ORDER BY TRY_CAST(Dash_Num AS int), Dash_Num;", conn);

            cmd.Parameters.AddWithValue("@pid",    projId);
            cmd.Parameters.AddWithValue("@parent", parentDashId);

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                string num  = rdr["Dash_Num"]  as string ?? string.Empty;
                string desc = rdr["Dash_Desc"] as string ?? string.Empty;
                list.Add((num, desc));
            }

            return list;
        }

        // -----------------------------------------------------------------------
        // Table helpers
        // -----------------------------------------------------------------------

        private static void SetCell(Table table, int row, int col,
            string text, double height, CellAlignment align,
            Autodesk.AutoCAD.Colors.Color? bgColor = null)
        {
            var cell = table.Cells[row, col];
            cell.TextString = text;
            cell.TextHeight = height;
            cell.Alignment  = align;

            if (bgColor != null)
            {
                cell.BackgroundColor = bgColor;
                // IsBackgroundColorNone = false enables the background fill;
                // if unavailable in this API version the colour is still stored.
                try { cell.IsBackgroundColorNone = false; } catch { }
            }
        }
    }
}
