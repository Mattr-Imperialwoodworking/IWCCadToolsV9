// ── Using aliases — resolve collisions caused by ImplicitUsings + AutoCAD + WinForms ──
// "Application" collides: Autodesk.AutoCAD.ApplicationServices.Application vs System.Windows.Forms.Application
// "Exception"   collides: Autodesk.AutoCAD.Runtime.Exception vs System.Exception
// Solution: alias the AutoCAD types; use System.Exception explicitly in catch clauses.
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

// ──────────────────────────────────────────────────────────────────────────────────
// IWC_DiagnoseDynamicBlock.cs
//
// DROP THIS FILE into IWCCadToolsV9/Commands/ (no other project changes needed).
// Adds one temporary, read-only command:  IWC_DIAGDYN
//
// USAGE
//   1. Insert the problem block from the IWC browser into any open drawing.
//   2. Type  IWC_DIAGDYN  at the AutoCAD command prompt.
//   3. Click the inserted block reference when prompted.
//   4. Press F2 to open the full text window — OR open the .txt log file whose
//      path is printed at the very end of the output.
//   5. Paste the complete output (Sections A–I) back for analysis.
//
// This command is STRICTLY READ-ONLY — it never modifies the drawing or database.
// ──────────────────────────────────────────────────────────────────────────────────

namespace IWCCadToolsV9.Commands
{
    public class IWC_DiagnoseDynamic
    {
        [CommandMethod("IWC_DIAGDYN")]
        public void DiagnoseDynamicBlock()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            ed.WriteMessage("\n\n══════════════════════════════════════════");
            ed.WriteMessage("\n  IWC Dynamic Block Diagnostic  (read-only)");
            ed.WriteMessage("\n══════════════════════════════════════════");

            // ── 1. Pick a block reference ─────────────────────────────────────────
            var peo = new PromptEntityOptions("\nSelect the block reference to diagnose: ")
            {
                AllowNone = false
            };
            peo.SetRejectMessage("\nMust select a block reference.");
            peo.AddAllowedClass(typeof(BlockReference), exactMatch: false);

            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) { ed.WriteMessage("\nCancelled."); return; }

            using var tr = db.TransactionManager.StartTransaction();

            var brPicked = tr.GetObject(per.ObjectId, OpenMode.ForRead) as BlockReference;
            if (brPicked == null)
            {
                ed.WriteMessage("\nSelection is not a BlockReference.");
                tr.Abort();
                return;
            }

            // ── 2. Resolve base BTR (dynamic-safe) ──────────────────────────────
            ObjectId baseBtrId = (!brPicked.DynamicBlockTableRecord.IsNull)
                                 ? brPicked.DynamicBlockTableRecord
                                 : brPicked.BlockTableRecord;

