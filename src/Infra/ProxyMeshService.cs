using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Per-object proxy mesh cache + decimation utilities.
    ///
    /// Why this exists: Blendkit imports often land 100k+ face meshes that
    /// crawl Rhino's viewport. Rendered / Raytraced views (and the actual
    /// _Render pipeline) still need the full geometry, but Wireframe /
    /// Shaded views can live with a decimated stand-in. <see cref="ProxyDisplayConduit"/>
    /// reads this cache to decide what to draw.
    ///
    /// Storage is process-lifetime only (no UserData persistence) — keeps
    /// the on-disk .3dm clean and avoids the cost of a custom UserData
    /// subclass for an experimental feature. Trade-off: proxies are
    /// regenerated when the user reopens Rhino. <see cref="MakeProxyFor"/>
    /// runs in well under a second per 100k-face mesh in practice, so the
    /// first frame after re-opening pays the cost once.
    ///
    /// Cache key is the RhinoObject's Guid. For InstanceObjects we cache
    /// against the InstanceObject's Id (not the definition's index) so two
    /// instances of the same block can independently have proxies — useful
    /// if one is in a "needs to look right" view and the other isn't.
    /// </summary>
    public static class ProxyMeshService
    {
        /// <summary>
        /// Threshold above which the auto-attach hook generates a proxy.
        /// Below this, the saving doesn't outweigh the GL-state churn of
        /// drawing through a conduit instead of through Rhino's normal
        /// instanced-batch path.
        /// </summary>
        public const int AutoAttachFaceThreshold = 20_000;

        /// <summary>Target face count for the decimated proxy.</summary>
        public const int DefaultTargetFaceCount = 5_000;

        // ConcurrentDictionary because the conduit runs on the render
        // thread while attach/detach happen on the UI thread. Lock-free
        // reads are the whole point — the conduit fires on every frame.
        private static readonly ConcurrentDictionary<Guid, Mesh> _proxies
            = new ConcurrentDictionary<Guid, Mesh>();

        public static bool TryGetProxy(Guid id, out Mesh mesh)
            => _proxies.TryGetValue(id, out mesh);

        public static bool HasProxy(Guid id) => _proxies.ContainsKey(id);

        public static int Count => _proxies.Count;

        public static IEnumerable<Guid> ProxiedIds => _proxies.Keys;

        /// <summary>
        /// Detach + return the original cached proxy (if any). The mesh is
        /// returned so callers can dispose it if they want; otherwise it
        /// falls out of scope and is GC'd.
        /// </summary>
        public static Mesh Detach(Guid id)
        {
            _proxies.TryRemove(id, out var m);
            return m;
        }

        public static void Clear() => _proxies.Clear();

        /// <summary>
        /// Use an externally-supplied mesh as the proxy for this object
        /// — no Mesh.Reduce roundtrip. Intended for the PRX-as-proxy
        /// path: the Blender add-on ships a hand-tuned proxy alongside
        /// the asset, we read it and adopt it directly, no decimation
        /// quality loss.
        ///
        /// Returns false when <paramref name="proxy"/> is null/empty.
        /// Otherwise the cache entry is replaced (any previous proxy
        /// for this id is disposed) and true is returned.
        /// </summary>
        public static bool AttachExistingProxy(Guid id, Mesh proxy)
        {
            if (proxy == null || proxy.Faces.Count == 0) return false;
            if (_proxies.TryRemove(id, out var old)) old?.Dispose();
            _proxies[id] = proxy;
            return true;
        }

        /// <summary>
        /// Build a decimated proxy mesh for the given RhinoObject and store
        /// it under the object's Id. Returns false if no proxy was needed
        /// or the mesh couldn't be reduced. Existing entries are overwritten.
        ///
        /// Supports raw <see cref="MeshObject"/> (uses MeshGeometry directly)
        /// and <see cref="InstanceObject"/> (combines all meshes inside the
        /// instance definition — see <see cref="CombineDefinitionMeshes"/>).
        /// </summary>
        public static bool MakeProxyFor(RhinoObject obj, int targetFaceCount = DefaultTargetFaceCount)
        {
            if (obj == null) return false;
            var combined = ExtractMesh(obj);
            if (combined == null || combined.Faces.Count == 0) return false;

            // Below threshold → not worth a proxy. Caller's auto-attach
            // path checks ShouldAutoAttach first; this guard is the
            // belt-and-braces for direct callers.
            if (combined.Faces.Count <= targetFaceCount) return false;

            // Reduce works in-place. CopyFrom on a Mesh doesn't deep-copy
            // attributes we don't need (textures, vertex colours), keeping
            // the proxy lean. allowDistortion=true favours triangle count
            // over preserving sharp features — acceptable for a viewport
            // stand-in. accuracy=10 is the spec'd max precision.
            var proxy = new Mesh();
            proxy.CopyFrom(combined);
            try
            {
                bool ok = proxy.Reduce(targetFaceCount, true, 10, false);
                if (!ok || proxy.Faces.Count == 0)
                {
                    proxy.Dispose();
                    return false;
                }
            }
            catch
            {
                proxy.Dispose();
                return false;
            }

            // Stash + invalidate the cached entry if one existed. The
            // conduit's next frame picks up the new mesh.
            if (_proxies.TryRemove(obj.Id, out var old)) old?.Dispose();
            _proxies[obj.Id] = proxy;
            return true;
        }

        /// <summary>
        /// Predicate the import auto-attach hook uses to decide whether the
        /// expense of decimating is worthwhile. Roughly: "would this slow
        /// the viewport enough to notice?" — yes for big meshes, no for
        /// small ones where the proxy overhead would dominate.
        /// </summary>
        public static bool ShouldAutoAttach(RhinoObject obj)
        {
            if (obj == null) return false;
            var combined = ExtractMesh(obj);
            return combined != null && combined.Faces.Count >= AutoAttachFaceThreshold;
        }

        /// <summary>
        /// Pull a single Mesh out of the RhinoObject we can hand to Reduce.
        /// For MeshObject: the geometry directly. For InstanceObject:
        /// combine every Mesh inside the definition (without applying the
        /// instance transform — the conduit applies that at draw time so
        /// the same proxy works for any number of instances).
        /// </summary>
        public static Mesh ExtractMesh(RhinoObject obj)
        {
            if (obj is MeshObject mo) return mo.MeshGeometry;
            if (obj is InstanceObject io) return CombineDefinitionMeshes(io.InstanceDefinition);
            // BrepObject / Extrusion etc could be supported by pulling
            // their render meshes — left for follow-up; the Blendkit
            // import path produces Mesh and Block (InstanceObject), so
            // these two cover the user-visible pain.
            return null;
        }

        /// <summary>
        /// Walk every mesh in an instance definition and stitch them into
        /// one combined Mesh in DEFINITION-LOCAL coordinates. The conduit
        /// applies the per-instance transform when drawing.
        ///
        /// Nested instance definitions are recursed into so a block
        /// containing blocks still gets fully captured.
        /// </summary>
        private static Mesh CombineDefinitionMeshes(InstanceDefinition def)
        {
            if (def == null) return null;
            var combined = new Mesh();
            foreach (var member in def.GetObjects())
            {
                AppendMeshes(member, Transform.Identity, combined);
            }
            return combined.Faces.Count == 0 ? null : combined;
        }

        private static void AppendMeshes(RhinoObject obj, Transform xform, Mesh acc)
        {
            if (obj is MeshObject mo)
            {
                var m = mo.MeshGeometry;
                if (m == null) return;
                var copy = m.DuplicateMesh();
                if (!xform.IsIdentity) copy.Transform(xform);
                acc.Append(copy);
                return;
            }
            if (obj is InstanceObject io)
            {
                var def = io.InstanceDefinition;
                if (def == null) return;
                var nested = Transform.Multiply(xform, io.InstanceXform);
                foreach (var sub in def.GetObjects())
                {
                    AppendMeshes(sub, nested, acc);
                }
            }
            // Other geometry types fall through silently — the import path
            // produces Meshes and Blocks, so this is good enough for now.
        }
    }
}
