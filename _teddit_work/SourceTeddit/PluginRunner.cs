using System.IO;
using Manager;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Standalone MonoBehaviour created at startup so Unity's normal update loop
    /// runs our polling logic. BepInEx's BaseUnityPlugin.Update() is never called
    /// in this game, but a manually created GameObject component works normally.
    /// </summary>
    internal class PluginRunner : MonoBehaviour
    {
        private bool _ran = false;
        private int _frame = 0;

        void Update()
        {
            _frame++;
            if (_frame == 1 || _frame == 60 || _frame == 300)
            {
                var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
                Plugin.Log.LogInfo($"[Teddit] frame={_frame} ran={_ran} OIM={(oim == null ? "null" : oim.allObjectInfos?.Count.ToString())}");
            }

            if (_ran) return;

            var oimCheck = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (oimCheck == null || oimCheck.allObjectInfos == null || oimCheck.allObjectInfos.Count == 0) return;

            _ran = true;
            Plugin.Log.LogInfo($"[Teddit] ObjectInfoManager ready — {oimCheck.allObjectInfos.Count} bodies.");

            if (Plugin.DumpOnLoad)
            {
                try { DataDumper.Run(Path.Combine(Plugin.PluginDir, "dump.json")); }
                catch (System.Exception ex) { Plugin.Log.LogError($"[DataDumper] {ex}"); }
            }

            var config = DepositConfig.Load(Path.Combine(Plugin.PluginDir, "deposits.json"));
            try { DepositInjector.Run(config, oimCheck); }
            catch (System.Exception ex) { Plugin.Log.LogError($"[DepositInjector] {ex}"); }
        }
    }
}
