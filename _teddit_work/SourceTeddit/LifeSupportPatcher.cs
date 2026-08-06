using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Data;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.ObjectInfoElements;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;

namespace Teddit
{
    internal static class LifeSupportPatcher
    {
        enum OutputTarget
        {
            Deposit,
            Storage
        }

        sealed class RuntimeByproduct
        {
            public ResourceDefinition Resource;
            public double Rate;
            public OutputTarget Target;
            public RowResourcesData.EResourceState State;
        }

        struct LifeSupportState
        {
            public double SupplyBefore;
            public ResourceDefinition SupplyResource;
        }

        static readonly List<RuntimeByproduct> _byproducts = new List<RuntimeByproduct>();
        static readonly FieldInfo _supplyResourceField = typeof(ObjectInfoData).GetField("supplyResource", BindingFlags.NonPublic | BindingFlags.Instance);
        static bool _enabled;
        static bool _hardDisabled;

        internal static void ResetConfig()
        {
            _enabled = false;
            _hardDisabled = false;
            _byproducts.Clear();
        }

        internal static void MergeConfig(LifeSupportConfig config, string label)
        {
            if (_hardDisabled)
            {
                Plugin.Log.LogInfo($"[LifeSupportPatcher:{label}] Skipped because the patcher is hard-disabled.");
                return;
            }

            if (config == null)
                return;

            if (config.HasSection && !config.Enabled)
            {
                _enabled = false;
                _hardDisabled = true;
                _byproducts.Clear();
                Plugin.Log.LogInfo($"[LifeSupportPatcher:{label}] Hard-disabled by config.");
                return;
            }

            if (!config.Enabled)
            {
                Plugin.Log.LogInfo($"[LifeSupportPatcher:{label}] Disabled.");
                return;
            }

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null)
            {
                Plugin.Log.LogError($"[LifeSupportPatcher:{label}] AllScriptableObjectManager is null.");
                return;
            }

            _enabled = true;

            foreach (var entry in config.Byproducts)
            {
                var resource = allSO.AllResourceDefinitions.GetByID(entry.ResourceId);
                if (resource == null)
                {
                    Plugin.Log.LogWarning($"[LifeSupportPatcher:{label}] Unknown resource '{entry.ResourceId}' in life_support.yaml.");
                    continue;
                }

                if (!Enum.TryParse(entry.Target ?? "Deposit", true, out OutputTarget target))
                {
                    Plugin.Log.LogWarning($"[LifeSupportPatcher:{label}] Unknown target '{entry.Target}' for '{entry.ResourceId}'. Use Deposit or Storage.");
                    continue;
                }

                var state = RowResourcesData.EResourceState.Solid;
                if (target == OutputTarget.Deposit && !Enum.TryParse(entry.State ?? "Solid", true, out state))
                {
                    Plugin.Log.LogWarning($"[LifeSupportPatcher:{label}] Unknown state '{entry.State}' for '{entry.ResourceId}'. Use Solid, Liquid, Gas, or Underground.");
                    continue;
                }

                _byproducts.Add(new RuntimeByproduct
                {
                    Resource = resource,
                    Rate = entry.Rate,
                    Target = target,
                    State = state
                });
            }

            Plugin.Log.LogInfo($"[LifeSupportPatcher:{label}] Active byproducts: {_byproducts.Count}");
        }

        internal static bool HasConfig => !_hardDisabled && _enabled && _byproducts.Count > 0;

        [HarmonyPatch(typeof(ObjectInfoData), "UpdateLifeSupport")]
        static class PatchObjectInfoDataUpdateLifeSupport
        {
            static void Prefix(ObjectInfoData __instance, ref LifeSupportState __state)
            {
                if (!HasConfig || _supplyResourceField == null)
                    return;

                var supplyRow = _supplyResourceField.GetValue(__instance) as RowResourcesData;
                __state.SupplyResource = supplyRow?.ResourcesType;
                __state.SupplyBefore = (__state.SupplyResource == null) ? 0.0 : __instance.CheckResources(__state.SupplyResource);
            }

            static void Postfix(ObjectInfoData __instance, LifeSupportState __state)
            {
                if (!HasConfig || __state.SupplyResource == null)
                    return;

                double supplyAfter = __instance.CheckResources(__state.SupplyResource);
                double consumed = Math.Max(0.0, __state.SupplyBefore - supplyAfter);
                if (consumed <= 0.0)
                    return;

                foreach (var byproduct in _byproducts)
                {
                    double amount = consumed * byproduct.Rate;
                    if (amount <= 0.0)
                        continue;

                    if (byproduct.Target == OutputTarget.Storage)
                    {
                        __instance.AddResources(byproduct.Resource, amount);
                        continue;
                    }

                    AddDepositOutput(__instance, byproduct, amount);
                }
            }
        }

        static void AddDepositOutput(ObjectInfoData objectInfoData, RuntimeByproduct byproduct, double amount)
        {
            var objectInfo = objectInfoData.ObjectInfo;
            if (objectInfo == null)
                return;

            var row = objectInfo.ListRowResourcesData?
                .Where(data => data.ResourcesType == byproduct.Resource && data.ResourceState == byproduct.State)
                .OrderByDescending(data => data.MiningFactor ?? 0f)
                .FirstOrDefault();

            if (row != null)
            {
                row.Value += amount;
            }
            else
            {
                objectInfo.AddDeposit(new RowResourcesData
                {
                    ResourcesType = byproduct.Resource,
                    ResourceState = byproduct.State,
                    MiningFactor = 1f,
                    resourceTypeIDSave = new ResourceDefinitionIDSave
                    {
                        id = byproduct.Resource.ID
                    },
                    Value = amount
                }, fullyExploredForAll: true);
            }
        }
    }
}
