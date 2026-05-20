using System;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Swaps full-resolution geometry for decimated proxies in viewport
    /// display modes that don't need final-quality meshes. The actual
    /// rendered / raytraced view, plus the _Render pipeline, untouched.
    ///
    /// Two hooks fire on every frame for every visible object:
    ///   * <see cref="DisplayConduit.ObjectCulling"/> — runs first. If the
    ///     view is shaded/wireframe AND the object has a cached proxy in
    ///     <see cref="ProxyMeshService"/>, set Cull=true so Rhino skips
    ///     its default draw.
    ///   * <see cref="DisplayConduit.PreDrawObjects"/> — runs once before
    ///     any object's draw. We iterate the proxy cache and draw each
    ///     proxy in place of its culled host using the object's display
    ///     material so the colour matches.
    ///
    /// Selection, picking, layer visibility, locked state — all still flow
    /// through the original RhinoObject. We only replace the *draw*. So
    /// "looks right" interactions (selecting a chair, locking a layer)
    /// work exactly the same as without proxies.
    /// </summary>
    public sealed class ProxyDisplayConduit : DisplayConduit
    {
        /// <summary>Set false from the toggle command to disable proxying without losing the cache.</summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// Display modes where we let Rhino draw the original geometry.
        /// Anything else (Wireframe, Shaded, Ghosted, X-Ray, Technical,
        /// Pen, Artistic, Arctic) uses the proxy.
        ///
        /// Checked case-insensitively because custom display modes may
        /// have arbitrary casing. "Rendered" + "Raytraced" cover both
        /// Rhino's preview rasteriser and the Cycles-backed raytrace
        /// viewport that's the closest in-viewport approximation of the
        /// final render. Cycles' background raytrace mode also shows up
        /// as "Raytraced".
        /// </summary>
        private static bool IsFullQualityMode(DisplayPipeline pipe)
        {
            if (pipe?.Viewport?.DisplayMode == null) return false;
            var name = pipe.Viewport.DisplayMode.EnglishName ?? "";
            return name.Equals("Rendered", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Raytraced", StringComparison.OrdinalIgnoreCase);
        }

        protected override void ObjectCulling(CullObjectEventArgs e)
        {
            if (!Active) return;
            if (IsFullQualityMode(e.Display)) return;
            if (e.RhinoObject == null) return;
            if (ProxyMeshService.HasProxy(e.RhinoObject.Id))
            {
                // Skip Rhino's default mesh draw for this object — we
                // draw the proxy ourselves in PreDrawObjects. Picking,
                // selection highlight, etc still work because they go
                // through the RhinoObject directly, not the draw path.
                e.CullObject = true;
            }
        }

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            if (!Active) return;
            if (IsFullQualityMode(e.Display)) return;
            var doc = e.RhinoDoc;
            if (doc == null) return;

            // Iterate the proxy cache, not the whole doc, so the per-frame
            // cost scales with the number of proxied objects rather than
            // the scene's total object count.
            foreach (var id in ProxyMeshService.ProxiedIds)
            {
                if (!ProxyMeshService.TryGetProxy(id, out var proxy) || proxy == null) continue;
                var obj = doc.Objects.FindId(id);
                if (obj == null || !obj.IsValid) continue;
                if (!obj.Visible) continue;

                // Pull the display material from the object's attributes
                // so the proxy honours custom object colours. Falls back
                // to a neutral grey if material lookup fails — at worst
                // the proxy looks slightly off, never crashes.
                var mat = ResolveMaterial(obj);

                if (obj is InstanceObject io)
                {
                    // For block instances, transform a working copy of
                    // the proxy by the instance's xform. Duplicate before
                    // transforming so the cached proxy stays in
                    // definition-local space for the next instance.
                    var working = proxy.DuplicateMesh();
                    working.Transform(io.InstanceXform);
                    e.Display.DrawMeshShaded(working, mat);
                    working.Dispose();
                }
                else
                {
                    // MeshObject: proxy is already in world space.
                    e.Display.DrawMeshShaded(proxy, mat);
                }
            }
        }

        private static DisplayMaterial ResolveMaterial(RhinoObject obj)
        {
            try
            {
                var attrs = obj.Attributes;
                // Use the object's display colour when available. Pulling
                // the render material would be more accurate but requires
                // RDK round-trips per frame — too expensive. Display
                // colour is good enough for proxy mode by design (the
                // user switches to Rendered to see real materials).
                var colour = obj.Attributes.ObjectColor;
                if (attrs.ColorSource == ObjectColorSource.ColorFromLayer)
                {
                    var layer = obj.Document?.Layers.FindIndex(attrs.LayerIndex);
                    if (layer != null) colour = layer.Color;
                }
                return new DisplayMaterial(colour);
            }
            catch
            {
                return new DisplayMaterial(System.Drawing.Color.LightGray);
            }
        }
    }
}
