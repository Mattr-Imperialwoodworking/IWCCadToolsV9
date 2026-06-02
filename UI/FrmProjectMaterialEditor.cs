using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Add or edit a project material entry in dbo.Proj_MatCompile.
    /// </summary>
    public sealed class FrmProjectMaterialEditor : IWCBaseForm
    {
        // -----------------------------------------------------------------------
        // Mode
        // -----------------------------------------------------------------------

        private readonly bool _isEdit;

        // -----------------------------------------------------------------------
        // Output properties
        // -----------------------------------------------------------------------

        public int?   SavedItemId  { get; private set; }
        public string MatNo        => txtMatNo.Text.Trim();
        public string MatDesc      => txtMatDesc.Text.Trim();
        public int?   MatGroupId   => (cboMatGroup.SelectedItem as MatGroupItem)?.Id;
        public string MatGroupName => (cboMatGroup.SelectedItem as MatGroupItem)?.Name ?? string.Empty;

        // -----------------------------------------------------------------------
        // Construction — Add mode
        // -----------------------------------------------------------------------

        /// <param name="projectId">Project the material belongs to.</param>
        /// <param name="preselectedGroupId">Optional group to pre-select in the dropdown.</param>
        public FrmProjectMaterialEditor(int projectId, int? preselectedGroupId = null)
        {
            _projectId          = projectId;
            _preselectedGroupId = preselectedGroupId;
            _isEdit             = false;
            InitializeComponent();
            Text = "Add New Material";
            WireEvents();
        }

        // -----------------------------------------------------------------------
        // Construction — Edit mode
        // -----------------------------------------------------------------------

        /// <param name="projectId">Project the material belongs to.</param>
        /// <param name="itemId">ID of the material record to edit.</param>
        /// <param name="matNo">Existing material number.</param>
        /// <param name="matDesc">Existing material description.</param>
        /// <param name="groupId">Existing group ID.</param>
        public FrmProjectMaterialEditor(int projectId, int itemId,
            string? matNo, string? matDesc, int groupId)
        {
            _projectId          = projectId;
            _editItemId         = itemId;
            _preselectedGroupId = groupId;
            _isEdit             = true;
            InitializeComponent();
            Text             = "Edit Material";
            txtMatNo.Text    = matNo   ?? string.Empty;
            txtMatDesc.Text  = matDesc ?? string.Empty;
            WireEvents();
        }

        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        private readonly int  _projectId;
        private readonly int  _editItemId;
        private readonly int? _preselectedGroupId;

        // -----------------------------------------------------------------------
        // Load
        // -----------------------------------------------------------------------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _ = LoadGroupsAsync();
        }

        private async Task LoadGroupsAsync()
        {
            cboMatGroup.Enabled = false;
            btnSave.Enabled     = false;

            List<MatGroupItem>? groups = null;
            try
            {
                groups = await Task.Run(FetchGroups);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load material groups:\n{ex.Message}",
                    "Material Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            cboMatGroup.Items.Clear();
            foreach (var g in groups!)
                cboMatGroup.Items.Add(g);

            // Pre-select
            if (_preselectedGroupId.HasValue)
            {
                foreach (MatGroupItem item in cboMatGroup.Items)
                {
                    if (item.Id == _preselectedGroupId.Value)
                    {
                        cboMatGroup.SelectedItem = item;
                        break;
                    }
                }
            }
            if (cboMatGroup.SelectedIndex < 0 && cboMatGroup.Items.Count > 0)
                cboMatGroup.SelectedIndex = 0;

            cboMatGroup.Enabled = true;
            btnSave.Enabled     = true;
        }

        private static List<MatGroupItem> FetchGroups()
        {
            var list = new List<MatGroupItem>();
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT ID, MatGroup FROM dbo.Proj_MatGroup ORDER BY MatGroup ASC;", conn);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                list.Add(new MatGroupItem(rdr.GetInt32(0), rdr.GetString(1)));
            return list;
        }

        // -----------------------------------------------------------------------
        // Save
        // -----------------------------------------------------------------------

        private void WireEvents()
        {
            btnSave.Click   += BtnSave_Click;
            btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtMatNo.Text))
            {
                MessageBox.Show("Material Number is required.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtMatNo.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(txtMatDesc.Text))
            {
                MessageBox.Show("Material Description is required.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtMatDesc.Focus();
                return;
            }
            if (cboMatGroup.SelectedItem is not MatGroupItem grp)
            {
                MessageBox.Show("Please select a Material Group.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                cboMatGroup.Focus();
                return;
            }

            try
            {
                if (_isEdit)
                    UpdateMaterial(_editItemId, txtMatNo.Text.Trim(), txtMatDesc.Text.Trim(), grp.Id);
                else
                    SavedItemId = InsertMaterial(_projectId, txtMatNo.Text.Trim(), txtMatDesc.Text.Trim(), grp.Id);

                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "Material Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static int InsertMaterial(int projectId, string matNo, string matDesc, int groupId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Proj_MatCompile (ProjID, MatNo, MatDesc, MatGroup)
                OUTPUT INSERTED.ID
                VALUES (@pid, @no, @desc, @grp);", conn);
            cmd.Parameters.AddWithValue("@pid",  projectId);
            cmd.Parameters.AddWithValue("@no",   matNo);
            cmd.Parameters.AddWithValue("@desc", matDesc);
            cmd.Parameters.AddWithValue("@grp",  groupId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static void UpdateMaterial(int itemId, string matNo, string matDesc, int groupId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Proj_MatCompile
                SET MatNo    = @no,
                    MatDesc  = @desc,
                    MatGroup = @grp
                WHERE ID = @id;", conn);
            cmd.Parameters.AddWithValue("@id",   itemId);
            cmd.Parameters.AddWithValue("@no",   matNo);
            cmd.Parameters.AddWithValue("@desc", matDesc);
            cmd.Parameters.AddWithValue("@grp",  groupId);
            cmd.ExecuteNonQuery();
        }

        // -----------------------------------------------------------------------
        // InitializeComponent
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            txtMatNo   = new TextBox { Dock = DockStyle.Fill };
            txtMatDesc = new TextBox { Dock = DockStyle.Fill };
            cboMatGroup= new ComboBox
            {
                Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList
            };
            btnSave    = new Button { Text = "Save",   Width = 88, Height = 28, Enabled = false };
            btnCancel  = new Button { Text = "Cancel", Width = 88, Height = 28 };

            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
                Padding = new Padding(12, 12, 12, 4)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            void AddRow(int row, string label, System.Windows.Forms.Control ctl)
            {
                tbl.Controls.Add(new Label
                {
                    Text = label, AutoSize = true,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Margin = new Padding(0, 8, 6, 0)
                }, 0, row);
                tbl.Controls.Add(ctl, 1, row);
            }

            AddRow(0, "Material No:",   txtMatNo);
            AddRow(1, "Description:",   txtMatDesc);
            AddRow(2, "Material Group:", cboMatGroup);

            var btnPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                Height        = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(8, 6, 8, 0)
            };
            btnPanel.Controls.Add(btnSave);
            btnPanel.Controls.Add(btnCancel);

            Controls.Add(tbl);
            Controls.Add(btnPanel);

            ClientSize      = new System.Drawing.Size(420, 180);
            MinimumSize     = new System.Drawing.Size(360, 180);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox     = false;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            AcceptButton    = btnSave;
            CancelButton    = btnCancel;
        }

        // -----------------------------------------------------------------------
        // Field declarations
        // -----------------------------------------------------------------------

        private TextBox   txtMatNo    = null!;
        private TextBox   txtMatDesc  = null!;
        private ComboBox  cboMatGroup = null!;
        private Button    btnSave     = null!;
        private Button    btnCancel   = null!;

        // -----------------------------------------------------------------------
        // Helper type for ComboBox items
        // -----------------------------------------------------------------------

        private sealed class MatGroupItem
        {
            public int    Id   { get; }
            public string Name { get; }
            public MatGroupItem(int id, string name) { Id = id; Name = name; }
            public override string ToString() => Name;
        }
    }
}
