using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.UI;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Captures the next viewport mouse-up via Rhino's MouseCallback and runs
    /// a ray-cast at that point. Used by the panel's drag flow so an asset
    /// can drop on the surface under the cursor without going through any
    /// "Import file" dialog or interactive GetPoint prompt.
    /// </summary>
    public class DropTargetService : MouseCallback
    {
        private static DropTargetService _active;
        private readonly Action<RhinoView, Point3d> _onDrop;
        private readonly Action _onCancel;
        // Optional: live preview drawn at the cursor's projected world point.
        // The service updates the conduit's Target on every viewport
        // MouseMove and triggers a redraw of that view.
        public DragPreviewConduit Preview;

        public DropTargetService(Action<RhinoView, Point3d> onDrop, Action onCancel = null)
        {
            _onDrop = onDrop;
            _onCancel = onCancel;
        }

        /// <summary>
        /// Arm the callback. Cancels any other DropTargetService still armed
        /// (we only support one drag at a time).
        /// </summary>
        public void Arm()
        {
            if (_active != null)
            {
                _active.Enabled = false;
                _active._onCancel?.Invoke();
            }
            _active = this;
            Enabled = true;
        }

        protected override void OnMouseMove(MouseCallbackEventArgs e)
        {
            if (_active != this || Preview == null) { base.OnMouseMove(e); return; }
            try
            {
                Preview.Target = RaycastDropPoint(e.View, e.ViewportPoint);
                e.View.Redraw();
            }
            catch { /* never throw from a mouse event */ }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseCallbackEventArgs e)
        {
            // Only consume the very first mouseup, then disarm.
            if (_active != this) return;
            _active = null;
            Enabled = false;
            if (Preview != null)
            {
                Preview.Enabled = false;
                e.View.Redraw();
            }

            try
            {
                var pt = RaycastDropPoint(e.View, e.ViewportPoint);
                _onDrop?.Invoke(e.View, pt);
            }
            catch (Exception ex)
            {
                BkLog.W("DropTargetService raycast failed: " + ex.Message);
            }
            base.OnMouseUp(e);
        }

        /// <summary>
        /// Find the world-space point under <paramref name="viewportPt"/> by
        /// (1) ray-casting the active viewport line against every render mesh
        /// in the doc, then (2) falling back to the construction plane.
        /// </summary>
        private static Point3d RaycastDropPoint(RhinoView view, System.Drawing.Point viewportPt)
        {
            var vp = view.ActiveViewport;
            if (!vp.GetFrustumLine(viewportPt.X, viewportPt.Y, out Line ray))
                return Point3d.Origin;

            // The ray returned by GetFrustumLine goes near→far. We want the
            // first hit along that direction.
            var dir = ray.To - ray.From;
            if (dir.IsZero) return Point3d.Origin;
            dir.Unitize();

            Point3d? bestHit = null;
            double bestT = double.MaxValue;

            foreach (var obj in RhinoDoc.ActiveDoc.Objects)
            {
                if (obj == null || !obj.IsValid) continue;
                if (obj.Attributes.Mode == ObjectMode.Hidden) continue;

                Mesh[] meshes = null;
                try { meshes = obj.GetMeshes(MeshType.Render); }
                catch { continue; }
                if (meshes == null) continue;

                foreach (var m in meshes)
                {
                    if (m == null) continue;
                    var hits = Intersection.MeshLine(m, ray, out _);
                    if (hits == null) continue;
                    foreach (var p in hits)
                    {
                        var t = (p - ray.From) * dir;
                        if (t <= 0) continue;
                        if (t < bestT) { bestT = t; bestHit = p; }
                    }
                }
            }

            if (bestHit.HasValue) return bestHit.Value;

            // Fallback: construction plane intersection.
            var plane = vp.ConstructionPlane();
            if (Intersection.LinePlane(ray, plane, out double pt))
                return ray.PointAt(pt);

            return Point3d.Origin;
        }
    }
}
