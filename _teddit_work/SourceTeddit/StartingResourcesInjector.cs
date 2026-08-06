using System;
using System.Collections.Generic;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Manager;
using ScriptableObjectScripts;

namespace Teddit
{
    /// <summary>
    /// Applies per-(body, company) starting resource stockpiles from
    /// `starting_resources.yaml`. Like facility placements: only on new-game start
    /// unless an entry sets unsafeOverride.
    /// </summary>
    internal static class StartingResourcesInjector
    {
        internal static void Run(StartingResourcesConfig config, ObjectInfoManager oim)
        {
            if (config == null || config.Bodies.Count == 0)
                return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            var gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (allSO == null || gm == null)
            {
                Plugin.Log.LogError("[StartingResources] AllScriptableObjectManager or GameManager unavailable.");
                return;
            }

            bool isNewGameStart = GameManager.InitFromStartGameConfigurationOrFromSaveFile;
            var companies = gm.Companies;
            int applied = 0, skipped = 0;

            foreach (var bodyKv in config.Bodies)
            {
                var body = FindBody(oim, bodyKv.Key);
                if (body == null)
                {
                    Plugin.Log.LogWarning($"[StartingResources] Body '{bodyKv.Key}' not found.");
                    skipped++;
                    continue;
                }

                foreach (var entry in bodyKv.Value)
                {
                    if (entry == null || entry.Resources == null || entry.Resources.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    if (!isNewGameStart && !entry.UnsafeOverride)
                    {
                        Plugin.Log.LogInfo($"[StartingResources] Skipping {entry.Company} on {body.ObjectName} (loaded save, unsafeOverride=false).");
                        skipped++;
                        continue;
                    }

                    var company = ResolveCompany(companies, entry.Company);
                    if (company == null)
                    {
                        Plugin.Log.LogWarning($"[StartingResources] Company '{entry.Company}' is not active in this game (disabled at start or missing) — skipping {body.ObjectName}.");
                        skipped++;
                        continue;
                    }

                    var data = body.GetObjectInfoData(company);
                    if (data?.ListRowResourcesData == null)
                    {
                        Plugin.Log.LogWarning($"[StartingResources] No ObjectInfoData on {body.ObjectName} for '{company.ID}'.");
                        skipped++;
                        continue;
                    }

                    bool setMode = !string.Equals(entry.Mode, "add", StringComparison.OrdinalIgnoreCase);
                    int counted = 0;
                    foreach (var resKv in entry.Resources)
                    {
                        var rd = allSO.AllResourceDefinitions?.GetByID(resKv.Key);
                        if (rd == null)
                        {
                            Plugin.Log.LogWarning($"[StartingResources] Resource '{resKv.Key}' not found.");
                            continue;
                        }

                        var row = FindOrCreateRow(data, rd);
                        if (row == null)
                        {
                            Plugin.Log.LogWarning($"[StartingResources] Could not get/create row for {resKv.Key} on {body.ObjectName}/{company.ID}.");
                            continue;
                        }

                        try
                        {
                            double before = row.Value;
                            row.Value = setMode ? resKv.Value : before + resKv.Value;
                            counted++;
                            Plugin.Log.LogInfo($"[StartingResources] {body.ObjectName}/{company.ID}: {resKv.Key} {before:F0} → {row.Value:F0} ({(setMode ? "set" : "add")})");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[StartingResources] Set {resKv.Key} failed: {ex.Message}");
                        }
                    }
                    applied += counted;

                    // Tell the game UI/economy to refresh derived values that depend on stockpiles.
                    try { data.MarkIsDirty(); } catch { /* best-effort */ }
                }
            }

            Plugin.Log.LogInfo($"[StartingResources] Done — set {applied} stockpile(s), skipped {skipped}, newGame={isNewGameStart}");
        }

        static RowResourcesData FindOrCreateRow(ObjectInfoData data, ResourceDefinition rd)
        {
            foreach (var row in data.ListRowResourcesData)
                if (row?.ResourcesType == rd) return row;

            // Force vanilla to materialize a row for this resource if missing.
            try
            {
                data.AddResources(rd, 0.0);
                foreach (var row in data.ListRowResourcesData)
                    if (row?.ResourcesType == rd) return row;
            }
            catch { /* fall through */ }
            return null;
        }

        static ObjectInfo FindBody(ObjectInfoManager oim, string objectName)
        {
            if (oim?.allObjectInfos == null) return null;
            foreach (var b in oim.allObjectInfos)
                if (b != null && string.Equals(b.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                    return b;
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
                if (wgAlias && c.Definition != null && c.Definition.IsWorldGovernment) return c;
                if (!wgAlias && string.Equals(c.ID, idOrAlias, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }
    }
}
