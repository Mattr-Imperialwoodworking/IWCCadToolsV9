namespace IWCCadToolsV9.Data.Models
{
    /// <summary>
    /// Typed representation of a hardware row from dbo.Proj_Hdw.
    /// </summary>
    public record HardwareRecord
    {
        // ---------------------------------------------------------------------------
        // Identity
        // ---------------------------------------------------------------------------

        /// <summary>Primary key — dbo.Proj_Hdw.ID</summary>
        public int    Id        { get; init; }

        /// <summary>Project FK — dbo.Proj_Hdw.Proj_ID</summary>
        public int    ProjectId { get; init; }

        // ---------------------------------------------------------------------------
        // Hardware identity
        // ---------------------------------------------------------------------------

        public string HdwNo      { get; init; } = string.Empty;
        public string HdwDesc    { get; init; } = string.Empty;
        public string HdwUnit    { get; init; } = string.Empty;
        public string HdwNotes   { get; init; } = string.Empty;
        public string VendorNum  { get; init; } = string.Empty;
        public string VendorLink { get; init; } = string.Empty;

        /// <summary>
        /// Hardware group FK — dbo.Proj_Hdw.HdwGroup.
        /// Numeric FK; resolve via lookup table if group names are needed.
        /// </summary>
        public int    HdwGroup   { get; init; }

        /// <summary>Library item FK — dbo.Proj_Hdw.HdwIWCLibraryID (0 = no library link)</summary>
        public int    LibraryId  { get; init; }

        /// <summary>Vendor FK — dbo.Proj_Hdw.HdwVendorID (0 = no vendor link)</summary>
        public int    VendorId   { get; init; }

        // ---------------------------------------------------------------------------
        // Status
        // ---------------------------------------------------------------------------

        public bool      IsVoid     { get; init; }
        public bool      IsByIWC    { get; init; }
        public DateOnly? HdwEdit    { get; init; }
        public DateOnly? HdwApprove { get; init; }

        // ---------------------------------------------------------------------------
        // Convenience
        // ---------------------------------------------------------------------------

        public string DisplayLabel => string.IsNullOrWhiteSpace(HdwNo)
            ? HdwDesc
            : $"{HdwNo} - {HdwDesc}";
    }
}
