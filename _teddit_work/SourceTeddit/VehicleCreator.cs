using System;
using System.Collections.Generic;
using Data.ScriptableObject;
using Game.CompanyScripts;
using Game.UI.Windows.Elements.SpaceCraftConstructElements;
using Manager;
using Newtonsoft.Json.Linq;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Creates new SpacecraftType and LaunchVehicleType ScriptableObject instances from YAML
    /// definitions and injects them into their respective AllXxx collections at runtime.
    /// Called by ScriptableObjectPatcher.RunSpacecraft / RunLaunchVehicles when an ID is
    /// not found in the game's list.
    /// </summary>
    internal static class VehicleCreator
    {
        // ── SpacecraftType ────────────────────────────────────────────────────

        internal static void CreateAndInjectSpacecraft(string id, Dictionary<string, JToken> def, string modDir)
        {
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            var donor = FindDonorSpacecraft(def, allSO, id);
            var sc = donor != null
                ? UnityEngine.Object.Instantiate(donor)
                : ScriptableObject.CreateInstance<SpacecraftType>();

            sc.name = id;
            FacilityCreator.SetField(sc, "id", id);
            FacilityCreator.SetField(sc, "toTranslate", null);
            FacilityCreator.SetField(sc, "spaceCraftConstructDefault", null);
            FacilityCreator.SetField(sc, "lockByHelpNotUse", null);
            FacilityCreator.SetField(sc, "checkIfWasFacilitySC", false);
            FacilityCreator.SetField(sc, "facilityUnlockThis", null);
            FacilityCreator.SetField(sc, "fuelType", null);
            sc.Hull = null;
            sc.cheatSC = false;

            if (donor == null)
            {
                Plugin.Log.LogWarning($"[VehicleCreator] {id}: no donor spacecraft found — spacecraftPrefab/threeDViewPrefab will be null.");
            }

            // The construction window calls type.spaceCraftConstructDefault for every list entry
            // (SpaceCraftConstructionWindow.RefreshFilter lines 369-370).  We must provide a fresh
            // instance that back-references our own SpacecraftType so the correct name/stats show.
            // DO NOT copy the donor's instance — its spacecraftType field points to the donor.
            {
                var constructData = new SpaceCraftConstructData();
                constructData.SpacecraftType = sc;
                constructData.LaunchVehicleType = null;
                FacilityCreator.SetField(sc, "spaceCraftConstructDefault", constructData);
            }

            // Sprite — rocketBackGround is what SpriteId returns, used for tooltips and the
            // build-window row icon. Fall back to the donor's sprite if no icon provided.
            Sprite sprite = LoadVehicleSprite(def, modDir, id);
            if (sprite == null && donor != null)
                sprite = donor.RocketBackGround;
            if (sprite == null)
                sprite = FallbackSprite(allSO.AllSpacecraftType.List,
                    s => FacilityCreator.FindField(s.GetType(), "rocketBackGround")?.GetValue(s) as Sprite);
            FacilityCreator.SetField(sc, "rocketBackGround", sprite);

            // Lock state
            sc.isLocked = FacilityCreator.GetVal<bool>(def, "isLocked", false);

            // Engine type (EEngineType enum — chemical / electric / nuclear / fusion / solar / none / special)
            string engineTypeStr = FacilityCreator.GetVal<string>(def, "engineType", "chemical");
            EEngineType engineType;
            if (!Enum.TryParse(engineTypeStr, ignoreCase: true, result: out engineType))
            {
                Plugin.Log.LogWarning($"[VehicleCreator] {id}: unknown engineType '{engineTypeStr}', defaulting to chemical.");
                engineType = EEngineType.chemical;
            }
            sc.engineType = engineType;

            // Boolean flags
            sc.orbitSC                          = FacilityCreator.GetVal<bool>(def, "orbitSC",                    false);
            sc.needLaunchVehicleToGoToMoon       = FacilityCreator.GetVal<bool>(def, "needLaunchVehicleToGoToMoon", true);
            FacilityCreator.SetField(sc, "canByBuildByUser",     FacilityCreator.GetVal<bool>(def, "canByBuildByUser",     true));
            FacilityCreator.SetField(sc, "buildOnlyLowOrbit",    FacilityCreator.GetVal<bool>(def, "buildOnlyLowOrbit",    false));
            FacilityCreator.SetField(sc, "lowOrbitContainer",    FacilityCreator.GetVal<bool>(def, "lowOrbitContainer",    false));
            FacilityCreator.SetField(sc, "magneticCatapult",     FacilityCreator.GetVal<bool>(def, "magneticCatapult",     false));
            FacilityCreator.SetField(sc, "solarSC",              FacilityCreator.GetVal<bool>(def, "solarSC",              false));
            FacilityCreator.SetField(sc, "solarParameter",       FacilityCreator.GetVal<float>(def, "solarParameter",      10f));
            FacilityCreator.SetField(sc, "isInterstellarShip",   FacilityCreator.GetVal<bool>(def, "isInterstellarShip",   false));
            FacilityCreator.SetField(sc, "asteroidPullingShip",  FacilityCreator.GetVal<bool>(def, "asteroidPullingShip",  false));
            FacilityCreator.SetField(sc, "constanceAcceleration",FacilityCreator.GetVal<bool>(def, "constanceAcceleration",false));
            FacilityCreator.SetField(sc, "showUIObjectInfo",     FacilityCreator.GetVal<bool>(def, "showUIObjectInfo",     true));

            // Performance floats
            FacilityCreator.SetField(sc, "mass",             FacilityCreator.GetVal<float>(def, "mass",             1000f));
            FacilityCreator.SetField(sc, "thrust",           FacilityCreator.GetVal<float>(def, "thrust",           10000f));
            FacilityCreator.SetField(sc, "exhaustV",         FacilityCreator.GetVal<float>(def, "exhaustV",         4.4f));
            FacilityCreator.SetField(sc, "cargoCapacity",    FacilityCreator.GetVal<float>(def, "cargoCapacity",    1000f));
            FacilityCreator.SetField(sc, "fuelCapacity",     FacilityCreator.GetVal<float>(def, "fuelCapacity",     1000f));
            FacilityCreator.SetField(sc, "maxLifeSupport",   FacilityCreator.GetVal<float>(def, "maxLifeSupport",   0f));
            FacilityCreator.SetField(sc, "availableDeltaV",  FacilityCreator.GetVal<float>(def, "availableDeltaV",  0f));
            FacilityCreator.SetField(sc, "reusability",      FacilityCreator.GetVal<float>(def, "reusability",      1f));
            FacilityCreator.SetField(sc, "maintenanceCostPerDay", FacilityCreator.GetVal<float>(def, "maintenanceCostPerDay", 4f));

            // maxPayload — nullable float
            JToken mpTok;
            float? maxPayload = def.TryGetValue("maxPayload", out mpTok) && mpTok.Type != JTokenType.Null
                ? (float?)mpTok.Value<float>() : null;
            FacilityCreator.SetField(sc, "maxPayload", maxPayload);

            string spacecraftFuelId = FacilityCreator.GetVal<string>(def, "fuelType", null);
            if (!string.IsNullOrEmpty(spacecraftFuelId))
            {
                var fuel = allSO.AllResourceDefinitions.GetByID(spacecraftFuelId);
                if (fuel == null)
                    Plugin.Log.LogWarning($"[VehicleCreator] {id}: unknown fuelType '{spacecraftFuelId}'.");
                else
                    FacilityCreator.SetField(sc, "fuelType", fuel);
            }

            // Build time
            FacilityCreator.SetField(sc, "timeToBuildInDays",
                FacilityCreator.GetVal<float>(def, "timeToBuildInDays", 30f));
            FacilityCreator.SetField(sc, "constructionEquipmentCountIsRequired",
                FacilityCreator.GetVal<bool>(def, "constructionEquipmentCountIsRequired", false));

            // Price
            double buildCost = FacilityCreator.GetVal<double>(def, "buildCost", 0.0);
            var resources = BuildResourceList(def, allSO, id, "[VehicleCreator]");
            FacilityCreator.SetField(sc, "priceBase", new ResourcePrice(resources, buildCost));

            // Translations
            string name = FacilityCreator.GetVal<string>(def, "name", null);
            string desc = FacilityCreator.GetVal<string>(def, "description", null);
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(desc))
                FacilityCreator.InjectTranslations(id, name, desc);

            // Inject
            InjectIntoCollection(allSO.AllSpacecraftType, sc, allSO, id);

            Plugin.Log.LogInfo($"[VehicleCreator] + spacecraft {id} (engine:{engineType}, orbitSC:{sc.orbitSC}, isLocked:{sc.isLocked})");
        }

        // ── LaunchVehicleType ─────────────────────────────────────────────────

        internal static void CreateAndInjectLaunchVehicle(string id, Dictionary<string, JToken> def, string modDir)
        {
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            var lvDonor = FindDonorLaunchVehicle(def, allSO, id);
            var lv = lvDonor != null
                ? UnityEngine.Object.Instantiate(lvDonor)
                : ScriptableObject.CreateInstance<LaunchVehicleType>();

            lv.name = id;
            FacilityCreator.SetField(lv, "id", id);
            FacilityCreator.SetField(lv, "toTranslate", null);
            FacilityCreator.SetField(lv, "spaceCraftConstructDefault", null);
            FacilityCreator.SetField(lv, "lockByHelpNotUse", null);
            FacilityCreator.SetField(lv, "checkIfWasFacilitySC", false);
            FacilityCreator.SetField(lv, "facilityUnlockThis", null);

            if (lvDonor == null)
            {
                Plugin.Log.LogWarning($"[VehicleCreator] {id}: no donor launch vehicle found — threeDViewPrefab will be null.");
            }

            // The construction window calls type.spaceCraftConstructDefault for every list entry.
            // Create a fresh instance pointing back to our own LaunchVehicleType so the window
            // shows the correct name/stats instead of the donor's.
            {
                var constructData = new SpaceCraftConstructData();
                constructData.LaunchVehicleType = lv;
                constructData.SpacecraftType = null;
                FacilityCreator.SetField(lv, "spaceCraftConstructDefault", constructData);
            }

            // Sprite
            Sprite sprite = LoadVehicleSprite(def, modDir, id);
            if (sprite == null && lvDonor != null)
                sprite = FacilityCreator.FindField(lvDonor.GetType(), "rocketBackGround")?.GetValue(lvDonor) as Sprite;
            if (sprite == null)
                sprite = FallbackSprite(allSO.AllLaunchVehicleType.List,
                    item => FacilityCreator.FindField(item.GetType(), "rocketBackGround")?.GetValue(item) as Sprite);
            FacilityCreator.SetField(lv, "rocketBackGround", sprite);

            // Lock state
            lv.isLocked = FacilityCreator.GetVal<bool>(def, "isLocked", false);

            // Special enum (None / SeaDragon)
            string specialStr = FacilityCreator.GetVal<string>(def, "special", "None");
            LaunchVehicleType.ESpecial special;
            if (!Enum.TryParse(specialStr, ignoreCase: true, result: out special))
            {
                Plugin.Log.LogWarning($"[VehicleCreator] {id}: unknown special '{specialStr}', defaulting to None.");
                special = LaunchVehicleType.ESpecial.None;
            }
            lv.special = special;

            // Boolean flags
            lv.canSendHuman = FacilityCreator.GetVal<bool>(def, "canSendHuman", true);
            FacilityCreator.SetField(lv, "forCycleMission", FacilityCreator.GetVal<bool>(def, "forCycleMission", false));
            FacilityCreator.SetField(lv, "fakeForFacility",  FacilityCreator.GetVal<bool>(def, "fakeForFacility",  false));

            // Performance fields (all public on LaunchVehicleType)
            lv.maxPayload  = FacilityCreator.GetVal<float>(def, "maxPayload",  1000f);
            lv.maxFuelLoad = FacilityCreator.GetVal<float>(def, "maxFuelLoad", 10000f);
            lv.costLaunch  = FacilityCreator.GetVal<float>(def, "costLaunch",  1000f);
            lv.exhaustV    = FacilityCreator.GetVal<float>(def, "exhaustV",    4.4f);
            lv.reusability = FacilityCreator.GetVal<float>(def, "reusability", 1f);
            FacilityCreator.SetField(lv, "maintenanceCostPerDay",
                FacilityCreator.GetVal<float>(def, "maintenanceCostPerDay", 4f));

            // canBuyMaxPayload — nullable float gravity threshold
            JToken cmpTok;
            float? canBuyMaxPayload = def.TryGetValue("canBuyMaxPayload", out cmpTok) && cmpTok.Type != JTokenType.Null
                ? (float?)cmpTok.Value<float>() : null;
            FacilityCreator.SetField(lv, "canBuyMaxPayload", canBuyMaxPayload);

            string launchFuelId = FacilityCreator.GetVal<string>(def, "fuelType", null);
            if (!string.IsNullOrEmpty(launchFuelId))
            {
                var fuel = allSO.AllResourceDefinitions.GetByID(launchFuelId);
                if (fuel == null)
                    Plugin.Log.LogWarning($"[VehicleCreator] {id}: unknown fuelType '{launchFuelId}'.");
                else
                    FacilityCreator.SetField(lv, "fuelTypeOnStart", fuel);
            }

            // Build time
            FacilityCreator.SetField(lv, "timeToBuildInDays",
                FacilityCreator.GetVal<float>(def, "timeToBuildInDays", 30f));
            FacilityCreator.SetField(lv, "constructionEquipmentCountIsRequired",
                FacilityCreator.GetVal<bool>(def, "constructionEquipmentCountIsRequired", false));

            // canBuildParameter — required non-null by CanBuildOnThisPlanet
            FacilityCreator.SetField(lv, "canBuildParameter", new CanBuildParameter());

            // Price
            double buildCost = FacilityCreator.GetVal<double>(def, "buildCost", 0.0);
            var resources = BuildResourceList(def, allSO, id, "[VehicleCreator]");
            FacilityCreator.SetField(lv, "priceBase", new ResourcePrice(resources, buildCost));

            // Translations
            string name = FacilityCreator.GetVal<string>(def, "name", null);
            string desc = FacilityCreator.GetVal<string>(def, "description", null);
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(desc))
                FacilityCreator.InjectTranslations(id, name, desc);

            // Inject
            InjectIntoCollection(allSO.AllLaunchVehicleType, lv, allSO, id);

            Plugin.Log.LogInfo($"[VehicleCreator] + launch vehicle {id} (special:{special}, isLocked:{lv.isLocked})");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static Sprite LoadVehicleSprite(Dictionary<string, JToken> def, string modDir, string id)
        {
            return FacilityCreator.ResolveConfiguredSprite(def, modDir, id, null);
        }

        static Sprite FallbackSprite<T>(List<T> list, Func<T, Sprite> getter) where T : class
        {
            if (list == null) return null;
            foreach (var item in list)
            {
                if (item == null) continue;
                var s = getter(item);
                if (s != null) return s;
            }
            return null;
        }

        static List<ResourcePriceOne> BuildResourceList(
            Dictionary<string, JToken> def, AllScriptableObjectManager allSO, string id, string prefix)
        {
            var resources = new List<ResourcePriceOne>();
            JToken brTok;
            if (!def.TryGetValue("buildResources", out brTok) || brTok.Type != JTokenType.Object)
                return resources;
            foreach (var kv in (JObject)brTok)
            {
                var res = allSO.AllResourceDefinitions.GetByID(kv.Key);
                if (res == null)
                {
                    Plugin.Log.LogWarning($"{prefix} {id}: unknown resource '{kv.Key}' in buildResources");
                    continue;
                }
                resources.Add(new ResourcePriceOne(res, kv.Value.Value<double>()));
            }
            return resources;
        }

        static SpacecraftType FindDonorSpacecraft(Dictionary<string, JToken> def, AllScriptableObjectManager allSO, string id)
        {
            string cloneFrom = FacilityCreator.GetVal<string>(def, "cloneFrom", null);
            if (!string.IsNullOrEmpty(cloneFrom))
            {
                var explicitDonor = allSO.AllSpacecraftType.GetByID(cloneFrom);
                if (explicitDonor == null)
                {
                    Plugin.Log.LogWarning($"[VehicleCreator] {id}: cloneFrom spacecraft '{cloneFrom}' not found.");
                }
                else if (explicitDonor.spacecraftPrefab == null)
                {
                    Plugin.Log.LogWarning($"[VehicleCreator] {id}: cloneFrom spacecraft '{cloneFrom}' has no spacecraftPrefab.");
                }
                else
                {
                    return explicitDonor;
                }
            }

            SpacecraftType fallback = null;
            foreach (var existing in allSO.AllSpacecraftType.List)
            {
                if (existing == null || existing.spacecraftPrefab == null)
                    continue;
                if (existing.Hull.HasValue && existing.Hull.Value.IsCompletedDesign)
                    continue;
                if (!existing.cheatSC)
                    return existing;
                if (fallback == null)
                    fallback = existing;
            }
            return fallback;
        }

        static LaunchVehicleType FindDonorLaunchVehicle(Dictionary<string, JToken> def, AllScriptableObjectManager allSO, string id)
        {
            string cloneFrom = FacilityCreator.GetVal<string>(def, "cloneFrom", null);
            if (!string.IsNullOrEmpty(cloneFrom))
            {
                var explicitDonor = allSO.AllLaunchVehicleType.GetByID(cloneFrom);
                if (explicitDonor == null)
                {
                    Plugin.Log.LogWarning($"[VehicleCreator] {id}: cloneFrom launch vehicle '{cloneFrom}' not found.");
                }
                else if (explicitDonor.ThreeDViewPrefab == null)
                {
                    Plugin.Log.LogWarning($"[VehicleCreator] {id}: cloneFrom launch vehicle '{cloneFrom}' has no threeDViewPrefab.");
                }
                else
                {
                    return explicitDonor;
                }
            }

            LaunchVehicleType fallback = null;
            foreach (var existing in allSO.AllLaunchVehicleType.List)
            {
                if (existing == null || existing.ThreeDViewPrefab == null)
                    continue;
                bool isFake = (bool?)FacilityCreator.FindField(existing.GetType(), "fakeForFacility")?.GetValue(existing) ?? false;
                if (!isFake)
                    return existing;
                if (fallback == null)
                    fallback = existing;
            }
            return fallback;
        }

        static void InjectIntoCollection<T>(object collection, T item,
            AllScriptableObjectManager allSO, string id) where T : MyIDScriptableObject
        {
            Type baseType = collection.GetType().BaseType;
            var listFi   = FacilityCreator.FindField(baseType, "list");
            var listNEFi = FacilityCreator.FindField(baseType, "listNotEmpty");
            if (listFi == null || listNEFi == null)
                throw new Exception($"[VehicleCreator] Could not find list fields on {collection.GetType().Name}.");

            ((System.Collections.IList)listFi.GetValue(collection)).Add(item);
            ((System.Collections.IList)listNEFi.GetValue(collection)).Add(item);

            try { allSO.AllMyIDScriptableObjects.Add(item); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[VehicleCreator] AllMyIDScriptableObjects.Add failed for {id}: {ex.Message}");
            }
        }
    }
}
