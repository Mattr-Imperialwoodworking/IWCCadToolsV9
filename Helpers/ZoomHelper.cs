using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// AutoCAD view / zoom utilities.
    /// </summary>
    public static class ZoomHelper
    {
        // ---------------------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Zooms the current document to the extents of all visible geometry in
        /// the active space.
        /// </summary>
        [CommandMethod("IWCZoomExtents")]
        public static void ZoomExtentsActiveSpace()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;
            var db  = doc.Database;

            using var tr = db.TransactionManager.StartTransaction();

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            Extents3d? combined = null;

            foreach (ObjectId entId in space)
            {
                if (!entId.IsValid || entId.IsErased) continue;

                var ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                try
                {
                    var ext = ent.GeometricExtents;
                    combined = combined.HasValue ? Union(combined.Value, ext) : ext;
                }
                catch { /* skip entities without geometric extents */ }
            }

            if (combined.HasValue)
                SetView(ed, combined.Value);
            else
                ed.WriteMessage("\nNo visible geometry found in active space.");

            tr.Commit();
        }

        // ---------------------------------------------------------------------------
        // Public API (called from block icon generator and palette)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Activates a named open document and zooms to all of its model-space geometry.
        /// </summary>
        public static void ZoomToDocumentExtents(string docFileName)
        {
            var doc = FindOpenDocumentByFileName(docFileName);
            if (doc == null)
            {
                Application.ShowAlertDialog($"Document '{docFileName}' is not open.");
                return;
            }

            using (doc.LockDocument())
            {
                Application.DocumentManager.CurrentDocument = doc;
                var ids = GetModelSpaceObjectIds(doc.Database);
                if (ids.Count > 0)
                    ZoomObjects(ids, doc);
            }
        }

        /// <summary>
        /// Zooms the given document's editor to fit the supplied set of object IDs.
        /// </summary>
        public static void ZoomObjects(ObjectIdCollection idCol, Document doc)
        {
            if (idCol == null || idCol.Count == 0) return;

            var db = doc.Database;
            var ed = doc.Editor;

            using var tr   = db.TransactionManager.StartTransaction();
            using var view = ed.GetCurrentView();

            var wcs2dcs = BuildWcs2Dcs(view);

            var ent = (Entity)tr.GetObject(idCol[0], OpenMode.ForRead);
            var ext = ent.GeometricExtents;
            ext.TransformBy(wcs2dcs);

            for (int i = 1; i < idCol.Count; i++)
            {
                ent = (Entity)tr.GetObject(idCol[i], OpenMode.ForRead);
                var tmp = ent.GeometricExtents;
                tmp.TransformBy(wcs2dcs);
                ext.AddExtents(tmp);
            }

            double ratio  = view.Width / view.Height;
            double width  = ext.MaxPoint.X - ext.MinPoint.X;
            double height = ext.MaxPoint.Y - ext.MinPoint.Y;
            if (width > height * ratio) height = width / ratio;

            var center = new Point2d(
                (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0);

            view.Height      = height * 2;
            view.Width       = width  * 2;
            view.CenterPoint = center;
            ed.SetCurrentView(view);

            tr.Commit();
        }

        /// <summary>
        /// Collects all non-erased object IDs from the model space of <paramref name="db"/>.
        /// </summary>
        public static ObjectIdCollection GetModelSpaceObjectIds(Database db)
        {
            var ids = new ObjectIdCollection();
            using var tr = db.TransactionManager.StartTransaction();

            var bt  = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms  = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (!id.IsErased)
                    ids.Add(id);
            }

            tr.Commit();
            return ids;
        }

        // ---------------------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------------------

        private static void SetView(Editor ed, Extents3d ext)
        {
            var ucs = ed.CurrentUserCoordinateSystem;
            ext.TransformBy(ucs.Inverse());

            var geomCenter = new Point3d(
                (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0,
                0);

            double geomWidth  = ext.MaxPoint.X - ext.MinPoint.X;
            double geomHeight = ext.MaxPoint.Y - ext.MinPoint.Y;

            var currentView  = ed.GetCurrentView();
            double viewAspect = currentView.Width / currentView.Height;

            double zoomWidth  = geomWidth;
            double zoomHeight = geomHeight;

            if (zoomWidth / Math.Max(zoomHeight, 1e-9) > viewAspect)
                zoomHeight = zoomWidth / viewAspect;
            else
                zoomWidth  = zoomHeight * viewAspect;

            var newView = new ViewTableRecord
            {
                CenterPoint    = new Point2d(geomCenter.X, geomCenter.Y),
                Target         = geomCenter,
                ViewDirection  = currentView.ViewDirection,
                Width          = zoomWidth,
                Height         = zoomHeight,
            };

            ed.SetCurrentView(newView);
        }

        private static Extents3d Union(Extents3d a, Extents3d b) =>
            new(
                new Point3d(
                    Math.Min(a.MinPoint.X, b.MinPoint.X),
                    Math.Min(a.MinPoint.Y, b.MinPoint.Y),
                    Math.Min(a.MinPoint.Z, b.MinPoint.Z)),
                new Point3d(
                    Math.Max(a.MaxPoint.X, b.MaxPoint.X),
                    Math.Max(a.MaxPoint.Y, b.MaxPoint.Y),
                    Math.Max(a.MaxPoint.Z, b.MaxPoint.Z)));

        private static Matrix3d BuildWcs2Dcs(ViewTableRecord view)
        {
            var m = Matrix3d.PlaneToWorld(view.ViewDirection);
            m = Matrix3d.Displacement(view.Target - Point3d.Origin) * m;
            m = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * m;
            return m.Inverse();
        }

        private static Document? FindOpenDocumentByFileName(string fileName)
        {
            var target = Path.GetFileName(fileName).ToLowerInvariant();
            foreach (Document doc in Application.DocumentManager)
            {
                if (Path.GetFileName(doc.Name).ToLowerInvariant() == target)
                    return doc;
            }
            return null;
        }
    }
}
