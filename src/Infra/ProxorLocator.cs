using System.IO;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Find a PRX / PRXC sidecar for a freshly-imported asset.
    ///
    /// The BlenderKit add-on (when it ships proxor data for an asset)
    /// drops the file alongside the asset's primary download — same
    /// directory, same stem, <c>.prxc</c> or <c>.prx</c> extension.
    /// This locator does just that lookup; it does NOT download anything.
    /// If no sidecar exists locally, the import path falls back to
    /// Mesh.Reduce-based decimation (see BlendkitPanel.StampBlenderKitMetadata).
    ///
    /// Two-extension probe so we accept both the binary (.prxc) and the
    /// human-readable text (.prx) variants. .prxc wins when both exist —
    /// quantized binary is the canonical shipped format.
    /// </summary>
    public static class ProxorLocator
    {
        /// <summary>
        /// Probe for a PRX/PRXC sidecar next to <paramref name="sourcePath"/>.
        /// Returns the absolute path of the first match, or null when
        /// neither variant exists / the input is empty.
        /// </summary>
        public static string FindForSourcePath(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return null;
            string dir = Path.GetDirectoryName(sourcePath);
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return null;

            // .prxc first — quantized binary, smaller on disk, canonical.
            var prxc = Path.Combine(dir, stem + ".prxc");
            if (File.Exists(prxc)) return prxc;

            var prx = Path.Combine(dir, stem + ".prx");
            if (File.Exists(prx)) return prx;

            return null;
        }
    }
}
