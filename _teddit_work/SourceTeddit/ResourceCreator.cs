using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Data.ScriptableObject;
using Extensions;
using Language;
using Manager;
using Newtonsoft.Json.Linq;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    internal static class ResourceCreator
    {
        static readonly HashSet<string> _namedSpriteResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool UsesNamedSprite(ResourceDefinition resource)
        {
            return resource != null && _namedSpriteResourceIds.Contains(resource.ID);
        }

        internal static void MarkNamedSprite(ResourceDefinition resource)
        {
            if (resource == null || string.IsNullOrEmpty(resource.ID)) return;
            _namedSpriteResourceIds.Add(resource.ID);
            if (resource.Sprite != null)
                FacilityCreator.EnsureSpriteRegisteredWithTMP(resource.Sprite, resource.Sprite.name);
        }

        internal static string GetIconString(ResourceDefinition resource)
        {
            if (resource == null) return "";
            if (UsesNamedSprite(resource) && !string.IsNullOrEmpty(resource.Sprite?.name))
                return $"<sprite name=\"{resource.Sprite.name}\" color=white>";
            return $"<sprite index={resource.IdSpritAttlastextMeshPro} color=white>";
        }

        internal static string GetIconWithLinkString(ResourceDefinition resource)
        {
            if (resource == null) return "";
            if (UsesNamedSprite(resource) && !string.IsNullOrEmpty(resource.Sprite?.name))
                return $"<link=\"ResourceDefinition:{resource.ID}\"><sprite name=\"{resource.Sprite.name}\" color=white></link>";
            return $"<link=\"ResourceDefinition:{resource.ID}\"><sprite index={resource.IdSpritAttlastextMeshPro} color=white></link>";
        }

        internal static string FormatResourceLink(ResourceDefinition resource, bool longText, bool firstIcon, string highLightColor, bool colorIcon, string highLightColorSprite, bool addSpace)
        {
            if (resource == null) return "";

            string text = highLightColorSprite ?? MonoBehaviourSingleton<ObjectInfoManager>.Instance.highLightTextStartSprite;
            string text2 = highLightColor ?? MonoBehaviourSingleton<ObjectInfoManager>.Instance.highLightTextStart;
            string icon = colorIcon
                ? $"<sprite name=\"{resource.Sprite?.name}\" color={text}>"
                : $"<sprite name=\"{resource.Sprite?.name}\">";
            string label = LEManager.Get(resource.ID);
            string prefixSpace = addSpace ? " " : "";
            if (!longText)
                return $"<link=\"ResourceDefinition:{resource.ID}\">{icon}</link>";

            if (firstIcon)
                return $"<link=\"ResourceDefinition:{resource.ID}\">{icon}{text2}{prefixSpace}{label}{MonoBehaviourSingleton<ObjectInfoManager>.Instance.highLightTextEnd}</link>";

            return $"<link=\"ResourceDefinition:{resource.ID}\">{text2}{label}{MonoBehaviourSingleton<ObjectInfoManager>.Instance.highLightTextEnd}{icon}</link>";
        }

        internal static string FormatResourceAmount(ResourceDefinition resource, double value)
        {
            return FormatResourceAmount(resource, value, 1.0);
        }

        internal static string FormatResourceAmount(ResourceDefinition resource, double value, double multiplier)
        {
            if (resource == null) return "";
            string format;
            switch (resource.ResourceType)
            {
                case ResourceDefinition.EResourceType.Energy:
                    format = LEManager.Get("UI.EnergyFormat");
                    break;
                case ResourceDefinition.EResourceType.Human:
                    format = "{0}";
                    break;
                default:
                    format = LEManager.Get("UI.MassFormat");
                    break;
            }
            value *= multiplier;
            string valueString = value.ToPostfixString(format, gray: false, resource.ResourceType == ResourceDefinition.EResourceType.Human);
            return $"<nobr><link=\"ResourceDefinition:{resource.ID}\">{valueString} {GetIconString(resource)}</link></nobr>";
        }

        internal static void CreateAndInjectResource(string id, Dictionary<string, JToken> def, string modDir)
        {
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            var donor = FindDonorResource(def, allSO.AllResourceDefinitions, id);
            var res = donor != null
                ? UnityEngine.Object.Instantiate(donor)
                : ScriptableObject.CreateInstance<ResourceDefinition>();
            res.name = id;
            FacilityCreator.SetField(res, "id", id);
            FacilityCreator.SetField(res, "toSortMarketOffer", FacilityCreator.GetVal<int>(def, "toSortMarketOffer", 999));
            FacilityCreator.SetField(res, "marketClearingPriceBase", FacilityCreator.GetVal<double>(def, "marketClearingPriceBase", 100.0));
            FacilityCreator.SetField(res, "priceChangeMultiplier", FacilityCreator.GetVal<double>(def, "priceChangeMultiplier", 1.0));
            FacilityCreator.SetField(res, "startingSummedPreviousTradesQuantity", FacilityCreator.GetVal<float>(def, "startingSummedPreviousTradesQuantity", 10000f));
            FacilityCreator.SetField(res, "startingPreviousTradesCount", FacilityCreator.GetVal<float>(def, "startingPreviousTradesCount", 10f));
            FacilityCreator.SetField(res, "showOnUI", FacilityCreator.GetVal<bool>(def, "showOnUI", true));
            FacilityCreator.SetField(res, "canBeLeftOnObject", FacilityCreator.GetVal<bool>(def, "canBeLeftOnObject", true));
            FacilityCreator.SetField(res, "idSpritAttlastextMeshPro", -1);

            string resourceTypeStr = FacilityCreator.GetVal<string>(def, "resourceType", "Normal");
            ResourceDefinition.EResourceType resourceType;
            if (!Enum.TryParse(resourceTypeStr, true, out resourceType))
                resourceType = ResourceDefinition.EResourceType.Normal;
            FacilityCreator.SetField(res, "resourceType", resourceType);

            Sprite sprite = ResolveResourceSprite(def, modDir, id, allSO.AllResourceDefinitions.List.FirstOrDefault(x => x != null)?.Sprite);
            if (sprite != null)
            {
                sprite.name = id;
                FacilityCreator.SetField(res, "sprite2", sprite);
                FacilityCreator.EnsureSpriteRegisteredWithTMP(sprite, sprite.name);
            }

            string name = FacilityCreator.GetVal<string>(def, "name", null);
            string description = FacilityCreator.GetVal<string>(def, "description", null);
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(description))
                FacilityCreator.InjectTranslations(id, name, description);

            InjectIntoResourceCollection(allSO.AllResourceDefinitions, res, FacilityCreator.GetVal<bool>(def, "showInMarket", resourceType == ResourceDefinition.EResourceType.Normal && res.ShowOnUI));
            MarkNamedSprite(res);
            RefreshAllObjectInfoDataRows();
            Plugin.Log.LogInfo($"[ResourceCreator] + {id} ({resourceType})");
        }

        internal static void ApplyComplexFields(ResourceDefinition resource, Dictionary<string, JToken> fields, string id, string modDir)
        {
            bool changedSprite = false;
            if (fields.ContainsKey("icon") || fields.ContainsKey("iconRef"))
            {
                var sprite = ResolveResourceSprite(fields, modDir, id, resource.Sprite);
                if (sprite != null)
                {
                    sprite.name = id;
                    FacilityCreator.SetField(resource, "sprite2", sprite);
                    FacilityCreator.EnsureSpriteRegisteredWithTMP(sprite, sprite.name);
                    changedSprite = true;
                }
            }

            JToken tok;
            if (fields.TryGetValue("name", out tok) || fields.TryGetValue("description", out tok))
            {
                string name = fields.ContainsKey("name") && fields["name"].Type != JTokenType.Null ? fields["name"].Value<string>() : null;
                string description = fields.ContainsKey("description") && fields["description"].Type != JTokenType.Null ? fields["description"].Value<string>() : null;
                if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(description))
                    FacilityCreator.InjectTranslations(id, name, description);
            }

            if (fields.TryGetValue("terraformationInfo", out tok) && tok.Type == JTokenType.Object)
                ApplyTerraformationInfo(resource, (JObject)tok);

            if (fields.TryGetValue("toxicityCurve", out tok) && tok.Type == JTokenType.Object)
                ApplyToxicityCurve(resource, (JObject)tok, id);

            var allResources = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllResourceDefinitions;
            bool showInMarket = fields.ContainsKey("showInMarket")
                ? fields["showInMarket"].Value<bool>()
                : allResources.ListResourceDefinitionInMarketPlaceOffer.Contains(resource);
            RefreshResourceLists(allResources, resource, showInMarket);

            if (changedSprite || fields.ContainsKey("iconRef") || fields.ContainsKey("icon"))
                MarkNamedSprite(resource);

            RefreshAllObjectInfoDataRows();
        }

        internal static void RefreshAllResourceLists(AllResourceDefinitions allResources)
        {
            if (allResources == null) return;
            typeof(AllResourceDefinitions)
                .GetMethod("UpdateList", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(allResources, null);
        }

        internal static void RefreshAllObjectInfoDataRows()
        {
            var gm = MonoBehaviourSingleton<GameManager>.Instance;
            var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (gm == null || oim == null || gm.Companies == null || oim.allObjectInfos == null)
                return;

            foreach (var objectInfo in oim.allObjectInfos)
            {
                if (objectInfo == null) continue;
                foreach (var company in gm.Companies)
                {
                    if (company == null) continue;
                    try
                    {
                        var oid = objectInfo.GetObjectInfoData(company);
                        oid?.FillResourcesRows();
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[ResourceCreator] FillResourcesRows failed for {objectInfo.ObjectName}/{company.ID}: {ex.Message}");
                    }
                }
            }
        }

        static void InjectIntoResourceCollection(AllResourceDefinitions allResources, ResourceDefinition resource, bool showInMarket)
        {
            if (allResources == null || resource == null) return;
            var listFi = typeof(AllScriptableObject<ResourceDefinition>).GetField("list", BindingFlags.NonPublic | BindingFlags.Instance);
            var listNotEmptyFi = typeof(AllScriptableObject<ResourceDefinition>).GetField("listNotEmpty", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = listFi?.GetValue(allResources) as List<ResourceDefinition>;
            var listNotEmpty = listNotEmptyFi?.GetValue(allResources) as List<ResourceDefinition>;
            if (list != null && !list.Contains(resource)) list.Add(resource);
            if (listNotEmpty != null && !listNotEmpty.Contains(resource)) listNotEmpty.Add(resource);

            RefreshResourceLists(allResources, resource, showInMarket);

            try { SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllMyIDScriptableObjects.Add(resource); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[ResourceCreator] AllMyIDScriptableObjects.Add failed: {ex.Message}"); }
        }

        static void RefreshResourceLists(AllResourceDefinitions allResources, ResourceDefinition resource, bool showInMarket)
        {
            var marketFi = typeof(AllResourceDefinitions).GetField("listResourceDefinitionInMarketPlaceOffer", BindingFlags.NonPublic | BindingFlags.Instance);
            var marketList = marketFi?.GetValue(allResources) as List<ResourceDefinition>;
            if (marketList != null)
            {
                marketList.RemoveAll(r => r != null && r.ID == resource.ID);
                if (showInMarket)
                    marketList.Add(resource);
            }

            RefreshAllResourceLists(allResources);
        }

        static Sprite ResolveResourceSprite(Dictionary<string, JToken> def, string modDir, string id, Sprite fallback)
        {
            string iconRef = FacilityCreator.GetVal<string>(def, "iconRef", null);
            if (!string.IsNullOrEmpty(iconRef))
            {
                var resSprite = FindResourceSprite(iconRef);
                if (resSprite != null)
                    return CloneSprite(resSprite, id);

                var referenced = FacilityCreator.FindBaseGameSprite(iconRef);
                if (referenced != null)
                    return CloneSprite(referenced, id);

                Plugin.Log.LogWarning($"[ResourceCreator] Base-game iconRef not found: {iconRef}");
            }

            string iconPath = FacilityCreator.GetVal<string>(def, "icon", null);
            if (!string.IsNullOrEmpty(iconPath))
            {
                string iconFull = System.IO.Path.IsPathRooted(iconPath) || string.IsNullOrEmpty(modDir)
                    ? iconPath
                    : System.IO.Path.Combine(modDir, iconPath);
                var loaded = FacilityCreator.LoadSprite(iconFull, id);
                if (loaded != null)
                    return loaded;
            }

            return fallback;
        }

        static void ApplyTerraformationInfo(ResourceDefinition resource, JObject obj)
        {
            var current = resource.TerraformationInfo;
            current.resourceOpticalDepthParameter = GetDouble(obj, "resourceOpticalDepthParameter", current.resourceOpticalDepthParameter);
            current.resourceHeatCapacity = GetDouble(obj, "resourceHeatCapacity", current.resourceHeatCapacity);
            current.vaporizationLatentHeat = GetDouble(obj, "vaporizationLatentHeat", current.vaporizationLatentHeat);
            current.baseTemperatureBoiling = GetDouble(obj, "baseTemperatureBoiling", current.baseTemperatureBoiling);
            current.temperatureMelting = GetDouble(obj, "temperatureMelting", current.temperatureMelting);
            current.pressureTriplePoint = GetDouble(obj, "pressureTriplePoint", current.pressureTriplePoint);
            FacilityCreator.SetField(resource, "terraformationInfo", current);
        }

        static void ApplyToxicityCurve(ResourceDefinition resource, JObject obj, string id)
        {
            var keysTok = obj["keys"] as JArray;
            if (keysTok == null || keysTok.Count == 0)
            {
                Plugin.Log.LogWarning($"[ResourceCreator] {id}: toxicityCurve.keys missing or empty");
                return;
            }

            var keys = new Keyframe[keysTok.Count];
            for (int i = 0; i < keysTok.Count; i++)
            {
                var keyObj = keysTok[i] as JObject;
                if (keyObj == null)
                    continue;

                var key = new Keyframe(
                    keyObj["time"]?.Value<float>() ?? 0f,
                    keyObj["value"]?.Value<float>() ?? 0f,
                    keyObj["inTangent"]?.Value<float>() ?? 0f,
                    keyObj["outTangent"]?.Value<float>() ?? 0f
                );

                if (keyObj.TryGetValue("weightedMode", StringComparison.OrdinalIgnoreCase, out var weightedTok))
                {
                    try
                    {
                        key.weightedMode = (WeightedMode)Enum.Parse(typeof(WeightedMode), weightedTok.Value<string>(), true);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[ResourceCreator] {id}: invalid toxicityCurve weightedMode '{weightedTok}': {ex.Message}");
                    }
                }

                if (keyObj["inWeight"] != null)
                    key.inWeight = keyObj["inWeight"].Value<float>();
                if (keyObj["outWeight"] != null)
                    key.outWeight = keyObj["outWeight"].Value<float>();

                keys[i] = key;
            }

            var curve = new AnimationCurve(keys);
            if (obj.TryGetValue("preWrapMode", StringComparison.OrdinalIgnoreCase, out var preWrapTok))
            {
                try { curve.preWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), preWrapTok.Value<string>(), true); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[ResourceCreator] {id}: invalid toxicityCurve preWrapMode '{preWrapTok}': {ex.Message}"); }
            }
            if (obj.TryGetValue("postWrapMode", StringComparison.OrdinalIgnoreCase, out var postWrapTok))
            {
                try { curve.postWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), postWrapTok.Value<string>(), true); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[ResourceCreator] {id}: invalid toxicityCurve postWrapMode '{postWrapTok}': {ex.Message}"); }
            }

            FacilityCreator.SetField(resource, "toxicityCurve", curve);
        }

        static double GetDouble(JObject obj, string key, double fallback)
        {
            return obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var tok) && tok.Type != JTokenType.Null
                ? tok.Value<double>()
                : fallback;
        }

        static ResourceDefinition FindDonorResource(Dictionary<string, JToken> def, AllResourceDefinitions allResources, string id)
        {
            string cloneFrom = FacilityCreator.GetVal<string>(def, "cloneFrom", null);
            if (!string.IsNullOrEmpty(cloneFrom))
            {
                var explicitDonor = allResources?.GetByID(cloneFrom);
                if (explicitDonor == null)
                    Plugin.Log.LogWarning($"[ResourceCreator] {id}: cloneFrom resource '{cloneFrom}' not found.");
                else
                    return explicitDonor;
            }

            if (allResources?.List == null)
                return null;

            foreach (var resource in allResources.List)
            {
                if (resource == null)
                    continue;
                if (resource.ToxicityCurve == null)
                    continue;
                return resource;
            }

            return allResources.List.FirstOrDefault(x => x != null);
        }

        static Sprite FindResourceSprite(string spriteRef)
        {
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO?.AllResourceDefinitions?.List == null) return null;
            foreach (var resource in allSO.AllResourceDefinitions.List)
            {
                var sprite = resource?.Sprite;
                if (sprite != null && string.Equals(sprite.name, spriteRef, StringComparison.OrdinalIgnoreCase))
                    return sprite;
            }
            return null;
        }

        static Sprite CloneSprite(Sprite source, string newName)
        {
            if (source == null) return null;
            try
            {
                Rect rect = source.rect;
                Texture2D sourceTexture = source.texture;
                int width = Mathf.RoundToInt(rect.width);
                int height = Mathf.RoundToInt(rect.height);
                var tex = new Texture2D(width, height, TextureFormat.ARGB32, mipChain: false);
                tex.name = newName;

                try
                {
                    tex.SetPixels(sourceTexture.GetPixels(Mathf.RoundToInt(rect.x), Mathf.RoundToInt(rect.y), width, height));
                    tex.Apply();
                }
                catch
                {
                    var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                    var prev = RenderTexture.active;
                    Graphics.Blit(sourceTexture, rt);
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(rect.x, rect.y, width, height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }

                var sprite = Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), source.pixelsPerUnit, 0u, SpriteMeshType.FullRect);
                sprite.name = newName;
                return sprite;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ResourceCreator] Failed to clone sprite '{source.name}' for {newName}: {ex.Message}");
                return source;
            }
        }
    }
}
