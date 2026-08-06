using System;
using System.Linq;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Manager;
using System.Collections.Generic;

namespace Teddit
{
    internal static class DepositInjector
    {
        internal static void Run(DepositConfig config, ObjectInfoManager oim)
        {
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[DepositInjector] AllScriptableObjectManager is null"); return; }

            bool isNewGameStart = MonoBehaviourSingleton<GameManager>.Instance != null &&
                                  GameManager.InitFromStartGameConfigurationOrFromSaveFile;

            int added = 0, overwritten = 0, skipped = 0;

            foreach (var oi in oim.allObjectInfos)
            {
                var bodyConfig = config.GetBodyConfigFor(oi.ObjectName);
                if (bodyConfig == null || bodyConfig.Deposits == null || bodyConfig.Deposits.Count == 0) continue;

                if (bodyConfig.Overwrite)
                {
                    if (!isNewGameStart && !bodyConfig.UnsafeOverwrite)
                    {
                        Plugin.Log.LogInfo($"[DepositInjector] Skipping full overwrite for {oi.ObjectName} on loaded save.");
                        skipped += bodyConfig.Deposits.Count;
                        continue;
                    }

                    if (!isNewGameStart && bodyConfig.UnsafeOverwrite)
                        Plugin.Log.LogWarning($"[DepositInjector] UNSAFE: forcing full overwrite for {oi.ObjectName} on loaded save.");

                    ClearAllDeposits(oi);
                    Plugin.Log.LogDebug($"[DepositInjector] Cleared all existing deposits on {oi.ObjectName} before reseeding.");
                }

                foreach (var entry in bodyConfig.Deposits)
                {
                    var resource = allSO.AllResourceDefinitions.GetByID(entry.ResourceId);
                    if (resource == null)
                    {
                        Plugin.Log.LogWarning($"[DepositInjector] Unknown resourceId '{entry.ResourceId}' on '{oi.ObjectName}' - skipping.");
                        skipped++;
                        continue;
                    }

                    if (!Enum.TryParse<RowResourcesData.EResourceState>(entry.State, out var state))
                    {
                        Plugin.Log.LogWarning($"[DepositInjector] Unknown state '{entry.State}' - valid: Solid, Liquid, Gas, Underground.");
                        skipped++;
                        continue;
                    }

                    if (!bodyConfig.Overwrite && entry.Overwrite)
                    {
                        var existing = oi.ListRowResourcesData?
                            .FirstOrDefault(d => d.ResourcesType?.ID == entry.ResourceId && d.ResourceState == state);
                        if (existing != null)
                        {
                            existing.Value = entry.Amount;
                            if (entry.MiningFactor.HasValue) existing.MiningFactor = entry.MiningFactor.Value;
                            ApplyDiscoveryState(oi, existing, entry);
                            overwritten++;
                            Plugin.Log.LogDebug($"[DepositInjector] Overwrote {oi.ObjectName} {entry.ResourceId}/{entry.State} -> {entry.Amount:N0}");
                            continue;
                        }
                    }

                    var deposit = new RowResourcesData
                    {
                        ResourcesType = resource,
                        ResourceState = state,
                        MiningFactor = entry.MiningFactor ?? 0.5f,
                        ForcePrimary = entry.ForcePrimary
                    };
                    oi.AddDeposit(deposit, fullyExploredForAll: InitialFullyExplored(entry));
                    deposit.Value = entry.Amount;
                    ApplyDiscoveryState(oi, deposit, entry);
                    added++;
                    Plugin.Log.LogDebug($"[DepositInjector] Added {oi.ObjectName} {entry.ResourceId}/{entry.State} = {entry.Amount:N0}");
                }
            }

            Plugin.Log.LogInfo($"[DepositInjector] Done - added: {added}, overwritten: {overwritten}, skipped: {skipped}");
        }

        static void ClearAllDeposits(ObjectInfo oi)
        {
            if (oi?.ListRowResourcesData == null || oi.ListRowResourcesData.Count == 0)
                return;

            var existing = new List<RowResourcesData>(oi.ListRowResourcesData);
            foreach (var deposit in existing)
                oi.RemoveDeposit(deposit);
        }

        static bool InitialFullyExplored(DepositEntry entry)
        {
            double level = ClampExploration(entry.ExplorationLevel ?? 1.0);
            bool preliminary = entry.PreliminaryExplored ?? level > 0.0;
            return preliminary && level >= 1.0;
        }

        static void ApplyDiscoveryState(ObjectInfo oi, RowResourcesData deposit, DepositEntry entry)
        {
            double level = ClampExploration(entry.ExplorationLevel ?? 1.0);
            bool preliminary = entry.PreliminaryExplored ?? level > 0.0;

            foreach (ObjectInfoData objectInfoData in oi.ObjectsInfoData)
            {
                if (objectInfoData?.listExploredResourcesRows == null)
                    continue;

                foreach (RowExploredResourcesData row in objectInfoData.listExploredResourcesRows.Where(r => r?.ObservedData == deposit))
                {
                    row.Value = level;
                    row.PreliminaryExplored = preliminary;
                }

                objectInfoData.ClearResourceDepositsCaches();
            }

            oi.MarkIsDirty();
        }

        static double ClampExploration(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }
    }
}
