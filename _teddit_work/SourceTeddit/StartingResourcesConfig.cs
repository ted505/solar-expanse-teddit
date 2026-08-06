using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Teddit
{
    /// <summary>
    /// Reads starting_resources.yaml from a mod folder. Sets per-(body, company)
    /// stockpiles on game start. Like facility_placements, additive on top of vanilla
    /// initial state and gated on new-game vs save-load via `unsafeOverride`.
    /// Companies must exist in GameManager.Companies for the current run; entries
    /// targeting companies disabled on the start-game screen are skipped.
    ///
    /// Format:
    ///   enabled: true
    ///   EARTH:
    ///     - company: world_government
    ///       unsafeOverride: false           # optional, default false
    ///       mode: set                       # optional: set (default) or add
    ///       resources:
    ///         id_resource_metal: 1000000
    ///         id_resource_steel: 750000
    ///     - company: id_company_spacey
    ///       resources:
    ///         id_resource_fuel: 5000
    /// </summary>
    internal sealed class StartingResourcesConfig
    {
        public Dictionary<string, List<StartingResourcesEntry>> Bodies { get; private set; }
            = new Dictionary<string, List<StartingResourcesEntry>>();

        public static StartingResourcesConfig Load(string path)
        {
            var config = new StartingResourcesConfig();
            if (!File.Exists(path))
                return config;

            var raw = YamlHelper.LoadRawMap(path);
            if (raw == null) return config;

            foreach (var kv in raw)
            {
                string key = kv.Key?.ToString() ?? "";
                if (key.StartsWith("_") || key.Equals("enabled", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var tok = YamlHelper.ToJToken(kv.Value);
                if (tok == null || tok.Type != JTokenType.Array) continue;

                var entries = tok.ToObject<List<StartingResourcesEntry>>();
                if (entries == null || entries.Count == 0) continue;

                config.Bodies[key.ToUpperInvariant()] = entries;
            }

            Plugin.Log.LogInfo($"[StartingResourcesConfig] Loaded starting-resource maps for {config.Bodies.Count} bodies from {Path.GetFileName(path)}.");
            return config;
        }
    }

    internal sealed class StartingResourcesEntry
    {
        [JsonProperty("company")]        public string                       Company        { get; set; }
        [JsonProperty("unsafeOverride")] public bool                         UnsafeOverride { get; set; }
        /// <summary>
        /// If "set" (default), overwrite the stockpile to exactly this value. If
        /// "add", increment current stockpile by this value (so a pre-existing
        /// vanilla stockpile is preserved and ours stacks on top).
        /// </summary>
        [JsonProperty("mode")]           public string                       Mode           { get; set; } = "set";
        [JsonProperty("resources")]      public Dictionary<string, double>   Resources      { get; set; }
    }
}
