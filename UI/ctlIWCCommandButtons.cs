using System;
using System.Windows.Forms;

namespace IWCCadToolsV9.UI
{
    public partial class CtlIWCCommandButtons : UserControl
    {
        public CtlIWCCommandButtons()
        {
            InitializeComponent();
            // Add buttons after the designer initializes the panel
            AddCommandButton("IWCStartup", "Startup Project", IWCStartup_Click);
            AddCommandButton("IWCNetPalette", "Show Project Palette", IWCNetPalette_Click);
            AddCommandButton("IWCMakeAndUploadBlock", "Make new block and upload to library", IWCMakeAndUploadBlock_Click);
        }

        private void AddCommandButton(string commandName, string buttonText, EventHandler handler)
        {
            var btn = new Button();
            btn.Text = buttonText;
            btn.Tag = commandName;
            btn.Width = 180;
            btn.Height = 32;
            btn.Margin = new Padding(6, 4, 6, 4);
            btn.Click += handler;
            // panel is defined in the Designer file, do not re-declare here!
            panel.Controls.Add(btn);
        }

        private void IWCStartup_Click(object? sender,  EventArgs e)
        {
            RunAutoCADCommand("IWCStartup");
        }
        private void IWCNetPalette_Click(object? sender,  EventArgs e)
        {
            RunAutoCADCommand("IWCNetPalette");
        }

        private void IWCMakeAndUploadBlock_Click(object? sender,  EventArgs e)
        {
            RunAutoCADCommand("IWCMakeAndUploadBlock");
        }
        private void RunAutoCADCommand(string commandName)
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.SendStringToExecute($"{commandName} ", true, false, false);
                }
                else
                {
                    MessageBox.Show("No active drawing found.", "IWC CAD Tools");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running command: {ex.Message}", "IWC CAD Tools");
            }
        }
    }
}
