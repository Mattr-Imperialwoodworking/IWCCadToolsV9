namespace IWCCadToolsV9.UI
{
    partial class ctlIWCBlockBrowser
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.SplitContainer split;
        private System.Windows.Forms.TreeView treeGroups;
        private System.Windows.Forms.Panel panelRight;
        private System.Windows.Forms.ListView listBlocks;
        private System.Windows.Forms.ColumnHeader colName;
        private System.Windows.Forms.ColumnHeader colDesc;
        private System.Windows.Forms.FlowLayoutPanel flowButtons;
        private System.Windows.Forms.Button btnRefresh;
        private System.Windows.Forms.Button btnInsert;
        private System.Windows.Forms.Panel detailsPanel;
        private System.Windows.Forms.Label lblBlockName;
        private System.Windows.Forms.TextBox txtBlockName;
        private System.Windows.Forms.Label lblBlockDesc;
        private System.Windows.Forms.TextBox txtBlockDesc;
        private System.Windows.Forms.Label lblBlockNotes;
        private System.Windows.Forms.TextBox txtBlockNotes;
        private System.Windows.Forms.Label lblCreate;
        private System.Windows.Forms.DateTimePicker dtBlockCreate;
        private System.Windows.Forms.Label lblModify;
        private System.Windows.Forms.DateTimePicker dtBlockModify;


        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            split = new SplitContainer();
            treeGroups = new TreeView();
            panelRight = new Panel();
            listBlocks = new ListView();
            colName = new ColumnHeader();
            colDesc = new ColumnHeader();
            flowButtons = new FlowLayoutPanel();
            btnRefresh = new Button();
            btnInsert = new Button();
            detailsPanel = new Panel();
            lblBlockName = new Label();
            txtBlockName = new TextBox();
            lblBlockDesc = new Label();
            txtBlockDesc = new TextBox();
            lblBlockNotes = new Label();
            txtBlockNotes = new TextBox();
            lblCreate = new Label();
            dtBlockCreate = new DateTimePicker();
            lblModify = new Label();
            dtBlockModify = new DateTimePicker();
            ((System.ComponentModel.ISupportInitialize)split).BeginInit();
            split.Panel1.SuspendLayout();
            split.Panel2.SuspendLayout();
            split.SuspendLayout();
            panelRight.SuspendLayout();
            flowButtons.SuspendLayout();
            detailsPanel.SuspendLayout();
            SuspendLayout();
            // 
            // split
            // 
            split.Dock = DockStyle.Fill;
            split.Location = new Point(0, 0);
            split.Name = "split";
            // 
            // split.Panel1
            // 
            split.Panel1.Controls.Add(treeGroups);
            // 
            // split.Panel2
            // 
            split.Panel2.Controls.Add(panelRight);
            split.Size = new Size(800, 500);
            split.SplitterDistance = 250;
            split.TabIndex = 0;
            // 
            // treeGroups
            // 
            treeGroups.Dock = DockStyle.Fill;
            treeGroups.HideSelection = false;
            treeGroups.Location = new Point(0, 0);
            treeGroups.Name = "treeGroups";
            treeGroups.Size = new Size(250, 500);
            treeGroups.TabIndex = 0;
            // 
            // panelRight
            // 
            panelRight.Controls.Add(listBlocks);
            panelRight.Controls.Add(flowButtons);
            panelRight.Controls.Add(detailsPanel);
            panelRight.Dock = DockStyle.Fill;
            panelRight.Location = new Point(0, 0);
            panelRight.Name = "panelRight";
            panelRight.Size = new Size(546, 500);
            panelRight.TabIndex = 0;
            // 
            // listBlocks
            // 
            listBlocks.BackColor = SystemColors.AppWorkspace;
            listBlocks.Columns.AddRange(new ColumnHeader[] { colName, colDesc });
            listBlocks.Dock = DockStyle.Fill;
            listBlocks.FullRowSelect = true;
            listBlocks.Location = new Point(0, 40);
            listBlocks.Name = "listBlocks";
            listBlocks.Size = new Size(546, 260);
            listBlocks.TabIndex = 1;
            listBlocks.UseCompatibleStateImageBehavior = false;
            listBlocks.View = View.Tile;
            // 
            // colName
            // 
            colName.Text = "Name";
            colName.Width = 220;
            // 
            // colDesc
            // 
            colDesc.Text = "Description";
            colDesc.Width = 300;
            // 
            // flowButtons
            // 
            flowButtons.Controls.Add(btnRefresh);
            flowButtons.Controls.Add(btnInsert);
            flowButtons.Dock = DockStyle.Top;
            flowButtons.Location = new Point(0, 0);
            flowButtons.Name = "flowButtons";
            flowButtons.Padding = new Padding(6);
            flowButtons.Size = new Size(546, 40);
            flowButtons.TabIndex = 2;
            // 
            // btnRefresh
            // 
            btnRefresh.AutoSize = true;
            btnRefresh.Location = new Point(12, 12);
            btnRefresh.Margin = new Padding(6);
            btnRefresh.Name = "btnRefresh";
            btnRefresh.Size = new Size(75, 25);
            btnRefresh.TabIndex = 0;
            btnRefresh.Text = "Refresh";
            // 
            // btnInsert
            // 
            btnInsert.AutoSize = true;
            btnInsert.Location = new Point(99, 12);
            btnInsert.Margin = new Padding(6);
            btnInsert.Name = "btnInsert";
            btnInsert.Size = new Size(93, 25);
            btnInsert.TabIndex = 1;
            btnInsert.Text = "Insert Selected";
            // 
            // detailsPanel
            // 
            detailsPanel.Controls.Add(lblBlockName);
            detailsPanel.Controls.Add(txtBlockName);
            detailsPanel.Controls.Add(lblBlockDesc);
            detailsPanel.Controls.Add(txtBlockDesc);
            detailsPanel.Controls.Add(lblBlockNotes);
            detailsPanel.Controls.Add(txtBlockNotes);
            detailsPanel.Controls.Add(lblCreate);
            detailsPanel.Controls.Add(dtBlockCreate);
            detailsPanel.Controls.Add(lblModify);
            detailsPanel.Controls.Add(dtBlockModify);
            detailsPanel.Dock = DockStyle.Bottom;
            detailsPanel.Location = new Point(0, 300);
            detailsPanel.Name = "detailsPanel";
            detailsPanel.Padding = new Padding(8);
            detailsPanel.Size = new Size(546, 200);
            detailsPanel.TabIndex = 3;
            // 
            // lblBlockName
            // 
            lblBlockName.AutoSize = true;
            lblBlockName.Location = new Point(8, 8);
            lblBlockName.Name = "lblBlockName";
            lblBlockName.Size = new Size(74, 15);
            lblBlockName.TabIndex = 0;
            lblBlockName.Text = "Block Name:";
            // 
            // txtBlockName
            // 
            txtBlockName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtBlockName.Location = new Point(100, 5);
            txtBlockName.Name = "txtBlockName";
            txtBlockName.ReadOnly = true;
            txtBlockName.Size = new Size(766, 23);
            txtBlockName.TabIndex = 1;
            // 
            // lblBlockDesc
            // 
            lblBlockDesc.AutoSize = true;
            lblBlockDesc.Location = new Point(8, 35);
            lblBlockDesc.Name = "lblBlockDesc";
            lblBlockDesc.Size = new Size(70, 15);
            lblBlockDesc.TabIndex = 2;
            lblBlockDesc.Text = "Description:";
            // 
            // txtBlockDesc
            // 
            txtBlockDesc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtBlockDesc.Location = new Point(100, 32);
            txtBlockDesc.Multiline = true;
            txtBlockDesc.Name = "txtBlockDesc";
            txtBlockDesc.ReadOnly = true;
            txtBlockDesc.ScrollBars = ScrollBars.Vertical;
            txtBlockDesc.Size = new Size(766, 60);
            txtBlockDesc.TabIndex = 3;
            // 
            // lblBlockNotes
            // 
            lblBlockNotes.AutoSize = true;
            lblBlockNotes.Location = new Point(8, 100);
            lblBlockNotes.Name = "lblBlockNotes";
            lblBlockNotes.Size = new Size(41, 15);
            lblBlockNotes.TabIndex = 4;
            lblBlockNotes.Text = "Notes:";
            // 
            // txtBlockNotes
            // 
            txtBlockNotes.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtBlockNotes.Location = new Point(100, 97);
            txtBlockNotes.Multiline = true;
            txtBlockNotes.Name = "txtBlockNotes";
            txtBlockNotes.ReadOnly = true;
            txtBlockNotes.ScrollBars = ScrollBars.Vertical;
            txtBlockNotes.Size = new Size(766, 60);
            txtBlockNotes.TabIndex = 5;
            // 
            // lblCreate
            // 
            lblCreate.AutoSize = true;
            lblCreate.Location = new Point(8, 165);
            lblCreate.Name = "lblCreate";
            lblCreate.Size = new Size(51, 15);
            lblCreate.TabIndex = 6;
            lblCreate.Text = "Created:";
            // 
            // dtBlockCreate
            // 
            dtBlockCreate.Format = DateTimePickerFormat.Short;
            dtBlockCreate.Location = new Point(100, 162);
            dtBlockCreate.Name = "dtBlockCreate";
            dtBlockCreate.ShowCheckBox = true;
            dtBlockCreate.Size = new Size(120, 23);
            dtBlockCreate.TabIndex = 7;
            // 
            // lblModify
            // 
            lblModify.AutoSize = true;
            lblModify.Location = new Point(240, 165);
            lblModify.Name = "lblModify";
            lblModify.Size = new Size(58, 15);
            lblModify.TabIndex = 8;
            lblModify.Text = "Modified:";
            // 
            // dtBlockModify
            // 
            dtBlockModify.Format = DateTimePickerFormat.Short;
            dtBlockModify.Location = new Point(300, 162);
            dtBlockModify.Name = "dtBlockModify";
            dtBlockModify.ShowCheckBox = true;
            dtBlockModify.Size = new Size(120, 23);
            dtBlockModify.TabIndex = 9;
            // 
            // ctlIWCBlockBrowser
            // 
            Controls.Add(split);
            Name = "ctlIWCBlockBrowser";
            Size = new Size(800, 500);
            split.Panel1.ResumeLayout(false);
            split.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)split).EndInit();
            split.ResumeLayout(false);
            panelRight.ResumeLayout(false);
            flowButtons.ResumeLayout(false);
            flowButtons.PerformLayout();
            detailsPanel.ResumeLayout(false);
            detailsPanel.PerformLayout();
            ResumeLayout(false);
        }
    }
}
