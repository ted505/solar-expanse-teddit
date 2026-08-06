using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CustomUpdate;
using Data.ScriptableObject;
using Extensions;
using Game.ObjectInfoDataScripts;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;

namespace Teddit
{
    /// <summary>
    /// Behavioral Harmony patches for game mechanics.
    /// All patches gate on static flags set at config-load time.
    /// </summary>
    static class GameplayPatches
    {
        internal static bool ConstructionRespectsEfficiency;
        internal static bool EnergyModuleConsumeFuel;

        static readonly Dictionary<string, double> _constructionSpeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        internal static void RegisterConstructionSpeed(string descriptorId, double speed)
        {
            _constructionSpeeds[descriptorId] = speed;
        }

        internal static double GetConstructionSpeed(Facility fac)
        {
            if (fac?.facilityDescriptor == null)
                return 1.0;
            double speed;
            if (_constructionSpeeds.TryGetValue(fac.facilityDescriptor.ID, out speed))
                return speed;
            return 1.0;
        }

        // ── Energy module fuel consumption helpers ──────────────────────

        static readonly FieldInfo _energyProductionField =
            AccessTools.Field(typeof(EnergyProductionModule), "energyProduction");
        static readonly FieldInfo _resourceBalanceField =
            AccessTools.Field(typeof(Facility), "resourceBalance");
        static readonly MethodInfo _generateByproductsMethod =
            AccessTools.Method(typeof(Facility), "GenerateByproducts",
                new[] { typeof(double), typeof(double), typeof(bool) });

        class NotificationState { public DateTime? LastNotification; }
        static readonly ConditionalWeakTable<EnergyProductionModule, NotificationState>
            _notificationTracker = new ConditionalWeakTable<EnergyProductionModule, NotificationState>();

        internal static double ComputeEnergyModuleResourceEfficiency(EnergyProductionModule module)
        {
            double result = 1.0;
            var input = module.facilityDescriptor.energyProductionData?.input;
            if (input == null)
                return result;

            long enabled = module.Enabled;
            foreach (var item in input)
            {
                double demand = item.ratePerDay * (double)enabled;
                if (Math.Abs(demand) < 1e-10)
                    continue;
                double available = Math.Min(module.ObjectInfoData.CheckResources(item.resource), demand);
                result = Math.Max(0.0, Math.Min(result, available / demand));
                if (result <= 0.0)
                    break;
            }
            return result;
        }

