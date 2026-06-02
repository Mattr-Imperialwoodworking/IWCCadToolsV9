using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Shared block-definition import helper used by commands that need to load
    /// a block from a DWG file into the active drawing.
    ///
    /// Mirrors the core pipeline used by ctlIWCBlockBrowserV2:
    ///   ReadDwgFile → locate root block definition → WblockCloneObjects → rename.
    ///
    /// AttributeFieldHelper.PatchFieldsFromSource should be called by the caller
    /// after ImportBlockDefinition returns, to preserve field expressions on
    /// AttributeDefinitions (e.g. the sheet-number field on the LT attribute).
    /// </summary>
    public static class IWCBlockImportHelper
    {
        /// <summary>
        /// Imports a block definition named <paramref name="desiredName"/> from
        /// <paramref name="sourceDwgPath"/> into <paramref name="destDb"/>.
        ///
        /// Strategy (matches block-browser behaviour):
        ///   1. If <paramref name="drcMode"/> is Ignore and the block already
        ///      exists in <paramref name="destDb"/>, returns immediately.
        ///   2. Opens the source DWG and looks for a block named
        ///      <paramref name="desiredName"/>; if not found, falls back to the
        ///      file stem, then to the heuristic of picking the non-layout BTR
        ///      with the most nested references.
        ///   3. Uses WblockCloneObjects to clone the full closure of the chosen
        ///      block definition (including nested block defs) into destDb, then
        ///      renames it to <paramref name="desiredName"/>.
        /// </summary>
        public static void ImportBlockDefinition(
            Database destDb,
            string   sourceDwgPath,
            string   desiredName,
            DuplicateRecordCloning drcMode)
        {
            if (destDb == null)            throw new ArgumentNullException(nameof(destDb));
            if (!File.Exists(sourceDwgPath))
                throw new FileNotFoundException("Source DWG not found.", sourceDwgPath);

            // Early-out for Ignore mode when block is already present
            if (drcMode == DuplicateRecordCloning.Ignore)
            {
                using var chk = destDb.TransactionManager.StartTransaction();
                var btChk = (BlockTable)chk.GetObject(destDb.BlockTableId, OpenMode.ForRead);
                if (btChk.Has(desiredName)) { chk.Commit(); return; }
                chk.Commit();
            }

            // Open source DWG
            using var srcDb = new Database(false, true);
            srcDb.ReadDwgFile(sourceDwgPath, FileOpenMode.OpenForReadAndAllShare, true, null);
            srcDb.CloseInput(true);

            ObjectId rootBtrId = ObjectId.Null;
            string?  rootName  = null;
            var      toClone   = new ObjectIdCollection();

            using (var srcTr = srcDb.TransactionManager.StartTransaction())
            {
                var sbt = (BlockTable)srcTr.GetObject(srcDb.BlockTableId, OpenMode.ForRead);

                // Prefer an exact name match, then file stem, then heuristic
                if (!string.IsNullOrWhiteSpace(desiredName) && sbt.Has(desiredName))
                {
                    rootBtrId = sbt[desiredName];
                    rootName  = desiredName;
                }
                else
                {
                    string stem = Path.GetFileNameWithoutExtension(sourceDwgPath);
                    if (stem.Length > 0 && sbt.Has(stem))
                    {
                        rootBtrId = sbt[stem];
                        rootName  = stem;
                    }
                    else
                    {
                        // Pick the non-layout, non-anonymous BTR with the most nested refs
                        int bestScore = -1;
                        foreach (ObjectId id in sbt)
                        {
                            var btr = (BlockTableRecord)srcTr.GetObject(id, OpenMode.ForRead);
                            if (btr.IsLayout || btr.Name.StartsWith("*", StringComparison.Ordinal))
                                continue;
                            int score = btr.Cast<ObjectId>()
                                           .Count(eid => srcTr.GetObject(eid, OpenMode.ForRead)
                                                              is BlockReference);
                            if (score > bestScore) { bestScore = score; rootBtrId = id; rootName = btr.Name; }
                        }
                    }
                }

                if (rootBtrId.IsNull)
                    throw new InvalidOperationException(
                        $"Could not find a usable block definition in '{sourceDwgPath}'.");

                // Collect full closure (root + all nested definitions)
                CollectClosure(srcDb, rootBtrId, srcTr, toClone);
                srcTr.Commit();
            }

            // Clone into destDb
            using var destTr = destDb.TransactionManager.StartTransaction();
            var destBt = (BlockTable)destTr.GetObject(destDb.BlockTableId, OpenMode.ForRead);
            var destBtId = destDb.BlockTableId;
            destTr.Commit();

            using var mapCol = new IdMapping();
            srcDb.WblockCloneObjects(toClone, destBtId, mapCol, drcMode, false);

            // Rename the cloned definition to desiredName if it came under a different name
            if (!string.IsNullOrWhiteSpace(rootName) &&
                !string.Equals(rootName, desiredName, StringComparison.OrdinalIgnoreCase))
            {
                using var ren = destDb.TransactionManager.StartTransaction();
                var bt2 = (BlockTable)ren.GetObject(destDb.BlockTableId, OpenMode.ForRead);
                if (bt2.Has(rootName) && !bt2.Has(desiredName))
                {
                    var btr = (BlockTableRecord)ren.GetObject(bt2[rootName], OpenMode.ForWrite);
                    btr.Name = desiredName;
                }
                ren.Commit();
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static void CollectClosure(Database srcDb, ObjectId btrId,
            Transaction tr, ObjectIdCollection collector)
        {
            if (collector.Contains(btrId)) return;
            collector.Add(btrId);

            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            foreach (ObjectId entId in btr)
            {
                collector.Add(entId);
                var bref = tr.GetObject(entId, OpenMode.ForRead) as BlockReference;
                if (bref == null) continue;

                ObjectId nested = bref.DynamicBlockTableRecord.IsNull
                    ? bref.BlockTableRecord
                    : bref.DynamicBlockTableRecord;

                if (!nested.IsNull && !collector.Contains(nested))
                    CollectClosure(srcDb, nested, tr, collector);
            }
        }
    }
}
