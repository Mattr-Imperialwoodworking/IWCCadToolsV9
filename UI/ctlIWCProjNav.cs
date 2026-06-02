using Autodesk.AutoCAD.GraphicsSystem;
using IWCCadToolsV9.Core;
using IWCCadToolsV9.Data;            // adjust if IWCConn lives under Data or Helpers
using IWCCadToolsV9.Helpers;         // (safe to keep; remove if not used)
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
// Disambiguate types shared between AutoCAD and .NET assemblies
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using DataTable   = System.Data.DataTable;
using Exception   = System.Exception;

namespace IWCCadToolsV9.UI
{
    public partial class ctlIWCProjNav : UserControl
    {
        // ---- Icons (keys) ----
        public const string IconKeyFolder = "folder";
        public const string IconKeyDataTable = "datatable";
        public const string IconKeyDashFolder = "dashfolder";
        public const string IconKeyDashMatFolder = "dash_mat_folder";
        public const string IconKeyDashHdwFolder = "dash_hdw_folder";

        // Desired min sizes (can tweak here only)
        private const int DesiredMinPanel1 = 240;
        private const int DesiredMinPanel2 = 200;
        private const int DesiredLeftStart = 320; // initial left width you want

        private ContextMenuStrip? _dashFolderMenu;
        private ToolStripMenuItem? _miDashFolderRefresh;

        // Material context menu
        private ContextMenuStrip  _matMenu               = new ContextMenuStrip();
        private ToolStripMenuItem _miAddMaterial          = new ToolStripMenuItem("Add New Material");
        private ToolStripMenuItem _miEditMaterial         = new ToolStripMenuItem("Edit Material");
        private ToolStripMenuItem _miAssociateToDash      = new ToolStripMenuItem("Associate to Current Dash");

        // Hardware context menu
        private ContextMenuStrip  _hdwMenu               = new ContextMenuStrip();
        private ToolStripMenuItem _miAddHardware          = new ToolStripMenuItem("Add New Hardware");
        private ToolStripMenuItem _miEditHardware         = new ToolStripMenuItem("Edit Hardware");
        private ToolStripMenuItem _miAssociateHdwToDash   = new ToolStripMenuItem("Associate to Current Dash");

        // Data binding
        private readonly BindingSource _gridBinding = new BindingSource();

        // Track current DataView for filtering (optional future use)
        private DataView? _currentView;

        // Tracks the active document's service so we can unsubscribe cleanly
        private ProjectContextService? _currentNavSvc;

        // ---- Child node labels (fixed order) ----
        private static readonly string[] ChildNodeNames = new[]
        {
            "Dash List",
            "Project Material List",
            "Project Hardware List",
            "Drawing Series List",
            "Shop Orders List",
            "Project Specifications",
            "Project Assets"
        };

        public ctlIWCProjNav()
        {
            InitializeComponent();

            // Bind grid ONCE to the binding source
            dataGrid.AutoGenerateColumns = true;
            dataGrid.DataSource = _gridBinding;
            dataGrid.ColumnHeadersVisible = true;
            dataGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            dataGrid.ColumnHeadersHeight = 30;
            dataGrid.EnableHeadersVisualStyles = true;
            dataGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;

            // Make every generated column sortable
            dataGrid.DataBindingComplete += (s, e) =>
            {
                foreach (DataGridViewColumn col in dataGrid.Columns)
                {
                    if (string.IsNullOrEmpty(col.HeaderText))
                        col.HeaderText = col.DataPropertyName;
                    col.SortMode = DataGridViewColumnSortMode.Automatic;
                }
                dataGrid.ColumnHeadersVisible = true;
            };

            SetupIcons();
            ConfigureTreeEvents();
            InitMaterialContextMenu();
            InitHardwareContextMenu();
            //InitDashFolderContextMenu();

            // Subscribe to document switches so the tree refreshes on each drawing
            Application.DocumentManager.DocumentActivated += OnNavDocumentActivated;

            // Bind to whichever document is active right now
            SubscribeToNavContext(Application.DocumentManager.MdiActiveDocument);
        }

        private void SubscribeToNavContext(
            Autodesk.AutoCAD.ApplicationServices.Document? doc)
        {
            if (_currentNavSvc != null)
                _currentNavSvc.ProjectLoaded -= OnProjectChanged;

            _currentNavSvc = doc == null ? null : ProjectContextService.GetOrCreate(doc);

            if (_currentNavSvc != null)
                _currentNavSvc.ProjectLoaded += OnProjectChanged;
        }

