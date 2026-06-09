using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Core
{
    public static class DrawingSeriesService
    {
        public static event EventHandler? DrawingSeriesDataChanged;

        private static void RaiseDrawingSeriesDataChanged()
        {
            try
            {
                DrawingSeriesDataChanged?.Invoke(null, EventArgs.Empty);
            }
            catch
            {
                // Palette refresh notifications should never break AutoCAD save/open workflows.
            }
        }

        public static bool AssociateActiveDocument(ProjectContextService? svc, bool showSuccessMessage)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || svc == null || !svc.HasProject || !svc.HasDash)
                return false;

            if (!DrawingSeriesAcadHelper.ActiveDocumentHasUsablePath())
            {
                MessageBox.Show("Save this drawing before associating it to the drawing series.",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var sheets = DrawingSeriesAcadHelper.FindTitleBlockSheets(doc);
            if (sheets.Count == 0)
            {
                var result = MessageBox.Show(
                    "No paper-space layouts with titleblocks containing SHEET and SUBJECT attributes were found. Associate the file anyway?",
                    "IWC Drawing Series", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result != DialogResult.Yes) return false;
            }

            var summary = AcadFilePropHelper.GetSummaryProps();
            var custom = AcadFilePropHelper.GetAllCustomProperties();
            var repo = new DrawingSeriesRepository();

            // Prefer the file ID already stored in the DWG.  This prevents duplicate
            // Dwg_File rows, and therefore duplicate sheet sets, when the same drawing
            // is opened through a different path/case/mapped drive later.
            int? existingFileId = DrawingSeriesAcadHelper.ReadFileIdFromDwg();
            int fileId = repo.UpsertFileForCurrentDocumentAsync(
                svc.Project!.Id,
                svc.Dash!.DashId,
                doc.Name,
                summary?.Title,
                summary?.Subject,
                summary?.Author,
                summary?.Keywords,
                summary?.Comments,
                summary?.HyperlinkBase,
                summary?.RevisionNumber,
                custom,
                existingFileId).GetAwaiter().GetResult();

            DrawingSeriesAcadHelper.WriteFileIdToDwg(fileId);

            int addedCount = 0;
            int skippedCount = 0;
            foreach (var sheet in sheets)
            {
                var result = repo.AddSheetIfMissingAsync(fileId, sheet).GetAwaiter().GetResult();
                if (result.Added)
                    addedCount++;
                else
                    skippedCount++;

                DrawingSeriesAcadHelper.WriteSheetXData(doc, sheet.LayoutId, fileId, result.SheetId, sheet.SheetNumber);
            }

            if (showSuccessMessage)
            {
                MessageBox.Show($"Current drawing associated to dash {svc.Dash.DashNum}.\n\nNew sheets logged: {addedCount:N0}\nAlready logged sheets skipped: {skippedCount:N0}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return true;
        }


        public static bool RefreshActiveDocumentSheetsToDatabase(ProjectContextService? svc, bool showSuccessMessage)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || svc == null || !svc.HasProject || !svc.HasDash)
                return false;

            if (!DrawingSeriesAcadHelper.ActiveDocumentHasUsablePath())
            {
                MessageBox.Show("Save this drawing before refreshing drawing series sheet data.",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var repo = new DrawingSeriesRepository();
            int? fileId = DrawingSeriesAcadHelper.ReadFileIdFromDwg();
            if (!fileId.HasValue)
                fileId = repo.GetFileIdByFullPathAsync(doc.Name).GetAwaiter().GetResult();

            if (!fileId.HasValue || fileId.Value <= 0)
            {
                MessageBox.Show("This drawing has not been associated to the Drawing Series yet. Use Add Current File to Dash first.",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var sheets = DrawingSeriesAcadHelper.FindTitleBlockSheets(doc);
            int refreshedCount = 0;
            int skippedCount = 0;
            foreach (var sheet in sheets)
            {
                bool isLogged = sheet.ExistingSheetId.HasValue ||
                                repo.IsSheetAlreadyLoggedAsync(fileId.Value, sheet).GetAwaiter().GetResult();
                if (!isLogged)
                {
                    skippedCount++;
                    continue;
                }

                int sheetId = repo.RefreshLoggedSheetAsync(fileId.Value, sheet).GetAwaiter().GetResult();
                DrawingSeriesAcadHelper.WriteSheetXData(doc, sheet.LayoutId, fileId.Value, sheetId, sheet.SheetNumber);
                refreshedCount++;
            }

            ReconcileDeletedSheetsInActiveDocument(promptUser: true);

            if (showSuccessMessage)
            {
                MessageBox.Show($"Drawing Series sheet data refreshed.\n\nLogged sheets refreshed: {refreshedCount:N0}\nUnlogged layouts skipped: {skippedCount:N0}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return true;
        }

        public static void SyncActiveDocumentSheetsFromDatabase()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || !DrawingSeriesAcadHelper.ActiveDocumentHasUsablePath())
                return;

            try
            {
                var repo = new DrawingSeriesRepository();
                int? fileId = DrawingSeriesAcadHelper.ReadFileIdFromDwg();
                if (!fileId.HasValue)
                    fileId = repo.GetFileIdByFullPathAsync(doc.Name).GetAwaiter().GetResult();
                if (!fileId.HasValue || fileId.Value <= 0)
                    return;

                ReconcileDeletedSheetsInActiveDocument(promptUser: true);

                var sheets = repo.GetSheetsForFileIdAsync(fileId.Value).GetAwaiter().GetResult();
                DrawingSeriesAcadHelper.ApplyDatabaseSheetsToActiveDocument(sheets);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nIWC: Drawing Series sheet sync failed — {ex.Message}\n");
            }
        }

        public static void ReconcileDeletedSheetsInActiveDocument(bool promptUser)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || !DrawingSeriesAcadHelper.ActiveDocumentHasUsablePath())
                return;

            try
            {
                var repo = new DrawingSeriesRepository();
                int? fileId = DrawingSeriesAcadHelper.ReadFileIdFromDwg();
                if (!fileId.HasValue)
                    fileId = repo.GetFileIdByFullPathAsync(doc.Name).GetAwaiter().GetResult();
                if (!fileId.HasValue || fileId.Value <= 0)
                    return;

                var loggedSheets = repo.GetSheetsForFileIdAsync(fileId.Value).GetAwaiter().GetResult();
                if (loggedSheets.Count == 0)
                    return;

                var keys = DrawingSeriesAcadHelper.GetCurrentSheetKeys(doc);
                var missing = loggedSheets
                    .Where(s => s.SheetId > 0 &&
                                !keys.SheetIds.Contains(s.SheetId) &&
                                (string.IsNullOrWhiteSpace(s.LayoutName) || !keys.LayoutNames.Contains(s.LayoutName.Trim())) &&
                                (string.IsNullOrWhiteSpace(s.SheetNumber) || !keys.SheetNumbers.Contains(s.SheetNumber.Trim())))
                    .ToList();

                if (missing.Count == 0)
                    return;

                if (promptUser)
                {
                    string preview = string.Join(Environment.NewLine,
                        missing.Take(12).Select(s => $"  • {s.SheetNumber} - {s.SheetSubject}  ({s.LayoutName})"));
                    if (missing.Count > 12)
                        preview += Environment.NewLine + $"  ...and {missing.Count - 12:N0} more";

                    var result = MessageBox.Show(
                        $"The following logged Drawing Series sheets no longer exist in this DWG. Delete their database entries?\n\n{preview}",
                        "IWC Drawing Series", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result != DialogResult.Yes)
                        return;
                }

                repo.DeleteSheetsAsync(missing.Select(s => s.SheetId)).GetAwaiter().GetResult();
                RaiseDrawingSeriesDataChanged();
                doc.Editor.WriteMessage($"\nIWC: Removed {missing.Count:N0} deleted sheet entr{(missing.Count == 1 ? "y" : "ies")} from Drawing Series database.\n");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nIWC: Drawing Series deleted-sheet reconciliation failed — {ex.Message}\n");
            }
        }


        public static void PromptToAssociateActiveDocumentIfNeeded(Document doc, ProjectContextService svc)
        {
            try
            {
                if (doc == null || svc == null || !svc.HasProject || !svc.HasDash)
                    return;
                if (!object.ReferenceEquals(Application.DocumentManager.MdiActiveDocument, doc))
                    return;
                if (!DrawingSeriesAcadHelper.ActiveDocumentHasUsablePath())
                    return;

                var sheets = DrawingSeriesAcadHelper.FindTitleBlockSheets(doc);
                if (sheets.Count == 0)
                    return;

                var repo = new DrawingSeriesRepository();
                bool associated = repo.IsFileAssociatedWithDashAsync(svc.Dash!.DashId, doc.Name)
                    .GetAwaiter().GetResult();
                if (associated)
                    return;

                var result = MessageBox.Show(
                    "This drawing contains paper-space titleblock sheets that are not associated to the current dash drawing series. Associate and log sheets now?",
                    "IWC Drawing Series", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                    AssociateActiveDocument(svc, showSuccessMessage: false);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nIWC: Drawing Series association check failed — {ex.Message}\n");
            }
        }
    }
}
