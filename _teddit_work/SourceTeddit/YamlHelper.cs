using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace Teddit
{
    /// <summary>
    /// Parses Teddit YAML files into the shapes consumed by the various config loaders.
    /// When full-document parsing fails, this helper falls back to parsing one top-level
    /// mapping block at a time so a single malformed entry does not take out the whole file.
    /// </summary>
    internal static class YamlHelper
    {
        private static readonly IDeserializer _deserializer =
            new DeserializerBuilder().Build();

        internal static Dictionary<object, object> LoadRawMap(string path)
        {
            if (!File.Exists(path))
                return null;

            string text = File.ReadAllText(path);
            try
            {
                return _deserializer.Deserialize<Dictionary<object, object>>(text);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[YamlHelper] Full parse failed for {Path.GetFileName(path)}: {ex.Message}. Falling back to best-effort block parsing.");
                return LoadRawMapBestEffort(path, text);
            }
        }

        /// <summary>
        /// Reads a YAML file and returns a flat string-keyed dictionary of field maps.
        /// Top-level keys that start with "_" are skipped.
        /// Top-level entries whose value is not a mapping are also skipped.
        /// </summary>
        public static Dictionary<string, Dictionary<string, JToken>> LoadPatch(string path)
        {
            var result = new Dictionary<string, Dictionary<string, JToken>>();
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"[YamlHelper] {Path.GetFileName(path)} not found - skipping.");
                return result;
            }

            var raw = LoadRawMap(path);
            if (raw == null)
                return result;

            foreach (var kv in raw)
            {
                string key = kv.Key?.ToString() ?? string.Empty;
                if (key.StartsWith("_", StringComparison.Ordinal))
                    continue;

                if (!(kv.Value is Dictionary<object, object> inner))
                    continue;

                var fields = new Dictionary<string, JToken>();
                foreach (var fkv in inner)
                {
                    string fkey = fkv.Key?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(fkey))
                        fields[fkey] = ToJToken(fkv.Value);
                }

                if (fields.Count > 0)
                    result[key] = fields;
            }

            return result;
        }

        /// <summary>
        /// Reads a deposits YAML file.
        /// Top-level keys map to a List&lt;object&gt; (each item is a mapping of deposit fields).
        /// Returns Dictionary&lt;string, JToken&gt; where each value is a JArray.
        /// </summary>
        public static Dictionary<string, JToken> LoadDeposits(string path)
        {
            var result = new Dictionary<string, JToken>();
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"[YamlHelper] {Path.GetFileName(path)} not found - skipping.");
                return result;
            }

            var raw = LoadRawMap(path);
            if (raw == null)
                return result;

            foreach (var kv in raw)
            {
                string key = kv.Key?.ToString() ?? string.Empty;
                if (key.StartsWith("_", StringComparison.Ordinal))
                    continue;

                if (!(kv.Value is List<object>))
                    continue;

                result[key] = ToJToken(kv.Value);
            }

            return result;
        }

        static Dictionary<object, object> LoadRawMapBestEffort(string path, string text)
        {
            var merged = new Dictionary<object, object>();
            foreach (var block in SplitTopLevelBlocks(text))
            {
                try
                {
                    var parsed = _deserializer.Deserialize<Dictionary<object, object>>(block.Content);
                    if (parsed == null)
                        continue;

                    foreach (var kv in parsed)
                        merged[kv.Key] = kv.Value;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[YamlHelper] Skipping malformed block '{block.Key}' in {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            return merged;
        }

        static IEnumerable<YamlBlock> SplitTopLevelBlocks(string text)
        {
            var blocks = new List<YamlBlock>();
            using (var reader = new StringReader(text ?? string.Empty))
            {
                string line;
                string currentKey = null;
                StringBuilder current = null;

                while ((line = reader.ReadLine()) != null)
                {
                    if (IsTopLevelKeyLine(line, out string key))
                    {
                        if (current != null && !string.IsNullOrWhiteSpace(current.ToString()))
                            blocks.Add(new YamlBlock(currentKey, current.ToString()));

                        currentKey = key;
                        current = new StringBuilder();
                    }

                    if (current != null)
                        current.AppendLine(line);
                }

                if (current != null && !string.IsNullOrWhiteSpace(current.ToString()))
                    blocks.Add(new YamlBlock(currentKey, current.ToString()));
            }

            return blocks;
        }

        static bool IsTopLevelKeyLine(string line, out string key)
        {
            key = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            if (char.IsWhiteSpace(line[0]))
                return false;

            string trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith("-", StringComparison.Ordinal))
                return false;

            int colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0)
                return false;

            key = trimmed.Substring(0, colonIndex).Trim();
            return key.Length > 0;
        }

        internal static JToken ToJToken(object value)
        {
            if (value == null)
                return JValue.CreateNull();

            if (value is Dictionary<object, object> dict)
            {
                var obj = new JObject();
                foreach (var kv in dict)
                    obj[kv.Key.ToString()] = ToJToken(kv.Value);
                return obj;
            }

            if (value is List<object> list)
            {
                var arr = new JArray();
                foreach (var item in list)
                    arr.Add(ToJToken(item));
                return arr;
            }

            return JToken.FromObject(value);
        }

        readonly struct YamlBlock
        {
            public YamlBlock(string key, string content)
            {
                Key = string.IsNullOrEmpty(key) ? "<unknown>" : key;
                Content = content ?? string.Empty;
            }

            public string Key { get; }
            public string Content { get; }
        }
    }
}
