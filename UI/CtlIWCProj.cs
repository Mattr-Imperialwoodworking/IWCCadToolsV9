using System;
using System.Drawing;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Palette tab showing current project information and DWG file properties.
    /// </summary>
    public partial class CtlIWCProj : UserControl
    {
        public CtlIWCProj()
        {
            InitializeComponent();
            tabControl.Dock = DockStyle.Fill;
            Dock            = DockStyle.Fill;
            LoadProjectData();
        }

        // ---------------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------------

        public void Reload() => LoadProjectData();

        // ---------------------------------------------------------------------------
        // Data load
        // ---------------------------------------------------------------------------

        private void LoadProjectData()
        {
            try
            {
                int projId = Convert.ToInt32(IWCAcadCommands.GetSystemVariable("USERI1"));

                using var conn = new IWCConn();
                conn.DBConnect();

                var proj = new IWCProj();
                proj.GetProject(projId, conn.OpenConn);

                txtProjNum.Text   = proj.ProjNum  ?? "NA";
                txtProjName.Text  = proj.ProjName ?? "NA";

                if (proj.ProjRS?.Tables.Count > 0 && proj.ProjRS.Tables[0].Rows.Count > 0)
                {
                    var row = proj.ProjRS.Tables[0].Rows[0];
                    txtArch.Text  = GetCol(row, "Architect");
                    txtCont.Text  = GetCol(row, "Contractor");
                    txtPM.Text    = GetCol(row, "PMINI");
                }
                else
                {
                    txtArch.Text = txtCont.Text = txtPM.Text = "NA";
                }

                LoadFileProps();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load project: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadFileProps()
        {
            txtFProjNum.Text    = AcadFilePropHelper.GetCustomProperty("IWC_ProjNo")    ?? "NA";
            txtFProjName.Text   = AcadFilePropHelper.GetCustomProperty("IWC_ProjName")  ?? "NA";
            txtFArch.Text       = AcadFilePropHelper.GetCustomProperty("IWC_Architect") ?? "NA";
            txtFCont.Text       = AcadFilePropHelper.GetCustomProperty("IWC_Contractor") ?? "NA";
            txtFPM.Text         = AcadFilePropHelper.GetCustomProperty("IWC_PMINI")     ?? "NA";
            txtFSeriesNo.Text   = AcadFilePropHelper.GetCustomProperty("IWC_SeriesNo")  ?? "NA";
            txtFSeriesName.Text = AcadFilePropHelper.GetCustomProperty("IWC_SeriesName") ?? "NA";
        }

        // ---------------------------------------------------------------------------
        // Button handlers
        // ---------------------------------------------------------------------------

        private void btnRefresh_Click(object? sender,  EventArgs e) => Reload();

        private void btnChangeProject_Click(object? sender,  EventArgs e)
        {
            // Reset project so IWCStartup will re-prompt
            IWCAcadCommands.SetSystemVariable("PROJECTNAME", "NA");
            IWCAcadCommands.SetSystemVariable("USERI1", 0);
            AcadFilePropHelper.SetCustomProperty("IWC_SeriesNo", "NA");

            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.SendStringToExecute("_SETVAR ProjectName NA\n", true, false, false);

            new Core.IWCStartup().Initialize();
        }

        private void btnSyncTitleblock_Click(object? sender,  EventArgs e)
        {
            // Re-run the file property sync
            new Core.IWCStartup().Initialize();
            LoadFileProps();
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static string GetCol(System.Data.DataRow row, string col)
            => row.Table.Columns.Contains(col) && row[col] != DBNull.Value
               ? row[col].ToString() ?? "NA" : "NA";

        // ---------------------------------------------------------------------------
        // Designer-generated members
        // ---------------------------------------------------------------------------

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
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6,
                Padding = new Padding(4)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100));

            txtProjNum  = AddRow(tbl, 0, "Project Number:");
            txtProjName = AddRow(tbl, 1, "Project Name:");
            txtArch     = AddRow(tbl, 2, "Architect:");
            txtCont     = AddRow(tbl, 3, "Contractor:");
            txtPM       = AddRow(tbl, 4, "Project PM:");

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
            tblF.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100));

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

            // Assemble
            tabControl.Controls.AddRange(new Control[] { tabProj, tabFile });
            Controls.Add(tabControl);
            Size = new Size(420, 420);
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
                BackColor = SystemColors.Window
            };
            tbl.Controls.Add(tb, 1, row);
            return tb;
        }

        // Controls
        private TabControl tabControl    = null!;
        private TextBox txtProjNum       = null!;
        private TextBox txtProjName      = null!;
        private TextBox txtArch          = null!;
        private TextBox txtCont          = null!;
        private TextBox txtPM            = null!;
        private TextBox txtFProjNum      = null!;
        private TextBox txtFProjName     = null!;
        private TextBox txtFArch         = null!;
        private TextBox txtFCont         = null!;
        private TextBox txtFPM           = null!;
        private TextBox txtFSeriesNo     = null!;
        private TextBox txtFSeriesName   = null!;
        private Button  btnRefresh       = null!;
        private Button  btnChangeProject = null!;
        private Button  btnSyncTitleblock = null!;
    }
}