        internal static void RunEnergyModuleProductEnergy(EnergyProductionModule module, double days)
        {
            if (LoadSaveManager.OnExtractAllFromSaveData)
                return;

            _energyProductionField.SetValue(module, 0.0);
            bool dryRun = days == 0.0;
            if (dryRun)
                days = 1.0;

            var resourceBalance = (MyExtensions.ResourceBalance)_resourceBalanceField.GetValue(module);
            resourceBalance.ZeroAll();

            if (module.Enabled <= 0)
                return;

            var epData = module.facilityDescriptor.energyProductionData;
            if (epData == null)
                return;

            long enabled = module.Enabled;
            double energyProduction = epData.energyProduction * (double)enabled;

            double resourceEfficiency = ComputeEnergyModuleResourceEfficiency(module);
            double throttle = EnergyThrottle.GetThrottleFactor(module);
            resourceEfficiency *= throttle;

            double efficiency = Math.Min(module.GetWorkForceEfficiency(), resourceEfficiency);
            energyProduction *= efficiency;

            double consumptionEfficiency = efficiency;
            if (efficiency > 0.0 && epData.input != null)
            {
                // Pass 1: feasibility — find minimum available ratio
                foreach (var item in epData.input)
                {
                    double baseDemand = days * item.ratePerDay * (double)enabled;
                    double scaledDemand = baseDemand * efficiency;
                    double available = Math.Min(
                        module.ObjectInfoData.CheckResources(item.resource), scaledDemand);
                    consumptionEfficiency = Math.Min(consumptionEfficiency, available / baseDemand);
                }

                // Pass 2: consume fuel
                foreach (var item in epData.input)
                {
                    double baseDemand = days * item.ratePerDay * (double)enabled;
                    double toConsume = Math.Min(
                        module.ObjectInfoData.CheckResources(item.resource),
                        baseDemand * consumptionEfficiency);
                    resourceBalance.Add(
                        MyExtensions.ResourceStorageType.Storage,
                        item.resource,
                        (0.0 - toConsume) / days);
                    if (!dryRun)
                    {
                        module.ObjectInfoData.FastAddRemoveResource(item.resource, 0.0 - toConsume);
                    }
                }
            }

            if (consumptionEfficiency > 0.0)
            {
                _generateByproductsMethod.Invoke(module,
                    new object[] { days * consumptionEfficiency * (double)enabled, days, dryRun });
            }

            // Fuel-out notification (matches facility: 30-day cooldown)
            if (consumptionEfficiency <= 0.0 && !dryRun)
            {
                energyProduction = 0.0;
                var state = _notificationTracker.GetOrCreateValue(module);
                bool shouldNotify = !state.LastNotification.HasValue;
                if (state.LastNotification.HasValue &&
                    (MonoBehaviourSingleton<TimeController>.Instance.CurrentTime
                        - state.LastNotification.Value).TotalDays > 30.0)
                    shouldNotify = true;

                if (shouldNotify)
                {
                    state.LastNotification = MonoBehaviourSingleton<TimeController>.Instance.CurrentTime;
                    MonoBehaviourSingleton<NotificationManager>.Instance.ShowNotification(
                        MonoBehaviourSingleton<NotificationManager>.Instance.GetNotification(
                            ENotificationActionAfterClick.EnergyFacilityShutDown),
                        module.ObjectInfoData.company,
                        module.ObjectInfoData.ObjectInfo,
                        delegate { module.ObjectInfoData.ObjectInfo.MyOnMouseUpAsButton2(); },
                        module.facilityDescriptor.Name,
                        module.ObjectInfoData.ObjectInfo.ObjectName);
                }
            }

            // Solar distance
            if (epData.solarPanels)
            {
                float distanceAU = module.ObjectInfoData.ObjectInfo.DistanceToSunInAU;
                energyProduction *= 1.0 / (distanceAU * distanceAU);
            }

            energyProduction *= module.Company.BonusController.GetBonus(
                EBonus.PowerProduction, module.facilityDescriptor);
            energyProduction /= days;

            _energyProductionField.SetValue(module, energyProduction);
        }

        // ── Construction efficiency helpers ─────────────────────────────

        /// <summary>
        /// Construction-specific efficiency: power, workforce, and resources
        /// but NOT habitability — construction modules should work on
        /// uninhabited or low-habitability bodies.
        /// </summary>
        internal static double GetConstructionEfficiency(Facility fac)
        {
            double power = fac.SinglePowerProductionMultiplier;
            double workforce = fac.GetWorkForceEfficiency();
            double resources = fac.GetResourceEfficiency();
            return Math.Min(power, Math.Min(workforce, resources));
        }

        /// <summary>
        /// Collects per-slot construction efficiencies for an ObjectInfoData,
        /// sorted descending so the first build-queue item gets the best module.
        /// Each enabled unit of a built ConstructionEquipmentModule with non-zero
        /// efficiency contributes one slot entry.
        /// </summary>
        internal static List<double> GetConstructionSlotEfficiencies(ObjectInfoData oid)
        {
            var slots = new List<double>();
            foreach (var fac in oid.ListFacility)
            {
                if (!(fac is ConstructionEquipmentModule cem))
                    continue;
                if (cem.BuildProgress < 1f || cem.Enabled <= 0)
                    continue;

                double eff = GetConstructionEfficiency(cem);
                if (eff <= 0.0)
                    continue;

                double speed = GetConstructionSpeed(cem);
                for (long u = 0; u < cem.Enabled; u++)
                    slots.Add(eff * speed);
            }

            long serializedCount = oid.ConstructionEquipmentCount;
            while (slots.Count < serializedCount)
                slots.Add(1.0);

            slots.Sort((a, b) => b.CompareTo(a));
            return slots;
        }
    }

    // ─── PreUpdateBuilding ──────────────────────────────────────────────
    // Postfix: trims the build lists that were already populated by
    // whoever owns the method (stock code or TedditQueuedConstruction).
    // Removes construction-equipment-requiring items beyond the
    // efficiency-gated slot count, and caches per-slot efficiencies
    // for the MyUpdate and WhenBuild patches.

    [HarmonyPatch(typeof(ObjectInfoData), "PreUpdateBuilding")]
    static class PatchPreUpdateBuilding
    {
        static readonly FieldInfo _buildingModuleField =
            AccessTools.Field(typeof(ObjectInfoData), "productionItemBuildingModule");

