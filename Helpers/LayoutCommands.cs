using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Commands for importing and managing sheet layout tabs.
    /// </summary>
    public static class LayoutCommands
    {
        /// <summary>Path to the company standard sheet template (.dwt).</summary>
        private const string TemplatePath   = @"I:\LIBRARY\App\ACAD\Templates\Standards\Imperial_Sheet.dwt";
        private const string LayoutToImport = "24 x 36 Series";

        // ---------------------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCImportSheetLayout")]
        public static void IWCImportSheetLayout()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;

            // Prompt for new sheet name
            var pr = ed.GetString("\nEnter new sheet number for layout tab: ");
            if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult))
            {
                ed.WriteMessage("\nSheet number entry cancelled.");
                return;
            }
            var newSheetName = pr.StringResult.Trim().ToUpper();

            // Send the LAYOUT command to import the template tab
            var acadCmd = $".-LAYOUT T \"{TemplatePath}\" \"{LayoutToImport}\"\n";
            doc.SendStringToExecute(acadCmd, true, false, false);

            // Brief wait for AutoCAD to create the new tab
            System.Threading.Thread.Sleep(1000);

            // Find and rename the newly imported layout
            var db = doc.Database;
            using var tr = db.TransactionManager.StartTransaction();

            var layoutDict    = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            ObjectId toRename = ObjectId.Null;
            int      maxSuffix = 1;

            foreach (DBDictionaryEntry entry in layoutDict)
            {
                if (!entry.Key.StartsWith(LayoutToImport, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.Key.Equals(LayoutToImport, StringComparison.OrdinalIgnoreCase))
                    continue;   // skip the original

                var part = entry.Key.Substring(LayoutToImport.Length).Trim();
                if (part.StartsWith("(") && part.EndsWith(")")
                    && int.TryParse(part.Trim('(', ')'), out int n)
                    && n >= maxSuffix)
                {
                    maxSuffix = n;
                    toRename  = entry.Value;
                }
            }

            if (!toRename.IsNull)
            {
                var layout = (Layout)tr.GetObject(toRename, OpenMode.ForWrite);
                layout.LayoutName = newSheetName;
                LayoutManager.Current.CurrentLayout = newSheetName;
                ed.WriteMessage($"\nImported and renamed layout tab: {newSheetName}");
            }
            else
            {
                ed.WriteMessage("\nCould not find the imported layout to rename.");
            }

            tr.Commit();
        }
    }
}
