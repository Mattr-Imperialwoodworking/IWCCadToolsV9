namespace IWCCadToolsV9.Data.Models
{
    /// <summary>
    /// Typed, immutable representation of a project row from dbo.Proj_CompileActive.
    /// Replaces raw DataSet/DataRow access scattered across UI controls.
    /// All string properties are non-nullable — empty string instead of null.
    /// </summary>
    public record ProjectRecord
    {
        // ---------------------------------------------------------------------------
        // Identity
        // ---------------------------------------------------------------------------

        /// <summary>Primary key — dbo.Proj.ID</summary>
        public int    Id         { get; init; }

        /// <summary>Human-readable project number — e.g. "2401"</summary>
        public string IdNum      { get; init; } = string.Empty;

        /// <summary>Full project name — dbo.Proj.Proj_Name</summary>
        public string Name       { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Contacts (resolved names from the Compile view)
        // ---------------------------------------------------------------------------

        /// <summary>Full architect name — Proj_Compile.Architect</summary>
        public string Architect  { get; init; } = string.Empty;

        /// <summary>Full contractor name — Proj_Compile.Contractor</summary>
        public string Contractor { get; init; } = string.Empty;

        /// <summary>Project manager full name — Proj_Compile.PM</summary>
        public string PM         { get; init; } = string.Empty;

        /// <summary>Project manager initials — Proj_Compile.PMINI (used on titleblocks)</summary>
        public string PMIni      { get; init; } = string.Empty;

        /// <summary>Architect titleblock name — Proj_Compile.ArchTb</summary>
        public string ArchTb     { get; init; } = string.Empty;

        /// <summary>Contractor titleblock name — Proj_Compile.ContTb</summary>
        public string ContTb     { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Dates
        // ---------------------------------------------------------------------------

        public DateOnly? StartDate       { get; init; }
        public DateOnly? EstimatedComp   { get; init; }
        public DateOnly? EstProduction   { get; init; }
        public DateOnly? EstInstall      { get; init; }
        public DateOnly? ActualComplete  { get; init; }

        // ---------------------------------------------------------------------------
        // Status flags
        // ---------------------------------------------------------------------------

        public bool IsActiveDrafting { get; init; }
        public bool IsActiveShop     { get; init; }
        public bool IsComplete       { get; init; }

        // ---------------------------------------------------------------------------
        // Certifications
        // ---------------------------------------------------------------------------

        public bool IsLEED { get; init; }
        public bool IsFSC  { get; init; }
        public bool IsNAUF { get; init; }
        public bool IsVOC  { get; init; }

        // ---------------------------------------------------------------------------
        // Misc
        // ---------------------------------------------------------------------------

        public string Color         { get; init; } = string.Empty;
        public string SharepointId  { get; init; } = string.Empty;

        // ---------------------------------------------------------------------------
        // Convenience
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Display string used in project picker lists — matches existing UI format.
        /// </summary>
        public string DisplayLabel => $"{IdNum} - {Name}";

        /// <summary>True when this record represents a real loaded project.</summary>
        public bool IsValid => Id > 0 && !string.IsNullOrWhiteSpace(IdNum);
    }
}
