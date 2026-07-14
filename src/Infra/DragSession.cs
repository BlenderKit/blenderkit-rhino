using System;
using System.Collections.Generic;
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
        // TickCount when the drag started. A safety net: if the mouse-release
        // is never observed (Eto's static Mouse.Buttons occasionally misses
        // the up-transition on macOS when the release lands outside our event
        // stream), the drag would otherwise poll forever and leave its preview
        // box painted in the viewport. After this long we treat it as a cancel.
        private int _startTick;
        private const int MaxDragMs = 45_000; // 45 s — far beyond any real drag

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
            _startTick = System.Environment.TickCount;
            // Build the ray-target cache ONCE here so ProjectWithNormal's
            // per-tick loop doesn't have to walk RhinoDoc.ActiveDoc.Objects
            // and (worst case) Duplicate+Transform every InstanceObject
            // member-mesh at 30 Hz. The cache lives only for the duration
            // of this drag; Stop() drops it. See BuildRayTargets for the
            // full rationale.
            BuildRayTargets();
            // Diagnostic so we can confirm in the panel log that the
            // drag-perf optimisation is actually live in the deployed
            // build. If you see this line, you're running the new code.
            try
            {
                int n = _rayTargets == null ? 0 : _rayTargets.Count;
                BkLog.W("[drag-perf] cached " + n + " ray targets at drag start");
            }
            catch { }
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
            _rayTargets = null;  // release the per-drag mesh refs
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
            // Safety net: a drag that outlives any plausible real drag means we
            // missed the mouse-up. Cancel it so the preview box can't hang in
            // the viewport forever (the "Drop to place" box that never clears).
            if (System.Environment.TickCount - _startTick > MaxDragMs)
            {
                Stop(disablePreview: true);
                OnCancel?.Invoke();
                return;
            }
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
        /// Cursor position in the same coordinate space Rhino's
        /// <see cref="RhinoView.ScreenRectangle"/> uses, so we can hit-test
        /// "is the cursor over a viewport?" without inventing our own
        /// projection.
        ///
        /// Per-platform:
        ///   - Windows: <c>GetCursorPos</c> returns raw physical pixels —
        ///     same space ScreenRectangle uses. Eto's Mouse.Position is
        ///     in DIPs (half-position at 200% DPI) so we can't just trust
        ///     that on Win.
        ///   - macOS: Eto.Mac and Rhino's screen rect are both in Cocoa
        ///     points (logical units). DO NOT scale by LogicalPixelSize —
        ///     that double-counts DPI and pushes the cursor off-screen on
        ///     Retina displays. The visible bug: drops cancel with
        ///     "released outside any viewport" even though the cursor is
        ///     clearly on the viewport (rhino_panel.log lines 1-3 of any
        ///     drag attempt on a 200% Mac).
        ///   - Linux: empirically the same logical-vs-physical mismatch
        ///     as Windows shows up under WPF, so we keep the
        ///     LogicalPixelSize multiplier as a best guess. Adjust if a
        ///     Linux user reports the same off-by-DPI symptom Mac had.
        /// </summary>
        private static (int x, int y) GetCursorPhysical()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (GetCursorPos(out var p)) return (p.X, p.Y);
            }
            var pos = Mouse.Position;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return ((int)pos.X, (int)pos.Y);
            }
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

        // ===== Per-drag ray-target cache ============================
        //
        // The old ProjectWithNormal iterated RhinoDoc.ActiveDoc.Objects
        // every tick — for every InstanceObject it then walked all
        // definition members and, on each one, called Duplicate +
        // Transform on the mesh before the raycast. At 30 Hz that's a
        // mesh allocation per (object × member) PER FRAME, which murders
        // the drag-drop frame rate in any scene with imported blocks
        // (visible vs. plain camera rotation, which uses Rhino's
        // pre-built display caches and stays smooth).
        //
        // BuildRayTargets pre-computes a flat list at drag-start:
        //   * For plain Mesh objects: hold a ref to the mesh, no xform.
        //   * For InstanceObjects: hold a ref to each member's mesh
        //     plus the instance's xform AND its precomputed inverse.
        //     The raycast in TestRayTarget transforms the RAY into
        //     mesh-local space (two Point3d ops) instead of duplicating
        //     and transforming a whole mesh. After the hit, the hit
        //     point + normal are transformed back to world space.
        //
        // Per-frame allocations drop to roughly zero. The drag-start
        // pass is O(scene-object-count) and runs once.
        //
        // Doc-mutation during a drag (rare — typical drag is < 5s) is
        // not handled: the cache is a snapshot. Worst case is the user
        // adds geometry mid-drag and the new objects aren't pickable
        // until the next drag.

        private struct RayTarget
        {
            public Guid TopLevelId;
            public Mesh Mesh;
            // True when Xform/InverseXform are meaningful (InstanceObject
            // member); false for plain meshes already in world space.
            public bool HasXform;
            public Transform Xform;        // world ← mesh-local
            public Transform InverseXform; // mesh-local ← world
        }

        private static List<RayTarget> _rayTargets;

        private static void BuildRayTargets()
        {
            var doc = RhinoDoc.ActiveDoc;
            _rayTargets = new List<RayTarget>(64);
            if (doc == null) return;

            foreach (var obj in doc.Objects)
            {
                if (obj == null || !obj.IsValid) continue;
                if (obj.Attributes.Mode == ObjectMode.Hidden) continue;

                // InstanceObject (block): cache one entry per member-mesh
                // with the instance's transform. Skip members whose
                // transform isn't invertible (degenerate scale = drop
                // would never hit them anyway).
                if (obj is InstanceObject inst && inst.InstanceDefinition != null)
                {
                    var members = inst.InstanceDefinition.GetObjects();
                    var xform = inst.InstanceXform;
                    if (!xform.TryGetInverse(out var inv)) continue;
                    if (members == null) continue;
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
                        foreach (var src in mms)
                        {
                            if (src == null) continue;
                            _rayTargets.Add(new RayTarget
                            {
                                TopLevelId = obj.Id,
                                Mesh = src,
                                HasXform = true,
                                Xform = xform,
                                InverseXform = inv,
                            });
                        }
                    }
                    continue;
                }

                // Plain object — direct meshes or render meshes.
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
                if (meshes == null) continue;
                foreach (var m in meshes)
                {
                    if (m == null) continue;
                    _rayTargets.Add(new RayTarget
                    {
                        TopLevelId = obj.Id,
                        Mesh = m,
                        HasXform = false,
                    });
                }
            }
        }

        private static (Point3d pt, Vector3d normal, Guid hitId) ProjectWithNormal(
            RhinoView view, int sx, int sy)
        {
            var r = view.ScreenRectangle;
            int lx = sx - r.Left;
            int ly = sy - r.Top;
            // macOS unit-mismatch: ScreenRectangle and Mouse.Position are
            // in Cocoa points (logical), but GetFrustumLine expects
            // physical pixels — same convention as on Windows. On a
            // Retina display that's a 2× difference, so without this
            // scale the ray hits a point at half the cursor's actual
            // distance from the viewport origin and the drop lands at
            // ~half-cursor (visible bug in quad-view: preview appears
            // in the right viewport but at half the cursor offset).
            // We only scale the local coords passed to GetFrustumLine
            // here — leaving the screen-space (sx, sy) alone so the
            // ViewAt rect-test elsewhere keeps working in points.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var screen = Eto.Forms.Screen.FromPoint(new Eto.Drawing.PointF(sx, sy))
                          ?? Eto.Forms.Screen.PrimaryScreen;
                var scale = (float)(screen?.LogicalPixelSize ?? 1.0);
                if (scale > 0 && Math.Abs(scale - 1.0) > 0.01)
                {
                    lx = (int)(lx * scale);
                    ly = (int)(ly * scale);
                }
            }
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

            // Tight per-target raycast. For a plain (already-world-space)
            // mesh: just MeshLine on the original. For an InstanceObject
            // member: transform the ray's endpoints into mesh-local
            // space (two Point3d ops), MeshLine on the original mesh,
            // then transform the hit point + normal back to world.
            // Zero per-frame mesh allocations regardless of scene size.
            if (_rayTargets == null) BuildRayTargets();
            var targets = _rayTargets;  // capture for thread-safety
            for (int t = 0; t < targets.Count; t++)
            {
                var tgt = targets[t];
                var m = tgt.Mesh;
                if (m == null) continue;
                // Lazy face-normal compute — cached on the Mesh itself,
                // so this only fires the first frame we touch it.
                if (m.FaceNormals.Count == 0) m.FaceNormals.ComputeFaceNormals();

                Line localRay = ray;
                if (tgt.HasXform)
                {
                    var p1 = ray.From; p1.Transform(tgt.InverseXform);
                    var p2 = ray.To;   p2.Transform(tgt.InverseXform);
                    localRay = new Line(p1, p2);
                }

                var hits = Intersection.MeshLine(m, localRay, out int[] faceIds);
                if (hits == null) continue;
                for (int i = 0; i < hits.Length; i++)
                {
                    var p = hits[i];
                    Vector3d nn = Vector3d.ZAxis;
                    if (faceIds != null && i < faceIds.Length
                        && faceIds[i] < m.FaceNormals.Count)
                    {
                        var fn = m.FaceNormals[faceIds[i]];
                        nn = new Vector3d(fn.X, fn.Y, fn.Z);
                        if (nn.IsZero) nn = Vector3d.ZAxis;
                    }
                    // Transform the hit point + normal back to world
                    // space so the camera-distance compare and
                    // downstream consumers see consistent coords.
                    if (tgt.HasXform)
                    {
                        p.Transform(tgt.Xform);
                        nn.Transform(tgt.Xform);
                        if (nn.IsZero) nn = Vector3d.ZAxis;
                        else nn.Unitize();
                    }
                    var d = camera.DistanceTo(p);
                    if (d >= bestDist) continue;
                    bestDist = d;
                    bestPt = p;
                    bestNormal = nn;
                    bestHitId = tgt.TopLevelId;
                }
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
