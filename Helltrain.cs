using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using Oxide.Core;
using Rust;
using UnityEngine;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using Facepunch;
using System;
using System.Text;
using System.Reflection;


namespace Oxide.Plugins
{
    [Info("Helltrain", "BLOODHELL", "6.6.6")]
    [Description("Поезд для ивентов с фракциями и лутом")]
    public class Helltrain : RustPlugin
    {
		
		void Unload()
{
    try
    {
        // Сносим наш состав и все наши прикреплённые сущности
        KillEventTrainCars("plugin_unload", force: true);

    }
    catch (Exception ex)
    {
        PrintError($"Unload cleanup error: {ex}");
    }
}
		
private const ulong HELL_OWNER_ID = 99999999999999999UL; // любое уникальное число формата ulong
private readonly HashSet<BaseNetworkable> _spawnedTrainEntities = new HashSet<BaseNetworkable>();
// GENERATOR: активная фракция текущего запуска (BANDIT/COBLAB/PMC)
private string _activeFactionKey = "BANDIT";
private string _activeLayoutName = null;

		// 🔇 Антиспам по хак-крейту


 private bool _explosionTimerArmedOnce = false;
 private Timer _engineWatchdog;
 private bool _explodedOnce = false;
 // глушилка хуков и анти-дубль очистки по локомотиву
private bool _suppressHooks = false;
private bool _engineCleanupTriggered = false;
private float _engineCleanupCooldownUntil = 0f;
private bool _firstLootAnnounced = false;
private const string LAPTOP_PREFAB_PATH = "assets/prefabs/misc/laptop_deployable.prefab";
private void Broadcast(string msg) => Server.Broadcast(msg);
// CORE ownership: пока идёт сборка состава — никакой параллельный cleanup/stop
private bool _isBuildingTrain = false;
// Anti-stuck immunity window (warmup phase)
private float _antiStuckIgnoreUntil = 0f;
private bool _abortRequested = false;
private string _abortReason = null;

// compositionKey, полученный из ResolveCompositionKey до assembly, используется после assembly
private string _lastResolvedCompositionKey = null;
// POPULATE PLAN runtime (CORE Step 2)
private class CrateAssignRt
{
    public string lootKey;     // "None" | CrateTypeName
    public string prefabPath;  // null (legacy) | "assets/....prefab" (new format)
}

private List<CrateAssignRt> _activeCrateAssignments = null; // index -> assignment
private int _activeCrateSlotCursor = 0; // глобальный индекс слота для исполнения assignments
// NPC assignments runtime (CORE Step N)
private class NpcAssignRt
{
    public string kitKey; // "None" | kitKey из PopulatePlan
}

private List<NpcAssignRt> _activeNpcAssignments = null; // index -> kitKey
private int _activeNpcSlotCursor = 0; // глобальный NPC slot index



private readonly Dictionary<ulong, string> _crateTypeName = new Dictionary<ulong, string>(); // netId -> CrateTypeName


private enum CrateState { Idle, CountingDown, Open }

// === Tracking helpers ===
private void Track(BaseNetworkable ent)
{
    if (ent != null && !ent.IsDestroyed) 
		_spawnedTrainEntities.Add(ent);
}
private void UntrackAndKill(BaseNetworkable ent)
{
    if (ent == null) return;
    _spawnedTrainEntities.Remove(ent);
    if (!ent.IsDestroyed) ent.Kill();
}
private void AbortRequest(string reason, string factionKey, string layoutName, string compositionKey)
{
    _abortRequested = true;
    _abortReason = reason ?? "UNKNOWN_ABORT";

    PrintError($"[Helltrain][ABORT] reason='{_abortReason}' faction={factionKey} layout={layoutName} compositionKey={compositionKey}");
}

private void ProcessAbortIfRequested(string context)
{
    if (!_abortRequested) return;

    var r = _abortReason ?? "UNKNOWN_ABORT";
    _abortRequested = false;
    _abortReason = null;

    // централизованный stop/cleanup только здесь (CORE)
    KillEventTrainCars($"abort:{context}:{r}", force: true);
}

private bool Gen_ResolveCompositionKey(string factionKey, string overrideKey, out string compositionKey, out List<string> wagons, out string reason)
{
    compositionKey = null;
    wagons = null;
    reason = null;

    if (HelltrainGenerator == null)
    {
        reason = "GENERATOR_NOT_LOADED";
        return false;
    }

    var res = HelltrainGenerator.Call("ResolveCompositionKey", factionKey, overrideKey);

    // 🔴 CONTRACT v1: (ok, compositionKey, wagons, reason)
    if (res is object[] ar && ar.Length >= 4)
    {
        var ok = Convert.ToBoolean(ar[0]);
        compositionKey = ar[1] as string;

        // wagons может прийти как List<string>, string[], object[]
        var w = ar[2];
        if (w is List<string> wl) wagons = wl;
        else if (w is string[] ws) wagons = new List<string>(ws);
        else if (w is object[] wo)
        {
            var list = new List<string>(wo.Length);
            foreach (var o in wo)
            {
                if (o is string s && !string.IsNullOrEmpty(s))
                    list.Add(s);
            }
            wagons = list;
        }

        reason = ar[3] as string;
        return ok;
    }

    reason = "GENERATOR_BAD_RETURN_CONTRACT";
    return false;
}


private bool Gen_ValidateWagons(string factionKey, List<string> wagonNames, out string reason)
{
    reason = null;

    if (HelltrainGenerator == null)
    {
        reason = "GENERATOR_NOT_LOADED";
        return false;
    }

    var res = HelltrainGenerator.Call("ValidateWagons", factionKey, wagonNames);
    if (res is object[] arr && arr.Length >= 2 && arr[0] is bool ok)
    {
        reason = arr[1] as string;
        return ok;
    }

    reason = "GENERATOR_BAD_RESPONSE";
    return false;
}

private bool Gen_BuildPopulatePlan(string factionKey, string compositionKey, string layoutName, List<BaseEntity> trainCars, out object plan, out string reason)
{
    plan = null;
    reason = null;

    if (HelltrainGenerator == null)
    {
        reason = "GENERATOR_NOT_LOADED";
        return false;
    }

    var res = HelltrainGenerator.Call("BuildPopulatePlan", factionKey, compositionKey, layoutName, trainCars);
    if (res is object[] arr && arr.Length >= 3 && arr[0] is bool ok)
    {
        plan = arr[1];          // план как object (DTO/JSON) — без предположений в CORE
        reason = arr[2] as string;
        return ok;
    }

    reason = "GENERATOR_BAD_RESPONSE";
    return false;
}

// PLAN PIPE helper: безопасно вытаскиваем кол-во слотов из planObj (не применяя план)
private int PlanPipe_GetPlanSlots(object planObj)
{
    if (planObj == null) return 0;

    if (planObj is Dictionary<string, object> dict)
    {
        if (dict.TryGetValue("CrateAssignments", out var ca) && ca != null)
        {
            if (ca is System.Collections.IList list) return list.Count;
            if (ca is string[] arrS) return arrS.Length;
            if (ca is object[] arrO) return arrO.Length;
        }
    }

    return 0;
}

// ApplyPopulatePlan: CORE Step 2 — читаем CrateAssignments и сохраняем в рантайм
private void ApplyPopulatePlan(object planObj)
{
    _activeCrateAssignments = null;
_activeCrateSlotCursor = 0;

_activeNpcAssignments = null;
_activeNpcSlotCursor = 0;




int parsedPlanSlots = PlanPipe_GetPlanSlots(planObj);
Puts($"[PLAN PIPE] applyCalled=true parsedPlanSlots={parsedPlanSlots}");

    if (planObj == null) return;

    // Generator возвращает Dictionary<string, object>
if (planObj is Dictionary<string, object> dict)
{
    if (dict.TryGetValue("CrateAssignments", out var ca) && ca != null)
    {
        // New/legacy: IList (List<object>, List<Dictionary<..>>, etc.)
        if (ca is System.Collections.IList anyList)
        {
            var tmp = new List<CrateAssignRt>(anyList.Count);

            foreach (var o in anyList)
            {
                // legacy: "CrateXxx" / "None"
                if (o is string s)
                {
                    tmp.Add(new CrateAssignRt
                    {
                        lootKey = string.IsNullOrEmpty(s) ? "None" : s,
                        prefabPath = null
                    });
                    continue;
                }

                // new: { lootKey, prefabPath }
                if (o is Dictionary<string, object> odict)
                {
                    odict.TryGetValue("lootKey", out var lkObj);
                    odict.TryGetValue("prefabPath", out var ppObj);

                    var lk = lkObj as string;
                    var pp = ppObj as string;

                    tmp.Add(new CrateAssignRt
                    {
                        lootKey = string.IsNullOrEmpty(lk) ? "None" : lk,
                        prefabPath = string.IsNullOrEmpty(pp) ? null : pp
                    });
                    continue;
                }

                // unknown entry => treat as None (no crash)
                tmp.Add(new CrateAssignRt { lootKey = "None", prefabPath = null });
            }

            _activeCrateAssignments = tmp;
        }
        // extra legacy safety: string[]
        else if (ca is string[] arrS)
        {
            var tmp = new List<CrateAssignRt>(arrS.Length);
            foreach (var s in arrS)
                tmp.Add(new CrateAssignRt { lootKey = string.IsNullOrEmpty(s) ? "None" : s, prefabPath = null });
            _activeCrateAssignments = tmp;
        }
        // extra legacy safety: object[]
        else if (ca is object[] arrO)
        {
            var tmp = new List<CrateAssignRt>(arrO.Length);
            foreach (var o in arrO)
            {
                if (o is string s && !string.IsNullOrEmpty(s))
                    tmp.Add(new CrateAssignRt { lootKey = s, prefabPath = null });
                else
                    tmp.Add(new CrateAssignRt { lootKey = "None", prefabPath = null });
            }
            _activeCrateAssignments = tmp;
        }
    }
	// NpcAssignments: new format [{kitKey}] or legacy ["kitKey"]
if (dict.TryGetValue("NpcAssignments", out var na) && na != null)
{
    if (na is System.Collections.IList anyList)
    {
        var tmp = new List<NpcAssignRt>(anyList.Count);

        foreach (var o in anyList)
        {
            // legacy: "kitbandit2" / "None"
            if (o is string s)
            {
                tmp.Add(new NpcAssignRt { kitKey = string.IsNullOrEmpty(s) ? "None" : s });
                continue;
            }

            // new: { kitKey }
            if (o is Dictionary<string, object> odict)
            {
                odict.TryGetValue("kitKey", out var kkObj);
                var kk = kkObj as string;
                tmp.Add(new NpcAssignRt { kitKey = string.IsNullOrEmpty(kk) ? "None" : kk });
                continue;
            }

            tmp.Add(new NpcAssignRt { kitKey = "None" });
        }

        _activeNpcAssignments = tmp;
    }
}

}




    // минимальный лог-доказательство применения плана (по данным плана, без спама)
    if (_activeCrateAssignments != null)
    {
        int slots = _activeCrateAssignments.Count;
        int spawned = 0;
        int skipped = 0;

        for (int i = 0; i < slots; i++)
        {
           var a = _activeCrateAssignments[i];
var lk = a?.lootKey;

if (string.IsNullOrEmpty(lk) || lk.Equals("None", StringComparison.OrdinalIgnoreCase))
    skipped++;
else
    spawned++;

        }

        Puts($"[POPAPPLY DBG] slots={slots} spawned={spawned} skipped={skipped}");
    }
}





		[PluginReference] Plugin KitsSuite;
		[PluginReference] private Plugin HelltrainGenerator;

