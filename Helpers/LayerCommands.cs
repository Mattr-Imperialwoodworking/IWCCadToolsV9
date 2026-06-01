using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using IWCCadToolsV9.Data;
using Microsoft.Data.SqlClient;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Color = Autodesk.AutoCAD.Colors.Color;
using Exception = System.Exception;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Command → Layer router and keyboard shortcut handler.
    ///
    /// Behavior:
    ///  • Intercepts AutoCAD commands and automatically switches CLAYER
    ///    based on a map loaded from dbo.DWG_LayerCmd_Assoc / dbo.Dwg_Layer.
    ///  • Restores the previous layer when the command finishes, cancels, or fails.
    ///  • Handles "H&lt;key&gt;" / "L&lt;key&gt;" unknown commands to
    ///    create/activate layers by LayerKey in dbo.Dwg_Layer.
    /// </summary>
    public static class CommandLayerRouter
    {
        // ---------------------------------------------------------------------------
        // Tables
        // ---------------------------------------------------------------------------

        private const string LayersTable = "dbo.Dwg_Layer";
        private const string AssocTable  = "dbo.DWG_LayerCmd_Assoc";

        // ---------------------------------------------------------------------------
        // In-memory maps / caches
        // ---------------------------------------------------------------------------

        private static readonly Dictionary<string, string>    _cmdLayerMap  = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LayerDef>  _layerDefCache = new(StringComparer.OrdinalIgnoreCase);

        // Per-document restore stack (weak-referenced so stale docs are GC'd)
        private static readonly ConditionalWeakTable<Document, Stack<LayerRestoreEntry>>
            _restoreStacks = new();

        private static bool _isHooked;

        private static readonly HashSet<string> _ignorePrefixes =
            new(StringComparer.OrdinalIgnoreCase) { "IWC", "NETLOAD" };

        // Shortcut pattern: H1, L1, HNOTES, etc.
        private static readonly Regex _shortcutRegex =
            new(@"^(?:H|L)(?<key>[A-Za-z0-9_\-]+)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---------------------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------------------

        [CommandMethod("IWCInitCommandLayerRouter")]
        public static void Initialize()
        {
            if (_isHooked) return;

            LoadMapFromDatabase();

            var dm = Application.DocumentManager;
            foreach (Document doc in dm) AttachToDocument(doc);
            dm.DocumentCreated       += (_, e) => AttachToDocument(e.Document);
            dm.DocumentToBeDestroyed += (_, e) => DetachFromDocument(e.Document);

            _isHooked = true;
            Application.DocumentManager.MdiActiveDocument?
                .Editor.WriteMessage("\n[IWC] Command→Layer router initialized.");
        }

        [CommandMethod("IWCReloadCommandLayerMap")]
        public static void ReloadMap()
        {
            LoadMapFromDatabase();
            Application.DocumentManager.MdiActiveDocument?
                .Editor.WriteMessage("\n[IWC] Command→Layer map reloaded.");
        }

        // ---------------------------------------------------------------------------
        // Document event wiring
        // ---------------------------------------------------------------------------

        private static void AttachToDocument(Document doc)
        {
            if (doc == null) return;
            _restoreStacks.GetOrCreateValue(doc);

            doc.CommandWillStart  -= OnCommandWillStart;
            doc.CommandWillStart  += OnCommandWillStart;
            doc.CommandEnded      -= OnCommandFinished;
            doc.CommandEnded      += OnCommandFinished;
            doc.CommandCancelled  -= OnCommandFinished;
            doc.CommandCancelled  += OnCommandFinished;
            doc.CommandFailed     -= OnCommandFinished;
            doc.CommandFailed     += OnCommandFinished;
            doc.UnknownCommand    -= OnUnknownCommand;
            doc.UnknownCommand    += OnUnknownCommand;
        }

        private static void DetachFromDocument(Document doc)
        {
            if (doc == null) return;
            doc.CommandWillStart  -= OnCommandWillStart;
            doc.CommandEnded      -= OnCommandFinished;
            doc.CommandCancelled  -= OnCommandFinished;
            doc.CommandFailed     -= OnCommandFinished;
            doc.UnknownCommand    -= OnUnknownCommand;
        }

        // ---------------------------------------------------------------------------
        // Event handlers
        // ---------------------------------------------------------------------------

        private static void OnCommandWillStart(object? sender,  CommandEventArgs e)
        {
            try
            {
                var doc = sender as Document ?? Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                var cmd = e.GlobalCommandName ?? string.Empty;
                if (cmd.Length == 0) return;

                foreach (var prefix in _ignorePrefixes)
                    if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

                if (!_cmdLayerMap.TryGetValue(cmd.ToUpperInvariant(), out var targetLayer)
                    || string.IsNullOrWhiteSpace(targetLayer))
                    return;

                if (TrySwitchLayer(doc, targetLayer, cmd, out var entry))
                    _restoreStacks.GetOrCreateValue(doc).Push(entry);
            }
            catch { }
        }

        private static void OnCommandFinished(object? sender,  CommandEventArgs e)
        {
            try
            {
                var doc = sender as Document ?? Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                if (!_restoreStacks.TryGetValue(doc, out var stack) || stack.Count == 0) return;
                RestoreLayer(doc, stack.Pop());
            }
            catch { }
        }

        private static void OnUnknownCommand(object? sender,  UnknownCommandEventArgs e)
        {
            try
            {
                var m = _shortcutRegex.Match(e.GlobalCommandName ?? string.Empty);
                if (!m.Success) return;

                var doc = sender as Document ?? Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                string key = m.Groups["key"].Value;
                if (MakeLayerCurrentByKey(doc, key))
                    doc.Editor.WriteMessage($"\n[IWC] Layer for key '{key}' activated.");
                else
                    doc.Editor.WriteMessage($"\n[IWC] No layer found for LayerKey='{key}'.");
            }
            catch { }
        }

        // ---------------------------------------------------------------------------
        // Core layer switch / restore
        // ---------------------------------------------------------------------------

        private static bool TrySwitchLayer(Document doc, string targetLayerName,
            string cmd, out LayerRestoreEntry entry)
        {
            entry = default;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                ObjectId prevId   = db.Clayer;
                string?  prevName = null;
                try
                {
                    prevName = ((LayerTableRecord)tr.GetObject(prevId, OpenMode.ForRead)).Name;
                }
                catch { }

                ObjectId targetId = EnsureLayerExists(db, tr, lt, targetLayerName);

                if (prevId == targetId) { tr.Commit(); return false; }

                db.Clayer = targetId;
                tr.Commit();

                entry = new LayerRestoreEntry(prevId, prevName, cmd);
                return true;
            }
        }

        private static void RestoreLayer(Document doc, LayerRestoreEntry entry)
        {
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId restoreId = ObjectId.Null;

                if (!entry.PrevLayerId.IsNull)
                {
                    try
                    {
                        tr.GetObject(entry.PrevLayerId, OpenMode.ForRead);
                        restoreId = entry.PrevLayerId;
                    }
                    catch { }
                }

                if (restoreId.IsNull && !string.IsNullOrWhiteSpace(entry.PrevLayerName)
                    && lt.Has(entry.PrevLayerName))
                    restoreId = lt[entry.PrevLayerName];

                if (!restoreId.IsNull) db.Clayer = restoreId;
                tr.Commit();
            }
        }

        private static bool MakeLayerCurrentByKey(Document doc, string layerKey)
        {
            var def = ReadLayerDefinitionByKey(layerKey);
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) return false;

            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt  = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var lid = EnsureLayerExists(db, tr, lt, def.Name);
                db.Clayer = lid;
                tr.Commit();
            }
            return true;
        }

        private static ObjectId EnsureLayerExists(Database db, Transaction tr,
            LayerTable lt, string layerName)
        {
            if (lt.Has(layerName)) return lt[layerName];

            lt.UpgradeOpen();
            var newLtr = new LayerTableRecord { Name = layerName };
            var id     = lt.Add(newLtr);
            tr.AddNewlyCreatedDBObject(newLtr, true);

            var ltrW = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
            ApplyDefinition(db, tr, ltrW, layerName);
            return id;
        }

        // ---------------------------------------------------------------------------
        // Apply DB layer definition
        // ---------------------------------------------------------------------------

        private static void ApplyDefinition(Database db, Transaction tr,
            LayerTableRecord ltr, string layerName)
        {
            if (!_layerDefCache.TryGetValue(layerName, out var def))
            {
                def = ReadLayerDefinition(layerName);
                if (def != null) _layerDefCache[layerName] = def;
            }
            if (def == null) return;

            // Color
            if (def.ColorType == 1 && def.ColorIndex.HasValue)
            {
                short idx = (short)Math.Clamp(def.ColorIndex.Value, 1, 255);
                ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, idx);
            }
            else if (def.ColorType == 2 && !string.IsNullOrWhiteSpace(def.ColorHex))
            {
                var (r, g, b) = ParseHexColor(def.ColorHex);
                ltr.Color = Color.FromRgb((byte)r, (byte)g, (byte)b);
            }

            // Linetype
            var ltt    = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            string ltn = string.IsNullOrWhiteSpace(def.LinetypeName) ? "Continuous" : def.LinetypeName;
            if (!ltt.Has(ltn)) { try { db.LoadLineTypeFile(ltn, "acad.lin"); } catch { } }
            if (!ltt.Has(ltn)) ltn = "Continuous";
            if (ltt.Has(ltn))  ltr.LinetypeObjectId = ltt[ltn];

            // Plottable
            if (def.LayerPrint.HasValue) ltr.IsPlottable = def.LayerPrint.Value;

            // Transparency
            if (def.TransparencyPct.HasValue)
            {
                try
                {
                    byte alpha = (byte)Math.Round(Math.Clamp(def.TransparencyPct.Value, 0, 90) * 2.55);
                    ltr.Transparency = new Transparency(alpha);
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------------------
        // DB queries
        // ---------------------------------------------------------------------------

        private static void LoadMapFromDatabase()
        {
            _cmdLayerMap.Clear();
            const string sql = $@"
                SELECT UPPER(LTRIM(RTRIM(A.AcadCmd))) AS Cmd, L.LayerName
                  FROM {AssocTable}  A
                  JOIN {LayersTable} L ON L.ID = A.LayerID
                 WHERE NULLIF(LTRIM(RTRIM(A.AcadCmd)),  '') IS NOT NULL
                   AND NULLIF(LTRIM(RTRIM(L.LayerName)),'') IS NOT NULL;";

            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();
                using var cmd = new SqlCommand(sql, conn.OpenConn);
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var cmdName    = (rdr["Cmd"]       as string)?.Trim();
                    var layerName  = (rdr["LayerName"] as string)?.Trim();
                    if (!string.IsNullOrEmpty(cmdName) && !string.IsNullOrEmpty(layerName))
                        _cmdLayerMap[cmdName] = layerName;
                }
            }
            catch (Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?
                    .Editor.WriteMessage($"\n[IWC] Failed loading Command→Layer map: {ex.Message}");
            }
        }

        private static LayerDef? ReadLayerDefinition(string layerName)
            => QueryLayerDef("LayerName = @p", "@p", layerName);

        private static LayerDef? ReadLayerDefinitionByKey(string layerKey)
            => QueryLayerDef("LTRIM(RTRIM(LayerKey)) = LTRIM(RTRIM(@p))", "@p", layerKey);

        private static LayerDef? QueryLayerDef(string whereClause, string paramName, string paramValue)
        {
            string sql = $@"
                SELECT LayerName, LayerDesc,
                       LayerColorType, LayerColorIndex, LayerColorCustom,
                       LayerLineType, LayerPrint, LayerTransparent
                  FROM {LayersTable}
                 WHERE {whereClause}";
            try
            {
                using var conn = new IWCConn();
                conn.DBConnect();
                using var cmd  = new SqlCommand(sql, conn.OpenConn);
                cmd.Parameters.AddWithValue(paramName, paramValue);
                using var rdr  = cmd.ExecuteReader();
                if (!rdr.Read()) return null;

                return new LayerDef
                {
                    Name           = (rdr["LayerName"]      as string)?.Trim(),
                    Desc           = (rdr["LayerDesc"]      as string)?.Trim(),
                    ColorType      = rdr["LayerColorType"]  is DBNull ? 0  : Convert.ToInt32(rdr["LayerColorType"]),
                    ColorIndex     = rdr["LayerColorIndex"] is DBNull ? null : Convert.ToInt32(rdr["LayerColorIndex"]),
                    ColorHex       = (rdr["LayerColorCustom"] as string)?.Trim(),
                    LinetypeName   = (rdr["LayerLineType"]    as string)?.Trim(),
                    LayerPrint     = rdr["LayerPrint"]       is DBNull ? null : Convert.ToBoolean(rdr["LayerPrint"]),
                    TransparencyPct = rdr["LayerTransparent"] is DBNull ? null : Convert.ToInt32(rdr["LayerTransparent"]),
                };
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------------------
        // Utility
        // ---------------------------------------------------------------------------

        private static (int R, int G, int B) ParseHexColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return (0, 0, 0);
            var s = hex.TrimStart('#');
            if (s.Length < 6) return (0, 0, 0);
            try
            {
                return (Convert.ToInt32(s[..2], 16),
                        Convert.ToInt32(s.Substring(2, 2), 16),
                        Convert.ToInt32(s.Substring(4, 2), 16));
            }
            catch { return (0, 0, 0); }
        }

        // ---------------------------------------------------------------------------
        // Private types
        // ---------------------------------------------------------------------------

        private sealed class LayerDef
        {
            public string? Name           { get; set; }
            public string? Desc           { get; set; }
            public int     ColorType      { get; set; }
            public int?    ColorIndex     { get; set; }
            public string? ColorHex       { get; set; }
            public string? LinetypeName   { get; set; }
            public bool?   LayerPrint     { get; set; }
            public int?    TransparencyPct { get; set; }
        }

        private readonly struct LayerRestoreEntry
        {
            public LayerRestoreEntry(ObjectId prevLayerId, string? prevLayerName, string cmd)
            {
                PrevLayerId   = prevLayerId;
                PrevLayerName = prevLayerName;
                CommandName   = cmd;
            }
            public ObjectId PrevLayerId   { get; }
            public string?  PrevLayerName { get; }
            public string   CommandName   { get; }
        }
    }
}
