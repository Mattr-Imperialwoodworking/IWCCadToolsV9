using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
// Disambiguate types shared between AutoCAD and .NET assemblies
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using DataTable   = System.Data.DataTable;
using Exception   = System.Exception;
using Res = IWCCadToolsV9.Properties.Resources;

namespace IWCCadToolsV9.UI
{
    public partial class ctlIWCBlockBrowserV2 : UserControl
    {
        #region Fields
        // --- Image lists ---
        private readonly ImageList _treeImages = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        private readonly ImageList _assetThumbs = new ImageList { ImageSize = new Size(96, 96), ColorDepth = ColorDepth.Depth32Bit };

        //private const string ICON_FOLDER = "folder"; - original icon replaced with custom image below
        private const string ICON_BLOCK = "IWCTreeBlock";
        private const string ICON_DWG = ".dwg";
        private const string ICON_PDF = ".pdf";
        private const string ICON_JPG = ".jpg";
        private const string ICON_PNG = ".png";
        private const string ICON_DOCX = ".docx";
        private const string ICON_XLSX = ".xlsx";
        private const string ICON_FILE = "file";
        private const string ICON_URL = "asset_url";

        // Add alongside your other icon keys
        //private const string ICON_COMPONENT = "component"; // ensure an image exists in your ImageList; else we’ll fall back to ICON_BLOCK
        // Image keys mapped to Resources.resx images
        private const string ICON_FOLDER = "IWCTreeFolder2"; // custom folder icon loaded from resources
        private const string ICON_COMPONENT = "IWCTreeBlock";
        private const string ICON_ASSEMBLY = "IWCTreeAsmb";
        private bool _treeIconsLoaded = false;

        // Cache for group & block nodes
        private readonly Dictionary<int, TreeNode> _nodeByGroupId = new();
        // Cache for assets per block
        private readonly Dictionary<int, List<AssetInfo>> _assetsByBlockId = new();

        // Default catch‑all
        private const int DEFAULT_GROUP_ID = 7;
        private const string DEFAULT_GROUP_NAME = "Block Library";

        private enum AssetListMode { LargeIcons, Details }
        private AssetListMode _currentMode = AssetListMode.LargeIcons;

        private struct ModelRefSnapshot
        {
            public string? DefName;           // br.BlockTableRecord name (might be anonymous *U### for dynamic eval)
            public string? DynamicBaseName;   // br.DynamicBlockTableRecord name (stable, non-anonymous)
            public Autodesk.AutoCAD.Geometry.Point3d Pos;
            public Autodesk.AutoCAD.Geometry.Scale3d Scale;
            public double Rot;
            public Autodesk.AutoCAD.Geometry.Vector3d Normal;
        }
        #endregion
        public ctlIWCBlockBrowserV2()
        {
            InitializeComponent();

            // Wire events
            treeGroups.ImageList = _treeImages;
            listAssets.LargeImageList = _assetThumbs;
            listAssets.SmallImageList = _assetThumbs;

            btnRefresh.Click          += (s, e) => LoadGroupsAndBlocks();
            btnSearchInline.Click     += (s, e) => OpenSearchDialog();
            txtSearchInline.KeyDown   += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSearchDialog(); }
            };
            treeGroups.AfterSelect += treeGroups_AfterSelect;
            treeGroups.NodeMouseClick += TreeGroups_NodeMouseClick;
            treeGroups.NodeMouseDoubleClick += TreeGroups_NodeMouseDoubleClick; // NEW
            listAssets.SelectedIndexChanged += listAssets_SelectedIndexChanged;
            listAssets.DoubleClick += listAssets_DoubleClick;
            btnOpenInsert.Click += (s, e) => OpenOrInsertSelected();


            // Seed image keys (system icons by extension; fall back to simple bitmaps)
            EnsureStockIcons();
            EnsureTreeIconsLoaded();
            EnsureTreeDragDropWired();

