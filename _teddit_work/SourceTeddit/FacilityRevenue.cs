using System;
using System.Collections.Generic;
using System.Reflection;
using Data.ScriptableObject;
using Extensions;
using Game;
using Game.ObjectInfoDataScripts;
using HarmonyLib;
using Manager;

namespace Teddit
{
    static class FacilityRevenue
    {
        static readonly Dictionary<string, double> _revenueRates =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        internal static void RegisterRevenue(string descriptorId, double revenuePerDay)
        {
            _revenueRates[descriptorId] = revenuePerDay;
            Plugin.Log.LogInfo($"[FacilityRevenue] Registered: {descriptorId} = {revenuePerDay}/day");
        }

        internal static void UnregisterRevenue(string descriptorId)
        {
            _revenueRates.Remove(descriptorId);
        }

        internal static bool HasRevenue(string descriptorId)
        {
            return descriptorId != null && _revenueRates.ContainsKey(descriptorId);
        }

        internal static double GetRate(string descriptorId)
        {
            double rate;
            if (descriptorId != null && _revenueRates.TryGetValue(descriptorId, out rate))
                return rate;
            return 0.0;
        }

        internal static double ComputeDailyRevenue(Company company)
        {
            if (_revenueRates.Count == 0) return 0.0;

            double total = 0.0;
            var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (oim == null) return 0.0;

            foreach (var oi in oim.allObjectInfos)
            {
                var oid = oi.GetObjectInfoData(company);
                if (oid == null) continue;

                foreach (var fac in oid.ListFacility)
                {
                    if (fac.BuildProgress < 1f || fac.Enabled <= 0)
                        continue;
                    if (fac.facilityDescriptor == null)
                        continue;

                    double rate;
                    if (_revenueRates.TryGetValue(fac.facilityDescriptor.ID, out rate))
                        total += rate * fac.Enabled * fac.GetResourceEfficiency();
                }
            }

            return total;
        }
    }

    [HarmonyPatch(typeof(ObjectInfoManager), "CalculateMaintenanceCostsAndIncomePerDay")]
    static class PatchCalcMaintenanceAddRevenue
    {
        static void Postfix(double days, Company company,
                            ref (double totalCost, double totalIncome) __result)
        {
            double dailyRevenue = FacilityRevenue.ComputeDailyRevenue(company);
            if (dailyRevenue <= 0.0) return;

            __result.totalIncome += dailyRevenue * days;
        }
    }

    [HarmonyPatch]
    static class PatchUpdateUpkeepAndIncomeRevenue
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Game.UI.UIManager), "UpdateUpkeepAndIncome");
        }

        static void Prefix(ref long totalIncome, ref long balance)
        {
            var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
            if (player == null) return;

            double dailyRevenue = FacilityRevenue.ComputeDailyRevenue(player);
            if (dailyRevenue <= 0.0) return;

            long monthlyRevenue = (long)(dailyRevenue * 30.0);
            totalIncome += monthlyRevenue;
            balance += monthlyRevenue;
        }
    }

    [HarmonyPatch]
    static class PatchUpdateUpkeepAndIncomeRevenueTooltip
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Game.UI.UIManager), "UpdateUpkeepAndIncome");
        }

        static void Postfix(object __instance)
        {
            var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
            if (player == null) return;

            double dailyRevenue = FacilityRevenue.ComputeDailyRevenue(player);
            if (dailyRevenue <= 0.0) return;

            long monthlyRevenue = (long)(dailyRevenue * 30.0);

            var tooltipFi = AccessTools.Field(typeof(Game.UI.UIManager), "upkeepTooltip");
            if (tooltipFi == null) return;

            var tooltip = tooltipFi.GetValue(__instance);
            if (tooltip == null) return;

            var textPi = AccessTools.Property(tooltip.GetType(), "CustomTextFromCode");
            if (textPi == null) return;

            string text = textPi.GetValue(tooltip, null) as string;
            if (string.IsNullOrEmpty(text)) return;

            string revLine = "\n  Facility Revenue: "
                + MyExtensions.DollarString
                + monthlyRevenue.ToPostfixString();

            text += revLine;
            textPi.SetValue(tooltip, text, null);
        }
    }

    [HarmonyPatch(typeof(FacilityBaseDescriptor), "GetFacilityStats")]
    static class PatchGetFacilityStatsRevenue
    {
        static void Postfix(FacilityBaseDescriptor __instance, Facility facility,
                            ref List<(string, string)> __result)
        {
            if (__instance == null) return;
            double rate = FacilityRevenue.GetRate(__instance.ID);
            if (rate <= 0.0) return;

            string value = MyExtensions.DollarString + rate.ToPostfixString() + "/day";
            __result.Add(("Revenue", value));
        }
    }
}
