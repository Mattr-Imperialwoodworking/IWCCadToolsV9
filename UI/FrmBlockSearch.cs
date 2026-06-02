using System;
using System.Data;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Full-text search across Dwg_Block and Dwg_BlockAssets.
    /// Returns the selected asset via SelectedAssetId / SelectedBlockId etc.
    /// Caller handles actual insert / open via DialogResult == OK.
    /// </summary>
    public sealed class FrmBlockSearch : IWCBaseForm
    {
        // -----------------------------------------------------------------------
        // Result properties (populated on double-click / OK)
        // -----------------------------------------------------------------------

        public int    SelectedAssetId    { get; private set; }
        public int    SelectedBlockId    { get; private set; }
        public string SelectedBlockName  { get; private set; } = string.Empty;
        public bool   SelectedIsComponent{ get; private set; }

        // -----------------------------------------------------------------------
        // Backing data table (drives the sortable grid)
        // -----------------------------------------------------------------------

        private DataTable _results = new DataTable();

        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        public FrmBlockSearch()
        {
            InitializeComponent();
            BuildResultSchema();
            WireEvents();
        }

        // -----------------------------------------------------------------------
        // Schema
        // -----------------------------------------------------------------------

        private void BuildResultSchema()
        {
            _results.Columns.Add("AssetID",      typeof(int));     // hidden
            _results.Columns.Add("BlockID",      typeof(int));     // hidden
            _results.Columns.Add("BlockName",    typeof(string));  // hidden (internal name)
            _results.Columns.Add("DwgCount",     typeof(int));     // hidden (component check)
            _results.Columns.Add("Display Name", typeof(string));
            _results.Columns.Add("Block Name",   typeof(string));
            _results.Columns.Add("Asset File",   typeof(string));
            _results.Columns.Add("Type",         typeof(string));
            _results.Columns.Add("Asset Description", typeof(string));
            _results.Columns.Add("Block Description", typeof(string));
            _results.Columns.Add("Manufacturer", typeof(string));
            _results.Columns.Add("Vendor",       typeof(string));
        }

        // -----------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------

        private void WireEvents()
        {
            txtSearch.KeyDown        += TxtSearch_KeyDown;
            btnSearch.Click          += async (_, _) => await RunSearchAsync();
            dgvResults.DoubleClick   += DgvResults_DoubleClick;
            dgvResults.CellFormatting+= DgvResults_CellFormatting;
            btnClose.Click           += (_, _) => Close();
        }

        private void TxtSearch_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = RunSearchAsync();
            }
        }

        private void DgvResults_DoubleClick(object? sender, EventArgs e)
        {
            if (dgvResults.CurrentRow == null) return;
            var row = ((DataRowView)dgvResults.CurrentRow.DataBoundItem).Row;

            SelectedAssetId    = (int)row["AssetID"];
            SelectedBlockId    = (int)row["BlockID"];
            SelectedBlockName  = row["BlockName"] as string ?? string.Empty;
            SelectedIsComponent= (int)row["DwgCount"] <= 1;

            DialogResult = DialogResult.OK;
        }

        // Bold Series-type rows (DWG assets) to distinguish from other file types
        private void DgvResults_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || dgvResults.Rows[e.RowIndex].DataBoundItem is not DataRowView drv)
                return;

            string ext = drv.Row["Type"] as string ?? string.Empty;
            if (string.Equals(ext, ".dwg", StringComparison.OrdinalIgnoreCase))
                e.CellStyle.Font = new Font(dgvResults.Font, FontStyle.Bold);
        }

        // -----------------------------------------------------------------------
        // Search
        // -----------------------------------------------------------------------

        private async Task RunSearchAsync()
        {
            string term = txtSearch.Text.Trim();
            if (string.IsNullOrWhiteSpace(term))
            {
                lblStatus.Text = "Enter a search term.";
                return;
            }

            btnSearch.Enabled = false;
            lblStatus.Text    = "Searching…";
            dgvResults.DataSource = null;
            _results.Rows.Clear();

            DataTable? dt = null;
            string? errorMsg = null;

            try
            {
                dt = await Task.Run(() => ExecuteSearch(term));
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }

            if (errorMsg != null)
            {
                lblStatus.Text    = $"⚠ Search failed: {errorMsg}";
                btnSearch.Enabled = true;
                return;
            }

            // Populate backing table on UI thread
            foreach (DataRow r in dt!.Rows)
            {
                _results.Rows.Add(
                    r["AssetID"],
                    r["BlockID"],
                    r["BlockName"],
                    r["DwgCount"],
                    r["DisplayName"],
                    r["BlockName"],
                    r["FileName"],
                    r["FileExt"],
                    r["AssetDesc"],
                    r["BlockDesc"],
                    r["BlockMfrName"],
                    r["BlockVendorName"]
                );
            }

            // Bind — DataTable gives built-in column sorting for free
            var bs = new BindingSource { DataSource = _results };
            dgvResults.DataSource = bs;

            // Hide internal columns
            foreach (string col in new[] { "AssetID", "BlockID", "BlockName", "DwgCount" })
                if (dgvResults.Columns.Contains(col))
                    dgvResults.Columns[col].Visible = false;

            // Size columns
            dgvResults.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);

            lblStatus.Text    = $"{_results.Rows.Count} result{(_results.Rows.Count == 1 ? "" : "s")}";
            btnSearch.Enabled = true;
        }

        /// <summary>Runs on a background thread — no UI access.</summary>
        private static DataTable ExecuteSearch(string term)
        {
            const string sql = @"
                SELECT
                    a.ID                                            AS AssetID,
                    b.ID                                            AS BlockID,
                    b.BlockName,
                    ISNULL(NULLIF(b.BlockTag,''), b.BlockName)      AS DisplayName,
                    ISNULL(b.BlockDesc,  '')                        AS BlockDesc,
                    ISNULL(b.BlockMfrName,  '')                     AS BlockMfrName,
                    ISNULL(b.BlockVendorName,'')                    AS BlockVendorName,
                    ISNULL(a.FileName,   '')                        AS FileName,
                    ISNULL(a.FileType,   '')                        AS FileExt,
                    ISNULL(a.FileDescription,'')                    AS AssetDesc,
                    (SELECT COUNT(*) FROM dbo.Dwg_BlockAssets x
                     WHERE x.BlockID = b.ID
                       AND x.FileType = '.dwg')                     AS DwgCount
                FROM dbo.Dwg_Block b
                INNER JOIN dbo.Dwg_BlockAssets a ON a.BlockID = b.ID
                WHERE  b.BlockName       LIKE @q
                    OR b.BlockTag        LIKE @q
                    OR b.BlockDesc       LIKE @q
                    OR b.BlockNotes      LIKE @q
                    OR b.BlockMfrName    LIKE @q
                    OR b.BlockVendorName LIKE @q
                    OR b.BlockVendorNum  LIKE @q
                    OR a.FileName        LIKE @q
                    OR a.FileDescription LIKE @q
                ORDER BY DisplayName, a.FileName";

            var dt = new DataTable();
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@q", $"%{term}%");
            using var da = new SqlDataAdapter(cmd);
            da.Fill(dt);
            return dt;
        }

        // -----------------------------------------------------------------------
        // InitializeComponent
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            txtSearch  = new TextBox  { Dock = DockStyle.Fill, PlaceholderText = "Search blocks and assets…" };
            btnSearch  = new Button   { Text = "Search", Width = 88, Height = 28, Anchor = AnchorStyles.Right };
            btnClose   = new Button   { Text = "Close",  Width = 88, Height = 28, Anchor = AnchorStyles.Right };
            lblStatus  = new Label    { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "Enter a search term and press Search or Enter." };

            dgvResults = new DataGridView
            {
                Dock                      = DockStyle.Fill,
                ReadOnly                  = true,
                SelectionMode             = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect               = false,
                AllowUserToAddRows        = false,
                AllowUserToDeleteRows     = false,
                RowHeadersVisible         = false,
                AutoGenerateColumns       = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode       = DataGridViewAutoSizeColumnsMode.None,
                AllowUserToResizeColumns  = true,
                BackgroundColor           = SystemColors.Window,
                BorderStyle               = BorderStyle.None,
            };

            // Search bar (top): text box + search button
            var searchBar = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = 36,
                ColumnCount = 3, RowCount = 1,
                Padding = new Padding(6, 4, 6, 4)
            };
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            searchBar.Controls.Add(txtSearch,  0, 0);
            searchBar.Controls.Add(btnSearch,  1, 0);
            searchBar.Controls.Add(btnClose,   2, 0);

            // Status bar (bottom)
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(6, 2, 6, 2) };
            statusBar.Controls.Add(lblStatus);

            // Hint label above grid
            var lblHint = new Label
            {
                Dock = DockStyle.Top, Height = 20,
                Text = "Double-click a row to insert (DWG) or open (other types).",
                ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            Controls.Add(dgvResults);
            Controls.Add(lblHint);
            Controls.Add(searchBar);
            Controls.Add(statusBar);

            ClientSize      = new Size(1000, 600);
            MinimumSize     = new Size(700, 400);
            Text            = "Block & Asset Search";
            MinimizeBox     = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterScreen;
            CancelButton    = btnClose;
        }

        // -----------------------------------------------------------------------
        // Field declarations
        // -----------------------------------------------------------------------

        private TextBox       txtSearch  = null!;
        private Button        btnSearch  = null!;
        private Button        btnClose   = null!;
        private Label         lblStatus  = null!;
        private DataGridView  dgvResults = null!;
    }
}
