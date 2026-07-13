using System;
using System.IO;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Tee logger: writes every line both to Rhino's command-line console and
    /// to a flat file under the global asset dir. Lets an external observer
    /// (e.g. a CI loop) read what the plugin is doing without driving Rhino.
    /// </summary>
    public static class BkLog
    {
        private static readonly object _lock = new object();
        private static string _path;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    var dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "blenderkit_data", "client");
                    Directory.CreateDirectory(dir);
                    _path = System.IO.Path.Combine(dir, "rhino_panel.log");
                }
                return _path;
            }
        }

        public static void StartSession(string label)
        {
            lock (_lock)
            {
                File.AppendAllText(Path, $"\n---- {DateTime.Now:O} {label} ----\n");
            }
        }

        public static void W(string msg)
        {
            try
            {
                global::Rhino.RhinoApp.WriteLine("[Blendkit] " + msg);
            }
            catch { /* outside Rhino in tests */ }
            try
            {
                lock (_lock) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            }
            catch { }
        }
    }
}
