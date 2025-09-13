using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateBefore(typeof(EndInitializationEntityCommandBufferSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [BurstCompile]
    public partial struct TileImpostorSystem : ISystem
    {
        public struct TileImpostorSystemConfigData : IComponentData
        {
            public float ImpostorLOD0Distance;
            public float ImpostorLOD1Distance;
            public float ImpostorCullDistance;
            public float ImpostorFadeDistance;
            public float FlattenHeightDifferenceMultiplier;
        }
        
        public struct TileImpostorDataHash : IComponentData
        {
            public Hash128 Hash;
        }

        public struct TileImpostorInfo : IComponentData
        {
            public int numberOfInstancesBaked;
        }
        
        public struct TileImpostorPrefabLODEntry : IBufferElementData
        {
            public Entity Value;
        }
        public struct TileImpostorDataEntry : IBufferElementData
        {
            public Entity Prefab;
            public AABB Bounds;
        }

        public struct TileImpostorDataProcessed : ICleanupComponentData
        {
            public Hash128 Hash;
        }

        public struct TileImpostorSourceHash : ISharedComponentData
        {
            public Hash128 Hash;
        }

        public struct TileImpostorMeshAndMaterialIndices : IComponentData
        {
            public int GetMeshIndex(int index)
            {
                return MeshMatIndices[index * 2];
            }

            public int GetMaterialIndex(int index)
            {
                return MeshMatIndices[index * 2 + 1];
            }
        
            public void SetMeshIndex(int index, int value)
            {
                Debug.Assert(value < 0xFFFF);
                MeshMatIndices[index * 2] = (short)value;
            }

            public void SetMaterialIndex(int index, int value)
            { 
                Debug.Assert(value < 0xFFFF);
                MeshMatIndices[index * 2 + 1] = (short)value;
            }

            public void Fill(short value)
            {
                MeshMatIndices.Fill(value);
            }
        
            public FixedArray32Bytes<short> MeshMatIndices;
        }   
        
        public const string TileImpostorVisibilityDistanceName = "_TileImpostorVisibilityDistance";
        public const string TileImpostorVisibilityDistanceFadeName = "_TileImpostorVisibilityDistanceFade";
        
        private EntityQuery m_ImpostorBatchesNotProcessed;
        private EntityQuery m_ImpostorBatchesAlreadyProcessed;
        private EntityQuery m_ImpostorBatchesRemoved;

        private EntityQuery m_ImpostorEntititesWithMeshAndMaterial;
        private EntityQuery m_AllInstantiatedImpostors;
        
        private ComponentTypeHandle<WorldRenderBounds> m_RenderBoundsType;
        private ComponentTypeHandle<TileImpostorMeshAndMaterialIndices> m_MatMeshIndicesType;
        private ComponentTypeHandle<MaterialMeshInfo> m_MaterialMeshInfo;
        
        public ComponentTypeHandle<TileImpostorDataHash> m_ImpostorDataHashType;
        public EntityTypeHandle m_EntityType;
        public BufferTypeHandle<TileImpostorDataEntry> m_ImpostorDataArrayType;
        public BufferLookup<TileImpostorPrefabLODEntry> m_TileImpostorPrefabLODLookup;
        public ComponentLookup<MaterialMeshInfo> m_MatMeshInfoLookup;

        private float3 m_CameraPosition;

        public void OnCreate(ref SystemState state)
        {
            m_RenderBoundsType = state.EntityManager.GetComponentTypeHandle<WorldRenderBounds>(true);
            m_MatMeshIndicesType = state.EntityManager.GetComponentTypeHandle<TileImpostorMeshAndMaterialIndices>(true);
            m_MaterialMeshInfo = state.EntityManager.GetComponentTypeHandle<MaterialMeshInfo>(false);
            
            m_ImpostorDataHashType = state.EntityManager.GetComponentTypeHandle<TileImpostorDataHash>(true);
            m_EntityType = state.EntityManager.GetEntityTypeHandle();
            m_ImpostorDataArrayType = state.EntityManager.GetBufferTypeHandle<TileImpostorDataEntry>(true);
            m_TileImpostorPrefabLODLookup = SystemAPI.GetBufferLookup<TileImpostorPrefabLODEntry>(true);
            m_MatMeshInfoLookup = SystemAPI.GetComponentLookup<MaterialMeshInfo>(true);

            m_ImpostorBatchesNotProcessed = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TileImpostorDataHash>(), ComponentType.ReadOnly<TileImpostorDataEntry>(), ComponentType.Exclude<TileImpostorDataProcessed>());
            m_ImpostorBatchesAlreadyProcessed = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TileImpostorDataHash>(), ComponentType.ReadOnly<TileImpostorDataProcessed>());
            m_ImpostorBatchesRemoved = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TileImpostorDataProcessed>(), ComponentType.Exclude<TileImpostorDataHash>());
            m_AllInstantiatedImpostors = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TileImpostorSourceHash>());
            m_ImpostorEntititesWithMeshAndMaterial = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TileImpostorSourceHash>(), ComponentType.ReadOnly<WorldRenderBounds>(), ComponentType.ReadOnly<TileImpostorMeshAndMaterialIndices>(), ComponentType.ReadWrite<MaterialMeshInfo>());
            
            state.EntityManager.AddComponentData(state.SystemHandle, new TileImpostorSystemConfigData() 
            {
                ImpostorLOD0Distance = 0.0f,
                ImpostorLOD1Distance = 10000.0f,
                ImpostorCullDistance = 20000.0f,
                ImpostorFadeDistance = 0.0f,
                FlattenHeightDifferenceMultiplier = 1.0f
            });
            m_CameraPosition = float3.zero;
        }

        public void OnDestroy(ref SystemState state)
        {
            m_ImpostorBatchesNotProcessed.Dispose();
            m_ImpostorBatchesAlreadyProcessed.Dispose();
            m_ImpostorBatchesRemoved.Dispose();
            m_AllInstantiatedImpostors.Dispose();
            m_ImpostorEntititesWithMeshAndMaterial.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            TileImpostorSystemConfigData config = state.EntityManager.GetComponentData<TileImpostorSystemConfigData>(state.SystemHandle);
            
            HandleRemovedBatches(ref state);
            HandleChangedBatches(ref state);
            HandleNewBatches(ref state);

            UpdateCameraState(ref state);
            state.Dependency = UpdateTileImpostorVisibility(ref state, config, state.Dependency);
            
            Shader.SetGlobalFloat(TileImpostorVisibilityDistanceName, config.ImpostorLOD0Distance);
            Shader.SetGlobalFloat(TileImpostorVisibilityDistanceFadeName, config.ImpostorFadeDistance);
        }
        
        

        void HandleNewBatches(ref SystemState state)
        {
            m_ImpostorDataHashType.Update(ref state);
            m_EntityType.Update(ref state);
            m_ImpostorDataArrayType.Update(ref state);
            m_TileImpostorPrefabLODLookup.Update(ref state);
            m_MatMeshInfoLookup.Update(ref state);

            if (m_ImpostorBatchesNotProcessed.IsEmpty) return;
            
            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.TempJob, PlaybackPolicy.SinglePlayback);
            var parallelWriter = ecb.AsParallelWriter();
            var job = new ProcessAndInstantiateTileImpostor()
            {
                Ecb = parallelWriter,
                ImpostorDataHashType = m_ImpostorDataHashType,
                EntityType = m_EntityType,
                ImpostorDataArrayType = m_ImpostorDataArrayType,
                TileImpostorPrefabLODLookup = m_TileImpostorPrefabLODLookup,
                MatMeshInfoLookup = m_MatMeshInfoLookup
            }.ScheduleParallel(m_ImpostorBatchesNotProcessed, state.Dependency);
            
            job.Complete();
            
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        void HandleChangedBatches(ref SystemState state)
        {
#if UNITY_EDITOR
            using NativeArray<TileImpostorDataHash> impostorData = m_ImpostorBatchesAlreadyProcessed.ToComponentDataArray<TileImpostorDataHash>(Allocator.Temp);
            using NativeArray<TileImpostorDataProcessed> cleanupComponent = m_ImpostorBatchesAlreadyProcessed.ToComponentDataArray<TileImpostorDataProcessed>(Allocator.Temp);
            using NativeArray<Entity> entityArray = m_ImpostorBatchesAlreadyProcessed.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entityArray.Length; ++i)
            {
                if (impostorData[i].Hash != cleanupComponent[i].Hash)
                {
                    DestroyAllImpostorsFromSource(ref state, cleanupComponent[i].Hash);
                    state.EntityManager.RemoveComponent<TileImpostorDataProcessed>(entityArray[i]);
                }
            }
