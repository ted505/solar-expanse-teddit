using System;
using System.Collections.Generic;
using System.IO;

namespace Teddit
{
    internal sealed class RootPatchSettings
    {
        public bool ResourcesEnabled = true;
        public bool CompaniesEnabled = true;
        public bool FacilitiesEnabled = true;
        public bool SpacecraftEnabled = true;
        public bool LaunchVehiclesEnabled = true;
        public bool ResearchEnabled = true;
        public bool DepositsEnabled = true;
        public bool BodiesEnabled = true;
        public bool LifeSupportEnabled = false;
        public bool FacilityPlacementsEnabled = true;
        public bool StartingResourcesEnabled = true;
        public bool ContractsEnabled = true;

        public static RootPatchSettings Load(string pluginDir)
        {
            var settings = new RootPatchSettings
            {
                ResourcesEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "resources.yaml"), true),
                CompaniesEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "companies.yaml"), true),
                FacilitiesEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "facilities.yaml"), true),
                SpacecraftEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "spacecraft.yaml"), true),
                LaunchVehiclesEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "launch_vehicles.yaml"), true),
                ResearchEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "research.yaml"), true),
                DepositsEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "deposits.yaml"), true),
                BodiesEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "bodies.yaml"), true),
                LifeSupportEnabled = ReadLifeSupportEnabled(Path.Combine(pluginDir, "life_support.yaml"), false),
                FacilityPlacementsEnabled = ReadTopLevelEnabled(Path.Combine(pluginDir, "facility_placements.yaml"), true),
                StartingResourcesEnabled  = ReadTopLevelEnabled(Path.Combine(pluginDir, "starting_resources.yaml"), true),
                ContractsEnabled          = ReadTopLevelEnabled(Path.Combine(pluginDir, "contracts.yaml"), true)
            };

            Plugin.Log.LogInfo(
                $"[RootPatchSettings] resources={settings.ResourcesEnabled}, companies={settings.CompaniesEnabled}, facilities={settings.FacilitiesEnabled}, " +
                $"spacecraft={settings.SpacecraftEnabled}, launchVehicles={settings.LaunchVehiclesEnabled}, " +
                $"research={settings.ResearchEnabled}, deposits={settings.DepositsEnabled}, " +
                $"bodies={settings.BodiesEnabled}, lifeSupport={settings.LifeSupportEnabled}, " +
                $"facilityPlacements={settings.FacilityPlacementsEnabled}, " +
                $"startingResources={settings.StartingResourcesEnabled}, " +
                $"contracts={settings.ContractsEnabled}");

            return settings;
        }

        static bool ReadTopLevelEnabled(string path, bool defaultValue)
        {
            try
            {
                var raw = YamlHelper.LoadRawMap(path);
                if (raw == null)
                    return defaultValue;

                if (!TryGetBool(raw, "enabled", out var enabled))
                    return defaultValue;

                return enabled;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RootPatchSettings] Failed to parse {Path.GetFileName(path)}: {ex.Message}");
                return defaultValue;
            }
        }

        static bool ReadLifeSupportEnabled(string path, bool defaultValue)
        {
            try
            {
                var raw = YamlHelper.LoadRawMap(path);
                if (raw == null)
                    return defaultValue;

                if (TryGetBool(raw, "enabled", out var topLevelEnabled))
                    return topLevelEnabled;

                if (TryGetNestedBool(raw, "humanLifeSupport", "enabled", out var nestedEnabled))
                    return nestedEnabled;

                return defaultValue;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RootPatchSettings] Failed to parse {Path.GetFileName(path)}: {ex.Message}");
                return defaultValue;
            }
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

        static bool TryGetNestedBool(Dictionary<object, object> raw, string outerKey, string innerKey, out bool value)
        {
            value = false;
            foreach (var kv in raw)
            {
                if (!string.Equals(kv.Key?.ToString(), outerKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!(kv.Value is Dictionary<object, object> nested))
                    return false;

                return TryGetBool(nested, innerKey, out value);
            }

            return false;
        }
    }
}
