
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("HeliTiersAutopilot", "BH-AI", "1.0.5")]
    [Description("Autospawn + TTL + death cooldown for HeliTiers with weighted random and no-repeat.")]
    public class HeliTiersAutopilot : CovalencePlugin
    {
        [PluginReference] private Plugin HeliTiers;

        #region Config

        private ConfigData _config;

        private class SelectionCfg
        {
            public string Mode = "weighted"; // "uniform" or "weighted"
            public Dictionary<string, int> Weights = new Dictionary<string, int>(); // {"easy":1,"normal":3,...}
            public int NoRepeatLast = 1; // do not repeat last N tiers (at least 0)
        }

        private class ConfigData
        {
            public int CheckPeriodSeconds = 60;
            public int ActiveLimit = 1;
            public int CooldownMinutesOnDeath = 60;
            public int TTLMinutes = 30;
            public bool RandomTier = true;
            public string FallbackTier = "normal";
            public string HeliTiersConfigFileName = "HeliTiers.json";
            public List<string> AllowedTiers = new List<string>();
            public bool Debug = true;
            public SelectionCfg Selection = new SelectionCfg();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
            // sensible defaults
            _config.Selection.Mode = "weighted";
            _config.Selection.Weights = new Dictionary<string, int> {
                {"easy", 1}, {"normal", 3}, {"strong", 3}, {"hardcore", 2}
            };
            _config.Selection.NoRepeatLast = 1;
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _config = Config.ReadObject<ConfigData>(); }
            catch { PrintWarning("Config corrupted, creating default."); LoadDefaultConfig(); }
            if (_config.Selection == null) _config.Selection = new SelectionCfg();
            if (_config.Selection.Weights == null) _config.Selection.Weights = new Dictionary<string, int>();
        }

        protected override void SaveConfig() { Config.WriteObject(_config, true); }

        #endregion

        private float _lastDeathUnix;
        private readonly Dictionary<ulong, Timer> _ttlTimers = new Dictionary<ulong, Timer>();
        private List<string> _cachedTiers = new List<string>();
        private readonly Queue<string> _lastPicked = new Queue<string>(); // history for NoRepeatLast

        private void OnServerInitialized()
        {
            RefreshTierCache();
            timer.Every(Mathf.Max(5, _config.CheckPeriodSeconds), EvaluateAutospawn);
            if (_config.Debug)
            {
                Puts("[HeliTiersAutopilot] Init. ActiveLimit=" + _config.ActiveLimit
                    + ", TTL=" + _config.TTLMinutes + "m, CD=" + _config.CooldownMinutesOnDeath
                    + "m, Check=" + _config.CheckPeriodSeconds + "s");
                var tiers = (_cachedTiers != null && _cachedTiers.Count > 0) ? string.Join(", ", _cachedTiers.ToArray()) : "<none>";
                var allowed = (_config.AllowedTiers != null && _config.AllowedTiers.Count > 0) ? string.Join(", ", _config.AllowedTiers.ToArray()) : "none";
                Puts("[HeliTiersAutopilot] Tiers from HT config: " + tiers + " (Allowed override: " + allowed + ")");
                Puts("[HeliTiersAutopilot] Selection: mode=" + _config.Selection.Mode + ", norepeat=" + _config.Selection.NoRepeatLast);
            }
        }

        #region Autospawn

        private void EvaluateAutospawn()
        {
            int current = CountPatrolHelicopters();
           // if (_config.Debug) Puts("[Autospawn] Active helis: " + current + "/" + _config.ActiveLimit + ", CD ready: " + CooldownReady());

            if (current >= _config.ActiveLimit) return;
            if (!CooldownReady()) return;

            int need = _config.ActiveLimit - current;
            for (int i = 0; i < need; i++)
            {
                string tier = PickTier();
                if (_config.Debug) Puts("[Autospawn] Requesting tier: " + tier);
                var ok = CallHeliTiersStart(tier);
                if (ok)
                {
                    RememberPick(tier);
                }
                else
                {
                    if (_config.Debug) Puts("[Autospawn] HeliTiers rejected spawn (limit/CD/internal). Stop this tick.");
                    break;
                }
            }
        }

        private void RememberPick(string tier)
        {
            if (_config.Selection.NoRepeatLast <= 0) return;
            _lastPicked.Enqueue(tier);
            while (_lastPicked.Count > _config.Selection.NoRepeatLast)
                _lastPicked.Dequeue();
        }

        private bool CooldownReady()
        {
            if (_lastDeathUnix <= 0) return true;
            float since = Now() - _lastDeathUnix;
            return since >= _config.CooldownMinutesOnDeath * 60f;
        }

        private int CountPatrolHelicopters()
        {
            int count = 0;
            foreach (var net in BaseNetworkable.serverEntities)
            {
                var ent = net as BaseEntity;
                if (ent == null || ent.IsDestroyed) continue;
                if (ent.ShortPrefabName == "patrolhelicopter") count++;
            }
            return count;
        }

        private bool CallHeliTiersStart(string tier)
        {
            if (HeliTiers == null)
            {
                PrintWarning("HeliTiers not found. Cannot spawn.");
                return false;
            }
            try
            {
                var res = HeliTiers.Call("API_HeliTiers_Start", tier);
                if (_config.Debug)
                {
                    string s = (res == null) ? "null" : res.ToString();
                    Puts("[Autospawn] API_HeliTiers_Start('" + tier + "') => " + s);
                }
                if (res is bool) return (bool)res;
                return true; // assume success for void
            }
            catch (Exception e)
            {
                PrintWarning("HeliTiers API call failed: " + e.Message);
                return false;
            }
        }

        #endregion

        #region TTL

        private void OnEntitySpawned(BaseNetworkable net)
        {
            var ent = net as BaseEntity;
            if (ent == null || ent.IsDestroyed) return;
            if (ent.ShortPrefabName != "patrolhelicopter") return;
            ScheduleTTL(ent);
        }

        private void ScheduleTTL(BaseEntity ent)
        {
            var id = (ent.net != null && ent.net.ID != null) ? ent.net.ID.Value : 0UL;
            if (id == 0) return;

            Timer old;
            if (_ttlTimers.TryGetValue(id, out old))
            {
                if (old != null) old.Destroy();
                _ttlTimers.Remove(id);
            }

            var ttl = Mathf.Max(60, _config.TTLMinutes * 60);
            _ttlTimers[id] = timer.Once(ttl, () =>
            {
                _ttlTimers.Remove(id);
                if (ent == null || ent.IsDestroyed) return;

                try
                {
                    var ai = ent.GetComponent<PatrolHelicopterAI>();
                    if (ai != null)
                    {
                        if (_config.Debug) Puts("[TTL] Retire heli netid=" + id + " after " + _config.TTLMinutes + "m");
                        ai.Retire();
                        return;
                    }
                }
                catch (Exception e)
                {
                    PrintWarning("Retire() failed: " + e.Message);
                }

                ent.Kill();
            });
            if (_config.Debug) Puts("[TTL] Scheduled " + ent.ShortPrefabName + " netid=" + id + " TTL=" + _config.TTLMinutes + "m");
        }

        private void OnEntityKill(BaseNetworkable net)
        {
            var ent = net as BaseEntity;
            if (ent == null) return;
            if (ent.ShortPrefabName != "patrolhelicopter") return;

            _lastDeathUnix = Now();

            var id = (ent.net != null && ent.net.ID != null) ? ent.net.ID.Value : 0UL;
            Timer t;
            if (id != 0 && _ttlTimers.TryGetValue(id, out t))
            {
                if (t != null) t.Destroy();
                _ttlTimers.Remove(id);
            }
            if (_config.Debug) Puts("[Death] Heli netid=" + id + ", cooldown " + _config.CooldownMinutesOnDeath + "m");
        }

        #endregion

        #region Helpers

        private float Now() { return (float)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }

        private string PickTier()
        {
            var pool = (_config.AllowedTiers != null && _config.AllowedTiers.Count > 0) ? _config.AllowedTiers : _cachedTiers;
            if (pool == null || pool.Count == 0) return _config.FallbackTier;

            // apply no-repeat filter
            var filtered = new List<string>();
            foreach (var t in pool)
            {
                if (_config.Selection.NoRepeatLast <= 0 || !_lastPicked.Contains(t))
                    filtered.Add(t);
            }
            if (filtered.Count == 0) filtered = pool; // fallback if all filtered

            if (_config.Selection.Mode == "weighted" && _config.Selection.Weights != null && _config.Selection.Weights.Count > 0)
            {
                // build cumulative weights
                var items = new List<Tuple<string,int>>();
                int total = 0;
                foreach (var t in filtered)
                {
                    int w = 0;
                    if (!_config.Selection.Weights.TryGetValue(t, out w)) w = 1;
                    if (w < 0) w = 0;
                    total += w;
                    items.Add(new Tuple<string,int>(t, w));
                }
                if (total <= 0) return filtered[UnityEngine.Random.Range(0, filtered.Count)];

                int r = UnityEngine.Random.Range(0, total);
                int acc = 0;
                foreach (var it in items)
                {
                    acc += it.Item2;
                    if (r < acc) return it.Item1;
                }
                return items[items.Count - 1].Item1;
            }
            else
            {
                int idx = UnityEngine.Random.Range(0, filtered.Count);
                return filtered[idx];
            }
        }

        private void RefreshTierCache()
        {
            _cachedTiers = new List<string>();
            var ids = ReadTierIdsFromHeliTiersJson();
            if (ids != null && ids.Count > 0) _cachedTiers = ids.Distinct().ToList();
        }

        private List<string> ReadTierIdsFromHeliTiersJson()
        {
            try
            {
                string path = Path.Combine(Interface.Oxide.ConfigDirectory, _config.HeliTiersConfigFileName);
                if (!File.Exists(path))
                {
                    if (_config.Debug) Puts("[Debug] HeliTiers config not found at: " + path);
                    return new List<string>();
                }

                string json = File.ReadAllText(path);
                var list = new List<string>();

                foreach (Match m in Regex.Matches(json, "\"Id\"\\s*:\\s*\"([^\"]+)\""))
                {
                    var id = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(id)) list.Add(id);
                }

                if (list.Count == 0)
                {
                    Match ts = Regex.Match(json, "\"TierSettings\"\\s*:\\s*\\{([\\s\\S]*?)\\}");
                    if (ts.Success)
                    {
                        string inner = ts.Groups[1].Value;
                        foreach (Match k in Regex.Matches(inner, "\"([^\"]+)\"\\s*:\\s*\\{"))
                        {
                            var id = k.Groups[1].Value.Trim();
                            if (!string.IsNullOrEmpty(id) && id != "TierSettings") list.Add(id);
                        }
                    }
                }

                return list.Distinct().ToList();
            }
            catch (Exception e)
            {
                PrintWarning("Failed to read tiers from HT config: " + e.Message);
                return new List<string>();
            }
        }

        #endregion
    }
}
