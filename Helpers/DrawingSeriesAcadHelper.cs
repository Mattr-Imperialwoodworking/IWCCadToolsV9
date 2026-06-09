using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Data.Models;
using IWCCadToolsV9.Helpers;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace IWCCadToolsV9.Helpers
{
    public static class DrawingSeriesAcadHelper
    {
        public const string FileIdPropertyName = "IWC_DwgFileID";
        public const string SheetRegAppName = "IWC_DWG_SHEET";

        public static string GetActiveDocumentFullPath()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            return doc?.Name ?? string.Empty;
        }

        public static bool ActiveDocumentHasUsablePath()
        {
            string path = GetActiveDocumentFullPath();
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<SheetTitleBlockInfo> FindTitleBlockSheets(Document doc)
        {
            var result = new List<SheetTitleBlockInfo>();
            var db = doc.Database;

            using var tr = db.TransactionManager.StartTransaction();
            var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layoutDict)
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (layout.ModelType) continue;

                int? existingSheetId = ReadSheetIdFromLayoutXData(layout);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                SheetTitleBlockInfo? found = null;

                foreach (ObjectId entId in btr)
                {
                    if (!entId.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(BlockReference))))
                        continue;

                    var br = tr.GetObject(entId, OpenMode.ForRead) as BlockReference;
                    if (br == null || br.AttributeCollection.Count == 0) continue;

                    string sheet = string.Empty;
                    string subject = string.Empty;
                    bool hasSheetAttribute = false;
                    bool hasSubjectAttribute = false;
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        if (tr.GetObject(attId, OpenMode.ForRead) is not AttributeReference att) continue;
                        string tag = (att.Tag ?? string.Empty).Trim().ToUpperInvariant();
                        if (tag == "SHEET")
                        {
                            hasSheetAttribute = true;
                            sheet = att.TextString?.Trim() ?? string.Empty;
                        }
                        else if (tag == "SUBJECT")
                        {
                            hasSubjectAttribute = true;
                            subject = att.TextString?.Trim() ?? string.Empty;
                        }
                    }

                    // A proper titleblock for this workflow must expose both SHEET and SUBJECT
                    // attributes.  Requiring both avoids matching unrelated blocks and then
                    // failing to update the actual titleblock subject later.
                    if (!hasSheetAttribute || !hasSubjectAttribute)
                        continue;

                    string blockName = GetBlockEffectiveName(br, tr);
                    found = new SheetTitleBlockInfo
                    {
                        LayoutName = layout.LayoutName,
                        SheetNumber = string.IsNullOrWhiteSpace(sheet) ? layout.LayoutName : sheet,
                        SheetSubject = subject,
                        TitleBlockName = blockName,
                        LayoutId = layout.ObjectId,
                        ExistingSheetId = existingSheetId
                    };
                    break;
                }

                if (found != null)
                    result.Add(found);
            }
            tr.Commit();
            return result;
        }


        public static bool TryOpenFileLocation(string fullPath)
        {
            try
            {
                string? folder = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return false;

                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
                return true;
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\nIWC: Unable to open file location '{fullPath}' — {ex.Message}\n");
                return false;
            }
        }

        public static bool TryApplySheetRecordToActiveDocument(DrawingSeriesSheetRecord sheet, bool renameLayoutToSheetNumber)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || sheet == null) return false;
            return TryApplySheetRecord(doc, sheet, renameLayoutToSheetNumber);
        }

        public static void ApplyDatabaseSheetsToActiveDocument(IReadOnlyList<DrawingSeriesSheetRecord> sheets)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || sheets == null || sheets.Count == 0) return;

            foreach (var sheet in sheets)
                TryApplySheetRecord(doc, sheet, renameLayoutToSheetNumber: true);
        }

        private static bool TryApplySheetRecord(Document doc, DrawingSeriesSheetRecord sheet, bool renameLayoutToSheetNumber)
        {
            var db = doc.Database;
            bool changed = false;
            string? originalLayoutName = null;

            using (doc.LockDocument())
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    Layout? layout = null;

                    foreach (DBDictionaryEntry entry in layoutDict)
                    {
                        var candidate = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                        if (candidate.ModelType) continue;

                        int? xdataSheetId = ReadSheetIdFromLayoutXData(candidate);
                        string name = candidate.LayoutName ?? string.Empty;
                        if ((sheet.SheetId > 0 && xdataSheetId == sheet.SheetId) ||
                            string.Equals(name, sheet.LayoutName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, sheet.SheetNumber, StringComparison.OrdinalIgnoreCase))
                        {
                            layout = candidate;
                            originalLayoutName = name;
                            break;
                        }
                    }

                    if (layout == null) return false;

                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    foreach (ObjectId entId in btr)
                    {
                        if (!entId.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(BlockReference)))) continue;
                        var br = tr.GetObject(entId, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.AttributeCollection.Count == 0) continue;

                        bool hasSheetAttribute = false;
                        bool hasSubjectAttribute = false;
                        var attributesToUpdate = new List<AttributeReference>();

                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            if (tr.GetObject(attId, OpenMode.ForRead) is not AttributeReference att) continue;
                            string tag = (att.Tag ?? string.Empty).Trim().ToUpperInvariant();
                            if (tag == "SHEET")
                            {
                                hasSheetAttribute = true;
                                attributesToUpdate.Add(att);
                            }
                            else if (tag == "SUBJECT")
                            {
                                hasSubjectAttribute = true;
                                attributesToUpdate.Add(att);
                            }
                        }

                        if (!hasSheetAttribute || !hasSubjectAttribute)
                            continue;

                        foreach (var att in attributesToUpdate)
                        {
                            string tag = (att.Tag ?? string.Empty).Trim().ToUpperInvariant();

                            // The SHEET attribute in the IWC titleblock is driven from the
                            // paper-space layout tab name.  Do not write the SHEET attribute
                            // directly here; rename the layout tab below and let the
                            // titleblock field/attribute follow its normal drawing logic.
                            if (tag != "SUBJECT")
                                continue;

                            string desired = sheet.SheetSubject ?? string.Empty;
                            if (!string.Equals(att.TextString ?? string.Empty, desired, StringComparison.Ordinal))
                            {
                                att.UpgradeOpen();
                                att.TextString = desired;
                                att.AdjustAlignment(db);
                                changed = true;
                            }
                        }

                        break;
                    }

                    tr.Commit();
                }

                if (renameLayoutToSheetNumber && !string.IsNullOrWhiteSpace(sheet.SheetNumber) &&
                    !string.IsNullOrWhiteSpace(originalLayoutName) &&
                    !string.Equals(originalLayoutName, sheet.SheetNumber, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // LayoutManager.RenameLayout is more reliable than assigning Layout.LayoutName
                        // directly, especially when the layout tab UI is already active.
                        Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current.RenameLayout(originalLayoutName, sheet.SheetNumber);
                        changed = true;
                    }
                    catch (System.Exception ex)
                    {
                        doc.Editor.WriteMessage($"\nIWC: Unable to rename layout '{originalLayoutName}' to '{sheet.SheetNumber}' — {ex.Message}\n");
                    }
                }
            }

            if (changed)
            {
                try
                {
                    doc.Editor.Regen();
                }
                catch
                {
                    // A regen failure should not invalidate the database/layout update.
                }
            }

            return changed;
        }

        public static (HashSet<int> SheetIds, HashSet<string> LayoutNames, HashSet<string> SheetNumbers) GetCurrentSheetKeys(Document doc)
        {
            var ids = new HashSet<int>();
            var layouts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var db = doc.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layoutDict)
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (layout.ModelType) continue;

                if (!string.IsNullOrWhiteSpace(layout.LayoutName))
                    layouts.Add(layout.LayoutName.Trim());

                int? sheetId = ReadSheetIdFromLayoutXData(layout);
                if (sheetId.HasValue && sheetId.Value > 0)
                    ids.Add(sheetId.Value);

                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId entId in btr)
                {
                    if (!entId.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(BlockReference)))) continue;
                    var br = tr.GetObject(entId, OpenMode.ForRead) as BlockReference;
                    if (br == null || br.AttributeCollection.Count == 0) continue;

                    bool hasSheetAttribute = false;
                    bool hasSubjectAttribute = false;
                    string sheetNumber = string.Empty;

                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        if (tr.GetObject(attId, OpenMode.ForRead) is not AttributeReference att) continue;
                        string tag = (att.Tag ?? string.Empty).Trim().ToUpperInvariant();
                        if (tag == "SHEET")
                        {
                            hasSheetAttribute = true;
                            sheetNumber = att.TextString?.Trim() ?? string.Empty;
                        }
                        else if (tag == "SUBJECT")
                        {
                            hasSubjectAttribute = true;
                        }
                    }

                    if (hasSheetAttribute && hasSubjectAttribute)
                    {
                        if (!string.IsNullOrWhiteSpace(sheetNumber))
                            numbers.Add(sheetNumber.Trim());
                        break;
                    }
                }
            }
            tr.Commit();
            return (ids, layouts, numbers);
        }


        private static int? ReadSheetIdFromLayoutXData(Layout layout)
        {
            try
            {
                var rb = layout.GetXDataForApplication(SheetRegAppName);
                if (rb == null) return null;
                using (rb)
                {
                    var values = rb.AsArray();
                    if (values.Length < 3) return null;
                    if (!string.Equals(values[0].Value?.ToString(), SheetRegAppName, StringComparison.OrdinalIgnoreCase)) return null;
                    if (values[2].Value is int sheetId && sheetId > 0) return sheetId;
                    return int.TryParse(values[2].Value?.ToString(), out int parsed) && parsed > 0 ? parsed : null;
                }
            }
            catch
            {
                return null;
            }
        }

        public static void WriteFileIdToDwg(int fileId)
        {
            AcadFilePropHelper.SetCustomProperty(FileIdPropertyName, fileId.ToString());
        }

        public static int? ReadFileIdFromDwg()
        {
            string? value = AcadFilePropHelper.GetCustomProperty(FileIdPropertyName);
            return int.TryParse(value, out int id) && id > 0 ? id : null;
        }

        public static void WriteSheetXData(Document doc, ObjectId layoutId, int fileId, int sheetId, string sheetNumber)
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                EnsureRegApp(db, tr, SheetRegAppName);
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                layout.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, SheetRegAppName),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, fileId),
                    new TypedValue((int)DxfCode.ExtendedDataInteger32, sheetId),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, sheetNumber ?? string.Empty));
                tr.Commit();
            }
        }

        public static bool TryOpenDwg(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return false;

            var dm = Application.DocumentManager;
            foreach (Document openDoc in dm)
            {
                if (string.Equals(openDoc.Name, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    dm.MdiActiveDocument = openDoc;
                    return true;
                }
            }

            try
            {
                var opened = dm.Open(fullPath, false);
                if (opened != null)
                    dm.MdiActiveDocument = opened;
                return true;
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\nIWC: Unable to open drawing '{fullPath}' — {ex.Message}\n");
                return false;
            }
        }

        public static bool TryActivateSheetLayout(string fullPath, DrawingSeriesSheetRecord sheet)
        {
            if (sheet == null || !TryOpenDwg(fullPath))
                return false;

            var dm = Application.DocumentManager;
            var doc = dm.MdiActiveDocument;
            if (doc == null) return false;

            try
            {
                string? targetLayout = FindMatchingLayoutName(doc, sheet);
                if (string.IsNullOrWhiteSpace(targetLayout))
                    return false;

                // Make sure AutoCAD's active-document pointer is still the drawing we just
                // opened/activated.  Palette clicks are modeless, and CurrentLayout can fail
                // if AutoCAD has not fully switched document context before the layout change.
                dm.MdiActiveDocument = doc;

                if (TrySetCurrentLayout(doc, targetLayout))
                    return true;

                doc.Editor.WriteMessage($"\nIWC: Layout was found but could not be activated: '{targetLayout}' in '{fullPath}'.\n");
                return false;
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nIWC: Unable to activate Drawing Series layout '{sheet.LayoutName}' — {ex.Message}\n");
                return false;
            }
        }

        private static string? FindMatchingLayoutName(Document doc, DrawingSeriesSheetRecord sheet)
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var layoutDict = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (layout.ModelType) continue;

                    int? xdataSheetId = ReadSheetIdFromLayoutXData(layout);
                    string name = (layout.LayoutName ?? string.Empty).Trim();
                    string storedLayoutName = (sheet.LayoutName ?? string.Empty).Trim();
                    string storedSheetNumber = (sheet.SheetNumber ?? string.Empty).Trim();

                    if ((sheet.SheetId > 0 && xdataSheetId == sheet.SheetId) ||
                        string.Equals(name, storedLayoutName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, storedSheetNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        tr.Commit();
                        return layout.LayoutName;
                    }
                }

                tr.Commit();
            }

            return null;
        }

        private static bool TrySetCurrentLayout(Document doc, string layoutName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(layoutName))
                return false;

            try
            {
                // CTAB is the most reliable way to switch layouts from modeless palette code.
                // LayoutManager.CurrentLayout can throw in some document-context timing cases
                // even when the target layout exists and the drawing has been activated.
                Application.SetSystemVariable("CTAB", layoutName);
                doc.Editor.Regen();
                return string.Equals(Convert.ToString(Application.GetSystemVariable("CTAB")), layoutName, StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Exception ctabEx)
            {
                doc.Editor.WriteMessage($"\nIWC: CTAB layout switch failed for '{layoutName}' — {ctabEx.Message}\n");
            }

            try
            {
                Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current.CurrentLayout = layoutName;
                doc.Editor.Regen();
                return string.Equals(Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current.CurrentLayout, layoutName, StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Exception lmEx)
            {
                doc.Editor.WriteMessage($"\nIWC: LayoutManager layout switch failed for '{layoutName}' — {lmEx.Message}\n");
            }

            try
            {
                string escaped = layoutName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                doc.SendStringToExecute($"_.LAYOUT _Set \"{escaped}\"\n", true, false, false);
                return true;
            }
            catch (System.Exception sendEx)
            {
                doc.Editor.WriteMessage($"\nIWC: Command-line layout switch failed for '{layoutName}' — {sendEx.Message}\n");
                return false;
            }
        }


        public static IReadOnlyList<int> GetLoggedSheetIdsFromLayouts(Document doc)
        {
            var ids = new List<int>();
            if (doc == null) return ids;

            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (layout.ModelType) continue;

                    int? sheetId = ReadSheetIdFromLayoutXData(layout);
                    if (sheetId.HasValue && sheetId.Value > 0 && !ids.Contains(sheetId.Value))
                        ids.Add(sheetId.Value);
                }
                tr.Commit();
            }

            return ids;
        }

        public static void ClearDrawingSeriesXData(Document doc)
        {
            if (doc == null) return;

            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForWrite);
                    if (layout.ModelType) continue;

                    if (layout.GetXDataForApplication(SheetRegAppName) != null)
                        layout.XData = null;
                }
                tr.Commit();
            }
        }

        public static void ClearFileIdFromDwg()
        {
            // Set to NA instead of removing the custom property; ReadFileIdFromDwg()
            // treats non-numeric values as no file ID while preserving the expected key.
            AcadFilePropHelper.SetCustomProperty(FileIdPropertyName, "NA");
        }


        private static void EnsureRegApp(Database db, Transaction tr, string appName)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(appName)) return;
            rat.UpgradeOpen();
            var rec = new RegAppTableRecord { Name = appName };
            rat.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        private static string GetBlockEffectiveName(BlockReference br, Transaction tr)
        {
            try
            {
                if (br.IsDynamicBlock)
                {
                    var dyn = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                    return dyn.Name;
                }
                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                return btr.Name;
            }
            catch (AcadException)
            {
                return string.Empty;
            }
        }
    }
}
