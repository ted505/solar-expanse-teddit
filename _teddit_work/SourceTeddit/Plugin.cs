using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace Teddit
{
    [BepInPlugin("com.teddit.teddit", "Teddit", "1.12")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static string PluginDir;
        internal static string Version;

        // Set to false once you have dump.json.
        internal const bool DumpOnLoad = true;

        void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(Info.Location);
            Version = Info.Metadata.Version.ToString();
            Log.LogInfo($"Teddit v{Version} loaded.");
            // Patch each class independently so a patch whose target method is
            // absent in the running game version can't abort the batch and silently
            // take the rest of the mod's patches down with it.
            var harmony = new Harmony("com.teddit.teddit");
            foreach (var type in AccessTools.GetTypesFromAssembly(System.Reflection.Assembly.GetExecutingAssembly()))
            {
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception ex) { Log.LogWarning($"Skipped incompatible patch '{type.Name}': {ex.Message}"); }
            }
        }
    }
}
