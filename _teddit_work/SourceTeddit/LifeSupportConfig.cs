using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Teddit
{
    internal sealed class LifeSupportConfig
    {
        internal sealed class ByproductEntry
        {
            public string ResourceId;
            public double Rate;
            public string Target = "Deposit";
            public string State = "Solid";
        }

        public bool HasSection { get; private set; }
        public bool Enabled { get; private set; }
        public List<ByproductEntry> Byproducts { get; } = new List<ByproductEntry>();

        public bool HasEntries => Enabled && Byproducts.Count > 0;

        public static LifeSupportConfig Load(string path, bool honorEnabled = true)
        {
            var config = new LifeSupportConfig();
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"[LifeSupportConfig] {Path.GetFileName(path)} not found - skipping.");
                return config;
            }

            try
            {
                var rawRoot = YamlHelper.LoadRawMap(path);
                var raw = YamlHelper.LoadPatch(path);
                if (!raw.TryGetValue("humanLifeSupport", out var fields))
                {
                    Plugin.Log.LogInfo($"[LifeSupportConfig] {Path.GetFileName(path)}: 0 entries.");
                    return config;
                }

                config.HasSection = true;
                if (honorEnabled)
                {
                    if (rawRoot != null && rawRoot.TryGetValue("enabled", out var topLevelTok) && topLevelTok != null)
                        config.Enabled = bool.Parse(topLevelTok.ToString());
                    else
                        config.Enabled = fields.TryGetValue("enabled", out var enabledTok) && enabledTok.Value<bool>();
                }
                else
                {
                    config.Enabled = true;
                }

                if (fields.TryGetValue("byproducts", out var byproductsTok) && byproductsTok.Type == JTokenType.Array)
                {
                    foreach (var item in (JArray)byproductsTok)
                    {
                        if (item.Type != JTokenType.Object)
                            continue;

                        var obj = (JObject)item;
                        string resourceId = obj["resource"]?.Value<string>();
                        if (string.IsNullOrWhiteSpace(resourceId))
                            continue;

                        config.Byproducts.Add(new ByproductEntry
                        {
                            ResourceId = resourceId,
                            Rate = obj["rate"]?.Value<double>() ?? 0.0,
                            Target = obj["target"]?.Value<string>() ?? "Deposit",
                            State = obj["state"]?.Value<string>() ?? "Solid"
                        });
                    }
                }

                Plugin.Log.LogInfo($"[LifeSupportConfig] {Path.GetFileName(path)}: enabled={config.Enabled}, byproducts={config.Byproducts.Count}, honorEnabled={honorEnabled}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[LifeSupportConfig] Failed to load {path}: {ex}");
            }

            return config;
        }
    }
}