		[PluginReference]
private Plugin Loottable;


private const string PERM_ADMIN = "helltrain.admin";
private const string PERM_ADMIN_DRIVE = "helltrain.admin.drive";
private const string PERM_EDITOR = "helltrain.editor";
private const string PERM_START = "helltrain.start";
private const string PERM_VIEW = "helltrain.view";
private const string PERM_DEBUG = "helltrain.debug";

private bool HasPerm(BasePlayer player, string perm)
{
    if (player == null) return false;
    if (player.IsAdmin) return true; // фолбек, чтобы не запереть себя
    return permission.UserHasPermission(player.UserIDString, perm);
}

private System.Random _rng = new System.Random();

private string PickPresetAB(string factionUpper)
{
    string a = factionUpper + "_A";
    string b = factionUpper + "_B";
    return (_rng.Next(2) == 0) ? a : b;
}

// === Loottable preset bootstrap ===
private void RegisterHelltrainPresetsToLoottable()
{
    if (Loottable == null)
    {
        PrintWarning("Loottable не найден — пресеты не зарегистрированы");
        return;
    }

    // Очистим наши старые (если были), зададим категорию и создадим 6 пресетов
    Loottable.Call("ClearPresets", this);
    Loottable.Call("CreatePresetCategory", this, "Helltrain");

    // 6 ключей: PMC_A/B, COBLAB_A/B, BANDIT_A/B
    Loottable.Call("CreatePreset", this, "PMC_A", "Helltrain · PMC A", null, false);
    Loottable.Call("CreatePreset", this, "PMC_B", "Helltrain · PMC B", null, false);
    Loottable.Call("CreatePreset", this, "COBLAB_A", "Helltrain · COBLAB A", null, false);
    Loottable.Call("CreatePreset", this, "COBLAB_B", "Helltrain · COBLAB B", null, false);
    Loottable.Call("CreatePreset", this, "BANDIT_A", "Helltrain · BANDIT A", null, false);
    Loottable.Call("CreatePreset", this, "BANDIT_B", "Helltrain · BANDIT B", null, false);

    Puts("[Helltrain] Loottable: зарегистрированы пресеты PMC/COBLAB/BANDIT (A/B).");
}

// Регистрируем пресеты на старте сервера
private void OnServerInitialized()
{
    // permissions
    permission.RegisterPermission(PERM_ADMIN, this);
    permission.RegisterPermission(PERM_ADMIN_DRIVE, this);
    permission.RegisterPermission(PERM_EDITOR, this);
    permission.RegisterPermission(PERM_START, this);
    permission.RegisterPermission(PERM_VIEW, this);
    permission.RegisterPermission(PERM_DEBUG, this);

    // loottable presets
    try { RegisterHelltrainPresetsToLoottable(); }
    catch (Exception ex) { PrintWarning($"Loottable preset init error: {ex.Message}"); }

    // layouts cache
    try { LoadLayouts(); } catch (Exception ex) { PrintError($"LoadLayouts init error: {ex}"); }

    // track cache (optional)
    try { CacheSplines(); } catch { }

    if (config != null && config.AutoRespawn)
    {
        timer.Once(5f, () =>
        {
            if (activeHellTrain == null && (_spawnedCars.Count == 0))
                StartRespawnTimer();
        });
    }
}

private TrainEngine activeHellTrain = null;
        private Timer respawnTimer = null;
		private Timer _gridCheckTimer = null;
        private List<TrainTrackSpline> availableOverworldSplines = new List<TrainTrackSpline>();
        private List<TrainTrackSpline> availableUnderworldSplines = new List<TrainTrackSpline>();

private void CacheSplines()
{
    availableOverworldSplines.Clear();
    availableUnderworldSplines.Clear();

    var all = UnityEngine.Object.FindObjectsOfType<TrainTrackSpline>();
    foreach (var s in all)
    {
        if (s == null) continue;
        var name = s.name ?? string.Empty;
        if (name.IndexOf("under", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("tunnel", StringComparison.OrdinalIgnoreCase) >= 0)
            availableUnderworldSplines.Add(s);
        else
            availableOverworldSplines.Add(s);
    }

    Puts($"[Helltrain] splines cached: overworld={availableOverworldSplines.Count}, underworld={availableUnderworldSplines.Count}");
}

		private bool _allowDestroy = false;

        #region HT.PREFABS
        private const string EnginePrefab = "assets/content/vehicles/trains/locomotive/locomotive.entity.prefab";
        private const string WorkcartPrefab = "assets/content/vehicles/trains/workcart/workcart.entity.prefab";
        private const string WagonPrefabA = "assets/content/vehicles/trains/wagons/trainwagona.entity.prefab";
        private const string WagonPrefabB = "assets/content/vehicles/trains/wagons/trainwagonb.entity.prefab";
        private const string WagonPrefabC = "assets/content/vehicles/trains/wagons/trainwagonc.entity.prefab";
        private const string WagonPrefabLoot = "assets/content/vehicles/trains/wagons/trainwagonunloadableloot.entity.prefab";
        private const string WagonPrefabUnloaded = "assets/content/vehicles/trains/wagons/trainwagonunloadable.entity.prefab";
 private const string PREFAB_CRATE_PMC    = "assets/bundled/prefabs/radtown/crate_elite.prefab";
 private const string PREFAB_CRATE_BANDIT = "assets/bundled/prefabs/radtown/crate_normal_2.prefab";
 private const string PREFAB_CRATE_COBLAB = "assets/bundled/prefabs/radtown/crate_normal.prefab";
        private const string SCIENTIST_PREFAB = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_cargo_turret_any.prefab";
        private const string SAMSITE_PREFAB = "assets/prefabs/npc/sam_site_turret/sam_static.prefab";
       private const string TURRET_PREFAB = "assets/prefabs/npc/autoturret/autoturret_deployed.prefab";
        private const string HACKABLE_CRATE_PREFAB = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
       public string HackableCratePrefab => HACKABLE_CRATE_PREFAB;
	   private string GetCratePrefabForFaction(string faction)
{
    switch ((faction ?? "BANDIT").ToUpper())
    {
        case "PMC":    return PREFAB_CRATE_PMC;
        case "COBLAB": return PREFAB_CRATE_COBLAB;
        default:       return PREFAB_CRATE_BANDIT;
    }
}
	  
	  #endregion
	  
		
#region HT.AI.COMPONENTS

public class HellTrainDefender : MonoBehaviour { }

public class TurretMarker : MonoBehaviour
{
    public string gun;
    public string ammo;
    public int ammoCount;

    public void Set(string gun, string ammo, int ammoCount)
    {
        this.gun = gun;
        this.ammo = ammo;
        this.ammoCount = ammoCount;
    }
}



public class NPCTypeMarker : MonoBehaviour
{
	public string savedKit;
public List<string> savedKits = new List<string>();
    public string npcType;
}

public class SlotMarker : MonoBehaviour
{
    public enum Kind { Npc, Crate }
    public Kind kind;
}

public class ShelfMarker : MonoBehaviour
{
    public string prefab;
}

// ✅ ТОЛЬКО ОДИН класс TrainAutoTurret!
public class TrainAutoTurret : MonoBehaviour
{
    private AutoTurret turret;
    private bool weaponReady = false;
    public Helltrain plugin;
    
    void Start()
    {
        turret = GetComponent<AutoTurret>();
        if (turret == null) return;
        
        gameObject.AddComponent<HellTrainDefender>();
        
        turret.SetFlag(IOEntity.Flag_HasPower, true, false, true);
        turret.UpdateFromInput(100, 0);
        
        turret.SetFlag(BaseEntity.Flags.On, false, false, true);
        turret.isLootable = false;
        turret.sightRange = 30f;
        
        turret.InvokeRepeating(CheckTargetForFF, 0.5f, 0.5f);
        turret.InvokeRepeating(CheckMagazine, 0.5f, 0.5f);
        turret.InvokeRepeating(RefillAmmo, 5f, 5f);
    }
    
	
    private void CheckMagazine()
    {
        if (turret == null || turret.IsDestroyed || turret.inventory == null) 
            return;
        
        if (!turret.HasFlag(IOEntity.Flag_HasPower))
        {
            turret.SetFlag(IOEntity.Flag_HasPower, true, false, true);
            turret.UpdateFromInput(100, 0);
        }
        
        if (!weaponReady)
        {
            if (turret.inventory.itemList.Count >= 2)
            {
                weaponReady = true;
                
                turret.SetFlag(IOEntity.Flag_HasPower, true, false, true);
                turret.UpdateFromInput(100, 0);
                
                turret.UpdateAttachedWeapon();
                turret.UpdateTotalAmmo();
                turret.SetFlag(BaseEntity.Flags.On, true, false, true);
                
                turret.SendNetworkUpdate();
                
                if (plugin != null)
                    plugin.Puts($"   🔋 Турель получила питание и включена!");
            }
            return;
        }
        
        if (turret.inventory.itemList.Count > 0)
        {
            Item weaponItem = turret.inventory.itemList[0];
            if (weaponItem != null)
            {
                BaseProjectile weapon = weaponItem.GetHeldEntity() as BaseProjectile;
                if (weapon != null && weapon.primaryMagazine != null)
                {
                    if (weapon.primaryMagazine.contents == 0)
                    {
                        weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
                        weapon.SendNetworkUpdateImmediate();
                    }
                }
            }
        }
    }
    
    private void RefillAmmo()
    {
        if (turret == null || turret.IsDestroyed || turret.inventory == null) 
            return;
        
        if (turret.inventory.itemList.Count > 1)
        {
            Item ammoItem = turret.inventory.itemList[1];
            if (ammoItem != null && ammoItem.amount < 500)
            {
                ammoItem.amount = 500;
                ammoItem.MarkDirty();
                turret.UpdateTotalAmmo();
            }
        }
    }
    
    private void CheckTargetForFF()
    {
        if (turret == null || turret.IsDestroyed) return;
        
        if (turret.target != null)
        {
            var targetDefender = turret.target.GetComponent<HellTrainDefender>();
            if (targetDefender != null)
            {
                turret.SetTarget(null);
            }
        }
    }
    
    void OnDestroy()
    {
        if (turret != null && !turret.IsDestroyed)
        {
            CancelInvoke("CheckTargetForFF");
            CancelInvoke("CheckMagazine");
            CancelInvoke("RefillAmmo");
        }
    }
}

public class TrainSamSite : MonoBehaviour
{
    private SamSite samsite;
    
    void Awake()
    {
        samsite = GetComponent<SamSite>();
        if (samsite == null) return;
        
        samsite.staticRespawn = true;
        gameObject.AddComponent<HellTrainDefender>();
    }
}

public class HellTrainComponent : MonoBehaviour
{
    public Helltrain plugin;
    public TrainEngine engine;
    private int zeroSpeedTicks = 0;
    private bool movingForward = true;

    private void FixedUpdate()
    {
        if (engine == null || engine.IsDestroyed) 
        {
            Destroy(this);
            return;
        }

         // WARMUP IMMUNITY: пока идёт окно иммунитета после спавна — не считаем "застрял"
        if (plugin != null && Time.realtimeSinceStartup < plugin._antiStuckIgnoreUntil)
        {
            zeroSpeedTicks = 0; // чтобы не накапливалось "до реверса"
            return;
        }

        float speed = engine.GetTrackSpeed();


        if (Mathf.Abs(speed) < 0.1f)
        {
            zeroSpeedTicks++;

            if (zeroSpeedTicks >= 90)
            {
                movingForward = !movingForward;

                if (movingForward)
                    engine.SetThrottle(TrainEngine.EngineSpeeds.Fwd_Hi);
                else
                    engine.SetThrottle(TrainEngine.EngineSpeeds.Rev_Hi);

                plugin.Puts($"⚠️ Поезд застрял! Реверс → {(movingForward ? "ВПЕРЁД" : "НАЗАД")}");

                zeroSpeedTicks = 0;
            }
        }
        else
        {
            zeroSpeedTicks = 0;
        }
    }
}

private void StartEngineWatchdog()
{
    _engineWatchdog = timer.Every(5f, () =>
    {
        // если у нас вообще ничего не заспавнено — молчим
        if (_spawnedCars.Count == 0 && _spawnedTrainEntities.Count == 0) return;

        // есть ли среди наших вагонов живой локомотив?
        bool engineAlive = false;
        foreach (var e in _spawnedCars)
        {
            var eng = e as TrainEngine;
            if (eng != null && !eng.IsDestroyed) { engineAlive = true; break; }
        }
        if (!engineAlive)
        {
            Puts("[Helltrain] Engine watchdog: engine missing → cleanup event cars");
            KillEventTrainCars("watchdog_no_engine");
        }
    });
}

private void StopEngineWatchdog()
{
    if (_engineWatchdog != null)
    {
        _engineWatchdog.Destroy();
        _engineWatchdog = null;
    }
}


// Если внешний плагин/команда (cleanup.trains и т.п.) убила наш локомотив,
// автоматически добиваем все ивентовые вагоны, чтобы не оставались "призраки".
// Если внешний плагин/команда убила наш локомотив → чистим состав
private void OnEntityKill(BaseNetworkable entity)
{
    if (_suppressHooks) return;

    var engine = entity as TrainEngine;
    if (engine == null) return;

    // реагируем ТОЛЬКО на наш ивент-лок, если метка есть
    bool ours = (_spawnedCars.Contains(engine) || _spawnedTrainEntities.Contains(engine));
    if (!ours && engine.OwnerID != HELL_OWNER_ID) return;

    // анти-спам: 1 вызов в секунду и только один триггер до конца очистки
    if (Time.realtimeSinceStartup < _engineCleanupCooldownUntil) return;
    _engineCleanupCooldownUntil = Time.realtimeSinceStartup + 1f;
    if (_engineCleanupTriggered) return;
    _engineCleanupTriggered = true;

    Puts("[Helltrain] Engine OnEntityKill → cleanup event cars");
    KillEventTrainCars("engine_removed");
}

private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
{
    if (_suppressHooks) return;

    var engine = entity as TrainEngine;
    if (engine == null) return;

    if (Time.realtimeSinceStartup < _engineCleanupCooldownUntil) return;
    _engineCleanupCooldownUntil = Time.realtimeSinceStartup + 1f;
    if (_engineCleanupTriggered) return;
    _engineCleanupTriggered = true;

    Puts("[Helltrain] Engine OnEntityDeath → cleanup event cars");
    KillEventTrainCars("engine_died");
}



// Хелпер: снести весь наш состав (только ивентовые entities)
private void KillEventTrainCars(string reason, bool force = false)
{
    if (_isBuildingTrain && !force)
    {
        PrintWarning($"[Helltrain] Cleanup suppressed during train build ({reason})");
        return;
    }

    // сброс окна иммунитета anti-stuck при любом stop/cleanup
    _antiStuckIgnoreUntil = 0f;

    _suppressHooks = true;

    StopEngineWatchdog();
    StopGridCheckTimer();
    CancelLifecycleTimer();

    try
    {
        // восстановить защиту (если меняли)
        RestoreProtectionForAll();

        // убиваем всё по «снимку», чтобы не падать и не зациклиться на хуках
        var entsSnap = _spawnedTrainEntities.ToArray();
foreach (var e in entsSnap)
{
    if (e == null || e.IsDestroyed) continue;

    try { e.Kill(); }
    catch (Exception ex) { PrintWarning($"[Helltrain] Cleanup entity kill error: {ex}"); }


    // добивка: иногда Kill не доводит уничтожение до конца
    if (e != null && !e.IsDestroyed)
    {
        try { e.Kill(BaseNetworkable.DestroyMode.Gib); } catch {}
        try { e.Kill(BaseNetworkable.DestroyMode.None); } catch {}
    }
}
_spawnedTrainEntities.Clear();



        var carsSnap = _spawnedCars.ToArray();
foreach (var car in carsSnap)
{
    if (car == null || car.IsDestroyed) continue;

    try { car.Kill(); }
    catch (Exception ex) { PrintWarning($"[Helltrain] Cleanup car kill error: {ex}"); }


    // добивка: иногда вагон не умирает с первого Kill()
    if (car != null && !car.IsDestroyed)
    {
        try { car.Kill(BaseNetworkable.DestroyMode.Gib); } catch {}
        try { car.Kill(BaseNetworkable.DestroyMode.None); } catch {}
    }
}
_spawnedCars.Clear();
// ФОЛБЭК: если какой-то вагон не попал в _spawnedCars или Kill не сработал — добиваем по OwnerID
try
{
    var bnSnap = BaseNetworkable.serverEntities.ToArray();
foreach (var bn in bnSnap)
{
    var tc = bn as TrainCar;
    if (tc == null || tc.IsDestroyed) continue;
    if (tc.OwnerID != HELL_OWNER_ID) continue;

    try { tc.Kill(); } catch {}
    if (tc != null && !tc.IsDestroyed)
    {
        try { tc.Kill(BaseNetworkable.DestroyMode.Gib); } catch {}
        try { tc.Kill(BaseNetworkable.DestroyMode.None); } catch {}
    }
}

}
catch (Exception ex)
{
    PrintWarning($"[Helltrain] Cleanup fallback TrainCar scan error: {ex}");
}



        _spawnedTurrets.Clear();
        _spawnedSamSites.Clear();
        _spawnedNPCs.Clear();
        _savedProtection.Clear();

        _explosionDamageArmed = false;
        _explodedOnce = false;
        activeHellTrain = null;
        _trainLifecycle = null;

        Puts($"[Helltrain] Event cars cleanup completed ({reason}).");
    }
    catch (Exception ex)
    {
        PrintError($"KillEventTrainCars error: {ex}");
    }
    finally
    {
        _suppressHooks = false;               // снова слушаем хуки
        _engineCleanupTriggered = false;      // разрешим будущие триггеры
        _engineCleanupCooldownUntil = 0f;
    }
}

// CORE: нормализация алиасов имени лэйаута (аргумент команды).
// Пользователь может писать wagona_bandit, а файл называется wagonA_bandit.json.
private static string NormalizeLayoutName(string name)
{
    if (string.IsNullOrEmpty(name)) return name;

    // унифицируем регистр/пробелы
    name = name.Trim();

    // wagona_* -> wagonA_* (и аналогично для b/c)
    if (name.StartsWith("wagona_", StringComparison.OrdinalIgnoreCase))
        return "wagonA_" + name.Substring("wagona_".Length);

    if (name.StartsWith("wagonb_", StringComparison.OrdinalIgnoreCase))
        return "wagonB_" + name.Substring("wagonb_".Length);

    if (name.StartsWith("wagonc_", StringComparison.OrdinalIgnoreCase))
        return "wagonC_" + name.Substring("wagonc_".Length);

    return name;
}





#endregion

        #region HT.CONFIG

private class ConfigData
{
	
	[JsonProperty("LootTimerRanges")]
public Dictionary<string, LootTimerRange> LootTimerRanges { get; set; } = new Dictionary<string, LootTimerRange>
{
    ["BANDIT"] = new LootTimerRange { Min = 250, Max = 350 },
    ["COBLAB"] = new LootTimerRange { Min = 300, Max = 425 },
    ["PMC"]    = new LootTimerRange { Min = 400, Max = 500 },
};
public class LootTimerRange { public int Min { get; set; } = 250; public int Max { get; set; } = 500; }

	
	
    public Dictionary<string, TrainComposition> Compositions { get; set; } = new Dictionary<string, TrainComposition>
{
    ["bandit"] = new TrainComposition
    {
        Tier = TrainTier.LIGHT,
        Weight = 34,

        Loco = "loco_bandit",
        MinWagons = 3,
        MaxWagons = 6,

        WagonPools = new Dictionary<string, Dictionary<string, float>>
        {
            ["A"] = new Dictionary<string, float> { ["wagonA_bandit"] = 1f },
            ["B"] = new Dictionary<string, float> { ["wagonB_bandit"] = 1f },
            ["C"] = new Dictionary<string, float> { ["wagonC_bandit"] = 1f }
        },

        Limits = new Dictionary<string, int>
        {
            ["C"] = 2
        }
    },

    ["coblab"] = new TrainComposition
    {
        Tier = TrainTier.MEDIUM,
        Weight = 33,

        Loco = "loco_coblab",
        MinWagons = 3,
        MaxWagons = 6,

        WagonPools = new Dictionary<string, Dictionary<string, float>>
        {
            ["A"] = new Dictionary<string, float> { ["wagonA_coblab"] = 1f },
            ["B"] = new Dictionary<string, float> { ["wagonB_coblab"] = 1f },
            ["C"] = new Dictionary<string, float> { ["wagonC_coblab"] = 1f }
        },

        Limits = new Dictionary<string, int>
        {
            ["C"] = 2
        }
    },

    ["pmc"] = new TrainComposition
    {
        Tier = TrainTier.HEAVY,
        Weight = 33,

        Loco = "loco_pmc",
        MinWagons = 3,
        MaxWagons = 6,

        WagonPools = new Dictionary<string, Dictionary<string, float>>
        {
            ["A"] = new Dictionary<string, float> { ["wagonA_pmc"] = 1f },
            ["B"] = new Dictionary<string, float> { ["wagonB_pmc"] = 1f },
            ["C"] = new Dictionary<string, float> { ["wagonC_pmc"] = 1f }
        },

        Limits = new Dictionary<string, int>
        {
            ["C"] = 2
        }
    },
};

    
    public SpeedSettings Speed { get; set; } = new SpeedSettings();
	
	public class GeneratorSettings
{
    // Общие правила генератора (НЕ веса)
    [JsonProperty("NpcMinDistanceMeters")]
    public float NpcMinDistanceMeters { get; set; } = 1.0f;

    [JsonProperty("NpcRetryLimit")]
    public int NpcRetryLimit { get; set; } = 5;

    [JsonProperty("Factions")]
    public Dictionary<string, FactionGenerator> Factions { get; set; } = new Dictionary<string, FactionGenerator>
    {
        ["BANDIT"] = new FactionGenerator(),
        ["COBLAB"] = new FactionGenerator(),
        ["PMC"]    = new FactionGenerator(),
    };
}

public class FactionGenerator
{
    // 2–4 веса по директиве MAIN (диапазон фиксированный)
    [JsonProperty("NPCCountWeights")]
    public Dictionary<int, float> NPCCountWeights { get; set; } = new Dictionary<int, float>
    {
        [2] = 1f, [3] = 1f, [4] = 1f
    };

    // None / DefaultCrate
    [JsonProperty("CrateSlotWeights")]
    public Dictionary<string, float> CrateSlotWeights { get; set; } = new Dictionary<string, float>
    {
        ["None"] = 1f,
        ["DefaultCrate"] = 1f
    };

    // Под будущее (без внедрения логики сейчас)
    [JsonProperty("KitPools")]
    public Dictionary<string, Dictionary<string, float>> KitPools { get; set; } = new Dictionary<string, Dictionary<string, float>>();

    [JsonProperty("LootKeys")]
    public Dictionary<string, float> LootKeys { get; set; } = new Dictionary<string, float>();
}

	// === GENERATOR (вся логика/веса только в конфиге) ===
[JsonProperty("Generator")]
public GeneratorSettings Generator { get; set; } = new GeneratorSettings();


    
    public bool AutoRespawn { get; set; } = true;
    public float RespawnTime { get; set; } = 60f;
    
    [JsonProperty("Разрешить спавн на поверхности")]
    public bool AllowAboveGround { get; set; } = true;

    [JsonProperty("Разрешить спавн в подземке")]
    public bool AllowUnderGround { get; set; } = false;

    [JsonProperty("Разрешить переходы между уровнями")]
    public bool AllowTransition { get; set; } = false;

    [JsonProperty("Минимальная длина трека для спавна (метры)")]
    public float MinTrackLength { get; set; } = 500f;
    
    [JsonProperty("Названия композиций для анонсов")]
    public Dictionary<string, string> CompositionNames { get; set; } = new Dictionary<string, string>
    {
        ["bandit"] = "Бандитский состав",
        ["coblab"] = "Поезд ученых",
        ["pmc"] = "ЧВК"
    };

    [JsonProperty("Время жизни поезда (минуты)")]
    public float TrainLifetimeMinutes { get; set; } = 60f;

    [JsonProperty("Время респавна после уничтожения (минуты)")]
    public float TrainRespawnMinutes { get; set; } = 60f;

    [JsonProperty("Время до взрыва после взлома (секунды)")]
    public int ExplosionTimerSeconds { get; set; } = 180;

    [JsonProperty("Анонсы времени до взрыва (секунды)")]
    public List<int> ExplosionAnnouncements { get; set; } = new List<int> { 120, 60, 20, 5 };

    [JsonProperty("Количество C4 на вагон при взрыве")]
    public int C4PerWagon { get; set; } = 5;
    
    public Dictionary<string, object> NPC_Types { get; set; } = new Dictionary<string, object>();
    
    // ✅ НОВОЕ: СИСТЕМА АНОНСОВ
    [JsonProperty("Сообщения")]
    public MessageSettings Messages { get; set; } = new MessageSettings();
    
    // ✅ НОВОЕ: ВЕСА
    public class TrainComposition
{
    public TrainTier Tier { get; set; }

    [JsonProperty("Вес (вероятность спавна)")]
    public int Weight { get; set; } = 33;

    // === DIFF#3 (MAIN): layout = геометрия, состав = генерация из конфига ===

    // Локомотив строго 1, обязателен, всегда первый (НЕ часть рандома)
    [JsonProperty("Loco")]
    public string Loco { get; set; }

    [JsonProperty("MinWagons")]
    public int MinWagons { get; set; } = 3;

    [JsonProperty("MaxWagons")]
    public int MaxWagons { get; set; } = 5;

    // Пулы вагонов по типам (A/B/C...), внутри: layoutName -> weight
    [JsonProperty("WagonPools")]
    public Dictionary<string, Dictionary<string, float>> WagonPools { get; set; } =
        new Dictionary<string, Dictionary<string, float>>();

    // Ограничения по типам, например: { "C": 1 }
    [JsonProperty("Limits")]
    public Dictionary<string, int> Limits { get; set; } = new Dictionary<string, int>();

    // Runtime-собранный список (в конфиг НЕ пишется)
    [JsonIgnore]
    public List<string> Wagons { get; set; } = new List<string>();
}

    
    public class SpeedSettings
    {
        [JsonProperty("PMC (Heavy) - максимальная скорость")]
        public float TierHeavy { get; set; } = 10f;
        
        [JsonProperty("COBLAB (Medium) - максимальная скорость")]
        public float TierMedium { get; set; } = 12f;
        
        [JsonProperty("Bandit (Light) - максимальная скорость")]
        public float TierLight { get; set; } = 14f;
    }
	
    
    public enum TrainTier
    {
        LIGHT,
        MEDIUM,
        HEAVY
    }
    
    public class MessageSettings
    {
        [JsonProperty("Спавн поезда")]
        public string TrainSpawned { get; set; } = "🚂 {trainName} появился в квадрате {grid}!";
        
        [JsonProperty("Направление движения")]
        public string TrainDirection { get; set; } = "🚂 {trainName} движется из {fromGrid} → {toGrid}";
        
        [JsonProperty("Взлом начат")]
        public string HackStarted { get; set; } = "🔥 {trainName} ВЗЛОМАН! {minutes} МИНУТ ДО ВЗРЫВА!";
        
        [JsonProperty("Отсчёт взрыва (минуты)")]
        public string ExplosionMinutes { get; set; } = "⚠️ {trainName} взорвётся через {minutes} {minutesWord}!";
        
        [JsonProperty("Отсчёт взрыва (секунды)")]
        public string ExplosionSeconds { get; set; } = "💥 {trainName} взорвётся через {seconds} секунд!";
        
        [JsonProperty("Взрыв")]
        public string Exploded { get; set; } = "💥 {trainName} ВЗОРВАН!";
        
        [JsonProperty("Успешная разгрузка")]
        public string SuccessfulDelivery { get; set; } = "✅ {trainName} успешно разгрузился";
        
        [JsonProperty("Следующий поезд")]
        public string NextTrain { get; set; } = "⏳ Следующий поезд через {minutes} {minutesWord}";
    }
}

// Регистрация пресетов Helltrain в лут-таблице (заглушка, чтобы не падать при компиляции)
// Если потребуется реальная логика — допишем отдельно.


private ConfigData config;

protected override void LoadDefaultConfig()
{
    config = new ConfigData();
    SaveConfig();
}

protected override void LoadConfig()
{
    base.LoadConfig();
    try
    {
        config = Config.ReadObject<ConfigData>();
        if (config == null) config = new ConfigData();
    }
    catch
    {
        config = new ConfigData();
    }
}

protected override void SaveConfig() => Config.WriteObject(config);

#endregion
		
		#region HT.LIFECYCLE

private class TrainLifecycle
{
    public DateTime SpawnTime;
    public DateTime? FirstLootTime;
    public string LastGrid;
    public bool DirectionAnnounced;
    public string CompositionType; // bandit/coblab/pmc
    
    public TrainLifecycle(string compositionType, Vector3 startPos, Helltrain plugin)
    {
        SpawnTime = DateTime.Now;
        CompositionType = compositionType;
        LastGrid = plugin.GetGridPosition(startPos);
    }
}

private TrainLifecycle _trainLifecycle = null;

#endregion


#region HT.TIMERS

// Таймер жизненного цикла (если поезд не лутали — через lifeMin минут снесём и поставим респавн)

// Остановка таймера проверки грида (без дублей)
private void StopGridCheckTimer()
{
    if (_gridCheckTimer != null)
    {
        _gridCheckTimer.Destroy();
        _gridCheckTimer = null;
    }
}



private void StartLifecycleTimer()
{
    CancelLifecycleTimer();

    float lifeMin = config.TrainLifetimeMinutes; // обычно 60
    _lifecycleTimer = timer.Once(lifeMin * 60f, () =>
    {
        // Никто не лутал — считаем «успешная доставка», сносим состав и готовим респавн
        ForceDestroyHellTrain();
        StartRespawnTimer();
    });

    Puts($"⏰ Lifecycle таймер запущен на {lifeMin} мин.");
}

private void CancelLifecycleTimer()
{
    if (_lifecycleTimer != null)
    {
        _lifecycleTimer.Destroy();
        _lifecycleTimer = null;
        Puts("Lifecycle timer canceled");
    }
}


 // Визуал перед детонацией (огни/звук/дым) — T≈total-15
 private void PlayPreDetonationFx()
{
    foreach (var car in _spawnedCars)
    {
        if (car == null || car.IsDestroyed) continue;

        // Эффект предупреждения (огонь, дым, звук)
        Effect.server.Run(
            "assets/prefabs/misc/fireball/small_explosion.prefab",
            car.transform.position,
            Vector3.up
        );
    }

    Server.Broadcast("⚠️ Поезд дрожит... взрыв близко!");
}

// Периодическая проверка состояния состава/сетки (безопасная заглушка)
private void CheckTrainGrid()
{
    // если поезда нет — ничего не делаем
    if (activeHellTrain == null || _trainLifecycle == null)
        return;

    // при желании можно добавить тут свои проверки (например уход из грида/декора)
    // сейчас просто «пинг», чтобы не падала компиляция
}


// FX взрыва + контролируемый AoE-урон вокруг каждого вагона
private void SpawnExplosionFXAndDamage()
{
    // Проходимся по всем нашим вагонам
    foreach (var car in _spawnedCars)
    {
        if (car == null || car.IsDestroyed) continue;

        // Визуальный эффект взрыва на каждом вагоне
        Effect.server.Run(
            "assets/bundled/prefabs/fx/explosions/explosion_03.prefab",
            car.transform.position,
            Vector3.up
        );

        // AoE-урон по окрестным сущностям (8м радиус)
        var ents = Pool.GetList<BaseCombatEntity>();
        Vis.Entities(car.transform.position, 8f, ents, Rust.Layers.Mask.Default);

        foreach (var e in ents)
        {
            if (e == null || e.IsDestroyed) continue;

            var hi = new HitInfo
            {
                damageTypes = new DamageTypeList()
            };
            hi.damageTypes.Add(DamageType.Explosion, 1000f);
            hi.PointStart = car.transform.position + Vector3.up * 0.5f;

            e.OnAttacked(hi);
        }

        Pool.FreeList(ref ents);
    }
}


private void ArmExplosionDamage()
{
    if (_explosionDamageArmed) return;
    _explosionDamageArmed = true;

    Puts("Explosion damage window ARMED (T-6s)");

    foreach (var car in _spawnedCars)
    {
        if (car == null || car.IsDestroyed) continue;

        var tc = car as TrainCar;
        if (tc == null) continue;

        var id = (uint)(tc.net?.ID.Value ?? 0UL);
if (id == 0U) continue;

        if (!_savedProtection.ContainsKey(id))
            _savedProtection[id] = tc.baseProtection;

        var allow = ScriptableObject.CreateInstance<ProtectionProperties>();
        allow.density = 100;
        allow.amounts = new float[]
        {
            1,1,1,1,1,1,1,1,1,1,
            1,1,1,1,1,1,1,1,1,1,
            1,1,1,1,1
        };

        tc.baseProtection = allow;
    }
}



private void SpawnC4OnTrain()
{
    int perWagon = Mathf.Max(1, config.C4PerWagon);
    float fuse = Mathf.Max(3f, config.ExplosionTimerSeconds);

    Vector3[] offsets = new Vector3[]
    {
        new Vector3(-2f, 0.5f, -2f),
        new Vector3( 2f, 0.5f, -2f),
        new Vector3(-2f, 0.5f,  2f),
        new Vector3( 2f, 0.5f,  2f),
        new Vector3( 0f, 0.5f,  0f)
    };

    foreach (var car in _spawnedCars)
    {
        var tc = car as TrainCar;
        if (tc == null || tc.IsDestroyed) continue;

        for (int i = 0; i < perWagon; i++)
        {
            Vector3 pos = tc.transform.TransformPoint(offsets[i % offsets.Length]);
            var c4 = GameManager.server.CreateEntity("assets/prefabs/tools/c4/explosive.timed.deployed.prefab", pos) as TimedExplosive;
            if (c4 == null) continue;

            c4.timerAmountMax = fuse;
            c4.timerAmountMin = fuse;
            c4.Spawn();
            c4.SetFuse(fuse);
        }
    }

    Puts($"💣 C4 заспавнены ({perWagon} на вагон), взрыв через {fuse:F0} сек...");
}


private void DestroyTrainAfterExplosion()
{
    if (_explodedOnce) return;           // защита от двойного вызова
    _explodedOnce = true;
SpawnExplosionFXAndDamage();
	StopEngineWatchdog();

    string trainName = _trainLifecycle != null
        ? config.CompositionNames[_trainLifecycle.CompositionType]
        : "Hell Train";
		
RestoreProtectionForAll();
    Server.Broadcast(config.Messages.Exploded.Replace("{trainName}", trainName));
    Puts("💥 Взрыв! Диспавн состава...");

    // Снести весь наш состав: все TrainCar, все крейты/NPC/турели/SAM и пр.
	
    try
    {
        // если где-то не все вагоны добавились в _spawnedCars — добьёмся по трекингу
        foreach (var e in _spawnedTrainEntities.ToArray())
        {
            if (e != null && !e.IsDestroyed) e.Kill();
            _spawnedTrainEntities.Remove(e);
        }

        foreach (var car in _spawnedCars.ToArray())
        {
            if (car != null && !car.IsDestroyed) car.Kill();
            _spawnedCars.Remove(car);
        }
    }
    finally
    {
        _spawnedTurrets.Clear();
        _spawnedSamSites.Clear();
        _spawnedNPCs.Clear();
        _savedProtection.Clear();
        _explosionDamageArmed = false;
        activeHellTrain = null;
        _trainLifecycle = null;
    }

    if (config.AutoRespawn)
        StartRespawnTimer();
}


private void StartRespawnTimer()
{
    if (respawnTimer != null)
        respawnTimer.Destroy();
    
    float minutes = config.TrainRespawnMinutes;
    respawnTimer = timer.Once(minutes * 60f, () => SpawnHellTrain());
    
    // ✅ ИЗМЕНЕНО: АНОНС СЛЕДУЮЩЕГО ПОЕЗДА
    string minutesWord = GetMinutesWord((int)minutes);
    string message = config.Messages.NextTrain
        .Replace("{minutes}", minutes.ToString("F0"))
        .Replace("{minutesWord}", minutesWord);
    
    Server.Broadcast(message);
    Puts($"⏳ Респавн через {minutes} минут");
}

#endregion
		

        #region HT.LAYOUT.LOADER
private readonly Dictionary<string, TrainLayout> _layouts = new Dictionary<string, TrainLayout>(System.StringComparer.OrdinalIgnoreCase);
private const string LayoutDir = "Helltrain/Layouts";

private class TrainLayout
{
    [JsonProperty("name")]
    public string name { get; set; }
    
    [JsonProperty("faction")]
    public string faction { get; set; }
    
    [JsonProperty("cars")]
    public List<CarSpec> cars { get; set; }
	

    // LEGACY: старые лэйауты (контент-объекты). Оставляем для чтения, не пишем редактором.
    [JsonProperty("objects")]
    public List<ObjSpec> objects { get; set; }

    // ✅ НОВЫЙ ФОРМАТ РЕДАКТОРА: СЛОТЫ (локальные)
    [JsonProperty("NpcSlots")]
    public List<SlotSpec> NpcSlots { get; set; }

    [JsonProperty("CrateSlots")]
    public List<SlotSpec> CrateSlots { get; set; }

    [JsonProperty("Shelves")]
    public List<ShelfSpec> Shelves { get; set; }
}

class SlotSpec
{
    [JsonProperty("pos")]
    public float[] pos; // local xyz

    [JsonProperty("rot")]
    public float[] rot; // local euler xyz

    // Ярлык пула китов (без весов). Если пусто — берём дефолт фракции из конфига.
    [JsonProperty("kitPool")]
    public string kitPool;

    // Ярлык ключа лута (без весов). Если пусто — берём дефолт фракции из конфига.
    [JsonProperty("lootKey")]
    public string lootKey;
}



private class ShelfSpec
{
    [JsonProperty("prefab")]
    public string prefab;

    [JsonProperty("pos")]
    public float[] pos; // local xyz

    [JsonProperty("rot")]
    public float[] rot; // local euler xyz
}


private class CarSpec
{
    [JsonProperty("type")]
    public string type;
    
    [JsonProperty("variant")]
    public string variant;
    
    // ✅ УБРАЛИ Type/Prefab — они не нужны!
}

private class ObjSpec
{
	
	[JsonIgnore]
public int ammoCount { get => ammo_count; set => ammo_count = value; }

	
    [JsonProperty("type")]
    public string type;
    
    [JsonProperty("faction")]
    public string faction;
    
    [JsonProperty("npc_type")]
    public string npc_type;
    
    [JsonProperty("kit")]
    public string kit;
    
    [JsonProperty("kits")]
    public List<string> kits;
    
    [JsonProperty("gun")]
    public string gun;
    
    [JsonProperty("ammo")]
    public string ammo;
    
    [JsonProperty("ammo_count")]
    public int ammo_count;
    
    [JsonProperty("preset")]
    public string preset;
    
    [JsonProperty("presets")]
    public string[] presets;
    
    [JsonProperty("position")]
    public float[] position;
    
    [JsonProperty("rotationY")]
    public float rotationY;
    
    // ✅ НОВОЕ ПОЛЕ ДЛЯ HP
    [JsonProperty("health")]
    public float health;
	
	[JsonProperty("hack_timer")]
public float hack_timer;
public float hack_timer_min = 0f;   // если >0 — нижняя граница
public float hack_timer_max = 0f;   // если >0 — верхняя граница
}

private static Vector3 V3(float[] p) => (p != null && p.Length == 3) ? new Vector3(p[0], p[1], p[2]) : Vector3.zero;

// ВСЁ ОСТАЛЬНОЕ В ЭТОМ РЕГИОНЕ ОСТАЁТСЯ БЕЗ ИЗМЕНЕНИЙ
// (CreateDefaultLayouts, LoadLayouts, GetLayout и т.д. - копируй как есть)

        private void CreateDefaultLayouts()
        {
            var dir = Path.Combine(Interface.Oxide.DataDirectory, LayoutDir);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            
            var defaults = new Dictionary<string, TrainLayout>
            {
                ["bandit_full"] = new TrainLayout 
                { 
                    name = "bandit_full", 
                    faction = "BANDIT", 
                    cars = new List<CarSpec> 
                    { 
                        new CarSpec { variant = "LOCO" },
                        new CarSpec { variant = "C" },
                        new CarSpec { variant = "C" }
                    } 
                },
                ["pmc_full"] = new TrainLayout 
                { 
                    name = "pmc_full", 
                    faction = "PMC", 
                    cars = new List<CarSpec> 
                    { 
                        new CarSpec { variant = "LOCO" },
                        new CarSpec { variant = "C" },
                        new CarSpec { variant = "C" },
                        new CarSpec { variant = "C" }
                    } 
                },
                ["coblab_full"] = new TrainLayout 
                { 
                    name = "coblab_full", 
                    faction = "COBLAB", 
                    cars = new List<CarSpec> 
                    { 
                        new CarSpec { variant = "LOCO" },
                        new CarSpec { variant = "C" },
                        new CarSpec { variant = "C" },
                        new CarSpec { variant = "C" },
                        new CarSpec { variant = "C" }
                    } 
                },
                
                ["wagonC_samsite"] = new TrainLayout { name = "wagonC_samsite", faction = "COBLAB", cars = new List<CarSpec> { new CarSpec { variant = "C" } } },
                ["wagonC_labcob"] = new TrainLayout { name = "wagonC_labcob", faction = "COBLAB", cars = new List<CarSpec> { new CarSpec { variant = "C" } } },
                ["wagonC_bradley"] = new TrainLayout { name = "wagonC_bradley", faction = "PMC", cars = new List<CarSpec> { new CarSpec { variant = "C" } } },
                ["wagonC_pmc"] = new TrainLayout { name = "wagonC_pmc", faction = "PMC", cars = new List<CarSpec> { new CarSpec { variant = "C" } } },
                ["wagonC_bandit"] = new TrainLayout { name = "wagonC_bandit", faction = "BANDIT", cars = new List<CarSpec> { new CarSpec { variant = "C" } } },
                
                ["loco_coblab"] = new TrainLayout { name = "loco_coblab", faction = "COBLAB", cars = new List<CarSpec> { new CarSpec { variant = "LOCO" } } },
                ["loco_bandit"] = new TrainLayout { name = "loco_bandit", faction = "BANDIT", cars = new List<CarSpec> { new CarSpec { variant = "LOCO" } } },
                ["loco_pmc"] = new TrainLayout { name = "loco_pmc", faction = "PMC", cars = new List<CarSpec> { new CarSpec { variant = "LOCO" } } },

                ["wagonA_bandit"] = new TrainLayout { name = "wagonA_bandit", faction = "BANDIT", cars = new List<CarSpec> { new CarSpec { variant = "A" } } },
                ["wagonA_labcob"] = new TrainLayout { name = "wagonA_labcob", faction = "COBLAB", cars = new List<CarSpec> { new CarSpec { variant = "A" } } },
                ["wagonA_pmc"] = new TrainLayout { name = "wagonA_pmc", faction = "PMC", cars = new List<CarSpec> { new CarSpec { variant = "A" } } },

                ["wagonB_bandit"] = new TrainLayout { name = "wagonB_bandit", faction = "BANDIT", cars = new List<CarSpec> { new CarSpec { variant = "B" } } },
                ["wagonB_labcob"] = new TrainLayout { name = "wagonB_labcob", faction = "COBLAB", cars = new List<CarSpec> { new CarSpec { variant = "B" } } },
                ["wagonB_pmc"] = new TrainLayout { name = "wagonB_pmc", faction = "PMC", cars = new List<CarSpec> { new CarSpec { variant = "B" } } }
            };
            
            foreach (var kv in defaults)
            {
                string filePath = Path.Combine(dir, $"{kv.Key}.json");
                if (!File.Exists(filePath))
                    File.WriteAllText(filePath, JsonConvert.SerializeObject(kv.Value, Formatting.Indented));
            }
        }

private void LoadLayouts()
{
    _layouts.Clear();
    var dir = Path.Combine(Interface.Oxide.DataDirectory, LayoutDir);
    if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir);

    Puts($"📂 Загружаем layouts из: {dir}");

    foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
    {
        try
        {
            var json = File.ReadAllText(file, System.Text.Encoding.UTF8);
            
            // ✅ КРИТИЧНО: Логируем размер файла!
            Puts($"📄 Файл: {Path.GetFileName(file)} ({json.Length} байт)");
            
            var settings = new JsonSerializerSettings
            {
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                DefaultValueHandling = DefaultValueHandling.Populate,
                NullValueHandling = NullValueHandling.Ignore
            };
            
            var layout = JsonConvert.DeserializeObject<TrainLayout>(json, settings);
            
            if (layout == null)
            {
                PrintWarning($"⚠️ Layout NULL после десериализации: {Path.GetFileName(file)}");
                continue;
            }
            
            if (string.IsNullOrEmpty(layout.name))
            {
                PrintWarning($"⚠️ Layout.name пусто: {Path.GetFileName(file)}");
                continue;
            }
            
            // ✅ КРИТИЧНО: Проверяем objects!
            int objCount = layout.objects?.Count ?? 0;
            Puts($"   📦 {layout.name}: {objCount} objects (null={layout.objects == null})");
            
            var fileKey = Path.GetFileNameWithoutExtension(file);

// STRICT: file name must match layout.name
if (!layout.name.Equals(fileKey, StringComparison.OrdinalIgnoreCase))
{
    PrintError($"[Helltrain][LAYOUT_LOAD][MISMATCH] fileKey='{fileKey}' != layout.name='{layout.name}'");
    AbortRequest($"Layout load error: name mismatch (fileKey='{fileKey}', layout.name='{layout.name}')",
    _activeFactionKey ?? "BOOT",
    fileKey,
    "BOOT");
return;

}

// STRICT: no duplicate keys allowed
if (_layouts.ContainsKey(layout.name))
{
    PrintError($"[Helltrain][LAYOUT_LOAD][DUPLICATE] layout.name='{layout.name}' already loaded");
    AbortRequest($"Layout load error: duplicate key (layout.name='{layout.name}')",
    _activeFactionKey ?? "BOOT",
    layout.name,
    "BOOT");
return;

}

_layouts[layout.name] = layout;

        }
        catch (System.Exception e)
        {
            PrintError($"❌ Ошибка загрузки {Path.GetFileName(file)}: {e.Message}");
        }
    }

    Puts($"✅ Всего загружено layouts: {_layouts.Count}");
}

public void ReloadSingleLayout(string layoutName, string filePath)
{
    try
    {
        Puts($"🔄 Перезагружаю ТОЛЬКО layout: {layoutName}");
        
        var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
        
        var settings = new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            DefaultValueHandling = DefaultValueHandling.Populate,
            NullValueHandling = NullValueHandling.Ignore
        };
        
        var layout = JsonConvert.DeserializeObject<TrainLayout>(json, settings);
        
        if (layout == null || string.IsNullOrEmpty(layout.name))
        {
            PrintWarning($"❌ Не удалось загрузить layout: {layoutName}");
            return;
        }
        
        // ✅ Обновляем ТОЛЬКО этот layout в кеше!
        _layouts[layout.name] = layout;
        
        Puts($"✅ Layout '{layout.name}' обновлён в кеше ({layout.objects?.Count ?? 0} объектов)");
    }
    catch (System.Exception e)
    {
        PrintError($"❌ Ошибка перезагрузки layout '{layoutName}': {e.Message}");
    }
}

