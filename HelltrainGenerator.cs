using System;
using System.Collections.Generic;
using Oxide.Core;
using System.IO;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("HelltrainGenerator", "BLOODHELL", "0.1.0")]
    [Description("HELLTRAIN planner-only generator (Resolve/Validate/BuildPlan). No spawn, no timers, no lifecycle.")]
    public class HelltrainGenerator : RustPlugin
    {
        // Контракт: (ok, compositionKey, reason)
        // overrideKey имеет приоритет, если не пустой
        // никаких fallback
		
	   // -----------------------------
// GENERATOR v1 config (planner-only)
// -----------------------------
private class PluginConfig
{
    public bool Debug = false;
    public Dictionary<string, FactionCfg> Factions = new Dictionary<string, FactionCfg>(StringComparer.OrdinalIgnoreCase);
}


private class FactionCfg
{
    public int MinWagons = 1;
    public int MaxWagons = 1;

    // compositions as SoT inside generator (override must exist here)
    public Dictionary<string, float> Compositions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    // wagon pools with weights
    public Dictionary<string, float> WagonPools = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    // per-wagon limits (0 = запрещено)
    public Dictionary<string, int> Limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}

private PluginConfig _cfg;
private readonly System.Random _rng = new System.Random();
// ---- Helltrain.json (main plugin) config snapshot ----
private class HelltrainRootCfg
{
    public Dictionary<string, HelltrainCompositionCfg> Compositions = new Dictionary<string, HelltrainCompositionCfg>(StringComparer.OrdinalIgnoreCase);

    public HelltrainGeneratorCfg Generator = new HelltrainGeneratorCfg();

    // HEAVY (optional): отдельная система поверх Limits.C
    public HelltrainHeavyCfg Heavy = new HelltrainHeavyCfg();
}



private class HelltrainGeneratorCfg
{
    public Dictionary<string, HelltrainFactionGenCfg> Factions = new Dictionary<string, HelltrainFactionGenCfg>(StringComparer.OrdinalIgnoreCase);
}

private class HelltrainFactionGenCfg
{
    public Dictionary<string, float> CrateSlotWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
}


// -----------------------------
// HEAVY config (optional)
// -----------------------------
private class HelltrainHeavyCfg
{
    public Dictionary<string, HelltrainHeavyFactionCfg> Factions = new Dictionary<string, HelltrainHeavyFactionCfg>(StringComparer.OrdinalIgnoreCase);
}

private class HelltrainHeavyFactionCfg
{
    // keys: "0","1","2"... values: weights
    public Dictionary<string, float> CountWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    // keys: "bradley","samsite" values: weights (PMC only)
    public Dictionary<string, float> KindWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
}

private class HelltrainCompositionCfg
{
    public int MinWagons = 1;
    public int MaxWagons = 1;

    // e.g. { "A": { "wagonA_pmc": 38.0 }, "B": {...}, "C": {...} }
    public Dictionary<string, Dictionary<string, float>> WagonPools = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);

    // e.g. { "C": 3 } or per-wagon name (optional)
    public Dictionary<string, int> Limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}

private HelltrainRootCfg _helltrainCfg;

// ---- HEAVY runtime (last resolve snapshot) ----
private string _lastHeavyFactionKey = null;          // upper
private string _lastHeavyCompositionKey = null;      // as returned
private List<string> _lastHeavyAssignmentsWagons = null; // aligned to wagons list (NO ENGINE)

// ---- Layout snapshot (planner-only) ----
private const string LayoutDir = "Helltrain/Layouts";

private class TrainLayoutLegacy
{
    public string name;
    public string faction;

    public List<CrateSlotSpec> CrateSlots;

    // NPC slots: generator reads only COUNT, not coordinates
    public List<NpcSlotSpec> NpcSlots;
}



private class CrateSlotSpec
{
    public float[] pos;
    public float[] rot;
    public string kitPool;
    public string lootKey;
}

private class NpcSlotSpec
{
    public float[] pos;
    public float[] rot;

    // IMPORTANT:
    // In v1, generator will treat kitPool as a concrete kitKey (poolKey -> kitKey identity),
    // to avoid guessing missing Tier pools. No fallback.
    public string kitPool;
}


private bool TryLoadLayoutLegacy(string layoutName, out TrainLayoutLegacy layout, out string reason)
{
    layout = null;
    reason = null;

    try
    {
        if (string.IsNullOrWhiteSpace(layoutName))
        {
            reason = "LAYOUT_EMPTY";
            return false;
        }

        var fileName = layoutName.Trim();
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            fileName += ".json";

        var path = Path.Combine(Oxide.Core.Interface.Oxide.DataDirectory, LayoutDir, fileName);
        if (!File.Exists(path))
        {
            reason = $"LAYOUT_NOT_FOUND:{path}";
            return false;
        }

        var file = new DynamicConfigFile(path);
        file.Load();
        layout = file.ReadObject<TrainLayoutLegacy>();

        if (layout == null)
        {
            reason = "LAYOUT_DESERIALIZE_NULL";
            return false;
        }

        return true;
    }
    catch (Exception ex)
    {
        reason = $"LAYOUT_LOAD_FAIL:{ex.Message}";
        return false;
    }
}

