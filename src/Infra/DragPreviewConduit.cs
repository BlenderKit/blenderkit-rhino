using System;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Drag-drop visual feedback. Two stacked boxes at <see cref="Target"/>:
    ///   * Outer = full asset bounding-box, dark transparent green —
    ///     the user's "where will this land" gauge.
    ///   * Inner = same XY footprint, scales 0→1 along Z by
    ///     <see cref="Progress"/>, brighter transparent green — a download
    ///     progress fill.
    ///
    /// Pivot is at the bottom-center of the outer box (matches how Rhino
    /// imports glTF — origin at the asset's local origin).
    /// </summary>
    public class DragPreviewConduit : DisplayConduit
    {
        public Point3d Target;
        // Surface normal at Target. The cube's local +Z aligns with this so
        // it sits flush on whatever surface (or cplane) was hit.
        public Vector3d Normal = Vector3d.ZAxis;
        // User-controlled rotation around the surface normal, in radians.
        // Adjusted via mousewheel during a drag (DragSession.OnMouseWheel).
        public double SpinRadians;
        public double Progress;
        public string Label = "";
        // Name of the object the cursor is currently hovering over (set
        // by DragSession on each tick). When non-null, the preview
        // overlays "↳ drop on <name>" so the user knows exactly what
        // the material/asset will land on. Especially useful for
        // material drops where the visual cue is otherwise subtle.
        public string HoverTargetName;
        // Bounding box of the asset, used to size the cube. Defaults to a
        // half-meter cube; overridden by the panel from the search hit's
        // dimensions when available.
        public Vector3d Size = new Vector3d(50, 50, 50);
        // Visual style. MODEL → bbox cube as before. MATERIAL → small
        // disc that hugs the hit surface (the "I'll paint this object"
        // hint matches the addon's drop UX). HDR → no in-viewport
        // preview (we just bind the env on drop) — keep the label only.
        public enum DragStyle { ModelBox, MaterialDisc, HdrNothing }
        public DragStyle Style = DragStyle.ModelBox;

        private static readonly Color OuterColor  = Color.FromArgb( 60, 0, 120, 0);
        private static readonly Color OuterEdge   = Color.FromArgb(220, 0, 180, 0);
        private static readonly Color InnerColor  = Color.FromArgb(160, 60, 220, 60);
        private static readonly Color InnerEdge   = Color.FromArgb(220, 60, 220, 60);

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            var b = new BoundingBox(
                Target - new Vector3d(Size.X, Size.Y, 0),
                Target + new Vector3d(Size.X, Size.Y, Size.Z));
            e.IncludeBoundingBox(b);
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            var n = Normal.IsZero ? Vector3d.ZAxis : Normal;
            var basePlane = new Plane(Target, n);
            if (Math.Abs(SpinRadians) > 1e-6)
                basePlane.Rotate(SpinRadians, n, Target);

            switch (Style)
            {
                case DragStyle.MaterialDisc:
                    DrawMaterialDisc(e, basePlane);
                    break;
                case DragStyle.HdrNothing:
                    // No 3D preview — HDR sets the env on drop, there's
                    // nothing to "place". Label still gets drawn below.
                    break;
                default:
                    DrawModelBox(e, basePlane);
                    break;
            }

            if (!string.IsNullOrEmpty(Label))
            {
                // Anchor the label above the surface for material/HDR
                // (no z-extruded box to clear); for the model box, rest
                // it on top of the cube.
                var anchorOffset = Style == DragStyle.ModelBox
                    ? new Vector3d(0, 0, Math.Max(Size.Z, 1.0) + 5)
                    : new Vector3d(0, 0, Math.Max(Size.Z, 50) * 0.4);
                var screen = e.Viewport.WorldToClient(Target + anchorOffset);
                e.Display.Draw2dText(Label, Color.White,
                    new Point2d(screen.X, screen.Y - 14), middleJustified: true, 14);
                // Second line: hover-target hint, if any. Shown a row
                // below the main label so it doesn't crowd the label
                // text. Empty hover (drop on nothing → cplane) shows a
                // subtler message.
                var hint = !string.IsNullOrEmpty(HoverTargetName)
                    ? $"↳ drop on \"{HoverTargetName}\""
                    : (Style == DragStyle.MaterialDisc
                        ? "↳ hover over an object to assign"
                        : null);
                if (!string.IsNullOrEmpty(hint))
                {
                    e.Display.Draw2dText(hint, Color.LightGreen,
                        new Point2d(screen.X, screen.Y + 4), middleJustified: true, 11);
                }
            }
        }

        private void DrawModelBox(DrawEventArgs e, Plane basePlane)
        {
            var sx = Math.Max(Size.X / 2.0, 1.0);
            var sy = Math.Max(Size.Y / 2.0, 1.0);
            var sz = Math.Max(Size.Z, 1.0);

            var outerBox = new Box(
                basePlane,
                new Interval(-sx, sx),
                new Interval(-sy, sy),
                new Interval(0, sz));
            var outerBrep = outerBox.ToBrep();
            if (outerBrep != null)
                e.Display.DrawBrepShaded(outerBrep, new DisplayMaterial(OuterColor, 0.7));
            e.Display.DrawBox(outerBox, OuterEdge, 2);

            // Inner box: same XY footprint, Z scales by progress.
            if (Progress > 0.001)
            {
                var innerBox = new Box(
                    basePlane,
                    new Interval(-sx, sx),
                    new Interval(-sy, sy),
                    new Interval(0, sz * Math.Min(Progress, 1.0)));
                var innerBrep = innerBox.ToBrep();
                if (innerBrep != null)
                    e.Display.DrawBrepShaded(innerBrep, new DisplayMaterial(InnerColor, 0.4));
                e.Display.DrawBox(innerBox, InnerEdge, 1);
            }
        }

        private void DrawMaterialDisc(DrawEventArgs e, Plane basePlane)
        {
            // A flat disc that hugs the hit surface — visual hint that
            // dropping here will paint the object the user is hovering
            // over, not place new geometry. Radius scales with the
            // largest XY extent of the asset bbox so it reads clearly
            // on both small swatches and large props.
            var radius = Math.Max(Math.Max(Size.X, Size.Y) * 0.6, 5.0);
            // Use a pulsing thickness for the outer ring — actually a
            // pair of concentric circles is enough to read well.
            var inner = new Circle(basePlane, radius * 0.7);
            var outer = new Circle(basePlane, radius);
            // Filled disc as a planar surface.
            try
            {
                var disc = global::Rhino.Geometry.NurbsSurface.CreateFromCorners(
                    basePlane.PointAt(-radius, -radius),
                    basePlane.PointAt( radius, -radius),
                    basePlane.PointAt( radius,  radius),
                    basePlane.PointAt(-radius,  radius));
                if (disc != null)
                    e.Display.DrawSurface(disc, OuterColor, 1);
            }
            catch { }
            e.Display.DrawCircle(outer, OuterEdge, 3);
            e.Display.DrawCircle(inner, InnerEdge, 2);
            // Progress arc — sweep a partial circle as download progresses.
            if (Progress > 0.001)
            {
                int segs = 48;
                double sweep = 2 * Math.PI * Math.Min(Progress, 1.0);
                Point3d? prev = null;
                for (int i = 0; i <= segs; i++)
                {
                    double a = sweep * i / segs;
                    var pt = basePlane.PointAt(Math.Cos(a) * radius, Math.Sin(a) * radius);
                    if (prev.HasValue) e.Display.DrawLine(prev.Value, pt, InnerEdge, 4);
                    prev = pt;
                }
            }
        }
    }
}
