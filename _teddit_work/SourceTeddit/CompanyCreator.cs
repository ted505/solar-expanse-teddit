using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Manager;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Creates entirely new Company entries from the <c>new_companies:</c> section of
    /// companies.yaml and injects them into the game through three ordered phases.
    ///
    /// ── Phase 1  InjectDefinitions()     AllScriptableObjectManager.InitializeSingleton [Postfix]
    ///    Creates CompanyDefinition ScriptableObjects and registers them in AllCompanyDefinitions
    ///    plus allMyIDScriptableObjects so the rest of the game can look them up by ID.
    ///
    /// ── Phase 2  InjectCompanyObjects()  GameManager.Awake [Postfix]
    ///    Clones an existing AI Company's full GameObject (gives us ResearchManager,
    ///    MoneyController, CompanyBehaviour etc. for free), swaps the companyDefinition
    ///    reference, and adds the clone to GameManager.companies.
    ///
    /// ── Phase 3  InjectSaveData()        LoadSaveManager.ExtractAllFromSaveData [Prefix]
    ///    Injects a CompanyDataSave entry so the company hydrates correctly, and adds the
    ///    definition to StartGameConfig.enabledRivalCorporations so SetupPlayerAndRivalAIs
    ///    keeps the GameObject active.
    ///
    /// Field patches (aiConfig, populationConfig, etc.) are NOT applied here — they are
    /// handled automatically by ScriptableObjectPatcher.RunCompanies, which runs later in
    /// PatchObjectInfoManagerUpdate and will find the new definitions already in
    /// AllCompanyDefinitions.
    /// </summary>
    internal static class CompanyCreator
    {
        // ── Per-entry config ──────────────────────────────────────────────────

        internal class NewCompanyEntry
        {
            public string Id;
            public string DonorId;                   // ID of Company to clone; null = auto
            public double StartingMoney = 100_000_000.0;
            public bool   OnInMenu      = true;      // show in corporation select UI
            public bool   AlwaysActiveAI = true;     // add to enabledRivalCorporations
        }

        // ── Cross-phase state ─────────────────────────────────────────────────

        static List<NewCompanyEntry>                          _entries   = new List<NewCompanyEntry>();
        static readonly List<CompanyDefinition>               _newDefs   = new List<CompanyDefinition>();
        static readonly Dictionary<CompanyDefinition, NewCompanyEntry> _defToEntry
                                                             = new Dictionary<CompanyDefinition, NewCompanyEntry>();

        // ── Reflection ────────────────────────────────────────────────────────

        // AllScriptableObject<CompanyDefinition> base class fields
        static readonly FieldInfo _soListFi =
            typeof(AllScriptableObject<CompanyDefinition>)
                .GetField("list",         BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _soListNotEmptyFi =
            typeof(AllScriptableObject<CompanyDefinition>)
                .GetField("listNotEmpty", BindingFlags.NonPublic | BindingFlags.Instance);

        // AllScriptableObjectManager.allMyIDScriptableObjects  (private)
        static readonly FieldInfo _allMyIDFi =
            typeof(AllScriptableObjectManager)
                .GetField("allMyIDScriptableObjects", BindingFlags.NonPublic | BindingFlags.Instance);

        // Company.companyDefinition  (private, serialized)
        static readonly FieldInfo _companyDefFi =
            typeof(Company)
                .GetField("companyDefinition", BindingFlags.NonPublic | BindingFlags.Instance);

        // GameManager.companies  (private, serialized)
        static readonly FieldInfo _gmCompaniesFi =
            typeof(GameManager)
                .GetField("companies", BindingFlags.NonPublic | BindingFlags.Instance);

        // ────────────────────────────────────────────────────────────────────
        // Config loading
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the <c>new_companies:</c> block from companies.yaml in <paramref name="dir"/>.
        /// Replaces any previously loaded entries.
        /// </summary>
        internal static void LoadConfig(string dir)
        {
            _entries.Clear();

            string path = Path.Combine(dir, "companies.yaml");
            var raw = YamlHelper.LoadRawMap(path);
            if (raw == null) return;

            if (!raw.TryGetValue("new_companies", out object ncObj)) return;
            if (!(ncObj is Dictionary<object, object> ncMap)) return;

            foreach (var kv in ncMap)
            {
                string id = kv.Key?.ToString() ?? "";
                if (string.IsNullOrEmpty(id) || id.StartsWith("_")) continue;
                if (!(kv.Value is Dictionary<object, object> fields)) continue;

                var entry = new NewCompanyEntry { Id = id };
                foreach (var fkv in fields)
                {
                    string fk = fkv.Key?.ToString()   ?? "";
                    string fv = fkv.Value?.ToString() ?? "";
                    switch (fk)
                    {
                        case "donor":          entry.DonorId       = fv; break;
                        case "startingMoney":  double.TryParse(fv, out entry.StartingMoney);  break;
                        case "onInMenu":       bool.TryParse(fv,   out entry.OnInMenu);       break;
                        case "alwaysActiveAI": bool.TryParse(fv,   out entry.AlwaysActiveAI); break;
                    }
                }

                _entries.Add(entry);
                Plugin.Log.LogInfo($"[CompanyCreator] Config loaded: '{id}'" +
                    $" donor={entry.DonorId ?? "auto"}" +
                    $" money={entry.StartingMoney:N0}" +
                    $" onInMenu={entry.OnInMenu}" +
                    $" alwaysActiveAI={entry.AlwaysActiveAI}");
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Phase 1 — CompanyDefinition injection
        // Hook: AllScriptableObjectManager.InitializeSingleton [Postfix]
        // ────────────────────────────────────────────────────────────────────

        internal static void InjectDefinitions(AllScriptableObjectManager allSO)
        {
            // Lazy config load — Plugin.PluginDir is guaranteed set before any scene Awake.
            LoadConfig(Plugin.PluginDir);
            if (_entries.Count == 0) return;

            var allDefs = allSO.AllCompanyDefinitions;
            var list    = _soListFi?.GetValue(allDefs)         as List<CompanyDefinition>;
            var listNE  = _soListNotEmptyFi?.GetValue(allDefs) as List<CompanyDefinition>;
            var allMyID = _allMyIDFi?.GetValue(allSO)          as List<MyIDScriptableObject>;

            if (list == null || listNE == null)
            {
                Plugin.Log.LogError("[CompanyCreator] Phase 1: reflection failed — AllCompanyDefinitions lists not accessible.");
                return;
            }

            foreach (var entry in _entries)
            {
                // Hot-reload / double-init guard
                if (allDefs.GetByID(entry.Id) != null)
                {
                    Plugin.Log.LogInfo($"[CompanyCreator] Phase 1: '{entry.Id}' already exists — skipping.");
                    continue;
                }

                var def = ScriptableObject.CreateInstance<CompanyDefinition>();
                def.name = entry.Id;
                FacilityCreator.SetField(def, "id",       entry.Id);
                FacilityCreator.SetField(def, "onInMenu", entry.OnInMenu);

                list.Add(def);
                listNE.Add(def);
                allMyID?.Add(def);

                _newDefs.Add(def);
                _defToEntry[def] = entry;

                Plugin.Log.LogInfo($"[CompanyCreator] Phase 1: registered CompanyDefinition '{entry.Id}'.");
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Phase 2 — Company MonoBehaviour injection
        // Hook: GameManager.Awake [Postfix]
        //   Runs after SetMainObjectForCompany() has already iterated the list,
        //   so we must mirror that call manually for new definitions.
        // ────────────────────────────────────────────────────────────────────

        internal static void InjectCompanyObjects(GameManager gm)
        {
            if (_newDefs.Count == 0) return;

            var companies = _gmCompaniesFi?.GetValue(gm) as List<Company>;
            if (companies == null)
            {
                Plugin.Log.LogError("[CompanyCreator] Phase 2: could not access GameManager.companies.");
                return;
            }

            // Grab the mainObject that SetMainObjectForCompany() assigned to the player.
            // We replicate the same assignment for each new definition so Company.Start()
            // ( farthestPointReached = mainObjectInfo ) doesn't NRE.
            IdForObjectInfo sharedMainObj = null;
            try { sharedMainObj = gm.Player?.Definition?.mainObject; } catch { }

            foreach (var def in _newDefs)
            {
                var entry = _defToEntry[def];

                // ── Find donor ────────────────────────────────────────────────
                Company donor = null;
                if (!string.IsNullOrEmpty(entry.DonorId))
                    donor = companies.FirstOrDefault(c => c.Definition?.ID == entry.DonorId);
                // Fall back: any non-player, non-world-government AI company
                if (donor == null)
                    donor = companies.FirstOrDefault(c =>
                        c != gm.Player &&
                        c.Definition != null &&
                        !c.Definition.IsWorldGovernment);
                // Last resort: any non-player company
                if (donor == null)
                    donor = companies.FirstOrDefault(c => c != gm.Player);

                if (donor == null)
                {
                    Plugin.Log.LogError($"[CompanyCreator] Phase 2: no donor found for '{def.ID}' — skipping.");
                    continue;
                }

                // ── Clone the entire Company GameObject ───────────────────────
                // Instantiate copies ResearchManager, MoneyController, BonusController,
                // CompanyBehaviour and all other components without any boilerplate.
                var newGO = Object.Instantiate(donor.gameObject, donor.transform.parent);
                newGO.name = $"Company_{def.ID}";

                var newCompany = newGO.GetComponent<Company>();
                if (newCompany == null)
                {
                    Plugin.Log.LogError($"[CompanyCreator] Phase 2: cloned GO has no Company component for '{def.ID}'.");
                    Object.Destroy(newGO);
                    continue;
                }

                // ── Swap definition and mainObject ────────────────────────────
                _companyDefFi.SetValue(newCompany, def);

                if (sharedMainObj != null)
                    def.mainObject = sharedMainObj;

                // ── Register with GameManager ─────────────────────────────────
                companies.Add(newCompany);

                Plugin.Log.LogInfo(
                    $"[CompanyCreator] Phase 2: created Company '{def.ID}'" +
                    $" (cloned from '{donor.Definition?.ID ?? "?"}').");
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Phase 3 — Save data + activation
        // Hook: LoadSaveManager.ExtractAllFromSaveData [Prefix]
        //   Runs for BOTH new games (InitFromStartGameConfiguration path) and
        //   save loads (LoadGame path) — the single reliable choke point.
        //   SetupPlayerAndRivalAIs() is called at the start of ExtractAllFromSaveData,
        //   so we must inject into enabledRivalCorporations before that call.
        // ────────────────────────────────────────────────────────────────────

        internal static void InjectSaveData(SaveGameData saveData)
        {
            if (_newDefs.Count == 0) return;

            var gm = MonoBehaviourSingleton<GameManager>.Instance;
            if (gm?.StartGameConfig == null) return;

            var cfg = gm.StartGameConfig;

            foreach (var def in _newDefs)
            {
                var entry = _defToEntry[def];

                // ── Keep the Company GO active ────────────────────────────────
                // SetupPlayerAndRivalAIs() deactivates any company not in
                // enabledRivalCorporations (unless IsWorldGovernment or idleCompanies).
                if (entry.AlwaysActiveAI &&
                    cfg.enabledRivalCorporations != null &&
                    !cfg.enabledRivalCorporations.Contains(def))
                {
                    cfg.enabledRivalCorporations.Add(def);
                    Plugin.Log.LogInfo($"[CompanyCreator] Phase 3: added '{def.ID}' to enabledRivalCorporations.");
                }

                // ── Inject CompanyDataSave ────────────────────────────────────
                // Company.ExtractFromSaveGameData matches by companyID.id == this.ID.
                // MoneyController does the same for its own component.
                // Without this entry the Company logs an error and stays at defaults.
                bool alreadySaved = saveData.companyDataSave
                    .Any(s => s.companyID?.id == def.ID);

                if (alreadySaved)
                {
                    Plugin.Log.LogInfo($"[CompanyCreator] Phase 3: '{def.ID}' already has a CompanyDataSave — not overwriting.");
                    continue;
                }

                saveData.companyDataSave.Add(new CompanyDataSave
                {
                    companyID = new CompanyIDSave { id = def.ID },
                    money     = entry.StartingMoney,
                });
                Plugin.Log.LogInfo(
                    $"[CompanyCreator] Phase 3: injected CompanyDataSave for '{def.ID}'" +
                    $" (starting money: ${entry.StartingMoney:N0}).");
            }
        }
    }
}