private List<string> BuildCrateTypePool(string factionKey)
{
    if (string.IsNullOrWhiteSpace(factionKey)) return null;

    var fk = factionKey.Trim().ToUpperInvariant();
    switch (fk)
    {
        case "BANDIT":
            return new List<string> { "CrateBanditWood_A", "CrateBanditWood_B" };
        case "COBLAB":
            return new List<string> { "CrateCobLabMil_A", "CrateCobLabElite_B", "CrateCobLabMed_C" };
        case "PMC":
            return new List<string> { "CratePMCMil_A", "CratePMCElite_B", "CratePMCHACKS_C" };
        default:
            return null;
    }
}

// Tier SoT: NPC kit pools (no CORE decisions, equal weights)
private List<string> BuildNpcKitPool(string factionKey)
{
    var key = (factionKey ?? string.Empty).Trim().ToUpperInvariant();

    // IMPORTANT: Tier is the only source of npc kit pool
    switch (key)
    {
        case "BANDIT":
            return new List<string> { "kitbandit1", "kitbandit2", "kitbandit3" };

        case "COBLAB":
        case "COBALT":
            return new List<string> { "kitcobalt1", "kitcobalt2", "kitcobalt3" };

        case "PMC":
            return new List<string> { "kitpmc1", "kitpmc2", "kitpmc3" };
    }

    return new List<string>();
}

// Map spawned train car entity -> wagonKey used by layouts (no CORE, no geometry)
// Returns null if cannot classify (then we degrade slots for that car).
private string GetWagonKeyFromCar(BaseEntity car, string factionKey)
{
    if (car == null) return null;
    if (car is TrainEngine) return null;

    var fk = (factionKey ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(fk)) return null;

    // Use prefab name/path only to classify A/B/C wagon type.
    // This is stable and does not rely on layoutName.
    var prefabName = car.ShortPrefabName ?? string.Empty; // e.g. "trainwagona.entity"
    if (prefabName.Contains("trainwagona", StringComparison.OrdinalIgnoreCase))
        return $"wagonA_{fk}";
    if (prefabName.Contains("trainwagonb", StringComparison.OrdinalIgnoreCase))
        return $"wagonB_{fk}";
    if (prefabName.Contains("trainwagonc", StringComparison.OrdinalIgnoreCase))
        return $"wagonC_{fk}";

    return null;
}


// Tier SoT: lootKey -> prefabPath (no CORE decisions)
private bool TryResolveCratePrefabPath(string lootKey, out string prefabPath)
{
    prefabPath = null;
    if (string.IsNullOrWhiteSpace(lootKey))
        return false;

    switch (lootKey.Trim())
    {
        // BANDIT
        case "CrateBanditWood_A":
            prefabPath = "assets/bundled/prefabs/radtown/crate_normal_2.prefab";
            return true;
        case "CrateBanditWood_B":
            prefabPath = "assets/bundled/prefabs/radtown/crate_tools.prefab";
            return true;

        // COBLAB
        case "CrateCobLabMil_A":
            prefabPath = "assets/bundled/prefabs/radtown/crate_normal.prefab";
            return true;
        case "CrateCobLabElite_B":
            prefabPath = "assets/bundled/prefabs/radtown/crate_elite.prefab";
            return true;
        case "CrateCobLabMed_C":
            prefabPath = "assets/bundled/prefabs/radtown/crate_normal_2_medical.prefab";
            return true;

        // PMC
        case "CratePMCMil_A":
            prefabPath = "assets/bundled/prefabs/radtown/crate_normal.prefab";
            return true;
        case "CratePMCElite_B":
            prefabPath = "assets/bundled/prefabs/radtown/crate_elite.prefab";
            return true;
        case "CratePMCHACKS_C":
            prefabPath = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate_oilrig.prefab";
            return true;
    }

    return false;
}


private Dictionary<string, float> GetCrateSlotWeightsFromHelltrain(string factionKey)
{
    if (_helltrainCfg?.Generator?.Factions == null || string.IsNullOrWhiteSpace(factionKey))
        return null;

    var key = factionKey.Trim();
    if (_helltrainCfg.Generator.Factions.TryGetValue(key, out var fc) && fc?.CrateSlotWeights != null)
        return fc.CrateSlotWeights;

    var up = key.ToUpperInvariant();
    if (_helltrainCfg.Generator.Factions.TryGetValue(up, out fc) && fc?.CrateSlotWeights != null)
        return fc.CrateSlotWeights;

    return null;
}

// true => DefaultCrate, false => None
private bool RollCrateSlot(Dictionary<string, float> weights)
{
    float noneW = 1f;
    float defW  = 1f;

    if (weights != null)
    {
        if (weights.TryGetValue("None", out var n)) noneW = n;
        if (weights.TryGetValue("DefaultCrate", out var d)) defW = d;
    }

    noneW = Math.Max(0f, noneW);
    defW  = Math.Max(0f, defW);

    var sum = noneW + defW;
    // No fallback content: if weights are invalid/zero -> leave slot empty.
if (sum <= 0.0001f) return false;


    var r = (float)(_rng.NextDouble() * sum);
    return r >= noneW;
}



private class TrainLayout
{
    [JsonProperty("name")] public string name { get; set; }
    [JsonProperty("faction")] public string faction { get; set; }
    [JsonProperty("CrateSlots")] public List<SlotSpec> CrateSlots { get; set; }
}

private class SlotSpec
{
    [JsonProperty("pos")] public float[] pos;
    [JsonProperty("rot")] public float[] rot;
    [JsonProperty("kitPool")] public string kitPool;
    [JsonProperty("lootKey")] public string lootKey;
}

