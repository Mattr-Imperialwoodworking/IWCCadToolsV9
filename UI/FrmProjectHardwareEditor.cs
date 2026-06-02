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
    /// Add or edit a project hardware entry in dbo.Proj_Hdw.
    ///
    /// Add mode  : new FrmProjectHardwareEditor(projectId, preselectedGroupId?, preselectedGroupTag?)
    /// Edit mode : new FrmProjectHardwareEditor(projectId, itemId)  — loads full record from DB
    ///
    /// In Add mode the HdwNo is auto-generated on save as {GroupTag}{seq:D2}
    /// (e.g. H01 → H0101, H0102, …) based on the highest existing number for that group.
    /// </summary>
    public sealed class FrmProjectHardwareEditor : IWCBaseForm
    {
        // -----------------------------------------------------------------------
        // Output properties
        // -----------------------------------------------------------------------

        public int?    SavedItemId  { get; private set; }
        public string  HdwNo       => txtHdwNo.Text.Trim();
        public string  HdwDesc     => txtHdwDesc.Text.Trim();
        public int?    HdwGroupId  => (cboGroup.SelectedItem as HdwGroupItem)?.Id;
        public string  HdwUnit     => txtHdwUnit.Text.Trim();

        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        private readonly bool  _isEdit;
        private readonly int   _projectId;
        private readonly int   _editItemId;
        private readonly int?  _preselectedGroupId;
        private readonly string? _preselectedGroupTag;
        private byte[]? _imageBytes;

        // -----------------------------------------------------------------------
        // Construction — Add mode
        // -----------------------------------------------------------------------

        public FrmProjectHardwareEditor(int projectId,
            int? preselectedGroupId = null, string? preselectedGroupTag = null)
        {
            _projectId           = projectId;
            _preselectedGroupId  = preselectedGroupId;
            _preselectedGroupTag = preselectedGroupTag;
            _isEdit              = false;
            InitializeComponent();
            Text = "Add New Hardware";
            WireEvents();
        }

        // -----------------------------------------------------------------------
        // Construction — Edit mode (loads all fields from DB)
        // -----------------------------------------------------------------------

        public FrmProjectHardwareEditor(int projectId, int itemId)
        {
            _projectId  = projectId;
            _editItemId = itemId;
            _isEdit     = true;
            InitializeComponent();
            Text = "Edit Hardware";
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
            btnSave.Enabled    = false;
            cboGroup.Enabled   = false;
            cboVendor.Enabled  = false;

            FormData? data = null;
            try
            {
                data = await Task.Run(() => FetchFormData(_editItemId, _isEdit));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load form data:\n{ex.Message}",
                    "Hardware Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            // Groups
            cboGroup.Items.Clear();
            foreach (var g in data!.Groups)
                cboGroup.Items.Add(g);

            int? preselectGroup = _isEdit ? data.Record?.HdwGroup : _preselectedGroupId;
            if (preselectGroup.HasValue)
                foreach (HdwGroupItem g in cboGroup.Items)
                    if (g.Id == preselectGroup.Value) { cboGroup.SelectedItem = g; break; }
            if (cboGroup.SelectedIndex < 0 && cboGroup.Items.Count > 0)
                cboGroup.SelectedIndex = 0;

            // Vendors
            cboVendor.Items.Clear();
            cboVendor.Items.Add(new VendorItem(null, "(none)"));
            foreach (var v in data.Vendors)
                cboVendor.Items.Add(v);
            cboVendor.SelectedIndex = 0;

            // Populate existing record in edit mode
            if (_isEdit && data.Record != null)
            {
                var r = data.Record;
                txtHdwNo.Text   = r.HdwNo    ?? string.Empty;
                txtHdwDesc.Text = r.HdwDesc  ?? string.Empty;
                txtHdwUnit.Text = r.HdwUnit  ?? string.Empty;
                txtHdwNotes.Text= r.HdwNotes ?? string.Empty;
                txtVendorNum.Text = r.HdwVendorNum  ?? string.Empty;
                txtVendorLink.Text= r.HdwVendorLink ?? string.Empty;
                chkByIWC.Checked = r.HdwByIWC;

                if (r.HdwApprove.HasValue)
                {
                    chkApproved.Checked = true;
                    dtpApprove.Value    = r.HdwApprove.Value;
                }

                if (r.HdwVendorId.HasValue)
                    foreach (VendorItem v in cboVendor.Items)
                        if (v.Id == r.HdwVendorId.Value) { cboVendor.SelectedItem = v; break; }

                if (r.ImageBytes?.Length > 0)
                {
                    _imageBytes = r.ImageBytes;
                    ShowImageBytes(_imageBytes);
                }
            }
            else
            {
                // Add mode — query DB for the real next number
                await RefreshHdwNoPreviewAsync();
            }

            btnSave.Enabled   = true;
            cboGroup.Enabled  = true;
            cboVendor.Enabled = true;
        }

        /// <summary>
        /// Queries the DB for the next available HdwNo for the currently
        /// selected group and writes it into the (read-only) txtHdwNo field.
        /// Called on initial load and whenever the group selection changes.
        /// </summary>
        private async Task RefreshHdwNoPreviewAsync()
        {
            if (_isEdit) return;
            if (cboGroup.SelectedItem is not HdwGroupItem grp)
            {
                txtHdwNo.Text = "(select a group)";
                return;
            }

            txtHdwNo.Text = "…";
            btnSave.Enabled = false;

            string next;
            int pid = _projectId;
            try
            {
                next = await Task.Run(() => ComputeNextHdwNo(grp.Id, grp.GroupTag, pid));
            }
            catch (InvalidOperationException ex)
            {
                next = $"(full: {ex.Message.Split('(')[0].Trim()})";
                btnSave.Enabled = false;   // can't add more to this group
                txtHdwNo.Text   = next;
                return;
            }
            catch
            {
                next = $"{grp.GroupTag}01";   // safe fallback on connection error
            }

            txtHdwNo.Text   = next;
            btnSave.Enabled = true;
        }

        private static string ComputeNextHdwNo(int groupId, string groupTag,
            int projectId = 0)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();

            // Filter by both project and group so numbers are scoped per project.
            // Numbers 50+ are reserved for stock hardware — only consider 01-48.
            using var cmd = new SqlCommand(@"
                SELECT HdwNo
                FROM   dbo.Proj_Hdw
                WHERE  Proj_ID   = @pid
                  AND  HdwGroup  = @gid;", conn);
            cmd.Parameters.AddWithValue("@pid", projectId);
            cmd.Parameters.AddWithValue("@gid", groupId);

            int maxSeq = 0;
            using (var rdr = cmd.ExecuteReader())
                while (rdr.Read())
                {
                    string? no = rdr[0] as string;
                    if (no == null) continue;
                    if (no.StartsWith(groupTag, StringComparison.OrdinalIgnoreCase))
                    {
                        string suffix = no.Substring(groupTag.Length);
                        if (int.TryParse(suffix, out int seq) && seq < 50)  // ignore reserved ≥50
                            maxSeq = Math.Max(maxSeq, seq);
                    }
                }

            int next = maxSeq + 1;
            if (next >= 50)
                throw new InvalidOperationException(
                    $"No available hardware numbers remain in group {groupTag} " +
                    $"for this project (numbers 01–48 are all in use; 50+ are reserved for stock).");

            return groupTag + (next < 10 ? $"0{next}" : next.ToString());
        }

        private static FormData FetchFormData(int itemId, bool loadRecord)
        {
            var data = new FormData();
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();

            // Groups
            using (var cmd = new SqlCommand(@"
                SELECT ID, HdwGroupTag, HdwGroup, HdwGroupDesc
                FROM dbo.Proj_HdwGroup
                ORDER BY HdwGroupTag, HdwGroup;", conn))
            using (var rdr = cmd.ExecuteReader())
                while (rdr.Read())
                    data.Groups.Add(new HdwGroupItem(
                        rdr.GetInt32(0),
                        rdr["HdwGroupTag"] as string ?? "",
                        rdr["HdwGroup"]    as string ?? "",
                        rdr["HdwGroupDesc"] as string ?? ""));

            // Vendors/Manufacturers from Cont_Comp
            using (var cmd = new SqlCommand(@"
                SELECT ID, Name_Comp, Comp_Type
                FROM   dbo.Cont_Comp
                WHERE  Comp_Type IN ('Vendor', 'Manufacturer')
                ORDER BY Name_Comp;", conn))
            using (var rdr = cmd.ExecuteReader())
                while (rdr.Read())
                    data.Vendors.Add(new VendorItem(
                        rdr.GetInt32(0),
                        rdr["Name_Comp"] as string ?? "(unknown)",
                        rdr["Comp_Type"] as string ?? ""));

            // Full record for edit mode
            if (loadRecord && itemId > 0)
            {
                using var cmd2 = new SqlCommand(@"
                    SELECT HdwNo, HdwDesc, HdwGroup, HdwUnit, HdwNotes,
                           HdwApprove, HdwVendorID, HdwVendorNum, HdwVendorlink,
                           HdwByIWC, HdwImage
                    FROM   dbo.Proj_Hdw
                    WHERE  ID = @id;", conn);
                cmd2.Parameters.AddWithValue("@id", itemId);
                using var rdr2 = cmd2.ExecuteReader();
                if (rdr2.Read())
                {
                    data.Record = new HdwRecord
                    {
                        HdwNo        = rdr2["HdwNo"]       as string,
                        HdwDesc      = rdr2["HdwDesc"]      as string,
                        HdwGroup     = rdr2["HdwGroup"]     is int g  ? g    : null,
                        HdwUnit      = rdr2["HdwUnit"]      as string,
                        HdwNotes     = rdr2["HdwNotes"]     as string,
                        HdwApprove   = rdr2["HdwApprove"]   is DateTime dt ? dt : null,
                        HdwVendorId  = rdr2["HdwVendorID"]  is int v  ? v    : null,
                        HdwVendorNum = rdr2["HdwVendorNum"] as string,
                        HdwVendorLink= rdr2["HdwVendorlink"] as string,
                        HdwByIWC     = rdr2["HdwByIWC"]     is bool b && b,
                        ImageBytes   = rdr2["HdwImage"]     as byte[],
                    };
                }
            }

            return data;
        }

        // -----------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------

        private void WireEvents()
        {
            btnSave.Click    += BtnSave_Click;
            btnCancel.Click  += (_, _) => DialogResult = DialogResult.Cancel;

            chkApproved.CheckedChanged += (_, _) => dtpApprove.Enabled = chkApproved.Checked;
            cboGroup.SelectedIndexChanged += (_, _) => _ = RefreshHdwNoPreviewAsync();

            btnBrowseImage.Click += BtnBrowseImage_Click;
            btnClearImage.Click  += (_, _) => { _imageBytes = null; pictImage.Image = null; };
        }

        private void BtnBrowseImage_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title  = "Select Hardware Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All Files|*.*"
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
            try { using var ms = new MemoryStream(bytes); pictImage.Image = Image.FromStream(ms); }
            catch { pictImage.Image = null; }
        }

        // -----------------------------------------------------------------------
        // Save
        // -----------------------------------------------------------------------

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtHdwDesc.Text))
            {
                MessageBox.Show("Hardware Description is required.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                tabControl.SelectedIndex = 0; txtHdwDesc.Focus(); return;
            }
            if (cboGroup.SelectedItem is not HdwGroupItem grp)
            {
                MessageBox.Show("Please select a Hardware Group.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                tabControl.SelectedIndex = 0; cboGroup.Focus(); return;
            }

            var vendorItem = cboVendor.SelectedItem as VendorItem;

            var args = new SaveArgs
            {
                HdwNo        = _isEdit ? txtHdwNo.Text.Trim() : txtHdwNo.Text.Trim(),  // always use displayed value
                HdwDesc      = txtHdwDesc.Text.Trim(),
                GroupId      = grp.Id,
                GroupTag     = grp.GroupTag,
                HdwUnit      = txtHdwUnit.Text.Trim(),
                HdwNotes     = txtHdwNotes.Text.Length == 0 ? null : txtHdwNotes.Text,
                HdwApprove   = chkApproved.Checked ? dtpApprove.Value.Date : null,
                HdwVendorId  = vendorItem?.Id,
                HdwVendorNum = txtVendorNum.Text.Trim(),
                HdwVendorLink= txtVendorLink.Text.Trim(),
                HdwByIWC     = chkByIWC.Checked,
                ImageBytes   = _imageBytes,
            };

            try
            {
                if (_isEdit)
                {
                    var editArgs = new SaveArgs
                    {
                        HdwNo        = txtHdwNo.Text.Trim(),
                        HdwDesc      = args.HdwDesc,
                        GroupId      = args.GroupId,
                        GroupTag     = args.GroupTag,
                        HdwUnit      = args.HdwUnit,
                        HdwNotes     = args.HdwNotes,
                        HdwApprove   = args.HdwApprove,
                        HdwVendorId  = args.HdwVendorId,
                        HdwVendorNum = args.HdwVendorNum,
                        HdwVendorLink= args.HdwVendorLink,
                        HdwByIWC     = args.HdwByIWC,
                        ImageBytes   = args.ImageBytes,
                    };
                    UpdateHardware(_editItemId, editArgs);
                }
                else
                {
                    (SavedItemId, var generatedNo) = InsertHardware(_projectId, args);
                    txtHdwNo.Text = generatedNo;   // show generated number
                }
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "Hardware Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // -----------------------------------------------------------------------
        // SQL
        // -----------------------------------------------------------------------

        private static (int newId, string hdwNo) InsertHardware(int projectId, SaveArgs a)
        {
            // HdwNo was computed and shown in the dialog at load time — use it directly.
            string hdwNo = a.HdwNo ?? ComputeNextHdwNo(a.GroupId, a.GroupTag ?? "H", projectId);

            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Proj_Hdw
                    (Proj_ID, HdwNo, HdwDesc, HdwGroup, HdwUnit,
                     HdwNotes, HdwApprove, HdwVendorID, HdwVendorNum, HdwVendorlink,
                     HdwByIWC, HdwImage, HdwEdit)
                OUTPUT INSERTED.ID
                VALUES
                    (@pid, @no, @desc, @grp, @unit,
                     @notes, @approve, @vid, @vnum, @vlink,
                     @byiwc, @img, CAST(GETDATE() AS date));", conn);

            AddSqlParams(cmd, hdwNo, a);
            cmd.Parameters.AddWithValue("@pid", projectId);

            int newId = Convert.ToInt32(cmd.ExecuteScalar());
            return (newId, hdwNo);
        }

        private static void UpdateHardware(int itemId, SaveArgs a)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Proj_Hdw
                SET HdwNo        = @no,
                    HdwDesc      = @desc,
                    HdwGroup     = @grp,
                    HdwUnit      = @unit,
                    HdwNotes     = @notes,
                    HdwApprove   = @approve,
                    HdwVendorID  = @vid,
                    HdwVendorNum = @vnum,
                    HdwVendorlink= @vlink,
                    HdwByIWC     = @byiwc,
                    HdwImage     = @img,
                    HdwEdit      = CAST(GETDATE() AS date)
                WHERE ID = @id;", conn);
            cmd.Parameters.AddWithValue("@id", itemId);
            AddSqlParams(cmd, a.HdwNo ?? string.Empty, a);
            cmd.ExecuteNonQuery();
        }

        private static void AddSqlParams(SqlCommand cmd, string hdwNo, SaveArgs a)
        {
            cmd.Parameters.AddWithValue("@no",     Nv(hdwNo));
            cmd.Parameters.AddWithValue("@desc",   Nv(a.HdwDesc));
            cmd.Parameters.AddWithValue("@grp",    a.GroupId);
            cmd.Parameters.AddWithValue("@unit",   Nv(a.HdwUnit));
            cmd.Parameters.AddWithValue("@notes",  Nv(a.HdwNotes));
            cmd.Parameters.AddWithValue("@approve",a.HdwApprove.HasValue ? (object)a.HdwApprove.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@vid",    a.HdwVendorId.HasValue ? (object)a.HdwVendorId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@vnum",   Nv(a.HdwVendorNum));
            cmd.Parameters.AddWithValue("@vlink",  Nv(a.HdwVendorLink));
            cmd.Parameters.AddWithValue("@byiwc",  a.HdwByIWC);
            cmd.Parameters.Add("@img", System.Data.SqlDbType.Image).Value = (object?)a.ImageBytes ?? DBNull.Value;
        }


        private static object Nv(string? s) => string.IsNullOrEmpty(s) ? DBNull.Value : (object)s;

        // -----------------------------------------------------------------------
        // InitializeComponent
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            txtHdwNo    = new TextBox { Dock = DockStyle.Fill, ReadOnly = true,
                                        BackColor = SystemColors.Control };
            txtHdwDesc  = new TextBox { Dock = DockStyle.Fill };
            cboGroup    = new ComboBox { Dock = DockStyle.Fill,
                                         DropDownStyle = ComboBoxStyle.DropDownList };
            txtHdwUnit  = new TextBox { Dock = DockStyle.Fill };
            txtHdwNotes = new TextBox { Dock = DockStyle.Fill, Multiline = true,
                                        ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
            chkApproved = new CheckBox { Text = "Approved", AutoSize = true,
                                          Anchor = AnchorStyles.Left };
            dtpApprove  = new DateTimePicker { Format = DateTimePickerFormat.Short,
                                               Dock = DockStyle.Fill, Enabled = false };
            pictImage     = new PictureBox { Dock = DockStyle.Fill,
                                             SizeMode = PictureBoxSizeMode.Zoom,
                                             BorderStyle = BorderStyle.FixedSingle };
            btnBrowseImage= new Button { Text = "Browse Image…", AutoSize = true };
            btnClearImage  = new Button { Text = "Clear", AutoSize = true };
            chkByIWC      = new CheckBox { Text = "Supplied by IWC", AutoSize = true,
                                            Anchor = AnchorStyles.Left };
            cboVendor     = new ComboBox { Dock = DockStyle.Fill,
                                            DropDownStyle = ComboBoxStyle.DropDownList };
            txtVendorNum  = new TextBox { Dock = DockStyle.Fill };
            txtVendorLink = new TextBox { Dock = DockStyle.Fill };
            btnSave       = new Button { Text = "Save",   Width = 88, Height = 28, Enabled = false };
            btnCancel     = new Button { Text = "Cancel", Width = 88, Height = 28 };

            // ---- Tab: Basic ------------------------------------------------
            var tabBasic = new TabPage("Basic");
            var tblBasic = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
                Padding = new Padding(10, 10, 10, 4)
            };
            tblBasic.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tblBasic.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++)
                tblBasic.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            void AddBasicRow(int row, string lbl, Control ctl)
            {
                tblBasic.Controls.Add(new Label { Text = lbl, AutoSize = true,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Margin = new Padding(0, 8, 6, 0) }, 0, row);
                tblBasic.Controls.Add(ctl, 1, row);
            }
            AddBasicRow(0, "Hdw No:",          txtHdwNo);
            AddBasicRow(1, "Description:",     txtHdwDesc);
            AddBasicRow(2, "Hardware Group:",  cboGroup);
            AddBasicRow(3, "Units:",           txtHdwUnit);
            tabBasic.Controls.Add(tblBasic);

            // ---- Tab: Details ----------------------------------------------
            var tabDetails = new TabPage("Details");
            var tblDet = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
                Padding = new Padding(10, 10, 10, 4)
            };
            tblDet.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tblDet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tblDet.RowStyles.Add(new RowStyle(SizeType.Percent,  40));   // Notes
            tblDet.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Approved
            tblDet.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // Date
            tblDet.RowStyles.Add(new RowStyle(SizeType.Percent,  60));   // Image + ByIWC

            tblDet.Controls.Add(new Label { Text = "Notes:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 6, 0) }, 0, 0);
            tblDet.Controls.Add(txtHdwNotes, 1, 0);
            tblDet.Controls.Add(new Label(), 0, 1);
            tblDet.Controls.Add(chkApproved, 1, 1);
            tblDet.Controls.Add(new Label { Text = "Approval Date:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 8, 6, 0) }, 0, 2);
            tblDet.Controls.Add(dtpApprove, 1, 2);

            var imgPanel = new TableLayoutPanel { Dock = DockStyle.Fill,
                RowCount = 3, ColumnCount = 1 };
            imgPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            imgPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            imgPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var imgBtns = new FlowLayoutPanel { Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight };
            imgBtns.Controls.Add(btnBrowseImage);
            imgBtns.Controls.Add(btnClearImage);
            imgPanel.Controls.Add(pictImage,   0, 0);
            imgPanel.Controls.Add(imgBtns,     0, 1);
            imgPanel.Controls.Add(chkByIWC,    0, 2);
            tblDet.Controls.Add(new Label { Text = "Image:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 6, 0) }, 0, 3);
            tblDet.Controls.Add(imgPanel, 1, 3);
            tabDetails.Controls.Add(tblDet);

            // ---- Tab: Vendor -----------------------------------------------
            var tabVendor = new TabPage("Vendor");
            var tblVend = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
                Padding = new Padding(10, 10, 10, 4)
            };
            tblVend.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tblVend.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++)
                tblVend.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            void AddVendRow(int row, string lbl, Control ctl)
            {
                tblVend.Controls.Add(new Label { Text = lbl, AutoSize = true,
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Margin = new Padding(0, 8, 6, 0) }, 0, row);
                tblVend.Controls.Add(ctl, 1, row);
            }
            AddVendRow(0, "Vendor/Mfr:",   cboVendor);
            AddVendRow(1, "Vendor Item #:", txtVendorNum);
            AddVendRow(2, "Vendor Link:",   txtVendorLink);
            tabVendor.Controls.Add(tblVend);

            // ---- TabControl ------------------------------------------------
            tabControl = new TabControl { Dock = DockStyle.Fill };
            tabControl.TabPages.Add(tabBasic);
            tabControl.TabPages.Add(tabDetails);
            tabControl.TabPages.Add(tabVendor);

            var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 6, 8, 0) };
            btnPanel.Controls.Add(btnSave);
            btnPanel.Controls.Add(btnCancel);

            Controls.Add(tabControl);
            Controls.Add(btnPanel);

            ClientSize      = new Size(540, 480);
            MinimumSize     = new Size(440, 400);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox     = false; MaximizeBox = false;
            StartPosition   = FormStartPosition.CenterParent;
            AcceptButton    = btnSave;
            CancelButton    = btnCancel;
        }

        // -----------------------------------------------------------------------
        // Field declarations
        // -----------------------------------------------------------------------

        private TabControl     tabControl     = null!;
        private TextBox        txtHdwNo       = null!;
        private TextBox        txtHdwDesc     = null!;
        private ComboBox       cboGroup       = null!;
        private TextBox        txtHdwUnit     = null!;
        private TextBox        txtHdwNotes    = null!;
        private CheckBox       chkApproved    = null!;
        private DateTimePicker dtpApprove     = null!;
        private PictureBox     pictImage      = null!;
        private Button         btnBrowseImage = null!;
        private Button         btnClearImage  = null!;
        private CheckBox       chkByIWC       = null!;
        private ComboBox       cboVendor      = null!;
        private TextBox        txtVendorNum   = null!;
        private TextBox        txtVendorLink  = null!;
        private Button         btnSave        = null!;
        private Button         btnCancel      = null!;

        // -----------------------------------------------------------------------
        // Helper types
        // -----------------------------------------------------------------------

        private sealed class HdwGroupItem
        {
            public int    Id       { get; }
            public string GroupTag { get; }
            public string Name     { get; }
            public HdwGroupItem(int id, string tag, string name, string desc)
            {
                Id = id; GroupTag = tag;
                Name = string.IsNullOrWhiteSpace(desc) ? $"{tag} - {name}" : $"{tag} - {name} ({desc})";
            }
            public override string ToString() => Name;
        }

        private sealed class VendorItem
        {
            public int?   Id   { get; }
            public string Name { get; }
            public VendorItem(int? id, string name, string type = "")
            {
                Id = id;
                Name = string.IsNullOrWhiteSpace(type) ? name : $"{name} [{type}]";
            }
            public override string ToString() => Name;
        }

        private sealed class HdwRecord
        {
            public string?   HdwNo        { get; init; }
            public string?   HdwDesc      { get; init; }
            public int?      HdwGroup     { get; init; }
            public string?   HdwUnit      { get; init; }
            public string?   HdwNotes     { get; init; }
            public DateTime? HdwApprove   { get; init; }
            public int?      HdwVendorId  { get; init; }
            public string?   HdwVendorNum { get; init; }
            public string?   HdwVendorLink{ get; init; }
            public bool      HdwByIWC     { get; init; }
            public byte[]?   ImageBytes   { get; init; }
        }

        private sealed class SaveArgs
        {
            public string?   HdwNo        { get; init; }
            public string?   HdwDesc      { get; init; }
            public int       GroupId      { get; init; }
            public string?   GroupTag     { get; init; }
            public string?   HdwUnit      { get; init; }
            public string?   HdwNotes     { get; init; }
            public DateTime? HdwApprove   { get; init; }
            public int?      HdwVendorId  { get; init; }
            public string?   HdwVendorNum { get; init; }
            public string?   HdwVendorLink{ get; init; }
            public bool      HdwByIWC     { get; init; }
            public byte[]?   ImageBytes   { get; init; }
        }

        private sealed class FormData
        {
            public List<HdwGroupItem> Groups  { get; } = new();
            public List<VendorItem>   Vendors { get; } = new();
            public HdwRecord?         Record  { get; set; }
        }
    }
}
