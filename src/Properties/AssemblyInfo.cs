using System.Reflection;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

// Plugin GUID. Keep stable across builds — Rhino uses it to identify this
// plugin in the registered plug-ins list. Changing it means every user has
// to re-install.
[assembly: Guid("3f1c9d20-2e6b-4a0c-9d5f-1b7a2e4d4f01")]

// Optional Rhino-visible metadata.
[assembly: PlugInDescription(DescriptionType.Organization, "Blendkit")]
[assembly: PlugInDescription(DescriptionType.Email, "info@blendkit.com")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://www.blendkit.com")]
[assembly: PlugInDescription(DescriptionType.UpdateUrl, "https://www.blendkit.com")]

// Title / Description / Company / Product are emitted automatically by
// MSBuild from the csproj's <Title>, <Description>, <Company>, <Product>
// properties — do not duplicate them here or the build fails with CS0579.