        internal static List<double> CachedSlotEfficiencies = new List<double>();

        static void Postfix(ObjectInfoData __instance)
        {
            if (!GameplayPatches.ConstructionRespectsEfficiency)
                return;

            try
            {
                Run(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameplayPatches:PreUpdateBuilding] {ex}");
            }
        }

        static void Run(ObjectInfoData oid)
        {
            var slotEfficiencies = GameplayPatches.GetConstructionSlotEfficiencies(oid);
            CachedSlotEfficiencies = slotEfficiencies;
            int effectiveCount = slotEfficiencies.Count;

            var buildingModule = (List<ProductionItem>)_buildingModuleField.GetValue(oid);
            if (buildingModule == null || buildingModule.Count == 0)
                return;

            // Rebuild the list, keeping only items that fit within the
            // efficiency-gated slot count.  Items that don't require
            // construction equipment are always kept.
            var kept = new List<ProductionItem>(buildingModule.Count);
            int equipSlot = 0;
            foreach (var item in buildingModule)
            {
                bool requiresEquipment = true;
                var facDesc = item.ProductionItemType as Data.ScriptableObject.FacilityBaseDescriptor;
                if (facDesc != null)
                    requiresEquipment = facDesc.ConstructionEquipmentCountIsRequired;

                if (requiresEquipment)
                {
                    if (equipSlot < effectiveCount)
                    {
                        kept.Add(item);
                        equipSlot++;
                    }
                }
                else
                {
                    kept.Add(item);
                }
            }

            buildingModule.Clear();
            buildingModule.AddRange(kept);
        }
    }

    // ─── MyUpdate ───────────────────────────────────────────────────────
    // Full prefix replacement.  Passes per-slot efficiency as the
    // ConstructionPower argument instead of the stock hardcoded 1f.
    // TedditQueuedConstruction's MyUpdate postfix (TryStartQueuedFacilities)
    // is compatible — Harmony runs postfixes after our prefix.

    [HarmonyPatch(typeof(ObjectInfoData), "MyUpdate")]
    static class PatchMyUpdate
    {
        static readonly FieldInfo _buildingModuleField =
            AccessTools.Field(typeof(ObjectInfoData), "productionItemBuildingModule");
        static readonly FieldInfo _sclvField =
            AccessTools.Field(typeof(ObjectInfoData), "productionItemSCLV");
        static readonly FieldInfo _productionItemsField =
            AccessTools.Field(typeof(ObjectInfoData), "productionItems");
        static readonly FieldInfo _lastUpdateField =
            AccessTools.Field(typeof(ObjectInfoData), "updateBuildingDateTimeLastUpdate");

        static bool Prefix(ObjectInfoData __instance)
        {
            if (!GameplayPatches.ConstructionRespectsEfficiency)
                return true;

            try
            {
                Run(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameplayPatches:MyUpdate] {ex}");
                return true;
            }
            return false;
        }

        static void Run(ObjectInfoData oid)
        {
            // PreUpdateBuilding populates the build lists (stock or
            // TedditQueuedConstruction), then our postfix trims them
            // and caches slot efficiencies.
            oid.PreUpdateBuilding();

            var buildingModule = (List<ProductionItem>)_buildingModuleField.GetValue(oid);
            var sclv = (List<ProductionItem>)_sclvField.GetValue(oid);
            var lastUpdate = (DateTime?)_lastUpdateField.GetValue(oid);
            var slotEfficiencies = PatchPreUpdateBuilding.CachedSlotEfficiencies;

            if ((buildingModule.Count > 0 || sclv.Count > 0) && lastUpdate.HasValue)
            {
                double totalDays = (MonoBehaviourSingleton<TimeController>.Instance.CurrentTime - lastUpdate.Value).TotalDays;
                if (totalDays > 0.0)
                {
                    int slotIndex = 0;
                    for (int i = 0; i < buildingModule.Count; i++)
                    {
                        var itemType = buildingModule[i].ProductionItemType;
                        bool requiresEquipment = true;
                        var facDesc = itemType as Data.ScriptableObject.FacilityBaseDescriptor;
                        if (facDesc != null)
                            requiresEquipment = facDesc.ConstructionEquipmentCountIsRequired;

                        if (requiresEquipment && slotIndex < slotEfficiencies.Count)
                        {
                            buildingModule[i].UpdateBuilding((float)slotEfficiencies[slotIndex], totalDays);
                            slotIndex++;
                        }
                        else
                        {
                            buildingModule[i].UpdateBuilding(1f, totalDays);
                        }
                    }

                    for (int j = 0; j < sclv.Count; j++)
                    {
                        sclv[j].UpdateBuilding(1f, totalDays);
                    }
                }
            }

            _lastUpdateField.SetValue(oid, MonoBehaviourSingleton<TimeController>.Instance.CurrentTime);

            var productionItems = (List<ProductionItem>)_productionItemsField.GetValue(oid);
            for (int k = 0; k < productionItems.Count; k++)
            {
                ProductionItem productionItem;
                do
                {
                    productionItem = productionItems[k];
                    productionItem.MyUpdate();
                }
                while (k < productionItems.Count && productionItem != productionItems[k]);
            }
        }
    }

