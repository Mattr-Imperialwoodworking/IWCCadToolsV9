using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using IWCCadToolsV9.UI;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// AutoCAD palette commands and general AutoCAD session helpers.
    /// </summary>
    public static class IWCAcadCommands
    {
        // ---------------------------------------------------------------------------
        // Palette state (singleton pattern)
        // ---------------------------------------------------------------------------

        private static PaletteSet?         _paletteSet;
        private static CtlIWCProj?         _ctlProj;
        private static ctlIWCProjNav?      _ctlProjNav;
        private static ctlIWCBlockBrowserV2? _ctlBlockBrowser;

        // ---------------------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------------------

        /// <summary>Shows (or creates) the IWC Project Data palette set.</summary>
        [CommandMethod("IWCNetPalette")]
        public static void ShowIWCNetPalette()
        {
            if (_paletteSet == null)
            {
                _paletteSet = new PaletteSet("IWC Project Data")
                {
                    Style = PaletteSetStyles.ShowAutoHideButton
                          | PaletteSetStyles.ShowCloseButton
                          | PaletteSetStyles.ShowPropertiesMenu,
                };
                try { _paletteSet.Icon = Properties.Resources.IWCStamp; }
                catch { /* icon unavailable — continue */ }

                _ctlProj         = new CtlIWCProj();
                _ctlProjNav      = new ctlIWCProjNav();
                _ctlBlockBrowser = new ctlIWCBlockBrowserV2();

                _paletteSet.Add("IWC Project",          _ctlProj);
                _paletteSet.Add("IWC Project Navigator", _ctlProjNav);
                _paletteSet.Add("IWC Block Browser",     _ctlBlockBrowser);
            }

            _paletteSet.Visible = true;
        }

        /// <summary>Unloads the IWCCadToolsV9 DLL from AutoCAD.</summary>
        [CommandMethod("IWCUnload", CommandFlags.Modal)]
        public static void IwcUnload()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string asmPath = Assembly.GetExecutingAssembly().Location;
            string asmFile = Path.GetFileName(asmPath);

            doc.SendStringToExecute($@"^C^C_.NETUNLOAD ""{asmPath}"" ", true, false, false);
            doc.SendStringToExecute($@"_.NETUNLOAD ""{asmFile}"" ",      true, false, false);
        }

        // ---------------------------------------------------------------------------
        // General session helpers (used by UI controls)
        // ---------------------------------------------------------------------------

        public static Document ThisDrawing()
            => Application.DocumentManager.MdiActiveDocument;

        public static object? GetSystemVariable(string name)
            => Application.GetSystemVariable(name);

        public static void SetSystemVariable(string name, object value)
            => Application.SetSystemVariable(name, value);
    }
}
