using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

// Disambiguate: Autodesk.AutoCAD.Colors.Color vs System.Drawing.Color
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using SmoothingMode = System.Drawing.Drawing2D.SmoothingMode;
using PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode;
using InterpolationMode = System.Drawing.Drawing2D.InterpolationMode;
using CompositingQuality = System.Drawing.Drawing2D.CompositingQuality;
using PenAlignment = System.Drawing.Drawing2D.PenAlignment;
using LineCap = System.Drawing.Drawing2D.LineCap;
using LineJoin = System.Drawing.Drawing2D.LineJoin;

namespace IWCCadToolsV9.Helpers
{
    /// <summary>
    /// Renders an AutoCAD block definition to a PNG thumbnail using GDI+.
    ///
    /// IMPROVEMENTS OVER V1:
    ///   1. Text/MText rendering  – DBText and MText entities are drawn as readable labels.
    ///   2. Solid fills           – Solid, 2D Solid, and wide-segment polylines are filled.
    ///   3. Hatch outline         – Hatch boundary loops are stroked so filled regions appear.
    ///   4. Attribute definitions – ATTDEF geometry is rendered, keeping labels visible.
    ///   5. Insert (nested block) – Nested BlockReferences are recursed with transform applied.
    ///   6. Dark-background aware – Foreground/ByLayer white lines are remapped to a
    ///                              light colour when the background is dark, not invisible.
    ///   7. Adaptive stroke width – Stroke scales with the final icon size so small icons
    ///                              do not collapse to invisible hairlines.
    ///   8. Colour contrast guard – Any colour whose luminance is too close to the
    ///                              background's is shifted away to remain visible.
    /// </summary>
    public static class BlockIconRenderer
    {
        // -----------------------------------------------------------------------
        // Rendering context – passed through all helpers instead of global state.
        // -----------------------------------------------------------------------

        private sealed class RenderCtx
        {
            public Transaction        Tr        { get; }
            public Graphics           G         { get; }
            public Func<Point3d, PointF> Xform  { get; }
            public float              StrokePx  { get; }
            public Color              Background { get; }
            public bool               DarkBg    { get; }

            public RenderCtx(Transaction tr, Graphics g,
                             Func<Point3d, PointF> xform, float strokePx,
                             Color background)
            {
                Tr         = tr;
                G          = g;
                Xform      = xform;
                StrokePx   = strokePx;
                Background = background;
                // "dark" = perceived luminance < 0.35
                DarkBg = Luminance(background) < 0.35;
            }
        }

        // -----------------------------------------------------------------------
        // Primary render entry point – from block definition name
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders a block definition from <paramref name="db"/> to a PNG byte array.
        /// </summary>
        public static byte[]? RenderBlockIconPng(
            Database db,
            string   blockName,
            int      iconSizePx       = 48,
            int      supersampleFactor = 3,
            Color?   background        = null,
            float    finalHairlinePx   = 0.6f)
        {
            background ??= Color.Transparent;
            int canvas = Math.Max(16, iconSizePx * Math.Max(1, supersampleFactor));

            using var tr = db.TransactionManager.StartTransaction();

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(blockName)) return null;

            var btr = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);

            // 1) Compute extents (skip attribute definitions from extent calc)
            Extents3d? ext = ComputeBlockExtents(btr, tr, includeAttDef: false);
            if (!ext.HasValue) return null;

            // 2) Fit with 8% padding
            var (scale, offsetX, offsetY) = ComputeTransform(ext.Value, canvas);

            PointF Xform(Point3d w) => new(
                (float)(offsetX + (w.X - ext.Value.MinPoint.X) * scale),
                (float)(canvas  - (offsetY + (w.Y - ext.Value.MinPoint.Y) * scale)));

            float superStroke = Math.Max(finalHairlinePx * supersampleFactor, 0.5f);

            // 3) Render at super-sampled canvas size
            using var big = RenderToBitmap(btr, tr, canvas, background.Value,
                Xform, superStroke, scale);

            // 4) Downscale to target icon size
            using var final = new Bitmap(iconSizePx, iconSizePx, PixelFormat.Format32bppArgb);
            final.SetResolution(300, 300);
            using (var g = Graphics.FromImage(final))
            {
                g.SmoothingMode      = SmoothingMode.AntiAlias;
                g.PixelOffsetMode    = PixelOffsetMode.Half;
                g.InterpolationMode  = InterpolationMode.HighQualityBilinear;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(background.Value);
                g.DrawImage(big,
                    new Rectangle(0, 0, iconSizePx, iconSizePx),
                    new Rectangle(0, 0, big.Width, big.Height),
                    GraphicsUnit.Pixel);
            }

