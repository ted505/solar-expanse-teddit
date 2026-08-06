using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Teddit
{
    /// <summary>
    /// Extends CorporationSelectUI.Awake() to:
    ///  1. Add a toggle button for every CompanyDefinition with ONInMenu=true that is not
    ///     already present in the scene-baked corporationItems list.
    ///  2. Wrap the toggle container in a ScrollRect so the panel keeps its original size
    ///     while the player can scroll through any number of corporation entries.
    /// </summary>
    [HarmonyPatch(typeof(CorporationSelectUI), "Awake")]
    static class PatchCorporationSelectUIAwake
    {
        // ── Reflection setup (runs once at type-load) ────────────────────────
        static readonly Type      _uiType    = typeof(CorporationSelectUI);
        static readonly Type      _itemType  = _uiType.GetNestedType("CorporationItem", BindingFlags.NonPublic);
        static readonly FieldInfo _itemsFi   = _uiType.GetField("corporationItems",    BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _companyFi = _itemType?.GetField("companyDefinition", BindingFlags.Public   | BindingFlags.Instance);
        static readonly FieldInfo _toggleFi  = _itemType?.GetField("toggle",            BindingFlags.Public   | BindingFlags.Instance);
        static readonly FieldInfo _transFi   = _itemType?.GetField("translationId",     BindingFlags.Public   | BindingFlags.Instance);
        static readonly FieldInfo _selFi     = _uiType.GetField("selectedCompany",     BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _nameFi    = _uiType.GetField("txtName",             BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _descFi    = _uiType.GetField("txtDescription",      BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _traitsFi  = _uiType.GetField("txtTraitsList",       BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _onSelFi   = _uiType.GetField("OnCompanySelected",   BindingFlags.Public    | BindingFlags.Instance);

        static void Postfix(CorporationSelectUI __instance)
        {
            try
            {
                if (_itemType == null || _itemsFi == null || _companyFi == null ||
                    _toggleFi == null || _transFi == null)
                {
                    Plugin.Log.LogWarning("[CorpSelect] Reflection setup incomplete — patch skipped.");
                    return;
                }

                var items = _itemsFi.GetValue(__instance) as IList;
                if (items == null || items.Count == 0)
                {
                    Plugin.Log.LogWarning("[CorpSelect] corporationItems is null or empty.");
                    return;
                }

                // ── 1. Inject toggles for new companies ───────────────────────
                var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
                if (allSO != null)
                {
                    // Collect definitions that already have an entry.
                    var existing = new HashSet<CompanyDefinition>();
                    foreach (var item in items)
                        if (_companyFi.GetValue(item) is CompanyDefinition cd)
                            existing.Add(cd);

                    // Last item's toggle parent is the toggle container.
                    var lastItem   = items[items.Count - 1];
                    var lastToggle = _toggleFi.GetValue(lastItem) as Toggle;
                    Transform container = lastToggle?.transform.parent;

                    if (container != null)
                    {
                        foreach (var def in allSO.AllCompanyDefinitions.ListNotEmpty)
                        {
                            if (!def.ONInMenu || existing.Contains(def)) continue;

                            // Clone the last toggle GO into the same parent.
                            var newGO     = UnityEngine.Object.Instantiate(lastToggle.gameObject, container);
                            var newToggle = newGO.GetComponent<Toggle>();

                            // Remove any listeners copied from the source and add ours.
                            newToggle.onValueChanged.RemoveAllListeners();
                            var capturedDef = def;
                            var capturedUI  = __instance;
                            newToggle.onValueChanged.AddListener(delegate(bool isOn)
                            {
                                if (isOn) InvokeSetSelected(capturedUI, capturedDef, capturedDef.ID);
                            });

                            // Register in the list so the base class is aware of the new entry.
                            var newItem = Activator.CreateInstance(_itemType);
                            _companyFi.SetValue(newItem, def);
                            _toggleFi.SetValue(newItem, newToggle);
                            _transFi.SetValue(newItem, def.ID);
                            items.Add(newItem);

                            Plugin.Log.LogInfo($"[CorpSelect] Injected toggle for '{def.ID}'.");
                        }
                    }
                }

                // ── 2. Add scroll rect around the toggle container ────────────
                var firstToggle = _toggleFi.GetValue(items[0]) as Toggle;
                if (firstToggle == null) return;

                Transform toggleContainer = firstToggle.transform.parent;
                if (toggleContainer == null) return;

                // Guard against being called twice (scene reload, etc.).
                if (toggleContainer.parent != null &&
                    toggleContainer.parent.name == "TedditCorpScrollViewport")
                    return;

                Transform panelParent = toggleContainer.parent;
                if (panelParent == null) return;

                var containerRect = toggleContainer.GetComponent<RectTransform>();

                // Create the viewport that will clip the list.
                var viewportGO   = new GameObject("TedditCorpScrollViewport");
                viewportGO.transform.SetParent(panelParent, false);
                viewportGO.transform.SetSiblingIndex(containerRect.GetSiblingIndex());

                var viewportRect = viewportGO.AddComponent<RectTransform>();
                viewportRect.anchorMin        = containerRect.anchorMin;
                viewportRect.anchorMax        = containerRect.anchorMax;
                viewportRect.pivot            = containerRect.pivot;
                viewportRect.anchoredPosition = containerRect.anchoredPosition;
                viewportRect.sizeDelta        = containerRect.sizeDelta;

                viewportGO.AddComponent<RectMask2D>();

                // Reparent the toggle container into the viewport.
                toggleContainer.SetParent(viewportGO.transform, false);

                // Anchor container to the top of the viewport so it expands downward.
                containerRect.anchorMin        = new Vector2(0f, 1f);
                containerRect.anchorMax        = new Vector2(1f, 1f);
                containerRect.pivot            = new Vector2(0.5f, 1f);
                containerRect.anchoredPosition = Vector2.zero;
                containerRect.sizeDelta        = new Vector2(0f, containerRect.sizeDelta.y);

                // Let the container grow to fit all its children.
                var csf = toggleContainer.GetComponent<ContentSizeFitter>()
                       ?? toggleContainer.gameObject.AddComponent<ContentSizeFitter>();
                csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

                // Wire up the ScrollRect.
                var sr = viewportGO.AddComponent<ScrollRect>();
                sr.content           = containerRect;
                sr.viewport          = viewportRect;
                sr.horizontal        = false;
                sr.vertical          = true;
                sr.movementType      = ScrollRect.MovementType.Clamped;
                sr.scrollSensitivity = 30f;
                sr.inertia           = false;

                // ── Vertical scrollbar ────────────────────────────────────────
                const float SbWidth = 12f;

                var sbGO = new GameObject("TedditCorpScrollbar");
                sbGO.transform.SetParent(viewportGO.transform, false);

                var sbRect = sbGO.AddComponent<RectTransform>();
                sbRect.anchorMin        = new Vector2(1f, 0f);
                sbRect.anchorMax        = new Vector2(1f, 1f);
                sbRect.pivot            = new Vector2(1f, 0.5f);
                sbRect.anchoredPosition = new Vector2(SbWidth, 0f);
                sbRect.sizeDelta        = new Vector2(SbWidth, 0f);

                var sbBg = sbGO.AddComponent<Image>();
                sbBg.color = new Color(0.1f, 0.1f, 0.1f, 0.6f);

                var slideGO   = new GameObject("Sliding Area");
                slideGO.transform.SetParent(sbGO.transform, false);
                var slideRect = slideGO.AddComponent<RectTransform>();
                slideRect.anchorMin        = Vector2.zero;
                slideRect.anchorMax        = Vector2.one;
                slideRect.pivot            = new Vector2(0.5f, 0.5f);
                slideRect.anchoredPosition = Vector2.zero;
                slideRect.sizeDelta        = new Vector2(-SbWidth, -SbWidth);

                var handleGO   = new GameObject("Handle");
                handleGO.transform.SetParent(slideGO.transform, false);
                var handleRect = handleGO.AddComponent<RectTransform>();
                handleRect.anchorMin        = Vector2.zero;
                handleRect.anchorMax        = Vector2.one;
                handleRect.pivot            = new Vector2(0.5f, 0.5f);
                handleRect.anchoredPosition = Vector2.zero;
                handleRect.sizeDelta        = new Vector2(SbWidth, SbWidth);

                var handleImg = handleGO.AddComponent<Image>();
                handleImg.color = new Color(0.8f, 0.8f, 0.8f, 0.85f);

                var sb = sbGO.AddComponent<Scrollbar>();
                sb.handleRect = handleRect;
                sb.direction  = Scrollbar.Direction.BottomToTop;

                sr.verticalScrollbar           = sb;
                sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                sr.verticalScrollbarSpacing    = 2f;

                Plugin.Log.LogInfo("[CorpSelect] ScrollRect + Scrollbar injected around corporation toggle list.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CorpSelect] Postfix crashed: {ex}");
            }
        }

        /// <summary>Mirrors the private CorporationSelectUI.SetSelectedCorporation() logic.</summary>
        static void InvokeSetSelected(CorporationSelectUI ui, CompanyDefinition def, string translationId)
        {
            _selFi?.SetValue(ui, def);

            if (_nameFi?.GetValue(ui) is TMP_Text txtName)
                txtName.text = Language.LEManager.Get(
                    $"Game.UI.CustomizationScreen.CorporationInfo.{translationId}.Name");

            if (_descFi?.GetValue(ui) is TMP_Text txtDesc)
                txtDesc.text = Language.LEManager.Get(
                    $"Game.UI.CustomizationScreen.CorporationInfo.{translationId}.Description");

            if (_traitsFi?.GetValue(ui) is TMP_Text txtTraits)
                txtTraits.text = Language.LEManager.Get(
                    $"Game.UI.CustomizationScreen.CorporationInfo.{translationId}.TraitsList");

            (_onSelFi?.GetValue(ui) as Action<CompanyDefinition>)?.Invoke(def);
        }
    }
}