private bool TryLoadLayout(string layoutName, out TrainLayout layout, out string reason)
{
    layout = null;
    reason = null;

    try
    {
        if (string.IsNullOrWhiteSpace(layoutName))
        {
            reason = "LAYOUT_EMPTY";
            return false;
        }

        var fileName = layoutName.Trim();
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            fileName += ".json";

        var path = Path.Combine(Oxide.Core.Interface.Oxide.DataDirectory, LayoutDir, fileName);
        if (!File.Exists(path))
        {
            reason = $"LAYOUT_NOT_FOUND:{path}";
            return false;
        }

        var json = File.ReadAllText(path);
        layout = JsonConvert.DeserializeObject<TrainLayout>(json);
        if (layout == null)
        {
            reason = "LAYOUT_DESERIALIZE_NULL";
            return false;
        }

        return true;
    }
    catch (Exception ex)
    {
        reason = $"LAYOUT_LOAD_FAIL:{ex.Message}";
        return false;
    }
}

private bool TryLoadHelltrainConfig(out string reason)
{
    reason = null;

    try
    {
        var path = Path.Combine(Oxide.Core.Interface.Oxide.ConfigDirectory, "Helltrain.json");
var file = new DynamicConfigFile(path);
file.Load();
_helltrainCfg = file.ReadObject<HelltrainRootCfg>();

        if (_helltrainCfg == null || _helltrainCfg.Compositions == null || _helltrainCfg.Compositions.Count == 0)
        {
            reason = "Helltrain.json has no Compositions";
            return false;
        }

        return true;
    }
    catch (Exception ex)
    {
        reason = $"Failed to load Helltrain.json: {ex.Message}";
        return false;
    }
}

private FactionCfg BuildFactionCfgFromHelltrain(string compositionKey, HelltrainCompositionCfg comp, out string reason)
{
    reason = null;
    if (comp == null)
    {
        reason = "Composition is null";
        return null;
    }

    var fc = new FactionCfg
    {
        MinWagons = Math.Max(1, comp.MinWagons),
        MaxWagons = Math.Max(Math.Max(1, comp.MinWagons), comp.MaxWagons),
        Limits = comp.Limits ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    };

    // Flatten wagon pools (A/B/C groups -> single pool of wagonName->weight)
    fc.WagonPools = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    if (comp.WagonPools != null)
    {
        foreach (var group in comp.WagonPools)
        {
            if (group.Value == null) continue;
            foreach (var kv in group.Value)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                if (kv.Value <= 0f) continue;
                fc.WagonPools[kv.Key.Trim()] = kv.Value;
            }
        }
    }

    if (fc.WagonPools.Count == 0)
    {
        reason = "WagonPools empty in Helltrain composition";
        return null;
    }

    // Compositions внутри генератора тут не нужны — compositionKey выбираем снаружи (по override или по faction)
    fc.Compositions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
    {
        [compositionKey] = 1f
    };

    return fc;
}


protected override void LoadDefaultConfig()
{
    // NOTE: это дефолты для генератор-плагина, но fallback на другие фракции отсутствует.
    _cfg = new PluginConfig();

    // Примерные пресеты (пользователь переопределит в config HelltrainGenerator.json при необходимости)
    _cfg.Factions["PMC"] = new FactionCfg
    {
        MinWagons = 2,
        MaxWagons = 4,
        Compositions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["pmc"] = 1f },
        WagonPools = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["wagon_pmc_a"] = 1f,
            ["wagon_pmc_b"] = 1f,
            ["wagon_pmc_c"] = 1f
        },
        Limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["wagon_pmc_a"] = 2,
            ["wagon_pmc_b"] = 2,
            ["wagon_pmc_c"] = 1
        }
    };

    _cfg.Factions["BANDIT"] = new FactionCfg
    {
        MinWagons = 2,
        MaxWagons = 4,
        Compositions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["bandit"] = 1f },
        WagonPools = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["wagon_bandit_a"] = 1f,
            ["wagon_bandit_b"] = 1f,
            ["wagon_bandit_c"] = 1f
        },
        Limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["wagon_bandit_a"] = 2,
            ["wagon_bandit_b"] = 2,
            ["wagon_bandit_c"] = 1
        }
    };

    _cfg.Factions["COBLAB"] = new FactionCfg
    {
        MinWagons = 2,
        MaxWagons = 4,
        Compositions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["coblab"] = 1f },
        WagonPools = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["wagon_coblab_a"] = 1f,
            ["wagon_coblab_b"] = 1f,
            ["wagon_coblab_c"] = 1f
        },
        Limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["wagon_coblab_a"] = 2,
            ["wagon_coblab_b"] = 2,
            ["wagon_coblab_c"] = 1
        }
    };

    SaveConfig();
}

protected override void LoadConfig()
{
    base.LoadConfig();
    try
    {
        _cfg = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
    }
    catch
    {
        PrintWarning("[HelltrainGenerator] Config load failed, regenerating defaults.");
        LoadDefaultConfig();
    }
}

protected override void SaveConfig()
{
    Config.WriteObject(_cfg, true);
}

