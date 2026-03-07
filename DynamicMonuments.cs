using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Plugins.DynamicMonumentsExtensionMethods;
using Newtonsoft.Json;
using UnityEngine;
using System.IO;
using Oxide.Core.Plugins;
using Newtonsoft.Json.Linq;
using Facepunch;
using Rust.Ai.Gen2;
using UnityEngine.AI;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info("DynamicMonuments", "Adem", "1.1.7")]
    internal class DynamicMonuments : RustPlugin
    {
        #region Variables
        private static DynamicMonuments _ins;
        private const bool En = false;

        readonly HashSet<string> _subscribeMethods = new HashSet<string>
        {
            "OnButtonPress",
            "CanPickupEntity",
            "OnCardSwipe",
            "OnPlayerSleep",
            "OnActiveItemChanged",
            "OnExplosiveThrown",
            "OnExplosiveDropped",
            "CanLootEntity",
            "OnCorpsePopulate",
            "OnEntitySpawned",
            "OnPlayerViolation",
            "CanUpdateSign",
            "OnBackpackDrop",

            "CanPopulateLoot",
            "OnContainerPopulate",
        };
        [PluginReference] Plugin NpcSpawn, AlphaLoot;

        private readonly HashSet<CustomMonument> _monuments = new HashSet<CustomMonument>();
        private Coroutine _spawnCoroutine;
        private Coroutine _loadCoroutine;
        private readonly HashSet<PlayerFlareInfo> _playersWithFlare = new HashSet<PlayerFlareInfo>();
        private Coroutine _playerMonumentSpawnUpdateCoroutine;
        private readonly HashSet<CardDoor> _cardDoors = new HashSet<CardDoor>();
        private readonly HashSet<ZoneController> _monumentZones = new HashSet<ZoneController>();
        private readonly HashSet<LootPrefabController> _lootPrefabData = new HashSet<LootPrefabController>();
        private readonly HashSet<CustomElevator> _elevators = new HashSet<CustomElevator>();
        private readonly Dictionary<string, uint> _images = new Dictionary<string, uint>();
        private ProtectionProperties _protection;
        #endregion Variables

        #region Hooks
        private void Init()
        {
            Unsubscribes();
        }

        private void OnServerInitialized()
        {
            _ins = this;
            if (!NpcSpawnManager.IsNpcSpawnReady())
                return;

            UpdateConfig();
            LoadDefaultMessages();

            if (!TryLoadData())
            {
                NotifyManagerLite.PrintError(null, "DataNotFound_Exception");
                NextTick(() => Interface.Oxide.UnloadPlugin(Name));
                return;
            }

            LootManager.InitialLootManagerUpdate();
            _protection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _protection.Add(1);

            PermissionManager.RegisterPermissions();
            SiteSpawner.Switch(true);

            if (_ins._config.MainConfig.IsRespawnLocation || _savedMonuments == null || _savedMonuments.Count == 0)
                MonumentAutoSpawner.StartAutoSpawn();
            else
                MonumentAutoSpawner.LoadMonuments();

            PlayerMonumentSpawner.StartUpdate();
            Subscribes();
        }

        private void Unload()
        {
            PlayerMonumentSpawner.StopUpdate();
            CustomMonument.KillAllMonuments();
            MonumentAutoSpawner.StopAutoSpawn();
            SiteSpawner.Switch(false);
            _ins = null;
        }

        private void OnButtonPress(PressButton button, BasePlayer player)
        {
            if (player == null || button == null || button.net == null)
                return;

            CardDoor cardDoor = CardDoor.GetCardDoorByButtonNetId(button.net.ID.Value);
            if (cardDoor != null)
            {
                cardDoor.OpenDoor();
                return;
            }

            CustomElevator elevator = CustomElevator.GetElevatorByButton(button.net.ID.Value);
            if (elevator != null)
                elevator.OnButtonPressed(button.net.ID.Value);
        }

        private object CanPickupEntity(BasePlayer player, PressButton button)
        {
            if (button == null || button.net == null)
                return null;

            CardDoor cardDoor = CardDoor.GetCardDoorByButtonNetId(button.net.ID.Value);
            if (cardDoor == null)
                return null;

            return false;
        }

        private void OnCardSwipe(CardReader cardReader, Keycard card, BasePlayer player)
        {
            if (player == null || cardReader == null || cardReader.net == null)
                return;

            CardDoor cardDoor = CardDoor.GetCardDoorByReaderNetId(cardReader.net.ID.Value);
            if (cardDoor == null)
                return;

            cardReader.Invoke(() =>
            {
                if (cardReader.HasFlag(BaseEntity.Flags.On))
                    cardDoor.OpenDoor();
            }, 1.1f);
        }

        private void OnPlayerSleep(BasePlayer player)
        {
            if (!player.IsRealPlayer())
                return;

            ZoneController monumentZone = ZoneController.GetMonumentZoneByPlayer(player.userID);
            if (monumentZone != null)
                monumentZone.OnPlayerLeaveZone(player);
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (!player.IsRealPlayer() || newItem == null)
                return;

            if (newItem.info.shortname == "flare")
            {
                SiteSpawnInfo monumentSpawnInfo = SiteSpawnInfo.GetSiteInfo(newItem.skin);
                if (monumentSpawnInfo == null)
                    return;

                PlayerMonumentSpawner.AddPlayer(player, monumentSpawnInfo);
                NotifyManagerLite.SendMessageToPlayer(player, monumentSpawnInfo.SiteConfig.SummonConfig.SpawnDescriptionLang, _config.Prefix);
            }
        }

        private void OnExplosiveThrown(BasePlayer player, RoadFlare roadFlare, ThrownWeapon thrownWeapon)
        {
            OnPlayerDropFlare(player, roadFlare, thrownWeapon);
        }

        private void OnExplosiveDropped(BasePlayer player, RoadFlare roadFlare, ThrownWeapon thrownWeapon)
        {
            OnPlayerDropFlare(player, roadFlare, thrownWeapon);
        }

        private void OnPlayerDropFlare(BasePlayer player, RoadFlare roadFlare, ThrownWeapon thrownWeapon)
        {
            if (!player.IsRealPlayer() || roadFlare == null || thrownWeapon == null)
                return;

            Item item = thrownWeapon.GetItem();
            if (item == null)
                return;

            SiteSpawnInfo monumentSpawnInfo = SiteSpawnInfo.GetSiteInfo(item.skin);
            if (monumentSpawnInfo == null)
                return;

            SiteSpawnFlare.Attach(roadFlare, player, monumentSpawnInfo);
        }

        private object CanLootEntity(BasePlayer player, LootContainer lootContainer)
        {
            if (player == null || lootContainer == null || lootContainer.net == null)
                return null;

            CustomMonument customMonument = CustomMonument.GetMonumentByEntity(lootContainer);
            if (customMonument == null)
                return null;

            if (!customMonument.IsPlayerCanLoot(player))
                return false;

            return null;
        }

        private object OnEntityTakeDamage(LootContainer lootContainer, HitInfo info)
        {
            if (lootContainer == null || info == null || info.InitiatorPlayer == null)
                return null;

            if (lootContainer.ShortPrefabName.Contains("barrel"))
            {
                CustomMonument customMonument = CustomMonument.GetMonumentByEntity(lootContainer);
                if (customMonument == null)
                    return null;

                if (!customMonument.IsPlayerCanLoot(info.InitiatorPlayer))
                    return true;
            }

            return null;
        }

        private object OnEntityTakeDamage(VineSwingingTree vineSwingingTree, HitInfo info)
        {
            if (vineSwingingTree == null || info == null || info.InitiatorPlayer == null)
                return null;

            CustomMonument customMonument = CustomMonument.GetMonumentByEntity(vineSwingingTree);
            if (customMonument == null)
                return null;

            info.CanGather = false;
            return true;
        }

        private void OnCorpsePopulate(ScientistNPC scientistNpc, NPCPlayerCorpse corpse)
        {
            if (scientistNpc == null || corpse == null)
                return;

            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(scientistNpc.displayName);
            if (npcConfig == null)
                return;

            _ins.NextTick(() =>
            {
                if (corpse == null)
                    return;

                if (!corpse.containers.IsNullOrEmpty() && corpse.containers[0] != null)
                    LootManager.UpdateItemContainer(corpse.containers[0], npcConfig.LootTableConfig, npcConfig.LootTableConfig.ClearDefaultItemList);

                if (npcConfig.DeleteCorpse && !corpse.IsDestroyed)
                    corpse.Kill();
            });
        }

        private void OnEntitySpawned(BaseLock baseLock)
        {
            if (baseLock == null)
                return;

            Door parentDoor = baseLock.GetParentEntity() as Door;
            if (parentDoor == null)
                return;

            CustomMonument customMonument = CustomMonument.GetMonumentByEntity(parentDoor);
            if (customMonument != null)
                baseLock.Kill();
        }

        private object OnPlayerViolation(BasePlayer player, AntiHackType type, float amount)
        {
            if (player == null)
                return null;

            if (type == AntiHackType.FlyHack)
            {
                ZoneController monumentZone = ZoneController.GetMonumentZoneByPlayer(player.userID);
                if (monumentZone != null)
                    return true;
            }

            return null;
        }
        
        private object OnBackpackDrop(Item backpack, ScientistNPC scientistNpc)
        {
            if (scientistNpc == null)
                return null;
            
            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(scientistNpc.displayName);
            if (npcConfig != null)
                return true;    
                
            return null;
        }
        #region OtherPlugins
        private object CanPopulateLoot(LootContainer lootContainer)
        {
            if (lootContainer == null || lootContainer.net == null)
                return null;

            LootContainerData storageContainerData = CustomMonument.GetLootContainerData(lootContainer.net.ID.Value);

            if (storageContainerData != null)
            {
                CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(storageContainerData.PresetName);

                if (crateConfig != null && !crateConfig.LootTableConfig.IsAlphaLoot)
                    return true;
            }

            return null;
        }

        private object CanPopulateLoot(ScientistNPC scientistNpc, NPCPlayerCorpse corpse)
        {
            if (scientistNpc == null || scientistNpc.net == null)
                return null;

            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(scientistNpc.displayName);

            if (npcConfig != null && !npcConfig.LootTableConfig.IsAlphaLoot)
                return true;

            return null;
        }

        private object OnContainerPopulate(LootContainer lootContainer)
        {
            if (lootContainer == null || lootContainer.net == null)
                return null;

            LootContainerData lootContainerData = CustomMonument.GetLootContainerData(lootContainer.net.ID.Value);

            if (lootContainerData != null)
            {
                CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(lootContainerData.PresetName);

                if (crateConfig != null && !crateConfig.LootTableConfig.IsLootTablePLugin)
                    return true;
            }

            return null;
        }

        private object OnCorpsePopulate(NPCPlayerCorpse corpse)
        {
            if (corpse == null)
                return null;

            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(corpse.playerName);

            if (npcConfig != null && !npcConfig.LootTableConfig.IsLootTablePLugin)
                return true;

            return null;
        }
        
        private object OnCustomLootContainer(NetworkableId netID)
        {
            LootContainerData lootContainerData = CustomMonument.GetLootContainerData(netID.Value);
            if (lootContainerData == null) 
                return null;
            
            CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(lootContainerData.PresetName);
            if (crateConfig != null && !crateConfig.LootTableConfig.IsCustomLoot)
                return true;

            return null;
        }
        #endregion OtherPlugins
        #endregion Hooks

        #region Methods
        private void UpdateConfig()
        {
            if (_config.Version != Version)
            {
                PluginConfig defaultConfig = PluginConfig.DefaultConfig();

                if (_config.Version.Minor == 0)
                {
                    if (_config.Version.Patch <= 1)
                    {
                        _config.MainConfig.IsSpawnLogging = true;
                    }

                    if (_config.Version.Patch <= 4)
                    {
                        foreach (NpcConfig npcConfig in _config.NpcConfigs)
                        {
                            if (npcConfig.LootTableConfig.ClearDefaultItemList && !npcConfig.LootTableConfig.IsRandomItemsEnable && !npcConfig.LootTableConfig.PrefabConfigs.IsEnable)
                            {
                                NpcConfig defaultNpcConfig = defaultConfig.NpcConfigs.FirstOrDefault(x => x.PresetName == npcConfig.PresetName);
                                if (defaultNpcConfig == null)
                                    continue;

                                npcConfig.LootTableConfig = defaultNpcConfig.LootTableConfig;
                            }
                        }
                    }

                    if (_config.Version.Patch <= 7)
                    {
                        if (_config.MarkerConfig == null)
                            _config.MarkerConfig = defaultConfig.MarkerConfig;
                    }

                    if (_config.Version.Patch <= 8)
                    {
                        SiteConfig siteConfig = _config.WaterTypeConfig.Sites.FirstOrDefault(x => x.PresetName == "oilPlatform");
                        if (siteConfig != null)
                        {
                            siteConfig.CustomNavmeshNpc.RemoveWhere(x => x.Position == "(-12.071, 34.669, 21.606)");
                        }

                        _config.GroundTypeConfig = defaultConfig.GroundTypeConfig;

                    }

                    _config.Version = new VersionNumber(1, 1, 0);
                }
                if (_config.Version.Minor == 1)
                {
                    if (_config.Version.Patch <= 2)
                        _isAddonJustInstalled = true;
                    if (_config.Version.Patch <= 3)
                    {
                        _isAddonJustInstalled = true;
                        HashSet<string> changedPrefabs = new HashSet<string>
                        {
                            "PowerlinePlatform_A",
                            "mining_quarry_b",
                            "mining_quarry_c",
                            "power_sub_big_1",
                            "power_sub_big_2",
                            "entrance_bunker_a",
                            "coastal_rocks_large_a"
                        };
                        foreach (SiteConfig siteConfig in _config.PrefabTypeConfig.Sites)
                        {
                            if (changedPrefabs.Any(x => siteConfig.PrefabNames.Contains(x)))
                            {
                                SiteConfig defaultSite = defaultConfig.PrefabTypeConfig.Sites.FirstOrDefault(x => x.PresetName == siteConfig.PresetName);
                                if (defaultSite == null)
                                    continue;

                                siteConfig.PrefabNames = defaultSite.PrefabNames;
                            }
                        }

                        foreach (SiteConfig siteConfig in _config.GroundTypeConfig.Sites)
                            siteConfig.EnableMarker = _config.MarkerConfig.UseRingMarker || _config.MarkerConfig.UseShopMarker;

                        foreach (SiteConfig siteConfig in _config.CoastalTypeConfig.Sites)
                            siteConfig.EnableMarker = _config.MarkerConfig.UseRingMarker || _config.MarkerConfig.UseShopMarker;

                        foreach (SiteConfig siteConfig in _config.PrefabTypeConfig.Sites)
                            siteConfig.EnableMarker = _config.MarkerConfig.UseRingMarker || _config.MarkerConfig.UseShopMarker;

                        foreach (SiteConfig siteConfig in _config.WaterTypeConfig.Sites)
                            siteConfig.EnableMarker = _config.MarkerConfig.UseRingMarker || _config.MarkerConfig.UseShopMarker;

                        foreach (SiteConfig siteConfig in _config.RiverTypeConfig.Sites)
                            siteConfig.EnableMarker = _config.MarkerConfig.UseRingMarker || _config.MarkerConfig.UseShopMarker;

                        foreach (SiteConfig siteConfig in _config.RoadTypeConfig.Sites)
                            siteConfig.EnableMarker = _config.MarkerConfig.UseRingMarker || _config.MarkerConfig.UseShopMarker;
                    }

                    if (_config.Version.Patch <= 5)
                    {
                        foreach (NpcConfig npcConfig in _config.NpcConfigs)
                        {
                            npcConfig.CanSleep = true;
                            npcConfig.SleepDistance = 100f;
                        }
                    }

                    if (_config.Version.Patch <= 6)
                    {
                        foreach (NpcConfig npcConfig in _config.NpcConfigs)
                        {
                            if (npcConfig.SleepDistance == 0)
                                npcConfig.SleepDistance = 100f;
                        }

                        foreach (SiteConfig siteConfig in _config.RoadTypeConfig.Sites)
                        {
                            if (siteConfig.PresetName == "rustedGates")
                            {
                                SiteConfig defaultSiteConfig = defaultConfig.RoadTypeConfig.Sites.FirstOrDefault(x => x.PresetName == siteConfig.PresetName);
                                siteConfig.GroudNpcs = defaultSiteConfig.GroudNpcs;
                            }
                        }
                        
                        foreach (SiteConfig siteConfig in _config.RiverTypeConfig.Sites)
                        {
                            if (siteConfig.PresetName == "rustedDam")
                            {
                                SiteConfig defaultSiteConfig = defaultConfig.RiverTypeConfig.Sites.FirstOrDefault(x => x.PresetName == siteConfig.PresetName);
                                siteConfig.GroudNpcs = defaultSiteConfig.GroudNpcs;
                            }
                        }
                        
                        foreach (SiteConfig siteConfig in _config.PrefabTypeConfig.Sites)
                        {
                            if (siteConfig.PresetName is "depotGates_1" or "depotGates_2" or "stoneQuarryRuins" or "hqmQuarryRuins")
                            {
                                SiteConfig defaultSiteConfig = defaultConfig.PrefabTypeConfig.Sites.FirstOrDefault(x => x.PresetName == siteConfig.PresetName);
                                siteConfig.GroudNpcs = defaultSiteConfig.GroudNpcs;
                            }
                        }

                        NpcConfig hqmQuarryNpc = defaultConfig.NpcConfigs.FirstOrDefault(x => x.PresetName == "hqmQuarryNpc");
                        if (hqmQuarryNpc != null)
                            _config.NpcConfigs.Add(hqmQuarryNpc);
                        
                        NpcConfig stoneQuarryNpc = defaultConfig.NpcConfigs.FirstOrDefault(x => x.PresetName == "stoneQuarryNpc");
                        if (stoneQuarryNpc != null)
                            _config.NpcConfigs.Add(stoneQuarryNpc);
                    }
                }

                _config.Version = Version;
                SaveConfig();
            }
        }

        private void Unsubscribes()
        {
            foreach (string hook in _subscribeMethods)
                Unsubscribe(hook);
        }

        private void Subscribes()
        {
            foreach (string hook in _subscribeMethods)
                Subscribe(hook);
        }

        private static void Debug(params object[] arg)
        {
            string result = "";

            foreach (object obj in arg)
                if (obj != null)
                    result += obj.ToString() + " ";

            _ins.Puts(result);
        }

        private static bool IsTeam(BasePlayer player, ulong targetId)
        {
            if (player.userID == targetId)
                return true;

            if (player.currentTeam != 0)
            {
                RelationshipManager.PlayerTeam playerTeam = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);

                if (playerTeam == null)
                    return false;

                if (playerTeam.members.Contains(targetId))
                    return true;
            }
            return false;
        }

        private bool IsWipe()
        {
            return _mapSaveData == null || _mapSaveData.MapName != World.SaveFileName;
        }
        #endregion Methods 

        #region Commands
        [ChatCommand("spawnmonumentmypos")]
        private void SpawnMonumentPlayerPosChatCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            string presetName = arg[0];
            SiteSpawnInfo spawningMonumentInfo = SiteSpawnInfo.GetSiteInfo(presetName);
            if (spawningMonumentInfo == null)
            {
                NotifyManagerLite.PrintError(player, "ConfigNotFound_Exeption", presetName);
                return;
            }

            Quaternion rotation = Quaternion.Euler(0, player.viewAngles.y, 0);
            SiteSpawner.SpawnSite(spawningMonumentInfo, player.transform.position, rotation, false);
        }

        [ChatCommand("killmonument")]
        private void KillMonumentCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            BaseEntity target = PositionDefiner.RaycastAll<BaseEntity>(player.eyes.HeadRay());
            if (target == null)
                return;

            CustomMonument customMonument = CustomMonument.GetMonumentByEntity(target);
            if (customMonument == null)
                return;

            customMonument.KillMonument();
            CustomMonument.SaveMonuments();
        }

        [ChatCommand("replacecrate")]
        private void ReplaceCrateCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin || arg.Length < 1)
                return;

            CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(arg[0]);
            if (crateConfig == null)
                return;

            LootContainer lootContainer = PositionDefiner.RaycastAll<BaseEntity>(player.eyes.HeadRay()) as LootContainer;
            if (lootContainer == null)
                return;

            CustomMonument customMonument = CustomMonument.GetMonumentByEntity(lootContainer);
            if (customMonument == null)
                return;

            customMonument.ReplaceCrate(lootContainer, arg[0], player);
        }

        [ChatCommand("addprefabspawnpoint")]
        private void AddSpawnPointCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin || arg.Length < 1)
                return;

            CustomMonument customMonument = CustomMonument.GetClosestCustomMonument(player.transform.position, out float distance);
            if (customMonument == null || distance > customMonument.MonumentData.ExternalRadius)
            {
                NotifyManagerLite.PrintError(player, "MonumentNotFound_Exception", _config.Prefix);
                return;
            }

            string presetName = arg[0];
            CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(presetName);
            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByPresetName(presetName);

            if (crateConfig == null && npcConfig == null)
            {
                NotifyManagerLite.PrintError(player, "PresetNotFound_Exeption", _config.Prefix, presetName);
                return;
            }

            Quaternion rotation = Quaternion.Euler(0, player.viewAngles.y, 0);
            customMonument.AddNewSpawnPoint(player.transform.position, rotation, presetName);
            NotifyManagerLite.PrintError(player, "SpawnPosAdded");
        }

        [ChatCommand("addgroundnpcspawnpoint")]
        private void AddGroundNpcSpawnPointCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin || arg.Length < 1)
                return;

            CustomMonument customMonument = CustomMonument.GetClosestCustomMonument(player.transform.position, out float distance);
            if (customMonument == null || distance > customMonument.MonumentData.ExternalRadius)
            {
                NotifyManagerLite.PrintError(player, "MonumentNotFound_Exception", _config.Prefix);
                return;
            }
            
            string presetName = arg[0];
            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByPresetName(presetName);
            
            if (npcConfig == null)
            {
                NotifyManagerLite.PrintError(player, "PresetNotFound_Exeption", _config.Prefix, presetName);
                return;
            }
            customMonument.AddGroundNpcSpawnPoint(player.transform.position, presetName);
            NotifyManagerLite.PrintError(player, "SpawnPosAdded");
        }

        [ChatCommand("removemonumententity")]
        private void RemoveObjectCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            BaseEntity entity = PositionDefiner.RaycastAll<BaseEntity>(player.eyes.HeadRay());
            if (entity == null || entity.net == null)
            {
                NotifyManagerLite.PrintError(player, "EntityDelete_Exception", _config.Prefix);
                return;
            }

            CustomMonument customMonument = CustomMonument.GetMonumentByEntity(entity);
            if (customMonument == null)
            {
                NotifyManagerLite.PrintError(player, "MonumentNotFound_Exception", _config.Prefix);
                return;
            }

            if (!customMonument.TryRemoveEntity(entity))
                NotifyManagerLite.PrintError(player, "EntityDelete_Exception", _config.Prefix);
        }

        [ChatCommand("spawnmonument")]
        private void SpawnMonumentChatCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin || arg.Length < 1)
                return;

            string presetName = arg[0];
            SiteSpawnInfo monumentSpawnInfo = SiteSpawnInfo.GetSiteInfo(presetName);
            if (monumentSpawnInfo == null)
            {
                NotifyManagerLite.PrintError(player, "ConfigNotFound_Exeption", presetName);
                return;
            }

            NotifyManagerLite.SendMessageToPlayer(player, "Spawn_Start");
            SiteSpawner.TrySpawnMonument(monumentSpawnInfo, player);
        }

        [ConsoleCommand("spawnmonument")]
        private void SpawnMonumentConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null || arg.Args.Length < 1)
                return;

            string presetName = arg.Args[0];
            SiteSpawnInfo monumentSpawnInfo = SiteSpawnInfo.GetSiteInfo(presetName);
            if (monumentSpawnInfo == null)
            {
                NotifyManagerLite.PrintError(null, "ConfigNotFound_Exeption", presetName);
                return;
            }

            NotifyManagerLite.PrintLogMessage("Spawn_Start");
            SiteSpawner.TrySpawnMonument(monumentSpawnInfo);
        }

        [ChatCommand("killallmonuments")]
        private void KillAllMonumentsChatCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            CustomMonument.KillAllMonuments();
            CustomMonument.SaveMonuments();
        }

        [ConsoleCommand("killallmonuments")]
        private void KillAllMonumentsConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
                return;

            CustomMonument.KillAllMonuments();
            CustomMonument.SaveMonuments();
        }

        [ChatCommand("respawnmonuments")]
        private void RespawnMonumentsChatCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            MonumentAutoSpawner.StartAutoSpawn();
        }

        [ConsoleCommand("respawnmonuments")]
        private void RespawnMonumentsConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
                return;

            MonumentAutoSpawner.StartAutoSpawn();
        }

        [ChatCommand("givemonument")]
        private void GiveMonumentChatCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin || arg.Length < 1)
                return;

            string presetName = arg[0];
            SiteConfig siteConfig = SiteSpawnInfo.GetSiteConfig(presetName, out LocationType _);
            if (siteConfig == null)
            {
                NotifyManagerLite.PrintError(player, "ConfigNotFound_Exeption", presetName);
                return;
            }

            LootManager.GiveItemToPLayer(player, siteConfig.SummonConfig, 1);
            NotifyManagerLite.SendMessageToPlayer(player, "GotMonument");
        }

        [ConsoleCommand("givemonument")]
        private void GiveMonumentConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null || arg.Args.Length < 2)
                return;

            string presetName = arg.Args[0];
            SiteConfig siteConfig = SiteSpawnInfo.GetSiteConfig(presetName, out LocationType _);
            if (siteConfig == null)
            {
                NotifyManagerLite.PrintError(null, "ConfigNotFound_Exeption", presetName);
                return;
            }

            ulong targetUserId = Convert.ToUInt64(arg.Args[1]);
            BasePlayer targetPlayer = BasePlayer.FindByID(targetUserId);
            if (targetPlayer == null)
            {
                NotifyManagerLite.PrintError(null, "PlayerNotFound_Exeption", arg.Args[1]);
                return;
            }

            LootManager.GiveItemToPLayer(targetPlayer, siteConfig.SummonConfig, 1);
            NotifyManagerLite.SendMessageToPlayer(targetPlayer, "GotMonument");
        }

        [ChatCommand("savemonument")]
        private void SaveMapCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            string locationName = arg[0];
            MapSaver.SaveMap(locationName);
        }
        #endregion Commands

        #region Classes
        private static class MonumentAutoSpawner
        {
            public static void StartAutoSpawn()
            {
                _ins._spawnCoroutine = ServerMgr.Instance.StartCoroutine(AutoSpawnCoroutine());
            }

            public static void StopAutoSpawn()
            {
                if (_ins._spawnCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_ins._spawnCoroutine);

                if (_ins._loadCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_ins._loadCoroutine);
            }

            public static bool IsRespawnFinish()
            {
                return _ins._spawnCoroutine == null && _ins._loadCoroutine == null;
            }

            private static SiteConfig GetRandomSiteConfig(IEnumerable<SiteConfig> siteConfigs)
            {
                float sumChance = 0;
                foreach (SiteConfig baseSiteConfig in siteConfigs)
                    if (baseSiteConfig.IsAutoSpawn)
                        sumChance += baseSiteConfig.Probability;

                float random = UnityEngine.Random.Range(0, sumChance);

                foreach (SiteConfig baseSiteConfig in siteConfigs)
                {
                    if (!baseSiteConfig.IsAutoSpawn)
                        continue;
                        
                    random -= baseSiteConfig.Probability;

                    if (random <= 0)
                        return baseSiteConfig;
                }

                return null;
            }

            private static IEnumerator AutoSpawnCoroutine()
            {
                while (!SiteSpawner.IsReady())
                    yield return CoroutineEx.waitForSeconds(1f);

                NotifyManagerLite.PrintWarningMessage("SpawnStart_Log");

                int groundSitesCount = UnityEngine.Random.Range(_ins._config.GroundTypeConfig.MinAmount, _ins._config.GroundTypeConfig.MaxAmount);
                if (_ins._config.GroundTypeConfig.IsAutoSpawn && _ins._config.GroundTypeConfig.Sites.Count > 0 && groundSitesCount > 0)
                {
                    for (int i = 0; i < groundSitesCount; i++)
                    {
                        SiteConfig siteConfig = GetRandomSiteConfig(_ins._config.GroundTypeConfig.Sites);
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                int waterSitesCount = UnityEngine.Random.Range(_ins._config.WaterTypeConfig.MinAmount, _ins._config.WaterTypeConfig.MaxAmount);
                if (_ins._config.WaterTypeConfig.IsAutoSpawn && _ins._config.WaterTypeConfig.Sites.Count > 0 && waterSitesCount > 0)
                {
                    for (int i = 0; i < waterSitesCount; i++)
                    {
                        SiteConfig siteConfig = GetRandomSiteConfig(_ins._config.WaterTypeConfig.Sites);
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                int prefabSitesCount = UnityEngine.Random.Range(_ins._config.PrefabTypeConfig.MinAmount, _ins._config.PrefabTypeConfig.MaxAmount);
                if (_ins._config.PrefabTypeConfig.IsAutoSpawn && _ins._config.PrefabTypeConfig.Sites.Count > 0 && prefabSitesCount > 0)
                {
                    for (int i = 0; i < prefabSitesCount; i++)
                    {
                        SiteConfig siteConfig = GetRandomSiteConfig(_ins._config.PrefabTypeConfig.Sites);
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                int shoreSitesCount = UnityEngine.Random.Range(_ins._config.CoastalTypeConfig.MinAmount, _ins._config.CoastalTypeConfig.MaxAmount);
                if (_ins._config.CoastalTypeConfig.IsAutoSpawn && _ins._config.CoastalTypeConfig.Sites.Count > 0 && shoreSitesCount > 0)
                {
                    for (int i = 0; i < shoreSitesCount; i++)
                    {
                        SiteConfig siteConfig = GetRandomSiteConfig(_ins._config.CoastalTypeConfig.Sites);
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                int riverSitesCount = UnityEngine.Random.Range(_ins._config.RiverTypeConfig.MinAmount, _ins._config.RiverTypeConfig.MaxAmount);
                if (_ins._config.RiverTypeConfig.IsAutoSpawn && _ins._config.RiverTypeConfig.Sites.Count > 0 && riverSitesCount > 0)
                {
                    for (int i = 0; i < riverSitesCount; i++)
                    {
                        SiteConfig siteConfig = GetRandomSiteConfig(_ins._config.RiverTypeConfig.Sites);
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                int roadSitesCount = UnityEngine.Random.Range(_ins._config.RoadTypeConfig.MinAmount, _ins._config.RoadTypeConfig.MaxAmount);
                if (_ins._config.RoadTypeConfig.IsAutoSpawn && _ins._config.RoadTypeConfig.Sites.Count > 0 && roadSitesCount > 0)
                {
                    for (int i = 0; i < roadSitesCount; i++)
                    {
                        SiteConfig siteConfig = GetRandomSiteConfig(_ins._config.RoadTypeConfig.Sites);
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                foreach (SiteConfig siteConfig in _ins._config.GroundTypeConfig.Sites)
                {
                    for (int i = 0; i < siteConfig.AdditionalCount; i++)
                    {
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                foreach (SiteConfig siteConfig in _ins._config.WaterTypeConfig.Sites)
                {
                    for (int i = 0; i < siteConfig.AdditionalCount; i++)
                    {
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                foreach (SiteConfig siteConfig in _ins._config.PrefabTypeConfig.Sites)
                {
                    for (int i = 0; i < siteConfig.AdditionalCount; i++)
                    {
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                foreach (SiteConfig siteConfig in _ins._config.CoastalTypeConfig.Sites)
                {
                    for (int i = 0; i < siteConfig.AdditionalCount; i++)
                    {
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                foreach (SiteConfig siteConfig in _ins._config.RiverTypeConfig.Sites)
                {
                    for (int i = 0; i < siteConfig.AdditionalCount; i++)
                    {
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                foreach (SiteConfig siteConfig in _ins._config.RoadTypeConfig.Sites)
                {
                    for (int i = 0; i < siteConfig.AdditionalCount; i++)
                    {
                        SiteSpawner.TrySpawnMonument(siteConfig.PresetName);
                        yield return CoroutineEx.waitForSeconds(5f);
                    }
                }

                CustomMonument.SaveMonuments();
                NotifyManagerLite.PrintWarningMessage("SpawnStop_Log");
                _ins._spawnCoroutine = null;
            }

            public static void LoadMonuments()
            {
                if (_ins._savedMonuments == null || _ins._savedMonuments.Count == 0)
                    return;

                _ins._loadCoroutine = ServerMgr.Instance.StartCoroutine(LoadCoroutine());
            }

            private static IEnumerator LoadCoroutine()
            {
                foreach (MonumentSaveData monumentSaveData in _ins._savedMonuments)
                {
                    SiteSpawnInfo spawnInfo = SiteSpawnInfo.GetSiteInfo(monumentSaveData.MonumentPreset);
                    if (spawnInfo == null)
                    {
                        NotifyManagerLite.PrintError(null, "ConfigNotFound_Exeption", monumentSaveData.MonumentPreset);
                        continue;
                    }

                    Vector3 position = monumentSaveData.Position.ToVector3();
                    Quaternion rotation = Quaternion.Euler(monumentSaveData.Rotation.ToVector3());
                    CustomMonument customMonument = SiteSpawner.SpawnSite(spawnInfo, position, rotation, false, true);

                    if (customMonument && monumentSaveData.OwnerId != 0)
                        customMonument.SetOwner(monumentSaveData.OwnerId);

                    yield return CoroutineEx.waitForSeconds(2f);
                }

                CustomMonument.SaveMonuments();
                _ins._loadCoroutine = null;
            }
        }

        private static class SiteSpawner
        {
            public static void Switch(bool isEnable)
            {
                if (isEnable)
                    PrefabSpawner.StartCaching();
                else
                    PrefabSpawner.ClearCache();
            }

            public static bool IsReady()
            {
                return PrefabSpawner.IsCachingEnd();
            }

            public static bool IsSpawnPositionSuitable(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
            {
                switch (spawningMonumentInfo.Type)
                {
                    case LocationType.Prefab:
                        return PrefabSpawner.IsPositionSuitable(position, ref spawningMonumentInfo);
                    case LocationType.Coastal:
                        return ShoreSpawner.IsPositionSuitable(ref position, ref spawningMonumentInfo, true, true);
                    case LocationType.River:
                        return RiverSpawner.IsPositionSuitable(position, ref spawningMonumentInfo);
                    case LocationType.Road:
                        return RoadSpawner.IsPositionSuitable(position, ref spawningMonumentInfo);
                    case LocationType.Water:
                        return WaterSpawner.IsPositionSuitable(position, ref spawningMonumentInfo, true, true);
                    case LocationType.Ground:
                        return GroundSpawner.IsPositionSuitable(ref position, ref spawningMonumentInfo, true, true);
                }

                return false;
            }

            public static void TrySpawnMonument(string presetName, BasePlayer initator = null)
            {
                SiteSpawnInfo spawningMonumentInfo = SiteSpawnInfo.GetSiteInfo(presetName);
                if (spawningMonumentInfo == null)
                {
                    NotifyManagerLite.PrintError(null, "ConfigNotFound_Exeption", presetName);
                    return;
                }

                TrySpawnMonument(spawningMonumentInfo, initator);
            }

            public static void TrySpawnMonument(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator = null)
            {
                switch (spawningMonumentInfo.Type)
                {
                    case LocationType.Prefab:
                        PrefabSpawner.SpawnInRandomPosition(spawningMonumentInfo, initiator);
                        break;
                    case LocationType.Coastal:
                        ShoreSpawner.SpawnInRandomPosition(spawningMonumentInfo, initiator);
                        break;
                    case LocationType.Water:
                        WaterSpawner.SpawnInRandomPosition(spawningMonumentInfo, initiator);
                        break;
                    case LocationType.River:
                        RiverSpawner.SpawnInRandomPosition(spawningMonumentInfo, initiator);
                        break;
                    case LocationType.Road:
                        RoadSpawner.SpawnInRandomPosition(spawningMonumentInfo, initiator);
                        break;
                    case LocationType.Ground:
                        GroundSpawner.SpawnInRandomPosition(spawningMonumentInfo, initiator);
                        break;
                }
            }

            public static CustomMonument TrySpawnMonument(ref SiteSpawnInfo spawningMonumentInfo, Vector3 position)
            {
                switch (spawningMonumentInfo.Type)
                {
                    case LocationType.Prefab:
                        return PrefabSpawner.TrySpawnMonument(position, ref spawningMonumentInfo);
                    case LocationType.Coastal:
                        return ShoreSpawner.TrySpawnMonument(position, ref spawningMonumentInfo);
                    case LocationType.River:
                        return RiverSpawner.TrySpawnMonument(position, ref spawningMonumentInfo);
                    case LocationType.Road:
                        return RoadSpawner.TrySpawnMonument(position, ref spawningMonumentInfo);
                    case LocationType.Water:
                        return WaterSpawner.TrySpawnMonument(position, ref spawningMonumentInfo);
                    case LocationType.Ground:
                        return GroundSpawner.TrySpawnMonument(position, ref spawningMonumentInfo);
                }

                return null;
            }

            public static CustomMonument SpawnSite(SiteSpawnInfo spawningMonumentInfo, Vector3 position, Quaternion rotation, bool isPlayerSummon, bool isLoadingMonument = false)
            {
                foreach (BaseEntity entity in spawningMonumentInfo.EntitiesForDestroy)
                    if (entity.IsExists())
                        entity.Kill();

                BaseEntity mainEntity = BuildManager.SpawnDecorEntity("assets/content/vehicles/scrap heli carrier/servergibs_scraptransport.prefab", position, rotation);

                CustomMonument customMonument;
                if (spawningMonumentInfo.SiteConfig.PresetName.Contains("depotGates"))
                    customMonument = mainEntity.gameObject.AddComponent<TrainTunnelMonument>();
                else
                    customMonument = mainEntity.gameObject.AddComponent<CustomMonument>();

                customMonument.BuildMonument(mainEntity, spawningMonumentInfo.SiteConfig, spawningMonumentInfo.Data, isPlayerSummon);

                if (MonumentAutoSpawner.IsRespawnFinish())
                    _ins.NextTick(() => CustomMonument.SaveMonuments());

                if (!isLoadingMonument && _ins._config.MainConfig.IsSpawnLogging)
                    NotifyManagerLite.PrintLogMessage("MonumentSpawn_Log", spawningMonumentInfo.SiteConfig.PresetName, MapHelper.PositionToString(position));

                return customMonument;
            }

            private static class GroundSpawner
            {
                public static void SpawnInRandomPosition(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    ServerMgr.Instance.StartCoroutine(SpawnGroundLocationCoroutine(spawningMonumentInfo, initiator));
                }

                private static IEnumerator SpawnGroundLocationCoroutine(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    int counter = 25000;
                    CustomMonument site = null;

                    while (counter-- > 0)
                    {
                        Vector3 position = PositionDefiner.GetRandomMapPoint();

                        if (IsPositionSuitable(ref position, ref spawningMonumentInfo, false, false))
                        {
                            site = SpawnSite(spawningMonumentInfo, position, spawningMonumentInfo.Rotation, false);
                            break;
                        }

                        else if (counter % 10 == 0)
                            yield return CoroutineEx.waitForEndOfFrame;
                    }

                    if (site == null)
                        NotifyManagerLite.PrintInfoMessage(initiator, "CantFindPosition_Exeption", spawningMonumentInfo.SiteConfig.PresetName);
                }

                public static bool IsPositionSuitable(ref Vector3 position, ref SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers, bool isPlayerSummoned)
                {
                    if (position.y < -0.5f)
                        return false;

                    if (CustomMonument.IsSpawnPositionBlockByOtherMonument(position, ref spawningMonumentInfo))
                        return false;

                    if (!isPlayerSummoned && !IsBiomeSuitable(position, ref spawningMonumentInfo))
                        return false;

                    if (IsPositionBlockedByTopology(position, ref spawningMonumentInfo, isPlayerSummoned))
                        return false;

                    if (IsAnyColliderBlockSpawn(position, ref spawningMonumentInfo, ignorePlayers))
                        return false;

                    bool isSuitable = false;
                    Vector3 newPosition = position;
                    Vector3 offset = spawningMonumentInfo.Data.Offset.ToVector3();

                    for (int i = -10; i <= 10; i++)
                    {
                        Vector3 deltaPos = Vector3.up * i / 2;
                        newPosition = position + deltaPos;
                        if (newPosition.y + offset.y < 0.2f)
                            continue;

                        if (spawningMonumentInfo.Data.LandscapeCheckPositions == null || spawningMonumentInfo.Data.LandscapeCheckPositions.Count == 0)
                        {
                            if (IsPositionBlockByHeightInRadius(newPosition, spawningMonumentInfo.Data.InternalRadius, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                                continue;

                            if (IsPositionBlockByHeightInRadius(newPosition, spawningMonumentInfo.Data.InternalRadius * 0.5f, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                                continue;

                            isSuitable = true;
                            break;
                        }
                        else
                        {
                            Quaternion newRotation = Quaternion.identity;
                            for (int angle = 0; angle < 360; angle += 90)
                            {
                                newRotation = Quaternion.Euler(0, angle, 0);
                                GameObject gameObject = new GameObject
                                {
                                    transform =
                                    {
                                        position = newPosition,
                                        rotation = newRotation
                                    }
                                };

                                bool isFailed = false;

                                foreach (string localPositionString in spawningMonumentInfo.Data.LandscapeCheckPositions)
                                {
                                    Vector3 localPosition = localPositionString.ToVector3();
                                    Vector3 globalPosition = PositionDefiner.GetGlobalPosition(gameObject.transform, localPosition);

                                    if (IsPositionBlockByHeight(newPosition, globalPosition, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                                    {
                                        spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                                        isFailed = true;
                                        break;
                                    }
                                }
                                UnityEngine.Object.Destroy(gameObject);

                                if (!isFailed)
                                {
                                    isSuitable = true;
                                    break;
                                }
                            }

                            if (isSuitable)
                            {
                                spawningMonumentInfo.Rotation = newRotation;
                                break;
                            }
                        }
                    }

                    if (isSuitable)
                    {
                        position = newPosition;
                    }
                    else
                    {
                        spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                        return false;
                    }

                    return true;
                }

                public static CustomMonument TrySpawnMonument(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    if (IsPositionSuitable(ref position, ref spawningMonumentInfo, false, true))
                    {
                        return SpawnSite(spawningMonumentInfo, position, spawningMonumentInfo.Rotation, true);
                    }

                    return null;
                }
            }

            private static class PrefabSpawner
            {
                private static Coroutine _cacheCoroutine;
                private static List<PrefabInfo> _cachedGameObjects;

                private static readonly HashSet<string> SingleMonumentPrefabs = new HashSet<string>
                {
                    "depotGates_1",
                    "depotGates_2",
                    "stoneQuarryRuins",
                    "hqmQuarryRuins",
                    "teslaGenerator",
                    "lostOutpost",
                    "jungle_ruins_a"
                };

                public static void StartCaching()
                {
                    _cacheCoroutine = ServerMgr.Instance.StartCoroutine(CacheCoroutine());
                }

                private static IEnumerator CacheCoroutine()
                {
                    if (_ins.IsWipe() || _ins._mapSaveData.SavedPrefabs == null || _ins._mapSaveData.SavedPrefabs.Count == 0 || _ins._isAddonJustInstalled)
                    {
                        _cachedGameObjects = Pool.Get<List<PrefabInfo>>();
                        _cachedGameObjects.Clear();
                        _ins._mapSaveData = new MapSaveData
                        {
                            MapName = World.SaveFileName
                        };
                        NotifyManagerLite.PrintWarningMessage("Caching of monuments/prefabs has started!");


                        for (int i = 0; i < TerrainMeta.Path.Monuments.Count; i++)
                        {
                            MonumentInfo monumentInfo = TerrainMeta.Path.Monuments[i];
                            TryCacheMonument(monumentInfo);

                            if (i % 10 == 0)
                                yield return CoroutineEx.waitForFixedUpdate;
                        }

                        HashSet<string> interestPrefabs = new HashSet<string>
                        {
                            "assets/bundled/prefabs/autospawn/decor/busstop/busstop.prefab",
                            "assets/bundled/prefabs/autospawn/decor/coastal_rocks_large/coastal_rocks_large_c.prefab",
                            "assets/bundled/prefabs/autospawn/decor/coastal_rocks_large/coastal_rocks_large_b.prefab",
                            "assets/bundled/prefabs/autospawn/decor/powerline/powerlineplatform_a.prefab",
                            "assets/bundled/prefabs/autospawn/decor/powerline/powerlineplatform_b.prefab",
                            "assets/bundled/prefabs/autospawn/decor/powerline/powerlineplatform_c.prefab",
                            "assets/bundled/prefabs/autospawn/unique_environment/jungle/ue_jungle_swamp_a.prefab",
                        };

                        for (int i = 0; i < World.Serialization.world.prefabs.Count; i++)
                        {
                            if (i % 100 == 0)
                                yield return CoroutineEx.waitForEndOfFrame;

                            ProtoBuf.PrefabData prefabData = World.Serialization.world.prefabs[i];
                            string prefabName = StringPool.Get(prefabData.id);

                            if (!interestPrefabs.Contains(prefabName))
                                continue;

                            GameObject gameObject = GameManager.server.FindPrefab(prefabData.id);
                            if (gameObject == null)
                                continue;

                            TryCacheGameObject(gameObject, prefabData.position, prefabData.rotation);
                        }
                        NotifyManagerLite.PrintWarningMessage($"Cached {_ins._mapSaveData.SavedPrefabs.Count} monuments/prefabs");
                    }
                    else
                    {
                        ProcessCache();
                    }

                    SaveDataFile(_ins._mapSaveData, "mapSave");
                    _cachedGameObjects.Shuffle();
                    _cacheCoroutine = null;
                }

                private static void TryCacheMonument(MonumentInfo monumentInfo)
                {
                    string name = monumentInfo.name;

                    if (!string.IsNullOrEmpty(name))
                    {
                        if (_ins._config.PrefabTypeConfig.Sites.Any(x => x.PrefabNames.Any(y => y == name)))
                        {
                            if (_cachedGameObjects.Any(x => Vector3.Distance(x.Position, monumentInfo.transform.position) < 20))
                                return;

                            Vector3 position = monumentInfo.transform.position;
                            Vector3 eulerAngels = monumentInfo.transform.eulerAngles;

                            CacheGameObject(name, position, eulerAngels);
                        }
                    }
                }

                private static void TryCacheGameObject(GameObject gameObject, Vector3 overridePosition = new Vector3(), Vector3 overrideEulerAngles = new Vector3())
                {
                    string name = gameObject.name;
                    if (name == "ue_jungle_swamp_a")
                        name = "assets/bundled/prefabs/autospawn/unique_environment/jungle/ue_jungle_swamp_a.prefab";

                    if (_ins._config.PrefabTypeConfig.Sites.Any(x => x.PrefabNames.Any(y => y == name || y.ToLower() == name)))
                    {
                        if (_cachedGameObjects.Any(x => Vector3.Distance(x.Position, gameObject.transform.position) < 20))
                            return;

                        Vector3 position = overridePosition == Vector3.zero ? gameObject.transform.position : overridePosition;
                        Vector3 eulerAngels = overridePosition == Vector3.zero ? gameObject.transform.eulerAngles : overrideEulerAngles;

                        CacheGameObject(name, position, eulerAngels);
                    }
                }

                private static void CacheGameObject(string prefabName, Vector3 position, Vector3 rotation)
                {
                    PrefabSavedData prefabData = new PrefabSavedData
                    {
                        PrefabName = prefabName,
                        Position = position.ToString(),
                        EulerAngels = rotation.ToString(),
                    };
                    _ins._mapSaveData.SavedPrefabs.Add(prefabData);

                    PrefabInfo prefabInfo = new PrefabInfo
                    {
                        PrefabName = prefabName,
                        Position = position,
                        EulerAngels = rotation
                    };
                    _cachedGameObjects.Add(prefabInfo);
                }

                private static void ProcessCache()
                {
                    _cachedGameObjects = Pool.Get<List<PrefabInfo>>();
                    _cachedGameObjects.Clear();

                    foreach (PrefabSavedData prefabSaveData in _ins._mapSaveData.SavedPrefabs)
                    {
                        PrefabInfo prefabInfo = new PrefabInfo
                        {
                            PrefabName = prefabSaveData.PrefabName,
                            Position = prefabSaveData.Position.ToVector3(),
                            EulerAngels = prefabSaveData.EulerAngels.ToVector3(),
                        };
                        _cachedGameObjects.Add(prefabInfo);
                    }
                }

                public static void ClearCache()
                {
                    if (_cacheCoroutine != null)
                        ServerMgr.Instance.StopCoroutine(_cacheCoroutine);

                    if (_cachedGameObjects != null)
                        Pool.FreeUnmanaged(ref _cachedGameObjects);
                }

                public static bool IsCachingEnd()
                {
                    return _cacheCoroutine == null;
                }


                public static bool IsPositionSuitable(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    return GetClosestSuitablePrefab(position, ref spawningMonumentInfo, true, true) != null;
                }

                public static CustomMonument TrySpawnMonument(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    PrefabInfo closestPrefab = GetClosestSuitablePrefab(position, ref spawningMonumentInfo, false, true);
                    if (closestPrefab == null)
                        return null;
                    Quaternion spawnRotation = GetRotation(spawningMonumentInfo.SiteConfig.PresetName, closestPrefab);

                    return SpawnSite(spawningMonumentInfo, closestPrefab.Position, spawnRotation, true);
                }

                private static PrefabInfo GetClosestSuitablePrefab(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers, bool isPlayerSummoned)
                {
                    PrefabInfo closestPrefab = _cachedGameObjects.Min(x => Vector3.Distance(position, x.Position));

                    if (Vector3.Distance(closestPrefab.Position, position) > 20f)
                    {
                        spawningMonumentInfo.ReasonOfFail = "ObjectNotFound_BlockSpawn";
                        return null;
                    }

                    if (!IsPrefabSuitable(closestPrefab, ref spawningMonumentInfo, ignorePlayers, isPlayerSummoned))
                        return null;

                    return closestPrefab;
                }

                private static bool IsPrefabSuitable(PrefabInfo prefabData, ref SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers = false, bool isPlayerSummoned = false)
                {
                    if (!isPlayerSummoned && !IsBiomeSuitable(prefabData.Position, ref spawningMonumentInfo))
                        return false;
                    if (CustomMonument.IsSpawnPositionBlockByOtherMonument(prefabData.Position, ref spawningMonumentInfo))
                        return false;
                    if (!IsMonumentPrefab(spawningMonumentInfo.SiteConfig.PresetName) && IsAnyColliderBlockSpawn(prefabData.Position, ref spawningMonumentInfo, ignorePlayers))
                        return false;
                    return true;
                }

                public static void SpawnInRandomPosition(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    List<PrefabInfo> suitableGameObjects = Pool.Get<List<PrefabInfo>>();
                    suitableGameObjects = _cachedGameObjects.Where(x => spawningMonumentInfo.SiteConfig.PrefabNames.Any(y => y == x.PrefabName || y.ToLower() == x.PrefabName)).ToList();
                    PrefabInfo prefabData = suitableGameObjects.FirstOrDefault(x => IsPrefabSuitable(x, ref spawningMonumentInfo));
                    if (prefabData == null)
                    {
                        Pool.FreeUnmanaged(ref suitableGameObjects);
                        NotifyManagerLite.PrintInfoMessage(initiator, "CantSpawnPrefabLocation_Exeption", spawningMonumentInfo.SiteConfig.PresetName);
                        return;
                    }

                    Quaternion spawnRotation = GetRotation(spawningMonumentInfo.SiteConfig.PresetName, prefabData);
                    SpawnSite(spawningMonumentInfo, prefabData.Position, spawnRotation, false);

                    if (suitableGameObjects != null)
                        Pool.FreeUnmanaged(ref suitableGameObjects);
                }

                private static Quaternion GetRotation(string monumentPreset, PrefabInfo prefabInfo)
                {
                    if (monumentPreset == "depotGates_2")
                    {
                        GameObject gameObject = new GameObject
                        {
                            transform =
                            {
                                position = prefabInfo.Position,
                                eulerAngles = prefabInfo.EulerAngels
                            }
                        };
                        Quaternion rotation = PositionDefiner.GetGlobalRotation(gameObject.transform, new Vector3(0, 90, 0));
                        UnityEngine.Object.DestroyImmediate(gameObject);
                        return rotation;
                    }

                    return Quaternion.Euler(prefabInfo.EulerAngels);
                }

                private static bool IsMonumentPrefab(string prefabName)
                {
                    return SingleMonumentPrefabs.Contains(prefabName);
                }

                private class PrefabInfo
                {
                    public string PrefabName;
                    public Vector3 Position;
                    public Vector3 EulerAngels;
                }
            }

            private static class ShoreSpawner
            {
                public static void SpawnInRandomPosition(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    ServerMgr.Instance.StartCoroutine(SpawnShoreMonumentCoroutine(spawningMonumentInfo, initiator));
                }

                private static IEnumerator SpawnShoreMonumentCoroutine(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    int counter = 25000;
                    CustomMonument monument = null;

                    while (counter-- > 0)
                    {
                        Vector3 position = PositionDefiner.GetRandomMapPoint();
                        monument = TrySpawnMonument(position, ref spawningMonumentInfo);
                        if (monument != null)
                            break;

                        else if (counter % 10 == 0)
                            yield return CoroutineEx.waitForEndOfFrame;
                    }

                    if (monument == null)
                        NotifyManagerLite.PrintInfoMessage(initiator, "CantFindPosition_Exeption", spawningMonumentInfo.SiteConfig.PresetName);
                }

                public static CustomMonument TrySpawnMonument(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    if (IsPositionSuitable(ref position, ref spawningMonumentInfo))
                    {
                        return SpawnSite(spawningMonumentInfo, position, spawningMonumentInfo.Rotation, true);
                    }

                    return null;
                }

                public static bool IsPositionSuitable(ref Vector3 position, ref SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers = false, bool isPlayerSummoned = false)
                {
                    position.y = 0;
                    Vector3 groundPosition = PositionDefiner.GetGroundPosition(position);
                    spawningMonumentInfo.Rotation = GetShoreMonumentRotation(position);

                    if (!isPlayerSummoned && !IsBiomeSuitable(position, ref spawningMonumentInfo))
                        return false;

                    if (groundPosition.y > 0f)
                    {
                        spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                        return false;
                    }

                    if (spawningMonumentInfo.SiteConfig.DataFileName == "pirs")
                    {
                        if (groundPosition.y > -0.75f)
                        {
                            spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                            return false;
                        }
                    }

                    if (!TopologyChecker.IsShoreTopology(position))
                    {
                        spawningMonumentInfo.ReasonOfFail = "FarShore_BlockSpawn";
                        return false;
                    }

                    if (CustomMonument.IsSpawnPositionBlockByOtherMonument(position, ref spawningMonumentInfo))
                        return false;

                    if (spawningMonumentInfo.Data.LandscapeCheckPositions == null || spawningMonumentInfo.Data.LandscapeCheckPositions.Count == 0)
                    {
                        if (IsPositionBlockByHeightInRadius(position, spawningMonumentInfo.Data.InternalRadius, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                        {
                            spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                            return false;
                        }

                        if (IsPositionBlockByHeightInRadius(position, spawningMonumentInfo.Data.InternalRadius * 0.5f, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                        {
                            spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                            return false;
                        }
                    }
                    else
                    {
                        GameObject gameObject = new GameObject
                        {
                            transform =
                            {
                                position = position,
                                rotation = spawningMonumentInfo.Rotation
                            }
                        };

                        foreach (string localPositionString in spawningMonumentInfo.Data.LandscapeCheckPositions)
                        {
                            Vector3 localPosition = localPositionString.ToVector3();
                            Vector3 globalPosition = PositionDefiner.GetGlobalPosition(gameObject.transform, localPosition);

                            if (IsPositionBlockByHeight(position, globalPosition, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                            {
                                spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                                return false;
                            }
                        }
                    }

                    if (IsAnyColliderBlockSpawn(position, ref spawningMonumentInfo, ignorePlayers))
                        return false;

                    if (spawningMonumentInfo.Data.CheckPositions != null)
                    {
                        Quaternion rotation = GetShoreMonumentRotation(position);
                        GameObject gameObject = new GameObject
                        {
                            transform =
                            {
                                position = position,
                                rotation = rotation
                            }
                        };

                        foreach (string localPositionString in spawningMonumentInfo.Data.CheckPositions)
                        {
                            Vector3 localPosition = localPositionString.ToVector3();
                            Vector3 globalPosition = PositionDefiner.GetGlobalPosition(gameObject.transform, localPosition);
                            Vector3 groundCheckPosition = PositionDefiner.GetGroundPosition(globalPosition);

                            if (groundCheckPosition.y < -0.15f)
                            {
                                UnityEngine.Object.DestroyImmediate(gameObject);
                                {
                                    spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                                    return false;
                                }
                            }
                        }

                        UnityEngine.Object.DestroyImmediate(gameObject);
                    }

                    return true;
                }

                private static Quaternion GetShoreMonumentRotation(Vector3 position)
                {
                    float radius = 50f;
                    int angleStep = 10;

                    Vector3 maxYPosition = new Vector3(0, -10, 0);

                    for (int angle = 0; angle < 360; angle += angleStep)
                    {
                        float radian = 2f * Mathf.PI * angle / 360;
                        float x = position.x + radius * Mathf.Cos(radian);
                        float z = position.z + radius * Mathf.Sin(radian);
                        Vector3 positionInRadius = PositionDefiner.GetGroundPosition(new Vector3(x, position.y, z), 1 << 23);

                        if (positionInRadius.y > maxYPosition.y)
                            maxYPosition = positionInRadius;
                    }

                    if (maxYPosition == new Vector3(0, -10, 0))
                        return Quaternion.Euler(0, UnityEngine.Random.Range(0, 360f), 0);

                    position.y = 0;
                    maxYPosition.y = 0;

                    Vector3 direction = (maxYPosition - position).normalized;
                    direction = Vector3.Cross(direction, Vector3.up);
                    return Quaternion.LookRotation(direction);
                }
            }

            private static class RiverSpawner
            {
                public static void SpawnInRandomPosition(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    ServerMgr.Instance.StartCoroutine(SpawnRiverMonumentCoroutine(spawningMonumentInfo, initiator));
                }

                private static IEnumerator SpawnRiverMonumentCoroutine(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    int counter = 1000;
                    CustomMonument monument = null;

                    while (counter-- > 0)
                    {
                        PathList randomRiver = TerrainMeta.Path.Rivers.GetRandom();
                        if (randomRiver == null)
                            break;
                        int randomVectorIndex = UnityEngine.Random.Range(1, randomRiver.Path.Points.Length - 2);

                        if (IsRiverPositionAvailable(out Vector3 spawnPosition, randomRiver, randomVectorIndex, ref spawningMonumentInfo, false, false))
                        {
                            monument = SpawnSite(spawningMonumentInfo, spawnPosition, spawningMonumentInfo.Rotation, false);
                            break;
                        }

                        if (counter % 10 == 0)
                            yield return CoroutineEx.waitForEndOfFrame;
                    }

                    if (!monument)
                        NotifyManagerLite.PrintInfoMessage(initiator, "CantFindPosition_Exeption", spawningMonumentInfo.SiteConfig.PresetName);
                }

                private static bool IsRiverPositionAvailable(out Vector3 position, PathList pathList, int targetPathIndex, ref SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers, bool isPlayerSummoned)
                {
                    position = pathList.Path.Points[targetPathIndex];
                    spawningMonumentInfo.Rotation = GetPathMonumentRotation(pathList, targetPathIndex);

                    if (position.y < 0)
                        return false;

                    if (!isPlayerSummoned && !IsBiomeSuitable(position, ref spawningMonumentInfo))
                        return false;

                    if (IsPositionBlockedByTopology(position, ref spawningMonumentInfo, isPlayerSummoned))
                        return false;

                    if (CustomMonument.IsSpawnPositionBlockByOtherMonument(position, ref spawningMonumentInfo))
                        return false;

                    if (IsAnyColliderBlockSpawn(position, ref spawningMonumentInfo, ignorePlayers))
                        return false;

                    bool isSuitable = false;
                    Vector3 newPosition = position;
                    Vector3 offset = spawningMonumentInfo.Data.Offset.ToVector3();

                    for (int i = -10; i <= 10; i++)
                    {
                        Vector3 deltaPos = Vector3.up * i / 2;
                        newPosition = position + deltaPos;
                        if (newPosition.y + offset.y < 0.2f)
                            continue;

                        if (spawningMonumentInfo.Data.LandscapeCheckPositions == null || spawningMonumentInfo.Data.LandscapeCheckPositions.Count == 0)
                        {
                            if (IsPositionBlockByHeightInRadius(newPosition, spawningMonumentInfo.Data.InternalRadius, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                                continue;

                            if (IsPositionBlockByHeightInRadius(newPosition, spawningMonumentInfo.Data.InternalRadius * 0.5f, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                                continue;

                            isSuitable = true;
                            break;
                        }

                        GameObject gameObject = new GameObject
                        {
                            transform =
                            {
                                position = newPosition,
                                rotation = spawningMonumentInfo.Rotation
                            }
                        };

                        bool isFailed = false;

                        foreach (string localPositionString in spawningMonumentInfo.Data.LandscapeCheckPositions)
                        {
                            Vector3 localPosition = localPositionString.ToVector3();
                            Vector3 globalPosition = PositionDefiner.GetGlobalPosition(gameObject.transform, localPosition);

                            if (IsPositionBlockByHeight(newPosition, globalPosition, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                            {
                                isFailed = true;
                                break;
                            }
                        }

                        if (!isFailed)
                        {
                            isSuitable = true;
                            break;
                        }

                        UnityEngine.Object.Destroy(gameObject);
                    }

                    if (isSuitable)
                    {
                        position = newPosition;
                    }
                    else
                    {
                        spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                        return false;
                    }

                    return true;
                }

                public static bool IsPositionSuitable(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    PathList closestRiver = GetClosestPath(TerrainMeta.Path.Rivers, position, out int closestPointIndex);
                    if (closestRiver == null)
                        return false;

                    Vector3 riverPoint = closestRiver.Path.Points[closestPointIndex];
                    float distanceToRiverPoint = Vector3.Distance(position, riverPoint);
                    if (distanceToRiverPoint > 10)
                        return false;

                    return IsRiverPositionAvailable(out position, closestRiver, closestPointIndex, ref spawningMonumentInfo, true, true);
                }

                public static CustomMonument TrySpawnMonument(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    PathList closestRiver = GetClosestPath(TerrainMeta.Path.Rivers, position, out int closestPointIndex);
                    if (closestRiver == null)
                        return null;

                    if (IsRiverPositionAvailable(out position, closestRiver, closestPointIndex, ref spawningMonumentInfo, false, true))
                    {
                        return SpawnSite(spawningMonumentInfo, position, spawningMonumentInfo.Rotation, true);
                    }

                    return null;
                }
            }

            private static class RoadSpawner
            {
                public static void SpawnInRandomPosition(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    ServerMgr.Instance.StartCoroutine(SpawnRoadMonumentCoroutine(spawningMonumentInfo, initiator));
                }

                private static IEnumerator SpawnRoadMonumentCoroutine(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    int counter = 2500;
                    CustomMonument monument = null;
                    while (counter-- > 0)
                    {
                        PathList randomRoad = TerrainMeta.Path.Roads.GetRandom();
                        if (randomRoad == null)
                            break;

                        int randomVectorIndex = UnityEngine.Random.Range(1, randomRoad.Path.Points.Length - 2);

                        if (IsRoadPositionAvailable(randomRoad, randomVectorIndex, spawningMonumentInfo, false, false))
                        {
                            Vector3 position = randomRoad.Path.Points[randomVectorIndex];
                            Vector3 backPosition = randomRoad.Path.Points[randomVectorIndex - 1];
                            backPosition.y = 0;
                            Vector3 frontPosition = randomRoad.Path.Points[randomVectorIndex + 1];
                            frontPosition.y = 0;

                            Vector3 direction = (frontPosition - backPosition).normalized;
                            Quaternion rotation = Quaternion.LookRotation(direction);

                            if (spawningMonumentInfo.Data.CheckPositions != null)
                            {
                                GameObject gameObject = new GameObject
                                {
                                    transform =
                                    {
                                        position = position,
                                        rotation = rotation
                                    }
                                };

                                bool isCheckPointBlock = false;
                                foreach (string localPositionString in spawningMonumentInfo.Data.CheckPositions)
                                {
                                    Vector3 localPosition = localPositionString.ToVector3();
                                    Vector3 globalPosition = PositionDefiner.GetGlobalPosition(gameObject.transform, localPosition);

                                    if (TopologyChecker.IsPositionHaveTopology(globalPosition, (int)(TerrainTopology.Enum.Road | TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside | TerrainTopology.Enum.Monument | TerrainTopology.Enum.Cliff | TerrainTopology.Enum.Building)))
                                    {
                                        UnityEngine.Object.DestroyImmediate(gameObject);
                                        isCheckPointBlock = true;
                                        break;
                                    }
                                    
                                    if (IsPositionBlockByHeight(globalPosition, globalPosition, spawningMonumentInfo.Data.MaxUpDeltaHeigh, spawningMonumentInfo.Data.MaxDownDeltaHeigh, spawningMonumentInfo.Type))
                                    {
                                        spawningMonumentInfo.ReasonOfFail = "UnsuitableLandscape_BlockSpawn";
                                        isCheckPointBlock = true;
                                        break;
                                    }
                                }

                                if (isCheckPointBlock)
                                {
                                    continue;
                                }

                                UnityEngine.Object.DestroyImmediate(gameObject);
                            }

                            monument = SpawnSite(spawningMonumentInfo, position, rotation, false);
                            break;
                        }

                        if (counter % 10 == 0)
                            yield return CoroutineEx.waitForEndOfFrame;
                    }

                    if (monument == null)
                        NotifyManagerLite.PrintInfoMessage(initiator, "CantFindPosition_Exeption", spawningMonumentInfo.SiteConfig.PresetName);
                }

                public static bool IsPositionSuitable(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    PathList closestRoad = GetClosestPath(TerrainMeta.Path.Roads, position, out int closestPointIndex);
                    if (closestRoad == null)
                        return false;

                    Vector3 roadPoint = closestRoad.Path.Points[closestPointIndex];
                    float distanceToRoadPoint = Vector3.Distance(position, roadPoint);

                    if (distanceToRoadPoint > 10)
                        return false;

                    return IsRoadPositionAvailable(closestRoad, closestPointIndex, spawningMonumentInfo, true, true);
                }

                private static bool IsRoadPositionAvailable(PathList roadPathList, int targetPointIndex, SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers, bool isPlayerSummoned)
                {
                    Vector3 position = roadPathList.Path.Points[targetPointIndex];

                    if (CustomMonument.IsSpawnPositionBlockByOtherMonument(position, ref spawningMonumentInfo))
                        return false;
                    
                    if (IsPositionBlockedByTopology(position, ref spawningMonumentInfo, isPlayerSummoned))
                        return false;
                    
                    if (!isPlayerSummoned && !IsBiomeSuitable(position, ref spawningMonumentInfo))
                        return false;

                    if (IsAnyColliderBlockSpawn(position, ref spawningMonumentInfo))
                        return false;

                    return true;
                }

                public static CustomMonument TrySpawnMonument(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    PathList closestRoad = GetClosestPath(TerrainMeta.Path.Roads, position, out int closestPointIndex);
                    if (closestRoad == null)
                        return null;

                    position = closestRoad.Path.Points[closestPointIndex];

                    if (IsRoadPositionAvailable(closestRoad, closestPointIndex, spawningMonumentInfo, false, true))
                    {
                        Vector3 backPosition = closestRoad.Path.Points[closestPointIndex - 1];
                        backPosition.y = 0;
                        Vector3 frontPosition = closestRoad.Path.Points[closestPointIndex + 1];
                        frontPosition.y = 0;

                        Vector3 direction = (frontPosition - backPosition).normalized;
                        Quaternion rotation = Quaternion.LookRotation(direction);

                        return SpawnSite(spawningMonumentInfo, position, rotation, true);
                    }

                    return null;
                }
            }

            private static class WaterSpawner
            {
                public static void SpawnInRandomPosition(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    ServerMgr.Instance.StartCoroutine(SpawnWaterMonumentCoroutine(spawningMonumentInfo, initiator));
                }

                private static IEnumerator SpawnWaterMonumentCoroutine(SiteSpawnInfo spawningMonumentInfo, BasePlayer initiator)
                {
                    int counter = 25000;
                    CustomMonument monument = null;

                    while (counter-- > 0)
                    {
                        Vector3 position = PositionDefiner.GetRandomMapPoint();
                        position.y = 0;

                        if (IsPositionSuitable(position, ref spawningMonumentInfo, false, false))
                        {
                            Quaternion rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0, 360f), 0);
                            monument = SpawnSite(spawningMonumentInfo, position, rotation, false);
                            break;
                        }

                        else if (counter % 10 == 0)
                            yield return CoroutineEx.waitForEndOfFrame;
                    }

                    if (!monument)
                        NotifyManagerLite.PrintInfoMessage(initiator, "CantFindPosition_Exeption", spawningMonumentInfo.SiteConfig.PresetName);
                }

                public static CustomMonument TrySpawnMonument(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
                {
                    position.y = 0;

                    if (IsPositionSuitable(position, ref spawningMonumentInfo, false, true))
                    {
                        Quaternion rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0, 360f), 0);
                        return SpawnSite(spawningMonumentInfo, position, rotation, true);
                    }

                    return null;
                }

                public static bool IsPositionSuitable(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers, bool isPlayerSummoned)
                {
                    position.y = 0;

                    float mapHeight = TerrainMeta.HeightMap.GetHeight(position);
                    if (-mapHeight < 5)
                    {
                        spawningMonumentInfo.ReasonOfFail = "InsufficientDepth_BlockSpawn";
                        return false;
                    }

                    if (GetShoreDistance(position) > 500)
                    {
                        spawningMonumentInfo.ReasonOfFail = "FarShore_BlockSpawn";
                        return false;
                    }

                    if (CustomMonument.IsSpawnPositionBlockByOtherMonument(position, ref spawningMonumentInfo))
                        return false;

                    if (!isPlayerSummoned && !IsBiomeSuitable(position, ref spawningMonumentInfo))
                        return false;

                    if (PositionDefiner.IsPositionOnCargoPath(position))
                    {
                        spawningMonumentInfo.ReasonOfFail = "CargoBlock_BlockSpawn";
                        return false;
                    }

                    if (IsPositionBlockedByTopology(position, ref spawningMonumentInfo, isPlayerSummoned))
                        return false;

                    if (IsAnyColliderBlockSpawn(position, ref spawningMonumentInfo, ignorePlayers))
                        return false;

                    return true;
                }

                private static float GetShoreDistance(Vector3 position)
                {
                    float xDistanceToShore = position.x - World.Size / 2f;
                    float zDistanceToShore = position.z - World.Size / 2f;
                    float distanceToShore = xDistanceToShore > zDistanceToShore ? xDistanceToShore : zDistanceToShore;
                    return distanceToShore;
                }
            }


            private static PathList GetClosestPath(List<PathList> paths, Vector3 checkPosition, out int closestPointIndex)
            {
                PathList result = null;
                float minDistance = float.MaxValue;
                closestPointIndex = 0;

                foreach (PathList pathList in paths)
                {
                    for (int i = 1; i < pathList.Path.Points.Length - 1; i++)
                    {
                        Vector3 position = pathList.Path.Points[i];
                        float distance = Vector3.Distance(position, checkPosition);

                        if (distance < minDistance)
                        {
                            result = pathList;
                            minDistance = distance;
                            closestPointIndex = i;
                        }
                    }
                }

                return result;
            }

            private static Quaternion GetPathMonumentRotation(PathList pathList, int pointIndex)
            {
                Vector3 backPosition = pathList.Path.Points[pointIndex - 1];
                backPosition.y = 0;
                Vector3 frontPosition = pathList.Path.Points[pointIndex + 1];
                frontPosition.y = 0;
                Vector3 direction = (frontPosition - backPosition).normalized;
                direction = Vector3.Cross(direction, Vector3.down);
                Quaternion rotation = Quaternion.LookRotation(direction);
                return rotation;
            }

            private static bool IsBiomeSuitable(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo)
            {
                if (spawningMonumentInfo.SiteConfig.Biomes.Count == 0)
                    return true;

                TerrainBiome.Enum biome = (TerrainBiome.Enum)TerrainMeta.BiomeMap.GetBiomeMaxType(position);
                bool isBiomeSuitable = spawningMonumentInfo.SiteConfig.Biomes.Contains(biome.ToString());
                if (!isBiomeSuitable)
                {
                    spawningMonumentInfo.ReasonOfFail = "WrongBiome_BlockSpawn";
                    return false;
                }

                return true;
            }

            private static bool IsPositionBlockByHeightInRadius(Vector3 centerPosition, float radius, float maxUpDelta, float maxDownDelta, LocationType locationType, int angleStep = 15)
            {
                for (int angle = 0; angle < 360; angle += angleStep)
                {
                    float radian = 2f * Mathf.PI * angle / 360;
                    float x = centerPosition.x + radius * Mathf.Cos(radian);
                    float z = centerPosition.z + radius * Mathf.Sin(radian);
                    Vector3 posForCheck = new Vector3(x, centerPosition.y, z);

                    if (IsPositionBlockByHeight(centerPosition, posForCheck, maxUpDelta, maxDownDelta, locationType))
                        return true;
                }

                return false;
            }

            private static bool IsPositionBlockByHeight(Vector3 centerPosition, Vector3 position, float maxUpDelta, float maxDownDelta, LocationType locationType)
            {
                position = PositionDefiner.GetGroundPosition(position);
                if (locationType == LocationType.Coastal && position.y < 0)
                    return false;

                float delta = centerPosition.y - position.y;


                if (delta > 0 && delta > maxDownDelta)
                    return true;

                if (delta < 0 && -delta > maxUpDelta)
                    return true;

                return false;
            }

            private static bool IsAnyColliderBlockSpawn(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo, bool ignorePlayers = false)
            {
                HashSet<string> blockedColliders = new HashSet<string>
                {
                    "iceberg",
                    "prevent_building",
                    "preventbuilding",
                    "prevent building"
                };

                foreach (Collider collider in Physics.OverlapSphere(position, spawningMonumentInfo.Data.ExternalRadius))
                {
                    if (collider.name.Contains("heatSource"))
                        continue;

                    if (collider.name.Contains("Safe"))
                    {
                        spawningMonumentInfo.ReasonOfFail = "ObjectBlocks_BlockSpawn";
                        return true;
                    }

                    BaseEntity entity = collider.ToBaseEntity();
                    if (!entity)
                    {
                        string colliderLowerName = collider.name.ToLower();

                        if (spawningMonumentInfo.Type != LocationType.Prefab && blockedColliders.Any(x => colliderLowerName.Contains(x)))
                        {
                            BoxCollider boxCollider =  collider as BoxCollider;
                            if (boxCollider != null)
                            {
                                if (boxCollider.size.x < 2 && boxCollider.size.z < 2)
                                    continue;
                            }
                            
                            spawningMonumentInfo.ReasonOfFail = "ObjectBlocks_BlockSpawn";
                            return true;
                        }

                        continue;
                    }

                    bool isEntityInInternalRadius = Vector3.Distance(position, entity.transform.position) < spawningMonumentInfo.Data.InternalRadius;
                    
                    if (entity.GetBuildingPrivilege() || entity is BuildingBlock or SimpleBuildingBlock)
                    {
                        spawningMonumentInfo.ReasonOfFail = "ObjectBlocks_BlockSpawn";
                        return true;
                    }
                    
                    if (!isEntityInInternalRadius)
                        continue;

                    if (ignorePlayers && entity is BaseBoat && spawningMonumentInfo.Type is LocationType.Water or LocationType.Coastal)
                        continue;

                    if (!ignorePlayers)
                    {
                        BasePlayer basePlayer = entity as BasePlayer;
                        if (basePlayer.IsRealPlayer())
                        {
                            spawningMonumentInfo.ReasonOfFail = "PlayerBlocks_BlockSpawn";
                            return true;
                        }
                    }
                    
                    if (entity is BaseVehicle and not TrainCar)
                    {
                        spawningMonumentInfo.ReasonOfFail = "ObjectBlocks_BlockSpawn";
                        return true;
                    }
                    
                    if (entity is JunkPile or DiveSite or LootContainer or OreResourceEntity or BasePortal)
                        spawningMonumentInfo.EntitiesForDestroy.Add(entity);
                }

                return false;
            }

            private static bool IsPositionBlockedByTopology(Vector3 position, ref SiteSpawnInfo spawningMonumentInfo, bool isPlayerSummoned, int angleStep = 15)
            {
                if (spawningMonumentInfo.Type == LocationType.Road)
                {
                    if (TopologyChecker.IsBlockedTopology(position, (int)(TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside)))
                    {
                        spawningMonumentInfo.ReasonOfFail = "WrongPlace_BlockSpawn";
                        return true;
                    }
                }
                else if (TopologyChecker.IsBlockedTopology(position) || (isPlayerSummoned && TopologyChecker.IsBlockedTopology(position, (int)(TerrainTopology.Enum.Building))))
                {
                    if (TopologyChecker.IsBlockedTopology(position, (int)(TerrainTopology.Enum.Road | TerrainTopology.Enum.Roadside | TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside)))
                        spawningMonumentInfo.ReasonOfFail = "RoadOrRail_BlockSpawn";
                    else
                        spawningMonumentInfo.ReasonOfFail = "WrongPlace_BlockSpawn";
                    return true;
                }

                if (spawningMonumentInfo.Type == LocationType.Ground)
                {
                    if (TopologyChecker.IsBlockedTopology(position, (int)(TerrainTopology.Enum.River)))
                    {
                        spawningMonumentInfo.ReasonOfFail = "WrongPlace_BlockSpawn";
                        return true;
                    }
                }

                for (int angle = 0; angle < 360; angle += angleStep)
                {
                    float radian = 2f * Mathf.PI * angle / 360;
                    float x = position.x + spawningMonumentInfo.Data.ExternalRadius * Mathf.Cos(radian);
                    float z = position.z + spawningMonumentInfo.Data.ExternalRadius * Mathf.Sin(radian);
                    Vector3 positionInRadius = PositionDefiner.GetGroundPosition(new Vector3(x, position.y, z));

                    if (spawningMonumentInfo.SiteConfig.DataFileName == "temple_1" && !isPlayerSummoned)
                    {
                        if (TopologyChecker.IsBlockedTopology(positionInRadius, (int)(TerrainTopology.Enum.Beach | TerrainTopology.Enum.Beachside | TerrainTopology.Enum.River | TerrainTopology.Enum.Riverside)))
                        {
                            spawningMonumentInfo.ReasonOfFail = "WrongPlace_BlockSpawn";
                            return true;
                        }
                    }
                    if (spawningMonumentInfo.Type == LocationType.Road)
                    {
                        if (TopologyChecker.IsBlockedTopology(positionInRadius, (int)(TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside)))
                        {
                            spawningMonumentInfo.ReasonOfFail = "WrongPlace_BlockSpawn";
                            return true;
                        }
                    }
                    else if (TopologyChecker.IsBlockedTopology(positionInRadius) || (isPlayerSummoned && TopologyChecker.IsBlockedTopology(positionInRadius, (int)(TerrainTopology.Enum.Building))))
                    {
                        if (TopologyChecker.IsBlockedTopology(positionInRadius, (int)(TerrainTopology.Enum.Road | TerrainTopology.Enum.Roadside | TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside)))
                            spawningMonumentInfo.ReasonOfFail = "RoadOrRail_BlockSpawn";
                        else
                            spawningMonumentInfo.ReasonOfFail = "WrongPlace_BlockSpawn";
                        return true;
                    }

                    if (spawningMonumentInfo.Type == LocationType.Water && !TopologyChecker.IsOceanTopology(positionInRadius))
                    {
                        spawningMonumentInfo.ReasonOfFail = "WrongPlace_BlockSpawn";
                        return true;
                    }
                }

                return false;
            }
        }

        private static class PlayerMonumentSpawner
        {
            public static void AddPlayer(BasePlayer player, SiteSpawnInfo monumentSpawnInfo)
            {
                if (_ins._playersWithFlare.Any(x => x.Player != null && x.Player.userID == player.userID))
                    return;

                PlayerFlareInfo playerFlareData = new PlayerFlareInfo(player, monumentSpawnInfo);
                _ins._playersWithFlare.Add(playerFlareData);
            }

            public static void StartUpdate()
            {
                _ins._playerMonumentSpawnUpdateCoroutine = ServerMgr.Instance.StartCoroutine(SpawnLocationCoroutine());
            }

            public static void StopUpdate()
            {
                if (_ins._playerMonumentSpawnUpdateCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_ins._playerMonumentSpawnUpdateCoroutine);
            }

            private static IEnumerator SpawnLocationCoroutine()
            {
                while (true)
                {
                    _ins._playersWithFlare.RemoveWhere(x => !IsFlareDataActive(x));

                    foreach (PlayerFlareInfo playerFlareData in _ins._playersWithFlare)
                    {
                        DisplayPlayerData(playerFlareData);
                    }

                    yield return CoroutineEx.waitForSeconds(1f);
                }
            }

            private static void DisplayPlayerData(PlayerFlareInfo playerFlareData)
            {
                SiteConfig monumentConfig = playerFlareData.MonumentSpawnInfo.SiteConfig;

                if (!string.IsNullOrEmpty(monumentConfig.SummonConfig.Permission) && !PermissionManager.IsUserHavePermission(playerFlareData.Player.UserIDString, monumentConfig.SummonConfig.Permission))
                {
                    NotifyManagerLite.SendMessageToPlayer(playerFlareData.Player, "NoPermission");
                    return;
                }

                Vector3 spawnPosition = playerFlareData.Player.transform.position;
                if (SiteSpawner.IsSpawnPositionSuitable(spawnPosition, ref playerFlareData.MonumentSpawnInfo))
                    NotifyManagerLite.SendMessageToPlayer(playerFlareData.Player, "Position_Suitable", playerFlareData.MonumentSpawnInfo.Data.ExternalRadius);
                else if (!string.IsNullOrEmpty(playerFlareData.MonumentSpawnInfo.ReasonOfFail))
                    NotifyManagerLite.SendMessageToPlayer(playerFlareData.Player, playerFlareData.MonumentSpawnInfo.ReasonOfFail);
            }

            private static bool IsFlareDataActive(PlayerFlareInfo playerFlareData)
            {
                if (playerFlareData.Player == null || playerFlareData.Player.IsSleeping())
                    return false;

                Item activeItem = playerFlareData.Player.GetActiveItem();
                if (activeItem == null || activeItem.info.shortname != "flare")
                    return false;

                return true;
            }
        }

        private class SiteSpawnFlare : FacepunchBehaviour
        {
            private RoadFlare _roadFlare;
            private PlayerFlareInfo _playerFlareData;

            public static void Attach(RoadFlare roadFlare, BasePlayer player, SiteSpawnInfo monumentSpawnInfo)
            {
                SiteSpawnFlare siteSpawnFlare = roadFlare.gameObject.AddComponent<SiteSpawnFlare>();
                siteSpawnFlare.Init(roadFlare, player, monumentSpawnInfo);
            }

            private void Init(RoadFlare roadFlare, BasePlayer player, SiteSpawnInfo monumentSpawnInfo)
            {
                _playerFlareData = new PlayerFlareInfo(player, monumentSpawnInfo);
                this._roadFlare = roadFlare;
                roadFlare.enableSaving = false;
                roadFlare.waterCausesExplosion = false;

                SiteConfig monumentConfig = _playerFlareData.MonumentSpawnInfo.SiteConfig;

                if (!string.IsNullOrEmpty(monumentConfig.SummonConfig.Permission) && !PermissionManager.IsUserHavePermission(player.UserIDString, monumentConfig.SummonConfig.Permission))
                {
                    LootManager.GiveItemToPLayer(player, monumentConfig.SummonConfig, 1);
                    NotifyManagerLite.SendMessageToPlayer(player, "NoPermission");
                    roadFlare.Kill();
                    return;
                }

                roadFlare.Invoke(CallSite, 30f);
                SphereEntity sphereEntity = BuildManager.SpawnChildEntity(roadFlare, "assets/bundled/prefabs/modding/events/twitch/br_sphere_red.prefab", Vector3.zero, Vector3.zero, isDecor: false) as SphereEntity;
                sphereEntity.LerpRadiusTo(monumentSpawnInfo.Data.ExternalRadius, float.MaxValue);

                if (monumentSpawnInfo.Type == LocationType.Water || monumentSpawnInfo.Type == LocationType.Coastal)
                    roadFlare.InvokeRepeating(WaterCheck, 1f, 1f);
            }

            private void OnCollisionEnter(Collision collision)
            {
                Rigidbody rigidbody = _roadFlare.GetComponent<Rigidbody>();
                rigidbody.isKinematic = true;
            }

            private void WaterCheck()
            {
                if (_roadFlare.transform.position.y <= 0)
                {
                    Rigidbody rigidbody = _roadFlare.GetComponent<Rigidbody>();
                    rigidbody.isKinematic = true;
                    _roadFlare.transform.position = new Vector3(_roadFlare.transform.position.x, 0, _roadFlare.transform.position.z);
                    _roadFlare.CancelInvoke(WaterCheck);
                }
            }

            private void CallSite()
            {
                if (_ins._config.MainConfig.MaxLocationNumberPerPlayer >= 0)
                {
                    HashSet<CustomMonument> playerMonuments = CustomMonument.GetPlayerMonuments(_playerFlareData.Player.userID);
                    int playerSitesCount = playerMonuments.Count;

                    if (playerSitesCount >= _ins._config.MainConfig.MaxLocationNumberPerPlayer)
                    {
                        LootManager.GiveItemToPLayer(_playerFlareData.Player, _playerFlareData.MonumentSpawnInfo.SiteConfig.SummonConfig, 1);
                        NotifyManagerLite.SendMessageToPlayer(_playerFlareData.Player, "MaxSitesForPlayer");
                        _roadFlare.Kill();
                        return;
                    }
                }

                CustomMonument customMonument = SiteSpawner.TrySpawnMonument(ref _playerFlareData.MonumentSpawnInfo, _roadFlare.transform.position);
                if (customMonument == null)
                {
                    LootManager.GiveItemToPLayer(_playerFlareData.Player, _playerFlareData.MonumentSpawnInfo.SiteConfig.SummonConfig, 1);
                    NotifyManagerLite.SendMessageToPlayer(_playerFlareData.Player, _playerFlareData.MonumentSpawnInfo.ReasonOfFail);
                }
                else
                {
                    customMonument.SetOwner(_playerFlareData.Player.userID);

                }

                _roadFlare.Kill();
            }
        }

        private class SiteSpawnInfo
        {
            public readonly SiteConfig SiteConfig;
            public readonly SiteData Data;
            public readonly LocationType Type;
            public Quaternion Rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0, 360f), 0);
            public readonly HashSet<BaseEntity> EntitiesForDestroy = new HashSet<BaseEntity>();
            public string ReasonOfFail = string.Empty;

            private SiteSpawnInfo(SiteConfig siteConfig, SiteData data, LocationType type)
            {
                this.SiteConfig = siteConfig;
                this.Data = data;
                this.Type = type;
            }

            public static SiteSpawnInfo GetSiteInfo(string presetName)
            {
                SiteConfig siteConfig = GetSiteConfig(presetName, out LocationType monumentType);
                if (siteConfig == null)
                    return null;

                if (!_ins._siteCustomizationData.TryGetValue(siteConfig.DataFileName, out SiteData data))
                    return null;

                return new SiteSpawnInfo(siteConfig, data, monumentType);
            }

            public static SiteSpawnInfo GetSiteInfo(ulong skinID)
            {
                SiteConfig config = GetSiteConfig(skinID, out LocationType monumentType);
                if (config == null)
                    return null;

                if (!_ins._siteCustomizationData.TryGetValue(config.DataFileName, out SiteData data))
                    return null;
                
                return new SiteSpawnInfo(config, data, monumentType);
            }

            public static SiteConfig GetSiteConfig(string presetName, out LocationType monumentType)
            {
                SiteConfig config = GetConfigByPreset(_ins._config.GroundTypeConfig.Sites, presetName);
                if (config != null)
                {
                    monumentType = LocationType.Ground;
                    return config;
                }

                config = GetConfigByPreset(_ins._config.PrefabTypeConfig.Sites, presetName);
                if (config != null)
                {
                    monumentType = LocationType.Prefab;
                    return config;
                }

                config = GetConfigByPreset(_ins._config.CoastalTypeConfig.Sites, presetName);
                if (config != null)
                {
                    monumentType = LocationType.Coastal;
                    return config;
                }

                config = GetConfigByPreset(_ins._config.WaterTypeConfig.Sites, presetName);
                if (config != null)
                {
                    monumentType = LocationType.Water;
                    return config;
                }

                config = GetConfigByPreset(_ins._config.RiverTypeConfig.Sites, presetName);
                if (config != null)
                {
                    monumentType = LocationType.River;
                    return config;
                }

                config = GetConfigByPreset(_ins._config.RoadTypeConfig.Sites, presetName);
                if (config != null)
                {
                    monumentType = LocationType.Road;
                    return config;
                }

                monumentType = LocationType.None;
                return null;
            }

            private static SiteConfig GetSiteConfig(ulong skinID, out LocationType monumentType)
            {
                SiteConfig config = GetConfigBySkin(_ins._config.GroundTypeConfig.Sites, skinID);
                if (config != null)
                {
                    monumentType = LocationType.Ground;
                    return config;
                }

                config = GetConfigBySkin(_ins._config.PrefabTypeConfig.Sites, skinID);
                if (config != null)
                {
                    monumentType = LocationType.Prefab;
                    return config;
                }

                config = GetConfigBySkin(_ins._config.CoastalTypeConfig.Sites, skinID);
                if (config != null)
                {
                    monumentType = LocationType.Coastal;
                    return config;
                }

                config = GetConfigBySkin(_ins._config.WaterTypeConfig.Sites, skinID);
                if (config != null)
                {
                    monumentType = LocationType.Water;
                    return config;
                }

                config = GetConfigBySkin(_ins._config.RiverTypeConfig.Sites, skinID);
                if (config != null)
                {
                    monumentType = LocationType.River;
                    return config;
                }

                config = GetConfigBySkin(_ins._config.RoadTypeConfig.Sites, skinID);
                if (config != null)
                {
                    monumentType = LocationType.Road;
                    return config;
                }

                monumentType = LocationType.None;
                return null;
            }

            private static SiteConfig GetConfigByPreset(HashSet<SiteConfig> sites, string presetName)
            {
                return sites.FirstOrDefault(x => x.PresetName == presetName);
            }

            private static SiteConfig GetConfigBySkin(HashSet<SiteConfig> monuments, ulong skinID)
            {
                return monuments.FirstOrDefault(x => x.SummonConfig != null && x.SummonConfig.Skin == skinID);
            }
        }

        private enum LocationType
        {
            None,
            Prefab,
            Water,
            River,
            Road,
            Coastal,
            Ground,
            Sky
        }

        private class PlayerFlareInfo
        {
            public readonly BasePlayer Player;
            public SiteSpawnInfo MonumentSpawnInfo;

            public PlayerFlareInfo(BasePlayer player, SiteSpawnInfo spawningMonumentInfo)
            {
                this.Player = player;
                MonumentSpawnInfo = spawningMonumentInfo;
            }
        }

        private class TrainTunnelMonument : CustomMonument
        {
            private readonly HashSet<CustomDoor> _customDoors = new HashSet<CustomDoor>();
            private Coroutine _neonBlinkingCoroutine;

            public override void BuildMonument(BaseEntity entity, SiteConfig siteConfig, SiteData monumentData, bool isSummonedByPlayer)
            {
                HashSet<string> trainTunnelMonumentImages = new HashSet<string>
                {
                    "C_red",
                    "L_red",
                    "O_red",
                    "S_red",
                    "E_red",
                    "D_red",
                    "O_green",
                    "P_green",
                    "E_green",
                    "N_green"
                };
                SignPainter.LoadImages(trainTunnelMonumentImages);
                base.BuildMonument(entity, siteConfig, monumentData, isSummonedByPlayer);
            }

            protected override void OnBuildFinish()
            {
                base.OnBuildFinish();

                if (MonumentData.CustomDoors != null)
                {
                    foreach (CustomDoorData customDoorData in MonumentData.CustomDoors)
                    {
                        CustomDoor customDoor = CustomDoor.CreateCustomDoor(customDoorData, mainEntity.transform, Offset, this);
                        _customDoors.Add(customDoor);
                    }
                }

                ShowClosedSign();
                _neonBlinkingCoroutine = ServerMgr.Instance.StartCoroutine(SpawnLocationCoroutine());
            }

            public void ShowClosedSign()
            {
                SignPainter.UpdateSign(NeonSigns[0], "C_red");
                SignPainter.UpdateSign(NeonSigns[1], "L_red");
                SignPainter.UpdateSign(NeonSigns[2], "O_red");
                SignPainter.UpdateSign(NeonSigns[3], "S_red");
                SignPainter.UpdateSign(NeonSigns[4], "E_red");
                SignPainter.UpdateSign(NeonSigns[5], "D_red");
            }

            public void ShowOpenSign()
            {
                NeonSigns[0].ClearContent();
                SignPainter.UpdateSign(NeonSigns[1], "O_green");
                SignPainter.UpdateSign(NeonSigns[2], "P_green");
                SignPainter.UpdateSign(NeonSigns[3], "E_green");
                SignPainter.UpdateSign(NeonSigns[4], "N_green");
                NeonSigns[5].ClearContent();
            }

            private IEnumerator SpawnLocationCoroutine()
            {
                while (NeonSigns[3].IsExists())
                {
                    NeonSigns[3].SetFlag(BaseEntity.Flags.Reserved8, !NeonSigns[3].HasFlag(BaseEntity.Flags.Reserved8));
                    yield return CoroutineEx.waitForSeconds(UnityEngine.Random.Range(0.5f, 1f));
                    NeonSigns[3].SetFlag(BaseEntity.Flags.Reserved8, !NeonSigns[3].HasFlag(BaseEntity.Flags.Reserved8));
                    yield return CoroutineEx.waitForSeconds(UnityEngine.Random.Range(0.5f, 1f));
                    NeonSigns[3].SetFlag(BaseEntity.Flags.Reserved8, !NeonSigns[3].HasFlag(BaseEntity.Flags.Reserved8));
                    yield return CoroutineEx.waitForSeconds(UnityEngine.Random.Range(2f, 5f));
                }
            }

            public override void UnloadMonument()
            {
                foreach (CustomDoor customDoor in _customDoors)
                    if (customDoor != null)
                        customDoor.KillDoor();

                if (_neonBlinkingCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_neonBlinkingCoroutine);

                base.UnloadMonument();
            }
        }

        private class CustomDoor : FacepunchBehaviour
        {
            private TrainTunnelMonument _trainTunnelMonument;
            private BaseEntity _mainEntity;
            private Vector3 _startPosition;
            private Vector3 _endPosition;
            private Vector3 _openingVector;
            private int _openState;
            private float _timeSinceButtonPress;
            private float _timeSinceLastCheck;
            private readonly HashSet<ItemBasedFlowRestrictor> _fuseSwitches = new HashSet<ItemBasedFlowRestrictor>();
            private readonly HashSet<PressButton> _pressButtons = new HashSet<PressButton>();
            private readonly HashSet<GameObject> _shouldDestroyAfterUnload = new HashSet<GameObject>();
            private TrainPassTrigger _trainPassTrigger;

            public static CustomDoor CreateCustomDoor(CustomDoorData customDoorData, Transform parentTransform, Vector3 offset, TrainTunnelMonument trainTunnelMonument)
            {
                Vector3 localPosition = customDoorData.StartPosition.Position.ToVector3() + offset;
                Vector3 localRotation = customDoorData.StartPosition.Rotation.ToVector3();

                Vector3 globalPosition = PositionDefiner.GetGlobalPosition(parentTransform, localPosition);
                Quaternion globalRotation = PositionDefiner.GetGlobalRotation(parentTransform, localRotation);

                BaseEntity mainEntity = BuildManager.SpawnDecorEntity("assets/content/vehicles/scrap heli carrier/servergibs_scraptransport.prefab", globalPosition, globalRotation);
                CustomDoor customDoor = mainEntity.gameObject.AddComponent<CustomDoor>();
                customDoor.Init(mainEntity, customDoorData, trainTunnelMonument);
                return customDoor;
            }

            private void Init(BaseEntity mainEntity, CustomDoorData customDoorData, TrainTunnelMonument trainTunnelMonument)
            {
                _trainTunnelMonument = trainTunnelMonument;
                _mainEntity = mainEntity;
                _startPosition = mainEntity.transform.position;
                _openingVector = customDoorData.EndPosition.ToVector3();
                _endPosition = mainEntity.transform.position + _openingVector;
                BuildDoor(customDoorData);
            }

            private void BuildDoor(CustomDoorData customDoorData)
            {
                foreach (EntityData entityData in customDoorData.ChildEntities)
                {
                    Vector3 localPosition = entityData.Position.ToVector3();
                    Vector3 localRotation = entityData.Rotation.ToVector3();

                    BaseEntity entity = BuildManager.SpawnChildEntity(_mainEntity, entityData.PrefabName, localPosition, localRotation, entityData.Skin);
                    entity.gameObject.layer = 8;
                }

                foreach (EntityData entityData in customDoorData.Buttons)
                {
                    Vector3 localPosition = entityData.Position.ToVector3();
                    Vector3 localRotation = entityData.Rotation.ToVector3();

                    Vector3 globalPosition = PositionDefiner.GetGlobalPosition(_mainEntity.transform, localPosition);
                    Quaternion globalRotation = PositionDefiner.GetGlobalRotation(_mainEntity.transform, localRotation);

                    BaseEntity entity = BuildManager.SpawnStaticEntity(entityData.PrefabName, globalPosition, globalRotation, entityData.Skin);

                    ItemBasedFlowRestrictor itemBasedFlowRestrictor = entity as ItemBasedFlowRestrictor;
                    if (itemBasedFlowRestrictor)
                    {
                        itemBasedFlowRestrictor.UpdateFromInput(10, 0);
                        _fuseSwitches.Add(itemBasedFlowRestrictor);
                        continue;
                    }

                    PressButton pressButton = entity as PressButton;
                    if (pressButton)
                        _pressButtons.Add(pressButton);
                }

                Vector3 size = customDoorData.TrainPassTriggerData.Size.ToVector3();
                Vector3 localTriggerPosition = customDoorData.TrainPassTriggerData.Position.ToVector3();
                Vector3 localTriggerRotation = customDoorData.TrainPassTriggerData.Rotation.ToVector3();

                GameObject gameObj = new GameObject("TrainPassController")
                {
                    transform =
                    {
                        localPosition = localTriggerPosition,
                        localEulerAngles = localTriggerRotation
                    },
                    layer = 18
                };
                gameObj.transform.SetParent(_mainEntity.transform, false);

                BoxCollider boxCollider = gameObj.AddComponent<BoxCollider>();
                boxCollider.size = size;
                boxCollider.center = Vector3.zero;
                boxCollider.isTrigger = true;

                _trainPassTrigger = gameObj.AddComponent<TrainPassTrigger>();
                _trainPassTrigger.Init(this);
                _shouldDestroyAfterUnload.Add(gameObj);
            }

            public bool IsDoorClosing()
            {
                return _openState == -1;
            }

            private void FixedUpdate()
            {
                if (Time.realtimeSinceStartup - _timeSinceLastCheck > 2)
                {
                    if (_fuseSwitches.Any(x => x != null && x.inventory.itemList.Any(y => y.info.shortname == "fuse")))
                    {
                        StartOpen();
                    }
                    else if (Time.realtimeSinceStartup - _timeSinceButtonPress < 60)
                    {
                        StartOpen();
                    }
                    else if (_pressButtons.Any(x => x != null && x.HasFlag(BaseEntity.Flags.On)))
                    {
                        StartOpen();
                        _timeSinceButtonPress = Time.realtimeSinceStartup;
                    }
                    else if ((_mainEntity.transform.position - _endPosition).magnitude < _openingVector.magnitude - 0.1f)
                        _openState = -1;
                    else
                        _openState = 0;

                    _timeSinceLastCheck = Time.realtimeSinceStartup;
                }

                if (_openState == 1)
                {
                    if ((_mainEntity.transform.position - _startPosition).magnitude < _openingVector.magnitude)
                    {
                        _mainEntity.transform.position += _openingVector / 300;
                        _mainEntity.SendNetworkUpdate();
                    }
                    else
                    {
                        _mainEntity.transform.position = _endPosition;
                        _openState = 0;
                    }
                }
                else if (_openState == -1)
                {
                    if ((_mainEntity.transform.position - _endPosition).magnitude < _openingVector.magnitude)
                    {
                        _mainEntity.transform.position -= _openingVector / 300;
                        _mainEntity.SendNetworkUpdate();
                    }
                    else
                    {
                        _mainEntity.transform.position = _startPosition;

                        if (_openState != 0)
                        {
                            _trainTunnelMonument.ShowClosedSign();
                        }

                        _openState = 0;
                    }
                }
            }

            private void StartOpen()
            {
                if (_openState != 1)
                    _trainTunnelMonument.ShowOpenSign();

                _openState = 1;
            }

            public void KillDoor()
            {
                foreach (GameObject gameObj in _shouldDestroyAfterUnload)
                    if (gameObj != null)
                        UnityEngine.GameObject.Destroy(gameObj);

                foreach (ItemBasedFlowRestrictor fuseSwitch in _fuseSwitches)
                    if (fuseSwitch.IsExists())
                        fuseSwitch.Kill();

                foreach (PressButton pressButton in _pressButtons)
                    if (pressButton.IsExists())
                        pressButton.Kill();

                if (_mainEntity.IsExists())
                    _mainEntity.Kill();
            }
        }

        private class TrainPassTrigger : FacepunchBehaviour
        {
            private CustomDoor _customDoor;

            public void Init(CustomDoor customDoor)
            {
                this._customDoor = customDoor;
            }

            private void OnTriggerEnter(Collider other)
            {
                if (other == null)
                    return;

                if (_customDoor.IsDoorClosing())
                {
                    BaseEntity entity = other.ToBaseEntity();
                    if (entity.IsExists() && entity is TrainCar)
                        entity.Kill(BaseNetworkable.DestroyMode.Gib);
                }
            }
        }

        private class CustomMonument : FacepunchBehaviour
        {
            public BaseEntity mainEntity;
            public SiteConfig MonumentConfig;
            public SiteData MonumentData;
            private ulong _ownerID;
            protected Vector3 Offset;

            private MarkerController _mapMarker;
            private ZoneController _monumentZone;

            private Coroutine _flameCoroutine;
            private Coroutine _buildCoroutine;
            private Coroutine _respawnCoroutine;

            private readonly HashSet<FlameThrowerData> _flameThrowers = new HashSet<FlameThrowerData>();
            private readonly HashSet<CardDoor> _cardDoors = new HashSet<CardDoor>();
            private readonly HashSet<BaseEntity> _decorEntities = new HashSet<BaseEntity>();
            private readonly HashSet<BaseEntity> _regularEntities = new HashSet<BaseEntity>();
            private readonly HashSet<BuildingBlock> _buildingBlocks = new HashSet<BuildingBlock>();

            private readonly HashSet<LootContainerData> _lootContainerData = new HashSet<LootContainerData>();
            private readonly HashSet<BaseEntity> _respawnEntities = new HashSet<BaseEntity>();
            private readonly HashSet<ScientistNPC> _npcHashSet = new HashSet<ScientistNPC>();

            protected readonly List<NeonSign> NeonSigns = new List<NeonSign>();

            public static CustomMonument GetMonumentByEntity(BaseEntity entity)
            {
                return _ins._monuments.FirstOrDefault(x => x != null && x.mainEntity.IsExists() && x.IsMonumentEntity(entity));
            }

            public static void SaveMonuments()
            {
                _ins._savedMonuments = new HashSet<MonumentSaveData>();

                foreach (CustomMonument customMonument in _ins._monuments)
                {
                    if (customMonument == null || !customMonument.mainEntity.IsExists())
                        continue;

                    if (customMonument._ownerID == 0 && _ins._config.MainConfig.IsRespawnLocation)
                        continue;

                    MonumentSaveData monumentSaveData = new MonumentSaveData()
                    {
                        Position = customMonument.mainEntity.transform.position.ToString(),
                        Rotation = customMonument.mainEntity.transform.eulerAngles.ToString(),
                        MonumentPreset = customMonument.MonumentConfig.PresetName,
                        OwnerId = customMonument._ownerID
                    };

                    _ins._savedMonuments.Add(monumentSaveData);
                }

                SaveDataFile(_ins._savedMonuments, "save");
            }

            public static bool IsSpawnPositionBlockByOtherMonument(Vector3 position, ref SiteSpawnInfo monumentSpawnInfo)
            {
                position.y = 0;

                foreach (CustomMonument site in _ins._monuments)
                {
                    if (site == null || !site.mainEntity.IsExists())
                        continue;

                    Vector3 siteGroundPosition = new Vector3(site.mainEntity.transform.position.x, 0, site.mainEntity.transform.position.z);
                    float distance = Vector3.Distance(position, siteGroundPosition);

                    if (distance < (monumentSpawnInfo.Data.ExternalRadius + site.MonumentData.ExternalRadius) * 1.2f)
                    {
                        monumentSpawnInfo.ReasonOfFail = "CloseMonument_BlockSpawn";
                        return true;
                    }
                }

                return false;
            }

            public static CustomMonument GetClosestCustomMonument(Vector3 position, out float minDistance)
            {
                position.y = 0;
                CustomMonument result = null;
                minDistance = float.MaxValue;

                foreach (CustomMonument customMonument in _ins._monuments)
                {
                    if (customMonument == null || !customMonument.mainEntity.IsExists())
                        continue;

                    Vector3 monumentGroundPosition = new Vector3(customMonument.mainEntity.transform.position.x, 0, customMonument.mainEntity.transform.position.z);
                    float distance = Vector3.Distance(position, monumentGroundPosition);

                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        result = customMonument;
                    }
                }

                return result;
            }

            public static HashSet<CustomMonument> GetPlayerMonuments(ulong userID)
            {
                return _ins._monuments.Where(x => x != null && x.mainEntity.IsExists() && x._ownerID == userID);
            }

            public void SetOwner(ulong userID)
            {
                _ownerID = userID;
            }

            public bool HaveOwner()
            {
                return _ownerID != 0;
            }

            public bool IsPlayerCanLoot(BasePlayer player)
            {
                if (_ownerID == 0)
                    return true;

                return _ownerID == player.userID || IsTeam(player, _ownerID);
            }

            public void ChangeAllSkins(string prefabName, ulong skin)
            {
                foreach (EntityData entityData in MonumentData.RegularEntities)
                {
                    if (prefabName == entityData.PrefabName)
                        entityData.Skin = skin;
                }

                foreach (EntityData entityData in MonumentData.DecorEntities)
                {
                    if (prefabName == entityData.PrefabName)
                        entityData.Skin = skin;
                }

                SaveDataFile(MonumentData, MonumentConfig.DataFileName);
            }

            public void ChangeEntitySkin(BaseEntity baseEntity, ulong skin)
            {
                baseEntity.skinID = skin;
                baseEntity.SendNetworkUpdate();
                EntityData targetEntityData = null;
                Vector3 baseEntityLocalPosition = PositionDefiner.GetLocalPosition(mainEntity.transform, baseEntity.transform.position);

                foreach (EntityData entityData in MonumentData.RegularEntities)
                {
                    Vector3 localposition = entityData.Position.ToVector3() + MonumentData.Offset.ToVector3();

                    if (baseEntity.PrefabName == entityData.PrefabName && Vector3.Distance(baseEntityLocalPosition, localposition) < 0.1f)
                    {
                        targetEntityData = entityData;
                        break;
                    }
                }

                if (targetEntityData == null)
                {
                    foreach (EntityData entityData in MonumentData.DecorEntities)
                    {
                        Vector3 localposition = entityData.Position.ToVector3() + MonumentData.Offset.ToVector3();

                        if (baseEntity.PrefabName == entityData.PrefabName && Vector3.Distance(baseEntityLocalPosition, localposition) < 0.1f)
                        {
                            targetEntityData = entityData;
                            break;
                        }
                    }
                }

                if (targetEntityData != null)
                {
                    targetEntityData.Skin = skin;
                    SaveDataFile(MonumentData, MonumentConfig.DataFileName);
                }
            }

            public void AddNewSpawnPoint(Vector3 position, Quaternion rotation, string presetName)
            {
                Vector3 localPosition = PositionDefiner.GetLocalPosition(mainEntity.transform, position);
                Vector3 localRotation = PositionDefiner.GetLocalRotation(mainEntity.transform, rotation);
                localPosition -= Offset;

                NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByPresetName(presetName);
                if (npcConfig != null)
                {
                    NpcPresetLocationConfig npcPresetLocation = new NpcPresetLocationConfig
                    {
                        PresetName = presetName,
                        Position = localPosition.ToString()
                    };

                    MonumentConfig.StaticNpcs.Add(npcPresetLocation);
                    SpawnStaticNpc(npcPresetLocation);
                }

                CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(presetName);
                if (crateConfig != null)
                {
                    PresetLocationConfig presetLocationConfig = new PresetLocationConfig
                    {
                        PresetName = presetName,
                        Position = localPosition.ToString(),
                        Rotation = localRotation.ToString(),
                    };
                    MonumentConfig.Crates.Add(presetLocationConfig);
                    SpawnCrate(presetLocationConfig);
                }

                _ins.SaveConfig();
            }

            public void AddGroundNpcSpawnPoint(Vector3 position, string presetName)
            {
                Vector3 localPosition = PositionDefiner.GetLocalPosition(mainEntity.transform, position);
                localPosition -= Offset;
                localPosition.y = 0;
                
                NpcPresetLocationConfig npcPresetLocation = new NpcPresetLocationConfig
                {
                    PresetName = presetName,
                    Position = localPosition.ToString()
                };
                
                SpawnGroundNpc(npcPresetLocation);
                _ins.SaveConfig();
            }

            public void ReplaceCrate(LootContainer lootContainer, string newPresetName, BasePlayer player)
            {
                PresetLocationConfig presetLocationConfig = MonumentConfig.Crates.FirstOrDefault(x => Vector3.Distance(PositionDefiner.GetLocalPosition(mainEntity.transform, lootContainer.transform.position), x.Position.ToVector3() + Offset) < 0.1f);
                if (presetLocationConfig == null)
                    return;

                presetLocationConfig.PresetName = newPresetName;
                lootContainer.Kill();
                SpawnCrate(presetLocationConfig);
                _ins.SaveConfig();
            }

            public bool TryRemoveEntity(BaseEntity entity)
            {
                Vector3 localPosition = PositionDefiner.GetLocalPosition(mainEntity.transform, entity.transform.position) - Offset;

                if (_npcHashSet.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value))
                {
                    NpcPresetLocationConfig npcPresetLocationConfig = MonumentConfig.StaticNpcs.FirstOrDefault(x => Vector3.Distance(x.Position.ToVector3(), localPosition) < 0.25f);
                    if (npcPresetLocationConfig == null)
                        return false;

                    MonumentConfig.StaticNpcs.Remove(npcPresetLocationConfig);
                }
                else if (_respawnEntities.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value))
                {
                    PrefabLocationConfig prefabLocationConfig = MonumentConfig.RespawnEntities.FirstOrDefault(x => Vector3.Distance(x.Position.ToVector3(), localPosition) < 0.25f);
                    if (prefabLocationConfig == null)
                        return false;

                    MonumentConfig.RespawnEntities.Remove(prefabLocationConfig);
                }
                else if (_lootContainerData.Any(x => x != null && x.StorageContainer != null && x.StorageContainer.net != null && x.StorageContainer.net.ID.Value == entity.net.ID.Value))
                {
                    PresetLocationConfig presetLocationConfig = MonumentConfig.Crates.FirstOrDefault(x => Vector3.Distance(x.Position.ToVector3(), localPosition) < 0.25f);
                    if (presetLocationConfig == null)
                        return false;

                    MonumentConfig.Crates.Remove(presetLocationConfig);
                }
                else
                    return false;


                _ins.SaveConfig();
                entity.Kill();
                return true;
            }

            public static LootContainerData GetLootContainerData(ulong netID)
            {
                if (_ins._monuments == null)
                    return null;

                foreach (CustomMonument customMonument in _ins._monuments)
                {
                    if (customMonument == null)
                        continue;

                    foreach (LootContainerData lootContainerData in customMonument._lootContainerData)
                    {
                        if (lootContainerData == null || lootContainerData.StorageContainer == null || lootContainerData.StorageContainer.net == null)
                            continue;

                        if (lootContainerData.StorageContainer.net.ID.Value == netID)
                            return lootContainerData;
                    }
                }

                return null;
            }

            public virtual void BuildMonument(BaseEntity entity, SiteConfig siteConfig, SiteData monumentData, bool isSummonedByPlayer)
            {
                this.mainEntity = entity;
                this.MonumentConfig = siteConfig;
                this.MonumentData = monumentData;

                _ins._monuments.Add(this);
                Offset = monumentData.Offset.ToVector3();
                _buildCoroutine = ServerMgr.Instance.StartCoroutine(BuildCoroutine(isSummonedByPlayer));
            }

            private IEnumerator BuildCoroutine(bool isSummonedByPlayer)
            {
                if (MonumentData.PaintedEntities != null && MonumentData.PaintedEntities.Count > 0)
                    SignPainter.LoadImages(MonumentData.PaintedEntities);
                
                int counter = 0;
                foreach (BuildingBlockData buildingBlockData in MonumentData.BuildingBlocks)
                {
                    counter++;
                    Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, buildingBlockData, Offset);
                    Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, buildingBlockData);

                    BuildingBlock buildingBlock = BuildManager.SpawnBuildingBlock(buildingBlockData, globalPosition, globalRotation);
                    buildingBlock.baseProtection = _ins._protection;
                    buildingBlock.SetHealthToMax();
                    _buildingBlocks.Add(buildingBlock);

                    if (buildingBlockData.ConditionModel != 0)
                    {
                        buildingBlock.Invoke(() =>
                        {
                            buildingBlock.SetConditionalModel((ulong)buildingBlockData.ConditionModel);
                            buildingBlock.SendNetworkUpdate();
                        }, 1f);
                    }

                    if (counter % 10 == 0)
                        yield return CoroutineEx.waitForFixedUpdate;
                }

                foreach (EntityData regularEntityData in MonumentData.RegularEntities)
                {
                    if (_ins._config.MainConfig.DisableRecyclers && regularEntityData.PrefabName.Contains("recycler_static"))
                        continue;

                    counter++;
                    Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, regularEntityData, Offset);
                    Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, regularEntityData);

                    BaseEntity baseEntity = BuildManager.SpawnStaticEntity(regularEntityData.PrefabName, globalPosition, globalRotation, regularEntityData.Skin);
                    if (baseEntity is PercentFullStorageContainer)
                        baseEntity.SetFlag(BaseEntity.Flags.Busy, true);

                    HotAirBalloon hotAirBalloon = baseEntity as HotAirBalloon;
                    if (hotAirBalloon != null)
                    {
                        Rigidbody rigidbody = hotAirBalloon.myRigidbody;
                        rigidbody.isKinematic = true;
                        rigidbody.freezeRotation = true;
                        hotAirBalloon.inflationLevel = 1;
                        hotAirBalloon.enabled = false;
                        hotAirBalloon.SendNetworkUpdate();
                        hotAirBalloon.enableSaving = false;
                    }

                    NeonSign neonSign = baseEntity as NeonSign;
                    if (neonSign != null)
                    {
                        neonSign.SetFlag(BaseEntity.Flags.Busy, true);
                        neonSign.SetFlag(BaseEntity.Flags.Locked, true);
                        NeonSigns.Add(neonSign);
                    }

                    ItemBasedFlowRestrictor iOEntity = baseEntity as ItemBasedFlowRestrictor;
                    if (iOEntity != null)
                    {
                        iOEntity.UpdateFromInput(10, 0);
                    }

                    DeployableBoomBox deployableBoomBox = baseEntity as DeployableBoomBox;
                    if (deployableBoomBox != null)
                    {
                        deployableBoomBox.SetFlag(BaseEntity.Flags.On, true);
                        deployableBoomBox.SendNetworkUpdate();
                    }

                    BaseCombatEntity baseCombatEntity = baseEntity as BaseCombatEntity;
                    if (baseCombatEntity != null)
                    {
                        baseCombatEntity.baseProtection = _ins._protection;
                    }

                    TreeEntity treeEntity = baseEntity as TreeEntity;
                    if (treeEntity != null)
                    {
                        treeEntity.hasBonusGame = false;
                    }

                    VineSwingingTree vineSwingingTree = baseEntity as VineSwingingTree;
                    if (vineSwingingTree != null)
                        vineSwingingTree.StumpPrefab.guid = "";

                    foreach (Collider collider in baseEntity.gameObject.GetComponentsInChildren<Collider>())
                    {
                        if (collider.name == "PreventMovement")
                            collider.gameObject.layer = 18;
                    }

                    _regularEntities.Add(baseEntity);

                    if (counter % 10 == 0)
                        yield return CoroutineEx.waitForFixedUpdate;
                }

                foreach (EntityData entityData in MonumentData.DecorEntities)
                {
                    counter++;
                    Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, entityData, Offset);
                    Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, entityData);

                    BaseEntity entity;

                    if (entityData.PrefabName.Contains("cargoship"))
                        entity = BuildManager.SpawnDecorCargo(globalPosition, globalRotation);
                    else if (entityData.PrefabName.Contains("tunneldwelling"))
                    {
                        entity = BuildManager.SpawnDecorEntity("assets/content/vehicles/scrap heli carrier/servergibs_scraptransport.prefab", globalPosition, globalRotation, entityData.Skin);
                        BuildManager.SpawnChildEntity(entity, entityData.PrefabName, Vector3.zero, Vector3.zero);
                    }
                    else
                        entity = BuildManager.SpawnDecorEntity(entityData.PrefabName, globalPosition, globalRotation, entityData.Skin);

                    if (entity.ShortPrefabName == "teslacoil.deployed")
                    {
                        entity.SetFlag(BaseEntity.Flags.On, true);
                        entity.SetFlag(BaseEntity.Flags.Reserved1, true);
                        entity.SendNetworkUpdate();
                    }
                    else if (entity.ShortPrefabName == "chineselantern.deployed" || entity.ShortPrefabName == "electric.digitalclock.deployed" || entity.ShortPrefabName == "furnace" || entity.ShortPrefabName == "largecandleset" || entity.ShortPrefabName == "skull_fire_pit" || entity.ShortPrefabName == "ceilinglight.deployed")
                    {
                        entity.SetFlag(BaseEntity.Flags.On, true);
                        entity.SendNetworkUpdate();
                    }
                    else if (entity.ShortPrefabName == "harbor_dynamic_container")
                    {
                        foreach (Collider collider in entity.gameObject.GetComponentsInChildren<Collider>())
                            if (collider.name == "hinge_door_L" || collider.name == "hinge_door_R")
                                collider.gameObject.layer = 18;
                    }
                    else if (entity.ShortPrefabName == "siegetower.entity" || entity.ShortPrefabName == "catapult.entity")
                    {
                        foreach (WheelCollider wheelCollider in entity.GetComponentsInChildren<WheelCollider>())
                            DestroyImmediate(wheelCollider);
                    }

                    foreach (Collider collider in entity.gameObject.GetComponentsInChildren<Collider>())
                    {
                        if (collider.name == "PreventMovement")
                            collider.gameObject.layer = 18;
                    }

                    if (entity.ShortPrefabName != "wall.frame.garagedoor")
                        entity.SetFlag(BaseEntity.Flags.Busy, true);
                    entity.SetFlag(BaseEntity.Flags.Locked, true);
                    _decorEntities.Add(entity);

                    if (counter % 10 == 0)
                        yield return CoroutineEx.waitForFixedUpdate;
                }

                if (MonumentData.ScaledDecorEntitiesOld != null)
                {
                    foreach (ScaledEntityDataOld scaledEntityData in MonumentData.ScaledDecorEntitiesOld)
                    {
                        counter++;
                        Vector3 localPosition = scaledEntityData.Position.ToVector3() + Offset;
                        Vector3 localRotation = scaledEntityData.Rotation.ToVector3();
                        Vector3 sphereOffset = scaledEntityData.SphereOffset.ToVector3();

                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, localPosition);
                        Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, localRotation);

                        BaseEntity baseEntity = BuildManager.SpawnScaledDecorEntity(scaledEntityData.PrefabName, globalPosition, globalRotation, scaledEntityData.Scale, sphereOffset);
                        _decorEntities.Add(baseEntity);

                        PaddlingPool paddlingPool = baseEntity.children[0] as PaddlingPool;
                        if (paddlingPool)
                        {
                            paddlingPool.maxStackSize = 1;
                            Item waterItem = ItemManager.CreateByName("water");
                            waterItem.MoveToContainer(paddlingPool.inventory);

                        }

                        if (counter % 10 == 0)
                            yield return CoroutineEx.waitForFixedUpdate;
                    }
                }

                foreach (CardReaderData cardReaderData in MonumentData.CardDoors)
                {
                    counter++;
                    CardDoor cardDoor = CardDoor.SpawnCardDoor(cardReaderData, mainEntity, Offset);
                    _cardDoors.Add(cardDoor);
                    if (counter % 10 == 0)
                        yield return CoroutineEx.waitForFixedUpdate;
                }

                if (MonumentData.ScaledEntities != null)
                {
                    foreach (ScaledEntityData scaledEntitiesData in MonumentData.ScaledEntities)
                    {
                        Vector3 sphereLocalPosition = scaledEntitiesData.Position.ToVector3() + Offset;
                        Vector3 sphereGlobalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, sphereLocalPosition);

                        SphereEntity sphereEntity = BuildManager.SpawnRegularEntity("assets/prefabs/visualization/sphere.prefab", sphereGlobalPosition, Quaternion.identity) as SphereEntity;
                        _decorEntities.Add(sphereEntity);
                        sphereEntity.LerpRadiusTo(scaledEntitiesData.Scale, float.MaxValue);

                        foreach (EntityData entityData in scaledEntitiesData.ChildEntities)
                        {
                            Vector3 localPosition = entityData.Position.ToVector3() + Offset;
                            Vector3 localRotation = entityData.Rotation.ToVector3();

                            Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, localPosition);
                            Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, localRotation);

                            BaseEntity baseEntity = BuildManager.SpawnChildEntity(sphereEntity, entityData.PrefabName, Vector3.zero, localRotation, entityData.Skin, !scaledEntitiesData.IsRegular);
                            if (baseEntity.ShortPrefabName == "skulltorch.entity" || baseEntity.ShortPrefabName == "skullspikes.candles.deployed")
                                baseEntity.SetFlag(BaseEntity.Flags.On, true);

                            PaddlingPool paddlingPool = baseEntity as PaddlingPool;
                            if (paddlingPool != null)
                            {
                                Item waterItem = ItemManager.CreateByName("water.salt", 1500);
                                waterItem.MoveToContainer(paddlingPool.inventory);
                                paddlingPool.SendNetworkUpdate();
                            }

                            BaseCombatEntity baseCombatEntity = baseEntity as BaseCombatEntity;
                            if (baseCombatEntity != null)
                                baseCombatEntity.baseProtection = _ins._protection;

                            baseEntity.Invoke(() =>
                            {
                                baseEntity.transform.position = globalPosition;
                                baseEntity.transform.rotation = globalRotation;
                                baseEntity.SendNetworkUpdate();
                            }, 5f);
                        }
                    }
                }

                if (MonumentData.FirePoints != null)
                {
                    foreach (LocationData locationData in MonumentData.FirePoints)
                    {
                        counter++;
                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, locationData.Position.ToVector3() + Offset);
                        Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, locationData.Rotation.ToVector3());

                        FlameThrowerData flameThrowerData = new FlameThrowerData(globalPosition, globalRotation);
                        _flameThrowers.Add(flameThrowerData);

                        if (counter % 10 == 0)
                            yield return CoroutineEx.waitForFixedUpdate;
                    }

                    _flameCoroutine = ServerMgr.Instance.StartCoroutine(FlameThrowerCoroutine());
                }

                if (MonumentData.WireDatas != null)
                {
                    foreach (WireData wireData in MonumentData.WireDatas)
                    {
                        counter++;
                        Vector3 localStartPosition = wireData.StartPosition.ToVector3() + Offset;
                        Vector3 localEndPosition = wireData.EndPosition.ToVector3() + Offset;

                        Vector3 globalEndPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, localEndPosition);

                        CustomDoorManipulator doorManipulator = BuildManager.SpawnChildEntity(mainEntity, "assets/prefabs/deployable/playerioents/doormanipulators/doorcontroller.deployed.prefab", localStartPosition, Vector3.zero, isDecor: false, enableSaving: false) as CustomDoorManipulator;
                        IOEntity.IOSlot ioOutput = doorManipulator.outputs[0];
                        ioOutput.connectedTo.entityRef.uid = doorManipulator.net.ID;
                        ioOutput.connectedTo = new IOEntity.IORef();
                        ioOutput.connectedTo.Set(doorManipulator);
                        ioOutput.connectedToSlot = 0;

                        ioOutput.linePoints = new List<Vector3> { new Vector3(0, 0, 0), PositionDefiner.GetLocalPosition(doorManipulator.transform, globalEndPosition) }.ToArray();
                        ioOutput.connectedTo.Init();
                        doorManipulator.MarkDirtyForceUpdateOutputs();
                        doorManipulator.SendNetworkUpdate();
                        if (counter % 10 == 0)
                            yield return CoroutineEx.waitForFixedUpdate;
                    }
                }

                if (MonumentData.Elevators != null)
                {
                    foreach (ElevatorData elevatorData in MonumentData.Elevators)
                    {
                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, elevatorData, Offset);
                        Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, elevatorData);
                        List<ulong> buttonsIDs = new List<ulong>();

                        foreach (LocationData locationData in elevatorData.Buttons)
                        {
                            Vector3 globalButtonPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, locationData, Offset);
                            Quaternion globalButtonRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, locationData);
                            PressButton pressButton = BuildManager.SpawnStaticEntity("assets/prefabs/deployable/playerioents/button/button.prefab", globalButtonPosition, globalButtonRotation) as PressButton;
                            buttonsIDs.Add(pressButton.net.ID.Value);
                            pressButton.baseProtection = _ins._protection;
                            _regularEntities.Add(pressButton);
                        }

                        CustomElevator customElevator = CustomElevator.SpawnElevator(globalPosition, globalRotation, elevatorData, buttonsIDs);
                        _decorEntities.Add(customElevator);
                    }
                }

                if (MonumentData.RandomDoors != null)
                {
                    foreach (EntityData entityData in MonumentData.RandomDoors)
                    {
                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, entityData.Position.ToVector3() + Offset);
                        Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, entityData.Rotation.ToVector3());
                        Door door = BuildManager.SpawnStaticEntity(entityData.PrefabName, globalPosition, globalRotation, entityData.Skin) as Door;
                        door.baseProtection = _ins._protection;
                        door.InvokeRandomized(() => door.SetOpen(!door.IsOpen()), 7.5f, 7.5f, 4f);
                        door.SetFlag(BaseEntity.Flags.Locked, true);
                        _regularEntities.Add(door);
                    }
                }

                if (MonumentData.ObstacleData != null)
                {
                    Vector3 doorCloserGlobalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, MonumentData.ObstacleData.DoorCloserPosition.ToVector3() + Offset);
                    BaseEntity baseEntity = BuildManager.SpawnStaticEntity("assets/prefabs/misc/doorcloser/doorcloser.prefab", doorCloserGlobalPosition, Quaternion.identity);
                    _decorEntities.Add(baseEntity);

                    foreach (BoxColliderData boxColliderData in MonumentData.ObstacleData.ObstacleColliders)
                    {
                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, boxColliderData.Position.ToVector3() + Offset);
                        Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, boxColliderData.Rotation.ToVector3());

                        GameObject child = new GameObject("ObstaclePart")
                        {
                            transform =
                            {
                                position = globalPosition,
                                rotation = globalRotation
                            }
                        };
                        child.transform.SetParent(baseEntity.transform, true);

                        NavMeshObstacle navMeshObstacle = child.gameObject.AddComponent<NavMeshObstacle>();
                        navMeshObstacle.carving = true;
                        navMeshObstacle.shape = NavMeshObstacleShape.Box;
                        navMeshObstacle.size = boxColliderData.Size.ToVector3();
                        navMeshObstacle.center = Vector3.zero;
                    }
                }
                
                if (MonumentData.PaintedEntities != null)
                {
                    foreach (PaintedEntityData paintedEntityData in MonumentData.PaintedEntities)
                    {
                        counter++;
                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, paintedEntityData, Offset);
                        Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, paintedEntityData);

                        Signage signage = BuildManager.SpawnStaticEntity(paintedEntityData.PrefabName, globalPosition, globalRotation, paintedEntityData.Skin) as Signage;
                        _regularEntities.Add(signage);
                        SignPainter.UpdateSign(signage, paintedEntityData.ImageName);
                        signage.baseProtection = _ins._protection;
                        if (!_ins._config.MainConfig.AllowDrawOnSigns)
                        {
                            signage.SetFlag(BaseEntity.Flags.Busy, true);
                            signage.SetFlag(BaseEntity.Flags.Locked, true);
                        }

                        if (counter % 10 == 0)
                            yield return CoroutineEx.waitForFixedUpdate;
                    }
                }

                if (MonumentConfig.EnableMarker)
                    _mapMarker = MarkerController.CreateMarker(this);

                yield return CoroutineEx.waitForEndOfFrame;
                OnBuildFinish();
            }

            protected virtual void OnBuildFinish()
            {
                _respawnCoroutine = ServerMgr.Instance.StartCoroutine(RespawnCoroutine());
                _monumentZone = ZoneController.CreateZone(this);
            }

            private IEnumerator RespawnCoroutine()
            {
                while (mainEntity.IsExists())
                {
                    while (_monumentZone != null && _monumentZone.IsAnyPlayerInEventZone())
                        yield return CoroutineEx.waitForSeconds(60);

                    KillCrates();
                    yield return CoroutineEx.waitForSeconds(0.1f);
                    KillAllNpc();
                    yield return CoroutineEx.waitForSeconds(0.1f);
                    KillRespawnEntities();
                    yield return CoroutineEx.waitForSeconds(0.1f);

                    foreach (PresetLocationConfig presetLocationConfig in MonumentConfig.Crates)
                    {
                        SpawnCrate(presetLocationConfig);
                        yield return CoroutineEx.waitForSeconds(0.1f);
                    }

                    foreach (NpcPresetLocationConfig presetLocationConfig in MonumentConfig.StaticNpcs)
                    {
                        SpawnStaticNpc(presetLocationConfig);
                        yield return CoroutineEx.waitForSeconds(0.1f);
                    }

                    if (MonumentData.ObstacleData != null && MonumentConfig.GroudNpcs != null)
                    {
                        foreach (NpcPresetLocationConfig presetLocationConfig in MonumentConfig.GroudNpcs)
                        {
                            SpawnGroundNpc(presetLocationConfig);
                            yield return CoroutineEx.waitForSeconds(0.1f);
                        }
                    }

                    foreach (MovableNpcPresetLocationConfig presetLocationConfig in MonumentConfig.CustomNavmeshNpc)
                    {
                        Vector3 localPosition = presetLocationConfig.Position.ToVector3() + Offset;
                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, localPosition);

                        ScientistNPC scientistNpc;
                        if (string.IsNullOrEmpty(presetLocationConfig.NavMeshPresetName))
                            scientistNpc = NpcSpawnManager.SpawnScientistNpc(presetLocationConfig.PresetName, globalPosition, 1, false);
                        else
                        {
                            BaseEntity navmeshParentEntity;

                            if (presetLocationConfig.NavMeshPresetName.Contains("cargo"))
                            {
                                if (presetLocationConfig.NavMeshPresetName.Contains("front"))
                                    navmeshParentEntity = _decorEntities.FirstOrDefault(x => x != null && x.ShortPrefabName == "cargoshiptest" && Vector3.Distance(new Vector3(-2.687f, -3.395f, -18.571f), PositionDefiner.GetLocalPosition(mainEntity.transform, x.transform.position) - Offset) < 0.1f);
                                else
                                    navmeshParentEntity = _decorEntities.FirstOrDefault(x => x != null && x.ShortPrefabName == "cargoshiptest" && Vector3.Distance(new Vector3(8.155f, 5.759f, -35.829f), PositionDefiner.GetLocalPosition(mainEntity.transform, x.transform.position) - Offset) < 0.1f);

                                if (navmeshParentEntity == null)
                                    continue;
                            }
                            else
                                navmeshParentEntity = mainEntity;

                            scientistNpc = NpcSpawnManager.SpawnScientistNpc(presetLocationConfig.PresetName, globalPosition, navmeshParentEntity.transform, presetLocationConfig.NavMeshPresetName);
                        }

                        _npcHashSet.Add(scientistNpc);
                        yield return CoroutineEx.waitForSeconds(0.1f);
                    }

                    foreach (PrefabLocationConfig prefabLocationConfig in MonumentConfig.RespawnEntities)
                    {
                        Vector3 localPosition = prefabLocationConfig.Position.ToVector3() + Offset;
                        Vector3 localRotation = prefabLocationConfig.Rotation.ToVector3();

                        Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, localPosition);
                        Quaternion globalRotation = PositionDefiner.GetGlobalRotation(mainEntity.transform, localRotation);

                        BaseEntity baseEntity = BuildManager.SpawnStaticEntity(prefabLocationConfig.PrefabName, globalPosition, globalRotation);
                        _respawnEntities.Add(baseEntity);
                        yield return CoroutineEx.waitForSeconds(0.1f);
                    }

                    yield return CoroutineEx.waitForSeconds(UnityEngine.Random.Range(MonumentConfig.MinRespawnTime, MonumentConfig.MaxRespawnTime));
                }
            }

            private void SpawnStaticNpc(NpcPresetLocationConfig presetLocationConfig)
            {
                Vector3 localPosition = presetLocationConfig.Position.ToVector3() + Offset;
                Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, localPosition);
                ScientistNPC scientistNpc = NpcSpawnManager.SpawnScientistNpc(presetLocationConfig.PresetName, globalPosition, 1, true);
                _npcHashSet.Add(scientistNpc);
            }

            private void SpawnGroundNpc(NpcPresetLocationConfig presetLocationConfig)
            {
                Vector3 localPosition = presetLocationConfig.Position.ToVector3() + Offset;
                Vector3 globalPosition = PositionDefiner.GetGlobalPosition(mainEntity.transform, localPosition);
                Vector3 groundPosition = PositionDefiner.GetGroundPosition(globalPosition);

                if (!PositionDefiner.GetNavmeshInPoint(groundPosition, 2, out NavMeshHit navMeshHit))
                    return;
                            
                groundPosition = navMeshHit.position;
                ScientistNPC scientistNpc = NpcSpawnManager.SpawnScientistNpc(presetLocationConfig.PresetName, groundPosition, 1, false);
                _npcHashSet.Add(scientistNpc);
            }

            private void SpawnCrate(PresetLocationConfig presetLocationConfig)
            {
                CrateConfig crateConfig = _ins._config.CrateConfigs.FirstOrDefault(x => x.PresetName == presetLocationConfig.PresetName);
                if (crateConfig == null)
                {
                    NotifyManagerLite.PrintError(null, "PresetNotFound_Exeption", presetLocationConfig.PresetName);
                    return;
                }

                BaseEntity crateEntity = BuildManager.SpawnStaticEntity(crateConfig.PrefabName, presetLocationConfig.Position.ToVector3() + Offset, presetLocationConfig.Rotation.ToVector3(), mainEntity.transform, crateConfig.Skin);

                LootContainer lootContainer = crateEntity as LootContainer;
                if (lootContainer != null)
                {
                    LootContainerData storageContainerData = LootManager.UpdateLootContainer(lootContainer, crateConfig);
                    _lootContainerData.Add(storageContainerData);
                    return;
                }

                StorageContainer storageContainer = crateEntity as StorageContainer;
                if (storageContainer != null)
                {
                    LootContainerData storageContainerData = LootManager.UpdateStorageContainer(storageContainer, crateConfig);
                    _lootContainerData.Add(storageContainerData);
                    storageContainer.baseProtection = _ins._protection;
                    return;
                }

                Fridge fridge = crateEntity as Fridge;
                if (fridge != null)
                {
                    LootContainerData storageContainerData = LootManager.UpdateFridgeContainer(fridge, crateConfig);
                    _lootContainerData.Add(storageContainerData);
                    fridge.baseProtection = _ins._protection;
                }
            }

            private IEnumerator FlameThrowerCoroutine()
            {
                while (true)
                {
                    foreach (FlameThrowerData flameThrowerData in _flameThrowers)
                    {
                        bool isEnabled = UnityEngine.Random.Range(0f, 1f) < 0.5f;

                        if (isEnabled)
                        {
                            if (flameThrowerData.FlameThrower.IsExists())
                                continue;

                            flameThrowerData.FlameThrower = BuildManager.SpawnRegularEntity("assets/prefabs/weapons/military flamethrower/militaryflamethrower.entity.prefab", flameThrowerData.Position, flameThrowerData.Rotation);
                        }
                        else
                        {
                            if (flameThrowerData.FlameThrower.IsExists())
                                flameThrowerData.FlameThrower.Kill();
                        }
                    }

                    yield return CoroutineEx.waitForSeconds(3f);
                }
            }

            private bool IsMonumentEntity(BaseEntity entity)
            {
                if (entity == null || entity.net == null)
                    return false;

                if (entity is BuildingBlock)
                    return _buildingBlocks.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value);

                if (entity is LootContainer)
                    return _lootContainerData.Any(x => x != null && x.StorageContainer != null && x.StorageContainer.net != null && x.StorageContainer.net.ID.Value == entity.net.ID.Value);

                if (entity is ScientistNPC)
                    return _npcHashSet.Any(x => x != null && x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value);

                if (entity is Crocodile)
                    return _respawnEntities.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value);

                if (entity is Door)
                {
                    if (_regularEntities.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value))
                        return true;

                    return _cardDoors.Any(x => x != null && x.IsMyDoor(entity));
                }

                BaseEntity parentEntity = entity.GetParentEntity();
                if (parentEntity != null && parentEntity.net != null)
                    entity = parentEntity;

                if (entity.net.ID.Value == mainEntity.net.ID.Value)
                    return true;

                if (_regularEntities.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value))
                    return true;

                if (_decorEntities.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value))
                    return true;

                if (_respawnEntities.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value))
                    return true;

                return false;
            }

            private  void KillCrates()
            {
                foreach (LootContainerData storageContainerData in _lootContainerData)
                    if (storageContainerData.StorageContainer.IsExists())
                        storageContainerData.StorageContainer.Kill();

                _lootContainerData.Clear();
            }

            private void KillAllNpc()
            {
                foreach (ScientistNPC scientistNpc in _npcHashSet)
                    if (scientistNpc.IsExists())
                        scientistNpc.Kill();

                _npcHashSet.Clear();
            }

            private void KillRespawnEntities()
            {
                foreach (BaseEntity entity in _respawnEntities)
                    if (entity.IsExists())
                        entity.Kill();

                _respawnEntities.Clear();
            }

            private void OnDestroy()
            {
                this.UnloadMonument();
            }

            public static void KillAllMonuments()
            {
                if (_ins._monuments == null)
                    return;

                foreach (CustomMonument farmSite in _ins._monuments)
                    if (farmSite != null && farmSite.mainEntity.IsExists())
                        farmSite.KillMonument();
            }

            public void KillMonument()
            {
                if (mainEntity.IsExists())
                {
                    UnloadMonument();
                    mainEntity.Kill();
                }
            }

            public virtual void UnloadMonument()
            {
                KillCrates();
                KillAllNpc();
                KillRespawnEntities();

                foreach (BaseEntity baseEntity in _buildingBlocks)
                    if (baseEntity.IsExists())
                        baseEntity.Kill();

                foreach (BaseEntity baseEntity in _decorEntities)
                    if (baseEntity.IsExists())
                        baseEntity.Kill();

                foreach (BaseEntity baseEntity in _regularEntities)
                    if (baseEntity.IsExists())
                        baseEntity.Kill();

                foreach (CardDoor cardDoor in _cardDoors)
                    if (cardDoor != null)
                        cardDoor.KillDoor();

                if (_flameCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_flameCoroutine);

                if (_buildCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_buildCoroutine);

                if (_respawnCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_respawnCoroutine);

                if (_monumentZone != null)
                    _monumentZone.DeleteZone();

                if (_mapMarker != null)
                    _mapMarker.Delete();

                foreach (FlameThrowerData flameThrowerData in _flameThrowers)
                    if (flameThrowerData.FlameThrower.IsExists())
                        flameThrowerData.FlameThrower.Kill();
            }

            private class FlameThrowerData
            {
                public BaseEntity FlameThrower;
                public readonly Vector3 Position;
                public readonly Quaternion Rotation;

                public FlameThrowerData(Vector3 position, Quaternion rotation)
                {
                    Position = position;
                    Rotation = rotation;
                }
            }
        }

        private class CustomElevator : Elevator
        {
            private float _floorHeight;
            private List<ulong> _buttonsIDs = new List<ulong>();
            public override float FloorHeight => _floorHeight;
            public override bool IsStatic => true;

            public static CustomElevator SpawnElevator(Vector3 position, Quaternion rotation, ElevatorData elevatorData, List<ulong> buttonsIDs)
            {
                Elevator elevatorEntity = BuildManager.CreateEntity("assets/prefabs/deployable/elevator/elevator.prefab", position, rotation, 0, false) as Elevator;
                CustomElevator customElevator = elevatorEntity.gameObject.AddComponent<CustomElevator>();
                BuildManager.CopySerializableFields(elevatorEntity, customElevator);
                UnityEngine.Object.DestroyImmediate(elevatorEntity, true);
                customElevator.Spawn();
                _ins._elevators.Add(customElevator);
                customElevator.Init(elevatorData, buttonsIDs);
                return customElevator;
            }

            public static CustomElevator GetElevatorByButton(ulong netId)
            {
                return _ins._elevators.FirstOrDefault(x => x != null && x.IsElevatorButton(netId));
            }

            public void OnButtonPressed(ulong netId)
            {
                int floor = _buttonsIDs.IndexOf(netId);
                RequestMoveLiftTo(floor, out float _, this);
            }

            private void Init(ElevatorData elevatorData, List<ulong> buttonsIDs)
            {
                FieldInfo elevatorPoweredField = typeof(Elevator).GetField("ElevatorPowered", BindingFlags.Public | BindingFlags.Static);
                this.SetFlag(elevatorPoweredField?.GetValue(null) is BaseEntity.Flags poweredFlag ? poweredFlag : BaseEntity.Flags.Reserved8, true);
                this.OnDeployed(null, null, null);
                this.Floor = elevatorData.NumberOfFloors - 1;
                this.UpdateChildEntities(true);
                this.SendNetworkUpdate();
                this._floorHeight = elevatorData.FloorHeight;
                this.baseProtection = _ins._protection;
                this._buttonsIDs = buttonsIDs;
                RequestMoveLiftTo(0, out float _, this);
            }

            private bool IsElevatorButton(ulong netId)
            {
                return _buttonsIDs.Any(x => x == netId);
            }
        }

        private class CardDoor : FacepunchBehaviour
        {
            private CardReader _cardReader;
            private ItemBasedFlowRestrictor _fuseContainer;
            private readonly HashSet<Door> _doors = new HashSet<Door>();
            private readonly HashSet<PressButton> _buttons = new HashSet<PressButton>();
            private Coroutine _updateCoroutine;
            private float _lastOpenTime;
            private bool _isOpen;

            public static CardDoor GetCardDoorByButtonNetId(ulong netID)
            {
                return _ins._cardDoors.FirstOrDefault(x => x != null && x._buttons.Any(y => y != null && y.net.ID.Value == netID));
            }

            public static CardDoor GetCardDoorByReaderNetId(ulong netID)
            {
                return _ins._cardDoors.FirstOrDefault(x => x != null && x._cardReader.net.ID.Value == netID);
            }

            public static CardDoor SpawnCardDoor(CardReaderData cardReaderData, BaseEntity mainEntity, Vector3 offset)
            {
                CardReader cardReader = BuildManager.SpawnStaticEntity("assets/prefabs/io/electric/switches/cardreader.prefab", cardReaderData.CardReaderLocation.Position.ToVector3() + offset, cardReaderData.CardReaderLocation.Rotation.ToVector3(), mainEntity.transform) as CardReader;
                CardDoor cardDoor = cardReader.gameObject.AddComponent<CardDoor>();
                cardDoor.Init(cardReader, cardReaderData, mainEntity, offset);
                _ins._cardDoors.Add(cardDoor);
                return cardDoor;
            }

            public bool IsMyDoor(BaseEntity entity)
            {
                return _doors.Any(x => x != null && x.net != null && x.net.ID.Value == entity.net.ID.Value);
            }

            private void Init(CardReader cardReader, CardReaderData cardReaderData, BaseEntity mainEntity, Vector3 offset)
            {
                this._cardReader = cardReader;
                cardReader.accessLevel = cardReaderData.CardReaderType;
                cardReader.accessDuration = 60f;
                cardReader.SetFlag(cardReader.AccessLevel1, cardReader.accessLevel == 1);
                cardReader.SetFlag(cardReader.AccessLevel2, cardReader.accessLevel == 2);
                cardReader.SetFlag(cardReader.AccessLevel3, cardReader.accessLevel == 3);
                cardReader.MarkDirty();

                BuildCardDoor(cardReaderData, mainEntity, offset);
                _updateCoroutine = ServerMgr.Instance.StartCoroutine(UpdateCoroutine());

                if (_fuseContainer == null)
                    cardReader.UpdateFromInput(1, 0);
            }

            private void BuildCardDoor(CardReaderData cardReaderData, BaseEntity mainEntity, Vector3 offset)
            {
                if (cardReaderData.FuseLocation != null)
                {
                    _fuseContainer = BuildManager.SpawnStaticEntity("assets/prefabs/io/electric/switches/fusebox/fusebox.prefab", cardReaderData.FuseLocation.Position.ToVector3() + offset, cardReaderData.FuseLocation.Rotation.ToVector3(), mainEntity.transform) as ItemBasedFlowRestrictor;
                    _fuseContainer.UpdateFromInput(1, 0);
                }

                foreach (EntityData entityData in cardReaderData.Doors)
                {
                    Door door = BuildManager.SpawnStaticEntity(entityData.PrefabName, entityData.Position.ToVector3() + offset, entityData.Rotation.ToVector3(), mainEntity.transform, entityData.Skin) as Door;
                    door.SetFlag(BaseEntity.Flags.Locked, true);
                    door.baseProtection = _ins._protection;
                    _doors.Add(door);
                }

                foreach (LocationData locationData in cardReaderData.Buttons)
                {
                    PressButton pressButton = BuildManager.SpawnStaticEntity("assets/prefabs/deployable/playerioents/button/button.prefab", locationData.Position.ToVector3() + offset, locationData.Rotation.ToVector3(), mainEntity.transform) as PressButton;
                    pressButton.baseProtection = _ins._protection;
                    _buttons.Add(pressButton);
                }
            }

            private IEnumerator UpdateCoroutine()
            {
                while (_cardReader.IsExists())
                {
                    if (_fuseContainer != null)
                        _cardReader.UpdateFromInput(_fuseContainer.HasPassthroughItem() ? 1 : 0, 0);

                    if (_isOpen && Time.realtimeSinceStartup - _lastOpenTime > _cardReader.accessDuration)
                    {
                        foreach (Door door in _doors)
                            if (door != null && door.IsOpen())
                                door.SetOpen(false);

                        _cardReader.CancelAccess();
                        _isOpen = false;
                    }

                    yield return CoroutineEx.waitForSeconds(2f);
                }
            }

            public void OpenDoor()
            {
                foreach (Door door in _doors)
                    if (door != null)
                        door.SetOpen(true);

                _isOpen = true;
                _lastOpenTime = Time.realtimeSinceStartup;
            }

            public void KillDoor()
            {
                if (_updateCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_updateCoroutine);

                if (_fuseContainer.IsExists())
                    _fuseContainer.Kill();

                foreach (PressButton pressButton in _buttons)
                    if (pressButton.IsExists())
                        pressButton.Kill();

                foreach (Door door in _doors)
                    if (door.IsExists())
                        door.Kill();

                if (_cardReader.IsExists())
                    _cardReader.Kill();
            }
        }

        private class MarkerController : FacepunchBehaviour
        {
            private MapMarkerGenericRadius _radiusMarker;
            private VendingMachineMapMarker _vendingMarker;
            private Coroutine _updateCounter;
            private CustomMonument _customMonument;

            public static MarkerController CreateMarker(CustomMonument customMonument)
            {
                if (!_ins._config.MarkerConfig.UseRingMarker && !_ins._config.MarkerConfig.UseShopMarker)
                    return null;

                GameObject gameObject = new GameObject
                {
                    transform =
                    {
                        position = customMonument.transform.position
                    },
                    layer = 18
                };
                MarkerController mapMarker = gameObject.AddComponent<MarkerController>();
                mapMarker.Init(customMonument);
                return mapMarker;
            }

            private void Init(CustomMonument customMonument)
            {
                this._customMonument = customMonument;
                CreateRadiusMarker();
                CreateVendingMarker();
                _updateCounter = ServerMgr.Instance.StartCoroutine(MarkerUpdateCounter());
            }

            private void CreateRadiusMarker()
            {
                if (!_ins._config.MarkerConfig.UseRingMarker)
                    return;

                _radiusMarker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", this.gameObject.transform.position) as MapMarkerGenericRadius;
                _radiusMarker.enableSaving = false;
                _radiusMarker.Spawn();
                _radiusMarker.radius = _ins._config.MarkerConfig.Radius;
                _radiusMarker.alpha = _ins._config.MarkerConfig.Alpha;
                _radiusMarker.color1 = new Color(_ins._config.MarkerConfig.Color1.R, _ins._config.MarkerConfig.Color1.G, _ins._config.MarkerConfig.Color1.B);
                _radiusMarker.color2 = new Color(_ins._config.MarkerConfig.Color2.R, _ins._config.MarkerConfig.Color2.G, _ins._config.MarkerConfig.Color2.B);
            }

            private void CreateVendingMarker()
            {
                if (!_ins._config.MarkerConfig.UseShopMarker)
                    return;

                _vendingMarker = GameManager.server.CreateEntity("assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab", this.gameObject.transform.position) as VendingMachineMapMarker;
                _vendingMarker.enableSaving = false;
                _vendingMarker.Spawn();
                _vendingMarker.markerShopName = $"{_customMonument.MonumentConfig.DisplayName}";
                _vendingMarker.SetFlag(BaseEntity.Flags.Busy, false);
                _vendingMarker.SendNetworkUpdate();
            }

            private IEnumerator MarkerUpdateCounter()
            {
                while (_customMonument != null)
                {
                    UpdateVendingMarker();
                    UpdateRadiusMarker();
                    yield return CoroutineEx.waitForSeconds(1f);
                }
            }

            private void UpdateRadiusMarker()
            {
                if (!_radiusMarker.IsExists())
                    return;

                _radiusMarker.SendUpdate();
                _radiusMarker.SendNetworkUpdate();
            }

            private void UpdateVendingMarker()
            {
                if (!_vendingMarker.IsExists())
                    return;

                _vendingMarker.SetFlag(BaseEntity.Flags.Busy, !_customMonument.HaveOwner());
                _vendingMarker.SendNetworkUpdate();
            }

            public void Delete()
            {
                if (_radiusMarker.IsExists())
                    _radiusMarker.Kill();

                if (_vendingMarker.IsExists())
                    _vendingMarker.Kill();

                if (_updateCounter != null)
                    ServerMgr.Instance.StopCoroutine(_updateCounter);

                Destroy(this.gameObject);
            }
        }

        private class ZoneController : FacepunchBehaviour
        {
            private CustomMonument _customMonument;
            private GameObject _preventBuilding;
            private SphereCollider _sphereCollider;
            private TriggerRadiation _radiation;
            private readonly HashSet<BaseEntity> _spheres = new HashSet<BaseEntity>();
            private readonly HashSet<BasePlayer> _playersInZone = new HashSet<BasePlayer>();

            public static ZoneController CreateZone(CustomMonument customMonument)
            {
                GameObject gameObject = new GameObject
                {
                    transform =
                    {
                        position = customMonument.transform.position
                    },
                    layer = (int)Rust.Layer.Reserved1
                };

                ZoneController zoneController = gameObject.AddComponent<ZoneController>();
                zoneController.Init(customMonument);
                _ins._monumentZones.Add(zoneController);
                return zoneController;
            }

            public static ZoneController GetMonumentZoneByPlayer(ulong userId)
            {
                return _ins._monumentZones.FirstOrDefault(x => x != null && x._playersInZone.Any(y => y != null && y.userID == userId));
            }

            public bool IsPlayerInZone(ulong userID)
            {
                return _playersInZone.Any(x => x != null && x.userID == userID);
            }

            public bool IsAnyPlayerInEventZone()
            {
                return _playersInZone.Any(x => x.IsExists() && !x.IsSleeping());
            }

            public void OnPlayerLeaveZone(BasePlayer player)
            {
                Interface.CallHook($"OnPlayerExit{_ins.Name}", player);
                _playersInZone.Remove(player);

                if (_radiation != null)
                    player.LeaveTrigger(_radiation);
            }

            private void Init(CustomMonument customMonument)
            {
                this._customMonument = customMonument;
                CreateTriggerSphere();

                if (customMonument.MonumentData.IsBuildingBlocked)
                    CreateBuildingBlockZone();

                if (customMonument.MonumentConfig.Radiation > 0)
                {
                    _radiation = _sphereCollider.gameObject.AddComponent<TriggerRadiation>();
                    _radiation.RadiationAmountOverride = customMonument.MonumentConfig.Radiation;
                    _radiation.InterestLayers = 131072;
                    _radiation.enabled = true;
                }
            }

            private void CreateTriggerSphere()
            {
                _sphereCollider = gameObject.AddComponent<SphereCollider>();
                _sphereCollider.isTrigger = true;
                _sphereCollider.radius = _customMonument.MonumentData.ExternalRadius;
            }

            private void CreateSphere(string prefabName)
            {
                BaseEntity sphere = GameManager.server.CreateEntity(prefabName, gameObject.transform.position);
                SphereEntity entity = sphere.GetComponent<SphereEntity>();
                entity.currentRadius = _customMonument.MonumentData.ExternalRadius * 2;
                entity.lerpSpeed = 0f;
                sphere.enableSaving = false;
                sphere.Spawn();
                _spheres.Add(sphere);
            }

            private void CreateBuildingBlockZone()
            {
                _preventBuilding = new GameObject("PreventBuildingCollider");
                _preventBuilding.transform.position = _customMonument.mainEntity.transform.position + _customMonument.MonumentData.Offset.ToVector3();
                _preventBuilding.layer = 29;

                SphereCollider sphereCollider = _preventBuilding.AddComponent<SphereCollider>();
                sphereCollider.radius = _customMonument.MonumentData.ExternalRadius;
            }

            private void OnTriggerEnter(Collider other)
            {
                BasePlayer player = other.GetComponentInParent<BasePlayer>();
                if (player.IsRealPlayer())
                {
                    Interface.CallHook($"OnPlayerEnter{_ins.Name}", player);
                    _playersInZone.Add(player);
                }
            }

            private void OnTriggerExit(Collider other)
            {
                BasePlayer player = other.GetComponentInParent<BasePlayer>();

                if (player.IsRealPlayer())
                    OnPlayerLeaveZone(player);
            }

            public void DeleteZone()
            {
                foreach (BaseEntity sphere in _spheres)
                    if (sphere != null && !sphere.IsDestroyed)
                        sphere.Kill();

                if (_preventBuilding != null)
                    UnityEngine.GameObject.Destroy(_preventBuilding);

                UnityEngine.GameObject.Destroy(gameObject);
            }
        }

        private static class LootManager
        {
            public static void GiveItemToPLayer(BasePlayer player, ItemConfig itemConfig, int amount)
            {
                Item item = CreateItem(itemConfig, amount);
                if (item == null)
                    return;

                GiveItemToPLayer(player, item);
            }

            private static void GiveItemToPLayer(BasePlayer player, Item item)
            {
                int slots = player.inventory.containerMain.capacity + player.inventory.containerBelt.capacity;
                int taken = player.inventory.containerMain.itemList.Count + player.inventory.containerBelt.itemList.Count;

                if (slots - taken > 0)
                    player.inventory.GiveItem(item);
                else
                    item.Drop(player.transform.position, Vector3.up);
            }

            private static Item CreateItem(ItemConfig itemConfig, int amount)
            {
                Item item = ItemManager.CreateByName(itemConfig.Shortname, amount, itemConfig.Skin);

                if (itemConfig.Name != "")
                    item.name = itemConfig.Name;

                return item;
            }

            public static CrateConfig GetCrateConfigByPresetName(string presetName)
            {
                return _ins._config.CrateConfigs.FirstOrDefault(x => x.PresetName == presetName);
            }

            public static void InitialLootManagerUpdate()
            {
                LootPrefabController.FindPrefabs();
                UpdateLootTables();
            }

            private static void UpdateLootTables()
            {
                foreach (CrateConfig crateConfig in _ins._config.CrateConfigs)
                    UpdateBaseLootTable(crateConfig.LootTableConfig);

                foreach (NpcConfig npcConfig in _ins._config.NpcConfigs)
                    UpdateBaseLootTable(npcConfig.LootTableConfig);

                _ins.SaveConfig();
            }

            private static void UpdateBaseLootTable(BaseLootTableConfig baseLootTableConfig)
            {
                for (int i = 0; i < baseLootTableConfig.Items.Count; i++)
                {
                    LootItemConfig lootItemConfig = baseLootTableConfig.Items[i];

                    if (lootItemConfig.Chance <= 0)
                        baseLootTableConfig.Items.RemoveAt(i);
                }

                baseLootTableConfig.Items = baseLootTableConfig.Items.OrderByQuickSort(x => x.Chance);

                if (baseLootTableConfig.MaxItemsAmount > baseLootTableConfig.Items.Count)
                    baseLootTableConfig.MaxItemsAmount = baseLootTableConfig.Items.Count;

                if (baseLootTableConfig.MinItemsAmount > baseLootTableConfig.MaxItemsAmount)
                    baseLootTableConfig.MinItemsAmount = baseLootTableConfig.MaxItemsAmount;
            }

            public static void UpdateItemContainer(ItemContainer itemContainer, LootTableConfig lootTableConfig, bool deleteItems = false)
            {
                UpdateLootTable(itemContainer, lootTableConfig, deleteItems);
            }

            public static LootContainerData UpdateLootContainer(LootContainer lootContainer, CrateConfig crateConfig)
            {
                HackableLockedCrate hackableLockedCrate = lootContainer as HackableLockedCrate;
                if (hackableLockedCrate != null)
                {
                    if (hackableLockedCrate.mapMarkerInstance.IsExists())
                    {
                        hackableLockedCrate.mapMarkerInstance.Kill();
                        hackableLockedCrate.mapMarkerInstance = null;
                    }

                    hackableLockedCrate.Invoke(() => DelayUpdateHackableLockedCrate(hackableLockedCrate, crateConfig), 1f);
                }

                SupplyDrop supplyDrop = lootContainer as SupplyDrop;
                if (supplyDrop != null)
                {
                    supplyDrop.RemoveParachute();
                    supplyDrop.MakeLootable();
                }

                FreeableLootContainer freeableLootContainer = lootContainer as FreeableLootContainer;
                if (freeableLootContainer != null)
                    freeableLootContainer.SetFlag(BaseEntity.Flags.Reserved8, false);

                lootContainer.Invoke(() => UpdateLootTable(lootContainer.inventory, crateConfig.LootTableConfig, crateConfig.LootTableConfig.ClearDefaultItemList), 2f);
                LootContainerData storageContainerData = new LootContainerData(lootContainer, crateConfig.PresetName);
                return storageContainerData;
            }

            public static LootContainerData UpdateFridgeContainer(Fridge fridge, CrateConfig crateConfig)
            {
                fridge.OnlyAcceptCategory = ItemCategory.All;
                UpdateLootTable(fridge.inventory, crateConfig.LootTableConfig, false);
                LootContainerData storageContainerData = new LootContainerData(fridge, crateConfig.PresetName);
                return storageContainerData;
            }

            public static LootContainerData UpdateStorageContainer(StorageContainer storageContainer, CrateConfig crateConfig)
            {
                storageContainer.onlyAcceptCategory = ItemCategory.All;
                UpdateLootTable(storageContainer.inventory, crateConfig.LootTableConfig, false);
                LootContainerData storageContainerData = new LootContainerData(storageContainer, crateConfig.PresetName);
                return storageContainerData;
            }

            private static void DelayUpdateHackableLockedCrate(HackableLockedCrate hackableLockedCrate, CrateConfig crateConfig)
            {
                if (hackableLockedCrate == null || crateConfig.HackTime < 0)
                    return;

                hackableLockedCrate.hackSeconds = HackableLockedCrate.requiredHackSeconds - crateConfig.HackTime;
                UpdateLootTable(hackableLockedCrate.inventory, crateConfig.LootTableConfig, crateConfig.LootTableConfig.ClearDefaultItemList);
                hackableLockedCrate.InvokeRepeating(() => hackableLockedCrate.SendNetworkUpdate(), 1f, 1f);
            }

            public static void UpdateCrateHackTime(HackableLockedCrate hackableLockedCrate, string cratePresetName)
            {
                CrateConfig crateConfig = GetCrateConfigByPresetName(cratePresetName);

                if (crateConfig.HackTime < 0)
                    return;

                hackableLockedCrate.Invoke(() => hackableLockedCrate.hackSeconds = HackableLockedCrate.requiredHackSeconds - crateConfig.HackTime, 1.1f);
            }

            private static void UpdateLootTable(ItemContainer itemContainer, LootTableConfig lootTableConfig, bool clearContainer)
            {
                if (itemContainer == null)
                    return;

                UpdateBaseLootTable(itemContainer, lootTableConfig, clearContainer || !string.IsNullOrEmpty(lootTableConfig.AlphaLootPresetName));

                if (!string.IsNullOrEmpty(lootTableConfig.AlphaLootPresetName))
                {
                    if (_ins.plugins.Exists("AlphaLoot") && (bool)_ins.AlphaLoot.Call("ProfileExists", lootTableConfig.AlphaLootPresetName))
                    {
                        _ins.AlphaLoot.Call("PopulateLoot", itemContainer, lootTableConfig.AlphaLootPresetName);
                    }
                }
            }

            private static void UpdateBaseLootTable(ItemContainer itemContainer, BaseLootTableConfig baseLootTableConfig, bool clearContainer)
            {
                if (itemContainer == null)
                    return;

                if (clearContainer)
                    ClearItemsContainer(itemContainer);

                LootPrefabController.TryAddLootFromPrefabs(itemContainer, baseLootTableConfig.PrefabConfigs);
                RandomItemsFiller.TryAddItemsToContainer(itemContainer, baseLootTableConfig);

                if (itemContainer.capacity < itemContainer.itemList.Count)
                    itemContainer.capacity = itemContainer.itemList.Count;
            }

            private static void ClearItemsContainer(ItemContainer container)
            {
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    Item item = container.itemList[i];
                    item.RemoveFromContainer();
                    item.Remove();
                }
            }
        }

        private class LootPrefabController
        {
            private string _prefabName;
            private LootContainer.LootSpawnSlot[] _lootSpawnSlot;
            private LootSpawn _lootDefinition;
            private int _maxDefinitionsToSpawn;
            private int _scrapAmount;

            public static void TryAddLootFromPrefabs(ItemContainer itemContainer, PrefabLootTableConfigs prefabLootTableConfig)
            {
                if (!prefabLootTableConfig.IsEnable)
                    return;

                PrefabConfig prefabConfig = prefabLootTableConfig.Prefabs.GetRandom();

                if (prefabConfig == null)
                    return;

                int multiplicator = UnityEngine.Random.Range(prefabConfig.MinLootScale, prefabConfig.MaxLootScale + 1);
                TryFillContainerByPrefab(itemContainer, prefabConfig.PrefabName, multiplicator);
            }

            public static void FindPrefabs()
            {
                foreach (CrateConfig crateConfig in _ins._config.CrateConfigs.Where(x => x.LootTableConfig.PrefabConfigs.IsEnable))
                    foreach (PrefabConfig prefabConfig in crateConfig.LootTableConfig.PrefabConfigs.Prefabs)
                        TrySaveLootPrefab(prefabConfig.PrefabName);

                foreach (NpcConfig npcConfig in _ins._config.NpcConfigs.Where(x => x.LootTableConfig.PrefabConfigs.IsEnable))
                    foreach (PrefabConfig prefabConfig in npcConfig.LootTableConfig.PrefabConfigs.Prefabs)
                        TrySaveLootPrefab(prefabConfig.PrefabName);
            }

            private static void TrySaveLootPrefab(string prefabName)
            {
                if (_ins._lootPrefabData.Any(x => x._prefabName == prefabName))
                    return;

                GameObject gameObject = GameManager.server.FindPrefab(prefabName);

                if (gameObject == null)
                    return;

                LootContainer lootContainer = gameObject.GetComponent<LootContainer>();

                if (lootContainer != null)
                {
                    SaveLootPrefabData(prefabName, lootContainer.LootSpawnSlots, lootContainer.scrapAmount, lootContainer.lootDefinition, lootContainer.maxDefinitionsToSpawn);
                    return;
                }

                global::HumanNPC humanNpc = gameObject.GetComponent<global::HumanNPC>();

                if (humanNpc != null && humanNpc.LootSpawnSlots.Length > 0)
                {
                    SaveLootPrefabData(prefabName, humanNpc.LootSpawnSlots, 0);
                    return;
                }

                global::ScarecrowNPC scarecrowNpc = gameObject.GetComponent<global::ScarecrowNPC>();

                if (scarecrowNpc != null && scarecrowNpc.LootSpawnSlots.Length > 0)
                    SaveLootPrefabData(prefabName, scarecrowNpc.LootSpawnSlots, 0);
            }

            private static void SaveLootPrefabData(string prefabName, LootContainer.LootSpawnSlot[] lootSpawnSlot, int scrapAmount, LootSpawn lootDefinition = null, int maxDefinitionsToSpawn = 0)
            {
                LootPrefabController lootPrefabData = new LootPrefabController
                {
                    _prefabName = prefabName,
                    _lootSpawnSlot = lootSpawnSlot,
                    _lootDefinition = lootDefinition,
                    _maxDefinitionsToSpawn = maxDefinitionsToSpawn,
                    _scrapAmount = scrapAmount
                };

                _ins._lootPrefabData.Add(lootPrefabData);
            }

            private static void TryFillContainerByPrefab(ItemContainer itemContainer, string prefabName, int multiplicator)
            {
                LootPrefabController lootPrefabData = GetDataForPrefabName(prefabName);

                if (lootPrefabData != null)
                    for (int i = 0; i < multiplicator; i++)
                        lootPrefabData.SpawnPrefabLootInCrate(itemContainer);
            }

            private static LootPrefabController GetDataForPrefabName(string prefabName)
            {
                return _ins._lootPrefabData.FirstOrDefault(x => x._prefabName == prefabName);
            }

            private void SpawnPrefabLootInCrate(ItemContainer itemContainer)
            {
                if (_lootSpawnSlot != null && _lootSpawnSlot.Length > 0)
                {
                    foreach (LootContainer.LootSpawnSlot lootSpawnSlot in _lootSpawnSlot)
                        for (int j = 0; j < lootSpawnSlot.numberToSpawn; j++)
                            if (UnityEngine.Random.Range(0f, 1f) <= lootSpawnSlot.probability)
                                lootSpawnSlot.definition.SpawnIntoContainer(itemContainer);
                }
                else if (_lootDefinition != null)
                {
                    for (int i = 0; i < _maxDefinitionsToSpawn; i++)
                        _lootDefinition.SpawnIntoContainer(itemContainer);
                }

                GenerateScrap(itemContainer);
            }

            private void GenerateScrap(ItemContainer itemContainer)
            {
                if (_scrapAmount <= 0)
                    return;

                Item item = ItemManager.CreateByName("scrap", _scrapAmount);

                if (item == null)
                    return;

                if (!item.MoveToContainer(itemContainer))
                    item.Remove();
            }
        }

        private static class RandomItemsFiller
        {
            private static readonly Dictionary<char, GrowableGenetics.GeneType> CharToGene = new Dictionary<char, GrowableGenetics.GeneType>
            {
                ['g'] = GrowableGenetics.GeneType.GrowthSpeed,
                ['y'] = GrowableGenetics.GeneType.Yield,
                ['h'] = GrowableGenetics.GeneType.Hardiness,
                ['w'] = GrowableGenetics.GeneType.WaterRequirement,
            };

            public static void TryAddItemsToContainer(ItemContainer itemContainer, BaseLootTableConfig baseLootTableConfig)
            {
                if (!baseLootTableConfig.IsRandomItemsEnable)
                    return;

                HashSet<int> includeItemIndexes = new HashSet<int>();
                int targetItemsCount = UnityEngine.Random.Range(baseLootTableConfig.MinItemsAmount, baseLootTableConfig.MaxItemsAmount + 1);

                while (includeItemIndexes.Count < targetItemsCount)
                {
                    if (!baseLootTableConfig.Items.Any(x => x.Chance >= 0.1f && !includeItemIndexes.Contains(baseLootTableConfig.Items.IndexOf(x))))
                        break;

                    for (int i = 0; i < baseLootTableConfig.Items.Count; i++)
                    {
                        if (includeItemIndexes.Contains(i))
                            continue;

                        LootItemConfig lootItemConfig = baseLootTableConfig.Items[i];
                        float chance = UnityEngine.Random.Range(0.0f, 100.0f);

                        if (chance <= lootItemConfig.Chance)
                        {
                            Item item = CreateItem(lootItemConfig);
                            includeItemIndexes.Add(i);

                            if (itemContainer.itemList.Count >= itemContainer.capacity)
                                itemContainer.capacity += 1;

                            if (item == null || !item.MoveToContainer(itemContainer))
                                item.Remove();

                            if (includeItemIndexes.Count == targetItemsCount)
                                return;
                        }
                    }
                }
            }

            private static Item CreateItem(LootItemConfig lootItemConfig)
            {
                int amount = UnityEngine.Random.Range(lootItemConfig.MinAmount, lootItemConfig.MaxAmount + 1);

                if (amount <= 0)
                    amount = 1;

                return CreateItem(lootItemConfig, amount);
            }

            private static Item CreateItem(LootItemConfig itemConfig, int amount)
            {
                Item item;

                if (itemConfig.IsBlueprint)
                {
                    item = ItemManager.CreateByName("blueprintbase");
                    item.blueprintTarget = ItemManager.FindItemDefinition(itemConfig.Shortname).itemid;
                }
                else
                    item = ItemManager.CreateByName(itemConfig.Shortname, amount, itemConfig.Skin);

                if (item == null)
                {
                    _ins.PrintWarning($"Failed to create item! ({itemConfig.Shortname})");
                    return null;
                }

                if (!string.IsNullOrEmpty(itemConfig.Name))
                    item.name = itemConfig.Name;

                if (itemConfig.Genomes != null && itemConfig.Genomes.Count > 0)
                {
                    string genome = itemConfig.Genomes.GetRandom();
                    UpdateGenome(item, genome);
                }

                return item;
            }

            private static void UpdateGenome(Item item, string genome)
            {
                genome = genome.ToLower();
                GrowableGenes growableGenes = new GrowableGenes();

                for (int i = 0; i < 6 && i < genome.Length; ++i)
                {
                    GrowableGenetics.GeneType geneType = CharToGene.GetValueOrDefault(genome[i], GrowableGenetics.GeneType.Empty);
                    growableGenes.Genes[i].Set(geneType, true);
                    GrowableGeneEncoding.EncodeGenesToItem(GrowableGeneEncoding.EncodeGenesToInt(growableGenes), item);
                }

            }
        }

        private class LootContainerData
        {
            public readonly BaseEntity StorageContainer;
            public readonly string PresetName;

            public LootContainerData(BaseEntity entityContainer, string presetName)
            {
                this.StorageContainer = entityContainer;
                this.PresetName = presetName;
            }
        }

        private static class NpcSpawnManager
        {
            public static bool IsNpcSpawnReady()
            {
                if (!_ins.plugins.Exists("NpcSpawn"))
                {
                    _ins.PrintError("NpcSpawn plugin doesn`t exist! Please read the file ReadMe.txt!");
                    _ins.NextTick(() => Interface.Oxide.UnloadPlugin(_ins.Name));
                    return false;
                }
                else
                    return true;
            }

            public static ScientistNPC SpawnScientistNpc(string npcPresetName, Vector3 position, Transform parentTransform, string navMeshName)
            {
                ScientistNPC scientistNpc = SpawnScientistNpc(npcPresetName, position, 1, false);
                if (!scientistNpc)
                    return null;

                _ins.NpcSpawn.Call("SetCustomNavMesh", scientistNpc, parentTransform, navMeshName);
                return scientistNpc;
            }

            public static ScientistNPC SpawnScientistNpc(string npcPresetName, Vector3 position, float healthFraction, bool isStationary, bool isPassive = false)
            {
                NpcConfig npcConfig = GetNpcConfigByPresetName(npcPresetName);
                if (npcConfig == null)
                {
                    NotifyManagerLite.PrintError(null, "PresetNotFound_Exeption", npcPresetName);
                    return null;
                }

                ScientistNPC scientistNpc = SpawnScientistNpc(npcConfig, position, healthFraction, isStationary, isPassive);

                if (isStationary)
                    UpdateClothesWeight(scientistNpc);

                return scientistNpc;
            }

            private static ScientistNPC SpawnScientistNpc(NpcConfig npcConfig, Vector3 position, float healthFraction, bool isStationary, bool isPassive)
            {
                JObject baseNpcConfigObj = GetBaseNpcConfig(npcConfig, healthFraction, isStationary, isPassive);
                ScientistNPC scientistNpc = (ScientistNPC)_ins.NpcSpawn.Call("SpawnNpc", position, baseNpcConfigObj, isPassive);
                return scientistNpc;
            }

            public static NpcConfig GetNpcConfigByPresetName(string npcPresetName)
            {
                return _ins._config.NpcConfigs.FirstOrDefault(x => x.PresetName == npcPresetName);
            }

            public static NpcConfig GetNpcConfigByDisplayName(string displayName)
            {
                return _ins._config.NpcConfigs.FirstOrDefault(x => x.DisplayName == displayName);
            }

            private static JObject GetBaseNpcConfig(NpcConfig config, float healthFraction, bool isStationary, bool isPassive)
            {
                return new JObject
                {
                    ["Name"] = config.DisplayName,
                    ["WearItems"] = new JArray
                    {
                        config.WearItems.Select(x => new JObject
                        {
                            ["ShortName"] = x.ShortName,
                            ["SkinID"] = x.SkinID
                        })
                    },
                    ["BeltItems"] = isPassive ? new JArray() : new JArray { config.BeltItems.Select(x => new JObject { ["ShortName"] = x.ShortName, ["Amount"] = x.Amount, ["SkinID"] = x.SkinID, ["mods"] = new JArray { x.Mods.ToHashSet() }, ["Ammo"] = x.Ammo }) },
                    ["Kit"] = config.Kit,
                    ["Health"] = config.Health * healthFraction,
                    ["RoamRange"] = isStationary ? 0 : config.RoamRange,
                    ["ChaseRange"] = isStationary ? 0 : config.ChaseRange,
                    ["SenseRange"] = config.SenseRange,
                    ["ListenRange"] = config.SenseRange / 2,
                    ["AttackRangeMultiplier"] = config.AttackRangeMultiplier,
                    ["CheckVisionCone"] = config.CheckVisionCone,
                    ["VisionCone"] = config.VisionCone,
                    ["HostileTargetsOnly"] = false,
                    ["DamageScale"] = config.DamageScale,
                    ["TurretDamageScale"] = config.TurretDamageScale,
                    ["AimConeScale"] = config.AimConeScale,
                    ["DisableRadio"] = config.DisableRadio,
                    ["CanRunAwayWater"] = false,
                    ["CanSleep"] = config.CanSleep,
                    ["SleepDistance"] = config.SleepDistance,
                    ["Speed"] = isStationary ? 0 : config.Speed,
                    ["AreaMask"] = 1,
                    ["AgentTypeID"] = -1372625422,
                    ["HomePosition"] = string.Empty,
                    ["MemoryDuration"] = config.MemoryDuration,
                    ["States"] = isPassive ? new JArray() : isStationary ? new JArray { "IdleState", "CombatStationaryState" } : config.BeltItems.Any(x => x.ShortName == "rocket.launcher" || x.ShortName == "explosive.timed") ? new JArray { "RaidState", "RoamState", "ChaseState", "CombatState" } : new JArray { "RoamState", "ChaseState", "CombatState" }
                };
            }

            private static void UpdateClothesWeight(ScientistNPC scientistNpc)
            {
                foreach (Item item in scientistNpc.inventory.containerWear.itemList)
                {
                    ItemModWearable component = item.info.GetComponent<ItemModWearable>();

                    if (component != null)
                        component.weight = 0;
                }
            }
        }

        private static class SignPainter
        {
            public static void LoadImages(HashSet<string> imageNames)
            {
                foreach (string imageName in  imageNames)
                {
                    if (_ins._images.ContainsKey(imageName))
                        continue;

                    ServerMgr.Instance.StartCoroutine(LoadImage(imageName));
                }
            }
            
            public static void LoadImages(HashSet<PaintedEntityData> paintedEntityDatas)
            {
                foreach (PaintedEntityData paintedEntityData in  paintedEntityDatas)
                {
                    if (_ins._images.ContainsKey(paintedEntityData.ImageName))
                        continue;

                    ServerMgr.Instance.StartCoroutine(LoadImage(paintedEntityData.ImageName));
                }
            }
            
            public static void UpdateSign(Signage signage, string imageName)
            {
                if (string.IsNullOrEmpty(imageName) || !_ins._images.TryGetValue(imageName, out uint imageId))
                    return;
                
                signage.EnsureInitialized();
                Array.Resize(ref signage.textureIDs, 1);
                signage.textureIDs[0] = imageId;
                signage.SendNetworkUpdate();
                
                if (signage is NeonSign)
                    signage.UpdateFromInput(100, 0);
            }

            private static IEnumerator LoadImage(string imageName)
            {
                string imagePath = $"{_ins.Name}/Images/";
                string url = "file://" + Interface.Oxide.DataDirectory + Path.DirectorySeparatorChar + imagePath + imageName + ".png";
                using UnityWebRequest unityWebRequest = UnityWebRequestTexture.GetTexture(url);
                yield return unityWebRequest.SendWebRequest();
                if (unityWebRequest.result == UnityWebRequest.Result.Success)
                {
                    Texture2D texture = DownloadHandlerTexture.GetContent(unityWebRequest);
                    uint imageId = FileStorage.server.Store(texture.EncodeToPNG(), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                    _ins._images.TryAdd(imageName, imageId);
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        private static class TopologyChecker
        {
            private const int OceanTopologies = (int)(TerrainTopology.Enum.Ocean | TerrainTopology.Enum.Oceanside);
            private const int BlockedTopologies = (int)(TerrainTopology.Enum.Road | TerrainTopology.Enum.Roadside | TerrainTopology.Enum.Rail | TerrainTopology.Enum.Railside);
            private const int MonumentTopologies = (int)(TerrainTopology.Enum.Monument);

            public static bool IsPositionHaveTopology(Vector3 position, int topology)
            {
                int pointTopologies = TerrainMeta.TopologyMap.GetTopology(position);

                if ((pointTopologies & topology) != 0)
                    return true;

                return false;
            }

            public static bool IsBlockedTopology(Vector3 position, int topologies = BlockedTopologies | MonumentTopologies)
            {
                int pointTopologies = TerrainMeta.TopologyMap.GetTopology(position);

                if ((pointTopologies & topologies) != 0)
                    return true;

                return false;
            }

            public static bool IsShoreTopology(Vector3 position)
            {
                int pointTopologies = TerrainMeta.TopologyMap.GetTopology(position);

                if ((pointTopologies & (int)TerrainTopology.Enum.Oceanside) != 0)
                    return true;

                return false;
            }

            public static bool IsOceanTopology(Vector3 position)
            {
                int pointTopologies = TerrainMeta.TopologyMap.GetTopology(position);

                if ((pointTopologies & OceanTopologies) != 0)
                    return true;

                return false;
            }
        }

        private static class BuildManager
        {
            public static BuildingBlock SpawnBuildingBlock(BuildingBlockData buildingBlockData, Vector3 position, Quaternion rotation, bool enableSaving = false)
            {
                BuildingBlock buildingBlock = CreateEntity(buildingBlockData.PrefabName, position, rotation, 0, enableSaving) as BuildingBlock;
                buildingBlock.AttachToBuilding(BuildingManager.server.NewBuildingID());
                buildingBlock.grounded = true;
                buildingBlock.cachedStability = 1;
                buildingBlock.Spawn();
                BuildingManager.server.decayEntities.Remove(buildingBlock);
                BuildingGrade.Enum buildingGrade = (BuildingGrade.Enum)buildingBlockData.Grade;
                buildingBlock.ChangeGradeAndSkin(buildingGrade, buildingBlockData.Skin);

                if (buildingBlockData.Color != 0)
                    buildingBlock.SetCustomColour(buildingBlockData.Color);

                return buildingBlock;
            }
            internal static BaseEntity SpawnScaledDecorEntity(string prefabName, Vector3 position, Quaternion rotation, float scale, Vector3 sphereOffset, ulong skinId = 0)
            {
                SphereEntity sphereEntity = SpawnRegularEntity("assets/prefabs/visualization/sphere.prefab", position - new Vector3(0, 0, 0), rotation) as SphereEntity;

                BaseEntity scaledEntity = SpawnChildEntity(sphereEntity, prefabName, new Vector3(0, 0, 0) / scale, Vector3.zero, skinId);
                if (scaledEntity.ShortPrefabName == "submarineduo.entity")
                {
                    foreach (Collider collider in scaledEntity.gameObject.GetComponentsInChildren<Collider>())
                    {
                        if (collider.name == "PreventMovementColliders")
                            collider.gameObject.layer = 18;
                    }
                }

                DestroyEntityComponent<TorpedoServerProjectile>(scaledEntity);
                sphereEntity.LerpRadiusTo(scale, float.MaxValue);

                return sphereEntity;
            }

            public static BaseEntity SpawnRegularEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0, bool enableSaving = false)
            {
                BaseEntity entity = CreateEntity(prefabName, position, rotation, skinId, enableSaving);
                entity.Spawn();
                return entity;
            }

            public static BaseEntity SpawnStaticEntity(string prefabName, Vector3 localPosition, Vector3 localRotation, Transform parentTransform, ulong skinId = 0)
            {
                Vector3 globalPosition = PositionDefiner.GetGlobalPosition(parentTransform, localPosition);
                Quaternion globalRotation = PositionDefiner.GetGlobalRotation(parentTransform, localRotation);
                return SpawnStaticEntity(prefabName, globalPosition, globalRotation, skinId);
            }

            public static BaseEntity SpawnStaticEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0)
            {
                BaseEntity entity = CreateEntity(prefabName, position, rotation, skinId, false);
                DestroyUnnecessaryComponents(entity);

                StabilityEntity stabilityEntity = entity as StabilityEntity;
                if (stabilityEntity != null)
                    stabilityEntity.grounded = true;

                BaseCombatEntity baseCombatEntity = entity as BaseCombatEntity;
                if (baseCombatEntity != null)
                    baseCombatEntity.pickup.enabled = false;

                entity.Spawn();
                return entity;
            }

            public static BaseEntity SpawnDecorEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0)
            {
                BaseEntity entity = CreateDecorEntity(prefabName, position, rotation, skinId);
                DestroyUnnecessaryComponents(entity);
                DestroyDecorComponents(entity);
                entity.Spawn();
                return entity;
            }

            public static BaseEntity SpawnChildEntity(BaseEntity parentEntity, string prefabName, Vector3 localPosition, Vector3 localRotation, ulong skinId = 0, bool isDecor = true, bool enableSaving = false)
            {
                BaseEntity entity = isDecor ? CreateDecorEntity(prefabName, parentEntity.transform.position, Quaternion.identity, skinId) : CreateEntity(prefabName, parentEntity.transform.position, Quaternion.identity, skinId, enableSaving);
                SetParent(parentEntity, entity, localPosition, localRotation);
                DestroyUnnecessaryComponents(entity);
                if (isDecor)
                    DestroyDecorComponents(entity);

                entity.Spawn();
                return entity;
            }

            public static BaseEntity SpawnDecorCargo(Vector3 position, Quaternion rotation)
            {
                CargoShip cargoShip = CreateEntity("assets/content/vehicles/boats/cargoship/cargoshiptest.prefab", position, rotation, 0, false) as CargoShip;
                cargoShip.layouts[0].SetActive(true);
                cargoShip.SendNetworkUpdate();
                cargoShip.scientistSpawnPoints = Array.Empty<Transform>();
                DestroyDecorComponents(cargoShip);
                BaseEntity customCargoShip = cargoShip.gameObject.AddComponent<BaseEntity>();
                CopySerializableFields(cargoShip, customCargoShip);
                UnityEngine.Object.DestroyImmediate(cargoShip, true);

                customCargoShip.Spawn();
                SpawnChildEntity(customCargoShip, "assets/bundled/prefabs/static/door.hinged.cargo_ship_side.prefab", new Vector3(11.90f, 3.50f, 2.25f), new Vector3(0.00f, 180.00f, 0.00f));
                SpawnChildEntity(customCargoShip, "assets/bundled/prefabs/static/door.hinged.cargo_ship_side.prefab", new Vector3(11.90f, 3.50f, 18.75f), new Vector3(0.00f, 180.00f, 0.00f));
                SpawnChildEntity(customCargoShip, "assets/bundled/prefabs/static/door.hinged.cargo_ship_side.prefab", new Vector3(-11.90f, 3.50f, 18.75f), new Vector3(0.00f, 0.00f, 0.00f));
                SpawnChildEntity(customCargoShip, "assets/bundled/prefabs/static/door.hinged.cargo_ship_side.prefab", new Vector3(-11.90f, 3.50f, 2.25f), new Vector3(0.00f, 0.00f, 0.00f));
                return customCargoShip;
            }
            
            public static BaseEntity CreateEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId, bool enableSaving)
            {
                BaseEntity entity = GameManager.server.CreateEntity(prefabName, position, rotation);
                entity.enableSaving = enableSaving;
                entity.skinID = skinId;
                return entity;
            }

            private static BaseEntity CreateDecorEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0, bool enableSaving = false)
            {
                BaseEntity entity = CreateEntity(prefabName, position, rotation, skinId, enableSaving);

                BaseEntity trueBaseEntity = entity.gameObject.AddComponent<BaseEntity>();
                CopySerializableFields(entity, trueBaseEntity);
                UnityEngine.Object.DestroyImmediate(entity, true);
                entity.SetFlag(BaseEntity.Flags.Busy, true);
                entity.SetFlag(BaseEntity.Flags.Locked, true);

                return trueBaseEntity;
            }

            private static void SetParent(BaseEntity parentEntity, BaseEntity childEntity, Vector3 localPosition, Vector3 localRotation)
            {
                childEntity.transform.localPosition = localPosition;
                childEntity.transform.localEulerAngles = localRotation;
                childEntity.SetParent(parentEntity);
            }

            private static void DestroyDecorComponents(BaseEntity entity)
            {
                DestroyEntityComponents<TriggerParent>(entity);

                Component[] components = entity.GetComponentsInChildren<Component>();

                foreach (Component component in components)
                {
                    EntityCollisionMessage entityCollisionMessage = component as EntityCollisionMessage;

                    if (entityCollisionMessage || (component && component.name != entity.PrefabName))
                    {
                        Transform transform = component as Transform;
                        if (transform)
                            continue;

                        Collider collider = component as Collider;
                        if (collider && !collider.isTrigger && collider.gameObject.layer != 29)
                            continue;

                        if (component is Model)
                            continue;

                        UnityEngine.Object.DestroyImmediate(component);
                    }
                }
            }

            private static void DestroyUnnecessaryComponents(BaseEntity entity)
            {
                DestroyEntityComponent<GroundWatch>(entity);
                DestroyEntityComponent<DestroyOnGroundMissing>(entity);
                DestroyEntityComponent<TriggerHurtEx>(entity);

                if (entity.ShortPrefabName == "catapult.entity" || entity.ShortPrefabName == "siegetower.entity")
                {
                    Rigidbody rigidbody = entity.GetComponent<Rigidbody>();
                    if (rigidbody != null)
                        rigidbody.isKinematic = true;

                    return;
                }


                if (entity is not HotAirBalloon && entity is not FreeableLootContainer)
                {
                    DestroyEntityComponent<Rigidbody>(entity);
                }
            }

            private static void DestroyEntityComponent<TYpeForDestroy>(BaseEntity entity) where TYpeForDestroy : UnityEngine.Object
            {
                TYpeForDestroy component = entity.GetComponent<TYpeForDestroy>();
                if (component)
                    UnityEngine.Object.DestroyImmediate(component);
            }

            private static void DestroyEntityComponents<TYpeForDestroy>(BaseEntity entity) where TYpeForDestroy : UnityEngine.Object
            {
                TYpeForDestroy[] components = entity.GetComponentsInChildren<TYpeForDestroy>();

                foreach (TYpeForDestroy component in components)
                {
                    if (component != null)
                        UnityEngine.Object.DestroyImmediate(component);
                }
            }

            public static void CopySerializableFields<T>(T src, T dst)
            {
                FieldInfo[] srcFields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (FieldInfo field in srcFields)
                {
                    object value = field.GetValue(src);
                    field.SetValue(dst, value);
                }
            }
        }
        
        private static class MapSaver
        {
            private static readonly Vector3 LocationCenterPosition = new Vector3(0, 200, 0);
            private const float RadiusForSaving = 80;

            private static readonly Dictionary<string, string> ColliderToPrefabs = new Dictionary<string, string>
            {
                ["wall.frame.fence"] = "assets/prefabs/building/wall.frame.fence/wall.frame.fence.prefab",
                ["assets/content/props/barricades_static/barricade_sandbags.prefab"] = "assets/prefabs/deployable/barricades/barricade.sandbags.prefab",
                ["assets/content/props/barricades_static/barricade_concrete.prefab"] = "assets/prefabs/deployable/barricades/barricade.concrete.prefab",
                ["glass_collider"] = "assets/prefabs/building/wall.window.reinforcedglass/wall.window.glass.reinforced.prefab",
                ["assets/content/building/parts/static/floor.grill.prefab"] = "assets/prefabs/building/floor.grill/floor.grill.prefab",
            };

            public static void SaveMap(string dataFileName)
            {
                SiteData buildingSiteData = new SiteData
                {
                    Offset = "(0, 0, 0)",
                    Rotation = "(0, 0, 0)",
                    DecorEntities = new HashSet<EntityData>(),
                    RegularEntities = new HashSet<EntityData>(),
                    BuildingBlocks = new HashSet<BuildingBlockData>(),
                    WireDatas = new HashSet<WireData>(),
                    CardDoors = new HashSet<CardReaderData>(),
                    CustomDoors = new HashSet<CustomDoorData>(),
                    CheckPositions = new HashSet<string>(),
                    LandscapeCheckPositions = new HashSet<string>(),
                    FirePoints = new HashSet<LocationData>(),
                    ScaledDecorEntitiesOld = new HashSet<ScaledEntityDataOld>(),
                    ScaledEntities = new HashSet<ScaledEntityData>()
                };

                SaveAllEntitiesInRadius(ref buildingSiteData);
                SaveDataFile(buildingSiteData, dataFileName);
            }

            private static void SaveAllEntitiesInRadius(ref SiteData buildingSiteData)
            {
                List<Collider> colliders = Physics.OverlapSphere(LocationCenterPosition, RadiusForSaving).Where(x => true).OrderBy(x => x.transform.position.z);
                colliders = colliders.OrderBy(x => x.name.Length);

                foreach (Collider collider in colliders)
                {
                    if (collider.name.Contains("building core"))
                    {
                        BuildingBlockData buildingBlockData = new BuildingBlockData();
                        buildingBlockData.PrefabName = GetBuildingBlockPrefabNameAndConditionModel(collider.name, out int conditionModel);
                        buildingBlockData.ConditionModel = conditionModel;
                        buildingBlockData.Grade = GetBuildingBlockGradeAndSkin(collider.name, out ulong skin);
                        buildingBlockData.Skin = skin;
                        buildingBlockData.Position = GetPosition(collider.transform);
                        buildingBlockData.Rotation = GetRotation(collider.transform);

                        if (!buildingSiteData.BuildingBlocks.Any(x => x.PrefabName == buildingBlockData.PrefabName && x.Position == buildingBlockData.Position && x.Rotation == buildingBlockData.Rotation))
                            buildingSiteData.BuildingBlocks.Add(buildingBlockData);

                        continue;
                    }

                    if (ColliderToPrefabs.TryGetValue(collider.name, out string redefinedPrefab))
                    {
                        EntityData entityData = new EntityData();
                        entityData.PrefabName = redefinedPrefab;
                        entityData.Position = GetPosition(collider.transform);
                        entityData.Rotation = GetRotation(collider.transform);

                        if (!buildingSiteData.DecorEntities.Any(x => x.PrefabName == entityData.PrefabName && x.Position == entityData.Position && x.Rotation == entityData.Rotation))
                            buildingSiteData.DecorEntities.Add(entityData);
                        continue;
                    }

                    BaseEntity entity = collider.ToBaseEntity();
                    if (entity == null || entity is BasePlayer or BaseAnimalNPC or BaseCorpse or CCTV_RC or ReactiveTarget)
                        continue;

                    if (entity is DoorCloser)
                    {
                        Vector3 position = GetPosition(entity.transform).ToVector3();
                        position.y = 0;
                        buildingSiteData.LandscapeCheckPositions.Add(position.ToString());
                        continue;
                    }

                    if (entity is LootContainer && !entity.ShortPrefabName.Contains("roadsign"))
                    {
                        string prefabName = entity.PrefabName;
                        CrateConfig crateConfig = _ins._config.CrateConfigs.FirstOrDefault(x => x.PrefabName == prefabName);

                        Debug(crateConfig == null ? prefabName : crateConfig.PresetName, GetPosition(entity.transform), GetRotation(entity.transform));
                    }
                    else
                    {
                        EntityData entityData = new EntityData();
                        entityData.PrefabName = entity.PrefabName;
                        entityData.Skin = entity.skinID;
                        entityData.Position = GetPosition(entity.transform);
                        entityData.Rotation = GetRotation(entity.transform);

                        if ((entity is NPCDwelling or ElectricBattery or WaterPurifier or SolarPanel or SimpleBuildingBlock or Barricade or CargoShipContainer) || entity.ShortPrefabName == "door.hinged.shipping_container" || entity.ShortPrefabName.Contains("roadsign") || entity.ShortPrefabName.Contains("furnace"))
                        {
                            if (!buildingSiteData.DecorEntities.Any(x => x.PrefabName == entityData.PrefabName && x.Position == entityData.Position && x.Rotation == entityData.Rotation))
                                buildingSiteData.DecorEntities.Add(entityData);
                        }
                        else
                        {
                            if (!buildingSiteData.RegularEntities.Any(x => x.PrefabName == entityData.PrefabName && x.Position == entityData.Position && x.Rotation == entityData.Rotation))
                                buildingSiteData.RegularEntities.Add(entityData);
                        }
                    }
                }
            }

            private static string GetBuildingBlockPrefabNameAndConditionModel(string colliderName, out int conditionModel)
            {
                conditionModel = 0;

                if (colliderName.Contains("foundation"))
                {
                    if (colliderName.Contains("triangle"))
                        return "assets/prefabs/building core/foundation.triangle/foundation.triangle.prefab";
                    else if (colliderName.Contains("steps"))
                        return "assets/prefabs/building core/foundation.steps/foundation.steps.prefab";
                    else
                        return "assets/prefabs/building core/foundation/foundation.prefab";
                }
                else if (colliderName.Contains("ramp"))
                {
                    return "assets/prefabs/building core/ramp/ramp.prefab";
                }
                else if (colliderName.Contains("floor"))
                {
                    if (colliderName.Contains("frame"))
                    {
                        if (colliderName.Contains("triangle"))
                            return "assets/prefabs/building core/floor.triangle.frame/floor.triangle.frame.prefab";
                        else
                            return "assets/prefabs/building core/floor.frame/floor.frame.prefab";
                    }
                    else if (colliderName.Contains("triangle"))
                        return "assets/prefabs/building core/floor.triangle/floor.triangle.prefab";
                    else
                        return "assets/prefabs/building core/floor/floor.prefab";
                }
                else if (colliderName.Contains("wall"))
                {
                    if (colliderName.Contains("doorway"))
                        return "assets/prefabs/building core/wall.doorway/wall.doorway.prefab";
                    else if (colliderName.Contains("window"))
                        return "assets/prefabs/building core/wall.window/wall.window.prefab";
                    else if (colliderName.Contains("frame"))
                        return "assets/prefabs/building core/wall.frame/wall.frame.prefab";
                    else if (colliderName.Contains("half"))
                        return "assets/prefabs/building core/wall.half/wall.half.prefab";
                    else if (colliderName.Contains("low"))
                        return "assets/prefabs/building core/wall.low/wall.low.prefab";
                    else
                    {
                        if (colliderName.Contains("left"))
                            conditionModel = 2;
                        else if (colliderName.Contains("right"))
                            conditionModel = 4;

                        return "assets/prefabs/building core/wall/wall.prefab";
                    }
                }
                else if (colliderName.Contains("stair"))
                {
                    if (colliderName.Contains("spiral"))
                    {
                        if (colliderName.Contains("triangle"))
                            return "assets/prefabs/building core/stairs.spiral.triangle/block.stair.spiral.triangle.prefab";
                        else
                            return "assets/prefabs/building core/stairs.spiral/block.stair.spiral.prefab";
                    }
                    else if (colliderName.Contains("ushape"))
                        return "assets/prefabs/building core/stairs.u/block.stair.ushape.prefab";
                    else if (colliderName.Contains("lshape"))
                        return "assets/prefabs/building core/stairs.l/block.stair.lshape.prefab";
                }
                else if (colliderName.Contains("roof"))
                {
                    if (colliderName.Contains("triangle"))
                        return "assets/prefabs/building core/roof.triangle/roof.triangle.prefab";
                    else
                        return "assets/prefabs/building core/roof/roof.prefab";
                }

                return null;
            }

            private static int GetBuildingBlockGradeAndSkin(string colliderName, out ulong skin)
            {
                skin = 0;

                if (colliderName.Contains("wood"))
                {
                    return 1;
                }
                if (colliderName.Contains("frontier"))
                {
                    skin = 10232;
                    return 1;
                }

                if (colliderName.Contains("stone"))
                {
                    return 2;
                }
                if (colliderName.Contains("adobe"))
                {
                    skin = 10220;
                    return 2;
                }
                if (colliderName.Contains("brick"))
                {
                    skin = 10223;
                    return 2;
                }
                if (colliderName.Contains("brutalist"))
                {
                    skin = 10225;
                    return 2;
                }
                if (colliderName.Contains("jungle"))
                {
                    skin = 10326;
                    return 2;
                }

                if (colliderName.Contains("metal"))
                {
                    return 3;
                }
                if (colliderName.Contains("container"))
                {
                    skin = 10221;
                    return 3;
                }
                if (colliderName.Contains("toptier"))
                {
                    return 4;
                }

                return 0;
            }

            private static string GetPosition(Transform transform)
            {
                Vector3 localPosition = transform.position - LocationCenterPosition;
                return $"({localPosition.x}, {localPosition.y}, {localPosition.z})";
            }

            private static string GetRotation(Transform transform)
            {
                return transform.eulerAngles.ToString();
            }
        }

        private static class NotifyManagerLite
        {
            public static void PrintInfoMessage(BasePlayer player, string langKey, params object[] args)
            {
                if (player == null)
                    _ins.PrintWarning(ClearColorAndSize(GetMessage(langKey, null, args)));
                else
                    _ins.PrintToChat(player, GetMessage(langKey, player.UserIDString, args));
            }

            public static void PrintError(BasePlayer player, string langKey, params object[] args)
            {
                if (player == null)
                    _ins.PrintError(ClearColorAndSize(GetMessage(langKey, null, args)));
                else
                    _ins.PrintToChat(player, GetMessage(langKey, player.UserIDString, args));
            }

            public static void PrintLogMessage(string langKey, params object[] args)
            {
                for (int i = 0; i < args.Length; i++)
                    if (args[i] is int)
                        args[i] = GetTimeMessage(null, (int)args[i]);

                _ins.Puts(ClearColorAndSize(GetMessage(langKey, null, args)));
            }

            public static void PrintWarningMessage(string langKey, params object[] args)
            {
                _ins.PrintWarning(ClearColorAndSize(GetMessage(langKey, null, args)));
            }

            private static string ClearColorAndSize(string message)
            {
                message = message.Replace("</color>", string.Empty);
                message = message.Replace("</size>", string.Empty);
                while (message.Contains("<color="))
                {
                    int index = message.IndexOf("<color=");
                    message = message.Remove(index, message.IndexOf(">", index) - index + 1);
                }
                while (message.Contains("<size="))
                {
                    int index = message.IndexOf("<size=");
                    message = message.Remove(index, message.IndexOf(">", index) - index + 1);
                }
                return message;
            }

            public static void SendMessageToAll(string langKey, params object[] args)
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                    if (player != null)
                        SendMessageToPlayer(player, langKey, args);
            }

            public static void SendMessageToPlayer(BasePlayer player, string langKey, params object[] args)
            {
                object[] argsClone = new object[args.Length];

                for (int i = 0; i < args.Length; i++)
                    argsClone[i] = args[i];

                for (int i = 0; i < argsClone.Length; i++)
                    if (argsClone[i] is int)
                        argsClone[i] = GetTimeMessage(player.UserIDString, (int)argsClone[i]);

                RedefinedMessageConfig redefinedMessageConfig = GetRedefinedMessageConfig(langKey);

                if (redefinedMessageConfig != null && !redefinedMessageConfig.IsEnable)
                    return;

                string playerMessage = GetMessage(langKey, player.UserIDString, args);

                if (redefinedMessageConfig != null)
                    SendMessage(redefinedMessageConfig, player, playerMessage);
                else
                    SendMessage(_ins._config.NotifyConfig, player, playerMessage);
            }

            private static void SendMessage(BaseMessageConfig baseMessageConfig, BasePlayer player, string playerMessage)
            {
                if (baseMessageConfig.ChatConfig.IsEnabled)
                    _ins.PrintToChat(player, playerMessage);

                if (baseMessageConfig.GameTipConfig.IsEnabled)
                    player.SendConsoleCommand("gametip.showtoast", baseMessageConfig.GameTipConfig.Style, ClearColorAndSize(playerMessage), string.Empty);
            }

            private static RedefinedMessageConfig GetRedefinedMessageConfig(string langKey)
            {
                return _ins._config.NotifyConfig.RedefinedMessages.FirstOrDefault(x => x.LangKey == langKey);
            }

            private static string GetTimeMessage(string userIDString, int seconds)
            {
                string message = "";

                TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
                if (timeSpan.Hours > 0) message += $" {timeSpan.Hours} {GetMessage("Hours", userIDString)}";
                if (timeSpan.Minutes > 0) message += $" {timeSpan.Minutes} {GetMessage("Minutes", userIDString)}";
                if (message == "") message += $" {timeSpan.Seconds} {GetMessage("Seconds", userIDString)}";

                return message;
            }
        }

        private static class PositionDefiner
        {
            public static Vector3 GetLocalPosition(Transform parentTransform, Vector3 globalPosition)
            {
                return parentTransform.InverseTransformPoint(globalPosition);
            }

            public static Vector3 GetLocalRotation(Transform parentTransform, Quaternion globalRotation)
            {
                Quaternion localRotation = Quaternion.Inverse(parentTransform.rotation) * globalRotation;
                return localRotation.eulerAngles;
            }

            public static Vector3 GetLocalRotation(Transform parentTransform, Vector3 globalEuler)
            {
                Quaternion globalRotation = Quaternion.Euler(globalEuler);
                Quaternion localRotation = Quaternion.Inverse(parentTransform.rotation) * globalRotation;
                return localRotation.eulerAngles;
            }

            public static Vector3 GetGlobalPosition(Transform parentTransform, LocationData localLocationData, Vector3 offset)
            {
                Vector3 localPosition = localLocationData.Position.ToVector3() + offset;
                return GetGlobalPosition(parentTransform, localPosition);
            }

            public static Vector3 GetGlobalPosition(Transform parentTransform, Vector3 localPosition)
            {
                return parentTransform.transform.TransformPoint(localPosition);
            }

            public static Quaternion GetGlobalRotation(Transform parentTransform, LocationData localLocationData)
            {
                Vector3 localRotation = localLocationData.Rotation.ToVector3();
                return GetGlobalRotation(parentTransform, localRotation);
            }

            public static Quaternion GetGlobalRotation(Transform parentTransform, Vector3 rotation)
            {
                return parentTransform.rotation * Quaternion.Euler(rotation);
            }

            public static Vector3 GetRandomMapPoint()
            {
                Vector2 randomVector2 = World.Size * 0.6f * UnityEngine.Random.insideUnitCircle;
                Vector3 randomVector3 = PositionDefiner.GetGroundPosition(new Vector3(randomVector2.x, 0, randomVector2.y));
                return randomVector3;
            }

            public static Vector3 GetGroundPosition(Vector3 position, int layerMask = 1 << 16 | 1 << 23)
            {
                position.y = 400;
                RaycastHit raycastHit;

                if (Physics.Raycast(position, Vector3.down, out raycastHit, 500, layerMask))
                    position.y = raycastHit.point.y;
                else
                    position.y = 0;

                return position;
            }
            
            public static bool GetNavmeshInPoint(Vector3 position, float radius, out NavMeshHit navMeshHit)
            {
                return NavMesh.SamplePosition(position, out navMeshHit, radius, 1);
            }

            public static BaseEntity RaycastAll<T>(Ray ray, float distance = 50) where T : BaseEntity
            {
                RaycastHit[] hits = Physics.RaycastAll(ray);
                GamePhysics.Sort(hits);
                BaseEntity target = null;

                foreach (RaycastHit hit in hits)
                {
                    BaseEntity ent = hit.GetEntity();

                    if (ent is T && hit.distance < distance)
                    {
                        target = ent;
                        break;
                    }
                }

                return target;
            }

            public static bool IsPositionOnCargoPath(Vector3 position)
            {
                foreach (CargoShip.HarborInfo harborInfo in CargoShip.harbors)
                {
                    IAIPathNode pathNode = harborInfo.harborPath.GetClosestToPoint(position);

                    if (Vector3.Distance(pathNode.Position, position) < 90)
                        return true;
                }

                float distanceToCargoPath = GetDistanceToCargoPath(position);
                return distanceToCargoPath < 100;
            }

            private static float GetDistanceToCargoPath(Vector3 position)
            {
                int index = GetNearIndexPathCargo(position);
                int indexNext = TerrainMeta.Path.OceanPatrolFar.Count - 1 == index ? 0 : index + 1;
                int indexPrevious = index == 0 ? TerrainMeta.Path.OceanPatrolFar.Count - 1 : index - 1;
                float distanceNext = GetDistanceToCargoPath(position, index, indexNext);
                float distancePrevious = GetDistanceToCargoPath(position, indexPrevious, index);
                return distanceNext < distancePrevious ? distanceNext : distancePrevious;
            }

            private static int GetNearIndexPathCargo(Vector3 position)
            {
                int index = 0;
                float distance = float.MaxValue;

                for (int i = 0; i < TerrainMeta.Path.OceanPatrolFar.Count; i++)
                {
                    Vector3 vector3 = TerrainMeta.Path.OceanPatrolFar[i];
                    float single = Vector3.Distance(position, vector3);

                    if (single < distance)
                    {
                        index = i;
                        distance = single;
                    }
                }

                return index;
            }

            private static float GetDistanceToCargoPath(Vector3 position, int index1, int index2)
            {
                Vector3 pos1 = TerrainMeta.Path.OceanPatrolFar[index1];
                Vector3 pos2 = TerrainMeta.Path.OceanPatrolFar[index2];

                float distance1 = Vector3.Distance(position, pos1);
                float distance2 = Vector3.Distance(position, pos2);
                float distance12 = Vector3.Distance(pos1, pos2);

                float p = (distance1 + distance2 + distance12) / 2;

                return (2 / distance12) * (float)Math.Sqrt(p * (p - distance1) * (p - distance2) * (p - distance12));
            }
        }

        private static class PermissionManager
        {
            public static void RegisterPermissions()
            {
                foreach (SiteConfig baseMonumentConfig in _ins._config.WaterTypeConfig.Sites)
                    if (baseMonumentConfig.SummonConfig != null && !string.IsNullOrEmpty(baseMonumentConfig.SummonConfig.Permission))
                        _ins.permission.RegisterPermission(baseMonumentConfig.SummonConfig.Permission, _ins);

                foreach (SiteConfig baseMonumentConfig in _ins._config.CoastalTypeConfig.Sites)
                    if (baseMonumentConfig.SummonConfig != null && !string.IsNullOrEmpty(baseMonumentConfig.SummonConfig.Permission))
                        _ins.permission.RegisterPermission(baseMonumentConfig.SummonConfig.Permission, _ins);

                foreach (SiteConfig baseMonumentConfig in _ins._config.RoadTypeConfig.Sites)
                    if (baseMonumentConfig.SummonConfig != null && !string.IsNullOrEmpty(baseMonumentConfig.SummonConfig.Permission))
                        _ins.permission.RegisterPermission(baseMonumentConfig.SummonConfig.Permission, _ins);

                foreach (SiteConfig baseMonumentConfig in _ins._config.RiverTypeConfig.Sites)
                    if (baseMonumentConfig.SummonConfig != null && !string.IsNullOrEmpty(baseMonumentConfig.SummonConfig.Permission))
                        _ins.permission.RegisterPermission(baseMonumentConfig.SummonConfig.Permission, _ins);

                foreach (SiteConfig baseMonumentConfig in _ins._config.PrefabTypeConfig.Sites)
                    if (baseMonumentConfig.SummonConfig != null && !string.IsNullOrEmpty(baseMonumentConfig.SummonConfig.Permission))
                        _ins.permission.RegisterPermission(baseMonumentConfig.SummonConfig.Permission, _ins);
            }

            public static bool IsUserHavePermission(string userIdString, string permissionName)
            {
                return _ins.permission.UserHasPermission(userIdString, permissionName);
            }
        }
        #endregion Classes

        #region Data
        private readonly Dictionary<string, SiteData> _siteCustomizationData = new Dictionary<string, SiteData>();

        private readonly HashSet<string> _addonNames = new HashSet<string>
        {
            "junglePack"
        };

        private bool _isAddonJustInstalled;

        private bool TryLoadData()
        {
            foreach (string name in _addonNames)
            {
                AdditionData additionData = LoadDataFile<AdditionData>(name);
                if (additionData != null && additionData.GroundSites != null)
                    AddAddonToConfig(additionData);

                Interface.Oxide.DataFileSystem.DeleteDataFile($"{_ins.Name}/{name}");
            }

            _siteCustomizationData.Clear();

            foreach (SiteConfig monumentConfig in _config.WaterTypeConfig.Sites)
            {
                if (!TryLoadSiteDataFile(monumentConfig.DataFileName))
                    return false;
            }

            foreach (SiteConfig monumentConfig in _config.PrefabTypeConfig.Sites)
            {
                if (!TryLoadSiteDataFile(monumentConfig.DataFileName))
                    return false;
            }

            foreach (SiteConfig monumentConfig in _config.CoastalTypeConfig.Sites)
            {
                if (!TryLoadSiteDataFile(monumentConfig.DataFileName))
                    return false;
            }

            foreach (SiteConfig monumentConfig in _config.RiverTypeConfig.Sites)
            {
                if (!TryLoadSiteDataFile(monumentConfig.DataFileName))
                    return false;
            }

            foreach (SiteConfig monumentConfig in _config.RoadTypeConfig.Sites)
            {
                if (!TryLoadSiteDataFile(monumentConfig.DataFileName))
                    return false;
            }

            foreach (SiteConfig monumentConfig in _config.GroundTypeConfig.Sites)
            {
                if (!TryLoadSiteDataFile(monumentConfig.DataFileName))
                    return false;
            }

            _mapSaveData = LoadDataFile<MapSaveData>("mapSave");

            if (IsWipe() && _mapSaveData != null && !string.IsNullOrEmpty(_mapSaveData.MapName))
                _savedMonuments = new HashSet<MonumentSaveData>();
            else
                _savedMonuments = LoadDataFile<HashSet<MonumentSaveData>>("save");

            if (_savedMonuments == null)
                _savedMonuments = new HashSet<MonumentSaveData>();

            return true;
        }

        private void AddAddonToConfig(AdditionData additionData)
        {
            if (!IsAddonLocationAlreadyExist(additionData))
            {
                foreach (AddonCrateData addonCrateData in additionData.Crates)
                {
                    CrateConfig crateConfig = new CrateConfig
                    {
                        PresetName = addonCrateData.PresetName,
                        PrefabName = addonCrateData.PrefabName,
                        Skin = addonCrateData.Skin,
                        LootTableConfig = GetAddonLootTableConfig(addonCrateData.AddonLootDataData)
                    };

                    if (addonCrateData.PrefabName.Contains("woodbox_deployed") || addonCrateData.PrefabName.Contains("box.wooden.large") || addonCrateData.PrefabName.Contains("fridge.deployed"))
                        crateConfig.LootTableConfig.ClearDefaultItemList = false;

                    _config.CrateConfigs.Add(crateConfig);
                }

                foreach (AddonNpcData addonNpcData in additionData.Npcs)
                {
                    NpcConfig npcConfig = new NpcConfig
                    {
                        PresetName = addonNpcData.PresetName,
                        DisplayName = addonNpcData.DisplayName,
                        Health = addonNpcData.Health,
                        Kit = string.Empty,
                        WearItems = new List<NpcWear>(),
                        BeltItems = new List<NpcBelt>(),
                        Speed = 5f,
                        RoamRange = 10f,
                        ChaseRange = 50f,
                        AttackRangeMultiplier = addonNpcData.AttackRange,
                        AimConeScale = addonNpcData.AimConeScale,
                        DamageScale = addonNpcData.ScaleDamage,

                        SenseRange = 100f,
                        MemoryDuration = 10f,
                        CheckVisionCone = false,
                        VisionCone = 135f,
                        TurretDamageScale = 1f,
                        CanSleep = true,
                        SleepDistance = 100f,
                        DisableRadio = true,
                        DeleteCorpse = true,
                        LootTableConfig = GetAddonLootTableConfig(addonNpcData.AddonLootDataData)
                    };

                    foreach (AddonItemData itemData in addonNpcData.WearItems)
                    {
                        NpcWear npcWear = new NpcWear
                        {
                            ShortName = itemData.ShortName,
                            SkinID = itemData.Skin
                        };

                        npcConfig.WearItems.Add(npcWear);
                    }
                    foreach (AddonBeltItemData itemData in addonNpcData.BeltItems)
                    {
                        NpcBelt npcBelt = new NpcBelt
                        {
                            ShortName = itemData.ShortName,
                            SkinID = itemData.Skin,
                            Ammo = itemData.Ammo,
                            Amount = itemData.Amount,
                            Mods = itemData.Mods
                        };

                        npcConfig.BeltItems.Add(npcBelt);
                    }

                    _config.NpcConfigs.Add(npcConfig);
                }
            }

            foreach (AddonSiteData addonSiteData in additionData.GroundSites)
                AddSiteToConfig(addonSiteData, LocationType.Ground);

            foreach (AddonSiteData addonSiteData in additionData.WaterSites)
                AddSiteToConfig(addonSiteData, LocationType.Water);

            foreach (AddonSiteData addonSiteData in additionData.PrefabSites)
                AddSiteToConfig(addonSiteData, LocationType.Prefab);

            foreach (AddonSiteData addonSiteData in additionData.CoastalSites)
                AddSiteToConfig(addonSiteData, LocationType.Coastal);

            foreach (AddonSiteData addonSiteData in additionData.RoadSites)
                AddSiteToConfig(addonSiteData, LocationType.Road);

            foreach (AddonSiteData addonSiteData in additionData.RiverSites)
                AddSiteToConfig(addonSiteData, LocationType.River);

            PrintWarning("The addon has been successfully installed!");
            SaveConfig();
            _isAddonJustInstalled = true;
        }

        private bool IsAddonLocationAlreadyExist(AdditionData additionData)
        {
            if (_config.GroundTypeConfig.Sites.Any(x => additionData.GroundSites.Any(y => x.PresetName == y.PresetName)))
                return true;
            if (_config.WaterTypeConfig.Sites.Any(x => additionData.WaterSites.Any(y => x.PresetName == y.PresetName)))
                return true;
            if (_config.CoastalTypeConfig.Sites.Any(x => additionData.CoastalSites.Any(y => x.PresetName == y.PresetName)))
                return true;
            if (_config.RiverTypeConfig.Sites.Any(x => additionData.RiverSites.Any(y => x.PresetName == y.PresetName)))
                return true;
            if (_config.RoadTypeConfig.Sites.Any(x => additionData.RoadSites.Any(y => x.PresetName == y.PresetName)))
                return true;
            if (_config.PrefabTypeConfig.Sites.Any(x => additionData.PrefabSites.Any(y => x.PresetName == y.PresetName)))
                return true;

            return false;
        }

        private LootTableConfig GetAddonLootTableConfig(AddonLootTableData addonLootDataData)
        {
            return new LootTableConfig
            {
                ClearDefaultItemList = !string.IsNullOrEmpty(addonLootDataData.CratePrefabName),
                AlphaLootPresetName = "",
                PrefabConfigs = new PrefabLootTableConfigs
                {
                    IsEnable = !string.IsNullOrEmpty(addonLootDataData.CratePrefabName),
                    Prefabs = string.IsNullOrEmpty(addonLootDataData.CratePrefabName) ? new List<PrefabConfig>() : new List<PrefabConfig>
                    {
                        new PrefabConfig
                        {
                            PrefabName = addonLootDataData.CratePrefabName,
                            MinLootScale = addonLootDataData.LootScale,
                            MaxLootScale = addonLootDataData.LootScale
                        }
                    }
                },
                Items = new List<LootItemConfig>()
            };
        }

        private void AddSiteToConfig(AddonSiteData addonSiteData, LocationType locationType)
        {
            string siteDisplayName = En ? addonSiteData.EnDisplayName : addonSiteData.RuDisplayName;

            SiteConfig newSiteConfig = new SiteConfig
            {
                PresetName = addonSiteData.PresetName,
                DisplayName = siteDisplayName,
                DataFileName = addonSiteData.DataFileName,
                PrefabNames = addonSiteData.PrefabNames,
                MinRespawnTime = 1800,
                MaxRespawnTime = 3600,
                SummonConfig = addonSiteData.ItemSkinId == 0 ? null : new SummonMonumentConfig
                {
                    Shortname = "flare",
                    Name = siteDisplayName,
                    Skin = addonSiteData.ItemSkinId,
                    SpawnDescriptionLang = locationType switch
                    {
                        LocationType.Ground => "GroundLocation_Decription",
                        LocationType.Coastal => "ShoreLocation_Decription",
                        LocationType.Water => "WaterLocation_Decription",
                        LocationType.River => "River_Decription",
                        _ => "UnknownLocation_Description"
                    },
                    Permission = "",
                },
                EnableMarker = true,
                IsAutoSpawn = true,
                Probability = 100,
                Biomes = addonSiteData.Biomes,
                Radiation = addonSiteData.Radiation,
                Crates = new HashSet<PresetLocationConfig>(),
                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>()
            };

            foreach (AddonEntityLocationData crateData in addonSiteData.Crates)
            {
                PresetLocationConfig presetLocationConfig = new PresetLocationConfig
                {
                    PresetName = crateData.PresetOrPrefab,
                    Position = crateData.Position,
                    Rotation = crateData.Rotation
                };
                newSiteConfig.Crates.Add(presetLocationConfig);
            }

            foreach (AddonEntityLocationData entityData in addonSiteData.RespawnEntities)
            {
                PrefabLocationConfig presetLocationConfig = new PrefabLocationConfig
                {
                    PrefabName = entityData.PresetOrPrefab,
                    Position = entityData.Position,
                    Rotation = entityData.Rotation
                };
                newSiteConfig.RespawnEntities.Add(presetLocationConfig);
            }

            foreach (AddonNpcLocationData npcData in addonSiteData.MobileNpcs)
            {
                MovableNpcPresetLocationConfig movableNpcPreset = new MovableNpcPresetLocationConfig
                {
                    PresetName = npcData.PresetOrPrefab,
                    Position = npcData.Position,
                    NavMeshPresetName = npcData.NavMesh
                };
                newSiteConfig.CustomNavmeshNpc.Add(movableNpcPreset);
            }

            foreach (AddonEntityLocationData npcData in addonSiteData.StaticNpcs)
            {
                NpcPresetLocationConfig presetLocationConfig = new NpcPresetLocationConfig
                {
                    PresetName = npcData.PresetOrPrefab,
                    Position = npcData.Position
                };
                newSiteConfig.StaticNpcs.Add(presetLocationConfig);
            }

            if (locationType == LocationType.Ground && !_config.GroundTypeConfig.Sites.Any(x => x.PresetName == newSiteConfig.PresetName))
                _config.GroundTypeConfig.Sites.Add(newSiteConfig);
            if (locationType == LocationType.Water && !_config.WaterTypeConfig.Sites.Any(x => x.PresetName == newSiteConfig.PresetName))
                _config.WaterTypeConfig.Sites.Add(newSiteConfig);
            if (locationType == LocationType.Prefab && !_config.PrefabTypeConfig.Sites.Any(x => x.PresetName == newSiteConfig.PresetName))
                _config.PrefabTypeConfig.Sites.Add(newSiteConfig);
            if (locationType == LocationType.Coastal && !_config.CoastalTypeConfig.Sites.Any(x => x.PresetName == newSiteConfig.PresetName))
                _config.CoastalTypeConfig.Sites.Add(newSiteConfig);
            if (locationType == LocationType.River && !_config.RiverTypeConfig.Sites.Any(x => x.PresetName == newSiteConfig.PresetName))
                _config.RiverTypeConfig.Sites.Add(newSiteConfig);
            if (locationType == LocationType.Road && !_config.RoadTypeConfig.Sites.Any(x => x.PresetName == newSiteConfig.PresetName))
                _config.RoadTypeConfig.Sites.Add(newSiteConfig);
        }

        private bool TryLoadSiteDataFile(string path)
        {
            SiteData siteData = LoadDataFile<SiteData>($"{path}");

            if (siteData == null || siteData.BuildingBlocks == null)
                return false;

            if (siteData.ObstacleData == null && path is "depotGates_1" or "depotGates_2" or "rustedGates" or "rustedDam" or "stoneQuarryRuins" or "hqmQuarryRuins" || path is "stoneQuarryRuins" && siteData.ObstacleData.ObstacleColliders.Count <= 3 || path is "hqmQuarryRuins" && siteData.ObstacleData.ObstacleColliders.Count <= 4)
                NotifyManagerLite.PrintWarningMessage("DataFileOutdated_Exception", path);

            if (!_siteCustomizationData.ContainsKey(path))
                _siteCustomizationData.Add(path, siteData);

            return true;
        }

        private static TYpe LoadDataFile<TYpe>(string path)
        {
            string fullPath = $"{_ins.Name}/{path}";
            return Interface.Oxide.DataFileSystem.ReadObject<TYpe>(fullPath);
        }

        private static void SaveDataFile<TYpe>(TYpe objectForSaving, string path)
        {
            string fullPath = $"{_ins.Name}/{path}";
            Interface.Oxide.DataFileSystem.WriteObject(fullPath, objectForSaving);
        }

        private class SiteData
        {
            [JsonProperty("Offset")] 
            public string Offset { get; set; }
            
            [JsonProperty("Rotation")] 
            public string Rotation { get; set; }
            
            [JsonProperty("Internal radius")] 
            public float InternalRadius { get; set; }
            
            [JsonProperty("External radius")] 
            public float ExternalRadius { get; set; }
            
            [JsonProperty("Building Block")] 
            public bool IsBuildingBlocked { get; set; }
            
            [JsonProperty("Maximum upward deviation in height")] 
            public float MaxUpDeltaHeigh { get; set; }
            
            [JsonProperty("Maximum downward deviation in height")] 
            public float MaxDownDeltaHeigh { get; set; }
            
            [JsonProperty("Building blocks")] 
            public HashSet<BuildingBlockData> BuildingBlocks { get; set; }
            
            [JsonProperty("Regular Entities")] 
            public HashSet<EntityData> RegularEntities { get; set; }
            
            [JsonProperty("Decor Entities")] 
            public HashSet<EntityData> DecorEntities { get; set; }
            
            [JsonProperty("Painted Entities")] 
            public HashSet<PaintedEntityData> PaintedEntities { get; set; }
            
            [JsonProperty("Scaled Decor Entities")] 
            public HashSet<ScaledEntityDataOld> ScaledDecorEntitiesOld { get; set; }
            
            [JsonProperty("Scaled Entities")] 
            public HashSet<ScaledEntityData> ScaledEntities { get; set; }
            
            [JsonProperty("Wire Datas")] 
            public HashSet<WireData> WireDatas { get; set; }
            
            [JsonProperty("Card Doors")] 
            public HashSet<CardReaderData> CardDoors { get; set; }
            
            [JsonProperty("Custom Doors")] 
            public HashSet<CustomDoorData> CustomDoors { get; set; }
            
            [JsonProperty("Check Positions")] 
            public HashSet<string> CheckPositions { get; set; }
            
            [JsonProperty("Landscape Check Positions")] 
            public HashSet<string> LandscapeCheckPositions { get; set; }
            
            [JsonProperty("Fire Points")] 
            public HashSet<LocationData> FirePoints { get; set; }
            
            [JsonProperty("Elevators")] 
            public HashSet<ElevatorData> Elevators { get; set; }
            
            [JsonProperty("Random Doors")] 
            public HashSet<EntityData> RandomDoors { get; set; }
            
            [JsonProperty("NPC Obstacles")] 
            public NavmeshObstaclesData ObstacleData { get; set; }
        }

        private class BuildingBlockData : EntityData
        {
            [JsonProperty("Grade [0 - 4]", Order = 102)] 
            public int Grade { get; set; }
            
            [JsonProperty("Color", Order = 103)] 
            public uint Color { get; set; }
            
            [JsonProperty("Conditional Model", Order = 104)] 
            public int ConditionModel { get; set; }
        }

        private class ScaledEntityDataOld : EntityData
        {
            [JsonProperty("Scale", Order = 10)] 
            public float Scale { get; set; }
            
            [JsonProperty("Sphere Offset", Order = 11)] 
            public string SphereOffset { get; set; }
        }

        private class ScaledEntityData : EntityData
        {
            [JsonProperty("Scale", Order = 10)] 
            public float Scale { get; set; }
            
            [JsonProperty("Is regular", Order = 12)] 
            public bool IsRegular { get; set; }
            
            [JsonProperty("Child Entities", Order = 13)] 
            public HashSet<EntityData> ChildEntities { get; set; }
        }

        private class ElevatorData : LocationData
        {
            [JsonProperty("Number of floors")] 
            public int NumberOfFloors { get; set; }
            
            [JsonProperty("Floor Height")] 
            public float FloorHeight { get; set; }
            
            [JsonProperty("Buttons")] 
            public HashSet<LocationData> Buttons { get; set; }
        }

        private class PaintedEntityData : EntityData
        {
            [JsonProperty("Image Name")] 
            public string ImageName { get; set; }
        }

        private class EntityData : LocationData
        {
            [JsonProperty("Prefab")] 
            public string PrefabName { get; set; }
            
            [JsonProperty("Skin")] 
            public ulong Skin { get; set; }
        }


        private class BoxColliderData : LocationData
        {
            [JsonProperty("Size")] 
            public string Size { get; set; }
        }

        private class NavmeshObstaclesData
        {
            [JsonProperty("DoorCloser Position")] 
            public string DoorCloserPosition { get; set; }
            
            [JsonProperty("Obstacle Locations")] 
            public HashSet<BoxColliderData> ObstacleColliders { get; set; }
        }

        private class LocationData
        {
            [JsonProperty("Position", Order = 100)] 
            public string Position { get; set; }
            
            [JsonProperty("Rotation", Order = 101)] 
            public string Rotation { get; set; }
        }

        private class WireData
        {
            [JsonProperty("Start Position")] public string StartPosition { get; set; }
            [JsonProperty("End Position")] public string EndPosition { get; set; }
        }

        private class CustomDoorData
        {
            [JsonProperty("Buttons")] 
            public HashSet<EntityData> Buttons { get; set; }
            
            [JsonProperty("Child Entities")] 
            public HashSet<EntityData> ChildEntities { get; set; }
            
            [JsonProperty("Start Location")] 
            public LocationData StartPosition { get; set; }
            
            [JsonProperty("End Position")] 
            public string EndPosition { get; set; }
            
            [JsonProperty("Train Passage Controller")] 
            public BoxColliderData TrainPassTriggerData { get; set; }
        }

        private class CardReaderData
        {
            [JsonProperty("Fuse Location")] 
            public LocationData FuseLocation { get; set; }
            
            [JsonProperty("Card Reader Location")] 
            public LocationData CardReaderLocation { get; set; }
            
            [JsonProperty("Card Reader Type")]
            public int CardReaderType { get; set; }
            
            [JsonProperty("Doors")] 
            public HashSet<EntityData> Doors { get; set; }
            
            [JsonProperty("Buttons")] 
            public HashSet<LocationData> Buttons { get; set; }
        }


        private HashSet<MonumentSaveData> _savedMonuments = new HashSet<MonumentSaveData>();

        private class MonumentSaveData
        {
            public string Position;
            public string Rotation;
            public string MonumentPreset;
            public ulong OwnerId;
        }


        private MapSaveData _mapSaveData;

        private class MapSaveData
        {
            public string MapName;
            public readonly HashSet<PrefabSavedData> SavedPrefabs = new HashSet<PrefabSavedData>();
        }

        private class PrefabSavedData
        {
            public string PrefabName;
            public string Position;
            public string EulerAngels;
        }

        private class AdditionData
        {
            public HashSet<AddonSiteData> GroundSites { get; set; }
            public HashSet<AddonSiteData> CoastalSites { get; set; }
            public HashSet<AddonSiteData> PrefabSites { get; set; }
            public HashSet<AddonSiteData> WaterSites { get; set; }
            public HashSet<AddonSiteData> RiverSites { get; set; }
            public HashSet<AddonSiteData> RoadSites { get; set; }
            public HashSet<AddonCrateData> Crates { get; set; }
            public HashSet<AddonNpcData> Npcs { get; set; }
        }

        private class AddonSiteData
        {
            public string PresetName { get; set; }
            public string EnDisplayName { get; set; }
            public string RuDisplayName { get; set; }
            public string DataFileName { get; set; }
            public HashSet<string> PrefabNames { get; set; }
            public ulong ItemSkinId { get; set; }
            public HashSet<string> Biomes { get; set; }
            public float Radiation { get; set; }
            public HashSet<AddonEntityLocationData> Crates { get; set; }
            public HashSet<AddonEntityLocationData> RespawnEntities { get; set; }
            public HashSet<AddonNpcLocationData> MobileNpcs { get; set; }
            public HashSet<AddonEntityLocationData> StaticNpcs { get; set; }
        }

        private class AddonNpcLocationData
        {
            public string PresetOrPrefab { get; set; }
            public string Position { get; set; }
            public string NavMesh { get; set; }
        }

        private class AddonEntityLocationData
        {
            public string PresetOrPrefab { get; set; }
            public string Position { get; set; }
            public string Rotation { get; set; }
        }

        private class AddonCrateData
        {
            public string PresetName { get; set; }
            public string PrefabName { get; set; }
            public ulong Skin { get; set; }
            public AddonLootTableData AddonLootDataData { get; set; }
        }

        private class AddonNpcData
        {
            public string PresetName { get; set; }
            public string DisplayName { get; set; }
            public float Health { get; set; }
            public HashSet<AddonItemData> WearItems { get; set; }
            public HashSet<AddonBeltItemData> BeltItems { get; set; }
            public float AttackRange { get; set; }
            public float ScaleDamage { get; set; }
            public float AimConeScale { get; set; }
            public AddonLootTableData AddonLootDataData { get; set; }
        }

        private class AddonBeltItemData : AddonItemData
        {
            public int Amount { get; set; }
            public List<string> Mods { get; set; }
            public string Ammo { get; set; }
        }

        private class AddonItemData
        {
            public string ShortName { get; set; }
            public ulong Skin { get; set; }
        }

        private class AddonLootTableData
        {
            public string CratePrefabName { get; set; }
            public int LootScale { get; set; }
        }
        #endregion Data

        #region Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["ConfigNotFound_Exeption"] = "Конфигурация не найдена! ({0})",
                ["PresetNotFound_Exeption"] = "{0} <color=#ce3f27>Пресет</color> не найден! ({1})",
                ["CantSpawnPrefabLocation_Exeption"] = "<color=#ce3f27>Не удалось</color> найти подходяший объект для спавна локации! ({0})",
                ["CantFindPosition_Exeption"] = "<color=#ce3f27>Не удалось</color> найти подходящую позицию для спавна локации! ({0})",
                ["MonumentNotFound_Exception"] = "{0} <color=#ce3f27>Не удалось</color> найти монумент поблизости!",
                ["EntityDelete_Exception"] = "{0} <color=#ce3f27>Не удалось</color> удалить entity!",

                ["NoPermission"] = "У вас нет разрешения!",
                ["GotMonument"] = "Вы получили монумент!",
                ["Spawn_Start"] = "Начался спавн локации!",

                ["GroundLocation_Decription"] = "{0} Найдите <color=#738d43>плоскую поверхность</color> и бросьте флаер на землю",
                ["ShoreLocation_Decription"] = "{0} Зайдите в воду на глубину около <color=#738d43>2м</color>! Выбирайте локацию подальше от больших <color=#ce3f27>рифов</color> и <color=#ce3f27>айсбергов</color>!",
                ["WaterLocation_Decription"] = "{0} Заплывите на <color=#738d43>глубину</color> и найдите место вдали от маршрута <color=#ce3f27>карго</color>!",
                ["PowerLines_Decription"] = "{0} Найдите опору ЛЭП с <color=#738d43>зиплайн-станцией</color>!",
                ["River_Decription"] = "{0} Ищите место на реке в <color=#738d43>равнинной</color> местности!",
                ["BusStop_Decription"] = "{0} Найдите <color=#738d43>автобусную</color> остановку!",

                ["UnsuitableLandscape_BlockSpawn"] = "Неподходящий ландшафт!",
                ["WrongPlace_BlockSpawn"] = "Неподходящее место!",
                ["RoadOrRail_BlockSpawn"] = "Слишком близко к дороге!",
                ["WrongBiome_BlockSpawn"] = "Неподходящий биом!",
                ["CloseMonument_BlockSpawn"] = "Слишком близко к другому монументу!",
                ["ObjectBlocks_BlockSpawn"] = "Объект блокирует размещение монумента!",
                ["PlayerBlocks_BlockSpawn"] = "Игрок блокирует размещение монумента!",

                ["ObjectNotFound_BlockSpawn"] = "Объект для размещения монумента не найден!",
                ["FarShore_BlockSpawn"] = "Слишком далеко от берега!",
                ["InsufficientDepth_BlockSpawn"] = "Недостаточная глубина!",
                ["CargoBlock_BlockSpawn"] = "Слишком близко к маршруту карго!",

                ["Position_Suitable"] = "Киньте флаер в свою позицию и отступите на {0} метров!",
                ["SpawnPosAdded"] = "Позиция для спавна пресета успешно добавлена!",

            }, this, "ru");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["DataFileOutdated_Exception"] = "The data file is outdated! Replace it with a new one! ({0})",
                ["ConfigNotFound_Exeption"] = "Configuration not found! ({0})",
                ["PresetNotFound_Exeption"] = "{0} <color=#ce3f27>Preset</color> not found! ({1})",
                ["DataNotFound_Exeption"] = "Data files were not found, or are corrupted. Move the contents of the data folder from the archive to the oxide/data folder on your server!",
                ["CantSpawnPrefabLocation_Exeption"] = "<color=#ce3f27>Couldn't find</color> a suitable object for spawn monunent! ({0})",
                ["CantFindPosition_Exeption"] = "<color=#ce3f27>Couldn't find</color> a suitable position to spawn the monunent! ({0})",
                ["MonumentNotFound_Exception"] = "{0} <color=#ce3f27>Failed</color> to find a monument nearby!",
                ["EntityDelete_Exception"] = "{0} <color=#ce3f27>Failed</color> to delete the entity!",

                ["SpawnStart_Log"] = "The spawn of monuments has begun!",
                ["SpawnStop_Log"] = "The spawn has ended!",
                ["MonumentSpawn_Log"] = "{0} is spawned at grid {1}",

                ["NoPermission"] = "You don't have permission!",
                ["GotMonument"] = "You've got a monument!",
                ["Spawn_Start"] = "The spawn has begun!",

                ["GroundLocation_Decription"] = "{0} Find a <color=#738d43>flat surface</color> and throw the flare to the ground",
                ["ShoreLocation_Decription"] = "{0} Go into the water to a depth of about <color=#738d43>2m</color>! Choose a location away from large <color=#ce3f27>reefs</color> and <color=#ce3f27>icebergs</color>!",
                ["WaterLocation_Decription"] = "{0} Swim out to <color=#738d43>deeper</color> waters and find a spot away from the <color=#ce3f27>cargo ship</color> route!",
                ["PowerLines_Decription"] = "{0} Look for the <color=#738d43>power line support</color> with the zipline platform!",
                ["River_Decription"] = "{0} Look for a spot on the river in a <color=#738d43>flat</color> area!",
                ["BusStop_Decription"] = "{0} Find the <color=#738d43>bus</color> stop",

                ["UnsuitableLandscape_BlockSpawn"] = "Unsuitable landscape!",
                ["WrongPlace_BlockSpawn"] = "The wrong place!",
                ["RoadOrRail_BlockSpawn"] = "Too close to the road or railway!",
                ["WrongBiome_BlockSpawn"] = "Wrong biome!",
                ["CloseMonument_BlockSpawn"] = "Too close to another monument!",
                ["ObjectBlocks_BlockSpawn"] = "An object is blocking the placement of the monument!",
                ["PlayerBlocks_BlockSpawn"] = "A player is blocking the placement of the monument!",

                ["ObjectNotFound_BlockSpawn"] = "No suitable object found for monument placement!",
                ["FarShore_BlockSpawn"] = "Too far from the shore!",
                ["InsufficientDepth_BlockSpawn"] = "Insufficient depth!",
                ["CargoBlock_BlockSpawn"] = "Too close to the cargo ship's route!",

                ["Position_Suitable"] = "Throw the flyer to your position and run {0} meters away!",
                ["SpawnPosAdded"] = "The spawn position has been successfully added!",

            }, this);
        }

        private static string GetMessage(string langKey, string userID)
        {
            return _ins.lang.GetMessage(langKey, _ins, userID);
        }

        private static string GetMessage(string langKey, string userID, params object[] args)
        {
            return (args.Length == 0) ? GetMessage(langKey, userID) : string.Format(GetMessage(langKey, userID), args);
        }
        #endregion Lang

        #region Configs
        private PluginConfig _config;

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.DefaultConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>();
            Config.WriteObject(_config, true);
        }
        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        private class MainConfig
        {
            [JsonProperty(En ? "Recreate locations on every plugin restart [true/false]" : "Создавать локации заново при перезагрузке плагина [true/false]")]
            public bool IsRespawnLocation { get; set; }

            [JsonProperty(En ? "The maximum number of locations that one player can summon (-1 - not limited)" : "Максимальное количество локаций, которое может вызвать один игрок (-1 - не ограничивать)")]
            public int MaxLocationNumberPerPlayer { get; set; }

            [JsonProperty(En ? "Enable monument spawn logging [true/false]" : "Включить логирование спавна монументов [true/false]")]
            public bool IsSpawnLogging { get; set; }

            [JsonProperty(En ? "Allow players to draw on signs? [true/false]" : "Разрешить игрокам рисовать на табличках? [true/false]")]
            public bool AllowDrawOnSigns { get; set; }

            [JsonProperty(En ? "Disable recycler spawning? [true/false]" : "Отключить спавн переработчиков? [true/false]")]
            public bool DisableRecyclers { get; set; }
        }


        private class MonumentTypeConfig
        {
            [JsonProperty(En ? "Allow automatic spawn? [true/false]" : "Разрешить автоматический спавн? [true/false]")]
            public bool IsAutoSpawn { get; set; }

            [JsonProperty(En ? "The minimum number of locations" : "Минимальное количество локаций этого типа", Order = 1)]
            public int MinAmount { get; set; }

            [JsonProperty(En ? "The maximum number of locations" : "Максимальное количество локаций этого типа", Order = 2)]
            public int MaxAmount { get; set; }

            [JsonProperty(En ? "List of locations" : "Список локаций", Order = 103)]
            public HashSet<SiteConfig> Sites { get; set; }
        }

        private class SiteConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Name" : "Название")]
            public string DisplayName { get; set; }

            [JsonProperty(En ? "Data file Name" : "Название дата файла")]
            public string DataFileName { get; set; }

            [JsonProperty(En ? "Prefab Names" : "Названия префабов", NullValueHandling = NullValueHandling.Ignore)]
            public HashSet<string> PrefabNames { get; set; }

            [JsonProperty(En ? "Minimum time between Loot/NPC respawn" : "Минимальное время между респавном лута/нпс [sec]")]
            public int MinRespawnTime { get; set; }

            [JsonProperty(En ? "Maximum time between Loot/NPC respawn" : "Максимальное время между респавном лута/нпс [sec]")]
            public int MaxRespawnTime { get; set; }

            [JsonProperty(En ? "Setting up the summoning of a monument by the player" : "Настрока призыва монумента игроком", NullValueHandling = NullValueHandling.Ignore)]
            public SummonMonumentConfig SummonConfig { get; set; }

            [JsonProperty(En ? "Enable marker for this monument? [true/false]" : "Включить маркер для этого монумента? [true/false]")]
            public bool EnableMarker { get; set; }

            [JsonProperty(En ? "Allow automatic spawn? [true/false]" : "Разрешить автоматический спавн? [true/false]")]
            public bool IsAutoSpawn { get; set; }

            [JsonProperty(En ? "Probability " : "Вероятность спавна")]
            public float Probability { get; set; }

            [JsonProperty(En ? "Number of monuments that will spawn additionally (Does not depend on the previous two parameters)" : "Количество монументов, которые будут появляться дополнительно (Не зависит от предыдущих двух параметров)")]
            public int AdditionalCount { get; set; }

            [JsonProperty(En ? "Biomes for automatic location spawn (Arid, Temperate, Tundra, Arctic)" : "Биомы для автоматического спавна локации (Arid, Temperate, Tundra, Arctic)")]
            public HashSet<string> Biomes { get; set; }

            [JsonProperty(En ? "Radiation power" : "Сила радиации")]
            public float Radiation { get; set; }

            [JsonProperty(En ? "Crates" : "Ящики")]
            public HashSet<PresetLocationConfig> Crates { get; set; }

            [JsonProperty(En ? "Entities for respawn" : "Прочие объекты для респавна")]
            public HashSet<PrefabLocationConfig> RespawnEntities { get; set; }

            [JsonProperty(En ? "Static NPCs" : "Статичные НПС")]
            public HashSet<NpcPresetLocationConfig> StaticNpcs { get; set; }

            [JsonProperty(En ? "Regular Npcs" : "Подвижные NPC")]
            public HashSet<MovableNpcPresetLocationConfig> CustomNavmeshNpc { get; set; }
            
            [JsonProperty(En ? "Ground NPCs" : "Наземные НПС", NullValueHandling = NullValueHandling.Ignore)]
            public HashSet<NpcPresetLocationConfig> GroudNpcs { get; set; }
        }


        private class MovableNpcPresetLocationConfig : NpcPresetLocationConfig
        {
            [JsonProperty(En ? "NavMesh preset" : "Пресет навигационной сетки", Order = 100)] 
            public string NavMeshPresetName { get; set; }
        }

        private class NpcPresetLocationConfig
        {
            [JsonProperty(En ? "Preset name" : "Название пресета")] 
            public string PresetName { get; set; }
            
            [JsonProperty(En ? "Position" : "Позиция")] 
            public string Position { get; set; }
        }

        private class PresetLocationConfig : LocationConfig
        {
            [JsonProperty(En ? "Preset name" : "Название пресета")] 
            public string PresetName { get; set; }
        }

        private class PrefabLocationConfig : LocationConfig
        {
            [JsonProperty(En ? "Prefab name" : "Название префаба")] 
            public string PrefabName { get; set; }
        }

        private class LocationConfig
        {
            [JsonProperty(En ? "Position" : "Позиция")] 
            public string Position { get; set; }
            
            [JsonProperty(En ? "Rotation" : "Вращение")] 
            public string Rotation { get; set; }
        }


        private class NpcConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")] 
            public string PresetName { get; set; }
            
            [JsonProperty(En ? "Name" : "Название")] 
            public string DisplayName { get; set; }
            
            [JsonProperty(En ? "Health" : "Кол-во ХП")] 
            public float Health { get; set; }
            
            [JsonProperty("Kit")] 
            public string Kit { get; set; }
            
            [JsonProperty(En ? "Wear items" : "Одежда")] 
            public List<NpcWear> WearItems { get; set; }
            
            [JsonProperty(En ? "Belt items" : "Быстрые слоты")] 
            public List<NpcBelt> BeltItems { get; set; }
            
            [JsonProperty(En ? "Speed" : "Скорость")] 
            public float Speed { get; set; }
            
            [JsonProperty(En ? "Roam Range" : "Дальность патрулирования местности")] 
            public float RoamRange { get; set; }
            
            [JsonProperty(En ? "Chase Range" : "Дальность погони за целью")] 
            public float ChaseRange { get; set; }
            
            [JsonProperty(En ? "Attack Range Multiplier" : "Множитель радиуса атаки")] 
            public float AttackRangeMultiplier { get; set; }
            
            [JsonProperty(En ? "Sense Range" : "Радиус обнаружения цели")] 
            public float SenseRange { get; set; }
            
            [JsonProperty(En ? "Memory duration [sec.]" : "Длительность памяти цели [sec.]")] 
            public float MemoryDuration { get; set; }
            
            [JsonProperty(En ? "Scale damage" : "Множитель урона")] 
            public float DamageScale { get; set; }
            
            [JsonProperty(En ? "Aim Cone Scale" : "Множитель разброса")] 
            public float AimConeScale { get; set; }
            
            [JsonProperty(En ? "Detect the target only in the NPC's viewing vision cone?" : "Обнаруживать цель только в углу обзора NPC? [true/false]")] 
            public bool CheckVisionCone { get; set; }
            
            [JsonProperty(En ? "Vision Cone" : "Угол обзора")] 
            public float VisionCone { get; set; }
            
            [JsonProperty(En ? "Turret damage scale" : "Множитель урона от турелей")] 
            public float TurretDamageScale { get; set; }
            
            [JsonProperty(En ? "Disable radio effects? [true/false]" : "Отключать эффекты рации? [true/false]")] 
            public bool DisableRadio { get; set; }
            
            [JsonProperty(En ? "Should remove the corpse?" : "Удалять труп?")] 
            public bool DeleteCorpse { get; set; }
            
            [JsonProperty(En ? "Enable sleeping NPCs outside of player range to improve performance? [true/false]" : "Использовать режим сна для NPС, когда игрока нет рядом с NPC? (используется для повышения производительности) [true/false]")] 
            public bool CanSleep { get; set; }
            
            [JsonProperty(En ? "The range from NPC to player at which to wake sleeping NPCs [m.]" : "Расстояние от игрока до NPC, чтобы отключать спящий режим у NPC [m.]")] 
            public float SleepDistance { get; set; }
            
            [JsonProperty(En ? "Own loot table" : "Собственная таблица лута")] 
            public LootTableConfig LootTableConfig { get; set; }
        }

        private class NpcWear
        {
            [JsonProperty("ShortName")] 
            public string ShortName { get; set; }
            
            [JsonProperty(En ? "skinID (0 - default)" : "SkinID (0 - default)")] 
            public ulong SkinID { get; set; }
        }

        private class NpcBelt
        {
            [JsonProperty("ShortName")] 
            public string ShortName { get; set; }
            
            [JsonProperty(En ? "Amount" : "Кол-во")] 
            public int Amount { get; set; }
            
            [JsonProperty(En ? "skinID (0 - default)" : "SkinID (0 - default)")] 
            public ulong SkinID { get; set; }
            
            [JsonProperty(En ? "Mods" : "Модификации на оружие")] 
            public List<string> Mods { get; set; }
            
            [JsonProperty(En ? "Ammo" : "Патроны")] 
            public string Ammo { get; set; }
        }


        private class CrateConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")] 
            public string PresetName { get; set; }
            
            [JsonProperty("Prefab")] 
            public string PrefabName { get; set; }
            
            [JsonProperty(En ? "SkinID (0 - default)" : "Скин")] 
            public ulong Skin { get; set; }
            
            [JsonProperty(En ? "Time to unlock the crates (LockedCrate) [sec.]" : "Время до открытия заблокированного ящика (LockedCrate) [sec.]")] 
            public float HackTime { get; set; }
            
            [JsonProperty(En ? "Own loot table" : "Собственная таблица предметов")] 
            public LootTableConfig LootTableConfig { get; set; }
        }

        private class LootTableConfig : BaseLootTableConfig
        {
            [JsonProperty(En ? "Allow the AlphaLoot plugin to spawn items in this crate" : "Разрешить плагину AlphaLoot спавнить предметы в этом ящике")] 
            public bool IsAlphaLoot { get; set; }
            
            [JsonProperty(En ? "The name of the loot preset for AlphaLoot" : "Название пресета лута AlphaLoot")] 
            public string AlphaLootPresetName { get; set; }
            
            [JsonProperty(En ? "Allow the CustomLoot plugin to spawn items in this crate" : "Разрешить плагину CustomLoot спавнить предметы в этом ящике")] 
            public bool IsCustomLoot { get; set; }
            
            [JsonProperty(En ? "Allow the Loot Table Stacksize GUI plugin to spawn items in this crate" : "Разрешить плагину Loot Table Stacksize GUI спавнить предметы в этом ящике")] 
            public bool IsLootTablePLugin { get; set; }
        }

        private class BaseLootTableConfig
        {
            [JsonProperty(En ? "Clear the standard content of the crate" : "Отчистить стандартное содержимое крейта")] 
            public bool ClearDefaultItemList { get; set; }
            
            [JsonProperty(En ? "Setting up loot from the loot table" : "Настройка лута из лутовой таблицы")] 
            public PrefabLootTableConfigs PrefabConfigs { get; set; }
            
            [JsonProperty(En ? "Enable spawn of items from the list" : "Включить спавн предметов из списка")] 
            public bool IsRandomItemsEnable { get; set; }
            
            [JsonProperty(En ? "Minimum numbers of items" : "Минимальное кол-во элементов")] 
            public int MinItemsAmount { get; set; }
            
            [JsonProperty(En ? "Maximum numbers of items" : "Максимальное кол-во элементов")] 
            public int MaxItemsAmount { get; set; }
            
            [JsonProperty(En ? "List of items" : "Список предметов")] 
            public List<LootItemConfig> Items { get; set; }
        }

        private class PrefabLootTableConfigs
        {
            [JsonProperty(En ? "Enable spawn loot from prefabs" : "Включить спавн лута из префабов")] 
            public bool IsEnable { get; set; }
            
            [JsonProperty(En ? "List of prefabs (one is randomly selected)" : "Список префабов (выбирается один рандомно)")] 
            public List<PrefabConfig> Prefabs { get; set; }
        }

        private class PrefabConfig
        {
            [JsonProperty(En ? "Prefab name" : "Название префаба")] 
            public string PrefabName { get; set; }
            
            [JsonProperty(En ? "Minimum Loot multiplier" : "Минимальный множитель лута")] 
            public int MinLootScale { get; set; }
            
            [JsonProperty(En ? "Maximum Loot multiplier" : "Максимальный множитель лута")] 
            public int MaxLootScale { get; set; }
        }

        private class LootItemConfig
        {
            [JsonProperty("ShortName")] 
            public string Shortname { get; set; }
            
            [JsonProperty(En ? "Minimum" : "Минимальное кол-во")] 
            public int MinAmount { get; set; }
            
            [JsonProperty(En ? "Maximum" : "Максимальное кол-во")] 
            public int MaxAmount { get; set; }
            
            [JsonProperty(En ? "Chance [0.0-100.0]" : "Шанс выпадения предмета [0.0-100.0]")] 
            public float Chance { get; set; }
            
            [JsonProperty(En ? "Is this a blueprint? [true/false]" : "Это чертеж? [true/false]")] 
            public bool IsBlueprint { get; set; }
            
            [JsonProperty("SkinID (0 - default)")] 
            public ulong Skin { get; set; }
            
            [JsonProperty(En ? "Name (empty - default)" : "Название (empty - default)")] 
            public string Name { get; set; }
            
            [JsonProperty(En ? "List of genomes" : "Список геномов")] 
            public List<string> Genomes { get; set; }
        }


        private class SummonMonumentConfig : ItemConfig
        {
            [JsonProperty("Permission")]
            public string Permission { get; set; }

            [JsonProperty(En ? "Lang instructions for spawn locations" : "Lang инструкция спавна локации")]
            public string SpawnDescriptionLang { get; set; }
        }

        private class ItemConfig
        {
            [JsonProperty("ShortName", Order = 100)]
            public string Shortname { get; set; }

            [JsonProperty("SkinID (0 - default)", Order = 101)]
            public ulong Skin { get; set; }

            [JsonProperty(En ? "Name (empty - default)" : "Название (empty - default)", Order = 102)]
            public string Name { get; set; }
        }

        private class MarkerConfig
        {
            [JsonProperty(En ? "Use a vending marker? [true/false]" : "Добавить маркер магазина? [true/false]")]
            public bool UseShopMarker { get; set; }

            [JsonProperty(En ? "Use a circular marker? [true/false]" : "Добавить круговой маркер? [true/false]")]
            public bool UseRingMarker { get; set; }

            [JsonProperty(En ? "Radius" : "Радиус")]
            public float Radius { get; set; }

            [JsonProperty(En ? "Alpha" : "Прозрачность")]
            public float Alpha { get; set; }

            [JsonProperty(En ? "Marker color" : "Цвет маркера")]
            public ColorConfig Color1 { get; set; }

            [JsonProperty(En ? "Outline color" : "Цвет контура")]
            public ColorConfig Color2 { get; set; }
        }

        private class ColorConfig
        {
            [JsonProperty("r")] 
            public float R { get; set; }
            
            [JsonProperty("g")] 
            public float G { get; set; }
            
            [JsonProperty("b")] 
            public float B { get; set; }
        }

        private class NotifyConfig : BaseMessageConfig
        {
            [JsonProperty(En ? "Redefined messages" : "Переопределенные сообщения )", Order = 101)]
            public HashSet<RedefinedMessageConfig> RedefinedMessages { get; set; }
        }

        private class RedefinedMessageConfig : BaseMessageConfig
        {
            [JsonProperty(En ? "Enable this message? [true/false]" : "Включить сообщение? [true/false]", Order = 1)]
            public bool IsEnable { get; set; }

            [JsonProperty("Lang Key", Order = 1)]
            public string LangKey { get; set; }
        }

        private class BaseMessageConfig
        {
            [JsonProperty(En ? "Chat Message setting" : "Настройки сообщений в чате", Order = 1)]
            public ChatConfig ChatConfig { get; set; }

            [JsonProperty(En ? "Facepunch Game Tips setting" : "Настройка сообщений Facepunch Game Tip", Order = 2)]
            public GameTipConfig GameTipConfig { get; set; }
        }

        private class ChatConfig
        {
            [JsonProperty(En ? "Use chat notifications? [true/false]" : "Использовать ли чат? [true/false]")]
            public bool IsEnabled { get; set; }
        }

        private class GameTipConfig
        {
            [JsonProperty(En ? "Use Facepunch Game Tips (notification bar above hotbar)? [true/false]" : "Использовать ли Facepunch Game Tip (оповещения над слотами быстрого доступа игрока)? [true/false]")]
            public bool IsEnabled { get; set; }

            [JsonProperty(En ? "Style (0 - Blue Normal, 1 - Red Normal, 2 - Blue Long, 3 - Blue Short, 4 - Server Event)" : "Стиль (0 - Blue Normal, 1 - Red Normal, 2 - Blue Long, 3 - Blue Short, 4 - Server Event)")]
            public int Style { get; set; }
        }
        
        private class PluginConfig
        {
            [JsonProperty(En ? "Version" : "Версия")]
            public VersionNumber Version { get; set; }

            [JsonProperty(En ? "Prefix of messages" : "Префикс сообщений")]
            public string Prefix { get; set; }

            [JsonProperty(En ? "Main Settings" : "Основные настройки")]
            public MainConfig MainConfig { get; set; }

            [JsonProperty(En ? "Ground Monuments" : "Наземные монументы")]
            public MonumentTypeConfig GroundTypeConfig { get; set; }

            [JsonProperty(En ? "Coastal Monuments" : "Прибрежные монументы")]
            public MonumentTypeConfig CoastalTypeConfig { get; set; }

            [JsonProperty(En ? "Customization of default prefabs" : "Кастомизации дефолтных префабов")]
            public MonumentTypeConfig PrefabTypeConfig { get; set; }

            [JsonProperty(En ? "Water Monuments" : "Водные монументы")]
            public MonumentTypeConfig WaterTypeConfig { get; set; }

            [JsonProperty(En ? "River Monuments" : "Речные монументы")]
            public MonumentTypeConfig RiverTypeConfig { get; set; }

            [JsonProperty(En ? "Road Monuments" : "Придорожные монументы")]
            public MonumentTypeConfig RoadTypeConfig { get; set; }


            [JsonProperty(En ? "Crates" : "Крейты")]
            public HashSet<CrateConfig> CrateConfigs { get; set; }

            [JsonProperty(En ? "NPC Configurations" : "Кофигурации NPC")]
            public HashSet<NpcConfig> NpcConfigs { get; set; }

            [JsonProperty(En ? "Map Marker" : "Маркер на карте")]
            public MarkerConfig MarkerConfig { get; set; }

            [JsonProperty(En ? "Notification Settings" : "Настройки уведомлений")]
            public NotifyConfig NotifyConfig { get; set; }
            
            // ReSharper disable All
            public static PluginConfig DefaultConfig()
            {
                return new PluginConfig()
                {
                    Version = new VersionNumber(1, 1, 7),
                    Prefix = "[DynamicMonuments]",
                    MainConfig = new MainConfig
                    {
                        IsRespawnLocation = false,
                        MaxLocationNumberPerPlayer = 3,
                        IsSpawnLogging = true,
                        AllowDrawOnSigns = false,
                        DisableRecyclers = false
                    },
                    GroundTypeConfig = new MonumentTypeConfig
                    {
                        IsAutoSpawn = false,
                        MinAmount = 2,
                        MaxAmount = 2,
                        Sites = new HashSet<SiteConfig>()
                    },
                    PrefabTypeConfig = new MonumentTypeConfig
                    {
                        IsAutoSpawn = false,
                        MinAmount = 2,
                        MaxAmount = 2,
                        Sites = new HashSet<SiteConfig>
                        {
                            new SiteConfig
                            {
                                PresetName = "highHouse",
                                DisplayName = En ? "The High House" : "Дом на опоре",
                                DataFileName = "highHouse",
                                PrefabNames = new HashSet<string>
                                {
                                    "powerlineplatform_a",
                                    "powerlineplatform_b",
                                    "powerlineplatform_c"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                AdditionalCount = 0,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "PowerLines_Decription",
                                    Shortname = "flare",
                                    Name = En ? "The High House" : "Дом на опоре",
                                    Skin = 3440564122,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>
                                {
                                    "Temperate",
                                    "Tundra"
                                },
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-0.8475733, 12.2486, -6.579161)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-7.130701, 5.679596, 6.521563)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-2.199258, 4.739288, -0.5442489)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-2.263955, 4.739288, 0.2936112)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-0.2266017, 4.736923, -4.389799)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-2.810372, 10.6424, -2.098418)",
                                        Rotation = "(0.00, 74.20, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(2.284614, 9.07724, 2.441164)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(2.284614, 9.07724, 2.441164)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-1.079904, 9.804703, 3.159227)",
                                        Rotation = "(0.00, 180.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(1.879737, 4.7621, 2.592088)",
                                        Rotation = "(0.00, 209.54, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-4.289703, 9.189941, -2.099571)",
                                        Rotation = "(0.00, 90.38, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(2.1417, 9.100601, -1.972044)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(2.104926, 9.124603, -1.141585)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(2.832114, 4.739288, -0.3331893)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(1.113791, 12.09024, 0.5962541)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(2.84481, 4.739288, 0.7587144)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(0.6300421, 12.09024, 1.503404)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-2.930704, 9.185944, 3.142429)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(3.982001, 4.739288, -3.476912)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-2.386392, 12.09024, -1.736601)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(4.328297, 9.239944, 1.863429)",
                                        Rotation = "(0.00, 333.50, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(3.741523, 6.076233, 3.052233)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-3.866677, 4.739288, -3.674605)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(2.234855, 12.09024, -1.811049)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    }
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(-1.392221, 6.01622, 3.147325)",
                                        Rotation = "(0.00, 98.00, 0.00)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "observationTower",
                                DisplayName = En ? "Observation Tower" : "Наблюдательная вышка",
                                DataFileName = "observationTower",
                                PrefabNames = new HashSet<string>
                                {
                                    "powerlineplatform_a",
                                    "powerlineplatform_b",
                                    "powerlineplatform_c"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "PowerLines_Decription",
                                    Shortname = "flare",
                                    Name = En ? "Observation Tower" : "Наблюдательная вышка",
                                    Skin = 3440564440,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>
                                {
                                    "Arctic",
                                    "Arid"
                                },
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-7.238985, 5.679489, 6.589406)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(3.323912, 31.62233, -1.234508)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(2.619887, 18.22374, -0.06985015)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(3.329161, 31.62233, 0.009190381)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-5.358598, -0.09359741, 0.8419998)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(2.721831, 18.22374, 0.942601)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-4.666643, -0.0916748, 1.260335)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-5.355714, -0.09359741, 1.683339)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(4.045577, 23.0511, 3.076695)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-1.64055, 10.16595, -2.266826)",
                                        Rotation = "(0.00, 31.29, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(2.743376, 32.49428, 1.328068)",
                                        Rotation = "(0.00, 221.38, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(1.384612, 31.61427, 3.156163)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(2.16754, 9.145706, 5.899266)",
                                        Rotation = "(0.00, 60.36, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(5.302077, -0.0916748, -8.123836)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(4.601851, -0.0916748, -7.29005)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(5.64604, -0.0916748, -7.219722)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-0.05651977, 31.60042, -4.074214)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-3.649431, 18.22008, -1.410411)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-2.580339, 18.22252, -0.9225876)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-0.1415112, 23.05351, -5.861232)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(1.662511, 10.62929, -2.772869)",
                                        Rotation = "(0.00, 162.66, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(0.04858278, 1.224533, -1.201335)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.971055, 0.3175812, 10.84984)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-0.1206677, 9.147293, -2.324153)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/tools/keycard/keycard_green_pickup.entity.prefab",
                                        Position = "(2.082, 24.1, -1.6)",
                                        Rotation = "(0.10, 291.08, 0.39)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "busStopGarage",
                                DisplayName = En ? "The Bus Stop Garage" : "Гараж на остановке",
                                DataFileName = "busStopGarage",
                                PrefabNames = new HashSet<string>
                                {
                                    "Busstop"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "BusStop_Decription",
                                    Shortname = "flare",
                                    Name = En ? "The Bus Stop Garage" : "Гараж на остановке",
                                    Skin = 3440561525,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(2.615707, 0.2186491, 1.220779)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-0.9587956, 0.3436494, -0.8342445)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(1.62314, 0.3436493, -1.434321)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(1.622542, 0.3436494, -0.3691741)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-0.782683, 0.3436495, 0.9385735)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(0.2178077, 1.345649, -0.9923327)",
                                        Rotation = "(0.00, 76.57, 0.00)"
                                    }
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(3.432129, 3.544194, -2.348595)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(3.432129, 3.544194, -1.423602)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(3.432129, 3.544194, -0.4825292)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(3.432129, 3.544194, 0.552768)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(3.432129, 3.544194, 1.464897)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(3.432129, 3.544194, 2.348777)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "blackGoldIsland",
                                DisplayName = En ? "The Island of Black Gold" : "Остров черного золота",
                                DataFileName = "blackGoldIsland",
                                PrefabNames = new HashSet<string>
                                {
                                    "coastal_rocks_large_c"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,

                                Probability = 100f,
                                Biomes = new HashSet<string>
                                {
                                    "Temperate",
                                    "Tundra",
                                    "Arid"
                                },
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-24.0125, 0.1229401, -15.40552)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-23.15069, 0.1188507, -14.84289)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-4.898001, 3.889481, 9.344544)",
                                        Rotation = "(10.36, 0.00, 3.66)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-4.008475, 3.971878, 9.359681)",
                                        Rotation = "(10.36, 0.00, 3.66)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-24.73479, 0.1043396, -8.000578)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(10.20124, 0.02082825, 15.66315)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(1.091379, 0.118866, -23.06347)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-8.848197, 4.643021, -8.024137)",
                                        Rotation = "(5.55, 2.23, 21.96)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-18.1347, 0.09983826, -21.33703)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-18.7188, 0.1080475, -20.37634)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(20.16755, 0.1039276, 13.5871)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(19.01203, 0.1217804, 13.87708)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-2.060233, 0.1209869, 20.91837)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-1.442069, 0.101944, 22.02286)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-18.02117, 0.111908, -26.7796)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-19.99468, 0.08702087, 6.591799)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-15.92083, 0.01826477, -18.95825)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-17.99017, 0.1179657, -3.517027)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-9.508719, 0.1247406, 21.95426)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(4.628244, 0.1274261, 21.52674)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "fishermansRetreat",
                                DisplayName = En ? "The Fisherman's Retreat" : "Приют рыбака",
                                DataFileName = "fishermansRetreat",
                                PrefabNames = new HashSet<string>
                                {
                                    "coastal_rocks_large_b"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-4.571888, 6.332367, -34.15929)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-4.571888, 6.332367, -33.11266)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-12.09009, 9.291183, -31.86034)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(1.330608, 6.347168, -27.90673)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-6.147008, 0.3672638, -22.94188)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-7.078465, 0.3335571, -19.23644)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-3.139912, 7.356613, -34.82177)",
                                        Rotation = "(0.00, 120.90, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-12.39844, 9.291183, -33.74572)",
                                        Rotation = "(0.00, 23.91, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(4.293727, 7.076508, -33.26928)",
                                        Rotation = "(0.00, 238.51, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(1.45805, 6.347168, -31.51256)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(2.624767, 6.858856, -35.72326)",
                                        Rotation = "(0.00, 300.79, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(0.5116689, 9.346695, -37.61388)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(2.140026, 9.341492, -30.95324)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-0.05469161, 6.347168, -29.65868)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(4.457576, 6.347168, -27.71996)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(0.03745621, 6.328171, -37.43175)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.804417, 9.333359, -35.47484)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(4.030757, 6.347168, -31.01415)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(13.54513, 0.3847961, -24.43993)",
                                        Rotation = "(0.00, 66.00, 1.84)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-1.686701, 9.75, -34.23278)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-2.609646, 9.75, -34.23249)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-3.369705, 9.75, -34.21754)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-1.686701, 9.75, -33.31186)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-2.609646, 9.75, -33.31157)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-3.369705, 9.75, -33.29662)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-1.686701, 9.75, -32.47798)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-2.609646, 9.75, -32.4777)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-3.369705, 9.75, -32.46275)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-1.686701, 9.75, -31.40132)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-2.609646, 9.75, -31.40104)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-3.369705, 9.75, -31.38609)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(1.133906, 9.75, -31.37024)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(0.2109619, 9.75, -31.36996)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-0.5490971, 9.75, -31.35501)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(1.133906, 9.75, -30.50255)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(0.2109619, 9.75, -30.50226)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-1.686701, 9.75, -30.49907)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-2.609646, 9.75, -30.49879)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-0.5490971, 9.75, -30.48731)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-3.369705, 9.75, -30.48384)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(1.133906, 9.75, -29.59689)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-1.686701, 9.75, -29.59689)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(0.2109619, 9.75, -29.5966)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-2.609646, 9.75, -29.5966)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-0.5490971, 9.75, -29.58165)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/autospawn/collectable/hemp/hemp-collectable.prefab",
                                        Position = "(-3.369705, 9.75, -29.58165)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "depotGates_1",
                                DisplayName = En ? "Depot Gates" : "Ворота депо",
                                DataFileName = "depotGates_1",
                                PrefabNames = new HashSet<string>
                                {
                                    "train_tunnel_double_entrance_b_72m"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                AdditionalCount = 0,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-13.55263, -2.328522, 0.6071314)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.20247, 0.01745605, -1.697602)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-11.10488, 0.01745605, -1.663254)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-0.7030253, -2.923309, -1.361971)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(0.1895525, -2.923309, -0.9984461)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.61519, 0.01745605, -0.9633032)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(15.38901, -2.925598, -3.125432)",
                                        Rotation = "(0.00, 135.16, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(11.21616, -2.928238, -2.613438)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-10.4, -2.934616, -1.680817)",
                                        Rotation = "(0.00, 215.61, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-13.58626, -2.928391, 2.802016)",
                                        Rotation = "(0.00, 123.69, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-10.96675, -2.802155, 6.816892)",
                                        Rotation = "(0.00, 159.77, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-10.17483, -2.940079, -3.582016)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(12.42514, -2.962494, 1.361414)",
                                        Rotation = "(0.00, 354.72, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(16.20096, 0.04554749, -3.995015)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-16.59731, 0.04612732, -1.627288)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(12.77628, 0.03575134, 2.029627)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(11.58054, 0.04554749, 2.073435)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(9.958167, -2.802155, 8.261302)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-16.30502, 0.04612732, -0.7530973)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(9.120276, -2.802155, 8.14445)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-13.36946, -2.942703, -1.537323)",
                                        Rotation = "(0.00, 138.11, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(13.37387, -2.944855, 3.123025)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-0.2376926, -2.923309, 3.838954)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-9.4, -2.938797, 2.304655)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    }
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(9.416114, -2.923309, 2.725672)",
                                        Rotation = "(0.00, 284.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(10.1832, -2.923309, 3.122889)",
                                        Rotation = "(0.00, 227.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/tools/keycard/keycard_red_pickup.entity.prefab",
                                        Position = "(11.1003, -2.019409, -3.703557)",
                                        Rotation = "(0.18, 18.66, 359.51)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>
                                {
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates1_1",
                                        Position = "(-14.179, 3.046, -3.327)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates1_1",
                                        Position = "(0, 3.046, -3.721)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates1_1",
                                        Position = "(14.725, 3.046, -3.721)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates1_2",
                                        Position = "(-10.615, 0.048, 2.175)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates1_3",
                                        Position = "(8.579, 0.048, 2.540)"
                                    },
                                },
                                GroudNpcs = new HashSet<NpcPresetLocationConfig>()
                                {
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(11.5, 0, 11.3)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(-11.5, 0, 11.3)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(5, 0, 2)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(-5, 0, 2)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(5, 0, -11)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(-5, 0, -11)"
                                    },
                                }
                            },
                            new SiteConfig
                            {
                                PresetName = "depotGates_2",
                                DisplayName = En ? "Depot Gates" : "Ворота депо",
                                DataFileName = "depotGates_2",
                                PrefabNames = new HashSet<string>
                                {
                                    "train_tunnel_double_entrance"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-0.8489515, -2.764099, -13.73632)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(2.183779, 0.01611328, -11.32909)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(1.483827, 0.01611328, -10.83941)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(2.218126, 0.01611328, -10.42669)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(1.882494, -2.924652, -0.9272366)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(1.518968, -2.924652, -0.03465841)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-2.281492, -2.929733, -13.81048)",
                                        Rotation = "(0.00, 33.69, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-6.29637, -2.916351, -11.19097)",
                                        Rotation = "(0.00, 69.77, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(2.201341, -2.925507, -9.678)",
                                        Rotation = "(0.00, 125.61, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(3.133959, -2.929565, 10.99195)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(1.445772, -2.921509, 11.24842)",
                                        Rotation = "(0.00, 86.31, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(4.102541, -2.941422, -10.39904)",
                                        Rotation = "(0.00, 270.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-0.8408949, -2.963837, 12.20094)",
                                        Rotation = "(0.00, 264.72, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-9.059081, -2.916351, 10.76808)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-1.552916, 0.04420471, 11.35633)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-9.175933, -2.916351, 11.60597)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-1.509108, 0.03440857, 12.55207)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(4.565905, 0.04420471, 12.94099)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(2.057848, -2.734955, -13.59368)",
                                        Rotation = "(0.00, 48.11, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-3.318434, -2.924652, -0.4619045)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-2.602507, -2.946198, 13.14967)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-1.784132, -2.94014, -9.791)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_underwater_1",
                                        Position = "(4.401393, 0.0425415, -9.327874)",
                                        Rotation = "(0.00, 112.05, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_underwater_1",
                                        Position = "(-8.455953, -1.921494, -9.108129)",
                                        Rotation = "(0.00, 162.30, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(-1.34359, 0.0425415, -7.535215)",
                                        Rotation = "(0.00, 346.26, 0.00)"
                                    }
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(-2.205153, -2.924652, 9.191904)",
                                        Rotation = "(0.00, 347.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(-2.60237, -2.924652, 9.958994)",
                                        Rotation = "(0.00, 48.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/tools/keycard/keycard_red_pickup.entity.prefab",
                                        Position = "(4.224305, -2.020782, 10.87637)",
                                        Rotation = "(0.24, 288.61, 359.78)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>
                                {
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates2_1",
                                        Position = "(4.053, 3.047, -14.365)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates2_1",
                                        Position = "(4.053, 3.047, 0)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates2_1",
                                        Position = "(4.053, 3.047, 14.365)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates2_2",
                                        Position = "(-0.619, 0.047, -10.839)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_depotGates2_3",
                                        Position = "(-0.619, 0.047, 10.839)"
                                    },
                                },
                                GroudNpcs = new HashSet<NpcPresetLocationConfig>()
                                {
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(-10.5, 0, -11)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(-10.5, 0, 11)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(-4, 0, 4.5)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(-4, 0, -4.5)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(16.5, 0, 4)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "depotGates_npc_1",
                                        Position = "(16.5, 0, -4)"
                                    },
                                }
                            },
                            new SiteConfig
                            {
                                PresetName = "forgottenSubstation",
                                DisplayName = En ? "Forgotten Substation" : "Забытая подстанция",
                                DataFileName = "forgottenSubstation",
                                PrefabNames = new HashSet<string>
                                {
                                    "assets/bundled/prefabs/autospawn/power substations/big/power_sub_big_1.prefab"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-9.568066, 1.089218, 1.987731)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-3.935587, 0.03622437, -5.56394)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(2.161487, 0.03450012, -5.535013)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-3.965865, 0.03622437, -4.346861)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-2.426339, -0.0003814697, 14.33414)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-3.433297, -0.0003814697, 14.37765)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(4.706206, 1.574051, -6.089833)",
                                        Rotation = "(0.00, 270.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-7.514342, 0.03622437, -1.481182)",
                                        Rotation = "(0.00, 270.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-2.015377, 0.03129578, -10.52361)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-14.14921, 0.004623413, -1.143294)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(2.582942, 0.03622437, 2.21823)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-2.639915, 0.02563477, -10.01397)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-14.97824, 0.004623413, -1.668772)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(1.721334, 0.03622437, 2.399976)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-0.4846973, 0.03622437, -7.729892)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-2.704832, 0.7102051, 4.135362)",
                                        Rotation = "(0.00, 345.48, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.590271, 0.7102051, 4.424043)",
                                        Rotation = "(0.00, 345.48, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-9.88674, 0.004623413, 6.87587)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(7.533805, -0.0003814697, 8.998565)",
                                        Rotation = "(0.00, 34.20, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(-0.9996948, 0.9322968, -11.45498)",
                                        Rotation = "(0.00, 242.23, 0.00) "
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "teslaGenerator",
                                DisplayName = En ? "Tesla Generator" : "Тесла-Генератор",
                                DataFileName = "teslaGenerator",
                                PrefabNames = new HashSet<string>
                                {
                                    "assets/bundled/prefabs/autospawn/power substations/big/power_sub_big_2.prefab"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-2.011811, 1.446213, -5.029413)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-12.15041, 6.078064, 9.540295)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-3.734749, 6.031982, 9.543083)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-11.23285, 6.07756, 10.39489)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-3.939115, 6.032364, 10.89636)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(1.128371, 11.90196, -2.368514)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(7.490137, 11.9021, 3.223332)",
                                        Rotation = "(0.00, 180.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-9.121287, 6.078033, 4.691266)",
                                        Rotation = "(0.00, 47.11, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(0.4490093, 0.01699829, 3.542377)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(0.4448124, 0.03669739, 5.550117)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(7.684707, 2.964127, 2.566774)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-2.13514, 2.959473, 6.12552)",
                                        Rotation = "(0.00, 30.06, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(5.179719, -0.01548767, 9.656042)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-3.82433, 0.03669739, -5.802939)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-2.749432, 0.03669739, -1.141503)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(-5.190793, 0.9376984, -1.673797)",
                                        Rotation = "(0.00, 298.30, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(7.902139, 12.65363, 0.06211978)",
                                        Rotation = "(0.00, 264.37, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "tech_parts_1_underwater_1",
                                        Position = "(8.37364, 1.089996, 5.647061)",
                                        Rotation = "(0.00, 271.64, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "lostOutpost",
                                DisplayName = En ? "Lost Outpost" : "Затерянный аванпост",
                                DataFileName = "lostOutpost",
                                PrefabNames = new HashSet<string>
                                {
                                    "assets/bundled/prefabs/autospawn/tunnel-entrance/entrance_bunker_a.prefab",
                                    "assets/bundled/prefabs/autospawn/tunnel-entrance/entrance_bunker_b.prefab",
                                    "assets/bundled/prefabs/autospawn/tunnel-entrance/entrance_bunker_c.prefab"
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-4.288268, 6.148712, -20.63659)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-4.849257, 5.038818, 7.083858)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.66355, 5.004974, -8.471495)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-11.4589, 5.004974, -8.026365)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.20768, 5.004974, -7.536436)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(11.33114, 5.004974, -6.987485)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(10.60635, 5.004974, -6.489622)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-9.769514, 5.713028, 3.232539)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.254, 5.712479, 3.903736)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-11.14047, 5.713165, 3.918092)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.71872, 5.71315, 4.637576)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(11.072, 6.083252, -1.897408)",
                                        Rotation = "(0, 238.18, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(5.391705, 6.662048, 7.092419)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-4.425855, 5.129974, -9.477951)",
                                        Rotation = "(0, 90, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(13.27175, 5.004974, 1.378909)",
                                        Rotation = "(0.00, 296.45, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(4.087811, 12.18497, 12.06048)",
                                        Rotation = "(0.00, 322.70, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-0.4471247, 5.129974, -18.92692)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(0.6300392, 5.129974, -18.74933)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-6.001991, 5.004974, -13.50036)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-6.046974, 5.004974, -12.24042)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-2.474256, 5.129974, -11.10676)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(1.958372, 5.129974, -10.4175)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-7.093788, 4.993118, -2.28491)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-4.092262, 5.004974, 11.50977)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-4.603188, 5.004974, 12.89594)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-4.603188, 5.004974, 12.89594)",
                                        Rotation = "(0, 90, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(6.615074, 5.004974, -9.184568)",
                                        Rotation = "(0.00, 33.47, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(4.212838, 5.129974, -18.92175)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(4.369078, 5.129974, -12.29582)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-14.90434, 5.004974, -11.81006)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-2.213844, 5.004974, 14.9005)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-15.56553, 5.004974, -1.071592)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_fuel_underwater_1",
                                        Position = "(-4.464322, 6.04097, -10.89169)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "vehicle_parts_underwater_1",
                                        Position = "(4.307678, 6.431976, -13.79469)",
                                        Rotation = "(0.00, 252.34, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "vehicle_parts_underwater_1",
                                        Position = "(-4.303323, 6.04097, -11.73969)",
                                        Rotation = "(0.00, 98.05, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(4.496531, 6.219971, -9.357279)",
                                        Rotation = "(0.00, 270.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(-19.08018, 6.006973, -1.332221)",
                                        Rotation = "(0.00, 352.38, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(-5.511777, 5.905975, 14.96759)",
                                        Rotation = "(0.00, 357.97, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "stoneQuarryRuins",
                                DisplayName = En ?"Stone Quarry Ruins" : "Руины каменоломни",
                                DataFileName = "stoneQuarryRuins",
                                PrefabNames = new HashSet<string>
                                {
                                    "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_b.prefab",
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(4.957563, 14.36839, -28.91836)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-18.65694, 7.065109, -28.7432)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-4.80501, 1.142029, -17.46685)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-3.898394, 1.171295, -17.38502)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-24.60579, 1.479004, -6.1195)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-24.97493, 1.499374, -4.895944)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-12.80672, 0.00630188, 7.292686)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-12.56404, 2.151871, 20.73287)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-15.8254, 2.471191, 23.92255)",
                                        Rotation = "(0, 0, 0)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-21.76424, 7.639496, -26.82327)",
                                        Rotation = "(353.71, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(0.8088822, 14.23474, -25.00999)",
                                        Rotation = "(0.00, 22.32, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-21.0135, 6.429153, -18.89325)",
                                        Rotation = "(0.00, 62.09, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-20.76375, 1.140121, -7.254495)",
                                        Rotation = "(0.00, 158.89, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-9.307817, 1.143066, 9.282354)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(6.950972, 1.299057, 1.218604)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(5.930952, 0.002502441, 10.8171)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-21.74361, 12.61156, 19.40305)",
                                        Rotation = "(0.00, 314.78, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-14.04427, 6.434845, -21.75882)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-3.462969, 6.434845, -20.75878)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-2.71309, 6.429153, -19.46595)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-13.26265, 6.429153, -19.28751)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-13.47725, 1.130615, -15.51248)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-20.47908, 2.0215, -13.72486)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-9.609941, 0.9156342, -13.35163)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(11.58146, 5.673355, -12.31863)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(6.812178, -0.007537842, -4.312539)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(7.443647, -0.007537842, -1.943399)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(16.05717, 12.01755, 6.888373)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(16.01835, 12.00778, 8.092077)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-28.21175, 9.975967, 11.73809)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-18.89168, 10.73726, 25.92025)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.945024, -0.03034973, -15.54637)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-4.306353, 1.534119, -9.990519)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(0.00, 348.66, 0.00)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(9.332317, 13.00282, 14.2143)",
                                        Rotation = "(0.00, 330.89, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(1.558149, 1.732254, -7.460595)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-15.97139, -0.007537842, 1.868857)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-27.69136, 13.45184, -14.28118)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_underwater_1",
                                        Position = "(-12.92224, 7.349899, -21.43986)",
                                        Rotation = "(0.00, 208.76, 0.00) "
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(2.624797, 0.9188843, -13.86646)",
                                        Rotation = "(0.00, 264.00, 0.31)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(6.370405, 1.144089, -9.544947)",
                                        Rotation = "(0.00, 41.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/tools/keycard/keycard_green_pickup.entity.prefab",
                                        Position = "(-4.212811, 0.62677, -15.27436)",
                                        Rotation = "(0.94, 358.72, 0.11)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                                GroudNpcs = new HashSet<NpcPresetLocationConfig>()
                                {
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "stoneQuarryNpc",
                                        Position = "(0, 0, 10)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "stoneQuarryNpc",
                                        Position = "(-10, 0, 6)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "stoneQuarryNpc",
                                        Position = "(-4, 0, 31)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "stoneQuarryNpc",
                                        Position = "(-18, 0, -3)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "stoneQuarryNpc",
                                        Position = "(5, 0, 36)"
                                    }
                                }
                            },
                            new SiteConfig
                            {
                                PresetName = "hqmQuarryRuins",
                                DisplayName = En ?"HQM Quarry Ruins" : "Руины МВК-карьера",
                                DataFileName = "hqmQuarryRuins",
                                PrefabNames = new HashSet<string>
                                {
                                    "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_c.prefab",
                                },
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(17.69779, 9.710831, -10.3552)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(16.3639, 9.711472, -9.788931)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(19.9703, 5.211792, 2.316758)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(10.1443, 5.206955, 3.983699)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(17.90485, 5.206543, 4.597902)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(10.30162, 5.206955, 5.312668)",
                                        Rotation = "(0, 0, 0)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-22.59542, 5.261169, -8.476933)",
                                        Rotation = "(0.00, 37.03, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-23.96522, 5.260925, -2.898531)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-17.61814, 4.593613, 0.7915612)",
                                        Rotation = "(0.00, 189.40, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-16.36087, 5.591843, 1.074316)",
                                        Rotation = "(0.00, 163.98, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-16.6041, 11.84813, 19.23355)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-13.95787, 10.91885, -21.0829)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(2.35342, 8.687775, 6.942188)",
                                        Rotation = "(0.00, 338.42, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-25.60976, 4.611816, -12.54166)",
                                        Rotation = "(6.87, 196.44, 354.27)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-14.8926, 4.591309, -11.34989)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-16.84859, 1.915695, -4.691257)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-16.68929, 2.044128, 2.192826)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(20.84373, 15.54031, 4.953628)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(18.99124, 15.61911, 6.785128)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-14.6069, 12.08046, 15.88936)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-5.509924, 12.08582, 17.65427)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-11.94849, 2.993958, 26.69995)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-7.74292, 2.993958, 30.6748)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(19.16477, 5.21402, -0.6179694)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(2.321533, 0.003051758, 1.294189)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-15.62256, 4.591949, 7.506112)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.799411, 3.425323, 19.92568)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(8.023352, 5.215973, -0.6656107)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-15.60817, 4.591309, -3.899843)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(1.311523, 0.003051758, -2.77002)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-4.460449, 0.003051758, 10.01782)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    }
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/radtown/ore_metal.prefab",
                                        Position = "(-5.706543, 2.560852, -15.5498)",
                                        Rotation = "(0.00, 283.77, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/radtown/ore_metal.prefab",
                                        Position = "(-10.79004, 1.341248, -13.09497)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/bundled/prefabs/radtown/ore_metal.prefab",
                                        Position = "(-3.397705, 0.6538696, -10.50098)",
                                        Rotation = "(0.00, 283.77, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(-13.43555, 0.003051758, -5.412109)",
                                        Rotation = "(0.00, 264.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(-13.23193, 0.003051758, -4.233154)",
                                        Rotation = "(0.00, 312.00, 0.00)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                                GroudNpcs = new HashSet<NpcPresetLocationConfig>()
                                {
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "hqmQuarryNpc",
                                        Position = "(-0.5, 0, -3)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "hqmQuarryNpc",
                                        Position = "(-8, 0, -10)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "hqmQuarryNpc",
                                        Position = "(-5, 0, -20)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "hqmQuarryNpc",
                                        Position = "(-5, 0, -28)"
                                    },
                                }
                            }
                        },
                    },
                    CoastalTypeConfig = new MonumentTypeConfig
                    {
                        IsAutoSpawn = false,
                        MinAmount = 2,
                        MaxAmount = 2,
                        Sites = new HashSet<SiteConfig>
                        {
                            new SiteConfig
                            {
                                PresetName = "strandedBarge",
                                DisplayName = En ? "Stranded Barge" : "Баржа на отмели",
                                DataFileName = "strandedBarge",
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "ShoreLocation_Decription",
                                    Shortname = "flare",
                                    Name = En ? "Stranded Barge" : "Баржа на отмели",
                                    Skin = 3440565893,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.21728, 0.3683777, -1.366616)",
                                        Rotation = "(353.60, 183.56, 355.32)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-9.464961, 0.3735657, -0.9276509)",
                                        Rotation = "(353.60, 183.56, 355.32)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-10.23767, 0.2621918, -0.4729085)",
                                        Rotation = "(353.60, 183.56, 355.32)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(2.267826, 1.142334, 0.164207)",
                                        Rotation = "(4.21, 269.20, 353.28)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(1.956425, 1.013092, 1.06768)",
                                        Rotation = "(354.88, 197.23, 353.94)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-9.297359, -0.2871094, 3.658805)",
                                        Rotation = "(352.61, 126.07, 2.86)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-15.22387, 0.01791382, -2.644173)",
                                        Rotation = "(6.58, 1.25, 4.42)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-16.63867, -0.2480164, -1.282052)",
                                        Rotation = "(6.58, 1.25, 4.42)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(3.134526, 1.155136, 0.6157451)",
                                        Rotation = "(1.69, 249.54, 352.26)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(16.39417, 1.985229, 0.7317424)",
                                        Rotation = "(6.89, 357.02, 3.92)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(2.841679, 1.04007, 1.410423)",
                                        Rotation = "(1.69, 249.54, 352.26)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(16.23816, 1.824371, 2.005363)",
                                        Rotation = "(6.89, 357.02, 3.92)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(3.061283, 1.523056, -2.610329)",
                                        Rotation = "(1.69, 249.54, 352.26)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-15.17187, -0.1217194, -1.418679)",
                                        Rotation = "(6.58, 1.25, 4.42)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-9.868696, -0.04119873, 1.145286)",
                                        Rotation = "(353.60, 183.56, 355.32)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(0.7074016, 0.6273651, 3.526542)",
                                        Rotation = "(1.69, 249.54, 352.26)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(12.47339, 1.307495, 4.028587)",
                                        Rotation = "(355.18, 94.59, 6.30)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(11.55762, 2.095139, -3.260018)",
                                        Rotation = "(7.90, 331.70, 0.61)"
                                    }
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "pirs",
                                DisplayName = En ? "Pier" : "Пирс",
                                DataFileName = "pirs",
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "ShoreLocation_Decription",
                                    Shortname = "flare",
                                    Name = En ? "Pier" : "Пирс",
                                    Skin = 3440565414,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-15.75244, 2.25856, -0.07395363)",
                                        Rotation = "(0.00, 90.00, 0.00) "
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(6.845214, 2.357071, 5.132003)",
                                        Rotation = "(0.00, 90.00, 0.00) "
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-6.547728, 2.345856, 11.12389)",
                                        Rotation = "(0.00, 90.00, 0.00) "
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-4.300172, 1.473892, -5.392773)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-5.485719, 1.473892, -5.384686)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-15.88256, 1.492538, 11.05434)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-16.97204, 1.49086, 11.10781)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-12.61304, 2.10643, -6.904063)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-16.36926, 1.473892, -8.310493)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(0.6187755, 1.473892, 9.366074)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-13.64734, 1.487213, -17.71238)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(23.56225, 1.473892, -3.292862)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-19.56775, 1.482941, 6.41451)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(11.10925, 1.473892, 8.281265)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-11.974, 1.482162, -17.3827)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(16.76012, 1.473892, -1.569595)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-19.1029, 1.473892, 5.277761)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(10.02954, 1.473892, 8.192398)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-5.256472, 1.481552, -15.40281)",
                                        Rotation = "(0.00, 150.13, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-12.2998, 1.587524, -12.19877)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-16.91931, 1.478485, 6.265186)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(3.724853, 1.478485, 3.269701)",
                                        Rotation = "(0.00, 63.50, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-3.610106, 1.769257, 6.865557)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_food_1_1",
                                        Position = "(6.83872, 1.780502, -0.3066206)",
                                        Rotation = "(0.00, 180.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_food_2_1",
                                        Position = "(6.70472, 2.389496, 6.013379)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/tools/keycard/keycard_green_pickup.entity.prefab",
                                        Position = "(-15.87139, 2.283401, -0.8923661)",
                                        Rotation = "(0.53, 51.76, 1.35)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                            new SiteConfig
                            {
                                PresetName = "submarine",
                                DisplayName = En ? "Shadows of the Deep" : "Остов подлодки",
                                DataFileName = "submarine_1",
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "ShoreLocation_Decription",
                                    Shortname = "flare",
                                    Name = En ? "Shadows of the Deep" : "Остов подлодки",
                                    Skin = 3440566062,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 0,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(1.024601, 0.07252502, -5.733923)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-1.031386, 0.101181, -5.188834)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-12.25953, 0.07046509, -4.771481)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-14.24724, 0.03559875, -4.173099)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(10.35712, 0.07998657, -6.592622)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-11.12359, 0.719635, 4.199656)",
                                        Rotation = "(0.00, 149.07, 5.14)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-11.69065, 0.7072449, 5.414001)",
                                        Rotation = "(2.89, 3.25, 355.75)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(4.465392, 0.1213989, -9.838397)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(11.82382, 0.06266785, 3.860003)",
                                        Rotation = "(0.00, 207.68, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-4.350622, 0.04353333, 7.376753)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-6.281789, 0.04701233, -0.3250599)",
                                        Rotation = "(0.00, 149.07, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>(),
                            },
                        }
                    },
                    WaterTypeConfig = new MonumentTypeConfig
                    {
                        IsAutoSpawn = false,
                        MinAmount = 2,
                        MaxAmount = 2,
                        Sites = new HashSet<SiteConfig>
                        {
                            new SiteConfig
                            {
                                PresetName = "oilPlatform",
                                DisplayName = En ? "Halted Oil Tower" : "Остановленная нефтяная платформа",
                                DataFileName = "oilPlatform",
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "WaterLocation_Decription",
                                    Shortname = "flare",
                                    Name = En ? "Halted Oil Tower" : "Остановленная нефтяная платформа",
                                    Skin = 3440564766,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 10,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "codelockedhackablecrate_oilrig_1",
                                        Position = "(6.205575, 5.120117, -4.738463)",
                                        Rotation = "(359.73, 18.66, 358.09)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_fuel_underwater_1",
                                        Position = "(9.757612, 3.033905, 16.84295)",
                                        Rotation = "(0.00, 270.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(12.12314, 6.206085, -11.20855)",
                                        Rotation = "(0.00, 329.66, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(8.696691, 3.123337, -2.316334)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-3.52426, 2.280838, -11.04947)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(3.175024, 5.187668, 0.4740709)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(2.643713, 5.187683, 1.099315)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(3.323278, 5.187683, 1.459056)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-11.39126, 2.280823, 4.89154)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-5.719387, 2.280838, 8.699953)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(10.30061, 2.393143, 11.39495)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-12.54916, 2.280823, 15.33723)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-11.72598, 2.280823, 15.71858)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-12.42007, 2.280823, 16.35225)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(9.657506, 2.280823, 19.4217)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(10.56174, 2.280823, 20.20698)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(9.351171, 2.280823, 20.51313)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(10.6102, 2.280823, -10.7376)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(12.31684, 6.168427, -8.281128)",
                                        Rotation = "(0.00, 342.66, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(10.3732, 5.93512, 0.2694811)",
                                        Rotation = "(0.00, 95.33, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(5.164526, 6.735275, 16.11531)",
                                        Rotation = "(0.00, 69.59, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(3.229894, 5.187668, -5.622609)",
                                        Rotation = "(0.00, 327.45, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-12.94381, 2.280823, 18.76094)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(6.865087, 5.187668, -0.5135999)",
                                        Rotation = "(0.00, 108.06, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(5.342016, 6.448822, -0.04900033)",
                                        Rotation = "(0.00, 72.52, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(7.655553, 5.187668, -4.175587)",
                                        Rotation = "(0.00, 330.01, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-15.51705, 2.280823, 10.9582)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(6.426244, 6.705139, 13.00691)",
                                        Rotation = "(0.00, 62.13, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-16.23202, 2.280823, -14.36089)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-15.50943, 2.280823, -13.8781)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(8.643834, 5.31366, -9.075368)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(11.99546, 2.280823, -8.265798)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(8.928502, 6.715775, -7.82317)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(12.20975, 2.280823, -7.417286)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-14.46224, 2.280823, 0.02485221)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-15.43356, 2.280823, 0.3297838)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-14.69003, 2.280823, 1.226635)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-1.257655, 2.280823, 6.333812)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-0.7290291, 2.280823, 7.40266)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-1.728968, 2.280823, 7.465282)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(8.352452, 6.717285, 10.52449)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-14.95608, 2.280823, -21.02019)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-14.67092, 2.280823, -8.84258)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(4.93247, 5.189163, -7.765675)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-12.13052, 8.122116, 2.234935)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-5.20826, 2.389908, 4.77254)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(11.09848, 5.187881, 5.24702)",
                                        Rotation = "(0.00, 222.10, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(11.30161, 2.266907, 13.15895)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(9.886388, 3.800507, 14.80757)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(10.71366, 2.280823, 3.519359)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(4.341405, 2.280823, 18.96211)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(10.5947, 5.187927, -12.24285)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-7.740626, 2.33847, -6.961232)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(3.358984, 2.280823, 1.51411)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_underwater_1",
                                        Position = "(7.20984, 8.180603, -0.4221714)",
                                        Rotation = "(0.00, 224.56, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_underwater_1",
                                        Position = "(8.832774, 5.187668, 4.394595)",
                                        Rotation = "(0.00, 346.26, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(9.278905, 5.187683, 0.3661607)",
                                        Rotation = "(0.00, 165.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(9.391637, 5.187683, 1.28535)",
                                        Rotation = "(0.00, 192.00, 0.00)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>
                                {
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_1",
                                        Position = "(10.0, 2.281, -5.5)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_1",
                                        Position = "(10.0, 2.281, 9.412)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_1",
                                        Position = "(-0.648, 2.281, 21.511)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_1",
                                        Position = "(2.454, 2.281, -15.194)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_2",
                                        Position = "(6.414, 5.188, 6.135)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_2",
                                        Position = "(7.945, 5.188, -2.040)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_2",
                                        Position = "(11.686, 5.188, -2.040)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_2",
                                        Position = "(11.146, 5.188, -9.943)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "oilPlatform_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_oilPlatform_4",
                                        Position = "(6.624, 9.512, 13.535)"
                                    }
                                }
                            },
                            new SiteConfig
                            {
                                PresetName = "cargo",
                                DisplayName = En ? "The Broken Giant" : "Разломленный гигант",
                                DataFileName = "cargo_1",
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "WaterLocation_Decription",
                                    Shortname = "flare",
                                    Name = En ? "The Broken Giant" : "Разломленный гигант",
                                    Skin = 3440561888,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 10,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(10.09751, 30.67609, -82.82128)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(18.68857, 29.14087, -81.45744)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(19.51951, 28.99896, -81.34682)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(21.70344, 30.62161, -77.38569)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(22.79081, 30.43591, -77.24094)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-57.74513, 9.309296, -50.94901)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-56.94093, 9.309296, -50.50033)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(35.22555, 11.12405, -80.02283)",
                                        Rotation = "(0.00, 115.10, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(8.358516, 22.29373, -75.75642)",
                                        Rotation = "(350.12, 261.50, 343.56)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(9.295245, 22.12885, -75.61646)",
                                        Rotation = "(350.12, 261.50, 343.56)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(8.509837, 21.85132, -74.44263)",
                                        Rotation = "(16.38, 349.72, 350.03)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(23.20736, 41.6181, -69.11809)",
                                        Rotation = "(16.36, 349.57, 349.98)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-5.728112, 10.17346, -53.50078)",
                                        Rotation = "(356.01, 0.02, 1.30)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(-53.9995, 9.405029, -50.20459)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(-22.57508, 10.85809, -37.79404)",
                                        Rotation = "(0.00, 28.87, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-65.66609, 9.420898, -11.69719)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(-27.39557, 10.1264, -2.396172)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(-27.37453, 10.1264, -1.460151)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(37.2704, 10.01312, 4.980726)",
                                        Rotation = "(0.00, 0.00, 354.79)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-28.00104, 6.514709, 42.54534)",
                                        Rotation = "(352.03, 359.86, 3.57)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(22.55501, 32.72711, -74.35749)",
                                        Rotation = "(350.39, 262.42, 343.40)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(23.08169, 40.63538, -69.43446)",
                                        Rotation = "(16.36, 349.57, 349.98)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(18.85524, 39.63293, -64.75366)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-8.141012, 10.0069, -56.05413)",
                                        Rotation = "(0.00, 331.75, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(3.235897, 19.31836, -54.61958)",
                                        Rotation = "(350.39, 262.42, 343.40)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(37.23317, 11.05524, -41.41867)",
                                        Rotation = "(0.00, 327.23, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(1.556495, 13.00534, -34.34652)",
                                        Rotation = "(16.36, 349.57, 349.98)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-67.79198, 9.403809, -13.58664)",
                                        Rotation = "(0.00, 111.55, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-24.05066, 10.15292, -1.536598)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-30.71612, 10.16107, -1.311595)",
                                        Rotation = "(0.00, 315.39, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(5.05566, 15.96887, 34.4852)",
                                        Rotation = "(17.24, 178.09, 24.20)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(19.9155, 6.74408, 59.17436)",
                                        Rotation = "(0.00, 67.15, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(1.521148, 6.789093, 80.23664)",
                                        Rotation = "(5.71, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(37.38762, 11.10172, -73.66526)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(40.42172, 11.00977, -40.14184)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-68.27382, 9.409485, -16.71242)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-66.39027, 9.418121, -10.94395)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(40.26933, 9.706604, 6.264243)",
                                        Rotation = "(3.09, 359.85, 357.20)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(40.75071, 9.631775, 7.009593)",
                                        Rotation = "(3.09, 359.85, 357.20)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-21.81205, 6.764008, 42.50785)",
                                        Rotation = "(354.88, 0.00, 2.81)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(19.79044, 6.694092, 62.14681)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(19.18922, 6.68512, 62.91283)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(4.706951, 6.760101, 80.84296)",
                                        Rotation = "(4.90, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(5.513088, 6.760101, 80.84296)",
                                        Rotation = "(4.90, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(14.60915, 42.49976, -72.00867)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(9.214427, 42.51025, -69.85957)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(12.68083, 19.95871, -60.37882)",
                                        Rotation = "(16.36, 349.57, 349.98)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(22.4877, 16.78653, -54.40489)",
                                        Rotation = "(16.36, 349.57, 349.98)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-60.68431, 9.228333, -49.72331)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(35.376, 11.10968, -44.34385)",
                                        Rotation = "(0.00, 332.38, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(16.77925, 12.64902, -39.28746)",
                                        Rotation = "(16.36, 349.57, 349.98)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.772637, 11.70776, -28.99401)",
                                        Rotation = "(16.36, 349.57, 349.98)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(42.51536, 9.408203, 9.264755)",
                                        Rotation = "(0.00, 317.18, 356.30)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-10.11624, 19.14255, 20.50145)",
                                        Rotation = "(17.24, 178.09, 24.20)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(1.80011, 16.54684, 31.13473)",
                                        Rotation = "(17.24, 178.09, 24.20)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-7.904127, 21.5759, 32.37524)",
                                        Rotation = "(17.24, 178.09, 24.20)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-24.80634, 6.543213, 41.96694)",
                                        Rotation = "(354.48, 359.70, 3.08)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(22.02942, 6.815857, 56.41366)",
                                        Rotation = "(0.00, 341.81, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.945595, 6.777649, 81.20692)",
                                        Rotation = "(5.25, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(17.65125, 29.42984, -81.88966)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(16.83221, 34.41336, -77.3207)",
                                        Rotation = "(343.64, 169.57, 10.02)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(35.57034, 11.07172, -76.84374)",
                                        Rotation = "(0.00, 340.04, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_c.prefab",
                                        Position = "(66.22261, 7.904327, -26.65886)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_c.prefab",
                                        Position = "(43.20569, 11.81079, -33.13052)",
                                        Rotation = "(0.00, 186.52, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_b.prefab",
                                        Position = "(18.14113, 11.81079, -95.01936)",
                                        Rotation = "(0.00, 301.42, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_b.prefab",
                                        Position = "(-15.95289, 11.81079, -64.55003)",
                                        Rotation = "(0.00, 87.17, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_b.prefab",
                                        Position = "(-42.13051, 11.81079, 55.29453)",
                                        Rotation = "(0.00, 72.69, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_b.prefab",
                                        Position = "(-39.50603, 11.81079, 58.44899)",
                                        Rotation = "(0.00, 267.04, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_b.prefab",
                                        Position = "(15.55058, 11.81079, 13.83197)",
                                        Rotation = "(0.00, 72.69, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_a.prefab",
                                        Position = "(38.52159, 11.81079, -52.29641)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_a.prefab",
                                        Position = "(1.331756, 11.81079, -12.98829)",
                                        Rotation = "(0.00, 273.68, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_a.prefab",
                                        Position = "(-49.85819, 11.81079, -6.054282)",
                                        Rotation = "(0.00, 221.74, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_a.prefab",
                                        Position = "(-16.93083, 11.81079, 3.974042)",
                                        Rotation = "(0.00, 273.68, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_a.prefab",
                                        Position = "(-14.01865, 11.81079, 61.886)",
                                        Rotation = "(0.00, 143.07, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_a.prefab",
                                        Position = "(-17.53669, 11.81079, 29.89967)",
                                        Rotation = "(0.00, 235.17, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/prefabs/misc/junkpile_water/junkpile_water_a.prefab",
                                        Position = "(65.15681, 11.81079, -25.11244)",
                                        Rotation = "(0.00, 62.04, 0.00)"
                                    }
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>
                                {
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_1",
                                        Position = "(-9.792, 20.886, 26.953)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_1",
                                        Position = "(6.313, 13.806, 29.193)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_1",
                                        Position = "(7.978, 10.928, 22.137)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_1",
                                        Position = "(-10.992, 18.364, 16.428)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_1",
                                        Position = "(-9.072, 13.117, 1.742)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_2",
                                        Position = "(-5.169, 26.441, 41.666)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_2",
                                        Position = "(3.327, 22.743, 43.005)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_2",
                                        Position = "(-0.326, 20.433, 29.167)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_front_2",
                                        Position = "(-0.688, 13.538, 5.163)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_1",
                                        Position = "(5.268, 21.520, -62.264)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_1",
                                        Position = "(1.942, 19.390, -54.329)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_1",
                                        Position = "(0.245, 14.425, -38.233)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_1",
                                        Position = "(20.908, 12.703, -41.095)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_1",
                                        Position = "(23.553, 18.725, -60.847)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_1",
                                        Position = "(15.807, 19.579, -60.423)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_2",
                                        Position = "(10.950, 25.274, -66.322)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_2",
                                        Position = "(25.228, 23.016, -64.979)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_2",
                                        Position = "(25.219, 28.817, -82.992)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_2",
                                        Position = "(12.052, 31.805, -87.042)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_2",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_2",
                                        Position = "(15.021, 21.135, -55.081)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_3_sniper",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_sniper_1",
                                        Position = "(13.015, 44.087, -66.407)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "cargo_npc_3_sniper",
                                        NavMeshPresetName = "DynamicMonuments_cargo_back_sniper_1",
                                        Position = "(24.303, 42.417, -65.703)"
                                    },
                                },
                            }
                        }
                    },
                    RiverTypeConfig = new MonumentTypeConfig
                    {
                        IsAutoSpawn = false,
                        MinAmount = 2,
                        MaxAmount = 2,
                        Sites = new HashSet<SiteConfig>
                        {
                            new SiteConfig
                            {
                                PresetName = "rustedDam",
                                DisplayName = En ? "The Rusted Dam" : "Ржавая плотина",
                                DataFileName = "rustedDam",
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                SummonConfig = new SummonMonumentConfig
                                {
                                    Permission = "",
                                    SpawnDescriptionLang = "River_Decription",
                                    Shortname = "flare",
                                    Name = En ? "The Rusted Dam" : "Ржавая плотина",
                                    Skin = 3440565681,
                                },
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 5,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(5.332829, 3.4953, -8.008698)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-0.8516796, 6.110428, -5.478428)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(4.517034, 4.570877, -9.237579)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(13.76276, 1.51741, -8.42691)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(9.46082, -1.981186, 11.96347)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(8.844914, -1.981186, 12.59128)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(10.41701, -1.934433, 15.88662)",
                                        Rotation = "(0.00, 199.00, 352.10)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(0.5390033, -1.981201, 24.53662)",
                                        Rotation = "(0.00, 145.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(-2.113887, 1.569031, -7.79593)",
                                        Rotation = "(0.00, 37.02, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(12.23743, -1.429504, -6.61124)",
                                        Rotation = "(0.00, 274.28, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(6.418157, 5.443604, -5.249546)",
                                        Rotation = "(0.00, 167.43, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(8.083745, -1.429123, -5.153477)",
                                        Rotation = "(0.00, 83.62, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(26.08594, -1.922394, 20.11349)",
                                        Rotation = "(0.00, 282.16, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-0.9664257, -1.856888, -25.85477)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(3.587896, 1.570877, -9.41922)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-0.6305493, 1.570877, -5.280552)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-2.358943, -1.983551, 15.48337)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(15.89911, -1.785477, 29.82467)",
                                        Rotation = "(0.00, 29.34, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-4.507686, 5.524628, -22.7172)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(5.448247, 4.570877, -9.362091)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(3.751958, 4.570877, -5.260532)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(4.634526, 4.570877, -5.182652)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-4.398555, 5.524628, -0.4546232)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-3.418208, 5.524628, 5.761684)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-4.757747, 5.524628, 17.7579)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-2.855586, 5.524628, 22.37337)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(25.91407, -1.920731, 23.90768)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(23.65302, -1.922516, 25.01449)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(24.58179, -1.936081, 25.26742)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(19.09455, -1.762299, 28.77352)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(13.36268, -1.762299, 31.82565)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-4.611323, 7.557373, -11.15823)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-1.709651, 4.570877, -9.115387)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-0.5306957, 1.570877, -7.211338)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(10.82167, 6.032227, -5.631732)",
                                        Rotation = "(0.00, 342.66, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-3.016352, 7.557373, 11.33151)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(10.55359, -1.981186, 29.17953)",
                                        Rotation = "(0.00, 302.15, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(5.958004, -1.948807, 32.90762)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(20.83399, 5.191162, 33.20687)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(-2.190058, -1.983551, 30.83077)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-1.477412, -1.981186, 32.17709)",
                                        Rotation = "(0.00, 44.95, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>
                                {
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(3.59876, 1.570892, -6.216831)",
                                        Rotation = "(0.00, 111.00, 0.00)"
                                    },
                                    new PrefabLocationConfig
                                    {
                                        PrefabName = "assets/content/structures/excavator/prefabs/diesel_collectable.prefab",
                                        Position = "(0.6276295, 1.570892, -6.016392)",
                                        Rotation = "(0.00, 305.00, 0.00)"
                                    },
                                },
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>
                                {
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedDam_1",
                                        Position = "(-3.362, 5.525, -0.455)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedDam_1",
                                        Position = "(-3.362, 5.525, -5.584)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedDam_1",
                                        Position = "(-3.362, 5.525, -19.981)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedDam_1",
                                        Position = "(-3.362, 5.525, 20)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_2_sniper",
                                        NavMeshPresetName = "DynamicMonuments_rustedDam_2",
                                        Position = "(10.481, 9.254, -16.136)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedDam_3",
                                        Position = "(0.184, 4.571, -7.159)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedDam_3",
                                        Position = "(11.446, 4.571, -7.159)"
                                    },
                                },
                                GroudNpcs = new HashSet<NpcPresetLocationConfig>()
                                {
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        Position = "(-18, 0, -35)"
                                    }, 
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        Position = "(-18, 0, 35)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        Position = "(8, 0, 46)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        Position = "(8, 0, -45)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        Position = "(28, 0, -35)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedDam_npc_1",
                                        Position = "(35, 0, 35)"
                                    }
                                }
                            }
                        }
                    },
                    RoadTypeConfig = new MonumentTypeConfig
                    {
                        IsAutoSpawn = false,
                        MinAmount = 2,
                        MaxAmount = 2,
                        Sites = new HashSet<SiteConfig>
                        {
                            new SiteConfig
                            {
                                PresetName = "rustedGates",
                                DataFileName = "rustedGates",
                                DisplayName = En ? "The Rusted Gates" : "Ржавые ворота",
                                MinRespawnTime = 1800,
                                MaxRespawnTime = 3600,
                                EnableMarker = true,
                                IsAutoSpawn = true,
                                Probability = 100f,
                                Biomes = new HashSet<string>(),
                                Radiation = 5,
                                Crates = new HashSet<PresetLocationConfig>
                                {
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-15.57621, -1.770935, 0.1013353)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "foodbox_1",
                                        Position = "(-16.75162, -2.074524, 1.567537)",
                                        Rotation = "(0.00, 320.95, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(20.2648, -2.571014, -5.40312)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(23.29702, -2.571014, -5.017653)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(17.97573, -2.571014, -4.58827)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-17.6583, -2.571014, -3.639418)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-19.38035, -2.571014, -3.506377)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(-18.6503, -2.571014, -2.578184)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(23.98287, -2.520355, -1.244017)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "oil_barrel_1",
                                        Position = "(23.98287, -2.520355, 0.2016924)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-19.49307, -1.590012, -4.496307)",
                                        Rotation = "(0.00, 252.66, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-6.81211, 4.46524, -2.176756)",
                                        Rotation = "(0.00, 270.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-7.561927, 4.46524, -2.176756)",
                                        Rotation = "(0.00, 90.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(7.914635, 0.4033051, -2.13069)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_elite_1",
                                        Position = "(-8.837074, 0.4033051, -1.511366)",
                                        Rotation = "(0.00, 279.52, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_basic_1",
                                        Position = "(11.36629, 3.40332, 0.8197649)",
                                        Rotation = "(0.00, 87.74, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-9.542946, -2.571014, 0.830385)",
                                        Rotation = "(0.00, 267.27, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-24.72129, -2.408218, 1.438539)",
                                        Rotation = "(0.00, 104.46, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_tools_1",
                                        Position = "(-20.37302, -2.571014, 5.780733)",
                                        Rotation = "(0.00, 280.60, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-22.32584, -2.403259, -2.660887)",
                                        Rotation = "(0.00, 328.38, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(-10.72697, 0.4033051, -0.5221083)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_1",
                                        Position = "(6.759911, 1.345367, 0.9304369)",
                                        Rotation = "(0.00, 300.71, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-11.33164, -2.571014, -4.537809)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-10.61472, -2.571014, -3.724516)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(10.19369, -2.571014, 1.347078)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-1.156654, 3.40332, 2.323061)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(9.89016, -2.544418, 3.994234)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(11.05978, -2.468765, 5.359697)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_2_1",
                                        Position = "(-22.71531, -2.571014, 6.101763)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "loot_barrel_1_1",
                                        Position = "(-23.73832, -2.571014, 6.558672)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-7.422401, 0.4033051, 0.8766497)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(10.8193, 0.4033051, 1.118852)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_1",
                                        Position = "(-10.32047, -2.571014, 4.16437)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },

                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_food_1",
                                        Position = "(18.61215, -1.84938, 5.132418)",
                                        Rotation = "(0.00, 344.74, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(-18.49302, -2.444763, -7.937925)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(4.972924, 3.397583, 1.336534)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                    new PresetLocationConfig
                                    {
                                        PresetName = "crate_normal_2_medical_1",
                                        Position = "(25.10678, -2.571014, 3.338167)",
                                        Rotation = "(0.00, 0.00, 0.00)"
                                    },
                                },
                                RespawnEntities = new HashSet<PrefabLocationConfig>(),
                                StaticNpcs = new HashSet<NpcPresetLocationConfig>(),
                                CustomNavmeshNpc = new HashSet<MovableNpcPresetLocationConfig>
                                {
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_1",
                                        Position = "(8.751, 6.529, 0.820)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_1",
                                        Position = "(-8.751, 6.529, 0.820)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_2",
                                        Position = "(8.751, 3.403, 0.820)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_2",
                                        Position = "(0, 3.403, 0.820)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_2",
                                        Position = "(-8.751, 3.403, 0.820)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_3",
                                        Position = "(14.790, -2.57, 0.384)"
                                    },
                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_3",
                                        Position = "(23.607, -2.57, 3.897)"
                                    },

                                    new MovableNpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        NavMeshPresetName = "DynamicMonuments_rustedGates_4",
                                        Position = "(-14.439, -2.57, -2.578)"
                                    },
                                },
                                GroudNpcs = new HashSet<NpcPresetLocationConfig>()
                                {
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        Position = "(0, 0, 8)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        Position = "(0, 0, -8)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        Position = "(-12, 0, -23)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        Position = "(12, 0, -23)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        Position = "(-12, 0, 23)"
                                    },
                                    new NpcPresetLocationConfig
                                    {
                                        PresetName = "rustedGates_npc_1",
                                        Position = "(12, 0, 23)"
                                    },
                                }
                            }
                        }
                    },
                    CrateConfigs = new HashSet<CrateConfig>
                    {
                        new CrateConfig
                        {
                            PresetName = "foodbox_1",
                            PrefabName = "assets/bundled/prefabs/radtown/foodbox.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "oil_barrel_1",
                            PrefabName = "assets/bundled/prefabs/radtown/oil_barrel.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_elite_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_tools_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_tools.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_basic_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_basic.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "loot_barrel_1_1",
                            PrefabName = "assets/bundled/prefabs/radtown/loot_barrel_1.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "loot_barrel_2_1",
                            PrefabName = "assets/bundled/prefabs/radtown/loot_barrel_2.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_2_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal_2.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_2_food_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal_2_food.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_2_medical_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal_2_medical.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_underwater_basic_1",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_underwater_basic.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_food_1_1",
                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_food_2_1",
                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_2.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_2_underwater_1",
                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_normal_2.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "tech_parts_1_underwater_1",
                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_underwater_1",
                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_normal.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_fuel_underwater_1",
                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_fuel.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "vehicle_parts_underwater_1",
                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/vehicle_parts.prefab",
                            Skin = 0,
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "codelockedhackablecrate_oilrig_1",
                            PrefabName = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate_oilrig.prefab",
                            Skin = 0,
                            HackTime = 900,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "cloth",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 100,
                                        MaxAmount = 150,
                                        Chance = 100,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                    },
                    NpcConfigs = new HashSet<NpcConfig>
                    {
                        new NpcConfig
                        {
                            PresetName = "rustedGates_npc_1",
                            DisplayName = En ? "The guard of the gate" : "Охранник ржавых ворот",
                            Health = 260,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "mask.bandana",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "hat.boonie",
                                    SkinID = 2557702256
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 1282142258
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2080977144
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "pistol.m92",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight" },
                                    Ammo = ""
                                }
                            },
                            Speed = 7.5f,
                            RoamRange = 8f,
                            ChaseRange = 30,
                            AttackRangeMultiplier = 2f,
                            SenseRange = 60,
                            MemoryDuration = 10f,
                            DamageScale = 0.7f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = true,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = true,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 2,
                                            MaxLootScale = 2,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = true,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 2,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "syringe.medical",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 20,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "largemedkit",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 10,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "rustedDam_npc_1",
                            DisplayName = En ? "Keeper of the Rusted Dam" : "Смотритель ржавой дамбы",
                            Health = 275,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "hat.boonie",
                                    SkinID = 3274518283
                                },
                                new NpcWear
                                {
                                    ShortName = "attire.hide.poncho",
                                    SkinID = 835445469
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2985477298
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2985475422
                                },
                                new NpcWear
                                {
                                    ShortName = "burlap.gloves",
                                    SkinID = 1402323871
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "smg.thompson",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 7.3f,
                            RoamRange = 8f,
                            ChaseRange = 45,
                            AttackRangeMultiplier = 1.3f,
                            SenseRange = 60,
                            MemoryDuration = 20,
                            DamageScale = 0.25f,
                            AimConeScale = 1.2f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = true,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = true,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 2,
                                            MaxLootScale = 2,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = true,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 2,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "syringe.medical",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 20,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "largemedkit",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 10,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "rustedDam_npc_2_sniper",
                            DisplayName = En ? "Sniper of the Rusted Dam" : "Снайпер ржавой дамбы",
                            Health = 175,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 3332226899
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.plate.torso",
                                    SkinID = 3332223500
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2293185782
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2293180981
                                },
                                new NpcWear
                                {
                                    ShortName = "burlap.gloves",
                                    SkinID = 1402323871
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.bolt",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.small.scope" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 8f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 80,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = true,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = true,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 2,
                                            MaxLootScale = 2,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = true,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 2,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "syringe.medical",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 20,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "largemedkit",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 10,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "depotGates_npc_1",
                            DisplayName = En ? "The mechanic of the depot gate" : "Механик ворот депо",
                            Health = 260,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "hat.cap",
                                    SkinID = 1137543887
                                },
                                new NpcWear
                                {
                                    ShortName = "shirt.collared",
                                    SkinID = 1402339549
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 1402353612
                                },
                                new NpcWear
                                {
                                    ShortName = "burlap.gloves",
                                    SkinID = 1402323871
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "smg.2",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight" },
                                    Ammo = ""
                                }
                            },
                            Speed = 6f,
                            RoamRange = 8f,
                            ChaseRange = 40,
                            AttackRangeMultiplier = 1.4f,
                            SenseRange = 60,
                            MemoryDuration = 20f,
                            DamageScale = 0.34f,
                            AimConeScale = 1.1f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = true,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = true,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 2,
                                            MaxLootScale = 2,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = true,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 2,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "syringe.medical",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 20,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        Shortname = "largemedkit",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 10,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "oilPlatform_npc_1",
                            DisplayName = En ? "Oil Platform Guard" : "Охранник нефтяной платформы",
                            Health = 350,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "coffeecan.helmet",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.plate.torso",
                                    SkinID = 819160334
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 1679717140
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 1679719582
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "smg.2",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 8f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 60,
                            MemoryDuration = 10f,
                            DamageScale = 0.5f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = true,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = true,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 3,
                                            MaxLootScale = 3,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MinItemsAmount = 1,
                                MaxItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "scrap",
                                        Skin = 0,
                                        Name = "",
                                        MinAmount = 50,
                                        MaxAmount = 100,
                                        Chance = 50,
                                        IsBlueprint = false,
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "cargo_npc_1",
                            DisplayName = En ? "Guardian of the Broken Giant" : "Страж разломленного гиганта",
                            Health = 500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 3274815691
                                },
                                new NpcWear
                                {
                                    ShortName = "jacket",
                                    SkinID = 3299342065
                                },
                                new NpcWear
                                {
                                    ShortName = "burlap.shirt",
                                    SkinID = 3299444959
                                },
                                new NpcWear
                                {
                                    ShortName = "largebackpack",
                                    SkinID = 3360083868
                                },
                                new NpcWear
                                {
                                    ShortName = "burlap.trousers",
                                    SkinID = 3299445361
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.bolt",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.small.scope" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 8f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.5f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "cargo_npc_2",
                            DisplayName = En ? "The Caretaker of the Broken Giant" : "Смотритель разломленного гиганта",
                            Health = 500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 3274815691
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.plate.torso",
                                    SkinID = 3274816373
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 3318207180
                                },
                                new NpcWear
                                {
                                    ShortName = "largebackpack",
                                    SkinID = 3376718969
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 3318206106
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.m39",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 8f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.5f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "cargo_npc_3_sniper",
                            DisplayName = En ? "Sniper of the Broken Giant" : "Снайпер разломленного гиганта",
                            Health = 500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 2143679757
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 1552703337
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 1552705077
                                },
                                new NpcWear
                                {
                                    ShortName = "burlap.gloves",
                                    SkinID = 1552705918
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.bolt",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.small.scope" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 8f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.5f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = false,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = false,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "stoneQuarryNpc",
                            DisplayName = En ? "Stone Quarry Worker" : "Рабочий каменоломни",
                            Health = 210,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "hat.miner",
                                    SkinID = 886318490
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 1638742127
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 3495665503
                                }, 
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "pistol.revolver",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> {  },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "bandage",
                                    Amount = 5,
                                    SkinID = 0,
                                    Mods = new List<string> {  },
                                    Ammo = ""
                                },
                            },
                            Speed = 7.5f,
                            RoamRange = 10f,
                            ChaseRange = 40,
                            AttackRangeMultiplier = 3.5f,
                            SenseRange = 35,
                            MemoryDuration = 20,
                            DamageScale = 1f,
                            AimConeScale = 1.1f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = true,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = true,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = true,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "hqmQuarryNpc",
                            DisplayName = En ? "HQM Quarry Defender" : "Защитник МВК карьера",
                            Health = 210,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "knightsarmour.helmet",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "knighttorso.armour",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "knightsarmour.skirt",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.gloves",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 0
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "minicrossbow",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> {  },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "bandage",
                                    Amount = 5,
                                    SkinID = 0,
                                    Mods = new List<string> {  },
                                    Ammo = ""
                                },
                            },
                            Speed = 7.6f,
                            RoamRange = 15f,
                            ChaseRange = 35,
                            AttackRangeMultiplier = 1.2f,
                            SenseRange = 60,
                            MemoryDuration = 20f,
                            DamageScale = 0.36f,
                            AimConeScale = 1.1f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = true,
                            DeleteCorpse = true,
                            CanSleep = true,
                            SleepDistance = 100f,
                            LootTableConfig = new LootTableConfig
                            {
                                ClearDefaultItemList = true,
                                IsAlphaLoot = false,
                                AlphaLootPresetName = "",
                                IsCustomLoot = false,
                                IsLootTablePLugin = false,
                                PrefabConfigs = new PrefabLootTableConfigs
                                {
                                    IsEnable = true,
                                    Prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            MinLootScale = 1,
                                            MaxLootScale = 1,
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                IsRandomItemsEnable = false,
                                MaxItemsAmount = 1,
                                MinItemsAmount = 1,
                                Items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        Shortname = "bandage",
                                        MinAmount = 1,
                                        MaxAmount = 3,
                                        Chance = 30,
                                        IsBlueprint = false,
                                        Skin = 0,
                                        Name = "",
                                        Genomes = new List<string>()
                                    }
                                }
                            }
                        },
                    },
                    MarkerConfig = new MarkerConfig
                    {
                        UseRingMarker = true,
                        UseShopMarker = true,
                        Radius = 0.2f,
                        Alpha = 0.6f,
                        Color1 = new ColorConfig { R = 0.2f, G = 0.8f, B = 0.1f },
                        Color2 = new ColorConfig { R = 0f, G = 0f, B = 0f }
                    },
                    NotifyConfig = new NotifyConfig
                    {
                        ChatConfig = new ChatConfig
                        {
                            IsEnabled = false,
                        },
                        GameTipConfig = new GameTipConfig
                        {
                            IsEnabled = true,
                            Style = 1,
                        },
                        RedefinedMessages = new HashSet<RedefinedMessageConfig>
                        {
                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "GotMonument",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = false,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = true,
                                    Style = 2,
                                }
                            },
                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "ShoreLocation_Decription",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = true,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = false,
                                    Style = 2,
                                }
                            },

                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "GroundLocation_Decription",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = true,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = false,
                                    Style = 2,
                                }
                            },
                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "WaterLocation_Decription",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = true,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = false,
                                    Style = 2,
                                }
                            },
                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "PowerLines_Decription",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = true,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = false,
                                    Style = 2,
                                }
                            },
                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "River_Decription",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = true,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = false,
                                    Style = 2,
                                }
                            },
                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "BusStop_Decription",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = true,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = false,
                                    Style = 2,
                                }
                            },
                            new RedefinedMessageConfig
                            {
                                IsEnable = true,
                                LangKey = "Position_Suitable",
                                ChatConfig = new ChatConfig
                                {
                                    IsEnabled = false,
                                },
                                GameTipConfig = new GameTipConfig
                                {
                                    IsEnabled = true,
                                    Style = 2,
                                }
                            },
                        }
                    },
                };
            }
        }
        #endregion Configs
    }
}

namespace Oxide.Plugins.DynamicMonumentsExtensionMethods
{
    public static class ExtensionMethods
    {
        // ReSharper disable All
        public static bool Any<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) if (predicate(enumerator.Current)) return true;
            return false;
        }

        public static HashSet<TSource> Where<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            HashSet<TSource> result = new HashSet<TSource>();

            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    if (predicate(enumerator.Current))
                        result.Add(enumerator.Current);

            return result;
        }

        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) if (predicate(enumerator.Current)) return enumerator.Current;
            return default(TSource);
        }

        public static HashSet<TResult> Select<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, TResult> predicate)
        {
            HashSet<TResult> result = new HashSet<TResult>();
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) result.Add(predicate(enumerator.Current));
            return result;
        }

        public static List<TResult> Select<TSource, TResult>(this IList<TSource> source, Func<TSource, TResult> predicate)
        {
            List<TResult> result = new List<TResult>();
            for (int i = 0; i < source.Count; i++)
            {
                TSource element = source[i];
                result.Add(predicate(element));
            }
            return result;
        }

        public static bool IsExists(this BaseNetworkable entity) => entity != null && !entity.IsDestroyed;

        public static bool IsRealPlayer(this BasePlayer player) => player != null && player.userID.IsSteamId();

        public static List<TSource> OrderBy<TSource>(this IEnumerable<TSource> source, Func<TSource, float> predicate)
        {
            List<TSource> result = source.ToList();
            for (int i = 0; i < result.Count; i++)
            {
                for (int j = 0; j < result.Count - 1; j++)
                {
                    if (predicate(result[j]) > predicate(result[j + 1]))
                    {
                        TSource z = result[j];
                        result[j] = result[j + 1];
                        result[j + 1] = z;
                    }
                }
            }
            return result;
        }

        public static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
        {
            List<TSource> result = new List<TSource>();
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) result.Add(enumerator.Current);
            return result;
        }

        public static void Shuffle<T>(this IList<T> list)
        {
            System.Random rng = new System.Random();

            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source)
        {
            HashSet<TSource> result = new HashSet<TSource>();
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) result.Add(enumerator.Current);
            return result;
        }

        public static HashSet<T> OfType<T>(this IEnumerable<BaseNetworkable> source)
        {
            HashSet<T> result = new HashSet<T>();
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) if (enumerator.Current is T) result.Add((T)(object)enumerator.Current);
            return result;
        }

        public static TSource Max<TSource>(this IEnumerable<TSource> source, Func<TSource, float> predicate)
        {
            TSource result = source.ElementAt(0);
            float resultValue = predicate(result);
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    TSource element = enumerator.Current;
                    float elementValue = predicate(element);
                    if (elementValue > resultValue)
                    {
                        result = element;
                        resultValue = elementValue;
                    }
                }
            }
            return result;
        }

        public static TSource Min<TSource>(this IEnumerable<TSource> source, Func<TSource, float> predicate)
        {
            TSource result = source.ElementAt(0);
            float resultValue = predicate(result);
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    TSource element = enumerator.Current;
                    float elementValue = predicate(element);
                    if (elementValue < resultValue)
                    {
                        result = element;
                        resultValue = elementValue;
                    }
                }
            }
            return result;
        }

        public static TSource ElementAt<TSource>(this IEnumerable<TSource> source, int index)
        {
            int movements = 0;
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    if (movements == index) return enumerator.Current;
                    movements++;
                }
            }
            return default(TSource);
        }

        public static TSource First<TSource>(this IList<TSource> source) => source[0];

        public static TSource Last<TSource>(this IList<TSource> source) => source[source.Count - 1];

        public static bool IsEqualVector3(this Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.1f;

        public static List<TSource> OrderByQuickSort<TSource>(this List<TSource> source, Func<TSource, float> predicate)
        {
            return source.QuickSort(predicate, 0, source.Count - 1);
        }

        private static List<TSource> QuickSort<TSource>(this List<TSource> source, Func<TSource, float> predicate, int minIndex, int maxIndex)
        {
            if (minIndex >= maxIndex) return source;

            int pivotIndex = minIndex - 1;
            for (int i = minIndex; i < maxIndex; i++)
            {
                if (predicate(source[i]) < predicate(source[maxIndex]))
                {
                    pivotIndex++;
                    source.Replace(pivotIndex, i);
                }
            }
            pivotIndex++;
            source.Replace(pivotIndex, maxIndex);

            QuickSort(source, predicate, minIndex, pivotIndex - 1);
            QuickSort(source, predicate, pivotIndex + 1, maxIndex);

            return source;
        }

        private static void Replace<TSource>(this IList<TSource> source, int x, int y)
        {
            TSource t = source[x];
            source[x] = source[y];
            source[y] = t;
        }

        public static object GetPrivateFieldValue(this object obj, string fieldName)
        {
            FieldInfo fi = GetPrivateFieldInfo(obj.GetType(), fieldName);
            if (fi != null) return fi.GetValue(obj);
            else return null;
        }

        public static void SetPrivateFieldValue(this object obj, string fieldName, object value)
        {
            FieldInfo info = GetPrivateFieldInfo(obj.GetType(), fieldName);
            if (info != null) info.SetValue(obj, value);
        }

        public static FieldInfo GetPrivateFieldInfo(Type type, string fieldName)
        {
            foreach (FieldInfo fi in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)) if (fi.Name == fieldName) return fi;
            return null;
        }

        public static Action GetPrivateAction(this object obj, string methodName)
        {
            MethodInfo mi = obj.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi != null) return (Action)Delegate.CreateDelegate(typeof(Action), obj, mi);
            else return null;
        }

        public static object CallPrivateMethod(this object obj, string methodName, params object[] args)
        {
            MethodInfo mi = obj.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi != null) return mi.Invoke(obj, args);
            else return null;
        }
    }
}
