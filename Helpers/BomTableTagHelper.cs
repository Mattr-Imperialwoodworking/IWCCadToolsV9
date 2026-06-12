using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Tags IWC-generated BOM tables directly on the AutoCAD Table entity.
    ///
    /// This replaces the old one-handle-per-drawing custom property workflow.
    /// Because the metadata is stored on the table entity itself, copied tables
    /// and tables placed inside block definitions can all be found and refreshed.
    /// </summary>
    public static class BomTableTagHelper
    {
        public const string XRecordName = "IWC_BOM_TABLE";
        public const int SchemaVersion = 1;

        public const string FormatWide = "Wide";
        public const string FormatTitleblock = "Titleblock";

        public enum BomTableKind
        {
            Hardware,
            Material,
            Metal
        }

        public sealed class BomTableInfo
        {
            public BomTableInfo(ObjectId objectId, BomTableKind kind, string format)
            {
                ObjectId = objectId;
                Kind = kind;
                Format = NormalizeFormat(format);
            }

            public ObjectId ObjectId { get; }
            public BomTableKind Kind { get; }
            public string Format { get; }
        }

        /// <summary>
        /// Writes the IWC BOM tag to a table entity. The table must already be
        /// database-resident and open for write in <paramref name="tr"/>.
        /// </summary>
        public static void TagTable(Table table, Transaction tr, BomTableKind kind, string format)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (tr == null) throw new ArgumentNullException(nameof(tr));

            if (!table.ExtensionDictionary.IsValid)
                table.CreateExtensionDictionary();

            var extDict = (DBDictionary)tr.GetObject(table.ExtensionDictionary, OpenMode.ForWrite);
            Xrecord xrec;

            if (extDict.Contains(XRecordName))
            {
                xrec = (Xrecord)tr.GetObject(extDict.GetAt(XRecordName), OpenMode.ForWrite);
            }
            else
            {
                xrec = new Xrecord();
                extDict.SetAt(XRecordName, xrec);
                tr.AddNewlyCreatedDBObject(xrec, true);
            }

            xrec.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, XRecordName),
                new TypedValue((int)DxfCode.Int16, SchemaVersion),
                new TypedValue((int)DxfCode.Text, kind.ToString()),
                new TypedValue((int)DxfCode.Text, NormalizeFormat(format)));
        }

        /// <summary>
        /// Finds all tagged IWC BOM tables in model space, paper space, and user
        /// block definitions. Xrefs and dependent blocks are skipped.
        /// </summary>
        public static List<BomTableInfo> FindBomTables(Database db, BomTableKind? kind = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var results = new List<BomTableInfo>();
            var seen = new HashSet<ObjectId>();

            using var tr = db.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            foreach (ObjectId btrId in blockTable)
            {
                var btr = tr.GetObject(btrId, OpenMode.ForRead, false) as BlockTableRecord;
                if (btr == null) continue;
                if (btr.IsFromExternalReference || btr.IsDependent) continue;

                foreach (ObjectId entId in btr)
                {
                    if (entId.IsNull || entId.IsErased) continue;
                    if (!seen.Add(entId)) continue;

                    if (tr.GetObject(entId, OpenMode.ForRead, false) is not Table table)
                        continue;

                    if (!TryReadTag(table, tr, out BomTableInfo? info))
                        continue;

                    if (kind.HasValue && info.Kind != kind.Value)
                        continue;

                    results.Add(info);
                }
            }

            tr.Commit();
            return results;
        }

        public static bool TryReadTag(Table table, Transaction tr, out BomTableInfo? info)
        {
            info = null;
            if (table == null || tr == null) return false;
            if (!table.ExtensionDictionary.IsValid) return false;

            try
            {
                var extDict = (DBDictionary)tr.GetObject(table.ExtensionDictionary, OpenMode.ForRead);
                if (!extDict.Contains(XRecordName)) return false;

                var xrec = (Xrecord)tr.GetObject(extDict.GetAt(XRecordName), OpenMode.ForRead);
                var data = xrec.Data?.AsArray();
                if (data == null || data.Length < 4) return false;

                string? kindText = null;
                string? formatText = null;

                foreach (TypedValue tv in data)
                {
                    if (tv.TypeCode != (int)DxfCode.Text) continue;
                    string text = Convert.ToString(tv.Value) ?? string.Empty;

                    if (string.Equals(text, XRecordName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (kindText == null && Enum.TryParse(text, ignoreCase: true, out BomTableKind parsedKind))
                    {
                        kindText = parsedKind.ToString();
                        continue;
                    }

                    if (formatText == null && IsKnownFormat(text))
                    {
                        formatText = NormalizeFormat(text);
                    }
                }

                if (kindText == null || !Enum.TryParse(kindText, ignoreCase: true, out BomTableKind kind))
                    return false;

                info = new BomTableInfo(table.ObjectId, kind, NormalizeFormat(formatText));
                return true;
            }
            catch
            {
                info = null;
                return false;
            }
        }

        public static string NormalizeFormat(string? format)
        {
            return string.Equals(format, FormatTitleblock, StringComparison.OrdinalIgnoreCase)
                ? FormatTitleblock
                : FormatWide;
        }

        public static bool IsTitleblock(string? format)
        {
            return string.Equals(format, FormatTitleblock, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownFormat(string? format)
        {
            return string.Equals(format, FormatWide, StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, FormatTitleblock, StringComparison.OrdinalIgnoreCase);
        }
    }
}