private string PickWeightedKey(Dictionary<string, float> weights)
{
    if (weights == null || weights.Count == 0) return null;

    double total = 0;
    foreach (var kv in weights)
    {
        var w = kv.Value;
        if (w <= 0f) continue;
        total += w;
    }
    if (total <= 0.0001) return null;

    var roll = _rng.NextDouble() * total;
    foreach (var kv in weights)
    {
        var w = kv.Value;
        if (w <= 0f) continue;
        roll -= w;
        if (roll <= 0) return kv.Key;
    }

    // fallback: first positive
    foreach (var kv in weights)
        if (kv.Value > 0f) return kv.Key;

    return null;
}

private void Shuffle<T>(List<T> list)
{
    for (int i = list.Count - 1; i > 0; i--)
    {
        int j = _rng.Next(i + 1);
        var tmp = list[i];
        list[i] = list[j];
        list[j] = tmp;
    }
}

private int NextIntInclusive(int min, int max)
{
    if (min > max) { var t = min; min = max; max = t; }
    return _rng.Next(min, max + 1);
}

private List<string> BuildWagonList(FactionCfg fc, int target, out bool degraded, out string failReason)
{
    degraded = false;
    failReason = null;

    if (fc == null)
    {
        failReason = "FACTION_CFG_NULL";
        return null;
    }

    if (fc.WagonPools == null || fc.WagonPools.Count == 0)
    {
        failReason = "WAGONPOOLS_EMPTY";
        return null;
    }

    // remaining limits
    var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in fc.WagonPools)
    {
        var name = kv.Key;
        if (string.IsNullOrWhiteSpace(name)) continue;

        int lim = 0;

// 1) exact per-wagon limit
if (fc.Limits != null && fc.Limits.TryGetValue(name, out var v))
{
    lim = v;
}
else
{
    // 2) group limit by variant letter (A/B/C) e.g. Limits["C"]=3
    string groupKey = null;
    var m = System.Text.RegularExpressions.Regex.Match(name, @"^wagon([ABC])_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (m.Success) groupKey = m.Groups[1].Value.ToUpperInvariant();

    if (groupKey != null && fc.Limits != null && fc.Limits.TryGetValue(groupKey, out var gv))
        lim = gv;
    else
        lim = target; // 3) if not limited, allow repeats up to target (otherwise Min/Max > pool size is impossible)

}


        if (lim > 0)
            remaining[name] = lim;
    }

    if (remaining.Count == 0)
    {
        failReason = "NO_WAGONS_AVAILABLE_BY_LIMITS";
        return null;
    }

    var wagons = new List<string>();
    int desired = Math.Max(1, target);

    // основной набор
    for (int i = 0; i < desired; i++)
    {
        // weights only among remaining>0
        var pickWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in fc.WagonPools)
        {
            if (!remaining.TryGetValue(kv.Key, out var r) || r <= 0) continue;
            if (kv.Value <= 0f) continue;
            pickWeights[kv.Key] = kv.Value;
        }

        if (pickWeights.Count == 0)
            break;

        var pick = PickWeightedKey(pickWeights);
        if (string.IsNullOrWhiteSpace(pick))
            break;

        wagons.Add(pick);
        remaining[pick] = remaining[pick] - 1;
    }

    if (wagons.Count == 0)
    {
        failReason = "CANNOT_BUILD_ANY_WAGON";
        return null;
    }

    if (wagons.Count < desired)
        degraded = true;

    Shuffle(wagons);
    return wagons;
}

        // CONTRACT v1: (ok, compositionKey, wagons, reason)
// wagons = SoT внутри GENERATOR (Min/Max, Pools, Limits, random order)
// никаких cross-faction fallback, никаких silent-отклонений
private static string CanonicalizeWagonKey(string key)
{
    if (string.IsNullOrWhiteSpace(key)) return key;
    key = key.Trim();

    // wagon_pmc_a -> wagonA_pmc
    // wagon_bandit_c -> wagonC_bandit
    var m = System.Text.RegularExpressions.Regex.Match(
        key,
        @"^wagon_(pmc|bandit|coblab)_([abc])$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    );

    if (m.Success)
    {
        var faction = m.Groups[1].Value.ToLowerInvariant();
        var letter = m.Groups[2].Value.ToUpperInvariant(); // A/B/C
        return $"wagon{letter}_{faction}";
    }

    // Если уже в формате wagonA_pmc — оставляем как есть
    return key;
}

private string GetWagonTypeABC(string wagonKey)
{
    if (string.IsNullOrEmpty(wagonKey)) return "?";
    var k = wagonKey.ToLowerInvariant();

    // SoT: wagonA_ / wagonB_ / wagonC_
    if (k.StartsWith("wagonA_".ToLowerInvariant())) return "A";
    if (k.StartsWith("wagonB_".ToLowerInvariant())) return "B";
    if (k.StartsWith("wagonC_".ToLowerInvariant())) return "C";

    return "?";
}

private void CountABC(List<string> wagons, out int a, out int b, out int c, out int unk)
{
    a = b = c = unk = 0;
    if (wagons == null) return;

    foreach (var w in wagons)
    {
        switch (GetWagonTypeABC(w))
        {
            case "A": a++; break;
            case "B": b++; break;
            case "C": c++; break;
            default: unk++; break;
        }
    }
}

private string FormatTypeLimits(Dictionary<string, int> limits)
{
    if (limits == null) return "<null>";
    limits.TryGetValue("A", out var la);
    limits.TryGetValue("B", out var lb);
    limits.TryGetValue("C", out var lc);
    return $"A={la} B={lb} C={lc}";
}


// -----------------------------
// HEAVY planner helpers
// -----------------------------
private int PickHeavyCount(string factionKeyUpper)
{
    // Defaults (contract):
    // BANDIT: 0
    // COBLAB: 0..1 (if configured)
    // PMC: 1..2 (if configured)
    if (_helltrainCfg == null || _helltrainCfg.Heavy == null || _helltrainCfg.Heavy.Factions == null)
        return 0;

    if (!_helltrainCfg.Heavy.Factions.TryGetValue(factionKeyUpper, out var hf) || hf == null || hf.CountWeights == null)
        return 0;

    // sanitize weights: keep only int keys >=0 with weight >0
    var tmp = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in hf.CountWeights)
    {
        if (kv.Value <= 0f) continue;
        if (!int.TryParse(kv.Key, out var n)) continue;
        if (n < 0) continue;
        tmp[kv.Key] = kv.Value;
    }

    if (tmp.Count == 0) return 0;

    var picked = PickWeightedKey(tmp);
    if (!int.TryParse(picked, out var count)) return 0;

    // clamp by faction rules
    if (string.Equals(factionKeyUpper, "BANDIT", StringComparison.OrdinalIgnoreCase))
        return 0;

    if (string.Equals(factionKeyUpper, "COBLAB", StringComparison.OrdinalIgnoreCase))
        return Mathf.Clamp(count, 0, 1);

    if (string.Equals(factionKeyUpper, "PMC", StringComparison.OrdinalIgnoreCase))
        return Mathf.Clamp(count, 0, 2);

    return 0;
}