        private TrainLayout GetLayout(string name)
        {
            TrainLayout l;
            return _layouts.TryGetValue(name, out l) ? l : null;
        }

        private TrainLayout ChooseFactionLayout(string faction)
        {
            if (_layouts.Count == 0) return null;
            foreach (var kv in _layouts)
                if (!string.IsNullOrEmpty(kv.Value.faction) && kv.Value.faction.Equals(faction, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            
            using (var en = _layouts.Values.GetEnumerator())
                return en.MoveNext() ? en.Current : null;
        }

private string ParseWagonVariantFromKey(string wagonKey)
{
    if (string.IsNullOrEmpty(wagonKey)) return null;

    // ожидаем wagonA_ / wagonB_ / wagonC_
    var m = System.Text.RegularExpressions.Regex.Match(
        wagonKey,
        @"^wagon([ABC])_",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    );

    if (!m.Success) return null;
    return m.Groups[1].Value.ToUpperInvariant();
}


        private string GetWagonPrefabByVariant(string variant)
        {
            var v = variant?.ToUpperInvariant();
            if (string.IsNullOrEmpty(v)) return WagonPrefabC;

            // Поддержка вариантов типа "loco_bandit"/"loco_pmc"/"loco_coblab"
            if (v.StartsWith("LOCO")) return EnginePrefab;

            switch (v)
            {
                case "A": return WagonPrefabA;
                case "B": return WagonPrefabB;
                case "C": return WagonPrefabC;
                case "LOOT": return WagonPrefabLoot;
                case "EMPTY": return WagonPrefabUnloaded;
                default: return WagonPrefabC;
            }
        }


        private TrainLayout ResolveLayoutArg(string[] args)
        {
            if (args != null && args.Length > 1 && !string.IsNullOrEmpty(args[1]))
            {
                var byName = GetLayout(args[1]);
                if (byName != null) return byName;
            }
            if (args != null && args.Length > 0)
            {
                var faction = args[0];
                var byFaction = ChooseFactionLayout(faction);
                if (byFaction != null) return byFaction;
            }
            
            using (var en = _layouts.Values.GetEnumerator())
                return en.MoveNext() ? en.Current : null;
        }
        #endregion

        #region HT.TRAIN.ASSEMBLY
        private const float CAR_SPACING = 8f;
        private readonly List<BaseEntity> _spawnedCars = new List<BaseEntity>();
private readonly List<AutoTurret> _spawnedTurrets = new List<AutoTurret>();
private readonly List<SamSite> _spawnedSamSites = new List<SamSite>();
private readonly List<ScientistNPC> _spawnedNPCs = new List<ScientistNPC>();
private Timer _lifecycleTimer = null;
 // Окно реального урона и кэш исходной защиты
 private bool _explosionDamageArmed = false;
 private readonly Dictionary<uint, ProtectionProperties> _savedProtection = new Dictionary<uint, ProtectionProperties>();


        private BaseEntity SpawnCar(string prefab, TrainTrackSpline track, float distOnSpline)
        {
            Vector3 position = track.GetPosition(distOnSpline);
            Vector3 forward = track.GetTangentCubicHermiteWorld(distOnSpline);
            
            if (forward.sqrMagnitude < 0.001f) 
                forward = Vector3.forward;
            
            Quaternion rotation = Quaternion.LookRotation(forward);
            
            TrainCar trainCar = GameManager.server.CreateEntity(prefab, position, rotation) as TrainCar;
            if (trainCar == null)
            {
                PrintError($"❌ CreateEntity вернул null для {prefab}");
                return null;
            }
            
            trainCar.enableSaving = false;
trainCar.OwnerID = HELL_OWNER_ID;

if (trainCar is TrainEngine engine)
{
    engine.engineForce = 250000f;
    engine.maxSpeed = 18f;
    engine.OwnerID = HELL_OWNER_ID;
}

trainCar.Spawn();

            
            if (!trainCar || trainCar.IsDestroyed)
            {
                PrintError($"❌ TrainCar destroyed после Spawn!");
                return null;
            }
            
            trainCar.CancelInvoke(trainCar.DecayTick);
            
            if (trainCar is TrainEngine eng)
            {
                eng.SetFlag(BaseEntity.Flags.On, false);
                eng.SetThrottle(TrainEngine.EngineSpeeds.Zero);
            }
            
            if (trainCar.FrontTrackSection != null)
            {
               // Puts($"   🔧 Выровнен на {trainCar.FrontTrackSection.name} @ {trainCar.FrontWheelSplineDist:F1}м");
            }
            
            NextTick(() =>
            {
                if (trainCar == null || trainCar.IsDestroyed) return;
                
                if (trainCar.platformParentTrigger != null)
                    trainCar.platformParentTrigger.ParentNPCPlayers = true;
            });
            _spawnedTrainEntities.Add(trainCar);
            _spawnedCars.Add(trainCar);
            return trainCar;
        }

        private BaseEntity SpawnCar(string prefab, Vector3 pos, Quaternion rot)
        {
            TrainCar trainCar = GameManager.server.CreateEntity(prefab, pos, rot) as TrainCar;
            if (trainCar == null)
            {
                PrintError($"❌ CreateEntity вернул null для {prefab}");
                return null;
            }
            
            trainCar.enableSaving = false;
trainCar.OwnerID = HELL_OWNER_ID;

if (trainCar is TrainEngine engine)
{
    engine.engineForce = 250000f;
    engine.maxSpeed = 18f;
    engine.OwnerID = HELL_OWNER_ID;
}

trainCar.Spawn();

            
            if (!trainCar || trainCar.IsDestroyed)
            {
                PrintError($"❌ TrainCar destroyed после Spawn!");
                return null;
            }
            
            trainCar.CancelInvoke(trainCar.DecayTick);
            
            if (trainCar is TrainEngine eng)
            {
                eng.SetFlag(BaseEntity.Flags.On, false);
                eng.SetThrottle(TrainEngine.EngineSpeeds.Zero);
            }
            
            NextTick(() =>
            {
                if (trainCar == null || trainCar.IsDestroyed) return;
                
                if (trainCar.platformParentTrigger != null)
                    trainCar.platformParentTrigger.ParentNPCPlayers = true;
            });
            _spawnedTrainEntities.Add(trainCar);
            _spawnedCars.Add(trainCar);
            return trainCar;
        }

        private TrainEngine SpawnTrainFromComposition(
            string compositionName, 
            TrainTrackSpline targetTrack,
            float targetDist
        )
        {
            var factionKey = _activeFactionKey;


// overrideKey: если команда /helltrain start . [layout?] дает override — передать сюда.
// если нет — оставить null.
string overrideKey = null;

if (!Gen_ResolveCompositionKey(factionKey, overrideKey, out var compositionKey, out var wagons, out var resolveReason))
{
    AbortRequest(resolveReason ?? "ResolveCompositionKey failed", factionKey, compositionName, compositionKey ?? "null");
    ProcessAbortIfRequested("resolve");
    return null;
}


_lastResolvedCompositionKey = compositionKey;

if (!config.Compositions.TryGetValue(compositionKey, out var comp) || comp == null)
{
    AbortRequest("COMPOSITION_NOT_FOUND_IN_CONFIG", factionKey, compositionName, compositionKey);
    ProcessAbortIfRequested("composition");
    return null;
}

// существующая логика формирования wagonNames (без новых весов/рандома добавлять нельзя)
// 🟢 MAIN v6: CORE больше не рандомит состав.
// comp.Wagons = BuildRandomCompositionWagons(comp);

// 🔴 CONTRACT GUARD: если Resolve вернул ok=true, wagons обязаны быть не пустыми.
if (wagons == null || wagons.Count == 0)
{
    AbortRequest("RESOLVE_WAGONS_EMPTY_CONTRACT",
        factionKey, compositionName, compositionKey);
    ProcessAbortIfRequested("wagons_empty_contract");
    return null;
}

if (!Gen_ValidateWagons(factionKey, wagons, out var validateReason))
{
    AbortRequest(validateReason ?? "ValidateWagons failed",
        factionKey, compositionName, compositionKey);
    ProcessAbortIfRequested("validate");
    return null;
}



ServerMgr.Instance.StartCoroutine(BuildTrainWithSpline(compositionName, comp, wagons, targetTrack, targetDist));





            
                        return null;
        }

        private IEnumerator BuildTrainWithSpline(
    string compositionName,
    ConfigData.TrainComposition comp,
    List<string> wagons,
    TrainTrackSpline track,
    float splineDist
)

        {
            if (_isBuildingTrain)
            {
                PrintWarning($"[Helltrain] Duplicate BuildTrainWithSpline blocked: {compositionName}");
                yield break;
            }


    _isBuildingTrain = true;
	// 40 секунд иммунитета anti-stuck после спавна состава
_antiStuckIgnoreUntil = Time.realtimeSinceStartup + 40f;

    var prevSuppress = _suppressHooks;
    _suppressHooks = true;

    try
    {
        // ✅ ОЧИСТКА СТАРЫХ ВАГОНОВ ПЕРЕД СБОРКОЙ НОВОГО ПОЕЗДА!
        foreach (var entity in _spawnedCars.ToArray())
        {
            if (entity != null && !entity.IsDestroyed)
                entity.Kill(BaseNetworkable.DestroyMode.None);
        }

        _spawnedCars.Clear();
        _spawnedTrainEntities.Clear();

        // дальше код метода — без изменений


    
  //  Puts($"🔧 Собираем композицию: {comp.Wagons.Count} вагонов...");
            
    const float SPACING_DISTANCE = 20f;
    
    string firstWagonName = wagons.Count > 0 ? wagons[0] : null;
    var firstLayout = !string.IsNullOrEmpty(firstWagonName) ? GetLayout(firstWagonName) : null;
    
    bool firstIsLoco = false;
    if (firstLayout != null && firstLayout.cars != null && firstLayout.cars.Count > 0)
    {
        var firstCar = firstLayout.cars[0];
        firstIsLoco = (firstCar.type?.ToLower() == "locomotive" || firstCar.variant == "LOCO");
    }
    
    int wagonStartIndex = firstIsLoco ? 1 : 0;
    
    List<SpawnPosition> spawnPositions = new List<SpawnPosition>();
    
    TrainTrackSpline currentTrack = track;
    Vector3 currentPosition = currentTrack.GetPosition(splineDist);
    Vector3 currentForward = currentTrack.GetTangentCubicHermiteWorld(splineDist);
    
    spawnPositions.Add(new SpawnPosition(currentPosition, currentForward));
    
    for (int i = wagonStartIndex; i < wagons.Count; i++)
    {
        TrainTrackSpline.MoveResult result = currentTrack.MoveAlongSpline(
            splineDist, 
            currentForward, 
            SPACING_DISTANCE
        );
        
        currentTrack = result.spline;
        splineDist = result.distAlongSpline;
        currentPosition = currentTrack.GetPosition(splineDist);
        currentForward = currentTrack.GetTangentCubicHermiteWorld(splineDist);
        
        spawnPositions.Add(new SpawnPosition(currentPosition, currentForward));
    }
    
    //Puts($"✅ Рассчитано {spawnPositions.Count} позиций");

    string locoPrefab = EnginePrefab;

    if (firstIsLoco && firstLayout != null && firstLayout.cars != null && firstLayout.cars.Count > 0)
    {
        locoPrefab = GetWagonPrefabByVariant(firstLayout.cars[0].variant);
        Puts($"🚂 Используем локомотив из лэйаута: {firstWagonName}");
    }

    TrainCar locoEnt = GameManager.server.CreateEntity(
        locoPrefab, 
        spawnPositions[0].Position, 
        spawnPositions[0].Rotation
    ) as TrainCar;
    
	if (locoEnt == null)
{
    PrintError($"[Helltrain] ❌ Loco CreateEntity вернул null. prefab={locoPrefab}");
    yield break;
}

	
    locoEnt.enableSaving = false;
	
    
    if (locoEnt is TrainEngine engine)
    {
        engine.engineForce = 250000f;
        engine.maxSpeed = 18f;
		engine.OwnerID = HELL_OWNER_ID;
    }
    else
{
    PrintError($"[Helltrain] ❌ Префаб локомотива не является TrainEngine. prefab={locoPrefab} type={(locoEnt != null ? locoEnt.GetType().Name : "null")}");
    // чтобы не пытаться сцеплять вагоны с "не локомотивом"
    locoEnt.Kill();
    yield break;
}

	
    locoEnt.Spawn();
    locoEnt.OwnerID = HELL_OWNER_ID;
locoEnt.SendNetworkUpdate();
	
    NextTick(() =>
    {
        if (locoEnt != null && !locoEnt.IsDestroyed && locoEnt.platformParentTrigger != null)
            locoEnt.platformParentTrigger.ParentNPCPlayers = true;
    });
    
    locoEnt.CancelInvoke(locoEnt.DecayTick);
    
    TrainEngine trainEngine = locoEnt as TrainEngine;
    TrainCar lastSpawnedCar = locoEnt;

  //  Puts($"🚂 Локомотив создан, ID: {locoEnt.net.ID}");

    _spawnedCars.Add(locoEnt);
	_spawnedTrainEntities.Add(locoEnt);


    yield return new WaitForSeconds(0.5f);
    
    int positionIndex = 1;

    for (int i = wagonStartIndex; i < wagons.Count; i++)
{
    string wagonName = wagons[i];
    var layout = GetLayout(wagonName);

    var parsedVariant = ParseWagonVariantFromKey(wagonName);
    if (string.IsNullOrEmpty(parsedVariant))
    {
        PrintError($"[Helltrain][WAGONKEY_INVALID] i={i} wagonKey='{wagonName}' => cannot parse ^wagon([ABC])_ (fail-fast)");
        AbortRequest("WAGONKEY_INVALID", _activeFactionKey, wagonName, _lastResolvedCompositionKey ?? compositionName);
        yield break;
    }

    // ✅ ИНВАРИАНТ: геометрия вагона определяется ТОЛЬКО wagonKey
    string prefab = GetWagonPrefabByVariant(parsedVariant);

    var foundName = layout?.name ?? "NULL";
    var layoutVariant = (layout != null && layout.cars != null && layout.cars.Count > 0) ? (layout.cars[0].variant ?? "NULL") : "NULL";

    Puts($"[Helltrain][DBG_RESOLVE_LAYOUT] i={i} wagonKey='{wagonName}' found='{foundName}' layoutVariant='{layoutVariant}' parsedVariant='{parsedVariant}' prefab='{prefab}'");

    // layoutVariant — только визуальная метка/отладка
if (!string.IsNullOrEmpty(layoutVariant) && layoutVariant != "NULL" && !layoutVariant.Equals(parsedVariant, StringComparison.OrdinalIgnoreCase))
    PrintWarning($"[Helltrain][WAGON_GEOM_MISMATCH] i={i} wagonKey='{wagonName}' parsedVariant='{parsedVariant}' layoutVariant='{layoutVariant}' (layout.variant is visual/debug only)");

if (layout == null)
    PrintError($"[Helltrain][DBG_NO_LAYOUT] i={i} picked='{wagonName}' => default prefabC='{prefab}'");



        // Жёсткая защита от "левых" префабов (без куплингов и т.п.)
        if (prefab != WagonPrefabA && prefab != WagonPrefabB && prefab != WagonPrefabC && prefab != WorkcartPrefab)
        {
            PrintError($"❌ [{i}] НЕВАЛИДНЫЙ prefab вагона: '{prefab}' (wagonName='{wagonName}'). Остановка сборки.");
            KillEventTrainCars($"Invalid wagon prefab: {prefab}", force: true);
            yield break;
        }
        
        if (positionIndex >= spawnPositions.Count)

        {
            PrintError($"❌ Кончились позиции! Вагон [{i}] не будет создан");
            break;
        }
        
        TrainCar trainCar = GameManager.server.CreateEntity(
            prefab, 
            spawnPositions[positionIndex].Position, 
            spawnPositions[positionIndex].Rotation
        ) as TrainCar;
        
        if (trainCar == null)
        {
            PrintError($"❌ Не удалось создать вагон [{i}]");
            continue;
        }
        
        trainCar.enableSaving = false;
trainCar.Spawn();
trainCar.OwnerID = HELL_OWNER_ID;
trainCar.SendNetworkUpdate();

        
        NextTick(() =>
        {
            if (trainCar != null && !trainCar.IsDestroyed && trainCar.platformParentTrigger != null)
                trainCar.platformParentTrigger.ParentNPCPlayers = true;
        });
        
        if (trainCar.IsDestroyed)
        {
            PrintError($"❌ Вагон [{i}] destroyed после Spawn");
            continue;
        }
        
        trainCar.CancelInvoke(trainCar.DecayTick);
        
      //  Puts($"   🔧 [{i}] {wagonName}: {trainCar.ShortPrefabName} (ID: {trainCar.net.ID})");
                _spawnedTrainEntities.Add(trainCar);
        _spawnedCars.Add(trainCar);

        // WAIT-UNTIL-READY (вместо фиксированного 0.2s): даём Unity/Entity инициализировать рельсы/куплинги
        // Таймаут короткий, дальше fail-fast с причиной (чтобы не было "костыля 40 секунд")
        const float coupleReadyTimeout = 3f;
        float coupleReadyStart = Time.realtimeSinceStartup;
        string coupleMissing = null;
        while (true)
        {
            coupleMissing = null;

            // Текущий вагон должен быть на рельсах и иметь front*
            if (trainCar == null || trainCar.IsDestroyed) coupleMissing = "cur:null/destroyed";
            else if (trainCar.FrontTrackSection == null) coupleMissing = "cur:FrontTrackSection";
            else if (trainCar.frontCoupling == null) coupleMissing = "cur:frontCoupling";
            else if (trainCar.coupling == null) coupleMissing = "cur:coupling";
            else if (trainCar.coupling.frontCoupling == null) coupleMissing = "cur:coupling.frontCoupling";

            // Предыдущий вагон должен быть на рельсах и иметь rear*
            else if (lastSpawnedCar == null || lastSpawnedCar.IsDestroyed) coupleMissing = "prev:null/destroyed";
            else if (lastSpawnedCar.FrontTrackSection == null) coupleMissing = "prev:FrontTrackSection";
            else if (lastSpawnedCar.rearCoupling == null) coupleMissing = "prev:rearCoupling";
            else if (lastSpawnedCar.coupling == null) coupleMissing = "prev:coupling";
            else if (lastSpawnedCar.coupling.rearCoupling == null) coupleMissing = "prev:coupling.rearCoupling";

            if (coupleMissing == null) break;

            if (Time.realtimeSinceStartup - coupleReadyStart >= coupleReadyTimeout)
            {
                PrintError($"❌ [{i}] Coupling init timeout {coupleReadyTimeout:F1}s missing='{coupleMissing}' curPrefab='{prefab}' wagonName='{wagonName}' prev='{lastSpawnedCar?.ShortPrefabName}'");
                KillEventTrainCars($"Coupling init timeout: {coupleMissing}", force: true);
                yield break;
            }

            yield return null; // ждём следующий кадр
        }

        // Жёсткие проверки: если нет рельс/куплингов — сразу чистим и выходим (иначе "поезд через жопу")

        if (trainCar.FrontTrackSection == null)
        {
            PrintError($"❌ [{i}] Вагон НЕ привязан к рельсам! prefab='{prefab}' wagonName='{wagonName}'");
            KillEventTrainCars($"Wagon not on track: {prefab}", force: true);
            yield break;
        }

        if (lastSpawnedCar == null || lastSpawnedCar.IsDestroyed)
        {
            PrintError($"❌ [{i}] lastSpawnedCar уничтожен/NULL перед сцепкой. prefab='{prefab}' wagonName='{wagonName}'");
            KillEventTrainCars("lastSpawnedCar invalid", force: true);
            yield break;
        }

        // Проверяем ТРАНСФОРМЫ куплингов (rear/front)
        if (lastSpawnedCar.rearCoupling == null)
        {
            PrintError($"❌ [{i}] У предыдущего вагона НЕТ rearCoupling! prev='{lastSpawnedCar.ShortPrefabName}' prefab='{prefab}' wagonName='{wagonName}'");
            KillEventTrainCars($"Missing rearCoupling: {lastSpawnedCar.ShortPrefabName}", force: true);
            yield break;
        }

        if (trainCar.frontCoupling == null)
        {
            PrintError($"❌ [{i}] У текущего вагона НЕТ frontCoupling! curPrefab='{prefab}' wagonName='{wagonName}'");
            KillEventTrainCars($"Missing frontCoupling: {prefab}", force: true);
            yield break;
        }

        // Проверяем КОМПОНЕНТЫ сцепки (coupling.frontCoupling / coupling.rearCoupling)
        if (lastSpawnedCar.coupling == null || lastSpawnedCar.coupling.rearCoupling == null)
        {
            PrintError($"❌ [{i}] У предыдущего вагона НЕТ coupling.rearCoupling! prev='{lastSpawnedCar.ShortPrefabName}'");
            KillEventTrainCars($"Missing coupling.rearCoupling: {lastSpawnedCar.ShortPrefabName}", force: true);
            yield break;
        }

        if (trainCar.coupling == null || trainCar.coupling.frontCoupling == null)
        {
            PrintError($"❌ [{i}] У текущего вагона НЕТ coupling.frontCoupling! curPrefab='{prefab}' wagonName='{wagonName}'");
            KillEventTrainCars($"Missing coupling.frontCoupling: {prefab}", force: true);
            yield break;
        }

        
        float distToMove = Vector3Ex.Distance2D(
            lastSpawnedCar.rearCoupling.position, 
            trainCar.frontCoupling.position
        );
        
       // Puts($"   📏 [{i}] Расстояние между сцепками: {distToMove:F2}м");
        
        trainCar.MoveFrontWheelsAlongTrackSpline(
            trainCar.FrontTrackSection, 
            trainCar.FrontWheelSplineDist, 
            distToMove,
            null, 
            0
        );
        
        
        
        bool coupled = trainCar.coupling.frontCoupling.TryCouple(
            lastSpawnedCar.coupling.rearCoupling, 
            true
        );
        
       // Puts($"   {(coupled ? "✅" : "❌")} Сцепка: {lastSpawnedCar.ShortPrefabName} ↔ {trainCar.ShortPrefabName}");
        
        lastSpawnedCar = trainCar;
        positionIndex++;
    }
    
    if (lastSpawnedCar != null && lastSpawnedCar != locoEnt && lastSpawnedCar.rearCoupling != null)
    {
        lastSpawnedCar.rearCoupling = null;
       // Puts($"   🔒 Задняя сцепка отключена для последнего вагона");
    }
    
    yield return new WaitForSeconds(1f);
    
    switch (comp.Tier)
    {
        case ConfigData.TrainTier.LIGHT:
            trainEngine.maxSpeed = config.Speed.TierLight;
            break;
        case ConfigData.TrainTier.MEDIUM:
            trainEngine.maxSpeed = config.Speed.TierMedium;
            break;
        case ConfigData.TrainTier.HEAVY:
            trainEngine.maxSpeed = config.Speed.TierHeavy;
            break;
    }
    trainEngine.engineForce = 250000f;
    
    EntityFuelSystem fuelSystem = trainEngine.GetFuelSystem() as EntityFuelSystem;
    if (fuelSystem != null)
    {
        fuelSystem.AddFuel(500);
        fuelSystem.GetFuelContainer()?.SetFlag(BaseEntity.Flags.Locked, true);
    }
    
    activeHellTrain = trainEngine;
    
    var antiStuckComponent = trainEngine.gameObject.AddComponent<HellTrainComponent>();
    antiStuckComponent.plugin = this;
    antiStuckComponent.engine = trainEngine;
    
    // ✅ BUILD POPULATE PLAN ДО spawn layout objects (иначе crateSlots исполняются без assignments)
var factionKey = _activeFactionKey;


// ✅ ВАЖНО: Спавним объекты С ЗАДЕРЖКОЙ после полной сборки поезда!
yield return new WaitForSeconds(12f);

// ✅ PLAN PIPE (order fix): build PopulatePlan only AFTER train is fully built and slots are available
// inferredLayoutName как имя лэйаута.
string inferredLayoutName = null;
if (wagons != null && wagons.Count > 0)
{
    int idx = (wagonStartIndex >= 0 && wagonStartIndex < wagons.Count) ? wagonStartIndex : 0;
    inferredLayoutName = wagons[idx];
}

var layoutName = !string.IsNullOrEmpty(_activeLayoutName) ? _activeLayoutName : inferredLayoutName;
var compositionKey = _lastResolvedCompositionKey ?? "null";

if (!Gen_BuildPopulatePlan(factionKey, compositionKey, layoutName, _spawnedCars, out var planObj, out var planReason))
{
    AbortRequest(planReason ?? "BuildPopulatePlan failed", factionKey, layoutName, compositionKey);
    ProcessAbortIfRequested("buildplan");
    yield break;
}

int gotPlanSlots = PlanPipe_GetPlanSlots(planObj);
Puts($"[PLAN PIPE] gotPlan={(planObj != null ? "true" : "false")} planSlots={gotPlanSlots} src=BuildTrain");

ApplyPopulatePlan(planObj);
Puts($"[Helltrain][PLAN OK] faction={factionKey} layout={layoutName} compositionKey={compositionKey}");
    
    // Спавним объекты на локомотив
    if (firstIsLoco && firstLayout != null)
    {
        SpawnLayoutObjects(locoEnt, firstLayout);
        Puts($"   🎯 Объекты локомотива заспавнены из лэйаута: {firstWagonName}");
    }
    
    // Спавним объекты на вагоны
    positionIndex = 1;
    for (int i = wagonStartIndex; i < wagons.Count; i++)
    {
        if (positionIndex >= _spawnedCars.Count)
            break;
        
            string wagonName = wagons[i];
var wagonLayout = GetLayout(wagonName);
var foundName = wagonLayout?.name ?? "NULL";
var foundVar = (wagonLayout != null && wagonLayout.cars != null && wagonLayout.cars.Count > 0)
    ? (wagonLayout.cars[0].variant ?? "NULL")
    : "NULL";
Puts($"[Helltrain][DBG_RESOLVE_LAYOUT] i={i} picked='{wagonName}' found='{foundName}' variant='{foundVar}'");


        
        if (wagonLayout != null)
        {
            TrainCar wagonCar = _spawnedCars[positionIndex] as TrainCar;
            if (wagonCar != null && !wagonCar.IsDestroyed)
            {
                SpawnLayoutObjects(wagonCar, wagonLayout);
              //  Puts($"   🎯 Объекты вагона [{i}] заспавнены из лэйаута: {wagonName}");
            }
        }
        
        positionIndex++;
        yield return new WaitForSeconds(0.1f);
    }
    
    yield return new WaitForSeconds(20f);
    StartEngine(trainEngine);
	
// ✅ ИНИЦИАЛИЗАЦИЯ LIFECYCLE
_trainLifecycle = new TrainLifecycle(
    compositionName,
    trainEngine.transform.position,
    this
);

string trainName = config.CompositionNames[_trainLifecycle.CompositionType];

// ✅ ИЗМЕНЕНО: АНОНС СПАВНА ИЗ КОНФИГА
string spawnMessage = config.Messages.TrainSpawned
    .Replace("{trainName}", trainName)
    .Replace("{grid}", _trainLifecycle.LastGrid);

Server.Broadcast(spawnMessage);

StopGridCheckTimer();
_gridCheckTimer = timer.Repeat(10f, 0, CheckTrainGrid);

StartLifecycleTimer();
StartEngineWatchdog();

Puts($"✅ Hell Train готов! Вагонов: {wagons.Count - wagonStartIndex}");

    }
    finally
    {
        _suppressHooks = prevSuppress;
        _isBuildingTrain = false;
    }
}
        private struct SpawnPosition
        {
            public Vector3 Position;
            public Vector3 Forward;

            public Quaternion Rotation => Forward.magnitude == 0f 
                ? Quaternion.identity * Quaternion.Euler(0f, 180f, 0f) 
                : Quaternion.LookRotation(Forward) * Quaternion.Euler(0f, 180f, 0f);

            public SpawnPosition(Vector3 position, Vector3 forward)
            {
                this.Position = position;
                this.Forward = forward;
            }
        }

        private TrainEngine SpawnTrainFromLayout(TrainLayout layout, Vector3 origin, Quaternion facing)
        {
          //  Puts($"🔧 [SpawnLayout] Layout: {layout.name}, Cars: {layout.cars?.Count ?? 0}");
            
            Vector3 fwd = facing * Vector3.forward;
            BaseEntity last = null;
            TrainEngine engine = null;
            float offset = 0f;

            if (layout.cars == null || layout.cars.Count == 0)
            {
                PrintWarning("⚠️ Layout has no cars!");
                return null;
            }

            foreach (var car in layout.cars)
{
    string prefab = null;
    
    if (car.type?.ToLower() == "locomotive" || car.variant == "LOCO")
        prefab = EnginePrefab;
    else
        prefab = GetWagonPrefabByVariant(car.variant);
    
    Vector3 pos = origin - fwd * offset;
    var carEnt = SpawnCar(prefab, pos, facing);
    
    if (carEnt == null)
    {
        PrintWarning($"⚠️ Spawn failed: {car.type ?? car.variant}");
        continue;
    }

    if (engine == null && carEnt is TrainEngine)
        engine = carEnt as TrainEngine;

    if (last != null)
        CoupleCars(last, carEnt);

    // ✅ СПАВНИМ ОБЪЕКТЫ НА ВАГОНЕ!
    TrainCar trainCar = carEnt as TrainCar;
    if (trainCar != null)
    {
        SpawnLayoutObjects(trainCar, layout);
    }

    last = carEnt;
    offset += CAR_SPACING;
}

            if (engine == null)
            {
                PrintError("❌ No locomotive in layout!");
                return null;
            }

          //  Puts($"✅ Train assembled! Cars: {layout.cars.Count}");
            return engine;
        }
        #endregion

       #region HT.SPAWN.TRAIN

// ✅ НОВОЕ: WEIGHTED RANDOM ВЫБОР КОМПОЗИЦИИ
private string ChooseWeightedComposition()
{
    int totalWeight = config.Compositions.Values.Sum(c => c.Weight);
    
    if (totalWeight <= 0)
    {
        PrintWarning("⚠️ Суммарный вес композиций = 0! Выбираю первую.");
        return config.Compositions.Keys.First();
    }
    
    int random = _rng.Next(0, totalWeight);
    
    foreach (var kv in config.Compositions)
    {
        random -= kv.Value.Weight;
        if (random < 0)
        {
       //    Puts($"🎲 Выбрана композиция: {kv.Key} (вес: {kv.Value.Weight}/{totalWeight})");
            return kv.Key;
        }
    }
    
    return config.Compositions.Keys.First();
}

private void SpawnHellTrain(BasePlayer player = null)
{
	// reset crate state (антиспам + первый ящик)
    if (config.Compositions.Count == 0)
    {
        PrintError("❌ Нет композиций в конфиге!");
        return;
    }

    // ✅ ИЗМЕНЕНО: ИСПОЛЬЗУЕМ WEIGHTED RANDOM
    string chosen = ChooseWeightedComposition();

    if (activeHellTrain != null && !activeHellTrain.IsDestroyed)
    {
        activeHellTrain.Kill();
        activeHellTrain = null;
    }

    int overworldCount = availableOverworldSplines.Count;
    int underworldCount = availableUnderworldSplines.Count;
    
    if (overworldCount == 0 && underworldCount == 0)
    {
        PrintError("❌ Нет доступных треков! Проверь AllowAboveGround/AllowUnderGround в конфиге.");
        return;
    }
    
    bool useUnderground = underworldCount > 0 && (overworldCount == 0 || UnityEngine.Random.value > 0.5f);
    
    List<TrainTrackSpline> tracksToUse = useUnderground ? availableUnderworldSplines : availableOverworldSplines;
    
    int maxAttempts = Mathf.Min(10, tracksToUse.Count);
    
    for (int attempt = 0; attempt < maxAttempts; attempt++)
    {
        TrainTrackSpline trackSpline = tracksToUse[UnityEngine.Random.Range(0, tracksToUse.Count)];
        float length = trackSpline.GetLength();
        
        if (length < config.MinTrackLength)
        {
          //  Puts($"⚠️ Попытка {attempt + 1}/{maxAttempts}: трек {trackSpline.name} слишком короткий ({length:F0}м)");
            continue;
        }
        
        float start = length * 0.15f;
        float end = length * 0.85f;
        float distOnSpline = UnityEngine.Random.Range(start, end);
        
      //  Puts($"🎲 Попытка {attempt + 1}: {(useUnderground ? "подземный" : "наземный")} трек: {trackSpline.name}");
      //  Puts($"🎲 Длина трека: {length:F0}м, позиция: {distOnSpline:F1}м");
_activeLayoutName = null; // авто-спавн не форсит layout файл

        SpawnTrainFromComposition(chosen, trackSpline, distOnSpline);
        
        Puts($"✅ Запущена сборка Hell Train: {chosen}");
        return;
    }
    
    PrintError($"❌ Не удалось найти подходящий трек за {maxAttempts} попыток!");
    
    if (config.AutoRespawn)
    {
        timer.Once(10f, () => SpawnHellTrain());
        Puts("🔄 Попробую снова через 10 секунд...");
    }
}



#endregion

private void ForceDestroyHellTrainHard()
{
    try
    {
        // 1) Снести всё, что мы трекали при спавне
        foreach (var e in _spawnedTrainEntities.ToArray())
	            KillEntitySafe(e);
        _spawnedTrainEntities.Clear();

        // 2) Снести все TrainCar, что остались в мире (и их детей)
        var trainCars = BaseNetworkable.serverEntities.OfType<TrainCar>().ToArray();
        foreach (var car in trainCars)
            KillEntitySafe(car);

        // ❌ 3) Больше НЕ подметаем Vis.Entities по радиусу — чтобы не задевать игроков

        // 4) Сброс внутреннего состояния
        _trainLifecycle = null;
       
    }
    catch (Exception ex)
    {
        PrintError($"ForceDestroyHellTrainHard ERR: {ex}");
    }
}


private void KillEntitySafe(BaseNetworkable e)
{
    if (e == null || e.IsDestroyed) return;

    // 🚫 Никогда не трогаем живых игроков
    if (e is BasePlayer) return;

    var be = e as BaseEntity;
    try
    {
        if (be != null)
        {
            be.CancelInvoke();
            be.SetParent(null, true, true);

            if (be is TrainCar tc)
            {
                var eng = tc as TrainEngine;
                if (eng != null)
                {
                    try { eng.SetFlag(BaseEntity.Flags.On, false, false, true); } catch { }
                    try { eng.SetThrottle(TrainEngine.EngineSpeeds.Zero); } catch { }
                }
                else
                {
                    try { tc.SetFlag(BaseEntity.Flags.On, false); } catch { }
                }

                // Убиваем только дочерние сущности вагона (NPC/турели/прочее), НО не игроков
                var children = tc.children?.ToArray() ?? Array.Empty<BaseEntity>();
                foreach (var child in children)
                {
                    if (child == null || child.IsDestroyed) continue;
                    if (child is BasePlayer) continue;

                    var npcChild = child as NPCPlayer;
                    if (npcChild != null)
                    {
                        try { npcChild.inventory?.Strip(); } catch { }
                    }
                    try { child.Kill(BaseNetworkable.DestroyMode.None); } catch { }
                }
            }

            // Если это сам NPC — зачистить инвентарь
            var np = be as NPCPlayer;
            if (np != null)
            {
                try { np.inventory?.Strip(); } catch { }
            }
        }
    }
    catch { /* ignore */ }

    try { e.Kill(BaseNetworkable.DestroyMode.None); } catch { /* ignore */ }
}





        #region HT.ENGINE.CONTROL
        private void StartEngine(TrainEngine engine)
        {
            if (!engine || engine.IsDestroyed) return;
            
            Puts($"🔧 Запускаем двигатель ID: {engine.net.ID}");
            
            engine.SetFlag(BaseEntity.Flags.On, true, false, true);
            
            if (engine.engineController != null)
                engine.SetFlag(engine.engineController.engineStartingFlag, false, false, true);
            
            engine.SetThrottle(TrainEngine.EngineSpeeds.Fwd_Hi);
            Puts("🚂 Локомотив едет вперёд!");
            
            engine.InvokeRandomized(() => EnsureEngineRunning(engine), 1f, 1f, 0.1f);
            engine.InvokeRandomized(() => CheckRefreshFuel(engine), 5f, 5f, 0.5f);
            
            Puts($"✅ Двигатель запущен!");
        }

        private void ReCoupleAllCars(TrainEngine engine)
        {
            if (engine == null || engine.IsDestroyed) return;
            
            var completeTrain = engine.completeTrain;
            if (completeTrain == null || completeTrain.trainCars == null) 
            {
                Puts("⚠️ completeTrain == null, пробуем найти вагоны вручную");
                
                var nearCars = new List<TrainCar>();
                foreach (var e in _spawnedCars)
                {
                    if (e != null && !e.IsDestroyed && e is TrainCar)
                        nearCars.Add(e as TrainCar);
                }
                
                if (nearCars.Count <= 1)
                {
                    PrintWarning("⚠️ Недостаточно вагонов для сцепки");
                    return;
                }
                
                nearCars = nearCars.OrderBy(c => Vector3.Distance(engine.transform.position, c.transform.position)).ToList();
                
            //    Puts($"🔗 Пересцепка {nearCars.Count} вагонов вручную...");
                
                for (int i = 0; i < nearCars.Count - 1; i++)
                {
                    var front = nearCars[i];
                    var rear = nearCars[i + 1];
                    
                    if (front == null || rear == null) continue;
                    
                    front.coupling.rearCoupling.TryCouple(rear.coupling.frontCoupling, true);
                    Puts($"   ↔ {front.ShortPrefabName} → {rear.ShortPrefabName}");
                }
                
                return;
            }
            
          //  Puts($"🔗 Пересцепка... вагонов в completeTrain: {completeTrain.trainCars.Count}");
            
            for (int i = 0; i < completeTrain.trainCars.Count - 1; i++)
            {
                var front = completeTrain.trainCars[i];
                var rear = completeTrain.trainCars[i + 1];
                
                if (front == null || rear == null) continue;
                
                front.coupling.rearCoupling.TryCouple(rear.coupling.frontCoupling, true);
            }
        }

        private void EnsureEngineRunning(TrainEngine engine)
        {
            if (engine == null || engine.IsDestroyed) return;
            
            if (!engine.HasFlag(BaseEntity.Flags.On))
            {
                engine.SetFlag(BaseEntity.Flags.On, true, false, true);
                engine.SetThrottle(TrainEngine.EngineSpeeds.Fwd_Hi);
            }
        }

        private void CheckRefreshFuel(TrainEngine engine)
        {
            if (engine == null || engine.IsDestroyed) return;
            
            EntityFuelSystem fuel = engine.GetFuelSystem() as EntityFuelSystem;
            if (fuel != null && fuel.GetFuelAmount() < 100)
                fuel.AddFuel(500);
        }
        #endregion

        #region HT.CODELOCK
        private string trainCode = "6666";
        private HashSet<ulong> authorizedPlayers = new HashSet<ulong>();

        private void AddCodeLockToTrain(TrainEngine engine)
        {
         //   Puts($"🔒 Поезд защищён виртуальным кодом: {trainCode}");
        }

        private void RemoveCodeLock()
        {
            authorizedPlayers.Clear();
        //   Puts("🔓 Авторизации сброшены");
        }
        #endregion

        #region HT.COUPLE
        private void CoupleCars(BaseEntity front, BaseEntity rear)
        {
            TrainCar frontCar = front as TrainCar;
            TrainCar rearCar = rear as TrainCar;
            
            if (frontCar == null || rearCar == null) 
            {
                PrintWarning("⚠️ CoupleCars: не TrainCar!");
                return;
            }
            
            float dist = Vector3.Distance(front.transform.position, rear.transform.position);
          //  Puts($"      🔗 Расстояние для сцепки: {dist:F1}м");
            
            if (dist > 20f) 
            {
                PrintWarning($"⚠️ Слишком далеко: {dist:F1}м > 20м");
                return;
            }
            
            bool coupled = frontCar.coupling.rearCoupling.TryCouple(rearCar.coupling.frontCoupling, true);
          // Puts($"      {(coupled ? "✅" : "❌")} Сцепка: {frontCar.ShortPrefabName} ↔ {rearCar.ShortPrefabName}");
        }
        #endregion

        #region HT.UTILS
private TrainEngine GetNearestEngine(BasePlayer player, float maxDistance = 50f)
{
    if (activeHellTrain != null && !activeHellTrain.IsDestroyed)
    {
        float dist = Vector3.Distance(player.transform.position, activeHellTrain.transform.position);
        if (dist <= maxDistance)
            return activeHellTrain;
    }
    
    var allTrains = UnityEngine.Object.FindObjectsOfType<TrainEngine>();
    TrainEngine nearest = null;
    float nearestDist = maxDistance;
    
    foreach (var train in allTrains)
    {
        if (train == null || train.IsDestroyed) continue;
        
        float dist = Vector3.Distance(player.transform.position, train.transform.position);
        if (dist < nearestDist)
        {
            nearest = train;
            nearestDist = dist;
        }
    }
    
    return nearest;
}

// ✅ ВЫНЕСЕН КАК ОТДЕЛЬНЫЙ МЕТОД!
private string GetGridPosition(Vector3 position)
{
    float gridSize = TerrainMeta.Size.x / 26f;
    
    int x = Mathf.FloorToInt((position.x + TerrainMeta.Size.x / 2) / gridSize);
    int z = Mathf.FloorToInt((position.z + TerrainMeta.Size.z / 2) / gridSize);
    
    char letter = (char)('A' + Mathf.Clamp(x, 0, 25));
    int number = Mathf.Clamp(z, 0, 25);
    
    return $"{letter}{number}";
}
#endregion

        #region HT.COMMANDS
		
[ChatCommand("ht.wipe_all_cars")]
private void CmdWipeAllCars(BasePlayer player, string cmd, string[] args)
{
    if (player != null && !player.IsAdmin) { SendReply(player, "Недостаточно прав."); return; }

    _suppressHooks = true;
    StopEngineWatchdog();
    StopGridCheckTimer();
    CancelLifecycleTimer();

    int killed = 0;
    try
    {
        // снимок всех TrainCar
        var snapshot = Pool.GetList<TrainCar>();
        foreach (var bn in BaseNetworkable.serverEntities)
        {
            var car = bn as TrainCar;
            if (car != null && !car.IsDestroyed) snapshot.Add(car);
        }
        foreach (var car in snapshot) { car.Kill(); killed++; }
        Pool.FreeList(ref snapshot);

        // чистим локальные трекеры
        _spawnedCars.Clear();
        _spawnedTrainEntities.Clear();
        _spawnedTurrets.Clear();
        _spawnedSamSites.Clear();
        _spawnedNPCs.Clear();
        _savedProtection.Clear();
        _explosionDamageArmed = false;
        _explodedOnce = false;
        activeHellTrain = null;
        _trainLifecycle = null;

        SendReply(player, $"Helltrain: глобально удалено TrainCar = {killed}");
        Puts($"[Helltrain] wipe_all_cars (chat) → killed={killed}");
    }
    finally
    {
        _suppressHooks = false;
        _engineCleanupTriggered = false;
        _engineCleanupCooldownUntil = 0f;
    }
}

[ConsoleCommand("helltrain.wipe_all_cars")]
private void CcmdWipeAllCars(ConsoleSystem.Arg arg)
{
    _suppressHooks = true;
    StopEngineWatchdog();
    StopGridCheckTimer();
    CancelLifecycleTimer();

    int killed = 0;
    try
    {
        var snapshot = Pool.GetList<TrainCar>();
        foreach (var bn in BaseNetworkable.serverEntities)
        {
            var car = bn as TrainCar;
            if (car != null && !car.IsDestroyed) snapshot.Add(car);
        }
        foreach (var car in snapshot) { car.Kill(); killed++; }
        Pool.FreeList(ref snapshot);

        _spawnedCars.Clear();
        _spawnedTrainEntities.Clear();
        _spawnedTurrets.Clear();
        _spawnedSamSites.Clear();
        _spawnedNPCs.Clear();
        _savedProtection.Clear();
        _explosionDamageArmed = false;
        _explodedOnce = false;
        activeHellTrain = null;
        _trainLifecycle = null;

        arg.ReplyWith($"Helltrain: глобально удалено TrainCar = {killed}");
        Puts($"[Helltrain] wipe_all_cars (console) → killed={killed}");
    }
    finally
    {
        _suppressHooks = false;
        _engineCleanupTriggered = false;
        _engineCleanupCooldownUntil = 0f;
    }
}



		
		[ChatCommand("ht.clean_event_cars")]
private void CmdCleanEventCars(BasePlayer player, string cmd, string[] args)
{
    if (player != null && !player.IsAdmin)
    {
        SendReply(player, "Недостаточно прав.");
        return;
    }
    KillEventTrainCars("manual_command");
    SendReply(player, "Helltrain: ивентовые вагоны очищены.");
}

[ConsoleCommand("helltrain.clean_event_cars")]
private void CcmdCleanEventCars(ConsoleSystem.Arg arg)
{
    KillEventTrainCars("console_command");
    arg.ReplyWith("Helltrain: ивентовые вагоны очищены.");
}

		
		
		[ChatCommand("ht.counts")]
private void CmdCounts(BasePlayer p, string cmd, string[] args)
{
    SendReply(p, $"cars={_spawnedCars.Count}, ents={_spawnedTrainEntities.Count}, turrets={_spawnedTurrets.Count}, sams={_spawnedSamSites.Count}, npcs={_spawnedNPCs.Count}");
}

[ChatCommand("ht.resetflags")]
private void CmdResetFlags(BasePlayer p, string cmd, string[] args)
{
    _explosionTimerArmedOnce = false;
    _explodedOnce = false;
    _explosionDamageArmed = false;
    SendReply(p, "flags reset");
}

		
		[ChatCommand("htdel")]
private void CmdHtDelCrate(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }
    
    // Ищем ящик через raycast
    RaycastHit hit;
    if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 10f))
    {
        player.ChatMessage("❌ Смотри на ящик! (макс 10м)");
        return;
    }
    
