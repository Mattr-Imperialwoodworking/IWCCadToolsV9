using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Dialog for picking one or more block library groups (folders) and
    /// entering block metadata when uploading a new block.
    /// </summary>
    internal partial class GroupPickerForm : Form
    {
        private readonly Dictionary<int, TreeNode> _nodeById = new();

        // Public outputs (read after DialogResult == OK)
        public List<int>  SelectedGroupIds { get; } = new();
        public string?    BlockName  => txtName.Text?.Trim();
        public string     BlockDesc  => txtDesc.Text  ?? string.Empty;
        public string     BlockNotes => txtNotes.Text ?? string.Empty;

        // ---------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------

        public GroupPickerForm()
        {
            InitializeComponent();

            Load += (_, _) => { LoadGroups(); treeGroups.ExpandAll(); };
            treeGroups.AfterCheck += (_, e) => PropagateCheck(e.Node);

            panelButtons.Resize += (_, _) => PositionButtons();

            FormClosing += ValidateOnClose;
        }

        // ---------------------------------------------------------------------------
        // Data
        // ---------------------------------------------------------------------------

        private void LoadGroups()
        {
            treeGroups.BeginUpdate();
            treeGroups.Nodes.Clear();
            _nodeById.Clear();

            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                var dt = new DataTable();
                using (var da = new SqlDataAdapter(@"
                    SELECT ID, GroupName, GroupParent
                    FROM dbo.Dwg_BlockGroups
                    ORDER BY GroupOrder, GroupName;", conn.OpenConn))
                    da.Fill(dt);

                // First pass: create all nodes
                foreach (DataRow r in dt.Rows)
                {
                    int    id   = Convert.ToInt32(r["ID"]);
                    string name = Convert.ToString(r["GroupName"]) ?? string.Empty;
                    var    node = new TreeNode(name) { Tag = id };
                    _nodeById[id] = node;
                }

                // Second pass: wire parent/child hierarchy
                foreach (DataRow r in dt.Rows)
                {
                    int id     = Convert.ToInt32(r["ID"]);
                    int parent = r["GroupParent"] == DBNull.Value
                                 ? 0 : Convert.ToInt32(r["GroupParent"]);

                    var node = _nodeById[id];
                    if (parent == 0 || !_nodeById.TryGetValue(parent, out var parentNode))
                        treeGroups.Nodes.Add(node);
                    else
                        parentNode.Nodes.Add(node);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load groups:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            treeGroups.EndUpdate();
        }

        // ---------------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------------

        private void PropagateCheck(TreeNode? node)
        {
            if (node == null) return;
            foreach (TreeNode child in node.Nodes)
            {
                child.Checked = node.Checked;
                PropagateCheck(child);
            }
        }

        private void ValidateOnClose(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult != DialogResult.OK) return;

            if (string.IsNullOrWhiteSpace(BlockName))
            {
                e.Cancel = true;
                MessageBox.Show("Block Name is required.", "Create Block",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtName.Focus();
                return;
            }

            SelectedGroupIds.Clear();
            CollectChecked(treeGroups.Nodes);

            if (SelectedGroupIds.Count == 0)
            {
                e.Cancel = true;
                MessageBox.Show("Please select at least one folder.", "Create Block",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void CollectChecked(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Checked && n.Tag is int id)
                    SelectedGroupIds.Add(id);
                CollectChecked(n.Nodes);
            }
        }

        private void PositionButtons()
        {
            btnCancel.Left = panelButtons.ClientSize.Width  - btnCancel.Width  - 8;
            btnCancel.Top  = panelButtons.ClientSize.Height - btnCancel.Height - 8;
            btnOK.Left     = btnCancel.Left - btnOK.Width   - 8;
            btnOK.Top      = btnCancel.Top;
        }

        // ---------------------------------------------------------------------------
        // Designer-generated members
        // ---------------------------------------------------------------------------

        private void InitializeComponent()
        {
            treeGroups   = new TreeView();
            txtName      = new TextBox();
            txtDesc      = new TextBox();
            txtNotes     = new TextBox();
            panelButtons = new Panel();
            btnOK        = new Button();
            btnCancel    = new Button();

            var lblName  = new Label { Text = "Block Name:",  AutoSize = true };
            var lblDesc  = new Label { Text = "Description:", AutoSize = true };
            var lblNotes = new Label { Text = "Notes:",       AutoSize = true };
            var lblGroups = new Label { Text = "Select Group(s):", AutoSize = true };

            SuspendLayout();

            // Layout
            int y = 12;
            lblName.SetBounds(12, y, 100, 15);
            txtName.SetBounds(120, y, 360, 23); y += 34;
            lblDesc.SetBounds(12, y, 100, 15);
            txtDesc.SetBounds(120, y, 360, 23); y += 34;
            lblNotes.SetBounds(12, y, 100, 15);
            txtNotes.SetBounds(120, y, 360, 60);
            txtNotes.Multiline = true; y += 72;

            lblGroups.SetBounds(12, y, 200, 15); y += 20;
            treeGroups.SetBounds(12, y, 468, 160);
            treeGroups.CheckBoxes = true; y += 170;

            panelButtons.SetBounds(0, y, 500, 48);
            panelButtons.Dock = DockStyle.Bottom;

            btnOK.Text       = "OK";     btnOK.Size = new Size(88, 28);
            btnOK.DialogResult = DialogResult.OK;
            btnCancel.Text   = "Cancel"; btnCancel.Size = new Size(88, 28);
            btnCancel.DialogResult = DialogResult.Cancel;
            panelButtons.Controls.AddRange(new Control[] { btnOK, btnCancel });

            Controls.AddRange(new Control[]
                { lblName, txtName, lblDesc, txtDesc, lblNotes, txtNotes,
                  lblGroups, treeGroups, panelButtons });

            ClientSize       = new Size(500, y + 56);
            Text             = "Create Block – Select Groups";
            AcceptButton     = btnOK;
            CancelButton     = btnCancel;
            MinimizeBox      = false;
            MaximizeBox      = false;
            FormBorderStyle  = FormBorderStyle.FixedDialog;
            StartPosition    = FormStartPosition.CenterScreen;

            ResumeLayout(false);
        }

        private TreeView treeGroups  = null!;
        private TextBox  txtName     = null!;
        private TextBox  txtDesc     = null!;
        private TextBox  txtNotes    = null!;
        private Panel    panelButtons = null!;
        private Button   btnOK       = null!;
        private Button   btnCancel   = null!;
    }
}
