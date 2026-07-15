using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Blendkit.Rhino.Infra;

namespace Blendkit.Rhino
{
    /// <summary>
    /// Command: `BlendkitClearBlocks` — drop the per-doc cache of
    /// Blendkit InstanceDefinitions so the next drop re-imports +
    /// re-blockifies. Doesn't delete the actual InstDefs from the doc
    /// (the user can still place them via _Insert), it just disconnects
    /// our reuse tracking. Useful while iterating on the import pipeline.
    /// </summary>
    [Guid("117f68fb-a4b1-446d-a943-cd23f4c1dc7c")]
    public class BlendkitClearBlocksCommand : Command
    {
        public BlendkitClearBlocksCommand() { Instance = this; }
        public static BlendkitClearBlocksCommand Instance { get; private set; }
        public override string EnglishName => "BlendkitClearBlocks";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            int cleared = BlendkitPanel.ClearInstDefCache(doc);
            BkLog.W($"BlendkitClearBlocks: dropped {cleared} cache entries for doc #{doc.RuntimeSerialNumber}");
            RhinoApp.WriteLine($"[Blendkit] cleared {cleared} block-cache entries.");
            return Result.Success;
        }
    }
}
