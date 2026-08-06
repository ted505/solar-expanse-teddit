using System;
using System.Collections.Generic;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Manager;

namespace Teddit
{
    /// <summary>
    /// Places existing facility descriptors as prebuilt facilities on (body, company)
    /// pairs at game start. Skips on loaded saves unless an entry sets unsafeOverride.
    /// </summary>
    internal static class FacilityPlacementInjector
    {
        internal static void Run(FacilityPlacementConfig config, ObjectInfoManager oim)
        {
            if (config == null || config.Bodies.Count == 0)
                return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            var gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (allSO == null || gm == null)
            {
                Plugin.Log.LogError("[FacilityPlacement] AllScriptableObjectManager or GameManager unavailable.");
                return;
            }

            bool isNewGameStart = GameManager.InitFromStartGameConfigurationOrFromSaveFile;
            var companies = gm.Companies;
            int placed = 0, skipped = 0;

            foreach (var bodyKv in config.Bodies)
            {
                var body = FindBody(oim, bodyKv.Key);
                if (body == null)
                {
                    Plugin.Log.LogWarning($"[FacilityPlacement] Body '{bodyKv.Key}' not found — skipping {bodyKv.Value.Count} placement(s).");
                    skipped += bodyKv.Value.Count;
                    continue;
                }

                foreach (var entry in bodyKv.Value)
                {
                    if (entry == null) { skipped++; continue; }

                    if (!isNewGameStart && !entry.UnsafeOverride)
                    {
                        string what = !string.IsNullOrEmpty(entry.Clear) ? $"clear:{entry.Clear}" : entry.Facility;
                        Plugin.Log.LogInfo($"[FacilityPlacement] Skipping {what} on {body.ObjectName} (loaded save, unsafeOverride=false).");
                        skipped++;
                        continue;
                    }

                    if (!string.IsNullOrEmpty(entry.Clear))
                    {
                        var clearTarget = ResolveCompany(companies, entry.Clear);
                        if (clearTarget == null)
                        {
                            Plugin.Log.LogWarning($"[FacilityPlacement] clear: company '{entry.Clear}' is not active in this game on {body.ObjectName} (disabled at start or missing) — skipping.");
                            skipped++;
                            continue;
                        }
                        var clearData = body.GetObjectInfoData(clearTarget);
                        if (clearData == null || clearData.ListFacility == null || clearData.ListFacility.Count == 0)
                        {
                            Plugin.Log.LogInfo($"[FacilityPlacement] clear: nothing to remove on {body.ObjectName} for {clearTarget.ID}.");
                            continue;
                        }
                        int cleared = 0;
                        foreach (var facility in new List<Facility>(clearData.ListFacility))
                        {
                            if (facility == null) continue;
                            try
                            {
                                long qty = Math.Max(1L, facility.Quantity);
                                facility.Scrap(qty, addResourceOnScrap: false);
                                cleared++;
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.LogWarning($"[FacilityPlacement] clear: scrap failed for {facility.facilityDescriptor?.ID} on {body.ObjectName}/{clearTarget.ID}: {ex.Message}");
                            }
                        }
                        Plugin.Log.LogInfo($"[FacilityPlacement] clear: removed {cleared} facility row(s) on {body.ObjectName} for {clearTarget.ID}.");
                        continue;
                    }

                    var company = ResolveCompany(companies, entry.Company);
                    if (company == null)
                    {
                        Plugin.Log.LogWarning($"[FacilityPlacement] Company '{entry.Company}' is not active in this game (disabled at start or missing) — skipping {entry.Facility} on {body.ObjectName}.");
                        skipped++;
                        continue;
                    }

                    var descriptor = allSO.AllFacility.GetByID(entry.Facility);
                    if (descriptor == null)
                    {
                        Plugin.Log.LogWarning($"[FacilityPlacement] Facility '{entry.Facility}' not in AllFacility — skipping on {body.ObjectName}/{company.ID}.");
                        skipped++;
                        continue;
                    }

                    var data = body.GetObjectInfoData(company);
                    if (data == null)
                    {
                        Plugin.Log.LogWarning($"[FacilityPlacement] No ObjectInfoData on {body.ObjectName} for company '{company.ID}' — skipping {entry.Facility}.");
                        skipped++;
                        continue;
                    }

                    if (!data.CanAddFacility(descriptor))
                    {
                        Plugin.Log.LogWarning(
                            $"[FacilityPlacement] {body.ObjectName}/{company.ID} cannot accept {entry.Facility} " +
                            $"(objectType={body.objectTypes}, placement={descriptor.PossiblePlacement}) — skipping.");
                        skipped++;
                        continue;
                    }

                    int count = entry.Count > 0 ? entry.Count : 1;
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            var facility = data.AddFacility(descriptor, prebuilt: true);
                            if (facility != null)
                            {
                                placed++;
                                Plugin.Log.LogInfo($"[FacilityPlacement] + {entry.Facility} on {body.ObjectName}/{company.ID} ({i + 1}/{count})");
                            }
                            else
                            {
                                Plugin.Log.LogWarning($"[FacilityPlacement] AddFacility returned null for {entry.Facility} on {body.ObjectName}/{company.ID}.");
                                skipped++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogError($"[FacilityPlacement] AddFacility threw for {entry.Facility} on {body.ObjectName}/{company.ID}: {ex.Message}");
                            skipped++;
                        }
                    }
                }
            }

            Plugin.Log.LogInfo($"[FacilityPlacement] Done — placed: {placed}, skipped: {skipped}, newGame={isNewGameStart}");
        }

        static ObjectInfo FindBody(ObjectInfoManager oim, string objectName)
        {
            if (oim?.allObjectInfos == null) return null;
            foreach (var b in oim.allObjectInfos)
            {
                if (b == null) continue;
                if (string.Equals(b.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                    return b;
            }
            return null;
        }

        static Company ResolveCompany(List<Company> companies, string idOrAlias)
        {
            if (companies == null || string.IsNullOrEmpty(idOrAlias)) return null;

            bool wgAlias = string.Equals(idOrAlias, "world_government", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(idOrAlias, "world-government", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(idOrAlias, "wg",               StringComparison.OrdinalIgnoreCase);

            foreach (var c in companies)
            {
                if (c == null) continue;
                if (wgAlias && c.Definition != null && c.Definition.IsWorldGovernment)
                    return c;
                if (!wgAlias && string.Equals(c.ID, idOrAlias, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }
    }
}
