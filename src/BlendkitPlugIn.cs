using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;
using Blendkit.Rhino.Infra;

namespace Blendkit.Rhino
{
    /// <summary>
    /// Blendkit for Rhino 8 — plugin entry point.
    /// Registers the dockable panel and ensures a Go client is running.
    /// </summary>
    // Plugin identity. Regenerated for the Blendkit rebrand so this is a
    // DISTINCT Rhino plugin from the old "BlenderKit" package (which shared
    // the previous GUID 3f1c9d20-…). Rhino keys plugins + their command
    // registrations by this GUID; reusing the old one made the new Blendkit
    // package collide with any prior BlenderKit registration (and with the
    // deploy_rhino.bat dev-shadow), which manifested as "Unknown command:
    // Blendkit" after a Package Manager install. Must stay in lockstep with
    // the [assembly: Guid] in Properties/AssemblyInfo.cs.
    [Guid("0a97f7d3-b53f-44a5-965e-bd49b688577a")]
    public class BlendkitPlugIn : PlugIn
    {
        public BlendkitPlugIn() { Instance = this; }
        public static BlendkitPlugIn Instance { get; private set; }

        /// <summary>
        /// Singleton display conduit that swaps decimated proxies in for
        /// imported meshes in non-rendered viewport modes. Lifecycle is
        /// tied to OnLoad/OnShutdown — Active toggles via the
        /// BlendkitProxy command without losing the cached proxies.
        /// See <see cref="Infra.ProxyDisplayConduit"/> for the draw logic.
        /// </summary>
        public Infra.ProxyDisplayConduit ProxyConduit { get; private set; }

        // Self-driving test hook. When TestQuery is non-empty, the panel
        // switches to TestAssetType (default MODEL), runs that query on
        // first open, and auto-downloads + imports the first result.
        public static string TestQuery;
        public static string TestAssetType = "MODEL";

        private Process _clientProcess;
        private bool _clientSpawned; // we should only kill what we spawned

