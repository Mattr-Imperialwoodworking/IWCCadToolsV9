using System;
using System.Collections.Generic;

namespace IWCCadToolsV9.Data.Models
{
    public sealed record DrawingSeriesDashNode
    {
        public int DashId { get; init; }
        public string DashNum { get; init; } = string.Empty;
        public string DashDesc { get; init; } = string.Empty;
        public int? DashParent { get; init; }
        public int? DashType { get; init; }
        public bool IsCurrentDash { get; init; }
        public List<DrawingSeriesFileRecord> Files { get; } = new();

        public string DisplayText => string.IsNullOrWhiteSpace(DashDesc)
            ? DashNum
            : $"{DashNum} - {DashDesc}";
    }

    public sealed record DrawingSeriesFileRecord
    {
        public int FileId { get; init; }
        public int DashId { get; init; }
        public string DashNum { get; init; } = string.Empty;
        public string DashDesc { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string SavedPath { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public DateTime? LastWriteTimeUtc { get; init; }
        public List<DrawingSeriesSheetRecord> Sheets { get; } = new();
    }

    public sealed record DrawingSeriesSheetRecord
    {
        public int SheetId { get; init; }
        public int FileId { get; init; }
        public string LayoutName { get; init; } = string.Empty;
        public string SheetNumber { get; init; } = string.Empty;
        public string SheetSubject { get; init; } = string.Empty;
    }

    public sealed record SheetTitleBlockInfo
    {
        public string LayoutName { get; init; } = string.Empty;
        public string SheetNumber { get; init; } = string.Empty;
        public string SheetSubject { get; init; } = string.Empty;
        public string TitleBlockName { get; init; } = string.Empty;
        public Autodesk.AutoCAD.DatabaseServices.ObjectId LayoutId { get; init; }
        public int? ExistingSheetId { get; init; }
    }
}
