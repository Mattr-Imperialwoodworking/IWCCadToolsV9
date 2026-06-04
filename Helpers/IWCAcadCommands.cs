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
                // Allow WinForms text editors inside the palette to retain keyboard focus.
                // Without this, AutoCAD can immediately return focus to the drawing editor,
                // making palette TextBox controls appear non-editable.
                _paletteSet.KeepFocus = true;

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

                // Use the work area of the monitor AutoCAD is on.
                // GetWindowRect on a maximised window returns inflated coords
                // (Windows extends the frame off-screen), so the monitor work
                // area gives the true usable pixel rectangle.
                var screen = System.Windows.Forms.Screen.FromHandle(acadHandle);
                var wa     = screen.WorkingArea;        // excludes taskbar

                int palW = Math.Max(wa.Width  / 2, 400);
                int palH = Math.Max(wa.Height / 2, 300);

                // Center within the monitor work area
                int palX = wa.Left + (wa.Width  - palW) / 2;
                int palY = wa.Top  + (wa.Height - palH) / 2;

                // Defer 200 ms — AutoCAD's palette-frame initialisation runs
                // after Visible=true and would otherwise override our sizing.
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
                // ps.Handle is the palette content area.
                // GA_ROOT (2) walks up to the actual top-level floating frame
                // (the window that has the title bar and can be freely moved).
                var contentHandle = ps.Handle;
                if (contentHandle == IntPtr.Zero) return;

                var frameHandle = NativeMethods.GetAncestor(contentHandle, 2 /* GA_ROOT */);
                if (frameHandle == IntPtr.Zero) frameHandle = contentHandle;

                const uint SWP_NOZORDER     = 0x0004;
                const uint SWP_NOACTIVATE   = 0x0010;
                const uint SWP_FRAMECHANGED = 0x0020;

                NativeMethods.SetWindowPos(frameHandle, IntPtr.Zero,
                    x, y, w, h,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                // Force a full repaint so the palette renders without needing to be moved.
                const uint RDW_INVALIDATE  = 0x0001;
                const uint RDW_UPDATENOW   = 0x0100;
                const uint RDW_ALLCHILDREN = 0x0080;
                NativeMethods.RedrawWindow(frameHandle, IntPtr.Zero, IntPtr.Zero,
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

            // GA_ROOT = 2: returns the root (top-level) window in the parent chain
            [DllImport("user32.dll", ExactSpelling = true)]
            internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        }
    }
}
