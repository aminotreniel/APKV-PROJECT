using System.Collections.Generic;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEngine;
using FrustumPlanes = Unity.Rendering.FrustumPlanes;

namespace TimeGhost
{
    public struct ScatterSceneToLoad : IComponentData
    {
        public EntitySceneReference sceneReference;
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateBefore(typeof(EndInitializationEntityCommandBufferSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [BurstCompile]
    public partial struct ScatterSceneLoaderSystem : ISystem
    {
        
        public struct ScatterSceneLoaderSystemConfigData : IComponentData
        {
            public bool BlockOnLoad;
            public int MaxInstancesToQueue;
            public float AreaUnloadMargin;
            public float FlattenHeightDifferenceMultiplier;
        }
        
        
        public struct ScatterSceneLoaded : ICleanupComponentData
        {
            public Entity SceneEntity;
        }
        
        public struct ScatterSceneSectionMetaData : IComponentData
        {
            public AABB Bounds;
            public int NumberOfInstances;
            public float ActiveAreaMin;
            public float ActiveAreaMax;
        }
    
        struct SceneMetaDataLoadPendingTag : IComponentData
        {

        }
        public struct ScatterSceneSectionPendingTag : IComponentData
        {
        }
    
        public struct ScatterSceneSectionLoadingTag : IComponentData
        {
        }
    
        public struct ScatterSceneSectionLoadedTag : IComponentData
        {
        }
        
        public struct PendingSectionComparer : IComparer<PendingSectionComparer.SortData>
        {
            public struct SortData
            {
                public float sortValue;
                public int index;
            }
            
            public float3 center;

            // Compares by Length, Height, and Width.
            public int Compare(SortData a,
                SortData b)
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
        private struct FindSectionsToProcessJob : IJobChunk
        {
            public float3 AreaCenter;
            public float ExtraAreaMargin;
            public float FlattenHeightDifferenceMultiplier;
            [ReadOnly]
            public NativeArray<ScatteringPrewarmPosition> PrewarmPositions;
            [ReadOnly]
            public ComponentTypeHandle<ScatterSceneSectionMetaData> SectionMetaDataType;
            [ReadOnly]
            public EntityTypeHandle EntityTypeHandle;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<Entity> SectionsToProcess;
            [NativeDisableUnsafePtrRestriction]
            public UnsafeAtomicCounter32 Counter;
            public bool FindSectionsOutside;

            float GetDistanceSqr(AABB bounds)
            {
                float distSq;
                {
                    var closestPoint = math.clamp(AreaCenter, bounds.Min, bounds.Max);
                    var delta = closestPoint - AreaCenter;
                    delta.y *= FlattenHeightDifferenceMultiplier;
                    distSq = math.max(math.lengthsq(delta), 0.000001f);
                }
                

                if (PrewarmPositions.IsCreated)
                {
                    for (int i = 0; i < PrewarmPositions.Length; ++i)
                    {
                        var p = PrewarmPositions[i].Value;
                        var closestPoint = math.clamp(p, bounds.Min, bounds.Max);
                        var delta = closestPoint - p;
                        delta.y *= FlattenHeightDifferenceMultiplier;
                        var distPrewarm = math.max(math.lengthsq(delta), 0.000001f);
                        distSq = math.min(distPrewarm, distSq);
                    }
                }

                return distSq;
            }
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var metaDatas = chunk.GetNativeArray(ref SectionMetaDataType);
                var entitites = chunk.GetNativeArray(EntityTypeHandle);

                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    var metaData = metaDatas[i];
                    var bounds = metaData.Bounds;
                    var distSqr = GetDistanceSqr(bounds);
                    float distanceThreshold1 = math.max(metaData.ActiveAreaMin - ExtraAreaMargin, 0);
                    float distanceThreshold2 = metaData.ActiveAreaMax + ExtraAreaMargin;
                    bool isInside = (distSqr < distanceThreshold2 * distanceThreshold2) && (distSqr >= distanceThreshold1 * distanceThreshold1);

                    bool needProcessing = FindSectionsOutside ? !isInside : isInside;
                    
                    if (needProcessing)
                    {
                        var ind = Counter.Add(1);
                        SectionsToProcess[ind] = entitites[i];
                    }
                    
                }
            }
        }
        

        private EntityQuery m_ScatterScenesToLoadQuery;
        private EntityQuery m_MetaDataLoadPendingQuery;
        private EntityQuery m_ScenesRemovedQuery;
        
        private EntityQuery m_ScatterSectionPendingQuery;
        private EntityQuery m_ScatterSectionLoadingQuery;
        private EntityQuery m_ScatterSectionLoadedQuery;
        
        private EntityQuery m_PrewarmPositionsQuery;

        private ComponentTypeHandle<ScatterSceneSectionMetaData> m_SectionMetaDataTypeHandle;
        private EntityTypeHandle m_EntityTypeHandle;
        
        private UnsafeAtomicCounter32 m_SectionsToLoadCounter;
        private UnsafeAtomicCounter32 m_SectionsToUnloadCounter;

        private NativeArray<Entity> m_SectionsToLoad;
        private NativeArray<Entity> m_SectionsToUnload;

        private int m_NumberOfSections;
        
        public void OnCreate(ref SystemState state)
        {
            m_ScatterScenesToLoadQuery = state.EntityManager.CreateEntityQuery(typeof(ScatterSceneToLoad), ComponentType.Exclude<ScatterSceneLoaded>());
            m_MetaDataLoadPendingQuery = state.EntityManager.CreateEntityQuery(typeof(SceneMetaDataLoadPendingTag));
            m_ScenesRemovedQuery = state.EntityManager.CreateEntityQuery(typeof(ScatterSceneLoaded), ComponentType.Exclude<ScatterSceneToLoad>());
            
            m_ScatterSectionPendingQuery = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ScatterSceneSectionMetaData>(), ComponentType.ReadOnly<ScatterSceneSectionPendingTag>(), ComponentType.Exclude<ScatterSceneSectionLoadingTag>(), ComponentType.Exclude<ScatterSceneSectionLoadedTag>());
            m_ScatterSectionLoadingQuery = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ScatterSceneSectionMetaData>(), ComponentType.ReadOnly<ScatterSceneSectionLoadingTag>(), ComponentType.Exclude<ScatterSceneSectionPendingTag>(), ComponentType.Exclude<ScatterSceneSectionLoadedTag>());
            m_ScatterSectionLoadedQuery = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ScatterSceneSectionMetaData>(), ComponentType.ReadOnly<ScatterSceneSectionLoadedTag>(), ComponentType.Exclude<ScatterSceneSectionPendingTag>(), ComponentType.Exclude<ScatterSceneSectionLoadingTag>());
            
