using System;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;

namespace Teddit
{
    internal static class MarketRefresh
    {
        static bool _scheduled;
        static Type _cbType;

        internal static void Schedule()
        {
            if (_scheduled) return;

            _cbType = AccessTools.TypeByName("CompanyBehaviour");
            if (_cbType == null)
            {
                Plugin.Log.LogWarning("[MarketRefresh] CompanyBehaviour type not found — skipping.");
                return;
            }

            _scheduled = true;
            TimeController instance = MonoBehaviourSingleton<TimeController>.Instance;
            instance.onEachDayChange = (Action<double>)Delegate.Combine(
                instance.onEachDayChange, new Action<double>(OnDayChange));
            Plugin.Log.LogInfo("[MarketRefresh] Scheduled for first day-change.");
        }

        static void OnDayChange(double day)
        {
            TimeController instance = MonoBehaviourSingleton<TimeController>.Instance;
            instance.onEachDayChange = (Action<double>)Delegate.Remove(
                instance.onEachDayChange, new Action<double>(OnDayChange));
            _scheduled = false;

            try
            {
                var mi = _cbType.GetMethod("TriggerPriceChanges",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null)
                {
                    Plugin.Log.LogWarning("[MarketRefresh] Could not find TriggerPriceChanges method.");
                    return;
                }

                foreach (Company company in MonoBehaviourSingleton<GameManager>.Instance.Companies)
                {
                    if (!company.Definition.IsWorldGovernment) continue;
                    var cb = company.GetComponent(_cbType);
                    if (cb == null) continue;
                    mi.Invoke(cb, new object[] { null });
                    Plugin.Log.LogInfo($"[MarketRefresh] Triggered price changes for WG ({company.ID}).");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MarketRefresh] {ex}");
            }
        }
    }
}
