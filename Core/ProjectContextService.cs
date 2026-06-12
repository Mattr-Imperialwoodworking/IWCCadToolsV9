using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Data.Models;
using IWCCadToolsV9.Helpers;
using IWCCadToolsV9.UI;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Core
{
    /// <summary>
    /// Per-document project context hub.
    ///
    /// One instance per open AutoCAD document, stored in Document.UserData.
    /// Replaces the static IWCStartup.ActiveProject / ActiveDash properties,
    /// which caused cross-document contamination in MDI sessions.
    ///
    /// Usage:
    ///   var svc = ProjectContextService.GetOrCreate(doc);
    ///   await svc.LoadAsync();
    ///
    /// Subscribe to ProjectLoaded to be notified when data is ready:
    ///   svc.ProjectLoaded += OnProjectLoaded;
    /// </summary>
    public class ProjectContextService
    {
        // -----------------------------------------------------------------------
        // UserData key — must be unique within the document's UserData dictionary
        // -----------------------------------------------------------------------

        private const string UserDataKey = "IWC_ProjectContext_v1";

        // -----------------------------------------------------------------------
        // Shared repository (one per process is fine — stateless)
        // -----------------------------------------------------------------------

        private static readonly IWCProjRepository _repo = new();

        // -----------------------------------------------------------------------
        // Per-document state
        // -----------------------------------------------------------------------

        private readonly Document _doc;

        /// <summary>The AutoCAD document this context is bound to.</summary>
        public Document Document => _doc;

        public ProjectRecord?              Project   { get; private set; }
        public DashRecord?                 Dash      { get; private set; }
        public IReadOnlyList<DashRecord>   Dashes    { get; private set; } = Array.Empty<DashRecord>();
        public IReadOnlyList<MaterialRecord> Materials { get; private set; } = Array.Empty<MaterialRecord>();
        public IReadOnlyList<HardwareRecord> Hardware  { get; private set; } = Array.Empty<HardwareRecord>();

        public bool HasProject => Project?.IsValid == true;
        public bool HasDash    => Dash?.IsValid     == true;

        /// <summary>
        /// True when data was loaded from the DWG cache because the database
        /// was unreachable. Callers can use this to show an offline indicator.
        /// </summary>
        public bool IsOffline { get; private set; }

        // -----------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------

        /// <summary>
        /// Raised after a successful project load (from DB or cache).
        /// UI controls subscribe here instead of the legacy static
        /// CtlIWCProj.ProjectChanged event.
        /// </summary>
        public event EventHandler? ProjectLoaded;

        // -----------------------------------------------------------------------
        // Factory
        // -----------------------------------------------------------------------

        private ProjectContextService(Document doc) => _doc = doc;

        /// <summary>
        /// Returns the ProjectContextService for <paramref name="doc"/>,
        /// creating one if this is the first call for that document.
        /// Safe to call from any thread — Document.UserData is per-document.
        /// </summary>
        public static ProjectContextService GetOrCreate(Document doc)
        {
            if (doc.UserData[UserDataKey] is not ProjectContextService svc)
            {
                svc = new ProjectContextService(doc);
                doc.UserData[UserDataKey] = svc;
            }
            return svc;
        }

        /// <summary>
        /// Returns the service for the currently active document, or null if
        /// no document is active.
        /// </summary>
        public static ProjectContextService? ForActiveDocument()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            return doc == null ? null : GetOrCreate(doc);
        }

        // -----------------------------------------------------------------------
        // Load sequence
        // -----------------------------------------------------------------------

        /// <summary>
        /// Main load entry point. Called by DrawingLifecycleHandler on open/activate.
        ///
        /// Strategy:
        ///   1. Read IWC_ID from DWG custom file properties.
        ///   2. If found, query the database.
        ///   3. If the database is unreachable, serve from the JSON cache embedded
        ///      in IWC_CachedJson custom property.
        ///   4. If no IWC_ID exists, do nothing — PromptForProject() handles that.
        /// </summary>
        public async Task LoadAsync()
        {
            string? idStr = AcadFilePropHelper.GetCustomProperty("IWC_ID");
            if (!int.TryParse(idStr, out int projId) || projId <= 0)
                return;   // No project linked yet — PromptForProject() handles new drawings

            string? dashIdStr = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            int.TryParse(dashIdStr, out int dashId);

            await LoadProjectDataAsync(projId, dashId > 0 ? dashId : null);
        }

        // Sync wrapper for legacy call sites
        public void Load() => LoadAsync().GetAwaiter().GetResult();

        /// <summary>
        /// Loads project (and optionally dash) data by ID.
        /// Falls back to embedded DWG cache if the database is unreachable.
        /// </summary>
        private async Task LoadProjectDataAsync(int projectId, int? dashId)
        {
            try
            {
                Project   = await _repo.GetProjectByIdAsync(projectId);
                // When loading a drawing that is already associated by DWG custom properties,
                // include inactive/archived dash records so old project files still resolve.
                // The project/dash selection dialog still uses GetDashesForProjectAsync(),
                // which is limited to active project/dash records.
                Dashes    = await _repo.GetAllDashesForProjectAsync(projectId);
                Materials = await _repo.GetMaterialsAsync(projectId);
                Hardware  = await _repo.GetHardwareAsync(projectId);

                if (dashId.HasValue && dashId > 0)
                    Dash = await _repo.GetDashByIdAsync(dashId.Value);

                IsOffline = false;
            }
            catch (Exception)
            {
                // Database unreachable — serve from DWG cache
                LoadFromCache();
                IsOffline = true;
            }

            if (HasProject)
                RaiseProjectLoaded();
        }

        // -----------------------------------------------------------------------
        // Project / Dash selector prompts
        // -----------------------------------------------------------------------

        /// <summary>
        /// Shows the project + dash selector dialog.
        /// On OK: loads project data and persists IDs to DWG properties.
        /// Must be called on the main AutoCAD thread.
        /// </summary>
        public void PromptForProject()
        {
            using var picker = new FrmProjectSelector();
            // ShowModalDialog correctly parents the form to the AutoCAD main window
            // so it appears on top and isn't hidden behind the application frame.
            var result = Application.ShowModalDialog(
                Application.MainWindow.Handle, picker, false);

            if (result != System.Windows.Forms.DialogResult.OK) return;
            if (!picker.SelectedProjectId.HasValue) return;

            int projId  = picker.SelectedProjectId.Value;
            int? dashId = picker.SelectedDashId;

            // Persist IDs immediately so the drawing is associated even if the
            // user closes/crashes before the full data sync completes.
            DwgPropertyStore.WriteProjectIds(_doc, projId, dashId);

            // IMPORTANT: Do not block the AutoCAD/WinForms UI thread here with
            // LoadProjectDataAsync(...).GetAwaiter().GetResult(). The project
            // selector already has the selected active project/dash records in
            // memory. Use those records to write the full DWG properties
            // immediately, then refresh the rest of the context in the
            // background. This avoids the hang seen immediately after selecting
            // the dash from the dialog.
            if (picker.SelectedProject != null)
            {
                Project = picker.SelectedProject;
                Dash = picker.SelectedDash;
                IsOffline = false;

                PersistToDwg();
                RaiseProjectLoaded();
            }

            // Refresh the full project context asynchronously for subscribers
            // that need lists such as Materials/Hardware/Dashes. This should not
            // be required for the DWG custom property sync above.
            _ = LoadProjectDataAsync(projId, dashId);
        }

        /// <summary>
        /// Shows the archive project + dash selector dialog.
        /// This is intentionally separate from PromptForProject().
        /// PromptForProject() remains active-project only; this method loads from
        /// dbo.Proj_Compile and dbo.Proj_DashCompileReport for legacy archived drawings.
        /// </summary>
        public void PromptForArchiveProject()
        {
            using var picker = new FrmProjectArchiveSelector();
            var result = Application.ShowModalDialog(
                Application.MainWindow.Handle, picker, false);

            if (result != System.Windows.Forms.DialogResult.OK) return;
            if (!picker.SelectedProjectId.HasValue) return;

            int projId = picker.SelectedProjectId.Value;
            int? dashId = picker.SelectedDashId;

            // Persist to DWG immediately so archived/legacy associations are saved
            // the same way as normal active-project associations.
            DwgPropertyStore.WriteProjectIds(_doc, projId, dashId);

            // This load path already resolves through dbo.Proj_Compile and
            // dbo.Proj_DashCompileReport, so inactive/archived records remain available.
            _ = LoadProjectDataAsync(projId, dashId);

            if (dashId.HasValue && dashId.Value > 0)
            {
                DrawingSeriesService.ReviewLoggedSheetsAfterProjectDashChange(
                    _doc, null, projId, dashId.Value);
            }
        }

        /// <summary>
        /// Shows only the dash selector for the current project.
        /// Used when a project is already linked but no dash has been chosen.
        /// </summary>
        public void PromptForDash()
        {
            if (!HasProject) return;

            using var picker = new ProjDashSelect(Project!.Id);
            var result = Application.ShowModalDialog(
                Application.MainWindow.Handle, picker, false);

            if (result != System.Windows.Forms.DialogResult.OK) return;
            if (!picker.SelectedID.HasValue) return;

            int dashId = picker.SelectedID.Value;
            DwgPropertyStore.WriteDashId(_doc, dashId);

            Dash = _repo.GetDashById(dashId);
            RaiseProjectLoaded();
        }


        private static int? ReadProjectIdFromDwg()
        {
            string? idStr = AcadFilePropHelper.GetCustomProperty("IWC_ID");
            return int.TryParse(idStr, out int id) && id > 0 ? id : null;
        }

        private static int? ReadDashIdFromDwg()
        {
            string? idStr = AcadFilePropHelper.GetCustomProperty("IWC_SeriesID");
            return int.TryParse(idStr, out int id) && id > 0 ? id : null;
        }

        // -----------------------------------------------------------------------
        // Change project
        // -----------------------------------------------------------------------

        /// <summary>
        /// Resets project state and re-prompts. Equivalent to the old
        /// CtlIWCProj.btnChangeProject_Click workflow.
        /// </summary>
        public void ChangeProject()
        {
            int? previousProjectId = ReadProjectIdFromDwg();
            int? previousDashId = ReadDashIdFromDwg();

            Project   = null;
            Dash      = null;
            Dashes    = Array.Empty<DashRecord>();
            Materials = Array.Empty<MaterialRecord>();
            Hardware  = Array.Empty<HardwareRecord>();

            DwgPropertyStore.ClearProjectIds(_doc);
            PromptForProject();

            int? newProjectId = ReadProjectIdFromDwg();
            int? newDashId = ReadDashIdFromDwg();
            if (newProjectId.HasValue && newDashId.HasValue &&
                (previousProjectId != newProjectId || previousDashId != newDashId))
            {
                DrawingSeriesService.ReviewLoggedSheetsAfterProjectDashChange(
                    _doc, previousDashId, newProjectId.Value, newDashId.Value);
            }
        }

        // -----------------------------------------------------------------------
        // BeforeSave sync
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called by the BeforeSave event handler. Writes current project data
        /// back to DWG custom properties so the file is always self-describing.
        /// </summary>
        public void PersistToDwg()
        {
            if (!HasProject) return;
            DwgPropertyStore.SyncFromProject(_doc, Project!, Dash);
        }

// PATCH — add this method to ProjectContextService.cs
// inside the ProjectContextService class body,
// below the PersistToDwg() method.

// This method is called by DrawingLifecycleHandler.OnDocumentActivated
// to re-notify UI subscribers when the user switches to an already-loaded document,
// without triggering a new database round-trip.

/// <summary>
/// Re-raises ProjectLoaded for the current document context.
/// Called by DrawingLifecycleHandler on DocumentActivated so subscribed
/// UI controls (CtlIWCProj, ctlIWCProjNav) rebind without a DB query.
/// Always marshals to the WPF UI Dispatcher so subscribers never need
/// to check InvokeRequired or Dispatcher.CheckAccess themselves.
/// </summary>
internal void RaiseProjectLoaded()
{
    var dispatcher = System.Windows.Application.Current?.Dispatcher;
    if (dispatcher != null && !dispatcher.CheckAccess())
    {
        // We are on a background thread — post to the UI dispatcher and return
        // immediately so we don't block the caller.
        dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() => ProjectLoaded?.Invoke(this, EventArgs.Empty)));
    }
    else
    {
        // Already on the UI thread (or no WPF Application — e.g. unit tests).
        ProjectLoaded?.Invoke(this, EventArgs.Empty);
    }
}

    
        // -----------------------------------------------------------------------
        // Offline cache (DWG-embedded JSON)
        // -----------------------------------------------------------------------

        private void LoadFromCache()
        {
            // The cache is written by DwgPropertyStore.SyncFromProject as a JSON
            // blob in IWC_CachedJson. If present, deserialize the project record.
            // Materials, hardware, and dash list are NOT cached (too large) —
            // offline sessions show project header info only.
            string? json = AcadFilePropHelper.GetCustomProperty("IWC_CachedProjectJson");
            if (string.IsNullOrWhiteSpace(json)) return;

            try
            {
                var cached = System.Text.Json.JsonSerializer.Deserialize<ProjectRecord>(json);
                if (cached != null)
                    Project = cached;
            }
            catch { /* corrupt cache — project stays null */ }
        }
    }
}
