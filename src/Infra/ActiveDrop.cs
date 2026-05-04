using System;
using Rhino.Geometry;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// State for a single in-flight drag → download → convert → place flow.
    /// Multiple of these can be alive at once: the panel owns a list and
    /// dispatches /report task results to whichever drop's task_id matches.
    /// </summary>
    public class ActiveDrop
    {
        public DragPreviewConduit Preview = new DragPreviewConduit();
        // Captured at the moment the user releases the mouse over a viewport
        // (DragSession.OnDrop). Null until then; null after a finished import.
        public Point3d? DropPoint;
        public Vector3d Normal = Vector3d.ZAxis;
        // Doc id of the object hit by the drop raycast, when one was hit.
        // Used by the material drop path to assign the new material to the
        // specific object the user dropped onto (mirrors the Blender addon's
        // "target object/slot" behavior). Guid.Empty when the drop landed
        // on the construction plane / nothing.
        public Guid HitObjectId = Guid.Empty;
        // Mousewheel rotation accumulated during the drag, in radians.
        // Applied in ImportAtPoint so the asset lands oriented as the user
        // last saw the preview cube spinning.
        public double SpinRadians;
        // Asset name + bbox come from the search hit — used for the preview.
        public string AssetName = "";
        // "model" / "material" / "hdr" — used to pick the right
        // post-download pipeline (geometry vs material vs environment).
        public string AssetType = "model";
        // Canonical BlenderKit UUID for the asset. Used as the cache key
        // for the InstanceDefinition reuse fast path — once the user
        // drops an asset once we keep its block around so further drops
        // of the same asset_base_id can skip both the download and the
        // geometry-duplication on import.
        public string AssetBaseId = "";
        // Task ids for the download and the .blend → .glb convert step. Either
        // can be null until the corresponding step has been dispatched.
        public string DownloadTaskId;
        public string ConvertTaskId;
        // Once the imported geometry lands we set this so progress callbacks
        // ignore the entry.
        public bool Done;
        // Free-form status string for the Downloads popup. Examples:
        // "Downloading…", "Converting…", "Waiting for drop", "Error: …".
        // Set at the call sites that mutate the drop's lifecycle so the
        // popup can render up-to-date status without us re-deriving from
        // the various task ids.
        public string Status = "Starting…";
    }
}
