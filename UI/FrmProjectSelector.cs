using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Data.Models;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Two-pane project + dash selector dialog.
    /// Replaces the separate ProjSelect and ProjDashSelect dialogs (Step 6).
    ///
    /// Left pane:  filterable project list (from dbo.Proj_CompileActive)
    /// Right pane: dash list that populates once a project is selected
    ///             (from dbo.Proj_DashCompileReportActive)
    ///
    /// After DialogResult == OK:
    ///   SelectedProjectId — always set
    ///   SelectedDashId    — set if the user chose a dash; null if skipped
    /// </summary>
    public partial class FrmProjectSelector : IWCBaseForm
    {
        // -----------------------------------------------------------------------
        // Output properties
        // -----------------------------------------------------------------------

        public int?  SelectedProjectId { get; private set; }
        public int?  SelectedDashId    { get; private set; }

        // -----------------------------------------------------------------------
        // Private state
        // -----------------------------------------------------------------------

        private static readonly IWCProjRepository _repo = new();

        private List<ProjectRecord> _allProjects = new();
        private List<DashRecord>    _dashes      = new();
        private int?                _selectedProjId;

        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        public FrmProjectSelector()
        {
            InitializeComponent();
            WireEvents();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LoadProjects();
        }

        // -----------------------------------------------------------------------
        // Data load
        // -----------------------------------------------------------------------

        private void LoadProjects()
        {
            try
            {
                _allProjects = _repo.GetActiveProjects().ToList();
                BindProjects(_allProjects);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load projects:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BindProjects(IEnumerable<ProjectRecord> projects)
        {
            lstProjects.BeginUpdate();
            lstProjects.Items.Clear();
            foreach (var p in projects)
                lstProjects.Items.Add(new ListItem<ProjectRecord>(p.DisplayLabel, p));
            lstProjects.EndUpdate();

            lblProjectCount.Text = $"{lstProjects.Items.Count} projects";
            ClearDashPane();
        }

        private void LoadDashes(int projectId)
        {
            try
            {
                _dashes = _repo.GetDashesForProject(projectId).ToList();
                BindDashes(_dashes);
            }
            catch (Exception ex)
            {
                dgvDashes.DataSource = null;
                lblDashStatus.Text = "Failed to load dashes: " + ex.Message;
            }
        }

        private void BindDashes(List<DashRecord> dashes)
        {
            // Build a lightweight display table — avoids exposing raw DataRow
            var display = dashes.Select(d => new
            {
                d.DashId,
                DashNum  = d.DashNum,
                Desc     = d.DashDesc,
                Status   = d.DashStatus,
                Type     = d.IsSeries ? "Series" : "Component",
                d.CADIni,
            }).ToList();

            dgvDashes.AutoGenerateColumns = true;
            dgvDashes.DataSource = display;

            if (dgvDashes.Columns.Contains("DashId"))
                dgvDashes.Columns["DashId"].Visible = false;
            if (dgvDashes.Columns.Contains("DashNum"))
                dgvDashes.Columns["DashNum"].HeaderText = "Number";
            if (dgvDashes.Columns.Contains("Desc"))
                dgvDashes.Columns["Desc"].HeaderText = "Description";
            if (dgvDashes.Columns.Contains("Status"))
                dgvDashes.Columns["Status"].HeaderText = "Status";
            if (dgvDashes.Columns.Contains("Type"))
                dgvDashes.Columns["Type"].HeaderText = "Type";
            if (dgvDashes.Columns.Contains("CADIni"))
                dgvDashes.Columns["CADIni"].HeaderText = "CAD";

            // Bold series rows
            foreach (DataGridViewRow row in dgvDashes.Rows)
            {
                if (row.DataBoundItem == null) continue;
                var type = row.DataBoundItem.GetType().GetProperty("Type")
                              ?.GetValue(row.DataBoundItem)?.ToString();
                if (type == "Series")
                    row.DefaultCellStyle.Font = new Font(dgvDashes.Font, FontStyle.Bold);
            }

            dgvDashes.AutoResizeColumns();
            lblDashStatus.Text = $"{dashes.Count} dashes";
        }

        private void ClearDashPane()
        {
            dgvDashes.DataSource = null;
            lblDashStatus.Text = "Select a project to load dashes";
            btnSkipDash.Enabled = false;
        }

        // -----------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------

        private void WireEvents()
        {
            txtSearch.TextChanged      += (_, _) => FilterProjects();
            lstProjects.SelectedIndexChanged += OnProjectSelected;
            dgvDashes.CellDoubleClick  += (_, _) => AcceptWithDash();
            btnOk.Click                += (_, _) => AcceptWithDash();
            btnSkipDash.Click          += (_, _) => AcceptProjectOnly();
            btnCancel.Click            += (_, _) => DialogResult = DialogResult.Cancel;
        }

        private void FilterProjects()
        {
            var term = txtSearch.Text.Trim();
            var filtered = string.IsNullOrWhiteSpace(term)
                ? _allProjects
                : _allProjects.Where(p =>
                    p.IdNum.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

            BindProjects(filtered);
        }

        private void OnProjectSelected(object? sender, EventArgs e)
        {
            if (lstProjects.SelectedItem is not ListItem<ProjectRecord> item) return;
            _selectedProjId = item.Value.Id;
            btnSkipDash.Enabled = true;
            LoadDashes(item.Value.Id);
        }

        private void AcceptWithDash()
        {
            if (_selectedProjId == null) return;
            SelectedProjectId = _selectedProjId;

            // If a dash row is selected, use it; otherwise accept project only
            if (dgvDashes.CurrentRow?.DataBoundItem != null)
            {
                var dashId = (int?)dgvDashes.CurrentRow.DataBoundItem
                    .GetType().GetProperty("DashId")
                    ?.GetValue(dgvDashes.CurrentRow.DataBoundItem);
                SelectedDashId = dashId > 0 ? dashId : null;
            }

            DialogResult = DialogResult.OK;
        }

        private void AcceptProjectOnly()
        {
            if (_selectedProjId == null) return;
            SelectedProjectId = _selectedProjId;
            SelectedDashId    = null;
            DialogResult      = DialogResult.OK;
        }

        // -----------------------------------------------------------------------
        // Designer-generated members
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            txtSearch   = new TextBox    { PlaceholderText = "Search projects…", Dock = DockStyle.Top };
            lstProjects = new ListBox    { Dock = DockStyle.Fill, IntegralHeight = false };
            dgvDashes   = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
            };
            lblProjectCount = new Label { Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.MiddleLeft };
            lblDashStatus   = new Label { Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.MiddleLeft };

            btnOk       = new Button { Text = "OK",           Width = 90, Height = 30 };
            btnSkipDash = new Button { Text = "No Dash",      Width = 90, Height = 30, Enabled = false };
            btnCancel   = new Button { Text = "Cancel",       Width = 90, Height = 30 };

            // Left panel — project list
            var leftPanel = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(4) };
            leftPanel.Controls.Add(lstProjects);
            leftPanel.Controls.Add(lblProjectCount);
            leftPanel.Controls.Add(txtSearch);

            // Right panel — dash grid
            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            rightPanel.Controls.Add(dgvDashes);
            rightPanel.Controls.Add(lblDashStatus);

            // Button panel
            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, Padding = new Padding(4)
            };
            btnPanel.Controls.AddRange(new Control[] { btnCancel, btnSkipDash, btnOk });

            Controls.Add(rightPanel);
            Controls.Add(leftPanel);
            Controls.Add(btnPanel);

            ClientSize     = new Size(860, 520);
            Text           = "Select Project & Drawing Series";
            MinimizeBox    = false; MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition  = FormStartPosition.CenterParent;
            AcceptButton   = btnOk;
            CancelButton   = btnCancel;
            MinimumSize    = new Size(640, 380);
        }

        private TextBox       txtSearch      = null!;
        private ListBox       lstProjects    = null!;
        private DataGridView  dgvDashes      = null!;
        private Label         lblProjectCount = null!;
        private Label         lblDashStatus  = null!;
        private Button        btnOk          = null!;
        private Button        btnSkipDash    = null!;
        private Button        btnCancel      = null!;

        // -----------------------------------------------------------------------
        // Helper — typed list box item
        // -----------------------------------------------------------------------

        private sealed class ListItem<T>
        {
            public string Label { get; }
            public T      Value { get; }
            public ListItem(string label, T value) { Label = label; Value = value; }
            public override string ToString() => Label;
        }
    }
}
