//Reference: Unity.Mathematics
//Reference: Unity.Burst

using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    using Network;
    using TrainHeistExtensions;

    [Info("TrainHeist", "k1lly0u", "0.2.34")]
    class TrainHeist : RustPlugin
    {
        #region Fields  
        [PluginReference] Plugin Kits;

        private Timer eventTimer;

        private HeistTrain activeHeistTrain;

        private NativeArray<float3> overworldTracks;

        private NativeArray<float3> underworldTracks;

        private bool trainTrackDetected = false;

        private List<SpawnPosition> spawnPositions = new List<SpawnPosition>();

        private static Action<bool> OnEventFinishedEvent;

        private static Action<BasePlayer, string> _GiveKit;

        private static readonly HashSet<ulong> HostilePlayers = new HashSet<ulong>();

        #region Constants
        private const string ADMIN_PERMISSION = "trainheist.admin";

        private const string LOCOMOTIVE_PREFAB = "assets/content/vehicles/trains/locomotive/locomotive.entity.prefab";
        private const string WORKCART_PREFAB = "assets/content/vehicles/trains/workcart/workcart.entity.prefab";
        private const string WAGON_A_ENTITY = "assets/content/vehicles/trains/wagons/trainwagona.entity.prefab";
        private const string WAGON_B_ENTITY = "assets/content/vehicles/trains/wagons/trainwagonb.entity.prefab";
        private const string WAGON_C_ENTITY = "assets/content/vehicles/trains/wagons/trainwagonc.entity.prefab";
        private const string WAGON_RESOURCES = "assets/content/vehicles/trains/wagons/trainwagonunloadable.entity.prefab";
        private const string WAGON_LOOT = "assets/content/vehicles/trains/wagons/trainwagonunloadableloot.entity.prefab";

        private const string SCIENTIST_PREFAB = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_cargo_turret_any.prefab";
        private const string SAMSITE_PREFAB = "assets/prefabs/npc/sam_site_turret/sam_static.prefab";
        private const string TURRET_PREFAB = "assets/content/props/sentry_scientists/sentry.bandit.static.prefab";
        private const string HACKABLE_CRATE_PREFAB = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        private const string BRADLEY_PREFAB = "assets/prefabs/npc/m2bradley/bradleyapc.prefab";
        
        private const string TRANSITION_UP = "transition_up";
        private const string TRANSITION_DOWN = "transition_down";
        #endregion
        #endregion

        #region Oxide Hooks
        private void Loaded()
        {
            Message = new Action<BasePlayer, string, object[]>(ChatMessage);

            permission.RegisterPermission(ADMIN_PERMISSION, this);

            OnEventFinishedEvent += OnEventFinished;

            _GiveKit = GiveKit;

            LoadOrCreateLootTable();
            LoadOrCreateLocomotiveLayout();
            LoadOrCreateEngineLayout();
            LoadOrCreateWagonALayout();
            LoadOrCreateWagonBLayout();
            LoadOrCreateWagonCLayout();
            LoadOrCreateWagonResourcesLayout();
            LoadOrCreateWagonLootLayout();
        }

        protected override void LoadDefaultMessages() => lang.RegisterMessages(Messages, this);

        private void OnServerInitialized()
        {
            UnsubscribeFromHooks();

            ServerMgr.Instance.StartCoroutine(FindRailLoops());
        }

        private void GiveKit(BasePlayer player, string kit) => Kits?.Call("GiveKit", player, kit);

        private object CanMountEntity(BasePlayer player, BaseMountable baseMountable)
        {
            if (!activeHeistTrain)
                return null;
            
            TrainCar trainCar = baseMountable.VehicleParent() as TrainCar;
            if (trainCar && activeHeistTrain.IsHeistTrain(trainCar))
            {
                ChatMessage(player, "Notification.ControlsJammed");
                return false;
            }

            return null;
        }

        private object CanEntityTakeDamage(TrainCar trainCar, HitInfo hitInfo)
        {
            if (activeHeistTrain && activeHeistTrain.IsHeistTrain(trainCar))
                return true;
            return null;
        }
        
        private object CanEntityTakeDamage(NPCAutoTurret autoTurret, HitInfo hitInfo)
        {
            if (activeHeistTrain && activeHeistTrain.IsHeistEntity(autoTurret))
                return true;
            return null;
        }

        private void OnEntityTakeDamage(TrainCar trainCar, HitInfo hitInfo)
        {
            if (!activeHeistTrain)
                return;
            
            if (activeHeistTrain && activeHeistTrain.IsHeistTrain(trainCar))
            {
                if (trainCar.healthFraction <= 0.25f || hitInfo.damageTypes.Total() >= trainCar.health || !hitInfo.InitiatorPlayer)
                    hitInfo.damageTypes.Clear();

                if (Configuration.UseHostile && hitInfo.InitiatorPlayer && hitInfo.InitiatorPlayer.userID.IsSteamId())
                    HostilePlayers.Add(hitInfo.InitiatorPlayer.userID);
            }
        }
        
        private void OnEntityTakeDamage(ScientistNPC scientistNpc, HitInfo hitInfo)
        {
            if (!scientistNpc.IsMounted() || !hitInfo.InitiatorPlayer || !activeHeistTrain)
                return;

            TrainCar trainCar = scientistNpc.GetMountedVehicle() as TrainCar;
            if (activeHeistTrain.IsHeistTrain(trainCar))
                hitInfo.damageTypes.ScaleAll(10f);

            if (Configuration.UseHostile && hitInfo.InitiatorPlayer.userID.IsSteamId() && activeHeistTrain.IsHeistEntity(scientistNpc))
                HostilePlayers.Add(hitInfo.InitiatorPlayer.userID);
        }

        private void OnEntityTakeDamage(NPCAutoTurret autoTurret, HitInfo hitInfo)
        {
            if (!activeHeistTrain || !activeHeistTrain.IsHeistEntity(autoTurret))
                return;
            
            if (Configuration.UseHostile && hitInfo.InitiatorPlayer && hitInfo.InitiatorPlayer.userID.IsSteamId())
                HostilePlayers.Add(hitInfo.InitiatorPlayer.userID);
        }

        private void OnEntityTakeDamage(BradleyAPC bradleyApc, HitInfo hitInfo)
        {
            if (!activeHeistTrain || !activeHeistTrain.IsHeistEntity(bradleyApc))
                return;
            
            if (Configuration.UseHostile && hitInfo.InitiatorPlayer && hitInfo.InitiatorPlayer.userID.IsSteamId())
                HostilePlayers.Add(hitInfo.InitiatorPlayer.userID);
        }

        private void OnEntityTakeDamage(SamSite samsite, HitInfo hitInfo)
        {
            if (!activeHeistTrain || !activeHeistTrain.IsHeistEntity(samsite))
                return;
            
            if (Configuration.UseHostile && hitInfo.InitiatorPlayer && hitInfo.InitiatorPlayer.userID.IsSteamId())
                HostilePlayers.Add(hitInfo.InitiatorPlayer.userID);
        }

        private void OnEntityTakeDamage(HackableLockedCrate hackableLockedCrate, HitInfo hitInfo)
        {
            if (!activeHeistTrain || !activeHeistTrain.IsHeistEntity(hackableLockedCrate))
                return;
            
            if (Configuration.UseHostile && hitInfo.InitiatorPlayer && hitInfo.InitiatorPlayer.userID.IsSteamId())
                HostilePlayers.Add(hitInfo.InitiatorPlayer.userID);
        }

        private void OnEntityTakeDamage(PatrolHelicopter baseHelicopter, HitInfo hitInfo)
        {
            if (!activeHeistTrain || !activeHeistTrain.IsHeistEntity(baseHelicopter))
                return;
            
            if (Configuration.UseHostile && hitInfo.InitiatorPlayer && hitInfo.InitiatorPlayer.userID.IsSteamId())
                HostilePlayers.Add(hitInfo.InitiatorPlayer.userID);
        }

        private void OnEntityDeath(ScientistNPC scientistNpc, HitInfo hitInfo)
        {
            if (!activeHeistTrain || !activeHeistTrain.IsHeistEntity(scientistNpc))
                return;
            
            activeHeistTrain.OnScientistNPCKilled(scientistNpc);
        }

        private void OnCrateHack(HackableLockedCrate hackableLockedCrate)
        {
            if (!hackableLockedCrate.HasParent() || !activeHeistTrain || !activeHeistTrain.IsHeistEntity(hackableLockedCrate))
                return;

            activeHeistTrain.OnCrateHackBegin();
        }

        private void OnCrateHackEnd(HackableLockedCrate hackableLockedCrate)
        {
            if (!hackableLockedCrate.HasParent() || !activeHeistTrain || !activeHeistTrain.IsHeistEntity(hackableLockedCrate))
                return;

            activeHeistTrain.OnCrateHackFinished(hackableLockedCrate);
        }

        private object OnEngineStop(TrainEngine trainEngine)
        {
            if (!trainEngine || !activeHeistTrain || !activeHeistTrain.IsHeistTrain(trainEngine))
                return null;
            
            return activeHeistTrain.OnEngineStop();
        }

        private object OnTrainCarUncouple(TrainCar trainCar, BasePlayer player)
        {
            if (!trainCar || !activeHeistTrain || !activeHeistTrain.IsHeistTrain(trainCar))
                return null;

            return false;
        }

        #region Mark Hostile
        private void OnEntityEnter(TriggerParent triggerParent, BasePlayer player)
        {
            if (!activeHeistTrain)
                return;
            
            if (triggerParent.GetComponentInParent<HeistTrainCar>())
                HostilePlayers.Add(player.userID);
        }

        private object OnNpcTarget(ScientistNPC scientistNpc, BasePlayer player)
        {
            if (!activeHeistTrain || !activeHeistTrain.IsHeistEntity(scientistNpc))
                return null;
            
            if (!scientistNpc || !player || !player.userID.IsSteamId())
                return null;

            if (!HostilePlayers.Contains(player.userID))
                return false;

            return null;
        }
        #endregion

        private void Unload()
        {
            foreach (KeyValuePair<ulong, WagonEditor> kvp in m_WagonEditors)
                UnityEngine.Object.DestroyImmediate(kvp.Value);

            Physics.IgnoreLayerCollision((int)Rust.Layer.Vehicle_World, (int)Rust.Layer.Trigger, true);

            OnEventFinishedEvent = null;

            if (activeHeistTrain)
                UnityEngine.Object.Destroy(activeHeistTrain);

            if (overworldTracks.IsCreated)
                overworldTracks.Dispose();

            if (underworldTracks.IsCreated)
                underworldTracks.Dispose();

            HostilePlayers.Clear();

            Configuration = null;
            Message = null;
        }
        #endregion

        #region Functions
        private void StartEventTimer()
        {
            if (!Configuration.Automation.Enabled)
                return;

            eventTimer = timer.In(Random.Range(Configuration.Automation.Minimum, Configuration.Automation.Maximum), () =>
            {
                if (BasePlayer.activePlayerList.Count >= Configuration.Automation.RequiredPlayers)
                {
                    ServerMgr.Instance.StartCoroutine(FindBestSpawnPointAndRunEvent());
                    return;
                }

                StartEventTimer();
            });
        }

        private bool RunEvent(Vector3 desiredPosition, bool isUnderground)
        {
            const float SPACING_DISTANCE = 20f;

            HostilePlayers.Clear();
            spawnPositions.Clear();

            if (TrainTrackSpline.TryFindTrackNear(desiredPosition, 10f, out TrainTrackSpline trainTrack, out float splineDistance))
            {
                TrainTrackSpline currentTrack = trainTrack;

                Vector3 currentPosition = currentTrack.GetPosition(splineDistance);
                Vector3 currentForward = currentTrack.GetTangentCubicHermiteWorld(splineDistance);

                spawnPositions.Add(new SpawnPosition(currentPosition, currentForward));
                
                for (int i = 0; i < Configuration.Train.Layout.Count; i++)
                {
                    TrainTrackSpline.MoveResult result = currentTrack.MoveAlongSpline(splineDistance, currentForward, SPACING_DISTANCE);
                    
                    currentTrack = result.spline;
                    splineDistance = result.distAlongSpline;
                    currentPosition = currentTrack.GetPosition(splineDistance);
                    currentForward = currentTrack.GetTangentCubicHermiteWorld(splineDistance);
                    
                    if (!HasEnoughSpaceToSpawn(currentTrack, currentPosition))
                        return false;
                    
                    spawnPositions.Add(new SpawnPosition(currentPosition, currentForward));
                }
            }
            else return false;

            bool useLocomotive = Configuration.Train.UseLocomotive && !isUnderground;
            
            if (SpawnEntity<HeistTrain>(useLocomotive ? LOCOMOTIVE_PREFAB : WORKCART_PREFAB,
                                        spawnPositions[0].Position, spawnPositions[0].Rotation, out activeHeistTrain, (TrainCar t) => t.frontCoupling = null))
            {
                activeHeistTrain.SetupLayout(useLocomotive ? LocomotiveLayout.Data : EngineLayout.Data, null, null);

                TrainCar lastSpawnedCar = activeHeistTrain.Entity;
                
                for (int i = 1; i < spawnPositions.Count; i++)
                {
                    string prefab = string.Empty;
                    TrainCarLayout layout = null;

                    switch (Configuration.Train.Layout[i - 1])
                    {
                        case "WagonB":
                            prefab = WAGON_B_ENTITY;
                            layout = WagonBLayout.Data;
                            break;
                        case "WagonC":
                            prefab = WAGON_C_ENTITY;
                            layout = WagonCLayout.Data;
                            break;
                        case "WagonResources":
                            prefab = WAGON_RESOURCES;
                            layout = WagonResourcesLayout.Data;
                            break;
                        case "WagonLoot":
                            prefab = WAGON_LOOT;
                            layout = WagonLootLayout.Data;
                            break;
                        default:
                            prefab = WAGON_A_ENTITY;
                            layout = WagonALayout.Data;
                            break;
                    }

                    Action<TrainCar> disableCoupling = i == Configuration.Train.Layout.Count ? new Action<TrainCar>((TrainCar t) => t.rearCoupling = null) : null;

                    if (SpawnEntity<HeistTrainCar>(prefab, spawnPositions[i].Position, spawnPositions[i].Rotation, out HeistTrainCar heistTrainCar, disableCoupling))
                    {
                        heistTrainCar.SetupLayout(layout, activeHeistTrain, lastSpawnedCar);

                        TrainCar trainCar = heistTrainCar.Entity;

                        float distToMove = Vector3Ex.Distance2D(lastSpawnedCar.rearCoupling.position, trainCar.frontCoupling.position);
                        
                        trainCar.MoveFrontWheelsAlongTrackSpline(trainCar.FrontTrackSection, trainCar.FrontWheelSplineDist, distToMove, null, 0);

                        trainCar.coupling.frontCoupling.TryCouple(lastSpawnedCar.coupling.rearCoupling, true);

                        lastSpawnedCar = trainCar;
                    }
                }

                SubscribeToHooks();

                if (Configuration.Automation.BroadcastStart)
                    BroadcastMessage(trainTrack.transform.root.name == "Dungeon" ? "Notification.EventStarted.Underground" : "Notification.EventStarted",
                    MapHelper.PositionToString(activeHeistTrain.transform.position), RotationToCompassHeading(activeHeistTrain.transform));

                Debug.Log($"[TrainHeist] - A heist train has spawned at {desiredPosition}");

                Interface.Call("OnTrainHeistStarted");

                return true;
            }

            return false;
        }
        
        private bool HasEnoughSpaceToSpawn(TrainTrackSpline trainTrackSpline, Vector3 position)
        {
            foreach (TrainTrackSpline.ITrainTrackUser trainTrackUser in trainTrackSpline.trackUsers)
            {
                if (Vector3.SqrMagnitude(trainTrackUser.Position - position) < 144f)
                    return false;
            }

            return true;
        }
        
        private bool SpawnEntity<T>(string prefab, Vector3 position, Quaternion rotation, out T component, Action<TrainCar> preSpawn) where T : HeistTrainCar
        {
            TrainCar trainCar = GameManager.server.CreateEntity(prefab, position, rotation) as TrainCar;
            trainCar.enableSaving = false;

            if (trainCar is TrainEngine trainEngine)
                Configuration.Train.ApplySettingsToEngine(trainEngine);

            preSpawn?.Invoke(trainCar);

            trainCar.platformParentTrigger.ParentNPCPlayers = true;

            Collider[] colliders = trainCar.GetComponentsInChildren<Collider>();
            TerrainCollider terrainCollider = TerrainMeta.Terrain.GetComponent<TerrainCollider>();

            for (int i = 0; i < colliders.Length; i++)
                Physics.IgnoreCollision(colliders[i], terrainCollider, true);

            trainCar.Spawn();

            if (!trainCar || trainCar.IsDestroyed)
            {
                component = default(T);
                return false;
            }

            component = trainCar.gameObject.AddComponent<T>();
            return true;
        }
               

        private struct SpawnPosition
        {
            public Vector3 Position;
            public Vector3 Forward;

            public Quaternion Rotation
            {
                get
                {
                    if (Forward.magnitude == 0f)
                        return Quaternion.identity * Quaternion.Euler(0f, 180f, 0f);

                    return Quaternion.LookRotation(Forward) * Quaternion.Euler(0f, 180f, 0f);
                }
            }

            public SpawnPosition(Vector3 position, Vector3 forward)
            {
                this.Position = position;
                this.Forward = forward;
            }
        }

        private void OnEventFinished(bool noContest)
        {
            if (Configuration.Automation.BroadcastStop)
                BroadcastMessage(noContest ? "Notification.EventFinishedNoContest" : "Notification.EventFinished");

            UnsubscribeFromHooks();
            StartEventTimer();

            HostilePlayers.Clear();
            activeHeistTrain = null;
        }

        private void SubscribeToHooks()
        {
            Subscribe(nameof(CanMountEntity));
            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(CanEntityTakeDamage));
            Subscribe(nameof(OnEntityDeath));
            Subscribe(nameof(OnCrateHack));
            Subscribe(nameof(OnCrateHackEnd));
            Subscribe(nameof(OnEngineStop));
            Subscribe(nameof(OnTrainCarUncouple));
            
            if (Configuration.UseHostile)
                Subscribe(nameof(OnEntityEnter));

            if (Configuration.UseHostile)
                Subscribe(nameof(OnNpcTarget));
        }

        private void UnsubscribeFromHooks()
        {
            if (IsLoaded)
            {
                Unsubscribe(nameof(CanMountEntity));
                Unsubscribe(nameof(OnEntityTakeDamage));
                Unsubscribe(nameof(CanEntityTakeDamage));
                Unsubscribe(nameof(OnEntityDeath));
                Unsubscribe(nameof(OnCrateHack));
                Unsubscribe(nameof(OnCrateHackEnd));
                Unsubscribe(nameof(OnEngineStop));
                Unsubscribe(nameof(OnTrainCarUncouple));
                Unsubscribe(nameof(OnEntityEnter));
                Unsubscribe(nameof(OnNpcTarget));
            }
        }

        private bool IsHeistTrainCar(TrainCar trainCar) => activeHeistTrain && activeHeistTrain.IsHeistTrain(trainCar);

        private static string RotationToCompassHeading(Transform t)
        {
            const string N = "North ";
            const string NE = "North-East ";
            const string E = "East ";
            const string SE = "South-East ";
            const string S = "South ";
            const string SW = "South-West ";
            const string W = "West ";
            const string NW = "North-West ";

            float rotation = t.rotation.eulerAngles.y;
            string heading = string.Empty;

            if (rotation > 337.5 || rotation <= 22.5)
                heading = N;
            else if (rotation > 22.5 && rotation <= 67.5)
                heading = NE;
            else if (rotation > 67.5 && rotation <= 112.5)
                heading = E;
            else if (rotation > 112.5 && rotation <= 157.5)
                heading = SE;
            else if (rotation > 157.5 && rotation <= 202.5)
                heading = S;
            else if (rotation > 202.5 && rotation <= 247.5)
                heading = SW;
            else if (rotation > 247.5 && rotation <= 292.5)
                heading = W;
            else if (rotation > 292.5 && rotation <= 337.5)
                heading = NW;

            return heading;
        }
        #endregion

        #region Best Spawn Point        
        private IEnumerator FindRailLoops()
        {
            Debug.Log("[TrainHeist] - Processing train tracks...");

            List<float3> spawnPoints = Pool.Get<List<float3>>();

            TrainTrackSpline[] array = UnityEngine.Object.FindObjectsOfType<TrainTrackSpline>();
            
            List<TrainTrackSpline> allTrainSplines = Pool.Get<List<TrainTrackSpline>>();
            allTrainSplines.AddRange(array);

            if (Configuration.Train.AllowAboveGround)
            {
                const string TRACK_NAME = "train_track";
                
                #region Track Prefabs
                allTrainSplines.RemoveAll(x =>
                {
                    if (!x || !x.gameObject)
                        return true;
                    
                    if (TrainTrackSpline.SidingSplines.Contains(x))
                        return true;
                            
                    if (!x.gameObject.name.StartsWith(TRACK_NAME))
                        return true;
                    
                    return false;
                });

                if (allTrainSplines.Count > 0)
                {
                    List<List<TrainTrackSpline>> loopedTracks = new List<List<TrainTrackSpline>>();

                    while (allTrainSplines.Count > 0)
                    {
                        TrainTrackSpline beginning = allTrainSplines[allTrainSplines.Count - 1];
                        allTrainSplines.Remove(beginning);

                        if (!beginning.HasNextTrack)
                            continue;

                        List<TrainTrackSpline> currentTrack = Pool.Get<List<TrainTrackSpline>>();
                        currentTrack.Add(beginning);

                        TrainTrackSpline currentSpline = beginning.nextTracks[0].track;

                        bool hasNextTrack = true;
                        bool isForward = true;

                        while (hasNextTrack)
                        {
                            hasNextTrack = isForward ? currentSpline.HasNextTrack : currentSpline.HasPrevTrack;

                            if (!hasNextTrack)
                            {
                                Pool.FreeUnmanaged(ref currentTrack);

                                allTrainSplines.Remove(currentSpline);
                                hasNextTrack = false;
                            }
                            else if (currentSpline == beginning || currentTrack.Contains(currentSpline))
                            {
                                int index = currentTrack.IndexOf(currentSpline);

                                if (!currentTrack.Contains(currentSpline))
                                    currentTrack.Add(currentSpline);

                                if (index >= 0)
                                    currentTrack.RemoveRange(0, index + 1);

                                for (int i = 0; i < currentTrack.Count; i++)
                                    allTrainSplines.Remove(currentTrack[i]);

                                loopedTracks.Add(currentTrack);

                                hasNextTrack = false;
                            }
                            else
                            {
                                currentTrack.Add(currentSpline);

                                Vector3 lastPoint = isForward ? currentSpline.GetEndPointWorld() : currentSpline.GetStartPointWorld();

                                List<TrainTrackSpline.ConnectedTrackInfo> nextTracks = isForward ? currentSpline.nextTracks : currentSpline.prevTracks;
                                for (int i = 0; i < nextTracks.Count; i++)
                                {
                                    TrainTrackSpline.ConnectedTrackInfo connectedTrack = nextTracks[i];
                                    if (TrainTrackSpline.SidingSplines.Contains(connectedTrack.track))
                                        continue;

                                    currentSpline = connectedTrack.track;
                                    break;
                                }

                                isForward = Vector3.Distance(lastPoint, currentSpline.GetStartPointWorld()) < Vector3.Distance(lastPoint, currentSpline.GetEndPointWorld());
                            }
                        }

                        yield return null;
                    }

                    List<float3> splinePoints = Pool.Get<List<float3>>();

                    for (int i = 0; i < loopedTracks.Count; i++)
                    {
                        List<TrainTrackSpline> railLoop = loopedTracks[i];

                        if (railLoop.Count > 1)
                        {
                            for (int y = 0; y < railLoop.Count; y++)
                            {
                                TrainTrackSpline trainTrackSpline = railLoop[y];

                                splinePoints.Clear();
                                PointsToWorld(trainTrackSpline, splinePoints);

                                if (splinePoints.Count > 2)
                                {
                                    for (int j = 1; j < splinePoints.Count; j += 10)
                                    {
                                        spawnPoints.Add(splinePoints[j]);
                                    }
                                }
                                else
                                {
                                    spawnPoints.AddRange(splinePoints);
                                }
                            }
                        }

                        Pool.FreeUnmanaged(ref railLoop);
                    }

                    loopedTracks = null;

                    Pool.FreeUnmanaged(ref splinePoints);
                }
                #endregion

                #region Track Paths
                foreach (PathList pathList in TerrainMeta.Path.Rails)
                {
                    if (pathList.Path.Circular)
                    {
                        int skip = pathList.Path.Points.Length >= 1000 ? 10 : pathList.Path.Points.Length >= 500 ? 5 : 1;
                        for (int i = 0; i < pathList.Path.Points.Length; i += skip)
                        {
                            spawnPoints.Add(pathList.Path.Points[i]);
                        }
                    }
                }
                #endregion
                
                overworldTracks = new NativeArray<float3>(spawnPoints.ToArray(), Allocator.Persistent);
            }

            spawnPoints.Clear();
            allTrainSplines.Clear();
            allTrainSplines.AddRange(array);
            
            if (Configuration.Train.AllowUnderGround)
            {        
                const string TUNNEL_NAME = "train_tunnel";
                allTrainSplines.RemoveAll(x =>
                {
                    if (!x || !x.gameObject)
                        return true;
                    
                    if (!x.gameObject.name.StartsWith(TUNNEL_NAME))
                        return true;   
                    
                    if (!Configuration.Train.AllowTransition && (x.gameObject.name.Contains(TRANSITION_UP, CompareOptions.OrdinalIgnoreCase) || x.gameObject.name.Contains(TRANSITION_DOWN, CompareOptions.OrdinalIgnoreCase)))
                        return true;
                    
                    return false;
                });

                if (allTrainSplines.Count > 0)
                {
                    int skip = allTrainSplines.Count >= 1000 ? 10 : allTrainSplines.Count >= 500 ? 5 : 1;
                    List<float3> splinePoints = Pool.Get<List<float3>>();
                    for (int i = 0; i < allTrainSplines.Count; i+= skip)
                    {
                        TrainTrackSpline trainTrackSpline = allTrainSplines[i];

                        splinePoints.Clear();
                        PointsToWorld(trainTrackSpline, splinePoints);

                        if (splinePoints.Count > 0)
                            spawnPoints.Add(splinePoints.First());
                    }

                    Pool.FreeUnmanaged(ref splinePoints);                    
                }

                underworldTracks = new NativeArray<float3>(spawnPoints.ToArray(), Allocator.Persistent);
            }
            
            Pool.FreeUnmanaged(ref spawnPoints);
            Pool.FreeUnmanaged(ref allTrainSplines);

            if (overworldTracks.Length > 0 || underworldTracks.Length > 0)
            {
                trainTrackDetected = true;

                StartEventTimer();

                Debug.Log($"[TrainHeist] - Train loops detected. Populated with {overworldTracks.Length} above ground and {underworldTracks.Length} under ground spawn points");
            }
            else Debug.LogError($"[TrainHeist] - No rail loops detected on your map. Unable to continue");
        }

        private void PointsToWorld(TrainTrackSpline trainTrackSpline, List<float3> list)
        {
            for (int i = 0; i < trainTrackSpline.points.Length; i++)
            {
                list.Add(trainTrackSpline.transform.TransformPoint(trainTrackSpline.points[i]));
            }
        }

        private IEnumerator FindBestSpawnPointAndRunEvent(Vector3? desiredPosition = null, int iterations = 0)
        {
            NativeArray<float3> tracks;
            NativeArray<float> distanceBuffer;
            NativeArray<float3> positionBuffer;
            
            List<Vector3> overrides = Pool.Get<List<Vector3>>();
            
            int underworldCount = underworldTracks.IsCreated ? underworldTracks.Length : 0;
            int overworldCount = overworldTracks.IsCreated ? overworldTracks.Length : 0;
            
            // Prepare available tracks 
            if (!desiredPosition.HasValue) // Random map spawn
            {
                bool underground = underworldCount > 0 && (overworldCount == 0 || Random.value > 0.5f);

                if (Configuration.Automation.UseSpawnOverrides)
                {
                    foreach (SpawnOverride spawnOverride in Configuration.Automation.Spawns)
                    {
                        if (underground == spawnOverride.Underground)
                            overrides.Add(spawnOverride.Position);
                    }
                }

                tracks = underground ? new NativeArray<float3>(underworldTracks, Allocator.Persistent) : new NativeArray<float3>(overworldTracks, Allocator.Persistent);
            }
            else // Spawn near position
            {
                overrides.Add(desiredPosition.Value);
                
                int totalCount = underworldCount + overworldCount;
                
                tracks = new NativeArray<float3>(totalCount, Allocator.Persistent);
                
                if (underworldCount > 0)
                {
                    for (int i = 0; i < underworldCount; i++)
                        tracks[i] = underworldTracks[i];
                }
                
                if (overworldCount > 0)
                {
                    for (int i = 0; i < overworldCount; i++)
                        tracks[underworldCount + i] = overworldTracks[i];
                }
            }
            
            distanceBuffer = new NativeArray<float>(tracks.Length, Allocator.Persistent);

            bool sortByFurthest = false;
            if ((!Configuration.Automation.UseSpawnOverrides && !desiredPosition.HasValue) || overrides.Count == 0)
            {
                // If no overrides, prepare avoid positions (players and other trains)
                // to find the furthest spawn point from a avoided position
                sortByFurthest = true;

                List<float3> avoidPositions = Pool.Get<List<float3>>();

                foreach (BaseMountable baseMountable in BaseMountable.AllMountables)
                {
                    if (baseMountable is TrainCar)
                        avoidPositions.Add(baseMountable.transform.position);
                }
                
                foreach (BasePlayer basePlayer in BasePlayer.activePlayerList)
                    avoidPositions.Add(basePlayer.transform.position);
                
                yield return null;

                positionBuffer = new NativeArray<float3>(avoidPositions.Count, Allocator.Persistent);
                
                for (int i = 0; i < avoidPositions.Count; i++)
                    positionBuffer[i] = avoidPositions[i];

                Pool.FreeUnmanaged(ref avoidPositions);

                yield return null;
            }
            else 
            {
                // Otherwise, set the position buffer to the overrides to find the closest spawn point
                positionBuffer = new NativeArray<float3>(overrides.Count, Allocator.Persistent);

                for (int i = 0; i < overrides.Count; i++)
                    positionBuffer[i] = overrides[i];

                yield return null;                
            }
            
            JobHandle jobHandle = RunTrackSelectionJob(distanceBuffer, tracks, positionBuffer, sortByFurthest);

            yield return new WaitUntil(() => jobHandle.IsCompleted);

            //DebugDrawPointsAndDistances(tracks, distanceBuffer);
            
            yield return ServerMgr.Instance.StartCoroutine(LoopPositionsAndRunEvent(tracks));
            
            positionBuffer.Dispose();
            distanceBuffer.Dispose();
            tracks.Dispose();
            Pool.FreeUnmanaged(ref overrides);
        }

        private void DebugDrawPointsAndDistances(NativeArray<float3> tracks, NativeArray<float> distanceBuffer)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                for (var index = 0; index < tracks.Length; index++)
                {
                    Vector3 pos = tracks[index];
                    player.SendConsoleCommand("ddraw.text", 30, Color.green, pos + new Vector3(0, 1.5f, 0), $"<size=20>{distanceBuffer[index]}</size>");
                    player.SendConsoleCommand("ddraw.sphere", 30, Color.blue, pos, 1f);
                }
            }
        }

        private JobHandle RunTrackSelectionJob(NativeArray<float> distanceBuffer, NativeArray<float3> tracks, NativeArray<float3> positionBuffer, bool furthestDistance)
        {
            JobHandle jobHandle = new EvaluateDistanceJob
            {
                distanceBuffer = distanceBuffer,
                tracks = tracks,
                avoidPositions = positionBuffer
            }.Schedule(distanceBuffer.Length, 4);

            jobHandle = new SortByDistanceJob
            {
                distances = distanceBuffer,
                tracks = tracks,
                furthestDistance = furthestDistance
            }.Schedule(jobHandle);

            return jobHandle;
        }

        private IEnumerator LoopPositionsAndRunEvent(NativeArray<float3> positions)
        {
            int num = Mathf.Min(positions.Length, 5);

            for (int i = 0; i < num; i++)
            {
                float3 position = positions[i];
                
                if (RunEvent(position, position.y < 0f))
                    yield break;
            }
            
            StartEventTimer();
            Debug.Log(num == 0 ? "[TrainHeist] - No valid spawn points found." : $"[TrainHeist] - Failed to find a appropriate spawn point after {num} attempts.");
        }

        [BurstCompile]
        private struct EvaluateDistanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> tracks;
            [ReadOnly] public NativeArray<float3> avoidPositions;

            public NativeArray<float> distanceBuffer;

            public void Execute(int index)
            {
                float distance = float.MaxValue;

                for (int i = 0; i < avoidPositions.Length; i++)
                {
                    float d = math.distance(tracks[index], avoidPositions[i]);
                    if (d < distance)
                        distance = d;
                }

                distanceBuffer[index] = distance;
            }
        }


        [BurstCompile]
        private struct SortByDistanceJob : IJob
        {
            public NativeArray<float> distances;            
            public NativeArray<float3> tracks;
            public bool furthestDistance;

            public void Execute()
            {
                for (int index = 0; index < distances.Length; index++)
                {
                    for (int j = index + 1; j < distances.Length; j++)
                    {
                        if (furthestDistance ? distances[index] < distances[j] : distances[index] > distances[j])
                        {
                            float temp1 = distances[index];
                            float3 temp2 = tracks[index];

                            distances[index] = distances[j];
                            tracks[index] = tracks[j];
                            distances[j] = temp1;
                            tracks[j] = temp2;
                        }
                    }
                }
            }
        }
        #endregion

        #region Components
        private class HeistTrainCar : MonoBehaviour
        {
            public virtual TrainCar Entity { get; set; }

            public TrainCarLayout layout;

            private HeistTrain parent;

            private TrainCar coupledTo;

            public void SetupLayout(TrainCarLayout trainCarLayout, HeistTrain parent, TrainCar coupledTo)
            {
                this.layout = trainCarLayout;
                this.coupledTo = coupledTo;
                this.parent = parent;

                if (parent)
                    parent.RegisterChildCar(this);

                Entity = GetComponent<TrainCar>();
                Entity.Invoke(DelayedStart, 0.5f);

                Entity.InvokeRandomized(HealthRegeneration, 1f, 1f, 0.1f);
            }

            private void Update()
            {
                if (!coupledTo || coupledTo.IsDestroyed)
                    return;

                if (!Entity.coupling.frontCoupling.IsCoupled)
                    Entity.coupling.frontCoupling.TryCouple(coupledTo.coupling.rearCoupling, true);
            }

            protected virtual void DelayedStart()
            {
                layout.Loot.Spawn(parent, Entity);
                layout.NPC.Spawn(parent, Entity);
                layout.Turret.Spawn(parent, Entity);
                layout.APC.Spawn(parent, Entity);

                if (!IsUnderground())
                    layout.SamSite.Spawn(parent, Entity);
            }
            
            public virtual void OnCrateHackBegin()
            {
                if (parent)
                    parent.OnCrateHackBegin();
            }

            public virtual void OnCrateHackFinished(HackableLockedCrate hackableLockedCrate)
            {
                if (parent)
                    parent.OnCrateHackFinished(hackableLockedCrate);
            }

            public virtual void OnScientistNPCKilled(ScientistNPC scientistNpc)
            {
                if (parent)
                    parent.OnScientistNPCKilled(scientistNpc);
            }

            private void HealthRegeneration()
            {
                if (!Entity || Entity.IsDestroyed)
                    return;

                if (Entity.health >= Entity.MaxHealth())
                    return;

                Entity.Heal(Configuration.Train.HealthRegen);
            }

            private bool IsCoupledBackwards(int trainCarIndex)
            {
                if (Entity.completeTrain.trainCars.Count == 1 || trainCarIndex < 0 || trainCarIndex > Entity.completeTrain.trainCars.Count - 1)                
                    return false;
                
                TrainCar trainCar = Entity.completeTrain.trainCars[trainCarIndex];
                if (trainCarIndex == 0)                
                    return trainCar.coupling.IsFrontCoupled;
                
                TrainCoupling coupledTo = trainCar.coupling.frontCoupling.CoupledTo;
                return coupledTo == null || coupledTo.owner != Entity.completeTrain.trainCars[trainCarIndex - 1];
            }

            public bool IsUnderground() => Entity.FrontTrackSection.transform.root.name == "Dungeon";
        }

        private class HeistTrain : HeistTrainCar
        {
            public List<LootContainer> lootContainers = Pool.Get<List<LootContainer>>();
            public List<AutoTurret> autoTurrets = Pool.Get<List<AutoTurret>>();
            public List<SamSite> samSites = Pool.Get<List<SamSite>>();
            public List<ScientistNPC> npcPlayers = Pool.Get<List<ScientistNPC>>();
            public List<BradleyAPC> apcs = Pool.Get<List<BradleyAPC>>();

            private List<HeistTrainCar> trainCars = Pool.Get<List<HeistTrainCar>>();
            private List<LootContainer> hackedCrates = Pool.Get<List<LootContainer>>();

            private TrainEngine.EngineSpeeds desiredSpeed = TrainEngine.EngineSpeeds.Fwd_Med;

            private PatrolHelicopter activeChaseHeli;
            private int totalHelicoptersSpawned;

            private bool hackingBegan = false;
            private bool driverIsDead = false;

            private bool despawnNoContest = false;

            private int selfDestructCountdown;

            private static readonly BasePlayer[] PLAYER_BUFFER = new BasePlayer[128];

            private static readonly List<DamageTypeEntry> RADIAL_DAMAGE_LIST = new List<DamageTypeEntry>
            {
                new DamageTypeEntry {amount = 75f, type = DamageType.Explosion}
            };
            
            public bool IsHeistTrain(TrainCar trainCar) => trainCars.Any(x => x.Entity == trainCar);
            
            public bool IsHeistEntity(LootContainer lootContainer) => lootContainers.Contains(lootContainer);
            
            public bool IsHeistEntity(ScientistNPC scientistNpc) => npcPlayers.Contains(scientistNpc);
            
            public bool IsHeistEntity(AutoTurret autoTurret) => autoTurrets.Contains(autoTurret);
            
            public bool IsHeistEntity(SamSite samSite) => samSites.Contains(samSite);
            
            public bool IsHeistEntity(BradleyAPC bradleyAPC) => apcs.Contains(bradleyAPC);
            
            public bool IsHeistEntity(PatrolHelicopter patrolHelicopter) => activeChaseHeli == patrolHelicopter;
            
            public void RegisterChildCar(HeistTrainCar heistTrainCar)
            {
                trainCars.Add(heistTrainCar);
            }

            protected override void DelayedStart()
            {
                layout.Loot.Spawn(this, Entity);
                layout.NPC.Spawn(this, Entity);
                layout.Turret.Spawn(this, Entity);
                layout.APC.Spawn(this, Entity);
                layout.SamSite.Spawn(this, Entity);

                Entity.InvokeRandomized(ReverseIfHazardAhead, 1f, 1f, 0.05f);
                Entity.InvokeRandomized(CheckNightLights, 0f, 5f, 0.5f);
                Entity.InvokeRandomized(CheckRefreshFuel, 0f, 5f, 0.5f);

                EntityFuelSystem fuelSystem = Entity.GetFuelSystem() as EntityFuelSystem;
                fuelSystem?.GetFuelContainer().SetFlag(BaseEntity.Flags.Locked, true);

                if (Configuration.Train.DespawnSeconds > 0)
                    Entity.Invoke(Despawn, Configuration.Train.DespawnSeconds);

                desiredSpeed = GetDesiredSpeed(true);
                
                EnsureEngineRunning();
            }

            private void EnsureEngineRunning()
            {
                if (Entity is not TrainEngine engine) 
                    return;

                if (!Entity.HasFlag(BaseEntity.Flags.On))
                {
                    engine.SetFlag(BaseEntity.Flags.On, true, false, true);
                    engine.SetFlag(engine.engineController.engineStartingFlag, false, false, true);
                    
                    Entity.Invoke(EnsureEngineRunning, 2f);
                }
            }

            private void Despawn()
            {
                if (selfDestructCountdown != 0)
                    return;

                int count = BaseEntity.Query.Server.PlayerGrid.Query(transform.position.x, transform.position.z, 150f, PLAYER_BUFFER, (BasePlayer player) => !player.IsNpc);

                if (count == 0)
                {
                    despawnNoContest = true;
                    Destroy(this);
                }
                else Entity.Invoke(Despawn, 30f);
            }

            private void OnDestroy()
            {
                DespawnEntities();

                if (!despawnNoContest && activeChaseHeli && !activeChaseHeli.IsDestroyed)
                    activeChaseHeli.Kill(BaseNetworkable.DestroyMode.None);

                if (Entity && !Entity.IsDestroyed)
                {
                    Entity.CancelInvoke(ReverseIfHazardAhead);
                    Entity.CancelInvoke(CheckNightLights);
                    Entity.CancelInvoke(CheckRefreshFuel);

                    Entity.Kill(BaseNetworkable.DestroyMode.None);
                }

                for (int i = 0; i < trainCars.Count; i++)
                {
                    TrainCar heistTrainCar = trainCars[i].Entity;
                    if (!heistTrainCar || heistTrainCar.IsDestroyed)
                        continue;

                    heistTrainCar.Kill(BaseNetworkable.DestroyMode.None);
                }

                Pool.FreeUnmanaged(ref lootContainers);
                Pool.FreeUnmanaged(ref hackedCrates);
                Pool.FreeUnmanaged(ref npcPlayers);
                Pool.FreeUnmanaged(ref autoTurrets);
                Pool.FreeUnmanaged(ref samSites);
                Pool.FreeUnmanaged(ref apcs);
                Pool.FreeUnmanaged(ref trainCars);

                OnEventFinishedEvent?.Invoke(despawnNoContest);

                Interface.Call("OnTrainHeistFinished");
            }

            private void DespawnEntities()
            {
                for (int i = 0; i < lootContainers.Count; i++)
                {
                    LootContainer lootContainer = lootContainers[i];
                    if (!lootContainer || lootContainer.IsDestroyed)
                        continue;
                    
                    lootContainer.inventory.Clear();
                    lootContainer.Kill(BaseNetworkable.DestroyMode.None);
                }

                for (int i = 0; i < npcPlayers.Count; i++)
                {
                    ScientistNPC scientistNpc = npcPlayers[i];
                    if (!scientistNpc || scientistNpc.IsDestroyed)
                        continue;
                    
                    if (scientistNpc.IsMounted())
                        scientistNpc.GetMounted().DismountPlayer(scientistNpc, true);

                    scientistNpc.inventory.Strip();
                    scientistNpc.Kill(BaseNetworkable.DestroyMode.None);
                }

                for (int i = 0; i < autoTurrets.Count; i++)
                {
                    AutoTurret autoTurret = autoTurrets[i];
                    if (!autoTurret || autoTurret.IsDestroyed)
                        continue;
                    
                    autoTurret.Kill(BaseNetworkable.DestroyMode.None);
                }

                for (int i = 0; i < samSites.Count; i++)
                {
                    SamSite samSite = samSites[i];
                    if (!samSite || samSite.IsDestroyed)
                        continue;
                    
                    samSite.Kill(BaseNetworkable.DestroyMode.None);
                }

                for (int i = 0; i < apcs.Count; i++)
                {
                    BradleyAPC bradleyAPC = apcs[i];
                    if (!bradleyAPC || bradleyAPC.IsDestroyed)
                        continue;
                    
                    bradleyAPC.Kill(BaseNetworkable.DestroyMode.None);
                }

                lootContainers.Clear();
                npcPlayers.Clear();
                apcs.Clear();
                autoTurrets.Clear();
                samSites.Clear();
            }

            #region Locked Crates
            private float maxHackTime;

            public void RegisterLockableCrate(HackableLockedCrate hackableLockedCrate, float maxHackTime)
            {
                lootContainers.Add(hackableLockedCrate);

                this.maxHackTime = Mathf.Max(this.maxHackTime, maxHackTime);
            }

            public override void OnCrateHackBegin()
            {
                if (hackingBegan)
                {
                    if (Entity.IsInvoking(Despawn))
                    {
                        Entity.CancelInvoke(Despawn);
                        Entity.Invoke(Despawn, Configuration.Train.DespawnSeconds + maxHackTime);
                    }

                    return;
                }

                if (Configuration.Helicopter.Amount > 0 && !IsUnderground())
                {
                    activeChaseHeli = Configuration.Helicopter.Spawn(this);
                    totalHelicoptersSpawned++;

                    NotifyHackStarted();
                }

                if (Configuration.Train.DespawnSeconds > 0)
                {
                    Entity.CancelInvoke(Despawn);
                    Entity.Invoke(Despawn, Configuration.Train.DespawnSeconds + maxHackTime);
                }

                hackingBegan = true;
            }

            public override void OnCrateHackFinished(HackableLockedCrate hackableLockedCrate)
            {
                if (!hackedCrates.Contains(hackableLockedCrate))
                {
                    hackedCrates.Add(hackableLockedCrate);
                    if (hackedCrates.Count == lootContainers.Count)
                    {
                        Entity.CancelInvoke(ReverseIfHazardAhead);
                        Entity.CancelInvoke(Despawn);

                        (Entity as TrainEngine).SetThrottle(desiredSpeed = TrainEngine.EngineSpeeds.Zero);

                        selfDestructCountdown = Configuration.Train.SelfDestructSeconds;

                        SelfDestructCountdown();

                        int count = BaseEntity.Query.Server.PlayerGrid.Query(transform.position.x, transform.position.z, 30f, PLAYER_BUFFER, (BasePlayer player) => !player.IsNpc);

                        for (int i = 0; i < count; i++)
                        {
                            BasePlayer player = PLAYER_BUFFER[i];
                            if (!player)
                                continue;

                            Message(player, "Notification.SelfDestruct", new object[] { Configuration.Train.SelfDestructSeconds });
                        }
                    }
                }
            }

            private void NotifyHackStarted()
            {
                int count = BaseEntity.Query.Server.PlayerGrid.Query(transform.position.x, transform.position.z, 25f, PLAYER_BUFFER, (BasePlayer player) => !player.IsNpc);

                for (int i = 0; i < count; i++)
                {
                    BasePlayer player = PLAYER_BUFFER[i];
                    if (!player)
                        continue;

                    Message(player, "Notification.BackupInbound", null);
                }

                object[] param = new object[] { MapHelper.PositionToString(transform.position), RotationToCompassHeading(transform) };
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (PLAYER_BUFFER.Contains(player, count))
                        continue;

                    Message(player, "Notification.HeistStarted", param);
                }
            }
            #endregion

            #region Self Destruct
            private void SelfDestructCountdown()
            {
                if (selfDestructCountdown > 0)
                {
                    const string BEEP_PREFAB = "assets/prefabs/locks/keypad/effects/lock.code.unlock.prefab";
                    Effect.server.Run(BEEP_PREFAB, transform.position + (Vector3.up * 1.5f));

                    selfDestructCountdown--;

                    Entity.Invoke(SelfDestructCountdown, 1f);
                }
                else
                {
                    DespawnEntities();

                    for (int i = trainCars.Count - 1; i >= 0; i--)
                    {
                        HeistTrainCar heistTrainCar = trainCars[i];
                        if (!heistTrainCar || !heistTrainCar.Entity || heistTrainCar.Entity.IsDestroyed)
                            continue;

                        PerformExplosionEffects(heistTrainCar.transform, (i - trainCars.Count - 1) * 0.5f);
                    }

                    PerformExplosionEffects(transform, (trainCars.Count - 1) * 0.5f);
                }
            }

            private void PerformExplosionEffects(Transform transform, float timeOffset)
            {
                const string C4_EXPLOSION = "assets/prefabs/tools/c4/effects/c4_explosion.prefab";

                Entity.Invoke(() => Effect.server.Run(C4_EXPLOSION, transform.position + (Vector3.up * 1.5f)), timeOffset);
                Entity.Invoke(() => Effect.server.Run(C4_EXPLOSION, transform.position + (Vector3.up * 1.5f) + (transform.forward * 3f)), timeOffset + 0.1f);
                Entity.Invoke(() => Effect.server.Run(C4_EXPLOSION, transform.position + (Vector3.up * 1.5f) + (-transform.forward * 3f)), timeOffset + Random.Range(0.2f, 0.8f));
                Entity.Invoke(DieAndDamageNearbyPlayers, timeOffset + 1.2f);
            }

            private void DieAndDamageNearbyPlayers()
            {
                const string HELI_EXPLOSION = "assets/prefabs/npc/patrol helicopter/effects/heli_explosion.prefab";

                Effect.server.Run(HELI_EXPLOSION, transform.position + (Vector3.up * 1.5f));

                DamageUtil.RadiusDamage(Entity, Entity, transform.position, 5f, 15f, RADIAL_DAMAGE_LIST, 164096, true);

                Entity.Die(new HitInfo(Entity, Entity, DamageType.Explosion, 1000f));
            }
            #endregion

            public void OnChaseHelicopterDowned()
            {
                activeChaseHeli = null;

                if (totalHelicoptersSpawned < Configuration.Helicopter.Amount)
                {
                    activeChaseHeli = Configuration.Helicopter.Spawn(this);
                    totalHelicoptersSpawned++;
                }

                if (Configuration.Train.DespawnSeconds > 0)
                {
                    Entity.CancelInvoke(Despawn);
                    Entity.Invoke(Despawn, Configuration.Train.DespawnSeconds);
                }
            }

            public override void OnScientistNPCKilled(ScientistNPC scientistNpc)
            {
                npcPlayers.Remove(scientistNpc);

                if (Entity.IsDriver(scientistNpc))
                {
                    driverIsDead = true;

                    if (Configuration.Train.StopOnDriverDeath)
                        (Entity as TrainEngine).SetThrottle(desiredSpeed = TrainEngine.EngineSpeeds.Zero);

                    Entity.CancelInvoke(ReverseIfHazardAhead);
                }

                if (!hackingBegan && npcPlayers.Count == 0)
                    OnCrateHackBegin();

                if (Configuration.Train.DespawnSeconds > 0 && !Entity.IsInvoking(Despawn))                
                    Entity.Invoke(Despawn, Configuration.Train.DespawnSeconds);                
            }

            public object OnEngineStop()
            {
                if (driverIsDead && Configuration.Train.StopOnDriverDeath)
                    return null;

                return false;
            }

            private bool CheckForHazards(float trackSpeed, float maxHazardDistance, bool checkForward)
            {                
                if (checkForward)
                    return HasValidHazardWithin(Entity.FrontTrackSection, Entity, Entity.FrontWheelSplineDist, 20f, maxHazardDistance, Entity.localTrackSelection, trackSpeed, Entity.RearTrackSection, null);
                
                Vector3 dir = (trackSpeed >= 0f) ? -transform.forward : transform.forward;

                TrainCar lastCar = trainCars.Last().Entity;
                Vector3 rearPosition = lastCar.GetRearOfTrainPos();

                lastCar.RearTrackSection.GetDistance(rearPosition, 1f, out float rearSplineDist);

                bool movingForward = lastCar.RearTrackSection.IsForward(dir, rearSplineDist);
                return HasValidHazardWithin(lastCar.RearTrackSection, lastCar, dir, rearSplineDist, 20f, maxHazardDistance, Entity.localTrackSelection, movingForward, null, null);
                
            }
                        
            private int zeroSpeedCount = 0;

            private void ReverseIfHazardAhead()
            {
                if (!Configuration.Train.StopOnDriverDeath && !Entity.HasDriver())
                    return;

                float trackSpeed = Entity.GetTrackSpeed();

                if (trackSpeed == 0)
                {
                    zeroSpeedCount++;
                    if (zeroSpeedCount > 3)
                    {
                        desiredSpeed = GetDesiredSpeed((int)(Entity as TrainEngine).CurThrottleSetting >= 3 ? false : true);
                        (Entity as TrainEngine).SetThrottle(desiredSpeed);
                        zeroSpeedCount = 0;
                        return;
                    }
                }
                else zeroSpeedCount = 0;

                float maxHazardDistance = Mathf.Lerp(40f, 160f, Mathf.Abs(trackSpeed) * 0.05f);

                if (CheckForHazards(trackSpeed, maxHazardDistance, true))
                {
                    if (CheckForHazards(trackSpeed, maxHazardDistance, false))
                    {
                        desiredSpeed = GetFastestSpeed((int)Entity.GetTrackSpeed() >= 0 ? true : false);
                    }
                    else desiredSpeed = GetDesiredSpeed((int)Entity.GetTrackSpeed() >= 0 ? false : true);
                }
                else DeterminePreferredTrack();

                if ((Entity as TrainEngine).CurThrottleSetting != desiredSpeed)
                    (Entity as TrainEngine).SetThrottle(desiredSpeed);
            }

            private void DeterminePreferredTrack()
            {
                float trackSpeed = Entity.GetTrackSpeed();
                float maxHazardDist = Mathf.Lerp(40f, 325f, Mathf.Abs(trackSpeed) * 0.05f);

                Vector3 askerForward = (trackSpeed >= 0f) ? Entity.transform.forward : (-Entity.transform.forward);
                bool movingForward = Entity.FrontTrackSection.IsForward(askerForward, Entity.FrontWheelSplineDist);

                if (HasValidHazardWithin(Entity.FrontTrackSection, Entity, askerForward, Entity.FrontWheelSplineDist, 20f, maxHazardDist, TrainTrackSpline.TrackSelection.Left, movingForward, Entity.RearTrackSection, null))
                    Entity.SetTrackSelection(TrainTrackSpline.TrackSelection.Right);
                else if (HasValidHazardWithin(Entity.FrontTrackSection, Entity, askerForward, Entity.FrontWheelSplineDist, 20f, maxHazardDist, TrainTrackSpline.TrackSelection.Right, movingForward, Entity.RearTrackSection, null))
                    Entity.SetTrackSelection(TrainTrackSpline.TrackSelection.Left);

                else
                {
                    if (IsUnderground())
                        Entity.SetTrackSelection(Random.value <= 0.5f ? TrainTrackSpline.TrackSelection.Left : TrainTrackSpline.TrackSelection.Right);
                    else Entity.SetTrackSelection(TrainTrackSpline.TrackSelection.Default);
                }
            }

            #region Hazards
            public bool HasValidHazardWithin(TrainTrackSpline trainTrackSpline, TrainCar asker, float askerSplineDist, float minHazardDist, float maxHazardDist, TrainTrackSpline.TrackSelection trackSelection, float trackSpeed, TrainTrackSpline preferredAltA, TrainTrackSpline preferredAltB)
            {
                Vector3 askerForward = (trackSpeed >= 0f) ? asker.transform.forward : (-asker.transform.forward);
                bool movingForward = trainTrackSpline.IsForward(askerForward, askerSplineDist);
                return HasValidHazardWithin(trainTrackSpline, asker, askerForward, askerSplineDist, minHazardDist, maxHazardDist, trackSelection, movingForward, preferredAltA, preferredAltB);
            }

            public bool HasValidHazardWithin(TrainTrackSpline trainTrackSpline, TrainTrackSpline.ITrainTrackUser asker, Vector3 askerForward, float askerSplineDist, float minHazardDist, float maxHazardDist, TrainTrackSpline.TrackSelection trackSelection, bool movingForward, TrainTrackSpline preferredAltA, TrainTrackSpline preferredAltB)
            {
                if (!Configuration.Train.AllowTransition && 
                    (trainTrackSpline.gameObject.name.Contains(TRANSITION_UP, CompareOptions.OrdinalIgnoreCase) || 
                     trainTrackSpline.gameObject.name.Contains(TRANSITION_DOWN, CompareOptions.OrdinalIgnoreCase)))
                {
                    return true;
                }
                
                WorldSplineData data = trainTrackSpline.GetData();
                foreach (TrainTrackSpline.ITrainTrackUser trainTrackUser in trainTrackSpline.trackUsers)
                {
                    if (trainTrackUser == asker)
                        continue;

                    if (trainTrackUser is TrainCar car && (asker as TrainCar).completeTrain.trainCars.Contains(car))
                        continue;

                    if (trainTrackUser is HittableByTrains)
                        continue;

                    Vector3 rhs = trainTrackUser.Position - asker.Position;
                    if (Vector3.Dot(askerForward, rhs) >= 0f)
                    {
                        float magnitude = rhs.magnitude;
                        if (magnitude > minHazardDist && magnitude < maxHazardDist)
                        {
                            Vector3 worldVelocity = trainTrackUser.GetWorldVelocity();
                            if (worldVelocity.sqrMagnitude < 4f || Vector3.Dot(worldVelocity, rhs) < 0f)
                            {
                                return true;
                            }
                        }
                    }
                }

                float minDist = movingForward ? (askerSplineDist + minHazardDist) : (askerSplineDist - minHazardDist);
                float maxDist = movingForward ? (askerSplineDist + maxHazardDist) : (askerSplineDist - maxHazardDist);
                if (maxDist < 0f)
                {
                    if (trainTrackSpline.HasPrevTrack)
                    {
                        TrainTrackSpline.ConnectedTrackInfo trackSelection2 = trainTrackSpline.GetTrackSelection(
                            trainTrackSpline.prevTracks, trainTrackSpline.straightestPrevIndex, false, movingForward, new TrainTrackSpline.TrackRequest(trackSelection, preferredAltA, preferredAltB));
                        if (trackSelection2.orientation == TrainTrackSpline.TrackOrientation.Same)
                        {
                            askerSplineDist = trackSelection2.track.GetLength();
                        }
                        else
                        {
                            askerSplineDist = 0f;
                            movingForward = !movingForward;
                        }
                        float minHazardDist2 = Mathf.Max(-minDist, 0f);
                        float maxHazardDist2 = -maxDist;
                        return HasValidHazardWithin(trackSelection2.track, asker, askerForward, askerSplineDist, minHazardDist2, maxHazardDist2, trackSelection, movingForward, preferredAltA, preferredAltB);
                    }
                }
                else if (maxDist > data.Length && trainTrackSpline.HasNextTrack)
                {
                    TrainTrackSpline.ConnectedTrackInfo trackSelection3 = trainTrackSpline.GetTrackSelection(
                        trainTrackSpline.nextTracks, trainTrackSpline.straightestNextIndex, true, movingForward, new TrainTrackSpline.TrackRequest(trackSelection, preferredAltA, preferredAltB));
                    if (trackSelection3.orientation == TrainTrackSpline.TrackOrientation.Same)
                    {
                        askerSplineDist = 0f;
                    }
                    else
                    {
                        askerSplineDist = trackSelection3.track.GetLength();
                        movingForward = !movingForward;
                    }

                    float minHazardDist3 = Mathf.Max(minDist - data.Length, 0f);
                    float maxHazardDist3 = maxDist - data.Length;

                    if (trackSelection3.track.gameObject.name == "train_tunnel_stop_transition")
                        return true;

                    return !trackSelection3.track.HasPrevTrack || !trackSelection3.track.HasNextTrack || HasValidHazardWithin(trackSelection3.track, asker, askerForward, askerSplineDist, minHazardDist3, maxHazardDist3, trackSelection, movingForward, preferredAltA, preferredAltB);
                }
                return false;
            }
            #endregion

            private TrainEngine.EngineSpeeds GetDesiredSpeed(bool forward)
            {
                switch (Configuration.Train.Speed)
                {
                    case ConfigData.TrainSettings.TravelSpeed.Low:
                        return forward ? TrainEngine.EngineSpeeds.Fwd_Lo : TrainEngine.EngineSpeeds.Rev_Lo;
                    case ConfigData.TrainSettings.TravelSpeed.Medium:
                        return forward ? TrainEngine.EngineSpeeds.Fwd_Med : TrainEngine.EngineSpeeds.Rev_Med;
                    case ConfigData.TrainSettings.TravelSpeed.High:
                        return forward ? TrainEngine.EngineSpeeds.Fwd_Hi : TrainEngine.EngineSpeeds.Rev_Hi;
                }

                return TrainEngine.EngineSpeeds.Zero;
            }

            private TrainEngine.EngineSpeeds GetFastestSpeed(bool forward) => forward? TrainEngine.EngineSpeeds.Fwd_Hi : TrainEngine.EngineSpeeds.Rev_Hi;

            private void CheckNightLights()
            {
                bool lightsOn = Entity.HasFlag(BaseEntity.Flags.Reserved5);

                if ((TOD_Sky.Instance.IsNight || IsUnderground()) && !lightsOn)
                    Entity.SetFlag(BaseEntity.Flags.Reserved5, true, false, true);
                else if (TOD_Sky.Instance.IsDay && !IsUnderground() && lightsOn)
                    Entity.SetFlag(BaseEntity.Flags.Reserved5, false, false, true);
            }

            private void CheckRefreshFuel()
            {
                TrainEngine trainEngine = Entity as TrainEngine;
                if (!trainEngine || trainEngine.IsDestroyed)
                    return;

                IFuelSystem fuelSystem = trainEngine.engineController.FuelSystem;
                if (fuelSystem == null)
                    return;
                
                if (fuelSystem.GetFuelAmount() <= 50)
                    fuelSystem.AddFuel(200);
            }
        }

        private class ChaseHelicopterAI : MonoBehaviour
        {
            public PatrolHelicopter Entity { get; private set; }

            public PatrolHelicopterAI AI { get; private set; }

            public HeistTrain Target { get; set; }

            private List<BasePlayer> visPlayers = Pool.Get<List<BasePlayer>>();

            private bool notifiedDeath = false;

            private float lastThinkTime;

            private void Awake()
            {
                Entity = GetComponent<PatrolHelicopter>();
                AI = GetComponent<PatrolHelicopterAI>();

                AI.enabled = false;
            }

            private void OnDestroy()
            {
                Pool.FreeUnmanaged(ref visPlayers);
            }

            public void OnEventFinished() => Entity.Invoke(ReturnToPatrol, 60f);

            private void ReturnToPatrol()
            {
                AI.enabled = true;
                Destroy(this);
            }

            private void Update()
            {
                if (!Target || Target.Entity.IsDestroyed)
                {
                    Entity.CancelInvoke(ReturnToPatrol);
                    ReturnToPatrol();
                    return;
                }

                UpdateTargetList();

                AI.MoveToDestination();
                AI.UpdateRotation();
                AI.UpdateSpotlight();
                AIThink();
                AI.DoMachineGuns();
            }

            private void AIThink()
            {
                float time = AI.GetTime();
                float delta = time - lastThinkTime;

                lastThinkTime = time;

                switch (AI._currentState)
                {
                    case PatrolHelicopterAI.aiState.MOVE:
                        {
                            AI.State_Move_Think(delta);
                            return;
                        }
                    case PatrolHelicopterAI.aiState.ORBIT:
                        {
                            AI.State_Orbit_Think(delta);
                            return;
                        }
                    case PatrolHelicopterAI.aiState.STRAFE:
                        {
                            AI.State_Strafe_Think(delta);
                            return;
                        }
                    case PatrolHelicopterAI.aiState.PATROL:
                        {
                            AI.State_Patrol_Think(delta);
                            return;
                        }
                    case PatrolHelicopterAI.aiState.DEATH:
                        {
                            if (!notifiedDeath)
                            {
                                notifiedDeath = true;
                                Target.OnChaseHelicopterDowned();
                            }
                            AI.State_Death_Think(delta);
                            return;
                        }
                    default:
                        {
                            AI.State_Idle_Think(delta);
                            return;
                        }
                }
            }

            private void UpdateTargetList()
            {
                for (int i = AI._targetList.Count - 1; i >= 0; i--)
                {
                    PatrolHelicopterAI.targetinfo targetinfo = AI._targetList[i];
                    if (targetinfo == null || !targetinfo.ply || targetinfo.ply.IsDead() || Vector3.Distance(targetinfo.ply.transform.position, transform.position) > 150f)
                        AI._targetList.Remove(targetinfo);
                }

                visPlayers.Clear();

                Vis.Entities<BasePlayer>(Target.transform.position, 15f, visPlayers, 131072);

                foreach (BasePlayer player in visPlayers)
                {
                    if (player.IsNpc)
                        continue;

                    if (Configuration.UseHostile && !HostilePlayers.Contains(player.userID))
                        continue;

                    if (!player.HasPlayerFlag(BasePlayer.PlayerFlags.SafeZone) && Vector3Ex.Distance2D(transform.position, player.transform.position) <= 150f)
                    {
                        bool isAlreadyATarget = false;
                        for (int i = 0; i < AI._targetList.Count; i++)
                        {
                            PatrolHelicopterAI.targetinfo targetInfo = AI._targetList[i];
                            if (targetInfo.ply == player)
                            {
                                isAlreadyATarget = true;
                                break;
                            }
                        }

                        if (!isAlreadyATarget && AI.PlayerVisible(player))
                        {
                            AI._targetList.Add(new PatrolHelicopterAI.targetinfo(player, player));
                        }
                    }
                }

                if (AI._currentState != PatrolHelicopterAI.aiState.DEATH && AI._currentState != PatrolHelicopterAI.aiState.MOVE)
                {
                    AI.ExitCurrentState();

                    AI._currentState = PatrolHelicopterAI.aiState.MOVE;
                    AI.SetTargetDestination(GetMoveTarget(), 5f, 30f);
                    AI.targetThrottleSpeed = GetThrottleForDistance(Vector3.Distance(AI.transform.position, AI.destination));
                }
            }

            private Vector3 GetMoveTarget()
            {
                Vector3 moveTarget = Target.transform.position;
                moveTarget += Target.transform.right * Random.Range(-40f, 40f);
                moveTarget += Target.transform.forward * Random.Range(60f, 80f);
                moveTarget += Vector3.up * Random.Range(30f, 50f);

                return moveTarget;
            }

            private float GetThrottleForDistance(float distToTarget)
            {
                if (distToTarget >= 50f)
                    return 1f;
                else if (distToTarget >= 25f)
                    return 0.8f;
                return (distToTarget < 5f ? 0.05f * (1f - distToTarget / 5f) : 0.05f);
            }
        }

        private class TrainAutoTurret : MonoBehaviour
        {
            public NPCAutoTurret Entity { get; private set; }

            private RealTimeSinceEx timeSinceLastServerTick;
            
            private static readonly float[] visibilityOffsets = new float[]
            {
                0f,
                0.15f,
                -0.15f
            };

            private void Start()
            {
                Entity = GetComponent<NPCAutoTurret>();

                Entity.CancelInvoke(Entity.ServerTick);
                Entity.CancelInvoke(Entity.ScheduleForTargetScan);

                Entity.InvokeRepeating(ServerTick, Random.Range(0f, 1f), 0.015f);
                Entity.InvokeRandomized(TargetScan, Random.Range(0f, 1f), 1.25f, 0.2f);

                Entity.SetFlag(BaseEntity.Flags.Reserved1, false, false, true);
            }

            public void ServerTick()
            {                
                if (Entity.IsDestroyed)                
                    return;
                
                float timeSince = (float) timeSinceLastServerTick;
                timeSinceLastServerTick = 0;
                
                if (!Entity.IsOnline())
                {
                    Entity.SetFlag(BaseEntity.Flags.On, true, false, true);
                    Entity.booting = false;
                    Entity.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                    Entity.isLootable = false;
                }

                if (Entity.HasTarget())                
                    TargetTick();                
                else Entity.IdleTick(timeSince);                

                Entity.UpdateFacingToTarget(timeSince);

                if (Entity.totalAmmoDirty && Time.time > Entity.nextAmmoCheckTime)
                {
                    Entity.UpdateTotalAmmo();
                    Entity.totalAmmoDirty = false;
                    Entity.nextAmmoCheckTime = Time.time + 0.5f;
                }
            }        

            public void TargetScan()
            {
                if (Entity.HasTarget())                
                    return;                
               
                if (Entity.targetTrigger.entityContents != null)
                {
                    foreach (BaseEntity baseEntity in Entity.targetTrigger.entityContents)
                    {
                        BaseCombatEntity baseCombatEntity = baseEntity as BaseCombatEntity;
                        if (baseCombatEntity && baseCombatEntity.IsAlive() && Entity.InFiringArc(baseCombatEntity) && ObjectVisible(baseCombatEntity))
                        {
                            if (Entity.ShouldTarget(baseCombatEntity) && baseCombatEntity is BasePlayer player && !(player is ScientistNPC) && !(player is BanditGuard))
                            {
                                if (Configuration.UseHostile && !HostilePlayers.Contains(player.userID))
                                    continue;

                                if (Entity.target != player)
                                {
                                    Effect.server.Run(Entity.targetAcquiredEffect.resourcePath, Entity.transform.position, Vector3.up, null, false);
                                    Entity.MarkDirtyForceUpdateOutputs();
                                    Entity.nextShotTime += 0.1f;
                                }

                                Entity.target = player;
                            }
                        }
                    }
                }
            }

            private bool ObjectVisible(BaseCombatEntity obj)
            {                
                List<RaycastHit> list = Pool.Get<List<RaycastHit>>();

                Vector3 eyePosition = Entity.eyePos.transform.position;
                if (GamePhysics.CheckSphere(eyePosition, 0.1f, 2097152, QueryTriggerInteraction.UseGlobal))                
                    return false;                

                Vector3 targetPosition = Entity.AimOffset(obj);
                float distance = Vector3.Distance(targetPosition, eyePosition);

                Vector3 directionToTarget = targetPosition - eyePosition;
                Vector3 perpendicular = Vector3.Cross(directionToTarget.normalized, Vector3.up);

                int iteration = 0;
                while (iteration < (Entity.CheckPeekers() ? 3f : 1f))
                {                    
                    directionToTarget = (targetPosition + (perpendicular * visibilityOffsets[iteration])) - eyePosition;

                    list.Clear();

                    GamePhysics.TraceAll(new Ray(eyePosition, directionToTarget.normalized), 0f, list, distance * 1.1f, 1218652417, QueryTriggerInteraction.UseGlobal, null);
                    
                    for (int i = 0; i < list.Count; i++)
                    {
                        BaseEntity entity = list[i].GetEntity();
                        if ((entity == null || !entity.isClient) && (entity == null || entity.ToPlayer() == null || entity.EqualNetID(obj)) && (entity == null || !entity.EqualNetID(Entity)))
                        {
                            if (entity && (entity == obj || entity.EqualNetID(obj)))
                            {
                                Pool.FreeUnmanaged<RaycastHit>(ref list);
                                Entity.peekIndex = iteration;
                                return true;
                            }
                            if (!entity || entity.ShouldBlockProjectiles())
                            {
                                break;
                            }
                        }
                    }
                    iteration++;
                }
                Pool.FreeUnmanaged<RaycastHit>(ref list);
                return false;
            }

            public void TargetTick()
            {
                if (Time.realtimeSinceStartup >= Entity.nextVisCheck)
                {
                    Entity.nextVisCheck = Time.realtimeSinceStartup + Random.Range(0.2f, 0.3f);
                    Entity.targetVisible = ObjectVisible(Entity.target);
                    
                    if (Entity.targetVisible)                    
                        Entity.lastTargetSeenTime = Time.realtimeSinceStartup;
                    
                }

                Entity.EnsureReloaded(true);

                if (Time.time >= Entity.nextShotTime && Entity.targetVisible && Mathf.Abs(Entity.AngleToTarget(Entity.target, Entity.currentAmmoGravity != 0f)) < Entity.GetMaxAngleForEngagement())
                {
                    FireGun(Entity.AimOffset(Entity.target), Entity.aimCone, null, Entity.target);
                    Entity.nextShotTime = Time.time + 0.115f;
                }

                if (!Entity.target || Entity.target.IsDead() || Time.realtimeSinceStartup - Entity.lastTargetSeenTime > 3f || Vector3.Distance(Entity.transform.position, Entity.target.transform.position) > Entity.sightRange || (Entity.PeacekeeperMode() && !Entity.IsEntityHostile(Entity.target)))                
                    Entity.SetTarget(null);
                
            }

            public void FireGun(Vector3 targetPos, float aimCone, Transform muzzleToUse = null, BaseCombatEntity target = null)
            {
                if (Entity.IsOffline())                
                    return;
                
                if (!muzzleToUse)                
                    muzzleToUse = Entity.muzzleRight;
                
                Vector3 forward = Entity.GetCenterMuzzle().MultiplyVector(Vector3.forward);
                Vector3 firePosition = Entity.GetCenterMuzzle().MultiplyPoint3x4(Vector3.zero) - forward * 0.25f;

                Vector3 modifiedAimConeDirection = AimConeUtil.GetModifiedAimConeDirection(aimCone, forward, true);

                targetPos = firePosition + modifiedAimConeDirection * 300f;

                List<RaycastHit> list = Pool.Get<List<RaycastHit>>();
                GamePhysics.TraceAll(new Ray(firePosition, modifiedAimConeDirection), 0f, list, 300f, 1219701521, QueryTriggerInteraction.UseGlobal, null);
                bool hitTarget = false;

                for (int i = 0; i < list.Count; i++)
                {
                    RaycastHit hit = list[i];
                    BaseEntity entity = hit.GetEntity();

                    if ((entity == null || (entity != Entity && !entity.EqualNetID(Entity))) && 
                        (!Entity.PeacekeeperMode() || target == null || entity == null || (entity as BasePlayer) == null || entity.EqualNetID(target)))
                    {
                        BaseCombatEntity baseCombatEntity = entity as BaseCombatEntity;
                        if (baseCombatEntity)
                        {
                            if (baseCombatEntity is BasePlayer player && player.IsNpc)                            
                                continue;
                            
                            Entity.ApplyDamage(baseCombatEntity, hit.point, modifiedAimConeDirection);

                            if (baseCombatEntity.EqualNetID(target))                            
                                hitTarget = true;                            
                        }

                        if (!entity || entity.ShouldBlockProjectiles())
                        {
                            targetPos = hit.point;
                            forward = (targetPos - firePosition).normalized;
                            break;
                        }
                    }
                }
                if (!hitTarget)                
                    Entity.numConsecutiveMisses++;                
                else Entity.numConsecutiveMisses = 0;
                
                if (target && Entity.targetVisible && Entity.numConsecutiveMisses > 2)
                {
                    Entity.ApplyDamage(target, target.transform.position - forward * 0.25f, forward);
                    Entity.numConsecutiveMisses = 0;
                }

                Entity.ClientRPC<uint, Vector3>(null, "CLIENT_FireGun", StringPool.Get(muzzleToUse.gameObject.name), targetPos);
                Pool.FreeUnmanaged<RaycastHit>(ref list);
            }
        }

        public class TrainBradleyAPC : MonoBehaviour
        {
            public BradleyAPC Entity { get; private set; }

            private void Start()
            {
                Entity = GetComponent<BradleyAPC>();

                Entity.enabled = false;
                Entity.myRigidBody.isKinematic = true;
                Entity.myRigidBody.interpolation = RigidbodyInterpolation.None;

                Entity.InvokeRepeating(UpdateTargetList, 0f, 2f);
                Entity.InvokeRepeating(Entity.UpdateTargetVisibilities, 0f, BradleyAPC.sightUpdateRate);
            }

            private void Update()
            {
                Entity.SetFlag(BaseEntity.Flags.Reserved5, TOD_Sky.Instance.IsNight, false, true);
                
                if (Entity.targetList.Count > 0)
                {
                    if (Entity.targetList[0].IsValid() && Entity.targetList[0].IsVisible())                    
                        Entity.mainGunTarget = (Entity.targetList[0].entity as BaseCombatEntity);                    
                    else Entity.mainGunTarget = null;
                    
                }
                else Entity.mainGunTarget = null;
                
                Entity.DoWeaponAiming();
                Entity.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
            
            }

            private void FixedUpdate()
            {
                Entity.DoWeapons();
                Entity.DoHealing();
            }

            private void LateUpdate() => Entity.LateUpdate();

            public void UpdateTargetList()
            {
                List<BaseEntity> list = Pool.Get<List<BaseEntity>>();

                Vis.Entities<BaseEntity>(Entity.transform.position, Entity.searchRange, list, 133120, QueryTriggerInteraction.Collide);

                foreach (BaseEntity baseEntity in list)
                {
                    if (baseEntity is BasePlayer player && !(player is global::HumanNPC))
                    {
                        if (Configuration.UseHostile && !HostilePlayers.Contains(player.userID))
                            continue;

                        if (!player.IsDead() && Entity.VisibilityTest(player))
                        {
                            bool isKnown = false;
                            foreach (BradleyAPC.TargetInfo targetInfo in Entity.targetList)
                            {
                                if (targetInfo.entity == player)
                                {
                                    targetInfo.lastSeenTime = Time.time;
                                    isKnown = true;
                                    break;
                                }
                            }

                            if (!isKnown)
                            {
                                BradleyAPC.TargetInfo targetInfo = Pool.Get<BradleyAPC.TargetInfo>();
                                targetInfo.Setup(player, Time.time);
                                Entity.targetList.Add(targetInfo);
                            }
                        }
                    }
                }

                for (int i = Entity.targetList.Count - 1; i >= 0; i--)
                {
                    BradleyAPC.TargetInfo targetInfo = Entity.targetList[i];
                    if (!targetInfo.entity || Time.time - targetInfo.lastSeenTime > Entity.memoryDuration || (targetInfo.entity as BasePlayer).IsDead())
                    {
                        Entity.targetList.Remove(targetInfo);
                        Pool.Free<BradleyAPC.TargetInfo>(ref targetInfo);
                    }
                }

                Pool.FreeUnmanaged<BaseEntity>(ref list);
                Entity.targetList.Sort(new Comparison<BradleyAPC.TargetInfo>(Entity.SortTargets));
            }
        }

        public class MarkerFollower : MonoBehaviour
        {
            private BaseEntity _marker;
            private BaseEntity _follow;
            
            private const string PREFAB = "assets/prefabs/tools/map/cratemarker.prefab";
            
            public static void Create(BaseEntity follow)
            {
                Vector3 position = follow.transform.position;
                position.y = 250f;
                
                MobileMapMarker marker = GameManager.server.CreateEntity(PREFAB, position) as MobileMapMarker;
                marker.enableSaving = false;
                marker.Spawn();
                
                MarkerFollower markerFollower = marker.gameObject.AddComponent<MarkerFollower>();
                markerFollower._marker = marker;
                markerFollower._follow = follow;
            }

            private void Start() => InvokeHandler.InvokeRepeating(this, NetworkUpdate, 0.5f, 0.5f);

            private void OnDestroy()
            {
                if (_marker && !_marker.IsDestroyed)
                    _marker.Kill(BaseNetworkable.DestroyMode.None);
            }

            private void NetworkUpdate()
            {
                if (!_follow || _follow.IsDestroyed)
                {
                    Destroy(gameObject);
                    return;
                }
                
                Vector3 position = _follow.transform.position;
                position.y = 250f;
                transform.position = position;
                
                _marker.SendNetworkUpdate();
            }
        }
        #endregion

        #region Commands
        
        [ChatCommand("trainheist")]
        private void CmdTrainHeist(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, ADMIN_PERMISSION))
                return;

            if (args.Length == 0)
            {
                ChatMessage(player, "Chat.Start");
                ChatMessage(player, "Chat.Stop");
                return;
            }

            if (!trainTrackDetected || (overworldTracks.Length == 0 && underworldTracks.Length == 0))
            {
                ChatMessage(player, "Chat.NoRailLoop");
                return;
            }

            switch (args[0].ToLower())
            {
                case "drawspawns":
                    foreach(float3 point in overworldTracks)
                        player.SendConsoleCommand("ddraw.sphere", 30f, Color.red, (Vector3)point + Vector3.up, 1f);

                    foreach (float3 point in underworldTracks)
                        player.SendConsoleCommand("ddraw.sphere", 30f, Color.blue, (Vector3)point + Vector3.up, 1f);
                    return;

                case "start":
                    if (activeHeistTrain)
                    {
                        ChatMessage(player, "Chat.InProgress");
                        return;
                    }

                    eventTimer?.Destroy();

                    ServerMgr.Instance.StartCoroutine(FindBestSpawnPointAndRunEvent());

                    ChatMessage(player, "Chat.Starting");
                    return;

                case "startnear":
                    if (activeHeistTrain)
                    {
                        ChatMessage(player, "Chat.InProgress");
                        return;
                    }

                    if (!TrainTrackSpline.TryFindTrackNear(player.transform.position, 20f, out TrainTrackSpline splineResult, out float distResult))
                    {
                        ChatMessage(player, "Chat.FailedNearby");
                        return;
                    }

                    ServerMgr.Instance.StartCoroutine(FindBestSpawnPointAndRunEvent(player.transform.position));
                    ChatMessage(player, "Chat.Starting");

                    return;
                case "stop":
                    if (!activeHeistTrain)
                    {
                        ChatMessage(player, "Chat.NoHeist");
                        return;
                    }

                    UnityEngine.Object.Destroy(activeHeistTrain);

                    ChatMessage(player, "Chat.Cancelled");
                    return;

                default:
                    ChatMessage(player, "Chat.Start");
                    ChatMessage(player, "Chat.Stop");
                    return;
            }
        }
        
        [ConsoleCommand("trainheist")]
        private void CCmdTrainHeist(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                SendReply(arg, "This console command can only be run through rcon. Use the provided chat commands for ingame use");
                return;
            }

            if (arg.Args?.Length == 0)
            {
                SendReply(arg, "trainheist start - Starts the train heist");
                SendReply(arg, "trainheist stop - Cancels the train heist");
                return;
            }

            if (!trainTrackDetected || (overworldTracks.Length == 0 && underworldTracks.Length == 0))
            {
                SendReply(arg, "Either no rail loops have been detected or rail loops have not finished processing");
                return;
            }

            switch (arg.Args[0].ToLower())
            {
                case "start":
                    if (activeHeistTrain)
                    {
                        SendReply(arg, "There is already a train heist in progress");
                        return;
                    }

                    eventTimer?.Destroy();

                    ServerMgr.Instance.StartCoroutine(FindBestSpawnPointAndRunEvent());

                    SendReply(arg, "Starting train heist event");
                    return;

                case "stop":
                    if (!activeHeistTrain)
                    {
                        SendReply(arg, "There is not a train heist in progress");
                        return;
                    }

                    UnityEngine.Object.Destroy(activeHeistTrain);

                    SendReply(arg, "Train heist event cancelled");
                    return;

                default:
                    SendReply(arg, "trainheist start - Starts the train heist");
                    SendReply(arg, "trainheist stop - Cancels the train heist");
                    return;
            }
        }
        #endregion

        #region Wagon Editor
        private readonly Hash<ulong, WagonEditor> m_WagonEditors = new Hash<ulong, WagonEditor>();

        [ChatCommand("th.edit")]
        private void CmdTrainHeistEditor(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, ADMIN_PERMISSION))
                return;

            if (args.Length == 0)
            {
                player.ChatMessage("/th.edit load <layout_name> - Start editing the specified layout");
                player.ChatMessage("/th.edit save - Save your current edits");
                player.ChatMessage("/th.edit cancel - Cancel your current edits");

                if (m_WagonEditors.ContainsKey(player.userID))
                {
                    player.ChatMessage("/th.edit move - Move the entity you are looking at");
                    player.ChatMessage("/th.edit spawn <entity_name> - Spawn the specified entity");
                    player.ChatMessage("/th.edit delete - Delete the entity you are looking at");                    
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
                            player.ChatMessage("You are already editing a wagon. Save or cancel the current edit before continuing");
                            return;
                        }

                        if (args.Length != 2)
                        {
                            player.ChatMessage("You must enter a layout name, available options are;\nEngineLayout, LocomotiveLayout, WagonALayout, WagonBLayout, WagonCLayout, WagonLootLayout, WagonResourcesLayout");
                            return;
                        }

                        if (!TrainTrackSpline.TryFindTrackNear(player.transform.position, 10f, out TrainTrackSpline splineResult, out _))
                        {
                            player.ChatMessage("You need to move closer to a train track to start editing");
                            return;
                        }

                        Datafile<TrainCarLayout> datafile = null;
                        string trainPrefab = string.Empty;

                        switch (args[1].ToLower())
                        {
                            case "enginelayout":
                                datafile = EngineLayout;
                                trainPrefab = WORKCART_PREFAB;
                                break;
                            case "locomotivelayout":
                                datafile = LocomotiveLayout;
                                trainPrefab = LOCOMOTIVE_PREFAB;
                                break;
                            case "wagonalayout":
                                datafile = WagonALayout;
                                trainPrefab = WAGON_A_ENTITY;
                                break;
                            case "wagonblayout":
                                datafile = WagonBLayout;
                                trainPrefab = WAGON_B_ENTITY;
                                break;
                            case "wagonclayout":
                                datafile = WagonCLayout;
                                trainPrefab = WAGON_C_ENTITY;
                                break;
                            case "wagonlootlayout":
                                datafile = WagonLootLayout;
                                trainPrefab = WAGON_LOOT;
                                break;
                            case "wagonresourceslayout":
                                datafile = WagonResourcesLayout;
                                trainPrefab = WAGON_RESOURCES;
                                break;                            
                            default:
                                player.ChatMessage("Invalid layout name, available options are;\nEngineLayout, LocomotiveLayout, WagonALayout, WagonBLayout, WagonCLayout, WagonLootLayout, WagonResourcesLayout");
                                return;
                        }

                        Vector3 closestPosition = player.transform.position;
                        float closestDistance = float.MaxValue;

                        List<float3> splinePoints = Pool.Get<List<float3>>();

                        PointsToWorld(splineResult, splinePoints);

                        for (int i = 0; i < splinePoints.Count; i++)
                        {
                            float distance = Vector3.Distance(player.transform.position, splinePoints[i]);
                            if (distance < closestDistance)
                            {
                                closestDistance = distance;
                                closestPosition = splinePoints[i];
                            }
                        }

                        Pool.FreeUnmanaged(ref splinePoints);

                        TrainCar trainCar = GameManager.server.CreateEntity(trainPrefab, closestPosition, Quaternion.identity) as TrainCar;
                        trainCar.enableSaving = false;

                        trainCar.frontCoupling = null;
                        trainCar.rearCoupling = null;

                        trainCar.platformParentTrigger.ParentNPCPlayers = true;

                        trainCar.Spawn();

                        wagonEditor = player.gameObject.AddComponent<WagonEditor>();
                        wagonEditor.Load(trainCar, datafile);

                        m_WagonEditors[player.userID] = wagonEditor;

                        player.ChatMessage($"You are now editing : {args[1]}\nLook at a entity and press '<color=#ce422b>Fire</color>' to select to move, or use the provided commands");
                        return;
                    }

                case "save":
                    {
                        if (!wagonEditor)
                        {
                            player.ChatMessage("You are not currently editing a wagon");
                            return;
                        }

                        wagonEditor.Save();
                        UnityEngine.Object.Destroy(wagonEditor);

                        m_WagonEditors.Remove(player.userID);
                        player.ChatMessage("Layout editing has been saved");
                        return;
                    }

                case "cancel":
                    {
                        if (!wagonEditor)
                        {
                            player.ChatMessage("You are not currently editing a wagon");
                            return;
                        }

                        UnityEngine.Object.Destroy(wagonEditor);

                        m_WagonEditors.Remove(player.userID);
                        player.ChatMessage("Layout editing has been cancelled");
                        return;
                    }

                case "move":
                    {
                        if (!wagonEditor)
                        {
                            player.ChatMessage("You are not currently editing a wagon");
                            return;
                        }

                        BaseEntity baseEntity = WagonEditor.FindEntityFromRay(player);
                        if (!baseEntity || !wagonEditor.IsTrainEntity(baseEntity))
                        {
                            player.ChatMessage("This entity is not a child of the train car");
                            return;
                        }

                        wagonEditor.StartEditingEntity(baseEntity, false);
                        return;
                    }

                case "spawn":
                    {
                        if (!wagonEditor)
                        {
                            player.ChatMessage("You are not currently editing a wagon");
                            return;
                        }

                        if (args.Length != 2)
                        {
                            player.ChatMessage("You must enter a entity name, available options are;\napc, npc, loot, turret, samsite");
                            return;
                        }

                        string entityPrefab = string.Empty;
                        switch (args[1].ToLower())
                        {
                            case "apc":
                                entityPrefab = BRADLEY_PREFAB;
                                break;
                            case "npc":
                                entityPrefab = SCIENTIST_PREFAB;
                                break;
                            case "loot":
                                entityPrefab = HACKABLE_CRATE_PREFAB;
                                break;
                            case "turret":
                                entityPrefab = TURRET_PREFAB;
                                break;
                            case "samsite":
                                entityPrefab = SAMSITE_PREFAB;
                                break;
                            default:
                                player.ChatMessage("Invalid entity name, available options are;\napc, npc, loot, turret, samsite");
                                return;
                        }

                        Vector3 position = player.transform.position + (player.eyes.BodyForward() * 3f);

                        BaseEntity baseEntity = wagonEditor.CreateChildEntity(entityPrefab, wagonEditor.TrainCar.transform.InverseTransformPoint(position), Quaternion.identity);
                        if (!baseEntity)
                        {
                            player.ChatMessage("Failed to spawn entity");
                            return;
                        }

                        wagonEditor.StartEditingEntity(baseEntity, true);
                        return;
                    }

                case "delete":
                    {
                        if (!wagonEditor)
                        {
                            player.ChatMessage("You are not currently editing a wagon");
                            return;
                        }

                        BaseEntity baseEntity = WagonEditor.FindEntityFromRay(player);
                        if (!baseEntity || !wagonEditor.IsTrainEntity(baseEntity))
                        {
                            player.ChatMessage("This entity is not a child of the train car");
                            return;
                        }

                        wagonEditor.DeleteWagonEntity(baseEntity);
                        return;
                    }

                default:
                    player.ChatMessage("Invalid syntax. Type /th.edit to see available options");
                    return;
            }
        }

        private class WagonEditor : MonoBehaviour
        {
            private BasePlayer m_Player;

            private Datafile<TrainCarLayout> m_Datafile;

            private TrainCar m_TrainCar;

            private List<BaseEntity> m_Children = Pool.Get<List<BaseEntity>>();

            private BaseEntity m_CurrentEntity;

            private Construction m_Construction;

            private Vector3 m_RotationOffset = Vector3.zero;

            private float m_NextRotateFrame;

            private float m_NextClickFrame;

            private Vector3 m_StartPosition;

            private Quaternion m_StartRotation;

            public TrainCar TrainCar => m_TrainCar;

            private static ProtectionProperties _fullProtection;

            private static readonly RaycastHit[] RaycastBuffer = new RaycastHit[32];

            private void Awake()
            {
                m_Player = GetComponent<BasePlayer>();

                if (!_fullProtection)
                {
                    _fullProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
                    _fullProtection.density = 100; //20015
                    _fullProtection.amounts = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
                }
            }

            private void OnDestroy()
            {
                foreach (BaseEntity baseEntity in m_Children)
                {
                    if (!baseEntity || baseEntity.IsDestroyed)
                        continue;

                    baseEntity.Kill(BaseNetworkable.DestroyMode.None);
                }

                Pool.FreeUnmanaged(ref m_Children);

                if (m_TrainCar && !m_TrainCar.IsDestroyed)
                    m_TrainCar.Kill(BaseNetworkable.DestroyMode.None);
            }

            public void Load(TrainCar trainCar, Datafile<TrainCarLayout> datafile)
            {
                m_TrainCar = trainCar;
                m_Datafile = datafile;

                InvokeHandler.Invoke(this, () =>
                {
                    foreach (TrainCarLayout.SpawnTransform spawn in m_Datafile.Data.APC.Spawns)
                        CreateChildEntity(BRADLEY_PREFAB, spawn.Position, spawn.Rotation);

                    foreach (TrainCarLayout.SpawnTransform spawn in m_Datafile.Data.Loot.Spawns)
                        CreateChildEntity(HACKABLE_CRATE_PREFAB, spawn.Position, spawn.Rotation);

                    foreach (TrainCarLayout.SpawnTransform spawn in m_Datafile.Data.NPC.Spawns)
                        CreateChildEntity(SCIENTIST_PREFAB, spawn.Position, spawn.Rotation);

                    foreach (TrainCarLayout.SpawnTransform spawn in m_Datafile.Data.SamSite.Spawns)
                        CreateChildEntity(SAMSITE_PREFAB, spawn.Position, spawn.Rotation);

                    foreach (TrainCarLayout.SpawnTransform spawn in m_Datafile.Data.Turret.Spawns)
                        CreateChildEntity(TURRET_PREFAB, spawn.Position, spawn.Rotation);
                }, 1f);
            }

            public void Save()
            {
                m_Datafile.Data.APC.Spawns.Clear();
                m_Datafile.Data.Loot.Spawns.Clear();
                m_Datafile.Data.NPC.Spawns.Clear();
                m_Datafile.Data.SamSite.Spawns.Clear();
                m_Datafile.Data.Turret.Spawns.Clear();

                foreach(BaseEntity baseEntity in m_Children)
                {
                    if (!baseEntity || baseEntity.IsDestroyed)
                        continue;

                    TrainCarLayout.SpawnTransform spawnTransform = new TrainCarLayout.SpawnTransform
                    {
                        Position = baseEntity.transform.localPosition,
                        Rotation = baseEntity.transform.localRotation
                    };

                    if (baseEntity is BradleyAPC)
                        m_Datafile.Data.APC.Spawns.Add(spawnTransform);
                    else if (baseEntity is HackableLockedCrate)
                        m_Datafile.Data.Loot.Spawns.Add(spawnTransform);
                    else if (baseEntity is global::HumanNPC)
                        m_Datafile.Data.NPC.Spawns.Add(spawnTransform);
                    else if (baseEntity is SamSite)
                        m_Datafile.Data.SamSite.Spawns.Add(spawnTransform);
                    else if (baseEntity is AutoTurret)
                        m_Datafile.Data.Turret.Spawns.Add(spawnTransform);
                }

                m_Datafile.Save();
            }

            public static BaseEntity FindEntityFromRay(BasePlayer player)
            {
                const int LAYERS = 1 << 0 | 1 << 8 | 1 << 17; //20015

                int hits = Physics.RaycastNonAlloc(player.eyes.HeadRay(), RaycastBuffer, 4f, LAYERS, QueryTriggerInteraction.Ignore);
                   
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
                if (!justSpawned)
                {
                    m_StartPosition = baseEntity.transform.localPosition;
                    m_StartRotation = baseEntity.transform.localRotation;
                }

                m_CurrentEntity = baseEntity;

                m_Construction = PrefabAttribute.server.Find<Construction>(m_CurrentEntity.prefabID);
                if (!m_Construction)
                {
                    m_Construction = new Construction();
                    m_Construction.rotationAmount = new Vector3(0, 90f, 0);
                    m_Construction.fullName = m_CurrentEntity.PrefabName;
                    m_Construction.maxplaceDistance = 2f;
                    m_Construction.canRotateBeforePlacement = m_Construction.canRotateAfterPlacement = true;
                }

                m_Player.ChatMessage($"Began editing : <color=#ce422b>{m_CurrentEntity.ShortPrefabName}</color>\n\nPress '<color=#ce422b>Fire</color>' to place the object\nPress '<color=#ce422b>Aim</color>' to cancel placement\nPress '<color=#ce422b>Reload</color>' to rotate the object on its Y axis\nHold '<color=#ce422b>Crouch</color>' and press '<color=#ce422b>Reload</color>' to rotate the object on its Z axis\nHold '<color=#ce422b>Sprint</color>' and press '<color=#ce422b>Reload</color>' to rotate the object on its X axis");
            }

            public void DeleteWagonEntity(BaseEntity baseEntity)
            {
                if (baseEntity == m_CurrentEntity)
                    m_CurrentEntity = null;

                m_Children.Remove(baseEntity);
                baseEntity.Kill(BaseNetworkable.DestroyMode.None);
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
                    else m_RotationOffset.y = Mathf.Repeat(m_RotationOffset.y + 90f, 360);

                    m_NextRotateFrame = Time.frameCount + 20;
                }

                if (m_Player.serverInput.WasJustPressed(BUTTON.FIRE_PRIMARY) && Time.frameCount > m_NextClickFrame)
                {
                    m_Player.ChatMessage($"Placed <color=#ce422b>{m_CurrentEntity.ShortPrefabName}</color> at {m_CurrentEntity.transform.localPosition}");

                    m_CurrentEntity = null;
                    m_NextClickFrame = Time.frameCount + 20;
                }
                else if (m_Player.serverInput.WasJustPressed(BUTTON.FIRE_SECONDARY))
                {
                    m_Player.ChatMessage($"Cancelled placement of <color=#ce422b>{m_CurrentEntity.ShortPrefabName}</color>");

                    if (m_StartPosition != Vector3.zero && m_StartRotation != Quaternion.identity)
                    {
                        m_CurrentEntity.transform.localPosition = m_StartPosition;
                        m_CurrentEntity.transform.localRotation = m_StartRotation;

                        UpdateNetworkTransform();                        
                    }
                    else
                    {
                        m_Children.Remove(m_CurrentEntity);
                        m_CurrentEntity.Kill(BaseNetworkable.DestroyMode.None);
                    }

                    m_CurrentEntity = null;
                }
            }

            public BaseEntity CreateChildEntity(string prefab, Vector3 position, Quaternion rotation)
            {
                BaseEntity baseEntity = GameManager.server.CreateEntity(prefab, m_TrainCar.transform.TransformPoint(position));

                if (!(baseEntity is global::HumanNPC))
                {
                    baseEntity.SetParent(m_TrainCar, true, true);
                    baseEntity.transform.localPosition = position;
                    baseEntity.transform.localRotation = rotation;
                }

                if (baseEntity is BaseCombatEntity entity)
                    entity.baseProtection = _fullProtection;

                baseEntity.enableSaving = false;
                baseEntity.enabled = false;

                BaseAIBrain baseAiBrain = baseEntity.GetComponent<BaseAIBrain>();
                if (baseAiBrain)
                    baseAiBrain.enabled = false;

                baseEntity.Spawn();

                if (baseEntity is AutoTurret turret)
                {
                    turret.CancelInvoke(turret.ServerTick);
                    turret.CancelInvoke(turret.SendAimDir);
                    turret.CancelInvoke(turret.ScheduleForTargetScan);
                }

                if (baseEntity is SamSite site)
                {
                    site.CancelInvoke(site.TargetScan);
                }

                if (baseEntity is BradleyAPC apc)
                {
                    apc.CancelInvoke(apc.UpdateTargetList);
                    apc.CancelInvoke(apc.UpdateTargetVisibilities);
                }

                if (baseEntity is global::HumanNPC npc)
                {
                    npc.CancelInvoke(npc.TickMovement);
                    AIThinkManager.Remove(npc);
                }

                m_Children.Add(baseEntity);

                return baseEntity;
            }

            private void UpdateNetworkTransform()
            {
                if (m_CurrentEntity is AutoTurret or SamSite)
                {
                    NetWrite netWrite = Net.sv.StartWrite();
                    netWrite.PacketID(Network.Message.Type.EntityDestroy);
                    netWrite.EntityID(m_CurrentEntity.net.ID);
                    netWrite.UInt8(0);
                    netWrite.Send(new SendInfo(m_CurrentEntity.net.group.subscribers));

                    m_CurrentEntity.SendNetworkUpdateImmediate();
                }
                else
                {
                    NetWrite netWrite = Net.sv.StartWrite();
                    netWrite.PacketID(Network.Message.Type.EntityPosition);
                    netWrite.EntityID(m_CurrentEntity.net.ID);

                    netWrite.WriteObject(m_CurrentEntity.GetNetworkPosition());
                    netWrite.WriteObject(m_CurrentEntity.GetNetworkRotation().eulerAngles);

                    netWrite.Float(m_CurrentEntity.GetNetworkTime());
                    SendInfo info = new SendInfo(m_CurrentEntity.net.group.subscribers)
                    {
                        method = SendMethod.ReliableUnordered,
                        priority = Priority.Immediate
                    };
                    netWrite.Send(info);
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
                m_CurrentEntity.transform.rotation = Quaternion.Euler(constructionTarget.rotation) * Quaternion.LookRotation(direction);

                m_CurrentEntity.transform.position = Vector3.Lerp(position, m_CurrentEntity.transform.position, Time.deltaTime * 6f);
                m_CurrentEntity.transform.rotation = Quaternion.Lerp(rotation, m_CurrentEntity.transform.rotation, Time.deltaTime * 10f);
            }
        }
        #endregion

        #region Config        
        private static ConfigData Configuration;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Only attack players deemed hostile")]
            public bool UseHostile { get; set; }

            [JsonProperty(PropertyName = "Event Automation")]
            public EventAutomation Automation { get; set; }

            [JsonProperty(PropertyName = "Train Settings")]
            public TrainSettings Train { get; set; }

            [JsonProperty(PropertyName = "Helicopter Settings")]
            public HelicopterSettings Helicopter { get; set; }

            public class EventAutomation
            {
                public bool Enabled { get; set; }

                [JsonProperty(PropertyName = "Broadcast when event starts")]
                public bool BroadcastStart { get; set; } = true;
                
                [JsonProperty(PropertyName = "Broadcast when event ends")]
                public bool BroadcastStop { get; set; } = true;

                [JsonProperty(PropertyName = "Minimum amount of online players to trigger the event")]
                public int RequiredPlayers { get; set; }

                [JsonProperty(PropertyName = "Minimum amount of time between events (seconds)")]
                public int Minimum { get; set; }

                [JsonProperty(PropertyName = "Maximum amount of time between events (seconds)")]
                public int Maximum { get; set; }

                [JsonProperty(PropertyName = "Use spawn overrides (if enabled the starting spawn point will be selected from the spawn override list)")]
                public bool UseSpawnOverrides { get; set; }

                [JsonProperty(PropertyName = "Preferred spawn overrides")]
                public List<SpawnOverride> Spawns { get; set; } = new ();
            }

            public class TrainSettings
            {
                [JsonProperty(PropertyName = "Use locomotive as train engine")]
                public bool UseLocomotive { get; set; }

                [JsonProperty(PropertyName = "Allow spawns to generate in under ground rail network")]
                public bool AllowUnderGround { get; set; }

                [JsonProperty(PropertyName = "Allow spawns to generate on above ground rail network")]
                public bool AllowAboveGround { get; set; }
                
                [JsonProperty(PropertyName = "Allow train to transition from under ground to above ground")]
                public bool AllowTransition { get; set; }

                [JsonProperty(PropertyName = "Stop train on driver death")]
                public bool StopOnDriverDeath { get; set; }

                [JsonProperty(PropertyName = "Despawn time for inactivity (seconds)")]
                public int DespawnSeconds { get; set; }

                [JsonProperty(PropertyName = "Amount of time from when all crates have been hacked until train self destructs (seconds)")]
                public int SelfDestructSeconds { get; set; }

                [JsonProperty(PropertyName = "Train engine force")]
                public float EngineForce { get; set; }

                [JsonProperty(PropertyName = "Train max speed")]
                public float MaxSpeed { get; set; }

                [JsonProperty(PropertyName = "Amount of damage required to slow the train down")]
                public float EngineDamageToSlow { get; set; }

                [JsonProperty(PropertyName = "Amount of time the train slows down when damaged (seconds)")]
                public float EngineSlowTime { get; set; }

                [JsonProperty(PropertyName = "Maximum velocity the train can travel when slowed due to damage")]
                public float EngineSlowMaxVelocity { get; set; }

                [JsonProperty(PropertyName = "Health regeneration per second")]
                public float HealthRegen { get; set; }

                [JsonProperty(PropertyName = "Desired travel speed (Low, Medium, High)"), JsonConverter(typeof(StringEnumConverter))]
                public TravelSpeed Speed { get; set; }

                [JsonProperty(PropertyName = "Carriage layout (Combination of WagonA, WagonB, WagonC, WagonResources, WagonLoot)")]
                public List<string> Layout { get; set; }

                public enum TravelSpeed { Low, Medium, High }

                public void ApplySettingsToEngine(TrainEngine trainEngine)
                {                    
                    trainEngine.engineForce = UseLocomotive ? EngineForce * 5f : EngineForce;
                    trainEngine.maxSpeed = MaxSpeed;
                    trainEngine.maxFuelPerSec = 0f;

                    trainEngine.engineSlowedTime = EngineSlowTime;
                    trainEngine.engineDamageToSlow = EngineDamageToSlow;
                    trainEngine.engineSlowedMaxVel = EngineSlowMaxVelocity;
                }
            }

            public class HelicopterSettings
            {
                [JsonProperty(PropertyName = "Amount to spawn")]
                public int Amount { get; set; }

                [JsonProperty(PropertyName = "Loot crates to spawn")]
                public int LootToSpawn { get; set; }

                public PatrolHelicopter Spawn(HeistTrain heistTrain)
                {
                    const string HELICOPTER_PREFAB = "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab";

                    PatrolHelicopter baseHelicopter = GameManager.server.CreateEntity(HELICOPTER_PREFAB) as PatrolHelicopter;
                    baseHelicopter.enableSaving = false;

                    baseHelicopter.maxCratesToSpawn = LootToSpawn;

                    ChaseHelicopterAI chaseHeliAi = baseHelicopter.gameObject.AddComponent<ChaseHelicopterAI>();
                    chaseHeliAi.Target = heistTrain;

                    baseHelicopter.Spawn();

                    baseHelicopter.Invoke(() =>
                    {
                        Vector3 position = heistTrain.transform.position;
                        position.y = 490f;

                        baseHelicopter.transform.position = position;
                    }, 1f);

                    return baseHelicopter;
                }
            }

            public Oxide.Core.VersionNumber Version { get; set; }
        }

        private class SpawnOverride
        {
            public bool Underground { get; set; }
            public TrainCarLayout.SpawnTransform.SerializedVector Position { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Configuration = Config.ReadObject<ConfigData>();

            if (Configuration.Version < Version)
                UpdateConfigValues();

            Config.WriteObject(Configuration, true);
        }

        protected override void LoadDefaultConfig() => Configuration = GetBaseConfig();

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                Automation = new ConfigData.EventAutomation
                {
                    Enabled = true,
                    Minimum = 3600,
                    Maximum = 5400,
                    RequiredPlayers = 1,
                    UseSpawnOverrides = false,
                    Spawns = new List<SpawnOverride>
                    {
                        new SpawnOverride
                        {
                            Underground = false,
                            Position = new TrainCarLayout.SpawnTransform.SerializedVector
                            {
                                X = 0,
                                Y = 0,
                                Z = 0
                            }
                        },
                        new SpawnOverride
                        {
                            Underground = false,
                            Position = new TrainCarLayout.SpawnTransform.SerializedVector
                            {
                                X = 0,
                                Y = 0,
                                Z = 0
                            }
                        }
                    }
                },
                Helicopter = new ConfigData.HelicopterSettings
                {
                    Amount = 1,
                    LootToSpawn = 0,
                },
                Train = new ConfigData.TrainSettings
                {
                    UseLocomotive = true,
                    AllowUnderGround = false,
                    AllowAboveGround = true,
                    EngineForce = 50000,
                    MaxSpeed = 18,
                    EngineDamageToSlow = 200,
                    EngineSlowTime = 8,
                    EngineSlowMaxVelocity = 4,
                    HealthRegen = 5f,
                    Speed = ConfigData.TrainSettings.TravelSpeed.Medium,
                    StopOnDriverDeath = false,
                    SelfDestructSeconds = 60,
                    DespawnSeconds = 600,
                    Layout = new List<string> { "WagonA", "WagonB", "WagonC" }
                },
                Version = Version
            };
        }

        protected override void SaveConfig() => Config.WriteObject(Configuration, true);

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();

            if (Configuration.Version < new Core.VersionNumber(0, 1, 2))
                Configuration.Train.Speed = ConfigData.TrainSettings.TravelSpeed.Medium;

            if (Configuration.Version < new VersionNumber(0, 2, 0))
            {
                Configuration.Helicopter.LootToSpawn = baseConfig.Helicopter.LootToSpawn;

                Configuration.Train.HealthRegen = baseConfig.Train.HealthRegen;
                Configuration.Train.EngineForce = baseConfig.Train.EngineForce;
                Configuration.Train.MaxSpeed = baseConfig.Train.MaxSpeed;
                Configuration.Train.EngineDamageToSlow = baseConfig.Train.EngineDamageToSlow;
                Configuration.Train.EngineSlowTime = baseConfig.Train.EngineSlowTime;
                Configuration.Train.EngineSlowMaxVelocity = baseConfig.Train.EngineSlowMaxVelocity;
                Configuration.Train.Layout = baseConfig.Train.Layout;
            }

            if (Configuration.Version < new VersionNumber(0, 2, 7))
            {
                Configuration.Automation.UseSpawnOverrides = false;
                Configuration.Automation.Spawns = baseConfig.Automation.Spawns;
            }

            if (Configuration.Version < new VersionNumber(0, 2, 9))
            {
                Configuration.Train.Layout = baseConfig.Train.Layout;
                Configuration.Train.UseLocomotive = true;
            }

            if (Configuration.Version < new VersionNumber(0, 2, 11))
            {
                Configuration.Train.Layout = baseConfig.Train.Layout;
                Configuration.Train.AllowAboveGround = true;
                Configuration.Train.AllowUnderGround = true;
            }
            
            if (Configuration.Version < new VersionNumber(0, 2, 29))
                Configuration.Automation.Spawns = baseConfig.Automation.Spawns;

            Configuration.Version = Version;
            PrintWarning("Config update completed!");
        }

        #endregion
               
        #region Data
        private static Datafile<Container> LootTable;

        private Datafile<TrainCarLayout> LocomotiveLayout;
        private Datafile<TrainCarLayout> EngineLayout;
        private Datafile<TrainCarLayout> WagonALayout;
        private Datafile<TrainCarLayout> WagonBLayout;
        private Datafile<TrainCarLayout> WagonCLayout;
        private Datafile<TrainCarLayout> WagonResourcesLayout;
        private Datafile<TrainCarLayout> WagonLootLayout;
        //private Datafile<TrainCarLayout> WagonDLayout;

        private class TrainCarLayout
        {
            [JsonIgnore]
            public bool IsValid => NPC != null && Turret != null && Loot != null;

            [JsonProperty(PropertyName = "NPC Settings")]
            public NPCSettings NPC { get; set; }

            [JsonProperty(PropertyName = "AutoTurret Settings")]
            public AutoTurretSettings Turret { get; set; }

            [JsonProperty(PropertyName = "SamSite Settings")]
            public SamSiteSettings SamSite { get; set; }

            [JsonProperty(PropertyName = "Loot Settings")]
            public LootSettings Loot { get; set; }

            [JsonProperty(PropertyName = "Bradley Settings")]
            public BradleySettings APC { get; set; }

            public class LootSettings
            {
                [JsonProperty(PropertyName = "The minimum amount of time a crate takes to hack (seconds)")]
                public int HackTimeMin { get; set; }

                [JsonProperty(PropertyName = "The maximum amount of time a crate takes to hack (seconds)")]
                public int HackTimeMax { get; set; }

                [JsonProperty(PropertyName = "Use custom loot table")]
                public bool UseCustom { get; set; }

                [JsonProperty(PropertyName = "Show loot container marker on map")]
                public bool ShowOnMap { get; set; }

                [JsonProperty(PropertyName = "Amount to spawn")]
                public int Amount { get; set; }

                [JsonProperty(PropertyName = "Local spawn positions")]
                public List<SpawnTransform> Spawns { get; set; }

                public void Spawn(HeistTrain heistTrain, TrainCar parent)
                {
                    for (int i = 0; i < Mathf.Min(Amount, Spawns.Count); i++)
                    {
                        HackableLockedCrate hackableLockedCrate = GameManager.server.CreateEntity(HACKABLE_CRATE_PREFAB, parent.transform.position) as HackableLockedCrate;

                        hackableLockedCrate.SetParent(parent, true, true);
                        hackableLockedCrate.transform.localPosition = Spawns[i].Position;
                        hackableLockedCrate.transform.localRotation = Quaternion.Euler(Spawns[i].Rotation);

                        Collider[] colliders = hackableLockedCrate.GetComponentsInChildren<Collider>(true);

                        for (int y = 0; y < colliders.Length; y++)
                        {
                            Physics.IgnoreCollision(parent.frontCollisionTrigger.triggerCollider, colliders[y]);
                            Physics.IgnoreCollision(parent.rearCollisionTrigger.triggerCollider, colliders[y]);
                        }

                        UnityEngine.Object.Destroy(hackableLockedCrate.GetComponent<Rigidbody>());

                        hackableLockedCrate.initialLootSpawn = !UseCustom;
                        hackableLockedCrate.enableSaving = false;
                        hackableLockedCrate.hackSeconds = HackableLockedCrate.requiredHackSeconds - Random.Range(HackTimeMin, HackTimeMax);

                        hackableLockedCrate.Spawn();

                        hackableLockedCrate.InvokeRandomized(() => hackableLockedCrate.SendNetworkUpdate(BasePlayer.NetworkQueue.Update), 2f, 2f, 0.25f);

                        if (hackableLockedCrate.mapMarkerInstance)
                        {
                            hackableLockedCrate.mapMarkerInstance.Kill(BaseNetworkable.DestroyMode.None);
                            hackableLockedCrate.mapMarkerInstance = null;
                        }
                        
                        if (i == 0 && ShowOnMap)
                            MarkerFollower.Create(hackableLockedCrate);
                        
                        if (UseCustom)
                            hackableLockedCrate.Invoke(() => LootTable.Data.Populate(hackableLockedCrate.inventory), 1f);

                        heistTrain.RegisterLockableCrate(hackableLockedCrate, HackTimeMax);
                    }
                }
            }

            public class AutoTurretSettings
            {
                [JsonProperty(PropertyName = "Amount to spawn")]
                public int Amount { get; set; }

                [JsonProperty(PropertyName = "Health")]
                public float Health { get; set; }

                [JsonProperty(PropertyName = "Local spawn positions")]
                public List<SpawnTransform> Spawns { get; set; }

                [JsonIgnore]
                private static ProtectionProperties TURRET_PROTECTION;

                public void Spawn(HeistTrain heistTrain, TrainCar parent)
                {
                    if (!TURRET_PROTECTION)
                    {
                        TURRET_PROTECTION = ScriptableObject.CreateInstance<ProtectionProperties>();
                        TURRET_PROTECTION.density = 100;
                        TURRET_PROTECTION.amounts = new float[] { 1f, 1f, 1f, 1f, 1f, 0.8f, 1f, 1f, 1f, 0.9f, 0.5f, 0.5f, 1f, 1f, 0f, 0.5f, 0f, 1f, 1f, 0f, 1f, 0.9f, 0f, 1f, 0f };
                    }

                    for (int i = 0; i < Mathf.Min(Amount, Spawns.Count); i++)
                    {
                        AutoTurret autoTurret = GameManager.server.CreateEntity(TURRET_PREFAB, parent.transform.position) as AutoTurret;

                        autoTurret.syncPosition = true;
                        
                        autoTurret.SetParent(parent);
                        autoTurret.transform.localPosition = Spawns[i].Position;
                        autoTurret.transform.localRotation = Quaternion.Euler(Spawns[i].Rotation);

                        autoTurret.baseProtection = TURRET_PROTECTION;

                        autoTurret.enableSaving = false;

                        autoTurret.Spawn();

                        autoTurret.InitializeHealth(Health, Health);

                        autoTurret.gameObject.AddComponent<TrainAutoTurret>();

                        heistTrain.autoTurrets.Add(autoTurret);
                    }
                }
            }

            public class SamSiteSettings
            {
                [JsonProperty(PropertyName = "Amount to spawn")]
                public int Amount { get; set; }

                [JsonProperty(PropertyName = "Health")]
                public float Health { get; set; }

                [JsonProperty(PropertyName = "Local spawn positions")]
                public List<SpawnTransform> Spawns { get; set; }

                [JsonIgnore]
                private static ProtectionProperties TURRET_PROTECTION;

                public void Spawn(HeistTrain heistTrain, TrainCar parent)
                {
                    if (!TURRET_PROTECTION)
                    {
                        TURRET_PROTECTION = ScriptableObject.CreateInstance<ProtectionProperties>();
                        TURRET_PROTECTION.density = 100;
                        TURRET_PROTECTION.amounts = new float[] { 1f, 1f, 1f, 1f, 1f, 0.8f, 1f, 1f, 1f, 0.9f, 0.5f, 0.5f, 1f, 1f, 0f, 0.5f, 0f, 1f, 1f, 0f, 1f, 0.9f, 0f, 1f, 0f };
                    }

                    for (int i = 0; i < Mathf.Min(Amount, Spawns.Count); i++)
                    {
                        SamSite samsite = GameManager.server.CreateEntity(SAMSITE_PREFAB, parent.transform.position) as SamSite;

                        samsite.syncPosition = true;
                        
                        samsite.SetParent(parent);
                        samsite.transform.localPosition = Spawns[i].Position;
                        samsite.transform.localRotation = Quaternion.Euler(Spawns[i].Rotation);

                        samsite.baseProtection = TURRET_PROTECTION;

                        samsite.enableSaving = false;

                        samsite.Spawn();

                        samsite.InitializeHealth(Health, Health);

                        heistTrain.samSites.Add(samsite);
                    }
                }
            }

            public class BradleySettings
            {
                [JsonProperty(PropertyName = "Amount to spawn")]
                public int Amount { get; set; }

                [JsonProperty(PropertyName = "Local spawn positions")]
                public List<SpawnTransform> Spawns { get; set; }

                [JsonProperty(PropertyName = "Health")]
                public float Health { get; set; }

                public void Spawn(HeistTrain heistTrain, TrainCar parent)
                {
                    for (int i = 0; i < Mathf.Min(Amount, Spawns.Count); i++)
                    {
                        BradleyAPC bradleyAPC = GameManager.server.CreateEntity(BRADLEY_PREFAB, parent.transform.position) as BradleyAPC;

                        bradleyAPC.SetParent(parent);
                        bradleyAPC.transform.localPosition = Spawns[i].Position;
                        bradleyAPC.transform.localRotation = Quaternion.Euler(Spawns[i].Rotation);

                        bradleyAPC.enableSaving = false;

                        bradleyAPC.Spawn();

                        bradleyAPC.InitializeHealth(Health, Health);

                        bradleyAPC.gameObject.AddComponent<TrainBradleyAPC>();

                        heistTrain.apcs.Add(bradleyAPC);
                    }
                }
            }

            public class NPCSettings
            {
                [JsonProperty(PropertyName = "Amount to spawn")]
                public int Amount { get; set; }

                [JsonProperty(PropertyName = "Starting health")]
                public int Health { get; set; }

                [JsonProperty(PropertyName = "NPC Names")]
                public List<string> Names { get; set; } = new List<string>();

                [JsonProperty(PropertyName = "Scientist kits (chosen at random)")]
                public List<string> Kits { get; set; }

                [JsonProperty(PropertyName = "Local spawn positions")]
                public List<SpawnTransform> Spawns { get; set; }

                [JsonIgnore]
                private const string TUNNEL_DWELLER = "assets/rust.ai/agents/npcplayer/humannpc/tunneldweller/npc_tunneldweller.prefab";

                [JsonIgnore]
                private static PlayerInventoryProperties[] m_TunnelDwellerLoadouts;

                public void Spawn(HeistTrain heistTrain, TrainCar parent)
                {
                    if (parent is TrainEngine)
                    {
                        ScientistNPC driverNpc = CreateScientist(heistTrain, parent, Vector3.zero);
                        FindAndMountDriverMountPoint(driverNpc, parent);
                        driverNpc.CancelInvoke(driverNpc.EquipTest);

                        heistTrain.npcPlayers.Add(driverNpc);
                    }

                    for (int i = 0; i < Math.Min(Amount, Spawns.Count); i++)
                    {
                        ScientistNPC scientistNpc = CreateScientist(heistTrain, parent, Spawns[i].Position);
                        heistTrain.npcPlayers.Add(scientistNpc);
                    }
                }

                private void FindAndMountDriverMountPoint(ScientistNPC scientistNpc, TrainCar trainCar)
                {
                    foreach (BaseVehicle.MountPointInfo allMountPoint in trainCar.allMountPoints)
                    {
                        if (allMountPoint == null || !allMountPoint.mountable || !allMountPoint.isDriver)
                            continue;

                        BasePlayer mounted = allMountPoint.mountable.GetMounted();
                        if (mounted)
                            continue;

                        allMountPoint.mountable.MountPlayer(scientistNpc);
                        return;
                    }
                }

                private ScientistNPC CreateScientist(HeistTrain heistTrain, TrainCar trainCar, Vector3 localPosition)
                {
                    ScientistNPC scientistNpc = GameManager.server.CreateEntity(SCIENTIST_PREFAB, trainCar.transform.TransformPoint(localPosition)) as ScientistNPC;

                    if (heistTrain.IsUnderground())
                    {
                        if (m_TunnelDwellerLoadouts == null) 
                        {
                            TunnelDweller tunnelDweller = GameManager.server.FindPrefab(TUNNEL_DWELLER).GetComponent<TunnelDweller>();
                            m_TunnelDwellerLoadouts = tunnelDweller.loadouts;
                        }

                        scientistNpc.loadouts = m_TunnelDwellerLoadouts;
                    }

                    scientistNpc.enableSaving = false;
                    
                    if (Names.Count > 0)
                        scientistNpc.displayName = Names.GetRandom();

                    scientistNpc.Spawn();

                    scientistNpc.Invoke(() =>
                    {
                        scientistNpc.startHealth = scientistNpc._health = scientistNpc._maxHealth = Health;
                        scientistNpc.InitializeHealth(Health, Health);
                    }, 1f);

                    if (Kits.Count > 0)
                    {
                        scientistNpc.inventory.Strip();
                        _GiveKit(scientistNpc, Kits.GetRandom());
                    }
                    
                    return scientistNpc;
                }
            }

            public class SpawnTransform
            {
                public SerializedVector Position { get; set; }

                [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
                public SerializedVector Rotation { get; set; } = Vector3.zero;

                public class SerializedVector
                {
                    public float X, Y, Z;

                    public SerializedVector() { }

                    public SerializedVector(float x, float y, float z)
                    {
                        X = x;
                        Y = y;
                        Z = z;
                    }

                    public static implicit operator Quaternion(SerializedVector v) => Quaternion.Euler(v.X, v.Y, v.Z);

                    public static implicit operator Vector3(SerializedVector v) => new Vector3(v.X, v.Y, v.Z);

                    public static implicit operator SerializedVector(Vector3 v) => new SerializedVector(v.x, v.y, v.z);

                    public static implicit operator SerializedVector(Quaternion v) => new SerializedVector(v.eulerAngles.x, v.eulerAngles.y, v.eulerAngles.z);
                }
            }
        }

        public class Container
        {
            [JsonProperty(PropertyName = "Minimum amount of items")]
            public int Minimum { get; set; }

            [JsonProperty(PropertyName = "Maximum amount of items")]
            public int Maximum { get; set; }

            [JsonProperty(PropertyName = "Items")]
            public Item[] Items { get; set; }

            [JsonIgnore]
            public bool IsValid => Items != null && Items.Length > 0;

            public void Populate(ItemContainer container)
            {
                if (container == null)
                    return;

                Clear(container);

                int amount = Random.Range(Minimum, Maximum + 1);

                List<Item> list = Pool.Get<List<Item>>();
                list.AddRange(Items);

                int itemCount = 0;
                while (itemCount < amount)
                {
                    int totalWeight = list.Sum((Item item) => Mathf.Max(1, item.Weight));
                    int random = Random.Range(0, totalWeight);

                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        Item data = list[i];

                        totalWeight -= Mathf.Max(1, data.Weight);

                        if (random >= totalWeight)
                        {
                            list.Remove(data);

                            global::Item item = data.Create();
                            if (item != null)
                            {
                                if (!item.MoveToContainer(container))
                                    item.Remove(0f);
                            }

                            if (data.SpawnWith != null)
                            {
                                item = data.SpawnWith.Create();
                                if (item != null)
                                {
                                    if (!item.MoveToContainer(container))
                                        item.Remove(0f);
                                }

                                itemCount++;
                            }

                            itemCount++;
                            break;
                        }
                    }

                    if (list.Count == 0)
                        list.AddRange(Items);
                }

                Pool.FreeUnmanaged(ref list);
            }

            private void Clear(ItemContainer container)
            {
                if (container == null || container.itemList == null)
                    return;

                while (container.itemList.Count > 0)
                {
                    global::Item item = container.itemList[0];
                    item.RemoveFromContainer();
                    item.Remove(0f);
                }
            }

            public class Item
            {
                [JsonProperty(PropertyName = "Shortname")]
                public string Name { get; set; }

                [JsonProperty(PropertyName = "Skin ID", DefaultValueHandling = DefaultValueHandling.Ignore)]
                public ulong Skin { get; set; } = 0UL;

                [JsonProperty(PropertyName = "Minimum amount")]
                public int Minimum { get; set; }

                [JsonProperty(PropertyName = "Maximum amount")]
                public int Maximum { get; set; }

                [JsonProperty(PropertyName = "Weight")]
                public int Weight { get; set; } = 1;

                [JsonProperty(PropertyName = "Blueprint", DefaultValueHandling = DefaultValueHandling.Ignore)]
                public bool Blueprint { get; set; }

                [JsonProperty(PropertyName = "Spawn with item", NullValueHandling = NullValueHandling.Ignore)]
                public Item SpawnWith { get; set; }

                [JsonIgnore]
                private ItemDefinition _itemDefinition;

                [JsonIgnore]
                private ItemDefinition Definition
                {
                    get
                    {
                        if (!_itemDefinition)
                        {
                            _itemDefinition = ItemManager.FindItemDefinition(Name);
                        }
                        return _itemDefinition;
                    }
                }

                [JsonIgnore]
                private static ItemDefinition _blueprintDefinition;

                [JsonIgnore]
                private static ItemDefinition BlueprintDefinition
                {
                    get
                    {
                        if (!_blueprintDefinition)
                        {
                            _blueprintDefinition = ItemManager.FindItemDefinition("blueprintbase");
                        }
                        return _blueprintDefinition;
                    }
                }

                public global::Item Create()
                {
                    if (!Definition)
                        return null;

                    global::Item item;
                    if (Blueprint)
                    {
                        item = ItemManager.Create(BlueprintDefinition, 1, 0UL);
                        item.blueprintTarget = Definition.itemid;
                        item.amount = Random.Range(Minimum, Maximum + 1);
                    }
                    else
                    {
                        item = ItemManager.Create(Definition, Random.Range(Minimum, Maximum + 1), Skin);
                    }

                    return item;
                }
            }
        }

        private class Datafile<T>
        {
            private readonly string name;

            private DynamicConfigFile dynamicConfigFile;

            public T Data;

            public Datafile(string name)
            {
                this.name = name;

                Load();
            }

            public void Load()
            {
                dynamicConfigFile = Interface.Oxide.DataFileSystem.GetFile(name);

                Data = dynamicConfigFile.ReadObject<T>();

                if (Data == null)
                    Data = (T)Activator.CreateInstance(typeof(T));
            }

            public void Save()
            {
                dynamicConfigFile.WriteObject(Data);
            }
        }

        private void LoadOrCreateLootTable()
        {
            LootTable = new Datafile<Container>($"{Name}/LootTable");

            if (!LootTable.Data.IsValid)
            {
                LootTable.Data = new Container
                {
                    Minimum = 4,
                    Maximum = 12,
                    Items = new Container.Item[]
                    {
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "smgbody",
                            Weight = 30,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "riflebody",
                            Weight = 30,
                        },
                        new Container.Item
                        {
                            Minimum = 2,
                            Maximum = 4,
                            Name = "techtrash",
                            Weight = 30,
                        },
                        new Container.Item
                        {
                            Minimum = 5,
                            Maximum = 6,
                            Name = "metalpipe",
                            Weight = 30,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "targeting.computer",
                            Weight = 30,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "cctv.camera",
                            Weight = 30,
                        },
                        new Container.Item
                        {
                            Minimum = 15,
                            Maximum = 25,
                            Name = "metal.refined",
                            Weight = 30,
                        },
                         new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "supply.signal",
                            Weight = 30,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "rifle.lr300",
                            Weight = 15,
                            SpawnWith = new Container.Item{Minimum = 10, Maximum = 25, Name = "ammo.rifle" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "rifle.m39",
                            Weight = 15,
                            SpawnWith = new Container.Item{Minimum = 10, Maximum = 25, Name = "ammo.rifle" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "rifle.l96",
                            Weight = 15,
                            SpawnWith = new Container.Item{Minimum = 10, Maximum = 25, Name = "ammo.rifle" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "shotgun.spas12",
                            Weight = 15,
                            SpawnWith = new Container.Item{Minimum = 8, Maximum = 12, Name = "ammo.shotgun" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "pistol.m92",
                            Weight = 20,
                            SpawnWith = new Container.Item{Minimum = 10, Maximum = 25, Name = "ammo.pistol" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "rifle.ak",
                            Weight = 20,
                            SpawnWith = new Container.Item{Minimum = 10, Maximum = 25, Name = "ammo.rifle" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "rifle.bolt",
                            Weight = 20,
                            SpawnWith = new Container.Item{Minimum = 10, Maximum = 25, Name = "ammo.rifle" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "smg.mp5",
                            Weight = 20,
                            SpawnWith = new Container.Item{Minimum = 15, Maximum = 25, Name = "ammo.pistol" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "lmg.m249",
                            Weight = 10,
                            SpawnWith = new Container.Item{Minimum = 15, Maximum = 25, Name = "ammo.rifle" }
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 3,
                            Name = "ammo.rocket.basic",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "metal.facemask",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "metal.plate.torso",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "explosive.timed",
                            Weight = 15,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "weapon.mod.lasersight",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "weapon.mod.smallscope",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "weapon.mod.8x.scope",
                            Weight = 15,
                        },
                        new Container.Item
                        {
                            Minimum = 5,
                            Maximum = 15,
                            Name = "ammo.rifle.explosive",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 5,
                            Maximum = 15,
                            Name = "ammo.rifle.incendiary",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 10,
                            Maximum = 25,
                            Name = "ammo.rifle.hv",
                            Weight = 20,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "heavy.plate.helment",
                            Weight = 10,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "heavy.plate.jacket",
                            Weight = 10,
                        },
                        new Container.Item
                        {
                            Minimum = 1,
                            Maximum = 1,
                            Name = "heavy.plate.pants",
                            Weight = 10,
                        },
                    }
                };

                LootTable.Save();
            }
        }

        private void LoadOrCreateLocomotiveLayout()
        {
            LocomotiveLayout = new Datafile<TrainCarLayout>($"{Name}/LocomotiveLayout");

            if (!LocomotiveLayout.Data.IsValid)
            {
                LocomotiveLayout.Data = new TrainCarLayout
                {
                    NPC = new TrainCarLayout.NPCSettings
                    {
                        Amount = 3,
                        Health = 200,
                        Kits = new List<string>(),
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-1.15f, 1.59f, 7.9f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(1.15f, 1.59f, 7.9f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-1.15f, 1.59f, -8.2f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(1.15f, 1.59f, -8.2f) }
                        },
                    },
                    Turret = new TrainCarLayout.AutoTurretSettings
                    {
                        Amount = 1,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0f, 4.641f, -7f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0f, 4.641f, 5f) },
                        },
                    },
                    Loot = new TrainCarLayout.LootSettings
                    {
                        Amount = 0,
                        HackTimeMin = 600,
                        HackTimeMax = 900,
                        ShowOnMap = true,
                        UseCustom = true,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { },
                    },
                    APC = new TrainCarLayout.BradleySettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    SamSite = new TrainCarLayout.SamSiteSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    }
                };

                LocomotiveLayout.Save();
            }
        }

        private void LoadOrCreateEngineLayout()
        {
            EngineLayout = new Datafile<TrainCarLayout>($"{Name}/EngineLayout");

            if (!EngineLayout.Data.IsValid)
            {
                EngineLayout.Data = new TrainCarLayout
                {
                    NPC = new TrainCarLayout.NPCSettings
                    {
                        Amount = 3,
                        Health = 200,
                        Kits = new List<string>(),
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.61f, 1.422f, -4.25f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.61f, 1.422f, 0f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.61f, 1.422f, 1.25f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.61f, 1.422f, -2f) },
                        },
                    },
                    Turret = new TrainCarLayout.AutoTurretSettings
                    {
                        Amount = 1,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.87f, 2.625f, 0.45f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.65f, 3.845f, 3.6f) },
                        },
                    },
                    Loot = new TrainCarLayout.LootSettings
                    {
                        Amount = 1,
                        HackTimeMin = 600,
                        HackTimeMax = 900,
                        ShowOnMap = true,
                        UseCustom = true,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.79f, 1.42f, -1.145531f), Rotation = new Vector3(0f, 270f, 0f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.79f, 1.42f, -2.474861f), Rotation = new Vector3(0f, 270f, 0f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.79f, 1.42f, -3.770401f), Rotation = new Vector3(0f, 270f, 0f) }
                        },
                    },
                    APC = new TrainCarLayout.BradleySettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    SamSite = new TrainCarLayout.SamSiteSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    }
                };

                EngineLayout.Save();
            }

            if (EngineLayout.Data.APC == null)
            {
                EngineLayout.Data.APC = new TrainCarLayout.BradleySettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> { }
                };
                EngineLayout.Save();
            }

            if (EngineLayout.Data.SamSite == null)
            {
                EngineLayout.Data.SamSite = new TrainCarLayout.SamSiteSettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> { }
                };
                EngineLayout.Save();
            }
        }

        private void LoadOrCreateWagonALayout()
        {
            WagonALayout = new Datafile<TrainCarLayout>($"{Name}/WagonALayout");
            if (!WagonALayout.Data.IsValid)
            {
                WagonALayout.Data = new TrainCarLayout
                {
                    NPC = new TrainCarLayout.NPCSettings
                    {
                        Amount = 3,
                        Health = 200,
                        Kits = new List<string>(),
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.77f, 1.54f, -6f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.77f, 1.54f, 3.29f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.77f, 1.54f, 3.29f) },
                        },
                    },
                    Turret = new TrainCarLayout.AutoTurretSettings
                    {
                        Amount = 1,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform
                            {
                                Position = new Vector3(-0.63f, 1.54f, 5.64f),
                                Rotation = new Vector3(0f, 180f, 0f)
                            }
                        }
                    },
                    Loot = new TrainCarLayout.LootSettings
                    {
                        Amount = 1,
                        HackTimeMin = 600,
                        HackTimeMax = 900,
                        ShowOnMap = true,
                        UseCustom = true,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.77f, 1.54f, -2.9f), Rotation = new Vector3(0f, 270f, 0f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.77f, 1.54f, -0.19f), Rotation = new Vector3(0f, 270f, 0f) },
                        },
                    },
                    APC = new TrainCarLayout.BradleySettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    SamSite = new TrainCarLayout.SamSiteSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    }
                };
                WagonALayout.Save();
            }

            if (WagonALayout.Data.APC == null)
            {
                WagonALayout.Data.APC = new TrainCarLayout.BradleySettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> { }
                };
                WagonALayout.Save();
            }

            if (WagonALayout.Data.SamSite == null)
            {
                WagonALayout.Data.SamSite = new TrainCarLayout.SamSiteSettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> { }
                };
                WagonALayout.Save();
            }
        }

        private void LoadOrCreateWagonBLayout()
        {
            WagonBLayout = new Datafile<TrainCarLayout>($"{Name}/WagonBLayout");
            if (!WagonBLayout.Data.IsValid)
            {
                WagonBLayout.Data = new TrainCarLayout
                {
                    NPC = new TrainCarLayout.NPCSettings
                    {
                        Amount = 3,
                        Health = 200,
                        Kits = new List<string>(),
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.77f, 1.54f, -6f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(-0.77f, 1.54f, 3.29f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.77f, 1.54f, 3.3f) },
                        },
                    },
                    Turret = new TrainCarLayout.AutoTurretSettings
                    {
                        Amount = 1,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform
                            {
                                Position = new Vector3(-0.63f, 1.54f, 5.64f),
                                Rotation = new Vector3(0f, 180f, 0f)
                            }
                        }
                    },
                    Loot = new TrainCarLayout.LootSettings
                    {
                        Amount = 1,
                        HackTimeMin = 600,
                        HackTimeMax = 900,
                        ShowOnMap = true,
                        UseCustom = true,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.77f, 1.54f, -2.9f), Rotation = new Vector3(0f, 270f, 0f) },
                            new TrainCarLayout.SpawnTransform { Position = new Vector3(0.77f, 1.54f, -0.19f), Rotation = new Vector3(0f, 270f, 0f) },
                        },
                    },
                    APC = new TrainCarLayout.BradleySettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    SamSite = new TrainCarLayout.SamSiteSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    }
                };
                WagonBLayout.Save();
            }

            if (WagonBLayout.Data.APC == null)
            {
                WagonBLayout.Data.APC = new TrainCarLayout.BradleySettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> { }
                };
                WagonBLayout.Save();
            }

            if (WagonBLayout.Data.SamSite == null)
            {
                WagonBLayout.Data.SamSite = new TrainCarLayout.SamSiteSettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> { }
                };
                WagonBLayout.Save();
            }
        }

        private void LoadOrCreateWagonCLayout()
        {
            WagonCLayout = new Datafile<TrainCarLayout>($"{Name}/WagonCLayout");
            if (!WagonCLayout.Data.IsValid)
            {
                WagonCLayout.Data = new TrainCarLayout
                {
                    NPC = new TrainCarLayout.NPCSettings
                    {
                        Amount = 0,
                        Health = 200,
                        Kits = new List<string>(),
                        Spawns = new List<TrainCarLayout.SpawnTransform> { },
                    },
                    Turret = new TrainCarLayout.AutoTurretSettings
                    {
                        Amount = 1,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform
                            {
                                Position = new Vector3(0f, 1.54f, 0f),
                            }
                        }
                    },
                    Loot = new TrainCarLayout.LootSettings
                    {
                        Amount = 0,
                        HackTimeMin = 600,
                        HackTimeMax = 900,
                        ShowOnMap = true,
                        UseCustom = true,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { },
                    },
                    APC = new TrainCarLayout.BradleySettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    SamSite = new TrainCarLayout.SamSiteSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform>
                        {
                            new TrainCarLayout.SpawnTransform
                            {
                                Position = new Vector3(0f, 1.54f, 0f),
                            }
                        }
                    }
                };
                WagonCLayout.Save();
            }

            if (WagonCLayout.Data.APC == null)
            {
                WagonCLayout.Data.APC = new TrainCarLayout.BradleySettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> { }
                };
                WagonCLayout.Save();
            }

            if (WagonCLayout.Data.SamSite == null)
            {
                WagonCLayout.Data.SamSite = new TrainCarLayout.SamSiteSettings
                {
                    Amount = 0,
                    Health = 500,
                    Spawns = new List<TrainCarLayout.SpawnTransform> 
                    {
                        new TrainCarLayout.SpawnTransform
                        {
                            Position = new Vector3(0f, 1.54f, 0f),
                        }
                    }
                };
                WagonCLayout.Save();
            }
        }

        private void LoadOrCreateWagonResourcesLayout()
        {
            WagonResourcesLayout = new Datafile<TrainCarLayout>($"{Name}/WagonResourcesLayout");
            if (!WagonResourcesLayout.Data.IsValid)
            {
                WagonResourcesLayout.Data = new TrainCarLayout
                {
                    NPC = new TrainCarLayout.NPCSettings
                    {
                        Amount = 0,
                        Health = 200,
                        Kits = new List<string>(),
                        Spawns = new List<TrainCarLayout.SpawnTransform> { },
                    },
                    Turret = new TrainCarLayout.AutoTurretSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    Loot = new TrainCarLayout.LootSettings
                    {
                        Amount = 0,
                        HackTimeMin = 600,
                        HackTimeMax = 900,
                        ShowOnMap = true,
                        UseCustom = true,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { },
                    },
                    APC = new TrainCarLayout.BradleySettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    SamSite = new TrainCarLayout.SamSiteSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    }
                };
                WagonResourcesLayout.Save();
            }
        }

        private void LoadOrCreateWagonLootLayout()
        {
            WagonLootLayout = new Datafile<TrainCarLayout>($"{Name}/WagonLootLayout");
            if (!WagonLootLayout.Data.IsValid)
            {
                WagonLootLayout.Data = new TrainCarLayout
                {
                    NPC = new TrainCarLayout.NPCSettings
                    {
                        Amount = 0,
                        Health = 200,
                        Kits = new List<string>(),
                        Spawns = new List<TrainCarLayout.SpawnTransform> { },
                    },
                    Turret = new TrainCarLayout.AutoTurretSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    Loot = new TrainCarLayout.LootSettings
                    {
                        Amount = 0,
                        HackTimeMin = 600,
                        HackTimeMax = 900,
                        ShowOnMap = true,
                        UseCustom = true,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { },
                    },
                    APC = new TrainCarLayout.BradleySettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    },
                    SamSite = new TrainCarLayout.SamSiteSettings
                    {
                        Amount = 0,
                        Health = 500,
                        Spawns = new List<TrainCarLayout.SpawnTransform> { }
                    }
                };
                WagonLootLayout.Save();
            }
        }

        //private void LoadOrCreateWagonDLayout()
        //{
        //    WagonDLayout = new Datafile<TrainCarLayout>($"{Name}/WagonDLayout");
        //    if (!WagonDLayout.Data.IsValid)
        //    {
        //        WagonDLayout.Data = new TrainCarLayout
        //        {
        //            NPC = new TrainCarLayout.NPCSettings
        //            {
        //                Amount = 0,
        //                Health = 200,
        //                Kits = new List<string>(),
        //                Spawns = new List<TrainCarLayout.SpawnTransform>
        //                {
        //                    new TrainCarLayout.SpawnTransform
        //                    {
        //                        Position = new Vector3(0f, 1.54f, -2.5f)
        //                    },
        //                    new TrainCarLayout.SpawnTransform
        //                    {
        //                        Position = new Vector3(0f, 1.54f, 0f)
        //                    },
        //                    new TrainCarLayout.SpawnTransform
        //                    {
        //                        Position = new Vector3(0f, 1.54f, 2.5f)
        //                    }
        //                },
        //            },
        //            Turret = new TrainCarLayout.AutoTurretSettings
        //            {
        //                Amount = 2,
        //                Health = 500,
        //                Spawns = new List<TrainCarLayout.SpawnTransform>
        //                {
        //                    new TrainCarLayout.SpawnTransform
        //                    {
        //                        Position = new Vector3(0f, 1.54f, 5f),
        //                    },
        //                    new TrainCarLayout.SpawnTransform
        //                    {
        //                        Position = new Vector3(0f, 1.54f, -5f),
        //                    }
        //                }
        //            },
        //            Loot = new TrainCarLayout.LootSettings
        //            {
        //                Amount = 0,
        //                HackTimeMin = 600,
        //                HackTimeMax = 900,
        //                ShowOnMap = true,
        //                UseCustom = true,
        //                Spawns = new List<TrainCarLayout.SpawnTransform> { },
        //            },
        //            APC = new TrainCarLayout.BradleySettings
        //            {
        //                Amount = 0,
        //                Health = 500,
        //                Spawns = new List<TrainCarLayout.SpawnTransform>
        //                {
        //                    new TrainCarLayout.SpawnTransform
        //                    {
        //                        Position = new Vector3(0f, 1.54f, 0f)
        //                    }
        //                }
        //            },
        //            SamSite = new TrainCarLayout.SamSiteSettings
        //            {
        //                Amount = 0,
        //                Health = 500,
        //                Spawns = new List<TrainCarLayout.SpawnTransform> { }
        //            }
        //        };
        //        WagonDLayout.Save();
        //    }

        //    if (WagonDLayout.Data.APC == null)
        //    {
        //        WagonDLayout.Data.APC = new TrainCarLayout.BradleySettings
        //        {
        //            Amount = 0,
        //            Health = 500,
        //            Spawns = new List<TrainCarLayout.SpawnTransform>
        //            {
        //                new TrainCarLayout.SpawnTransform
        //                {
        //                    Position = new Vector3(0f, 1.54f, 0f)
        //                }
        //            }
        //        };
        //        WagonDLayout.Save();
        //    }

        //    if (WagonDLayout.Data.SamSite == null)
        //    {
        //        WagonDLayout.Data.SamSite = new TrainCarLayout.SamSiteSettings
        //        {
        //            Amount = 0,
        //            Health = 500,
        //            Spawns = new List<TrainCarLayout.SpawnTransform> { }
        //        };
        //        WagonDLayout.Save();
        //    }
        //}
        #endregion

        #region Localization
        private static Action<BasePlayer, string, object[]> Message;

        private void ChatMessage(BasePlayer player, string key, params object[] args)
        {
            if (args?.Length > 0)
                player.ChatMessage(string.Format(lang.GetMessage(key, this, player.UserIDString), args));
            else player.ChatMessage(lang.GetMessage(key, this, player.UserIDString));
        }

        private void BroadcastMessage(string key, params object[] args)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                ChatMessage(player, key, args);
        }

        private Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["Chat.NoRailLoop"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> Either no rail loops have been detected or rail loops have not finished processing",
            ["Chat.Start"] = "<color=#62cd32>/trainheist start</color> - Starts the train heist",
            ["Chat.Stop"] = "<color=#62cd32>/trainheist stop</color> - Cancels the train heist",
            ["Chat.InProgress"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> There is already a train heist in progress",
            ["Chat.Starting"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> Starting train heist event",
            ["Chat.NoHeist"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> There is not a train heist in progress",
            ["Chat.Cancelled"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> Train heist event cancelled",
            ["Chat.FailedNearby"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> Failed to find a train track nearby. Move closer to a train track",

            ["Notification.EventStarted"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> A Cobalt train is carrying valuable cargo on the island.\nIt was seen around <color=#62cd32>{0}</color> heading <color=#62cd32>{1}</color>\nIntercept it!",
            ["Notification.EventStarted.Underground"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> A Cobalt train is carrying valuable cargo in the underground rail network.\nIt was seen around <color=#62cd32>{0}</color> heading <color=#62cd32>{1}</color>\nIntercept it!",

            ["Notification.EventFinishedNoContest"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> The Cobalt train has arrived at its destination!",
            ["Notification.EventFinished"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> The Cobalt train never arrived at its destination. A heist crew got lucky!",
            ["Notification.BackupInbound"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> A Cobalt backup crew is inbound, and other heisters have been notified!",
            ["Notification.HeistStarted"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> A heist crew are hitting the train! Last seen around <color=#62cd32>{0}</color> heading <color=#62cd32>{1}</color>\nCatch them to contest it!",
            ["Notification.ControlsJammed"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> You can not enter the cockpit. The train controls have been jammed!",
            ["Notification.SelfDestruct"] = "<color=#b4b4b4>[<color=#62cd32>Train Heist</color>]</color> Self destruct sequence engaged. You have <color=#62cd32>{0} seconds</color> to get out of there!",
        };
        #endregion
    }

    namespace TrainHeistExtensions
    {
        public static class QueryBufferEx
        {
            public static bool Contains<T>(this T[] array, T value, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    T t = array[i];
                    if (t == null)
                        continue;

                    if (t.Equals(value))
                        return true;
                }

                return false;
            }     
        } 
    }
}
  