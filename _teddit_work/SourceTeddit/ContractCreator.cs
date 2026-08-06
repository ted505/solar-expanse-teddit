using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Data;
using Data.ScriptableObject;
using Game.CompanyScripts;
using Game.ContractsObjectives;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Language;
using Manager;
using Newtonsoft.Json.Linq;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    internal static class ContractCreator
    {
        const string Prefix = "[ContractPatcher]";

        internal static readonly HashSet<Reward> AiOnlyRewards = new HashSet<Reward>();

        static readonly HashSet<string> _complexContractKeys = new HashSet<string>
        {
            "title", "description", "descriptionEnd",
            "objectives", "helpObjectives",
            "rewards", "rewardsStartContract",
            "override",
            "unlockContract", "isLockedByContract", "unlockResearch",
            "whatResource", "whatSpaceModuleDescriptor", "launchVehicleType",
            "targetID", "startID",
            "steamAchievements"
        };

        internal static HashSet<string> ComplexKeys => _complexContractKeys;

        // ── Creation ──────────────────────────────────────────────────────────

        internal static void CreateAndInjectContract(string id, Dictionary<string, JToken> def)
        {
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;

            var cd = ScriptableObject.CreateInstance<ContractDefinition>();
            SetField(cd, "m_Name", id);
            var idFi = ScriptableObjectPatcher.FindField(typeof(MyIDScriptableObject), "id")
                    ?? ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "id");
            if (idFi != null) idFi.SetValue(cd, id);

            // Simple fields
            var simpleFields = def
                .Where(f => !_complexContractKeys.Contains(f.Key))
                .ToDictionary(f => f.Key, f => f.Value);
            ScriptableObjectPatcher.ApplyFieldsPublic(cd, simpleFields, Prefix, id);

            // Complex fields
            ApplyComplexFields(cd, def, id, allSO);

            // Translations
            InjectTranslations(id, def);

            // Register in AllContract
            var allContract = allSO.AllContract;
            Type baseType = allContract.GetType().BaseType;
            var listFI = ScriptableObjectPatcher.FindField(baseType, "list");
            var listNEFI = ScriptableObjectPatcher.FindField(baseType, "listNotEmpty");
            if (listFI == null || listNEFI == null)
                throw new Exception("Could not find AllContract list fields via reflection.");

            ((List<ContractDefinition>)listFI.GetValue(allContract)).Add(cd);
            ((List<ContractDefinition>)listNEFI.GetValue(allContract)).Add(cd);

            try { allSO.AllMyIDScriptableObjects.Add(cd); }
            catch (Exception ex) { Plugin.Log.LogWarning($"{Prefix} AllMyIDScriptableObjects.Add failed: {ex.Message}"); }

            // Create runtime Contract instance so ContractManager knows about it
            try
            {
                var cm = MonoBehaviourSingleton<ContractManager>.Instance;
                if (cm != null && !cm.ContractsInstances.ContainsKey(cd))
                {
                    var contract = new Contract(cd);
                    Plugin.Log.LogInfo($"{Prefix} + {id} (runtime Contract created, allContracts={cm.allContracts.Count})");
                }
                else
                {
                    Plugin.Log.LogInfo($"{Prefix} + {id} (ContractManager not ready or already registered)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"{Prefix} {id}: runtime Contract creation failed — {ex.Message}");
                Plugin.Log.LogInfo($"{Prefix} + {id} (definition only)");
            }
        }

        // ── Complex field application (shared by create & patch) ──────────

        internal static void ApplyComplexFields(ContractDefinition cd, Dictionary<string, JToken> fields,
                                                string id, AllScriptableObjectManager allSO,
                                                bool replace = false)
        {
            JToken tok;

            // Object references
            if (fields.TryGetValue("targetID", out tok))
                cd.targetID = ResolveObjectId(tok);
            if (fields.TryGetValue("startID", out tok))
                cd.startID = ResolveObjectId(tok);

            if (fields.TryGetValue("whatResource", out tok) && tok.Type == JTokenType.String)
            {
                var res = allSO.AllResourceDefinitions.GetByID(tok.Value<string>());
                if (res != null) cd.whatResource = res;
                else Plugin.Log.LogWarning($"{Prefix} {id}: unknown whatResource '{tok}'");
            }
            if (fields.TryGetValue("whatSpaceModuleDescriptor", out tok) && tok.Type == JTokenType.String)
            {
                var smd = allSO.AllFacility.GetByID(tok.Value<string>()) as SpaceModuleDescriptor;
                if (smd != null) cd.whatSpaceModuleDescriptor = smd;
                else Plugin.Log.LogWarning($"{Prefix} {id}: unknown whatSpaceModuleDescriptor '{tok}'");
            }
            if (fields.TryGetValue("launchVehicleType", out tok) && tok.Type == JTokenType.String)
            {
                var lv = allSO.AllLaunchVehicleType.GetByID(tok.Value<string>());
                if (lv != null) cd.launchVehicleType = lv;
                else Plugin.Log.LogWarning($"{Prefix} {id}: unknown launchVehicleType '{tok}'");
            }

            // Contract chain references
            if (fields.TryGetValue("unlockContract", out tok) && tok.Type == JTokenType.String)
            {
                var target = allSO.AllContract.GetByID(tok.Value<string>());
                var fi = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "unlockContractHelpNotUse");
                if (fi != null && target != null) fi.SetValue(cd, target);
            }
            if (fields.TryGetValue("isLockedByContract", out tok) && tok.Type == JTokenType.String)
            {
                var target = allSO.AllContract.GetByID(tok.Value<string>());
                if (target != null) cd.isLockByContractHelpNotUse = target;
            }
            if (fields.TryGetValue("unlockResearch", out tok) && tok.Type == JTokenType.String)
            {
                var target = allSO.AllResearchDefinition.GetByID(tok.Value<string>());
                if (target != null) cd.unlockResearchDefinitionHelpNotUse = target;
            }

            // Objectives (append by default, replace if override: true)
            if (fields.TryGetValue("objectives", out tok) && tok.Type == JTokenType.Array)
            {
                var additions = BuildObjectiveList((JArray)tok, id, allSO);
                var fi = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "objectives");
                if (fi != null)
                {
                    var final = replace ? additions : MergeList(fi.GetValue(cd) as List<Objective>, additions);
                    fi.SetValue(cd, final);
                }
            }
            if (fields.TryGetValue("helpObjectives", out tok) && tok.Type == JTokenType.Array)
            {
                var additions = BuildObjectiveList((JArray)tok, id, allSO);
                var fi = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "helpObjectives");
                if (fi != null)
                {
                    var final = replace ? additions : MergeList(fi.GetValue(cd) as List<Objective>, additions);
                    fi.SetValue(cd, final);
                }
            }

            // Rewards (append by default, replace if override: true)
            if (fields.TryGetValue("rewards", out tok) && tok.Type == JTokenType.Array)
            {
                var additions = BuildRewardList((JArray)tok, id, allSO);
                var fi = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "rewards");
                if (fi != null)
                {
                    var final = replace ? additions : MergeList(fi.GetValue(cd) as List<Reward>, additions);
                    fi.SetValue(cd, final);
                }
            }
            if (fields.TryGetValue("rewardsStartContract", out tok) && tok.Type == JTokenType.Array)
            {
                var additions = BuildRewardList((JArray)tok, id, allSO);
                var fi = ScriptableObjectPatcher.FindField(typeof(ContractDefinition), "rewardsStartContract");
                if (fi != null)
                {
                    var final = replace ? additions : MergeList(fi.GetValue(cd) as List<Reward>, additions);
                    fi.SetValue(cd, final);
                }
            }
        }

        // ── Objective building ────────────────────────────────────────────────

        static List<Objective> BuildObjectiveList(JArray arr, string contractId, AllScriptableObjectManager allSO)
        {
            var list = new List<Objective>();
            foreach (var item in arr)
            {
                if (item.Type != JTokenType.Object) continue;
                try
                {
                    var obj = BuildObjective((JObject)item, contractId, allSO);
                    if (obj != null) list.Add(obj);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"{Prefix} {contractId}: objective build failed — {ex.Message}");
                }
            }
            return list;
        }

        static readonly Dictionary<EObjectiveType, string> _defaultObjectiveIds = new Dictionary<EObjectiveType, string>
        {
            { EObjectiveType.Deliver,                    "id_Contract_Deliver_Objective1" },
            { EObjectiveType.BuildFacility,              "id_Contract_BuildFacility_Objective2" },
            { EObjectiveType.CreateSpaceCraft,           "id_Contract_CreatNewSpacecraft_Objective2" },
            { EObjectiveType.CreateVehicle,              "id_Contract_CreatNewVehicle_Objective2" },
            { EObjectiveType.MakeResearch,               "id_Contract_MakeResearch_Objective1" },
            { EObjectiveType.Possession,                 "id_Contract_Possession_Objective1" },
            { EObjectiveType.ScheduleFly,                "id_Contract_ScheduleFly_Objective1" },
            { EObjectiveType.Move,                       "id_Contract_Move_Objective1" },
            { EObjectiveType.ExplorationObject,          "id_Contract_ExplorationObject_Objective1" },
            { EObjectiveType.MarketPlaceOffers,          "id_Contract_MarketPlaceOffers_Objective1" },
            { EObjectiveType.ChangeHabitabilityParameters,"id_Contract_ChangeHabitabilityParameters_Objective1" },
            { EObjectiveType.ChangeDeposit,              "id_Contract_ChangeDeposit_Objective1" },
            { EObjectiveType.ExplorationInterstellar,    "id_Contract_ExplorationInterstellar_Objective1" },
            { EObjectiveType.ScheduleCyclicalMission,    "id_Contract_ScheduleCyclicalFly_Objective1" },
            { EObjectiveType.SelectLayer,                "id_Contract_SelectLayer_Objective1" },
            { EObjectiveType.MakeEnergyProduction,       "id_Contract_MakeEnergyProduction_Objective1" },
            { EObjectiveType.DetonateNuclearDevice,      "id_Contract_DetonateNuclearDevice_Objective1" },
        };

        static Objective BuildObjective(JObject data, string contractId, AllScriptableObjectManager allSO)
        {
            var obj = new Objective();
            obj.tutorialOnForThisObjective = false;

            if (data.TryGetValue("objectiveType", out var typeTok))
                obj.objectiveType = ParseEnum<EObjectiveType>(typeTok.Value<string>());

            if (data.TryGetValue("id", out var idTok))
                obj.ID = idTok.Value<string>();
            else if (_defaultObjectiveIds.TryGetValue(obj.objectiveType, out var defaultId))
                obj.ID = defaultId;
            else
                obj.ID = "id_Contract_" + obj.objectiveType + "_Objective1";

            if (data.TryGetValue("productItem", out var piTok) && piTok.Type == JTokenType.String)
                obj.productItem = ResolveScriptableObject(piTok.Value<string>(), allSO);
            if (data.TryGetValue("productItem2", out var pi2Tok) && pi2Tok.Type == JTokenType.String)
                obj.productItem2 = ResolveScriptableObject(pi2Tok.Value<string>(), allSO);

            if (data.TryGetValue("fromList", out var flTok))
                obj.fromList = flTok.Value<bool>();
            if (data.TryGetValue("productItems", out var pisTok) && pisTok.Type == JTokenType.Array)
                obj.productItems = ((JArray)pisTok)
                    .Select(x => ResolveScriptableObject(x.Value<string>(), allSO))
                    .Where(x => x != null).ToArray();

            if (data.TryGetValue("howMuch", out var hmTok))
                obj.howMuch = hmTok.Value<float>();
            if (data.TryGetValue("howMuch2", out var hm2Tok))
                obj.howMuch2 = hm2Tok.Value<float>();

            if (data.TryGetValue("fromID", out var fromTok))
                obj.fromID = ResolveObjectId(fromTok);
            if (data.TryGetValue("toID", out var toTok))
                obj.toID = ResolveObjectId(toTok);
            if (data.TryGetValue("fromIDString", out var fromSTok))
                obj.fromIDString = fromSTok.Value<string>();
            if (data.TryGetValue("toIDString", out var toSTok))
                obj.toIDString = toSTok.Value<string>();

            if (data.TryGetValue("possessionType", out var ptTok))
                obj.possessionType = ParseEnum<Objective.EPossessionType>(ptTok.Value<string>());
            if (data.TryGetValue("resourceTypeType", out var rttTok))
                obj.resourceTypeType = ParseEnum<EResourceTypeType>(rttTok.Value<string>());

            if (data.TryGetValue("deliverEntireAsteroid", out var deaTok))
                obj.deliverEntireAsteroid = deaTok.Value<bool>();
            if (data.TryGetValue("checkCrewInCargoMustBe", out var ccTok))
                obj.checkCrewInCargoMustBe = ccTok.Value<bool>();
            if (data.TryGetValue("justScheduleToComplete", out var jsTok))
                obj.justScheduleToComplete = jsTok.Value<bool>();

            if (data.TryGetValue("advance", out var advTok))
                obj.advance = advTok.Value<bool>();
            if (data.TryGetValue("moonOfID", out var moonTok))
                obj.moonOfID = ResolveObjectId(moonTok);
            else
                obj.moonOfID = -1;
            if (data.TryGetValue("objectSubTypeID", out var ostTok))
                obj.objectSubTypeID = ostTok.Value<string>();
            if (data.TryGetValue("advanceObjectTypes", out var aotTok))
                obj.advanceObjectTypes = ParseEnum<Data.EObjectTypes>(aotTok.Value<string>());

            if (data.TryGetValue("asteroidPullingNeedDeposit", out var apdTok) && apdTok.Type == JTokenType.Array)
                obj.asteroidPullingNeedDeposit = ((JArray)apdTok)
                    .Select(x => allSO.AllResourceDefinitions.GetByID(x.Value<string>()))
                    .Where(x => x != null).ToList();

            if (data.TryGetValue("helpSpaceCraftType", out var hsctTok) && hsctTok.Type == JTokenType.String)
                obj.helpSpaceCraftType = allSO.AllSpacecraftType.GetByID(hsctTok.Value<string>());

            if (data.TryGetValue("needPreviousObjective", out var npoTok))
                obj.needPreviousObjective = npoTok.Value<bool>();
            if (data.TryGetValue("checkCompletedOnStart", out var ccosTok))
                obj.checkCompletedOnStart = ccosTok.Value<bool>();
            if (data.TryGetValue("dummy", out var dTok))
                obj.dummy = dTok.Value<bool>();
            if (data.TryGetValue("dummyVersion1", out var dv1Tok))
                obj.dummyVersion1 = dv1Tok.Value<bool>();
            if (data.TryGetValue("dummyAlternativeIDTranslate", out var daitTok))
                obj.dummyAlternativeIDTranslate = daitTok.Value<string>();

            if (data.TryGetValue("afterFindAutomaticFillFromID", out var affTok))
                obj.afterFindAutomaticFillFromID = affTok.Value<bool>();
            if (data.TryGetValue("afterBuildAutomaticFillToID", out var abtTok))
                obj.afterBuildAutomaticFillToID = abtTok.Value<bool>();
            if (data.TryGetValue("afterBuildAutomaticFillToIDObjectNameHighLightShort", out var abtsTok))
                obj.afterBuildAutomaticFillToIDObjectNameHighLightShort = abtsTok.Value<bool>();
            if (data.TryGetValue("fromIDUseTranslateBeforeBuild", out var fiutTok))
                obj.fromIDUseTranslateBeforeBuild = fiutTok.Value<string>();
            if (data.TryGetValue("toIDUseTranslateBeforeBuild", out var tiutTok))
                obj.toIDUseTranslateBeforeBuild = tiutTok.Value<string>();

            if (data.TryGetValue("dateTimeStringLimit", out var dtlTok))
                obj.dateTimeStringLimit = dtlTok.Value<string>();
            if (data.TryGetValue("dateTimeStringLimit2", out var dtl2Tok))
                obj.dateTimeStringLimit2 = dtl2Tok.Value<string>();
            if (data.TryGetValue("helpObjectiveID", out var hoTok))
                obj.helpObjectiveID = hoTok.Value<string>();
            if (data.TryGetValue("objectiveToMarkDoneAfterThisObjective", out var otmdTok))
                obj.objectiveToMarkDoneAfterThisObjective = otmdTok.Value<string>();
            if (data.TryGetValue("showWarningInPlanMission", out var swTok))
                obj.showWarningInPlanMission = swTok.Value<bool>();
            if (data.TryGetValue("layer", out var layerTok))
                obj.layer = ParseEnum<LabelsManager.ELayerLabel>(layerTok.Value<string>());

            if (data.TryGetValue("tutorialOnForThisObjective", out var tutTok))
                obj.tutorialOnForThisObjective = tutTok.Value<bool>();
            if (data.TryGetValue("showShortTutorial", out var sstTok))
                obj.showShortTutorial = sstTok.Value<bool>();
            if (data.TryGetValue("idTextShortTutorial", out var itstTok))
                obj.idTextShortTutorial = itstTok.Value<string>();
            if (data.TryGetValue("showDragAndDrop", out var sddTok))
                obj.showDragAndDrop = sddTok.Value<bool>();
            if (data.TryGetValue("fake", out var fakeTok))
                obj.fake = fakeTok.Value<bool>();
            if (data.TryGetValue("thisHelpObjective", out var thoTok))
                obj.thisHelpObjective = thoTok.Value<bool>();

            return obj;
        }

        // ── Reward building ───────────────────────────────────────────────────

        static List<Reward> BuildRewardList(JArray arr, string contractId, AllScriptableObjectManager allSO)
        {
            var list = new List<Reward>();
            foreach (var item in arr)
            {
                if (item.Type != JTokenType.Object) continue;
                try
                {
                    var reward = BuildReward((JObject)item, contractId, allSO);
                    if (reward != null) list.Add(reward);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"{Prefix} {contractId}: reward build failed — {ex.Message}");
                }
            }
            return list;
        }

        static Reward BuildReward(JObject data, string contractId, AllScriptableObjectManager allSO)
        {
            var reward = new Reward();

            if (data.TryGetValue("rewardType", out var rtTok))
                reward.rewardType = ParseEnum<EReward>(rtTok.Value<string>());

            if (data.TryGetValue("amount", out var amtTok))
                reward.amount = amtTok.Value<int>();

            if (data.TryGetValue("resource", out var resTok) && resTok.Type == JTokenType.String)
                reward.resourceDefinition = allSO.AllResourceDefinitions.GetByID(resTok.Value<string>());

            if (data.TryGetValue("facility", out var facTok) && facTok.Type == JTokenType.String)
                reward.facilityBaseDescriptor = allSO.AllFacility.GetByID(facTok.Value<string>());

            if (data.TryGetValue("spacecraft", out var scTok) && scTok.Type == JTokenType.String)
                reward.spaceCraftType = allSO.AllSpacecraftType.GetByID(scTok.Value<string>());

            if (data.TryGetValue("launchVehicle", out var lvTok) && lvTok.Type == JTokenType.String)
                reward.launchVehicleType = allSO.AllLaunchVehicleType.GetByID(lvTok.Value<string>());

            if (data.TryGetValue("buildImmediately", out var biTok))
                reward.buildImmediately = biTok.Value<bool>();

            if (data.TryGetValue("unlock", out var unlockTok) && unlockTok.Type == JTokenType.Object)
                reward.unlockData = BuildUnlockData((JObject)unlockTok, contractId, allSO);

            if (data.TryGetValue("aiOnly", out var aoTok) && aoTok.Value<bool>())
                AiOnlyRewards.Add(reward);

            return reward;
        }

        static UnlockData BuildUnlockData(JObject data, string contractId, AllScriptableObjectManager allSO)
        {
            var ud = new UnlockData();

            if (data.TryGetValue("action", out var actTok))
                ud.actionUnlock = ParseEnum<EActionUnlock>(actTok.Value<string>());

            if (data.TryGetValue("id", out var idTok))
                ud.parameter1 = idTok.Value<string>();
            if (data.TryGetValue("parameter2", out var p2Tok))
                ud.parameter2 = p2Tok.Value<string>();

            if (data.TryGetValue("bonus", out var bonusTok))
                ud.bonus = ParseEnum<EBonus>(bonusTok.Value<string>());
            if (data.TryGetValue("bonusParameter", out var bpTok))
                ud.bonusParameter = bpTok.Value<float>();

            if (data.TryGetValue("targets", out var targetsTok) && targetsTok.Type == JTokenType.Array)
                ud.id_ComponentOrOther = ((JArray)targetsTok)
                    .Select(x => x.Value<string>())
                    .Where(x => !string.IsNullOrEmpty(x)).ToArray();

            if (data.TryGetValue("unlockContractAdvance", out var ucaTok))
                ud.unlockContractAdvance = ParseEnum<UnlockData.EUnlockContractAdvance>(ucaTok.Value<string>());

            return ud;
        }

        // ── Translations ──────────────────────────────────────────────────────

        internal static void InjectTranslations(string id, Dictionary<string, JToken> fields)
        {
            string title = null, description = null, descriptionEnd = null;
            JToken tok;
            if (fields.TryGetValue("title", out tok) && tok.Type == JTokenType.String)
                title = tok.Value<string>();
            if (fields.TryGetValue("description", out tok) && tok.Type == JTokenType.String)
                description = tok.Value<string>();
            if (fields.TryGetValue("descriptionEnd", out tok) && tok.Type == JTokenType.String)
                descriptionEnd = tok.Value<string>();

            if (title == null && description == null && descriptionEnd == null) return;

            try
            {
                var le = MonoBehaviourSingleton<LEManager>.Instance;
                var leType = typeof(LEManager);
                object currentLang = FacilityCreator.GetFieldValueByNames(le, leType, FacilityCreator.CurrentLangCandidates);
                object defaultLang = FacilityCreator.GetFieldValueByNames(le, leType, FacilityCreator.DefaultLangCandidates);
                int injected = 0;
                foreach (var lang in new[] { currentLang, defaultLang })
                {
                    if (lang == null) { Plugin.Log.LogDebug($"{Prefix} {id}: lang object is null"); continue; }
                    var dictFI = lang.GetType().GetField("translations", BindingFlags.Public | BindingFlags.Instance);
                    if (dictFI == null) { Plugin.Log.LogDebug($"{Prefix} {id}: 'translations' field not found on {lang.GetType().Name}"); continue; }
                    if (!(dictFI.GetValue(lang) is Dictionary<string, string> dict)) { Plugin.Log.LogDebug($"{Prefix} {id}: translations field is not Dict<string,string>"); continue; }

                    if (!string.IsNullOrEmpty(title))
                        dict[id + "_Title"] = title;
                    if (!string.IsNullOrEmpty(description))
                        dict[id + "_fluff"] = description;
                    if (!string.IsNullOrEmpty(descriptionEnd))
                        dict[id + "_fluffEnd"] = descriptionEnd;
                    injected++;
                }

                le?.InvokeTranslationChanged();
                Plugin.Log.LogInfo($"{Prefix} {id}: translations injected into {injected} language(s) (title={title != null}, desc={description != null}, end={descriptionEnd != null})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"{Prefix} {id}: translation injection failed — {ex.Message}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static int ResolveObjectId(JToken tok)
        {
            if (tok.Type == JTokenType.Integer)
                return tok.Value<int>();

            if (tok.Type == JTokenType.String)
            {
                string val = tok.Value<string>();
                if (int.TryParse(val, out int numeric))
                    return numeric;

                var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
                if (oim != null)
                {
                    foreach (var oi in oim.allObjectInfos)
                    {
                        if (string.Equals(oi.ObjectName, val, StringComparison.OrdinalIgnoreCase))
                            return oi.id;
                    }
                }
                Plugin.Log.LogWarning($"{Prefix} Could not resolve object name '{val}' to ID");
            }

            return -1;
        }

        static MyIDScriptableObject ResolveScriptableObject(string soId, AllScriptableObjectManager allSO)
        {
            if (string.IsNullOrEmpty(soId)) return null;

            MyIDScriptableObject result = allSO.AllFacility.GetByID(soId);
            if (result != null) return result;
            result = allSO.AllResourceDefinitions.GetByID(soId);
            if (result != null) return result;
            result = allSO.AllSpacecraftType.GetByID(soId);
            if (result != null) return result;
            result = allSO.AllLaunchVehicleType.GetByID(soId);
            if (result != null) return result;
            result = allSO.AllResearchDefinition.GetByID(soId);
            if (result != null) return result;

            Plugin.Log.LogWarning($"{Prefix} Could not resolve SO '{soId}'");
            return null;
        }

        static T ParseEnum<T>(string value) where T : struct
        {
            if (Enum.TryParse(value, true, out T result))
                return result;
            Plugin.Log.LogWarning($"{Prefix} Unknown enum value '{value}' for {typeof(T).Name}");
            return default;
        }

        static List<T> MergeList<T>(List<T> existing, List<T> additions)
        {
            if (existing == null || existing.Count == 0) return additions;
            var merged = new List<T>(existing);
            merged.AddRange(additions);
            return merged;
        }

        static void SetField(object target, string fieldName, object value)
        {
            var fi = target.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (fi != null) fi.SetValue(target, value);
        }
    }
}
