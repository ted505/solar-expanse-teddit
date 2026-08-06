// CompanyPatches.cs — excluded from compilation via <Compile Remove> in Teddit.csproj
// Wires CompanyCreator's three injection phases into the Harmony patch pipeline.
// Re-enable by removing the <Compile Remove> entry and rebuilding.
using System;
using HarmonyLib;
using Manager;

namespace Teddit
{
    /// <summary>
    /// Phase 1 — create CompanyDefinition ScriptableObjects and register them in
    /// AllCompanyDefinitions + allMyIDScriptableObjects before anything else reads those lists.
    /// </summary>
    [HarmonyPatch(typeof(AllScriptableObjectManager), "InitializeSingleton")]
    static class PatchAllSOManagerInitializeSingleton
    {
        static void Postfix(AllScriptableObjectManager __instance)
        {
            try { CompanyCreator.InjectDefinitions(__instance); }
            catch (Exception ex) { Plugin.Log.LogError($"[CompanyCreator] Phase 1 crashed: {ex}"); }
        }
    }

    /// <summary>
    /// Phase 2 — clone AI Company GameObjects for each new definition and register them
    /// with GameManager.companies.  Runs after SetMainObjectForCompany() has already set
    /// the shared mainObject on existing companies, so we mirror that call ourselves.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "Awake")]
    static class PatchGameManagerAwake
    {
        static void Postfix(GameManager __instance)
        {
            try { CompanyCreator.InjectCompanyObjects(__instance); }
            catch (Exception ex) { Plugin.Log.LogError($"[CompanyCreator] Phase 2 crashed: {ex}"); }
        }
    }

    /// <summary>
    /// Phase 3 — inject CompanyDataSave entries and add definitions to
    /// enabledRivalCorporations so SetupPlayerAndRivalAIs (called at the top of this
    /// method) keeps the new Company GameObjects active and their AI enabled.
    /// This fires for both new-game and load-game paths.
    /// </summary>
    [HarmonyPatch(typeof(LoadSaveManager), "ExtractAllFromSaveData")]
    static class PatchExtractAllFromSaveData
    {
        static void Prefix(SaveGameData saveGameData)
        {
            try { CompanyCreator.InjectSaveData(saveGameData); }
            catch (Exception ex) { Plugin.Log.LogError($"[CompanyCreator] Phase 3 crashed: {ex}"); }
        }
    }
}