private string PickHeavyKind(string factionKeyUpper)
{
    // BANDIT запрещён
    if (string.Equals(factionKeyUpper, "BANDIT", StringComparison.OrdinalIgnoreCase))
        return "None";

    // COBLAB всегда turret
    if (string.Equals(factionKeyUpper, "COBLAB", StringComparison.OrdinalIgnoreCase))
        return "turret";

    // PMC: bradley XOR samsite (весами)
    if (!string.Equals(factionKeyUpper, "PMC", StringComparison.OrdinalIgnoreCase))
        return "None";

    if (_helltrainCfg == null || _helltrainCfg.Heavy == null || _helltrainCfg.Heavy.Factions == null)
        return "None";

    if (!_helltrainCfg.Heavy.Factions.TryGetValue(factionKeyUpper, out var hf) || hf == null || hf.KindWeights == null)
        return "None";

    var tmp = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in hf.KindWeights)
    {
        if (kv.Value <= 0f) continue;
        var k = (kv.Key ?? "").Trim().ToLowerInvariant();
        if (k != "bradley" && k != "samsite") continue;
        tmp[k] = kv.Value;
    }

    if (tmp.Count == 0) return "None";

    var picked = (PickWeightedKey(tmp) ?? "").Trim().ToLowerInvariant();
    return (picked == "bradley" || picked == "samsite") ? picked : "None";
}

private void ApplyHeavyToWagons(string factionKeyUpper, string compositionKey, ref List<string> wagons, out List<string> heavyAssignmentsWagons)
{
    heavyAssignmentsWagons = new List<string>(wagons?.Count ?? 0);
    if (wagons == null) return;

    // start with "None" for all existing wagons
    for (int i = 0; i < wagons.Count; i++)
        heavyAssignmentsWagons.Add("None");

    int heavyCount = PickHeavyCount(factionKeyUpper);
    if (heavyCount <= 0) return;

    // Heavy uses the same prefab/layout as wagonC_<composition>
    var heavyWagonKey = CanonicalizeWagonKey($"wagonC_{(compositionKey ?? "").Trim().ToLowerInvariant()}");

    for (int n = 0; n < heavyCount; n++)
    {
        var kind = PickHeavyKind(factionKeyUpper);
        if (kind == "None")
            break;

        wagons.Add(heavyWagonKey);
        heavyAssignmentsWagons.Add(kind);
    }
}

private object ResolveCompositionKey(string activeFactionKey, string overrideKey)
{
    if (string.IsNullOrWhiteSpace(activeFactionKey))
    {
        Puts("[RESOLVE FAIL] faction=<empty> reason=FACTION_EMPTY");
        return new object[] { false, null, null, "FACTION_EMPTY" };
    }

    var fk = activeFactionKey.Trim().ToUpperInvariant();

    // Prefer Helltrain.json Compositions as SoT (fail-fast, no silent fallback)
if (_helltrainCfg == null)
{
    if (!TryLoadHelltrainConfig(out var loadReason))
    {
        Puts($"[RESOLVE FAIL] faction={fk} reason={loadReason}");
        return new object[] { false, null, null, loadReason };
    }
}

string compositionKey = null;

if (!string.IsNullOrWhiteSpace(overrideKey))
{
    compositionKey = overrideKey.Trim();
}
else
{
    // default mapping: faction -> composition key (pmc/coblab/bandit)
    compositionKey = fk.ToLowerInvariant();
}

if (_helltrainCfg == null || _helltrainCfg.Compositions == null || !_helltrainCfg.Compositions.TryGetValue(compositionKey, out var comp) || comp == null)
{
    var r = $"Composition '{compositionKey}' not found in Helltrain.json";
    Puts($"[RESOLVE FAIL] faction={fk} compositionKey={compositionKey} reason={r}");
    return new object[] { false, null, null, r };
}

var fc = BuildFactionCfgFromHelltrain(compositionKey, comp, out var fcReason);
if (fc == null)
{
    var r = string.IsNullOrWhiteSpace(fcReason) ? "Failed to build faction cfg from Helltrain.json" : fcReason;
    Puts($"[RESOLVE FAIL] faction={fk} compositionKey={compositionKey} reason={r}");
    return new object[] { false, null, null, r };
}



