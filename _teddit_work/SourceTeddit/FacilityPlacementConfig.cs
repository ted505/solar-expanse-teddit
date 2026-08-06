using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Teddit
{
    /// <summary>
    /// Reads facility_placements.yaml from a mod folder.
    ///
    /// Format:
    ///   enabled: true
    ///   MARS:
    ///     - company: id_company_spacey       # or 'world_government' for the WG company
    ///       facility: id_facility_solar_panel
    ///       count: 2                          # default 1
    ///       unsafeOverride: false             # default false (new-game only)
    ///
    /// Body keys must match ObjectInfo.ObjectName (uppercase, e.g. "MARS", "EARTH").
    /// Company must exist in GameManager.Companies for the current run. If that
    /// company was disabled on the start-game screen, the entry is skipped.
    /// </summary>
    internal sealed class FacilityPlacementConfig
    {
        public Dictionary<string, List<FacilityPlacementEntry>> Bodies { get; private set; }
            = new Dictionary<string, List<FacilityPlacementEntry>>();

        public static FacilityPlacementConfig Load(string path)
        {
            var config = new FacilityPlacementConfig();
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

                var entries = tok.ToObject<List<FacilityPlacementEntry>>();
                if (entries == null || entries.Count == 0) continue;

                config.Bodies[key.ToUpperInvariant()] = entries;
            }

            Plugin.Log.LogInfo($"[FacilityPlacementConfig] Loaded placements for {config.Bodies.Count} bodies from {Path.GetFileName(path)}.");
            return config;
        }
    }

    internal sealed class FacilityPlacementEntry
    {
        [JsonProperty("company")]         public string Company        { get; set; }
        [JsonProperty("facility")]        public string Facility       { get; set; }
        [JsonProperty("count")]           public int    Count          { get; set; } = 1;
        [JsonProperty("unsafeOverride")]  public bool   UnsafeOverride { get; set; }

        /// <summary>
        /// If non-null, this entry is a clear directive: scrap all of `Clear` company's
        /// existing facilities on the current body before processing later entries.
        /// `company`/`facility` are ignored on clear entries.
        /// </summary>
        [JsonProperty("clear")]           public string Clear          { get; set; }
    }
}
