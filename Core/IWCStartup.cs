using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Helpers;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Core
{
    /// <summary>
    /// Manual fallback commands for cases where the automatic lifecycle
    /// events don't fire (e.g. drawings opened before the add-in loaded,
    /// or a user who wants to re-link a drawing to a different project).
    ///
    /// IWCStartup is kept as a no-op alias for backwards compatibility
    /// with any scripts or muscle memory that call it directly.
    /// The real work is now done by ProjectContextService.
    /// </summary>
    public class IWCStartup
    {
        // -----------------------------------------------------------------------
        // Legacy command — kept for script compatibility
        // Delegates to IWC_RELOAD
        // -----------------------------------------------------------------------

        [CommandMethod("IWCStartup")]
        public void Initialize() => IwcStartupProjectAssociation();

        /// <summary>
        /// IWCStartup is the user-facing project association command.
        ///
        /// Behavior:
        ///   - If the drawing does not already have IWC project association data,
        ///     run the existing reload/startup path unchanged. That path creates
        ///     the required custom property keys, loads any available context, and
        ///     prompts for project/dash when needed.
        ///
        ///   - If the drawing already has IWC_ID / IWC_SeriesID values, do not run
        ///     the SQL reload path first. Running the reload path from a command
        ///     context can interact badly with AutoCAD palette/ribbon UI refresh.
        ///     Instead, use the same ProjectContextService.ChangeProject() path as
        ///     the Current Project tab's Change Project button.
        /// </summary>
        private static void IwcStartupProjectAssociation()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            DwgPropertyStore.EnsureAllKeysExist(doc);

            bool hasExistingAssociation = HasExistingProjectAssociation();
            var svc = ProjectContextService.GetOrCreate(doc);

            if (hasExistingAssociation)
            {
                // Match the palette Change Project button behavior exactly.
                // This clears existing IDs and opens the normal active-project selector.
                svc.ChangeProject();
                doc.Editor.WriteMessage("\nIWC: Project association workflow complete.\n");
                return;
            }

            // No association exists: keep the original startup/reload behavior.
            IwcReload();
        }

        private static bool HasExistingProjectAssociation()
        {
            string? projectId = AcadFilePropHelper.GetCustomProperty("IWC_ID");
            if (int.TryParse(projectId, out int projId) && projId > 0)
                return true;

            string? dashId = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            return int.TryParse(dashId, out int dId) && dId > 0;
        }

        // -----------------------------------------------------------------------
        // IWC_RELOAD — full re-initialization of the active drawing's context
        // -----------------------------------------------------------------------

        [CommandMethod("IWC_RELOAD")]
        public static void IwcReload()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var svc = ProjectContextService.GetOrCreate(doc);

            // Ensure DWG property keys exist
            DwgPropertyStore.EnsureAllKeysExist(doc);

            // Re-load from DWG properties / DB
            svc.Load();

            // If still no project after load, prompt
            if (!svc.HasProject)
                svc.PromptForProject();
            else if (!svc.HasDash)
                svc.PromptForDash();

            doc.Editor.WriteMessage(
                svc.IsOffline
                    ? "\nIWC: Project loaded from cache (database unreachable).\n"
                    : "\nIWC: Project context refreshed.\n");
        }

        // -----------------------------------------------------------------------
        // IWC_CHANGEPROJECT — re-prompt without needing to open the palette
        // -----------------------------------------------------------------------

        [CommandMethod("IWC_CHANGEPROJECT")]
        public static void IwcChangeProject()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            ProjectContextService.GetOrCreate(doc).ChangeProject();
        }


        // -----------------------------------------------------------------------
        // IWCStartupArchiveProj — explicit legacy/archive association workflow
        // -----------------------------------------------------------------------

        [CommandMethod("IWCStartupArchiveProj")]
        public static void IwcStartupArchiveProj()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            DwgPropertyStore.EnsureAllKeysExist(doc);

            var svc = ProjectContextService.GetOrCreate(doc);
            svc.PromptForArchiveProject();

            doc.Editor.WriteMessage("\nIWC: Archive project/dash association workflow complete.\n");
        }
    }
}
