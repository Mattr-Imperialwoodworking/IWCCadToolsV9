using System;
using System.Data;
using System.Windows.Forms;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using Microsoft.Data.SqlClient;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Modal dialog for selecting the active IWC project from a list box.
    /// The chosen project ID is exposed via <see cref="SelectedProjectId"/>.
    /// </summary>
    public partial class ProjSelect : IWCBaseForm
    {
        /// <summary>The project ID selected by the user, or null if cancelled.</summary>
        public int? SelectedProjectId { get; private set; }

        // ---------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------

        public ProjSelect()
        {
            InitializeComponent();
            StartPosition = FormStartPosition.CenterScreen;
            Load         += ProjSelect_Load;
        }

        // ---------------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------------

        private void ProjSelect_Load(object? sender,  EventArgs e)
        {
            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                using var da = new SqlDataAdapter(
                    "SELECT ID, IDNum + ' - ' + Proj_Name AS Proj " +
                    "FROM dbo.Proj_CompileActive ORDER BY Proj ASC", conn.OpenConn);

                var dt = new DataTable();
                da.Fill(dt);

                ListBox1.DataSource   = dt;
                ListBox1.DisplayMember = "Proj";
                ListBox1.ValueMember  = "ID";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load projects:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Button1_Click(object? sender,  EventArgs e)    => AcceptSelection();
        private void ListBox1_DoubleClick(object? sender,  EventArgs e) => AcceptSelection();

        private void AcceptSelection()
        {
            if (ListBox1.SelectedValue != null
                && int.TryParse(ListBox1.SelectedValue.ToString(), out int id) && id > 0)
            {
                SelectedProjectId = id;
                IWCAcadCommands.SetSystemVariable("USERI1", id);
            }
            Hide();
        }

        // ---------------------------------------------------------------------------
        // Designer-generated members (minimal – no separate .Designer.cs)
        // ---------------------------------------------------------------------------

        private void InitializeComponent()
        {
            ListBox1 = new ListBox();
            Button1  = new Button();
            SuspendLayout();

            ListBox1.Dock        = DockStyle.Fill;
            ListBox1.IntegralHeight = false;
            ListBox1.DoubleClick += ListBox1_DoubleClick;

            Button1.Dock        = DockStyle.Bottom;
            Button1.Text        = "Select Project";
            Button1.Height      = 36;
            Button1.Click      += Button1_Click;

            ClientSize = new System.Drawing.Size(460, 360);
            Controls.Add(ListBox1);
            Controls.Add(Button1);
            Text = "Select Project";
            ResumeLayout(false);
        }

        private ListBox ListBox1 = null!;
        private Button  Button1  = null!;
    }
}
