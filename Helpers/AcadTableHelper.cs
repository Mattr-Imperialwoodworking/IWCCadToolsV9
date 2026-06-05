using System;
using System.Collections.Generic;
using System.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// Disambiguate: Autodesk.AutoCAD.DatabaseServices also defines DataTable
using DataTable = System.Data.DataTable;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Helpers for building and refreshing AutoCAD <see cref="Table"/> objects
    /// from a <see cref="System.Data.DataTable"/>.
    ///
    /// Consolidates V8's AcadTableUpdater and the duplicate update logic that
    /// existed separately in HardwareTableCommands and MaterialTableCommands.
    /// </summary>
    public static class AcadTableHelper
    {
        // ---------------------------------------------------------------------------
        // Column specification type
        // ---------------------------------------------------------------------------

        /// <summary>Describes a single column: header title, data column name, width (inches).</summary>
        public readonly struct ColumnSpec
        {
            public string Header   { get; }
            public string DataCol  { get; }
            public double Width    { get; }

            public ColumnSpec(string header, string dataCol, double width)
            {
                Header  = header;
                DataCol = dataCol;
                Width   = width;
            }
        }

        // ---------------------------------------------------------------------------
        // Hardware table columns
        // ---------------------------------------------------------------------------

        public static readonly ColumnSpec[] HardwareCols =
        {
            // Match the sample table: 3/4", 4", 3/4", 3/4", 1-1/2".
            new("HDW ID",      "HdwNo",    0.75),
            new("DESCRIPTION", "HdwDesc",  4.00),
            new("QTY",         "HdwQty",   0.75),
            new("UNIT",        "HdwUnit",  0.75),
            new("GROUP",       "HdwGroup", 1.50),
        };

        // ---------------------------------------------------------------------------
        // Material table columns
        // ---------------------------------------------------------------------------

        public static readonly ColumnSpec[] MaterialCols =
        {
            // Match the sample table: 3/4", 4", 1-1/2", 1-1/2".
            new("MAT#",        "MatNo",      0.75),
            new("DESCRIPTION", "MatDesc",    4.00),
            new("APPROVAL",    "MatApprove", 1.50),
            new("GROUP",       "MatGroup",   1.50),
        };

        // ---------------------------------------------------------------------------
        // Metal table columns
        // ---------------------------------------------------------------------------

        public static readonly ColumnSpec[] MetalCols =
        {
            new("PART #",      "Mtl_PrtNo",        0.75),
            new("DESCRIPTION", "Mtl_PrtDesc",      3.00),
            new("FINISH",      "Mtl_Finish",       1.25),
            new("MATERIAL",    "Mtl_Material",     1.25),
            new("LENGTH",      "Mtl_Length",       0.75),
            new("WIDTH",       "Mtl_Width",        0.75),
            new("HEIGHT",      "Mtl_Height",       0.75),
            new("THK",         "Mtl_Thk",          0.50),
            new("QTY",         "Mtl_Qty",          0.50),
            new("UNIT",        "Mtl_QtyUnits",     0.75),
            new("SHEET REF",   "Mtl_ShtReference", 1.00),
            new("NOTES",       "Mtl_Notes",        2.00),
        };

        // ---------------------------------------------------------------------------
        // Build a new Table
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Creates a new AutoCAD <see cref="Table"/> populated from <paramref name="data"/>.
        /// Row 0 is the merged title, row 1 is the column header, and data rows follow.
        /// Dimensions are specified in plotted inches and scaled by the current drawing DIMSCALE.
        /// </summary>
        public static Table BuildTable(DataTable data, ColumnSpec[] columns, string titleText)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (columns == null || columns.Length == 0)
                throw new ArgumentException("At least one column spec is required.", nameof(columns));

            int rowCount = data.Rows.Count + 2; // title + header + data rows
            int colCount = columns.Length;
            double scale = GetDimScale();

            var table = new Table();
            table.SetSize(rowCount, colCount);
            table.Position = Point3d.Origin; // caller repositions at insertion

            ApplyColumnWidths(table, columns, scale);

            // Title row: merged across all columns, 1/4" high.
            table.SetRowHeight(0, 0.25 * scale);
            table.MergeCells(CellRange.Create(table, 0, 0, 0, colCount - 1));
            SetCell(table, 0, 0, titleText, 5.0 / 64.0 * scale, CellAlignment.MiddleCenter);

            // Header row: 1/4" high.
            table.SetRowHeight(1, 0.25 * scale);
            for (int c = 0; c < colCount; c++)
                SetCell(table, 1, c, columns[c].Header, 5.0 / 64.0 * scale, CellAlignment.MiddleCenter);

            // Data rows.
            PopulateRows(table, data, columns, startRow: 2, scale);
            table.GenerateLayout();

            return table;
        }

        /// <summary>
        /// Updates an existing AutoCAD table while preserving the IWC title/header/data-row structure.
        /// </summary>
        public static void UpdateTable(Table acadTable, DataTable data, ColumnSpec[] columns, string titleText)
        {
            if (acadTable == null) throw new ArgumentNullException(nameof(acadTable));
            if (data == null)     throw new ArgumentNullException(nameof(data));
            if (columns == null)  throw new ArgumentNullException(nameof(columns));

            int expectedRows = data.Rows.Count + 2;
            if (acadTable.Rows.Count != expectedRows || acadTable.Columns.Count != columns.Length)
                acadTable.SetSize(expectedRows, columns.Length);

            double scale = GetDimScale();
            ApplyColumnWidths(acadTable, columns, scale);

            acadTable.SetRowHeight(0, 0.25 * scale);
            try { acadTable.MergeCells(CellRange.Create(acadTable, 0, 0, 0, columns.Length - 1)); } catch { }
            SetCell(acadTable, 0, 0, titleText, 5.0 / 64.0 * scale, CellAlignment.MiddleCenter);

            acadTable.SetRowHeight(1, 0.25 * scale);
            for (int c = 0; c < columns.Length; c++)
                SetCell(acadTable, 1, c, columns[c].Header, 5.0 / 64.0 * scale, CellAlignment.MiddleCenter);

            PopulateRows(acadTable, data, columns, startRow: 2, scale);
            acadTable.GenerateLayout();
        }


        /// <summary>
        /// Preferred IWC table style name used by BOM-related tables.
        /// If the style is not present in the current drawing, the drawing's current/default
        /// table style is left in use so insertion/update will still succeed.
        /// </summary>
        public const string PreferredTableStyleName = "IWC_Material";

        /// <summary>
        /// Applies the preferred IWC table style when it exists in the active drawing.
        /// Falls back to the database default table style if the named style is not available.
        /// </summary>
        public static void ApplyPreferredTableStyle(Table table, Database db, Transaction tr)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (tr == null) throw new ArgumentNullException(nameof(tr));

            table.SetDatabaseDefaults(db);

            try
            {
                var tableStyleDictionary = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
                table.TableStyle = tableStyleDictionary.Contains(PreferredTableStyleName)
                    ? tableStyleDictionary.GetAt(PreferredTableStyleName)
                    : db.Tablestyle;
            }
            catch
            {
                table.TableStyle = db.Tablestyle;
            }
        }

        // ---------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------

        private static void PopulateRows(Table table, DataTable data,
            ColumnSpec[] columns, int startRow, double scale)
        {
            double textHeight = 5.0 / 64.0 * scale;
            double dataHeight = 0.75 * scale;

            for (int r = 0; r < data.Rows.Count; r++)
            {
                int tableRow = startRow + r;
                table.SetRowHeight(tableRow, dataHeight);

                var row = data.Rows[r];
                for (int c = 0; c < columns.Length; c++)
                {
                    var col = columns[c];
                    string val = data.Columns.Contains(col.DataCol)
                        ? NormalizeCellText(row[col.DataCol]?.ToString() ?? string.Empty)
                        : string.Empty;

                    var align = IsCenteredColumn(col.Header)
                        ? CellAlignment.MiddleCenter
                        : CellAlignment.MiddleLeft;

                    SetCell(table, tableRow, c, val, textHeight, align);
                }
            }
        }

        private static void ApplyColumnWidths(Table table, ColumnSpec[] columns, double scale)
        {
            for (int c = 0; c < columns.Length; c++)
                table.SetColumnWidth(c, columns[c].Width * scale);
        }

        private static void SetCell(Table table, int row, int col, string text, double height, CellAlignment align)
        {
            var cell = table.Cells[row, col];
            cell.TextString = NormalizeCellText(text).ToUpperInvariant();
            cell.TextHeight = height;
            cell.Alignment = align;
        }

        private static string NormalizeCellText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            return text
                .Replace("\r\n", "\\P")
                .Replace("\n", "\\P")
                .Replace("\r", "\\P");
        }

        private static bool IsCenteredColumn(string header)
        {
            header = header.Trim().ToUpperInvariant();
            return header is "HDW ID" or "MAT#" or "PART #" or "QTY" or "UNIT"
                or "LENGTH" or "WIDTH" or "HEIGHT" or "THK" or "SHEET REF";
        }

        private static double GetDimScale()
        {
            try
            {
                double scale = Convert.ToDouble(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("DIMSCALE"));
                return scale > 0 ? scale : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }
    }
}
