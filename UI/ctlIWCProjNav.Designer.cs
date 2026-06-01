namespace IWCCadToolsV9.UI
{
    partial class ctlIWCProjNav
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.SplitContainer split;
        private System.Windows.Forms.TreeView tree;
        private System.Windows.Forms.DataGridView dataGrid;
        private System.Windows.Forms.ImageList images;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CtlIWCProj.ProjectChanged -= OnProjectChanged;
                components?.Dispose();
                images?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            split = new SplitContainer();
            tree = new TreeView();
            images = new ImageList(components);
            dataGrid = new DataGridView();
            ((System.ComponentModel.ISupportInitialize)split).BeginInit();
            split.Panel1.SuspendLayout();
            split.Panel2.SuspendLayout();
            split.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)dataGrid).BeginInit();
            SuspendLayout();
            // 
            // split
            // 
            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel1;
            split.Location = new Point(0, 0);
            split.Name = "split";
            // 
            // split.Panel1
            // 
            split.Panel1.Controls.Add(tree);
            // 
            // split.Panel2
            // 
            split.Panel2.Controls.Add(dataGrid);
            split.Size = new Size(1100, 650);
            split.SplitterDistance = 290;
            split.TabIndex = 0;
            // 
            // tree
            // 
            tree.Dock = DockStyle.Fill;
            tree.FullRowSelect = true;
            tree.HideSelection = false;
            tree.ImageIndex = 0;
            tree.ImageList = images;
            tree.ItemHeight = 20;
            tree.Location = new Point(0, 0);
            tree.Name = "tree";
            tree.SelectedImageIndex = 0;
            tree.Size = new Size(290, 650);
            tree.TabIndex = 0;
            // 
            // images
            // 
            images.ColorDepth = ColorDepth.Depth32Bit;
            images.ImageSize = new Size(20, 20);
            images.TransparentColor = Color.Transparent;
            // 
            // dataGrid
            // 
            dataGrid.AllowUserToAddRows = false;
            dataGrid.AllowUserToDeleteRows = false;
            dataGrid.AllowUserToOrderColumns = true;
            dataGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            dataGrid.Dock = DockStyle.Fill;
            dataGrid.Location = new Point(0, 0);
            dataGrid.MultiSelect = false;
            dataGrid.Name = "dataGrid";
            dataGrid.ReadOnly = true;
            dataGrid.RowHeadersVisible = false;
            dataGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dataGrid.Size = new Size(806, 650);
            dataGrid.TabIndex = 0;
            // 
            // ctlIWCProjNav
            // 
            AutoScaleMode = AutoScaleMode.None;
            Controls.Add(split);
            Name = "ctlIWCProjNav";
            Size = new Size(1100, 650);
            split.Panel1.ResumeLayout(false);
            split.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)split).EndInit();
            split.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)dataGrid).EndInit();
            ResumeLayout(false);
        }
    }
}
