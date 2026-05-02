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
        private readonly string _apiKey;
        private readonly string _addonVersion;
        private readonly Action<JsonElement> _onTask;
        private Task _task;

        public ReportPoller(int appId, string apiKey, string addonVersion, Action<JsonElement> onTask)
        {
            _appId = appId;
            _apiKey = apiKey ?? "";
            _addonVersion = addonVersion;
            _onTask = onTask;
        }

        public void Start() => _task = Task.Run(LoopAsync);

        private async Task LoopAsync()
        {
            var ct = _cts.Token;
            int iter = 0;
            while (!ct.IsCancellationRequested)
            {
                if (iter++ % 30 == 0) // ~ every 9 seconds
                    RhinoApp.WriteLine($"[BlenderKit][poll] iter={iter} port={ClientLib.ActivePort}");

                try
                {
                    if (ClientLib.ActivePort == null)
                        await ClientLib.DiscoverPortAsync(ct);
                    if (ClientLib.ActivePort == null)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    var payload = new
                    {
                        app_id = _appId,
                        api_key = _apiKey,
                        addon_version = _addonVersion,
                        // See SearchService comment — empty blender_version
                        // crashes the Go client's thumbnail parser.
                        blender_version = "4.2.0",
                        platform_version = "Rhino 8",
                        project_name = "",
                    };
                    var body = await ClientLib.PostJsonAsync("/report", payload, ct);
                    if (iter % 30 == 1) // sample roughly every 9s
                        RhinoApp.WriteLine($"[BlenderKit][poll] body.Length={body?.Length ?? 0}, head={(body?.Length > 0 ? body.Substring(0, Math.Min(120, body.Length)) : "")}");
                    Dispatch(body);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[BlenderKit] report poll error: {ex.Message}");
                    // Connection refused / actively refused / socket
                    // closed — almost always means the Go client died.
                    // Drop the cached port so the next iteration re-runs
                    // the candidate scan; otherwise we'd retry the dead
                    // port forever and the panel would silently stop
                    // showing tasks.
                    var msg = ex.Message ?? "";
                    if (msg.Contains("actively refused")
                        || msg.Contains("connection refused")
                        || msg.Contains("ECONNREFUSED")
                        || msg.Contains("No connection could be made")
                        || msg.Contains("target machine actively refused"))
                    {
                        ClientLib.InvalidatePort();
                    }
                }
                try { await Task.Delay(300, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void Dispatch(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[BlenderKit][dispatch] parse error: {ex.Message}");
                return;
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                RhinoApp.WriteLine($"[BlenderKit][dispatch] not an array, kind={doc.RootElement.ValueKind}");
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
                    RhinoApp.WriteLine($"[BlenderKit][onTask] threw: {ex}");
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
