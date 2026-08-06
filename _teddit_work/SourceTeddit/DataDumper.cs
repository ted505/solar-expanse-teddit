using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Data;
using Data.ScriptableObject;
using Game;
using Game.CompanyScripts;
using Game.ContractsObjectives;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Elements.SpaceCraftConstructElements;
using Language;
using Manager;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Run-once tool: dumps all game data to BepInEx/plugins/Teddit/dump/ as separate YAML files:
    ///   resources.yaml  — all resource definition IDs and translation keys
    ///   bodies.yaml     — all celestial bodies with subtype config and current deposits
    ///   facilities.yaml — all facility/module descriptors in patch-compatible format
    ///   spacecraft.yaml — all spacecraft type descriptors in patch-compatible format
    ///   launch_vehicles.yaml — all launch vehicle descriptors in patch-compatible format
    ///   research.yaml   — all research definitions
    ///   deposits.yaml   — all body deposits in patch-compatible format
    /// Dump format == patch format: keyed by ID, camelCase field names, copy-pasteable.
    /// Disable by setting DumpOnLoad = false in Plugin after you have the data.
    /// </summary>
    internal static class DataDumper
    {
        internal static void Run(string dumpDir)
        {
            Plugin.Log.LogInfo("[DataDumper] Starting dump...");
            Directory.CreateDirectory(dumpDir);

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) throw new System.NullReferenceException("AllScriptableObjectManager.Instance is null");

            var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (oim == null) throw new System.NullReferenceException("ObjectInfoManager.Instance is null");

            DumpResources(allSO, dumpDir);
            DumpCompanies(allSO, dumpDir);
            DumpBodies(oim, dumpDir);
            DumpDeposits(oim, dumpDir);
            DumpFacilities(allSO, dumpDir);
            DumpFacilityPlacements(oim, dumpDir);
            DumpSpacecraft(allSO, dumpDir);
            DumpLaunchVehicles(allSO, dumpDir);
            DumpResearch(allSO, dumpDir);
            DumpContracts(allSO, oim, dumpDir);

            Plugin.Log.LogInfo($"[DataDumper] Done → {dumpDir}");
        }

        // ── Resources ─────────────────────────────────────────────────────────

        static void DumpResources(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var res in allSO.AllResourceDefinitions.List)
            {
                if (res == null) continue;
                string name = res.ID;
                try { name = res.Name; } catch { }
                sb.AppendLine($"# {name}");
                sb.AppendLine($"{YamlKey(res.ID)}:");
                sb.AppendLine($"  resourceType: {YamlScalar(res.ResourceType.ToString())}");
                sb.AppendLine($"  showOnUI: {FormatBool(res.ShowOnUI)}");
                sb.AppendLine($"  canBeLeftOnObject: {FormatBool(res.CanBeLeftOnObject)}");
                sb.AppendLine($"  marketClearingPriceBase: {FormatDouble(res.MarketClearingPriceBase)}");
                sb.AppendLine($"  priceChangeMultiplier: {FormatDouble(res.PriceChangeMultiplier)}");
                sb.AppendLine($"  startingSummedPreviousTradesQuantity: {FormatFloat(res.StartingSummedPreviousTradesQuantity)}");
                sb.AppendLine($"  startingPreviousTradesCount: {FormatFloat(res.StartingPreviousTradesCount)}");
                sb.AppendLine($"  toSortMarketOffer: {res.ToSortMarketOffer}");
                sb.AppendLine($"  showInMarket: {FormatBool(allSO.AllResourceDefinitions.ListResourceDefinitionInMarketPlaceOffer.Contains(res))}");
                string iconRef = FacilityCreator.GetSpriteReference(res.Sprite);
                if (!string.IsNullOrEmpty(iconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(iconRef)}");
                AppendResourceTerraformationInfo(sb, "  terraformationInfo", res.TerraformationInfo);
                AppendAnimationCurve(sb, "  toxicityCurve", res.ToxicityCurve);
                sb.AppendLine();
                count++;
            }
            File.WriteAllText(Path.Combine(dir, "resources.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] resources.yaml — {count} entries");
        }

        // ── Bodies ────────────────────────────────────────────────────────────

        static void DumpCompanies(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var company in allSO.AllCompanyDefinitions.List)
            {
                if (company == null) continue;

                string name = company.ID;
                try { name = LEManager.Get(company.localeID); } catch { }

                sb.AppendLine($"# {name}");
                sb.AppendLine($"{YamlKey(company.ID)}:");
                sb.AppendLine($"  isWorldGovernment: {FormatBool(company.IsWorldGovernment)}");
                sb.AppendLine($"  mainObject: {YamlScalar(company.MainObject != null ? (company.MainObject.Object != null ? company.MainObject.Object.ObjectName : company.MainObject.ID.ToString()) : null)}");
                sb.AppendLine($"  onInMenu: {FormatBool(company.ONInMenu)}");
                sb.AppendLine($"  skipCosts: {FormatBool(company.SkipCosts)}");

                string logoRef = FacilityCreator.GetSpriteReference(company.LogoImage);
                if (!string.IsNullOrEmpty(logoRef))
                    sb.AppendLine($"  logoImageRef: {YamlScalar(logoRef)}");
                string logoLargeRef = FacilityCreator.GetSpriteReference(company.LogoImageLarge);
                if (!string.IsNullOrEmpty(logoLargeRef))
                    sb.AppendLine($"  logoImageLargeRef: {YamlScalar(logoLargeRef)}");

                if (company.PopulationConfig != null)
                {
                    sb.AppendLine("  populationConfig:");
                    sb.AppendLine($"    housingExpectancyModifier: {company.PopulationConfig.housingExpectancyModifier}");
                    sb.AppendLine($"    foodDemandMultiplier: {FormatFloat(company.PopulationConfig.foodDemandMultiplier)}");
                    sb.AppendLine($"    populationParameter: {FormatFloat(company.PopulationConfig.populationParameter)}");
                    sb.AppendLine($"    minPopulationSize: {company.PopulationConfig.minPopulationSize}");
                    sb.AppendLine($"    minConsumptionRate: {FormatFloat(company.PopulationConfig.minConsumptionRate)}");
                }

                if (company.MarketConfig != null)
                {
                    sb.AppendLine("  marketConfig:");
                    sb.AppendLine($"    requiredMoneyMarginMultiplier: {FormatFloat(company.MarketConfig.requiredMoneyMarginMultiplier)}");
                    sb.AppendLine($"    futureResourceStorageMultiplier: {FormatFloat(company.MarketConfig.futureResourceStorageMultiplier)}");
                }

                if (company.AIConfig != null)
                {
                    sb.AppendLine("  aiConfig:");
                    AppendCompanyCost(sb, "    costMultiplier", company.AIConfig.costMultiplier);
                    sb.AppendLine($"    costCalcType: {YamlScalar(company.AIConfig.costCalcType.ToString())}");
                    sb.AppendLine($"    makeOfferTimeThreshold: {FormatFloat(company.AIConfig.makeOfferTimeThreshold)}");
                    sb.AppendLine($"    makeOfferMinEndTime: {FormatFloat(company.AIConfig.makeOfferMinEndTime)}");
                    sb.AppendLine($"    makeOfferMaxEndTime: {FormatFloat(company.AIConfig.makeOfferMaxEndTime)}");
                    sb.AppendLine($"    makeOfferEndTimeMultiplier: {FormatFloat(company.AIConfig.makeOfferEndTimeMultiplier)}");
                    AppendCompanyCost(sb, "    makeOfferUnitCostMultiplier", company.AIConfig.makeOfferUnitCostMultiplier);
                    sb.AppendLine($"    takeOffersFromOtherAIs: {FormatBool(company.AIConfig.takeOffersFromOtherAIs)}");
                    sb.AppendLine($"    takeOfferOnlyInstant: {FormatBool(company.AIConfig.takeOfferOnlyInstant)}");
                    sb.AppendLine($"    takeOfferTimeThreshold: {FormatFloat(company.AIConfig.takeOfferTimeThreshold)}");
                    AppendCompanyCost(sb, "    takeOfferBuyUnitCostMultiplier", company.AIConfig.takeOfferBuyUnitCostMultiplier);
                    AppendCompanyCost(sb, "    takeOfferSellUnitCostMultiplier", company.AIConfig.takeOfferSellUnitCostMultiplier);
                    sb.AppendLine($"    cachePlanMissionResults: {FormatBool(company.AIConfig.cachePlanMissionResults)}");
                    sb.AppendLine($"    cacheAsyncResults: {FormatBool(company.AIConfig.cacheAsyncResults)}");
                    sb.AppendLine($"    includeUnlockableSpacecraftAndLVsForFlights: {FormatBool(company.AIConfig.includeUnlockableSpacecraftAndLVsForFlights)}");
                }

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "companies.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] companies.yaml â€” {count} entries");
        }

        static void DumpBodies(ObjectInfoManager oim, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var oi in oim.allObjectInfos)
            {
                sb.AppendLine($"# {oi.ObjectName}");
                sb.AppendLine($"{YamlKey(oi.ObjectName)}:");
                sb.AppendLine($"  objectTypes: {YamlScalar(oi.objectTypes.ToString())}");

                var sub = oi.objectSubType;
                if (sub != null)
                {
                    sb.AppendLine($"  objectSubTypeId: {YamlScalar(sub.ID)}");

                    // MiningFactorSlots
                    if (sub.MiningFactors != null && sub.MiningFactors.Count > 0)
                    {
                        sb.AppendLine("  miningFactorSlots:");
                        foreach (var slot in sub.MiningFactors)
                        {
                            sb.AppendLine("    -");
                            foreach (var mf in slot)
                            {
                                sb.AppendLine($"      - resourceId: {YamlScalar(mf.ResourceDefinition?.ID)}");
                                sb.AppendLine($"        state: {YamlScalar(mf.resourceState.ToString())}");
                                sb.AppendLine($"        category: {YamlScalar(mf.Category.ToString())}");
                                sb.AppendLine($"        probability: {FormatFloat(mf.probability)}");
                                sb.AppendLine($"        forcePrimary: {FormatBool(mf.forcePrimary)}");
                            }
                        }
                    }

                    // ResourceRandomMap
                    if (sub.ResourceRandomMap != null && sub.ResourceRandomMap.Count > 0)
                    {
                        sb.AppendLine("  resourceRandomMap:");
                        foreach (var kv in sub.ResourceRandomMap)
                        {
                            sb.AppendLine($"    - resourceId: {YamlScalar(kv.Key.definition?.ID)}");
                            sb.AppendLine($"      state: {YamlScalar(kv.Key.state.ToString())}");
                            sb.AppendLine($"      isInfinite: {FormatBool(kv.Value.isInfinite)}");
                            sb.AppendLine($"      rangeMin: {FormatFloat(kv.Value.range.x)}");
                            sb.AppendLine($"      rangeMax: {FormatFloat(kv.Value.range.y)}");
                        }
                    }
                }

                // Current deposits — in patch-compatible format
                if (oi.ListRowResourcesData != null && oi.ListRowResourcesData.Count > 0)
                {
                    sb.AppendLine("  currentDeposits:");
                    foreach (var dep in oi.ListRowResourcesData)
                    {
                        var explored = FindPreferredExploredRow(oi, dep);
                        sb.AppendLine($"    - resourceId: {YamlScalar(dep.ResourcesType?.ID)}");
                        sb.AppendLine($"      state: {YamlScalar(dep.ResourceState.ToString())}");
                        sb.AppendLine($"      amount: {FormatDouble(dep.Value)}");
                        sb.AppendLine($"      miningFactor: {FormatNullableFloat(dep.MiningFactor)}");
                        if (explored != null)
                        {
                            sb.AppendLine($"      explorationLevel: {FormatDouble(explored.Value)}");
                            sb.AppendLine($"      preliminaryExplored: {FormatBool(explored.PreliminaryExplored)}");
                        }
                        sb.AppendLine($"      forcePrimary: {FormatBool(dep.ForcePrimary)}");
                    }
                }

                sb.AppendLine();
                count++;
            }
            File.WriteAllText(Path.Combine(dir, "bodies.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] bodies.yaml — {count} entries");
        }

        // ── Deposits ──────────────────────────────────────────────────────────

        static void DumpDeposits(ObjectInfoManager oim, string dir)
        {
            var sb = new StringBuilder();
            int bodyCount = 0, depCount = 0;
            foreach (var oi in oim.allObjectInfos)
            {
                if (oi.ListRowResourcesData == null || oi.ListRowResourcesData.Count == 0)
                    continue;

                sb.AppendLine($"# {oi.ObjectName}");
                sb.AppendLine($"{YamlKey(oi.ObjectName)}:");
                foreach (var dep in oi.ListRowResourcesData)
                {
                    var explored = FindPreferredExploredRow(oi, dep);
                    sb.AppendLine($"  - resourceId: {YamlScalar(dep.ResourcesType?.ID)}");
                    sb.AppendLine($"    state: {YamlScalar(dep.ResourceState.ToString())}");
                    sb.AppendLine($"    amount: {FormatDouble(dep.Value)}");
                    sb.AppendLine($"    miningFactor: {FormatNullableFloat(dep.MiningFactor)}");
                    if (explored != null)
                    {
                        sb.AppendLine($"    explorationLevel: {FormatDouble(explored.Value)}");
                        sb.AppendLine($"    preliminaryExplored: {FormatBool(explored.PreliminaryExplored)}");
                    }
                    sb.AppendLine($"    forcePrimary: {FormatBool(dep.ForcePrimary)}");
                    depCount++;
                }
                sb.AppendLine();
                bodyCount++;
            }
            File.WriteAllText(Path.Combine(dir, "deposits.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] deposits.yaml — {bodyCount} bodies, {depCount} deposits");
        }

        // ── Facilities ────────────────────────────────────────────────────────

        static readonly FieldInfo _timeToBuildFi = typeof(Data.ScriptableObject.MyIDScriptableObjectProductionItem)
            .GetField("timeToBuildInDays", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _ctorEquipFi = typeof(Data.ScriptableObject.MyIDScriptableObjectProductionItem)
            .GetField("constructionEquipmentCountIsRequired", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _priceFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "price");
        static readonly FieldInfo _refinerFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "refinerData");
        static readonly FieldInfo _resourcesToMineFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "resourcesToMine");
        static readonly FieldInfo _byproductsFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "byproducts");

        static void DumpFacilities(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var fd in allSO.AllFacility.List)
            {
                if (fd == null) continue;

                string name = fd.ID;
                try { name = fd.Name; } catch { }

                // Comment header
                sb.AppendLine($"# {name.ToUpperInvariant()} ({fd.GetType().Name})");
                sb.AppendLine($"{YamlKey(fd.ID)}:");

                sb.AppendLine($"  facilityType: {YamlScalar(fd.facilityType.ToString())}");
                sb.AppendLine($"  possiblePlacement: {YamlScalar(fd.PossiblePlacement.ToString())}");
                sb.AppendLine($"  maintenanceCostPerDay: {FormatFloat(fd.MaintenanceCostPerDay)}");
                sb.AppendLine($"  energyConsumption: {FormatDouble(fd.EnergyConsumption)}");
                sb.AppendLine($"  needWorkersToWork: {(int)fd.NeedWorkersToWork(null)}");
                sb.AppendLine($"  specialAbility: {YamlScalar(fd.specialAbilityFacilityNew.ToString())}");
                sb.AppendLine($"  specialAbilityParameter: {FormatFloat(fd.specialAbilityParameter)}");
                sb.AppendLine($"  upkeepStacking: {FormatBool(fd.upkeepStacking)}");
                sb.AppendLine($"  upkeepStackingValue: {FormatFloat(fd.upkeepStackingValue)}");
                sb.AppendLine($"  blockStacking: {FormatBool(fd.BlockStacking)}");
                if (fd.CanBuildParameter != null)
                {
                    sb.AppendLine($"  canBuildBy: {YamlScalar(fd.CanBuildParameter.canBuildBy.ToString())}");
                    if (fd.CanBuildParameter.canBuildBy == CanBuildParameter.ECanBuild.countOnPlanet)
                        sb.AppendLine($"  countOnPlanet: {fd.CanBuildParameter.countOnPlanet}");
                }
                string facilityIconRef = FacilityCreator.GetSpriteReference(fd.Sprite);
                if (!string.IsNullOrEmpty(facilityIconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(facilityIconRef)}");

                float timeToBuild = _timeToBuildFi != null ? (float)_timeToBuildFi.GetValue(fd) : 0f;
                bool ctorEquip   = _ctorEquipFi  != null ? (bool)_ctorEquipFi.GetValue(fd)   : true;
                sb.AppendLine($"  timeToBuildInDays: {FormatFloat(timeToBuild)}");
                sb.AppendLine($"  constructionEquipmentCountIsRequired: {FormatBool(ctorEquip)}");
                sb.AppendLine($"  facilityItemClass: {YamlScalar(fd.FacilityItemClass?.Name)}");

                // Price
                double buildCost = 0;
                var buildResources = new Dictionary<string, double>();
                if (_priceFi != null && _priceFi.GetValue(fd) is ResourcePrice price)
                {
                    buildCost = price.BuildCost;
                    if (price.ListResources != null)
                        foreach (var r in price.ListResources)
                            if (r?.ResourceDefinition != null)
                                buildResources[r.ResourceDefinition.ID] = r.Price;
                }
                sb.AppendLine($"  buildCost: {FormatDouble(buildCost)}");
                AppendDict(sb, "  buildResources", buildResources);

                // EnergyProductionData
                if (fd.energyProductionData != null)
                {
                    sb.AppendLine($"  energyProduction: {FormatNullableDouble(fd.energyProductionData.energyProduction)}");
                    sb.AppendLine($"  solarPanels: {FormatBool(fd.energyProductionData.solarPanels)}");
                    sb.AppendLine($"  windPower: {FormatBool(fd.energyProductionData.windPower)}");
                    sb.AppendLine($"  geothermal: {FormatBool(fd.energyProductionData.geothermalPower)}");
                    var energyInput = new Dictionary<string, double>();
                    if (fd.energyProductionData.input != null)
                        foreach (var inp in fd.energyProductionData.input)
                            if (inp?.resource != null)
                                energyInput[inp.resource.ID] = inp.ratePerDay;
                    AppendDict(sb, "  energyInput", energyInput);
                }
                if (fd.energyStorageData2 != null)
                    sb.AppendLine($"  energyStorage: {FormatNullableDouble(fd.energyStorageData2.energyMaxCapacity)}");
                if (fd is GroundFacilityDescriptor gfd)
                {
                    if (Math.Abs(gfd.resourceExplorationBonus) > 0.0001f)
                        sb.AppendLine($"  resourceExplorationBonus: {FormatFloat(gfd.resourceExplorationBonus)}");

                    bool isLab = fd.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.Lab)
                              || fd.FacilityItemClass == typeof(LabFacility);
                    if (isLab && gfd.labData != null)
                    {
                        sb.AppendLine($"  labBonusToResearchInPerHour: {gfd.labData.bonusToResearchInPerHour}");
                        sb.AppendLine($"  labResearchSubTypeId: {YamlScalar(gfd.labData.idResearchSubType)}");
                        AppendStringList(sb, "  labIdToBonus", gfd.labData.idToBonus?.ToList() ?? new List<string>());
                    }
                }
                else if (fd is SpaceModuleDescriptor smd)
                {
                    sb.AppendLine($"  mass: {FormatDouble(smd.Mass)}");
                    sb.AppendLine($"  canBeLoadAsCargo: {FormatBool(smd.CanBeLoadAsCargo)}");
                    sb.AppendLine($"  buildOnOrbitSpaceModuleAllow: {FormatBool(smd.BuildOnOrbitSpaceModuleAllow)}");
                    sb.AppendLine($"  timeToDestroyInDay: {FormatNullableFloat(smd.TimeToDestroyInDay)}");
                    sb.AppendLine($"  spaceModuleType: {YamlScalar(smd.SpaceModuleType.ToString())}");
                    sb.AppendLine($"  typeSubObjectInfo: {YamlScalar(smd.TypeSubObjectInfo.ToString())}");
                    sb.AppendLine($"  isLockedCreate: {FormatBool(smd.isLockedCreate)}");
                    sb.AppendLine($"  isSpaceConstructionOnOrbit: {FormatBool(smd.isSpaceConstructionOnOrbit)}");
                    if (smd.facilityOrModuleToInstall != null)
                        sb.AppendLine($"  facilityOrModuleToInstall: {YamlScalar(smd.facilityOrModuleToInstall.ID)}");
                    if (smd.isSpaceConstructionOnOrbitSpaceCraftToCreate != null)
                        sb.AppendLine($"  isSpaceConstructionOnOrbitSpaceCraftToCreate: {YamlScalar(smd.isSpaceConstructionOnOrbitSpaceCraftToCreate.ID)}");
                    if (smd.isSpaceConstructionOnOrbitLaunchVehicleCanUse != null)
                        sb.AppendLine($"  isSpaceConstructionOnOrbitLaunchVehicleCanUse: {YamlScalar(smd.isSpaceConstructionOnOrbitLaunchVehicleCanUse.ID)}");
                }

                // ResourcesToMine
                var resourcesToMine = new List<string>();
                if (_resourcesToMineFi != null && _resourcesToMineFi.GetValue(fd) is HashSet<ResourceDefinition> mines)
                    foreach (var r in mines)
                        if (r != null) resourcesToMine.Add(r.ID);
                AppendStringList(sb, "  resourcesToMine", resourcesToMine);

                // RefinerData
                var refinerInput  = new Dictionary<string, double>();
                var refinerOutput = new Dictionary<string, double>();
                if (fd.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.Refiner) && _refinerFi != null)
                {
                    object rd = _refinerFi.GetValue(fd);
                    if (rd != null)
                    {
                        Type rdType = rd.GetType();
                        DumpRefinerList(rd, rdType, "Input",  refinerInput);
                        DumpRefinerList(rd, rdType, "Output", refinerOutput);
                    }
                }
                AppendDict(sb, "  refinerInput",  refinerInput);
                AppendDict(sb, "  refinerOutput", refinerOutput);

                // Byproducts
                var byproducts = new List<ByproductEntry>();
                if (_byproductsFi != null && _byproductsFi.GetValue(fd) is FacilityBaseDescriptor.Byproduct[] bps)
                    foreach (var bp in bps)
                        if (bp?.resource != null)
                            byproducts.Add(new ByproductEntry
                            {
                                Resource = bp.resource.ID,
                                Rate     = bp.rate,
                                State    = bp.state.ToString(),
                            });
                AppendByproducts(sb, "  byproducts", byproducts);

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "facilities.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] facilities.yaml — {count} entries");
        }

        // ── Facility placements (per-body, per-company initial buildings) ─────

        static void DumpFacilityPlacements(ObjectInfoManager oim, string dir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Initial facilities present on each body, grouped by company.");
            sb.AppendLine("# Format matches facility_placements.yaml so entries can be copy-pasted.");
            sb.AppendLine("# Duplicate descriptors are collapsed to a single entry with `count:`.");
            sb.AppendLine();

            int bodyCount = 0, entryCount = 0, facilityCount = 0;
            foreach (var oi in oim.allObjectInfos)
            {
                if (oi == null || oi.ObjectsInfoData == null || oi.ObjectsInfoData.Count == 0)
                    continue;

                var perBodyEntries = new List<(string company, string facility, int count)>();
                foreach (var data in oi.ObjectsInfoData)
                {
                    if (data?.company == null || data.ListFacility == null || data.ListFacility.Count == 0)
                        continue;

                    var byDescriptor = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var facility in data.ListFacility)
                    {
                        var descriptor = facility?.facilityDescriptor;
                        if (descriptor == null || string.IsNullOrEmpty(descriptor.ID))
                            continue;

                        int stack = (int)Math.Max(1L, facility.Quantity);
                        byDescriptor.TryGetValue(descriptor.ID, out int existing);
                        byDescriptor[descriptor.ID] = existing + stack;
                    }

                    if (byDescriptor.Count == 0)
                        continue;

                    string companyKey = data.company.Definition != null && data.company.Definition.IsWorldGovernment
                        ? "world_government"
                        : (data.company.ID ?? "");

                    foreach (var kv in byDescriptor)
                    {
                        perBodyEntries.Add((companyKey, kv.Key, kv.Value));
                        entryCount++;
                        facilityCount += kv.Value;
                    }
                }

                if (perBodyEntries.Count == 0)
                    continue;

                perBodyEntries.Sort((a, b) =>
                {
                    int c = string.CompareOrdinal(a.company, b.company);
                    return c != 0 ? c : string.CompareOrdinal(a.facility, b.facility);
                });

                sb.AppendLine($"# {oi.ObjectName}");
                sb.AppendLine($"{YamlKey(oi.ObjectName)}:");
                foreach (var (company, facility, count) in perBodyEntries)
                {
                    sb.AppendLine($"  - company: {YamlScalar(company)}");
                    sb.AppendLine($"    facility: {YamlScalar(facility)}");
                    if (count != 1)
                        sb.AppendLine($"    count: {count}");
                }
                sb.AppendLine();
                bodyCount++;
            }

            File.WriteAllText(Path.Combine(dir, "facility_placements.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] facility_placements.yaml — {bodyCount} bodies, {entryCount} entries, {facilityCount} facilities");
        }

        static void DumpRefinerList(object refinerData, Type rdType, string memberName, Dictionary<string, double> target)
        {
            try
            {
                object listObj = GetRefinerMemberValue(refinerData, rdType, memberName);
                if (!(listObj is IEnumerable items)) return;
                foreach (object item in items)
                {
                    if (item == null) continue;
                    var resFi      = item.GetType().GetField("resource");
                    var rateFi     = item.GetType().GetField("ratePerDay");
                    var res        = resFi?.GetValue(item) as ResourceDefinition;
                    var rate       = rateFi != null ? (double)rateFi.GetValue(item) : 0.0;
                    if (res != null) target[res.ID] = rate;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DataDumper] DumpRefinerList({memberName}): {ex.Message}");
            }
        }

        static object GetRefinerMemberValue(object refinerData, Type rdType, string memberName)
        {
            string fieldName = char.ToLowerInvariant(memberName[0]) + memberName.Substring(1);
            var fi = rdType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                  ?? rdType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) return fi.GetValue(refinerData);

            var prop = rdType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return prop?.GetValue(refinerData, null);
        }

        // ── Spacecraft ────────────────────────────────────────────────────────

        static readonly FieldInfo _scHullFi    = typeof(Data.ScriptableObject.SpacecraftType)
            .GetField("hull", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _scIsLockedFi = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.SpacecraftType), "isLocked");
        static readonly FieldInfo _lvPriceFi    = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "priceBase");

        static void DumpSpacecraft(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var sc in allSO.AllSpacecraftType.List)
            {
                if (sc == null) continue;

                string name = sc.ID;
                try { name = sc.Name; } catch { }

                sb.AppendLine($"# {name.ToUpperInvariant()} (SpacecraftType)");
                sb.AppendLine($"{YamlKey(sc.ID)}:");

                sb.AppendLine($"  engineType: {YamlScalar(sc.engineType.ToString())}");
                sb.AppendLine($"  orbitSC: {FormatBool(sc.OrbitSC)}");
                sb.AppendLine($"  solarSC: {FormatBool(sc.SolarSC)}");
                sb.AppendLine($"  solarParameter: {FormatFloat(sc.SolarParameter)}");
                sb.AppendLine($"  isLocked: {FormatBool(_scIsLockedFi != null && (bool)_scIsLockedFi.GetValue(sc))}");
                sb.AppendLine($"  needLaunchVehicleToGoToMoon: {FormatBool(sc.needLaunchVehicleToGoToMoon)}");
                sb.AppendLine($"  buildOnlyLowOrbit: {FormatBool(sc.BuildOnlyLowOrbit)}");
                sb.AppendLine($"  canByBuildByUser: {FormatBool(sc.CanByBuildByUser)}");
                sb.AppendLine($"  lowOrbitContainer: {FormatBool(sc.LowOrbitContainer)}");
                sb.AppendLine($"  magneticCatapult: {FormatBool(sc.MagneticCatapult)}");
                sb.AppendLine($"  reusability: {FormatFloat(sc.Reusability)}");
                sb.AppendLine($"  constanceAcceleration: {FormatBool(sc.ConstanceAcceleration)}");
                sb.AppendLine($"  isInterstellarShip: {FormatBool(sc.IsInterstellarShip)}");
                sb.AppendLine($"  asteroidPullingShip: {FormatBool(sc.AsteroidPullingShip)}");
                var spacecraftFuel = sc.GetFuelType();
                if (spacecraftFuel != null)
                    sb.AppendLine($"  fuelType: {YamlScalar(spacecraftFuel.ID)}");
                string spacecraftIconRef = FacilityCreator.GetSpriteReference(sc.RocketBackGround);
                if (!string.IsNullOrEmpty(spacecraftIconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(spacecraftIconRef)}");
                sb.AppendLine($"  timeToBuildInDays: {FormatFloat(_timeToBuildFi != null ? (float)_timeToBuildFi.GetValue(sc) : 0f)}");
                sb.AppendLine($"  constructionEquipmentCountIsRequired: {FormatBool(_ctorEquipFi != null ? (bool)_ctorEquipFi.GetValue(sc) : true)}");

                double buildCost = 0;
                var buildResources = new Dictionary<string, double>();
                {
                    // priceBase may live on the hull object for completed-design spacecraft
                    object priceSource = sc;
                    object hullBoxed = _scHullFi?.GetValue(sc);
                    if (hullBoxed != null && ScriptableObjectPatcher.FindField(hullBoxed.GetType(), "priceBase") != null)
                        priceSource = hullBoxed;
                    var priceFi = ScriptableObjectPatcher.FindField(priceSource.GetType(), "priceBase");
                    if (priceFi?.GetValue(priceSource) is ResourcePrice price)
                    {
                        buildCost = price.BuildCost;
                        if (price.ListResources != null)
                            foreach (var r in price.ListResources)
                                if (r?.ResourceDefinition != null)
                                    buildResources[r.ResourceDefinition.ID] = r.Price;
                    }
                }
                sb.AppendLine($"  buildCost: {FormatDouble(buildCost)}");
                AppendDict(sb, "  buildResources", buildResources);

                sb.AppendLine($"  cargoCapacity: {FormatFloat((float)sc.CargoCapacity)}");
                sb.AppendLine($"  fuelCapacity: {FormatFloat(sc.FuelCapacity)}");
                sb.AppendLine($"  thrust: {FormatFloat(sc.Thrust)}");
                sb.AppendLine($"  exhaustV: {FormatFloat(sc.ExhaustV)}");
                sb.AppendLine($"  mass: {FormatFloat(sc.Mass)}");
                sb.AppendLine($"  maxLifeSupport: {FormatFloat(sc.MAXLifeSupport)}");
                sb.AppendLine($"  availableDeltaV: {FormatFloat(sc.AvailableDeltaV)}");
                sb.AppendLine($"  maximumAcceleration: {FormatFloat(sc.MaximumAcceleration)}");
                sb.AppendLine($"  maintenanceCostPerDay: {FormatFloat(sc.MaintenanceCostPerDay)}");
                sb.AppendLine($"  maxPayload: {FormatNullableFloat(sc.MaxPayload)}");

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "spacecraft.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] spacecraft.yaml — {count} entries");
        }

        // ── Launch Vehicles ───────────────────────────────────────────────────

        static readonly FieldInfo _lvIsLockedFi        = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "isLocked");
        static readonly FieldInfo _lvForCycleMissionFi  = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "forCycleMission");
        static readonly FieldInfo _lvFakeForFacilityFi  = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "fakeForFacility");
        static readonly FieldInfo _lvCanBuyMaxPayloadFi = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "canBuyMaxPayload");

        static void DumpLaunchVehicles(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var lv in allSO.AllLaunchVehicleType.List)
            {
                if (lv == null) continue;

                string name = lv.ID;
                try { name = lv.Name; } catch { }

                sb.AppendLine($"# {name.ToUpperInvariant()} (LaunchVehicleType)");
                sb.AppendLine($"{YamlKey(lv.ID)}:");

                sb.AppendLine($"  isLocked: {FormatBool(_lvIsLockedFi != null && (bool)_lvIsLockedFi.GetValue(lv))}");
                sb.AppendLine($"  special: {YamlScalar(lv.special.ToString())}");
                sb.AppendLine($"  canSendHuman: {FormatBool(lv.canSendHuman)}");
                if (lv.FuelTypeOnStart != null)
                    sb.AppendLine($"  fuelType: {YamlScalar(lv.FuelTypeOnStart.ID)}");
                string launchVehicleIconRef = FacilityCreator.GetSpriteReference(FindFieldValue<Sprite>(lv, "rocketBackGround"));
                if (!string.IsNullOrEmpty(launchVehicleIconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(launchVehicleIconRef)}");
                sb.AppendLine($"  forCycleMission: {FormatBool(_lvForCycleMissionFi != null && (bool)_lvForCycleMissionFi.GetValue(lv))}");
                sb.AppendLine($"  fakeForFacility: {FormatBool(_lvFakeForFacilityFi != null && (bool)_lvFakeForFacilityFi.GetValue(lv))}");
                sb.AppendLine($"  timeToBuildInDays: {FormatFloat(_timeToBuildFi != null ? (float)_timeToBuildFi.GetValue(lv) : 0f)}");
                sb.AppendLine($"  constructionEquipmentCountIsRequired: {FormatBool(_ctorEquipFi != null ? (bool)_ctorEquipFi.GetValue(lv) : true)}");

                double buildCost = 0;
                var buildResources = new Dictionary<string, double>();
                if (_lvPriceFi?.GetValue(lv) is ResourcePrice price)
                {
                    buildCost = price.BuildCost;
                    if (price.ListResources != null)
                        foreach (var r in price.ListResources)
                            if (r?.ResourceDefinition != null)
                                buildResources[r.ResourceDefinition.ID] = r.Price;
                }
                sb.AppendLine($"  buildCost: {FormatDouble(buildCost)}");
                AppendDict(sb, "  buildResources", buildResources);

                sb.AppendLine($"  maxPayload: {FormatFloat(lv.maxPayload)}");
                sb.AppendLine($"  maxFuelLoad: {FormatFloat(lv.maxFuelLoad)}");
                sb.AppendLine($"  costLaunch: {FormatFloat(lv.costLaunch)}");
                sb.AppendLine($"  exhaustV: {FormatFloat(lv.exhaustV)}");
                sb.AppendLine($"  reusability: {FormatFloat(lv.reusability)}");
                sb.AppendLine($"  maintenanceCostPerDay: {FormatFloat(lv.MaintenanceCostPerDay)}");

                float? canBuyMaxPayload = _lvCanBuyMaxPayloadFi?.GetValue(lv) as float?;
                sb.AppendLine($"  canBuyMaxPayload: {FormatNullableFloat(canBuyMaxPayload)}");

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "launch_vehicles.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] launch_vehicles.yaml — {count} entries");
        }

        // ── Research ──────────────────────────────────────────────────────────

        static void DumpResearch(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var rd in allSO.AllResearchDefinition.List)
            {
                if (rd == null) continue;

                string title = rd.ID;
                try { title = rd.Title; } catch { }

                sb.AppendLine($"# {title.ToUpperInvariant()} (ResearchDefinition)");
                sb.AppendLine($"{YamlKey(rd.ID)}:");

                sb.AppendLine($"  isLocked: {FormatBool(rd.isLocked)}");
                sb.AppendLine($"  isLockedForUI: {FormatBool(rd.isLockedForUI)}");
                sb.AppendLine($"  workHourToComplete: {FormatFloat(rd.WorkHourToComplete)}");
                sb.AppendLine($"  researchType: {YamlScalar(rd.ResearchType?.ID)}");
                sb.AppendLine($"  researchSubType: {YamlScalar(rd.ResearchSubType?.ID)}");
                sb.AppendLine($"  stage: {rd.Stage}");
                int subStage = _rdSubStageFi != null ? (int)_rdSubStageFi.GetValue(rd) : 0;
                sb.AppendLine($"  subStage: {subStage}");
                bool showInTree = _rdShowInTreeFi != null && (bool)_rdShowInTreeFi.GetValue(rd);
                sb.AppendLine($"  showInTree: {FormatBool(showInTree)}");
                var rdParent = _rdParentFi?.GetValue(rd) as ResearchDefinition;
                sb.AppendLine($"  newViewResearchTreeParent: {YamlScalar(rdParent?.ID)}");

                var reqs = new List<string>();
                foreach (var req in rd.RequirementsResearch)
                    if (req != null) reqs.Add(req.ID);
                AppendStringList(sb, "  requirementsResearch", reqs);

                AppendResearchUnlocks(sb, rd);

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "research.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] research.yaml — {count} entries");
        }

        static readonly FieldInfo _rdSubStageFi       = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "subStage");
        static readonly FieldInfo _rdShowInTreeFi     = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "showInTree");
        static readonly FieldInfo _rdParentFi         = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "newViewResearchTreeParent");
        static readonly FieldInfo _rdUnlockDataFi     = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "unlockData");
        static readonly FieldInfo _rdUnlockDataListFi  = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "unlockDataList");
        static readonly Type      _udType              = typeof(ResearchDefinition).Assembly.GetType("Game.CompanyScripts.UnlockData");
        static readonly FieldInfo _udActionFi          = _udType?.GetField("actionUnlock", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udParam1Fi          = _udType?.GetField("parameter1",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udParam2Fi          = _udType?.GetField("parameter2",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udBonusFi           = _udType?.GetField("bonus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udBonusParamFi      = _udType?.GetField("bonusParameter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udIdsFi             = _udType?.GetField("id_ComponentOrOther", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udIdsShowUiFi       = _udType?.GetField("id_ComponentOrOtherBoolShowUI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udUnlockUiFi        = _udType?.GetField("unlockUIElement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udUnlockEndGameFi   = _udType?.GetField("unlockEndGame", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udUnlockContractAdvanceFi = _udType?.GetField("unlockContractAdvance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udStartGameEpochFi  = _udType?.GetField("startGameEpoch", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udAsteroidFi        = _udType?.GetField("idObjectInfoAsteroid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udAllowPullFi       = _udType?.GetField("idObjectInfoAllowPullToOrbit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static void AppendResearchUnlocks(StringBuilder sb, ResearchDefinition rd)
        {
            object single = _rdUnlockDataFi?.GetValue(rd);
            object listObj = _rdUnlockDataListFi?.GetValue(rd);
            var entries = new List<object>();
            if (single != null)
                entries.Add(single);
            if (listObj is Array arr)
                entries.AddRange(arr.Cast<object>().Where(item => item != null));
            if (entries.Count == 0) return;

            sb.AppendLine("  unlocks:");
            foreach (var entry in entries)
                AppendUnlockData(sb, entry);
        }

        static void AppendUnlockData(StringBuilder sb, object entry)
        {
            string action = _udActionFi?.GetValue(entry)?.ToString() ?? "None";
            string p1 = _udParam1Fi?.GetValue(entry) as string;
            string p2 = _udParam2Fi?.GetValue(entry) as string;

            sb.AppendLine($"    - action: {YamlScalar(action)}");

            if (!string.IsNullOrEmpty(p1))
                sb.AppendLine($"      id: {YamlScalar(p1)}");
            if (!string.IsNullOrEmpty(p2))
                sb.AppendLine($"      parameter2: {YamlScalar(p2)}");

            if (string.Equals(action, "UnlockBonus", StringComparison.OrdinalIgnoreCase))
            {
                string bonus = _udBonusFi?.GetValue(entry)?.ToString();
                if (!string.IsNullOrEmpty(bonus))
                    sb.AppendLine($"      bonus: {YamlScalar(bonus)}");

                object bonusParam = _udBonusParamFi?.GetValue(entry);
                if (bonusParam is float f)
                    sb.AppendLine($"      bonusParameter: {FormatFloat(f)}");
                else if (bonusParam is double d)
                    sb.AppendLine($"      bonusParameter: {FormatDouble(d)}");

                if (_udIdsFi?.GetValue(entry) is string[] ids && ids.Length > 0)
                {
                    sb.AppendLine("      targets:");
                    foreach (var id in ids.Where(x => !string.IsNullOrEmpty(x)))
                        sb.AppendLine($"        - {YamlScalar(id)}");
                }

                if (_udIdsShowUiFi?.GetValue(entry) is System.Collections.IEnumerable rawVisibility)
                {
                    var visibility = new List<(string id, string show)>();
                    foreach (var item in rawVisibility)
                    {
                        if (item == null) continue;
                        var itemType = item.GetType();
                        var f1 = itemType.GetField("Item1", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var f2 = itemType.GetField("Item2", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        string id = f1?.GetValue(item) as string;
                        object showObj = f2?.GetValue(item);
                        if (!string.IsNullOrEmpty(id) && showObj != null)
                            visibility.Add((id, showObj.ToString().ToLowerInvariant()));
                    }
                    if (visibility.Count > 0)
                    {
                        sb.AppendLine("      targetVisibility:");
                        foreach (var (id, show) in visibility)
                        {
                            sb.AppendLine($"        - id: {YamlScalar(id)}");
                            sb.AppendLine($"          showOnUI: {show}");
                        }
                    }
                }
            }

            var uiUnlock = _udUnlockUiFi?.GetValue(entry);
            if (HasNonDefaultEnumValue(uiUnlock))
                sb.AppendLine($"      unlockUIElement: {YamlScalar(uiUnlock.ToString())}");

            var endGameUnlock = _udUnlockEndGameFi?.GetValue(entry);
            if (HasNonDefaultEnumValue(endGameUnlock))
                sb.AppendLine($"      unlockEndGame: {YamlScalar(endGameUnlock.ToString())}");

            var contractAdvance = _udUnlockContractAdvanceFi?.GetValue(entry);
            if (HasNonDefaultEnumValue(contractAdvance))
                sb.AppendLine($"      unlockContractAdvance: {YamlScalar(contractAdvance.ToString())}");

            object epoch = _udStartGameEpochFi?.GetValue(entry);
            if (epoch != null)
            {
                var idProp = epoch.GetType().GetProperty("ID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                string epochId = idProp?.GetValue(epoch) as string;
                if (!string.IsNullOrEmpty(epochId))
                    sb.AppendLine($"      startGameEpoch: {YamlScalar(epochId)}");
            }

            object asteroidId = _udAsteroidFi?.GetValue(entry);
            if (asteroidId is int asteroidInt && asteroidInt >= 0)
                sb.AppendLine($"      idObjectInfoAsteroid: {asteroidInt}");

            object allowPullId = _udAllowPullFi?.GetValue(entry);
            if (allowPullId is int allowPullInt && allowPullInt >= 0)
                sb.AppendLine($"      idObjectInfoAllowPullToOrbit: {allowPullInt}");
        }

        // ── Contracts ──────────────────────────────────────────────────────────

        static readonly FieldInfo _cdOverrideTranslationFi = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "overrideTranslation");
        static readonly FieldInfo _cdRewardsFi             = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "rewards");
        static readonly FieldInfo _cdRewardsStartFi        = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "rewardsStartContract");
        static readonly FieldInfo _cdObjectivesFi          = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "objectives");
        static readonly FieldInfo _cdHelpObjectivesFi      = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "helpObjectives");
        static readonly FieldInfo _cdHideUIFi              = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "hideUI");
        static readonly FieldInfo _cdSkipForAIFi           = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "skipForAI");
        static readonly FieldInfo _cdIsFinalFi             = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "isFinalContract");
        static readonly FieldInfo _cdIsFinalDemoFi         = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "isFinalContractToEndDemo");
        static readonly FieldInfo _cdYearsToExpireFi       = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "yearsToExpire");
        static readonly FieldInfo _cdDateStartFi           = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "dateTimeStringStart");
        static readonly FieldInfo _cdDateStartLimitFi      = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "dateTimeStringStartLimit");
        static readonly FieldInfo _cdDateStartEnableFi     = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "dateTimeStringStartEnable");
        static readonly FieldInfo _cdStageTutorialFi       = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "stageTutorialToActive");
        static readonly FieldInfo _cdTextNextContractFi    = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "textToShowNextContractKeyId");
        static readonly FieldInfo _cdUnlockContractFi      = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "unlockContractHelpNotUse");
        static readonly FieldInfo _cdIsLockedByFi          = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "isLockByContractHelpNotUse");
        static readonly FieldInfo _cdUnlockResearchFi      = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "unlockResearchDefinitionHelpNotUse");

        static string ObjectName(ObjectInfoManager oim, int id)
        {
            if (id < 0) return null;
            try
            {
                var oi = oim.GetByID(id);
                if (oi != null) return oi.ObjectName;
            }
            catch { }
            return null;
        }

        static string FormatObjectId(ObjectInfoManager oim, int id)
        {
            string name = ObjectName(oim, id);
            if (name != null)
                return YamlScalar(name);
            return id.ToString();
        }

        static void DumpContracts(AllScriptableObjectManager allSO, ObjectInfoManager oim, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var cd in allSO.AllContract.List)
            {
                if (cd == null) continue;

                string title = cd.ID;
                try { title = LEManager.Get(cd.ID + "_Title"); } catch { }
                string fluff = null;
                try { fluff = LEManager.Get(cd.ID + "_fluff"); } catch { }
                string fluffEnd = null;
                try { fluffEnd = LEManager.Get(cd.ID + "_fluffEnd"); } catch { }

                sb.AppendLine($"# {title}");
                sb.AppendLine($"{YamlKey(cd.ID)}:");
                sb.AppendLine($"  title: {YamlScalar(title)}");
                if (!string.IsNullOrEmpty(fluff) && fluff != cd.ID + "_fluff")
                    sb.AppendLine($"  description: {YamlScalar(fluff)}");
                if (!string.IsNullOrEmpty(fluffEnd) && fluffEnd != cd.ID + "_fluffEnd")
                    sb.AppendLine($"  descriptionEnd: {YamlScalar(fluffEnd)}");

                sb.AppendLine($"  isLocked: {FormatBool(cd.isLocked)}");

                bool hideUI = _cdHideUIFi != null && (bool)_cdHideUIFi.GetValue(cd);
                sb.AppendLine($"  hideUI: {FormatBool(hideUI)}");

                bool skipForAI = _cdSkipForAIFi != null && (bool)_cdSkipForAIFi.GetValue(cd);
                sb.AppendLine($"  skipForAI: {FormatBool(skipForAI)}");

                bool isFinal = _cdIsFinalFi != null && (bool)_cdIsFinalFi.GetValue(cd);
                sb.AppendLine($"  isFinalContract: {FormatBool(isFinal)}");

                bool isFinalDemo = _cdIsFinalDemoFi != null && (bool)_cdIsFinalDemoFi.GetValue(cd);
                if (isFinalDemo) sb.AppendLine($"  isFinalContractToEndDemo: true");

                sb.AppendLine($"  waitForComplete: {FormatBool(cd.waitForComplete)}");
                sb.AppendLine($"  showUIAvailable: {FormatBool(cd.showUIAvailable)}");
                sb.AppendLine($"  cancelOn: {FormatBool(cd.cancelOn)}");

                int yearsToExpire = _cdYearsToExpireFi != null ? (int)_cdYearsToExpireFi.GetValue(cd) : 2;
                sb.AppendLine($"  yearsToExpire: {yearsToExpire}");

                int stageTutorial = _cdStageTutorialFi != null ? (int)_cdStageTutorialFi.GetValue(cd) : int.MaxValue;
                if (stageTutorial != int.MaxValue)
                    sb.AppendLine($"  stageTutorialToActive: {stageTutorial}");

                bool dateStartEnable = _cdDateStartEnableFi != null && (bool)_cdDateStartEnableFi.GetValue(cd);
                if (dateStartEnable)
                {
                    sb.AppendLine($"  dateTimeStringStartEnable: true");
                    string dateStart = _cdDateStartFi?.GetValue(cd) as string;
                    if (!string.IsNullOrEmpty(dateStart))
                        sb.AppendLine($"  dateTimeStringStart: {YamlScalar(dateStart)}");
                    string dateStartLimit = _cdDateStartLimitFi?.GetValue(cd) as string;
                    if (!string.IsNullOrEmpty(dateStartLimit))
                        sb.AppendLine($"  dateTimeStringStartLimit: {YamlScalar(dateStartLimit)}");
                }

                if (!string.IsNullOrEmpty(cd.dateStartActive))
                    sb.AppendLine($"  dateStartActive: {YamlScalar(cd.dateStartActive)}");

                string overrideTranslation = _cdOverrideTranslationFi?.GetValue(cd) as string;
                if (!string.IsNullOrEmpty(overrideTranslation))
                    sb.AppendLine($"  overrideTranslation: {YamlScalar(overrideTranslation)}");

                string textNextContract = _cdTextNextContractFi?.GetValue(cd) as string;
                if (!string.IsNullOrEmpty(textNextContract))
                    sb.AppendLine($"  textToShowNextContractKeyId: {YamlScalar(textNextContract)}");

                // Contract chain references
                var unlockContract = _cdUnlockContractFi?.GetValue(cd) as ContractDefinition;
                if (unlockContract != null)
                    sb.AppendLine($"  unlockContract: {YamlScalar(unlockContract.ID)}");
                var isLockedBy = _cdIsLockedByFi?.GetValue(cd) as ContractDefinition;
                if (isLockedBy != null)
                    sb.AppendLine($"  isLockedByContract: {YamlScalar(isLockedBy.ID)}");
                var unlockResearch = _cdUnlockResearchFi?.GetValue(cd) as ResearchDefinition;
                if (unlockResearch != null)
                    sb.AppendLine($"  unlockResearch: {YamlScalar(unlockResearch.ID)}");

                // Delivery-related fields on the ContractDefinition itself
                if (cd.whatResource != null)
                    sb.AppendLine($"  whatResource: {YamlScalar(cd.whatResource.ID)}");
                if (cd.whatSpaceModuleDescriptor != null)
                    sb.AppendLine($"  whatSpaceModuleDescriptor: {YamlScalar(cd.whatSpaceModuleDescriptor.ID)}");
                if (cd.launchVehicleType != null)
                    sb.AppendLine($"  launchVehicleType: {YamlScalar(cd.launchVehicleType.ID)}");
                if (cd.howMuch > 0f)
                    sb.AppendLine($"  howMuch: {FormatFloat(cd.howMuch)}");
                if (cd.targetID >= 0)
                    sb.AppendLine($"  targetID: {FormatObjectId(oim, cd.targetID)}");
                if (cd.startID >= 0)
                    sb.AppendLine($"  startID: {FormatObjectId(oim, cd.startID)}");
                if (cd.sendFromAsteroid)
                    sb.AppendLine($"  sendFromAsteroid: true");
                if (cd.timeLandAndReturnLandTimeInDay > 0f)
                    sb.AppendLine($"  timeLandAndReturnLandTimeInDay: {FormatFloat(cd.timeLandAndReturnLandTimeInDay)}");

                // Rewards on start
                AppendRewardList(sb, "  rewardsStartContract", cd.RewardsStartContract);

                // Objectives
                AppendObjectiveList(sb, "  objectives", cd.Objectives, oim);

                // Help objectives
                var helpObjectives = _cdHelpObjectivesFi?.GetValue(cd) as List<Objective>;
                if (helpObjectives != null && helpObjectives.Count > 0)
                    AppendObjectiveList(sb, "  helpObjectives", helpObjectives, oim);

                // Rewards on completion
                AppendRewardList(sb, "  rewards", cd.Rewards);

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "contracts.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] contracts.yaml — {count} entries");
        }

        static void AppendRewardList(StringBuilder sb, string keyWithIndent, List<Reward> rewards)
        {
            if (rewards == null || rewards.Count == 0) return;
            sb.AppendLine($"{keyWithIndent}:");
            foreach (var reward in rewards)
            {
                sb.AppendLine($"    - rewardType: {YamlScalar(reward.rewardType.ToString())}");
                if (reward.amount != 0)
                    sb.AppendLine($"      amount: {reward.amount}");
                if (reward.resourceDefinition != null)
                    sb.AppendLine($"      resource: {YamlScalar(reward.resourceDefinition.ID)}");
                if (reward.facilityBaseDescriptor != null)
                    sb.AppendLine($"      facility: {YamlScalar(reward.facilityBaseDescriptor.ID)}");
                if (reward.spaceCraftType != null)
                    sb.AppendLine($"      spacecraft: {YamlScalar(reward.spaceCraftType.ID)}");
                if (reward.launchVehicleType != null)
                    sb.AppendLine($"      launchVehicle: {YamlScalar(reward.launchVehicleType.ID)}");
                if (reward.buildImmediately)
                    sb.AppendLine($"      buildImmediately: true");
                if (reward.rewardType == EReward.Unlock && reward.unlockData != null)
                    AppendContractUnlockData(sb, "      ", reward.unlockData);
            }
        }

        static void AppendContractUnlockData(StringBuilder sb, string indent, UnlockData ud)
        {
            sb.AppendLine($"{indent}unlock:");
            sb.AppendLine($"{indent}  action: {YamlScalar(ud.actionUnlock.ToString())}");
            if (!string.IsNullOrEmpty(ud.parameter1))
                sb.AppendLine($"{indent}  id: {YamlScalar(ud.parameter1)}");
            if (!string.IsNullOrEmpty(ud.parameter2))
                sb.AppendLine($"{indent}  parameter2: {YamlScalar(ud.parameter2)}");
            if (ud.actionUnlock == EActionUnlock.UnlockBonus)
            {
                sb.AppendLine($"{indent}  bonus: {YamlScalar(ud.bonus.ToString())}");
                sb.AppendLine($"{indent}  bonusParameter: {FormatFloat(ud.bonusParameter)}");
                if (ud.id_ComponentOrOther != null && ud.id_ComponentOrOther.Length > 0)
                {
                    sb.AppendLine($"{indent}  targets:");
                    foreach (var id in ud.id_ComponentOrOther.Where(x => !string.IsNullOrEmpty(x)))
                        sb.AppendLine($"{indent}    - {YamlScalar(id)}");
                }
            }
            if (ud.unlockContractAdvance != UnlockData.EUnlockContractAdvance.None)
                sb.AppendLine($"{indent}  unlockContractAdvance: {YamlScalar(ud.unlockContractAdvance.ToString())}");
        }

        static void AppendObjectiveList(StringBuilder sb, string keyWithIndent, List<Objective> objectives, ObjectInfoManager oim)
        {
            if (objectives == null || objectives.Count == 0) return;
            sb.AppendLine($"{keyWithIndent}:");
            foreach (var obj in objectives)
            {
                sb.AppendLine($"    - id: {YamlScalar(obj.ID)}");
                sb.AppendLine($"      objectiveType: {YamlScalar(obj.objectiveType.ToString())}");

                if (obj.productItem != null)
                    sb.AppendLine($"      productItem: {YamlScalar(obj.productItem.ID)}");
                if (obj.productItem2 != null)
                    sb.AppendLine($"      productItem2: {YamlScalar(obj.productItem2.ID)}");
                if (obj.fromList && obj.productItems != null && obj.productItems.Length > 0)
                {
                    sb.AppendLine($"      fromList: true");
                    sb.AppendLine($"      productItems:");
                    foreach (var pi in obj.productItems)
                        if (pi != null)
                            sb.AppendLine($"        - {YamlScalar(pi.ID)}");
                }

                if (obj.howMuch != 0f)
                    sb.AppendLine($"      howMuch: {FormatFloat(obj.howMuch)}");
                if (obj.howMuch2 != 0f)
                    sb.AppendLine($"      howMuch2: {FormatFloat(obj.howMuch2)}");
                if (obj.fromID != 0)
                    sb.AppendLine($"      fromID: {FormatObjectId(oim, obj.fromID)}");
                if (obj.toID != 0)
                    sb.AppendLine($"      toID: {FormatObjectId(oim, obj.toID)}");

                if (!string.IsNullOrEmpty(obj.fromIDString))
                    sb.AppendLine($"      fromIDString: {YamlScalar(obj.fromIDString)}");
                if (!string.IsNullOrEmpty(obj.toIDString))
                    sb.AppendLine($"      toIDString: {YamlScalar(obj.toIDString)}");

                if (obj.objectiveType == EObjectiveType.Deliver || obj.objectiveType == EObjectiveType.Possession)
                    sb.AppendLine($"      possessionType: {YamlScalar(obj.possessionType.ToString())}");

                if (obj.objectiveType == EObjectiveType.Deliver)
                {
                    sb.AppendLine($"      resourceTypeType: {YamlScalar(obj.resourceTypeType.ToString())}");
                    if (obj.deliverEntireAsteroid)
                        sb.AppendLine($"      deliverEntireAsteroid: true");
                    if (obj.checkCrewInCargoMustBe)
                        sb.AppendLine($"      checkCrewInCargoMustBe: true");
                    if (obj.justScheduleToComplete)
                        sb.AppendLine($"      justScheduleToComplete: true");
                }

                if (obj.advance)
                {
                    sb.AppendLine($"      advance: true");
                    if (obj.moonOfID != -1)
                        sb.AppendLine($"      moonOfID: {FormatObjectId(oim, obj.moonOfID)}");
                    if (!string.IsNullOrEmpty(obj.objectSubTypeID))
                        sb.AppendLine($"      objectSubTypeID: {YamlScalar(obj.objectSubTypeID)}");
                    if (obj.advanceObjectTypes != EObjectTypes.None)
                        sb.AppendLine($"      advanceObjectTypes: {YamlScalar(obj.advanceObjectTypes.ToString())}");
                }

                if (obj.asteroidPullingNeedDeposit != null && obj.asteroidPullingNeedDeposit.Count > 0)
                {
                    sb.AppendLine($"      asteroidPullingNeedDeposit:");
                    foreach (var rd in obj.asteroidPullingNeedDeposit)
                        if (rd != null) sb.AppendLine($"        - {YamlScalar(rd.ID)}");
                }

                if (obj.helpSpaceCraftType != null)
                    sb.AppendLine($"      helpSpaceCraftType: {YamlScalar(obj.helpSpaceCraftType.ID)}");

                if (obj.needPreviousObjective)
                    sb.AppendLine($"      needPreviousObjective: true");
                if (!obj.checkCompletedOnStart)
                    sb.AppendLine($"      checkCompletedOnStart: false");
                if (obj.dummy)
                    sb.AppendLine($"      dummy: true");
                if (obj.dummyVersion1)
                    sb.AppendLine($"      dummyVersion1: true");
                if (!string.IsNullOrEmpty(obj.dummyAlternativeIDTranslate))
                    sb.AppendLine($"      dummyAlternativeIDTranslate: {YamlScalar(obj.dummyAlternativeIDTranslate)}");

                if (obj.afterFindAutomaticFillFromID)
                    sb.AppendLine($"      afterFindAutomaticFillFromID: true");
                if (obj.afterBuildAutomaticFillToID)
                    sb.AppendLine($"      afterBuildAutomaticFillToID: true");
                if (obj.afterBuildAutomaticFillToIDObjectNameHighLightShort)
                    sb.AppendLine($"      afterBuildAutomaticFillToIDObjectNameHighLightShort: true");
                if (!string.IsNullOrEmpty(obj.fromIDUseTranslateBeforeBuild))
                    sb.AppendLine($"      fromIDUseTranslateBeforeBuild: {YamlScalar(obj.fromIDUseTranslateBeforeBuild)}");
                if (!string.IsNullOrEmpty(obj.toIDUseTranslateBeforeBuild))
                    sb.AppendLine($"      toIDUseTranslateBeforeBuild: {YamlScalar(obj.toIDUseTranslateBeforeBuild)}");

                if (!string.IsNullOrEmpty(obj.dateTimeStringLimit))
                    sb.AppendLine($"      dateTimeStringLimit: {YamlScalar(obj.dateTimeStringLimit)}");
                if (!string.IsNullOrEmpty(obj.dateTimeStringLimit2))
                    sb.AppendLine($"      dateTimeStringLimit2: {YamlScalar(obj.dateTimeStringLimit2)}");

                if (!string.IsNullOrEmpty(obj.helpObjectiveID))
                    sb.AppendLine($"      helpObjectiveID: {YamlScalar(obj.helpObjectiveID)}");

                if (!string.IsNullOrEmpty(obj.objectiveToMarkDoneAfterThisObjective))
                    sb.AppendLine($"      objectiveToMarkDoneAfterThisObjective: {YamlScalar(obj.objectiveToMarkDoneAfterThisObjective)}");

                if (obj.showWarningInPlanMission)
                    sb.AppendLine($"      showWarningInPlanMission: true");

                if (obj.objectiveType == EObjectiveType.SelectLayer)
                    sb.AppendLine($"      layer: {YamlScalar(obj.layer.ToString())}");

                // Cyclical mission data
                if (obj.scheduleCyclicalFlyDataObjective != null && obj.objectiveType == EObjectiveType.ScheduleCyclicalMission)
                    AppendScheduleCyclicalData(sb, "      ", obj.scheduleCyclicalFlyDataObjective);

                // Markets offers data
                if (obj.marketsOffersObjectiveData != null && obj.objectiveType == EObjectiveType.MarketPlaceOffers)
                {
                    sb.AppendLine($"      marketsOffers:");
                    sb.AppendLine($"        sellOffers: {FormatBool(obj.marketsOffersObjectiveData.sellOffers)}");
                    if (obj.marketsOffersObjectiveData.rd != null)
                        sb.AppendLine($"        resource: {YamlScalar(obj.marketsOffersObjectiveData.rd.ID)}");
                    sb.AppendLine($"        howMuch: {obj.marketsOffersObjectiveData.howMuch}");
                    if (obj.marketsOffersObjectiveData.IDObjectInfo?.Object != null)
                        sb.AppendLine($"        location: {YamlScalar(obj.marketsOffersObjectiveData.IDObjectInfo.Object.ObjectName)}");
                }

                // Change habitability data
                if (obj.changeHabitabilityParametersObjectiveData != null && obj.objectiveType == EObjectiveType.ChangeHabitabilityParameters)
                {
                    var chpd = obj.changeHabitabilityParametersObjectiveData;
                    sb.AppendLine($"      changeHabitability:");
                    sb.AppendLine($"        parameter: {YamlScalar(chpd.whatHabitabilityParameterToChange.ToString())}");
                    sb.AppendLine($"        comparingType: {YamlScalar(chpd.comparingType.ToString())}");
                    sb.AppendLine($"        toCompare: {FormatFloat(chpd.toCompare)}");
                    if (chpd.comparingType == Objective.ChangeHabitabilityParametersObjectiveData.EComparingType.Between)
                    {
                        sb.AppendLine($"        min: {FormatFloat(chpd.Min)}");
                        sb.AppendLine($"        max: {FormatFloat(chpd.Max)}");
                    }
                    if (chpd.IDObjectInfo?.Object != null)
                        sb.AppendLine($"        location: {YamlScalar(chpd.IDObjectInfo.Object.ObjectName)}");
                }

                // Change deposit data
                if (obj.changeDepositParametersObjectiveData != null && obj.objectiveType == EObjectiveType.ChangeDeposit)
                {
                    var cdpd = obj.changeDepositParametersObjectiveData;
                    sb.AppendLine($"      changeDeposit:");
                    if (cdpd.rd != null)
                        sb.AppendLine($"        resource: {YamlScalar(cdpd.rd.ID)}");
                    sb.AppendLine($"        howMuch: {cdpd.howMuch}");
                    if (cdpd.IDObjectInfo?.Object != null)
                        sb.AppendLine($"        location: {YamlScalar(cdpd.IDObjectInfo.Object.ObjectName)}");
                }

                // Exploration interstellar data
                if (obj.explorationInterstellarData != null && obj.objectiveType == EObjectiveType.ExplorationInterstellar)
                {
                    var eid = obj.explorationInterstellarData;
                    sb.AppendLine($"      explorationInterstellar:");
                    sb.AppendLine($"        condition: {YamlScalar(eid.condition.ToString())}");
                    sb.AppendLine($"        number: {eid.number}");
                    if (eid.list != null && eid.list.Count > 0)
                    {
                        sb.AppendLine($"        objects:");
                        foreach (var obj2 in eid.list)
                        {
                            sb.AppendLine($"          - checkMass: {FormatBool(obj2.checkMass)}");
                            sb.AppendLine($"            minMass: {FormatFloat(obj2.minMass)}");
                            sb.AppendLine($"            maxMass: {FormatFloat(obj2.maxMass)}");
                            sb.AppendLine($"            checkHabitableZone: {FormatBool(obj2.checkHabitableZone)}");
                        }
                    }
                }

                // Tutorial/UI flags (only emit non-defaults)
                if (!obj.tutorialOnForThisObjective)
                    sb.AppendLine($"      tutorialOnForThisObjective: false");
                if (obj.showShortTutorial)
                    sb.AppendLine($"      showShortTutorial: true");
                if (!string.IsNullOrEmpty(obj.idTextShortTutorial))
                    sb.AppendLine($"      idTextShortTutorial: {YamlScalar(obj.idTextShortTutorial)}");
                if (obj.showDragAndDrop)
                    sb.AppendLine($"      showDragAndDrop: true");
                if (obj.fake)
                    sb.AppendLine($"      fake: true");
                if (obj.thisHelpObjective)
                    sb.AppendLine($"      thisHelpObjective: true");
            }
        }

        static void AppendScheduleCyclicalData(StringBuilder sb, string indent, ScheduleCyclicalFlyDataObjective data)
        {
            sb.AppendLine($"{indent}scheduleCyclicalFlyData:");
            var dataType = data.GetType();
            foreach (var fi in dataType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object val = fi.GetValue(data);
                if (val == null) continue;
                if (val is bool b)
                    sb.AppendLine($"{indent}  {fi.Name}: {FormatBool(b)}");
                else if (val is float f)
                    sb.AppendLine($"{indent}  {fi.Name}: {FormatFloat(f)}");
                else if (val is int i)
                    sb.AppendLine($"{indent}  {fi.Name}: {i}");
                else if (val is string s && !string.IsNullOrEmpty(s))
                    sb.AppendLine($"{indent}  {fi.Name}: {YamlScalar(s)}");
                else if (val is Enum)
                    sb.AppendLine($"{indent}  {fi.Name}: {YamlScalar(val.ToString())}");
            }
        }

        // ── YAML emit helpers ─────────────────────────────────────────────────

        static void AppendResourceTerraformationInfo(StringBuilder sb, string keyWithIndent, ResourceDefinition.TerraformationInfoDef info)
        {
            sb.AppendLine($"{keyWithIndent}:");
            string indent = new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            sb.AppendLine($"{indent}resourceOpticalDepthParameter: {FormatDouble(info.resourceOpticalDepthParameter)}");
            sb.AppendLine($"{indent}resourceHeatCapacity: {FormatDouble(info.resourceHeatCapacity)}");
            sb.AppendLine($"{indent}vaporizationLatentHeat: {FormatDouble(info.vaporizationLatentHeat)}");
            sb.AppendLine($"{indent}baseTemperatureBoiling: {FormatDouble(info.baseTemperatureBoiling)}");
            sb.AppendLine($"{indent}temperatureMelting: {FormatDouble(info.temperatureMelting)}");
            sb.AppendLine($"{indent}pressureTriplePoint: {FormatDouble(info.pressureTriplePoint)}");
        }

        static void AppendAnimationCurve(StringBuilder sb, string keyWithIndent, AnimationCurve curve)
        {
            if (curve == null || curve.keys == null || curve.keys.Length == 0)
                return;

            sb.AppendLine($"{keyWithIndent}:");
            string indent = new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            sb.AppendLine($"{indent}preWrapMode: {YamlScalar(curve.preWrapMode.ToString())}");
            sb.AppendLine($"{indent}postWrapMode: {YamlScalar(curve.postWrapMode.ToString())}");
            sb.AppendLine($"{indent}keys:");
            foreach (var frame in curve.keys)
            {
                sb.AppendLine($"{indent}  - time: {FormatFloat(frame.time)}");
                sb.AppendLine($"{indent}    value: {FormatFloat(frame.value)}");
                sb.AppendLine($"{indent}    inTangent: {FormatFloat(frame.inTangent)}");
                sb.AppendLine($"{indent}    outTangent: {FormatFloat(frame.outTangent)}");
                sb.AppendLine($"{indent}    weightedMode: {YamlScalar(frame.weightedMode.ToString())}");
                sb.AppendLine($"{indent}    inWeight: {FormatFloat(frame.inWeight)}");
                sb.AppendLine($"{indent}    outWeight: {FormatFloat(frame.outWeight)}");
            }
        }

        static T FindFieldValue<T>(object target, string fieldName) where T : class
        {
            if (target == null) return null;
            var fi = ScriptableObjectPatcher.FindField(target.GetType(), fieldName);
            return fi?.GetValue(target) as T;
        }

        static RowExploredResourcesData FindPreferredExploredRow(ObjectInfo oi, RowResourcesData deposit)
        {
            if (oi?.ObjectsInfoData == null || deposit == null)
                return null;

            var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
            var preferredData = player != null
                ? oi.ObjectsInfoData.FirstOrDefault(d => d?.company == player)
                : null;
            if (preferredData == null)
                preferredData = oi.ObjectsInfoData.FirstOrDefault(d => d != null);
            if (preferredData?.listExploredResourcesRows == null)
                return null;

            return preferredData.listExploredResourcesRows.FirstOrDefault(r => r?.ObservedData == deposit);
        }

        /// <summary>Returns a YAML-safe scalar. Quotes the value if it contains special chars.</summary>
        static string YamlScalar(string value)
        {
            if (value == null) return "null";
            if (value.Length == 0) return "\"\"";
            // Quote if starts with special char or contains colon-space, #, or other YAML metacharacters
            bool needsQuotes = value[0] == '\'' || value[0] == '"' || value[0] == '&'
                            || value[0] == '*'  || value[0] == '!' || value[0] == '|'
                            || value[0] == '>'  || value[0] == '%' || value[0] == '@'
                            || value[0] == '`'  || value[0] == '{'  || value[0] == '['
                            || value.Contains(": ") || value.Contains(" #")
                            || value.Contains("\n") || value.Contains("\\");
            if (needsQuotes)
                return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return value;
        }

        /// <summary>Returns a YAML-safe mapping key (no quoting needed for typical IDs).</summary>
        static string YamlKey(string value)
        {
            if (value == null) return "\"\"";
            return value;
        }

        static string FormatFloat(float v)   => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        static string FormatDouble(double v)  => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        static string FormatBool(bool v)      => v ? "true" : "false";

        static string FormatNullableFloat(float? v)
        {
            if (v == null) return "null";
            return FormatFloat(v.Value);
        }

        static string FormatNullableDouble(double? v)
        {
            if (v == null) return "null";
            return FormatDouble(v.Value);
        }

        static bool HasNonDefaultEnumValue(object value)
        {
            if (value == null)
                return false;

            Type type = value.GetType();
            if (!type.IsEnum)
                return true;

            return Convert.ToInt32(value) != 0;
        }

        static void AppendCompanyCost(StringBuilder sb, string keyWithIndent, AI.CompanyCost cost)
        {
            sb.AppendLine($"{keyWithIndent}:");
            string indent = keyWithIndent.Length - keyWithIndent.TrimStart().Length == 0
                ? "  "
                : new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            sb.AppendLine($"{indent}money: {FormatDouble(cost.Money)}");
            sb.AppendLine($"{indent}time: {FormatDouble(cost.Time)}");
        }

        /// <summary>Appends a dict as YAML mapping. Skips empty/null values.</summary>
        static void AppendDict(StringBuilder sb, string keyWithIndent, Dictionary<string, double> dict)
        {
            if (dict == null || dict.Count == 0)
                return;
            sb.AppendLine($"{keyWithIndent}:");
            string indent = keyWithIndent.Length - keyWithIndent.TrimStart().Length == 0
                ? "  "
                : new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            foreach (var kv in dict)
                sb.AppendLine($"{indent}{YamlKey(kv.Key)}: {FormatDouble(kv.Value)}");
        }

        /// <summary>Appends a list of strings as YAML sequence. Skips empty/null values.</summary>
        static void AppendStringList(StringBuilder sb, string keyWithIndent, List<string> list)
        {
            if (list == null || list.Count == 0)
                return;
            sb.AppendLine($"{keyWithIndent}:");
            string indent = keyWithIndent.Length - keyWithIndent.TrimStart().Length == 0
                ? "  "
                : new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            foreach (var item in list)
                sb.AppendLine($"{indent}- {YamlScalar(item)}");
        }

        /// <summary>Appends byproduct entries as YAML sequence. Skips empty/null values.</summary>
        static void AppendByproducts(StringBuilder sb, string keyWithIndent, List<ByproductEntry> list)
        {
            if (list == null || list.Count == 0)
                return;
            sb.AppendLine($"{keyWithIndent}:");
            string indent = keyWithIndent.Length - keyWithIndent.TrimStart().Length == 0
                ? "  "
                : new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            foreach (var bp in list)
            {
                sb.AppendLine($"{indent}- resource: {YamlScalar(bp.Resource)}");
                sb.AppendLine($"{indent}  rate: {FormatDouble(bp.Rate)}");
                sb.AppendLine($"{indent}  state: {YamlScalar(bp.State)}");
            }
        }

        // ── Data classes ──────────────────────────────────────────────────────

        class ByproductEntry
        {
            public string Resource { get; set; }
            public double Rate     { get; set; }
            public string State    { get; set; }
        }
    }
}
