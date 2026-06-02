using System.Windows.Forms;
using System.Drawing;

namespace IWCCadToolsV9.UI
{
    partial class ctlIWCBlockBrowserV2
    {
        private System.ComponentModel.IContainer components = null;

        private SplitContainer splitMain;       // left: tree, right: assets + preview
        private TreeView treeGroups;

        private TableLayoutPanel rightLayout;   // top: toolbar, middle: assets, right: preview panel
        private FlowLayoutPanel toolbar;
        private Button btnRefresh;
        private Label lblBlockCaption;
        private Label lblSelectedBlock;
        private Button btnOpenInsert;

        private ListView listAssets;

        private Panel previewPanel;
        private PictureBox picturePreview;
        private Label lblAssetCaption;
        private Label lblAssetName;
        private Label lblTypeCaption;
        private Label lblAssetType;

        private System.Windows.Forms.GroupBox grpDetails;
        private System.Windows.Forms.Label lblDetNameCaption;
        private System.Windows.Forms.Label lblDetAddedCaption;
        private System.Windows.Forms.Label lblDetFileCaption;
        private System.Windows.Forms.Label lblDetDescCaption;
        private System.Windows.Forms.Label lblDetNotesCaption;
        private System.Windows.Forms.Label lblDetName;
        private System.Windows.Forms.Label lblDetAdded;
        private System.Windows.Forms.Label lblDetFile;
        private System.Windows.Forms.TextBox txtDetDesc;
        private System.Windows.Forms.TextBox txtDetNotes;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private TableLayoutPanel pnlSearchBar;
        private TextBox          txtSearchInline;
        private Button           btnSearchInline;

        private void InitializeComponent()
        {
            splitMain = new SplitContainer();
            treeGroups = new TreeView();
            rightLayout = new TableLayoutPanel();
            toolbar = new FlowLayoutPanel();
            btnRefresh = new Button();
            lblBlockCaption = new Label();
            lblSelectedBlock = new Label();
            btnOpenInsert = new Button();
            listAssets = new ListView();
            previewPanel = new Panel();
            lblAssetCaption = new Label();
            lblAssetName = new Label();
            lblTypeCaption = new Label();
            lblAssetType = new Label();
            picturePreview = new PictureBox();
            pnlSearchBar    = new TableLayoutPanel();
            txtSearchInline = new TextBox();
            btnSearchInline = new Button();

            ((System.ComponentModel.ISupportInitialize)splitMain).BeginInit();
            splitMain.Panel1.SuspendLayout();
            splitMain.Panel2.SuspendLayout();
            splitMain.SuspendLayout();
            rightLayout.SuspendLayout();
            toolbar.SuspendLayout();
            previewPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)picturePreview).BeginInit();
            SuspendLayout();

            //
            // pnlSearchBar — sits above the tree in Panel1
            //
            pnlSearchBar.Dock        = DockStyle.Top;
            pnlSearchBar.Height      = 36;
            pnlSearchBar.ColumnCount = 2;
            pnlSearchBar.RowCount    = 1;
            pnlSearchBar.Padding     = new Padding(4, 4, 4, 4);
            pnlSearchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pnlSearchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
            pnlSearchBar.Controls.Add(txtSearchInline,  0, 0);
            pnlSearchBar.Controls.Add(btnSearchInline,  1, 0);
            pnlSearchBar.Name = "pnlSearchBar";
            //
            // txtSearchInline
            //
            txtSearchInline.Dock          = DockStyle.Fill;
            txtSearchInline.PlaceholderText = "Search blocks & assets…";
            txtSearchInline.Name          = "txtSearchInline";
            txtSearchInline.TabIndex      = 0;
            //
            // btnSearchInline
            //
            btnSearchInline.Dock     = DockStyle.Fill;
            btnSearchInline.Name     = "btnSearchInline";
            btnSearchInline.Text     = "🔍 Search";
            btnSearchInline.TabIndex = 1;

