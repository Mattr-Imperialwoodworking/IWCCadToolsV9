using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Modal dialog for selecting the active Drawing Series (Dash) for a project.
    /// Displays a flat DataGridView with bold parent rows and indented child rows.
    /// The selected Dash ID is exposed via <see cref="SelectedID"/>.
    /// </summary>
    public partial class ProjDashSelect : IWCBaseForm
    {
        public int? SelectedID { get; private set; }

        private readonly int _projId;

        // ---------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------

        public ProjDashSelect(int projId)
        {
            InitializeComponent();
            StartPosition = FormStartPosition.CenterScreen;
            _projId = projId;

            dataGridView1.CellFormatting     += dataGridView1_CellFormatting;
            dataGridView1.DataError          += (s, e) => e.ThrowException = false;
            dataGridView1.DataBindingComplete += (s, e) => ApplyDescWidth();
            dataGridView1.Resize             += (s, e) => ApplyDescWidth();

            LoadDashData();
            ApplyDescWidth();
        }

        // ---------------------------------------------------------------------------
        // Data load
        // ---------------------------------------------------------------------------

        private void LoadDashData()
        {
            using var conn = new IWCConn();
            conn.DBConnect();

            var dt = new DataTable();
            using (var cmd = new SqlCommand(
                "SELECT ID, ID_Num, Dash_Desc, Dash_Type " +
                "FROM dbo.Proj_DashCompile " +
                "WHERE Proj_ID = @ProjID ORDER BY ID_Num ASC", conn.OpenConn))
            {
                cmd.Parameters.AddWithValue("@ProjID", _projId);
                using var da = new SqlDataAdapter(cmd);
                da.Fill(dt);
            }

            dataGridView1.AutoGenerateColumns = true;
            dataGridView1.DataSource = dt;

            // Hide PK, set friendly headers
            if (dataGridView1.Columns.Contains("ID"))
                dataGridView1.Columns["ID"].Visible = false;
            if (dataGridView1.Columns.Contains("ID_Num"))
                dataGridView1.Columns["ID_Num"].HeaderText = "ID Number";
            if (dataGridView1.Columns.Contains("Dash_Desc"))
                dataGridView1.Columns["Dash_Desc"].HeaderText = "Dash Description";
            if (dataGridView1.Columns.Contains("Dash_Type"))
                dataGridView1.Columns["Dash_Type"].HeaderText = "Type";

            dataGridView1.AutoResizeColumns();
            ApplyDescWidth();
        }

        // ---------------------------------------------------------------------------
        // Cell formatting – bold parents, indented children
        // ---------------------------------------------------------------------------

        private void dataGridView1_CellFormatting(object? sender,  DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;

            var row      = dataGridView1.Rows[e.RowIndex];
            int dashType = CoerceDashType(row.DataBoundItem);

            switch (dashType)
            {
                case 2: // Series (parent)
                    row.DefaultCellStyle.BackColor = Color.White;
                    row.DefaultCellStyle.Font      = new Font(dataGridView1.Font, FontStyle.Bold);
                    break;

                case 1: // Component (child) – indent first cell
                    row.DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
                    row.DefaultCellStyle.Font      = dataGridView1.Font;
                    if (dataGridView1.Columns.Contains("ID_Num") && e.ColumnIndex ==
                        dataGridView1.Columns["ID_Num"].Index)
                    {
                        e.Value          = "  " + (e.Value?.ToString() ?? string.Empty);
                        e.FormattingApplied = true;
                    }
                    break;

                default:
                    row.DefaultCellStyle.BackColor = Color.White;
                    row.DefaultCellStyle.Font      = dataGridView1.Font;
                    break;
            }
        }

        // ---------------------------------------------------------------------------
        // Button handlers
        // ---------------------------------------------------------------------------

        private void btnOK_Click(object? sender,  EventArgs e)
        {
            if (dataGridView1.CurrentRow?.Cells["ID"] is { } cell)
            {
                SelectedID   = Convert.ToInt32(cell.Value);
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void btnCancel_Click(object? sender,  EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private void ApplyDescWidth()
        {
            if (!dataGridView1.Columns.Contains("Dash_Desc")) return;
            using var g = dataGridView1.CreateGraphics();
            int min = (int)Math.Round(3.0 * g.DpiX); // 3 inches at current DPI
            var col = dataGridView1.Columns["Dash_Desc"];
            col.AutoSizeMode  = DataGridViewAutoSizeColumnMode.None;
            col.MinimumWidth  = min;
            if (col.Width < min) col.Width = min;
        }

        private static int CoerceDashType(object? dataBoundItem)
        {
            object? raw = null;

            if (dataBoundItem is DataRowView drv)
                raw = drv["Dash_Type"];
            else
            {
                var prop = dataBoundItem?.GetType().GetProperty("Dash_Type");
                raw = prop?.GetValue(dataBoundItem);
            }

            if (raw == null || raw == DBNull.Value) return 0;
            if (raw is int i)    return i;
            if (raw is long l)   return (int)l;
            if (raw is short s)  return s;
            if (raw is byte b)   return b;
            if (raw is string str)
            {
                if (int.TryParse(str.Trim(), out var n)) return n;
                var t = str.Trim().ToLowerInvariant();
                if (t is "2" or "series" or "parent")    return 2;
                if (t is "1" or "component" or "child")  return 1;
            }
            return 0;
        }

        // ---------------------------------------------------------------------------
        // Designer-generated members
        // ---------------------------------------------------------------------------

        private void InitializeComponent()
        {
            dataGridView1 = new DataGridView();
            btnOK         = new Button();
            btnCancel     = new Button();

            ((System.ComponentModel.ISupportInitialize)dataGridView1).BeginInit();
            SuspendLayout();

            dataGridView1.Dock                     = DockStyle.Fill;
            dataGridView1.ReadOnly                  = true;
            dataGridView1.SelectionMode             = DataGridViewSelectionMode.FullRowSelect;
            dataGridView1.MultiSelect               = false;
            dataGridView1.AllowUserToAddRows        = false;
            dataGridView1.AllowUserToDeleteRows     = false;
            dataGridView1.RowHeadersVisible         = false;
            dataGridView1.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;

            btnOK.Text         = "OK";
            btnOK.Width        = 90;
            btnOK.Height       = 30;
            btnOK.Anchor       = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOK.Click       += btnOK_Click;

            btnCancel.Text     = "Cancel";
            btnCancel.Width    = 90;
            btnCancel.Height   = 30;
            btnCancel.Anchor   = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancel.Click   += btnCancel_Click;

            var panel = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            btnOK.Location     = new Point(panel.Width - 196, 7);
            btnCancel.Location = new Point(panel.Width - 98,  7);
            btnOK.Anchor       = AnchorStyles.Right | AnchorStyles.Bottom;
            btnCancel.Anchor   = AnchorStyles.Right | AnchorStyles.Bottom;
            panel.Controls.AddRange(new Control[] { btnOK, btnCancel });

            ClientSize = new System.Drawing.Size(620, 460);
            Controls.Add(dataGridView1);
            Controls.Add(panel);
            Text = "Select Drawing Series";

            ((System.ComponentModel.ISupportInitialize)dataGridView1).EndInit();
            ResumeLayout(false);
        }

        private DataGridView dataGridView1 = null!;
        private Button       btnOK         = null!;
        private Button       btnCancel     = null!;
    }
}
