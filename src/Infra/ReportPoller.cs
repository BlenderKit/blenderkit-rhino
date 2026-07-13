using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rhino;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Poll the Go client's /report endpoint on a background task and dispatch
    /// each returned task to a subscriber callback on the UI thread.
    ///
    /// Mirrors blenderkit/timer.py in the Blender addon.
    /// </summary>
    public class ReportPoller : IDisposable
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly int _appId;
        // Func, not string. The panel rotates _apiKey on token_refresh
        // (handled in HandleTokenRefreshTask) and we want each /report
        // POST to carry whatever the current key is, not whichever value
        // was in scope when the poller was constructed at panel-load
        // time. Cheap to call — just a field read on the panel.
        private readonly Func<string> _apiKeyProvider;
        private readonly string _addonVersion;
        private readonly Action<JsonElement> _onTask;
        private Task _task;

        public ReportPoller(int appId, Func<string> apiKeyProvider, string addonVersion, Action<JsonElement> onTask)
        {
            _appId = appId;
            _apiKeyProvider = apiKeyProvider ?? (() => "");
            _addonVersion = addonVersion;
            _onTask = onTask;
        }

        public void Start() => _task = Task.Run(LoopAsync);

        private async Task LoopAsync()
        {
            var ct = _cts.Token;
            int iter = 0;
            // Consecutive failures observed while the cached port is set.
            // Used to trigger a respawn after the client appears to have
            // died (e.g. user killed client.exe in Task Manager). Mirrors
            // blenderkit/timer.py:report_failure_handler in the Blender
            // add-on, which calls start_blenderkit_client() once after
            // the first failure in a run.
            int consecutiveRefused = 0;
            while (!ct.IsCancellationRequested)
            {
                if (iter++ % 30 == 0) // ~ every 9 seconds
                    RhinoApp.WriteLine($"[Blendkit][poll] iter={iter} port={ClientLib.ActivePort}");

                try
                {
                    if (ClientLib.ActivePort == null)
                        await ClientLib.DiscoverPortAsync(ct);
                    if (ClientLib.ActivePort == null)
                    {
                        // No port found via discovery either — ask the
                        // plug-in to (re)spawn its own client. EnsureGoClient
                        // is idempotent + capped, so this is safe to call
                        // every loop until something answers.
                        TryRespawnClient();
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    var payload = new
                    {
                        app_id = _appId,
                        api_key = _apiKeyProvider() ?? "",
                        addon_version = _addonVersion,
                        // See SearchService comment — empty blender_version
                        // crashes the Go client's thumbnail parser.
                        blender_version = "4.2.0",
                        platform_version = "Rhino 8",
                        project_name = "",
                    };
                    var body = await ClientLib.PostJsonAsync("/report", payload, ct);
                    if (iter % 30 == 1) // sample roughly every 9s
                        RhinoApp.WriteLine($"[Blendkit][poll] body.Length={body?.Length ?? 0}, head={(body?.Length > 0 ? body.Substring(0, Math.Min(120, body.Length)) : "")}");
                    Dispatch(body);
                    // Clean tick — reset the failure counter so a future
                    // outage starts counting from zero again.
                    consecutiveRefused = 0;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Blendkit] report poll error: {ex.Message}");
                    // Connection refused / actively refused / socket
                    // closed — almost always means the Go client died.
                    // Drop the cached port so the next iteration re-runs
                    // the candidate scan; otherwise we'd retry the dead
                    // port forever and the panel would silently stop
                    // showing tasks.
                    var msg = ex.Message ?? "";
                    bool refused = msg.Contains("actively refused")
                        || msg.Contains("connection refused")
                        || msg.Contains("ECONNREFUSED")
                        || msg.Contains("No connection could be made")
                        || msg.Contains("target machine actively refused");
                    if (refused)
                    {
                        ClientLib.InvalidatePort();
                        consecutiveRefused++;
                        // Three failures back-to-back at 300ms cadence
                        // ≈ 1 second of dead client. Respawn on the
                        // *third* so a transient blip doesn't spam
                        // Process.Start, but a real death gets recovered
                        // before the user's first action notices.
                        if (consecutiveRefused == 3) TryRespawnClient();
                    }
                }
                try { await Task.Delay(300, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// Ask the plug-in to re-spawn the Go client. Runs on a background
        /// task so the poll loop isn't blocked by the 5-second wait inside
        /// EnsureGoClient. EnsureGoClient handles its own respawn cap and
        /// concurrency lock, so this is safe to call repeatedly.
        /// </summary>
        private static void TryRespawnClient()
        {
            var plugin = BlendkitPlugIn.Instance;
            if (plugin == null) return;
            _ = Task.Run(() =>
            {
                try { plugin.EnsureGoClient(); }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Blendkit] respawn attempt failed: {ex.Message}");
                }
            });
        }

        private void Dispatch(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Blendkit][dispatch] parse error: {ex.Message}");
                return;
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                RhinoApp.WriteLine($"[Blendkit][dispatch] not an array, kind={doc.RootElement.ValueKind}");
                doc.Dispose();
                return;
            }

            int count = 0;
            foreach (var task in doc.RootElement.EnumerateArray())
            {
                count++;
                var snapshot = task.Clone();
                // Don't wrap in any UI dispatcher here — handlers do their own
                // marshaling. Wrapping in Application.Instance.AsyncInvoke
                // silently swallowed delegates inside Rhino's WPF Eto host.
                try { _onTask?.Invoke(snapshot); }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Blendkit][onTask] threw: {ex}");
                }
            }
            doc.Dispose();
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _task?.Wait(1500); } catch { }
            _cts.Dispose();
        }
    }
}