    BaseEntity entity = hit.GetEntity();
    if (entity == null)
    {
        player.ChatMessage("❌ Не найден объект!");
        return;
    }
    
    HackableLockedCrate crate = entity as HackableLockedCrate;
    if (crate == null)
    {
        player.ChatMessage($"❌ Это не ящик! ({entity.ShortPrefabName})");
        return;
    }
    
    var defender = crate.GetComponent<HellTrainDefender>();
    if (defender == null)
    {
        player.ChatMessage("⚠️ Это не ящик Hell Train!");
        return;
    }
    
    Vector3 pos = crate.transform.position;
    crate.Kill(BaseNetworkable.DestroyMode.None);
    
    player.ChatMessage($"✅ Ящик удалён! Поз: {pos}");
    Puts($"🗑️ {player.displayName} удалил ящик в {pos}");
}
		
		[ChatCommand("htclear")]
private void CmdHtClearCrates(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }
    
    int removed = 0;
    
    // Удаляем все ящики с компонентом HellTrainDefender
    var allCrates = UnityEngine.Object.FindObjectsOfType<HackableLockedCrate>();
    
    foreach (var crate in allCrates)
    {
        if (crate == null || crate.IsDestroyed) continue;
        
        var defender = crate.GetComponent<HellTrainDefender>();
        if (defender != null)
        {
            crate.Kill(BaseNetworkable.DestroyMode.None);
            removed++;
        }
    }
    
    player.ChatMessage($"🧹 Удалено ящиков Hell Train: {removed}");
    Puts($"🧹 {player.displayName} удалил {removed} ящиков Hell Train");
}

/// <summary>
/// Принудительное удаление Hell Train (через команду или автоматически)
/// </summary>
private void ForceDestroyHellTrain()
{
    KillEventTrainCars("force_destroy");
    _explosionTimerArmedOnce = false;
    _firstLootAnnounced = false;
    _explodedOnce = false;
}

