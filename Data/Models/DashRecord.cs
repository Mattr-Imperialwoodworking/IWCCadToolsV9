namespace IWCCadToolsV9.Data.Models
{
    /// <summary>
    /// Typed representation of a dash (drawing series) row from dbo.Proj_DashCompileReportActive.
    /// Note: the view exposes DashID as the meaningful PK — ID is the project row ID.
    /// Replaces IWCDash.DashRecord (raw DataRow).
    /// </summary>
    public record DashRecord
    {
        // ---------------------------------------------------------------------------
        // Identity
        // NOTE: In Proj_DashCompileReportActive, DashID is the dash primary key.
        //       ID is the project-level ID. Both are carried here for convenience.
        // ---------------------------------------------------------------------------

        /// <summary>Dash primary key — Proj_DashCompileReportActive.DashID</summary>
        public int    DashId      { get; init; }

        /// <summary>Project FK — Proj_DashCompileReportActive.Proj_ID</summary>
        public int    ProjectId   { get; init; }

        /// <summary>Project number — Proj_DashCompileReportActive.IDNum</summary>
        public string ProjectIdNum { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Dash identity
        // ---------------------------------------------------------------------------

        /// <summary>Dash number string — e.g. "2401-001"</summary>
        public string DashNum     { get; init; } = string.Empty;

        /// <summary>Dash description / title</summary>
        public string DashDesc    { get; init; } = string.Empty;

        /// <summary>Linked drawing file name — Proj_DashCompileReportActive.Dash_Dwg</summary>
        public string DashDwg     { get; init; } = string.Empty;

        /// <summary>
        /// Dash type integer — Proj_DashCompileReportActive.Dash_Type.
        /// 2 = Series (parent), 1 = Component (child).
        /// </summary>
        public int    DashType    { get; init; }

        /// <summary>Parent dash ID (0 = top-level series)</summary>
        public int    DashParent  { get; init; }

        /// <summary>Phase number</summary>
        public int    DashPhase   { get; init; }

        // ---------------------------------------------------------------------------
        // Status
        // ---------------------------------------------------------------------------

        public bool   IsActive       { get; init; }
        public bool   IsActiveDraft  { get; init; }
        public bool   IsActiveShop   { get; init; }
        public bool   IsVoid         { get; init; }
        public int    DwgPriority    { get; init; }

        // ---------------------------------------------------------------------------
        // Assigned personnel
        // ---------------------------------------------------------------------------

        /// <summary>PM name from the view join</summary>
        public string PMName   { get; init; } = string.Empty;

        /// <summary>CAD initials</summary>
        public string CADIni   { get; init; } = string.Empty;

        /// <summary>Manufacturer name</summary>
        public string MfrName  { get; init; } = string.Empty;

        /// <summary>Manufacturer initials</summary>
        public string MfrIni   { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Key dates
        // ---------------------------------------------------------------------------

        public DateOnly? DateTargetSubmit  { get; init; }
        public DateOnly? DateActualSubmit  { get; init; }
        public DateOnly? DateTargetApprove { get; init; }
        public DateOnly? DateActualApprove { get; init; }
        public DateOnly? DateTargetRlsMfr  { get; init; }
        public DateOnly? DateActualRlsMfr  { get; init; }
        public DateOnly? DateTargetShip    { get; init; }
        public DateOnly? DateActualShip    { get; init; }
        public DateOnly? DateLastUpdate    { get; init; }

        // ---------------------------------------------------------------------------
        // Convenience
        // ---------------------------------------------------------------------------

        public string DisplayLabel => $"{DashNum} - {DashDesc}";

        /// <summary>True when this record represents a real loaded dash.</summary>
        public bool IsValid => DashId > 0;

        /// <summary>True when this dash is a Series-level (parent) row.</summary>
        public bool IsSeries    => DashType == 2;

        /// <summary>True when this dash is a Component-level (child) row.</summary>
        public bool IsComponent => DashType == 1;
    }
}
