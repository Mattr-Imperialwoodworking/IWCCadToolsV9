namespace IWCCadToolsV9.Data.Models
{
    /// <summary>
    /// Typed representation of a material row from dbo.Proj_MatDash_Compile.
    /// NOTE: The view uses ProjID (no underscore) unlike all other tables.
    /// </summary>
    public record MaterialRecord
    {
        // ---------------------------------------------------------------------------
        // Identity
        // NOTE: Proj_MatDash_Compile uses ProjID (no underscore).
        // ---------------------------------------------------------------------------

        /// <summary>Material record ID — Proj_MatDash_Compile.ID (nullable in view)</summary>
        public int    Id        { get; init; }

        /// <summary>Material item ID — Proj_MatDash_Compile.MatID</summary>
        public int    MatId     { get; init; }

        /// <summary>Linked dash ID — Proj_MatDash_Compile.DashID</summary>
        public int    DashId    { get; init; }

        /// <summary>Project ID — Proj_MatDash_Compile.ProjID (no underscore)</summary>
        public int    ProjectId { get; init; }

        // ---------------------------------------------------------------------------
        // Material identity
        // ---------------------------------------------------------------------------

        public string MatNo    { get; init; } = string.Empty;
        public string MatDesc  { get; init; } = string.Empty;
        public string MatGroup { get; init; } = string.Empty;
        public string MatUnits { get; init; } = string.Empty;

        /// <summary>Quantity for this dash assignment</summary>
        public long   MatQty   { get; init; }

        // ---------------------------------------------------------------------------
        // Dash context (from the view join)
        // ---------------------------------------------------------------------------

        public string DashNum  { get; init; } = string.Empty;
        public string DashDesc { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Wood / finish specification
        // ---------------------------------------------------------------------------

        public string WdSpecies    { get; init; } = string.Empty;
        public string WdCut        { get; init; } = string.Empty;
        public string WdMatch      { get; init; } = string.Empty;
        public string FinishType   { get; init; } = string.Empty;
        public string FinishPore   { get; init; } = string.Empty;
        public string FinishSheen  { get; init; } = string.Empty;
        public string FinishColor  { get; init; } = string.Empty;
        public string FinishNotes  { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Status / tracking
        // ---------------------------------------------------------------------------

        public DateOnly? MatApprove  { get; init; }
        public DateOnly? MatEdit     { get; init; }
        public DateOnly? ItemUpdate  { get; init; }
        public string    MatNotes    { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Convenience
        // ---------------------------------------------------------------------------

        public string DisplayLabel => string.IsNullOrWhiteSpace(MatNo)
            ? MatDesc
            : $"{MatNo} - {MatDesc}";
    }
}
