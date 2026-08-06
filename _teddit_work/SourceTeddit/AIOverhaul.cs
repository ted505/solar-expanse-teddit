using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AI;
using AI.Actions;
using AI.Conditionals;
using BehaviorDesigner.Runtime;
using BDTask = BehaviorDesigner.Runtime.Tasks.Task;
using BDTaskStatus = BehaviorDesigner.Runtime.Tasks.TaskStatus;
using Data;
using Data.ScriptableObject;
using CustomUpdate;
using Game;
using Game.ObjectInfoDataScripts;
using Game.ContractsObjectives;
using Game.Info;
using Game.UI.Windows.Elements.PlanMissionElements;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;
using UnityEngine;
using TaskStatus = BehaviorDesigner.Runtime.Tasks.TaskStatus;

namespace Teddit
{
    /// <summary>
    /// Strategic overlay for the stock Solar Expanse AI.
    ///
    /// We are not replacing the Behavior Designer trees wholesale. Instead, we patch the
    /// weak seams we identified during debugging:
    ///  - body/source ordering is too local and ignores existing company footholds
    ///  - research selection is random instead of goal-directed
    ///  - UnlockProductionMeans is a stub and never bootstraps missing infrastructure
    ///
    /// The goal is to preserve the game's existing task graph while giving it a more
    /// useful strategic backbone.
    /// </summary>
    internal static class AIOverhaul
    {
        const long HomeCrewReserveTarget = 500L;

        internal static readonly FieldInfo _pickResearchFi =
            AccessTools.Field(typeof(PickRandomResearch), "research");

        static readonly FieldInfo _unlockProductionWhereFi =
            AccessTools.Field(typeof(UnlockProductionMeans), "where");

        static readonly FieldInfo _rdUnlockDataFi =
            AccessTools.Field(typeof(ResearchDefinition), "unlockData");

        static readonly FieldInfo _rdUnlockDataListFi =
            AccessTools.Field(typeof(ResearchDefinition), "unlockDataList");

        internal static readonly FieldInfo _doContractContractFi =
            AccessTools.Field(typeof(DoContract), "contract");

        internal static readonly FieldInfo _flightCargoElementFi =
            AccessTools.Field(typeof(FlightOrDelivery), "cargoElement");

        internal static readonly FieldInfo _flightPlanResultFi =
            AccessTools.Field(typeof(FlightOrDelivery), "planFlyCodeResult");

        internal static readonly FieldInfo _flightFromFi =
            AccessTools.Field(typeof(FlightOrDelivery), "from");

        internal static readonly FieldInfo _flightToFi =
            AccessTools.Field(typeof(FlightOrDelivery), "to");

        internal static readonly FieldInfo _flightSpacecraftFi =
            AccessTools.Field(typeof(FlightOrDelivery), "spacecraft");

        internal static readonly FieldInfo _flightLaunchVehicleFi =
            AccessTools.Field(typeof(FlightOrDelivery), "launchVehicle");

        internal static readonly FieldInfo _flightSpaceModuleFi =
            AccessTools.Field(typeof(FlightOrDelivery), "spaceModule");

        internal static readonly FieldInfo _flightMadeFuelReservationFi =
            AccessTools.Field(typeof(FlightOrDelivery), "madeFuelReservation");

        internal static readonly FieldInfo _flightMadeCargoReservationFi =
            AccessTools.Field(typeof(FlightOrDelivery), "madeCargoReservation");

        internal static readonly FieldInfo _flightHowMuchFi =
            AccessTools.Field(typeof(FlightOrDelivery), "howMuch");

        internal static readonly FieldInfo _flightQuantityFi =
            AccessTools.Field(typeof(FlightOrDelivery), "quantity");

        internal static readonly FieldInfo _flightFuelNeedFi =
            AccessTools.Field(typeof(FlightOrDelivery), "fuelNeed");

        internal static readonly FieldInfo _flightFuelResourceTypeFi =
            AccessTools.Field(typeof(FlightOrDelivery), "fuelResourceType");

        internal static readonly FieldInfo _flightPmpFi =
            AccessTools.Field(typeof(FlightOrDelivery), "pmp");

        static readonly FieldInfo _companyHasEnoughResourceWhereFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "where");

