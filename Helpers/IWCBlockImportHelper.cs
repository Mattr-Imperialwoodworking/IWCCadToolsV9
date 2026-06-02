using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Public static wrapper around the block-definition import pipeline that
    /// ctlIWCBlockBrowserV2 uses privately.  Extracted here so other commands
    /// (e.g. IWC_VIEW) can share the exact same working import logic without
    /// duplicating code.
    ///
    /// After calling ImportBlockDefinition(), callers should invoke
    ///   AttributeFieldHelper.PatchFieldsFromSource(db, tempPath, blockName)
    /// to restore any field expressions on AttributeDefinitions.
    /// </summary>
    public static class IWCBlockImportHelper
    {
        // -----------------------------------------------------------------------
        // ModelRefSnapshot — carries data about block refs found in Model Space
        // -----------------------------------------------------------------------

        internal struct ModelRefSnapshot
        {
            public string?  DefName;
            public string?  DynamicBaseName;
            public Point3d  Pos;
            public Scale3d  Scale;
            public double   Rot;
            public Vector3d Normal;
        }

        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        /// <summary>
        /// Imports a block definition named <paramref name="desiredName"/> from
        /// <paramref name="sourceDwgPath"/> into <paramref name="destDb"/>.
        /// This is the exact same logic as ctlIWCBlockBrowserV2.ImportBlockDefinitionFromFile.
        /// </summary>
        public static void ImportBlockDefinition(
            Database              destDb,
            string                sourceDwgPath,
            string                desiredName,
            DuplicateRecordCloning drcMode)
        {
            if (destDb == null)
                throw new ArgumentNullException(nameof(destDb));
            if (string.IsNullOrWhiteSpace(sourceDwgPath) || !File.Exists(sourceDwgPath))
                throw new FileNotFoundException("Source DWG not found.", sourceDwgPath);

            // Early out — Ignore mode and block already present
            if (!string.IsNullOrWhiteSpace(desiredName) &&
                drcMode == DuplicateRecordCloning.Ignore)
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

            var defsToClone = new ObjectIdCollection();
            var msSnapshots = new List<ModelRefSnapshot>();

            ObjectId rootBtrId = ObjectId.Null;
            string?  rootName  = null;

            using (var tr = srcDb.TransactionManager.StartTransaction())
            {
                var sbt = (BlockTable)tr.GetObject(srcDb.BlockTableId, OpenMode.ForRead);

                // 1) Choose a root definition
                if (!string.IsNullOrWhiteSpace(desiredName) && sbt.Has(desiredName))
                {
                    rootBtrId = sbt[desiredName];
                    rootName  = desiredName;
                }
                else
                {
                    var stem = Path.GetFileNameWithoutExtension(sourceDwgPath) ?? "";
                    if (stem.Length > 0 && sbt.Has(stem))
                    {
                        rootBtrId = sbt[stem];
                        rootName  = stem;
                    }
                    else
                    {
                        // Heuristic: pick non-layout BTR with the most nested refs
                        int bestScore = -1;
                        foreach (ObjectId id in sbt)
                        {
                            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (btr.IsLayout || btr.Name.StartsWith("*", StringComparison.Ordinal))
                                continue;
                            int score = 0;
                            foreach (ObjectId eid in btr)
                                if (tr.GetObject(eid, OpenMode.ForRead) is BlockReference) score++;
                            if (score > bestScore) { bestScore = score; rootBtrId = id; rootName = btr.Name; }
                        }
                    }
                }

                // 2) Full closure of root definition
                if (!rootBtrId.IsNull)
                {
                    var closure = BuildBlockDefinitionClosure(srcDb, rootBtrId, tr);
                    foreach (ObjectId id in closure)
                        defsToClone.Add(id);
                }

                // 3) Scan Model Space for composed block refs
                var msId = SymbolUtilityServices.GetBlockModelSpaceId(srcDb);
                var ms   = (BlockTableRecord)tr.GetObject(msId, OpenMode.ForRead);

                foreach (ObjectId entId in ms)
                {
                    if (tr.GetObject(entId, OpenMode.ForRead) is not BlockReference br) continue;

                    string? defName = null;
                    try
                    {
                        var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        if (!def.IsLayout) defName = def.Name;
                    }
                    catch { }

                    string? dynBase = null;
                    try
                    {
                        var baseId = br.DynamicBlockTableRecord;
                        if (!baseId.IsNull)
                            dynBase = ((BlockTableRecord)tr.GetObject(baseId, OpenMode.ForRead)).Name;
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(defName) || !string.IsNullOrWhiteSpace(dynBase))
                        msSnapshots.Add(new ModelRefSnapshot
                        {
                            DefName         = defName,
                            DynamicBaseName = dynBase,
                            Pos             = br.Position,
                            Scale           = br.ScaleFactors,
                            Rot             = br.Rotation,
                            Normal          = br.Normal
                        });

                    var defId = br.BlockTableRecord;
                    if (!defId.IsNull)
                    {
                        var nestedClosure = BuildBlockDefinitionClosure(srcDb, defId, tr);
                        foreach (ObjectId id in nestedClosure)
                            if (!defsToClone.Contains(id)) defsToClone.Add(id);
                    }
                }

                tr.Commit();
            }

            // 4) Clone all collected definitions into destination
            if (defsToClone.Count > 0)
            {
                var map = new IdMapping();
                destDb.WblockCloneObjects(defsToClone, destDb.BlockTableId, map, drcMode, false);
            }

            // 5) Ensure a top-level definition named desiredName exists
            using var trd = destDb.TransactionManager.StartTransaction();
            var bt        = (BlockTable)trd.GetObject(destDb.BlockTableId, OpenMode.ForRead);
            bool hasDesired = bt.Has(desiredName);

            // Case A: exactly one model-space ref → rename its imported definition
            if (!hasDesired && msSnapshots.Count == 1)
            {
                var snap      = msSnapshots[0];
                string candidate = !string.IsNullOrWhiteSpace(snap.DynamicBaseName)
                                   && !snap.DynamicBaseName!.StartsWith("*")
                                   ? snap.DynamicBaseName!
                                   : snap.DefName ?? "";

                if (!string.IsNullOrWhiteSpace(candidate) && bt.Has(candidate))
                {
                    if (bt.Has(desiredName) && drcMode != DuplicateRecordCloning.Ignore)
                    {
                        var oldBtr = (BlockTableRecord)trd.GetObject(bt[desiredName], OpenMode.ForWrite);
                        oldBtr.Name = $"{desiredName}._IWC_OLD_{Guid.NewGuid():N}".Replace(":", "_");
                    }

                    if (!bt.Has(desiredName))
                    {
                        bt.UpgradeOpen();
                        var btrw = (BlockTableRecord)trd.GetObject(bt[candidate], OpenMode.ForWrite);
                        btrw.Name = desiredName;
                        hasDesired = true;
                    }
                }
            }

            // Case B: multiple refs or Case A failed → build a wrapper definition
            if (!hasDesired && msSnapshots.Count > 0)
            {
                bt.UpgradeOpen();
                var newDef   = new BlockTableRecord { Name = desiredName };
                var newDefId = bt.Add(newDef);
                trd.AddNewlyCreatedDBObject(newDef, true);

                foreach (var snap in msSnapshots)
                {
                    string childName = !string.IsNullOrWhiteSpace(snap.DynamicBaseName)
                                       && bt.Has(snap.DynamicBaseName!)
                                       ? snap.DynamicBaseName!
                                       : snap.DefName ?? "";
                    if (string.IsNullOrWhiteSpace(childName) || !bt.Has(childName)) continue;

                    var childRef = new BlockReference(snap.Pos, bt[childName])
                    {
                        Rotation     = snap.Rot,
                        ScaleFactors = snap.Scale
                    };
                    try { childRef.Normal = snap.Normal; } catch { }
                    newDef.AppendEntity(childRef);
                    trd.AddNewlyCreatedDBObject(childRef, true);
                }
            }

            trd.Commit();
        }

        // -----------------------------------------------------------------------
        // BuildBlockDefinitionClosure
        // -----------------------------------------------------------------------

        internal static ObjectIdCollection BuildBlockDefinitionClosure(
            Database    srcDb,
            ObjectId    rootBtrId,
            Transaction tr)
        {
            var result = new ObjectIdCollection();
            var seen   = new HashSet<ObjectId>();
            var stack  = new Stack<ObjectId>();

            if (rootBtrId.IsNull) return result;
            stack.Push(rootBtrId);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id.IsNull || !seen.Add(id)) continue;

                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout) continue;

                result.Add(id);

                // Dynamic block: include all anonymous variant BTRs
                if (btr.IsDynamicBlock)
                    foreach (var anonId in GetAnonymousBlockIdsCompat(btr))
                        if (!anonId.IsNull && !seen.Contains(anonId)) stack.Push(anonId);

                // Walk nested block references
                foreach (ObjectId entId in btr)
                {
                    if (tr.GetObject(entId, OpenMode.ForRead) is not BlockReference br) continue;

                    var refDef = br.BlockTableRecord;
                    if (!refDef.IsNull && !seen.Contains(refDef)) stack.Push(refDef);

                    try
                    {
                        var dynBase = br.DynamicBlockTableRecord;
                        if (!dynBase.IsNull && !seen.Contains(dynBase)) stack.Push(dynBase);
                    }
                    catch { }
                }
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // GetAnonymousBlockIdsCompat — works across AutoCAD API versions
        // -----------------------------------------------------------------------

        private static IEnumerable<ObjectId> GetAnonymousBlockIdsCompat(BlockTableRecord btr)
        {
            var results = new List<ObjectId>();
            try
            {
                var t = typeof(BlockTableRecord);

                // Newer: ObjectIdCollection GetAnonymousBlockIds()
                var miNoArgs = t.GetMethod("GetAnonymousBlockIds", Type.EmptyTypes);
                if (miNoArgs != null &&
                    miNoArgs.ReturnType == typeof(ObjectIdCollection))
                {
                    var ret = miNoArgs.Invoke(btr, null) as ObjectIdCollection;
                    if (ret != null)
                        foreach (ObjectId id in ret) results.Add(id);
                    return results;
                }

                // Older: void GetAnonymousBlockIds(ObjectIdCollection)
                var miOneArg = t.GetMethod(
                    "GetAnonymousBlockIds",
                    new[] { typeof(ObjectIdCollection) });
                if (miOneArg != null)
                {
                    var col = new ObjectIdCollection();
                    miOneArg.Invoke(btr, new object[] { col });
                    foreach (ObjectId id in col) results.Add(id);
                }
            }
            catch { }
            return results;
        }
    }
}