        private void OnNavDocumentActivated(object? sender,
            Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
        {
            SubscribeToNavContext(e.Document);
            if (e.Document != null)
                ReloadFromContext();
        }

        private void OnProjectChanged(object? sender, EventArgs e) => ReloadFromContext();

        /// <summary>
        /// If a project is active in the current context, show only that project.
        /// Falls back to all active projects when no context project is set.
        /// </summary>
        private void ReloadFromContext()
        {
            var svc = _currentNavSvc;
            if (svc?.HasProject == true && svc.Project != null)
                ReloadForProject(svc.Project.Id, svc.Project.IdNum, svc.Project.Name);
            else
                ShowNoActiveProject();
        }

        private void ShowNoActiveProject()
        {
            if (InvokeRequired) { BeginInvoke(new Action(ShowNoActiveProject)); return; }
            tree.BeginUpdate();
            tree.Nodes.Clear();
            var placeholder = new TreeNode("No Active Project")
            {
                ImageKey         = IconKeyFolder,
                SelectedImageKey = IconKeyFolder,
                ForeColor        = System.Drawing.SystemColors.GrayText
            };
            tree.Nodes.Add(placeholder);
            tree.EndUpdate();
        }

        #region Material Context Menu

        private void InitMaterialContextMenu()
        {
            _miAddMaterial.Click     += (_, _) => OnAddMaterial();
            _miEditMaterial.Click    += (_, _) => OnEditMaterial();
            _miAssociateToDash.Click += (_, _) => OnAssociateToDash();
            _matMenu.Items.Add(_miAddMaterial);
            _matMenu.Items.Add(_miEditMaterial);
            _matMenu.Items.Add(new ToolStripSeparator());
            _matMenu.Items.Add(_miAssociateToDash);
        }

        /// <summary>
        /// Shows the right context menu based on what node was right-clicked.
        /// - "Project Material List" ChildTag  → Add New Material
        /// - MatGroupTag (group folder)         → Add New Material
        /// - MatItemTag (leaf item)             → Edit Material
        /// </summary>
        private void ShowNodeContextMenu(TreeNode? node, System.Drawing.Point location)
        {
            if (node == null) return;

            bool isMaterialListNode = node.Tag is ChildTag mct &&
                mct.ChildLabel?.Contains("Material", StringComparison.OrdinalIgnoreCase) == true;
            bool isMaterialGroup = node.Tag is MatGroupTag;
            bool isMaterialItem  = node.Tag is MatItemTag;

            bool isHardwareListNode = node.Tag is ChildTag hct &&
                hct.ChildLabel?.Contains("Hardware", StringComparison.OrdinalIgnoreCase) == true;
            bool isHardwareGroup = node.Tag is HdwGroupTag;
            bool isHardwareItem  = node.Tag is HdwItemTag;

            bool hasActiveDash   = _currentNavSvc?.HasDash == true;
            string dashLabel     = hasActiveDash
                ? $"Associate to Current Dash ({_currentNavSvc!.Dash!.DashNum})"
                : "Associate to Current Dash (no active dash)";

            if (isMaterialListNode || isMaterialGroup || isMaterialItem)
            {
                _miAddMaterial.Visible     = isMaterialListNode || isMaterialGroup;
                _miEditMaterial.Visible    = isMaterialItem;
                _miAssociateToDash.Visible = isMaterialItem;
                _miAssociateToDash.Enabled = hasActiveDash;
                _miAssociateToDash.Text    = dashLabel;
                _matMenu.Show(tree, location);
            }
            else if (isHardwareListNode || isHardwareGroup || isHardwareItem)
            {
                _miAddHardware.Visible          = isHardwareListNode || isHardwareGroup;
                _miEditHardware.Visible         = isHardwareItem;
                _miAssociateHdwToDash.Visible   = isHardwareItem;
                _miAssociateHdwToDash.Enabled   = hasActiveDash;
                _miAssociateHdwToDash.Text      = dashLabel;
                _hdwMenu.Show(tree, location);
            }
        }

        private void OnAddMaterial()
        {
            var node = tree.SelectedNode;
            if (node == null) return;

            // Resolve project ID and optional pre-selected group
            int  projectId = 0;
            int? groupId   = null;

            if (node.Tag is ChildTag ct)
                projectId = ct.ProjectId;
            else if (node.Tag is MatGroupTag mgt)
            {
                projectId = mgt.ProjectId;
                groupId   = mgt.GroupId;
            }

            if (projectId == 0) return;

            using var dlg = new FrmProjectMaterialEditor(projectId, groupId);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // Add just the new node into the correct group — no full reload needed
            InsertMaterialNodeIntoTree(node, dlg);
        }

        private void OnEditMaterial()
        {
            var node = tree.SelectedNode;
            if (node?.Tag is not MatItemTag mit) return;

            // Edit mode: pass projectId + itemId; dialog loads full record from DB
            using var dlg = new FrmProjectMaterialEditor(mit.ProjectId, mit.ItemId);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // Update the node text and tag in-place — no tree rebuild needed
            node.Text = $"{dlg.MatNo} - {dlg.MatDesc}";
            node.Tag  = MatItemTag.Create(mit.ProjectId, mit.ItemId,
                dlg.MatGroupId ?? mit.GroupId, dlg.MatNo, dlg.MatDesc);
        }

        private void OnAssociateToDash()
        {
            var node = tree.SelectedNode;
            if (node?.Tag is not MatItemTag mit) return;

            var svc = _currentNavSvc;
            if (svc?.HasDash != true || svc.Dash == null)
            {
                MessageBox.Show("No active dash is selected for this drawing.\n" +
                    "Select a dash via Change Project before associating materials.",
                    "Associate to Dash", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int dashId   = svc.Dash.DashId;
            string dashNum = svc.Dash.DashNum ?? dashId.ToString();

            // Prompt for optional quantity
            bool cancelled;
            long? qty = PromptForQty(mit.MatNo, mit.MatDesc, dashNum, out cancelled);
            if (cancelled) return;
            if (qty == null && !ConfirmedNullQty()) return;

            try
            {
                AssociateMaterialToDash(mit.ItemId, dashId, qty);
                MessageBox.Show(
                    $"Material '{mit.MatNo} – {mit.MatDesc}' associated to Dash {dashNum}" +
                    (qty.HasValue ? $" (Qty: {qty})" : " (no quantity entered)") + ".",
                    "Associate to Dash", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to associate material:\n{ex.Message}",
                    "Associate to Dash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Shows a simple input box for quantity. Returns the entered value,
        /// or null if the user left the box empty and clicked OK.
        /// Returns a sentinel of -1 if the user cancelled.
        /// </summary>
        private static long? PromptForQty(string? matNo, string? matDesc, string dashNum, out bool cancelled)
        {
            cancelled = false;
            using var frm = new Form
            {
                Text            = "Enter Quantity",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ClientSize      = new System.Drawing.Size(360, 140),
                MinimizeBox     = false, MaximizeBox = false,
                StartPosition   = FormStartPosition.CenterParent
            };

            var lbl = new Label
            {
                Text = $"Associating: {matNo} – {matDesc}\nDash: {dashNum}\n\nQuantity (leave blank to skip):",
                AutoSize = false, Dock = DockStyle.None,
                Location = new System.Drawing.Point(12, 10),
                Size     = new System.Drawing.Size(336, 60)
            };
            var txt = new TextBox
            {
                Location = new System.Drawing.Point(12, 74),
                Width    = 120
            };
            var btnOk     = new Button { Text = "OK",     Width = 80, DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
            btnOk.Location     = new System.Drawing.Point(108, 100);
            btnCancel.Location = new System.Drawing.Point(196, 100);

            frm.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            frm.AcceptButton = btnOk;
            frm.CancelButton = btnCancel;

            if (frm.ShowDialog() != DialogResult.OK)
            {
                cancelled = true;
                return null;
            }

            return long.TryParse(txt.Text.Trim(), out long v) ? v : null;
        }

        // Returns true if user confirmed they want to proceed without a quantity
        private bool ConfirmedNullQty()
        {
            var answer = MessageBox.Show(
                "No quantity was entered. Associate this material without a quantity?",
                "Associate to Dash", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            return answer == DialogResult.Yes;
        }

        private static void AssociateMaterialToDash(int matId, int dashId, long? qty)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();

            // Avoid duplicate associations — upsert pattern
            using var cmd = new SqlCommand(@"
                IF EXISTS (SELECT 1 FROM dbo.Proj_Mat_Dash WHERE MatID = @mid AND DashID = @did)
                    UPDATE dbo.Proj_Mat_Dash
                    SET    MatQty = @qty
                    WHERE  MatID  = @mid AND DashID = @did;
                ELSE
                    INSERT INTO dbo.Proj_Mat_Dash (MatID, DashID, MatQty)
                    VALUES (@mid, @did, @qty);", conn);

            cmd.Parameters.AddWithValue("@mid", matId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.Parameters.AddWithValue("@qty", qty.HasValue ? (object)qty.Value : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// After a successful Add, inserts the new item node directly into the
        /// correct group folder without rebuilding the entire materials branch.
        /// </summary>
        private void InsertMaterialNodeIntoTree(TreeNode contextNode, FrmProjectMaterialEditor dlg)
        {
            if (dlg.SavedItemId == null || dlg.MatGroupId == null) return;

            // Walk up to the "Project Material List" ChildTag node
            TreeNode? matListNode = contextNode;
            while (matListNode != null)
            {
                if (matListNode.Tag is ChildTag ct &&
                    ct.ChildLabel?.Contains("Material", StringComparison.OrdinalIgnoreCase) == true)
                    break;
                matListNode = matListNode.Parent;
            }
            if (matListNode?.Tag is not ChildTag listTag) return;

            // If the branch hasn't been expanded yet the group nodes don't exist —
            // force a load so the new item lands in the right place
            bool wasUnloaded = matListNode.Nodes.Count == 1 &&
                               matListNode.Nodes[0].Tag is PlaceholderTag;
            if (wasUnloaded)
            {
                try { LoadMaterialsForProject(matListNode, listTag.ProjectId); }
                catch { /* best-effort */ }
            }

            // Find the matching group node
            TreeNode? groupNode = null;
            foreach (TreeNode child in matListNode.Nodes)
            {
                if (child.Tag is MatGroupTag mgt && mgt.GroupId == dlg.MatGroupId.Value)
                {
                    groupNode = child;
                    break;
                }
            }
            if (groupNode == null) return;

            // Create and insert the new item node
            var newNode = new TreeNode($"{dlg.MatNo} - {dlg.MatDesc}")
            {
                ImageKey         = IconKeyDataTable,
                SelectedImageKey = IconKeyDataTable,
                Tag              = MatItemTag.Create(listTag.ProjectId, dlg.SavedItemId.Value,
                                       dlg.MatGroupId.Value, dlg.MatNo, dlg.MatDesc)
            };
            groupNode.Nodes.Add(newNode);
            groupNode.Expand();
            tree.SelectedNode = newNode;
            newNode.EnsureVisible();
        }

        #endregion

        #region Hardware Context Menu

        private void InitHardwareContextMenu()
        {
            _miAddHardware.Click        += (_, _) => OnAddHardware();
            _miEditHardware.Click       += (_, _) => OnEditHardware();
            _miAssociateHdwToDash.Click += (_, _) => OnAssociateHdwToDash();
            _hdwMenu.Items.Add(_miAddHardware);
            _hdwMenu.Items.Add(_miEditHardware);
            _hdwMenu.Items.Add(new ToolStripSeparator());
            _hdwMenu.Items.Add(_miAssociateHdwToDash);
        }

        private void OnAddHardware()
        {
            var node = tree.SelectedNode;
            if (node == null) return;

            int     projectId  = 0;
            int?    groupId    = null;
            string? groupTag   = null;

            if (node.Tag is ChildTag ct)
                projectId = ct.ProjectId;
            else if (node.Tag is HdwGroupTag hgt)
            {
                projectId = hgt.ProjectId;
                groupId   = hgt.GroupId;
                groupTag  = hgt.GroupTag;
            }
            if (projectId == 0) return;

            using var dlg = new FrmProjectHardwareEditor(projectId, groupId, groupTag);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            InsertHardwareNodeIntoTree(node, dlg);
        }

        private void OnEditHardware()
        {
            var node = tree.SelectedNode;
            if (node?.Tag is not HdwItemTag hit) return;

            using var dlg = new FrmProjectHardwareEditor(hit.ProjectId, hit.ItemId);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // Update node in-place
            node.Text = $"{dlg.HdwNo} - {dlg.HdwDesc}";
            node.Tag  = HdwItemTag.Create(hit.ProjectId, hit.ItemId,
                dlg.HdwGroupId ?? hit.GroupId, dlg.HdwNo, dlg.HdwDesc);
        }

        private void OnAssociateHdwToDash()
        {
            var node = tree.SelectedNode;
            if (node?.Tag is not HdwItemTag hit) return;

            var svc = _currentNavSvc;
            if (svc?.HasDash != true || svc.Dash == null)
            {
                MessageBox.Show("No active dash is selected for this drawing.\n" +
                    "Select a dash via Change Project before associating hardware.",
                    "Associate to Dash", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int    dashId  = svc.Dash.DashId;
            string dashNum = svc.Dash.DashNum ?? dashId.ToString();

            bool cancelled;
            long? qty = PromptForQty(hit.HdwNo, hit.HdwDesc, dashNum, out cancelled);
            if (cancelled) return;
            if (qty == null && !ConfirmedNullQty()) return;

            try
            {
                AssociateHardwareToDash(hit.ItemId, dashId, qty);
                MessageBox.Show(
                    $"Hardware '{hit.HdwNo} – {hit.HdwDesc}' associated to Dash {dashNum}" +
                    (qty.HasValue ? $" (Qty: {qty})" : " (no quantity entered)") + ".",
                    "Associate to Dash", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to associate hardware:\n{ex.Message}",
                    "Associate to Dash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void AssociateHardwareToDash(int hdwId, int dashId, long? qty)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                IF EXISTS (SELECT 1 FROM dbo.Proj_Hdw_Dash WHERE HdwID = @hid AND DashID = @did)
                    UPDATE dbo.Proj_Hdw_Dash SET HdwQty = @qty WHERE HdwID = @hid AND DashID = @did;
                ELSE
                    INSERT INTO dbo.Proj_Hdw_Dash (HdwID, DashID, HdwQty) VALUES (@hid, @did, @qty);",
                conn);
            cmd.Parameters.AddWithValue("@hid", hdwId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.Parameters.AddWithValue("@qty", qty.HasValue ? (object)qty.Value : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private void InsertHardwareNodeIntoTree(TreeNode contextNode, FrmProjectHardwareEditor dlg)
        {
            if (dlg.SavedItemId == null || dlg.HdwGroupId == null) return;

            // Walk up to "Project Hardware List" ChildTag node
            TreeNode? hdwListNode = contextNode;
            while (hdwListNode != null)
            {
                if (hdwListNode.Tag is ChildTag ct &&
                    ct.ChildLabel?.Contains("Hardware", StringComparison.OrdinalIgnoreCase) == true)
                    break;
                hdwListNode = hdwListNode.Parent;
            }
            if (hdwListNode?.Tag is not ChildTag listTag) return;

            // If branch not yet loaded, trigger load first
            bool wasUnloaded = hdwListNode.Nodes.Count == 1 &&
                               hdwListNode.Nodes[0].Tag is PlaceholderTag;
            if (wasUnloaded)
            {
                try { LoadHardwareForProject(hdwListNode, listTag.ProjectId); }
                catch { /* best-effort */ }
            }

            // Find the matching group node
            TreeNode? groupNode = null;
            foreach (TreeNode child in hdwListNode.Nodes)
            {
                if (child.Tag is HdwGroupTag hgt && hgt.GroupId == dlg.HdwGroupId.Value)
                { groupNode = child; break; }
            }
            if (groupNode == null) return;

            var newNode = new TreeNode($"{dlg.HdwNo} - {dlg.HdwDesc}")
            {
                ImageKey         = IconKeyDataTable,
                SelectedImageKey = IconKeyDataTable,
                Tag              = HdwItemTag.Create(listTag.ProjectId, dlg.SavedItemId.Value,
                                       dlg.HdwGroupId.Value, dlg.HdwNo, dlg.HdwDesc)
            };
            groupNode.Nodes.Add(newNode);
            groupNode.Expand();
            tree.SelectedNode = newNode;
            newNode.EnsureVisible();
        }

        #endregion

        #region Public API

        // Raw row data fetched off the UI thread
        private readonly record struct ProjectRow(int Id, int IdNum, string Name);

        /// <summary>
        /// Refreshes the left tree asynchronously so the UI thread is never blocked
        /// by the SQL connection timeout.  Safe to call from any thread.
        /// </summary>
        public void ReloadProjects()
        {
            // Marshal to the UI thread if called from a background thread
            // (e.g. from ProjectLoaded event after an async SQL load).
            if (InvokeRequired) { BeginInvoke(new Action(ReloadProjects)); return; }

            // Show placeholder immediately so the palette feels responsive
            tree.BeginUpdate();
            tree.Nodes.Clear();
            tree.Nodes.Add(new TreeNode("Loading projects…") { ImageKey = IconKeyFolder });
            tree.EndUpdate();

            // Fetch raw data on a thread-pool thread — never block the UI thread
            System.Threading.Tasks.Task.Run(() =>
            {
                List<ProjectRow> rows;
                string? errorMsg = null;
                try
                {
                    rows = FetchProjectRows();
                }
                catch (Exception ex)
                {
                    rows     = new List<ProjectRow>();
                    errorMsg = ex.Message;
                }

                // Marshal tree building back to the UI thread
                BeginInvoke(new Action(() => PopulateProjectTree(rows, errorMsg)));
            });
        }

        /// <summary>Queries the DB on a background thread — no UI access allowed here.</summary>
        private List<ProjectRow> FetchProjectRows()
        {
            var result = new List<ProjectRow>();
            using var conn = GetOpenSqlConnection();
            using var cmd  = new SqlCommand(
                @"SELECT ID, IDNum, Proj_Name AS ProjName
                  FROM dbo.Proj_CompileActive
                  WHERE Act_Drafting = 1
                  ORDER BY IDNum ASC;", conn);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                int    id    = SafeGet<int>(rdr, "ID");
                int    idNum = SafeGet<int>(rdr, "IDNum");
                string name  = SafeGet(rdr, "ProjName", fallback: $"Project {idNum}");
                result.Add(new ProjectRow(id, idNum, name));
            }
            return result;
        }

        /// <summary>Builds TreeNodes and populates the tree — must run on the UI thread.</summary>
        private void PopulateProjectTree(List<ProjectRow> rows, string? errorMsg)
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();

            if (errorMsg != null)
            {
                tree.Nodes.Add(new TreeNode($"⚠ Load failed: {errorMsg}") { ImageKey = IconKeyFolder });
                tree.EndUpdate();
                return;
            }

            foreach (var row in rows)
            {
                var parent = new TreeNode($"{row.IdNum:D4} - {row.Name}")
                {
                    ImageKey         = IconKeyFolder,
                    SelectedImageKey = IconKeyFolder,
                    Tag              = ProjectTag.Create(row.Id, row.IdNum, row.Name)
                };

                foreach (var child in ChildNodeNames)
                {
                    var childNode = new TreeNode(child)
                    {
                        ImageKey         = IconKeyDataTable,
                        SelectedImageKey = IconKeyDataTable,
                        Tag              = ChildTag.Create(row.Id, child)
                    };

                    if (child.Equals("Dash List", StringComparison.OrdinalIgnoreCase))
                        childNode.Nodes.Add(new TreeNode("...Load Dashes")        { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("DashListLoader")   });

                    if (child.Equals("Project Material List", StringComparison.OrdinalIgnoreCase)
                     || child.Equals("Project Materials List", StringComparison.OrdinalIgnoreCase))
                        childNode.Nodes.Add(new TreeNode("...Load Materials")     { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("MatListLoader")    });

                    if (child.Equals("Project Hardware List", StringComparison.OrdinalIgnoreCase))
                        childNode.Nodes.Add(new TreeNode("...Load Hardware")      { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("HdwListLoader")    });

                    if (child.Equals("Shop Orders List", StringComparison.OrdinalIgnoreCase))
                        childNode.Nodes.Add(new TreeNode("...Load Shop Orders")   { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("ShopOrdersLoader") });

                    parent.Nodes.Add(childNode);
                }

                tree.Nodes.Add(parent);
            }

            tree.CollapseAll();
            tree.EndUpdate();
        }

        /// <summary>
        /// Shows only the specified project in the tree, expanded so the user
        /// can immediately drill into its child nodes.  Called when a project
        /// context is active; no SQL query needed — data comes from the service.
        /// </summary>
        private void ReloadForProject(int id, string idNum, string name)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => ReloadForProject(id, idNum, name))); return; }

            tree.BeginUpdate();
            tree.Nodes.Clear();

            var parent = new TreeNode($"{idNum} - {name}")
            {
                ImageKey         = IconKeyFolder,
                SelectedImageKey = IconKeyFolder,
                Tag              = ProjectTag.Create(id, int.TryParse(idNum, out var n) ? n : 0, name)
            };

            foreach (var child in ChildNodeNames)
            {
                var childNode = new TreeNode(child)
                {
                    ImageKey         = IconKeyDataTable,
                    SelectedImageKey = IconKeyDataTable,
                    Tag              = ChildTag.Create(id, child)
                };

                if (child.Equals("Dash List", StringComparison.OrdinalIgnoreCase))
                    childNode.Nodes.Add(new TreeNode("...Load Dashes")      { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("DashListLoader")   });

                if (child.Equals("Project Material List", StringComparison.OrdinalIgnoreCase)
                 || child.Equals("Project Materials List", StringComparison.OrdinalIgnoreCase))
                    childNode.Nodes.Add(new TreeNode("...Load Materials")   { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("MatListLoader")    });

                if (child.Equals("Project Hardware List", StringComparison.OrdinalIgnoreCase))
                    childNode.Nodes.Add(new TreeNode("...Load Hardware")    { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("HdwListLoader")    });

                if (child.Equals("Shop Orders List", StringComparison.OrdinalIgnoreCase))
                    childNode.Nodes.Add(new TreeNode("...Load Shop Orders") { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder, Tag = PlaceholderTag.For("ShopOrdersLoader") });

                parent.Nodes.Add(childNode);
            }

            tree.Nodes.Add(parent);
            parent.Expand();   // expand the project node so children are immediately visible
            tree.EndUpdate();
        }

        /// <summary>
        /// Replace or add an icon in the control’s ImageList by key.
        /// </summary>
        private void SetupIcons()
        {
            images.Images.Clear();

            // Base folder
            Image folder = null;
            try { folder = IWCCadToolsV9.Properties.Resources.IWCTreeFolder2; } catch { }
            folder ??= SystemIcons.WinLogo.ToBitmap();
            images.Images.Add(IconKeyFolder, folder);

            // Data table
            Image dt = null;
            try { dt = IWCCadToolsV9.Properties.Resources.DataTable; } catch { }
            dt ??= SystemIcons.Application.ToBitmap();
            images.Images.Add(IconKeyDataTable, dt);

            // Dash (generic)
            Image dashFolder = null;
            try { dashFolder = IWCCadToolsV9.Properties.Resources.IWCTreeDashFolder; } catch { }
            dashFolder ??= folder;
            images.Images.Add(IconKeyDashFolder, dashFolder);

            // Dash Materials (NEW)
            Image dashMat = null;
            try { dashMat = IWCCadToolsV9.Properties.Resources.IWCTreeMatFolder; } catch { }
            dashMat ??= dashFolder;
            images.Images.Add(IconKeyDashMatFolder, dashMat);

            // Dash Hardware (NEW)
            Image dashHdw = null;
            try { dashHdw = IWCCadToolsV9.Properties.Resources.IWCTreeHdwFolder; } catch { }
            dashHdw ??= dashFolder;
            images.Images.Add(IconKeyDashHdwFolder, dashHdw);
        }



        #endregion

        #region Private helpers

        private SqlConnection GetOpenSqlConnection()
        {
            // Prefer your existing connection helper if available.
            var conn = TryOpen(() => IWCConn.GetOpenConnection());
            if (conn != null) return conn;

            conn = TryOpen(() =>
            {
                var c = IWCConn.GetSqlConnection();
                c.Open();
                return c;
            });

            if (conn != null) return conn;

            throw new InvalidOperationException("No SQL connection method is available. Wire this to IWCConn.");
        }

        private static SqlConnection? TryOpen(Func<SqlConnection> factory)
        {
            try { return factory(); }
            catch { return null; }
        }

        private static T SafeGet<T>(SqlDataReader rdr, string colName, T fallback = default)
        {
            int i;
            try { i = rdr.GetOrdinal(colName); }
            catch { return fallback; }

            if (rdr.IsDBNull(i)) return fallback;
            object v = rdr.GetValue(i);

            try
            {
                if (typeof(T) == typeof(string)) return (T)(object)Convert.ToString(v);
                if (typeof(T).IsEnum) return (T)Enum.ToObject(typeof(T), v);
                return (T)Convert.ChangeType(v, typeof(T));
            }
            catch { return fallback; }
        }

        // --- DataRow-safe helpers (avoid Field<int> on bigint) ---
        private static string? SafeStr(DataRow? r, string col)
            => r != null && r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToString(r[col]) : null;

        private static int SafeInt(DataRow? r, string col, int fallback = 0)
            => r != null && r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToInt32(r[col]) : fallback;

        private static int? SafeIntOrNull(DataRow? r, string col)
            => r != null && r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToInt32(r[col]) : (int?)null;

        private static DateTime? SafeDateOrNull(DataRow r, string col)
            => r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToDateTime(r[col]) : (DateTime?)null;

        

        private void ConfigureTreeEvents()
        {
            // When a node is selected, show details / run queries
            tree.AfterSelect += (s, e) =>
            {
                // Hardware item leaf (Project Hardware List) → show details in grid
                if (e.Node?.Tag is HdwItemTag hi)
                {
                    var t = new DataTable("Hardware");
                    t.Columns.Add("ItemID", typeof(int));
                    t.Columns.Add("GroupID", typeof(int));
                    t.Columns.Add("HdwNo", typeof(string));
                    t.Columns.Add("HdwDesc", typeof(string));
                    t.Rows.Add(hi.ItemId, hi.GroupId, hi.HdwNo, hi.HdwDesc);
                    UpdateGrid(t, "Hardware");
                    return;
                }

                // Material item leaf (Project Material List) → show details in grid
                if (e.Node?.Tag is MatItemTag mi)
                {
                    var t = new DataTable("Material");
                    t.Columns.Add("ItemID", typeof(int));
                    t.Columns.Add("GroupID", typeof(int));
                    t.Columns.Add("MatNo", typeof(string));
                    t.Columns.Add("MatDesc", typeof(string));
                    t.Rows.Add(mi.ItemId, mi.GroupId, mi.MatNo, mi.MatDesc);
                    UpdateGrid(t, "Material");
                    return;
                }

                // Selecting a series/component dash → small summary
                if (e.Node?.Tag is DashTag dt)
                {
                    var t = new DataTable("Dash");
                    t.Columns.Add("DashID", typeof(int));
                    t.Columns.Add("DashNum", typeof(string));
                    t.Columns.Add("Desc", typeof(string));
                    t.Rows.Add(dt.DashId, dt.DashNumText, dt.DashDesc);
                    UpdateGrid(t, "Dash");
                    return;
                }

                // Dash → Materials → leaf (dash-scoped materials)
                if (e.Node?.Tag is MatDashItemTag mdi)
                {
                    var t = new DataTable("DashMaterial");
                    t.Columns.Add("ItemID", typeof(int));
                    t.Columns.Add("DashID", typeof(int));
                    t.Columns.Add("MatGroup", typeof(string));
                    t.Columns.Add("MatNo", typeof(string));
                    t.Columns.Add("MatDesc", typeof(string));
                    t.Rows.Add(mdi.ItemId, mdi.SeriesDashId, mdi.MatGroup, mdi.MatNo, mdi.MatDesc);
                    UpdateGrid(t, "Dash Material");
                    return;
                }
                if (e.Node?.Tag is DashPOItemTag poi)
                {
                    var t = new DataTable("PO Item");
                    t.Columns.Add("PONum", typeof(string));
                    t.Columns.Add("Component", typeof(string));
                    t.Columns.Add("Description", typeof(string));
                    t.Columns.Add("PO_ID", typeof(int));
                    t.Columns.Add("ID", typeof(int));
                    t.Rows.Add(poi.PONum, poi.Name_Comp, poi.ItemProductDesc,
                               poi.POId.GetValueOrDefault(), poi.POItemId.GetValueOrDefault());
                    UpdateGrid(t, "PO Item");
                    return;
                }

                // Selecting a top-level project → prompt user to choose a child
                if (e.Node?.Tag is ProjectTag)
                {
                    UpdateGrid(CreateEmptyTable("Select a child node to view data."), "Info");
                    return;
                }

                // Selecting an unhandled child node → placeholder
                if (e.Node?.Tag is ChildTag ct)
                {
                    UpdateGrid(CreateEmptyTable($"{ct.ChildLabel}: select a sub-item or expand to load."), "Info");
                    return;
                }

                // Selecting a dash section folder (Materials/Hardware/PO Items/Shop Orders/Assets) → placeholder
                if (e.Node?.Tag is DashSectionTag st)
                {
                    UpdateGrid(CreateEmptyTable($"Right-click '{st.Section}' and choose Refresh."), "Info");
                    return;
                }
            };

            // Right-click selects node and shows appropriate context menu
            tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                tree.SelectedNode = e.Node;
                ShowNodeContextMenu(e.Node, e.Location);
            };

            // Double-click toggles expand/collapse
            tree.NodeMouseDoubleClick += (s, e) =>
            {
                if (e.Node == null) return;
                if (e.Node.IsExpanded) e.Node.Collapse();
                else e.Node.Expand();
            };

            // Lazy loaders on expand
            tree.BeforeExpand += (s, e) =>
            {
                // Project-level: Dash List
                if (e.Node?.Tag is ChildTag dct &&
                    dct.ChildLabel.Equals("Dash List", StringComparison.OrdinalIgnoreCase))
                {
                    if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is PlaceholderTag ph
                        && string.Equals(ph.Purpose, "DashListLoader", StringComparison.OrdinalIgnoreCase))
                    {
                        try { LoadDashesForProject(e.Node, dct.ProjectId); }
                        catch (Exception ex) { MessageBox.Show($"Failed to load dashes:\n{ex.Message}", "Project Navigator", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    }
                    return;
                }

                // Project-level: Materials list
                if (e.Node?.Tag is ChildTag mct &&
                    (mct.ChildLabel.Equals("Project Material List", StringComparison.OrdinalIgnoreCase)
                  || mct.ChildLabel.Equals("Project Materials List", StringComparison.OrdinalIgnoreCase)))
                {
                    if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is PlaceholderTag mph
                        && string.Equals(mph.Purpose, "MatListLoader", StringComparison.OrdinalIgnoreCase))
                    {
                        try { LoadMaterialsForProject(e.Node, mct.ProjectId); }
                        catch (Exception ex) { MessageBox.Show($"Failed to load materials:\n{ex.Message}", "Project Navigator", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    }
                    return;
                }

                // Project-level: Hardware list
                if (e.Node?.Tag is ChildTag hct &&
                    hct.ChildLabel.Equals("Project Hardware List", StringComparison.OrdinalIgnoreCase))
                {
                    if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is PlaceholderTag hph
                        && string.Equals(hph.Purpose, "HdwListLoader", StringComparison.OrdinalIgnoreCase))
                    {
                        try { LoadHardwareForProject(e.Node, hct.ProjectId); }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to load hardware:\n{ex.Message}", "Project Navigator",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    return;
                }

                // Project-level: Shop Orders list
                if (e.Node?.Tag is ChildTag soCt &&
                    soCt.ChildLabel.Equals("Shop Orders List", StringComparison.OrdinalIgnoreCase))
                {
                    if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is PlaceholderTag sop
                        && string.Equals(sop.Purpose, "ShopOrdersLoader", StringComparison.OrdinalIgnoreCase))
                    {
                        try { LoadShopOrdersForProject(e.Node, soCt.ProjectId); }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to load shop orders:\n{ex.Message}", "Project Navigator",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    return;
                }

                // Unified Dash-level: Materials / Hardware / etc.  (single branch only)
                if (e.Node?.Tag is DashSectionTag dst)
                {
                    // Needs hydration if no children OR first child is the lazy placeholder
                    bool needsHydrate = e.Node.Nodes.Count == 0
                                     || (e.Node.Nodes.Count > 0 && IsLazyPlaceholder(e.Node.Nodes[0])
                                         || e.Node.Nodes[0].Tag is LoaderTag); // also handle LoaderTag placeholder

                    if (!needsHydrate) return;

                    try
                    {
                        // Drop any placeholders if present
                        RemoveLazyPlaceholders(e.Node);
                        if (e.Node.Nodes.Count > 0 && e.Node.Nodes[0].Tag is LoaderTag)
                            RemoveMaterialsLoaderPlaceholder(e.Node);

                        switch (dst.Section)
                        {
                            case DashSection.Materials:
                                ReloadDashFolder(e.Node, dst);
                                break;

                            case DashSection.Hardware:
                                EnsureDashHardwareLoaded(e.Node, dst.ProjectId, dst.SeriesDashId);
                                break;

                            case DashSection.POItems:
                                EnsureDashPOItemsLoaded(e.Node, dst.ProjectId, dst.SeriesDashId);
                                break;

                            case DashSection.ShopOrders:                                        // NEW
                                EnsureDashShopOrdersLoaded(e.Node, dst.ProjectId, dst.SeriesDashId);
                                break;

                            case DashSection.Assets:
                            default:
                                // No-op (or plug in loaders later)
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Failed to load dash {dst.Section}:\n{ex.Message}",
                            "Project Navigator",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }

                    return;
                }

            };
        }

        private static DataTable CreateEmptyTable(string message)
        {
            var t = new DataTable("Info");
            t.Columns.Add("Message", typeof(string));
            t.Rows.Add(message);
            return t;
        }

        private static bool HasOnlyPlaceholder(TreeNode node, string? purpose = null)
        {
            if (node == null || node.Nodes.Count != 1) return false;
            if (node.Nodes[0].Tag is not PlaceholderTag ph) return false;
            return purpose == null || string.Equals(ph.Purpose, purpose, StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearLoaderPlaceholder(TreeNode node, string? purpose = null)
        {
            if (node == null || node.Nodes.Count == 0) return;
            if (node.Nodes[0].Tag is PlaceholderTag ph &&
                (purpose == null || string.Equals(ph.Purpose, purpose, StringComparison.OrdinalIgnoreCase)))
            {
                node.Nodes.Clear();
            }
        }

        private void EnsureMaterialsLoaderPlaceholder(TreeNode materialsFolder)
        {
            if (materialsFolder == null) return;

            // Only add if not already present
            bool hasLoader = materialsFolder.Nodes
                .Cast<TreeNode>()
                .Any(n => n.Tag is LoaderTag lt && string.Equals(lt.Kind, "DashMaterials", StringComparison.OrdinalIgnoreCase));

            if (!hasLoader)
            {
                var n = new TreeNode("…Load Materials")
                {
                    Tag = new LoaderTag("DashMaterials"),
                    ImageKey = IconKeyDataTable,
                    SelectedImageKey = IconKeyDataTable
                };
                // Put it first so users see it even if folder already has other items later
                materialsFolder.Nodes.Insert(0, n);
            }
        }
        private void RemoveMaterialsLoaderPlaceholder(TreeNode materialsFolder)
        {
            if (materialsFolder == null) return;

            for (int i = materialsFolder.Nodes.Count - 1; i >= 0; i--)
            {
                if (materialsFolder.Nodes[i].Tag is LoaderTag lt &&
                    string.Equals(lt.Kind, "DashMaterials", StringComparison.OrdinalIgnoreCase))
                {
                    materialsFolder.Nodes.RemoveAt(i);
                }
            }
        }

        // Robust placeholders
        private const string DUMMY_NODE_KEY = "__lazy_dummy__";
        private const string DUMMY_TEXT = "(loading...)";

        private static TreeNode NewDummyNode()
            => new TreeNode(DUMMY_TEXT) { Name = DUMMY_NODE_KEY };

        private static bool IsLazyPlaceholder(TreeNode n)
            => n != null && (
                   string.Equals(n.Name, DUMMY_NODE_KEY, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(n.Text) && n.Text.StartsWith("(loading", StringComparison.OrdinalIgnoreCase))
               );

        private static void RemoveLazyPlaceholders(TreeNode parent)
        {
            if (parent == null) return;
            for (int i = parent.Nodes.Count - 1; i >= 0; i--)
                if (IsLazyPlaceholder(parent.Nodes[i]))
                    parent.Nodes.RemoveAt(i);
        }

        private void EnsureDashHardwareLoaded(TreeNode hardwareFolderNode, int projectId, int dashId)
        {
            // If already populated (no placeholder present), do nothing.
            if (hardwareFolderNode?.Nodes == null) return;
            if (hardwareFolderNode.Nodes.Count > 0 && !IsLazyPlaceholder(hardwareFolderNode.Nodes[0]))
                return;

            Cursor prev = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                RemoveLazyPlaceholders(hardwareFolderNode);

                // 1) Pull rows for this DashID
                var dt = new DataTable();
                using (var conn = new IWCConn())
                {
                    conn.DBConnect();
                    using (var da = new SqlDataAdapter(@"
                        SELECT 
                            [ID],
                            [HdwNo],
                            [HdwDesc],
                            [HdwGroupID],
                            [HdwEdit],
                            [HdwNotes],
                            [HdwApprove],
                            [HdwVendorID],
                            [HdwUnit],
                            [HdwGroupNo],
                            [HdwGroupTag],
                            [Name_Comp],
                            [Comp_Type],
                            [DashID],
                            [HdwQty],
                            [HdwRQID]
                        FROM dbo.Proj_HdwDash_Compile
                        WHERE [DashID] = @DashId
                        ORDER BY [HdwGroupID], [HdwGroupNo], [HdwNo];", conn.OpenConn))
                    {
                        da.SelectCommand.Parameters.Add("@DashId", SqlDbType.Int).Value = dashId;
                        da.Fill(dt);
                    }
                    conn.DBClose();
                }

                // 2) Group by HdwGroupID (NULL-safe, bigint-safe)
                var groups = dt.AsEnumerable()
                               .GroupBy(r => SafeIntOrNull(r, "HdwGroupID") ?? -1)
                               .OrderBy(g => g.Key);

                foreach (var g in groups)
                {
                    var first = g.FirstOrDefault();
                    string? grpNo = SafeStr(first, "HdwGroupNo");
                    string? grpTag = SafeStr(first, "HdwGroupTag");
                    int grpId = SafeIntOrNull(first, "HdwGroupID") ?? -1;

                    string folderName = MakeHardwareGroupFolderName(grpTag, grpNo);
                    if (grpId == -1 && string.IsNullOrWhiteSpace(folderName))
                        folderName = "Unassigned Hardware";

                    var gnode = new TreeNode(folderName)
                    {
                        Tag = new DashHardwareGroupTag
                        {
                            ProjectId = projectId,
                            DashId = dashId,
                            HdwGroupId = grpId,
                            HdwGroupNo = grpNo,
                            HdwGroupTag = grpTag
                        },
                        ImageKey = IconKeyDashHdwFolder,
                        SelectedImageKey = IconKeyDashHdwFolder
                    };

                    // 3) Add item nodes under the group folder
                    foreach (var r in g)
                    {
                        var item = BuildHardwareItemTag(projectId, dashId, r);
                        var text = BuildHardwareItemCaption(item);

                        var inode = new TreeNode(text)
                        {
                            Tag = item,
                            ImageKey = IconKeyDataTable,   // use your item icon
                            SelectedImageKey = IconKeyDataTable
                        };
                        gnode.Nodes.Add(inode);
                    }

                    hardwareFolderNode.Nodes.Add(gnode);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hardware load failed:\n{ex.Message}", "Dash Hardware",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor.Current = prev;
            }
        }

        private static string MakeHardwareGroupFolderName(string? tag, string? no)
        {
            tag = string.IsNullOrWhiteSpace(tag) ? "Group" : tag.Trim();
            no = string.IsNullOrWhiteSpace(no) ? "" : no.Trim();
            return string.IsNullOrEmpty(no) ? tag : $"{tag} - {no}";
        }

        private DashHardwareItemTag BuildHardwareItemTag(int projectId, int dashId, DataRow r)
        {
            // Use Convert.ToInt32 for bigint safety; trim strings safely
            string? S(string col) => r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToString(r[col])?.Trim() : null;
            int? I(string col) => r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToInt32(r[col]) : (int?)null;
            DateTime? D(string col) => r.Table.Columns.Contains(col) && r[col] != DBNull.Value ? Convert.ToDateTime(r[col]) : (DateTime?)null;

            return new DashHardwareItemTag
            {
                ProjectId = projectId,
                DashId = dashId,                            // trust filter value; don't read nullable cell
                Id = I("ID") ?? 0,
                HdwNo = S("HdwNo"),
                HdwDesc = S("HdwDesc"),
                HdwGroupId = I("HdwGroupID") ?? -1,
                HdwEdit = D("HdwEdit"),
                HdwNotes = S("HdwNotes"),
                HdwApprove = D("HdwApprove"),
                HdwVendorId = I("HdwVendorID"),
                HdwUnit = S("HdwUnit"),                      // string as requested
                HdwGroupNo = S("HdwGroupNo"),
                HdwGroupTag = S("HdwGroupTag"),
                Name_Comp = S("Name_Comp"),
                Comp_Type = S("Comp_Type"),
                HdwQty = I("HdwQty") ?? 0,
                HdwRQID = I("HdwRQID") ?? 0
            };
        }

        private static string BuildHardwareItemCaption(DashHardwareItemTag t)
        {
            // Prefer Name_Comp if present, otherwise HdwDesc
            string desc = (!string.IsNullOrWhiteSpace(t.Name_Comp) ? t.Name_Comp
                        : !string.IsNullOrWhiteSpace(t.HdwDesc) ? t.HdwDesc
                        : "") ?? "";

            string qtyPart = t.HdwQty > 0 ? $" x{t.HdwQty}" : "";
            string numPart = string.IsNullOrWhiteSpace(t.HdwNo) ? "" : (t.HdwNo?.Trim() ?? "");

            string left = string.IsNullOrEmpty(numPart) ? $"Item{t.Id}" : numPart;
            string captionLeft = $"{left}{qtyPart}";

            return string.IsNullOrWhiteSpace(desc) ? captionLeft : $"{captionLeft} — {desc}";
        }

        private void EnsureDashPOItemsLoaded(TreeNode poFolderNode, int projectId, int dashId)
        {
            // Already populated? (first child is NOT a dummy) → nothing to do
            if (poFolderNode?.Nodes == null) return;
            if (poFolderNode.Nodes.Count > 0 && !IsLazyPlaceholder(poFolderNode.Nodes[0]))
                return;

            Cursor prev = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                RemoveLazyPlaceholders(poFolderNode);

                var dt = new DataTable();
                using (var conn = GetOpenSqlConnection())
                using (var da = new SqlDataAdapter(@"
                    SELECT 
                        -- optional ID columns first, if present
                        CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.PurchPO_CompileItem'), 'PO_ID', 'ColumnId') IS NOT NULL THEN PO_ID END AS PO_ID,
                        CASE WHEN COLUMNPROPERTY(OBJECT_ID('dbo.PurchPO_CompileItem'), 'ID', 'ColumnId') IS NOT NULL THEN ID END AS ID,

                        Dash_ID,
                        PONum,
                        Name_Comp,
                        ItemProductDesc
                    FROM dbo.PurchPO_CompileItem
                    WHERE Dash_ID = @DashId
                    ORDER BY TRY_CAST(PONum AS int), PONum, Name_Comp;", conn))
                {
                    da.SelectCommand.Parameters.AddWithValue("@DashId", dashId);
                    da.Fill(dt);
                }

                foreach (DataRow r in dt.Rows)
                {
                    string? poNum = SafeStr(r, "PONum") ?? "";
                    string? comp = SafeStr(r, "Name_Comp") ?? "";
                    string? desc = SafeStr(r, "ItemProductDesc") ?? "";

                    // Optional IDs, safe against bigint/NULL
                    int? poId = SafeIntOrNull(r, "PO_ID");
                    int? poItemId = SafeIntOrNull(r, "ID");

                    string title = $"{poNum} - {comp}: {desc}".Trim();

                    var node = new TreeNode(title)
                    {
                        ImageKey = IconKeyDataTable,
                        SelectedImageKey = IconKeyDataTable,
                        Tag = new DashPOItemTag
                        {
                            ProjectId = projectId,
                            DashId = dashId,
                            PONum = poNum,
                            Name_Comp = comp,
                            ItemProductDesc = desc,
                            POId = poId,
                            POItemId = poItemId
                        }
                    };

                    poFolderNode.Nodes.Add(node);
                }

                if (poFolderNode.Nodes.Count == 0)
                {
                    poFolderNode.Nodes.Add(new TreeNode("(no PO items found)")
                    {
                        ImageKey = IconKeyDataTable,
                        SelectedImageKey = IconKeyDataTable,
                        Tag = PlaceholderTag.For("EmptyPOItems")
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"PO Items load failed:\n{ex.Message}", "Dash PO Items",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor.Current = prev;
            }
        }

        private void EnsureDashShopOrdersLoaded(TreeNode soFolderNode, int projectId, int dashId)
        {
            if (soFolderNode?.Nodes == null) return;
            if (soFolderNode.Nodes.Count > 0 && !IsLazyPlaceholder(soFolderNode.Nodes[0]))
                return;

            Cursor prev = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                RemoveLazyPlaceholders(soFolderNode);

                var dt = new DataTable();
                using (var conn = GetOpenSqlConnection())
                using (var da = new SqlDataAdapter(@"
                    SELECT ID, ProjID, SO_Number, SO_Desc, Date_ActualShip
                    FROM dbo.ShopSO_CompiledSO
                    WHERE DashID = @dashId
                    ORDER BY TRY_CAST(SO_Number AS int), SO_Number;", conn))
                {
                    da.SelectCommand.Parameters.AddWithValue("@dashId", dashId);
                    da.Fill(dt);
                }

                foreach (DataRow r in dt.Rows)
                {
                    int soId = SafeInt(r, "ID");
                    string? soNumber = SafeStr(r, "SO_Number") ?? "";
                    string? soDesc = SafeStr(r, "SO_Desc") ?? "";
                    DateTime? dateShipped = SafeDateOrNull(r, "Date_ActualShip");

                    string text = $"{soNumber} - {soDesc}";
                    if (dateShipped.HasValue) text += $" - Shipped: {dateShipped:MM/dd/yyyy}";

                    var node = new TreeNode(text)
                    {
                        ImageKey = IconKeyDataTable,
                        SelectedImageKey = IconKeyDataTable,
                        Tag = ShopOrderTag.Create(projectId, soId, soNumber, soDesc, dateShipped)
                    };

                    soFolderNode.Nodes.Add(node);
                }

                if (soFolderNode.Nodes.Count == 0)
                {
                    soFolderNode.Nodes.Add(new TreeNode("(no shop orders found)")
                    {
                        ImageKey = IconKeyDataTable,
                        SelectedImageKey = IconKeyDataTable,
                        Tag = PlaceholderTag.For("EmptyDashShopOrders")
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Dash Shop Orders load failed:\n{ex.Message}", "Dash Shop Orders",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor.Current = prev;
            }
        }


        #endregion Private Helpers

        #region Node Tag Types

        private sealed class ProjectTag
        {
            public int ProjectId { get; }
            public int ProjectNum { get; }
            public string? ProjectName { get; }

            private ProjectTag(int projectId, int projectNum, string name)
            {
                ProjectId = projectId;
                ProjectNum = projectNum;
                ProjectName = name;
            }

            public static ProjectTag Create(int id, int num, string name) => new ProjectTag(id, num, name);
            public override string ToString() => $"{ProjectNum} - {ProjectName} (ID={ProjectId})";
        }

        private sealed class ChildTag
        {
            public int ProjectId { get; }
            public string? ChildLabel { get; }

            private ChildTag(int id, string label)
            {
                ProjectId = id;
                ChildLabel = label ?? "";
            }

            public static ChildTag Create(int projectId, string label) => new ChildTag(projectId, label);
            public override string ToString() => $"{ChildLabel} [ProjectID={ProjectId}]";
        }

        private sealed class PlaceholderTag
        {
            public string? Purpose { get; }
            private PlaceholderTag(string purpose) { Purpose = purpose ?? "placeholder"; }
            public static PlaceholderTag For(string purpose) => new PlaceholderTag(purpose);
            public override string ToString() => $"Placeholder({Purpose})";
        }

        private sealed class DashTag
        {
            public int ProjectId { get; }
            public int DashId { get; }
            public string? DashNumText { get; }   // preserves leading zeros
            public int? DashNum { get; }         // optional parsed int
            public string? DashDesc { get; }

            private DashTag(int projectId, int dashId, string dashNumText, int? dashNum, string dashDesc)
            {
                ProjectId = projectId;
                DashId = dashId;
                DashNumText = dashNumText ?? "";
                DashNum = dashNum;
                DashDesc = dashDesc ?? "";
            }

            // Preferred: create from TEXT (keeps leading zeros)
            public static DashTag Create(int projectId, int dashId, string dashNumText, string dashDesc)
                => new DashTag(projectId, dashId, dashNumText, TryParse(dashNumText), dashDesc);

            // Back-compat: create from INT
            public static DashTag Create(int projectId, int dashId, int dashNum, string dashDesc)
                => new DashTag(projectId, dashId, dashNum.ToString("D4"), dashNum, dashDesc);

            public string DashNumDisplay =>
                !string.IsNullOrEmpty(DashNumText) ? DashNumText :
                (DashNum.HasValue ? DashNum.Value.ToString("D4") : "");

            public override string ToString() => $"Dash {DashNumDisplay} ({DashId})";

            private static int? TryParse(string s) => int.TryParse(s, out var n) ? n : (int?)null;
        }

        private enum DashSection
        {
            Materials,
            Hardware,
            POItems,
            ShopOrders,
            Assets
        }

        private sealed class DashSectionTag
        {
            public int ProjectId { get; }
            public int SeriesDashId { get; }  // parent series/component dash DB ID
            public DashSection Section { get; }

            private DashSectionTag(int projectId, int seriesDashId, DashSection section)
            {
                ProjectId = projectId;
                SeriesDashId = seriesDashId;
                Section = section;
            }

            public static DashSectionTag Create(int projectId, int seriesDashId, DashSection section)
                => new DashSectionTag(projectId, seriesDashId, section);

            public override string ToString() => $"{Section} [Proj={ProjectId}, DashID={SeriesDashId}]";
        }

        // Group folder under Project Materials List
        private sealed class MatGroupTag
        {
            public int ProjectId { get; }
            public int GroupId { get; }
            public string? GroupName { get; }

            private MatGroupTag(int projectId, int groupId, string name)
            {
                ProjectId = projectId; GroupId = groupId; GroupName = name ?? "";
            }
            public static MatGroupTag Create(int projectId, int groupId, string name)
                => new MatGroupTag(projectId, groupId, name);
            public override string ToString() => $"Group {GroupId} - {GroupName}";
        }

        // Leaf material item under a Project Material group
        private sealed class MatItemTag
        {
            public int ProjectId { get; }
            public int ItemId { get; }
            public int GroupId { get; }
            public string? MatNo { get; }
            public string? MatDesc { get; }

            private MatItemTag(int projectId, int itemId, int groupId, string matNo, string matDesc)
            {
                ProjectId = projectId; ItemId = itemId; GroupId = groupId;
                MatNo = matNo ?? ""; MatDesc = matDesc ?? "";
            }
            public static MatItemTag Create(int projectId, int itemId, int groupId, string matNo, string matDesc)
                => new MatItemTag(projectId, itemId, groupId, matNo, matDesc);
            public override string ToString() => $"{MatNo} - {MatDesc} (ID={ItemId}, Group={GroupId})";
        }

        // Group folder under Project Hardware List
        private sealed class HdwGroupTag
        {
            public int ProjectId { get; }
            public int GroupId { get; }
            public string? GroupTag { get; }   // HdwGroupTag
            public string? GroupName { get; }  // HdwGroup

            private HdwGroupTag(int projectId, int groupId, string tag, string name)
            {
                ProjectId = projectId; GroupId = groupId; GroupTag = tag ?? ""; GroupName = name ?? "";
            }
            public static HdwGroupTag Create(int projectId, int groupId, string tag, string name)
                => new HdwGroupTag(projectId, groupId, tag, name);
            public override string ToString() => $"{GroupTag} - {GroupName} (ID={GroupId})";
        }

        // Leaf hardware item under a Project Hardware group
        private sealed class HdwItemTag
        {
            public int ProjectId { get; }
            public int ItemId { get; }
            public int GroupId { get; }
            public string? HdwNo { get; }
            public string? HdwDesc { get; }

            private HdwItemTag(int projectId, int itemId, int groupId, string hdwNo, string hdwDesc)
            {
                ProjectId = projectId; ItemId = itemId; GroupId = groupId;
                HdwNo = hdwNo ?? ""; HdwDesc = hdwDesc ?? "";
            }
            public static HdwItemTag Create(int projectId, int itemId, int groupId, string hdwNo, string hdwDesc)
                => new HdwItemTag(projectId, itemId, groupId, hdwNo, hdwDesc);
            public override string ToString() => $"{HdwNo} - {HdwDesc} (ID={ItemId}, Group={GroupId})";
        }

        // Dash ➜ Materials: group folder tag (string name for display)
        private sealed class MatDashGroupTag
        {
            public int ProjectId { get; }
            public int SeriesDashId { get; }
            public string? MatGroup { get; }

            private MatDashGroupTag(int projectId, int seriesDashId, string matGroup)
            {
                ProjectId = projectId;
                SeriesDashId = seriesDashId;
                MatGroup = matGroup ?? "";
            }

            public static MatDashGroupTag Create(int projectId, int seriesDashId, string matGroup)
                => new MatDashGroupTag(projectId, seriesDashId, matGroup);

            public override string ToString() => $"{MatGroup} (DashID={SeriesDashId})";
        }

        // Dash ➜ Materials: leaf item tag
        private sealed class MatDashItemTag
        {
            public int ProjectId { get; }
            public int SeriesDashId { get; }
            public int ItemId { get; }
            public string? MatGroup { get; }
            public string? MatNo { get; }
            public string? MatDesc { get; }

            private MatDashItemTag(int projectId, int seriesDashId, int itemId, string matGroup, string matNo, string matDesc)
            {
                ProjectId = projectId;
                SeriesDashId = seriesDashId;
                ItemId = itemId;
                MatGroup = matGroup ?? "";
                MatNo = matNo ?? "";
                MatDesc = matDesc ?? "";
            }

            public static MatDashItemTag Create(int projectId, int seriesDashId, int itemId, string matGroup, string matNo, string matDesc)
                => new MatDashItemTag(projectId, seriesDashId, itemId, matGroup, matNo, matDesc);

            public override string ToString() => $"{MatNo} - {MatDesc} (ID={ItemId})";
        }

        private sealed class ShopOrderTag
        {
            public int ProjectId { get; }
            public int SOId { get; }
            public string? SONumber { get; }
            public string? SODesc { get; }
            public DateTime? DateActualShip { get; }

            private ShopOrderTag(int projectId, int soId, string soNumber, string soDesc, DateTime? dateActualShip)
            {
                ProjectId = projectId;
                SOId = soId;
                SONumber = soNumber ?? "";
                SODesc = soDesc ?? "";
                DateActualShip = dateActualShip;
            }

            public static ShopOrderTag Create(int projectId, int soId, string soNumber, string soDesc, DateTime? dateActualShip)
                => new ShopOrderTag(projectId, soId, soNumber, soDesc, dateActualShip);

            public override string ToString()
                => $"{SONumber} - {SODesc}" + (DateActualShip.HasValue ? $" - Shipped: {DateActualShip:MM/dd/yyyy}" : "");
        }

        // Dash Hardware tags
        private sealed class DashHardwareGroupTag
        {
            public int ProjectId { get; init; }
            public int DashId { get; init; }
            public int HdwGroupId { get; init; }
            public string? HdwGroupNo { get; init; }
            public string? HdwGroupTag { get; init; }
        }

        private sealed class DashHardwareItemTag
        {
            public int ProjectId { get; init; }
            public int DashId { get; init; }

            public int Id { get; init; }                 // [ID]
            public string? HdwNo { get; init; }           // [HdwNo]
            public string? HdwDesc { get; init; }         // [HdwDesc]
            public int HdwGroupId { get; init; }         // [HdwGroupID]
            public DateTime? HdwEdit { get; init; }      // [HdwEdit]
            public string? HdwNotes { get; init; }        // [HdwNotes]
            public DateTime? HdwApprove { get; init; }   // [HdwApprove]
            public int? HdwVendorId { get; init; }       // [HdwVendorID]
            public string? HdwUnit { get; init; }        // [HdwUnit] (string per request)
            public string? HdwGroupNo { get; init; }      // [HdwGroupNo]
            public string? HdwGroupTag { get; init; }     // [HdwGroupTag]
            public string? Name_Comp { get; init; }       // [Name_Comp]
            public string? Comp_Type { get; init; }       // [Comp_Type]
            public int HdwQty { get; init; }             // D.[HdwQty]
            public int HdwRQID { get; init; }            // D.[HdwRQID]
        }

        // Lightweight loader tag so we can detect/remove temp nodes reliably
        private sealed class LoaderTag
        {
            public string? Kind { get; }
            public LoaderTag(string kind) { Kind = string.IsNullOrWhiteSpace(kind) ? "loader" : kind.Trim(); }
            public override string ToString() => $"Loader({Kind})";
        }

        private sealed class DashPOItemTag
        {
            public int ProjectId { get; init; }
            public int DashId { get; init; }

            // Minimal set for caption; include optional IDs if present
            public string? PONum { get; init; }
            public string? Name_Comp { get; init; }
            public string? ItemProductDesc { get; init; }

            public int? POId { get; init; }           // if the view exposes it (optional)
            public int? POItemId { get; init; }       // if the view exposes it (optional)
            public override string ToString() => $"{PONum} - {Name_Comp}: {ItemProductDesc}";
        }

        #endregion

        #region Designer host convenience

        protected override void OnCreateControl()
        {
            base.OnCreateControl();

            if (!DesignMode)
                // Defer past OnCreateControl so the palette window is fully visible
                // before the background SQL query starts — prevents a blank hang.
                BeginInvoke(new Action(ReloadFromContext));
            else
                ShowDesignTimePreview();

            // Defer min sizes & splitter distance until layout is usable.
            InitSplitterSafely();
        }

        private void ShowDesignTimePreview()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();

            var p = new TreeNode("100123 - Sample Project") { ImageKey = IconKeyFolder, SelectedImageKey = IconKeyFolder };
            foreach (var c in ChildNodeNames)
                p.Nodes.Add(new TreeNode(c) { ImageKey = IconKeyDataTable, SelectedImageKey = IconKeyDataTable });

            tree.Nodes.Add(p);
            tree.CollapseAll();
            tree.EndUpdate();

            UpdateGrid(CreateEmptyTable("Design-time preview"), "Info");
        }

        #endregion

        #region Splitter sizing (robust)

        private void InitSplitterSafely()
        {
            if (!split.IsHandleCreated)
            {
                split.HandleCreated += Split_HandleCreatedOnce;
                return;
            }

            if (!HasUsableWidthFor(DesiredMinPanel1, DesiredMinPanel2))
            {
                // Relax mins to avoid exceptions on tiny startup sizes
                split.Panel1MinSize = 0;
                split.Panel2MinSize = 0;

                split.SizeChanged += Split_SizeChangedRaiseMinsOnce;
                return;
            }

            ApplyMinsAndDistance();
        }

        private void Split_HandleCreatedOnce(object? sender,  EventArgs e)
        {
            split.HandleCreated -= Split_HandleCreatedOnce;
            InitSplitterSafely();
        }

        private void Split_SizeChangedRaiseMinsOnce(object? sender,  EventArgs e)
        {
            if (!HasUsableWidthFor(DesiredMinPanel1, DesiredMinPanel2)) return;

            split.SizeChanged -= Split_SizeChangedRaiseMinsOnce;
            ApplyMinsAndDistance();
        }

        private bool HasUsableWidthFor(int min1, int min2)
        {
            int total = split.Width;
            int sw = split.SplitterWidth;
            return total >= (min1 + min2 + sw + 1) && total > 0;
        }

        private void ApplyMinsAndDistance()
        {
            int total = split.Width;
            int sw = split.SplitterWidth;

            // Set desired mins first to establish constraints
            split.Panel1MinSize = DesiredMinPanel1;
            split.Panel2MinSize = DesiredMinPanel2;

            // Compute legal range for SplitterDistance
            int minDistance = split.Panel1MinSize;
            int maxDistance = Math.Max(minDistance, total - split.Panel2MinSize - sw);

            // Clamp desired left width
            int desired = DesiredLeftStart;
            int clamped = Math.Min(Math.Max(desired, minDistance), maxDistance);

            if (split.SplitterDistance < minDistance || split.SplitterDistance > maxDistance)
                split.SplitterDistance = clamped;
        }

        #endregion

        #region Data loading & grid update

        private void UpdateGrid(DataTable table, string friendlyName)
        {
            if (table != null)
            {
                _currentView = table.DefaultView;
            }
            else
            {
                var empty = CreateEmptyTable($"{friendlyName}: no data");
                _currentView = empty.DefaultView;
            }

            _gridBinding.DataSource = _currentView;
        }

        // --- Project-level list loaders (left-side fixed children) ---

        private void LoadMaterialsForProject(TreeNode matListNode, int projectId)
        {
            if (matListNode == null) return;

            if (matListNode.Nodes.Count == 1 && matListNode.Nodes[0].Tag is PlaceholderTag)
                matListNode.Nodes.Clear();

            // 1) Get all groups
            var groups = new List<(int Id, string Name)>();
            using (var conn = GetOpenSqlConnection())
            using (var gcmd = new SqlCommand(
                @"SELECT ID, MatGroup 
                  FROM dbo.Proj_MatGroup
                  ORDER BY MatGroup ASC;", conn))
            using (var rdr = gcmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    int gid = SafeGet<int>(rdr, "ID");
                    string gname = SafeGet<string>(rdr, "MatGroup", "");
                    groups.Add((gid, gname));
                }
            }

            var groupNodeById = new Dictionary<int, TreeNode>();
            foreach (var g in groups)
            {
                var gNode = new TreeNode(g.Name)
                {
                    ImageKey = IconKeyFolder,
                    SelectedImageKey = IconKeyFolder,
                    Tag = MatGroupTag.Create(projectId, g.Id, g.Name)
                };
                matListNode.Nodes.Add(gNode);
                groupNodeById[g.Id] = gNode;
            }

            // 2) Get all material items for this project (once)
            var items = new DataTable();
            using (var conn = GetOpenSqlConnection())
            using (var icmd = new SqlCommand(
                @"SELECT ID, ProjID, MatNo, MatDesc, MatGroup
                  FROM dbo.Proj_MatCompile
                  WHERE ProjID = @pid
                  ORDER BY TRY_CAST(MatNo AS int), MatNo;", conn))
            {
                icmd.Parameters.AddWithValue("@pid", projectId);
                using (var da = new SqlDataAdapter(icmd))
                    da.Fill(items);
            }

            foreach (DataRow row in items.Rows)
            {
                int grpId = SafeIntOrNull(row, "MatGroup") ?? -1;
                if (!groupNodeById.TryGetValue(grpId, out var gNode)) continue;

                int itemId = SafeInt(row, "ID");
                string matNo = Convert.ToString(row["MatNo"]) ?? "";
                string matDesc = Convert.ToString(row["MatDesc"]) ?? "";

                var itemNode = new TreeNode($"{matNo} - {matDesc}")
                {
                    ImageKey = IconKeyDataTable,
                    SelectedImageKey = IconKeyDataTable,
                    Tag = MatItemTag.Create(projectId, itemId, grpId, matNo, matDesc)
                };
                gNode.Nodes.Add(itemNode);
            }

            if (matListNode.Nodes.Count == 0)
            {
                matListNode.Nodes.Add(new TreeNode("(no material groups found)")
                {
                    ImageKey = IconKeyDataTable,
                    SelectedImageKey = IconKeyDataTable,
                    Tag = PlaceholderTag.For("EmptyMatGroups")
                });
            }
        }

        private void LoadHardwareForProject(TreeNode hdwListNode, int projectId)
        {
            if (hdwListNode == null) return;

            if (hdwListNode.Nodes.Count == 1 && hdwListNode.Nodes[0].Tag is PlaceholderTag)
                hdwListNode.Nodes.Clear();

            // 1) Get all hardware groups
            var groups = new DataTable();
            using (var conn = GetOpenSqlConnection())
            using (var gcmd = new SqlCommand(
                @"SELECT ID, HdwGroupTag, HdwGroup
                  FROM dbo.Proj_HdwGroup
                  ORDER BY HdwGroupTag, HdwGroup;", conn))
            using (var da = new SqlDataAdapter(gcmd))
            {
                da.Fill(groups);
            }

            var groupNodeById = new Dictionary<int, TreeNode>();
            foreach (DataRow row in groups.Rows)
            {
                int gid = Convert.ToInt32(row["ID"]);
                string tag = Convert.ToString(row["HdwGroupTag"]) ?? "";
                string name = Convert.ToString(row["HdwGroup"]) ?? "";

                var gNode = new TreeNode($"{tag} - {name}")
                {
                    ImageKey = IconKeyFolder,
                    SelectedImageKey = IconKeyFolder,
                    Tag = HdwGroupTag.Create(projectId, gid, tag, name)
                };
                hdwListNode.Nodes.Add(gNode);
                groupNodeById[gid] = gNode;
            }

            // 2) Get all hardware items for this project
            var items = new DataTable();
            using (var conn = GetOpenSqlConnection())
            using (var icmd = new SqlCommand(
                @"SELECT ID, ProjID, HdwNo, HdwDesc, HdwGroupID
                  FROM dbo.Proj_HdwCompile
                  WHERE ProjID = @pid
                  ORDER BY TRY_CAST(HdwNo AS int), HdwNo;", conn))
            {
                icmd.Parameters.AddWithValue("@pid", projectId);
                using (var da = new SqlDataAdapter(icmd))
                    da.Fill(items);
            }

            foreach (DataRow row in items.Rows)
            {
                int grpId = SafeIntOrNull(row, "HdwGroupID") ?? -1;
                if (!groupNodeById.TryGetValue(grpId, out var gNode)) continue;

                int itemId = SafeInt(row, "ID");
                string hdwNo = Convert.ToString(row["HdwNo"]) ?? "";
                string hdwDesc = Convert.ToString(row["HdwDesc"]) ?? "";

                var itemNode = new TreeNode($"{hdwNo} - {hdwDesc}")
                {
                    ImageKey = IconKeyDataTable,
                    SelectedImageKey = IconKeyDataTable,
                    Tag = HdwItemTag.Create(projectId, itemId, grpId, hdwNo, hdwDesc)
                };
                gNode.Nodes.Add(itemNode);
            }

            if (hdwListNode.Nodes.Count == 0)
            {
                hdwListNode.Nodes.Add(new TreeNode("(no hardware groups found)")
                {
                    ImageKey = IconKeyDataTable,
                    SelectedImageKey = IconKeyDataTable,
                    Tag = PlaceholderTag.For("EmptyHdwGroups")
                });
            }
        }

        private void LoadShopOrdersForProject(TreeNode shopListNode, int projectId)
        {
            if (shopListNode == null) return;

            if (shopListNode.Nodes.Count == 1 && shopListNode.Nodes[0].Tag is PlaceholderTag)
                shopListNode.Nodes.Clear();

            using (var conn = GetOpenSqlConnection())
            using (var cmd = new SqlCommand(
                @"SELECT ID, ProjID, SO_Number, SO_Desc, Date_ActualShip
                  FROM dbo.ShopSO_CompiledSO
                  WHERE ProjID = @pid
                  ORDER BY TRY_CAST(SO_Number AS int), SO_Number;", conn))
            {
                cmd.Parameters.AddWithValue("@pid", projectId);

                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        int soId = SafeGet<int>(rdr, "ID");
                        string soNumber = SafeGet<string>(rdr, "SO_Number", "");
                        string soDesc = SafeGet<string>(rdr, "SO_Desc", "");
                        DateTime? shipped = null;

                        try
                        {
                            int i = rdr.GetOrdinal("Date_ActualShip");
                            if (!rdr.IsDBNull(i)) shipped = rdr.GetDateTime(i);
                        }
                        catch { /* ignore */ }

                        string text = $"{soNumber} - {soDesc}";
                        if (shipped.HasValue)
                            text += $" - Shipped: {shipped:MM/dd/yyyy}";

                        var node = new TreeNode(text)
                        {
                            ImageKey = IconKeyDataTable,
                            SelectedImageKey = IconKeyDataTable,
                            Tag = ShopOrderTag.Create(projectId, soId, soNumber, soDesc, shipped)
                        };

                        shopListNode.Nodes.Add(node);
                    }
                }
            }

            if (shopListNode.Nodes.Count == 0)
            {
                shopListNode.Nodes.Add(new TreeNode("(no shop orders found)")
                {
                    ImageKey = IconKeyDataTable,
                    SelectedImageKey = IconKeyDataTable,
                    Tag = PlaceholderTag.For("EmptyShopOrders")
                });
            }
        }

        // --- Dash list & dash section loaders ---

        private void LoadDashesForProject(TreeNode dashListNode, int projectId)
        {
            if (dashListNode == null) return;

            // Remove the placeholder loader if present
            if (dashListNode.Nodes.Count == 1 && dashListNode.Nodes[0].Tag is PlaceholderTag)
                dashListNode.Nodes.Clear();

            using (var conn = GetOpenSqlConnection())
            {
                // 1) Load SERIES dashes (tolerate either Dash_TypeID = 1 or text 'Series')
                using (var seriesCmd = new SqlCommand(
                    @"SELECT ID, Proj_ID, Dash_Num, Dash_Desc
                      FROM dbo.Proj_DashCompile
                      WHERE Proj_ID = @pid
                        AND (Dash_TypeID = 1 OR Dash_Type = 'Series' OR TRY_CAST(Dash_Type AS int) = 1)
                      ORDER BY TRY_CAST(Dash_Num AS int), Dash_Num;", conn))
                {
                    seriesCmd.Parameters.AddWithValue("@pid", projectId);

                    using (var rdr = seriesCmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            int seriesId = SafeGet<int>(rdr, "ID");
                            string seriesNumText = SafeGet<string>(rdr, "Dash_Num", "");   // preserve leading zeros
                            string seriesDesc = SafeGet<string>(rdr, "Dash_Desc", "");

                            var seriesText = $"{seriesNumText} - {seriesDesc}";
                            var seriesNode = new TreeNode(seriesText)
                            {
                                ImageKey = IconKeyDashFolder,
                                SelectedImageKey = IconKeyDashFolder,
                                Tag = DashTag.Create(projectId, seriesId, seriesNumText, seriesDesc)
                            };

                            dashListNode.Nodes.Add(seriesNode);

                            // Add the five dash section folders (Materials/Hardware/PO Items/Shop Orders/Assets)
                            AddDashSectionFolders(seriesNode, projectId, seriesId);
                        }
                    }
                }

                // 2) Load component dashes (Dash_Parent = series.ID)
                foreach (TreeNode seriesNode in dashListNode.Nodes)
                {
                    if (seriesNode.Tag is not DashTag sTag) continue;

                    using (var compCmd = new SqlCommand(
                        @"SELECT ID, Proj_ID, Dash_Num, Dash_Desc
                          FROM dbo.Proj_DashCompile
                          WHERE Proj_ID = @pid
                            AND (TRY_CAST(Dash_Parent AS int) = @parentId OR Dash_Parent = @parentIdStr)
                          ORDER BY TRY_CAST(Dash_Num AS int), Dash_Num;", conn))
                    {
                        compCmd.Parameters.AddWithValue("@pid", projectId);
                        compCmd.Parameters.AddWithValue("@parentId", sTag.DashId);
                        compCmd.Parameters.AddWithValue("@parentIdStr", sTag.DashId.ToString());

                        using (var rdr = compCmd.ExecuteReader())
                        {
                            while (rdr.Read())
                            {
                                int compId = SafeGet<int>(rdr, "ID");
                                string compNumText = SafeGet<string>(rdr, "Dash_Num", "");
                                string compDesc = SafeGet<string>(rdr, "Dash_Desc", "");

                                var compNode = new TreeNode($"{compNumText} - {compDesc}")
                                {
                                    ImageKey = IconKeyFolder,
                                    SelectedImageKey = IconKeyFolder,
                                    Tag = DashTag.Create(projectId, compId, compNumText, compDesc)
                                };

                                seriesNode.Nodes.Add(compNode);
                                AddDashSectionFolders(compNode, projectId, compId);
                            }
                        }
                    }
                }
            }
        }

        private void AddDashSectionFolders(TreeNode dashNode, int projectId, int seriesDashId)
        {
            (string Text, DashSection Section)[] sections = new[]
            {
                ("Materials",   DashSection.Materials),
                ("Hardware",    DashSection.Hardware),
                ("PO Items",    DashSection.POItems),
                ("Shop Orders", DashSection.ShopOrders),
                ("Assets",      DashSection.Assets)
            };

            foreach (var s in sections)
            {
                // Pick icon based on section
                string iconKey = s.Section switch
                {
                    DashSection.Materials => IconKeyDashMatFolder,
                    DashSection.Hardware => IconKeyDashHdwFolder,
                    _ => IconKeyDashFolder
                };

                var folderNode = new TreeNode(s.Text)
                {
                    ImageKey = iconKey,
                    SelectedImageKey = iconKey,
                    Tag = DashSectionTag.Create(projectId, seriesDashId, s.Section),
                    ContextMenuStrip = _dashFolderMenu
                };

                // Placeholders (unchanged behavior)
                if (s.Section == DashSection.Materials)
                    EnsureMaterialsLoaderPlaceholder(folderNode);

                if (s.Section == DashSection.Hardware
                 || s.Section == DashSection.POItems
                 || s.Section == DashSection.ShopOrders)
                    folderNode.Nodes.Add(NewDummyNode());

                dashNode.Nodes.Add(folderNode);
            }
        }


        private void ReloadDashFolder(TreeNode folderNode, DashSectionTag st)
        {
            if (folderNode == null) return;
            folderNode.Nodes.Clear();

            try
            {
                switch (st.Section)
                {
                    case DashSection.Materials:
                        LoadDashMaterials(folderNode, st.ProjectId, st.SeriesDashId);
                        break;

                    case DashSection.Hardware:
                        EnsureDashHardwareLoaded(folderNode, st.ProjectId, st.SeriesDashId);
                        break;

                    case DashSection.POItems:
                        EnsureDashPOItemsLoaded(folderNode, st.ProjectId, st.SeriesDashId);
                        break;

                    case DashSection.ShopOrders:                                        // NEW
                        EnsureDashShopOrdersLoaded(folderNode, st.ProjectId, st.SeriesDashId);
                        break;

                    case DashSection.Assets:
                        folderNode.Nodes.Add(new TreeNode("(Assets loader not implemented yet)")
                        { ImageKey = IconKeyDataTable, SelectedImageKey = IconKeyDataTable });
                        break;
                }
            }
            catch (Exception ex)
            {
                folderNode.Nodes.Add(new TreeNode($"(refresh failed: {ex.Message})")
                { ImageKey = IconKeyDataTable, SelectedImageKey = IconKeyDataTable });
            }

            folderNode.Expand();
        }

        private void LoadDashMaterials(TreeNode materialsNode, int projectId, int dashId)
        {
            if (materialsNode == null) return;

            // Properly remove both our loader placeholder and any lazy dummy
            RemoveMaterialsLoaderPlaceholder(materialsNode);
            RemoveLazyPlaceholders(materialsNode);

            using (var conn = GetOpenSqlConnection())
            using (var cmd = new SqlCommand(@"
                SELECT md.ID, md.DashID, md.MatID, md.MatGroup, m.MatNo, m.MatDesc
                FROM dbo.Proj_MatDash_Compile md
                INNER JOIN dbo.Proj_Mat m ON md.MatID = m.ID
                WHERE md.DashID = @dashId
                ORDER BY md.MatGroup, m.MatNo;", conn))
            {
                cmd.Parameters.AddWithValue("@dashId", dashId);
                using (var rdr = cmd.ExecuteReader())
                {
                    var groups = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

                    while (rdr.Read())
                    {
                        int id = SafeGet<int>(rdr, "ID");
                        int matId = SafeGet<int>(rdr, "MatID");
                        string matGroup = SafeGet<string>(rdr, "MatGroup", "Other");
                        string matNo = SafeGet<string>(rdr, "MatNo", "");
                        string matDesc = SafeGet<string>(rdr, "MatDesc", "");

                        if (!groups.TryGetValue(matGroup, out TreeNode groupNode))
                        {
                            groupNode = new TreeNode(matGroup)
                            {
                                ImageKey = IconKeyDashMatFolder,
                                SelectedImageKey = IconKeyDashMatFolder,
                                Tag = MatDashGroupTag.Create(projectId, dashId, matGroup) // <-- fixed
                            };
                            materialsNode.Nodes.Add(groupNode);
                            groups[matGroup] = groupNode;
                        }

                        var itemNode = new TreeNode($"{matNo} - {matDesc}")
                        {
                            ImageKey = IconKeyDataTable,
                            SelectedImageKey = IconKeyDataTable,
                            Tag = MatDashItemTag.Create(projectId, dashId, id, matGroup, matNo, matDesc) // <-- fixed
                        };
                        groupNode.Nodes.Add(itemNode);
                    }
                }
            }
        }

        #endregion
    }
}
