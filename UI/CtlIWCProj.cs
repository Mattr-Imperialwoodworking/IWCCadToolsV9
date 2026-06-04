using System;
using System.Data;
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Core;
using IWCCadToolsV9.Data.Models;
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
            tabControl.Dock = DockStyle.Fill;
            Dock            = DockStyle.Fill;

            // Subscribe to document activation so the palette updates
            // when the user switches between open drawings
            Application.DocumentManager.DocumentActivated += OnDocumentActivated;

            RefreshFromContext();
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
            // Run on the UI thread — DocumentActivated is already main-thread safe
            if (e.Document == null) return;
            var svc = ProjectContextService.GetOrCreate(e.Document);
            SubscribeToContext(svc);
            BindToService(svc);
        }

        private void OnProjectLoaded(object? sender, EventArgs e)
        {
            // ProjectLoaded may fire from a background Task — marshal to UI thread
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

            // Tab 3 — DWG File Properties — always re-read from the DWG after
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
            if (btnBomExportPdf != null) btnBomExportPdf.Enabled = hasContext;

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
            => LoadCurrentBom(_currentSvc?.Project?.Id ?? 0, _currentSvc?.Dash?.DashId ?? 0);

        private void btnBomExportPdf_Click(object? sender, EventArgs e)
            => ExportCurrentBomPdf();

        private void btnBomAddMaterial_Click(object? sender, EventArgs e)
        {
            int projectId = _currentSvc?.Project?.Id ?? 0;
            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (projectId <= 0 || dashId <= 0) return;

            var item = PickProjectItem(projectId, "Material", @"
                SELECT ID, MatNo AS ItemNo, MatDesc AS ItemDesc
                FROM dbo.Proj_Mat
                WHERE Proj_ID = @projId AND (MatVoid = 0 OR MatVoid IS NULL)
                ORDER BY MatNo;");
            if (item == null) return;

            bool cancelled;
            long? qty = PromptForBomQty(item.Value.No, item.Value.Description, _currentSvc?.Dash?.DashNum ?? dashId.ToString(), out cancelled);
            if (cancelled) return;
            if (qty == null && !ConfirmedNullQty("material")) return;

            AssociateMaterialToDash(item.Value.Id, dashId, qty);
            LoadCurrentBom(projectId, dashId);
        }

        private void btnBomAddHardware_Click(object? sender, EventArgs e)
        {
            int projectId = _currentSvc?.Project?.Id ?? 0;
            int dashId = _currentSvc?.Dash?.DashId ?? 0;
            if (projectId <= 0 || dashId <= 0) return;

            var item = PickProjectItem(projectId, "Hardware", @"
                SELECT ID, HdwNo AS ItemNo, HdwDesc AS ItemDesc
                FROM dbo.Proj_Hdw
                WHERE Proj_ID = @projId AND (HdwVoid = 0 OR HdwVoid IS NULL)
                ORDER BY HdwNo;");
            if (item == null) return;

            bool cancelled;
            long? qty = PromptForBomQty(item.Value.No, item.Value.Description, _currentSvc?.Dash?.DashNum ?? dashId.ToString(), out cancelled);
            if (cancelled) return;
            if (qty == null && !ConfirmedNullQty("hardware")) return;

            AssociateHardwareToDash(item.Value.Id, dashId, qty);
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

        private static (int Id, string No, string Description)? PickProjectItem(int projectId, string itemType, string sql)
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
                MultiSelect = false,
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
            frm.Controls.Add(buttons);
            frm.AcceptButton = ok;
            frm.CancelButton = cancel;

            grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) frm.DialogResult = DialogResult.OK; };

            if (frm.ShowDialog() != DialogResult.OK || grid.CurrentRow == null)
                return null;

            return ((int)(grid.CurrentRow.Tag ?? 0),
                    grid.CurrentRow.Cells["ItemNo"].Value?.ToString() ?? string.Empty,
                    grid.CurrentRow.Cells["ItemDesc"].Value?.ToString() ?? string.Empty);
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

        // -----------------------------------------------------------------------
        // Designer-generated members (unchanged from original)
        // -----------------------------------------------------------------------

        private void InitializeComponent()
        {
            tabControl   = new TabControl();
            var tabProj  = new TabPage("Current Project");
            var tabBom   = new TabPage("Current BOM");
            var tabFile  = new TabPage("File Properties");

            // --- Tab 1: Current Project ---
            var grpProj = new GroupBox
            {
                Text = "Current Project Data", Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
                Padding = new Padding(4)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            txtProjNum  = AddRow(tbl, 0, "Project Number:");
            txtProjName = AddRow(tbl, 1, "Project Name:");
            txtArch     = AddRow(tbl, 2, "Architect:");
            txtCont     = AddRow(tbl, 3, "Contractor:");
            txtPM       = AddRow(tbl, 4, "Project PM:");

            // Offline status label
            lblOffline = new Label
            {
                Dock = DockStyle.Top, Height = 22, Visible = false,
                ForeColor = Color.DarkOrange, Font = new Font(Font, FontStyle.Bold)
            };
            tbl.Controls.Add(lblOffline, 0, 5);
            tbl.SetColumnSpan(lblOffline, 2);

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, Padding = new Padding(4)
            };
            btnRefresh         = new Button { Text = "Refresh",        Width = 100, Height = 30 };
            btnChangeProject   = new Button { Text = "Change Project",  Width = 120, Height = 30 };
            btnRefresh.Click       += btnRefresh_Click;
            btnChangeProject.Click += btnChangeProject_Click;
            btnPanel.Controls.AddRange(new Control[] { btnRefresh, btnChangeProject });

            grpProj.Controls.Add(tbl);
            grpProj.Controls.Add(btnPanel);
            tabProj.Controls.Add(grpProj);

            // --- Tab 2: Current BOM ---
            var bomMain = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(6)
            };
            bomMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            bomMain.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            bomMain.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
            bomMain.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
            bomMain.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
            bomMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

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
            bomMain.Controls.Add(CreateLabeledPanel("Child Component Dashes", dgvBomComponents), 0, 1);

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
            bomMain.Controls.Add(CreateLabeledPanel("Associated Materials", dgvBomMaterials), 0, 2);

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
            bomMain.Controls.Add(CreateLabeledPanel("Associated Hardware", dgvBomHardware), 0, 3);

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
            bomMain.Controls.Add(CreateLabeledPanel("Metal Part List", dgvBomMetal), 0, 4);

            var bomButtonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(4, 5, 4, 4)
            };
            btnBomRefresh = new Button { Text = "Refresh", Width = 90, Height = 28 };
            btnBomExportPdf = new Button { Text = "Export PDF", Width = 100, Height = 28 };
            btnBomAddMaterial = new Button { Text = "Add Material", Width = 105, Height = 28 };
            btnBomAddHardware = new Button { Text = "Add Hardware", Width = 110, Height = 28 };
            btnBomRefresh.Click += btnBomRefresh_Click;
            btnBomExportPdf.Click += btnBomExportPdf_Click;
            btnBomAddMaterial.Click += btnBomAddMaterial_Click;
            btnBomAddHardware.Click += btnBomAddHardware_Click;
            bomButtonRow.Controls.AddRange(new Control[] { btnBomRefresh, btnBomExportPdf, btnBomAddHardware, btnBomAddMaterial });
            bomMain.Controls.Add(bomButtonRow, 0, 5);
            tabBom.Controls.Add(bomMain);

            // --- Tab 2: File Properties — inner TabControl ---
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

            tabControl.Controls.AddRange(new Control[] { tabProj, tabBom, tabFile });
            Controls.Add(tabControl);
            Size = new Size(420, 440);
            Name = "CtlIWCProj";
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


        // Controls — Tab 2 (Current BOM)
        private Label           lblBomContext      = null!;
        private DataGridView    dgvBomComponents   = null!;
        private DataGridView    dgvBomMaterials    = null!;
        private DataGridView    dgvBomHardware     = null!;
        private DataGridView    dgvBomMetal        = null!;
        private Button          btnBomRefresh      = null!;
        private Button          btnBomExportPdf    = null!;
        private Button          btnBomAddMaterial  = null!;
        private Button          btnBomAddHardware  = null!;

        // Controls — Tab 3 / File Properties tab
        private DataGridView    dgvCustomProps   = null!;
        private Button          btnSaveFileProps  = null!;

        // Controls — Tab 3 / Summary sub-tab
        private TextBox         txtSummTitle     = null!;
        private TextBox         txtSummSubject   = null!;
        private TextBox         txtSummAuthor    = null!;
        private TextBox         txtSummKeywords  = null!;
        private TextBox         txtSummHyperlink = null!;
        private TextBox         txtSummRevision  = null!;
        private TextBox         txtSummComments  = null!;

        // Controls — Tab 3 / File Info sub-tab
        private TextBox         txtInfoFile      = null!;
        private TextBox         txtInfoLocation  = null!;
        private TextBox         txtInfoSize      = null!;
        private TextBox         txtInfoCreated   = null!;
        private TextBox         txtInfoModified  = null!;
        private TextBox         txtInfoAccessed  = null!;
        private TextBox         txtInfoLastSaved = null!;
    }
}
