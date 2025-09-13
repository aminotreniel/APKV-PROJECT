using System.Collections.Generic;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using FrustumPlanes = Unity.Rendering.FrustumPlanes;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    public struct ScatteredPointCloudNeedsScatterTag : IComponentData
    {
    }

    public struct ScatteredPointCloudPrefabLoadPendingTag : IComponentData
    {
    }

    public struct ScatteredPointCloudReadyToScatterTag : IComponentData
    {
    }

    public struct ScatteredPointCloudScatteringBatchesCreatedTag : IComponentData
    {
    }

    public struct GeneratedScatterInstanceTag : IComponentData
    {
    }
    
    public struct IgnoreMaxScatterDistanceTag : IComponentData
    {
    }
    
    public struct NeedsMotionVectorsTag : IComponentData
    {
    }
    
    public struct ScatteredInstanceRootTag : IComponentData
    {
    }

    public struct ScatterPointCloudInstanceBatchPendingTag : IComponentData
    {
    }
    
    public struct ScatterPointCloudInstanceBatchScatteringTag : IComponentData
    {
    }

    public struct ScatterPointCloudInstanceBatchScatteredTag : IComponentData
    {
    }

    public struct ScatteringPrewarmPosition : IComponentData
    {
        public float3 Value;
    }
    
    public struct ScatteredInstanceParent : IComponentData
    {
        public Entity Value;
    }

    public struct ScatteredInstanceImportanceData: IComponentData
    {
        public float RelativeDensity; 
    }

    public struct ScatteredInstanceBatch : ISharedComponentData
    {
        public uint RuntimeBatchId;
    }

    public struct ScatterPointCloudTileInfo : ISharedComponentData
    {
        public float2 SizeMinMax;
        public int3 DensityMinMaxAverage;
    }

    public struct ScatterPointCloudScatterId : ISharedComponentData
    {
        public Hash128 ScatterId;
    }
    
    public struct ScatterPointCloudScatterGroupId : ISharedComponentData
    {
        public Hash128 Value;
    }

    public struct ScatterPointCloudPreloadPrefab : IBufferElementData
    {
        public EntityPrefabReference PrefabRef;
    }
    
    public struct ScatterPointCloudPrefab : IComponentData
    {
        public EntityPrefabReference PrefabRef;
    }

    public struct PointCloudSystemConfigData : IComponentData
    {
        public float InstanceTargetSizeOnScreen;
        public float InstanceUnloadTargetSizeMargin;
        public float InstanceModelSizeInterp;
        public float MaxVisibleDistance;
        public float FlattenHeightDifferenceMultiplier;
        
        public int MaxNumberOfSpawnedInstancesPerBatch;
        public bool DisableScatteringWhilePrefabsLoading;
        public bool ImmediateBlockingScatter;

        public bool DisableScatter;
    }

    public struct ScatterPointCloudInstanceBatch : IComponentData
    {
        public Entity InstanceDataEntity;
        public float2 MinMaxModelSize;
        public AABB BatchBounds;
        public int AttributeRangeIndex;
        public uint RuntimeId;
    }
    
    public struct ScatterPointCloudInstanceData : IComponentData
    {
        public Hash128 ScatterId; //Id of a given tile in a point cloud
        public Hash128 ScatterGroupId; //Id of an authoring source (many point clouds share this)
        public BlobAssetReference<ScatterPointCloudInstanceAttributes> Attributes;
        public BlobAssetReference<ScatterPointCloudAttributeRange> AttributeRanges;
        public BlobAssetReference<ScatterPointCloudInstanceSizeMinMax> InstanceSizeMinMax;
        public BlobAssetReference<ScatterPointCloudPointImportanceData> ImportanceData;
        public AABB PointCloudBounds;
        public float CellSizeInMeters;
        public int ExtraDataMask;
    }

    public struct ScatterPointCloudCleanup : ICleanupComponentData
    {
        public Hash128 ScatterId;
        public Hash128 ScatterGroupId;
        public EntityPrefabReference PrefabRef;
        public BlobAssetReference<ScatterPointCloudInstanceAttributes> Attributes;
        public BlobAssetReference<ScatterPointCloudAttributeRange> AttributeRanges;
        public BlobAssetReference<ScatterPointCloudPointImportanceData> ImportanceData;
    }

    public struct ScatterPointCloudInstanceAttributes
    {
        public BlobArray<float3> Positions;
        public BlobArray<quaternion> Orientations;
        public BlobArray<float> Scales;
        public BlobArray<int> ExtraDataIndex;
    }

    public struct ScatterPointCloudPointImportanceData
    {
        public BlobArray<float> PerPointImportanceData;
        public BlobArray<int3> PerTileDensityMinMaxAverage;
    }

    public struct ScatterPointCloudAttributeRange
    {
        public BlobArray<int2> OffsetCount;
    }
    public struct ScatterPointCloudInstanceSizeMinMax
    {
        public BlobArray<float2> SizeMinMax;
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateBefore(typeof(ReplicatePointCloudExtraDataSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial class ScatterPointCloudSystem : SystemBase
    {
        [System.Serializable]
        public struct PartitioningInfo
        {
            public AABB bounds;
            public float cellSize;

            public int2 GetNumberOfCells()
            {
                int x = (int)math.ceil(bounds.Size.x / cellSize);
                int y = (int)math.ceil(bounds.Size.z / cellSize);
                return new int2(x, y);
            }

            public int2 GetCellIndex(float3 pos)
            {
                float2 p = math.clamp(pos.xz, bounds.Min.xz, bounds.Max.xz);
                p -= bounds.Min.xz;
                p /= cellSize;
                return math.clamp((int2)math.floor(p), int2.zero, GetNumberOfCells() - new int2(1, 1));
            }

            public int GetFlatCellIndex(float3 pos)
            {
                int2 ind = GetCellIndex(pos);
                return FlattenCellIndex(ind);
            }

            public int FlattenCellIndex(int2 ind)
            {
                return ind.x + GetNumberOfCells().x * ind.y;
            }

            public float2 GetCellCenter(int flatIndex)
            {
                int w = GetNumberOfCells().x;
                int x = flatIndex % w;
                int y = flatIndex / w;
                return GetCellCenter(new int2(x, y));
            }
            
            public AABB GetCellBounds(int flatIndex)
            {
                int w = GetNumberOfCells().x;
                int x = flatIndex % w;
                int y = flatIndex / w;

                float2 center2D = GetCellCenter(new int2(x, y));
                
                AABB aabb;
                aabb.Center = new float3(center2D.x, bounds.Center.y, center2D.y);
                aabb.Extents = new float3(cellSize * 0.5f, bounds.Extents.y, cellSize * 0.5f);
                return aabb;
            }

            public float2 GetCellCenter(int2 index)
            {
                return bounds.Min.xz + new float2(index.x * cellSize + cellSize * 0.5f,
                    index.y * cellSize + cellSize * 0.5f);
            }
        }

        struct PendingPrefab
        {
            public EntityPrefabReference reference;
            public Entity loadedPrefab;
            public int numberOfAttempts;
        }
        struct LoadedPrefab
        {
            public Entity mainWorldRoot;
            public Entity stagingWorldRoot;
            public NativeHashMap<int, Entity> variantPerPointCloud;

            public void Destroy(EntityManager stagingEntMngr)
            {
                stagingEntMngr.DestroyEntity(stagingWorldRoot);
                foreach (var variant in variantPerPointCloud)
                {
                    stagingEntMngr.DestroyEntity(variant.Value);
                }
                variantPerPointCloud.Dispose();
            }
        }

        public struct PendingBatchComparer : IComparer<PendingBatchComparer.BatchSortData>
        {
            public struct BatchSortData
            {
                public float sortValue;
                public int batchIndex;
            }
            
            public float3 center;

            // Compares by Length, Height, and Width.
            public int Compare(BatchSortData a,
                BatchSortData b)
            {

                if (a.sortValue < b.sortValue)
                {
                    return -1;
                }

                if (b.sortValue < a.sortValue)
                {
                    return 1;
                }

                return 0;
            }
        }
        
        [BurstCompile]
        private struct FindInstanceBatchesToProcess : IJobChunk
        {
            public float3 ScatterAreaCenter;
            public float TanFOV;
            public float TargetRelativeSize;
            public float ModelMinMaxSizeInterp;
            public float MaxDistance;
            public float FlattenHeightDifferenceMultiplier;

            [ReadOnly]
            public NativeArray<ScatteringPrewarmPosition> PrewarmPositions;
            [ReadOnly]
            public ComponentTypeHandle<ScatterPointCloudInstanceBatch> InstanceBatchType;
            [ReadOnly]
            public EntityTypeHandle EntityTypeHandle;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<Entity> SectionsToProcess;
            [NativeDisableUnsafePtrRestriction]
            public UnsafeAtomicCounter32 Counter;
            public bool ProcessIfSmallerThanTarget;

            float GetDistanceToPointCloud(AABB pcBounds)
            {
                float distSq;
                {
                    var closestPoint = math.clamp(ScatterAreaCenter, pcBounds.Min, pcBounds.Max);
                    var delta = closestPoint - ScatterAreaCenter;
                    delta.y *= FlattenHeightDifferenceMultiplier;
                    distSq = math.max(math.lengthsq(delta), 0.000001f);
                }
                

                if (PrewarmPositions.IsCreated)
                {
                    for (int i = 0; i < PrewarmPositions.Length; ++i)
                    {
                        var p = PrewarmPositions[i].Value;
                        var closestPrewarmPos = math.clamp(p, pcBounds.Min, pcBounds.Max);
                        var delta = closestPrewarmPos - p;
                        delta.y *= FlattenHeightDifferenceMultiplier;
                        float prewarmDist = math.max(math.lengthsq(delta), 0.000001f);
                        distSq = math.min(distSq, prewarmDist);
                    }
                }
                return math.sqrt(distSq);
            }
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var batches = chunk.GetNativeArray(ref InstanceBatchType);
                var entitites = chunk.GetNativeArray(EntityTypeHandle);

                bool ignoreDistance = chunk.Has<IgnoreMaxScatterDistanceTag>();
                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    var batch = batches[i];
                    var pcBounds = batch.BatchBounds;

                    float dist = GetDistanceToPointCloud(pcBounds);
                    
                    bool outsideOfMaxDistance = !ignoreDistance && MaxDistance < dist;

                    float distMin = batch.MinMaxModelSize.x / (TanFOV * TargetRelativeSize);
                    float distMax = batch.MinMaxModelSize.y / (TanFOV * TargetRelativeSize);

                    float compDistance = math.lerp(distMin, distMax, ModelMinMaxSizeInterp);
                    bool smallerThanTarget = dist > compDistance;

                    bool needProcessing = ProcessIfSmallerThanTarget ? (smallerThanTarget || outsideOfMaxDistance) : (!smallerThanTarget && !outsideOfMaxDistance);
                    
                    if (needProcessing)
                    {
                        var ind = Counter.Add(1);
                        SectionsToProcess[ind] = entitites[i];
                    }
                    
                }
            }
        }

        //members
        
        private static readonly ProfilerMarker s_RemoveDeletedEntitiesMarker = new("RemoveDeletedEntities");
        private static readonly ProfilerMarker s_PrepareNewPayloadBatchesMarker = new("PrepareNewPayloadBatches");
        private static readonly ProfilerMarker s_ProcessBatches = new("ProcessBatches");
        private static readonly ProfilerMarker s_ProcessBatchesLoad = new("ProcessBatchesLoad");
        private static readonly ProfilerMarker s_ProcessBatchesLoadSort = new("ProcessBatchesLoadSort");
        private static readonly ProfilerMarker s_ProcessBatchesUnload = new("ProcessBatchesUnload");

        private UnsafeAtomicCounter32 m_BatchesToLoadCounter;
        private UnsafeAtomicCounter32 m_BatchesToUnloadCounter;

        private NativeArray<Entity> m_BatchesToLoad;
        private NativeArray<Entity> m_BatchesToUnload;
        
        private ComponentTypeHandle<ScatterPointCloudInstanceBatch> m_InstanceBatchTypeHandle;
        private ComponentTypeHandle<IgnoreMaxScatterDistanceTag> m_IgnoreMaxDistanceTagTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;
        
        private EntityQuery m_NewScatteringPayloadsQuery;
        private EntityQuery m_PendingPrefabPayloadsQuery;
        private EntityQuery m_PayloadsReadyToScatterQuery;
        private EntityQuery m_DestroyedPayloadsQuery;
        private EntityQuery m_PotentiallyOutOfSyncPayloadsQuery;
        private EntityQuery m_AllEntitiesWithScatterIdQuery;
        private EntityQuery m_AllScatteredInstancesQuery;
        private EntityQuery m_PrefabPreloadEntitiesQuery;
        private EntityQuery m_PrewarmPositionsQuery;
        private EntityArchetype m_PendingBatchInstancesArchetype;
        
        private EntityQuery m_PendingInstanceBatchesQuery;
        private EntityQuery m_ScatteringInstanceBatchesQuery;
        private EntityQuery m_ScatteredInstanceBatchesQuery;

        private NativeHashMap<Hash128, int> m_PrefabRefCounts;
        private NativeHashMap<Hash128, LoadedPrefab> m_LoadedPrefabs;
        private NativeHashMap<Hash128, PendingPrefab> m_PendingPrefabs;

        private uint m_NextRuntimeId;
        
        //staging
        private World m_StagingWorld;
        private SystemHandle m_StagingSystem;
        private EntityQuery m_ScatteredInstancesStagingQuery;
        private EntityQuery m_AllEntitiesWithScatterIdStagingQuery;
        private EntityArchetype m_StagingWorldPayloadArchetype;
        
        
        protected override void OnCreate()
        {
            m_StagingWorld = new World("ScatterStagingWorld", WorldFlags.Staging);
            m_StagingSystem = m_StagingWorld.CreateSystem<ScatterPointCloudStagingSystem>();

            m_PendingBatchInstancesArchetype =
                EntityManager.CreateArchetype(typeof(ScatterPointCloudInstanceBatch),
                    typeof(ScatterPointCloudScatterId), typeof(ScatterPointCloudScatterGroupId));

            m_StagingWorldPayloadArchetype = m_StagingWorld.EntityManager.CreateArchetype(
                typeof(ScatterPointCloudStagingSystem.InstantiatePayload), typeof(ScatterPointCloudScatterId), typeof(ScatterPointCloudScatterGroupId));

            unsafe
            {
                m_BatchesToLoadCounter = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
                m_BatchesToUnloadCounter = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
            }
            m_BatchesToLoadCounter.Reset();
            m_BatchesToUnloadCounter.Reset();
            
            m_InstanceBatchTypeHandle = EntityManager.GetComponentTypeHandle<ScatterPointCloudInstanceBatch>(true);
            m_IgnoreMaxDistanceTagTypeHandle = EntityManager.GetComponentTypeHandle<IgnoreMaxScatterDistanceTag>(true);
            m_EntityTypeHandle = EntityManager.GetEntityTypeHandle();
            
            m_PrefabRefCounts = new NativeHashMap<Hash128, int>(64, Allocator.Persistent);
            m_LoadedPrefabs = new NativeHashMap<Hash128, LoadedPrefab>(64, Allocator.Persistent);
            m_PendingPrefabs = new NativeHashMap<Hash128, PendingPrefab>(64, Allocator.Persistent);

            
            m_NewScatteringPayloadsQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudInstanceData>(),
                ComponentType.ReadOnly<ScatterPointCloudPrefab>(),
                ComponentType.ReadOnly<ScatteredPointCloudNeedsScatterTag>());

            m_PendingPrefabPayloadsQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudPrefab>(),
                ComponentType.ReadOnly<ScatteredPointCloudPrefabLoadPendingTag>());

            m_PayloadsReadyToScatterQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudInstanceData>(),
                ComponentType.ReadOnly<ScatterPointCloudPrefab>(),
                ComponentType.ReadOnly<ScatteredPointCloudReadyToScatterTag>());

            m_DestroyedPayloadsQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudCleanup>(),
                ComponentType.Exclude<ScatterPointCloudScatterId>());

            m_PotentiallyOutOfSyncPayloadsQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudCleanup>(),
                ComponentType.ReadOnly<ScatterPointCloudScatterId>());
            
            m_AllEntitiesWithScatterIdQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudScatterId>());
            
            m_AllScatteredInstancesQuery = EntityManager.CreateEntityQuery(
                ComponentType.Exclude<Prefab>(),
                ComponentType.ReadOnly<GeneratedScatterInstanceTag>(),
                ComponentType.ReadOnly<ScatteredInstanceBatch>());

            m_PrefabPreloadEntitiesQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudPreloadPrefab>());
            
            m_ScatteredInstancesStagingQuery = m_StagingWorld.EntityManager.CreateEntityQuery(
                ComponentType.Exclude<Prefab>(),
                ComponentType.ReadOnly<GeneratedScatterInstanceTag>());
            
            m_AllEntitiesWithScatterIdStagingQuery = m_StagingWorld.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudScatterId>());
            
            m_PendingInstanceBatchesQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudInstanceBatch>(),
                ComponentType.ReadOnly<ScatterPointCloudInstanceBatchPendingTag>());
                
            m_ScatteringInstanceBatchesQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudInstanceBatch>(),
                ComponentType.ReadOnly<ScatterPointCloudInstanceBatchScatteringTag>());
            
            m_ScatteredInstanceBatchesQuery =  EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterPointCloudInstanceBatch>(),
                ComponentType.ReadOnly<ScatterPointCloudInstanceBatchScatteredTag>());

            m_PrewarmPositionsQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ScatteringPrewarmPosition>());
            
            EntityManager.AddComponent<PointCloudSystemConfigData>(SystemHandle);
            ref var config = ref EntityManager.GetComponentDataRW<PointCloudSystemConfigData>(SystemHandle).ValueRW;
            config.MaxNumberOfSpawnedInstancesPerBatch = 10000000;
            
            config.InstanceTargetSizeOnScreen = 0.01f;
            config.InstanceUnloadTargetSizeMargin = 0.05f;
            config.InstanceModelSizeInterp = 0.5f;
            config.MaxVisibleDistance = float.MaxValue;
            config.DisableScatter = false;
            config.FlattenHeightDifferenceMultiplier = 1;

            m_NextRuntimeId = 0;
        }

        protected override void OnDestroy()
        {
            foreach (var prefabEntry in m_LoadedPrefabs)
            {
                prefabEntry.Value.Destroy(m_StagingWorld.EntityManager);
            }
        
            m_PendingPrefabPayloadsQuery.Dispose();
            m_NewScatteringPayloadsQuery.Dispose();
            m_PayloadsReadyToScatterQuery.Dispose();
            m_DestroyedPayloadsQuery.Dispose();
            m_AllEntitiesWithScatterIdQuery.Dispose();
            m_AllScatteredInstancesQuery.Dispose();
            
            m_PendingInstanceBatchesQuery.Dispose();
            m_ScatteringInstanceBatchesQuery.Dispose();
            m_ScatteredInstanceBatchesQuery.Dispose();
            m_PrewarmPositionsQuery.Dispose();
            
            m_ScatteredInstancesStagingQuery.Dispose();
            m_AllEntitiesWithScatterIdStagingQuery.Dispose();

            m_StagingWorld.Dispose();

            m_PrefabRefCounts.Dispose();

            
            m_LoadedPrefabs.Dispose();
            m_PendingPrefabs.Dispose();
            
            unsafe
            {
                UnsafeUtility.Free(m_BatchesToLoadCounter.Counter, Allocator.Persistent);
                UnsafeUtility.Free(m_BatchesToUnloadCounter.Counter, Allocator.Persistent);
            }
            
            if(m_BatchesToLoad.IsCreated)
                m_BatchesToLoad.Dispose();
            if(m_BatchesToUnload.IsCreated)
                m_BatchesToUnload.Dispose();
            
        }

        protected override void OnUpdate()
        {
            var config = EntityManager.GetComponentData<PointCloudSystemConfigData>(SystemHandle);
            
            m_StagingSystem.Update(m_StagingWorld.Unmanaged);

            //prefab loading
            HandlePreloadPrefabs(ref CheckedStateRef);
            InitializePrefabLoading(ref CheckedStateRef);

            bool canAccessStagingWorld = !IsStagingWorldBusy();
            //only operate on entities when staging world is idle
            if (canAccessStagingWorld)
            {
                MoveInstancesFromStaging(ref CheckedStateRef);
                
                HandlePendingPrefabLoading(ref CheckedStateRef);
                CleanRemovedEntities(ref CheckedStateRef);
                
#if UNITY_EDITOR
                HandleIncrementalBakingChanges(ref CheckedStateRef);
#endif
                if (config.DisableScatteringWhilePrefabsLoading && HasPendingPrefabsToLoad())
                {
                    return;
                }
                
                //TODO: move this to a job? only reask and resort list when it runs for? For now done in main thread and always cleared 
                using (var marker = s_PrepareNewPayloadBatchesMarker.Auto())
                {
                    HandlePayloadsReadyToScatter(ref CheckedStateRef);
                }

                using (var marker = s_ProcessBatches.Auto())
                {
                    var cts = World.GetExistingSystem<CameraTrackingSystem>();
                    float3 camPos = float3.zero;
                    float tanFov = 1.732f;
                    if (cts != SystemHandle.Null)
                    {
                        var cData = EntityManager.GetComponentData<CameraTrackingSystem.CameraTrackingData>(cts);
                        camPos = cData.CameraPosition;
                        tanFov = cData.TanFOV;
                    }
                    
                    float screenTargetSize = config.DisableScatter ? float.MaxValue: config.InstanceTargetSizeOnScreen;
                    Dependency = FindBatchesToProcess(ref CheckedStateRef, Dependency, screenTargetSize, config.InstanceUnloadTargetSizeMargin, 
                        config.InstanceModelSizeInterp, tanFov, camPos, config.MaxVisibleDistance, config.FlattenHeightDifferenceMultiplier);
                    Dependency.Complete();

                    int batchesToLoadCount;
                    int batchesToUnloadCount;
                    unsafe
                    {
                        batchesToLoadCount = *m_BatchesToLoadCounter.Counter;
                        batchesToUnloadCount = *m_BatchesToUnloadCounter.Counter;
                    }

                    using (var markerLoad = s_ProcessBatchesLoad.Auto())
                    {
                        LoadBatches(ref CheckedStateRef, config, m_BatchesToLoad.Slice(0, batchesToLoadCount));
                    }
                    
                    using (var markerUnload = s_ProcessBatchesUnload.Auto())
                    {
                        UnloadBatches(ref CheckedStateRef, config, m_BatchesToUnload.Slice(0, batchesToUnloadCount));
                    }
                }
                
                
                
                //in case we want to scatter synchronously, busyloop here
                if (config.ImmediateBlockingScatter)
                {
                    bool scatteringStillRunning = true;
                    while (scatteringStillRunning)
                    {
                        m_StagingSystem.Update(m_StagingWorld.Unmanaged);
                        if (m_StagingWorld.EntityManager.CanBeginExclusiveEntityTransaction())
                        {
                            var stagingState =
                                m_StagingWorld.EntityManager.GetComponentData<ScatterPointCloudStagingSystem.CurrentState>(m_StagingSystem);
                            if (stagingState.Value == ScatterPointCloudStagingSystem.State.Idle)
                            {
                                scatteringStillRunning = false;
                            }
                        }
                    }

                    MoveInstancesFromStaging(ref CheckedStateRef);
                }
                
            }
        }

        bool IsStagingWorldBusy()
        {
            if (m_StagingWorld.EntityManager.CanBeginExclusiveEntityTransaction())
            {
                var stagingState =
                    m_StagingWorld.EntityManager.GetComponentData<ScatterPointCloudStagingSystem.CurrentState>(
                        m_StagingSystem);
                return stagingState.Value != ScatterPointCloudStagingSystem.State.Idle;
            }

            return true;
        }
        
        void HandlePayloadsReadyToScatter(ref SystemState state)
        {
            using var readyToScatterEntities = m_PayloadsReadyToScatterQuery.ToEntityArray(Allocator.Temp);
            if (readyToScatterEntities.Length == 0)
            {
                return;
            }

            int entityCount = 0;
            for (int i = 0; i < readyToScatterEntities.Length; ++i)
            {
                ScatterPointCloudInstanceData instanceData =
                    state.EntityManager.GetComponentData<ScatterPointCloudInstanceData>(readyToScatterEntities[i]);

                if (!instanceData.Attributes.IsCreated || !instanceData.AttributeRanges.IsCreated) continue;
                ref var attributeRanges = ref instanceData.AttributeRanges.Value.OffsetCount;

                for(int rangeIndex = 0; rangeIndex < attributeRanges.Length; ++rangeIndex)
                {
                    if (attributeRanges[rangeIndex].y > 0)
                    {
                        entityCount += 1;
                    }
                }
            }

            //setup batches to create
            var batchEntities =
                state.EntityManager.CreateEntity(m_PendingBatchInstancesArchetype, entityCount, Allocator.Temp);
            int batchEntityOffset = 0;

            for (int i = 0; i < readyToScatterEntities.Length; ++i)
            {
                ScatterPointCloudInstanceData instanceData =
                    state.EntityManager.GetComponentData<ScatterPointCloudInstanceData>(readyToScatterEntities[i]);
                bool ignoreMaxDistance =
                    state.EntityManager.HasComponent<IgnoreMaxScatterDistanceTag>(readyToScatterEntities[i]);

                if (!instanceData.Attributes.IsCreated || !instanceData.AttributeRanges.IsCreated || !instanceData.InstanceSizeMinMax.IsCreated) continue;

                PartitioningInfo partitioningInfo;
                partitioningInfo.bounds = instanceData.PointCloudBounds;
                partitioningInfo.cellSize = instanceData.CellSizeInMeters;

                ref var attributeRangeOffsetCountArray = ref instanceData.AttributeRanges.Value.OffsetCount;
                
                for (int batchIndex = 0;
                     batchIndex < instanceData.AttributeRanges.Value.OffsetCount.Length;
                     ++batchIndex)
                {
                    if (attributeRangeOffsetCountArray[batchIndex].y == 0) continue;
                    
                    var batchEntity = batchEntities[batchEntityOffset];
                    var batchComponent =
                        state.EntityManager.GetComponentData<ScatterPointCloudInstanceBatch>(batchEntity);

                    batchComponent.AttributeRangeIndex = batchIndex;
                    batchComponent.InstanceDataEntity = readyToScatterEntities[i];
                    batchComponent.BatchBounds = partitioningInfo.GetCellBounds(batchIndex);
                    batchComponent.RuntimeId = m_NextRuntimeId++;
                    batchComponent.MinMaxModelSize = instanceData.InstanceSizeMinMax.Value.SizeMinMax[batchIndex];

                    state.EntityManager.SetComponentData(batchEntity, batchComponent);
                    state.EntityManager.SetSharedComponent(batchEntity,
                        new ScatterPointCloudScatterId() { ScatterId = instanceData.ScatterId });
                    state.EntityManager.SetSharedComponent(batchEntity,
                        new ScatterPointCloudScatterGroupId() { Value = instanceData.ScatterGroupId });
                    
                    state.EntityManager.AddComponent<ScatterPointCloudInstanceBatchPendingTag>(batchEntity);

                    if (ignoreMaxDistance)
                    {
                        state.EntityManager.AddComponent<IgnoreMaxScatterDistanceTag>(batchEntity);
                    }
                    
                    ++batchEntityOffset;
                }
            }

            batchEntities.Dispose();

            state.EntityManager.AddComponent<ScatteredPointCloudScatteringBatchesCreatedTag>(readyToScatterEntities);
            state.EntityManager.RemoveComponent<ScatteredPointCloudReadyToScatterTag>(readyToScatterEntities);
            
        }

        JobHandle FindBatchesToProcess(ref SystemState state, JobHandle deps, float targetScreenSize, float unloadTargetMarginRatio, float modelSizeInterp, float tanFOV, float3 targetAreaCenter, float maxDistance, float flattenDistance)
        {
            CommonScatterUtilities.Resize(ref m_BatchesToLoad, m_PendingInstanceBatchesQuery.CalculateEntityCount(), Allocator.Persistent);
            CommonScatterUtilities.Resize(ref m_BatchesToUnload, m_ScatteredInstanceBatchesQuery.CalculateEntityCount(), Allocator.Persistent);

            m_BatchesToLoadCounter.Reset();
            m_BatchesToUnloadCounter.Reset();
            
            m_InstanceBatchTypeHandle.Update(ref state);
            m_EntityTypeHandle.Update(ref state);
            m_IgnoreMaxDistanceTagTypeHandle.Update(ref state);

            var prewarmPositions = m_PrewarmPositionsQuery.ToComponentDataArray<ScatteringPrewarmPosition>(Allocator.TempJob);

            //find sections to load
            var dep1 = new FindInstanceBatchesToProcess
            {
                TargetRelativeSize = targetScreenSize,
                ModelMinMaxSizeInterp = math.saturate(modelSizeInterp),
                TanFOV = tanFOV,
                ScatterAreaCenter = targetAreaCenter,
                FlattenHeightDifferenceMultiplier = flattenDistance,
                PrewarmPositions = prewarmPositions,
                Counter = m_BatchesToLoadCounter,
                SectionsToProcess = m_BatchesToLoad,
                EntityTypeHandle = m_EntityTypeHandle,
                InstanceBatchType = m_InstanceBatchTypeHandle,
                ProcessIfSmallerThanTarget = false,
                MaxDistance = maxDistance
            }.ScheduleParallel(m_PendingInstanceBatchesQuery, deps);
            
            //find sections to unload

            var dep2 = new FindInstanceBatchesToProcess
            {
                TargetRelativeSize = targetScreenSize - targetScreenSize * unloadTargetMarginRatio,
                ModelMinMaxSizeInterp = math.saturate(modelSizeInterp),
                TanFOV = tanFOV,
                ScatterAreaCenter = targetAreaCenter,
                FlattenHeightDifferenceMultiplier = flattenDistance,
                PrewarmPositions = prewarmPositions,
                Counter = m_BatchesToUnloadCounter,
                SectionsToProcess = m_BatchesToUnload,
                EntityTypeHandle = m_EntityTypeHandle,
                InstanceBatchType = m_InstanceBatchTypeHandle,
                ProcessIfSmallerThanTarget = true,
                MaxDistance = maxDistance
            }.ScheduleParallel(m_ScatteredInstanceBatchesQuery, deps);
            var handle = JobHandle.CombineDependencies(dep1, dep2);
            prewarmPositions.Dispose(handle);
            return handle;
        }

        void LoadBatches(ref SystemState state, PointCloudSystemConfigData config, NativeSlice<Entity> batchEntitiesToLoad)
        {
            
            if (batchEntitiesToLoad.Length > 0)
            {
                NativeArray<ScatterPointCloudInstanceBatch> instanceBatchComponents = new NativeArray<ScatterPointCloudInstanceBatch>(batchEntitiesToLoad.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < batchEntitiesToLoad.Length; ++i)
                {
                    instanceBatchComponents[i] = state.EntityManager.GetComponentData<ScatterPointCloudInstanceBatch>(batchEntitiesToLoad[i]);
                }

                NativeArray<PendingBatchComparer.BatchSortData> sortedBatches;
                using (var markerSort = s_ProcessBatchesLoadSort.Auto())
                {
                    SortPendingScatterBatches(instanceBatchComponents, out sortedBatches);
                }

                int maxInstancesToSpawn = config.MaxNumberOfSpawnedInstancesPerBatch;
                int numberOfInstances = 0;
                int numberOfBatchesToInstantiate = 0;
                foreach (var batchSortData in sortedBatches)
                {
                    ScatterPointCloudInstanceBatch batch = instanceBatchComponents[batchSortData.batchIndex];
                    var instanceData = state.EntityManager.GetComponentData<ScatterPointCloudInstanceData>(batch.InstanceDataEntity);
                    int numberForInstancesForBatch = instanceData.AttributeRanges.Value.OffsetCount[batch.AttributeRangeIndex].y;

                    //break if the next batch would go over our target (could potentially traverse further to see if we can find an entry that fits)
                    if (numberOfInstances + numberForInstancesForBatch > maxInstancesToSpawn && numberOfBatchesToInstantiate > 0 && !config.ImmediateBlockingScatter)
                    {
                        break;
                    }
                    
                    ++numberOfBatchesToInstantiate;
                    numberOfInstances += numberForInstancesForBatch;
                }
                
                var payloadEntities = m_StagingWorld.EntityManager.CreateEntity(m_StagingWorldPayloadArchetype,
                    numberOfBatchesToInstantiate, Allocator.Temp);

                for (int i = 0; i < payloadEntities.Length; ++i)
                {
                    ScatterPointCloudInstanceBatch batch = instanceBatchComponents[sortedBatches[i].batchIndex];
                    ScatterPointCloudCleanup instData =
                        state.EntityManager.GetComponentData<ScatterPointCloudCleanup>(batch
                            .InstanceDataEntity); //use references to cleanup blob rather than the real instance data since the instancedata can get rebaked/unloaded while scattering in editor mode
                    ScatterPointCloudPrefab prefabRef = EntityManager.GetComponentData<ScatterPointCloudPrefab>(batch.InstanceDataEntity);
                    int extraDataMask = EntityManager.GetComponentData<ScatterPointCloudInstanceData>(batch.InstanceDataEntity).ExtraDataMask;
                    int2 offsetCount = instData.AttributeRanges.Value.OffsetCount[batch.AttributeRangeIndex];
                    int3 tileImportanceData = instData.ImportanceData.IsCreated ? instData.ImportanceData.Value.PerTileDensityMinMaxAverage[batch.AttributeRangeIndex] : new int3(0, 0, 0);
                    
                    Entity prefab = GetPrefabStagingForPointCloud(prefabRef.PrefabRef, extraDataMask);

                    m_StagingWorld.EntityManager.SetComponentData(payloadEntities[i],
                        new ScatterPointCloudStagingSystem.InstantiatePayload
                        {
                            Attributes = instData.Attributes,
                            ImportanceData = instData.ImportanceData,
                            TileDensityMinMaxAverage = tileImportanceData,
                            Hash = instData.ScatterId,
                            GroupHash = instData.ScatterGroupId,
                            Prefab = prefab,
                            RuntimeId = batch.RuntimeId,
                            InstanceDataOffsetCount = offsetCount,
                            InstanceSizeRange = batch.MinMaxModelSize
                        });

                    m_StagingWorld.EntityManager.SetSharedComponent(payloadEntities[i],
                        new ScatterPointCloudScatterId
                        {
                            ScatterId = instData.ScatterId
                        });
                    
                    m_StagingWorld.EntityManager.SetSharedComponent(payloadEntities[i],
                        new ScatterPointCloudScatterGroupId()
                        {
                            Value = instData.ScatterGroupId
                        });
                }
                
                //destroy pending batches that have been now submitted to staging for processing
                for (int i = 0; i < numberOfBatchesToInstantiate; ++i)
                {
                    var batchEntityScattered = batchEntitiesToLoad[sortedBatches[i].batchIndex];
                    state.EntityManager.AddComponent<ScatterPointCloudInstanceBatchScatteringTag>(batchEntityScattered);
                    state.EntityManager.RemoveComponent<ScatterPointCloudInstanceBatchPendingTag>(batchEntityScattered);
                }
                
                payloadEntities.Dispose();
                sortedBatches.Dispose();
                instanceBatchComponents.Dispose();
            }
        }

        void UnloadBatches(ref SystemState state, PointCloudSystemConfigData config, NativeSlice<Entity> batchesToUnload)
        {
            if (batchesToUnload.Length > 0)
            {
                //destroy pending batches that have been now submitted to staging for processing
                for (int i = 0; i < batchesToUnload.Length; ++i)
                {
                    var batchEntityScattered = batchesToUnload[i];
                    ScatterPointCloudInstanceBatch batchData = state.EntityManager.GetComponentData<ScatterPointCloudInstanceBatch>(batchEntityScattered);
                    
                    m_AllScatteredInstancesQuery.SetSharedComponentFilter(new ScatteredInstanceBatch(){RuntimeBatchId = batchData.RuntimeId});
                    EntityManager.DestroyEntity(m_AllScatteredInstancesQuery);
                    
                    state.EntityManager.AddComponent<ScatterPointCloudInstanceBatchPendingTag>(batchEntityScattered);
                    state.EntityManager.RemoveComponent<ScatterPointCloudInstanceBatchScatteredTag>(batchEntityScattered);
                }
            }
        }

        void MoveInstancesFromStaging(ref SystemState state)
        {
            if (!m_ScatteredInstancesStagingQuery.IsEmpty)
            {
                EntityManager.MoveEntitiesFrom(m_StagingWorld.EntityManager, m_ScatteredInstancesStagingQuery);
                EntityManager.AddComponent<ScatterPointCloudInstanceBatchScatteredTag>(m_ScatteringInstanceBatchesQuery);
                EntityManager.RemoveComponent<ScatterPointCloudInstanceBatchScatteringTag>(m_ScatteringInstanceBatchesQuery);
            }
        }

        void SortPendingScatterBatches(NativeArray<ScatterPointCloudInstanceBatch> ranges, out NativeArray<PendingBatchComparer.BatchSortData> sortedBatches)
        {
            var cts = World.GetExistingSystem<CameraTrackingSystem>();
            //no camera tracking, just fill unsorted
            if (cts == SystemHandle.Null)
            {
                sortedBatches = new NativeArray<PendingBatchComparer.BatchSortData>(0, Allocator.Temp);
                Debug.LogError("No Camera tracking system found! Can't sort scatter batches");
                return;
            }

            var cData = EntityManager.GetComponentData<CameraTrackingSystem.CameraTrackingData>(cts);
            sortedBatches = new NativeArray<PendingBatchComparer.BatchSortData>(ranges.Length, Allocator.Temp);
            for (int i = 0; i < ranges.Length; ++i)
            {
                float sortValue = math.length(cData.CameraPosition - ranges[i].BatchBounds.Center);
                var frustrumIntersectionRes = FrustumPlanes.Intersect(cData.CameraFrustrumPlanes, ranges[i].BatchBounds);
                if (frustrumIntersectionRes == FrustumPlanes.IntersectResult.Out)
                {
                    sortValue *= 100.0f; //for now just flat multiplier
                }
                sortedBatches[i] = new PendingBatchComparer.BatchSortData() { batchIndex = i, sortValue = sortValue };
            }
            
            PendingBatchComparer pendingBatch = new PendingBatchComparer();
            sortedBatches.Sort(pendingBatch);
        }
        


        
        void FreeScatteringAttributes(BlobAssetReference<ScatterPointCloudInstanceAttributes> attributes, BlobAssetReference<ScatterPointCloudAttributeRange> attributeRanges)
        {
//#if UNITY_EDITOR
            attributes.Dispose();
            attributeRanges.Dispose();
//#endif
        }
        

        void Cleanup(ref SystemState state,Entity ent,  ScatterPointCloudCleanup cleanupComp)
        {
            ReleasePrefab(ref state, cleanupComp.PrefabRef);
            FreeScatteringAttributes(cleanupComp.Attributes, cleanupComp.AttributeRanges);
            Hash128 hashToDelete = cleanupComp.ScatterId;
            
            state.EntityManager.RemoveComponent<ScatterPointCloudCleanup>(ent);
            //cleanup main world
            m_AllEntitiesWithScatterIdQuery.ResetFilter();
            m_AllEntitiesWithScatterIdQuery.AddSharedComponentFilter(new ScatterPointCloudScatterId()
                { ScatterId = hashToDelete });
            state.EntityManager.DestroyEntity(m_AllEntitiesWithScatterIdQuery);
            
            //cleanup staging
            m_AllEntitiesWithScatterIdStagingQuery.ResetFilter();
            m_AllEntitiesWithScatterIdStagingQuery.AddSharedComponentFilter(new ScatterPointCloudScatterId()
                { ScatterId = hashToDelete });
            m_StagingWorld.EntityManager.DestroyEntity(m_AllEntitiesWithScatterIdStagingQuery);
            
        }
        
        void CleanRemovedEntities(ref SystemState state)
        {
            using var marker = s_RemoveDeletedEntitiesMarker.Auto();
            NativeArray<Entity> entitiesToDelete = m_DestroyedPayloadsQuery.ToEntityArray(Allocator.Temp);
            if (!entitiesToDelete.IsCreated || entitiesToDelete.Length == 0) return;

            foreach (var entity in entitiesToDelete)
            {
                ScatterPointCloudCleanup cleanupComponent =
                    state.EntityManager.GetComponentData<ScatterPointCloudCleanup>(entity);
                Cleanup(ref state, entity, cleanupComponent);
            }
            
            entitiesToDelete.Dispose();
        }
        
        static BlobAssetReference<T> DeepCopy<T>(BlobAssetReference<T> input) where T : unmanaged
        {
//#if UNITY_EDITOR
            if (!input.IsCreated) return input;
            unsafe
            {
                MemoryBinaryWriter writer = new MemoryBinaryWriter();
                writer.Write(input);
                BinaryReader reader = new MemoryBinaryReader(writer.Data, writer.Length);
               return reader.Read<T>();
            }
/*#else
                return input;
#endif*/
        }
