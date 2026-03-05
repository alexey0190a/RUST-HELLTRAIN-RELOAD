using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Preload", "BLOODHELL", "1.1.2")]
    [Description("Standalone image/icon preloader for ImageLibrary (for kits/UI). Supports auto-scan of oxide/data.")]
    public class Preload : CovalencePlugin
    {
        private const string Perm = "preloadpng.admin";

        [PluginReference] private Plugin ImageLibrary;


        #region Configuration

        private ConfigData _config;

        private class ConfigData
        {
            public string Version = "1.1.2";

            // Auto-run after server starts
            public bool AutoPreloadOnServerInitialized = true;

            // Auto-run on each player connection (1 player = 1 warmup trigger)
            public bool AutoPreloadOnPlayerConnected = true;

            // Also scan oxide/data for images (PNG/JPG) and register them with keys by filename
            public bool AutoScanDataDirectory = true;

            // Optional subfolder within oxide/data to scan (leave empty to scan whole data dir)
            public string DataSubfolder = "";

            // Delay between items to avoid spikes
            public float BatchDelaySeconds = 0.10f;

            // Log progress to console
            public bool LogProgress = true;

            // Also collect shortnames from currently connected players' inventories when running a preload
            public bool IncludeConnectedPlayersInventories = false;

            // Known item shortnames to preload (for kits/UI)
            public List<string> ItemShortnames = new List<string>
            {
                // Example: "rifle.ak", "metal.plate.torso", "syringe.medical"
            };

            // Arbitrary image URLs to preload: key -> url (useful for custom buttons/backgrounds)
            public Dictionary<string, string> ImageUrls = new Dictionary<string, string>
            {
                // { "bloodhell_bg", "https://example.com/bg.png" }
            };
        }

        protected override void LoadDefaultConfig() => _config = new ConfigData();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null || string.IsNullOrEmpty(_config.Version))
                    throw new Exception("Invalid config");
            }
            catch
            {
                PrintWarning("Config file is invalid or corrupt; creating a new one.");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(Perm, this);
        }

        private void OnServerInitialized()
        {
            if (_config.AutoPreloadOnServerInitialized)
            {
                timer.Once(5f, () =>
                {
                    Puts("[Preload] Auto-preload starting...");
                    StartPreload(null, includePlayers: _config.IncludeConnectedPlayersInventories, doScan: _config.AutoScanDataDirectory);
                });
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!_config.AutoPreloadOnPlayerConnected) return;
            Puts($"[Preload] Player-connect fast warmup starting for {player?.UserIDString}...");
            StartPreload(null, includePlayers: false, doScan: _config.AutoScanDataDirectory);
        }

        #endregion

        #region Chat / Console Commands

        [Command("preloadpng")]
        private void CmdPreload(IPlayer player, string cmd, string[] args)
        {
            if (player?.IsServer == false && !player.HasPermission(Perm))
            {
                player?.Reply("[Preload] Недостаточно прав (preloadpng.admin).");
                return;
            }

            bool includePlayers = false;
            bool doScan = false;

            if (args != null && args.Length > 0)
            {
                includePlayers = args.Any(a => a.Equals("players", StringComparison.OrdinalIgnoreCase));
                doScan        = args.Any(a => a.Equals("scan", StringComparison.OrdinalIgnoreCase));
            }

            player?.Reply($"[Preload] Старт прогрева. Источники: конфиг{(doScan ? " + oxide/data" : "")}{(includePlayers ? " + инвентари игроков" : "")}.");
            StartPreload(player, includePlayers, doScan);
        }

        #endregion

        #region Core

        private void StartPreload(IPlayer caller, bool includePlayers, bool doScan)
        {
            if (ImageLibrary == null)
            {
                Reply(caller, "[Preload] ImageLibrary не найден. Установите плагин ImageLibrary и перезапустите.");
                return;
            }

            var queue = new List<string>();
            var uniq = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // From config shortnames
            foreach (var sn in _config.ItemShortnames)
            {
                if (string.IsNullOrEmpty(sn)) continue;
                if (uniq.Add(sn)) queue.Add(sn);
            }

            // From connected players inventories
            if (includePlayers)
            {
                foreach (var basePlayer in BasePlayer.activePlayerList)
                {
                    if (basePlayer == null) continue;
                    AddFromContainer(basePlayer.inventory?.containerMain, uniq, queue);
                    AddFromContainer(basePlayer.inventory?.containerBelt, uniq, queue);
                    AddFromContainer(basePlayer.inventory?.containerWear, uniq, queue);
                }
            }

            // From config URLs
            foreach (var kvp in _config.ImageUrls)
            {
                var key = kvp.Key;
                var url = kvp.Value;
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url)) continue;
                TryAddImageUrl(key, url);
                if (uniq.Add(key)) queue.Add(key);
            }

            // Scan oxide/data for png/jpg
            if (doScan)
            {
                int added = ScanDataDirectory(uniq, queue);
                if (_config.LogProgress) Puts($"[Preload] Data scan queued {added} file images.");
            }

            if (queue.Count == 0)
            {
                Reply(caller, "[Preload] Нет элементов для прогрева.");
                return;
            }

            Reply(caller, $"[Preload] В очередь поставлено {queue.Count} элементов. Начинаю прогрев...");
            ProcessQueue(caller, queue, 0, 0, 0);
        }

        private int ScanDataDirectory(HashSet<string> uniq, List<string> queue)
        {
            var dataRoot = Interface.Oxide.DataDirectory; // absolute path to oxide/data
            if (!string.IsNullOrEmpty(_config.DataSubfolder))
                dataRoot = Path.Combine(dataRoot, _config.DataSubfolder);

            if (!Directory.Exists(dataRoot))
            {
                PrintWarning($"[Preload] Data directory not found: {dataRoot}");
                return 0;
            }

            int added = 0;
            var files = Directory.GetFiles(dataRoot, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var filePath in files)
            {
                try
                {
                    // Key = filename without extension
                    var key = Path.GetFileNameWithoutExtension(filePath);
                    if (string.IsNullOrEmpty(key)) continue;

                    // Build file:// URL with forward slashes
                    var norm = filePath.Replace('\\', '/');
                    var url = $"file://{norm}";

                    TryAddImageUrl(key, url);

                    if (uniq.Add(key))
                    {
                        queue.Add(key);
                        added++;
                    }
                }
                catch (Exception e)
                {
                    PrintWarning($"[Preload] Scan error for '{filePath}': {e.Message}");
                }
            }
            return added;
        }

        private void TryAddImageUrl(string key, string url)
        {
            try
            {
                // Register or ensure exists in ImageLibrary
                ImageLibrary.Call("AddImage", url, key);
            }
            catch (Exception e)
            {
                PrintWarning($"[Preload] AddImage failed for '{key}' ({url}): {e.Message}");
            }
        }

        private void AddFromContainer(ItemContainer c, HashSet<string> uniq, List<string> queue)
        {
            if (c?.itemList == null) return;
            foreach (var it in c.itemList)
            {
                if (it?.info == null) continue;
                var sn = it.info.shortname;
                if (string.IsNullOrEmpty(sn)) continue;
                if (uniq.Add(sn)) queue.Add(sn);
            }
        }

        private void ProcessQueue(IPlayer caller, List<string> queue, int index, int ok, int fail)
        {
            if (index >= queue.Count)
            {
                var msg = $"[Preload] Готово: {ok} ок, {fail} фейл (из {queue.Count}).";
                Reply(caller, msg);
                if (_config.LogProgress) Puts(msg);
                return;
            }

            string key = queue[index];
            bool success = false;

            try
            {
                // Ensure image exists in cache by key (shortname or custom key)
                var res = ImageLibrary.Call("GetImage", key);
                if (IsValidImageId(res)) success = true;
                else success = res != null;
            }
            catch (Exception e)
            {
                if (_config.LogProgress) PrintWarning($"[Preload] GetImage failed for '{key}': {e.Message}");
            }

            if (success) ok++; else fail++;
            if (_config.LogProgress && (index % 25 == 0 || index == queue.Count - 1))
            {
                Puts($"[Preload] Progress {index + 1}/{queue.Count} (ok:{ok} fail:{fail})");
            }

            timer.Once(Mathf.Max(0.01f, _config.BatchDelaySeconds), () =>
                ProcessQueue(caller, queue, index + 1, ok, fail));
        }

        private bool IsValidImageId(object obj)
        {
            if (obj == null) return false;
            if (obj is string s) return !string.IsNullOrEmpty(s);
            if (obj is ulong u) return u != 0;
            return false;
        }

        private void Reply(IPlayer player, string msg)
        {
            if (player == null) { if (_config.LogProgress) Puts(msg); return; }
            player.Reply(msg);
        }

        #endregion
    }
}
