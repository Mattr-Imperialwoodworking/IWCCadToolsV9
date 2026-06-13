namespace IWCCadToolsV9.UI
{
    partial class ctlIWCProjNav
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.SplitContainer split;
        private System.Windows.Forms.TreeView tree;
        private Microsoft.Web.WebView2.WinForms.WebView2 detailBrowser;
        private System.Windows.Forms.ImageList images;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.DocumentActivated -= OnNavDocumentActivated;
                if (_currentNavSvc != null)
                    _currentNavSvc.ProjectLoaded -= OnProjectChanged;
                components?.Dispose();
                images?.Dispose();
                detailBrowser?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            split = new SplitContainer();
            tree = new TreeView();
            images = new ImageList(components);
            detailBrowser = new Microsoft.Web.WebView2.WinForms.WebView2();
            ((System.ComponentModel.ISupportInitialize)split).BeginInit();
            split.Panel1.SuspendLayout();
            split.Panel2.SuspendLayout();
            split.SuspendLayout();
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
            split.Panel2.Controls.Add(detailBrowser);
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
            // detailBrowser
            // 
            detailBrowser.Dock = DockStyle.Fill;
            detailBrowser.Location = new Point(0, 0);
            detailBrowser.Name = "detailBrowser";
            detailBrowser.Size = new Size(806, 650);
            detailBrowser.TabIndex = 0;
            detailBrowser.DefaultBackgroundColor = Color.White;
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
            ResumeLayout(false);
        }
    }
}
