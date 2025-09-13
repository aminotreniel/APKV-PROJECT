using System.Threading;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    [DisableAutoCreation]
    public partial class ScatterPointCloudStagingSystem : SystemBase
    {
        public enum State
        {
            Idle,
            Instantiate,
            SetupNewInstances
        }

        public struct InstantiatePayload : IComponentData
        {
            public BlobAssetReference<ScatterPointCloudInstanceAttributes> Attributes;
            public BlobAssetReference<ScatterPointCloudPointImportanceData> ImportanceData;
            public int2 InstanceDataOffsetCount;
            public int3 TileDensityMinMaxAverage;
            public uint RuntimeId;
            public Entity Prefab;
            public Hash128 Hash;
            public Hash128 GroupHash;
            public float2 InstanceSizeRange;
        }

        public struct CurrentState : IComponentData
        {
            public State Value;
        }

        public struct PrefabComponentOptimizationPending : IComponentData
        {
            public bool AddChunkComponents;
        }

        [BurstCompile]
        private struct InstantiatePrefabsJob : IJob
        {
            public ExclusiveEntityTransaction EntityTransaction;
            public NativeArray<Entity> Prefabs;
            public NativeArray<int> InstanceCounts;
            public NativeArray<Hash128> ScatterIds;
            public NativeArray<Hash128> ScatterIdsNoPartition;
            public NativeArray<uint> RuntimeIds;
            public NativeArray<float2> InstanceSizeRanges;
            public NativeArray<int3> DensityMinMaxAverage;
            public void Execute()
            {
                for (int i = 0; i < Prefabs.Length; ++i)
                {
                    int instanceCount = InstanceCounts[i];
                    Hash128 scatterHash = ScatterIds[i];
                    Hash128 scatterHashNoPartition = ScatterIdsNoPartition[i];
                    Entity prefab = Prefabs[i];
                    uint runtimeId = RuntimeIds[i];
                    float2 sizeRange = InstanceSizeRanges[i];
                    int3 density = DensityMinMaxAverage[i];

                    //setup scatterId before scattering
                    if (EntityTransaction.HasComponent(prefab, ComponentType.ReadOnly<LinkedEntityGroup>()))
                    {
                        using var allGroupEntities = EntityTransaction.EntityManager
                            .GetBuffer<LinkedEntityGroup>(prefab, isReadOnly: true).Reinterpret<Entity>()
                            .ToNativeArray(Allocator.Temp);
                        EntityTransaction.SetSharedComponent(allGroupEntities, new ScatterPointCloudScatterId() { ScatterId = scatterHash });
                        EntityTransaction.SetSharedComponent(allGroupEntities, new ScatterPointCloudScatterGroupId() { Value = scatterHashNoPartition });
                        EntityTransaction.SetSharedComponent(allGroupEntities,
                            new ScatteredInstanceBatch() { RuntimeBatchId = runtimeId });
                        EntityTransaction.SetSharedComponent(allGroupEntities, new ScatterPointCloudTileInfo() { SizeMinMax = sizeRange, DensityMinMaxAverage = density});
                    }
                    else
                    {
                        EntityTransaction.SetSharedComponent(prefab, new ScatterPointCloudScatterId() { ScatterId = scatterHash });
                        EntityTransaction.SetSharedComponent(prefab, new ScatterPointCloudScatterGroupId() { Value = scatterHashNoPartition });
                        EntityTransaction.SetSharedComponent(prefab,
                            new ScatteredInstanceBatch() { RuntimeBatchId = runtimeId });
                        EntityTransaction.SetSharedComponent(prefab, new ScatterPointCloudTileInfo() { SizeMinMax = sizeRange, DensityMinMaxAverage = density});
                    }

                    NativeArray<Entity> newEntities = new NativeArray<Entity>(instanceCount, Allocator.Temp);
                    EntityTransaction.Instantiate(prefab, newEntities);
                    newEntities.Dispose();

                }
            }
        }
        
        [BurstCompile]
        private struct SetupScatteredInstanceDataJob : IJobChunk
        {
            [ReadOnly]
            public NativeList<InstantiatePayload> ScatterData;
            [NativeDisableParallelForRestriction]
            public NativeList<int> ScatterDataOffsets;

            public ComponentTypeHandle<LocalToWorld> LocalToWorldTypeRW;
            public ComponentTypeHandle<ScatteredInstanceExtraData> InstanceExtraDataTypeRW;
            public ComponentTypeHandle<ScatteredInstanceImportanceData> InstanceImportanceDataTypeRW;
            
            [ReadOnly] 
            public SharedComponentTypeHandle<ScatteredInstanceBatch> ScatteredInstanceBatchTypeRO;
            [ReadOnly]
            public NativeHashMap<uint, int>.ReadOnly RuntimeIdToBatchIndexMapping;


            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                uint runtimeId = chunk.GetSharedComponent(ScatteredInstanceBatchTypeRO).RuntimeBatchId;
                int batchIndex = RuntimeIdToBatchIndexMapping[runtimeId];
                InstantiatePayload instanceData = ScatterData[batchIndex];
                BlobAssetReference<ScatterPointCloudInstanceAttributes> scatterDataAttr = instanceData.Attributes;
                //its possible that our BlobAssetReference has become stale (baking system destroyes the data, but jobs are still queued). These entities should get destroyed once the ScatterPointCloudSystem removes dead entities next time so just early out
                if (!scatterDataAttr.IsCreated) return;
                
                var transforms = chunk.GetNativeArray(ref LocalToWorldTypeRW);
                
                int entityCount = chunk.Count;

                int instanceOffset = Interlocked.Add(ref ScatterDataOffsets.ElementAt(batchIndex), entityCount) -
                                     entityCount;
                bool hasExtraData = chunk.Has<ScatteredInstanceExtraData>();

                bool importanceDataGenerated = instanceData.ImportanceData.IsCreated;
                bool chunkHasImportanceData = chunk.Has<ScatteredInstanceImportanceData>();
                
                for (int i = 0; i < entityCount; ++i)
                {
                    int offset = instanceOffset + i;
                    float3 pos = scatterDataAttr.Value.Positions[offset];
                    quaternion orientation = scatterDataAttr.Value.Orientations[offset];
                    float scale = scatterDataAttr.Value.Scales[offset];

                    transforms[i] = new LocalToWorld()
                        { Value = math.mul(float4x4.TRS(pos, orientation, scale), transforms[i].Value) };
                }
                
                if (hasExtraData)
                {
                    NativeArray<ScatteredInstanceExtraData> extraDataArray = chunk.GetNativeArray(ref InstanceExtraDataTypeRW);
                    for (int i = 0; i < entityCount; ++i)
                    {
                        int offset = instanceOffset + i;
                        int extraDataIndex = scatterDataAttr.Value.ExtraDataIndex[offset];

                        extraDataArray[i] = new ScatteredInstanceExtraData()
                            { ExtraDataHash = instanceData.Hash, InstanceIndex = extraDataIndex };
                    }
                }

                if (importanceDataGenerated && chunkHasImportanceData)
                {
                    NativeArray<ScatteredInstanceImportanceData> importanceDataArray = chunk.GetNativeArray(ref InstanceImportanceDataTypeRW);
                    for (int i = 0; i < entityCount; ++i)
                    {
                        int offset = instanceOffset + i;
                        float density = instanceData.ImportanceData.Value.PerPointImportanceData[offset];
                        importanceDataArray[i] = new ScatteredInstanceImportanceData()
                        {
                            RelativeDensity = density
                        };
                    }
                }
            }
        }
        
        [BurstCompile]
        internal struct ReplicateParentTransformsToChildrenJob : IJobChunk
        {
            [NativeDisableContainerSafetyRestriction]
            [NativeDisableParallelForRestriction]
            public ComponentTypeHandle<LocalToWorld> LocalToWorldTypeRW;
            [ReadOnly]
            public ComponentTypeHandle<ScatteredInstanceParent> ParentTypeRO;
            [ReadOnly][NativeDisableParallelForRestriction][NativeDisableContainerSafetyRestriction]
            public ComponentLookup<LocalToWorld> LocalToWorldLookupRO;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var transforms = chunk.GetNativeArray(ref LocalToWorldTypeRW);
                var parent = chunk.GetNativeArray(ref ParentTypeRO);
                for (int i = 0; i < chunk.Count; ++i)
                {
                    var parentTransform = LocalToWorldLookupRO[parent[i].Value].Value;
                    transforms[i] = new LocalToWorld() { Value = math.mul(parentTransform, transforms[i].Value) };
                }
            }
        }
        
        [BurstCompile]
        internal struct InitializePartitionDataJob : IJobChunk
        {
            [NativeDisableContainerSafetyRestriction]
            [NativeDisableParallelForRestriction]
            public ComponentTypeHandle<ScatteredInstancePartitioningData> ScatteredInstancePartitioningTypeRW;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var partitioningData = chunk.GetNativeArray(ref ScatteredInstancePartitioningTypeRW);
                for (int i = 0; i < chunk.Count; ++i)
                {
                    partitioningData[i] = new ScatteredInstancePartitioningData()
                    {
                        FlatTileIndex = -1,
                        IndexInTile = -1
                    };
                }
            }
        }
        
        private EntityQuery m_NewScatteredInstances;
        private EntityQuery m_NewScatteredInstanceChildren;
        private EntityQuery m_NewScatteredInstancesWithPartitioningDataRequest;
        
        private EntityQuery m_WaitingTasks;

        private ComponentTypeHandle<LocalToWorld> m_LocalToWorldTypeRW;
        private ComponentTypeHandle<ScatteredInstanceExtraData> m_InstanceExtraDataRW;
        private ComponentTypeHandle<ScatteredInstanceImportanceData> m_InstanceImportanceDataRW;
        private ComponentTypeHandle<ScatteredInstancePartitioningData> m_PartitioningDataTypeRW;
        
        private ComponentTypeHandle<ScatteredInstanceParent> m_ParentTypeRO;
        private SharedComponentTypeHandle<ScatteredInstanceBatch> m_ScatteredInstanceBatchTypeRO;
        private ComponentLookup<LocalToWorld> m_LocalToWorldLookupR0;

        private NativeList<InstantiatePayload> m_CurrentPayloads;

        private NativeHashMap<uint, int> m_RuntimeIDToInstantiateBatchIndex;

        private JobHandle m_LastScatterBatchJobHandle;
        private State m_State;

        private int framesWaitedPerStage;
        private const int MAX_FRAMES_TO_WAIT = 4;

        protected override void OnCreate()
        {
            var state = CheckedStateRef;
            state.EntityManager.AddComponent<CurrentState>(state.SystemHandle);
            m_CurrentPayloads = new NativeList<InstantiatePayload>(64, Allocator.Persistent);
            
            m_NewScatteredInstances = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatteredInstanceBatch>(),
                ComponentType.ReadOnly<ScatteredInstanceRootTag>());

            m_NewScatteredInstancesWithPartitioningDataRequest = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatteredInstanceBatch>(),
                ComponentType.ReadOnly<ScatteredInstanceRootTag>(),
                ComponentType.ReadOnly<ScatteredInstanceNeedsPartitioningTag>()
            );
            

            
            m_NewScatteredInstanceChildren = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<LocalToWorld>(),
                ComponentType.ReadOnly<ScatteredInstanceParent>());
            
            m_WaitingTasks = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<InstantiatePayload>());
            
            m_LocalToWorldTypeRW = state.GetComponentTypeHandle<LocalToWorld>();
            m_InstanceExtraDataRW = state.GetComponentTypeHandle<ScatteredInstanceExtraData>();
            m_InstanceImportanceDataRW = state.GetComponentTypeHandle<ScatteredInstanceImportanceData>();
            m_ScatteredInstanceBatchTypeRO = state.GetSharedComponentTypeHandle<ScatteredInstanceBatch>();
            m_LocalToWorldLookupR0 = state.GetComponentLookup<LocalToWorld>(true);
            m_ParentTypeRO = state.GetComponentTypeHandle<ScatteredInstanceParent>(true);
            m_PartitioningDataTypeRW = state.GetComponentTypeHandle<ScatteredInstancePartitioningData>();

            m_RuntimeIDToInstantiateBatchIndex = new NativeHashMap<uint, int>(64, Allocator.Persistent);
            
            framesWaitedPerStage = 0;
            m_State = State.Idle;

            OnCreate_EntityGraphicsOptimizations();
        }
        
        protected override void OnDestroy()
        {
            var state = CheckedStateRef;
            state.EntityManager.RemoveComponent<CurrentState>(state.SystemHandle);
            
            m_NewScatteredInstances.Dispose();
            m_NewScatteredInstanceChildren.Dispose();
            m_WaitingTasks.Dispose();
            m_NewScatteredInstancesWithPartitioningDataRequest.Dispose();

            if (m_CurrentPayloads.IsCreated)
            {
                m_CurrentPayloads.Dispose();
            }

            m_RuntimeIDToInstantiateBatchIndex.Dispose();
            
            OnDestroy_EntityGraphicsOptimizations();
        }

        protected override void OnUpdate()
        {
            var state = CheckedStateRef;
            //staging world has been locked and no instantiate running. Likely a bug in the code. 
            if (m_State == State.Idle && !state.EntityManager.CanBeginExclusiveEntityTransaction())
            {
                state.EntityManager.ExclusiveEntityTransactionDependency.Complete();
                state.EntityManager.EndExclusiveEntityTransaction();
                Debug.LogError("No instantiate was running and staging scene was locked!");
            }

            if (m_State == State.Idle)
            {
                m_CurrentPayloads.Clear();

                HandlePrefabOptimizations();
                
                var instantiateEntities = m_WaitingTasks.ToEntityArray(Allocator.Temp);
                
                m_RuntimeIDToInstantiateBatchIndex.Clear();
                
                int batchId = 0;
                for (int i = 0; i < instantiateEntities.Length; ++i)
                {
                    var payload = state.EntityManager.GetComponentData<InstantiatePayload>(instantiateEntities[i]);
                    if (payload.Prefab == Entity.Null || payload.InstanceDataOffsetCount.y <= 0)
                    {
                        state.EntityManager.DestroyEntity(instantiateEntities[i]);
                        continue;
                    }

                    m_RuntimeIDToInstantiateBatchIndex[payload.RuntimeId] = batchId++;
                    
                    m_CurrentPayloads.Add(payload);
                }

                instantiateEntities.Dispose();

                if (m_CurrentPayloads.Length > 0)
                {
                    SetState(ref state, State.Instantiate);
                    InstantiateNewScatteringInstances(ref state, state.Dependency, m_CurrentPayloads);
                }
            }

            if (m_State == State.Instantiate)
            {
                if (!state.EntityManager.ExclusiveEntityTransactionDependency.IsCompleted)
                {
                    if (framesWaitedPerStage >= MAX_FRAMES_TO_WAIT)
                    {
                        state.EntityManager.ExclusiveEntityTransactionDependency.Complete();
                        
                    }
                    else
                    {
                        ++framesWaitedPerStage;
                        return;
                    }
                
                    
                }
                state.EntityManager.EndExclusiveEntityTransaction();

                SetState(ref state, State.SetupNewInstances);
                Dependency = SetupNewInstantiatedEntities(ref state, Dependency, m_CurrentPayloads);
                m_LastScatterBatchJobHandle = Dependency;
                framesWaitedPerStage = 0;
            }

            if (m_State == State.SetupNewInstances)
            {
                if (!m_LastScatterBatchJobHandle.IsCompleted)
                {
                    if (framesWaitedPerStage >= MAX_FRAMES_TO_WAIT)
                    {
                        m_LastScatterBatchJobHandle.Complete();
                    }
                    else
                    {
                        ++framesWaitedPerStage;
                        return;
                    }
                }
                state.EntityManager.DestroyEntity(m_WaitingTasks);

                framesWaitedPerStage = 0;
                SetState(ref state, State.Idle);
                
            }
            
        }

        private void SetState(ref SystemState systemState, State state)
        {
            m_State = state;
            systemState.EntityManager.SetComponentData(systemState.SystemHandle, new CurrentState() {Value = m_State});
        }
        
        void InstantiateNewScatteringInstances(ref SystemState state, JobHandle handle, NativeList<InstantiatePayload> payloadsToScatter)
        {
            
            //instantiate prefabs
            {
                //instantiate job might last for a long time (over 4 frames) which causes memory leak errors. Use persistent instead
                NativeArray<Entity> prefabList = new NativeArray<Entity>(payloadsToScatter.Length, Allocator.Persistent);
                NativeArray<int> instanceCounts = new NativeArray<int>(payloadsToScatter.Length, Allocator.Persistent);
                NativeArray<Hash128> scatterIds = new NativeArray<Hash128>(payloadsToScatter.Length, Allocator.Persistent);
                NativeArray<Hash128> scatterIdsNoPartition = new NativeArray<Hash128>(payloadsToScatter.Length, Allocator.Persistent);
                NativeArray<uint> runtimeBatchIds = new NativeArray<uint>(payloadsToScatter.Length, Allocator.Persistent);
                NativeArray<float2> instanceSizeRanges = new NativeArray<float2>(payloadsToScatter.Length, Allocator.Persistent);
                NativeArray<int3> tileDensity = new NativeArray<int3>(payloadsToScatter.Length, Allocator.Persistent);

                for (int i = 0; i < payloadsToScatter.Length; ++i)
                {
                    var prefab = payloadsToScatter[i].Prefab;
                    prefabList[i] = prefab;
                    Debug.Assert(prefabList[i] != Entity.Null);
                    instanceCounts[i] = payloadsToScatter[i].InstanceDataOffsetCount.y;
                    scatterIds[i] = payloadsToScatter[i].Hash;
                    scatterIdsNoPartition[i] = payloadsToScatter[i].GroupHash;
                    runtimeBatchIds[i] = payloadsToScatter[i].RuntimeId;
                    instanceSizeRanges[i] = payloadsToScatter[i].InstanceSizeRange;
                    tileDensity[i] = payloadsToScatter[i].TileDensityMinMaxAverage;
                }

                var scatterEntMngr = state.EntityManager;
                
                var transaction = scatterEntMngr.BeginExclusiveEntityTransaction();
                
                handle = new InstantiatePrefabsJob()
                {
                    EntityTransaction = transaction,
                    InstanceCounts = instanceCounts,
                    Prefabs = prefabList,
                    ScatterIds = scatterIds,
                    ScatterIdsNoPartition = scatterIdsNoPartition,
                    RuntimeIds = runtimeBatchIds,
                    InstanceSizeRanges = instanceSizeRanges,
                    DensityMinMaxAverage = tileDensity
                }.Schedule(handle);

                prefabList.Dispose(handle);
                instanceCounts.Dispose(handle);
                scatterIds.Dispose(handle);
                scatterIdsNoPartition.Dispose(handle);
                runtimeBatchIds.Dispose(handle);
                instanceSizeRanges.Dispose(handle);
                tileDensity.Dispose(handle);

                scatterEntMngr.ExclusiveEntityTransactionDependency = handle;
            }
        }
        
        JobHandle SetupNewInstantiatedEntities(ref SystemState state, JobHandle deps, NativeList<InstantiatePayload> payloadsToScatter)
        {
            state.EntityManager.AddComponent<ScatteredInstancePartitioningData>(m_NewScatteredInstancesWithPartitioningDataRequest);
            //setup data from point cloud per instance
            {
                var scatterPayloadsList = new NativeList<InstantiatePayload>(64, Allocator.TempJob);
                var instanceOffsetsList = new NativeList<int>(64, Allocator.TempJob);
                for (int i = 0; i < payloadsToScatter.Length; ++i)
                {
                    scatterPayloadsList.Add(payloadsToScatter[i]);
                    instanceOffsetsList.Add(payloadsToScatter[i].InstanceDataOffsetCount.x);
                }

                m_LocalToWorldTypeRW.Update(ref state);
                m_InstanceExtraDataRW.Update(ref state);
                m_InstanceImportanceDataRW.Update(ref state);
                m_ScatteredInstanceBatchTypeRO.Update(ref state);
                m_LocalToWorldLookupR0.Update(ref state);
                m_ParentTypeRO.Update(ref state);
                m_PartitioningDataTypeRW.Update(ref state);
                
                deps = new InitializePartitionDataJob()
                {
                    ScatteredInstancePartitioningTypeRW = m_PartitioningDataTypeRW
                }.ScheduleParallel(m_NewScatteredInstancesWithPartitioningDataRequest, deps);
                
                deps = new SetupScatteredInstanceDataJob()
                {
                    InstanceExtraDataTypeRW = m_InstanceExtraDataRW,
                    LocalToWorldTypeRW = m_LocalToWorldTypeRW,
                    ScatteredInstanceBatchTypeRO = m_ScatteredInstanceBatchTypeRO,
                    InstanceImportanceDataTypeRW = m_InstanceImportanceDataRW,
                    ScatterData = scatterPayloadsList,
                    ScatterDataOffsets = instanceOffsetsList,
                    RuntimeIdToBatchIndexMapping = m_RuntimeIDToInstantiateBatchIndex.AsReadOnly()
                }.ScheduleParallel(m_NewScatteredInstances, deps);

                scatterPayloadsList.Dispose(deps);
                instanceOffsetsList.Dispose(deps);
            }
            
            //some of the prefabs might have had children (lods etc). Copy transforms
            {
                deps = new ReplicateParentTransformsToChildrenJob()
                {
                    LocalToWorldTypeRW = m_LocalToWorldTypeRW,
                    LocalToWorldLookupRO = m_LocalToWorldLookupR0,
                    ParentTypeRO = m_ParentTypeRO
                }.ScheduleParallel(m_NewScatteredInstanceChildren, deps);

                
            }
            //bunch of optimization jobs needed for graphics entities
            {
                deps = ScheduleOptimizationJobsForGraphics(deps);
            }

            return deps;
        }
    }
}
