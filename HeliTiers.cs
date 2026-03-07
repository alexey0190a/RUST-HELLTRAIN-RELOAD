using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("HeliTiers", "HELLBLOOD", "0.2.3")]
    [Description("Tiered patrol helicopter spawns with per-tier settings, cooldowns, announcements, rocket/napalm control, killer announce (cached), and crash-at-death-point binding.")]
    public class HeliTiers : RustPlugin
    {
        [PluginReference] private Plugin Loottable;

        #region Config

        private ConfigData config;

        public class Tier
        {
            public string Id;
            public string Title;
            public string Prefix;
            public int Health;
            public int Crates;
            public bool NapalmEnabled;

            // зарезервировано (как в твоём файле)
            public float BulletDamage;
            public float CruiseSpeed;
            public float CruiseAltitude;
            public int RocketsPerVolley;
            public float RocketIntervalSeconds;
            public string LootPreset;

            // текст на тир
            public string SpawnMsg;
            public string EngageMsg;
            public string DownMsg;      // поддерживает {count} {player} {prefix}
            public string FinishedMsg;
        }

        public class MessagesCfg
        {
            public string Spawn = "{prefix} Поднимается в небо.";
            public string Engage = "{prefix} Захват цели.";
            public string Down = "{prefix} Сбит. Ящики: {count}.";
            public string Finished = "{prefix} Ушёл живым. Позор охотникам!";

            public string CooldownActive = "Подождите {minutes} мин. до следующего вызова.";
            public string LimitReached = "Достигнут лимит активных вертолётов ({limit}).";
            public string TierNotFound = "Тир \"{tier}\" не найден.";
            public string Spawned = "";
            public string Stopped = "{prefix} Снят с карты.";
            public string NoPermission = "Недостаточно прав.";
            public string ListActiveHeader = "Активные вертолёты: {count}";
            public string DoubleSpawnProc = "🎲 Шанс сработал: в небо поднялся второй вертолёт!";
        }

        public class GeneralCfg
        {
            public bool Announce = true;
            public int GlobalCooldownSeconds = 7600;
            public int ActiveHeliLimit = 2;
            public bool CleanOnUnload = true;
            public string SpawnMode = "WaterRing";
            public float WaterRingRadiusMin = 350f;
            public float WaterRingRadiusMax = 480f;
            public float DefaultSpawnAltitude = 60f;
            public bool ExposeApi = true;
            public bool DebugProjectilesEnabled = false;
            public bool DisableVanillaPatrolHeli = true;
            public bool KeepConvoyHeli = true;
        }

        public class AutopilotSelectionCfg
        {
            public string Mode = "weighted";
            public Dictionary<string, int> Weights = new Dictionary<string, int>();
            public int NoRepeatLast = 1;
        }

        public class AutopilotCfg
        {
            public bool Enabled = true;
            public int CheckPeriodSeconds = 60;
            public int ActiveLimit = 1;
            public int TTLMinutes = 30;
            public int SecondHeliChanceDenominator = 12;
            public bool RandomTier = true;
            public string FallbackTier = "normal";
            public List<string> AllowedTiers = new List<string>();
            public bool Debug = false;
            public AutopilotSelectionCfg Selection = new AutopilotSelectionCfg();
        }

        public class ConfigData
        {
            public GeneralCfg General = new GeneralCfg();
            public MessagesCfg Messages = new MessagesCfg();
            public List<Tier> Tiers = new List<Tier>();
            public AutopilotCfg Autopilot = new AutopilotCfg();
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData
            {
                General = new GeneralCfg
                {
                    Announce = true,
                    GlobalCooldownSeconds = 7600,
                    ActiveHeliLimit = 2,
                    CleanOnUnload = true,
                    SpawnMode = "WaterRing",
                    WaterRingRadiusMin = 350f,
                    WaterRingRadiusMax = 480f,
                    DefaultSpawnAltitude = 60f,
                    ExposeApi = true,
                    DebugProjectilesEnabled = false,
                    DisableVanillaPatrolHeli = true,
                    KeepConvoyHeli = true
                },
                Messages = new MessagesCfg(),
                Autopilot = new AutopilotCfg
                {
                    Enabled = true,
                    CheckPeriodSeconds = 60,
                    ActiveLimit = 1,
                    TTLMinutes = 30,
                    SecondHeliChanceDenominator = 12,
                    RandomTier = true,
                    FallbackTier = "normal",
                    AllowedTiers = new List<string>(),
                    Debug = false,
                    Selection = new AutopilotSelectionCfg
                    {
                        Mode = "weighted",
                        Weights = new Dictionary<string, int>
                        {
                            {"easy", 1}, {"normal", 3}, {"strong", 3}, {"hardcore", 2}
                        },
                        NoRepeatLast = 1
                    }
                },
                Tiers = new List<Tier>
                {
                    new Tier{
                        Id="easy", Title="EASY", Prefix="[Вертолет EASY]",
                        Health=7000, Crates=1, NapalmEnabled=false,
                        BulletDamage=8, CruiseSpeed=22, CruiseAltitude=28,
                        RocketsPerVolley=1, RocketIntervalSeconds=5.0f,
                        SpawnMsg="{prefix} Взлетает. Разминка началась.",
                        EngageMsg="{prefix} Цель отмечена. Пощекочим нервы.",
                        DownMsg="{prefix} Сбит. Заберите {count} ящ.",
                        FinishedMsg="{prefix} Ушёл живым. Эх..."
                    },
                    new Tier{
                        Id="normal", Title="NORMAL", Prefix="[Вертолет NORMAL]",
                        Health=10000, Crates=2, NapalmEnabled=false,
                        BulletDamage=12, CruiseSpeed=25, CruiseAltitude=30,
                        RocketsPerVolley=2, RocketIntervalSeconds=4.0f,
                        SpawnMsg="{prefix} В небе. Начинаем охоту.",
                        EngageMsg="{prefix} Цель в прицеле.",
                        DownMsg="{prefix} Рухнул. Ящиков: {count}.",
                        FinishedMsg="{prefix} Свалил. Слабовато."
                    },
                    new Tier{
                        Id="strong", Title="STRONG", Prefix="[Вертолет STRONG]",
                        Health=13000, Crates=3, NapalmEnabled=true,
                        BulletDamage=16, CruiseSpeed=28, CruiseAltitude=32,
                        RocketsPerVolley=3, RocketIntervalSeconds=3.5f,
                        SpawnMsg="{prefix} Рёв винтов. Готовьтесь к аду!",
                        EngageMsg="{prefix} Прижал добычу!",
                        DownMsg="{prefix} Упал. Разгребайте {count}.",
                        FinishedMsg="{prefix} Улетел. Сегодня вам повезло…"
                    },
                    new Tier{
                        Id="hardcore", Title="HARDCORE", Prefix="[Вертолет HARDCORE]",
                        Health=20000, Crates=5, NapalmEnabled=true,
                        BulletDamage=20, CruiseSpeed=30, CruiseAltitude=34,
                        RocketsPerVolley=3, RocketIntervalSeconds=4.0f,
                        SpawnMsg="{prefix} БЕРСЕРК В НЕБЕ!",
                        EngageMsg="{prefix} В ОГОНЬ!",
                        DownMsg="{prefix} РАЗНЕСЕН! {count} ЯЩИКОВ В АГОНИИ!",
                        FinishedMsg="{prefix} УШЁЛ ЖИВЫМ — ПОЗОР ОХОТНИКАМ!"
                    },
                }
            };
            SaveConfig();
        }

        private void SaveConfig() => Config.WriteObject(config, true);
        private void LoadConfigValues()
        {
            try
            {
                config = Config.ReadObject<ConfigData>();
                if (config == null) throw new Exception("null config");
                if (config.Tiers == null || config.Tiers.Count == 0) LoadDefaultConfig();
                if (config.Autopilot == null) config.Autopilot = new AutopilotCfg();
                if (config.Autopilot.AllowedTiers == null) config.Autopilot.AllowedTiers = new List<string>();
                if (config.Autopilot.Selection == null) config.Autopilot.Selection = new AutopilotSelectionCfg();
                if (config.Autopilot.Selection.Weights == null) config.Autopilot.Selection.Weights = new Dictionary<string, int>();
            }
            catch (Exception e)
            {
                PrintError($"Error reading config, loading defaults: {e.Message}");
                LoadDefaultConfig();
            }
        }

        #endregion

        #region State

        private readonly Dictionary<ulong, ActiveHeli> active = new Dictionary<ulong, ActiveHeli>();

        // crash bind
        private readonly Dictionary<ulong, Vector3> forcedCrashPos = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, int> crashSpawnCount = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, double> antiRedirectUntil = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, string> crashTierByHeli = new Dictionary<ulong, string>();

        // кэш последнего игрока, кто наносил урон (чтобы не было "неизвестный")
        private readonly Dictionary<ulong, string> lastAttacker = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, Timer> autopilotTtlTimers = new Dictionary<ulong, Timer>();
        private readonly Queue<string> autopilotLastPicked = new Queue<string>();
        private Timer autopilotTimer;

        private double lastDeathTime;
        private double autopilotLastDeathTime;

        private class ActiveHeli
        {
            public ulong NetId;
            public string TierId;
            public BaseEntity Entity;
        }

        private class HeliTiersOwnedMarker : FacepunchBehaviour {}

        #endregion

        #region Hooks

        private void Init()
        {
            LoadConfigValues();
            permission.RegisterPermission("helitiers.admin", this);
            foreach (var t in config.Tiers)
                permission.RegisterPermission($"helitiers.spawn.{t.Id}", this);

            // Ensure vanilla does not force helicopter to crash at monuments
            ForceMonumentCrashOff();
        }

        private void OnServerInitialized()
        {
            CleanupOrphaned();
            RegisterHeliTiersPresetsToLoottable();
            StartAutopilot();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null) return;
            if (!string.Equals(plugin.Name, "Loottable", StringComparison.OrdinalIgnoreCase)) return;
            RegisterHeliTiersPresetsToLoottable();
        }

        private void Unload()
        {
            if (config.General.CleanOnUnload)
            {
                foreach (var kv in active)
                {
                    var ent = kv.Value.Entity;
                    if (ent != null && !ent.IsDestroyed) ent.Kill();
                }
            }
            active.Clear();
            forcedCrashPos.Clear();
            crashSpawnCount.Clear();
            crashTierByHeli.Clear();
            lastAttacker.Clear();
            autopilotTimer?.Destroy();
            autopilotTimer = null;
            foreach (var kv in autopilotTtlTimers)
                kv.Value?.Destroy();
            autopilotTtlTimers.Clear();
        }

        // кэшируем последнего атакующего игрока
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            // интересуемся только патрульником
            var shortName = (entity.ShortPrefabName ?? string.Empty).ToLower();
            if (!shortName.Contains("patrolhelicopter")) return;

            var be = entity as BaseEntity;
            if (be == null || !IsOurHeli(be)) return;

            var attacker = info.InitiatorPlayer;
            if (attacker == null) return;

            ulong id = entity.net?.ID.Value ?? 0UL;
            if (id == 0UL) return;

            lastAttacker[id] = attacker.displayName;
            // Пред-привязка точки падения при летальном уроне (до физического "дотягивания" до монумента)
            try
            {
                var bce = entity as BaseCombatEntity;
                float hp = bce != null ? bce.Health() : 0f;
                float incoming = info?.damageTypes != null ? info.damageTypes.Total() : 0f;
                if (hp - incoming <= 0.1f)
                {
                    forcedCrashPos[id] = entity.transform.position;
                    crashSpawnCount[id] = 0;
                    antiRedirectUntil[id] = CurrentTime() + 12.0f; // слегка дольше держим
                }
            } catch {}
    
        }

        // смерть в бою → анонс с именем убийцы (с кэшем) + анти-флуд + crash bind
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;
            var netId = entity.net?.ID.Value ?? 0UL;
            if (netId == 0UL) return;

            ActiveHeli ah;
            if (active.TryGetValue(netId, out ah))
            {
                var tier = GetTier(ah.TierId);

                // запоминаем точку смерти
                forcedCrashPos[netId] = entity.transform.position;
                crashSpawnCount[netId] = 0;
                crashTierByHeli[netId] = ah.TierId;

                antiRedirectUntil[netId] = CurrentTime() + 10.0f;// имя убийцы: прямой инициатор → кэш → "неизвестный"
                string killerName = (info?.InitiatorPlayer != null)
                    ? info.InitiatorPlayer.displayName
                    : (lastAttacker.TryGetValue(netId, out var cached) ? cached : "неизвестный");

                var tpl = string.IsNullOrEmpty(tier.DownMsg) ? config.Messages.Down : tier.DownMsg;
                var down = tpl
                    .Replace("{player}", EscapeRichText(killerName))
                    .Replace("{count}", tier?.Crates.ToString() ?? "0")
                    .Replace("{prefix}", tier.Prefix);

                // анти-флуд: задержка, чтобы не совпасть с дождём debris/napalm
                timer.Once(0.5f, () => Server.Broadcast(down));

                active.Remove(netId);
                lastDeathTime = CurrentTime();
                autopilotLastDeathTime = CurrentTime();

                // чистка вспомогательных структур
                lastAttacker.Remove(netId);
                timer.Once(120f, () => { forcedCrashPos.Remove(netId); crashSpawnCount.Remove(netId); crashTierByHeli.Remove(netId); });
            }
        }

        // улетел/киломанулся → Finished (с КД)
        private void OnEntityKill(BaseNetworkable networkable)
        {
            var be = networkable as BaseEntity;
            if (be == null) return;

            var netId = be.net?.ID.Value ?? 0UL;
            if (netId == 0UL) return;

            ActiveHeli ah;
            if (active.TryGetValue(netId, out ah))
            {
                var tier = GetTier(ah.TierId);
                Announce(tier, TierLine(config.Messages.Finished, tier, tier.FinishedMsg));
                active.Remove(netId);
                autopilotLastDeathTime = CurrentTime();
            }

            forcedCrashPos.Remove(netId);
            crashSpawnCount.Remove(netId);
            crashTierByHeli.Remove(netId);
            lastAttacker.Remove(netId);
            CancelAutopilotTtl(netId);
        }

        // фильтр ракет/напалма + перенос краш-объектов в точку смерти
        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            var ent = networkable as BaseEntity;
            if (ent == null) return;

            string s = ent.ShortPrefabName ?? string.Empty;

            if (s == "patrolhelicopter" && config.General.DisableVanillaPatrolHeli)
            {
                if (!IsOurHeli(ent))
                {
                    if (!(config.General.KeepConvoyHeli && IsConvoyHeli(ent)))
                    {
                        if (config.Autopilot != null && config.Autopilot.Debug)
                            Puts("[HeliTiers] Kill non-tier patrol helicopter netid=" + (ent.net?.ID.Value ?? 0UL));
                        ent.Kill();
                        return;
                    }
                }
            }

            // --- контроль снарядов/огня по тиру ---
            bool candidate = IsBlockedProjectile(s);
            if (candidate || config.General.DebugProjectilesEnabled)
            {
                BaseEntity creator = null;
                try { creator = ent.creatorEntity; } catch {}
                ulong creatorId = creator?.net?.ID.Value ?? 0UL;

                ActiveHeli ah = null;
                Tier tier = null;
                bool belongsToOurHeli = false;

                if (creatorId != 0UL && active.TryGetValue(creatorId, out ah))
                {
                    tier = GetTier(ah.TierId);
                    belongsToOurHeli = tier != null;
                }
                else
                {
                    float best = 99999f;
                    ActiveHeli bestHeli = null;
                    foreach (var kv in active)
                    {
                        var hEnt = kv.Value.Entity;
                        if (hEnt == null || hEnt.IsDestroyed) continue;
                        float dist = Vector3.Distance(hEnt.transform.position, ent.transform.position);
                        if (dist < best)
                        {
                            best = dist;
                            bestHeli = kv.Value;
                        }
                    }
                    if (bestHeli != null && best <= 80f)
                    {
                        ah = bestHeli;
                        tier = GetTier(ah.TierId);
                        belongsToOurHeli = tier != null;
                    }
                }

                if (candidate && belongsToOurHeli && tier != null)
                {
                    if (!tier.NapalmEnabled)
                    {
                        if (config.General.DebugProjectilesEnabled)
                            Puts($"[HeliTiers] BLOCK {s} near {tier.Id}");
                        ent.Kill();
                        return;
                    }
                }
            }

            // --- crash bind: ящики/обломки/напалм к точке смерти ---
            bool isCrashSpawn =
                s == "heli_crate" ||
                s.Contains("gibs") || s.Contains("debris") ||
                s == "fireball_small" || s == "napalm" || s == "rocket_heli_napalm";

            if (isCrashSpawn)
            {
                ulong bestId = 0UL;
                float bestDist = 99999f;
                Vector3 bestPos = Vector3.zero;

                foreach (var kv in forcedCrashPos)
                {
                    float d = Vector3.Distance(kv.Value, ent.transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestPos = kv.Value;
                        bestId = kv.Key;
                    }
                }

                if (bestId != 0UL && bestDist <= 80f)
                {
                    // ent.limitNetworking disabled for crash spawns

                    Vector3 drop = bestPos + new Vector3(UnityEngine.Random.Range(-3f, 3f), 0f, UnityEngine.Random.Range(-3f, 3f));
                    try
                    {
                        float ground = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(drop) : drop.y;
                        if (drop.y < ground + 0.2f) drop.y = ground + 0.2f;
                    } catch {}
ent.transform.position = drop;

// Anti-redirect: keep crash spawns at death point for a short window
double until = 0;
antiRedirectUntil.TryGetValue(bestId, out until);
bool crateNow = (s == "heli_crate");
if (until > 0 && CurrentTime() <= until)
{
    int ticks = 0;
    var anchor = drop;
    Timer rep = null;
rep = timer.Every(0.1f, () => {
        if (ent == null || ent.IsDestroyed) { if (rep != null) rep.Destroy(); return; }

        ticks++;
        ent.transform.position = anchor;
        if (ticks >= 60) // ~6 seconds of enforcement
        {
            // final network update
            if (crateNow) { ent.SendNetworkUpdateImmediate(); } else { timer.Once(0.5f, () => { if (ent != null && !ent.IsDestroyed) ent.SendNetworkUpdate(); }); }
            rep.Destroy();
        }
    });
}

                    if (s == "heli_crate") { ent.SendNetworkUpdateImmediate(); } else { timer.Once(0.5f, () => { if (ent != null && !ent.IsDestroyed) ent.SendNetworkUpdate(); }); }

                    if (s == "heli_crate")
                        TryAssignLootPreset(bestId, ent as LootContainer);

                    crashSpawnCount[bestId] = crashSpawnCount.TryGetValue(bestId, out var c) ? c + 1 : 1;
                    if (config.General.DebugProjectilesEnabled)
                        Puts($"[HeliTiers] Crash bind: moved {s} to death point ({crashSpawnCount[bestId]})");
                }
            }
        }

        #endregion

        #region Commands

        [ChatCommand("helitiers.list")]
        private void CmdList(BasePlayer player, string command, string[] args)
        {
            if (!HasAnyPerm(player))
            {
                Reply(player, config.Messages.NoPermission);
                return;
            }

            Reply(player, config.Messages.ListActiveHeader.Replace("{count}", active.Count.ToString()));
            foreach (var kv in active)
            {
                var t = GetTier(kv.Value.TierId);
                Reply(player, $"- {kv.Value.TierId} ({t?.Title ?? "?"}) id={kv.Key}");
            }
        }

        [ChatCommand("helitiers.stop")]
        private void CmdStop(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, "helitiers.admin"))
            {
                Reply(player, config.Messages.NoPermission);
                return;
            }

            string tierFilter = args.Length > 0 ? args[0].ToLower() : "all";
            int removed = 0;
            var toKill = new List<ActiveHeli>();
            foreach (var a in active.Values)
            {
                if (tierFilter == "all" || a.TierId == tierFilter)
                    toKill.Add(a);
            }
            foreach (var a in toKill)
            {
                if (a.Entity != null && !a.Entity.IsDestroyed) a.Entity.Kill();
                active.Remove(a.NetId);
                removed++;
            }
            Reply(player, $"Снято: {removed}");
        }

        [ChatCommand("helitiers.inspect")]
        private void CmdInspect(BasePlayer player, string command, string[] args)
        {
            if (!HasAnyPerm(player))
            {
                Reply(player, config.Messages.NoPermission);
                return;
            }
            if (active.Count == 0)
            {
                Reply(player, "Активных вертолётов нет.");
                return;
            }
            foreach (var kv in active)
            {
                var a = kv.Value;
                var ent = a.Entity;
                if (ent == null || ent.IsDestroyed)
                {
                    Reply(player, $"- id={kv.Key} ({a.TierId}): entity missing");
                    continue;
                }
                var bce = ent as BaseCombatEntity;
                var hp = bce != null ? $"{bce.Health():0}" : "n/a";
                var ai = ent.GetComponent<PatrolHelicopterAI>();
                int crates = -1;
                if (!(TryGetIntField(ai, "maxCratesToSpawn", out crates) ||
                      TryGetIntField(ai, "numCratesToSpawn", out crates) ||
                      TryGetIntField(ai, "cratesToSpawn", out crates) ||
                      TryGetIntField(ent, "maxCratesToSpawn", out crates) ||
                      TryGetIntField(ent, "numCratesToSpawn", out crates)))
                {
                    crates = -1;
                }
                string cratesStr = crates >= 0 ? crates.ToString() : "?";
                bool hasAi = ai != null;
                string type = ent.ShortPrefabName;
                Reply(player, $"- id={kv.Key} tier={a.TierId} type={type} hp={hp} cratesField={cratesStr} ai={hasAi}");
            }
        }

        [ChatCommand("helitiers.spawn")]
        private void CmdSpawn(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                Reply(player, "Использование: /helitiers.spawn <tierId> [x z]");
                return;
            }

            var tierId = args[0].ToLower();
            var tier = GetTier(tierId);
            if (tier == null)
            {
                Reply(player, config.Messages.TierNotFound.Replace("{tier}", tierId));
                return;
            }

            if (!(permission.UserHasPermission(player.UserIDString, "helitiers.admin") ||
                  permission.UserHasPermission(player.UserIDString, $"helitiers.spawn.{tierId}")))
            {
                Reply(player, config.Messages.NoPermission);
                return;
            }

            var cdLeft = CooldownLeft();
            if (cdLeft > 0)
            {
                Reply(player, config.Messages.CooldownActive.Replace("{minutes}", Math.Ceiling(cdLeft / 60f).ToString()));
                return;
            }

            if (active.Count >= config.General.ActiveHeliLimit)
            {
                Reply(player, config.Messages.LimitReached.Replace("{limit}", config.General.ActiveHeliLimit.ToString()));
                return;
            }

            Vector3 pos;
            if (args.Length >= 3 && float.TryParse(args[1], out var x) && float.TryParse(args[2], out var z))
                pos = new Vector3(x, config.General.DefaultSpawnAltitude, z);
            else
                pos = GetSpawnPosition();

            var created = SpawnHeliAt(pos, tier);
            if (created == null)
            {
                Reply(player, "Не удалось создать вертолёт.");
                return;
            }

            Reply(player, config.Messages.Spawned.Replace("{prefix}", tier.Prefix));
        }

        #endregion

        #region Core

        private Tier GetTier(string id) => config.Tiers.Find(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        private bool HasAnyPerm(BasePlayer player)
        {
            if (player == null) return false;
            if (permission.UserHasPermission(player.UserIDString, "helitiers.admin")) return true;
            foreach (var t in config.Tiers)
                if (permission.UserHasPermission(player.UserIDString, $"helitiers.spawn.{t.Id}"))
                    return true;
            return false;
        }

        private double CurrentTime() => (double)UnityEngine.Time.realtimeSinceStartup;

        private double CooldownLeft()
        {
            var passed = CurrentTime() - lastDeathTime;
            var left = config.General.GlobalCooldownSeconds - passed;
            return left > 0 ? left : 0;
        }

        private string TierLine(string fallback, Tier tier, string custom)
        {
            var line = string.IsNullOrEmpty(custom) ? fallback : custom;
            return line.Replace("{prefix}", tier.Prefix);
        }

        private void Announce(Tier tier, string message)
        {
            if (!config.General.Announce || tier == null) return;
            var text = message.Replace("{prefix}", tier.Prefix);
            Server.Broadcast(text);
        }

        private void CleanupOrphaned()
        {
            // future
        }

        private void StartAutopilot()
        {
            autopilotTimer?.Destroy();
            autopilotTimer = null;

            if (config.Autopilot == null || !config.Autopilot.Enabled) return;

            autopilotTimer = timer.Every(Mathf.Max(5, config.Autopilot.CheckPeriodSeconds), EvaluateAutopilot);
            if (config.Autopilot.Debug)
            {
                Puts("[HeliTiers] Autopilot ON. ActiveLimit=" + config.Autopilot.ActiveLimit
                    + ", TTL=" + config.Autopilot.TTLMinutes + "m, CD=" + config.General.GlobalCooldownSeconds
                    + "s, Check=" + config.Autopilot.CheckPeriodSeconds + "s");
            }
        }

        private void EvaluateAutopilot()
        {
            if (config.Autopilot == null || !config.Autopilot.Enabled) return;

            if (active.Count > 0) return;
            if (!AutopilotCooldownReady()) return;

            var tier = PickAutopilotTier();
            if (tier == null) return;

            var ok = API_HeliTiers_Start(tier.Id);
            if (!ok) return;

            RememberAutopilotPick(tier.Id);
            if (config.Autopilot.Debug)
                Puts("[HeliTiers] Autopilot spawned tier=" + tier.Id);

            if (config.Autopilot.ActiveLimit < 2) return;
            if (active.Count < 1) return;

            var denominator = Mathf.Max(1, config.Autopilot.SecondHeliChanceDenominator);
            if (UnityEngine.Random.Range(1, denominator + 1) != 1) return;

            var tierSecond = PickAutopilotTier();
            if (tierSecond == null) return;

            var okSecond = API_HeliTiers_Start(tierSecond.Id);
            if (!okSecond) return;

            RememberAutopilotPick(tierSecond.Id);

            if (config.General.Announce && !string.IsNullOrEmpty(config.Messages.DoubleSpawnProc))
                Server.Broadcast(config.Messages.DoubleSpawnProc);

            if (config.Autopilot.Debug)
                Puts("[HeliTiers] Autopilot extra heli spawned by chance 1/" + denominator + ", tier=" + tierSecond.Id);
        }

        private bool AutopilotCooldownReady()
        {
            if (autopilotLastDeathTime <= 0) return true;
            var since = CurrentTime() - autopilotLastDeathTime;
            return since >= config.General.GlobalCooldownSeconds;
        }

        private void RememberAutopilotPick(string tierId)
        {
            if (config.Autopilot.Selection == null || config.Autopilot.Selection.NoRepeatLast <= 0) return;
            autopilotLastPicked.Enqueue(tierId);
            while (autopilotLastPicked.Count > config.Autopilot.Selection.NoRepeatLast)
                autopilotLastPicked.Dequeue();
        }

        private Tier PickAutopilotTier()
        {
            List<string> pool;
            if (config.Autopilot.AllowedTiers != null && config.Autopilot.AllowedTiers.Count > 0)
                pool = config.Autopilot.AllowedTiers;
            else
                pool = config.Tiers.Select(t => t.Id).ToList();

            if (pool == null || pool.Count == 0)
                return GetTier(config.Autopilot.FallbackTier);

            var filtered = new List<string>();
            foreach (var id in pool)
            {
                if (config.Autopilot.Selection == null || config.Autopilot.Selection.NoRepeatLast <= 0 || !autopilotLastPicked.Contains(id))
                    filtered.Add(id);
            }
            if (filtered.Count == 0) filtered = pool;

            if (!config.Autopilot.RandomTier)
                return GetTier(filtered[0]) ?? GetTier(config.Autopilot.FallbackTier);

            string mode = config.Autopilot.Selection != null ? (config.Autopilot.Selection.Mode ?? "weighted") : "weighted";
            var weights = (config.Autopilot.Selection != null && config.Autopilot.Selection.Weights != null)
                ? config.Autopilot.Selection.Weights
                : new Dictionary<string, int>();

            if (mode == "weighted" && weights.Count > 0)
            {
                var items = new List<Tuple<string, int>>();
                int total = 0;
                foreach (var id in filtered)
                {
                    int w;
                    if (!weights.TryGetValue(id, out w)) w = 1;
                    if (w < 0) w = 0;
                    total += w;
                    items.Add(new Tuple<string, int>(id, w));
                }

                if (total > 0)
                {
                    int r = UnityEngine.Random.Range(0, total);
                    int acc = 0;
                    foreach (var it in items)
                    {
                        acc += it.Item2;
                        if (r < acc)
                            return GetTier(it.Item1) ?? GetTier(config.Autopilot.FallbackTier);
                    }
                    return GetTier(items[items.Count - 1].Item1) ?? GetTier(config.Autopilot.FallbackTier);
                }
            }

            int idx = UnityEngine.Random.Range(0, filtered.Count);
            return GetTier(filtered[idx]) ?? GetTier(config.Autopilot.FallbackTier);
        }

        private void ScheduleAutopilotTtl(BaseEntity ent, ulong netId)
        {
            if (config.Autopilot == null || !config.Autopilot.Enabled) return;
            if (config.Autopilot.TTLMinutes <= 0) return;

            CancelAutopilotTtl(netId);

            var ttl = Mathf.Max(60, config.Autopilot.TTLMinutes * 60);
            autopilotTtlTimers[netId] = timer.Once(ttl, () =>
            {
                autopilotTtlTimers.Remove(netId);
                if (ent == null || ent.IsDestroyed) return;

                try
                {
                    var ai = ent.GetComponent<PatrolHelicopterAI>();
                    if (ai != null)
                    {
                        if (config.Autopilot.Debug) Puts("[HeliTiers] TTL retire heli netid=" + netId);
                        ai.Retire();
                        return;
                    }
                }
                catch (Exception e)
                {
                    PrintWarning("Autopilot Retire() failed: " + e.Message);
                }

                ent.Kill();
            });
        }

        private void CancelAutopilotTtl(ulong netId)
        {
            Timer t;
            if (autopilotTtlTimers.TryGetValue(netId, out t))
            {
                t?.Destroy();
                autopilotTtlTimers.Remove(netId);
            }
        }

        private bool IsOurHeli(BaseEntity ent)
        {
            if (ent == null || ent.IsDestroyed) return false;
            if (ent.GetComponent<HeliTiersOwnedMarker>() != null) return true;
            var netId = ent.net?.ID.Value ?? 0UL;
            if (netId == 0UL) return false;
            return active.ContainsKey(netId);
        }

        private bool IsConvoyHeli(BaseEntity ent)
        {
            if (ent == null || ent.IsDestroyed) return false;
            return ent.gameObject.GetComponent("EventHeli") != null;
        }

        private void RegisterHeliTiersPresetsToLoottable()
        {
            if (Loottable == null || config == null || config.Tiers == null) return;

            try
            {
                Loottable.Call("ClearPresets", this);
                Loottable.Call("CreatePresetCategory", this, "HeliTiers");

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tier in config.Tiers)
                {
                    if (tier == null || string.IsNullOrEmpty(tier.LootPreset)) continue;
                    if (!keys.Add(tier.LootPreset)) continue;

                    var display = !string.IsNullOrEmpty(tier.Title) ? tier.Title : tier.LootPreset;
                    Loottable.Call("CreatePreset", this, tier.LootPreset, display, "crate_normal", false);
                }
            }
            catch (Exception e)
            {
                PrintWarning("Loottable preset registration failed: " + e.Message);
            }
        }

        private void TryAssignLootPreset(ulong heliNetId, LootContainer crate)
        {
            if (crate == null || crate.inventory == null || Loottable == null) return;

            string tierId;
            if (!crashTierByHeli.TryGetValue(heliNetId, out tierId) || string.IsNullOrEmpty(tierId)) return;

            var tier = GetTier(tierId);
            if (tier == null || string.IsNullOrEmpty(tier.LootPreset)) return;

            try
            {
                var ok = Loottable.Call("AssignPreset", this, tier.LootPreset, crate.inventory);
                if (config.Autopilot != null && config.Autopilot.Debug)
                    Puts($"[HeliTiers] Loottable AssignPreset tier={tierId} preset={tier.LootPreset} => {ok}");
            }
            catch (Exception e)
            {
                PrintWarning($"Loottable AssignPreset failed for tier={tierId}: {e.Message}");
            }
        }

        private Vector3 GetSpawnPosition()
        {
            var size = TerrainMeta.Size.x;
            var radiusMin = Mathf.Clamp(config.General.WaterRingRadiusMin, 50f, size);
            var radiusMax = Mathf.Clamp(config.General.WaterRingRadiusMax, radiusMin + 1f, size * 0.9f);

            for (int i = 0; i < 25; i++)
            {
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var radius = UnityEngine.Random.Range(radiusMin, radiusMax);
                var x = Mathf.Cos(ang) * radius;
                var z = Mathf.Sin(ang) * radius;
                var y = Mathf.Max(Mathf.Max(100f, config.General.DefaultSpawnAltitude), GetWaterHeight(new Vector3(x, 0, z)) + 30f);
                var pos = new Vector3(x, y, z);

                var waterY = GetWaterHeight(pos);
                if (pos.y > waterY + 5f) return pos;
            }
            return new Vector3(0f, Mathf.Max(120f, config.General.DefaultSpawnAltitude), 0f);
        }

        private BaseEntity SpawnHeliAt(Vector3 position, Tier tier)
        {
            var prefab = "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab";
            try
            {
                if (position.y < 100f) position.y = 120f;

                var ent = GameManager.server.CreateEntity(prefab, position, Quaternion.identity, true);
                if (ent == null)
                {
                    PrintWarning($"CreateEntity returned null for prefab: {prefab} at {position}");
                    return null;
                }

                ent.gameObject.AddComponent<HeliTiersOwnedMarker>();

                ent.Spawn();

                var bce = ent as BaseCombatEntity;
                if (bce == null)
                {
                    PrintWarning("Spawned entity is not BaseCombatEntity; killing it to avoid leaks.");
                    ent.Kill();
                    return null;
                }

                // HP
                try
                {
                    bce.InitializeHealth(tier.Health, tier.Health);
                }
                catch (Exception ex)
                {
                    PrintWarning($"Failed to set helicopter health: {ex.Message}");
                }

                // crates via reflection
                var ai = ent.GetComponent<PatrolHelicopterAI>();
                bool cratesSet = false;
                if (ai != null)
                {
                    cratesSet |= TrySetIntField(ai, "maxCratesToSpawn", tier.Crates);
                    cratesSet |= TrySetIntField(ai, "numCratesToSpawn", tier.Crates);
                    cratesSet |= TrySetIntField(ai, "cratesToSpawn", tier.Crates);
                }
                cratesSet |= TrySetIntField(ent, "maxCratesToSpawn", tier.Crates);
                cratesSet |= TrySetIntField(ent, "numCratesToSpawn", tier.Crates);
                if (!cratesSet)
                {
                    PrintWarning($"Не удалось применить количество ящиков для {tier.Id} — поле версии изменилось.");
                }

                // track
                var netId = ent.net?.ID.Value ?? 0UL;
                active[netId] = new ActiveHeli
                {
                    NetId = netId,
                    TierId = tier.Id,
                    Entity = ent
                };
                ScheduleAutopilotTtl(ent, netId);

                // announce
                Announce(tier, TierLine(config.Messages.Spawn, tier, tier.SpawnMsg));
                timer.Once(5f, () => Announce(tier, TierLine(config.Messages.Engage, tier, tier.EngageMsg)));

                return ent;
            }
            catch (Exception e)
            {
                PrintError($"SpawnHeliAt exception: {e}");
                return null;
            }
        }

        #endregion

        #region API

        private bool API_HeliTiers_Start(string tierId)
        {
            var tier = GetTier(tierId);
            if (tier == null) return false;

            if (CooldownLeft() > 0) return false;
            if (active.Count >= config.General.ActiveHeliLimit) return false;

            var pos = GetSpawnPosition();
            return SpawnHeliAt(pos, tier) != null;
        }

        private int API_HeliTiers_Stop(string tierId)
        {
            int removed = 0;
            var toKill = new List<ActiveHeli>();
            foreach (var a in active.Values)
            {
                if (string.IsNullOrEmpty(tierId) || a.TierId.Equals(tierId, StringComparison.OrdinalIgnoreCase))
                    toKill.Add(a);
            }
            foreach (var a in toKill)
            {
                if (a.Entity != null && !a.Entity.IsDestroyed) a.Entity.Kill();
                active.Remove(a.NetId);
                removed++;
            }
            return removed;
        }

        private bool API_HeliTiers_IsActive(string tierId)
        {
            foreach (var a in active.Values)
                if (a.TierId.Equals(tierId, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        #endregion

        #region Helpers

        private bool IsBlockedProjectile(string shortPrefabName)
        {
            if (string.IsNullOrEmpty(shortPrefabName)) return false;
            var s = shortPrefabName.ToLower();
            return s == "rocket_heli_napalm"
                || s == "napalm"
                || s == "fireball_small"
                || s == "rocket_heli";
        }

        private bool TrySetIntField(object obj, string fieldName, int value)
        {
            if (obj == null) return false;
            try
            {
                var f = obj.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(int))
                {
                    f.SetValue(obj, value);
                    return true;
                }
            }
            catch {}
            return false;
        }

        private bool TryGetIntField(object obj, string fieldName, out int value)
        {
            value = 0;
            if (obj == null) return false;
            try
            {
                var f = obj.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(int))
                {
                    value = (int)f.GetValue(obj);
                    return true;
                }
            }
            catch {}
            return false;
        }

        private float GetWaterHeight(Vector3 pos)
        {
            try
            {
                if (TerrainMeta.WaterMap != null) return TerrainMeta.WaterMap.GetHeight(pos);
                return 0f;
            }
            catch { return 0f; }
        }

        private string EscapeRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("<", "\u02C2").Replace(">", "\u02C3");
        }

        private void Reply(BasePlayer player, string msg) => player?.ChatMessage(msg);


        private void ForceMonumentCrashOff()
        {
            try
            {
                var asm = typeof(ConsoleSystem).Assembly;
                var tAI = asm.GetType("ConVar+PatrolHelicopterAI");
                var tPH = asm.GetType("ConVar+PatrolHelicopter");
                var fieldAI = tAI != null ? tAI.GetField("monument_crash", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                var fieldPH = tPH != null ? tPH.GetField("monument_crash", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                bool changed = false;
                if (fieldAI != null)
                {
                    fieldAI.SetValue(null, false);
                    changed = true;
                }
                if (fieldPH != null)
                {
                    fieldPH.SetValue(null, false);
                    changed = true;
                }
                if (changed)
                    Puts("[HeliTiers] monument_crash отключён через рефлексию.");
                else
                {
                    // Фоллбек на консольные команды (под разные названия конваров)
                    ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "patrolhelicopterai.monument_crash false");
                    ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "patrolhelicopter.monument_crash false");
                    Puts("[HeliTiers] monument_crash отключён через консольную команду.");
                }
            }
            catch (System.Exception ex)
            {
                PrintWarning($"Не удалось отключить monument_crash: {ex.Message}");
                try
                {
                    ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "patrolhelicopterai.monument_crash false");
                    ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "patrolhelicopter.monument_crash false");
                }
                catch {}
            }
        }

        #endregion
    }
}
