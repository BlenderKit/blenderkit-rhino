using System;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Forms;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Modal-style drag tracker. Polls the global mouse state (Eto's static
    /// `Mouse.Position` / `Mouse.Buttons`) on a UI timer, so we don't depend
    /// on Rhino's MouseCallback or Eto's per-control event routing — both of
    /// which miss events when the press starts in our panel and the release
    /// happens elsewhere (which is exactly the drag-drop case).
    ///
    /// Behavior:
    ///   - On every tick: figure out which Rhino view (if any) the cursor is
    ///     over, raycast scene meshes for the drop point, update the
    ///     <see cref="Preview"/> conduit's Target, redraw that view.
    ///   - When the user releases the mouse: if the cursor was over a view
    ///     at release time, fire <see cref="OnDrop"/> with that point.
    ///     Otherwise fire <see cref="OnCancel"/>.
    /// </summary>
    public class DragSession
    {
        private readonly UITimer _timer;
        private bool _started;
        private RhinoView _lastView;

        public DragPreviewConduit Preview;
        // (view, hitPoint, surfaceNormal, spinRadians, hitObjectId).
        // Normal is +Z when we miss all meshes and fall back to the cplane.
        // spin is the accumulated mousewheel rotation about that normal
        // during the drag. hitObjectId is the doc id of the first
        // raycast-hit RhinoObject (Guid.Empty if the drop landed on the
        // construction plane). Used by the material drop path to assign
        // the new material to a specific target object.
        public Action<RhinoView, Point3d, Vector3d, double, Guid> OnDrop;
        public Action OnCancel;

        // Win32 low-level mouse hook — used only during a drag to capture
        // and SWALLOW wheel ticks. Rhino's MouseCallback doesn't surface
        // wheel events, and without swallowing the event the viewport
        // would zoom while the user is trying to rotate the asset.
        private WheelHook _wheel;

        public DragSession()
        {
            _timer = new UITimer { Interval = 0.033 }; // ~30 Hz
            _timer.Elapsed += (s, e) => Tick();
        }

        public void Start()
        {
            _started = true;
            // Wheel-capture is Windows-only (uses WH_MOUSE_LL via
            // user32.dll). On macOS / Linux we just don't capture
            // wheel events during drag — the viewport will zoom
            // instead, which is a tolerable degradation. Implementing
            // the same UX on macOS would require a CGEventTap monitor
            // and is left as a follow-up.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _wheel = WheelHook.Install(this);
            _timer.Start();
        }

        private void Stop(bool disablePreview = true)
        {
            _started = false;
            _timer.Stop();
            _wheel?.Uninstall(); _wheel = null;
            if (disablePreview && Preview != null) Preview.Enabled = false;
            if (_lastView != null) _lastView.Redraw();
        }

        /// <summary>
        /// Win32 low-level mouse hook (WH_MOUSE_LL). Active only during a
        /// drag. Returns a non-zero result for WM_MOUSEWHEEL so the event
        /// never reaches Rhino's viewport — otherwise the wheel both rotates
        /// our preview AND zooms the viewport at the same time.
        /// </summary>
        private class WheelHook
        {
            private const int WH_MOUSE_LL = 14;
            private const int WM_MOUSEWHEEL = 0x020A;

            private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct MSLLHOOKSTRUCT
            {
                public POINT pt;
                public uint mouseData;
                public uint flags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            private static extern bool UnhookWindowsHookEx(IntPtr hhk);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr GetModuleHandle(string lpModuleName);

            private IntPtr _handle;
            private LowLevelMouseProc _proc; // keep a strong reference
            private DragSession _owner;

            public static WheelHook Install(DragSession owner)
            {
                var h = new WheelHook { _owner = owner };
                h._proc = h.Callback;
                h._handle = SetWindowsHookEx(WH_MOUSE_LL, h._proc, GetModuleHandle(null), 0);
                return h;
            }

            public void Uninstall()
            {
                if (_handle != IntPtr.Zero) { UnhookWindowsHookEx(_handle); _handle = IntPtr.Zero; }
            }

            private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL && _owner != null && _owner._started && _owner.Preview != null)
                {
                    var data = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    // High word of mouseData carries the signed wheel delta.
                    var delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    var step = (delta > 0 ? 1 : -1) * (Math.PI / 12.0); // 15° per notch
                    _owner.Preview.SpinRadians += step;
                    if (_owner._lastView != null) _owner._lastView.Redraw();
                    // Non-zero return swallows the event — viewport doesn't zoom.
                    return new IntPtr(1);
                }
                return CallNextHookEx(_handle, nCode, wParam, lParam);
            }
        }

        private void Tick()
        {
            if (!_started) return;
            // Eto's Mouse.Position is in DIPs (logical pixels) on WPF; on a
            // 200%-DPI display it's half of where the cursor actually is.
            // Win32 GetCursorPos returns raw physical pixels — same space
            // Rhino.RhinoView.ScreenRectangle uses — so positions match
            // regardless of DPI.
            var (px, py) = GetCursorPhysical();
            var view = ViewAt(px, py);

            if (Mouse.Buttons == MouseButtons.None)
            {
                if (view != null)
                {
                    var (pt, normal, hitId) = ProjectWithNormal(view, px, py);
                    var spin = Preview?.SpinRadians ?? 0;
                    Stop(disablePreview: false); // leave the cube at the drop point
                    OnDrop?.Invoke(view, pt, normal, spin, hitId);
                }
                else
                {
                    Stop(disablePreview: true);
                    OnCancel?.Invoke();
                }
                return;
            }

            if (view != null && Preview != null)
            {
                var (pt, normal, hitId) = ProjectWithNormal(view, px, py);
                Preview.Target = pt;
                Preview.Normal = normal;
                // Surface the hovered object in the preview so the user
                // sees "drop on Chrome Side Table" instead of guessing
                // whether the material will land somewhere useful.
                Preview.HoverTargetName = hitId == Guid.Empty
                    ? null
                    : (RhinoDoc.ActiveDoc?.Objects?.Find(hitId)?.Attributes?.Name);
                view.Redraw();
                _lastView = view;
            }
        }

        // (legacy duplicate Stop removed — see Stop(bool) at the top.)

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        /// <summary>
        /// Cursor position in screen pixels — same coordinate space Rhino's
        /// RhinoView.ScreenRectangle uses. On Windows we call GetCursorPos
        /// directly because Eto's Mouse.Position returns DIPs (half-position
        /// at 200% DPI). On macOS / Linux we fall back to Mouse.Position
        /// scaled by the screen's logical-pixel factor — same effect, no
        /// Win32 dependency required for the build to load.
        /// </summary>
        private static (int x, int y) GetCursorPhysical()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (GetCursorPos(out var p)) return (p.X, p.Y);
            }
            var pos = Mouse.Position;
            var screen = Eto.Forms.Screen.FromPoint(pos) ?? Eto.Forms.Screen.PrimaryScreen;
            var scale = (float)(screen?.LogicalPixelSize ?? 1.0);
            return ((int)(pos.X * scale), (int)(pos.Y * scale));
        }

        private static RhinoView ViewAt(int x, int y)
        {
            foreach (var v in RhinoDoc.ActiveDoc.Views)
            {
                if (v == null) continue;
                var r = v.ScreenRectangle;
                if (r.Contains(x, y)) return v;
            }
            return null;
        }

        private static (Point3d pt, Vector3d normal, Guid hitId) ProjectWithNormal(
            RhinoView view, int sx, int sy)
        {
            var r = view.ScreenRectangle;
            int lx = sx - r.Left;
            int ly = sy - r.Top;
            var vp = view.ActiveViewport;
            if (!vp.GetFrustumLine(lx, ly, out Line ray))
                return (Point3d.Origin, Vector3d.ZAxis, Guid.Empty);

            Point3d? bestPt = null;
            Vector3d bestNormal = Vector3d.ZAxis;
            Guid bestHitId = Guid.Empty;
            // Use distance-from-camera for "closest" instead of t-along-ray.
            // GetFrustumLine's direction is documented as near→far but on
            // certain projections the practical result has been the back-
            // side hit getting picked. CameraLocation is unambiguous.
            var camera = vp.CameraLocation;
            double bestDist = double.MaxValue;
            var dir = ray.To - ray.From;
            if (dir.IsZero) return (Point3d.Origin, Vector3d.ZAxis, Guid.Empty);
            dir.Unitize();

            // Inner test against one mesh + the (top-level) Guid we'd
            // attribute the hit to. Used by both the plain and the
            // InstanceObject branch below.
            void TestMesh(Mesh m, Guid topLevelId)
            {
                if (m == null) return;
                if (m.FaceNormals.Count == 0) m.FaceNormals.ComputeFaceNormals();
                var hits = Intersection.MeshLine(m, ray, out int[] faceIds);
                if (hits == null) return;
                for (int i = 0; i < hits.Length; i++)
                {
                    var p = hits[i];
                    var d = camera.DistanceTo(p);
                    if (d >= bestDist) continue;
                    Vector3d nn = Vector3d.ZAxis;
                    if (faceIds != null && i < faceIds.Length
                        && faceIds[i] < m.FaceNormals.Count)
                    {
                        var fn = m.FaceNormals[faceIds[i]];
                        nn = new Vector3d(fn.X, fn.Y, fn.Z);
                        if (nn.IsZero) nn = Vector3d.ZAxis;
                    }
                    bestDist = d;
                    bestPt = p;
                    bestNormal = nn;
                    bestHitId = topLevelId;
                }
            }

            foreach (var obj in RhinoDoc.ActiveDoc.Objects)
            {
                if (obj == null || !obj.IsValid) continue;
                if (obj.Attributes.Mode == ObjectMode.Hidden) continue;

                // InstanceObject (block) — its Geometry is an
                // InstanceReferenceGeometry placeholder, not a Mesh, so
                // the plain "is Mesh" / GetMeshes path returns nothing
                // and the user's drop falls through to the cplane
                // (verified in rhino_panel.log: hit=00000000... when
                // dropping material on an imported BlenderKit model).
                // Walk the InstanceDefinition's members and raycast
                // each one's mesh, transformed by the instance's
                // xform. Attribute the hit to the top-level
                // InstanceObject so AssignMaterialToObject can find it
                // and propagate to the InstDef members.
                if (obj is InstanceObject inst && inst.InstanceDefinition != null)
                {
                    var members = inst.InstanceDefinition.GetObjects();
                    var xform = inst.InstanceXform;
                    if (members != null)
                    {
                        foreach (var member in members)
                        {
                            if (member == null) continue;
                            Mesh[] mms = null;
                            if (member.Geometry is Mesh mm0) mms = new[] { mm0 };
                            else
                            {
                                try { mms = member.GetMeshes(MeshType.Render); } catch { }
                                if (mms == null || mms.Length == 0)
                                {
                                    try { mms = member.GetMeshes(MeshType.Default); } catch { }
                                }
                            }
                            if (mms == null) continue;
                            foreach (var srcMesh in mms)
                            {
                                if (srcMesh == null) continue;
                                // Transform a duplicate so the raycast
                                // sees world-space geometry without
                                // mutating the InstDef's stored mesh.
                                var dup = (Mesh)srcMesh.Duplicate();
                                dup.Transform(xform);
                                TestMesh(dup, obj.Id);
                            }
                        }
                    }
                    continue;
                }

                // Plain object. Direct Mesh objects (typical of imported
                // glTF) → use the geometry. Breps / extrusions → fall
                // back to render meshes. Try both because some imports
                // expose either path.
                Mesh[] meshes = null;
                if (obj.Geometry is Mesh mm) meshes = new[] { mm };
                else
                {
                    try { meshes = obj.GetMeshes(MeshType.Render); } catch { }
                    if (meshes == null || meshes.Length == 0)
                    {
                        try { meshes = obj.GetMeshes(MeshType.Default); } catch { }
                    }
                }
                if (meshes == null || meshes.Length == 0) continue;
                foreach (var m in meshes) TestMesh(m, obj.Id);
            }

            if (bestPt.HasValue) return (bestPt.Value, bestNormal, bestHitId);

            // Fall back to construction plane intersection.
            var cplane = vp.ConstructionPlane();
            if (Intersection.LinePlane(ray, cplane, out double tt))
                return (ray.PointAt(tt), cplane.Normal, Guid.Empty);
            return (Point3d.Origin, Vector3d.ZAxis, Guid.Empty);
        }
    }
}