            this.Load += (s, e) =>
            {
                InitListViewForAssets();
                AttachListViewContextMenu();
            };
            // Load data
            LoadGroupsAndBlocks();
        }

        #region UI / Tree wiring
        private void LoadGroupsAndBlocks()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                ClearDetails();
                _nodeByGroupId.Clear();
                _assetsByBlockId.Clear();
                treeGroups.BeginUpdate();
                treeGroups.Nodes.Clear();

                using var conn = new IWCConn();
                conn.DBConnect();

                // 1) Groups
                var dtGroups = new DataTable();
                using (var da = new SqlDataAdapter(@"
                        SELECT ID, GroupName, GroupParent, GroupDesc, GroupOrder, GroupLock, GroupTag
                        FROM dbo.Dwg_BlockGroups
                        ORDER BY GroupParent, GroupOrder, GroupName;", conn.OpenConn))
                {
                    da.Fill(dtGroups);
                }

                foreach (DataRow r in dtGroups.Rows)
                {
                    int id = Convert.ToInt32(r["ID"]);
                    string name = Convert.ToString(r["GroupName"]) ?? $"Group_{id}";
                    string desc = Convert.ToString(r["GroupDesc"]);
                    int parent = Convert.ToInt32(r["GroupParent"]);
                    int order = r.Table.Columns.Contains("GroupOrder") && r["GroupOrder"] != DBNull.Value
                                 ? Convert.ToInt32(r["GroupOrder"]) : 0;
                    bool locked = r.Table.Columns.Contains("GroupLock") && r["GroupLock"] != DBNull.Value
                                  && Convert.ToBoolean(r["GroupLock"]);
                    string tagStr = r["GroupTag"] != DBNull.Value ? Convert.ToString(r["GroupTag"]) : null;

                    var node = new TreeNode(name)
                    {
                        Tag = new GroupTag
                        {
                            Id = id,
                            ParentId = parent,
                            Desc = desc,
                            Order = order,
                            Locked = locked,
                            TagCode = string.IsNullOrWhiteSpace(tagStr) ? null : tagStr.Trim()
                        },
                        ImageKey = ICON_FOLDER,
                        SelectedImageKey = ICON_FOLDER
                    };
                    _nodeByGroupId[id] = node;
                }


                // Ensure default group exists
                if (!_nodeByGroupId.ContainsKey(DEFAULT_GROUP_ID))
                {
                    var fallback = new TreeNode(DEFAULT_GROUP_NAME)
                    {
                        Tag = new GroupTag { Id = DEFAULT_GROUP_ID, ParentId = 0, Desc = "Catch‑all for blocks without associations" },
                        ImageKey = ICON_FOLDER,
                        SelectedImageKey = ICON_FOLDER
                    };
                    _nodeByGroupId[DEFAULT_GROUP_ID] = fallback;
                }

                // Parent/child wiring
                foreach (var kvp in _nodeByGroupId)
                {
                    var node = kvp.Value;
                    var tag = (GroupTag)node.Tag;
                    if (tag.ParentId == 0)
                        treeGroups.Nodes.Add(node);
                    else if (_nodeByGroupId.TryGetValue(tag.ParentId, out var parent))
                        parent.Nodes.Add(node);
                    else
                        treeGroups.Nodes.Add(node);
                }

                // 2) Blocks under groups (from assoc)
                var dtAssoc = new DataTable();
                using (var da = new SqlDataAdapter(@"
                        SELECT a.GroupID, b.ID AS BlockID, b.BlockName, b.BlockTag
                        FROM dbo.Dwg_BlockGroups_Assoc a
                        INNER JOIN dbo.Dwg_Block b ON b.ID = a.BlockID
                        ORDER BY a.GroupID, b.BlockName;", conn.OpenConn))
                {
                    da.Fill(dtAssoc);
                }

                foreach (DataRow r in dtAssoc.Rows)
                {
                    int gid = Convert.ToInt32(r["GroupID"]);
                    int bid = Convert.ToInt32(r["BlockID"]);
                    string bname    = Convert.ToString(r["BlockName"]) ?? $"Block_{bid}";
                    string? btag    = r["BlockTag"] == DBNull.Value ? null : Convert.ToString(r["BlockTag"]);
                    string nodeText = !string.IsNullOrWhiteSpace(btag) ? btag : bname;

                    if (_nodeByGroupId.TryGetValue(gid, out var gnode))
                    {
                        var bnode = new TreeNode(nodeText)
                        {
                            Tag = new BlockTag { BlockId = bid, Name = bname, DisplayName = btag, ParentGroupId = gid },
                            ImageKey = ICON_ASSEMBLY,
                            SelectedImageKey = ICON_ASSEMBLY
                        };

                        gnode.Nodes.Add(bnode);
                        UpdateBlockNodeVisual(bnode, bid);
                    }
                }

                // 3) Orphan blocks into default group
                using (var cmd = new SqlCommand(@"
                        SELECT b.ID, b.BlockName, b.BlockTag
                        FROM dbo.Dwg_Block b
                        LEFT JOIN dbo.Dwg_BlockGroups_Assoc a ON a.BlockID = b.ID
                        WHERE a.BlockID IS NULL
                        ORDER BY b.BlockName;", conn.OpenConn))
                using (var rdr = cmd.ExecuteReader())
                {
                    if (_nodeByGroupId.TryGetValue(DEFAULT_GROUP_ID, out var defNode))
                    {
                        while (rdr.Read())
                        {
                            int bid      = rdr.GetInt32(rdr.GetOrdinal("ID"));
                            string bname = rdr.IsDBNull(rdr.GetOrdinal("BlockName")) ? $"Block_{bid}" : rdr.GetString(rdr.GetOrdinal("BlockName"));
                            string? btag = rdr.IsDBNull(rdr.GetOrdinal("BlockTag"))  ? null : rdr.GetString(rdr.GetOrdinal("BlockTag"));
                            string nodeText = !string.IsNullOrWhiteSpace(btag) ? btag : bname;

                            var bnode = new TreeNode(nodeText)
                            {
                                Tag = new BlockTag { BlockId = bid, Name = bname, DisplayName = btag, ParentGroupId = DEFAULT_GROUP_ID },
                                ImageKey = ICON_ASSEMBLY,
                                SelectedImageKey = ICON_ASSEMBLY
                            };

                            defNode.Nodes.Add(bnode);
                            UpdateBlockNodeVisual(bnode, bid);
                        }
                    }
                }

                // 4) Eager-load assets for all blocks so nodes can be appended under each block (and list quickly)
                EagerLoadAssetsForAllBlocks(conn.OpenConn);

                // 5) Append asset nodes under each block
                AppendAssetsAsChildNodes();
                RefreshAllBlockIcons();

                treeGroups.CollapseAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading tree: {ex.Message}", "Block Browser V2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                treeGroups.EndUpdate();
                Cursor = Cursors.Default;
            }
        }

        private void EagerLoadAssetsForAllBlocks(SqlConnection sql)
        {
            // This query assumes typical columns; it will fall back safely if some are missing.
            // Expected columns on dbo.Dwg_BlockAssets:
            //   ID (int), BlockID (int), FileName (nvarchar), FileExt (nvarchar) or FileType,
            //   FileImage (varbinary(max)) preview for DWG (and optionally others),
            //   FileData (varbinary(max)) binary contents (optional if you store file paths elsewhere).
            var dt = new DataTable();

            // Try to cover both FileExt and FileType naming
            string sqlAssets = @"
                        SELECT 
                            ID,
                            BlockID,
                            FileName,
                            FileType,
                            FileDescription,
                            FileDateAdded,
                            FileImage,
                            FileData,
                            FileIsView,
                            AssetLinkUrl
                        FROM dbo.Dwg_BlockAssets
                        ORDER BY BlockID, FileName;";

            using (var da = new SqlDataAdapter(sqlAssets, sql))
            {
                da.Fill(dt);
            }

            _assetsByBlockId.Clear();

            foreach (DataRow r in dt.Rows)
            {
                // In EagerLoadAssetsForAllBlocks row mapping:
                var a = new AssetInfo
                {
                    Id = SafeGet<int>(r, "ID"),
                    BlockId = SafeGet<int>(r, "BlockID"),
                    FileName = SafeGet<string>(r, "FileName") ?? $"Asset_{SafeGet<int>(r, "ID")}",
                    FileExt = NormalizeExt(SafeGet<string>(r, "FileType")),
                    Description = SafeGet<string>(r, "FileDescription"),
                    DateAdded = r.Table.Columns.Contains("FileDateAdded") && r["FileDateAdded"] != DBNull.Value
                    ? (DateTime?)Convert.ToDateTime(r["FileDateAdded"]) : null,
                    FileImageBytes = r.Table.Columns.Contains("FileImage") && r["FileImage"] != DBNull.Value
                    ? (byte[])r["FileImage"] : null,
                    FileDataBytes = r.Table.Columns.Contains("FileData") && r["FileData"] != DBNull.Value
                    ? (byte[])r["FileData"] : null,
                    IsView = r.Table.Columns.Contains("FileIsView") && r["FileIsView"] != DBNull.Value
                    && Convert.ToBoolean(r["FileIsView"]),
                    LinkUrl = r.Table.Columns.Contains("AssetLinkUrl") && r["AssetLinkUrl"] != DBNull.Value
                    ? Convert.ToString(r["AssetLinkUrl"])
                    : null
                };



                if (!_assetsByBlockId.TryGetValue(a.BlockId, out var list))
                {
                    list = new List<AssetInfo>();
                    _assetsByBlockId[a.BlockId] = list;
                }
                list.Add(a);
            }
        }

        private static T SafeGet<T>(DataRow r, string col)
        {
            if (!r.Table.Columns.Contains(col) || r[col] == DBNull.Value) return default!;
            return (T)Convert.ChangeType(r[col], typeof(T));
        }

        private static string NormalizeExt(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return "";
            ext = ext.Trim();
            if (!ext.StartsWith(".")) ext = "." + ext;
            return ext.ToLowerInvariant();
        }

        private void AppendAssetsAsChildNodes()
        {
            // Walk all group nodes → block nodes and add asset child nodes beneath each block
            foreach (TreeNode groupNode in treeGroups.Nodes)
                AppendAssetsUnderBlocksRecursive(groupNode);
        }

        private void AppendAssetsUnderBlocksRecursive(TreeNode node)
        {
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag is BlockTag bt)
                {
                    // Remove any previous asset/view nodes
                    for (int i = child.Nodes.Count - 1; i >= 0; i--)
                    {
                        var t = child.Nodes[i].Tag;
                        if (t is AssetTag || t is ViewsFolderTag) child.Nodes.RemoveAt(i);
                    }

                    if (_assetsByBlockId.TryGetValue(bt.BlockId, out var assets))
                    {
                        var viewAssets = assets.Where(a => a.IsView).ToList();
                        var otherAssets = assets.Where(a => !a.IsView).ToList();

                        // Non-view assets directly under the block
                        foreach (var a in otherAssets)
                        {
                            child.Nodes.Add(new TreeNode(a.FileName)
                            {
                                Tag = new AssetTag { AssetId = a.Id, BlockId = a.BlockId, FileName = a.FileName, FileExt = a.FileExt },
                                ImageKey = ImageKeyForExt(a.FileExt),
                                SelectedImageKey = ImageKeyForExt(a.FileExt)
                            });
                        }

                        // Views under a "Views" folder
                        if (viewAssets.Count > 0)
                        {
                            var viewsNode = new TreeNode("Views")
                            {
                                Tag = new ViewsFolderTag { BlockId = bt.BlockId },
                                ImageKey = ICON_FOLDER,
                                SelectedImageKey = ICON_FOLDER
                            };

                            foreach (var a in viewAssets)
                            {
                                viewsNode.Nodes.Add(new TreeNode(a.FileName)
                                {
                                    Tag = new AssetTag { AssetId = a.Id, BlockId = a.BlockId, FileName = a.FileName, FileExt = a.FileExt },
                                    ImageKey = ImageKeyForExt(a.FileExt),
                                    SelectedImageKey = ImageKeyForExt(a.FileExt)
                                });
                            }

                            child.Nodes.Add(viewsNode);
                        }

                        UpdateBlockNodeVisual(child, bt.BlockId); // sets Component vs Assembly icon
                    }

                    // Recurse into the block node (safe; children are assets/folders)
                    AppendAssetsUnderBlocksRecursive(child);
                }
                else if (child.Tag is GroupTag)
                {
                    // NEW: recurse into sub-folders so we don’t skip blocks inside them
                    AppendAssetsUnderBlocksRecursive(child);
                }
            }
        }

        private bool _dragDropWired;

        private void EnsureTreeDragDropWired()
        {
            if (_dragDropWired) return;

            treeGroups.AllowDrop = true;
            treeGroups.ItemDrag += treeGroups_ItemDrag;
            treeGroups.DragEnter += treeGroups_DragEnter;
            treeGroups.DragOver += treeGroups_DragOver;
            treeGroups.DragDrop += treeGroups_DragDrop;

            _dragDropWired = true;
        }
        #endregion
        #region Asset I/O (download/upload/preview)
        #endregion
        #region Exporters (build DWG assets)
        #endregion
        #region Importers (closure + MS composition)   <-- your new canonical importer
        #endregion
        #region Insert & Annotative helpers
        #endregion
        #region Context menus
        #endregion
        #region Utilities (naming, compat helpers)

        //User logger functions for tracking interactions
        private int? _currentProjectId;
        private int? _currentDashId;

        public int? CurrentProjectId
        {
            get => _currentProjectId;
            set => _currentProjectId = value;
        }
        public int? CurrentDashId
        {
            get => _currentDashId;
            set => _currentDashId = value;
        }
        private void LogUserActivity(string operation)
        {
            try
            {
                // Trims/limits to 50 chars inside logger; safe to pass any string here.
                UserActivityLogger.Log(operation, projId: _currentProjectId, dashId: _currentDashId);
            }
            catch { /* never block UI on logging */ }
        }
        #endregion






        // -----------------------------
        // Selection behavior
        // -----------------------------
        private void treeGroups_AfterSelect(object? sender,  TreeViewEventArgs e)
        {
            ClearPreview();

            switch (e.Node?.Tag)
            {
                case GroupTag gt:
                    listAssets.Items.Clear();
                    lblSelectedBlock.Text = "(no block selected)";
                    // Show folder info (name/desc/tag), no date/file
                    SetDetails(
                        e.Node.Text,
                        null,
                        gt.Desc,
                        "",          // no notes for a folder
                        ""
                    );
                    break;

                case BlockTag bt:
                    ShowBlockDetails(bt.BlockId);
                    PopulateAssetListForBlock(bt.BlockId, bt.Name);
                    break;

                case AssetTag at:
                    if (e.Node.Parent?.Tag is BlockTag pbt)
                    {
                        // show the block’s details (left side) and select asset (right list)
                        ShowBlockDetails(pbt.BlockId);
                        PopulateAssetListForBlock(pbt.BlockId, pbt.Name, selectAssetId: at.AssetId);
                    }
                    break;
            }
        }



        private void PopulateAssetListForBlock(int blockId, string? blockName, int? selectAssetId = null)
        {
            listAssets.BeginUpdate();

            // If user flipped the ListView to Details elsewhere, ensure columns exist.
            if (listAssets.View == View.Details && listAssets.Columns.Count == 0)
            {
                listAssets.Columns.Add("Name", 220);
                listAssets.Columns.Add("Type", 80);
                listAssets.Columns.Add("Added", 140);
                listAssets.Columns.Add("Description", 300);
            }

            listAssets.Items.Clear();
            _assetThumbs.Images.Clear();
            lblSelectedBlock.Text = blockName ?? "(block)";

            if (_assetsByBlockId.TryGetValue(blockId, out var assets))
            {
                int imgIndex = 0;
                foreach (var a in assets)
                {
                    // Choose thumbnail: prefer DB-stored preview, else system icon
                    // choose thumbnail
                    System.Drawing.Image thumb = null;
                    if (!string.IsNullOrWhiteSpace(a.LinkUrl))
                    {
                        thumb = GetUrlThumb();
                    }
                    else if (a.FileImageBytes?.Length > 0)
                    {
                        // FIX: Image.FromStream holds a lazy ref to the stream; disposing it before
                        // the next paint causes GDI+ to render a black square.
                        // new Bitmap(...) forces a full pixel decode before the stream closes.
                        using var ms = new MemoryStream(a.FileImageBytes);
                        try { thumb = new Bitmap(System.Drawing.Image.FromStream(ms)); } catch { thumb = null; }
                    }
                    if (thumb == null)
                        thumb = GetSystemIconBitmap(a.FileExt) ?? SystemIcons.Application.ToBitmap();

                    _assetThumbs.Images.Add(thumb);

                    var item = new ListViewItem(a.FileName, imgIndex) { Tag = a };

                    // "Type" column: show URL for links
                    string typeTxt = !string.IsNullOrWhiteSpace(a.LinkUrl) ? "URL"
                                      : string.IsNullOrWhiteSpace(a.FileExt) ? "" : a.FileExt.ToLowerInvariant();
                    string addedTxt = a.DateAdded.HasValue ? a.DateAdded.Value.ToLocalTime().ToString("g") : "";
                    string descTxt = a.Description ?? "";

                    item.SubItems.Add(typeTxt);
                    item.SubItems.Add(addedTxt);
                    item.SubItems.Add(descTxt);

                    // Tooltip: include hostname for links
                    if (!string.IsNullOrWhiteSpace(a.LinkUrl) && Uri.TryCreate(a.LinkUrl, UriKind.Absolute, out var u))
                    {
                        item.ToolTipText = $"URL • {u.Host}" + (a.DateAdded.HasValue ? $" • {a.DateAdded:yyyy-MM-dd}" : "");
                    }
                    else
                    {
                        string tipExt = string.IsNullOrWhiteSpace(a.FileExt) ? "(unknown)" : a.FileExt.ToUpperInvariant();
                        string tipDate = a.DateAdded.HasValue ? a.DateAdded.Value.ToString("yyyy-MM-dd") : "";
                        var parts = new List<string> { tipExt };
                        if (!string.IsNullOrWhiteSpace(descTxt)) parts.Add(descTxt.Trim());
                        if (!string.IsNullOrWhiteSpace(tipDate)) parts.Add(tipDate);
                        item.ToolTipText = string.Join(" • ", parts);
                    }



                    listAssets.Items.Add(item);
                    imgIndex++;
                }

                // Optionally select a specific asset (e.g., when clicked in the tree)
                if (selectAssetId.HasValue)
                {
                    foreach (ListViewItem li in listAssets.Items)
                    {
                        if (li.Tag is AssetInfo ai && ai.Id == selectAssetId.Value)
                        {
                            li.Selected = true;
                            li.Focused = true;
                            li.EnsureVisible();
                            ShowAssetPreview(ai);
                            break;
                        }
                    }
                }

                // If we're in Details mode, auto-size columns and let Description stretch
                if (listAssets.View == View.Details && listAssets.Columns.Count > 0)
                {
                    // Auto-size to header/content first
                    for (int i = 0; i < listAssets.Columns.Count; i++)
                        listAssets.Columns[i].Width = -2;

                    // Make last column (Description) fill remaining space
                    int fixedWidth = 0;
                    for (int i = 0; i < listAssets.Columns.Count - 1; i++)
                        fixedWidth += listAssets.Columns[i].Width;

                    int remaining = listAssets.ClientSize.Width - fixedWidth - SystemInformation.VerticalScrollBarWidth;
                    if (remaining > 120)
                        listAssets.Columns[listAssets.Columns.Count - 1].Width = remaining;
                }
            }

            listAssets.EndUpdate();
        }


        private void listAssets_SelectedIndexChanged(object? sender,  EventArgs e)
        {
            if (listAssets.SelectedItems.Count == 1 && listAssets.SelectedItems[0].Tag is AssetInfo ai)
                ShowAssetPreview(ai);
            else
                ClearPreview();
        }

        private void listAssets_DoubleClick(object? sender,  EventArgs e)
        {
            OpenOrInsertSelected();
        }


        private void OpenOrInsertSelected()
        {
            if (listAssets.SelectedItems.Count == 0) return;
            if (listAssets.SelectedItems[0].Tag is not AssetInfo ai) return;
            if (!string.IsNullOrWhiteSpace(ai.LinkUrl))
            {
                OpenUrl(ai.LinkUrl);
                return;
            }
            try
            {
                if (string.Equals(ai.FileExt, ".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    InsertDwgAsset(ai);
                }
                else
                {
                    OpenNonDwgAsset(ai);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Action failed: {ex.Message}", "Block Browser V2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // -----------------------------------------------------------------------
        // Block Search dialog
        // -----------------------------------------------------------------------

        private void OpenSearchDialog()
        {
            using var dlg = new FrmBlockSearch(txtSearchInline.Text.Trim());
            if (dlg.ShowDialog(this) != System.Windows.Forms.DialogResult.OK) return;
            OpenOrInsertFromSearch(dlg.SelectedAssetId, dlg.SelectedBlockId,
                                   dlg.SelectedBlockName, dlg.SelectedIsComponent);
        }

        /// <summary>
        /// Loads the full asset record (including file bytes) and inserts or opens it.
        /// Called from FrmBlockSearch when the user double-clicks a result row.
        /// </summary>
        private void OpenOrInsertFromSearch(int assetId, int blockId, string blockName, bool isComponent)
        {
            AssetInfo? ai = null;
            try
            {
                ai = LoadFullAsset(assetId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load asset data:\n{ex.Message}",
                    "Block Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (ai == null)
            {
                MessageBox.Show("Asset not found.", "Block Browser",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(ai.LinkUrl))
                {
                    OpenUrl(ai.LinkUrl);
                }
                else if (string.Equals(ai.FileExt, ".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    // Build desired name using the same rules as InsertDwgAsset:
                    // component (1 DWG asset) → bare asset name; assembly → "BlockName.AssetName"
                    string baseName     = System.IO.Path.GetFileNameWithoutExtension(ai.FileName ?? $"Asset_{ai.Id}");
                    string desiredName  = isComponent
                        ? SanitizeBlockName(baseName)
                        : SanitizeBlockName($"{blockName}.{baseName}");

                    // Re-use InsertDwgAsset with a synthetic context node so we get the
                    // full import/prompt/insert pipeline. Pass null — the method will
                    // call FindBlockNode(ai.BlockId) to locate the tree node automatically.
                    InsertDwgAsset(ai, contextNode: null);
                }
                else
                {
                    OpenNonDwgAsset(ai);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Action failed:\n{ex.Message}",
                    "Block Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Loads a single asset record including its FileData bytes from the database.
        /// </summary>
        private static AssetInfo? LoadFullAsset(int assetId)
        {
            using var conn = IWCConn.GetSqlConnection();
            conn.Open();
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                SELECT ID, BlockID, FileName, FileType, FileDescription,
                       FileDateAdded, FileImage, FileData, FileIsView, AssetLinkUrl
                FROM dbo.Dwg_BlockAssets
                WHERE ID = @id", conn);
            cmd.Parameters.AddWithValue("@id", assetId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) return null;

            return new AssetInfo
            {
                Id            = rdr.GetInt32(rdr.GetOrdinal("ID")),
                BlockId       = rdr.GetInt32(rdr.GetOrdinal("BlockID")),
                FileName      = rdr["FileName"]      as string,
                FileExt       = rdr["FileType"]       as string,
                Description   = rdr["FileDescription"] as string,
                DateAdded     = rdr["FileDateAdded"]  is DateTime dt ? dt : null,
                FileImageBytes= rdr["FileImage"]      as byte[],
                FileDataBytes = rdr["FileData"]       as byte[],
                IsView        = rdr["FileIsView"]  is bool b && b,
                LinkUrl       = rdr["AssetLinkUrl"]   as string,
            };
        }

        // -----------------------------
        // Previews & Details
        // -----------------------------
        private void ShowAssetPreview(AssetInfo ai)
        {
            lblAssetName.Text = ai.FileName;
            lblAssetType.Text = ai.IsUrl
                ? "URL"
                : (string.IsNullOrEmpty(ai.FileExt) ? "(unknown)" : ai.FileExt.ToUpperInvariant());

            System.Drawing.Image img = null;
            if (!ai.IsUrl)
            {
                if (ai.FileImageBytes != null && ai.FileImageBytes.Length > 0)
                {
                    // FIX: wrap in new Bitmap() to force full decode before MemoryStream is disposed.
                    // Without this, GDI+ lazily reads from a closed stream and renders black.
                    try { using var ms = new MemoryStream(ai.FileImageBytes); img = new Bitmap(System.Drawing.Image.FromStream(ms)); } catch { img = null; }
                }
                if (img == null) img = GetSystemIconBitmap(ai.FileExt) ?? SystemIcons.Application.ToBitmap();
            }
            else
            {
                img = Res.url_asset_48 ?? SystemIcons.Information.ToBitmap();
            }

            picturePreview.Image = img;
            SetDetails(
                ai.FileName,
                ai.DateAdded,
                ai.Description,
                "",                 // assets don’t have separate "notes"
                ai.FileName
            );

        }

        private void ClearPreview()
        {
            lblAssetName.Text = "(no asset selected)";
            lblAssetType.Text = "";
            picturePreview.Image = null;
        }

        private void ClearDetails()
        {
            lblSelectedBlock.Text = "(no block selected)";
            SetDetails("", null, "", "", "");
            ClearPreview();
        }


        // -----------------------------
        // Actions
        // -----------------------------
        // Inside ctlIWCBlockBrowserV2
        // Overload: pass the clicked node when available (asset node OR block node)

        private void InsertDwgAsset(AssetInfo? ai, TreeNode? contextNode = null)
        {
            if (ai == null || ai.FileDataBytes == null || ai.FileDataBytes.Length == 0)
                throw new Exception("This DWG asset has no binary data (FileData).");

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            // contextNode is resolved first so we can read the parent assembly name below.
            if (contextNode == null)
                contextNode = FindBlockNode(ai.BlockId) ?? treeGroups.SelectedNode;

            // Build the block name to use in the drawing.
            // All assets stored under an assembly block (both view assets in the "Views"
            // folder AND plain assets sitting directly under the assembly node) are saved
            // to the DB with only their bare stem name (e.g. "EL01.dwg", "test.dwg").
            // When inserting we must qualify them with the parent assembly name so the
            // block lands in the drawing as "Lagrand.Adorne.EL01" / "Lagrand.Adorne.test"
            // rather than just "EL01" / "test". This prevents name collisions between
            // assemblies and matches the expected IWC naming convention.
            string baseName = System.IO.Path.GetFileNameWithoutExtension(ai.FileName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = $"Asset_{ai.Id}";

            // Locate the parent BlockTag by walking up from contextNode.
            // For view-folder assets the walk goes: AssetNode → ViewsFolderNode → BlockNode.
            // For direct assembly assets the walk goes: AssetNode → BlockNode (one step).
            string? assemblyName   = null;
            bool parentIsComponent = false;
            TreeNode? n = contextNode;
            while (n != null)
            {
                if (n.Tag is BlockTag bt && !string.IsNullOrWhiteSpace(bt.Name))
                {
                    assemblyName      = bt.Name.Trim();
                    parentIsComponent = bt.IsComponent;
                    break;
                }
                n = n.Parent;
            }

            // For simple components (exactly 1 DWG asset, IsComponent == true) the parent
            // block name and the asset name are the same, so concatenating would produce the
            // redundant "IWC_SYM.VIEW.IWC_SYM.VIEW" pattern.  Use just the asset name.
            // For assemblies (multiple assets or explicitly flagged as assembly) prefix with
            // the parent name: "AssemblyName.AssetName" (e.g. "Lagrand.Adorne.EL01").
            string desiredName = (parentIsComponent || string.IsNullOrWhiteSpace(assemblyName))
                ? SanitizeBlockName(baseName)
                : SanitizeBlockName($"{assemblyName}.{baseName}");

            // Persist asset bytes to a temp DWG
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IWCAssets");
            System.IO.Directory.CreateDirectory(tempDir);
            string tempPath = System.IO.Path.Combine(tempDir, $"{desiredName}_{ai.Id}.dwg");
            System.IO.File.WriteAllBytes(tempPath, ai.FileDataBytes);

            using (doc.LockDocument())
            {
                // Pick insertion point
                var ppo = new Autodesk.AutoCAD.EditorInput.PromptPointOptions("\nSpecify insertion point: ");
                var ppr = ed.GetPoint(ppo);
                if (ppr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
                {
                    ed.WriteMessage("\nInsert cancelled.");
                    return;
                }
                var insPt = ppr.Value;

                // Import/overwrite logic
                bool alreadyExists;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    alreadyExists = bt.Has(desiredName);
                    tr.Commit();
                }

                if (alreadyExists)
                {
                    var pko = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions(
                        $"\nBlock '{desiredName}' already exists. [Overwrite/Keep] <Keep>: ")
                    { AllowArbitraryInput = false };

                    pko.Keywords.Add("Overwrite");
                    pko.Keywords.Add("Keep");
                    pko.Keywords.Default = "Keep";

                    var pkr = ed.GetKeywords(pko);
                    if (pkr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

                    if (string.Equals(pkr.StringResult, "Overwrite", StringComparison.OrdinalIgnoreCase))
                    {
                        // Replace the existing definition with the one from the DWG.
                        ImportBlockDefinitionFromFile(db, tempPath, desiredName,
                            Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Replace);

                        //using (var trDump = db.TransactionManager.StartTransaction())
                        //{
                        //    var bt = (BlockTable)trDump.GetObject(db.BlockTableId, OpenMode.ForRead);
                        //    if (bt.Has(desiredName))
                        //    {
                        //        var btr = (BlockTableRecord)trDump.GetObject(bt[desiredName], OpenMode.ForRead);
                        //        AttributeFieldHelper.DumpAdFieldState("AFTER ImportBlockDefinitionFromFile", trDump, btr);
                        //    }
                        //    trDump.Commit();
                        //}
                        // WblockCloneObjects with Replace does NOT copy ExtensionDictionary
                        // entries — and AttributeDefinition Field objects live there.
                        // Manually re-apply field expressions from the source DWG.
                        AttributeFieldHelper.PatchFieldsFromSource(db, tempPath, desiredName);
                        int fixedCount = AttributeFieldHelper.ResyncAllBlockReferences(db, desiredName, removeOrphaned: false);
                        ed.WriteMessage($"\nSynchronized attributes on {fixedCount} reference(s).");
                    }
                    // else Keep: do not import — we’ll just insert the existing definition
                }
                else
                {
                    // Definition not present — import it (no overwrite needed)
                    ImportBlockDefinitionFromFile(db, tempPath, desiredName,
                        Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Ignore);

                    //using (var trDump = db.TransactionManager.StartTransaction())
                    //{
                    //    var bt = (BlockTable)trDump.GetObject(db.BlockTableId, OpenMode.ForRead);
                    //    if (bt.Has(desiredName))
                    //    {
                    //        var btr = (BlockTableRecord)trDump.GetObject(bt[desiredName], OpenMode.ForRead);
                    //        AttributeFieldHelper.DumpAdFieldState("AFTER ImportBlockDefinitionFromFile", trDump, btr);
                    //    }
                    //    trDump.Commit();
                    //}
                    // First-insert path: WblockCloneObjects can still drop Field objects on
                    // AttributeDefinitions if the destination BTR previously existed (e.g. from
                    // an earlier insert in this session). Re-apply field expressions from source.
                    AttributeFieldHelper.PatchFieldsFromSource(db, tempPath, desiredName);
                    //using (var trDump = db.TransactionManager.StartTransaction())
                    //{
                    //    var bt = (BlockTable)trDump.GetObject(db.BlockTableId, OpenMode.ForRead);
                    //    var btr = (BlockTableRecord)trDump.GetObject(bt[desiredName], OpenMode.ForRead);
                    //    AttributeFieldHelper.DumpAdFieldState("AFTER PatchFieldsFromSource", trDump, btr);
                    //    trDump.Commit();
                    //}
                }

                // Insert the reference
                using (var tr2 = db.TransactionManager.StartTransaction())
                {
                    var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr2.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(desiredName))
                    {
                        tr2.Commit();
                        throw new Exception($"Block definition '{desiredName}' was not found after import.");
                    }

                    var btrId = bt[desiredName];
                    var space = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr2.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                    using (var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(insPt, btrId))
                    {
                        space.AppendEntity(br);
                        tr2.AddNewlyCreatedDBObject(br, true);

                        // Determine annotative status
                        bool annotative = IsAnnotativeBtr(btrId, tr2);
                        if (!annotative)
                        {
                            // Fallback: check the reference itself
                            try { annotative = (br.Annotative == AnnotativeStates.True); } catch { }
                        }

                        if (annotative)
                        {
                            // Ensure the BR is flagged annotative (some defs require explicitly setting it on the ref)
                            try
                            {
                                if (br.Annotative != AnnotativeStates.True)
                                    br.Annotative = AnnotativeStates.True;
                            }
                            catch { /* ignore if not supported */ }

                            // Ensure and apply "1:1" annotation scale
                            EnsureAnnotativeScaleOn(br, db, "1:1");
                        }
                        AttributeFieldHelper.InitializeAttributesOnInsert(tr2, br);
                    }


                    tr2.Commit();
                }

                AttributeFieldHelper.EvaluateFieldsNow();
                ed.WriteMessage($"\nInserted '{desiredName}'.");
            }

            try { System.IO.File.Delete(tempPath); } catch { /* ignore */ }
        }

        /// Replace the binary content of an existing DWG asset using current selection.
        /// - If exactly one BlockReference is selected, exports its *definition as-is* (dynamic-safe) for bytes
        ///   and renders a crisp preview from the reference.
        /// - Otherwise, clones the selection into a temporary BlockTableRecord (no changes to originals),
        ///   WBLOCKs that to a temp DWG for bytes, renders a preview from the temp BTR, and cleans up.
        private void ReplaceDwgAssetFromSelection(AssetInfo ai)
        {
            if (ai == null) return;

            var confirm = MessageBox.Show(
                "This will replace the current file information. Are you sure you want to continue?",
                "Replace Asset",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.OK) return;

            if (!TryCaptureDwgFromSelection(out var dwgBytes, out var previewPng))
                return; // user canceled or selection invalid

            // Persist to DB: update only bytes/preview + stamp date
            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                UPDATE dbo.Dwg_BlockAssets
                   SET FileData      = @data,
                       FileImage     = @img,
                       FileDateAdded = @ts
                 WHERE ID = @id;", conn.OpenConn);

                var pData = cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
                pData.Value = dwgBytes ?? Array.Empty<byte>();

                var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
                pImg.Value = (object)previewPng ?? DBNull.Value;

                cmd.Parameters.Add("@ts", System.Data.SqlDbType.DateTime).Value = DateTime.Now;
                cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = ai.Id;

                cmd.ExecuteNonQuery();
                conn.DBClose();

                // Refresh cache/UI for this block
                RefreshAssetsForBlock(ai.BlockId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Database update failed: {ex.Message}", "Replace Asset",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// Captures a DWG (bytes + preview PNG) from the current selection.
        /// Returns false if user cancels or nothing valid selected.
        private bool TryCaptureDwgFromSelection(out byte[] dwgBytes, out byte[] previewPng)
        {
            dwgBytes = null; previewPng = null;

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc?.Database;
            var ed = doc?.Editor;
            if (db == null || ed == null) return false;

            using (doc.LockDocument())
            {
                // 1) Selection (implied -> prompt)
                var selRes = ed.SelectImplied();
                if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || selRes.Value == null || selRes.Value.Count == 0)
                {
                    var opts = new Autodesk.AutoCAD.EditorInput.PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect a single block OR pick objects to capture as DWG: "
                    };
                    selRes = ed.GetSelection(opts);
                    if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return false;
                }

                // 2) If exactly one BlockReference → export definition as-is (dynamic-safe), no base-point prompt
                if (selRes.Value.Count == 1)
                {
                    using var tr = db.TransactionManager.StartTransaction();
                    var id = selRes.Value.GetObjectIds()[0];
                    var ent = tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                    var br = ent as Autodesk.AutoCAD.DatabaseServices.BlockReference;

                    if (br != null)
                    {
                        var defId = !br.DynamicBlockTableRecord.IsNull ? br.DynamicBlockTableRecord : br.BlockTableRecord;

                        // Export DWG bytes (definition "as-is")
                        string tempPath = ExportBlockDefinitionAsIs(db, defId, "IWC_REPLACE");
                        dwgBytes = System.IO.File.ReadAllBytes(tempPath);
                        try { System.IO.File.Delete(tempPath); } catch { /* ignore */ }

                        // Preview from the selected reference (crisp)
                        try
                        {
                            previewPng = BlockIconRenderer.RenderBlockIconFromReference(
                                db,
                                br.ObjectId,
                                iconSizePx: 64,
                                supersampleFactor: 3,
                                background: System.Drawing.Color.Black,
                                finalHairlinePx: 0.55f);
                        }
                        catch { previewPng = null; }

                        tr.Commit();
                        return true;
                    }
                    tr.Commit();
                }

                // 3) Generic geometry path  → match "new dwg asset" logic: PROMPT FOR BASE POINT
                var ppo = new Autodesk.AutoCAD.EditorInput.PromptPointOptions("\nSpecify insertion point for captured DWG: ");
                var ppr = ed.GetPoint(ppo);
                if (ppr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return false;
                var basePt = ppr.Value;

                string tempName = $"IWC_TMP_REPL_{Guid.NewGuid():N}";
                Autodesk.AutoCAD.DatabaseServices.ObjectId tempBtrId;

                // 3a) Build a TEMP BTR inside current DB with Origin = basePt; clone selected entities (DO NOT erase originals)
                using (var trB = db.TransactionManager.StartTransaction())
                {
                    var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)trB.GetObject(db.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

                    var btr = new Autodesk.AutoCAD.DatabaseServices.BlockTableRecord
                    {
                        Name = tempName,
                        Origin = basePt
                    };

                    bt.UpgradeOpen();
                    tempBtrId = bt.Add(btr);
                    trB.AddNewlyCreatedDBObject(btr, true);

                    foreach (var oid in selRes.Value.GetObjectIds())
                    {
                        var src = trB.GetObject(oid, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                        if (src == null) continue;
                        var clone = (Autodesk.AutoCAD.DatabaseServices.Entity)src.Clone();
                        btr.AppendEntity(clone);
                        trB.AddNewlyCreatedDBObject(clone, true);
                    }

                    trB.Commit();
                }

                // 3b) WBLOCK to DWG bytes using the SAME base point (matches add-asset flow)
                string tmpDwg = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{tempName}.dwg");
                try
                {
                    BlockLibraryHelper.WblockToFile(
                        new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection { tempBtrId },
                        basePt,
                        tempName,
                        tmpDwg);

                    dwgBytes = System.IO.File.ReadAllBytes(tmpDwg);
                }
                finally
                {
                    try { if (System.IO.File.Exists(tmpDwg)) System.IO.File.Delete(tmpDwg); } catch { /* ignore */ }
                }

                // 3c) Render a preview from the temp definition in the current DB
                try
                {
                    ed.Regen();
                    previewPng = BlockIconRenderer.RenderBlockIconPng(
                        db,
                        tempName,
                        iconSizePx: 64,
                        supersampleFactor: 3,
                        background: System.Drawing.Color.Black,
                        finalHairlinePx: 0.55f);
                }
                catch { previewPng = null; }

                // 3d) Clean up the temporary BTR
                using (var trC = db.TransactionManager.StartTransaction())
                {
                    var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)trC.GetObject(db.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    if (bt.Has(tempName))
                    {
                        var tmp = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)trC.GetObject(bt[tempName], Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
                        tmp.Erase(true);
                    }
                    trC.Commit();
                }

                return (dwgBytes != null && dwgBytes.Length > 0);
            }
        }

        //private void InsertDwgAsset(AssetInfo? ai, TreeNode? contextNode = null)
        //{
        //    if (ai == null) return;

        //    var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        //    var db = doc.Database;
        //    var ed = doc.Editor;

        //    if (ai.FileDataBytes == null || ai.FileDataBytes.Length == 0)
        //        throw new Exception("This DWG asset has no binary data (FileData).");

        //    // 1) Save DWG to temp
        //    string tempDir = Path.Combine(Path.GetTempPath(), "IWCAssets");
        //    Directory.CreateDirectory(tempDir);
        //    string baseName = Path.GetFileNameWithoutExtension(ai.FileName);
        //    if (string.IsNullOrWhiteSpace(baseName)) baseName = $"Asset_{ai.Id}";
        //    baseName = SanitizeBlockName(baseName);

        //    // 2) Compute prefixed name using group path
        //    // If contextNode is null, try to find the block node to infer the path
        //    if (contextNode == null)
        //        contextNode = FindBlockNode(ai.BlockId) ?? treeGroups.SelectedNode;

        //    string desiredName = BuildPrefixedName(contextNode, baseName);

        //    string tempDwg = Path.Combine(tempDir, $"{baseName}_{ai.Id}.dwg");
        //    File.WriteAllBytes(tempDwg, ai.FileDataBytes);

        //    using (doc.LockDocument())
        //    {
        //        // Prompt for insertion point
        //        var ppo = new Autodesk.AutoCAD.EditorInput.PromptPointOptions("\nSpecify insertion point: ");
        //        var ppr = ed.GetPoint(ppo);
        //        if (ppr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
        //        {
        //            ed.WriteMessage("\nInsert cancelled.");
        //            return;
        //        }
        //        var insPt = ppr.Value;

        //        // Dynamic-safe import under the desired (prefixed) name, with Overwrite/Keep prompt
        //        ObjectId btrId = ImportBlockDefinitionWithPrompt_DynamicSafe(db, ed, tempDwg, desiredName);

        //        // Place reference
        //        using (var tr = db.TransactionManager.StartTransaction())
        //        {
        //            var space = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
        //                        tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

        //            using var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(insPt, btrId);
        //            space.AppendEntity(br);
        //            tr.AddNewlyCreatedDBObject(br, true);

        //            tr.Commit();
        //        }
        //        ed.WriteMessage($"\nInserted '{desiredName}' at picked point.");
        //    }

        //    try { File.Delete(tempDwg); } catch { /* ignore */ }
        //}


        /// <summary>
        /// Imports a block definition from a DWG file into the current DB. If a block with the same name exists,
        /// prompts the user to Overwrite or Keep Existing. Returns the ObjectId of the resulting definition.
        /// </summary>
        /// 

        //private ObjectId ImportBlockDefinitionWithPrompt(Database targetDb, Autodesk.AutoCAD.EditorInput.Editor ed, string dwgPath, string desiredName)
        //{
        //    // Check if the name already exists
        //    using (var tr = targetDb.TransactionManager.StartTransaction())
        //    {
        //        var bt = (BlockTable)tr.GetObject(targetDb.BlockTableId, OpenMode.ForRead);
        //        if (bt.Has(desiredName))
        //        {
        //            // Prompt user for action
        //            var pko = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions($"\nBlock '{desiredName}' already exists. ")
        //            {
        //                Message = "\nChoose action"
        //            };
        //            pko.Keywords.Add("Overwrite");
        //            pko.Keywords.Add("Keep");
        //            pko.Keywords.Default = "Keep";

        //            var pkr = ed.GetKeywords(pko);
        //            if (pkr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || pkr.StringResult == "Keep")
        //            {
        //                // Keep the existing definition; return its ObjectId
        //                var existingId = bt[desiredName];
        //                tr.Commit();
        //                return existingId;
        //            }

        //            tr.Commit();
        //            // If we get here, user chose Overwrite; fall through to replace flow
        //            return ImportOrReplaceBlockDefinition(targetDb, ed, dwgPath, desiredName, overwrite: true);
        //        }
        //        else
        //        {
        //            tr.Commit();
        //            // No conflict; import the block normally
        //            return ImportOrReplaceBlockDefinition(targetDb, ed, dwgPath, desiredName, overwrite: false);
        //        }
        //    }
        //}

        /// <summary>
        /// Imports a block definition from a source DWG into the target DB.
        /// If overwrite==true and a block with desiredName already exists, it will be replaced.
        /// Ensures that the incoming definition's name matches desiredName so Replace works.
        /// </summary>
        /// 
        //------------------------------------------------------------------------------------------------------------------
        //8-18 OLD IMPORTER, REPLACED FOR NESTED BLOCKS
        //
        //private ObjectId ImportOrReplaceBlockDefinition(Database targetDb, Autodesk.AutoCAD.EditorInput.Editor ed, string dwgPath, string desiredName, bool overwrite)
        //{
        //    using var sourceDb = new Database(false, true);
        //    sourceDb.ReadDwgFile(dwgPath, FileShare.Read, true, null);
        //    sourceDb.CloseInput(true);

        //    using var trSrc = sourceDb.TransactionManager.StartTransaction();
        //    var btSrc = (BlockTable)trSrc.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);

        //    // Find a suitable source definition (prefer desiredName; else first named block)
        //    ObjectId srcBtrId = ObjectId.Null;
        //    string? srcName = null;

        //    if (btSrc.Has(desiredName))
        //    {
        //        srcBtrId = btSrc[desiredName];
        //        srcName = desiredName;
        //    }
        //    else
        //    {
        //        foreach (ObjectId id in btSrc)
        //        {
        //            var btr = (BlockTableRecord)trSrc.GetObject(id, OpenMode.ForRead);
        //            if (!btr.IsLayout && !btr.Name.StartsWith("*"))
        //            {
        //                srcBtrId = id;
        //                srcName = btr.Name;
        //                //break;
        //            }
        //        }
        //    }

        //    if (srcBtrId == ObjectId.Null)
        //        throw new Exception("No valid block definition found in asset DWG.");

        //    // If we need to Replace in target, the incoming name must match desiredName.
        //    // If the source name differs, temporarily rename the source BTR.
        //    if (!string.Equals(srcName, desiredName, StringComparison.OrdinalIgnoreCase))
        //    {
        //        var btrw = (BlockTableRecord)trSrc.GetObject(srcBtrId, OpenMode.ForWrite);
        //        btrw.Name = desiredName; // rename to match target for Replace
        //        srcName = desiredName;
        //    }

        //    // Clone into target
        //    using var trTgt = targetDb.TransactionManager.StartTransaction();
        //    var btTgt = (BlockTable)trTgt.GetObject(targetDb.BlockTableId, OpenMode.ForWrite);

        //    var ids = new ObjectIdCollection { srcBtrId };
        //    var idMap = new IdMapping();

        //    // DuplicateRecordCloning.Replace only replaces if a record with the SAME NAME already exists.
        //    // If overwrite==true, Replace will swap the definition's geometry.
        //    // If overwrite==false, and no existing name, this is just a normal import.
        //    sourceDb.WblockCloneObjects(
        //        ids,
        //        targetDb.BlockTableId,
        //        idMap,
        //        overwrite ? DuplicateRecordCloning.Replace : DuplicateRecordCloning.Ignore,
        //        false);

        //    // Return the resulting BTR id
        //    ObjectId resultId = btTgt[desiredName];
        //    trSrc.Commit();
        //    trTgt.Commit();

        //    return resultId;
        //}


        private void OpenNonDwgAsset(AssetInfo ai)
        {
            if (ai.FileDataBytes == null || ai.FileDataBytes.Length == 0)
                throw new Exception("This asset has no binary data (FileData).");

            string tempDir = Path.Combine(Path.GetTempPath(), "IWCAssets");
            Directory.CreateDirectory(tempDir);

            string safeName = Sanitize(ai.FileName);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(safeName)) && !string.IsNullOrWhiteSpace(ai.FileExt))
                safeName += ai.FileExt;

            string tempPath = Path.Combine(tempDir, safeName);
            File.WriteAllBytes(tempPath, ai.FileDataBytes);

            var psi = new ProcessStartInfo(tempPath)
            {
                UseShellExecute = true,
                Verb = "open"
            };
            Process.Start(psi);
        }

        // -----------------------------
        // Helpers
        // -----------------------------
        private static string Sanitize(string? s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        /// <summary>
        /// Reads a DWG into a side Database and clones its block definition into the current drawing if needed.
        /// Returns the imported BlockTableRecord Id.
        /// </summary>
        //------------------------------
        //
        //8-18 works but doesn't import nested blocks
        //
        //------------------------------
        //private ObjectId EnsureBlockDefinitionInCurrentDb(string dwgPath, string preferredBlockName)
        //{
        //    var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        //    var db = doc.Database;

        //    using (var tr = db.TransactionManager.StartTransaction())
        //    {
        //        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        //        if (bt.Has(preferredBlockName))
        //        {
        //            var existing = bt[preferredBlockName];
        //            tr.Commit();
        //            return existing;
        //        }
        //        tr.Commit();
        //    }

        //    using var sourceDb = new Database(false, true);
        //    sourceDb.ReadDwgFile(dwgPath, FileShare.Read, true, null);
        //    sourceDb.CloseInput(true);

        //    using var trTarget = db.TransactionManager.StartTransaction();
        //    using var trSource = sourceDb.TransactionManager.StartTransaction();

        //    var sourceBt = (BlockTable)trSource.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);

        //    ObjectId srcBtrId = ObjectId.Null;
        //    if (sourceBt.Has(preferredBlockName))
        //    {
        //        srcBtrId = sourceBt[preferredBlockName];
        //    }
        //    else
        //    {
        //        foreach (ObjectId id in sourceBt)
        //        {
        //            var btr = (BlockTableRecord)trSource.GetObject(id, OpenMode.ForRead);
        //            if (!btr.IsLayout && !btr.Name.StartsWith("*"))
        //            {
        //                srcBtrId = id;
        //                preferredBlockName = btr.Name; // adopt actual
        //                break;
        //            }
        //        }
        //    }

        //    if (srcBtrId == ObjectId.Null)
        //        throw new Exception("Could not locate a valid block definition in the DWG.");

        //    var ids = new ObjectIdCollection { srcBtrId };
        //    var idMap = new IdMapping();

        //    var targetBt = (BlockTable)trTarget.GetObject(db.BlockTableId, OpenMode.ForWrite);
        //    sourceDb.WblockCloneObjects(ids, db.BlockTableId, idMap, DuplicateRecordCloning.Replace, false);

        //    var imported = targetBt[preferredBlockName];

        //    trSource.Commit();
        //    trTarget.Commit();
        //    return imported;
        //}

        //------------------------------------------------------------------------------------------------------------------
        //8-18 OLD IMPORTER, REPLACED FOR NESTED BLOCKS
        //
        //private ObjectId EnsureBlockDefinitionInCurrentDb(string dwgPath, string preferredBlockName)
        //{
        //    var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        //    var db = doc.Database;

        //    using (var tr = db.TransactionManager.StartTransaction())
        //    {
        //        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        //        if (bt.Has(preferredBlockName))
        //        {
        //            var existing = bt[preferredBlockName];
        //            tr.Commit();
        //            return existing;
        //        }
        //        tr.Commit();
        //    }

        //    using var sourceDb = new Database(false, true);
        //    sourceDb.ReadDwgFile(dwgPath, FileShare.Read, true, null);
        //    sourceDb.CloseInput(true);

        //    // If source doesn’t have preferred name, find the first named block and rename it to preferred.
        //    using (var trSrc = sourceDb.TransactionManager.StartTransaction())
        //    {
        //        var sbt = (BlockTable)trSrc.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);
        //        ObjectId pick = ObjectId.Null;
        //        if (sbt.Has(preferredBlockName))
        //        {
        //            pick = sbt[preferredBlockName];
        //        }
        //        else
        //        {
        //            foreach (ObjectId id in sbt)
        //            {
        //                var btr = (BlockTableRecord)trSrc.GetObject(id, OpenMode.ForRead);
        //                if (!btr.IsLayout && !btr.Name.StartsWith("*")) { pick = id; break; }
        //            }
        //            if (!pick.IsNull)
        //            {
        //                var btrw = (BlockTableRecord)trSrc.GetObject(pick, OpenMode.ForWrite);
        //                btrw.Name = preferredBlockName;
        //            }
        //        }
        //        trSrc.Commit();
        //    }

        //    // Insert brings the whole dependency closure
        //    db.Insert(preferredBlockName, sourceDb, false);

        //    // Return the new id
        //    using (var tr = db.TransactionManager.StartTransaction())
        //    {
        //        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        //        var id = bt[preferredBlockName];
        //        tr.Commit();
        //        return id;
        //    }
        //}

        private void EnsureStockIcons()
        {
            // Tree icons (you can swap to embedded resources if you prefer)
            AddOrReplaceIcon(ICON_FOLDER, GetSystemIconBitmapForFolder() ?? CreateFallbackFolderBitmap());
            AddOrReplaceIcon(ICON_BLOCK, CreateBulletBitmap());

            // File-type icons
            foreach (var ext in new[] { ICON_DWG, ICON_PDF, ICON_JPG, ICON_PNG, ICON_DOCX, ICON_XLSX })
                AddOrReplaceIcon(ext, GetSystemIconBitmap(ext) ?? SystemIcons.Application.ToBitmap());

            AddOrReplaceIcon(ICON_FILE, SystemIcons.Application.ToBitmap());
        }

        private void EnsureTreeIconsLoaded()
        {
            if (_treeIconsLoaded) return;

            // Make sure your ImageList exists and is hooked to the TreeView
            _treeImages.ColorDepth = ColorDepth.Depth32Bit;
            _treeImages.ImageSize = new Size(16, 16);

            // Remove existing keys (avoids duplicates if reloaded)
            if (_treeImages.Images.ContainsKey(ICON_FOLDER)) _treeImages.Images.RemoveByKey(ICON_FOLDER);
            if (_treeImages.Images.ContainsKey(ICON_COMPONENT)) _treeImages.Images.RemoveByKey(ICON_COMPONENT);
            if (_treeImages.Images.ContainsKey(ICON_ASSEMBLY)) _treeImages.Images.RemoveByKey(ICON_ASSEMBLY);

            // Add images from Resources.resx
            // If you didn't add an alias, use: Properties.Resources.IWCTreeFolder2 (etc.)
            if (Res.IWCTreeFolder2 != null)
                _treeImages.Images.Add(ICON_FOLDER, Res.IWCTreeFolder2);

            if (Res.IWCTreeBlock != null)
                _treeImages.Images.Add(ICON_COMPONENT, Res.IWCTreeBlock);

            if (Res.IWCTreeAsmb != null)
                _treeImages.Images.Add(ICON_ASSEMBLY, Res.IWCTreeAsmb);

            if (Res.url_asset_16 != null) _treeImages.Images.Add(ICON_URL, Res.url_asset_16);

            _treeIconsLoaded = true;
        }



        private string ImageKeyForExt(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return ICON_FILE;
            ext = ext.ToLowerInvariant();

            // special-case: URL assets
            if (ext == ".url") return ICON_URL;

            return _treeImages.Images.ContainsKey(ext) ? ext : ICON_FILE;
        }



        private void AddOrReplaceIcon(string key, System.Drawing.Image img)
        {
            if (img == null) return;

            if (img.Width != _treeImages.ImageSize.Width || img.Height != _treeImages.ImageSize.Height)
                img = new Bitmap(img, _treeImages.ImageSize);

            if (_treeImages.Images.ContainsKey(key))
                _treeImages.Images.RemoveByKey(key);

            _treeImages.Images.Add(key, img);
        }

        // --- System icon helpers ---
        private static Bitmap? GetSystemIconBitmap(string? extension)
        {
            try
            {
                const uint SHGFI_ICON = 0x000000100;
                const uint SHGFI_SMALLICON = 0x000000001;
                const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

                SHFILEINFO shinfo;
                IntPtr hImg = SHGetFileInfo(
                    "X" + extension,
                    0,
                    out shinfo,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
                    return null;

                Icon icon = Icon.FromHandle(shinfo.hIcon);
                Bitmap bmp = icon.ToBitmap();
                DestroyIcon(shinfo.hIcon);
                return bmp;
            }
            catch { return null; }
        }

        private static Bitmap? GetSystemIconBitmapForFolder()
        {
            const uint SHGFI_ICON = 0x000000100;
            const uint SHGFI_SMALLICON = 0x000000001;
            const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
            const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

            SHFILEINFO shinfo;
            IntPtr hImg = SHGetFileInfo(
                @"C:\",
                FILE_ATTRIBUTE_DIRECTORY,
                out shinfo,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

            if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
                return null;

            Icon icon = Icon.FromHandle(shinfo.hIcon);
            Bitmap bmp = icon.ToBitmap();
            DestroyIcon(shinfo.hIcon);
            return bmp;
        }

        private static Bitmap CreateBulletBitmap()
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            var rect = new Rectangle(6, 6, 4, 4);
            using var b = new SolidBrush(SystemColors.ControlText);
            g.FillEllipse(b, rect);
            using var p = new Pen(SystemColors.ControlText);
            g.DrawEllipse(p, rect);
            return bmp;
        }

        private static Bitmap CreateFallbackFolderBitmap()
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.Goldenrod);
            using var tab = new SolidBrush(Color.Khaki);
            g.FillRectangle(body, new Rectangle(2, 6, 12, 8));
            g.FillRectangle(tab, new Rectangle(3, 4, 6, 4));
            using var pen = new Pen(Color.Brown);
            g.DrawRectangle(pen, new Rectangle(2, 6, 12, 8));
            g.DrawRectangle(pen, new Rectangle(3, 4, 6, 4));
            return bmp;
        }

        // --- Interop for SHGetFileInfo ---
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            out SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);


        // Set up columns & behaviors (reuses existing image lists)
        private void InitListViewForAssets()
        {
            listAssets.FullRowSelect = true;
            listAssets.HideSelection = false;
            listAssets.MultiSelect = false;
            listAssets.ShowItemToolTips = true;

            // Add columns used when in Details mode (kept even when not visible)
            if (listAssets.Columns.Count == 0)
            {
                listAssets.Columns.Add("Name", 220);
                listAssets.Columns.Add("Type", 80);
                listAssets.Columns.Add("Added", 140);
                listAssets.Columns.Add("Description", 300);
            }

            // Default to your current icon gallery behavior
            SetListMode(AssetListMode.LargeIcons);

            // Sorting & resize handling
            listAssets.ColumnClick += (s, e) => ListViewColumnSort(listAssets, e.Column);
            listAssets.Resize += (s, e) => AutoSizeDetailColumns();
        }

        // Right-click menu to switch between Icons <-> Details
        private void AttachListViewContextMenu()
        {
            var cms = new ContextMenuStrip();
            var miView = new ToolStripMenuItem("View");

            var miIcons = new ToolStripMenuItem("Large Icons", null, (s, e) => SetListMode(AssetListMode.LargeIcons));
            var miDetails = new ToolStripMenuItem("Details", null, (s, e) => SetListMode(AssetListMode.Details));

            cms.Opening += (s, e) =>
            {
                miIcons.Checked = _currentMode == AssetListMode.LargeIcons;
                miDetails.Checked = _currentMode == AssetListMode.Details;
            };

            miView.DropDownItems.Add(miIcons);
            miView.DropDownItems.Add(miDetails);
            cms.Items.Add(miView);

            listAssets.ContextMenuStrip = cms;
        }

        private void SetListMode(AssetListMode mode)
        {
            _currentMode = mode;

            if (mode == AssetListMode.LargeIcons)
            {
                listAssets.View = View.LargeIcon;
                listAssets.LargeImageList = _assetThumbs; // reuse existing thumbnails
                listAssets.SmallImageList = _assetThumbs; // reuse (Windows will scale down)
            }
            else
            {
                listAssets.View = View.Details;
                listAssets.SmallImageList = _assetThumbs; // reuse existing images; no new resources needed
                listAssets.LargeImageList = null;
                AutoSizeDetailColumns();
            }
        }

        private void AutoSizeDetailColumns()
        {
            if (listAssets.View != View.Details) return;

            // Auto-fit then let "Description" stretch to fill
            for (int i = 0; i < listAssets.Columns.Count; i++)
                listAssets.Columns[i].Width = -2; // header/content

            if (listAssets.Columns.Count > 0)
            {
                int fixedWidth = 0;
                for (int i = 0; i < listAssets.Columns.Count - 1; i++)
                    fixedWidth += listAssets.Columns[i].Width;

                int remaining = listAssets.ClientSize.Width - fixedWidth - SystemInformation.VerticalScrollBarWidth;
                if (remaining > 120)
                    listAssets.Columns[listAssets.Columns.Count - 1].Width = remaining;
            }
        }

        // Simple column sorter for Details mode
        private void ListViewColumnSort(ListView lv, int column)
        {
            var sorter = lv.ListViewItemSorter as ListViewItemComparer;
            if (sorter == null || sorter.Column != column)
                lv.ListViewItemSorter = new ListViewItemComparer(column, ascending: true);
            else
                lv.ListViewItemSorter = new ListViewItemComparer(column, ascending: !sorter.Ascending);
            lv.Sort();
        }

        private sealed class ListViewItemComparer : System.Collections.IComparer
        {
            public int Column { get; }
            public bool Ascending { get; }

            public ListViewItemComparer(int column, bool ascending)
            {
                Column = column; Ascending = ascending;
            }

            public int Compare(object? x, object? y)
            {
                var a = x as ListViewItem; var b = y as ListViewItem;
                string sa = a?.SubItems.Count > Column ? a.SubItems[Column].Text : a?.Text ?? "";
                string sb = b?.SubItems.Count > Column ? b.SubItems[Column].Text : b?.Text ?? "";

                // Try date then numeric then string
                if (DateTime.TryParse(sa, out var da) && DateTime.TryParse(sb, out var db))
                    return (Ascending ? 1 : -1) * DateTime.Compare(da, db);

                if (double.TryParse(sa.Replace(",", ""), out var na) && double.TryParse(sb.Replace(",", ""), out var nb))
                    return (Ascending ? 1 : -1) * na.CompareTo(nb);

                return (Ascending ? 1 : -1) * string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
            }
        }


        // -----------------------------
        // Internal tags / models
        // -----------------------------
        private class GroupTag
        {
            public int Id { get; set; }
            public int ParentId { get; set; }
            public string? Desc { get; set; }
            public int Order { get; set; }
            public bool Locked { get; set; }
            public string? TagCode { get; set; }   // NEW: varchar(5) from DB (can be null)
        }


        private class BlockTag
        {
            public int BlockId { get; set; }
            /// <summary>BlockName — used as the AutoCAD block table key on insertion.</summary>
            public string? Name { get; set; }
            /// <summary>BlockTag — user-friendly display name shown in the tree. Null if not set.</summary>
            public string? DisplayName { get; set; }
            public int ParentGroupId { get; set; }
            public bool IsComponent { get; set; }
        }

        private class AssetTag
        {
            public int AssetId { get; set; }
            public int BlockId { get; set; }
            public string? FileName { get; set; }
            public string? FileExt { get; set; }
        }

        private sealed class AssetInfo
        {
            public int Id { get; init; }
            public int BlockId { get; init; }
            public string? FileName { get; init; }
            public string? FileExt { get; init; }
            public string? Description { get; init; }
            public DateTime? DateAdded { get; init; }
            public byte[]? FileImageBytes { get; init; }
            public byte[]? FileDataBytes { get; init; }
            public bool IsView { get; init; }
            public string? LinkUrl { get; init; }
            public bool IsUrl => !string.IsNullOrWhiteSpace(LinkUrl) ||
                                 string.Equals(FileExt, ".url", StringComparison.OrdinalIgnoreCase);
        }



        private sealed class ViewsFolderTag
        {
            public int BlockId { get; init; }
        }


        private void TreeGroups_NodeMouseClick(object? sender,  TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.Node == null) return;
            treeGroups.SelectedNode = e.Node;

            if (e.Node.Tag is GroupTag gtag)
                ShowGroupContextMenu(e.Node, e.Location);
            else if (e.Node.Tag is BlockTag btag)
                ShowBlockContextMenu(e.Node, e.Location);
            else if (e.Node.Tag is AssetTag)
                ShowAssetContextMenu(e.Node, e.Location);
            else if (e.Node.Tag is ViewsFolderTag) ShowViewsFolderContextMenu(e.Node, e.Location);

        }

        private void ShowGroupContextMenu(TreeNode node, Point location)
        {
            var menu = new ContextMenuStrip();

            var miNewNode = new ToolStripMenuItem("New folder");
            var miNewAssembly = new ToolStripMenuItem("New Assembly");
            var miNewComponent = new ToolStripMenuItem("New Component");
            var miEdit = new ToolStripMenuItem("Edit Folder");   // NEW
            var miDelete = new ToolStripMenuItem("Delete Folder");

            miNewNode.Click += (s, e) => CreateChildGroupNode(node);
            miNewAssembly.Click += (s, e) => CreateAssemblyAndRefreshTree();
            miNewComponent.Click += (s, e) => CreateComponentUnderGroup(node);
            miEdit.Click += (s, e) => EditGroupNode(node);       // NEW
            miDelete.Click += (s, e) => DeleteGroupNode(node);

            // Enable/disable actions based on lock/default rules
            bool canDelete = false, canEdit = false;
            if (node.Tag is GroupTag gt)
            {
                canDelete = !gt.Locked && gt.Id != DEFAULT_GROUP_ID;
                canEdit = !gt.Locked && gt.Id != DEFAULT_GROUP_ID; // allow edit for unlocked, non-default
            }
            miDelete.Enabled = canDelete;
            miEdit.Enabled = canEdit;

            // Build menu (Edit before Delete)
            menu.Items.Add(miNewNode);
            menu.Items.Add(miNewAssembly);
            menu.Items.Add(miNewComponent);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miEdit);      // NEW
            menu.Items.Add(miDelete);

            menu.Show(treeGroups, location);
        }



        private void ShowBlockContextMenu(TreeNode node, Point location)
        {
            var menu = new ContextMenuStrip();
            if (node?.Tag is not BlockTag bt)
            {
                menu.Show(treeGroups, location);
                return;
            }

            // --- New / renamed creators ---
            var miNewDwgComponent = new ToolStripMenuItem("New DWG Component");      // renamed
            var miNewPlanView = new ToolStripMenuItem("New Plan View");          // PL
            var miNewElevationView = new ToolStripMenuItem("New Elevation View");     // EL
            var miNewPlanSectionView = new ToolStripMenuItem("New Plan Section View");  // SP
            var miNewVerticalSection = new ToolStripMenuItem("New Vertical Section View"); // SV
            var miNewReflectedView = new ToolStripMenuItem("New Reflected View");     // RV
            var miEditAssembly = new ToolStripMenuItem("Edit Assembly", null, (s, e) => EditAssemblyForNode(node));
            var miAddUrl = new ToolStripMenuItem("Add URL/Web Link…");

            miNewDwgComponent.Click += (s, e) => AddDwgAssetForBlock(bt.BlockId, bt.Name, node); // ✅
            miNewPlanView.Click += (s, e) => AddDwgViewAssetForBlock(bt.BlockId, "PL");
            miNewElevationView.Click += (s, e) => AddDwgViewAssetForBlock(bt.BlockId, "EL");
            miNewPlanSectionView.Click += (s, e) => AddDwgViewAssetForBlock(bt.BlockId, "SP");
            miNewVerticalSection.Click += (s, e) => AddDwgViewAssetForBlock(bt.BlockId, "SV");
            miNewReflectedView.Click += (s, e) => AddDwgViewAssetForBlock(bt.BlockId, "RV");
            miAddUrl.Click += (s, e) => AddUrlAssetForBlock(bt.BlockId, bt.Name);

            // Existing items
            var miUploadFile = new ToolStripMenuItem("Upload File…");
            miUploadFile.Click += (s, e) => UploadFileAssetForBlock(bt.BlockId, bt.Name);

            var miDeleteBlock = new ToolStripMenuItem("Delete");
            miDeleteBlock.Click += (s, e) => DeleteBlockAndAssets(node);

            // Build menu
            menu.Items.Add(miNewDwgComponent);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miNewPlanView);
            menu.Items.Add(miNewElevationView);
            menu.Items.Add(miNewPlanSectionView);
            menu.Items.Add(miNewVerticalSection);
            menu.Items.Add(miNewReflectedView);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(miAddUrl);

            menu.Items.Add(miUploadFile);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miEditAssembly);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miDeleteBlock);

            menu.Show(treeGroups, location);
        }


        private void ShowAssetContextMenu(TreeNode node, Point location)
        {
            var menu = new ContextMenuStrip();

            AssetInfo? ai = null;
            if (node?.Tag is AssetTag at)
                ai = GetAssetInfoById(at.BlockId, at.AssetId);

            // Robust DWG check...
            bool canInsert = false;
            if (ai != null)
            {
                string? ext = ai.FileExt;
                if (string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(ai.FileName))
                    ext = System.IO.Path.GetExtension(ai.FileName);

                if (!string.IsNullOrWhiteSpace(ext))
                {
                    ext = ext.Trim().ToLowerInvariant();
                    if (!ext.StartsWith(".")) ext = "." + ext;
                    canInsert = (ext == ".dwg");
                }
            }

            // Insert (DWG only)
            if (canInsert)
            {
                var miInsert = new ToolStripMenuItem("Insert");
                miInsert.Click += (s, e) =>
                {
                    try { InsertDwgAsset(ai, node); }
                    catch (Exception ex) { MessageBox.Show($"Insert failed: {ex.Message}", "Insert Asset", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                menu.Items.Add(miInsert);
                menu.Items.Add(new ToolStripSeparator());
            }

            //Replace DWG
            if (ai != null &&
                string.Equals(ai.FileExt, ".dwg", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(ai.LinkUrl))
            {
                var miReplace = new ToolStripMenuItem("Replace…");
                miReplace.Click += (s, e) =>
                {
                    try { ReplaceDwgAssetFromSelection(ai); }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Replace failed: {ex.Message}",
                            "Replace Asset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                menu.Items.Add(miReplace);
                menu.Items.Add(new ToolStripSeparator());
            }

            // >>> NEW: Download raw file bytes (any asset that has FileData)
            var miDownload = new ToolStripMenuItem("Download…");
            miDownload.Enabled = (ai?.FileDataBytes != null && ai.FileDataBytes.Length > 0);
            miDownload.Click += (s, e) =>
            {
                try { DownloadAssetToDisk(ai); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Download failed: {ex.Message}", "Download Asset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            menu.Items.Add(miDownload);
            menu.Items.Add(new ToolStripSeparator());
            // <<< NEW

            // Delete (existing)
            var miDelete = new ToolStripMenuItem("Delete");
            miDelete.Click += (s, e) => DeleteAssetNode(node);
            menu.Items.Add(miDelete);

            menu.Show(treeGroups, location);
        }


        private void ShowViewsFolderContextMenu(TreeNode node, Point location)
        {
            if (node?.Tag is not ViewsFolderTag vft) return;

            var menu = new ContextMenuStrip();

            void AddItem(string text, string prefix)
            {
                var mi = new ToolStripMenuItem(text);
                mi.Click += (s, e) =>
                {
                    try
                    {
                        AddDwgViewAssetForBlock(vft.BlockId, prefix);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Create view failed: {ex.Message}", "Views",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                menu.Items.Add(mi);
            }

            AddItem("New Plan View", "PL");
            AddItem("New Elevation View", "EL");
            AddItem("New Plan Section View", "SP");
            AddItem("New Vertical Section View", "SV");
            AddItem("New Reflected View", "RV");

            menu.Show(treeGroups, location);
        }


        private void DeleteAssetNode(TreeNode? assetNode)
        {
            if (assetNode?.Tag is not AssetTag at) return;

            var confirm = MessageBox.Show(
                $"Delete asset:\n\n{at.FileName}\n\nThis cannot be undone.",
                "Delete Asset",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.OK) return;

            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                    "DELETE FROM dbo.Dwg_BlockAssets WHERE ID = @id;", conn.OpenConn);
                cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = at.AssetId;
                cmd.ExecuteNonQuery();
                conn.DBClose();

                // Refresh the parent block's assets (ListView + child nodes)
                RefreshAssetsForBlock(at.BlockId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed: {ex.Message}", "Block Browser V2",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteBlockAndAssets(TreeNode blockNode)
        {
            if (blockNode?.Tag is not BlockTag bt) return;

            var confirm = MessageBox.Show(
                $"Delete block '{bt.Name}' and all associated assets?\n\nThis cannot be undone.",
                "Delete Block",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.OK) return;

            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();
                using var tran = conn.OpenConn.BeginTransaction();

                // 1) Delete assets
                using (var cmdA = new Microsoft.Data.SqlClient.SqlCommand(
                    "DELETE FROM dbo.Dwg_BlockAssets WHERE BlockID = @bid;", conn.OpenConn, tran))
                {
                    cmdA.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = bt.BlockId;
                    cmdA.ExecuteNonQuery();
                }

                // 2) Delete group associations (in case no cascade)
                using (var cmdG = new Microsoft.Data.SqlClient.SqlCommand(
                    "DELETE FROM dbo.Dwg_BlockGroups_Assoc WHERE BlockID = @bid;", conn.OpenConn, tran))
                {
                    cmdG.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = bt.BlockId;
                    cmdG.ExecuteNonQuery();
                }

                // 3) Delete the block row
                using (var cmdB = new Microsoft.Data.SqlClient.SqlCommand(
                    "DELETE FROM dbo.Dwg_Block WHERE ID = @bid;", conn.OpenConn, tran))
                {
                    cmdB.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = bt.BlockId;
                    cmdB.ExecuteNonQuery();
                }

                tran.Commit();
                conn.DBClose();

                // Update caches/UI
                _assetsByBlockId.Remove(bt.BlockId);

                var parentNode = blockNode.Parent;
                blockNode.Remove(); // remove from tree

                // If the deleted block was selected, clear the right side
                if (treeGroups.SelectedNode == null || treeGroups.SelectedNode.Tag is not BlockTag)
                {
                    listAssets.Items.Clear();
                    _assetThumbs.Images.Clear();
                    ClearPreview();
                    lblSelectedBlock.Text = "(no block selected)";
                }

                // Lightweight "refresh": parent node is already updated in the UI.
                // If you prefer a full reload for the parent group, call LoadGroupsAndBlocks();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed: {ex.Message}", "Block Browser V2",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        //private void CreateChildGroupNode(TreeNode parentNode)
        //{
        //    if (parentNode?.Tag is not GroupTag pgt) return;

        //    using var dlg = new SimpleTwoFieldDialog("New Node", "Name:", "Description:");
        //    if (dlg.ShowDialog(this) != DialogResult.OK) return;

        //    string name = (dlg.Value1 ?? "").Trim();
        //    string desc = (dlg.Value2 ?? "").Trim();
        //    if (string.IsNullOrWhiteSpace(name)) return;

        //    int newId, newOrder;
        //    using (var conn = new IWCConn())
        //    {
        //        conn.DBConnect();

        //        // Next GroupOrder under this parent
        //        using (var cmdNext = new Microsoft.Data.SqlClient.SqlCommand(
        //            "SELECT ISNULL(MAX(GroupOrder), 0) + 1 FROM dbo.Dwg_BlockGroups WHERE GroupParent = @pid;", conn.OpenConn))
        //        {
        //            cmdNext.Parameters.Add("@pid", System.Data.SqlDbType.Int).Value = pgt.Id;
        //            newOrder = Convert.ToInt32(cmdNext.ExecuteScalar());
        //        }

        //        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        //            INSERT INTO dbo.Dwg_BlockGroups (GroupName, GroupDesc, GroupParent, GroupOrder, GroupLock)
        //            OUTPUT INSERTED.ID
        //            VALUES (@n, @d, @pid, @ord, @lock);", conn.OpenConn);

        //        cmd.Parameters.Add("@n", System.Data.SqlDbType.NVarChar, 200).Value = name;
        //        cmd.Parameters.Add("@d", System.Data.SqlDbType.NVarChar).Value = string.IsNullOrWhiteSpace(desc) ? (object)DBNull.Value : desc;
        //        cmd.Parameters.Add("@pid", System.Data.SqlDbType.Int).Value = pgt.Id;
        //        cmd.Parameters.Add("@ord", System.Data.SqlDbType.Int).Value = newOrder;
        //        cmd.Parameters.Add("@lock", System.Data.SqlDbType.Bit).Value = 0;   // NEW: user nodes are removable


        //        newId = Convert.ToInt32(cmd.ExecuteScalar());
        //        conn.DBClose();
        //    }

        //    // Build the new node and insert it by order
        //    var child = new TreeNode(name)
        //    {
        //        Tag = new GroupTag { Id = newId, ParentId = pgt.Id, Desc = desc, Order = newOrder, Locked = false },

        //        ImageKey = ICON_FOLDER,
        //        SelectedImageKey = ICON_FOLDER
        //    };

        //    InsertChildByGroupOrder(parentNode, child);
        //    parentNode.Expand();
        //}

        private void CreateChildGroupNode(TreeNode parentNode)
        {
            if (parentNode?.Tag is not GroupTag pgt) return;

            using var dlg = new SimpleTwoFieldDialog(
                "New Folder",
                "Name:",
                "Description:",
                "Group Tag (optional, up to 5 letters/numbers):" // 3rd line
            );

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string name = (dlg.Value1 ?? "").Trim();
            string desc = (dlg.Value2 ?? "").Trim();
            string? code = dlg.Value3;

            if (string.IsNullOrWhiteSpace(name)) return;

            // Final sanitize for DB: keep A–Z/0–9, uppercase, max 5; NULL if empty
            code = string.IsNullOrWhiteSpace(code)
                ? null
                : new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (!string.IsNullOrEmpty(code) && code.Length > 5) code = code.Substring(0, 5);

            int newId, newOrder;
            using (var conn = new IWCConn())
            {
                conn.DBConnect();

                using (var cmdNext = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT ISNULL(MAX(GroupOrder), 0) + 1 FROM dbo.Dwg_BlockGroups WHERE GroupParent = @pid;", conn.OpenConn))
                {
                    cmdNext.Parameters.Add("@pid", System.Data.SqlDbType.Int).Value = pgt.Id;
                    newOrder = Convert.ToInt32(cmdNext.ExecuteScalar());
                }

                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
            INSERT INTO dbo.Dwg_BlockGroups
                (GroupName, GroupDesc, GroupParent, GroupOrder, GroupLock, GroupTag)
            OUTPUT INSERTED.ID
            VALUES
                (@n, @d, @pid, @ord, @lock, @tag);", conn.OpenConn);

                cmd.Parameters.Add("@n", System.Data.SqlDbType.NVarChar, 200).Value = name;
                cmd.Parameters.Add("@d", System.Data.SqlDbType.NVarChar).Value = string.IsNullOrWhiteSpace(desc) ? (object)DBNull.Value : desc;
                cmd.Parameters.Add("@pid", System.Data.SqlDbType.Int).Value = pgt.Id;
                cmd.Parameters.Add("@ord", System.Data.SqlDbType.Int).Value = newOrder;
                cmd.Parameters.Add("@lock", System.Data.SqlDbType.Bit).Value = 0;

                var pTag = cmd.Parameters.Add("@tag", System.Data.SqlDbType.VarChar, 5);
                pTag.Value = string.IsNullOrEmpty(code) ? (object)DBNull.Value : code;

                newId = Convert.ToInt32(cmd.ExecuteScalar());
                conn.DBClose();
            }

            var child = new TreeNode(name)
            {
                Tag = new GroupTag
                {
                    Id = newId,
                    ParentId = pgt.Id,
                    Desc = desc,
                    Order = newOrder,
                    Locked = false,
                    TagCode = string.IsNullOrEmpty(code) ? null : code
                },
                ImageKey = ICON_FOLDER,
                SelectedImageKey = ICON_FOLDER
            };

            InsertChildByGroupOrder(parentNode, child);
            LogUserActivity("Added Folder" + child.Name);
            parentNode.Expand();
        }

        private void EditGroupNode(TreeNode node)
        {
            if (node?.Tag is not GroupTag gt) return;

            // Use the upgraded 3-line dialog (multiline for description)
            using var dlg = new SimpleTwoFieldDialog(
                "Edit Folder",
                "Name:",
                "Description:",
                "Group Tag (optional, up to 5 letters/numbers):",
                multilineSecond: true
            );

            // Prefill current values
            dlg.Value1 = node.Text ?? "";
            dlg.Value2 = gt.Desc ?? "";
            dlg.Value3 = gt.TagCode ?? "";

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string name = (dlg.Value1 ?? "").Trim();
            string desc = (dlg.Value2 ?? "").Trim();
            string? code = dlg.Value3;

            if (string.IsNullOrWhiteSpace(name)) return;

            // Sanitize Group Tag: alnum only, upper, max 5; NULL if empty
            code = string.IsNullOrWhiteSpace(code)
                ? null
                : new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (!string.IsNullOrEmpty(code) && code.Length > 5) code = code.Substring(0, 5);

            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                UPDATE dbo.Dwg_BlockGroups
                SET GroupName = @n,
                    GroupDesc = @d,
                    GroupTag  = @tag
                WHERE ID = @id;", conn.OpenConn);

                cmd.Parameters.Add("@n", System.Data.SqlDbType.NVarChar, 200).Value = name;
                cmd.Parameters.Add("@d", System.Data.SqlDbType.NVarChar).Value = string.IsNullOrWhiteSpace(desc) ? (object)DBNull.Value : desc;
                var pTag = cmd.Parameters.Add("@tag", System.Data.SqlDbType.VarChar, 5);
                pTag.Value = string.IsNullOrEmpty(code) ? (object)DBNull.Value : code;
                cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = gt.Id;

                cmd.ExecuteNonQuery();
                conn.DBClose();

                // Update UI/cache
                node.Text = name;
                gt.Desc = desc;
                gt.TagCode = string.IsNullOrEmpty(code) ? null : code;
                node.Tag = gt;

                treeGroups.Invalidate(); // refresh icon/text render
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Edit Folder",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private void InsertChildByGroupOrder(TreeNode parent, TreeNode childToInsert)
        {
            int insertAt = parent.Nodes.Count; // default append
            int newOrder = (childToInsert.Tag as GroupTag)?.Order ?? int.MaxValue;

            for (int i = 0; i < parent.Nodes.Count; i++)
            {
                var gt = parent.Nodes[i].Tag as GroupTag;
                if (gt == null) continue;

                if (newOrder < gt.Order ||
                    (newOrder == gt.Order && string.Compare(childToInsert.Text, parent.Nodes[i].Text, StringComparison.CurrentCultureIgnoreCase) < 0))
                {
                    insertAt = i;
                    break;
                }
            }
            parent.Nodes.Insert(insertAt, childToInsert);
        }

        //------------------------------------------------------------------------------------------
        //Works but modified to add group tag option when creating new folder

        private void CreateChildBlockUnderGroup(TreeNode parentNode)
        {
            if (parentNode?.Tag is not GroupTag pgt) return;

            using var dlg = new SimpleTwoFieldDialog("New Block", "Block Name:", "Description:");
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string blockName = (dlg.Value1 ?? "").Trim();
            string blockDesc = (dlg.Value2 ?? "").Trim();
            if (string.IsNullOrWhiteSpace(blockName)) return;

            int newBlockId;
            using (var conn = new IWCConn())
            {
                conn.DBConnect();
                using var cmd = new SqlCommand(@"
            INSERT INTO dbo.Dwg_Block (BlockName, BlockDesc, BlockDateCreate, BlockNotes)
            OUTPUT INSERTED.ID
            VALUES (@n, @d, @dc, @notes);", conn.OpenConn);

                cmd.Parameters.AddWithValue("@n", blockName);
                cmd.Parameters.AddWithValue("@d", (object)blockDesc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@dc", DateTime.Now);
                cmd.Parameters.AddWithValue("@notes", DBNull.Value);
                //cmd.Parameters.AddWithValue("@fn", DBNull.Value);
                //cmd.Parameters.AddWithValue("@data", Array.Empty<byte>());
                //cmd.Parameters.AddWithValue("@thumb", DBNull.Value);

                newBlockId = Convert.ToInt32(cmd.ExecuteScalar());

                // association
                using var cmdAssoc = new SqlCommand(@"
            INSERT INTO dbo.Dwg_BlockGroups_Assoc (GroupID, BlockID)
            VALUES (@gid, @bid);", conn.OpenConn);
                cmdAssoc.Parameters.AddWithValue("@gid", pgt.Id);
                cmdAssoc.Parameters.AddWithValue("@bid", newBlockId);
                cmdAssoc.ExecuteNonQuery();

                conn.DBClose();
            }

            // Add block node + eager asset cache entry
            var blockNode = new TreeNode(blockName)
            {
                Tag = new BlockTag { BlockId = newBlockId, Name = blockName, ParentGroupId = pgt.Id },
                ImageKey = ICON_ASSEMBLY,
                SelectedImageKey = ICON_ASSEMBLY
            };
            parentNode.Nodes.Add(blockNode);
            UpdateBlockNodeVisual(blockNode, newBlockId);
            parentNode.Expand();

            _assetsByBlockId[newBlockId] = new List<AssetInfo>(); // empty
        }


        // Inside ctlIWCBlockBrowserV2
        // Requires: using Autodesk.AutoCAD.Geometry;
        //           using IWCCadToolsV9.Helpers; // for BlockLibraryHelper.WblockToFile & BlockIconRenderer


        private void AddDwgViewAssetForBlock(int blockId, string prefix)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            // Label for prompts/descriptions
            string viewKindLabel = prefix switch
            {
                "PL" => "Plan",
                "EL" => "Elevation",
                "SP" => "Plan Section",
                "SV" => "Vertical Section",
                "RV" => "Reflected",
                _ => "View"
            };

            using (doc.LockDocument())
            {
                // 1) Select geometry (allow implied)
                var selRes = ed.SelectImplied();
                if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || selRes.Value == null || selRes.Value.Count == 0)
                {
                    var opts = new Autodesk.AutoCAD.EditorInput.PromptSelectionOptions
                    {
                        MessageForAdding = $"\nSelect objects for {viewKindLabel} view asset: "
                    };
                    selRes = ed.GetSelection(opts);
                    if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
                }

                // 2) Pick insertion/base point
                var ppo = new Autodesk.AutoCAD.EditorInput.PromptPointOptions($"\nSpecify insertion point for new {viewKindLabel} view block: ");
                var ppr = ed.GetPoint(ppo);
                if (ppr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) { ed.WriteMessage("\nCancelled."); return; }
                var insPt = ppr.Value;

                // 3) Per-block sequential base name (no prefixes): PL01/EL02/SP03/SV04/RV05…
                string forcedName = GetNextSequentialNameForPrefix(blockId, prefix);

                // 3.5) Build prefixed final name for LOCAL definition (e.g., IWC.SYM.PL01)
                TreeNode ctxNode = treeGroups.SelectedNode;
                if (ctxNode == null || (ctxNode.Tag is not GroupTag && ctxNode.Tag is not BlockTag && ctxNode.Tag is not ViewsFolderTag))
                    ctxNode = FindBlockNode(blockId) ?? treeGroups.SelectedNode;

                string desiredName = BuildPrefixedName(ctxNode, forcedName); // <- prefixed local name

                // 4) Check for local name collision
                bool nameExists;
                using (var trCheck = db.TransactionManager.StartTransaction())
                {
                    var btCheck = (Autodesk.AutoCAD.DatabaseServices.BlockTable)trCheck.GetObject(db.BlockTableId, OpenMode.ForRead);
                    nameExists = btCheck.Has(desiredName);
                    trCheck.Commit();
                }

                // We'll create a temporary BTR from selection (no insert yet), WBLOCK it,
                // then either rename to desiredName (if free) or Overwrite/Keep the existing one.
                string tempDefName = nameExists ? $"{desiredName}_TMP_{Guid.NewGuid():N}" : desiredName;
                Autodesk.AutoCAD.DatabaseServices.ObjectId tempBtrId;

                // 5) Build the BTR from selection (no insert yet)
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

                    var btr = new Autodesk.AutoCAD.DatabaseServices.BlockTableRecord
                    {
                        Name = tempDefName,
                        Origin = insPt
                    };

                    foreach (var objId in selRes.Value.GetObjectIds())
                    {
                        var ent = (Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(objId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
                        btr.AppendEntity((Autodesk.AutoCAD.DatabaseServices.Entity)ent.Clone());
                        ent.Erase(); // keep current behavior; remove if you prefer to retain source geometry
                    }

                    bt.UpgradeOpen();
                    tempBtrId = bt.Add(btr);
                    tr.AddNewlyCreatedDBObject(btr, true);

                    tr.Commit();
                }

                // 6) WBLOCK that temp definition to DWG using the *desired* (prefixed) name (internal def)
                string tempDwgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{desiredName}_{Guid.NewGuid():N}.dwg");
                BlockLibraryHelper.WblockToFile(new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection { tempBtrId }, insPt, desiredName, tempDwgPath);

                // 7) Finalize local definition name and insert one instance
                if (!nameExists)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                        if (!string.Equals(tempDefName, desiredName, StringComparison.Ordinal))
                        {
                            bt.UpgradeOpen();
                            var tempBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(bt[tempDefName], OpenMode.ForWrite);
                            tempBtr.Name = desiredName; // now local def is prefixed, e.g. IWC.SYM.PL01
                        }

                        var space = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                        var finalBtrId = bt[desiredName];

                        using (var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(insPt, finalBtrId))
                        {
                            space.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);
                            AttributeFieldHelper.InitializeAttributesOnInsert(tr, br);
                        }

                        tr.Commit();
                    }
                }
                else
                {
                    // desiredName exists → prompt Overwrite/Keep
                    var pko = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions(
                        $"\nBlock '{desiredName}' already exists. [Overwrite/Keep] <Keep>: ")
                    { AllowArbitraryInput = false };

                    pko.Keywords.Add("Overwrite");
                    pko.Keywords.Add("Keep");
                    pko.Keywords.Default = "Keep";

                    var pkr = ed.GetKeywords(pko);
                    if (pkr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

                    if (string.Equals(pkr.StringResult, "Overwrite", StringComparison.OrdinalIgnoreCase))
                    {
                        ImportBlockDefinitionFromFile(db, tempDwgPath, desiredName,
                            Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Replace);
                        AttributeFieldHelper.PatchFieldsFromSource(db, tempDwgPath, desiredName);
                    }

                    // Clean up temp BTR (we never inserted it)
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        if (bt.Has(tempDefName))
                        {
                            bt.UpgradeOpen();
                            var tmp = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(bt[tempDefName], OpenMode.ForWrite);
                            tmp.Erase(true);
                        }

                        // Insert the (existing or replaced) desiredName
                        var space = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                        var finalBtrId = ((Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))[desiredName];

                        using (var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(insPt, finalBtrId))
                        {
                            space.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);
                            AttributeFieldHelper.InitializeAttributesOnInsert(tr, br);
                        }

                        tr.Commit();
                    }
                }

                // 8) Read bytes & build preview (from the final local name)
                byte[] dwgBytes = System.IO.File.ReadAllBytes(tempDwgPath);
                byte[]? previewPng = null;
                try
                {
                    ed.Regen();
                    previewPng = BlockIconRenderer.RenderBlockIconPng(
                        db, desiredName,
                        iconSizePx: 64,
                        supersampleFactor: 3,
                        background: System.Drawing.Color.Black,
                        finalHairlinePx: 0.55f);
                } catch { }
                // DEBUG: writes PNG to %TEMP% so you can inspect the rendered icon outside the dialog.
                // Remove this block once icon quality is confirmed.
                if (previewPng != null)
                    try { System.IO.File.WriteAllBytes(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"IWC_icon_debug_{desiredName}.png"),
                        previewPng); } catch { /* ignore debug write failures */ }
                try { System.IO.File.Delete(tempDwgPath); } catch { /* ignore */ }

                string descText = $"{viewKindLabel} View asset • {DateTime.Now:g}";

                // 9) Upload DWG asset to SQL
                // IMPORTANT CHANGE: save only the simple view number/name (no prefixes)
                using (var conn = new IWCConn())
                {
                    conn.DBConnect();
                    using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                INSERT INTO dbo.Dwg_BlockAssets
                    (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData,  FileImage, FileIsView)
                VALUES
                    (@bid,   @fn,      @ft,      @fd,            @fda,         @data,     @img,      @isview);", conn.OpenConn);

                    cmd.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
                    cmd.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = forcedName + ".dwg"; // <-- only PL01/EL01/etc.
                    cmd.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = ".dwg";
                    cmd.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = (object)descText ?? DBNull.Value;
                    cmd.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

                    var pData = cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
                    pData.Value = dwgBytes;

                    var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
                    pImg.Value = (object)previewPng ?? DBNull.Value;

                    var pIsView = cmd.Parameters.Add("@isview", System.Data.SqlDbType.Bit);
                    pIsView.Value = true;

                    cmd.ExecuteNonQuery();
                    conn.DBClose();
                }

                // 10) Refresh cache/UI (rebuilds "Views" folder)
                RefreshAssetsForBlock(blockId);
                ed.WriteMessage($"\nCreated and uploaded {viewKindLabel} view asset '{forcedName}.dwg' (local name: '{desiredName}').");
            }
        }




        //8/15 - Works well, updated with toggle if user selects a single block on screen to add it using block name as an asset OR seleccting blocks of geometry. If that happens then prompt for block name as typical
        //private void AddDwgAssetForBlock(int blockId, string defaultNameFromBlock)
        //{
        //    var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        //    var db = doc.Database;
        //    var ed = doc.Editor;

        //    using (doc.LockDocument())
        //    {
        //        // A) Selection (try implied, else prompt)
        //        var selRes = ed.SelectImplied();
        //        if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || selRes.Value == null || selRes.Value.Count == 0)
        //        {
        //            var opts = new Autodesk.AutoCAD.EditorInput.PromptSelectionOptions { MessageForAdding = "\nSelect object(s) for new asset: " };
        //            selRes = ed.GetSelection(opts);
        //            if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
        //        }

        //        string? assetName = null;
        //        string? tempDwgPath = null;
        //        bool usedBlockAsIs = false;

        //        // B) If exactly one BlockReference → export its definition as-is and use its base name
        //        if (selRes.Value.Count == 1)
        //        {
        //            using var tr = db.TransactionManager.StartTransaction();
        //            var id = selRes.Value.GetObjectIds()[0];
        //            var ent = tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
        //            var br = ent as Autodesk.AutoCAD.DatabaseServices.BlockReference;

        //            if (br != null)
        //            {
        //                // Get the *real* definition and name (dynamic-safe)
        //                var defId = !br.DynamicBlockTableRecord.IsNull ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        //                assetName = GetBaseBlockName(br, tr);
        //                tr.Commit();

        //                // Export definition "as-is" (no explode/no wrapper)
        //                tempDwgPath = ExportBlockDefinitionAsIs(db, defId, assetName);
        //                usedBlockAsIs = true;
        //            }
        //            else tr.Commit();
        //        }

        //        // C) Fallback: existing flow for generic geometry (no block selected)
        //        if (tempDwgPath == null)
        //        {
        //            // Ask for asset name when not using an existing block
        //            var nameOpts = new Autodesk.AutoCAD.EditorInput.PromptStringOptions("\nEnter Asset Name (no extension): ") { AllowSpaces = true };
        //            var nameRes = ed.GetString(nameOpts);
        //            if (nameRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

        //            string raw = string.IsNullOrWhiteSpace(nameRes.StringResult) ? defaultNameFromBlock : nameRes.StringResult.Trim();
        //            assetName = Sanitize(raw);
        //            if (assetName.Length > 255) assetName = assetName.Substring(0, 255);

        //            // WBLOCK selected geometry into a temp DWG (your prior safe pattern)
        //            using (var newDb = new Autodesk.AutoCAD.DatabaseServices.Database(true, false))
        //            {
        //                var ids = new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection(selRes.Value.GetObjectIds());
        //                Autodesk.AutoCAD.DatabaseServices.ObjectId msId;
        //                using (var tr = newDb.TransactionManager.StartTransaction())
        //                {
        //                    var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(newDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        //                    msId = bt[Autodesk.AutoCAD.DatabaseServices.BlockTableRecord.ModelSpace];
        //                    tr.Commit();
        //                }
        //                var idMap = new Autodesk.AutoCAD.DatabaseServices.IdMapping();
        //                db.WblockCloneObjects(ids, msId, idMap, Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Ignore, false);

        //                tempDwgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{assetName}_{Guid.NewGuid():N}.dwg");
        //                newDb.SaveAs(tempDwgPath, Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
        //            }
        //        }

        //        // D) Read DWG bytes (varbinary) and build preview from current DB definition (fast & sharp)
        //        byte[] dwgBytes = System.IO.File.ReadAllBytes(tempDwgPath);
        //        byte[]? pngPreview = null;
        //        try
        //        {
        //            // If we used a block as-is, the definition exists in current db under assetName → render it
        //            // (If fallback path, you can skip or render from newDb if desired)
        //            if (usedBlockAsIs && !string.IsNullOrWhiteSpace(assetName))
        //                pngPreview = BlockIconRenderer.RenderBlockIconPng(db, assetName, iconSizePx: 48, supersampleFactor: 3);
        //        }
        //        catch { pngPreview = null; }

        //        // E) Optional description
        //        string desc = usedBlockAsIs
        //            ? $"DWG asset from existing block '{assetName}' ({DateTime.Now:g})"
        //            : $"DWG asset from selected geometry ({DateTime.Now:g})";

        //        // F) INSERT into dbo.Dwg_BlockAssets (typed)
        //        using var conn = new IWCConn();
        //        conn.DBConnect();

        //        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        //    INSERT INTO dbo.Dwg_BlockAssets
        //        (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData,  FileImage)
        //    VALUES
        //        (@bid,   @fn,      @ft,      @fd,            @fda,         @data,     @img);", conn.OpenConn);

        //        cmd.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
        //        cmd.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = assetName + ".dwg";
        //        cmd.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = ".dwg";
        //        cmd.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = (object)desc ?? DBNull.Value;
        //        cmd.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

        //        var pData = cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
        //        pData.Value = dwgBytes;
        //        var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
        //        pImg.Value = (object)pngPreview ?? DBNull.Value;

        //        cmd.ExecuteNonQuery();
        //        conn.DBClose();

        //        try { System.IO.File.Delete(tempDwgPath); } catch { /* ignore */ }

        //        RefreshAssetsForBlock(blockId);

        //        ed.WriteMessage(usedBlockAsIs
        //            ? $"\nAsset '{assetName}.dwg' (from existing block) uploaded."
        //            : $"\nAsset '{assetName}.dwg' (from geometry) uploaded.");
        //    }
        //}


        //-------------------------------------------------------------------------------------------------------------------------
        //WORKS, HOWEVER REPLACED TO TEST NEW DYNAMIC BLOCK SUPPORT LOGIC

        //private void AddDwgAssetForBlock(int blockId, string defaultNameFromBlock)
        //{
        //    var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        //    var db = doc.Database;
        //    var ed = doc.Editor;

        //    using (doc.LockDocument()) // prevent eLockViolation from modeless UI
        //    {
        //        // 1) Selection (reuse your pattern)
        //        PromptSelectionResult selRes = ed.SelectImplied();
        //        if (selRes.Status != PromptStatus.OK || selRes.Value == null || selRes.Value.Count == 0)
        //        {
        //            var opts = new PromptSelectionOptions { MessageForAdding = "\nSelect Objects for New Block Asset: " };
        //            selRes = ed.GetSelection(opts);
        //            if (selRes.Status != PromptStatus.OK) return;
        //        }

        //        // 2) Base point
        //        var ppo = new PromptPointOptions("\nSpecify base point for block asset: ");
        //        var ppr = ed.GetPoint(ppo);
        //        if (ppr.Status != PromptStatus.OK) return;
        //        Point3d basePt = ppr.Value;

        //        // 3) Name for the block/asset (same validations as your routine)
        //        var nameOpts = new PromptStringOptions("\nEnter Asset Block Name: ") { AllowSpaces = true };
        //        var nameRes = ed.GetString(nameOpts);
        //        if (nameRes.Status != PromptStatus.OK) return;

        //        string rawName = string.IsNullOrWhiteSpace(nameRes.StringResult)
        //            ? defaultNameFromBlock
        //            : nameRes.StringResult.Trim();

        //        string invalidChars = "\\/:;*?\"<>|,='[](){}";
        //        string cleanName = new string(rawName.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
        //        if (string.IsNullOrWhiteSpace(cleanName)) { ed.WriteMessage("\nInvalid name."); return; }
        //        if (cleanName != cleanName.Trim()) { ed.WriteMessage("\nName cannot begin or end with space."); return; }
        //        if (cleanName.Length > 255) cleanName = cleanName.Substring(0, 255);

        //        string blockName = cleanName;
        //        string tempDwgPath;

        //        // 4) Create a block def from the selection (and insert a reference at basePt), then WBLOCK it out
        //        ObjectId newBtrId;
        //        using (var tr = db.TransactionManager.StartTransaction())
        //        {
        //            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        //            if (bt.Has(blockName))
        //            {
        //                ed.WriteMessage($"\nA block named '{blockName}' already exists.");
        //                return;
        //            }

        //            // Create new BlockTableRecord and clone the selected entities into it
        //            var btr = new BlockTableRecord { Name = blockName, Origin = basePt };

        //            foreach (var id in selRes.Value.GetObjectIds())
        //            {
        //                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
        //                btr.AppendEntity((Entity)ent.Clone());
        //                ent.Erase(); // match your existing behavior (erase source); remove if not desired
        //            }

        //            bt.UpgradeOpen();
        //            newBtrId = bt.Add(btr);
        //            tr.AddNewlyCreatedDBObject(btr, true);

        //            // Insert a block reference at the same base point (mirrors your flow)
        //            var curSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        //            using (var br = new BlockReference(basePt, newBtrId))
        //            {
        //                curSpace.AppendEntity(br);
        //                tr.AddNewlyCreatedDBObject(br, true);
        //            }

        //            tr.Commit();

        //            // 5) WBLOCK just this definition out to a temp DWG
        //            var entIds = new ObjectIdCollection { newBtrId };
        //            tempDwgPath = Path.Combine(Path.GetTempPath(), $"{blockName}_{Guid.NewGuid():N}.dwg");
        //            BlockLibraryHelper.WblockToFile(entIds, basePt, blockName, tempDwgPath);
        //        }

        //        // 6) Real DWG bytes (NOT a path string)
        //        byte[] dwgBytes = File.ReadAllBytes(tempDwgPath);

        //        // 7) Render crisp PNG preview for the ListView (reuses your renderer)
        //        ed.Regen();
        //        byte[] pngPreview = NetBlockCommands.BlockIconRenderer.RenderBlockIconPng(
        //            db, blockName, iconSizePx: 48, supersampleFactor: 3);

        //        // 8) Optional: default description
        //        string defaultDesc = $"DWG asset created {DateTime.Now:g}";

        //        // 9) INSERT into dbo.Dwg_BlockAssets with **typed** parameters
        //        using var conn = new IWCConn();
        //        conn.DBConnect();

        //        using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        //    INSERT INTO dbo.Dwg_BlockAssets
        //        (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData,  FileImage)
        //    VALUES
        //        (@bid,    @fn,      @ft,      @fd,            @fda,          @data,     @img);", conn.OpenConn);

        //        cmd.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
        //        cmd.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = blockName + ".dwg";
        //        cmd.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = ".dwg";
        //        cmd.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = (object)defaultDesc ?? DBNull.Value;
        //        cmd.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

        //        // IMPORTANT: varbinary(MAX) with typed parameters
        //        var pData = cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
        //        pData.Value = dwgBytes; // <-- BYTE[]

        //        var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
        //        pImg.Value = (object)pngPreview ?? DBNull.Value; // byte[] or DB NULL

        //        cmd.ExecuteNonQuery();
        //        conn.DBClose();

        //        try { File.Delete(tempDwgPath); } catch { /* ignore */ }

        //        // 10) Refresh the asset cache + UI under this block
        //        RefreshAssetsForBlock(blockId);

        //        ed.WriteMessage($"\nAsset '{blockName}.dwg' added to block (ID {blockId}).");
        //    } // LockDocument
        //}

        //
        private void AddDwgAssetForBlock(int blockId, string? defaultNameFromBlock, TreeNode? contextNode = null)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            using (doc.LockDocument()) // modeless/palette-safe
            {
                // 1) Selection (try implied)
                var selRes = ed.SelectImplied();
                if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || selRes.Value == null || selRes.Value.Count == 0)
                {
                    var opts = new Autodesk.AutoCAD.EditorInput.PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect a single block OR pick objects to wrap into a new block: "
                    };
                    selRes = ed.GetSelection(opts);
                    if (selRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
                }

                // 2) If user picked exactly one BlockReference → export that definition as-is (dynamic-safe)
                if (selRes.Value.Count == 1)
                {
                    using var tr = db.TransactionManager.StartTransaction();
                    var id = selRes.Value.GetObjectIds()[0];
                    var ent = tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                    var br = ent as Autodesk.AutoCAD.DatabaseServices.BlockReference;

                    if (br != null)
                    {
                        var defId = !br.DynamicBlockTableRecord.IsNull ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                        var assetName1 = GetBaseBlockName(br, tr); // base name only (no prefixes)
                        tr.Commit();

                        // Export that definition as-is to DWG (no wrapping)

                        //--------------------------------------------------------------------------------------------------
                        //
                        //8-18 works but didn't handle nested blocks in dynamic blocks well
                        //
                        //---------------------------------------------------------------------------------------------------
                        //string tempDwg1 = ExportBlockDefinitionAsIs(db, defId, assetName1);
                        //byte[] dwgBytes1 = System.IO.File.ReadAllBytes(tempDwg1);
                        //try { System.IO.File.Delete(tempDwg1); } catch { /* ignore */ }

                        //// Preview from current db definition
                        //byte[]? previewBytes1 = null;
                        //try {
                        //    previewBytes1 = BlockIconRenderer.RenderBlockIconFromReference(
                        //        db,
                        //        br.ObjectId,                 // the selected BlockReference
                        //        iconSizePx: 64,
                        //        supersampleFactor: 2,
                        //        background: System.Drawing.Color.Transparent, // or Black
                        //        finalHairlinePx: 0.55f);
                        //                            } catch { }
                        // INSIDE AddDwgAssetForBlock(...), single BlockReference branch:

                        // ... after you compute defId and assetName1 ...
                        string tempDwg1;
                        try
                        {
                            // Preferred: true definition with full dependency graph
                            tempDwg1 = ExportBlockDefinitionAsIs(db, defId, assetName1);
                        }
                        catch
                        {
                            // Safety net: bake the current visibility state into a static snapshot
                            tempDwg1 = ExportBlockSnapshotFromReference(db, br, assetName1, Point3d.Origin);
                        }

                        // Read file bytes for upload
                        byte[] dwgBytes1 = File.ReadAllBytes(tempDwg1);
                        try { File.Delete(tempDwg1); } catch { /* ignore */ }

                        // Safer preview: render by definition name (works for dynamic base defs)
                        byte[]? previewBytes1 = null;
                        try
                        {
                            previewBytes1 = BlockIconRenderer.RenderBlockIconPng(
                                db, assetName1,
                                iconSizePx: 64,
                                supersampleFactor: 3,
                                background: System.Drawing.Color.Black,
                                finalHairlinePx: 0.55f);
                        }
                        catch { /* leave null */ }


                        // Upload (FileIsView = 0) — SAVE ONLY BASE NAME
                        using var conn1 = new IWCConn();
                        conn1.DBConnect();
                        using var cmd1 = new Microsoft.Data.SqlClient.SqlCommand(@"
                    INSERT INTO dbo.Dwg_BlockAssets
                        (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData,  FileImage, FileIsView)
                    VALUES
                        (@bid,   @fn,      @ft,      @fd,            @fda,         @data,     @img,      @isview);", conn1.Conn);

                        cmd1.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
                        cmd1.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = assetName1 + ".dwg"; // base only
                        cmd1.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = ".dwg";
                        cmd1.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = (object)$"Asset from block '{assetName1}' • {DateTime.Now:g}" ?? DBNull.Value;
                        cmd1.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

                        var pData1 = cmd1.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
                        pData1.Value = dwgBytes1;

                        var pImg1 = cmd1.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
                        pImg1.Value = (object)previewBytes1 ?? DBNull.Value;

                        var pIsView1 = cmd1.Parameters.Add("@isview", System.Data.SqlDbType.Bit);
                        pIsView1.Value = false;

                        cmd1.ExecuteNonQuery();
                        conn1.DBClose();

                        RefreshAssetsForBlock(blockId);
                        ed.WriteMessage($"\nAsset '{assetName1}.dwg' uploaded from existing block.");
                        return;
                    }
                    // else: fall through to wrapped-geometry branch
                }

                // === WRAPPED-GEOMETRY BRANCH (create a new block from selection with PREFIXED local name) ===

                // 3) Pick insertion/base point
                var ppo = new Autodesk.AutoCAD.EditorInput.PromptPointOptions("\nSpecify insertion point for new block: ");
                var ppr = ed.GetPoint(ppo);
                if (ppr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) { ed.WriteMessage("\nCancelled."); return; }
                var insPt = ppr.Value;

                // 4) Ask for base name (without prefixes); we’ll add GroupTag prefixes next
                var nameOpts = new Autodesk.AutoCAD.EditorInput.PromptStringOptions("\nEnter Block Name: ") { AllowSpaces = true };
                var nameRes = ed.GetString(nameOpts);
                if (nameRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || string.IsNullOrWhiteSpace(nameRes.StringResult))
                { ed.WriteMessage("\nCancelled."); return; }

                string rawBase = nameRes.StringResult.Trim();
                const string invalid = "\\/:;*?\"<>|,='[](){}"; // dot '.' is allowed
                string sanitizedBase = new string(rawBase.Where(c => !invalid.Contains(c)).ToArray()).Trim();
                if (string.IsNullOrWhiteSpace(sanitizedBase)) { ed.WriteMessage("\nInvalid name."); return; }
                if (sanitizedBase.Length > 255) sanitizedBase = sanitizedBase.Substring(0, 255);

                // 5) Optional description
                var descOpts = new Autodesk.AutoCAD.EditorInput.PromptStringOptions("\nEnter Block Description: ") { AllowSpaces = true };
                var descRes = ed.GetString(descOpts);
                string descText = (descRes.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK) ? descRes.StringResult : "";

                // 6) Build the *prefixed* target name from the tree GroupTag path (e.g., IWC.SYM.Name)
                if (contextNode == null)
                    contextNode = FindBlockNode(blockId) ?? treeGroups.SelectedNode;

                string desiredName = BuildPrefixedName(contextNode, sanitizedBase); // local prefixed name

                // 7) Check if desiredName already exists in this drawing
                bool nameExists;
                using (var trCheck = db.TransactionManager.StartTransaction())
                {
                    var btCheck = (Autodesk.AutoCAD.DatabaseServices.BlockTable)trCheck.GetObject(db.BlockTableId, OpenMode.ForRead);
                    nameExists = btCheck.Has(desiredName);
                    trCheck.Commit();
                }

                // We’ll always produce a DWG file for upload from the selected geometry
                string tempDwgPath;
                string tempDefName;  // local temporary BTR name (if we can’t use desiredName directly)
                Autodesk.AutoCAD.DatabaseServices.ObjectId tempBtrId;

                // 8) Build a BTR from selection (name = desiredName if free; else temporary unique name)
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                    tempDefName = desiredName;
                    if (nameExists)
                        tempDefName = $"{desiredName}_TMP_{Guid.NewGuid():N}";

                    var btr = new Autodesk.AutoCAD.DatabaseServices.BlockTableRecord
                    {
                        Name = tempDefName,
                        Origin = insPt
                    };

                    foreach (var objId in selRes.Value.GetObjectIds())
                    {
                        var ent = (Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(objId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
                        btr.AppendEntity((Autodesk.AutoCAD.DatabaseServices.Entity)ent.Clone());
                        ent.Erase(); // keep your current behavior; remove to retain source geometry
                    }

                    bt.UpgradeOpen();
                    tempBtrId = bt.Add(btr);
                    tr.AddNewlyCreatedDBObject(btr, true);

                    tr.Commit();
                }

                // 9) WBLOCK that temp definition to a DWG using the *desired* (prefixed) name
                tempDwgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{desiredName}_{Guid.NewGuid():N}.dwg");
                BlockLibraryHelper.WblockToFile(new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection { tempBtrId }, insPt, desiredName, tempDwgPath);

                // 10) If desiredName didn’t exist: rename the temp BTR to desiredName and insert it.
                //     If it DID exist: prompt Overwrite/Keep and act accordingly.
                if (!nameExists)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                        // rename temp -> desired
                        if (!string.Equals(tempDefName, desiredName, StringComparison.Ordinal))
                        {
                            bt.UpgradeOpen();
                            var tempBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(bt[tempDefName], OpenMode.ForWrite);
                            tempBtr.Name = desiredName; // safe: we already know it didn't exist
                        }

                        // insert one instance
                        var space = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                        var btrId = bt[desiredName];

                        using (var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(insPt, btrId))
                        {
                            space.AppendEntity(br);
                            tr.AddNewlyCreatedDBObject(br, true);
                            AttributeFieldHelper.InitializeAttributesOnInsert(tr, br);
                        }

                        tr.Commit();
                    }
                }
                else
                {
                    // desiredName exists → ask Overwrite/Keep
                    var pko = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions(
                        $"\nBlock '{desiredName}' already exists. [Overwrite/Keep] <Keep>: ")
                    { AllowArbitraryInput = false };

                    pko.Keywords.Add("Overwrite");
                    pko.Keywords.Add("Keep");
                    pko.Keywords.Default = "Keep";

                    var pkr = ed.GetKeywords(pko);
                    if (pkr.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

                    if (string.Equals(pkr.StringResult, "Overwrite", StringComparison.OrdinalIgnoreCase))
                    {
                        // Replace existing definition with the one we just generated to DWG
                        ImportBlockDefinitionFromFile(db, tempDwgPath, desiredName,
                            Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Replace);
                        AttributeFieldHelper.PatchFieldsFromSource(db, tempDwgPath, desiredName);

                        // Insert the (now replaced) definition
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            var btrId = bt[desiredName];
                            var space = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                            using (var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(insPt, btrId))
                            {
                                space.AppendEntity(br);
                                tr.AddNewlyCreatedDBObject(br, true);
                                AttributeFieldHelper.InitializeAttributesOnInsert(tr, br);
                            }
                            tr.Commit();
                        }
                    }
                    else
                    {
                        // Keep existing definition; just insert it (still upload the new asset to DB)
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            var btrId = bt[desiredName];
                            var space = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                            using (var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(insPt, btrId))
                            {
                                space.AppendEntity(br);
                                tr.AddNewlyCreatedDBObject(br, true);
                                AttributeFieldHelper.InitializeAttributesOnInsert(tr, br);
                            }
                            tr.Commit();
                        }
                    }
                }

                // 11) Preview icon — render from the in-drawing definition (desiredName)
                byte[]? previewBytes = null;
                try
                {
                    ed.Regen();
                    previewBytes = BlockIconRenderer.RenderBlockIconPng(
                        db, desiredName,
                        iconSizePx: 64,
                        supersampleFactor: 3,
                        background: System.Drawing.Color.Black,
                        finalHairlinePx: 0.55f);
                } catch { }
                // DEBUG: inspect rendered icon at %TEMP%\IWC_icon_debug_<name>.png
                if (previewBytes != null)
                    try { System.IO.File.WriteAllBytes(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"IWC_icon_debug_{desiredName}.png"),
                        previewBytes); } catch { /* ignore debug write failures */ }

                // 12) Upload the asset to SQL (FileIsView = 0) — SAVE ONLY BASE NAME (no prefixes)
                byte[] dwgBytes = System.IO.File.ReadAllBytes(tempDwgPath);
                using (var conn2 = new IWCConn())
                {
                    conn2.DBConnect();
                    using var cmd2 = new Microsoft.Data.SqlClient.SqlCommand(@"
                INSERT INTO dbo.Dwg_BlockAssets
                    (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData,  FileImage, FileIsView)
                VALUES
                    (@bid,   @fn,      @ft,      @fd,            @fda,         @data,     @img,      @isview);", conn2.Conn);

                    cmd2.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
                    cmd2.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = sanitizedBase + ".dwg"; // <-- base only
                    cmd2.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = ".dwg";
                    cmd2.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = string.IsNullOrWhiteSpace(descText) ? (object)DBNull.Value : descText;
                    cmd2.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

                    var pData2 = cmd2.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
                    pData2.Value = dwgBytes;

                    var pImg2 = cmd2.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
                    pImg2.Value = (object)previewBytes ?? DBNull.Value;

                    var pIsView2 = cmd2.Parameters.Add("@isview", System.Data.SqlDbType.Bit);
                    pIsView2.Value = false;

                    cmd2.ExecuteNonQuery();
                    conn2.DBClose();
                }

                // 13) Clean up temp DWG
                try { System.IO.File.Delete(tempDwgPath); } catch { /* ignore */ }

                // 14) Refresh caches/UI
                RefreshAssetsForBlock(blockId);
                ed.WriteMessage($"\nAsset '{sanitizedBase}.dwg' uploaded; local block '{desiredName}' created and inserted.");
            }
        }


        //----------------------------
        //----------------------------
        //----TREE VIEW DRAG AND DROP
        //----------------------------
        //----------------------------
        // Allow dragging both blocks and UNLOCKED groups (not the default/root group)
        private void treeGroups_ItemDrag(object? sender,  ItemDragEventArgs e)
        {
            if (e.Item is not TreeNode n) return;

            if (n.Tag is BlockTag)
            {
                treeGroups.SelectedNode = n;
                DoDragDrop(n, DragDropEffects.Move);
                return;
            }

            if (n.Tag is GroupTag gg)
            {
                // Block moving locked groups or the default "Block Library"
                if (gg.Locked || gg.Id == DEFAULT_GROUP_ID) return;

                treeGroups.SelectedNode = n;
                DoDragDrop(n, DragDropEffects.Move);
            }
        }

        private void treeGroups_DragEnter(object? sender,  DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(TreeNode)) ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void treeGroups_DragOver(object? sender,  DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (!e.Data.GetDataPresent(typeof(TreeNode))) return;

            var src = (TreeNode)e.Data.GetData(typeof(TreeNode));
            if (src == null) return;

            var clientPt = treeGroups.PointToClient(new Point(e.X, e.Y));
            var over = treeGroups.GetNodeAt(clientPt);
            if (over == null) return;

            if (src.Tag is BlockTag)
            {
                // existing behavior for blocks
                var (targetGroup, _) = ResolveDropTargetForBlocks(over, clientPt);
                if (targetGroup != null) { e.Effect = DragDropEffects.Move; treeGroups.SelectedNode = over; }
                return;
            }

            if (src.Tag is GroupTag sg)
            {
                // Can't move locked or default (shouldn’t reach here due to ItemDrag gate)
                if (sg.Locked || sg.Id == DEFAULT_GROUP_ID) return;

                var targetGroup = ResolveDropTargetForGroups(over);
                if (targetGroup == null) return;

                // Disallow dropping a group into itself or any of its descendants
                if (IsDescendant(src, targetGroup)) return;

                // OK to drop: we don't block locked destinations
                e.Effect = DragDropEffects.Move;
                treeGroups.SelectedNode = over;
                return;
            }
        }

        private void treeGroups_DragDrop(object? sender,  DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TreeNode))) return;

            var srcNode = (TreeNode)e.Data.GetData(typeof(TreeNode));
            if (srcNode == null) return;

            var clientPt = treeGroups.PointToClient(new Point(e.X, e.Y));
            var over = treeGroups.GetNodeAt(clientPt);
            if (over == null) return;

            // --- Moving a block (existing behavior) ---
            if (srcNode.Tag is BlockTag srcBt)
            {
                var (targetGroup, insertIndex) = ResolveDropTargetForBlocks(over, clientPt);
                if (targetGroup?.Tag is not GroupTag destGt) return;

                var currentParent = srcNode.Parent;

                // Reorder within same parent (UI only)
                if (currentParent == targetGroup)
                {
                    if (over == srcNode) return;
                    srcNode.Remove();
                    if (insertIndex > currentParent.Nodes.Count) insertIndex = currentParent.Nodes.Count;
                    currentParent.Nodes.Insert(insertIndex, srcNode);
                    treeGroups.SelectedNode = srcNode;
                    return;
                }

                // Reparent block to a new group (persist assoc)
                try
                {
                    UpdateBlockParentGroup(srcBt.BlockId, destGt.Id);

                    srcNode.Remove();
                    if (insertIndex > targetGroup.Nodes.Count) insertIndex = targetGroup.Nodes.Count;
                    targetGroup.Nodes.Insert(insertIndex, srcNode);

                    srcBt.ParentGroupId = destGt.Id;
                    srcNode.Tag = srcBt;

                    targetGroup.Expand();
                    treeGroups.SelectedNode = srcNode;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Move failed: {ex.Message}", "Drag & Drop",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            // --- Moving a group (NEW) ---
            if (srcNode.Tag is GroupTag srcGt)
            {
                // Safety: don't move locked/default groups
                if (srcGt.Locked || srcGt.Id == DEFAULT_GROUP_ID) return;

                var targetGroup = ResolveDropTargetForGroups(over);
                if (targetGroup?.Tag is not GroupTag destGt) return;

                // Prevent cycles: can't drop inside itself/descendant
                if (IsDescendant(srcNode, targetGroup)) return;

                try
                {
                    // Get next order under destination parent and persist parent/order
                    int nextOrder = GetNextGroupOrder(destGt.Id);
                    UpdateGroupParentAndOrder(srcGt.Id, destGt.Id, nextOrder);

                    // Update in-memory tag so InsertChildByGroupOrder can place correctly
                    srcGt.ParentId = destGt.Id;
                    srcGt.Order = nextOrder;
                    srcNode.Tag = srcGt;

                    // Move in UI (as ordered among other groups)
                    var oldParent = srcNode.Parent;
                    srcNode.Remove();
                    InsertChildByGroupOrder(targetGroup, srcNode);

                    destGt = (GroupTag)targetGroup.Tag;
                    targetGroup.Expand();
                    treeGroups.SelectedNode = srcNode;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Move failed: {ex.Message}", "Drag & Drop",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }





        private void UploadFileAssetForBlock(int blockId, string? blockNameForDefault)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select file to upload",
                Filter = "All Files (*.*)|*.*|DWG (*.dwg)|*.dwg|PDF (*.pdf)|*.pdf|Images (*.png;*.jpg)|*.png;*.jpg;*.jpeg|Docs (*.docx;*.xlsx)|*.docx;*.xlsx",
                Multiselect = false,
                CheckFileExists = true,
                DereferenceLinks = true,
                RestoreDirectory = true
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            // Resolve absolute path once and only use this
            string selectedPath = Path.GetFullPath(ofd.FileName);
            string ext = NormalizeExt(Path.GetExtension(selectedPath));
            string name = Path.GetFileName(selectedPath);

            // Read the file bytes robustly (avoid stale buffers)
            byte[] data;
            using (var fs = new FileStream(selectedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                data = ms.ToArray();
            }

            // Optional: tiny checksum to help you confirm it's a different file
            string checksum = "";
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                checksum = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").Substring(0, 12);
            }
            catch { /* ignore */ }

            // Optional description prompt
            using var dlg = new SimpleOneFieldDialog("Upload File", "Description (optional):");
            dlg.Value = $"{ext.ToUpperInvariant()} uploaded {DateTime.Now:g}" + (checksum != "" ? $" • {checksum}" : "");
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string desc = dlg.Value?.Trim();

            // Preview for images (DWG previews left null here; they’ll show file-type icon)
            byte[]? preview = null;
            if (ext is ".png" or ".jpg" or ".jpeg")
            {
                try
                {
                    using var img = System.Drawing.Image.FromFile(selectedPath);
                    using var resized = new Bitmap(img, new Size(256, 256));
                    using var msPrev = new MemoryStream();
                    resized.Save(msPrev, System.Drawing.Imaging.ImageFormat.Png);
                    preview = msPrev.ToArray();
                }
                catch { preview = null; }
            }

            using var conn = new IWCConn();
            conn.DBConnect();

            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        INSERT INTO dbo.Dwg_BlockAssets
            (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData, FileImage)
        VALUES
            (@bid,   @fn,      @ft,      @fd,            @fda,          @data,   @img);", conn.OpenConn);

            cmd.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
            cmd.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = name;
            cmd.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(ext) ? "(unknown)" : ext;
            cmd.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = string.IsNullOrWhiteSpace(desc) ? (object)DBNull.Value : desc;
            cmd.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

            // CRITICAL: bind varbinary(MAX) explicitly to avoid nvarchar coercion
            var pData = cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
            pData.Value = data;

            var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
            pImg.Value = (object)preview ?? DBNull.Value;

            cmd.ExecuteNonQuery();
            conn.DBClose();

            // refresh UI / cache for that block
            RefreshAssetsForBlock(blockId);
        }


        //private void UploadFileAssetForBlock(int blockId, string blockNameForDefault)
        //{
        //    using var ofd = new OpenFileDialog
        //    {
        //        Title = "Select file to upload",
        //        Filter = "All Files (*.*)|*.*",
        //        Multiselect = false
        //    };
        //    if (ofd.ShowDialog(this) != DialogResult.OK) return;

        //    string path = ofd.FileName;
        //    string ext = NormalizeExt(Path.GetExtension(path));
        //    string name = Path.GetFileName(path);

        //    // Optional description prompt
        //    using var dlg = new SimpleOneFieldDialog("Upload File", "Description (optional):");
        //    dlg.Value = $"{ext.ToUpperInvariant()} asset uploaded {DateTime.Now:g}";
        //    if (dlg.ShowDialog(this) != DialogResult.OK) return;
        //    string desc = dlg.Value?.Trim();

        //    // Read bytes for DB
        //    byte[] data = File.ReadAllBytes(path);
        //    if (data == null) data = Array.Empty<byte>(); // FileData is NOT NULL

        //    // Optional preview for images
        //    byte[]? preview = null;
        //    try
        //    {
        //        if (ext is ".png" or ".jpg" or ".jpeg")
        //        {
        //            using var img = System.Drawing.Image.FromFile(path);
        //            using var thumb = new Bitmap(img, new Size(256, 256));
        //            using var ms = new MemoryStream();
        //            thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        //            preview = ms.ToArray();
        //        }
        //    }
        //    catch { preview = null; }

        //    using var conn = new IWCConn();
        //    conn.DBConnect();

        //    using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        //INSERT INTO dbo.Dwg_BlockAssets
        //    (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData, FileImage)
        //VALUES
        //    (@bid,   @fn,      @ft,      @fd,             @fda,          @data,   @img);", conn.OpenConn);

        //    cmd.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
        //    cmd.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = name ?? (object)DBNull.Value;
        //    cmd.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(ext) ? "(unknown)" : ext;
        //    cmd.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = string.IsNullOrWhiteSpace(desc) ? (object)DBNull.Value : desc;
        //    cmd.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

        //    // IMPORTANT: bind varbinary explicitly
        //    var pData = cmd.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
        //    pData.Value = data; // byte[]

        //    var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
        //    pImg.Value = (object)preview ?? DBNull.Value; // byte[] or NULL

        //    cmd.ExecuteNonQuery();
        //    conn.DBClose();

        //    RefreshAssetsForBlock(blockId);
        //}

        private void RefreshAssetsForBlock(int blockId)
        {
            // Re‑load assets for one block only
            using var conn = new IWCConn();
            conn.DBConnect();

            var dt = new DataTable();
            using (var da = new SqlDataAdapter(@"
                SELECT ID, BlockID, FileName, FileType, FileDescription, FileDateAdded, FileImage, FileData, FileIsView, AssetLinkUrl
                FROM dbo.Dwg_BlockAssets
                WHERE BlockID = @bid
                ORDER BY FileName;", conn.OpenConn))
            {
                da.SelectCommand.Parameters.AddWithValue("@bid", blockId);
                da.Fill(dt);
            }
            conn.DBClose();

            var list = new List<AssetInfo>();
            foreach (DataRow r in dt.Rows)
            {
                list.Add(new AssetInfo
                {
                    Id = SafeGet<int>(r, "ID"),
                    BlockId = SafeGet<int>(r, "BlockID"),
                    FileName = SafeGet<string>(r, "FileName"),
                    FileExt = NormalizeExt(SafeGet<string>(r, "FileType")),
                    Description = SafeGet<string>(r, "FileDescription"),
                    DateAdded = r["FileDateAdded"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["FileDateAdded"]),
                    FileImageBytes = r["FileImage"] == DBNull.Value ? null : (byte[])r["FileImage"],
                    FileDataBytes = r["FileData"] == DBNull.Value ? null : (byte[])r["FileData"],
                    IsView = r.Table.Columns.Contains("FileIsView") && r["FileIsView"] != DBNull.Value
                            ? Convert.ToBoolean(r["FileIsView"]) : false,
                    LinkUrl = r.Table.Columns.Contains("AssetLinkUrl") && r["AssetLinkUrl"] != DBNull.Value
                    ? Convert.ToString(r["AssetLinkUrl"])
                    : null
                });
            }
            _assetsByBlockId[blockId] = list;

            // If the selected node is that block, refresh the list view
            if (treeGroups.SelectedNode?.Tag is BlockTag bt && bt.BlockId == blockId)
                PopulateAssetListForBlock(blockId, bt.Name);

            // Also refresh asset child nodes under that block node
            TreeNode blockNode = FindBlockNode(blockId);
            if (blockNode != null)
            {
                // remove current AssetTag children
                for (int i = blockNode.Nodes.Count - 1; i >= 0; i--)
                {
                    var t = blockNode.Nodes[i].Tag;
                    if (t is AssetTag || t is ViewsFolderTag) blockNode.Nodes.RemoveAt(i);
                }

                if (_assetsByBlockId.TryGetValue(blockId, out var assets))
                {
                    // Split views vs non-views
                    var viewAssets = assets.Where(a => a.IsView).ToList();
                    var otherAssets = assets.Where(a => !a.IsView).ToList();

                    // Add non-view assets directly under the block
                    foreach (var a in otherAssets)
                    {
                        blockNode.Nodes.Add(new TreeNode(a.FileName)
                        {
                            Tag = new AssetTag { AssetId = a.Id, BlockId = a.BlockId, FileName = a.FileName, FileExt = a.FileExt },
                            ImageKey = ImageKeyForExt(a.FileExt),
                            SelectedImageKey = ImageKeyForExt(a.FileExt)
                        });
                    }

                    // If there are views, create a "Views" folder and put them inside
                    if (viewAssets.Count > 0)
                    {
                        var viewsNode = new TreeNode("Views")
                        {
                            Tag = new ViewsFolderTag { BlockId = blockId },
                            ImageKey = ICON_FOLDER,
                            SelectedImageKey = ICON_FOLDER
                        };

                        foreach (var a in viewAssets)
                        {
                            viewsNode.Nodes.Add(new TreeNode(a.FileName)
                            {
                                Tag = new AssetTag { AssetId = a.Id, BlockId = a.BlockId, FileName = a.FileName, FileExt = a.FileExt },
                                ImageKey = ImageKeyForExt(a.FileExt),
                                SelectedImageKey = ImageKeyForExt(a.FileExt)
                            });
                        }

                        blockNode.Nodes.Add(viewsNode);
                        viewsNode.Expand();
                    }
                }

                //if (_assetsByBlockId.TryGetValue(blockId, out var assets))
                //{
                //    foreach (var a in assets)
                //    {
                //        blockNode.Nodes.Add(new TreeNode(a.FileName)
                //        {
                //            Tag = new AssetTag { AssetId = a.Id, BlockId = a.BlockId, FileName = a.FileName, FileExt = a.FileExt },
                //            ImageKey = ImageKeyForExt(a.FileExt),
                //            SelectedImageKey = ImageKeyForExt(a.FileExt)
                //        });
                //    }
                //}
                UpdateBlockNodeComponentBadge(blockNode);
                UpdateBlockNodeVisual(blockNode, blockId); // set Component vs Assembly icon
                blockNode.Expand();
            }
        }

        private TreeNode? FindBlockNode(int blockId)
        {
            foreach (TreeNode g in treeGroups.Nodes)
            {
                var n = FindBlockNodeRecursive(g, blockId);
                if (n != null) return n;
            }
            return null;
        }
        private TreeNode? FindBlockNodeRecursive(TreeNode node, int blockId)
        {
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag is BlockTag bt && bt.BlockId == blockId) return child;
                var hit = FindBlockNodeRecursive(child, blockId);
                if (hit != null) return hit;
            }
            return null;
        }
        private sealed class SimpleTwoFieldDialog : Form
        {
            private TextBox t1, t2;
            private TextBox? t3;
            public string? Value1 { get => t1.Text; set => t1.Text = value ?? ""; }
            public string? Value2 { get => t2.Text; set => t2.Text = value ?? ""; }
            public string? Value3 { get => t3?.Text; set { if (t3 != null) t3.Text = value ?? ""; } }

            /// <param name="label3">Optional 3rd line. If null/empty, dialog shows 2 fields.</param>
            /// <param name="multilineSecond">If true, the second textbox is multiline.</param>
            public SimpleTwoFieldDialog(string title, string label1, string label2, string? label3 = null, bool multilineSecond = false)
            {
                Text = title;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = MaximizeBox = false;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                bool hasThird = !string.IsNullOrWhiteSpace(label3);
                int rows = hasThird ? 4 : 3;

                var table = new TableLayoutPanel { ColumnCount = 2, RowCount = rows, Padding = new Padding(10), AutoSize = true };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var l1 = new Label { Text = label1, AutoSize = true, Anchor = AnchorStyles.Left };
                var l2 = new Label { Text = label2, AutoSize = true, Anchor = AnchorStyles.Left };

                t1 = new TextBox { Width = 320, Anchor = AnchorStyles.Left | AnchorStyles.Right };
                t2 = new TextBox { Width = 320, Anchor = AnchorStyles.Left | AnchorStyles.Right };

                if (multilineSecond)
                {
                    t2.Multiline = true;
                    t2.AcceptsReturn = true;
                    t2.ScrollBars = ScrollBars.Vertical;
                    t2.Height = 80;
                }

                table.Controls.Add(l1, 0, 0); table.Controls.Add(t1, 1, 0);
                table.Controls.Add(l2, 0, 1); table.Controls.Add(t2, 1, 1);

                if (hasThird)
                {
                    var l3 = new Label { Text = label3, AutoSize = true, Anchor = AnchorStyles.Left };
                    t3 = new TextBox { Width = 160, Anchor = AnchorStyles.Left };
                    t3.CharacterCasing = CharacterCasing.Upper;
                    t3.MaxLength = 5;
                    t3.KeyPress += (s, e) =>
                    {
                        if (!char.IsControl(e.KeyChar) && !char.IsLetterOrDigit(e.KeyChar))
                            e.Handled = true;
                    };

                    table.Controls.Add(l3, 0, 2);
                    table.Controls.Add(t3, 1, 2);
                }

                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var btns = new FlowLayoutPanel { FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, AutoSize = true };
                btns.Controls.Add(ok); btns.Controls.Add(cancel);

                table.Controls.Add(btns, 0, hasThird ? 3 : 2);
                table.SetColumnSpan(btns, 2);

                Controls.Add(table);
                AcceptButton = ok;
                CancelButton = cancel;
            }
        }


        private sealed class SimpleOneFieldDialog : Form
        {
            private TextBox t;
            public string? Value { get => t.Text; set => t.Text = value; }

            public SimpleOneFieldDialog(string title, string label)
            {
                Text = title;
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MinimizeBox = MaximizeBox = false;
                AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var table = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Padding = new Padding(10), AutoSize = true };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
                t = new TextBox { Width = 320 };

                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

                var btns = new FlowLayoutPanel { FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, AutoSize = true };
                btns.Controls.Add(ok); btns.Controls.Add(cancel);

                table.Controls.Add(l, 0, 0); table.Controls.Add(t, 1, 0);
                table.Controls.Add(btns, 0, 1); table.SetColumnSpan(btns, 2);

                Controls.Add(table);
                AcceptButton = ok; CancelButton = cancel;
            }
        }
        private void TreeGroups_NodeMouseDoubleClick(object? sender,  TreeNodeMouseClickEventArgs e)
        {
            if (e.Node?.Tag is AssetTag at)
            {
                var ai = GetAssetInfoById(at.BlockId, at.AssetId);
                if (ai == null) return;

                // Open URL assets on double-click
                if (!string.IsNullOrWhiteSpace(ai.LinkUrl))
                {
                    OpenUrl(ai.LinkUrl);
                    return;
                }

                // Fall back to file-type handling
                string? ext = ai.FileExt;
                if (string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(ai.FileName))
                    ext = Path.GetExtension(ai.FileName);
                ext = (ext ?? string.Empty).Trim().ToLowerInvariant();
                if (!ext.StartsWith(".")) ext = "." + ext;

                if (ext == ".dwg")
                {
                    try { InsertDwgAsset(ai, e.Node); }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Insert failed: {ex.Message}", "Block Browser V2",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else
                {
                    try { OpenNonDwgAsset(ai); }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Open failed: {ex.Message}", "Block Browser V2",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                return;
            }

            // Existing behavior for component blocks (kept as-is)
            if (e.Node?.Tag is BlockTag bt && bt.IsComponent)
            {
                if (_assetsByBlockId.TryGetValue(bt.BlockId, out var list) && list?.Count == 1)
                {
                    var ai = list[0];

                    if (!string.IsNullOrWhiteSpace(ai.LinkUrl))
                    {
                        OpenUrl(ai.LinkUrl);
                        return;
                    }

                    if (string.Equals(Path.GetExtension(ai.FileName), ".dwg", StringComparison.OrdinalIgnoreCase))
                    {
                        try { InsertDwgAsset(ai, e.Node); }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Insert failed: {ex.Message}", "Component Insert",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    else
                    {
                        try { OpenNonDwgAsset(ai); }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Open failed: {ex.Message}", "Component Insert",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
        }


        private AssetInfo? GetAssetInfoById(int blockId, int assetId)
        {
            if (_assetsByBlockId.TryGetValue(blockId, out var list))
                return list.FirstOrDefault(a => a.Id == assetId);
            return null;
        }

        private static bool IsDynamic(BlockReference br, out ObjectId defId)
        {
            defId = ObjectId.Null;

            if (br == null) return false;

            // For dynamic refs, the “definition” is DynamicBlockTableRecord.
            if (!br.DynamicBlockTableRecord.IsNull)
            {
                defId = br.DynamicBlockTableRecord;
                return true;
            }

            // Non-dynamic or definition already the plain BlockTableRecord
            defId = br.BlockTableRecord;
            return false;
        }

        //private static string ExportBlockDefinition(Database db, ObjectId btrId, Point3d basePt, string outName)
        //{
        //    // Create a new side Database containing only that BlockTableRecord (true definition)

        //    var ids = new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection { btrId };
        //    using var defDb = db.Wblock(ids, basePt);

        //    string tempPath = Path.Combine(Path.GetTempPath(), $"{outName}_{Guid.NewGuid():N}.dwg");
        //    defDb.SaveAs(tempPath, Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
        //    return tempPath;
        //}

        private static string ExportBlockDefinition(Database srcDb, ObjectId defBtrId, Autodesk.AutoCAD.Geometry.Point3d basePt, string outName)
        {
            // 1) Create an empty destination database
            using var dstDb = new Database(true, true);

            // 2) Clone the source definition into the destination DB
            var ids = new ObjectIdCollection { defBtrId };
            var idMap = new IdMapping();
            srcDb.WblockCloneObjects(
                ids,
                dstDb.BlockTableId,
                idMap,
                DuplicateRecordCloning.Ignore,
                false);

            // 3) Get the newly created BTR id in the destination and set its origin
            var newBtrId = idMap[defBtrId].Value;
            using (var tr = dstDb.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(newBtrId, OpenMode.ForWrite);
                btr.Origin = basePt;   // mimic the old Wblock(basePt) behavior
                tr.Commit();
            }

            // 4) Save the one-definition DB to a temp DWG
            string tempPath = Path.Combine(Path.GetTempPath(), $"{outName}_{Guid.NewGuid():N}.dwg");
            dstDb.SaveAs(tempPath, DwgVersion.Current);
            return tempPath;
        }

        private string ExportBlockSnapshotFromReference(Database db, BlockReference br, string blockName, Point3d basePt)
        {
            // Make a temp, single-definition DWG that represents the current visual state
            // (you already have this pattern wired with MakeWBlock)
            string tempDwgPath = Path.Combine(Path.GetTempPath(), $"{blockName}_{Guid.NewGuid():N}.dwg");

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                // Create a new BTR and copy the exploded contents of the reference
                var btrNew = new BlockTableRecord { Name = blockName, Origin = basePt };
                bt.UpgradeOpen();
                ObjectId newBtrId = bt.Add(btrNew);
                tr.AddNewlyCreatedDBObject(btrNew, true);

                // Explode the BR into new BTR (baked geometry)
                var objs = new DBObjectCollection();
                br.Explode(objs);
                foreach (DBObject dbo in objs)
                {
                    if (dbo is Entity ent)
                    {
                        btrNew.AppendEntity(ent);
                        tr.AddNewlyCreatedDBObject(ent, true);
                    }
                    else
                    {
                        dbo.Dispose();
                    }
                }

                tr.Commit();

                // WBLOCK the new definition out (you already use this)
                var ids = new ObjectIdCollection { newBtrId };
                BlockLibraryHelper.WblockToFile(ids, basePt, blockName, tempDwgPath);
            }

            return tempDwgPath;
        }

        private ObjectId ImportDynamicBlockPreserving(string srcDwgPath, string desiredName, bool insertBind = true)
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;

            // Attach as XREF first — this brings *everything* the block needs
            ObjectId xrefBtrId;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                // Use a temporary xref name to avoid collisions
                string xrefName = $"{desiredName}_XREF_{Guid.NewGuid():N}";
                xrefBtrId = db.AttachXref(srcDwgPath, xrefName);
                tr.Commit();
            }

            // Bind the xref using INSERT-bind so all dictionaries/symbols merge properly
            var idsToBind = new ObjectIdCollection { xrefBtrId };
            db.BindXrefs(idsToBind, insertBind); // insertBind=true == "Bind as Insert"

            // After bind, the xref becomes a normal block definition.
            // Its name will be the former xref name.
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                // The bound block name equals the xref name (we can rename later if needed)
                string? boundName = null;
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsLayout && btr.Name.StartsWith($"{desiredName}_XREF_", StringComparison.OrdinalIgnoreCase))
                    {
                        boundName = btr.Name;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(boundName))
                    throw new Exception("Failed to locate bound block after XREF bind.");

                var resultId = bt[boundName];
                tr.Commit();
                return resultId;
            }
        }
        //---------------------------------------------------------------------------------------------------------------------------------
        //
        //8-18 - works but doesn't import nested dynamic blocks properly
        //
        //----------------------------------------------------------------------------------------------------------------------------------
        //    private void ImportBlockDefinitionFromFile(
        //Autodesk.AutoCAD.DatabaseServices.Database destDb,
        //string sourceDwgPath,
        //string desiredName,
        //Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning drcMode)
        //    {
        //        if (string.IsNullOrWhiteSpace(sourceDwgPath))
        //            throw new ArgumentException("Source DWG path is empty.", nameof(sourceDwgPath));
        //        if (string.IsNullOrWhiteSpace(desiredName))
        //            throw new ArgumentException("Desired name is empty.", nameof(desiredName));

        //        // 1) Open source DWG
        //        using var srcDb = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
        //        srcDb.ReadDwgFile(
        //            sourceDwgPath,
        //            Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndAllShare,
        //            true, null);
        //        srcDb.CloseInput(true);

        //        // 2) Find a suitable source BlockTableRecord (prefer desiredName; else first non-layout, non-anon)
        //        Autodesk.AutoCAD.DatabaseServices.ObjectId srcBtrId = Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;

        //        using (var tr = srcDb.TransactionManager.StartTransaction())
        //        {
        //            var sbt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
        //                      tr.GetObject(srcDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

        //            if (sbt.Has(desiredName))
        //            {
        //                srcBtrId = sbt[desiredName];
        //            }
        //            else
        //            {
        //                foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in sbt)
        //                {
        //                    var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
        //                              tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        //                    if (btr.IsLayout) continue;
        //                    if (btr.Name.StartsWith("*")) continue; // skip anonymous blocks
        //                    srcBtrId = id;
        //                    break;
        //                }
        //            }

        //            if (srcBtrId.IsNull)
        //                throw new Exception("No importable block definition found in the source DWG.");

        //            tr.Commit();
        //        }

        //        // 3) Create a clean WORK database that will contain only that one definition
        //        using var workDb = new Autodesk.AutoCAD.DatabaseServices.Database(true, true);

        //        // Clone the chosen src definition into the work DB
        //        var ids = new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection { srcBtrId };
        //        var map = new Autodesk.AutoCAD.DatabaseServices.IdMapping();
        //        srcDb.WblockCloneObjects(
        //            ids,
        //            workDb.BlockTableId,
        //            map,
        //            Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Ignore,
        //            false);

        //        // Get the new BTR id in the work DB
        //        var workBtrId = map[srcBtrId].Value;

        //        // 4) **Rename inside the work DB** to EXACTLY desiredName
        //        using (var trW = workDb.TransactionManager.StartTransaction())
        //        {
        //            var wbt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
        //                      trW.GetObject(workDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

        //            // If a conflicting name exists in the work DB (unlikely; it's empty except this one),
        //            // rename the conflicting one out of the way.
        //            if (wbt.Has(desiredName))
        //            {
        //                var conflict = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
        //                               trW.GetObject(wbt[desiredName], Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
        //                conflict.Name = $"{desiredName}_CONFLICT_{Guid.NewGuid():N}";
        //            }

        //            var wBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
        //                       trW.GetObject(workBtrId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
        //            wBtr.Name = desiredName;   // <-- critical for Replace to work

        //            trW.Commit();
        //        }

        //        // 5) Clone from WORK DB into the DEST DB using caller’s mode (Replace/Ignore)
        //        var ids2 = new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection { workBtrId };
        //        var map2 = new Autodesk.AutoCAD.DatabaseServices.IdMapping();
        //        workDb.WblockCloneObjects(
        //            ids2,
        //            destDb.BlockTableId,
        //            map2,
        //            drcMode,   // Replace if user chose "Overwrite"; Ignore if creating new
        //            false);

        //        // Done. If drcMode == Replace and the dest already had "desiredName",
        //        // its definition is now swapped with the work DB's one.
        //    }


        //    private ObjectId ImportBlockDefinitionWithPrompt_DynamicSafe(Database targetDb, Editor ed, string dwgPath, string desiredName)
        //    {
        //        using (var tr = targetDb.TransactionManager.StartTransaction())
        //        {
        //            var bt = (BlockTable)tr.GetObject(targetDb.BlockTableId, OpenMode.ForRead);
        //            bool exists = bt.Has(desiredName);
        //            tr.Commit();

        //            if (!exists)
        //            {
        //                // No conflict: import via xref bind, then rename to desiredName
        //                var newBtrId = ImportDynamicBlockPreserving(dwgPath, desiredName, insertBind: true);
        //                RenameBlock(newBtrId, desiredName); // tidy up the name
        //                return GetBlockByName(desiredName);
        //            }

        //            // Exists — ask user
        //            var pko = new PromptKeywordOptions($"\nBlock '{desiredName}' already exists. ")
        //            {
        //                Message = "\nChoose action"
        //            };
        //            pko.Keywords.Add("Overwrite");
        //            pko.Keywords.Add("Keep");
        //            pko.Keywords.Default = "Keep";
        //            var pkr = ed.GetKeywords(pko);
        //            if (pkr.Status != PromptStatus.OK || pkr.StringResult == "Keep")
        //            {
        //                return GetBlockByName(desiredName);
        //            }

        //            // Overwrite: import as a *temporary* new def (dynamic-preserving), 
        //            // retarget all existing references to the new def, then rename.
        //            var importedTempId = ImportDynamicBlockPreserving(dwgPath, desiredName, insertBind: true); // name like desiredName_XREF_GUID
        //                                                                                                       // Rename temp to a predictable temporary name
        //            string tempName = $"{desiredName}_NEW_{Guid.NewGuid():N}";
        //            RenameBlock(importedTempId, tempName);

        //            var oldId = GetBlockByName(desiredName);
        //            var newId = GetBlockByName(tempName);

        //            // Switch all existing references from oldId -> newId
        //            SwitchAllReferences(oldId, newId);

        //            // Erase the old definition (now unreferenced)
        //            EraseBlockDefinition(oldId);

        //            // Finally rename the new one to the desiredName
        //            RenameBlock(newId, desiredName);

        //            return GetBlockByName(desiredName);
        //        }

        //        // Local helpers
        //        ObjectId GetBlockByName(string name)
        //        {
        //            using var tr2 = targetDb.TransactionManager.StartTransaction();
        //            var bt2 = (BlockTable)tr2.GetObject(targetDb.BlockTableId, OpenMode.ForRead);
        //            var id = bt2[name];
        //            tr2.Commit();
        //            return id;
        //        }
        //    }

        // ctlIWCBlockBrowserV2.cs  — definitive importer used by InsertDwgAsset and elsewhere
        //private void ImportBlockDefinitionFromFile(
        //   Autodesk.AutoCAD.DatabaseServices.Database destDb,
        //   string sourceDwgPath,
        //   string desiredName,
        //   Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning drcMode)
        //{
        //    if (destDb == null) throw new ArgumentNullException(nameof(destDb));
        //    if (string.IsNullOrWhiteSpace(sourceDwgPath) || !System.IO.File.Exists(sourceDwgPath))
        //        throw new System.IO.FileNotFoundException("Source DWG not found.", sourceDwgPath);

        //    // Early-out if we're ignoring duplicates and it already exists
        //    if (!string.IsNullOrWhiteSpace(desiredName) &&
        //        drcMode == Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Ignore)
        //    {
        //        using var chk = destDb.TransactionManager.StartTransaction();
        //        var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
        //                 chk.GetObject(destDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        //        if (bt.Has(desiredName)) { chk.Commit(); return; }
        //        chk.Commit();
        //    }

        //    // Open source DWG
        //    using var srcDb = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
        //    srcDb.ReadDwgFile(
        //        sourceDwgPath,
        //        Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndAllShare,
        //        true, null);
        //    srcDb.CloseInput(true);

        //    Autodesk.AutoCAD.DatabaseServices.ObjectId rootBtrId = Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;
        //    string? rootName = null;

        //    // Declare closure OUTSIDE so it's in scope after the transaction
        //    Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection closure = null;

        //    // Pick root and build closure
        //    using (var tr = srcDb.TransactionManager.StartTransaction())
        //    {
        //        var sbt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
        //                  tr.GetObject(srcDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

        //        // Prefer desiredName, else first non-layout, non-anonymous
        //        if (!string.IsNullOrWhiteSpace(desiredName) && sbt.Has(desiredName))
        //        {
        //            rootBtrId = sbt[desiredName];
        //            rootName = desiredName;
        //        }
        //        else
        //        {
        //            foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in sbt)
        //            {
        //                var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
        //                          tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
        //                if (btr.IsLayout) continue;
        //                if (btr.Name.StartsWith("*", StringComparison.Ordinal)) continue; // skip anonymous
        //                rootBtrId = id;
        //                rootName = btr.Name;
        //                break;
        //            }
        //        }

        //        if (rootBtrId.IsNull)
        //            throw new Exception("No importable block definition found in the source DWG.");

        //        // Build closure (root + nested + dynamic anonymous)
        //        closure = BuildBlockDefinitionClosure(srcDb, rootBtrId, tr);

        //        tr.Commit();
        //    }

        //    // Clone closure into destination (definitions only) — declare 'map' once here
        //    var map = new Autodesk.AutoCAD.DatabaseServices.IdMapping();
        //    destDb.WblockCloneObjects(
        //        closure,
        //        destDb.BlockTableId,
        //        map,
        //        drcMode,        // Replace to redefine, Ignore to keep existing
        //        false);

        //    // Optional: rename imported root to desiredName if needed
        //    if (!string.IsNullOrWhiteSpace(desiredName))
        //    {
        //        using var trd = destDb.TransactionManager.StartTransaction();
        //        var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
        //                 trd.GetObject(destDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

        //        // If Replace was used, desiredName may already exist; otherwise rename the imported root
        //        if (!bt.Has(desiredName))
        //        {
        //            Autodesk.AutoCAD.DatabaseServices.ObjectId importedRootId =
        //                map.Contains(rootBtrId) && map[rootBtrId].IsCloned
        //                ? map[rootBtrId].Value
        //                : Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;

        //            if (!importedRootId.IsNull)
        //            {
        //                var btrw = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
        //                           trd.GetObject(importedRootId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
        //                btrw.Name = desiredName;
        //            }
        //        }

        //        trd.Commit();
        //    }
        //}

        private void ImportBlockDefinitionFromFile(
    Autodesk.AutoCAD.DatabaseServices.Database destDb,
    string sourceDwgPath,
    string desiredName,
    Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning drcMode)
        {
            if (destDb == null) throw new ArgumentNullException(nameof(destDb));
            if (string.IsNullOrWhiteSpace(sourceDwgPath) || !System.IO.File.Exists(sourceDwgPath))
                throw new System.IO.FileNotFoundException("Source DWG not found.", sourceDwgPath);

            // Early out if we should ignore and dest already has it
            if (!string.IsNullOrWhiteSpace(desiredName) &&
                drcMode == Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Ignore)
            {
                using var chk = destDb.TransactionManager.StartTransaction();
                var btChk = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                            chk.GetObject(destDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                if (btChk.Has(desiredName)) { chk.Commit(); return; }
                chk.Commit();
            }

            // Open source DWG
            using var srcDb = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
            srcDb.ReadDwgFile(
                sourceDwgPath,
                Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndAllShare,
                true, null);
            srcDb.CloseInput(true);

            // We'll collect all definitions to clone here
            var defsToClone = new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection();

            // We'll also snapshot any Model Space composition so we can rebuild it into a new definition if needed
            //var msSnapshots = new System.Collections.Generic.List<(string Name, Autodesk.AutoCAD.Geometry.Point3d Pos, Autodesk.AutoCAD.Geometry.Scale3d Scale, double Rot, Autodesk.AutoCAD.Geometry.Vector3d Normal)>();
            var msSnapshots = new System.Collections.Generic.List<ModelRefSnapshot>();

            // Try to pick a reasonable root definition (by desiredName, file stem, or a heuristic)
            Autodesk.AutoCAD.DatabaseServices.ObjectId rootBtrId = Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;
            string? rootName = null;

            using (var tr = srcDb.TransactionManager.StartTransaction())
            {
                var sbt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                          tr.GetObject(srcDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

                // 1) Choose a root def if available
                if (!string.IsNullOrWhiteSpace(desiredName) && sbt.Has(desiredName))
                {
                    rootBtrId = sbt[desiredName];
                    rootName = desiredName;
                }
                else
                {
                    var stem = System.IO.Path.GetFileNameWithoutExtension(sourceDwgPath) ?? "";
                    if (stem.Length > 0 && sbt.Has(stem))
                    {
                        rootBtrId = sbt[stem];
                        rootName = stem;
                    }
                    else
                    {
                        // Heuristic: pick non-layout BTR with most nested refs
                        Autodesk.AutoCAD.DatabaseServices.ObjectId best = Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;
                        int bestScore = -1; string? bestName = null;
                        foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in sbt)
                        {
                            var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                      tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                            if (btr.IsLayout) continue;
                            if (btr.Name.StartsWith("*", StringComparison.Ordinal)) continue;
                            int score = 0;
                            foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId entId in btr)
                                if (tr.GetObject(entId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.BlockReference) score++;
                            if (score > bestScore) { bestScore = score; best = id; bestName = btr.Name; }
                        }
                        if (!best.IsNull) { rootBtrId = best; rootName = bestName; }
                    }
                }

                // 2) If we found a root definition, add its full closure
                if (!rootBtrId.IsNull)
                {
                    var closure = BuildBlockDefinitionClosure(srcDb, rootBtrId, tr);
                    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in closure)
                        defsToClone.Add(id);
                }

                // 3) Scan Model Space for a composition and (a) snapshot refs, (b) add their definition closures
                var msId = Autodesk.AutoCAD.DatabaseServices.SymbolUtilityServices.GetBlockModelSpaceId(srcDb);
                var ms = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(msId, OpenMode.ForRead);

                // foundMsRefs tracks whether any refs exist; kept for future use
                _ = false; // placeholder
                foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId entId in ms)
                {
                    if (tr.GetObject(entId, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.BlockReference br)
                    {

                        string? defName = null;
                        try
                        {
                            var def = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                      tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                            if (!def.IsLayout) defName = def.Name;
                        }
                        catch { /* ignore */ }

                        string? dynBaseName = null;
                        try
                        {
                            var baseId = br.DynamicBlockTableRecord;
                            if (!baseId.IsNull)
                            {
                                var baseBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                               tr.GetObject(baseId, OpenMode.ForRead);
                                dynBaseName = baseBtr.Name; // non-anonymous
                            }
                        }
                        catch { /* older versions may not expose */ }

                        if (!string.IsNullOrWhiteSpace(defName) || !string.IsNullOrWhiteSpace(dynBaseName))
                        {
                            msSnapshots.Add(new ModelRefSnapshot
                            {
                                DefName = defName,
                                DynamicBaseName = dynBaseName,
                                Pos = br.Position,
                                Scale = br.ScaleFactors,
                                Rot = br.Rotation,
                                Normal = br.Normal
                            });
                        }

                        // add closure for the referenced definition as you already do...
                        var defId = br.BlockTableRecord;
                        if (!defId.IsNull)
                        {
                            var nestedClosure = BuildBlockDefinitionClosure(srcDb, defId, tr);
                            foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in nestedClosure)
                                if (!defsToClone.Contains(id)) defsToClone.Add(id);
                        }
                    }
                }

                tr.Commit();
            }

            // 4) Clone *all* collected definitions into destination (no Xrefs, no Insert)
            if (defsToClone.Count > 0)
            {
                var map = new Autodesk.AutoCAD.DatabaseServices.IdMapping();
                destDb.WblockCloneObjects(
                    defsToClone,
                    destDb.BlockTableId,
                    map,
                    drcMode,   // Replace redefines; Ignore keeps existing
                    false);
            }

            // 5) Ensure a top definition with 'desiredName' exists in destination.
            using var trd = destDb.TransactionManager.StartTransaction();
            var bt = (Autodesk.AutoCAD.DatabaseServices.BlockTable)
                     trd.GetObject(destDb.BlockTableId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

            bool hasDesired = bt.Has(desiredName);

            // CASE A: exactly one model-space ref in source -> rename that imported child to desiredName
            if (!hasDesired && msSnapshots.Count == 1)
            {
                var snap = msSnapshots[0];

                // Prefer the dynamic base (stable, non-anonymous). Fallback to the ref'd def name.
                string candidate = !string.IsNullOrWhiteSpace(snap.DynamicBaseName) && !snap.DynamicBaseName.StartsWith("*")
                                   ? snap.DynamicBaseName
                                   : snap.DefName;

                if (!string.IsNullOrWhiteSpace(candidate) && bt.Has(candidate))
                {
                    // If desiredName already exists and we're in Replace mode, move it out of the way first
                    if (bt.Has(desiredName))
                    {
                        if (drcMode == Autodesk.AutoCAD.DatabaseServices.DuplicateRecordCloning.Ignore)
                        {
                            // honor Ignore: leave existing and do nothing; caller inserts existing desiredName
                        }
                        else
                        {
                            var oldId = bt[desiredName];
                            var oldBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                         trd.GetObject(oldId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
                            oldBtr.Name = $"{desiredName}._IWC_OLD_{Guid.NewGuid():N}".Replace(":", "_");
                        }
                    }

                    // Rename candidate to desiredName (this makes InsertDwgAsset work without wrappers)
                    bt.UpgradeOpen();
                    var candId = bt[candidate];
                    var btrw = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                 trd.GetObject(candId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite);
                    btrw.Name = desiredName;
                    hasDesired = true;
                }
            }

            // CASE B: multiple model-space refs or CASE A failed -> build wrapper definition
            if (!hasDesired && msSnapshots.Count > 0)
            {
                bt.UpgradeOpen();
                var newDef = new Autodesk.AutoCAD.DatabaseServices.BlockTableRecord { Name = desiredName };
                var newDefId = bt.Add(newDef);
                trd.AddNewlyCreatedDBObject(newDef, true);

                foreach (var snap in msSnapshots)
                {
                    // choose the child def to reference: prefer dynamic base if available
                    string childName = !string.IsNullOrWhiteSpace(snap.DynamicBaseName) && bt.Has(snap.DynamicBaseName)
                                       ? snap.DynamicBaseName
                                       : snap.DefName;

                    if (string.IsNullOrWhiteSpace(childName) || !bt.Has(childName)) continue;

                    var childId = bt[childName];

                    var br = new Autodesk.AutoCAD.DatabaseServices.BlockReference(snap.Pos, childId)
                    {
                        Rotation = snap.Rot,
                        ScaleFactors = snap.Scale
                    };
                    try { br.Normal = snap.Normal; } catch { /* ignore */ }

                    newDef.AppendEntity(br);
                    trd.AddNewlyCreatedDBObject(br, true);
                }
            }

            trd.Commit();

        }






        private void RenameBlock(ObjectId btrId, string newName)
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
            btr.Name = newName;
            tr.Commit();
        }

        private void SwitchAllReferences(ObjectId oldBtrId, ObjectId newBtrId)
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();

            var oldBtr = (BlockTableRecord)tr.GetObject(oldBtrId, OpenMode.ForRead);
            var refs = oldBtr.GetBlockReferenceIds(true, false);

            foreach (ObjectId brId in refs)
            {
                var br = (BlockReference)tr.GetObject(brId, OpenMode.ForWrite);
                br.BlockTableRecord = newBtrId; // retarget to the new definition
            }

            tr.Commit();
        }

        private void EraseBlockDefinition(ObjectId btrId)
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using var tr = db.TransactionManager.StartTransaction();
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);

            // Ensure it has no refs left
            if (btr.GetBlockReferenceIds(true, false).Count == 0)
            {
                btr.Erase();
                tr.Commit();
            }
            else
            {
                tr.Abort();
                throw new Exception("Cannot erase old block definition; it still has references.");
            }
        }
        private void DeleteGroupNode(TreeNode groupNode)
        {
            if (groupNode?.Tag is not GroupTag gt) return;

            // Re-check DB-side lock and child groups to be safe
            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                bool locked;
                int childCount;

                using (var chk = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT GroupLock FROM dbo.Dwg_BlockGroups WHERE ID = @id;", conn.OpenConn))
                {
                    chk.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = gt.Id;
                    var v = chk.ExecuteScalar();
                    locked = v != null && v != DBNull.Value && Convert.ToBoolean(v);
                }

                if (locked || gt.Id == DEFAULT_GROUP_ID)
                {
                    MessageBox.Show("This node is locked and cannot be deleted.", "Delete Node",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    conn.DBClose();
                    return;
                }

                using (var chk2 = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT COUNT(*) FROM dbo.Dwg_BlockGroups WHERE GroupParent = @id;", conn.OpenConn))
                {
                    chk2.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = gt.Id;
                    childCount = Convert.ToInt32(chk2.ExecuteScalar());
                }

                if (childCount > 0)
                {
                    MessageBox.Show("This node has child nodes. Delete or move them first.", "Delete Node",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    conn.DBClose();
                    return;
                }

                var confirm = MessageBox.Show(
                    $"Delete node '{groupNode.Text}'?\n\nBlocks/Assets will NOT be deleted; they will reappear under 'Block Library' on refresh.",
                    "Delete Node",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);

                if (confirm != DialogResult.OK)
                {
                    conn.DBClose();
                    return;
                }

                using var tx = conn.OpenConn.BeginTransaction();

                // Remove associations for this group (do NOT delete blocks or assets)
                using (var cmdAssoc = new Microsoft.Data.SqlClient.SqlCommand(
                    "DELETE FROM dbo.Dwg_BlockGroups_Assoc WHERE GroupID = @gid;", conn.OpenConn, tx))
                {
                    cmdAssoc.Parameters.Add("@gid", System.Data.SqlDbType.Int).Value = gt.Id;
                    cmdAssoc.ExecuteNonQuery();
                }

                // Delete the group itself
                using (var cmdDel = new Microsoft.Data.SqlClient.SqlCommand(
                    "DELETE FROM dbo.Dwg_BlockGroups WHERE ID = @gid;", conn.OpenConn, tx))
                {
                    cmdDel.Parameters.Add("@gid", System.Data.SqlDbType.Int).Value = gt.Id;
                    cmdDel.ExecuteNonQuery();
                }

                tx.Commit();
                conn.DBClose();

                // Refresh the whole tree so orphans are caught by "Block Library"
                LoadGroupsAndBlocks();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed: {ex.Message}", "Block Browser V2",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        //---------------------------------------------------------------------------------------------------------------------
        //ADDED TEST FOR HANDLING DYNAMIC BLOCKS BETTER WHEN CREATING ASSETS
        // Get the real/base name for a block reference (dynamic-safe)
        private static string GetBaseBlockName(Autodesk.AutoCAD.DatabaseServices.BlockReference br, Autodesk.AutoCAD.DatabaseServices.Transaction tr)
        {
            var btrId = !br.DynamicBlockTableRecord.IsNull ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)tr.GetObject(btrId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
            return btr.Name;
        }

        // Export one BlockTableRecord "as-is" to a DWG using Wblock on the BTR id.
        // This preserves dynamic dictionaries/graph more reliably than exploding or re-building.
        //----------------------------------------------------------------------------------------------------------------------
        //
        //8-18 - works but does not handle dynamic blocks well for nested blocks
        //
        //----------------------------------------------------------------------------------------------------------------------
        //private static string ExportBlockDefinitionAsIs(Database srcDb, ObjectId defBtrId, string outName)
        //{
        //    // Validate: we must be cloning a real BlockTableRecord (not Model/Paper layouts)
        //    using (var tr = srcDb.TransactionManager.StartTransaction())
        //    {
        //        var btr = tr.GetObject(defBtrId, OpenMode.ForRead) as BlockTableRecord
        //                  ?? throw new System.Exception("Export failed: ObjectId is not a BlockTableRecord.");

        //        if (btr.IsLayout)
        //            throw new System.Exception("Export failed: Cannot export a layout BlockTableRecord.");

        //        tr.Commit();
        //    }

        //    // Create a clean destination drawing
        //    using var dstDb = new Database(true, true);

        //    // Clone the definition into the destination DB’s BlockTable (not ModelSpace)
        //    var ids = new ObjectIdCollection { defBtrId };
        //    var idMap = new IdMapping();
        //    srcDb.WblockCloneObjects(
        //        ids,
        //        dstDb.BlockTableId,                     // clone as a definition
        //        idMap,
        //        DuplicateRecordCloning.Ignore,
        //        deferTranslation: false);

        //    // Save the one-definition drawing
        //    string fileSafe = string.Concat((outName ?? "Asset").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        //    string tempPath = Path.Combine(Path.GetTempPath(), $"{fileSafe}_{System.Guid.NewGuid():N}.dwg");
        //    dstDb.SaveAs(tempPath, DwgVersion.Current);
        //    return tempPath;
        //}

        // ctlIWCBlockBrowserV2.cs
        // REPLACE the body of ExportBlockDefinitionAsIs with this:

        private static string ExportBlockDefinitionAsIs(
    Autodesk.AutoCAD.DatabaseServices.Database srcDb,
    Autodesk.AutoCAD.DatabaseServices.ObjectId defBtrId,
    string outName)
        {
            // Validate and grab the true block name + origin
            string defName;
            Autodesk.AutoCAD.Geometry.Point3d defOrigin;

            using (var tr = srcDb.TransactionManager.StartTransaction())
            {
                var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                          tr.GetObject(defBtrId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                          ?? throw new System.Exception("Export failed: ObjectId is not a BlockTableRecord.");

                if (btr.IsLayout)
                    throw new System.Exception("Export failed: cannot export a layout BlockTableRecord.");

                defName = btr.Name;     // MUST use the actual definition name for MakeWBlock lookup
                defOrigin = btr.Origin;
                tr.Commit();
            }

            // We can name the file however we want
            string fileSafe = string.Concat((outName ?? defName)
                                .Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{fileSafe}_{System.Guid.NewGuid():N}.dwg");

            // Use the proven helper: clones the definition (with deps) into a new DB and saves it
            BlockLibraryHelper.WblockToFile(
                new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection { defBtrId },
                defOrigin,
                defName,       // this must match the cloned BTR's name
                tempPath
            );

            return tempPath;
        }


        /// Returns true when a single BlockReference is selected and exported.
        /// Outputs: assetName (base block name), dwgBytes (file), previewPng (icon), desc (description).
        private bool TryBuildDwgAssetFromSingleBlock(out string? assetName, out byte[]? dwgBytes, out byte[]? previewPng, out string? desc)
        {
            assetName = null; dwgBytes = null; previewPng = null; desc = null;

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            using (doc.LockDocument())
            {
                var sel = ed.SelectImplied();
                if (sel.Status != PromptStatus.OK || sel.Value == null || sel.Value.Count == 0)
                {
                    var opts = new PromptSelectionOptions { MessageForAdding = "\nSelect a single block for the new Component: " };
                    sel = ed.GetSelection(opts);
                    if (sel.Status != PromptStatus.OK) return false;
                }

                if (sel.Value.Count != 1)
                {
                    ed.WriteMessage("\nPlease select exactly one block reference.");
                    return false;
                }

                using var tr = db.TransactionManager.StartTransaction();
                var id = sel.Value.GetObjectIds()[0];
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                var br = ent as BlockReference;
                if (br == null)
                {
                    ed.WriteMessage("\nSelection must be a block reference.");
                    return false;
                }

                // base definition + name (dynamic-safe)
                var defId = !br.DynamicBlockTableRecord.IsNull ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                assetName = Sanitize(GetBaseBlockName(br, tr));
                if (assetName.Length > 255) assetName = assetName.Substring(0, 255);
                tr.Commit();

                // export + bytes
                //8-18 replaced - string tempDwg = ExportBlockDefinitionAsIs(db, defId, assetName);
                //dwgBytes = File.ReadAllBytes(tempDwg);
                //try { File.Delete(tempDwg); } catch { /* ignore */ }
                // export + bytes
                string tempDwg;
                try
                {
                    tempDwg = ExportBlockDefinitionAsIs(db, defId, assetName);
                }
                catch
                {
                    // Fallback: bake the current visibility state into static geometry
                    tempDwg = ExportBlockSnapshotFromReference(db, br, assetName, Autodesk.AutoCAD.Geometry.Point3d.Origin);
                }

                dwgBytes = System.IO.File.ReadAllBytes(tempDwg);
                try { System.IO.File.Delete(tempDwg); } catch { /* ignore */ }


                // crisp icon from current DB definition
                try
                {
                    previewPng = BlockIconRenderer.RenderBlockIconPng(
                        db,
                        assetName,              // the block def name you just exported/used
                        iconSizePx: 64,         // a bit more detail than 48
                        supersampleFactor: 2,   // lighter AA → preserves detail
                        background: System.Drawing.Color.Black, // or Transparent if your UI blends it
                        finalHairlinePx: 0.35f  // ~0.55 px final stroke
                    );
                }
                catch { previewPng = null; }

                desc = $"Component from block '{assetName}' • {DateTime.Now:g}";
                return true;
            }
        }
        private void CreateComponentUnderGroup(TreeNode parentNode)
        {
            if (parentNode?.Tag is not GroupTag pgt) return;

            // 1) Build the DWG asset from a single block on screen
            if (!TryBuildDwgAssetFromSingleBlock(out var assetName, out var dwgBytes, out var preview, out var desc))
                return;

            int newBlockId;
            using (var conn = new IWCConn())
            {
                conn.DBConnect();
                using var tx = conn.OpenConn.BeginTransaction();

                // 2) Insert Block (block takes asset name)
                using (var cmdB = new Microsoft.Data.SqlClient.SqlCommand(@"
            INSERT INTO dbo.Dwg_Block (BlockName, BlockDesc, BlockDateCreate, BlockNotes, BlockFileName, BlockData, BlockThumbnail)
            OUTPUT INSERTED.ID
            VALUES (@n, @d, @dc, @notes, @fn, @data, @thumb);", conn.OpenConn, tx))
                {
                    cmdB.Parameters.Add("@n", System.Data.SqlDbType.NVarChar, 255).Value = assetName;
                    cmdB.Parameters.Add("@d", System.Data.SqlDbType.NVarChar).Value = (object)desc ?? DBNull.Value;
                    cmdB.Parameters.Add("@dc", System.Data.SqlDbType.DateTime).Value = DateTime.Now;
                    cmdB.Parameters.Add("@notes", System.Data.SqlDbType.NVarChar).Value = DBNull.Value;
                    cmdB.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = DBNull.Value;
                    cmdB.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1).Value = Array.Empty<byte>(); // leave BlockData empty
                    cmdB.Parameters.Add("@thumb", System.Data.SqlDbType.VarBinary, -1).Value = DBNull.Value;

                    newBlockId = Convert.ToInt32(cmdB.ExecuteScalar());
                }

                // 3) Associate block to the group
                using (var cmdG = new Microsoft.Data.SqlClient.SqlCommand(@"
            INSERT INTO dbo.Dwg_BlockGroups_Assoc (GroupID, BlockID)
            VALUES (@gid, @bid);", conn.OpenConn, tx))
                {
                    cmdG.Parameters.Add("@gid", System.Data.SqlDbType.Int).Value = pgt.Id;
                    cmdG.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = newBlockId;
                    cmdG.ExecuteNonQuery();
                }

                // 4) Insert the single DWG asset
                using (var cmdA = new Microsoft.Data.SqlClient.SqlCommand(@"
            INSERT INTO dbo.Dwg_BlockAssets
                (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData,  FileImage)
            VALUES
                (@bid,   @fn,      @ft,      @fd,            @fda,         @data,    @img);", conn.OpenConn, tx))
                {
                    cmdA.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = newBlockId;
                    cmdA.Parameters.Add("@fn", System.Data.SqlDbType.NVarChar, 255).Value = assetName + ".dwg";
                    cmdA.Parameters.Add("@ft", System.Data.SqlDbType.NVarChar, 50).Value = ".dwg";
                    cmdA.Parameters.Add("@fd", System.Data.SqlDbType.NVarChar).Value = (object)desc ?? DBNull.Value;
                    cmdA.Parameters.Add("@fda", System.Data.SqlDbType.DateTime).Value = DateTime.Now;

                    var pData = cmdA.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1);
                    pData.Value = dwgBytes;
                    var pImg = cmdA.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
                    pImg.Value = (object)preview ?? DBNull.Value;

                    cmdA.ExecuteNonQuery();
                }

                tx.Commit();
                conn.DBClose();
            }

            // 5) Update caches/UI
            var ai = new AssetInfo
            {
                Id = -1, // unknown until reload; optional if you want to requery; leave -1 for now
                BlockId = newBlockId,
                FileName = assetName + ".dwg",
                FileExt = ".dwg",
                Description = desc,
                DateAdded = DateTime.Now,
                FileImageBytes = preview,
                FileDataBytes = dwgBytes
            };
            _assetsByBlockId[newBlockId] = new List<AssetInfo> { ai };

            var blockNode = new TreeNode(assetName)
            {
                Tag = new BlockTag { BlockId = newBlockId, Name = assetName, ParentGroupId = pgt.Id, IsComponent = true }, // mark as component
                ImageKey = ICON_COMPONENT,
                SelectedImageKey = ICON_COMPONENT
            };
            UpdateBlockNodeVisual(blockNode, newBlockId);

            // Add its single asset child node
            blockNode.Nodes.Add(new TreeNode(ai.FileName)
            {
                Tag = new AssetTag { AssetId = ai.Id, BlockId = ai.BlockId, FileName = ai.FileName, FileExt = ai.FileExt },
                ImageKey = ImageKeyForExt(ai.FileExt),
                SelectedImageKey = ImageKeyForExt(ai.FileExt)
            });

            parentNode.Nodes.Add(blockNode);
            parentNode.Expand();

            // If selected group is this one, refresh list
            if (treeGroups.SelectedNode == parentNode)
                PopulateAssetListForBlock(newBlockId, assetName);
        }

        private void UpdateBlockNodeComponentBadge(TreeNode blockNode)
        {
            if (blockNode?.Tag is not BlockTag bt) return;

            bool isComponent = false;
            if (_assetsByBlockId.TryGetValue(bt.BlockId, out var list) && list != null)
                isComponent = list.Count == 1 && string.Equals(list[0].FileExt, ".dwg", StringComparison.OrdinalIgnoreCase);

            bt.IsComponent = isComponent;
            var key = isComponent ? ICON_COMPONENT : ICON_ASSEMBLY;
            blockNode.ImageKey = key;
            blockNode.SelectedImageKey = key;
        }

        private void UpdateBlockNodeVisual(TreeNode blockNode, int blockId)
        {
            if (blockNode?.Tag is not BlockTag bt) return;

            // sanity: ensure keys exist
            if (!_treeImages.Images.ContainsKey(ICON_COMPONENT) ||
                !_treeImages.Images.ContainsKey(ICON_ASSEMBLY))
            {
                EnsureTreeIconsLoaded();
            }

            bool isComponent = false;
            if (_assetsByBlockId.TryGetValue(blockId, out var list) && list != null)
            {
                if (list.Count == 1 && IsDwgAsset(list[0]))
                    isComponent = true;
            }

            bt.IsComponent = isComponent;

            string key = isComponent ? ICON_COMPONENT : ICON_ASSEMBLY;

            // only update if changed
            if (!string.Equals(blockNode.ImageKey, key, StringComparison.Ordinal))
            {
                blockNode.ImageKey = key;
                blockNode.SelectedImageKey = key;
                // ensure the TreeView redraws
                treeGroups.Invalidate();
            }
        }

        private static bool IsDwgAsset(AssetInfo a)
        {
            // Prefer explicit FileExt; fall back to FileName
            var ext = a?.FileExt;
            if (string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(a?.FileName))
                ext = System.IO.Path.GetExtension(a.FileName);

            if (string.IsNullOrWhiteSpace(ext)) return false;

            ext = ext.Trim().ToLowerInvariant();
            if (!ext.StartsWith(".")) ext = "." + ext;
            return ext == ".dwg";
        }

        private void RefreshAllBlockIcons()
        {
            foreach (TreeNode root in treeGroups.Nodes)
                RefreshBlockIconsRecursive(root);
        }

        private void RefreshBlockIconsRecursive(TreeNode node)
        {
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag is BlockTag bt)
                    UpdateBlockNodeVisual(child, bt.BlockId);
                RefreshBlockIconsRecursive(child);
            }
        }

        /// Build "TAG.TAG.baseName" from the clicked node's group path (skips null/empty tags)
        private string BuildPrefixedName(TreeNode? contextNode, string? baseName)
        {
            baseName = SanitizeBlockName(baseName);
            if (contextNode == null) return baseName;

            var tags = new List<string>();
            TreeNode n = contextNode;
            // Walk up to root; collect GroupTag.TagCode
            while (n != null)
            {
                if (n.Tag is GroupTag gt && !string.IsNullOrWhiteSpace(gt.TagCode))
                {
                    var t = gt.TagCode.Trim();
                    if (t.Length > 0) tags.Add(t);
                }
                n = n.Parent;
            }
            if (tags.Count == 0) return baseName;

            tags.Reverse(); // root → leaf
            var prefixed = string.Join(".", tags) + "." + baseName;
            return SanitizeBlockName(prefixed);
        }
        private static string SanitizeBlockName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Asset";
            const string invalid = "\\/:;*?\"<>|,='[](){}"; // dot '.' IS allowed
            var cleaned = new string(s.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? "Asset" : (cleaned.Length > 255 ? cleaned.Substring(0, 255) : cleaned);
        }

        private string? GetNextSequentialNameForPrefix(int blockId, string prefix)
        {
            prefix ??= "";
            prefix = prefix.Trim();
            if (prefix.Length == 0) prefix = "X";

            int max = 0;

            if (_assetsByBlockId.TryGetValue(blockId, out var list) && list != null)
            {
                foreach (var a in list)
                {
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(a?.FileName ?? "");
                    if (string.IsNullOrWhiteSpace(baseName)) continue;

                    if (baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var tail = baseName.Substring(prefix.Length);
                        // strictly numeric tail -> count it
                        if (int.TryParse(tail, out int n) && n > max) max = n;
                    }
                }
            }

            int next = max + 1;
            // Two digits for 1..99; beyond 99 naturally grows (e.g., 100)
            return $"{prefix}{next:D2}";
        }

        // Drop target normalization: accept GroupTag only; if over a BlockTag or ViewsFolderTag, use its parent group
        private TreeNode? ResolveDropGroup(TreeNode over)
        {
            if (over == null) return null;
            if (over.Tag is GroupTag) return over;
            if (over.Tag is BlockTag or ViewsFolderTag) return over.Parent;
            return null;
        }

        // Persist the new parent in Dwg_BlockGroups_Assoc
        private void UpdateBlockParentGroup(int blockId, int newGroupId)
        {
            using var conn = new IWCConn();
            conn.DBConnect();

            // assumes 1:1 mapping of block -> group in assoc table
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        IF EXISTS (SELECT 1 FROM dbo.Dwg_BlockGroups_Assoc WHERE BlockID = @bid)
            UPDATE dbo.Dwg_BlockGroups_Assoc SET GroupID = @gid WHERE BlockID = @bid;
        ELSE
            INSERT INTO dbo.Dwg_BlockGroups_Assoc (GroupID, BlockID) VALUES (@gid, @bid);
    ", conn.OpenConn);

            cmd.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = blockId;
            cmd.Parameters.Add("@gid", System.Data.SqlDbType.Int).Value = newGroupId;

            cmd.ExecuteNonQuery();
            conn.DBClose();
        }

        // Optional: alphabetical sort of direct children after a move (groups first, then blocks)
        private void SortChildrenAlpha(TreeNode groupNode)
        {
            if (groupNode == null || groupNode.Nodes.Count <= 1) return;

            var items = groupNode.Nodes.Cast<TreeNode>().ToList();

            items.Sort((a, b) =>
            {
                int Rank(TreeNode n) => n.Tag is GroupTag ? 0 : n.Tag is BlockTag ? 1 : 2;
                int r = Rank(a).CompareTo(Rank(b));
                if (r != 0) return r;
                return string.Compare(a.Text, b.Text, StringComparison.CurrentCultureIgnoreCase);
            });

            groupNode.Nodes.Clear();
            groupNode.Nodes.AddRange(items.ToArray());
        }

        /// <summary>
        /// Given the node under the cursor and the cursor point, decide:
        ///  - which Group node is the destination,
        ///  - at which index under that Group to insert for reordering.
        /// Rules:
        ///  - Dropping on a GroupTag => into that folder, append at end.
        ///  - Dropping on a BlockTag => into its parent folder, before/after that item based on mouse Y (top/bottom half).
        ///  - Dropping on a ViewsFolderTag => into its parent folder, append at end.
        /// </summary>
        private (TreeNode? targetGroup, int insertIndex) ResolveDropTargetForBlocks(TreeNode over, Point clientPt)
        {
            if (over == null) return (null, -1);

            if (over.Tag is GroupTag)
            {
                var group = over;
                // append to end by default; you can refine to drop-before/after children if you want
                return (group, group.Nodes.Count);
            }

            if (over.Tag is ViewsFolderTag)
            {
                var group = over.Parent; // Views is a pseudo child under the block; but we move components under parent group
                if (group?.Tag is GroupTag)
                    return (group, group.Nodes.Count);
                return (null, -1);
            }

            if (over.Tag is BlockTag)
            {
                var group = over.Parent;
                if (group?.Tag is not GroupTag) return (null, -1);

                // compute before/after based on mouse Y relative to node bounds
                var bounds = over.Bounds; // TreeNode bounds in client coords
                int insertIndex = over.Index;
                if (clientPt.Y > bounds.Top + bounds.Height / 2)
                    insertIndex = over.Index + 1; // drop after

                return (group, insertIndex);
            }

            return (null, -1);
        }

        //------------------------------------------------------------------------------------------------------------------------
        //
        // Logic for Updated assembly dialog box workflow
        //
        //-------------------------------------------------------------------------------------------------------------------------
        private int InsertBlock(
             string name,
             string blockTag,
             string desc,
             string mfrName,
             string vendorName,
             string vendorNum,
             string notes,
             string linkUrl)
        {
            using var conn = new IWCConn();
            conn.DBConnect();

            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                        INSERT INTO dbo.Dwg_Block
                            (BlockName, BlockTag, BlockDesc,
                             BlockMfrID, BlockMfrName,
                             BlockVendorID, BlockVendorName, BlockVendorNum,
                             BlockNotes, BlockLinkUrl,
                             BlockDateCreate, BlockDateModify)
                        OUTPUT INSERTED.ID
                        VALUES
                            (@n, @tag, @d,
                             NULL, @mname,
                             NULL, @vname, @vnum,
                             @notes, @link,
                             SYSUTCDATETIME(), SYSUTCDATETIME());", conn.OpenConn);

            cmd.Parameters.AddWithValue("@n",     (object?)name     ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tag",   (object?)blockTag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@d",     (object?)desc     ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mname", (object?)mfrName  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vname", (object?)vendorName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vnum",  (object?)vendorNum  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes", (object?)notes    ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@link",  (object?)linkUrl  ?? DBNull.Value);

            var id = Convert.ToInt32(Convert.ToDecimal(cmd.ExecuteScalar()));
            conn.DBClose();
            return id;
        }

        private void UpdateBlock(
                    int blockId,
                    string name,
                    string blockTag,
                    string desc,
                    string mfrName,
                    string vendorName,
                    string vendorNum,
                    string notes,
                    string linkUrl)
        {
            using var conn = new IWCConn();
            conn.DBConnect();

            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                        UPDATE dbo.Dwg_Block
                        SET BlockName      = @n,
                            BlockTag       = @tag,
                            BlockDesc      = @d,
                            BlockMfrID     = NULL,
                            BlockMfrName   = @mname,
                            BlockVendorID  = NULL,
                            BlockVendorName= @vname,
                            BlockVendorNum = @vnum,
                            BlockNotes     = @notes,
                            BlockLinkUrl   = @link,
                            BlockDateModify= SYSUTCDATETIME()
                        WHERE ID = @id;", conn.OpenConn);

            cmd.Parameters.AddWithValue("@id",    blockId);
            cmd.Parameters.AddWithValue("@n",     (object?)name     ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tag",   (object?)blockTag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@d",     (object?)desc     ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mname", (object?)mfrName  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vname", (object?)vendorName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vnum",  (object?)vendorNum  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes", (object?)notes    ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@link",  (object?)linkUrl  ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            conn.DBClose();
        }


        private sealed class BlockDto
        {
            public int Id { get; set; }
            public string? BlockName { get; set; }
            /// <summary>User-friendly display name (BlockTag). Used for tree node text.</summary>
            public string? BlockTag { get; set; }
            public string? BlockDesc { get; set; }

            // Simplified fields (text only)
            public string? BlockMfrName { get; set; }
            public string? BlockVendorName { get; set; }
            public string? BlockVendorNum { get; set; }

            public string? BlockNotes { get; set; }
            public string? BlockLinkUrl { get; set; }
        }

        private BlockDto? GetBlockById(int id)
        {
            using var conn = new IWCConn();
            conn.DBConnect();

            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                SELECT TOP (1)
                       b.ID,
                       b.BlockName,
                       b.BlockTag,
                       b.BlockDesc,
                       b.BlockMfrName,
                       b.BlockVendorName,
                       b.BlockVendorNum,
                       b.BlockNotes,
                       b.BlockLinkUrl
                FROM dbo.Dwg_Block b
                WHERE b.ID = @id;", conn.OpenConn);

            cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = id;

            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read())
            {
                conn.DBClose();
                return null;
            }

            var dto = new BlockDto
            {
                Id              = rdr.GetInt32(rdr.GetOrdinal("ID")),
                BlockName       = rdr["BlockName"] as string,
                BlockTag        = rdr["BlockTag"]  as string,
                BlockDesc       = rdr["BlockDesc"] as string,
                BlockMfrName    = rdr["BlockMfrName"] as string,
                BlockVendorName = rdr["BlockVendorName"] as string,
                BlockVendorNum  = rdr["BlockVendorNum"] as string,
                BlockNotes      = rdr["BlockNotes"] as string,
                BlockLinkUrl    = rdr["BlockLinkUrl"] as string
            };

            conn.DBClose();
            return dto;
        }

        private System.Drawing.Image GetUrlThumb()
        {
            var img = Res.url_asset_48 ?? SystemIcons.Application.ToBitmap();
            if (img.Width != _assetThumbs.ImageSize.Width || img.Height != _assetThumbs.ImageSize.Height)
                img = new Bitmap(img, _assetThumbs.ImageSize);
            return img;
        }


        /// <summary>
        /// Returns true if <paramref name="node"/> is inside a "Project Specific"
        /// group node — by walking up through ancestors and checking the group
        /// name (case-insensitive partial match so variants like "Project-Specific"
        /// also qualify).
        /// </summary>
        private static bool IsInProjectSpecificGroup(TreeNode? node)
        {
            var current = node;
            while (current != null)
            {
                if (current.Tag is GroupTag &&
                    current.Text.IndexOf("Project Specific",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                current = current.Parent;
            }
            return false;
        }

        // -----------------------------------------------------------------------

        private bool CreateAssemblyViaDialog(out int newBlockId, out string? blockName)
        {
            newBlockId = 0;
            blockName = null;

            // "Project Specific" groups allow duplicates — pass the flag to the
            // dialog so it can skip the inline duplicate check at save time.
            bool isProjectSpecific = IsInProjectSpecificGroup(treeGroups.SelectedNode);

            using var dlg = new IWCCadToolsV9.UI.FrmAssemblyEditor(allowDuplicates: isProjectSpecific);
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;

            blockName = dlg.BlockNameValue;

            newBlockId = InsertBlock(
                name:       dlg.BlockNameValue,
                blockTag:   dlg.BlockTagValue,
                desc:       dlg.BlockDescValue,
                mfrName:    dlg.BlockMfrNameValue,
                vendorName: dlg.BlockVendorNameValue,
                vendorNum:  dlg.BlockVendorNumValue,
                notes:      dlg.BlockNotesValue,
                linkUrl:    dlg.BlockLinkUrlValue
            );

            // If a group is selected, associate the new assembly to it right away.
            if (treeGroups.SelectedNode?.Tag is GroupTag gt)
            {
                try
                {
                    using var conn = new IWCConn();
                    conn.DBConnect();
                    using var cmdAssoc = new Microsoft.Data.SqlClient.SqlCommand(
                        "INSERT INTO dbo.Dwg_BlockGroups_Assoc (GroupID, BlockID) VALUES (@gid, @bid);",
                        conn.OpenConn);
                    cmdAssoc.Parameters.Add("@gid", System.Data.SqlDbType.Int).Value = gt.Id;
                    cmdAssoc.Parameters.Add("@bid", System.Data.SqlDbType.Int).Value = newBlockId;
                    cmdAssoc.ExecuteNonQuery();
                    conn.DBClose();
                }
                catch { /* non-fatal; tree reload will still show the block under default group if assoc fails */ }
            }

            return true;
        }

        private void CreateAssemblyAndRefreshTree()
        {
            if (!CreateAssemblyViaDialog(out int newId, out string? newName)) return;

            LoadGroupsAndBlocks();
            var node = FindBlockNodeById(newId);
            if (node != null)
            {
                treeGroups.SelectedNode = node;
                node.EnsureVisible();
            }
        }


        private TreeNode? FindBlockNodeById(int blockId)
        {
            foreach (TreeNode n in treeGroups.Nodes)
            {
                var hit = FindBlockNodeByIdRecursive(n, blockId);
                if (hit != null) return hit;
            }
            return null;
        }

        private TreeNode? FindBlockNodeByIdRecursive(TreeNode n, int blockId)
        {
            if (TryGetBlockIdFromNode(n, out int id) && id == blockId) return n;
            foreach (TreeNode c in n.Nodes)
            {
                var hit = FindBlockNodeByIdRecursive(c, blockId);
                if (hit != null) return hit;
            }
            return null;
        }

        private bool TryGetBlockIdFromNode(TreeNode n, out int id)
        {
            id = 0;
            if (n?.Tag is BlockTag bt) { id = bt.BlockId; return id > 0; }
            return false;
        }

        private void EditAssemblyForNode(TreeNode n)
        {
            if (!TryGetBlockIdFromNode(n, out int id)) return;

            var b = GetBlockById(id);
            if (b == null)
            {
                MessageBox.Show(this, "Could not load assembly details.", "Edit Assembly",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using var dlg = new IWCCadToolsV9.UI.FrmAssemblyEditor(
                blockId:    b.Id,
                blockName:  b.BlockName,
                blockTag:   b.BlockTag,
                blockDesc:  b.BlockDesc,
                mfrName:    b.BlockMfrName,
                vendorName: b.BlockVendorName,
                vendorNum:  b.BlockVendorNum,
                notes:      b.BlockNotes,
                linkUrl:    b.BlockLinkUrl
            );

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            UpdateBlock(
                blockId:    b.Id,
                name:       dlg.BlockNameValue,
                blockTag:   dlg.BlockTagValue,
                desc:       dlg.BlockDescValue,
                mfrName:    dlg.BlockMfrNameValue,
                vendorName: dlg.BlockVendorNameValue,
                vendorNum:  dlg.BlockVendorNumValue,
                notes:      dlg.BlockNotesValue,
                linkUrl:    dlg.BlockLinkUrlValue
            );

            // Refresh & reselect — node text uses BlockTag (display name)
            LoadGroupsAndBlocks();
            var node = FindBlockNodeById(b.Id);
            if (node != null)
            {
                treeGroups.SelectedNode = node;
                node.EnsureVisible();
                node.Text = dlg.BlockTagValue;   // reflect rename using display name
            }
        }

        // For group moves: dropping *onto* a group means "become its child".
        // Dropping on a block/views folder → use that block's parent group.
        private TreeNode? ResolveDropTargetForGroups(TreeNode over)
        {
            if (over == null) return null;
            if (over.Tag is GroupTag) return over;
            if (over.Tag is BlockTag or ViewsFolderTag) return over.Parent;
            return null;
        }

        // Prevent dropping a group into itself or its descendants
        private static bool IsDescendant(TreeNode ancestor, TreeNode candidateTarget)
        {
            if (ancestor == null || candidateTarget == null) return false;
            var n = candidateTarget;
            while (n != null)
            {
                if (n == ancestor) return true;
                n = n.Parent;
            }
            return false;
        }

        // DB: find next GroupOrder under a parent (append-at-end)
        private int GetNextGroupOrder(int parentGroupId)
        {
            using var conn = new IWCConn();
            conn.DBConnect();
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT ISNULL(MAX(GroupOrder), 0) + 1 FROM dbo.Dwg_BlockGroups WHERE GroupParent = @pid;",
                conn.OpenConn);
            cmd.Parameters.Add("@pid", System.Data.SqlDbType.Int).Value = parentGroupId;
            int next = Convert.ToInt32(cmd.ExecuteScalar());
            conn.DBClose();
            return next <= 0 ? 1 : next;
        }

        // DB: persist new parent and order for a group folder
        private void UpdateGroupParentAndOrder(int groupId, int newParentId, int newOrder)
        {
            using var conn = new IWCConn();
            conn.DBConnect();
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        UPDATE dbo.Dwg_BlockGroups
        SET GroupParent = @pid, GroupOrder = @ord
        WHERE ID = @gid;", conn.OpenConn);

            cmd.Parameters.Add("@gid", System.Data.SqlDbType.Int).Value = groupId;
            cmd.Parameters.Add("@pid", System.Data.SqlDbType.Int).Value = newParentId;
            cmd.Parameters.Add("@ord", System.Data.SqlDbType.Int).Value = newOrder;

            cmd.ExecuteNonQuery();
            conn.DBClose();
        }


        //private void UploadIconForAsset(AssetInfo ai)
        //{
        //    using var ofd = new OpenFileDialog
        //    {
        //        Title = "Select icon image",
        //        Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
        //        Multiselect = false,
        //        CheckFileExists = true,
        //        DereferenceLinks = true,
        //        RestoreDirectory = true
        //    };

        //    if (ofd.ShowDialog(this) != DialogResult.OK) return;

        //    // Load and convert to a reasonable PNG thumbnail (max dimension ~256 px)
        //    byte[] pngBytes;
        //    using (var img = System.Drawing.Image.FromFile(ofd.FileName))
        //    {
        //        // Compute target size preserving aspect ratio
        //        const int maxSide = 256;
        //        int w = img.Width, h = img.Height;
        //        double scale = (double)maxSide / Math.Max(w, h);
        //        if (scale > 1.0) scale = 1.0; // don't upscale
        //        int tw = Math.Max(1, (int)Math.Round(w * scale));
        //        int th = Math.Max(1, (int)Math.Round(h * scale));

        //        using var bmp = new Bitmap(tw, th);
        //        using (var g = Graphics.FromImage(bmp))
        //        {
        //            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        //            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        //            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        //            g.Clear(Color.Transparent);
        //            g.DrawImage(img, new Rectangle(0, 0, tw, th));
        //        }

        //        using var ms = new MemoryStream();
        //        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        //        pngBytes = ms.ToArray();
        //    }

        //    // Save into FileImage
        //    using var conn = new IWCConn();
        //    conn.DBConnect();

        //    using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        //UPDATE dbo.Dwg_BlockAssets
        //   SET FileImage = @img,
        //       FileDateAdded = SYSUTCDATETIME()
        // WHERE ID = @id;", conn.OpenConn);

        //    var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
        //    pImg.Value = (object)pngBytes ?? DBNull.Value;

        //    cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = ai.Id;

        //    cmd.ExecuteNonQuery();
        //    conn.DBClose();

        //    // Refresh cache/UI so the new icon shows immediately
        //    RefreshAssetsForBlock(ai.BlockId);

        //    // If this asset is currently selected in the list, refresh the preview pane too
        //    if (listAssets.SelectedItems.Count == 1 &&
        //        listAssets.SelectedItems[0].Tag is AssetInfo sel &&
        //        sel.Id == ai.Id)
        //    {
        //        // Reload the in-memory info and show preview again
        //        var refreshed = GetAssetInfoById(ai.BlockId, ai.Id);
        //        if (refreshed != null) ShowAssetPreview(refreshed);
        //    }
        //}
        private void UploadIconForAsset(AssetInfo ai)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select icon image",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
                Multiselect = false,
                CheckFileExists = true,
                DereferenceLinks = true,
                RestoreDirectory = true
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            // Load and convert to a reasonable PNG thumbnail (max dimension ~256 px)
            byte[] pngBytes;
            using (var img = System.Drawing.Image.FromFile(ofd.FileName))
            {
                // Compute target size preserving aspect ratio
                const int maxSide = 256;
                int w = img.Width, h = img.Height;
                double scale = (double)maxSide / Math.Max(w, h);
                if (scale > 1.0) scale = 1.0; // don't upscale
                int tw = Math.Max(1, (int)Math.Round(w * scale));
                int th = Math.Max(1, (int)Math.Round(h * scale));

                using var bmp = new Bitmap(tw, th);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(img, new Rectangle(0, 0, tw, th));
                }

                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                pngBytes = ms.ToArray();
            }

            // Save into FileImage
            using var conn = new IWCConn();
            conn.DBConnect();

            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
        UPDATE dbo.Dwg_BlockAssets
           SET FileImage = @img,
               FileDateAdded = SYSUTCDATETIME()
         WHERE ID = @id;", conn.OpenConn);

            var pImg = cmd.Parameters.Add("@img", System.Data.SqlDbType.VarBinary, -1);
            pImg.Value = (object)pngBytes ?? DBNull.Value;

            cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = ai.Id;

            cmd.ExecuteNonQuery();
            conn.DBClose();

            // Refresh cache/UI so the new icon shows immediately
            RefreshAssetsForBlock(ai.BlockId);

            // If this asset is currently selected in the list, refresh the preview pane too
            if (listAssets.SelectedItems.Count == 1 &&
                listAssets.SelectedItems[0].Tag is AssetInfo sel &&
                sel.Id == ai.Id)
            {
                // Reload the in-memory info and show preview again
                var refreshed = GetAssetInfoById(ai.BlockId, ai.Id);
                if (refreshed != null) ShowAssetPreview(refreshed);
            }
        }



        public static class BlockAssetRepo
        {
            public static int InsertUrlAsset(SqlConnection conn, int blockId, string name, string desc, string url)
            {
                const string sql = @"
                INSERT INTO dbo.Dwg_BlockAssets (BlockID, AssetName, AssetDesc, FileType, AssetLinkUrl)
                OUTPUT INSERTED.ID
                VALUES (@BlockID, @AssetName, @AssetDesc, @FileType, @AssetLinkUrl);";

                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@BlockID", blockId);
                    cmd.Parameters.AddWithValue("@AssetName", (object)name ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@AssetDesc", (object)desc ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@FileType", "url");
                    cmd.Parameters.AddWithValue("@AssetLinkUrl", (object)url ?? DBNull.Value);

                    var id = cmd.ExecuteScalar();
                    return Convert.ToInt32(id);
                }
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                var psi = new ProcessStartInfo(url) { UseShellExecute = true, Verb = "open" };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open link:\n{url}\n\n{ex.Message}", "Open Link",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private void AddUrlAssetForBlock(int blockId, string? defaultNameFromBlock)
        {
            using var f = new FrmAddUrlAsset();
            // optional prefill: block name as a hint for the asset name
            f.Prefill(assetName: defaultNameFromBlock);

            if (f.ShowDialog(this) != DialogResult.OK) return;

            string name = (f.AssetName ?? "").Trim();
            string url = (f.AssetUrl ?? "").Trim();
            string desc = string.IsNullOrWhiteSpace(f.AssetDesc) ? null : f.AssetDesc.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Please enter a name for the asset.", "Add URL", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !(uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                  uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Please enter a valid http(s) URL.", "Add URL", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // build an icon thumbnail for the list (optional)
            byte[]? previewBytes = null;
            try
            {
                using var img = GetUrlThumb();
                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                previewBytes = ms.ToArray();
            }
            catch { /* ignore */ }

            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                using var cmd = new SqlCommand(@"
            INSERT INTO dbo.Dwg_BlockAssets
                (BlockID, FileName, FileType, FileDescription, FileDateAdded, FileData, FileImage, AssetLinkUrl)
            VALUES
                (@bid,    @fn,      @ft,      @fd,            @fda,          @data,   @img,      @url);", conn.OpenConn);

                cmd.Parameters.Add("@bid", SqlDbType.Int).Value = blockId;
                cmd.Parameters.Add("@fn", SqlDbType.NVarChar, 255).Value = name;
                cmd.Parameters.Add("@ft", SqlDbType.NVarChar, 50).Value = ".url"; // <- filetype signals URL
                cmd.Parameters.Add("@fd", SqlDbType.NVarChar).Value = (object?)desc ?? DBNull.Value;
                cmd.Parameters.Add("@fda", SqlDbType.DateTime).Value = DateTime.Now;

                // URL assets store no binary file
                var pData = cmd.Parameters.Add("@data", SqlDbType.VarBinary, -1);
                pData.Value = DBNull.Value;

                var pImg = cmd.Parameters.Add("@img", SqlDbType.VarBinary, -1);
                pImg.Value = (object?)previewBytes ?? DBNull.Value;

                cmd.Parameters.Add("@url", SqlDbType.VarChar).Value = url;

                cmd.ExecuteNonQuery();
                conn.DBClose();

                // refresh just this block
                RefreshAssetsForBlock(blockId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Saving URL failed: {ex.Message}", "Add URL", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetDetails(string? name, DateTime? added, string? description, string? notes, string? fileName)
        {
            lblDetName.Text = string.IsNullOrWhiteSpace(name) ? "" : name;
            lblDetAdded.Text = added.HasValue ? added.Value.ToLocalTime().ToString("g") : "";
            lblDetFile.Text = string.IsNullOrWhiteSpace(fileName) ? "" : fileName;
            txtDetDesc.Text = description ?? "";
            txtDetNotes.Text = notes ?? "";
        }

        private static string StripHtml(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var noTags = Regex.Replace(html, "<.*?>", string.Empty);
            return System.Net.WebUtility.HtmlDecode(noTags).Trim();
        }

        private void ShowBlockDetails(int blockId)
        {
            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();

                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT BlockName, BlockDateCreate, BlockDesc, BlockNotes FROM dbo.Dwg_Block WHERE ID = @id;", conn.OpenConn);
                cmd.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = blockId;

                using var rdr = cmd.ExecuteReader();
                if (rdr.Read())
                {
                    string name = rdr["BlockName"] as string ?? $"Block_{blockId}";
                    DateTime? dt = rdr["BlockDateCreate"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(rdr["BlockDateCreate"]);
                    string? desc = rdr["BlockDesc"] as string;
                    string? notes = StripHtml(rdr["BlockNotes"] as string);

                    SetDetails(name, dt, desc, notes, "");   // no file name for blocks
                    lblSelectedBlock.Text = name;            // keep your existing selected-block label in sync
                }
                else
                {
                    SetDetails("", null, "", "", "");
                }
                conn.DBClose();
            }
            catch
            {
                SetDetails("", null, "", "", "");
            }
        }

        // Add this helper somewhere in ctlIWCBlockBrowserV2 (e.g., near other helpers)
        // before:
        // private static void EnsureAnnotativeScaleOn(DbObject obj, Database db, string scaleName = "1:1")

        // after:
        private static void EnsureAnnotativeScaleOn(
            Autodesk.AutoCAD.DatabaseServices.DBObject obj,
            Autodesk.AutoCAD.DatabaseServices.Database db,
            string scaleName = "1:1")
        {
            if (obj == null || db == null) return;

            var ocm = db.ObjectContextManager;
            if (ocm == null) return;

            const string ctxName = "ACDB_ANNOTATIONSCALES";
            var occ = ocm.GetContextCollection(ctxName);
            if (occ == null) return;

            var scale = occ.GetContext(scaleName);
            if (scale == null)
            {
                using var newScale = new Autodesk.AutoCAD.DatabaseServices.AnnotationScale
                {
                    Name = scaleName,
                    PaperUnits = 1.0,
                    DrawingUnits = 1.0
                };
                occ.AddContext(newScale);
                scale = occ.GetContext(scaleName);
            }

            try { obj.AddContext(scale); } catch { /* non-annotative or already applied */ }
        }



        private static bool IsAnnotativeBtr(Autodesk.AutoCAD.DatabaseServices.ObjectId btrId,
                                            Autodesk.AutoCAD.DatabaseServices.Transaction tr)
        {
            if (btrId.IsNull) return false;

            var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                      tr.GetObject(btrId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);

            try
            {
                // Valid values are False, True, NotApplicable
                return btr.Annotative == Autodesk.AutoCAD.DatabaseServices.AnnotativeStates.True;
            }
            catch
            {
                // Older content / proxies: if Annotative isn't exposed, assume non-annotative
                return false;
            }
        }

        private static string SanitizeFileNameForSave(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Asset";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        private void DownloadAssetToDisk(AssetInfo? ai)
        {
            if (ai == null)
            {
                MessageBox.Show("No asset selected.", "Download Asset", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var bytes = ai.FileDataBytes;
            if (bytes == null || bytes.Length == 0)
            {
                MessageBox.Show("This asset has no stored file data (FileData is empty).", "Download Asset", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var ext = NormalizeExt(ai.FileExt);
            var defaultName = SanitizeFileNameForSave(string.IsNullOrWhiteSpace(ai.FileName) ? "Asset" : ai.FileName);
            if (!string.IsNullOrWhiteSpace(ext) && !defaultName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                defaultName += ext;

            using var sfd = new SaveFileDialog
            {
                Title = "Save asset as…",
                FileName = defaultName,
                Filter = !string.IsNullOrWhiteSpace(ext)
                         ? $"{ext.ToUpperInvariant().Trim('.')} (*{ext})|*{ext}|All files (*.*)|*.*"
                         : "All files (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true
            };

            var owner = this.FindForm();
            if (sfd.ShowDialog(owner) != DialogResult.OK) return;

            System.IO.File.WriteAllBytes(sfd.FileName, bytes);

            try
            {
                // Nice UX: reveal the saved file in Explorer
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{sfd.FileName}\"");
            }
            catch { /* non-fatal */ }
        }
        // Recursively collect nested block definitions referenced by entities inside a definition.
        // Works entirely in the SOURCE database/transaction.
        private static void CollectNestedBlockDefs(
            Autodesk.AutoCAD.DatabaseServices.Transaction trSrc,
            Autodesk.AutoCAD.DatabaseServices.BlockTableRecord srcBtr,
            System.Collections.Generic.HashSet<Autodesk.AutoCAD.DatabaseServices.ObjectId> acc)
        {
            foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId entId in srcBtr)
            {
                var ent = trSrc.GetObject(entId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead, false)
                          as Autodesk.AutoCAD.DatabaseServices.Entity;
                if (ent is Autodesk.AutoCAD.DatabaseServices.BlockReference br)
                {
                    var nestedId = br.BlockTableRecord;
                    if (!nestedId.IsNull && !acc.Contains(nestedId))
                    {
                        var nestedBtr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                                        trSrc.GetObject(nestedId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                        if (!nestedBtr.IsLayout)
                        {
                            acc.Add(nestedId);
                            // Recurse into the nested definition as well
                            CollectNestedBlockDefs(trSrc, nestedBtr, acc);
                        }
                    }
                }
            }
        }
        // Returns the root + all nested refs + dynamic anonymous children as an ObjectIdCollection
        private static Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection
BuildBlockDefinitionClosure(
    Autodesk.AutoCAD.DatabaseServices.Database srcDb,
    Autodesk.AutoCAD.DatabaseServices.ObjectId rootBtrId,
    Autodesk.AutoCAD.DatabaseServices.Transaction tr)
        {
            var result = new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection();
            var seen = new System.Collections.Generic.HashSet<
                            Autodesk.AutoCAD.DatabaseServices.ObjectId>();
            var stack = new System.Collections.Generic.Stack<
                            Autodesk.AutoCAD.DatabaseServices.ObjectId>();

            if (rootBtrId.IsNull) return result;
            stack.Push(rootBtrId);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id.IsNull || !seen.Add(id)) continue;

                var btr = (Autodesk.AutoCAD.DatabaseServices.BlockTableRecord)
                          tr.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                if (btr.IsLayout) continue;

                result.Add(id);

                // Dynamic base: include all anonymous children using the compat wrapper
                if (btr.IsDynamicBlock)
                {
                    foreach (var anonId in GetAnonymousBlockIdsCompat(btr))
                        if (!anonId.IsNull && !seen.Contains(anonId)) stack.Push(anonId);
                }

                // Walk nested block references referenced by this definition
                foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId entId in btr)
                {
                    var ent = tr.GetObject(entId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                              as Autodesk.AutoCAD.DatabaseServices.Entity;
                    if (ent is Autodesk.AutoCAD.DatabaseServices.BlockReference br)
                    {
                        // The specific def this ref points to (often anonymous for dynamic instances)
                        var refDef = br.BlockTableRecord;
                        if (!refDef.IsNull && !seen.Contains(refDef)) stack.Push(refDef);

                        // Also include the base dynamic definition of the nested ref (if exposed)
                        try
                        {
                            var dynBase = br.DynamicBlockTableRecord;
                            if (!dynBase.IsNull && !seen.Contains(dynBase)) stack.Push(dynBase);
                        }
                        catch { /* older versions may not expose */ }
                    }
                }
            }

            return result;
        }



        // Returns anonymous dynamic variant BlockTableRecord ids for a dynamic base definition.
        // Works across AutoCAD .NET versions that expose either:
        //   a) ObjectIdCollection GetAnonymousBlockIds()
        //   b) void GetAnonymousBlockIds(ObjectIdCollection outIds)
        private static System.Collections.Generic.IEnumerable<
            Autodesk.AutoCAD.DatabaseServices.ObjectId>
        GetAnonymousBlockIdsCompat(Autodesk.AutoCAD.DatabaseServices.BlockTableRecord btr)
        {
            var results = new System.Collections.Generic.List<
                Autodesk.AutoCAD.DatabaseServices.ObjectId>();

            try
            {
                var t = typeof(Autodesk.AutoCAD.DatabaseServices.BlockTableRecord);

                // Newer signature: ObjectIdCollection GetAnonymousBlockIds()
                var miNoArgs = t.GetMethod("GetAnonymousBlockIds", System.Type.EmptyTypes);
                if (miNoArgs != null &&
                    miNoArgs.ReturnType == typeof(Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection))
                {
                    var ret = miNoArgs.Invoke(btr, null)
                              as Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection;
                    if (ret != null)
                    {
                        foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in ret)
                            results.Add(id);
                    }
                    return results;
                }

                // Older signature: void GetAnonymousBlockIds(ObjectIdCollection)
                var miOneArg = t.GetMethod(
                    "GetAnonymousBlockIds",
                    new[] { typeof(Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection) });

                if (miOneArg != null)
                {
                    var col = new Autodesk.AutoCAD.DatabaseServices.ObjectIdCollection();
                    miOneArg.Invoke(btr, new object[] { col });
                    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in col)
                        results.Add(id);
                    return results;
                }
            }
            catch
            {
                // Ignore; return what we collected (likely empty on non-dynamic defs)
            }

            return results;
        }

        // Create attribute refs when a block is first inserted.
        //private static void InitializeAttributesOnInsert(Transaction tr, BlockReference br)
        //{
        //    var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
        //    if (!btr.HasAttributeDefinitions) return;

        //    foreach (ObjectId id in btr)
        //    {
        //        if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad)
        //        {
        //            if (ad.Constant) continue; // constant attrs are baked into geometry

        //            // If already present (rare on first insert), skip
        //            bool exists = br.AttributeCollection
        //                            .Cast<ObjectId>()
        //                            .Select(aid => (AttributeReference)tr.GetObject(aid, OpenMode.ForRead))
        //                            .Any(ar => string.Equals(ar.Tag, ad.Tag, StringComparison.OrdinalIgnoreCase));
        //            if (exists) continue;

        //            var ar = new AttributeReference();
        //            ar.SetAttributeFromBlock(ad, br.BlockTransform);
        //            ar.TextString = ad.TextString; // default value
        //            br.AttributeCollection.AppendAttribute(ar);
        //            tr.AddNewlyCreatedDBObject(ar, true);
        //        }
        //    }
        //}


        // Delegated to AttributeFieldHelper — see Helpers/AttributeFieldHelper.cs.
        private static void InitializeAttributesOnInsert(Transaction tr, BlockReference br)
            => AttributeFieldHelper.InitializeAttributesOnInsert(tr, br);

        // Delegated to AttributeFieldHelper — see Helpers/AttributeFieldHelper.cs.
        private static int ResyncAllBlockReferences(Database db, string blockName, bool removeOrphaned = false)
            => AttributeFieldHelper.ResyncAllBlockReferences(db, blockName, removeOrphaned);

        // Delegated to AttributeFieldHelper.
        private static bool HasFieldOnDefinition(AttributeDefinition ad)
            => AttributeFieldHelper.HasFieldOnDefinition(ad);

        // Delegated to AttributeFieldHelper.
        private static void CopyFieldFromDefinition(Transaction tr, AttributeDefinition ad, AttributeReference ar)
            => AttributeFieldHelper.CopyFieldFromDefinition(tr, ad, ar);

        // Delegated to AttributeFieldHelper.
        private static void EvaluateFieldsNow()
            => AttributeFieldHelper.EvaluateFieldsNow();



        //// Like ATTSYNC: make sure every reference of a named block matches current defs.
        //// Keeps existing values where tag names match; creates missing ones; (optionally) removes orphaned ones.
        //private static int ResyncAllBlockReferences(Database db, string blockName, bool removeOrphaned = false)
        //    {
        //        int updated = 0;
        //        using (var tr = db.TransactionManager.StartTransaction())
        //        {
        //            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        //            if (!bt.Has(blockName))
        //            {
        //                tr.Commit();
        //                return 0;
        //            }

        //            var btrDef = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);

        //            // Build map of current definition ADs
        //            var defByTag = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
        //            foreach (ObjectId id in btrDef)
        //            {
        //                if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
        //                    defByTag[ad.Tag] = ad;
        //            }

        //            // Walk Model & Paper space for BlockReferences matching name
        //            foreach (ObjectId spaceId in new[] { bt[BlockTableRecord.ModelSpace], bt[BlockTableRecord.PaperSpace] })
        //            {
        //                var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
        //                foreach (ObjectId entId in space)
        //                {
        //                    if (tr.GetObject(entId, OpenMode.ForRead) is not BlockReference br) continue;

        //                    // Resolve dynamic “real” definition name
        //                    var defId = !br.DynamicBlockTableRecord.IsNull ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        //                    if (defId != btrDef.ObjectId) continue;

        //                    // Map existing ARs by tag
        //                    var arByTag = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
        //                    foreach (ObjectId arId in br.AttributeCollection)
        //                    {
        //                        if (tr.GetObject(arId, OpenMode.ForRead) is AttributeReference ar)
        //                            arByTag[ar.Tag] = ar;
        //                    }

        //                    bool changed = false;

        //                    // Ensure all def tags exist on the reference
        //                    foreach (var kvp in defByTag)
        //                    {
        //                        var tag = kvp.Key;
        //                        var ad = kvp.Value;

        //                        if (!arByTag.TryGetValue(tag, out var ar)) // missing → create
        //                        {
        //                            space.UpgradeOpen();
        //                            var brw = (BlockReference)tr.GetObject(entId, OpenMode.ForWrite);

        //                            var newAr = new AttributeReference();
        //                            newAr.SetAttributeFromBlock(ad, brw.BlockTransform);
        //                            newAr.TextString = ad.TextString; // default or later set
        //                            brw.AttributeCollection.AppendAttribute(newAr);
        //                            tr.AddNewlyCreatedDBObject(newAr, true);

        //                            changed = true;
        //                        }
        //                        else
        //                        {
        //                            // Optional: update position/rotation/etc. to match AD (keeps user value)
        //                            var arw = (AttributeReference)tr.GetObject(ar.ObjectId, OpenMode.ForWrite);
        //                            var oldVal = arw.TextString;
        //                            arw.SetAttributeFromBlock(ad, br.BlockTransform);
        //                            arw.TextString = oldVal; // preserve user-entered value
        //                            changed = true;
        //                        }
        //                    }

        //                    // Remove ARs whose tags no longer exist in the definition (if requested)
        //                    if (removeOrphaned && arByTag.Count > 0)
        //                    {
        //                        foreach (var ar in arByTag.Values)
        //                        {
        //                            if (!defByTag.ContainsKey(ar.Tag))
        //                            {
        //                                var arw = (AttributeReference)tr.GetObject(ar.ObjectId, OpenMode.ForWrite);
        //                                arw.Erase();
        //                                changed = true;
        //                            }
        //                        }
        //                    }

        //                    if (changed) updated++;
        //                }
        //            }

        //            tr.Commit();
        //        }
        //        return updated;
        //    }





    }
}
