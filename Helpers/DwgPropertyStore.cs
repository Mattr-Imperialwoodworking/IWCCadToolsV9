using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using IWCCadToolsV9.Data.Models;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Project-aware wrapper around AcadFilePropHelper.
    /// Knows the mapping from ProjectRecord / DashRecord properties
    /// to DWG custom file property keys.
    ///
    /// Replaces the SetProjectFileProps() switch-statement in IWCStartup
    /// and the ApplyToDrawing() method on IWCDash.
    ///
    /// All keys defined here must match CustomFileProps.csv exactly.
    /// </summary>
    public static class DwgPropertyStore
    {
        // -----------------------------------------------------------------------
        // DWG property key constants
        // -----------------------------------------------------------------------

        public const string KeyProjectId   = "IWC_ID";
        public const string KeyProjNum     = "IWC_ProjNo";
        public const string KeyProjName    = "IWC_ProjName";
        public const string KeyArchitect   = "IWC_Architect";
        public const string KeyContractor  = "IWC_Contractor";
        public const string KeyPMIni       = "IWC_PMINI";
        public const string KeyDashId      = "IWC_SeriesID";
        public const string KeyDashNum     = "IWC_SeriesNo";
        public const string KeyDashName    = "IWC_SeriesName";
        public const string KeyDraftBy     = "IWC_Draft";
        public const string KeyDate        = "IWC_Date";
        public const string KeyCachedJson  = "IWC_CachedProjectJson";

        // -----------------------------------------------------------------------
        // Full sync — called on BeforeSave and after project load
        // -----------------------------------------------------------------------

        /// <summary>
        /// Writes all project and dash fields to DWG custom properties,
        /// and caches a JSON snapshot for offline use.
        /// </summary>
        public static void SyncFromProject(Document doc, ProjectRecord project, DashRecord? dash)
        {
            var values = new Dictionary<string, string>
            {
                [KeyProjectId]  = project.Id.ToString(),
                [KeyProjNum]    = project.IdNum,
                [KeyProjName]   = project.Name,
                [KeyArchitect]  = project.ArchTb,      // titleblock-formatted name
                [KeyContractor] = project.ContTb,      // titleblock-formatted name
                [KeyPMIni]      = project.PMIni,
            };

            if (dash != null)
            {
                values[KeyDashId]  = dash.DashId.ToString();
                values[KeyDashNum] = dash.DashNum;
                values[KeyDashName]= dash.DashDesc;
            }

            // JSON cache for offline open
            try
            {
                var json = JsonSerializer.Serialize(project);
                values[KeyCachedJson] = json;
            }
            catch { /* non-fatal — cache will just be missing */ }

            using (doc.LockDocument())
            {
                AcadFilePropHelper.SetCustomProperties(values);
            }

            // Keep PROJECTNAME system variable in sync for AutoCAD sheet sets etc.
            try
            {
                Autodesk.AutoCAD.ApplicationServices.Application
                    .SetSystemVariable("PROJECTNAME", project.IdNum);
            }
            catch { /* best-effort — not all contexts allow sysvar writes */ }
        }

        // -----------------------------------------------------------------------
        // Targeted writes — used during initial selection before full load
        // -----------------------------------------------------------------------

        /// <summary>
        /// Writes only the project and dash IDs — called immediately after
        /// the user makes a selection so the IDs survive a crash before BeforeSave.
        /// </summary>
        public static void WriteProjectIds(Document doc, int projectId, int? dashId)
        {
            var values = new Dictionary<string, string>
            {
                [KeyProjectId] = projectId.ToString(),
            };
            if (dashId.HasValue && dashId > 0)
                values[KeyDashId] = dashId.Value.ToString();

            using (doc.LockDocument())
            {
                AcadFilePropHelper.SetCustomProperties(values);
            }
        }

        /// <summary>Writes only the dash ID — used when selecting a dash after project load.</summary>
        public static void WriteDashId(Document doc, int dashId)
        {
            using (doc.LockDocument())
            {
                AcadFilePropHelper.SetCustomProperty(KeyDashId, dashId.ToString());
            }
        }

        /// <summary>
        /// Resets all IWC project properties to "NA" — called by ChangeProject().
        /// </summary>
        public static void ClearProjectIds(Document doc)
        {
            var values = new Dictionary<string, string>
            {
                [KeyProjectId]  = "NA",
                [KeyProjNum]    = "NA",
                [KeyProjName]   = "NA",
                [KeyArchitect]  = "NA",
                [KeyContractor] = "NA",
                [KeyPMIni]      = "NA",
                [KeyDashId]     = "NA",
                [KeyDashNum]    = "NA",
                [KeyDashName]   = "NA",
                [KeyCachedJson] = "NA",
            };

            using (doc.LockDocument())
            {
                AcadFilePropHelper.SetCustomProperties(values);
            }

            try
            {
                Autodesk.AutoCAD.ApplicationServices.Application
                    .SetSystemVariable("PROJECTNAME", "NA");
            }
            catch { }
        }

        // -----------------------------------------------------------------------
        // Ensure all keys exist (called once on new drawing)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Adds any missing IWC property keys with default value "NA".
        /// Safe to call multiple times — existing values are not overwritten.
        /// </summary>
        public static void EnsureAllKeysExist(Document doc)
        {
            var keys = new[]
            {
                KeyProjectId, KeyProjNum, KeyProjName,
                KeyArchitect, KeyContractor, KeyPMIni,
                KeyDashId, KeyDashNum, KeyDashName,
                KeyDraftBy, KeyDate, KeyCachedJson,
            };

            using (doc.LockDocument())
            {
                AcadFilePropHelper.EnsurePropertiesExist(keys);
            }
        }
    }
}
