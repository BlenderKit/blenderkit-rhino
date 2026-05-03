using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// Plugin-wide preferences persisted as a single JSON file at
    /// %APPDATA%\BlenderKit\config.json (same file AuthService uses for the
    /// API token). Keep all keys in one file so a fresh install can be set up
    /// by copying one path; keep the schema flat so we can grow it without
    /// migrations.
    /// </summary>
    public static class Settings
    {
        public static string Path
        {
            get
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return System.IO.Path.Combine(appdata, "BlenderKit", "config.json");
            }
        }

        public static Dictionary<string, JsonElement> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new Dictionary<string, JsonElement>();
                using var doc = JsonDocument.Parse(File.ReadAllText(Path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return new Dictionary<string, JsonElement>();
                var dict = new Dictionary<string, JsonElement>();
                foreach (var p in doc.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                return dict;
            }
            catch { return new Dictionary<string, JsonElement>(); }
        }

        public static void Save(Dictionary<string, JsonElement> dict)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                using var stream = File.Create(Path);
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                foreach (var kv in dict)
                {
                    writer.WritePropertyName(kv.Key);
                    kv.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            catch { /* best-effort: persistence failure shouldn't break the panel */ }
        }

        public static string GetString(string key, string fallback = "")
        {
            var d = Load();
            if (d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? fallback;
            return fallback;
        }

        public static List<string> GetStringList(string key)
        {
            var d = Load();
            if (!d.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array)
                return new List<string>();
            var list = new List<string>();
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString());
            return list;
        }

        public static bool GetBool(string key, bool fallback = false)
        {
            var d = Load();
            if (!d.TryGetValue(key, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            // Tolerate stringly-typed legacy values ("true" / "false").
            if (v.ValueKind == JsonValueKind.String
                && bool.TryParse(v.GetString(), out var b)) return b;
            return fallback;
        }

        public static void SetBool(string key, bool value)
        {
            using var doc = JsonDocument.Parse(value ? "true" : "false");
            SetRaw(key, doc.RootElement.Clone());
        }

        public static void SetString(string key, string value) => SetRaw(key, JsonValue(value));
        public static void SetStringList(string key, IEnumerable<string> values)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(values));
            SetRaw(key, doc.RootElement.Clone());
        }

        private static JsonElement JsonValue(string s)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s));
            return doc.RootElement.Clone();
        }

        private static void SetRaw(string key, JsonElement value)
        {
            var d = Load();
            d[key] = value;
            Save(d);
        }
    }
}
