using System;
using System.Collections.Generic;
using System.Data;
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
            new("HDW ID",      "HdwNo",    1.25),
            new("DESCRIPTION", "HdwDesc",  5.00),
            new("QTY",         "HdwQty",   1.00),
            new("UNIT",        "HdwUnit",  1.25),
            new("GROUP",       "HdwGroup", 2.50),
        };

        // ---------------------------------------------------------------------------
        // Material table columns
        // ---------------------------------------------------------------------------

        public static readonly ColumnSpec[] MaterialCols =
        {
            new("MAT#",        "MatNo",      1.50),
            new("DESCRIPTION", "MatDesc",    6.00),
            new("APPROVAL",    "MatApprove", 2.50),
            new("GROUP",       "MatGroup",   2.50),
        };

        // ---------------------------------------------------------------------------
        // Build a new Table
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Creates a new AutoCAD <see cref="Table"/> populated from <paramref name="data"/>.
        /// Row 0 is always the header; data rows follow.
        /// </summary>
        public static Table BuildTable(DataTable data, ColumnSpec[] columns)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (columns == null || columns.Length == 0)
                throw new ArgumentException("At least one column spec is required.", nameof(columns));

            int rowCount = data.Rows.Count + 1; // +1 for header
            int colCount = columns.Length;

            var table = new Table();
            table.SetSize(rowCount, colCount);
            table.Position = Point3d.Origin; // caller repositions at insertion

            // Header row
            for (int c = 0; c < colCount; c++)
            {
                table.Cells[0, c].TextString = columns[c].Header;
                table.Cells[0, c].Alignment  = CellAlignment.MiddleCenter;
                table.Columns[c].Width       = columns[c].Width;
            }

            // Data rows
            PopulateRows(table, data, columns, startRow: 1);

            return table;
        }

        // ---------------------------------------------------------------------------
        // Update an existing Table
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Updates the content rows of an existing AutoCAD <see cref="Table"/>.
        /// Resizes the table if the row count has changed.
        /// Assumes the header row (row 0) and column order are unchanged.
        /// </summary>
        public static void UpdateTable(Table acadTable, DataTable data, ColumnSpec[] columns)
        {
            if (acadTable == null) throw new ArgumentNullException(nameof(acadTable));
            if (data == null)     throw new ArgumentNullException(nameof(data));
            if (columns == null)  throw new ArgumentNullException(nameof(columns));

            int expectedRows = data.Rows.Count + 1;
            if (acadTable.Rows.Count != expectedRows)
                acadTable.SetSize(expectedRows, columns.Length);

            PopulateRows(acadTable, data, columns, startRow: 1);
        }

        // ---------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------

        private static void PopulateRows(Table table, DataTable data,
            ColumnSpec[] columns, int startRow)
        {
            for (int r = 0; r < data.Rows.Count; r++)
            {
                var row = data.Rows[r];
                for (int c = 0; c < columns.Length; c++)
                {
                    var col = columns[c];
                    string val = data.Columns.Contains(col.DataCol)
                        ? (row[col.DataCol]?.ToString() ?? string.Empty).ToUpper()
                        : string.Empty;
                    table.Cells[startRow + r, c].TextString = val;
                }
            }
        }
    }
}
