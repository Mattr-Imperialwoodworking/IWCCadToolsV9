using System;
using System.Collections.Generic;
using System.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
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


        /// <summary>
        /// Creates the narrow two-column titleblock hardware table layout.
        /// Row 0 is the title row. Each following row is one hardware item with
        /// variable row height based on multiline description text.
        /// </summary>
        public static Table BuildTitleblockHardwareTable(DataTable data, string titleText)
        {
            var columns = new[]
            {
                new ColumnSpec("#", "HdwNo", 0.75),
                new ColumnSpec(titleText, "_TitleblockHardwareText", 3.125),
            };

            return BuildTitleblockTable(data, columns, titleText, BuildHardwareTitleblockText, BuildHardwareGroupHeaderText);
        }

        /// <summary>
        /// Updates an existing titleblock hardware table.
        /// </summary>
        public static void UpdateTitleblockHardwareTable(Table acadTable, DataTable data, string titleText)
        {
            var columns = new[]
            {
                new ColumnSpec("#", "HdwNo", 0.75),
                new ColumnSpec(titleText, "_TitleblockHardwareText", 3.125),
            };

            UpdateTitleblockTable(acadTable, data, columns, titleText, BuildHardwareTitleblockText, BuildHardwareGroupHeaderText);
        }

        /// <summary>
        /// Creates the narrow two-column titleblock material table layout.
        /// Row 0 is the title row. Each following row is one material item with
        /// variable row height based on multiline description text.
        /// </summary>
        public static Table BuildTitleblockMaterialTable(DataTable data, string titleText)
        {
            var columns = new[]
            {
                new ColumnSpec("#", "MatNo", 0.75),
                new ColumnSpec(titleText, "_TitleblockMaterialText", 3.125),
            };

            return BuildTitleblockTable(data, columns, titleText, BuildMaterialTitleblockText, BuildMaterialGroupHeaderText);
        }

        /// <summary>
        /// Updates an existing titleblock material table.
        /// </summary>
        public static void UpdateTitleblockMaterialTable(Table acadTable, DataTable data, string titleText)
        {
            var columns = new[]
            {
                new ColumnSpec("#", "MatNo", 0.75),
                new ColumnSpec(titleText, "_TitleblockMaterialText", 3.125),
            };

            UpdateTitleblockTable(acadTable, data, columns, titleText, BuildMaterialTitleblockText, BuildMaterialGroupHeaderText);
        }

        private static Table BuildTitleblockTable(
            DataTable data,
            ColumnSpec[] columns,
            string titleText,
            Func<DataRow, string> textBuilder,
            Func<DataRow, string> groupBuilder)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            int rowCount = CountTitleblockRows(data, groupBuilder);
            double scale = GetDimScale();

            var table = new Table();
            table.SetSize(rowCount, 2);
            table.Position = Point3d.Origin;
            ApplyColumnWidths(table, columns, scale);

            SetTitleblockTitleRow(table, 0, titleText, 5.0 / 64.0 * scale, scale);

            var groupHeaderRows = PopulateTitleblockRows(table, data, columns, textBuilder, groupBuilder, scale, preserveExistingRowHeights: false, existingRowCount: 0);
            table.GenerateLayout();
            ReapplyTitleblockGroupHeaderStyles(table, groupHeaderRows, scale);

            return table;
        }

        private static void UpdateTitleblockTable(
            Table acadTable,
            DataTable data,
            ColumnSpec[] columns,
            string titleText,
            Func<DataRow, string> textBuilder,
            Func<DataRow, string> groupBuilder)
        {
            if (acadTable == null) throw new ArgumentNullException(nameof(acadTable));
            if (data == null) throw new ArgumentNullException(nameof(data));

            int expectedRows = CountTitleblockRows(data, groupBuilder);
            int existingRowCount = acadTable.Rows.Count;
            if (acadTable.Rows.Count != expectedRows || acadTable.Columns.Count != 2)
                acadTable.SetSize(expectedRows, 2);

            double scale = GetDimScale();
            ApplyColumnWidths(acadTable, columns, scale);

            SetTitleblockTitleRow(acadTable, 0, titleText, 5.0 / 64.0 * scale, scale);

            var groupHeaderRows = PopulateTitleblockRows(acadTable, data, columns, textBuilder, groupBuilder, scale, preserveExistingRowHeights: true, existingRowCount: existingRowCount);
            acadTable.GenerateLayout();
            ReapplyTitleblockGroupHeaderStyles(acadTable, groupHeaderRows, scale);
        }

        private static List<int> PopulateTitleblockRows(
            Table table,
            DataTable data,
            ColumnSpec[] columns,
            Func<DataRow, string> textBuilder,
            Func<DataRow, string> groupBuilder,
            double scale,
            bool preserveExistingRowHeights,
            int existingRowCount)
        {
            double textHeight = 5.0 / 64.0 * scale;
            int tableRow = 1;
            string? currentGroup = null;
            var groupHeaderRows = new List<int>();

            for (int r = 0; r < data.Rows.Count; r++)
            {
                DataRow row = data.Rows[r];
                string groupText = NormalizeGroupText(groupBuilder(row));

                if (!string.Equals(currentGroup, groupText, StringComparison.OrdinalIgnoreCase))
                {
                    currentGroup = groupText;
                    SetTitleblockGroupHeaderRow(table, tableRow, groupText, textHeight, scale);
                    groupHeaderRows.Add(tableRow);
                    tableRow++;
                }

                string key = GetSafeField(row, columns[0].DataCol);
                string detailText = textBuilder(row);
                double rowHeight = EstimateTitleblockRowHeight(detailText, scale);

                // Insert uses a calculated starting height. Update/refresh should not
                // overwrite a row height that the user has manually adjusted in the
                // drawing, because that caused refreshed borders to jump back to the
                // original generated size. Newly added rows still receive the calculated
                // height.
                if (!preserveExistingRowHeights || tableRow >= existingRowCount || table.Rows[tableRow].Height <= 0)
                    table.SetRowHeight(tableRow, rowHeight);

                SetCell(table, tableRow, 0, key, textHeight, CellAlignment.MiddleCenter);
                SetCell(table, tableRow, 1, detailText, textHeight, CellAlignment.MiddleLeft);
                tableRow++;
            }

            return groupHeaderRows;
        }

        private static void ReapplyTitleblockGroupHeaderStyles(Table table, List<int> groupHeaderRows, double scale)
        {
            if (groupHeaderRows == null || groupHeaderRows.Count == 0) return;

            // GenerateLayout() can reset row/cell formatting after content is written.
            // Re-stamp every group header row's manual formatting after layout is
            // complete. This is the authoritative final pass — nothing runs after this.
            foreach (int row in groupHeaderRows)
            {
                if (row < 0 || row >= table.Rows.Count) continue;

                ApplyGroupHeaderRowFormatting(table, row, scale);
            }
        }

        private static int CountTitleblockRows(DataTable data, Func<DataRow, string> groupBuilder)
        {
            int count = 1; // top title row
            string? currentGroup = null;

            foreach (DataRow row in data.Rows)
            {
                string groupText = NormalizeGroupText(groupBuilder(row));
                if (!string.Equals(currentGroup, groupText, StringComparison.OrdinalIgnoreCase))
                {
                    currentGroup = groupText;
                    count++; // group header row
                }

                count++; // item row
            }

            return count;
        }

        private static void SetTitleblockGroupHeaderRow(Table table, int row, string groupText, double textHeight, double scale)
        {
            // Merge first — MergeCells can reset row/cell formatting, so all
            // explicit formatting is (re)applied afterward via
            // ApplyGroupHeaderRowFormatting, including by the post-layout pass
            // in ReapplyTitleblockGroupHeaderStyles.
            try { table.MergeCells(CellRange.Create(table, row, 0, row, 1)); } catch { }

            SetCell(table, row, 0, groupText, textHeight, CellAlignment.MiddleCenter);

            ApplyGroupHeaderRowFormatting(table, row, scale);
        }

        /// <summary>
        /// Applies the manual formatting spec for a group header (separator) row:
        /// background fill color 254, .06 horizontal/vertical cell margins,
        /// .07 text height, and .1875 cell (row) height. Applied to both cells
        /// in the merged row.
        /// </summary>
        private static void ApplyGroupHeaderRowFormatting(Table table, int row, double scale)
        {
            const short backgroundColorIndex = 254;
            const double cellMargin = 0.06;
            const double textHeight = 0.07;
            const double cellHeight = 0.1875;

            table.Rows[row].Height = cellHeight * scale;

            var bgColor = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, backgroundColorIndex);

            for (int col = 0; col < table.Columns.Count; col++)
            {
                table.SetBackgroundColorNone(row, col, false);
                table.SetBackgroundColor(row, col, bgColor);
                table.SetMargin(row, col, CellMargins.Left, cellMargin * scale);
                table.SetMargin(row, col, CellMargins.Right, cellMargin * scale);
                table.SetMargin(row, col, CellMargins.Top, cellMargin * scale);
                table.SetMargin(row, col, CellMargins.Bottom, cellMargin * scale);

                var cell = table.Cells[row, col];
                cell.TextHeight = textHeight * scale;
            }
        }

        /// <summary>
        /// Sets up the top title row of a titleblock table (e.g. "IWC HARDWARE:" /
        /// "IWC MATERIALS:"): merges both columns into a single cell, writes the
        /// title text, and sets the background fill to color 253.
        /// </summary>
        private static void SetTitleblockTitleRow(Table table, int row, string titleText, double textHeight, double scale)
        {
            const short backgroundColorIndex = 253;

            table.Rows[row].Height = 0.25 * scale;

            try { table.MergeCells(CellRange.Create(table, row, 0, row, 1)); } catch { }

            SetCell(table, row, 0, titleText, textHeight, CellAlignment.MiddleCenter);

            var bgColor = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, backgroundColorIndex);

            for (int col = 0; col < table.Columns.Count; col++)
            {
                table.SetBackgroundColorNone(row, col, false);
                table.SetBackgroundColor(row, col, bgColor);
            }
        }

        private static string NormalizeGroupText(string groupText)
        {
            return string.IsNullOrWhiteSpace(groupText) ? "UNGROUPED" : groupText.Trim();
        }

        private static double EstimateTitleblockRowHeight(string detailText, double scale)
        {
            int lineCount = CountTextLines(detailText);

            // Titleblock tables use 5/64 text. A row-height factor of ~0.145
            // per line keeps multiline rows compact while still allowing the
            // row to grow with the amount of text.
            return Math.Max(0.35, 0.145 * lineCount + 0.12) * scale;
        }

        private static string BuildHardwareGroupHeaderText(DataRow row)
        {
            // Titleblock hardware group rows should use the hardware group tag
            // followed by the group description/name, e.g. "H01 - PULLS".
            // In the current compile views/stored procedure, HdwGroupNo is the
            // user-facing group name/description and HdwGroupTag is the short tag.
            string groupTag = GetSafeField(row, "HdwGroupTag");
            string groupDesc = GetFirstSafeField(row, "HdwGroupNo", "HdwGroupDesc", "HdwGroup");

            if (!string.IsNullOrWhiteSpace(groupTag) && !string.IsNullOrWhiteSpace(groupDesc))
                return $"{groupTag} - {groupDesc}";

            return !string.IsNullOrWhiteSpace(groupTag) ? groupTag : groupDesc;
        }

        private static string BuildMaterialGroupHeaderText(DataRow row)
        {
            string groupNo = GetFirstSafeField(row, "MatGroupNo", "MatGroup", "MatGroupTag");
            string groupDesc = GetSafeField(row, "MatGroupDesc");

            if (!string.IsNullOrWhiteSpace(groupNo) && !string.IsNullOrWhiteSpace(groupDesc))
                return $"{groupNo} - {groupDesc}";

            return !string.IsNullOrWhiteSpace(groupNo) ? groupNo : groupDesc;
        }

        private static string BuildHardwareTitleblockText(DataRow row)
        {
            var lines = new List<string>();
            AddLine(lines, GetSafeField(row, "HdwDesc"));

            string qty = GetSafeField(row, "HdwQty");
            string unit = GetSafeField(row, "HdwUnit");
            if (!string.IsNullOrWhiteSpace(qty) || !string.IsNullOrWhiteSpace(unit))
                AddLine(lines, $"QTY: {qty}{(string.IsNullOrWhiteSpace(unit) ? string.Empty : ", " + unit)}");

            string approval = FormatDateOrText(GetSafeField(row, "HdwApprove"));
            if (!string.IsNullOrWhiteSpace(approval))
                AddLine(lines, $"APPROVAL: {approval}");

            return string.Join("\r\n", lines);
        }

        private static string BuildMaterialTitleblockText(DataRow row)
        {
            var lines = new List<string>();
            AddLine(lines, GetSafeField(row, "MatDesc"));

            string qty = GetSafeField(row, "MatQty");
            string units = GetSafeField(row, "MatUnits");
            if (!string.IsNullOrWhiteSpace(qty) || !string.IsNullOrWhiteSpace(units))
                AddLine(lines, $"QTY: {qty}{(string.IsNullOrWhiteSpace(units) ? string.Empty : ", " + units)}");

            string approval = FormatDateOrText(GetSafeField(row, "MatApprove"));
            if (!string.IsNullOrWhiteSpace(approval))
                AddLine(lines, $"APPROVAL: {approval}");

            return string.Join("\r\n", lines);
        }

        private static void AddLine(List<string> lines, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                lines.Add(value.Trim());
        }

        private static string GetFirstSafeField(DataRow row, params string[] columnNames)
        {
            foreach (string columnName in columnNames)
            {
                string value = GetSafeField(row, columnName);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static string GetSafeField(DataRow row, string columnName)
        {
            if (row.Table == null || !row.Table.Columns.Contains(columnName))
                return string.Empty;

            object? value = row[columnName];
            if (value == null || value == DBNull.Value)
                return string.Empty;

            return Convert.ToString(value) ?? string.Empty;
        }

        private static string FormatDateOrText(string value)
        {
            if (DateTime.TryParse(value, out var dt))
                return dt.ToString("MM/dd/yyyy");

            return value;
        }

        private static int CountTextLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;

            const int approximateCharsPerLine = 34;
            string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            int total = 0;

            foreach (string line in normalized.Split('\n'))
            {
                int len = Math.Max(1, line.Length);
                total += Math.Max(1, (int)Math.Ceiling(len / (double)approximateCharsPerLine));
            }

            return Math.Max(1, total);
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
