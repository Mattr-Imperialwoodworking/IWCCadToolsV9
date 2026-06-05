using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Add or edit a project material entry in dbo.Proj_Mat.
    ///
    /// Add mode  : new FrmProjectMaterialEditor(projectId, preselectedGroupId?)
    /// Edit mode : new FrmProjectMaterialEditor(projectId, itemId)   — loads full record from DB
    /// </summary>
    public sealed class FrmProjectMaterialEditor : IWCBaseForm
    {
        // -----------------------------------------------------------------------
        // Output properties
        // -----------------------------------------------------------------------

        public int?    SavedItemId  { get; private set; }
        public string  MatNo        => txtMatNo.Text.Trim();
        public string  MatDesc      => txtMatDesc.Text.Trim();
        public int?    MatGroupId   => (cboMatGroup.SelectedItem as MatGroupItem)?.Id;
        public string  MatGroupName => (cboMatGroup.SelectedItem as MatGroupItem)?.Name ?? string.Empty;
        public string  MatUnits     => txtMatUnits.Text.Trim();
        public string  MatNotes     => txtMatNotes.Text;
        public DateTime? MatApprove => chkApproved.Checked ? dtpApprove.Value.Date : null;

        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        private readonly bool _isEdit;
        private readonly int  _projectId;
        private readonly int  _editItemId;
        private readonly int? _preselectedGroupId;
        private byte[]?       _imageBytes;      // current Mat_Image bytes

        // -----------------------------------------------------------------------
        // Construction — Add mode
        // -----------------------------------------------------------------------

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
        // Construction — Edit mode (loads all fields from DB)
        // -----------------------------------------------------------------------

        public FrmProjectMaterialEditor(int projectId, int itemId)
        {
            _projectId  = projectId;
            _editItemId = itemId;
            _isEdit     = true;
            InitializeComponent();
            Text = "Edit Material";
            WireEvents();
        }

        // -----------------------------------------------------------------------
        // Load
        // -----------------------------------------------------------------------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _ = LoadFormDataAsync();
        }

        private async Task LoadFormDataAsync()
        {
            btnSave.Enabled     = false;
            cboMatGroup.Enabled = false;

            // --- Load groups ---
            List<MatGroupItem>? groups = null;
            MatRecord?          record = null;
            try
            {
                (groups, record) = await Task.Run(() => FetchFormData(_editItemId, _isEdit));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load form data:\n{ex.Message}",
                    "Material Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            // Populate group combo
            cboMatGroup.Items.Clear();
            foreach (var g in groups!)
                cboMatGroup.Items.Add(g);

            int? preselectGroup = _isEdit ? record?.MatGroup : _preselectedGroupId;
            if (preselectGroup.HasValue)
            {
                foreach (MatGroupItem item in cboMatGroup.Items)
                    if (item.Id == preselectGroup.Value) { cboMatGroup.SelectedItem = item; break; }
            }
            if (cboMatGroup.SelectedIndex < 0 && cboMatGroup.Items.Count > 0)
                cboMatGroup.SelectedIndex = 0;

            // Populate fields from existing record (edit mode)
            if (_isEdit && record != null)
            {
                txtMatNo.Text    = record.MatNo    ?? string.Empty;
                txtMatDesc.Text  = record.MatDesc  ?? string.Empty;
                txtMatUnits.Text = record.MatUnits ?? string.Empty;
                txtMatNotes.Text = record.MatNotes ?? string.Empty;

                if (record.MatApprove.HasValue)
                {
                    chkApproved.Checked  = true;
                    dtpApprove.Value     = record.MatApprove.Value;
                }

                if (record.ImageBytes != null && record.ImageBytes.Length > 0)
                {
                    _imageBytes = record.ImageBytes;
                    ShowImageBytes(_imageBytes);
                }
            }

            cboMatGroup.Enabled = true;
            btnSave.Enabled     = true;
        }

        private static (List<MatGroupItem> groups, MatRecord? record) FetchFormData(int itemId, bool loadRecord)
        {
            var groups = new List<MatGroupItem>();
            MatRecord? record = null;

            using var conn = IWCConn.GetSqlConnection();
            conn.Open();

            using (var cmd = new SqlCommand(
                "SELECT ID, MatGroup FROM dbo.Proj_MatGroup ORDER BY MatGroup ASC;", conn))
            using (var rdr = cmd.ExecuteReader())
                while (rdr.Read())
                    groups.Add(new MatGroupItem(rdr.GetInt32(0), rdr.GetString(1)));

            if (loadRecord && itemId > 0)
            {
                using var cmd2 = new SqlCommand(@"
                    SELECT MatNo, MatDesc, MatGroup, MatUnits, MatNotes, MatApprove, Mat_Image
                    FROM   dbo.Proj_Mat
                    WHERE  ID = @id;", conn);
                cmd2.Parameters.AddWithValue("@id", itemId);
                using var rdr2 = cmd2.ExecuteReader();
                if (rdr2.Read())
                {
                    record = new MatRecord
                    {
                        MatNo      = rdr2["MatNo"]      as string,
                        MatDesc    = rdr2["MatDesc"]    as string,
                        MatGroup   = rdr2["MatGroup"]   is int g  ? g                    : null,
                        MatUnits   = rdr2["MatUnits"]   as string,
                        MatNotes   = rdr2["MatNotes"]   as string,
                        MatApprove = rdr2["MatApprove"] is DateTime dt ? dt              : null,
                        ImageBytes = rdr2["Mat_Image"]  as byte[],
                    };
                }
            }

            return (groups, record);
        }

        // -----------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------

        private void WireEvents()
        {
            btnSave.Click    += BtnSave_Click;
            btnCancel.Click  += (_, _) => DialogResult = DialogResult.Cancel;
            chkApproved.CheckedChanged += (_, _) => dtpApprove.Enabled = chkApproved.Checked;
            btnBrowseImage.Click += BtnBrowseImage_Click;
            btnClearImage.Click  += (_, _) => { _imageBytes = null; pictImage.Image = null; };
        }

        private void BtnBrowseImage_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title  = "Select Material Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff|All Files|*.*"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                _imageBytes = File.ReadAllBytes(ofd.FileName);
                ShowImageBytes(_imageBytes);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load image:\n{ex.Message}", "Image",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowImageBytes(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                pictImage.Image = Image.FromStream(ms);
            }
            catch { pictImage.Image = null; }
        }

        // -----------------------------------------------------------------------
        // Save
        // -----------------------------------------------------------------------

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtMatNo.Text))
            {
                MessageBox.Show("Material Number is required.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                tabControl.SelectedIndex = 0; txtMatNo.Focus(); return;
            }
            if (string.IsNullOrWhiteSpace(txtMatDesc.Text))
            {
                MessageBox.Show("Material Description is required.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                tabControl.SelectedIndex = 0; txtMatDesc.Focus(); return;
            }
            if (cboMatGroup.SelectedItem is not MatGroupItem grp)
            {
                MessageBox.Show("Please select a Material Group.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                tabControl.SelectedIndex = 0; cboMatGroup.Focus(); return;
            }

            try
            {
                var args = new SaveArgs
                {
                    MatNo      = txtMatNo.Text.Trim(),
                    MatDesc    = txtMatDesc.Text.Trim(),
                    GroupId    = grp.Id,
                    MatUnits   = txtMatUnits.Text.Trim(),
                    MatNotes   = txtMatNotes.Text.Length == 0 ? null : txtMatNotes.Text,
                    MatApprove = chkApproved.Checked ? dtpApprove.Value.Date : null,
                    ImageBytes = _imageBytes,
                };

                if (_isEdit)
                    UpdateMaterial(_editItemId, args);
                else
                    SavedItemId = InsertMaterial(_projectId, args);

                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "Material Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static int InsertMaterial(int projectId, SaveArgs a)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Proj_Mat
                    (Proj_ID, MatNo, MatDesc, MatGroup, MatUnits,
                     MatNotes, MatApprove, Mat_Image, ItemUpdate)
                OUTPUT INSERTED.ID
                VALUES
                    (@pid, @no, @desc, @grp, @units,
                     @notes, @approve, @img, CAST(GETDATE() AS date));", conn);

            cmd.Parameters.AddWithValue("@pid",    projectId);
            cmd.Parameters.AddWithValue("@no",     Nv(a.MatNo));
            cmd.Parameters.AddWithValue("@desc",   Nv(a.MatDesc));
            cmd.Parameters.AddWithValue("@grp",    a.GroupId);
            cmd.Parameters.AddWithValue("@units",  Nv(a.MatUnits));
            cmd.Parameters.AddWithValue("@notes",  Nv(a.MatNotes));
            cmd.Parameters.AddWithValue("@approve",a.MatApprove.HasValue ? (object)a.MatApprove.Value : DBNull.Value);
            cmd.Parameters.Add("@img", System.Data.SqlDbType.Image).Value = (object?)a.ImageBytes ?? DBNull.Value;

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static void UpdateMaterial(int itemId, SaveArgs a)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Proj_Mat
                SET MatNo      = @no,
                    MatDesc    = @desc,
                    MatGroup   = @grp,
                    MatUnits   = @units,
                    MatNotes   = @notes,
                    MatApprove = @approve,
                    Mat_Image  = @img,
                    ItemUpdate = CAST(GETDATE() AS date)
                WHERE ID = @id;", conn);

            cmd.Parameters.AddWithValue("@id",     itemId);
            cmd.Parameters.AddWithValue("@no",     Nv(a.MatNo));
            cmd.Parameters.AddWithValue("@desc",   Nv(a.MatDesc));
            cmd.Parameters.AddWithValue("@grp",    a.GroupId);
            cmd.Parameters.AddWithValue("@units",  Nv(a.MatUnits));
            cmd.Parameters.AddWithValue("@notes",  Nv(a.MatNotes));
            cmd.Parameters.AddWithValue("@approve",a.MatApprove.HasValue ? (object)a.MatApprove.Value : DBNull.Value);
            cmd.Parameters.Add("@img", System.Data.SqlDbType.Image).Value = (object?)a.ImageBytes ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        private static object Nv(string? s) => string.IsNullOrEmpty(s) ? DBNull.Value : s;

        // -----------------------------------------------------------------------
        // InitializeComponent
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            // ---- Controls --------------------------------------------------------
            txtMatNo    = new TextBox { Dock = DockStyle.Fill };
            txtMatDesc  = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                AcceptsTab = false,
                WordWrap = true
            };
            cboMatGroup = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            txtMatUnits = new TextBox { Dock = DockStyle.Fill };

            txtMatNotes = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true,
                ScrollBars = ScrollBars.Vertical, AcceptsReturn = true
            };

            chkApproved = new CheckBox { Text = "Approved", AutoSize = true, Anchor = AnchorStyles.Left };
            dtpApprove  = new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Dock = DockStyle.Fill,
                Enabled = false
            };

            pictImage = new PictureBox
            {
                Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle, Height = 160
            };
            btnBrowseImage = new Button { Text = "Browse Image…", AutoSize = true };
            btnClearImage  = new Button { Text = "Clear",         AutoSize = true };

            btnSave   = new Button { Text = "Save",   Width = 88, Height = 28, Enabled = false };
            btnCancel = new Button { Text = "Cancel", Width = 88, Height = 28 };

            // ---- Tab: Basic fields -----------------------------------------------
            var tabBasic = new TabPage("Basic");
            var tblBasic = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
                Padding = new Padding(10, 10, 10, 4)
            };
            tblBasic.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tblBasic.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tblBasic.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Material No
            tblBasic.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // Description
            tblBasic.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Material Group
            tblBasic.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Units

            void AddBasic(int row, string lbl, Control ctl)
            {
                tblBasic.Controls.Add(new Label
                {
                    Text = lbl, AutoSize = true,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Margin = new Padding(0, 8, 6, 0)
                }, 0, row);
                tblBasic.Controls.Add(ctl, 1, row);
            }
            AddBasic(0, "Material No:",    txtMatNo);
            AddBasic(1, "Description:",    txtMatDesc);
            AddBasic(2, "Material Group:", cboMatGroup);
            AddBasic(3, "Units:",          txtMatUnits);
            tabBasic.Controls.Add(tblBasic);

            // ---- Tab: Details (notes + approval + image) -------------------------
            var tabDetails = new TabPage("Details");
            var tblDet = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
                Padding = new Padding(10, 10, 10, 4)
            };
            tblDet.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tblDet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tblDet.RowStyles.Add(new RowStyle(SizeType.Percent,  40));   // Notes
            tblDet.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Approved checkbox
            tblDet.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Approval date
            tblDet.RowStyles.Add(new RowStyle(SizeType.Percent,  60));   // Image

            tblDet.Controls.Add(new Label
            {
                Text = "Notes:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 6, 0)
            }, 0, 0);
            tblDet.Controls.Add(txtMatNotes, 1, 0);

            tblDet.Controls.Add(new Label(), 0, 1);   // blank label
            tblDet.Controls.Add(chkApproved, 1, 1);

            tblDet.Controls.Add(new Label
            {
                Text = "Approval Date:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 8, 6, 0)
            }, 0, 2);
            tblDet.Controls.Add(dtpApprove, 1, 2);

            // Image row: label + picture + buttons stacked
            tblDet.Controls.Add(new Label
            {
                Text = "Image:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 6, 0)
            }, 0, 3);

            var imgPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
            };
            imgPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            imgPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            var imgBtnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                AutoSize = false
            };
            imgBtnRow.Controls.Add(btnBrowseImage);
            imgBtnRow.Controls.Add(btnClearImage);

            imgPanel.Controls.Add(pictImage,   0, 0);
            imgPanel.Controls.Add(imgBtnRow,   0, 1);
            tblDet.Controls.Add(imgPanel, 1, 3);

            tabDetails.Controls.Add(tblDet);

            // ---- TabControl ------------------------------------------------------
            tabControl = new TabControl { Dock = DockStyle.Fill };
            tabControl.TabPages.Add(tabBasic);
            tabControl.TabPages.Add(tabDetails);

            // ---- Button strip ----------------------------------------------------
            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 6, 8, 0)
            };
            btnPanel.Controls.Add(btnSave);
            btnPanel.Controls.Add(btnCancel);

            Controls.Add(tabControl);
            Controls.Add(btnPanel);

            ClientSize      = new Size(500, 440);
            MinimumSize     = new Size(420, 380);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox     = false;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            AcceptButton    = btnSave;
            CancelButton    = btnCancel;
        }

        // -----------------------------------------------------------------------
        // Field declarations
        // -----------------------------------------------------------------------

        private TabControl   tabControl     = null!;
        private TextBox      txtMatNo       = null!;
        private TextBox      txtMatDesc     = null!;
        private ComboBox     cboMatGroup    = null!;
        private TextBox      txtMatUnits    = null!;
        private TextBox      txtMatNotes    = null!;
        private CheckBox     chkApproved    = null!;
        private DateTimePicker dtpApprove   = null!;
        private PictureBox   pictImage      = null!;
        private Button       btnBrowseImage = null!;
        private Button       btnClearImage  = null!;
        private Button       btnSave        = null!;
        private Button       btnCancel      = null!;

        // -----------------------------------------------------------------------
        // Private helper types
        // -----------------------------------------------------------------------

        private sealed class MatGroupItem
        {
            public int    Id   { get; }
            public string Name { get; }
            public MatGroupItem(int id, string name) { Id = id; Name = name; }
            public override string ToString() => Name;
        }

        private sealed class MatRecord
        {
            public string?   MatNo      { get; init; }
            public string?   MatDesc    { get; init; }
            public int?      MatGroup   { get; init; }
            public string?   MatUnits   { get; init; }
            public string?   MatNotes   { get; init; }
            public DateTime? MatApprove { get; init; }
            public byte[]?   ImageBytes { get; init; }
        }

        private sealed class SaveArgs
        {
            public string?   MatNo      { get; init; }
            public string?   MatDesc    { get; init; }
            public int       GroupId    { get; init; }
            public string?   MatUnits   { get; init; }
            public string?   MatNotes   { get; init; }
            public DateTime? MatApprove { get; init; }
            public byte[]?   ImageBytes { get; init; }
        }
    }
}
