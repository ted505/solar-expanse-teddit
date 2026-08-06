using System;
using System.Collections.Generic;
using System.IO;

namespace Teddit
{
    internal sealed class GameplayPatchSettings
    {
        public bool Enabled = true;
        public bool ConstructionRespectsEfficiency = true;
        public bool EnergyModuleConsumeFuel = true;

        public static GameplayPatchSettings Load(string pluginDir)
        {
            var settings = new GameplayPatchSettings();
            string path = Path.Combine(pluginDir, "gameplay_patches.yaml");

            try
            {
                var raw = YamlHelper.LoadRawMap(path);
                if (raw == null)
                {
                    Plugin.Log.LogInfo("[GameplayPatchSettings] gameplay_patches.yaml not found — using defaults.");
                    return settings;
                }

                if (TryGetBool(raw, "enabled", out var enabled))
                    settings.Enabled = enabled;

                if (settings.Enabled && TryGetBool(raw, "constructionRespectsEfficiency", out var cre))
                    settings.ConstructionRespectsEfficiency = cre;

                if (settings.Enabled && TryGetBool(raw, "energyModuleConsumeFuel", out var emcf))
                    settings.EnergyModuleConsumeFuel = emcf;

                if (!settings.Enabled)
                {
                    settings.ConstructionRespectsEfficiency = false;
                    settings.EnergyModuleConsumeFuel = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameplayPatchSettings] Failed to parse gameplay_patches.yaml: {ex.Message}");
            }

            Plugin.Log.LogInfo(
                $"[GameplayPatchSettings] enabled={settings.Enabled}, " +
                $"constructionRespectsEfficiency={settings.ConstructionRespectsEfficiency}, " +
                $"energyModuleConsumeFuel={settings.EnergyModuleConsumeFuel}");

            return settings;
        }

        static bool TryGetBool(Dictionary<object, object> raw, string key, out bool value)
        {
            value = false;
            foreach (var kv in raw)
            {
                if (!string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kv.Value is bool boolValue)
                {
                    value = boolValue;
                    return true;
                }

                if (kv.Value != null && bool.TryParse(kv.Value.ToString(), out bool parsed))
                {
                    value = parsed;
                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
