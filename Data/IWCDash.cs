using System;
using System.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.Data
{
    /// <summary>
    /// Holds the active drawing series (Dash) loaded from dbo.Proj_DashCompileReportActive.
    /// After loading, call <see cref="ApplyToDrawing"/> to persist the values as DWG
    /// custom file properties via the shared <see cref="Helpers.AcadFilePropHelper"/> helper.
    /// </summary>
    public class IWCDash
    {
        public int    ID        { get; set; }
        public string ID_Num    { get; set; } = string.Empty;
        public string Dash_Desc { get; set; } = string.Empty;
        public int    Proj_ID   { get; set; }

        /// <summary>The raw data row returned by the query.</summary>
        public DataRow? DashRecord { get; set; }

        // ---------------------------------------------------------------------------
        // Load
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Loads a Dash record by its primary key and writes the three
        /// IWC_Series* custom file properties to the active drawing.
        /// </summary>
        public void LoadByID(int dashId, SqlConnection dbConn)
        {
            using var cmd = new SqlCommand(
                "SELECT * FROM dbo.Proj_DashCompileReportActive WHERE DashID = @ID", dbConn);
            cmd.Parameters.AddWithValue("@ID", dashId);

            var dt = new DataTable();
            using (var adapter = new SqlDataAdapter(cmd))
                adapter.Fill(dt);

            if (dt.Rows.Count > 0)
            {
                var row = dt.Rows[0];

                ID        = SafeInt(row, "DashID");
                ID_Num    = SafeStr(row, "Dash_Num");
                Dash_Desc = SafeStr(row, "Dash_Desc");
                Proj_ID   = SafeInt(row, "Proj_ID");
                DashRecord = row;

                ApplyToDrawing();
            }
            else
            {
                ID = 0; ID_Num = string.Empty; Dash_Desc = string.Empty;
                Proj_ID = 0; DashRecord = null;
            }
        }

        // ---------------------------------------------------------------------------
        // Write series properties to the active DWG
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Writes IWC_SeriesID, IWC_SeriesNo, and IWC_SeriesName to the active
        /// drawing's custom file properties using the shared helper.
        /// </summary>
        public void ApplyToDrawing()
        {
            try
            {
                Helpers.AcadFilePropHelper.SetCustomProperty("IWC_SeriesID",   ID.ToString());
                Helpers.AcadFilePropHelper.SetCustomProperty("IWC_SeriesNo",   ID_Num);
                Helpers.AcadFilePropHelper.SetCustomProperty("IWC_SeriesName", Dash_Desc);
            }
            catch (Exception ex)
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog(
                    "Error setting Dash file properties: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static int SafeInt(DataRow row, string col)
            => row.Table.Columns.Contains(col) && row[col] != DBNull.Value
               ? Convert.ToInt32(row[col]) : 0;

        private static string SafeStr(DataRow row, string col)
            => row.Table.Columns.Contains(col) && row[col] != DBNull.Value
               ? row[col].ToString() ?? string.Empty : string.Empty;
    }
}
