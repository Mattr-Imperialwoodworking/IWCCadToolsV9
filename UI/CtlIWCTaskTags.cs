using System;
using System.IO;
using System.Windows.Forms;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Palette tab for reviewing IWC task tags (TODO / NOTE) logged in the CSV file.
    /// </summary>
    public partial class CtlIWCTaskTags : UserControl
    {
        private ListView _listView = null!;
        private Button   _btnRefresh = null!;

        public CtlIWCTaskTags()
        {
            BuildLayout();
        }

        /// <summary>Direct reference to the list view for external population.</summary>
        public ListView TaskListView => _listView;

        // ---------------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------------

        private void BtnRefresh_Click(object? sender,  EventArgs e)
        {
            try
            {
                string? folder  = Path.GetDirectoryName(Application.DocumentManager.MdiActiveDocument?.Name);
                string  csvPath = Path.Combine(folder ?? string.Empty, Helpers.TaskTagHelper.CsvFileName);

                if (!File.Exists(csvPath))
                {
                    MessageBox.Show("CSV not found: " + csvPath);
                    return;
                }

                _listView.Items.Clear();
                foreach (var line in File.ReadAllLines(csvPath))
                {
                    var fields = line.Split(',');
                    if (fields.Length < 7) continue;

                    var item = new ListViewItem(fields[0]);
                    for (int i = 1; i < Math.Min(fields.Length, 9); i++)
                        item.SubItems.Add(fields[i]);

                    _listView.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading task tags: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // Layout
        // ---------------------------------------------------------------------------

        private void BuildLayout()
        {
            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines     = true,
            };
            _listView.Columns.Add("File",        150);
            _listView.Columns.Add("Tag",          70);
            _listView.Columns.Add("Description", 250);
            _listView.Columns.Add("X",            70);
            _listView.Columns.Add("Y",            70);
            _listView.Columns.Add("Z",            70);
            _listView.Columns.Add("Space",        100);
            _listView.Columns.Add("Created",       80);
            _listView.Columns.Add("Due",           80);

            _btnRefresh = new Button
            {
                Text   = "Refresh from CSV",
                Dock   = DockStyle.Top,
                Height = 32,
            };
            _btnRefresh.Click += BtnRefresh_Click;

            Controls.Add(_listView);
            Controls.Add(_btnRefresh);
        }

        // Satisfy partial class requirement if designer files exist
        private void InitializeComponent() { }
    }
}
