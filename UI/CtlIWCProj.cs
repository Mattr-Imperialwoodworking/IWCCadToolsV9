using System;
using System.Drawing;
using System.Windows.Forms;
using IWCCadToolsV9.Core;
using IWCCadToolsV9.Data.Models;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Palette tab showing current project information and DWG file properties.
    ///
    /// Updated to bind against ProjectContextService instead of re-querying
    /// USERI1 / the database directly. Subscribes to ProjectContextService.ProjectLoaded
    /// instead of the legacy static CtlIWCProj.ProjectChanged event.
    /// </summary>
    public partial class CtlIWCProj : UserControl
    {
        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        public CtlIWCProj()
        {
            // AutoCAD palettes can be aggressive about returning focus to the
            // drawing editor.  Make the user control itself selectable so its
            // child TextBox/DataGridView editors can keep focus while typing.
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;

            InitializeComponent();
            tabControl.Dock = DockStyle.Fill;
            Dock            = DockStyle.Fill;

            // Subscribe to document activation so the palette updates
            // when the user switches between open drawings
            Application.DocumentManager.DocumentActivated += OnDocumentActivated;

            RefreshFromContext();
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        public void Reload() => RefreshFromContext();

        // -----------------------------------------------------------------------
        // Context binding
        // -----------------------------------------------------------------------

        private ProjectContextService? _currentSvc;
        private bool _loadingFileProps;
        private bool _filePropsDirty;

        private void SubscribeToContext(ProjectContextService svc)
        {
            // Unsubscribe from previous document's service
            if (_currentSvc != null)
                _currentSvc.ProjectLoaded -= OnProjectLoaded;

            _currentSvc = svc;
            _currentSvc.ProjectLoaded += OnProjectLoaded;
        }

        private void OnDocumentActivated(object? sender,
            Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
        {
            // Run on the UI thread — DocumentActivated is already main-thread safe
            if (e.Document == null) return;
            var svc = ProjectContextService.GetOrCreate(e.Document);
            SubscribeToContext(svc);
            BindToService(svc);
        }

        private void OnProjectLoaded(object? sender, EventArgs e)
        {
            // ProjectLoaded may fire from a background Task — marshal to UI thread
            if (InvokeRequired)
                Invoke(new Action(() => BindToService(_currentSvc)));
            else
                BindToService(_currentSvc);
        }

        private void RefreshFromContext()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var svc = ProjectContextService.GetOrCreate(doc);
            SubscribeToContext(svc);
            BindToService(svc);
        }

        // -----------------------------------------------------------------------
        // UI binding — no database calls here, all data comes from the service
        // -----------------------------------------------------------------------

        private void BindToService(ProjectContextService? svc)
        {
            if (svc == null)
            {
                ClearProjectFields();
                return;
            }

            var proj = svc.Project;
            var dash = svc.Dash;

            // Tab 1 — Current Project (from typed ProjectRecord)
            if (proj != null)
            {
                txtProjNum.Text  = proj.IdNum;
                txtProjName.Text = proj.Name;
                txtArch.Text     = proj.Architect;
                txtCont.Text     = proj.Contractor;
                txtPM.Text       = proj.PMIni;

                // Auto-sync project data to DWG custom file properties every time
                // the project context is updated — keeps the file self-describing
                // without requiring a manual "Sync Title Block" click.
                try { svc.PersistToDwg(); } catch { /* non-fatal — file may be read-only */ }
            }
            else
            {
                ClearProjectFields();
            }

            // Offline indicator
            lblOffline.Visible = svc.IsOffline;
            lblOffline.Text    = svc.IsOffline ? "⚠ Offline — showing cached data" : string.Empty;

            // Tab 2 — DWG File Properties — always re-read from the DWG after
            // PersistToDwg() so the File Properties tab reflects what was written.
            LoadFileProps();
        }

        private void LoadFileProps()
        {
            _loadingFileProps = true;

            try
            {
                // ── Custom Properties grid ────────────────────────────────────
                dgvCustomProps.Rows.Clear();
                var all = Helpers.AcadFilePropHelper.GetAllCustomProperties();
                foreach (var kv in all)
                    dgvCustomProps.Rows.Add(kv.Key, kv.Value);

                // ── Summary tab ───────────────────────────────────────────────
                var summ = Helpers.AcadFilePropHelper.GetSummaryProps();
                if (summ != null)
                {
                    txtSummTitle.Text     = summ.Title;
                    txtSummSubject.Text   = summ.Subject;
                    txtSummAuthor.Text    = summ.Author;
                    txtSummKeywords.Text  = summ.Keywords;
                    txtSummHyperlink.Text = summ.HyperlinkBase;
                    txtSummRevision.Text  = summ.RevisionNumber;
                    txtSummComments.Text  = summ.Comments;
                }

                // ── File Info tab ─────────────────────────────────────────────
                var info = Helpers.AcadFilePropHelper.GetFileInfoProps();
                if (info != null)
                {
                    txtInfoFile.Text      = info.FileName     ?? string.Empty;
                    txtInfoLocation.Text  = info.Location     ?? string.Empty;
                    txtInfoSize.Text      = info.SizeBytes > 0
                        ? $"{info.SizeBytes / 1024.0 / 1024.0:F2} MB ({info.SizeBytes:N0} bytes)"
                        : string.Empty;
                    txtInfoCreated.Text   = info.Created?.ToString("g")  ?? string.Empty;
                    txtInfoModified.Text  = info.Modified?.ToString("g") ?? string.Empty;
                    txtInfoAccessed.Text  = info.Accessed?.ToString("g") ?? string.Empty;
                    txtInfoLastSaved.Text = info.LastSavedBy ?? string.Empty;
                }
            }
            finally
            {
                _loadingFileProps = false;
                SetFilePropsDirty(false);
            }
        }

        private static TextBox AddFilePropRow(TableLayoutPanel tbl, int row, string label, bool readOnly = true)
        {
            tbl.Controls.Add(new Label
            {
                Text = label, AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 4, 0)
            }, 0, row);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = readOnly,
                Enabled = true,
                TabStop = !readOnly,
                BackColor = System.Drawing.SystemColors.Window
            };
            if (!readOnly)
            {
                tb.Cursor = Cursors.IBeam;
                tb.MouseDown += EditableFilePropControl_MouseDown;
                tb.Enter += EditableFilePropControl_Enter;
            }
            tbl.Controls.Add(tb, 1, row);
            return tb;
        }

        private static void EditableFilePropControl_MouseDown(object? sender, MouseEventArgs e)
        {
            if (sender is Control ctl && ctl.CanFocus && !ctl.Focused)
                ctl.Focus();
        }

        private static void EditableFilePropControl_Enter(object? sender, EventArgs e)
        {
            if (sender is TextBox tb && !tb.ReadOnly)
                tb.SelectionStart = tb.TextLength;
        }

        private void MarkFilePropsDirty(object? sender, EventArgs e)
        {
            if (!_loadingFileProps)
                SetFilePropsDirty(true);
        }

        private void SetFilePropsDirty(bool dirty)
        {
            _filePropsDirty = dirty;
            if (btnSaveFileProps != null)
            {
                btnSaveFileProps.Visible = dirty;
                btnSaveFileProps.Enabled = dirty;
            }
        }

        private void SaveFileProps()
        {
            if (!_filePropsDirty) return;

            try
            {
                var customProps = new System.Collections.Generic.Dictionary<string, string>(
                    System.StringComparer.OrdinalIgnoreCase);

                foreach (DataGridViewRow row in dgvCustomProps.Rows)
                {
                    if (row.IsNewRow) continue;

                    var key = row.Cells["PropName"].Value?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    customProps[key] = row.Cells["PropValue"].Value?.ToString() ?? string.Empty;
                }

                Helpers.AcadFilePropHelper.SetCustomProperties(customProps);

                Helpers.AcadFilePropHelper.SetSummaryProps(new Helpers.AcadFilePropHelper.SummaryProps
                {
                    Title          = txtSummTitle.Text,
                    Subject        = txtSummSubject.Text,
                    Author         = txtSummAuthor.Text,
                    Keywords       = txtSummKeywords.Text,
                    HyperlinkBase  = txtSummHyperlink.Text,
                    RevisionNumber = txtSummRevision.Text,
                    Comments       = txtSummComments.Text
                });

                LoadFileProps();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Unable to save drawing file properties.\n\n{ex.Message}",
                    "IWC File Properties",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void ClearProjectFields()
        {
            txtProjNum.Text = txtProjName.Text = txtArch.Text =
            txtCont.Text    = txtPM.Text       = "NA";
        }

        // -----------------------------------------------------------------------
        // Button handlers — delegate to ProjectContextService
        // -----------------------------------------------------------------------

        private void btnRefresh_Click(object? sender, EventArgs e)
            => RefreshFromContext();

        private void btnChangeProject_Click(object? sender, EventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            ProjectContextService.GetOrCreate(doc).ChangeProject();
        }

        private void btnSaveFileProps_Click(object? sender, EventArgs e)
            => SaveFileProps();

        // -----------------------------------------------------------------------
        // Designer-generated members (unchanged from original)
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            tabControl   = new TabControl();
            var tabProj  = new TabPage("Current Project");
            var tabFile  = new TabPage("File Properties");

            // --- Tab 1: Current Project ---
            var grpProj = new GroupBox
            {
                Text = "Current Project Data", Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
                Padding = new Padding(4)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            txtProjNum  = AddRow(tbl, 0, "Project Number:");
            txtProjName = AddRow(tbl, 1, "Project Name:");
            txtArch     = AddRow(tbl, 2, "Architect:");
            txtCont     = AddRow(tbl, 3, "Contractor:");
            txtPM       = AddRow(tbl, 4, "Project PM:");

            // Offline status label
            lblOffline = new Label
            {
                Dock = DockStyle.Top, Height = 22, Visible = false,
                ForeColor = Color.DarkOrange, Font = new Font(Font, FontStyle.Bold)
            };
            tbl.Controls.Add(lblOffline, 0, 5);
            tbl.SetColumnSpan(lblOffline, 2);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, Padding = new Padding(4)
            };
            btnRefresh         = new Button { Text = "Refresh",        Width = 100, Height = 30 };
            btnChangeProject   = new Button { Text = "Change Project",  Width = 120, Height = 30 };
            btnRefresh.Click       += btnRefresh_Click;
            btnChangeProject.Click += btnChangeProject_Click;
            btnPanel.Controls.AddRange(new Control[] { btnRefresh, btnChangeProject });

            grpProj.Controls.Add(tbl);
            grpProj.Controls.Add(btnPanel);
            tabProj.Controls.Add(grpProj);

            // --- Tab 2: File Properties — inner TabControl ---
            var fileTabCtl = new TabControl { Dock = DockStyle.Fill };

            // ── Sub-tab A: Custom Properties ──────────────────────────────
            var subCustom = new TabPage("Custom Properties");
            dgvCustomProps = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = false, Enabled = true, TabStop = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false, AllowUserToAddRows = true,
                AllowUserToDeleteRows = false, RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BorderStyle = BorderStyle.None,
            };
            dgvCustomProps.Columns.Add("PropName",  "Property");
            dgvCustomProps.Columns.Add("PropValue", "Value");
            dgvCustomProps.Columns["PropName"].FillWeight  = 40;
            dgvCustomProps.Columns["PropValue"].FillWeight = 60;
            dgvCustomProps.CellValueChanged += MarkFilePropsDirty;
            dgvCustomProps.CellEndEdit      += MarkFilePropsDirty;
            dgvCustomProps.RowsAdded        += MarkFilePropsDirty;
            dgvCustomProps.MouseDown       += EditableFilePropControl_MouseDown;
            dgvCustomProps.Enter           += EditableFilePropControl_Enter;

            subCustom.Controls.Add(dgvCustomProps);

            // ── Sub-tab B: Summary ────────────────────────────────────────
            var subSummary = new TabPage("Summary");
            var tblSumm = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
                Padding = new Padding(6, 6, 6, 4)
            };
            tblSumm.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tblSumm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++)
                tblSumm.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tblSumm.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Comments

            txtSummTitle      = AddFilePropRow(tblSumm, 0, "Title:", readOnly: false);
            txtSummSubject    = AddFilePropRow(tblSumm, 1, "Subject:", readOnly: false);
            txtSummAuthor     = AddFilePropRow(tblSumm, 2, "Author:", readOnly: false);
            txtSummKeywords   = AddFilePropRow(tblSumm, 3, "Keywords:", readOnly: false);
            txtSummHyperlink  = AddFilePropRow(tblSumm, 4, "Hyperlink:", readOnly: false);
            txtSummRevision   = AddFilePropRow(tblSumm, 5, "Revision:", readOnly: false);
            txtSummComments   = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = false,
                Enabled = true, TabStop = true,
                BackColor = System.Drawing.SystemColors.Window, ScrollBars = ScrollBars.Vertical,
                Cursor = Cursors.IBeam
            };
            txtSummComments.MouseDown += EditableFilePropControl_MouseDown;
            txtSummComments.Enter     += EditableFilePropControl_Enter;
            tblSumm.Controls.Add(new Label { Text = "Comments:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 4, 4, 0) }, 0, 6);
            tblSumm.Controls.Add(txtSummComments, 1, 6);

            txtSummTitle.TextChanged     += MarkFilePropsDirty;
            txtSummSubject.TextChanged   += MarkFilePropsDirty;
            txtSummAuthor.TextChanged    += MarkFilePropsDirty;
            txtSummKeywords.TextChanged  += MarkFilePropsDirty;
            txtSummHyperlink.TextChanged += MarkFilePropsDirty;
            txtSummRevision.TextChanged  += MarkFilePropsDirty;
            txtSummComments.TextChanged  += MarkFilePropsDirty;

            subSummary.Controls.Add(tblSumm);

            // ── Sub-tab C: File Info ──────────────────────────────────────
            var subInfo = new TabPage("File Info");
            var tblInfo = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
                Padding = new Padding(6)
            };
            tblInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tblInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++)
                tblInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            txtInfoFile       = AddFilePropRow(tblInfo, 0, "File:");
            txtInfoLocation   = AddFilePropRow(tblInfo, 1, "Location:");
            txtInfoSize       = AddFilePropRow(tblInfo, 2, "Size:");
            txtInfoCreated    = AddFilePropRow(tblInfo, 3, "Created:");
            txtInfoModified   = AddFilePropRow(tblInfo, 4, "Modified:");
            txtInfoAccessed   = AddFilePropRow(tblInfo, 5, "Accessed:");
            txtInfoLastSaved  = AddFilePropRow(tblInfo, 6, "Last Saved:");
            subInfo.Controls.Add(tblInfo);

            fileTabCtl.TabPages.Add(subCustom);
            fileTabCtl.TabPages.Add(subSummary);
            fileTabCtl.TabPages.Add(subInfo);

            var fileBtnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 5, 4, 4)
            };
            btnSaveFileProps = new Button { Text = "Save Changes", Width = 120, Height = 28, Visible = false, Enabled = false };
            btnSaveFileProps.Click += btnSaveFileProps_Click;
            fileBtnRow.Controls.Add(btnSaveFileProps);

            tabFile.Controls.Add(fileTabCtl);
            tabFile.Controls.Add(fileBtnRow);

            tabControl.Controls.AddRange(new Control[] { tabProj, tabFile });
            Controls.Add(tabControl);
            Size = new Size(420, 440);
            Name = "CtlIWCProj";
        }

        private static TextBox AddRow(TableLayoutPanel tbl, int row, string label)
        {
            tbl.Controls.Add(new Label
            {
                Text = label, AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            }, 0, row);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true,
                BackColor = System.Drawing.SystemColors.Window
            };
            tbl.Controls.Add(tb, 1, row);
            return tb;
        }

        // Controls — Tab 1 (Current Project)
        private TabControl      tabControl       = null!;
        private Label           lblOffline       = null!;
        private TextBox         txtProjNum       = null!;
        private TextBox         txtProjName      = null!;
        private TextBox         txtArch          = null!;
        private TextBox         txtCont          = null!;
        private TextBox         txtPM            = null!;
        private Button          btnRefresh       = null!;
        private Button          btnChangeProject = null!;

        // Controls — Tab 2 / File Properties tab
        private DataGridView    dgvCustomProps   = null!;
        private Button          btnSaveFileProps  = null!;

        // Controls — Tab 2 / Summary sub-tab
        private TextBox         txtSummTitle     = null!;
        private TextBox         txtSummSubject   = null!;
        private TextBox         txtSummAuthor    = null!;
        private TextBox         txtSummKeywords  = null!;
        private TextBox         txtSummHyperlink = null!;
        private TextBox         txtSummRevision  = null!;
        private TextBox         txtSummComments  = null!;

        // Controls — Tab 2 / File Info sub-tab
        private TextBox         txtInfoFile      = null!;
        private TextBox         txtInfoLocation  = null!;
        private TextBox         txtInfoSize      = null!;
        private TextBox         txtInfoCreated   = null!;
        private TextBox         txtInfoModified  = null!;
        private TextBox         txtInfoAccessed  = null!;
        private TextBox         txtInfoLastSaved = null!;
    }
}
