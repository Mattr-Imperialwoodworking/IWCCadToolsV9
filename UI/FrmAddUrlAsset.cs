using System;
using System.Windows.Forms;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Modal dialog for adding a URL-based asset to the block library.
    /// </summary>
    public partial class FrmAddUrlAsset : IWCBaseForm
    {
        public string AssetName => txtName.Text.Trim();
        public string AssetDesc => txtDesc.Text.Trim();
        public string AssetUrl  => txtUrl.Text.Trim();

        // ---------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------

        public FrmAddUrlAsset()
        {
            InitializeComponent();
            Text = "Add URL Asset";
        }

        /// <summary>Pre-fills the form fields.</summary>
        public void Prefill(string? assetName = null, string? url = null, string? description = null)
        {
            if (!string.IsNullOrWhiteSpace(assetName))   txtName.Text = assetName;
            if (!string.IsNullOrWhiteSpace(url))         txtUrl.Text  = url;
            if (!string.IsNullOrWhiteSpace(description)) txtDesc.Text = description;
        }

        // ---------------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------------

        private void btnOK_Click(object? sender,  EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(AssetName))
            {
                MessageBox.Show("Asset name is required.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtName.Focus(); return;
            }
            if (!IsValidUrl(AssetUrl))
            {
                MessageBox.Show("Please enter a valid http(s) URL.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtUrl.Focus(); return;
            }
            DialogResult = DialogResult.OK;
        }

        private static bool IsValidUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        // ---------------------------------------------------------------------------
        // Designer-generated members
        // ---------------------------------------------------------------------------

        private void InitializeComponent()
        {
            txtName   = new TextBox();
            txtDesc   = new TextBox();
            txtUrl    = new TextBox();
            var lblName = new Label { Text = "Asset Name:",  AutoSize = true };
            var lblDesc = new Label { Text = "Description:", AutoSize = true };
            var lblUrl  = new Label { Text = "URL:",         AutoSize = true };
            var btnOK  = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80, Height = 28 };
            var btnCan = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80, Height = 28 };
            btnOK.Click += btnOK_Click;

            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
                Padding = new Padding(8)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            tbl.Controls.Add(lblName, 0, 0); tbl.Controls.Add(txtName, 1, 0);
            tbl.Controls.Add(lblDesc, 0, 1); tbl.Controls.Add(txtDesc, 1, 1);
            tbl.Controls.Add(lblUrl,  0, 2); tbl.Controls.Add(txtUrl,  1, 2);

            var btns = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, Padding = new Padding(4)
            };
            btns.Controls.AddRange(new Control[] { btnCan, btnOK });

            Controls.Add(tbl);
            Controls.Add(btns);
            ClientSize    = new System.Drawing.Size(460, 180);
            AcceptButton  = btnOK;
            CancelButton  = btnCan;
            MinimizeBox   = false; MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
        }

        private TextBox txtName = null!;
        private TextBox txtDesc = null!;
        private TextBox txtUrl  = null!;
    }
}
