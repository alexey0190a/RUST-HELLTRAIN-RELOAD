using CompanionServer.Handlers;
using Epic.OnlineServices.Presence;
using Facepunch;
using Network;
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
using UnityEngine;
using UnityEngine.AI;
using static BaseVehicle;
using Time = UnityEngine.Time;

namespace Oxide.Plugins
{
    [Info("Convoy", "Adem", "2.8.1")]
    public class Convoy : RustPlugin
    {
        // 🔥 наши вставки идут тут
        private static readonly string[] FilteredHooks = {
            "CanBradleySpawnNpc",
            "CanHelicopterSpawnNpc",
            "OnCreateDynamicPVP",
            "CanPopulateLoot",
            "OnCustomLootContainer",
            "OnCustomLootNPC",
            "OnContainerPopulate",
            "OnCorpsePopulate",
            "SetOwnerPveMode",
            "ClearOwnerPveMode",
            "OnEngineLoadoutOverride",
            "OnSetupTurret"
        };

        private static string PresetKey(string presetName)
        {
            // BH fix: do not swap tiers; preserve exact preset names
            return presetName;
        }
        

        #region Variables
        const bool en = false;
        static Convoy ins;
        EventController eventController;
        [PluginReference] Plugin ArmoredTrain, NpcSpawn, GUIAnnouncements, DiscordMessages, Notify, PveMode, Economics, ServerRewards, IQEconomic, DynamicPVP, AlphaLoot, Loottable;

        HashSet<string> subscribeMetods = new HashSet<string>
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
            "OnCorpsePopulate",
            "CanHackCrate",
            "CanLootEntity",
            "OnLootEntity",
            "OnLootEntityEnd",
            "OnEntityEnter",
            "CanEntityBeTargeted",
            "CanEntityTakeDamage",
            "CanBradleySpawnNpc",
            "CanHelicopterSpawnNpc",
            "OnCreateDynamicPVP",
            "CanPopulateLoot",
            "OnCustomLootContainer",
            "OnCustomLootNPC",
            "OnContainerPopulate",
            "OnCorpsePopulate",
            "SetOwnerPveMode",
            "ClearOwnerPveMode",
            "OnEngineLoadoutOverride",
            "OnSetupTurret"
        };
        #endregion Variables

        // ---- silent helpers (вместо чатов) ----
        private void BroadcastSilent(string msg) { Puts($"[Convoy] {msg}"); }
        private void Silent(string msg)          { Puts($"[Convoy] {msg}"); }
        private void Silent(BasePlayer _, string msg) { Puts($"[Convoy] {msg}"); }

        #region API
        private bool IsConvoyVehicle(BaseEntity entity)
        {
            if (eventController == null || entity == null || entity.net == null)
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
            if (eventController == null || crate == null || crate.net == null)
                return false;

            return LootManager.GetContainerDataByNetId(crate.net.ID.Value) != null;
        }

        private bool IsConvoyHeli(PatrolHelicopter patrolHelicopter)
        {
            if (eventController == null || patrolHelicopter == null || patrolHelicopter.net == null)
                return false;

            return EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value) != null;
        }

        private bool IsConvoyNpc(ScientistNPC scientistNPC)
        {
            if (eventController == null || scientistNPC == null || scientistNPC.net == null)
                return false;

            return NpcSpawnManager.GetScientistByNetId(scientistNPC.net.ID.Value) != null;
        }
        #endregion API

        #region Hooks
        void Init()
        {
            Unsubscribes();
        }

        void OnServerInitialized()
        {
            RegisterConvoyPresetsToLoottable();
            ins = this;

            if (!NpcSpawnManager.IsNpcSpawnReady())
                return;

            LoadDefaultMessages();
            LoadData();

            GuiManager.LoadImages();
            PathManager.StartCachingRouts();
            LootManager.InitialLootManagerUpdate();
            EventLauncher.AutoStartEvent();
        }

        void Unload()
        {
            EventLauncher.StopEvent(true);
            PathManager.OnPluginUnloaded();
            ins = null;
        }

        void OnEntitySpawned(LockedByEntCrate entity)
        {
            if (!entity.IsExists())
                return;

            if (entity.ShortPrefabName == "heli_crate")
                LootManager.OnHeliCrateSpawned(entity);
            else if (entity.ShortPrefabName == "bradley_crate")
                LootManager.OnBradleyCrateSpawned(entity);
        }

        void OnEntitySpawned(TimedExplosive timedExplosive)
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

        void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon item)
        {
            if (!player.IsRealPlayer())
                return;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetClosestVehicle<ConvoyPathVehicle>(player.transform.position);

            if (convoyPathVehicle != null && Vector3.Distance(convoyPathVehicle.transform.position, player.transform.position) < 20f)
                eventController.OnEventAttacked(player);
        }

        object CanExplosiveStick(RFTimedExplosive rfTimedExplosive, BaseEntity entity)
        {
            if (rfTimedExplosive == null || entity == null || entity.net == null)
                return null;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value);
            if (convoyPathVehicle != null && rfTimedExplosive.GetFrequency() > 0)
                return false;

            return null;
        }

        object CanMountEntity(BasePlayer player, BaseMountable entity)
        {
            if (!player.IsRealPlayer() || entity == null)
                return null;

            BaseEntity vehicle = entity.VehicleParent();

            if (vehicle != null && ConvoyPathVehicle.GetVehicleByNetId(vehicle.net.ID.Value) != null)
                return true;

            return null;
        }

        object CanDismountEntity(BasePlayer player, BaseMountable baseMountable)
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

        void OnPlayerSleep(BasePlayer player)
        {
            if (!player.IsRealPlayer())
                return;

            ZoneController.OnPlayerLeaveZone(player);
        }

        object CanPickupEntity(BasePlayer player, Door door)
        {
            if (door == null || door.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(door.net.ID.Value) == null)
                return null;

            return false;
        }

        object OnEntityTakeDamage(BaseVehicle baseVehicle, HitInfo info)
        {
            if (baseVehicle == null || baseVehicle.net == null || info == null)
                return null;

            return CheckIfVehicleAttacked(baseVehicle, info);
        }

        object OnEntityTakeDamage(BradleyAPC bradley, HitInfo info)
        {
            if (bradley == null || bradley.net == null || info == null)
                return null;

            return CheckIfVehicleAttacked(bradley, info);
        }

        object OnEntityTakeDamage(BaseVehicleModule baseVehicleModule, HitInfo info)
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
                modularCar.health -= info.damageTypes.Total() * modularCarVehicle.modularCarConfig.damageScale / 5;

                if (!modularCar.IsDestroyed && modularCar.health <= 0)
                    modularCar.Kill(BaseNetworkable.DestroyMode.Gib);

                for (int i = 0; i <= modularCar.moduleSockets.Count; i++)
                {
                    BaseVehicleModule module;

                    if (modularCar.TryGetModuleAt(i, out module))
                        module.SetHealth(module._maxHealth * modularCar.health / modularCar._maxHealth);
                }
            }

            return true;
        }

        object CheckIfVehicleAttacked(BaseCombatEntity entity, HitInfo info)
        {

            if (ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) == null)
                return null;

            if (!info.InitiatorPlayer.IsRealPlayer())
                return true;

            if (!eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, true))
                return true;

            eventController.OnEventAttacked(info.InitiatorPlayer);
            return null;
        }

        object OnEntityTakeDamage(AutoTurret autoTurret, HitInfo info)
        {
            if (autoTurret == null || autoTurret.net == null || info == null)
                return null;

            return CheckIfConvoyChildAttacked(autoTurret, info);
        }

        object OnEntityTakeDamage(Door door, HitInfo info)
        {
            if (door == null || door.net == null || info == null)
                return null;

            return CheckIfConvoyChildAttacked(door, info);
        }

        object OnEntityTakeDamage(SamSite samSite, HitInfo info)
        {
            if (samSite == null || samSite.net == null || info == null)
                return null;

            return CheckIfConvoyChildAttacked(samSite, info);
        }

        object CheckIfConvoyChildAttacked(BaseCombatEntity entity, HitInfo info)
        {
            if (ConvoyPathVehicle.GetVehicleByChildNetId(entity.net.ID.Value) == null)
                return null;

            if (!info.InitiatorPlayer.IsRealPlayer())
                return true;

            if (!eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, true))
                return true;

            eventController.OnEventAttacked(info.InitiatorPlayer);

            return null;
        }

        object OnEntityTakeDamage(ScientistNPC scientistNPC, HitInfo info)
        {
            if (scientistNPC == null || scientistNPC.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return null;

            if (NpcSpawnManager.GetScientistByNetId(scientistNPC.net.ID.Value) == null)
                return null;

            if (!eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, scientistNPC, true))
            {
                info.damageTypes.ScaleAll(0f);
                return true;
            }

            if (scientistNPC.isMounted)
                info.damageTypes.ScaleAll(10f);

            eventController.OnEventAttacked(info.InitiatorPlayer);

            float weaponDamageScale;
            if (info.WeaponPrefab != null && eventController.eventConfig.weaponToScaleDamageNpc.TryGetValue(info.WeaponPrefab.ShortPrefabName, out weaponDamageScale))
                info.damageTypes.ScaleAll(weaponDamageScale);

            return null;
        }

        object OnEntityTakeDamage(StorageContainer storageContainer, HitInfo info)
        {
            if (storageContainer == null || storageContainer.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(storageContainer.net.ID.Value) != null)
                return true;

            return null;
        }

        object OnEntityTakeDamage(Fridge fridge, HitInfo info)
        {
            if (fridge == null || fridge.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(fridge.net.ID.Value) != null)
                return true;

            return null;
        }

        object OnEntityTakeDamage(PatrolHelicopter patrolHelicopter, HitInfo info)
        {
            if (patrolHelicopter == null || patrolHelicopter.net == null || info == null)
                return null;

            EventHeli eventHeli = EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value);

            if (eventHeli == null)
                return null;

            if (info.InitiatorPlayer.IsRealPlayer())
            {
                if (!eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, patrolHelicopter, true))
                    return true;
                else
                {
                    eventHeli.OnHeliAttacked(info.InitiatorPlayer.userID);
                    eventController.OnEventAttacked(info.InitiatorPlayer);
                }
            }

            return null;
        }

        void OnEntityTakeDamage(BuildingBlock buildingBlock, HitInfo info)
        {
            if (buildingBlock == null || info == null)
                return;

            CustomBradley customBradley = info.Initiator as CustomBradley;

            if (customBradley != null)
            {
                if (customBradley.bradleyConfig.bradleyBuildingDamageScale < 0)
                    return;
                else
                    info.damageTypes.ScaleAll(customBradley.bradleyConfig.bradleyBuildingDamageScale);
            }
        }

        void OnEntityDeath(BradleyAPC bradleyAPC, HitInfo info)
        {
            if (bradleyAPC == null || bradleyAPC.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(bradleyAPC.net.ID.Value);

            if (convoyPathVehicle != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.supportedPluginsConfig.economicsConfig.bradleyPoint);
        }

        void OnEntityDeath(BasicCar basicCar, HitInfo info)
        {
            if (basicCar == null || basicCar.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByNetId(basicCar.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.supportedPluginsConfig.economicsConfig.sedanPoint);
        }

        void OnEntityDeath(ModularCar modularCar, HitInfo info)
        {
            if (modularCar == null || modularCar.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByNetId(modularCar.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.supportedPluginsConfig.economicsConfig.modularCarPoint);
        }

        void OnEntityDeath(PatrolHelicopter patrolHelicopter, HitInfo info)
        {
            if (patrolHelicopter == null || patrolHelicopter.net == null)
                return;

            EventHeli eventHeli = EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value);

            if (eventHeli != null && eventHeli.lastAttackedPlayer != 0)
                EconomyManager.AddBalance(eventHeli.lastAttackedPlayer, _config.supportedPluginsConfig.economicsConfig.heliPoint);
        }

        void OnEntityDeath(AutoTurret autoTurret, HitInfo info)
        {
            if (autoTurret == null || autoTurret.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(autoTurret.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.supportedPluginsConfig.economicsConfig.turretPoint);
        }

        void OnEntityDeath(SamSite samSite, HitInfo info)
        {
            if (samSite == null || samSite.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(samSite.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.supportedPluginsConfig.economicsConfig.samsitePoint);
        }

        void OnEntityDeath(ScientistNPC scientistNPC, HitInfo info)
        {
            if (scientistNPC == null || scientistNPC.net == null || info == null || !info.InitiatorPlayer.IsRealPlayer())
                return;

            if (NpcSpawnManager.GetScientistByNetId(scientistNPC.net.ID.Value) != null)
                EconomyManager.AddBalance(info.InitiatorPlayer.userID, _config.supportedPluginsConfig.economicsConfig.npcPoint);
        }

        void OnEntityKill(BaseMountable baseMountable)
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

                if (EventLauncher.IsEventActive() && ins.eventController.IsFullySpawned())
                    convoyPathVehicle.DropCrates();
            }

            if (ConvoyPathVehicle.GetVehicleByChildNetId(baseMountable.net.ID.Value) != null)
            {
                if (baseMountable._mounted.IsExists())
                    baseMountable._mounted.Kill();
            }
        }

        void OnEntityKill(BradleyAPC bradleyAPC)
        {
            if (bradleyAPC == null || bradleyAPC.net == null)
                return;

            ConvoyPathVehicle convoyPathVehicle = ConvoyPathVehicle.GetVehicleByNetId(bradleyAPC.net.ID.Value);

            if (convoyPathVehicle != null && EventLauncher.IsEventActive() && ins.eventController.IsFullySpawned())
                convoyPathVehicle.DropCrates();
        }

        object CanHelicopterTarget(PatrolHelicopterAI heli, BasePlayer player)
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

        object OnCustomNpcTarget(ScientistNPC scientistNPC, BasePlayer player)
        {
            if (eventController == null || scientistNPC == null || scientistNPC.net == null)
                return null;

            if (NpcSpawnManager.GetScientistByNetId(scientistNPC.net.ID.Value) == null)
                return null;

            if (!eventController.IsAgressive())
                return false;
            else if (player.IsSleeping() || (player.InSafeZone() && !player.IsHostile()))
                return false;

            return null;
        }

        object CanBradleyApcTarget(BradleyAPC bradley, BaseEntity entity)
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

        void OnCorpsePopulate(ScientistNPC scientistNPC, NPCPlayerCorpse corpse)
        {
            if (scientistNPC == null || corpse == null)
                return;

            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(scientistNPC.displayName);

            if (npcConfig != null)
            {
                ins.NextTick(() =>
                {
                    if (corpse == null)
                        return;

                    if (!corpse.containers.IsNullOrEmpty() && corpse.containers[0] != null)
                        LootManager.UpdateItemContainer(corpse.containers[0], npcConfig.lootTableConfig, npcConfig.lootTableConfig.clearDefaultItemList);

                    if (npcConfig.deleteCorpse && !corpse.IsDestroyed)
                        corpse.Kill();
                });
            }
        }

        object CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (player == null || crate == null || crate.net == null)
                return null;

            StorageContainerData storageContainerData = LootManager.GetContainerDataByNetId(crate.net.ID.Value);

            if (storageContainerData != null)
            {
                if (!ins.eventController.IsPlayerCanLoot(player, true))
                {
                    eventController.SwitchAgressive(true);
                    return true;
                }

                EconomyManager.AddBalance(player.userID, _config.supportedPluginsConfig.economicsConfig.lockedCratePoint);
                crate.Invoke(() => LootManager.UpdateCrateHackTime(crate, storageContainerData.presetName), 1.1f);
            }

            return null;
        }

        object CanLootEntity(BasePlayer player, StorageContainer storageContainer)
        {
            return CanLootEventEntity(player, storageContainer);
        }

        object CanLootEntity(BasePlayer player, Fridge fridge)
        {
            return CanLootEventEntity(player, fridge);
        }

        object CanLootEventEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || entity.net == null)
                return null;

            if (LootManager.GetContainerDataByNetId(entity.net.ID.Value) != null)
            {
                if (!ins.eventController.IsPlayerCanLoot(player, true))
                {
                    eventController.SwitchAgressive(true);
                    return true;
                }
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

        void OnLootEntity(BasePlayer player, StorageContainer storageContainer)
        {
            if (player == null || storageContainer == null || storageContainer.net == null)
                return;

            if (LootManager.IsEventCrate(storageContainer.net.ID.Value))
            {
                LootManager.OnEventCrateLooted(storageContainer, player.userID);
                eventController.EventPassingCheck();
            }
        }

        void OnLootEntity(BasePlayer player, Fridge fridge)
        {
            if (player == null || fridge == null || fridge.net == null)
                return;

            if (LootManager.IsEventCrate(fridge.net.ID.Value))
            {
                LootManager.OnEventCrateLooted(fridge, player.userID);
                eventController.EventPassingCheck();
            }
        }

        void OnLootEntityEnd(BasePlayer player, StorageContainer storageContainer)
        {
            if (storageContainer == null || storageContainer.net == null || !player.IsRealPlayer())
                return;

            if (!(storageContainer is LootContainer) && LootManager.GetContainerDataByNetId(storageContainer.net.ID.Value) != null)
                if (storageContainer.inventory.IsEmpty())
                    storageContainer.Kill();
        }

        void OnLootEntityEnd(BasePlayer player, Fridge fridge)
        {
            if (fridge == null || fridge.net == null || !player.IsRealPlayer())
                return;

            if (LootManager.GetContainerDataByNetId(fridge.net.ID.Value) != null)
                if (fridge.inventory.IsEmpty())
                    fridge.Kill();
        }

        object OnEntityEnter(TriggerVehiclePush trigger, BaseCombatEntity entity)
        {
            if (trigger == null || entity == null || entity.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) != null)
                return true;

            return null;
        }

        object OnEntityEnter(TriggerPath trigger, BradleyAPC bradleyAPC)
        {
            if (bradleyAPC == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(bradleyAPC.net.ID.Value) == null)
                return null;

            NextTick(() => bradleyAPC.myRigidBody.isKinematic = false);
            return null;
        }

        object OnEntityEnter(TriggerPath trigger, TravellingVendor travellingVendor)
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

        object OnEntityEnter(TargetTrigger trigger, ScientistNPC scientistNPC)
        {
            if (trigger == null || scientistNPC == null || scientistNPC.net == null)
                return null;

            AutoTurret autoTurret = trigger.GetComponentInParent<AutoTurret>();
            if (autoTurret == null || autoTurret.net == null)
                return null;

            if (NpcSpawnManager.GetScientistByNetId(scientistNPC.net.ID.Value) == null)
                return null;

            if (scientistNPC.isMounted || !_config.behaviorConfig.isPlayerTurretEnable)
                return true;

            return null;
        }

        object OnEntityEnter(TriggerVehiclePush trigger, TravellingVendor travellingVendor)
        {
            if (trigger == null || travellingVendor == null || travellingVendor.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(travellingVendor.net.ID.Value))
                return true;

            return null;
        }

        #region OtherPLugins
        object CanEntityBeTargeted(BasePlayer player, AutoTurret turret)
        {
            if (eventController == null || turret == null || turret.net == null || turret.OwnerID != 0)
                return null;

            if (!eventController.IsEventTurret(turret.net.ID.Value))
                return null;

            if (!player.IsRealPlayer())
                return false;
            else if (!eventController.IsAgressive())
                return false;
            else if (!PveModeManager.IsPveModDefaultBlockAction(player))
                return true;

            return null;
        }

        object CanEntityBeTargeted(PlayerHelicopter playerHelicopter, SamSite samSite)
        {
            if (eventController == null || samSite == null || samSite.net == null || samSite.OwnerID != 0)
                return null;

            if (!eventController.IsEventSamSite(samSite.net.ID.Value))
                return null;

            if (!eventController.IsAgressive())
                return false;

            return true;
        }

        object CanEntityTakeDamage(Bike bike, HitInfo info)
        {
            if (eventController == null || info == null || !bike.IsExists() || bike.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(bike, info);
        }

        object CanEntityTakeDamage(ModularCar modularCar, HitInfo info)
        {
            if (eventController == null || info == null || !modularCar.IsExists() || modularCar.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(modularCar, info);
        }

        object CanEntityTakeDamage(BaseVehicleModule baseVehicleModule, HitInfo info)
        {
            if (eventController == null || info == null || !baseVehicleModule.IsExists() || baseVehicleModule.net == null)
                return null;

            BaseModularVehicle modularCar = baseVehicleModule.Vehicle;

            if (modularCar == null || modularCar.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(modularCar, info);
        }

        object CanEntityTakeDamage(BradleyAPC bradleyAPC, HitInfo info)
        {
            if (eventController == null || info == null || !bradleyAPC.IsExists() || bradleyAPC.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(bradleyAPC, info);
        }

        object CanEntityTakeDamage(BasicCar basicCar, HitInfo info)
        {
            if (eventController == null || info == null || !basicCar.IsExists() || basicCar.net == null)
                return null;

            return CanConvoyVehicleTakeDamage(basicCar, info);
        }

        object CanConvoyVehicleTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (ConvoyPathVehicle.GetVehicleByNetId(entity.net.ID.Value) == null)
                return null;

            if (info.InitiatorPlayer.IsRealPlayer())
            {
                if (eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, shoudSendMessages: false) && !PveModeManager.IsPveModDefaultBlockAction(info.InitiatorPlayer))
                    return true;
                else
                    return false;
            }
            else
                return false;
        }

        object CanEntityTakeDamage(SamSite samSite, HitInfo info)
        {
            if (eventController == null || info == null || !samSite.IsExists() || samSite.net == null)
                return null;

            return CheckTruePVEDamageToConvoyChild(samSite, info);
        }

        object CanEntityTakeDamage(AutoTurret autoTurret, HitInfo info)
        {
            if (eventController == null || info == null || !autoTurret.IsExists() || autoTurret.net == null)
                return null;

            return CheckTruePVEDamageToConvoyChild(autoTurret, info);
        }

        object CanEntityTakeDamage(Door door, HitInfo info)
        {
            if (door == null || door.net == null || info == null)
                return null;

            return CheckTruePVEDamageToConvoyChild(door, info);
        }

        object CheckTruePVEDamageToConvoyChild(BaseCombatEntity entity, HitInfo info)
        {
            if (ConvoyPathVehicle.GetVehicleByChildNetId(entity.net.ID.Value) == null)
                return null;

            if (info.InitiatorPlayer.IsRealPlayer())
            {
                if (eventController.IsPlayerCanDealDamage(info.InitiatorPlayer, entity, shoudSendMessages: true) && !PveModeManager.IsPveModDefaultBlockAction(info.InitiatorPlayer))
                    return true;
                else
                    return false;
            }
            else
                return false;
        }

        object CanEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            if (info == null || info.Initiator == null || info.Initiator.net == null)
                return null;

            if (victim.IsRealPlayer())
            {
                if (_config.zoneConfig.isPVPZone && !_config.supportedPluginsConfig.pveMode.enable)
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

            ScientistNPC scientistNPC = victim as ScientistNPC;
            if (scientistNPC != null && scientistNPC.net != null && NpcSpawnManager.GetScientistByNetId(scientistNPC.net.ID.Value) != null)
            {
                AutoTurret autoTurret = info.Initiator as AutoTurret;
                if (autoTurret != null && autoTurret.net != null && !eventController.IsEventTurret(autoTurret.net.ID.Value))
                    return true;
            }

            return null;
        }

        object CanEntityTakeDamage(PlayerHelicopter playerHelicopter, HitInfo info)
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

        object CanBradleySpawnNpc(BradleyAPC bradley)
        {
            if (eventController == null || bradley == null || bradley.net == null)
                return null;

            if (!_config.supportedPluginsConfig.betterNpcConfig.bradleyNpc && ConvoyPathVehicle.GetVehicleByNetId(bradley.net.ID.Value) != null)
                return true;

            return null;
        }

        object CanHelicopterSpawnNpc(PatrolHelicopter helicopter)
        {
            if (eventController == null || helicopter == null || helicopter.net == null)
                return null;

            if (!_config.supportedPluginsConfig.betterNpcConfig.heliNpc && EventHeli.GetEventHeliByNetId(helicopter.net.ID.Value) != null)
                return true;

            return null;
        }

        object OnCreateDynamicPVP(string eventName, PatrolHelicopter patrolHelicopter)
        {
            if (eventController == null || patrolHelicopter == null || patrolHelicopter.net == null)
                return null;

            if (EventHeli.GetEventHeliByNetId(patrolHelicopter.net.ID.Value) != null)
                return true;

            return null;
        }

        object OnCreateDynamicPVP(string eventName, BradleyAPC bradleyAPC)
        {
            if (eventController == null || bradleyAPC == null || bradleyAPC.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByNetId(bradleyAPC.net.ID.Value))
                return true;

            return null;
        }

        object CanPopulateLoot(LootContainer lootContainer)
        {
            if (eventController == null || lootContainer == null || lootContainer.net == null)
                return null;

            StorageContainerData storageContainerData = LootManager.GetContainerDataByNetId(lootContainer.net.ID.Value);

            if (storageContainerData != null)
            {
                CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(storageContainerData.presetName);

                if (crateConfig != null && !crateConfig.lootTableConfig.isAlphaLoot)
                    return true;
            }

            return null;
        }

        object CanPopulateLoot(ScientistNPC scientistNPC, NPCPlayerCorpse corpse)
        {
            if (eventController == null || scientistNPC == null || scientistNPC.net == null)
                return null;

            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(scientistNPC.displayName);

            if (npcConfig != null && !npcConfig.lootTableConfig.isAlphaLoot)
                return true;

            return null;
        }

        object OnCustomLootContainer(NetworkableId netID)
        {
            if (eventController == null || netID == null)
                return null;

            StorageContainerData storageContainerData = LootManager.GetContainerDataByNetId(netID.Value);

            if (storageContainerData != null)
            {
                CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(storageContainerData.presetName);

                if (crateConfig != null && !crateConfig.lootTableConfig.isCustomLoot)
                    return true;
            }

            return null;
        }

        object OnCustomLootNPC(NetworkableId netID)
        {
            if (eventController == null || netID == null)
                return null;

            ScientistNPC scientistNPC = NpcSpawnManager.GetScientistByNetId(netID.Value);

            if (scientistNPC != null)
            {
                NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(scientistNPC.displayName);

                if (npcConfig != null && !npcConfig.lootTableConfig.isCustomLoot)
                    return true;
            }

            return null;
        }

        object OnContainerPopulate(LootContainer lootContainer)
        {
            if (eventController == null || lootContainer == null || lootContainer.net == null)
                return null;

            StorageContainerData storageContainerData = LootManager.GetContainerDataByNetId(lootContainer.net.ID.Value);

            if (storageContainerData != null)
            {
                CrateConfig crateConfig = LootManager.GetCrateConfigByPresetName(storageContainerData.presetName);

                if (crateConfig != null && !crateConfig.lootTableConfig.isLootTablePLugin)
                    return true;
            }

            return null;
        }

        object OnCorpsePopulate(NPCPlayerCorpse corpse)
        {
            if (eventController == null || corpse == null)
                return null;

            NpcConfig npcConfig = NpcSpawnManager.GetNpcConfigByDisplayName(corpse.playerName);

            if (npcConfig != null && !npcConfig.lootTableConfig.isLootTablePLugin)
                return true;

            return null;
        }

        void SetOwnerPveMode(string shortname, BasePlayer player)
        {
            if (eventController == null || string.IsNullOrEmpty(shortname) || shortname != Name || !player.IsRealPlayer())
                return;

            if (shortname == Name)
                PveModeManager.OnNewOwnerSet(player);
        }

        void ClearOwnerPveMode(string shortname)
        {
            if (eventController == null || string.IsNullOrEmpty(shortname))
                return;

            if (shortname == Name)
                PveModeManager.OnOwnerDeleted();
        }

        object OnSetupTurret(AutoTurret autoTurret)
        {
            if (autoTurret == null || autoTurret.net == null)
                return null;

            if (ConvoyPathVehicle.GetVehicleByChildNetId(autoTurret.net.ID.Value) != null)
                return true;

            return null;
        }
        #endregion OtherPLugins
        #endregion Hooks

        #region Commands
        [ChatCommand("convoystart")]
        void ChatStartCommand(BasePlayer player, string command, string[] arg)
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
        void ConsoleStartEventCommand(ConsoleSystem.Arg arg)
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
        void ChatStopCommand(BasePlayer player, string command, string[] arg)
        {
            if (player.IsAdmin)
                EventLauncher.StopEvent();
        }

        [ConsoleCommand("convoystop")]
        void ConsoleStopEventCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
                return;

            EventLauncher.StopEvent();
        }

        [ChatCommand("convoyroadblock")]
        void ChatRoadBlockCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            PathList blockRoad = TerrainMeta.Path.Roads.FirstOrDefault(x => x.Path.Points.Any(y => Vector3.Distance(player.transform.position, y) < 10));

            if (blockRoad == null)
            {
                NotifyManager.SendMessageToPlayer(player, $"{_config.prefix} Road <color=#ce3f27>not found</color>. Step onto the required road and enter the command again.");
                return;
            }

            int index = TerrainMeta.Path.Roads.IndexOf(blockRoad);

            if (_config.pathConfig.blockRoads.Contains(index))
            {
                NotifyManager.SendMessageToPlayer(player, $"{_config.prefix} The road is already <color=#ce3f27>blocked</color>");
                return;
            }

            _config.pathConfig.blockRoads.Add(index);
            SaveConfig();

            NotifyManager.SendMessageToPlayer(player, $"{_config.prefix} The road with the index <color=#738d43>{index}</color> is <color=#ce3f27>blocked</color>");
        }

        [ChatCommand("convoyshowpath")]
        void ChatShowPathCommand(BasePlayer player, string command, string[] arg)
        {
            if (player.IsAdmin)
                PathManager.DdrawPath(PathManager.currentPath, player);
        }

        [ChatCommand("convoypathstart")]
        void ChatPathStartCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            PathRecorder.StartRecordingRoute(player);
        }

        [ChatCommand("convoypathsave")]
        void ChatPathSaveCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            if (arg.Length == 0)
            {
                NotifyManager.SendMessageToPlayer(player, "CustomRouteDescription", ins._config.prefix);
                return;
            }

            string pathName = arg[0];
            PathRecorder.TrySaveRoute(player.userID, pathName);
        }

        [ChatCommand("convoypathcancel")]
        void ChatPathCancelCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            PathRecorder.TryCancelRoute(player.userID);
        }

        [ChatCommand("convoystartmoving")]
        void ChatStartMovingCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            ins.eventController.SwitchMoving(true);
        }

        [ChatCommand("convoystopmoving")]
        void ChatStopMovingCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;

            ins.eventController.SwitchMoving(false);
        }

        [ChatCommand("convoytest")]
        void ChatTestCommand(BasePlayer player, string command, string[] arg)
        {
            if (!player.IsAdmin)
                return;
        }
        #endregion Commands

        #region Methods
        void Unsubscribes()
        {
            foreach (string hook in subscribeMetods)
                Unsubscribe(hook);
        }

        void Subscribes()
        {
            foreach (string hook in subscribeMetods)
                Subscribe(hook);
        }

        static void Debug(params object[] arg)
        {
            string result = "";

            foreach (object obj in arg)
                if (obj != null)
                    result += obj.ToString() + " ";

            ins.Puts(result);
        }
        #endregion Methods

        #region Classes 
        static class EventLauncher
        {
            static Coroutine autoEventCoroutine;
            static Coroutine delayedEventStartCorountine;
            static Coroutine fixedScheduleStopCoroutine;
            static DateTime? pendingFixedScheduleEndUtc;
            static DateTime? activeFixedScheduleEndUtc;

            internal static bool IsEventActive()
            {
                return ins != null && ins.eventController != null;
            }

            internal static void AutoStartEvent()
            {
                if (!ins._config.mainConfig.isAutoEvent)
                    return;

                if (autoEventCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(autoEventCoroutine);

                if (ins._config.mainConfig.fixedSchedule != null && ins._config.mainConfig.fixedSchedule.enabled)
                    autoEventCoroutine = ServerMgr.Instance.StartCoroutine(FixedScheduleAutoEventCoroutine());
                else
                    autoEventCoroutine = ServerMgr.Instance.StartCoroutine(AutoEventCorountine());
            }

            internal static void DelayStartEvent(bool isAutoActivated = false, BasePlayer activator = null, string presetName = "")
            {
                if (IsEventActive() || delayedEventStartCorountine != null)
                {
                    NotifyManager.PrintError(activator, "EventActive_Exeption");
                    return;
                }

                if (autoEventCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(autoEventCoroutine);

                EventConfig eventConfig = DefineEventConfig(presetName);
                if (eventConfig == null)
                {
                    NotifyManager.PrintError(activator, "ConfigurationNotFound_Exeption");
                    StopEvent();
                    return;
                }

                delayedEventStartCorountine = ServerMgr.Instance.StartCoroutine(DelayedStartEventCorountine(eventConfig));

                if (!isAutoActivated)
                    NotifyManager.PrintInfoMessage(activator, "SuccessfullyLaunched");
            }

            static IEnumerator AutoEventCorountine()
            {
                yield return CoroutineEx.waitForSeconds(UnityEngine.Random.Range(ins._config.mainConfig.minTimeBetweenEvents, ins._config.mainConfig.maxTimeBetweenEvents));
                yield return CoroutineEx.waitForSeconds(5f);
                DelayStartEvent(true);
            }

            static IEnumerator FixedScheduleAutoEventCoroutine()
            {
                DateTime nextStartUtc;
                DateTime nextEndUtc;

                if (!TryGetNextFixedScheduleWindowUtc(out nextStartUtc, out nextEndUtc))
                    yield break;

                var delay = Mathf.Max(1f, (float)(nextStartUtc - DateTime.UtcNow).TotalSeconds);
                yield return CoroutineEx.waitForSeconds(delay);

                pendingFixedScheduleEndUtc = nextEndUtc;
                DelayStartEvent(true);
            }

            static IEnumerator DelayedStartEventCorountine(EventConfig eventConfig)
            {
                if (ins._config.mainConfig.preStartTime > 0)
                    NotifyManager.SendMessageToAll("ConvoySpawned_" + PresetKey(eventConfig.presetName), ins._config.prefix, ins._config.mainConfig.preStartTime);

                yield return CoroutineEx.waitForSeconds(ins._config.mainConfig.preStartTime);

                StartEvent(eventConfig);
            }

            static void StartEvent(EventConfig eventConfig)
            {
                PathManager.GenerateNewPath();

                GameObject gameObject = new GameObject();
                ins.eventController = gameObject.AddComponent<EventController>();
                ins.eventController.Init(eventConfig);

                if (ins._config.mainConfig.enableStartStopLogs)
                    NotifyManager.PrintLogMessage("EventStart_Log", eventConfig.presetName);

                ApplyFixedScheduleStopIfNeeded();

                Interface.CallHook($"On{ins.Name}Start");
            }

            internal static void StopEvent(bool isPluginUnloadingOrFailed = false)
            {
                pendingFixedScheduleEndUtc = null;
                activeFixedScheduleEndUtc = null;

                if (fixedScheduleStopCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(fixedScheduleStopCoroutine);
                    fixedScheduleStopCoroutine = null;
                }

                if (IsEventActive())
                {
                    ins.Unsubscribes();

                    ins.eventController.DeleteController();
                    ZoneController.TryDeleteZone();
                    PveModeManager.OnEventEnd();
                    EventMapMarker.DeleteMapMarker();
                    NpcSpawnManager.ClearData(true);
                    LootManager.ClearLootData();
                    GuiManager.DestroyAllGui();
                    EconomyManager.OnEventEnd();
                    EventHeli.ClearData();

                    // Если ивент ограблен — финальный «Finish_*» не объявляем (уже был «Looted_*»)
                    if (!ins.eventController.IsLooted)
                        NotifyManager.SendMessageToAll("Finish_" + PresetKey(ins.eventController.eventConfig.presetName), ins._config.prefix);
                    Interface.CallHook($"On{ins.Name}Stop");

                    if (ins._config.mainConfig.enableStartStopLogs)
                        NotifyManager.PrintLogMessage("EventStop_Log");

                    if (!isPluginUnloadingOrFailed)
                        AutoStartEvent();
                }

                if (delayedEventStartCorountine != null)
                {
                    ServerMgr.Instance.StopCoroutine(delayedEventStartCorountine);
                    delayedEventStartCorountine = null;
                }
            }

            static EventConfig DefineEventConfig(string eventPresetName)
            {
                if (eventPresetName != "")
                    return ins._config.eventConfigs.FirstOrDefault(x => x.presetName == eventPresetName);

                HashSet<EventConfig> suitableEventConfigs = ins._config.eventConfigs.Where(x => x.chance > 0 && x.isAutoStart && IsEventConfigSuitableByTime(x));

                if (suitableEventConfigs == null || suitableEventConfigs.Count == 0)
                    return null;

                float sumChance = 0;
                foreach (EventConfig eventConfig in suitableEventConfigs)
                    sumChance += eventConfig.chance;

                float random = UnityEngine.Random.Range(0, sumChance);

                foreach (EventConfig eventConfig in suitableEventConfigs)
                {
                    random -= eventConfig.chance;

                    if (random <= 0)
                        return eventConfig;
                }

                return null;
            }

            static bool IsEventConfigSuitableByTime(EventConfig eventConfig)
            {
                if (eventConfig.minTimeAfterWipe <= 0 && eventConfig.maxTimeAfterWipe <= 0)
                    return true;

                int timeScienceWipe = GetTimeScienceLastWipe();

                if (timeScienceWipe < eventConfig.minTimeAfterWipe)
                    return false;
                if (eventConfig.maxTimeAfterWipe > 0 && timeScienceWipe > eventConfig.maxTimeAfterWipe)
                    return false;

                return true;
            }


            static void ApplyFixedScheduleStopIfNeeded()
            {
                if (pendingFixedScheduleEndUtc == null)
                    return;

                var endUtc = pendingFixedScheduleEndUtc.Value;
                pendingFixedScheduleEndUtc = null;
                activeFixedScheduleEndUtc = endUtc;

                var secondsToEnd = Mathf.Max(1f, (float)(endUtc - DateTime.UtcNow).TotalSeconds);

                if (fixedScheduleStopCoroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(fixedScheduleStopCoroutine);
                    fixedScheduleStopCoroutine = null;
                }

                fixedScheduleStopCoroutine = ServerMgr.Instance.StartCoroutine(FixedScheduleStopCoroutine(secondsToEnd));
            }

            static IEnumerator FixedScheduleStopCoroutine(float delaySeconds)
            {
                yield return CoroutineEx.waitForSeconds(delaySeconds);
                fixedScheduleStopCoroutine = null;

                activeFixedScheduleEndUtc = null;

                if (IsEventActive())
                    StopEvent();
            }

            static bool TryParseHhMm(string raw, out TimeSpan tod)
            {
                tod = TimeSpan.Zero;
                if (string.IsNullOrWhiteSpace(raw)) return false;

                var parts = raw.Trim().Split(':');
                if (parts.Length != 2) return false;

                int h;
                int m;
                if (!int.TryParse(parts[0], out h)) return false;
                if (!int.TryParse(parts[1], out m)) return false;
                if (h < 0 || h > 23 || m < 0 || m > 59) return false;

                tod = new TimeSpan(h, m, 0);
                return true;
            }

            static bool TryGetNextFixedScheduleWindowUtc(out DateTime nextStartUtc, out DateTime nextEndUtc)
            {
                nextStartUtc = default(DateTime);
                nextEndUtc = default(DateTime);

                var fs = ins?._config?.mainConfig?.fixedSchedule;
                if (fs == null || !fs.enabled || fs.windows == null || fs.windows.Count == 0)
                    return false;

                int offsetHours = fs.utcOffsetHours;
                if (offsetHours < -12) offsetHours = -12;
                if (offsetHours > 14) offsetHours = 14;

                var offset = TimeSpan.FromHours(offsetHours);
                var nowTz = DateTime.UtcNow + offset;

                DateTime? bestStart = null;
                DateTime? bestEnd = null;

                foreach (var w in fs.windows)
                {
                    if (w == null) continue;

                    TimeSpan startTod;
                    if (!TryParseHhMm(w.start, out startTod)) continue;

                    TimeSpan endTod;
                    if (!TryParseHhMm(w.end, out endTod)) continue;

                    var candidateStart = nowTz.Date + startTod;
                    if (candidateStart <= nowTz) candidateStart = candidateStart.AddDays(1);

                    var candidateEnd = candidateStart.Date + endTod;
                    if (candidateEnd <= candidateStart)
                        candidateEnd = candidateEnd.AddDays(1);

                    if (!bestStart.HasValue || candidateStart < bestStart.Value)
                    {
                        bestStart = candidateStart;
                        bestEnd = candidateEnd;
                    }
                }

                if (!bestStart.HasValue || !bestEnd.HasValue)
                    return false;

                nextStartUtc = bestStart.Value - offset;
                nextEndUtc = bestEnd.Value - offset;
                return true;
            }

            internal static bool TryGetFixedScheduleRemainingSeconds(out int remainingSeconds)
            {
                remainingSeconds = 0;

                if (activeFixedScheduleEndUtc == null)
                    return false;

                var remain = (activeFixedScheduleEndUtc.Value - DateTime.UtcNow).TotalSeconds;
                if (remain <= 0)
                    return false;

                remainingSeconds = Mathf.Max(1, Mathf.CeilToInt((float)remain));
                return true;
            }

            static int GetTimeScienceLastWipe()
            {
                DateTime startTime = new DateTime(2019, 1, 1, 0, 0, 0);

                double realTime = DateTime.UtcNow.Subtract(startTime).TotalSeconds;
                double wipeTime = SaveRestore.SaveCreatedTime.Subtract(startTime).TotalSeconds;

                return Convert.ToInt32(realTime - wipeTime);
            }
        }

        class EventController : FacepunchBehaviour
        {
            internal bool IsLooted => isEventLooted;

            internal EventConfig eventConfig;
            HashSet<NpcData> npcDatas = new HashSet<NpcData>();
            HashSet<AutoTurret> turrets = new HashSet<AutoTurret>();
            HashSet<SamSite> samsites = new HashSet<SamSite>();
            Coroutine spawnCoroutine;
            Coroutine eventCoroutine;
            int eventTime;
            int agressiveTime;
            int stopTime;
            bool isStopped;
            bool isEventLooted;

            internal int GetEventTime()
            {
                return eventTime;
            }

            internal bool IsFullySpawned()
            {
                return spawnCoroutine == null;
            }

            internal bool IsStopped()
            {
                return isStopped;
            }

            internal bool IsAgressive()
            {
                return ins._config.behaviorConfig.agressiveTime < 0 || agressiveTime > 0 || (ins._config.behaviorConfig.isStopConvoyAgressive && IsStopped());
            }

            internal HashSet<ulong> GetAliveTurretsNetIDS()
            {
                HashSet<ulong> turretsIDs = new HashSet<ulong>();

                foreach (AutoTurret autoTurret in turrets)
                    if (autoTurret.IsExists() && autoTurret.net != null)
                        turretsIDs.Add(autoTurret.net.ID.Value);

                return turretsIDs;
            }

            internal bool IsEventTurret(ulong netID)
            {
                return turrets.Any(x => x != null && x.net != null && x.net.ID.Value == netID);
            }

            internal bool IsEventSamSite(ulong netID)
            {
                return samsites.Any(x => x != null && x.net != null && x.net.ID.Value == netID);
            }

            internal bool IsPlayerCanDealDamage(BasePlayer player, BaseCombatEntity eventEntity, bool shoudSendMessages)
            {
                if (spawnCoroutine != null)
                    return false;

                Vector3 playerGroundPosition = new Vector3(player.transform.position.x, 0, player.transform.position.z);
                Vector3 entityGroundPosition = new Vector3(eventEntity.transform.position.x, 0, eventEntity.transform.position.z);
                float distance = Vector3.Distance(playerGroundPosition, entityGroundPosition);
                float maxDamageDistance = eventEntity is PatrolHelicopter && IsStopped() ? eventConfig.maxHeliDamageDistance : eventConfig.maxGroundDamageDistance;

                if (maxDamageDistance > 0 && distance > maxDamageDistance)
                {
                    if (shoudSendMessages)
                        NotifyManager.SendMessageToPlayer(player, "DamageDistance", ins._config.prefix);

                    return false;
                }

                if (PveModeManager.IsPveModeBlockInterractByCooldown(player))
                {
                    SwitchAgressive(true);
                    NotifyManager.SendMessageToPlayer(player, "PveMode_BlockAction", ins._config.prefix);
                    return false;
                }

                return true;
            }

            internal bool IsPlayerCanLoot(BasePlayer player, bool shoudSendMessages)
            {
                if (IsLootBlockedByThisPlugin())
                {
                    NotifyManager.SendMessageToPlayer(player, "CantLoot", ins._config.prefix);
                    return false;
                }

                if (PveModeManager.IsPveModeBlockInterractByCooldown(player))
                {
                    if (shoudSendMessages)
                        NotifyManager.SendMessageToPlayer(player, "PveMode_BlockAction", ins._config.prefix);

                    return false;
                }

                if (PveModeManager.IsPveModeBlockNoOwnerLooting(player))
                {
                    if (shoudSendMessages)
                        NotifyManager.SendMessageToPlayer(player, "PveMode_YouAreNoOwner", ins._config.prefix);

                    return false;
                }

                return true;
            }

            bool IsLootBlockedByThisPlugin()
            {
                if (agressiveTime <= 0 && ins._config.behaviorConfig.agressiveTime > 0)
                    return true;

                if (ins._config.lootConfig.blockLootingByMove && !IsStopped())
                    return true;

                if (ins._config.lootConfig.blockLootingByBradleys && ConvoyPathVehicle.GetAliveBradleysNetIDS().Count > 0)
                    return true;

                if (ins._config.lootConfig.blockLootingByNpcs && NpcSpawnManager.GetEventNpcCount() > 0)
                    return true;

                if (ins._config.lootConfig.blockLootingByHeli && EventHeli.GetAliveHeliesNetIDS().Count > 0)
                    return true;

                return false;
            }

            internal void Init(EventConfig eventConfig)
            {
                this.eventConfig = eventConfig;
                SpawnConvoy();
            }

            void SpawnConvoy()
            {
                if (PathManager.currentPath == null)
                {
                    EventLauncher.StopEvent();
                    return;
                }

                ins.Subscribes();
                spawnCoroutine = ServerMgr.Instance.StartCoroutine(SpawnCorountine());
            }

            IEnumerator SpawnCorountine()
            {
                ConvoyPathVehicle lastSpawnedVehicle = null;
                float lastVehicleSpawnTime = UnityEngine.Time.realtimeSinceStartup;

                foreach (string vehiclePreset in eventConfig.vehiclesOrder)
                {
                    while (lastSpawnedVehicle != null && Vector3.Distance(lastSpawnedVehicle.transform.position, PathManager.currentPath.startPathPoint.position) < 10)
                    {
                        if (UnityEngine.Time.realtimeSinceStartup - lastVehicleSpawnTime > 30)
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

                    lastVehicleSpawnTime = UnityEngine.Time.realtimeSinceStartup;
                    lastSpawnedVehicle = convoyVehicle;
                }

                yield return CoroutineEx.waitForSeconds(3f);

                if (eventConfig.heliPreset != "" && eventConfig.isHeli)
                {
                    HeliConfig heliConfig = ins._config.heliConfigs.FirstOrDefault(x => x.presetName == eventConfig.heliPreset);

                    if (heliConfig == null)
                        NotifyManager.PrintError(null, "PresetNotFound_Exeption", eventConfig.heliPreset);
                    else
                        EventHeli.SpawnHeli(heliConfig);
                }

                yield return CoroutineEx.waitForSeconds(1);
                OnSpawnFinished();
            }

            internal void OnSpawnFailed()
            {
                KillConvoy();
                ins.Unsubscribes();

                if (spawnCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(spawnCoroutine);

                SpawnConvoy();
            }

            ConvoyPathVehicle SpawnConvoyVehicle(string presetName, ConvoyPathVehicle frontVehicle)
            {
                ConvoyPathVehicle convoyVehicle = null;
                VehicleConfig vehicleConfig = null;
                ModularCarConfig modularCarConfig = ins._config.modularCarConfigs.FirstOrDefault(x => x.presetName == presetName);

                if (modularCarConfig != null)
                {
                    convoyVehicle = ModularCarVehicle.SpawnModularCar(modularCarConfig, frontVehicle);
                    vehicleConfig = modularCarConfig;
                }

                if (convoyVehicle == null)
                {
                    BradleyConfig bradleyConfig = ins._config.bradleyConfigs.FirstOrDefault(x => x.presetName == presetName);

                    if (bradleyConfig != null)
                    {
                        convoyVehicle = BradleyVehicle.SpawnBradley(bradleyConfig, frontVehicle);
                        vehicleConfig = bradleyConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    TravellingVendorConfig travellingVendorConfig = ins._config.tarvelingVendorConfigs.FirstOrDefault(x => x.presetName == presetName);

                    if (travellingVendorConfig != null)
                    {
                        convoyVehicle = TravellingVendorVehicle.SpawnTravellingVendor(travellingVendorConfig, frontVehicle);
                        vehicleConfig = travellingVendorConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    SedanConfig sedanConfig = ins._config.sedanConfigs.FirstOrDefault(x => x.presetName == presetName);

                    if (sedanConfig != null)
                    {
                        convoyVehicle = SedanVehicle.SpawnSedan(sedanConfig, frontVehicle);
                        vehicleConfig = sedanConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    BikeConfig bikeConfig = ins._config.bikeConfigs.FirstOrDefault(x => x.presetName == presetName);

                    if (bikeConfig != null)
                    {
                        convoyVehicle = BikeVehicle.SpawnBike(bikeConfig, frontVehicle);
                        vehicleConfig = bikeConfig;
                    }
                }

                if (convoyVehicle == null)
                {
                    KaruzaCarConfig customCarConfig = ins._config.karuzaCarConfigs.FirstOrDefault(x => x.presetName == presetName);

                    if (customCarConfig != null)
                    {
                        convoyVehicle = KaruzaCarVehicle.SpawnVehicle(customCarConfig, frontVehicle);
                        vehicleConfig = customCarConfig;
                    }
                }


                if (convoyVehicle == null)
                    return null;

                foreach (PresetLocationConfig presetLocationConfig in vehicleConfig.turretLocations)
                    SpawnTurret(presetLocationConfig, convoyVehicle.baseEntity);

                foreach (PresetLocationConfig presetLocationConfig in vehicleConfig.samsiteLocations)
                    SpawnSamSite(presetLocationConfig, convoyVehicle.baseEntity);

                foreach (PresetLocationConfig presetLocationConfig in vehicleConfig.crateLocations)
                    SpawnCrate(presetLocationConfig, convoyVehicle.baseEntity);

                InitialSpawnVehicleNpc(vehicleConfig, convoyVehicle.baseEntity);

                return convoyVehicle;
            }

            void SpawnTurret(PresetLocationConfig presetLocationConfig, BaseEntity parentEntity)
            {
                TurretConfig turretConfig = ins._config.turretConfigs.FirstOrDefault(x => x.presetName == presetLocationConfig.presetName);

                if (turretConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", presetLocationConfig.presetName);
                    return;
                }

                AutoTurret autoTurret = BuildManager.SpawnChildEntity(parentEntity, "assets/prefabs/npc/autoturret/autoturret_deployed.prefab", presetLocationConfig, 0, isDecor: false) as AutoTurret;
                turrets.Add(autoTurret);
                BuildManager.UpdateEntityMaxHealth(autoTurret, turretConfig.hp);

                autoTurret.inventory.Insert(ItemManager.CreateByName(turretConfig.shortNameWeapon));
                autoTurret.inventory.Insert(ItemManager.CreateByName(turretConfig.shortNameAmmo, turretConfig.countAmmo));

                autoTurret.isLootable = false;
                autoTurret.dropFloats = false;
                autoTurret.dropsLoot = ins._config.mainConfig.isTurretDropWeapon;

                autoTurret.SetFlag(AutoTurret.Flags.Busy, true);
                autoTurret.SetFlag(AutoTurret.Flags.Locked, true);

                if (turretConfig.targetLossRange != 0)
                    autoTurret.sightRange = turretConfig.targetLossRange;

                TurretOptimizer.Attach(autoTurret, turretConfig.targetDetectionRange == 0 ? 30 : turretConfig.targetDetectionRange);
            }

            void SpawnSamSite(PresetLocationConfig presetLocationConfig, BaseEntity parentEntity)
            {
                SamSiteConfig samSiteConfig = ins._config.samsiteConfigs.FirstOrDefault(x => x.presetName == presetLocationConfig.presetName);

                if (samSiteConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", presetLocationConfig.presetName);
                    return;
                }

                SamSite samSite = BuildManager.SpawnChildEntity(parentEntity, "assets/prefabs/npc/sam_site_turret/sam_site_turret_deployed.prefab", presetLocationConfig, 0, false) as SamSite;
                samsites.Add(samSite);
                BuildManager.UpdateEntityMaxHealth(samSite, samSiteConfig.hp);

                samSite.inventory.Insert(ItemManager.CreateByName("ammo.rocket.sam", samSiteConfig.countAmmo));
                samSite.isLootable = false;
                samSite.dropFloats = false;
                samSite.dropsLoot = false;

                samSite.inventory.SetLocked(true);
                samSite.SetFlag(BaseEntity.Flags.Locked, true);
                samSite.SetFlag(BaseEntity.Flags.Busy, true);
            }

            void SpawnCrate(PresetLocationConfig presetLocationConfig, BaseEntity parentEntity)
            {
                CrateConfig crateConfig = ins._config.crateConfigs.FirstOrDefault(x => x.presetName == presetLocationConfig.presetName);

                if (crateConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", presetLocationConfig.presetName);
                    return;
                }

                BaseEntity crateEntity = BuildManager.SpawnChildEntity(parentEntity, crateConfig.prefabName, presetLocationConfig, crateConfig.skin, false);

                LootContainer lootContainer = crateEntity as LootContainer;
                if (lootContainer != null)
                {
                    LootManager.UpdateLootContainer(lootContainer, crateConfig);
                    return;
                }

                StorageContainer storageContainer = crateEntity as StorageContainer;
                if (storageContainer != null)
                    LootManager.UpdateStorageContainer(storageContainer, crateConfig);

                Fridge fridge = crateEntity as Fridge;
                if (fridge != null)
                    LootManager.UpdateFridgeContainer(fridge, crateConfig);

                DroppedItemContainer droppedItemContainer = crateEntity as DroppedItemContainer;
                if (droppedItemContainer != null)
                    LootManager.UpdateItemContainer(droppedItemContainer.inventory, crateConfig.lootTableConfig);
            }

            void InitialSpawnVehicleNpc(VehicleConfig npcSpawnedVehicleConfig, BaseEntity parentEntity)
            {
                BaseVehicle baseVehicle = parentEntity as BaseVehicle;

                if (baseVehicle != null)
                {
                    int countOfNpc = 0;

                    foreach (MountPointInfo mountPointInfo in baseVehicle.allMountPoints)
                    {
                        NpcData npcData = new NpcData
                        {
                            healthFraction = 1,
                            npcPresetName = npcSpawnedVehicleConfig.npcPresetName,
                            convoyVehicle = parentEntity
                        };

                        if (mountPointInfo != null)
                        {
                            npcData.scientistNPC = NpcSpawnManager.SpawnScientistNpc(npcSpawnedVehicleConfig.npcPresetName, baseVehicle.transform.position, 1, true, mountPointInfo.isDriver);

                            if (npcData.scientistNPC == null)
                                continue;

                            npcData.baseMountable = mountPointInfo.mountable;
                            npcData.baseMountable.AttemptMount(npcData.scientistNPC, false);
                            npcData.isDriver = mountPointInfo.isDriver;
                            npcData.isDismount = true;
                            countOfNpc++;
                        }

                        npcDatas.Add(npcData);

                        if (countOfNpc >= npcSpawnedVehicleConfig.numberOfNpc)
                            break;
                    }
                }

                if (npcSpawnedVehicleConfig.additionalNpcs.Count > 0)
                {
                    foreach (NpcPoseConfig npcPoseConfig in npcSpawnedVehicleConfig.additionalNpcs)
                    {
                        if (!npcPoseConfig.isEnable)
                            continue;

                        NpcData npcData = new NpcData
                        {
                            healthFraction = 1,
                            npcPresetName = string.IsNullOrEmpty(npcPoseConfig.npcPresetName) ? npcSpawnedVehicleConfig.npcPresetName : npcPoseConfig.npcPresetName,
                            convoyVehicle = parentEntity
                        };

                        npcData.baseMountable = MovableBaseMountable.CreateMovableBaseMountable(npcPoseConfig.seatPrefab, parentEntity, npcPoseConfig.position.ToVector3(), npcPoseConfig.rotation.ToVector3());
                        npcData.scientistNPC = NpcSpawnManager.SpawnScientistNpc(npcData.npcPresetName, parentEntity.transform.position, 1, true, false);
                        npcData.baseMountable.isMobile = true;
                        npcData.baseMountable.ignoreVehicleParent = true;
                        npcData.isDismount = npcPoseConfig.isDismount;

                        if (npcData.scientistNPC == null || npcData.baseMountable == null)
                            continue;

                        npcData.baseMountable.AttemptMount(npcData.scientistNPC, false);

                        if (baseVehicle != null)
                        {
                            npcData.baseMountable.dismountPositions = baseVehicle.dismountPositions;
                        }
                        else
                        {
                            CustomBradley customBradley = parentEntity as CustomBradley;
                            if (customBradley != null)
                                npcData.baseMountable.dismountPositions = customBradley.GetDismountPositions();
                        }

                        npcDatas.Add(npcData);
                    }
                }
            }

            void OnSpawnFinished()
            {
                spawnCoroutine = null;
                eventTime = eventConfig.eventTime;

                int fixedScheduleSeconds;
                if (EventLauncher.TryGetFixedScheduleRemainingSeconds(out fixedScheduleSeconds))
                    eventTime = fixedScheduleSeconds;
                EventMapMarker.CreateMarker();
                eventCoroutine = ServerMgr.Instance.StartCoroutine(EventCorountine());
                PathManager.OnSpawnFinish();
                SwitchAgressive(IsAgressive());
                LootManager.UpdateCountOfUnlootedCrates();
                NotifyManager.SendMessageToAll("EventStart_" + PresetKey(eventConfig.presetName), ins._config.prefix, eventConfig.displayName, MapHelper.GridToString(MapHelper.PositionToGrid(PathManager.currentPath.startPathPoint.position)));
            }

            IEnumerator EventCorountine()
            {
                while (eventTime > 0 || (!isEventLooted && ins._config.mainConfig.dontStopEventIfPlayerInZone && ZoneController.IsAnyPlayerInEventZone()))
                {
                    if (ins._config.notifyConfig.timeNotifications.Contains(eventTime) && !isEventLooted)
                        NotifyManager.SendMessageToAll("RemainTime", ins._config.prefix, eventConfig.displayName, eventTime);

                    if (isStopped && !isEventLooted)
                    {
                        stopTime--;

                        if (stopTime <= 0 && NpcSpawnManager.GetEventNpcCount() > 0)
                        {
                            SwitchMoving(true);
                        }
                    }

                    if (!IsStopped() && agressiveTime > 0)
                    {
                        agressiveTime--;

                        if (agressiveTime <= 0)
                            SwitchAgressive(false);
                    }

                    if (eventTime % 30 == 0 && eventConfig.eventTime - eventTime > 30)
                        EventPassingCheck();

                    if (!IsAgressive())
                        UpdateMountedNpcsLookRotation();

                    if (eventTime > 0)
                        eventTime--;

                    yield return CoroutineEx.waitForSeconds(1);
                }

                EventLauncher.StopEvent();
            }

            internal void EventPassingCheck()
            {
                if (isEventLooted)
                    return;

                LootManager.UpdateCountOfUnlootedCrates();
                int countOfUnlootedCrates = LootManager.GetCountOfUnlootedCrates();

                if (countOfUnlootedCrates == 0)
                {
                    isEventLooted = true;
                    SwitchMoving(false);

                    int _ignoreFixedScheduleSeconds;
                    if (!EventLauncher.TryGetFixedScheduleRemainingSeconds(out _ignoreFixedScheduleSeconds))
                        eventTime = ins._config.mainConfig.endAfterLootTime;

                    NotifyManager.SendMessageToAll("Looted_" + PresetKey(eventConfig.presetName), ins._config.prefix, eventConfig.displayName);
                }
            }

            internal void OnEventAttacked(BasePlayer player)
            {
                Invoke(() =>
                {
                    if (player != null && ((agressiveTime <= 0 && ins._config.behaviorConfig.agressiveTime > 0) || !isStopped))
                    {
                        Interface.CallHook($"OnConvoyAttacked", player, ConvoyPathVehicle.GetEventPosition());
                        NotifyManager.SendMessageToAll("ConvoyAttacked", ins._config.prefix, player.displayName);
                    }

                    SwitchAgressive(true);
                    SwitchMoving(false, player);
                }, 0);
            }

            internal void SwitchAgressive(bool isAgressive)
            {
                if (isAgressive)
                    agressiveTime = ins._config.behaviorConfig.agressiveTime;

                bool shoudEnableTurrets = isAgressive || ins._config.behaviorConfig.agressiveTime <= 0;

                foreach (AutoTurret autoTurret in turrets)
                {
                    if (!autoTurret.IsExists())
                        continue;

                    if (shoudEnableTurrets && autoTurret.IsPowered())
                        continue;

                    autoTurret.UpdateFromInput(shoudEnableTurrets ? 10 : 0, 0);
                }

                foreach (SamSite samSite in samsites)
                    if (samSite.IsExists())
                        samSite.UpdateFromInput(shoudEnableTurrets ? 100 : 0, 0);
            }

            internal void SwitchMoving(bool isMoving, BasePlayer attacker = null)
            {
                if (spawnCoroutine != null)
                    return;

                if (isMoving)
                {
                    if (IsStopped())
                    {
                        ReplaceByMountedNpcs();
                        ZoneController.TryDeleteZone();
                        ConvoyPathVehicle.SwitchMoving(true);
                        stopTime = 0;
                        isStopped = false;
                        Interface.CallHook($"OnConvoyStartMoving", ConvoyPathVehicle.GetEventPosition());
                    }
                }
                else
                {
                    if (!IsStopped())
                    {
                        stopTime = ins._config.behaviorConfig.stopTime;
                        ReplaceByRoamNpcs();
                        ZoneController.CreateZone(attacker != null && ins._config.supportedPluginsConfig.pveMode.ownerIsStopper && !PveModeManager.IsPlayerHaveCooldown(attacker.userID) ? attacker : null);
                        ConvoyPathVehicle.SwitchMoving(false);
                        isStopped = true;
                        Interface.CallHook($"OnConvoyStopMoving", ConvoyPathVehicle.GetEventPosition());
                    }
                    else
                    {
                        stopTime = ins._config.behaviorConfig.stopTime;
                        isStopped = true;
                    }
                }
            }

            void ReplaceByRoamNpcs()
            {
                foreach (NpcData npcData in npcDatas)
                {
                    if (npcData.isRoaming || !npcData.isDismount || !npcData.scientistNPC.IsExists())
                        continue;

                    Vector3 spawnPosition;
                    NavMeshHit navMeshHit;

                    if (PositionDefiner.GetNavmeshInPoint(npcData.scientistNPC.transform.position, 5f, out navMeshHit))
                        spawnPosition = navMeshHit.position;
                    else
                        continue;

                    npcData.scientistNPC.Kill();
                    npcData.scientistNPC = NpcSpawnManager.SpawnScientistNpc(npcData.npcPresetName, spawnPosition, npcData.scientistNPC.healthFraction, false);
                    npcData.isRoaming = true;
                }
            }

            void ReplaceByMountedNpcs()
            {
                foreach (NpcData npcData in npcDatas)
                {
                    if (npcData.isDriver && !npcData.scientistNPC.IsExists())
                    {
                        NpcData newDriver = npcDatas.FirstOrDefault(x => x.convoyVehicle == npcData.convoyVehicle && x.scientistNPC.IsExists() && !x.isDriver);

                        if (newDriver == null)
                            newDriver = npcDatas.FirstOrDefault(x => x.scientistNPC.IsExists() && !x.isDriver);

                        if (newDriver == null)
                            break;

                        npcData.scientistNPC = newDriver.scientistNPC;
                        npcData.npcPresetName = newDriver.npcPresetName;

                        newDriver.scientistNPC = null;
                    }
                }

                foreach (NpcData npcData in npcDatas)
                {
                    if (npcData.baseMountable == null || (!npcData.isDriver && (!npcData.scientistNPC.IsExists() || !npcData.isRoaming)) || !npcData.isDismount)
                    {
                        if (npcData.scientistNPC.IsExists())
                            npcData.scientistNPC.Kill();

                        continue;
                    }

                    if (npcData.scientistNPC.IsExists())
                    {
                        npcData.healthFraction = npcData.scientistNPC.healthFraction;
                        npcData.scientistNPC.Kill();
                    }

                    npcData.scientistNPC = NpcSpawnManager.SpawnScientistNpc(npcData.npcPresetName, npcData.baseMountable.transform.position, 1, true, npcData.isDriver);
                    npcData.baseMountable.AttemptMount(npcData.scientistNPC, false);
                    npcData.isRoaming = false;
                }
            }

            void UpdateMountedNpcsLookRotation()
            {
                foreach (NpcData npcData in npcDatas)
                {
                    if (!npcData.isDriver && npcData.scientistNPC != null && npcData.scientistNPC.isMounted)
                    {
                        npcData.scientistNPC.OverrideViewAngles(npcData.baseMountable.mountAnchor.transform.rotation.eulerAngles);
                        npcData.scientistNPC.SendNetworkUpdate();
                    }
                }
            }

            internal void DeleteController()
            {
                if (eventCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(eventCoroutine);

                if (spawnCoroutine != null)
                    ServerMgr.Instance.StopCoroutine(spawnCoroutine);

                KillConvoy();
                GameObject.Destroy(this);
            }

            void KillConvoy()
            {
                foreach (AutoTurret autoTurret in turrets)
                {
                    if (autoTurret.IsExists())
                    {
                        AutoTurret.interferenceUpdateList.Remove(autoTurret);
                        autoTurret.Kill();
                    }
                }

                ConvoyPathVehicle.KillAllVehicles();
            }

            class NpcData
            {
                internal ScientistNPC scientistNPC;
                internal BaseEntity convoyVehicle;
                internal BaseMountable baseMountable;
                internal string npcPresetName;
                internal float healthFraction;
                internal bool isDriver;
                internal bool isRoaming;
                internal bool isDismount;
            }
        }


        class TravellingVendorVehicle : DirectControlVehicle
        {
            TravellingVendor travellingVendor;
            TravellingVendorConfig travellingVendorConfig;
            const float power = 2500;

            internal static TravellingVendorVehicle SpawnTravellingVendor(TravellingVendorConfig travellingVendorConfig, ConvoyPathVehicle frontVehicle)
            {
                TravellingVendor travellingVendor = BuildManager.SpawnRegularEntity("assets/prefabs/npc/travelling vendor/travellingvendor.prefab", PathManager.currentPath.startPathPoint.position + Vector3.up, Quaternion.LookRotation(PathManager.currentPath.spawnRotation)) as TravellingVendor;
                TravellingVendorVehicle convoyTravellingVendor = travellingVendor.gameObject.AddComponent<TravellingVendorVehicle>();
                convoyTravellingVendor.Init(travellingVendor, travellingVendorConfig, frontVehicle);
                return convoyTravellingVendor;
            }

            void Init(TravellingVendor travellingVendor, TravellingVendorConfig travellingVendorConfig, ConvoyPathVehicle frontVehicle)
            {
                this.travellingVendor = travellingVendor;
                this.travellingVendorConfig = travellingVendorConfig;

                Rigidbody rigidbody = travellingVendor.GetComponentInChildren<Rigidbody>();
                UpdateTravellingVendor();
                EntityDecorator.DecorateEntity(travellingVendor, ins.entityCustomizationData["van_default"]);
                Init(travellingVendor, GetWheelDatas(), rigidbody, frontVehicle, power);
                PostSpawnUpdate();
            }

            HashSet<WheelData> GetWheelDatas()
            {
                HashSet<WheelData> wheelDatas = new HashSet<WheelData>();
                HashSet<VisualCarWheel> visualCarWheels = new HashSet<VisualCarWheel>
                {
                    travellingVendor.GetPrivateFieldValue("wheelFL") as VisualCarWheel,
                    travellingVendor.GetPrivateFieldValue("wheelFR") as VisualCarWheel,
                    travellingVendor.GetPrivateFieldValue("wheelRL") as VisualCarWheel,
                    travellingVendor.GetPrivateFieldValue("wheelRR") as VisualCarWheel
                };

                foreach (VisualCarWheel visualCarWheel in visualCarWheels)
                {
                    WheelData wheelData = new WheelData(visualCarWheel.wheelCollider, visualCarWheel.steerWheel);
                    wheelDatas.Add(wheelData);
                }

                return wheelDatas;
            }

            void UpdateTravellingVendor()
            {
                travellingVendor.SetFlag(TravellingVendor.Flags.Busy, true);
                travellingVendor.DoAI = false;
                travellingVendor.SetPrivateFieldValue("currentPath", new List<Vector3> { Vector3.zero });

                foreach (BaseEntity baseEntity in travellingVendor.children.ToHashSet())
                    if (baseEntity.IsExists())
                        baseEntity.Kill();

                if (travellingVendorConfig.deleteMapMarker)
                {
                    MapMarker mapMarker = travellingVendor.GetPrivateFieldValue("mapMarkerInstance") as MapMarker;

                    if (mapMarker.IsExists())
                        mapMarker.Kill();
                }

                BuildManager.DestroyEntityConponents<TriggerBase>(travellingVendor);
            }

            void PostSpawnUpdate()
            {
                HashSet<BaseEntity> doors = travellingVendor.children.Where(x => x is Door);

                foreach (BaseEntity doorEntity in doors)
                {
                    Door door = doorEntity as Door;

                    if (door == null)
                        continue;

                    BuildManager.UpdateEntityMaxHealth(door, travellingVendorConfig.doorHealth);
                    door.skinID = travellingVendorConfig.doorSkin;

                    if (travellingVendorConfig.isLocked)
                    {
                        CodeLock codeLock = GameManager.server.CreateEntity("assets/prefabs/locks/keypad/lock.code.prefab") as CodeLock;
                        codeLock.SetParent(door, door.GetSlotAnchorName(BaseEntity.Slot.Lock));
                        codeLock.Spawn();
                        codeLock.code = UnityEngine.Random.Range(0, 9999).ToString();
                        codeLock.hasCode = true;
                        door.SetSlot(BaseEntity.Slot.Lock, codeLock);
                        codeLock.SetFlag(BaseEntity.Flags.Locked, true);
                    }
                }
            }

            protected override void StopMoving()
            {
                travellingVendor.SetFlag(TravellingVendor.Flags.Reserved2, true);
                travellingVendor.SetFlag(TravellingVendor.Flags.Reserved4, true);
                base.StopMoving();
            }

            protected override void StartMoving()
            {
                travellingVendor.SetFlag(TravellingVendor.Flags.Reserved2, false);
                travellingVendor.SetFlag(TravellingVendor.Flags.Reserved4, false);
                base.StartMoving();
            }
        }

        class SedanVehicle : DirectControlVehicle
        {
            BasicCar basicCar;
            SedanConfig sedanConfig;
            const float power = 1500;

            internal static SedanVehicle SpawnSedan(SedanConfig sedanConfig, ConvoyPathVehicle frontVehicle)
            {
                BasicCar basicCar = BuildManager.SpawnRegularEntity("assets/content/vehicles/sedan_a/sedantest.entity.prefab", PathManager.currentPath.startPathPoint.position + Vector3.up, Quaternion.LookRotation(PathManager.currentPath.spawnRotation)) as BasicCar;
                SedanVehicle sedanVehicle = basicCar.gameObject.AddComponent<SedanVehicle>();
                sedanVehicle.Init(basicCar, sedanConfig, frontVehicle);
                return sedanVehicle;
            }

            void Init(BasicCar basicCar, SedanConfig sedanConfig, ConvoyPathVehicle frontVehicle)
            {
                this.basicCar = basicCar;
                this.sedanConfig = sedanConfig;
                base.Init(basicCar, GetWheelDatas(), basicCar.rigidBody, frontVehicle, power);
                BaseVehicle.AllMountables.Remove(basicCar);
                BuildManager.UpdateEntityMaxHealth(basicCar, sedanConfig.hp);

                if (sedanConfig.crateLocations.Count > 0)
                    EntityDecorator.DecorateEntity(basicCar, ins.entityCustomizationData["sedan_default"]);
            }

            HashSet<WheelData> GetWheelDatas()
            {
                HashSet<WheelData> wheelDatas = new HashSet<WheelData>();

                foreach (BasicCar.VehicleWheel vehicleWheel in basicCar.wheels)
                {
                    WheelData wheelData = new WheelData(vehicleWheel.wheelCollider, vehicleWheel.steerWheel);
                    wheelDatas.Add(wheelData);
                }

                return wheelDatas;
            }

            protected override void UpdateMoving()
            {
                if (ins.eventController.IsStopped())
                    return;
                bool tooHardTurn;
                float turnFraction = GetTurnFraction(out tooHardTurn);
                basicCar.SetFlag(BasicCar.Flags.Reserved4, turnFraction < -5f);
                basicCar.SetFlag(BasicCar.Flags.Reserved5, turnFraction > 5f);
                basicCar.SetFlag(BasicCar.Flags.Reserved1, !ins.eventController.IsStopped());
                basicCar.SetFlag(BasicCar.Flags.Reserved2, !ins.eventController.IsStopped() && TOD_Sky.Instance.IsNight);

                base.UpdateMoving();
            }
        }

        class DirectControlVehicle : ConvoyPathVehicle
        {
            HashSet<WheelData> wheelDatas;
            float basePower;

            protected void Init(BaseEntity entity, HashSet<WheelData> wheelDatas, Rigidbody rigidbody, ConvoyPathVehicle frontVehicle, float basePower)
            {
                this.wheelDatas = wheelDatas;
                this.basePower = basePower;
                base.Init(entity, rigidbody, frontVehicle);
            }

            protected override void UpdateMoving()
            {
                if (ins.eventController.IsStopped())
                    return;

                float speedFraction = GetSpeedFraction();
                bool tooHardTurn;
                float turnFraction = GetTurnFraction(out tooHardTurn);

                bool shoudBradke = isStopped || speedFraction < 0;
                Brake(shoudBradke);

                if (tooHardTurn)
                {
                    if (!rigidbody.isKinematic)
                        rigidbody.velocity = Vector3.zero;

                    baseEntity.transform.Rotate(Vector3.up, 180);
                    return;
                }

                foreach (WheelData wheelData in wheelDatas)
                {
                    wheelData.wheelCollider.motorTorque = !shoudBradke && wheelData.wheelCollider.isGrounded ? basePower : 0;

                    if (wheelData.isSteering)
                        wheelData.wheelCollider.steerAngle = turnFraction;
                }

                baseEntity.SendNetworkUpdate();
            }

            void Brake(bool isEnable)
            {
                foreach (WheelData wheelData in wheelDatas)
                    wheelData.wheelCollider.brakeTorque = isEnable ? 1000f : 0;
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

        class WheelData
        {
            public WheelCollider wheelCollider;
            public bool isSteering;

            public WheelData(WheelCollider wheelCollider, bool isSteering)
            {
                this.wheelCollider = wheelCollider;
                this.isSteering = isSteering;
            }
        }


        class BradleyVehicle : ConvoyPathVehicle
        {
            internal BradleyConfig bradleyConfig;
            internal CustomBradley bradley;

            internal static BradleyVehicle SpawnBradley(BradleyConfig bradleyConfig, ConvoyPathVehicle frontVehicle)
            {
                CustomBradley customBradley = CustomBradley.CreateCustomBradley(PathManager.currentPath.startPathPoint.position + Vector3.up, Quaternion.LookRotation(PathManager.currentPath.spawnRotation), bradleyConfig);
                BradleyVehicle convoyBradley = customBradley.gameObject.AddComponent<BradleyVehicle>();
                convoyBradley.Init(customBradley, bradleyConfig, frontVehicle);
                return convoyBradley;
            }

            void Init(CustomBradley customBradley, BradleyConfig bradleyConfig, ConvoyPathVehicle frontVehicle)
            {
                this.bradley = customBradley;
                this.bradleyConfig = bradleyConfig;
                UpdateBradley();
                base.Init(customBradley, customBradley.myRigidBody, frontVehicle);
            }

            void UpdateBradley()
            {
                BuildManager.UpdateEntityMaxHealth(bradley, bradleyConfig.hp);
                bradley.maxCratesToSpawn = bradleyConfig.countCrates;
                bradley.viewDistance = bradleyConfig.viewDistance;
                bradley.searchRange = bradleyConfig.searchDistance;
                bradley.coaxAimCone *= bradleyConfig.coaxAimCone;
                bradley.coaxFireRate *= bradleyConfig.coaxFireRate;
                bradley.coaxBurstLength = bradleyConfig.coaxBurstLength;
                bradley.nextFireTime = bradleyConfig.nextFireTime;
                bradley.topTurretFireRate = bradleyConfig.topTurretFireRate;
                bradley.finalDestination = Vector3.zero;
                bradley.moveForceMax = 4000f;
                bradley.myRigidBody.maxAngularVelocity = 2.5f;
            }

            protected override void UpdateMoving()
            {
                if (ins.eventController.IsStopped())
                    return;

                float speedFraction = GetSpeedFraction();
                bool tooHardTurn;
                float turnFraction = GetTurnFraction(out tooHardTurn);

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
                    if (!rigidbody.isKinematic)
                        rigidbody.velocity = Vector3.zero;

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

            protected override void StartMoving()
            {
                base.StartMoving();
            }
        }

        class CustomBradley : BradleyAPC
        {
            internal BradleyConfig bradleyConfig;
            Transform[] dismountLocalPositions;
            static List<Vector3> dismountPositions = new List<Vector3>
            {
                new Vector3(1f, 0.5f, -3.5f),
                new Vector3(-1f, 0.5f, -3.5f),
                new Vector3(2.5f, 0.5f, -2f),
                new Vector3(-2.5f, 0.5f, -2f),
                new Vector3(0f, 0.5f, -3.5f),
                new Vector3(-2.5f, 0.5f, 1.7f),
                new Vector3(-0.9f, 0.5f, 4.5f),
                new Vector3(0.9f, 0.5f, 4.5f),
                new Vector3(2.5f, 0.5f, 1.7f),
            };

            internal static CustomBradley CreateCustomBradley(Vector3 position, Quaternion rotation, BradleyConfig bradleyConfig)
            {
                BradleyAPC bradley = GameManager.server.CreateEntity("assets/prefabs/npc/m2bradley/bradleyapc.prefab", position, rotation) as BradleyAPC;
                bradley.skinID = 755446;
                bradley.enableSaving = false;
                bradley.ScientistSpawns.Clear();
                CustomBradley customBradley = bradley.gameObject.AddComponent<CustomBradley>();
                BuildManager.CopySerializableFields(bradley, customBradley);
                bradley.StopAllCoroutines();
                UnityEngine.GameObject.DestroyImmediate(bradley, true);

                customBradley.bradleyConfig = bradleyConfig;
                customBradley.Spawn();

                TriggerHurtNotChild[] triggerHurts = customBradley.GetComponentsInChildren<TriggerHurtNotChild>();

                foreach (TriggerHurtNotChild triggerHurt in triggerHurts)
                    triggerHurt.enabled = false;

                return customBradley;
            }

            new void FixedUpdate()
            {
                SetFlag(Flags.Reserved5, TOD_Sky.Instance.IsNight);

                if (ins.eventController.IsAgressive())
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

            void UpdateTarget()
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

            internal Transform[] GetDismountPositions()
            {
                return dismountLocalPositions;
            }
        }


        class ModularCarVehicle : MountableVehicle
        {
            const float basePower = 800;

            ModularCar modularCar;
            internal ModularCarConfig modularCarConfig;
            HashSet<VehicleModuleEngine> engines = new HashSet<VehicleModuleEngine>();

            internal static ModularCarVehicle SpawnModularCar(ModularCarConfig modularCarConfig, ConvoyPathVehicle ftontVehicle)
            {
                ModularCar modularCar = ModularCarManager.SpawnModularCar(PathManager.currentPath.startPathPoint.position, Quaternion.LookRotation(PathManager.currentPath.spawnRotation), modularCarConfig.modules);
                ModularCarVehicle modularCarVehicle = modularCar.gameObject.AddComponent<ModularCarVehicle>();
                modularCarVehicle.Init(modularCar, modularCarConfig, ftontVehicle);
                return modularCarVehicle;
            }

            void Init(ModularCar modularCar, ModularCarConfig modularCarConfig, ConvoyPathVehicle ftontVehicle)
            {
                base.Init(modularCar, modularCar.engineController, modularCar.carSettings, ftontVehicle);
                this.modularCar = modularCar;
                this.modularCarConfig = modularCarConfig;
                modularCar.Invoke(DelayUpdate, 1f);
            }

            void DelayUpdate()
            {
                GetEngines();
                UpdatePower();
            }

            void GetEngines()
            {
                foreach (BaseVehicleModule module in modularCar.AttachedModuleEntities)
                {
                    VehicleModuleEngine vehicleModuleEngine = module as VehicleModuleEngine;

                    if (vehicleModuleEngine != null)
                        engines.Add(vehicleModuleEngine);
                }
            }

            protected void UpdatePower()
            {
                if (engines.Count == 0)
                    return;

                float power = basePower / engines.Count;

                foreach (VehicleModuleEngine vehicleModuleEngine in engines)
                    vehicleModuleEngine.engine.engineKW = (int)(power);
            }

            protected override void UpdateMoving()
            {
                base.UpdateMoving();
            }
        }

        class BikeVehicle : MountableVehicle
        {
            Bike bike;
            BikeConfig bikeConfig;

            internal static BikeVehicle SpawnBike(BikeConfig bikeConfig, ConvoyPathVehicle ftontVehicle)
            {
                Bike bike = BuildManager.SpawnRegularEntity(bikeConfig.prefabName, PathManager.currentPath.startPathPoint.position + Vector3.up, Quaternion.LookRotation(PathManager.currentPath.spawnRotation)) as Bike;
                BikeVehicle bikeVehicle = bike.gameObject.AddComponent<BikeVehicle>();
                bikeVehicle.Init(bike, bikeConfig, ftontVehicle);
                return bikeVehicle;
            }

            void Init(Bike bike, BikeConfig bikeConfig, ConvoyPathVehicle ftontVehicle)
            {
                this.bike = bike;
                this.bikeConfig = bikeConfig;
                ModularCarManager.UpdateFuelSystem(bike);
                BuildManager.UpdateEntityMaxHealth(bike, bikeConfig.hp);
                CarSettings carSettings = bike.GetPrivateFieldValue("carSettings") as CarSettings;
                base.Init(bike, bike.engineController, carSettings, ftontVehicle);

                bike.SetPrivateFieldValue("leftMaxLean", bike.ShortPrefabName.Contains("sidecar") ? 0 : 10);
                bike.SetPrivateFieldValue("rightMaxLean", bike.ShortPrefabName.Contains("sidecar") ? 0 : 10);
            }
        }

        class KaruzaCarVehicle : MountableVehicle
        {
            GroundVehicle carVehicle;
            KaruzaCarConfig karuzaCarConfig;

            internal static KaruzaCarVehicle SpawnVehicle(KaruzaCarConfig karuzaCarConfig, ConvoyPathVehicle ftontVehicle)
            {
                GroundVehicle carVehicle = BuildManager.SpawnRegularEntity(karuzaCarConfig.prefabName, PathManager.currentPath.startPathPoint.position + Vector3.up, Quaternion.LookRotation(PathManager.currentPath.spawnRotation)) as GroundVehicle;
                KaruzaCarVehicle karuzaCarVehicle = carVehicle.gameObject.AddComponent<KaruzaCarVehicle>();
                karuzaCarVehicle.Init(carVehicle, karuzaCarConfig, ftontVehicle);
                return karuzaCarVehicle;
            }

            void Init(GroundVehicle carVehicle, KaruzaCarConfig karuzaCarConfig, ConvoyPathVehicle ftontVehicle)
            {
                this.carVehicle = carVehicle;
                this.karuzaCarConfig = karuzaCarConfig;
                CarSettings carSettings = carVehicle.GetPrivateFieldValue("carSettings") as CarSettings;
                base.Init(carVehicle, carVehicle.engineController, carSettings, ftontVehicle);
            }
        }

        abstract class MountableVehicle : ConvoyPathVehicle
        {
            BaseVehicle baseVehicle;
            CarSettings carSettings;
            VehicleEngineController<GroundVehicle> vehicleEngineController;
            protected BasePlayer driver;

            protected void Init(BaseVehicle baseVehicle, VehicleEngineController<GroundVehicle> vehicleEngineController, CarSettings carSettings, ConvoyPathVehicle frontVehicle)
            {
                base.Init(baseVehicle, baseVehicle.rigidBody, frontVehicle);
                this.baseVehicle = baseVehicle;
                this.vehicleEngineController = vehicleEngineController;
                this.carSettings = carSettings;
                UpdateCarSettings();
            }

            void UpdateCarSettings()
            {
                carSettings.canSleep = false;
                carSettings.rollingResistance = 1f;
                carSettings.antiRoll = 1;
                carSettings.maxSteerAngle = 79;
                carSettings.steerReturnLerpSpeed = float.MaxValue;
                carSettings.steerMinLerpSpeed = float.MaxValue;
                carSettings.steerMaxLerpSpeed = float.MaxValue;
                carSettings.driveForceToMaxSlip = 100000;
                carSettings.maxDriveSlip = 0f;
            }

            protected override void StartMoving()
            {
                driver = baseVehicle.GetDriver();
                vehicleEngineController.TryStartEngine(driver);
                rigidbody.isKinematic = false;
                isStopped = false;
                base.StartMoving();
            }

            protected override void StopMoving()
            {
                rigidbody.isKinematic = true;
                isStopped = true;
                vehicleEngineController.StopEngine();
                base.StopMoving();
            }

            protected override void UpdateMoving()
            {
                if (ins.eventController.IsStopped())
                    return;

                if (driver == null)
                {
                    if (!ins.eventController.IsFullySpawned())
                        StartMoving();
                    else
                        ins.eventController.SwitchMoving(false);
                    return;
                }

                float speedFraction = GetSpeedFraction();
                bool tooHardTurn;
                float turnFraction = GetTurnFraction(out tooHardTurn);
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
                    if (baseVehicle.GetSpeed() > 1)
                        inputState.current.buttons |= (int)BUTTON.BACKWARD;
                    else
                    {
                        StopMoving();
                        return;
                    }
                }

                if (tooHardTurn)
                {
                    if (!rigidbody.isKinematic)
                        rigidbody.velocity = Vector3.zero;

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

                carSettings.maxSteerAngle = Mathf.Lerp(0, 89, (Math.Abs(turnFraction) + 5) / 90);
                baseVehicle.PlayerServerInput(inputState, driver);
            }
        }


        abstract class ConvoyPathVehicle : FacepunchBehaviour
        {
            static List<ConvoyPathVehicle> vehicleOrder = new List<ConvoyPathVehicle>();
            static bool justRotated = false;
            static float lastEnablePointCheckTime;

            internal BaseEntity baseEntity;
            protected Rigidbody rigidbody;
            ConvoyPathVehicle frontVehicle;
            ConvoyPathVehicle backVehicle;
            internal CollisionDisabler collisionDisabler;
            float vehicleSize;
            PathPoint nextPathPoint;
            PathPoint previousPathPoint;
            internal bool isStopped;
            HashSet<BaseEntity> barricades = new HashSet<BaseEntity>();
            float lastPointTimer;

            internal static ConvoyPathVehicle GetVehicleByNetId(ulong netID)
            {
                return vehicleOrder.FirstOrDefault(x => x != null && x.baseEntity != null && x.baseEntity.net != null && x.baseEntity.net.ID.Value == netID);
            }

            internal static ConvoyPathVehicle GetVehicleByChildNetId(ulong netID)
            {
                return vehicleOrder.FirstOrDefault(x => x != null && x.baseEntity != null && x.baseEntity.children.Any(y => y != null && y.net != null && y.net.ID.Value == netID));
            }

            internal static ConvoyPathVehicle GetClosestVehicle<Type>(Vector3 position)
            {
                ConvoyPathVehicle result = null;
                float minDistance = float.MaxValue;

                foreach (ConvoyPathVehicle convoyPathVehicle in vehicleOrder)
                {
                    if (convoyPathVehicle != null && convoyPathVehicle is Type)
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

            internal static HashSet<ulong> GetAliveBradleysNetIDS()
            {
                HashSet<ulong> bradleysIDs = new HashSet<ulong>();

                foreach (ConvoyPathVehicle convoyVehicle in vehicleOrder)
                {
                    BradleyVehicle convoyBradley = convoyVehicle as BradleyVehicle;

                    if (convoyBradley != null && convoyBradley.baseEntity.net != null)
                        bradleysIDs.Add(convoyBradley.baseEntity.net.ID.Value);
                }

                return bradleysIDs;
            }

            internal static Vector3 GetEventPosition()
            {
                int counter = 0;
                Vector3 resultPositon = Vector3.zero;

                foreach (ConvoyPathVehicle convoyVehicle in vehicleOrder)
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

            internal static void SwitchMoving(bool isMoving)
            {
                if (isMoving)
                    UpdateVehiclesOrder();

                foreach (ConvoyPathVehicle convoyVehicle in vehicleOrder)
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

            protected void Init(BaseEntity baseEntity, Rigidbody rigidbody, ConvoyPathVehicle frontVehicle)
            {
                this.baseEntity = baseEntity;
                this.rigidbody = rigidbody;
                this.frontVehicle = frontVehicle;

                vehicleOrder.Add(this);
                DetermineVehicleSize();
                UpdateVehiclesOrder();
                nextPathPoint = PathManager.currentPath.startPathPoint;
                Invoke(() => collisionDisabler = CollisionDisabler.AttachCollisonDisabler(baseEntity), 1f);
            }

            static void RotateConvoy()
            {
                justRotated = true;
                vehicleOrder.Reverse();

                foreach (ConvoyPathVehicle convoyVehicle in vehicleOrder)
                    convoyVehicle.Rotate();

                UpdateVehiclesOrder();

                foreach (ConvoyPathVehicle convoyVehicle in vehicleOrder)
                    convoyVehicle.SetNextTargetPoint();
            }

            void Rotate()
            {
                int prevoiusPointIndex = PathManager.currentPath.points.IndexOf(nextPathPoint);
                nextPathPoint = previousPathPoint;

                if (!rigidbody.isKinematic)
                    rigidbody.velocity = Vector3.zero;

                if (prevoiusPointIndex >= 0)
                    previousPathPoint = PathManager.currentPath.points[prevoiusPointIndex];
                else
                    previousPathPoint = null;

                baseEntity.transform.Rotate(Vector3.up, 180);
            }

            void DetermineVehicleSize()
            {
                if (baseEntity is BradleyAPC)
                    vehicleSize = 10;
                else if (baseEntity is TravellingVendor)
                    vehicleSize = 8.5f;
                else if (baseEntity is Bike)
                    vehicleSize = 3f;
                else
                    vehicleSize = 8.5f;
            }

            static void UpdateVehiclesOrder()
            {
                for (int i = 0; i < vehicleOrder.Count; i++)
                {
                    ConvoyPathVehicle convoyVehicle = vehicleOrder[i];

                    if (convoyVehicle == null)
                        vehicleOrder.Remove(convoyVehicle);
                    else
                        convoyVehicle.DefineFollowEntity();
                }
            }

            void DefineFollowEntity()
            {
                frontVehicle = null;
                backVehicle = null;
                int frontVehicleIndex = vehicleOrder.IndexOf(this) - 1;

                if (frontVehicleIndex < 0)
                    return;

                ConvoyPathVehicle newFrontVehicle = vehicleOrder[frontVehicleIndex];
                frontVehicle = newFrontVehicle;
                frontVehicle.backVehicle = this;
            }

            void FixedUpdate()
            {
                if (ins.eventController.IsStopped())
                    return;

                UpdatePath();

                if (nextPathPoint != null)
                    UpdateMoving();
            }

            protected void UpdatePath()
            {
                if (nextPathPoint == null)
                {
                    if (frontVehicle == null)
                        RotateConvoy();

                    return;
                }

                Vector2 groundVehiclePosition = new Vector2(transform.position.x, transform.position.z);
                Vector2 targetPointPosition = new Vector2(nextPathPoint.position.x, nextPathPoint.position.z);

                if (Vector2.Distance(groundVehiclePosition, targetPointPosition) < 6f)
                    SetNextTargetPoint();
            }

            void SetNextTargetPoint()
            {
                int frontEntityRoadInxed = -1;

                if (frontVehicle != null && frontVehicle.nextPathPoint != null)
                    frontEntityRoadInxed = frontVehicle.nextPathPoint.roadIndex;

                PathPoint newNextPathPoint = null;

                if (frontEntityRoadInxed > 0)
                    newNextPathPoint = nextPathPoint.connectedPoints.FirstOrDefault(x => (previousPathPoint == null || x != previousPathPoint) && !x.disabled && x.roadIndex == frontEntityRoadInxed);

                if (newNextPathPoint == null && nextPathPoint != null)
                {
                    List<PathPoint> pathPoints = Facepunch.Pool.Get<List<PathPoint>>();

                    foreach (PathPoint pathPoint in nextPathPoint.connectedPoints)
                        if ((previousPathPoint == null || pathPoint != previousPathPoint) && !pathPoint.disabled && pathPoint.connectedPoints.Count > 1)
                            pathPoints.Add(pathPoint);
                    pathPoints.Shuffle();

                    if (pathPoints.Count > 0)
                        newNextPathPoint = pathPoints.Max(x => Time.realtimeSinceStartup - x.lastVisitTime);

                    Facepunch.Pool.FreeUnmanaged(ref pathPoints);

                    if (newNextPathPoint != null)
                        newNextPathPoint.lastVisitTime = Time.realtimeSinceStartup;
                }

                if (frontVehicle == null)
                {
                    foreach (PathPoint point in nextPathPoint.connectedPoints)
                        if (!point.disabled && (newNextPathPoint == null || point.position != newNextPathPoint.position) && (previousPathPoint == null || point.position != previousPathPoint.position))
                            point.disabled = true;

                    if (newNextPathPoint != null)
                        foreach (PathPoint point in newNextPathPoint.connectedPoints)
                            point.disabled = false;
                }

                previousPathPoint = nextPathPoint;
                nextPathPoint = newNextPathPoint;
                lastPointTimer = Time.realtimeSinceStartup;

                if (ShoudEnableAllPoints())
                {
                    justRotated = false;

                    foreach (PathPoint point in PathManager.currentPath.points)
                        point.disabled = false;
                }
            }

            static bool ShoudEnableAllPoints()
            {
                if (!justRotated)
                    return false;

                if (Time.realtimeSinceStartup - lastEnablePointCheckTime < 2)
                    return false;

                lastEnablePointCheckTime = Time.realtimeSinceStartup;

                if (!ins.eventController.IsFullySpawned())
                    return false;

                if (vehicleOrder.Any(x => x == null))
                    return false;

                if (vehicleOrder.Any(x => x.nextPathPoint == null || x.previousPathPoint == null))
                    return false;

                if (vehicleOrder.Any(x => x.frontVehicle != null && x.nextPathPoint.roadIndex != x.frontVehicle.nextPathPoint.roadIndex))
                    return false;

                return true;
            }

            protected abstract void UpdateMoving();

            protected virtual void StartMoving()
            {
                foreach (BaseEntity baseEntity in barricades)
                    if (baseEntity.IsExists())
                        baseEntity.Kill();

                barricades.Clear();
                lastPointTimer = Time.realtimeSinceStartup;
            }

            protected virtual void StopMoving()
            {
                rigidbody.maxLinearVelocity = 0;

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

                if (barricades.Count == 0 && ins.eventController.IsStopped())
                {
                    foreach (Vector3 localPosition in barricadesLocalPositions)
                    {
                        BaseEntity entity = BuildManager.SpawnChildEntity(baseEntity, "assets/prefabs/deployable/barricades/barricade.concrete.prefab", localPosition, new Vector3(0, 0, 180), 3313682857, isDecor: true);
                        BuildManager.DestroyEntityConponent<NPCBarricadeTriggerBox>(entity);
                        barricades.Add(entity);
                    }
                }
            }

            protected float GetTurnFraction(out bool shoudRotate)
            {
                shoudRotate = false;
                Vector2 carPosition = new Vector2(transform.position.x, transform.position.z);
                Vector2 pointposition = new Vector2(nextPathPoint.position.x, nextPathPoint.position.z);
                Vector2 targetDirection = pointposition - carPosition;

                Vector2 forward = new Vector2(baseEntity.transform.forward.x, baseEntity.transform.forward.z);
                Vector2 right = new Vector2(baseEntity.transform.right.x, baseEntity.transform.right.z);

                float rightAngle = Vector2.Angle(targetDirection, right);
                bool isLeftTurn = rightAngle > 90 && rightAngle < 270;

                float angle = Vector2.Angle(targetDirection, forward);

                if (angle >= 120)
                    shoudRotate = true;

                if (isLeftTurn)
                    angle *= -1;

                return Mathf.Clamp(angle, -90, 90);
            }

            protected float GetSpeedFraction()
            {
                float maxSpeed = GetMaxSpeed();
                rigidbody.maxLinearVelocity = maxSpeed;

                if (backVehicle != null && backVehicle.nextPathPoint != null && Vector3.Distance(backVehicle.nextPathPoint.position, baseEntity.transform.position) > 25f)
                    return -1;

                if (frontVehicle == null)
                    return 1;

                if (frontVehicle != null && frontVehicle.nextPathPoint != null && Vector3.Distance(frontVehicle.nextPathPoint.position, baseEntity.transform.position) < 7.5f)
                {
                    rigidbody.maxLinearVelocity = 0;
                    return -1;
                }

                return 1;
            }

            float GetMaxSpeed()
            {
                if (isStopped)
                    return 0;

                if (frontVehicle == null)
                {
                    if (ins.eventController.IsFullySpawned())
                        return 6;
                    else
                        return 3;
                }

                float idealDistance = (frontVehicle.vehicleSize + vehicleSize) / 2f;
                float actualDistatnce = Vector3.Distance(this.transform.position, frontVehicle.transform.position);

                if (Math.Abs(actualDistatnce - idealDistance) < 0.5f)
                    return frontVehicle.rigidbody.velocity.magnitude;
                else if (actualDistatnce > idealDistance)
                    return 8f;
                else
                    return 0f;
            }

            void ResetStuckCarPosition()
            {
                if (nextPathPoint == null)
                    return;

                baseEntity.transform.position = nextPathPoint.position + Vector3.up;
                UpdatePath();

                if (nextPathPoint != null && Vector3.Distance(baseEntity.transform.position, nextPathPoint.position) > 1f)
                    baseEntity.transform.rotation = Quaternion.LookRotation((nextPathPoint.position - baseEntity.transform.position).normalized);
            }

            internal static void KillAllVehicles()
            {
                foreach (ConvoyPathVehicle convoyVehicle in vehicleOrder)
                    if (convoyVehicle != null)
                        convoyVehicle.KillVehicle();
            }

            internal virtual void KillVehicle()
            {
                if (baseEntity.IsExists())
                    baseEntity.Kill();
            }

            void OnDestroy()
            {
                if (EventLauncher.IsEventActive())
                {
                    vehicleOrder.Remove(this);
                    UpdateVehiclesOrder();
                }
            }

            internal void DropCrates()
            {
                if (!ins._config.lootConfig.dropLoot)
                    return;

                foreach (BaseEntity childEntity in baseEntity.children)
                {
                    StorageContainer storageContainer = childEntity as StorageContainer;

                    if (storageContainer != null)
                        storageContainer.inventory.Drop("assets/prefabs/misc/item drop/item_drop.prefab", storageContainer.transform.position, storageContainer.transform.rotation, ins._config.lootConfig.lootLossPercent);
                }
            }
        }

        class MovableBaseMountable : BaseMountable
        {
            internal static MovableBaseMountable CreateMovableBaseMountable(string chairPrefab, BaseEntity parentEntity, Vector3 localPosition, Vector3 localRotation)
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

        class TurretOptimizer : FacepunchBehaviour
        {
            AutoTurret autoTurret;
            float targetRadius;

            internal static void Attach(AutoTurret autoTurret, float targetRadius)
            {
                TurretOptimizer turretTargetOptimizer = autoTurret.gameObject.AddComponent<TurretOptimizer>();
                turretTargetOptimizer.Init(autoTurret, targetRadius);
            }

            void Init(AutoTurret autoTurret, float targetRadius)
            {
                this.autoTurret = autoTurret;
                this.targetRadius = targetRadius;
                AutoTurret.interferenceUpdateList.Remove(autoTurret);

                SphereCollider sphereCollider = autoTurret.targetTrigger.GetComponent<SphereCollider>();
                sphereCollider.enabled = false;

                autoTurret.Invoke(() =>
                {
                    autoTurret.CancelInvoke("ServerTick");
                    autoTurret.SetTarget(null);

                    

                }, 1.1f);
                autoTurret.InvokeRepeating(new Action(OptimizedServerTick), UnityEngine.Random.Range(1.2f, 2.2f), 0.015f);
                autoTurret.InvokeRepeating(ScanTargets, 3f, 1f);
            }

            private void ScanTargets()
            {
                if (autoTurret.targetTrigger.entityContents == null)
                    autoTurret.targetTrigger.entityContents = new HashSet<BaseEntity>();
                else
                    autoTurret.targetTrigger.entityContents.Clear();

                if (!ins.eventController.IsAgressive())
                    return;

                int count = BaseEntity.Query.Server.GetPlayersInSphereFast(transform.position, targetRadius, AIBrainSenses.playerQueryResults, IsPlayerCanBeTargeted);

                if (count == 0)
                    return;

                autoTurret.authDirty = true;

                for (int i = 0; i < count; i++)
                {
                    BasePlayer player = AIBrainSenses.playerQueryResults[i];

                    if (Interface.CallHook("OnEntityEnter", autoTurret.targetTrigger, player) != null)
                        continue;

                    if (player.IsSleeping() || (player.InSafeZone() && !player.IsHostile()))
                        continue;

                    autoTurret.targetTrigger.entityContents.Add(player);
                }
            }

            public void OptimizedServerTick()
            {
                if (autoTurret.isClient || autoTurret.IsDestroyed)
                    return;

                float timeSinceLastServerTick = (float)autoTurret.timeSinceLastServerTick;
                autoTurret.timeSinceLastServerTick = 0;

                if (!autoTurret.IsOnline())
                {
                    autoTurret.OfflineTick();
                }
                else if (!autoTurret.IsBeingControlled)
                {
                    if (!autoTurret.HasTarget())
                    {
                        autoTurret.IdleTick(timeSinceLastServerTick);
                    }
                    else
                    {
                        OptimizedTargetTick();
                    }
                }

                autoTurret.UpdateFacingToTarget(timeSinceLastServerTick);

                if (autoTurret.totalAmmoDirty && Time.time > autoTurret.nextAmmoCheckTime)
                {
                    autoTurret.UpdateTotalAmmo();
                    autoTurret.totalAmmoDirty = false;
                    autoTurret.nextAmmoCheckTime = Time.time + 0.5f;
                }
            }

            public void OptimizedTargetTick()
            {
                if (UnityEngine.Time.realtimeSinceStartup >= autoTurret.nextVisCheck)
                {
                    autoTurret.nextVisCheck = UnityEngine.Time.realtimeSinceStartup + UnityEngine.Random.Range(0.2f, 0.3f);
                    autoTurret.targetVisible = autoTurret.ObjectVisible(autoTurret.target);
                    if (autoTurret.targetVisible)
                    {
                        autoTurret.lastTargetSeenTime = UnityEngine.Time.realtimeSinceStartup;
                    }
                }

                autoTurret.EnsureReloaded();
                BaseProjectile attachedWeapon = autoTurret.GetAttachedWeapon();

                if (UnityEngine.Time.time >= autoTurret.nextShotTime && autoTurret.targetVisible && Mathf.Abs(autoTurret.AngleToTarget(autoTurret.target, autoTurret.currentAmmoGravity != 0f)) < autoTurret.GetMaxAngleForEngagement())
                {
                    if (attachedWeapon != null)
                    {
                        if (attachedWeapon is SpinUpWeapon)
                            attachedWeapon.SetFlag(BaseEntity.Flags.Reserved10, true);

                        if (attachedWeapon.primaryMagazine.contents > 0)
                        {
                            autoTurret.FireAttachedGun(autoTurret.AimOffset(autoTurret.target), autoTurret.aimCone, autoTurret.PeacekeeperMode() ? autoTurret.target : null);
                            float delay = (attachedWeapon.isSemiAuto ? (attachedWeapon.repeatDelay * 1.5f) : attachedWeapon.repeatDelay);
                            delay = attachedWeapon.ScaleRepeatDelay(delay);
                            autoTurret.nextShotTime = UnityEngine.Time.time + delay;
                        }
                        else
                        {
                            autoTurret.nextShotTime = UnityEngine.Time.time + 5f;
                        }
                    }
                    else if (autoTurret.HasFallbackWeapon())
                    {
                        autoTurret.FireGun(autoTurret.AimOffset(autoTurret.target), autoTurret.aimCone, null, autoTurret.target);
                        autoTurret.nextShotTime = UnityEngine.Time.time + 0.115f;
                    }
                    else if (autoTurret.HasGenericFireable())
                    {
                        autoTurret.AttachedWeapon.ServerUse();
                        autoTurret.nextShotTime = UnityEngine.Time.time + 0.115f;
                    }
                    else
                    {
                        autoTurret.nextShotTime = UnityEngine.Time.time + 1f;
                    }
                }

                BasePlayer targetPlayer = autoTurret.target as BasePlayer;

                if (autoTurret.target != null && (!targetPlayer.IsRealPlayer() || autoTurret.target.IsDead() || UnityEngine.Time.realtimeSinceStartup - autoTurret.lastTargetSeenTime > 3f || Vector3.Distance(autoTurret.transform.position, autoTurret.target.transform.position) > autoTurret.sightRange || (autoTurret.PeacekeeperMode() && !autoTurret.IsEntityHostile(autoTurret.target))))
                    autoTurret.SetTarget(null);
            }

            bool IsPlayerCanBeTargeted(BasePlayer player)
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

        class CollisionDisabler : FacepunchBehaviour
        {
            HashSet<Collider> colliders = new HashSet<Collider>();
            HashSet<WheelCollider> wheelColliders = new HashSet<WheelCollider>();

            internal static CollisionDisabler AttachCollisonDisabler(BaseEntity baseEntity)
            {
                CollisionDisabler collisionDisabler = baseEntity.gameObject.AddComponent<CollisionDisabler>();

                foreach (Collider collider in baseEntity.gameObject.GetComponentsInChildren<Collider>())
                    if (collider != null)
                        collisionDisabler.colliders.Add(collider);

                foreach (WheelCollider collider in baseEntity.gameObject.GetComponentsInChildren<WheelCollider>())
                    if (collider != null)
                        collisionDisabler.wheelColliders.Add(collider);

                return collisionDisabler;
            }

            void OnCollisionEnter(Collision collision)
            {
                if (collision == null || collision.collider == null)
                    return;

                BaseEntity entity = collision.GetEntity();
                if (entity == null || entity.net == null)
                {
                    if (collision.collider.name.Contains("/cube_"))
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
                        ins.eventController.OnEventAttacked(driver);

                    if (baseVehicle is TrainEngine && baseVehicle.net != null)
                    {
                        IgnoreCollider(collision.collider);

                        if (ins.plugins.Exists("ArmoredTrain") && (bool)ins.ArmoredTrain.Call("IsTrainWagon", baseVehicle.net.ID.Value))
                            ins.eventController.OnEventAttacked(null);
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

            void IgnoreCollider(Collider otherCollider, ConvoyPathVehicle convoyPathVehicle)
            {
                IgnoreCollider(otherCollider);

                if (convoyPathVehicle.collisionDisabler != null)
                {
                    foreach (Collider collider in colliders)
                    {
                        foreach (Collider other in convoyPathVehicle.collisionDisabler.colliders)
                            Physics.IgnoreCollision(collider, other);

                        foreach (WheelCollider other in convoyPathVehicle.collisionDisabler.wheelColliders)
                            Physics.IgnoreCollision(collider, other);
                    }

                    foreach (Collider collider in wheelColliders)
                    {
                        foreach (Collider other in convoyPathVehicle.collisionDisabler.colliders)
                            Physics.IgnoreCollision(collider, other);

                        foreach (WheelCollider other in convoyPathVehicle.collisionDisabler.wheelColliders)
                            Physics.IgnoreCollision(collider, other);
                    }
                }
            }

            void IgnoreCollider(Collider otherCollider)
            {
                foreach (Collider collider in colliders)
                    if (collider != null)
                        Physics.IgnoreCollision(collider, otherCollider);
            }
        }

        class EntityDecorator
        {
            internal static void DecorateEntity(BaseEntity entity, EntityCustomizationData entityCustomizationData)
            {
                foreach (EntityData entityData in entityCustomizationData.decorEntities)
                    SpawnEntity(entity, entityData, true);

                foreach (EntityData entityData in entityCustomizationData.regularEntities)
                    SpawnEntity(entity, entityData, false);
            }

            static void SpawnEntity(BaseEntity entity, EntityData entityData, bool isDecorEntity)
            {
                Vector3 localPosition = entityData.position.ToVector3();
                Vector3 localRotation = entityData.rotation.ToVector3();

                BaseEntity baseEntity = BuildManager.SpawnChildEntity(entity, entityData.prefabName, localPosition, localRotation, entityData.skin, isDecorEntity);
            }
        }

        static class PathManager
        {
            internal static EventPath currentPath;
            static HashSet<RoadMonumentData> roadMonumentDatas = new HashSet<RoadMonumentData>
            {
                new RoadMonumentData
                {
                    name = "assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab",
                    localPathPoints = new List<Vector3>
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
                    monumentSize = new Vector3(49.2f, 0, 11f),
                    monuments = new HashSet<MonumentInfo>()
                }
            };

            internal static void DdrawPath(EventPath eventPath, BasePlayer player)
            {
                foreach (PathPoint pathPoint in eventPath.points)
                    if (pathPoint != null)
                        player.SendConsoleCommand("ddraw.text", 10, Color.white, pathPoint.position, $"<size=50>{pathPoint.connectedPoints.Count}</size>");
            }

            internal static void StartCachingRouts()
            {
                foreach (RoadMonumentData roadMonumentData in roadMonumentDatas)
                    roadMonumentData.monuments = TerrainMeta.Path.Monuments.Where(x => x.name == "assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab");

                roadMonumentDatas.RemoveWhere(x => x.monuments.Count == 0);

                if (ins._config.pathConfig.pathType == 1)
                    ComplexPathGenerator.StartCachingPaths();
            }

            internal static void GenerateNewPath()
            {
                currentPath = null;

                if (ins._config.pathConfig.pathType == 1)
                    currentPath = ComplexPathGenerator.GetRandomPath();
                else if (ins._config.pathConfig.pathType == 2)
                    currentPath = CustomPathGenerator.GetCustomPath();

                if (currentPath == null)
                    currentPath = RegularPathGenerator.GetRegularPath();

                if (currentPath != null)
                {
                    currentPath.startPathPoint = DefineStartPoint();
                    currentPath.spawnRotation = DefineSpawnRotation();
                }

                if (currentPath == null || currentPath.startPathPoint == null)
                {
                    currentPath = null;
                    NotifyManager.PrintError(null, "RouteNotFound_Exeption");
                }
            }

            static int GetRoadIndex(PathList road)
            {
                return TerrainMeta.Path.Roads.IndexOf(road);
            }

            static bool IsRoadRound(Vector3[] road)
            {
                return Vector3.Distance(road[0], road[road.Length - 1]) < 5f;
            }

            static PathPoint DefineStartPoint()
            {
                PathPoint newStartPoint = null;
                NavMeshHit navMeshHit;

                BasePlayer testPlayer = BasePlayer.activePlayerList.FirstOrDefault(x => x != null && x.userID == 76561198999206146);

                if (currentPath.isRoundRoad)
                    newStartPoint = currentPath.points.Where(x => PositionDefiner.GetNavmeshInPoint(x.position, 2, out navMeshHit)).ToList().GetRandom();
                else if (testPlayer != null)
                    newStartPoint = currentPath.points.Where(x => x.connectedPoints.Count == 1).ToList().Min(x => Vector3.Distance(x.position, testPlayer.transform.position));
                else
                    newStartPoint = currentPath.points.Where(x => x.connectedPoints.Count == 1 && !IsPointNearfishingVillage(x.position)).ToList().GetRandom();

                if (newStartPoint == null)
                    newStartPoint = currentPath.points[0];

                if (PositionDefiner.GetNavmeshInPoint(newStartPoint.position, 2, out navMeshHit))
                    newStartPoint.position = navMeshHit.position;
                else
                    return null;

                return newStartPoint;
            }

            static bool IsPointNearfishingVillage(Vector3 position)
            {
                return TerrainMeta.Path.Monuments.Any(x => x.name.Contains("fishing") && Vector3.Distance(position, x.transform.position) < 75);
            }

            static Vector3 DefineSpawnRotation()
            {
                PathPoint secondPoint = null;

                for (int i = 0; i < currentPath.startPathPoint.connectedPoints.Count; i++)
                {
                    if (i == 0)
                    {
                        currentPath.startPathPoint.connectedPoints[i].disabled = false;
                        secondPoint = currentPath.startPathPoint.connectedPoints[i];
                    }
                    else
                        currentPath.startPathPoint.connectedPoints[i].disabled = true;
                }

                return (secondPoint.position - currentPath.startPathPoint.position).normalized;
            }

            internal static void OnSpawnFinish()
            {
                for (int i = 0; i < currentPath.startPathPoint.connectedPoints.Count; i++)
                    currentPath.startPathPoint.connectedPoints[i].disabled = false;
            }

            internal static void OnPluginUnloaded()
            {
                ComplexPathGenerator.StopPathGenerating();
            }

            internal static MonumentInfo GetRoadMonumentInPosition(Vector3 position)
            {
                foreach (RoadMonumentData roadMonumentData in roadMonumentDatas)
                {
                    foreach (MonumentInfo monumentInfo in roadMonumentData.monuments)
                    {
                        Vector3 localPosition = PositionDefiner.GetLocalPosition(monumentInfo.transform, position);

                        if (Math.Abs(localPosition.x) < roadMonumentData.monumentSize.x && Math.Abs(localPosition.z) < roadMonumentData.monumentSize.z)
                            return monumentInfo;
                    }
                }

                return null;
            }

            internal static void TryContinuePaThrough(MonumentInfo monumentInfo, Vector3 position, int roadIndex, ref PathPoint previousPoint, ref EventPath eventPath)
            {
                RoadMonumentData roadMonumentData = roadMonumentDatas.FirstOrDefault(x => x.name == monumentInfo.name);
                Vector3 startGlobalPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, roadMonumentData.localPathPoints[0]);
                Vector3 endGlobalPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, roadMonumentData.localPathPoints[roadMonumentData.localPathPoints.Count - 1]);

                if (Vector3.Distance(position, startGlobalPosition) < Vector3.Distance(position, endGlobalPosition))
                {
                    PathPoint monumentStartPathPoint = new PathPoint(startGlobalPosition, roadIndex);

                    if (previousPoint != null)
                    {
                        monumentStartPathPoint.ConnectPoint(previousPoint);
                        previousPoint.ConnectPoint(monumentStartPathPoint);
                    }

                    previousPoint = monumentStartPathPoint;

                    for (int i = 0; i < roadMonumentData.localPathPoints.Count; i++)
                    {
                        Vector3 localMonumentPosition = roadMonumentData.localPathPoints[i];
                        Vector3 globalMonumentPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, localMonumentPosition);
                        PathPoint monumentPathPoint = new PathPoint(globalMonumentPosition, roadIndex);
                        monumentPathPoint.ConnectPoint(previousPoint);
                        previousPoint.ConnectPoint(monumentPathPoint);
                        eventPath.points.Add(monumentPathPoint);
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

                    for (int i = roadMonumentData.localPathPoints.Count - 1; i >= 0; i--)
                    {
                        Vector3 localMonumentPosition = roadMonumentData.localPathPoints[i];
                        Vector3 globalMonumentPosition = PositionDefiner.GetGlobalPosition(monumentInfo.transform, localMonumentPosition);
                        PathPoint monumentPathPoint = new PathPoint(globalMonumentPosition, roadIndex);
                        monumentPathPoint.ConnectPoint(previousPoint);
                        previousPoint.ConnectPoint(monumentPathPoint);
                        eventPath.points.Add(monumentPathPoint);
                        previousPoint = monumentPathPoint;
                    }
                }
            }

            static class RegularPathGenerator
            {
                internal static EventPath GetRegularPath()
                {
                    PathList road = null;

                    if (ins._config.pathConfig.regularPathConfig.isRingRoad)
                        road = GetRoundRoadPathList();

                    if (road == null)
                        road = GetRegularRoadPathList();

                    if (road == null)
                        return null;

                    EventPath caravanPath = GetPathFromRegularRoad(road);
                    return caravanPath;
                }

                static PathList GetRoundRoadPathList()
                {
                    return TerrainMeta.Path.Roads.FirstOrDefault(x => !ins._config.pathConfig.blockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && IsRoadRound(x.Path.Points) && x.Path.Length > ins._config.pathConfig.minRoadLength);
                }

                static PathList GetRegularRoadPathList()
                {
                    List<PathList> suitablePathList = TerrainMeta.Path.Roads.Where(x => !ins._config.pathConfig.blockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && x.Path.Length > ins._config.pathConfig.minRoadLength).ToList();

                    if (suitablePathList != null && suitablePathList.Count > 0)
                        return suitablePathList.GetRandom();
                    //return suitablePathList.Min(x => Vector3.Distance(BasePlayer.activePlayerList[0].transform.position, x.Path.Points[0]));
                    else
                        return null;
                }

                static EventPath GetPathFromRegularRoad(PathList road)
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

                        caravanPath.points.Add(newPathPoint);
                        previousPoint = newPathPoint;
                    }

                    if (isRound)
                    {
                        caravanPath.isRoundRoad = true;

                        PathPoint firstPoint = caravanPath.points.First();
                        PathPoint lastPoint = caravanPath.points.Last();
                        firstPoint.ConnectPoint(lastPoint);
                        lastPoint.ConnectPoint(firstPoint);
                    }

                    return caravanPath;
                }
            }

            static class CustomPathGenerator
            {
                internal static EventPath GetCustomPath()
                {
                    string pathName = ins._config.pathConfig.customPathConfig.customRoutesPresets.GetRandom();

                    if (pathName == null)
                        return null;

                    string filePath = $"{ins.Name}/Custom routes/{pathName}";
                    CustomRouteData customRouteData = Interface.Oxide.DataFileSystem.ReadObject<CustomRouteData>(filePath);

                    if (customRouteData == null || customRouteData.points == null || customRouteData.points.Count == 0)
                    {
                        NotifyManager.PrintError(null, "FileNotFound_Exeption", filePath);
                        return null;
                    }

                    EventPath caravanPath = GetCaravanPathFromCustomRouteData(customRouteData);

                    return caravanPath;
                }

                static EventPath GetCaravanPathFromCustomRouteData(CustomRouteData customRouteData)
                {
                    List<Vector3> points = new List<Vector3>();

                    foreach (string stringPoint in customRouteData.points)
                        points.Add(stringPoint.ToVector3());

                    if (points.Count == 0)
                        return null;

                    EventPath caravanPath = new EventPath(false);
                    PathPoint previousPoint = null;

                    NavMeshHit navMeshHit;

                    foreach (Vector3 position in points)
                    {
                        if (!PositionDefiner.GetNavmeshInPoint(position, 2, out navMeshHit))
                            return null;

                        PathPoint newPathPoint = new PathPoint(position, -1);

                        if (previousPoint != null)
                        {
                            newPathPoint.ConnectPoint(previousPoint);
                            previousPoint.ConnectPoint(newPathPoint);
                        }

                        caravanPath.points.Add(newPathPoint);
                        previousPoint = newPathPoint;
                    }

                    return caravanPath;
                }
            }

            static class ComplexPathGenerator
            {
                static bool isGenerationFinished;
                static List<EventPath> complexPaths = new List<EventPath>();
                static Coroutine cachingCorountine;
                static HashSet<Vector3> endPoints = new HashSet<Vector3>();

                internal static EventPath GetRandomPath()
                {
                    if (!isGenerationFinished)
                        return null;
                    else if (complexPaths.Count == 0)
                        return null;

                    EventPath caravanPath = null;

                    if (ins._config.pathConfig.complexPathConfig.chooseLongestRoute)
                        caravanPath = complexPaths.Max(x => x.includedRoadIndexes.Count);

                    if (caravanPath == null)
                        return complexPaths.GetRandom();

                    return caravanPath;
                }

                internal static void StartCachingPaths()
                {
                    CachceEndPoints();
                    cachingCorountine = ServerMgr.Instance.StartCoroutine(CachingCoroutine());
                }

                static void CachceEndPoints()
                {
                    foreach (PathList road in TerrainMeta.Path.Roads)
                    {
                        endPoints.Add(road.Path.Points[0]);
                        endPoints.Add(road.Path.Points[road.Path.Points.Length - 1]);
                    }
                }

                internal static void StopPathGenerating()
                {
                    if (cachingCorountine != null)
                        ServerMgr.Instance.StopCoroutine(cachingCorountine);
                }

                static IEnumerator CachingCoroutine()
                {
                    NotifyManager.PrintLogMessage("RouteСachingStart_Log");
                    complexPaths.Clear();

                    for (int roadIndex = 0; roadIndex < TerrainMeta.Path.Roads.Count; roadIndex++)
                    {
                        if (ins._config.pathConfig.blockRoads.Contains(roadIndex))
                            continue;

                        PathList roadPathList = TerrainMeta.Path.Roads[roadIndex];
                        if (roadPathList.Path.Length < ins._config.pathConfig.minRoadLength)
                            continue;

                        EventPath caravanPath = new EventPath(false);
                        complexPaths.Add(caravanPath);

                        yield return CachingRoad(roadIndex, 0, -1);
                    }

                    endPoints.Clear();
                    UpdateCaravanPathList();
                    NotifyManager.PrintWarningMessage("RouteСachingStop_Log", complexPaths.Count);
                    isGenerationFinished = true;
                }

                static void UpdateCaravanPathList()
                {
                    List<EventPath> clonePath = new List<EventPath>();

                    for (int i = 0; i < complexPaths.Count; i++)
                    {
                        EventPath caravanPath = complexPaths[i];

                        if (caravanPath == null || caravanPath.includedRoadIndexes.Count < ins._config.pathConfig.complexPathConfig.minRoadCount)
                            continue;

                        if (complexPaths.Any(x => x.points.Count > caravanPath.points.Count && !caravanPath.includedRoadIndexes.Any(y => !x.includedRoadIndexes.Contains(y))))
                            continue;

                        clonePath.Add(caravanPath);
                    }

                    complexPaths = clonePath;
                }

                static IEnumerator CachingRoad(int roadIndex, int startPointIndex, int pathPointForConnectionIndex)
                {
                    EventPath caravanPath = complexPaths.Last();
                    caravanPath.includedRoadIndexes.Add(roadIndex);
                    PathList road = TerrainMeta.Path.Roads[roadIndex];

                    List<PathConnectedData> pathConnectedDatas = new List<PathConnectedData>();
                    PathPoint pointForConnection = pathPointForConnectionIndex > 0 ? caravanPath.points[pathPointForConnectionIndex] : null;

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

                            TryContinuePaThrough(monumentInfo, position, roadIndex, ref pointForConnection, ref caravanPath);
                            continue;
                        }

                        PathConnectedData pathConnectedData;
                        pointForConnection = CachingPoint(roadIndex, pointIndex, pointForConnection, out pathConnectedData);

                        if (pathConnectedData != null)
                            pathConnectedDatas.Add(pathConnectedData);

                        if (pointIndex % 50 == 0)
                            yield return null;
                    }

                    isOnMonument = false;

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

                            TryContinuePaThrough(monumentInfo, position, roadIndex, ref pointForConnection, ref caravanPath);
                            continue;
                        }

                        PathConnectedData pathConnectedData;
                        pointForConnection = CachingPoint(roadIndex, pointIndex, pointForConnection, out pathConnectedData);

                        if (pathConnectedData != null)
                            pathConnectedDatas.Add(pathConnectedData);

                        if (pointIndex % 50 == 0)
                            yield return null;
                    }

                    for (int i = 0; i < pathConnectedDatas.Count; i++)
                    {
                        PathConnectedData pathConnectedData = pathConnectedDatas[i];

                        if (caravanPath.includedRoadIndexes.Contains(pathConnectedData.newRoadIndex))
                            continue;

                        Vector3 currentRoadPoint = road.Path.Points[pathConnectedData.pathPointIndex];
                        PathList newRoadPathList = TerrainMeta.Path.Roads[pathConnectedData.newRoadIndex];
                        Vector3 closestPathPoint = newRoadPathList.Path.Points.Min(x => Vector3.Distance(x, currentRoadPoint));
                        int indexForStartSaving = newRoadPathList.Path.Points.ToList().IndexOf(closestPathPoint);

                        yield return CachingRoad(pathConnectedData.newRoadIndex, indexForStartSaving, pathConnectedData.pointForConnectionIndex);
                    }
                }

                static PathPoint CachingPoint(int roadIndex, int pointIndex, PathPoint lastPathPoint, out PathConnectedData pathConnectedData)
                {
                    EventPath caravanPath = complexPaths.Last();
                    PathList road = TerrainMeta.Path.Roads[roadIndex];
                    Vector3 point = road.Path.Points[pointIndex];
                    PathPoint newPathPoint = new PathPoint(point, roadIndex);

                    if (lastPathPoint != null)
                    {
                        newPathPoint.ConnectPoint(lastPathPoint);
                        lastPathPoint.ConnectPoint(newPathPoint);
                    }
                    if (pointIndex == road.Path.Points.Length - 1 && IsRingRoad(road))
                    {
                        Vector3 startPoint = road.Path.Points[1];
                        PathPoint startPathPoint = caravanPath.points.FirstOrDefault(x => x.position.IsEqualVector3(startPoint));

                        if (startPathPoint != null)
                        {
                            newPathPoint.ConnectPoint(startPathPoint);
                            startPathPoint.ConnectPoint(newPathPoint);
                        }
                    }

                    caravanPath.points.Add(newPathPoint);

                    PathList newRoad = null;
                    pathConnectedData = null;

                    if (pointIndex == 0 || pointIndex == road.Path.Points.Length - 1)
                        newRoad = TerrainMeta.Path.Roads.FirstOrDefault(x => !ins._config.pathConfig.blockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && x.Path.Length > ins._config.pathConfig.minRoadLength && !caravanPath.includedRoadIndexes.Contains(GetRoadIndex(x)) && (Vector3.Distance(x.Path.Points[0], point) < 7.5f || Vector3.Distance(x.Path.Points[x.Path.Points.Length - 1], point) < 7.5f));
                    else if (endPoints.Any(x => Vector3.Distance(x, point) < 7.5f))
                        newRoad = TerrainMeta.Path.Roads.FirstOrDefault(x => !ins._config.pathConfig.blockRoads.Contains(TerrainMeta.Path.Roads.IndexOf(x)) && x.Path.Length > ins._config.pathConfig.minRoadLength && !caravanPath.includedRoadIndexes.Contains(GetRoadIndex(x)) && x.Path.Points.Any(y => Vector3.Distance(y, point) < 7.5f));

                    if (newRoad != null)
                    {
                        int newRoadIndex = GetRoadIndex(newRoad);
                        int pointForConnectionIndex = caravanPath.points.IndexOf(newPathPoint);

                        pathConnectedData = new PathConnectedData
                        {
                            pathRoadIndex = roadIndex,
                            pathPointIndex = pointIndex,
                            newRoadIndex = newRoadIndex,
                            pointForConnectionIndex = pointForConnectionIndex
                        };
                    }

                    return newPathPoint;
                }

                static bool IsRingRoad(PathList road)
                {
                    return road.Hierarchy == 0 && Vector3.Distance(road.Path.Points[0], road.Path.Points[road.Path.Points.Length - 1]) < 2f;
                }

                class PathConnectedData
                {
                    internal int pathRoadIndex;
                    internal int pathPointIndex;
                    internal int newRoadIndex;
                    internal int pointForConnectionIndex;
                }
            }

            class RoadMonumentData
            {
                public string name;
                public List<Vector3> localPathPoints;
                public Vector3 monumentSize;
                public HashSet<MonumentInfo> monuments;
            }
        }

        class EventPath
        {
            internal List<PathPoint> points = new List<PathPoint>();
            internal List<int> includedRoadIndexes = new List<int>();
            internal bool isRoundRoad;
            internal PathPoint startPathPoint;
            internal Vector3 spawnRotation;

            internal EventPath(bool isRoundRoad)
            {
                this.isRoundRoad = isRoundRoad;
            }
        }

        class PathPoint
        {
            internal Vector3 position;
            internal List<PathPoint> connectedPoints = new List<PathPoint>();
            internal bool disabled;
            internal int roadIndex;
            internal float lastVisitTime;

            internal PathPoint(Vector3 position, int roadIndex)
            {
                this.position = position;
                this.roadIndex = roadIndex;
            }

            internal void ConnectPoint(PathPoint pathPoint)
            {
                connectedPoints.Add(pathPoint);
            }
        }

        class EventHeli : FacepunchBehaviour
        {
            static EventHeli eventHeli;

            internal HeliConfig heliConfig;
            PatrolHelicopter patrolHelicopter;
            Vector3 patrolPosition;
            int ounsideTime;
            bool isFollowing;
            bool isDead;
            internal ulong lastAttackedPlayer;

            internal static EventHeli SpawnHeli(HeliConfig heliConfig)
            {
                Vector3 position = ConvoyPathVehicle.GetEventPosition() + Vector3.up * heliConfig.height;

                PatrolHelicopter patrolHelicopter = BuildManager.SpawnRegularEntity("assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab", position, Quaternion.identity, 755446) as PatrolHelicopter;
                patrolHelicopter.transform.position = position;
                eventHeli = patrolHelicopter.gameObject.AddComponent<EventHeli>();
                eventHeli.Init(heliConfig, patrolHelicopter);
                return eventHeli;
            }

            internal static EventHeli GetEventHeliByNetId(ulong netID)
            {
                if (eventHeli != null && eventHeli.patrolHelicopter.IsExists() && eventHeli.patrolHelicopter.net != null && eventHeli.patrolHelicopter.net.ID.Value == netID)
                    return eventHeli;
                else
                    return null;
            }

            internal static EventHeli GetClosestHeli(Vector3 position)
            {
                return eventHeli;
            }

            internal static bool IsEventHeliAlive()
            {
                return eventHeli != null && eventHeli.patrolHelicopter.IsExists();
            }

            internal static HashSet<ulong> GetAliveHeliesNetIDS()
            {
                HashSet<ulong> helies = new HashSet<ulong>();

                if (eventHeli != null && eventHeli.patrolHelicopter != null && eventHeli.patrolHelicopter.net != null)
                    helies.Add(eventHeli.patrolHelicopter.net.ID.Value);

                return helies;
            }

            void Init(HeliConfig heliConfig, PatrolHelicopter patrolHelicopter)
            {
                this.heliConfig = heliConfig;
                this.patrolHelicopter = patrolHelicopter;
                UpdateHelicopter();
                StartFollowing();
                patrolHelicopter.InvokeRepeating(UpdatePosition, 1, 1);
            }

            void UpdateHelicopter()
            {
                BuildManager.UpdateEntityMaxHealth(patrolHelicopter, heliConfig.hp);
                patrolHelicopter.maxCratesToSpawn = heliConfig.cratesAmount;
                patrolHelicopter.bulletDamage = heliConfig.bulletDamage;
                patrolHelicopter.bulletSpeed = heliConfig.bulletSpeed;

                var weakspots = patrolHelicopter.weakspots;
                if (weakspots != null && weakspots.Length > 1)
                {
                    weakspots[0].maxHealth = heliConfig.mainRotorHealth;
                    weakspots[0].health = heliConfig.mainRotorHealth;
                    weakspots[1].maxHealth = heliConfig.rearRotorHealth;
                    weakspots[1].health = heliConfig.rearRotorHealth;
                }
            }

            void UpdatePosition()
            {
                if (isDead)
                    return;

                isDead = patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.DEATH;

                if (isDead && heliConfig.immediatelyKill)
                {
                    patrolHelicopter.Hurt(patrolHelicopter.health * 2f, DamageType.Generic, null, useProtection: false);
                    return;
                }

                patrolHelicopter.myAI.spawnTime = UnityEngine.Time.realtimeSinceStartup;

                if (patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.STRAFE || patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.ORBIT || patrolHelicopter.myAI._currentState == PatrolHelicopterAI.aiState.ORBITSTRAFE)
                    return;

                if (ins.eventController.IsStopped())
                {
                    if (isFollowing)
                        StartPatrol();
                }
                else if (!isFollowing)
                    StartFollowing();

                if (isFollowing)
                    DoFollowing();
                else
                    DoPatrol();
            }

            void StartFollowing()
            {
                isFollowing = true;
            }

            void StartPatrol()
            {
                isFollowing = false;
                ounsideTime = 0;
                patrolPosition = ConvoyPathVehicle.GetEventPosition() + Vector3.up * heliConfig.height;
            }

            void DoFollowing()
            {
                Vector3 position = ConvoyPathVehicle.GetEventPosition();

                if (position == Vector3.zero)
                {
                    Kill();
                    return;
                }

                position += Vector3.up * heliConfig.height;
                patrolHelicopter.myAI.State_Move_Enter(position);

                if (Vector3.Distance(patrolHelicopter.transform.position, position) < 35)
                {
                    Vector3 targetRotation = (position - patrolHelicopter.transform.position).normalized;
                    targetRotation.y = 0;
                    patrolHelicopter.myAI.SetIdealRotation(Quaternion.LookRotation(targetRotation));
                }
            }

            void DoPatrol()
            {
                if (patrolHelicopter.myAI.leftGun.HasTarget() || patrolHelicopter.myAI.rightGun.HasTarget())
                {
                    if (Vector3.Distance(patrolPosition, patrolHelicopter.transform.position) > heliConfig.distance)
                    {
                        ounsideTime++;

                        if (ounsideTime > heliConfig.outsideTime)
                            patrolHelicopter.myAI.State_Move_Enter(patrolPosition);
                    }
                    else
                    {
                        ounsideTime = 0;
                    }
                }
                else if (Vector3.Distance(patrolPosition, patrolHelicopter.transform.position) > heliConfig.distance)
                {
                    patrolHelicopter.myAI.State_Move_Enter(patrolPosition);
                    ounsideTime = 0;
                }
                else
                    ounsideTime = 0;
            }

            internal bool IsHeliCanTarget()
            {
                return ins.eventController.IsAgressive();
            }

            internal void OnHeliAttacked(ulong userId)
            {
                if (patrolHelicopter.myAI.isDead)
                    return;
                else
                    lastAttackedPlayer = userId;
            }

            internal void Kill()
            {
                if (patrolHelicopter.IsExists())
                    patrolHelicopter.Kill();
            }

            internal static void ClearData()
            {
                if (eventHeli != null)
                    eventHeli.Kill();

                eventHeli = null;
            }
        }

        class ZoneController : FacepunchBehaviour
        {
            static ZoneController zoneController;
            SphereCollider sphereCollider;
            Coroutine zoneUpdateCorountine;
            HashSet<BaseEntity> spheres = new HashSet<BaseEntity>();
            HashSet<BasePlayer> playersInZone = new HashSet<BasePlayer>();

            internal static void CreateZone(BasePlayer externalOwner = null)
            {
                TryDeleteZone();
                Vector3 position = ConvoyPathVehicle.GetEventPosition();

                if (position == Vector3.zero)
                    return;

                GameObject gameObject = new GameObject();
                gameObject.transform.position = position;
                gameObject.layer = (int)Rust.Layer.Reserved1;

                zoneController = gameObject.AddComponent<ZoneController>();
                zoneController.Init(externalOwner);
            }

            internal static bool IsZoneCreated()
            {
                return zoneController != null;
            }

            internal static bool IsPlayerInZone(ulong userID)
            {
                return zoneController != null && zoneController.playersInZone.Any(x => x != null && x.userID == userID);
            }

            internal static bool IsAnyPlayerInEventZone()
            {
                return zoneController != null && zoneController.playersInZone.Any(x => x.IsExists() && !x.IsSleeping());
            }

            internal static void OnPlayerLeaveZone(BasePlayer player)
            {
                if (zoneController == null)
                    return;

                Interface.CallHook($"OnPlayerExit{ins.Name}", player);
                zoneController.playersInZone.Remove(player);
                GuiManager.DestroyGui(player);

                if (ins._config.zoneConfig.isPVPZone)
                {
                    if (ins.plugins.Exists("DynamicPVP") && (bool)ins.DynamicPVP.Call("IsPlayerInPVPDelay", (ulong)player.userID))
                        return;

                    NotifyManager.SendMessageToPlayer(player, "ExitPVP", ins._config.prefix);
                }
            }

            internal static bool IsEventPosition(Vector3 position)
            {
                Vector3 eventPosition = ConvoyPathVehicle.GetEventPosition();
                return Vector3.Distance(position, eventPosition) < ins.eventController.eventConfig.zoneRadius;
            }

            internal static HashSet<BasePlayer> GetAllPlayersInZone()
            {
                if (zoneController == null)
                    return new HashSet<BasePlayer>();
                else
                    return zoneController.playersInZone;
            }

            void Init(BasePlayer externalOwner)
            {
                CreateTriggerSphere();
                CreateSpheres();

                if (PveModeManager.IsPveModeReady())
                    PveModeManager.CreatePveModeZone(this.transform.position, externalOwner);

                zoneUpdateCorountine = ServerMgr.Instance.StartCoroutine(ZoneUpdateCorountine());
            }

            void CreateTriggerSphere()
            {
                sphereCollider = gameObject.AddComponent<SphereCollider>();
                sphereCollider.isTrigger = true;
                sphereCollider.radius = ins.eventController.eventConfig.zoneRadius;
            }

            void CreateSpheres()
            {
                if (ins._config.zoneConfig.isDome)
                    for (int i = 0; i < ins._config.zoneConfig.darkening; i++)
                        CreateSphere("assets/prefabs/visualization/sphere.prefab");

                if (ins._config.zoneConfig.isColoredBorder)
                {
                    string spherePrefab = ins._config.zoneConfig.borderColor == 0 ? "assets/bundled/prefabs/modding/events/twitch/br_sphere.prefab" : ins._config.zoneConfig.borderColor == 1 ? "assets/bundled/prefabs/modding/events/twitch/br_sphere_green.prefab" :
                         ins._config.zoneConfig.borderColor == 2 ? "assets/bundled/prefabs/modding/events/twitch/br_sphere_purple.prefab" : "assets/bundled/prefabs/modding/events/twitch/br_sphere_red.prefab";

                    for (int i = 0; i < ins._config.zoneConfig.brightness; i++)
                        CreateSphere(spherePrefab);
                }
            }

            void CreateSphere(string prefabName)
            {
                BaseEntity sphere = GameManager.server.CreateEntity(prefabName, gameObject.transform.position);
                SphereEntity entity = sphere.GetComponent<SphereEntity>();
                entity.currentRadius = ins.eventController.eventConfig.zoneRadius * 2;
                entity.lerpSpeed = 0f;
                sphere.enableSaving = false;
                sphere.Spawn();
                spheres.Add(sphere);
            }

            void OnTriggerEnter(Collider other)
            {
                BasePlayer player = other.GetComponentInParent<BasePlayer>();
                if (player.IsRealPlayer())
                {
                    Interface.CallHook($"OnPlayerEnter{ins.Name}", player);
                    playersInZone.Add(player);

                    if (ins._config.zoneConfig.isPVPZone)
                        NotifyManager.SendMessageToPlayer(player, "EnterPVP", ins._config.prefix);

                    GuiManager.CreateGui(player, NotifyManager.GetTimeMessage(player.UserIDString, ins.eventController.GetEventTime()), LootManager.GetCountOfUnlootedCrates().ToString(), NpcSpawnManager.GetEventNpcCount().ToString());
                }
            }

            void OnTriggerExit(Collider other)
            {
                BasePlayer player = other.GetComponentInParent<BasePlayer>();

                if (player.IsRealPlayer())
                    OnPlayerLeaveZone(player);
            }

            IEnumerator ZoneUpdateCorountine()
            {
                while (zoneController != null)
                {
                    int countOfCrates = LootManager.GetCountOfUnlootedCrates();
                    int countOfGuardNpc = NpcSpawnManager.GetEventNpcCount();

                    foreach (BasePlayer player in playersInZone)
                        if (player != null)
                            GuiManager.CreateGui(player, NotifyManager.GetTimeMessage(player.UserIDString, ins.eventController.GetEventTime()), countOfCrates.ToString(), countOfGuardNpc.ToString());

                    yield return CoroutineEx.waitForSeconds(1f);
                }
            }

            internal static void TryDeleteZone()
            {
                if (zoneController != null)
                    zoneController.DeleteZone();
            }

            void DeleteZone()
            {
                foreach (BaseEntity sphere in spheres)
                    if (sphere != null && !sphere.IsDestroyed)
                        sphere.Kill();

                if (zoneUpdateCorountine != null)
                    ServerMgr.Instance.StopCoroutine(zoneUpdateCorountine);

                GuiManager.DestroyAllGui();
                PveModeManager.DeletePveModeZone();
                UnityEngine.GameObject.Destroy(gameObject);
            }
        }

        static class PveModeManager
        {
            static HashSet<ulong> pveModeOwners = new HashSet<ulong>();
            static BasePlayer owner;
            static float lastZoneDeleteTime;

            internal static bool IsPveModeReady()
            {
                return ins._config.supportedPluginsConfig.pveMode.enable && ins.plugins.Exists("PveMode");
            }

            internal static BasePlayer UpdateAndGetEventOwner()
            {
                if (ins.eventController.IsStopped())
                    return owner;

                float timeScienceLastZoneDelete = Time.realtimeSinceStartup - lastZoneDeleteTime;

                if (timeScienceLastZoneDelete > ins._config.supportedPluginsConfig.pveMode.timeExitOwner)
                    owner = null;

                return owner;
            }

            internal static void CreatePveModeZone(Vector3 position, BasePlayer externalOwner)
            {
                Dictionary<string, object> config = GetPveModeConfig();

                HashSet<ulong> npcs = NpcSpawnManager.GetEventNpcNetIds();
                HashSet<ulong> bradleys = ConvoyPathVehicle.GetAliveBradleysNetIDS();
                HashSet<ulong> helicopters = EventHeli.GetAliveHeliesNetIDS();
                HashSet<ulong> crates = LootManager.GetEventCratesNetIDs();
                HashSet<ulong> turrets = ins.eventController.GetAliveTurretsNetIDS();

                BasePlayer playerOwner = GetEventOwner();

                if (playerOwner == null)
                    playerOwner = externalOwner;

                ins.PveMode.Call("EventAddPveMode", ins.Name, config, position, ins.eventController.eventConfig.zoneRadius, crates, npcs, bradleys, helicopters, turrets, pveModeOwners, playerOwner);
            }

            static BasePlayer GetEventOwner()
            {
                BasePlayer playerOwner = null;

                float timeScienceLastZoneDelete = Time.realtimeSinceStartup - lastZoneDeleteTime;

                if (owner != null && (ins.eventController.IsStopped() || timeScienceLastZoneDelete < ins._config.supportedPluginsConfig.pveMode.timeExitOwner))
                    playerOwner = owner;

                return playerOwner;
            }

            static Dictionary<string, object> GetPveModeConfig()
            {
                return new Dictionary<string, object>
                {
                    ["Damage"] = ins._config.supportedPluginsConfig.pveMode.damage,
                    ["ScaleDamage"] = ins._config.supportedPluginsConfig.pveMode.scaleDamage,
                    ["LootCrate"] = ins._config.supportedPluginsConfig.pveMode.lootCrate,
                    ["HackCrate"] = ins._config.supportedPluginsConfig.pveMode.hackCrate,
                    ["LootNpc"] = ins._config.supportedPluginsConfig.pveMode.lootNpc,
                    ["DamageNpc"] = ins._config.supportedPluginsConfig.pveMode.damageNpc,
                    ["DamageTank"] = ins._config.supportedPluginsConfig.pveMode.damageTank,
                    ["DamageHelicopter"] = ins._config.supportedPluginsConfig.pveMode.damageHeli,
                    ["DamageTurret"] = ins._config.supportedPluginsConfig.pveMode.damageTurret,
                    ["TargetNpc"] = ins._config.supportedPluginsConfig.pveMode.targetNpc,
                    ["TargetTank"] = ins._config.supportedPluginsConfig.pveMode.targetTank,
                    ["TargetHelicopter"] = ins._config.supportedPluginsConfig.pveMode.targetHeli,
                    ["TargetTurret"] = ins._config.supportedPluginsConfig.pveMode.targetTurret,
                    ["CanEnter"] = ins._config.supportedPluginsConfig.pveMode.canEnter,
                    ["CanEnterCooldownPlayer"] = ins._config.supportedPluginsConfig.pveMode.canEnterCooldownPlayer,
                    ["TimeExitOwner"] = ins._config.supportedPluginsConfig.pveMode.timeExitOwner,
                    ["AlertTime"] = ins._config.supportedPluginsConfig.pveMode.alertTime,
                    ["RestoreUponDeath"] = ins._config.supportedPluginsConfig.pveMode.restoreUponDeath,
                    ["CooldownOwner"] = ins._config.supportedPluginsConfig.pveMode.cooldown,
                    ["Darkening"] = 0
                };
            }

            internal static void DeletePveModeZone()
            {
                if (!IsPveModeReady())
                    return;

                lastZoneDeleteTime = Time.realtimeSinceStartup;
                pveModeOwners = (HashSet<ulong>)ins.PveMode.Call("GetEventOwners", ins.Name);

                if (pveModeOwners == null)
                    pveModeOwners = new HashSet<ulong>();

                ulong userId = (ulong)ins.PveMode.Call("GetEventOwner", ins.Name);
                OnNewOwnerSet(userId);

                ins.PveMode.Call("EventRemovePveMode", ins.Name, false);
            }

            static void OnNewOwnerSet(ulong userId)
            {
                if (userId == 0)
                    return;

                BasePlayer player = BasePlayer.FindByID(userId);
                OnNewOwnerSet(player);
            }

            internal static void OnNewOwnerSet(BasePlayer player)
            {
                owner = player;
            }

            internal static void OnOwnerDeleted()
            {
                owner = null;
            }

            internal static void OnEventEnd()
            {
                if (IsPveModeReady())
                    ins.PveMode.Call("EventAddCooldown", ins.Name, pveModeOwners, ins._config.supportedPluginsConfig.pveMode.cooldown);

                lastZoneDeleteTime = 0;
                pveModeOwners.Clear();
                owner = null;
            }

            internal static bool IsPveModDefaultBlockAction(BasePlayer player)
            {

                if (IsPveModeReady())
                    return ins.PveMode.Call("CanActionEventNoMessage", ins.Name, player) != null;

                return false;
            }

            internal static bool IsPveModeBlockInterractByCooldown(BasePlayer player)
            {
                if (!IsPveModeReady())
                    return false;

                BasePlayer eventOwner = GetEventOwner();

                if ((ins._config.supportedPluginsConfig.pveMode.noInterractIfCooldownAndNoOwners && eventOwner == null) || ins._config.supportedPluginsConfig.pveMode.noDealDamageIfCooldownAndTeamOwner)
                    return !(bool)ins.PveMode.Call("CanTimeOwner", ins.Name, (ulong)player.userID, ins._config.supportedPluginsConfig.pveMode.cooldown);

                return false;
            }

            internal static bool IsPveModeBlockNoOwnerLooting(BasePlayer player)
            {
                if (!IsPveModeReady())
                    return false;

                BasePlayer eventOwner = GetEventOwner();

                if (eventOwner == null)
                    return false;

                if (ins._config.supportedPluginsConfig.pveMode.canLootOnlyOwner && !IsTeam(player, eventOwner.userID))
                    return true;

                return false;
            }

            internal static bool IsPlayerHaveCooldown(ulong userId)
            {
                if (!IsPveModeReady())
                    return false;

                return !(bool)ins.PveMode.Call("CanTimeOwner", ins.Name, userId, ins._config.supportedPluginsConfig.pveMode.cooldown);
            }

            static bool IsTeam(BasePlayer player, ulong targetId)
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

        class EventMapMarker : FacepunchBehaviour
        {
            static EventMapMarker eventMapMarker;

            MapMarkerGenericRadius radiusMarker;
            VendingMachineMapMarker vendingMarker;
            Coroutine updateCounter;

            internal static EventMapMarker CreateMarker()
            {
                if (!ins._config.markerConfig.enable)
                    return null;

                GameObject gameObject = new GameObject();
                gameObject.layer = (int)Rust.Layer.Reserved1;
                eventMapMarker = gameObject.AddComponent<EventMapMarker>();
                eventMapMarker.Init();
                return eventMapMarker;
            }

            void Init()
            {
                Vector3 eventPosition = ConvoyPathVehicle.GetEventPosition();
                CreateRadiusMarker(eventPosition);
                CreateVendingMarker(eventPosition);
                updateCounter = ServerMgr.Instance.StartCoroutine(MarkerUpdateCounter());
            }

            void CreateRadiusMarker(Vector3 position)
            {
                if (!ins._config.markerConfig.useRingMarker)
                    return;

                radiusMarker = GameManager.server.CreateEntity("assets/prefabs/tools/map/genericradiusmarker.prefab", position) as MapMarkerGenericRadius;
                radiusMarker.enableSaving = false;
                radiusMarker.Spawn();
                radiusMarker.radius = ins._config.markerConfig.radius;
                radiusMarker.alpha = ins._config.markerConfig.alpha;
                radiusMarker.color1 = new Color(ins._config.markerConfig.color1.r, ins._config.markerConfig.color1.g, ins._config.markerConfig.color1.b);
                radiusMarker.color2 = new Color(ins._config.markerConfig.color2.r, ins._config.markerConfig.color2.g, ins._config.markerConfig.color2.b);
            }

            void CreateVendingMarker(Vector3 position)
            {
                if (!ins._config.markerConfig.useShopMarker)
                    return;

                vendingMarker = GameManager.server.CreateEntity("assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab", position) as VendingMachineMapMarker;
                vendingMarker.Spawn();
                vendingMarker.markerShopName = $"{ins.eventController.eventConfig.displayName} ({NotifyManager.GetTimeMessage(null, ins.eventController.GetEventTime())})";
            }

            IEnumerator MarkerUpdateCounter()
            {
                while (EventLauncher.IsEventActive())
                {
                    Vector3 position = ConvoyPathVehicle.GetEventPosition();
                    UpdateVendingMarker(position);
                    UpdateRadiusMarker(position);
                    yield return CoroutineEx.waitForSeconds(1f);
                }
            }

            void UpdateRadiusMarker(Vector3 position)
            {
                if (!radiusMarker.IsExists())
                    return;

                radiusMarker.transform.position = position;
                radiusMarker.SendUpdate();
                radiusMarker.SendNetworkUpdate();
            }

            void UpdateVendingMarker(Vector3 position)
            {
                if (!vendingMarker.IsExists())
                    return;

                vendingMarker.transform.position = position;
                BasePlayer pveModeEventOwner = PveModeManager.UpdateAndGetEventOwner();
                string displayEventOwnerName = ins._config.supportedPluginsConfig.pveMode.showEventOwnerNameOnMap && pveModeEventOwner != null ? GetMessage("Marker_EventOwner", null, pveModeEventOwner.displayName) : "";
                vendingMarker.markerShopName = $"{ins.eventController.eventConfig.displayName} ({NotifyManager.GetTimeMessage(null, ins.eventController.GetEventTime())}) {displayEventOwnerName}";
                vendingMarker.SetFlag(BaseEntity.Flags.Busy, pveModeEventOwner == null);
                vendingMarker.SendNetworkUpdate();
            }

            internal static void DeleteMapMarker()
            {
                if (eventMapMarker != null)
                    eventMapMarker.Delete();
            }

            void Delete()
            {
                if (radiusMarker.IsExists())
                    radiusMarker.Kill();

                if (vendingMarker.IsExists())
                    vendingMarker.Kill();

                if (updateCounter != null)
                    ServerMgr.Instance.StopCoroutine(updateCounter);

                Destroy(eventMapMarker.gameObject);
            }
        }

        static class LootManager
        {
            static HashSet<ulong> lootedContainersUids = new HashSet<ulong>();
            static HashSet<StorageContainerData> storageContainers = new HashSet<StorageContainerData>();
            static int countOfUnlootedCrates;

            internal static int GetCountOfUnlootedCrates()
            {
                return countOfUnlootedCrates;
            }

            internal static void UpdateCountOfUnlootedCrates()
            {
                countOfUnlootedCrates = storageContainers.Where(x => x != null && x.entityContainer.IsExists() && x.entityContainer.net != null && !IsCrateLooted(x.entityContainer.net.ID.Value)).Count;
            }

            internal static void OnHeliCrateSpawned(LockedByEntCrate lockedByEntCrate)
            {
                EventHeli eventHeli = EventHeli.GetClosestHeli(lockedByEntCrate.transform.position);

                if (eventHeli == null)
                    return;

                if (Vector3.Distance(lockedByEntCrate.transform.position, eventHeli.transform.position) <= 10)
                {
                    lockedByEntCrate.Invoke(() =>
                    {
                        UpdateBaseLootTable(lockedByEntCrate.inventory, eventHeli.heliConfig.baseLootTableConfig, eventHeli.heliConfig.baseLootTableConfig.clearDefaultItemList);

                        if (eventHeli.heliConfig.instCrateOpen)
                        {
                            lockedByEntCrate.SetLockingEnt(null);
                            lockedByEntCrate.SetLocked(false);
                        }
                    }, 1f);

                    if (PveModeManager.IsPveModeReady())
                        ins.PveMode.Call("EventAddCrates", ins.Name, new HashSet<ulong> { lockedByEntCrate.net.ID.Value });
                }
            }

            internal static void OnBradleyCrateSpawned(LockedByEntCrate lockedByEntCrate)
            {
                BradleyVehicle bradleyVehicle = ConvoyPathVehicle.GetClosestVehicle<BradleyVehicle>(lockedByEntCrate.transform.position) as BradleyVehicle;

                if (bradleyVehicle == null)
                    return;

                lockedByEntCrate.SetLocked(false);

                if (Vector3.Distance(lockedByEntCrate.transform.position, bradleyVehicle.transform.position) <= 10)
                {
                    lockedByEntCrate.Invoke(() =>
                    {
                        UpdateBaseLootTable(lockedByEntCrate.inventory, bradleyVehicle.bradleyConfig.baseLootTableConfig, bradleyVehicle.bradleyConfig.baseLootTableConfig.clearDefaultItemList);

                        if (bradleyVehicle.bradleyConfig.instCrateOpen)
                        {
                            lockedByEntCrate.SetLockingEnt(null);
                            lockedByEntCrate.SetLocked(false);
                        }
                    }, 1f);

                    if (PveModeManager.IsPveModeReady())
                        ins.PveMode.Call("EventAddCrates", ins.Name, new HashSet<ulong> { lockedByEntCrate.net.ID.Value });
                }
            }

            internal static void OnEventCrateLooted(BaseEntity baseEntity, ulong userId)
            {
                if (baseEntity.net == null)
                    return;

                if (!IsCrateLooted(baseEntity.net.ID.Value))
                {
                    double cratePoint;

                    if (ins._config.supportedPluginsConfig.economicsConfig.crates.TryGetValue(baseEntity.PrefabName, out cratePoint))
                        EconomyManager.AddBalance(userId, cratePoint);

                    lootedContainersUids.Add(baseEntity.net.ID.Value);
                }

                UpdateCountOfUnlootedCrates();
            }

            internal static bool IsCrateLooted(ulong netID)
            {
                return lootedContainersUids.Contains(netID);
            }

            internal static bool IsEventCrate(ulong netID)
            {
                return GetContainerDataByNetId(netID) != null;
            }

            internal static StorageContainerData GetContainerDataByNetId(ulong netID)
            {
                return storageContainers.FirstOrDefault(x => x != null && x.entityContainer.IsExists() && x.entityContainer.net != null && x.entityContainer.net.ID.Value == netID);
            }

            internal static HashSet<ulong> GetEventCratesNetIDs()
            {
                HashSet<ulong> eventCrates = new HashSet<ulong>();

                foreach (StorageContainerData storageContainerData in storageContainers)
                    if (storageContainerData != null && storageContainerData.entityContainer != null && storageContainerData.entityContainer.net != null)
                        eventCrates.Add(storageContainerData.entityContainer.net.ID.Value);

                return eventCrates;
            }

            internal static CrateConfig GetCrateConfigByPresetName(string presetName)
            {
                return ins._config.crateConfigs.FirstOrDefault(x => x.presetName == presetName);
            }

            internal static void InitialLootManagerUpdate()
            {
                LootPrefabController.FindPrefabs();
                UpdateLootTables();
            }

            static void UpdateLootTables()
            {
                foreach (CrateConfig crateConfig in ins._config.crateConfigs)
                    UpdateBaseLootTable(crateConfig.lootTableConfig);

                foreach (NpcConfig npcConfig in ins._config.npcConfigs)
                    UpdateBaseLootTable(npcConfig.lootTableConfig);

                foreach (BradleyConfig bradleyConfig in ins._config.bradleyConfigs)
                    UpdateBaseLootTable(bradleyConfig.baseLootTableConfig);

                foreach (HeliConfig heliConfig in ins._config.heliConfigs)
                    UpdateBaseLootTable(heliConfig.baseLootTableConfig);

                ins.SaveConfig();
            }

            static void UpdateBaseLootTable(BaseLootTableConfig baseLootTableConfig)
            {
                for (int i = 0; i < baseLootTableConfig.items.Count; i++)
                {
                    LootItemConfig lootItemConfig = baseLootTableConfig.items[i];

                    if (lootItemConfig.chance <= 0)
                        baseLootTableConfig.items.RemoveAt(i);
                }

                baseLootTableConfig.items = baseLootTableConfig.items.OrderByQuickSort(x => x.chance);

                if (baseLootTableConfig.maxItemsAmount > baseLootTableConfig.items.Count)
                    baseLootTableConfig.maxItemsAmount = baseLootTableConfig.items.Count;

                if (baseLootTableConfig.minItemsAmount > baseLootTableConfig.maxItemsAmount)
                    baseLootTableConfig.minItemsAmount = baseLootTableConfig.maxItemsAmount;
            }

            internal static void UpdateItemContainer(ItemContainer itemContainer, LootTableConfig lootTableConfig, bool deleteItems = false)
            {
                UpdateLootTable(itemContainer, lootTableConfig, deleteItems);
            }

            internal static void UpdateStorageContainer(StorageContainer storageContainer, CrateConfig crateConfig)
            {
                storageContainer.onlyAcceptCategory = ItemCategory.All;
                if (!crateConfig.lootTableConfig.isLootTablePLugin)
                    UpdateLootTable(storageContainer.inventory, crateConfig.lootTableConfig, false);
                else
                    TryAssignLoottable(storageContainer.inventory, crateConfig.presetName);
                storageContainers.Add(new StorageContainerData(storageContainer, crateConfig.presetName));
            }

            internal static void UpdateFridgeContainer(Fridge frige, CrateConfig crateConfig)
            {
                frige.OnlyAcceptCategory = ItemCategory.All;
                if (!crateConfig.lootTableConfig.isLootTablePLugin)
                    UpdateLootTable(frige.inventory, crateConfig.lootTableConfig, false);
                else
                    TryAssignLoottable(frige.inventory, crateConfig.presetName);
                storageContainers.Add(new StorageContainerData(frige, crateConfig.presetName));
            }

            internal static void UpdateLootContainer(LootContainer lootContainer, CrateConfig crateConfig)
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

                lootContainer.Invoke(() => UpdateLootTable(lootContainer.inventory, crateConfig.lootTableConfig, crateConfig.lootTableConfig.clearDefaultItemList), 2f);
                storageContainers.Add(new StorageContainerData(lootContainer, crateConfig.presetName));
            }

            static void DelayUpdateHackableLockedCrate(HackableLockedCrate hackableLockedCrate, CrateConfig crateConfig)
            {
                if (hackableLockedCrate == null || crateConfig.hackTime < 0)
                    return;

                hackableLockedCrate.hackSeconds = HackableLockedCrate.requiredHackSeconds - crateConfig.hackTime;
                UpdateLootTable(hackableLockedCrate.inventory, crateConfig.lootTableConfig, crateConfig.lootTableConfig.clearDefaultItemList);
                hackableLockedCrate.InvokeRepeating(() => hackableLockedCrate.SendNetworkUpdate(), 1f, 1f);
            }

            internal static void UpdateCrateHackTime(HackableLockedCrate hackableLockedCrate, string cratePresetName)
            {
                CrateConfig crateConfig = GetCrateConfigByPresetName(cratePresetName);

                if (crateConfig.hackTime < 0)
                    return;

                hackableLockedCrate.Invoke(() => hackableLockedCrate.hackSeconds = HackableLockedCrate.requiredHackSeconds - crateConfig.hackTime, 1.1f);
            }

            
            // Added by BH fix: assign Loottable preset safely (optional dependency)
            static void TryAssignLoottable(ItemContainer itemContainer, string cratePresetName)
            {
                if (itemContainer == null || string.IsNullOrEmpty(cratePresetName))
                    return;

                if (ins == null || ins.Loottable == null || !ins.plugins.Exists("Loottable"))
                    return;

                try
                {
                    // Map preset key if needed (hard<->nightmare) to keep compatibility with RegisterConvoyPresetsToLoottable()
                    var key = PresetKey(cratePresetName);

                    // Prefer a clean container before filling via external plugin
                    try { itemContainer.Clear(); } catch {}

                    // Try common Loottable APIs. Calls to non-existent methods are safely ignored by Oxide.Call (return null).
                    ins.Loottable.Call("ApplyPresetToContainer", ins, key, itemContainer);
                    ins.Loottable.Call("PopulateContainerWithPreset", ins, key, itemContainer);
                    ins.Loottable.Call("TryApplyPresetToContainer", ins, key, itemContainer);
                    ins.Loottable.Call("AssignContainerPreset", ins, key, itemContainer);
                }
                catch (System.Exception e)
                {
                    ins.PrintWarning($"[Convoy] Loottable assign failed for preset '{cratePresetName}': {e.Message}");
                }
            }
static void UpdateLootTable(ItemContainer itemContainer, LootTableConfig lootTableConfig, bool clearContainer)
            {
                if (itemContainer == null)
                    return;

                UpdateBaseLootTable(itemContainer, lootTableConfig, clearContainer || !string.IsNullOrEmpty(lootTableConfig.alphaLootPresetName));

                if (!string.IsNullOrEmpty(lootTableConfig.alphaLootPresetName))
                {
                    if (ins.plugins.Exists("AlphaLoot") && (bool)ins.AlphaLoot.Call("ProfileExists", lootTableConfig.alphaLootPresetName))
                    {
                        ins.AlphaLoot.Call("PopulateLoot", itemContainer, lootTableConfig.alphaLootPresetName);
                    }
                }
            }

            static void UpdateBaseLootTable(ItemContainer itemContainer, BaseLootTableConfig baseLootTableConfig, bool clearContainer)
            {
                if (itemContainer == null)
                    return;

                if (clearContainer)
                    ClearItemsContainer(itemContainer);

                LootPrefabController.TryAddLootFromPrefabs(itemContainer, baseLootTableConfig.prefabConfigs);
                RandomItemsFiller.TryAddItemsToContainer(itemContainer, baseLootTableConfig);

                if (itemContainer.capacity < itemContainer.itemList.Count)
                    itemContainer.capacity = itemContainer.itemList.Count;
            }

            static void ClearItemsContainer(ItemContainer container)
            {
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    Item item = container.itemList[i];
                    item.RemoveFromContainer();
                    item.Remove();
                }
            }

            internal static void ClearLootData(bool shoudKillCrates = false)
            {
                if (shoudKillCrates)
                    foreach (StorageContainerData storageContainerData in storageContainers)
                        if (storageContainerData != null && storageContainerData.entityContainer.IsExists())
                            storageContainerData.entityContainer.Kill();

                lootedContainersUids.Clear();
                storageContainers.Clear();
            }

            class LootPrefabController
            {
                static HashSet<LootPrefabController> lootPrefabDatas = new HashSet<LootPrefabController>();

                string prefabName;
                LootContainer.LootSpawnSlot[] lootSpawnSlot;
                LootSpawn lootDefinition;
                int maxDefinitionsToSpawn;
                int scrapAmount;

                internal static void TryAddLootFromPrefabs(ItemContainer itemContainer, PrefabLootTableConfigs prefabLootTableConfig)
                {
                    if (!prefabLootTableConfig.isEnable)
                        return;

                    PrefabConfig prefabConfig = prefabLootTableConfig.prefabs.GetRandom();

                    if (prefabConfig == null)
                        return;

                    int multiplicator = UnityEngine.Random.Range(prefabConfig.minLootScale, prefabConfig.maxLootScale + 1);
                    TryFillContainerByPrefab(itemContainer, prefabConfig.prefabName, multiplicator);
                }

                internal static void FindPrefabs()
                {
                    foreach (CrateConfig crateConfig in ins._config.crateConfigs.Where(x => x.lootTableConfig.prefabConfigs.isEnable))
                        foreach (PrefabConfig prefabConfig in crateConfig.lootTableConfig.prefabConfigs.prefabs)
                            TrySaveLootPrefab(prefabConfig.prefabName);

                    foreach (NpcConfig npcConfig in ins._config.npcConfigs.Where(x => x.lootTableConfig.prefabConfigs.isEnable))
                        foreach (PrefabConfig prefabConfig in npcConfig.lootTableConfig.prefabConfigs.prefabs)
                            TrySaveLootPrefab(prefabConfig.prefabName);

                    foreach (BradleyConfig bradleyConfig in ins._config.bradleyConfigs.Where(x => x.baseLootTableConfig.prefabConfigs.isEnable))
                        foreach (PrefabConfig prefabConfig in bradleyConfig.baseLootTableConfig.prefabConfigs.prefabs)
                            TrySaveLootPrefab(prefabConfig.prefabName);

                    foreach (HeliConfig heliConfig in ins._config.heliConfigs.Where(x => x.baseLootTableConfig.prefabConfigs.isEnable))
                        foreach (PrefabConfig prefabConfig in heliConfig.baseLootTableConfig.prefabConfigs.prefabs)
                            TrySaveLootPrefab(prefabConfig.prefabName);
                }

                internal static void TrySaveLootPrefab(string prefabName)
                {
                    if (lootPrefabDatas.Any(x => x.prefabName == prefabName))
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

                    global::HumanNPC humanNPC = gameObject.GetComponent<global::HumanNPC>();

                    if (humanNPC != null && humanNPC.LootSpawnSlots.Length > 0)
                    {
                        SaveLootPrefabData(prefabName, humanNPC.LootSpawnSlots, 0);
                        return;
                    }

                    ScarecrowNPC scarecrowNPC = gameObject.GetComponent<ScarecrowNPC>();

                    if (scarecrowNPC != null && scarecrowNPC.LootSpawnSlots.Length > 0)
                    {
                        SaveLootPrefabData(prefabName, scarecrowNPC.LootSpawnSlots, 0);
                        return;
                    }
                }

                internal static void SaveLootPrefabData(string prefabName, LootContainer.LootSpawnSlot[] lootSpawnSlot, int scrapAmount, LootSpawn lootDefinition = null, int maxDefinitionsToSpawn = 0)
                {
                    LootPrefabController lootPrefabData = new LootPrefabController
                    {
                        prefabName = prefabName,
                        lootSpawnSlot = lootSpawnSlot,
                        lootDefinition = lootDefinition,
                        maxDefinitionsToSpawn = maxDefinitionsToSpawn,
                        scrapAmount = scrapAmount
                    };

                    lootPrefabDatas.Add(lootPrefabData);
                }

                internal static void TryFillContainerByPrefab(ItemContainer itemContainer, string prefabName, int multiplicator)
                {
                    LootPrefabController lootPrefabData = GetDataForPrefabName(prefabName);

                    if (lootPrefabData != null)
                        for (int i = 0; i < multiplicator; i++)
                            lootPrefabData.SpawnPrefabLootInCrate(itemContainer);
                }

                static LootPrefabController GetDataForPrefabName(string prefabName)
                {
                    return lootPrefabDatas.FirstOrDefault(x => x.prefabName == prefabName);
                }

                void SpawnPrefabLootInCrate(ItemContainer itemContainer)
                {
                    if (lootSpawnSlot != null && lootSpawnSlot.Length > 0)
                    {
                        foreach (LootContainer.LootSpawnSlot lootSpawnSlot in lootSpawnSlot)
                            for (int j = 0; j < lootSpawnSlot.numberToSpawn; j++)
                                if (UnityEngine.Random.Range(0f, 1f) <= lootSpawnSlot.probability)
                                    lootSpawnSlot.definition.SpawnIntoContainer(itemContainer);
                    }
                    else if (lootDefinition != null)
                    {
                        for (int i = 0; i < maxDefinitionsToSpawn; i++)
                            lootDefinition.SpawnIntoContainer(itemContainer);
                    }

                    GenerateScrap(itemContainer);
                }

                void GenerateScrap(ItemContainer itemContainer)
                {
                    if (scrapAmount <= 0)
                        return;

                    Item item = ItemManager.CreateByName("scrap", scrapAmount, 0);

                    if (item == null)
                        return;

                    if (!item.MoveToContainer(itemContainer))
                        item.Remove();
                }
            }

            static class RandomItemsFiller
            {
                static Dictionary<char, GrowableGenetics.GeneType> charToGene = new Dictionary<char, GrowableGenetics.GeneType>
                {
                    ['g'] = GrowableGenetics.GeneType.GrowthSpeed,
                    ['y'] = GrowableGenetics.GeneType.Yield,
                    ['h'] = GrowableGenetics.GeneType.Hardiness,
                    ['w'] = GrowableGenetics.GeneType.WaterRequirement,
                };

                internal static void TryAddItemsToContainer(ItemContainer itemContainer, BaseLootTableConfig baseLootTableConfig)
                {
                    if (!baseLootTableConfig.isRandomItemsEnable)
                        return;

                    HashSet<int> includeItemIndexes = new HashSet<int>();
                    int targetItemsCount = UnityEngine.Random.Range(baseLootTableConfig.minItemsAmount, baseLootTableConfig.maxItemsAmount + 1);

                    while (includeItemIndexes.Count < targetItemsCount)
                    {
                        if (!baseLootTableConfig.items.Any(x => x.chance >= 0.1f && !includeItemIndexes.Contains(baseLootTableConfig.items.IndexOf(x))))
                            break;

                        for (int i = 0; i < baseLootTableConfig.items.Count; i++)
                        {
                            if (includeItemIndexes.Contains(i))
                                continue;

                            LootItemConfig lootItemConfig = baseLootTableConfig.items[i];
                            float chance = UnityEngine.Random.Range(0.0f, 100.0f);

                            if (chance <= lootItemConfig.chance)
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

                internal static Item CreateItem(LootItemConfig lootItemConfig)
                {
                    int amount = UnityEngine.Random.Range((int)(lootItemConfig.minAmount), (int)(lootItemConfig.maxAmount + 1));

                    if (amount <= 0)
                        amount = 1;

                    return CreateItem(lootItemConfig, amount);
                }

                internal static Item CreateItem(LootItemConfig itemConfig, int amount)
                {
                    Item item = null;

                    if (itemConfig.isBlueprint)
                    {
                        item = ItemManager.CreateByName("blueprintbase");
                        item.blueprintTarget = ItemManager.FindItemDefinition(itemConfig.shortname).itemid;
                    }
                    else
                        item = ItemManager.CreateByName(itemConfig.shortname, amount, itemConfig.skin);

                    if (item == null)
                    {
                        ins.PrintWarning($"Failed to create item! ({itemConfig.shortname})");
                        return null;
                    }

                    if (!string.IsNullOrEmpty(itemConfig.name))
                        item.name = itemConfig.name;

                    if (itemConfig.genomes != null && itemConfig.genomes.Count > 0)
                    {
                        string genome = itemConfig.genomes.GetRandom();
                        UpdateGenome(item, genome);
                    }

                    return item;
                }

                static void UpdateGenome(Item item, string genome)
                {
                    genome = genome.ToLower();
                    GrowableGenes growableGenes = new GrowableGenes();

                    for (int i = 0; i < 6 && i < genome.Length; ++i)
                    {
                        GrowableGenetics.GeneType geneType;

                        if (!charToGene.TryGetValue(genome[i], out geneType))
                            geneType = GrowableGenetics.GeneType.Empty;

                        growableGenes.Genes[i].Set(geneType, true);
                        GrowableGeneEncoding.EncodeGenesToItem(GrowableGeneEncoding.EncodeGenesToInt(growableGenes), item);
                    }

                }
            }
        }

        class StorageContainerData
        {
            public BaseEntity entityContainer;
            public string presetName;

            public StorageContainerData(BaseEntity entityContainer, string presetName)
            {
                this.presetName = presetName;
                this.entityContainer = entityContainer;
            }
        }

        static class ModularCarManager
        {
            internal static ModularCar SpawnModularCar(Vector3 position, Quaternion rotation, List<string> moduleShortnames)
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

            static int GetRequiredCarLength(List<string> moduleShortnameList)
            {
                int doubleModulesCount = moduleShortnameList.Where(x => x.Contains("2mod")).Count;

                int count = doubleModulesCount + moduleShortnameList.Count; ;

                if (count < 2)
                    count = 2;
                else if (count > 4)
                    count = 4;

                return count;
            }

            static void CreateCarModules(ModularCar modularCar, List<string> modules)
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

            internal static void UpdateCarriageWheel(VisualCarWheel visualCarWheel)
            {
                visualCarWheel.tyreFriction = 10f;
                visualCarWheel.wheelCollider.wheelDampingRate = 0;
                visualCarWheel.powerWheel = false;
                visualCarWheel.brakeWheel = true;
            }

            static void DelayedCarUpdate(ModularCar modularCar)
            {
                if (modularCar == null || modularCar.rigidBody == null)
                    return;

                modularCar.rigidBody.mass = 3000;
                modularCar.SetFlag(BaseEntity.Flags.Locked, true);

                foreach (TriggerBase triggerBase in modularCar.GetComponentsInChildren<TriggerParentEnclosed>())
                    UnityEngine.GameObject.Destroy(triggerBase);

                UpdateModules(modularCar);
                UpdateFuelSystem(modularCar);
                modularCar.SetFlag(BaseEntity.Flags.Busy, true);
            }

            internal static void UpdateFuelSystem(BaseVehicle vehicle)
            {
                EntityFuelSystem entityFuelSystem = vehicle.GetFuelSystem() as EntityFuelSystem;
                entityFuelSystem.cachedHasFuel = true;
                entityFuelSystem.nextFuelCheckTime = float.MaxValue;
            }

            static void UpdateModules(ModularCar modularCar)
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
                        ItemModEngineItem itemModEngineItem;

                        if (!engineStorage.allEngineItems.TryGetItem(1, engineStorage.slotTypes[i], out itemModEngineItem))
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

        static class NpcSpawnManager
        {
            internal static HashSet<ScientistNPC> eventNpcs = new HashSet<ScientistNPC>();

            internal static int GetEventNpcCount()
            {
                return eventNpcs.Where(x => x.IsExists() && !x.isMounted).Count;
            }

            internal static void OnEventNpcKill()
            {

            }

            internal static HashSet<ulong> GetEventNpcNetIds()
            {
                HashSet<ulong> result = new HashSet<ulong>();

                foreach (ScientistNPC scientistNPC in eventNpcs)
                    if (scientistNPC != null && scientistNPC.net != null)
                        result.Add(scientistNPC.net.ID.Value);

                return result;
            }

            internal static ScientistNPC GetScientistByNetId(ulong netId)
            {
                return eventNpcs.FirstOrDefault(x => x != null && x.net != null && x.net.ID.Value == netId);
            }

            internal static bool IsNpcSpawnReady()
            {
                if (!ins.plugins.Exists("NpcSpawn"))
                {
                    ins.PrintError("NpcSpawn plugin doesn`t exist! Please read the file ReadMe.txt. NPCs will not spawn!");
                    ins.NextTick(() => Interface.Oxide.UnloadPlugin(ins.Name));
                    return false;
                }
                else
                    return true;
            }

            internal static ScientistNPC SpawnScientistNpc(string npcPresetName, Vector3 position, float healthFraction, bool isStationary, bool isPassive = false)
            {
                NpcConfig npcConfig = GetNpcConfigByPresetName(npcPresetName);
                if (npcConfig == null)
                {
                    NotifyManager.PrintError(null, "PresetNotFound_Exeption", npcPresetName);
                    return null;
                }

                ScientistNPC scientistNPC = SpawnScientistNpc(npcConfig, position, healthFraction, isStationary, isPassive);

                if (isStationary)
                    UpdateClothesWeight(scientistNPC);

                return scientistNPC;
            }

            internal static ScientistNPC SpawnScientistNpc(NpcConfig npcConfig, Vector3 position, float healthFraction, bool isStationary, bool isPassive)
            {
                JObject baseNpcConfigObj = GetBaseNpcConfig(npcConfig, healthFraction, isStationary, isPassive);

                var spawnPosition = position;
                NavMeshHit navMeshHit;

                if (PositionDefiner.GetNavmeshInPoint(spawnPosition, 6f, out navMeshHit) ||
                    PositionDefiner.GetNavmeshInPoint(spawnPosition, 15f, out navMeshHit) ||
                    PositionDefiner.GetNavmeshInPoint(spawnPosition, 35f, out navMeshHit) ||
                    PositionDefiner.GetNavmeshInPoint(PositionDefiner.GetGroundPositionInPoint(spawnPosition), 35f, out navMeshHit))
                {
                    spawnPosition = navMeshHit.position;
                }

                ScientistNPC scientistNPC = (ScientistNPC)ins.NpcSpawn.Call("SpawnNpc", spawnPosition, baseNpcConfigObj, isPassive);

                if (scientistNPC != null)
                    eventNpcs.Add(scientistNPC);

                return scientistNPC;
            }

            internal static NpcConfig GetNpcConfigByDisplayName(string displayName)
            {
                return ins._config.npcConfigs.FirstOrDefault(x => x.displayName == displayName);
            }

            static NpcConfig GetNpcConfigByPresetName(string npcPresetName)
            {
                return ins._config.npcConfigs.FirstOrDefault(x => x.presetName == npcPresetName);
            }

            static JObject GetBaseNpcConfig(NpcConfig config, float healthFraction, bool isStationary, bool isPassive)
            {
                return new JObject
                {
                    ["Name"] = config.displayName,
                    ["WearItems"] = new JArray
                    {
                        config.wearItems.Select(x => new JObject
                        {
                            ["ShortName"] = x.shortName,
                            ["SkinID"] = x.skinID
                        })
                    },
                    ["BeltItems"] = isPassive ? new JArray() : new JArray { config.beltItems.Select(x => new JObject { ["ShortName"] = x.shortName, ["Amount"] = x.amount, ["SkinID"] = x.skinID, ["mods"] = new JArray { x.mods.ToHashSet() }, ["Ammo"] = x.ammo }) },
                    ["Kit"] = config.kit,
                    ["Health"] = config.health * healthFraction,
                    ["RoamRange"] = isStationary ? 0 : config.roamRange,
                    ["ChaseRange"] = isStationary ? 0 : config.chaseRange,
                    ["SenseRange"] = config.senseRange,
                    ["ListenRange"] = config.senseRange / 2,
                    ["AttackRangeMultiplier"] = config.attackRangeMultiplier,
                    ["CheckVisionCone"] = true,
                    ["VisionCone"] = config.visionCone,
                    ["HostileTargetsOnly"] = false,
                    ["DamageScale"] = config.damageScale,
                    ["TurretDamageScale"] = config.turretDamageScale,
                    ["AimConeScale"] = config.aimConeScale,
                    ["DisableRadio"] = config.disableRadio,
                    ["CanRunAwayWater"] = true,
                    ["CanSleep"] = false,
                    ["SleepDistance"] = 100f,
                    ["Speed"] = isStationary ? 0 : config.speed,
                    ["AreaMask"] = 1,
                    ["AgentTypeID"] = -1372625422,
                    ["HomePosition"] = string.Empty,
                    ["MemoryDuration"] = config.memoryDuration,
                    ["States"] = isPassive ? new JArray() : isStationary ? new JArray { "IdleState", "CombatStationaryState" } : config.beltItems.Any(x => x.shortName == "rocket.launcher" || x.shortName == "explosive.timed") ? new JArray { "RaidState", "RoamState", "ChaseState", "CombatState" } : new JArray { "RoamState", "ChaseState", "CombatState" }
                };
            }

            static void UpdateClothesWeight(ScientistNPC scientistNPC)
            {
                foreach (Item item in scientistNPC.inventory.containerWear.itemList)
                {
                    ItemModWearable component = item.info.GetComponent<ItemModWearable>();

                    if (component != null)
                        component.weight = 0;
                }
            }

            internal static void ClearData(bool shoudKillNpcs)
            {
                if (shoudKillNpcs)
                    foreach (ScientistNPC scientistNPC in eventNpcs)
                        if (scientistNPC.IsExists())
                            scientistNPC.Kill();

                eventNpcs.Clear();
            }
        }

        static class BuildManager
        {
            internal static void UpdateMeshColliders(BaseEntity entity)
            {
                MeshCollider[] meshColliders = entity.GetComponentsInChildren<MeshCollider>();

                for (int i = 0; i < meshColliders.Length; i++)
                {
                    MeshCollider meshCollider = meshColliders[i];
                    meshCollider.convex = true;
                }
            }

            internal static BaseEntity SpawnChildEntity(BaseEntity parrentEntity, string prefabName, LocationConfig locationConfig, ulong skinId, bool isDecor)
            {
                Vector3 localPosition = locationConfig.position.ToVector3();
                Vector3 localRotation = locationConfig.rotation.ToVector3();
                return SpawnChildEntity(parrentEntity, prefabName, localPosition, localRotation, skinId, isDecor);
            }

            internal static BaseEntity SpawnRegularEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0, bool enableSaving = false)
            {
                BaseEntity entity = CreateEntity(prefabName, position, rotation, skinId, enableSaving);
                entity.Spawn();
                return entity;
            }

            internal static BaseEntity SpawnStaticEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0)
            {
                BaseEntity entity = CreateEntity(prefabName, position, rotation, skinId, false);
                DestroyUnnessesaryComponents(entity);

                StabilityEntity stabilityEntity = entity as StabilityEntity;
                if (stabilityEntity != null)
                    stabilityEntity.grounded = true;

                BaseCombatEntity baseCombatEntity = entity as BaseCombatEntity;
                if (baseCombatEntity != null)
                    baseCombatEntity.pickup.enabled = false;

                entity.Spawn();
                return entity;
            }

            internal static BaseEntity SpawnChildEntity(BaseEntity parrentEntity, string prefabName, Vector3 localPosition, Vector3 localRotation, ulong skinId = 0, bool isDecor = true, bool enableSaving = false)
            {
                BaseEntity entity = isDecor ? CreateDecorEntity(prefabName, parrentEntity.transform.position, Quaternion.identity, skinId) : CreateEntity(prefabName, parrentEntity.transform.position, Quaternion.identity, skinId, enableSaving);
                SetParent(parrentEntity, entity, localPosition, localRotation);

                DestroyUnnessesaryComponents(entity);

                if (isDecor)
                    DestroyDecorComponents(entity);

                UpdateMeshColliders(entity);
                entity.Spawn();
                return entity;
            }

            internal static void UpdateEntityMaxHealth(BaseCombatEntity baseCombatEntity, float maxHealth)
            {
                baseCombatEntity.startHealth = maxHealth;
                baseCombatEntity.InitializeHealth(maxHealth, maxHealth);
            }

            internal static BaseEntity CreateEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId, bool enableSaving)
            {
                BaseEntity entity = GameManager.server.CreateEntity(prefabName, position, rotation);
                entity.enableSaving = enableSaving;
                entity.skinID = skinId;
                return entity;
            }

            static BaseEntity CreateDecorEntity(string prefabName, Vector3 position, Quaternion rotation, ulong skinId = 0, bool enableSaving = false)
            {
                BaseEntity entity = CreateEntity(prefabName, position, rotation, skinId, enableSaving);

                BaseEntity trueBaseEntity = entity.gameObject.AddComponent<BaseEntity>();
                CopySerializableFields(entity, trueBaseEntity);
                UnityEngine.Object.DestroyImmediate(entity, true);
                entity.SetFlag(BaseEntity.Flags.Busy, true);
                entity.SetFlag(BaseEntity.Flags.Locked, true);

                return trueBaseEntity;
            }

            internal static void SetParent(BaseEntity parrentEntity, BaseEntity childEntity, Vector3 localPosition, Vector3 localRotation)
            {
                childEntity.SetParent(parrentEntity, true, false);
                childEntity.transform.localPosition = localPosition;
                childEntity.transform.localEulerAngles = localRotation;
            }

            static void DestroyDecorComponents(BaseEntity entity)
            {
                Component[] components = entity.GetComponentsInChildren<Component>();

                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];

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

                        UnityEngine.GameObject.DestroyImmediate(component as UnityEngine.Object);
                    }
                }
            }

            static void DestroyUnnessesaryComponents(BaseEntity entity)
            {
                DestroyEntityConponent<GroundWatch>(entity);
                DestroyEntityConponent<DestroyOnGroundMissing>(entity);
                DestroyEntityConponent<TriggerHurtEx>(entity);

                if (entity is BradleyAPC == false)
                    DestroyEntityConponent<Rigidbody>(entity);
            }

            internal static void DestroyEntityConponent<TypeForDestroy>(BaseEntity entity)
            {
                if (entity == null)
                    return;

                TypeForDestroy component = entity.GetComponent<TypeForDestroy>();
                if (component != null)
                    UnityEngine.GameObject.DestroyImmediate(component as UnityEngine.Object);
            }

            internal static void DestroyEntityConponents<TypeForDestroy>(BaseEntity entity)
            {
                if (entity == null)
                    return;

                TypeForDestroy[] components = entity.GetComponentsInChildren<TypeForDestroy>();

                for (int i = 0; i < components.Length; i++)
                {
                    TypeForDestroy component = components[i];

                    if (component != null)
                        UnityEngine.GameObject.DestroyImmediate(component as UnityEngine.Object);
                }
            }

            internal static void CopySerializableFields<T>(T src, T dst)
            {
                FieldInfo[] srcFields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (FieldInfo field in srcFields)
                {
                    object value = field.GetValue(src);
                    field.SetValue(dst, value);
                }
            }
        }

        static class PositionDefiner
        {
            internal static Vector3 GetGlobalPosition(Transform parentTransform, Vector3 position)
            {
                return parentTransform.transform.TransformPoint(position);
            }

            internal static Quaternion GetGlobalRotation(Transform parentTransform, Vector3 rotation)
            {
                return parentTransform.rotation * Quaternion.Euler(rotation);
            }

            internal static Vector3 GetLocalPosition(Transform parentTransform, Vector3 globalPosition)
            {
                return parentTransform.transform.InverseTransformPoint(globalPosition);
            }

            internal static Vector3 GetGroundPositionInPoint(Vector3 position)
            {
                position.y = 100;
                RaycastHit raycastHit;

                if (Physics.Raycast(position, Vector3.down, out raycastHit, 500, 1 << 16 | 1 << 23))
                    position.y = raycastHit.point.y;

                return position;
            }

            internal static bool GetNavmeshInPoint(Vector3 position, float radius, out NavMeshHit navMeshHit)
            {
                return NavMesh.SamplePosition(position, out navMeshHit, radius, 1);
            }
        }

        static class GuiManager
        {
            static bool isLoadingImageFailed;
            const float tabWidth = 109;
            const float tabHeigth = 25;
            static ImageInfo tabImageInfo = new ImageInfo("Tab_Adem");
            static List<ImageInfo> iconImageInfos = new List<ImageInfo>
            {
                new ImageInfo("Clock_Adem"),
                new ImageInfo("Crates_Adem"),
                new ImageInfo("Soldiers_Adem"),
            };

            internal static void LoadImages()
            {
                ServerMgr.Instance.StartCoroutine(LoadImagesCoroutine());
            }

            static IEnumerator LoadImagesCoroutine()
            {
                yield return LoadTabCoroutine();

                if (!isLoadingImageFailed)
                    yield return LoadIconsCoroutine();
            }

            static IEnumerator LoadTabCoroutine()
            {
                string url = "file://" + Interface.Oxide.DataDirectory + Path.DirectorySeparatorChar + "Images/" + tabImageInfo.imageName + ".png";

                using (WWW www = new WWW(url))
                {
                    yield return www;

                    if (www.error != null)
                    {
                        OnImageSaveFailed(tabImageInfo.imageName);
                        isLoadingImageFailed = true;
                    }
                    else
                    {
                        Texture2D texture = www.texture;
                        uint imageId = FileStorage.server.Store(texture.EncodeToPNG(), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                        tabImageInfo.imageId = imageId.ToString();
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
            }

            static IEnumerator LoadIconsCoroutine()
            {
                for (int i = 0; i < iconImageInfos.Count; i++)
                {
                    ImageInfo imageInfo = iconImageInfos[i];
                    string url = "file://" + Interface.Oxide.DataDirectory + Path.DirectorySeparatorChar + "Images/" + imageInfo.imageName + ".png";

                    using (WWW www = new WWW(url))
                    {
                        yield return www;

                        if (www.error != null)
                        {
                            OnImageSaveFailed(imageInfo.imageName);
                            break;
                        }
                        else
                        {
                            Texture2D texture = www.texture;
                            uint imageId = FileStorage.server.Store(texture.EncodeToPNG(), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                            imageInfo.imageId = imageId.ToString();
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }
                }
            }

            static void OnImageSaveFailed(string imageName)
            {
                NotifyManager.PrintError(null, $"Move the contents of the data folder from the archive you downloaded from the website into the oxide/data folder on your server!");
                Interface.Oxide.UnloadPlugin(ins.Name);
            }

            internal static void CreateGui(BasePlayer player, params string[] args)
            {
                if (!ins._config.guiConfig.isEnable)
                    return;

                CuiHelper.DestroyUi(player, "Tabs_Adem");
                CuiElementContainer container = new CuiElementContainer();
                float halfWidth = tabWidth / 2 + tabWidth / 2 * (iconImageInfos.Count - 1);

                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"{-halfWidth} {ins._config.guiConfig.offsetMinY}", OffsetMax = $"{halfWidth} {ins._config.guiConfig.offsetMinY + tabHeigth}" },
                    CursorEnabled = false,
                }, "Under", "Tabs_Adem");

                float xmin = 0;

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    DrawTab(ref container, i, arg, xmin);
                    xmin += tabWidth;
                }

                CuiHelper.AddUi(player, container);
            }

            static void DrawTab(ref CuiElementContainer container, int index, string text, float xmin)
            {
                ImageInfo imageInfo = iconImageInfos[index];

                container.Add(new CuiElement
                {
                    Name = $"Tab_{index}_Adem",
                    Parent = "Tabs_Adem",
                    Components =
                    {
                        new CuiRawImageComponent { Png = tabImageInfo.imageId },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{xmin} 0", OffsetMax = $"{xmin + tabWidth} {tabHeigth}" }
                    }
                });
                container.Add(new CuiElement
                {
                    Parent = $"Tab_{index}_Adem",
                    Components =
                    {
                        new CuiRawImageComponent { Png = imageInfo.imageId },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "9 5", OffsetMax = "23 19" }
                    }
                });
                container.Add(new CuiElement
                {
                    Parent = $"Tab_{index}_Adem",
                    Components =
                    {
                        new CuiTextComponent() { Color = "1 1 1 1", Text = text, Align = TextAnchor.MiddleCenter, FontSize = 10, Font = "robotocondensed-bold.ttf" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "23 5", OffsetMax = $"{tabWidth - 9} 19" }
                    }
                });
            }

            internal static void DestroyAllGui()
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                    if (player != null)
                        DestroyGui(player);
            }

            internal static void DestroyGui(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, "Tabs_Adem");
            }

            class ImageInfo
            {
                public string imageName;
                public string imageId;

                internal ImageInfo(string imageName)
                {
                    this.imageName = imageName;
                }
            }
        }

        static class NotifyManager
        {
            internal static void PrintInfoMessage(BasePlayer player, string langKey, params object[] args)
{
    var uid = player?.UserIDString;
    var msg = ClearColorAndSize(GetMessage(langKey, uid, args));
    ins.Puts($"[Convoy] {msg}");
}

            internal static void PrintError(BasePlayer player, string langKey, params object[] args)
{
    var uid = player?.UserIDString;
    var msg = ClearColorAndSize(GetMessage(langKey, uid, args));
    ins.PrintError($"[Convoy] {msg}");
}

            internal static void PrintLogMessage(string langKey, params object[] args)
            {
                for (int i = 0; i < args.Length; i++)
                    if (args[i] is int)
                        args[i] = GetTimeMessage(null, (int)args[i]);

                ins.Puts(ClearColorAndSize(GetMessage(langKey, null, args)));
            }

            internal static void PrintWarningMessage(string langKey, params object[] args)
            {
                ins.PrintWarning(ClearColorAndSize(GetMessage(langKey, null, args)));
            }

            internal static string ClearColorAndSize(string message)
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

            internal static void SendMessageToAll(string langKey, params object[] args)
            {
            // Global chat broadcast (once), if enabled — with smart prefix handling
            if (ins._config.notifyConfig.isChatEnable)
            {
                string text = GetMessage(langKey, null, args);
                bool hasColor = text.Contains("<color=");
                bool hasOwnPrefix = text.Contains("[АНОНС]") || text.Contains("[КОНВОЙ") || text.TrimStart().StartsWith("[");
                if (hasColor || hasOwnPrefix)
                {
                    // Text already styled / has its own prefix — send as is
                    ins.Server.Broadcast(text);
                }
                else
                {
                    // Add configured prefix and keep body white
                    var msgForChat = $"{ins._config.prefix} {ClearColorAndSize(text)}";
                    ins.Server.Broadcast(msgForChat);
                }
            }

                foreach (BasePlayer player in BasePlayer.activePlayerList)
                    if (player != null)
                        SendMessageToPlayer(player, langKey, args);

                TrySendDiscordMessage(langKey, args);
            }

            internal static void SendMessageToPlayer(BasePlayer player, string langKey, params object[] args)
            {
                object[] argsClone = new object[args.Length];

                for (int i = 0; i < args.Length; i++)
                    argsClone[i] = args[i];

                for (int i = 0; i < argsClone.Length; i++)
                    if (argsClone[i] is int)
                        argsClone[i] = GetTimeMessage(player.UserIDString, (int)argsClone[i]);

                string playerMessage = GetMessage(langKey, player.UserIDString, argsClone);

                if (ins._config.notifyConfig.isChatEnable)
                    // BroadcastSilent($"[Convoy] started at {pos}");

                if (ins._config.notifyConfig.gameTipConfig.isEnabled)
                    player.SendConsoleCommand("gametip.showtoast", ins._config.notifyConfig.gameTipConfig.style, ClearColorAndSize(playerMessage), string.Empty);

                if (ins._config.supportedPluginsConfig.guiAnnouncementsConfig.isEnabled && ins.plugins.Exists("guiAnnouncementsConfig"))
                    ins.GUIAnnouncements?.Call("CreateAnnouncement", ClearColorAndSize(playerMessage), ins._config.supportedPluginsConfig.guiAnnouncementsConfig.bannerColor, ins._config.supportedPluginsConfig.guiAnnouncementsConfig.textColor, player, ins._config.supportedPluginsConfig.guiAnnouncementsConfig.apiAdjustVPosition);

                if (ins._config.supportedPluginsConfig.notifyPluginConfig.isEnabled && ins.plugins.Exists("Notify"))
                    ins.Notify?.Call("SendNotify", player, ins._config.supportedPluginsConfig.notifyPluginConfig.type, ClearColorAndSize(playerMessage));
            }

            internal static string GetTimeMessage(string userIDString, int seconds)
            {
                string message = "";

                TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
                if (timeSpan.Hours > 0) message += $" {timeSpan.Hours} {GetMessage("Hours", userIDString)}";
                if (timeSpan.Minutes > 0) message += $" {timeSpan.Minutes} {GetMessage("Minutes", userIDString)}";
                if (message == "") message += $" {timeSpan.Seconds} {GetMessage("Seconds", userIDString)}";

                return message;
            }

            static void TrySendDiscordMessage(string langKey, params object[] args)
            {
                if (CanSendDiscordMessage(langKey))
                {
                    for (int i = 0; i < args.Length; i++)
                        if (args[i] is int)
                            args[i] = GetTimeMessage(null, (int)args[i]);

                    object fields = new[] { new { name = ins.Title, value = ClearColorAndSize(GetMessage(langKey, null, args)), inline = false } };
                    ins.DiscordMessages?.Call("API_SendFancyMessage", ins._config.supportedPluginsConfig.discordMessagesConfig.webhookUrl, "", ins._config.supportedPluginsConfig.discordMessagesConfig.embedColor, JsonConvert.SerializeObject(fields), null, ins);
                }
            }

            static bool CanSendDiscordMessage(string langKey)
            {
                return ins._config.supportedPluginsConfig.discordMessagesConfig.keys.Contains(langKey) && ins._config.supportedPluginsConfig.discordMessagesConfig.isEnabled && !string.IsNullOrEmpty(ins._config.supportedPluginsConfig.discordMessagesConfig.webhookUrl) && ins._config.supportedPluginsConfig.discordMessagesConfig.webhookUrl != "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            }
        }

        static class EconomyManager
        {
            static readonly Dictionary<ulong, double> playersBalance = new Dictionary<ulong, double>();

            internal static void AddBalance(ulong playerId, double balance)
            {
                if (balance == 0 || playerId == 0)
                    return;

                if (playersBalance.ContainsKey(playerId))
                    playersBalance[playerId] += balance;
                else
                    playersBalance.Add(playerId, balance);
            }

            internal static void OnEventEnd()
            {
                DefineEventWinner();

                if (!ins._config.supportedPluginsConfig.economicsConfig.enable || playersBalance.Count == 0)
                {
                    playersBalance.Clear();
                    return;
                }

                SendBalanceToPlayers();
                playersBalance.Clear();
            }

            static void DefineEventWinner()
            {
                var winnerPair = playersBalance.Max(x => (float)x.Value);

                if (winnerPair.Value > 0)
                    Interface.CallHook($"On{ins.Name}EventWin", winnerPair.Key);

                if (winnerPair.Value >= ins._config.supportedPluginsConfig.economicsConfig.minCommandPoint)
                    foreach (string command in ins._config.supportedPluginsConfig.economicsConfig.commands)
                        ins.Server.Command(command.Replace("{steamid}", $"{winnerPair.Key}"));
            }

            static void SendBalanceToPlayers()
            {
                foreach (KeyValuePair<ulong, double> pair in playersBalance)
                    SendBalanceToPlayer(pair.Key, pair.Value);
            }

            static void SendBalanceToPlayer(ulong userID, double amount)
            {
                if (amount < ins._config.supportedPluginsConfig.economicsConfig.minEconomyPiont)
                    return;

                int intAmount = Convert.ToInt32(amount);

                if (intAmount <= 0)
                    return;

                if (ins._config.supportedPluginsConfig.economicsConfig.plugins.Contains("Economics") && ins.plugins.Exists("Economics"))
                    ins.Economics.Call("Deposit", userID.ToString(), amount);

                if (ins._config.supportedPluginsConfig.economicsConfig.plugins.Contains("Server Rewards") && ins.plugins.Exists("ServerRewards"))
                    ins.ServerRewards.Call("AddPoints", userID, intAmount);

                if (ins._config.supportedPluginsConfig.economicsConfig.plugins.Contains("IQEconomic") && ins.plugins.Exists("IQEconomic"))
                    ins.IQEconomic.Call("API_SET_BALANCE", userID, intAmount);

                BasePlayer player = BasePlayer.FindByID(userID);
                if (player != null)
                    NotifyManager.SendMessageToPlayer(player, "SendEconomy", ins._config.prefix, amount);
            }
        }

        class PathRecorder : FacepunchBehaviour
        {
            static HashSet<PathRecorder> customRouteSavers = new HashSet<PathRecorder>();
            BasePlayer player;
            RidableHorse ridableHorse;
            List<Vector3> positions = new List<Vector3>();

            static PathRecorder GetCustomRouteSavingByUserId(ulong userId)
            {
                return customRouteSavers.FirstOrDefault(x => x != null && x.ridableHorse.IsExists() && x.player != null && x.player.userID == userId);
            }

            internal static void StartRecordingRoute(BasePlayer player)
            {
                if (GetCustomRouteSavingByUserId(player.userID) != null)
                    return;

                RidableHorse ridableHorse = BuildManager.SpawnRegularEntity("assets/content/vehicles/horse/ridablehorse.prefab", player.transform.position, player.eyes.GetLookRotation()) as RidableHorse;
                ridableHorse.AttemptMount(player);
                PathRecorder customRouteSaving = ridableHorse.gameObject.AddComponent<PathRecorder>();
                customRouteSaving.Init(player, ridableHorse);
                customRouteSavers.Add(customRouteSaving);
            }

            internal static void TrySaveRoute(ulong userId, string pathName)
            {
                PathRecorder customRouteSaving = GetCustomRouteSavingByUserId(userId);

                if (customRouteSaving != null)
                    customRouteSaving.SavePath(pathName);
            }

            internal static void TryCancelRoute(ulong userId)
            {
                PathRecorder customRouteSaving = GetCustomRouteSavingByUserId(userId);

                if (customRouteSaving != null)
                    customRouteSaving.KillHorse();
            }

            void Init(BasePlayer player, RidableHorse ridableHorse)
            {
                this.player = player;
                this.ridableHorse = ridableHorse;

                TryAddFindPositionOrDestroy();
            }

            void FixedUpdate()
            {
                if (player == null || !player.isMounted)
                {
                    KillHorse();
                    return;
                }

                Vector3 lastPosition = positions.Last();
                float distance = Vector3.Distance(lastPosition, ridableHorse.transform.position);

                if (distance > 10)
                    TryAddFindPositionOrDestroy();
            }

            void TryAddFindPositionOrDestroy()
            {
                Vector3 newPosition = ridableHorse.transform.position;

                NavMeshHit navMeshHit;
                if (!PositionDefiner.GetNavmeshInPoint(newPosition, 2, out navMeshHit))
                {
                    NotifyManager.PrintError(player, "NavMesh_Exeption");
                    KillHorse();
                    return;
                }

                else
                    positions.Add(navMeshHit.position);
            }

            void SavePath(string pathName)
            {
                float pathLength = GetPathLength();
                if (pathLength < ins._config.pathConfig.minRoadLength)
                {
                    NotifyManager.SendMessageToPlayer(player, "CustomRouteTooShort", ins._config.prefix);
                    return;
                }
                List<string> path = new List<string>();

                foreach (Vector3 point in positions)
                    path.Add(point.ToString());

                CustomRouteData customRouteData = new CustomRouteData
                {
                    points = path
                };

                Interface.Oxide.DataFileSystem.WriteObject($"{ins.Name}/Custom routes/{pathName}", customRouteData);
                NotifyManager.SendMessageToPlayer(player, "CustomRouteSuccess", ins._config.prefix);
                ins._config.pathConfig.customPathConfig.customRoutesPresets.Add(pathName);
                KillHorse();
                ins.SaveConfig();
            }

            float GetPathLength()
            {
                float length = 0;

                for (int i = 0; i < positions.Count - 1; i++)
                {
                    Vector3 thisPoint = positions[i];
                    Vector3 nextPoint = positions[i + 1];
                    float distance = Vector3.Distance(thisPoint, nextPoint);
                    length += distance;
                }

                return length;
            }

            void KillHorse()
            {
                if (ridableHorse.IsExists())
                    ridableHorse.Kill();
            }
        }
        #endregion Classes

        #region Lang
        protected override void LoadDefaultMessages()
{
    var ru = new Dictionary<string, string>
    {
        ["ConvoySpawned_easy"]      = "<color=#ffff00>[АНОНС]</color>\n<color=#cccccc>[РАЗВЕДКА]</color> : <color=#00ff00>БАНДИТСКИЙ КОНВОЙ</color> ожидается через <color=#738d43>{1}</color> сек.",
        ["ConvoySpawned_medium"]    = "<color=#ffff00>[АНОНС]</color>\n<color=#cccccc>[РАЗВЕДКА]</color> : <color=#00aaff>ГРУЗОВОЙ КОНВОЙ</color> ожидается через <color=#738d43>{1}</color> сек.",
        ["ConvoySpawned_hard"]      = "<color=#ffff00>[АНОНС]</color>\n<color=#cccccc>[РАЗВЕДКА]</color> : <color=#ff9900>ВОЕНИЗИРОВАННЫЙ КОНВОЙ</color> ожидается через <color=#738d43>{1}</color> сек.",
        ["ConvoySpawned_nightmare"] = "<color=#ffff00>[АНОНС]</color>\n<color=#cccccc>[РАЗВЕДКА]</color> : <color=#ff0000>КОНВОЙ ЧВК</color> ожидается через <color=#738d43>{1}</color> сек.",

        ["EventStart_easy"]   = "<color=#ffff00>[АНОНС]</color>\n<color=#00ff00>[БАНДИТСКИЙ КОНВОЙ]</color> вышел на дорогу! Жалкие шавки решили катить свой хабар!",
        ["Finish_easy"]       = "<color=#ffff00>[АНОНС]</color>\n<color=#00ff00>[БАНДИТСКИЙ КОНВОЙ]</color> добрался до бандитки. Вы так и не решились напасть!",
        ["Looted_easy"]       = "<color=#ffff00>[АНОНС]</color>\n<color=#00ff00>[БАНДИТСКИЙ КОНВОЙ]</color> разнесён в клочья! Вы выгрызли добычу у падали!",

        ["EventStart_medium"] = "<color=#ffff00>[АНОНС]</color>\n<color=#00aaff>[ГРУЗОВОЙ КОНВОЙ]</color> качает сундуками по трассе! Лезь или сдохни!",
        ["Finish_medium"]     = "<color=#ffff00>[АНОНС]</color>\n<color=#00aaff>[ГРУЗОВОЙ КОНВОЙ]</color> скрылся с глаз. Никто даже не напал? Позор!",
        ["Looted_medium"]     = "<color=#ffff00>[АНОНС]</color>\n<color=#00aaff>[ГРУЗОВОЙ КОНВОЙ]</color> повержен! Хабар растаскан жадными руками!",

        ["EventStart_hard"]   = "<color=#ffff00>[АНОНС]</color>\n<color=#ff9900>[ВОЕНИЗИРОВАННЫЙ КОНВОЙ]</color> вышел на трассу! Готовьтесь к мясорубке!",
        ["Finish_hard"]       = "<color=#ffff00>[АНОНС]</color>\n<color=#ff9900>[ВОЕНИЗИРОВАННЫЙ КОНВОЙ]</color> ушёл без происшествий. Никто не рискнул сунуться!",
        ["Looted_hard"]       = "<color=#ffff00>[АНОНС]</color>\n<color=#ff9900>[ВОЕНИЗИРОВАННЫЙ КОНВОЙ]</color> растоптан! Вы выдрали лут из рук солдат!",

        ["EventStart_nightmare"] = "<color=#ffff00>[АНОНС]</color>\n<color=#ff0000>[КОНВОЙ ЧВК]</color>\nНУ ЧТО ПАДАЛЬ? ГОТОВА ВСТРЕТИТЬСЯ СО СВИНЦОВЫМ ДОЖДЕМ?\nМЫ ВАС РАСКАТАЕМ В ФАРШ И СКОРМИМ СОБАКАМ!\nНЕ СУЙТЕСЬ ПОД НОГИ УРОДЫ, ОСТАНЕТЕСЬ ЦЕЛЫМИ!",
        ["Finish_nightmare"]     = "<color=#ffff00>[АНОНС]</color>\n<color=#ff0000>[КОНВОЙ ЧВК]</color> растворился в дыму. Вы даже не пикнули — позор!",
        ["Looted_nightmare"]     = "<color=#ffff00>[АНОНС]</color>\n<color=#ff0000>[КОНВОЙ ЧВК]</color> ЭТИ ГЕРОИ СОВЕРШИЛИ НЕВОЗМОЖНОЕ!!! ВАЛЬХАЛЛА ЖДЁТ ВАС, БРАТЬЯ!",

        ["EventActive_Exeption"]           = "<color=#ff0000>[КОНВОЙ]</color> Событие уже активно. Подожди завершения или останови текущее!",
        ["ConfigurationNotFound_Exeption"] = "<color=#ff0000>[КОНВОЙ]</color> Конфигурация не найдена. Проверь файлы настроек!",
        ["PresetNotFound_Exeption"]        = "<color=#ff0000>[КОНВОЙ]</color> Пресет не найден. Укажи верный пресет маршрута!",
        ["FileNotFound_Exeption"]          = "<color=#ff0000>[КОНВОЙ]</color> Файл данных не найден или повреждён! ({0}.json)!",
        ["RouteNotFound_Exeption"]         = "Маршрут не удалось сгенерировать! Увеличь минимальную длину дороги или смени тип маршрута!",

        ["CustomRouteDescription"] = "<color=#ff9900>[КОНВОЙ]</color> Кастомный маршрут: <color=#ffff00>{0}</color> → <color=#ffff00>{1}</color>. Готовь засаду.",
        ["SuccessfullyLaunched"]   = "<color=#00ff00>[КОНВОЙ]</color> Ивент запущен успешно.",

        // Глобальные (с плейсхолдерами как в коде)
        ["PreStart"]       = "<color=#ffff00>[АНОНС]</color> {0} Через <color=#738d43>{1}</color> груз пойдёт по дороге!",
        ["EventStart"]     = "<color=#ff9900>[КОНВОЙ]</color> {0} <color=#738d43>{1}</color> вышел на маршрут! Старт: клетка <color=#738d43>{2}</color>.",
        ["ConvoyAttacked"] = "<color=#ff0000>[КОНВОЙ]</color> {0} {1} <color=#ce3f27>напал</color> на конвой!",
        ["DamageDistance"] = "<color=#ff0000>[КОНВОЙ]</color> {0} Подойди <color=#ce3f27>ближе</color>!",
        ["CantLoot"]       = "<color=#ff0000>[КОНВОЙ]</color> {0} Нужно остановить конвой и убить <color=#ce3f27>охрану</color>!",
        ["Looted"]         = "<color=#ff0000>[КОНВОЙ]</color> {0} <color=#738d43>{1}</color> был <color=#ce3f27>ограблен</color>!",
        ["RemainTime"]     = "<color=#ff9900>[КОНВОЙ]</color> {0} {1} будет уничтожен через <color=#ce3f27>{2}</color>!",
        ["PreFinish"]      = "<color=#ffff00>[АНОНС]</color> {0} Ивент завершится через <color=#ce3f27>{1}</color>!",
        ["Finish"]         = "<color=#ffff00>[АНОНС]</color> {0} Ивент <color=#ce3f27>завершён</color>!",

        ["EnterPVP"] = "<color=#ff0000>[КОНВОЙ]</color> Ты вошёл в PVP-зону конвоя. Здесь выживает дерзкий.",
        ["ExitPVP"]  = "<color=#00ff00>[КОНВОЙ]</color> Ты покинул PVP-зону конвоя. Переведи дух.",
        ["SendEconomy"] = "<color=#ff9900>[КОНВОЙ]</color> Начислено: <color=#ffff00>{0}</color> {1}. Кровавый бизнес.",
        ["Hours"]   = "ч",
        ["Minutes"] = "м",
        ["Seconds"] = "с",
        ["PveMode_NewOwner"]    = "<color=#ff9900>[КОНВОЙ]</color> Новый владелец контейнера: <color=#ffff00>{0}</color>.",
        ["Marker_EventOwner"]   = "Владелец ивента: {0}",
        ["PveMode_BlockAction"] = "<color=#ff0000>[КОНВОЙ]</color> Действие заблокировано. Контейнер не твой.",
        ["PveMode_YouAreNoOwner"] = "<color=#ff0000>[КОНВОЙ]</color> Это не твой контейнер."
    };

    // Регистрируем одинаково для ru и en, чтобы игнорить внешние lang-файлы
    lang.RegisterMessages(ru, this, "ru");
    lang.RegisterMessages(ru, this, "en");
}

        internal static string GetMessage(string langKey, string userID) => ins.lang.GetMessage(langKey, ins, userID);

        internal static string GetMessage(string langKey, string userID, params object[] args) => (args.Length == 0) ? GetMessage(langKey, userID) : string.Format(GetMessage(langKey, userID), args);
        #endregion Lang

        #region Data 

        public class CustomRouteData
        {
            [JsonProperty("Points")] public List<string> points { get; set; }
        }

        Dictionary<string, List<List<string>>> roots = new Dictionary<string, List<List<string>>>();

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Title, roots);

        private void LoadData() => roots = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, List<List<string>>>>(Title);

        Dictionary<string, EntityCustomizationData> entityCustomizationData = new Dictionary<string, EntityCustomizationData>
        {
            ["van_default"] = new EntityCustomizationData
            {
                regularEntities = new HashSet<EntityData>
                {
                    new EntityData
                    {
                        prefabName = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab",
                        skin = 0,
                        position = "(0, -0.220, -2.294)",
                        rotation = "(0, 90, 359.06)"
                    }
                },
                decorEntities = new HashSet<EntityData>
                {
                    new EntityData
                    {
                        prefabName = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab",
                        skin = 1984902763,
                        position = "(-1.113, -0.221, -0.147)",
                        rotation = "(0, 0, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/building/door.hinged/door.hinged.metal.prefab",
                        skin = 1984902763,
                        position = "(1.113, -0.221, -0.147)",
                        rotation = "(0, 180, 0)"
                    },

                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(1.206, 2.251, -0.526)",
                        rotation = "(270, 90, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(1.206, 2.251, -0.151)",
                        rotation = "(270, 90, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(1.206, 2.251, 0.223)",
                        rotation = "(270, 90, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(-1.202, 2.251, -0.530)",
                        rotation = "(270, 270, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(-1.202, 2.251, -0.157)",
                        rotation = "(270, 270, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(-1.202, 2.251, 0.218)",
                        rotation = "(270, 270, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(-0.398, 2.182, -2.422)",
                        rotation = "(270, 180, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(-0.025, 2.182, -2.422)",
                        rotation = "(270, 180, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/playerioents/app/storagemonitor/storagemonitor.deployed.prefab",
                        skin = 0,
                        position = "(0.349, 2.182, -2.422)",
                        rotation = "(270, 180, 0)"
                    }
                }
            },
            ["sedan_default"] = new EntityCustomizationData
            {
                regularEntities = new HashSet<EntityData>
                {
                },
                decorEntities = new HashSet<EntityData>
                {
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        skin = 0,
                        position = "(-0.624, 1.715, 0.536)",
                        rotation = "(0, 90, 180)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        skin = 0,
                        position = "(-0.624, 1.715, -0.372)",
                        rotation = "(0, 90, 180)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        skin = 0,
                        position = "(0.624, 1.715, 0.536)",
                        rotation = "(0, 270, 180)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        skin = 0,
                        position = "(0.624, 1.715, -0.372)",
                        rotation = "(0, 270, 180)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        skin = 0,
                        position = "(-0.628, 1.216, -1.776)",
                        rotation = "(0, 90, 175.691)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/prefabs/deployable/weaponracks/weaponrack_single2.deployed.prefab",
                        skin = 0,
                        position = "(0.628, 1.216, -1.776)",
                        rotation = "(0, 270, 184.309)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/content/vehicles/boats/rhib/subents/fuel_storage.prefab",
                        skin = 0,
                        position = "(0.833, 1.09, -2.019)",
                        rotation = "(85.057, 0, 0)"
                    },
                    new EntityData
                    {
                        prefabName = "assets/content/vehicles/boats/rhib/subents/fuel_storage.prefab",
                        skin = 0,
                        position = "(-0.833, 1.09, -2.019)",
                        rotation = "(85.057, 0, 0)"
                    }
                }
            }
        };

        public class EntityCustomizationData
        {
            [JsonProperty("Decorative entities")] public HashSet<EntityData> decorEntities { get; set; }
            [JsonProperty("Regular entities")] public HashSet<EntityData> regularEntities { get; set; }
        }

        public class EntityData
        {
            [JsonProperty("Prefab")] public string prefabName { get; set; }
            [JsonProperty("Skin")] public ulong skin { get; set; }
            [JsonProperty("Position")] public string position { get; set; }
            [JsonProperty("Rotation")] public string rotation { get; set; }
        }
        #endregion Data 

        #region Config  
        PluginConfig _config;

        protected override void LoadDefaultConfig()
        {
            _config = PluginConfig.DefaultConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>();

            if (TryUpdateConfig())
                Config.WriteObject(_config, true);
        }

        bool TryUpdateConfig()
        {
            if (_config.version != Version.ToString())
            {
                PluginConfig defaultConfig = PluginConfig.DefaultConfig();

                VersionNumber versionNumber;
                var versionArray = _config.version.Split('.');
                versionNumber.Major = Convert.ToInt32(versionArray[0]);
                versionNumber.Minor = Convert.ToInt32(versionArray[1]);
                versionNumber.Patch = Convert.ToInt32(versionArray[2]);

                if (versionNumber.Major == 2)
                {
                    if (versionNumber.Minor == 3)
                    {
                        if (versionNumber.Patch <= 7)
                        {
                            foreach (HeliConfig heliConfig in _config.heliConfigs)
                            {
                                heliConfig.outsideTime = 30;
                            }
                        }

                        versionNumber.Minor = 4;
                        versionNumber.Patch = 0;
                        versionNumber = new VersionNumber(2, 5, 0);
                    }
                    if (versionNumber.Minor == 5)
                    {
                        if (versionNumber.Patch <= 2)
                        {
                            OldPluginConfig oldPluginConfig = Config.ReadObject<OldPluginConfig>();
                            if (oldPluginConfig == null || oldPluginConfig.truckConfigs == null)
                                return false;

                            PrefabLootTableConfigs prefabConfigs = new PrefabLootTableConfigs
                            {
                                isEnable = false,
                                prefabs = new List<PrefabConfig>
                                {
                                    new PrefabConfig
                                    {
                                        minLootScale = 1,
                                        maxLootScale = 1,
                                        prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                    }
                                }
                            };
                            _config.notifyConfig = defaultConfig.notifyConfig;
                            _config.crateConfigs = defaultConfig.crateConfigs;
                            _config.guiConfig.offsetMinY = defaultConfig.guiConfig.offsetMinY;

                            _config.zoneConfig.isColoredBorder = false;
                            _config.zoneConfig.brightness = 5;
                            _config.zoneConfig.borderColor = 2;

                            foreach (EventConfig eventConfig in _config.eventConfigs)
                            {
                                eventConfig.eventTime = oldPluginConfig.eventTime;
                                eventConfig.maxTimeAfterWipe = -1;
                                eventConfig.maxGroundDamageDistance = (int)oldPluginConfig.maxDamageDistance;
                                eventConfig.zoneRadius = 70;

                                eventConfig.weaponToScaleDamageNpc = new Dictionary<string, float>
                                {
                                    ["grenade.beancan.deployed"] = 1f,
                                    ["grenade.f1.deployed"] = 1f,
                                    ["explosive.satchel.deployed"] = 1f,
                                    ["explosive.timed.deployed"] = 1f,
                                    ["rocket_hv"] = 1f,
                                    ["rocket_basic"] = 1f,
                                    ["40mm_grenade_he"] = 1f,
                                };
                            }
                            foreach (EventConfig eventConfig in defaultConfig.eventConfigs)
                            {
                                eventConfig.isAutoStart = false;
                                eventConfig.presetName += "_reforged";
                                _config.eventConfigs.Add(eventConfig);
                            }

                            foreach (NpcConfig npcConfig in _config.npcConfigs)
                            {
                                npcConfig.presetName = npcConfig.displayName.ToLower();
                                npcConfig.lootTableConfig.prefabConfigs = prefabConfigs;

                                foreach (NpcBelt npcBelt in npcConfig.beltItems)
                                    if (npcBelt.ammo == null)
                                        npcBelt.ammo = "";

                                foreach (LootItemConfig lootItemConfig in npcConfig.lootTableConfig.items)
                                    lootItemConfig.genomes = new List<string>();
                            }
                            foreach (NpcConfig npcConfig in defaultConfig.npcConfigs)
                                _config.npcConfigs.Add(npcConfig);

                            foreach (ModularCarConfig modularCarConfig in _config.modularCarConfigs)
                            {
                                modularCarConfig.damageScale = 1;
                                NpcConfig npcConfig = _config.npcConfigs.FirstOrDefault(x => x.displayName == modularCarConfig.npcPresetName);
                                modularCarConfig.npcPresetName = npcConfig.presetName;
                                modularCarConfig.additionalNpcs = new HashSet<NpcPoseConfig>();
                                modularCarConfig.crateLocations = new HashSet<PresetLocationConfig>();
                                modularCarConfig.turretLocations = new HashSet<PresetLocationConfig>();
                                modularCarConfig.samsiteLocations = new HashSet<PresetLocationConfig>();
                                modularCarConfig.numberOfNpc = 4;
                            }
                            foreach (OldTruckConfig truckConfig in oldPluginConfig.truckConfigs)
                            {
                                NpcConfig npcConfig = _config.npcConfigs.FirstOrDefault(x => x.displayName == truckConfig.npcPresetName);

                                ModularCarConfig modularCarConfig = new ModularCarConfig
                                {
                                    presetName = truckConfig.presetName,
                                    modules = truckConfig.modules,
                                    npcPresetName = npcConfig.presetName,
                                    crateLocations = new HashSet<PresetLocationConfig>(),
                                    additionalNpcs = new HashSet<NpcPoseConfig>(),
                                    damageScale = 1f,
                                    samsiteLocations = new HashSet<PresetLocationConfig>(),
                                    turretLocations = new HashSet<PresetLocationConfig>(),
                                    numberOfNpc = truckConfig.numberOfNpc
                                };

                                CrateConfig crateConfig = new CrateConfig
                                {
                                    presetName = $"chinooklockedcrate_{modularCarConfig.presetName}",
                                    prefabName = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab",
                                    hackTime = truckConfig.hackTime,
                                    lootTableConfig = truckConfig.lootTableConfig,
                                };

                                crateConfig.lootTableConfig.prefabConfigs = prefabConfigs;
                                _config.crateConfigs.Add(crateConfig);

                                PresetLocationConfig presetLocationConfig = new PresetLocationConfig
                                {
                                    presetName = crateConfig.presetName,
                                    position = truckConfig.crateLocation.position,
                                    rotation = truckConfig.crateLocation.rotation
                                };
                                modularCarConfig.crateLocations.Add(presetLocationConfig);

                                foreach (LootItemConfig lootItemConfig in crateConfig.lootTableConfig.items)
                                    lootItemConfig.genomes = new List<string>();

                                _config.modularCarConfigs.Add(modularCarConfig);
                            }
                            foreach (ModularCarConfig modularCarConfig in defaultConfig.modularCarConfigs)
                                _config.modularCarConfigs.Add(modularCarConfig);

                            foreach (SedanConfig sedanConfig in _config.sedanConfigs)
                            {
                                NpcConfig npcConfig = _config.npcConfigs.FirstOrDefault(x => x.displayName == sedanConfig.npcPresetName);
                                sedanConfig.npcPresetName = npcConfig.presetName;

                                sedanConfig.additionalNpcs = new HashSet<NpcPoseConfig>();
                                sedanConfig.crateLocations = new HashSet<PresetLocationConfig>();
                                sedanConfig.turretLocations = new HashSet<PresetLocationConfig>();
                                sedanConfig.samsiteLocations = new HashSet<PresetLocationConfig>();
                            }
                            foreach (SedanConfig sedanConfig in defaultConfig.sedanConfigs)
                                _config.sedanConfigs.Add(sedanConfig);

                            foreach (BradleyConfig bradleyConfig in _config.bradleyConfigs)
                            {
                                bradleyConfig.baseLootTableConfig.prefabConfigs = prefabConfigs;
                                NpcConfig npcConfig = _config.npcConfigs.FirstOrDefault(x => x.displayName == bradleyConfig.npcPresetName);
                                bradleyConfig.npcPresetName = npcConfig.presetName;

                                foreach (LootItemConfig lootItemConfig in bradleyConfig.baseLootTableConfig.items)
                                    lootItemConfig.genomes = new List<string>();

                                bradleyConfig.additionalNpcs = new HashSet<NpcPoseConfig>();
                                bradleyConfig.crateLocations = new HashSet<PresetLocationConfig>();
                                bradleyConfig.turretLocations = new HashSet<PresetLocationConfig>();
                                bradleyConfig.samsiteLocations = new HashSet<PresetLocationConfig>();
                                bradleyConfig.bradleyBuildingDamageScale = oldPluginConfig.bradleyBuildingDamageScale;
                                bradleyConfig.instCrateOpen = true;
                            }
                            foreach (BradleyConfig bradleyConfig in defaultConfig.bradleyConfigs)
                                _config.bradleyConfigs.Add(bradleyConfig);

                            foreach (HeliConfig heliConfig in _config.heliConfigs)
                            {
                                heliConfig.baseLootTableConfig.prefabConfigs = prefabConfigs;
                                heliConfig.immediatelyKill = true;
                                heliConfig.instCrateOpen = true;

                                foreach (LootItemConfig lootItemConfig in heliConfig.baseLootTableConfig.items)
                                    lootItemConfig.genomes = new List<string>();
                            }
                            foreach (HeliConfig heliConfig in defaultConfig.heliConfigs)
                                _config.heliConfigs.Add(heliConfig);

                            _config.tarvelingVendorConfigs = defaultConfig.tarvelingVendorConfigs;
                            _config.bikeConfigs = defaultConfig.bikeConfigs;

                            _config.turretConfigs = defaultConfig.turretConfigs;
                            _config.samsiteConfigs = defaultConfig.samsiteConfigs;

                            _config.mainConfig = new MainConfig();
                            _config.mainConfig.isAutoEvent = oldPluginConfig.isAutoEvent;
                            _config.mainConfig.minTimeBetweenEvents = oldPluginConfig.minTimeBetweenEvents;
                            _config.mainConfig.maxTimeBetweenEvents = oldPluginConfig.maxTimeBetweenEvents;
                            _config.mainConfig.preStartTime = oldPluginConfig.preStartTime;
                            _config.mainConfig.dontStopEventIfPlayerInZone = oldPluginConfig.dontStopEventIfPlayerInZone;
                            _config.mainConfig.enableStartStopLogs = oldPluginConfig.enableStartStopLogs;
                            _config.mainConfig.killEventAfterLoot = true;
                            _config.mainConfig.endAfterLootTime = oldPluginConfig.killTimeConvoyAfterLoot;
                            _config.mainConfig.fixedSchedule = defaultConfig.mainConfig.fixedSchedule;

                            _config.behaviorConfig = new BehaviorConfig
                            {
                                stopTime = oldPluginConfig.damamageStopTime,
                                agressiveTime = oldPluginConfig.isAggressive ? -1 : oldPluginConfig.damamageStopTime,
                            };
                            _config.lootConfig = new LootConfig
                            {
                                dropLoot = oldPluginConfig.dropCrateAfterKillTruck,
                                blockLootingByBradleys = oldPluginConfig.needKillCars,
                                blockLootingByNpcs = oldPluginConfig.needKillNpc,
                                blockLootingByMove = oldPluginConfig.needStopConvoy
                            };
                            _config.pathConfig = defaultConfig.pathConfig;
                            _config.pathConfig.regularPathConfig.isRingRoad = oldPluginConfig.rounRoadPriority;
                            _config.pathConfig.minRoadLength = oldPluginConfig.roadLength;

                            _config.supportedPluginsConfig = new SupportedPluginsConfig
                            {
                                economicsConfig = oldPluginConfig.economicsConfig,
                                betterNpcConfig = oldPluginConfig.betterNpcConfig,
                                discordMessagesConfig = oldPluginConfig.discordMessagesConfig,
                                guiAnnouncementsConfig = oldPluginConfig.guiAnnouncementsConfig,
                                notifyPluginConfig = oldPluginConfig.notifyPluginConfig,
                                pveMode = oldPluginConfig.pveMode,
                            };
                            _config.supportedPluginsConfig.economicsConfig.crates = defaultConfig.supportedPluginsConfig.economicsConfig.crates;
                            _config.supportedPluginsConfig.pveMode.scaleDamage.Add("Turret", 1f);
                        }
                        if (versionNumber.Patch <= 6)
                        {
                            foreach (NpcConfig npcConfig in _config.npcConfigs)
                            {
                                foreach (NpcBelt npcBelt in npcConfig.beltItems)
                                    if (npcBelt.ammo == "ammo.grenadelauncher.smoke")
                                        npcBelt.ammo = "40mm_grenade_smoke";
                            }

                            foreach (EventConfig eventConfig in _config.eventConfigs)
                                eventConfig.maxHeliDamageDistance = 300;
                        }
                        versionNumber = new VersionNumber(2, 6, 0);
                    }
                    if (versionNumber.Minor == 6)
                    {
                        if (versionNumber.Patch <= 1)
                        {
                            foreach (NpcConfig npcConfig in _config.npcConfigs)
                                npcConfig.lootTableConfig.alphaLootPresetName = string.Empty;

                            foreach (CrateConfig crateConfig in _config.crateConfigs)
                                crateConfig.lootTableConfig.alphaLootPresetName = string.Empty;
                        }

                        if (versionNumber.Patch <= 2)
                            _config.behaviorConfig.isPlayerTurretEnable = true;

                        if (versionNumber.Patch <= 7)
                        {
                            foreach (NpcConfig npcConfig in _config.npcConfigs)
                                foreach (NpcBelt npcBelt in npcConfig.beltItems)
                                    if (npcBelt.shortName == "rocket.launcher.dragon")
                                        npcBelt.shortName = "rocket.launcher";
                        }
                        versionNumber = new VersionNumber(2, 7, 0);
                    }

                    if (versionNumber.Minor == 7)
                    {
                        if (versionNumber.Patch <= 4)
                        {
                            _config.karuzaCarConfigs = defaultConfig.karuzaCarConfigs;
                        }
                        versionNumber = new VersionNumber(2, 8, 0);
                    }

                    if (versionNumber.Minor == 8)
                    {
                        if (versionNumber.Patch <= 0)
                        {
                            foreach (var vendorConfig in _config.tarvelingVendorConfigs)
                                vendorConfig.doorSkin = 934924536;

                            _config.behaviorConfig.isStopConvoyAgressive = true;
                        }
                    }
                }
                else
                {
                    PrintError("Delete the configuration file!");
                    NextTick(() => Server.Command($"o.unload {Name}"));
                    return false;
                }

                if (_config.mainConfig != null && _config.mainConfig.fixedSchedule == null)
                    _config.mainConfig.fixedSchedule = PluginConfig.DefaultConfig().mainConfig.fixedSchedule;

                _config.version = Version.ToString();
            }
            return true;
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        public class FixedScheduleWindow
        {
            [JsonProperty(en ? "Start" : "Начало")] public string start { get; set; }
            [JsonProperty(en ? "End" : "Конец")] public string end { get; set; }
        }

        public class FixedScheduleConfig
        {
            [JsonProperty(en ? "Enable fixed schedule [true/false]" : "Включить фиксированное расписание [true/false]")] public bool enabled { get; set; }
            [JsonProperty(en ? "UTC timezone offset" : "Часовой пояс UTC")] public int utcOffsetHours { get; set; }
            [JsonProperty(en ? "Windows" : "Окна")] public List<FixedScheduleWindow> windows { get; set; }
        }

        public class MainConfig
        {
            [JsonProperty(en ? "Enable automatic event holding [true/false]" : "Включить автоматическое проведение ивента [true/false]")] public bool isAutoEvent { get; set; }
            [JsonProperty(en ? "Minimum time between events [sec]" : "Минимальное вермя между ивентами [sec]")] public int minTimeBetweenEvents { get; set; }
            [JsonProperty(en ? "Maximum time between events [sec]" : "Максимальное вермя между ивентами [sec]")] public int maxTimeBetweenEvents { get; set; }
            [JsonProperty(en ? "Fixed schedule" : "Фиксированное расписание")] public FixedScheduleConfig fixedSchedule { get; set; }
            [JsonProperty(en ? "The time between receiving a chat notification and the start of the event [sec.]" : "Время до начала ивента после сообщения в чате [sec.]")] public int preStartTime { get; set; }
            [JsonProperty(en ? "Enable logging of the start and end of the event? [true/false]" : "Включить логирование начала и окончания ивента? [true/false]")] public bool enableStartStopLogs { get; set; }
            [JsonProperty(en ? "The event will not end if there are players nearby [true/false]" : "Ивент не будет заканчиваться, если рядом есть игроки [true/false]")] public bool dontStopEventIfPlayerInZone { get; set; }
            [JsonProperty(en ? "The turrets of the сonvoy will drop loot after destruction? [true/false]" : "Турели конвоя будут оставлять лут после уничтожения? [true/false]")] public bool isTurretDropWeapon { get; set; }
            [JsonProperty(en ? "Destroy the сonvoy after opening all the crates [true/false]" : "Уничтожать конвой после открытия всех ящиков [true/false]")] public bool killEventAfterLoot { get; set; }
            [JsonProperty(en ? "Time to destroy the сonvoy after opening all the crates [sec]" : "Время до уничтожения конвоя после открытия всех ящиков [sec]")] public int endAfterLootTime { get; set; }
        }

        public class BehaviorConfig
        {
            [JsonProperty(en ? "The time for which the convoy becomes aggressive after it has been attacked (-1 - is always aggressive)" : "Время на которое конвой становится агрессивным после того как был атакован (-1 - всегда агрессивен)")] public int agressiveTime { get; set; }
            [JsonProperty(en ? "The convoy will always remain aggressive while stopped [true/false]" : "Остановленный конвой будет всегда агрессивен [true/false]")] public bool isStopConvoyAgressive { get; set; }
            [JsonProperty(en ? "The duration of the stop after the attack" : "Длительность остановки после нападения")] public int stopTime { get; set; }
            [JsonProperty(en ? "Player turrets will attack NPCs if the convoy is stopped (false - They won't attack at all) [true/false]" : "Турели игроков будут атаковать остановившийся конвой (false - не будут атаковать вообще) [true/false]")] public bool isPlayerTurretEnable { get; set; }
        }

        public class LootConfig
        {
            [JsonProperty(en ? "When the car is destroyed, loot falls to the ground [true/false]" : "При уничтожении машины лут будет падать на землю [true/false]")] public bool dropLoot { get; set; }
            [JsonProperty(en ? "Percentage of loot loss when destroying a сar [0.0-1.0]" : "Процент потери лута при уничтожении машины [0.0-1.0]")] public float lootLossPercent { get; set; }
            [JsonProperty(en ? "Prohibit looting crates if the convoy is moving [true/false]" : "Запретить лутать ящики, если конвой движется [true/false]")] public bool blockLootingByMove { get; set; }
            [JsonProperty(en ? "Prohibit looting crates if NPCs are alive [true/false]" : "Запретить лутать ящики, если живы нпс [true/false]")] public bool blockLootingByNpcs { get; set; }
            [JsonProperty(en ? "Prohibit looting crates if Bradleys are alive [true/false]" : "Запретить лутать ящики, если живы Bradley [true/false]")] public bool blockLootingByBradleys { get; set; }
            [JsonProperty(en ? "Prohibit looting crates if Heli is alive [true/false]" : "Запретить лутать ящики, если жив вертолет [true/false]")] public bool blockLootingByHeli { get; set; }
        }

        public class PathConfig
        {
            [JsonProperty(en ? "Type of routes (0 - standard (fast generation), 1 - experimental (multiple roads are used), 2 - custom)" : "Тип маршрутов (0 - стандартный (быстрая генерация), 1 - экспериментальный (используется несколько дорог), 2 - кастомый)")] public int pathType { get; set; }
            [JsonProperty(en ? "Minimum road length" : "Минимальная длина дороги")] public int minRoadLength { get; set; }
            [JsonProperty(en ? "List of excluded roads (/convoyroadblock)" : "Список исключенных дорог (/convoyroadblock)")] public HashSet<int> blockRoads { get; set; }
            [JsonProperty(en ? "Setting up the standard route type" : "Настройка стандартного режима маршрутов")] public RegularPathConfig regularPathConfig { get; set; }
            [JsonProperty(en ? "Setting up a experimental type" : "Настройка экспериментального режима маршрутов")] public ComplexPathConfig complexPathConfig { get; set; }
            [JsonProperty(en ? "Setting up a custom route type" : "Настройка кастомного режима маршрутов")] public CustomPathConfig customPathConfig { get; set; }
        }

        public class RegularPathConfig
        {
            [JsonProperty(en ? "If there is a ring road on the map, then the convoy will always spawn here" : "Если на карте есть кольцевая дорога, то конвой будет двигаться только по ней")] public bool isRingRoad { get; set; }
        }

        public class ComplexPathConfig
        {
            [JsonProperty(en ? "Always choose the longest route? [true/false]" : "Всегда выбирать самый длинный маршрут? [true/false]")] public bool chooseLongestRoute { get; set; }
            [JsonProperty(en ? "The minimum number of roads in a complex route" : "Минимальное количество дорог в комплексом маршруте")] public int minRoadCount { get; set; }
        }

        public class CustomPathConfig
        {
            [JsonProperty(en ? "List of presets for custom routes" : "Список пресетов кастомных маршрутов")] public List<string> customRoutesPresets { get; set; }
        }

        public class EventConfig
        {
            [JsonProperty(en ? "Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Name displayed on the map (For custom marker)" : "Отображаемое на карте название (для кастомного маркера)")] public string displayName { get; set; }
            [JsonProperty(en ? "Automatic startup" : "Автоматический запуск")] public bool isAutoStart { get; set; }
            [JsonProperty(en ? "Probability of a preset [0.0-100.0]" : "Вероятность пресета [0.0-100.0]")] public float chance { get; set; }
            [JsonProperty(en ? "The minimum time after the server's wipe when this preset can be selected automatically [sec]" : "Минимальное время после вайпа сервера, когда этот пресет может быть выбран автоматически [sec]")] public int minTimeAfterWipe { get; set; }
            [JsonProperty(en ? "The maximum time after the server's wipe when this preset can be selected automatically [sec] (-1 - do not use this parameter)" : "Максимальное время после вайпа сервера, когда этот пресет может быть выбран автоматически [sec] (-1 - не использовать)")] public int maxTimeAfterWipe { get; set; }
            [JsonProperty(en ? "Event time" : "Время ивента")] public int eventTime { get; set; }
            [JsonProperty(en ? "Radius of the event zone" : "Радиус зоны ивента")] public float zoneRadius { get; set; }
            [JsonProperty(en ? "Maximum range for damage to Bradleys/NPCs/turrets (-1 - do not limit)" : "Максимальная дистанция для нанесения урона по турелям/нпс/бредли (-1 - не ограничивать)")] public int maxGroundDamageDistance { get; set; }
            [JsonProperty(en ? "Maximum range for damage to Heli when the convoy is stopped (-1 - do not limit)" : "Максимальная дистанция для нанесения урона по вертолету, когда конвой остановлен (-1 - не ограничивать)")] public int maxHeliDamageDistance { get; set; }
            [JsonProperty(en ? "Order of vehicles" : "Порядок транспортных средств")] public List<string> vehiclesOrder { get; set; }
            [JsonProperty(en ? "Enable the helicopter" : "Включить вертолет")] public bool isHeli { get; set; }
            [JsonProperty(en ? "Heli preset" : "Пресет вертолета")] public string heliPreset { get; set; }
            [JsonProperty(en ? "NPC damage multipliers depending on the attacker's weapon" : "Множители урона по NPC в зависимости от оружия атакующего")] public Dictionary<string, float> weaponToScaleDamageNpc { get; set; }
        }

        public class TravellingVendorConfig : VehicleConfig
        {
            [JsonProperty(en ? "Preset Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Delete the vendor's map marker?" : "Удалять маркер торговца? [true/false]")] public bool deleteMapMarker { get; set; }
            [JsonProperty(en ? "Add a lock on the Loot Door? [true/false]" : "Добавить замок на дверь к луту? [true/false]")] public bool isLocked { get; set; }
            [JsonProperty(en ? "Loot Door Health" : "Здоровье двери к луту")] public float doorHealth { get; set; }
            [JsonProperty(en ? "Loot door SkinID" : "SkinID двери к луту")] public ulong doorSkin { get; set; }
        }

        public class ModularCarConfig : VehicleConfig
        {
            [JsonProperty(en ? "Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Scale damage" : "Множитель урона")] public float damageScale { get; set; }
            [JsonProperty(en ? "Modules" : "Модули")] public List<string> modules { get; set; }
        }

        public class BradleyConfig : VehicleConfig
        {
            [JsonProperty(en ? "Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty("HP")] public float hp { get; set; }
            [JsonProperty(en ? "Damage multiplier from Bradley to buildings (-1 - do not change)" : "Множитель урона от бредли по постройкам (-1 - не изменять)")] public float bradleyBuildingDamageScale { get; set; }
            [JsonProperty(en ? "The viewing distance" : "Дальность обзора")] public float viewDistance { get; set; }
            [JsonProperty(en ? "Radius of search" : "Радиус поиска")] public float searchDistance { get; set; }
            [JsonProperty(en ? "The multiplier of Machine-gun aim cone" : "Множитель разброса пулемёта")] public float coaxAimCone { get; set; }
            [JsonProperty(en ? "The multiplier of Machine-gun fire rate" : "Множитель скорострельности пулемёта")] public float coaxFireRate { get; set; }
            [JsonProperty(en ? "Amount of Machine-gun burst shots" : "Кол-во выстрелов очереди пулемёта")] public int coaxBurstLength { get; set; }
            [JsonProperty(en ? "The time between shots of the main gun [sec.]" : "Время между залпами основного орудия [sec.]")] public float nextFireTime { get; set; }
            [JsonProperty(en ? "The time between shots of the main gun in a fire rate [sec.]" : "Время между выстрелами основного орудия в залпе [sec.]")] public float topTurretFireRate { get; set; }
            [JsonProperty(en ? "Numbers of crates" : "Кол-во ящиков после уничтожения")] public int countCrates { get; set; }
            [JsonProperty(en ? "Open the crates immediately after spawn" : "Открывать ящики сразу после спавна")] public bool instCrateOpen { get; set; }
            [JsonProperty(en ? "Own loot table" : "Собственная таблица лута")] public BaseLootTableConfig baseLootTableConfig { get; set; }
        }

        public class SedanConfig : VehicleConfig
        {
            [JsonProperty(en ? "Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty("HP")] public float hp { get; set; }
        }

        public class KaruzaCarConfig : VehicleConfig
        {
            [JsonProperty(en ? "Preset Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Prefab Name" : "Название префаба")] public string prefabName { get; set; }
        }

        public class BikeConfig : VehicleConfig
        {
            [JsonProperty(en ? "Preset Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Prefab Name" : "Название префаба")] public string prefabName { get; set; }
            [JsonProperty("HP")] public float hp { get; set; }
        }

        public class VehicleConfig
        {
            [JsonProperty(en ? "NPC preset" : "Пресет НПС", Order = 100)] public string npcPresetName { get; set; }
            [JsonProperty(en ? "Number of NPCs" : "Количество НПС", Order = 100)] public int numberOfNpc { get; set; }
            [JsonProperty(en ? "Locations of additional NPCs" : "Расположения дополнительных нпс", Order = 101)] public HashSet<NpcPoseConfig> additionalNpcs { get; set; }
            [JsonProperty(en ? "Crates" : "Крейты", Order = 102)] public HashSet<PresetLocationConfig> crateLocations { get; set; }
            [JsonProperty(en ? "Turrets" : "Турели", Order = 103)] public HashSet<PresetLocationConfig> turretLocations { get; set; }
            [JsonProperty(en ? "SamSites" : "ПВО", Order = 104)] public HashSet<PresetLocationConfig> samsiteLocations { get; set; }
        }

        public class NpcPoseConfig : LocationConfig
        {
            [JsonProperty(en ? "Enable spawn?" : "Активировать спавн?")] public bool isEnable { get; set; }
            [JsonProperty(en ? "Seat prefab" : "Префаб сидения")] public string seatPrefab { get; set; }
            [JsonProperty(en ? "Will the NPC dismount when the vehicle stops?" : "Будет ли NPC спешиваться при остановке транспортного средства?")] public bool isDismount { get; set; }
            [JsonProperty(en ? "NPC preset (Empty - as in a vehicle)" : "Пресет НПС (Empty - оставить как в транспортном средстве)")] public string npcPresetName { get; set; }
        }

        public class SamSiteConfig
        {
            [JsonProperty(en ? "Preset Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Health" : "Кол-во ХП")] public float hp { get; set; }
            [JsonProperty(en ? "Number of ammo" : "Кол-во патронов")] public int countAmmo { get; set; }
        }

        public class TurretConfig
        {
            [JsonProperty(en ? "Preset Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Health" : "Кол-во ХП")] public float hp { get; set; }
            [JsonProperty(en ? "Weapon ShortName" : "ShortName оружия")] public string shortNameWeapon { get; set; }
            [JsonProperty(en ? "Ammo ShortName" : "ShortName патронов")] public string shortNameAmmo { get; set; }
            [JsonProperty(en ? "Number of ammo" : "Кол-во патронов")] public int countAmmo { get; set; }
            [JsonProperty(en ? "Target detection range (0 - do not change)" : "Дальность обнаружения цели (0 - не изменять)")] public float targetDetectionRange { get; set; }
            [JsonProperty(en ? "Target loss range (0 - do not change)" : "Дальность потери цели (0 - не изменять)")] public float targetLossRange { get; set; }
        }

        public class CrateConfig
        {
            [JsonProperty(en ? "Preset Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty("Prefab")] public string prefabName { get; set; }
            [JsonProperty(en ? "SkinID (0 - default)" : "Скин")] public ulong skin { get; set; }
            [JsonProperty(en ? "Time to unlock the crates (LockedCrate) [sec.]" : "Время до открытия заблокированного ящика (LockedCrate) [sec.]")] public float hackTime { get; set; }
            [JsonProperty(en ? "Own loot table" : "Собственная таблица предметов")] public LootTableConfig lootTableConfig { get; set; }
        }

        public class PresetLocationConfig : LocationConfig
        {
            [JsonProperty(en ? "Preset name" : "Название пресета")] public string presetName { get; set; }
        }

        public class LocationConfig
        {
            [JsonProperty(en ? "Position" : "Позиция")] public string position { get; set; }
            [JsonProperty(en ? "Rotation" : "Вращение")] public string rotation { get; set; }
        }

        public class HeliConfig
        {
            [JsonProperty(en ? "Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty("HP")] public float hp { get; set; }
            [JsonProperty(en ? "HP of the main rotor" : "HP главного винта")] public float mainRotorHealth { get; set; }
            [JsonProperty(en ? "HP of tail rotor" : "HP хвостового винта")] public float rearRotorHealth { get; set; }
            [JsonProperty(en ? "Numbers of crates" : "Количество ящиков")] public int cratesAmount { get; set; }
            [JsonProperty(en ? "Flying height" : "Высота полета")] public float height { get; set; }
            [JsonProperty(en ? "Bullet speed" : "Скорость пуль")] public float bulletSpeed { get; set; }
            [JsonProperty(en ? "Bullet Damage" : "Урон пуль")] public float bulletDamage { get; set; }
            [JsonProperty(en ? "The distance to which the helicopter can move away from the convoy" : "Дистанция, на которую вертолет может отдаляться от конвоя")] public float distance { get; set; }
            [JsonProperty(en ? "The time for which the helicopter can leave the convoy to attack the target [sec.]" : "Время, на которое верталет может покидать конвой для атаки цели [sec.]")] public float outsideTime { get; set; }
            [JsonProperty(en ? "The helicopter will not aim for the nearest monument at death [true/false]" : "Вертолет не будет стремиться к ближайшему монументу при смерти [true/false]")] public bool immediatelyKill { get; set; }
            [JsonProperty(en ? "Open the crates immediately after spawn" : "Открывать ящики сразу после спавна")] public bool instCrateOpen { get; set; }
            [JsonProperty(en ? "Own loot table" : "Собственная таблица лута")] public BaseLootTableConfig baseLootTableConfig { get; set; }
        }

        public class NpcConfig
        {
            [JsonProperty(en ? "Preset Name" : "Название пресета")] public string presetName { get; set; }
            [JsonProperty(en ? "Name" : "Название")] public string displayName { get; set; }
            [JsonProperty(en ? "Health" : "Кол-во ХП")] public float health { get; set; }
            [JsonProperty(en ? "Kit" : "Kit")] public string kit { get; set; }
            [JsonProperty(en ? "Wear items" : "Одежда")] public List<NpcWear> wearItems { get; set; }
            [JsonProperty(en ? "Belt items" : "Быстрые слоты")] public List<NpcBelt> beltItems { get; set; }
            [JsonProperty(en ? "Speed" : "Скорость")] public float speed { get; set; }
            [JsonProperty(en ? "Roam Range" : "Дальность патрулирования местности")] public float roamRange { get; set; }
            [JsonProperty(en ? "Chase Range" : "Дальность погони за целью")] public float chaseRange { get; set; }
            [JsonProperty(en ? "Attack Range Multiplier" : "Множитель радиуса атаки")] public float attackRangeMultiplier { get; set; }
            [JsonProperty(en ? "Sense Range" : "Радиус обнаружения цели")] public float senseRange { get; set; }
            [JsonProperty(en ? "Memory duration [sec.]" : "Длительность памяти цели [sec.]")] public float memoryDuration { get; set; }
            [JsonProperty(en ? "Scale damage" : "Множитель урона")] public float damageScale { get; set; }
            [JsonProperty(en ? "Aim Cone Scale" : "Множитель разброса")] public float aimConeScale { get; set; }
            [JsonProperty(en ? "Detect the target only in the NPC's viewing vision cone?" : "Обнаруживать цель только в углу обзора NPC? [true/false]")] public bool checkVisionCone { get; set; }
            [JsonProperty(en ? "Vision Cone" : "Угол обзора")] public float visionCone { get; set; }
            [JsonProperty(en ? "Turret damage scale" : "Множитель урона от турелей")] public float turretDamageScale { get; set; }
            [JsonProperty(en ? "Disable radio effects? [true/false]" : "Отключать эффекты рации? [true/false]")] public bool disableRadio { get; set; }
            [JsonProperty(en ? "Should remove the corpse?" : "Удалять труп?")] public bool deleteCorpse { get; set; }
            [JsonProperty(en ? "Own loot table" : "Собственная таблица лута")] public LootTableConfig lootTableConfig { get; set; }
        }

        public class NpcWear
        {
            [JsonProperty(en ? "ShortName" : "ShortName")] public string shortName { get; set; }
            [JsonProperty(en ? "skinID (0 - default)" : "SkinID (0 - default)")] public ulong skinID { get; set; }
        }

        public class NpcBelt
        {
            [JsonProperty(en ? "ShortName" : "ShortName")] public string shortName { get; set; }
            [JsonProperty(en ? "Amount" : "Кол-во")] public int amount { get; set; }
            [JsonProperty(en ? "skinID (0 - default)" : "SkinID (0 - default)")] public ulong skinID { get; set; }
            [JsonProperty(en ? "Mods" : "Модификации на оружие")] public List<string> mods { get; set; }
            [JsonProperty(en ? "Ammo" : "Патроны")] public string ammo { get; set; }
        }

        public class LootTableConfig : BaseLootTableConfig
        {
            [JsonProperty(en ? "Allow the AlphaLoot plugin to spawn items in this crate" : "Разрешить плагину AlphaLoot спавнить предметы в этом ящике")] public bool isAlphaLoot { get; set; }
            [JsonProperty(en ? "The name of the loot preset for AlphaLoot" : "Название пресета лута AlphaLoot")] public string alphaLootPresetName { get; set; }
            [JsonProperty(en ? "Allow the CustomLoot plugin to spawn items in this crate" : "Разрешить плагину CustomLoot спавнить предметы в этом ящике")] public bool isCustomLoot { get; set; }
            [JsonProperty(en ? "Allow the Loot Table Stacksize GUI plugin to spawn items in this crate" : "Разрешить плагину Loot Table Stacksize GUI спавнить предметы в этом ящике")] public bool isLootTablePLugin { get; set; }
        }

        public class BaseLootTableConfig
        {
            [JsonProperty(en ? "Clear the standard content of the crate" : "Отчистить стандартное содержимое крейта")] public bool clearDefaultItemList { get; set; }
            [JsonProperty(en ? "Setting up loot from the loot table" : "Настройка лута из лутовой таблицы")] public PrefabLootTableConfigs prefabConfigs { get; set; }
            [JsonProperty(en ? "Enable spawn of items from the list" : "Включить спавн предметов из списка")] public bool isRandomItemsEnable { get; set; }
            [JsonProperty(en ? "Minimum numbers of items" : "Минимальное кол-во элементов")] public int minItemsAmount { get; set; }
            [JsonProperty(en ? "Maximum numbers of items" : "Максимальное кол-во элементов")] public int maxItemsAmount { get; set; }
            [JsonProperty(en ? "List of items" : "Список предметов")] public List<LootItemConfig> items { get; set; }
        }

        public class PrefabLootTableConfigs
        {
            [JsonProperty(en ? "Enable spawn loot from prefabs" : "Включить спавн лута из префабов")] public bool isEnable { get; set; }
            [JsonProperty(en ? "List of prefabs (one is randomly selected)" : "Список префабов (выбирается один рандомно)")] public List<PrefabConfig> prefabs { get; set; }
        }

        public class PrefabConfig
        {
            [JsonProperty(en ? "Prefab displayName" : "Название префаба")] public string prefabName { get; set; }
            [JsonProperty(en ? "Minimum Loot multiplier" : "Минимальный множитель лута")] public int minLootScale { get; set; }
            [JsonProperty(en ? "Maximum Loot multiplier" : "Максимальный множитель лута")] public int maxLootScale { get; set; }
        }

        public class LootItemConfig
        {
            [JsonProperty("ShortName")] public string shortname { get; set; }
            [JsonProperty(en ? "Minimum" : "Минимальное кол-во")] public int minAmount { get; set; }
            [JsonProperty(en ? "Maximum" : "Максимальное кол-во")] public int maxAmount { get; set; }
            [JsonProperty(en ? "Chance [0.0-100.0]" : "Шанс выпадения предмета [0.0-100.0]")] public float chance { get; set; }
            [JsonProperty(en ? "Is this a blueprint? [true/false]" : "Это чертеж? [true/false]")] public bool isBlueprint { get; set; }
            [JsonProperty(en ? "SkinID (0 - default)" : "SkinID (0 - default)")] public ulong skin { get; set; }
            [JsonProperty(en ? "Name (empty - default)" : "Название (empty - default)")] public string name { get; set; }
            [JsonProperty(en ? "List of genomes" : "Список геномов")] public List<string> genomes { get; set; }
        }

        public class MarkerConfig
        {
            [JsonProperty(en ? "Do you use the Marker? [true/false]" : "Использовать ли маркер? [true/false]")] public bool enable { get; set; }
            [JsonProperty(en ? "Use a shop marker? [true/false]" : "Добавить маркер магазина? [true/false]")] public bool useShopMarker { get; set; }
            [JsonProperty(en ? "Use a circular marker? [true/false]" : "Добавить круговой маркер? [true/false]")] public bool useRingMarker { get; set; }
            [JsonProperty(en ? "Radius" : "Радиус")] public float radius { get; set; }
            [JsonProperty(en ? "Alpha" : "Прозрачность")] public float alpha { get; set; }
            [JsonProperty(en ? "Marker color" : "Цвет маркера")] public ColorConfig color1 { get; set; }
            [JsonProperty(en ? "Outline color" : "Цвет контура")] public ColorConfig color2 { get; set; }
        }

        public class ColorConfig
        {
            [JsonProperty("r")] public float r { get; set; }
            [JsonProperty("g")] public float g { get; set; }
            [JsonProperty("b")] public float b { get; set; }
        }

        public class ZoneConfig
        {
            [JsonProperty(en ? "Create a PVP zone in the convoy stop zone? (only for those who use the TruePVE plugin)[true/false]" : "Создавать зону PVP в зоне проведения ивента? (только для тех, кто использует плагин TruePVE) [true/false]")] public bool isPVPZone { get; set; }
            [JsonProperty(en ? "Use the dome? [true/false]" : "Использовать ли купол? [true/false]")] public bool isDome { get; set; }
            [JsonProperty(en ? "Darkening the dome" : "Затемнение купола")] public int darkening { get; set; }
            [JsonProperty(en ? "Use a colored border? [true/false]" : "Использовать цветную границу? [true/false]")] public bool isColoredBorder { get; set; }
            [JsonProperty(en ? "Border color (0 - blue, 1 - green, 2 - purple, 3 - red)" : "Цвет границы (0 - синий, 1 - зеленый, 2 - фиолетовый, 3 - красный)")] public int borderColor { get; set; }
            [JsonProperty(en ? "Brightness of the color border" : "Яркость цветной границы")] public int brightness { get; set; }
        }

        public class NotifyConfig
        {
            [JsonProperty(en ? "Use a chat? [true/false]" : "Использовать ли чат? [true/false]")] public bool isChatEnable { get; set; }
            [JsonProperty(en ? "The time until the end of the event, when a message is displayed about the time until the end of the event [sec]" : "Время до конца ивента, когда выводится сообщение о сокром окончании ивента [sec]")] public HashSet<int> timeNotifications { get; set; }
            [JsonProperty(en ? "Facepunch Game Tips setting" : "Настройка сообщений Facepunch Game Tip")] public GameTipConfig gameTipConfig { get; set; }
        }

        public class GameTipConfig
        {
            [JsonProperty(en ? "Use Facepunch Game Tips (notification bar above hotbar)? [true/false]" : "Использовать ли Facepunch Game Tip (оповещения над слотами быстрого доступа игрока)? [true/false]")] public bool isEnabled { get; set; }
            [JsonProperty(en ? "Style (0 - Blue Normal, 1 - Red Normal, 2 - Blue Long, 3 - Blue Short, 4 - Server Event)" : "Стиль (0 - Blue Normal, 1 - Red Normal, 2 - Blue Long, 3 - Blue Short, 4 - Server Event)")] public int style { get; set; }
        }

        public class GUIConfig
        {
            [JsonProperty(en ? "Use the Countdown GUI? [true/false]" : "Использовать ли GUI обратного отсчета? [true/false]")] public bool isEnable { get; set; }
            [JsonProperty(en ? "Vertical offset" : "Смещение по вертикали")] public int offsetMinY { get; set; }
        }

        public class SupportedPluginsConfig
        {
            [JsonProperty(en ? "PVE Mode Setting" : "Настройка PVE Mode")] public PveModeConfig pveMode { get; set; }
            [JsonProperty(en ? "Economy Setting" : "Настройка экономики")] public EconomyConfig economicsConfig { get; set; }
            [JsonProperty(en ? "BetterNpc Setting" : "Настройка плагина BetterNpc")] public BetterNpcConfig betterNpcConfig { get; set; }
            [JsonProperty(en ? "GUI Announcements setting" : "Настройка GUI Announcements")] public GUIAnnouncementsConfig guiAnnouncementsConfig { get; set; }
            [JsonProperty(en ? "Notify setting" : "Настройка Notify")] public NotifyPluginConfig notifyPluginConfig { get; set; }
            [JsonProperty(en ? "DiscordMessages setting" : "Настройка DiscordMessages")] public DiscordConfig discordMessagesConfig { get; set; }
        }

        public class PveModeConfig
        {
            [JsonProperty(en ? "Use the PVE mode of the plugin? [true/false]" : "Использовать PVE режим работы плагина? [true/false]")] public bool enable { get; set; }

            [JsonProperty(en ? "The owner will immediately be the one who stopped the convoy" : "Владельцем сразу будет становиться тот, кто остановил конвой")] public bool ownerIsStopper { get; set; }
            [JsonProperty(en ? "If a player has a cooldown and the event has NO OWNERS, then he will not be able to interact with the event? [true/false]" : "Если у игрока кулдаун, а у ивента НЕТ ВЛАДЕЛЬЦЕВ, то он не сможет взаимодействовать с ивентом? [true/false]")] public bool noInterractIfCooldownAndNoOwners { get; set; }
            [JsonProperty(en ? "If a player has a cooldown, and the event HAS AN OWNER, then he will not be able to interact with the event, even if he is on a team with the owner? [true/false]" : "Если у игрока кулдаун, а у ивента ЕСТЬ ВЛАДЕЛЕЦ, то он не сможет взаимодействовать с ивентом, даже если находится в команде с владельцем? [true/false]")] public bool noDealDamageIfCooldownAndTeamOwner { get; set; }
            [JsonProperty(en ? "Allow only the owner or his teammates to loot crates? [true/false]" : "Разрешить лутать ящики только владельцу или его тиммейтам? [true/false]")] public bool canLootOnlyOwner { get; set; }

            [JsonProperty(en ? "Display the name of the event owner on a marker on the map? [true/false]" : "Отображать имя владелца ивента на маркере на карте? [true/false]")] public bool showEventOwnerNameOnMap { get; set; }
            [JsonProperty(en ? "The amount of damage that the player has to do to become the Event Owner" : "Кол-во урона, которое должен нанести игрок, чтобы стать владельцем ивента")] public float damage { get; set; }
            [JsonProperty(en ? "Damage coefficients for calculate to become the Event Owner." : "Коэффициенты урона для подсчета, чтобы стать владельцем события.")] public Dictionary<string, float> scaleDamage { get; set; }
            [JsonProperty(en ? "Can the non-owner of the event loot the crates? [true/false]" : "Может ли не владелец ивента грабить ящики? [true/false]")] public bool lootCrate { get; set; }
            [JsonProperty(en ? "Can the non-owner of the event hack locked crates? [true/false]" : "Может ли не владелец ивента взламывать заблокированные ящики? [true/false]")] public bool hackCrate { get; set; }
            [JsonProperty(en ? "Can the non-owner of the event loot NPC corpses? [true/false]" : "Может ли не владелец ивента грабить трупы NPC? [true/false]")] public bool lootNpc { get; set; }
            [JsonProperty(en ? "Can an Npc attack a non-owner of the event? [true/false]" : "Может ли Npc атаковать не владельца ивента? [true/false]")] public bool targetNpc { get; set; }
            [JsonProperty(en ? "Can Bradley attack a non-owner of the event? [true/false]" : "Может ли Bradley атаковать не владельца ивента? [true/false]")] public bool targetTank { get; set; }
            [JsonProperty(en ? "Can Turret attack a non-owner of the event? [true/false]" : "Может ли Турель атаковать не владельца ивента? [true/false]")] public bool targetTurret { get; set; }
            [JsonProperty(en ? "Can Helicopter attack a non-owner of the event? [true/false]" : "Может ли Вертолет атаковать не владельца ивента? [true/false]")] public bool targetHeli { get; set; }
            [JsonProperty(en ? "Can the non-owner of the event do damage to Bradley? [true/false]" : "Может ли не владелец ивента наносить урон по Bradley? [true/false]")] public bool damageTank { get; set; }
            [JsonProperty(en ? "Can the non-owner of the event deal damage to the NPC? [true/false]" : "Может ли не владелец ивента наносить урон по NPC? [true/false]")] public bool damageNpc { get; set; }
            [JsonProperty(en ? "Can the non-owner of the event do damage to Helicopter? [true/false]" : "Может ли не владелец ивента наносить урон по Вертолету? [true/false]")] public bool damageHeli { get; set; }
            [JsonProperty(en ? "Can the non-owner of the event do damage to Turret? [true/false]" : "Может ли не владелец ивента наносить урон по Турелям? [true/false]")] public bool damageTurret { get; set; }
            [JsonProperty(en ? "Allow the non-owner of the event to enter the event zone? [true/false]" : "Разрешать входить внутрь зоны ивента не владельцу ивента? [true/false]")] public bool canEnter { get; set; }
            [JsonProperty(en ? "Allow a player who has an active cooldown of the Event Owner to enter the event zone? [true/false]" : "Разрешать входить внутрь зоны ивента игроку, у которого активен кулдаун на получение статуса владельца ивента? [true/false]")] public bool canEnterCooldownPlayer { get; set; }
            [JsonProperty(en ? "The time that the Event Owner may not be inside the event zone [sec.]" : "Время, которое владелец ивента может не находиться внутри зоны ивента [сек.]")] public int timeExitOwner { get; set; }
            [JsonProperty(en ? "The time until the end of Event Owner status when it is necessary to warn the player [sec.]" : "Время таймера до окончания действия статуса владельца ивента, когда необходимо предупредить игрока [сек.]")] public int alertTime { get; set; }
            [JsonProperty(en ? "Prevent the actions of the RestoreUponDeath plugin in the event zone? [true/false]" : "Запрещать работу плагина RestoreUponDeath в зоне действия ивента? [true/false]")] public bool restoreUponDeath { get; set; }
            [JsonProperty(en ? "The time that the player can`t become the Event Owner, after the end of the event and the player was its owner [sec.]" : "Время, которое игрок не сможет стать владельцем ивента, после того как ивент окончен и игрок был его владельцем [sec.]")] public double cooldown { get; set; }
        }

        public class EconomyConfig
        {
            [JsonProperty(en ? "Enable economy" : "Включить экономику?")] public bool enable { get; set; }
            [JsonProperty(en ? "Which economy plugins do you want to use? (Economics, Server Rewards, IQEconomic)" : "Какие плагины экономики вы хотите использовать? (Economics, Server Rewards, IQEconomic)")] public HashSet<string> plugins { get; set; }
            [JsonProperty(en ? "The minimum value that a player must collect to get points for the economy" : "Минимальное значение, которое игрок должен заработать, чтобы получить баллы за экономику")] public double minEconomyPiont { get; set; }
            [JsonProperty(en ? "The minimum value that a winner must collect to make the commands work" : "Минимальное значение, которое победитель должен заработать, чтобы сработали команды")] public double minCommandPoint { get; set; }
            [JsonProperty(en ? "Looting of crates" : "Ограбление ящиков")] public Dictionary<string, double> crates { get; set; }
            [JsonProperty(en ? "Killing an NPC" : "Убийство NPC")] public double npcPoint { get; set; }
            [JsonProperty(en ? "Killing an Bradley" : "Уничтожение Bradley")] public double bradleyPoint { get; set; }
            [JsonProperty(en ? "Killing an Heli" : "Уничтожение вертолета")] public double heliPoint { get; set; }
            [JsonProperty(en ? "Killing an sedan" : "Уничтожение седана")] public double sedanPoint { get; set; }
            [JsonProperty(en ? "Killing an mpdular Car" : "Уничтожение модульной машины")] public double modularCarPoint { get; set; }
            [JsonProperty(en ? "Killing a turret" : "Уничтожение турели")] public double turretPoint { get; set; }
            [JsonProperty(en ? "Killing a Samsite" : "Уничтожение Samsite")] public double samsitePoint { get; set; }
            [JsonProperty(en ? "Hacking a locked crate" : "Взлом заблокированного ящика")] public double lockedCratePoint { get; set; }
            [JsonProperty(en ? "List of commands that are executed in the console at the end of the event ({steamid} - the player who collected the highest number of points)" : "Список команд, которые выполняются в консоли по окончанию ивента ({steamid} - игрок, который набрал наибольшее кол-во баллов)")] public HashSet<string> commands { get; set; }
        }

        public class GUIAnnouncementsConfig
        {
            [JsonProperty(en ? "Do you use the GUI Announcements? [true/false]" : "Использовать ли GUI Announcements? [true/false]")] public bool isEnabled { get; set; }
            [JsonProperty(en ? "Banner color" : "Цвет баннера")] public string bannerColor { get; set; }
            [JsonProperty(en ? "Text color" : "Цвет текста")] public string textColor { get; set; }
            [JsonProperty(en ? "Adjust Vertical Position" : "Отступ от верхнего края")] public float apiAdjustVPosition { get; set; }
        }

        public class NotifyPluginConfig
        {
            [JsonProperty(en ? "Do you use the Notify? [true/false]" : "Использовать ли Notify? [true/false]")] public bool isEnabled { get; set; }
            [JsonProperty(en ? "Type" : "Тип")] public int type { get; set; }
        }

        public class DiscordConfig
        {
            [JsonProperty(en ? "Do you use the Discord? [true/false]" : "Использовать ли Discord? [true/false]")] public bool isEnabled { get; set; }
            [JsonProperty("Webhook URL")] public string webhookUrl;
            [JsonProperty(en ? "Embed Color (DECIMAL)" : "Цвет полосы (DECIMAL)")] public int embedColor { get; set; }
            [JsonProperty(en ? "Keys of required messages" : "Ключи необходимых сообщений")] public HashSet<string> keys { get; set; }
        }

        public class BetterNpcConfig
        {
            [JsonProperty(en ? "Allow Npc spawn after destroying Bradley" : "Разрешить спавн Npc после уничтожения Бредли")] public bool bradleyNpc { get; set; }
            [JsonProperty(en ? "Allow Npc spawn after destroying Heli" : "Разрешить спавн Npc после уничтожения Вертолета")] public bool heliNpc { get; set; }
        }

        class OldPluginConfig : PluginConfig
        {
            [JsonProperty(en ? "Enable automatic event holding [true/false]" : "Включить автоматическое проведение ивента [true/false]")] public bool isAutoEvent { get; set; }
            [JsonProperty(en ? "Minimum time between events [sec.]" : "Минимальное время между ивентами [sec.]")] public int minTimeBetweenEvents { get; set; }
            [JsonProperty(en ? "Maximum time between events [sec.]" : "Максимальное время между ивентами [sec.]")] public int maxTimeBetweenEvents { get; set; }
            [JsonProperty(en ? "Truck Configurations" : "Кофигурации грузовиков")] public HashSet<OldTruckConfig> truckConfigs { get; set; }
            [JsonProperty(en ? "Duration of the event [sec.]" : "Длительность ивента [sec.]")] public int eventTime { get; set; }
            [JsonProperty(en ? "Time before the starting of the event after receiving a chat message [sec.]" : "Время до начала ивента после сообщения в чате [sec.]")] public int preStartTime { get; set; }
            [JsonProperty(en ? "The convoy attacks first [true/false]" : "Конвой атакует первым [true/false]")] public bool isAggressive { get; set; }
            [JsonProperty(en ? "It is necessary to stop the convoy to open the crate" : "Необходимо остановить конвой, чтобы открыть ящик")] public bool needStopConvoy { get; set; }
            [JsonProperty(en ? "It is necessary to kill all vehicles to open the crate" : "Необходимо убить все машины, чтобы открыть ящик")] public bool needKillCars { get; set; }
            [JsonProperty(en ? "It is necessary to kill all NPC to open the crate" : "Необходимо убить всех NPC, чтобы открыть ящик")] public bool needKillNpc { get; set; }
            [JsonProperty(en ? "The time for which the convoy stops moving after receiving damage [sec.]" : "Время, на которое останавливается конвой, после получения урона [sec.]")] public int damamageStopTime { get; set; }
            [JsonProperty(en ? "If there is a ring road on the map, then the event will be held on it" : "Если на карте есть кольцевая дорога, то ивент будет проводиться на ней")] public bool rounRoadPriority { get; set; }
            [JsonProperty(en ? "The minimum length of the road on which the event can be held" : "Минимальное длина дороги, на которой может проводиться ивент")] public int roadLength { get; set; }
            [JsonProperty(en ? "When the truck is destroyed, the crate will remain [true/false]" : "При уничтножении грузовика ящик будет падать на землю [true/false]")] public bool dropCrateAfterKillTruck { get; set; }
            [JsonProperty(en ? "Maximum distance for dealing damage to the convoy" : "Максимальное расстояние для нанесения урона конвою")] public float maxDamageDistance { get; set; }
            [JsonProperty(en ? "Time to destroy the convoy after opening all the crates [sec]" : "Время до уничтожения конвоя после открытия всех ящиков [sec]")] public int killTimeConvoyAfterLoot { get; set; }
            [JsonProperty(en ? "The event will not end if there are players nearby [true/false]" : "Ивент не будет заканчиваться, если рядом есть игроки [true/false]")] public bool dontStopEventIfPlayerInZone { get; set; }
            [JsonProperty(en ? "Enable logging of the start and end of the event? [true/false]" : "Включить логирование начала и окончания ивента? [true/false]")] public bool enableStartStopLogs { get; set; }
            [JsonProperty(en ? "Damage multiplier from Bradley to buildings (0 - do not change)" : "Множитель урона от бредли по постройкам (0 - не изменять)")] public float bradleyBuildingDamageScale { get; set; }
            [JsonProperty(en ? "Discord setting (only for DiscordMessages)" : "Настройка оповещений в Discord (только для DiscordMessages)")] public DiscordConfig discordMessagesConfig { get; set; }
            [JsonProperty(en ? "Notify setting" : "Настройка Notify")] public NotifyPluginConfig notifyPluginConfig { get; set; }
            [JsonProperty(en ? "GUI Announcements setting" : "Настройка GUI Announcements")] public GUIAnnouncementsConfig guiAnnouncementsConfig { get; set; }
            [JsonProperty(en ? "BetterNpc Setting" : "Настройка плагина BetterNpc")] public BetterNpcConfig betterNpcConfig { get; set; }
            [JsonProperty(en ? "Setting Up the economy" : "Настройка экономики")] public EconomyConfig economicsConfig { get; set; }
            [JsonProperty(en ? "PVE Mode Setting (only for users PveMode plugin)" : "Настройка PVE режима работы плагина (только для тех, кто использует плагин PveMode)")] public PveModeConfig pveMode { get; set; }
        }

        public class OldTruckConfig : ModularCarConfig
        {
            [JsonProperty(en ? "Change the hacking time of locked crate [true/false]" : "Изменять время взлома заблокированного ящика [true/false]", Order = 100)] public bool changeUnlockTime { get; set; }
            [JsonProperty(en ? "Time to unlock the crates [sec.]" : "Время до открытия заблокированного ящика [sec.]", Order = 100)] public float hackTime { get; set; }
            [JsonProperty(en ? "Location of the locked crate" : "Расположение заблокированного ящика", Order = 101)] public LocationConfig crateLocation { get; set; }
            [JsonProperty(en ? "Which loot table should the plugin use? (0 - default, BetterLoot, MagicLoot; 1 - own; 2 - AlphaLoot)" : "Какую таблицу лута необходимо использовать? (0 - стандартную, BetterLoot, MagicLoot; 1 - собственную; 2 - AlphaLoot)", Order = 102)] public int typeLootTable { get; set; }
            [JsonProperty(en ? "Own loot table" : "Собственная таблица лута", Order = 103)] public LootTableConfig lootTableConfig { get; set; }
        }

        class PluginConfig
        {
            [JsonProperty(en ? "Version" : "Версия плагина")] public string version { get; set; }
            [JsonProperty(en ? "Prefix of chat messages" : "Префикс в чате")] public string prefix { get; set; }
            [JsonProperty(en ? "Main Setting" : "Основные настройки")] public MainConfig mainConfig { get; set; }
            [JsonProperty(en ? "Behavior Settings" : "Настройка поведения")] public BehaviorConfig behaviorConfig { get; set; }
            [JsonProperty(en ? "Loot Settings" : "Настройки Лута")] public LootConfig lootConfig { get; set; }
            [JsonProperty(en ? "Route Settings" : "Настройки маршрутов")] public PathConfig pathConfig { get; set; }
            [JsonProperty(en ? "Convoy Presets" : "Пресеты конвоя")] public HashSet<EventConfig> eventConfigs { get; set; }
            [JsonProperty(en ? "Travelling Vendor Configurations" : "Кофигурации фургонов торговцев")] public HashSet<TravellingVendorConfig> tarvelingVendorConfigs { get; set; }
            [JsonProperty(en ? "Modular Configurations" : "Кофигурации модульных машин")] public HashSet<ModularCarConfig> modularCarConfigs { get; set; }
            [JsonProperty(en ? "Bradley Configurations" : "Кофигурации бредли")] public HashSet<BradleyConfig> bradleyConfigs { get; set; }
            [JsonProperty(en ? "Sedan Configurations" : "Кофигурации седанов")] public HashSet<SedanConfig> sedanConfigs { get; set; }
            [JsonProperty(en ? "Bike Configurations" : "Кофигурации мотоциклов")] public HashSet<BikeConfig> bikeConfigs { get; set; }
            [JsonProperty(en ? "Karuza Car Configurations" : "Кофигурации автомобилей Karuza")] public HashSet<KaruzaCarConfig> karuzaCarConfigs { get; set; }
            [JsonProperty(en ? "Heli Configurations" : "Кофигурации вертолетов")] public HashSet<HeliConfig> heliConfigs { get; set; }
            [JsonProperty(en ? "Turret Configurations" : "Кофигурации турелей")] public HashSet<TurretConfig> turretConfigs { get; set; }
            [JsonProperty(en ? "SamSite Configurations" : "Кофигурации SamSite")] public HashSet<SamSiteConfig> samsiteConfigs { get; set; }
            [JsonProperty(en ? "Crate presets" : "Пресеты ящиков")] public HashSet<CrateConfig> crateConfigs { get; set; }
            [JsonProperty(en ? "NPC Configurations" : "Кофигурации NPC")] public HashSet<NpcConfig> npcConfigs { get; set; }
            [JsonProperty(en ? "Marker Setting" : "Настройки маркера")] public MarkerConfig markerConfig { get; set; }
            [JsonProperty(en ? "Event zone" : "Настройка зоны ивента")] public ZoneConfig zoneConfig { get; set; }
            [JsonProperty(en ? "Notification Settings" : "Настройки уведомлений")] public NotifyConfig notifyConfig { get; set; }
            [JsonProperty("GUI")] public GUIConfig guiConfig { get; set; }
            [JsonProperty(en ? "Supported Plugins" : "Поддерживаемые плагины")] public SupportedPluginsConfig supportedPluginsConfig { get; set; }

            public static PluginConfig DefaultConfig()
            {
                return new PluginConfig()
                {
                    version = "2.8.1",
                    prefix = "[Convoy]",
                    mainConfig = new MainConfig
                    {
                        isAutoEvent = false,
                        minTimeBetweenEvents = 3600,
                        maxTimeBetweenEvents = 3600,
                        fixedSchedule = new FixedScheduleConfig
                        {
                            enabled = false,
                            utcOffsetHours = 3,
                            windows = new List<FixedScheduleWindow>
                            {
                                new FixedScheduleWindow { start = "08:00", end = "09:00" },
                                new FixedScheduleWindow { start = "12:00", end = "13:00" },
                                new FixedScheduleWindow { start = "16:00", end = "17:00" },
                                new FixedScheduleWindow { start = "20:00", end = "21:00" },
                                new FixedScheduleWindow { start = "23:00", end = "00:00" }
                            }
                        },
                        preStartTime = 0,
                        enableStartStopLogs = false,
                        dontStopEventIfPlayerInZone = false,
                        isTurretDropWeapon = false,
                        killEventAfterLoot = true,
                        endAfterLootTime = 300,
                    },
                    behaviorConfig = new BehaviorConfig
                    {
                        agressiveTime = 80,
                        isStopConvoyAgressive = true,
                        stopTime = 80,
                        isPlayerTurretEnable = true
                    },
                    lootConfig = new LootConfig
                    {
                        dropLoot = true,
                        lootLossPercent = 0.5f,
                        blockLootingByMove = false,
                        blockLootingByNpcs = false,
                        blockLootingByBradleys = false,
                        blockLootingByHeli = false
                    },
                    pathConfig = new PathConfig
                    {
                        pathType = 1,
                        minRoadLength = 200,
                        blockRoads = new HashSet<int>(),
                        regularPathConfig = new RegularPathConfig
                        {
                            isRingRoad = true
                        },
                        complexPathConfig = new ComplexPathConfig
                        {
                            chooseLongestRoute = true,
                            minRoadCount = 3
                        },
                        customPathConfig = new CustomPathConfig
                        {
                            customRoutesPresets = new List<string>()
                        }
                    },
                    eventConfigs = new HashSet<EventConfig>
                    {
                        new EventConfig
                        {
                            presetName = "easy",
                            displayName = en ? "Bandit Convoy" : "БАНДИТСКИЙ КОНВОЙ",
                            isAutoStart = true,
                            chance = 40,
                            minTimeAfterWipe = 0,
                            maxTimeAfterWipe = 259200,
                            eventTime = 3600,
                            zoneRadius = 50,
                            maxGroundDamageDistance = 50,
                            maxHeliDamageDistance = 300,
                            vehiclesOrder = new List<string>
                            {
                                "motorbike_easy",
                                "motorbike_sidecar_easy",
                                "sedan_easy",
                                "motorbike_sidecar_easy",
                                "motorbike_easy"
                            },
                            isHeli = false,
                            heliPreset = "",
                            weaponToScaleDamageNpc = new Dictionary<string, float>
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
                            presetName = "medium",
                            displayName = en ? "Cargo Convoy" : "ГРУЗОВОЙ КОНВОЙ",
                            isAutoStart = true,
                            chance = 30,
                            minTimeAfterWipe = 86400,
                            maxTimeAfterWipe = -1,
                            eventTime = 3600,
                            zoneRadius = 60,
                            maxGroundDamageDistance = 100,
                            maxHeliDamageDistance = 300,
                            vehiclesOrder = new List<string>
                            {
                                "motorbike_sidecar_medium",
                                "sedan_medium",
                                "modular_npc_medium",
                                "bradley_medium",
                                "modular_npc_medium",
                                "sedan_medium",
                                "motorbike_sidecar_medium"
                            },
                            isHeli = false,
                            heliPreset = "",
                            weaponToScaleDamageNpc = new Dictionary<string, float>
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
                            presetName = "hard",
                            displayName = en ? "Military Convoy" : "ВОЕНИЗИРОВАННЫЙ КОНВОЙ",
                            isAutoStart = true,
                            chance = 20,
                            minTimeAfterWipe = 259200,
                            maxTimeAfterWipe = -1,
                            eventTime = 3600,
                            zoneRadius = 70,
                            maxGroundDamageDistance = 100,
                            maxHeliDamageDistance = 300,
                            vehiclesOrder = new List<string>
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
                            isHeli = true,
                            heliPreset = "heli_hard",
                            weaponToScaleDamageNpc = new Dictionary<string, float>
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
                            presetName = "nightmare",
                            displayName = en ? "Nightmarish Convoy" : "Кошмарный Конвой",
                            isAutoStart = false,
                            chance = 10,
                            minTimeAfterWipe = 259200,
                            maxTimeAfterWipe = -1,
                            eventTime = 3600,
                            zoneRadius = 100,
                            maxGroundDamageDistance = 120,
                            maxHeliDamageDistance = 300,
                            vehiclesOrder = new List<string>
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
                            isHeli = true,
                            heliPreset = "heli_nightmare",
                            weaponToScaleDamageNpc = new Dictionary<string, float>
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
                    tarvelingVendorConfigs = new HashSet<TravellingVendorConfig>
                    {
                        new TravellingVendorConfig
                        {
                            presetName = "vendor_loot_hard",
                            npcPresetName = "",
                            deleteMapMarker = false,
                            isLocked = true,
                            doorHealth = 250,
                            doorSkin = 934924536,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_minigun_hard",
                                    position = "(0, 0.85, -1.25)",
                                    rotation = "(0, 180, 0)"
                                },
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "frige_safe_hard",
                                    position = "(0, 0, -1.85)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "turret_minigun_hard",
                                    position = "(0, 1.78, 0.818)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "turret_minigun_hard",
                                    position = "(0, 1.78, -1.707)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new TravellingVendorConfig
                        {
                            presetName = "vendor_samsite_hard",
                            npcPresetName = "",
                            deleteMapMarker = false,
                            isLocked = true,
                            doorHealth = 250,
                            doorSkin = 934924536,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_flamethrower_hard",
                                    position = "(0, 0.85, -1.25)",
                                    rotation = "(0, 180, 0)"
                                },
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "frige_safe_hard",
                                    position = "(0, 0, -1.85)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "turret_minigun_hard",
                                    position = "(0, 1.78, 0.818)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            samsiteLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "samsite_default",
                                    position = "(0, 1, -1)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                        },
                        new TravellingVendorConfig
                        {
                            presetName = "vendor_turret_nightmare",
                            npcPresetName = "",
                            deleteMapMarker = false,
                            isLocked = true,
                            doorHealth = 250,
                            doorSkin = 934924536,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_minigun_hard",
                                    position = "(0, 0.85, -1.25)",
                                    rotation = "(0, 180, 0)"
                                },
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "frige_safe_nightmare",
                                    position = "(0, 0, -1.85)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "turret_minigun_nightmare",
                                    position = "(0, 1.78, 0.818)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "turret_minigun_nightmare",
                                    position = "(0, 1.78, -1.707)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new TravellingVendorConfig
                        {
                            presetName = "vendor_samsite_nightmare",
                            npcPresetName = "",
                            deleteMapMarker = false,
                            isLocked = false,
                            doorHealth = 250,
                            doorSkin = 934924536,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_flamethrower_hard",
                                    position = "(0.5, 0.85, -1.25)",
                                    rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_smoke_nightmare",
                                    position = "(-0.5, 0.85, -1.25)",
                                    rotation = "(-1, 180, 0)"
                                },
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "frige_safe_nightmare",
                                    position = "(0, 0, -1.85)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "turret_minigun_nightmare",
                                    position = "(0, 1.78, 0.818)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            samsiteLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "samsite_default",
                                    position = "(0, 1, -1)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                        }
                    },
                    modularCarConfigs = new HashSet<ModularCarConfig>
                    {
                        new ModularCarConfig
                        {
                            presetName = "modular_npc_medium",
                            damageScale = 1,
                            modules = new List<string>
                            {
                                "vehicle.1mod.engine",
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.storage"
                            },
                            npcPresetName = "carnpc_lr300_raid_medium",
                            numberOfNpc = 2,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_lr300_medium",
                                    position = "(-0.9, 0.85, -1.5)",
                                    rotation = "(0, 270, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_lr300_medium",
                                    position = "(0.9, 0.85, -1.5)",
                                    rotation = "(0, 90, 0)"
                                }
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_normal_medium",
                                    position = "(0, 1.5, -1.5)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },

                        new ModularCarConfig
                        {
                            presetName = "modular_sniper_hard",
                            damageScale = 1,
                            modules = new List<string>
                            {
                                "vehicle.1mod.flatbed",
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.storage"
                            },
                            npcPresetName = "carnpc_grenadelauncher_hard",
                            numberOfNpc = 2,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_bolt_hard",
                                    position = "(-0.9, 0.85, -1.5)",
                                    rotation = "(0, 270, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "carnpc_bolt_hard",
                                    position = "(0.9, 0.85, -1.5)",
                                    rotation = "(0, 90, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/testseat.prefab",
                                    isDismount = false,
                                    npcPresetName = "carnpc_bolt_hard",
                                    position = "(0, 0.8, 1.5)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_normal_hard",
                                    position = "(0, 1.5, -1.5)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new ModularCarConfig
                        {
                            presetName = "modular_npc_long_hard",
                            damageScale = 1,
                            modules = new List<string>
                            {
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.2mod.passengers",
                                "vehicle.1mod.rear.seats"
                            },
                            npcPresetName = "carnpc_lr300_raid_hard",
                            numberOfNpc = 10,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>(),
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },

                        new ModularCarConfig
                        {
                            presetName = "modular_npc_long_nightmare",
                            damageScale = 1,
                            modules = new List<string>
                            {
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.rear.seats",
                                "vehicle.1mod.rear.seats",
                                "vehicle.1mod.rear.seats"
                            },
                            npcPresetName = "carnpc_ak_raid_nightmare",
                            numberOfNpc = 8,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>(),
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new ModularCarConfig
                        {
                            presetName = "modular_fuel_nightmare",
                            damageScale = 1,
                            modules = new List<string>
                            {
                                "vehicle.1mod.cockpit.with.engine",
                                "vehicle.1mod.rear.seats",
                                "vehicle.2mod.fuel.tank"
                            },
                            npcPresetName = "carnpc_ak_raid_nightmare_3",
                            numberOfNpc = 4,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_normal_hard",
                                    position = "(0, 0.5, -1.5)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        }
                    },
                    bradleyConfigs = new HashSet<BradleyConfig>
                    {
                        new BradleyConfig
                        {
                            presetName = "bradley_medium",
                            hp = 600f,
                            bradleyBuildingDamageScale = -1,
                            viewDistance = 100.0f,
                            searchDistance = 100.0f,
                            coaxAimCone = 1.5f,
                            coaxFireRate = 0.7f,
                            coaxBurstLength = 5,
                            nextFireTime = 15f,
                            topTurretFireRate = 0.25f,
                            countCrates = 3,
                            instCrateOpen = true,
                            baseLootTableConfig = new BaseLootTableConfig
                            {
                                clearDefaultItemList = true,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 3,
                                minItemsAmount = 3,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.seeker",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.basic",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.mlrs",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.explosive",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            },
                            npcPresetName = "biker_grenadelauncher_medium",
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    isDismount = true,
                                    npcPresetName = "",
                                    position = "(0.624, 1.4, -3)",
                                    rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    isDismount = true,
                                    npcPresetName = "",
                                    position = "(-0.624, 1.4, -3)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            crateLocations = new HashSet<PresetLocationConfig>(),
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new BradleyConfig
                        {
                            presetName = "bradley_hard",
                            hp = 600f,
                            bradleyBuildingDamageScale = -1,
                            viewDistance = 100.0f,
                            searchDistance = 100.0f,
                            coaxAimCone = 1.5f,
                            coaxFireRate = 0.7f,
                            coaxBurstLength = 5,
                            nextFireTime = 15f,
                            topTurretFireRate = 0.25f,
                            countCrates = 3,
                            instCrateOpen = true,
                            baseLootTableConfig = new BaseLootTableConfig
                            {
                                clearDefaultItemList = true,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 4,
                                minItemsAmount = 4,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.basic",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.mlrs",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.explosive",
                                        skin = 0,
                                        name = "",
                                        minAmount = 40,
                                        maxAmount = 60,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "explosive.timed",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.he",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 15,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            },
                            npcPresetName = "carnpc_grenadelauncher_hard",
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    isDismount = true,
                                    npcPresetName = "",
                                    position = "(0.624, 1.4, -3)",
                                    rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    isDismount = true,
                                    npcPresetName = "",
                                    position = "(-0.624, 1.4, -3)",
                                    rotation = "(0, 180, 0)"
                                }
                            },
                            crateLocations = new HashSet<PresetLocationConfig>(),
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new BradleyConfig
                        {
                            presetName = "bradley_samsite_nightmare",
                            hp = 1000,
                            bradleyBuildingDamageScale = -1,
                            viewDistance = 100.0f,
                            searchDistance = 100.0f,
                            coaxAimCone = 1.1f,
                            coaxFireRate = 1f,
                            coaxBurstLength = 10,
                            nextFireTime = 10,
                            topTurretFireRate = 0.25f,
                            countCrates = 3,
                            instCrateOpen = true,
                            baseLootTableConfig = new BaseLootTableConfig
                            {
                                clearDefaultItemList = true,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 4,
                                minItemsAmount = 4,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.basic",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.mlrs",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.explosive",
                                        skin = 0,
                                        name = "",
                                        minAmount = 40,
                                        maxAmount = 60,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "explosive.timed",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.he",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 15,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            },
                            npcPresetName = "carnpc_ak_raid_nightmare_2",
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    isDismount = true,
                                    npcPresetName = "",
                                    position = "(0.624, 1.4, -3)",
                                    rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    isDismount = true,
                                    npcPresetName = "",
                                    position = "(-0.624, 1.4, -3)",
                                    rotation = "(0, 180, 0)"
                                },
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/bikepassengerseat.prefab",
                                    isDismount = true,
                                    npcPresetName = "",
                                    position = "(1.75, 1.234, 1.4)",
                                    rotation = "(0, 90, 0)"
                                }
                            },
                            crateLocations = new HashSet<PresetLocationConfig>(),
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "samsite_default",
                                    position = "(0.238, 1.5, -0.29)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                        }
                    },
                    sedanConfigs = new HashSet<SedanConfig>
                    {
                        new SedanConfig
                        {
                            presetName = "sedan_easy",
                            hp = 500f,
                            npcPresetName = "sedan_npc_easy",
                            numberOfNpc = 1,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_normal_weapon_easy",
                                    position = "(0, 1.734, 0.55)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_normal_weapon_easy",
                                    position = "(0, 1.734, -0.35)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_normal_explosive_easy",
                                    position = "(0, 1.229, -1.780)",
                                    rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(-0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new SedanConfig
                        {
                            presetName = "sedan_medium",
                            hp = 500f,
                            npcPresetName = "carnpc_shotgunm4_medium",
                            numberOfNpc = 2,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_elite_explosive_medium",
                                    position = "(0, 1.734, 0.55)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_elite_weapon_medium",
                                    position = "(0, 1.734, -0.35)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_normal_explosive_medium",
                                    position = "(0, 1.229, -1.780)",
                                    rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(-0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new SedanConfig
                        {
                            presetName = "sedan_hard",
                            hp = 500f,
                            npcPresetName = "carnpc_lr300_hard",
                            numberOfNpc = 2,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_elite_explosive_hard",
                                    position = "(0, 1.734, 0.55)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_elite_weapon_hard",
                                    position = "(0, 1.734, -0.35)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_normal_explosive_hard",
                                    position = "(0, 1.229, -1.780)",
                                    rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(-0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new SedanConfig
                        {
                            presetName = "sedan_nightmare",
                            hp = 500f,
                            npcPresetName = "carnpc_ak_raid_nightmare_3",
                            numberOfNpc = 2,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_elite_explosive_hard",
                                    position = "(0, 1.734, 0.55)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_elite_weapon_hard",
                                    position = "(0, 1.734, -0.35)",
                                    rotation = "(0, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_normal_explosive_hard",
                                    position = "(0, 1.229, -1.780)",
                                    rotation = "(355.691, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                                new PresetLocationConfig
                                {
                                    presetName = "crate_invisible_fuel_easy",
                                    position = "(-0.833, 1.09, -2.019)",
                                    rotation = "(85.057, 0, 0)"
                                },
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        }
                    },
                    bikeConfigs = new HashSet<BikeConfig>
                    {
                        new BikeConfig
                        {
                            presetName = "motorbike_easy",
                            prefabName = "assets/content/vehicles/bikes/motorbike.prefab",
                            hp = 300,
                            npcPresetName = "biker_m92_easy_2",
                            numberOfNpc = 1,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                            },
                            crateLocations = new HashSet<PresetLocationConfig>(),
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },
                        new BikeConfig
                        {
                            presetName = "motorbike_sidecar_easy",
                            prefabName = "assets/content/vehicles/bikes/motorbike_sidecar.prefab",
                            hp = 350,
                            npcPresetName = "biker_m92_easy_1",
                            numberOfNpc = 2,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "biker_spas12_easy",
                                    position = "(0, 0.45, -0.45)",
                                    rotation = "(15, 0, 0)"
                                }
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_basic_techparts_easy",
                                    position = "(0.715, 0.354, -0.712)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        },

                        new BikeConfig
                        {
                            presetName = "motorbike_sidecar_medium",
                            prefabName = "assets/content/vehicles/bikes/motorbike_sidecar.prefab",
                            hp = 350,
                            npcPresetName = "biker_mp5_medium",
                            numberOfNpc = 2,
                            additionalNpcs = new HashSet<NpcPoseConfig>
                            {
                                new NpcPoseConfig
                                {
                                    isEnable = true,
                                    seatPrefab = "assets/prefabs/vehicle/seats/attackheligunner.prefab",
                                    isDismount = true,
                                    npcPresetName = "biker_grenadelauncher_medium",
                                    position = "(0, 0.45, -0.45)",
                                    rotation = "(15, 0, 0)"
                                }
                            },
                            crateLocations = new HashSet<PresetLocationConfig>
                            {
                                new PresetLocationConfig
                                {
                                    presetName = "crate_basic_resources_medium",
                                    position = "(0.715, 0.354, -0.712)",
                                    rotation = "(0, 0, 0)"
                                }
                            },
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>(),
                        }
                    },
                    karuzaCarConfigs = new HashSet<KaruzaCarConfig>
                    {
                        new KaruzaCarConfig
                        {
                            presetName = "duneBuggie",
                            prefabName = "assets/custom/dunebuggie.prefab",
                            npcPresetName = "biker_m92_easy_2",
                            numberOfNpc = 1,
                            additionalNpcs = new HashSet<NpcPoseConfig>(),
                            crateLocations = new HashSet<PresetLocationConfig>(),
                            turretLocations = new HashSet<PresetLocationConfig>(),
                            samsiteLocations = new HashSet<PresetLocationConfig>()
                        }
                    },
                    turretConfigs = new HashSet<TurretConfig>
                    {
                        new TurretConfig
                        {
                            presetName = "turret_minigun_hard",
                            hp = 500f,
                            shortNameWeapon = "minigun",
                            shortNameAmmo = "ammo.rifle",
                            countAmmo = 500,
                            targetDetectionRange = 100,
                            targetLossRange = 0
                        },
                        new TurretConfig
                        {
                            presetName = "turret_minigun_nightmare",
                            hp = 500f,
                            shortNameWeapon = "minigun",
                            shortNameAmmo = "ammo.rifle",
                            countAmmo = 800,
                            targetDetectionRange = 130,
                            targetLossRange = 0
                        }
                    },
                    samsiteConfigs = new HashSet<SamSiteConfig>
                    {
                        new SamSiteConfig
                        {
                            presetName = "samsite_default",
                            hp = 1000,
                            countAmmo = 100
                        }
                    },
                    crateConfigs = new HashSet<CrateConfig>
                    {
                        new CrateConfig
                        {
                            presetName = "crate_normal_weapon_easy",
                            prefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 1,
                                minItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "smg.thompson",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.semiauto",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.2",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "pistol.m92",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "shotgun.spas12",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_normal_explosive_easy",
                            prefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 4,
                                minItemsAmount = 4,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.beancan",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "explosive.satchel",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.molotov",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.f1",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.holosight",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.silencer",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.extendedmags",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_basic_techparts_easy",
                            prefabName = "assets/bundled/prefabs/radtown/crate_basic.prefab",
                            skin = 0,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_invisible_fuel_easy",
                            prefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_basic.prefab",
                            skin = 0,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/loot_barrel_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 2,
                                maxItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "crude.oil",
                                        skin = 0,
                                        name = "",
                                        minAmount = 100,
                                        maxAmount = 200,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "lowgradefuel",
                                        skin = 0,
                                        name = "",
                                        minAmount = 100,
                                        maxAmount = 200,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },

                        new CrateConfig
                        {
                            presetName = "crate_basic_resources_medium",
                            prefabName = "assets/bundled/prefabs/radtown/crate_basic.prefab",
                            skin = 0,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 9,
                                maxItemsAmount = 9,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "cloth",
                                        skin = 0,
                                        name = "",
                                        minAmount = 200,
                                        maxAmount = 300,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "leather",
                                        skin = 0,
                                        name = "",
                                        minAmount = 100,
                                        maxAmount = 200,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "charcoal",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2000,
                                        maxAmount = 4000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "gunpowder",
                                        skin = 0,
                                        name = "",
                                        minAmount = 250,
                                        maxAmount = 750,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "sulfur",
                                        skin = 0,
                                        name = "",
                                        minAmount = 750,
                                        maxAmount = 1250,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.refined",
                                        skin = 0,
                                        name = "",
                                        minAmount = 75,
                                        maxAmount = 125,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.fragments",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1500,
                                        maxAmount = 3000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 100,
                                        maxAmount = 300,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "diesel_barrel",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_normal_explosive_medium",
                            prefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 4,
                                minItemsAmount = 4,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.molotov",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.f1",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.flashbang",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },

                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.holosight",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.silencer",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.extendedmags",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_elite_weapon_medium",
                            prefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 2,
                                minItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "smg.thompson",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.semiauto",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.2",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.mp5",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 20,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.lr300",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 20,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "hmlmg",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 20,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.ak",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.bolt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_elite_explosive_medium",
                            prefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 3,
                                minItemsAmount = 3,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.seeker",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.basic",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 2,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.mlrs",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.explosive",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_invisible_normal_medium",
                            prefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_normal.prefab",
                            skin = 0,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/loot_barrel_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 6,
                                maxItemsAmount = 6,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "roadsign.kilt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.facemask",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.plate.torso",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle",
                                        skin = 0,
                                        name = "",
                                        minAmount = 64,
                                        maxAmount = 128,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "syringe.medical",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 10,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.thompson",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.semiauto",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.2",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.mp5",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.lr300",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "hmlmg",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.ak",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.bolt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },

                        new CrateConfig
                        {
                            presetName = "crate_invisible_normal_hard",
                            prefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_normal_2.prefab",
                            skin = 0,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/loot_barrel_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 9,
                                maxItemsAmount = 9,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "roadsign.kilt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.facemask",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.plate.torso",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "syringe.medical",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 20,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "largemedkit",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 10,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "black.raspberries",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 15,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.mp5",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.lr300",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.ak",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.bolt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "lmg.m249",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.l96",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_elite_explosive_hard",
                            prefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 4,
                                minItemsAmount = 4,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.basic",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.mlrs",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.explosive",
                                        skin = 0,
                                        name = "",
                                        minAmount = 40,
                                        maxAmount = 60,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "explosive.timed",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.he",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 15,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_elite_weapon_hard",
                            prefabName = "assets/bundled/prefabs/radtown/crate_elite.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/loot_barrel_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 3,
                                maxItemsAmount = 3,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "smg.mp5",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 15,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.lr300",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 15,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.ak",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 15,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.bolt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 15,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "lmg.m249",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.l96",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "crate_normal_explosive_hard",
                            prefabName = "assets/bundled/prefabs/radtown/crate_normal.prefab",
                            hackTime = 0,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 4,
                                minItemsAmount = 4,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.molotov",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.f1",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "grenade.flashbang",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3,
                                        maxAmount = 10,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },

                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.holosight",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.silencer",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "weapon.mod.extendedmags",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "frige_safe_hard",
                            prefabName = "assets/prefabs/deployable/fridge/fridge.deployed.prefab",
                            skin = 3005880420,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = false,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 5,
                                            maxLootScale = 5,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 9,
                                maxItemsAmount = 9,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "cloth",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1000,
                                        maxAmount = 1500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "leather",
                                        skin = 0,
                                        name = "",
                                        minAmount = 500,
                                        maxAmount = 1000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "charcoal",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10000,
                                        maxAmount = 20000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "gunpowder",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2000,
                                        maxAmount = 3500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "sulfur",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2500,
                                        maxAmount = 5000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.refined",
                                        skin = 0,
                                        name = "",
                                        minAmount = 350,
                                        maxAmount = 500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.fragments",
                                        skin = 0,
                                        name = "",
                                        minAmount = 7500,
                                        maxAmount = 15000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 500,
                                        maxAmount = 1500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "diesel_barrel",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 10,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                }
                            }
                        },

                        new CrateConfig
                        {
                            presetName = "crate_invisible_normal_nightmare",
                            prefabName = "assets/bundled/prefabs/modding/lootables/invisible/invisible_lootable_prefabs/invisible_crate_normal_2.prefab",
                            skin = 0,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/loot_barrel_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 9,
                                maxItemsAmount = 9,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "roadsign.kilt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.facemask",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.plate.torso",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "syringe.medical",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 20,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "largemedkit",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 10,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "black.raspberries",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 15,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.mp5",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.lr300",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.ak",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.bolt",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 10,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "lmg.m249",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.l96",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new CrateConfig
                        {
                            presetName = "frige_safe_nightmare",
                            prefabName = "assets/prefabs/deployable/fridge/fridge.deployed.prefab",
                            skin = 3005880420,
                            hackTime = -1,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = false,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 5,
                                            maxLootScale = 5,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 9,
                                maxItemsAmount = 9,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "cloth",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1500,
                                        maxAmount = 2500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "leather",
                                        skin = 0,
                                        name = "",
                                        minAmount = 750,
                                        maxAmount = 1500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "charcoal",
                                        skin = 0,
                                        name = "",
                                        minAmount = 15000,
                                        maxAmount = 25000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "gunpowder",
                                        skin = 0,
                                        name = "",
                                        minAmount = 2500,
                                        maxAmount = 4000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "sulfur",
                                        skin = 0,
                                        name = "",
                                        minAmount = 3000,
                                        maxAmount = 6000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.refined",
                                        skin = 0,
                                        name = "",
                                        minAmount = 350,
                                        maxAmount = 500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "metal.fragments",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10000,
                                        maxAmount = 17500,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1000,
                                        maxAmount = 2000,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "diesel_barrel",
                                        skin = 0,
                                        name = "",
                                        minAmount = 10,
                                        maxAmount = 15,
                                        chance = 100,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                }
                            }
                        },
                    },
                    heliConfigs = new HashSet<HeliConfig>
                    {
                        new HeliConfig
                        {
                            presetName = "heli_hard",
                            hp = 10000,
                            cratesAmount = 3,
                            mainRotorHealth = 750,
                            rearRotorHealth = 375,
                            height = 50f,
                            bulletDamage = 20f,
                            bulletSpeed = 250f,
                            distance = 350f,
                            outsideTime = 30,
                            immediatelyKill = true,
                            instCrateOpen = true,
                            baseLootTableConfig = new BaseLootTableConfig
                            {
                                clearDefaultItemList = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                maxItemsAmount = 1,
                                minItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        minAmount = 100,
                                        maxAmount = 200,
                                        chance = 100f,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new HeliConfig
                        {
                            presetName = "heli_nightmare",
                            hp = 20000,
                            cratesAmount = 3,
                            mainRotorHealth = 1500,
                            rearRotorHealth = 750,
                            height = 50f,
                            bulletDamage = 20f,
                            bulletSpeed = 250f,
                            distance = 350f,
                            outsideTime = 30,
                            immediatelyKill = true,
                            instCrateOpen = true,
                            baseLootTableConfig = new BaseLootTableConfig
                            {
                                clearDefaultItemList = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                maxItemsAmount = 1,
                                minItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        minAmount = 100,
                                        maxAmount = 200,
                                        chance = 100f,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        }
                    },
                    npcConfigs = new HashSet<NpcConfig>
                    {
                        new NpcConfig
                        {
                            presetName = "biker_m92_easy_1",
                            displayName = en ? "Forest Bandit" : "Лесной бандит",
                            health = 100,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "mask.bandana",
                                    skinID = 3255213783
                                },
                                new NpcWear
                                {
                                    shortName = "hat.boonie",
                                    skinID = 2557702256
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 1282142258
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2080977144
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 0
                                }
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "pistol.m92",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight" },
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110f,
                            attackRangeMultiplier = 1f,
                            senseRange = 60f,
                            memoryDuration = 10f,
                            damageScale = 0.4f,
                            aimConeScale = 1.5f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 2,
                                            maxLootScale = 2,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 1,
                                minItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "bandage",
                                        minAmount = 1,
                                        maxAmount = 3,
                                        chance = 30,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "syringe.medical",
                                        minAmount = 1,
                                        maxAmount = 3,
                                        chance = 20,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "largemedkit",
                                        minAmount = 1,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "biker_m92_easy_2",
                            displayName =  en ? "Road Bandit" : "Дорожный бандит",
                            health = 100,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "coffeecan.helmet",
                                    skinID = 2803024592
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2811533300
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2811533832
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 2816776847
                                }
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "pistol.m92",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight" },
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110f,
                            attackRangeMultiplier = 1f,
                            senseRange = 60f,
                            memoryDuration = 10f,
                            damageScale = 0.4f,
                            aimConeScale = 1.5f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 2,
                                            maxLootScale = 2,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 1,
                                minItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "bandage",
                                        minAmount = 1,
                                        maxAmount = 3,
                                        chance = 30,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "syringe.medical",
                                        minAmount = 1,
                                        maxAmount = 3,
                                        chance = 20,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "largemedkit",
                                        minAmount = 1,
                                        maxAmount = 3,
                                        chance = 10,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "biker_spas12_easy",
                            displayName = en ? "Hunter" : "Охотник",
                            health = 125,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 961066582
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 961084105
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 961096730
                                },
                                new NpcWear
                                {
                                    shortName = "burlap.gloves",
                                    skinID = 961103399
                                },
                                new NpcWear
                                {
                                    shortName = "hat.beenie",
                                    skinID = 594202145
                                },
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "shotgun.spas12",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight" },
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110f,
                            attackRangeMultiplier = 1f,
                            senseRange = 60f,
                            memoryDuration = 10f,
                            damageScale = 0.8f,
                            aimConeScale = 1f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                minItemsAmount = 2,
                                maxItemsAmount = 4,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 60,
                                        maxAmount = 80,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol",
                                        skin = 0,
                                        name = "",
                                        minAmount = 60,
                                        maxAmount = 80,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 15,
                                        maxAmount = 30,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun",
                                        skin = 0,
                                        name = "",
                                        minAmount = 15,
                                        maxAmount = 30,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun.slug",
                                        skin = 0,
                                        name = "",
                                        minAmount = 15,
                                        maxAmount = 30,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle",
                                        skin = 0,
                                        name = "",
                                        minAmount = 60,
                                        maxAmount = 80,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "sedan_npc_easy",
                            displayName = en ? "Armored Bandit" : "Бронированный бандит",
                            health = 150,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "coffeecan.helmet",
                                    skinID = 3312398531
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.jacket",
                                    skinID = 3312406908
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.kilt",
                                    skinID = 3312413579
                                },

                                new NpcWear
                                {
                                    shortName = "roadsign.gloves",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 1582399729
                                },
                                new NpcWear
                                {
                                    shortName = "tshirt",
                                    skinID = 1582403431
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 0
                                },
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "smg.thompson",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight" },
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110f,
                            attackRangeMultiplier = 1f,
                            senseRange = 60f,
                            memoryDuration = 10f,
                            damageScale = 0.4f,
                            aimConeScale = 1.3f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = true,
                                maxItemsAmount = 1,
                                minItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "smg.thompson",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.semiauto",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "smg.2",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "pistol.m92",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "shotgun.spas12",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rifle.ak",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 1,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "rocket.launcher",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 1,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rocket.basic",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 1,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "explosive.timed",
                                        skin = 0,
                                        name = "",
                                        minAmount = 1,
                                        maxAmount = 1,
                                        chance = 1,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },

                        new NpcConfig
                        {
                            presetName = "biker_grenadelauncher_medium",
                            displayName = en ? "Bomber Man" : "Взрыватель",
                            health = 200,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "coffeecan.helmet",
                                    skinID = 2350097716
                                },
                                new NpcWear
                                {
                                    shortName = "jacket",
                                    skinID = 2395820290
                                },
                                new NpcWear
                                {
                                    shortName = "tshirt",
                                    skinID = 856391177
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2080977144
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 0
                                }
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "multiplegrenadelauncher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> {},
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.2f,
                            aimConeScale = 1.5f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "biker_mp5_medium",
                            displayName = en ? "Armored Road Bandit" : "Бронированный дорожный бандит",
                            health = 125,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "coffeecan.helmet",
                                    skinID = 1269589560
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.jacket",
                                    skinID = 1706089885
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.gloves",
                                    skinID = 2806216923
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2811533300
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2080977144
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 0
                                }
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "smg.mp5",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> {},
                                    ammo = ""
                                }
                            },
                            speed = 7.5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 1.4f,
                            aimConeScale = 0.8f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_shotgunm4_medium",
                            displayName = en ? "Madman" : "Безумец",
                            health = 170,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "coffeecan.helmet",
                                    skinID = 1624104393
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.jacket",
                                    skinID = 1624100124
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.kilt",
                                    skinID = 1624102935
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2099705103
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2099701364
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 0
                                },
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "shotgun.m4",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight" },
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 1.5f,
                            aimConeScale = 1f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_lr300_medium",
                            displayName = en ? "Radiation Liquidator" : "Ликвидатор Радиации",
                            health = 175,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "hat.gas.mask",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.kilt",
                                    skinID = 1740068457
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.jacket",
                                    skinID = 1740065674
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.gloves",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2649552973
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2649555568
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "diving.tank",
                                    skinID = 0
                                },

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "rifle.lr300",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 7.5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_lr300_raid_medium",
                            displayName = en ? "Raider" : "Рейдер",
                            health = 175,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "clatter.helmet",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "attire.hide.poncho",
                                    skinID = 2819301476
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.gloves",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2984978438
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2080977144
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 0
                                },

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "rifle.lr300",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "rocket.launcher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 7.5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },

                        new NpcConfig
                        {
                            presetName = "carnpc_lr300_hard",
                            displayName = en ? "Defender" : "Защитник",
                            health = 175,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "metal.facemask",
                                    skinID = 3274815691
                                },
                                new NpcWear
                                {
                                    shortName = "metal.plate.torso",
                                    skinID = 3274816373
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.kilt",
                                    skinID = 3299983586
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 3322149888
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 3322151159
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 819211835
                                },

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "rifle.lr300",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 7.5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_lr300_raid_hard",
                            displayName = en ? "Armored Raider" : "Бронированный Рейдер",
                            health = 175,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "metal.facemask",
                                    skinID = 1644415525
                                },
                                new NpcWear
                                {
                                    shortName = "metal.plate.torso",
                                    skinID = 1644419309
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2282815003
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2282817402
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 919261524
                                }
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "rifle.lr300",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "rocket.launcher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 7.5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_grenadelauncher_hard",
                            displayName = en ? "Armored Bomber Man" : "Бронированный Взрыватель",
                            health = 175,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "metal.facemask",
                                    skinID = 2810942233
                                },
                                new NpcWear
                                {
                                    shortName = "jacket",
                                    skinID = 2843424058
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.kilt",
                                    skinID = 2823738497
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2814837980
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2814838951
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 919261524
                                }

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "multiplegrenadelauncher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> {},
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.5f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_bolt_hard",
                            displayName = en ? "Sniper" : "Снайпер",
                            health = 175,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "gloweyes",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "metal.facemask",
                                    skinID = 2226597543
                                },
                                new NpcWear
                                {
                                    shortName = "metal.plate.torso",
                                    skinID = 2226598382
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2282815003
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2282817402
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 919261524
                                }

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "rifle.bolt",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.lasersight", "weapon.mod.8x.scope" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_flamethrower_hard",
                            displayName = en ? "Fire Boss" : "Огненный босс",
                            health = 1500,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "scientistsuit_heavy",
                                    skinID = 0
                                }

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "military flamethrower",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 3.5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 6,
                                maxItemsAmount = 6,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun.slug",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol",
                                        skin = 0,
                                        name = "",
                                        minAmount = 120,
                                        maxAmount = 160,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 120,
                                        maxAmount = 160,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 120,
                                        maxAmount = 160,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.explosive",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.incendiary",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.buckshot",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 15,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.he",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 15,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.smoke",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 15,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_minigun_hard",
                            displayName = en ? "Boss" : "Босс",
                            health = 1500,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "scientistsuit_heavy",
                                    skinID = 0
                                }

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "minigun",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 3.5f,
                            roamRange = 10f,
                            chaseRange = 110,
                            attackRangeMultiplier = 1f,
                            senseRange = 110,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 0.7f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },

                        new NpcConfig
                        {
                            presetName = "carnpc_ak_raid_nightmare",
                            displayName = en ? "Armored Raider" : "Бронированный Рейдер",
                            health = 500,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "metal.facemask",
                                    skinID = 3274815691
                                },
                                new NpcWear
                                {
                                    shortName = "metal.plate.torso",
                                    skinID = 3274816373
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.kilt",
                                    skinID = 3299983586
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 3318206106
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 3318207180
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 819211835
                                },

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "rifle.ak",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "rocket.launcher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 7.5f,
                            roamRange = 10f,
                            chaseRange = 130,
                            attackRangeMultiplier = 1f,
                            senseRange = 130,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_ak_raid_nightmare_2",
                            displayName = en ? "Armored Raider" : "Бронированный Рейдер",
                            health = 500,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "metal.facemask",
                                    skinID = 2810942233
                                },
                                new NpcWear
                                {
                                    shortName = "jacket",
                                    skinID = 2843424058
                                },
                                new NpcWear
                                {
                                    shortName = "roadsign.kilt",
                                    skinID = 2823738497
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 2814837980
                                },
                                new NpcWear
                                {
                                    shortName = "hoodie",
                                    skinID = 2814838951
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 919261524
                                }

                            },
                            beltItems = new List<NpcBelt>
                            {
                                 new NpcBelt
                                {
                                    shortName = "rifle.ak",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "rocket.launcher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 130,
                            attackRangeMultiplier = 1f,
                            senseRange = 130,
                            memoryDuration = 10f,
                            damageScale = 0.5f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_2.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_ak_raid_nightmare_3",
                            displayName = en ? "Armored Raider" : "Бронированный Рейдер",
                            health = 500,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "burlap.shirt",
                                    skinID = 636287439
                                },
                                new NpcWear
                                {
                                    shortName = "metal.facemask",
                                    skinID = 2703876402
                                },
                                new NpcWear
                                {
                                    shortName = "tactical.gloves",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "metal.plate.torso",
                                    skinID = 891976364
                                },
                                new NpcWear
                                {
                                    shortName = "pants",
                                    skinID = 636287180
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 636286960
                                }
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "rifle.ak",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight" },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "rocket.launcher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "grenade.f1",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 130,
                            attackRangeMultiplier = 3f,
                            senseRange = 130,
                            memoryDuration = 10f,
                            damageScale = 0.4f,
                            aimConeScale = 1.3f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = false,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                maxItemsAmount = 1,
                                minItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        minAmount = 100,
                                        maxAmount = 200,
                                        chance = 100f,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_smoke_nightmare",
                            displayName = en ? "Smoker" : "Смокер",
                            health = 500,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "heavy.plate.helmet",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "heavy.plate.jacket",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "heavy.plate.pants",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "tactical.gloves",
                                    skinID = 0
                                },
                                new NpcWear
                                {
                                    shortName = "shoes.boots",
                                    skinID = 636286960
                                }
                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "multiplegrenadelauncher",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> {},
                                    ammo = "40mm_grenade_smoke"
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "grenade.f1",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 5f,
                            roamRange = 10f,
                            chaseRange = 130,
                            attackRangeMultiplier = 3f,
                            senseRange = 130,
                            memoryDuration = 10f,
                            damageScale = 0.4f,
                            aimConeScale = 1.3f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = false,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = false,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 1,
                                            maxLootScale = 1,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/crate_food_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                maxItemsAmount = 1,
                                minItemsAmount = 2,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        minAmount = 100,
                                        maxAmount = 200,
                                        chance = 100f,
                                        isBlueprint = false,
                                        skin = 0,
                                        name = "",
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_flamethrower_nightmare",
                            displayName = en ? "Fire Boss" : "Огненный босс",
                            health = 3000,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "scientistsuit_heavy",
                                    skinID = 0
                                }

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "military flamethrower",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 3.5f,
                            roamRange = 10f,
                            chaseRange = 130,
                            attackRangeMultiplier = 1f,
                            senseRange = 130,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 1.0f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 6,
                                maxItemsAmount = 6,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.shotgun.slug",
                                        skin = 0,
                                        name = "",
                                        minAmount = 20,
                                        maxAmount = 40,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol",
                                        skin = 0,
                                        name = "",
                                        minAmount = 120,
                                        maxAmount = 160,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol.fire",
                                        skin = 0,
                                        name = "",
                                        minAmount = 120,
                                        maxAmount = 160,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.pistol.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 120,
                                        maxAmount = 160,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.explosive",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.incendiary",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.rifle.hv",
                                        skin = 0,
                                        name = "",
                                        minAmount = 128,
                                        maxAmount = 256,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.buckshot",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 15,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.he",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 15,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    },
                                    new LootItemConfig
                                    {
                                        shortname = "ammo.grenadelauncher.smoke",
                                        skin = 0,
                                        name = "",
                                        minAmount = 5,
                                        maxAmount = 15,
                                        chance = 5,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                        new NpcConfig
                        {
                            presetName = "carnpc_minigun_nightmare",
                            displayName = en ? "Boss" : "Босс",
                            health = 3000,
                            kit = "",
                            wearItems = new List<NpcWear>
                            {
                                new NpcWear
                                {
                                    shortName = "scientistsuit_heavy",
                                    skinID = 0
                                }

                            },
                            beltItems = new List<NpcBelt>
                            {
                                new NpcBelt
                                {
                                    shortName = "minigun",
                                    amount = 1,
                                    skinID = 0,
                                    mods = new List<string> { },
                                    ammo = ""
                                },
                                new NpcBelt
                                {
                                    shortName = "syringe.medical",
                                    amount = 10,
                                    skinID = 0,
                                    mods = new List<string>(),
                                    ammo = ""
                                }
                            },
                            speed = 3.5f,
                            roamRange = 10f,
                            chaseRange = 130,
                            attackRangeMultiplier = 1f,
                            senseRange = 130,
                            memoryDuration = 10f,
                            damageScale = 0.65f,
                            aimConeScale = 0.7f,
                            checkVisionCone = false,
                            visionCone = 135f,
                            turretDamageScale = 1f,
                            disableRadio = false,
                            deleteCorpse = true,
                            lootTableConfig = new LootTableConfig
                            {
                                clearDefaultItemList = true,
                                isAlphaLoot = false,
                                alphaLootPresetName = "",
                                isCustomLoot = false,
                                isLootTablePLugin = false,
                                prefabConfigs = new PrefabLootTableConfigs
                                {
                                    isEnable = true,
                                    prefabs = new List<PrefabConfig>
                                    {
                                        new PrefabConfig
                                        {
                                            minLootScale = 3,
                                            maxLootScale = 3,
                                            prefabName = "assets/bundled/prefabs/radtown/underwater_labs/tech_parts_1.prefab"
                                        }
                                    }
                                },
                                isRandomItemsEnable = false,
                                minItemsAmount = 1,
                                maxItemsAmount = 1,
                                items = new List<LootItemConfig>
                                {
                                    new LootItemConfig
                                    {
                                        shortname = "scrap",
                                        skin = 0,
                                        name = "",
                                        minAmount = 50,
                                        maxAmount = 100,
                                        chance = 50,
                                        isBlueprint = false,
                                        genomes = new List<string>()
                                    }
                                }
                            }
                        },
                    },
                    markerConfig = new MarkerConfig
                    {
                        enable = true,
                        useRingMarker = true,
                        useShopMarker = true,
                        radius = 0.2f,
                        alpha = 0.6f,
                        color1 = new ColorConfig { r = 0.81f, g = 0.25f, b = 0.15f },
                        color2 = new ColorConfig { r = 0f, g = 0f, b = 0f }
                    },
                    zoneConfig = new ZoneConfig
                    {
                        isPVPZone = false,
                        isDome = false,
                        darkening = 5,
                        isColoredBorder = false,
                        brightness = 5,
                        borderColor = 2
                    },
                    notifyConfig = new NotifyConfig
                    {
                        isChatEnable = true,
                        gameTipConfig = new GameTipConfig
                        {
                            isEnabled = false,
                            style = 2,
                        },
                        timeNotifications = new HashSet<int>
                        {
                            300,
                            60,
                            30,
                            5
                        },
                    },
                    guiConfig = new GUIConfig
                    {
                        isEnable = true,
                        offsetMinY = -56
                    },

                    supportedPluginsConfig = new SupportedPluginsConfig
                    {
                        pveMode = new PveModeConfig
                        {
                            enable = false,
                            ownerIsStopper = false,
                            noDealDamageIfCooldownAndTeamOwner = false,
                            noInterractIfCooldownAndNoOwners = false,
                            showEventOwnerNameOnMap = true,
                            damage = 500f,
                            scaleDamage = new Dictionary<string, float>
                            {
                                ["Npc"] = 1f,
                                ["Bradley"] = 2f,
                                ["Helicopter"] = 2f,
                                ["Turret"] = 1f,
                            },
                            lootCrate = false,
                            hackCrate = false,
                            lootNpc = false,
                            damageNpc = false,
                            targetNpc = false,
                            damageHeli = false,
                            targetHeli = false,
                            damageTank = false,
                            targetTank = false,
                            canEnter = false,
                            canEnterCooldownPlayer = true,
                            timeExitOwner = 300,
                            alertTime = 60,
                            restoreUponDeath = true,
                            cooldown = 86400,
                        },
                        economicsConfig = new EconomyConfig
                        {
                            enable = false,
                            plugins = new HashSet<string> { "Economics", "Server Rewards", "IQEconomic" },
                            minCommandPoint = 0,
                            minEconomyPiont = 0,
                            crates = new Dictionary<string, double>
                            {
                                ["assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab"] = 0.4
                            },
                            npcPoint = 1,
                            bradleyPoint = 5,
                            heliPoint = 5,
                            sedanPoint = 0,
                            modularCarPoint = 0,
                            turretPoint = 1,
                            lockedCratePoint = 1,
                            commands = new HashSet<string>()
                        },
                        betterNpcConfig = new BetterNpcConfig
                        {
                            bradleyNpc = false,
                            heliNpc = false
                        },
                        guiAnnouncementsConfig = new GUIAnnouncementsConfig
                        {
                            isEnabled = false,
                            bannerColor = "Grey",
                            textColor = "White",
                            apiAdjustVPosition = 0.03f
                        },
                        notifyPluginConfig = new NotifyPluginConfig
                        {
                            isEnabled = false,
                            type = 0
                        },
                        discordMessagesConfig = new DiscordConfig
                        {
                            isEnabled = false,
                            webhookUrl = "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
                            embedColor = 13516583,
                            keys = new HashSet<string>
                            {
                                "PreStart",
                                "EventStart",
                                "PreFinish",
                                "Finish",
                                "StartHackCrate"
                            }
                        },
                    }
                };
            }
        }
        #endregion Config

        private void RegisterConvoyPresetsToLoottable()
        {
            if (plugins.Exists("Loottable") && Loottable != null)
            {
                try
                {
                    Loottable.Call("CreatePresetCategory", this, "Convoy");
                    if (_config != null && _config.crateConfigs != null)
                    {
                        foreach (var crate in _config.crateConfigs)
                        {
                            var key = PresetKey(crate.presetName);
                            Loottable.Call("CreatePreset", this, key, crate.presetName, "", false);
                        }
                    }
                }
                catch { }
            }
        }

        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "Loottable")
            {
                RegisterConvoyPresetsToLoottable();
            }
        }

        [ConsoleCommand("convoy.register_loottable")]
        private void CcmdRegisterLoottable(ConsoleSystem.Arg arg)
        {
            if (arg == null || (arg.Connection != null && arg.Connection.authLevel < 2)) return;
            RegisterConvoyPresetsToLoottable();
            Puts("[Convoy] Loottable presets registered.");
        }

}
                }

namespace Oxide.Plugins.ConvoyExtensionMethods
{
//     using UnityEngine;  // moved/disabled by cold fix

    public static class ExtensionMethods
    {
        // ---- Collections ----
        public static bool Any<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null || predicate == null) return false;
            foreach (var item in source) if (predicate(item)) return true;
            return false;
        }

        public static HashSet<TSource> Where<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            var result = new HashSet<TSource>();
            if (source == null || predicate == null) return result;
            foreach (var item in source) if (predicate(item)) result.Add(item);
            return result;
        }

        public static List<TSource> WhereList<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            var result = new List<TSource>();
            if (source == null || predicate == null) return result;
            foreach (var item in source) if (predicate(item)) result.Add(item);
            return result;
        }

        public static HashSet<TResult> Select<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, TResult> selector)
        {
            var result = new HashSet<TResult>();
            if (source == null || selector == null) return result;
            foreach (var item in source) result.Add(selector(item));
            return result;
        }

        public static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
        {
            var list = new List<TSource>();
            if (source == null) return list;
            foreach (var item in source) list.Add(item);
            return list;
        }

        public static TSource ElementAt<TSource>(this IEnumerable<TSource> source, int index)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            int i = 0;
            foreach (var item in source) { if (i == index) return item; i++; }
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        public static TSource First<TSource>(this IEnumerable<TSource> source)
        {
            foreach (var item in source) return item;
            throw new InvalidOperationException("Sequence contains no elements");
        }

        public static TSource First<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            foreach (var item in source) if (predicate(item)) return item;
            throw new InvalidOperationException("No matching element");
        }

        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            foreach (var item in source) if (predicate(item)) return item;
            return default(TSource);
        }

        public static TSource Last<TSource>(this IEnumerable<TSource> source)
        {
            bool found = false;
            TSource last = default(TSource);
            foreach (var item in source) { last = item; found = true; }
            if (!found) throw new InvalidOperationException("Sequence contains no elements");
            return last;
        }

        public static TSource Max<TSource>(this IEnumerable<TSource> source, Func<TSource, float> selector)
        {
            if (source == null || selector == null) return default(TSource);
            bool found = false;
            TSource best = default(TSource);
            float bestVal = float.NegativeInfinity;
            foreach (var item in source)
            {
                float v = selector(item);
                if (!found || v > bestVal) { found = true; bestVal = v; best = item; }
            }
            return best; // default if empty
        }

        public static TSource Min<TSource>(this IEnumerable<TSource> source, Func<TSource, float> selector)
        {
            if (source == null || selector == null) return default(TSource);
            bool found = false;
            TSource best = default(TSource);
            float bestVal = float.PositiveInfinity;
            foreach (var item in source)
            {
                float v = selector(item);
                if (!found || v < bestVal) { found = true; bestVal = v; best = item; }
            }
            return best; // default if empty
        }

        public static List<TSource> Shuffle<TSource>(this IEnumerable<TSource> source)
        {
            var list = source.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                var tmp = list[j];
                list[j] = list[i];
                list[i] = tmp;
            }
            return list;
        }

        public static HashSet<T> OfType<T>(this IEnumerable source)
        {
            var set = new HashSet<T>();
            if (source == null) return set;
            foreach (var obj in source) if (obj is T t) set.Add(t);
            return set;
        }

        // ---- Reflection ----
        public static FieldInfo GetPrivateFieldInfo(this Type type, string fieldName)
        {
            foreach (FieldInfo fi in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                if (fi.Name == fieldName) return fi;
            return null;
        }

        public static FieldInfo GetPrivateFieldInfo(this object obj, string fieldName) =>
            obj?.GetType().GetPrivateFieldInfo(fieldName);

        public static object GetPrivateFieldValue(this object obj, string fieldName)
        {
            var fi = obj.GetPrivateFieldInfo(fieldName);
            return fi == null ? null : fi.GetValue(obj);
        }

        public static void SetPrivateFieldValue(this object obj, string fieldName, object value)
        {
            var fi = obj.GetPrivateFieldInfo(fieldName);
            if (fi != null) fi.SetValue(obj, value);
        }

        public static object CallPrivateMethod(this object obj, string methodName, params object[] args)
        {
            if (obj == null || string.IsNullOrEmpty(methodName)) return null;
            var t = obj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            while (t != null)
            {
                var mi = t.GetMethod(methodName, flags);
                if (mi != null) return mi.Invoke(obj, args);
                t = t.BaseType;
            }
            return null;
        }

        public static Action GetPrivateAction(this object obj, string methodName)
        {
            if (obj == null || string.IsNullOrEmpty(methodName)) return null;
            var t = obj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            while (t != null)
            {
                var mi = t.GetMethod(methodName, flags);
                if (mi != null) return (Action)Delegate.CreateDelegate(typeof(Action), obj, mi);
                t = t.BaseType;
            }
            return null;
        }

        // ---- Game helpers ----
        public static bool IsExists(this BaseNetworkable entity) => entity != null && !entity.IsDestroyed;
        public static bool IsExists(this BaseEntity entity) => entity != null && !entity.IsDestroyed;
        public static bool IsRealPlayer(this BasePlayer player) => player != null && player.userID.IsSteamId();
        public static bool IsEqualVector3(this Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.01f;

        // Sorting helper
        public static List<TSource> OrderByQuickSort<TSource, TKey>(this List<TSource> source, Func<TSource, TKey> keySelector)
            where TKey : IComparable<TKey>
        {
            if (source == null) return null;
            if (keySelector == null) return source;
            source.Sort((a, b) =>
            {
                var ka = keySelector(a);
                var kb = keySelector(b);
                if (ka == null && kb == null) return 0;
                if (ka == null) return -1;
                if (kb == null) return 1;
                return ka.CompareTo(kb);
            });
            return source;
        }
    }
}