    if (!string.IsNullOrWhiteSpace(overrideKey))
    {
        var ov = overrideKey.Trim();
        if (fc.Compositions == null || !fc.Compositions.ContainsKey(ov))
        {
            var r = "Override composition not found in faction";
            Puts($"[RESOLVE FAIL] faction={fk} override={ov} reason={r}");
            return new object[] { false, null, null, r };
        }
        compositionKey = ov;
    }
    else
    {
        compositionKey = PickWeightedKey(fc.Compositions);
        if (string.IsNullOrWhiteSpace(compositionKey))
        {
            var r = "No compositions configured for faction";
            Puts($"[RESOLVE FAIL] faction={fk} reason={r}");
            return new object[] { false, null, null, r };
        }
    }

    // target wagons
    int min = Math.Max(1, fc.MinWagons);
    int max = Math.Max(min, fc.MaxWagons);
    int target = NextIntInclusive(min, max);

    bool degraded;
    string failReason;
    var wagons = BuildWagonList(fc, target, out degraded, out failReason);

    if (wagons == null || wagons.Count == 0)
    {
        var r = string.IsNullOrWhiteSpace(failReason) ? "CANNOT_BUILD_ANY_WAGON" : failReason;
        Puts($"[RESOLVE FAIL] faction={fk} compositionKey={compositionKey} reason={r}");
        return new object[] { false, null, null, r };
    }

