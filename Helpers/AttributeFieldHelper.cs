using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Shared helpers for handling AutoCAD attribute fields when block definitions
    /// are imported from external DWG files via WblockCloneObjects.
    ///
    /// <para><b>Why this class exists</b></para>
    /// WblockCloneObjects is the correct (and only) AutoCAD API for importing block
    /// definitions from one drawing to another when the destination might already
    /// contain a definition with the same name. (Database.Insert throws
    /// eSelfReference in that case — see Autodesk Community 11678218.)
    ///
    /// However, WblockCloneObjects has a documented limitation since its inception:
    /// when DuplicateRecordCloning.Replace is used and the destination BlockTableRecord
    /// already exists, AutoCAD does NOT copy the ExtensionDictionary entries of cloned
    /// entities (Autodesk Community 10382033). AttributeDefinition Field objects live
    /// in the AD's ExtensionDictionary under ACAD_FIELD/TEXT, so they vanish from the
    /// destination definition.
    ///
    /// <see cref="PatchFieldsFromSource"/> works around this by reading the field code
    /// (the %&lt;…&gt;% expression) from each source AD via Field.GetFieldCode() and
    /// rebuilding the Field on the matching destination AD via AttributeDefinition.SetField().
    ///
    /// <para><b>Call sequence for every block import</b></para>
    ///   1. <c>ImportBlockDefinitionFromFile(...)</c>          — the original WblockCloneObjects-based import (unchanged)
    ///   2. <c>AttributeFieldHelper.PatchFieldsFromSource(...)</c> — copy Field expressions onto cloned ADs
    ///   3. Create the BlockReference inside a transaction
    ///   4. <c>AttributeFieldHelper.InitializeAttributesOnInsert(tr, br)</c> — create ARs with Fields
    ///   5. Commit transaction
    ///   6. <c>AttributeFieldHelper.EvaluateFieldsNow()</c>    — force field evaluation
    /// </summary>
    internal static class AttributeFieldHelper
    {
        // -------------------------------------------------------------------------
        // Field patching after WblockCloneObjects import
        // -------------------------------------------------------------------------

        /// <summary>
        /// After a block definition has been imported via WblockCloneObjects, reads the
        /// AttributeDefinition Field expressions from the source DWG and re-applies them
        /// to the matching ADs in the destination database.
        ///
        /// This is necessary because WblockCloneObjects does not copy ExtensionDictionary
        /// contents when the destination BTR already exists. Fields live in the
        /// ExtensionDictionary, so without this patch step they are silently lost on
        /// any Replace import (and also on the first import if the destination already
        /// had a same-named BTR for any reason).
        ///
        /// Matching is by AttributeDefinition.Tag. If a tag in the source has no live
        /// Field but its TextString contains a %&lt;…&gt;% code, the code is used to
        /// build the Field.
        ///
        /// Safe to call after any import — does nothing if the source DWG cannot be
        /// opened, the named definition is missing on either side, or no ADs have Fields.
        /// </summary>
        /// <param name="destDb">The drawing the block was just imported into.</param>
        /// <param name="sourceDwgPath">The DWG file the block was imported from.</param>
        /// <param name="blockName">The name of the imported BlockTableRecord in destDb.</param>
        public static void PatchFieldsFromSource(Database destDb, string sourceDwgPath, string blockName)
        {
            if (destDb == null) return;
            if (string.IsNullOrWhiteSpace(sourceDwgPath) || !File.Exists(sourceDwgPath)) return;
            if (string.IsNullOrWhiteSpace(blockName)) return;

            // ── Step 1: harvest tag → field code map from the source DWG ─────────────
            var fieldCodeByTag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var srcDb = new Database(false, true);
                srcDb.ReadDwgFile(sourceDwgPath, System.IO.FileShare.Read, true, null);
                srcDb.CloseInput(true);

                using var trSrc = srcDb.TransactionManager.StartTransaction();
                var sbt = (BlockTable)trSrc.GetObject(srcDb.BlockTableId, OpenMode.ForRead);

                // Find the source definition. The block we just imported may not have
                // the same name in srcDb — try blockName, file stem, and the file's
                // single model-space reference's definition.
                BlockTableRecord? srcBtr = null;
                if (sbt.Has(blockName))
                    srcBtr = (BlockTableRecord)trSrc.GetObject(sbt[blockName], OpenMode.ForRead);
                else
                {
                    var stem = Path.GetFileNameWithoutExtension(sourceDwgPath) ?? string.Empty;
                    if (sbt.Has(stem))
                        srcBtr = (BlockTableRecord)trSrc.GetObject(sbt[stem], OpenMode.ForRead);
                }

                if (srcBtr == null)
                {
                    // Fall back: pick the first non-layout, non-anonymous BTR with attribute defs.
                    foreach (ObjectId id in sbt)
                    {
                        var b = (BlockTableRecord)trSrc.GetObject(id, OpenMode.ForRead);
                        if (b.IsLayout) continue;
                        if (b.Name.StartsWith("*", StringComparison.Ordinal)) continue;
                        if (!b.HasAttributeDefinitions) continue;
                        srcBtr = b; break;
                    }
                }

                if (srcBtr != null)
                {
                    foreach (ObjectId id in srcBtr)
                    {
                        if (trSrc.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad) continue;
                        if (ad.Constant) continue;

                        string? code = ExtractFieldCode(trSrc, ad);
                        if (!string.IsNullOrWhiteSpace(code))
                            fieldCodeByTag[ad.Tag] = code!;
                    }
                }

                trSrc.Commit();
            }
            catch
            {
                // Source unreadable — silently skip; the import already happened, the
                // worst case is missing fields which the user can re-import to fix.
                return;
            }

            if (fieldCodeByTag.Count == 0) return; // no fields to patch

            // ── Step 2: apply field codes to matching ADs in destDb ──────────────────
            using var trDst = destDb.TransactionManager.StartTransaction();
            var dbt = (BlockTable)trDst.GetObject(destDb.BlockTableId, OpenMode.ForRead);
            if (!dbt.Has(blockName)) { trDst.Commit(); return; }

            var dstBtr = (BlockTableRecord)trDst.GetObject(dbt[blockName], OpenMode.ForRead);
            if (!dstBtr.HasAttributeDefinitions) { trDst.Commit(); return; }

            foreach (ObjectId id in dstBtr)
            {
                if (trDst.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad) continue;
                if (ad.Constant) continue;
                if (!fieldCodeByTag.TryGetValue(ad.Tag, out var code)) continue;

                // Skip if the AD already has a live Field with the same code.
                if (ad.HasFields)
                {
                    try
                    {
                        var existingId = ad.GetField();
                        if (!existingId.IsNull && existingId.IsValid) continue;
                    }
                    catch { /* fall through and re-attach */ }
                }

                var adw = (AttributeDefinition)trDst.GetObject(ad.ObjectId, OpenMode.ForWrite);
                try
                {
                    using var f = new Field(code);
                    adw.SetField(f);
                }
                catch { /* skip ADs that refuse the field code */ }
            }

            trDst.Commit();
        }

        /// <summary>
        /// Reads the field code (the raw %&lt;…&gt;% expression) attached to an
        /// AttributeDefinition. Tries Field.GetFieldCode() first, then falls back to
        /// scanning TextString for a %&lt;…&gt;% literal.
        /// Returns null when no field code can be recovered.
        /// </summary>
        private static string? ExtractFieldCode(Transaction tr, AttributeDefinition ad)
        {
            try
            {
                if (ad.HasFields)
                {
                    var fldId = ad.GetField();
                    if (!fldId.IsNull && fldId.IsValid)
                    {
                        var fld = (Field)tr.GetObject(fldId, OpenMode.ForRead);
                        // FieldCodeFlags.AddMarkers returns the full %<…>% expression
                        // (the same form AutoCAD writes to DXF).
                        var code = fld.GetFieldCode(FieldCodeFlags.AddMarkers);
                        if (!string.IsNullOrWhiteSpace(code)) return code;
                    }
                }
            }
            catch { /* fall through to TextString scan */ }

            // Some legacy ADs carry the field code inline in TextString.
            var txt = ad.TextString;
            if (!string.IsNullOrWhiteSpace(txt)
                && txt.Contains("%<", StringComparison.Ordinal)
                && txt.Contains(">%", StringComparison.Ordinal))
            {
                return txt;
            }
            return null;
        }

        // -------------------------------------------------------------------------
        // Attribute / Field initialisation on insert
        // -------------------------------------------------------------------------

        /// <summary>
        /// Creates AttributeReferences for every non-constant AttributeDefinition in
        /// the block definition and attaches live Field expressions where the definition
        /// carries them.
        ///
        /// Call inside an open transaction immediately after appending the
        /// BlockReference to its owner space and before committing.
        /// </summary>
        public static void InitializeAttributesOnInsert(Transaction tr, BlockReference br)
        {
            if (tr == null) throw new ArgumentNullException(nameof(tr));
            if (br == null) throw new ArgumentNullException(nameof(br));

            var defId = !br.DynamicBlockTableRecord.IsNull
                ? br.DynamicBlockTableRecord
                : br.BlockTableRecord;

            var btr = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
            if (!btr.HasAttributeDefinitions) return;

            var arByTag = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId arId in br.AttributeCollection)
            {
                if (tr.GetObject(arId, OpenMode.ForRead) is AttributeReference existingAr)
                    arByTag[existingAr.Tag] = existingAr;
            }

            BlockTableRecord? ownerSpace = null;

            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad) continue;
                if (ad.Constant) continue;

                if (arByTag.TryGetValue(ad.Tag, out var existingAr))
                {
                    var arw = (AttributeReference)tr.GetObject(existingAr.ObjectId, OpenMode.ForWrite);
                    bool hadField = arw.HasFields;
                    string oldVal = arw.TextString;

                    arw.SetAttributeFromBlock(ad, br.BlockTransform);

                    if (HasFieldOnDefinition(ad))
                    {
                        if (!arw.HasFields)
                            CopyFieldFromDefinition(tr, ad, arw);
                    }
                    else
                    {
                        if (!hadField && !arw.HasFields)
                            arw.TextString = oldVal;
                    }
                    continue;
                }

                // CRITICAL ORDERING — see notes below.
                //
                // The AttributeReference must be appended to the BlockReference and
                // registered with the transaction BEFORE SetAttributeFromBlock is called.
                // Otherwise the Field that SetAttributeFromBlock attaches lives on a
                // detached, non-database AttributeReference. The symptom is:
                //   newAr.HasFields == true  (AutoCAD thinks a field is attached)
                //   newAr.GetField() throws eNotInDatabase
                // and at REGEN time the field code's "?BlockRefId" placeholder cannot
                // be resolved to the parent BlockReference's handle — producing the
                // truncated field codes the user reported (e.g. "%<\AcObjProp \f ...>%").
                //
                // The correct ordering is:
                //   1. Create the bare AttributeReference
                //   2. Append it to the BlockReference's AttributeCollection
                //   3. Add it to the transaction (AddNewlyCreatedDBObject)
                //   4. NOW call SetAttributeFromBlock — the AR is database-resident
                //      so the Field can be attached as a real database object and the
                //      ?BlockRefId placeholder resolves correctly.
                var newAr = new AttributeReference();

                if (ownerSpace == null)
                    ownerSpace = (BlockTableRecord)tr.GetObject(br.OwnerId, OpenMode.ForWrite);

                br.AttributeCollection.AppendAttribute(newAr);
                tr.AddNewlyCreatedDBObject(newAr, true);

                // Now SetAttributeFromBlock attaches the AD's Field to a database-
                // resident AR, so the Field is real and queryable.
                newAr.SetAttributeFromBlock(ad, br.BlockTransform);

                if (HasFieldOnDefinition(ad))
                {
                    // Fallback only if SetAttributeFromBlock did not carry the field.
                    if (!newAr.HasFields)
                        CopyFieldFromDefinition(tr, ad, newAr);
                }
                else
                {
                    newAr.TextString = ad.TextString;
                }
            }
        }

        /// <summary>
        /// ATTSYNC-equivalent: walks every BlockReference of blockName in Model Space
        /// and Paper Space and ensures its AttributeReferences match the current
        /// AttributeDefinitions. Re-attaches Fields where the AD has one and the AR
        /// does not. Preserves user-entered values otherwise.
        /// </summary>
        public static int ResyncAllBlockReferences(Database db, string blockName, bool removeOrphaned = false)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(blockName)) throw new ArgumentNullException(nameof(blockName));

            int updated = 0;
            using var tr = db.TransactionManager.StartTransaction();

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(blockName)) { tr.Commit(); return 0; }

            var btrDef = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);

            var defByTag = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in btrDef)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                    defByTag[ad.Tag] = ad;
            }

            ObjectId[] spaceIds =
            {
                bt[BlockTableRecord.ModelSpace],
                bt[BlockTableRecord.PaperSpace]
            };

            foreach (var spaceId in spaceIds)
            {
                var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);

                foreach (ObjectId entId in space)
                {
                    if (tr.GetObject(entId, OpenMode.ForRead) is not BlockReference br) continue;

                    var refDefId = !br.DynamicBlockTableRecord.IsNull
                        ? br.DynamicBlockTableRecord
                        : br.BlockTableRecord;
                    if (refDefId != btrDef.ObjectId) continue;

                    var arByTag = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId arId in br.AttributeCollection)
                    {
                        if (tr.GetObject(arId, OpenMode.ForRead) is AttributeReference ar)
                            arByTag[ar.Tag] = ar;
                    }

                    bool changed = false;

                    foreach (var kvp in defByTag)
                    {
                        var tag = kvp.Key;
                        var ad = kvp.Value;

                        if (!arByTag.TryGetValue(tag, out var existingAr))
                        {
                            space.UpgradeOpen();
                            var brw = (BlockReference)tr.GetObject(entId, OpenMode.ForWrite);

                            // See notes in InitializeAttributesOnInsert: AR must be
                            // appended to the BR and added to the transaction BEFORE
                            // SetAttributeFromBlock can attach a queryable Field.
                            var newAr = new AttributeReference();
                            brw.AttributeCollection.AppendAttribute(newAr);
                            tr.AddNewlyCreatedDBObject(newAr, true);

                            newAr.SetAttributeFromBlock(ad, brw.BlockTransform);

                            if (HasFieldOnDefinition(ad))
                            {
                                if (!newAr.HasFields)
                                    CopyFieldFromDefinition(tr, ad, newAr);
                            }
                            else
                            {
                                newAr.TextString = ad.TextString;
                            }
                            changed = true;
                            continue;
                        }

                        var arw = (AttributeReference)tr.GetObject(existingAr.ObjectId, OpenMode.ForWrite);
                        bool hadField = arw.HasFields;
                        string oldVal = arw.TextString;

                        arw.SetAttributeFromBlock(ad, br.BlockTransform);

                        if (HasFieldOnDefinition(ad))
                        {
                            if (!arw.HasFields)
                                CopyFieldFromDefinition(tr, ad, arw);
                        }
                        else
                        {
                            if (!hadField && !arw.HasFields)
                                arw.TextString = oldVal;
                        }
                        changed = true;
                    }

                    if (removeOrphaned && arByTag.Count > 0)
                    {
                        foreach (var ar in arByTag.Values)
                        {
                            if (!defByTag.ContainsKey(ar.Tag))
                            {
                                var arw = (AttributeReference)tr.GetObject(ar.ObjectId, OpenMode.ForWrite);
                                arw.Erase();
                                changed = true;
                            }
                        }
                    }

                    if (changed) updated++;
                }
            }

            tr.Commit();
            return updated;
        }

        /// <summary>
        /// Sends a REGEN so AutoCAD evaluates all pending field expressions. Call after
        /// the insert transaction commits, outside any open transaction.
        /// </summary>
        public static void EvaluateFieldsNow()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute("_.REGEN ", true, false, false);
            }
            catch { /* non-fatal */ }
        }

        // -------------------------------------------------------------------------
        // Internal helpers
        // -------------------------------------------------------------------------

        internal static bool HasFieldOnDefinition(AttributeDefinition ad)
        {
            try
            {
                if (!ad.HasFields) return false;
                var fldId = ad.GetField();
                return !fldId.IsNull && fldId.IsValid;
            }
            catch { return false; }
        }

        /// <summary>
        /// Attaches a Field to <paramref name="ar"/> derived from <paramref name="ad"/>.
        ///
        /// This is a fallback path only — SetAttributeFromBlock should normally have
        /// already transferred the AD's field via AutoCAD's internal copy mechanism
        /// (which correctly preserves Object() references). Callers should guard with
        /// <c>if (!ar.HasFields)</c> before invoking this method.
        ///
        /// Strategy order:
        ///   1. Clone the live Field object directly. Field.Clone preserves the full
        ///      internal representation including embedded Object(\_ObjId X) references,
        ///      whereas Field.GetFieldCode() + new Field(code) silently strips object
        ///      references that the text serializer cannot persist.
        ///   2. Build a Field from a %&lt;…&gt;% expression stored inline in TextString.
        ///      Only applies to legacy ADs where the field code is in the text, not
        ///      attached as a real Field object.
        ///   3. Plain literal text fallback.
        /// </summary>
        internal static void CopyFieldFromDefinition(Transaction tr, AttributeDefinition ad, AttributeReference ar)
        {
            try
            {
                // Strategy 1: direct Field.Clone of the live Field object.
                // Do NOT round-trip through GetFieldCode/new Field — that strips
                // Object(\_ObjId X) references on fields that target other entities.
                ObjectId fldId = ObjectId.Null;
                try { fldId = ad.GetField(); } catch { }

                if (!fldId.IsNull && fldId.IsValid)
                {
                    var srcFld = (Field)tr.GetObject(fldId, OpenMode.ForRead);
                    if (srcFld != null)
                    {
                        ar.SetField((Field)srcFld.Clone());
                        return;
                    }
                }

                // Strategy 2: build from %<…>% expression stored in TextString.
                // Safe here because TextString-embedded codes don't carry ObjectId refs;
                // they're self-contained (date, sheet number, user variable, etc).
                var inline = ad.TextString;
                if (!string.IsNullOrWhiteSpace(inline)
                    && inline.Contains("%<", StringComparison.Ordinal)
                    && inline.Contains(">%", StringComparison.Ordinal))
                {
                    ar.SetField(new Field(inline));
                    return;
                }

                // Strategy 3: plain literal text.
                if (!ar.HasFields) ar.TextString = ad.TextString;
            }
            catch
            {
                if (!ar.HasFields) ar.TextString = ad.TextString;
            }
        }
    }
}