[ChatCommand("htinfo")]
private void CmdHtInfo(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }

    var sb = new StringBuilder();
    sb.AppendLine("════════════════════════════════════════");
    sb.AppendLine("📋 HELL TRAIN - КОМАНДЫ");
    sb.AppendLine("════════════════════════════════════════");
    sb.AppendLine("");
    
    // ОСНОВНЫЕ
    sb.AppendLine("🚂 ОСНОВНЫЕ:");
    sb.AppendLine("  /helltrain startnear [composition] - Спавн рядом");
    sb.AppendLine("  /htspawn <name> - Спавн композиции");
    sb.AppendLine("  /htcleanup [hell] - Удалить поезда");
    sb.AppendLine("  /htcheck - Инфо о поезде");
    sb.AppendLine("  /http - ТП к поезду");
    sb.AppendLine("");
    
    // РЕДАКТОР
    sb.AppendLine("✏️ РЕДАКТОР:");
    sb.AppendLine("  /htedit load <layoutName> - Открыть редактор");
    sb.AppendLine("  /htedit save - Сохранить изменения");
    sb.AppendLine("  /htedit cancel - Закрыть без сохранения");
    sb.AppendLine("  /htedit spawn <type> [args] - Создать объект");
    sb.AppendLine("  /htedit move - Переместить (смотри на объект)");
    sb.AppendLine("  /htedit delete - Удалить (смотри на объект)");
    sb.AppendLine("");
    
    // SPAWN ТИПЫ
    sb.AppendLine("📦 ТИПЫ ДЛЯ SPAWN:");
    sb.AppendLine("  npc <kitname> - NPC с китом");
    sb.AppendLine("    Пример: /htedit spawn npc pmcjuggernaut");
    sb.AppendLine("  turret [gun] [ammo] [count] - Турель");
    sb.AppendLine("    Пример: /htedit spawn turret m249 ammo.rifle 500");
    sb.AppendLine("  samsite - SAM турель");
    sb.AppendLine("  loot - Hackable ящик");
    sb.AppendLine("");
    
    // УТИЛИТЫ
    sb.AppendLine("🔧 УТИЛИТЫ:");
    sb.AppendLine("  /htpos - Твоя позиция от поезда");
    sb.AppendLine("  /htreload - Перезагрузить лэйауты");
    sb.AppendLine("");
    sb.AppendLine("🔍 ДИАГНОСТИКА:");
    sb.AppendLine("  /htdebug npc - Маркеры NPC");
    sb.AppendLine("  /htdebug turret - Маркеры турелей");
    sb.AppendLine("  /htdebug samsite - SAM турели");
    sb.AppendLine("  /htdebug loot - Ящики с лутом");
    sb.AppendLine("  /htdebug all - Полная диагностика вагона");
    sb.AppendLine("");
    
    // WAGON УТИЛИТЫ
    sb.AppendLine("🗑️ ОЧИСТКА:");
    sb.AppendLine("  /wagon.remove <type> - Удалить объекты");
    sb.AppendLine("    Типы: npc, turret, samsite, loot, all");
    sb.AppendLine("  /wagon.undo - Отменить последнее");
    sb.AppendLine("  /wagon.list - Список объектов");
    sb.AppendLine("");
    
    // УПРАВЛЕНИЕ
    sb.AppendLine("🎮 УПРАВЛЕНИЕ В РЕДАКТОРЕ:");
    sb.AppendLine("  ЛКМ - разместить объект");
    sb.AppendLine("  ПКМ - отменить размещение");
    sb.AppendLine("  RELOAD - поворот объекта");
    sb.AppendLine("  DUCK+RELOAD - поворот по Z");
    sb.AppendLine("  SPRINT+RELOAD - поворот по X");
    sb.AppendLine("");
    
    // ДОСТУПНЫЕ КОМПОЗИЦИИ
    sb.AppendLine("📋 ДОСТУПНЫЕ КОМПОЗИЦИИ:");
    foreach (var kv in config.Compositions)
    {
        var comp = kv.Value;
        sb.AppendLine($"  • {kv.Key} ({comp.Tier}, {comp.Wagons.Count} вагонов)");
    }
    sb.AppendLine("");
    
    // ЛЭЙАУТЫ
    sb.AppendLine("📦 ЗАГРУЖЕННЫЕ ЛЭЙАУТЫ:");
    int layoutCount = 0;
    foreach (var kv in _layouts)
    {
        if (layoutCount < 10)
        {
            int objCount = kv.Value.objects?.Count ?? 0;
            sb.AppendLine($"  • {kv.Key} ({objCount} объектов)");
        }
        layoutCount++;
    }
    if (layoutCount > 10)
        sb.AppendLine($"  ... и еще {layoutCount - 10}");
    sb.AppendLine("");
    
    sb.AppendLine("════════════════════════════════════════");
    
    player.ChatMessage(sb.ToString());
}

[ChatCommand("htdebug")]
private void CmdHtDebug(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }

    string mode = args.Length > 0 ? args[0].ToLower() : "all";

    // Найти ближайший вагон
    TrainCar nearestCar = null;
    float nearestDist = 20f;

    foreach (var entity in _spawnedCars)
    {
        if (entity == null || entity.IsDestroyed) continue;
        if (!(entity is TrainCar car)) continue;

        float dist = Vector3.Distance(player.transform.position, car.transform.position);
        if (dist < nearestDist)
        {
            nearestCar = car;
            nearestDist = dist;
        }
    }

    if (nearestCar == null)
    {
        player.ChatMessage("❌ Вагон не найден в радиусе 20м");
        return;
    }

    var sb = new StringBuilder();
    sb.AppendLine($"════════════════════════════════════════");
    sb.AppendLine($"🔍 DEBUG: {nearestCar.ShortPrefabName}");
    sb.AppendLine($"Расстояние: {nearestDist:F1}м");
    sb.AppendLine($"════════════════════════════════════════");

    int npcCount = 0;
    int turretCount = 0;
    int samsiteCount = 0;
    int lootCount = 0;
    int otherCount = 0;

    // Собираем все child entities
    var children = new List<BaseEntity>();
    foreach (var child in nearestCar.children)
    {
        if (child == null) continue;
        children.Add(child);
    }

    // Также проверяем спавненные объекты рядом
    foreach (var entity in _spawnedCars)
    {
        if (entity == null || entity.IsDestroyed) continue;
        if (entity == nearestCar) continue;
        if (entity.GetParentEntity() == nearestCar)
            children.Add(entity);
    }

    if (mode == "npc" || mode == "all")
    {
        sb.AppendLine("");
        sb.AppendLine("👤 NPC:");
        foreach (var child in children)
        {
            if (!(child is ScientistNPC npc)) continue;

            var marker = npc.GetComponent<NPCTypeMarker>();
            string npcType = marker?.npcType ?? "❌ НЕТ МАРКЕРА";
            
            Vector3 localPos = nearestCar.transform.InverseTransformPoint(npc.transform.position);
            
            int itemCount = (npc.inventory?.containerMain?.itemList.Count ?? 0)
                          + (npc.inventory?.containerBelt?.itemList.Count ?? 0)
                          + (npc.inventory?.containerWear?.itemList.Count ?? 0);

            string weapon = "нет";
            if (npc.inventory?.containerBelt != null)
            {
                foreach (var item in npc.inventory.containerBelt.itemList)
                {
                    var held = item?.GetHeldEntity();
                    if (held != null)
                    {
                        weapon = item.info.shortname;
                        break;
                    }
                }
            }

            sb.AppendLine($"  • Type: {npcType}");
            sb.AppendLine($"    Оружие: {weapon}");
            sb.AppendLine($"    Предметов: {itemCount}");
            sb.AppendLine($"    Локальная поз: {localPos}");
            sb.AppendLine($"    HP: {npc.Health():F0}/{npc.MaxHealth():F0}");
            sb.AppendLine("");
            
            npcCount++;
        }
        if (npcCount == 0)
            sb.AppendLine("  (нет NPC)");
    }

    if (mode == "turret" || mode == "all")
    {
        sb.AppendLine("");
        sb.AppendLine("🔫 ТУРЕЛИ:");
        foreach (var child in children)
        {
            if (!(child is AutoTurret turret)) continue;

            var marker = turret.GetComponent<TurretMarker>();
            string gun = marker?.gun ?? "❌ НЕТ МАРКЕРА";
            string ammo = marker?.ammo ?? "?";
            int ammoCount = marker?.ammoCount ?? 0;

            Vector3 localPos = nearestCar.transform.InverseTransformPoint(turret.transform.position);

            string actualGun = "пусто";
            string actualAmmo = "пусто";
            int actualAmmoCount = 0;

            if (turret.inventory != null)
            {
                if (turret.inventory.itemList.Count > 0)
                    actualGun = turret.inventory.itemList[0]?.info?.shortname ?? "?";
                if (turret.inventory.itemList.Count > 1)
                {
                    actualAmmo = turret.inventory.itemList[1]?.info?.shortname ?? "?";
                    actualAmmoCount = turret.inventory.itemList[1]?.amount ?? 0;
                }
            }

            sb.AppendLine($"  • Маркер: {gun} + {ammo} x{ammoCount}");
            sb.AppendLine($"    Реально: {actualGun} + {actualAmmo} x{actualAmmoCount}");
            sb.AppendLine($"    Локальная поз: {localPos}");
            sb.AppendLine($"    HP: {turret.Health():F0}/{turret.MaxHealth():F0}");
            sb.AppendLine($"    Включена: {turret.IsOn()}");
            sb.AppendLine("");
            
            turretCount++;
        }
        if (turretCount == 0)
            sb.AppendLine("  (нет турелей)");
    }

    if (mode == "samsite" || mode == "all")
    {
        sb.AppendLine("");
        sb.AppendLine("🚀 SAM SITES:");
        foreach (var child in children)
        {
            if (!(child is SamSite sam)) continue;

            Vector3 localPos = nearestCar.transform.InverseTransformPoint(sam.transform.position);

            sb.AppendLine($"  • Локальная поз: {localPos}");
            sb.AppendLine($"    HP: {sam.Health():F0}/{sam.MaxHealth():F0}");
            sb.AppendLine("");
            
            samsiteCount++;
        }
        if (samsiteCount == 0)
            sb.AppendLine("  (нет SAM)");
    }

    if (mode == "loot" || mode == "all")
    {
        sb.AppendLine("");
        sb.AppendLine("📦 ЯЩИКИ:");
        foreach (var child in children)
        {
            if (!(child is HackableLockedCrate crate)) continue;

            Vector3 localPos = nearestCar.transform.InverseTransformPoint(crate.transform.position);

            int itemCount = crate.inventory?.itemList?.Count ?? 0;
            bool hasDefender = crate.GetComponent<HellTrainDefender>() != null;

            sb.AppendLine($"  • Локальная поз: {localPos}");
            sb.AppendLine($"    Предметов: {itemCount}");
            sb.AppendLine($"    HP: {crate.Health():F0}");
            sb.AppendLine($"    Компонент защиты: {(hasDefender ? "✅" : "❌")}");
            sb.AppendLine("");
            
            lootCount++;
        }
        if (lootCount == 0)
            sb.AppendLine("  (нет ящиков)");
    }

    sb.AppendLine("");
    sb.AppendLine($"ВСЕГО: NPC={npcCount}, Турели={turretCount}, SAM={samsiteCount}, Ящики={lootCount}");
    sb.AppendLine($"════════════════════════════════════════");

    player.ChatMessage(sb.ToString());
}

[ChatCommand("htcheck")]
private void CmdCheckTrain(BasePlayer player, string command, string[] args)
{
    if (activeHellTrain == null || activeHellTrain.IsDestroyed)
    {
        player.ChatMessage("❌ Hell Train не активен!");
        return;
    }
    
    player.ChatMessage($"🚂 Hell Train ID: {activeHellTrain.net.ID}");
    player.ChatMessage($"   Позиция: {activeHellTrain.transform.position}");
    
    var completeTrain = activeHellTrain.completeTrain;
    if (completeTrain != null && completeTrain.trainCars != null)
    {
        player.ChatMessage($"   Вагонов в составе: {completeTrain.trainCars.Count}");
        
        foreach (var car in completeTrain.trainCars)
        {
            if (car == null) continue;
            player.ChatMessage($"      - {car.ShortPrefabName} (ID: {car.net.ID})");
        }
    }
    else
    {
        player.ChatMessage("   ⚠️ completeTrain == null!");
    }
    
    player.ChatMessage($"📦 В _spawnedCars: {_spawnedCars.Count}");
    int alive = 0;
    foreach (var e in _spawnedCars)
    {
        if (e != null && !e.IsDestroyed) alive++;
    }
    player.ChatMessage($"   Живых: {alive}");
}

[ChatCommand("http")]
private void CmdTeleportToTrain(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }
    
    if (activeHellTrain == null || activeHellTrain.IsDestroyed)
    {
        player.ChatMessage("❌ Hell Train не активен!");
        return;
    }
    
    Vector3 trainPos = activeHellTrain.transform.position;
    Vector3 trainFwd = activeHellTrain.transform.forward;
    Vector3 tpPos = trainPos + trainFwd * 5f + Vector3.up * 2f;
    
    player.Teleport(tpPos);
    player.ChatMessage($"✅ ТП к Hell Train! Pos: {trainPos}");
    
    int carCount = 0;
    var completeTrain = activeHellTrain.completeTrain;
    if (completeTrain != null)
        carCount = completeTrain.trainCars.Count;
    
    player.ChatMessage($"🚂 Вагонов в составе: {carCount}");
}

[ChatCommand("htcode")]
private void EnterCodeCommand(BasePlayer player, string command, string[] args)
{
    if (args.Length == 0)
    {
        player.ChatMessage("Используй: /htcode 6666");
        return;
    }
    
    if (args[0] == trainCode)
    {
        authorizedPlayers.Add(player.userID);
        player.ChatMessage("✅ Код принят! Можешь сесть.");
    }
    else
    {
        player.ChatMessage("❌ Неверный код!");
    }
}

[ChatCommand("wagon.remove")]
private void CmdWagonRemove(BasePlayer player, string command, string[] args)
{
    WagonEditor editor = player.GetComponent<WagonEditor>();
    if (editor == null)
    {
        player.ChatMessage("Редактор не активен!");
        return;
    }
    
    if (args.Length == 0)
    {
        player.ChatMessage("Использование: /wagon.remove <тип>");
        player.ChatMessage("Типы: bradley, turret, samsite, npc, crate, all");
        return;
    }
    
    string type = args[0].ToLower();
    int removed = 0;
    
    List<BaseEntity> toRemove = new List<BaseEntity>();
    
    foreach (var child in editor.GetChildren())
    {
        bool shouldRemove = false;
        
        switch (type)
        {
            case "turret":
    shouldRemove = child is AutoTurret;
    break;
            case "samsite":
                shouldRemove = child is SamSite;
                break;
            case "npc":
                shouldRemove = child is global::HumanNPC;
                break;
            case "crate":
                shouldRemove = child.ShortPrefabName.Contains("crate");
                break;
            case "all":
                shouldRemove = true;
                break;
        }
        
        if (shouldRemove)
            toRemove.Add(child);
    }
    
    foreach (var entity in toRemove)
    {
        editor.DeleteWagonEntity(entity);
        removed++;
    }
    
    player.ChatMessage($"Удалено объектов: {removed}");
}

[ChatCommand("wagon.undo")]
private void CmdWagonUndo(BasePlayer player)
{
    WagonEditor editor = player.GetComponent<WagonEditor>();
    if (editor == null)
    {
        player.ChatMessage("Редактор не активен!");
        return;
    }
    
    var children = editor.GetChildren();
    if (children.Count == 0)
    {
        player.ChatMessage("Нет объектов для удаления!");
        return;
    }
    
    var last = children[children.Count - 1];
    editor.DeleteWagonEntity(last);
    player.ChatMessage($"Удалён: {last.ShortPrefabName}");
}

[ChatCommand("wagon.list")]
private void CmdWagonList(BasePlayer player)
{
    WagonEditor editor = player.GetComponent<WagonEditor>();
    if (editor == null)
    {
        player.ChatMessage("Редактор не активен!");
        return;
    }
    
    var children = editor.GetChildren();
    if (children.Count == 0)
    {
        player.ChatMessage("Нет объектов!");
        return;
    }
    
    player.ChatMessage($"=== Объекты на вагоне ({children.Count}) ===");
    for (int i = 0; i < children.Count; i++)
    {
        var child = children[i];
        string name = child.ShortPrefabName;
        if (child is BradleyAPC) name = "Bradley APC";
        if (child is AutoTurret) name = "Auto Turret";
        if (child is SamSite) name = "SAM Site";
        if (child is global::HumanNPC) name = "NPC";
        
        player.ChatMessage($"{i + 1}. {name}");
    }
}

[ChatCommand("htspawn")]
private void CmdSpawnComposition(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }
    
    if (args.Length == 0)
    {
        player.ChatMessage("📋 Доступные композиции:");
        foreach (var key in config.Compositions.Keys)
        {
            var comp = config.Compositions[key];
            player.ChatMessage($"   • {key} ({comp.Tier}, {comp.Wagons.Count} вагонов)");
        }
        player.ChatMessage("Используй: /htspawn <название>");
        return;
    }
    
    string compositionName = args[0].ToLower();
    
    if (!config.Compositions.ContainsKey(compositionName))
    {
        player.ChatMessage($"❌ Композиция '{compositionName}' не найдена!");
        player.ChatMessage("Используй: /htspawn для списка");
        return;
    }
    
    if (!TrainTrackSpline.TryFindTrackNear(player.transform.position, 1500f, out TrainTrackSpline trackSpline, out float distOnSpline))
    {
        player.ChatMessage("❌ Рельсы не найдены в радиусе 1500м");
        return;
    }
    
    float len = trackSpline.GetLength();
    string nm = trackSpline.name;
    
    if (len < config.MinTrackLength || 
        nm.IndexOf("3x36", System.StringComparison.OrdinalIgnoreCase) >= 0 || 
        nm.IndexOf("monument", System.StringComparison.OrdinalIgnoreCase) >= 0)
    {
        player.ChatMessage($"⚠️ Ближайший трек не годится ({nm}, {len:F0} м)");
        return;
    }
    
    if (activeHellTrain != null && !activeHellTrain.IsDestroyed)
    {
        activeHellTrain.Kill();
        activeHellTrain = null;
    }
    
    player.ChatMessage($"✅ Спавним композицию: {compositionName}");
    
    SpawnTrainFromComposition(compositionName, trackSpline, distOnSpline);
}


[ChatCommand("helltrain")]
private void CmdHelltrain(BasePlayer player, string command, string[] args)
{
    if (player == null) return;

    if (args == null || args.Length == 0)
    {
        SendReply(player, "📋 /helltrain start [PMC|BANDIT|COBLAB] [layout?]  — старт в случайной точке карты");
        SendReply(player, "📋 /helltrain startnear [PMC|BANDIT|COBLAB] [layout?] — старт рядом с тобой");
        SendReply(player, "📋 /helltrain stop — принудительно остановить и очистить");
        SendReply(player, "📋 /helltrain reload — перезагрузка конфига и лэйаутов");
        return;
    }

    var sub = args[0].ToLowerInvariant();

    if (sub == "reload")
    {
        if (!HasPerm(player, PERM_ADMIN)) { SendReply(player, "⛔ Нет прав."); return; }
        LoadConfig();
        LoadLayouts();
        SendReply(player, $"✅ Перезагружено. Layouts: {_layouts.Count}");
        return;
    }

    if (sub == "stop")
    {
        if (!HasPerm(player, PERM_ADMIN)) { SendReply(player, "⛔ Нет прав."); return; }
        ForceDestroyHellTrain();
        SendReply(player, "🧹 Helltrain остановлен и очищен.");
        StartRespawnTimer();
        return;
    }

    // P1: deny new start if build is running or runtime is dirty (no cleanup here)
    if (sub == "start" || sub == "startnear")
    {
        bool dirty =
            _isBuildingTrain ||
            (activeHellTrain != null && !activeHellTrain.IsDestroyed) ||
            (_spawnedCars != null && _spawnedCars.Count > 0) ||
            (_spawnedTrainEntities != null && _spawnedTrainEntities.Count > 0);

        if (dirty)
        {
            SendReply(player, "⛔ Helltrain сейчас занят (идёт сборка или остались хвосты). Подожди 10–20 сек и попробуй снова.");
            return;
        }
    }

    if (sub != "start" && sub != "startnear")
    {
        SendReply(player, "❌ Неизвестная подкоманда. Напиши просто /helltrain");
        return;
    }

    if (!HasPerm(player, PERM_START) && !HasPerm(player, PERM_ADMIN))
    {
        SendReply(player, "⛔ Нет прав на запуск (нужно helltrain.start).");
        return;
    }

    string faction = (args.Length >= 2 ? args[1].ToUpperInvariant() : "PMC");
    if (faction != "PMC" && faction != "BANDIT" && faction != "COBLAB")
        faction = "PMC";

    string layoutName = (args.Length >= 3 ? args[2].ToLowerInvariant() : null);

    if (_layouts.Count == 0) LoadLayouts();

    TrainLayout forcedLayout = null;
    if (!string.IsNullOrEmpty(layoutName))
    {
        forcedLayout = GetLayout(layoutName);
        if (forcedLayout == null)
        {
            SendReply(player, $"❌ Layout '{layoutName}' не найден в /oxide/data/Helltrain/Layouts/");
            return;
        }
    }

    string compositionName = null;

    if (forcedLayout != null)
    {
        compositionName = "__forced__";
        config.Compositions[compositionName] = new ConfigData.TrainComposition
        {
            Tier = ConfigData.TrainTier.MEDIUM,
            Weight = 0,
            Wagons = new List<string> { forcedLayout.name }
        };
    }
    else
    {
        compositionName = faction.ToLowerInvariant();
        if (!config.Compositions.ContainsKey(compositionName))
        {
            compositionName = config.Compositions.Keys.FirstOrDefault();
            if (compositionName == null)
            {
                SendReply(player, "❌ В конфиге нет Compositions.");
                return;
            }
        }
    }

    TrainTrackSpline trackSpline;
    float distOnSpline;

    if (sub == "startnear")
    {
        if (!TrainTrackSpline.TryFindTrackNear(player.transform.position, 1500f, out trackSpline, out distOnSpline))
        {
            SendReply(player, "❌ Рельсы не найдены в радиусе 1500м.");
            return;
        }
    }
    else
    {
        if (availableOverworldSplines == null || availableOverworldSplines.Count == 0)
            CacheSplines();

        var pool = new List<TrainTrackSpline>();
        if (config.AllowAboveGround) pool.AddRange(availableOverworldSplines);
        if (config.AllowUnderGround) pool.AddRange(availableUnderworldSplines);

        trackSpline = pool.Count > 0 ? pool[_rng.Next(pool.Count)] : null;
        if (trackSpline == null)
        {
            SendReply(player, "❌ Не удалось найти доступные треки. Попробуй /helltrain startnear.");
            return;
        }

        distOnSpline = _rng.Next(0, Mathf.Max(10, Mathf.FloorToInt(trackSpline.GetLength())));
    }

    float len = trackSpline.GetLength();
    string nm = trackSpline.name;

    if (len < config.MinTrackLength ||
        nm.IndexOf("3x36", StringComparison.OrdinalIgnoreCase) >= 0 ||
        nm.IndexOf("monument", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        SendReply(player, $"⚠️ Трек не годится ({nm}, {len:F0} м). Попробуй другое место.");
        return;
    }

    if (_spawnedCars.Count > 0 || _spawnedTrainEntities.Count > 0 || activeHellTrain != null)
        KillEventTrainCars("manual_start");

_activeFactionKey = faction.ToUpperInvariant();
_activeLayoutName = NormalizeLayoutName(layoutName); // алиасы wagona/wagonb/wagonc -> wagonA/B/C


    SendReply(player, $"🚂 Запуск Helltrain: faction={faction}, composition={compositionName}");
    SpawnTrainFromComposition(compositionName, trackSpline, distOnSpline);

}

[ChatCommand("htpos")]
private void CmdGetPosition(BasePlayer player, string command, string[] args)
{
    var engine = GetNearestEngine(player);
    if (engine == null)
    {
        SendReply(player, "❌ Поезд далеко!");
        return;
    }
    
    Vector3 localPos = engine.transform.InverseTransformPoint(player.transform.position);
    
    SendReply(player, $"📍 Твоя позиция:");
    SendReply(player, $"World: {player.transform.position}");
    SendReply(player, $"Local (от поезда): {localPos}");
    
   // Puts($"📍 Игрок {player.displayName}: Local={localPos}");
}

[ConsoleCommand("cleanup.trains")]
private void CleanupTrains(ConsoleSystem.Arg arg)
{

    BasePlayer player = arg.Player();
    if (player != null && !player.IsAdmin)
    {
        SendReply(arg, "❌ Только для админов!");
        return;
    }

    bool onlyHellTrain = arg.Args != null && arg.Args.Length > 0 && arg.Args[0] == "hell";
    int count = 0;

    if (onlyHellTrain)
    {
        if (activeHellTrain != null && !activeHellTrain.IsDestroyed)
        {
            ForceDestroyHellTrain();
            count = 1;
            SendReply(arg, $"🧹 Hell Train принудительно удалён");
        }
        else
        {
            SendReply(arg, "⚠️ Активного Hell Train нет");
        }
    }
    else
    {
        if (activeHellTrain != null && !activeHellTrain.IsDestroyed)
        {
            ForceDestroyHellTrain();
            count++;
        }

        var trains = UnityEngine.Object.FindObjectsOfType<TrainEngine>();
        
        foreach (var train in trains)
        {
            if (train != null && !train.IsDestroyed)
            {
                train.Kill();
                count++;
            }
        }
        
        SendReply(arg, $"🧹 Удалено поездов: {count}");
    }
    
  //  Puts($"🧹 Удалено поездов: {count} (admin: {player?.displayName ?? "RCON"})");
}

[ChatCommand("htcleanup")]
private void CmdHtCleanup(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }

    bool onlyHellTrain = args.Length > 0 && args[0].Equals("hell", System.StringComparison.OrdinalIgnoreCase);

    _suppressHooks = true;
    StopEngineWatchdog();
    StopGridCheckTimer();
    CancelLifecycleTimer();

    try
    {
        if (onlyHellTrain)
        {
            ForceDestroyHellTrain(); // внутренняя чистка своих списков
            player.ChatMessage("🧹 Hell Train принудительно удалён");
            return;
        }

        // глобально: безопасный снапшот
        var snapshot = Pool.GetList<TrainEngine>();
        foreach (var te in UnityEngine.Object.FindObjectsOfType<TrainEngine>())
            if (te != null && !te.IsDestroyed) snapshot.Add(te);

        int killed = 0;
        foreach (var te in snapshot) { te.Kill(); killed++; }
        Pool.FreeList(ref snapshot);

        // локальные трекеры
        _spawnedCars.Clear();
        _spawnedTrainEntities.Clear();
        _spawnedTurrets.Clear();
        _spawnedSamSites.Clear();
        _spawnedNPCs.Clear();
        _savedProtection.Clear();
        _explosionDamageArmed = false;
        _explodedOnce = false;
        activeHellTrain = null;
        _trainLifecycle = null;

        player.ChatMessage($"🧹 Удалено поездов: {killed}");
    }
    finally
    {
        _suppressHooks = false;
        _engineCleanupTriggered = false;
        _engineCleanupCooldownUntil = 0f;
    }
}


#endregion


        #region OXIDE.HOOKS

// ============================================
// ✅ ЕДИНСТВЕННЫЙ ХУК УРОНА - ОБЪЕДИНЁННЫЙ
// ============================================

// 1️⃣ CanEntityTakeDamage - FF защита + блок урона до _allowDestroy
private object CanEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
{
    if (entity == null || hitInfo == null) 
        return null;
    
    // Защита вагонов
    if (entity is TrainCar && _spawnedCars.Contains(entity))
    {
        if (!_allowDestroy)
        {
            hitInfo?.damageTypes?.Clear();
            return false;
        }
    }
    
