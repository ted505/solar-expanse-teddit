using System;
using System.Collections.Generic;
using Data.ScriptableObject;
using Game.ObjectInfoDataScripts;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using HarmonyLib;
using Extensions;
using Manager;

namespace Teddit
{
    static class EnergyThrottle
    {
        static readonly HashSet<string> _throttleableIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<ObjectInfoData, TickCache> _cache =
            new Dictionary<ObjectInfoData, TickCache>();

        // Reentrant guard: when true, GetThrottleFactor returns 1.0 so
        // ComputeThrottleFactors reads un-throttled potential from producers.
        static bool _computingPotential;

        class TickCache
        {
            public DateTime Tick;
            public Dictionary<Facility, double> Factors;
        }

        internal static void RegisterThrottleable(string descriptorId)
        {
            _throttleableIds.Add(descriptorId);
            Plugin.Log.LogInfo($"[EnergyThrottle] Registered throttleable: {descriptorId}"); // TEMP-DEBUG
        }

        internal static void UnregisterThrottleable(string descriptorId)
        {
            _throttleableIds.Remove(descriptorId);
        }

        internal static bool IsThrottleable(Facility fac)
        {
            return fac?.facilityDescriptor != null
                && _throttleableIds.Contains(fac.facilityDescriptor.ID);
        }

        internal static double GetThrottleFactor(Facility producer)
        {
            if (_computingPotential)
            {
                Plugin.Log.LogInfo($"[EnergyThrottle:GetFactor] {producer.facilityDescriptor?.ID} — guard active, returning 1.0"); // TEMP-DEBUG
                return 1.0;
            }

            if (!IsThrottleable(producer))
            {
                Plugin.Log.LogInfo($"[EnergyThrottle:GetFactor] {producer.facilityDescriptor?.ID} — NOT throttleable (registered: {string.Join(",", _throttleableIds)})"); // TEMP-DEBUG
                return 1.0;
            }

            var oid = producer.ObjectInfoData;
            if (oid == null)
            {
                Plugin.Log.LogInfo($"[EnergyThrottle:GetFactor] {producer.facilityDescriptor?.ID} — oid null"); // TEMP-DEBUG
                return 1.0;
            }

            var now = MonoBehaviourSingleton<TimeController>.Instance.CurrentTime;

            TickCache tc;
            if (!_cache.TryGetValue(oid, out tc) || tc.Tick != now)
            {
                tc = ComputeThrottleFactors(oid, now);
                _cache[oid] = tc;
            }

            double factor;
            if (tc.Factors != null && tc.Factors.TryGetValue(producer, out factor))
            {
                Plugin.Log.LogInfo($"[EnergyThrottle:GetFactor] {producer.facilityDescriptor?.ID} — factor={factor:F4}"); // TEMP-DEBUG
                return factor;
            }

            Plugin.Log.LogInfo($"[EnergyThrottle:GetFactor] {producer.facilityDescriptor?.ID} — not in cache factors, returning 1.0"); // TEMP-DEBUG
            return 1.0;
        }

        static TickCache ComputeThrottleFactors(ObjectInfoData oid, DateTime tick)
        {
            var tc = new TickCache { Tick = tick };

            double demand = oid.EnergyConsumptionMax;
            double batterySpace = oid.EnergyFreeSpaceInBattery;
            double effectiveDemand = demand + batterySpace;

            var throttleable = new List<(Facility fac, double potential, int priority)>();
            double nonThrottleableTotal = 0.0;

            // Read un-throttled potential: set guard so GetThrottleFactor
            // returns 1.0 for any GetResourceEfficiency calls triggered
            // by the dryRun ProductEnergy that populates EnergyProduction.
            _computingPotential = true;
            try
            {
                foreach (var fac in oid.ListFacility)
                {
                    double potential = GetProducerPotential(fac, oid);
                    if (potential <= 0.0)
                        continue;

                    if (_throttleableIds.Contains(fac.facilityDescriptor.ID))
                    {
                        int priority = oid.GetPriority(fac.facilityDescriptor);
                        throttleable.Add((fac, potential, priority));
                    }
                    else
                    {
                        nonThrottleableTotal += potential;
                    }
                }
            }
            finally
            {
                _computingPotential = false;
            }

            Plugin.Log.LogInfo($"[EnergyThrottle:Compute] body={oid.ObjectInfo?.ObjectName} demand={demand:F2} battery={batterySpace:F2} effectiveDemand={effectiveDemand:F2} nonThrottle={nonThrottleableTotal:F2} throttleableCount={throttleable.Count}"); // TEMP-DEBUG

            if (throttleable.Count == 0)
                return tc;

            double remaining = Math.Max(0.0, effectiveDemand - nonThrottleableTotal);

            // Highest priority gets full allocation first; lowest throttles first
            throttleable.Sort((a, b) => b.priority.CompareTo(a.priority));

            var factors = new Dictionary<Facility, double>(throttleable.Count);
            foreach (var entry in throttleable)
            {
                if (remaining >= entry.potential)
                {
                    factors[entry.fac] = 1.0;
                    remaining -= entry.potential;
                }
                else if (remaining > 0.0)
                {
                    factors[entry.fac] = remaining / entry.potential;
                    remaining = 0.0;
                }
                else
                {
                    factors[entry.fac] = 0.0;
                }
            }

            tc.Factors = factors;
            return tc;
        }