            var baseBtr = tr.GetObject(baseBtrId, OpenMode.ForRead) as BlockTableRecord;
            if (baseBtr == null)
            {
                ed.WriteMessage("\nCould not open base BlockTableRecord.");
                tr.Abort();
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION A — Block Reference properties");
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine($"  ObjectId (BR):              {per.ObjectId}");
            sb.AppendLine($"  br.Name (effective):        {brPicked.Name}");
            sb.AppendLine($"  br.BlockTableRecord:        {brPicked.BlockTableRecord}");
            sb.AppendLine($"  br.DynamicBlockTableRecord: {brPicked.DynamicBlockTableRecord}");
            sb.AppendLine($"  br.IsDynamicBlock:          {brPicked.IsDynamicBlock}");
            sb.AppendLine($"  br.Position:                {brPicked.Position}");
            sb.AppendLine($"  br.ScaleFactors:            {brPicked.ScaleFactors}");
            sb.AppendLine($"  br.Rotation (rad):          {brPicked.Rotation:F4}");

            // ── 3. Dynamic block properties via reflection ───────────────────────
            // GetDynamicBlockProperties() exists on BlockReference in acdbmgd but
            // is resolved here via reflection so the diagnostic compiles cleanly
            // regardless of which AutoCAD version's API header is on the machine.
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION B — Dynamic block properties on the REFERENCE");
            sb.AppendLine("  (Visibility state names, current value, and all allowed values)");
            sb.AppendLine("══════════════════════════════════════════════════════════");

            string? activeVisibilityState = null;
            ReportDynamicProperties(brPicked, sb, ref activeVisibilityState);

            if (activeVisibilityState != null)
                sb.AppendLine($"\n  *** Active visibility state: \"{activeVisibilityState}\" ***");

            // ── 4. Base BTR summary ──────────────────────────────────────────────
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION C — Base BlockTableRecord");
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine($"  ObjectId:          {baseBtrId}");
            sb.AppendLine($"  Name:              {baseBtr.Name}");
            sb.AppendLine($"  IsDynamicBlock:    {baseBtr.IsDynamicBlock}");
            sb.AppendLine($"  IsAnonymous:       {baseBtr.IsAnonymous}");
            sb.AppendLine($"  IsLayout:          {baseBtr.IsLayout}");
            sb.AppendLine($"  Origin:            {baseBtr.Origin}");
            sb.AppendLine($"  Entity count:      {baseBtr.Cast<ObjectId>().Count()}");

            // ── 5. Anonymous variant blocks (*Uxx) ──────────────────────────────
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION D — Anonymous variant blocks");
            sb.AppendLine("  Each *Uxx block holds geometry for one visibility state.");
            sb.AppendLine("  Healthy N-state dynamic block should have N anon variants.");
            sb.AppendLine("══════════════════════════════════════════════════════════");

            var anonIds = GetAnonymousBlockIds(baseBtr);
            if (anonIds.Count == 0)
            {
                sb.AppendLine("  (none returned by GetAnonymousBlockIds)");
                if (baseBtr.IsDynamicBlock)
                    sb.AppendLine("  *** WARNING: IsDynamicBlock=true but GetAnonymousBlockIds returned 0 — " +
                                  "anonymous variants may have been stripped during Wblock/import ***");
            }
            else
            {
                sb.AppendLine($"  Count: {anonIds.Count}");
                foreach (var anonId in anonIds)
                {
                    try
                    {
                        var anonBtr = tr.GetObject(anonId, OpenMode.ForRead) as BlockTableRecord;
                        if (anonBtr == null) { sb.AppendLine($"  [{anonId}] — could not open as BTR"); continue; }

                        int entCount     = anonBtr.Cast<ObjectId>().Count();
                        int visibleCount = 0;
                        int hiddenCount  = 0;
                        var nestedRefNames = new List<string>();

                        foreach (ObjectId entId in anonBtr)
                        {
                            try
                            {
                                var ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                                if (ent == null) continue;
                                bool vis = ent.Visible;
                                if (vis) visibleCount++; else hiddenCount++;

                                if (ent is BlockReference nbr2)
                                {
                                    string nname = "(?)";
                                    try { nname = ((BlockTableRecord)tr.GetObject(nbr2.BlockTableRecord, OpenMode.ForRead)).Name; }
                                    catch { }
                                    nestedRefNames.Add($"{nname}(vis={vis})");
                                }
                            }
                            catch { /* skip unreadable entity */ }
                        }

                        sb.AppendLine($"  [{anonBtr.Name}]  id={anonId}");
                        sb.AppendLine($"    Entities: {entCount}  (visible={visibleCount}, hidden={hiddenCount})");
                        if (nestedRefNames.Count > 0)
                            sb.AppendLine($"    Nested refs: {string.Join(", ", nestedRefNames)}");
                    }
                    catch (System.Exception ex)
                    {
                        sb.AppendLine($"  [{anonId}] — ERROR: {ex.Message}");
                    }
                }
            }

            // ── 6. Direct BTR the reference actually points to ──────────────────
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION E — BTR currently pointed to by br.BlockTableRecord");
            sb.AppendLine("  (This is the anonymous variant AutoCAD is actively displaying)");
            sb.AppendLine("══════════════════════════════════════════════════════════");
            try
            {
                var directBtr = tr.GetObject(brPicked.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                if (directBtr != null)
                {
                    sb.AppendLine($"  Name:                    {directBtr.Name}");
                    sb.AppendLine($"  IsAnonymous:             {directBtr.IsAnonymous}");
                    sb.AppendLine($"  IsDynamicBlock:          {directBtr.IsDynamicBlock}");
                    sb.AppendLine($"  Entity count:            {directBtr.Cast<ObjectId>().Count()}");
                    bool foundInAnon = anonIds.Contains(brPicked.BlockTableRecord);
                    sb.AppendLine($"  In GetAnonymousBlockIds: {foundInAnon}");
                    if (!foundInAnon && directBtr.IsAnonymous)
                        sb.AppendLine("  *** WARNING: reference targets an anonymous BTR that GetAnonymousBlockIds " +
                                      "does NOT return from the base — likely root cause of missing states ***");
                }
            }
            catch (System.Exception ex)
            {
                sb.AppendLine($"  ERROR: {ex.Message}");
            }

            // ── 7. Full definition closure ───────────────────────────────────────
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION F — Full definition closure reachable from base BTR");
            sb.AppendLine("══════════════════════════════════════════════════════════");
            var closure = BuildClosure(baseBtrId, tr);
            sb.AppendLine($"  Closure size: {closure.Count} definitions");
            foreach (var cid in closure)
            {
                try
                {
                    var cbtr = tr.GetObject(cid, OpenMode.ForRead) as BlockTableRecord;
                    if (cbtr == null) continue;
                    sb.AppendLine($"  [{cbtr.Name}]  isAnon={cbtr.IsAnonymous}  isDyn={cbtr.IsDynamicBlock}  entities={cbtr.Cast<ObjectId>().Count()}");
                }
                catch { sb.AppendLine($"  [{cid}] — could not open"); }
            }

            // ── 8. db.Wblock() round-trip ────────────────────────────────────────
            // Simulates exactly what ExportBlockDefinitionAsIs + BlockLibraryHelper.WblockToFile does.
            // If anonymous variants disappear here the UPLOAD step is stripping visibility states.
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION G — db.Wblock() round-trip");
            sb.AppendLine("  Simulates ExportBlockDefinitionAsIs / WblockToFile (the UPLOAD path).");
            sb.AppendLine("  If anon count drops to 0 here, visibility states are lost on upload.");
            sb.AppendLine("══════════════════════════════════════════════════════════");

            string? tempPath = null;
            try
            {
                string safeName = string.Concat(baseBtr.Name.Select(c =>
                    System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"IWC_DIAG_{safeName}_{Guid.NewGuid():N}.dwg");

                using (var newDb = new Database(true, false))
                {
                    db.Wblock(newDb,
                              new ObjectIdCollection { baseBtrId },
                              baseBtr.Origin,
                              DuplicateRecordCloning.Ignore);

                    using (var tr2 = newDb.TransactionManager.StartTransaction())
                    {
                        var bt2 = (BlockTable)tr2.GetObject(newDb.BlockTableId, OpenMode.ForRead);
                        int dynCount = 0, anonCount = 0;
                        sb.AppendLine("  BTRs in Wblock'd DB:");
                        foreach (ObjectId id2 in bt2)
                        {
                            var btr2 = tr2.GetObject(id2, OpenMode.ForRead) as BlockTableRecord;
                            if (btr2 == null || btr2.IsLayout) continue;
                            string flag2 = btr2.IsDynamicBlock ? " [DYN]" : btr2.IsAnonymous ? " [ANON]" : "";
                            sb.AppendLine($"    {btr2.Name}{flag2}  entities={btr2.Cast<ObjectId>().Count()}");
                            if (btr2.IsDynamicBlock) dynCount++;
                            if (btr2.IsAnonymous)    anonCount++;
                        }
                        sb.AppendLine($"  Summary: dynamic={dynCount}  anonymous={anonCount}");
                        if (dynCount > 0 && anonCount == 0)
                            sb.AppendLine("  *** CONFIRMED: db.Wblock() stripped all anonymous variant BTRs. " +
                                          "Visibility states WILL be broken after upload. ***");
                        else if (anonCount > 0)
                            sb.AppendLine("  OK: anonymous variants survived Wblock.");
                        tr2.Commit();
                    }
                    newDb.SaveAs(tempPath, DwgVersion.Current);
                }
                sb.AppendLine($"  Temp DWG: {tempPath}");
            }
            catch (System.Exception ex)
            {
                sb.AppendLine($"  ERROR during Wblock round-trip: {ex.Message}");
            }

            // ── 9. Re-read the temp DWG (import path simulation) ─────────────────
            // Simulates how ImportBlockDefinitionFromFile opens the stored DWG bytes.
            if (tempPath != null && System.IO.File.Exists(tempPath))
            {
                sb.AppendLine();
                sb.AppendLine("══════════════════════════════════════════════════════════");
                sb.AppendLine("  SECTION H — Re-read temp DWG via ReadDwgFile");
                sb.AppendLine("  Simulates ImportBlockDefinitionFromFile opening stored bytes.");
                sb.AppendLine("══════════════════════════════════════════════════════════");
                try
                {
                    using var srcDb = new Database(false, true);
                    srcDb.ReadDwgFile(tempPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                    srcDb.CloseInput(true);

                    using var tr3 = srcDb.TransactionManager.StartTransaction();
                    var sbt3 = (BlockTable)tr3.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    int dynCount3 = 0, anonCount3 = 0;

                    foreach (ObjectId id3 in sbt3)
                    {
                        var btr3 = tr3.GetObject(id3, OpenMode.ForRead) as BlockTableRecord;
                        if (btr3 == null || btr3.IsLayout) continue;
                        string flag3 = btr3.IsDynamicBlock ? " [DYN]" : btr3.IsAnonymous ? " [ANON]" : "";
                        sb.AppendLine($"  {btr3.Name}{flag3}  entities={btr3.Cast<ObjectId>().Count()}");
                        if (btr3.IsDynamicBlock) dynCount3++;
                        if (btr3.IsAnonymous)    anonCount3++;

                        if (btr3.IsDynamicBlock)
                        {
                            var anonList3 = GetAnonymousBlockIds(btr3);
                            sb.AppendLine($"    GetAnonymousBlockIds() → {anonList3.Count} ids");
                            foreach (var aid3 in anonList3)
                            {
                                try
                                {
                                    var ab3 = tr3.GetObject(aid3, OpenMode.ForRead) as BlockTableRecord;
                                    sb.AppendLine($"      [{ab3?.Name}]  entities={ab3?.Cast<ObjectId>().Count()}");
                                }
                                catch { sb.AppendLine($"      [{aid3}] — open error"); }
                            }
                        }
                    }
                    sb.AppendLine($"  Total: dynamic={dynCount3}  anonymous={anonCount3}");

                    if (dynCount3 > 0 && anonCount3 == 0)
                        sb.AppendLine("  *** WARNING: dynamic base present but NO anonymous variants in re-read DB ***");
                    else if (anonCount3 > 0)
                        sb.AppendLine("  OK: anonymous variants survive ReadDwgFile.");

                    // Model Space contents
                    var msId3 = SymbolUtilityServices.GetBlockModelSpaceId(srcDb);
                    var ms3   = (BlockTableRecord)tr3.GetObject(msId3, OpenMode.ForRead);
                    int msEnt = ms3.Cast<ObjectId>().Count();
                    sb.AppendLine($"  Model Space entity count: {msEnt}");
                    foreach (ObjectId eid3 in ms3)
                    {
                        try
                        {
                            var e3 = tr3.GetObject(eid3, OpenMode.ForRead);
                            if (e3 is BlockReference br3)
                            {
                                string btrName3 = "(?)";
                                try { btrName3 = ((BlockTableRecord)tr3.GetObject(br3.BlockTableRecord, OpenMode.ForRead)).Name; }
                                catch { }
                                string dynName3 = "(none)";
                                try
                                {
                                    if (!br3.DynamicBlockTableRecord.IsNull)
                                        dynName3 = ((BlockTableRecord)tr3.GetObject(br3.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
                                }
                                catch { }
                                sb.AppendLine($"    BR → btr='{btrName3}'  dynBase='{dynName3}'  isDyn={br3.IsDynamicBlock}");
                            }
                            else
                            {
                                sb.AppendLine($"    Entity: {e3.GetType().Name}");
                            }
                        }
                        catch (System.Exception ex3) { sb.AppendLine($"    (error: {ex3.Message})"); }
                    }
                    tr3.Commit();
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine($"  ERROR re-reading temp DWG: {ex.Message}");
                }
            }

            // ── 10. Nested named child block definitions ──────────────────────────
            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION I — Nested named child block definitions");
            sb.AppendLine("  (Non-anonymous blocks referenced by the base or its variants)");
            sb.AppendLine("══════════════════════════════════════════════════════════");

            var nestedDefs = new Dictionary<string, (int entityCount, bool existsInDb)>();
            CollectNestedDefNames(db, baseBtrId, tr, nestedDefs);
            if (nestedDefs.Count == 0)
            {
                sb.AppendLine("  (none found)");
            }
            else
            {
                foreach (var kv in nestedDefs)
                    sb.AppendLine($"  [{kv.Key}]  entities={kv.Value.entityCount}  presentInCurrentDb={kv.Value.existsInDb}");
            }

            sb.AppendLine();
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine("  END OF DIAGNOSTIC — paste everything above this line");
            sb.AppendLine("══════════════════════════════════════════════════════════");
            sb.AppendLine();

            tr.Commit();

            // ── Output: command line + log file ──────────────────────────────────
            string output = sb.ToString();
            foreach (var line in output.Split('\n'))
                ed.WriteMessage(line + "\n");

            try
            {
                string logPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"IWC_DIAG_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(logPath, output, Encoding.UTF8);
                ed.WriteMessage($"\n>>> Log saved to: {logPath}\n");
                ed.WriteMessage("    Open that file and paste its full contents.\n");
            }
            catch { /* non-critical */ }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Section B helper — reads DynamicBlockReferenceProperty[] via reflection.
        //
        // GetDynamicBlockProperties() is a method on BlockReference in acdbmgd.dll
        // but its return type (DynamicBlockReferenceProperty[]) lives in the same
        // assembly. Using reflection avoids a direct type reference that may fail
        // to resolve at compile time depending on which API header set is installed.
        // ─────────────────────────────────────────────────────────────────────────
        private static void ReportDynamicProperties(
            BlockReference br,
            StringBuilder sb,
            ref string? activeVisibilityState)
        {
            try
            {
                var mi = typeof(BlockReference).GetMethod(
                    "GetDynamicBlockProperties",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);

                if (mi == null)
                {
                    sb.AppendLine("  GetDynamicBlockProperties() not found on BlockReference — API version may differ.");
                    return;
                }

                var rawResult = mi.Invoke(br, null);
                if (rawResult == null)
                {
                    sb.AppendLine("  (returned null)");
                    return;
                }

                // Cast to non-generic IEnumerable so we can iterate without knowing the concrete type
                var enumerable = rawResult as System.Collections.IEnumerable;
                if (enumerable == null)
                {
                    sb.AppendLine("  (result is not enumerable — unexpected return type)");
                    return;
                }

                bool anyProps = false;
                foreach (var dp in enumerable)
                {
                    anyProps = true;
                    if (dp == null) continue;

                    var dpType = dp.GetType();

                    string propName    = GetPropStr(dp, dpType, "PropertyName");
                    string description = GetPropStr(dp, dpType, "Description");
                    string typeCode    = GetPropStr(dp, dpType, "PropertyTypeCode");
                    string readOnly    = GetPropStr(dp, dpType, "ReadOnly");

                    string valStr = "(error)";
                    try
                    {
                        var valProp = dpType.GetProperty("Value");
                        valStr = valProp?.GetValue(dp)?.ToString() ?? "(null)";
                    }
                    catch { }

                    // GetAllowedValues() — returns object[]
                    string allowedStr = "(n/a)";
                    try
                    {
                        var miAllowed = dpType.GetMethod("GetAllowedValues", Type.EmptyTypes);
                        if (miAllowed != null)
                        {
                            var allowed = miAllowed.Invoke(dp, null) as object[];
                            if (allowed != null && allowed.Length > 0)
                                allowedStr = string.Join(", ", allowed.Select(v => v?.ToString() ?? "null"));
                        }
                    }
                    catch { allowedStr = "(error)"; }

                    sb.AppendLine($"  [{propName}]");
                    sb.AppendLine($"    Description:  {description}");
                    sb.AppendLine($"    TypeCode:     {typeCode}");
                    sb.AppendLine($"    Value:        {valStr}");
                    sb.AppendLine($"    ReadOnly:     {readOnly}");
                    sb.AppendLine($"    AllowedValues:{allowedStr}");

                    if (string.Equals(propName, "Visibility", StringComparison.OrdinalIgnoreCase) ||
                        (propName.StartsWith("Vis", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrEmpty(allowedStr) && allowedStr != "(n/a)"))
                    {
                        activeVisibilityState = valStr;
                    }
                }

                if (!anyProps)
                    sb.AppendLine("  (no properties returned — block may not be dynamic, or has no parameters)");
            }
            catch (System.Exception ex)
            {
                sb.AppendLine($"  ERROR: {ex.Message}");
            }
        }

        private static string GetPropStr(object obj, Type t, string propName)
        {
            try { return t.GetProperty(propName)?.GetValue(obj)?.ToString() ?? "(null)"; }
            catch { return "(error)"; }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // GetAnonymousBlockIds — compat wrapper (same as existing codebase pattern)
        // ─────────────────────────────────────────────────────────────────────────
        private static List<ObjectId> GetAnonymousBlockIds(BlockTableRecord btr)
        {
            var list = new List<ObjectId>();
            try
            {
                var t = typeof(BlockTableRecord);

                // AutoCAD 2021+: ObjectIdCollection GetAnonymousBlockIds()
                var miNoArgs = t.GetMethod("GetAnonymousBlockIds", Type.EmptyTypes);
                if (miNoArgs?.ReturnType == typeof(ObjectIdCollection))
                {
                    var col = miNoArgs.Invoke(btr, null) as ObjectIdCollection;
                    if (col != null) foreach (ObjectId id in col) list.Add(id);
                    return list;
                }

                // Older: void GetAnonymousBlockIds(ObjectIdCollection)
                var miOneArg = t.GetMethod("GetAnonymousBlockIds",
                                           new[] { typeof(ObjectIdCollection) });
                if (miOneArg != null)
                {
                    var col = new ObjectIdCollection();
                    miOneArg.Invoke(btr, new object[] { col });
                    foreach (ObjectId id in col) list.Add(id);
                }
            }
            catch { /* ignore */ }
            return list;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // BuildClosure — full set of BTR ObjectIds reachable from rootId
        // ─────────────────────────────────────────────────────────────────────────
        private static HashSet<ObjectId> BuildClosure(ObjectId rootId, Transaction tr)
        {
            var seen  = new HashSet<ObjectId>();
            var stack = new Stack<ObjectId>();
            stack.Push(rootId);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id.IsNull || !seen.Add(id)) continue;
                try
                {
                    var btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                    if (btr == null || btr.IsLayout) continue;

                    if (btr.IsDynamicBlock)
                        foreach (var aid in GetAnonymousBlockIds(btr))
                            if (!seen.Contains(aid)) stack.Push(aid);

                    foreach (ObjectId eid in btr)
                    {
                        try
                        {
                            if (tr.GetObject(eid, OpenMode.ForRead) is BlockReference nbr)
                            {
                                if (!nbr.BlockTableRecord.IsNull && !seen.Contains(nbr.BlockTableRecord))
                                    stack.Push(nbr.BlockTableRecord);
                                try
                                {
                                    if (!nbr.DynamicBlockTableRecord.IsNull && !seen.Contains(nbr.DynamicBlockTableRecord))
                                        stack.Push(nbr.DynamicBlockTableRecord);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return seen;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CollectNestedDefNames — walks base + anon variants, records named children
        // ─────────────────────────────────────────────────────────────────────────
        private static void CollectNestedDefNames(
            Database db,
            ObjectId rootId,
            Transaction tr,
            Dictionary<string, (int entityCount, bool existsInDb)> result)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            void Walk(ObjectId btrId)
            {
                try
                {
                    var btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                    if (btr == null || btr.IsLayout) return;

                    var toWalk = new List<ObjectId> { btrId };
                    if (btr.IsDynamicBlock)
                        toWalk.AddRange(GetAnonymousBlockIds(btr));

                    foreach (var walkId in toWalk)
                    {
                        var wbtr = tr.GetObject(walkId, OpenMode.ForRead) as BlockTableRecord;
                        if (wbtr == null) continue;

                        foreach (ObjectId eid in wbtr)
                        {
                            try
                            {
                                if (tr.GetObject(eid, OpenMode.ForRead) is not BlockReference nbr) continue;
                                ObjectId defId;
                                string? defName;
                                try
                                {
                                    var dynId = nbr.DynamicBlockTableRecord;
                                    defId   = (!dynId.IsNull) ? dynId : nbr.BlockTableRecord;
                                    defName = ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
                                }
                                catch { continue; }

                                if (string.IsNullOrEmpty(defName) || defName!.StartsWith("*")) continue;
                                if (result.ContainsKey(defName)) continue;

                                var childBtr = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                                result[defName] = (childBtr?.Cast<ObjectId>().Count() ?? 0, bt.Has(defName));
                                Walk(defId);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            Walk(rootId);
        }
    }
}
