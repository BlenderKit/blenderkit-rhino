using System;
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
    /// BlenderKit for Rhino 8 — plugin entry point.
    /// Registers the dockable panel and ensures a Go client is running.
    /// </summary>
    [Guid("3f1c9d20-2e6b-4a0c-9d5f-1b7a2e4d4f01")]
    public class BlendkitPlugIn : PlugIn
    {
        public BlendkitPlugIn() { Instance = this; }
        public static BlendkitPlugIn Instance { get; private set; }

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
                // BlenderKitTest* commands invoked that way never reached
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
                            catch (Exception ex) { RhinoApp.WriteLine($"[BlenderKit] AUTOTEST OpenPanel failed: {ex.Message}"); }
                        }));
                    });
                }

                RegisterPanel();
                // First-run toolbar load. Plugin authors who want a
                // one-click "open BlenderKit panel" toolbar button
                // ship a BlenderKit.rui (authored once via Tools >
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
                        var rui = Path.Combine(asmDir, "BlenderKit.rui");
                        var stampDir = Path.Combine(DefaultGlobalDir, "client");
                        Directory.CreateDirectory(stampDir);
                        var stamp = Path.Combine(stampDir, "BlenderKit.rui.loaded");
                        if (File.Exists(rui) && !File.Exists(stamp))
                        {
                            RhinoApp.RunScript($"_-Toolbar _Open \"{rui}\" _Enter", false);
                            try { File.WriteAllText(stamp, DateTime.UtcNow.ToString("o")); } catch { }
                            RhinoApp.WriteLine("[BlenderKit] BlenderKit toolbar loaded — drag the icon onto any toolbar group.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[BlenderKit] toolbar auto-load failed: {ex.Message}");
                }
                // Never block the UI thread during load. Go client discovery
                // can take several seconds; run it on a background task and
                // let the panel's poller and status line reflect the outcome.
                Task.Run(() =>
                {
                    try { EnsureGoClient(); }
                    catch (Exception ex) { RhinoApp.WriteLine($"[BlenderKit] client boot error: {ex.Message}"); }
                });
                return LoadReturnCode.Success;
            }
            catch (Exception ex)
            {
                errorMessage = $"BlenderKit failed to load: {ex.Message}";
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
            // Load the BlenderKit logo embedded in this assembly and feed it
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
                    RhinoApp.WriteLine($"[BlenderKit] couldn't load panel icon: {ex.Message}");
                }
            }
            // Version goes in the panel name itself so we don't waste a
            // row of panel content on a banner. Bump in lockstep with
            // the OnLoad version reported to the Go client.
            Panels.RegisterPanel(this, typeof(BlendkitPanel), "BlenderKit v0.1", icon);
        }

        /// <summary>
        /// If a Go client is already serving on any candidate port (e.g. started
        /// by a running Blender), attach to it. Otherwise spawn our own with
        /// stdout/stderr redirected to a log file so we can diagnose failures.
        /// </summary>
        private void EnsureGoClient()
        {
            // Step 1: try to discover an existing client.
            var existing = ClientLib.DiscoverPortAsync().GetAwaiter().GetResult();
            if (existing != null)
            {
                RhinoApp.WriteLine($"[BlenderKit] Found existing Go client on port {existing}.");
                return;
            }

            // Step 2: spawn our own. On Windows we ship `client.exe`,
            // on macOS / Linux a plain `client` binary (no extension).
            // Probe both so the same plug-in dir works across OSes.
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            string clientExe = null;
            foreach (var name in new[] { "client.exe", "client" })
            {
                var p = Path.Combine(asmDir, "client", name);
                if (File.Exists(p)) { clientExe = p; break; }
            }
            if (string.IsNullOrEmpty(clientExe))
            {
                RhinoApp.WriteLine($"[BlenderKit] Go client binary missing under {Path.Combine(asmDir, "client")}.");
                return;
            }

            var logDir = Path.Combine(DefaultGlobalDir, "client");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "rhino.log");

            var args =
                "--port 62485 " +
                "--server https://www.blenderkit.com " +
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
            RhinoApp.WriteLine($"[BlenderKit] Go client spawned (pid={_clientProcess?.Id}). Log: {logPath}");

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
                    RhinoApp.WriteLine($"[BlenderKit] Go client ready on port {port}.");
                    return;
                }
                if (_clientProcess.HasExited)
                {
                    RhinoApp.WriteLine(
                        $"[BlenderKit] Go client exited early (code {_clientProcess.ExitCode}). See {logPath}.");
                    return;
                }
            }
            RhinoApp.WriteLine("[BlenderKit] Go client didn't answer within 5s — see log.");
        }

        private void StopGoClient()
        {
            if (_clientProcess == null || _clientProcess.HasExited) return;
            try { _clientProcess.Kill(); _clientProcess.WaitForExit(3000); }
            catch (Exception ex) { RhinoApp.WriteLine($"[BlenderKit] Kill failed: {ex.Message}"); }
        }
    }
}
