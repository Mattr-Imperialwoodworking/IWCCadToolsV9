using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;

// Disambiguate conflicting type names brought in by AutoCAD assemblies
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception   = System.Exception;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Commands and helpers for creating, exporting, and importing
    /// AutoCAD block definitions to/from the IWC block library database.
    ///
    /// Consolidates V8's BlockLibraryHelper + NetBlockCommands (DwgBlockUploader).
    /// </summary>
    public static class BlockLibraryHelper
    {
        // ---------------------------------------------------------------------------
        // CREATE – in-drawing block only
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCMakeBlock")]
        public static void MakeBlock()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            try
            {
                var (selRes, blockName, basePt) = PromptBlockCreationInputs(ed);
                if (selRes == null) return;

                using var tr = db.TransactionManager.StartTransaction();
                var btrId = CreateBlockDefinition(db, tr, blockName!, basePt, selRes);
                if (btrId.IsNull) { ed.WriteMessage($"\nBlock '{blockName}' already exists."); return; }

                InsertBlockReference(db, tr, btrId, basePt);
                tr.Commit();

                ed.WriteMessage($"\nBlock '{blockName}' created at {basePt}.");
            }
            catch (Exception ex) { ed.WriteMessage($"\nError: {ex.Message}"); }
        }

        // ---------------------------------------------------------------------------
        // MAKE + UPLOAD to database (simple, no group picker)
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCMakeAndUploadBlock")]
        public static void MakeAndUploadBlock()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            try
            {
                var (selRes, blockName, basePt) = PromptBlockCreationInputs(ed);
                if (selRes == null) return;

                string blockDesc  = PromptString(ed, "\nEnter block description: ");
                string blockNotes = PromptString(ed, "\nEnter block notes: ");

                ObjectId btrId;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    btrId = CreateBlockDefinition(db, tr, blockName!, basePt, selRes);
                    if (btrId.IsNull) { ed.WriteMessage($"\nBlock '{blockName}' already exists."); return; }
                    InsertBlockReference(db, tr, btrId, basePt);
                    tr.Commit();
                }

                UploadBlockToDatabase(db, blockName!, blockDesc, blockNotes, btrId, basePt, null, ed);
                ed.WriteMessage($"\nBlock '{blockName}' created and uploaded.");
            }
            catch (Exception ex) { ed.WriteMessage($"\nError: {ex.Message}"); }
        }

        // ---------------------------------------------------------------------------
        // MAKE + UPLOAD WITH GROUP PICKER
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCMakeAndUploadBlockToGroups")]
        public static void MakeAndUploadBlockToGroups()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            try
            {
                // Show group picker dialog
                IReadOnlyList<int> groupIds;
                string? blockName, blockDesc, blockNotes;
                using (var dlg = new UI.GroupPickerForm())
                {
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    { ed.WriteMessage("\nCancelled."); return; }

                    blockName  = Sanitize(dlg.BlockName);
                    blockDesc  = dlg.BlockDesc ?? string.Empty;
                    blockNotes = dlg.BlockNotes ?? string.Empty;
                    groupIds   = dlg.SelectedGroupIds;
                }

                if (string.IsNullOrWhiteSpace(blockName))
                { ed.WriteMessage("\nInvalid block name."); return; }

                var selRes = GetSelection(ed);
                if (selRes == null) return;

                var ppr = ed.GetPoint(new PromptPointOptions("\nSpecify insertion point: "));
                if (ppr.Status != PromptStatus.OK) { ed.WriteMessage("\nCancelled."); return; }
                var basePt = ppr.Value;

                ObjectId btrId;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    btrId = CreateBlockDefinition(db, tr, blockName, basePt, selRes);
                    if (btrId.IsNull) { ed.WriteMessage($"\nBlock '{blockName}' already exists."); return; }
                    InsertBlockReference(db, tr, btrId, basePt);
                    tr.Commit();
                }

                int newBlockId = UploadBlockToDatabase(db, blockName, blockDesc, blockNotes, btrId, basePt, null, ed);
                if (newBlockId > 0)
                    InsertBlockGroupAssociations(newBlockId, groupIds);

                ed.WriteMessage($"\nBlock '{blockName}' uploaded and associated with {groupIds.Count} group(s).");
            }
            catch (Exception ex) { ed.WriteMessage($"\nError: {ex.Message}"); }
        }

        // ---------------------------------------------------------------------------
        // EXPORT to local file library
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCExportBlock")]
        public static void ExportBlockToLibrary()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            try
            {
                var selRes = GetSelection(ed);
                if (selRes == null) return;

                var nameRes = ed.GetString("\nEnter a name for the block DWG file: ");
                if (nameRes.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nameRes.StringResult)) return;
                string blockName = nameRes.StringResult.Trim();

                string libraryPath = IWCUserSettings.Load().GetBlockLibraryPath();
                if (!Directory.Exists(libraryPath))
                { ed.WriteMessage($"\nBlock library path not found:\n{libraryPath}"); return; }

                var insRes = ed.GetPoint("\nSpecify base point: ");
                if (insRes.Status != PromptStatus.OK) return;
                var basePt = insRes.Value;

                ObjectId btrId;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    btrId = CreateBlockDefinition(db, tr, blockName, basePt, selRes, eraseOriginals: false);
                    if (btrId.IsNull) { ed.WriteMessage($"\nBlock '{blockName}' already exists."); return; }
                    tr.Commit();
                }

                string exportFile = Path.Combine(libraryPath, blockName + ".dwg");
                WblockToFile(db, btrId, basePt, exportFile);

                // Remove temporary block def from current drawing
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                    if (bt.Has(blockName))
                        ((BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForWrite)).Erase();
                    tr.Commit();
                }

                ed.WriteMessage($"\nBlock exported to:\n{exportFile}");
            }
            catch (Exception ex) { ed.WriteMessage($"\nError: {ex.Message}"); }
        }

        // ---------------------------------------------------------------------------
        // DOWNLOAD + INSERT from database
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCInsertBlock")]
        public static void InsertBlockFromDatabase()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            var nameRes = ed.GetString(new PromptStringOptions("\nEnter Block Name to Insert: ") { AllowSpaces = true });
            if (nameRes.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nameRes.StringResult))
            { ed.WriteMessage("\nCancelled."); return; }

            try { DownloadAndInsertByName(nameRes.StringResult.Trim()); }
            catch (Exception ex) { ed.WriteMessage($"\nError: {ex.Message}"); }
        }

        /// <summary>
        /// Downloads a block definition from the IWC database by name and inserts it
        /// into the active drawing, preserving all AttributeDefinition Field expressions.
        ///
        /// Bug fixed: previous implementations dropped Field objects on every import.
        /// Two root causes are addressed:
        ///   1. WblockCloneObjects does not copy ExtensionDictionary entries when the
        ///      destination BTR already exists — and Fields live in the ExtensionDictionary.
        ///      <see cref="AttributeFieldHelper.PatchFieldsFromSource"/> re-applies the
        ///      field expressions by reading them from the source DWG and rebuilding
        ///      them via AttributeDefinition.SetField().
        ///   2. The BlockReference inserted after the import had no AttributeReferences
        ///      created.  <see cref="AttributeFieldHelper.InitializeAttributesOnInsert"/>
        ///      builds them from the patched ADs and attaches Fields where present.
        ///
        /// Database.Insert is deliberately not used here because it throws
        /// eSelfReference when the source DWG contains a block with the same name as
        /// one already in the destination drawing — which is the normal case for IWC
        /// blocks, since each is stored under its canonical name in both file and DB.
        ///
        /// This overload is callable directly from the palette (no command prompt).
        /// </summary>
        public static void DownloadAndInsertByName(string blockName)
        {
            if (string.IsNullOrWhiteSpace(blockName))
                throw new ArgumentException("Block name cannot be empty.", nameof(blockName));

            var doc = Application.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            (byte[]? blockBytes, byte[]? _) = FetchBlockFromDatabase(blockName);
            if (blockBytes == null || blockBytes.Length == 0)
            { ed.WriteMessage($"\nBlock '{blockName}' not found in database."); return; }

            string tempDwg = Path.Combine(Path.GetTempPath(), $"{SafeFileName(blockName)}_{Guid.NewGuid():N}.dwg");
            try
            {
                File.WriteAllBytes(tempDwg, blockBytes);

                using (doc.LockDocument())
                {
                    // ── Import block definition ──────────────────────────────────────────
                    // WblockCloneObjects with Replace handles the case where a same-named
                    // definition already exists in the drawing without throwing
                    // eSelfReference (which Database.Insert would).  After the clone we
                    // call PatchFieldsFromSource because WblockCloneObjects does not copy
                    // AttributeDefinition ExtensionDictionary entries — and Fields live there.
                    if (!ImportBlockViaWblockClone(db, tempDwg, blockName))
                    { ed.WriteMessage("\nBlock import failed."); return; }

                    AttributeFieldHelper.PatchFieldsFromSource(db, tempDwg, blockName);

                    // ── Prompt for insertion point ───────────────────────────────────────
                    var ppr = ed.GetPoint(new PromptPointOptions("\nSpecify insertion point: "));
                    if (ppr.Status != PromptStatus.OK) { ed.WriteMessage("\nInsertion cancelled."); return; }

                    // ── Insert BlockReference and initialise AttributeReferences ─────────
                    // InitializeAttributesOnInsert creates an AttributeReference for every
                    // non-constant AttributeDefinition in the block and attaches live Field
                    // expressions cloned from the definition, so fields like ARCHREF and
                    // SCALE resolve correctly on the first REGEN rather than showing stale
                    // placeholder text or remaining blank.
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        if (!bt.Has(blockName)) { ed.WriteMessage("\nBlock import failed."); return; }

                        var curSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                        using var br = new BlockReference(ppr.Value, bt[blockName]);
                        curSpace.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);

                        AttributeFieldHelper.InitializeAttributesOnInsert(tr, br);

                        tr.Commit();
                    }

                    // Force field evaluation so values display immediately rather than
                    // waiting for the user's next manual REGEN.
                    AttributeFieldHelper.EvaluateFieldsNow();
                }

                ed.WriteMessage($"\nBlock '{blockName}' inserted.");
            }
            finally
            {
                try { if (File.Exists(tempDwg)) File.Delete(tempDwg); } catch { }
            }
        }

        /// <summary>Lookup and insert by DB primary key.</summary>
        public static void DownloadAndInsertById(int blockId)
        {
            string? name = LookupBlockNameById(blockId);
            if (string.IsNullOrWhiteSpace(name))
                throw new Exception($"Block ID {blockId} not found.");
            DownloadAndInsertByName(name);
        }

        // ---------------------------------------------------------------------------
        // Block icon thumbnail helpers
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Renders a 48 px PNG thumbnail for <paramref name="blockName"/>
        /// using the GDI+ software renderer.
        /// </summary>
        //public static byte[]? GetBlockThumbnail(Database db, string blockName, int sizePx = 48)
        //    => BlockIconRenderer.RenderBlockIconPng(db, blockName, iconSizePx: sizePx, supersampleFactor: 3);
        public static byte[]? GetBlockThumbnail(Database db, string blockName, int sizePx = 64)
        => BlockIconRenderer.RenderBlockIconPng(db, blockName,
               iconSizePx: sizePx,
               supersampleFactor: 3,
               background: System.Drawing.Color.Black,
               finalHairlinePx: 0.55f);
        // ---------------------------------------------------------------------------
        // Internal database upload helpers
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Uploads a block to dbo.Dwg_Block and returns the new row ID.
        /// Returns 0 on failure.
        /// </summary>
        private static int UploadBlockToDatabase(
            Database db, string blockName, string? blockDesc, string? blockNotes,
            ObjectId btrId, Point3d basePt, byte[]? thumbnailOverride, Editor ed)
        {
            string tempDwg = Path.Combine(Path.GetTempPath(), $"{blockName}_{Guid.NewGuid():N}.dwg");
            try
            {
                WblockToFile(db, btrId, basePt, tempDwg);
                byte[] dwgBytes = File.ReadAllBytes(tempDwg);
                byte[]? thumb   = thumbnailOverride ?? GetBlockThumbnail(db, blockName);

                using var conn = new IWCConn();
                conn.DBConnect();

                using var cmd = new SqlCommand(@"
                    INSERT INTO dbo.Dwg_Block
                        (BlockName, BlockDesc, BlockNotes, BlockDateCreate, BlockFileName, BlockData, BlockThumbnail)
                    OUTPUT INSERTED.ID
                    VALUES
                        (@Name, @Desc, @Notes, @Date, @File, @Data, @Thumb);", conn.OpenConn);

                cmd.Parameters.AddWithValue("@Name",  blockName);
                cmd.Parameters.AddWithValue("@Desc",  (object?)blockDesc  ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Notes", (object?)blockNotes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Date",  DateTime.Now);
                cmd.Parameters.AddWithValue("@File",  blockName + ".dwg");
                cmd.Parameters.AddWithValue("@Data",  dwgBytes);
                cmd.Parameters.AddWithValue("@Thumb", (object?)thumb ?? DBNull.Value);

                var idObj = cmd.ExecuteScalar();
                return idObj != null && idObj != DBNull.Value ? Convert.ToInt32(idObj) : 0;
            }
            finally
            {
                try { if (File.Exists(tempDwg)) File.Delete(tempDwg); } catch { }
            }
        }

        private static void InsertBlockGroupAssociations(int blockId, IEnumerable<int> groupIds)
        {
            using var conn = new IWCConn();
            conn.DBConnect();

            using var cmd = new SqlCommand(@"
                INSERT INTO dbo.Dwg_BlockGroups_Assoc (GroupID, BlockID)
                VALUES (@GroupID, @BlockID);", conn.OpenConn);

            cmd.Parameters.Add("@GroupID", System.Data.SqlDbType.Int);
            cmd.Parameters.Add("@BlockID", System.Data.SqlDbType.Int).Value = blockId;

            foreach (var gid in groupIds.Distinct())
            {
                cmd.Parameters["@GroupID"].Value = gid;
                cmd.ExecuteNonQuery();
            }
        }

        private static (byte[]? blockBytes, byte[]? thumbnail) FetchBlockFromDatabase(string blockName)
        {
            using var conn = new IWCConn();
            conn.DBConnect();
            using var cmd = conn.OpenConn.CreateCommand();
            cmd.CommandText = "SELECT BlockData, BlockThumbnail FROM dbo.Dwg_Block WHERE BlockName = @n";
            cmd.Parameters.AddWithValue("@n", blockName);

            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) return (null, null);

            byte[]? bytes = rdr.IsDBNull(0) ? null : (byte[])rdr["BlockData"];
            byte[]? thumb = rdr.IsDBNull(1) ? null : (byte[])rdr["BlockThumbnail"];
            return (bytes, thumb);
        }

        private static string? LookupBlockNameById(int id)
        {
            using var conn = new IWCConn();
            conn.DBConnect();
            using var cmd = conn.OpenConn.CreateCommand();
            cmd.CommandText = "SELECT BlockName FROM dbo.Dwg_Block WHERE ID = @id";
            cmd.Parameters.AddWithValue("@id", id);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : result.ToString();
        }

        // ---------------------------------------------------------------------------
        // Core AutoCAD helpers
        // ---------------------------------------------------------------------------

        private static ObjectId CreateBlockDefinition(
            Database db, Transaction tr, string blockName, Point3d basePt,
            PromptSelectionResult selRes, bool eraseOriginals = true)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(blockName)) return ObjectId.Null;

            var btr = new BlockTableRecord { Name = blockName, Origin = basePt };

            foreach (var objId in selRes.Value.GetObjectIds())
            {
                var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
                btr.AppendEntity((Entity)ent.Clone());
                if (eraseOriginals) ent.Erase();
            }

            bt.UpgradeOpen();
            var btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            return btrId;
        }

        private static void InsertBlockReference(Database db, Transaction tr,
            ObjectId btrId, Point3d insertionPoint)
        {
            var curSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            using var br = new BlockReference(insertionPoint, btrId);
            curSpace.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
        }

        /// <summary>
        /// Compatibility overload matching the old MakeWBlock(ObjectIdCollection, Point3d, string, string)
        /// signature used by ctlIWCBlockBrowserV2.
        /// </summary>
        public static void WblockToFile(ObjectIdCollection ids, Point3d basePt, string blockName, string filePath)
        {
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("ids must contain at least one ObjectId.", nameof(ids));
            WblockToFile(db, ids[0], basePt, filePath);
        }

        /// <summary>Creates a standalone DWG file containing the block definition + a reference.</summary>
        public static void WblockToFile(Database db, ObjectId btrId, Point3d basePt, string filePath)
        {
            using var newDb = new Database(true, false);
            db.Wblock(newDb, new ObjectIdCollection { btrId }, basePt, DuplicateRecordCloning.Ignore);

            // Insert a block reference in the new drawing's model space
            using (var tr = newDb.TransactionManager.StartTransaction())
            {
                var bt  = (BlockTable)tr.GetObject(newDb.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Find the cloned block definition by its origin point (any non-layout, non-modelspace block)
                foreach (ObjectId id in bt)
                {
                    var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!b.IsLayout && b.Name != BlockTableRecord.ModelSpace
                        && b.Name != BlockTableRecord.PaperSpace)
                    {
                        using var br = new BlockReference(basePt, id);
                        btr.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        break;
                    }
                }
                tr.Commit();
            }

            newDb.SaveAs(filePath, DwgVersion.Current);
        }

        // ---------------------------------------------------------------------------
        // Prompt helpers
        // ---------------------------------------------------------------------------

        private static (PromptSelectionResult? sel, string? name, Point3d basePt)
            PromptBlockCreationInputs(Editor ed)
        {
            var sel = GetSelection(ed);
            if (sel == null) return (null, null, Point3d.Origin);

            var nameRes = ed.GetString("\nEnter a name for the new block: ");
            if (nameRes.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nameRes.StringResult))
            { ed.WriteMessage("\nBlock creation cancelled."); return (null, null, Point3d.Origin); }

            string? name = Sanitize(nameRes.StringResult);
            if (string.IsNullOrWhiteSpace(name))
            { ed.WriteMessage("\nInvalid block name."); return (null, null, Point3d.Origin); }

            var insRes = ed.GetPoint("\nSpecify base point for the block: ");
            if (insRes.Status != PromptStatus.OK)
            { ed.WriteMessage("\nBase point entry cancelled."); return (null, null, Point3d.Origin); }

            return (sel, name, insRes.Value);
        }

        private static PromptSelectionResult? GetSelection(Editor ed)
        {
            var sel = ed.SelectImplied();
            if (sel.Status == PromptStatus.OK && sel.Value?.Count > 0) return sel;

            sel = ed.GetSelection(new PromptSelectionOptions
                { MessageForAdding = "\nSelect objects for block: " });
            return sel.Status == PromptStatus.OK ? sel : null;
        }

        private static string PromptString(Editor ed, string prompt)
        {
            var r = ed.GetString(new PromptStringOptions(prompt) { AllowSpaces = true });
            return r.Status == PromptStatus.OK ? r.StringResult : string.Empty;
        }

        private static string? Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            const string invalid = "\\/:;*?\"<>|,='[](){}";
            var clean = new string(raw.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(clean)) return null;
            return clean.Length > 255 ? clean[..255] : clean;
        }

        /// <summary>
        /// Imports the named block definition from <paramref name="sourceDwgPath"/> into
        /// <paramref name="destDb"/> using WblockCloneObjects with Replace semantics.
        ///
        /// Returns <c>true</c> on success.  The caller should follow this with
        /// <see cref="AttributeFieldHelper.PatchFieldsFromSource"/> to restore any
        /// AttributeDefinition Field objects, which WblockCloneObjects silently drops
        /// when the destination BTR already exists (documented AutoCAD limitation).
        ///
        /// We use WblockCloneObjects rather than Database.Insert because Insert throws
        /// eSelfReference when the source DWG already contains a definition with the
        /// target name — which is the normal case here, since IWC blocks are stored
        /// under their canonical block name in both the file and the database.
        /// </summary>
        private static bool ImportBlockViaWblockClone(Database destDb, string sourceDwgPath, string blockName)
        {
            try
            {
                using var srcDb = new Database(false, true);
                srcDb.ReadDwgFile(sourceDwgPath, FileShare.Read, true, null);
                srcDb.CloseInput(true);

                // Locate the source definition.  Prefer an exact name match; fall back
                // to the file-stem name (Wblock often stores blocks under that name).
                ObjectIdCollection idsToClone;
                using (var trSrc = srcDb.TransactionManager.StartTransaction())
                {
                    var sbt = (BlockTable)trSrc.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    ObjectId srcId = ObjectId.Null;

                    if (sbt.Has(blockName))
                        srcId = sbt[blockName];
                    else
                    {
                        var stem = Path.GetFileNameWithoutExtension(sourceDwgPath) ?? string.Empty;
                        if (sbt.Has(stem))
                            srcId = sbt[stem];
                    }

                    // Final fallback: first non-layout, non-anonymous BTR.
                    if (srcId.IsNull)
                    {
                        foreach (ObjectId id in sbt)
                        {
                            var b = (BlockTableRecord)trSrc.GetObject(id, OpenMode.ForRead);
                            if (b.IsLayout) continue;
                            if (b.Name.StartsWith("*", StringComparison.Ordinal)) continue;
                            srcId = id; break;
                        }
                    }

                    if (srcId.IsNull) { trSrc.Commit(); return false; }
                    idsToClone = new ObjectIdCollection { srcId };
                    trSrc.Commit();
                }

                // WblockCloneObjects with Replace silently handles same-named existing
                // BTRs in destDb (no eSelfReference).  ExtensionDictionary contents are
                // NOT copied — the caller must follow with PatchFieldsFromSource.
                srcDb.WblockCloneObjects(
                    idsToClone,
                    destDb.BlockTableId,
                    new IdMapping(),
                    DuplicateRecordCloning.Replace,
                    false);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string SafeFileName(string name)
            => new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
    }
}
