using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Generates a JSON closure report for a selected block reference,
    /// listing the root definition, all nested block definitions, and
    /// dynamic anonymous children.
    /// </summary>
    public static class IWCBlockAudit
    {
        [CommandMethod("IWCBlockClosureReport")]
        public static void BlockClosureReport()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db  = doc.Database;
            var ed  = doc.Editor;

            var peo = new PromptEntityOptions("\nSelect a block reference to audit: ");
            peo.SetRejectMessage("\nMust be a block reference.");
            peo.AddAllowedClass(typeof(BlockReference), exactMatch: false);
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            using var tr = db.TransactionManager.StartTransaction();

            var br    = (BlockReference)tr.GetObject(per.ObjectId, OpenMode.ForRead);
            var rootId = !br.DynamicBlockTableRecord.IsNull
                         ? br.DynamicBlockTableRecord
                         : br.BlockTableRecord;
            var root  = (BlockTableRecord)tr.GetObject(rootId, OpenMode.ForRead);

            var closure = BuildClosure(db, rootId, tr);

            // ObjectIdCollection doesn't implement IEnumerable<T> - enumerate via Cast<>
            var report = new
            {
                Root   = root.Name,
                Count  = closure.Count,
                Blocks = closure.Cast<ObjectId>().Select(id =>
                {
                    var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    return new
                    {
                        b.Name,
                        b.IsLayout,
                        IsDynamic = TryIsDynamic(b),
                        AnonymousChildren = GetAnonymousChildNames(b, tr)
                    };
                })
                .OrderBy(x => x.Name)
                .ToArray()
            };

            var path = Path.Combine(
                Path.GetTempPath(),
                $"IWC_BlockClosure_{Safe(root.Name)}_{Guid.NewGuid():N}.json");

            File.WriteAllText(path, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));

            ed.WriteMessage($"\n[IWC] Closure report saved: {path}");
            try { System.Diagnostics.Process.Start("notepad.exe", path); } catch { }

            tr.Commit();
        }

        // ---------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------

        private static ObjectIdCollection BuildClosure(Database db, ObjectId rootId, Transaction tr)
        {
            var result = new ObjectIdCollection();
            var seen   = new HashSet<ObjectId>();
            var stack  = new Stack<ObjectId>();
            stack.Push(rootId);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id.IsNull || !seen.Add(id)) continue;

                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout) continue;
                result.Add(id);

                if (TryIsDynamic(btr))
                    foreach (var aId in GetAnonymousBlockIds(btr))
                        if (!seen.Contains(aId)) stack.Push(aId);

                foreach (ObjectId entId in btr)
                {
                    if (tr.GetObject(entId, OpenMode.ForRead) is not BlockReference nbr) continue;

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
            return result;
        }

        private static bool TryIsDynamic(BlockTableRecord btr)
        {
            try { return btr.IsDynamicBlock; } catch { return false; }
        }

        private static IEnumerable<string> GetAnonymousChildNames(BlockTableRecord btr, Transaction tr)
        {
            foreach (var id in GetAnonymousBlockIds(btr))
            {
                if (id.IsNull) continue;
                var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                yield return b.Name;
            }
        }

        private static IEnumerable<ObjectId> GetAnonymousBlockIds(BlockTableRecord btr)
        {
            var list = new List<ObjectId>();
            try
            {
                var t      = typeof(BlockTableRecord);
                var noArgs = t.GetMethod("GetAnonymousBlockIds", Type.EmptyTypes);
                if (noArgs?.ReturnType == typeof(ObjectIdCollection))
                {
                    var coll = noArgs.Invoke(btr, null) as ObjectIdCollection;
                    if (coll != null) foreach (ObjectId id in coll) list.Add(id);
                    return list;
                }

                var oneArg = t.GetMethod("GetAnonymousBlockIds", new[] { typeof(ObjectIdCollection) });
                if (oneArg != null)
                {
                    var coll = new ObjectIdCollection();
                    oneArg.Invoke(btr, new object[] { coll });
                    foreach (ObjectId id in coll) list.Add(id);
                }
            }
            catch { }
            return list;
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Block";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
