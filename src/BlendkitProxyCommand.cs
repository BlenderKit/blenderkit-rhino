using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Blendkit.Rhino.Infra;

namespace Blendkit.Rhino
{
    /// <summary>
    /// `BlendkitProxy` — explicit user control over the viewport-proxy
    /// system. Modes:
    ///
    ///   Toggle  — flip the global conduit Active flag on/off.
    ///   Attach  — generate proxies for currently-selected mesh/block
    ///             objects (a no-op if their face count is below
    ///             <see cref="ProxyMeshService.AutoAttachFaceThreshold"/>;
    ///             use AttachForce to override that floor).
    ///   AttachForce — like Attach but ignores the face-count floor.
    ///   Detach  — drop the cached proxy for selected objects.
    ///   Clear   — drop every cached proxy.
    ///   Status  — report cache size + conduit Active flag.
    ///
    /// The auto-attach hook in BlendkitPanel.ImportFile / ImportForDrop
    /// covers the Blendkit-import case; this command is for everything
    /// else (existing scene geometry, manual experimentation).
    /// </summary>
    [System.Runtime.InteropServices.Guid("c2a4e8f0-1d6f-4f10-9a31-2b9b8c1d4e02")]
    public class BlendkitProxyCommand : Command
    {
        public override string EnglishName => "BlendkitProxy";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var go = new GetOption();
            go.SetCommandPrompt("Proxy mesh action");
            int idxToggle = go.AddOption("Toggle");
            int idxAttach = go.AddOption("Attach");
            int idxAttachForce = go.AddOption("AttachForce");
            int idxDetach = go.AddOption("Detach");
            int idxClear = go.AddOption("Clear");
            int idxStatus = go.AddOption("Status");
            go.Get();
            if (go.CommandResult() != Result.Success) return go.CommandResult();

            int idx = go.OptionIndex();
            if (idx == idxToggle)      return RunToggle(doc);
            if (idx == idxAttach)      return RunAttach(doc, forceLowPoly: false);
            if (idx == idxAttachForce) return RunAttach(doc, forceLowPoly: true);
            if (idx == idxDetach)      return RunDetach(doc);
            if (idx == idxClear)       return RunClear(doc);
            if (idx == idxStatus)      return RunStatus(doc);
            return Result.Cancel;
        }

        private static Result RunToggle(RhinoDoc doc)
        {
            var conduit = BlendkitPlugIn.Instance?.ProxyConduit;
            if (conduit == null) { RhinoApp.WriteLine("[Blendkit] Proxy conduit not initialised."); return Result.Failure; }
            conduit.Active = !conduit.Active;
            doc.Views.Redraw();
            RhinoApp.WriteLine($"[Blendkit] Proxy display now {(conduit.Active ? "ENABLED" : "DISABLED")}.");
            return Result.Success;
        }

        private static Result RunAttach(RhinoDoc doc, bool forceLowPoly)
        {
            // Honour an existing selection if one is set; otherwise prompt
            // for objects. Mirrors how Rhino's built-in commands behave.
            var selected = SelectedOrPick(doc);
            if (selected.Count == 0) return Result.Cancel;

            int attached = 0, skipped = 0, failed = 0;
            foreach (var obj in selected)
            {
                bool eligible = forceLowPoly || ProxyMeshService.ShouldAutoAttach(obj);
                if (!eligible) { skipped++; continue; }
                var ok = ProxyMeshService.MakeProxyFor(obj);
                if (ok) attached++; else failed++;
            }
            RhinoApp.WriteLine($"[Blendkit] Proxy attach: {attached} attached, {skipped} below threshold, {failed} failed.");
            doc.Views.Redraw();
            return Result.Success;
        }

        private static Result RunDetach(RhinoDoc doc)
        {
            var selected = SelectedOrPick(doc);
            if (selected.Count == 0) return Result.Cancel;
            int detached = 0;
            foreach (var obj in selected)
            {
                if (ProxyMeshService.Detach(obj.Id) != null) detached++;
            }
            RhinoApp.WriteLine($"[Blendkit] Proxy detach: {detached} cleared.");
            doc.Views.Redraw();
            return Result.Success;
        }

        private static Result RunClear(RhinoDoc doc)
        {
            int n = ProxyMeshService.Count;
            ProxyMeshService.Clear();
            RhinoApp.WriteLine($"[Blendkit] Cleared {n} proxy mesh(es).");
            doc.Views.Redraw();
            return Result.Success;
        }

        private static Result RunStatus(RhinoDoc doc)
        {
            var conduit = BlendkitPlugIn.Instance?.ProxyConduit;
            string state = conduit == null ? "uninitialised" : (conduit.Active ? "ENABLED" : "DISABLED");
            RhinoApp.WriteLine($"[Blendkit] Proxy display: {state}. Cached proxies: {ProxyMeshService.Count}.");
            return Result.Success;
        }

        /// <summary>
        /// Return currently-selected RhinoObjects, or prompt the user to
        /// pick if nothing is selected. Filtered to Mesh + InstanceObject
        /// since those are the types ProxyMeshService knows how to handle.
        /// </summary>
        private static List<RhinoObject> SelectedOrPick(RhinoDoc doc)
        {
            var existing = new List<RhinoObject>();
            foreach (var o in doc.Objects.GetSelectedObjects(includeLights: false, includeGrips: false))
            {
                if (o is MeshObject || o is InstanceObject) existing.Add(o);
            }
            if (existing.Count > 0) return existing;

            var go = new GetObject();
            go.SetCommandPrompt("Select meshes or blocks");
            go.GeometryFilter = ObjectType.Mesh | ObjectType.InstanceReference;
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success) return new List<RhinoObject>();

            var picked = new List<RhinoObject>();
            for (int i = 0; i < go.ObjectCount; i++)
            {
                var oref = go.Object(i);
                var ro = oref?.Object();
                if (ro != null) picked.Add(ro);
            }
            return picked;
        }
    }
}
