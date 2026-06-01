using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Helpers;
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
                WireBeforeSave(doc, svc);

                // LoadAsync does SQL I/O — run on background thread, then return
                // to the AutoCAD main thread before showing any modal dialogs.
                await svc.LoadAsync().ConfigureAwait(false);

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

                // Re-raise so subscribed UI controls refresh without a DB round-trip
                // The data is already loaded from when this document was opened/created.
                if (svc.HasProject)
                    svc.RaiseProjectLoaded();
            }
            catch { /* activation notification is best-effort */ }
        }

        // -----------------------------------------------------------------------
        // BeforeSave — wire per-document (called from Created and Opened)
        // -----------------------------------------------------------------------

        private static void WireBeforeSave(Document doc, ProjectContextService svc)
        {
            // Database.BeginSave fires before the DWG bytes are written to disk,
            // ensuring the custom properties in the saved file are always current.
            doc.Database.BeginSave += (s, e) =>
            {
                try { svc.PersistToDwg(); }
                catch { /* non-fatal — don't block the save */ }
            };
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
