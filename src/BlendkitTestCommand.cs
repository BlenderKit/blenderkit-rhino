using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using Blendkit.Rhino.Infra;

namespace Blendkit.Rhino
{
    /// <summary>
    /// Self-test command. Usage:
    ///     _BlendkitTest                  → query "chair"
    ///     _BlendkitTest car              → query "car"
    ///
    /// What it does: opens the Blendkit panel, kicks off a search for the
    /// given query, and tells the panel to auto-download + import the first
    /// result. Progress streams to ~/blenderkit_data/client/rhino_panel.log.
    /// Lets a CI / iteration loop verify the full pipeline without manual UI
    /// clicks.
    /// </summary>
    [Guid("9a4b0e21-3c2d-4f8e-9a01-6c4f1a8b2d50")]
    public class BlendkitTestCommand : Command
    {
        public BlendkitTestCommand() { Instance = this; }
        public static BlendkitTestCommand Instance { get; private set; }
        public override string EnglishName => "BlendkitTest";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // Default exercises the model pipeline.
            BlendkitPlugIn.TestQuery = "chair";
            BlendkitPlugIn.TestAssetType = "MODEL";
            BkLog.StartSession($"BlendkitTest query=chair type=MODEL");
            BkLog.W("BlendkitTest invoked: model pipeline");
            OpenPanel();
            return Result.Success;
        }

        protected static void OpenPanel()
        {
            var id = BlendkitPanel.PanelId;
            if (!Panels.IsPanelVisible(id)) Panels.OpenPanel(id);
            // If the panel was already open the constructor doesn't re-run,
            // so the auto-search path in there doesn't fire. Hand the test
            // params straight to the live instance instead.
            if (BlendkitPanel.ActiveInstance != null
                && !string.IsNullOrEmpty(BlendkitPlugIn.TestQuery))
            {
                BlendkitPanel.ActiveInstance.TriggerTestSearch(
                    BlendkitPlugIn.TestQuery,
                    BlendkitPlugIn.TestAssetType);
            }
        }
    }

    /// <summary>Same as BlendkitTest but exercises the MATERIAL pipeline:
    /// downloads a wood material, runs the .blend → JSON extractor, and
    /// imports as a Rhino PBR material.</summary>
    [Guid("e2f5b430-7a90-4a51-902d-1f0c61d3a812")]
    public class BlendkitTestMaterialCommand : BlendkitTestCommand
    {
        public override string EnglishName => "BlendkitTestMaterial";
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            BlendkitPlugIn.TestQuery = "wood";
            BlendkitPlugIn.TestAssetType = "MATERIAL";
            BkLog.StartSession("BlendkitTestMaterial query=wood");
            BkLog.W("BlendkitTestMaterial invoked: material pipeline");
            OpenPanel();
            return Result.Success;
        }
    }

    /// <summary>HDR pipeline smoke test — first hit becomes the doc's
    /// render background.</summary>
    [Guid("c3d8a210-4e6b-4f79-9b1e-5a07f12c8e44")]
    public class BlendkitTestHdrCommand : BlendkitTestCommand
    {
        public override string EnglishName => "BlendkitTestHdr";
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            BlendkitPlugIn.TestQuery = "sky";
            BlendkitPlugIn.TestAssetType = "HDR";
            BkLog.StartSession("BlendkitTestHdr query=sky");
            BkLog.W("BlendkitTestHdr invoked: HDR pipeline");
            OpenPanel();
            return Result.Success;
        }
    }
}