        static double GetProducerPotential(Facility fac, ObjectInfoData oid)
        {
            var epData = fac.facilityDescriptor?.energyProductionData;
            if (epData == null || fac.Enabled <= 0)
                return 0.0;

            double production = epData.energyProduction * (double)fac.Enabled;

            double fuelEff = 1.0;
            if (epData.input != null)
            {
                foreach (var item in epData.input)
                {
                    double need = item.ratePerDay * (double)fac.Enabled;
                    if (need < 1e-10) continue;
                    double have = Math.Min(oid.CheckResources(item.resource), need);
                    fuelEff = Math.Min(fuelEff, have / need);
                }
            }

            double workEff = fac.GetWorkForceEfficiency();
            double eff = Math.Min(workEff, fuelEff);

            if (fac is EnergyProductionFacility)
                eff *= fac.HabitabilityEfficiencyMultiplier;

            production *= eff;

            if (epData.solarPanels)
            {
                float dist = oid.ObjectInfo.DistanceToSunInAU;
                if (dist > 0f)
                    production *= 1.0 / (dist * dist);
            }

            return production;
        }
    }

    // ─── UI: show Power Efficiency for throttleable producers ──────
    // Stock skips the PowerEfficiency line when EnergyConsumption == 0.
    // For throttleable energy producers we inject it so the player can
    // see the current throttle level.

    [HarmonyPatch(typeof(FacilityBaseDescriptor), "GetFacilityStats")]
    static class PatchGetFacilityStatsThrottleDisplay
    {
        static void Postfix(FacilityBaseDescriptor __instance, Facility facility,
                            ref List<(string, string)> __result)
        {
            Plugin.Log.LogInfo($"[EnergyThrottle:UI] GetFacilityStats postfix — id={__instance?.ID} facility={facility?.facilityDescriptor?.ID} energyCons={__instance?.EnergyConsumption} isThrottleable={facility != null && EnergyThrottle.IsThrottleable(facility)}"); // TEMP-DEBUG
            if (facility == null || __instance.EnergyConsumption > 0.0)
                return;
            if (!EnergyThrottle.IsThrottleable(facility))
                return;

            double eff = facility.GetResourceEfficiency() * 100.0;
            Plugin.Log.LogInfo($"[EnergyThrottle:UI] Adding PowerEfficiency line: {eff:F2}%"); // TEMP-DEBUG
            __result.Add(("Game.UI.Windows.FacilityInfoWindow.PowerEfficiency",
                eff.ToPostfixString() + "%"));
        }
    }

    // ─── Facility GetResourceEfficiency — throttle injection ────────
    // EnergyProductionFacility already overrides GetResourceEfficiency
    // to check fuel availability. This postfix clamps the result by
    // the throttle factor so the stock ProductEnergy naturally scales
    // both output and fuel consumption.

    [HarmonyPatch(typeof(EnergyProductionFacility), "GetResourceEfficiency")]
    static class PatchEnergyFacilityGetResourceEfficiencyThrottle
    {
        static void Postfix(EnergyProductionFacility __instance, ref double __result)
        {
            if (!EnergyThrottle.IsThrottleable(__instance))
                return;

            try
            {
                double factor = EnergyThrottle.GetThrottleFactor(__instance);
                Plugin.Log.LogInfo($"[EnergyThrottle:FacilityEff] id={__instance.facilityDescriptor?.ID} stockEff={__result:F4} throttle={factor:F4}"); // TEMP-DEBUG
                if (factor < 1.0)
                    __result *= factor;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[EnergyThrottle:FacilityEfficiency] {ex}");
            }
        }
    }
}
