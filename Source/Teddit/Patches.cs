using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CustomUpdate;
using Data.ScriptableObject;
using Extensions;
using Game;
using Game.ContractsObjectives;
using Game.Info;
using Game.UI;
using Game.UI.Windows.Elements.ChoseFacilityElements;
using Game.UI.Windows.Windows;
using Game.VisualizationScripts;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Teddit
{
    static class TooltipImageFix
    {
        internal static void Repair(Image imgType)
        {
            if (imgType == null)
                return;

            imgType.type = Image.Type.Simple;
            imgType.useSpriteMesh = false;
            imgType.preserveAspect = true;
        }
    }

    /// <summary>
    /// Patches ResearchTree.Show() so we can inject mod-created research nodes into the
    /// already-built tree the first time the player opens the window.
    ///
    /// CreateUI() is never called at runtime (the tree is built via scene-level events
    /// before our patcher runs), so patching Show() is the only reliable hook we have.
    /// SpawnPendingEntries rebuilds just the affected branch(es) so the new entries appear.
    /// </summary>
    [HarmonyPatch]
    static class PatchResearchTreeCreateUI
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Game.UI.Windows.Windows.ResearchTree.ResearchTree");
            if (t == null)
            {
                Plugin.Log.LogWarning("[Patch] ResearchTree type not found — CreateUI patch skipped.");
                return null;
            }
            return AccessTools.Method(t, "CreateUI");
        }

        static void Prefix(object __instance)
        {
            ResearchCreator._researchTreeInstance = __instance;
            ResearchCreator.EnsureResearchLoadedBeforeCreateUI();
        }

        static void Postfix(object __instance)
        {
            ResearchCreator._researchTreeInstance = __instance;
            ResearchCreator.LogTreeStatus(__instance);
        }
    }

    [HarmonyPatch]
    static class PatchResearchTreeShowAppend
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Game.UI.Windows.Windows.ResearchTree.ResearchTree");
            if (t == null)
                return null;
            return AccessTools.Method(t, "Show");
        }

        static void Postfix(object __instance)
        {
            ResearchCreator._researchTreeInstance = __instance;
            ResearchCreator.SpawnPendingEntries(__instance);
        }
    }

    /// <summary>
    /// Hooks into ObjectInfoManager.Update() — a scene MonoBehaviour whose Update() DOES fire.
    /// (BepInEx plugin MonoBehaviour.Update() is never called in this game; scene objects are fine.)
    /// Waits for solarSystemLoadEventWas == true, meaning solarSystemLoadEvent.Invoke() has
    /// completed and all ObjectInfo.StartStuff() handlers have run — deposits are ready.
    ///
    /// Multi-mod loading order:
    ///   1. Root plugin directory (backwards-compatible location for config files)
    ///   2. Each subdirectory inside plugins/Teddit/mods/, sorted alphabetically
    ///
    /// Each mod directory may contain any subset of:
    ///   facilities.yaml, spacecraft.yaml, launch_vehicles.yaml, research.yaml, deposits.yaml
    /// Missing files are silently skipped. Patches from later mods override earlier ones
    /// for the same object-ID + field combinations.
    /// </summary>
    [HarmonyPatch(typeof(ObjectInfoManager), "Update")]
    static class PatchObjectInfoManagerUpdate
    {
        static bool _ran = false;
        static int _lastInstanceId = -1;

        static readonly FieldInfo _eventWasField = typeof(ObjectInfoManager)
            .GetField("solarSystemLoadEventWas",
                      BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(ObjectInfoManager __instance)
        {
            int instanceId = __instance.GetInstanceID();
            if (instanceId != _lastInstanceId)
            {
                _ran = false;
                _lastInstanceId = instanceId;
                BodyPatcher.ResetSessionState();
                Plugin.Log.LogInfo($"[Teddit] New ObjectInfoManager instance ({instanceId}) — resetting load state.");
            }

            if (_ran) return;
            if (_eventWasField == null)
            {
                Plugin.Log.LogError("[Patch] solarSystemLoadEventWas field not found — check decompile.");
                _ran = true;
                return;
            }

            bool eventFired = (bool)_eventWasField.GetValue(__instance);
            if (!eventFired) return;

            _ran = true;
            Plugin.Log.LogInfo($"[Teddit] solarSystemLoadEvent done — {__instance.allObjectInfos?.Count} bodies.");

            // ── Data dump (disable once you have the data) ────────────────────
            if (Plugin.DumpOnLoad)
            {
                try { DataDumper.Run(Path.Combine(Plugin.PluginDir, "dump")); }
                catch (Exception ex) { Plugin.Log.LogError($"[DataDumper] {ex}"); }
            }

            Plugin.Log.LogInfo("[Teddit] About to load RootPatchSettings.");
            var rootSettings = RootPatchSettings.Load(Plugin.PluginDir);
            Plugin.Log.LogInfo("[Teddit] RootPatchSettings loaded.");

            var gameplaySettings = GameplayPatchSettings.Load(Plugin.PluginDir);
            GameplayPatches.ConstructionRespectsEfficiency = gameplaySettings.ConstructionRespectsEfficiency;
            GameplayPatches.EnergyModuleConsumeFuel = gameplaySettings.EnergyModuleConsumeFuel;

            // ── Collect mod directories in load order ─────────────────────────
            var dirs = new List<string> { Plugin.PluginDir };

            string modsFolder = Path.Combine(Plugin.PluginDir, "mods");
            if (Directory.Exists(modsFolder))
            {
                var subDirs = Directory.GetDirectories(modsFolder);
                Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
                dirs.AddRange(subDirs);
            }


            // ── Apply each mod directory in order ─────────────────────────────
            LifeSupportPatcher.ResetConfig();

            foreach (var dir in dirs)
            {
                Plugin.Log.LogInfo($"[Teddit] Applying mod dir: {dir}");
                ApplyModDir(dir, __instance, rootSettings);
            }

            // ── Second pass: facility placements run AFTER every dir has had a
            // chance to register its facility descriptors and company definitions,
            // so any placement in any dir can reference any modded ID.
            if (rootSettings.FacilityPlacementsEnabled)
            {
                foreach (var dir in dirs)
                {
                    string label = Path.GetFileName(dir);
                    var placementConfig = FacilityPlacementConfig.Load(Path.Combine(dir, "facility_placements.yaml"));
                    try { FacilityPlacementInjector.Run(placementConfig, __instance); }
                    catch (Exception ex) { Plugin.Log.LogError($"[FacilityPlacement:{label}] {ex}"); }
                }
            }

            // ── Third pass: starting resources after facilities exist so the
            // ObjectInfoData row set is fully realized. Also gives ResourceCreator
            // mods a chance to register new resources before stockpiles are set.
            if (rootSettings.StartingResourcesEnabled)
            {
                foreach (var dir in dirs)
                {
                    string label = Path.GetFileName(dir);
                    var startingConfig = StartingResourcesConfig.Load(Path.Combine(dir, "starting_resources.yaml"));
                    try { StartingResourcesInjector.Run(startingConfig, __instance); }
                    catch (Exception ex) { Plugin.Log.LogError($"[StartingResources:{label}] {ex}"); }
                }
            }

            // ── Deferred market refresh so modded resources get trade offers ──
            MarketRefresh.Schedule();
        }

        static void ApplyModDir(string dir, ObjectInfoManager oi, RootPatchSettings rootSettings)
        {
            string label = Path.GetFileName(dir);

            if (rootSettings.ResourcesEnabled)
            {
                var resourcePatches = PatchConfig.Load(Path.Combine(dir, "resources.yaml"));
                try { ScriptableObjectPatcher.RunResources(resourcePatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[ResourcePatcher:{label}] {ex}"); }
            }

            if (rootSettings.CompaniesEnabled)
            {
                var companyPatches = PatchConfig.Load(Path.Combine(dir, "companies.yaml"));
                try { ScriptableObjectPatcher.RunCompanies(companyPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[CompanyPatcher:{label}] {ex}"); }
            }

            if (rootSettings.FacilitiesEnabled)
            {
                var facilityPatches = PatchConfig.Load(Path.Combine(dir, "facilities.yaml"));
                try { ScriptableObjectPatcher.RunFacilities(facilityPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[FacilityPatcher:{label}] {ex}"); }
            }

            if (rootSettings.SpacecraftEnabled)
            {
                var spacecraftPatches = PatchConfig.Load(Path.Combine(dir, "spacecraft.yaml"));
                try { ScriptableObjectPatcher.RunSpacecraft(spacecraftPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[SpacecraftPatcher:{label}] {ex}"); }
            }

            if (rootSettings.LaunchVehiclesEnabled)
            {
                var launchVehiclePatches = PatchConfig.Load(Path.Combine(dir, "launch_vehicles.yaml"));
                try { ScriptableObjectPatcher.RunLaunchVehicles(launchVehiclePatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[LaunchVehiclePatcher:{label}] {ex}"); }
            }

            if (rootSettings.ResearchEnabled)
            {
                var researchPatches = PatchConfig.Load(Path.Combine(dir, "research.yaml"));
                try { ScriptableObjectPatcher.RunResearch(researchPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[ResearchPatcher:{label}] {ex}"); }
            }

            if (rootSettings.DepositsEnabled)
            {
                var depositConfig = DepositConfig.Load(Path.Combine(dir, "deposits.yaml"));
                try { DepositInjector.Run(depositConfig, oi); }
                catch (Exception ex) { Plugin.Log.LogError($"[DepositInjector:{label}] {ex}"); }
            }

            if (rootSettings.BodiesEnabled)
            {
                var bodyPatches = PatchConfig.Load(Path.Combine(dir, "bodies.yaml"));
                try { BodyPatcher.Run(bodyPatches, oi); }
                catch (Exception ex) { Plugin.Log.LogError($"[BodyPatcher:{label}] {ex}"); }
            }

            string lifeSupportPath = Path.Combine(dir, "life_support.yaml");
            if (rootSettings.LifeSupportEnabled || File.Exists(lifeSupportPath))
            {
                bool honorEnabled = string.Equals(dir, Plugin.PluginDir, StringComparison.OrdinalIgnoreCase);
                var lifeSupportConfig = LifeSupportConfig.Load(lifeSupportPath, honorEnabled);
                try { LifeSupportPatcher.MergeConfig(lifeSupportConfig, label); }
                catch (Exception ex) { Plugin.Log.LogError($"[LifeSupportPatcher:{label}] {ex}"); }
            }

            if (rootSettings.ContractsEnabled)
            {
                var contractPatches = PatchConfig.Load(Path.Combine(dir, "contracts.yaml"));
                try { ScriptableObjectPatcher.RunContracts(contractPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[ContractPatcher:{label}] {ex}"); }
            }

        }
    }

    [HarmonyPatch(typeof(Spacecraft), "SetCurrentlyOnThisObject")]
    static class PatchSpacecraftSetCurrentlyOnThisObjectNoOrbitBodies
    {
        static readonly HashSet<int> _redirecting = new HashSet<int>();

        static void Postfix(Spacecraft __instance)
        {
            if (__instance == null || __instance.spacecraftType == null)
                return;

            // Guard against re-entrant calls: SetCurrentlyOnThisObject(parent) below
            // is itself patched, and the game may route through the orbit body internally,
            // which would retrigger this postfix infinitely without this check.
            int instanceId = __instance.GetInstanceID();
            if (_redirecting.Contains(instanceId))
                return;

            var current = __instance.CurrentlyOnThisObject;
            if (current == null || !string.Equals(current.objectTypes.ToString(), "Orbit", StringComparison.OrdinalIgnoreCase))
                return;

            var parent = current.parentObjectInfo;
            if (parent == null || !BodyPatcher.BodyHasRemovedOrbit(parent))
                return;

            try
            {
                _redirecting.Add(instanceId);
                Plugin.Log.LogInfo($"[BodyPatcher] Redirecting '{__instance.GetSpacecraftName()}' from hidden orbit '{current.ObjectName}' to '{parent.ObjectName}'.");
                __instance.SetCurrentlyOnThisObject(parent);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] Failed to redirect spacecraft '{__instance.GetSpacecraftName()}' off hidden orbit '{current.ObjectName}': {ex.Message}");
            }
            finally
            {
                _redirecting.Remove(instanceId);
            }
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "NeedVehicleToLaunch")]
    static class PatchObjectInfoNeedVehicleToLaunchRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo __instance, ref bool __result)
        {
            if (__instance == null || !BodyPatcher.BodyHasRemovedOrbit(__instance))
                return true;

            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    static class PatchPMMissionParameterStartHermesCaseRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return PMMissionParameterType?.GetProperty("StartHermesCase", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
        }

        static void Postfix(object __instance, ref ObjectInfo __result)
        {
            if (__instance == null || PMMissionParameterType == null || StartProperty == null)
                return;

            try
            {
                ObjectInfo start = StartProperty.GetValue(__instance) as ObjectInfo;
                if (start == null || !BodyPatcher.BodyHasRemovedOrbit(start))
                    return;

                // Match the asteroid-style planner path: no separate hidden orbit source.
                __result = start;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] StartHermesCase override failed: {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    static class PatchPMMissionParameterCheckLVFullListOrNoneRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo TargetProperty = PMMissionParameterType?.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo ScField = PMMissionParameterType?.GetField("sc", BindingFlags.NonPublic | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(PMMissionParameterType, "CheckLVFullListOrNone");
        }

        static bool Prefix(object __instance, ref bool __result)
        {
            if (__instance == null || PMMissionParameterType == null)
                return true;

            ObjectInfo start = GetPlannerObjectInfoSafe(StartProperty, __instance);
            if (start == null || !BodyPatcher.BodyHasRemovedOrbit(start))
                return true;

            try
            {
                ISpacecraftInfo sc = ScField?.GetValue(__instance) as ISpacecraftInfo;
                if (sc != null && sc.GetTypeSpaceCraft() != null && sc.GetTypeSpaceCraft().LowOrbitContainer)
                    return true;

                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] CheckLVFullListOrNone override failed for '{start?.ObjectName ?? "NULL"}': {ex.Message}");
            }

            return true;
        }

        static ObjectInfo GetPlannerObjectInfoSafe(PropertyInfo property, object pmp)
        {
            try
            {
                return property?.GetValue(pmp) as ObjectInfo;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch]
    static class PatchPMMissionParameterCheckLVRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo TargetProperty = PMMissionParameterType?.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo ScField = PMMissionParameterType?.GetField("sc", BindingFlags.NonPublic | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(PMMissionParameterType, "CheckLV");
        }

        static bool Prefix(object __instance, ref bool __result)
        {
            if (__instance == null || PMMissionParameterType == null)
                return true;

            ObjectInfo start = GetPlannerObjectInfoSafe(StartProperty, __instance);
            if (start == null || !BodyPatcher.BodyHasRemovedOrbit(start))
                return true;

            try
            {
                ISpacecraftInfo sc = ScField?.GetValue(__instance) as ISpacecraftInfo;
                if (sc == null || sc.GetTypeSpaceCraft() == null)
                    return true;

                if (sc.GetTypeSpaceCraft().LowOrbitContainer)
                    return true;

                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] CheckLV override failed for '{start?.ObjectName ?? "NULL"}': {ex.Message}");
            }

            return true;
        }

        static ObjectInfo GetPlannerObjectInfoSafe(PropertyInfo property, object pmp)
        {
            try
            {
                return property?.GetValue(pmp) as ObjectInfo;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch]
    static class PatchPMMissionParameterNeedLvHermesCaseCargoRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo ScField = PMMissionParameterType?.GetField("sc", BindingFlags.NonPublic | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(PMMissionParameterType, "NeedLvHermesCaseCargo");
        }

        static bool Prefix(object __instance, ref bool __result)
        {
            if (__instance == null || PMMissionParameterType == null)
                return true;

            ObjectInfo start = GetPlannerObjectInfoSafe(StartProperty, __instance);
            if (start == null || !BodyPatcher.BodyHasRemovedOrbit(start))
                return true;

            try
            {
                ISpacecraftInfo sc = ScField?.GetValue(__instance) as ISpacecraftInfo;
                if (sc == null || sc.GetTypeSpaceCraft() == null)
                    return true;

                if (sc.GetTypeSpaceCraft().LowOrbitContainer)
                    return true;

                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] NeedLvHermesCaseCargo override failed for '{start?.ObjectName ?? "NULL"}': {ex.Message}");
            }

            return true;
        }

        static ObjectInfo GetPlannerObjectInfoSafe(PropertyInfo property, object pmp)
        {
            try
            {
                return property?.GetValue(pmp) as ObjectInfo;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch]
    static class PatchPMTabScheduleCalculateCostStartRemovedOrbitBodies
    {
        static readonly Type PMTabScheduleType = AccessTools.TypeByName("Game.UI.Windows.Elements.PlanMissionElements.PMTabSchedule");
        static readonly Type PlanMissionWindowType = AccessTools.TypeByName("Game.UI.Windows.Windows.PlanMissionWindow");
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter");
        static readonly FieldInfo PlanMissionWindowField = AccessTools.Field(PMTabScheduleType, "planMissionWindow") ?? AccessTools.Field(PMTabScheduleType?.BaseType, "planMissionWindow");
        static readonly PropertyInfo PMMissionParameterProperty = PlanMissionWindowType?.GetProperty("PMMissionParameter", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo LVProperty = PMMissionParameterType?.GetProperty("LV", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo ScField = PMMissionParameterType?.GetField("sc", BindingFlags.NonPublic | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(PMTabScheduleType, "CalculateCostStart");
        }

        static void Postfix(object __instance, ref double __result, ref bool launchCostZero)
        {
            if (__instance == null || PMTabScheduleType == null || PMMissionParameterType == null)
                return;

            try
            {
                object planMissionWindow = PlanMissionWindowField?.GetValue(__instance);
                object pmp = PMMissionParameterProperty?.GetValue(planMissionWindow);
                if (pmp == null)
                    return;

                ObjectInfo start = StartProperty?.GetValue(pmp) as ObjectInfo;
                if (start == null || !BodyPatcher.BodyHasRemovedOrbit(start))
                    return;

                object lv = LVProperty?.GetValue(pmp);
                if (lv != null)
                    return;

                ISpacecraftInfo sc = ScField?.GetValue(pmp) as ISpacecraftInfo;
                if (sc?.GetTypeSpaceCraft() == null || !sc.GetTypeSpaceCraft().OrbitSC || sc.GetTypeSpaceCraft().LowOrbitContainer)
                    return;

                launchCostZero = true;
                __result = 0.0;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] CalculateCostStart override failed: {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    static class PatchPMMissionParameterSetFuelNeedRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("Game.UI.Windows.Elements.PlanMissionElements.PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo LVProperty = PMMissionParameterType?.GetProperty("LV", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo ScField = PMMissionParameterType?.GetField("sc", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo FuelNeedToGetFuelToOrbitField = PMMissionParameterType?.GetField("fuelNeedToGetFuelToOrbit", BindingFlags.NonPublic | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(PMMissionParameterType, "SetFuelNeed");
        }

        static void Postfix(object __instance)
        {
            if (__instance == null || PMMissionParameterType == null)
                return;

            try
            {
                ObjectInfo start = StartProperty?.GetValue(__instance) as ObjectInfo;
                if (start == null || !BodyPatcher.BodyHasRemovedOrbit(start))
                    return;

                object lv = LVProperty?.GetValue(__instance);
                if (lv != null)
                    return;

                ISpacecraftInfo sc = ScField?.GetValue(__instance) as ISpacecraftInfo;
                if (sc?.GetTypeSpaceCraft() == null || !sc.GetTypeSpaceCraft().OrbitSC || sc.GetTypeSpaceCraft().LowOrbitContainer)
                    return;

                FuelNeedToGetFuelToOrbitField?.SetValue(__instance, 0.0);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] SetFuelNeed override failed: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "GetLayerLabel")]
    static class PatchObjectInfoGetLayerLabelRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo __instance, ref LabelsManager.ELayerLabel __result)
        {
            if (__instance == null || !BodyPatcher.BodyHasRemovedOrbit(__instance))
                return true;

            if (__instance.parentObjectInfo != null)
            {
                __result = LabelsManager.ELayerLabel.Moon;
                return false;
            }

            __result = LabelsManager.ELayerLabel.Planet;
            return false;
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "CheckEarthMoonCase")]
    static class PatchObjectInfoCheckEarthMoonCaseRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo objectInfoStart, ObjectInfo objectInfoTarget, ref bool __result)
        {
            bool startRemoved = BodyPatcher.BodyHasRemovedOrbit(objectInfoStart);
            bool targetRemoved = BodyPatcher.BodyHasRemovedOrbit(objectInfoTarget);
            if (!startRemoved && !targetRemoved)
                return true;

            __result = CheckMoonCaseLikeStock(objectInfoStart, objectInfoTarget);
            return false;
        }

        static bool CheckMoonCaseLikeStock(ObjectInfo objectInfoStart, ObjectInfo objectInfoTarget)
        {
            try
            {
                if (IsMoonLike(objectInfoTarget))
                {
                    if (objectInfoStart == objectInfoTarget)
                        return false;

                    if (GetParentNBody(objectInfoStart) == GetParentNBody(objectInfoTarget))
                        return true;
                }
                else if (IsMoonLike(objectInfoStart))
                {
                    if (objectInfoTarget?.NBody == objectInfoStart?.parentObjectInfo?.NBody)
                        return true;

                    if (GetParentNBody(objectInfoTarget) == objectInfoStart?.parentObjectInfo?.NBody)
                        return true;
                }
                else if (objectInfoTarget?.parentObjectInfo != null && objectInfoTarget.parentObjectInfo.NBody?.GetObjectInfo()?.parentObjectInfo != null)
                {
                    if (IsOrbitLike(objectInfoTarget))
                    {
                        if (objectInfoStart?.NBody == objectInfoTarget.parentObjectInfo.NBody.GetObjectInfo().parentObjectInfo.NBody)
                            return true;
                    }
                    else if (IsOrbitLike(objectInfoStart) && objectInfoTarget?.NBody == objectInfoStart.parentObjectInfo.NBody.GetObjectInfo().parentObjectInfo.NBody)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] CheckEarthMoonCase custom evaluation failed: {ex.Message}");
            }

            return false;
        }

        static bool IsMoonLike(ObjectInfo body)
        {
            if (body == null)
                return false;

            if (string.Equals(body.objectTypes.ToString(), "Moons", StringComparison.OrdinalIgnoreCase))
                return true;

            return BodyPatcher.BodyHasRemovedOrbit(body) && body.parentObjectInfo != null;
        }

        static bool IsOrbitLike(ObjectInfo body)
        {
            return body != null && string.Equals(body.objectTypes.ToString(), "Orbit", StringComparison.OrdinalIgnoreCase);
        }

        static NBody GetParentNBody(ObjectInfo body)
        {
            return body?.parentObjectInfo?.NBody;
        }
    }

    [HarmonyPatch]
    static class PatchPMMissionParameterChangeRealToCalculationTargetRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo TargetProperty = PMMissionParameterType?.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo StartCalculationField = PMMissionParameterType?.GetField("objectInfoStartCalculation", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo TargetCalculationField = PMMissionParameterType?.GetField("objectInfoTargetCalculation", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo CenterBodyField = PMMissionParameterType?.GetField("centerBodyLambertPorkchop", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo OrbitCaseField = PMMissionParameterType?.GetField("orbitCase", BindingFlags.NonPublic | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(PMMissionParameterType, "ChangeRealToCalculationTarget");
        }

        static void Postfix(object __instance)
        {
            if (__instance == null || PMMissionParameterType == null)
                return;

            var start = GetPlannerObjectInfoSafe(StartProperty, __instance);
            var target = GetPlannerObjectInfoSafe(TargetProperty, __instance);
            bool startRemoved = BodyPatcher.BodyHasRemovedOrbit(start);
            bool targetRemoved = BodyPatcher.BodyHasRemovedOrbit(target);
            if (!startRemoved && !targetRemoved)
                return;

            var normalizedStart = NormalizeCalculationBody(start, target);
            var normalizedTarget = NormalizeCalculationBody(target, start);
            if (normalizedStart == null || normalizedTarget == null || normalizedStart.NBody == null || normalizedTarget.NBody == null)
            {
                return;
            }

            var startOrbit = normalizedStart.NBody.gameObject != null ? normalizedStart.NBody.gameObject.GetComponent<OrbitUniversal>() : null;
            var targetOrbit = normalizedTarget.NBody.gameObject != null ? normalizedTarget.NBody.gameObject.GetComponent<OrbitUniversal>() : null;
            if (startOrbit == null || targetOrbit == null)
            {
                return;
            }

            bool orbitCase = ComputeOrbitCase(normalizedStart, normalizedTarget, startOrbit, targetOrbit);
            NBody centerBody = targetOrbit.centerNbody ?? startOrbit.centerNbody;

            StartCalculationField?.SetValue(__instance, normalizedStart);
            TargetCalculationField?.SetValue(__instance, normalizedTarget);
            CenterBodyField?.SetValue(__instance, centerBody);
            OrbitCaseField?.SetValue(__instance, orbitCase);

        }

        static ObjectInfo NormalizeCalculationBody(ObjectInfo body, ObjectInfo counterpart)
        {
            if (body == null)
                return null;

            if (BodyPatcher.BodyHasRemovedOrbit(body))
            {
                if (body.parentObjectInfo == null)
                    return body;

                if (ShouldPreserveRemovedMoonForMoonCase(body, counterpart))
                    return body;

                if (CounterpartOrbitMatchesRemovedBodyParent(counterpart, body.parentObjectInfo))
                    return body;

                return body.parentObjectInfo;
            }

            if (BodyPatcher.BodyHasRemovedOrbit(counterpart) && counterpart.parentObjectInfo == body)
            {
                try
                {
                    if (body.LowOrbitCustom != null)
                    {
                        ObjectInfo orbitObject = body.LowOrbitCustom.GetObjectInfo();
                        if (orbitObject != null)
                            return orbitObject;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[BodyPatcher] Failed to normalize parent body '{body.ObjectName}' to low orbit for removed-orbit counterpart '{counterpart.ObjectName}': {ex.Message}");
                }
            }

            if (IsOrbitObjectInfo(body) && BodyPatcher.BodyHasRemovedOrbit(counterpart))
            {
                // Keep the orbit body only when it directly orbits the same body as the removed
                // counterpart's parent (e.g. MARS [ORBIT] → PHOBOS: both centered on MARS).
                // Do NOT use the grandparent check here — CALLISTO [ORBIT] orbits CALLISTO (not
                // Jupiter), so it must be unwound to CALLISTO to share a center with AMALTHEA.
                var bodyOrbit = GetOrbitUniversal(body);
                if (bodyOrbit != null && counterpart.parentObjectInfo?.NBody != null &&
                    bodyOrbit.centerNbody == counterpart.parentObjectInfo.NBody)
                    return body;

                ObjectInfo centerBody = GetOrbitCenterObjectInfo(body);
                if (centerBody != null)
                    return centerBody;
            }

            return body;
        }

        static bool ShouldPreserveRemovedMoonForMoonCase(ObjectInfo removedBody, ObjectInfo counterpart)
        {
            if (removedBody == null || counterpart == null || removedBody.parentObjectInfo == null)
                return false;

            if (ReferenceEquals(counterpart, removedBody.parentObjectInfo))
                return true;

            if (BodyPatcher.BodyHasRemovedOrbit(counterpart) && counterpart.parentObjectInfo == removedBody.parentObjectInfo)
                return true;

            if (counterpart.parentObjectInfo == removedBody.parentObjectInfo)
                return true;

            return false;
        }

        static bool CounterpartOrbitMatchesRemovedBodyParent(ObjectInfo counterpart, ObjectInfo removedBodyParent)
        {
            if (counterpart == null || removedBodyParent?.NBody == null || !IsOrbitObjectInfo(counterpart))
                return false;

            OrbitUniversal counterpartOrbit = GetOrbitUniversal(counterpart);
            if (counterpartOrbit == null) return false;

            if (counterpartOrbit.centerNbody == removedBodyParent.NBody)
                return true;

            // One level up: handles the case where the counterpart is an orbit companion of a
            // moon that itself orbits the removed body's parent system. Example: CALLISTO [ORBIT]
            // orbits CALLISTO, which orbits JUPITER — so it shares AMALTHEA's parent (JUPITER).
            try
            {
                var grandparentOrbit = counterpartOrbit.centerNbody?.gameObject.GetComponent<OrbitUniversal>();
                if (grandparentOrbit != null && grandparentOrbit.centerNbody == removedBodyParent.NBody)
                    return true;
            }
            catch { }

            return false;
        }

        static bool IsOrbitObjectInfo(ObjectInfo body)
        {
            return body != null && string.Equals(body.objectTypes.ToString(), "Orbit", StringComparison.OrdinalIgnoreCase);
        }

        static bool ComputeOrbitCase(ObjectInfo normalizedStart, ObjectInfo normalizedTarget, OrbitUniversal startOrbit, OrbitUniversal targetOrbit)
        {
            if (normalizedStart == null || normalizedTarget == null || startOrbit == null || targetOrbit == null)
                return false;

            if (ReferenceEquals(normalizedStart, normalizedTarget))
                return true;

            // Mirror the stock local-transfer rules from PMMissionParameter.ChangeRealToCalculationTarget
            // so removed-orbit bodies still classify local parent-orbit hops as orbit cases.
            if (startOrbit.GetNBody() == targetOrbit.centerNbody && IsOrbitObjectInfo(normalizedTarget))
                return true;

            if (targetOrbit.GetNBody() == startOrbit.centerNbody && IsOrbitObjectInfo(normalizedStart))
                return true;

            if (targetOrbit.centerNbody == startOrbit.centerNbody
                && targetOrbit.centerNbody != null
                && IsOrbitObjectInfo(normalizedStart)
                && IsOrbitObjectInfo(normalizedTarget))
                return true;

            return false;
        }

        static ObjectInfo GetOrbitCenterObjectInfo(ObjectInfo orbitBody)
        {
            OrbitUniversal orbit = GetOrbitUniversal(orbitBody);
            if (orbit?.centerNbody == null)
                return null;

            try
            {
                return orbit.centerNbody.GetObjectInfo();
            }
            catch
            {
                return null;
            }
        }

        static OrbitUniversal GetOrbitUniversal(ObjectInfo body)
        {
            try
            {
                return body?.NBody?.gameObject != null ? body.NBody.gameObject.GetComponent<OrbitUniversal>() : null;
            }
            catch
            {
                return null;
            }
        }

        static ObjectInfo GetPlannerObjectInfoSafe(PropertyInfo property, object pmp)
        {
            try
            {
                return property?.GetValue(pmp) as ObjectInfo;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(ObjectInfoGroups), "SetAsteroidCommetsIfNotHaveGroup")]
    static class PatchObjectInfoGroupsSetAsteroidCommetsIfNotHaveGroupRemovedOrbitBodies
    {
        static void Postfix(ObjectInfoGroups __instance)
        {
            if (__instance == null || __instance.objectInGroup == null || __instance.objectInGroup.Count == 0)
                return;

            for (int i = __instance.objectInGroup.Count - 1; i >= 0; i--)
            {
                var objectInfo = __instance.objectInGroup[i];
                if (!BodyPatcher.BodyHasRemovedOrbit(objectInfo))
                    continue;

                __instance.objectInGroup.RemoveAt(i);
                if (objectInfo != null && objectInfo.parentObjectInfoGropup == __instance)
                    objectInfo.parentObjectInfoGropup = null;

                Plugin.Log.LogInfo($"[BodyPatcher] Removed '{objectInfo?.ObjectName ?? "NULL"}' from asteroid object group for moon-like presentation.");
            }
        }
    }

    [HarmonyPatch(typeof(ChoseFacilityWindow), "FilterByObjectInfo")]
    static class PatchChoseFacilityWindowFilterByObjectInfoRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo objectInfo, ref object __state)
        {
            __state = null;
            if (objectInfo == null || !BodyPatcher.BodyHasRemovedOrbit(objectInfo))
                return true;

            var field = AccessTools.Field(typeof(ObjectInfo), "objectTypes");
            if (field == null)
                return true;

            try
            {
                __state = field.GetValue(objectInfo);
                var asteroidEnum = Enum.Parse(field.FieldType, "Asteroid", ignoreCase: true);
                field.SetValue(objectInfo, asteroidEnum);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] Failed to present '{objectInfo.ObjectName}' as asteroid-like in facility picker: {ex.Message}");
                __state = null;
            }

            return true;
        }

        static void Postfix(ObjectInfo objectInfo, object __state)
        {
            if (objectInfo == null || __state == null)
                return;

            var field = AccessTools.Field(typeof(ObjectInfo), "objectTypes");
            if (field == null)
                return;

            try
            {
                field.SetValue(objectInfo, __state);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] Failed to restore objectTypes after facility picker for '{objectInfo.ObjectName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// New modded spacecraft borrow a baked donor prefab. The donor prefab's Spacecraft
    /// component often still has its original serialized spacecraftType reference, and the
    /// base game only rewrites that reference for non-base completed hull designs.
    ///
    /// For ordinary modded SpacecraftType entries, that leaves the built ship identifying
    /// as the donor in the object-info rocket list after construction completes.
    ///
    /// This postfix force-rebinds the spawned runtime object back to the requested
    /// SpacecraftType so built ships keep their modded identity.
    /// </summary>
    [HarmonyPatch(typeof(global::ShipManager), "ConstructShipOnPlanet",
        new Type[] { typeof(Game.Info.ObjectInfo), typeof(SpacecraftType), typeof(Data.SpacecraftConstructData), typeof(Game.Company) })]
    static class PatchShipManagerConstructShipOnPlanet
    {
        static readonly FieldInfo _ooiFacilityField = typeof(ObjectOnOrbit)
            .GetField("facilityBaseDescriptor", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _ooiSpacecraftField = typeof(ObjectOnOrbit)
            .GetField("spacecraftType", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(SpacecraftType spacecraftType, ref object __result)
        {
            if (spacecraftType == null || __result == null)
                return;

            var spacecraft = __result as Spacecraft;
            if (spacecraft == null)
                return;

            if (spacecraft.spacecraftType != spacecraftType)
            {
                Plugin.Log.LogInfo($"[VehicleFix] Rebinding spawned spacecraft from '{spacecraft.spacecraftType?.ID ?? "null"}' to '{spacecraftType.ID}'.");
                spacecraft.spacecraftType = spacecraftType;
            }

            try
            {
                var orbitMarkers = spacecraft.GetComponentsInChildren<ObjectOnOrbit>(true);
                foreach (var marker in orbitMarkers)
                {
                    _ooiFacilityField?.SetValue(marker, null);
                    _ooiSpacecraftField?.SetValue(marker, spacecraftType);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[VehicleFix] Failed to retarget ObjectOnOrbit markers for '{spacecraftType.ID}': {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ResourceDefinition), "get_IconString")]
    static class PatchResourceDefinitionGetIconString
    {
        static void Postfix(ResourceDefinition __instance, ref string __result)
        {
            if (ResourceCreator.UsesNamedSprite(__instance))
                __result = ResourceCreator.GetIconString(__instance);
        }
    }

    [HarmonyPatch(typeof(ResourceDefinition), "get_IconWithLinkString")]
    static class PatchResourceDefinitionGetIconWithLinkString
    {
        static void Postfix(ResourceDefinition __instance, ref string __result)
        {
            if (ResourceCreator.UsesNamedSprite(__instance))
                __result = ResourceCreator.GetIconWithLinkString(__instance);
        }
    }

    [HarmonyPatch(typeof(MyIDScriptableObject), "GetText")]
    static class PatchMyIDScriptableObjectGetText
    {
        static void Postfix(MyIDScriptableObject __instance, bool longText, bool firstIcon, string _highLightColor, bool colorIcon, string _highLightColorSprite, bool addSpace, ref string __result)
        {
            var resource = __instance as ResourceDefinition;
            if (resource == null || !ResourceCreator.UsesNamedSprite(resource))
                return;

            __result = ResourceCreator.FormatResourceLink(resource, longText, firstIcon, _highLightColor, colorIcon, _highLightColorSprite, addSpace);
        }
    }

    [HarmonyPatch]
    static class PatchMyExtensionsTupleToString
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Extensions.MyExtensions");
            return AccessTools.Method(t, "ToString", new[] { typeof(ValueTuple<ResourceDefinition, double>), typeof(string), typeof(double) });
        }

        static void Postfix(ValueTuple<ResourceDefinition, double> rb, string format, double multiplier, ref string __result)
        {
            if (!ResourceCreator.UsesNamedSprite(rb.Item1))
                return;
            __result = ResourceCreator.FormatResourceAmount(rb.Item1, rb.Item2, multiplier);
        }
    }

    [HarmonyPatch(typeof(global::DropDownEnum), "SetOptionsAwake")]
    static class PatchDropDownEnumSetOptionsAwake
    {
        static readonly FieldInfo _dropDownTypeFi = typeof(global::DropDownEnum).GetField("dropDownType", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _shortTextFi = typeof(global::DropDownEnum).GetField("shortText", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _emptyTextAllFi = typeof(global::DropDownEnum).GetField("emptyTextAll", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo _allResourcesPi = typeof(global::DropDownEnum).GetProperty("AllResourceDefinitions", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(global::DropDownEnum __instance)
        {
            if (_dropDownTypeFi == null || _allResourcesPi == null || __instance.dropDown == null)
                return;
            if (!string.Equals(_dropDownTypeFi.GetValue(__instance)?.ToString(), "resorce", StringComparison.OrdinalIgnoreCase))
                return;

            var resources = _allResourcesPi.GetValue(__instance, null) as List<ResourceDefinition>;
            if (resources == null || resources.Count != __instance.dropDown.options.Count)
                return;

            bool shortText = _shortTextFi != null && (bool)_shortTextFi.GetValue(__instance);
            bool emptyTextAll = _emptyTextAllFi != null && (bool)_emptyTextAllFi.GetValue(__instance);
            for (int i = 0; i < resources.Count; i++)
            {
                var rd = resources[i];
                if (rd == null || __instance.dropDown.options[i] == null)
                    continue;
                if (rd.ID == "id_resource_empty")
                {
                    __instance.dropDown.options[i].text = emptyTextAll ? Language.LEManager.Get("DropDownEnum.ALL") : Language.LEManager.Get("id_resource_empty");
                    continue;
                }
                __instance.dropDown.options[i].text = shortText
                    ? ResourceCreator.GetIconString(rd)
                    : ResourceCreator.GetIconString(rd) + " " + Language.LEManager.Get(rd.ID);
            }
            __instance.dropDown.RefreshShownValue();
        }
    }

    [HarmonyPatch(typeof(Game.UI.Windows.Elements.MarketOfferElements.OfferUIRow), "SetData")]
    static class PatchOfferUIRowSetData
    {
        static readonly FieldInfo _rdTextFi = typeof(Game.UI.Windows.Elements.MarketOfferElements.OfferUIRow).GetField("rdText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(Game.UI.Windows.Elements.MarketOfferElements.OfferUIRow __instance)
        {
            var offer = __instance.Offer;
            if (offer?.Rd == null) return;
            var rdText = _rdTextFi?.GetValue(__instance) as TextMeshProUGUI;
            if (rdText != null)
                rdText.text = ResourceCreator.GetIconString(offer.Rd);
        }
    }

    [HarmonyPatch(typeof(GroundFacilityDescriptor), "get_SpecialCapabilities")]
    static class PatchGroundFacilityDescriptorGetSpecialCapabilities
    {
        static void Postfix(GroundFacilityDescriptor __instance, ref string __result)
        {
            if (__instance == null)
                return;

            if (string.IsNullOrEmpty(__result))
            {
                string customCapabilities = Language.LEManager.Get(__instance.ID + FacilityBaseDescriptor.CapabilitiesString, "");
                if (!string.IsNullOrEmpty(customCapabilities))
                    __result = customCapabilities;
            }

            if (string.IsNullOrEmpty(__result) || __instance.energyProductionData?.input == null || __instance.energyProductionData.input.Length == 0)
                return;

            foreach (var item in __instance.energyProductionData.input)
            {
                if (item?.resource == null || !ResourceCreator.UsesNamedSprite(item.resource))
                    continue;
                string oldIcon = $"<sprite index={item.resource.IdSpritAttlastextMeshPro}>";
                string newIcon = ResourceCreator.GetIconString(item.resource);
                __result = __result.Replace(oldIcon, newIcon);
            }
        }
    }

    [HarmonyPatch(typeof(UIRowFacility), "GetTooltip")]
    static class PatchChoseFacilityRowTooltip
    {
        static readonly FieldInfo _rowDataField = typeof(UIRowFacility)
            .GetField("curentRowFacilityData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _isDisabledField = typeof(UIRowFacility)
            .GetField("isDisabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(UIRowFacility __instance, ref (string, List<(string, string)>, string) __result)
        {
            var rowData = _rowDataField?.GetValue(__instance) as RowFacilityData;
            var descriptor = rowData?.FacilityDescriptor;
            if (descriptor == null)
                return;

            string baseTooltip = descriptor.Tooltip();
            if (string.IsNullOrWhiteSpace(__result.Item1))
            {
                __result.Item1 = baseTooltip;
                return;
            }

            bool isDisabled = _isDisabledField != null && (bool)_isDisabledField.GetValue(__instance);
            if (!isDisabled)
                return;

            if (!string.Equals(__result.Item1, baseTooltip, StringComparison.Ordinal))
                __result.Item1 = baseTooltip + "\n\n" + __result.Item1;
        }

        static Exception Finalizer(UIRowFacility __instance, Exception __exception, ref (string, List<(string, string)>, string) __result)
        {
            if (__exception == null)
                return null;

            var rowData = _rowDataField?.GetValue(__instance) as RowFacilityData;
            var descriptor = rowData?.FacilityDescriptor;
            string id = descriptor?.ID ?? "<unknown>";
            Plugin.Log.LogError($"[TooltipFix] Facility tooltip crashed for '{id}': {__exception}");

            if (descriptor == null)
            {
                __result.Item1 = Language.LEManager.Get("ToolTip_empty");
                return null;
            }

            string name = Language.LEManager.Get(id, id).ToUpper();
            string description = Language.LEManager.Get(id + "_Description", string.Empty) ?? string.Empty;
            string capabilities = Language.LEManager.Get(id + FacilityBaseDescriptor.CapabilitiesString, string.Empty) ?? string.Empty;

            string text = descriptor.TooltipStart
                + "<size=14><font=\"Oxanium-Medium SDF\"><b><color=white>" + name + "</color></b></font></size>\n\n"
                + description;

            if (!string.IsNullOrWhiteSpace(capabilities))
                text += "\n\n" + capabilities;

            __result.Item1 = text;
            return null;
        }
    }

    [HarmonyPatch(typeof(AllFacility), "SetImgType")]
    static class PatchAllFacilitySetImgTypeTooltipImage
    {
        static void Postfix(Image imgType)
        {
            TooltipImageFix.Repair(imgType);
        }
    }

    [HarmonyPatch(typeof(AllFacility), "SetImgTypeSC")]
    static class PatchAllFacilitySetImgTypeSCTooltipImage
    {
        static void Postfix(Image imgType)
        {
            TooltipImageFix.Repair(imgType);
        }
    }

    /// <summary>
    /// Injects a small version badge into the bottom-right corner of the main menu.
    /// Patches MenuSceneUI.Start() which runs after the menu canvas is fully built.
    /// </summary>
    [HarmonyPatch(typeof(Contract), "GiveRewards",
        new[] { typeof(ContractDefinition), typeof(Contract), typeof(Company), typeof(List<Reward>), typeof(bool), typeof(bool) })]
    static class PatchContractGiveRewardsAiOnly
    {
        static void Prefix(Company company, ref List<Reward> rewards)
        {
            if (ContractCreator.AiOnlyRewards.Count == 0) return;
            if (company != MonoBehaviourSingleton<GameManager>.Instance.Player) return;
            if (!rewards.Any(r => ContractCreator.AiOnlyRewards.Contains(r))) return;
            rewards = rewards.Where(r => !ContractCreator.AiOnlyRewards.Contains(r)).ToList();
        }
    }

    [HarmonyPatch(typeof(CompanyObjectiveData), "MarkAsComplete")]
    static class PatchContractMarkAsComplete
    {
        static void Prefix(CompanyObjectiveData __instance)
        {
            try
            {
                var obj = __instance.Objective;
                var cd = __instance.ContractData?.contractDefinition;
                Plugin.Log.LogInfo($"[ContractDebug] MarkAsComplete called — contract={cd?.ID}, objective={obj?.ID}, type={obj?.objectiveType}, howMuch={obj?.howMuch}, howMuchCurrent={__instance.howMuchCurrent}, company={__instance.ContractData?.company?.ID}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ContractDebug] MarkAsComplete prefix error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ContractManager), "ObjectiveOnOnCompleteStatic")]
    static class PatchContractObjectiveOnComplete
    {
        static void Prefix(CompanyObjectiveData cod)
        {
            try
            {
                var cm = MonoBehaviourSingleton<ContractManager>.Instance;
                var company = cod.ContractData?.company;
                Plugin.Log.LogInfo($"[ContractDebug] ObjectiveOnOnCompleteStatic — source contract={cod.ContractData?.contractDefinition?.ID}, company={company?.ID}");
                foreach (var contract in cm.allContracts)
                {
                    var state = contract.ContractStateForCompany(company);
                    if (state != ContractManager.EContractState.Active) continue;
                    var ccd = contract.PerCompanyContractData.ContainsKey(company) ? contract.PerCompanyContractData[company] : null;
                    if (ccd == null) { Plugin.Log.LogInfo($"[ContractDebug]   {contract.ContractDefinition.ID}: Active but no per-company data"); continue; }
                    bool allDone = ccd.ObjectivesDataList.TrueForAll(d => d.IsComplete);
                    Plugin.Log.LogInfo($"[ContractDebug]   {contract.ContractDefinition.ID}: Active, objectives={ccd.ObjectivesDataList.Count}, allComplete={allDone}");
                    foreach (var od in ccd.ObjectivesDataList)
                        Plugin.Log.LogInfo($"[ContractDebug]     obj={od.Objective?.ID} type={od.Objective?.objectiveType} isComplete={od.IsComplete}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ContractDebug] ObjectiveOnOnCompleteStatic prefix error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MenuSceneUI), "Start")]
    static class PatchMenuSceneUIStart
    {
        static void Postfix(MenuSceneUI __instance)
        {
            try
            {
                // Find the root canvas — the menu scene has one screen-space overlay canvas.
                Canvas canvas = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c.isRootCanvas) { canvas = c; break; }
                }
                if (canvas == null)
                {
                    Plugin.Log.LogWarning("[VersionBadge] No root Canvas found in menu scene.");
                    return;
                }

                var go = new GameObject("TedditVersionBadge");
                go.transform.SetParent(canvas.transform, false);
                go.transform.SetAsLastSibling(); // render on top

                var text = go.AddComponent<TextMeshProUGUI>();
                text.text = $"Teddit v{Plugin.Version}";
                text.fontSize = 18f;
                text.color = new Color(1f, 1f, 1f, 0.85f);
                text.alignment = TextAlignmentOptions.BottomRight;
                text.raycastTarget = false;

                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(1f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot     = new Vector2(1f, 0f);
                rect.sizeDelta = new Vector2(180f, 28f);
                rect.anchoredPosition = new Vector2(-12f, 12f);

                UnityEngine.Object.Destroy(go, 5f);
                Plugin.Log.LogInfo($"[VersionBadge] Injected version badge (Teddit v{Plugin.Version}).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[VersionBadge] Failed to create version badge: {ex.Message}");
            }
        }
    }
}
