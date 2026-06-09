using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using IWCCadToolsV9.Data.Models;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.Data
{
    /// <summary>
    /// Single data-access class for all IWC project queries.
    ///
    /// Rules:
    ///   - Never holds a SqlConnection as a field — all connections are opened,
    ///     used, and disposed within each method.
    ///   - All connections come from IWCConn.GetSqlConnection() — never new SqlConnection().
    ///   - Returns typed model records, never DataSet / DataRow / DataTable.
    ///   - Async by default; sync wrappers available for legacy callers.
    /// </summary>
    public class IWCProjRepository
    {
        // ============================================================
        // PROJECT queries — dbo.Proj_CompileActive / Proj_Compile
        // ============================================================

        /// <summary>
        /// Returns all active projects for the project picker list.
        /// Queries dbo.Proj_CompileActive ordered by IDNum ascending.
        /// </summary>
        public async Task<IReadOnlyList<ProjectRecord>> GetActiveProjectsAsync()
        {
            const string sql = @"
                SELECT ID, IDNum, Proj_Name,
                       Architect, Contractor, PM, PMINI,
                       Architect_TBName AS ArchTb, Contractor_TBName AS ContTb,
                       Proj_StartDate, Proj_EstComp, Proj_EstProduction,
                       Proj_EstInstall, Proj_DateActualComplete,
                       Act_Drafting, Act_Shop,
                       LEED, FSC, NAUF, VOC,
                       Proj_Color, SharepointID, Date_Modified
                FROM dbo.Proj_CompileActive
                ORDER BY IDNum ASC";

            var results = new List<ProjectRecord>();

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
                results.Add(MapProject(rdr));

            return results;
        }



        /// <summary>
        /// Returns all projects for the archive project picker list, including inactive/completed/archived projects.
        /// Queries dbo.Proj_Compile ordered by IDNum ascending.
        /// Use this only from explicit archive/legacy association workflows; the normal selector must remain active-only.
        /// </summary>
        public async Task<IReadOnlyList<ProjectRecord>> GetAllProjectsAsync()
        {
            const string sql = @"
                SELECT ID, IDNum, Proj_Name,
                       Architect, Contractor, PM, PMINI,
                       ArchTb, ContTb,
                       Proj_StartDate, Proj_EstComp, Proj_EstProduction,
                       Proj_EstInstall, Proj_DateActualComplete,
                       Act_Drafting, Act_Shop,
                       LEED, FSC, NAUF, VOC,
                       Proj_Color, SharepointID, Date_Modified
                FROM dbo.Proj_Compile
                ORDER BY IDNum ASC";

            var results = new List<ProjectRecord>();

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
                results.Add(MapProject(rdr));

            return results;
        }

        /// <summary>
        /// Returns a single project by primary key.
        /// Queries dbo.Proj_Compile (includes completed projects).
        /// Returns null if not found.
        /// </summary>
        public async Task<ProjectRecord?> GetProjectByIdAsync(int projectId)
        {
            const string sql = @"
                SELECT ID, IDNum, Proj_Name,
                       Architect, Contractor, PM, PMINI,
                       ArchTb, ContTb,
                       Proj_StartDate, Proj_EstComp, Proj_EstProduction,
                       Proj_EstInstall, Proj_DateActualComplete,
                       Act_Drafting, Act_Shop,
                       LEED, FSC, NAUF, VOC,
                       Proj_Color, SharepointID, Date_Modified
                FROM dbo.Proj_Compile
                WHERE ID = @id";

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", projectId);
            using var rdr = await cmd.ExecuteReaderAsync();

            return await rdr.ReadAsync() ? MapProject(rdr) : null;
        }

        // Sync wrapper for legacy callers (ProjSelect_Load, CtlIWCProj, etc.)
        public ProjectRecord? GetProjectById(int projectId)
            => GetProjectByIdAsync(projectId).GetAwaiter().GetResult();

        public IReadOnlyList<ProjectRecord> GetActiveProjects()
            => GetActiveProjectsAsync().GetAwaiter().GetResult();

        public IReadOnlyList<ProjectRecord> GetAllProjects()
            => GetAllProjectsAsync().GetAwaiter().GetResult();

        // ============================================================
        // DASH queries — dbo.Proj_DashCompileReportActive / Proj_DashCompile
        // ============================================================

        /// <summary>
        /// Returns all active dashes for a project, ordered by Dash_Num.
        /// Queries dbo.Proj_DashCompileReportActive.
        /// Used by ProjDashSelect and the project browser dash list.
        /// </summary>
        public async Task<IReadOnlyList<DashRecord>> GetDashesForProjectAsync(int projectId)
        {
            const string sql = @"
                SELECT DashID, Proj_ID, IDNum,
                       Dash_Num, Dash_Desc, Dash_Dwg,
                       Dash_Type, Dash_Parent, Dash_Phase,
                       Act, Act_Draft, Act_Shop, Act_Void,
                       Dash_DwgPriority,
                       PMName, CADIni, MfrName, MfrIni,
                       Date_TargetSubmit, Date_ActualSubmit,
                       Date_TargetApprove, Date_ActualApprove,
                       Date_TargetRLSMfr, Date_ActualRlsMfr,
                       Date_TargetShip, Date_ActualShip,
                       Date_DashUpdate
                FROM dbo.Proj_DashCompileReportActive
                WHERE Proj_ID = @projId
                  AND (Act_Void = 0 OR Act_Void IS NULL)
                ORDER BY TRY_CAST(Dash_Num AS int), Dash_Num";

            var results = new List<DashRecord>();

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projId", projectId);
            using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
                results.Add(MapDash(rdr));

            return results;
        }

        /// <summary>
        /// Returns all dashes for a project, including inactive/archived dashes.
        /// Queries dbo.Proj_DashCompileReport.
        /// Used when loading an already-associated drawing from DWG custom properties,
        /// so old project files continue to resolve even after the project/dash is no longer active.
        /// Do not use this method for the project/dash association picker.
        /// </summary>
        public async Task<IReadOnlyList<DashRecord>> GetAllDashesForProjectAsync(int projectId)
        {
            const string sql = @"
                SELECT DashID, Proj_ID, IDNum,
                       Dash_Num, Dash_Desc, Dash_Dwg,
                       Dash_Type, Dash_Parent, Dash_Phase,
                       Act, Act_Draft, Act_Shop, Act_Void,
                       Dash_DwgPriority,
                       PMName, CADIni, MfrName, MfrIni,
                       Date_TargetSubmit, Date_ActualSubmit,
                       Date_TargetApprove, Date_ActualApprove,
                       Date_TargetRLSMfr, Date_ActualRlsMfr,
                       Date_TargetShip, Date_ActualShip,
                       Date_DashUpdate
                FROM dbo.Proj_DashCompileReport
                WHERE Proj_ID = @projId
                ORDER BY TRY_CAST(Dash_Num AS int), Dash_Num";

            var results = new List<DashRecord>();

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projId", projectId);
            using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
                results.Add(MapDash(rdr));

            return results;
        }

        /// <summary>
        /// Returns a single dash by its DashID primary key, including inactive/archived dashes.
        /// NOTE: Proj_DashCompileReport uses DashID — not ID.
        /// Returns null if not found.
        /// </summary>
        public async Task<DashRecord?> GetDashByIdAsync(int dashId)
        {
            const string sql = @"
                SELECT DashID, Proj_ID, IDNum,
                       Dash_Num, Dash_Desc, Dash_Dwg,
                       Dash_Type, Dash_Parent, Dash_Phase,
                       Act, Act_Draft, Act_Shop, Act_Void,
                       Dash_DwgPriority,
                       PMName, CADIni, MfrName, MfrIni,
                       Date_TargetSubmit, Date_ActualSubmit,
                       Date_TargetApprove, Date_ActualApprove,
                       Date_TargetRLSMfr, Date_ActualRlsMfr,
                       Date_TargetShip, Date_ActualShip,
                       Date_DashUpdate
                FROM dbo.Proj_DashCompileReport
                WHERE DashID = @dashId";

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@dashId", dashId);
            using var rdr = await cmd.ExecuteReaderAsync();

            return await rdr.ReadAsync() ? MapDash(rdr) : null;
        }

        /// <summary>
        /// Returns the full dash list for a project including parent/child hierarchy.
        /// Queries dbo.Proj_DashCompile which includes Dash_ParentNum, Dash_ParentDesc.
        /// Used by ctlIWCProjNav tree builder.
        /// </summary>
        public async Task<IReadOnlyList<DashRecord>> GetDashHierarchyForProjectAsync(int projectId)
        {
            const string sql = @"
                SELECT DashID, Proj_ID, Proj_IDNum AS IDNum,
                       Dash_Num, Dash_Desc, NULL AS Dash_Dwg,
                       Dash_TypeID AS Dash_Type, Dash_Parent, Dash_Phase,
                       Act, NULL AS Act_Draft, NULL AS Act_Shop, Act_Void,
                       NULL AS Dash_DwgPriority,
                       NULL AS PMName, UserIni AS CADIni, MfrName, MfrIni,
                       Date_TargetSubmit, Date_ActualSubmit,
                       Date_TargetApprove, Date_ActualApprove,
                       Date_TargetRLSMfr, Date_ActualRlsMfr,
                       Date_TargetShip, Date_ActualShip,
                       NULL AS Date_DashUpdate
                FROM dbo.Proj_DashCompile
                WHERE Proj_ID = @projId
                  AND (Act_Void = 0 OR Act_Void IS NULL)
                ORDER BY TRY_CAST(Dash_Num AS int), Dash_Num";

            var results = new List<DashRecord>();

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projId", projectId);
            using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
                results.Add(MapDash(rdr));

            return results;
        }

        // Sync wrappers
        public IReadOnlyList<DashRecord> GetDashesForProject(int projectId)
            => GetDashesForProjectAsync(projectId).GetAwaiter().GetResult();

        public IReadOnlyList<DashRecord> GetAllDashesForProject(int projectId)
            => GetAllDashesForProjectAsync(projectId).GetAwaiter().GetResult();

        public DashRecord? GetDashById(int dashId)
            => GetDashByIdAsync(dashId).GetAwaiter().GetResult();

        // ============================================================
        // MATERIALS queries — dbo.Proj_MatDash_Compile
        // NOTE: This view uses ProjID (no underscore)
        // ============================================================

        /// <summary>
        /// Returns all materials for a project, optionally filtered to a specific dash.
        /// </summary>
        public async Task<IReadOnlyList<MaterialRecord>> GetMaterialsAsync(
            int projectId, int? dashId = null)
        {
            // NOTE: Proj_MatDash_Compile uses ProjID (no underscore) — not Proj_ID
            var sql = dashId.HasValue
                ? @"SELECT MatNo, MatDesc, ID, MatEdit, MatNotes, MatApprove,
                           MatUnits, WdSpecies, WdCut, WdMatch,
                           FinishType, FinishPore, FinishColor, FinishSheen, FinishNotes,
                           ItemUpdate, ItemUpdateBy, MatID, DashID, MatQty,
                           ID_Num, Dash_Desc, MatGroup, ProjID
                    FROM dbo.Proj_MatDash_Compile
                    WHERE ProjID = @projId AND DashID = @dashId
                    ORDER BY MatGroup, MatNo"
                : @"SELECT MatNo, MatDesc, ID, MatEdit, MatNotes, MatApprove,
                           MatUnits, WdSpecies, WdCut, WdMatch,
                           FinishType, FinishPore, FinishColor, FinishSheen, FinishNotes,
                           ItemUpdate, ItemUpdateBy, MatID, DashID, MatQty,
                           ID_Num, Dash_Desc, MatGroup, ProjID
                    FROM dbo.Proj_MatDash_Compile
                    WHERE ProjID = @projId
                    ORDER BY MatGroup, MatNo";

            var results = new List<MaterialRecord>();

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projId", projectId);
            if (dashId.HasValue)
                cmd.Parameters.AddWithValue("@dashId", dashId.Value);

            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                results.Add(MapMaterial(rdr));

            return results;
        }

        // Sync wrapper
        public IReadOnlyList<MaterialRecord> GetMaterials(int projectId, int? dashId = null)
            => GetMaterialsAsync(projectId, dashId).GetAwaiter().GetResult();

        // ============================================================
        // HARDWARE queries — dbo.Proj_Hdw
        // ============================================================

        /// <summary>
        /// Returns all non-voided hardware for a project.
        /// </summary>
        public async Task<IReadOnlyList<HardwareRecord>> GetHardwareAsync(int projectId)
        {
            const string sql = @"
                SELECT ID, Proj_ID, HdwNo, HdwDesc, HdwGroup,
                       HdwEdit, HdwNotes, HdwApprove, HdwVoid, HdwByIWC,
                       HdwIWCLibraryID, HdwVendorID, HdwUnit, HdwVendorNum, HdwVendorlink
                FROM dbo.Proj_Hdw
                WHERE Proj_ID = @projId
                  AND (HdwVoid = 0 OR HdwVoid IS NULL)
                ORDER BY HdwGroup, HdwNo";

            var results = new List<HardwareRecord>();

            using var conn = IWCConn.GetSqlConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projId", projectId);
            using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
                results.Add(MapHardware(rdr));

            return results;
        }

        // Sync wrapper
        public IReadOnlyList<HardwareRecord> GetHardware(int projectId)
            => GetHardwareAsync(projectId).GetAwaiter().GetResult();

        // ============================================================
        // MAPPERS — reader → record
        // ============================================================

        private static ProjectRecord MapProject(SqlDataReader r) => new()
        {
            Id          = r.GetInt32(r.GetOrdinal("ID")),
            IdNum       = Safe(r, "IDNum"),
            Name        = Safe(r, "Proj_Name"),
            Architect   = Safe(r, "Architect"),
            Contractor  = Safe(r, "Contractor"),
            PM          = Safe(r, "PM"),
            PMIni       = Safe(r, "PMINI"),
            ArchTb      = Safe(r, "ArchTb"),
            ContTb      = Safe(r, "ContTb"),
            StartDate         = SafeDate(r, "Proj_StartDate"),
            EstimatedComp     = SafeDate(r, "Proj_EstComp"),
            EstProduction     = SafeDate(r, "Proj_EstProduction"),
            EstInstall        = SafeDate(r, "Proj_EstInstall"),
            ActualComplete    = SafeDate(r, "Proj_DateActualComplete"),
            IsActiveDrafting  = SafeBool(r, "Act_Drafting"),
            IsActiveShop      = SafeBool(r, "Act_Shop"),
            IsLEED  = SafeBool(r, "LEED"),
            IsFSC   = SafeBool(r, "FSC"),
            IsNAUF  = SafeBool(r, "NAUF"),
            IsVOC   = SafeBool(r, "VOC"),
            Color        = Safe(r, "Proj_Color"),
            SharepointId = Safe(r, "SharepointID"),
        };

        private static DashRecord MapDash(SqlDataReader r) => new()
        {
            DashId       = SafeInt(r, "DashID"),
            ProjectId    = SafeInt(r, "Proj_ID"),
            ProjectIdNum = Safe(r, "IDNum"),
            DashNum      = Safe(r, "Dash_Num"),
            DashDesc     = Safe(r, "Dash_Desc"),
            DashDwg      = Safe(r, "Dash_Dwg"),
            DashType     = SafeInt(r, "Dash_Type"),
            DashParent   = SafeInt(r, "Dash_Parent"),
            DashPhase    = SafeInt(r, "Dash_Phase"),
            IsActive      = SafeBool(r, "Act"),
            IsActiveDraft = SafeBool(r, "Act_Draft"),
            IsActiveShop  = SafeBool(r, "Act_Shop"),
            IsVoid        = SafeBool(r, "Act_Void"),
            DwgPriority   = SafeInt(r, "Dash_DwgPriority"),
            PMName  = Safe(r, "PMName"),
            CADIni  = Safe(r, "CADIni"),
            MfrName = Safe(r, "MfrName"),
            MfrIni  = Safe(r, "MfrIni"),
            DateTargetSubmit  = SafeDate(r, "Date_TargetSubmit"),
            DateActualSubmit  = SafeDate(r, "Date_ActualSubmit"),
            DateTargetApprove = SafeDate(r, "Date_TargetApprove"),
            DateActualApprove = SafeDate(r, "Date_ActualApprove"),
            DateTargetRlsMfr  = SafeDate(r, "Date_TargetRLSMfr"),
            DateActualRlsMfr  = SafeDate(r, "Date_ActualRlsMfr"),
            DateTargetShip    = SafeDate(r, "Date_TargetShip"),
            DateActualShip    = SafeDate(r, "Date_ActualShip"),
            DateLastUpdate    = SafeDate(r, "Date_DashUpdate"),
        };

        private static MaterialRecord MapMaterial(SqlDataReader r) => new()
        {
            Id         = SafeInt(r, "ID"),
            MatId      = SafeInt(r, "MatID"),
            DashId     = SafeInt(r, "DashID"),
            ProjectId  = SafeInt(r, "ProjID"),   // NOTE: no underscore
            MatNo      = Safe(r, "MatNo"),
            MatDesc    = Safe(r, "MatDesc"),
            MatGroup   = Safe(r, "MatGroup"),
            MatUnits   = Safe(r, "MatUnits"),
            MatQty     = SafeLong(r, "MatQty"),
            DashNum    = Safe(r, "ID_Num"),
            DashDesc   = Safe(r, "Dash_Desc"),
            WdSpecies  = Safe(r, "WdSpecies"),
            WdCut      = Safe(r, "WdCut"),
            WdMatch    = Safe(r, "WdMatch"),
            FinishType  = Safe(r, "FinishType"),
            FinishPore  = Safe(r, "FinishPore"),
            FinishSheen = Safe(r, "FinishSheen"),
            FinishColor = Safe(r, "FinishColor"),
            FinishNotes = Safe(r, "FinishNotes"),
            MatApprove  = SafeDate(r, "MatApprove"),
            MatEdit     = SafeDate(r, "MatEdit"),
            ItemUpdate  = SafeDate(r, "ItemUpdate"),
            MatNotes    = Safe(r, "MatNotes"),
        };

        private static HardwareRecord MapHardware(SqlDataReader r) => new()
        {
            Id        = r.GetInt32(r.GetOrdinal("ID")),
            ProjectId = r.GetInt32(r.GetOrdinal("Proj_ID")),
            HdwNo     = Safe(r, "HdwNo"),
            HdwDesc   = Safe(r, "HdwDesc"),
            HdwUnit   = Safe(r, "HdwUnit"),
            HdwNotes  = Safe(r, "HdwNotes"),
            VendorNum  = Safe(r, "HdwVendorNum"),
            VendorLink = Safe(r, "HdwVendorlink"),
            HdwGroup  = SafeInt(r, "HdwGroup"),
            LibraryId = SafeInt(r, "HdwIWCLibraryID"),
            VendorId  = SafeInt(r, "HdwVendorID"),
            IsVoid    = SafeBool(r, "HdwVoid"),
            IsByIWC   = SafeBool(r, "HdwByIWC"),
            HdwEdit    = SafeDate(r, "HdwEdit"),
            HdwApprove = SafeDate(r, "HdwApprove"),
        };

        // ============================================================
        // Safe reader helpers
        // ============================================================

        private static string Safe(SqlDataReader r, string col)
        {
            try
            {
                int i = r.GetOrdinal(col);
                return r.IsDBNull(i) ? string.Empty : r.GetString(i).Trim();
            }
            catch { return string.Empty; }
        }

        private static int SafeInt(SqlDataReader r, string col)
        {
            try
            {
                int i = r.GetOrdinal(col);
                if (r.IsDBNull(i)) return 0;
                var val = r.GetValue(i);
                return val switch
                {
                    int    v => v,
                    long   v => (int)v,
                    short  v => v,
                    byte   v => v,
                    string s => int.TryParse(s, out var n) ? n : 0,
                    _        => Convert.ToInt32(val)
                };
            }
            catch { return 0; }
        }

        private static long SafeLong(SqlDataReader r, string col)
        {
            try
            {
                int i = r.GetOrdinal(col);
                return r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i));
            }
            catch { return 0L; }
        }

        private static bool SafeBool(SqlDataReader r, string col)
        {
            try
            {
                int i = r.GetOrdinal(col);
                return !r.IsDBNull(i) && r.GetBoolean(i);
            }
            catch { return false; }
        }

        private static DateOnly? SafeDate(SqlDataReader r, string col)
        {
            try
            {
                int i = r.GetOrdinal(col);
                if (r.IsDBNull(i)) return null;
                var val = r.GetValue(i);
                return val switch
                {
                    DateTime dt   => DateOnly.FromDateTime(dt),
                    DateOnly d    => d,
                    string   s    => DateOnly.TryParse(s, out var d2) ? d2 : null,
                    _             => DateOnly.FromDateTime(Convert.ToDateTime(val))
                };
            }
            catch { return null; }
        }
    }
}