    // Защита от FF
    var victimDefender = entity.GetComponent<HellTrainDefender>();
    if (victimDefender != null)
    {
        BaseEntity attacker = hitInfo.Initiator;
        
        if (attacker != null)
        {
            var attackerDefender = attacker.GetComponent<HellTrainDefender>();
            
            if (attackerDefender != null)
            {
                hitInfo.damageTypes.Clear();
                hitInfo.DoHitEffects = false;
                hitInfo.HitMaterial = 0;
                
                if (entity is AutoTurret turret)
                {
                    NextTick(() => {
                        if (turret != null && !turret.IsDestroyed && turret.target != null)
                        {
                            var targetDefender = turret.target.GetComponent<HellTrainDefender>();
                            if (targetDefender != null)
                                turret.SetTarget(null);
                        }
                    });
                }
                
                return false;
            }
        }
    }
    
    return null;
}

// ============================================
// ✅ ТУРЕЛЬ НЕ АТАКУЕТ СОЮЗНИКОВ
// ============================================

private object OnTurretTarget(AutoTurret turret, BaseCombatEntity target)
{
    if (turret == null || target == null)
        return null;
    
    if (!_spawnedTurrets.Contains(turret)) 
        return null;
    
    var targetDefender = target.GetComponent<HellTrainDefender>();
    if (targetDefender != null)
    {
        NextTick(() => {
            if (turret != null && !turret.IsDestroyed)
                turret.SetTarget(null);
        });
        
        return false;
    }
    
    return null;
}

// ============================================
// ✅ КОДЛОК / ОТЦЕПЛЕНИЕ / ОСТАНОВКА
// ============================================

private object CanMountEntity(BasePlayer player, BaseMountable baseMountable)
{
    if (activeHellTrain == null || activeHellTrain.IsDestroyed)
        return null;
    
    TrainCar trainCar = baseMountable.VehicleParent() as TrainCar;
    if (trainCar && _spawnedCars.Contains(trainCar))
    {
        if (authorizedPlayers.Contains(player.userID))
            return null;
        
        player.ChatMessage("🔒 Введи код: /htcode 6666");
        return false;
    }

    return null;
}

private object OnTrainCarUncouple(TrainCar trainCar, BasePlayer player)
{
    if (trainCar && _spawnedCars.Contains(trainCar))
    {
        player.ChatMessage("⚠️ Нельзя отцепить вагоны Hell Train!");
        return false;
    }

    return null;
}

private object OnEngineStop(TrainEngine trainEngine)
{
    if (trainEngine && trainEngine == activeHellTrain)
        return false;

    return null;
}

// ============================================
// ✅ СТАНДАРТНЫЕ HOOKS
// ============================================



private readonly List<ulong> _tmpIds = new List<ulong>();


// ... остальные хуки без изменений

#endregion

#region HT.RAILWAY.SCAN

private void ScanRailwayNetwork()
{
    Puts("🔍 Сканируем железнодорожную сеть...");
    
    availableOverworldSplines.Clear();
    availableUnderworldSplines.Clear();
    
    // Используем ТОЛЬКО Path.Rails для кольцевых петель
    if (config.AllowAboveGround && TerrainMeta.Path != null && TerrainMeta.Path.Rails != null)
    {
        foreach (PathList pathList in TerrainMeta.Path.Rails)
        {
            if (pathList == null || pathList.Path == null) 
                continue;

            // ТОЛЬКО КОЛЬЦЕВЫЕ ПЕТЛИ!
            if (!pathList.Path.Circular)
            {
              //  Puts($"   ⚠️ Пропускаем линейный путь (не петля): {pathList.Name}");
                continue;
            }

            float totalLength = 0f;
            for (int i = 0; i < pathList.Path.Points.Length - 1; i++)
            {
                totalLength += Vector3.Distance(pathList.Path.Points[i], pathList.Path.Points[i + 1]);
            }
            
            if (totalLength < config.MinTrackLength)
            {
             //   Puts($"   ⚠️ Петля слишком короткая: {pathList.Name} ({totalLength:F0}м < {config.MinTrackLength:F0}м)");
                continue;
            }

         //   Puts($"   ✅ Найдена петля: {pathList.Name} ({totalLength:F0}м)");

            // Добавляем ВСЕ сплайны этой петли
            int skip = pathList.Path.Points.Length >= 1000 ? 10 : pathList.Path.Points.Length >= 500 ? 5 : 1;
            
            for (int i = 0; i < pathList.Path.Points.Length; i += skip)
            {
                Vector3 point = pathList.Path.Points[i];
                
                if (TrainTrackSpline.TryFindTrackNear(point, 10f, out TrainTrackSpline spline, out float dist))
                {
                    if (!availableOverworldSplines.Contains(spline))
                    {
                        availableOverworldSplines.Add(spline);
                    }
                }
            }
        }
    }
    
    // Подземка (опционально)
    if (config.AllowUnderGround)
    {
        TrainTrackSpline[] allSplines = UnityEngine.Object.FindObjectsOfType<TrainTrackSpline>();
        
        foreach (var spline in allSplines)
        {
            if (!spline || !spline.gameObject)
                continue;
                
            string name = spline.gameObject.name;
            
            if (name.StartsWith("train_tunnel"))
            {
                if (!config.AllowTransition && 
                    (name.Contains("transition_up", System.StringComparison.OrdinalIgnoreCase) || 
                     name.Contains("transition_down", System.StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                
                if (!availableUnderworldSplines.Contains(spline))
                {
                    availableUnderworldSplines.Add(spline);
                }
            }
        }
        
      //  Puts($"   ✅ Подземных треков: {availableUnderworldSplines.Count}");
    }
    
   // Puts($"✅ Найдено треков: {availableOverworldSplines.Count} наземных, {availableUnderworldSplines.Count} подземных");
}

#endregion

        #region HT.DEBUG
        private void DebugLog(string message)
        {
            Puts(message);
        }
        #endregion
		

#region HT.LAYOUT.OBJECTS

private void SpawnLayoutObjects(TrainCar trainCar, TrainLayout layout)
{
    if (layout.objects == null || layout.objects.Count == 0)
{
    int npcCount = layout.NpcSlots?.Count ?? 0;
    int crateCount = layout.CrateSlots?.Count ?? 0;
    int shelfCount = layout.Shelves?.Count ?? 0;

    if (npcCount > 0 || crateCount > 0 || shelfCount > 0)
    {
        Puts($"Slots spawn: npc={npcCount}, crates={crateCount}, shelves={shelfCount}");
        SpawnLayoutSlots(trainCar, layout);
        return;
    }

    Puts($"   ⚠️ SpawnLayoutObjects({layout.name}): objects пуст! (null={layout.objects == null}, count={layout.objects?.Count ?? 0})");
    return;
}

    
  //  Puts($"   🎯 Спавним {layout.objects.Count} объектов из {layout.name}...");
    
    ProtectionProperties turretProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
    turretProtection.density = 100;
    turretProtection.amounts = new float[] 
    { 
        1f, 1f, 1f, 1f, 1f, 0.8f, 1f, 1f, 1f, 0.9f,
        0.5f, 0.5f, 1f, 1f, 0f, 0.5f, 0f, 1f, 1f, 0f, 
        1f, 0.9f, 0f, 1f, 0f 
    };
    
    foreach (var obj in layout.objects)
    {
        Puts($"🔍 DEBUG: Спавним {obj.type}, npc_type={obj.npc_type ?? "null"}, gun={obj.gun ?? "null"}, kit={obj.kit ?? "null"}");
        
        Vector3 localPos = V3(obj.position);
        Quaternion localRot = Quaternion.Euler(0, obj.rotationY, 0);
        
        Vector3 worldPos = trainCar.transform.TransformPoint(localPos);
        Quaternion worldRot = trainCar.transform.rotation * localRot;
        
        string prefab = null;
        
        switch (obj.type?.ToLower())
{
    case "npc":
        prefab = SCIENTIST_PREFAB;
        break;
    case "turret":
        prefab = TURRET_PREFAB;
        break;
    case "samsite":
        prefab = SAMSITE_PREFAB;
        break;
case "loot":
{

    // фракция поезда/лэйаута
    string factionUpper = (layout?.faction ?? "BANDIT").ToUpper();

    // обычный (НЕ hack) префаб под фракцию
    string lootPrefab = GetCratePrefabForFaction(factionUpper);

    var ent = GameManager.server.CreateEntity(lootPrefab, worldPos, worldRot);


    if (ent == null)
    {
        Puts("❌ Не удалось создать лут-ящик (CreateEntity вернул null)");
        break;
    }

    ent.enableSaving = false;
    ent.SetParent(trainCar, false, false);
    ent.transform.localPosition = localPos;
    ent.transform.localRotation = localRot;

    // защита от физики
    var combat = ent as BaseCombatEntity;
    if (combat != null) combat.InitializeHealth(5000f, 5000f);
    var rb = ent.GetComponent<Rigidbody>();
    if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }

    ent.Spawn();

    // учёт наших ящиков
    ulong id = ent.net.ID.Value;
    _ourCrates.Add(id);
    _crateStates[id] = CrateState.Idle;
    _crateFaction[id] = factionUpper;

    // назначение пресета A/B 50/50
    string presetKey = PickPresetAB(factionUpper);
    Puts($"   🎲 Применяю пресет: {presetKey}");

    bool presetApplied = false;
    var sc = ent as StorageContainer;
    if (Loottable != null && sc != null)
    {
        var ok = (bool)(Loottable.Call("AssignPreset", this, presetKey, sc) ?? false);
        presetApplied = ok;
        if (!ok)
            Puts($"   ⚠️ Не удалось применить пресет '{presetKey}' — проверь, что он создан и включён в Loottable UI (категория Helltrain).");
    }

    // fallback: если A/B не применился, пробуем то, что записано в объекте (preset/presets)
    if (!presetApplied && sc != null && Loottable != null)
    {
        string fallback = null;
        if (obj.presets != null && obj.presets.Length > 0)
            fallback = obj.presets[UnityEngine.Random.Range(0, obj.presets.Length)];
        else if (!string.IsNullOrEmpty(obj.preset))
            fallback = obj.preset;

        if (!string.IsNullOrEmpty(fallback))
        {
            var ok2 = (bool)(Loottable.Call("AssignPreset", this, fallback, sc) ?? false);
            if (ok2)
                Puts($"   ✅ Fallback пресет применён: {fallback}");
            else
                Puts($"   ⚠️ Fallback пресет '{fallback}' тоже не применился.");
        }
    }

    break;
}

        
        BaseEntity entity = GameManager.server.CreateEntity(prefab, worldPos, worldRot);
        if (entity == null) continue;
        
        entity.enableSaving = false;
        entity.Spawn();
        Track(entity);

		
		
        
        // --- АКТИВАЦИЯ В БОЕВОМ РЕЖИМЕ (runtime) ---
        var npcCast = entity as ScientistNPC;
        if (npcCast != null)
        {
            var brain = npcCast.GetComponent<BaseAIBrain>();
            if (brain != null) brain.enabled = true;

            var nav = npcCast.GetComponent<BaseNavigator>();
            if (nav != null)
            {
                nav.CanUseNavMesh = true;
                nav.SetDestination(npcCast.transform.position, BaseNavigator.NavigationSpeed.Normal, 0f);
                nav.ClearFacingDirectionOverride();
            }

            npcCast.SetPlayerFlag(BasePlayer.PlayerFlags.SafeZone, false);
            npcCast.InvalidateNetworkCache();
            npcCast.SendNetworkUpdateImmediate();
        }
        else
        {
            var at = entity as AutoTurret;
            if (at != null)
            {
                at.SetFlag(IOEntity.Flag_HasPower, true, false, true);
                at.UpdateFromInput(100, 0);
                at.SetFlag(BaseEntity.Flags.On, true, false, true);
                at.InvalidateNetworkCache();
                at.SendNetworkUpdateImmediate();
            }
            else
            {
                var samRT = entity as SamSite;
                if (samRT != null)
                {
                    samRT.SetFlag(IOEntity.Flag_HasPower, true, false, true);
                    samRT.SetFlag(BaseEntity.Flags.On, true, false, true);
                    samRT.InvalidateNetworkCache();
                    samRT.SendNetworkUpdateImmediate();
                }
            }
        }


		
		

        if (entity is AutoTurret turret)
            _spawnedTurrets.Add(turret);
        else if (entity is SamSite samSite)
            _spawnedSamSites.Add(samSite);
        else if (entity is ScientistNPC npcRT)
            _spawnedNPCs.Add(npcRT);
        
        NextTick(() =>
        {
            if (entity == null || entity.IsDestroyed || trainCar == null || trainCar.IsDestroyed)
                return;
            
            bool shouldParent = !(entity is ScientistNPC);
            
            if (shouldParent)
            {
                entity.SetParent(trainCar, false, false);
                entity.transform.localPosition = localPos;
                entity.transform.localRotation = localRot;
                entity.SendNetworkUpdate();
            }
            
            if (entity is AutoTurret turret)
            {
                var turretComponent = turret.gameObject.AddComponent<TrainAutoTurret>();
                turretComponent.plugin = this;
                
                if (!string.IsNullOrEmpty(obj.gun))
                {
                    timer.Once(2.0f, () =>
                    {
                        if (turret == null || turret.IsDestroyed)
                            return;

                        GiveTurretWeapon(turret, obj.gun, obj.ammo, obj.ammo_count);
                    });
                }
            }
            else if (entity is SamSite samRT)
            {
                samRT.gameObject.AddComponent<TrainSamSite>();
            }
            else if (entity is ScientistNPC npc)
{
    npc.gameObject.AddComponent<HellTrainDefender>();
    
    BaseAIBrain brain = npc.GetComponent<BaseAIBrain>();
    if (brain != null)
    {
        brain.enabled = true;
        brain.ForceSetAge(0);
    }
    
    var marker = npc.gameObject.AddComponent<NPCTypeMarker>();
    marker.npcType = obj.npc_type;
	marker.savedKit = obj.kit;                              // сохранить кит из JSON
marker.savedKits = obj.kits != null ? new List<string>(obj.kits) : new List<string>();

    
    // ✅ КРИТИЧНО: Захватываем obj в локальную переменную!
    ObjSpec capturedObj = obj;
    
    timer.Once(1.0f, () =>
    {
        if (npc == null || npc.IsDestroyed || npc.inventory == null)
            return;

        Puts($"   🎯 Выдаём предметы NPC ({marker.npcType})...");
        GiveNPCItems(npc, capturedObj);  // ← Используем ЗАХВАЧЕННЫЙ obj!
    });
}
            
            Puts($"   🎯 Заспавнен: {obj.type} на {trainCar.ShortPrefabName}");
        });
    }
}
}

private Vector3 ReadLocalPos(float[] a)
{
    if (a == null || a.Length < 3) return Vector3.zero;
    return new Vector3(a[0], a[1], a[2]);
}

private Quaternion ReadLocalRot(float[] a)
{
    if (a == null || a.Length < 3) return Quaternion.identity;
    return Quaternion.Euler(a[0], a[1], a[2]);
}
// === GENERATOR helpers (веса/рандом) ===
private int PickNpcCount(int slotCount, Dictionary<int, float> weights)
{
    if (slotCount <= 0) return 0;

    // Директива MAIN: диапазон строго 2–4 (а ограничение сверху = количество слотов)
    int lo = 2;
    int hi = Mathf.Min(4, slotCount);
    if (hi < lo) return hi; // если слотов 0/1 — вернёт 0/1

    if (weights == null || weights.Count == 0)
        return UnityEngine.Random.Range(lo, hi + 1);

    double total = 0;
    for (int c = lo; c <= hi; c++)
    {
        float w = 1f;
        if (weights.TryGetValue(c, out var ww)) w = Mathf.Max(0.0001f, ww);
        total += w;
    }
    if (total <= 0.0001) return UnityEngine.Random.Range(lo, hi + 1);

    double roll = UnityEngine.Random.Range(0f, 1f) * total;
    for (int c = lo; c <= hi; c++)
    {
        float w = 1f;
        if (weights.TryGetValue(c, out var ww)) w = Mathf.Max(0.0001f, ww);
        roll -= w;
        if (roll <= 0) return c;
    }
    return hi;
}


private int PickNpcCount(int min, int max, int slotCount, Dictionary<int, float> weights)
{
    if (slotCount <= 0) return 0;
    if (min < 0) min = 0;
    if (max < min) max = min;

    int hi = System.Math.Min(max, slotCount);
    int lo = System.Math.Min(min, hi);

    // Если веса не заданы — равномерно
    if (weights == null || weights.Count == 0)
        return UnityEngine.Random.Range(lo, hi + 1);

    double total = 0;
    for (int c = lo; c <= hi; c++)
    {
        float w = 1f;
        if (weights.TryGetValue(c, out var ww))
            w = Mathf.Max(0.0001f, ww);
        total += w;
    }

    if (total <= 0.0001)
        return UnityEngine.Random.Range(lo, hi + 1);

    double roll = UnityEngine.Random.Range(0f, 1f) * total;
    for (int c = lo; c <= hi; c++)
    {
        float w = 1f;
        if (weights.TryGetValue(c, out var ww))
            w = Mathf.Max(0.0001f, ww);
        roll -= w;
        if (roll <= 0) return c;
    }
    return hi;
}


private bool RollCrateSpawn(Dictionary<string, float> weights)
{
    float noneW = 1f;
    float defW  = 1f;

    if (weights != null)
    {
        if (weights.TryGetValue("None", out var n)) noneW = n;
        if (weights.TryGetValue("DefaultCrate", out var d)) defW = d;
    }

    noneW = Mathf.Max(0f, noneW);
    defW  = Mathf.Max(0f, defW);
    float sum = noneW + defW;
    if (sum <= 0.0001f) return true;

    float r = UnityEngine.Random.Range(0f, sum);
    return r >= noneW; // true=DefaultCrate, false=None
}


// MAIN: запрещено брать веса из layout. Layout = только геометрия.


private bool RollCrateSpawn(float noneW, float defW)
{
    noneW = Mathf.Max(0f, noneW);
    defW  = Mathf.Max(0f, defW);
    float sum = noneW + defW;
    if (sum <= 0.0001f) return true;
    float r = UnityEngine.Random.Range(0f, sum);
    return r >= noneW; // true=DefaultCrate, false=None
}

// === DIFF#3: Random Composition builder (GENERATOR) ===
private List<string> BuildRandomCompositionWagons(ConfigData.TrainComposition comp)

{
    var result = new List<string>();

    if (comp == null)
        return result;

    // 0) Абсолютное правило: локомотив обязателен и всегда первый
    if (string.IsNullOrEmpty(comp.Loco))
    {
        PrintWarning("[Helltrain] DIFF#3: Composition has no Loco (required).");
        return result;
    }
    result.Add(comp.Loco);

    // 1) Сколько вагонов (кроме локомотива)
    int min = Mathf.Max(0, comp.MinWagons);
    int max = Mathf.Max(min, comp.MaxWagons);
    int wagonsToAdd = UnityEngine.Random.Range(min, max + 1);

    if (wagonsToAdd <= 0)
        return result;

    // 2) Кандидаты из WagonPools: (type, layoutName, weight)
    var candidates = new List<(string type, string name, float w)>();
    if (comp.WagonPools != null)
    {
        foreach (var kv in comp.WagonPools)
        {
            var type = kv.Key;
            var pool = kv.Value;
            if (string.IsNullOrEmpty(type) || pool == null) continue;

            foreach (var e in pool)
            {
                if (string.IsNullOrEmpty(e.Key)) continue;
                float w = Mathf.Max(0f, e.Value);
                if (w <= 0f) continue;
                candidates.Add((type, e.Key, w));
            }
        }
    }

    if (candidates.Count == 0)
    {
        PrintWarning("[Helltrain] DIFF#3: WagonPools empty, only Loco will spawn.");
        return result;
    }
	
	Puts($"[Helltrain][DBG] WagonPools keys: {string.Join(",", comp.WagonPools.Keys)}");
Puts($"[Helltrain][DBG] Limits keys: {(comp.Limits == null ? "null" : string.Join(",", comp.Limits.Keys))}");
Puts($"[Helltrain][DBG] Candidates sample: {string.Join(" | ", candidates.Take(10).Select(x => $"{x.type}:{x.name}:{x.w}"))}");

	

    // 3) Лимиты по типам
    var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    bool TypeLimitReached(string type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        if (comp.Limits == null) return false;
        if (!comp.Limits.TryGetValue(type, out var lim)) return false;
        if (lim < 0) return false;
        typeCounts.TryGetValue(type, out var cur);
        return cur >= lim;
    }

    void IncType(string type)
    {
        if (string.IsNullOrEmpty(type)) return;
        typeCounts.TryGetValue(type, out var cur);
        typeCounts[type] = cur + 1;
    }

    // 4) Выбор строго с учётом лимитов
for (int i = 0; i < wagonsToAdd; i++)
{
    // выбираем без нарушения лимитов
    var pick = PickCandidate(candidates, TypeLimitReached, ignoreLimits: false);

    // если упёрлись в лимиты — прекращаем добавление вагонов
    if (pick.name == null)
        break;

    result.Add(pick.name);
	Puts($"[Helltrain][DBG_WAGON_PICK] i={i} type={pick.type} name={pick.name}");
    IncType(pick.type);
}


    return result;
}

private (string type, string name) PickCandidate(
    List<(string type, string name, float w)> candidates,
    Func<string, bool> isTypeLimited,
    bool ignoreLimits)
{
    double total = 0;
    for (int i = 0; i < candidates.Count; i++)
    {
        var c = candidates[i];
        if (!ignoreLimits && isTypeLimited(c.type)) continue;
        total += c.w;
    }

    if (total <= 0.0001)
        return (null, null);

    double roll = UnityEngine.Random.Range(0f, 1f) * total;
    for (int i = 0; i < candidates.Count; i++)
    {
        var c = candidates[i];
        if (!ignoreLimits && isTypeLimited(c.type)) continue;

        roll -= c.w;
        if (roll <= 0)
            return (c.type, c.name);
    }

    // fallback
    for (int i = 0; i < candidates.Count; i++)
    {
        var c = candidates[i];
        if (!ignoreLimits && isTypeLimited(c.type)) continue;
        return (c.type, c.name);
    }

    return (null, null);
}


private void SpawnLayoutSlots(TrainCar trainCar, TrainLayout layout)
{
    if (trainCar == null || trainCar.IsDestroyed || layout == null) return;

    var gen = config?.Generator;
    if (gen == null || gen.Factions == null)
    {
        PrintWarning("[Helltrain] Generator config missing (Generator/Factions).");
        return;
    }

    var factionKey = (_activeFactionKey ?? "BANDIT").ToUpperInvariant();
    if (!gen.Factions.TryGetValue(factionKey, out var factionGen) || factionGen == null)
    gen.Factions.TryGetValue("BANDIT", out factionGen);

if (factionGen == null)
{
    PrintWarning($"[Helltrain] Generator faction missing: {factionKey} (and BANDIT fallback missing)");
    return;
}



    // 1) Shelves
    if (layout.Shelves != null)
    {
        foreach (var sh in layout.Shelves)
        {
            if (sh == null || string.IsNullOrEmpty(sh.prefab)) continue;

            var lp = ReadLocalPos(sh.pos);
            var lr = ReadLocalRot(sh.rot);

            Vector3 worldPos = trainCar.transform.TransformPoint(lp);
            Quaternion worldRot = trainCar.transform.rotation * lr;

            var ent = GameManager.server.CreateEntity(sh.prefab, worldPos, worldRot);
            if (ent == null) continue;

            ent.enableSaving = false;
            ent.SetParent(trainCar, false, false);
            ent.transform.localPosition = lp;
            ent.transform.localRotation = lr;

            var rb = ent.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }

            ent.Spawn();
            Track(ent); // ✅ MAIN: чтобы Stop/Cleanup не оставлял сирот
        }
    }

    // 2) NPC slots (GENERATOR: N по весам/рандому, без повторов слотов, minDistance только NPC↔NPC, retryLimit=5)
if (layout.NpcSlots != null && layout.NpcSlots.Count > 0)
{
    int slotCount = layout.NpcSlots.Count;

// MAIN: диапазон 2–4 задаётся весами фракции (логика строго в конфиге)
float minDist = gen.NpcMinDistanceMeters;
int retryLimit = gen.NpcRetryLimit;

int n = PickNpcCount(slotCount, factionGen?.NPCCountWeights);


    var used = new HashSet<int>();
    var chosenWorld = new List<Vector3>(n);

    for (int k = 0; k < n; k++)
    {
        int pickedIdx = -1;

        // Пытаемся соблюсти дистанцию
        for (int attempt = 0; attempt < retryLimit; attempt++)
        {
            if (used.Count >= slotCount) break;

            int idx;
            int guard = 0;
            do
            {
                idx = UnityEngine.Random.Range(0, slotCount);
                guard++;
                if (guard > 1000) break;
            }
            while (used.Contains(idx));

            if (used.Contains(idx)) break;

            var sTry = layout.NpcSlots[idx];
            if (sTry == null) continue;

            Vector3 worldTry = trainCar.transform.TransformPoint(ReadLocalPos(sTry.pos));

            bool ok = true;
            for (int p = 0; p < chosenWorld.Count; p++)
            {
                if (Vector3.Distance(chosenWorld[p], worldTry) < minDist)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;

            pickedIdx = idx;
            break;
        }

        // Если не нашли — игнорим дистанцию, но слот не повторяем
        if (pickedIdx == -1)
        {
            if (used.Count >= slotCount) break;

            int guard = 0;
            do
            {
                pickedIdx = UnityEngine.Random.Range(0, slotCount);
                guard++;
                if (guard > 1000) { pickedIdx = -1; break; }
            }
            while (used.Contains(pickedIdx));

            if (pickedIdx == -1) break;
        }

        used.Add(pickedIdx);

        var s = layout.NpcSlots[pickedIdx];
        if (s == null) continue;
		int npcSlot = _activeNpcSlotCursor;
_activeNpcSlotCursor++;

string kitKey =
    (_activeNpcAssignments != null && npcSlot < _activeNpcAssignments.Count)
        ? _activeNpcAssignments[npcSlot]?.kitKey
        : null;

if (string.IsNullOrEmpty(kitKey) || kitKey.Equals("None", StringComparison.OrdinalIgnoreCase))
{
    Puts($"[NPC SKIP] slot={npcSlot} reason=NO_KIT_IN_PLAN");
    continue;
}


        BaseEntity ent = null;

        try
        {
            var lp = ReadLocalPos(s.pos);
            var lr = ReadLocalRot(s.rot);

            Vector3 worldPos = trainCar.transform.TransformPoint(lp);
            Quaternion worldRot = trainCar.transform.rotation * lr;

            ent = GameManager.server.CreateEntity(SCIENTIST_PREFAB, worldPos, worldRot);
            if (ent == null)
            {
                PrintWarning($"[Helltrain] Slot NPC: CreateEntity NULL (idx={pickedIdx}, prefab={SCIENTIST_PREFAB}, layout={layout?.name})");
                continue;
            }

            ent.enableSaving = false;
            // NPC не parent’им к TrainCar
            ent.transform.position = worldPos;
            ent.transform.rotation = worldRot;

            ent.Spawn();
            Track(ent);
var npc = ent as ScientistNPC;
if (npc == null)
{
    PrintWarning($"[NPC SKIP] slot={npcSlot} reason=NOT_SCIENTIST type={ent.GetType().Name}");
    ent.Kill();
    continue;
}

_spawnedNPCs.Add(npc);

// STRICT: никаких дефолтных NPC
npc.inventory?.Strip();

var result = KitsSuite?.Call("GiveKit", (BaseEntity)npc, kitKey);

// нормализуем успех (разные плагины возвращают по-разному)
bool ok = true;
if (result is bool b) ok = b;
else ok = (result != null);

if (ok)
{
    Puts($"[NPC SPAWN] slot={npcSlot} kitKey={kitKey} result=OK");
}
else
{
    PrintWarning($"[NPC SPAWN] slot={npcSlot} kitKey={kitKey} result=FAIL_GIVEKIT");
    npc.Kill(); // нет голых NPC
}

            chosenWorld.Add(worldPos);
 }
        catch (Exception ex)
        {
            PrintWarning($"[Helltrain] Slot NPC spawn error (idx={pickedIdx}, layout={layout?.name}): {ex}");
            if (ent != null && !ent.IsDestroyed)
            {
                try { ent.Kill(); } catch {}
            }
        }
    }
}



    // 3) Crate slots (обычный crate + A/B assign как в legacy)
    if (layout.CrateSlots != null && layout.CrateSlots.Count > 0)
    {
        string factionUpper = (_activeFactionKey ?? "BANDIT").ToUpperInvariant();
string lootPrefab = GetCratePrefabForFaction(factionUpper);
Puts($"[CRATE SPAWN CFG] faction={factionUpper} layout={layout?.name} lootPrefab={lootPrefab} crateSlots={layout.CrateSlots.Count} planSlots={_activeCrateAssignments?.Count ?? 0}");



               for (int i = 0; i < layout.CrateSlots.Count; i++)
{
    var s = layout.CrateSlots[i];
    if (s == null) continue;

    // Step 2.2 — фиксированный порядок, без прединкремента
    int slotIndex = _activeCrateSlotCursor;
    _activeCrateSlotCursor++; // ++ РОВНО 1 раз на каждый реальный crate-slot

    var a = (_activeCrateAssignments != null && slotIndex < _activeCrateAssignments.Count)
    ? _activeCrateAssignments[slotIndex]
    : null;

string lootKey = a?.lootKey ?? "None";
string prefabPath = a?.prefabPath; // new format
string spawnPrefab = !string.IsNullOrEmpty(prefabPath) ? prefabPath : lootPrefab; // legacy fallback

if (slotIndex < 16)
    Puts($"[CRATE SLOT] slotIndex={slotIndex} lootKey={lootKey} prefabPath={(prefabPath ?? "legacy")}");

if (string.IsNullOrEmpty(lootKey) || lootKey.Equals("None", StringComparison.OrdinalIgnoreCase))
    continue;

// вычисляем координаты слота (как в NPC-блоке выше)
var lp = ReadLocalPos(s.pos);
var lr = ReadLocalRot(s.rot);

Vector3 worldPos = trainCar.transform.TransformPoint(lp);
Quaternion worldRot = trainCar.transform.rotation * lr;

Puts($"[CRATE SPAWN TRY] slotIndex={slotIndex} lootKey={lootKey} prefab={spawnPrefab}");

var ent = GameManager.server.CreateEntity(spawnPrefab, worldPos, worldRot);



if (ent == null)
{
    PrintWarning($"[CRATE SPAWN FAIL] CreateEntity returned null i={i} lootKey={lootKey} lootPrefab={lootPrefab} layout={layout?.name} worldPos={worldPos}");

    continue;
}


            ent.enableSaving = false;
            ent.SetParent(trainCar, false, false);
            ent.transform.localPosition = lp;
            ent.transform.localRotation = lr;

            var combat = ent as BaseCombatEntity;
            if (combat != null) combat.InitializeHealth(5000f, 5000f);

            var rb = ent.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }

           ent.Spawn();
Track(ent); // ✅ MAIN: чтобы Stop/Cleanup чистил эти ящики

// OK лог сразу после Spawn
ulong spawnedId = (ent.net != null) ? ent.net.ID.Value : 0UL;
Puts($"[CRATE SPAWN OK] slotIndex={slotIndex} lootKey={lootKey} id={spawnedId} worldPos={worldPos} parent={(trainCar != null ? trainCar.ShortPrefabName : "null")}");



// Проверка на следующем тике: жив ли, есть ли net
var entRef = ent; // захват ссылки
timer.Once(0f, () =>
{
    try
    {
       if (entRef == null) { PrintWarning($"[CRATE SPAWN TICK] slotIndex={slotIndex} lootKey={lootKey} ent=null"); return; }
if (entRef.IsDestroyed) { PrintWarning($"[CRATE SPAWN TICK] slotIndex={slotIndex} lootKey={lootKey} destroyed=true id={(entRef.net != null ? entRef.net.ID.Value : 0UL)}"); return; }
if (entRef.net == null) { PrintWarning($"[CRATE SPAWN TICK] slotIndex={slotIndex} lootKey={lootKey} net=null alive=true"); return; }

Puts($"[CRATE SPAWN TICK] slotIndex={slotIndex} lootKey={lootKey} alive=true id={entRef.net.ID.Value}");

    }
    catch (Exception ex)
    {
       PrintWarning($"[CRATE SPAWN TICK] exception i={i} lootKey={lootKey}: {ex.Message}");
    }
});


// net может быть null, если сущность не успела корректно заспавниться
if (ent.net == null)
{
    PrintWarning($"[Helltrain] Slot crate spawned but net==null (prefab={lootPrefab}, layout={layout?.name})");
    continue;
}


// учёт наших ящиков (как в legacy "loot")
ulong id = ent.net.ID.Value;
_ourCrates.Add(id);
_crateStates[id] = CrateState.Idle;
_crateFaction[id] = factionUpper;
// CORE Step 2: метка CrateTypeName из PopulatePlan (lootKey) для шага 3
_crateTypeName[id] = lootKey;



// A/B assign (без весов, прогрев)
var sc = ent as StorageContainer;
if (Loottable != null && sc != null)
{
    try
    {
        string presetKey = PickPresetAB(factionUpper);
        Loottable.Call("AssignPreset", this, presetKey, sc);
    }
    catch (Exception ex)
    {
        PrintWarning($"[Helltrain] Loottable AssignPreset error (slot crate): {ex.Message}");
    }
}

        }
		Puts($"[CRATE CURSOR END] cursorEnd={_activeCrateSlotCursor} planSlots={_activeCrateAssignments?.Count ?? 0}");

    }
}


private string GetKitForNPC(ObjSpec obj)
{
    if (!string.IsNullOrEmpty(obj.kit))
        return obj.kit;
    
    if (obj.kits != null && obj.kits.Count > 0)
    {
        int index = _rng.Next(0, obj.kits.Count);
        return obj.kits[index];
    }
       
    return null;
}

private void GiveTurretWeapon(AutoTurret turret, string gun, string ammo, int ammoCount)
{
    if (turret == null || turret.IsDestroyed || turret.inventory == null)
    {
        PrintWarning($"❌ GiveTurretWeapon: turret недоступна!");
        return;
    }
    
    Puts($"🔧 Выдаём оружие турели: gun={gun}, ammo={ammo}, count={ammoCount}");
    
    turret.inventory.Clear();
    ItemManager.DoRemoves();
    
    string weaponShortname = gun?.ToLower();
    if (string.IsNullOrEmpty(weaponShortname))
        weaponShortname = "lmg.m249";

    switch (weaponShortname)
    {
        case "m249": weaponShortname = "lmg.m249"; break;
        case "ak": weaponShortname = "rifle.ak"; break;
        case "lr300":
        case "lr": weaponShortname = "rifle.lr300"; break;
        case "mp5": weaponShortname = "smg.mp5"; break;
    }

    var weaponDef = ItemManager.FindItemDefinition(weaponShortname);
    if (weaponDef == null)
    {
        PrintWarning($"❌ Не найден ItemDefinition: {weaponShortname}");
        return;
    }
    
    var weaponItem = ItemManager.Create(weaponDef, 1, 0);
    if (weaponItem == null || !weaponItem.MoveToContainer(turret.inventory, 0, true))
    {
        PrintWarning($"❌ Не удалось добавить оружие!");
        weaponItem?.Remove();
        return;
    }
    
    Puts($"   ✅ Оружие добавлено в слот 0");
    
    if (string.IsNullOrEmpty(ammo))
        ammo = "ammo.rifle";
    
    if (ammoCount <= 0)
        ammoCount = 500;
    
    var ammoDef = ItemManager.FindItemDefinition(ammo);
    if (ammoDef != null)
    {
        var ammoItem = ItemManager.Create(ammoDef, ammoCount, 0);
        if (ammoItem != null && ammoItem.MoveToContainer(turret.inventory, 1, true))
        {
            Puts($"   ✅ Патроны добавлены в слот 1");
        }
        else
        {
            ammoItem?.Remove();
        }
    }
    
    NextTick(() => 
    {
        if (turret == null || turret.IsDestroyed)
            return;
        
        turret.UpdateAttachedWeapon();
        turret.UpdateTotalAmmo();
        turret.SendNetworkUpdate();
        
        Puts($"   ✅ Турель готова к бою!");
    });
}

private void GiveNPCItems(ScientistNPC npc, ObjSpec obj)
{
    if (npc == null || npc.inventory == null)
    {
        Puts("❌ NPC или инвентарь null!");
        return;
    }

    // 1) Сначала полностью чистим дефолтный лут у NPC (убираем синий хазмат и т.д.)
    npc.inventory.Strip();

    // 2) Кит берём ТОЛЬКО из obj.kit / obj.kits (НИКАК НЕ ИЗ npcType!)
    string kitName = GetKitForNPC(obj);

    Puts("════════════════════════════════════════");
    Puts($"🎯 GiveNPCItems:");
    Puts($"   obj.kit = '{obj?.kit ?? "NULL"}'");
    Puts($"   obj.kits.Count = {obj?.kits?.Count ?? 0}");
    Puts($"   выбранный kitName = '{kitName ?? "NULL"}'");
    Puts("════════════════════════════════════════");

    if (string.IsNullOrEmpty(kitName))
    {
        Puts("⚠️ Кит не задан в лэйауте (obj.kit/obj.kits пусто) — ничего не выдаю, чтобы не было рандом-хазмата.");
        return;
    }

    // 3) Выдаём кит через KitsSuite
    var result = KitsSuite?.Call("GiveKit", (BaseEntity)npc, kitName);
    Puts($"📞 KitsSuite.GiveKit('{kitName}') => {result} (тип: {result?.GetType().Name ?? "null"})");
	timer.Once(0.25f, () =>
{
    if (npc == null || npc.IsDestroyed || npc.inventory == null) return;
    // если одежда лежит в main — перекинем в wear
    foreach (var it in npc.inventory.containerMain.itemList.ToArray())
        if (it.info.category == ItemCategory.Attire)
            it.MoveToContainer(npc.inventory.containerWear, -1, true);
    npc.SendNetworkUpdate();
});


    // 4) Проверка/добивка через 1.0с: активируем оружие и убеждаемся, что броня в wear
    timer.Once(1.0f, () =>
    {
        if (npc == null || npc.IsDestroyed || npc.inventory == null)
            return;

        // Активируем первое оружие на поясе, если есть
        Item firstWeapon = null;
        if (npc.inventory?.containerBelt != null)
        {
            foreach (var item in npc.inventory.containerBelt.itemList)
            {
                if (item?.GetHeldEntity() is BaseProjectile)
                {
                    firstWeapon = item;
                    break;
                }
            }
        }
        if (firstWeapon != null)
            npc.UpdateActiveItem(firstWeapon.uid);

        // Если по какой-то причине весь инвентарь пуст — НИКАКИХ фолбеков на синий хазмат,
        // лучше оставить пустым, чтобы сразу видно было проблему кита.
        int total =
            (npc.inventory.containerMain?.itemList?.Count ?? 0) +
            (npc.inventory.containerBelt?.itemList?.Count ?? 0) +
            (npc.inventory.containerWear?.itemList?.Count ?? 0);

        if (total == 0)
            Puts($"❌ Кит '{kitName}' ничего не выдал (инвентарь пуст). Проверь пресет в KitsSuite.");
    });
}

private Item GiveItem(ScientistNPC npc, string shortname, int amount, ulong skin, string containerName)
{
    var def = ItemManager.FindItemDefinition(shortname);
    if (def == null)
    {
        Puts($"   ❌ Не найден ItemDefinition: {shortname}");
        return null;
    }
    
    var item = ItemManager.Create(def, amount, skin);
    if (item == null)
    {
        Puts($"   ❌ Не удалось создать Item: {shortname}");
        return null;
    }
    
    ItemContainer container = null;
    
    switch (containerName)
    {
        case "wear":
            container = npc.inventory.containerWear;
            break;
        case "belt":
            container = npc.inventory.containerBelt;
            break;
        default:
            container = npc.inventory.containerMain;
            break;
    }
    
    if (container == null || !item.MoveToContainer(container, -1, true))
    {
        item.Remove();
        Puts($"   ❌ Не удалось переместить {shortname} в {containerName}");
        return null;
    }
    
    return item;
}




#endregion


#region HT.LOOT.LOOTTABLE


private string GetRandomLootPreset(string faction)
{
    switch ((faction ?? "BANDIT").ToUpper())
    {
        case "PMC":    return UnityEngine.Random.value < 0.5f ? "pmc_weapon"    : "pmc_other";
        case "COBLAB": return UnityEngine.Random.value < 0.5f ? "coblab_weapon" : "coblab_medeat";
        default:       return UnityEngine.Random.value < 0.5f ? "bandit_weapon" : "bandit_medeat";
    }
}

private string QualifyLtPreset(string name)
{
    return name != null && !name.StartsWith("Helltrain_", StringComparison.OrdinalIgnoreCase)
        ? $"Helltrain_{name}"
        : name;
}



// ЗАМЕНИ оба объявления TryAssignLoottable на этот ОДИН метод
private void TryAssignLoottable(ItemContainer container, string preset)
{
    if (container == null || string.IsNullOrEmpty(preset)) return;
    if (!plugins.Exists("Loottable") || Loottable == null) return;

    Puts($"   🎲 Применяю пресет: {preset}");
    bool ok = false;

    var r1 = Loottable.Call("AssignPreset", this, preset, container);
    if (r1 is bool b1 && b1) ok = true;

    if (!ok)
    {
        Loottable.Call("PopulateContainer", this, preset, container);
        ok = container.itemList != null && container.itemList.Count > 0;
    }
    if (!ok)
    {
        Loottable.Call("ApplyPreset", this, preset, container);
        ok = container.itemList != null && container.itemList.Count > 0;
    }

    if (!ok)
        PrintWarning($"   ⚠️ Не удалось применить пресет '{preset}' — проверь точное имя в Loottable UI.");
}



#endregion


        #region РЕДАКТОР ЛЭЙАУТОВ

private readonly Hash<ulong, WagonEditor> m_WagonEditors = new Hash<ulong, WagonEditor>();
// === HELLTRAIN CRATES SYSTEM ===
private readonly List<ulong> _ourCrates = new List<ulong>();
private readonly Dictionary<ulong, CrateState> _crateStates = new Dictionary<ulong, CrateState>();
private readonly Dictionary<ulong, string> _crateFaction = new Dictionary<ulong, string>();


[ChatCommand("htreload")]
private void CmdHtReload(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }
    
    player.ChatMessage("🔄 ПОЛНАЯ перезагрузка всех лэйаутов...");
    
    // Очищаем кеш и перезагружаем всё
    _layouts.Clear();
    LoadLayouts();
    
    player.ChatMessage($"✅ Лэйауты перезагружены! Найдено: {_layouts.Count}");
    
    foreach (var kv in _layouts)
    {
        int objCount = kv.Value.objects?.Count ?? 0;
        player.ChatMessage($"   • {kv.Key}: {objCount} объектов");
    }
}