#endif
        }

        void HandleRemovedBatches(ref SystemState state)
        {
            using NativeArray<TileImpostorDataProcessed> cleanupComponent = m_ImpostorBatchesRemoved.ToComponentDataArray<TileImpostorDataProcessed>(Allocator.Temp);
            using NativeArray<Entity> entityArray = m_ImpostorBatchesRemoved.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < cleanupComponent.Length; ++i)
            {
                DestroyAllImpostorsFromSource(ref state, cleanupComponent[i].Hash);
                state.EntityManager.RemoveComponent<TileImpostorDataProcessed>(entityArray[i]);
            }
        }

        private void DestroyAllImpostorsFromSource(ref SystemState state, Hash128 hash)
        {
            m_AllInstantiatedImpostors.SetSharedComponentFilter(new TileImpostorSourceHash()
            {
                Hash = hash
            });
            state.EntityManager.DestroyEntity(m_AllInstantiatedImpostors);
            m_AllInstantiatedImpostors.ResetFilter();
        }
        
        void UpdateCameraState(ref SystemState state)
        {
            var cts = state.World.GetExistingSystem<CameraTrackingSystem>();
            if (cts != SystemHandle.Null)
            {
                var cData = state.EntityManager.GetComponentData<CameraTrackingSystem.CameraTrackingData>(cts);
                m_CameraPosition = cData.CameraPosition;
            }
        }

        JobHandle UpdateTileImpostorVisibility(ref SystemState state, TileImpostorSystemConfigData config, JobHandle deps)
        {
            m_RenderBoundsType.Update(ref state);
            m_MatMeshIndicesType.Update(ref state);
            m_MaterialMeshInfo.Update(ref state);

            NativeArray<float> lodDistances = new NativeArray<float>(3, Allocator.TempJob);
            lodDistances[0] = config.ImpostorLOD0Distance;
            lodDistances[1] = config.ImpostorLOD1Distance;
            lodDistances[2] = config.ImpostorCullDistance;

            var jobHandle = new ChangeImpostorVisibility()
            {
                WorldRenderBoundsType = m_RenderBoundsType,
                MaterialMeshIndicesType = m_MatMeshIndicesType,
                MaterialMeshInfoType = m_MaterialMeshInfo,
                ComparePosition = m_CameraPosition,
                LODDistances = lodDistances,
                FlattenDistance = config.FlattenHeightDifferenceMultiplier
            }.ScheduleParallel(m_ImpostorEntititesWithMeshAndMaterial, deps);
            lodDistances.Dispose(jobHandle);
            return jobHandle;
        }

        
        [BurstCompile]
        private struct ProcessAndInstantiateTileImpostor : IJobChunk
        {
            public EntityCommandBuffer.ParallelWriter Ecb;
            [ReadOnly] 
            public ComponentTypeHandle<TileImpostorDataHash> ImpostorDataHashType;
            public EntityTypeHandle EntityType;
            public BufferTypeHandle<TileImpostorDataEntry> ImpostorDataArrayType;
            [ReadOnly]
            public BufferLookup<TileImpostorPrefabLODEntry> TileImpostorPrefabLODLookup;
            [ReadOnly]
            public ComponentLookup<MaterialMeshInfo> MatMeshInfoLookup;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var dataHash = chunk.GetNativeArray(ref ImpostorDataHashType);
                var prefabBufferAccessor = chunk.GetBufferAccessor(ref ImpostorDataArrayType);
                var entityArray = chunk.GetNativeArray(EntityType);
                
                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    TileImpostorSourceHash sharedHash = new TileImpostorSourceHash()
                    {
                        Hash = dataHash[i].Hash
                    };

                    TileImpostorMeshAndMaterialIndices meshMaterialIndices = new TileImpostorMeshAndMaterialIndices();
                    meshMaterialIndices.Fill(0);
                    var prefabBuffer = prefabBufferAccessor[i];
                    for (int entryIndex = 0; entryIndex < prefabBuffer.Length; ++entryIndex)
                    {
                        var impostorEntry = prefabBuffer[entryIndex];

                        if (MatMeshInfoLookup.HasComponent(impostorEntry.Prefab))
                        {
                            var matMesh = MatMeshInfoLookup[impostorEntry.Prefab];
                            meshMaterialIndices.SetMeshIndex(0, matMesh.Mesh);
                            meshMaterialIndices.SetMaterialIndex(0, matMesh.Material);
                        }

                        if (TileImpostorPrefabLODLookup.HasBuffer(impostorEntry.Prefab))
                        {
                            var lodArray = TileImpostorPrefabLODLookup[impostorEntry.Prefab];
                            for (int lodIndex = 0; lodIndex < lodArray.Length; ++lodIndex)
                            {
                                var lodEntry = lodArray[lodIndex];
                                if (MatMeshInfoLookup.HasComponent(lodEntry.Value))
                                {
                                    var matMesh = MatMeshInfoLookup[lodEntry.Value];
                                    meshMaterialIndices.SetMeshIndex(lodIndex + 1, matMesh.Mesh);
                                    meshMaterialIndices.SetMaterialIndex(lodIndex + 1, matMesh.Material);
                                }
                            }

                        }

                        Ecb.RemoveComponent<LinkedEntityGroup>(unfilteredChunkIndex, impostorEntry.Prefab);
                        Ecb.RemoveComponent<TileImpostorPrefabLODEntry>(unfilteredChunkIndex, impostorEntry.Prefab);
                        Ecb.RemoveComponent<LocalTransform>(unfilteredChunkIndex, impostorEntry.Prefab);


                        Entity impostorEntity = Ecb.Instantiate(unfilteredChunkIndex, impostorEntry.Prefab);
                        if (impostorEntity != Entity.Null)
                        {
                            Ecb.AddSharedComponent(unfilteredChunkIndex, impostorEntity, sharedHash);
                            LocalToWorld localToWorld = new LocalToWorld()
                            {
                                Value = float4x4.Translate(impostorEntry.Bounds.Center)
                            };
                            Ecb.SetComponent(unfilteredChunkIndex, impostorEntity, localToWorld);
                            Ecb.AddComponent(unfilteredChunkIndex, impostorEntity, meshMaterialIndices);

                        }
                    }
                    
                    Ecb.AddComponent(unfilteredChunkIndex, entityArray[i], new TileImpostorDataProcessed()
                    {
                        Hash = sharedHash.Hash
                    });
                }
            }
        }

        [BurstCompile]
        private struct ChangeImpostorVisibility : IJobChunk
        {
            public float3 ComparePosition;
            public float FlattenDistance;
            [ReadOnly]
            public ComponentTypeHandle<WorldRenderBounds> WorldRenderBoundsType;
            [ReadOnly]
            public ComponentTypeHandle<TileImpostorMeshAndMaterialIndices> MaterialMeshIndicesType;
            [ReadOnly]
            public NativeArray<float> LODDistances;
            public ComponentTypeHandle<MaterialMeshInfo> MaterialMeshInfoType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var worldBoundsArray = chunk.GetNativeArray(ref WorldRenderBoundsType);
                var materialMeshInfoArray = chunk.GetNativeArray(ref MaterialMeshInfoType);
                var materialMeshIndices = chunk.GetNativeArray(ref MaterialMeshIndicesType);

                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    WorldRenderBounds worldBounds = worldBoundsArray[i];
                    MaterialMeshInfo matMeshInfo = materialMeshInfoArray[i];
                    var closestPoint = math.min(math.max(ComparePosition, worldBounds.Value.Center - worldBounds.Value.Extents), worldBounds.Value.Center + worldBounds.Value.Extents);
                    var delta = ComparePosition - closestPoint;
                    delta.y *= FlattenDistance;
                    float distClosestSq = math.lengthsq(delta);
                    float unloadMargin = math.length(worldBounds.Value.Size);
                    bool isVisible = matMeshInfo.Mesh != 0 && matMeshInfo.Material != 0;
                    
                    //have "easing" when turning impostor off: impostor becomes visible when the closest points is at vista distance, but is only removed when the distance is greater than Distance - UnloadMargin
                    float closestAppearDistance = LODDistances[0];
                    float furthestVisibleDistance = LODDistances[^1];
                    
                    var compareDistNear = isVisible ? math.max(closestAppearDistance - unloadMargin, 0) : closestAppearDistance;
                    var compareDistFar = isVisible ? math.max(furthestVisibleDistance + unloadMargin, 0) : furthestVisibleDistance;
                    bool shouldBeVisible = distClosestSq >= compareDistNear * compareDistNear;
                    shouldBeVisible = shouldBeVisible && distClosestSq < compareDistFar * compareDistFar;

                    int lodIndex = 0;
                    if (shouldBeVisible)
                    {
                        for (int lod = 1; lod < LODDistances.Length; ++lod)
                        {
                            float lodDistance = LODDistances[lod];
                            if (distClosestSq < lodDistance * lodDistance) break;
                            ++lodIndex;
                        }
                    }
                    
                    var meshIndex = materialMeshIndices[i].GetMeshIndex(lodIndex);
                    var matIndex = materialMeshIndices[i].GetMaterialIndex(lodIndex);

                    if (shouldBeVisible != isVisible || matMeshInfo.Mesh != meshIndex || matMeshInfo.Material != matIndex)
                    {
                        if (shouldBeVisible)
                        {
                            matMeshInfo.Mesh = meshIndex;
                            matMeshInfo.Material = matIndex;
                        }
                        else
                        {
                            matMeshInfo.Mesh = 0;
                            matMeshInfo.Material = 0;
                        }
                        
                        materialMeshInfoArray[i] = matMeshInfo;
                    }
                }
            }
        }
    }
}