    for (int i = 0; i < wagons.Count; i++)
    wagons[i] = CanonicalizeWagonKey(wagons[i]);

int achieved = wagons.Count;
var degradedStr = degraded ? "yes" : "no";
CountABC(wagons, out var ca, out var cb, out var cc, out var cu);

Puts($"[RESOLVE DBG] faction={fk} compositionKey={compositionKey} " +
     $"limits={FormatTypeLimits(fc.Limits)} " +
     $"counts(A/B/C/?)={ca}/{cb}/{cc}/{cu} " +
     $"target={target} achieved={wagons.Count} degraded={(degraded ? "yes" : "no")} " +
     $"wagons={string.Join(",", wagons)}");
Puts($"[RESOLVE OK] faction={fk} compositionKey={compositionKey} target={target} achieved={achieved} degraded={degradedStr} wagons={string.Join(",", wagons)}");



// HEAVY: apply on top of normal wagons (does NOT count into Limits.C)
List<string> heavyAssignmentsWagons;
ApplyHeavyToWagons(fk, compositionKey, ref wagons, out heavyAssignmentsWagons);

// snapshot for BuildPopulatePlan (post-spawn)
_lastHeavyFactionKey = fk;
_lastHeavyCompositionKey = compositionKey;
_lastHeavyAssignmentsWagons = heavyAssignmentsWagons;

return new object[] { true, compositionKey, wagons, "OK", heavyAssignmentsWagons };

}


        // Контракт: (ok, reason)
        // На первом шаге допускаем всегда OK (без “попробуем другое”)
        private object ValidateWagons(string faction, List<string> wagonNames)
        {
            // fail-fast: null ИЛИ пустой список — иначе CORE получит только локомотив
if (wagonNames == null)
    return new object[] { false, "WAGON_NAMES_NULL" };

if (wagonNames.Count == 0)
    return new object[] { false, "WAGON_NAMES_EMPTY" };

return new object[] { true, "OK" };


        }

        // Контракт: (ok, plan, reason)
        // plan может быть минимальным/пустым (CORE ApplyPopulatePlan сейчас stub)
        private object BuildPopulatePlan(string activeFactionKey, string compositionKey, string layoutName, List<BaseEntity> trainCars)
        {
            if (string.IsNullOrWhiteSpace(activeFactionKey))
                return new object[] { false, null, "FACTION_EMPTY" };

            if (string.IsNullOrWhiteSpace(compositionKey))
                return new object[] { false, null, "COMPOSITIONKEY_EMPTY" };

            // ensure Helltrain.json snapshot is loaded (for CrateSlotWeights)
            if (_helltrainCfg == null)
            {
                if (!TryLoadHelltrainConfig(out var cfgReason))
                    return new object[] { false, null, string.IsNullOrWhiteSpace(cfgReason) ? "HELLTRAINCFG_LOAD_FAIL" : cfgReason };
            }

            // NOTE: layoutName is kept only as debug label, NOT as SoT for slots.
// Slots MUST be counted per actual car in trainCars using that car's wagonKey -> layout.
int carsCount = trainCars?.Count ?? 0;

// per-car slots counts (global plan cursor is implicit by totalSlots/totalNpcSlots)
var slotsByCar = new List<int>(carsCount);
var npcSlotsByCar = new List<int>(carsCount);

int totalSlots = 0;
int totalNpcSlots = 0;

// Track layout resolution quality (degrade, no авария)
bool slotsDegraded = false;
string slotsDegradeReason = null;

for (int c = 0; c < carsCount; c++)
{
    int perCarCrateSlots = 0;
    int perCarNpcSlots = 0;

    var car = trainCars[c];
    if (car == null || car is TrainEngine)
    {
        slotsByCar.Add(0);
        npcSlotsByCar.Add(0);
        continue;
    }

    var wagonKey = GetWagonKeyFromCar(car, activeFactionKey);
    if (string.IsNullOrWhiteSpace(wagonKey))
    {
        // Cannot classify car -> no slots counted for this car (degrade)
        slotsDegraded = true;
        slotsDegradeReason = slotsDegradeReason ?? $"WAGONKEY_RESOLVE_FAIL:c={c} prefab={(car.ShortPrefabName ?? "null")}";
        slotsByCar.Add(0);
        npcSlotsByCar.Add(0);
        continue;
    }

    if (!TryLoadLayoutLegacy(wagonKey, out TrainLayoutLegacy carLayout, out var carLayoutReason))
    {
        // Layout missing for this wagonKey -> no slots counted (degrade, no fallback)
        slotsDegraded = true;
        slotsDegradeReason = slotsDegradeReason ?? $"LAYOUT_LOAD_FAIL:{wagonKey}:{carLayoutReason}";
        slotsByCar.Add(0);
        npcSlotsByCar.Add(0);
        continue;
    }

    var carCrateSlots = carLayout.CrateSlots ?? new List<CrateSlotSpec>();
    var carNpcSlots = carLayout.NpcSlots ?? new List<NpcSlotSpec>();

    perCarCrateSlots = carCrateSlots.Count;
    perCarNpcSlots = carNpcSlots.Count;

    slotsByCar.Add(perCarCrateSlots);
    npcSlotsByCar.Add(perCarNpcSlots);

    totalSlots += perCarCrateSlots;
    totalNpcSlots += perCarNpcSlots;
}

// Assignments: линейно на весь состав (0..totalSlots-1)
// Контракт: каждый assignment содержит prefabPath + lootKey (из Tier SoT)
var assignments = new List<Dictionary<string, object>>(totalSlots);

int noneCount = 0;
int crateCount = 0;
bool degraded = false;
string degradeReason = null;
// propagate slot-counting degrade into crate plan flags
if (slotsDegraded)
{
    degraded = true;
    degradeReason = degradeReason ?? slotsDegradeReason;
}
// slot weights from Helltrain.json (SoT)
// If weights missing -> RollCrateSlot will use defaults (None=1, DefaultCrate=1)
var slotWeights = GetCrateSlotWeightsFromHelltrain(activeFactionKey);

// crate pool: Tier SoT (keys) — use existing mapping
var pool = BuildCrateTypePool(activeFactionKey);
if (pool == null || pool.Count == 0)
{
    // No fallback content: keep all crate slots empty, mark degraded (but do not аварить поезд)
    degraded = true;
    degradeReason = degradeReason ?? "CRATE_TIER_POOL_EMPTY";
    pool = new List<string>(); // keep non-null for logging/plan fields
}

var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
foreach (var t in pool) byType[t] = 0;
// максимум 1 retry при недозаполнении (2 итерации всего)
for (int attempt = 0; attempt < 2; attempt++)
{
    assignments.Clear();
    noneCount = 0;
    crateCount = 0;

    foreach (var t in pool) byType[t] = 0;

    for (int i = 0; i < totalSlots; i++)
    {
        bool spawnDefault = RollCrateSlot(slotWeights);
        if (!spawnDefault)
        {
            assignments.Add(new Dictionary<string, object>
            {
                ["slotIndex"] = i,
                ["lootKey"] = null,
                ["prefabPath"] = null
            });
            noneCount++;
            continue;
        }

        var pickedLootKey = pool[_rng.Next(pool.Count)];
        if (!TryResolveCratePrefabPath(pickedLootKey, out var prefabPath))
        {
            // No fallback content: if Tier mapping is missing -> leave empty and mark degraded.
            assignments.Add(new Dictionary<string, object>
            {
                ["slotIndex"] = i,
                ["lootKey"] = null,
                ["prefabPath"] = null
            });

            noneCount++;
            degraded = true;
            degradeReason = $"UNKNOWN_LOOTKEY:{pickedLootKey}";
            continue;
        }

        assignments.Add(new Dictionary<string, object>
        {
            ["slotIndex"] = i,
            ["lootKey"] = pickedLootKey,
            ["prefabPath"] = prefabPath
        });

        crateCount++;
        byType[pickedLootKey]++;
    }

    // недозаполнение: если при наличии слотов получилось 0 ящиков — 1 retry
    if (totalSlots > 0 && crateCount == 0)
    {
        if (attempt == 0) continue; // 1 retry
        degraded = true;
        degradeReason = degradeReason ?? "CRATE_ALL_NONE";
    }

    break;
}


// NpcAssignments: линейно на весь состав (0..totalNpcSlots-1)
var npcAssignments = new List<Dictionary<string, object>>(totalNpcSlots);

int npcNoneCount = 0;
int npcCount = 0;
int npcAssignedCount = 0;
bool npcDegraded = false;
string npcDegradeReason = null;
// propagate slot-counting degrade into NPC plan flags
if (slotsDegraded)
{
    npcDegraded = true;
    npcDegradeReason = npcDegradeReason ?? slotsDegradeReason;
}
var npcKitPool = BuildNpcKitPool(activeFactionKey);


for (int i = 0; i < totalNpcSlots; i++)
{
    
    string kitKey = null;

// STRICT: kitKey is chosen only from Tier pool (equal weight)
if (npcKitPool != null && npcKitPool.Count > 0)
{
    kitKey = npcKitPool[_rng.Next(npcKitPool.Count)];
    if (!string.IsNullOrWhiteSpace(kitKey))
        kitKey = kitKey.Trim();
}


    if (string.IsNullOrWhiteSpace(kitKey))
    {
        npcAssignments.Add(new Dictionary<string, object>
        {
            ["slotIndex"] = i,
            ["kitKey"] = null
        });

        npcNoneCount++;
        npcDegraded = true;
        npcDegradeReason = npcDegradeReason ?? "NPC_TIER_POOL_EMPTY";
		if (_cfg != null && _cfg.Debug)
    Puts($"[NPC ASSIGN] slot={i} SKIP reason={npcDegradeReason}");


        continue;
    }

    npcAssignments.Add(new Dictionary<string, object>
    {
        ["slotIndex"] = i,
        ["kitKey"] = kitKey
    });

    npcAssignedCount++;
	if (_cfg != null && _cfg.Debug)
    Puts($"[NPC ASSIGN] slot={i} kitKey={kitKey}");

}

// If there are npc slots but nothing assigned -> degraded
if (totalNpcSlots > 0 && npcAssignedCount == 0)
{
    npcDegraded = true;
    npcDegradeReason = npcDegradeReason ?? "NPC_ALL_EMPTY";
}

// HEAVY assignments (aligned to trainCars indices; ENGINE => "None")
var heavyAssignmentsCars = new List<string>(carsCount);
bool heavyDegraded = false;
string heavyDegradeReason = null;

for (int i = 0; i < carsCount; i++)
    heavyAssignmentsCars.Add("None");

if (!string.IsNullOrWhiteSpace(_lastHeavyFactionKey) &&
    !string.IsNullOrWhiteSpace(_lastHeavyCompositionKey) &&
    string.Equals(_lastHeavyFactionKey, activeFactionKey.Trim().ToUpperInvariant(), StringComparison.OrdinalIgnoreCase) &&
    string.Equals(_lastHeavyCompositionKey, compositionKey, StringComparison.OrdinalIgnoreCase) &&
    _lastHeavyAssignmentsWagons != null)
{
    int wagonCursor = 0;
    for (int c = 0; c < carsCount; c++)
    {
        var car = trainCars[c];
        if (car == null || car is TrainEngine)
            continue;

        if (wagonCursor < _lastHeavyAssignmentsWagons.Count)
        {
            heavyAssignmentsCars[c] = _lastHeavyAssignmentsWagons[wagonCursor] ?? "None";
        }
        else
        {
            heavyDegraded = true;
            heavyDegradeReason = heavyDegradeReason ?? "HEAVY_CURSOR_OOB";
            heavyAssignmentsCars[c] = "None";
        }

        wagonCursor++;
    }

    if (wagonCursor != _lastHeavyAssignmentsWagons.Count)
    {
        heavyDegraded = true;
        heavyDegradeReason = heavyDegradeReason ?? $"HEAVY_WAGONCOUNT_MISMATCH expected={_lastHeavyAssignmentsWagons.Count} actual={wagonCursor}";
    }
}
else
{
    // no snapshot or mismatch => all None (safe)
}

            // План: мета + контрактные поля crates
            var plan = new Dictionary<string, object>
            {
                ["PlanId"] = Guid.NewGuid().ToString("N"),
                ["FactionKey"] = activeFactionKey,
                ["CompositionKey"] = compositionKey,
                ["LayoutName"] = layoutName,
                ["CarCount"] = trainCars?.Count ?? 0,
                ["HeavyAssignments"] = heavyAssignmentsCars,
                ["HeavyDegraded"] = heavyDegraded,
                ["HeavyDegradeReason"] = heavyDegradeReason,
				["NpcAssignments"] = npcAssignments,
["NpcDegraded"] = npcDegraded,
["NpcDegradeReason"] = npcDegradeReason,

["Degraded"] = degraded,
["DegradeReason"] = degradeReason,


                ["CrateTypePool"] = pool,
                ["CrateAssignments"] = assignments
				
            };

            if (_cfg != null && _cfg.Debug)
            {
                var distParts = new List<string>();
                foreach (var kv in byType)
                    distParts.Add($"{kv.Key}={kv.Value}");

                Puts($"[POPPLAN DBG] faction={activeFactionKey} layout={layoutName} cars={carsCount} slotsByCar={string.Join(",", slotsByCar)} totalSlots={totalSlots} none={noneCount} crates={crateCount} degraded={(degraded ? "yes" : "no")} reason={(degradeReason ?? "-")} pool={string.Join(",", pool)} dist={string.Join(" ", distParts)}");
Puts($"[NPCPLAN DBG] faction={activeFactionKey} layout={layoutName} cars={carsCount} npcSlotsByCar={string.Join(",", npcSlotsByCar)} totalNpcSlots={totalNpcSlots} none={npcNoneCount} assigned={npcAssignedCount} degraded={(npcDegraded ? "yes" : "no")} reason={(npcDegradeReason ?? "-")}");


            }

            return new object[] { true, plan, "OK" };
        }

    }
}