[ChatCommand("htedit")]
private void CmdHtEdit(BasePlayer player, string command, string[] args)
{
    if (!player.IsAdmin)
    {
        player.ChatMessage("❌ Только для админов!");
        return;
    }

    if (args.Length == 0)
    {
        player.ChatMessage("📋 Команды редактора:");
        player.ChatMessage("/htedit load <layoutName> - Открыть лэйаут");
        player.ChatMessage("/htedit save - Сохранить изменения");
        player.ChatMessage("/htedit cancel - Закрыть без сохранения");
        
        if (m_WagonEditors.ContainsKey(player.userID))
        {
            player.ChatMessage("/htedit move - Переместить объект (смотри на него)");
            player.ChatMessage("/htedit spawn <type> - Создать npc/turret/bradley/samsite/loot");
            player.ChatMessage("/htedit delete - Удалить объект (смотри на него)");
            player.ChatMessage("");
            player.ChatMessage("💡 ЛКМ - разместить | ПКМ - отмена | RELOAD - поворот");
        }
        return;
    }

    m_WagonEditors.TryGetValue(player.userID, out WagonEditor wagonEditor);

    switch (args[0].ToLower())
{
    case "load":
{
    if (wagonEditor)
    {
        player.ChatMessage("⚠️ Сначала закрой текущий редактор: /htedit save или /htedit cancel");
        return;
    }

    if (args.Length != 2)
    {
        player.ChatMessage("❌ Укажи имя: /htedit load wagonC_pmc");
        player.ChatMessage("📋 Доступные:");
        var dir = Path.Combine(Interface.Oxide.DataDirectory, "Helltrain/Layouts");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var files = Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(x => x)
            .ToList();

        if (files.Count == 0)
        {
            player.ChatMessage("❌ Нет лэйаутов в oxide/data/Helltrain/Layouts/");
        }
        else
        {
            player.ChatMessage("📂 Доступные лэйауты:\n" + string.Join(", ", files));
        }
        return;
    }

    LoadLayouts(); // ← перечитываем лэйауты
    string layoutName = args[1].ToLower();

    if (!_layouts.ContainsKey(layoutName))
    {
        player.ChatMessage($"❌ Лэйаут '{layoutName}' не найден даже после обновления. Проверь имя файла в oxide/data/Helltrain/Layouts/");
        return;
    }

    if (!TrainTrackSpline.TryFindTrackNear(player.transform.position, 20f, out TrainTrackSpline spline, out float dist))
    {
        player.ChatMessage("⚠️ Рельсы не найдены! Подойди ближе к ним.");
        return;
    }

    var layout = _layouts[layoutName];
	if (string.IsNullOrEmpty(layout.name))
{
    layout.name = layoutName;
    Interface.Oxide.DataFileSystem.WriteObject($"Helltrain/Layouts/{layout.name}", layout, true);
}

    if (layout == null)
    {
        player.ChatMessage($"❌ Лэйаут '{args[1]}' не найден!");
        return;
    }

    // загрузка в редактор
    Vector3 pos = spline.GetPosition(dist);
    Vector3 fwd = spline.GetTangentCubicHermiteWorld(dist);
    Quaternion rot = fwd.magnitude > 0 ? Quaternion.LookRotation(fwd) * Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;

    string prefab = WagonPrefabC;
    if (layout.cars != null && layout.cars.Count > 0)
        prefab = GetWagonPrefabByVariant(layout.cars[0].variant);

    TrainCar trainCar = GameManager.server.CreateEntity(prefab, pos, rot) as TrainCar;
    trainCar.enableSaving = true;
    trainCar.frontCoupling = null;
    trainCar.rearCoupling = null;
    trainCar.platformParentTrigger.ParentNPCPlayers = true;
    trainCar.Spawn();

    wagonEditor = player.gameObject.AddComponent<WagonEditor>();
    wagonEditor.Load(trainCar, layout, this);
    m_WagonEditors[player.userID] = wagonEditor;

    player.ChatMessage($"✅ Редактор: {args[1]}");
    player.ChatMessage($"📦 Загружено объектов: {layout.objects?.Count ?? 0}");
    return;
}



    case "save":
    {
        if (!wagonEditor)
        {
            player.ChatMessage("❌ Редактор не открыт!");
            return;
        }

        wagonEditor.Save();
        UnityEngine.Object.Destroy(wagonEditor);

        m_WagonEditors.Remove(player.userID);
        player.ChatMessage("✅ Сохранено и закрыто");
        return;
    }

    case "cancel":
    {
        if (!wagonEditor)
        {
            player.ChatMessage("❌ Редактор не открыт!");
            return;
        }

        UnityEngine.Object.Destroy(wagonEditor);

        m_WagonEditors.Remove(player.userID);
        player.ChatMessage("✅ Редактор закрыт без сохранения");
        return;
    }

    case "move":
    {
        if (!wagonEditor)
        {
            player.ChatMessage("❌ Редактор не открыт!");
            return;
        }

        BaseEntity baseEntity = WagonEditor.FindEntityFromRay(player);
        if (!baseEntity || !wagonEditor.IsTrainEntity(baseEntity))
        {
            player.ChatMessage("❌ Это не объект редактора!");
            return;
        }

        wagonEditor.StartEditingEntity(baseEntity, false);
        return;
    }

    case "spawn":
{
    if (!wagonEditor)
    {
        player.ChatMessage("❌ Редактор не открыт!");
        return;
    }

    if (args.Length < 2)
    {
                player.ChatMessage("❌ Использование (редактор = СЛОТЫ):");
        player.ChatMessage("/htedit spawn npcslot");
        player.ChatMessage("/htedit spawn crateslot");
        player.ChatMessage("/htedit spawn shelf <prefab>");
        player.ChatMessage("🖱️ ЛКМ - разместить | ПКМ - отмена | RELOAD - поворот");
        return;

    }

        string entityType = args[1].ToLower();
		// --- DIFF#1: slots/spawn preview (MAIN approved) ---
if (entityType == "npcslot" || entityType == "crateslot" || entityType == "shelf")
{
const string NPC_SLOT_MARKER_PREFAB   = "assets/prefabs/deployable/signs/sign.post.single.prefab";
const string CRATE_SLOT_MARKER_PREFAB = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";


    Vector3 worldPos = player.transform.position + (player.eyes.BodyForward() * 3f);
    Vector3 localPos = wagonEditor.TrainCar.transform.InverseTransformPoint(worldPos);

    if (entityType == "npcslot")
    {
        var ent = wagonEditor.CreateChildEntity(NPC_SLOT_MARKER_PREFAB, localPos, Quaternion.identity, null, null, null, 0, 0f);
        if (!ent) { player.ChatMessage("❌ Не удалось создать npcslot"); return; }

        ent.gameObject.AddComponent<SlotMarker>().kind = SlotMarker.Kind.Npc;

// Freeze preview NPC (чтобы не двигался/не агрил)
var npc = ent as ScientistNPC;
if (npc != null)
{
    var brain = npc.GetComponent<BaseAIBrain>();
    if (brain != null) brain.enabled = false;

    var nav = npc.GetComponent<BaseNavigator>();
    if (nav != null)
    {
        nav.CanUseNavMesh = false;
        nav.SetDestination(npc.transform.position, BaseNavigator.NavigationSpeed.Slow, 0f);
    }

    npc.CancelInvoke();
    npc.SendNetworkUpdate();
}

wagonEditor.GetChildren().Add(ent);
wagonEditor.StartEditingEntity(ent, true);
player.ChatMessage("✅ NPC SLOT (preview man) создан");
return;

    }

    if (entityType == "crateslot")
    {
        var ent = wagonEditor.CreateChildEntity(CRATE_SLOT_MARKER_PREFAB, localPos, Quaternion.identity, null, null, null, 0, 0f);
        if (!ent) { player.ChatMessage("❌ Не удалось создать crateslot"); return; }

        ent.gameObject.AddComponent<SlotMarker>().kind = SlotMarker.Kind.Crate;
        wagonEditor.GetChildren().Add(ent);
        wagonEditor.StartEditingEntity(ent, true);
        player.ChatMessage("✅ CRATE SLOT создан");
        return;
    }

    // shelf <prefab>
    if (args.Length < 3) { player.ChatMessage("❌ /htedit spawn shelf <prefab>"); return; }

    string shelfPrefab = args[2];
    var shelfEnt = wagonEditor.CreateChildEntity(shelfPrefab, localPos, Quaternion.identity, null, null, null, 0, 0f);
    if (!shelfEnt) { player.ChatMessage("❌ Не удалось создать shelf"); return; }

    shelfEnt.gameObject.AddComponent<ShelfMarker>().prefab = shelfPrefab;
    wagonEditor.GetChildren().Add(shelfEnt);
    wagonEditor.StartEditingEntity(shelfEnt, true);
    player.ChatMessage("✅ SHELF создан");
    return;
}
// --- /DIFF#1 ---
player.ChatMessage("❌ Неизвестный тип! (в DIFF#1 только: npcslot | crateslot | shelf <prefab>)");
return;

}



    case "delete":
    {
        if (!wagonEditor)
        {
            player.ChatMessage("❌ Редактор не открыт!");
            return;
        }

        BaseEntity baseEntity = WagonEditor.FindEntityFromRay(player);
        if (!baseEntity || !wagonEditor.IsTrainEntity(baseEntity))
        {
            player.ChatMessage("❌ Это не объект редактора!");
            return;
        }

        wagonEditor.DeleteWagonEntity(baseEntity);
        player.ChatMessage($"✅ Удалён: {baseEntity.ShortPrefabName}");
        return;
    }

    default:
        player.ChatMessage("❌ Неизвестная команда! Используй /htedit для списка");
        return;
}
}
[ChatCommand("ht")]
private void CmdHtTools(BasePlayer player, string command, string[] args)
{
    if (player == null) return;

    // editor/admin — ок
    if (!HasPerm(player, PERM_EDITOR) && !HasPerm(player, PERM_ADMIN))
    {
        player.ChatMessage("⛔ Нет прав.");
        return;
    }

    if (args == null || args.Length == 0)
    {
        player.ChatMessage("📋 /ht preflook — показать prefab под прицелом");
        player.ChatMessage("📋 /ht preftest <prefabPath> — проверить, создаётся ли prefab");
        return;
    }

    var sub = args[0].ToLowerInvariant();

    if (sub == "preflook")
    {
        var ent = WagonEditor.FindEntityFromRay(player);
        if (ent == null || ent.IsDestroyed)
        {
            player.ChatMessage("❌ Под прицелом ничего нет.");
            return;
        }

        player.ChatMessage($"✅ Prefab: {ent.ShortPrefabName}");
        player.ChatMessage($"✅ Path: {ent.PrefabName}");
        return;
    }

    if (sub == "preftest")
    {
        if (args.Length < 2)
        {
            player.ChatMessage("❌ Использование: /ht preftest <prefabPath>");
            return;
        }

        var prefab = args[1];
        var pos = player.transform.position + player.eyes.BodyForward() * 2f + Vector3.up * 0.5f;
        var rot = Quaternion.identity;

        var ent = GameManager.server.CreateEntity(prefab, pos, rot, true);
        if (ent == null)
        {
            player.ChatMessage($"❌ Не создался prefab: {prefab}");
            return;
        }

        ent.enableSaving = false;
        ent.Spawn();
        timer.Once(0.2f, () =>
        {
            if (ent != null && !ent.IsDestroyed) ent.Kill();
        });

        player.ChatMessage($"✅ Prefab OK: {prefab}");
        return;
    }

    player.ChatMessage("❌ Неизвестная подкоманда. /ht preflook | /ht preftest <prefab>");
}


