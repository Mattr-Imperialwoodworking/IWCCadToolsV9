using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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

        private static PaletteSet?           _paletteSet;
        private static CtlIWCProj?           _ctlProj;
        private static ctlIWCProjNav?        _ctlProjNav;
        private static ctlIWCBlockBrowserV2? _ctlBlockBrowser;
        private static bool                  _paletteSized;

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

            // Size only on first show — after that, respect what the user set.
            // Must run after Visible = true so the HWND exists.
            if (!_paletteSized)
            {
                SizePaletteToAcadWindow(_paletteSet);
                _paletteSized = true;
            }
        }

        // Size and center the palette to 50% of the AutoCAD main window.
        // Must be called after ps.Visible = true so the palette HWND exists.
        private static void SizePaletteToAcadWindow(PaletteSet ps)
        {
            try
            {
                var acadHandle    = Application.MainWindow.Handle;
                var paletteHandle = ps.Handle;
                if (acadHandle == IntPtr.Zero || paletteHandle == IntPtr.Zero) return;

                if (!NativeMethods.GetWindowRect(acadHandle, out var acadRect)) return;

                int acadW = acadRect.Right  - acadRect.Left;
                int acadH = acadRect.Bottom - acadRect.Top;

                int palW = Math.Max(acadW / 2, 400);
                int palH = Math.Max(acadH / 2, 300);

                // Center over the AutoCAD window.
                int palX = acadRect.Left + (acadW - palW) / 2;
                int palY = acadRect.Top  + (acadH - palH) / 2;

                const uint SWP_NOZORDER    = 0x0004;
                const uint SWP_NOACTIVATE  = 0x0010;
                NativeMethods.SetWindowPos(paletteHandle, IntPtr.Zero,
                    palX, palY, palW, palH,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { /* sizing is best-effort; palette will use AutoCAD defaults on failure */ }
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

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct RECT { public int Left, Top, Right, Bottom; }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                int x, int y, int cx, int cy, uint uFlags);
        }
    }
}