    // ─── WhenBuild (ETA) ────────────────────────────────────────────────
    // Full prefix replacement.  Uses efficiency-aware slot count and
    // per-slot speed for the actively-building item's remaining time.

    [HarmonyPatch(typeof(ObjectInfoData), "WhenBuild")]
    static class PatchWhenBuild
    {
        static readonly FieldInfo _buildingModuleField =
            AccessTools.Field(typeof(ObjectInfoData), "productionItemBuildingModule");
        static readonly FieldInfo _sclvField =
            AccessTools.Field(typeof(ObjectInfoData), "productionItemSCLV");
        static readonly FieldInfo _productionItemsField =
            AccessTools.Field(typeof(ObjectInfoData), "productionItems");

        static bool Prefix(ObjectInfoData __instance, ProductionItem productionItem, ref DateTime __result)
        {
            if (!GameplayPatches.ConstructionRespectsEfficiency)
                return true;

            try
            {
                __result = Run(__instance, productionItem);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameplayPatches:WhenBuild] {ex}");
                return true;
            }
            return false;
        }

        static DateTime Run(ObjectInfoData oid, ProductionItem productionItem)
        {
            var buildingModule = (List<ProductionItem>)_buildingModuleField.GetValue(oid);
            var sclv = (List<ProductionItem>)_sclvField.GetValue(oid);

            if (buildingModule == null || sclv == null)
            {
                oid.PreUpdateBuilding();
                buildingModule = (List<ProductionItem>)_buildingModuleField.GetValue(oid);
                sclv = (List<ProductionItem>)_sclvField.GetValue(oid);
            }

            var slotEfficiencies = PatchPreUpdateBuilding.CachedSlotEfficiencies;
            int effectiveCount = slotEfficiencies.Count;

            DateTime now = MonoBehaviourSingleton<TimeController>.Instance.CurrentTime;

            var itemType = productionItem.ProductionItemType;

            if (itemType is Data.ScriptableObject.FacilityBaseDescriptor ||
                itemType is Data.ScriptableObject.SpaceModuleDescriptor)
            {
                int slotIdx = -1;
                int equipSlot = 0;
                for (int i = 0; i < buildingModule.Count; i++)
                {
                    if (buildingModule[i] == productionItem)
                    {
                        slotIdx = equipSlot;
                        break;
                    }

                    bool requiresEquipment = true;
                    var fd = buildingModule[i].ProductionItemType as Data.ScriptableObject.FacilityBaseDescriptor;
                    if (fd != null)
                        requiresEquipment = fd.ConstructionEquipmentCountIsRequired;
                    if (requiresEquipment)
                        equipSlot++;
                }

                if (slotIdx >= 0)
                {
                    double eff = (slotIdx < slotEfficiencies.Count) ? slotEfficiencies[slotIdx] : 1.0;
                    if (eff <= 0.0) eff = 1.0;
                    float remaining = (1f - productionItem.BuildProgress) * productionItem.TimeToBuildInDays();
                    return now.AddDays(remaining / eff);
                }

                if (effectiveCount == 0)
                    return now;

                var productionItems = (List<ProductionItem>)_productionItemsField.GetValue(oid);
                DateTime result = now;
                int counter = 0;
                for (int i = 0; i < productionItems.Count; i++)
                {
                    if (productionItems[i].BuildProgress >= 1f)
                        continue;

                    var pit = productionItems[i].ProductionItemType;
                    if (!(pit is Data.ScriptableObject.FacilityBaseDescriptor) &&
                        !(pit is Data.ScriptableObject.SpaceModuleDescriptor))
                        continue;

                    bool reqEquip = true;
                    var fd2 = pit as Data.ScriptableObject.FacilityBaseDescriptor;
                    if (fd2 != null)
                        reqEquip = fd2.ConstructionEquipmentCountIsRequired;

                    if (!reqEquip)
                        continue;

                    if (counter % effectiveCount == 0)
                    {
                        float batchTime = (1f - productionItem.BuildProgress) * productionItem.TimeToBuildInDays();
                        result = result.AddDays(batchTime);
                    }

                    if (productionItems[i] == productionItem)
                        return result;

                    counter++;
                }

                return result;
            }

            if (itemType is Data.ScriptableObject.SpacecraftType ||
                itemType is Data.ScriptableObject.LaunchVehicleType)
            {
                if (sclv.Contains(productionItem))
                {
                    float remaining = (1f - productionItem.BuildProgress) * productionItem.TimeToBuildInDays();
                    return now.AddDays(remaining);
                }

                int vehicleCount = 0;
                double vaCount = oid.VehicleAssemblyCountEnable;
                vehicleCount = (vaCount > 0.0 && vaCount < 1.0) ? 1 : (int)vaCount;

                if (vehicleCount == 0)
                    return now;

                var productionItems = (List<ProductionItem>)_productionItemsField.GetValue(oid);
                DateTime result = now;
                int counter = 0;
                for (int i = 0; i < productionItems.Count; i++)
                {
                    if (productionItems[i].BuildProgress >= 1f)
                        continue;

                    var pit = productionItems[i].ProductionItemType;
                    if (!(pit is Data.ScriptableObject.SpacecraftType) &&
                        !(pit is Data.ScriptableObject.LaunchVehicleType))
                        continue;

                    if (counter % vehicleCount == 0)
                    {
                        float batchTime = (1f - productionItem.BuildProgress) * productionItem.TimeToBuildInDays();
                        result = result.AddDays(batchTime);
                    }

                    if (productionItems[i] == productionItem)
                        return result;

                    counter++;
                }

                return result;
            }

            return now;
        }
    }

    // ─── EnergyProductionModule.ProductEnergy ───────────────────────
    // Full prefix replacement. Adds fuel consumption, solar distance,
    // workforce efficiency, and shutdown notifications — matching the
    // ground-facility behaviour minus habitability.

    [HarmonyPatch(typeof(EnergyProductionModule), "ProductEnergy")]
    static class PatchEnergyModuleProductEnergy
    {
        static bool Prefix(EnergyProductionModule __instance, double days)
        {
            Plugin.Log.LogInfo($"[EnergyModule:ProductEnergy] id={__instance.facilityDescriptor?.ID} days={days:F4} flag={GameplayPatches.EnergyModuleConsumeFuel}"); // TEMP-DEBUG
            if (!GameplayPatches.EnergyModuleConsumeFuel)
                return true;

            try
            {
                GameplayPatches.RunEnergyModuleProductEnergy(__instance, days);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameplayPatches:EnergyModuleProductEnergy] {ex}");
                return true;
            }
            return false;
        }
    }

    // ─── EnergyProductionModule → GetResourceEfficiency ─────────────
    // EnergyProductionModule inherits Facility.GetResourceEfficiency()
    // which returns 1.0. This prefix makes it report fuel availability
    // so other game systems (UI, efficiency display) see the real value.

    [HarmonyPatch(typeof(Facility), "GetResourceEfficiency")]
    static class PatchEnergyModuleGetResourceEfficiency
    {
        static bool Prefix(Facility __instance, ref double __result)
        {
            if (!GameplayPatches.EnergyModuleConsumeFuel)
                return true;
            if (!(__instance is EnergyProductionModule module))
                return true;

            try
            {
                __result = GameplayPatches.ComputeEnergyModuleResourceEfficiency(module);
                double throttle = EnergyThrottle.GetThrottleFactor(module);
                Plugin.Log.LogInfo($"[EnergyModule:GetResEff] id={module.facilityDescriptor?.ID} resEff={__result:F4} throttle={throttle:F4}"); // TEMP-DEBUG
                if (throttle < 1.0)
                    __result *= throttle;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameplayPatches:EnergyModuleResourceEfficiency] {ex}");
                return true;
            }
            return false;
        }
    }
}
