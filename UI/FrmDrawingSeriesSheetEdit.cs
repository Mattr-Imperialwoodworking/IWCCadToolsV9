using System;
using System.Drawing;
using System.Windows.Forms;
using IWCCadToolsV9.Data.Models;

namespace IWCCadToolsV9.UI
{
    public sealed class FrmDrawingSeriesSheetEdit : IWCBaseForm
    {
        private readonly TextBox _txtSheetNumber;
        private readonly TextBox _txtSubject;
        private readonly CheckBox _chkRenameLayout;

        public string SheetNumber => _txtSheetNumber.Text.Trim();
        public string SheetSubject => _txtSubject.Text.Trim();
        public bool RenameLayoutToSheetNumber => _chkRenameLayout.Checked;

        public FrmDrawingSeriesSheetEdit(DrawingSeriesSheetRecord sheet)
        {
            Text = "Edit Drawing Series Sheet";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 520;
            Height = 250;

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(12)
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            var lblCurrent = new Label
            {
                Text = $"Current layout: {sheet.LayoutName}",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font, FontStyle.Bold)
            };
            main.Controls.Add(lblCurrent, 0, 0);
            main.SetColumnSpan(lblCurrent, 2);

            main.Controls.Add(new Label { Text = "Sheet Number:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 0, 1);
            _txtSheetNumber = new TextBox { Dock = DockStyle.Fill, Text = sheet.SheetNumber };
            main.Controls.Add(_txtSheetNumber, 1, 1);

            main.Controls.Add(new Label { Text = "Subject:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 0, 2);
            _txtSubject = new TextBox { Dock = DockStyle.Fill, Text = sheet.SheetSubject };
            main.Controls.Add(_txtSubject, 1, 2);

            _chkRenameLayout = new CheckBox
            {
                Dock = DockStyle.Top,
                Text = "Rename paper-space layout tab to match Sheet Number",
                Checked = true
            };
            main.Controls.Add(_chkRenameLayout, 1, 3);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            main.Controls.Add(buttons, 0, 4);
            main.SetColumnSpan(buttons, 2);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(main);
        }
    }
}
