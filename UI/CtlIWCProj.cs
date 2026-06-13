using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Printing;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Core;
using IWCCadToolsV9.Commands;
using IWCCadToolsV9.Data.Models;
using IWCCadToolsV9.Helpers;
using Microsoft.Data.SqlClient;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.UI
{
    /// <summary>
    /// Palette tab showing current project information and DWG file properties.
    ///
    /// Updated to bind against ProjectContextService instead of re-querying
    /// USERI1 / the database directly. Subscribes to ProjectContextService.ProjectLoaded
    /// instead of the legacy static CtlIWCProj.ProjectChanged event.
    /// </summary>
    public partial class CtlIWCProj : UserControl
    {
        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        public CtlIWCProj()
        {
            // AutoCAD palettes can be aggressive about returning focus to the
            // drawing editor.  Make the user control itself selectable so its
            // child TextBox/DataGridView editors can keep focus while typing.
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;

            InitializeComponent();
            ConfigureLargeNoteTextBoxes();
            tabControl.Dock = DockStyle.Fill;
            Dock            = DockStyle.Fill;

            // Subscribe to document activation so the palette updates
            // when the user switches between open drawings
            Application.DocumentManager.DocumentActivated += OnDocumentActivated;

            DrawingSeriesService.DrawingSeriesDataChanged -= OnDrawingSeriesDataChanged;
            DrawingSeriesService.DrawingSeriesDataChanged += OnDrawingSeriesDataChanged;

            RefreshFromContext();
        }

        private void OnDrawingSeriesDataChanged(object? sender, EventArgs e)
        {
            if (IsDisposed) return;

            // Debounce: if multiple DataChanged events arrive in rapid succession
            // (e.g. the two RaiseDrawingSeriesDataChanged calls inside
            // ReviewProjectDashChange), collapse them into a single UI refresh.
            // BeginInvoke already queues on the UI thread; the flag ensures only
            // one queued refresh is ever pending at a time.
            if (_seriesChangeDebounced) return;
            _seriesChangeDebounced = true;

            void RefreshDrawingSeriesView()
            {
                _seriesChangeDebounced = false;
                LoadDrawingSeries(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);
            }

            if (InvokeRequired)
                BeginInvoke((MethodInvoker)RefreshDrawingSeriesView);
            else
                RefreshDrawingSeriesView();
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        public void Reload() => RefreshFromContext();

        // -----------------------------------------------------------------------
        // Context binding
        // -----------------------------------------------------------------------

        private ProjectContextService? _currentSvc;
        private bool _loadingFileProps;
        private bool _filePropsDirty;
        private bool _loadingBomMetal;

        // Guards against concurrent or back-to-back LoadDrawingSeries calls that
        // would append duplicate rows.  _seriesLoadToken is incremented each time
        // a new load starts; the async continuation checks its captured token and
        // discards results if a newer load has since been initiated.
        private int  _seriesLoadToken;
        private bool _seriesChangeDebounced;

        // Tracks which document path was most recently bound by OnDocumentActivated
        // *with already-loaded data*.  If OnProjectLoaded fires for that same path
        // immediately after, the data was already present at activation time so the
        // bind is redundant — suppress it to avoid a duplicate series load.
        //
        // When a file is newly opened, svc.HasProject is false at activation time
        // (SQL load is still in progress), so the guard is NOT set and ProjectLoaded
        // is allowed through to perform the real bind once the data is ready.
        private string _lastActivatedDocPath = string.Empty;

        private void SubscribeToContext(ProjectContextService svc)
        {
            // Unsubscribe from previous document's service
            if (_currentSvc != null)
                _currentSvc.ProjectLoaded -= OnProjectLoaded;

            _currentSvc = svc;
            _currentSvc.ProjectLoaded += OnProjectLoaded;
        }

        private void OnDocumentActivated(object? sender,
            Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
        {
            if (e.Document == null) return;
            var svc = ProjectContextService.GetOrCreate(e.Document);

            // Only set the guard when data is already in memory.  If the service has
            // no project yet the SQL load is still running; ProjectLoaded must fire
            // to populate the palette once data arrives.
            _lastActivatedDocPath = svc.HasProject
                ? (e.Document.Name ?? string.Empty)
                : string.Empty;

            SubscribeToContext(svc);
            BindToService(svc);
        }

        private void OnProjectLoaded(object? sender, EventArgs e)
        {
            // Suppress only when OnDocumentActivated already bound this same document
            // with complete data (guard path is set and matches current document).
            string currentPath = _currentSvc?.Document?.Name ?? string.Empty;
            if (!string.IsNullOrEmpty(_lastActivatedDocPath) &&
                string.Equals(currentPath, _lastActivatedDocPath, StringComparison.OrdinalIgnoreCase))
            {
                // Data was already present at activation — this ProjectLoaded is redundant.
                // Clear the guard so the next genuine event (dash change, etc.) is not suppressed.
                _lastActivatedDocPath = string.Empty;
                return;
            }

            // Fresh file open (data just finished loading) or post-activation event
            // (dash changed, project reassigned) — proceed with binding.
            if (InvokeRequired)
                Invoke(new Action(() => BindToService(_currentSvc)));
            else
                BindToService(_currentSvc);
        }

        private void RefreshFromContext()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var svc = ProjectContextService.GetOrCreate(doc);
            SubscribeToContext(svc);
            BindToService(svc);
        }

        // -----------------------------------------------------------------------
        // UI binding — no database calls here, all data comes from the service
        // -----------------------------------------------------------------------

        private void BindToService(ProjectContextService? svc)
        {
            if (svc == null)
            {
                ClearProjectFields();
                return;
            }

            var proj = svc.Project;
            var dash = svc.Dash;

            // Tab 1 — Current Project (from typed ProjectRecord)
            if (proj != null)
            {
                txtProjNum.Text  = proj.IdNum;
                txtProjName.Text = proj.Name;
                txtArch.Text     = proj.Architect;
                txtCont.Text     = proj.Contractor;
                txtPM.Text       = proj.PMIni;
                LoadProjectDashEditData(proj.Id, dash?.DashId ?? 0);

                // Auto-sync project data to DWG custom file properties every time
                // the project context is updated — keeps the file self-describing
                // without requiring a manual "Sync Title Block" click.
                try { svc.PersistToDwg(); } catch { /* non-fatal — file may be read-only */ }
            }
            else
            {
                ClearProjectFields();
            }

            // Offline indicator
            lblOffline.Visible = svc.IsOffline;
            lblOffline.Text    = svc.IsOffline ? "⚠ Offline — showing cached data" : string.Empty;

            // Tab 2 — Current BOM — current active dash quick reference.
            LoadCurrentBom(proj?.Id ?? 0, dash?.DashId ?? 0);

            // Tab 3 — Drawing Series — dash/file/sheet association view.
            LoadDrawingSeries(proj?.Id ?? 0, dash?.DashId ?? 0);

            // Tab 4 — DWG File Properties — always re-read from the DWG after
            // PersistToDwg() so the File Properties tab reflects what was written.
            LoadFileProps();
        }

        private void LoadFileProps()
        {
            _loadingFileProps = true;

            try
            {
                // ── Custom Properties grid ────────────────────────────────────
                dgvCustomProps.Rows.Clear();
                var all = Helpers.AcadFilePropHelper.GetAllCustomProperties();
                foreach (var kv in all)
                    dgvCustomProps.Rows.Add(kv.Key, kv.Value);

                // ── Summary tab ───────────────────────────────────────────────
                var summ = Helpers.AcadFilePropHelper.GetSummaryProps();
                if (summ != null)
                {
                    txtSummTitle.Text     = summ.Title;
                    txtSummSubject.Text   = summ.Subject;
                    txtSummAuthor.Text    = summ.Author;
                    txtSummKeywords.Text  = summ.Keywords;
                    txtSummHyperlink.Text = summ.HyperlinkBase;
                    txtSummRevision.Text  = summ.RevisionNumber;
                    txtSummComments.Text  = summ.Comments;
                }

                // ── File Info tab ─────────────────────────────────────────────
                var info = Helpers.AcadFilePropHelper.GetFileInfoProps();
                if (info != null)
                {
                    txtInfoFile.Text      = info.FileName     ?? string.Empty;
                    txtInfoLocation.Text  = info.Location     ?? string.Empty;
                    txtInfoSize.Text      = info.SizeBytes > 0
                        ? $"{info.SizeBytes / 1024.0 / 1024.0:F2} MB ({info.SizeBytes:N0} bytes)"
                        : string.Empty;
                    txtInfoCreated.Text   = info.Created?.ToString("g")  ?? string.Empty;
                    txtInfoModified.Text  = info.Modified?.ToString("g") ?? string.Empty;
                    txtInfoAccessed.Text  = info.Accessed?.ToString("g") ?? string.Empty;
                    txtInfoLastSaved.Text = info.LastSavedBy ?? string.Empty;
                }
            }
            finally
            {
                _loadingFileProps = false;
                SetFilePropsDirty(false);
            }
        }

        private static TextBox AddFilePropRow(TableLayoutPanel tbl, int row, string label, bool readOnly = true)
        {
            tbl.Controls.Add(new Label
            {
                Text = label, AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 4, 0)
            }, 0, row);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = readOnly,
                Enabled = true,
                TabStop = !readOnly,
                BackColor = System.Drawing.SystemColors.Window
            };
            if (!readOnly)
            {
                tb.Cursor = Cursors.IBeam;
                tb.MouseDown += EditableFilePropControl_MouseDown;
                tb.Enter += EditableFilePropControl_Enter;
            }
            tbl.Controls.Add(tb, 1, row);
            return tb;
        }

        private static void EditableFilePropControl_MouseDown(object? sender, MouseEventArgs e)
        {
            if (sender is Control ctl && ctl.CanFocus && !ctl.Focused)
                ctl.Focus();
        }

        private static void EditableFilePropControl_Enter(object? sender, EventArgs e)
        {
            if (sender is TextBox tb && !tb.ReadOnly)
                tb.SelectionStart = tb.TextLength;
        }

        private void MarkFilePropsDirty(object? sender, EventArgs e)
        {
            if (!_loadingFileProps)
                SetFilePropsDirty(true);
        }

        private void SetFilePropsDirty(bool dirty)
        {
            _filePropsDirty = dirty;
            if (btnSaveFileProps != null)
            {
                btnSaveFileProps.Visible = dirty;
                btnSaveFileProps.Enabled = dirty;
            }
        }

        private void SaveFileProps()
        {
            if (!_filePropsDirty) return;

            try
            {
                var customProps = new System.Collections.Generic.Dictionary<string, string>(
                    System.StringComparer.OrdinalIgnoreCase);

                foreach (DataGridViewRow row in dgvCustomProps.Rows)
                {
                    if (row.IsNewRow) continue;

                    var key = row.Cells["PropName"].Value?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    customProps[key] = row.Cells["PropValue"].Value?.ToString() ?? string.Empty;
                }

                Helpers.AcadFilePropHelper.SetCustomProperties(customProps);

                Helpers.AcadFilePropHelper.SetSummaryProps(new Helpers.AcadFilePropHelper.SummaryProps
                {
                    Title          = txtSummTitle.Text,
                    Subject        = txtSummSubject.Text,
                    Author         = txtSummAuthor.Text,
                    Keywords       = txtSummKeywords.Text,
                    HyperlinkBase  = txtSummHyperlink.Text,
                    RevisionNumber = txtSummRevision.Text,
                    Comments       = txtSummComments.Text
                });

                LoadFileProps();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    $"Unable to save drawing file properties.\n\n{ex.Message}",
                    "IWC File Properties",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void LoadCurrentBom(int projectId, int dashId)
        {
            if (dgvBomComponents == null || dgvBomMaterials == null || dgvBomHardware == null || dgvBomMetal == null)
                return;

            dgvBomComponents.Rows.Clear();
            dgvBomMaterials.Rows.Clear();
            dgvBomHardware.Rows.Clear();
            dgvBomMetal.Rows.Clear();

            bool hasContext = projectId > 0 && dashId > 0;
            if (lblBomContext != null)
            {
                var proj = _currentSvc?.Project;
                var dash = _currentSvc?.Dash;
                lblBomContext.Text = hasContext && proj != null && dash != null
                    ? $"{proj.IdNum}-{dash.DashNum} {proj.Name}, {dash.DashDesc}"
                    : "No active dash selected for this drawing.";
            }
            if (btnBomAddMaterial != null) btnBomAddMaterial.Enabled = hasContext;
            if (btnBomAddHardware != null) btnBomAddHardware.Enabled = hasContext;
            if (btnBomExportPdf  != null) btnBomExportPdf.Enabled  = hasContext;
            if (btnBomExportCsv  != null) btnBomExportCsv.Enabled  = hasContext;

            if (!hasContext) return;

            try
            {
                using var conn = IWCConn.GetSqlConnection();
                conn.Open();

                using (var cmd = new SqlCommand(@"
                    SELECT Dash_Num, Dash_Desc, MfrName,
                           Date_TargetRLSMfr, Date_ActualRlsMfr,
                           Date_TargetShip, Date_ActualShip
                    FROM dbo.Proj_DashCompile
                    WHERE Proj_ID = @projId
                      AND Dash_Parent = @dashId
                      AND (Act_Void = 0 OR Act_Void IS NULL)
                    ORDER BY TRY_CAST(Dash_Num AS int), Dash_Num;", conn))
                {
                    cmd.Parameters.AddWithValue("@projId", projectId);
                    cmd.Parameters.AddWithValue("@dashId", dashId);
                    using var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        dgvBomComponents.Rows.Add(
                            SafeDbString(rdr, "Dash_Num"),
                            SafeDbString(rdr, "Dash_Desc"),
                            SafeDbString(rdr, "MfrName"),
                            SafeDbDate(rdr, "Date_TargetRLSMfr"),
                            SafeDbDate(rdr, "Date_ActualRlsMfr"),
                            SafeDbDate(rdr, "Date_TargetShip"),
                            SafeDbDate(rdr, "Date_ActualShip"));
                    }
                }

                using (var cmd = new SqlCommand(@"
                    SELECT MatID, MatNo, MatDesc, MatGroup, MatUnits, MatQty, MatNotes, MatApprove
                    FROM dbo.Proj_MatDash_Compile
                    WHERE DashID = @dashId
                    ORDER BY MatGroup, MatNo;", conn))
                {
                    cmd.Parameters.AddWithValue("@dashId", dashId);
                    using var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        int rowIndex = dgvBomMaterials.Rows.Add(
                            SafeDbString(rdr, "MatNo"),
                            SafeDbString(rdr, "MatGroup"),
                            SafeDbString(rdr, "MatDesc"),
                            SafeDbString(rdr, "MatUnits"),
                            SafeDbLongString(rdr, "MatQty"),
                            SafeDbString(rdr, "MatNotes"),
                            SafeDbDate(rdr, "MatApprove"));
                        dgvBomMaterials.Rows[rowIndex].Tag = SafeDbInt(rdr, "MatID");
                    }
                }

                using (var cmd = new SqlCommand(@"
                    SELECT ID, HdwNo, HdwDesc, HdwGroupNo, HdwQty, HdwUnit, HdwNotes, HdwApprove
                    FROM dbo.Proj_HdwDash_Compile
                    WHERE DashID = @dashId
                    ORDER BY HdwGroupID, HdwGroupNo, HdwNo;", conn))
                {
                    cmd.Parameters.AddWithValue("@dashId", dashId);
                    using var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        int rowIndex = dgvBomHardware.Rows.Add(
                            SafeDbString(rdr, "HdwNo"),
                            SafeDbString(rdr, "HdwGroupNo"),
                            SafeDbString(rdr, "HdwDesc"),
                            SafeDbLongString(rdr, "HdwQty"),
                            SafeDbString(rdr, "HdwUnit"),
                            SafeDbString(rdr, "HdwNotes"),
                            SafeDbDate(rdr, "HdwApprove"));
                        dgvBomHardware.Rows[rowIndex].Tag = SafeDbInt(rdr, "ID");
                    }
                }

                LoadMetalMaterialLists(conn, projectId);
                _loadingBomMetal = true;
                try
                {
                    using var cmd = new SqlCommand(@"
                        SELECT ID, Mtl_PrtNo, Mtl_PrtDesc, MatID, Mtl_Finish, Mtl_Material,
                               Mtl_Length, Mtl_Width, Mtl_Height, Mtl_Thk, Mtl_Qty, Mtl_QtyUnits,
                               Mtl_Volume, Mtl_Weight, Mtl_Notes, Mtl_ShtReference,
                               Date_ActualRls, Date_ActualShip
                        FROM dbo.Proj_Mtl
                        WHERE DashID = @dashId
                        ORDER BY Mtl_PrtNo, ID;", conn);
                    cmd.Parameters.AddWithValue("@dashId", dashId);
                    using var rdr = cmd.ExecuteReader();
                    while (rdr.Read())
                    {
                        int rowIndex = dgvBomMetal.Rows.Add(
                            SafeDbDecimalString(rdr, "Mtl_PrtNo"),
                            SafeDbString(rdr, "Mtl_PrtDesc"),
                            SafeDbNullableInt(rdr, "MatID"),
                            SafeDbString(rdr, "Mtl_Finish"),
                            SafeDbString(rdr, "Mtl_Material"),
                            SafeDbDecimalString(rdr, "Mtl_Length"),
                            SafeDbDecimalString(rdr, "Mtl_Width"),
                            SafeDbDecimalString(rdr, "Mtl_Height"),
                            SafeDbDecimalString(rdr, "Mtl_Thk"),
                            SafeDbIntString(rdr, "Mtl_Qty"),
                            SafeDbString(rdr, "Mtl_QtyUnits"),
                            SafeDbDecimalString(rdr, "Mtl_Volume"),
                            SafeDbDecimalString(rdr, "Mtl_Weight"),
                            SafeDbString(rdr, "Mtl_Notes"),
                            SafeDbString(rdr, "Mtl_ShtReference"),
                            SafeDbDate(rdr, "Date_ActualRls"),
                            SafeDbDate(rdr, "Date_ActualShip"));
                        dgvBomMetal.Rows[rowIndex].Tag = SafeDbInt(rdr, "ID");
                    }
                }
                finally
                {
                    _loadingBomMetal = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to load Current BOM.\n\n{ex.Message}",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnBomRefresh_Click(object? sender, EventArgs e)
        {
            LoadCurrentBom(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);
            RefreshExistingBomTablesInDrawing();
        }

        private static void RefreshExistingBomTablesInDrawing()
        {
            try
            {
                // Updates tagged IWC tables that already exist in the current drawing:
                // hardware, materials, and metal parts. Multiple copies and tables
                // inside block definitions are supported.
                TableCommands.AutoUpdateExistingTablesInActiveDocument(quiet: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Current BOM lists were refreshed, but existing AutoCAD tables could not be updated.\n\n{ex.Message}",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnBomExportPdf_Click(object? sender, EventArgs e)
            => ExportCurrentBomPdf();

        private void btnBomExportCsv_Click(object? sender, EventArgs e)
            => ExportCurrentBomCsv();

        private void btnBomInsertHdwTable_Click(object? sender, EventArgs e)
            => RunAcadCommandFromPalette("IWCInsertHardwareTable");

        private void btnBomInsertMatTable_Click(object? sender, EventArgs e)
            => RunAcadCommandFromPalette("IWCInsertMaterialTable");

        private void btnBomInsertMetalTable_Click(object? sender, EventArgs e)
            => RunAcadCommandFromPalette("IWCInsertMetalTable");

        private void btnBomInsertCompList_Click(object? sender, EventArgs e)
            => RunAcadCommandFromPalette("IWC_CompList");

        private static void RunAcadCommandFromPalette(string commandName)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.SendStringToExecute($"{commandName} ", true, false, false);
        }


        private async void LoadDrawingSeries(int projectId, int dashId)
        {
            if (lblDrawingSeriesContext == null || dgvDrawingSeriesFiles == null || tvDrawingSeriesSheets == null)
                return;

            // Increment the token before clearing the UI.  Any in-flight load that
            // was started before this call captured an older token value and will
            // discard its results when it resumes after the await, preventing it
            // from appending a second set of rows into the now-reloading controls.
            int myToken = ++_seriesLoadToken;

            dgvDrawingSeriesFiles.Rows.Clear();
            tvDrawingSeriesSheets.Nodes.Clear();

            bool hasContext = projectId > 0 && dashId > 0;
            if (btnDrawingSeriesAssociateCurrent != null) btnDrawingSeriesAssociateCurrent.Enabled = hasContext;
            if (btnDrawingSeriesRefresh != null) btnDrawingSeriesRefresh.Enabled = hasContext;
            if (btnDrawingSeriesRefreshDatabase != null) btnDrawingSeriesRefreshDatabase.Enabled = hasContext;

            var proj = _currentSvc?.Project;
            var dash = _currentSvc?.Dash;
            lblDrawingSeriesContext.Text = hasContext && proj != null && dash != null
                ? $"{proj.IdNum}-{dash.DashNum} {proj.Name}, {dash.DashDesc}"
                : "No active dash selected for this drawing.";

            if (!hasContext) return;

            try
            {
                var repo = new DrawingSeriesRepository();
                var nodes = await repo.GetDashSheetTreeAsync(projectId, dashId);

                // A newer load was started while we were awaiting the DB query.
                // Our results are stale — discard them to avoid appending duplicates.
                if (myToken != _seriesLoadToken) return;

                foreach (var dashNodeData in nodes)
                {
                    var dashNode = new TreeNode(dashNodeData.IsCurrentDash
                        ? $"{dashNodeData.DisplayText}  (current)"
                        : dashNodeData.DisplayText)
                    {
                        Tag = dashNodeData
                    };

                    foreach (var file in dashNodeData.Files)
                    {
                        int rowIndex = dgvDrawingSeriesFiles.Rows.Add(
                            dashNodeData.DashNum,
                            dashNodeData.DashDesc,
                            file.FileName,
                            file.SavedPath,
                            file.Sheets.Count.ToString("N0"));
                        dgvDrawingSeriesFiles.Rows[rowIndex].Tag = file;

                        var fileNode = new TreeNode($"{file.FileName}  ({file.Sheets.Count:N0} sheets)") { Tag = file };
                        foreach (var sheet in file.Sheets)
                        {
                            fileNode.Nodes.Add(new TreeNode(FormatDrawingSeriesSheetNodeText(sheet)) { Tag = sheet });
                        }
                        dashNode.Nodes.Add(fileNode);
                    }

                    tvDrawingSeriesSheets.Nodes.Add(dashNode);
                    dashNode.Expand();
                }
            }
            catch (System.Exception ex)
            {
                // Only show the error if we are still the active load.
                if (myToken == _seriesLoadToken)
                    MessageBox.Show($"Unable to load Drawing Series data.\n\n{ex.Message}",
                        "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnDrawingSeriesRefresh_Click(object? sender, EventArgs e)
            => LoadDrawingSeries(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);

        private static string FormatDrawingSeriesSheetNodeText(DrawingSeriesSheetRecord sheet)
        {
            string number = string.IsNullOrWhiteSpace(sheet.SheetNumber)
                ? sheet.LayoutName
                : sheet.SheetNumber;

            return string.IsNullOrWhiteSpace(sheet.SheetSubject)
                ? $"{number} -"
                : $"{number} - {sheet.SheetSubject}";
        }

        private void UpdateDrawingSeriesSheetNode(TreeNode sheetNode, DrawingSeriesSheetRecord updatedSheet)
        {
            sheetNode.Tag = updatedSheet;
            sheetNode.Text = FormatDrawingSeriesSheetNodeText(updatedSheet);

            if (sheetNode.Parent?.Tag is DrawingSeriesFileRecord file)
            {
                int index = file.Sheets.FindIndex(s => s.SheetId == updatedSheet.SheetId);
                if (index >= 0)
                    file.Sheets[index] = updatedSheet;

                sheetNode.Parent.Text = $"{file.FileName}  ({file.Sheets.Count:N0} sheets)";
            }

            sheetNode.Parent?.Expand();
            sheetNode.EnsureVisible();
            tvDrawingSeriesSheets.SelectedNode = sheetNode;
        }

        private TreeNode? FindDrawingSeriesSheetNode(int sheetId)
        {
            foreach (TreeNode root in tvDrawingSeriesSheets.Nodes)
            {
                var match = FindDrawingSeriesSheetNode(root, sheetId);
                if (match != null) return match;
            }

            return null;
        }

        private static TreeNode? FindDrawingSeriesSheetNode(TreeNode node, int sheetId)
        {
            if (node.Tag is DrawingSeriesSheetRecord sheet && sheet.SheetId == sheetId)
                return node;

            foreach (TreeNode child in node.Nodes)
            {
                var match = FindDrawingSeriesSheetNode(child, sheetId);
                if (match != null) return match;
            }

            return null;
        }


        private void btnDrawingSeriesRefreshDatabase_Click(object? sender, EventArgs e)
        {
            try
            {
                bool refreshed = DrawingSeriesService.RefreshActiveDocumentSheetsToDatabase(_currentSvc, showSuccessMessage: true);
                if (refreshed)
                    LoadDrawingSeries(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Unable to refresh Drawing Series sheet data.\n\n{ex.Message}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnDrawingSeriesAssociateCurrent_Click(object? sender, EventArgs e)
        {
            try
            {
                bool associated = DrawingSeriesService.AssociateActiveDocument(_currentSvc, showSuccessMessage: true);
                if (associated)
                    LoadDrawingSeries(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Unable to associate current drawing.\n\n{ex.Message}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void dgvDrawingSeriesFiles_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (dgvDrawingSeriesFiles.Rows[e.RowIndex].Tag is not DrawingSeriesFileRecord file) return;
            OpenDrawingSeriesFile(file);
        }

        private void OpenDrawingSeriesFile(DrawingSeriesFileRecord file)
        {
            if (!DrawingSeriesAcadHelper.TryOpenDwg(file.FullPath))
            {
                MessageBox.Show($"Unable to open file.\n\n{file.FullPath}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // When a tracked file is opened or activated, push database sheet values
            // back to the layout/titleblock so renamed sheets stay coordinated.
            DrawingSeriesService.SyncActiveDocumentSheetsFromDatabase();
        }

        private DrawingSeriesFileRecord? GetSelectedDrawingSeriesFile()
        {
            if (dgvDrawingSeriesFiles.SelectedRows.Count == 0) return null;
            return dgvDrawingSeriesFiles.SelectedRows[0].Tag as DrawingSeriesFileRecord;
        }

        private void drawingSeriesFiles_OpenFile_Click(object? sender, EventArgs e)
        {
            var file = GetSelectedDrawingSeriesFile();
            if (file != null) OpenDrawingSeriesFile(file);
        }

        private void drawingSeriesFiles_OpenLocation_Click(object? sender, EventArgs e)
        {
            var file = GetSelectedDrawingSeriesFile();
            if (file == null) return;
            if (!DrawingSeriesAcadHelper.TryOpenFileLocation(file.FullPath))
            {
                MessageBox.Show($"Unable to open file location.\n\n{file.FullPath}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }


        private void drawingSeriesFiles_DeleteFileEntry_Click(object? sender, EventArgs e)
        {
            var file = GetSelectedDrawingSeriesFile();
            if (file == null) return;

            var confirm = MessageBox.Show(
                $"Delete this drawing series file entry?\n\n{file.FileName}\n{file.FullPath}\n\nThis removes the dash/file association from the database. If no other dash is associated to this file, its logged sheet entries will also be removed.",
                "IWC Drawing Series", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                var repo = new DrawingSeriesRepository();
                repo.DeleteFileEntryAsync(file.DashId, file.FileId).GetAwaiter().GetResult();
                LoadDrawingSeries(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Unable to delete drawing series file entry.\n\n{ex.Message}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void dgvDrawingSeriesFiles_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                dgvDrawingSeriesFiles.ClearSelection();
                dgvDrawingSeriesFiles.Rows[e.RowIndex].Selected = true;
                dgvDrawingSeriesFiles.CurrentCell = dgvDrawingSeriesFiles.Rows[e.RowIndex].Cells[0];
            }
        }

        private void tvDrawingSeriesSheets_NodeMouseDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node?.Tag is DrawingSeriesSheetRecord sheet)
            {
                tvDrawingSeriesSheets.SelectedNode = e.Node;
                EditDrawingSeriesSheet(sheet, e.Node.Parent?.Tag as DrawingSeriesFileRecord);
            }
        }

        private void drawingSeriesSheets_EditSheet_Click(object? sender, EventArgs e)
        {
            if (tvDrawingSeriesSheets.SelectedNode?.Tag is DrawingSeriesSheetRecord sheet)
                EditDrawingSeriesSheet(sheet, tvDrawingSeriesSheets.SelectedNode.Parent?.Tag as DrawingSeriesFileRecord);
        }

        private void drawingSeriesSheets_JumpToLayout_Click(object? sender, EventArgs e)
        {
            if (tvDrawingSeriesSheets.SelectedNode?.Tag is not DrawingSeriesSheetRecord sheet)
                return;

            var file = tvDrawingSeriesSheets.SelectedNode.Parent?.Tag as DrawingSeriesFileRecord;
            if (file == null)
                return;

            if (!DrawingSeriesAcadHelper.TryActivateSheetLayout(file.FullPath, sheet))
            {
                MessageBox.Show($"Unable to open the file and jump to the selected layout.\n\nFile: {file.FullPath}\nLayout: {sheet.LayoutName}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DrawingSeriesService.SyncActiveDocumentSheetsFromDatabase();
        }


        private void drawingSeriesSheets_DeleteSheetEntry_Click(object? sender, EventArgs e)
        {
            if (tvDrawingSeriesSheets.SelectedNode?.Tag is not DrawingSeriesSheetRecord sheet)
                return;

            var confirm = MessageBox.Show(
                $"Delete this drawing series sheet entry?\n\n{sheet.SheetNumber} - {sheet.SheetSubject}\nLayout: {sheet.LayoutName}",
                "IWC Drawing Series", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                var repo = new DrawingSeriesRepository();
                repo.DeleteSheetAsync(sheet.SheetId).GetAwaiter().GetResult();
                LoadDrawingSeries(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Unable to delete drawing series sheet entry.\n\n{ex.Message}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void tvDrawingSeriesSheets_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                tvDrawingSeriesSheets.SelectedNode = e.Node;
        }

        private void EditDrawingSeriesSheet(DrawingSeriesSheetRecord sheet, DrawingSeriesFileRecord? file)
        {
            TreeNode? editedNode = tvDrawingSeriesSheets.SelectedNode?.Tag is DrawingSeriesSheetRecord selectedSheet
                && selectedSheet.SheetId == sheet.SheetId
                    ? tvDrawingSeriesSheets.SelectedNode
                    : FindDrawingSeriesSheetNode(sheet.SheetId);

            using var frm = new FrmDrawingSeriesSheetEdit(sheet);
            if (frm.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                // IWC titleblocks derive the SHEET value from the paper-space layout tab name.
                // Therefore the sheet number edit renames the layout tab; the SHEET attribute is
                // not written directly.
                string requestedLayoutName = !string.IsNullOrWhiteSpace(frm.SheetNumber)
                    ? frm.SheetNumber.Trim()
                    : sheet.LayoutName;

                // Apply to the actual drawing first.  If the sheet belongs to a file that is not
                // active, open/activate that file so the layout tab and titleblock update the
                // correct DWG instead of silently doing nothing in the current drawing.
                if (file != null)
                {
                    if (!DrawingSeriesAcadHelper.TryOpenDwg(file.FullPath))
                    {
                        MessageBox.Show($"Unable to open the drawing that contains this sheet.\n\n{file.FullPath}",
                            "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                var updated = sheet with
                {
                    LayoutName = requestedLayoutName,
                    SheetNumber = frm.SheetNumber,
                    SheetSubject = frm.SheetSubject
                };

                bool drawingUpdated = DrawingSeriesAcadHelper.TryApplySheetRecordToActiveDocument(updated, renameLayoutToSheetNumber: true);

                var repo = new DrawingSeriesRepository();
                repo.UpdateSheetAsync(sheet.SheetId, requestedLayoutName, frm.SheetNumber, frm.SheetSubject)
                    .GetAwaiter().GetResult();

                if (!drawingUpdated)
                {
                    MessageBox.Show("The database entry was updated, but the active drawing did not contain a matching logged layout/titleblock to update.",
                        "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                if (editedNode != null)
                    UpdateDrawingSeriesSheetNode(editedNode, updated);
                else
                    LoadDrawingSeries(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Unable to update sheet data.\n\n{ex.Message}",
                    "IWC Drawing Series", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnBomAddMaterial_Click(object? sender, EventArgs e)
        {
            int projectId = _currentSvc?.Project?.Id ?? 0;
            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (projectId <= 0 || dashId <= 0) return;

            var items = PickProjectItems(projectId, "Material", @"
                SELECT ID, MatNo AS ItemNo, MatDesc AS ItemDesc
                FROM dbo.Proj_Mat
                WHERE Proj_ID = @projId AND (MatVoid = 0 OR MatVoid IS NULL)
                ORDER BY MatNo;");
            if (items.Count == 0) return;

            foreach (var item in items)
                AssociateMaterialToDash(item.Id, dashId, 0);

            LoadCurrentBom(projectId, dashId);
        }

        private void btnBomAddHardware_Click(object? sender, EventArgs e)
        {
            int projectId = _currentSvc?.Project?.Id ?? 0;
            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (projectId <= 0 || dashId <= 0) return;

            var items = PickProjectItems(projectId, "Hardware", @"
                SELECT ID, HdwNo AS ItemNo, HdwDesc AS ItemDesc
                FROM dbo.Proj_Hdw
                WHERE Proj_ID = @projId AND (HdwVoid = 0 OR HdwVoid IS NULL)
                ORDER BY HdwNo;");
            if (items.Count == 0) return;

            foreach (var item in items)
                AssociateHardwareToDash(item.Id, dashId, 0);

            LoadCurrentBom(projectId, dashId);
        }

        private void dgvBomMaterials_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex >= 0 && dgvBomMaterials.Columns[e.ColumnIndex].Name == "MatQty") return;
            int projectId = _currentSvc?.Project?.Id ?? 0;
            if (projectId <= 0) return;
            if (dgvBomMaterials.Rows[e.RowIndex].Tag is not int matId || matId <= 0) return;

            using var dlg = new FrmProjectMaterialEditor(projectId, matId);
            if (dlg.ShowDialog() == DialogResult.OK)
                LoadCurrentBom(projectId, _currentSvc?.Dash?.DashId ?? 0);
        }

        private void dgvBomHardware_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex >= 0 && dgvBomHardware.Columns[e.ColumnIndex].Name == "HdwQty") return;
            int projectId = _currentSvc?.Project?.Id ?? 0;
            if (projectId <= 0) return;
            if (dgvBomHardware.Rows[e.RowIndex].Tag is not int hdwId || hdwId <= 0) return;

            using var dlg = new FrmProjectHardwareEditor(projectId, hdwId);
            if (dlg.ShowDialog() == DialogResult.OK)
                LoadCurrentBom(projectId, _currentSvc?.Dash?.DashId ?? 0);
        }

        private void dgvBomMaterials_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (dgvBomMaterials.Columns[e.ColumnIndex].Name != "MatQty") return;
            if (dgvBomMaterials.Rows[e.RowIndex].Tag is not int matId || matId <= 0) return;

            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (dashId <= 0) return;

            if (!TryGetBomQty(dgvBomMaterials.Rows[e.RowIndex].Cells[e.ColumnIndex].Value, out long? qty))
            {
                MessageBox.Show("Quantity must be a whole number or blank.",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                LoadCurrentBom(_currentSvc?.Project?.Id ?? 0, dashId);
                return;
            }

            UpdateMaterialDashQty(matId, dashId, qty);
            dgvBomMaterials.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = qty.HasValue ? qty.Value.ToString("N0") : string.Empty;
        }

        private void dgvBomHardware_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (dgvBomHardware.Columns[e.ColumnIndex].Name != "HdwQty") return;
            if (dgvBomHardware.Rows[e.RowIndex].Tag is not int hdwId || hdwId <= 0) return;

            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (dashId <= 0) return;

            if (!TryGetBomQty(dgvBomHardware.Rows[e.RowIndex].Cells[e.ColumnIndex].Value, out long? qty))
            {
                MessageBox.Show("Quantity must be a whole number or blank.",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                LoadCurrentBom(_currentSvc?.Project?.Id ?? 0, dashId);
                return;
            }

            UpdateHardwareDashQty(hdwId, dashId, qty);
            dgvBomHardware.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = qty.HasValue ? qty.Value.ToString("N0") : string.Empty;
        }

        private void dgvBomMaterials_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            SelectBomRowForContextMenu(dgvBomMaterials, e);
        }

        private void dgvBomHardware_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            SelectBomRowForContextMenu(dgvBomHardware, e);
        }

        private static void SelectBomRowForContextMenu(DataGridView grid, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;

            if (!grid.Rows[e.RowIndex].Selected)
            {
                grid.ClearSelection();
                grid.Rows[e.RowIndex].Selected = true;
            }

            if (e.ColumnIndex >= 0)
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        }

        private void bomMaterials_RemoveAssociation_Click(object? sender, EventArgs e)
        {
            RemoveSelectedBomAssociations(dgvBomMaterials, "material", DeleteMaterialDashAssociation);
        }

        private void bomHardware_RemoveAssociation_Click(object? sender, EventArgs e)
        {
            RemoveSelectedBomAssociations(dgvBomHardware, "hardware", DeleteHardwareDashAssociation);
        }

        private void RemoveSelectedBomAssociations(DataGridView grid, string itemType, Action<int, int> deleteAction)
        {
            int projectId = _currentSvc?.Project?.Id ?? 0;
            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (dashId <= 0) return;

            var ids = grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(r => r.Tag is int id && id > 0)
                .Select(r => (int)r.Tag)
                .Distinct()
                .ToList();

            if (ids.Count == 0 && grid.CurrentRow?.Tag is int currentId && currentId > 0)
                ids.Add(currentId);

            if (ids.Count == 0) return;

            string message = ids.Count == 1
                ? $"Remove this {itemType} association from the current dash?"
                : $"Remove these {ids.Count} {itemType} associations from the current dash?";

            if (MessageBox.Show(message, "IWC Current BOM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                foreach (int id in ids)
                    deleteAction(id, dashId);

                LoadCurrentBom(projectId, dashId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to remove {itemType} association.\n\n{ex.Message}",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadMetalMaterialLists(SqlConnection conn, int projectId)
        {
            if (dgvBomMetal == null) return;

            if (dgvBomMetal.Columns["Mtl_Finish"] is DataGridViewComboBoxColumn finishCol)
            {
                finishCol.Items.Clear();
                using var cmd = new SqlCommand(@"
                    SELECT FinishValue
                    FROM (
                        SELECT DISTINCT MatNo AS FinishValue
                        FROM dbo.Proj_Mat
                        WHERE Proj_ID = @projId
                          AND (MatVoid = 0 OR MatVoid IS NULL)
                          AND MatNo IS NOT NULL
                          AND LTRIM(RTRIM(MatNo)) <> ''
                        UNION
                        SELECT DISTINCT Mtl_Finish AS FinishValue
                        FROM dbo.Proj_Mtl
                        WHERE ProjID = @projId
                          AND Mtl_Finish IS NOT NULL
                          AND LTRIM(RTRIM(Mtl_Finish)) <> ''
                    ) x
                    ORDER BY FinishValue;", conn);
                cmd.Parameters.AddWithValue("@projId", projectId);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    finishCol.Items.Add(SafeDbString(rdr, "FinishValue"));
            }

            if (dgvBomMetal.Columns["Mtl_Material"] is DataGridViewComboBoxColumn typeCol)
            {
                typeCol.Items.Clear();
                using var cmd = new SqlCommand(@"
                    SELECT Mtl_Material
                    FROM (
                        SELECT DISTINCT Mtl_Material
                        FROM dbo.Proj_MtlType
                        WHERE Mtl_Material IS NOT NULL AND LTRIM(RTRIM(Mtl_Material)) <> ''
                        UNION
                        SELECT DISTINCT Mtl_Material
                        FROM dbo.Proj_Mtl
                        WHERE ProjID = @projId
                          AND Mtl_Material IS NOT NULL
                          AND LTRIM(RTRIM(Mtl_Material)) <> ''
                    ) x
                    ORDER BY Mtl_Material;", conn);
                cmd.Parameters.AddWithValue("@projId", projectId);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                    typeCol.Items.Add(SafeDbString(rdr, "Mtl_Material"));
            }
        }

        private void dgvBomMetal_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (_loadingBomMetal || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            string colName = dgvBomMetal.Columns[e.ColumnIndex].Name;
            var row = dgvBomMetal.Rows[e.RowIndex];

            if (colName == "Mtl_Finish")
                SetMetalMatIdFromFinish(row);

            if (colName == "Mtl_Length" || colName == "Mtl_Width" || colName == "Mtl_Height")
                CalculateMetalVolume(row);
        }

        private void dgvBomMetal_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
        {
            if (dgvBomMetal.IsCurrentCellDirty)
                dgvBomMetal.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void dgvBomMetal_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            string colName = dgvBomMetal.Columns[e.ColumnIndex].Name;

            if ((colName == "Mtl_Material" || colName == "Mtl_Finish") &&
                dgvBomMetal.Columns[colName] is DataGridViewComboBoxColumn comboCol)
            {
                string value = e.FormattedValue?.ToString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value) && !comboCol.Items.Contains(value))
                    comboCol.Items.Add(value);
            }
        }

        private void dgvBomMetal_EditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (dgvBomMetal.CurrentCell == null) return;
            string colName = dgvBomMetal.Columns[dgvBomMetal.CurrentCell.ColumnIndex].Name;

            if ((colName == "Mtl_Material" || colName == "Mtl_Finish") && e.Control is ComboBox combo)
                combo.DropDownStyle = ComboBoxStyle.DropDown;
            else if (e.Control is ComboBox combo2)
                combo2.DropDownStyle = ComboBoxStyle.DropDownList;
        }

        private void dgvBomMetal_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            string colName = dgvBomMetal.Columns[e.ColumnIndex].Name;
            if (colName != "Mtl_Length" && colName != "Mtl_Width" && colName != "Mtl_Height" && colName != "Mtl_Thk") return;

            dgvBomMetal.CurrentCell = dgvBomMetal.Rows[e.RowIndex].Cells[e.ColumnIndex];
            dgvBomMetal.Rows[e.RowIndex].Selected = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Linear Measurement", null, (_, __) => ApplyLinearMeasurement(e.RowIndex, e.ColumnIndex));
            menu.Items.Add("Length & Width Measurement", null, (_, __) => ApplyLengthWidthMeasurement(e.RowIndex));
            menu.Show(dgvBomMetal, dgvBomMetal.PointToClient(Cursor.Position));
        }

        private void ApplyLinearMeasurement(int rowIndex, int columnIndex)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string label = dgvBomMetal.Columns[columnIndex].HeaderText;
            var result = doc.Editor.GetDistance($"\nPick two points to measure {label}: ");
            if (result.Status != PromptStatus.OK) return;

            var row = dgvBomMetal.Rows[rowIndex];
            row.Cells[columnIndex].Value = result.Value.ToString("0.###");
            CalculateMetalVolume(row);
            SaveMetalRow(row);
        }

        private void ApplyLengthWidthMeasurement(int rowIndex)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var p1 = doc.Editor.GetPoint("\nPick first rectangle corner: ");
            if (p1.Status != PromptStatus.OK) return;

            var p2Opts = new PromptPointOptions("\nPick opposite rectangle corner: ");
            p2Opts.BasePoint = p1.Value;
            p2Opts.UseBasePoint = true;
            var p2 = doc.Editor.GetPoint(p2Opts);
            if (p2.Status != PromptStatus.OK) return;

            double length = Math.Abs(p2.Value.X - p1.Value.X);
            double width = Math.Abs(p2.Value.Y - p1.Value.Y);

            var row = dgvBomMetal.Rows[rowIndex];
            row.Cells["Mtl_Length"].Value = length.ToString("0.###");
            row.Cells["Mtl_Width"].Value = width.ToString("0.###");
            CalculateMetalVolume(row);
            SaveMetalRow(row);
        }

        private void dgvBomMetal_RowValidated(object? sender, DataGridViewCellEventArgs e)
        {
            if (_loadingBomMetal || e.RowIndex < 0) return;
            var row = dgvBomMetal.Rows[e.RowIndex];
            if (row.IsNewRow || IsMetalRowEmpty(row)) return;
            SaveMetalRow(row);
        }

        private void dgvBomMetal_UserDeletingRow(object? sender, DataGridViewRowCancelEventArgs e)
        {
            if (e.Row.Tag is not int id || id <= 0) return;

            if (MessageBox.Show("Delete this metal part row?", "IWC Current BOM",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand("DELETE FROM dbo.Proj_Mtl WHERE ID = @id;", conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        private void dgvBomMetal_DataError(object? sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
        }

        private bool IsMetalRowEmpty(DataGridViewRow row)
        {
            foreach (DataGridViewCell cell in row.Cells)
            {
                if (!string.IsNullOrWhiteSpace(cell.Value?.ToString()))
                    return false;
            }
            return true;
        }

        private void SetMetalMatIdFromFinish(DataGridViewRow row)
        {
            int projectId = _currentSvc?.Project?.Id ?? 0;
            string finish = row.Cells["Mtl_Finish"].Value?.ToString()?.Trim() ?? string.Empty;
            if (projectId <= 0 || string.IsNullOrWhiteSpace(finish))
            {
                row.Cells["MatID"].Value = null;
                return;
            }

            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                SELECT TOP 1 ID
                FROM dbo.Proj_Mat
                WHERE Proj_ID = @projId AND MatNo = @finish
                ORDER BY ID;", conn);
            cmd.Parameters.AddWithValue("@projId", projectId);
            cmd.Parameters.AddWithValue("@finish", finish);
            object? matId = cmd.ExecuteScalar();
            row.Cells["MatID"].Value = matId == null || matId == DBNull.Value ? null : Convert.ToInt32(matId);
        }

        private static void CalculateMetalVolume(DataGridViewRow row)
        {
            if (TryGetDecimalCell(row, "Mtl_Length", out decimal length) &&
                TryGetDecimalCell(row, "Mtl_Width", out decimal width) &&
                TryGetDecimalCell(row, "Mtl_Height", out decimal height))
            {
                row.Cells["Mtl_Volume"].Value = (length * width * height).ToString("0.###");
            }
        }

        private static bool TryGetDecimalCell(DataGridViewRow row, string columnName, out decimal value)
        {
            value = 0m;
            string text = row.Cells[columnName].Value?.ToString()?.Replace(",", string.Empty).Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text) && decimal.TryParse(text, out value);
        }

        private void SaveMetalRow(DataGridViewRow row)
        {
            int projectId = _currentSvc?.Project?.Id ?? 0;
            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (projectId <= 0 || dashId <= 0) return;

            int id = row.Tag is int existingId ? existingId : 0;

            SetMetalMatIdFromFinish(row);
            CalculateMetalVolume(row);

            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                IF @id > 0
                BEGIN
                    UPDATE dbo.Proj_Mtl
                    SET Mtl_PrtNo = @prtNo,
                        Mtl_PrtDesc = @prtDesc,
                        MatID = @matId,
                        Mtl_Finish = @finish,
                        Mtl_Material = @material,
                        Mtl_Length = @length,
                        Mtl_Width = @width,
                        Mtl_Height = @height,
                        Mtl_Thk = @thk,
                        Mtl_Qty = @qty,
                        Mtl_QtyUnits = @qtyUnits,
                        Mtl_Volume = @volume,
                        Mtl_Weight = @weight,
                        Mtl_Notes = @notes,
                        Mtl_ShtReference = @shtRef,
                        Date_ActualRls = @actualRls,
                        Date_ActualShip = @actualShip,
                        DashID = @dashId,
                        ProjID = @projId
                    WHERE ID = @id;
                    SELECT @id;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.Proj_Mtl
                    (Mtl_PrtNo, Mtl_PrtDesc, MatID, Mtl_Finish, Mtl_Material,
                     Mtl_Length, Mtl_Width, Mtl_Height, Mtl_Thk, Mtl_Qty, Mtl_QtyUnits,
                     Mtl_Volume, Mtl_Weight, Mtl_Notes, Mtl_ShtReference,
                     Date_ActualRls, Date_ActualShip, DashID, ProjID)
                    OUTPUT INSERTED.ID
                    VALUES
                    (@prtNo, @prtDesc, @matId, @finish, @material,
                     @length, @width, @height, @thk, @qty, @qtyUnits,
                     @volume, @weight, @notes, @shtRef,
                     @actualRls, @actualShip, @dashId, @projId);
                END", conn);

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@projId", projectId);
            cmd.Parameters.AddWithValue("@dashId", dashId);
            AddDbDecimalParam(cmd, "@prtNo", row.Cells["Mtl_PrtNo"].Value);
            AddDbStringParam(cmd, "@prtDesc", row.Cells["Mtl_PrtDesc"].Value);
            AddDbIntParam(cmd, "@matId", row.Cells["MatID"].Value);
            AddDbStringParam(cmd, "@finish", row.Cells["Mtl_Finish"].Value);
            AddDbStringParam(cmd, "@material", row.Cells["Mtl_Material"].Value);
            AddDbDecimalParam(cmd, "@length", row.Cells["Mtl_Length"].Value);
            AddDbDecimalParam(cmd, "@width", row.Cells["Mtl_Width"].Value);
            AddDbDecimalParam(cmd, "@height", row.Cells["Mtl_Height"].Value);
            AddDbDecimalParam(cmd, "@thk", row.Cells["Mtl_Thk"].Value);
            AddDbIntParam(cmd, "@qty", row.Cells["Mtl_Qty"].Value);
            AddDbStringParam(cmd, "@qtyUnits", row.Cells["Mtl_QtyUnits"].Value);
            AddDbDecimalParam(cmd, "@volume", row.Cells["Mtl_Volume"].Value);
            AddDbDecimalParam(cmd, "@weight", row.Cells["Mtl_Weight"].Value);
            AddDbStringParam(cmd, "@notes", row.Cells["Mtl_Notes"].Value);
            AddDbStringParam(cmd, "@shtRef", row.Cells["Mtl_ShtReference"].Value);
            AddDbDateParam(cmd, "@actualRls", row.Cells["Date_ActualRls"].Value);
            AddDbDateParam(cmd, "@actualShip", row.Cells["Date_ActualShip"].Value);

            object? newId = cmd.ExecuteScalar();
            if (id <= 0 && newId != null && int.TryParse(newId.ToString(), out int insertedId))
                row.Tag = insertedId;
        }

        private void ExportCurrentBomPdf()
        {
            if (_currentSvc?.Project == null || _currentSvc?.Dash == null)
            {
                MessageBox.Show("No active project/dash context is loaded for the Current BOM report.",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Make sure any active grid edits are committed before the report is collected.
            Validate();
            dgvBomMaterials.EndEdit();
            dgvBomHardware.EndEdit();
            dgvBomMetal.EndEdit();

            var sections = new List<BomPdfSection>
            {
                CollectGridSection("Child Component Dashes", dgvBomComponents),
                CollectGridSection("Associated Materials", dgvBomMaterials),
                CollectGridSection("Associated Hardware", dgvBomHardware),
                CollectGridSection("Metal Part List", dgvBomMetal)
            };

            string reportTitle = lblBomContext?.Text?.Trim() ?? "Current BOM";
            string defaultName = SanitizeFileName($"IWC_BOM_{reportTitle}_{DateTime.Now:yyyyMMdd_HHmm}.pdf");

            using var save = new SaveFileDialog
            {
                Title = "Export Current BOM to PDF",
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = defaultName,
                AddExtension = true,
                DefaultExt = "pdf",
                OverwritePrompt = true
            };

            if (save.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                PrintBomReportToPdf(reportTitle, sections, save.FileName);
                MessageBox.Show("Current BOM PDF export complete.",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to export Current BOM PDF.\n\n{ex.Message}",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static BomPdfSection CollectGridSection(string title, DataGridView grid)
        {
            var section = new BomPdfSection(title);

            foreach (DataGridViewColumn col in grid.Columns)
            {
                if (!col.Visible) continue;
                section.Columns.Add(new BomPdfColumn(col.Name, col.HeaderText));
            }

            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;

                var values = new List<string>();
                bool hasData = false;
                foreach (var col in section.Columns)
                {
                    string text = FormatBomReportCell(row.Cells[col.Name].Value);
                    values.Add(text);
                    if (!string.IsNullOrWhiteSpace(text)) hasData = true;
                }

                if (hasData)
                    section.Rows.Add(values);
            }

            return section;
        }

        private static string FormatBomReportCell(object? value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;
            if (value is DateTime dt) return dt.ToShortDateString();
            return value.ToString()?.Trim() ?? string.Empty;
        }

        private static string SanitizeFileName(string fileName)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            return fileName;
        }

        // -----------------------------------------------------------------------
        // CSV export — mirrors ExportCurrentBomPdf but writes a flat CSV file.
        // Each BOM section is separated by a blank line with a section-header row
        // so the output remains easy to read and process in Excel.
        // -----------------------------------------------------------------------

        private void ExportCurrentBomCsv()
        {
            if (_currentSvc?.Project == null || _currentSvc?.Dash == null)
            {
                MessageBox.Show("No active project/dash context is loaded for the Current BOM report.",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Commit any in-progress grid edits before collecting data.
            Validate();
            dgvBomMaterials.EndEdit();
            dgvBomHardware.EndEdit();
            dgvBomMetal.EndEdit();

            var sections = new List<BomPdfSection>
            {
                CollectGridSection("Child Component Dashes", dgvBomComponents),
                CollectGridSection("Associated Materials",   dgvBomMaterials),
                CollectGridSection("Associated Hardware",    dgvBomHardware),
                CollectGridSection("Metal Part List",        dgvBomMetal)
            };

            string reportTitle = lblBomContext?.Text?.Trim() ?? "Current BOM";
            string defaultName = SanitizeFileName($"IWC_BOM_{reportTitle}_{DateTime.Now:yyyyMMdd_HHmm}.csv");

            using var save = new SaveFileDialog
            {
                Title         = "Export Current BOM to CSV",
                Filter        = "CSV files (*.csv)|*.csv",
                FileName      = defaultName,
                AddExtension  = true,
                DefaultExt    = "csv",
                OverwritePrompt = true
            };

            if (save.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                WriteBomCsv(reportTitle, sections, save.FileName);
                MessageBox.Show("Current BOM CSV export complete.",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to export Current BOM CSV.\n\n{ex.Message}",
                    "IWC Current BOM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Writes all BOM sections to a single CSV file.
        /// Layout: report-title row, then for each section — a blank line,
        /// a section-header row, a column-header row, and one row per data row.
        /// All values are RFC-4180 quoted so commas and line-breaks inside
        /// cell text don't corrupt the file.
        /// </summary>
        private static void WriteBomCsv(string reportTitle, List<BomPdfSection> sections, string path)
        {
            using var writer = new System.IO.StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8);

            // Report title line
            writer.WriteLine(CsvQuote(reportTitle));

            foreach (var section in sections)
            {
                // Blank separator line + section header
                writer.WriteLine();
                writer.WriteLine(CsvQuote(section.Title));

                if (section.Columns.Count == 0 || section.Rows.Count == 0)
                {
                    writer.WriteLine(CsvQuote("(no data)"));
                    continue;
                }

                // Column header row
                writer.WriteLine(string.Join(",", section.Columns.Select(c => CsvQuote(c.Header))));

                // Data rows
                foreach (var row in section.Rows)
                    writer.WriteLine(string.Join(",", row.Select(CsvQuote)));
            }
        }

        /// <summary>
        /// Wraps a single cell value in double-quotes and escapes any embedded
        /// double-quotes by doubling them (RFC 4180 §2.7).
        /// </summary>
        private static string CsvQuote(string value)
            => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

        private static void PrintBomReportToPdf(string reportTitle, List<BomPdfSection> sections, string pdfPath)
        {
            string pdfPrinter = null!;
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                if (printer.Equals("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase))
                {
                    pdfPrinter = printer;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(pdfPrinter))
                throw new InvalidOperationException("The Windows 'Microsoft Print to PDF' printer is not installed or is not available.");

            int sectionIndex = 0;
            int rowIndex = 0;
            int pageNumber = 0;

            using var printDoc = new PrintDocument
            {
                DocumentName = reportTitle,
                PrintController = new StandardPrintController()
            };
            printDoc.DefaultPageSettings.Landscape = true;
            printDoc.DefaultPageSettings.Margins = new Margins(40, 40, 40, 40);
            printDoc.PrinterSettings.PrinterName = pdfPrinter;
            printDoc.PrinterSettings.PrintToFile = true;
            printDoc.PrinterSettings.PrintFileName = pdfPath;

            printDoc.PrintPage += (sender, e) =>
            {
                if (e.Graphics == null) return;

                pageNumber++;
                Graphics g = e.Graphics;
                Rectangle bounds = e.MarginBounds;
                float y = bounds.Top;

                using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
                using var sectionFont = new Font("Segoe UI", 10, FontStyle.Bold);
                using var headerFont = new Font("Segoe UI", 8, FontStyle.Bold);
                using var cellFont = new Font("Segoe UI", 8, FontStyle.Regular);
                using var footerFont = new Font("Segoe UI", 7, FontStyle.Regular);
                using var pen = new Pen(Color.Gray, 0.75f);
                using var headerBrush = new SolidBrush(Color.FromArgb(235, 235, 235));
                using var textBrush = new SolidBrush(Color.Black);
                using var headerSf = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                    LineAlignment = StringAlignment.Center
                };
                using var cellSf = new StringFormat
                {
                    Trimming = StringTrimming.Word,
                    LineAlignment = StringAlignment.Near
                };

                g.DrawString(reportTitle, titleFont, textBrush, bounds.Left, y);
                y += titleFont.GetHeight(g) + 6;
                g.DrawString($"Printed: {DateTime.Now:g}", footerFont, textBrush, bounds.Left, y);
                y += footerFont.GetHeight(g) + 12;

                while (sectionIndex < sections.Count)
                {
                    BomPdfSection section = sections[sectionIndex];
                    if (section.Columns.Count == 0)
                    {
                        sectionIndex++;
                        rowIndex = 0;
                        continue;
                    }

                    const float sectionTitleHeight = 20f;
                    const float headerHeight = 22f;
                    const float minRowHeight = 22f;
                    float requiredForSectionStart = sectionTitleHeight + headerHeight + minRowHeight;

                    if (y + requiredForSectionStart > bounds.Bottom)
                    {
                        e.HasMorePages = true;
                        DrawBomReportFooter(g, bounds, footerFont, textBrush, pageNumber);
                        return;
                    }

                    if (rowIndex == 0)
                    {
                        g.DrawString(section.Title, sectionFont, textBrush, bounds.Left, y);
                        y += sectionTitleHeight;
                        DrawBomReportHeader(g, bounds.Left, y, bounds.Width, headerHeight, section, headerFont, textBrush, headerBrush, pen, headerSf);
                        y += headerHeight;
                    }

                    if (section.Rows.Count == 0)
                    {
                        DrawBomReportRow(g, bounds.Left, y, bounds.Width, minRowHeight, section, new List<string> { "No records found." }, cellFont, textBrush, pen, cellSf);
                        y += minRowHeight + 10;
                        sectionIndex++;
                        rowIndex = 0;
                        continue;
                    }

                    while (rowIndex < section.Rows.Count)
                    {
                        float rowHeight = MeasureBomReportRowHeight(g, bounds.Width, section, section.Rows[rowIndex], cellFont, cellSf, minRowHeight);

                        if (y + rowHeight > bounds.Bottom)
                        {
                            e.HasMorePages = true;
                            DrawBomReportFooter(g, bounds, footerFont, textBrush, pageNumber);
                            return;
                        }

                        DrawBomReportRow(g, bounds.Left, y, bounds.Width, rowHeight, section, section.Rows[rowIndex], cellFont, textBrush, pen, cellSf);
                        y += rowHeight;
                        rowIndex++;
                    }

                    y += 10;
                    sectionIndex++;
                    rowIndex = 0;
                }

                DrawBomReportFooter(g, bounds, footerFont, textBrush, pageNumber);
                e.HasMorePages = false;
            };

            printDoc.Print();
        }

        private static void DrawBomReportHeader(Graphics g, float left, float top, float width, float height,
            BomPdfSection section, Font font, Brush textBrush, Brush headerBrush, Pen pen, StringFormat sf)
        {
            float x = left;
            float colWidth = width / Math.Max(section.Columns.Count, 1);
            foreach (var col in section.Columns)
            {
                var rect = new RectangleF(x, top, colWidth, height);
                g.FillRectangle(headerBrush, rect);
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawString(col.Header, font, textBrush, rect, sf);
                x += colWidth;
            }
        }

        private static float MeasureBomReportRowHeight(Graphics g, float width, BomPdfSection section, List<string> values, Font font, StringFormat sf, float minHeight)
        {
            if (values.Count == 1 && values[0] == "No records found.")
                return minHeight;

            const float verticalPadding = 6f;
            const float horizontalPadding = 6f;
            float colWidth = width / Math.Max(section.Columns.Count, 1);
            float maxHeight = minHeight;

            for (int i = 0; i < section.Columns.Count; i++)
            {
                string text = i < values.Count ? values[i] : string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                SizeF measured = g.MeasureString(text, font, Math.Max(1, (int)(colWidth - horizontalPadding)), sf);
                maxHeight = Math.Max(maxHeight, measured.Height + verticalPadding);
            }

            return maxHeight;
        }

        private static void DrawBomReportRow(Graphics g, float left, float top, float width, float height,
            BomPdfSection section, List<string> values, Font font, Brush textBrush, Pen pen, StringFormat sf)
        {
            float x = left;
            float colWidth = width / Math.Max(section.Columns.Count, 1);

            if (values.Count == 1 && values[0] == "No records found.")
            {
                var rect = new RectangleF(left, top, width, height);
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                rect.Inflate(-3f, -2f);
                g.DrawString(values[0], font, textBrush, rect, sf);
                return;
            }

            for (int i = 0; i < section.Columns.Count; i++)
            {
                var rect = new RectangleF(x, top, colWidth, height);
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                string text = i < values.Count ? values[i] : string.Empty;
                rect.Inflate(-3f, -2f);
                g.DrawString(text, font, textBrush, rect, sf);
                x += colWidth;
            }
        }

        private static void DrawBomReportFooter(Graphics g, Rectangle bounds, Font font, Brush brush, int pageNumber)
        {
            string text = $"IWC Current BOM Report — Page {pageNumber}";
            using var sf = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString(text, font, brush, new RectangleF(bounds.Left, bounds.Bottom + 8, bounds.Width, 14), sf);
        }

        private sealed class BomPdfColumn
        {
            public BomPdfColumn(string name, string header)
            {
                Name = name;
                Header = header;
            }

            public string Name { get; }
            public string Header { get; }
        }

        private sealed class BomPdfSection
        {
            public BomPdfSection(string title)
            {
                Title = title;
            }

            public string Title { get; }
            public List<BomPdfColumn> Columns { get; } = new();
            public List<List<string>> Rows { get; } = new();
        }

        private static bool TryGetBomQty(object? value, out long? qty)
        {
            qty = null;
            string text = value?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return true;

            text = text.Replace(",", string.Empty);
            if (!long.TryParse(text, out long parsed) || parsed < 0)
                return false;

            qty = parsed;
            return true;
        }

        private static List<(int Id, string No, string Description)> PickProjectItems(int projectId, string itemType, string sql)
        {
            using var frm = new Form
            {
                Text = $"Associate {itemType} to Current Dash",
                StartPosition = FormStartPosition.CenterParent,
                Width = 650,
                Height = 450,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowIcon = false
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            grid.Columns.Add("ItemNo", "No");
            grid.Columns.Add("ItemDesc", "Description");
            grid.Columns["ItemNo"].FillWeight = 20;
            grid.Columns["ItemDesc"].FillWeight = 80;

            using (var conn = IWCConn.GetSqlConnection())
            {
                conn.Open();
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@projId", projectId);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    int row = grid.Rows.Add(SafeDbString(rdr, "ItemNo"), SafeDbString(rdr, "ItemDesc"));
                    grid.Rows[row].Tag = SafeDbInt(rdr, "ID");
                }
            }

            var help = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Select one or more rows, then click Associate. Use Ctrl/Shift to select multiple items.",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6)
            };
            var ok = new Button { Text = "Associate", Width = 100, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
            buttons.Controls.AddRange(new Control[] { ok, cancel });
            frm.Controls.Add(grid);
            frm.Controls.Add(help);
            frm.Controls.Add(buttons);
            frm.AcceptButton = ok;
            frm.CancelButton = cancel;

            grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    grid.ClearSelection();
                    grid.Rows[e.RowIndex].Selected = true;
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
                    frm.DialogResult = DialogResult.OK;
                }
            };

            if (frm.ShowDialog() != DialogResult.OK)
                return new List<(int Id, string No, string Description)>();

            var rows = grid.SelectedRows
                .Cast<DataGridViewRow>()
                .OrderBy(r => r.Index)
                .ToList();

            if (rows.Count == 0 && grid.CurrentRow != null)
                rows.Add(grid.CurrentRow);

            var selected = new List<(int Id, string No, string Description)>();
            foreach (var row in rows)
            {
                int id = row.Tag is int rowId ? rowId : 0;
                if (id <= 0) continue;

                selected.Add((
                    id,
                    row.Cells["ItemNo"].Value?.ToString() ?? string.Empty,
                    row.Cells["ItemDesc"].Value?.ToString() ?? string.Empty));
            }

            return selected;
        }

        private static long? PromptForBomQty(string itemNo, string itemDesc, string dashNum, out bool cancelled)
        {
            cancelled = false;
            using var frm = new Form
            {
                Text = "Quantity",
                StartPosition = FormStartPosition.CenterParent,
                Width = 330,
                Height = 175,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowIcon = false
            };
            var lbl = new Label
            {
                Text = $"Quantity for {itemNo} - {itemDesc}\r\nDash {dashNum}:",
                Left = 12,
                Top = 12,
                Width = 290,
                Height = 42
            };
            var txt = new TextBox { Left = 12, Top = 60, Width = 120 };
            var ok = new Button { Text = "OK", Left = 120, Top = 95, Width = 80, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 208, Top = 95, Width = 80, DialogResult = DialogResult.Cancel };
            frm.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            frm.AcceptButton = ok;
            frm.CancelButton = cancel;

            if (frm.ShowDialog() != DialogResult.OK)
            {
                cancelled = true;
                return null;
            }

            return long.TryParse(txt.Text.Trim(), out long v) ? v : null;
        }

        private static bool ConfirmedNullQty(string itemType)
        {
            return MessageBox.Show(
                $"No quantity was entered. Associate this {itemType} without a quantity?",
                "Associate to Dash", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private static void AssociateMaterialToDash(int matId, int dashId, long? qty)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                IF EXISTS (SELECT 1 FROM dbo.Proj_Mat_Dash WHERE MatID = @mid AND DashID = @did)
                    UPDATE dbo.Proj_Mat_Dash SET MatQty = @qty WHERE MatID = @mid AND DashID = @did;
                ELSE
                    INSERT INTO dbo.Proj_Mat_Dash (MatID, DashID, MatQty) VALUES (@mid, @did, @qty);", conn);
            cmd.Parameters.AddWithValue("@mid", matId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.Parameters.AddWithValue("@qty", qty.HasValue ? (object)qty.Value : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static void AssociateHardwareToDash(int hdwId, int dashId, long? qty)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                IF EXISTS (SELECT 1 FROM dbo.Proj_Hdw_Dash WHERE HdwID = @hid AND DashID = @did)
                    UPDATE dbo.Proj_Hdw_Dash SET HdwQty = @qty WHERE HdwID = @hid AND DashID = @did;
                ELSE
                    INSERT INTO dbo.Proj_Hdw_Dash (HdwID, DashID, HdwQty) VALUES (@hid, @did, @qty);", conn);
            cmd.Parameters.AddWithValue("@hid", hdwId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.Parameters.AddWithValue("@qty", qty.HasValue ? (object)qty.Value : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static void DeleteMaterialDashAssociation(int matId, int dashId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                DELETE FROM dbo.Proj_Mat_Dash
                WHERE MatID = @mid AND DashID = @did;", conn);
            cmd.Parameters.AddWithValue("@mid", matId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.ExecuteNonQuery();
        }

        private static void DeleteHardwareDashAssociation(int hdwId, int dashId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                DELETE FROM dbo.Proj_Hdw_Dash
                WHERE HdwID = @hid AND DashID = @did;", conn);
            cmd.Parameters.AddWithValue("@hid", hdwId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.ExecuteNonQuery();
        }

        private static void UpdateMaterialDashQty(int matId, int dashId, long? qty)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Proj_Mat_Dash
                SET MatQty = @qty
                WHERE MatID = @mid AND DashID = @did;", conn);
            cmd.Parameters.AddWithValue("@mid", matId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.Parameters.AddWithValue("@qty", qty.HasValue ? (object)qty.Value : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static void UpdateHardwareDashQty(int hdwId, int dashId, long? qty)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new SqlCommand(@"
                UPDATE dbo.Proj_Hdw_Dash
                SET HdwQty = @qty
                WHERE HdwID = @hid AND DashID = @did;", conn);
            cmd.Parameters.AddWithValue("@hid", hdwId);
            cmd.Parameters.AddWithValue("@did", dashId);
            cmd.Parameters.AddWithValue("@qty", qty.HasValue ? (object)qty.Value : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static void AddDbStringParam(SqlCommand cmd, string name, object? value)
        {
            string text = value?.ToString() ?? string.Empty;
            cmd.Parameters.AddWithValue(name, string.IsNullOrWhiteSpace(text) ? DBNull.Value : text.Trim());
        }

        private static void AddDbIntParam(SqlCommand cmd, string name, object? value)
        {
            string text = value?.ToString()?.Replace(",", string.Empty).Trim() ?? string.Empty;
            if (int.TryParse(text, out int parsed))
                cmd.Parameters.AddWithValue(name, parsed);
            else
                cmd.Parameters.AddWithValue(name, DBNull.Value);
        }

        private static void AddDbDecimalParam(SqlCommand cmd, string name, object? value)
        {
            string text = value?.ToString()?.Replace(",", string.Empty).Trim() ?? string.Empty;
            if (decimal.TryParse(text, out decimal parsed))
                cmd.Parameters.AddWithValue(name, parsed);
            else
                cmd.Parameters.AddWithValue(name, DBNull.Value);
        }

        private static void AddDbDateParam(SqlCommand cmd, string name, object? value)
        {
            string text = value?.ToString()?.Trim() ?? string.Empty;
            if (DateTime.TryParse(text, out DateTime parsed))
                cmd.Parameters.AddWithValue(name, parsed.Date);
            else
                cmd.Parameters.AddWithValue(name, DBNull.Value);
        }

        private static string SafeDbString(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                return rdr.IsDBNull(i) ? string.Empty : Convert.ToString(rdr.GetValue(i))?.Trim() ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static int SafeDbInt(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                return rdr.IsDBNull(i) ? 0 : Convert.ToInt32(rdr.GetValue(i));
            }
            catch { return 0; }
        }

        private static string SafeDbLongString(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                return rdr.IsDBNull(i) ? string.Empty : Convert.ToInt64(rdr.GetValue(i)).ToString("N0");
            }
            catch { return string.Empty; }
        }

        private static int? SafeDbNullableInt(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                return rdr.IsDBNull(i) ? null : Convert.ToInt32(rdr.GetValue(i));
            }
            catch { return null; }
        }

        private static string SafeDbIntString(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                return rdr.IsDBNull(i) ? string.Empty : Convert.ToInt32(rdr.GetValue(i)).ToString();
            }
            catch { return string.Empty; }
        }

        private static string SafeDbDecimalString(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                if (rdr.IsDBNull(i)) return string.Empty;
                decimal value = Convert.ToDecimal(rdr.GetValue(i));
                return value.ToString("0.###");
            }
            catch { return string.Empty; }
        }

        private static string SafeDbDate(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                if (rdr.IsDBNull(i)) return string.Empty;
                return Convert.ToDateTime(rdr.GetValue(i)).ToShortDateString();
            }
            catch { return string.Empty; }
        }

        private void ClearProjectFields()
        {
            txtProjNum.Text = txtProjName.Text = txtArch.Text =
            txtCont.Text    = txtPM.Text       = "NA";

            if (txtProjectAddress != null) txtProjectAddress.Text = string.Empty;
            if (txtProjStartDate != null) txtProjStartDate.Text = string.Empty;
            if (txtProjEstProduction != null) txtProjEstProduction.Text = string.Empty;
            if (txtProjEstInstall != null) txtProjEstInstall.Text = string.Empty;
            if (txtProjEstComplete != null) txtProjEstComplete.Text = string.Empty;
            if (txtProjDaysTotal != null) txtProjDaysTotal.Text = string.Empty;
            if (txtProjDaysRemaining != null) txtProjDaysRemaining.Text = string.Empty;
            if (txtProjPercentUsed != null) txtProjPercentUsed.Text = string.Empty;
            if (chkFSC != null) chkFSC.Checked = false;
            if (chkLEED != null) chkLEED.Checked = false;
            if (txtProjNotes != null) txtProjNotes.Text = string.Empty;
            if (lblDashHeader != null) lblDashHeader.Text = "Current Dash Data";
            ClearDashEditControls();
            _timelineStart = _timelineEnd = null;
            pnlTimeline?.Invalidate();
        }

        // -----------------------------------------------------------------------
        // Current Project tab — project/dash edit loading and saving
        // -----------------------------------------------------------------------

        private bool _loadingProjectDashEdit;
        private DateOnly? _timelineStart;
        private DateOnly? _timelineEnd;

        private sealed class ComboItem
        {
            public object? Value { get; init; }
            public string Text { get; init; } = string.Empty;
            public override string ToString() => Text;
        }

        private void LoadProjectDashEditData(int projectId, int dashId)
        {
            if (projectId <= 0 || txtProjectAddress == null) return;

            _loadingProjectDashEdit = true;
            try
            {
                using var conn = IWCConn.GetSqlConnection();
                conn.Open();

                LoadProjectEditRow(conn, projectId);
                LoadDashLookups(conn, projectId);

                if (dashId > 0)
                    LoadDashEditRow(conn, dashId);
                else
                    ClearDashEditControls();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to load Current Project tab detail fields.\n\n{ex.Message}",
                    "IWC Project Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _loadingProjectDashEdit = false;
                pnlTimeline?.Invalidate();
            }
        }

        private void LoadProjectEditRow(SqlConnection conn, int projectId)
        {
            using var cmd = new SqlCommand(@"
                SELECT IDNum, Proj_Name, Proj_Add_St, Proj_Add_City, Proj_Add_State, Proj_Add_Zip,
                       Proj_StartDate, Proj_EstProduction, Proj_EstInstall, Proj_EstComp,
                       FSC, LEED, Proj_Notes
                FROM dbo.Proj
                WHERE ID = @projectId;", conn);
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) return;

            string street = SafeDbString(rdr, "Proj_Add_St");
            string city = SafeDbString(rdr, "Proj_Add_City");
            string state = SafeDbString(rdr, "Proj_Add_State");
            string zip = SafeDbString(rdr, "Proj_Add_Zip");
            txtProjectAddress.Text = string.Join(Environment.NewLine,
                WhereNotBlank(new[]
                {
                    street,
                    string.Join(", ", WhereNotBlank(new[] { city, state })),
                    zip
                }));

            DateOnly? start = SafeDbDateOnly(rdr, "Proj_StartDate");
            DateOnly? production = SafeDbDateOnly(rdr, "Proj_EstProduction");
            DateOnly? install = SafeDbDateOnly(rdr, "Proj_EstInstall");
            DateOnly? complete = SafeDbDateOnly(rdr, "Proj_EstComp");

            txtProjStartDate.Text = FormatDate(start);
            txtProjEstProduction.Text = FormatDate(production);
            txtProjEstInstall.Text = FormatDate(install);
            txtProjEstComplete.Text = FormatDate(complete);
            chkFSC.Checked = SafeDbBool(rdr, "FSC");
            chkLEED.Checked = SafeDbBool(rdr, "LEED");
            txtProjNotes.Text = SafeDbString(rdr, "Proj_Notes");

            UpdateProjectTimeline(start, complete);
        }

        private void UpdateProjectTimeline(DateOnly? start, DateOnly? complete)
        {
            _timelineStart = start;
            _timelineEnd = complete;

            if (start.HasValue && complete.HasValue && complete.Value >= start.Value)
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                int total = complete.Value.DayNumber - start.Value.DayNumber + 1;
                int remaining = Math.Max(0, complete.Value.DayNumber - today.DayNumber);
                int elapsed = Math.Min(total, Math.Max(0, today.DayNumber - start.Value.DayNumber));
                double pct = total > 0 ? elapsed * 100.0 / total : 0;

                txtProjDaysTotal.Text = total.ToString("N0");
                txtProjDaysRemaining.Text = remaining.ToString("N0");
                txtProjPercentUsed.Text = pct.ToString("0.0") + "%";
            }
            else
            {
                txtProjDaysTotal.Text = string.Empty;
                txtProjDaysRemaining.Text = string.Empty;
                txtProjPercentUsed.Text = string.Empty;
            }

            pnlTimeline?.Invalidate();
        }

        private void LoadDashLookups(SqlConnection conn, int projectId)
        {
            FillCombo(cboDashType, conn,
                "SELECT ID, Dash_Type AS DisplayText FROM dbo.Proj_Dash_Type ORDER BY ID;",
                null, "ID", "DisplayText", includeBlank: true);

            FillCombo(cboDashParent, conn, @"
                SELECT d.ID,
                       CONCAT(d.Dash_Num, CASE WHEN NULLIF(LTRIM(RTRIM(d.Dash_Desc)), '') IS NULL THEN '' ELSE ' - ' + d.Dash_Desc END) AS DisplayText
                FROM dbo.Proj_Dash d
                LEFT JOIN dbo.Proj_Dash_Type dt ON dt.ID = d.Dash_Type
                WHERE d.Proj_ID = @projectId
                  AND (d.Act_Void = 0 OR d.Act_Void IS NULL)
                  AND (d.Dash_Type = 1 OR dt.Dash_Type = 'Series')
                ORDER BY TRY_CAST(d.Dash_Num AS int), d.Dash_Num;",
                new Dictionary<string, object?> { ["@projectId"] = projectId }, "ID", "DisplayText", includeBlank: true);

            FillCombo(cboMfg, conn,
                "SELECT ID, MfrName AS DisplayText FROM dbo.Proj_Dash_Mfr ORDER BY MfrName;",
                null, "ID", "DisplayText", includeBlank: true);

            FillCombo(cboDashStatus, conn,
                "SELECT ID, Status AS DisplayText FROM dbo.Proj_Dash_Status ORDER BY ID;",
                null, "ID", "DisplayText", includeBlank: true);

            FillCombo(cboDwgDraftsman, conn, @"
                SELECT ID, COALESCE(NULLIF(LTRIM(RTRIM(UserIni)), ''), UserName) AS DisplayText
                FROM dbo.Mng_Users
                WHERE UserRole = 'Eng'
                  AND UserStatus = 'active'
                ORDER BY DisplayText;",
                null, "ID", "DisplayText", includeBlank: true);

            FillCombo(cboDashUnit, conn,
                "SELECT Unit AS ID, Unit AS DisplayText FROM dbo.Proj_Dash_Units WHERE Unit IS NOT NULL ORDER BY Unit;",
                null, "ID", "DisplayText", includeBlank: true);
        }

        private void FillCombo(ComboBox combo, SqlConnection conn, string sql,
            Dictionary<string, object?>? parameters, string valueColumn, string textColumn, bool includeBlank)
        {
            var items = new List<ComboItem>();
            if (includeBlank) items.Add(new ComboItem { Value = null, Text = string.Empty });

            using var cmd = new SqlCommand(sql, conn);
            if (parameters != null)
            {
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            }

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                object? value = rdr[valueColumn] == DBNull.Value ? null : rdr[valueColumn];
                string text = rdr[textColumn] == DBNull.Value ? string.Empty : rdr[textColumn].ToString() ?? string.Empty;
                items.Add(new ComboItem { Value = value, Text = text });
            }

            combo.DisplayMember = nameof(ComboItem.Text);
            combo.ValueMember = nameof(ComboItem.Value);
            combo.DataSource = items;
        }

        private void LoadDashEditRow(SqlConnection conn, int dashId)
        {
            using var cmd = new SqlCommand(@"
                SELECT ID, Proj_ID, Dash_Num, Dash_Desc, Dash_Qty, Dash_Unit,
                       Date_TargetSubmit, Date_ActualSubmit,
                       Date_TargetApprove, Date_ActualApprove,
                       Date_Target_FD, Date_Actual_FD,
                       Date_TargetRLSMfr, Date_ActualRlsMfr,
                       Date_Target_FieldReady, Date_Actual_FieldReady,
                       Date_TargetShip, Date_ActualShip,
                       Mfg, Dwg_Draftsman, Dash_Room, Dash_Floor,
                       DashStatus, Shop_Notes, Dash_Notes, Dash_Parent, Dash_Type
                FROM dbo.Proj_Dash
                WHERE ID = @dashId;", conn);
            cmd.Parameters.AddWithValue("@dashId", dashId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read())
            {
                ClearDashEditControls();
                return;
            }

            lblDashHeader.Text = $"{SafeDbString(rdr, "Dash_Num")} {SafeDbString(rdr, "Dash_Desc")}".Trim();
            SelectComboValue(cboDashType, SafeDbNullableInt(rdr, "Dash_Type"));
            SelectComboValue(cboDashParent, SafeDbNullableInt(rdr, "Dash_Parent"));
            SelectComboValue(cboMfg, SafeDbNullableInt(rdr, "Mfg"));
            SelectComboValueOrText(cboDashStatus, ParseNullableInt(SafeDbString(rdr, "DashStatus")), SafeDbString(rdr, "DashStatus"));
            SelectComboValue(cboDwgDraftsman, SafeDbNullableInt(rdr, "Dwg_Draftsman"));
            SelectComboValue(cboDashUnit, SafeDbString(rdr, "Dash_Unit"));

            txtDashFloor.Text = SafeDbIntString(rdr, "Dash_Floor");
            txtDashRoom.Text = SafeDbString(rdr, "Dash_Room");
            txtDashQty.Text = SafeDbIntString(rdr, "Dash_Qty");
            txtDashNotes.Text = SafeDbString(rdr, "Dash_Notes");
            txtShopNotes.Text = SafeDbString(rdr, "Shop_Notes");

            SetSchedulePicker(txtDateTargetSubmit, SafeDbDateOnly(rdr, "Date_TargetSubmit"));
            SetSchedulePicker(txtDateActualSubmit, SafeDbDateOnly(rdr, "Date_ActualSubmit"));
            SetSchedulePicker(txtDateTargetApprove, SafeDbDateOnly(rdr, "Date_TargetApprove"));
            SetSchedulePicker(txtDateActualApprove, SafeDbDateOnly(rdr, "Date_ActualApprove"));
            SetSchedulePicker(txtDateTargetFD, SafeDbDateOnly(rdr, "Date_Target_FD"));
            SetSchedulePicker(txtDateActualFD, SafeDbDateOnly(rdr, "Date_Actual_FD"));
            SetSchedulePicker(txtDateTargetRlsMfr, SafeDbDateOnly(rdr, "Date_TargetRLSMfr"));
            SetSchedulePicker(txtDateActualRlsMfr, SafeDbDateOnly(rdr, "Date_ActualRlsMfr"));
            SetSchedulePicker(txtDateTargetFieldReady, SafeDbDateOnly(rdr, "Date_Target_FieldReady"));
            SetSchedulePicker(txtDateActualFieldReady, SafeDbDateOnly(rdr, "Date_Actual_FieldReady"));
            SetSchedulePicker(txtDateTargetShip, SafeDbDateOnly(rdr, "Date_TargetShip"));
            SetSchedulePicker(txtDateActualShip, SafeDbDateOnly(rdr, "Date_ActualShip"));
        }

        private void ClearDashEditControls()
        {
            if (cboDashType == null) return;
            SelectComboValue(cboDashType, null);
            SelectComboValue(cboDashParent, null);
            SelectComboValue(cboMfg, null);
            SelectComboValue(cboDashStatus, null);
            SelectComboValue(cboDwgDraftsman, null);
            SelectComboValue(cboDashUnit, null);
            txtDashFloor.Text = txtDashRoom.Text = txtDashQty.Text = string.Empty;
            txtDashNotes.Text = txtShopNotes.Text = string.Empty;
            SetSchedulePicker(txtDateTargetSubmit, null); SetSchedulePicker(txtDateActualSubmit, null);
            SetSchedulePicker(txtDateTargetApprove, null); SetSchedulePicker(txtDateActualApprove, null);
            SetSchedulePicker(txtDateTargetFD, null); SetSchedulePicker(txtDateActualFD, null);
            SetSchedulePicker(txtDateTargetRlsMfr, null); SetSchedulePicker(txtDateActualRlsMfr, null);
            SetSchedulePicker(txtDateTargetFieldReady, null); SetSchedulePicker(txtDateActualFieldReady, null);
            SetSchedulePicker(txtDateTargetShip, null); SetSchedulePicker(txtDateActualShip, null);
        }

        private void SaveProjectDashDetails()
        {
            if (_loadingProjectDashEdit) return;
            int projectId = _currentSvc?.Project?.Id ?? 0;
            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (projectId <= 0)
            {
                MessageBox.Show("No project is currently associated with this drawing.",
                    "IWC Project Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                using var conn = IWCConn.GetSqlConnection();
                conn.Open();

                using (var cmd = new SqlCommand(@"
                    UPDATE dbo.Proj
                    SET Proj_StartDate = @Proj_StartDate,
                        Proj_EstProduction = @Proj_EstProduction,
                        Proj_EstInstall = @Proj_EstInstall,
                        Proj_EstComp = @Proj_EstComp,
                        FSC = @FSC,
                        LEED = @LEED,
                        Proj_Notes = @Proj_Notes,
                        Date_Modified = SYSUTCDATETIME()
                    WHERE ID = @ProjectId;", conn))
                {
                    cmd.Parameters.AddWithValue("@ProjectId", projectId);
                    AddDateParam(cmd, "@Proj_StartDate", txtProjStartDate.Text);
                    AddDateParam(cmd, "@Proj_EstProduction", txtProjEstProduction.Text);
                    AddDateParam(cmd, "@Proj_EstInstall", txtProjEstInstall.Text);
                    AddDateParam(cmd, "@Proj_EstComp", txtProjEstComplete.Text);
                    cmd.Parameters.AddWithValue("@FSC", chkFSC.Checked);
                    cmd.Parameters.AddWithValue("@LEED", chkLEED.Checked);
                    cmd.Parameters.AddWithValue("@Proj_Notes", NullIfBlank(txtProjNotes.Text));
                    cmd.ExecuteNonQuery();
                }

                if (dashId > 0)
                {
                    using var cmd = new SqlCommand(@"
                        UPDATE dbo.Proj_Dash
                        SET Dash_Type = @Dash_Type,
                            Dash_Parent = @Dash_Parent,
                            Mfg = @Mfg,
                            DashStatus = @DashStatus,
                            Dwg_Draftsman = @Dwg_Draftsman,
                            Dash_Floor = @Dash_Floor,
                            Dash_Room = @Dash_Room,
                            Dash_Qty = @Dash_Qty,
                            Dash_Unit = @Dash_Unit,
                            Date_TargetSubmit = @Date_TargetSubmit,
                            Date_ActualSubmit = @Date_ActualSubmit,
                            Date_TargetApprove = @Date_TargetApprove,
                            Date_ActualApprove = @Date_ActualApprove,
                            Date_Target_FD = @Date_Target_FD,
                            Date_Actual_FD = @Date_Actual_FD,
                            Date_TargetRLSMfr = @Date_TargetRLSMfr,
                            Date_ActualRlsMfr = @Date_ActualRlsMfr,
                            Date_Target_FieldReady = @Date_Target_FieldReady,
                            Date_Actual_FieldReady = @Date_Actual_FieldReady,
                            Date_TargetShip = @Date_TargetShip,
                            Date_ActualShip = @Date_ActualShip,
                            Dash_Notes = @Dash_Notes,
                            Shop_Notes = @Shop_Notes,
                            Date_DashUpdate = SYSUTCDATETIME()
                        WHERE ID = @DashId;", conn);
                    cmd.Parameters.AddWithValue("@DashId", dashId);
                    AddIntParam(cmd, "@Dash_Type", SelectedComboInt(cboDashType));
                    AddIntParam(cmd, "@Dash_Parent", SelectedComboInt(cboDashParent));
                    AddIntParam(cmd, "@Mfg", SelectedComboInt(cboMfg));
                    AddIntParam(cmd, "@DashStatus", SelectedComboInt(cboDashStatus));
                    AddIntParam(cmd, "@Dwg_Draftsman", SelectedComboInt(cboDwgDraftsman));
                    AddIntParam(cmd, "@Dash_Floor", ParseNullableInt(txtDashFloor.Text));
                    cmd.Parameters.AddWithValue("@Dash_Room", NullIfBlank(txtDashRoom.Text));
                    AddIntParam(cmd, "@Dash_Qty", ParseNullableInt(txtDashQty.Text));
                    cmd.Parameters.AddWithValue("@Dash_Unit", SelectedComboText(cboDashUnit));
                    AddDateParam(cmd, "@Date_TargetSubmit", txtDateTargetSubmit);
                    AddDateParam(cmd, "@Date_ActualSubmit", txtDateActualSubmit);
                    AddDateParam(cmd, "@Date_TargetApprove", txtDateTargetApprove);
                    AddDateParam(cmd, "@Date_ActualApprove", txtDateActualApprove);
                    AddDateParam(cmd, "@Date_Target_FD", txtDateTargetFD);
                    AddDateParam(cmd, "@Date_Actual_FD", txtDateActualFD);
                    AddDateParam(cmd, "@Date_TargetRLSMfr", txtDateTargetRlsMfr);
                    AddDateParam(cmd, "@Date_ActualRlsMfr", txtDateActualRlsMfr);
                    AddDateParam(cmd, "@Date_Target_FieldReady", txtDateTargetFieldReady);
                    AddDateParam(cmd, "@Date_Actual_FieldReady", txtDateActualFieldReady);
                    AddDateParam(cmd, "@Date_TargetShip", txtDateTargetShip);
                    AddDateParam(cmd, "@Date_ActualShip", txtDateActualShip);
                    cmd.Parameters.AddWithValue("@Dash_Notes", NullIfBlank(txtDashNotes.Text));
                    cmd.Parameters.AddWithValue("@Shop_Notes", NullIfBlank(txtShopNotes.Text));
                    cmd.ExecuteNonQuery();
                }

                LoadProjectDashEditData(projectId, dashId);
                RefreshFromContext();
                MessageBox.Show("Project and dash data saved.",
                    "IWC Project Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to save project/dash data.\n\n{ex.Message}",
                    "IWC Project Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void pnlTimeline_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);

            var rect = new Rectangle(26, 28, Math.Max(10, pnlTimeline.Width - 52), 16);
            using var outline = new Pen(Color.Black);
            using var elapsedBrush = new SolidBrush(Color.Firebrick);
            using var remainBrush = new SolidBrush(Color.Gold);
            using var todayPen = new Pen(Color.Black, 2);
            using var font = new Font(SystemFonts.DefaultFont.FontFamily, 7);

            g.DrawRectangle(outline, rect);
            if (!_timelineStart.HasValue || !_timelineEnd.HasValue || _timelineEnd.Value < _timelineStart.Value)
            {
                g.DrawString("Project timeline dates are not set.", SystemFonts.DefaultFont, Brushes.DimGray, 8, 8);
                return;
            }

            var today = DateOnly.FromDateTime(DateTime.Today);
            int total = Math.Max(1, _timelineEnd.Value.DayNumber - _timelineStart.Value.DayNumber);
            double t = Math.Clamp((today.DayNumber - _timelineStart.Value.DayNumber) / (double)total, 0, 1);
            int usedWidth = (int)Math.Round(rect.Width * t);
            if (usedWidth > 0) g.FillRectangle(elapsedBrush, rect.X + 1, rect.Y + 1, usedWidth, rect.Height - 1);
            if (usedWidth < rect.Width) g.FillRectangle(remainBrush, rect.X + usedWidth, rect.Y + 1, rect.Width - usedWidth - 1, rect.Height - 1);
            int todayX = rect.X + usedWidth;
            g.DrawLine(todayPen, todayX, rect.Y - 6, todayX, rect.Bottom + 10);
            g.DrawString(FormatDate(_timelineStart), font, Brushes.Black, rect.X - 18, rect.Y - 18);
            string endText = FormatDate(_timelineEnd);
            var endSize = g.MeasureString(endText, font);
            g.DrawString(endText, font, Brushes.Black, rect.Right - endSize.Width + 18, rect.Y - 18);
            g.DrawString($"{t * 100:0.0}%", font, Brushes.Black, rect.X + rect.Width / 2 - 16, rect.Y - 14);
            g.DrawString("Today", font, Brushes.DimGray, todayX - 12, rect.Bottom + 10);
            g.DrawRectangle(outline, rect);
        }

        private static string FormatDate(DateOnly? date) => date.HasValue ? date.Value.ToString("M/d/yyyy") : string.Empty;

        private static DateOnly? SafeDbDateOnly(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                if (rdr.IsDBNull(i)) return null;
                return DateOnly.FromDateTime(Convert.ToDateTime(rdr.GetValue(i)));
            }
            catch { return null; }
        }

        private static bool SafeDbBool(SqlDataReader rdr, string column)
        {
            try
            {
                int i = rdr.GetOrdinal(column);
                return !rdr.IsDBNull(i) && Convert.ToBoolean(rdr.GetValue(i));
            }
            catch { return false; }
        }

        private static IEnumerable<string> WhereNotBlank(IEnumerable<string> values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
        }

        private static int? ParseNullableInt(string? text)
            => int.TryParse((text ?? string.Empty).Trim(), out int value) ? value : null;

        private static object NullIfBlank(string? text)
            => string.IsNullOrWhiteSpace(text) ? DBNull.Value : text.Trim();

        private static void AddDateParam(SqlCommand cmd, string name, string text)
        {
            if (DateTime.TryParse(text, out var dt))
                cmd.Parameters.AddWithValue(name, (object)dt.Date);
            else
                cmd.Parameters.AddWithValue(name, DBNull.Value);
        }

        /// <summary>
        /// Overload for the optional schedule-date pickers (ShowCheckBox = true):
        /// unchecked -> DBNull, checked -> the selected date.
        /// </summary>
        private static void AddDateParam(SqlCommand cmd, string name, DateTimePicker picker)
        {
            if (picker.Checked)
                cmd.Parameters.AddWithValue(name, picker.Value.Date);
            else
                cmd.Parameters.AddWithValue(name, DBNull.Value);
        }

        /// <summary>
        /// Sets a schedule DateTimePicker (ShowCheckBox = true) from a nullable
        /// DateOnly: null -> unchecked/empty, value -> checked with that date.
        /// </summary>
        private static void SetSchedulePicker(DateTimePicker picker, DateOnly? value)
        {
            if (value.HasValue)
            {
                picker.Value   = value.Value.ToDateTime(TimeOnly.MinValue);
                picker.Checked = true;
            }
            else
            {
                picker.Checked = false;
            }
        }

        private static void AddIntParam(SqlCommand cmd, string name, int? value)
            => cmd.Parameters.AddWithValue(name, value.HasValue ? (object)value.Value : DBNull.Value);

        private static int? SelectedComboInt(ComboBox combo)
        {
            if (combo.SelectedItem is ComboItem item && item.Value != null)
                return Convert.ToInt32(item.Value);
            return null;
        }

        private static object SelectedComboText(ComboBox combo)
        {
            if (combo.SelectedItem is ComboItem item && !string.IsNullOrWhiteSpace(item.Text))
                return item.Text.Trim();
            return DBNull.Value;
        }

        private static void SelectComboValueOrText(ComboBox combo, object? value, string? text)
        {
            SelectComboValue(combo, value);
            if (combo.SelectedItem is ComboItem selected && selected.Value != null) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var obj in combo.Items)
            {
                if (obj is ComboItem item && string.Equals(item.Text, text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private static void SelectComboValue(ComboBox combo, object? value)
        {
            if (combo.DataSource == null) return;
            foreach (var obj in combo.Items)
            {
                if (obj is not ComboItem item) continue;
                if (value == null && item.Value == null)
                {
                    combo.SelectedItem = item;
                    return;
                }
                if (value != null && item.Value != null && item.Value.ToString() == value.ToString())
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
        }

        // -----------------------------------------------------------------------
        // Button handlers — delegate to ProjectContextService
        // -----------------------------------------------------------------------

        private void btnRefresh_Click(object? sender, EventArgs e)
            => RefreshFromContext();

        private void btnChangeProject_Click(object? sender, EventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            ProjectContextService.GetOrCreate(doc).ChangeProject();
        }

        private void btnSaveFileProps_Click(object? sender, EventArgs e)
            => SaveFileProps();

        private void btnSaveProjectDash_Click(object? sender, EventArgs e)
            => SaveProjectDashDetails();

        // -----------------------------------------------------------------------
        // Designer-generated members (unchanged from original)
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            tabControl   = new TabControl();
            var tabProj  = new TabPage("Current Project");
            var tabBom   = new TabPage("Current BOM");
            var tabDrawingSeries = new TabPage("Drawing Series");
            var tabFile  = new TabPage("File Properties");
            ConfigureTopTabs();

            // --- Tab 1: Current Project ---
            var tabProjMain = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            tabProjMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tabProjMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tabProjMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            lblCurrentProjectHeader = new Label
            {
                Text = "Current Project Data",
                Dock = DockStyle.Fill,
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            tabProjMain.Controls.Add(lblCurrentProjectHeader, 0, 0);

            var currentScroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            var editMain = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                Padding = new Padding(6)
            };
            editMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            editMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            editMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            currentScroll.Controls.Add(editMain);
            tabProjMain.Controls.Add(currentScroll, 0, 1);

            // Project summary / address area
            var grpProjectInfo = new GroupBox
            {
                Text = "Project Information",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                MinimumSize = new Size(360, 250)
            };
            var tblProjInfo = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 8
            };
            tblProjInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            tblProjInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++) tblProjInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tblProjInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            tblProjInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            txtProjNum  = AddReadOnlyRow(tblProjInfo, 0, "Project Number:");
            txtProjName = AddReadOnlyRow(tblProjInfo, 1, "Project Name:");
            txtArch     = AddReadOnlyRow(tblProjInfo, 2, "Architect:");
            txtCont     = AddReadOnlyRow(tblProjInfo, 3, "Contractor:");
            txtPM       = AddReadOnlyRow(tblProjInfo, 4, "Project PM:");
            tblProjInfo.Controls.Add(new Label { Text = "Project Address:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 6, 4, 0) }, 0, 6);
            txtProjectAddress = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
            tblProjInfo.Controls.Add(txtProjectAddress, 1, 6);
            var certPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            chkFSC = new CheckBox { Text = "FSC", Width = 70 };
            chkLEED = new CheckBox { Text = "LEED", Width = 80 };
            certPanel.Controls.Add(chkFSC);
            certPanel.Controls.Add(chkLEED);
            tblProjInfo.Controls.Add(new Label { Text = "Certifications:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }, 0, 7);
            tblProjInfo.Controls.Add(certPanel, 1, 7);
            grpProjectInfo.Controls.Add(tblProjInfo);
            editMain.Controls.Add(grpProjectInfo, 0, 0);

            // Project dates / timeline area
            var grpProjectDates = new GroupBox
            {
                Text = "Project Dates",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                MinimumSize = new Size(360, 250)
            };
            var tblDates = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 6
            };
            tblDates.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            tblDates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tblDates.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            tblDates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 5; i++) tblDates.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tblDates.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            txtProjStartDate = AddEditableRow(tblDates, 0, 0, "Start Date:");
            txtProjEstProduction = AddEditableRow(tblDates, 1, 0, "Target Start Production:");
            txtProjEstInstall = AddEditableRow(tblDates, 2, 0, "Target Start Installation:");
            txtProjEstComplete = AddEditableRow(tblDates, 3, 0, "Target Completion Date:");
            txtProjDaysTotal = AddReadOnlyRow(tblDates, 0, 2, "Total Project Days:");
            txtProjDaysRemaining = AddReadOnlyRow(tblDates, 1, 2, "Days Remaining:");
            txtProjPercentUsed = AddReadOnlyRow(tblDates, 2, 2, "Percent Utilized:");
            tblDates.Controls.Add(new Label { Text = "Project Timeline:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 4);
            tblDates.SetColumnSpan(tblDates.GetControlFromPosition(0, 4), 4);
            pnlTimeline = new Panel { Dock = DockStyle.Fill, Height = 70, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            pnlTimeline.Paint += pnlTimeline_Paint;
            tblDates.Controls.Add(pnlTimeline, 0, 5);
            tblDates.SetColumnSpan(pnlTimeline, 4);
            grpProjectDates.Controls.Add(tblDates);
            editMain.Controls.Add(grpProjectDates, 1, 0);
            editMain.SetColumnSpan(grpProjectDates, 2);

            // Dash details
            var grpDashInfo = new GroupBox
            {
                Text = "Current Dash Data",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                MinimumSize = new Size(360, 300)
            };
            var dashInfo = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 9 };
            dashInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            dashInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            dashInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
            dashInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 9; i++) dashInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            lblDashHeader = new Label { Text = "Current Dash Data", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            dashInfo.Controls.Add(lblDashHeader, 0, 0);
            dashInfo.SetColumnSpan(lblDashHeader, 4);
            cboDashType = AddComboRow(dashInfo, 1, "Dash Type:");
            cboDashParent = AddComboRow(dashInfo, 2, "Dash Parent:");
            cboMfg = AddComboRow(dashInfo, 3, "Manufacturer:");
            cboDashStatus = AddComboRow(dashInfo, 4, "Dash Status:");
            cboDwgDraftsman = AddComboRow(dashInfo, 5, "CAD:");
            txtDashFloor = AddEditableRow(dashInfo, 6, 0, "Floor:");
            txtDashRoom = AddEditableRow(dashInfo, 6, 2, "Room #:");
            txtDashQty = AddEditableRow(dashInfo, 7, 0, "Qty:");
            cboDashUnit = AddComboRow(dashInfo, 7, "Units:", labelColumn: 2, valueColumn: 3);
            grpDashInfo.Controls.Add(dashInfo);
            editMain.Controls.Add(grpDashInfo, 0, 1);

            var grpSchedule = new GroupBox
            {
                Text = "Schedule Dates",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                MinimumSize = new Size(360, 300)
            };
            var sched = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 8 };
            sched.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            sched.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sched.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 8; i++) sched.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            sched.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 0);
            sched.Controls.Add(new Label { Text = "Target:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
            sched.Controls.Add(new Label { Text = "Actual:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 2, 0);
            txtDateTargetSubmit = AddScheduleRow(sched, 1, "Submittal:", out txtDateActualSubmit);
            txtDateTargetApprove = AddScheduleRow(sched, 2, "Approval:", out txtDateActualApprove);
            txtDateTargetFD = AddScheduleRow(sched, 3, "FD:", out txtDateActualFD);
            txtDateTargetRlsMfr = AddScheduleRow(sched, 4, "Rls MFR:", out txtDateActualRlsMfr);
            txtDateTargetFieldReady = AddScheduleRow(sched, 5, "Field Ready:", out txtDateActualFieldReady);
            txtDateTargetShip = AddScheduleRow(sched, 6, "Ship:", out txtDateActualShip);
            grpSchedule.Controls.Add(sched);
            editMain.Controls.Add(grpSchedule, 0, 2);

            var notesPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, MinimumSize = new Size(360, 300) };
            notesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            notesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            txtDashNotes = AddMemoBox(notesPanel, 0, "Dash Specific Notes:");
            txtShopNotes = AddMemoBox(notesPanel, 1, "Dash Shop Specific Notes:");
            editMain.Controls.Add(notesPanel, 1, 1);
            editMain.SetRowSpan(notesPanel, 2);

            txtProjNotes = AddMemoBox(editMain, 1, "Project Specific Notes:", column: 2, rowSpan: 2);

            // Offline status label
            lblOffline = new Label
            {
                Dock = DockStyle.Top, Height = 22, Visible = false,
                ForeColor = Color.DarkOrange, Font = new Font(Font, FontStyle.Bold)
            };
            editMain.Controls.Add(lblOffline, 0, 3);
            editMain.SetColumnSpan(lblOffline, 3);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4)
            };
            btnRefresh         = new Button { Text = "Refresh",        Width = 100, Height = 30 };
            btnChangeProject   = new Button { Text = "Change Project", Width = 120, Height = 30 };
            btnSaveProjectDash = new Button { Text = "Save Changes",   Width = 120, Height = 30 };
            btnRefresh.Click       += btnRefresh_Click;
            btnChangeProject.Click += btnChangeProject_Click;
            btnSaveProjectDash.Click += btnSaveProjectDash_Click;
            btnPanel.Controls.AddRange(new Control[] { btnRefresh, btnChangeProject, btnSaveProjectDash });
            tabProjMain.Controls.Add(btnPanel, 0, 2);
            tabProj.Controls.Add(tabProjMain);

            // --- Tab 2: Current BOM ---
            var bomMain = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(6)
            };
            bomMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            bomMain.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bomMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));

            lblBomContext = new Label
            {
                Text = "No active dash selected for this drawing.",
                Dock = DockStyle.Fill,
                Font = new Font(Font, FontStyle.Bold)
            };
            bomMain.Controls.Add(lblBomContext, 0, 0);

            dgvBomComponents = CreateBomGrid();
            AddGridColumn(dgvBomComponents, "DashNum", "Dash #", 12);
            AddGridColumn(dgvBomComponents, "Description", "Description", 34);
            AddGridColumn(dgvBomComponents, "Mfr", "Mfr", 12);
            AddGridColumn(dgvBomComponents, "TargetRelease", "Target Release", 14);
            AddGridColumn(dgvBomComponents, "ActualRelease", "Actual Release", 14);
            AddGridColumn(dgvBomComponents, "TargetShip", "Target Ship", 12);
            AddGridColumn(dgvBomComponents, "ActualShip", "Actual Ship", 12);

            dgvBomMaterials = CreateBomGrid();
            dgvBomMaterials.ReadOnly = false;
            AddGridColumn(dgvBomMaterials, "MatNo", "Material", 10, true);
            AddGridColumn(dgvBomMaterials, "MatGroup", "Group", 16, true);
            AddGridColumn(dgvBomMaterials, "MatDesc", "Material Description", 32, true);
            AddGridColumn(dgvBomMaterials, "MatUnits", "Units", 8, true);
            AddGridColumn(dgvBomMaterials, "MatQty", "Qty", 8, false);
            AddGridColumn(dgvBomMaterials, "MatNotes", "Notes", 20, true);
            AddGridColumn(dgvBomMaterials, "MatApprove", "Approve", 10, true);
            dgvBomMaterials.CellDoubleClick += dgvBomMaterials_CellDoubleClick;
            dgvBomMaterials.CellEndEdit += dgvBomMaterials_CellEndEdit;
            dgvBomMaterials.CellMouseDown += dgvBomMaterials_CellMouseDown;
            var bomMaterialsMenu = new ContextMenuStrip();
            bomMaterialsMenu.Items.Add("Remove Material Association", null, bomMaterials_RemoveAssociation_Click);
            dgvBomMaterials.ContextMenuStrip = bomMaterialsMenu;

            dgvBomHardware = CreateBomGrid();
            dgvBomHardware.ReadOnly = false;
            AddGridColumn(dgvBomHardware, "HdwNo", "Hdw", 10, true);
            AddGridColumn(dgvBomHardware, "HdwGroup", "Group", 16, true);
            AddGridColumn(dgvBomHardware, "HdwDesc", "Hardware Description", 32, true);
            AddGridColumn(dgvBomHardware, "HdwQty", "Qty", 8, false);
            AddGridColumn(dgvBomHardware, "HdwUnits", "Units", 8, true);
            AddGridColumn(dgvBomHardware, "HdwNotes", "Notes", 20, true);
            AddGridColumn(dgvBomHardware, "HdwApprove", "Approve", 10, true);
            dgvBomHardware.CellDoubleClick += dgvBomHardware_CellDoubleClick;
            dgvBomHardware.CellEndEdit += dgvBomHardware_CellEndEdit;
            dgvBomHardware.CellMouseDown += dgvBomHardware_CellMouseDown;
            var bomHardwareMenu = new ContextMenuStrip();
            bomHardwareMenu.Items.Add("Remove Hardware Association", null, bomHardware_RemoveAssociation_Click);
            dgvBomHardware.ContextMenuStrip = bomHardwareMenu;

            dgvBomMetal = CreateBomGrid();
            dgvBomMetal.ReadOnly = false;
            dgvBomMetal.AllowUserToAddRows = true;
            dgvBomMetal.AllowUserToDeleteRows = true;
            AddGridColumn(dgvBomMetal, "Mtl_PrtNo", "Part #", 8, false);
            AddGridColumn(dgvBomMetal, "Mtl_PrtDesc", "Part Description", 22, false);
            AddGridColumn(dgvBomMetal, "MatID", "MatID", 1, true);
            dgvBomMetal.Columns["MatID"].Visible = false;
            AddComboGridColumn(dgvBomMetal, "Mtl_Finish", "Finish", 14, false);
            AddComboGridColumn(dgvBomMetal, "Mtl_Material", "Metal Material", 16, false);
            AddGridColumn(dgvBomMetal, "Mtl_Length", "Length", 8, false);
            AddGridColumn(dgvBomMetal, "Mtl_Width", "Width", 8, false);
            AddGridColumn(dgvBomMetal, "Mtl_Height", "Height", 8, false);
            AddGridColumn(dgvBomMetal, "Mtl_Thk", "Thickness", 8, false);
            AddGridColumn(dgvBomMetal, "Mtl_Qty", "Qty", 7, false);
            AddGridColumn(dgvBomMetal, "Mtl_QtyUnits", "Units", 8, false);
            AddGridColumn(dgvBomMetal, "Mtl_Volume", "Volume", 8, true);
            AddGridColumn(dgvBomMetal, "Mtl_Weight", "Weight", 8, false);
            AddGridColumn(dgvBomMetal, "Mtl_Notes", "Notes", 18, false);
            AddGridColumn(dgvBomMetal, "Mtl_ShtReference", "Sheet Ref", 10, false);
            AddGridColumn(dgvBomMetal, "Date_ActualRls", "Actual Release", 10, false);
            AddGridColumn(dgvBomMetal, "Date_ActualShip", "Actual Ship", 10, false);
            dgvBomMetal.CellValueChanged += dgvBomMetal_CellValueChanged;
            dgvBomMetal.CurrentCellDirtyStateChanged += dgvBomMetal_CurrentCellDirtyStateChanged;
            dgvBomMetal.CellValidating += dgvBomMetal_CellValidating;
            dgvBomMetal.EditingControlShowing += dgvBomMetal_EditingControlShowing;
            dgvBomMetal.CellMouseDown += dgvBomMetal_CellMouseDown;
            dgvBomMetal.RowValidated += dgvBomMetal_RowValidated;
            dgvBomMetal.DataError += dgvBomMetal_DataError;
            dgvBomMetal.UserDeletingRow += dgvBomMetal_UserDeletingRow;

            var sectionComponents = new CollapsibleBomSection("Child Component Dashes", dgvBomComponents);
            var sectionMaterials = new CollapsibleBomSection("Associated Materials", dgvBomMaterials);
            var sectionHardware = new CollapsibleBomSection("Associated Hardware", dgvBomHardware);
            var sectionMetal = new CollapsibleBomSection("Metal Part List", dgvBomMetal);

            // Keep all four BOM sections in one ordered stack.  This prevents the last section
            // (Metal Part List) from snapping to the bottom when collapsed and lets it behave
            // the same as the component, material, and hardware sections.
            var bomSectionsTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            bomSectionsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++)
                bomSectionsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

            bomSectionsTable.Controls.Add(sectionComponents, 0, 0);
            bomSectionsTable.Controls.Add(sectionMaterials, 0, 1);
            bomSectionsTable.Controls.Add(sectionHardware, 0, 2);
            bomSectionsTable.Controls.Add(sectionMetal, 0, 3);

            WireBomSectionCollapse(sectionComponents, bomSectionsTable, 0);
            WireBomSectionCollapse(sectionMaterials, bomSectionsTable, 1);
            WireBomSectionCollapse(sectionHardware, bomSectionsTable, 2);
            WireBomSectionCollapse(sectionMetal, bomSectionsTable, 3);
            UpdateBomSectionRowStyles(bomSectionsTable);

            bomMain.Controls.Add(bomSectionsTable, 0, 1);

            var bomButtonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 5, 4, 4)
            };
            btnBomRefresh = new Button { Text = "Refresh", Width = 90, Height = 28 };
            btnBomExportPdf = new Button { Text = "Export PDF", Width = 100, Height = 28 };
            btnBomExportCsv = new Button { Text = "Export CSV", Width = 100, Height = 28 };
            btnBomAddMaterial = new Button { Text = "Add Material", Width = 105, Height = 28 };
            btnBomAddHardware = new Button { Text = "Add Hardware", Width = 110, Height = 28 };
            btnBomInsertHdwTable = new Button { Text = "Insert Hdw Table", Width = 125, Height = 28 };
            btnBomInsertMatTable = new Button { Text = "Insert Material Table", Width = 125, Height = 28 };
            btnBomInsertMetalTable = new Button { Text = "Insert Metal Table", Width = 130, Height = 28 };
            btnBomInsertCompList = new Button { Text = "Insert Comp List", Width = 130, Height = 28 };
            btnBomRefresh.Click += btnBomRefresh_Click;
            btnBomExportPdf.Click += btnBomExportPdf_Click;
            btnBomExportCsv.Click += btnBomExportCsv_Click;
            btnBomAddMaterial.Click += btnBomAddMaterial_Click;
            btnBomAddHardware.Click += btnBomAddHardware_Click;
            btnBomInsertHdwTable.Click += btnBomInsertHdwTable_Click;
            btnBomInsertMatTable.Click += btnBomInsertMatTable_Click;
            btnBomInsertMetalTable.Click += btnBomInsertMetalTable_Click;
            btnBomInsertCompList.Click += btnBomInsertCompList_Click;
            bomButtonRow.Controls.AddRange(new Control[]
            {
                btnBomRefresh,
                btnBomExportPdf,
                btnBomExportCsv,
                btnBomInsertMetalTable,
                btnBomInsertCompList,
                btnBomInsertMatTable,
                btnBomInsertHdwTable,
                btnBomAddHardware,
                btnBomAddMaterial
            });
            bomMain.Controls.Add(bomButtonRow, 0, 2);
            tabBom.Controls.Add(bomMain);


            // --- Tab 3: Drawing Series ---
            var drawingSeriesMain = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(8)
            };
            drawingSeriesMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            drawingSeriesMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            drawingSeriesMain.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            drawingSeriesMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            drawingSeriesMain.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            drawingSeriesMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            lblDrawingSeriesContext = new Label
            {
                Dock = DockStyle.Fill,
                Text = "No active dash selected for this drawing.",
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            drawingSeriesMain.Controls.Add(lblDrawingSeriesContext, 0, 0);

            lblDrawingSeriesFilesTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Series Files",
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            drawingSeriesMain.Controls.Add(lblDrawingSeriesFilesTitle, 0, 1);

            dgvDrawingSeriesFiles = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false
            };
            dgvDrawingSeriesFiles.Columns.Add("DashNum", "Dash");
            dgvDrawingSeriesFiles.Columns.Add("DashDesc", "Dash Description");
            dgvDrawingSeriesFiles.Columns.Add("FileName", "File Name");
            dgvDrawingSeriesFiles.Columns.Add("SavedPath", "Saved Path");
            dgvDrawingSeriesFiles.Columns.Add("SheetCount", "Sheets");
            dgvDrawingSeriesFiles.Columns["DashNum"].FillWeight = 14;
            dgvDrawingSeriesFiles.Columns["DashDesc"].FillWeight = 24;
            dgvDrawingSeriesFiles.Columns["FileName"].FillWeight = 26;
            dgvDrawingSeriesFiles.Columns["SavedPath"].FillWeight = 30;
            dgvDrawingSeriesFiles.Columns["SheetCount"].FillWeight = 8;
            dgvDrawingSeriesFiles.CellDoubleClick += dgvDrawingSeriesFiles_CellDoubleClick;
            dgvDrawingSeriesFiles.CellMouseDown += dgvDrawingSeriesFiles_CellMouseDown;
            var drawingSeriesFileMenu = new ContextMenuStrip();
            drawingSeriesFileMenu.Items.Add("Open File", null, drawingSeriesFiles_OpenFile_Click);
            drawingSeriesFileMenu.Items.Add("Open File Location", null, drawingSeriesFiles_OpenLocation_Click);
            drawingSeriesFileMenu.Items.Add(new ToolStripSeparator());
            drawingSeriesFileMenu.Items.Add("Delete File Entry", null, drawingSeriesFiles_DeleteFileEntry_Click);
            dgvDrawingSeriesFiles.ContextMenuStrip = drawingSeriesFileMenu;
            drawingSeriesMain.Controls.Add(dgvDrawingSeriesFiles, 0, 2);

            lblDrawingSeriesSheetsTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Series Sheets",
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            drawingSeriesMain.Controls.Add(lblDrawingSeriesSheetsTitle, 0, 3);

            tvDrawingSeriesSheets = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true
            };
            tvDrawingSeriesSheets.NodeMouseDoubleClick += tvDrawingSeriesSheets_NodeMouseDoubleClick;
            tvDrawingSeriesSheets.NodeMouseClick += tvDrawingSeriesSheets_NodeMouseClick;
            var drawingSeriesSheetMenu = new ContextMenuStrip();
            drawingSeriesSheetMenu.Items.Add("Edit Sheet Number / Subject", null, drawingSeriesSheets_EditSheet_Click);
            drawingSeriesSheetMenu.Items.Add("Jump to Layout", null, drawingSeriesSheets_JumpToLayout_Click);
            drawingSeriesSheetMenu.Items.Add(new ToolStripSeparator());
            drawingSeriesSheetMenu.Items.Add("Delete Sheet Entry", null, drawingSeriesSheets_DeleteSheetEntry_Click);
            tvDrawingSeriesSheets.ContextMenuStrip = drawingSeriesSheetMenu;
            drawingSeriesMain.Controls.Add(tvDrawingSeriesSheets, 0, 4);

            var drawingSeriesButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 5, 4, 4)
            };
            btnDrawingSeriesRefresh = new Button { Text = "Refresh View", Width = 105, Height = 28 };
            btnDrawingSeriesRefresh.Click += btnDrawingSeriesRefresh_Click;
            btnDrawingSeriesRefreshDatabase = new Button { Text = "Refresh Database", Width = 130, Height = 28 };
            btnDrawingSeriesRefreshDatabase.Click += btnDrawingSeriesRefreshDatabase_Click;
            btnDrawingSeriesAssociateCurrent = new Button { Text = "Add Current File to Dash", Width = 170, Height = 28 };
            btnDrawingSeriesAssociateCurrent.Click += btnDrawingSeriesAssociateCurrent_Click;
            drawingSeriesButtons.Controls.Add(btnDrawingSeriesRefresh);
            drawingSeriesButtons.Controls.Add(btnDrawingSeriesRefreshDatabase);
            drawingSeriesButtons.Controls.Add(btnDrawingSeriesAssociateCurrent);
            drawingSeriesMain.Controls.Add(drawingSeriesButtons, 0, 5);

            tabDrawingSeries.Controls.Add(drawingSeriesMain);

            // --- Tab 4: File Properties — inner TabControl ---
            var fileTabCtl = new TabControl { Dock = DockStyle.Fill };

            // ── Sub-tab A: Custom Properties ──────────────────────────────
            var subCustom = new TabPage("Custom Properties");
            dgvCustomProps = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = false, Enabled = true, TabStop = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false, AllowUserToAddRows = true,
                AllowUserToDeleteRows = false, RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BorderStyle = BorderStyle.None,
            };
            dgvCustomProps.Columns.Add("PropName",  "Property");
            dgvCustomProps.Columns.Add("PropValue", "Value");
            dgvCustomProps.Columns["PropName"].FillWeight  = 40;
            dgvCustomProps.Columns["PropValue"].FillWeight = 60;
            dgvCustomProps.CellValueChanged += MarkFilePropsDirty;
            dgvCustomProps.CellEndEdit      += MarkFilePropsDirty;
            dgvCustomProps.RowsAdded        += MarkFilePropsDirty;
            dgvCustomProps.MouseDown       += EditableFilePropControl_MouseDown;
            dgvCustomProps.Enter           += EditableFilePropControl_Enter;

            subCustom.Controls.Add(dgvCustomProps);

            // ── Sub-tab B: Summary ────────────────────────────────────────
            var subSummary = new TabPage("Summary");
            var tblSumm = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
                Padding = new Padding(6, 6, 6, 4)
            };
            tblSumm.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tblSumm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++)
                tblSumm.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tblSumm.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Comments

            txtSummTitle      = AddFilePropRow(tblSumm, 0, "Title:", readOnly: false);
            txtSummSubject    = AddFilePropRow(tblSumm, 1, "Subject:", readOnly: false);
            txtSummAuthor     = AddFilePropRow(tblSumm, 2, "Author:", readOnly: false);
            txtSummKeywords   = AddFilePropRow(tblSumm, 3, "Keywords:", readOnly: false);
            txtSummHyperlink  = AddFilePropRow(tblSumm, 4, "Hyperlink:", readOnly: false);
            txtSummRevision   = AddFilePropRow(tblSumm, 5, "Revision:", readOnly: false);
            txtSummComments   = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = false,
                Enabled = true, TabStop = true,
                BackColor = System.Drawing.SystemColors.Window, ScrollBars = ScrollBars.Vertical,
                Cursor = Cursors.IBeam
            };
            txtSummComments.MouseDown += EditableFilePropControl_MouseDown;
            txtSummComments.Enter     += EditableFilePropControl_Enter;
            tblSumm.Controls.Add(new Label { Text = "Comments:", AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 4, 4, 0) }, 0, 6);
            tblSumm.Controls.Add(txtSummComments, 1, 6);

            txtSummTitle.TextChanged     += MarkFilePropsDirty;
            txtSummSubject.TextChanged   += MarkFilePropsDirty;
            txtSummAuthor.TextChanged    += MarkFilePropsDirty;
            txtSummKeywords.TextChanged  += MarkFilePropsDirty;
            txtSummHyperlink.TextChanged += MarkFilePropsDirty;
            txtSummRevision.TextChanged  += MarkFilePropsDirty;
            txtSummComments.TextChanged  += MarkFilePropsDirty;

            subSummary.Controls.Add(tblSumm);

            // ── Sub-tab C: File Info ──────────────────────────────────────
            var subInfo = new TabPage("File Info");
            var tblInfo = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
                Padding = new Padding(6)
            };
            tblInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            tblInfo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++)
                tblInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            txtInfoFile       = AddFilePropRow(tblInfo, 0, "File:");
            txtInfoLocation   = AddFilePropRow(tblInfo, 1, "Location:");
            txtInfoSize       = AddFilePropRow(tblInfo, 2, "Size:");
            txtInfoCreated    = AddFilePropRow(tblInfo, 3, "Created:");
            txtInfoModified   = AddFilePropRow(tblInfo, 4, "Modified:");
            txtInfoAccessed   = AddFilePropRow(tblInfo, 5, "Accessed:");
            txtInfoLastSaved  = AddFilePropRow(tblInfo, 6, "Last Saved:");
            subInfo.Controls.Add(tblInfo);

            fileTabCtl.TabPages.Add(subCustom);
            fileTabCtl.TabPages.Add(subSummary);
            fileTabCtl.TabPages.Add(subInfo);

            var fileBtnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 5, 4, 4)
            };
            btnSaveFileProps = new Button { Text = "Save Changes", Width = 120, Height = 28, Visible = false, Enabled = false };
            btnSaveFileProps.Click += btnSaveFileProps_Click;
            fileBtnRow.Controls.Add(btnSaveFileProps);

            tabFile.Controls.Add(fileTabCtl);
            tabFile.Controls.Add(fileBtnRow);

            tabControl.Controls.AddRange(new Control[] { tabProj, tabBom, tabDrawingSeries, tabFile });
            Controls.Add(tabControl);
            Size = new Size(420, 440);
            Name = "CtlIWCProj";
        }


        private void ConfigureTopTabs()
        {
            tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl.ItemSize = new Size(128, 26);
            tabControl.SizeMode = TabSizeMode.Fixed;
            tabControl.DrawItem += tabControl_DrawItem;
        }

        private void tabControl_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not TabControl tc || e.Index < 0 || e.Index >= tc.TabPages.Count)
                return;

            var page = tc.TabPages[e.Index];
            bool selected = e.Index == tc.SelectedIndex;
            var bounds = e.Bounds;
            using var backBrush = new SolidBrush(selected ? SystemColors.Window : SystemColors.Control);
            e.Graphics.FillRectangle(backBrush, bounds);
            using var tabFont = new Font(tc.Font, FontStyle.Bold);
            TextRenderer.DrawText(
                e.Graphics,
                page.Text,
                tabFont,
                bounds,
                SystemColors.ControlText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        private static TextBox AddReadOnlyRow(TableLayoutPanel tbl, int row, string label)
            => AddReadOnlyRow(tbl, row, 0, label);

        private static TextBox AddReadOnlyRow(TableLayoutPanel tbl, int row, int labelColumn, string label)
        {
            int valueColumn = labelColumn + 1;
            tbl.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 4, 0)
            }, labelColumn, row);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = SystemColors.Window
            };
            tbl.Controls.Add(tb, valueColumn, row);
            return tb;
        }

        private static TextBox AddEditableRow(TableLayoutPanel tbl, int row, int labelColumn, string label)
        {
            int valueColumn = labelColumn + 1;
            tbl.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 4, 0)
            }, labelColumn, row);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Window
            };
            tbl.Controls.Add(tb, valueColumn, row);
            return tb;
        }

        private static ComboBox AddComboRow(TableLayoutPanel tbl, int row, string label, int labelColumn = 0, int valueColumn = 1)
        {
            tbl.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 4, 0)
            }, labelColumn, row);
            var combo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            tbl.Controls.Add(combo, valueColumn, row);
            return combo;
        }

        private static DateTimePicker AddScheduleRow(TableLayoutPanel tbl, int row, string label, out DateTimePicker actual)
        {
            tbl.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 4, 0)
            }, 0, row);

            // ShowCheckBox gives an unchecked/grayed picker representing "no date set"
            // (matches the Approved-checkbox + date picker pattern in the Material editor).
            var target = new DateTimePicker
            {
                Dock = DockStyle.Fill,
                Format = DateTimePickerFormat.Short,
                ShowCheckBox = true,
                Checked = false
            };
            actual = new DateTimePicker
            {
                Dock = DockStyle.Fill,
                Format = DateTimePickerFormat.Short,
                ShowCheckBox = true,
                Checked = false
            };
            tbl.Controls.Add(target, 1, row);
            tbl.Controls.Add(actual, 2, row);
            return target;
        }

        private static TextBox AddMemoBox(TableLayoutPanel tbl, int row, string label, int column = 0, int rowSpan = 1)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 2, 4, 4)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window
            };
            panel.Controls.Add(tb, 0, 1);
            tbl.Controls.Add(panel, column, row);
            if (rowSpan > 1) tbl.SetRowSpan(panel, rowSpan);
            return tb;
        }

        private static DataGridView CreateBomGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private static void AddGridColumn(DataGridView grid, string name, string header, float fillWeight, bool readOnly = true)
        {
            grid.Columns.Add(name, header);
            grid.Columns[name].FillWeight = fillWeight;
            grid.Columns[name].ReadOnly = readOnly;
        }

        private static void AddComboGridColumn(DataGridView grid, string name, string header, float fillWeight, bool readOnly = true)
        {
            var column = new DataGridViewComboBoxColumn
            {
                Name = name,
                HeaderText = header,
                FillWeight = fillWeight,
                ReadOnly = readOnly,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
            };
            grid.Columns.Add(column);
        }

        private static Control CreateLabeledPanel(string caption, Control child)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 2, 0, 4)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(new Label
            {
                Text = caption,
                Dock = DockStyle.Fill,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            panel.Controls.Add(child, 0, 1);
            return panel;
        }

        private static SplitContainer CreateBomSplitContainer()
        {
            return new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.None,
                Panel1MinSize = CollapsibleBomSection.HeaderHeight + 2,
                Panel2MinSize = CollapsibleBomSection.HeaderHeight + 2,
                BorderStyle = BorderStyle.None
            };
        }

        private static void InitializeBomSplitterDistanceOnce(SplitContainer split, int preferredDistance)
        {
            if (split.Tag is string tag && tag == "initialized") return;
            if (split.Height <= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth) return;

            var max = split.Height - split.Panel2MinSize - split.SplitterWidth;
            split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(preferredDistance, max));
            split.Tag = "initialized";
        }

        private static void WireBomSectionCollapse(CollapsibleBomSection section, TableLayoutPanel table, int rowIndex)
        {
            section.CollapsedChanged += (_, __) => UpdateBomSectionRowStyles(table);
        }

        private static void UpdateBomSectionRowStyles(TableLayoutPanel table)
        {
            if (table.RowStyles.Count == 0) return;

            int expandedCount = 0;
            for (int i = 0; i < table.RowCount; i++)
            {
                if (table.GetControlFromPosition(0, i) is CollapsibleBomSection section && !section.Collapsed)
                    expandedCount++;
            }

            float expandedPercent = expandedCount > 0 ? 100f / expandedCount : 100f;

            table.SuspendLayout();
            for (int i = 0; i < table.RowCount; i++)
            {
                if (table.GetControlFromPosition(0, i) is CollapsibleBomSection section && section.Collapsed)
                    table.RowStyles[i] = new RowStyle(SizeType.Absolute, CollapsibleBomSection.HeaderHeight + 6);
                else
                    table.RowStyles[i] = new RowStyle(SizeType.Percent, expandedPercent);
            }
            table.ResumeLayout(true);
        }

        private sealed class CollapsibleBomSection : UserControl
        {
            public const int HeaderHeight = 24;

            private readonly Label _header;
            private readonly Control _content;
            private bool _collapsed;

            public event EventHandler? CollapsedChanged;

            public bool Collapsed
            {
                get => _collapsed;
                set
                {
                    if (_collapsed == value) return;
                    _collapsed = value;
                    _content.Visible = !_collapsed;
                    UpdateHeaderText();
                    CollapsedChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public CollapsibleBomSection(string caption, Control content)
            {
                Dock = DockStyle.Fill;
                Margin = new Padding(0, 2, 0, 4);
                MinimumSize = new Size(0, HeaderHeight + 2);

                _content = content;
                _content.Dock = DockStyle.Fill;

                _header = new Label
                {
                    Dock = DockStyle.Top,
                    Height = HeaderHeight,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                    BackColor = SystemColors.ControlLight,
                    BorderStyle = BorderStyle.FixedSingle,
                    Padding = new Padding(4, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Tag = caption
                };
                _header.Click += (_, __) => Collapsed = !Collapsed;

                Controls.Add(_content);
                Controls.Add(_header);
                UpdateHeaderText();
            }

            private void UpdateHeaderText()
            {
                var caption = Convert.ToString(_header.Tag) ?? string.Empty;
                _header.Text = $"{(Collapsed ? "▶" : "▼")} {caption}";
            }
        }

        private void ConfigureLargeNoteTextBoxes()
        {
            ConfigureMultilineTextBox(txtDashNotes);
            ConfigureMultilineTextBox(txtProjNotes);
            ConfigureMultilineTextBox(txtShopNotes);
        }

        private static void ConfigureMultilineTextBox(TextBox tb)
        {
            if (tb == null) return;

            tb.Multiline = true;
            tb.AcceptsReturn = true;
            tb.AcceptsTab = true;
            tb.WordWrap = true;
            tb.ScrollBars = ScrollBars.Vertical;
        }

        private static TextBox AddRow(TableLayoutPanel tbl, int row, string label)
        {
            tbl.Controls.Add(new Label
            {
                Text = label, AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            }, 0, row);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true,
                BackColor = System.Drawing.SystemColors.Window
            };
            tbl.Controls.Add(tb, 1, row);
            return tb;
        }

        // Controls — Tab 1 (Current Project)
        private TabControl      tabControl       = null!;
        private Label           lblOffline       = null!;
        private TextBox         txtProjNum       = null!;
        private TextBox         txtProjName      = null!;
        private TextBox         txtArch          = null!;
        private TextBox         txtCont          = null!;
        private TextBox         txtPM            = null!;
        private Button          btnRefresh       = null!;
        private Button          btnChangeProject = null!;
        private Button          btnSaveProjectDash = null!;
        private Label           lblCurrentProjectHeader = null!;
        private Label           lblDashHeader    = null!;
        private TextBox         txtProjectAddress = null!;
        private TextBox         txtProjStartDate = null!;
        private TextBox         txtProjEstProduction = null!;
        private TextBox         txtProjEstInstall = null!;
        private TextBox         txtProjEstComplete = null!;
        private TextBox         txtProjDaysTotal = null!;
        private TextBox         txtProjDaysRemaining = null!;
        private TextBox         txtProjPercentUsed = null!;
        private CheckBox        chkFSC = null!;
        private CheckBox        chkLEED = null!;
        private Panel           pnlTimeline = null!;
        private ComboBox        cboDashType = null!;
        private ComboBox        cboDashParent = null!;
        private ComboBox        cboMfg = null!;
        private ComboBox        cboDashStatus = null!;
        private ComboBox        cboDwgDraftsman = null!;
        private TextBox         txtDashFloor = null!;
        private TextBox         txtDashRoom = null!;
        private TextBox         txtDashQty = null!;
        private ComboBox        cboDashUnit = null!;
        private DateTimePicker  txtDateTargetSubmit = null!;
        private DateTimePicker  txtDateActualSubmit = null!;
        private DateTimePicker  txtDateTargetApprove = null!;
        private DateTimePicker  txtDateActualApprove = null!;
        private DateTimePicker  txtDateTargetFD = null!;
        private DateTimePicker  txtDateActualFD = null!;
        private DateTimePicker  txtDateTargetRlsMfr = null!;
        private DateTimePicker  txtDateActualRlsMfr = null!;
        private DateTimePicker  txtDateTargetFieldReady = null!;
        private DateTimePicker  txtDateActualFieldReady = null!;
        private DateTimePicker  txtDateTargetShip = null!;
        private DateTimePicker  txtDateActualShip = null!;
        private TextBox         txtDashNotes = null!;
        private TextBox         txtShopNotes = null!;
        private TextBox         txtProjNotes = null!;


        // Controls — Tab 2 (Current BOM)
        private Label           lblBomContext      = null!;
        private DataGridView    dgvBomComponents   = null!;
        private DataGridView    dgvBomMaterials    = null!;
        private DataGridView    dgvBomHardware     = null!;
        private DataGridView    dgvBomMetal        = null!;
        private Button          btnBomRefresh      = null!;
        private Button          btnBomExportPdf    = null!;
        private Button          btnBomExportCsv    = null!;
        private Button          btnBomAddMaterial  = null!;
        private Button          btnBomAddHardware  = null!;
        private Button          btnBomInsertHdwTable = null!;
        private Button          btnBomInsertMatTable = null!;
        private Button          btnBomInsertMetalTable = null!;
        private Button          btnBomInsertCompList = null!;

        // Controls — Tab 3 (Drawing Series)
        private Label           lblDrawingSeriesContext = null!;
        private Label           lblDrawingSeriesFilesTitle = null!;
        private Label           lblDrawingSeriesSheetsTitle = null!;
        private DataGridView    dgvDrawingSeriesFiles = null!;
        private TreeView        tvDrawingSeriesSheets = null!;
        private Button          btnDrawingSeriesAssociateCurrent = null!;
        private Button          btnDrawingSeriesRefresh = null!;
        private Button          btnDrawingSeriesRefreshDatabase = null!;

        // Controls — Tab 4 / File Properties tab
        private DataGridView    dgvCustomProps   = null!;
        private Button          btnSaveFileProps  = null!;

        // Controls — Tab 4 / Summary sub-tab
        private TextBox         txtSummTitle     = null!;
        private TextBox         txtSummSubject   = null!;
        private TextBox         txtSummAuthor    = null!;
        private TextBox         txtSummKeywords  = null!;
        private TextBox         txtSummHyperlink = null!;
        private TextBox         txtSummRevision  = null!;
        private TextBox         txtSummComments  = null!;

        // Controls — Tab 4 / File Info sub-tab
        private TextBox         txtInfoFile      = null!;
        private TextBox         txtInfoLocation  = null!;
        private TextBox         txtInfoSize      = null!;
        private TextBox         txtInfoCreated   = null!;
        private TextBox         txtInfoModified  = null!;
        private TextBox         txtInfoAccessed  = null!;
        private TextBox         txtInfoLastSaved = null!;
    }
}