class WagonEditor : MonoBehaviour
{
    private BasePlayer m_Player;
	private bool m_IsLoading = false;
    private TrainLayout m_Layout;
    private TrainCar m_TrainCar;
    private Helltrain m_Plugin;
    private List<BaseEntity> m_Children = new List<BaseEntity>();
public string CurrentFaction => (m_Layout?.faction ?? "BANDIT").ToUpper();

    private BaseEntity m_CurrentEntity;
    private Construction m_Construction;
    private Vector3 m_RotationOffset = Vector3.zero;
    private int m_NextRotateFrame;
    private int m_NextClickFrame;
    private Vector3 m_StartPosition;
private Quaternion m_StartRotation;

// network throttle (уменьшаем лаги при перемещении)
private float m_NextNetUpdateTime;
private const float NET_UPDATE_INTERVAL = 0.10f; // 10 раз/сек. Больше = тяжелее, меньше = легче


    public TrainCar TrainCar => m_TrainCar;
    public List<BaseEntity> GetChildren() => m_Children;

    private static ProtectionProperties _fullProtection;
    private static readonly RaycastHit[] RaycastBuffer = new RaycastHit[32];

    private void Awake()
    {
        m_Player = GetComponent<BasePlayer>();

        if (!_fullProtection)
        {
            _fullProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _fullProtection.density = 100;
            _fullProtection.amounts = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
        }
    }

    private void OnDestroy()
    {
        foreach (BaseEntity baseEntity in m_Children)
        {
            if (!baseEntity || baseEntity.IsDestroyed)
                continue;

            baseEntity.Kill();

        }

        m_Children.Clear();

        if (m_TrainCar && !m_TrainCar.IsDestroyed)
    m_TrainCar.Kill();

    }

 public void Load(TrainLayout layout)
{
    if (layout == null)
    {
        m_Player.ChatMessage("❌ Пустой лэйаут — загружать нечего");
        return;
    }

    m_IsLoading = true;
    m_Layout = layout;

    // Сносим текущие редакторские объекты
    foreach (var child in m_Children)
    {
        if (child && !child.IsDestroyed)
    child.Kill();
    }
    m_Children.Clear();

    bool hasSlots =
        (layout.NpcSlots != null && layout.NpcSlots.Count > 0) ||
        (layout.CrateSlots != null && layout.CrateSlots.Count > 0) ||
        (layout.Shelves != null && layout.Shelves.Count > 0);

    // ✅ Backward compat: старые лэйауты без слотов открываем, но ничего не спавним (слоты пустые)
    if (!hasSlots)
    {
        m_IsLoading = false;
        m_Player.ChatMessage("⚠️ Это legacy-лэйаут без слотов (NpcSlots/CrateSlots/Shelves). Слоты пустые — добавляй через /htedit spawn npcslot/crateslot/shelf.");
        return;
    }

// ✅ Preview-ориентиры (похожи на финал, но используются только в редакторе)
const string NPC_SLOT_MARKER_PREFAB   = "assets/prefabs/deployable/signs/sign.post.single.prefab";
const string CRATE_SLOT_MARKER_PREFAB = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";


    // 1) NPC slots
    if (layout.NpcSlots != null)
    {
        foreach (var s in layout.NpcSlots)
        {
            var lp = new Vector3(
                (s.pos != null && s.pos.Length >= 3) ? s.pos[0] : 0f,
                (s.pos != null && s.pos.Length >= 3) ? s.pos[1] : 0f,
                (s.pos != null && s.pos.Length >= 3) ? s.pos[2] : 0f
            );
            var lr = Quaternion.Euler(
                (s.rot != null && s.rot.Length >= 3) ? s.rot[0] : 0f,
                (s.rot != null && s.rot.Length >= 3) ? s.rot[1] : 0f,
                (s.rot != null && s.rot.Length >= 3) ? s.rot[2] : 0f
            );

            var ent = CreateChildEntity(NPC_SLOT_MARKER_PREFAB, lp, lr);
            if (ent == null) continue;

            var mk = ent.gameObject.AddComponent<SlotMarker>();
            mk.kind = SlotMarker.Kind.Npc;

            m_Children.Add(ent);
        }
    }

    // 2) Crate slots
    if (layout.CrateSlots != null)
    {
        foreach (var s in layout.CrateSlots)
        {
            var lp = new Vector3(
                (s.pos != null && s.pos.Length >= 3) ? s.pos[0] : 0f,
                (s.pos != null && s.pos.Length >= 3) ? s.pos[1] : 0f,
                (s.pos != null && s.pos.Length >= 3) ? s.pos[2] : 0f
            );
            var lr = Quaternion.Euler(
                (s.rot != null && s.rot.Length >= 3) ? s.rot[0] : 0f,
                (s.rot != null && s.rot.Length >= 3) ? s.rot[1] : 0f,
                (s.rot != null && s.rot.Length >= 3) ? s.rot[2] : 0f
            );

            var ent = CreateChildEntity(CRATE_SLOT_MARKER_PREFAB, lp, lr);
            if (ent == null) continue;

            var mk = ent.gameObject.AddComponent<SlotMarker>();
            mk.kind = SlotMarker.Kind.Crate;

            m_Children.Add(ent);
        }
    }

    // 3) Shelves (реальные префабы полок)
    if (layout.Shelves != null)
    {
        foreach (var sh in layout.Shelves)
        {
            if (string.IsNullOrEmpty(sh.prefab)) continue;

            var lp = new Vector3(
                (sh.pos != null && sh.pos.Length >= 3) ? sh.pos[0] : 0f,
                (sh.pos != null && sh.pos.Length >= 3) ? sh.pos[1] : 0f,
                (sh.pos != null && sh.pos.Length >= 3) ? sh.pos[2] : 0f
            );
            var lr = Quaternion.Euler(
                (sh.rot != null && sh.rot.Length >= 3) ? sh.rot[0] : 0f,
                (sh.rot != null && sh.rot.Length >= 3) ? sh.rot[1] : 0f,
                (sh.rot != null && sh.rot.Length >= 3) ? sh.rot[2] : 0f
            );

            var ent = CreateChildEntity(sh.prefab, lp, lr);
            if (ent == null) continue;

            var mk = ent.gameObject.AddComponent<ShelfMarker>();
            mk.prefab = sh.prefab;

            m_Children.Add(ent);
        }
    }

    m_IsLoading = false;
    m_Player.ChatMessage($"✅ Загружено (слоты/полки): {m_Children.Count}");
}








// === Перегрузка для вызова /htedit load ===
public void Load(TrainCar trainCar, TrainLayout layout, Helltrain plugin)
{
    if (trainCar == null)
    {
        m_Player.ChatMessage("❌ Нет вагона для загрузки лэйаута");
        return;
    }
        if (layout == null)
    {
        m_Player.ChatMessage("❌ Пустой лэйаут — загружать нечего");
        return;
    }

    m_TrainCar = trainCar;
    m_Plugin = plugin;
    Load(layout);
}




   public void Save()
{
    if (m_TrainCar == null || m_Layout == null || string.IsNullOrEmpty(m_Layout.name))
    {
        m_Player.ChatMessage("❌ Нет вагона или имени лэйаута для сохранения");
        return;
    }

    string variant = "A";
    if (m_Layout.cars != null && m_Layout.cars.Count > 0 && !string.IsNullOrEmpty(m_Layout.cars[0].variant))
        variant = m_Layout.cars[0].variant.ToUpper();

    var npcSlots = new List<SlotSpec>();
    var crateSlots = new List<SlotSpec>();
    var shelves = new List<ShelfSpec>();

    foreach (var child in m_Children)
    {
        if (child == null || child.IsDestroyed) continue;

        Vector3 lp = m_TrainCar.transform.InverseTransformPoint(child.transform.position);
        Vector3 eul = child.transform.localRotation.eulerAngles;

        var sm = child.GetComponent<SlotMarker>();
        if (sm != null)
        {
            var s = new SlotSpec
            {
                pos = new float[] { lp.x, lp.y, lp.z },
                rot = new float[] { eul.x, eul.y, eul.z }
            };

            if (sm.kind == SlotMarker.Kind.Npc) npcSlots.Add(s);
            else crateSlots.Add(s);

            continue;
        }

        var sh = child.GetComponent<ShelfMarker>();
        if (sh != null)
        {
            shelves.Add(new ShelfSpec
            {
                prefab = sh.prefab,
                pos = new float[] { lp.x, lp.y, lp.z },
                rot = new float[] { eul.x, eul.y, eul.z }
            });
        }
    }

    // ✅ Правило полок (MAIN):
// A/B => обычно 2
// C   => не используются
// EDITOR2: жёстко НЕ валидируем на этом этапе — только предупреждаем, но сохраняем.
bool isC = !string.IsNullOrEmpty(variant) && variant.StartsWith("C", StringComparison.OrdinalIgnoreCase);

if (isC)
{
    if (shelves.Count != 0)
        m_Player.ChatMessage($"⚠️ Вагон {variant}: Shelves не используются. Сейчас: {shelves.Count} (сохраню как есть).");
}
else
{
    if (shelves.Count != 2)
        m_Player.ChatMessage($"⚠️ Вагон {variant}: обычно 2 Shelves. Сейчас: {shelves.Count} (сохраню как есть).");
}


    m_Layout.NpcSlots = npcSlots;
    m_Layout.CrateSlots = crateSlots;
    m_Layout.Shelves = shelves;

    // Legacy objects НЕ трогаем (совместимость; конвертации нет по ТЗ)
    // m_Layout.objects оставляем как было.

    string dataKey = $"Helltrain/Layouts/{m_Layout.name}";
    Interface.Oxide.DataFileSystem.WriteObject(dataKey, m_Layout, true);

    m_Player.ChatMessage($"💾 Сохранено слотов: NPC={npcSlots.Count}, Crate={crateSlots.Count}, Shelves={shelves.Count} → {m_Layout.name}.json");
}


private void WriteAutosave()
{
    var snapshot = new TrainLayout
    {
        NpcSlots = new List<SlotSpec>(),
        CrateSlots = new List<SlotSpec>(),
        Shelves = new List<ShelfSpec>()
    };

    foreach (var child in m_Children)
    {
        if (child == null || child.IsDestroyed) continue;

        Vector3 lp = m_TrainCar.transform.InverseTransformPoint(child.transform.position);
        Vector3 eul = child.transform.localRotation.eulerAngles;

        var sm = child.GetComponent<SlotMarker>();
        if (sm != null)
        {
            var s = new SlotSpec
            {
                pos = new float[] { lp.x, lp.y, lp.z },
                rot = new float[] { eul.x, eul.y, eul.z }
            };
            if (sm.kind == SlotMarker.Kind.Npc) snapshot.NpcSlots.Add(s);
            else snapshot.CrateSlots.Add(s);
            continue;
        }

        var sh = child.GetComponent<ShelfMarker>();
        if (sh != null)
        {
            snapshot.Shelves.Add(new ShelfSpec
            {
                prefab = sh.prefab,
                pos = new float[] { lp.x, lp.y, lp.z },
                rot = new float[] { eul.x, eul.y, eul.z }
            });
        }
    }

    Interface.Oxide.DataFileSystem.WriteObject("Helltrain/Layouts/_editor_autosave", snapshot, true);
    m_Player.ChatMessage("💾 Сохранено в _editor_autosave.json (слоты/полки)");
}







    public static BaseEntity FindEntityFromRay(BasePlayer player)
    {
        const int LAYERS = (1 << 0) | (1 << 8) | (1 << 10) | (1 << 17) | (1 << 26);

        int hits = Physics.RaycastNonAlloc(player.eyes.HeadRay(), RaycastBuffer, 10f, LAYERS, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hits; i++)
        {
            BaseEntity baseEntity = RaycastBuffer[i].collider.GetComponentInParent<BaseEntity>();
            if (!baseEntity || baseEntity.IsDestroyed)
                continue;

            if (baseEntity is TrainCar)
                continue;

            return baseEntity;
        }

        return null;
    }

    public bool IsTrainEntity(BaseEntity baseEntity) => m_Children.Contains(baseEntity);

    public void StartEditingEntity(BaseEntity baseEntity, bool justSpawned)
    {
		m_CurrentEntity = baseEntity;
        if (!justSpawned)
        {
            m_StartPosition = baseEntity.transform.localPosition;
            m_StartRotation = baseEntity.transform.localRotation;
        }

        m_NextNetUpdateTime = 0f;


        m_Construction = PrefabAttribute.server.Find<Construction>(m_CurrentEntity.prefabID);
        if (!m_Construction)
        {
            m_Construction = new Construction();
            m_Construction.rotationAmount = new Vector3(0, 90f, 0);
            m_Construction.fullName = m_CurrentEntity.PrefabName;
            m_Construction.maxplaceDistance = 4f;
            m_Construction.canRotateBeforePlacement = m_Construction.canRotateAfterPlacement = true;
        }

        m_Player.ChatMessage($"📦 Редактируем: <color=#ce422b>{m_CurrentEntity.ShortPrefabName}</color>");
        m_Player.ChatMessage("🖱️ ЛКМ - разместить | ПКМ - отмена | RELOAD - поворот");
    }

    public void DeleteWagonEntity(BaseEntity baseEntity)
    {
        if (baseEntity == m_CurrentEntity)
            m_CurrentEntity = null;

        m_Children.Remove(baseEntity);
baseEntity.Kill();

    }

    private void Update()
    {
        if (!m_CurrentEntity)
        {
            if (m_Player.serverInput.WasJustPressed(BUTTON.FIRE_PRIMARY) && Time.frameCount > m_NextClickFrame)
            {
                BaseEntity baseEntity = FindEntityFromRay(m_Player);
                if (baseEntity && IsTrainEntity(baseEntity))
                    StartEditingEntity(baseEntity, false);

                m_NextClickFrame = Time.frameCount + 20;
            }

            return;
        }

        Construction.Target target = new Construction.Target()
        {
            ray = m_Player.eyes.BodyRay(),
            player = m_Player,
            buildingBlocked = false,
        };

        UpdatePlacement(ref target);

        UpdateNetworkTransform();

        if (m_Player.serverInput.WasJustReleased(BUTTON.RELOAD) && Time.frameCount > m_NextRotateFrame)
        {
            if (m_Player.serverInput.IsDown(BUTTON.DUCK))
                m_RotationOffset.z = Mathf.Repeat(m_RotationOffset.z + 90f, 360);
            else if (m_Player.serverInput.IsDown(BUTTON.SPRINT))
                m_RotationOffset.x = Mathf.Repeat(m_RotationOffset.x + 90f, 360);
            else
                m_RotationOffset.y = Mathf.Repeat(m_RotationOffset.y + 90f, 360);

            m_NextRotateFrame = Time.frameCount + 20;
            m_Player.ChatMessage($"🔄 Поворот: X={m_RotationOffset.x:F0}° Y={m_RotationOffset.y:F0}° Z={m_RotationOffset.z:F0}°");
        }

        if (m_Player.serverInput.WasJustPressed(BUTTON.FIRE_PRIMARY) && Time.frameCount > m_NextClickFrame)
        {
            Vector3 finalLocalPos = m_TrainCar.transform.InverseTransformPoint(m_CurrentEntity.transform.position);
            m_Player.ChatMessage($"✅ Размещён: <color=#ce422b>{m_CurrentEntity.ShortPrefabName}</color>");
            m_Player.ChatMessage($"   Local: {finalLocalPos}");

            m_CurrentEntity = null;
            m_RotationOffset = Vector3.zero;
            m_NextClickFrame = Time.frameCount + 20;
        }
        else if (m_Player.serverInput.WasJustPressed(BUTTON.FIRE_SECONDARY))
        {
            m_Player.ChatMessage($"❌ Отменено: <color=#ce422b>{m_CurrentEntity.ShortPrefabName}</color>");

            if (m_StartPosition != Vector3.zero && m_StartRotation != Quaternion.identity)
            {
                m_CurrentEntity.transform.localPosition = m_StartPosition;
                m_CurrentEntity.transform.localRotation = m_StartRotation;

                UpdateNetworkTransform();
            }
            else
            {
                m_Children.Remove(m_CurrentEntity);
m_CurrentEntity.Kill();

            }

            m_CurrentEntity = null;
            m_RotationOffset = Vector3.zero;
        }
    }
	



    public BaseEntity CreateChildEntity(
    string prefab, 
    Vector3 position, 
    Quaternion rotation, 
    string npcType = null,
    string gun = null,
    string ammo = null,
    int ammoCount = 0,
    float hackTimer = 0
)
{
    if (m_TrainCar == null || string.IsNullOrEmpty(prefab))
        return null;

    // создаём энтити в мировых координатах, но с лок.привязкой к вагону
    BaseEntity baseEntity = GameManager.server.CreateEntity(prefab, m_TrainCar.transform.TransformPoint(position));
    if (baseEntity == null)
        return null;

    // NPC/Bradley живут отдельно; остальное — родим в вагон ДО Spawn()
    bool shouldParent = !(baseEntity is global::HumanNPC) && !(baseEntity is BradleyAPC);
    if (shouldParent)
    {
        baseEntity.SetParent(m_TrainCar, true, true);
        baseEntity.transform.localPosition = position;
        baseEntity.transform.localRotation = rotation;
    }

    // толстая защита в редакторе (чтобы ничто не ломалось)
    if (baseEntity is BaseCombatEntity be)
        be.baseProtection = _fullProtection;

    // опционально: таймер взлома для hack-крейта (если используешь)
    if (prefab == m_Plugin.HackableCratePrefab && hackTimer > 0)
    {
        var crate = baseEntity as HackableLockedCrate;
        if (crate != null)
        {
            // поставь свою логику таймера, если нужна
        }
    }

    baseEntity.Spawn();

    // маркер типа NPC (для сохранения в layout)
    if (!string.IsNullOrEmpty(npcType) && baseEntity is global::HumanNPC)
        baseEntity.gameObject.AddComponent<NPCTypeMarker>().npcType = npcType;

    // маркер параметров турели (оружие/патроны)
    if (!string.IsNullOrEmpty(gun) && baseEntity is AutoTurret)
        baseEntity.gameObject.AddComponent<TurretMarker>().Set(gun, ammo, ammoCount);

    // безопасная «заморозка» боевого поведения в редакторе
    if (baseEntity is AutoTurret at)
    {
        at.SetFlag(IOEntity.Flag_HasPower, false, false, true);
        at.SetFlag(BaseEntity.Flags.On, false, false, true);
        at.SetTarget(null);
        at.CancelInvoke();
        at.CancelInvoke(at.SendAimDir);
        at.CancelInvoke(at.ScheduleForTargetScan);
        at.SendNetworkUpdate();
    }
    else if (baseEntity is SamSite sam)
    {
        sam.SetFlag(IOEntity.Flag_HasPower, false, false, true);
        sam.SetFlag(BaseEntity.Flags.On, false, false, true);
        sam.CancelInvoke(sam.TargetScan);
        sam.SendNetworkUpdate();
    }
    else if (baseEntity is ScientistNPC npc)
    {
        // «заморозить» ИИ NPC в редакторе
        var brain = npc.GetComponent<BaseAIBrain>();
        if (brain != null) brain.enabled = false;

        var nav = npc.GetComponent<BaseNavigator>();
        if (nav != null)
        {
            nav.SetDestination(npc.transform.position, BaseNavigator.NavigationSpeed.Slow, 0f);
            nav.CanUseNavMesh = false;
            nav.ClearFacingDirectionOverride();
        }
        npc.SetPlayerFlag(BasePlayer.PlayerFlags.SafeZone, true);
        npc.SendNetworkUpdate();
    }

    return baseEntity;
}

public BaseEntity CreateChildEntity(string prefab, Vector3 localPos, Quaternion localRot)
{
    return CreateChildEntity(prefab, localPos, localRot, null, null, null, 0, 0f);
}





    private void UpdateNetworkTransform()
    {
        if (m_CurrentEntity == null || m_CurrentEntity.IsDestroyed)
            return;
        
        var rb = m_CurrentEntity.GetComponent<Rigidbody>();
        if (rb != null && rb.isKinematic)
        {
            rb.position = m_CurrentEntity.transform.position;
            rb.rotation = m_CurrentEntity.transform.rotation;
            rb.MovePosition(m_CurrentEntity.transform.position);
        }
        
        m_CurrentEntity.transform.hasChanged = true;

if (Time.realtimeSinceStartup >= m_NextNetUpdateTime)
{
    m_NextNetUpdateTime = Time.realtimeSinceStartup + NET_UPDATE_INTERVAL;
    m_CurrentEntity.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
}

    }

    private void UpdatePlacement(ref Construction.Target constructionTarget)
    {
        Vector3 position = m_CurrentEntity.transform.position;
        Quaternion rotation = m_CurrentEntity.transform.rotation;

        Vector3 direction = constructionTarget.ray.direction;
        direction.y = 0f;
        direction.Normalize();

        m_CurrentEntity.transform.position = constructionTarget.ray.origin + (constructionTarget.ray.direction * m_Construction.maxplaceDistance);

        Vector3 eulerRotation = constructionTarget.rotation + m_RotationOffset;
        m_CurrentEntity.transform.rotation = Quaternion.Euler(eulerRotation) * Quaternion.LookRotation(direction);

        m_CurrentEntity.transform.position = Vector3.Lerp(position, m_CurrentEntity.transform.position, Time.deltaTime * 6f);
        m_CurrentEntity.transform.rotation = Quaternion.Lerp(rotation, m_CurrentEntity.transform.rotation, Time.deltaTime * 10f);
    }



}


#endregion

#region HT.HELPERS

private static BaseNetworkable FindBN(ulong id)
{
    return BaseNetworkable.serverEntities.Find(new NetworkableId((uint)id));
}



private void RestoreProtectionForAll()
{
    try
    {
        foreach (var kv in _savedProtection)
        {
            var bn = BaseNetworkable.serverEntities.Find(new NetworkableId(kv.Key));
            var car = bn as TrainCar;
            if (car != null && !car.IsDestroyed)
            {
                car.baseProtection = kv.Value;
                car.SendNetworkUpdate();
            }
        }
    }
    finally
    {
        _savedProtection.Clear();
    }
}

private float ResolveCrateTimerSeconds(string faction, float overrideSeconds)
{
    if (overrideSeconds > 0f) return overrideSeconds;
    var key = (faction ?? "BANDIT").ToUpper();
    if (!config.LootTimerRanges.TryGetValue(key, out var r)) r = new ConfigData.LootTimerRange { Min = 250, Max = 500 };
    return UnityEngine.Random.Range(r.Min, r.Max + 1);
}



public string GetObjectType(BaseEntity entity)
{
    // NPC
    if (entity is ScientistNPC) return "npc";
    if (entity is global::HumanNPC) return "npc";
    if (entity.ShortPrefabName?.Contains("scientist", StringComparison.OrdinalIgnoreCase) == true)
        return "npc";

    // Турели
    if (entity is AutoTurret) return "turret";
    if (entity is SamSite)   return "samsite";

    // Лут (обычные ящики + hackable)
    var prefab = entity?.PrefabName ?? string.Empty;
    if (entity is StorageContainer && (
        prefab.Equals("assets/bundled/prefabs/radtown/crate_elite.prefab", StringComparison.OrdinalIgnoreCase) ||
        prefab.Equals("assets/bundled/prefabs/radtown/crate_normal.prefab", StringComparison.OrdinalIgnoreCase) ||
        prefab.Equals("assets/bundled/prefabs/radtown/crate_normal_2.prefab", StringComparison.OrdinalIgnoreCase)
    )) return "loot";
    if (entity is HackableLockedCrate) return "loot";

    return "unknown";
}

public string GetPrefabByType(string type)
{
    switch (type?.ToLower())
    {
        case "npc":     return SCIENTIST_PREFAB;
        case "turret":  return TURRET_PREFAB;
        case "samsite": return SAMSITE_PREFAB;
        case "loot":    return PREFAB_CRATE_BANDIT; // обычный ящик под фракцию через GetCratePrefabForFaction
        default:        return null;
    }
}


private string GetMinutesWord(int minutes)
{
    if (minutes == 1) return "минуту";
    if (minutes >= 2 && minutes <= 4) return "минуты";
    return "минут";
}

#endregion

[ConsoleCommand("helltrainclean")]
private void CmdClean(ConsoleSystem.Arg arg)
{
    var who = arg?.Player() != null ? arg.Player().displayName : "CONSOLE";
    Puts($"[Helltrain] 🔧 Форс-очистка поезда запрошена: {who}");

    ForceDestroyHellTrainHard();        // 1-й проход
    timer.Once(0.5f, ForceDestroyHellTrainHard); // повтор через полсек
    timer.Once(2.0f, ForceDestroyHellTrainHard); // и контроль через 2с
    arg?.ReplyWith("[Helltrain] Форс-очистка запущена (0.0s/0.5s/2.0s)");
}


[ConsoleCommand("helltrain.fixlayouts")]
private void CmdFixLayouts(ConsoleSystem.Arg arg)
{
    BasePlayer player = arg.Player();
    if (player != null && !player.IsAdmin)
    {
        SendReply(arg, "⛔ Только для админов!");
        return;
    }
    
    var dir = Path.Combine(Interface.Oxide.DataDirectory, LayoutDir);
    if (!Directory.Exists(dir))
    {
        SendReply(arg, "⛔ Папка лэйаутов не найдена!");
        return;
    }
    
    int fixedCount = 0;
    
    foreach (var file in Directory.GetFiles(dir, "*.json"))
    {
        try
        {
            string json = File.ReadAllText(file, System.Text.Encoding.UTF8);
            
            json = json.Replace("\"Name\":", "\"name\":");
            json = json.Replace("\"Faction\":", "\"faction\":");
            json = json.Replace("\"Wagons\":", "\"cars\":");
            json = json.Replace("\"Type\":", "\"type\":");
            json = json.Replace("\"Prefab\":", "\"variant\":");
            
            File.WriteAllText(file, json, System.Text.Encoding.UTF8);
            fixedCount++;
        }
        catch (System.Exception e)
        {
            PrintError($"Ошибка фикса {Path.GetFileName(file)}: {e.Message}");
        }
    }
    
    SendReply(arg, $"✅ Исправлено файлов: {fixedCount}");
    
    _layouts.Clear();
    LoadLayouts();
    
    SendReply(arg, $"✅ Лэйауты перезагружены! Найдено: {_layouts.Count}");
}
} // ← Закрывает класс Helltrain 
}