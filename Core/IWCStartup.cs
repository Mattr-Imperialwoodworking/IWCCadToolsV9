using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using IWCCadToolsV9.UI;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Core
{
    /// <summary>
    /// Entry point for the IWC CAD Tools add-in.
    /// Registered via IWCStartup AutoCAD command and the AcadNetAutoLoad.scr script.
    ///
    /// Startup sequence:
    ///   1. Connect to the IWC SQL database.
    ///   2. Ensure the active project is selected and loaded.
    ///   3. Ensure the active drawing has all required custom file properties.
    ///   4. Prompt for a Drawing Series (Dash) if one has not been assigned.
    /// </summary>
    public class IWCStartup
    {
        // ---------------------------------------------------------------------------
        // Static state (shared across the AutoCAD session)
        // ---------------------------------------------------------------------------

        public static IWCProj?  ActiveProject { get; private set; }
        public static IWCDash?  ActiveDash    { get; private set; }

        // ---------------------------------------------------------------------------
        // Main entry point
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCStartup")]
        public void Initialize()
        {
            using var conn = new IWCConn();
            conn.DBConnect();

            CheckProject(conn);
            CheckFileProps();
            CheckDash(conn);
        }

        // ---------------------------------------------------------------------------
        // Step 1 – Project
        // ---------------------------------------------------------------------------

        private void CheckProject(IWCConn conn)
        {
            // If PROJECTNAME is already set to a real value, the project is loaded.
            object? projectNameVar = Application.GetSystemVariable("ProjectName");
            string  projectName    = projectNameVar?.ToString() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(projectName) && projectName != "NA")
                return;   // already set

            // Recover project ID from AutoCAD integer variable USERI1
            int curProjId = System.Convert.ToInt32(Application.GetSystemVariable("USERI1"));

            // Show project picker if no project is active
            if (curProjId == 0)
            {
                using var picker = new ProjSelect();
                picker.ShowDialog();
                if (picker.SelectedProjectId.HasValue)
                    curProjId = picker.SelectedProjectId.Value;
            }

            if (curProjId == 0) return;

            ActiveProject = new IWCProj();
            ActiveProject.GetProject(curProjId, conn.OpenConn);

            // Persist identifiers for the session
            Application.SetSystemVariable("ProjectName", ActiveProject.ProjNum);
            Application.SetSystemVariable("USERI1",      ActiveProject.ProjID);
        }

        // ---------------------------------------------------------------------------
        // Step 2 – Custom file properties
        // ---------------------------------------------------------------------------

        private void CheckFileProps()
        {
            // Resolve the CSV path relative to the executing assembly
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string csvPath     = Path.Combine(assemblyDir, "CustomFileProps.csv");

            var props = CustomFileProps.LoadFromCsv(csvPath);

            // Ensure all keys exist (adds missing ones as "NA")
            AcadFilePropHelper.EnsurePropertiesExist(props.Keys);

            // Populate values from the project dataset
            if (ActiveProject != null)
                SetProjectFileProps(props);
        }

        private void SetProjectFileProps(System.Collections.Generic.Dictionary<string, string> props)
        {
            var updates = new System.Collections.Generic.Dictionary<string, string>();

            foreach (var kvp in props)
            {
                string key         = kvp.Key;
                string valueColumn = kvp.Value;
                string newValue    = "NA";

                if (!string.IsNullOrWhiteSpace(valueColumn)
                    && ActiveProject?.ProjRS?.Tables.Count > 0
                    && ActiveProject.ProjRS.Tables[0].Rows.Count > 0
                    && ActiveProject.ProjRS.Tables[0].Columns.Contains(valueColumn))
                {
                    newValue = ActiveProject.ProjRS.Tables[0].Rows[0][valueColumn]?.ToString() ?? "NA";
                }
                else
                {
                    newValue = key switch
                    {
                        "IWC_ProjNo"   => ActiveProject?.ProjNum  ?? "NA",
                        "IWC_ProjName" => ActiveProject?.ProjName ?? "NA",
                        "IWC_ID"       => ActiveProject?.ProjID.ToString() ?? "NA",
                        _              => "NA",
                    };
                }

                updates[key] = newValue;
            }

            AcadFilePropHelper.SetCustomProperties(updates);
        }

        // ---------------------------------------------------------------------------
        // Step 3 – Drawing series (Dash)
        // ---------------------------------------------------------------------------

        private void CheckDash(IWCConn conn)
        {
            // If the series is already assigned and is not the placeholder "NA", skip
            string? seriesNo = AcadFilePropHelper.GetCustomProperty("IWC_SeriesNo");
            if (!string.IsNullOrWhiteSpace(seriesNo) && seriesNo != "NA")
                return;

            int projId = ActiveProject?.ProjID ?? 0;
            if (projId == 0) return;

            using var dashPicker = new ProjDashSelect(projId);
            if (dashPicker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            if (!dashPicker.SelectedID.HasValue) return;

            ActiveDash = new IWCDash();
            ActiveDash.LoadByID(dashPicker.SelectedID.Value, conn.OpenConn);
        }
    }
}
