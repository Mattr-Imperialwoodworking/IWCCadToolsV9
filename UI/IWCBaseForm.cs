using System;
using System.Windows.Forms;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Base class for all IWC modal forms.
    /// Applies the company icon automatically. Fails gracefully if resources are missing.
    /// </summary>
    public class IWCBaseForm : Form
    {
        public IWCBaseForm()
        {
            try
            {
                this.Icon = Properties.Resources.IWCStamp;
            }
            catch (Exception)
            {
                // Resource not embedded — continue without custom icon
            }
        }
    }
}
