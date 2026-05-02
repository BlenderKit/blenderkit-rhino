using System;
using System.IO;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Local Blender install discovery. Used both as a UI precheck
    /// (don't bother starting a conversion when Blender isn't installed)
    /// and to populate the `blender_exe_path` field on
    /// /run_blender_script requests — the Go client trusts whatever
    /// path the host sends instead of doing its own platform-specific
    /// install-path scan.
    ///
    /// Strategy:
    ///   1. Discover blender.exe in standard install locations + PATH.
    ///   2. Pick the highest-versioned Foundation install when several exist.
    /// </summary>
    public static class BlenderService
    {
        private static string _cachedExe;

        /// <summary>
        /// Locate a Blender executable. Returns null if none found. Result is
        /// cached for the session — call <see cref="Reset"/> to re-scan.
        /// </summary>
        public static string FindBlenderExe()
        {
            if (_cachedExe != null && File.Exists(_cachedExe)) return _cachedExe;

            // macOS: try the canonical app-bundle location first.
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX))
            {
                var mac = "/Applications/Blender.app/Contents/MacOS/Blender";
                if (File.Exists(mac)) { _cachedExe = mac; return mac; }
                // User installed under their account.
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var userApp = Path.Combine(home, "Applications/Blender.app/Contents/MacOS/Blender");
                if (File.Exists(userApp)) { _cachedExe = userApp; return userApp; }
            }

            var roots = new[]
            {
                @"C:\Program Files\Blender Foundation",
                @"C:\Program Files (x86)\Blender Foundation",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Programs\Blender Foundation"),
                @"C:\Program Files (x86)\Steam\steamapps\common\Blender",
            };

            string best = null;
            Version bestVer = new Version(0, 0);
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                // Direct hit (Steam pattern)
                var direct = Path.Combine(root, "blender.exe");
                if (File.Exists(direct))
                {
                    if (best == null) best = direct;
                }
                // Foundation pattern: ...\Blender 4.1\blender.exe
                foreach (var sub in Directory.EnumerateDirectories(root, "Blender *"))
                {
                    var exe = Path.Combine(sub, "blender.exe");
                    if (!File.Exists(exe)) continue;
                    var v = ParseVersion(Path.GetFileName(sub));
                    if (v == null) continue;
                    if (v > bestVer) { bestVer = v; best = exe; }
                }
            }

            if (best == null)
            {
                // Last resort: PATH lookup. blender vs blender.exe by platform.
                var binary = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows) ? "blender.exe" : "blender";
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    try
                    {
                        var exe = Path.Combine(dir, binary);
                        if (File.Exists(exe)) { best = exe; break; }
                    }
                    catch { }
                }
            }

            _cachedExe = best;
            return best;
        }

        public static void Reset() => _cachedExe = null;

        private static Version ParseVersion(string s)
        {
            // "Blender 4.1" → 4.1
            var parts = s.Split(' ');
            if (parts.Length < 2) return null;
            return Version.TryParse(parts[1], out var v) ? v : null;
        }
    }
}
