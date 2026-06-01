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
            }
            else
            {
                ClearProjectFields();
            }

            // Offline indicator
            lblOffline.Visible = svc.IsOffline;
            lblOffline.Text    = svc.IsOffline ? "⚠ Offline — showing cached data" : string.Empty;

            // Tab 2 — DWG File Properties (read directly from DWG, not from service,
            // so we always show what's actually saved in the file)
            LoadFileProps();
        }

        private void LoadFileProps()
        {
            txtFProjNum.Text    = Helpers.AcadFilePropHelper.GetCustomProperty(Helpers.DwgPropertyStore.KeyProjNum)    ?? "NA";
            txtFProjName.Text   = Helpers.AcadFilePropHelper.GetCustomProperty(Helpers.DwgPropertyStore.KeyProjName)   ?? "NA";
            txtFArch.Text       = Helpers.AcadFilePropHelper.GetCustomProperty(Helpers.DwgPropertyStore.KeyArchitect)  ?? "NA";
            txtFCont.Text       = Helpers.AcadFilePropHelper.GetCustomProperty(Helpers.DwgPropertyStore.KeyContractor) ?? "NA";
            txtFPM.Text         = Helpers.AcadFilePropHelper.GetCustomProperty(Helpers.DwgPropertyStore.KeyPMIni)      ?? "NA";
            txtFSeriesNo.Text   = Helpers.AcadFilePropHelper.GetCustomProperty(Helpers.DwgPropertyStore.KeyDashNum)    ?? "NA";
            txtFSeriesName.Text = Helpers.AcadFilePropHelper.GetCustomProperty(Helpers.DwgPropertyStore.KeyDashName)   ?? "NA";
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

        private void btnSyncTitleblock_Click(object? sender, EventArgs e)
        {
            // Force a full write of current context back to DWG properties
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            ProjectContextService.GetOrCreate(doc).PersistToDwg();
            LoadFileProps();
        }

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

            // --- Tab 2: File Properties ---
            var grpFile = new GroupBox
            {
                Text = "DWG Custom File Properties", Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            var tblF = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8,
                Padding = new Padding(4)
            };
            tblF.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tblF.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            txtFProjNum    = AddRow(tblF, 0, "Proj Number:");
            txtFProjName   = AddRow(tblF, 1, "Proj Name:");
            txtFArch       = AddRow(tblF, 2, "Architect:");
            txtFCont       = AddRow(tblF, 3, "Contractor:");
            txtFPM         = AddRow(tblF, 4, "Project PM:");
            txtFSeriesNo   = AddRow(tblF, 5, "Series No:");
            txtFSeriesName = AddRow(tblF, 6, "Series Name:");

            var btnPanelF = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, Padding = new Padding(4)
            };
            btnSyncTitleblock = new Button { Text = "Sync Title Block", Width = 140, Height = 30 };
            btnSyncTitleblock.Click += btnSyncTitleblock_Click;
            btnPanelF.Controls.Add(btnSyncTitleblock);

            grpFile.Controls.Add(tblF);
            grpFile.Controls.Add(btnPanelF);
            tabFile.Controls.Add(grpFile);

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

        // Controls
        private TabControl tabControl       = null!;
        private Label      lblOffline       = null!;
        private TextBox    txtProjNum       = null!;
        private TextBox    txtProjName      = null!;
        private TextBox    txtArch          = null!;
        private TextBox    txtCont          = null!;
        private TextBox    txtPM            = null!;
        private TextBox    txtFProjNum      = null!;
        private TextBox    txtFProjName     = null!;
        private TextBox    txtFArch         = null!;
        private TextBox    txtFCont         = null!;
        private TextBox    txtFPM           = null!;
        private TextBox    txtFSeriesNo     = null!;
        private TextBox    txtFSeriesName   = null!;
        private Button     btnRefresh       = null!;
        private Button     btnChangeProject = null!;
        private Button     btnSyncTitleblock = null!;
    }
}
