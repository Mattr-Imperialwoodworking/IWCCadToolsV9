using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception   = System.Exception;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Commands and helpers for IWC task tags (TODO / NOTE annotations in drawings).
    ///
    /// Tag format placed in the drawing:  [IWC-TODO] some description
    /// CSV record format:  FileName,Tag,Description,X,Y,Z,Space,Created,Due
    /// </summary>
    public static class TaskTagHelper
    {
        public const string TagLayerName = "IWC_TODO_LAYER";
        public const string CsvFileName  = "ToDoIndex.csv";

        private static readonly Regex _tagPattern =
            new(@"^\[IWC-(TODO|NOTE)\]\s+(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---------------------------------------------------------------------------
        // IWCAddTaskTag
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCAddTaskTag")]
        public static void AddTaskTag()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            try
            {
                // 1. Tag type
                var tagOpts = new PromptKeywordOptions("\nSelect tag type: ");
                tagOpts.Keywords.Add("TODO");
                tagOpts.Keywords.Add("NOTE");
                tagOpts.Keywords.Default = "TODO";
                var tagRes = ed.GetKeywords(tagOpts);
                if (tagRes.Status != PromptStatus.OK) return;

                // 2. Description
                var descRes = ed.GetString(
                    new PromptStringOptions("\nEnter task description: ") { AllowSpaces = true });
                if (descRes.Status != PromptStatus.OK) return;

                // 3. Insertion point
                var ptRes = ed.GetPoint("\nSelect insertion point: ");
                if (ptRes.Status != PromptStatus.OK) return;

                // 4. Due date
                var dueRes = ed.GetString(
                    new PromptStringOptions("\nEnter due date (YYYY-MM-DD): ") { AllowSpaces = false });
                if (dueRes.Status != PromptStatus.OK) return;
                if (!DateTime.TryParse(dueRes.StringResult, out DateTime dueDate))
                {
                    ed.WriteMessage("\nInvalid due date format.");
                    return;
                }

                string tagType    = tagRes.StringResult;
                string desc       = descRes.StringResult.Trim();
                Point3d position  = ptRes.Value;
                string created    = DateTime.Now.ToString("yyyy-MM-dd");

                // 5. Write text entity to drawing
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    EnsureLayer(TagLayerName, db, tr);
                    var bt  = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var text = new DBText
                    {
                        Position   = position,
                        Height     = 6,
                        TextString = $"[IWC-{tagType}] {desc}",
                        Layer      = TagLayerName,
                    };
                    btr.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                    tr.Commit();
                }

                // 6. Append to CSV
                string folder  = Path.GetDirectoryName(doc.Name) ?? string.Empty;
                string csvPath = Path.Combine(folder, CsvFileName);
                string line    = BuildCsvRecord(
                    Path.GetFileName(doc.Name), tagType, desc,
                    position.X, position.Y, position.Z,
                    "Model", created, dueDate.ToString("yyyy-MM-dd"));
                File.AppendAllText(csvPath, line + Environment.NewLine);

                ed.WriteMessage($"\nTask tag added and logged to: {csvPath}");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nError: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------------
        // IWCListTaskTags
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCListTaskTags")]
        public static void ListTaskTags()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;

            var folderRes = ed.GetString(
                new PromptStringOptions("\nEnter folder path to scan for DWG files:") { AllowSpaces = true });
            if (folderRes.Status != PromptStatus.OK) return;

            string folder = folderRes.StringResult.Trim();
            if (!Directory.Exists(folder))
            {
                ed.WriteMessage("\nInvalid folder path.");
                return;
            }

            var dwgFiles = Directory.GetFiles(folder, "*.dwg", SearchOption.TopDirectoryOnly);
            var records  = new List<string>();

            foreach (var dwgPath in dwgFiles)
            {
                using var db = new Database(false, true);
                try
                {
                    db.ReadDwgFile(dwgPath, System.IO.FileShare.ReadWrite, true, string.Empty);
                    using var tr  = db.TransactionManager.StartTransaction();
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                    foreach (ObjectId btrId in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                        if (!btr.IsLayout && btr.Name != BlockTableRecord.ModelSpace)
                            continue;

                        foreach (ObjectId entId in btr)
                        {
                            var ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                            if (ent is DBText text)
                                TryMatchTag(text.TextString, text.Position, dwgPath, btr.Name, records);
                            else if (ent is MText mtext)
                                TryMatchTag(mtext.Contents, mtext.Location, dwgPath, btr.Name, records);
                        }
                    }
                    tr.Commit();
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\nSkipped {Path.GetFileName(dwgPath)}: {ex.Message}");
                }
            }

            string csvOut = Path.Combine(folder, CsvFileName);
            try
            {
                File.WriteAllLines(csvOut, records);
                ed.WriteMessage($"\nCSV written to: {csvOut}  ({records.Count} task(s) found)");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nError writing CSV: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------------
        // Internal helpers
        // ---------------------------------------------------------------------------

        private static void TryMatchTag(
            string content, Point3d pos, string filePath, string spaceName,
            List<string> output)
        {
            var m = _tagPattern.Match(content);
            if (!m.Success) return;

            string tag  = m.Groups[1].Value.ToUpper();
            string desc = m.Groups[2].Value;
            output.Add(BuildCsvRecord(
                Path.GetFileName(filePath), tag, desc,
                pos.X, pos.Y, pos.Z, spaceName,
                DateTime.Now.ToString("yyyy-MM-dd"), string.Empty));
        }

        private static void EnsureLayer(string layerName, Database db, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName)) return;

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = layerName };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        private static string BuildCsvRecord(
            string file, string tag, string desc,
            double x, double y, double z,
            string space, string created, string due)
        {
            // Quote description to handle embedded commas
            return $"{file},IWC-{tag},\"{desc}\",{x},{y},{z},{space},{created},{due}";
        }
    }
}
