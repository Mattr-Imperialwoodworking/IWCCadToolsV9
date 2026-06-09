using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Helpers;
using IWCCadToolsV9.Commands;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Core
{
    /// <summary>
    /// Wires AutoCAD document lifecycle events to the ProjectContextService.
    ///
    /// Register() is called once from IWCExtensionApp.Initialize() (IExtensionApplication).
    /// After that, every drawing open/create/activate/save is handled automatically
    /// without any manual IWCStartup command invocation.
    ///
    /// Thread safety note:
    ///   DocumentCreated and DocumentOpened can fire off the main AutoCAD thread.
    ///   All AutoCAD Database access MUST be wrapped in ExecuteInApplicationContext.
    ///   SQL queries (inside ProjectContextService.LoadAsync) are fine off-thread.
    /// </summary>
    public static class DrawingLifecycleHandler
    {
        private static bool _registered;

        // -----------------------------------------------------------------------
        // Registration — called once at add-in load
        // -----------------------------------------------------------------------

        public static void Register()
        {
            if (_registered) return;

            var dm = Application.DocumentManager;
            dm.DocumentCreated   += OnDocumentCreated;   // fires for both new and opened DWGs
            dm.DocumentActivated += OnDocumentActivated;

            _registered = true;
        }

        public static void Unregister()
        {
            if (!_registered) return;

            var dm = Application.DocumentManager;
            dm.DocumentCreated   -= OnDocumentCreated;
            dm.DocumentActivated -= OnDocumentActivated;

            _registered = false;
        }


        // -----------------------------------------------------------------------
        // Active-document bootstrap — used when the add-in/palette is loaded
        // after a drawing is already open. DocumentCreated will not fire for
        // that already-open drawing, so this checks the current DWG properties
        // and loads project context only when the file is already linked.
        // It intentionally does not prompt for a project/dash.
        // -----------------------------------------------------------------------

        public static void InitializeActiveDocumentIfLinked()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            _ = InitializeDocumentIfLinkedAsync(doc);
        }

        private static async System.Threading.Tasks.Task InitializeDocumentIfLinkedAsync(Document doc)
        {
            try
            {
                string? idStr = AcadFilePropHelper.GetCustomProperty("IWC_ID");
                if (!int.TryParse(idStr, out int projId) || projId <= 0)
                    return;

                var svc = ProjectContextService.GetOrCreate(doc);
                DwgPropertyStore.EnsureAllKeysExist(doc);
                WireBeforeSaveOnce(doc, svc);
                WireSheetEraseMonitorOnce(doc);
                WireSaveAsMonitorOnce(doc, svc);
                AutoUpdateExistingTablesOnce(doc);

                if (svc.HasProject)
                    svc.RaiseProjectLoaded();
                else
                    await svc.LoadAsync().ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\nIWC: Active drawing project check failed — {ex.Message}\n");
            }
        }

        // -----------------------------------------------------------------------
        // DocumentCreated — fires for both new drawings and opened DWG files.
        // DocumentCollection does not expose a separate DocumentOpened event;
        // DocumentCreated is the single entry point for all document loads.
        // -----------------------------------------------------------------------

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            // DocumentCreated fires on the main AutoCAD thread in AutoCAD 2025.
            // Run the async load as fire-and-forget so we don't block the event.
            _ = InitializeDocumentAsync(e.Document);
        }

        private static async System.Threading.Tasks.Task InitializeDocumentAsync(Document? doc)
        {
            if (doc == null) return;
            try
            {
                var svc = ProjectContextService.GetOrCreate(doc);

                DwgPropertyStore.EnsureAllKeysExist(doc);
                WireBeforeSaveOnce(doc, svc);
                WireSheetEraseMonitorOnce(doc);
                WireSaveAsMonitorOnce(doc, svc);

                // LoadAsync does SQL I/O — run on background thread, then return
                // to the AutoCAD main thread before showing any modal dialogs.
                await svc.LoadAsync().ConfigureAwait(false);

                Application.DocumentManager.ExecuteInApplicationContext(
                    state =>
                    {
                        AutoUpdateExistingTablesOnce((Document)state!);
                        DrawingSeriesService.SyncActiveDocumentSheetsFromDatabase();
                    }, doc);

                // PromptForProject / PromptForDash call ShowModalDialog which must
                // be invoked on the main AutoCAD thread. ExecuteInApplicationContext
                // is the documented safe way to marshal back from any thread.
                if (!svc.HasProject || !svc.HasDash)
                {
                    Application.DocumentManager.ExecuteInApplicationContext(
                        state =>
                        {
                            var s = (ProjectContextService)state!;
                            if (!s.HasProject) s.PromptForProject();
                            else if (!s.HasDash) s.PromptForDash();
                        }, svc);
                }
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\nIWC: Drawing initialization failed — {ex.Message}\n");
            }
        }

        // -----------------------------------------------------------------------
        // DocumentActivated — user switches between open drawings
        // -----------------------------------------------------------------------

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            // Activation is always on the main thread — no ExecuteInApplicationContext needed.
            // Just raise ProjectLoaded so UI controls (palette, project nav) rebind
            // to the newly active document's context.
            try
            {
                if (e.Document == null) return;
                var svc = ProjectContextService.GetOrCreate(e.Document);

                AutoUpdateExistingTablesOnce(e.Document);
                DrawingSeriesService.SyncActiveDocumentSheetsFromDatabase();

                // Re-raise so subscribed UI controls refresh without a DB round-trip
                // The data is already loaded from when this document was opened/created.
                if (svc.HasProject)
                    svc.RaiseProjectLoaded();
            }
            catch { /* activation notification is best-effort */ }
        }

        // -----------------------------------------------------------------------
        // Automatic table refresh — only refreshes drawings that already contain
        // stored IWC material/hardware table references. This keeps existing
        // IWCUpdateMaterialTable / IWCUpdateHardwareTable command behavior intact
        // while allowing opened drawings to update automatically.
        // -----------------------------------------------------------------------

        private const string AutoTablesUpdatedKey = "IWC_AutoTablesUpdatedOnOpen_v1";

        private static void AutoUpdateExistingTablesOnce(Document doc)
        {
            // TableCommands and AcadFilePropHelper operate on MdiActiveDocument.
            // If this lifecycle event is for a background/non-active document, wait
            // until DocumentActivated so the correct drawing is active before updating.
            if (!object.ReferenceEquals(Application.DocumentManager.MdiActiveDocument, doc))
                return;

            if (doc.UserData[AutoTablesUpdatedKey] is bool updated && updated)
                return;

            doc.UserData[AutoTablesUpdatedKey] = true;

            try
            {
                TableCommands.AutoUpdateExistingTablesInActiveDocument(quiet: true);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nIWC: Automatic material/hardware table refresh failed — {ex.Message}\n");
            }
        }

        // -----------------------------------------------------------------------
        // Layout deletion monitor — if a logged paper-space layout tab is erased,
        // prompt the user to remove the matching Dwg_Sheet row from SQL.
        // -----------------------------------------------------------------------

        private const string SheetEraseWireKey = "IWC_SheetEraseMonitorWired_v1";

        private static void WireSheetEraseMonitorOnce(Document doc)
        {
            if (doc.UserData[SheetEraseWireKey] is bool wired && wired)
                return;

            doc.Database.ObjectErased += (s, e) =>
            {
                try
                {
                    if (!e.Erased) return;
                    if (e.DBObject is not Autodesk.AutoCAD.DatabaseServices.Layout layout) return;
                    if (layout.ModelType) return;

                    Application.DocumentManager.ExecuteInApplicationContext(
                        state =>
                        {
                            var erasedDoc = (Document)state!;
                            if (!object.ReferenceEquals(Application.DocumentManager.MdiActiveDocument, erasedDoc))
                                return;
                            DrawingSeriesService.ReconcileDeletedSheetsInActiveDocument(promptUser: true);
                        }, doc);
                }
                catch { /* layout erase monitoring is best-effort */ }
            };

            doc.UserData[SheetEraseWireKey] = true;
        }



        // -----------------------------------------------------------------------
        // Save As monitor — AutoCAD updates Document.Name after SAVEAS completes.
        // When a logged Drawing Series file is saved under a different path, prompt
        // to either update the database file path or remove the copied sheet links.
        // -----------------------------------------------------------------------

        private const string SaveAsWireKey = "IWC_SaveAsMonitorWired_v1";
        private const string LastKnownPathKey = "IWC_LastKnownFullPath_v1";

        private static void WireSaveAsMonitorOnce(Document doc, ProjectContextService svc)
        {
            if (doc.UserData[SaveAsWireKey] is bool wired && wired)
                return;

            doc.UserData[LastKnownPathKey] = doc.Name ?? string.Empty;

            doc.CommandEnded += (s, e) =>
            {
                try
                {
                    string previousPath = doc.UserData[LastKnownPathKey]?.ToString() ?? string.Empty;
                    string currentPath = doc.Name ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(currentPath))
                        return;

                    if (!string.Equals(previousPath, currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Application.DocumentManager.ExecuteInApplicationContext(
                            state =>
                            {
                                var tuple = (Tuple<Document, ProjectContextService, string>)state!;
                                var monitoredDoc = tuple.Item1;
                                var monitoredSvc = tuple.Item2;
                                var oldPath = tuple.Item3;

                                if (!object.ReferenceEquals(Application.DocumentManager.MdiActiveDocument, monitoredDoc))
                                    Application.DocumentManager.MdiActiveDocument = monitoredDoc;

                                DrawingSeriesService.ReviewSaveAsPathChange(monitoredDoc, monitoredSvc, oldPath);
                                monitoredDoc.UserData[LastKnownPathKey] = monitoredDoc.Name ?? string.Empty;
                            }, Tuple.Create(doc, svc, previousPath));
                    }
                    else
                    {
                        doc.UserData[LastKnownPathKey] = currentPath;
                    }
                }
                catch { /* Save As monitoring is best-effort */ }
            };

            doc.UserData[SaveAsWireKey] = true;
        }

        // -----------------------------------------------------------------------
        // BeforeSave — wire per-document (called from Created and Opened)
        // -----------------------------------------------------------------------

        private const string BeforeSaveWireKey = "IWC_BeforeSaveWired_v1";

        private static void WireBeforeSaveOnce(Document doc, ProjectContextService svc)
        {
            // Database.BeginSave fires before the DWG bytes are written to disk,
            // ensuring the custom properties in the saved file are always current.
            if (doc.UserData[BeforeSaveWireKey] is bool wired && wired)
                return;

            doc.Database.BeginSave += (s, e) =>
            {
                try
                {
                    svc.PersistToDwg();
                    DrawingSeriesService.ReconcileDeletedSheetsInActiveDocument(promptUser: true);
                    DrawingSeriesService.PromptToAssociateActiveDocumentIfNeeded(doc, svc);
                }
                catch { /* non-fatal — don't block the save */ }
            };

            doc.UserData[BeforeSaveWireKey] = true;
        }
    }

    // ---------------------------------------------------------------------------
    // IExtensionApplication entry point
    // This is what AutoCAD calls when the DLL is loaded (via NETLOAD or .scr).
    // Replace any existing IExtensionApplication implementation with this,
    // or add the Register() call to your existing Initialize() method.
    // ---------------------------------------------------------------------------

    public class IWCExtensionApp : IExtensionApplication
    {
        public void Initialize()
        {
            DrawingLifecycleHandler.Register();

            // Show a brief confirmation in the command line
            Application.DocumentManager.MdiActiveDocument?.Editor
                .WriteMessage("\nIWC CAD Tools V9 loaded.\n");
        }

        public void Terminate()
        {
            DrawingLifecycleHandler.Unregister();
        }
    }
}
