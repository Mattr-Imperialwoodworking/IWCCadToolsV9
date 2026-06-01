using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;

using Microsoft.Data.SqlClient;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using IWCCadToolsV9.Data;
using IWCCadToolsV9.Helpers;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using DataTable = System.Data.DataTable;
using Exception = System.Exception;

namespace IWCCadToolsV9.UI
{
    public partial class ctlIWCBlockBrowser : UserControl
    {
        //TASKS
        // TODO: - Update code so i can add custom icons for use in tree view
        // TODO: - Update block browser so block details are shown when a block is selected in the list view
        // TODO: - Add make new block command that allows user to select the node to add block too. 
        // TODO: - Add feature to allow users to change folders or add/remove blocks from folders. Blocks should always be in a general catch all folder so they don't get lost


        private readonly ImageList _thumbs = new ImageList { ImageSize = new Size(64, 64), ColorDepth = ColorDepth.Depth32Bit };
        // One ImageList for the TreeView (16px works well for WinForms trees)
        private readonly ImageList _treeImages = new ImageList
        {
            ImageSize = new Size(16, 16),
            ColorDepth = ColorDepth.Depth32Bit
        };

        // Register well-known keys so you can refer by name instead of index
        private const string ICON_FOLDER = "folder";
        private const string ICON_BLOCK = "block";

        private readonly Dictionary<int, TreeNode> _nodeById = new Dictionary<int, TreeNode>();
        private readonly Dictionary<int, BlockInfo> _blocksInList = new Dictionary<int, BlockInfo>();

        // Default catch‑all folder for unassociated blocks
        private const int DEFAULT_GROUP_ID = 7;
        private const string DEFAULT_GROUP_NAME = "Block Library";

        public ctlIWCBlockBrowser()
        {
            InitializeComponent();
            listBlocks.LargeImageList = _thumbs;
            listBlocks.SmallImageList = _thumbs;

            treeGroups.AfterSelect += treeGroups_AfterSelect;
            btnRefresh.Click += (s, e) => LoadGroups();
            btnInsert.Click += (s, e) => InsertSelectedBlocks();
            listBlocks.DoubleClick += (s, e) => InsertSelectedBlocks();
            listBlocks.SelectedIndexChanged += listBlocks_SelectedIndexChanged;

            // Build the TreeView image list
            // Attach the image list to the tree
            treeGroups.ImageList = _treeImages;

            // Option A: load icons from embedded Resources (recommended)
            AddOrReplaceIcon(ICON_FOLDER, Properties.Resources.IWCTreeFolder2);  // PNG in your Resources.resx
            AddOrReplaceIcon(ICON_BLOCK, Properties.Resources.IWCTreeBlock);   // PNG in your Resources.resx


            LoadGroups();
        }

        private void LoadGroups()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                _nodeById.Clear();
                treeGroups.BeginUpdate();
                treeGroups.Nodes.Clear();

                using var conn = new IWCConn();
                conn.DBConnect();