#if UNITY_EDITOR
        //incremental baking might change the actual data in components that are baked but leave the cleanup component intact. Check for this case: cleanup the component and force rescatter
        void HandleIncrementalBakingChanges(ref SystemState state)
        {

            NativeArray<Entity> entityArray = m_PotentiallyOutOfSyncPayloadsQuery.ToEntityArray(Allocator.Temp);
            if (!entityArray.IsCreated || entityArray.Length == 0) return;

            foreach (var entity in entityArray)
            {
                ScatterPointCloudCleanup cleanupComponent =
                    state.EntityManager.GetComponentData<ScatterPointCloudCleanup>(entity);
                
                ScatterPointCloudScatterId scatterId =
                    state.EntityManager.GetSharedComponent<ScatterPointCloudScatterId>(entity);

                //mismatch between cleanup and current scatterId: incremental baking has changed the data, needs to rescatter
                if (cleanupComponent.ScatterId != scatterId.ScatterId)
                {
                    Cleanup(ref state, entity, cleanupComponent);
                    state.EntityManager.AddComponent<ScatteredPointCloudNeedsScatterTag>(entity);
                    state.EntityManager.RemoveComponent<ScatteredPointCloudPrefabLoadPendingTag>(entity);
                    state.EntityManager.RemoveComponent<ScatteredPointCloudReadyToScatterTag>(entity);
                }
            }
            

            entityArray.Dispose();
        }
#endif
    }
}