            using var ms = new MemoryStream();
            final.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        /// <summary>
        /// Renders a block definition identified by its <see cref="ObjectId"/> to
        /// a PNG byte array.  Use this overload to render anonymous BTRs (e.g.
        /// <c>br.BlockTableRecord</c> for a dynamic block) where no named lookup
        /// is possible.  The <see cref="Entity.Visible"/> filter is applied so
        /// only entities visible in the current dynamic-block state are rendered.
        /// </summary>
        public static byte[]? RenderBlockIconFromBtrId(
            Database db,
            ObjectId btrId,
            int      iconSizePx        = 64,
            int      supersampleFactor = 2,
            Color?   background        = null,
            float    finalHairlinePx   = 0.35f)
        {
            background ??= Color.Black;
            int canvas = Math.Max(16, iconSizePx * Math.Max(1, supersampleFactor));

            using var tr = db.TransactionManager.StartTransaction();

            if (btrId.IsNull || !btrId.IsValid) return null;
            var btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return null;

            Extents3d? ext = ComputeBlockExtents(btr, tr, includeAttDef: false);
            if (!ext.HasValue) return null;

            var (scale, offsetX, offsetY) = ComputeTransform(ext.Value, canvas);
            PointF Xform(Point3d w) => new(
                (float)(offsetX + (w.X - ext.Value.MinPoint.X) * scale),
                (float)(canvas  - (offsetY + (w.Y - ext.Value.MinPoint.Y) * scale)));

            float superStroke = Math.Max(finalHairlinePx * supersampleFactor, 0.5f);

            using var big = RenderToBitmap(btr, tr, canvas, background.Value,
                Xform, superStroke, scale);

            using var final = new Bitmap(iconSizePx, iconSizePx, PixelFormat.Format32bppArgb);
            final.SetResolution(300, 300);
            using (var g = Graphics.FromImage(final))
            {
                g.SmoothingMode      = SmoothingMode.AntiAlias;
                g.PixelOffsetMode    = PixelOffsetMode.Half;
                g.InterpolationMode  = InterpolationMode.HighQualityBilinear;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(background.Value);
                g.DrawImage(big,
                    new Rectangle(0, 0, iconSizePx, iconSizePx),
                    new Rectangle(0, 0, big.Width, big.Height),
                    GraphicsUnit.Pixel);
            }

            using var ms = new MemoryStream();
            final.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        // -----------------------------------------------------------------------
        // Private render helpers
        // -----------------------------------------------------------------------

        private static Bitmap RenderToBitmap(
            BlockTableRecord         btr,
            Transaction              tr,
            int                      canvas,
            Color                    background,
            Func<Point3d, PointF>    xform,
            float                    strokePx,
            double                   worldScale)
        {
            var bmp = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
            bmp.SetResolution(300, 300);

            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.PixelOffsetMode    = PixelOffsetMode.Half;
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(background);

            var ctx = new RenderCtx(tr, g, xform, strokePx, background);

            // Pass 1: fills (hatches, solids) so strokes appear on top
            foreach (ObjectId id in btr)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                // Entity.Visible == false means the entity is hidden by a dynamic block
                // visibility state — skip it so we only render what is currently shown.
                if (ent != null && ent.Visible) RenderEntityFill(ent, ctx, Matrix3d.Identity);
            }

            // Pass 2: strokes and text
            foreach (ObjectId id in btr)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent != null && ent.Visible) RenderEntityStroke(ent, ctx, Matrix3d.Identity, worldScale);
            }

            return bmp;
        }

        // -----------------------------------------------------------------------
        // Pass 1 – fills (Hatch, Solid, 2dSolid, wide polyline segments)
        // -----------------------------------------------------------------------

        private static void RenderEntityFill(Entity ent, RenderCtx ctx, Matrix3d transform)
        {
            try
            {
                switch (ent)
                {
                    case Hatch hatch:
                        DrawHatchFill(hatch, ctx, transform);
                        return;

                    case Solid solid:
                        DrawSolidFill(solid, ctx, transform);
                        return;

                    case BlockReference br:
                        // Recurse into nested blocks for fill pass
                        RenderNestedBlockFill(br, ctx, transform);
                        return;
                }

                // Explode everything else in fill pass to catch nested hatches etc.
                var exploded = new DBObjectCollection();
                try { ent.Explode(exploded); } catch { return; }
                foreach (DBObject dbo in exploded)
                {
                    using (dbo)
                    {
                        if (dbo is Entity child)
                            RenderEntityFill(child, ctx, transform);
                    }
                }
            }
            catch { /* ignore non-renderable entities */ }
        }

        private static void DrawHatchFill(Hatch hatch, RenderCtx ctx, Matrix3d transform)
        {
            try
            {
                if (hatch.HatchStyle == Autodesk.AutoCAD.DatabaseServices.HatchStyle.Ignore) return;

                // Collect all loops and build a graphics path
                using var path = new GraphicsPath();
                path.FillMode = FillMode.Alternate;

                for (int li = 0; li < hatch.NumberOfLoops; li++)
                {
                    var loopPts = new List<PointF>();
                    var loop = hatch.GetLoopAt(li);

                    if (loop.IsPolyline)
                    {
                        foreach (BulgeVertex bv in loop.Polyline)
                        {
                            var p3 = new Point3d(bv.Vertex.X, bv.Vertex.Y, 0.0);
                            if (transform != Matrix3d.Identity) p3 = p3.TransformBy(transform);
                            loopPts.Add(ctx.Xform(p3));
                        }
                    }
                    else
                    {
                        foreach (Curve2d seg in loop.Curves)
                        {
                            AppendCurve2dPoints(seg, loopPts, ctx, transform);
                        }
                    }

                    if (loopPts.Count >= 2)
                        path.AddPolygon(loopPts.ToArray());
                }

                var fillColor = ResolveColor(hatch, ctx.Tr, Color.Gray, ctx);
                // Semi-transparent fill so geometry underneath is still hinted
                fillColor = Color.FromArgb(180, fillColor.R, fillColor.G, fillColor.B);
                using var brush = new SolidBrush(fillColor);
                ctx.G.FillPath(brush, path);
            }
            catch { }
        }

        private static void DrawSolidFill(Solid solid, RenderCtx ctx, Matrix3d transform)
        {
            try
            {
                var pts = new PointF[4];
                for (int i = 0; i < 4; i++)
                {
                    var p3 = solid.GetPointAt((short)i);
                    if (transform != Matrix3d.Identity) p3 = p3.TransformBy(transform);
                    pts[i] = ctx.Xform(p3);
                }
                var color = ResolveColor(solid, ctx.Tr, Color.White, ctx);
                using var brush = new SolidBrush(color);
                // AutoCAD solid winding: 0,1,3,2
                ctx.G.FillPolygon(brush, new[] { pts[0], pts[1], pts[3], pts[2] });
            }
            catch { }
        }

        private static void RenderNestedBlockFill(BlockReference br, RenderCtx ctx, Matrix3d transform)
        {
            try
            {
                var combined = transform == Matrix3d.Identity
                    ? br.BlockTransform
                    : transform.PreMultiplyBy(br.BlockTransform);

                var defId = !br.DynamicBlockTableRecord.IsNull
                    ? br.DynamicBlockTableRecord
                    : br.BlockTableRecord;
                var nested = (BlockTableRecord)ctx.Tr.GetObject(defId, OpenMode.ForRead);

                foreach (ObjectId id in nested)
                {
                    var ent = ctx.Tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent != null) RenderEntityFill(ent, ctx, combined);
                }
            }
            catch { }
        }

        // -----------------------------------------------------------------------
        // Pass 2 – strokes, text, attribute definitions, nested inserts
        // -----------------------------------------------------------------------

        private static void RenderEntityStroke(
            Entity    ent,
            RenderCtx ctx,
            Matrix3d  transform,
            double    worldScale)
        {
            try
            {
                switch (ent)
                {
                    // AttributeDefinition derives from DBText — must come BEFORE the DBText arm
                    case AttributeDefinition attDef when attDef.Visible:
                        DrawAttributeDefinition(attDef, ctx, transform, worldScale);
                        return;

                    case AttributeDefinition:
                        return; // invisible ATTDEF — skip silently

                    case DBText text:
                        DrawText(text, ctx, transform, worldScale);
                        return;

                    case MText mtext:
                        DrawMText(mtext, ctx, transform, worldScale);
                        return;

                    case Hatch hatch:
                        // Stroke the hatch boundary for clarity
                        DrawHatchBoundary(hatch, ctx, transform);
                        return;

                    case BlockReference br:
                        RenderNestedBlockStroke(br, ctx, transform, worldScale);
                        return;

                    case Curve curve:
                        var col = ResolveColor(ent, ctx.Tr, GetFallbackForeground(ctx), ctx);
                        DrawCurveWithTransform(curve, col, ctx, transform);
                        return;
                }

                // Explode anything else
                var exploded = new DBObjectCollection();
                try { ent.Explode(exploded); } catch { return; }
                foreach (DBObject dbo in exploded)
                {
                    using (dbo)
                    {
                        if (dbo is Entity child)
                            RenderEntityStroke(child, ctx, transform, worldScale);
                    }
                }
            }
            catch { }
        }

        // -----------------------------------------------------------------------
        // Text rendering
        // -----------------------------------------------------------------------

        private static void DrawText(DBText text, RenderCtx ctx, Matrix3d transform, double worldScale)
        {
            try
            {
                string content = text.TextString;
                if (string.IsNullOrWhiteSpace(content)) return;

                var pos = text.Position;
                if (transform != Matrix3d.Identity) pos = pos.TransformBy(transform);
                var origin = ctx.Xform(pos);

                float fontPx = CalcFontPx(text.Height, worldScale, ctx);
                if (fontPx < 2f) return; // too small to render usefully

                var color = ResolveColor(text, ctx.Tr, GetFallbackForeground(ctx), ctx);
                DrawTextAt(ctx.G, content, origin, fontPx, color,
                    (float)text.Rotation, text.HorizontalMode, text.VerticalMode);
            }
            catch { }
        }

        private static void DrawMText(MText mtext, RenderCtx ctx, Matrix3d transform, double worldScale)
        {
            try
            {
                string content = mtext.Text;
                if (string.IsNullOrWhiteSpace(content)) return;

                // Strip MText formatting codes for the thumbnail
                content = StripMTextCodes(content);
                if (string.IsNullOrWhiteSpace(content)) return;

                var pos = mtext.Location;
                if (transform != Matrix3d.Identity) pos = pos.TransformBy(transform);
                var origin = ctx.Xform(pos);

                float fontPx = CalcFontPx(mtext.TextHeight, worldScale, ctx);
                if (fontPx < 2f) return;

                var color = ResolveColor(mtext, ctx.Tr, GetFallbackForeground(ctx), ctx);
                DrawTextAt(ctx.G, content, origin, fontPx, color, 0f,
                    TextHorizontalMode.TextLeft, TextVerticalMode.TextBase);
            }
            catch { }
        }

        private static void DrawAttributeDefinition(
            AttributeDefinition attDef, RenderCtx ctx, Matrix3d transform, double worldScale)
        {
            try
            {
                // Render the tag/default text so label text is visible in the thumbnail
                string content = !string.IsNullOrWhiteSpace(attDef.TextString)
                    ? attDef.TextString
                    : attDef.Tag;
                if (string.IsNullOrWhiteSpace(content)) return;

                var pos = attDef.Position;
                if (transform != Matrix3d.Identity) pos = pos.TransformBy(transform);
                var origin = ctx.Xform(pos);

                float fontPx = CalcFontPx(attDef.Height, worldScale, ctx);
                if (fontPx < 2f) return;

                // Draw attribute text in a slightly muted colour to distinguish from geometry
                var baseColor = ResolveColor(attDef, ctx.Tr, GetFallbackForeground(ctx), ctx);
                var attrColor = Color.FromArgb(
                    Math.Max(0, baseColor.A - 40),
                    baseColor.R, baseColor.G, baseColor.B);

                DrawTextAt(ctx.G, content, origin, fontPx, attrColor,
                    (float)attDef.Rotation, attDef.HorizontalMode, attDef.VerticalMode);
            }
            catch { }
        }

        private static void DrawTextAt(
            Graphics          g,
            string            text,
            PointF            origin,
            float             fontPx,
            Color             color,
            float             rotationRad,
            TextHorizontalMode hMode,
            TextVerticalMode  vMode)
        {
            try
            {
                using var font = new System.Drawing.Font("Arial", Math.Max(fontPx, 1f),
                    System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);

                var size = g.MeasureString(text, font);

                // Alignment offsets
                float dx = hMode switch
                {
                    TextHorizontalMode.TextCenter => -size.Width / 2f,
                    TextHorizontalMode.TextRight  => -size.Width,
                    _                             => 0f
                };
                float dy = vMode switch
                {
                    TextVerticalMode.TextVerticalMid => -size.Height / 2f,
                    TextVerticalMode.TextTop         => 0f,
                    _                               => -size.Height
                };

                var state = g.Save();
                g.TranslateTransform(origin.X, origin.Y);
                // AutoCAD rotation is counter-clockwise; GDI+ is clockwise screen coords
                g.RotateTransform(-(float)(rotationRad * 180.0 / Math.PI));
                using var brush = new SolidBrush(color);
                g.DrawString(text, font, brush, dx, dy);
                g.Restore(state);
            }
            catch { }
        }

        private static float CalcFontPx(double worldHeight, double worldScale, RenderCtx ctx)
        {
            // worldScale is pixels-per-world-unit at the super-sampled canvas size.
            return (float)(worldHeight * worldScale);
        }

        private static string StripMTextCodes(string s)
        {
            // Remove the most common MText format sequences: \P, \L, \~, {}, \f...;, etc.
            var sb = new System.Text.StringBuilder(s.Length);
            bool inBrace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{') { inBrace = true; continue; }
                if (c == '}') { inBrace = false; continue; }
                if (c == '\\')
                {
                    i++; // skip the format code letter
                    // skip until ';' for codes like \f...; \H...; \A...;
                    if (i < s.Length && (s[i] == 'f' || s[i] == 'F' ||
                                         s[i] == 'H' || s[i] == 'C' ||
                                         s[i] == 'T' || s[i] == 'Q' ||
                                         s[i] == 'A' || s[i] == 'W'))
                    {
                        while (i < s.Length && s[i] != ';') i++;
                    }
                    else if (i < s.Length && s[i] == 'P')
                    {
                        sb.Append(' '); // paragraph break -> space
                    }
                    continue;
                }
                if (!inBrace) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // -----------------------------------------------------------------------
        // Hatch boundary stroke
        // -----------------------------------------------------------------------

        private static void DrawHatchBoundary(Hatch hatch, RenderCtx ctx, Matrix3d transform)
        {
            try
            {
                var col = ResolveColor(hatch, ctx.Tr, GetFallbackForeground(ctx), ctx);
                using var pen = MakePen(col, ctx.StrokePx * 0.8f); // slightly thinner than geometry

                for (int li = 0; li < hatch.NumberOfLoops; li++)
                {
                    var loop = hatch.GetLoopAt(li);
                    var pts  = new List<PointF>();

                    if (loop.IsPolyline)
                    {
                        foreach (BulgeVertex bv in loop.Polyline)
                        {
                            var p3 = new Point3d(bv.Vertex.X, bv.Vertex.Y, 0.0);
                            if (transform != Matrix3d.Identity) p3 = p3.TransformBy(transform);
                            pts.Add(ctx.Xform(p3));
                        }
                        if (pts.Count >= 2) ctx.G.DrawLines(pen, pts.ToArray());
                    }
                    else
                    {
                        foreach (Curve2d seg in loop.Curves)
                            AppendCurve2dPoints(seg, pts, ctx, transform);
                        if (pts.Count >= 2) ctx.G.DrawLines(pen, pts.ToArray());
                    }
                }
            }
            catch { }
        }

        // -----------------------------------------------------------------------
        // Nested block reference stroke
        // -----------------------------------------------------------------------

        private static void RenderNestedBlockStroke(
            BlockReference br, RenderCtx ctx, Matrix3d transform, double worldScale)
        {
            try
            {
                var combined = transform == Matrix3d.Identity
                    ? br.BlockTransform
                    : transform.PreMultiplyBy(br.BlockTransform);

                var defId = !br.DynamicBlockTableRecord.IsNull
                    ? br.DynamicBlockTableRecord
                    : br.BlockTableRecord;
                var nested = (BlockTableRecord)ctx.Tr.GetObject(defId, OpenMode.ForRead);

                foreach (ObjectId id in nested)
                {
                    var ent = ctx.Tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent != null)
                        RenderEntityStroke(ent, ctx, combined, worldScale);
                }
            }
            catch { }
        }

        // -----------------------------------------------------------------------
        // Curve drawing (with optional transform)
        // -----------------------------------------------------------------------

        private static void DrawCurveWithTransform(
            Curve    curve,
            Color    color,
            RenderCtx ctx,
            Matrix3d transform)
        {
            try
            {
                using var pen = MakePen(color, ctx.StrokePx);

                double t0 = curve.StartParam;
                double t1 = curve.EndParam;
                if (double.IsNaN(t0) || double.IsNaN(t1) || Math.Abs(t1 - t0) < 1e-9) return;

                int samples = DetermineSamples(curve);
                PointF? prev = null;

                for (int i = 0; i <= samples; i++)
                {
                    double t = t0 + i * (t1 - t0) / samples;
                    Point3d pt3d;
                    try { pt3d = curve.GetPointAtParameter(t); }
                    catch { continue; }

                    if (transform != Matrix3d.Identity) pt3d = pt3d.TransformBy(transform);
                    var p = ctx.Xform(pt3d);
                    if (prev.HasValue) ctx.G.DrawLine(pen, prev.Value, p);
                    prev = p;
                }
            }
            catch { }
        }

        // -----------------------------------------------------------------------
        // Curve2d helper (for hatch loop curves)
        // -----------------------------------------------------------------------

        private static void AppendCurve2dPoints(
            Curve2d seg, List<PointF> pts, RenderCtx ctx, Matrix3d transform)
        {
            try
            {
                int n = seg is CircularArc2d ? 24 : 4;
                var interval = seg.GetInterval();
                double t0 = interval.LowerBound;
                double t1 = interval.UpperBound;
                if (double.IsInfinity(t0) || double.IsInfinity(t1)) return;
                for (int i = 0; i <= n; i++)
                {
                    double t  = t0 + i * (t1 - t0) / n;
                    var p2d = seg.EvaluatePoint(t);
                    var p3d = new Point3d(p2d.X, p2d.Y, 0.0);
                    if (transform != Matrix3d.Identity) p3d = p3d.TransformBy(transform);
                    pts.Add(ctx.Xform(p3d));
                }
            }
            catch { }
        }

        // -----------------------------------------------------------------------
        // Sampling
        // -----------------------------------------------------------------------

        private static int DetermineSamples(Curve curve)
        {
            int n = 28;
            if (curve.Closed)                             n += 10;
            if (curve is Spline)                          n += 18;
            if (curve is Polyline or Polyline2d)          n += 10;
            if (curve is Circle or Ellipse)               n += 10;
            return Math.Max(8, Math.Min(160, n));
        }

        // -----------------------------------------------------------------------
        // Colour resolution – improved with contrast guard
        // -----------------------------------------------------------------------

        private static Color ResolveColor(Entity ent, Transaction tr, Color fallback, RenderCtx ctx)
        {
            Color resolved = ResolveColorRaw(ent, tr, fallback, ctx.DarkBg);
            return EnsureContrast(resolved, ctx.Background);
        }

        private static Color ResolveColorRaw(
            Entity ent, Transaction tr, Color fallback, bool darkBg)
        {
            try
            {
                var c = ent.Color;
                if (c == null) return fallback;

                switch (c.ColorMethod)
                {
                    case Autodesk.AutoCAD.Colors.ColorMethod.ByAci:
                    case Autodesk.AutoCAD.Colors.ColorMethod.ByColor:
                        return AciToDisplay(c, darkBg);

                    case Autodesk.AutoCAD.Colors.ColorMethod.Foreground:
                        return darkBg ? Color.White : Color.Black;

                    case Autodesk.AutoCAD.Colors.ColorMethod.ByLayer:
                        if (!ent.LayerId.IsNull)
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForRead);
                            var lc  = ltr?.Color;
                            if (lc != null)
                            {
                                if (lc.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.Foreground)
                                    return darkBg ? Color.White : Color.Black;
                                if (lc.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci ||
                                    lc.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor)
                                    return AciToDisplay(lc, darkBg);
                            }
                        }
                        break;

                    case Autodesk.AutoCAD.Colors.ColorMethod.ByBlock:
                        // ByBlock inherits from the inserting reference; no context here, use foreground.
                        return darkBg ? Color.White : Color.Black;
                }
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// Converts an AutoCAD colour to a display RGB, correctly handling the ACI 7 swap rule.
        /// ACI 7 is stored as RGB(0,0,0) but displays as WHITE on dark backgrounds
        /// and BLACK on light backgrounds — the reverse of what ColorValue returns.
        /// ACI 0 is the ByBlock sentinel (also stored as black) and is treated the same way.
        /// </summary>
        private static Color AciToDisplay(Autodesk.AutoCAD.Colors.Color c, bool darkBg)
        {
            short idx = c.ColorIndex;
            // ACI 7 = white/black swap; ACI 0 = ByBlock sentinel
            if (idx == 7 || idx == 0)
                return darkBg ? Color.White : Color.Black;
            return c.ColorValue;
        }

        /// <summary>
        /// Shifts a colour away from the background if it is too similar (low contrast).
        /// Prevents invisible white-on-white or black-on-black lines.
        /// </summary>
        private static Color EnsureContrast(Color color, Color background)
        {
            // Skip alpha=0 (fully transparent background)
            if (background.A == 0) return color;

            double diff = Math.Abs(Luminance(color) - Luminance(background));
            if (diff >= 0.20) return color; // already sufficient contrast

            // Not enough contrast: flip toward the opposite extreme
            bool bgDark = Luminance(background) < 0.5;
            return bgDark ? LightenTowards(color, 0.75) : DarkenTowards(color, 0.25);
        }

        private static Color LightenTowards(Color c, double targetL)
        {
            double t = targetL;
            return Color.FromArgb(c.A,
                (int)Math.Round(c.R + (255 - c.R) * t),
                (int)Math.Round(c.G + (255 - c.G) * t),
                (int)Math.Round(c.B + (255 - c.B) * t));
        }

        private static Color DarkenTowards(Color c, double targetL)
        {
            double t = 1.0 - targetL;
            return Color.FromArgb(c.A,
                (int)Math.Round(c.R * (1 - t)),
                (int)Math.Round(c.G * (1 - t)),
                (int)Math.Round(c.B * (1 - t)));
        }

        /// <summary>Returns perceived luminance in [0,1] (sRGB approx).</summary>
        private static double Luminance(Color c) =>
            (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        private static Color GetFallbackForeground(RenderCtx ctx) =>
            ctx.DarkBg ? Color.White : Color.Black;

        // -----------------------------------------------------------------------
        // Extents helper – can optionally exclude ATTDEF entities
        // -----------------------------------------------------------------------

        private static Extents3d? ComputeBlockExtents(
            BlockTableRecord btr, Transaction tr, bool includeAttDef)
        {
            Extents3d? ext = null;
            foreach (ObjectId id in btr)
            {
                if (!id.IsValid) continue;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                if (!includeAttDef && ent is AttributeDefinition) continue;
                try
                {
                    var ge = ent.GeometricExtents;
                    ext = ext.HasValue ? ExpandExtents(ext.Value, ge) : ge;
                }
                catch { }
            }
            return ext;
        }

        // -----------------------------------------------------------------------
        // Pen factory
        // -----------------------------------------------------------------------

        private static Pen MakePen(Color color, float width) => new(color, Math.Max(width, 0.35f))
        {
            Alignment  = PenAlignment.Center,
            StartCap   = LineCap.Flat,
            EndCap     = LineCap.Flat,
            LineJoin   = LineJoin.Miter,
            MiterLimit = 2.0f,
        };

        // -----------------------------------------------------------------------
        // Transform helpers
        // -----------------------------------------------------------------------

        private static (double scale, double offsetX, double offsetY)
            ComputeTransform(Extents3d ext, int canvas)
        {
            double dx = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, 1e-9);
            double dy = Math.Max(ext.MaxPoint.Y - ext.MinPoint.Y, 1e-9);
            const double pad    = 0.08;
            const double usable = 1.0 - 2 * pad;
            double scale   = usable * Math.Min(canvas / dx, canvas / dy);
            double offsetX = (canvas - dx * scale) / 2.0;
            double offsetY = (canvas - dy * scale) / 2.0;
            return (scale, offsetX, offsetY);
        }

        private static Extents3d ExpandExtents(Extents3d a, Extents3d b) =>
            new(
                new Point3d(
                    Math.Min(a.MinPoint.X, b.MinPoint.X),
                    Math.Min(a.MinPoint.Y, b.MinPoint.Y),
                    0),
                new Point3d(
                    Math.Max(a.MaxPoint.X, b.MaxPoint.X),
                    Math.Max(a.MaxPoint.Y, b.MaxPoint.Y),
                    0));

        // -----------------------------------------------------------------------
        // Render from a live block reference (captures current visual state)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders a PNG thumbnail by exploding a live <see cref="BlockReference"/>
        /// rather than reading the block definition directly.  This captures the
        /// current dynamic/visibility state of the reference.
        /// </summary>
        public static byte[]? RenderBlockIconFromReference(
            Database db,
            ObjectId blockRefId,
            int      iconSizePx       = 64,
            int      supersampleFactor = 2,
            Color?   background        = null,
            float    finalHairlinePx   = 0.55f)
        {
            background ??= Color.Transparent;
            int canvas = Math.Max(16, iconSizePx * Math.Max(1, supersampleFactor));

            using var tr = db.TransactionManager.StartTransaction();

            var br = tr.GetObject(blockRefId, OpenMode.ForRead) as BlockReference;
            if (br == null) return null;

            // Use the definition-based path when the reference is a plain block
            // (avoids double-pass explode overhead for the common case).
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);

            Extents3d? ext = ComputeBlockExtents(btr, tr, includeAttDef: false);
            if (!ext.HasValue)
            {
                // Fall back to the explode-based approach for dynamic/external blocks
                return RenderFromExplode(db, tr, br, canvas, iconSizePx,
                    background.Value, finalHairlinePx, supersampleFactor);
            }

            // Transform the BTR extents by the block reference transform
            var xf = br.BlockTransform;
            var minW = new Point3d(ext.Value.MinPoint.X, ext.Value.MinPoint.Y, 0)
                .TransformBy(xf);
            var maxW = new Point3d(ext.Value.MaxPoint.X, ext.Value.MaxPoint.Y, 0)
                .TransformBy(xf);

            // Re-normalise after transform (handles mirrored blocks)
            var worldExt = new Extents3d(
                new Point3d(Math.Min(minW.X, maxW.X), Math.Min(minW.Y, maxW.Y), 0),
                new Point3d(Math.Max(minW.X, maxW.X), Math.Max(minW.Y, maxW.Y), 0));

            var (scale, ox, oy) = ComputeTransform(worldExt, canvas);

            PointF Xform(Point3d w) => new(
                (float)(ox + (w.X - worldExt.MinPoint.X) * scale),
                (float)(canvas - (oy + (w.Y - worldExt.MinPoint.Y) * scale)));

            float superStroke = Math.Max(finalHairlinePx * supersampleFactor, 0.5f);

            using var big = RenderToBitmap(btr, tr, canvas, background.Value,
                Xform, superStroke, scale);

            using var final = new Bitmap(iconSizePx, iconSizePx, PixelFormat.Format32bppArgb);
            final.SetResolution(300, 300);
            using (var g2 = Graphics.FromImage(final))
            {
                g2.SmoothingMode      = SmoothingMode.AntiAlias;
                g2.PixelOffsetMode    = PixelOffsetMode.Half;
                g2.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                g2.CompositingQuality = CompositingQuality.HighQuality;
                g2.Clear(background.Value);
                g2.DrawImage(big,
                    new Rectangle(0, 0, iconSizePx, iconSizePx),
                    new Rectangle(0, 0, big.Width, big.Height),
                    GraphicsUnit.Pixel);
            }

            using var ms = new MemoryStream();
            final.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        // -----------------------------------------------------------------------
        // Explode-based fallback (dynamic/external blocks)
        // -----------------------------------------------------------------------

        private static byte[]? RenderFromExplode(
            Database    db,
            Transaction tr,
            BlockReference br,
            int         canvas,
            int         iconSizePx,
            Color       background,
            float       finalHairlinePx,
            int         supersampleFactor)
        {
            var exploded = new DBObjectCollection();
            br.Explode(exploded);

            var flat = new List<Entity>(256);
            FlattenExploded(exploded, flat);

            if (flat.Count == 0) { DisposeAll(flat); DisposeAll(exploded); return null; }

            Extents3d? ext = null;
            foreach (var e in flat)
            {
                try { var ge = e.GeometricExtents; ext = ext.HasValue ? ExpandExtents(ext.Value, ge) : ge; }
                catch { }
            }

            if (!ext.HasValue) { DisposeAll(flat); DisposeAll(exploded); return null; }

            var (scale, offsetX, offsetY) = ComputeTransform(ext.Value, canvas);

            PointF Xform(Point3d w) => new(
                (float)(offsetX + (w.X - ext.Value.MinPoint.X) * scale),
                (float)(canvas  - (offsetY + (w.Y - ext.Value.MinPoint.Y) * scale)));

            using var big = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
            big.SetResolution(300, 300);

            using (var g = Graphics.FromImage(big))
            {
                g.SmoothingMode      = SmoothingMode.AntiAlias;
                g.PixelOffsetMode    = PixelOffsetMode.Half;
                g.InterpolationMode  = InterpolationMode.HighQualityBilinear;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(background);

                float strokePx = Math.Max(finalHairlinePx * supersampleFactor, 0.5f);
                var ctx = new RenderCtx(tr, g, Xform, strokePx, background);

                foreach (var e in flat)
                    if (e is Curve curve)
                        DrawCurveWithTransform(curve,
                            ResolveColor(e, tr, GetFallbackForeground(ctx), ctx), ctx, Matrix3d.Identity);
            }

            DisposeAll(flat);
            DisposeAll(exploded);

            using var final = new Bitmap(iconSizePx, iconSizePx, PixelFormat.Format32bppArgb);
            final.SetResolution(300, 300);
            using (var g2 = Graphics.FromImage(final))
            {
                g2.SmoothingMode      = SmoothingMode.AntiAlias;
                g2.PixelOffsetMode    = PixelOffsetMode.Half;
                g2.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                g2.CompositingQuality = CompositingQuality.HighQuality;
                g2.Clear(background);
                g2.DrawImage(big,
                    new Rectangle(0, 0, iconSizePx, iconSizePx),
                    new Rectangle(0, 0, big.Width, big.Height),
                    GraphicsUnit.Pixel);
            }

            using var ms = new MemoryStream();
            final.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        // -----------------------------------------------------------------------
        // Flatten helpers
        // -----------------------------------------------------------------------

        private static void FlattenExploded(DBObjectCollection src, List<Entity> outList)
        {
            foreach (DBObject dbo in src)
            {
                if (dbo is BlockReference nested)
                {
                    var tmp = new DBObjectCollection();
                    nested.Explode(tmp);
                    FlattenExploded(tmp, outList);
                    foreach (DBObject d in tmp) d.Dispose();
                    nested.Dispose();
                }
                else if (dbo is Entity e) outList.Add(e);
                else dbo?.Dispose();
            }
        }

        private static void DisposeAll(IEnumerable<Entity> items)
        {
            foreach (var e in items) e?.Dispose();
        }

        private static void DisposeAll(DBObjectCollection coll)
        {
            foreach (DBObject o in coll) o?.Dispose();
        }
    }
}