                // 1) Pull all groups
                var dtGroups = new DataTable();
                using (var da = new SqlDataAdapter(@"
            SELECT ID, GroupName, GroupParent, GroupType, GroupDesc, GroupOrder, LibraryID
            FROM dbo.Dwg_BlockGroups
            ORDER BY GroupOrder, GroupName;", conn.OpenConn))
                {
                    da.Fill(dtGroups);
                }

                // Make all group nodes
                foreach (DataRow r in dtGroups.Rows)
                {
                    int id = Convert.ToInt32(r["ID"]);
                    string name = Convert.ToString(r["GroupName"]);
                    string desc = Convert.ToString(r["GroupDesc"]);
                    int parent = Convert.ToInt32(r["GroupParent"]);

                    var node = new TreeNode(name)
                    {
                        Tag = new GroupTag { Id = id, ParentId = parent, Desc = desc },
                        ImageKey = ICON_FOLDER,
                        SelectedImageKey = ICON_FOLDER
                    };
                    _nodeById[id] = node;
                }

                // Ensure the default catch‑all group exists in the tree
                if (!_nodeById.ContainsKey(DEFAULT_GROUP_ID))
                {
                    var fallbackNode = new TreeNode(DEFAULT_GROUP_NAME)
                    {
                        Tag = new GroupTag { Id = DEFAULT_GROUP_ID, ParentId = 0, Desc = "Catch‑all for blocks without associations" },
                        ImageKey = ICON_FOLDER,
                        SelectedImageKey = ICON_FOLDER
                    };
                    _nodeById[DEFAULT_GROUP_ID] = fallbackNode;
                }

                // Wire group parents
                foreach (var kvp in _nodeById)
                {
                    var node = kvp.Value;
                    var tag = (GroupTag)node.Tag;
                    if (tag.ParentId == 0)
                        treeGroups.Nodes.Add(node);
                    else if (_nodeById.TryGetValue(tag.ParentId, out var parentNode))
                        parentNode.Nodes.Add(node);
                    else
                        treeGroups.Nodes.Add(node); // fallback if parent missing
                }

                // 2) Pull all block associations and add as children under each group
                var dtAssoc = new DataTable();
                using (var da = new SqlDataAdapter(@"
            SELECT a.GroupID,
                   b.ID   AS BlockID,
                   b.BlockName
            FROM dbo.Dwg_BlockGroups_Assoc a
            INNER JOIN dbo.Dwg_Block b ON b.ID = a.BlockID
            ORDER BY a.GroupID, b.BlockName;", conn.OpenConn))
                {
                    da.Fill(dtAssoc);
                }

                foreach (DataRow r in dtAssoc.Rows)
                {
                    int groupId = Convert.ToInt32(r["GroupID"]);
                    int blockId = Convert.ToInt32(r["BlockID"]);
                    string blockName = r["BlockName"] as string ?? $"Block_{blockId}";

                    if (_nodeById.TryGetValue(groupId, out var groupNode))
                    {
                        var child = new TreeNode(blockName)
                        {
                            Tag = new BlockTag
                            {
                                BlockId = blockId,
                                Name = blockName,
                                ParentGroupId = groupId
                            },
                            ImageKey = ICON_BLOCK,
                            SelectedImageKey = ICON_BLOCK
                        };
                        groupNode.Nodes.Add(child);
                    }
                }

                // 3) Append-orphan blocks (no associations) into DEFAULT_GROUP_ID
                using (var cmd = new SqlCommand(@"
            SELECT b.ID AS BlockID, b.BlockName
            FROM dbo.Dwg_Block b
            LEFT JOIN dbo.Dwg_BlockGroups_Assoc a ON a.BlockID = b.ID
            WHERE a.BlockID IS NULL
            ORDER BY b.BlockName;", conn.OpenConn))
                using (var rdr = cmd.ExecuteReader())
                {
                    if (_nodeById.TryGetValue(DEFAULT_GROUP_ID, out var defaultNode))
                    {
                        while (rdr.Read())
                        {
                            int blockId = rdr.GetInt32(0);
                            string blockName = rdr.IsDBNull(1) ? $"Block_{blockId}" : rdr.GetString(1);

                            var orphan = new TreeNode(blockName)
                            {
                                Tag = new BlockTag
                                {
                                    BlockId = blockId,
                                    Name = blockName,
                                    ParentGroupId = DEFAULT_GROUP_ID
                                },
                                ImageKey = ICON_BLOCK,
                                SelectedImageKey = ICON_BLOCK
                            };
                            defaultNode.Nodes.Add(orphan);
                        }
                    }
                }

                // 4) Start collapsed
                treeGroups.CollapseAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading groups/blocks: {ex.Message}", "Block Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                treeGroups.EndUpdate();
                Cursor = Cursors.Default;
            }
        }



        private void treeGroups_AfterSelect(object? sender,  TreeViewEventArgs e)
        {
            if (e.Node?.Tag is GroupTag gt)
            {
                LoadBlocksForGroup(gt.Id);
            }
            else if (e.Node?.Tag is BlockTag bt)
            {
                // Ensure the list shows the parent group’s blocks
                LoadBlocksForGroup(bt.ParentGroupId);

                // Optionally select the block in the list (if present)
                foreach (ListViewItem lvi in listBlocks.Items)
                {
                    if (lvi.Tag is int id && id == bt.BlockId)
                    {
                        lvi.Selected = true;
                        lvi.Focused = true;
                        lvi.EnsureVisible();
                        break;
                    }
                }
            }
        }


        private void LoadBlocksForGroup(int groupId)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                _blocksInList.Clear();
                _thumbs.Images.Clear();
                listBlocks.Items.Clear();

                using var conn = new IWCConn();
                conn.DBConnect();

                string sql;
                if (groupId == DEFAULT_GROUP_ID)
                {
                    // Show both: (1) blocks explicitly tied to Group 7, plus (2) all orphans
                    sql = @"
                SELECT b.ID AS BlockID,
                       b.BlockName,
                       b.BlockDesc,
                       b.BlockNotes,
                       b.BlockDateCreate,
                       b.BlockDateModify,
                       b.BlockThumbnail
                FROM dbo.Dwg_BlockGroups_Assoc a
                JOIN dbo.Dwg_Block b ON b.ID = a.BlockID
                WHERE a.GroupID = @gid

                UNION

                SELECT b.ID AS BlockID,
                       b.BlockName,
                       b.BlockDesc,
                       b.BlockNotes,
                       b.BlockDateCreate,
                       b.BlockDateModify,
                       b.BlockThumbnail
                FROM dbo.Dwg_Block b
                WHERE NOT EXISTS (SELECT 1 FROM dbo.Dwg_BlockGroups_Assoc a WHERE a.BlockID = b.ID)

                ORDER BY BlockName;";
                }
                else
                {
                    sql = @"
                SELECT a.BlockID,
                       b.BlockName,
                       b.BlockDesc,
                       b.BlockNotes,
                       b.BlockDateCreate,
                       b.BlockDateModify,
                       b.BlockThumbnail
                FROM dbo.Dwg_BlockGroups_Assoc a
                JOIN dbo.Dwg_Block b ON b.ID = a.BlockID
                WHERE a.GroupID = @gid
                ORDER BY b.BlockName;";
                }

                using var cmd = new SqlCommand(sql, conn.OpenConn);
                cmd.Parameters.AddWithValue("@gid", groupId);

                using var rdr = cmd.ExecuteReader();
                int imgIndex = 0;
                while (rdr.Read())
                {
                    int blockId = rdr.GetInt32(0);
                    string blockName = rdr.IsDBNull(1) ? "(unnamed)" : rdr.GetString(1);
                    string blockDesc = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                    string blockNotes = rdr.IsDBNull(3) ? "" : rdr.GetString(3);
                    DateTime? dateCreate = rdr.IsDBNull(4) ? (DateTime?)null : rdr.GetDateTime(4);
                    DateTime? dateModify = rdr.IsDBNull(5) ? (DateTime?)null : rdr.GetDateTime(5);

                    System.Drawing.Image thumb = null;
                    if (!rdr.IsDBNull(6))
                    {
                        var bytes = (byte[])rdr[6];
                        using var ms = new MemoryStream(bytes);
                        try { thumb = System.Drawing.Image.FromStream(ms); } catch { thumb = null; }
                    }
                    if (thumb == null) thumb = SystemIcons.Application.ToBitmap();

                    _thumbs.Images.Add(thumb);

                    var lvi = new ListViewItem(blockName, imgIndex)
                    {
                        Tag = blockId,
                        ToolTipText = blockDesc
                    };
                    lvi.SubItems.Add(blockDesc);
                    listBlocks.Items.Add(lvi);

                    _blocksInList[blockId] = new BlockInfo
                    {
                        Id = blockId,
                        Name = blockName,
                        Desc = blockDesc,
                        Notes = blockNotes,
                        DateCreate = dateCreate,
                        DateModify = dateModify,
                        ImageIndex = imgIndex
                    };

                    imgIndex++;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading blocks: {ex.Message}", "Block Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }


        private void InsertSelectedBlocks()
        {
            if (listBlocks.SelectedItems.Count == 0)
            {
                MessageBox.Show("Select one or more blocks to insert.", "Block Browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            foreach (ListViewItem item in listBlocks.SelectedItems)
            {
                if (item.Tag is not int blockId) continue;

                try
                {
                    // Option A: by ID (preferred—no reliance on UI text)
                    BlockLibraryHelper.DownloadAndInsertById(blockId);

                    // Option B: by Name
                     //var (blockName, _) = _blocksInList[blockId];
                     //NetBlockCommands.DownloadAndInsertNetBlockByName(blockName);
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\nInsert failed: {ex.Message}");
                }
            }
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        /// <summary>
        /// Reads the DWG from disk into a side Database and clones its block definition
        /// into the current drawing if not already present. Returns the target BlockTableRecord Id.
        /// </summary>
        private ObjectId EnsureBlockDefinitionInCurrentDb(string dwgPath, string blockName)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            // Already exists?
            if (bt.Has(blockName))
            {
                var existing = bt[blockName];
                tr.Commit();
                return existing;
            }
            tr.Commit();

            // Import from side db
            using var sourceDb = new Database(false, true);
            sourceDb.ReadDwgFile(dwgPath, FileShare.Read, true, null);
            // Resolve names etc.
            sourceDb.CloseInput(true);

            ObjectId importedBtrId = ObjectId.Null;

            using (var trTarget = db.TransactionManager.StartTransaction())
            using (var trSource = sourceDb.TransactionManager.StartTransaction())
            {
                var sourceBt = (BlockTable)trSource.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);

                // Try requested name, otherwise first non-anonymous, non-layout block
                ObjectId srcBtrId = ObjectId.Null;
                if (sourceBt.Has(blockName))
                {
                    srcBtrId = sourceBt[blockName];
                }
                else
                {
                    foreach (ObjectId id in sourceBt)
                    {
                        var btr = (BlockTableRecord)trSource.GetObject(id, OpenMode.ForRead);
                        if (!btr.IsLayout && !btr.Name.StartsWith("*"))
                        {
                            srcBtrId = id;
                            blockName = btr.Name; // adopt actual name
                            break;
                        }
                    }
                }

                if (srcBtrId == ObjectId.Null)
                    throw new System.Exception("Could not locate a valid block definition in the source DWG.");

                // Prepare clone mapping
                var ids = new ObjectIdCollection { srcBtrId };
                var idMap = new IdMapping();

                // Destination owner is the BlockTable of target db
                var targetBt = (BlockTable)trTarget.GetObject(db.BlockTableId, OpenMode.ForWrite);

                sourceDb.WblockCloneObjects(ids, db.BlockTableId, idMap, DuplicateRecordCloning.Replace, false);

                // Retrieve the imported block id by name
                importedBtrId = targetBt[blockName];

                trSource.Commit();
                trTarget.Commit();
            }

            return importedBtrId;
        }

        private class GroupTag
        {
            public int Id { get; set; }
            public int ParentId { get; set; }
            public string? Desc { get; set; }
        }

        private class BlockTag
        {
            public int BlockId { get; set; }
            public string? Name { get; set; }
            public int ParentGroupId { get; set; }
        }

        private sealed class BlockInfo
        {
            public int Id { get; init; }
            public string? Name { get; init; }
            public string? Desc { get; init; }
            public string? Notes { get; init; }
            public DateTime? DateCreate { get; init; }
            public DateTime? DateModify { get; init; }
            public int ImageIndex { get; init; }
        }


        // --- Icons: Folder (shell) & Bullet (drawn) ---

        private static Bitmap GetSmallFolderBitmap()
        {
            // Use SHGetFileInfo to get the Windows folder icon (small)
            const uint SHGFI_ICON = 0x000000100;
            const uint SHGFI_SMALLICON = 0x000000001;
            const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
            const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

            SHFILEINFO shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(
                pszPath: @"C:\",                                 // any dir path works
                dwFileAttributes: FILE_ATTRIBUTE_DIRECTORY,
                psfi: out shinfo,
                cbFileInfo: (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                uFlags: SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

            if (hImg == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
            {
                // Fallback: draw a simple folder-like rectangle
                return CreateFallbackFolderBitmap();
            }

            // Copy to managed Icon/Bitmap and destroy native handle
            Icon icon = Icon.FromHandle(shinfo.hIcon);
            Bitmap bmp = icon.ToBitmap();
            DestroyIcon(shinfo.hIcon);
            return bmp;
        }

        private static Bitmap CreateBulletBitmap()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                // draw a simple bullet (filled circle)
                var rect = new Rectangle(6, 6, 4, 4);
                using var b = new SolidBrush(SystemColors.ControlText);
                g.FillEllipse(b, rect);
                // optional: outline
                using var p = new Pen(SystemColors.ControlText);
                g.DrawEllipse(p, rect);
            }
            return bmp;
        }

        private static Bitmap CreateFallbackFolderBitmap()
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.Goldenrod);
            using var tab = new SolidBrush(Color.Khaki);
            g.FillRectangle(body, new Rectangle(2, 6, 12, 8));  // body
            g.FillRectangle(tab, new Rectangle(3, 4, 6, 4));   // tab
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

        private void AddOrReplaceIcon(string key, System.Drawing.Image img)
        {
            if (img == null) return;

            // Ensure size is correct for the ImageList
            if (img.Width != _treeImages.ImageSize.Width || img.Height != _treeImages.ImageSize.Height)
            {
                img = new Bitmap(img, _treeImages.ImageSize);
            }

            if (_treeImages.Images.ContainsKey(key))
                _treeImages.Images.RemoveByKey(key);

            _treeImages.Images.Add(key, img);
        }

        private void AddOrReplaceIconFromFile(string key, string filePath)
        {
            if (!File.Exists(filePath)) return;
            using var bmp = new Bitmap(filePath);
            AddOrReplaceIcon(key, bmp);
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

        private void listBlocks_SelectedIndexChanged(object? sender,  EventArgs e)
        {
            if (listBlocks.SelectedItems.Count == 1 && listBlocks.SelectedItems[0].Tag is int id && _blocksInList.TryGetValue(id, out var info))
            {
                PopulateDetails(info);
            }
            else
            {
                ClearDetails();
            }
        }

        private void PopulateDetails(BlockInfo info)
        {
            txtBlockName.Text = info.Name ?? "";
            txtBlockDesc.Text = info.Desc ?? "";
            txtBlockNotes.Text = info.Notes ?? "";
            if (info.DateCreate.HasValue)
            {
                dtBlockCreate.Value = info.DateCreate.Value;
                dtBlockCreate.Checked = true;
            }
            else
            {
                dtBlockCreate.Checked = false;
            }
            if (info.DateModify.HasValue)
            {
                dtBlockModify.Value = info.DateModify.Value;
                dtBlockModify.Checked = true;
            }
            else
            {
                dtBlockModify.Checked = false;
            }
        }

        private void ClearDetails()
        {
            txtBlockName.Text = "";
            txtBlockDesc.Text = "";
            txtBlockNotes.Text = "";
            dtBlockCreate.Checked = false;
            dtBlockModify.Checked = false;
        }

    }
}
