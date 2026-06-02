using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Dialog for creating or editing block assembly (library item) metadata.
    ///
    /// Usage – create mode:
    ///   using var dlg = new FrmAssemblyEditor();
    ///   dlg.OnSave += (_, _) => db.Insert(dlg.BlockNameValue, ...);
    ///   dlg.ShowDialog();
    ///
    /// Usage – edit mode:
    ///   using var dlg = new FrmAssemblyEditor(blockId, name, desc, ...);
    ///   dlg.OnSave += (_, _) => db.Update(dlg.BlockId!.Value, dlg.BlockNameValue, ...);
    ///   dlg.ShowDialog();
    ///
    /// The OnSave event fires (after validation) when the user clicks Save.
    /// The form stays open if OnSave throws, so the caller can show its own error UI.
    /// </summary>
    public partial class FrmAssemblyEditor : IWCBaseForm
    {
        // ---------------------------------------------------------------------------
        // Save event
        // ---------------------------------------------------------------------------

        public event EventHandler? OnSave;

        // ---------------------------------------------------------------------------
        // Public output properties
        // ---------------------------------------------------------------------------

        public int?   BlockId           { get; private set; }
        public string BlockNameValue    => txtName.Text.Trim();
        /// <summary>User-friendly display name shown in the tree. Falls back to BlockName if empty.</summary>
        public string BlockTagValue     => string.IsNullOrWhiteSpace(txtTag.Text) ? txtName.Text.Trim() : txtTag.Text.Trim();
        public string BlockDescValue    => txtDesc.Text;
        public string BlockMfrName      => txtMfrName.Text.Trim();
        public string BlockVendorName   => txtVendorName.Text.Trim();
        public string BlockVendorNum    => txtVendorNum.Text.Trim();
        public string BlockNotes        => txtNotes.Text;
        public string BlockLinkUrl      => txtLinkUrl.Text.Trim();

        // Aliases used by ctlIWCBlockBrowserV2
        public string BlockMfrNameValue    => BlockMfrName;
        public string BlockVendorNameValue => BlockVendorName;
        public string BlockVendorNumValue  => BlockVendorNum;
        public string BlockNotesValue      => BlockNotes;
        public string BlockLinkUrlValue    => BlockLinkUrl;

        // ---------------------------------------------------------------------------
        // Dirty-tracking + duplicate-check flag
        // ---------------------------------------------------------------------------

        private bool _isDirty;
        private void MarkDirty(object? sender, EventArgs e) => _isDirty = true;

        /// <summary>
        /// When false (default), saving is blocked if another block already has the
        /// same BlockName or Display Name.  Set to true for "Project Specific" groups
        /// where duplicates are intentionally allowed.
        /// </summary>
        private readonly bool _allowDuplicates;

        // ---------------------------------------------------------------------------
        // Construction – create mode
        // ---------------------------------------------------------------------------

        /// <param name="allowDuplicates">
        /// Pass <c>true</c> when adding to the "Project Specific" group to skip the
        /// duplicate-name check.
        /// </param>
        public FrmAssemblyEditor(bool allowDuplicates = false)
        {
            _allowDuplicates = allowDuplicates;
            InitializeComponent();
            WireEvents();
            UpdateLinkPreview();
        }

        // ---------------------------------------------------------------------------
        // Construction – edit mode
        // ---------------------------------------------------------------------------

        public FrmAssemblyEditor(
            int blockId, string? blockName, string? blockTag, string? blockDesc,
            string? mfrName,  string? vendorName, string? vendorNum,
            string? notes,    string? linkUrl)
            : this()
        {
            BlockId            = blockId;
            txtName.Text       = blockName   ?? string.Empty;
            txtTag.Text        = blockTag    ?? string.Empty;
            txtDesc.Text       = blockDesc   ?? string.Empty;
            txtMfrName.Text    = mfrName     ?? string.Empty;
            txtVendorName.Text = vendorName  ?? string.Empty;
            txtVendorNum.Text  = vendorNum   ?? string.Empty;
            txtNotes.Text      = notes       ?? string.Empty;
            txtLinkUrl.Text    = linkUrl     ?? string.Empty;

            _isDirty = false;
            UpdateLinkPreview();
        }

        // ---------------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------------

        private void WireEvents()
        {
            btnSave.Click   += BtnSave_Click;
            btnCancel.Click += BtnCancel_Click;

            txtLinkUrl.TextChanged  += (_, _) => UpdateLinkPreview();
            lnkOpenLink.LinkClicked += (_, _) => OpenUrl();

            foreach (Control ctl in new Control[]
                { txtName, txtTag, txtDesc, txtMfrName, txtVendorName, txtVendorNum, txtNotes, txtLinkUrl })
            {
                ctl.TextChanged += MarkDirty;
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            if (!ValidateInputs()) return;
            try
            {
                OnSave?.Invoke(this, EventArgs.Empty);
                _isDirty     = false;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "Save Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            if (_isDirty)
            {
                var answer = MessageBox.Show(
                    "You have unsaved changes. Discard them and close?",
                    "Unsaved Changes",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return;
            }
            DialogResult = DialogResult.Cancel;
        }

        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Block Name is required.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtName.Focus();
                return false;
            }

            // Duplicate check — only in Add mode and when duplicates are not allowed
            if (BlockId == null && !_allowDuplicates)
            {
                var dupes = FindDuplicateBlocks(txtName.Text.Trim(), txtTag.Text.Trim());
                if (dupes.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("A block with the same Block Name or Display Name already exists:\n");
                    foreach (var d in dupes)
                        sb.AppendLine($"  • [{d.Id}]  {d.BlockName}" +
                            (string.IsNullOrWhiteSpace(d.Tag)  ? "" : $"  /  \"{d.Tag}\"") +
                            (string.IsNullOrWhiteSpace(d.Desc) ? "" : $"  —  {d.Desc}"));
                    sb.AppendLine("\nTo allow duplicates, save this block under the \"Project Specific\" folder.");

                    MessageBox.Show(sb.ToString(), "Duplicate Block Name",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtName.Focus();
                    return false;
                }
            }

            return true;
        }

        // ---------------------------------------------------------------------------
        // Duplicate-block check
        // ---------------------------------------------------------------------------

        private sealed record DupeInfo(int Id, string? BlockName, string? Tag, string? Desc);

        private static List<DupeInfo> FindDuplicateBlocks(string name, string tag)
        {
            var results = new List<DupeInfo>();
            bool checkName = !string.IsNullOrWhiteSpace(name);
            bool checkTag  = !string.IsNullOrWhiteSpace(tag);
            if (!checkName && !checkTag) return results;

            try
            {
                using var conn = IWCConn.GetSqlConnection();
                conn.Open();
                using var cmd = new SqlCommand(@"
                    SELECT ID,
                           ISNULL(BlockName, '') AS BlockName,
                           ISNULL(BlockTag,  '') AS BlockTag,
                           ISNULL(BlockDesc, '') AS BlockDesc
                    FROM   dbo.Dwg_Block
                    WHERE  (@checkName = 1 AND LOWER(BlockName) = LOWER(@name))
                        OR (@checkTag  = 1 AND LOWER(BlockTag)  = LOWER(@tag));", conn);

                cmd.Parameters.AddWithValue("@checkName", checkName ? 1 : 0);
                cmd.Parameters.AddWithValue("@name",      (object?)name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@checkTag",  checkTag  ? 1 : 0);
                cmd.Parameters.AddWithValue("@tag",       (object?)tag  ?? DBNull.Value);

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    results.Add(new DupeInfo(
                        rdr.GetInt32(rdr.GetOrdinal("ID")),
                        rdr["BlockName"] as string,
                        rdr["BlockTag"]  as string,
                        rdr["BlockDesc"] as string));
            }
            catch { /* non-fatal — allow the save if the check itself fails */ }

            return results;
        }

        private void UpdateLinkPreview()
        {
            string url          = txtLinkUrl.Text.Trim();
            lnkOpenLink.Text    = string.IsNullOrEmpty(url) ? "(no link)" : url;
            lnkOpenLink.Enabled = !string.IsNullOrEmpty(url)
                                  && Uri.TryCreate(url, UriKind.Absolute, out _);
        }

        private void OpenUrl()
        {
            string url = txtLinkUrl.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        // ---------------------------------------------------------------------------
        // InitializeComponent
        //
        // Layout (bottom-up docking order):
        //
        //   ┌─────────────────────────────────────┐
        //   │  tblTop  (Dock=Fill)                 │  ← Block Name … Notes
        //   ├─────────────────────────────────────┤
        //   │  pnlBottom  (Dock=Bottom, ~118 px)  │  ← Link URL, Open Link, buttons
        //   └─────────────────────────────────────┘
        //
        // pnlBottom is added to Controls BEFORE tblTop so it claims its dock space first.
        // tblTop then fills the remainder.  Notes row is SizeType.Percent so it stretches.
        // ---------------------------------------------------------------------------

        private void InitializeComponent()
        {
            // ── Input controls ───────────────────────────────────────────────────────
            txtName       = new TextBox { Dock = DockStyle.Fill };
            txtTag        = new TextBox { Dock = DockStyle.Fill };
            txtDesc       = new TextBox { Dock = DockStyle.Fill, Multiline = true,
                                          Height = 52, ScrollBars = ScrollBars.Vertical };
            txtMfrName    = new TextBox { Dock = DockStyle.Fill };
            txtVendorName = new TextBox { Dock = DockStyle.Fill };
            txtVendorNum  = new TextBox { Dock = DockStyle.Fill };
            txtNotes      = new TextBox { Dock = DockStyle.Fill, Multiline = true,
                                          ScrollBars = ScrollBars.Vertical };
            txtLinkUrl    = new TextBox { Dock = DockStyle.Fill };
            lnkOpenLink   = new LinkLabel { AutoSize = true,
                                            Anchor = AnchorStyles.Left | AnchorStyles.Top };

            // ── Buttons ──────────────────────────────────────────────────────────────
            btnSave   = new Button { Text = "Save",   Width = 88, Height = 28 };
            btnCancel = new Button { Text = "Cancel", Width = 88, Height = 28 };

            // ── Bottom panel: Link URL row, Open Link row, button row ────────────────
            //
            // Uses a TableLayoutPanel so the text-box column stretches with the form.
            //
            var tblBottom = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 2,
                Padding     = new Padding(8, 4, 8, 4),
                AutoSize    = false
            };
            tblBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tblBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tblBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));  // Link URL
            tblBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));  // Open Link

            tblBottom.Controls.Add(new Label { Text = "Link URL:",  AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 6, 4, 0) }, 0, 0);
            tblBottom.Controls.Add(txtLinkUrl,  1, 0);
            tblBottom.Controls.Add(new Label { Text = "Open Link:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 4, 4, 0) }, 0, 1);
            tblBottom.Controls.Add(lnkOpenLink, 1, 1);

            // Button strip sits below the link rows
            var pnlBtnStrip = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                Height        = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(8, 6, 8, 0),
                AutoSize      = false
            };
            pnlBtnStrip.Controls.Add(btnSave);    // rightmost (FlowDirection reverses visual order)
            pnlBtnStrip.Controls.Add(btnCancel);

            // Container panel for link rows + button strip – docked to bottom of form
            var pnlBottom = new Panel
            {
                Dock   = DockStyle.Bottom,
                Height = 30 + 28 + 4 + 4 + 44  // linkRow + openRow + padding + btnStrip
            };
            pnlBottom.Controls.Add(tblBottom);
            pnlBottom.Controls.Add(pnlBtnStrip);

            // ── Top table: Block Name … Notes ───────────────────────────────────────
            var tblTop = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 7,
                Padding     = new Padding(8, 8, 8, 4),
                AutoSize    = false
            };
            tblTop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tblTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            tblTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // Block Name (AutoCAD internal)
            tblTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // Display Name (BlockTag)
            tblTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));    // Description
            tblTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // Manufacturer
            tblTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // Vendor Name
            tblTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // Vendor #
            tblTop.RowStyles.Add(new RowStyle(SizeType.Percent,  100));   // Notes – fills remaining

            void AddTopRow(int row, string labelText, Control ctl)
            {
                tblTop.Controls.Add(new Label
                {
                    Text     = labelText,
                    AutoSize = true,
                    Anchor   = AnchorStyles.Left | AnchorStyles.Top,
                    Margin   = new Padding(0, 6, 4, 0)
                }, 0, row);
                tblTop.Controls.Add(ctl, 1, row);
            }

            AddTopRow(0, "Block Name:",    txtName);
            AddTopRow(1, "Display Name:",  txtTag);
            AddTopRow(2, "Description:",   txtDesc);
            AddTopRow(3, "Manufacturer:",  txtMfrName);
            AddTopRow(4, "Vendor Name:",   txtVendorName);
            AddTopRow(5, "Vendor #:",      txtVendorNum);
            AddTopRow(6, "Notes:",         txtNotes);

            // ── Wire form ────────────────────────────────────────────────────────────
            // pnlBottom MUST be added before tblTop – Dock=Bottom claims space first,
            // then Dock=Fill takes the remainder.
            Controls.Add(pnlBottom);
            Controls.Add(tblTop);

            ClientSize      = new Size(520, 580);
            MinimumSize     = new Size(400, 440);
            Text            = "Assembly Editor";
            MinimizeBox     = false;
            MaximizeBox     = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterParent;
            AcceptButton    = btnSave;
            CancelButton    = btnCancel;
        }

        // ---------------------------------------------------------------------------
        // Field declarations
        // ---------------------------------------------------------------------------

        private TextBox   txtName       = null!;
        private TextBox   txtTag        = null!;
        private TextBox   txtDesc       = null!;
        private TextBox   txtMfrName    = null!;
        private TextBox   txtVendorName = null!;
        private TextBox   txtVendorNum  = null!;
        private TextBox   txtNotes      = null!;
        private TextBox   txtLinkUrl    = null!;
        private LinkLabel lnkOpenLink   = null!;
        private Button    btnSave       = null!;
        private Button    btnCancel     = null!;
    }
}