            m_PrewarmPositionsQuery = state.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ScatteringPrewarmPosition>());
            
            m_SectionMetaDataTypeHandle = state.EntityManager.GetComponentTypeHandle<ScatterSceneSectionMetaData>(true);
            m_EntityTypeHandle = state.EntityManager.GetEntityTypeHandle();

            state.EntityManager.AddComponentData(state.SystemHandle, new ScatterSceneLoaderSystemConfigData() {
                BlockOnLoad = false, 
                MaxInstancesToQueue = (int)1e6,
                AreaUnloadMargin = 5,
                FlattenHeightDifferenceMultiplier = 1
            });
            unsafe
            {
                m_SectionsToLoadCounter = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
                m_SectionsToUnloadCounter = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
            }
            m_SectionsToLoadCounter.Reset();
            m_SectionsToUnloadCounter.Reset();

            m_NumberOfSections = 0;
        }

        public void OnDestroy(ref SystemState state)
        {
            m_ScatterScenesToLoadQuery.Dispose();
            m_MetaDataLoadPendingQuery.Dispose();
            m_ScenesRemovedQuery.Dispose();
            
            m_ScatterSectionPendingQuery.Dispose();
            m_ScatterSectionLoadingQuery.Dispose();
            m_ScatterSectionLoadedQuery.Dispose();
            
            m_PrewarmPositionsQuery.Dispose();
            
            unsafe
            {
                UnsafeUtility.Free(m_SectionsToLoadCounter.Counter, Allocator.Persistent);
                UnsafeUtility.Free(m_SectionsToUnloadCounter.Counter, Allocator.Persistent);
            }
            
            if(m_SectionsToLoad.IsCreated)
                m_SectionsToLoad.Dispose();
            if(m_SectionsToUnload.IsCreated)
                m_SectionsToUnload.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            //find new scenes and load meta information
            var config = state.EntityManager.GetComponentData<ScatterSceneLoaderSystemConfigData>(state.SystemHandle);
            HandleNewSceneLoadRequests(ref state, config);
            HandlePendingLoadRequests(ref state, config);

            float3 cameraPos = float3.zero;
            var cts = state.World.GetExistingSystem<CameraTrackingSystem>();
            if (cts != SystemHandle.Null)
            {
                var cData = state.EntityManager.GetComponentData<CameraTrackingSystem.CameraTrackingData>(cts);
                cameraPos = cData.CameraPosition;
            }

            state.Dependency = FindSectionsToProcess(ref state, config, cameraPos, state.Dependency);
            state.Dependency.Complete(); //need to sync here to act upon sections to process
            
            HandleSceneSectionStreaming(ref state, config);
            
            HandleRemovedScenes(ref state, config);
        }

        void HandleNewSceneLoadRequests(ref SystemState state,  ScatterSceneLoaderSystemConfigData config)
        {
            using var requests = m_ScatterScenesToLoadQuery.ToComponentDataArray<ScatterSceneToLoad>(Allocator.Temp);
            using var requestEntities = m_ScatterScenesToLoadQuery.ToEntityArray(Allocator.Temp);
            SceneLoadFlags loadFlags = config.BlockOnLoad ? SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn | SceneLoadFlags.DisableAutoLoad : SceneLoadFlags.DisableAutoLoad;
            if (requests.Length > 0)
            {
                SceneSystem.LoadParameters loadParams = new SceneSystem.LoadParameters()
                {
                    Flags = loadFlags
                };

                // Can't use a foreach with a query as SceneSystem.LoadSceneAsync does structural changes
                for (int i = 0; i < requests.Length; i += 1)
                {
                    Entity loadingEntity = SceneSystem.LoadSceneAsync(state.WorldUnmanaged, requests[i].sceneReference, loadParams);
                    state.EntityManager.AddComponent<SceneMetaDataLoadPendingTag>(loadingEntity);
                    state.EntityManager.AddComponentData(requestEntities[i], new ScatterSceneLoaded {SceneEntity = loadingEntity});
                }

            }
        }
        
        void HandlePendingLoadRequests(ref SystemState state, ScatterSceneLoaderSystemConfigData config)
        {
            SceneLoadFlags loadFlags = config.BlockOnLoad ? SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn : 0;
            
            using var pendingMetaLoadEntities = m_MetaDataLoadPendingQuery.ToEntityArray(Allocator.Temp);

            EntityCommandBuffer cmdBuffer = new EntityCommandBuffer(Allocator.Temp);
            for(int index = 0; index < pendingMetaLoadEntities.Length; ++index)
            {
                var ent = pendingMetaLoadEntities[index];
                if(!state.EntityManager.HasComponent<RequestSceneLoaded>(ent) && !state.EntityManager.HasBuffer<ResolvedSectionEntity>(ent))
                {
                    cmdBuffer.DestroyEntity(ent);
                    continue;
                }
                var loadingState = SceneSystem.GetSceneStreamingState(state.WorldUnmanaged, ent);
                if (loadingState == SceneSystem.SceneStreamingState.LoadedSectionEntities || loadingState == SceneSystem.SceneStreamingState.LoadedSuccessfully)
                {
                    var sectionBuffer = state.EntityManager.GetBuffer<ResolvedSectionEntity>(ent);
                    for (int i = 0; i < sectionBuffer.Length; ++i)
                    {
                        var sectionEntity = sectionBuffer[i].SectionEntity;
                        //always load section 0 right away, since its required before any other section
                        if (i == 0)
                        {
                            cmdBuffer.AddComponent(sectionEntity, new RequestSceneLoaded() {LoadFlags = loadFlags});
                        }
                        else
                        {
                             if (state.EntityManager.HasComponent<ScatterSceneSectionMetaData>(sectionEntity))
                             {
                                 cmdBuffer.AddComponent<ScatterSceneSectionPendingTag>(sectionEntity);
                             }
                        }
                       
                    }
                    
                    m_NumberOfSections += sectionBuffer.Length;
                    
                    cmdBuffer.RemoveComponent<SceneMetaDataLoadPendingTag>(ent);
                }

            }
            
            cmdBuffer.Playback(state.EntityManager);
            cmdBuffer.Dispose();
        }

        JobHandle FindSectionsToProcess(ref SystemState state, ScatterSceneLoaderSystemConfigData config, float3 center, JobHandle deps)
        {
            CommonScatterUtilities.Resize(ref m_SectionsToLoad, m_NumberOfSections, Allocator.Persistent);
            CommonScatterUtilities.Resize(ref m_SectionsToUnload, m_NumberOfSections, Allocator.Persistent);
            
            m_SectionsToLoadCounter.Reset();
            m_SectionsToUnloadCounter.Reset();
            
            m_SectionMetaDataTypeHandle.Update(ref state);
            m_EntityTypeHandle.Update(ref state);
            
            var prewarmPositions = m_PrewarmPositionsQuery.ToComponentDataArray<ScatteringPrewarmPosition>(Allocator.TempJob);

            //find sections to load
            var dep1 = new FindSectionsToProcessJob
            {
                AreaCenter = center,
                ExtraAreaMargin = 0,
                PrewarmPositions = prewarmPositions,
                Counter = m_SectionsToLoadCounter,
                SectionsToProcess = m_SectionsToLoad,
                EntityTypeHandle = m_EntityTypeHandle,
                SectionMetaDataType = m_SectionMetaDataTypeHandle,
                FlattenHeightDifferenceMultiplier = config.FlattenHeightDifferenceMultiplier,
                FindSectionsOutside = false
            }.ScheduleParallel(m_ScatterSectionPendingQuery, deps);
            
            //find sections to unload
            var dep2 = new FindSectionsToProcessJob
            {
                AreaCenter = center,
                ExtraAreaMargin = config.AreaUnloadMargin,
                PrewarmPositions = prewarmPositions,
                Counter = m_SectionsToUnloadCounter,
                SectionsToProcess = m_SectionsToUnload,
                EntityTypeHandle = m_EntityTypeHandle,
                SectionMetaDataType = m_SectionMetaDataTypeHandle,
                FlattenHeightDifferenceMultiplier = config.FlattenHeightDifferenceMultiplier,
                FindSectionsOutside = true
            }.ScheduleParallel(m_ScatterSectionLoadedQuery, deps);

            var handle = JobHandle.CombineDependencies(dep1, dep2);
            prewarmPositions.Dispose(handle);
            return handle;
        }
        
        void HandleSceneSectionStreaming(ref SystemState state, ScatterSceneLoaderSystemConfigData config)
        {
            HandleSectionLoading(ref state, config);
            HandleSectionUnloading(ref state, config);

        }

        void HandleSectionLoading(ref SystemState state, ScatterSceneLoaderSystemConfigData config)
        {
            //handle loaded sections
            int currentlyLoadingInstanceCount = 0;
            {
                using var sectionMetaData = m_ScatterSectionLoadingQuery.ToComponentDataArray<ScatterSceneSectionMetaData>(Allocator.Temp);
                using var sectionEntities = m_ScatterSectionLoadingQuery.ToEntityArray(Allocator.Temp);
                
                for (int i = 0; i < sectionEntities.Length; ++i)
                {
                    var loadingEntry = sectionEntities[i];
                    var loadingState = SceneSystem.GetSectionStreamingState(state.WorldUnmanaged, loadingEntry);
                    if (loadingState == SceneSystem.SectionStreamingState.Loaded)
                    {
                        state.EntityManager.AddComponent<ScatterSceneSectionLoadedTag>(loadingEntry);
                        state.EntityManager.RemoveComponent<ScatterSceneSectionLoadingTag>(loadingEntry);
                    }
                    else
                    {
                        currentlyLoadingInstanceCount += sectionMetaData[i].NumberOfInstances;
                    }
                }
            }
            
            //handle sections to load
            int sectionsWaitingToBeLoaded;
            unsafe
            {
                sectionsWaitingToBeLoaded = *m_SectionsToLoadCounter.Counter;
            }
            
            if ((currentlyLoadingInstanceCount < config.MaxInstancesToQueue || currentlyLoadingInstanceCount == 0) && sectionsWaitingToBeLoaded > 0)
            {

                SceneLoadFlags loadFlags = config.BlockOnLoad ? SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn : 0;
                var cts = state.World.GetExistingSystem<CameraTrackingSystem>();
                if (cts != SystemHandle.Null)
                {
                    var sortData = new NativeArray<PendingSectionComparer.SortData>(sectionsWaitingToBeLoaded, Allocator.Temp);
                    var cData = state.EntityManager.GetComponentData<CameraTrackingSystem.CameraTrackingData>(cts);

                    for (int i = 0; i < sectionsWaitingToBeLoaded; ++i)
                    {
                        var pendingSection = m_SectionsToLoad[i];
                        var metaData = state.EntityManager.GetComponentData<ScatterSceneSectionMetaData>(pendingSection);
                        var aabb = metaData.Bounds;
                        float sortValue = math.length(cData.CameraPosition - aabb.Center);
                        var frustrumIntersectionRes = FrustumPlanes.Intersect(cData.CameraFrustrumPlanes, aabb);
                        if (frustrumIntersectionRes == FrustumPlanes.IntersectResult.Out)
                        {
                            sortValue *= 40.0f;
                        }

                        sortData[i] = new PendingSectionComparer.SortData() { index = i, sortValue = sortValue };
                    }
                    
                    PendingSectionComparer comparer = new PendingSectionComparer();
                    sortData.Sort(comparer);

                    
                    for (int i = 0; i < sortData.Length; ++i)
                    {
                        var ind = sortData[i].index;
                        var section = m_SectionsToLoad[ind];
                        var metaData = state.EntityManager.GetComponentData<ScatterSceneSectionMetaData>(section);
                        
                        state.EntityManager.AddComponentData(section, new RequestSceneLoaded() {LoadFlags = loadFlags});
                        
                        state.EntityManager.AddComponent<ScatterSceneSectionLoadingTag>(section);
                        state.EntityManager.RemoveComponent<ScatterSceneSectionPendingTag>(section);
                        
                        //Debug.Log($"loaded section {section.ToFixedString()}");

                        currentlyLoadingInstanceCount += metaData.NumberOfInstances;
                        if (currentlyLoadingInstanceCount >= config.MaxInstancesToQueue) break;
                    }
                    
                    
                    sortData.Dispose();
                }
            }
        }

        void HandleSectionUnloading(ref SystemState state, ScatterSceneLoaderSystemConfigData config)
        {
            int sectionsWaitingToBeUnloaded;
            unsafe
            {
                sectionsWaitingToBeUnloaded = *m_SectionsToUnloadCounter.Counter;
            }

            for (int i = 0; i < sectionsWaitingToBeUnloaded; ++i)
            {
                var sectionToUnload = m_SectionsToUnload[i];
                
                SceneSystem.UnloadScene(state.WorldUnmanaged, sectionToUnload, SceneSystem.UnloadParameters.Default);

                state.EntityManager.RemoveComponent<ScatterSceneSectionLoadedTag>(sectionToUnload);
                state.EntityManager.AddComponent<ScatterSceneSectionPendingTag>(sectionToUnload);
                
                //Debug.Log($"unloaded section {sectionToUnload.ToFixedString()}");
            }
        }
        
        void HandleRemovedScenes(ref SystemState state, ScatterSceneLoaderSystemConfigData config)
        {
            using var removedScenes = m_ScenesRemovedQuery.ToComponentDataArray<ScatterSceneLoaded>(Allocator.Temp);

            if (removedScenes.Length > 0)
            {
                for (int i = 0; i < removedScenes.Length; ++i)
                {
                    if (state.EntityManager.HasBuffer<ResolvedSectionEntity>(removedScenes[i].SceneEntity))
                    {
                        var sectionBuffer = state.EntityManager.GetBuffer<ResolvedSectionEntity>(removedScenes[i].SceneEntity);
                        if (sectionBuffer.IsCreated)
                        {
                            m_NumberOfSections -= sectionBuffer.Length;
                        }
                    }
                    SceneSystem.UnloadScene(state.WorldUnmanaged, removedScenes[i].SceneEntity, SceneSystem.UnloadParameters.DestroyMetaEntities);
                }
             
                state.EntityManager.RemoveComponent<ScatterSceneLoaded>(m_ScenesRemovedQuery);
            }
        
        }
        
    }
}