            //
            // splitMain
            //
            splitMain.Dock = DockStyle.Fill;
            splitMain.Location = new Point(0, 0);
            splitMain.Name = "splitMain";
            //
            // splitMain.Panel1
            //
            splitMain.Panel1.Controls.Add(treeGroups);
            splitMain.Panel1.Controls.Add(pnlSearchBar);  // search bar above tree
            // 
            // splitMain.Panel2
            // 
            splitMain.Panel2.Controls.Add(rightLayout);
            splitMain.Size = new Size(1048, 535);
            splitMain.SplitterDistance = 288;
            splitMain.TabIndex = 0;
            // 
            // treeGroups
            // 
            treeGroups.Dock = DockStyle.Fill;
            treeGroups.HideSelection = false;
            treeGroups.Location = new Point(0, 0);
            treeGroups.Name = "treeGroups";
            treeGroups.Size = new Size(288, 535);
            treeGroups.TabIndex = 0;
            // 
            // rightLayout
            // 
            rightLayout.ColumnCount = 2;
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260F));
            rightLayout.Controls.Add(toolbar, 0, 0);
            rightLayout.Controls.Add(listAssets, 0, 1);
            rightLayout.Controls.Add(previewPanel, 1, 1);
            rightLayout.Dock = DockStyle.Fill;
            rightLayout.Location = new Point(0, 0);
            rightLayout.Name = "rightLayout";
            rightLayout.RowCount = 2;
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            rightLayout.Size = new Size(199, 535);
            rightLayout.TabIndex = 0;
            // 
            // toolbar
            // 
            rightLayout.SetColumnSpan(toolbar, 2);
            toolbar.Controls.Add(btnRefresh);
            toolbar.Controls.Add(lblBlockCaption);
            toolbar.Controls.Add(lblSelectedBlock);
            toolbar.Controls.Add(btnOpenInsert);
            toolbar.Dock = DockStyle.Fill;
            toolbar.Location = new Point(3, 3);
            toolbar.Name = "toolbar";
            toolbar.Padding = new Padding(6);
            toolbar.Size = new Size(193, 38);
            toolbar.TabIndex = 0;
            toolbar.WrapContents = false;
            // 
            // btnRefresh
            // 
            btnRefresh.AutoSize = true;
            btnRefresh.Location = new Point(9, 9);
            btnRefresh.Name = "btnRefresh";
            btnRefresh.Size = new Size(75, 25);
            btnRefresh.TabIndex = 0;
            btnRefresh.Text = "Refresh";
            //
            // lblBlockCaption
            // 
            lblBlockCaption.AutoSize = true;
            lblBlockCaption.Location = new Point(105, 15);
            lblBlockCaption.Margin = new Padding(18, 9, 4, 0);
            lblBlockCaption.Name = "lblBlockCaption";
            lblBlockCaption.Size = new Size(39, 15);
            lblBlockCaption.TabIndex = 1;
            lblBlockCaption.Text = "Block:";
            // 
            // lblSelectedBlock
            // 
            lblSelectedBlock.AutoSize = true;
            lblSelectedBlock.Location = new Point(152, 15);
            lblSelectedBlock.Margin = new Padding(4, 9, 12, 0);
            lblSelectedBlock.Name = "lblSelectedBlock";
            lblSelectedBlock.Size = new Size(107, 15);
            lblSelectedBlock.TabIndex = 2;
            lblSelectedBlock.Text = "(no block selected)";
            // 
            // btnOpenInsert
            // 
            btnOpenInsert.AutoSize = true;
            btnOpenInsert.Location = new Point(274, 9);
            btnOpenInsert.Name = "btnOpenInsert";
            btnOpenInsert.Size = new Size(86, 25);
            btnOpenInsert.TabIndex = 3;
            btnOpenInsert.Text = "Open / Insert";
            // 
            // listAssets
            // 
            listAssets.BackColor = SystemColors.AppWorkspace;
            listAssets.Dock = DockStyle.Fill;
            listAssets.FullRowSelect = true;
            listAssets.Location = new Point(3, 47);
            listAssets.MultiSelect = false;
            listAssets.Name = "listAssets";
            listAssets.Size = new Size(1, 485);
            listAssets.TabIndex = 1;
            listAssets.UseCompatibleStateImageBehavior = false;
            listAssets.View = View.Tile;
            // 
            // previewPanel
            // 
            previewPanel.Controls.Add(lblAssetCaption);
            previewPanel.Controls.Add(lblAssetName);
            previewPanel.Controls.Add(lblTypeCaption);
            previewPanel.Controls.Add(lblAssetType);
            previewPanel.Controls.Add(picturePreview);
            previewPanel.Dock = DockStyle.Fill;
            previewPanel.Location = new Point(-58, 47);
            previewPanel.Name = "previewPanel";
            previewPanel.Padding = new Padding(8);
            previewPanel.Size = new Size(254, 485);
            previewPanel.TabIndex = 2;
            // 
            // lblAssetCaption
            // 
            lblAssetCaption.AutoSize = true;
            lblAssetCaption.Location = new Point(8, 8);
            lblAssetCaption.Name = "lblAssetCaption";
            lblAssetCaption.Size = new Size(38, 15);
            lblAssetCaption.TabIndex = 0;
            lblAssetCaption.Text = "Asset:";
            // 
            // lblAssetName
            // 
            lblAssetName.AutoSize = true;
            lblAssetName.Location = new Point(60, 8);
            lblAssetName.Name = "lblAssetName";
            lblAssetName.Size = new Size(104, 15);
            lblAssetName.TabIndex = 1;
            lblAssetName.Text = "(no asset selected)";
            // 
            // lblTypeCaption
            // 
            lblTypeCaption.AutoSize = true;
            lblTypeCaption.Location = new Point(8, 28);
            lblTypeCaption.Name = "lblTypeCaption";
            lblTypeCaption.Size = new Size(35, 15);
            lblTypeCaption.TabIndex = 2;
            lblTypeCaption.Text = "Type:";
            // 
            // lblAssetType
            // 
            lblAssetType.AutoSize = true;
            lblAssetType.Location = new Point(60, 28);
            lblAssetType.Name = "lblAssetType";
            lblAssetType.Size = new Size(0, 15);
            lblAssetType.TabIndex = 3;
            // 
            // picturePreview
            // 
            picturePreview.BorderStyle = BorderStyle.FixedSingle;
            picturePreview.Location = new Point(8, 52);
            picturePreview.Name = "picturePreview";
            picturePreview.Size = new Size(240, 240);
            picturePreview.SizeMode = PictureBoxSizeMode.Zoom;
            picturePreview.TabIndex = 4;
            picturePreview.TabStop = false;
            //
            //Asset,Block,AssemblyDetail Data
            //
            // Enable scrolling if content grows
            this.previewPanel.AutoScroll = true;

            // grpDetails
            this.grpDetails = new System.Windows.Forms.GroupBox();
            this.grpDetails.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.grpDetails.Location = new System.Drawing.Point(8, 300);   // below preview image
            this.grpDetails.Name = "grpDetails";
            this.grpDetails.Size = new System.Drawing.Size(260, 250);
            this.grpDetails.TabIndex = 50;
            this.grpDetails.TabStop = false;
            this.grpDetails.Text = "Details";

            // captions
            this.lblDetNameCaption = new System.Windows.Forms.Label();
            this.lblDetNameCaption.AutoSize = true;
            this.lblDetNameCaption.Location = new System.Drawing.Point(10, 22);
            this.lblDetNameCaption.Text = "Name:";

            this.lblDetAddedCaption = new System.Windows.Forms.Label();
            this.lblDetAddedCaption.AutoSize = true;
            this.lblDetAddedCaption.Location = new System.Drawing.Point(10, 42);
            this.lblDetAddedCaption.Text = "Added:";

            this.lblDetFileCaption = new System.Windows.Forms.Label();
            this.lblDetFileCaption.AutoSize = true;
            this.lblDetFileCaption.Location = new System.Drawing.Point(10, 62);
            this.lblDetFileCaption.Text = "File:";

            this.lblDetDescCaption = new System.Windows.Forms.Label();
            this.lblDetDescCaption.AutoSize = true;
            this.lblDetDescCaption.Location = new System.Drawing.Point(10, 84);
            this.lblDetDescCaption.Text = "Description:";

            this.lblDetNotesCaption = new System.Windows.Forms.Label();
            this.lblDetNotesCaption.AutoSize = true;
            this.lblDetNotesCaption.Location = new System.Drawing.Point(10, 168);
            this.lblDetNotesCaption.Text = "Notes:";

            // values
            this.lblDetName = new System.Windows.Forms.Label();
            this.lblDetName.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.lblDetName.AutoEllipsis = true;
            this.lblDetName.Location = new System.Drawing.Point(70, 22);
            this.lblDetName.Size = new System.Drawing.Size(180, 15);

            this.lblDetAdded = new System.Windows.Forms.Label();
            this.lblDetAdded.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.lblDetAdded.AutoEllipsis = true;
            this.lblDetAdded.Location = new System.Drawing.Point(70, 42);
            this.lblDetAdded.Size = new System.Drawing.Size(180, 15);

            this.lblDetFile = new System.Windows.Forms.Label();
            this.lblDetFile.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.lblDetFile.AutoEllipsis = true;
            this.lblDetFile.Location = new System.Drawing.Point(70, 62);
            this.lblDetFile.Size = new System.Drawing.Size(180, 15);

            this.txtDetDesc = new System.Windows.Forms.TextBox();
            this.txtDetDesc.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.txtDetDesc.Location = new System.Drawing.Point(13, 100);
            this.txtDetDesc.Multiline = true;
            this.txtDetDesc.ReadOnly = true;
            this.txtDetDesc.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtDetDesc.Size = new System.Drawing.Size(234, 60);

            this.txtDetNotes = new System.Windows.Forms.TextBox();
            this.txtDetNotes.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.txtDetNotes.Location = new System.Drawing.Point(13, 184);
            this.txtDetNotes.Multiline = true;
            this.txtDetNotes.ReadOnly = true;
            this.txtDetNotes.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtDetNotes.Size = new System.Drawing.Size(234, 56);

            // add to grp
            this.grpDetails.Controls.Add(this.lblDetNameCaption);
            this.grpDetails.Controls.Add(this.lblDetAddedCaption);
            this.grpDetails.Controls.Add(this.lblDetFileCaption);
            this.grpDetails.Controls.Add(this.lblDetDescCaption);
            this.grpDetails.Controls.Add(this.lblDetNotesCaption);
            this.grpDetails.Controls.Add(this.lblDetName);
            this.grpDetails.Controls.Add(this.lblDetAdded);
            this.grpDetails.Controls.Add(this.lblDetFile);
            this.grpDetails.Controls.Add(this.txtDetDesc);
            this.grpDetails.Controls.Add(this.txtDetNotes);

            // add grpDetails to previewPanel
            this.previewPanel.Controls.Add(this.grpDetails);

            // 
            // ctlIWCBlockBrowserV2
            // 
            Controls.Add(splitMain);
            Name = "ctlIWCBlockBrowserV2";
            Size = new Size(1048, 535);
            splitMain.Panel1.ResumeLayout(false);
            splitMain.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitMain).EndInit();
            splitMain.ResumeLayout(false);
            rightLayout.ResumeLayout(false);
            toolbar.ResumeLayout(false);
            toolbar.PerformLayout();
            previewPanel.ResumeLayout(false);
            previewPanel.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)picturePreview).EndInit();
            ResumeLayout(false);
        }
    }
}
