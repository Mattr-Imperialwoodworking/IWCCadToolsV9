using System.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.Data
{
    /// <summary>
    /// Holds the active IWC project loaded from dbo.Proj_Compile.
    /// </summary>
    public class IWCProj
    {
        public int    ProjID   { get; set; }
        public string ProjNum  { get; set; } = string.Empty;
        public string ProjName { get; set; } = string.Empty;

        /// <summary>Full result set from dbo.Proj_Compile for the loaded project row.</summary>
        public DataSet? ProjRS { get; set; }

        /// <summary>
        /// Loads project data by primary key from dbo.Proj_Compile.
        /// All properties are reset regardless of whether a row is found.
        /// </summary>
        public void GetProject(int id, SqlConnection dbConn)
        {
            using var adapter = new SqlDataAdapter(
                "SELECT * FROM dbo.Proj_Compile WHERE ID = @id", dbConn);
            adapter.SelectCommand!.Parameters.AddWithValue("@id", id);

            var ds = new DataSet();
            adapter.Fill(ds);

            ProjID  = id;
            ProjRS  = ds;

            if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            {
                var row = ds.Tables[0].Rows[0];
                ProjNum  = row["IDNum"]?.ToString()     ?? string.Empty;
                ProjName = row["Proj_Name"]?.ToString() ?? string.Empty;
            }
            else
            {
                ProjNum  = string.Empty;
                ProjName = string.Empty;
            }
        }
    }
}
