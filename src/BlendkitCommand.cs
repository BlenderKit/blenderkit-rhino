using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace Blendkit.Rhino
{
    /// <summary>
    /// Command: `Blendkit` — toggles the dockable panel on/off.
    /// Mirrors Blender's "show/hide N-panel" for the addon.
    /// </summary>
    [Guid("4136314c-7c1b-4a5f-bef0-f263c8e5b078")]
    public class BlendkitCommand : Command
    {
        public BlendkitCommand() { Instance = this; }
        public static BlendkitCommand Instance { get; private set; }
        public override string EnglishName => "Blendkit";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var id = BlendkitPanel.PanelId;
            if (Panels.IsPanelVisible(id)) Panels.ClosePanel(id);
            else Panels.OpenPanel(id);
            return Result.Success;
        }
    }
}
