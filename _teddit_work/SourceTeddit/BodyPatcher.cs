using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Info;
using Manager;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Patches celestial bodies at runtime via bodies.yaml.
    ///
    /// Keys are the exact body name as it appears in dump/bodies.yaml.
    ///
    /// ── Fields ───────────────────────────────────────────────────────────────────
    ///   objectTypes:              enum   — Planet / Moons / Asteroid / DwarfPlanet /
    ///                                      Comet / Orbit / Belt / Other
    ///                                      Controls mission planner routing and landing
    ///                                      rules. Set to Asteroid to allow direct
    ///                                      surface access without an orbital approach.
    ///   objectSubTypeId:          string — ID of the ObjectSubType scriptable object
    ///                                      (from dump/bodies.yaml). Determines mining
    ///                                      slot templates and resource maps.
    ///                                      Use "" to clear the subtype.
    ///   removeOrbit:              bool   — if true, removes the "{name} [ORBIT]"
    ///                                      companion body from allObjectInfos so the
    ///                                      game no longer generates orbital approach
    ///                                      missions for this body.
    ///   removeFromParentMoonList: bool   — if true, also removes this body from its
    ///                                      parent body's moon list (mounsObjectInfo).
    ///                                      Defaults to false; the body stays visible
    ///                                      in the solar system hierarchy under its
    ///                                      parent even when objectTypes is changed.
    /// </summary>
    internal static class BodyPatcher
    {
        // ObjectInfo.objectTypes — public field, EObjectTypes enum
        static readonly FieldInfo _objectTypesFi = typeof(ObjectInfo)
            .GetField("objectTypes", BindingFlags.Public | BindingFlags.Instance);

        // ObjectInfo.SetObjectTypes(EObjectTypes) — preferred setter (may update internal state)
        static readonly MethodInfo _setObjectTypesMi = typeof(ObjectInfo)
            .GetMethod("SetObjectTypes", BindingFlags.Public | BindingFlags.Instance);

        // ObjectInfo.objectSubType — public field, ScriptableObjectScripts.ObjectSubType
        static readonly FieldInfo _objectSubTypeFi =
            ScriptableObjectPatcher.FindField(typeof(ObjectInfo), "objectSubType");

        // ObjectInfo.mounsObjectInfo — public field, List<ObjectInfo> (note: game typo "mouns")
        static readonly FieldInfo _moonListFi =
            ScriptableObjectPatcher.FindField(typeof(ObjectInfo), "mounsObjectInfo");

        // ObjectInfo.lowOrbitCustom — public field, NBody
        static readonly FieldInfo _lowOrbitCustomFi =
            ScriptableObjectPatcher.FindField(typeof(ObjectInfo), "lowOrbitCustom");

        // ObjectInfo.nBody — public field, NBody backing field for ObjectInfo.NBody
        static readonly FieldInfo _nBodyFi =
            ScriptableObjectPatcher.FindField(typeof(ObjectInfo), "nBody");

        // Bodies whose low orbit was intentionally removed this session.
        static readonly HashSet<int> _removedOrbitParentIds = new HashSet<int>();

        public static void ResetSessionState()
        {
            _removedOrbitParentIds.Clear();
        }

        public static bool BodyHasRemovedOrbit(ObjectInfo body)
        {
            return body != null && _removedOrbitParentIds.Contains(body.id);
        }

        public static bool TreatAsAsteroidLike(ObjectInfo body)
        {
            if (body == null)
                return false;

            string typeName = body.objectTypes.ToString();
            if (string.Equals(typeName, "Asteroid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "Comet", StringComparison.OrdinalIgnoreCase))
                return true;

            return BodyHasRemovedOrbit(body);
        }

        static void ClearLowOrbitReference(ObjectInfo body)
        {
            if (body == null || _lowOrbitCustomFi == null)
                return;

            try
            {
                _lowOrbitCustomFi.SetValue(body, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] Failed to clear lowOrbitCustom for '{body.ObjectName}': {ex.Message}");
            }
        }

        public static void Run(Dictionary<string, Dictionary<string, JToken>> config, ObjectInfoManager oi)
        {
            if (config.Count == 0) return;

            // Build name → ObjectInfo lookup (case-insensitive)
            var bodyLookup = new Dictionary<string, ObjectInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var body in oi.allObjectInfos)
                if (body != null && !string.IsNullOrEmpty(body.ObjectName))
                    bodyLookup[body.ObjectName] = body;

            // Collect all ObjectSubType SO instances from existing bodies, keyed by ID
            var subtypeLookup = BuildSubtypeLookup(oi);

            var orbitNamesToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int patched = 0, skipped = 0;

            foreach (var kv in config)
            {
                if (kv.Key.StartsWith("_")) continue;
                string bodyName = kv.Key;

                if (!bodyLookup.TryGetValue(bodyName, out var body))
                {
                    Plugin.Log.LogWarning($"[BodyPatcher] '{bodyName}' not found in allObjectInfos — skipping.");
                    skipped++;
                    continue;
                }

                var fields = kv.Value;
                JToken tok;

                // ── objectTypes ───────────────────────────────────────────────
                if (fields.TryGetValue("objectTypes", out tok) && tok.Type != JTokenType.Null)
                {
                    string typeStr = tok.Value<string>();
                    if (_objectTypesFi != null)
                    {
                        try
                        {
                            object enumVal = Enum.Parse(_objectTypesFi.FieldType, typeStr, ignoreCase: true);
                            // Use the game's setter method if available (may sync internal state)
                            if (_setObjectTypesMi != null)
                                _setObjectTypesMi.Invoke(body, new[] { enumVal });
                            else
                                _objectTypesFi.SetValue(body, enumVal);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[BodyPatcher] '{bodyName}': invalid objectTypes '{typeStr}' — {ex.Message}");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[BodyPatcher] '{bodyName}': objectTypes field not found on ObjectInfo.");
                    }
                }

                // ── objectSubTypeId ───────────────────────────────────────────
                if (fields.TryGetValue("objectSubTypeId", out tok) && tok.Type != JTokenType.Null)
                {
                    string subtypeId = tok.Value<string>() ?? "";
                    if (string.IsNullOrEmpty(subtypeId))
                    {
                        _objectSubTypeFi?.SetValue(body, null);
                    }
                    else if (subtypeLookup.TryGetValue(subtypeId, out var subtype))
                    {
                        _objectSubTypeFi?.SetValue(body, subtype);
                    }
                    else
                    {
                        Plugin.Log.LogWarning(
                            $"[BodyPatcher] '{bodyName}': objectSubTypeId '{subtypeId}' not found in any existing body — skipping field.");
                    }
                }

                // ── removeOrbit ───────────────────────────────────────────────
                if (fields.TryGetValue("removeOrbit", out tok) && tok.Type != JTokenType.Null && tok.Value<bool>())
                {
                    orbitNamesToRemove.Add(bodyName + " [ORBIT]");

                    if (!fields.ContainsKey("objectTypes"))
                        Plugin.Log.LogInfo($"[BodyPatcher] '{bodyName}': removeOrbit=true; keeping original objectTypes (no asteroid fallback).");
                }

                // ── removeFromParentMoonList ───────────────────────────────────
                if (fields.TryGetValue("removeFromParentMoonList", out tok) && tok.Type != JTokenType.Null && tok.Value<bool>())
                {
                    var parent = body.parentObjectInfo;
                    if (parent != null && _moonListFi != null)
                    {
                        var moonList = _moonListFi.GetValue(parent) as List<ObjectInfo>;
                        if (moonList != null)
                        {
                            int n = moonList.RemoveAll(m => m == body);
                            if (n > 0)
                                Plugin.Log.LogInfo($"[BodyPatcher] '{bodyName}' removed from '{parent.ObjectName}' moon list.");
                            else
                                Plugin.Log.LogWarning($"[BodyPatcher] '{bodyName}' not found in '{parent.ObjectName}' moon list.");
                        }
                    }
                    else if (parent == null)
                    {
                        Plugin.Log.LogWarning(
                            $"[BodyPatcher] '{bodyName}': removeFromParentMoonList=true but parentObjectInfo is null.");
                    }
                }

                patched++;
            }

            // ── Remove orbit companion bodies ─────────────────────────────────
            if (orbitNamesToRemove.Count > 0)
            {
                int removed = 0;
                foreach (var orbitBody in oi.allObjectInfos.ToArray())
                {
                    if (orbitBody == null || !orbitNamesToRemove.Contains(orbitBody.ObjectName))
                        continue;

                    // Remove from the parent body's mounsObjectInfo so it no longer
                    // appears as a child destination in the body info panel
                    var parent = orbitBody.parentObjectInfo;
                    if (parent != null && _moonListFi != null)
                    {
                        var moonList = _moonListFi.GetValue(parent) as List<ObjectInfo>;
                        moonList?.RemoveAll(m => m == orbitBody);
                    }

                    if (parent != null)
                    {
                        _removedOrbitParentIds.Add(parent.id);
                        ClearLowOrbitReference(parent);
                    }

                    // Do not destroy the underlying orbit scene objects here.
                    // The game still has a lot of cached references that may touch the
                    // orbit ObjectInfo/NBody later, and hard-destroying it tends to produce
                    // lazy NBody resolution nullrefs. Clearing the parent's live orbit hook
                    // (lowOrbitCustom), removing the orbit from allObjectInfos, and removing
                    // it from the parent's moon list is enough to make the orbit disappear
                    // from gameplay/UI routing.
                    var rawOrbitNBody = _nBodyFi?.GetValue(orbitBody) as Component;
                    if (rawOrbitNBody != null && rawOrbitNBody.gameObject != null)
                        rawOrbitNBody.gameObject.SetActive(false);
                    if (orbitBody.gameObject != null)
                        orbitBody.gameObject.SetActive(false);

                    removed++;
                }

                // Also purge from allObjectInfos
                oi.allObjectInfos.RemoveAll(b => b != null && orbitNamesToRemove.Contains(b.ObjectName));

                if (removed > 0)
                    Plugin.Log.LogInfo($"[BodyPatcher] Removed {removed} orbit bod{(removed == 1 ? "y" : "ies")}.");
                else
                    Plugin.Log.LogWarning($"[BodyPatcher] removeOrbit was requested but no matching [ORBIT] bodies were found.");
            }

            if (patched > 0 || skipped > 0)
                Plugin.Log.LogInfo($"[BodyPatcher] Done — patched: {patched}, skipped: {skipped}");
        }

        /// <summary>
        /// Builds a subtype lookup by scanning all existing ObjectInfo entries for their
        /// objectSubType reference and indexing by ID. This avoids needing a dedicated
        /// AllObjectSubType collection on AllScriptableObjectManager.
        /// </summary>
        static Dictionary<string, object> BuildSubtypeLookup(ObjectInfoManager oi)
        {
            var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (_objectSubTypeFi == null) return lookup;

            foreach (var body in oi.allObjectInfos)
            {
                if (body == null) continue;
                object sub = _objectSubTypeFi.GetValue(body);
                if (sub == null) continue;

                // ObjectSubType.ID is a public property
                var idPi = sub.GetType().GetProperty("ID",
                    BindingFlags.Public | BindingFlags.Instance);
                string id = idPi?.GetValue(sub) as string;

                if (!string.IsNullOrEmpty(id) && !lookup.ContainsKey(id))
                    lookup[id] = sub;
            }
            return lookup;
        }
    }
}
