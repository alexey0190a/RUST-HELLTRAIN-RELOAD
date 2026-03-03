using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Plugins.ConvoyExtensionMethods;
using Rust;
using Rust.Modular;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Facepunch;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;
using static BaseVehicle;
using Random = UnityEngine.Random;
using Time = UnityEngine.Time;

namespace Oxide.Plugins
{
    [Info("Convoy", "pluginfuel.ru", "2.9.6")]
    internal class Convoy : RustPlugin
    {
        #region Variables
        private const bool En = false;
        private static Convoy _ins;
        private EventController _eventController;
        [PluginReference] Plugin ArmoredTrain, NpcSpawn, GUIAnnouncements, DiscordMessages, Notify, PveMode, Economics, ServerRewards, IQEconomic, DynamicPVP, AlphaLoot, CustomLoot, Loottable;
        private ProtectionProperties _protection;
        private EventHeli _eventHeli;

        private readonly HashSet<string> _subscribeMethods = new HashSet<string>
        {
            "OnEntitySpawned",
            "OnExplosiveThrown",
            "CanExplosiveStick",
            "CanMountEntity",
            "CanDismountEntity",
            "OnPlayerSleep",
            "CanPickupEntity",
            "OnEntityTakeDamage",
            "OnEntityDeath",
            "OnEntityKill",
            "CanHelicopterTarget",
            "OnCustomNpcTarget",
            "CanBradleyApcTarget",
            "CanHackCrate",
            "CanLootEntity",
            "OnLootEntity",
            "OnLootEntityEnd",
            "OnEntityEnter",
            
            "OnCorpsePopulate",
            "OnCrateSpawned",
            "OnContainerPopulate",
            "CanPopulateLoot",
            "OnCustomLootContainer",
            "OnCustomLootNPC",
            
            "CanEntityBeTargeted",
            "CanEntityTakeDamage",
            "CanBradleySpawnNpc",
            "CanHelicopterSpawnNpc",
            "OnCreateDynamicPVP",
            "SetOwnerPveMode",
            "ClearOwnerPveMode",
            "OnSetupTurret",
            "OnLifeSupportSavingLife"
        };
        #endregion Variables

        #region API
        private bool IsConvoyVehicle(BaseEntity entity)
        {
            if (_eventController == null || entity == null || entity.net == null)
                return false;

            if (ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) != null)
                return true;

            BaseVehicleModule baseVehicleModule = entity as BaseVehicleModule;
            if (baseVehicleModule != null)
            {
                BaseVehicle baseVehicle = baseVehicleModule.Vehicle;
                if (baseVehicle != null && baseVehicle.net != null)
                    return ConvoyPathVehicle.GetVehicleByNetId(baseVehicle.net.ID.Value) != null;
            }

            return false;
        }

        private bool IsConvoyCrate(BaseEntity crate)
        {
            if (_eventController == null || crate == null || crate.net == null)
                return false;

            return _eventController.IsEventCrate(crate.net.ID.Value);
        }

        private bool IsConvoyHeli(PatrolHelicopter patrolHelicopter)
        {
            if (_eventController == null || patrolHelicopter == null || patrolHelicopter.net == null)
                return false;

            return EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value) != null;
        }

        private bool IsConvoyNpc(ScientistNPC scientistNpc)
        {
            if (_eventController == null || scientistNpc == null || scientistNpc.net == null)
                return false;

            return NpcSpawnManager.GetScientistByNetId(scientistNpc.net.ID.Value) != null;
        }
        #endregion API

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
            LoadData();

            _protection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _protection.Add(1);

            PrefabController.CachePrefabs();
            GuiManager.LoadImages();
            PathManager.StartCachingRouts();
            EventLauncher.AutoStartEvent();
        }

        private void Unload()
        {
            EventLauncher.StopEvent(true);
            PathManager.OnPluginUnloaded();
            _ins = null;
        }

        private void OnEntitySpawned(TimedExplosive timedExplosive)
        {
            if (timedExplosive == null)
                return;

            if (timedExplosive.ShortPrefabName == "maincannonshell")
            {
                BradleyVehicle bradleyVehicle = ConvoyPathVehicle.GetClosestVehicle<BradleyVehicle>(timedExplosive.transform.position) as BradleyVehicle;
                if (bradleyVehicle == null)
                    return;

                if (Vector3.Distance(bradleyVehicle.transform.position, timedExplosive.transform.position) < 5f)
                    timedExplosive.SetCreatorEntity(bradleyVehicle.bradley);
            }
        }

        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon item)
        {
            if (!player.IsRealPlayer())
                return;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetClosestVehicle<ConvoyPathVehicle>(player.transform.position);

            if (convoyPathVehicle != null && Vector3.Distance(convoyPathVehicle.transform.position, player.transform.position) < 20f)
                _eventController.OnEventAttacked(player);
        }

        private object CanExplosiveStick(RFTimedExplosive rfTimedExplosive, BaseEntity entity)
        {
            if (rfTimedExplosive == null || entity == null || entity.net == null)
                return null;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value);
            if (convoyPathVehicle != null && rfTimedExplosive.GetFrequency() > 0)
                return false;

            return null;
        }

        private object CanMountEntity(BasePlayer player, BaseMountable entity)
        {
            if (!player.IsRealPlayer() || entity == null)
                return null;

            BaseEntity vehicle = entity.VehicleParent();

            if (vehicle != null && ConvoyPathVehicle.GetVehicleByNetId(vehicle.net.ID.Value) != null)
                return true;

            return null;
        }

        private object CanDismountEntity(BasePlayer player, BaseMountable baseMountable)
        {
            if (baseMountable == null || player == null || player.userID.IsSteamId())
                return null;

            BaseVehicle baseVehicle = baseMountable.VehicleParent();

            if (baseVehicle == null || baseVehicle.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(baseVehicle.net.ID.Value) != null)
                return true;

            return null;
        }

        private void OnPlayerSleep(BasePlayer player)
        {
            if (!player.IsRealPlayer())
                return;

            ZoneController.OnPlayerLeaveZone(player);
        }

        private object CanPickupEntity(BasePlayer player, Door door)
        {
            if (door == null || door.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(door.net.ID.Value) == null)
                return null;

            return false;
        }

        private object OnEntityTakeDamage(BaseVehicle baseVehicle, HitInfo info)
        {
            if (baseVehicle == null || baseVehicle.net == null || info == null)
                return null;

            return CheckIfVehicleAttacked(baseVehicle, info);
        }

        private object OnEntityTakeDamage(BradleyAPC bradley, HitInfo info)
        {
            if (bradley == null || bradley.net == null || info == null)
                return null;

            return CheckIfVehicleAttacked(bradley, info);
        }

        private object OnEntityTakeDamage(BaseVehicleModule baseVehicleModule, HitInfo info)
        {
            if (!baseVehicleModule.IsExists())
                return null;

            BaseModularVehicle modularCar = baseVehicleModule.Vehicle;
            if (modularCar == null || modularCar.net == null)
                return null;

            ModularCarVehicle modularCarVehicle = ConvoyPathVehicle.GetVehicleByNetId(modularCar.net.ID.Value) as ModularCarVehicle;
            if (modularCarVehicle == null)
                return null;

            if (CheckIfVehicleAttacked(modularCar, info) == null)
            {
                modularCar.health -= info.damageTypes.Total() * modularCarVehicle.ModularCarConfig.DamageScale / 5;

                if (!modularCar.IsDestroyed && modularCar.health <= 0)
                    modularCar.Kill(BaseNetworkable.DestroyMode.Gib);

                for (int i = 0; i <= modularCar.moduleSockets.Count; i++)
                {
                    if (modularCar.TryGetModuleAt(i, out BaseVehicleModule module))
                        module.SetHealth(module._maxHealth * modularCar.health / modularCar._maxHealth);
                }
            }

            return true;
        }

        private object CheckIfVehicleAttacked(BaseCombatEntity entity, HitInfo info)
        {
            if (ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) == null)
                return null;

            if (!info.InitiatorPlayer.IsRealPlayer())
                return true;

            if (!_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, true))
                return true;

            _eventController.OnEventAttacked(info.InitiatorPlayer);
            return null;
        }

        private object OnEntityTakeDamage(AutoTurret autoTurret, HitInfo info)
        {
            if (autoTurret == null || autoTurret.net == null || info == null)
                return null;

            return CheckIfConvoyChildAttacked(autoTurret, info);
        }

        private object OnEntityTakeDamage(Door door, HitInfo info)
        {
            if (door == null || door.net == null || info == null)
                return null;

            return CheckIfConvoyChildAttacked(door, info);
        }

        private object OnEntityTakeDamage(SamSite samSite, HitInfo info)
        {
            if (samSite == null || samSite.net == null || info == null)
                return null;

            return CheckIfConvoyChildAttacked(samSite, info);
        }

        private object CheckIfConvoyChildAttacked(BaseCombatEntity entity, HitInfo info)
        {
            if (ConvoyPathVehicle.GetVehicleByChildNetId(entity.net.ID.Value) == null)
                return null;

            if (!info.InitiatorPlayer.IsRealPlayer())
                return true;

            if (!_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, true))
                return true;

            _eventController.OnEventAttacked(info.InitiatorPlayer);

            return null;
        }

        private object OnEntityTakeDamage(ScientistNPC scientistNpc, HitInfo info)
        {
            if (scientistNpc == null || scientistNpc.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return null;

            if (NpcSpawnManager.GetScientistByNetId(scientistNpc.net.ID.Value) == null)
                return null;

            if (!_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, scientistNpc, true))
            {
                info.damageTypes.ScaleAll(0f);
                return true;
            }

            if (scientistNpc.isMounted)
                info.damageTypes.ScaleAll(10f);

            _eventController.OnEventAttacked(info.InitiatorPlayer);

            if (info.WeaponPrefab != null && _eventController.EventConfig.WeaponToScaleDamageNpc.TryGetValue(info.WeaponPrefab.ShortPrefabName, out float weaponDamageScale))
                info.damageTypes.ScaleAll(weaponDamageScale);

            return null;
        }

        private object OnEntityTakeDamage(StorageContainer storageContainer, HitInfo info)
        {
            if (storageContainer == null || storageContainer.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(storageContainer.net.ID.Value) != null)
                return true;

            return null;
        }

        private object OnEntityTakeDamage(Fridge fridge, HitInfo info)
        {
            if (fridge == null || fridge.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(fridge.net.ID.Value) != null)
                return true;

            return null;
        }

        private object OnEntityTakeDamage(PatrolHelicopter patrolHelicopter, HitInfo info)
        {
            if (patrolHelicopter == null || patrolHelicopter.net == null || info == null)
                return null;

            EventHeli eventHeli = EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value);

            if (eventHeli == null)
                return null;

            if (info.InitiatorPlayer.IsRealPlayer())
            {
                if (!_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, patrolHelicopter, true))
                    return true;
                else
                {
                    eventHeli.OnHeliAttacked(info.InitiatorPlayer.userID);
                    _eventController.OnEventAttacked(info.InitiatorPlayer);
                }
            }

            return null;
        }

        private void OnEntityTakeDamage(BuildingBlock buildingBlock, HitInfo info)
        {
            if (buildingBlock == null || info == null)
                return;

            CustomBradley customBradley = info.Initiator as CustomBradley;
            if (customBradley == null)
                return;

            if (customBradley.BradleyConfig.BradleyBuildingDamageScale >= 0)
                info.damageTypes.ScaleAll(customBradley.BradleyConfig.BradleyBuildingDamageScale);
        }

        private void OnEntityDeath(BradleyAPC bradleyApc, HitInfo info)
        {
            if (bradleyApc == null || bradleyApc.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(bradleyApc.net.ID.Value);

            if (convoyPathVehicle != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.SupportedPluginsConfig.EconomicsConfig.BradleyPoint);
        }

        private void OnEntityDeath(BasicCar basicCar, HitInfo info)
        {
            if (basicCar == null || basicCar.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByNetId(basicCar.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.SupportedPluginsConfig.EconomicsConfig.SedanPoint);
        }

        private void OnEntityDeath(ModularCar modularCar, HitInfo info)
        {
            if (modularCar == null || modularCar.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByNetId(modularCar.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.SupportedPluginsConfig.EconomicsConfig.ModularCarPoint);
        }

        private void OnEntityDeath(PatrolHelicopter patrolHelicopter, HitInfo info)
        {
            if (patrolHelicopter == null || patrolHelicopter.net == null)
                return;

            EventHeli eventHeli = EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value);

            if (eventHeli != null && eventHeli.lastAttackedPlayer != 0)
                EconomyManager.AddBalance(eventHeli.lastAttackedPlayer, _config.SupportedPluginsConfig.EconomicsConfig.HeliPoint);
        }

        private void OnEntityDeath(AutoTurret autoTurret, HitInfo info)
        {
            if (autoTurret == null || autoTurret.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(autoTurret.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.SupportedPluginsConfig.EconomicsConfig.TurretPoint);
        }

        private void OnEntityDeath(SamSite samSite, HitInfo info)
        {
            if (samSite == null || samSite.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(samSite.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.SupportedPluginsConfig.EconomicsConfig.SamsitePoint);
        }

        private void OnEntityDeath(ScientistNPC scientistNpc, HitInfo info)
        {
            if (scientistNpc == null || scientistNpc.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (NpcSpawnManager.GetScientistByNetId(scientistNpc.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.SupportedPluginsConfig.EconomicsConfig.NpcPoint);
        }

        private void OnEntityKill(BaseMountable baseMountable)
        {
            if (baseMountable == null || baseMountable.net == null)
                return;

            BaseVehicle baseVehicle = baseMountable as BaseVehicle;
            if (baseVehicle != null)
            {
                ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(baseVehicle.net.ID.Value);

                if (convoyPathVehicle == null)
                    return;

                foreach (MountPointInfo mountPointInfo in baseVehicle.allMountPoints)
                {
                    if (mountPointInfo == null || mountPointInfo.mountable == null)
                        continue;

                    BasePlayer mountedPlayer = mountPointInfo.mountable.GetMounted();

                    if (mountedPlayer.IsExists() && !mountedPlayer.userID.IsSteamId())
                        mountedPlayer.Kill();
                }

                if (EventLauncher.IsEventActive() && _ins._eventController.IsFullySpawned())
                    convoyPathVehicle.DropCrates();
            }

            if (ConvoyPathVehicle.GetVehicleByChildNetId(baseMountable.net.ID.Value) != null)
            {
                if (baseMountable._mounted.IsExists())
                    baseMountable._mounted.Kill();
            }
        }

        private void OnEntityKill(BradleyAPC bradleyApc)
        {
            if (bradleyApc == null || bradleyApc.net == null)
                return;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(bradleyApc.net.ID.Value);

            if (convoyPathVehicle != null && EventLauncher.IsEventActive() && _ins._eventController.IsFullySpawned())
                convoyPathVehicle.DropCrates();
        }

        private object CanHelicopterTarget(PatrolHelicopterAI heli, BasePlayer player)
        {
            if (heli == null || heli.helicopterBase == null || heli.helicopterBase.net == null)
                return null;

            EventHeli eventHeli = EventHeli.GetEventHeliByNetId(heli.helicopterBase.net.ID.Value);

            if (eventHeli != null && !eventHeli.IsHeliCanTarget())
                return false;

            if (player.IsSleeping() || player.InSafeZone())
                return false;

            return null;
        }

        private object OnCustomNpcTarget(ScientistNPC scientistNpc, BasePlayer player)
        {
            if (_eventController == null || scientistNpc == null || scientistNpc.net == null)
                return null;

            if (NpcSpawnManager.GetScientistByNetId(scientistNpc.net.ID.Value) == null)
                return null;

            if (!_eventController.IsAggressive())
                return false;
            else if (player.IsSleeping() || (player.InSafeZone() && !player.IsHostile()))
                return false;

            return null;
        }

        private object CanBradleyApcTarget(BradleyAPC bradley, BaseEntity entity)
        {
            if (bradley == null || bradley.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(bradley.net.ID.Value) == null)
                return null;

            BasePlayer targetPlayer = entity as BasePlayer;

            if (!targetPlayer.IsRealPlayer())
                return false;

            if (targetPlayer.IsSleeping() || (targetPlayer.InSafeZone() && !targetPlayer.IsHostile()))
                return false;

            return null;
        }

        private object CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (player == null || crate == null || crate.net == null)
                return null;

            if (_eventController.IsEventCrate(crate.net.ID.Value))
            {
                if (!_eventController.IsPlayerCanLoot(player, true))
                {
                    _eventController.SwitchAggressive(true);
                    return true;
                }

                EconomyManager.AddBalance(player.userID, _config.SupportedPluginsConfig.EconomicsConfig.LockedCratePoint);
            }

            return null;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer storageContainer)
        {
            return CanLootEventEntity(player, storageContainer);
        }

        private object CanLootEntity(BasePlayer player, Fridge fridge)
        {
            return CanLootEventEntity(player, fridge);
        }

        private object CanLootEventEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || entity.net == null)
                return null;

            if (_eventController.IsEventCrate(entity.net.ID.Value))
            {
                if (_eventController.IsPlayerCanLoot(player, true))
                    return null;

                _eventController.SwitchAggressive(true);
                return true;
            }
            else
            {
                BaseVehicleModule baseVehicleModule = entity.GetParentEntity() as BaseVehicleModule;

                if (baseVehicleModule == null || baseVehicleModule.net == null)
                    return null;

                if (ConvoyPathVehicle.GetVehicleByChildNetId(baseVehicleModule.net.ID.Value) != null)
                    return true;
            }

            return null;
        }

        private void OnLootEntity(BasePlayer player, StorageContainer storageContainer)
        {
            if (player == null || storageContainer == null || storageContainer.net == null)
                return;

            if (_eventController.IsEventCrate(storageContainer.net.ID.Value))
                _eventController.OnEventCrateLooted(storageContainer, player.userID);
        }

        private void OnLootEntity(BasePlayer player, Fridge fridge)
        {
            if (player == null || fridge == null || fridge.net == null)
                return;

            if (_eventController.IsEventCrate(fridge.net.ID.Value))
                _eventController.OnEventCrateLooted(fridge, player.userID);
        }

        private void OnLootEntityEnd(BasePlayer player, StorageContainer storageContainer)
        {
            if (storageContainer == null || storageContainer.net == null || !player.IsRealPlayer())
                return;

            if (storageContainer is not LootContainer && _eventController.IsEventCrate(storageContainer.net.ID.Value))
                if (storageContainer.inventory.IsEmpty())
                    storageContainer.Kill();
        }

        private void OnLootEntityEnd(BasePlayer player, Fridge fridge)
        {
            if (fridge == null || fridge.net == null || !player.IsRealPlayer())
                return;

            if (_eventController.IsEventCrate(fridge.net.ID.Value))
                if (fridge.inventory.IsEmpty())
                    fridge.Kill();
        }

        private object OnEntityEnter(TriggerVehiclePush trigger, BaseCombatEntity entity)
        {
            if (trigger == null || entity == null || entity.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) != null)
                return true;

            return null;
        }

        private object OnEntityEnter(TriggerPath trigger, BradleyAPC bradleyApc)
        {
            if (bradleyApc == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(bradleyApc.net.ID.Value) == null)
                return null;

            NextTick(() => bradleyApc.myRigidBody.isKinematic = false);
            return null;
        }

        private object OnEntityEnter(TriggerPath trigger, TravellingVendor travellingVendor)
        {
            if (travellingVendor == null || travellingVendor.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(travellingVendor.net.ID.Value) == null)
                return null;

            Rigidbody rigidbody = travellingVendor.GetComponentInChildren<Rigidbody>();
            NextTick(() =>
            {
                rigidbody.isKinematic = false;
            });

            return true;
        }

        private object OnEntityEnter(TargetTrigger trigger, ScientistNPC scientistNpc)
        {
            if (trigger == null || scientistNpc == null || scientistNpc.net == null)
                return null;

            AutoTurret autoTurret = trigger.GetComponentInParent<AutoTurret>();
            if (autoTurret == null || autoTurret.net == null)
                return null;

            if (NpcSpawnManager.GetScientistByNetId(scientistNpc.net.ID.Value) == null)
                return null;

            if (scientistNpc.isMounted || !_config.BehaviorConfig.IsPlayerTurretEnable)
                return true;

            return null;
        }

        private object OnEntityEnter(TriggerVehiclePush trigger, TravellingVendor travellingVendor)
        {
            if (trigger == null || travellingVendor == null || travellingVendor.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(travellingVendor.net.ID.Value))
                return true;

            return null;
        }
        #region Loot
        private void OnCorpsePopulate(ScientistNPC scientistNpc, NPCPlayerCorpse corpse)
        {
            if (scientistNpc == null || scientistNpc.net == null || corpse == null)
                return;
            
            EntityPresetInfo entityPresetInfo = scientistNpc.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return;
            
            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByPresetName(entityPresetInfo.presetName);
            if (npcConfig == null)
                return;
            
            LootController.UpdateNpcCorpse(corpse, npcConfig);
        }
        
        private void OnCrateSpawned(BradleyAPC bradleyApc, LockedByEntCrate crate)
        {
            if (bradleyApc == null || crate == null)
                return;
            
            EntityPresetInfo entityPresetInfo = bradleyApc.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return;

            BradleyConfig bradleyConfig = _config.BradleyConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (bradleyConfig == null)
                return;
            
            if (bradleyConfig.InstCrateOpen)
            {
                crate.SetLockingEnt(null);
                crate.SetLocked(false);
            }
            
            PveModeManager.AddCrate(crate.net.ID.Value);
            _eventController.AddEventCrate(crate);
            LootController.UpdateHeliOrBradleyCrate(crate, bradleyConfig.LootTableConfig, bradleyConfig.PresetName);
        }

        private void OnCrateSpawned(PatrolHelicopter patrolHelicopter, LockedByEntCrate crate)
        {
            if (patrolHelicopter == null || crate == null)
                return;
            
            EntityPresetInfo entityPresetInfo = patrolHelicopter.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return;
            
            HeliConfig heliConfig = _config.HeliConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (heliConfig == null)
                return;
            
            if (heliConfig.InstCrateOpen)
            {
                crate.SetLockingEnt(null);
                crate.SetLocked(false);
            }
            
            PveModeManager.AddCrate(crate.net.ID.Value);
            _eventController.AddEventCrate(crate);
            LootController.UpdateHeliOrBradleyCrate(crate, heliConfig.LootTableConfig, heliConfig.PresetName);
        }
        
        #region LootTablePlugin
        private object OnContainerPopulate(LootContainer lootContainer)
        {
            if (lootContainer == null)
                return null;
            
            LockedByEntCrate lockedByEntCrate = lootContainer as LockedByEntCrate;
            if (lockedByEntCrate != null)
            {
                LootTableConfig lootTableConfig = null;

                if (_eventHeli != null && Vector3.Distance(_eventHeli.transform.position, lockedByEntCrate.transform.position) < 15f)
                    lootTableConfig = _eventHeli.HeliConfig.LootTableConfig;

                if (lootTableConfig == null)
                {
                    ConvoyPathVehicle closestVehicle = ConvoyPathVehicle.GetClosestVehicle<BradleyVehicle>(lockedByEntCrate.transform.position);
                    if (closestVehicle == null)
                        return null;

                    BradleyVehicle bradleyVehicle = closestVehicle as BradleyVehicle;
                    if (bradleyVehicle == null)
                        return null;

                    if (Vector3.Distance(bradleyVehicle.transform.position, lockedByEntCrate.transform.position) > 15f)
                        return null;

                    lootTableConfig = bradleyVehicle.BradleyConfig.LootTableConfig;
                }
                
                if (lootTableConfig == null)
                    return null;

                if (!lootTableConfig.IsLoottablePlugin)
                    return true;
                    
                return null;
            }
            
            EntityPresetInfo entityPresetInfo = lootContainer.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return null;

            CrateConfig crateConfig = _config.CrateConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (crateConfig == null)
                return null;
            
            if (!crateConfig.LootTableConfig.IsLoottablePlugin)
                return true;
            
            return null;
        }

        private object OnCorpsePopulate(NPCPlayerCorpse corpse)
        {
            if (corpse == null)
                return null;

            EntityPresetInfo entityPresetInfo = corpse.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return null;
            
            NpcConfig npcConfig =  _config.NpcConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (npcConfig == null)
                return null;
            
            if (!npcConfig.LootTableConfig.IsLoottablePlugin)
                return true;
            
            return null;
        }
        #endregion LootTablePlugin
        
        #region AlphaLoot
        private object CanPopulateLoot(LootContainer lootContainer)
        {
            if (lootContainer == null)
                return null;

            EntityPresetInfo entityPresetInfo = lootContainer.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return null;

            LockedByEntCrate lockedByEntCrate = lootContainer as LockedByEntCrate;
            if (lockedByEntCrate != null)
            {
                LootTableConfig lootTableConfig = null;
                BradleyConfig bradleyConfig = _config.BradleyConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
                if (bradleyConfig != null)
                    lootTableConfig = bradleyConfig.LootTableConfig;
                
                if (lootTableConfig == null)
                {
                    HeliConfig heliConfig = _config.HeliConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
                    if (heliConfig != null)
                        lootTableConfig = heliConfig.LootTableConfig;
                }
                
                if (lootTableConfig == null)
                    return null;

                if (!lootTableConfig.IsAlphaLoot)
                    return true;
                    
                return null;
            }

            CrateConfig crateConfig = _config.CrateConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (crateConfig == null)
                return null;
            
            if (!crateConfig.LootTableConfig.IsAlphaLoot)
                return true;
            
            return null;
        }

        private object CanPopulateLoot(ScientistNPC scientistNpc, NPCPlayerCorpse corpse)
        {
            if (corpse == null)
                return null;

            EntityPresetInfo entityPresetInfo = corpse.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return null;
            
            NpcConfig npcConfig =  _config.NpcConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (npcConfig == null)
                return null;
            
            if (!npcConfig.LootTableConfig.IsAlphaLoot)
                return true;
            
            return null;
        }
        #endregion AlphaLoot
        
        #region CustomLoot
        private object OnCustomLootContainer(NetworkableId net)
        {
            BaseEntity crateEntity = _eventController.GetCrateByNetId(net.Value);
            if (crateEntity == null)
                return null;
            
            EntityPresetInfo entityPresetInfo = crateEntity.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return null;
            
            LockedByEntCrate lockedByEntCrate = crateEntity as LockedByEntCrate;
            if (lockedByEntCrate != null)
            {
                LootTableConfig lootTableConfig = null;
                BradleyConfig bradleyConfig =  _config.BradleyConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
                if (bradleyConfig != null)
                    lootTableConfig = bradleyConfig.LootTableConfig;
                
                if (lootTableConfig == null)
                {
                    HeliConfig heliConfig = _config.HeliConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
                    if (heliConfig != null)
                        lootTableConfig = heliConfig.LootTableConfig;
                }
                
                if (lootTableConfig == null)
                    return null;
                
                if (!lootTableConfig.IsCustomLootPlugin)
                    return true;
                    
                return null;
            }
            
            CrateConfig crateConfig = _config.CrateConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (crateConfig == null)
                return null;
            
            if (!crateConfig.LootTableConfig.IsCustomLootPlugin)
                return true;
            
            return null;
        }

        private object OnCustomLootNPC(NetworkableId net)
        {
            ScientistNPC scientistNpc = NpcSpawnManager.GetScientistByNetId(net.Value);
            if (scientistNpc == null)
                return null;
            
            EntityPresetInfo entityPresetInfo = scientistNpc.GetComponent<EntityPresetInfo>();
            if (entityPresetInfo == null)
                return null;
            
            NpcConfig npcConfig =  _config.NpcConfigs.FirstOrDefault(x => x.PresetName == entityPresetInfo.presetName);
            if (npcConfig == null)
                return null;

            if (!npcConfig.LootTableConfig.IsCustomLootPlugin)
                return true;
            
            return null;
        }
        #endregion CustomLoot

        #endregion Loot

        #region OtherPlugins

        private object CanPveModeAllowDamage(AutoTurret autoTurret, HitInfo info)
        {
            if (_eventController == null || autoTurret == null || autoTurret.net == null || info == null || info.InitiatorPlayer == null)
                return null;
            
            if (ConvoyPathVehicle.GetVehicleByChildNetId(autoTurret.net.ID.Value) == null)
                return null;
            
            if (!_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, autoTurret, true))
                return true;

            return null;
        }
        
        private object CanPveModeAllowDamage(SamSite samSite, HitInfo info)
        {
            if (_eventController == null || samSite == null || samSite.net == null || info == null || info.InitiatorPlayer == null)
                return null;
            
            if (ConvoyPathVehicle.GetVehicleByChildNetId(samSite.net.ID.Value) == null)
                return null;
            
            if (!_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, samSite, true))
                return true;

            return null;
        }
        
        private object CanEntityBeTargeted(BasePlayer player, AutoTurret turret)
        {
            if (_eventController == null || turret == null || turret.net == null || turret.OwnerID != 0)
                return null;

            if (!_eventController.IsEventTurret(turret.net.ID.Value))
                return null;

            if (!player.IsRealPlayer())
                return false;
            else if (!_eventController.IsAggressive())
                return false;
            else if (!PveModeManager.IsPveModDefaultBlockAction(player))
                return true;

            return null;
        }

        private object CanEntityBeTargeted(PlayerHelicopter playerHelicopter, SamSite samSite)
        {
            if (_eventController == null || samSite == null || samSite.net == null || samSite.OwnerID != 0)
                return null;

            if (!_eventController.IsEventSamSite(samSite.net.ID.Value))
                return null;

            if (!_eventController.IsAggressive())
                return false;

            return true;
        }

        private object CanEntityTakeDamage(Bike bike, HitInfo info)
        {
            if (_eventController == null || info == null || !bike.IsExists() || bike.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(bike, info);
        }

        private object CanEntityTakeDamage(ModularCar modularCar, HitInfo info)
        {
            if (_eventController == null || info == null || !modularCar.IsExists() || modularCar.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(modularCar, info);
        }

        private object CanEntityTakeDamage(BaseVehicleModule baseVehicleModule, HitInfo info)
        {
            if (_eventController == null || info == null || !baseVehicleModule.IsExists() || baseVehicleModule.net == null)
                return null;

            BaseModularVehicle modularCar = baseVehicleModule.Vehicle;

            if (modularCar == null || modularCar.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(modularCar, info);
        }

        private object CanEntityTakeDamage(BradleyAPC bradleyApc, HitInfo info)
        {
            if (_eventController == null || info == null || !bradleyApc.IsExists() || bradleyApc.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(bradleyApc, info);
        }

        private object CanEntityTakeDamage(BasicCar basicCar, HitInfo info)
        {
            if (_eventController == null || info == null || !basicCar.IsExists() || basicCar.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(basicCar, info);
        }

        private object CanConvoyVehicleTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) == null)
                return null;

            if (info.InitiatorPlayer.IsRealPlayer())
            {
                if (_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, shouldSendMessages: false) && !PveModeManager.IsPveModDefaultBlockAction(info.InitiatorPlayer))
                    return true;
            }

            return false;
        }

        private object CanEntityTakeDamage(SamSite samSite, HitInfo info)
        {
            if (_eventController == null || info == null || !samSite.IsExists() || samSite.net == null)
                return null;

            return CheckTruePveDamageToConvoyChild(samSite, info);
        }

        private object CanEntityTakeDamage(AutoTurret autoTurret, HitInfo info)
        {
            if (_eventController == null || info == null || !autoTurret.IsExists() || autoTurret.net == null)
                return null;

            return CheckTruePveDamageToConvoyChild(autoTurret, info);
        }

        private object CanEntityTakeDamage(Door door, HitInfo info)
        {
            if (door == null || door.net == null || info == null)
                return null;

            return CheckTruePveDamageToConvoyChild(door, info);
        }

        private object CheckTruePveDamageToConvoyChild(BaseCombatEntity entity, HitInfo info)
        {
            if (ConvoyPathVehicle.GetVehicleByChildNetId(entity.net.ID.Value) == null)
                return null;

            if (info.InitiatorPlayer.IsRealPlayer())
            {
                if (_eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, shouldSendMessages: true) && !PveModeManager.IsPveModDefaultBlockAction(info.InitiatorPlayer))
                    return true;
                else
                    return false;
            }
            else
                return false;
        }

        private object CanEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            if (info == null || info.Initiator == null || info.Initiator.net == null)
                return null;

            if (victim.IsRealPlayer())
            {
                if (_config.ZoneConfig.IsPvpZone && !_config.SupportedPluginsConfig.PveMode.Enable)
                {
                    if (info.InitiatorPlayer.IsRealPlayer() && ZoneController.IsPlayerInZone(info.InitiatorPlayer.userID) && ZoneController.IsPlayerInZone(victim.userID))
                        return true;
                }

                if (info.Initiator is AutoTurret or SamSite)
                {
                    if (ConvoyPathVehicle.GetVehicleByChildNetId(info.Initiator.net.ID.Value) != null)
                        return true;
                }
            }

            ScientistNPC scientistNpc = victim as ScientistNPC;
            if (scientistNpc != null && scientistNpc.net != null && NpcSpawnManager.GetScientistByNetId(scientistNpc.net.ID.Value) != null)
            {
                AutoTurret autoTurret = info.Initiator as AutoTurret;
                if (autoTurret != null && autoTurret.net != null && !_eventController.IsEventTurret(autoTurret.net.ID.Value))
                {
                    if (_config.SupportedPluginsConfig.PveMode.Enable)
                        return null;

                    return true;
                }
            }

            return null;
        }

        private object CanEntityTakeDamage(PlayerHelicopter playerHelicopter, HitInfo info)
        {
            if (info == null || info.Initiator == null || info.Initiator.net == null)
                return null;

            if (info.Initiator is SamSite)
            {
                if (ConvoyPathVehicle.GetVehicleByChildNetId(info.Initiator.net.ID.Value) != null)
                    return true;
            }

            return null;
        }


        private object CanBradleySpawnNpc(BradleyAPC bradley)
        {
            if (_eventController == null || bradley == null || bradley.net == null)
                return null;

            if (!_config.SupportedPluginsConfig.BetterNpcConfig.BradleyNpc && ConvoyPathVehicle.GetVehicleByNetId(bradley.net.ID.Value) != null)
                return true;

            return null;
        }

        private object CanHelicopterSpawnNpc(PatrolHelicopter helicopter)
        {
            if (_eventController == null || helicopter == null || helicopter.net == null)
                return null;

            if (!_config.SupportedPluginsConfig.BetterNpcConfig.HeliNpc && EventHeli.GetEventHeliByNetId(helicopter.net.ID.Value) != null)
                return true;

            return null;
        }

        private object OnCreateDynamicPVP(string eventName, PatrolHelicopter patrolHelicopter)
        {
            if (_eventController == null || patrolHelicopter == null || patrolHelicopter.net == null)
                return null;

            if (EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value) != null)
                return true;

            return null;
        }

        private object OnCreateDynamicPVP(string eventName, BradleyAPC bradleyApc)
        {
            if (_eventController == null || bradleyApc == null || bradleyApc.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(bradleyApc.net.ID.Value))
                return true;

            return null;
        }

        private void SetOwnerPveMode(string shortname, BasePlayer player)
        {
            if (_eventController == null || string.IsNullOrEmpty(shortname) || shortname != Name || !player.IsRealPlayer())
                return;

            if (shortname == Name)
                PveModeManager.OnNewOwnerSet(player);
        }

        private void ClearOwnerPveMode(string shortname)
        {
            if (_eventController == null || string.IsNullOrEmpty(shortname))
                return;

            if (shortname == Name)
                PveModeManager.OnOwnerDeleted();
        }

        private object OnSetupTurret(AutoTurret autoTurret)
        {
            if (autoTurret == null || autoTurret.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(autoTurret.net.ID.Value) != null)
                return true;

            return null;
        }

        private object OnSetupTurret(SamSite samSite)
        {
            if (samSite == null || samSite.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(samSite.net.ID.Value) != null)
                return true;

            return null;
        }

        private object OnLifeSupportSavingLife(BasePlayer player)
        {
            if (player == null)
                return null;

            if (!_config.SupportedPluginsConfig.LifeSupportConfig.IsDisabled)
                return null;
            
            if (ZoneController.IsPlayerInZone(player.userID))
                return true;
            
            return null;
        }
        #endregion OtherPlugins
        #endregion Hooks

        #region Commands
        [ChatCommand("convoystart")]
        private void ChatStartCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            if (arg == null || arg.Length == 0)
                EventLauncher.DelayStartEvent(false, player);
            else
            {
                string eventPresetName = arg[0];
                EventLauncher.DelayStartEvent(false, player, eventPresetName);
            }
        }

        [ConsoleCommand("convoystart")]
        private void ConsoleStartEventCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
                return;

            if (arg == null || arg.Args == null || arg.Args.Length == 0)
            {
                EventLauncher.DelayStartEvent();
            }
            else
            {
                string eventPresetName = arg.Args[0];
                EventLauncher.DelayStartEvent(presetName: eventPresetName);
            }
        }

        [ChatCommand("convoystop")]
        private void ChatStopCommand(BasePlayer player, string command, string[] arg)
        {
            if (player.IsAdmin)
                EventLauncher.StopEvent();
        }

        [ConsoleCommand("convoystop")]
        private void ConsoleStopEventCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
                return;

            EventLauncher.StopEvent();
        }

        [ChatCommand("convoyroadblock")]
        private void ChatRoadBlockCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            PathList blockRoad = TerrainMeta.Path.Roads.FirstOrDefault(x => x.Path.Points.Any(y => Vector3.Distance(player.transform.position, y) < 10));

            if (blockRoad == null)
            {
                NotifyManager.SendMessageToPlayer(player, $"{_config.Prefix} Road <color=#ce3f27>not found</color>. Step onto the required road and enter the command again.");
                return;
            }

            int index = TerrainMeta.Path.Roads.IndexOf(blockRoad);

            if (!_config.PathConfig.BlockRoads.Add(index))
            {
                NotifyManager.SendMessageToPlayer(player, $"{_config.Prefix} The road is already <color=#ce3f27>blocked</color>");
                return;
            }

            SaveConfig();

            NotifyManager.SendMessageToPlayer(player, $"{_config.Prefix} The road with the index <color=#738d43>{index}</color> is <color=#ce3f27>blocked</color>");
        }

        [ChatCommand("convoyshowpath")]
        private void ChatShowPathCommand(BasePlayer player, string command, string[] arg)
        {
            if (player.IsAdmin)
                PathManager.DrawPath(PathManager.CurrentPath, player);
        }

        [ChatCommand("convoypathstart")]
        private void ChatPathStartCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            PathRecorder.StartRecordingRoute(player);
        }

        [ChatCommand("convoypathsave")]
        private void ChatPathSaveCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            if (arg.Length == 0)
            {
                NotifyManager.SendMessageToPlayer(player, "CustomRouteDescription", _ins._config.Prefix);
                return;
            }

            string pathName = arg[0];
            PathRecorder.TrySaveRoute(player.userID, pathName);
        }

        [ChatCommand("convoypathcancel")]
        private void ChatPathCancelCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            PathRecorder.TryCancelRoute(player.userID);
        }

        [ChatCommand("convoystartmoving")]
        private void ChatStartMovingCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            _ins._eventController.SwitchMoving(true);
        }

        [ChatCommand("convoystopmoving")]
        private void ChatStopMovingCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            _ins._eventController.SwitchMoving(false);
        }
        #endregion Commands

        #region Methods
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
        #endregion Methods

        #region Classes
        private static class EventLauncher
        {
            private static Coroutine _autoEventCoroutine;
            private static Coroutine _delayedEventStartCoroutine;
            private static Coroutine _fixedScheduleStopCoroutine;
            private static DateTime? _activeFixedScheduleWindowEndUtc;

            public static bool IsEventActive()
            {
                return _ins != null && _ins._eventController != null;
            }

            public static void AutoStartEvent()
            {
                if (!_ins._config.MainConfig.IsAutoEvent)
                    return;

                if (_autoEventCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_autoEventCoroutine);

                if (_ins._config.MainConfig.FixedSchedule.Enabled)
                    _autoEventCoroutine = ServerMgr.Instance.StartCoroutine(FixedScheduleAutoEventCoroutine());
                else
                    _autoEventCoroutine = ServerMgr.Instance.StartCoroutine(AutoEventCoroutine());
            }

            public static void DelayStartEvent(bool isAutoActivated = false, BasePlayer activator = null, string presetName = null, bool usePreStartTime = true)
            {
                if (IsEventActive() || _delayedEventStartCoroutine != null)
                {
                    NotifyManager.PrintError(activator, "EventActive_Exeption");
                    return;
                }

                if (_autoEventCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_autoEventCoroutine);

                EventConfig eventConfig = DefineEventConfig(presetName);
                if (eventConfig == null)
                {
                    NotifyManager.PrintError(activator, "ConfigurationNotFound_Exeption");
                    StopEvent();
                    return;
                }

                _delayedEventStartCoroutine = ServerMgr.Instance.StartCoroutine(DelayedStartEventCoroutine(eventConfig, usePreStartTime));

                if (!isAutoActivated)
                    NotifyManager.PrintInfoMessage(activator, "SuccessfullyLaunched");
            }

            private static IEnumerator AutoEventCoroutine()
            {
                yield return CoroutineEx.waitForSeconds(Random.Range(_ins._config.MainConfig.MinTimeBetweenEvents, _ins._config.MainConfig.MaxTimeBetweenEvents));
                yield return CoroutineEx.waitForSeconds(5f);
                DelayStartEvent(true);
            }

            private static IEnumerator FixedScheduleAutoEventCoroutine()
            {
                DateTime startUtc;
                DateTime endUtc;
                if (!TryGetNextFixedScheduleWindowUtc(DateTime.UtcNow, out startUtc, out endUtc))
                    yield break;

                double waitSeconds = (startUtc - DateTime.UtcNow).TotalSeconds;
                if (waitSeconds > 0)
                    yield return CoroutineEx.waitForSeconds((float)waitSeconds);

                _activeFixedScheduleWindowEndUtc = endUtc;
                DelayStartEvent(true, null, null, false);
            }

            private static IEnumerator DelayedStartEventCoroutine(EventConfig eventConfig, bool usePreStartTime)
            {
                if (usePreStartTime && _ins._config.MainConfig.PreStartTime > 0)
                    NotifyManager.SendMessageToAll("PreStart", _ins._config.Prefix, _ins._config.MainConfig.PreStartTime);

                if (usePreStartTime && _ins._config.MainConfig.PreStartTime > 0)
                    yield return CoroutineEx.waitForSeconds(_ins._config.MainConfig.PreStartTime);

                StartEvent(eventConfig);
            }

            private static void StartEvent(EventConfig eventConfig)
            {
                PathManager.GenerateNewPath();

                GameObject gameObject = new GameObject();
                _ins._eventController = gameObject.AddComponent<EventController>();
                _ins._eventController.Init(eventConfig);

                if (_activeFixedScheduleWindowEndUtc != null)
                {
                    if (_fixedScheduleStopCoroutine != null)
                        ServerMgr.Instance.StopCoroutine(_fixedScheduleStopCoroutine);

                    _fixedScheduleStopCoroutine = ServerMgr.Instance.StartCoroutine(FixedScheduleStopEventCoroutine(_activeFixedScheduleWindowEndUtc.Value));
                }

                if (_ins._config.MainConfig.EnableStartStopLogs)
                    NotifyManager.PrintLogMessage("EventStart_Log", eventConfig.PresetName);

                Interface.CallHook($"On{_ins.Name}Start");
            }

            public static void StopEvent(bool isPluginUnloadingOrFailed = false)
            {
                if (IsEventActive())
                {
                    _ins.Unsubscribes();

                    _ins._eventController.DeleteController();
                    ZoneController.TryDeleteZone();
                    PveModeManager.OnEventEnd();
                    EventMapMarker.DeleteMapMarker();
                    NpcSpawnManager.ClearData(true);
                    GuiManager.DestroyAllGui();
                    EconomyManager.OnEventEnd();
                    EventHeli.ClearData();

                    NotifyManager.SendMessageToAll("Finish", _ins._config.Prefix);
                    Interface.CallHook($"On{_ins.Name}Stop");

                    if (_ins._config.MainConfig.EnableStartStopLogs)
                        NotifyManager.PrintLogMessage("EventStop_Log");

                    if (!isPluginUnloadingOrFailed)
                        AutoStartEvent();
                }

                if (_delayedEventStartCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_delayedEventStartCoroutine);
                    _delayedEventStartCoroutine = null;
                }

                if (_fixedScheduleStopCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_fixedScheduleStopCoroutine);
                    _fixedScheduleStopCoroutine = null;
                }

                _activeFixedScheduleWindowEndUtc = null;
            }

            public static int GetRemainFixedScheduleWindowSeconds()
            {
                if (_activeFixedScheduleWindowEndUtc == null)
                    return -1;

                return Math.Max(0, (int)Math.Ceiling((_activeFixedScheduleWindowEndUtc.Value - DateTime.UtcNow).TotalSeconds));
            }

            private static IEnumerator FixedScheduleStopEventCoroutine(DateTime endUtc)
            {
                double remainSeconds = (endUtc - DateTime.UtcNow).TotalSeconds;
                if (remainSeconds > 0)
                    yield return CoroutineEx.waitForSeconds((float)remainSeconds);

                if (IsEventActive())
                    StopEvent();
            }

            private static bool TryGetNextFixedScheduleWindowUtc(DateTime nowUtc, out DateTime startUtc, out DateTime endUtc)
            {
                startUtc = default;
                endUtc = default;

                FixedScheduleConfig fixedScheduleConfig = _ins._config.MainConfig.FixedSchedule;
                if (fixedScheduleConfig == null || fixedScheduleConfig.Windows == null || fixedScheduleConfig.Windows.Count == 0)
                    return false;

                TimeSpan offset = TimeSpan.FromHours(fixedScheduleConfig.UtcOffsetHours);
                DateTime localNow = nowUtc + offset;
                bool isFound = false;

                foreach (FixedScheduleWindowConfig window in fixedScheduleConfig.Windows)
                {
                    TimeSpan startTime;
                    TimeSpan endTime;
                    if (!TryParseHhMm(window.Start, out startTime) || !TryParseHhMm(window.End, out endTime))
                        continue;

                    for (int dayOffset = 0; dayOffset <= 1; dayOffset++)
                    {
                        DateTime startLocal = localNow.Date.AddDays(dayOffset).Add(startTime);
                        DateTime endLocal = localNow.Date.AddDays(dayOffset).Add(endTime);
                        if (endLocal <= startLocal)
                            endLocal = endLocal.AddDays(1);

                        DateTime candidateStartUtc = startLocal - offset;
                        if (candidateStartUtc <= nowUtc)
                            continue;

                        DateTime candidateEndUtc = endLocal - offset;

                        if (!isFound || candidateStartUtc < startUtc)
                        {
                            startUtc = candidateStartUtc;
                            endUtc = candidateEndUtc;
                            isFound = true;
                        }
                    }
                }

                return isFound;
            }

            private static bool TryParseHhMm(string value, out TimeSpan timeSpan)
            {
                timeSpan = default;
                if (string.IsNullOrEmpty(value))
                    return false;

                string[] parts = value.Split(':');
                if (parts.Length != 2)
                    return false;

                int hours;
                int minutes;
                if (!int.TryParse(parts[0], out hours) || !int.TryParse(parts[1], out minutes))
                    return false;

                if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
                    return false;

                timeSpan = new TimeSpan(hours, minutes, 0);
                return true;
            }

            private static EventConfig DefineEventConfig(string eventPresetName)
            {
                if (!string.IsNullOrEmpty(eventPresetName))
                    return _ins._config.EventConfigs.FirstOrDefault(x => x.PresetName == eventPresetName);

                HashSet<EventConfig> suitableEventConfigs = _ins._config.EventConfigs.Where(x => x.Chance > 0 && x.IsAutoStart && IsEventConfigSuitableByTime(x));
                if (suitableEventConfigs == null || suitableEventConfigs.Count == 0)
                    return null;

                float sumChance = 0;
                foreach (EventConfig eventConfig in suitableEventConfigs)
                    sumChance += eventConfig.Chance;

                float random = Random.Range(0, sumChance);

                foreach (EventConfig eventConfig in suitableEventConfigs)
                {
                    random -= eventConfig.Chance;

                    if (random <= 0)
                        return eventConfig;
                }

                return null;
            }

            private static bool IsEventConfigSuitableByTime(EventConfig eventConfig)
            {
                if (eventConfig.MinTimeAfterWipe <= 0 && eventConfig.MaxTimeAfterWipe <= 0)
                    return true;

                int timeScienceWipe = GetTimeScienceLastWipe();
                if (eventConfig.MinTimeAfterWipe > 0 && timeScienceWipe < eventConfig.MinTimeAfterWipe)
                    return false;
                
                if (eventConfig.MaxTimeAfterWipe > 0 && timeScienceWipe > eventConfig.MaxTimeAfterWipe)
                    return false;

                return true;
            }

            private static int GetTimeScienceLastWipe()
            {
                DateTime startTime = new DateTime(2019, 1, 1, 0, 0, 0);

                double realTime = DateTime.UtcNow.Subtract(startTime).TotalSeconds;
                double wipeTime = SaveRestore.SaveCreatedTime.Subtract(startTime).TotalSeconds;

                return Convert.ToInt32(realTime - wipeTime);
            }
        }

        private class EventController : FacepunchBehaviour
        {
            public EventConfig EventConfig;
            private readonly HashSet<NpcData> _npcData = new HashSet<NpcData>();
            private readonly HashSet<AutoTurret> _turrets = new HashSet<AutoTurret>();
            private readonly HashSet<SamSite> _samSites = new HashSet<SamSite>();
            private Coroutine _spawnCoroutine;
            private Coroutine _eventCoroutine;
            private int _eventTime;
            private int _aggressiveTime;
            private int _stopTime;
            private bool _isStopped;
            private bool _isEventLooted;

            private readonly HashSet<ulong> _lootedContainersUids = new HashSet<ulong>();
            private readonly HashSet<BaseEntity> _crates = new HashSet<BaseEntity>();
            private int _countOfUnlootedCrates;

            public int GetEventTime()
            {
                return _eventTime;
            }

            public bool IsFullySpawned()
            {
                return _spawnCoroutine == null;
            }

            public bool IsStopped()
            {
                return _isStopped;
            }

            public bool IsAggressive()
            {
                return _ins._config.BehaviorConfig.AggressiveTime < 0 || _aggressiveTime > 0 || (_ins._config.BehaviorConfig.IsStopConvoyAggressive && IsStopped());
            }


            public void AddEventCrate(BaseEntity entity)
            {
                _crates.Add(entity);
            }

            public void OnEventCrateLooted(BaseEntity baseEntity, ulong userId)
            {
                if (baseEntity.net == null)
                    return;

                if (!IsCrateLooted(baseEntity.net.ID.Value))
                {
                    if (_ins._config.SupportedPluginsConfig.EconomicsConfig.Crates.TryGetValue(baseEntity.PrefabName, out double cratePoint))
                        EconomyManager.AddBalance(userId, cratePoint);

                    _lootedContainersUids.Add(baseEntity.net.ID.Value);
                }

                EventPassingCheck();
            }

            public void UpdateCountOfUnlootedCrates()
            {
                _countOfUnlootedCrates = _crates.Where(x => x != null && x.IsExists() && x.net != null && !IsCrateLooted(x.net.ID.Value)).Count;
            }

            public int GetCountOfUnlootedCrates()
            {
                return _countOfUnlootedCrates;
            }
            
            public BaseEntity GetCrateByNetId(ulong netID)
            {
                return _crates.FirstOrDefault(x => x != null && x.net != null && x.net.ID.Value == netID);
            }

            public bool IsEventCrate(ulong netID)
            {
                return _crates.Any(x => x != null && x.net != null && x.net.ID.Value == netID);
            }

            public bool IsCrateLooted(ulong netID)
            {
                return _lootedContainersUids.Contains(netID);
            }

            public HashSet<ulong> GetEventCratesNetIDs()
            {
                HashSet<ulong> eventCrates = new HashSet<ulong>();

                foreach (BaseEntity crate in _crates)
                    if (crate != null && crate.net != null)
                        eventCrates.Add(crate.net.ID.Value);

                return eventCrates;
            }


            public HashSet<ulong> GetAliveTurretsNetIds()
            {
                HashSet<ulong> turretsIDs = new HashSet<ulong>();

                foreach (AutoTurret autoTurret in _turrets)
                    if (autoTurret.IsExists() && autoTurret.net != null)
                        turretsIDs.Add(autoTurret.net.ID.Value);

                return turretsIDs;
            }

            public bool IsEventTurret(ulong netID)
            {
                return _turrets.Any(x => x != null && x.net != null && x.net.ID.Value == netID);
            }

            public bool IsEventSamSite(ulong netID)
            {
                return _samSites.Any(x => x != null && x.net != null && x.net.ID.Value == netID);
            }

            public bool IsPlayerCanDealDamage(BasePlayer player, BaseCombatEntity eventEntity, bool shouldSendMessages)
            {
                if (_spawnCoroutine != null)
                    return false;

                Vector3 playerGroundPosition = new Vector3(player.transform.position.x, 0, player.transform.position.z);
                Vector3 entityGroundPosition = new Vector3(eventEntity.transform.position.x, 0, eventEntity.transform.position.z);
                float distance = Vector3.Distance(playerGroundPosition, entityGroundPosition);
                float maxDamageDistance = eventEntity is PatrolHelicopter && IsStopped() ? EventConfig.MaxHeliDamageDistance : EventConfig.MaxGroundDamageDistance;

                if (maxDamageDistance > 0 && distance > maxDamageDistance)
                {
                    if (shouldSendMessages)
                        NotifyManager.SendMessageToPlayer(player, "DamageDistance", _ins._config.Prefix);

                    return false;
                }

                if (PveModeManager.IsPveModeBlockInteractByCooldown(player))
                {
                    SwitchAggressive(true);
                    NotifyManager.SendMessageToPlayer(player, "PveMode_BlockAction", _ins._config.Prefix);
                    return false;
                }

                return true;
            }

            public bool IsPlayerCanLoot(BasePlayer player, bool shouldSendMessages)
            {
                if (IsLootBlockedByThisPlugin())
                {
                    NotifyManager.SendMessageToPlayer(player, "CantLoot", _ins._config.Prefix);
                    return false;
                }

                if (PveModeManager.IsPveModeBlockInteractByCooldown(player))
                {
                    if (shouldSendMessages)
                        NotifyManager.SendMessageToPlayer(player, "PveMode_BlockAction", _ins._config.Prefix);

                    return false;
                }

                if (PveModeManager.IsPveModeBlockNoOwnerLooting(player))
                {
                    if (shouldSendMessages)
                        NotifyManager.SendMessageToPlayer(player, "PveMode_YouAreNoOwner", _ins._config.Prefix);

                    return false;
                }

                return true;
            }

            private bool IsLootBlockedByThisPlugin()
            {
                if (_aggressiveTime <= 0 && _ins._config.BehaviorConfig.AggressiveTime > 0)
                    return true;

                if (_ins._config.LootConfig.BlockLootingByMove && !IsStopped())
                    return true;

                if (_ins._config.LootConfig.BlockLootingByBradleys && ConvoyPathVehicle.GetAllBradleyNetIds().Count > 0)
                    return true;

                if (_ins._config.LootConfig.BlockLootingByNpcs && NpcSpawnManager.GetEventNpcCount() > 0)
                    return true;

                if (_ins._config.LootConfig.BlockLootingByHeli && EventHeli.GetAliveHeliNetIds().Count > 0)
                    return true;

                return false;
            }

            public void Init(EventConfig eventConfig)
            {
                this.EventConfig = eventConfig;
                SpawnConvoy();
            }

            private void SpawnConvoy()
            {
                if (PathManager.CurrentPath == null)
                {
                    EventLauncher.StopEvent();
                    return;
                }

                _ins.Subscribes();
                _spawnCoroutine = ServerMgr.Instance.StartCoroutine(SpawnCoroutine());
            }

            private IEnumerator SpawnCoroutine()
            {
                ConvoyPathVehicle lastSpawnedVehicle = null;
                float lastVehicleSpawnTime = Time.realtimeSinceStartup;

                foreach (string vehiclePreset in EventConfig.VehiclesOrder)
                {
                    while (lastSpawnedVehicle != null && Vector3.Distance(lastSpawnedVehicle.transform.position, PathManager.CurrentPath.StartPathPoint.Position) < 10)
                    {
                        if (Time.realtimeSinceStartup - lastVehicleSpawnTime > 30)
                            OnSpawnFailed();

                        yield return CoroutineEx.waitForSeconds(1);
                    }

                    ConvoyPathVehicle convoyVehicle = SpawnConvoyVehicle(vehiclePreset, lastSpawnedVehicle);
                    if (convoyVehicle == null)
                    {
                        EventLauncher.StopEvent(true);
                        break;
                    }

                    yield return CoroutineEx.waitForSeconds(1f);

                    lastVehicleSpawnTime = Time.realtimeSinceStartup;
                    lastSpawnedVehicle = convoyVehicle;
                }

                yield return CoroutineEx.waitForSeconds(3f);

                if (EventConfig.HeliPreset != "" && EventConfig.IsHeli)
                {
                    HeliConfig heliConfig = _ins._config.HeliConfigs.FirstOrDefault(x => x.PresetName == EventConfig.HeliPreset);

                    if (heliConfig == null)
                        NotifyManager.PrintError(null, "PresetNotFound_Exeption", EventConfig.HeliPreset);
                    else
                        EventHeli.SpawnHeli(heliConfig);
                }

                yield return CoroutineEx.waitForSeconds(1);
                OnSpawnFinished();
            }

            public void OnSpawnFailed()
            {
                PathManager.GenerateNewPath();
                KillConvoy();
                _ins.Unsubscribes();

                if (_spawnCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_spawnCoroutine);

                SpawnConvoy();
            }

            private ConvoyPathVehicle SpawnConvoyVehicle(string presetName, ConvoyPathVehicle frontVehicle)
            {
                ConvoyPathVehicle convoyVehicle = null;
                VehicleConfig vehicleConfig = null;
                ModularCarConfig modularCarConfig = _ins._config.ModularCarConfigs.FirstOrDefault(x => x.PresetName == presetName);

                if (modularCarConfig != null)
                {
                    convoyVehicle = ModularCarVehicle.SpawnModularCar(modularCarConfig, frontVehicle);
                    vehicleConfig = modularCarConfig;
                }

                if (convoyVehicle == null)
                {
                    BradleyConfig bradleyConfig = _ins._config.BradleyConfigs.FirstOrDefault(x => x.PresetName == presetName);

                    if (bradleyConfig != null)
                    {
                        convoyVehicle = BradleyVehicle.SpawnBradley(bradleyConfig, frontVehicle);
                        vehicleConfig = bradleyConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    TravellingVendorConfig travellingVendorConfig = _ins._config.TravelingVendorConfigs.FirstOrDefault(x => x.PresetName == presetName);

                    if (travellingVendorConfig != null)
                    {
                        convoyVehicle = TravellingVendorVehicle.SpawnTravellingVendor(travellingVendorConfig, frontVehicle);
                        vehicleConfig = travellingVendorConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    SedanConfig sedanConfig = _ins._config.SedanConfigs.FirstOrDefault(x => x.PresetName == presetName);

                    if (sedanConfig != null)
                    {
                        convoyVehicle = SedanVehicle.SpawnSedan(sedanConfig, frontVehicle);
                        vehicleConfig = sedanConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    BikeConfig bikeConfig = _ins._config.BikeConfigs.FirstOrDefault(x => x.PresetName == presetName);

                    if (bikeConfig != null)
                    {
                        convoyVehicle = BikeVehicle.SpawnBike(bikeConfig, frontVehicle);
                        vehicleConfig = bikeConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    KaruzaCarConfig customCarConfig = _ins._config.KaruzaCarConfigs.FirstOrDefault(x => x.PresetName == presetName);

                    if (customCarConfig != null)
                    {
                        convoyVehicle = KaruzaCarVehicle.SpawnVehicle(customCarConfig, frontVehicle);
                        vehicleConfig = customCarConfig;
                    }
                }


                if (convoyVehicle == null)
                    return null;

                foreach (PresetLocationConfig presetLocationConfig in vehicleConfig.TurretLocations)
                    SpawnTurret(presetLocationConfig, convoyVehicle.baseEntity);

                foreach (PresetLocationConfig presetLocationConfig in vehicleConfig.SamSiteLocations)
                    SpawnSamSite(presetLocationConfig, convoyVehicle.baseEntity);

                foreach (PresetLocationConfig presetLocationConfig in vehicleConfig.CrateLocations)
                    SpawnCrate(presetLocationConfig, convoyVehicle.baseEntity);

                InitialSpawnVehicleNpc(vehicleConfig, convoyVehicle.baseEntity);

                return convoyVehicle;
            }

            private void SpawnTurret(PresetLocationConfig presetLocationConfig, BaseEntity parentEntity)
            {
                TurretConfig turretConfig = _ins._config.TurretConfigs.FirstOrDefault(x => x.PresetName == presetLocationConfig.PresetName);

                if (turretConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", presetLocationConfig.PresetName);
                    return;
                }

                AutoTurret autoTurret = BuildManager.SpawnChildEntity(parentEntity, "assets/prefabs/npc/autoturret/autoturret_deployed.prefab", presetLocationConfig, 0, isDecor: false) as AutoTurret;
                _turrets.Add(autoTurret);
                BuildManager.UpdateEntityMaxHealth(autoTurret, turretConfig.Hp);

                autoTurret.inventory.Insert(ItemManager.CreateByName(turretConfig.ShortNameWeapon));
                if (turretConfig.CountAmmo > 0)
                    autoTurret.inventory.Insert(ItemManager.CreateByName(turretConfig.ShortNameAmmo, turretConfig.CountAmmo));

                autoTurret.isLootable = false;
                autoTurret.dropFloats = false;
                autoTurret.dropsLoot = _ins._config.MainConfig.IsTurretDropWeapon;

                autoTurret.SetFlag(BaseEntity.Flags.Busy, true);
                autoTurret.SetFlag(BaseEntity.Flags.Locked, true);

                if (turretConfig.TargetLossRange != 0)
                    autoTurret.sightRange = turretConfig.TargetLossRange;
                
                TurretOptimizer.Attach(autoTurret, turretConfig.TargetLossRange);
            }

            private void SpawnSamSite(PresetLocationConfig presetLocationConfig, BaseEntity parentEntity)
            {
                SamSiteConfig samSiteConfig = _ins._config.SamsiteConfigs.FirstOrDefault(x => x.PresetName == presetLocationConfig.PresetName);

                if (samSiteConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", presetLocationConfig.PresetName);
                    return;
                }

                SamSite samSite = BuildManager.SpawnChildEntity(parentEntity, "assets/prefabs/npc/sam_site_turret/sam_site_turret_deployed.prefab", presetLocationConfig, 0, false) as SamSite;
                _samSites.Add(samSite);
                BuildManager.UpdateEntityMaxHealth(samSite, samSiteConfig.Hp);

                if (samSiteConfig.CountAmmo > 0)
                    samSite.inventory.Insert(ItemManager.CreateByName("ammo.rocket.sam", samSiteConfig.CountAmmo));

                samSite.isLootable = false;
                samSite.dropFloats = false;
                samSite.dropsLoot = false;

                samSite.inventory.SetLocked(true);
                samSite.SetFlag(BaseEntity.Flags.Locked, true);
                samSite.SetFlag(BaseEntity.Flags.Busy, true);
            }

            private void SpawnCrate(PresetLocationConfig presetLocationConfig, BaseEntity parentEntity)
            {
                CrateConfig crateConfig = _ins._config.CrateConfigs.FirstOrDefault(x => x.PresetName == presetLocationConfig.PresetName);
                if (crateConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", presetLocationConfig.PresetName);
                    return;
                }

                BaseEntity crateEntity = BuildManager.SpawnChildEntity(parentEntity, crateConfig.PrefabName, presetLocationConfig, crateConfig.Skin, false);
                if (crateEntity == null)
                    return;
                
                LootController.UpdateCrate(crateEntity, crateConfig);
                AddEventCrate(crateEntity);

                if (crateEntity is HackableLockedCrate hackableLockedCrate)
                {
                    if (hackableLockedCrate.mapMarkerInstance.IsExists())
                    {
                        hackableLockedCrate.mapMarkerInstance.Kill();
                        hackableLockedCrate.mapMarkerInstance = null;
                    }

                    hackableLockedCrate.Invoke(() => hackableLockedCrate.hackSeconds = HackableLockedCrate.requiredHackSeconds - crateConfig.HackTime, 1.1f);
                    hackableLockedCrate.InvokeRepeating(() => hackableLockedCrate.SendNetworkUpdate(), 1f, 1f);

                    return;
                }

                if (crateEntity is BaseCombatEntity baseCombatEntity)
                {
                    baseCombatEntity.baseProtection = _ins._protection;
                }
            }

            private void InitialSpawnVehicleNpc(VehicleConfig npcSpawnedVehicleConfig, BaseEntity parentEntity)
            {
                BaseVehicle baseVehicle = parentEntity as BaseVehicle;

                if (baseVehicle != null)
                {
                    int countOfNpc = 0;

                    foreach (MountPointInfo mountPointInfo in baseVehicle.allMountPoints)
                    {
                        NpcData npcData = new NpcData
                        {
                            NpcPresetName = npcSpawnedVehicleConfig.NpcPresetName,
                            ConvoyVehicle = parentEntity
                        };

                        if (mountPointInfo != null)
                        {
                            npcData.ScientistNpc = NpcSpawnManager.SpawnScientistNpc(npcSpawnedVehicleConfig.NpcPresetName, baseVehicle.transform.position, 1, true, mountPointInfo.isDriver);

                            if (npcData.ScientistNpc == null)
                                continue;

                            npcData.BaseMountable = mountPointInfo.mountable;
                            npcData.BaseMountable.AttemptMount(npcData.ScientistNpc, false);
                            npcData.IsDriver = mountPointInfo.isDriver;
                            npcData.IsDismount = true;
                            countOfNpc++;
                        }

                        _npcData.Add(npcData);

                        if (countOfNpc >= npcSpawnedVehicleConfig.NumberOfNpc)
                            break;
                    }
                }

                if (npcSpawnedVehicleConfig.AdditionalNpc.Count > 0)
                {
                    foreach (NpcPoseConfig npcPoseConfig in npcSpawnedVehicleConfig.AdditionalNpc)
                    {
                        if (!npcPoseConfig.IsEnable)
                            continue;

                        NpcData npcData = new NpcData
                        {
                            NpcPresetName = string.IsNullOrEmpty(npcPoseConfig.NpcPresetName) ? npcSpawnedVehicleConfig.NpcPresetName : npcPoseConfig.NpcPresetName,
                            ConvoyVehicle = parentEntity,
                            BaseMountable = MovableBaseMountable.CreateMovableBaseMountable(npcPoseConfig.SeatPrefab, parentEntity, npcPoseConfig.Position.ToVector3(), npcPoseConfig.Rotation.ToVector3())
                        };

                        npcData.ScientistNpc = NpcSpawnManager.SpawnScientistNpc(npcData.NpcPresetName, parentEntity.transform.position, 1, true);
                        npcData.BaseMountable.isMobile = true;
                        npcData.BaseMountable.ignoreVehicleParent = true;
                        npcData.IsDismount = npcPoseConfig.IsDismount;

                        if (npcData.ScientistNpc == null || npcData.BaseMountable == null)
                            continue;

                        npcData.BaseMountable.AttemptMount(npcData.ScientistNpc, false);

                        if (baseVehicle != null)
                        {
                            npcData.BaseMountable.dismountPositions = baseVehicle.dismountPositions;
                        }
                        else
                        {
                            CustomBradley customBradley = parentEntity as CustomBradley;
                            if (customBradley != null)
                                npcData.BaseMountable.dismountPositions = Array.Empty<Transform>();
                        }

                        _npcData.Add(npcData);
                    }
                }
            }

            private void OnSpawnFinished()
            {
                _spawnCoroutine = null;
                _eventTime = EventConfig.EventTime;
                int remainFixedScheduleWindowSeconds = EventLauncher.GetRemainFixedScheduleWindowSeconds();
                if (remainFixedScheduleWindowSeconds >= 0)
                    _eventTime = Math.Min(_eventTime, remainFixedScheduleWindowSeconds);

                EventMapMarker.CreateMarker();
                _eventCoroutine = ServerMgr.Instance.StartCoroutine(EventCoroutine());
                PathManager.OnSpawnFinish();
                SwitchAggressive(IsAggressive());
                UpdateCountOfUnlootedCrates();
                NotifyManager.SendMessageToAll("EventStart", _ins._config.Prefix, EventConfig.DisplayName, MapHelper.GridToString(MapHelper.PositionToGrid(PathManager.CurrentPath.StartPathPoint.Position)));
            }

            private IEnumerator EventCoroutine()
            {
                while (_eventTime > 0 || (!_isEventLooted && _ins._config.MainConfig.DontStopEventIfPlayerInZone && ZoneController.IsAnyPlayerInEventZone()))
                {
                    if (_ins._config.NotifyConfig.TimeNotifications.Contains(_eventTime) && !_isEventLooted)
                        NotifyManager.SendMessageToAll("RemainTime", _ins._config.Prefix, EventConfig.DisplayName, _eventTime);

                    if (_isStopped && !_isEventLooted)
                    {
                        _stopTime--;

                        if (_stopTime <= 0 && NpcSpawnManager.GetEventNpcCount() > 0)
                        {
                            SwitchMoving(true);
                        }
                    }

                    if (!IsStopped() && _aggressiveTime > 0)
                    {
                        _aggressiveTime--;

                        if (_aggressiveTime <= 0)
                            SwitchAggressive(false);
                    }

                    if (_eventTime % 30 == 0 && EventConfig.EventTime - _eventTime > 30)
                        EventPassingCheck();

                    if (!IsAggressive())
                        UpdateAllMountedNpcLookRotation();

                    if (_eventTime > 0)
                        _eventTime--;

                    yield return CoroutineEx.waitForSeconds(1);
                }

                EventLauncher.StopEvent();
            }

            public void EventPassingCheck()
            {
                if (_isEventLooted)
                    return;

                UpdateCountOfUnlootedCrates();
                int countOfUnlootedCrates = GetCountOfUnlootedCrates();

                if (countOfUnlootedCrates == 0)
                {
                    _isEventLooted = true;
                    SwitchMoving(false);
                    _eventTime = _ins._config.MainConfig.EndAfterLootTime;
                    int remainFixedScheduleWindowSeconds = EventLauncher.GetRemainFixedScheduleWindowSeconds();
                    if (remainFixedScheduleWindowSeconds >= 0)
                        _eventTime = Math.Min(_eventTime, remainFixedScheduleWindowSeconds);

                    NotifyManager.SendMessageToAll("Looted", _ins._config.Prefix, EventConfig.DisplayName);
                }
            }

            public void OnEventAttacked(BasePlayer player)
            {
                Invoke(() =>
                {
                    if (player != null && ((_aggressiveTime <= 0 && _ins._config.BehaviorConfig.AggressiveTime > 0) || !_isStopped))
                    {
                        Interface.CallHook($"OnConvoyAttacked", player, ConvoyPathVehicle.GetEventPosition());
                        NotifyManager.SendMessageToAll("ConvoyAttacked", _ins._config.Prefix, player.displayName);
                    }

                    SwitchAggressive(true);
                    SwitchMoving(false, player);
                }, 0);
            }

            public void SwitchAggressive(bool isAggressive)
            {
                if (isAggressive)
                    _aggressiveTime = _ins._config.BehaviorConfig.AggressiveTime;

                bool shouldEnableTurrets = isAggressive || _ins._config.BehaviorConfig.AggressiveTime <= 0;

                foreach (AutoTurret autoTurret in _turrets)
                {
                    if (!autoTurret.IsExists())
                        continue;

                    if (shouldEnableTurrets && autoTurret.IsPowered())
                        continue;

                    autoTurret.UpdateFromInput(shouldEnableTurrets ? 10 : 0, 0);
                }

                foreach (SamSite samSite in _samSites)
                    if (samSite.IsExists())
                        samSite.UpdateFromInput(shouldEnableTurrets ? 100 : 0, 0);
            }

            public void SwitchMoving(bool isMoving, BasePlayer attacker = null)
            {
                if (_spawnCoroutine != null)
                    return;

                if (isMoving)
                {
                    if (IsStopped())
                    {
                        MountAllNpc();
                        ZoneController.TryDeleteZone();
                        ConvoyPathVehicle.SwitchMoving(true);
                        _stopTime = 0;
                        _isStopped = false;
                        Interface.CallHook($"OnConvoyStartMoving", ConvoyPathVehicle.GetEventPosition());
                    }
                }
                else
                {
                    if (!IsStopped())
                    {
                        _stopTime = _ins._config.BehaviorConfig.StopTime;
                        RoamAllNpc();
                        ZoneController.CreateZone(attacker != null && _ins._config.SupportedPluginsConfig.PveMode.OwnerIsStopper && !PveModeManager.IsPlayerHaveCooldown(attacker.userID) ? attacker : null);
                        ConvoyPathVehicle.SwitchMoving(false);
                        _isStopped = true;
                        Interface.CallHook($"OnConvoyStopMoving", ConvoyPathVehicle.GetEventPosition());
                    }
                    else
                    {
                        _stopTime = _ins._config.BehaviorConfig.StopTime;
                        _isStopped = true;
                    }
                }
            }

            private void RoamAllNpc()
            {
                foreach (NpcData npcData in _npcData)
                {
                    if (!npcData.IsDismount || !npcData.ScientistNpc.IsExists())
                        continue;

                    Vector3 spawnPosition;

                    if (PositionDefiner.GetNavmeshInPoint(npcData.ScientistNpc.transform.position, 5f, out NavMeshHit navMeshHit))
                        spawnPosition = navMeshHit.position;
                    else
                        continue;

                    npcData.ScientistNpc.Kill();
                    npcData.ScientistNpc = NpcSpawnManager.SpawnScientistNpc(npcData.NpcPresetName, spawnPosition, npcData.ScientistNpc.healthFraction, false);
                    npcData.IsRoaming = true;
                }
            }

            private void MountAllNpc()
            {
                foreach (NpcData npcData in _npcData)
                {
                    if (npcData.IsDriver && !npcData.ScientistNpc.IsExists())
                    {
                        NpcData newDriver = _npcData.FirstOrDefault(x => x.ConvoyVehicle == npcData.ConvoyVehicle && x.ScientistNpc.IsExists() && !x.IsDriver);

                        if (newDriver == null)
                            newDriver = _npcData.FirstOrDefault(x => x.ScientistNpc.IsExists() && !x.IsDriver);

                        if (newDriver == null)
                            break;

                        npcData.ScientistNpc = newDriver.ScientistNpc;
                        npcData.NpcPresetName = newDriver.NpcPresetName;

                        newDriver.ScientistNpc = null;
                    }
                }

                foreach (NpcData npcData in _npcData)
                {
                    if (npcData.BaseMountable == null || (!npcData.IsDriver && (!npcData.ScientistNpc.IsExists() || !npcData.IsRoaming)) || !npcData.IsDismount)
                    {
                        if (npcData.ScientistNpc.IsExists())
                            npcData.ScientistNpc.Kill();

                        continue;
                    }

                    if (npcData.ScientistNpc.IsExists())
                    {
                        npcData.ScientistNpc.Kill();
                    }

                    npcData.ScientistNpc = NpcSpawnManager.SpawnScientistNpc(npcData.NpcPresetName, npcData.BaseMountable.transform.position, 1, true, npcData.IsDriver);
                    npcData.BaseMountable.AttemptMount(npcData.ScientistNpc, false);
                    npcData.IsRoaming = false;
                }
            }

            private void UpdateAllMountedNpcLookRotation()
            {
                foreach (NpcData npcData in _npcData)
                {
                    if (!npcData.IsDriver && npcData.ScientistNpc != null && npcData.ScientistNpc.isMounted)
                    {
                        npcData.ScientistNpc.OverrideViewAngles(npcData.BaseMountable.mountAnchor.transform.rotation.eulerAngles);
                        npcData.ScientistNpc.SendNetworkUpdate();
                    }
                }
            }

            public void DeleteController()
            {
                if (_eventCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_eventCoroutine);

                if (_spawnCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_spawnCoroutine);

                KillConvoy();
                GameObject.Destroy(this);
            }

            private void KillConvoy()
            {
                foreach (AutoTurret autoTurret in _turrets)
                {
                    if (autoTurret.IsExists())
                    {
                        AutoTurret.interferenceUpdateList.Remove(autoTurret);
                        autoTurret.Kill();
                    }
                }

                ConvoyPathVehicle.KillAllVehicles();
            }

            private class NpcData
            {
                public ScientistNPC ScientistNpc;
                public BaseEntity ConvoyVehicle;
                public BaseMountable BaseMountable;
                public string NpcPresetName;
                public bool IsDriver;
                public bool IsRoaming;
                public bool IsDismount;
            }
        }

        private class TravellingVendorVehicle : DirectControlVehicle
        {
            private TravellingVendor _travellingVendor;
            private TravellingVendorConfig _travellingVendorConfig;
            private const float Power = 2500;

            public static TravellingVendorVehicle SpawnTravellingVendor(TravellingVendorConfig travellingVendorConfig, ConvoyPathVehicle frontVehicle)
            {
                TravellingVendor travellingVendor = BuildManager.SpawnRegularEntity("assets/prefabs/npc/travelling vendor/travellingvendor.prefab", PathManager.CurrentPath.StartPathPoint.Position + Vector3.up, Quaternion.LookRotation(PathManager.CurrentPath.SpawnRotation)) as TravellingVendor;
                TravellingVendorVehicle convoyTravellingVendor = travellingVendor.gameObject.AddComponent<TravellingVendorVehicle>();
                convoyTravellingVendor.Init(travellingVendor, travellingVendorConfig, frontVehicle);
                return convoyTravellingVendor;
            }

            private void Init(TravellingVendor travellingVendor, TravellingVendorConfig travellingVendorConfig, ConvoyPathVehicle frontVehicle)
            {
                this._travellingVendor = travellingVendor;
                this._travellingVendorConfig = travellingVendorConfig;

                Rigidbody rigidbody = travellingVendor.GetComponentInChildren<Rigidbody>();
                UpdateTravellingVendor();
                EntityDecorator.DecorateEntity(travellingVendor, _ins._entityCustomizationData["van_default"]);
                Init(travellingVendor, GetWheelsData(), rigidbody, frontVehicle, Power);
                PostSpawnUpdate();
            }

            private HashSet<WheelData> GetWheelsData()
            {
                HashSet<WheelData> allWheelData = new HashSet<WheelData>();
                HashSet<VisualCarWheel> visualCarWheels = new HashSet<VisualCarWheel>
                {
                    _travellingVendor.GetPrivateFieldValue("wheelFL") as VisualCarWheel,
                    _travellingVendor.GetPrivateFieldValue("wheelFR") as VisualCarWheel,
                    _travellingVendor.GetPrivateFieldValue("wheelRL") as VisualCarWheel,
                    _travellingVendor.GetPrivateFieldValue("wheelRR") as VisualCarWheel
                };

                foreach (VisualCarWheel visualCarWheel in visualCarWheels)
                {
                    WheelData wheelData = new WheelData(visualCarWheel.wheelCollider, visualCarWheel.steerWheel);
                    allWheelData.Add(wheelData);
                }

                return allWheelData;
            }

            private void UpdateTravellingVendor()
            {
                _travellingVendor.SetFlag(BaseEntity.Flags.Busy, true);
                _travellingVendor.DoAI = false;
                _travellingVendor.SetPrivateFieldValue("currentPath", new List<Vector3> { Vector3.zero });

                foreach (BaseEntity entity in _travellingVendor.children.ToHashSet())
                    if (entity.IsExists())
                        entity.Kill();

                if (_travellingVendorConfig.DeleteMapMarker)
                {
                    MapMarker mapMarker = _travellingVendor.GetPrivateFieldValue("mapMarkerInstance") as MapMarker;

                    if (mapMarker.IsExists())
                        mapMarker.Kill();
                }

                BuildManager.DestroyEntityComponents<TriggerBase>(_travellingVendor);
            }

            private void PostSpawnUpdate()
            {
                HashSet<BaseEntity> doors = _travellingVendor.children.Where(x => x is Door);

                foreach (BaseEntity doorEntity in doors)
                {
                    Door door = doorEntity as Door;

                    if (door == null)
                        continue;

                    BuildManager.UpdateEntityMaxHealth(door, _travellingVendorConfig.DoorHealth);
                    door.skinID = _travellingVendorConfig.DoorSkin;

                    if (_travellingVendorConfig.IsLocked)
                    {
                        CodeLock codeLock = GameManager.server.CreateEntity("assets/prefabs/locks/keypad/lock.code.prefab") as CodeLock;
                        codeLock.SetParent(door, door.GetSlotAnchorName(BaseEntity.Slot.Lock));
                        codeLock.Spawn();
                        codeLock.code = Random.Range(0, 9999).ToString();
                        codeLock.hasCode = true;
                        door.SetSlot(BaseEntity.Slot.Lock, codeLock);
                        codeLock.SetFlag(BaseEntity.Flags.Locked, true);
                    }
                }
            }

            protected override void StopMoving()
            {
                _travellingVendor.SetFlag(BaseEntity.Flags.Reserved2, true);
                _travellingVendor.SetFlag(BaseEntity.Flags.Reserved4, true);
                base.StopMoving();
            }

            protected override void StartMoving()
            {
                _travellingVendor.SetFlag(BaseEntity.Flags.Reserved2, false);
                _travellingVendor.SetFlag(BaseEntity.Flags.Reserved4, false);
                base.StartMoving();
            }
        }

        private class SedanVehicle : DirectControlVehicle
        {
            private BasicCar _basicCar;
            private const float Power = 1500;

            public static SedanVehicle SpawnSedan(SedanConfig sedanConfig, ConvoyPathVehicle frontVehicle)
            {
                BasicCar basicCar = BuildManager.SpawnRegularEntity("assets/content/vehicles/sedan_a/sedantest.entity.prefab", PathManager.CurrentPath.StartPathPoint.Position + Vector3.up, Quaternion.LookRotation(PathManager.CurrentPath.SpawnRotation)) as BasicCar;
                SedanVehicle sedanVehicle = basicCar.gameObject.AddComponent<SedanVehicle>();
                sedanVehicle.Init(basicCar, sedanConfig, frontVehicle);
                return sedanVehicle;
            }

            private void Init(BasicCar basicCar, SedanConfig sedanConfig, ConvoyPathVehicle frontVehicle)
            {
                this._basicCar = basicCar;
                base.Init(basicCar, GetWheelsData(), basicCar.rigidBody, frontVehicle, Power);
                BaseMountable.AllMountables.Remove(basicCar);
                BuildManager.UpdateEntityMaxHealth(basicCar, sedanConfig.Hp);

                if (sedanConfig.CrateLocations.Count > 0)
                    EntityDecorator.DecorateEntity(basicCar, _ins._entityCustomizationData["sedan_default"]);
            }

            private HashSet<WheelData> GetWheelsData()
            {
                HashSet<WheelData> wheelsData = new HashSet<WheelData>();

                foreach (BasicCar.VehicleWheel vehicleWheel in _basicCar.wheels)
                {
                    WheelData wheelData = new WheelData(vehicleWheel.wheelCollider, vehicleWheel.steerWheel);
                    wheelsData.Add(wheelData);
                }

                return wheelsData;
            }

            protected override void UpdateMoving()
            {
                if (_ins._eventController.IsStopped())
                    return;

                float turnFraction = GetTurnFraction(out bool _);
                _basicCar.SetFlag(BaseEntity.Flags.Reserved4, turnFraction < -5f);
                _basicCar.SetFlag(BaseEntity.Flags.Reserved5, turnFraction > 5f);
                _basicCar.SetFlag(BaseEntity.Flags.Reserved1, !_ins._eventController.IsStopped());
                _basicCar.SetFlag(BaseEntity.Flags.Reserved2, !_ins._eventController.IsStopped() && TOD_Sky.Instance.IsNight);

                base.UpdateMoving();
            }
        }

        private class DirectControlVehicle : ConvoyPathVehicle
        {
            private HashSet<WheelData> _wheelsData;
            private float _basePower;

            protected void Init(BaseEntity entity, HashSet<WheelData> wheelsData, Rigidbody rigidbody, ConvoyPathVehicle frontVehicle, float basePower)
            {
                this._wheelsData = wheelsData;
                this._basePower = basePower;
                base.Init(entity, rigidbody, frontVehicle);
            }

            protected override void UpdateMoving()
            {
                if (_ins._eventController.IsStopped())
                    return;

                float speedFraction = GetSpeedFraction();
                float turnFraction = GetTurnFraction(out bool tooHardTurn);

                bool shouldBrake = isStopped || speedFraction < 0;
                Brake(shouldBrake);

                if (tooHardTurn)
                {
                    if (!Rigidbody.isKinematic)
                        Rigidbody.velocity = Vector3.zero;

                    baseEntity.transform.Rotate(Vector3.up, 180);
                    return;
                }

                foreach (WheelData wheelData in _wheelsData)
                {
                    wheelData.WheelCollider.motorTorque = !shouldBrake && wheelData.WheelCollider.isGrounded ? _basePower : 0;

                    if (wheelData.IsSteering)
                        wheelData.WheelCollider.steerAngle = turnFraction;
                }

                baseEntity.SendNetworkUpdate();
            }

            private void Brake(bool isEnable)
            {
                foreach (WheelData wheelData in _wheelsData)
                    wheelData.WheelCollider.brakeTorque = isEnable ? 1000f : 0;
            }

            protected override void StopMoving()
            {
                isStopped = true;
                Brake(true);
                base.StopMoving();
            }

            protected override void StartMoving()
            {
                isStopped = false;
                base.StartMoving();
            }
        }

        private class WheelData
        {
            public readonly WheelCollider WheelCollider;
            public readonly bool IsSteering;

            public WheelData(WheelCollider wheelCollider, bool isSteering)
            {
                this.WheelCollider = wheelCollider;
                this.IsSteering = isSteering;
            }
        }


        private class BradleyVehicle : ConvoyPathVehicle
        {
            public BradleyConfig BradleyConfig;
            public CustomBradley bradley;

            public static BradleyVehicle SpawnBradley(BradleyConfig bradleyConfig, ConvoyPathVehicle frontVehicle)
            {
                CustomBradley customBradley = CustomBradley.CreateCustomBradley(PathManager.CurrentPath.StartPathPoint.Position + Vector3.up, Quaternion.LookRotation(PathManager.CurrentPath.SpawnRotation), bradleyConfig);
                BradleyVehicle convoyBradley = customBradley.gameObject.AddComponent<BradleyVehicle>();
                EntityPresetInfo.Attach(customBradley, bradleyConfig.PresetName);
                convoyBradley.Init(customBradley, bradleyConfig, frontVehicle);
                return convoyBradley;
            }

            private void Init(CustomBradley customBradley, BradleyConfig bradleyConfig, ConvoyPathVehicle frontVehicle)
            {
                bradley = customBradley;
                BradleyConfig = bradleyConfig;
                UpdateBradley();
                base.Init(customBradley, customBradley.myRigidBody, frontVehicle);
            }

            private void UpdateBradley()
            {
                BuildManager.UpdateEntityMaxHealth(bradley, BradleyConfig.Hp);
                bradley.maxCratesToSpawn = BradleyConfig.CountCrates;
                bradley.viewDistance = BradleyConfig.ViewDistance;
                bradley.searchRange = BradleyConfig.SearchDistance;
                bradley.coaxAimCone *= BradleyConfig.CoaxAimCone;
                bradley.coaxFireRate *= BradleyConfig.CoaxFireRate;
                bradley.coaxBurstLength = BradleyConfig.CoaxBurstLength;
                bradley.nextFireTime = BradleyConfig.NextFireTime;
                bradley.topTurretFireRate = BradleyConfig.TopTurretFireRate;
                bradley.finalDestination = Vector3.zero;
                bradley.moveForceMax = 4000f;
                bradley.myRigidBody.maxAngularVelocity = 2.5f;
            }

            protected override void UpdateMoving()
            {
                if (_ins._eventController.IsStopped())
                    return;

                float speedFraction = GetSpeedFraction();
                float turnFraction = GetTurnFraction(out bool tooHardTurn);

                if (isStopped)
                {
                    if (speedFraction > 0)
                        StartMoving();
                    else
                        return;
                }

                if (speedFraction < 0)
                {
                    StopMoving();
                    return;
                }

                if (tooHardTurn)
                {
                    if (!Rigidbody.isKinematic)
                        Rigidbody.velocity = Vector3.zero;

                    baseEntity.transform.Rotate(Vector3.up, 180);
                    return;
                }

                if (Math.Abs(turnFraction) > 15)
                {
                    bradley.throttle = 0.2f;
                    bradley.turning = turnFraction > 0 ? 1 : -1;
                }
                else if (Math.Abs(turnFraction) > 5)
                {
                    bradley.throttle = speedFraction;
                    bradley.turning = turnFraction / 20;
                }
                else
                {
                    bradley.throttle = speedFraction;
                    bradley.turning = 0;
                }

                bradley.DoPhysicsMove();
            }

            protected override void StopMoving()
            {
                bradley.SetMotorTorque(0, true, 0);
                bradley.SetMotorTorque(0, false, 0);
                bradley.ApplyBrakes(1f);
                base.StopMoving();
            }
        }

        private class CustomBradley : BradleyAPC
        {
            public BradleyConfig BradleyConfig;

            public static CustomBradley CreateCustomBradley(Vector3 position, Quaternion rotation, BradleyConfig bradleyConfig)
            {
                BradleyAPC bradley = GameManager.server.CreateEntity("assets/prefabs/npc/m2bradley/bradleyapc.prefab", position, rotation) as BradleyAPC;
                bradley.skinID = 755446;
                bradley.enableSaving = false;
                bradley.ScientistSpawns.Clear();
                CustomBradley customBradley = bradley.gameObject.AddComponent<CustomBradley>();
                BuildManager.CopySerializableFields(bradley, customBradley);
                bradley.StopAllCoroutines();
                DestroyImmediate(bradley, true);

                customBradley.BradleyConfig = bradleyConfig;
                customBradley.Spawn();

                TriggerHurtNotChild[] triggerHurts = customBradley.GetComponentsInChildren<TriggerHurtNotChild>();

                foreach (TriggerHurtNotChild triggerHurt in triggerHurts)
                    triggerHurt.enabled = false;

                return customBradley;
            }

            private new void FixedUpdate()
            {
                SetFlag(Flags.Reserved5, TOD_Sky.Instance.IsNight);

                if (_ins._eventController.IsAggressive())
                {
                    UpdateTarget();
                    DoWeapons();
                }

                DoHealing();
                DoWeaponAiming();
                SendNetworkUpdate();

                if (mainGunTarget == null)
                    desiredAimVector = transform.forward;
            }

            private void UpdateTarget()
            {
                if (targetList.Count > 0)
                {
                    TargetInfo targetInfo = targetList[0];

                    if (targetInfo == null)
                    {
                        mainGunTarget = null;
                        return;
                    }

                    BasePlayer player = targetInfo.entity as BasePlayer;

                    if (player.IsRealPlayer() && targetInfo.IsVisible())
                        mainGunTarget = targetList[0].entity as BaseCombatEntity;
                    else
                        mainGunTarget = null;
                }
                else
                {
                    mainGunTarget = null;
                }
            }
        }


        private class ModularCarVehicle : MountableVehicle
        {
            private const float BasePower = 800;
            private ModularCar _modularCar;
            public ModularCarConfig ModularCarConfig;
            private readonly HashSet<VehicleModuleEngine> _engines = new HashSet<VehicleModuleEngine>();

            public static ModularCarVehicle SpawnModularCar(ModularCarConfig modularCarConfig, ConvoyPathVehicle frontVehicle)
            {
                ModularCar modularCar = ModularCarManager.SpawnModularCar(PathManager.CurrentPath.StartPathPoint.Position, Quaternion.LookRotation(PathManager.CurrentPath.SpawnRotation), modularCarConfig.Modules);
                ModularCarVehicle modularCarVehicle = modularCar.gameObject.AddComponent<ModularCarVehicle>();
                modularCarVehicle.Init(modularCar, modularCarConfig, frontVehicle);
                return modularCarVehicle;
            }

            private void Init(ModularCar modularCar, ModularCarConfig modularCarConfig, ConvoyPathVehicle frontVehicle)
            {
                base.Init(modularCar, modularCar.engineController, modularCar.carSettings, frontVehicle);
                this._modularCar = modularCar;
                this.ModularCarConfig = modularCarConfig;
                modularCar.Invoke(DelayUpdate, 1f);
            }

            private void DelayUpdate()
            {
                GetEngines();
                UpdatePower();
            }

            private void GetEngines()
            {
                foreach (BaseVehicleModule module in _modularCar.AttachedModuleEntities)
                {
                    VehicleModuleEngine vehicleModuleEngine = module as VehicleModuleEngine;

                    if (vehicleModuleEngine != null)
                        _engines.Add(vehicleModuleEngine);
                }
            }

            private void UpdatePower()
            {
                if (_engines.Count == 0)
                    return;

                float power = BasePower / _engines.Count;

                foreach (VehicleModuleEngine vehicleModuleEngine in _engines)
                    vehicleModuleEngine.engine.engineKW = (int)(power);
            }
        }

        private class BikeVehicle : MountableVehicle
        {
            public static BikeVehicle SpawnBike(BikeConfig bikeConfig, ConvoyPathVehicle frontVehicle)
            {
                Bike bike = BuildManager.SpawnRegularEntity(bikeConfig.PrefabName, PathManager.CurrentPath.StartPathPoint.Position + Vector3.up, Quaternion.LookRotation(PathManager.CurrentPath.SpawnRotation)) as Bike;
                BikeVehicle bikeVehicle = bike.gameObject.AddComponent<BikeVehicle>();
                bikeVehicle.Init(bike, bikeConfig, frontVehicle);
                return bikeVehicle;
            }

            private void Init(Bike bike, BikeConfig bikeConfig, ConvoyPathVehicle frontVehicle)
            {
                ModularCarManager.UpdateFuelSystem(bike);
                BuildManager.UpdateEntityMaxHealth(bike, bikeConfig.Hp);
                CarSettings carSettings = bike.GetPrivateFieldValue("carSettings") as CarSettings;
                base.Init(bike, bike.engineController, carSettings, frontVehicle);

                bike.SetPrivateFieldValue("leftMaxLean", bike.ShortPrefabName.Contains("sidecar") ? 0 : 10);
                bike.SetPrivateFieldValue("rightMaxLean", bike.ShortPrefabName.Contains("sidecar") ? 0 : 10);
            }
        }

        private class KaruzaCarVehicle : MountableVehicle
        {
            public static KaruzaCarVehicle SpawnVehicle(KaruzaCarConfig karuzaCarConfig, ConvoyPathVehicle frontVehicle)
            {
                GroundVehicle carVehicle = BuildManager.SpawnRegularEntity(karuzaCarConfig.PrefabName, PathManager.CurrentPath.StartPathPoint.Position + Vector3.up, Quaternion.LookRotation(PathManager.CurrentPath.SpawnRotation)) as GroundVehicle;
                KaruzaCarVehicle karuzaCarVehicle = carVehicle.gameObject.AddComponent<KaruzaCarVehicle>();
                karuzaCarVehicle.Init(carVehicle, karuzaCarConfig, frontVehicle);
                return karuzaCarVehicle;
            }

            private void Init(GroundVehicle carVehicle, KaruzaCarConfig karuzaCarConfig, ConvoyPathVehicle frontVehicle)
            {
                CarSettings carSettings = carVehicle.GetPrivateFieldValue("carSettings") as CarSettings;
                base.Init(carVehicle, carVehicle.engineController, carSettings, frontVehicle);
            }
        }

        private abstract class MountableVehicle : ConvoyPathVehicle
        {
            private BaseVehicle _baseVehicle;
            private CarSettings _carSettings;
            private VehicleEngineController<GroundVehicle> _vehicleEngineController;
            private BasePlayer _driver;

            protected void Init(BaseVehicle baseVehicle, VehicleEngineController<GroundVehicle> vehicleEngineController, CarSettings carSettings, ConvoyPathVehicle frontVehicle)
            {
                base.Init(baseVehicle, baseVehicle.rigidBody, frontVehicle);
                this._baseVehicle = baseVehicle;
                this._vehicleEngineController = vehicleEngineController;
                this._carSettings = carSettings;
                UpdateCarSettings();
            }

            private void UpdateCarSettings()
            {
                _carSettings.canSleep = false;
                _carSettings.rollingResistance = 1f;
                _carSettings.antiRoll = 1;
                _carSettings.maxSteerAngle = 79;
                _carSettings.steerReturnLerpSpeed = float.MaxValue;
                _carSettings.steerMinLerpSpeed = float.MaxValue;
                _carSettings.steerMaxLerpSpeed = float.MaxValue;
                _carSettings.driveForceToMaxSlip = 100000;
                _carSettings.maxDriveSlip = 0f;
            }

            protected override void StartMoving()
            {
                _driver = _baseVehicle.GetDriver();

                if (_driver != null)
                    _vehicleEngineController.TryStartEngine(_driver);

                Rigidbody.isKinematic = false;
                isStopped = false;
                base.StartMoving();
            }

            protected override void StopMoving()
            {
                Rigidbody.isKinematic = true;
                isStopped = true;
                _vehicleEngineController.StopEngine();
                base.StopMoving();
            }

            protected override void UpdateMoving()
            {
                if (_ins._eventController.IsStopped())
                    return;

                if (_driver == null)
                {
                    if (!_ins._eventController.IsFullySpawned())
                        StartMoving();
                    else
                        _ins._eventController.SwitchMoving(false);
                    return;
                }

                float speedFraction = GetSpeedFraction();
                float turnFraction = GetTurnFraction(out bool tooHardTurn);
                InputState inputState = new InputState();

                if (isStopped)
                {
                    if (speedFraction > 0)
                        StartMoving();
                    else
                        return;
                }

                if (speedFraction > 0)
                {
                    inputState.current.buttons |= (int)BUTTON.FORWARD;
                }
                else if (speedFraction < 0)
                {
                    if (_baseVehicle.GetSpeed() > 1)
                        inputState.current.buttons |= (int)BUTTON.BACKWARD;
                    else
                    {
                        StopMoving();
                        return;
                    }
                }

                if (tooHardTurn)
                {
                    if (!Rigidbody.isKinematic)
                        Rigidbody.velocity = Vector3.zero;

                    baseEntity.transform.Rotate(Vector3.up, 180);
                    return;
                }

                if (Math.Abs(turnFraction) > 5)
                {
                    if (turnFraction < 0)
                        inputState.current.buttons |= (int)BUTTON.LEFT;
                    else if (turnFraction > 0)
                        inputState.current.buttons |= (int)BUTTON.RIGHT;
                }

                _carSettings.maxSteerAngle = Mathf.Lerp(0, 89, (Math.Abs(turnFraction) + 5) / 90);
                _baseVehicle.PlayerServerInput(inputState, _driver);
            }
        }


        private class TurretOptimizer : FacepunchBehaviour
        {
            private AutoTurret _autoTurret;
            private float _targetRadius;
            
            public static void Attach(AutoTurret autoTurret, float targetRadius)
            {
                TurretOptimizer turretOptimizer = autoTurret.gameObject.AddComponent<TurretOptimizer>();
                turretOptimizer.Init(autoTurret, targetRadius);
            }

            private void Init(AutoTurret autoTurret, float targetRadius)
            {
                _autoTurret = autoTurret;
                _targetRadius = targetRadius;
                
                SphereCollider sphereCollider = autoTurret.targetTrigger.GetComponent<SphereCollider>();
                sphereCollider.enabled = false;
                autoTurret.InvokeRepeating(ScanTargets, 3f, 1f);
            }
            
            private void ScanTargets()
            {
                if (_autoTurret.target != null && _autoTurret.target is not BasePlayer)
                    _autoTurret.SetTarget(null);
                
                if (_autoTurret.targetTrigger.entityContents == null)
                    _autoTurret.targetTrigger.entityContents = new HashSet<BaseEntity>();
                else
                    _autoTurret.targetTrigger.entityContents.Clear();

                if (!_ins._eventController.IsAggressive())
                    return;

                int count = BaseEntity.Query.Server.GetPlayersInSphereFast(transform.position, _targetRadius, AIBrainSenses.playerQueryResults, IsPlayerCanBeTargeted);

                if (count == 0)
                    return;

                _autoTurret.authDirty = true;

                for (int i = 0; i < count; i++)
                {
                    BasePlayer player = AIBrainSenses.playerQueryResults[i];

                    if (Interface.CallHook("OnEntityEnter", _autoTurret.targetTrigger, player) != null)
                        continue;

                    if (player.IsSleeping() || (player.InSafeZone() && !player.IsHostile()))
                        continue;

                    _autoTurret.targetTrigger.entityContents.Add(player);
                }
            }
            
            private bool IsPlayerCanBeTargeted(BasePlayer player)
            {
                if (!player.IsRealPlayer())
                    return false;

                if (player.IsDead() || player.IsSleeping() || player.IsWounded())
                    return false;

                if (player.InSafeZone() || player._limitedNetworking)
                    return false;

                return true;
            }
        }
        
        private abstract class ConvoyPathVehicle : FacepunchBehaviour
        {
            private static readonly List<ConvoyPathVehicle> VehicleOrder = new List<ConvoyPathVehicle>();
            private static bool _justRotated;
            private static float _lastEnablePointCheckTime;

            public BaseEntity baseEntity;
            protected Rigidbody Rigidbody;
            private ConvoyPathVehicle _frontVehicle;
            private ConvoyPathVehicle _backVehicle;
            public CollisionDisabler collisionDisabler;
            private float _vehicleSize;
            private PathPoint _nextPathPoint;
            private PathPoint _previousPathPoint;
            public bool isStopped;
            private readonly HashSet<BaseEntity> _barricades = new HashSet<BaseEntity>();

            public static ConvoyPathVehicle GetVehicleByNetId(ulong netID)
            {
                return VehicleOrder.FirstOrDefault(x => x != null && x.baseEntity != null && x.baseEntity.net != null && x.baseEntity.net.ID.Value == netID);
            }

            public static ConvoyPathVehicle GetVehicleByChildNetId(ulong netID)
            {
                return VehicleOrder.FirstOrDefault(x => x != null && x.baseEntity != null && x.baseEntity.children.Any(y => y != null && y.net != null && y.net.ID.Value == netID));
            }

            public static ConvoyPathVehicle GetClosestVehicle<TYpe>(Vector3 position)
            {
                ConvoyPathVehicle result = null;
                float minDistance = float.MaxValue;

                foreach (ConvoyPathVehicle convoyPathVehicle in VehicleOrder)
                {
                    if (convoyPathVehicle != null && convoyPathVehicle is TYpe)
                    {
                        float distance = Vector3.Distance(convoyPathVehicle.transform.position, position);

                        if (distance < minDistance)
                        {
                            result = convoyPathVehicle;
                            minDistance = distance;
                        }
                    }
                }

                return result;
            }

            public static HashSet<ulong> GetAllBradleyNetIds()
            {
                HashSet<ulong> bradleyIds = new HashSet<ulong>();

                foreach (ConvoyPathVehicle convoyVehicle in VehicleOrder)
                {
                    BradleyVehicle convoyBradley = convoyVehicle as BradleyVehicle;

                    if (convoyBradley != null && convoyBradley.baseEntity.net != null)
                        bradleyIds.Add(convoyBradley.baseEntity.net.ID.Value);
                }

                return bradleyIds;
            }

            public static Vector3 GetEventPosition()
            {
                int counter = 0;
                Vector3 resultPositon = Vector3.zero;

                foreach (ConvoyPathVehicle convoyVehicle in VehicleOrder)
                {
                    if (convoyVehicle != null)
                    {
                        resultPositon += convoyVehicle.transform.position;
                        counter++;
                    }
                }

                if (counter == 0)
                    return Vector3.zero;

                return resultPositon / counter;
            }

            public static void SwitchMoving(bool isMoving)
            {
                if (isMoving)
                    UpdateVehiclesOrder();

                foreach (ConvoyPathVehicle convoyVehicle in VehicleOrder)
                {
                    if (convoyVehicle != null)
                    {
                        if (isMoving)
                            convoyVehicle.StartMoving();
                        else
                            convoyVehicle.StopMoving();
                    }
                }
            }

            protected void Init(BaseEntity entity, Rigidbody rigidbody, ConvoyPathVehicle frontVehicle)
            {
                baseEntity = entity;
                Rigidbody = rigidbody;
                _frontVehicle = frontVehicle;

                VehicleOrder.Add(this);
                DetermineVehicleSize();
                UpdateVehiclesOrder();
                _nextPathPoint = PathManager.CurrentPath.StartPathPoint;
                Invoke(() => collisionDisabler = CollisionDisabler.AttachCollisionDisabler(entity), 1f);
            }

            private static void RotateConvoy()
            {
                _justRotated = true;
                VehicleOrder.Reverse();

                foreach (ConvoyPathVehicle convoyVehicle in VehicleOrder)
                    convoyVehicle.Rotate();

                UpdateVehiclesOrder();

                foreach (ConvoyPathVehicle convoyVehicle in VehicleOrder)
                    convoyVehicle.SetNextTargetPoint();
            }

            private void Rotate()
            {
                int previousPointIndex = PathManager.CurrentPath.Points.IndexOf(_nextPathPoint);
                _nextPathPoint = _previousPathPoint;

                if (!Rigidbody.isKinematic)
                    Rigidbody.velocity = Vector3.zero;

                _previousPathPoint = previousPointIndex >= 0 ? PathManager.CurrentPath.Points[previousPointIndex] : null;
                baseEntity.transform.Rotate(Vector3.up, 180);
            }

            private void DetermineVehicleSize()
            {
                if (baseEntity is BradleyAPC)
                    _vehicleSize = 10;
                else if (baseEntity is TravellingVendor)
                    _vehicleSize = 8.5f;
                else if (baseEntity is Bike)
                    _vehicleSize = 3f;
                else
                    _vehicleSize = 8.5f;
            }

            private static void UpdateVehiclesOrder()
            {
                for (int i = 0; i < VehicleOrder.Count; i++)
                {
                    ConvoyPathVehicle convoyVehicle = VehicleOrder[i];

                    if (convoyVehicle == null)
                        VehicleOrder.Remove(convoyVehicle);
                    else
                        convoyVehicle.DefineFollowEntity();
                }
            }

            private void DefineFollowEntity()
            {
                _frontVehicle = null;
                _backVehicle = null;
                int frontVehicleIndex = VehicleOrder.IndexOf(this) - 1;

                if (frontVehicleIndex < 0)
                    return;

                ConvoyPathVehicle newFrontVehicle = VehicleOrder[frontVehicleIndex];
                _frontVehicle = newFrontVehicle;
                _frontVehicle._backVehicle = this;
            }

            private void FixedUpdate()
            {
                if (_ins._eventController.IsStopped())
                    return;

                UpdatePath();

                if (_nextPathPoint != null)
                    UpdateMoving();
            }

            private void UpdatePath()
            {
                if (_nextPathPoint == null)
                {
                    if (_frontVehicle == null)
                        RotateConvoy();

                    return;
                }

                Vector2 groundVehiclePosition = new Vector2(transform.position.x, transform.position.z);
                Vector2 targetPointPosition = new Vector2(_nextPathPoint.Position.x, _nextPathPoint.Position.z);

                if (Vector2.Distance(groundVehiclePosition, targetPointPosition) < 6f)
                    SetNextTargetPoint();
            }

            private void SetNextTargetPoint()
            {
                int frontEntityRoadIndex = -1;

                if (_frontVehicle != null && _frontVehicle._nextPathPoint != null)
                    frontEntityRoadIndex = _frontVehicle._nextPathPoint.RoadIndex;

                PathPoint newNextPathPoint = null;

                if (frontEntityRoadIndex >= 0)
                    newNextPathPoint = _nextPathPoint.ConnectedPoints.FirstOrDefault(x => (_previousPathPoint == null || x != _previousPathPoint) && !x.Disabled && x.RoadIndex == frontEntityRoadIndex);

                if (newNextPathPoint == null && _nextPathPoint != null)
                {
                    List<PathPoint> pathPoints = Facepunch.Pool.Get<List<PathPoint>>();

                    foreach (PathPoint pathPoint in _nextPathPoint.ConnectedPoints)
                        if ((_previousPathPoint == null || pathPoint != _previousPathPoint) && !pathPoint.Disabled && pathPoint.ConnectedPoints.Count > 1)
                            pathPoints.Add(pathPoint);
                    pathPoints.Shuffle();

                    if (pathPoints.Count > 0)
                        newNextPathPoint = pathPoints.Max(x => Time.realtimeSinceStartup - x.LastVisitTime);

                    Facepunch.Pool.FreeUnmanaged(ref pathPoints);

                    if (newNextPathPoint != null)
                        newNextPathPoint.LastVisitTime = Time.realtimeSinceStartup;
                }

                if (_frontVehicle == null)
                {
                    foreach (PathPoint point in _nextPathPoint.ConnectedPoints)
                        if (!point.Disabled && (newNextPathPoint == null || point.Position != newNextPathPoint.Position) && (_previousPathPoint == null || point.Position != _previousPathPoint.Position))
                            point.Disabled = true;

                    if (newNextPathPoint != null)
                        foreach (PathPoint point in newNextPathPoint.ConnectedPoints)
                            point.Disabled = false;
                }

                _previousPathPoint = _nextPathPoint;
                _nextPathPoint = newNextPathPoint;

                if (ShouldEnableAllPoints())
                {
                    _justRotated = false;

                    foreach (PathPoint point in PathManager.CurrentPath.Points)
                        point.Disabled = false;
                }
            }

            private static bool ShouldEnableAllPoints()
            {
                if (!_justRotated)
                    return false;

                if (Time.realtimeSinceStartup - _lastEnablePointCheckTime < 2)
                    return false;

                _lastEnablePointCheckTime = Time.realtimeSinceStartup;

                if (!_ins._eventController.IsFullySpawned())
                    return false;

                if (VehicleOrder.Any(x => x == null))
                    return false;

                if (VehicleOrder.Any(x => x._nextPathPoint == null || x._previousPathPoint == null))
                    return false;

                if (VehicleOrder.Any(x => x._frontVehicle != null && x._nextPathPoint.RoadIndex != x._frontVehicle._nextPathPoint.RoadIndex))
                    return false;

                return true;
            }

            protected abstract void UpdateMoving();

            protected virtual void StartMoving()
            {
                foreach (BaseEntity entity in _barricades)
                    if (entity.IsExists())
                        entity.Kill();

                _barricades.Clear();
            }

            protected virtual void StopMoving()
            {
                Rigidbody.maxLinearVelocity = 0;

                HashSet<Vector3> barricadesLocalPositions = new HashSet<Vector3>();

                if (this is BradleyVehicle)
                {
                    barricadesLocalPositions = new HashSet<Vector3>
                    {
                        new Vector3(0, 0, 3.2f),
                        new Vector3(0, 0, 2.2f),
                        new Vector3(0, 0, 1.2f),
                        new Vector3(0, 0, 0.2f),
                        new Vector3(0, 0, -0.8f),
                        new Vector3(0, 0, -1.8f),
                        new Vector3(0, 0, -2.8f),
                    };
                }
                else if (this is TravellingVendorVehicle)
                {
                    barricadesLocalPositions = new HashSet<Vector3>
                    {
                        new Vector3(0, -0.65f, 2.6f),
                        new Vector3(0, -0.65f, 1.6f),
                        new Vector3(0, -0.65f, 0.6f),
                        new Vector3(0, -0.65f, -0.4f),
                        new Vector3(0, -0.65f, -1.4f),
                        new Vector3(0, -0.65f, -2.4f)
                    };
                }
                else if (this is SedanVehicle)
                {
                    barricadesLocalPositions = new HashSet<Vector3>
                    {
                        new Vector3(0, -0.25f, 3.5f),
                        new Vector3(0, -0.25f, 2.6f),
                        new Vector3(0, -0.25f, 1.6f),
                        new Vector3(0, -0.25f, 0.6f),
                        new Vector3(0, -0.25f, -0.4f),
                        new Vector3(0, -0.25f, -1.4f),
                        new Vector3(0, -0.25f, -2.4f)
                    };
                }

                if (_barricades.Count == 0 && _ins._eventController.IsStopped())
                {
                    foreach (Vector3 localPosition in barricadesLocalPositions)
                    {
                        BaseEntity entity = BuildManager.SpawnChildEntity(baseEntity, "assets/prefabs/deployable/barricades/barricade.concrete.prefab", localPosition, new Vector3(0, 0, 180), 3313682857, isDecor: true);
                        BuildManager.DestroyEntityComponent<NPCBarricadeTriggerBox>(entity);
                        _barricades.Add(entity);
                    }
                }
            }

            protected float GetTurnFraction(out bool shouldRotate)
            {
                shouldRotate = false;
                Vector2 carPosition = new Vector2(transform.position.x, transform.position.z);
                Vector2 pointPosition = new Vector2(_nextPathPoint.Position.x, _nextPathPoint.Position.z);
                Vector2 targetDirection = pointPosition - carPosition;

                Vector2 forward = new Vector2(baseEntity.transform.forward.x, baseEntity.transform.forward.z);
                Vector2 right = new Vector2(baseEntity.transform.right.x, baseEntity.transform.right.z);

                float rightAngle = Vector2.Angle(targetDirection, right);
                bool isLeftTurn = rightAngle is > 90 and < 270;

                float angle = Vector2.Angle(targetDirection, forward);

                if (angle >= 120)
                    shouldRotate = true;

                if (isLeftTurn)
                    angle *= -1;

                return Mathf.Clamp(angle, -90, 90);
            }

            protected float GetSpeedFraction()
            {
                float maxSpeed = GetMaxSpeed();
                Rigidbody.maxLinearVelocity = maxSpeed;

                if (_backVehicle != null && _backVehicle._nextPathPoint != null && Vector3.Distance(_backVehicle._nextPathPoint.Position, baseEntity.transform.position) > 25f)
                    return -1;

                if (_frontVehicle == null)
                    return 1;

                if (_frontVehicle != null && _frontVehicle._nextPathPoint != null && Vector3.Distance(_frontVehicle._nextPathPoint.Position, baseEntity.transform.position) < 7.5f)
                {
                    Rigidbody.maxLinearVelocity = 0;
                    return -1;
                }

                return 1;
            }

            private float GetMaxSpeed()
            {
                if (isStopped)
                    return 0;

                if (_frontVehicle == null)
                {
                    if (_ins._eventController.IsFullySpawned())
                        return 6;
                    else
                        return 3;
                }

                float idealDistance = (_frontVehicle._vehicleSize + _vehicleSize) / 2f;
                float actualDistance = Vector3.Distance(this.transform.position, _frontVehicle.transform.position);

                if (Math.Abs(actualDistance - idealDistance) < 0.5f)
                    return _frontVehicle.Rigidbody.velocity.magnitude;
                else if (actualDistance > idealDistance)
                    return 8f;
                else
                    return 0f;
            }

            private void ResetStuckCarPosition()
            {
                if (_nextPathPoint == null)
                    return;

                baseEntity.transform.position = _nextPathPoint.Position + Vector3.up;
                UpdatePath();

                if (_nextPathPoint != null && Vector3.Distance(baseEntity.transform.position, _nextPathPoint.Position) > 1f)
                    baseEntity.transform.rotation = Quaternion.LookRotation((_nextPathPoint.Position - baseEntity.transform.position).normalized);
            }

            public static void KillAllVehicles()
            {
                foreach (ConvoyPathVehicle convoyVehicle in VehicleOrder)
                    if (convoyVehicle != null)
                        convoyVehicle.KillVehicle();
            }

            public virtual void KillVehicle()
            {
                if (baseEntity.IsExists())
                    baseEntity.Kill();
            }

            private void OnDestroy()
            {
                if (EventLauncher.IsEventActive())
                {
                    VehicleOrder.Remove(this);
                    UpdateVehiclesOrder();
                }
            }

            public void DropCrates()
            {
                if (!_ins._config.LootConfig.DropLoot)
                    return;

                foreach (BaseEntity childEntity in baseEntity.children)
                {
                    StorageContainer storageContainer = childEntity as StorageContainer;

                    if (storageContainer != null)
                        storageContainer.inventory.Drop("assets/prefabs/misc/item drop/item_drop.prefab", storageContainer.transform.position, storageContainer.transform.rotation, _ins._config.LootConfig.LootLossPercent);
                }
            }
        }

        private class MovableBaseMountable : BaseMountable
        {
            public static MovableBaseMountable CreateMovableBaseMountable(string chairPrefab, BaseEntity parentEntity, Vector3 localPosition, Vector3 localRotation)
            {
                BaseMountable baseMountable = GameManager.server.CreateEntity(chairPrefab, parentEntity.transform.position) as BaseMountable;
                baseMountable.enableSaving = false;
                MovableBaseMountable movableBaseMountable = baseMountable.gameObject.AddComponent<MovableBaseMountable>();
                BuildManager.CopySerializableFields(baseMountable, movableBaseMountable);
                baseMountable.StopAllCoroutines();
                UnityEngine.GameObject.DestroyImmediate(baseMountable, true);
                BuildManager.SetParent(parentEntity, movableBaseMountable, localPosition, localRotation);
                movableBaseMountable.Spawn();
                return movableBaseMountable;
            }

            public override void DismountAllPlayers()
            {
            }

            public override bool GetDismountPosition(BasePlayer player, out Vector3 res, bool silent = false)
            {
                res = player.transform.position;
                return true;
            }

            public override void ScaleDamageForPlayer(BasePlayer player, HitInfo info)
            {

            }
        }

        private class CollisionDisabler : FacepunchBehaviour
        {
            private readonly HashSet<Collider> _colliders = new HashSet<Collider>();
            private readonly HashSet<WheelCollider> _wheelColliders = new HashSet<WheelCollider>();

            public static CollisionDisabler AttachCollisionDisabler(BaseEntity baseEntity)
            {
                CollisionDisabler collisionDisabler = baseEntity.gameObject.AddComponent<CollisionDisabler>();

                foreach (Collider collider in baseEntity.gameObject.GetComponentsInChildren<Collider>())
                    if (collider != null)
                        collisionDisabler._colliders.Add(collider);

                foreach (WheelCollider collider in baseEntity.gameObject.GetComponentsInChildren<WheelCollider>())
                    if (collider != null)
                        collisionDisabler._wheelColliders.Add(collider);

                return collisionDisabler;
            }

            private void OnCollisionEnter(Collision collision)
            {
                if (collision == null || collision.collider == null)
                    return;

                BaseEntity entity = collision.GetEntity();
                if (entity == null || entity.net == null)
                {
                    if (collision.collider.name.Contains("/cube_") || collision.collider.name.Contains("_cube"))
                        return;

                    if (collision.collider.name != "Terrain" && collision.collider.name != "Road Mesh")
                        IgnoreCollider(collision.collider);

                    return;
                }

                ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value);
                if (convoyPathVehicle != null)
                {
                    IgnoreCollider(collision.collider, convoyPathVehicle);
                    return;
                }

                if (entity.net != null && ((entity is HelicopterDebris or LootContainer or DroppedItemContainer) || (ConvoyPathVehicle.GetVehicleByChildNetId(entity.net.ID.Value) != null && entity is not TimedExplosive)))
                {
                    IgnoreCollider(collision.collider);
                    return;
                }

                if (entity is TreeEntity or ResourceEntity or JunkPile or Barricade or HotAirBalloon or BasePortal or TravellingVendor)
                {
                    entity.Kill(BaseNetworkable.DestroyMode.Gib);
                    return;
                }

                if (entity is BradleyAPC && ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) == null)
                {
                    entity.Kill(BaseNetworkable.DestroyMode.Gib);
                    return;
                }

                BaseVehicle baseVehicle = entity as BaseVehicle;
                if (baseVehicle == null)
                    baseVehicle = entity.GetParentEntity() as BaseVehicle;

                BaseVehicleModule baseVehicleModule = entity as BaseVehicleModule;
                if (baseVehicleModule != null)
                    baseVehicle = baseVehicleModule.Vehicle;

                if (baseVehicle != null && entity is not TimedExplosive)
                {
                    BasePlayer driver = baseVehicle.GetDriver();

                    if (driver.IsRealPlayer())
                        _ins._eventController.OnEventAttacked(driver);

                    if (baseVehicle is TrainEngine && baseVehicle.net != null)
                    {
                        IgnoreCollider(collision.collider);

                        if (_ins.plugins.Exists("ArmoredTrain") && (bool)_ins.ArmoredTrain.Call("IsTrainWagon", baseVehicle.net.ID.Value))
                            _ins._eventController.OnEventAttacked(null);
                    }
                    else
                    {
                        ModularCar modularCar = baseVehicle as ModularCar;

                        if (modularCar != null)
                        {
                            StorageContainer storageContainer = modularCar.GetComponentsInChildren<StorageContainer>().FirstOrDefault(x => x.name == "modular_car_fuel_storage");

                            if (!BaseMountable.AllMountables.Contains(modularCar) || !modularCar.HasAnyEngines())
                            {
                                IgnoreCollider(collision.collider);
                                return;
                            }

                            if (storageContainer != null)
                                storageContainer.inventory.Drop("assets/prefabs/misc/item drop/item_drop.prefab", storageContainer.transform.position, storageContainer.transform.rotation, 0);
                        }

                        baseVehicle.Kill(BaseNetworkable.DestroyMode.Gib);
                    }
                }
            }

            private void IgnoreCollider(Collider otherCollider, ConvoyPathVehicle convoyPathVehicle)
            {
                IgnoreCollider(otherCollider);

                if (convoyPathVehicle.collisionDisabler != null)
                {
                    foreach (Collider collider in _colliders)
                    {
                        foreach (Collider other in convoyPathVehicle.collisionDisabler._colliders)
                            Physics.IgnoreCollision(collider, other);

                        foreach (WheelCollider other in convoyPathVehicle.collisionDisabler._wheelColliders)
                            Physics.IgnoreCollision(collider, other);
                    }

                    foreach (WheelCollider collider in _wheelColliders)
                    {
                        foreach (Collider other in convoyPathVehicle.collisionDisabler._colliders)
                            Physics.IgnoreCollision(collider, other);

                        foreach (WheelCollider other in convoyPathVehicle.collisionDisabler._wheelColliders)
                            Physics.IgnoreCollision(collider, other);
                    }
                }
            }

            private void IgnoreCollider(Collider otherCollider)
            {
                foreach (Collider collider in _colliders)
                    if (collider != null)
                        Physics.IgnoreCollision(collider, otherCollider);
            }
        }

        private class EntityDecorator
        {
            public static void DecorateEntity(BaseEntity entity, EntityCustomizationData entityCustomizationData)
            {
                foreach (EntityData entityData in entityCustomizationData.DecorEntities)
                    SpawnEntity(entity, entityData, true);

                foreach (EntityData entityData in entityCustomizationData.RegularEntities)
                    SpawnEntity(entity, entityData, false);
            }

            private static void SpawnEntity(BaseEntity entity, EntityData entityData, bool isDecorEntity)
            {
                Vector3 localPosition = entityData.Position.ToVector3();
                Vector3 localRotation = entityData.Rotation.ToVector3();

                BuildManager.SpawnChildEntity(entity, entityData.PrefabName, localPosition, localRotation, entityData.Skin, isDecorEntity);
            }
        }

        private static class PathManager
        {
            public static EventPath CurrentPath;
            private static readonly HashSet<RoadMonumentData> RoadMonuments = new HashSet<RoadMonumentData>
            {
                new RoadMonumentData
                {
                    MonumentName = "assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab",
                    LocalPathPoints = new List<Vector3>
                    {
                        new Vector3(-44.502f, 0, -0.247f),
                        new Vector3(-37.827f, 0, -3.054f),
                        new Vector3(-31.451f, 0, -4.384f),
                        new Vector3(-24.0621f, 0, -7.598f),
                        new Vector3(-14.619f, 0, -5.652f),
                        new Vector3(-7.505f, 0, -0.728f),
                        new Vector3(4.770f, 0, -0.499f),
                        new Vector3(13.913f, 0, 2.828f),
                        new Vector3(18.432f, 0, 4.635f),
                        new Vector3(23.489f, 0, 3.804f),
                        new Vector3(32.881f, 0, -4.063f),
                        new Vector3(47f, 0, -0.293f),
                    },
                    MonumentSize = new Vector3(49.2f, 0, 11f),
                    Monuments = new HashSet<MonumentInfo>()
                }
            };

            public static void DrawPath(EventPath eventPath, BasePlayer player)
            {
                foreach (PathPoint startPoint in eventPath.Points)
                {
                    if (Vector3.Distance(player.transform.position, startPoint.Position) > 250)
                        continue;

                    player.SendConsoleCommand("ddraw.text", 10, Color.white, startPoint.Position, $"<size=50>{startPoint.ConnectedPoints.Count}</size>");

                    foreach (PathPoint endPoint in startPoint.ConnectedPoints)
                    {
                        player.SendConsoleCommand("ddraw.line", 10f, Color.green, startPoint.Position, endPoint.Position);
                    }
                }
            }

            public static void StartCachingRouts()
            {
                foreach (RoadMonumentData roadMonumentData in RoadMonuments)
                    roadMonumentData.Monuments = TerrainMeta.Path.Monuments.Where(x => x.name == "assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab");

                RoadMonuments.RemoveWhere(x => x.Monuments.Count == 0);

                if (_ins._config.PathConfig.PathType == 1)
                    ComplexPathGenerator.StartCachingPaths();
            }

            public static void GenerateNewPath()
            {
                CurrentPath = null;

                if (_ins._config.PathConfig.PathType == 1)
                    CurrentPath = ComplexPathGenerator.GetRandomPath();
                else if (_ins._config.PathConfig.PathType == 2)
                    CurrentPath = CustomPathGenerator.GetCustomPath();

                if (CurrentPath == null)
                    CurrentPath = RegularPathGenerator.GetRegularPath();

                if (CurrentPath != null)
                {
                    CurrentPath.StartPathPoint = DefineStartPoint();
                    CurrentPath.SpawnRotation = DefineSpawnRotation();
                }

                if (CurrentPath == null || CurrentPath.StartPathPoint == null)
                {
                    CurrentPath = null;
                    NotifyManager.PrintError(null, "RouteNotFound_Exeption");
                }
            }

            private static int GetRoadIndex(PathList road)
            {
                return TerrainMeta.Path.Roads.IndexOf(road);
            }

            private static bool IsRoadRound(Vector3[] road)
            {
                return Vector3.Distance(road[0], road[road.Length - 1]) < 5f;
            }

            private static PathPoint DefineStartPoint()
            {
                PathPoint newStartPoint;
                NavMeshHit navMeshHit;

                if (CurrentPath.IsRoundRoad)
                    newStartPoint = CurrentPath.Points.Where(x => PositionDefiner.GetNavmeshInPoint(x.Position, 2, out navMeshHit)).ToList().GetRandom();
                else
                    newStartPoint = CurrentPath.Points.Where(x => x.ConnectedPoints.Count == 1 && !IsPointSpearfishingVillage(x.Position)).ToList().GetRandom();

                if (newStartPoint == null)
                    newStartPoint = CurrentPath.Points[0];

                if (PositionDefiner.GetNavmeshInPoint(newStartPoint.Position, 2, out navMeshHit))
                    newStartPoint.Position = navMeshHit.position;
                else
                    return null;

                return newStartPoint;
            }

            private static bool IsPointSpearfishingVillage(Vector3 position)
            {
                return TerrainMeta.Path.Monuments.Any(x => x.name.Contains("fishing") && Vector3.Distance(position, x.transform.position) < 75);
            }

            private static Vector3 DefineSpawnRotation()
            {
                PathPoint secondPoint = null;

                for (int i = 0; i < CurrentPath.StartPathPoint.ConnectedPoints.Count; i++)
                {
                    if (i == 0)
                    {
                        CurrentPath.StartPathPoint.ConnectedPoints[i].Disabled = false;
                        secondPoint = CurrentPath.StartPathPoint.ConnectedPoints[i];
                    }
                    else
                        CurrentPath.StartPathPoint.ConnectedPoints[i].Disabled = true;
                }

                return (secondPoint.Position - CurrentPath.StartPathPoint.Position).normalized;
            }

            public static void OnSpawnFinish()
            {
                foreach (PathPoint pathPoint in CurrentPath.StartPathPoint.ConnectedPoints)
                    pathPoint.Disabled = false;
            }

            public static void OnPluginUnloaded()
            {
                ComplexPathGenerator.StopPathGenerating();
            }

            public static MonumentInfo GetRoadMonumentInPosition(Vector3 position)
            {
                foreach (RoadMonumentData roadMonumentData in RoadMonuments)
                {
                    foreach (MonumentInfo monumentInfo in roadMonumentData.Monuments)
                    {
                        Vector3 localPosition = PositionDefiner.GetLocalPosition(monumentInfo.transform, position);

                        if (Math.Abs(localPosition.x) < roadMonumentData.MonumentSize.x && Math.Abs(localPosition.z) < roadMonumentData.MonumentSize.z)
                            return monumentInfo;
                    }
                }

                return null;
            }

            public static void TryContinuePaThrough(MonumentInfo monumentInfo, Vector3 position, int roadIndex, ref PathPoint previousPoint, ref EventPath eventPath)
            {
                RoadMonumentData roadMonumentData = RoadMonuments.FirstOrDefault(x => x.MonumentName == monumentInfo.name);
                Vector3 startGlobalPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, roadMonumentData.LocalPathPoints[0]);
                Vector3 endGlobalPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, roadMonumentData.LocalPathPoints[roadMonumentData.LocalPathPoints.Count - 1]);

                if (Vector3.Distance(position, startGlobalPosition) < Vector3.Distance(position, endGlobalPosition))
                {
                    PathPoint monumentStartPathPoint = new PathPoint(startGlobalPosition, roadIndex);

                    if (previousPoint != null)
                    {
                        monumentStartPathPoint.ConnectPoint(previousPoint);
                        previousPoint.ConnectPoint(monumentStartPathPoint);
                    }

                    previousPoint = monumentStartPathPoint;

                    foreach (Vector3 localMonumentPosition in roadMonumentData.LocalPathPoints)
                    {
                        Vector3 globalMonumentPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, localMonumentPosition);
                        PathPoint monumentPathPoint = new PathPoint(globalMonumentPosition, roadIndex);
                        monumentPathPoint.ConnectPoint(previousPoint);
                        previousPoint.ConnectPoint(monumentPathPoint);
                        eventPath.Points.Add(monumentPathPoint);
                        previousPoint = monumentPathPoint;
                    }
                }
                else
                {
                    PathPoint monumentStartPathPoint = new PathPoint(endGlobalPosition, roadIndex);

                    if (previousPoint != null)
                    {
                        monumentStartPathPoint.ConnectPoint(previousPoint);
                        previousPoint.ConnectPoint(monumentStartPathPoint);
                    }

                    previousPoint = monumentStartPathPoint;

                    for (int i = roadMonumentData.LocalPathPoints.Count - 1; i >= 0; i--)
                    {
                        Vector3 localMonumentPosition = roadMonumentData.LocalPathPoints[i];
                        Vector3 globalMonumentPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, localMonumentPosition);
                        PathPoint monumentPathPoint = new PathPoint(globalMonumentPosition, roadIndex);
                        monumentPathPoint.ConnectPoint(previousPoint);
                        previousPoint.ConnectPoint(monumentPathPoint);
                        eventPath.Points.Add(monumentPathPoint);
                        previousPoint = monumentPathPoint;
                    }
                }
            }

            private static class RegularPathGenerator
            {
                public static EventPath GetRegularPath()
                {
                    PathList road = null;

                    if (_ins._config.PathConfig.RegularPathConfig.IsRingRoad)
                        road = GetRoundRoadPathList();

                    if (road == null)
                        road = GetRegularRoadPathList();

                    if (road == null)
                        return null;

                    EventPath caravanPath = GetPathFromRegularRoad(road);
                    return caravanPath;
                }

                private static PathList GetRoundRoadPathList()
                {
                    return TerrainMeta.Path.Roads.FirstOrDefault(x => !_ins._config.PathConfig.BlockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && IsRoadRound(x.Path.Points) && x.Path.Length > _ins._config.PathConfig.MinRoadLength);
                }

                private static PathList GetRegularRoadPathList()
                {
                    List<PathList> suitablePathList = TerrainMeta.Path.Roads.Where(x => !_ins._config.PathConfig.BlockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && x.Path.Length > _ins._config.PathConfig.MinRoadLength).ToList();

                    if (suitablePathList != null && suitablePathList.Count > 0)
                        return suitablePathList.GetRandom();
                    //return suitablePathList.Min(x => Vector3.Distance(BasePlayer.activePlayerList[0].transform.position, x.Path.Points[0]));
                    return null;
                }

                private static EventPath GetPathFromRegularRoad(PathList road)
                {
                    bool isRound = IsRoadRound(road.Path.Points);
                    EventPath caravanPath = new EventPath(isRound);
                    PathPoint previousPoint = null;
                    int roadIndex = GetRoadIndex(road);

                    bool isOnMonument = false;

                    foreach (Vector3 position in road.Path.Points)
                    {
                        if (position.y < 0 && !isRound)
                            break;

                        if (isOnMonument)
                        {
                            if (GetRoadMonumentInPosition(position) == null)
                                isOnMonument = false;
                            else
                                continue;
                        }

                        MonumentInfo monumentInfo = GetRoadMonumentInPosition(position);
                        if (monumentInfo != null)
                        {
                            isOnMonument = true;
                            TryContinuePaThrough(monumentInfo, position, roadIndex, ref previousPoint, ref caravanPath);
                            continue;
                        }

                        PathPoint newPathPoint = new PathPoint(position, roadIndex);
                        if (previousPoint != null)
                        {
                            newPathPoint.ConnectPoint(previousPoint);
                            previousPoint.ConnectPoint(newPathPoint);
                        }

                        caravanPath.Points.Add(newPathPoint);
                        previousPoint = newPathPoint;
                    }

                    if (isRound)
                    {
                        caravanPath.IsRoundRoad = true;

                        PathPoint firstPoint = caravanPath.Points.First();
                        PathPoint lastPoint = caravanPath.Points.Last();
                        firstPoint.ConnectPoint(lastPoint);
                        lastPoint.ConnectPoint(firstPoint);
                    }

                    return caravanPath;
                }
            }

            private static class CustomPathGenerator
            {
                public static EventPath GetCustomPath()
                {
                    string pathName = _ins._config.PathConfig.CustomPathConfig.CustomRoutesPresets.GetRandom();

                    if (pathName == null)
                        return null;

                    string filePath = $"{_ins.Name}/Custom routes/{pathName}";
                    CustomRouteData customRouteData = Interface.Oxide.DataFileSystem.ReadObject<CustomRouteData>(filePath);

                    if (customRouteData == null || customRouteData.Points == null || customRouteData.Points.Count == 0)
                    {
                        NotifyManager.PrintError(null, "FileNotFound_Exeption", filePath);
                        return null;
                    }

                    EventPath caravanPath = GetCaravanPathFromCustomRouteData(customRouteData);

                    return caravanPath;
                }

                private static EventPath GetCaravanPathFromCustomRouteData(CustomRouteData customRouteData)
                {
                    List<Vector3> points = new List<Vector3>();

                    foreach (string stringPoint in customRouteData.Points)
                        points.Add(stringPoint.ToVector3());

                    if (points.Count == 0)
                        return null;

                    EventPath caravanPath = new EventPath(false);
                    PathPoint previousPoint = null;

                    foreach (Vector3 position in points)
                    {
                        if (!PositionDefiner.GetNavmeshInPoint(position, 2, out NavMeshHit _))
                            return null;

                        PathPoint newPathPoint = new PathPoint(position, -1);

                        if (previousPoint != null)
                        {
                            newPathPoint.ConnectPoint(previousPoint);
                            previousPoint.ConnectPoint(newPathPoint);
                        }

                        caravanPath.Points.Add(newPathPoint);
                        previousPoint = newPathPoint;
                    }

                    return caravanPath;
                }
            }

            private static class ComplexPathGenerator
            {
                private static bool _isGenerationFinished;
                private static List<EventPath> _complexPaths = new List<EventPath>();
                private static Coroutine _cachingCoroutine;
                private static readonly HashSet<Vector3> EndPoints = new HashSet<Vector3>();

                public static EventPath GetRandomPath()
                {
                    if (!_isGenerationFinished || _complexPaths.Count == 0)
                        return null;

                    EventPath caravanPath = null;

                    if (_ins._config.PathConfig.ComplexPathConfig.ChooseLongestRoute)
                        caravanPath = _complexPaths.Max(x => x.IncludedRoadIndexes.Count);

                    if (caravanPath == null)
                        return _complexPaths.GetRandom();

                    return caravanPath;
                }

                public static void StartCachingPaths()
                {
                    CacheEndPoints();
                    _cachingCoroutine = ServerMgr.Instance.StartCoroutine(CachingCoroutine());
                }

                private static void CacheEndPoints()
                {
                    foreach (PathList road in TerrainMeta.Path.Roads)
                    {
                        EndPoints.Add(road.Path.Points.First());
                        EndPoints.Add(road.Path.Points.Last());
                    }
                }

                public static void StopPathGenerating()
                {
                    if (_cachingCoroutine != null)
                        ServerMgr.Instance.StopCoroutine(_cachingCoroutine);
                }

                private static IEnumerator CachingCoroutine()
                {
                    NotifyManager.PrintLogMessage("RouteСachingStart_Log");
                    _complexPaths.Clear();

                    for (int roadIndex = 0; roadIndex < TerrainMeta.Path.Roads.Count; roadIndex++)
                    {
                        if (_ins._config.PathConfig.BlockRoads.Contains(roadIndex))
                            continue;

                        PathList roadPathList = TerrainMeta.Path.Roads[roadIndex];
                        if (roadPathList.Path.Length < _ins._config.PathConfig.MinRoadLength)
                            continue;

                        EventPath caravanPath = new EventPath(false);
                        _complexPaths.Add(caravanPath);

                        yield return CachingRoad(roadIndex, 0, -1);
                    }

                    EndPoints.Clear();
                    UpdatePathList();
                    NotifyManager.PrintWarningMessage("RouteСachingStop_Log", _complexPaths.Count);
                    _isGenerationFinished = true;
                }
                private static IEnumerator CachingRoad(int roadIndex, int startPointIndex, int pathPointForConnectionIndex)
                {
                    EventPath path = _complexPaths.Last();
                    path.IncludedRoadIndexes.Add(roadIndex);
                    PathList road = TerrainMeta.Path.Roads[roadIndex];

                    List<PathConnectedData> pathConnectedDatas = new List<PathConnectedData>();
                    PathPoint initialPointForConnection = pathPointForConnectionIndex >= 0 ? path.Points[pathPointForConnectionIndex] : null;
                    PathPoint pointForConnection = initialPointForConnection;

                    bool isOnMonument = false;

                    for (int pointIndex = startPointIndex + 1; pointIndex < road.Path.Points.Length; pointIndex++)
                    {
                        Vector3 position = road.Path.Points[pointIndex];
                        if (position.y < 0)
                            break;

                        if (isOnMonument)
                        {
                            if (GetRoadMonumentInPosition(position) == null)
                                isOnMonument = false;
                            else
                                continue;
                        }

                        MonumentInfo monumentInfo = GetRoadMonumentInPosition(position);

                        if (monumentInfo != null)
                        {
                            isOnMonument = true;

                            TryContinuePaThrough(monumentInfo, position, roadIndex, ref pointForConnection, ref path);
                            continue;
                        }

                        pointForConnection = CachingPoint(roadIndex, pointIndex, pointForConnection, out PathConnectedData pathConnectedData);

                        if (pathConnectedData != null)
                            pathConnectedDatas.Add(pathConnectedData);

                        if (pointIndex % 50 == 0)
                            yield return null;
                    }

                    isOnMonument = false;
                    pointForConnection = initialPointForConnection;

                    for (int pointIndex = startPointIndex - 1; pointIndex >= 0; pointIndex--)
                    {
                        Vector3 position = road.Path.Points[pointIndex];
                        if (position.y < 0)
                            break;

                        if (isOnMonument)
                        {
                            if (GetRoadMonumentInPosition(position) == null)
                                isOnMonument = false;
                            else
                                continue;
                        }

                        MonumentInfo monumentInfo = GetRoadMonumentInPosition(position);

                        if (monumentInfo != null)
                        {
                            isOnMonument = true;

                            TryContinuePaThrough(monumentInfo, position, roadIndex, ref pointForConnection, ref path);
                            continue;
                        }

                        pointForConnection = CachingPoint(roadIndex, pointIndex, pointForConnection, out PathConnectedData pathConnectedData);

                        if (pathConnectedData != null)
                            pathConnectedDatas.Add(pathConnectedData);

                        if (pointIndex % 50 == 0)
                            yield return null;
                    }

                    foreach (PathConnectedData pathConnectedData in pathConnectedDatas)
                    {
                        if (path.IncludedRoadIndexes.Contains(pathConnectedData.NewRoadIndex))
                            continue;

                        Vector3 currentRoadPoint = road.Path.Points[pathConnectedData.PathPointIndex];
                        PathList newRoadPathList = TerrainMeta.Path.Roads[pathConnectedData.NewRoadIndex];
                        Vector3 newRoadPoint = newRoadPathList.Path.Points.Min(x => Vector3.Distance(x, currentRoadPoint));
                        int indexForStartSaving = Array.IndexOf(newRoadPathList.Path.Points, newRoadPoint);

                        yield return CachingRoad(pathConnectedData.NewRoadIndex, indexForStartSaving, pathConnectedData.PointForConnectionIndex);
                    }
                }

                private static PathPoint CachingPoint(int roadIndex, int pointIndex, PathPoint lastPathPoint, out PathConnectedData pathConnectedData)
                {
                    EventPath eventPath = _complexPaths.Last();
                    PathList road = TerrainMeta.Path.Roads[roadIndex];
                    Vector3 point = road.Path.Points[pointIndex];
                    PathPoint newPathPoint = new PathPoint(point, roadIndex);

                    if (lastPathPoint != null)
                    {
                        // if (Vector3.Distance(newPathPoint.Position, lastPathPoint.Position) > 15f)
                        // {
                        //     pathConnectedData = null;
                        //     return null;
                        // }

                        newPathPoint.ConnectPoint(lastPathPoint);
                        lastPathPoint.ConnectPoint(newPathPoint);
                    }

                    eventPath.Points.Add(newPathPoint);

                    PathList newRoad = null;
                    pathConnectedData = null;

                    if (pointIndex == 0 || pointIndex == road.Path.Points.Length - 1)
                        newRoad = TerrainMeta.Path.Roads.FirstOrDefault(x => !_ins._config.PathConfig.BlockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && x.Path.Length > _ins._config.PathConfig.MinRoadLength && !eventPath.IncludedRoadIndexes.Contains(GetRoadIndex(x)) && (Vector3.Distance(x.Path.Points.First(), point) < 7.5f || Vector3.Distance(x.Path.Points.Last(), point) < 7.5f));
                    if (EndPoints.Any(x => Vector3.Distance(x, point) < 7.5f))
                        newRoad = TerrainMeta.Path.Roads.FirstOrDefault(x => x != road && !_ins._config.PathConfig.BlockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && x.Path.Length > _ins._config.PathConfig.MinRoadLength && !eventPath.IncludedRoadIndexes.Contains(GetRoadIndex(x)) && x.Path.Points.Any(y => Vector3.Distance(y, point) < 7.5f));

                    if (newRoad != null)
                    {
                        int newRoadIndex = GetRoadIndex(newRoad);
                        int pointForConnectionIndex = eventPath.Points.IndexOf(newPathPoint);

                        pathConnectedData = new PathConnectedData
                        {
                            PathPointIndex = pointIndex,
                            NewRoadIndex = newRoadIndex,
                            PointForConnectionIndex = pointForConnectionIndex
                        };
                    }

                    return newPathPoint;
                }

                private static bool IsRingRoad(PathList road)
                {
                    return road.Hierarchy == 0 && Vector3.Distance(road.Path.Points[0], road.Path.Points[road.Path.Points.Length - 1]) < 2f;
                }

                private static void UpdatePathList()
                {
                    List<EventPath> clonePath = new List<EventPath>();

                    for (int i = 0; i < _complexPaths.Count; i++)
                    {
                        EventPath eventPath = _complexPaths[i];

                        if (eventPath == null || eventPath.IncludedRoadIndexes.Count < _ins._config.PathConfig.ComplexPathConfig.MinRoadCount)
                            continue;

                        if (_complexPaths.Any(x => x.Points.Count > eventPath.Points.Count && !eventPath.IncludedRoadIndexes.Any(y => !x.IncludedRoadIndexes.Contains(y))))
                            continue;

                        clonePath.Add(eventPath);
                    }

                    _complexPaths = clonePath;
                }

                private class PathConnectedData
                {
                    public int PathPointIndex;
                    public int NewRoadIndex;
                    public int PointForConnectionIndex;
                }
            }

            private class RoadMonumentData
            {
                public string MonumentName;
                public List<Vector3> LocalPathPoints;
                public Vector3 MonumentSize;
                public HashSet<MonumentInfo> Monuments;
            }
        }

        private class EventPath
        {
            public readonly List<PathPoint> Points = new List<PathPoint>();
            public readonly HashSet<int> IncludedRoadIndexes = new HashSet<int>();
            public bool IsRoundRoad;
            public PathPoint StartPathPoint;
            public Vector3 SpawnRotation;

            public EventPath(bool isRoundRoad)
            {
                IsRoundRoad = isRoundRoad;
            }
        }

        private class PathPoint
        {
            public Vector3 Position;
            public readonly List<PathPoint> ConnectedPoints = new List<PathPoint>();
            public bool Disabled;
            public readonly int RoadIndex;
            public float LastVisitTime;

            public PathPoint(Vector3 position, int roadIndex)
            {
                this.Position = position;
                this.RoadIndex = roadIndex;
            }

            public void ConnectPoint(PathPoint pathPoint)
            {
                ConnectedPoints.Add(pathPoint);
            }
        }

        private class EventHeli : FacepunchBehaviour
        {
            public HeliConfig HeliConfig;
            private PatrolHelicopter _patrolHelicopter;
            private Vector3 _patrolPosition;
            private int _outsideTime;
            private bool _isFollowing;
            private bool _isDead;
            public ulong lastAttackedPlayer;

            public static void SpawnHeli(HeliConfig heliConfig)
            {
                Vector3 position = ConvoyPathVehicle.GetEventPosition() + Vector3.up * heliConfig.Height;

                PatrolHelicopter patrolHelicopter = BuildManager.SpawnRegularEntity("assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab", position, Quaternion.identity, 755446) as PatrolHelicopter;
                patrolHelicopter.transform.position = position;
                _ins._eventHeli = patrolHelicopter.gameObject.AddComponent<EventHeli>();
                _ins._eventHeli.Init(heliConfig, patrolHelicopter);
                EntityPresetInfo.Attach(patrolHelicopter, heliConfig.PresetName);
            }

            public static EventHeli GetEventHeliByNetId(ulong netID)
            {
                if (_ins._eventHeli != null && _ins._eventHeli._patrolHelicopter.IsExists() && _ins._eventHeli._patrolHelicopter.net != null && _ins._eventHeli._patrolHelicopter.net.ID.Value == netID)
                    return _ins._eventHeli;
                else
                    return null;
            }

            public static HashSet<ulong> GetAliveHeliNetIds()
            {
                HashSet<ulong> helies = new HashSet<ulong>();

                if (_ins._eventHeli != null && _ins._eventHeli._patrolHelicopter != null && _ins._eventHeli._patrolHelicopter.net != null)
                    helies.Add(_ins._eventHeli._patrolHelicopter.net.ID.Value);

                return helies;
            }

            private void Init(HeliConfig heliConfig, PatrolHelicopter patrolHelicopter)
            {
                this.HeliConfig = heliConfig;
                this._patrolHelicopter = patrolHelicopter;
                UpdateHelicopter();
                StartFollowing();
                patrolHelicopter.InvokeRepeating(UpdatePosition, 1, 1);
            }

            private void UpdateHelicopter()
            {
                BuildManager.UpdateEntityMaxHealth(_patrolHelicopter, HeliConfig.Hp);
                _patrolHelicopter.maxCratesToSpawn = HeliConfig.CratesAmount;
                _patrolHelicopter.bulletDamage = HeliConfig.BulletDamage;
                _patrolHelicopter.bulletSpeed = HeliConfig.BulletSpeed;

                PatrolHelicopter.weakspot[] helicopterWeakspots = _patrolHelicopter.weakspots;
                if (helicopterWeakspots != null && helicopterWeakspots.Length > 1)
                {
                    helicopterWeakspots[0].maxHealth = HeliConfig.MainRotorHealth;
                    helicopterWeakspots[0].health = HeliConfig.MainRotorHealth;
                    helicopterWeakspots[1].maxHealth = HeliConfig.RearRotorHealth;
                    helicopterWeakspots[1].health = HeliConfig.RearRotorHealth;
                }
            }

            private void UpdatePosition()
            {
                if (_isDead)
                    return;

                _isDead = _patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.DEATH;

                if (_isDead && HeliConfig.ImmediatelyKill)
                {
                    _patrolHelicopter.Hurt(_patrolHelicopter.health * 2f, DamageType.Generic, useProtection: false);
                    return;
                }

                _patrolHelicopter.myAI.spawnTime = Time.realtimeSinceStartup;

                if (_patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.STRAFE || _patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.ORBIT || _patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.ORBITSTRAFE)
                    return;

                if (_ins._eventController.IsStopped())
                {
                    if (_isFollowing)
                        StartPatrol();
                }
                else if (!_isFollowing)
                    StartFollowing();

                if (_isFollowing)
                    DoFollowing();
                else
                    DoPatrol();
            }

            private void StartFollowing()
            {
                _isFollowing = true;
            }

            private void StartPatrol()
            {
                _isFollowing = false;
                _outsideTime = 0;
                _patrolPosition = ConvoyPathVehicle.GetEventPosition() + Vector3.up * HeliConfig.Height;
            }

            private void DoFollowing()
            {
                Vector3 position = ConvoyPathVehicle.GetEventPosition();

                if (position == Vector3.zero)
                {
                    Kill();
                    return;
                }

                position += Vector3.up * HeliConfig.Height;
                _patrolHelicopter.myAI.State_Move_Enter(position);

                if (Vector3.Distance(_patrolHelicopter.transform.position, position) < 35)
                {
                    Vector3 targetRotation = (position - _patrolHelicopter.transform.position).normalized;
                    targetRotation.y = 0;
                    _patrolHelicopter.myAI.SetIdealRotation(Quaternion.LookRotation(targetRotation));
                }
            }

            private void DoPatrol()
            {
                if (_patrolHelicopter.myAI.leftGun.HasTarget() || _patrolHelicopter.myAI.rightGun.HasTarget())
                {
                    if (Vector3.Distance(_patrolPosition, _patrolHelicopter.transform.position) > HeliConfig.Distance)
                    {
                        _outsideTime++;

                        if (_outsideTime > HeliConfig.OutsideTime)
                            _patrolHelicopter.myAI.State_Move_Enter(_patrolPosition);
                    }
                    else
                    {
                        _outsideTime = 0;
                    }
                }
                else if (Vector3.Distance(_patrolPosition, _patrolHelicopter.transform.position) > HeliConfig.Distance)
                {
                    _patrolHelicopter.myAI.State_Move_Enter(_patrolPosition);
                    _outsideTime = 0;
                }
                else
                    _outsideTime = 0;
            }

            public bool IsHeliCanTarget()
            {
                return _ins._eventController.IsAggressive();
            }

            public void OnHeliAttacked(ulong userId)
            {
                if (!_patrolHelicopter.myAI.isDead)
                    lastAttackedPlayer = userId;
            }

            public void Kill()
            {
                if (_patrolHelicopter.IsExists())
                    _patrolHelicopter.Kill();
            }

            public static void ClearData()
            {
                if (_ins._eventHeli != null)
                    _ins._eventHeli.Kill();

                _ins._eventHeli = null;
            }
        }

        private class ZoneController : FacepunchBehaviour
        {
            private static ZoneController _zoneController;
            private SphereCollider _sphereCollider;
            private Coroutine _zoneUpdateCoroutine;
            private readonly HashSet<BaseEntity> _spheres = new HashSet<BaseEntity>();
            private readonly HashSet<BasePlayer> _playersInZone = new HashSet<BasePlayer>();

            public static void CreateZone(BasePlayer externalOwner = null)
            {
                TryDeleteZone();
                Vector3 position = ConvoyPathVehicle.GetEventPosition();

                if (position == Vector3.zero)
                    return;

                GameObject gameObject = new GameObject();
                gameObject.transform.position = position;
                gameObject.layer = (int)Layer.Reserved1;

                _zoneController = gameObject.AddComponent<ZoneController>();
                _zoneController.Init(externalOwner);
            }

            public static bool IsPlayerInZone(ulong userID)
            {
                return _zoneController != null && _zoneController._playersInZone.Any(x => x != null && x.userID == userID);
            }

            public static bool IsAnyPlayerInEventZone()
            {
                return _zoneController != null && _zoneController._playersInZone.Any(x => x.IsExists() && !x.IsSleeping());
            }

            public static void OnPlayerLeaveZone(BasePlayer player)
            {
                if (_zoneController == null)
                    return;

                Interface.CallHook($"OnPlayerExit{_ins.Name}", player);
                _zoneController._playersInZone.Remove(player);
                GuiManager.DestroyGui(player);

                if (_ins._config.ZoneConfig.IsPvpZone)
                {
                    if (_ins.plugins.Exists("DynamicPVP") && (bool)_ins.DynamicPVP.Call("IsPlayerInPVPDelay", (ulong)player.userID))
                        return;

                    NotifyManager.SendMessageToPlayer(player, "ExitPVP", _ins._config.Prefix);
                }
            }

            private void Init(BasePlayer externalOwner)
            {
                CreateTriggerSphere();
                CreateSpheres();

                if (PveModeManager.IsPveModeEnabled())
                    PveModeManager.CreatePveModeZone(this.transform.position, externalOwner);

                _zoneUpdateCoroutine = ServerMgr.Instance.StartCoroutine(ZoneUpdateCoroutine());
            }

            private void CreateTriggerSphere()
            {
                _sphereCollider = gameObject.AddComponent<SphereCollider>();
                _sphereCollider.isTrigger = true;
                _sphereCollider.radius = _ins._eventController.EventConfig.ZoneRadius;
            }

            private void CreateSpheres()
            {
                if (_ins._config.ZoneConfig.IsDome)
                    for (int i = 0; i < _ins._config.ZoneConfig.Darkening; i++)
                        CreateSphere("assets/prefabs/visualization/sphere.prefab");

                if (_ins._config.ZoneConfig.IsColoredBorder)
                {
                    string spherePrefab = _ins._config.ZoneConfig.BorderColor == 0 ? "assets/bundled/prefabs/modding/events/twitch/br_sphere.prefab" : _ins._config.ZoneConfig.BorderColor == 1 ? "assets/bundled/prefabs/modding/events/twitch/br_sphere_green.prefab" :
                         _ins._config.ZoneConfig.BorderColor == 2 ? "assets/bundled/prefabs/modding/events/twitch/br_sphere_purple.prefab" : "assets/bundled/prefabs/modding/events/twitch/br_sphere_red.prefab";

                    for (int i = 0; i < _ins._config.ZoneConfig.Brightness; i++)
                        CreateSphere(spherePrefab);
                }
            }

            private void CreateSphere(string prefabName)
            {
                BaseEntity sphere = GameManager.server.CreateEntity(prefabName, gameObject.transform.position);
                SphereEntity entity = sphere.GetComponent<SphereEntity>();
                entity.currentRadius = _ins._eventController.EventConfig.ZoneRadius * 2;
                entity.lerpSpeed = 0f;
                sphere.enableSaving = false;
                sphere.Spawn();
                _spheres.Add(sphere);
            }

            private void OnTriggerEnter(Collider other)
            {
                BasePlayer player = other.GetComponentInParent<BasePlayer>();
                if (player.IsRealPlayer())
                {
                    Interface.CallHook($"OnPlayerEnter{_ins.Name}", player);
                    _playersInZone.Add(player);

                    if (_ins._config.ZoneConfig.IsPvpZone)
                        NotifyManager.SendMessageToPlayer(player, "EnterPVP", _ins._config.Prefix);

                    GuiManager.CreateGui(player, NotifyManager.GetTimeMessage(player.UserIDString, _ins._eventController.GetEventTime()), _ins._eventController.GetCountOfUnlootedCrates().ToString(), NpcSpawnManager.GetEventNpcCount().ToString());
                }
            }

            private void OnTriggerExit(Collider other)
            {
                BasePlayer player = other.GetComponentInParent<BasePlayer>();

                if (player.IsRealPlayer())
                    OnPlayerLeaveZone(player);
            }

            private IEnumerator ZoneUpdateCoroutine()
            {
                while (_zoneController != null)
                {
                    int countOfCrates = _ins._eventController.GetCountOfUnlootedCrates();
                    int countOfGuardNpc = NpcSpawnManager.GetEventNpcCount();

                    foreach (BasePlayer player in _playersInZone)
                        if (player != null)
                            GuiManager.CreateGui(player, NotifyManager.GetTimeMessage(player.UserIDString, _ins._eventController.GetEventTime()), countOfCrates.ToString(), countOfGuardNpc.ToString());

                    yield return CoroutineEx.waitForSeconds(1f);
                }
            }

            public static void TryDeleteZone()
            {
                if (_zoneController != null)
                    _zoneController.DeleteZone();
            }

            private void DeleteZone()
            {
                foreach (BaseEntity sphere in _spheres)
                    if (sphere != null && !sphere.IsDestroyed)
                        sphere.Kill();

                if (_zoneUpdateCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(_zoneUpdateCoroutine);

                GuiManager.DestroyAllGui();
                PveModeManager.DeletePveModeZone();
                UnityEngine.GameObject.Destroy(gameObject);
            }
        }

        private static class PveModeManager
        {
            private static HashSet<ulong> _pveModeOwners = new HashSet<ulong>();
            private static BasePlayer _owner;
            private static float _lastZoneDeleteTime;

            public static bool IsPveModeEnabled()
            {
                return _ins._config.SupportedPluginsConfig.PveMode.Enable && _ins.plugins.Exists("PveMode");
            }

            public static BasePlayer UpdateAndGetEventOwner()
            {
                if (_ins._eventController.IsStopped())
                    return _owner;

                float timeScienceLastZoneDelete = Time.realtimeSinceStartup - _lastZoneDeleteTime;

                if (timeScienceLastZoneDelete > _ins._config.SupportedPluginsConfig.PveMode.TimeExitOwner)
                    _owner = null;

                return _owner;
            }

            public static void CreatePveModeZone(Vector3 position, BasePlayer externalOwner)
            {
                Dictionary<string, object> config = GetPveModeConfig();

                HashSet<ulong> npc = NpcSpawnManager.GetEventNpcNetIds();
                HashSet<ulong> bradley = ConvoyPathVehicle.GetAllBradleyNetIds();
                HashSet<ulong> helicopters = EventHeli.GetAliveHeliNetIds();
                HashSet<ulong> crates = _ins._eventController.GetEventCratesNetIDs();
                HashSet<ulong> turrets = _ins._eventController.GetAliveTurretsNetIds();

                if (externalOwner == null)
                    externalOwner = GetEventOwner();

                _ins.PveMode.Call("EventAddPveMode", _ins.Name, config, position, _ins._eventController.EventConfig.ZoneRadius, crates, npc, bradley, helicopters, turrets, _pveModeOwners, externalOwner);
            }

            private static BasePlayer GetEventOwner()
            {
                BasePlayer playerOwner = null;

                float timeScienceLastZoneDelete = Time.realtimeSinceStartup - _lastZoneDeleteTime;

                if (_owner != null && (_ins._eventController.IsStopped() || timeScienceLastZoneDelete < _ins._config.SupportedPluginsConfig.PveMode.TimeExitOwner))
                    playerOwner = _owner;

                return playerOwner;
            }

            private static Dictionary<string, object> GetPveModeConfig()
            {
                return new Dictionary<string, object>
                {
                    ["Damage"] = _ins._config.SupportedPluginsConfig.PveMode.Damage,
                    ["ScaleDamage"] = _ins._config.SupportedPluginsConfig.PveMode.ScaleDamage,
                    ["LootCrate"] = _ins._config.SupportedPluginsConfig.PveMode.LootCrate,
                    ["HackCrate"] = _ins._config.SupportedPluginsConfig.PveMode.HackCrate,
                    ["LootNpc"] = _ins._config.SupportedPluginsConfig.PveMode.LootNpc,
                    ["DamageNpc"] = _ins._config.SupportedPluginsConfig.PveMode.DamageNpc,
                    ["DamageTank"] = _ins._config.SupportedPluginsConfig.PveMode.DamageTank,
                    ["DamageHelicopter"] = _ins._config.SupportedPluginsConfig.PveMode.DamageHeli,
                    ["DamageTurret"] = _ins._config.SupportedPluginsConfig.PveMode.DamageTurret,
                    ["TargetNpc"] = _ins._config.SupportedPluginsConfig.PveMode.TargetNpc,
                    ["TargetTank"] = _ins._config.SupportedPluginsConfig.PveMode.TargetTank,
                    ["TargetHelicopter"] = _ins._config.SupportedPluginsConfig.PveMode.TargetHeli,
                    ["TargetTurret"] = _ins._config.SupportedPluginsConfig.PveMode.TargetTurret,
                    ["CanEnter"] = _ins._config.SupportedPluginsConfig.PveMode.CanEnter,
                    ["CanEnterCooldownPlayer"] = _ins._config.SupportedPluginsConfig.PveMode.CanEnterCooldownPlayer,
                    ["TimeExitOwner"] = _ins._config.SupportedPluginsConfig.PveMode.TimeExitOwner,
                    ["AlertTime"] = _ins._config.SupportedPluginsConfig.PveMode.AlertTime,
                    ["RestoreUponDeath"] = _ins._config.SupportedPluginsConfig.PveMode.RestoreUponDeath,
                    ["CooldownOwner"] = _ins._config.SupportedPluginsConfig.PveMode.Cooldown,
                    ["Darkening"] = 0
                };
            }

            public static void DeletePveModeZone()
            {
                if (!IsPveModeEnabled())
                    return;

                _lastZoneDeleteTime = Time.realtimeSinceStartup;
                _pveModeOwners = (HashSet<ulong>)_ins.PveMode.Call("GetEventOwners", _ins.Name);

                if (_pveModeOwners == null)
                    _pveModeOwners = new HashSet<ulong>();

                ulong userId = (ulong)_ins.PveMode.Call("GetEventOwner", _ins.Name);
                OnNewOwnerSet(userId);

                _ins.PveMode.Call("EventRemovePveMode", _ins.Name, false);
            }

            public static void AddCrate(ulong crateNetId)
            {
                if (!IsPveModeEnabled())
                    return;
                
                HashSet<ulong> cratesIds = new HashSet<ulong> {crateNetId};
                _ins.PveMode.Call("EventAddCrates", _ins.Name, cratesIds);
            }

            private static void OnNewOwnerSet(ulong userId)
            {
                if (userId == 0)
                    return;

                BasePlayer player = BasePlayer.FindByID(userId);
                OnNewOwnerSet(player);
            }

            public static void OnNewOwnerSet(BasePlayer player)
            {
                _owner = player;
            }

            public static void OnOwnerDeleted()
            {
                _owner = null;
            }

            public static void OnEventEnd()
            {
                if (IsPveModeEnabled())
                    _ins.PveMode.Call("EventAddCooldown", _ins.Name, _pveModeOwners, _ins._config.SupportedPluginsConfig.PveMode.Cooldown);

                _lastZoneDeleteTime = 0;
                _pveModeOwners.Clear();
                _owner = null;
            }

            public static bool IsPveModDefaultBlockAction(BasePlayer player)
            {

                if (IsPveModeEnabled())
                    return _ins.PveMode.Call("CanActionEventNoMessage", _ins.Name, player) != null;

                return false;
            }

            public static bool IsPveModeBlockInteractByCooldown(BasePlayer player)
            {
                if (!IsPveModeEnabled())
                    return false;

                BasePlayer eventOwner = GetEventOwner();

                if ((_ins._config.SupportedPluginsConfig.PveMode.NoInteractIfCooldownAndNoOwners && eventOwner == null) || _ins._config.SupportedPluginsConfig.PveMode.NoDealDamageIfCooldownAndTeamOwner)
                    return !(bool)_ins.PveMode.Call("CanTimeOwner", _ins.Name, (ulong)player.userID, _ins._config.SupportedPluginsConfig.PveMode.Cooldown);

                return false;
            }

            public static bool IsPveModeBlockNoOwnerLooting(BasePlayer player)
            {
                if (!IsPveModeEnabled())
                    return false;

                BasePlayer eventOwner = GetEventOwner();

                if (eventOwner == null)
                    return false;

                if (_ins._config.SupportedPluginsConfig.PveMode.CanLootOnlyOwner && !IsTeam(player, eventOwner.userID))
                    return true;

                return false;
            }

            public static bool IsPlayerHaveCooldown(ulong userId)
            {
                if (!IsPveModeEnabled())
                    return false;

                return !(bool)_ins.PveMode.Call("CanTimeOwner", _ins.Name, userId, _ins._config.SupportedPluginsConfig.PveMode.Cooldown);
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
        }

        private class EventMapMarker : FacepunchBehaviour
        {
            private static EventMapMarker _eventMapMarker;

            private MapMarkerGenericRadius _radiusMarker;
            private VendingMachineMapMarker _vendingMarker;
            private Coroutine _updateCounter;

            public static void CreateMarker()
            {
                if (!_ins._config.MarkerConfig.Enable) return;

                GameObject gameObject = new GameObject
                {
                    layer = (int)Layer.Reserved1
                };
                _eventMapMarker = gameObject.AddComponent<EventMapMarker>();
                _eventMapMarker.Init();
            }

            private void Init()
            {
                Vector3 eventPosition = ConvoyPathVehicle.GetEventPosition();
                CreateRadiusMarker(eventPosition);
                CreateVendingMarker(eventPosition);
                _updateCounter = ServerMgr.Instance.StartCoroutine(MarkerUpdateCounter());
            }

            private void CreateRadiusMarker(Vector3 position)
            {
                if (!_ins._config.MarkerConfig.UseRingMarker)
                    return;

                _radiusMarker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", position) as MapMarkerGenericRadius;
                _radiusMarker.enableSaving = false;
                _radiusMarker.Spawn();
                _radiusMarker.radius = _ins._config.MarkerConfig.Radius;
                _radiusMarker.alpha = _ins._config.MarkerConfig.Alpha;
                _radiusMarker.color1 = new Color(_ins._config.MarkerConfig.Color1.R, _ins._config.MarkerConfig.Color1.G, _ins._config.MarkerConfig.Color1.B);
                _radiusMarker.color2 = new Color(_ins._config.MarkerConfig.Color2.R, _ins._config.MarkerConfig.Color2.G, _ins._config.MarkerConfig.Color2.B);
            }

            private void CreateVendingMarker(Vector3 position)
            {
                if (!_ins._config.MarkerConfig.UseShopMarker)
                    return;

                _vendingMarker = GameManager.server.CreateEntity("assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab", position) as VendingMachineMapMarker;
                _vendingMarker.Spawn();
                _vendingMarker.markerShopName = $"{_ins._eventController.EventConfig.DisplayName} ({NotifyManager.GetTimeMessage(null, _ins._eventController.GetEventTime())})";
            }

            private IEnumerator MarkerUpdateCounter()
            {
                while (EventLauncher.IsEventActive())
                {
                    Vector3 position = ConvoyPathVehicle.GetEventPosition();
                    UpdateVendingMarker(position);
                    UpdateRadiusMarker(position);
                    yield return CoroutineEx.waitForSeconds(1f);
                }
            }

            private void UpdateRadiusMarker(Vector3 position)
            {
                if (!_radiusMarker.IsExists())
                    return;

                _radiusMarker.transform.position = position;
                _radiusMarker.SendUpdate();
                _radiusMarker.SendNetworkUpdate();
            }

            private void UpdateVendingMarker(Vector3 position)
            {
                if (!_vendingMarker.IsExists())
                    return;

                _vendingMarker.transform.position = position;
                BasePlayer pveModeEventOwner = PveModeManager.UpdateAndGetEventOwner();
                string displayEventOwnerName = _ins._config.SupportedPluginsConfig.PveMode.ShowEventOwnerNameOnMap && pveModeEventOwner != null ? GetMessage("Marker_EventOwner", null, pveModeEventOwner.displayName) : "";
                _vendingMarker.markerShopName = $"{_ins._eventController.EventConfig.DisplayName} ({NotifyManager.GetTimeMessage(null, _ins._eventController.GetEventTime())}) {displayEventOwnerName}";
                _vendingMarker.SetFlag(BaseEntity.Flags.Busy, pveModeEventOwner == null);
                _vendingMarker.SendNetworkUpdate();
            }

            public static void DeleteMapMarker()
            {
                if (_eventMapMarker != null)
                    _eventMapMarker.Delete();
            }

            private void Delete()
            {
                if (_radiusMarker.IsExists())
                    _radiusMarker.Kill();

                if (_vendingMarker.IsExists())
                    _vendingMarker.Kill();

                if (_updateCounter != null)
                    ServerMgr.Instance.StopCoroutine(_updateCounter);

                Destroy(_eventMapMarker.gameObject);
            }
        }

        private static class ModularCarManager
        {
            public static ModularCar SpawnModularCar(Vector3 position, Quaternion rotation, List<string> moduleShortnames)
            {
                int carLength = GetRequiredCarLength(moduleShortnames);

                string prefabName = $"assets/content/vehicles/modularcar/{carLength}module_car_spawned.entity.prefab";

                ModularCar modularCar = BuildManager.CreateEntity(prefabName, position, rotation, 0, false) as ModularCar;
                modularCar.spawnSettings.useSpawnSettings = false;
                modularCar.Spawn();
                CreateCarModules(modularCar, moduleShortnames);

                modularCar.Invoke(() => DelayedCarUpdate(modularCar), 0.5f);

                return modularCar;
            }

            private static int GetRequiredCarLength(List<string> moduleShortnameList)
            {
                int doubleModulesCount = moduleShortnameList.Where(x => x.Contains("2mod")).Count;

                int count = doubleModulesCount + moduleShortnameList.Count;

                if (count < 2)
                    count = 2;
                else if (count > 4)
                    count = 4;

                return count;
            }

            private static void CreateCarModules(ModularCar modularCar, List<string> modules)
            {
                int lastAddedModuleIndex = -1;

                for (int socketIndex = 0; socketIndex < modularCar.TotalSockets; socketIndex++)
                {
                    int newModuleIndex = lastAddedModuleIndex + 1;

                    if (newModuleIndex >= modules.Count)
                        return;

                    lastAddedModuleIndex = newModuleIndex;

                    string itemShortname = modules[newModuleIndex];

                    if (itemShortname == "")
                        continue;

                    Item moduleItem = ItemManager.CreateByName(itemShortname);
                    if (moduleItem == null)
                        continue;

                    if (!modularCar.TryAddModule(moduleItem, socketIndex))
                    {
                        moduleItem.Remove();
                        continue;
                    }


                    if (itemShortname.Contains("2mod"))
                        ++socketIndex;
                }
            }

            private static void DelayedCarUpdate(ModularCar modularCar)
            {
                if (modularCar == null || modularCar.rigidBody == null)
                    return;

                modularCar.rigidBody.mass = 3000;
                modularCar.SetFlag(BaseEntity.Flags.Locked, true);

                foreach (TriggerParentEnclosed triggerBase in modularCar.GetComponentsInChildren<TriggerParentEnclosed>())
                    UnityEngine.Object.Destroy(triggerBase);

                UpdateModules(modularCar);
                UpdateFuelSystem(modularCar);
                modularCar.SetFlag(BaseEntity.Flags.Busy, true);
            }

            public static void UpdateFuelSystem(BaseVehicle vehicle)
            {
                EntityFuelSystem entityFuelSystem = vehicle.GetFuelSystem() as EntityFuelSystem;
                entityFuelSystem.cachedHasFuel = true;
                entityFuelSystem.nextFuelCheckTime = float.MaxValue;
            }

            private static void UpdateModules(ModularCar modularCar)
            {
                foreach (BaseVehicleModule module in modularCar.AttachedModuleEntities)
                {
                    StorageContainer storageContainer = module.children.FirstOrDefault(x => x is StorageContainer) as StorageContainer;

                    if (storageContainer != null)
                    {
                        storageContainer.SetFlag(BaseEntity.Flags.Busy, true);
                        storageContainer.SetFlag(BaseEntity.Flags.Locked, true);
                    }

                    VehicleModuleEngine engineModule = module as VehicleModuleEngine;

                    if (engineModule == null)
                        continue;

                    engineModule.engine.maxFuelPerSec = 0;
                    engineModule.engine.idleFuelPerSec = 0;

                    EngineStorage engineStorage = engineModule.GetContainer() as EngineStorage;

                    if (engineStorage == null)
                        continue;

                    engineStorage.dropsLoot = false;
                    engineStorage.SetFlag(BaseEntity.Flags.Locked, true);

                    for (int i = 0; i < engineStorage.inventory.capacity; i++)
                    {
                        if (!engineStorage.allEngineItems.TryGetItem(1, engineStorage.slotTypes[i], out ItemModEngineItem itemModEngineItem))
                            continue;

                        ItemDefinition component = itemModEngineItem.GetComponent<ItemDefinition>();
                        Item item = ItemManager.Create(component);
                        item._maxCondition = int.MaxValue;
                        item.condition = int.MaxValue;

                        if (!item.MoveToContainer(engineStorage.inventory, i, allowStack: false))
                            item.RemoveFromWorld();
                    }

                    engineModule.RefreshPerformanceStats(engineStorage);
                    return;
                }
            }
        }

        private static class NpcSpawnManager
        {
            private static readonly HashSet<ScientistNPC> AllNpc = new HashSet<ScientistNPC>();

            public static int GetEventNpcCount()
            {
                return AllNpc.Where(x => x.IsExists() && !x.isMounted).Count;
            }

            public static HashSet<ulong> GetEventNpcNetIds()
            {
                HashSet<ulong> result = new HashSet<ulong>();

                foreach (ScientistNPC scientistNpc in AllNpc)
                    if (scientistNpc != null && scientistNpc.net != null)
                        result.Add(scientistNpc.net.ID.Value);

                return result;
            }

            public static ScientistNPC GetScientistByNetId(ulong netId)
            {
                return AllNpc.FirstOrDefault(x => x != null && x.net != null && x.net.ID.Value == netId);
            }

            public static bool IsNpcSpawnReady()
            {
                if (!_ins.plugins.Exists("NpcSpawn"))
                {
                    _ins.PrintError("NpcSpawn plugin doesn`t exist! Please read the file ReadMe.txt. NPCs will not spawn!");
                    _ins.NextTick(() => Interface.Oxide.UnloadPlugin(_ins.Name));
                    return false;
                }
                else
                    return true;
            }

            public static ScientistNPC SpawnScientistNpc(string npcPresetName, Vector3 position, float healthFraction, bool isStationary, bool isPassive = false)
            {
                NpcConfig npcConfig = GetNpcConfigByPresetName(npcPresetName);
                if (npcConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", npcPresetName);
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
                if (scientistNpc == null)
                    return null;
                
                EntityPresetInfo.Attach(scientistNpc, npcConfig.PresetName);
                AllNpc.Add(scientistNpc);
                return scientistNpc;
            }

            public static NpcConfig GetNpcConfigByDisplayName(string displayName)
            {
                return _ins._config.NpcConfigs.FirstOrDefault(x => x.DisplayName == displayName);
            }

            public static NpcConfig GetNpcConfigByPresetName(string npcPresetName)
            {
                return _ins._config.NpcConfigs.FirstOrDefault(x => x.PresetName == npcPresetName);
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
                    ["CheckVisionCone"] = true,
                    ["VisionCone"] = config.VisionCone,
                    ["HostileTargetsOnly"] = false,
                    ["DamageScale"] = config.DamageScale,
                    ["TurretDamageScale"] = config.TurretDamageScale,
                    ["AimConeScale"] = config.AimConeScale,
                    ["DisableRadio"] = config.DisableRadio,
                    ["CanRunAwayWater"] = true,
                    ["CanSleep"] = false,
                    ["SleepDistance"] = 100f,
                    ["Speed"] = isStationary ? 0 : config.Speed,
                    ["AreaMask"] = 1,
                    ["AgentTypeID"] = -1372625422,
                    ["HomePosition"] = string.Empty,
                    ["MemoryDuration"] = config.MemoryDuration,
                    ["States"] = isPassive ? new JArray() : isStationary ? new JArray { "IdleState", "CombatStationaryState" } : config.BeltItems.Any(x => x.ShortName == "rocket.launcher" || x.ShortName == "explosive.timed") ? new JArray { "RaidState", "RoamState", "ChaseState", "CombatState" } : new JArray { "RoamState", "ChaseState", "CombatState" },
                    ["IsRemoveCorpse"] = config.DeleteCorpse
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

            public static void ClearData(bool killNpc)
            {
                if (killNpc)
                    foreach (ScientistNPC scientistNpc in AllNpc)
                        if (scientistNpc.IsExists())
                            scientistNpc.Kill();

                AllNpc.Clear();
            }
        }

        private static class BuildManager
        {
            private static void UpdateMeshColliders(BaseEntity entity)
            {
                MeshCollider[] meshColliders = entity.GetComponentsInChildren<MeshCollider>();

                foreach (MeshCollider meshCollider in meshColliders)
                {
                    meshCollider.convex = true;
                }
            }

            public static BaseEntity SpawnChildEntity(BaseEntity parentEntity, string prefabName, LocationConfig locationConfig, ulong skinId, bool isDecor)
            {
                Vector3 localPosition = locationConfig.Position.ToVector3();
                Vector3 localRotation = locationConfig.Rotation.ToVector3();
                return SpawnChildEntity(parentEntity, prefabName, localPosition, localRotation, skinId, isDecor);
            }

            public static BaseEntity SpawnRegularEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0, bool enableSaving = false)
            {
                BaseEntity entity = CreateEntity(prefabName, position, rotation, skinId, enableSaving);
                entity.Spawn();
                return entity;
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

            public static BaseEntity SpawnChildEntity(BaseEntity parentEntity, string prefabName, Vector3 localPosition, Vector3 localRotation, ulong skinId = 0, bool isDecor = true, bool enableSaving = false)
            {
                BaseEntity entity = isDecor ? CreateDecorEntity(prefabName, parentEntity.transform.position, Quaternion.identity, skinId) : CreateEntity(prefabName, parentEntity.transform.position, Quaternion.identity, skinId, enableSaving);
                SetParent(parentEntity, entity, localPosition, localRotation);

                DestroyUnnecessaryComponents(entity);

                if (isDecor)
                    DestroyDecorComponents(entity);

                UpdateMeshColliders(entity);
                entity.Spawn();
                return entity;
            }

            public static void UpdateEntityMaxHealth(BaseCombatEntity baseCombatEntity, float maxHealth)
            {
                baseCombatEntity.startHealth = maxHealth;
                baseCombatEntity.InitializeHealth(maxHealth, maxHealth);
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

            public static void SetParent(BaseEntity parentEntity, BaseEntity childEntity, Vector3 localPosition, Vector3 localRotation)
            {
                childEntity.SetParent(parentEntity, true);
                childEntity.transform.localPosition = localPosition;
                childEntity.transform.localEulerAngles = localRotation;
            }

            private static void DestroyDecorComponents(BaseEntity entity)
            {
                Component[] components = entity.GetComponentsInChildren<Component>();

                foreach (Component component in components)
                {
                    EntityCollisionMessage entityCollisionMessage = component as EntityCollisionMessage;

                    if (entityCollisionMessage != null || (component != null && component.name != entity.PrefabName))
                    {
                        Transform transform = component as Transform;
                        if (transform != null)
                            continue;

                        Collider collider = component as Collider;
                        if (collider != null && collider is MeshCollider == false)
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

                if (entity is BradleyAPC == false)
                    DestroyEntityComponent<Rigidbody>(entity);
            }

            public static void DestroyEntityComponent<TYpeForDestroy>(BaseEntity entity)
            {
                if (entity == null)
                    return;

                TYpeForDestroy component = entity.GetComponent<TYpeForDestroy>();
                if (component != null)
                    UnityEngine.Object.DestroyImmediate(component as UnityEngine.Object);
            }

            public static void DestroyEntityComponents<TYpeForDestroy>(BaseEntity entity)
            {
                if (entity == null)
                    return;

                TYpeForDestroy[] components = entity.GetComponentsInChildren<TYpeForDestroy>();

                foreach (TYpeForDestroy component in components)
                {
                    if (component != null)
                        UnityEngine.Object.DestroyImmediate(component as UnityEngine.Object);
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

        private static class PositionDefiner
        {
            public static Vector3 GetGlobalPosition(Transform parentTransform, Vector3 position)
            {
                return parentTransform.transform.TransformPoint(position);
            }

            public static Quaternion GetGlobalRotation(Transform parentTransform, Vector3 rotation)
            {
                return parentTransform.rotation * Quaternion.Euler(rotation);
            }

            public static Vector3 GetLocalPosition(Transform parentTransform, Vector3 globalPosition)
            {
                return parentTransform.transform.InverseTransformPoint(globalPosition);
            }

            public static Vector3 GetGroundPositionInPoint(Vector3 position)
            {
                position.y = 100;

                if (Physics.Raycast(position, Vector3.down, out RaycastHit raycastHit, 500, 1 << 16 | 1 << 23))
                    position.y = raycastHit.point.y;

                return position;
            }

            public static bool GetNavmeshInPoint(Vector3 position, float radius, out NavMeshHit navMeshHit)
            {
                return NavMesh.SamplePosition(position, out navMeshHit, radius, 1);
            }
        }

        private static class GuiManager
        {
            private static bool _isLoadingImageFailed;
            private const float TabWidth = 109;
            private const float TabHeight = 25;
            private static readonly ImageInfo TabImageInfo = new ImageInfo("Tab_Adem");
            private static readonly List<ImageInfo> IconImageInfos = new List<ImageInfo>
            {
                new ImageInfo("Clock_Adem"),
                new ImageInfo("Crates_Adem"),
                new ImageInfo("Soldiers_Adem"),
            };

            public static void LoadImages()
            {
                ServerMgr.Instance.StartCoroutine(LoadImagesCoroutine());
            }

            private static IEnumerator LoadImagesCoroutine()
            {
                yield return LoadTabCoroutine();

                if (!_isLoadingImageFailed)
                    yield return LoadIconsCoroutine();
            }

            private static IEnumerator LoadTabCoroutine()
            {
                string url = "file://" + Interface.Oxide.DataDirectory + Path.DirectorySeparatorChar + "Images/" + TabImageInfo.ImageName + ".png";
                using UnityWebRequest unityWebRequest = UnityWebRequestTexture.GetTexture(url);
                yield return unityWebRequest.SendWebRequest();

                if (unityWebRequest.result != UnityWebRequest.Result.Success)
                {
                    OnImageSaveFailed();
                    _isLoadingImageFailed = true;
                }

                Texture2D texture = DownloadHandlerTexture.GetContent(unityWebRequest);
                uint imageId = FileStorage.server.Store(texture.EncodeToPNG(), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                TabImageInfo.ImageId = imageId.ToString();
                UnityEngine.Object.DestroyImmediate(texture);
            }

            private static IEnumerator LoadIconsCoroutine()
            {
                foreach (ImageInfo imageInfo in IconImageInfos)
                {
                    string url = "file://" + Interface.Oxide.DataDirectory + Path.DirectorySeparatorChar + "Images/" + imageInfo.ImageName + ".png";
                    using UnityWebRequest unityWebRequest = UnityWebRequestTexture.GetTexture(url);
                    yield return unityWebRequest.SendWebRequest();

                    if (unityWebRequest.result != UnityWebRequest.Result.Success)
                    {
                        OnImageSaveFailed();
                        break;
                    }

                    Texture2D texture = DownloadHandlerTexture.GetContent(unityWebRequest);
                    uint imageId = FileStorage.server.Store(texture.EncodeToPNG(), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                    imageInfo.ImageId = imageId.ToString();
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            private static void OnImageSaveFailed()
            {
                NotifyManager.PrintError(null, $"Move the contents of the data folder from the archive you downloaded from the website into the oxide/data folder on your server!");
                Interface.Oxide.UnloadPlugin(_ins.Name);
            }

            public static void CreateGui(BasePlayer player, params string[] args)
            {
                if (!_ins._config.GUIConfig.IsEnable)
                    return;

                CuiHelper.DestroyUi(player, "Tabs_Adem");
                CuiElementContainer container = new CuiElementContainer();
                float halfWidth = TabWidth / 2 + TabWidth / 2 * (IconImageInfos.Count - 1);

                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"{-halfWidth} {_ins._config.GUIConfig.OffsetMinY}", OffsetMax = $"{halfWidth} {_ins._config.GUIConfig.OffsetMinY + TabHeight}" },
                    CursorEnabled = false,
                }, "Under", "Tabs_Adem");

                float xMin = 0;

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    DrawTab(ref container, i, arg, xMin);
                    xMin += TabWidth;
                }

                CuiHelper.AddUi(player, container);
            }

            private static void DrawTab(ref CuiElementContainer container, int index, string text, float xMin)
            {
                ImageInfo imageInfo = IconImageInfos[index];

                container.Add(new CuiElement
                {
                    Name = $"Tab_{index}_Adem",
                    Parent = "Tabs_Adem",
                    Components =
                    {
                        new CuiRawImageComponent { Png = TabImageInfo.ImageId },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{xMin} 0", OffsetMax = $"{xMin + TabWidth} {TabHeight}" }
                    }
                });
                container.Add(new CuiElement
                {
                    Parent = $"Tab_{index}_Adem",
                    Components =
                    {
                        new CuiRawImageComponent { Png = imageInfo.ImageId },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "9 5", OffsetMax = "23 19" }
                    }
                });
                container.Add(new CuiElement
                {
                    Parent = $"Tab_{index}_Adem",
                    Components =
                    {
                        new CuiTextComponent() { Color = "1 1 1 1", Text = text, Align = TextAnchor.MiddleCenter, FontSize = 10, Font = "robotocondensed-bold.ttf" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "23 5", OffsetMax = $"{TabWidth - 9} 19" }
                    }
                });
            }

            public static void DestroyAllGui()
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                    if (player != null)
                        DestroyGui(player);
            }

            public static void DestroyGui(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, "Tabs_Adem");
            }

            private class ImageInfo
            {
                public readonly string ImageName;
                public string ImageId;

                public ImageInfo(string imageName)
                {
                    this.ImageName = imageName;
                }
            }
        }

        private static class NotifyManager
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

                TrySendDiscordMessage(langKey, args);
            }

            public static void SendMessageToPlayer(BasePlayer player, string langKey, params object[] args)
            {
                object[] argsClone = new object[args.Length];

                for (int i = 0; i < args.Length; i++)
                    argsClone[i] = args[i];

                for (int i = 0; i < argsClone.Length; i++)
                    if (argsClone[i] is int)
                        argsClone[i] = GetTimeMessage(player.UserIDString, (int)argsClone[i]);

                string playerMessage = GetMessage(langKey, player.UserIDString, argsClone);

                if (_ins._config.NotifyConfig.IsChatEnable)
                    _ins.PrintToChat(player, playerMessage);

                if (_ins._config.NotifyConfig.GameTipConfig.IsEnabled)
                    player.SendConsoleCommand("gametip.showtoast", _ins._config.NotifyConfig.GameTipConfig.Style, ClearColorAndSize(playerMessage), string.Empty);

                if (_ins._config.SupportedPluginsConfig.GUIAnnouncementsConfig.IsEnabled && _ins.plugins.Exists("guiAnnouncementsConfig"))
                    _ins.GUIAnnouncements?.Call("CreateAnnouncement", ClearColorAndSize(playerMessage), _ins._config.SupportedPluginsConfig.GUIAnnouncementsConfig.BannerColor, _ins._config.SupportedPluginsConfig.GUIAnnouncementsConfig.TextColor, player, _ins._config.SupportedPluginsConfig.GUIAnnouncementsConfig.APIAdjustVPosition);

                if (_ins._config.SupportedPluginsConfig.NotifyPluginConfig.IsEnabled && _ins.plugins.Exists("Notify"))
                    _ins.Notify?.Call("SendNotify", player, _ins._config.SupportedPluginsConfig.NotifyPluginConfig.Type, ClearColorAndSize(playerMessage));
            }

            public static string GetTimeMessage(string userIDString, int seconds)
            {
                string message = "";

                TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
                if (timeSpan.Hours > 0) message += $" {timeSpan.Hours} {GetMessage("Hours", userIDString)}";
                if (timeSpan.Minutes > 0) message += $" {timeSpan.Minutes} {GetMessage("Minutes", userIDString)}";
                if (message == "") message += $" {timeSpan.Seconds} {GetMessage("Seconds", userIDString)}";

                return message;
            }

            private static void TrySendDiscordMessage(string langKey, params object[] args)
            {
                if (CanSendDiscordMessage(langKey))
                {
                    for (int i = 0; i < args.Length; i++)
                        if (args[i] is int)
                            args[i] = GetTimeMessage(null, (int)args[i]);

                    object fields = new[] { new { name = _ins.Title, value = ClearColorAndSize(GetMessage(langKey, null, args)), inline = false } };
                    _ins.DiscordMessages?.Call("API_SendFancyMessage", _ins._config.SupportedPluginsConfig.DiscordMessagesConfig.WebhookUrl, "", _ins._config.SupportedPluginsConfig.DiscordMessagesConfig.EmbedColor, JsonConvert.SerializeObject(fields), null, _ins);
                }
            }

            private static bool CanSendDiscordMessage(string langKey)
            {
                return _ins._config.SupportedPluginsConfig.DiscordMessagesConfig.Keys.Contains(langKey) && _ins._config.SupportedPluginsConfig.DiscordMessagesConfig.IsEnabled && !string.IsNullOrEmpty(_ins._config.SupportedPluginsConfig.DiscordMessagesConfig.WebhookUrl) && _ins._config.SupportedPluginsConfig.DiscordMessagesConfig.WebhookUrl != "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            }
        }

        private static class EconomyManager
        {
            private static readonly Dictionary<ulong, double> PlayersBalance = new Dictionary<ulong, double>();

            public static void AddBalance(ulong playerId, double balance)
            {
                if (balance == 0 || playerId == 0)
                    return;

                if (!PlayersBalance.TryAdd(playerId, balance))
                    PlayersBalance[playerId] += balance;
            }

            public static void OnEventEnd()
            {
                DefineEventWinner();

                if (!_ins._config.SupportedPluginsConfig.EconomicsConfig.Enable || PlayersBalance.Count == 0)
                {
                    PlayersBalance.Clear();
                    return;
                }

                SendBalanceToPlayers();
                PlayersBalance.Clear();
            }

            private static void DefineEventWinner()
            {
                var winnerPair = PlayersBalance.Max(x => (float)x.Value);

                if (winnerPair.Value > 0)
                    Interface.CallHook($"On{_ins.Name}EventWin", winnerPair.Key);

                if (winnerPair.Value >= _ins._config.SupportedPluginsConfig.EconomicsConfig.MinCommandPoint)
                    foreach (string command in _ins._config.SupportedPluginsConfig.EconomicsConfig.Commands)
                        _ins.Server.Command(command.Replace("{steamid}", $"{winnerPair.Key}"));
            }

            private static void SendBalanceToPlayers()
            {
                foreach (KeyValuePair<ulong, double> pair in PlayersBalance)
                    SendBalanceToPlayer(pair.Key, pair.Value);
            }

            private static void SendBalanceToPlayer(ulong userID, double amount)
            {
                if (amount < _ins._config.SupportedPluginsConfig.EconomicsConfig.MinEconomyPiont)
                    return;

                int intAmount = Convert.ToInt32(amount);

                if (intAmount <= 0)
                    return;

                if (_ins._config.SupportedPluginsConfig.EconomicsConfig.Plugins.Contains("Economics") && _ins.plugins.Exists("Economics"))
                    _ins.Economics.Call("Deposit", userID.ToString(), amount);

                if (_ins._config.SupportedPluginsConfig.EconomicsConfig.Plugins.Contains("Server Rewards") && _ins.plugins.Exists("ServerRewards"))
                    _ins.ServerRewards.Call("AddPoints", userID, intAmount);

                if (_ins._config.SupportedPluginsConfig.EconomicsConfig.Plugins.Contains("IQEconomic") && _ins.plugins.Exists("IQEconomic"))
                    _ins.IQEconomic.Call("API_SET_BALANCE", userID, intAmount);

                BasePlayer player = BasePlayer.FindByID(userID);
                if (player != null)
                    NotifyManager.SendMessageToPlayer(player, "SendEconomy", _ins._config.Prefix, amount);
            }
        }

        private class PathRecorder : FacepunchBehaviour
        {
            private static readonly HashSet<PathRecorder> CustomRouteSavers = new HashSet<PathRecorder>();
            private BasePlayer _player;
            private RidableHorse _horse;
            private readonly List<Vector3> _positions = new List<Vector3>();

            private static PathRecorder GetCustomRouteSavingByUserId(ulong userId)
            {
                return CustomRouteSavers.FirstOrDefault(x => x != null && x._horse.IsExists() && x._player != null && x._player.userID == userId);
            }

            public static void StartRecordingRoute(BasePlayer player)
            {
                if (GetCustomRouteSavingByUserId(player.userID) != null)
                    return;

                RidableHorse horse = BuildManager.SpawnRegularEntity("assets/content/vehicles/horse/ridablehorse.prefab", player.transform.position, player.eyes.GetLookRotation()) as RidableHorse;
                horse.AttemptMount(player);
                PathRecorder customRouteSaving = horse.gameObject.AddComponent<PathRecorder>();
                customRouteSaving.Init(player, horse);
                CustomRouteSavers.Add(customRouteSaving);
            }

            public static void TrySaveRoute(ulong userId, string pathName)
            {
                PathRecorder customRouteSaving = GetCustomRouteSavingByUserId(userId);

                if (customRouteSaving != null)
                    customRouteSaving.SavePath(pathName);
            }

            public static void TryCancelRoute(ulong userId)
            {
                PathRecorder customRouteSaving = GetCustomRouteSavingByUserId(userId);

                if (customRouteSaving != null)
                    customRouteSaving.KillHorse();
            }

            private void Init(BasePlayer player, RidableHorse horse)
            {
                this._player = player;
                this._horse = horse;

                TryAddFindPositionOrDestroy();
            }

            private void FixedUpdate()
            {
                if (_player == null || !_player.isMounted)
                {
                    KillHorse();
                    return;
                }

                Vector3 lastPosition = _positions.Last();
                float distance = Vector3.Distance(lastPosition, _horse.transform.position);

                if (distance > 10)
                    TryAddFindPositionOrDestroy();
            }

            private void TryAddFindPositionOrDestroy()
            {
                Vector3 newPosition = _horse.transform.position;

                if (!PositionDefiner.GetNavmeshInPoint(newPosition, 2, out NavMeshHit navMeshHit))
                {
                    NotifyManager.PrintError(_player, "NavMesh_Exeption");
                    KillHorse();
                }
                else
                    _positions.Add(navMeshHit.position);
            }

            private void SavePath(string pathName)
            {
                float pathLength = GetPathLength();
                if (pathLength < _ins._config.PathConfig.MinRoadLength)
                {
                    NotifyManager.SendMessageToPlayer(_player, "CustomRouteTooShort", _ins._config.Prefix);
                    return;
                }
                List<string> path = new List<string>();

                foreach (Vector3 point in _positions)
                    path.Add(point.ToString());

                CustomRouteData customRouteData = new CustomRouteData
                {
                    Points = path
                };

                Interface.Oxide.DataFileSystem.WriteObject($"{_ins.Name}/Custom routes/{pathName}", customRouteData);
                NotifyManager.SendMessageToPlayer(_player, "CustomRouteSuccess", _ins._config.Prefix);
                _ins._config.PathConfig.CustomPathConfig.CustomRoutesPresets.Add(pathName);
                KillHorse();
                _ins.SaveConfig();
            }

            private float GetPathLength()
            {
                float length = 0;

                for (int i = 0; i < _positions.Count - 1; i++)
                {
                    Vector3 thisPoint = _positions[i];
                    Vector3 nextPoint = _positions[i + 1];
                    float distance = Vector3.Distance(thisPoint, nextPoint);
                    length += distance;
                }

                return length;
            }

            private void KillHorse()
            {
                if (_horse.IsExists())
                    _horse.Kill();
            }
        }
        #endregion Classes
        
        #region Loot
        private readonly Dictionary<string, PrefabLootInfo> _prefabLootTables = new Dictionary<string, PrefabLootInfo>();
        
        private static class LootController
        {
            public static LootTableConfig GetDefaultLootTable()
            {
                return new LootTableConfig
                {
                    IsAlphaLoot = true,
                    AlphaLootPreset = string.Empty,
                    IsLoottablePlugin = true,
                    LoottablePreset = string.Empty,
                    IsCustomLootPlugin = true,
                    CustomLootPreset = string.Empty,
                    ClearDefaultLoot = false,
                    
                    ItemsTable = new ItemsLootTableConfig
                    {
                        IsEnabled = false,
                        DisableMinMax = false,
                        MinItemsAmount = 1,
                        MaxItemsAmount =1,
                        Items = new List<LootItemConfig>
                        {
                            new LootItemConfig
                            {
                                Shortname = "scrap",
                                Skin = 0,
                                Chance = 100,
                                MinAmount = 100,
                                MaxAmount = 200,
                                DisplayName = string.Empty,
                                OwnerName = string.Empty,
                                IsBlueprint = false,
                                Genomes = new List<string>()
                            }
                        }
                    },
                    PrefabsTable = new PrefabsLootTableConfig
                    {
                        IsEnabled = false,
                        MinPrefabsAmount = 1,
                        MaxPrefabsAmount = 1,
                        Prefabs = new List<LootPrefabConfig>
                        {
                            new LootPrefabConfig
                            {
                                PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                Chance = 100,
                                MinAmount = 1,
                                MaxAmount = 1
                            }
                        }
                    }
                };
            }

            public static HashSet<LootTableConfig> GetAllLootTables()
            {
                HashSet<LootTableConfig> result = new HashSet<LootTableConfig>();
                
                foreach (NpcConfig npcConfig in _ins._config.NpcConfigs)
                    result.Add(npcConfig.LootTableConfig);
                
                foreach (CrateConfig crateConfig in _ins._config.CrateConfigs)
                    result.Add(crateConfig.LootTableConfig);
                
                foreach (BradleyConfig bradleyConfig in _ins._config.BradleyConfigs)
                    result.Add(bradleyConfig.LootTableConfig);
                
                foreach (HeliConfig heliConfig in _ins._config.HeliConfigs)
                    result.Add(heliConfig.LootTableConfig);
                
                return result;
            }

            public static void UpdateNpcCorpse(NPCPlayerCorpse corpse, NpcConfig npcConfig)
            {
                if (corpse.containers.IsNullOrEmpty())
                    return;
                
                EntityPresetInfo.Attach(corpse, npcConfig.PresetName);
                
                if (IsNoAdditionLoot(npcConfig.LootTableConfig))
                    return;
                
                ItemContainer container = corpse.containers[0];
                if (container == null)
                    return;
                
                corpse.Invoke(() =>
                {
                    if (!corpse.IsExists())
                        return;
                    
                    FillContainer(container, npcConfig.LootTableConfig);
                }, 0.1f);
            }

            public static void UpdateCrate(BaseEntity crateEntity, CrateConfig crateConfig)
            {
                if (crateEntity == null)
                    return;
                
                EntityPresetInfo.Attach(crateEntity, crateConfig.PresetName);

                if (IsNoAdditionLoot(crateConfig.LootTableConfig))
                    return;
                
                Fridge fridge = crateEntity as Fridge;
                if (fridge != null)
                {
                    fridge.OnlyAcceptCategory = ItemCategory.All;
                    FillContainer(fridge.inventory, crateConfig.LootTableConfig);
                    return;
                }
                
                LootContainer lootContainer = crateEntity as LootContainer;
                if (lootContainer != null)
                {
                    lootContainer.Invoke(() =>
                    {
                        if (lootContainer.inventory != null)
                            FillContainer(lootContainer.inventory, crateConfig.LootTableConfig);
                    }, 2f);
                    
                    return;
                }
                
                DroppedItemContainer droppedItemContainer = crateEntity as DroppedItemContainer;
                if (droppedItemContainer != null)
                {
                    FillContainer(droppedItemContainer.inventory, crateConfig.LootTableConfig);
                    return;
                }
                
                StorageContainer storageContainer = crateEntity as StorageContainer;
                if (storageContainer != null)
                {
                    FillContainer(storageContainer.inventory, crateConfig.LootTableConfig);
                }
            }

            public static void UpdateHeliOrBradleyCrate(LockedByEntCrate lockedByEntCrate, LootTableConfig lootTableConfig, string presetName)
            {
                EntityPresetInfo.Attach(lockedByEntCrate, presetName);
                
                if (IsNoAdditionLoot(lootTableConfig))
                    return;
                
                lockedByEntCrate.Invoke(() =>
                {
                    if (lockedByEntCrate.inventory != null)
                        FillContainer(lockedByEntCrate.inventory, lootTableConfig);
                }, 2f);
            }
            
            private static bool IsNoAdditionLoot(LootTableConfig lootTableConfig)
            {
                return !lootTableConfig.ClearDefaultLoot &&
                       !lootTableConfig.ItemsTable.IsEnabled &&
                       !lootTableConfig.PrefabsTable.IsEnabled &&
                       string.IsNullOrEmpty(lootTableConfig.AlphaLootPreset) &&
                       string.IsNullOrEmpty(lootTableConfig.LoottablePreset) &&
                       string.IsNullOrEmpty(lootTableConfig.CustomLootPreset);
            }

            private static void FillContainer(ItemContainer container, LootTableConfig lootTable)
            {
                if (lootTable.ClearDefaultLoot)
                    ClearContainer(container);
                
                if (lootTable.ItemsTable.IsEnabled)
                {
                    if (lootTable.ItemsTable.DisableMinMax)
                    {
                        ItemListController.FillContainerWithoutMinMax(container, lootTable.ItemsTable.Items);
                    }
                    else if (lootTable.ItemsTable.Items.Count > 0)
                    {
                        int itemsAmount = Random.Range(lootTable.ItemsTable.MinItemsAmount, lootTable.ItemsTable.MaxItemsAmount + 1);
                        HashSet<LootItemConfig> itemsForSpawn = GetElementsForSpawn(lootTable.ItemsTable.Items, itemsAmount);

                        if (itemsForSpawn.Count > 0)
                            ItemListController.FillContainer(container, itemsForSpawn);
                    }
                }
                
                if (lootTable.PrefabsTable.IsEnabled)
                {
                    int prefabsAmount = Random.Range(lootTable.PrefabsTable.MinPrefabsAmount, lootTable.PrefabsTable.MaxPrefabsAmount + 1);
                    HashSet<LootPrefabConfig> prefabsForSpawn = GetElementsForSpawn(lootTable.PrefabsTable.Prefabs, prefabsAmount);
                    PrefabController.FillContainer(container, prefabsForSpawn);
                }
                
                if (!string.IsNullOrEmpty(lootTable.AlphaLootPreset))
                {
                    if (_ins.plugins.Exists("AlphaLoot") && (bool)_ins.AlphaLoot.Call("ProfileExists", lootTable.AlphaLootPreset))
                    {
                        _ins.AlphaLoot.Call("PopulateLoot", container, lootTable.AlphaLootPreset);
                    }
                }

                if (!string.IsNullOrEmpty(lootTable.CustomLootPreset))
                {
                    if (!_ins.plugins.Exists("CustomLoot"))
                        return;

                    List<Item> items = _ins.CustomLoot?.Call<List<Item>>("MakeLoot", lootTable.CustomLootPreset);
                    if (items != null)
                        foreach (Item item in items)
                            if (item != null && !item.MoveToContainer(container))
                                item.Remove();
                }

                if (!string.IsNullOrEmpty(lootTable.LoottablePreset))
                {
                    if (!_ins.plugins.Exists("Loottable"))
                        return;

                    List<Item> items = _ins.Loottable?.Call<List<Item>>("MakeLoot", lootTable.LoottablePreset);
                    if (items != null)
                    {
                        foreach (Item item in items)
                            if (item != null && !item.MoveToContainer(container))
                                item.Remove();

                        Pool.FreeUnmanaged(ref items);
                    }
                }
            }
            
            private static void ClearContainer(ItemContainer container)
            {
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    Item item = container.itemList[i];
                    item.RemoveFromContainer();
                    item.Remove();
                }
            }
            
            private static HashSet<T> GetElementsForSpawn<T>(List<T> elements, int targetAmount) where T : LootElementChanceConfig
            {
                HashSet<T> result = new HashSet<T>();
                if (elements.Count == 0 || targetAmount <= 0)
                    return result;

                HashSet<int> includedIndexes = new HashSet<int>();
                
                for (int i = 0; i < elements.Count; i++)
                {
                    T elementConfig = elements[i];
                    
                    if (elementConfig.Chance >= 100f)
                    {
                        includedIndexes.Add(i);
                        result.Add(elementConfig);
                        
                        if (result.Count >= targetAmount)
                            break;
                    }
                }

                if (result.Count >= targetAmount)
                    return result;
                    
                int counter = 200;
                while (result.Count < targetAmount && counter-- > 0)
                {
                    float sumChance = 0f;
                        
                    for (int i = 0; i < elements.Count; i++)
                    {
                        if (includedIndexes.Contains(i))
                            continue;

                        sumChance += elements[i].Chance;
                    }
                        
                    if (sumChance <= 0f) 
                        break;

                    float random = Random.Range(0f, sumChance);
                    for (int i = 0; i < elements.Count; i++)
                    {
                        if (includedIndexes.Contains(i))
                            continue;

                        T lootElement = elements[i];
                        random -= lootElement.Chance;
                        if (random <= 0f)
                        {
                            includedIndexes.Add(i);
                            result.Add(lootElement);
                            break;
                        }
                    }
                }

                return result;
            }
        }
        
        private static class ItemListController
        {
            public static void FillContainer(ItemContainer container, HashSet<LootItemConfig> items)
            {
                int itemsCount = container.itemList.Count + items.Count;
                if (container.capacity < itemsCount)
                    container.capacity = itemsCount;
                
                foreach (LootItemConfig lootItemConfig in items)
                {
                    Item item = CreateItem(lootItemConfig);
                    if (item == null)
                        continue;

                    if (!item.MoveToContainer(container))
                        item.Remove();
                }
            }

            public static void FillContainerWithoutMinMax(ItemContainer container, List<LootItemConfig> items)
            {
                foreach (LootItemConfig itemConfig in items)
                {
                    float roll = Random.Range(0f, 100f);
                    if (roll > itemConfig.Chance)
                        continue;

                    Item item = CreateItem(itemConfig);
                    if (item == null)
                        continue;

                    if (!item.MoveToContainer(container))
                        item.Remove();
                }
            }

            private static Item CreateItem(LootItemConfig lootItemConfig)
            {
                int amount = Random.Range(lootItemConfig.MinAmount, lootItemConfig.MaxAmount + 1);
                if (amount == 0)
                    return null;

                Item item;

                if (lootItemConfig.IsBlueprint)
                {
                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(lootItemConfig.Shortname);
                    if (itemDefinition == null)
                        return null;
                    
                    item = ItemManager.CreateByName("blueprintbase");
                    item.blueprintTarget = itemDefinition.itemid;
                }
                else
                {
                    item = ItemManager.CreateByName(lootItemConfig.Shortname, amount, lootItemConfig.Skin);
                }

                if (item == null)
                {
                    _ins.PrintWarning($"Failed to create item! ({lootItemConfig.Shortname})");
                    return null;
                }

                if (!string.IsNullOrEmpty(lootItemConfig.DisplayName))
                    item.name = lootItemConfig.DisplayName;

                if (lootItemConfig.Genomes != null && lootItemConfig.Genomes.Count > 0)
                {
                    string genome = lootItemConfig.Genomes.GetRandom();
                    UpdateGenome(item, genome);
                }

                return item;
            }

            private static void UpdateGenome(Item item, string genome)
            {
                if (genome.Length != 6)
                    return;

                genome = genome.ToLower();
                GrowableGenes growableGenes = new GrowableGenes();

                for (int i = 0; i < 6 && i < genome.Length; ++i)
                {

                    GrowableGenetics.GeneType geneType = GrowableGenetics.GeneType.Empty;

                    switch (genome[i])
                    {
                        case 'g':
                            geneType = GrowableGenetics.GeneType.GrowthSpeed;
                            break;
                        case 'y':
                            geneType = GrowableGenetics.GeneType.Yield;
                            break;
                        case 'h':
                            geneType = GrowableGenetics.GeneType.Hardiness;
                            break;
                        case 'w':
                            geneType = GrowableGenetics.GeneType.WaterRequirement;
                            break;
                    }

                    growableGenes.Genes[i].Set(geneType, true);
                }

                GrowableGeneEncoding.EncodeGenesToItem(GrowableGeneEncoding.EncodeGenesToInt(growableGenes), item);
            }
        }
        
        private static class PrefabController
        {
            public static void CachePrefabs()
            {
                HashSet<LootTableConfig> lootTableConfigs = LootController.GetAllLootTables();
                
                foreach (LootTableConfig lootTableConfig in lootTableConfigs)
                {
                    if (!lootTableConfig.PrefabsTable.IsEnabled)
                        continue;
        
                    foreach (LootPrefabConfig lootPrefabConfig in lootTableConfig.PrefabsTable.Prefabs)
                        CachePrefab(lootPrefabConfig);
                }
            }

            private static void CachePrefab(LootPrefabConfig lootPrefabConfig)
            {
                if (_ins._prefabLootTables.ContainsKey(lootPrefabConfig.PrefabName))
                    return;
        
                GameObject gameObject = GameManager.server.FindPrefab(lootPrefabConfig.PrefabName);
                if (gameObject == null)
                    return;
        
                LootContainer lootContainer = gameObject.GetComponent<LootContainer>();
                if (lootContainer != null)
                {
                    SavePrefabLootInfo(lootPrefabConfig.PrefabName, lootContainer.LootSpawnSlots, lootContainer.scrapAmount, lootContainer.lootDefinition, lootContainer.maxDefinitionsToSpawn);
                    return;
                }
        
                global::HumanNPC humanNpc = gameObject.GetComponent<global::HumanNPC>();
                if (humanNpc != null)
                {
                    SavePrefabLootInfo(lootPrefabConfig.PrefabName, humanNpc.LootSpawnSlots);
                    return;
                }
        
                global::ScarecrowNPC scarecrowNpc = gameObject.GetComponent<global::ScarecrowNPC>();
                if (scarecrowNpc != null)
                {
                    SavePrefabLootInfo(lootPrefabConfig.PrefabName, scarecrowNpc.LootSpawnSlots);
                }
            }
        
            private static void SavePrefabLootInfo(string prefabName, LootContainer.LootSpawnSlot[] lootSpawnSlot, int scrapAmount = 0, LootSpawn lootDefinition = null, int maxDefinitionsToSpawn = 0)
            {
                PrefabLootInfo prefabLootInfo = new PrefabLootInfo
                {
                    LootSpawnSlots = lootSpawnSlot,
                    LootDefinition = lootDefinition,
                    MaxDefinitionsToSpawn = maxDefinitionsToSpawn,
                    ScrapAmount = scrapAmount
                };
        
                _ins._prefabLootTables.TryAdd(prefabName, prefabLootInfo);
            }
        
            public static void FillContainer(ItemContainer container, HashSet<LootPrefabConfig> prefabs)
            {
                foreach (LootPrefabConfig lootPrefabConfig in prefabs)
                    FillContainer(container, lootPrefabConfig);
            }
        
            private static void FillContainer(ItemContainer container, LootPrefabConfig prefab)
            {
                if (!_ins._prefabLootTables.TryGetValue(prefab.PrefabName, out PrefabLootInfo prefabLootInfo))
                    return;
        
                int lootScale = Random.Range(prefab.MinAmount, prefab.MaxAmount + 1);
        
                for (int i = 0; i < lootScale; i++)
                {
                    if (prefabLootInfo.LootSpawnSlots != null)
                    {
                        foreach (LootContainer.LootSpawnSlot lootSpawnSlot in prefabLootInfo.LootSpawnSlots)
                        {
                            if (lootSpawnSlot.eras == null || lootSpawnSlot.eras.Length == 0 || Array.IndexOf(lootSpawnSlot.eras, ConVar.Server.Era) != -1)
                            {
                                for (int j = 0; j < lootSpawnSlot.numberToSpawn; ++j)
                                {
                                    if (Random.Range(0f, 1f) <= lootSpawnSlot.probability)
                                    {
                                        lootSpawnSlot.definition.SpawnIntoContainer(container);
                                    }
                                }
                            }
                        }
                    }
        
                    if (prefabLootInfo.LootDefinition != null)
                        for (int j = 0; j < prefabLootInfo.MaxDefinitionsToSpawn; ++j)
                            prefabLootInfo.LootDefinition.SpawnIntoContainer(container);
        
                    if (prefabLootInfo.ScrapAmount > 0)
                    {
                        Item item = ItemManager.CreateByName("scrap", prefabLootInfo.ScrapAmount);
                        if (!item.MoveToContainer(container))
                            item.Remove();
                    }
                }
            }
        }
        
        private class PrefabLootInfo
        {
            public LootContainer.LootSpawnSlot[] LootSpawnSlots;
            public LootSpawn LootDefinition;
            public int MaxDefinitionsToSpawn;
            public int ScrapAmount;
        }

        private class EntityPresetInfo : FacepunchBehaviour
        {
            public string presetName;

            public static void Attach(BaseEntity entity, string presetName)
            {
                EntityPresetInfo entityPresetInfo = entity.gameObject.AddComponent<EntityPresetInfo>();
                entityPresetInfo.presetName = presetName;
            }
        }
        
        
        private static class LootMigrator
        {
            public static void MigrateFromLootManager()
            {
                foreach (CrateConfig crateConfig in _ins._config.CrateConfigs)
                {
                    crateConfig.LootTableConfig = GetLootTableConfig(crateConfig.LootManagerPreset);
                    
                    crateConfig.LootManagerPreset = null;
                    crateConfig.LootTableConfigOld = null;
                }

                foreach (NpcConfig npcConfig in _ins._config.NpcConfigs)
                {
                    npcConfig.LootTableConfig = GetLootTableConfig(npcConfig.LootManagerPreset);
                    
                    npcConfig.LootManagerPreset = null;
                    npcConfig.LootTableConfigOld = null;
                }
                
                foreach (BradleyConfig bradleyConfig in _ins._config.BradleyConfigs)
                {
                    bradleyConfig.LootTableConfig = GetLootTableConfig(bradleyConfig.LootManagerPreset);
                    
                    bradleyConfig.LootManagerPreset = null;
                }
                
                foreach (HeliConfig heliConfig in _ins._config.HeliConfigs)
                {
                    heliConfig.LootTableConfig = GetLootTableConfig(heliConfig.LootManagerPreset);
                    
                    heliConfig.LootManagerPreset = null;
                    heliConfig.BaseLootTableConfigOld = null;
                }
            }

            private static LootTableConfig GetLootTableConfig(string lootManagerPreset)
            {
                if (string.IsNullOrEmpty(lootManagerPreset))
                    return LootController.GetDefaultLootTable();

                LootTableData lootTableData = GetLootManagerTableData(lootManagerPreset);
                if (lootTableData == null)
                    return LootController.GetDefaultLootTable();
                
                return GetLootTable(lootTableData);
            }

            private static LootTableData GetLootManagerTableData(string lootManagerPreset)
            {
                string path = $"LootManager/LootTables/{lootManagerPreset}";
                LootTableData lootTableData = Interface.Oxide.DataFileSystem.ReadObject<LootTableData>(path);
                return lootTableData;
            }

            private static LootTableConfig GetLootTable(LootTableData lootTableData)
            {
                LootTableConfig lootTableConfig = new LootTableConfig
                {
                    IsAlphaLoot = lootTableData.IsAlphaLoot,
                    AlphaLootPreset = string.IsNullOrEmpty(lootTableData.AlphaLootPreset) ? string.Empty : lootTableData.AlphaLootPreset,
                    IsLoottablePlugin = lootTableData.IsLootTablePlugin,
                    LoottablePreset = string.IsNullOrEmpty(lootTableData.LootTablePluginLootPreset) ? string.Empty : lootTableData.LootTablePluginLootPreset,
                    IsCustomLootPlugin = lootTableData.IsCustomLoot,
                    CustomLootPreset = string.IsNullOrEmpty(lootTableData.CustomLootPreset) ? string.Empty : lootTableData.CustomLootPreset,
                    ClearDefaultLoot = lootTableData.ClearDefaultItems,
                    ItemsTable = new ItemsLootTableConfig
                    {
                        IsEnabled = lootTableData.UseItemList,
                        DisableMinMax = !lootTableData.UseMinMaxForItems,
                        MinItemsAmount = lootTableData.MinItemsAmount,
                        MaxItemsAmount = lootTableData.MaxItemsAmount,
                        Items = new List<LootItemConfig>()
                    },
                    PrefabsTable = new PrefabsLootTableConfig
                    {
                        IsEnabled = lootTableData.UsePrefabList,
                        MinPrefabsAmount = lootTableData.MinPrefabsAmount,
                        MaxPrefabsAmount = lootTableData.MaxPrefabsAmount,
                        Prefabs = new List<LootPrefabConfig>()
                    }
                };

                if (lootTableData.Items != null)
                {
                    foreach (ItemData itemData in lootTableData.Items)
                    {
                        if (string.IsNullOrEmpty(itemData.ShortName))
                            continue;
                        
                        LootItemConfig lootItemConfig = new LootItemConfig
                        {
                            Shortname = itemData.ShortName,
                            Skin = itemData.Skin,
                            Chance = itemData.Chance,
                            MinAmount = itemData.MinAmount,
                            MaxAmount = itemData.MaxAmount,
                            DisplayName = string.IsNullOrEmpty(itemData.CustomDisplayName) ? string.Empty : itemData.CustomDisplayName,
                            OwnerName = string.IsNullOrEmpty(itemData.OwnerDisplayName) ? string.Empty : itemData.OwnerDisplayName,
                            Genomes = new List<string>(),
                            IsBlueprint = itemData.IsBluePrint
                        };
                        
                        lootTableConfig.ItemsTable.Items.Add(lootItemConfig);
                    }
                }
                
                if (lootTableData.Prefabs != null)
                {
                    foreach (PrefabData prefabData in lootTableData.Prefabs)
                    {
                        if (string.IsNullOrEmpty(prefabData.PrefabName))
                            continue;

                        LootPrefabConfig lootPrefabConfig = new LootPrefabConfig
                        {
                            PrefabName = prefabData.PrefabName,
                            Chance = prefabData.Chance,
                            MinAmount = prefabData.MinAmount,
                            MaxAmount = prefabData.MaxAmount
                        };
                        
                        lootTableConfig.PrefabsTable.Prefabs.Add(lootPrefabConfig);
                    }
                }

                return lootTableConfig;
            }


            public static void MigrateFromOldLootTables() 
            {
                foreach (CrateConfig crateConfig in _ins._config.CrateConfigs)
                {
                    crateConfig.LootTableConfig = GetLootTableConfig(crateConfig.LootTableConfigOld);
                    
                    crateConfig.LootManagerPreset = null;
                    crateConfig.LootTableConfigOld = null;
                }

                foreach (NpcConfig npcConfig in _ins._config.NpcConfigs)
                {
                    npcConfig.LootTableConfig = GetLootTableConfig(npcConfig.LootTableConfigOld);
                    
                    npcConfig.LootManagerPreset = null;
                    npcConfig.LootTableConfigOld = null;
                }
                
                foreach (BradleyConfig bradleyConfig in _ins._config.BradleyConfigs)
                {
                    bradleyConfig.LootTableConfig = GetLootTableConfig(bradleyConfig.BaseLootTableConfigOld);
                    
                    bradleyConfig.BaseLootTableConfigOld = null;
                    bradleyConfig.LootManagerPreset = null;
                }
                
                foreach (HeliConfig heliConfig in _ins._config.HeliConfigs)
                {
                    heliConfig.LootTableConfig = GetLootTableConfig(heliConfig.BaseLootTableConfigOld);
                    
                    heliConfig.LootManagerPreset = null;
                    heliConfig.BaseLootTableConfigOld = null;
                }
            }
            
            private static LootTableConfig GetLootTableConfig(LootTableConfigOld lootTableConfigOld)
            {
                if (lootTableConfigOld == null || lootTableConfigOld.Items == null || lootTableConfigOld.PrefabConfigsOld == null)
                    return LootController.GetDefaultLootTable();
                
                LootTableConfig lootTableConfig = new LootTableConfig
                {
                    IsAlphaLoot = lootTableConfigOld.IsAlphaLoot,
                    AlphaLootPreset = string.IsNullOrEmpty(lootTableConfigOld.AlphaLootPresetName) ? string.Empty : lootTableConfigOld.AlphaLootPresetName,
                    IsLoottablePlugin = lootTableConfigOld.IsLootTablePlugin,
                    LoottablePreset = string.Empty,
                    IsCustomLootPlugin = lootTableConfigOld.IsCustomLoot,
                    CustomLootPreset = string.Empty,
                    ClearDefaultLoot = lootTableConfigOld.ClearDefaultItemList,
                    ItemsTable = new ItemsLootTableConfig
                    {
                        IsEnabled = lootTableConfigOld.IsRandomItemsEnable,
                        DisableMinMax = false,
                        MinItemsAmount = lootTableConfigOld.MinItemsAmount,
                        MaxItemsAmount = lootTableConfigOld.MaxItemsAmount,
                        Items = new List<LootItemConfig>()
                    },
                    PrefabsTable = new PrefabsLootTableConfig
                    {
                        IsEnabled = lootTableConfigOld.PrefabConfigsOld.IsEnable,
                        MinPrefabsAmount = 1,
                        MaxPrefabsAmount = 1,
                        Prefabs = new List<LootPrefabConfig>()
                    }
                };

                foreach (LootItemConfigOld itemConfigOld in lootTableConfigOld.Items)
                {
                    if (itemConfigOld == null || string.IsNullOrEmpty(itemConfigOld.Shortname))
                        continue;
                    
                    LootItemConfig lootItemConfig = new LootItemConfig
                    {
                        Shortname = itemConfigOld.Shortname,
                        Skin = itemConfigOld.Skin,
                        Chance = itemConfigOld.Chance,
                        MinAmount = itemConfigOld.MinAmount,
                        MaxAmount = itemConfigOld.MaxAmount,
                        DisplayName = string.IsNullOrEmpty(itemConfigOld.Name) ? string.Empty : itemConfigOld.Name,
                        OwnerName = string.Empty,
                        Genomes = new List<string>(),
                        IsBlueprint = itemConfigOld.IsBlueprint
                    };
                        
                    lootTableConfig.ItemsTable.Items.Add(lootItemConfig);
                }

                if (lootTableConfigOld.PrefabConfigsOld.Prefabs != null)
                {
                    foreach (PrefabConfigOld prefabConfigOld in lootTableConfigOld.PrefabConfigsOld.Prefabs)
                    {
                        if (string.IsNullOrEmpty(prefabConfigOld.PrefabName))
                            continue;

                        LootPrefabConfig lootPrefabConfig = new LootPrefabConfig
                        {
                            PrefabName = prefabConfigOld.PrefabName,
                            Chance = 100,
                            MinAmount = prefabConfigOld.MinLootScale,
                            MaxAmount = prefabConfigOld.MaxLootScale
                        };

                        lootTableConfig.PrefabsTable.Prefabs.Add(lootPrefabConfig);
                    }
                }

                return lootTableConfig;
            }
            
            private static LootTableConfig GetLootTableConfig(BaseLootTableConfigOld configOld)
            {
                LootTableConfig result = LootController.GetDefaultLootTable();
                
                result.ItemsTable.IsEnabled = configOld.IsRandomItemsEnable;
                result.ItemsTable.MinItemsAmount = configOld.MinItemsAmount;
                result.ItemsTable.MaxItemsAmount = configOld.MaxItemsAmount;
                if (configOld.Items != null)
                {
                    result.ItemsTable.Items.Clear();
                    
                    foreach (LootItemConfigOld itemConfigOld in configOld.Items)
                    {
                        if (itemConfigOld == null || string.IsNullOrEmpty(itemConfigOld.Shortname))
                            continue;
                    
                        LootItemConfig lootItemConfig = new LootItemConfig
                        {
                            Shortname = itemConfigOld.Shortname,
                            Skin = itemConfigOld.Skin,
                            Chance = itemConfigOld.Chance,
                            MinAmount = itemConfigOld.MinAmount,
                            MaxAmount = itemConfigOld.MaxAmount,
                            DisplayName = string.IsNullOrEmpty(itemConfigOld.Name) ? string.Empty : itemConfigOld.Name,
                            OwnerName = string.Empty,
                            Genomes = new List<string>(),
                            IsBlueprint = itemConfigOld.IsBlueprint
                        };
                        
                        result.ItemsTable.Items.Add(lootItemConfig);
                    }
                }

                if (configOld.PrefabConfigsOld != null)
                {
                    result.PrefabsTable.IsEnabled = configOld.PrefabConfigsOld.IsEnable;
                    result.PrefabsTable.Prefabs.Clear();
                    
                    if (configOld.PrefabConfigsOld.Prefabs != null)
                    {
                        foreach (PrefabConfigOld prefabConfigOld in configOld.PrefabConfigsOld.Prefabs)
                        {
                            if (string.IsNullOrEmpty(prefabConfigOld.PrefabName))
                                continue;

                            LootPrefabConfig lootPrefabConfig = new LootPrefabConfig
                            {
                                PrefabName = prefabConfigOld.PrefabName,
                                Chance = 100,
                                MinAmount = prefabConfigOld.MinLootScale,
                                MaxAmount = prefabConfigOld.MaxLootScale
                            };

                            result.PrefabsTable.Prefabs.Add(lootPrefabConfig);
                        }
                    }
                }

                return result;
            }
        }
        
        private class LootTableData
        {
            public bool ClearDefaultItems;
            public bool IsAlphaLoot;
            public string AlphaLootPreset;
            public bool IsCustomLoot;
            public string CustomLootPreset;
            public bool IsLootTablePlugin;
            public string LootTablePluginLootPreset;

            public bool UseItemList;
            public bool UseMinMaxForItems;
            public int MinItemsAmount;
            public int MaxItemsAmount;
            public List<ItemData> Items = new List<ItemData>();

            public bool UsePrefabList;
            public int MinPrefabsAmount;
            public int MaxPrefabsAmount;
            public List<PrefabData> Prefabs = new List<PrefabData>();
        }

        private class ItemData : LootElementChance
        {
            public string ShortName;
            public string CustomDisplayName;
            public string OwnerDisplayName;
            public ulong Skin;
            public bool IsBluePrint;
            public string Genomes;
        }

        private class PrefabData : LootElementChance
        {
            public string PrefabName;
            public string ShortPrefabName;
        }

        private class LootElementChance
        {
            public int MinAmount;
            public int MaxAmount;
            public float Chance;
        }
        #endregion Loot

        #region Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["EventActive_Exeption"] = "Ивент в данный момент активен, сначала завершите текущий ивент (<color=#ce3f27>/convoystop</color>)!",
                ["ConfigurationNotFound_Exeption"] = "<color=#ce3f27>Не удалось</color> найти конфигурацию ивента!",
                ["PresetNotFound_Exeption"] = "Пресет {0} <color=#ce3f27>не найден</color> в конфиге!",
                ["CustomRouteDescription"] = "{0} Для записи кастомного маршрута встаньте на землю и введите команду /convoypathstart. Двигайтесь по маршруту и введите команду /convoypathsave <routeName>. Для отмены маршрута используйте команду /convoypathcancel",

                ["SuccessfullyLaunched"] = "Ивент <color=#738d43>успешно</color> запущен!",
                ["PreStart"] = "{0} Через <color=#738d43>{1}</color>. начнется перевозка груза по автодороге!",
                ["EventStart"] = "{0} <color=#738d43>{1}</color> был обнаружен в квадрате <color=#738d43>{2}</color>",
                ["DamageDistance"] = "{0} Подойдите <color=#ce3f27>ближе</color>!",
                ["ConvoyAttacked"] = "{0} {1} <color=#ce3f27>напал</color> на конвой",
                ["CantLoot"] = "{0} Для того чтобы залутать конвой убейте <color=#ce3f27>всех нпс</color>!",
                ["Looted"] = "{0} <color=#738d43>{1}</color> был <color=#ce3f27>ограблен</color>!",
                ["RemainTime"] = "{0} {1} будет уничтожен через <color=#ce3f27>{2}</color>!",
                ["PreFinish"] = "{0} Ивент будет окончен через <color=#ce3f27>{1}</color>",
                ["Finish"] = "{0} Перевозка груза <color=#ce3f27>окончена</color>!",

                ["EnterPVP"] = "{0} Вы <color=#ce3f27>вошли</color> в PVP зону, теперь другие игроки <color=#ce3f27>могут</color> наносить вам урон!",
                ["ExitPVP"] = "{0} Вы <color=#738d43>вышли</color> из PVP зоны, теперь другие игроки <color=#738d43>не могут</color> наносить вам урон!",

                ["SendEconomy"] = "{0} Вы <color=#738d43>получили</color> <color=#55aaff>{1}</color> баллов в экономику за прохождение ивента",
                ["Hours"] = "ч.",
                ["Minutes"] = "м.",
                ["Seconds"] = "с.",

                ["PveMode_BlockAction"] = "{0} Вы <color=#ce3f27>не можете</color> взаимодействовать с ивентом из-за кулдауна!",
                ["PveMode_YouAreNoOwner"] = "{0} Вы <color=#ce3f27>не являетесь</color> владельцем ивента!",

                ["PveMode_NewOwner"] = "{0} <color=#55aaff>{1}</color> стал владельцем ивента!",
            }, this, "ru");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["EventActive_Exeption"] = "This event is active now. Finish the current event! (<color=#ce3f27>/convoystop</color>)!",
                ["RouteNotFound_Exeption"] = "The route could not be generated! Try to increase the minimum road length or change the route type!",
                ["ConfigurationNotFound_Exeption"] = "The event configuration <color=#ce3f27>could not</color> be found!",
                ["PresetNotFound_Exeption"] = "{0} preset was <color=#ce3f27>not found</color> in the config!",
                ["FileNotFound_Exeption"] = "Data file not found or corrupted! ({0}.json)!",
                ["CustomRouteDescription"] = "{0} To record a custom route, stand on the ground and enter the command /convoypathstart. Follow the route and enter the command /convoypathsave <routeName>. To cancel the route, use the command /convoypathcancel",

                ["SuccessfullyLaunched"] = "The event has been <color=#738d43>successfully</color> launched!",
                ["PreStart"] = "{0} In <color=#738d43>{1}</color> the cargo will be transported along the road!",
                ["EventStart"] = "{0} <color=#738d43>{1}</color> is spawned at grid <color=#738d43>{2}</color>",
                ["ConvoyAttacked"] = "{0} {1} <color=#ce3f27>attacked</color> a convoy",
                ["DamageDistance"] = "{0} Come <color=#ce3f27>closer</color>!",
                ["CantLoot"] = "{0} It is necessary to stop the convoy and kill the <color=#ce3f27>guards</color>!",
                ["Looted"] = "{0} <color=#738d43>{1}</color> has been <color=#ce3f27>looted</color>!",
                ["RemainTime"] = "{0} {1} will be destroyed in <color=#ce3f27>{2}</color>!",
                ["PreFinish"] = "{0} The event will be over in <color=#ce3f27>{1}</color>",
                ["Finish"] = "{0} The event is <color=#ce3f27>over</color>!",


                ["EnterPVP"] = "{0} You <color=#ce3f27>have entered</color> the PVP zone, now other players <color=#ce3f27>can damage</color> you!",
                ["ExitPVP"] = "{0} You <color=#738d43>have gone out</color> the PVP zone, now other players <color=#738d43>can’t damage</color> you!",

                ["SendEconomy"] = "{0} You <color=#738d43>have earned</color> <color=#55aaff>{1}</color> points in economics for participating in the event",
                ["Hours"] = "h.",
                ["Minutes"] = "m.",
                ["Seconds"] = "s.",

                ["PveMode_NewOwner"] = "{0} <color=#55aaff>{1}</color> became the owner of the event!",
                ["Marker_EventOwner"] = "Event Owner: {0}",

                ["PveMode_BlockAction"] = "{0} You <color=#ce3f27>can't interact</color> with the event because of the cooldown!",
                ["PveMode_YouAreNoOwner"] = "{0} You are not the <color=#ce3f27>owner</color> of the event!",

                ["EventStart_Log"] = "The event has begun! (Preset name - {0})",
                ["EventStop_Log"] = "The event is over!",
                ["RouteСachingStart_Log"] = "Route caching has started!",
                ["RouteСachingStop_Log"] = "Route caching has ended! The number of routes: {0}",

            }, this);
        }

        private static string GetMessage(string langKey, string userID) => _ins.lang.GetMessage(langKey, _ins, userID);

        private static string GetMessage(string langKey, string userID, params object[] args) => (args.Length == 0) ? GetMessage(langKey, userID) : string.Format(GetMessage(langKey, userID), args);
        #endregion Lang

        #region Data

        private class CustomRouteData
        {
            [JsonProperty("Points")]
            public List<string> Points { get; set; }
        }

        private Dictionary<string, List<List<string>>> _roots = new Dictionary<string, List<List<string>>>();

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Title, _roots);

        private void LoadData() => _roots = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, List<List<string>>>>(Title);

        private readonly Dictionary<string, EntityCustomizationData> _entityCustomizationData = new Dictionary<string, EntityCustomizationData>
        {
            ["van_default"] = new EntityCustomizationData
            {
                RegularEntities = new HashSet<EntityData>
                {
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab",
                        Skin = 0,
                        Position = "(0, -0.220, -2.294)",
                        Rotation = "(0, 90, 359.06)"
                    }
                },
                DecorEntities = new HashSet<EntityData>
                {
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab",
                        Skin = 1984902763,
                        Position = "(-1.113, -0.221, -0.147)",
                        Rotation = "(0, 0, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab",
                        Skin = 1984902763,
                        Position = "(1.113, -0.221, -0.147)",
                        Rotation = "(0, 180, 0)"
                    },

                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(1.206, 2.251, -0.526)",
                        Rotation = "(270, 90, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(1.206, 2.251, -0.151)",
                        Rotation = "(270, 90, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(1.206, 2.251, 0.223)",
                        Rotation = "(270, 90, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(-1.202, 2.251, -0.530)",
                        Rotation = "(270, 270, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(-1.202, 2.251, -0.157)",
                        Rotation = "(270, 270, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(-1.202, 2.251, 0.218)",
                        Rotation = "(270, 270, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(-0.398, 2.182, -2.422)",
                        Rotation = "(270, 180, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(-0.025, 2.182, -2.422)",
                        Rotation = "(270, 180, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        Skin = 0,
                        Position = "(0.349, 2.182, -2.422)",
                        Rotation = "(270, 180, 0)"
                    }
                }
            },
            ["sedan_default"] = new EntityCustomizationData
            {
                RegularEntities = new HashSet<EntityData>(),
                DecorEntities = new HashSet<EntityData>
                {
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        Skin = 0,
                        Position = "(-0.624, 1.715, 0.536)",
                        Rotation = "(0, 90, 180)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        Skin = 0,
                        Position = "(-0.624, 1.715, -0.372)",
                        Rotation = "(0, 90, 180)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        Skin = 0,
                        Position = "(0.624, 1.715, 0.536)",
                        Rotation = "(0, 270, 180)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        Skin = 0,
                        Position = "(0.624, 1.715, -0.372)",
                        Rotation = "(0, 270, 180)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        Skin = 0,
                        Position = "(-0.628, 1.216, -1.776)",
                        Rotation = "(0, 90, 175.691)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        Skin = 0,
                        Position = "(0.628, 1.216, -1.776)",
                        Rotation = "(0, 270, 184.309)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/content/vehicles/boats/rhib/subents/fuel_storage.prefab",
                        Skin = 0,
                        Position = "(0.833, 1.09, -2.019)",
                        Rotation = "(85.057, 0, 0)"
                    },
                    new EntityData
                    {
                        PrefabName = "assets/content/vehicles/boats/rhib/subents/fuel_storage.prefab",
                        Skin = 0,
                        Position = "(-0.833, 1.09, -2.019)",
                        Rotation = "(85.057, 0, 0)"
                    }
                }
            }
        };

        private class EntityCustomizationData
        {
            [JsonProperty("Decorative entities")]
            public HashSet<EntityData> DecorEntities { get; set; }

            [JsonProperty("Regular entities")]
            public HashSet<EntityData> RegularEntities { get; set; }
        }

        private class EntityData
        {
            [JsonProperty("Prefab")]
            public string PrefabName { get; set; }

            [JsonProperty("Skin")]
            public ulong Skin { get; set; }

            [JsonProperty("Position")]
            public string Position { get; set; }

            [JsonProperty("Rotation")]
            public string Rotation { get; set; }
        }
        #endregion Data 

        #region Config
        private PluginConfig _config;
        
        private void UpdateConfig()
        {
            if (string.IsNullOrEmpty(_config.Version))
            {
                PrintError("Delete the configuration file!");
                NextTick(() => Server.Command($"o.unload {Name}"));
                return;
            }
            
            if (_config.Version != Version.ToString())
            {
                PluginConfig defaultConfig = PluginConfig.DefaultConfig();
                bool isLootMigrated = false;
                
                VersionNumber versionNumber;
                string[] versionArray = _config.Version.Split('.');
                versionNumber.Major = Convert.ToInt32(versionArray[0]);
                versionNumber.Minor = Convert.ToInt32(versionArray[1]);
                versionNumber.Patch = Convert.ToInt32(versionArray[2]);

                if (versionNumber.Major == 2)
                {
                    if (versionNumber.Minor == 6)
                    {
                        if (versionNumber.Patch <= 2)
                            _config.BehaviorConfig.IsPlayerTurretEnable = true;

                        if (versionNumber.Patch <= 7)
                        {
                            foreach (NpcConfig npcConfig in _config.NpcConfigs)
                                foreach (NpcBelt npcBelt in npcConfig.BeltItems)
                                    if (npcBelt.ShortName == "rocket.launcher.dragon")
                                        npcBelt.ShortName = "rocket.launcher";
                        }
                        versionNumber = new VersionNumber(2, 7, 0);
                    }

                    if (versionNumber.Minor == 7)
                    {
                        if (versionNumber.Patch <= 4)
                        {
                            _config.KaruzaCarConfigs = defaultConfig.KaruzaCarConfigs;
                        }
                        versionNumber = new VersionNumber(2, 8, 0);
                    }

                    if (versionNumber.Minor == 8)
                    {
                        if (versionNumber.Patch <= 0)
                        {
                            foreach (TravellingVendorConfig vendorConfig in _config.TravelingVendorConfigs)
                                vendorConfig.DoorSkin = 934924536;

                            _config.BehaviorConfig.IsStopConvoyAggressive = true;
                        }

                        if (versionNumber.Patch <= 5)
                        {
                            isLootMigrated = true;
                            LootMigrator.MigrateFromOldLootTables();
                        }
                        
                        versionNumber = new VersionNumber(2, 9, 0);
                    }

                    if (versionNumber.Minor == 9)
                    {
                        if (versionNumber.Patch <= 1)
                        {
                            _config.SupportedPluginsConfig.LifeSupportConfig = defaultConfig.SupportedPluginsConfig.LifeSupportConfig;
                        }

                        if (versionNumber.Patch <= 4)
                        {
                            if (!isLootMigrated)
                                LootMigrator.MigrateFromLootManager();
                        }
                    }
                }
                else
                {
                    PrintError("Delete the configuration file!");
                    NextTick(() => Server.Command($"o.unload {Name}"));
                }

                _config.Version = Version.ToString();
                
            }

            PluginConfig defaultConfigForValues = PluginConfig.DefaultConfig();
            if (_config.MainConfig.FixedSchedule == null)
                _config.MainConfig.FixedSchedule = defaultConfigForValues.MainConfig.FixedSchedule;
            else if (_config.MainConfig.FixedSchedule.Windows == null)
                _config.MainConfig.FixedSchedule.Windows = defaultConfigForValues.MainConfig.FixedSchedule.Windows;

            UpdateConfigValues();
            SaveConfig();
        }
        
        private void UpdateConfigValues()
        {
            foreach (CrateConfig crateConfig in _config.CrateConfigs)
            {
                if (crateConfig.LootTableConfig == null)
                    crateConfig.LootTableConfig = LootController.GetDefaultLootTable();
                else
                    UpdateLootTableValues(crateConfig.LootTableConfig);
            }
            
            foreach (NpcConfig npcConfig in _config.NpcConfigs)
            {
                if (npcConfig.LootTableConfig == null)
                    npcConfig.LootTableConfig = LootController.GetDefaultLootTable();
                else
                    UpdateLootTableValues(npcConfig.LootTableConfig);
            }
            
            foreach (BradleyConfig bradleyConfig in _config.BradleyConfigs)
            {
                if (bradleyConfig.LootTableConfig == null)
                    bradleyConfig.LootTableConfig = LootController.GetDefaultLootTable();
                else
                    UpdateLootTableValues(bradleyConfig.LootTableConfig);
            }
            
            foreach (HeliConfig heliConfig in _config.HeliConfigs)
            {
                if (heliConfig.LootTableConfig == null)
                    heliConfig.LootTableConfig = LootController.GetDefaultLootTable();
                else
                    UpdateLootTableValues(heliConfig.LootTableConfig);
            }
        }

        private void UpdateLootTableValues(LootTableConfig lootTableConfig)
        {
            if (lootTableConfig.ItemsTable.MaxItemsAmount > lootTableConfig.ItemsTable.Items.Count) 
                lootTableConfig.ItemsTable.MaxItemsAmount = lootTableConfig.ItemsTable.Items.Count;
            
            lootTableConfig.ItemsTable.Items = lootTableConfig.ItemsTable.Items.OrderByQuickSort(x => x.Chance);
            
            for (int i = lootTableConfig.ItemsTable.Items.Count - 1; i >= 0; i--)
            {
                LootItemConfig lootItemConfig = lootTableConfig.ItemsTable.Items[i];
                
                if (!ItemManager.itemList.Any(x => x.shortname == lootItemConfig.Shortname))
                {
                    PrintWarning($"Unknown item removed! ({lootItemConfig.Shortname})");
                    lootTableConfig.ItemsTable.Items.RemoveAt(i);
                    continue;
                }

                lootItemConfig.Chance = Math.Clamp(lootItemConfig.Chance, 0f, 100f);
                
                if (lootItemConfig.MaxAmount < lootItemConfig.MinAmount) 
                    lootItemConfig.MaxAmount = lootItemConfig.MinAmount;
                
            }
            
            if (lootTableConfig.PrefabsTable.MaxPrefabsAmount > lootTableConfig.PrefabsTable.Prefabs.Count) 
                lootTableConfig.PrefabsTable.MaxPrefabsAmount = lootTableConfig.PrefabsTable.Prefabs.Count;
            
            lootTableConfig.PrefabsTable.Prefabs = lootTableConfig.PrefabsTable.Prefabs.OrderByQuickSort(x => x.Chance);

            for (int i = lootTableConfig.PrefabsTable.Prefabs.Count - 1; i >= 0; i--)
            {
                LootPrefabConfig lootPrefabConfig = lootTableConfig.PrefabsTable.Prefabs[i];
                
                lootPrefabConfig.Chance = Math.Clamp(lootPrefabConfig.Chance, 0f, 100f);
                
                if (lootPrefabConfig.MaxAmount < lootPrefabConfig.MinAmount) 
                    lootPrefabConfig.MaxAmount = lootPrefabConfig.MinAmount;
            }
        }

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
            [JsonProperty(En ? "Enable automatic event holding [true/false]" : "Включить автоматическое проведение ивента [true/false]")]
            public bool IsAutoEvent { get; set; }

            [JsonProperty(En ? "Minimum time between events [sec]" : "Минимальное вермя между ивентами [sec]")]
            public int MinTimeBetweenEvents { get; set; }

            [JsonProperty(En ? "Maximum time between events [sec]" : "Максимальное вермя между ивентами [sec]")]
            public int MaxTimeBetweenEvents { get; set; }

            [JsonProperty(En ? "The time between receiving a chat notification and the start of the event [sec.]" : "Время до начала ивента после сообщения в чате [sec.]")]
            public int PreStartTime { get; set; }

            [JsonProperty(En ? "Enable logging of the start and end of the event? [true/false]" : "Включить логирование начала и окончания ивента? [true/false]")]
            public bool EnableStartStopLogs { get; set; }

            [JsonProperty(En ? "The event will not end if there are players nearby [true/false]" : "Ивент не будет заканчиваться, если рядом есть игроки [true/false]")]
            public bool DontStopEventIfPlayerInZone { get; set; }

            [JsonProperty(En ? "The turrets of the сonvoy will drop loot after destruction? [true/false]" : "Турели конвоя будут оставлять лут после уничтожения? [true/false]")]
            public bool IsTurretDropWeapon { get; set; }

            [JsonProperty(En ? "Destroy the сonvoy after opening all the crates [true/false]" : "Уничтожать конвой после открытия всех ящиков [true/false]")]
            public bool KillEventAfterLoot { get; set; }

            [JsonProperty(En ? "Time to destroy the сonvoy after opening all the crates [sec]" : "Время до уничтожения конвоя после открытия всех ящиков [sec]")]
            public int EndAfterLootTime { get; set; }

            [JsonProperty(En ? "Fixed event schedule settings" : "Настройки фиксированного расписания ивента")]
            public FixedScheduleConfig FixedSchedule { get; set; }
        }

        private class FixedScheduleConfig
        {
            [JsonProperty(En ? "Enable fixed schedule [true/false]" : "Включить фиксированное расписание [true/false]")]
            public bool Enabled { get; set; }

            [JsonProperty(En ? "UTC offset in hours" : "Сдвиг UTC в часах")]
            public int UtcOffsetHours { get; set; }

            [JsonProperty(En ? "Event windows" : "Окна запуска ивента")]
            public List<FixedScheduleWindowConfig> Windows { get; set; }
        }

        private class FixedScheduleWindowConfig
        {
            [JsonProperty(En ? "Window start (HH:mm)" : "Начало окна (HH:mm)")]
            public string Start { get; set; }

            [JsonProperty(En ? "Window end (HH:mm)" : "Конец окна (HH:mm)")]
            public string End { get; set; }
        }

        private class BehaviorConfig
        {
            [JsonProperty(En ? "The time for which the convoy becomes aggressive after it has been attacked (-1 - is always aggressive)" : "Время на которое конвой становится агрессивным после того как был атакован (-1 - всегда агрессивен)")]
            public int AggressiveTime { get; set; }

            [JsonProperty(En ? "The convoy will always remain aggressive while stopped [true/false]" : "Остановленный конвой будет всегда агрессивен [true/false]")]
            public bool IsStopConvoyAggressive { get; set; }

            [JsonProperty(En ? "The duration of the stop after the attack" : "Длительность остановки после нападения")]
            public int StopTime { get; set; }

            [JsonProperty(En ? "Player turrets will attack NPCs if the convoy is stopped (false - They won't attack at all) [true/false]" : "Турели игроков будут атаковать остановившийся конвой (false - не будут атаковать вообще) [true/false]")]
            public bool IsPlayerTurretEnable { get; set; }
        }

        private class LootConfig
        {
            [JsonProperty(En ? "When the car is destroyed, loot falls to the ground [true/false]" : "При уничтожении машины лут будет падать на землю [true/false]")]
            public bool DropLoot { get; set; }

            [JsonProperty(En ? "Percentage of loot loss when destroying a сar [0.0-1.0]" : "Процент потери лута при уничтожении машины [0.0-1.0]")]
            public float LootLossPercent { get; set; }

            [JsonProperty(En ? "Prohibit looting crates if the convoy is moving [true/false]" : "Запретить лутать ящики, если конвой движется [true/false]")]
            public bool BlockLootingByMove { get; set; }

            [JsonProperty(En ? "Prohibit looting crates if NPCs are alive [true/false]" : "Запретить лутать ящики, если живы нпс [true/false]")]
            public bool BlockLootingByNpcs { get; set; }

            [JsonProperty(En ? "Prohibit looting crates if Bradleys are alive [true/false]" : "Запретить лутать ящики, если живы Bradley [true/false]")]
            public bool BlockLootingByBradleys { get; set; }

            [JsonProperty(En ? "Prohibit looting crates if Heli is alive [true/false]" : "Запретить лутать ящики, если жив вертолет [true/false]")]
            public bool BlockLootingByHeli { get; set; }
        }

        private class PathConfig
        {
            [JsonProperty(En ? "Type of routes (0 - standard (fast generation), 1 - experimental (multiple roads are used), 2 - custom)" : "Тип маршрутов (0 - стандартный (быстрая генерация), 1 - экспериментальный (используется несколько дорог), 2 - кастомый)")]
            public int PathType { get; set; }

            [JsonProperty(En ? "Minimum road length" : "Минимальная длина дороги")]
            public int MinRoadLength { get; set; }

            [JsonProperty(En ? "List of excluded roads (/convoyroadblock)" : "Список исключенных дорог (/convoyroadblock)")]
            public HashSet<int> BlockRoads { get; set; }

            [JsonProperty(En ? "Setting up the standard route type" : "Настройка стандартного режима маршрутов")]
            public RegularPathConfig RegularPathConfig { get; set; }

            [JsonProperty(En ? "Setting up a experimental type" : "Настройка экспериментального режима маршрутов")]
            public ComplexPathConfig ComplexPathConfig { get; set; }

            [JsonProperty(En ? "Setting up a custom route type" : "Настройка кастомного режима маршрутов")]
            public CustomPathConfig CustomPathConfig { get; set; }
        }

        private class RegularPathConfig
        {
            [JsonProperty(En ? "If there is a ring road on the map, then the convoy will always spawn here" : "Если на карте есть кольцевая дорога, то конвой будет двигаться только по ней")]
            public bool IsRingRoad { get; set; }
        }

        private class ComplexPathConfig
        {
            [JsonProperty(En ? "Always choose the longest route? [true/false]" : "Всегда выбирать самый длинный маршрут? [true/false]")]
            public bool ChooseLongestRoute { get; set; }

            [JsonProperty(En ? "The minimum number of roads in a complex route" : "Минимальное количество дорог в комплексом маршруте")]
            public int MinRoadCount { get; set; }
        }

        private class CustomPathConfig
        {
            [JsonProperty(En ? "List of presets for custom routes" : "Список пресетов кастомных маршрутов")]
            public List<string> CustomRoutesPresets { get; set; }
        }

        private class EventConfig
        {
            [JsonProperty(En ? "Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Name displayed on the map (For custom marker)" : "Отображаемое на карте название (для кастомного маркера)")]
            public string DisplayName { get; set; }

            [JsonProperty(En ? "Automatic startup" : "Автоматический запуск")]
            public bool IsAutoStart { get; set; }

            [JsonProperty(En ? "Probability of a preset [0.0-100.0]" : "Вероятность пресета [0.0-100.0]")]
            public float Chance { get; set; }

            [JsonProperty(En ? "The minimum time after the server's wipe when this preset can be selected automatically [sec]" : "Минимальное время после вайпа сервера, когда этот пресет может быть выбран автоматически [sec]")]
            public int MinTimeAfterWipe { get; set; }

            [JsonProperty(En ? "The maximum time after the server's wipe when this preset can be selected automatically [sec] (-1 - do not use this parameter)" : "Максимальное время после вайпа сервера, когда этот пресет может быть выбран автоматически [sec] (-1 - не использовать)")]
            public int MaxTimeAfterWipe { get; set; }

            [JsonProperty(En ? "Event time" : "Время ивента")]
            public int EventTime { get; set; }

            [JsonProperty(En ? "Radius of the event zone" : "Радиус зоны ивента")]
            public float ZoneRadius { get; set; }

            [JsonProperty(En ? "Maximum range for damage to Bradleys/NPCs/turrets (-1 - do not limit)" : "Максимальная дистанция для нанесения урона по турелям/нпс/бредли (-1 - не ограничивать)")]
            public int MaxGroundDamageDistance { get; set; }

            [JsonProperty(En ? "Maximum range for damage to Heli when the convoy is stopped (-1 - do not limit)" : "Максимальная дистанция для нанесения урона по вертолету, когда конвой остановлен (-1 - не ограничивать)")]
            public int MaxHeliDamageDistance { get; set; }

            [JsonProperty(En ? "Order of vehicles" : "Порядок транспортных средств")]
            public List<string> VehiclesOrder { get; set; }

            [JsonProperty(En ? "Enable the helicopter" : "Включить вертолет")]
            public bool IsHeli { get; set; }

            [JsonProperty(En ? "Heli preset" : "Пресет вертолета")]
            public string HeliPreset { get; set; }

            [JsonProperty(En ? "NPC damage multipliers depending on the attacker's weapon" : "Множители урона по NPC в зависимости от оружия атакующего")]
            public Dictionary<string, float> WeaponToScaleDamageNpc { get; set; }
        }

        private class TravellingVendorConfig : VehicleConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Delete the vendor's map marker?" : "Удалять маркер торговца? [true/false]")]
            public bool DeleteMapMarker { get; set; }

            [JsonProperty(En ? "Add a lock on the Loot Door? [true/false]" : "Добавить замок на дверь к луту? [true/false]")]
            public bool IsLocked { get; set; }

            [JsonProperty(En ? "Loot Door Health" : "Здоровье двери к луту")]
            public float DoorHealth { get; set; }

            [JsonProperty(En ? "Loot door SkinID" : "SkinID двери к луту")]
            public ulong DoorSkin { get; set; }
        }

        private class ModularCarConfig : VehicleConfig
        {
            [JsonProperty(En ? "Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Scale damage" : "Множитель урона")]
            public float DamageScale { get; set; }

            [JsonProperty(En ? "Modules" : "Модули")]
            public List<string> Modules { get; set; }
        }

        private class BradleyConfig : VehicleConfig
        {
            [JsonProperty(En ? "Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty("HP")]
            public float Hp { get; set; }

            [JsonProperty(En ? "Damage multiplier from Bradley to buildings (-1 - do not change)" : "Множитель урона от бредли по постройкам (-1 - не изменять)")]
            public float BradleyBuildingDamageScale { get; set; }

            [JsonProperty(En ? "The viewing distance" : "Дальность обзора")]
            public float ViewDistance { get; set; }

            [JsonProperty(En ? "Radius of search" : "Радиус поиска")]
            public float SearchDistance { get; set; }

            [JsonProperty(En ? "The multiplier of Machine-gun aim cone" : "Множитель разброса пулемёта")]
            public float CoaxAimCone { get; set; }

            [JsonProperty(En ? "The multiplier of Machine-gun fire rate" : "Множитель скорострельности пулемёта")]
            public float CoaxFireRate { get; set; }

            [JsonProperty(En ? "Amount of Machine-gun burst shots" : "Кол-во выстрелов очереди пулемёта")]
            public int CoaxBurstLength { get; set; }

            [JsonProperty(En ? "The time between shots of the main gun [sec.]" : "Время между залпами основного орудия [sec.]")]
            public float NextFireTime { get; set; }

            [JsonProperty(En ? "The time between shots of the main gun in a fire rate [sec.]" : "Время между выстрелами основного орудия в залпе [sec.]")]
            public float TopTurretFireRate { get; set; }

            [JsonProperty(En ? "Numbers of crates" : "Кол-во ящиков после уничтожения")]
            public int CountCrates { get; set; }

            [JsonProperty(En ? "Open the crates immediately after spawn" : "Открывать ящики сразу после спавна")]
            public bool InstCrateOpen { get; set; }
            
            [JsonProperty(En ? "Loot" : "Лут")]
            public LootTableConfig LootTableConfig { get; set; }

            [JsonProperty(En ? "LootManager Preset" : "Пресет LootManager", NullValueHandling = NullValueHandling.Ignore)]
            public string LootManagerPreset { get; set; }

            [JsonProperty(En ? "Own loot table" : "Собственная таблица лута", NullValueHandling = NullValueHandling.Ignore)]
            public BaseLootTableConfigOld BaseLootTableConfigOld { get; set; }
        }

        private class SedanConfig : VehicleConfig
        {
            [JsonProperty(En ? "Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty("HP")]
            public float Hp { get; set; }
        }

        private class KaruzaCarConfig : VehicleConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Prefab Name" : "Название префаба")]
            public string PrefabName { get; set; }
        }

        private class BikeConfig : VehicleConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Prefab Name" : "Название префаба")]
            public string PrefabName { get; set; }

            [JsonProperty("HP")]
            public float Hp { get; set; }
        }

        private class VehicleConfig
        {
            [JsonProperty(En ? "NPC preset" : "Пресет НПС", Order = 100)]
            public string NpcPresetName { get; set; }

            [JsonProperty(En ? "Number of NPCs" : "Количество НПС", Order = 100)]
            public int NumberOfNpc { get; set; }

            [JsonProperty(En ? "Locations of additional NPCs" : "Расположения дополнительных нпс", Order = 101)]
            public HashSet<NpcPoseConfig> AdditionalNpc { get; set; }

            [JsonProperty(En ? "Crates" : "Крейты", Order = 102)]
            public HashSet<PresetLocationConfig> CrateLocations { get; set; }

            [JsonProperty(En ? "Turrets" : "Турели", Order = 103)]
            public HashSet<PresetLocationConfig> TurretLocations { get; set; }

            [JsonProperty(En ? "SamSites" : "ПВО", Order = 104)]
            public HashSet<PresetLocationConfig> SamSiteLocations { get; set; }
        }

        private class NpcPoseConfig : LocationConfig
        {
            [JsonProperty(En ? "Enable spawn?" : "Активировать спавн?")]
            public bool IsEnable { get; set; }

            [JsonProperty(En ? "Seat prefab" : "Префаб сидения")]
            public string SeatPrefab { get; set; }

            [JsonProperty(En ? "Will the NPC dismount when the vehicle stops?" : "Будет ли NPC спешиваться при остановке транспортного средства?")]
            public bool IsDismount { get; set; }

            [JsonProperty(En ? "NPC preset (Empty - as in a vehicle)" : "Пресет НПС (Empty - оставить как в транспортном средстве)")]
            public string NpcPresetName { get; set; }
        }

        private class SamSiteConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Health" : "Кол-во ХП")]
            public float Hp { get; set; }

            [JsonProperty(En ? "Number of ammo" : "Кол-во патронов")]
            public int CountAmmo { get; set; }
        }

        private class TurretConfig
        {
            [JsonProperty(En ? "Preset Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty(En ? "Health" : "Кол-во ХП")]
            public float Hp { get; set; }

            [JsonProperty(En ? "Weapon ShortName" : "ShortName оружия")]
            public string ShortNameWeapon { get; set; }

            [JsonProperty(En ? "Ammo ShortName" : "ShortName патронов")]
            public string ShortNameAmmo { get; set; }

            [JsonProperty(En ? "Number of ammo" : "Кол-во патронов")]
            public int CountAmmo { get; set; }

            [JsonProperty(En ? "Target detection range (0 - do not change)" : "Дальность обнаружения цели (0 - не изменять)")]
            public float TargetDetectionRange { get; set; }

            [JsonProperty(En ? "Target loss range (0 - do not change)" : "Дальность потери цели (0 - не изменять)")]
            public float TargetLossRange { get; set; }
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
            
            [JsonProperty(En ? "Loot" : "Лут")]
            public LootTableConfig LootTableConfig { get; set; }

            [JsonProperty(En ? "LootManager Preset" : "Пресет LootManager", NullValueHandling = NullValueHandling.Ignore)]
            public string LootManagerPreset { get; set; }

            [JsonProperty(En ? "Own loot table" : "Собственная таблица предметов", NullValueHandling = NullValueHandling.Ignore)]
            public LootTableConfigOld LootTableConfigOld { get; set; }
        }

        private class PresetLocationConfig : LocationConfig
        {
            [JsonProperty(En ? "Preset name" : "Название пресета")]
            public string PresetName { get; set; }
        }

        private class LocationConfig
        {
            [JsonProperty(En ? "Position" : "Позиция")]
            public string Position { get; set; }

            [JsonProperty(En ? "Rotation" : "Вращение")]
            public string Rotation { get; set; }
        }

        private class HeliConfig
        {
            [JsonProperty(En ? "Name" : "Название пресета")]
            public string PresetName { get; set; }

            [JsonProperty("HP")]
            public float Hp { get; set; }

            [JsonProperty(En ? "HP of the main rotor" : "HP главного винта")]
            public float MainRotorHealth { get; set; }

            [JsonProperty(En ? "HP of tail rotor" : "HP хвостового винта")]
            public float RearRotorHealth { get; set; }

            [JsonProperty(En ? "Numbers of crates" : "Количество ящиков")]
            public int CratesAmount { get; set; }

            [JsonProperty(En ? "Flying height" : "Высота полета")]
            public float Height { get; set; }

            [JsonProperty(En ? "Bullet speed" : "Скорость пуль")]
            public float BulletSpeed { get; set; }

            [JsonProperty(En ? "Bullet Damage" : "Урон пуль")]
            public float BulletDamage { get; set; }

            [JsonProperty(En ? "The distance to which the helicopter can move away from the convoy" : "Дистанция, на которую вертолет может отдаляться от конвоя")]
            public float Distance { get; set; }

            [JsonProperty(En ? "The time for which the helicopter can leave the convoy to attack the target [sec.]" : "Время, на которое верталет может покидать конвой для атаки цели [sec.]")]
            public float OutsideTime { get; set; }

            [JsonProperty(En ? "The helicopter will not aim for the nearest monument at death [true/false]" : "Вертолет не будет стремиться к ближайшему монументу при смерти [true/false]")]
            public bool ImmediatelyKill { get; set; }

            [JsonProperty(En ? "Open the crates immediately after spawn" : "Открывать ящики сразу после спавна")]
            public bool InstCrateOpen { get; set; }
            
            [JsonProperty(En ? "Loot" : "Лут")]
            public LootTableConfig LootTableConfig { get; set; }

            [JsonProperty(En ? "LootManager Preset" : "Пресет LootManager", NullValueHandling = NullValueHandling.Ignore)]
            public string LootManagerPreset { get; set; }

            [JsonProperty(En ? "Own loot table" : "Собственная таблица лута", NullValueHandling = NullValueHandling.Ignore)]
            public BaseLootTableConfigOld BaseLootTableConfigOld { get; set; }
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
            
            [JsonProperty(En ? "Loot" : "Лут")]
            public LootTableConfig LootTableConfig { get; set; }

            [JsonProperty(En ? "LootManager Preset" : "Пресет LootManager", NullValueHandling = NullValueHandling.Ignore)]
            public string LootManagerPreset { get; set; }

            [JsonProperty(En ? "Own loot table" : "Собственная таблица лута", NullValueHandling = NullValueHandling.Ignore)]
            public LootTableConfigOld LootTableConfigOld { get; set; }
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
        
        
        private class LootTableConfig
        {
            [JsonProperty(En ? "Allow AlphaLoot to Modify Loot" : "Разрешить плагину AlphaLoot изменять лут")]
            public bool IsAlphaLoot { get; set; }
            
            [JsonProperty(En ? "Use AlphaLoot Preset with This Name" : "Использовать пресет AlphaLoot с этим названием")]
            public string AlphaLootPreset { get; set; }
            
            [JsonProperty(En ? "Allow LoottableStacksizeGUI to Modify Loot" : "Разрешить плагину LoottableStacksizeGUI изменять лут")]
            public bool IsLoottablePlugin { get; set; }
            
            [JsonProperty(En ? "Use LoottableStacksizeGUI Preset with This Name" : "Использовать пресет LoottableStacksizeGUI с этим названием")]
            public string LoottablePreset { get; set; }
            
            [JsonProperty(En ? "Allow CustomLoot to Modify Loot" : "Разрешить плагину CustomLoot изменять лут")]
            public bool IsCustomLootPlugin { get; set; }
            
            [JsonProperty(En ? "Use CustomLoot Preset with This Name" : "Использовать пресет CustomLoot с этим названием")]
            public string CustomLootPreset { get; set; }

            
            [JsonProperty(En ? "Clear the container before adding items/prefabs" : "Очистить контейнер перед добавлением предметов/префабов")]
            public bool ClearDefaultLoot { get; set; }
            
            [JsonProperty(En ? "Prefabs Table" : "Таблица префабов")]
            public PrefabsLootTableConfig PrefabsTable { get; set; }
            
            [JsonProperty(En ? "Items Table" : "Таблица предметов")]
            public ItemsLootTableConfig ItemsTable { get; set; }
        }

        private class ItemsLootTableConfig
        {
            [JsonProperty(En ? "Enabled" : "Включить")]
            public bool IsEnabled { get; set; }
            
            [JsonProperty(En ? "Use Chance-Based Spawn (Ignore Min/Max Unique Items)" : "Выпадение по шансу (игнорировать Min/Max разных предметов)")]
            public bool DisableMinMax { get; set; }
                
            [JsonProperty(En ? "Minimum Unique Items" : "Минимум разных предметов")]
            public int MinItemsAmount { get; set; }

            [JsonProperty(En ? "Maximum Unique Items" : "Максимум разных предметов")]
            public int MaxItemsAmount { get; set; }
            
            [JsonProperty(En ? "Item Pool" : "Пул предметов")]
            public List<LootItemConfig> Items { get; set; }
        }
        
        private class PrefabsLootTableConfig
        {
            [JsonProperty(En ? "Enabled" : "Включить")]
            public bool IsEnabled { get; set; }
            
            [JsonProperty(En ? "Minimum Unique Prefabs" : "Минимум разных префабов")]
            public int MinPrefabsAmount { get; set; }

            [JsonProperty(En ? "Maximum Unique Prefabs" : "Максимум разных префабов")]
            public int MaxPrefabsAmount { get; set; }
            
            [JsonProperty(En ? "Prefab Pool" : "Пул префабов")]
            public List<LootPrefabConfig> Prefabs { get; set; }
        }
        
        private class LootItemConfig : LootElementChanceConfig
        {
            [JsonProperty("Shortname")]
            public string Shortname { get; set; }
            
            [JsonProperty("SkinID")]
            public ulong Skin { get; set; }
            
            [JsonProperty(En ? "Display Name (empty - default)" : "Отображаемое название (оставить пустым - стандартное)")]
            public string DisplayName { get; set; }
            
            [JsonProperty(En ? "Owner Display Name" : "Имя владельца предмета")]
            public string OwnerName { get; set; }
            
            [JsonProperty(En ? "Is Blueprint" : "Это чертеж")]
            public bool IsBlueprint { get; set; }
            
            [JsonProperty(En ? "Genomes" : "Геномы")]
            public List<string> Genomes { get; set; }
        }

        private class LootPrefabConfig : LootElementChanceConfig
        {
            [JsonProperty(En ? "Prefab" : "Префаб")]
            public string PrefabName { get; set; }
        }

        private class LootElementChanceConfig
        {
            [JsonProperty(En ? "Chance [0.0-100.0]" : "Шанс [0.0-100.0]")]
            public float Chance { get; set; }
            
            [JsonProperty(En ? "Minimum Amount" : "Минимальное количество")]
            public int MinAmount { get; set; }
            
            [JsonProperty(En ? "Maximum Amount" : "Максимальное количество")]
            public int MaxAmount { get; set; }
        }
        

        private class LootTableConfigOld : BaseLootTableConfigOld
        {
            [JsonProperty(En ? "Allow the AlphaLoot plugin to spawn items in this crate" : "Разрешить плагину AlphaLoot спавнить предметы в этом ящике")]
            public bool IsAlphaLoot { get; set; }

            [JsonProperty(En ? "The name of the loot preset for AlphaLoot" : "Название пресета лута AlphaLoot")]
            public string AlphaLootPresetName { get; set; }

            [JsonProperty(En ? "Allow the CustomLoot plugin to spawn items in this crate" : "Разрешить плагину CustomLoot спавнить предметы в этом ящике")]
            public bool IsCustomLoot { get; set; }

            [JsonProperty(En ? "Allow the Loot Table Stacksize GUI plugin to spawn items in this crate" : "Разрешить плагину Loot Table Stacksize GUI спавнить предметы в этом ящике")]
            public bool IsLootTablePlugin { get; set; }
        }

        private class BaseLootTableConfigOld
        {
            [JsonProperty(En ? "Clear the standard content of the crate" : "Отчистить стандартное содержимое крейта")]
            public bool ClearDefaultItemList { get; set; }

            [JsonProperty(En ? "Setting up loot from the loot table" : "Настройка лута из лутовой таблицы")]
            public PrefabLootTableConfigsOld PrefabConfigsOld { get; set; }

            [JsonProperty(En ? "Enable spawn of items from the list" : "Включить спавн предметов из списка")]
            public bool IsRandomItemsEnable { get; set; }

            [JsonProperty(En ? "Minimum numbers of items" : "Минимальное кол-во элементов")]
            public int MinItemsAmount { get; set; }

            [JsonProperty(En ? "Maximum numbers of items" : "Максимальное кол-во элементов")]
            public int MaxItemsAmount { get; set; }

            [JsonProperty(En ? "List of items" : "Список предметов")]
            public List<LootItemConfigOld> Items { get; set; }
        }

        private class PrefabLootTableConfigsOld
        {
            [JsonProperty(En ? "Enable spawn loot from prefabs" : "Включить спавн лута из префабов")]
            public bool IsEnable { get; set; }

            [JsonProperty(En ? "List of prefabs (one is randomly selected)" : "Список префабов (выбирается один рандомно)")]
            public List<PrefabConfigOld> Prefabs { get; set; }
        }

        private class PrefabConfigOld
        {
            [JsonProperty(En ? "Prefab displayName" : "Название префаба")]
            public string PrefabName { get; set; }

            [JsonProperty(En ? "Minimum Loot multiplier" : "Минимальный множитель лута")]
            public int MinLootScale { get; set; }

            [JsonProperty(En ? "Maximum Loot multiplier" : "Максимальный множитель лута")]
            public int MaxLootScale { get; set; }
        }

        private class LootItemConfigOld
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
        

        private class MarkerConfig
        {
            [JsonProperty(En ? "Do you use the Marker? [true/false]" : "Использовать ли маркер? [true/false]")]
            public bool Enable { get; set; }

            [JsonProperty(En ? "Use a shop marker? [true/false]" : "Добавить маркер магазина? [true/false]")]
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

        private class ZoneConfig
        {
            [JsonProperty(En ? "Create a PVP zone in the convoy stop zone? (only for those who use the TruePVE plugin)[true/false]" : "Создавать зону PVP в зоне проведения ивента? (только для тех, кто использует плагин TruePVE) [true/false]")]
            public bool IsPvpZone { get; set; }

            [JsonProperty(En ? "Use the dome? [true/false]" : "Использовать ли купол? [true/false]")]
            public bool IsDome { get; set; }

            [JsonProperty(En ? "Darkening the dome" : "Затемнение купола")]
            public int Darkening { get; set; }

            [JsonProperty(En ? "Use a colored border? [true/false]" : "Использовать цветную границу? [true/false]")]
            public bool IsColoredBorder { get; set; }

            [JsonProperty(En ? "Border color (0 - blue, 1 - green, 2 - purple, 3 - red)" : "Цвет границы (0 - синий, 1 - зеленый, 2 - фиолетовый, 3 - красный)")]
            public int BorderColor { get; set; }

            [JsonProperty(En ? "Brightness of the color border" : "Яркость цветной границы")]
            public int Brightness { get; set; }
        }

        private class NotifyConfig
        {
            [JsonProperty(En ? "Use a chat? [true/false]" : "Использовать ли чат? [true/false]")]
            public bool IsChatEnable { get; set; }

            [JsonProperty(En ? "The time until the end of the event, when a message is displayed about the time until the end of the event [sec]" : "Время до конца ивента, когда выводится сообщение о сокром окончании ивента [sec]")]
            public HashSet<int> TimeNotifications { get; set; }

            [JsonProperty(En ? "Facepunch Game Tips setting" : "Настройка сообщений Facepunch Game Tip")]
            public GameTipConfig GameTipConfig { get; set; }
        }

        private class GameTipConfig
        {
            [JsonProperty(En ? "Use Facepunch Game Tips (notification bar above hotbar)? [true/false]" : "Использовать ли Facepunch Game Tip (оповещения над слотами быстрого доступа игрока)? [true/false]")]
            public bool IsEnabled { get; set; }

            [JsonProperty(En ? "Style (0 - Blue Normal, 1 - Red Normal, 2 - Blue Long, 3 - Blue Short, 4 - Server Event)" : "Стиль (0 - Blue Normal, 1 - Red Normal, 2 - Blue Long, 3 - Blue Short, 4 - Server Event)")]
            public int Style { get; set; }
        }

        private class GUIConfig
        {
            [JsonProperty(En ? "Use the Countdown GUI? [true/false]" : "Использовать ли GUI обратного отсчета? [true/false]")]
            public bool IsEnable { get; set; }

            [JsonProperty(En ? "Vertical offset" : "Смещение по вертикали")]
            public int OffsetMinY { get; set; }
        }

        private class SupportedPluginsConfig
        {
            [JsonProperty(En ? "PVE Mode Setting" : "Настройка PVE Mode")]
            public PveModeConfig PveMode { get; set; }

            [JsonProperty(En ? "Economy Setting" : "Настройка экономики")]
            public EconomyConfig EconomicsConfig { get; set; }

            [JsonProperty(En ? "BetterNpc Setting" : "Настройка плагина BetterNpc")]
            public BetterNpcConfig BetterNpcConfig { get; set; }

            [JsonProperty(En ? "GUI Announcements setting" : "Настройка GUI Announcements")]
            public GUIAnnouncementsConfig GUIAnnouncementsConfig { get; set; }

            [JsonProperty(En ? "Notify setting" : "Настройка Notify")]
            public NotifyPluginConfig NotifyPluginConfig { get; set; }

            [JsonProperty(En ? "DiscordMessages setting" : "Настройка DiscordMessages")]
            public DiscordConfig DiscordMessagesConfig { get; set; }
            
            [JsonProperty(En ? "LifeSupport setting" : "Настройка LifeSupport")]
            public LifeSupportConfig LifeSupportConfig { get; set; }
        }

        private class PveModeConfig
        {
            [JsonProperty(En ? "Use the PVE mode of the plugin? [true/false]" : "Использовать PVE режим работы плагина? [true/false]")]
            public bool Enable { get; set; }

            [JsonProperty(En ? "The owner will immediately be the one who stopped the convoy" : "Владельцем сразу будет становиться тот, кто остановил конвой")]
            public bool OwnerIsStopper { get; set; }

            [JsonProperty(En ? "If a player has a cooldown and the event has NO OWNERS, then he will not be able to interact with the event? [true/false]" : "Если у игрока кулдаун, а у ивента НЕТ ВЛАДЕЛЬЦЕВ, то он не сможет взаимодействовать с ивентом? [true/false]")]
            public bool NoInteractIfCooldownAndNoOwners { get; set; }

            [JsonProperty(En ? "If a player has a cooldown, and the event HAS AN OWNER, then he will not be able to interact with the event, even if he is on a team with the owner? [true/false]" : "Если у игрока кулдаун, а у ивента ЕСТЬ ВЛАДЕЛЕЦ, то он не сможет взаимодействовать с ивентом, даже если находится в команде с владельцем? [true/false]")]
            public bool NoDealDamageIfCooldownAndTeamOwner { get; set; }

            [JsonProperty(En ? "Allow only the owner or his teammates to loot crates? [true/false]" : "Разрешить лутать ящики только владельцу или его тиммейтам? [true/false]")]
            public bool CanLootOnlyOwner { get; set; }

            [JsonProperty(En ? "Display the name of the event owner on a marker on the map? [true/false]" : "Отображать имя владелца ивента на маркере на карте? [true/false]")]
            public bool ShowEventOwnerNameOnMap { get; set; }

            [JsonProperty(En ? "The amount of damage that the player has to do to become the Event Owner" : "Кол-во урона, которое должен нанести игрок, чтобы стать владельцем ивента")]
            public float Damage { get; set; }

            [JsonProperty(En ? "Damage coefficients for calculate to become the Event Owner." : "Коэффициенты урона для подсчета, чтобы стать владельцем события.")]
            public Dictionary<string, float> ScaleDamage { get; set; }

            [JsonProperty(En ? "Can the non-owner of the event loot the crates? [true/false]" : "Может ли не владелец ивента грабить ящики? [true/false]")]
            public bool LootCrate { get; set; }

            [JsonProperty(En ? "Can the non-owner of the event hack locked crates? [true/false]" : "Может ли не владелец ивента взламывать заблокированные ящики? [true/false]")]
            public bool HackCrate { get; set; }

            [JsonProperty(En ? "Can the non-owner of the event loot NPC corpses? [true/false]" : "Может ли не владелец ивента грабить трупы NPC? [true/false]")]
            public bool LootNpc { get; set; }

            [JsonProperty(En ? "Can an Npc attack a non-owner of the event? [true/false]" : "Может ли Npc атаковать не владельца ивента? [true/false]")]
            public bool TargetNpc { get; set; }

            [JsonProperty(En ? "Can Bradley attack a non-owner of the event? [true/false]" : "Может ли Bradley атаковать не владельца ивента? [true/false]")]
            public bool TargetTank { get; set; }

            [JsonProperty(En ? "Can Turret attack a non-owner of the event? [true/false]" : "Может ли Турель атаковать не владельца ивента? [true/false]")]
            public bool TargetTurret { get; set; }

            [JsonProperty(En ? "Can Helicopter attack a non-owner of the event? [true/false]" : "Может ли Вертолет атаковать не владельца ивента? [true/false]")]
            public bool TargetHeli { get; set; }

            [JsonProperty(En ? "Can the non-owner of the event do damage to Bradley? [true/false]" : "Может ли не владелец ивента наносить урон по Bradley? [true/false]")]
            public bool DamageTank { get; set; }

            [JsonProperty(En ? "Can the non-owner of the event deal damage to the NPC? [true/false]" : "Может ли не владелец ивента наносить урон по NPC? [true/false]")]
            public bool DamageNpc { get; set; }

            [JsonProperty(En ? "Can the non-owner of the event do damage to Helicopter? [true/false]" : "Может ли не владелец ивента наносить урон по Вертолету? [true/false]")]
            public bool DamageHeli { get; set; }

            [JsonProperty(En ? "Can the non-owner of the event do damage to Turret? [true/false]" : "Может ли не владелец ивента наносить урон по Турелям? [true/false]")]
            public bool DamageTurret { get; set; }

            [JsonProperty(En ? "Allow the non-owner of the event to enter the event zone? [true/false]" : "Разрешать входить внутрь зоны ивента не владельцу ивента? [true/false]")]
            public bool CanEnter { get; set; }

            [JsonProperty(En ? "Allow a player who has an active cooldown of the Event Owner to enter the event zone? [true/false]" : "Разрешать входить внутрь зоны ивента игроку, у которого активен кулдаун на получение статуса владельца ивента? [true/false]")]
            public bool CanEnterCooldownPlayer { get; set; }

            [JsonProperty(En ? "The time that the Event Owner may not be inside the event zone [sec.]" : "Время, которое владелец ивента может не находиться внутри зоны ивента [сек.]")]
            public int TimeExitOwner { get; set; }

            [JsonProperty(En ? "The time until the end of Event Owner status when it is necessary to warn the player [sec.]" : "Время таймера до окончания действия статуса владельца ивента, когда необходимо предупредить игрока [сек.]")]
            public int AlertTime { get; set; }

            [JsonProperty(En ? "Prevent the actions of the RestoreUponDeath plugin in the event zone? [true/false]" : "Запрещать работу плагина RestoreUponDeath в зоне действия ивента? [true/false]")]
            public bool RestoreUponDeath { get; set; }

            [JsonProperty(En ? "The time that the player can`t become the Event Owner, after the end of the event and the player was its owner [sec.]" : "Время, которое игрок не сможет стать владельцем ивента, после того как ивент окончен и игрок был его владельцем [sec.]")]
            public double Cooldown { get; set; }
        }

        private class EconomyConfig
        {
            [JsonProperty(En ? "Enable economy" : "Включить экономику?")]
            public bool Enable { get; set; }

            [JsonProperty(En ? "Which economy plugins do you want to use? (Economics, Server Rewards, IQEconomic)" : "Какие плагины экономики вы хотите использовать? (Economics, Server Rewards, IQEconomic)")]
            public HashSet<string> Plugins { get; set; }

            [JsonProperty(En ? "The minimum value that a player must collect to get points for the economy" : "Минимальное значение, которое игрок должен заработать, чтобы получить баллы за экономику")]
            public double MinEconomyPiont { get; set; }

            [JsonProperty(En ? "The minimum value that a winner must collect to make the commands work" : "Минимальное значение, которое победитель должен заработать, чтобы сработали команды")]
            public double MinCommandPoint { get; set; }

            [JsonProperty(En ? "Looting of crates" : "Ограбление ящиков")]
            public Dictionary<string, double> Crates { get; set; }

            [JsonProperty(En ? "Killing an NPC" : "Убийство NPC")]
            public double NpcPoint { get; set; }

            [JsonProperty(En ? "Killing an Bradley" : "Уничтожение Bradley")]
            public double BradleyPoint { get; set; }

            [JsonProperty(En ? "Killing an Heli" : "Уничтожение вертолета")]
            public double HeliPoint { get; set; }

            [JsonProperty(En ? "Killing an sedan" : "Уничтожение седана")]
            public double SedanPoint { get; set; }

            [JsonProperty(En ? "Killing an mpdular Car" : "Уничтожение модульной машины")]
            public double ModularCarPoint { get; set; }

            [JsonProperty(En ? "Killing a turret" : "Уничтожение турели")]
            public double TurretPoint { get; set; }

            [JsonProperty(En ? "Killing a Samsite" : "Уничтожение Samsite")]
            public double SamsitePoint { get; set; }

            [JsonProperty(En ? "Hacking a locked crate" : "Взлом заблокированного ящика")]
            public double LockedCratePoint { get; set; }

            [JsonProperty(En ? "List of commands that are executed in the console at the end of the event ({steamid} - the player who collected the highest number of points)" : "Список команд, которые выполняются в консоли по окончанию ивента ({steamid} - игрок, который набрал наибольшее кол-во баллов)")]
            public HashSet<string> Commands { get; set; }
        }

        private class GUIAnnouncementsConfig
        {
            [JsonProperty(En ? "Do you use the GUI Announcements? [true/false]" : "Использовать ли GUI Announcements? [true/false]")]
            public bool IsEnabled { get; set; }

            [JsonProperty(En ? "Banner color" : "Цвет баннера")]
            public string BannerColor { get; set; }

            [JsonProperty(En ? "Text color" : "Цвет текста")]
            public string TextColor { get; set; }

            [JsonProperty(En ? "Adjust Vertical Position" : "Отступ от верхнего края")]
            public float APIAdjustVPosition { get; set; }
        }

        private class NotifyPluginConfig
        {
            [JsonProperty(En ? "Do you use the Notify? [true/false]" : "Использовать ли Notify? [true/false]")]
            public bool IsEnabled { get; set; }

            [JsonProperty(En ? "Type" : "Тип")]
            public int Type { get; set; }
        }

        private class DiscordConfig
        {
            [JsonProperty(En ? "Do you use the Discord? [true/false]" : "Использовать ли Discord? [true/false]")]
            public bool IsEnabled { get; set; }

            [JsonProperty("Webhook URL")]
            public string WebhookUrl;

            [JsonProperty(En ? "Embed Color (DECIMAL)" : "Цвет полосы (DECIMAL)")]
            public int EmbedColor { get; set; }

            [JsonProperty(En ? "Keys of required messages" : "Ключи необходимых сообщений")]
            public HashSet<string> Keys { get; set; }
        }

        private class BetterNpcConfig
        {
            [JsonProperty(En ? "Allow Npc spawn after destroying Bradley" : "Разрешить спавн Npc после уничтожения Бредли")]
            public bool BradleyNpc { get; set; }

            [JsonProperty(En ? "Allow Npc spawn after destroying Heli" : "Разрешить спавн Npc после уничтожения Вертолета")]
            public bool HeliNpc { get; set; }
        }

        private class LifeSupportConfig
        {
            [JsonProperty(En ? "Disable the LifeSupport plugin in the event zone? [true/false]" : "Запретить работу плагина LifeSupport в зоне ивента? [true/false]")]
            public bool IsDisabled { get; set; }
        }

        private class PluginConfig
        {
            [JsonProperty(En ? "Version" : "Версия плагина")]
            public string Version { get; set; }

            [JsonProperty(En ? "Prefix of chat messages" : "Префикс в чате")]
            public string Prefix { get; set; }

            [JsonProperty(En ? "Main Setting" : "Основные настройки")]
            public MainConfig MainConfig { get; set; }

            [JsonProperty(En ? "Behavior Settings" : "Настройка поведения")]
            public BehaviorConfig BehaviorConfig { get; set; }

            [JsonProperty(En ? "Loot Settings" : "Настройки Лута")]
            public LootConfig LootConfig { get; set; }

            [JsonProperty(En ? "Route Settings" : "Настройки маршрутов")]
            public PathConfig PathConfig { get; set; }

            [JsonProperty(En ? "Convoy Presets" : "Пресеты конвоя")]
            public HashSet<EventConfig> EventConfigs { get; set; }

            [JsonProperty(En ? "Travelling Vendor Configurations" : "Кофигурации фургонов торговцев")]
            public HashSet<TravellingVendorConfig> TravelingVendorConfigs { get; set; }

            [JsonProperty(En ? "Modular Configurations" : "Кофигурации модульных машин")]
            public HashSet<ModularCarConfig> ModularCarConfigs { get; set; }

            [JsonProperty(En ? "Bradley Configurations" : "Кофигурации бредли")]
            public HashSet<BradleyConfig> BradleyConfigs { get; set; }

            [JsonProperty(En ? "Sedan Configurations" : "Кофигурации седанов")]
            public HashSet<SedanConfig> SedanConfigs { get; set; }

            [JsonProperty(En ? "Bike Configurations" : "Кофигурации мотоциклов")]
            public HashSet<BikeConfig> BikeConfigs { get; set; }

            [JsonProperty(En ? "Karuza Car Configurations" : "Кофигурации автомобилей Karuza")]
            public HashSet<KaruzaCarConfig> KaruzaCarConfigs { get; set; }

            [JsonProperty(En ? "Heli Configurations" : "Кофигурации вертолетов")]
            public HashSet<HeliConfig> HeliConfigs { get; set; }

            [JsonProperty(En ? "Turret Configurations" : "Кофигурации турелей")]
            public HashSet<TurretConfig> TurretConfigs { get; set; }

            [JsonProperty(En ? "SamSite Configurations" : "Кофигурации SamSite")]
            public HashSet<SamSiteConfig> SamsiteConfigs { get; set; }

            [JsonProperty(En ? "Crate presets" : "Пресеты ящиков")]
            public HashSet<CrateConfig> CrateConfigs { get; set; }

            [JsonProperty(En ? "NPC Configurations" : "Кофигурации NPC")]
            public HashSet<NpcConfig> NpcConfigs { get; set; }

            [JsonProperty(En ? "Marker Setting" : "Настройки маркера")]
            public MarkerConfig MarkerConfig { get; set; }

            [JsonProperty(En ? "Event zone" : "Настройка зоны ивента")]
            public ZoneConfig ZoneConfig { get; set; }

            [JsonProperty(En ? "Notification Settings" : "Настройки уведомлений")]
            public NotifyConfig NotifyConfig { get; set; }

            [JsonProperty("GUI")]
            public GUIConfig GUIConfig { get; set; }

            [JsonProperty(En ? "Supported Plugins" : "Поддерживаемые плагины")]
            public SupportedPluginsConfig SupportedPluginsConfig { get; set; }

            // ReSharper disable All
            public static PluginConfig DefaultConfig()
            {
                return new PluginConfig()
                {
                    Version = "2.9.6",
                    Prefix = "[Convoy]",
                    MainConfig = new MainConfig
                    {
                        IsAutoEvent = false,
                        MinTimeBetweenEvents = 3600,
                        MaxTimeBetweenEvents = 3600,
                        PreStartTime = 0,
                        EnableStartStopLogs = false,
                        DontStopEventIfPlayerInZone = false,
                        IsTurretDropWeapon = false,
                        KillEventAfterLoot = true,
                        EndAfterLootTime = 300,
                        FixedSchedule = new FixedScheduleConfig
                        {
                            Enabled = false,
                            UtcOffsetHours = 3,
                            Windows = new List<FixedScheduleWindowConfig>
                            {
                                new FixedScheduleWindowConfig { Start = "03:00", End = "04:00" },
                                new FixedScheduleWindowConfig { Start = "09:00", End = "10:00" },
                                new FixedScheduleWindowConfig { Start = "15:00", End = "16:00" },
                                new FixedScheduleWindowConfig { Start = "18:00", End = "19:00" },
                                new FixedScheduleWindowConfig { Start = "21:00", End = "22:00" }
                            }
                        }
                    },
                    BehaviorConfig = new BehaviorConfig
                    {
                        AggressiveTime = 80,
                        IsStopConvoyAggressive = true,
                        StopTime = 80,
                        IsPlayerTurretEnable = true
                    },
                    LootConfig = new LootConfig
                    {
                        DropLoot = true,
                        LootLossPercent = 0.5f,
                        BlockLootingByMove = false,
                        BlockLootingByNpcs = false,
                        BlockLootingByBradleys = false,
                        BlockLootingByHeli = false
                    },
                    PathConfig = new PathConfig
                    {
                        PathType = 1,
                        MinRoadLength = 200,
                        BlockRoads = new HashSet<int>(),
                        RegularPathConfig = new RegularPathConfig
                        {
                            IsRingRoad = true
                        },
                        ComplexPathConfig = new ComplexPathConfig
                        {
                            ChooseLongestRoute = true,
                            MinRoadCount = 3
                        },
                        CustomPathConfig = new CustomPathConfig
                        {
                            CustomRoutesPresets = new List<string>()
                        }
                    },
                    EventConfigs = new HashSet<EventConfig>
                    {
                        new EventConfig
                        {
                            PresetName = "easy",
                            DisplayName = En ? "Easy Convoy" : "Легкий конвой",
                            IsAutoStart = true,
                            Chance = 40,
                            MinTimeAfterWipe = 0,
                            MaxTimeAfterWipe = -1,
                            EventTime = 3600,
                            ZoneRadius = 50,
                            MaxGroundDamageDistance = 50,
                            MaxHeliDamageDistance = 300,
                            VehiclesOrder = new List<string>
                            {
                                "motorbike_easy",
                                "motorbike_sidecar_easy",
                                "sedan_easy",
                                "motorbike_sidecar_easy",
                                "motorbike_easy"
                            },
                            IsHeli = false,
                            HeliPreset = "",
                            WeaponToScaleDamageNpc = new Dictionary<string, float>
                            {
                                ["grenade.beancan.deployed"] = 0.5f,
                                ["grenade.f1.deployed"] = 0.5f,
                                ["explosive.satchel.deployed"] = 0.5f,
                                ["explosive.timed.deployed"] = 0.5f,
                                ["rocket_hv"] = 0.5f,
                                ["rocket_basic"] = 0.5f,
                                ["40mm_grenade_he"] = 0.5f,
                            },
                        },
                        new EventConfig
                        {
                            PresetName = "medium",
                            DisplayName = En ? "Medium Convoy" : "Средний Конвой",
                            IsAutoStart = true,
                            Chance = 30,
                            MinTimeAfterWipe = 0,
                            MaxTimeAfterWipe = -1,
                            EventTime = 3600,
                            ZoneRadius = 60,
                            MaxGroundDamageDistance = 100,
                            MaxHeliDamageDistance = 300,
                            VehiclesOrder = new List<string>
                            {
                                "motorbike_sidecar_medium",
                                "sedan_medium",
                                "modular_npc_medium",
                                "bradley_medium",
                                "modular_npc_medium",
                                "sedan_medium",
                                "motorbike_sidecar_medium"
                            },
                            IsHeli = false,
                            HeliPreset = "",
                            WeaponToScaleDamageNpc = new Dictionary<string, float>
                            {
                                ["grenade.beancan.deployed"] = 0.5f,
                                ["grenade.f1.deployed"] = 0.5f,
                                ["explosive.satchel.deployed"] = 0.5f,
                                ["explosive.timed.deployed"] = 0.5f,
                                ["rocket_hv"] = 0.5f,
                                ["rocket_basic"] = 0.5f,
                                ["40mm_grenade_he"] = 0.5f,
                            },
                        },
                        new EventConfig
                        {
                            PresetName = "hard",
                            DisplayName = En ? "Hard Convoy" : "Тяжелый Конвой",
                            IsAutoStart = true,
                            Chance = 20,
                            MinTimeAfterWipe = 0,
                            MaxTimeAfterWipe = -1,
                            EventTime = 3600,
                            ZoneRadius = 70,
                            MaxGroundDamageDistance = 100,
                            MaxHeliDamageDistance = 300,
                            VehiclesOrder = new List<string>
                            {
                                "sedan_hard",
                                "modular_npc_long_hard",
                                "modular_sniper_hard",
                                "bradley_hard",
                                "vendor_loot_hard",
                                "vendor_samsite_hard",
                                "bradley_hard",
                                "modular_sniper_hard",
                                "modular_npc_long_hard",
                                "sedan_hard"
                            },
                            IsHeli = true,
                            HeliPreset = "heli_hard",
                            WeaponToScaleDamageNpc = new Dictionary<string, float>
                            {
                                ["grenade.beancan.deployed"] = 0.5f,
                                ["grenade.f1.deployed"] = 0.5f,
                                ["explosive.satchel.deployed"] = 0.5f,
                                ["explosive.timed.deployed"] = 0.5f,
                                ["rocket_hv"] = 0.5f,
                                ["rocket_basic"] = 0.5f,
                                ["40mm_grenade_he"] = 0.5f,
                            },
                        },
                        new EventConfig
                        {
                            PresetName = "nightmare",
                            DisplayName = En ? "Nightmarish Convoy" : "Кошмарный Конвой",
                            IsAutoStart = false,
                            Chance = 10,
                            MinTimeAfterWipe = 0,
                            MaxTimeAfterWipe = -1,
                            EventTime = 3600,
                            ZoneRadius = 100,
                            MaxGroundDamageDistance = 120,
                            MaxHeliDamageDistance = 300,
                            VehiclesOrder = new List<string>
                            {
                                "bradley_samsite_nightmare",
                                "modular_npc_long_nightmare",
                                "vendor_turret_nightmare",
                                "vendor_samsite_nightmare",
                                "bradley_samsite_nightmare",
                                "sedan_nightmare",
                                "sedan_nightmare",
                                "bradley_samsite_nightmare",
                                "vendor_samsite_nightmare",
                                "vendor_turret_nightmare",
                                "modular_npc_long_nightmare",
                                "bradley_samsite_nightmare",
                            },
                            IsHeli = true,
                            HeliPreset = "heli_nightmare",
                            WeaponToScaleDamageNpc = new Dictionary<string, float>
                            {
                                ["grenade.beancan.deployed"] = 0.1f,
                                ["grenade.f1.deployed"] = 0.1f,
                                ["explosive.satchel.deployed"] = 0.1f,
                                ["explosive.timed.deployed"] = 0.1f,
                                ["rocket_hv"] = 0.1f,
                                ["rocket_basic"] = 0.1f,
                                ["40mm_grenade_he"] = 0.1f
                            },
                        }
                    },
                    TravelingVendorConfigs = new HashSet<TravellingVendorConfig>
                    {
                        new TravellingVendorConfig
                        {
                            PresetName = "vendor_loot_hard",
                            NpcPresetName = "",
                            DeleteMapMarker = false,
                            IsLocked = true,
                            DoorHealth = 250,
                            DoorSkin = 934924536,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_minigun_hard",
                                    Position = "(0, 0.85, -1.25)",
                                    Rotation = "(0, 180, 0)"
                                },
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "frige_safe_hard",
                                    Position = "(0, 0, -1.85)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "turret_minigun_hard",
                                    Position = "(0, 1.78, 0.818)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "turret_minigun_hard",
                                    Position = "(0, 1.78, -1.707)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new TravellingVendorConfig
                        {
                            PresetName = "vendor_samsite_hard",
                            NpcPresetName = "",
                            DeleteMapMarker = false,
                            IsLocked = true,
                            DoorHealth = 250,
                            DoorSkin = 934924536,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_flamethrower_hard",
                                    Position = "(0, 0.85, -1.25)",
                                    Rotation = "(0, 180, 0)"
                                },
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "frige_safe_hard",
                                    Position = "(0, 0, -1.85)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "turret_minigun_hard",
                                    Position = "(0, 1.78, 0.818)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            SamSiteLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "samsite_default",
                                    Position = "(0, 1, -1)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                        },
                        new TravellingVendorConfig
                        {
                            PresetName = "vendor_turret_nightmare",
                            NpcPresetName = "",
                            DeleteMapMarker = false,
                            IsLocked = true,
                            DoorHealth = 250,
                            DoorSkin = 934924536,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_minigun_hard",
                                    Position = "(0, 0.85, -1.25)",
                                    Rotation = "(0, 180, 0)"
                                },
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "frige_safe_nightmare",
                                    Position = "(0, 0, -1.85)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "turret_minigun_nightmare",
                                    Position = "(0, 1.78, 0.818)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "turret_minigun_nightmare",
                                    Position = "(0, 1.78, -1.707)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new TravellingVendorConfig
                        {
                            PresetName = "vendor_samsite_nightmare",
                            NpcPresetName = "",
                            DeleteMapMarker = false,
                            IsLocked = false,
                            DoorHealth = 250,
                            DoorSkin = 934924536,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_flamethrower_hard",
                                    Position = "(0.5, 0.85, -1.25)",
                                    Rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_smoke_nightmare",
                                    Position = "(-0.5, 0.85, -1.25)",
                                    Rotation = "(-1, 180, 0)"
                                },
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "frige_safe_nightmare",
                                    Position = "(0, 0, -1.85)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "turret_minigun_nightmare",
                                    Position = "(0, 1.78, 0.818)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            SamSiteLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "samsite_default",
                                    Position = "(0, 1, -1)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                        }
                    },
                    ModularCarConfigs = new HashSet<ModularCarConfig>
                    {
                        new ModularCarConfig
                        {
                            PresetName = "modular_npc_medium",
                            DamageScale = 1,
                            Modules = new List<string>
                            {
                                "vehicle.1mod.engine",
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.storage"
                            },
                            NpcPresetName = "carnpc_lr300_raid_medium",
                            NumberOfNpc = 2,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_lr300_medium",
                                    Position = "(-0.9, 0.85, -1.5)",
                                    Rotation = "(0, 270, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_lr300_medium",
                                    Position = "(0.9, 0.85, -1.5)",
                                    Rotation = "(0, 90, 0)"
                                }
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_normal_medium",
                                    Position = "(0, 1.5, -1.5)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },

                        new ModularCarConfig
                        {
                            PresetName = "modular_sniper_hard",
                            DamageScale = 1,
                            Modules = new List<string>
                            {
                                "vehicle.1mod.flatbed",
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.storage"
                            },
                            NpcPresetName = "carnpc_grenadelauncher_hard",
                            NumberOfNpc = 2,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_bolt_hard",
                                    Position = "(-0.9, 0.85, -1.5)",
                                    Rotation = "(0, 270, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "carnpc_bolt_hard",
                                    Position = "(0.9, 0.85, -1.5)",
                                    Rotation = "(0, 90, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/testseat.prefab",
                                    IsDismount = false,
                                    NpcPresetName = "carnpc_bolt_hard",
                                    Position = "(0, 0.8, 1.5)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_normal_hard",
                                    Position = "(0, 1.5, -1.5)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new ModularCarConfig
                        {
                            PresetName = "modular_npc_long_hard",
                            DamageScale = 1,
                            Modules = new List<string>
                            {
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.2mod.passengers",
                                "vehicle.1mod.rear.seats"
                            },
                            NpcPresetName = "carnpc_lr300_raid_hard",
                            NumberOfNpc = 10,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>(),
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },

                        new ModularCarConfig
                        {
                            PresetName = "modular_npc_long_nightmare",
                            DamageScale = 1,
                            Modules = new List<string>
                            {
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.rear.seats",
                                "vehicle.1mod.rear.seats",
                                "vehicle.1mod.rear.seats"
                            },
                            NpcPresetName = "carnpc_ak_raid_nightmare",
                            NumberOfNpc = 8,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>(),
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new ModularCarConfig
                        {
                            PresetName = "modular_fuel_nightmare",
                            DamageScale = 1,
                            Modules = new List<string>
                            {
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.rear.seats",
                                "vehicle.2mod.fuel.tank"
                            },
                            NpcPresetName = "carnpc_ak_raid_nightmare_3",
                            NumberOfNpc = 4,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_normal_hard",
                                    Position = "(0, 0.5, -1.5)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        }
                    },
                    BradleyConfigs = new HashSet<BradleyConfig>
                    {
                        new BradleyConfig
                        {
                            PresetName = "bradley_medium",
                            Hp = 600f,
                            BradleyBuildingDamageScale = -1,
                            ViewDistance = 100.0f,
                            SearchDistance = 100.0f,
                            CoaxAimCone = 1.5f,
                            CoaxFireRate = 0.7f,
                            CoaxBurstLength = 5,
                            NextFireTime = 15f,
                            TopTurretFireRate = 0.25f,
                            CountCrates = 3,
                            InstCrateOpen = true,
                            NpcPresetName = "biker_grenadelauncher_medium",
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "",
                                    Position = "(0.624, 1.4, -3)",
                                    Rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "",
                                    Position = "(-0.624, 1.4, -3)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>(),
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                            
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 3,
                                    MaxItemsAmount = 3,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.mlrs",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle.explosive",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 20,
                                            MaxAmount = 40,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.fire",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.hv",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.seeker",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.basic",
                                            Skin = 0,
                                            Chance = 10,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new BradleyConfig
                        {
                            PresetName = "bradley_hard",
                            Hp = 600f,
                            BradleyBuildingDamageScale = -1,
                            ViewDistance = 100.0f,
                            SearchDistance = 100.0f,
                            CoaxAimCone = 1.5f,
                            CoaxFireRate = 0.7f,
                            CoaxBurstLength = 5,
                            NextFireTime = 15f,
                            TopTurretFireRate = 0.25f,
                            CountCrates = 3,
                            InstCrateOpen = true,
                            NpcPresetName = "carnpc_grenadelauncher_hard",
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "",
                                    Position = "(0.624, 1.4, -3)",
                                    Rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "",
                                    Position = "(-0.624, 1.4, -3)",
                                    Rotation = "(0, 180, 0)"
                                }
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>(),
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                            
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 4,
                                    MaxItemsAmount = 4,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.fire",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.hv",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "explosive.timed",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.grenadelauncher.he",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 10,
                                            MaxAmount = 15,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.basic",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.mlrs",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle.explosive",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 40,
                                            MaxAmount = 60,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new BradleyConfig
                        {
                            PresetName = "bradley_samsite_nightmare",
                            Hp = 1000,
                            BradleyBuildingDamageScale = -1,
                            ViewDistance = 100.0f,
                            SearchDistance = 100.0f,
                            CoaxAimCone = 1.1f,
                            CoaxFireRate = 1f,
                            CoaxBurstLength = 10,
                            NextFireTime = 10,
                            TopTurretFireRate = 0.25f,
                            CountCrates = 3,
                            InstCrateOpen = true,
                            NpcPresetName = "carnpc_ak_raid_nightmare_2",
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "",
                                    Position = "(0.624, 1.4, -3)",
                                    Rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "",
                                    Position = "(-0.624, 1.4, -3)",
                                    Rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "",
                                    Position = "(1.75, 1.234, 1.4)",
                                    Rotation = "(0, 90, 0)"
                                }
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>(),
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "samsite_default",
                                    Position = "(0.238, 1.5, -0.29)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 4,
                                    MaxItemsAmount = 4,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.fire",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.hv",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "explosive.timed",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.grenadelauncher.he",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 10,
                                            MaxAmount = 15,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.basic",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.mlrs",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle.explosive",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 40,
                                            MaxAmount = 60,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        }
                    },
                    SedanConfigs = new HashSet<SedanConfig>
                    {
                        new SedanConfig
                        {
                            PresetName = "sedan_easy",
                            Hp = 500f,
                            NpcPresetName = "sedan_npc_easy",
                            NumberOfNpc = 1,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_normal_weapon_easy",
                                    Position = "(0, 1.734, 0.55)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_normal_weapon_easy",
                                    Position = "(0, 1.734, -0.35)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_normal_explosive_easy",
                                    Position = "(0, 1.229, -1.780)",
                                    Rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(-0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new SedanConfig
                        {
                            PresetName = "sedan_medium",
                            Hp = 500f,
                            NpcPresetName = "carnpc_shotgunm4_medium",
                            NumberOfNpc = 2,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_elite_explosive_medium",
                                    Position = "(0, 1.734, 0.55)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_elite_weapon_medium",
                                    Position = "(0, 1.734, -0.35)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_normal_explosive_medium",
                                    Position = "(0, 1.229, -1.780)",
                                    Rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(-0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new SedanConfig
                        {
                            PresetName = "sedan_hard",
                            Hp = 500f,
                            NpcPresetName = "carnpc_lr300_hard",
                            NumberOfNpc = 2,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_elite_explosive_hard",
                                    Position = "(0, 1.734, 0.55)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_elite_weapon_hard",
                                    Position = "(0, 1.734, -0.35)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_normal_explosive_hard",
                                    Position = "(0, 1.229, -1.780)",
                                    Rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(-0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new SedanConfig
                        {
                            PresetName = "sedan_nightmare",
                            Hp = 500f,
                            NpcPresetName = "carnpc_ak_raid_nightmare_3",
                            NumberOfNpc = 2,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_elite_explosive_hard",
                                    Position = "(0, 1.734, 0.55)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_elite_weapon_hard",
                                    Position = "(0, 1.734, -0.35)",
                                    Rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_normal_explosive_hard",
                                    Position = "(0, 1.229, -1.780)",
                                    Rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_invisible_fuel_easy",
                                    Position = "(-0.833, 1.09, -2.019)",
                                    Rotation = "(85.057, 0, 0)"
                                },
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        }
                    },
                    BikeConfigs = new HashSet<BikeConfig>
                    {
                        new BikeConfig
                        {
                            PresetName = "motorbike_easy",
                            PrefabName = "assets/content/vehicles/bikes/motorbike.prefab",
                            Hp = 300,
                            NpcPresetName = "biker_m92_easy_2",
                            NumberOfNpc = 1,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>(),
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new BikeConfig
                        {
                            PresetName = "motorbike_sidecar_easy",
                            PrefabName = "assets/content/vehicles/bikes/motorbike_sidecar.prefab",
                            Hp = 350,
                            NpcPresetName = "biker_m92_easy_1",
                            NumberOfNpc = 2,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "biker_spas12_easy",
                                    Position = "(0, 0.45, -0.45)",
                                    Rotation = "(15, 0, 0)"
                                }
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_basic_techparts_easy",
                                    Position = "(0.715, 0.354, -0.712)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        },

                        new BikeConfig
                        {
                            PresetName = "motorbike_sidecar_medium",
                            PrefabName = "assets/content/vehicles/bikes/motorbike_sidecar.prefab",
                            Hp = 350,
                            NpcPresetName = "biker_mp5_medium",
                            NumberOfNpc = 2,
                            AdditionalNpc = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    IsEnable = true,
                                    SeatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    IsDismount = true,
                                    NpcPresetName = "biker_grenadelauncher_medium",
                                    Position = "(0, 0.45, -0.45)",
                                    Rotation = "(15, 0, 0)"
                                }
                            },
                            CrateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    PresetName = "crate_basic_resources_medium",
                                    Position = "(0.715, 0.354, -0.712)",
                                    Rotation = "(0, 0, 0)"
                                }
                            },
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>(),
                        }
                    },
                    KaruzaCarConfigs = new HashSet<KaruzaCarConfig>
                    {
                        new KaruzaCarConfig
                        {
                            PresetName = "duneBuggie",
                            PrefabName = "assets/custom/dunebuggie.prefab",
                            NpcPresetName = "biker_m92_easy_2",
                            NumberOfNpc = 1,
                            AdditionalNpc = new HashSet<NpcPoseConfig>(),
                            CrateLocations = new HashSet<PresetLocationConfig>(),
                            TurretLocations = new HashSet<PresetLocationConfig>(),
                            SamSiteLocations = new HashSet<PresetLocationConfig>()
                        }
                    },
                    TurretConfigs = new HashSet<TurretConfig>
                    {
                        new TurretConfig
                        {
                            PresetName = "turret_minigun_hard",
                            Hp = 500f,
                            ShortNameWeapon = "minigun",
                            ShortNameAmmo = "ammo.rifle",
                            CountAmmo = 500,
                            TargetDetectionRange = 100,
                            TargetLossRange = 0
                        },
                        new TurretConfig
                        {
                            PresetName = "turret_minigun_nightmare",
                            Hp = 500f,
                            ShortNameWeapon = "minigun",
                            ShortNameAmmo = "ammo.rifle",
                            CountAmmo = 800,
                            TargetDetectionRange = 130,
                            TargetLossRange = 0
                        }
                    },
                    SamsiteConfigs = new HashSet<SamSiteConfig>
                    {
                        new SamSiteConfig
                        {
                            PresetName = "samsite_default",
                            Hp = 1000,
                            CountAmmo = 100
                        }
                    },
                    CrateConfigs = new HashSet<CrateConfig>
                    {
                        new CrateConfig
                        {
                            PresetName = "crate_normal_weapon_easy",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "pistol.m92",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "shotgun.spas12",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.thompson",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.semiauto",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.2",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_explosive_easy",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 4,
                                    MaxItemsAmount = 4,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.silencer",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.extendedmags",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.beancan",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "explosive.satchel",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.molotov",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.f1",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_basic_techparts_easy",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_basic.prefab",
                            Skin = 0,
                            HackTime = -1,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "scrap",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 50,
                                            MaxAmount = 100,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_invisible_fuel_easy",
                            PrefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_basic.prefab",
                            Skin = 0,
                            HackTime = -1,
                             LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 2,
                                    MaxItemsAmount = 2,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "crude.oil",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 100,
                                            MaxAmount = 200,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "lowgradefuel",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 100,
                                            MaxAmount = 200,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },

                        new CrateConfig
                        {
                            PresetName = "crate_basic_resources_medium",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_basic.prefab",
                            Skin = 0,
                            HackTime = -1,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 9,
                                    MaxItemsAmount = 9,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "scrap",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 100,
                                            MaxAmount = 300,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "diesel_barrel",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "cloth",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 200,
                                            MaxAmount = 300,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "leather",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 100,
                                            MaxAmount = 200,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "charcoal",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 2000,
                                            MaxAmount = 4000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "gunpowder",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 250,
                                            MaxAmount = 750,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "sulfur",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 750,
                                            MaxAmount = 1250,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.refined",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 75,
                                            MaxAmount = 125,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.fragments",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1500,
                                            MaxAmount = 3000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_explosive_medium",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 4,
                                    MaxItemsAmount = 4,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.silencer",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.extendedmags",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.molotov",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.f1",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.flashbang",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.holosight",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_elite_weapon_medium",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            HackTime = 0,
                             LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 2,
                                    MaxItemsAmount = 2,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.ak",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.bolt",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.thompson",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.semiauto",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.2",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.lr300",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.mp5",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "hmlmg",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_elite_explosive_medium",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 3,
                                    MaxItemsAmount = 3,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.mlrs",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle.explosive",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 20,
                                            MaxAmount = 40,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.fire",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.hv",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.seeker",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.basic",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 2,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_invisible_normal_medium",
                            PrefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_normal.prefab",
                            Skin = 0,
                            HackTime = -1,
                             LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 6,
                                    MaxItemsAmount = 6,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.ak",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.bolt",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "hmlmg",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.thompson",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.semiauto",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.2",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.mp5",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 64,
                                            MaxAmount = 128,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "roadsign.kilt",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "syringe.medical",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 5,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.facemask",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.plate.torso",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },

                        new CrateConfig
                        {
                            PresetName = "crate_invisible_normal_hard",
                            PrefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_normal_2.prefab",
                            Skin = 0,
                            HackTime = -1,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 9,
                                    MaxItemsAmount = 9,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "lmg.m249",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.l96",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.bolt",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.mp5",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.lr300",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.ak",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 5,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "roadsign.kilt",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "black.raspberries",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 10,
                                            MaxAmount = 15,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.facemask",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.plate.torso",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.plate.torso",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 128,
                                            MaxAmount = 256,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "syringe.medical",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 10,
                                            MaxAmount = 20,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_elite_explosive_hard",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            HackTime = 0,
                             LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 4,
                                    MaxItemsAmount = 4,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.fire",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.hv",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "explosive.timed",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.grenadelauncher.he",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 10,
                                            MaxAmount = 15,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.basic",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.mlrs",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 2,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle.explosive",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 40,
                                            MaxAmount = 60,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_elite_weapon_hard",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            HackTime = 0,
                             LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 3,
                                    MaxItemsAmount = 3,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "lmg.m249",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.l96",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.lr300",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.mp5",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.ak",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.bolt",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "crate_normal_explosive_hard",
                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            HackTime = 0,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 4,
                                    MaxItemsAmount = 4,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.silencer",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.extendedmags",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.molotov",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.f1",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "grenade.flashbang",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 3,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "weapon.mod.holosight",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new CrateConfig
                        {
                            PresetName = "frige_safe_hard",
                            PrefabName = "assets/prefabs/deployable/fridge/fridge.deployed.prefab",
                            Skin = 3005880420,
                            HackTime = -1,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = false,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 9,
                                    MaxItemsAmount = 9,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "scrap",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 500,
                                            MaxAmount = 1500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "diesel_barrel",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 5,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "cloth",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1000,
                                            MaxAmount = 1500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "leather",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 500,
                                            MaxAmount = 1000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "charcoal",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 10000,
                                            MaxAmount = 20000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "gunpowder",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 2000,
                                            MaxAmount = 3500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "sulfur",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 2500,
                                            MaxAmount = 5000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.refined",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 350,
                                            MaxAmount = 500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.fragments",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 7500,
                                            MaxAmount = 15000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },

                        new CrateConfig
                        {
                            PresetName = "crate_invisible_normal_nightmare",
                            PrefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_normal_2.prefab",
                            Skin = 0,
                            HackTime = -1,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 9,
                                    MaxItemsAmount = 9,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "lmg.m249",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.l96",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.bolt",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.mp5",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.lr300",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.ak",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 5,
                                            MaxAmount = 10,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "roadsign.kilt",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "black.raspberries",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 10,
                                            MaxAmount = 15,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.facemask",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.plate.torso",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.plate.torso",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 128,
                                            MaxAmount = 256,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "syringe.medical",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 10,
                                            MaxAmount = 20,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                }
                            }
                                    
                        },
                        new CrateConfig
                        {
                            PresetName = "frige_safe_nightmare",
                            PrefabName = "assets/prefabs/deployable/fridge/fridge.deployed.prefab",
                            Skin = 3005880420,
                            HackTime = -1,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = false,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 9,
                                    MaxItemsAmount = 9,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "scrap",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1000,
                                            MaxAmount = 2000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "diesel_barrel",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 10,
                                            MaxAmount = 15,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "cloth",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 1500,
                                            MaxAmount = 2500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "leather",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 750,
                                            MaxAmount = 1500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "charcoal",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 15000,
                                            MaxAmount = 25000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "gunpowder",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 2500,
                                            MaxAmount = 4000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "sulfur",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 3000,
                                            MaxAmount = 6000,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.refined",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 350,
                                            MaxAmount = 500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "metal.fragments",
                                            Skin = 0,
                                            Chance = 100,
                                            MinAmount = 10000,
                                            MaxAmount = 17500,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                    },
                    HeliConfigs = new HashSet<HeliConfig>
                    {
                        new HeliConfig
                        {
                            PresetName = "heli_hard",
                            Hp = 10000,
                            CratesAmount = 2,
                            MainRotorHealth = 750,
                            RearRotorHealth = 375,
                            Height = 50f,
                            BulletDamage = 20f,
                            BulletSpeed = 250f,
                            Distance = 350f,
                            OutsideTime = 30,
                            ImmediatelyKill = true,
                            InstCrateOpen = true,
                        },
                        new HeliConfig
                        {
                            PresetName = "heli_nightmare",
                            Hp = 20000,
                            CratesAmount = 3,
                            MainRotorHealth = 1500,
                            RearRotorHealth = 750,
                            Height = 50f,
                            BulletDamage = 20f,
                            BulletSpeed = 250f,
                            Distance = 350f,
                            OutsideTime = 30,
                            ImmediatelyKill = true,
                            InstCrateOpen = true,
                        }
                    },
                    NpcConfigs = new HashSet<NpcConfig>
                    {
                        new NpcConfig
                        {
                            PresetName = "biker_m92_easy_1",
                            DisplayName = En ? "Forest Bandit" : "Лесной бандит",
                            Health = 100,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "mask.bandana",
                                    SkinID = 3255213783
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
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110f,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 60f,
                            MemoryDuration = 10f,
                            DamageScale = 0.4f,
                            AimConeScale = 1.5f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 2,
                                            MaxAmount = 2
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "syringe.medical",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "bandage",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "biker_m92_easy_2",
                            DisplayName =  En ? "Road Bandit" : "Дорожный бандит",
                            Health = 100,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "coffeecan.helmet",
                                    SkinID = 2803024592
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2811533300
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2811533832
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 2816776847
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
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110f,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 60f,
                            MemoryDuration = 10f,
                            DamageScale = 0.4f,
                            AimConeScale = 1.5f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 2,
                                            MaxAmount = 2
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "syringe.medical",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "bandage",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 3,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "biker_spas12_easy",
                            DisplayName = En ? "Hunter" : "Охотник",
                            Health = 125,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 961066582
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 961084105
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 961096730
                                },
                                new NpcWear
                                {
                                    ShortName = "burlap.gloves",
                                    SkinID = 961103399
                                },
                                new NpcWear
                                {
                                    ShortName = "hat.beenie",
                                    SkinID = 594202145
                                },
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "shotgun.spas12",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110f,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 60f,
                            MemoryDuration = 10f,
                            DamageScale = 0.8f,
                            AimConeScale = 1f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                             LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 2,
                                    MaxItemsAmount = 4,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.shotgun.slug",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 15,
                                            MaxAmount = 30,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rifle",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 60,
                                            MaxAmount = 80,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.pistol.fire",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 60,
                                            MaxAmount = 80,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.pistol",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 60,
                                            MaxAmount = 80,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.shotgun.fire",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 15,
                                            MaxAmount = 30,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.shotgun",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 15,
                                            MaxAmount = 30,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "sedan_npc_easy",
                            DisplayName = En ? "Armored Bandit" : "Бронированный бандит",
                            Health = 150,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "coffeecan.helmet",
                                    SkinID = 3312398531
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.jacket",
                                    SkinID = 3312406908
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.kilt",
                                    SkinID = 3312413579
                                },

                                new NpcWear
                                {
                                    ShortName = "roadsign.gloves",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 1582399729
                                },
                                new NpcWear
                                {
                                    ShortName = "tshirt",
                                    SkinID = 1582403431
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                },
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "smg.thompson",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110f,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 60f,
                            MemoryDuration = 10f,
                            DamageScale = 0.4f,
                            AimConeScale = 1.3f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                                            Chance = 100,
                                            MinAmount = 1,
                                            MaxAmount = 1
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = true,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "rocket.launcher",
                                            Skin = 0,
                                            Chance = 4,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "explosive.timed",
                                            Skin = 0,
                                            Chance = 4,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "ammo.rocket.basic",
                                            Skin = 0,
                                            Chance = 4,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.ak",
                                            Skin = 0,
                                            Chance = 4,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "pistol.m92",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.thompson",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "shotgun.spas12",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "rifle.semiauto",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        },
                                        new LootItemConfig
                                        {
                                            Shortname = "smg.2",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },

                        new NpcConfig
                        {
                            PresetName = "biker_grenadelauncher_medium",
                            DisplayName = En ? "Bomber Man" : "Взрыватель",
                            Health = 200,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "coffeecan.helmet",
                                    SkinID = 2350097716
                                },
                                new NpcWear
                                {
                                    ShortName = "jacket",
                                    SkinID = 2395820290
                                },
                                new NpcWear
                                {
                                    ShortName = "tshirt",
                                    SkinID = 856391177
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
                                    ShortName = "multiplegrenadelauncher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.2f,
                            AimConeScale = 1.5f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "biker_mp5_medium",
                            DisplayName = En ? "Armored Road Bandit" : "Бронированный дорожный бандит",
                            Health = 125,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "coffeecan.helmet",
                                    SkinID = 1269589560
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.jacket",
                                    SkinID = 1706089885
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.gloves",
                                    SkinID = 2806216923
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2811533300
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
                                    ShortName = "smg.mp5",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 7.5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 1.4f,
                            AimConeScale = 0.8f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_shotgunm4_medium",
                            DisplayName = En ? "Madman" : "Безумец",
                            Health = 170,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "coffeecan.helmet",
                                    SkinID = 1624104393
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.jacket",
                                    SkinID = 1624100124
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.kilt",
                                    SkinID = 1624102935
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2099705103
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2099701364
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                },
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "shotgun.m4",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight" },
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 1.5f,
                            AimConeScale = 1f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_lr300_medium",
                            DisplayName = En ? "Radiation Liquidator" : "Ликвидатор Радиации",
                            Health = 175,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "hat.gas.mask",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.kilt",
                                    SkinID = 1740068457
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.jacket",
                                    SkinID = 1740065674
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.gloves",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2649552973
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2649555568
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "diving.tank",
                                    SkinID = 0
                                },

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.lr300",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 7.5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_lr300_raid_medium",
                            DisplayName = En ? "Raider" : "Рейдер",
                            Health = 175,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "clatter.helmet",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "attire.hide.poncho",
                                    SkinID = 2819301476
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.gloves",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2984978438
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
                                },

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.lr300",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "rocket.launcher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 7.5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },

                        new NpcConfig
                        {
                            PresetName = "carnpc_lr300_hard",
                            DisplayName = En ? "Defender" : "Защитник",
                            Health = 175,
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
                                    ShortName = "roadsign.kilt",
                                    SkinID = 3299983586
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 3322149888
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 3322151159
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 819211835
                                },

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.lr300",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 7.5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_lr300_raid_hard",
                            DisplayName = En ? "Armored Raider" : "Бронированный Рейдер",
                            Health = 175,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 1644415525
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.plate.torso",
                                    SkinID = 1644419309
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2282815003
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2282817402
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 919261524
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.lr300",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "rocket.launcher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 7.5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_grenadelauncher_hard",
                            DisplayName = En ? "Armored Bomber Man" : "Бронированный Взрыватель",
                            Health = 175,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 2810942233
                                },
                                new NpcWear
                                {
                                    ShortName = "jacket",
                                    SkinID = 2843424058
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.kilt",
                                    SkinID = 2823738497
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2814837980
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2814838951
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 919261524
                                }

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "multiplegrenadelauncher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.5f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_bolt_hard",
                            DisplayName = En ? "Sniper" : "Снайпер",
                            Health = 175,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "gloweyes",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 2226597543
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.plate.torso",
                                    SkinID = 2226598382
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2282815003
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2282817402
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 919261524
                                }

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.bolt",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.lasersight", "weapon.mod.8x.scope" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_flamethrower_hard",
                            DisplayName = En ? "Fire Boss" : "Огненный босс",
                            Health = 1500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "scientistsuit_heavy",
                                    SkinID = 0
                                }

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "military flamethrower",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 3.5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_minigun_hard",
                            DisplayName = En ? "Boss" : "Босс",
                            Health = 1500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "scientistsuit_heavy",
                                    SkinID = 0
                                }

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "minigun",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 3.5f,
                            RoamRange = 10f,
                            ChaseRange = 110,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 110,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 0.7f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },

                        new NpcConfig
                        {
                            PresetName = "carnpc_ak_raid_nightmare",
                            DisplayName = En ? "Armored Raider" : "Бронированный Рейдер",
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
                                    ShortName = "roadsign.kilt",
                                    SkinID = 3299983586
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 3318206106
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 3318207180
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 819211835
                                },

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.ak",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "rocket.launcher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 7.5f,
                            RoamRange = 10f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_ak_raid_nightmare_2",
                            DisplayName = En ? "Armored Raider" : "Бронированный Рейдер",
                            Health = 500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 2810942233
                                },
                                new NpcWear
                                {
                                    ShortName = "jacket",
                                    SkinID = 2843424058
                                },
                                new NpcWear
                                {
                                    ShortName = "roadsign.kilt",
                                    SkinID = 2823738497
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 2814837980
                                },
                                new NpcWear
                                {
                                    ShortName = "hoodie",
                                    SkinID = 2814838951
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 919261524
                                }

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                 new NpcBelt
                                {
                                    ShortName = "rifle.ak",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "rocket.launcher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
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
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = false,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = false,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = false,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = true,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_ak_raid_nightmare_3",
                            DisplayName = En ? "Armored Raider" : "Бронированный Рейдер",
                            Health = 500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "burlap.shirt",
                                    SkinID = 636287439
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.facemask",
                                    SkinID = 2703876402
                                },
                                new NpcWear
                                {
                                    ShortName = "tactical.gloves",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "metal.plate.torso",
                                    SkinID = 891976364
                                },
                                new NpcWear
                                {
                                    ShortName = "pants",
                                    SkinID = 636287180
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 636286960
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "rifle.ak",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "rocket.launcher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "grenade.f1",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 3f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.4f,
                            AimConeScale = 1.3f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = true,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = true,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = true,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = true,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_smoke_nightmare",
                            DisplayName = En ? "Smoker" : "Смокер",
                            Health = 500,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "heavy.plate.helmet",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "heavy.plate.jacket",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "heavy.plate.pants",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "tactical.gloves",
                                    SkinID = 0
                                },
                                new NpcWear
                                {
                                    ShortName = "shoes.boots",
                                    SkinID = 636286960
                                }
                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "multiplegrenadelauncher",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = "40mm_grenade_smoke"
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "grenade.f1",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 5f,
                            RoamRange = 10f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 3f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.4f,
                            AimConeScale = 1.3f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = true,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = true,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = true,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = false,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_flamethrower_nightmare",
                            DisplayName = En ? "Fire Boss" : "Огненный босс",
                            Health = 3000,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "scientistsuit_heavy",
                                    SkinID = 0
                                }

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "military flamethrower",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 3.5f,
                            RoamRange = 10f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 1.0f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = true,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = true,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = true,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = false,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                        new NpcConfig
                        {
                            PresetName = "carnpc_minigun_nightmare",
                            DisplayName = En ? "Boss" : "Босс",
                            Health = 3000,
                            Kit = "",
                            WearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    ShortName = "scientistsuit_heavy",
                                    SkinID = 0
                                }

                            },
                            BeltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    ShortName = "minigun",
                                    Amount = 1,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                },
                                new NpcBelt
                                {
                                    ShortName = "syringe.medical",
                                    Amount = 10,
                                    SkinID = 0,
                                    Mods = new List<string>(),
                                    Ammo = ""
                                }
                            },
                            Speed = 3.5f,
                            RoamRange = 10f,
                            ChaseRange = 130,
                            AttackRangeMultiplier = 1f,
                            SenseRange = 130,
                            MemoryDuration = 10f,
                            DamageScale = 0.65f,
                            AimConeScale = 0.7f,
                            CheckVisionCone = false,
                            VisionCone = 135f,
                            TurretDamageScale = 1f,
                            DisableRadio = false,
                            DeleteCorpse = true,
                            LootTableConfig = new LootTableConfig
                            {
                                IsAlphaLoot = true,
                                AlphaLootPreset = string.Empty,
                                IsLoottablePlugin = true,
                                LoottablePreset = string.Empty,
                                IsCustomLootPlugin = true,
                                CustomLootPreset = string.Empty,
                                ClearDefaultLoot = false,
                                
                                PrefabsTable = new PrefabsLootTableConfig
                                {
                                    IsEnabled = false,
                                    MinPrefabsAmount = 1,
                                    MaxPrefabsAmount = 1,
                                    Prefabs = new List<LootPrefabConfig>
                                    {
                                        new LootPrefabConfig
                                        {
                                            PrefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab",
                                            Chance = 100,
                                            MinAmount = 3,
                                            MaxAmount = 3
                                        }
                                    }
                                },
                                ItemsTable = new ItemsLootTableConfig
                                {
                                    IsEnabled = false,
                                    DisableMinMax = false,
                                    MinItemsAmount = 1,
                                    MaxItemsAmount = 1,
                                    Items = new List<LootItemConfig>
                                    {
                                        new LootItemConfig
                                        {
                                            Shortname = "largemedkit",
                                            Skin = 0,
                                            Chance = 20,
                                            MinAmount = 1,
                                            MaxAmount = 1,
                                            DisplayName = string.Empty,
                                            OwnerName = string.Empty,
                                            IsBlueprint = false,
                                            Genomes = new List<string>()
                                        }
                                    }
                                },
                            }
                        },
                    },
                    MarkerConfig = new MarkerConfig
                    {
                        Enable = true,
                        UseRingMarker = true,
                        UseShopMarker = true,
                        Radius = 0.2f,
                        Alpha = 0.6f,
                        Color1 = new ColorConfig { R = 0.81f, G = 0.25f, B = 0.15f },
                        Color2 = new ColorConfig { R = 0f, G = 0f, B = 0f }
                    },
                    ZoneConfig = new ZoneConfig
                    {
                        IsPvpZone = false,
                        IsDome = false,
                        Darkening = 5,
                        IsColoredBorder = false,
                        Brightness = 5,
                        BorderColor = 2
                    },
                    NotifyConfig = new NotifyConfig
                    {
                        IsChatEnable = true,
                        GameTipConfig = new GameTipConfig
                        {
                            IsEnabled = false,
                            Style = 2,
                        },
                        TimeNotifications = new HashSet<int>
                        {
                            300,
                            60,
                            30,
                            5
                        },
                    },
                    GUIConfig = new GUIConfig
                    {
                        IsEnable = true,
                        OffsetMinY = -56
                    },

                    SupportedPluginsConfig = new SupportedPluginsConfig
                    {
                        PveMode = new PveModeConfig
                        {
                            Enable = false,
                            OwnerIsStopper = false,
                            NoDealDamageIfCooldownAndTeamOwner = false,
                            NoInteractIfCooldownAndNoOwners = false,
                            ShowEventOwnerNameOnMap = true,
                            Damage = 500f,
                            ScaleDamage = new Dictionary<string, float>
                            {
                                ["Npc"] = 1f,
                                ["Bradley"] = 2f,
                                ["Helicopter"] = 2f,
                                ["Turret"] = 1f,
                            },
                            LootCrate = false,
                            HackCrate = false,
                            LootNpc = false,
                            DamageNpc = false,
                            TargetNpc = false,
                            DamageHeli = false,
                            TargetHeli = false,
                            DamageTank = false,
                            TargetTank = false,
                            CanEnter = false,
                            CanEnterCooldownPlayer = true,
                            TimeExitOwner = 300,
                            AlertTime = 60,
                            RestoreUponDeath = true,
                            Cooldown = 86400,
                        },
                        EconomicsConfig = new EconomyConfig
                        {
                            Enable = false,
                            Plugins = new HashSet<string> { "Economics", "Server Rewards", "IQEconomic" },
                            MinCommandPoint = 0,
                            MinEconomyPiont = 0,
                            Crates = new Dictionary<string, double>
                            {
                                ["assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab"] = 0.4
                            },
                            NpcPoint = 1,
                            BradleyPoint = 5,
                            HeliPoint = 5,
                            SedanPoint = 0,
                            ModularCarPoint = 0,
                            TurretPoint = 1,
                            LockedCratePoint = 1,
                            Commands = new HashSet<string>()
                        },
                        BetterNpcConfig = new BetterNpcConfig
                        {
                            BradleyNpc = false,
                            HeliNpc = false
                        },
                        GUIAnnouncementsConfig = new GUIAnnouncementsConfig
                        {
                            IsEnabled = false,
                            BannerColor = "Grey",
                            TextColor = "White",
                            APIAdjustVPosition = 0.03f
                        },
                        NotifyPluginConfig = new NotifyPluginConfig
                        {
                            IsEnabled = false,
                            Type = 0
                        },
                        DiscordMessagesConfig = new DiscordConfig
                        {
                            IsEnabled = false,
                            WebhookUrl = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
                            EmbedColor = 13516583,
                            Keys = new HashSet<string>
                            {
                                "PreStart",
                                "EventStart",
                                "PreFinish",
                                "Finish",
                                "StartHackCrate"
                            }
                        },
                        LifeSupportConfig = new LifeSupportConfig
                        {
                            IsDisabled = false
                        }
                    }
                };
            }
        }
        #endregion Config
    }
}

namespace Oxide.Plugins.ConvoyExtensionMethods
{
    public static class ExtensionMethods
    {
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

        public static List<TSource> WhereList<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            List<TSource> result = new List<TSource>();
            using (var enumerator = source.GetEnumerator()) while (enumerator.MoveNext()) if (predicate(enumerator.Current)) result.Add(enumerator.Current);
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

        public static List<TSource> Shuffle<TSource>(this IEnumerable<TSource> source)
        {
            List<TSource> result = source.ToList();

            for (int i = 0; i < result.Count; i++)
            {
                int j = Random.Range(0, i + 1);
                var temp = result[j];
                result[j] = result[i];
                result[i] = temp;
            }

            return result;
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
            foreach (FieldInfo fi in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) if (fi.Name == fieldName) return fi;
            return null;
        }

        public static Action GetPrivateAction(this object obj, string methodName)
        {
            MethodInfo mi = obj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
