using System.Reflection;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

// Plugin GUID. Keep stable across builds — Rhino uses it to identify this
// plugin in the registered plug-ins list. Changing it means every user has
// to re-install.
//
// Regenerated once for the Blendkit rebrand (was 3f1c9d20-…, shared with the
// old BlenderKit package) so Blendkit is a distinct plugin and doesn't
// collide with a prior BlenderKit registration. Must match the [Guid] on
// BlendkitPlugIn.
[assembly: Guid("0a97f7d3-b53f-44a5-965e-bd49b688577a")]

// Optional Rhino-visible metadata.
[assembly: PlugInDescription(DescriptionType.Organization, "Blendkit")]
[assembly: PlugInDescription(DescriptionType.Email, "info@blendkit.com")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://www.blendkit.com")]
[assembly: PlugInDescription(DescriptionType.UpdateUrl, "https://www.blendkit.com")]

// Title / Description / Company / Product are emitted automatically by
// MSBuild from the csproj's <Title>, <Description>, <Company>, <Product>
// properties — do not duplicate them here or the build fails with CS0579.
