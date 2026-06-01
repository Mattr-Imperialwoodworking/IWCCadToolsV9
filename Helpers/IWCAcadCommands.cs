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
        // Uses a short deferred timer so AutoCAD has time to finish initialising
        // its palette frame before we override the position — otherwise AutoCAD's
        // own layout pass fires after ours and undoes the resize.
        private static void SizePaletteToAcadWindow(PaletteSet ps)
        {
            try
            {
                var acadHandle = Application.MainWindow.Handle;
                if (acadHandle == IntPtr.Zero) return;

                if (!NativeMethods.GetWindowRect(acadHandle, out var acadRect)) return;

                int acadW = acadRect.Right  - acadRect.Left;
                int acadH = acadRect.Bottom - acadRect.Top;

                // Clamp to the monitor work area so we don't size against the
                // full virtual desktop when AutoCAD is maximised.
                acadW = Math.Min(acadW, System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Width  ?? acadW);
                acadH = Math.Min(acadH, System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea.Height ?? acadH);

                int palW = Math.Max(acadW / 2, 400);
                int palH = Math.Max(acadH / 2, 300);

                // Center over the AutoCAD window
                int palX = acadRect.Left + (acadW - palW) / 2;
                int palY = acadRect.Top  + (acadH - palH) / 2;

                // Defer 200 ms so AutoCAD's palette-frame initialisation completes
                // before we override the position/size.
                var timer = new System.Windows.Forms.Timer { Interval = 200 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    ApplyPaletteSize(ps, palX, palY, palW, palH);
                };
                timer.Start();
            }
            catch { /* sizing is best-effort */ }
        }

        private static void ApplyPaletteSize(PaletteSet ps, int x, int y, int w, int h)
        {
            try
            {
                var paletteHandle = ps.Handle;
                if (paletteHandle == IntPtr.Zero) return;

                const uint SWP_NOZORDER     = 0x0004;
                const uint SWP_NOACTIVATE   = 0x0010;
                const uint SWP_FRAMECHANGED = 0x0020;   // forces frame recalc + repaint

                NativeMethods.SetWindowPos(paletteHandle, IntPtr.Zero,
                    x, y, w, h,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                // Force a full repaint so the palette renders correctly without
                // requiring the user to move it first.
                const uint RDW_INVALIDATE   = 0x0001;
                const uint RDW_UPDATENOW    = 0x0100;
                const uint RDW_ALLCHILDREN  = 0x0080;
                NativeMethods.RedrawWindow(paletteHandle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            }
            catch { /* best-effort */ }
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

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate,
                IntPtr hrgnUpdate, uint flags);
        }
    }
}