        static readonly FieldInfo _companyHasEnoughResourceWhatFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "what");

        static readonly FieldInfo _companyHasEnoughResourceHowMuchFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "howMuch");

        static readonly FieldInfo _companyHasEnoughResourceCreateDemandFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "createDemand");

        static readonly FieldInfo _companyHasEnoughResourceMakeReservationFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "makeAReservation");

        static readonly FieldInfo _companyHasEnoughResourceRemainingAmountFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "remainingAmount");

        static readonly FieldInfo _companyHasEnoughResourceOnPlanetFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "companyResourceOnPlanet");

        static readonly FieldInfo _companyHasEnoughResourceDataFi =
            AccessTools.Field(typeof(CompanyHasEnoughResource), "data");

        static readonly Type _companyHasEnoughResourceDataType =
            typeof(CompanyHasEnoughResource).GetNestedType("Data", BindingFlags.NonPublic);

        static readonly FieldInfo _companyHasEnoughResourceMadeReservationsFi =
            _companyHasEnoughResourceDataType != null ? AccessTools.Field(_companyHasEnoughResourceDataType, "madeReservations") : null;

        static readonly Type _unlockDataType =
            typeof(ResearchDefinition).Assembly.GetType("Game.CompanyScripts.UnlockData");

        static readonly FieldInfo _udActionFi =
            _unlockDataType != null ? AccessTools.Field(_unlockDataType, "actionUnlock") : null;

        static readonly FieldInfo _udParameter1Fi =
            _unlockDataType != null ? AccessTools.Field(_unlockDataType, "parameter1") : null;

        internal sealed class ObjectiveNeed
        {
            public ObjectInfo Target;
            public MyIDScriptableObject Product;
            public ResourceDefinition Resource;
            public EObjectiveType Type;
        }

        internal sealed class UnlockInfo
        {
            public string Action;
            public string Id;
        }

        internal sealed class ManifestResourceDemand
        {
            public ResourceDefinition Resource;
            public double StockDemand;
            public double TotalDemand;
        }

        static readonly Dictionary<string, (int year, int month)> _lastWorkforcePass =
            new Dictionary<string, (int year, int month)>();

        static readonly Dictionary<string, (int year, int month)> _lastCrewCapacityAdditionPass =
            new Dictionary<string, (int year, int month)>();

        static readonly Dictionary<string, (int year, int month)> _lastReturnSweepPass =
            new Dictionary<string, (int year, int month)>();

        static readonly Dictionary<string, (int year, int month)> _lastCrewShipmentPass =
            new Dictionary<string, (int year, int month)>();

        static readonly Dictionary<string, (int year, int month)> _lastContractSnapshotPass =
            new Dictionary<string, (int year, int month)>();

        static readonly Dictionary<string, (int year, int month)> _lastFleetReservePass =
            new Dictionary<string, (int year, int month)>();

        internal static CompanyBehaviour GetCompanyBehaviour(BDTask task)
        {
            if (task == null)
                return null;
            if (task.Owner is CompanyBehaviour cb)
                return cb;
            if (task.Owner is CompanyChildBehavior child)
                return child.Parent;
            return null;
        }

        internal static bool HasPresence(CompanyBehaviour cb, ObjectInfoData data)
        {
            if (cb == null || data == null)
                return false;

            if (ReferenceEquals(data.ObjectInfo, cb.Company.mainObjectInfo))
                return true;

            return data.CurrentCrew > 0
                || (data.ListFacility != null && data.ListFacility.Count > 0)
                || (data.ListSpaceCrafts != null && data.ListSpaceCrafts.Count > 0)
                || (data.ProductionItem != null && data.ProductionItem.Count > 0);
        }

        internal static int InfrastructureRank(ObjectInfoData data)
        {
            if (data == null)
                return int.MaxValue;

            int rank = 0;
            if (data.CumulativeConstructionPower <= 0f)
                rank += 2;
            if (data.CumulativeVehicleAssemblyPower <= 0f)
                rank += 1;
            return rank;
        }

        internal static double SolarDistance(ObjectInfo where, ObjectInfoData data)
        {
            if (where == null || data?.ObjectInfo == null)
                return double.PositiveInfinity;
            return Math.Abs(data.ObjectInfo.DistanceToSunInAU - where.DistanceToSunInAU);
        }

        internal static List<ObjectiveNeed> CollectActiveNeeds(CompanyBehaviour cb)
        {
            var result = new List<ObjectiveNeed>();
            if (cb?.Company == null)
                return result;

            var contractManager = MonoBehaviourSingleton<ContractManager>.Instance;
            if (contractManager == null || contractManager.ContractsInstances == null)
                return result;

            foreach (var contract in contractManager.ContractsInstances.Values)
            {
                if (contract == null)
                    continue;
                if (contract.ContractStateForCompany(cb.Company) != ContractManager.EContractState.Active)
                    continue;

                CompanyContractData perCompany;
                if (!contract.PerCompanyContractData.TryGetValue(cb.Company, out perCompany) || perCompany == null)
                    continue;

                var objective = contract.Objectives.FirstOrDefault(o => !perCompany.IsObjectiveComplete(o));
                if (objective == null)
                    continue;

                var target = MonoBehaviourSingleton<ObjectInfoManager>.Instance.GetByID(objective.toID);
                var product = objective.productItem;
                var resource = product as ResourceDefinition;

                if (resource == null && objective.resourceTypeType == EResourceTypeType.crew)
                {
                    resource = null;
                }

                result.Add(new ObjectiveNeed
                {
                    Target = target,
                    Product = product,
                    Resource = resource,
                    Type = objective.objectiveType
                });
            }

            return result;
        }

        internal static bool PendingContractTouchesBody(CompanyBehaviour cb, ObjectInfo objectInfo)
        {
            if (cb?.Company == null || objectInfo == null)
                return false;

            var contractManager = MonoBehaviourSingleton<ContractManager>.Instance;
            if (contractManager?.ContractsInstances == null)
                return false;

            foreach (var contract in contractManager.ContractsInstances.Values)
            {
                if (contract == null)
                    continue;
                if (contract.ContractStateForCompany(cb.Company) != ContractManager.EContractState.Active)
                    continue;
                if (!contract.PerCompanyContractData.TryGetValue(cb.Company, out var perCompany) || perCompany == null)
                    continue;

                foreach (var objectiveData in perCompany.ObjectivesDataList)
                {
                    if (objectiveData?.Objective == null || objectiveData.IsComplete)
                        continue;

                    var objective = objectiveData.Objective;
                    var from = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.GetByID(objective.fromID);
                    var to = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.GetByID(objective.toID);
                    if (ReferenceEquals(from, objectInfo) || ReferenceEquals(to, objectInfo))
                        return true;
                }
            }

            return false;
        }

        internal static string DescribeObjectiveProgress(CompanyObjectiveData objectiveData)
        {
            if (objectiveData?.Objective == null)
                return "unknown objective";

            var objective = objectiveData.Objective;
            var from = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.GetByID(objective.fromID)?.ObjectName ?? "?";
            var to = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.GetByID(objective.toID)?.ObjectName ?? "?";
            var product1 = objective.productItem?.ID ?? "none";
            var product2 = objective.productItem2?.ID ?? "none";
            var targetAmount = objective.howMuch;
            var currentAmount = objectiveData.howMuchCurrent;
            var status = objectiveData.IsComplete ? "complete" : "pending";

            return $"{status} {objective.objectiveType} from={from} to={to} item={product1} item2={product2} howMuch={targetAmount:0.##} progress={currentAmount:0.##}";
        }

        internal static ObjectInfo GetObjectiveTargetBody(Objective objective)
        {
            if (objective == null)
                return null;

            int bodyId = objective.objectiveType == EObjectiveType.BuildFacility
                ? objective.fromID
                : objective.toID;

            return MonoBehaviourSingleton<ObjectInfoManager>.Instance?.GetByID(bodyId);
        }

        internal static List<ManifestResourceDemand> BuildDestinationManifest(CompanyBehaviour cb, ObjectInfo destination)
        {
            var manifest = new Dictionary<ResourceDefinition, ManifestResourceDemand>();
            if (cb?.Company == null || destination == null)
                return manifest.Values.ToList();

            foreach (var resource in SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllResourceDefinitions.ListNotEmpty.Where(r => r != null))
            {
                double stockDemand = cb.GetResourceDemandOnObject(destination, resource);
                if (stockDemand > 0.0)
                {
                    manifest[resource] = new ManifestResourceDemand
                    {
                        Resource = resource,
                        StockDemand = stockDemand,
                        TotalDemand = stockDemand
                    };
                }
            }

            var contractManager = MonoBehaviourSingleton<ContractManager>.Instance;
            var destinationData = destination.GetObjectInfoData(cb.Company);
            if (contractManager?.ContractsInstances == null || destinationData == null)
                return manifest.Values.OrderByDescending(x => x.TotalDemand).ToList();

            foreach (var contract in contractManager.ContractsInstances.Values)
            {
                if (contract == null)
                    continue;
                if (contract.ContractStateForCompany(cb.Company) != ContractManager.EContractState.Active)
                    continue;
                if (!contract.PerCompanyContractData.TryGetValue(cb.Company, out var perCompany) || perCompany == null)
                    continue;

                foreach (var objectiveData in perCompany.ObjectivesDataList)
                {
                    var objective = objectiveData?.Objective;
                    if (objective == null || objectiveData.IsComplete)
                        continue;
                    if (objective.objectiveType != EObjectiveType.BuildFacility)
                        continue;

                    var targetBody = GetObjectiveTargetBody(objective);
                    if (!ReferenceEquals(targetBody, destination))
                        continue;

                    var facility = objective.productItem as FacilityBaseDescriptor;
                    if (facility?.Price?.ListResources == null)
                        continue;

                    if (HasFacilityOrProduction(destinationData, facility))
                        continue;

                    foreach (var item in facility.Price.ListResources.Where(x => x?.ResourceDefinition != null && x.Price > 0))
                    {
                        if (!manifest.TryGetValue(item.ResourceDefinition, out var entry))
                        {
                            entry = new ManifestResourceDemand
                            {
                                Resource = item.ResourceDefinition
                            };
                            manifest[item.ResourceDefinition] = entry;
                        }

                        entry.TotalDemand += item.Price;
                    }
                }
            }

            return manifest.Values
                .Where(x => x.Resource != null && x.TotalDemand > 0.0)
                .OrderByDescending(x => x.TotalDemand)
                .ToList();
        }

        internal static List<SpaceModuleDescriptor> GetPendingModuleDeliveries(CompanyBehaviour cb, ObjectInfo destination)
        {
            var result = new List<SpaceModuleDescriptor>();
            if (cb?.Company == null || destination == null)
                return result;

            var contractManager = MonoBehaviourSingleton<ContractManager>.Instance;
            if (contractManager?.ContractsInstances == null)
                return result;

            foreach (var contract in contractManager.ContractsInstances.Values)
            {
                if (contract == null)
                    continue;
                if (contract.ContractStateForCompany(cb.Company) != ContractManager.EContractState.Active)
                    continue;
                if (!contract.PerCompanyContractData.TryGetValue(cb.Company, out var perCompany) || perCompany == null)
                    continue;

                foreach (var objectiveData in perCompany.ObjectivesDataList)
                {
                    var objective = objectiveData?.Objective;
                    if (objective == null || objectiveData.IsComplete)
                        continue;
                    if (objective.objectiveType != EObjectiveType.Deliver)
                        continue;

                    var targetBody = GetObjectiveTargetBody(objective);
                    if (!ReferenceEquals(targetBody, destination))
                        continue;

                    if (objective.productItem is SpaceModuleDescriptor module)
                        result.Add(module);
                }
            }

            return result;
        }

        internal static void LogActiveContracts(CompanyBehaviour cb)
        {
            if (cb?.Company == null || cb.Company.IsPlayer || cb.Company.Definition.IsWorldGovernment)
                return;

            var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? default;
            if (now == default)
                return;

            if (_lastContractSnapshotPass.TryGetValue(cb.Company.ID, out var last) &&
                last.year == now.Year &&
                last.month == now.Month)
                return;

            _lastContractSnapshotPass[cb.Company.ID] = (now.Year, now.Month);

            var contractManager = MonoBehaviourSingleton<ContractManager>.Instance;
            if (contractManager?.ContractsInstances == null)
                return;

            var activeContracts = contractManager.ContractsInstances.Values
                .Where(contract => contract != null &&
                    contract.ContractStateForCompany(cb.Company) == ContractManager.EContractState.Active &&
                    contract.PerCompanyContractData.TryGetValue(cb.Company, out var perCompany) &&
                    perCompany != null)
                .ToList();

            if (activeContracts.Count == 0)
            {
                Plugin.Log.LogInfo($"[AIOverhaul] Contract snapshot for {cb.Company.ID}: no active contracts.");
                return;
            }

            Plugin.Log.LogInfo($"[AIOverhaul] Contract snapshot for {cb.Company.ID}: {activeContracts.Count} active contract(s).");
            foreach (var contract in activeContracts)
            {
                if (!contract.PerCompanyContractData.TryGetValue(cb.Company, out var perCompany) || perCompany == null)
                    continue;

                Plugin.Log.LogInfo($"[AIOverhaul]   Contract '{contract.ContractDefinition.ID}' state={perCompany.currentState} objectives={perCompany.ObjectivesDataList.Count}.");
                for (int i = 0; i < perCompany.ObjectivesDataList.Count; i++)
                {
                    var objectiveData = perCompany.ObjectivesDataList[i];
                    if (objectiveData == null)
                        continue;

                    Plugin.Log.LogInfo($"[AIOverhaul]     [{i}] {DescribeObjectiveProgress(objectiveData)}");
                }
            }
        }

        internal static IEnumerable<object> EnumerateUnlocks(ResearchDefinition rd)
        {
            if (rd == null)
                yield break;

            var first = _rdUnlockDataFi?.GetValue(rd);
            if (first != null)
                yield return first;

            var list = _rdUnlockDataListFi?.GetValue(rd) as System.Collections.IEnumerable;
            if (list == null)
                yield break;

            foreach (var item in list)
            {
                if (item != null)
                    yield return item;
            }
        }

        internal static List<UnlockInfo> GetUnlockInfos(ResearchDefinition rd)
        {
            var result = new List<UnlockInfo>();
            foreach (var unlock in EnumerateUnlocks(rd))
            {
                string action = _udActionFi?.GetValue(unlock)?.ToString();
                string id = _udParameter1Fi?.GetValue(unlock) as string;
                result.Add(new UnlockInfo { Action = action, Id = id });
            }
            return result;
        }

        internal static bool ResearchUnlocksId(ResearchDefinition rd, string id, string action = null)
        {
            if (rd == null || string.IsNullOrEmpty(id))
                return false;

            foreach (var unlock in GetUnlockInfos(rd))
            {
                if (!string.Equals(unlock.Id, id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (action != null && !string.Equals(unlock.Action, action, StringComparison.OrdinalIgnoreCase))
                    continue;
                return true;
            }
            return false;
        }

        internal static double ScoreResearch(CompanyBehaviour cb, ResearchDefinition rd)
        {
            if (cb == null || rd == null)
                return double.NegativeInfinity;

            var unlocks = GetUnlockInfos(rd);
            if (unlocks.Count == 0)
                return double.NegativeInfinity;

            double score = 0.0;
            var activeNeeds = CollectActiveNeeds(cb);

            bool anyBodyNeedsConstruction = false;
            bool anyBodyNeedsAssembly = false;
            bool anyBodyNeedsMining = false;
            bool anyBodyNeedsRefining = false;

            foreach (var need in activeNeeds)
            {
                var targetData = need.Target != null ? need.Target.GetObjectInfoData(cb.Company) : null;
                if (targetData != null)
                {
                    if (targetData.CumulativeConstructionPower <= 0f)
                        anyBodyNeedsConstruction = true;
                    if (targetData.CumulativeVehicleAssemblyPower <= 0f)
                        anyBodyNeedsAssembly = true;
                }

                if (need.Product != null)
                {
                    foreach (var unlock in unlocks)
                    {
                        if (string.Equals(unlock.Id, need.Product.ID, StringComparison.OrdinalIgnoreCase))
                            score += 1200.0;
                    }
                }

                if (need.Resource != null)
                {
                    var mining = need.Resource.GetMiningGroundFacility();
                    var refining = need.Resource.GetRefineryGroundFacility();
                    if (mining != null && (targetData == null || !targetData.ListFacility.Any(f => f.facilityDescriptor == mining)))
                        anyBodyNeedsMining = true;
                    if (refining != null && (targetData == null || !targetData.ListFacility.Any(f => f.facilityDescriptor == refining)))
                        anyBodyNeedsRefining = true;
                }
            }

            foreach (var unlock in unlocks)
            {
                if (string.Equals(unlock.Action, "UnlockFacility", StringComparison.OrdinalIgnoreCase))
                {
                    var facility = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllFacility?.GetByID(unlock.Id);
                    if (facility != null)
                    {
                        score += 100.0;

                        if (anyBodyNeedsConstruction &&
                            facility.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.ConstructionEquipment))
                            score += 800.0;

                        if (anyBodyNeedsAssembly &&
                            facility.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.BuildSpacecraft))
                            score += 700.0;

                        if (anyBodyNeedsMining &&
                            facility.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.Mining))
                            score += 650.0;

                        if (anyBodyNeedsRefining &&
                            facility.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.Refiner))
                            score += 650.0;

                        if (facility.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewTransport))
                            score += 120.0;
                    }
                }
                else if (string.Equals(unlock.Action, "UnlockSpacecraftType", StringComparison.OrdinalIgnoreCase))
                {
                    score += 300.0;
                }
                else if (string.Equals(unlock.Action, "UnlockVehicleType", StringComparison.OrdinalIgnoreCase))
                {
                    score += 260.0;
                }
            }

            double cost = cb.Company.ResearchManager.GetResearchCost(rd);
            score -= cost * 0.01;
            return score;
        }

        internal static ResearchDefinition ChooseStrategicResearch(CompanyBehaviour cb)
        {
            if (cb?.Company?.ResearchManager == null)
                return null;

            var candidates = cb.Company.ResearchManager.GetCandidateResearch()
                .Where(rd => rd != null && !rd.isLockedForUI)
                .ToList();

            if (candidates.Count == 0)
                return null;

            return candidates
                .Select(rd => new { Research = rd, Score = ScoreResearch(cb, rd) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => cb.Company.ResearchManager.GetResearchCost(x.Research))
                .Select(x => x.Research)
                .FirstOrDefault();
        }

        internal static SharedMyIDScriptableObject GetSharedItemVariable(BDTask task, string name)
        {
            return task?.Owner?.GetVariable(name) as SharedMyIDScriptableObject;
        }

        internal static FacilityBaseDescriptor ResolveBootstrapFacility(ObjectInfoData data, MyIDScriptableObject item, out string reason)
        {
            reason = null;
            if (data == null || item == null)
                return null;

            var cb = data.company?.GetComponent<CompanyBehaviour>();
            if (cb == null)
                return null;

            if (item is SpacecraftType || item is LaunchVehicleType || item is SpaceModuleDescriptor)
            {
                if (data.CumulativeVehicleAssemblyPower <= 0f)
                {
                    reason = "vehicle assembly";
                    return cb.FindSuitableVehicleAssembly(data);
                }
            }

            var facility = item as FacilityBaseDescriptor;
            if (facility != null)
            {
                if (facility.ConstructionEquipmentCountIsRequired && data.CumulativeConstructionPower <= 0f)
                {
                    reason = "construction equipment";
                    return cb.FindSuitableConstructionEquipment(data);
                }
            }

            var resource = item as ResourceDefinition;
            if (resource != null)
            {
                var mining = resource.GetMiningGroundFacility();
                if (mining != null && data.ObjectInfo.CanMineResources &&
                    !data.ListFacility.Any(f => f.facilityDescriptor == mining))
                {
                    reason = "mining";
                    return mining;
                }

                var refining = resource.GetRefineryGroundFacility();
                if (refining != null &&
                    !data.ListFacility.Any(f => f.facilityDescriptor == refining))
                {
                    reason = "refining";
                    return refining;
                }
            }

            return null;
        }

        internal static bool HasFacilityOrProduction(ObjectInfoData data, FacilityBaseDescriptor descriptor)
        {
            if (data == null || descriptor == null)
                return false;

            if (data.ListFacility != null && data.ListFacility.Any(f => f.facilityDescriptor == descriptor))
                return true;

            if (data.ProductionItem != null && data.ProductionItem.Any(p => p.ProductionItemType == descriptor))
                return true;

            return false;
        }

        internal static BDTaskStatus HandleUnlockProductionMeans(UnlockProductionMeans action)
        {
            var cb = GetCompanyBehaviour(action);
            if (cb?.Company == null)
                return BDTaskStatus.Failure;

            var whereVar = _unlockProductionWhereFi?.GetValue(action) as SharedObjectInfo;
            var target = whereVar?.Value?.Object ?? cb.Company.mainObjectInfo;
            if (target == null)
                return BDTaskStatus.Failure;

            var targetData = target.GetObjectInfoData(cb.Company);
            if (targetData == null)
                return BDTaskStatus.Failure;

            var itemVar = GetSharedItemVariable(action, "what");
            var item = itemVar?.Value;
            if (item == null)
                return BDTaskStatus.Success;

            string reason;
            var needed = ResolveBootstrapFacility(targetData, item, out reason);
            if (needed == null)
                return BDTaskStatus.Success;

            if (HasFacilityOrProduction(targetData, needed))
                return BDTaskStatus.Running;

            if (!cb.Company.IsUnlockFacility(needed))
            {
                var research = ResearchManager.FindResearchForProductionItem(needed);
                if (research != null)
                {
                    cb.QueueResearch(research, force: true);
                    if (cb.Verbose)
                        cb.MyLog($"[AIOverhaul] Queued research '{research.ID}' to unlock {reason} on {target.ObjectName}.");
                    return BDTaskStatus.Running;
                }
                return BDTaskStatus.Failure;
            }

            if (!targetData.CanAddFacility(needed))
                return BDTaskStatus.Failure;

            targetData.AddFacility(needed, prebuilt: false);
            Plugin.Log.LogInfo($"[AIOverhaul] Queued {needed.ID} on {target.ObjectName} for {cb.Company.ID} ({reason}).");
            return BDTaskStatus.Running;
        }

        internal static bool IsCloseEnoughToReturnHome(ObjectInfo current, ObjectInfo home)
        {
            if (current == null || home == null)
                return false;

            if (current.IsEqualOrIsOrbitOf(home) || home.IsEqualOrIsOrbitOf(current))
                return true;

            if (ReferenceEquals(current.ParentObjectInfo, home))
                return true;

            if (ReferenceEquals(current.ParentObjectInfo?.ParentObjectInfo, home))
                return true;

            return false;
        }

        internal static bool ShouldAutoReturnHome(Spacecraft sc)
        {
            if (sc?.spacecraftType == null)
                return false;

            var company = sc.GetCompany();
            if (company == null || company.IsPlayer)
                return false;

            var cb = company.GetComponent<CompanyBehaviour>();
            if (cb == null)
                return false;

            if (sc.CurrentPhase != Spacecraft.EPhase.None)
                return false;

            if (cb.SpacecraftsLockedForAIMission.Contains(sc))
                return false;

            if (sc.spacecraftType.LowOrbitContainer || sc.spacecraftType.cheatSC)
                return false;

            if (sc.spacecraftType.GetReusability(company) <= 0f)
                return false;

            if (sc.CycleMissionsData != null)
                return false;

            var current = sc.CurrentlyOnThisObject;
            var home = company.mainObjectInfo;
            if (current == null || home == null)
                return false;

            if (current.IsEqualOrIsOrbitOf(home))
                return false;

            return IsCloseEnoughToReturnHome(current, home);
        }

        internal static bool TryScheduleReturnHome(Spacecraft sc)
        {
            if (!ShouldAutoReturnHome(sc))
                return false;

            var company = sc.GetCompany();
            var from = sc.CurrentlyOnThisObject;
            var home = company.mainObjectInfo;

            try
            {
                var cargoAll = CargoAll.CreateCargoEmpty();
                cargoAll.cargoFuel.objectInfo = from;
                cargoAll.cargoFuel.cargoMass = 0.0;
                cargoAll.cargoFuel.resourceType = sc.spacecraftType.GetFuelType();

                var pmp = new PMMissionParameter();
                pmp.ChangeMissionName($"{sc.GetSpacecraftName()} {sc.ID}\n[AI Return: {company.ID}]")
                    .SetCompany(company)
                    .SetTabSC(new List<ISpacecraftInfo> { sc }, 1)
                    .SetTabCargo(cargoAll)
                    .SetDontCheckRemoveFuel(dontCheck: true)
                    .SetTabDestination(from, home)
                    .SetTabLV(new List<ILaunchVehicleInfo>(), 0);
                pmp.Fast = true;
                pmp.RandomBestOption = false;
                pmp.TryFastAsPossible = true;

                var result = MonoBehaviourSingleton<GameManager>.Instance.PlanFlyCode(pmp);
                bool scheduled = result != null &&
                    result.endCalculation &&
                    result.missionInfo != null &&
                    !result.missionInfo.cancel;

                if (!scheduled && !sc.spacecraftType.SolarSC)
                {
                    var sourceData = from.GetObjectInfoData(company);
                    var fuelType = sc.spacecraftType.GetFuelType();
                    double seedFuelAmount = Math.Max(sc.spacecraftType.GetFuelCapacity(company), 1f);
                    string stagedFuel = StageResourceToObject(sourceData, fuelType, seedFuelAmount, allowReserveFallback: true);
                    if (!string.IsNullOrEmpty(stagedFuel))
                    {
                        Plugin.Log.LogInfo($"[AIOverhaul] Seeded return fuel for {company.ID} ship '{sc.GetSpacecraftName()}' on {from.ObjectName}: {stagedFuel}.");
                        result = MonoBehaviourSingleton<GameManager>.Instance.PlanFlyCode(pmp);
                        scheduled = result != null &&
                            result.endCalculation &&
                            result.missionInfo != null &&
                            !result.missionInfo.cancel;
                    }
                }

                if (scheduled)
                {
                    Plugin.Log.LogInfo($"[AIOverhaul] Scheduled return flight for {company.ID} ship '{sc.GetSpacecraftName()}' from {from.ObjectName} to {home.ObjectName}.");
                }

                return scheduled;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[AIOverhaul] Failed to schedule return flight for {company?.ID} ship '{sc?.GetSpacecraftName()}': {ex.Message}");
                return false;
            }
        }

        internal static string GetBodyScopedKey(Company company, ObjectInfo objectInfo)
        {
            return $"{company?.ID ?? "?"}:{objectInfo?.ObjectName ?? "?"}";
        }

        internal static bool WasHandledThisMonth(Dictionary<string, (int year, int month)> state, string key, int year, int month)
        {
            if (string.IsNullOrEmpty(key))
                return false;

            return state.TryGetValue(key, out var last) &&
                last.year == year &&
                last.month == month;
        }

        internal static void MarkHandledThisMonth(Dictionary<string, (int year, int month)> state, string key, int year, int month)
        {
            if (!string.IsNullOrEmpty(key))
                state[key] = (year, month);
        }

        internal static bool IsCrewCapacityFacility(Facility facility)
        {
            return facility?.facilityDescriptor != null &&
                facility.facilityDescriptor.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewCapacity);
        }

        internal static FacilityBaseDescriptor FindSuitableCrewCapacity(ObjectInfoData objectInfoData, long crewGap)
        {
            if (objectInfoData == null)
                return null;

            var company = objectInfoData.company;
            var candidates = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                .AllFacility.ListNotEmpty
                .Where(descriptor =>
                    descriptor != null &&
                    descriptor.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewCapacity) &&
                    company != null &&
                    company.IsUnlockFacility(descriptor) &&
                    objectInfoData.CanAddFacility(descriptor))
                .OrderBy(descriptor => descriptor.specialAbilityParameter)
                .ThenBy(descriptor => descriptor.NeedWorkersToWork(objectInfoData.company))
                .ToList();

            if (!candidates.Any())
                return null;

            return candidates.First();
        }

        internal static SpaceModuleDescriptor ChooseCrewTransportModule(Company company, long crewCount)
        {
            if (company == null)
                return null;

            var modules = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                .AllFacility.ListNotEmpty
                .OfType<SpaceModuleDescriptor>()
                .Where(descriptor =>
                    descriptor != null &&
                    company.IsUnlockFacility(descriptor) &&
                    descriptor.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewTransport))
                .ToList();

            if (modules.Count == 0)
                return null;

            return modules
                .Where(m => m.specialAbilityParameter >= crewCount)
                .OrderBy(m => m.specialAbilityParameter)
                .FirstOrDefault()
                ?? modules.OrderByDescending(m => m.specialAbilityParameter).FirstOrDefault();
        }

        internal static bool TryScheduleCrewShipment(CompanyBehaviour cb, ObjectInfoData sourceData, ObjectInfoData targetData, long crewNeeded)
        {
            if (cb?.Company == null || sourceData?.ObjectInfo == null || targetData?.ObjectInfo == null || crewNeeded <= 0)
                return false;

            long minimumReserve = Math.Max(100L, cb.Company.Definition.PopulationConfig.minPopulationSize);
            long preferredReserve = Math.Max(minimumReserve, HomeCrewReserveTarget);
            long sourceAvailableCrew = Math.Max(0L, sourceData.CurrentCrew - preferredReserve);
            if (sourceAvailableCrew <= 0)
            {
                // Let early off-world colonies seed themselves before the home world reaches the full 500-person reserve.
                sourceAvailableCrew = Math.Max(0L, sourceData.CurrentCrew - minimumReserve);
            }
            if (sourceAvailableCrew <= 0)
                return false;

            var module = ChooseCrewTransportModule(cb.Company, crewNeeded);
            if (module == null)
                return false;

            long crewToSend = Math.Min(sourceAvailableCrew, Math.Max(1L, Math.Min(crewNeeded, (long)Math.Max(1f, module.specialAbilityParameter))));
            if (crewToSend <= 0)
                return false;

            SharedFloat howMuch = (float)crewToSend;
            SharedMyIDScriptableObject spacecraftType = new SharedMyIDScriptableObject();
            SharedSpacecraft spacecraft = new SharedSpacecraft();
            SharedFloat minFuelCapacity = 0f;
            if (!TryFindStrategicSpacecraftForFlight(cb, sourceData.ObjectInfo, targetData.ObjectInfo, module, howMuch, spacecraftType, spacecraft, minFuelCapacity, dontReuse: false) ||
                spacecraft.Value?.Object == null)
                return false;

            SharedMyIDScriptableObject launchVehicleType = new SharedMyIDScriptableObject();
            SharedLaunchVehicle launchVehicle = new SharedLaunchVehicle();
            SharedBool launchVehicleNotNeeded = false;
            if (!TryFindStrategicLaunchVehicleForFlight(cb, sourceData.ObjectInfo, module, howMuch, spacecraftType.Value, launchVehicleType, launchVehicle, launchVehicleNotNeeded, minFuelCapacity, forceLV: false, dontReuse: false))
                return false;

            CargoAll cargoAll = CargoAll.CreateCargoEmpty();
            cargoAll.cargoFuel.objectInfo = sourceData.ObjectInfo;
            cargoAll.cargoFuel.cargoMass = 0.0;
            cargoAll.cargoFuel.resourceType = spacecraft.Value.Object.spacecraftType.GetFuelType();
            cargoAll.listCargo.Add(new Cargo
            {
                resourceTypeType = EResourceTypeType.modules,
                moduleData = module,
                SourceModule = null,
                crew = true,
                crewValue = (int)howMuch.Value,
                objectInfo = sourceData.ObjectInfo
            });

            PMMissionParameter pmp = new PMMissionParameter();
            pmp.ChangeMissionName($"{spacecraft.Value.Object.GetSpacecraftName()} {spacecraft.Value.ID}\n[AI Crew: {cb.Company.ID}]")
                .SetCompany(cb.Company)
                .SetTabSC(new List<ISpacecraftInfo> { spacecraft.Value.Object }, 1)
                .SetTabCargo(cargoAll)
                .SetDontCheckRemoveFuel(dontCheck: true)
                .SetTabDestination(sourceData.ObjectInfo, targetData.ObjectInfo);
            if (launchVehicle.Value?.Object != null)
                pmp.SetTabLV(new List<ILaunchVehicleInfo> { launchVehicle.Value.Object }, 1);
            else
                pmp.SetTabLV(new List<ILaunchVehicleInfo>(), 0);
            pmp.Fast = true;
            pmp.RandomBestOption = false;
            pmp.TryFastAsPossible = true;

            var result = MonoBehaviourSingleton<GameManager>.Instance.PlanFlyCode(pmp);
            if (result?.missionInfo == null || result.missionInfo.cancel)
                return false;

            if (spacecraft.Value?.Object != null)
                cb.SpacecraftsLockedForAIMission.Add(spacecraft.Value);
            if (launchVehicle.Value?.Object != null)
                cb.LaunchVehiclesLockedForAIMission.Add(launchVehicle.Value);

            Plugin.Log.LogInfo($"[AIOverhaul] Scheduled crew shipment of {crewToSend} from {sourceData.ObjectInfo.ObjectName} to {targetData.ObjectInfo.ObjectName} for {cb.Company.ID} using {spacecraft.Value.Object.spacecraftType.ID}.");
            return true;
        }

        internal static long GetCriticalWorkersNeeded(ObjectInfoData data)
        {
            if (data?.ListFacility == null)
                return 0L;

            long critical = 0L;
            foreach (var facility in data.ListFacility)
            {
                if (facility == null || facility.BuildProgress < 1f || facility.Enabled <= 0)
                    continue;

                var ability = facility.facilityDescriptor?.specialAbilityFacilityNew ?? 0;
                if (ability.HasFlag(ESpecialAbilityFacilityNew.Lab) ||
                    ability.HasFlag(ESpecialAbilityFacilityNew.BuildSpacecraft) ||
                    ability.HasFlag(ESpecialAbilityFacilityNew.ConstructionEquipment) ||
                    ability.HasFlag(ESpecialAbilityFacilityNew.Mining) ||
                    ability.HasFlag(ESpecialAbilityFacilityNew.Refiner))
                {
                    critical += facility.TotalWorkersNeeded;
                }
            }

            return critical;
        }

        internal static long GetNonHabitatWorkersNeeded(ObjectInfoData data)
        {
            if (data?.ListFacility == null)
                return 0L;

            return data.ListFacility
                .Where(f => f != null && f.BuildProgress >= 1f && f.Enabled > 0 && !IsCrewCapacityFacility(f))
                .Sum(f => f.TotalWorkersNeeded);
        }

        internal static bool EnsureReusableShipsReturnHome(CompanyBehaviour cb)
        {
            if (cb?.Company == null || cb.Company.IsPlayer)
                return false;

            var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? default;
            if (now == default)
                return false;

            string companyKey = cb.Company.ID;
            if (WasHandledThisMonth(_lastReturnSweepPass, companyKey, now.Year, now.Month))
                return false;

            MarkHandledThisMonth(_lastReturnSweepPass, companyKey, now.Year, now.Month);

            bool changed = false;
            var allObjects = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.allObjectInfos;
            if (allObjects == null)
                return false;

            foreach (var objectInfo in allObjects)
            {
                var data = objectInfo?.GetObjectInfoData(cb.Company);
                if (data?.ListSpaceCrafts == null)
                    continue;

                foreach (var spacecraft in data.ListSpaceCrafts.Distinct().ToList())
                {
                    if (spacecraft == null)
                        continue;

                    if (!ShouldAutoReturnHome(spacecraft))
                        continue;

                    if (TryScheduleReturnHome(spacecraft))
                    {
                        Plugin.Log.LogInfo($"[AIOverhaul] Return sweep recalled '{spacecraft.GetSpacecraftName()}' for {cb.Company.ID} from {spacecraft.CurrentlyOnThisObject?.ObjectName ?? "?"}.");
                        changed = true;
                    }
                }
            }

            return changed;
        }

        internal static bool EnsureFleetReserve(CompanyBehaviour cb)
        {
            if (cb?.Company == null || cb.Company.IsPlayer || cb.Company.Definition.IsWorldGovernment)
                return false;

            var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? default;
            if (now == default)
                return false;

            string companyKey = cb.Company.ID;
            if (WasHandledThisMonth(_lastFleetReservePass, companyKey, now.Year, now.Month))
                return false;

            MarkHandledThisMonth(_lastFleetReservePass, companyKey, now.Year, now.Month);

            var home = cb.Company.mainObjectInfo?.GetObjectInfoData(cb.Company);
            if (home == null)
                return false;

            bool changed = false;

            var preferredSpacecraft = ChooseReserveSpacecraftType(cb, home);
            if (preferredSpacecraft != null)
            {
                int readySpacecraft = CountReserveSpacecraft(home, preferredSpacecraft);
                int spacecraftTarget = 3;
                if (readySpacecraft < spacecraftTarget)
                {
                    if (TryQueueReserveSpacecraft(home, preferredSpacecraft, spacecraftTarget - readySpacecraft))
                        changed = true;
                }
            }

            var preferredLaunchVehicle = ChooseReserveLaunchVehicleType(cb, home, preferredSpacecraft);
            if (preferredLaunchVehicle != null)
            {
                int readyLaunchVehicles = CountReserveLaunchVehicles(home, preferredLaunchVehicle);
                int launchVehicleTarget = 3;
                if (readyLaunchVehicles < launchVehicleTarget)
                {
                    if (TryQueueReserveLaunchVehicle(home, preferredLaunchVehicle, launchVehicleTarget - readyLaunchVehicles))
                        changed = true;
                }
            }

            return changed;
        }

        static SpacecraftType ChooseReserveSpacecraftType(CompanyBehaviour cb, ObjectInfoData home)
        {
            if (cb?.Company == null || home == null)
                return null;

            return SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllSpacecraftType.ListNotEmpty
                .Where(type =>
                    type != null &&
                    cb.Company.IsUnlockSpacecraftType(type) &&
                    !type.cheatSC &&
                    !type.LowOrbitContainer &&
                    !type.DestroyOnLand &&
                    type.CargoCapacity >= 20f &&
                    (type.CanBuildParameter == null || type.CanBuildParameter.CanBuild(home)))
                .OrderByDescending(type => ScoreReserveSpacecraftType(cb.Company, type))
                .FirstOrDefault();
        }

        static double ScoreReserveSpacecraftType(Company company, SpacecraftType type)
        {
            double score = type.CargoCapacity;
            score += type.GetFuelCapacity(company) * 0.1;
            score += type.Acceleration * 20.0;

            string id = type.ID ?? string.Empty;
            if (id.IndexOf("fusion", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 10000.0;
            else if (id.IndexOf("nuke", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 5000.0;
            else if (id.IndexOf("chem", StringComparison.OrdinalIgnoreCase) >= 0)
                score -= 500.0;

            return score;
        }

        static LaunchVehicleType ChooseReserveLaunchVehicleType(CompanyBehaviour cb, ObjectInfoData home, SpacecraftType preferredSpacecraft)
        {
            if (cb?.Company == null || home == null || preferredSpacecraft == null)
                return null;

            double requiredPayload = preferredSpacecraft.Mass + Math.Max(20.0, preferredSpacecraft.CargoCapacity);

            return SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllLaunchVehicleType.ListNotEmpty
                .Where(type =>
                    type != null &&
                    cb.Company.IsUnlockRocketType(type) &&
                    type.CanBuildOnThisPlanet(home.ObjectInfo, cb.Company) &&
                    (type.CanBuildParameter == null || type.CanBuildParameter.CanBuild(home)) &&
                    type.MaxPayloadOnThisObject(home.ObjectInfo, cb.Company) >= requiredPayload)
                .OrderByDescending(type => ScoreReserveLaunchVehicleType(type, home.ObjectInfo, cb.Company))
                .FirstOrDefault();
        }

        static double ScoreReserveLaunchVehicleType(LaunchVehicleType type, ObjectInfo onObject, Company company)
        {
            double score = type.MaxPayloadOnThisObject(onObject, company);
            score += type.reusability * 2000.0;

            string id = type.ID ?? string.Empty;
            if (id.IndexOf("massdriver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("magrail", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 10000.0;
            else if (id.IndexOf("fusion", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 7000.0;
            else if (id.IndexOf("chem", StringComparison.OrdinalIgnoreCase) >= 0)
                score -= 500.0;

            return score;
        }

        static int CountReserveSpacecraft(ObjectInfoData home, SpacecraftType type)
        {
            int actual = home.ListSpaceCrafts?
                .Count(sc =>
                    sc != null &&
                    sc.spacecraftType == type &&
                    sc.CurrentPhase == Spacecraft.EPhase.None &&
                    !sc.IsPlanMission()) ?? 0;

            int queued = home.ProductionItem?
                .OfType<SpacecraftConstructData>()
                .Count(item => item != null && item.SpacecraftType == type) ?? 0;

            return actual + queued;
        }

        static int CountReserveLaunchVehicles(ObjectInfoData home, LaunchVehicleType type)
        {
            int actual = home.ObjectInfo?.ListLaunchVehicle?
                .Count(lv =>
                    lv != null &&
                    lv.company == home.company &&
                    lv.launchVehicleType == type &&
                    lv.spacecraft == null) ?? 0;

            int queued = home.ProductionItem?
                .OfType<SpacecraftConstructData>()
                .Count(item => item != null && item.LaunchVehicleType == type) ?? 0;

            return actual + queued;
        }

        static bool TryQueueReserveSpacecraft(ObjectInfoData home, SpacecraftType type, int deficit)
        {
            if (home?.company == null || type == null || deficit <= 0)
                return false;

            int toQueue = Math.Min(deficit, 1);
            bool changed = false;
            for (int i = 0; i < toQueue; i++)
            {
                var company = home.company;
                string baseName = type.NameRocketType ?? type.ID;
                string uniqueName = company.GetUniqueRocketName(baseName);
                var construct = new SpacecraftConstructData(baseName, uniqueName, type, home, company.productionCountID++);
                home.AddRocketToConstruct(construct);
                Plugin.Log.LogInfo($"[AIOverhaul] Queued reserve spacecraft '{type.ID}' on {home.ObjectInfo.ObjectName} for {company.ID}. Ready reserve below target.");
                changed = true;
            }

            return changed;
        }

        static bool TryQueueReserveLaunchVehicle(ObjectInfoData home, LaunchVehicleType type, int deficit)
        {
            if (home?.company == null || type == null || deficit <= 0)
                return false;

            int toQueue = Math.Min(deficit, 1);
            bool changed = false;
            for (int i = 0; i < toQueue; i++)
            {
                var company = home.company;
                string baseName = type.ID;
                string uniqueName = company.GetUniqueRocketName(baseName);
                var construct = new SpacecraftConstructData(baseName, uniqueName, type, home, company.productionCountID++);
                home.AddRocketToConstruct(construct);
                Plugin.Log.LogInfo($"[AIOverhaul] Queued reserve launch vehicle '{type.ID}' on {home.ObjectInfo.ObjectName} for {company.ID}. Ready reserve below target.");
                changed = true;
            }

            return changed;
        }

        internal static double ScoreSpacecraftTypeForFlight(CompanyBehaviour cb, SpacecraftType type, ObjectInfo to, double minCapacity, float minFuelCapacity)
        {
            if (cb?.Company == null || type == null || to == null)
                return double.NegativeInfinity;

            double score = 0.0;
            bool unlocked = cb.Company.IsUnlockSpacecraftType(type);
            score += unlocked ? 1000.0 : 100.0;
            score += type.GetReusability(cb.Company) * 350.0;
            score += type.AvailableDeltaV * 0.5;
            score += type.ConstanceAccelerationFlightMultiDelta * 120.0;
            score += type.GetFuelCapacity(cb.Company) * 0.02;
            score += type.GetCargoCapacity(cb.Company) * 0.01;
            score += type.engineType.ToString().IndexOf("fusion", StringComparison.OrdinalIgnoreCase) >= 0 ? 500.0 : 0.0;
            score += type.engineType.ToString().IndexOf("nuclear", StringComparison.OrdinalIgnoreCase) >= 0 ? 220.0 : 0.0;
            score -= Math.Max(0.0, type.GetCargoCapacity(cb.Company) - minCapacity) * 0.02;
            score -= Math.Max(0.0f, type.GetFuelCapacity(cb.Company) - minFuelCapacity) * 0.05;
            if (type.SolarSC)
            {
                score += type.GetSolarRange(cb.Company) >= to.DistanceToSunInAU ? 40.0 : -5000.0;
            }
            return score;
        }

        internal static double ScoreLaunchVehicleTypeForFlight(CompanyBehaviour cb, LaunchVehicleType type, ObjectInfo from, SpacecraftType scType, double minCapacity, float minFuelCapacity)
        {
            if (cb?.Company == null || type == null || from == null || scType == null)
                return double.NegativeInfinity;

            double requiredPayload = (double)scType.Mass + minCapacity + minFuelCapacity;
            double availablePayload = type.MaxPayloadOnThisObject(from, cb.Company);
            if (availablePayload < requiredPayload)
                return double.NegativeInfinity;

            double score = 0.0;
            bool unlocked = cb.Company.IsUnlockRocketType(type);
            score += unlocked ? 900.0 : 90.0;
            score += type.reusability * 300.0;
            score += type.maxFuelLoad * 0.01;
            score += availablePayload * 0.005;
            score += type.ID.IndexOf("fusion", StringComparison.OrdinalIgnoreCase) >= 0 ? 500.0 : 0.0;
            score += type.ID.IndexOf("massdriver", StringComparison.OrdinalIgnoreCase) >= 0 ? 380.0 : 0.0;
            score += type.ID.IndexOf("magrail", StringComparison.OrdinalIgnoreCase) >= 0 ? 360.0 : 0.0;
            score -= Math.Max(0.0, availablePayload - requiredPayload) * 0.002;
            return score;
        }

        internal static bool TryFindStrategicSpacecraftForFlight(
            CompanyBehaviour cb,
            ObjectInfo from,
            ObjectInfo to,
            MyIDScriptableObject cargoElement,
            SharedFloat howMuch,
            SharedMyIDScriptableObject spacecraftType,
            SharedSpacecraft spacecraft,
            SharedFloat minFuelCapacity,
            bool dontReuse)
        {
            if (cb?.Company == null || from == null || to == null || howMuch == null || spacecraftType == null || spacecraft == null || minFuelCapacity == null)
                return false;

            if (spacecraftType.Value == null && spacecraft.Value == null && (to.IsEqualOrIsOrbitOf(from) || from.IsEqualOrIsOrbitOf(to)))
            {
                spacecraft.Value = MonoBehaviourSingleton<ShipManager>.Instance.GetLowOrbitContainer(cb.Company);
                spacecraftType.Value = spacecraft.Value?.Object?.spacecraftType;
                return spacecraftType.Value != null;
            }

            float targetDistanceToSunAU = to.DistanceToSunInAU;
            var module = cargoElement as SpaceModuleDescriptor;
            bool crewTransportModule = module != null && module.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewTransport);
            double minCapacity = cargoElement == null ? 0.0 : (module != null ? module.Mass + (crewTransportModule ? howMuch.Value : 0f) : howMuch.Value);

            spacecraft.Value = null;
            if (spacecraftType.Value != null)
            {
                if (!dontReuse)
                {
                    spacecraft.Value = MonoBehaviourSingleton<ShipManager>.Instance.ListAllSpaceShip
                        .Where(sc =>
                            sc != null &&
                            Equals(sc.spacecraftType, spacecraftType.Value) &&
                            Equals(sc.GetCompany(), cb.Company) &&
                            !cb.SpacecraftsLockedForAIMission.Contains(sc) &&
                            ((sc.CurrentPhase == Spacecraft.EPhase.None && sc.CurrentlyOnThisObject.IsEqualOrIsOrbitOf(from)) ||
                             (sc.CurrentPhase == Spacecraft.EPhase.Landing && sc.MissionTarget.IsEqualOrIsOrbitOf(from))))
                        .OrderByDescending(sc => ScoreSpacecraftTypeForFlight(cb, sc.spacecraftType, to, minCapacity, minFuelCapacity.Value))
                        .FirstOrDefault();
                }
                return true;
            }

            if (!dontReuse)
            {
                spacecraft.Value = MonoBehaviourSingleton<ShipManager>.Instance.ListAllSpaceShip
                    .Where(sc =>
                        sc != null &&
                        Equals(sc.GetCompany(), cb.Company) &&
                        ((sc.CurrentPhase == Spacecraft.EPhase.None && sc.CurrentlyOnThisObject.IsEqualOrIsOrbitOf(from)) ||
                         (sc.CurrentPhase == Spacecraft.EPhase.Landing && sc.MissionTarget.IsEqualOrIsOrbitOf(from))) &&
                        (!sc.spacecraftType.SolarSC || sc.spacecraftType.GetSolarRange(cb.Company) >= targetDistanceToSunAU) &&
                        !cb.SpacecraftsLockedForAIMission.Contains(sc) &&
                        sc.spacecraftType.CargoCapacity >= minCapacity &&
                        !sc.spacecraftType.LowOrbitContainer &&
                        !sc.spacecraftType.cheatSC &&
                        sc.spacecraftType.GetFuelCapacity(cb.Company) >= minFuelCapacity.Value)
                    .OrderByDescending(sc => ScoreSpacecraftTypeForFlight(cb, sc.spacecraftType, to, minCapacity, minFuelCapacity.Value))
                    .FirstOrDefault();

                if (spacecraft.Value?.Object != null)
                {
                    spacecraftType.Value = spacecraft.Value.Object.spacecraftType;
                    return true;
                }
            }

            while (true)
            {
                spacecraftType.Value = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllSpacecraftType.ListNotEmpty
                    .Where(type =>
                        type.CargoCapacity >= minCapacity &&
                        type.GetFuelCapacity(cb.Company) >= minFuelCapacity.Value &&
                        (!type.SolarSC || type.GetSolarRange(cb.Company) >= targetDistanceToSunAU) &&
                        !type.cheatSC &&
                        !type.LowOrbitContainer &&
                        (cb.Company.IsUnlockSpacecraftType(type) ||
                         (cb.AIConfig.includeUnlockableSpacecraftAndLVsForFlights && ResearchManager.FindResearchForProductionItem(type) != null)))
                    .OrderByDescending(type => ScoreSpacecraftTypeForFlight(cb, type, to, minCapacity, minFuelCapacity.Value))
                    .FirstOrDefault();

                if (spacecraftType.Value != null)
                    break;

                if (cargoElement is ResourceDefinition && howMuch.Value > 1f)
                {
                    howMuch.Value = Mathf.Ceil(howMuch.Value / 2f);
                    minCapacity = howMuch.Value;
                    minFuelCapacity.Value = 0f;
                    continue;
                }

                if (!(module != null && crewTransportModule) || !(howMuch.Value > 1f))
                    break;

                howMuch.Value = Mathf.Ceil(howMuch.Value / 2f);
                minCapacity = module.Mass + howMuch.Value;
            }

            if (spacecraftType.Value != null && !dontReuse)
            {
                spacecraft.Value = MonoBehaviourSingleton<ShipManager>.Instance.ListAllSpaceShip
                    .Where(sc =>
                        sc != null &&
                        Equals(sc.spacecraftType, spacecraftType.Value) &&
                        Equals(sc.GetCompany(), cb.Company) &&
                        ((sc.CurrentPhase == Spacecraft.EPhase.None && sc.CurrentlyOnThisObject.IsEqualOrIsOrbitOf(from)) ||
                         (sc.CurrentPhase == Spacecraft.EPhase.Landing && sc.MissionTarget.IsEqualOrIsOrbitOf(from))) &&
                        !cb.SpacecraftsLockedForAIMission.Contains(sc))
                    .OrderByDescending(sc => ScoreSpacecraftTypeForFlight(cb, sc.spacecraftType, to, minCapacity, minFuelCapacity.Value))
                    .FirstOrDefault();
            }

            if (spacecraftType.Value != null)
            {
                Plugin.Log.LogInfo($"[AIOverhaul] {cb.Company.ID} selected spacecraft '{spacecraftType.Value.ID}' for route {from.ObjectName}->{to.ObjectName}.");
                return true;
            }

            return false;
        }

        internal static bool TryFindStrategicLaunchVehicleForFlight(
            CompanyBehaviour cb,
            ObjectInfo from,
            MyIDScriptableObject cargoElement,
            SharedFloat howMuch,
            MyIDScriptableObject spacecraftType,
            SharedMyIDScriptableObject launchVehicleType,
            SharedLaunchVehicle launchVehicle,
            SharedBool launchVehicleNotNeeded,
            SharedFloat minFuelCapacity,
            bool forceLV,
            bool dontReuse)
        {
            if (cb?.Company == null || from == null || howMuch == null || launchVehicleType == null || launchVehicle == null || launchVehicleNotNeeded == null || minFuelCapacity == null)
                return false;

            var module = cargoElement as SpaceModuleDescriptor;
            bool crewTransportModule = module != null && module.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewTransport);
            double minCapacity = cargoElement == null ? 0.0 : (module != null ? module.Mass + (crewTransportModule ? howMuch.Value : 0f) : howMuch.Value);
            var scType = spacecraftType as SpacecraftType;
            launchVehicleNotNeeded.Value = false;
            if (scType == null)
                return false;

            launchVehicle.Value = null;
            bool fromIsOrbit = string.Equals(from.objectTypes.ToString(), "Orbit", StringComparison.OrdinalIgnoreCase);
            if (launchVehicleType.Value != null && (fromIsOrbit || (!from.Equals(cb.Company.mainObjectInfo) && !scType.needLaunchVehicleToGoToMoon) || !from.NeedVehicleToLaunch()) && !forceLV)
            {
                launchVehicleType.Value = null;
            }

            if (launchVehicleType.Value != null)
            {
                if (!dontReuse)
                {
                    launchVehicle.Value = from.ListLaunchVehicle
                        .Where(lv => lv.launchVehicleType == launchVehicleType.Value && !cb.LaunchVehiclesLockedForAIMission.Contains(lv) && (!lv.launchTime.HasValue || lv.launchVehicleType.reusability > 0f) && Equals(lv.company, cb.Company))
                        .OrderByDescending(lv => ScoreLaunchVehicleTypeForFlight(cb, lv.launchVehicleType, from, scType, minCapacity, minFuelCapacity.Value))
                        .FirstOrDefault();
                }
                return true;
            }

            if ((!fromIsOrbit && (from.Equals(cb.Company.mainObjectInfo) || scType.needLaunchVehicleToGoToMoon) && from.NeedVehicleToLaunch()) || forceLV)
            {
                if (!dontReuse)
                {
                    launchVehicle.Value = from.ListLaunchVehicle
                        .Where(lv =>
                            !cb.LaunchVehiclesLockedForAIMission.Contains(lv) &&
                            lv.company == cb.Company &&
                            (!lv.launchTime.HasValue || lv.launchVehicleType.reusability > 0f) &&
                            lv.MaxPayloadOnCurrentObject >= (double)scType.Mass + minCapacity + minFuelCapacity.Value &&
                            lv.launchVehicleType.maxFuelLoad >= scType.GetFuelCapacity(cb.Company))
                        .OrderByDescending(lv => ScoreLaunchVehicleTypeForFlight(cb, lv.launchVehicleType, from, scType, minCapacity, minFuelCapacity.Value))
                        .FirstOrDefault();

                    if (launchVehicle.Value != null)
                    {
                        launchVehicleType.Value = launchVehicle.Value.Object.launchVehicleType;
                        return true;
                    }
                }

                var fromData = from.GetObjectInfoData(cb.Company);
                launchVehicleType.Value = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllLaunchVehicleType.ListNotEmpty
                    .Where(type =>
                        type.MaxPayloadOnThisObject(from, cb.Company) >= (double)scType.Mass + minCapacity + minFuelCapacity.Value &&
                        type.CanBuildOnThisPlanet(fromData) &&
                        type.CanBuildParameter.CanBuild(fromData) &&
                        (cb.Company.IsUnlockRocketType(type) ||
                         (cb.AIConfig.includeUnlockableSpacecraftAndLVsForFlights && ResearchManager.FindResearchForProductionItem(type) != null)))
                    .OrderByDescending(type => ScoreLaunchVehicleTypeForFlight(cb, type, from, scType, minCapacity, minFuelCapacity.Value))
                    .FirstOrDefault();

                if (launchVehicleType.Value != null && !dontReuse)
                {
                    launchVehicle.Value = from.ListLaunchVehicle
                        .Where(lv => lv.launchVehicleType == launchVehicleType.Value && !cb.LaunchVehiclesLockedForAIMission.Contains(lv) && (!lv.launchTime.HasValue || lv.launchVehicleType.reusability > 0f) && lv.company == cb.Company)
                        .OrderByDescending(lv => ScoreLaunchVehicleTypeForFlight(cb, lv.launchVehicleType, from, scType, minCapacity, minFuelCapacity.Value))
                        .FirstOrDefault();
                }

                if (launchVehicleType.Value != null)
                {
                    Plugin.Log.LogInfo($"[AIOverhaul] {cb.Company.ID} selected launch vehicle '{launchVehicleType.Value.ID}' for spacecraft '{scType.ID}' from {from.ObjectName}.");
                    return true;
                }

                return false;
            }

            launchVehicleNotNeeded.Value = true;
            return true;
        }

        internal static bool HasMeaningfulMiningOpportunity(ObjectInfoData data)
        {
            return data?.ObjectInfo != null &&
                data.ObjectInfo.CanMineResources &&
                data.ObjectInfo.ListRowResourcesData != null &&
                data.ObjectInfo.ListRowResourcesData.Any(rd =>
                    rd != null &&
                    rd.Value > 0.0 &&
                    rd.MiningFactor > 0f &&
                    rd.ResourcesType != null &&
                    rd.ResourcesType.GetMiningGroundFacility() != null);
        }

        internal static ResourceDefinition FindBestMineableResource(ObjectInfoData data)
        {
            return data?.ObjectInfo?.ListRowResourcesData?
                .Where(rd =>
                    rd != null &&
                    rd.Value > 0.0 &&
                    rd.MiningFactor > 0f &&
                    rd.ResourcesType != null &&
                    rd.ResourcesType.GetMiningGroundFacility() != null)
                .OrderByDescending(rd => rd.MiningFactor)
                .ThenByDescending(rd => rd.Value)
                .Select(rd => rd.ResourcesType)
                .FirstOrDefault();
        }

        internal static ResourceDefinition GetResourceById(string id)
        {
            return string.IsNullOrWhiteSpace(id)
                ? null
                : SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllResourceDefinitions?.GetByID(id);
        }

        internal static FacilityBaseDescriptor GetFacilityById(string id)
        {
            return string.IsNullOrWhiteSpace(id)
                ? null
                : SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllFacility?.GetByID(id);
        }

        internal static bool NeedsMorePower(ObjectInfoData data)
        {
            if (data == null)
                return false;

            if (data.EnergyAvailable < -0.01)
                return true;

            return data.ListFacility != null && data.ListFacility.Any(f =>
                f != null &&
                f.BuildProgress >= 1f &&
                f.Enabled > 0 &&
                f.facilityDescriptor != null &&
                f.facilityDescriptor.EnergyConsumption > 0.0 &&
                f.SinglePowerProductionMultiplier < 0.95);
        }

        internal static bool FacilityCanSourcePowerInputsLocally(ObjectInfoData data, FacilityBaseDescriptor descriptor)
        {
            if (data == null || descriptor?.energyProductionData == null)
                return false;

            var input = descriptor.energyProductionData.input;
            if (input == null || !input.Any())
                return true;

            return input.All(item =>
                item != null &&
                item.resource != null &&
                (BodyHasResourceDeposit(data, item.resource) || CompanyHasAccessibleResource(data.company, item.resource, Math.Max(item.ratePerDay, 0.01))));
        }

        internal static FacilityBaseDescriptor ChooseSuitablePowerFacility(ObjectInfoData data)
        {
            if (data == null)
                return null;

            return SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllFacility.ListNotEmpty
                .Where(descriptor =>
                    descriptor != null &&
                    descriptor.facilityType == FacilityBaseDescriptor.EFacilityType.Power &&
                    descriptor.energyProductionData != null &&
                    descriptor.energyProductionData.energyProduction > 0.0 &&
                    data.CanAddFacility(descriptor) &&
                    FacilityCanSourcePowerInputsLocally(data, descriptor))
                .OrderByDescending(descriptor => descriptor.energyProductionData.solarPanels)
                .ThenByDescending(descriptor => descriptor.energyProductionData.windPower)
                .ThenByDescending(descriptor => descriptor.energyProductionData.geothermalPower)
                .ThenBy(descriptor => descriptor.NeedWorkersToWork(data.company))
                .ThenByDescending(descriptor => descriptor.energyProductionData.energyProduction)
                .FirstOrDefault();
        }

        internal static IEnumerable<ResourceDefinition> GetDesiredLocalFuelResources(CompanyBehaviour cb)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (cb?.Company == null)
                yield break;

            foreach (var ship in MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip ?? Enumerable.Empty<Spacecraft>())
            {
                if (ship?.GetCompany() != cb.Company)
                    continue;

                var fuel = ship.spacecraftType?.GetFuelType();
                if (fuel != null && seen.Add(fuel.ID))
                    yield return fuel;
            }

            foreach (var scType in SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllSpacecraftType?.ListNotEmpty ?? Enumerable.Empty<SpacecraftType>())
            {
                if (scType == null || !cb.Company.IsUnlockSpacecraftType(scType))
                    continue;

                var fuel = scType.GetFuelType();
                if (fuel != null && seen.Add(fuel.ID))
                    yield return fuel;
            }

            foreach (var lvType in SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllLaunchVehicleType?.ListNotEmpty ?? Enumerable.Empty<LaunchVehicleType>())
            {
                if (lvType == null || !cb.Company.IsUnlockRocketType(lvType))
                    continue;

                var fuel = lvType.FuelTypeOnStart;
                if (fuel != null && seen.Add(fuel.ID))
                    yield return fuel;
            }
        }

        internal static FacilityBaseDescriptor ChooseLocalFuelFacility(CompanyBehaviour cb, ObjectInfoData data, out string fuelReason)
        {
            fuelReason = null;
            if (cb == null || data?.ObjectInfo == null)
                return null;

            foreach (var fuel in GetDesiredLocalFuelResources(cb))
            {
                if (!BodyHasResourceDeposit(data, fuel))
                    continue;

                var mine = fuel.GetMiningGroundFacility();
                if (mine != null && !HasFacilityOrProduction(data, mine))
                {
                    fuelReason = $"fuel extraction {fuel.ID}";
                    return mine;
                }
            }

            return null;
        }

        internal static IEnumerable<ObjectInfoData> GetCompanyObjectDatas(Company company)
        {
            var allObjects = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.allObjectInfos;
            if (company == null || allObjects == null)
                yield break;

            foreach (var objectInfo in allObjects)
            {
                var data = objectInfo?.GetObjectInfoData(company);
                if (data != null)
                    yield return data;
            }
        }

        internal static bool CompanyHasAccessibleResource(Company company, ResourceDefinition resource, double amountNeeded)
        {
            if (company == null || resource == null)
                return false;

            return GetCompanyObjectDatas(company).Sum(data => data.CheckResources(resource)) >= amountNeeded;
        }

        internal static string StageResourceToObject(ObjectInfoData targetData, ResourceDefinition resource, double amountNeeded, bool allowReserveFallback = true)
        {
            if (targetData?.company == null || resource == null || amountNeeded <= 0.0)
                return null;

            var company = targetData.company;
            var cb = company.GetComponent<CompanyBehaviour>();
            var sourceSummaries = new List<string>();
            double current = targetData.CheckResources(resource);
            double remaining = Math.Max(0.0, amountNeeded - current);
            if (remaining <= 0.0)
                return null;

            foreach (var source in GetCompanyObjectDatas(company)
                .Where(data => data != null && !ReferenceEquals(data, targetData))
                .OrderByDescending(data => ReferenceEquals(data.ObjectInfo, company.mainObjectInfo))
                .ThenByDescending(data => cb != null && HasPresence(cb, data))
                .ThenByDescending(data => data.CheckResources(resource)))
            {
                if (remaining <= 0.0)
                    break;

                double available = source.CheckResources(resource);
                if (available <= 0.0)
                    continue;

                double moved = Math.Min(available, remaining);
                if (moved <= 0.0)
                    continue;

                if (source.RemoveResource(resource, moved))
                {
                    targetData.AddResources(resource, moved);
                    sourceSummaries.Add($"{resource.ID}:{moved:0.##} from {source.ObjectInfo.ObjectName}");
                    remaining -= moved;
                }
            }

            if (remaining > 0.0 &&
                ReferenceEquals(targetData.ObjectInfo, company.mainObjectInfo) &&
                TryBuyResourceAtHomeWorld(targetData, resource, remaining, out var purchasedSummary))
            {
                sourceSummaries.Add(purchasedSummary);
                remaining = 0.0;
            }

            if (remaining > 0.0 && allowReserveFallback)
            {
                targetData.AddResources(resource, remaining);
                sourceSummaries.Add($"{resource.ID}:{remaining:0.##} from reserve");
            }

            return sourceSummaries.Count > 0 ? string.Join(", ", sourceSummaries) : null;
        }

        internal static bool TryBuyResourceAtHomeWorld(ObjectInfoData targetData, ResourceDefinition resource, double amountNeeded, out string summary)
        {
            summary = null;
            if (targetData?.company?.mainObjectInfo == null ||
                resource == null ||
                amountNeeded <= 0.0 ||
                !ReferenceEquals(targetData.ObjectInfo, targetData.company.mainObjectInfo))
                return false;

            var allSo = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllResourceDefinitions;
            if (allSo?.ListResourceDefinitionInMarketPlaceOffer == null ||
                !allSo.ListResourceDefinitionInMarketPlaceOffer.Contains(resource))
                return false;

            var market = MonoBehaviourSingleton<MarketOfferManager>.Instance;
            if (market == null)
                return false;

            double pricePerUnit = market.GetMarketClearingPrice(resource, targetData.ObjectInfo);
            if (!(pricePerUnit > 0.0))
                pricePerUnit = 1.0;

            double totalCost = pricePerUnit * amountNeeded;
            var money = targetData.company.MoneyController;
            if (money != null && !double.IsInfinity(money.CurrentMoney) && money.CurrentMoney < totalCost)
                return false;

            if (money != null && !double.IsInfinity(money.CurrentMoney) && !money.RemoveMoney(totalCost))
                return false;

            targetData.AddResources(resource, amountNeeded);
            summary = $"{resource.ID}:{amountNeeded:0.##} bought on market at {targetData.ObjectInfo.ObjectName}";
            return true;
        }

        internal static string StageBootstrapResources(ObjectInfoData targetData, FacilityBaseDescriptor facility)
        {
            if (targetData?.company == null || facility?.Price?.ListResources == null)
                return null;

            var sourceSummaries = new List<string>();

            foreach (var item in facility.Price.ListResources.Where(x => x?.ResourceDefinition != null && x.Price > 0))
            {
                string staged = StageResourceToObject(targetData, item.ResourceDefinition, item.Price, allowReserveFallback: true);
                if (!string.IsNullOrEmpty(staged))
                    sourceSummaries.Add(staged);
            }

            return sourceSummaries.Count > 0 ? string.Join(", ", sourceSummaries) : null;
        }

        internal static string PrepareManifestResourcesAtSource(CompanyBehaviour cb, ObjectInfoData sourceData, ObjectInfo destination)
        {
            if (cb?.Company == null || sourceData == null || destination == null)
                return null;

            var sourceSummaries = new List<string>();
            foreach (var demand in BuildDestinationManifest(cb, destination))
            {
                if (demand?.Resource == null || demand.TotalDemand <= 0.0)
                    continue;

                string staged = StageResourceToObject(sourceData, demand.Resource, demand.TotalDemand, allowReserveFallback: false);
                if (!string.IsNullOrEmpty(staged))
                    sourceSummaries.Add(staged);
            }

            return sourceSummaries.Count > 0 ? string.Join(", ", sourceSummaries) : null;
        }

        internal static bool BodyHasResourceDeposit(ObjectInfoData data, ResourceDefinition resource)
        {
            return data?.ObjectInfo?.ListRowResourcesData != null &&
                resource != null &&
                data.ObjectInfo.ListRowResourcesData.Any(rd =>
                    rd != null &&
                    rd.ResourcesType == resource &&
                    rd.Value > 0.0 &&
                    rd.MiningFactor > 0f);
        }

        internal static FacilityBaseDescriptor ChooseIndustryBootstrapFacility(ObjectInfoData data, out string reason)
        {
            reason = null;
            if (data?.company == null || data.ObjectInfo == null)
                return null;

            var cb = data.company.GetComponent<CompanyBehaviour>();
            if (cb == null)
                return null;

            if (data.CumulativeConstructionPower <= 0f)
            {
                reason = "construction";
                return cb.FindSuitableConstructionEquipment(data);
            }

            if (NeedsMorePower(data))
            {
                var power = ChooseSuitablePowerFacility(data);
                if (power != null && !HasFacilityOrProduction(data, power))
                {
                    reason = "power";
                    return power;
                }
            }

            var metal = GetResourceById("id_resource_metal");
            var raremetal = GetResourceById("id_resource_raremetal");
            var steel = GetResourceById("id_resource_steel");
            var alloy = GetResourceById("id_resource_alloy");
            var localFuelFacility = ChooseLocalFuelFacility(cb, data, out var fuelReason);
            if (localFuelFacility != null)
            {
                reason = fuelReason;
                return localFuelFacility;
            }

            var metalMine = metal?.GetMiningGroundFacility() ?? GetFacilityById("build_metalmine");
            if (BodyHasResourceDeposit(data, metal) && metalMine != null && !HasFacilityOrProduction(data, metalMine))
            {
                reason = "metal mining";
                return metalMine;
            }

            var steelRefinery = steel?.GetRefineryGroundFacility() ?? GetFacilityById("build_alloysmelting");
            if (metalMine != null && HasFacilityOrProduction(data, metalMine) && steelRefinery != null && !HasFacilityOrProduction(data, steelRefinery))
            {
                reason = "steel production";
                return steelRefinery;
            }

            var rareMine = raremetal?.GetMiningGroundFacility() ?? GetFacilityById("build_raremine");
            if (BodyHasResourceDeposit(data, raremetal) && rareMine != null && !HasFacilityOrProduction(data, rareMine))
            {
                reason = "rare metal mining";
                return rareMine;
            }

            var alloyRefinery = alloy?.GetRefineryGroundFacility() ?? GetFacilityById("build_exoticalloy");
            var uran = GetResourceById("id_resource_uran");
            bool canSupportAlloyProduction = BodyHasResourceDeposit(data, uran) || CompanyHasAccessibleResource(data.company, uran, 0.01);
            if (rareMine != null && HasFacilityOrProduction(data, rareMine) && alloyRefinery != null && !HasFacilityOrProduction(data, alloyRefinery) && canSupportAlloyProduction)
            {
                reason = "alloy production";
                return alloyRefinery;
            }

            return null;
        }

        internal static bool EnsureStrategicIndustry(CompanyBehaviour cb)
        {
            if (cb?.Company == null || cb.Company.IsPlayer || cb.Company.Definition.IsWorldGovernment)
                return false;

            var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? default;
            if (now == default)
                return false;

            bool changed = false;
            var allObjects = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.allObjectInfos;
            if (allObjects == null)
                return false;

            foreach (var objectInfo in allObjects)
            {
                if (objectInfo == null || ReferenceEquals(objectInfo, cb.Company.mainObjectInfo))
                    continue;

                var data = objectInfo.GetObjectInfoData(cb.Company);
                if (data == null || !HasPresence(cb, data) || data.CurrentCrew < 10)
                    continue;

                string bodyKey = GetBodyScopedKey(cb.Company, objectInfo) + ":industry";
                if (WasHandledThisMonth(_lastCrewCapacityAdditionPass, bodyKey, now.Year, now.Month))
                    continue;

                string reason;
                FacilityBaseDescriptor needed = ChooseIndustryBootstrapFacility(data, out reason);

                if (needed == null)
                    continue;

                if (!cb.Company.IsUnlockFacility(needed))
                {
                    var research = ResearchManager.FindResearchForProductionItem(needed);
                    if (research != null)
                    {
                        cb.QueueResearch(research, force: true);
                        Plugin.Log.LogInfo($"[AIOverhaul] Queued research '{research.ID}' to industrialize {objectInfo.ObjectName} for {cb.Company.ID} ({reason}).");
                        MarkHandledThisMonth(_lastCrewCapacityAdditionPass, bodyKey, now.Year, now.Month);
                        changed = true;
                    }
                    continue;
                }

                if (data.CanAddFacility(needed) && !HasFacilityOrProduction(data, needed))
                {
                    string bootstrapSource = StageBootstrapResources(data, needed);
                    data.AddFacility(needed, prebuilt: false);
                    Plugin.Log.LogInfo($"[AIOverhaul] Queued {needed.ID} on {objectInfo.ObjectName} for {cb.Company.ID} ({reason})" +
                        (string.IsNullOrEmpty(bootstrapSource) ? "." : $". Seeded resources: {bootstrapSource}."));
                    MarkHandledThisMonth(_lastCrewCapacityAdditionPass, bodyKey, now.Year, now.Month);
                    changed = true;
                }
            }

            return changed;
        }

        internal static bool EnsureColonyWorkforce(CompanyBehaviour cb)
        {
            if (cb?.Company == null || cb.Company.IsPlayer || cb.Company.Definition.IsWorldGovernment)
                return false;

            var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? default;
            if (now == default)
                return false;

            if (_lastWorkforcePass.TryGetValue(cb.Company.ID, out var last) &&
                last.year == now.Year &&
                last.month == now.Month)
                return false;

            _lastWorkforcePass[cb.Company.ID] = (now.Year, now.Month);

            bool changed = false;
            var allObjects = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.allObjectInfos;
            if (allObjects == null)
                return false;

            foreach (var objectInfo in allObjects)
            {
                var data = objectInfo?.GetObjectInfoData(cb.Company);
                if (data == null)
                    continue;

                bool isHomeWorld = ReferenceEquals(objectInfo, cb.Company.mainObjectInfo);

                long totalWorkersNeeded = GetNonHabitatWorkersNeeded(data);
                long criticalWorkersNeeded = GetCriticalWorkersNeeded(data);

                var populations = data.GetPopulationHabitats();
                long habitatCapacity = populations.Item2;
                if (habitatCapacity <= 0 && totalWorkersNeeded <= 0)
                    continue;

                long currentCrew = data.CurrentCrew;
                long minPopulation = isHomeWorld
                    ? Math.Max(cb.Company.Definition.PopulationConfig.minPopulationSize, HomeCrewReserveTarget)
                    : 0L;

                long desiredCrew = isHomeWorld
                    ? Math.Max(minPopulation, HomeCrewReserveTarget)
                    : Math.Max(totalWorkersNeeded + 5L, criticalWorkersNeeded + 5L);

                if (!isHomeWorld && habitatCapacity < desiredCrew)
                {
                    string bodyKey = GetBodyScopedKey(cb.Company, objectInfo);
                    long crewGap = Math.Max(1L, desiredCrew - habitatCapacity);
                    bool hasPendingCrewCapacity = data.ListFacility != null &&
                        data.ListFacility.Any(f => IsCrewCapacityFacility(f) && f.BuildProgress < 1f);

                    var habitat = FindSuitableCrewCapacity(data, crewGap);
                    bool alreadyHasCrewCapacity = data.ListFacility != null &&
                        data.ListFacility.Any(f => IsCrewCapacityFacility(f));
                    bool needsCapacityForActualJobs = habitatCapacity < Math.Max(totalWorkersNeeded, criticalWorkersNeeded);

                    if (habitat != null &&
                        !hasPendingCrewCapacity &&
                        (isHomeWorld || needsCapacityForActualJobs) &&
                        (!alreadyHasCrewCapacity || isHomeWorld || currentCrew >= habitatCapacity - 2L) &&
                        !WasHandledThisMonth(_lastCrewCapacityAdditionPass, bodyKey, now.Year, now.Month))
                    {
                        if (cb.Company.IsUnlockFacility(habitat))
                        {
                            string staged = StageBootstrapResources(data, habitat);
                            data.AddFacility(habitat, prebuilt: false);
                            MarkHandledThisMonth(_lastCrewCapacityAdditionPass, bodyKey, now.Year, now.Month);
                            Plugin.Log.LogInfo($"[AIOverhaul] Queued crew-capacity facility '{habitat.ID}' on {objectInfo.ObjectName} for {cb.Company.ID}. Desired crew {desiredCrew}, capacity {habitatCapacity}, gap {crewGap}.{(string.IsNullOrEmpty(staged) ? string.Empty : $" Seeded resources: {staged}")}");
                            changed = true;
                        }
                    }
                }

                long targetCrew = isHomeWorld ? desiredCrew : Math.Min(desiredCrew, habitatCapacity);
                if (targetCrew > currentCrew)
                {
                    if (!isHomeWorld)
                    {
                        string crewKey = GetBodyScopedKey(cb.Company, objectInfo) + ":crew";
                        if (!WasHandledThisMonth(_lastCrewShipmentPass, crewKey, now.Year, now.Month))
                        {
                            var homeData = cb.Company.mainObjectInfo?.GetObjectInfoData(cb.Company);
                            long offworldCrewGap = targetCrew - currentCrew;
                            if (homeData != null && TryScheduleCrewShipment(cb, homeData, data, offworldCrewGap))
                            {
                                MarkHandledThisMonth(_lastCrewShipmentPass, crewKey, now.Year, now.Month);
                                changed = true;
                            }
                            else
                            {
                                Plugin.Log.LogInfo($"[AIOverhaul] {cb.Company.ID} needs off-world crew on {objectInfo.ObjectName} but could not schedule shipment. Crew {currentCrew}, target {targetCrew}, workers needed {totalWorkersNeeded}, critical jobs {criticalWorkersNeeded}, habitat cap {habitatCapacity}.");
                                MarkHandledThisMonth(_lastCrewShipmentPass, crewKey, now.Year, now.Month);
                            }
                        }
                        continue;
                    }

                    long crewGap = targetCrew - currentCrew;
                    long maxStep = 250L;
                    long minStep = 100L;
                    long toHire = Math.Min(maxStep, Math.Max(minStep, crewGap / 2L));
                    if (criticalWorkersNeeded > currentCrew)
                    {
                        long criticalGap = criticalWorkersNeeded - currentCrew;
                        toHire = Math.Max(toHire, Math.Min(maxStep, criticalGap));
                    }

                    toHire = Math.Min(toHire, crewGap);
                    if (toHire > 0)
                    {
                        data.CrewResource.Value += toHire;
                        Plugin.Log.LogInfo($"[AIOverhaul] Staffed {toHire} crew on {objectInfo.ObjectName} for {cb.Company.ID}. Crew {currentCrew}->{data.CurrentCrew}, target {targetCrew}, workers needed {totalWorkersNeeded}, critical jobs {criticalWorkersNeeded}, habitat cap {habitatCapacity}.");
                        changed = true;
                    }
                }
            }

            return changed;
        }

        internal static bool ShouldReleaseFlightTaskEarly(FlightOrDelivery task)
        {
            if (task == null)
                return false;

            var cargoShared = _flightCargoElementFi?.GetValue(task) as SharedMyIDScriptableObject;
            if (!(cargoShared?.Value is ResourceDefinition))
                return false;

            var planResult = _flightPlanResultFi?.GetValue(task);
            if (planResult == null)
                return false;

            var endCalcPi = planResult.GetType().GetProperty("endCalculation");
            var missionPi = planResult.GetType().GetProperty("missionInfo");
            if (endCalcPi == null || missionPi == null)
                return false;

            bool endCalculation = (bool)endCalcPi.GetValue(planResult);
            var missionInfo = missionPi.GetValue(planResult) as MissionInfo;
            if (!endCalculation || missionInfo == null)
                return false;

            return !missionInfo.cancel && !missionInfo.complete;
        }

        internal static void TryBundleAdditionalCargo(CompanyBehaviour cb, ObjectInfo from, ObjectInfo to, CargoAll cargoAll, Spacecraft spacecraft, MyIDScriptableObject primaryCargo)
        {
            if (cb?.Company == null || from == null || to == null || cargoAll == null || spacecraft?.spacecraftType == null)
                return;

            double cargoCapacity = spacecraft.spacecraftType.GetCargoCapacity(cb.Company);
            double capacityRemaining = cargoCapacity - cargoAll.listCargo.Sum(c => c?.cargoMass ?? 0.0);
            if (capacityRemaining <= 1.0)
                return;

            var fromData = from.GetObjectInfoData(cb.Company);
            if (fromData == null)
                return;

            bool bundledAny = false;
            string primaryId = primaryCargo?.ID;
            var manifestDemands = BuildDestinationManifest(cb, to);
            if (primaryCargo is ResourceDefinition || manifestDemands.Count > 0)
            {
                string manifest = manifestDemands.Count == 0
                    ? "none"
                    : string.Join(", ", manifestDemands.Take(8).Select(x => $"{x.Resource.ID}={x.TotalDemand:0.##}"));
                Plugin.Log.LogInfo($"[AIOverhaul] Evaluating cargo bundle for {cb.Company.ID} {from.ObjectName}->{to.ObjectName}. Capacity={cargoCapacity:0.##}, free={capacityRemaining:0.##}, primary={primaryId ?? "none"}, destination demands: {manifest}");
            }

            if (primaryCargo is ResourceDefinition primaryResource)
            {
                var primaryCargoEntry = cargoAll.listCargo.FirstOrDefault(c =>
                    c != null &&
                    c.resourceTypeType == EResourceTypeType.resorces &&
                    c.resourceType == primaryResource);
                if (primaryCargoEntry != null)
                {
                    var primaryManifest = manifestDemands.FirstOrDefault(x => x.Resource == primaryResource);
                    double demand = primaryManifest?.TotalDemand ?? 0.0;
                    double stockDemand = primaryManifest?.StockDemand ?? 0.0;
                    double available = fromData.CheckResources(primaryResource);
                    double currentAmount = primaryCargoEntry.cargoMass;
                    double extraPrimary = Math.Min(capacityRemaining, Math.Min(Math.Max(0.0, available - currentAmount), Math.Max(0.0, demand - currentAmount)));
                    if (extraPrimary > 0.0)
                    {
                        primaryCargoEntry.cargoMass += extraPrimary;
                        capacityRemaining -= extraPrimary;
                        bundledAny = true;
                        Plugin.Log.LogInfo($"[AIOverhaul] Topped up primary cargo with {extraPrimary:0.##} of {primaryResource.ID} on AI flight {from.ObjectName}->{to.ObjectName} for {cb.Company.ID}.");
                    }
                }
            }

            foreach (var demandEntry in manifestDemands)
            {
                var resource = demandEntry.Resource;
                if (resource == null || string.Equals(resource.ID, primaryId, StringComparison.OrdinalIgnoreCase))
                    continue;

                double demand = demandEntry.TotalDemand;
                double stockDemand = demandEntry.StockDemand;

                double available = fromData.CheckResources(resource);
                if (available <= 0.0)
                    continue;

                double amount = Math.Min(capacityRemaining, Math.Min(available, demand));
                if (amount <= 0.0)
                    continue;

                cargoAll.listCargo.Add(new Cargo
                {
                    resourceTypeType = EResourceTypeType.resorces,
                    resourceType = resource,
                    objectInfo = from,
                    cargoMass = amount
                });
                capacityRemaining -= amount;
                bundledAny = true;
                Plugin.Log.LogInfo($"[AIOverhaul] Bundled {amount:0.##} of {resource.ID} onto AI flight {from.ObjectName}->{to.ObjectName} for {cb.Company.ID}.");

                if (capacityRemaining <= 1.0)
                    break;
            }

            if (!bundledAny && capacityRemaining > 1.0 && primaryCargo is ResourceDefinition)
            {
                Plugin.Log.LogInfo($"[AIOverhaul] No extra cargo available to bundle on AI flight {from.ObjectName}->{to.ObjectName} for {cb.Company.ID}; remaining capacity {capacityRemaining:0.##}.");
            }
        }

        internal static void TryBundleAdditionalModules(CompanyBehaviour cb, ObjectInfo from, ObjectInfo to, CargoAll cargoAll, Spacecraft spacecraft, SpaceModule primarySourceModule)
        {
            if (cb?.Company == null || from == null || to == null || cargoAll == null || spacecraft?.spacecraftType == null)
                return;

            double cargoCapacity = spacecraft.spacecraftType.GetCargoCapacity(cb.Company);
            double capacityRemaining = cargoCapacity - cargoAll.listCargo.Sum(c => c?.cargoMass ?? 0.0);
            if (capacityRemaining <= 1.0)
                return;

            var fromData = from.GetObjectInfoData(cb.Company);
            if (fromData?.ListFacility == null)
                return;

            foreach (var neededModule in GetPendingModuleDeliveries(cb, to))
            {
                if (neededModule == null || neededModule.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewTransport))
                    continue;
                if (capacityRemaining < neededModule.Mass)
                    break;

                var candidates = fromData.ListFacility
                    .OfType<SpaceModule>()
                    .Where(module =>
                        module != null &&
                        module.facilityDescriptor == neededModule &&
                        !cb.IsSpaceModuleFullyLockedForAIMission(module))
                    .ToList();

                foreach (var candidate in candidates)
                {
                    if (candidate == primarySourceModule)
                        continue;
                    if (capacityRemaining < neededModule.Mass)
                        return;
                    if (!cb.TryLockSpaceModuleForAIMission(candidate, 1L))
                        continue;

                    cargoAll.listCargo.Add(new Cargo
                    {
                        resourceTypeType = EResourceTypeType.modules,
                        SourceModule = candidate,
                        moduleData = neededModule,
                        objectInfo = from,
                        cargoMass = neededModule.Mass,
                        crew = false,
                        crewValue = 0
                    });
                    capacityRemaining -= neededModule.Mass;
                    Plugin.Log.LogInfo($"[AIOverhaul] Bundled module {neededModule.ID} onto AI flight {from.ObjectName}->{to.ObjectName} for {cb.Company.ID}.");
                    break;
                }
            }
        }

        internal static bool PlanBundledFlightOrDelivery(FlightOrDelivery task, ref bool result)
        {
            var cb = GetCompanyBehaviour(task);
            if (cb?.Company == null)
            {
                result = false;
                return false;
            }

            var from = _flightFromFi.GetValue(task) as SharedObjectInfo;
            var to = _flightToFi.GetValue(task) as SharedObjectInfo;
            var spacecraft = _flightSpacecraftFi.GetValue(task) as SharedSpacecraft;
            var launchVehicle = _flightLaunchVehicleFi.GetValue(task) as SharedLaunchVehicle;
            var cargoElement = _flightCargoElementFi.GetValue(task) as SharedMyIDScriptableObject;
            var spaceModule = _flightSpaceModuleFi.GetValue(task) as SharedFacility;
            var howMuch = _flightHowMuchFi.GetValue(task) as SharedFloat;
            var quantity = _flightQuantityFi.GetValue(task) as SharedInt;
            var fuelResourceType = _flightFuelResourceTypeFi.GetValue(task) as SharedMyIDScriptableObject;

            if (from?.Value == null || to?.Value == null || spacecraft?.Value?.Object == null || fuelResourceType?.Value == null || howMuch == null || quantity == null)
            {
                result = false;
                return false;
            }

            Plugin.Log.LogInfo($"[AIOverhaul] Custom PlanMission for {cb.Company.ID}: {from.Value.Object.ObjectName}->{to.Value.Object.ObjectName}, cargo={(cargoElement?.Value?.ID ?? "none")}, howMuch={howMuch.Value:0.##}, quantity={quantity.Value}, spacecraft={spacecraft.Value.Object.spacecraftType?.ID ?? "none"}.");

            var cargoAll = CargoAll.CreateCargoEmpty();
            cargoAll.cargoFuel.objectInfo = from.Value.Object;
            cargoAll.cargoFuel.cargoMass = 0.0;
            cargoAll.cargoFuel.resourceType = (ResourceDefinition)fuelResourceType.Value;
            quantity.Value = Math.Max(1, quantity.Value);
            var fromData = from.Value.Object.GetObjectInfoData(cb.Company);
            if (fromData != null)
            {
                string prepared = PrepareManifestResourcesAtSource(cb, fromData, to.Value.Object);
                if (!string.IsNullOrEmpty(prepared))
                    Plugin.Log.LogInfo($"[AIOverhaul] Prepared manifest resources at {from.Value.Object.ObjectName} for {cb.Company.ID} before launch to {to.Value.Object.ObjectName}: {prepared}.");
            }

            var cargoItem = cargoElement?.Value;
            if (cargoItem != null)
            {
                if (cargoItem is SpaceModuleDescriptor moduleDescriptor && spaceModule?.Value != null)
                {
                    bool crew = moduleDescriptor.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.CrewTransport);
                    double moduleCargoMass = moduleDescriptor.Mass;
                    for (int i = 0; i < quantity.Value; i++)
                    {
                        cargoAll.listCargo.Add(new Cargo
                        {
                            resourceTypeType = EResourceTypeType.modules,
                            moduleData = moduleDescriptor,
                            SourceModule = spaceModule.Value.Object as SpaceModule,
                            crew = crew,
                            crewValue = (int)howMuch.Value,
                            objectInfo = from.Value.Object,
                            cargoMass = moduleCargoMass
                        });
                    }
                }
                else if (cargoItem is ResourceDefinition resourceType)
                {
                    cargoAll.listCargo.Add(new Cargo
                    {
                        resourceTypeType = EResourceTypeType.resorces,
                        resourceType = resourceType,
                        objectInfo = from.Value.Object,
                        cargoMass = howMuch.Value
                    });
                }
                else
                {
                    result = false;
                    return false;
                }
            }

            TryBundleAdditionalCargo(cb, from.Value.Object, to.Value.Object, cargoAll, spacecraft.Value.Object, cargoItem);
            TryBundleAdditionalModules(cb, from.Value.Object, to.Value.Object, cargoAll, spacecraft.Value.Object, spaceModule?.Value?.Object as SpaceModule);

            var pmp = new PMMissionParameter();
            pmp.ChangeMissionName($"{spacecraft.Value.Object.GetSpacecraftName()} {spacecraft.Value.ID}\n[AI: {cb.Company.ID}]")
                .SetCompany(cb.Company)
                .SetTabSC(new List<ISpacecraftInfo> { spacecraft.Value.Object }, 1)
                .SetTabCargo(cargoAll)
                .SetDontCheckRemoveFuel(dontCheck: true)
                .SetTabDestination(from.Value, to.Value);
            if (launchVehicle?.Value != null)
            {
                pmp.SetTabLV(new List<ILaunchVehicleInfo> { launchVehicle.Value.Object }, 1);
            }
            else
            {
                pmp.SetTabLV(new List<ILaunchVehicleInfo>(), 0);
            }
            pmp.Fast = true;
            pmp.RandomBestOption = false;
            pmp.TryFastAsPossible = true;

            _flightPmpFi.SetValue(task, pmp);
            _flightPlanResultFi.SetValue(task, MonoBehaviourSingleton<GameManager>.Instance.PlanFlyCode(pmp));
            result = true;
            return false;
        }

        internal static bool HandleCompanyHasEnoughResourceOnEnd(CompanyHasEnoughResource task)
        {
            if (task == null)
                return false;

            var companyResourceOnPlanet = _companyHasEnoughResourceOnPlanetFi?.GetValue(task);
            if (companyResourceOnPlanet == null)
                return false;

            var data = _companyHasEnoughResourceDataFi?.GetValue(task);
            if (data == null || _companyHasEnoughResourceMadeReservationsFi == null)
                return false;

            bool madeReservations = (bool)_companyHasEnoughResourceMadeReservationsFi.GetValue(data);
            if (madeReservations)
                return false;

            _companyHasEnoughResourceMadeReservationsFi.SetValue(data, true);

            var createDemand = _companyHasEnoughResourceCreateDemandFi?.GetValue(task) as SharedBool;
            var makeReservation = _companyHasEnoughResourceMakeReservationFi?.GetValue(task) as SharedBool;
            var remainingAmount = _companyHasEnoughResourceRemainingAmountFi?.GetValue(task) as SharedFloat;
            var where = _companyHasEnoughResourceWhereFi?.GetValue(task) as SharedObjectInfo;
            var what = _companyHasEnoughResourceWhatFi?.GetValue(task) as SharedMyIDScriptableObject;
            var howMuch = _companyHasEnoughResourceHowMuchFi?.GetValue(task) as SharedFloat;

            var cb = GetCompanyBehaviour(task);
            var whereValue = where?.Value?.Object;
            var resource = what?.Value as ResourceDefinition;

            if (createDemand?.Value == true && cb?.Company != null && whereValue != null && resource != null && howMuch != null)
            {
                double existingDemand = cb.GetResourceDemandOnObject(whereValue, resource);
                double targetDemand = Math.Max(0.0, howMuch.Value);
                double delta = Math.Max(0.0, targetDemand - existingDemand);
                if (delta > 0.0)
                    cb.AddResourceDemandOnObject(whereValue, resource, (float)delta);
                createDemand.Value = false;
            }

            if (makeReservation?.Value == true && remainingAmount != null && remainingAmount.Value <= 0f &&
                cb?.Company != null && whereValue != null && resource != null && howMuch != null)
            {
                cb.AddResourceReservationOnObject(whereValue, resource, howMuch.Value);
                makeReservation.Value = false;
            }

            return false;
        }

    }

    [HarmonyPatch(typeof(CompanyBehaviour), "GetAllOtherObjectInfosSortedByDistance")]
    static class PatchAIGetAllOtherObjectInfosSortedByDistance
    {
        static void Postfix(CompanyBehaviour __instance, ObjectInfo where, ref List<ObjectInfoData> __result)
        {
            if (__result == null || __instance?.Company == null)
                return;

            __result = __result
                .OrderByDescending(data => AIOverhaul.HasPresence(__instance, data))
                .ThenBy(data => AIOverhaul.InfrastructureRank(data))
                .ThenBy(data => AIOverhaul.SolarDistance(where, data))
                .ThenBy(data => string.Equals(data.ObjectInfo.objectTypes.ToString(), "Orbit", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();
        }
    }

    [HarmonyPatch(typeof(CompanyBehaviour), "FindSuitableSpacecraftForFlight")]
    static class PatchAIFindSuitableSpacecraftForFlight
    {
        static bool Prefix(
            CompanyBehaviour __instance,
            ObjectInfo from,
            ObjectInfo to,
            MyIDScriptableObject cargoElement,
            SharedFloat howMuch,
            SharedMyIDScriptableObject spacecraftType,
            SharedSpacecraft spacecraft,
            SharedFloat minFuelCapacity,
            bool dontReuse,
            ref bool __result)
        {
            __result = AIOverhaul.TryFindStrategicSpacecraftForFlight(__instance, from, to, cargoElement, howMuch, spacecraftType, spacecraft, minFuelCapacity, dontReuse);
            return false;
        }
    }

    [HarmonyPatch(typeof(CompanyBehaviour), "FindSuitableLaunchVehicleForFlight")]
    static class PatchAIFindSuitableLaunchVehicleForFlight
    {
        static bool Prefix(
            CompanyBehaviour __instance,
            ObjectInfo from,
            MyIDScriptableObject cargoElement,
            SharedFloat howMuch,
            MyIDScriptableObject spacecraftType,
            SharedMyIDScriptableObject launchVehicleType,
            SharedLaunchVehicle launchVehicle,
            SharedBool launchVehicleNotNeeded,
            SharedFloat minFuelCapacity,
            bool forceLV,
            bool dontReuse,
            ref bool __result)
        {
            __result = AIOverhaul.TryFindStrategicLaunchVehicleForFlight(__instance, from, cargoElement, howMuch, spacecraftType, launchVehicleType, launchVehicle, launchVehicleNotNeeded, minFuelCapacity, forceLV, dontReuse);
            return false;
        }
    }

    [HarmonyPatch(typeof(PickRandomResearch), "OnUpdate")]
    static class PatchAIPickRandomResearch
    {
        static bool Prefix(PickRandomResearch __instance, ref BDTaskStatus __result)
        {
            var cb = AIOverhaul.GetCompanyBehaviour(__instance);
            var shared = AIOverhaul._pickResearchFi?.GetValue(__instance) as SharedMyIDScriptableObject;
            if (cb == null || shared == null)
                return true;

            var chosen = AIOverhaul.ChooseStrategicResearch(cb);
            shared.Value = chosen;
            if (chosen != null)
            {
                Plugin.Log.LogInfo($"[AIOverhaul] {cb.Company.ID} selected research '{chosen.ID}'.");
            }
            __result = chosen != null
                ? BDTaskStatus.Success
                : BDTaskStatus.Failure;
            return false;
        }
    }

    [HarmonyPatch(typeof(UnlockProductionMeans), "OnUpdate")]
    static class PatchAIUnlockProductionMeans
    {
        static bool Prefix(UnlockProductionMeans __instance, ref BDTaskStatus __result)
        {
            __result = AIOverhaul.HandleUnlockProductionMeans(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(Spacecraft), "ChangeStage")]
    static class PatchAISpacecraftReturnHome
    {
        static void Prefix(Spacecraft __instance, ref Spacecraft.EPhase __state)
        {
            __state = __instance.CurrentPhase;
        }

        static void Postfix(Spacecraft __instance, Spacecraft.EPhase phase, Spacecraft.EPhase __state)
        {
            var company = __instance?.GetCompany();
            if (company != null && !company.IsPlayer)
            {
                if (__state == Spacecraft.EPhase.Launch && phase == Spacecraft.EPhase.Fly)
                {
                    Plugin.Log.LogInfo($"[AIOverhaul] {company.ID} launched '{__instance.GetSpacecraftName()}' from {__instance.StartObjectOnLand()?.ObjectName ?? "?"} to {__instance.EndObjectOnLand()?.ObjectName ?? "?"}.");
                }
                else if (__state == Spacecraft.EPhase.Fly && phase == Spacecraft.EPhase.Landing)
                {
                    Plugin.Log.LogInfo($"[AIOverhaul] {company.ID} ship '{__instance.GetSpacecraftName()}' arrived at {__instance.EndObjectOnLand()?.ObjectName ?? "?"}.");
                }
            }

            if (__state == Spacecraft.EPhase.Landing && phase == Spacecraft.EPhase.None)
            {
                AIOverhaul.TryScheduleReturnHome(__instance);
            }
        }

    }

    [HarmonyPatch(typeof(DoContract), "OnStart")]
    static class PatchAIDoContractLogging
    {
        static void Postfix(DoContract __instance)
        {
            var cb = AIOverhaul.GetCompanyBehaviour(__instance);
            var shared = AIOverhaul._doContractContractFi?.GetValue(__instance) as SharedContract;
            var contract = shared?.Value?.Object;
            if (cb?.Company == null || contract == null)
                return;

            Plugin.Log.LogInfo($"[AIOverhaul] {cb.Company.ID} started contract '{contract.ContractDefinition.ID}'.");
        }
    }

    [HarmonyPatch(typeof(CompanyHasEnoughResource), "OnEnd")]
    static class PatchAICompanyHasEnoughResourceDemandFix
    {
        static bool Prefix(CompanyHasEnoughResource __instance)
        {
            return AIOverhaul.HandleCompanyHasEnoughResourceOnEnd(__instance);
        }
    }

    [HarmonyPatch(typeof(FlightOrDelivery), "OnUpdate")]
    static class PatchAIFlightOrDeliveryConcurrency
    {
        static void Postfix(FlightOrDelivery __instance, ref BDTaskStatus __result)
        {
            if (__result != BDTaskStatus.Running)
                return;

            if (!AIOverhaul.ShouldReleaseFlightTaskEarly(__instance))
                return;

            var cb = AIOverhaul.GetCompanyBehaviour(__instance);
            if (cb?.Company == null)
                return;

            Plugin.Log.LogInfo($"[AIOverhaul] {cb.Company.ID} released a resource delivery task early after launch so the AI can queue additional flights.");
            __result = BDTaskStatus.Success;
        }
    }

    [HarmonyPatch(typeof(ObjectInfoManager), "Update")]
    static class PatchAIHomeWorkforce
    {
        static void Postfix()
        {
            var gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (gm?.Companies == null)
                return;

            foreach (var company in gm.Companies)
            {
                var cb = company != null ? company.GetComponent<CompanyBehaviour>() : null;
                if (cb == null)
                    continue;

                AIOverhaul.LogActiveContracts(cb);
                AIOverhaul.EnsureColonyWorkforce(cb);
                AIOverhaul.EnsureStrategicIndustry(cb);
                AIOverhaul.EnsureFleetReserve(cb);
                AIOverhaul.EnsureReusableShipsReturnHome(cb);
            }
        }
    }
}
