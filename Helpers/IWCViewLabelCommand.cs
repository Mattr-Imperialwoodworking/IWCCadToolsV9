using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
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
    /// IWC_VIEW — inserts a view-label block (IWC_SYM.VIEW, DB id=150) below
    /// each selected paper-space viewport, with attributes and dynamic property
    /// pre-filled from the viewport geometry and user prompts.
    /// </summary>
    public static class IWCViewLabelCommand
    {
        private const string BlockName = "IWC_SYM.VIEW";
        private const int    DbBlockId = 150;

        // Architectural scale lookup: CustomScale (model/paper ratio) → label
        private static readonly (double Scale, string Label)[] ArchScales =
        {
            (1,   "FULL SIZE"),
            (2,   "6\"=1'-0\""),
            (4,   "3\"=1'-0\""),
            (6,   "2\"=1'-0\""),
            (8,   "1-1/2\"=1'-0\""),
            (12,  "1\"=1'-0\""),
            (16,  "3/4\"=1'-0\""),
            (24,  "1/2\"=1'-0\""),
            (32,  "3/8\"=1'-0\""),
            (48,  "1/4\"=1'-0\""),
            (64,  "3/16\"=1'-0\""),
            (96,  "1/8\"=1'-0\""),
            (128, "3/32\"=1'-0\""),
            (192, "1/16\"=1'-0\""),
        };

        // -----------------------------------------------------------------------
        // Command entry point
        // -----------------------------------------------------------------------

        [CommandMethod("IWC_VIEW")]
        public static void InsertViewLabels()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            // Must run in a layout (paper space)
            if (db.TileMode)
            {
                ed.WriteMessage("\nIWC_VIEW must be run in a layout (paper space) tab.\n");
                return;
            }

            // ── 1. Select viewports ──────────────────────────────────────────
            var filter = new SelectionFilter(
                new[] { new TypedValue((int)DxfCode.Start, "VIEWPORT") });
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect viewports to label (press Enter when done): "
            };
            var psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK || psr.Value.Count == 0) return;

            // ── 2. Ensure block is loaded ────────────────────────────────────
            if (!EnsureBlockLoaded(db, ed))
            {
                ed.WriteMessage($"\nIWC_VIEW: Could not load block '{BlockName}' from database.\n");
                return;
            }

            // ── 3. Build sorted viewport list ────────────────────────────────
            List<(ObjectId Id, Viewport Vp)> viewports;
            using (var scanTr = db.TransactionManager.StartTransaction())
            {
                viewports = psr.Value
                    .Cast<SelectedObject>()
                    .Select(so => (so.ObjectId,
                                   Vp: scanTr.GetObject(so.ObjectId, OpenMode.ForRead) as Viewport))
                    .Where(t => t.Vp != null && t.Vp.Number != 1)
                    .Select(t => (t.ObjectId, t.Vp!))
                    .ToList();
                scanTr.Commit();
            }

            if (viewports.Count == 0)
            {
                ed.WriteMessage("\nIWC_VIEW: No valid viewports in selection.\n");
                return;
            }

            // Sort: top-right first → right-to-left → top-to-bottom
            var sorted = SortViewports(viewports.Select(t => t.Vp).ToList());

            // ── 4. Main insert transaction ───────────────────────────────────
            using var tr = db.TransactionManager.StartTransaction();
            var bt      = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var layoutBtr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            ObjectId blockDefId = bt[BlockName];
            var btrDef = (BlockTableRecord)tr.GetObject(blockDefId, OpenMode.ForRead);

            // Count existing view-label instances already on this layout
            int existingCount = CountExistingLabels(tr, layoutBtr, blockDefId);

            int viewNumber = existingCount;

            foreach (var vp in sorted)
            {
                viewNumber++;

                // ── Prompt for title type per viewport ──────────────────────
                string? title = PromptForTitle(ed, viewNumber);
                if (title == null) { tr.Abort(); return; }   // user cancelled

                // ── Geometry ────────────────────────────────────────────────
                double vpLeft   = vp.CenterPoint.X - vp.Width  / 2.0;
                double vpBottom = vp.CenterPoint.Y - vp.Height / 2.0;
                // 1/2" below bottom-left in paper space (1 unit = 1 inch in layouts)
                var insPt = new Point3d(vpLeft, vpBottom - 0.5, 0);

                // ── Annotation scale label ───────────────────────────────────
                string scaleName = GetScaleName(vp);

                // ── Insert block reference ───────────────────────────────────
                var bref = new BlockReference(insPt, blockDefId);
                bref.SetDatabaseDefaults(db);
                layoutBtr.AppendEntity(bref);
                tr.AddNewlyCreatedDBObject(bref, true);

                // ── Set dynamic property "Distance1" = viewport width ────────
                SetDynamicProperty(bref, "Distance1", vp.Width);

                // ── Add and set attributes ───────────────────────────────────
                foreach (ObjectId attDefId in btrDef)
                {
                    var attDef = tr.GetObject(attDefId, OpenMode.ForRead) as AttributeDefinition;
                    if (attDef == null) continue;

                    var attRef = new AttributeReference();
                    attRef.SetAttributeFromBlock(attDef, bref.BlockTransform);
                    bref.AttributeCollection.AppendAttribute(attRef);
                    tr.AddNewlyCreatedDBObject(attRef, true);

                    switch (attDef.Tag.ToUpperInvariant())
                    {
                        case "UT":
                            attRef.TextString = viewNumber.ToString();
                            break;

                        case "LT":
                            // Keep the default field — do not overwrite.
                            break;

                        case "TITLE":
                            attRef.TextString = title;
                            break;

                        case "SCALE":
                            attRef.TextString = scaleName;
                            break;

                        case "ARCHREF":
                            attRef.TextString = string.Empty;
                            break;
                    }
                }
            }

            tr.Commit();
            ed.WriteMessage($"\nIWC_VIEW: Inserted {sorted.Count} view label(s).\n");
        }

        // -----------------------------------------------------------------------
        // Viewport sort — top-right first, then right→left, top→bottom
        // -----------------------------------------------------------------------

        private static List<Viewport> SortViewports(List<Viewport> viewports)
        {
            if (viewports.Count <= 1) return viewports;

            // Group into rows: viewports whose Y centres are within 1" of the
            // running row average are considered the same row.
            const double RowTolerance = 1.0;   // paper-space inches

            var byYDesc = viewports.OrderByDescending(v => v.CenterPoint.Y).ToList();
            var rows    = new List<List<Viewport>>();

            foreach (var vp in byYDesc)
            {
                bool placed = false;
                foreach (var row in rows)
                {
                    double rowAvgY = row.Average(v => v.CenterPoint.Y);
                    if (Math.Abs(rowAvgY - vp.CenterPoint.Y) <= RowTolerance)
                    { row.Add(vp); placed = true; break; }
                }
                if (!placed) rows.Add(new List<Viewport> { vp });
            }

            // Rows: top first (highest avg Y). Within each row: right first (highest X).
            return rows
                .OrderByDescending(r => r.Average(v => v.CenterPoint.Y))
                .SelectMany(r => r.OrderByDescending(v => v.CenterPoint.X))
                .ToList();
        }

        // -----------------------------------------------------------------------
        // Count existing view-label instances on the current layout
        // -----------------------------------------------------------------------

        private static int CountExistingLabels(Transaction tr,
            BlockTableRecord layoutBtr, ObjectId blockDefId)
        {
            int count = 0;
            foreach (ObjectId entId in layoutBtr)
            {
                var bref = tr.GetObject(entId, OpenMode.ForRead) as BlockReference;
                if (bref == null) continue;
                // DynamicBlockTableRecord points to the named def for dynamic blocks
                if (bref.DynamicBlockTableRecord == blockDefId ||
                    bref.BlockTableRecord        == blockDefId)
                    count++;
            }
            return count;
        }

        // -----------------------------------------------------------------------
        // Prompt — view type
        // -----------------------------------------------------------------------

        private static string? PromptForTitle(Editor ed, int viewNumber)
        {
            var pko = new PromptKeywordOptions(
                $"\nView {viewNumber} — select type " +
                "[Plan/Elevation/Vsection/Hsection/Detail]: ",
                "Plan Elevation Vsection Hsection Detail");
            pko.AllowNone = false;

            var pkr = ed.GetKeywords(pko);
            if (pkr.Status != PromptStatus.OK) return null;

            return pkr.StringResult.ToUpperInvariant() switch
            {
                "PLAN"      => "PLAN VIEW",
                "ELEVATION" => "ELEVATION",
                "VSECTION"  => "VERTICAL SECTION",
                "HSECTION"  => "HORIZONTAL SECTION",
                "DETAIL"    => "DETAIL",
                var other   => other
            };
        }

        // -----------------------------------------------------------------------
        // Viewport scale name
        // -----------------------------------------------------------------------

        private static string GetScaleName(Viewport vp)
        {
            // Try the named annotation scale first — it has a human-readable Name.
            try
            {
                var annScale = vp.AnnotationScale;
                if (annScale != null && !string.IsNullOrWhiteSpace(annScale.Name))
                    return annScale.Name.ToUpperInvariant();
            }
            catch { }

            // Fall back to CustomScale ratio lookup
            double cs = vp.CustomScale;
            if (cs > 0)
            {
                foreach (var (scale, label) in ArchScales)
                    if (Math.Abs(scale - cs) / scale < 0.01)
                        return label;

                // Unknown scale — express as ratio
                return $"1:{cs:0.##}";
            }

            return "1:1";
        }

        // -----------------------------------------------------------------------
        // Dynamic block property setter
        // -----------------------------------------------------------------------

        private static void SetDynamicProperty(BlockReference bref,
            string propName, double value)
        {
            try
            {
                var props = bref.DynamicBlockReferencePropertyCollection;
                if (props == null) return;
                foreach (DynamicBlockReferenceProperty prop in props)
                {
                    if (string.Equals(prop.PropertyName, propName,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        if (!prop.ReadOnly) prop.Value = value;
                        break;
                    }
                }
            }
            catch { /* non-fatal */ }
        }

        // -----------------------------------------------------------------------
        // Block loading
        //
        // Step 1 — check if BlockName already exists in the drawing → skip.
        // Step 2 — download the DWG bytes from dbo.Dwg_BlockAssets.
        // Step 3 — write to a temp DWG file.
        // Step 4 — use Database.Insert() (the same mechanism as AutoCAD's
        //           standard INSERT command) to add the block definition to the
        //           drawing.  This is the most reliable import path and correctly
        //           preserves all geometry, dynamic properties, and attributes.
        // Step 5 — call AttributeFieldHelper.PatchFieldsFromSource so field
        //           expressions on AttributeDefinitions (e.g. the LT sheet
        //           number field) survive the import.
        // -----------------------------------------------------------------------

        private static bool EnsureBlockLoaded(Database db, Editor ed)
        {
            // Already in drawing? Skip download entirely.
            using (var chk = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)chk.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (bt.Has(BlockName))
                {
                    chk.Commit();
                    ed.WriteMessage($"\nIWC_VIEW: Block '{BlockName}' already in drawing — skipping download.\n");
                    return true;
                }
                chk.Commit();
            }

            // Fetch DWG bytes from dbo.Dwg_BlockAssets
            byte[]? dwgBytes = FetchAssetDwgBytes(DbBlockId);
            if (dwgBytes == null || dwgBytes.Length == 0)
            {
                ed.WriteMessage($"\nIWC_VIEW: No .dwg asset found for block ID={DbBlockId}.\n");
                return false;
            }

            string tempPath = Path.Combine(
                Path.GetTempPath(), $"IWC_Block_{DbBlockId}_{Guid.NewGuid():N}.dwg");
            try
            {
                File.WriteAllBytes(tempPath, dwgBytes);

                // Database.Insert() is the standard AutoCAD import path:
                // it reads the source DWG and adds its model-space content as a
                // new named block definition in db — identical to the INSERT
                // command's external-DWG behaviour.
                using (var sourceDb = new Database(false, true))
                {
                    sourceDb.ReadDwgFile(
                        tempPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                    sourceDb.CloseInput(true);
                    db.Insert(BlockName, sourceDb, true);
                }

                // Restore field expressions (e.g. LT sheet-number field) that
                // Database.Insert may not preserve in the AttributeDefinitions.
                AttributeFieldHelper.PatchFieldsFromSource(db, tempPath, BlockName);

                return true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nIWC_VIEW: Failed to import block — {ex.Message}\n");
                return false;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        private static byte[]? FetchAssetDwgBytes(int blockId)
        {
            try
            {
                using var conn = IWCConn.GetSqlConnection();
                conn.Open();
                using var cmd = new SqlCommand(@"
                    SELECT TOP 1 FileData
                    FROM   dbo.Dwg_BlockAssets
                    WHERE  BlockID  = @bid
                      AND  FileType = '.dwg'
                    ORDER BY ID ASC;", conn);
                cmd.Parameters.AddWithValue("@bid", blockId);
                return cmd.ExecuteScalar() as byte[];
            }
            catch { return null; }
        }
    }
}
