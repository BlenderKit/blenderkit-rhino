using System.IO;
using System.Linq;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Find a PRX / PRXC sidecar for a freshly-imported asset.
    ///
    /// Blendkit drops the asset's downloads (.glb, .blend, .prxc, …)
    /// into a per-asset directory like
    /// <c>blenderkit_data/models/&lt;asset-base-id-prefix&gt;_&lt;uuid&gt;/</c>.
    /// Each file has its own UUID stem — the .glb and the .prxc do NOT
    /// share a stem (verified with the live cache: a "flower-hp" asset
    /// landed a .glb stemmed <c>flower-hp_ab834807-…</c> and a .prxc
    /// stemmed <c>6bdcc5fb-…</c>, both in the same folder). So the
    /// stem-match probe never hits in practice. Strategy instead:
    ///
    ///   1. Try the stem-match exact path first (cheap, would catch a
    ///      future renaming-convention change to "alongside" semantics).
    ///   2. Otherwise scan the directory for ANY *.prxc; if exactly one
    ///      exists, adopt it (asset folder owns one proxor per asset).
    ///   3. Fall through to the .prx text variant on the same logic.
    ///
    /// Returns null when no candidate exists. The import path falls
    /// back to Mesh.Reduce-based decimation in that case.
    /// </summary>
    public static class ProxorLocator
    {
        /// <summary>
        /// Probe for a PRX/PRXC sidecar near <paramref name="sourcePath"/>.
        /// See class doc for the lookup rules.
        /// </summary>
        public static string FindForSourcePath(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return null;
            string dir = Path.GetDirectoryName(sourcePath);
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

            // (1) Exact stem match — fast happy path if naming is ever
            // aligned (and matches the way we'd describe the contract
            // in docs). Returns immediately.
            if (!string.IsNullOrEmpty(stem))
            {
                var prxc = Path.Combine(dir, stem + ".prxc");
                if (File.Exists(prxc)) return prxc;
                var prx = Path.Combine(dir, stem + ".prx");
                if (File.Exists(prx)) return prx;
            }

            // (2) Single-sibling-in-folder fallback. Blendkit's asset
            // download dir holds the per-asset files; if there's just
            // one .prxc, it's unambiguous. Prefer .prxc over .prx.
            // .EnumerateFiles is cheap — directories are small.
            var prxcs = Directory.EnumerateFiles(dir, "*.prxc").ToList();
            if (prxcs.Count == 1) return prxcs[0];
            if (prxcs.Count > 1)
            {
                // More than one .prxc — pick the newest by mtime as a
                // tie-breaker so subsequent variants ship-wins. Logs
                // upstream will note this so the user can tell.
                return prxcs.OrderByDescending(p => File.GetLastWriteTimeUtc(p)).First();
            }
            var prxs = Directory.EnumerateFiles(dir, "*.prx").ToList();
            if (prxs.Count == 1) return prxs[0];
            if (prxs.Count > 1)
                return prxs.OrderByDescending(p => File.GetLastWriteTimeUtc(p)).First();

            return null;
        }
    }
}
