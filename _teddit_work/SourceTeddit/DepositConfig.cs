using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Teddit
{
    /// <summary>
    /// Reads deposits.yaml from the plugin folder.
    ///
    /// Format:
    ///   MARS:
    ///     - resourceId:   id_resource_carbon
    ///       state:        Solid
    ///       amount:       50000.0
    ///       miningFactor: 0.5
    ///       explorationLevel: 1.0
    ///       preliminaryExplored: true
    ///       forcePrimary: false
    ///       overwrite:    false
    ///
    /// Body names must match ObjectInfo.ObjectName (always uppercase, e.g. "MARS", "EARTH").
    /// </summary>
    internal class DepositConfig
    {
        // key = ObjectName (uppercase body name)
        public Dictionary<string, DepositBodyConfig> Bodies { get; private set; }
            = new Dictionary<string, DepositBodyConfig>();

        public static DepositConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[DepositConfig] Not found at {path} — skipping injection.");
                return new DepositConfig();
            }

            var config = new DepositConfig();
            var raw = YamlHelper.LoadRawMap(path);
            if (raw == null) return config;

            foreach (var kv in raw)
            {
                string key = kv.Key?.ToString() ?? "";
                if (key.StartsWith("_")) continue;

                var body = ParseBodyConfig(YamlHelper.ToJToken(kv.Value));
                if (body != null)
                    config.Bodies[key.ToUpperInvariant()] = body;
            }

            Plugin.Log.LogInfo($"[DepositConfig] Loaded {config.Bodies.Count} body overrides.");
            return config;
        }

        public DepositBodyConfig GetBodyConfigFor(string objectName)
        {
            // ObjectName is always uppercase; try exact match first, then case-insensitive
            if (Bodies.TryGetValue(objectName, out var list))
                return list;
            foreach (var kv in Bodies)
                if (kv.Key.Equals(objectName, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        static DepositBodyConfig ParseBodyConfig(JToken token)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.Array)
            {
                var entries = token.ToObject<List<DepositEntry>>();
                if (entries == null || entries.Count == 0)
                    return null;

                return new DepositBodyConfig
                {
                    Overwrite = entries.Exists(e => e != null && e.Overwrite),
                    UnsafeOverwrite = entries.Exists(e => e != null && e.UnsafeOverwrite),
                    Deposits = entries
                };
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var entriesTok = obj["deposits"];
                var entries = entriesTok != null && entriesTok.Type == JTokenType.Array
                    ? entriesTok.ToObject<List<DepositEntry>>()
                    : new List<DepositEntry>();

                return new DepositBodyConfig
                {
                    Overwrite = obj["overwrite"]?.Value<bool>() ?? false,
                    UnsafeOverwrite = obj["unsafeOverwrite"]?.Value<bool>() ?? false,
                    Deposits = entries ?? new List<DepositEntry>()
                };
            }

            return null;
        }
    }

    internal sealed class DepositBodyConfig
    {
        public bool Overwrite { get; set; }
        public bool UnsafeOverwrite { get; set; }
        public List<DepositEntry> Deposits { get; set; } = new List<DepositEntry>();
    }

    internal class DepositEntry
    {
        [JsonProperty("resourceId")]   public string ResourceId   { get; set; }
        [JsonProperty("state")]        public string State        { get; set; } = "Solid";
        [JsonProperty("amount")]       public double Amount       { get; set; }
        [JsonProperty("miningFactor")] public float? MiningFactor { get; set; }
        [JsonProperty("explorationLevel")] public double? ExplorationLevel { get; set; }
        [JsonProperty("preliminaryExplored")] public bool? PreliminaryExplored { get; set; }
        [JsonProperty("forcePrimary")] public bool   ForcePrimary { get; set; }
        [JsonProperty("overwrite")]    public bool   Overwrite    { get; set; }
        [JsonProperty("unsafeOverwrite")] public bool UnsafeOverwrite { get; set; }
    }
}