        // Match Blender addon's default on Windows so the asset cache is shared.
        public static string DefaultGlobalDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "blenderkit_data");

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            try
            {
                // Bootstrap autonomous test runs from an env var. Rhino's
                // /runscript fires before plugins load, so the
                // BlendkitTest* commands invoked that way never reached
                // their RunCommand handler. Reading BLENDERKIT_AUTOTEST
                // here in OnLoad gives the same effect with a guarantee
                // that the panel sees TestQuery on first paint.
                //   BLENDERKIT_AUTOTEST=model | material | hdr
                // Optional separate query override:
                //   BLENDERKIT_AUTOTEST_QUERY=...
                var autotest = Environment.GetEnvironmentVariable("BLENDERKIT_AUTOTEST");
                if (!string.IsNullOrEmpty(autotest))
                {
                    var t = autotest.Trim().ToUpperInvariant();
                    var (defaultQ, type) = t switch
                    {
                        "MODEL"    => ("chair", "MODEL"),
                        "MATERIAL" => ("wood",  "MATERIAL"),
                        "HDR"      => ("sky",   "HDR"),
                        _ => (autotest, "MODEL"), // free-form falls back to MODEL
                    };
                    TestQuery = Environment.GetEnvironmentVariable("BLENDERKIT_AUTOTEST_QUERY") ?? defaultQ;
                    TestAssetType = type;
                    BkLog.StartSession($"AUTOTEST env query={TestQuery} type={TestAssetType}");
                    BkLog.W($"BLENDERKIT_AUTOTEST={autotest} → query='{TestQuery}' type={TestAssetType}");
                    // Open the panel so the auto-search path runs. Wait
                    // for the Go client port AND a brief settle time so
                    // the panel's first search doesn't race with client
                    // startup (would error "port not discovered"; the
                    // autotest's first-hit auto-download then never
                    // fires).
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < 60; i++)
                        {
                            if (Infra.ClientLib.ActivePort != null) break;
                            await Task.Delay(500);
                        }
                        // A short extra wait so RegisterPanel finishes
                        // and the panel constructor's own port-wait loop
                        // doesn't double-fire searches when both the
                        // bootstrap and the panel race to OpenPanel.
                        await Task.Delay(750);
                        RhinoApp.InvokeOnUiThread((Action)(() =>
                        {
                            try { Panels.OpenPanel(BlendkitPanel.PanelId); }
                            catch (Exception ex) { RhinoApp.WriteLine($"[Blendkit] AUTOTEST OpenPanel failed: {ex.Message}"); }
                        }));
                    });
                }

                // Spin up the proxy display conduit. Inert until something
                // attaches a proxy to a RhinoObject — at which point the
                // ObjectCulling + PreDrawObjects hooks start firing for
                // that object in non-rendered viewport modes. See
                // ProxyMeshService for the cache + decimation entry points
                // and BlendkitProxy for the user-facing command.
                try
                {
                    ProxyConduit = new Infra.ProxyDisplayConduit { Enabled = true, Active = true };
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Blendkit] proxy conduit init failed: {ex.Message}");
                }

                RegisterPanel();
                // First-run toolbar load. Plugin authors who want a
                // one-click "open Blendkit panel" toolbar button
                // ship a Blendkit.rui (authored once via Tools >
                // Toolbar Layout in Rhino) next to the .rhp. We open
                // it via the scripted -Toolbar command on the FIRST
                // launch only and then drop a stamp file so the
                // command doesn't nag the user every Rhino startup.
                // RhinoCommon has no public API to programmatically
                // create a toolbar button (verified against the
                // McNeel SampleCsToolbar sample), so this .rui +
                // first-run-load pattern is the canonical workaround.
                try
                {
                    var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(asmDir))
                    {
                        var rui = Path.Combine(asmDir, "Blendkit.rui");
                        var stampDir = Path.Combine(DefaultGlobalDir, "client");
                        Directory.CreateDirectory(stampDir);
                        var stamp = Path.Combine(stampDir, "Blendkit.rui.loaded");
                        if (File.Exists(rui) && !File.Exists(stamp))
                        {
                            RhinoApp.RunScript($"_-Toolbar _Open \"{rui}\" _Enter", false);
                            try { File.WriteAllText(stamp, DateTime.UtcNow.ToString("o")); } catch { }
                            RhinoApp.WriteLine("[Blendkit] Blendkit toolbar loaded — drag the icon onto any toolbar group.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Blendkit] toolbar auto-load failed: {ex.Message}");
                }
                // Never block the UI thread during load. Go client discovery
                // can take several seconds; run it on a background task and
                // let the panel's poller and status line reflect the outcome.
                Task.Run(() =>
                {
                    try { EnsureGoClient(); }
                    catch (Exception ex) { RhinoApp.WriteLine($"[Blendkit] client boot error: {ex.Message}"); }
                });
                return LoadReturnCode.Success;
            }
            catch (Exception ex)
            {
                errorMessage = $"Blendkit failed to load: {ex.Message}";
                return LoadReturnCode.ErrorShowDialog;
            }
        }

        protected override void OnShutdown()
        {
            if (_clientSpawned) StopGoClient();
            base.OnShutdown();
        }

        private void RegisterPanel()
        {
            // Load the Blendkit logo embedded in this assembly and feed it
            // to Rhino so the panel tab + Panel Bar show our icon next to
            // (e.g.) the Grasshopper one. The PNG was embedded at build time
            // via <EmbeddedResource> in the csproj.
            System.Drawing.Icon icon = null;
            // Bitmap.GetHicon() / Icon.FromHandle are Windows-only —
            // they GP-fault on macOS/Linux .NET. Skip the Win32-icon
            // path off Windows; Rhino on macOS accepts a null icon and
            // falls back to a generic plug-in glyph (we'll switch to
            // an Eto.Drawing.Icon overload of RegisterPanel in a
            // follow-up if McNeel exposes one publicly).
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var resourceName = asm.GetName().Name + ".Resources.blenderkit_logo.png";
                    using var stream = asm.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var src = new System.Drawing.Bitmap(stream);
                        // Rhino's panel-tab strip uses ~16-24px icons; passing
                        // the raw 296x296 PNG via Bitmap.GetHicon() produces
                        // an Icon with a single huge frame that Rhino renders
                        // poorly (often a blank square). Downscale into a
                        // 32x32 surface so the tab icon is crisp at standard
                        // and HiDPI panel sizes.
                        using var scaled = new System.Drawing.Bitmap(32, 32);
                        using (var g = System.Drawing.Graphics.FromImage(scaled))
                        {
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                            g.Clear(System.Drawing.Color.Transparent);
                            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, 32, 32));
                        }
                        var hIcon = scaled.GetHicon();
                        icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Blendkit] couldn't load panel icon: {ex.Message}");
                }
            }
            // Version goes in the panel name itself so we don't waste a
            // row of panel content on a banner. Bump in lockstep with
            // the OnLoad version reported to the Go client.
            Panels.RegisterPanel(this, typeof(BlendkitPanel), "Blendkit v0.1", icon);
        }

        // Bound on auto-respawns within a single Rhino session. The poller
        // calls EnsureGoClient when the client dies; without a cap, a
        // fundamentally-broken binary (missing dependency, bad port, etc.)
        // would spin Process.Start in an infinite loop. 5 is generous —
        // expected: 0 in a healthy session, 1 if the user kills it manually.
        private const int MaxAutoRespawns = 5;
        private int _autoRespawnCount;
        private readonly object _ensureLock = new object();

        /// <summary>
        /// If a Go client is already serving on any candidate port (e.g. started
        /// by a running Blender), attach to it. Otherwise spawn our own with
        /// stdout/stderr redirected to a log file so we can diagnose failures.
        ///
        /// Public + thread-safe so the report poller can call it after it
        /// notices the client has gone away (user killed client.exe, the
        /// process crashed, etc.). Multiple concurrent callers are
        /// serialized through <see cref="_ensureLock"/>; the discover step
        /// short-circuits the second caller when the first has already
        /// brought the client back up.
        /// </summary>
        public void EnsureGoClient()
        {
            lock (_ensureLock) { EnsureGoClientLocked(); }
        }

        private void EnsureGoClientLocked()
        {
            // Step 1: try to discover an existing client.
            var existing = ClientLib.DiscoverPortAsync().GetAwaiter().GetResult();
            if (existing != null)
            {
                RhinoApp.WriteLine($"[Blendkit] Found existing Go client on port {existing}.");
                return;
            }
            if (_autoRespawnCount >= MaxAutoRespawns)
            {
                RhinoApp.WriteLine($"[Blendkit] Hit auto-respawn cap ({MaxAutoRespawns}). Restart Rhino to retry.");
                return;
            }
            _autoRespawnCount++;

            // Clean up any prior process handle (will be the case on a
            // respawn after the user killed client.exe). Process.Dispose
            // releases the OS handle + the stdout/stderr async readers.
            if (_clientProcess != null)
            {
                try
                {
                    if (!_clientProcess.HasExited) { _clientProcess.Kill(); }
                    _clientProcess.Dispose();
                }
                catch { /* best effort */ }
                _clientProcess = null;
            }

            // Step 2: spawn our own. We ship the Go client under the same
            // descriptive filename the Blendkit Blender add-on uses
            // (`blenderkit-client-<os>-<arch>(.exe)`, where os is one of
            // {windows,macos,linux} and arch is one of {x86_64,arm64}).
            // Mirrors `decide_client_binary_name()` in the add-on's
            // client_lib.py so a binary built with the add-on's dev.py
            // drops in unchanged.
            //
            // The pre-rename short names (`client.exe` on Windows, `client`
            // elsewhere) stay in the probe order as a fallback so the .rhp
            // shipped in the 0.1.2 yak — which still uses the short name —
            // keeps working after a plug-in upgrade-in-place.
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string clientExe = null;
            foreach (var name in CandidateClientBinaryNames())
            {
                var p = Path.Combine(asmDir, "client", name);
                if (File.Exists(p)) { clientExe = p; break; }
            }
            if (string.IsNullOrEmpty(clientExe))
            {
                RhinoApp.WriteLine($"[Blendkit] Go client binary missing under {Path.Combine(asmDir, "client")}.");
                return;
            }

            var logDir = Path.Combine(DefaultGlobalDir, "client");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "rhino.log");

            var args =
                "--port 62485 " +
                "--server https://www.blendkit.com " +
                "--proxy_which SYSTEM " +
                "--ssl_context ENABLED " +
                "--version 0.1.0 " +
                "--software Rhino " +
                $"--pid {Process.GetCurrentProcess().Id}";

            var psi = new ProcessStartInfo
            {
                FileName = clientExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(clientExe),
            };
            _clientProcess = Process.Start(psi);
            _clientSpawned = true;
            RhinoApp.WriteLine($"[Blendkit] Go client spawned (pid={_clientProcess?.Id}). Log: {logPath}");

            // Pipe stdout/stderr into the log file so crash causes are visible.
            var logStream = File.Open(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var writer = new StreamWriter(logStream) { AutoFlush = true };
            writer.WriteLine($"---- {DateTime.Now:O} Rhino session start, args: {args} ----");
            _clientProcess.OutputDataReceived += (s, e) => { if (e.Data != null) writer.WriteLine(e.Data); };
            _clientProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) writer.WriteLine("ERR " + e.Data); };
            _clientProcess.BeginOutputReadLine();
            _clientProcess.BeginErrorReadLine();

            // Step 3: wait for the client to come up, up to 5 seconds.
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                var port = ClientLib.DiscoverPortAsync().GetAwaiter().GetResult();
                if (port != null)
                {
                    RhinoApp.WriteLine($"[Blendkit] Go client ready on port {port}.");
                    return;
                }
                if (_clientProcess.HasExited)
                {
                    RhinoApp.WriteLine(
                        $"[Blendkit] Go client exited early (code {_clientProcess.ExitCode}). See {logPath}.");
                    return;
                }
            }
            RhinoApp.WriteLine("[Blendkit] Go client didn't answer within 5s — see log.");
        }

        private void StopGoClient()
        {
            if (_clientProcess == null || _clientProcess.HasExited) return;
            try { _clientProcess.Kill(); _clientProcess.WaitForExit(3000); }
            catch (Exception ex) { RhinoApp.WriteLine($"[Blendkit] Kill failed: {ex.Message}"); }
        }

        /// <summary>
        /// Filenames to try when locating the Go client binary inside the
        /// shipped <c>client/</c> directory. First entry matching wins.
        ///
        /// The first candidate is the addon-style descriptive name
        /// (<c>blenderkit-client-{os}-{arch}(.exe)</c>) — same convention
        /// as <c>decide_client_binary_name</c> in the Blender add-on's
        /// client_lib.py. Falling back to <c>client.exe</c>/<c>client</c>
        /// keeps the plug-in compatible with older yaks (0.1.2 and earlier)
        /// that shipped the binary under the short name.
        ///
        /// arch is read from <see cref="RuntimeInformation.OSArchitecture"/>
        /// rather than process architecture so a 32-bit Rhino on a 64-bit OS
        /// would still pick the right binary — though in practice Rhino 8
        /// is 64-bit only.
        /// </summary>
        private static IEnumerable<string> CandidateClientBinaryNames()
        {
            string os;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) os = "windows";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) os = "macos";
            else os = "linux";

            // The add-on aligns Windows' "amd64" and Linux' "aarch64" onto
            // x86_64/arm64 — replicate that here so the filenames match.
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64   => "x86_64",
                Architecture.Arm64 => "arm64",
                _                  => "x86_64",  // best-guess fallback
            };

            string ext = (os == "windows") ? ".exe" : "";
            string altArch = arch == "x86_64" ? "arm64" : "x86_64";

            // Unified name from the standalone bk_client repo's build
            // (dev.py BUILD_MATRIX → `bk_client-{os}-{arch}`). This is the
            // current convention; probe it first. Cross-arch second (a
            // Rosetta-running Intel Rhino on Apple Silicon, or vice versa).
            yield return $"bk_client-{os}-{arch}{ext}";
            yield return $"bk_client-{os}-{altArch}{ext}";

            // Legacy descriptive name from the pre-split client (shipped
            // inside the Blender add-on repo). Kept so older yaks still work.
            yield return $"blenderkit-client-{os}-{arch}{ext}";
            yield return $"blenderkit-client-{os}-{altArch}{ext}";

            // Pre-rename short names — kept for the 0.1.2 yak (and any
            // local dev workflow that still produces `client.exe` from a
            // plain `go build` rather than dev.py).
            yield return os == "windows" ? "client.exe" : "client";
        }
    }
}
