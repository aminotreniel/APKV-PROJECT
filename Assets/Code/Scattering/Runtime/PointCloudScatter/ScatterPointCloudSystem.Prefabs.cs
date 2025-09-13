using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Serialization;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Rendering;
using Unity.Scenes;
using Unity.Transforms;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace TimeGhost
{
    public partial class ScatterPointCloudSystem
    {
        public delegate void PostProcessScatterPrefab(NativeArray<Entity> allEntities, EntityManager entMngr, int extraDataMask);

        public static event PostProcessScatterPrefab OnPostProcessScatterPrefab;
        
        private static readonly ProfilerMarker s_PrepareLoadedPrefab = new("PrepareLoadedPrefab");
        private static readonly ProfilerMarker s_PrepareLoadedPrefabCopyEntities = new("PrepareLoadedPrefabCopyEntities");

        private const bool c_UseAlternativeLODSystem = true;
        
        void InitializePrefabLoading(ref SystemState state)
        {
            NativeArray<Entity> newPayloadEntities = m_NewScatteringPayloadsQuery.ToEntityArray(Allocator.Temp);
            state.EntityManager.RemoveComponent<ScatteredPointCloudNeedsScatterTag>(newPayloadEntities);

            foreach (var ent in newPayloadEntities)
            {
                var payload = state.EntityManager.GetComponentData<ScatterPointCloudPrefab>(ent);
                var data = state.EntityManager.GetComponentData<ScatterPointCloudInstanceData>(ent);
                LoadPrefab(ref state, payload.PrefabRef);
                if (TryGetPrefabStaging(payload.PrefabRef) != Entity.Null)
                {
                    state.EntityManager.AddComponent<ScatteredPointCloudReadyToScatterTag>(ent);
                }
                else
                {
                    state.EntityManager.AddComponent<ScatteredPointCloudPrefabLoadPendingTag>(ent);
                }

                state.EntityManager.AddComponent<ScatterPointCloudCleanup>(ent);
                //deep copy some members so they won't get destroyed when the scene is unloaded. TODO: have scene unloading work with staging world to avoid this
                state.EntityManager.SetComponentData(ent, new ScatterPointCloudCleanup() { ScatterId = data.ScatterId, ScatterGroupId = data.ScatterGroupId, PrefabRef = payload.PrefabRef, Attributes = DeepCopy(data.Attributes), AttributeRanges = DeepCopy(data.AttributeRanges), ImportanceData = DeepCopy(data.ImportanceData) });
            }

            newPayloadEntities.Dispose();
        }

        bool HasPendingPrefabsToLoad()
        {
            return m_PendingPrefabs.Count > 0;
        }

        void HandlePendingPrefabLoading(ref SystemState state)
        {
            //handle loaded prefabs
            NativeList<Hash128> prefabsReady = new NativeList<Hash128>(Allocator.Temp);
            NativeList<Hash128> prefabsFailed = new NativeList<Hash128>(Allocator.Temp);
            foreach (var pendingEntry in m_PendingPrefabs)
            {
                if (SceneSystem.IsSceneLoaded(state.WorldUnmanaged, pendingEntry.Value.loadedPrefab))
                {
                    prefabsReady.Add(pendingEntry.Key);
                }
                else
                {
                    //if the prefab loading failed, try again
                    var streamingState = SceneSystem.GetSceneStreamingState(state.WorldUnmanaged, pendingEntry.Value.loadedPrefab);
                    var sceneLoadingValidState = streamingState == SceneSystem.SceneStreamingState.Loading ||
                                                 streamingState == SceneSystem.SceneStreamingState.LoadedSectionEntities ||
                                                 streamingState == SceneSystem.SceneStreamingState.LoadedSuccessfully;
                    if (!sceneLoadingValidState)
                    {
                        prefabsFailed.Add(pendingEntry.Key);
                    }
                }
            }

            const int MAX_LOAD_ATTEMPTS = 4;
            foreach (var hash in prefabsFailed)
            {
                var entry = m_PendingPrefabs[hash];
                SceneSystem.UnloadScene(state.WorldUnmanaged, entry.reference, SceneSystem.UnloadParameters.DestroyMetaEntities);
                if (entry.numberOfAttempts == MAX_LOAD_ATTEMPTS)
                {
                    m_PendingPrefabs.Remove(hash);
                    m_PrefabRefCounts.Remove(hash);
                    
                    Debug.LogError($"Failed to load prefab {entry.reference.AssetGUID} after {MAX_LOAD_ATTEMPTS} attempts. Giving up.");
                }
                else
                {
                    ++entry.numberOfAttempts;
                    entry.loadedPrefab = LoadPrefabInternal(state.WorldUnmanaged, entry.reference);
                    m_PendingPrefabs[hash] = entry;
                }
            }

            foreach (var hash in prefabsReady)
            {
                var newLoadedPrefab = m_PendingPrefabs[hash].loadedPrefab;
                var stagingWorldCopy = PostProcessLoadedEntity(newLoadedPrefab);
                m_LoadedPrefabs.Add(hash, new LoadedPrefab() { stagingWorldRoot = stagingWorldCopy, mainWorldRoot = newLoadedPrefab, variantPerPointCloud = new NativeHashMap<int, Entity>(16, Allocator.Persistent) });
                m_PendingPrefabs.Remove(hash);
            }

            prefabsReady.Dispose();
            prefabsFailed.Dispose();

            //handle payloads which might have prefabs loaded
            NativeArray<Entity> pendingPayloads = m_PendingPrefabPayloadsQuery.ToEntityArray(Allocator.Temp);
            foreach (var ent in pendingPayloads)
            {
                var prefab = state.EntityManager.GetComponentData<ScatterPointCloudPrefab>(ent);
                Entity loadedPrefab = TryGetPrefabStaging(prefab.PrefabRef);
                if (loadedPrefab != Entity.Null)
                {
                    state.EntityManager.RemoveComponent<ScatteredPointCloudPrefabLoadPendingTag>(ent);
                    state.EntityManager.AddComponent<ScatteredPointCloudReadyToScatterTag>(ent);
                }
            }
        }

        void HandlePreloadPrefabs(ref SystemState state)
        {
            using var prefabPreloadComponents = m_PrefabPreloadEntitiesQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < prefabPreloadComponents.Length; ++i)
            {
                using var buffer = state.EntityManager.GetBuffer<ScatterPointCloudPreloadPrefab>(prefabPreloadComponents[i]).ToNativeArray(Allocator.Temp);

                for (int k = 0; k < buffer.Length; ++k)
                {
                    LoadPrefab(ref state, buffer[k].PrefabRef, 0, true);
                }
            }

            state.EntityManager.RemoveComponent<ScatterPointCloudPreloadPrefab>(prefabPreloadComponents);
        }

        Entity LoadPrefabInternal(WorldUnmanaged world, EntityPrefabReference prefabReferenceId, bool blockOnLoad = false)
        {
            return SceneSystem.LoadPrefabAsync(world, prefabReferenceId, new SceneSystem.LoadParameters() { Priority = 100, AutoLoad = true, Flags = blockOnLoad ? SceneLoadFlags.BlockOnStreamIn : 0 });
        }

        public Entity GetPrefabStagingForPointCloud(EntityPrefabReference reference, int extraDataMask)
        {
            if (m_LoadedPrefabs.TryGetValue(reference.AssetGUID, out var entry))
            {
                if (entry.variantPerPointCloud.TryGetValue(extraDataMask, out var entity))
                {
                    return entity;
                }
                else
                {
                    return CreatePrefabVariantStagingForPointCloud(entry.stagingWorldRoot, extraDataMask);
                }
            }

            return Entity.Null;
        }
        
        public Entity TryGetPrefabStaging(EntityPrefabReference reference)
        {
            if (m_LoadedPrefabs.TryGetValue(reference.AssetGUID, out var entry))
            {
                return entry.stagingWorldRoot;
            }

            return Entity.Null;
        }


        public void LoadPrefab(ref SystemState state, EntityPrefabReference reference, int refCountIncrease = 1, bool blockOnLoad = false)
        {
            if (m_PrefabRefCounts.TryGetValue(reference.AssetGUID, out var val))
            {
                m_PrefabRefCounts[reference.AssetGUID] = val + refCountIncrease;
            }
            else
            {
                m_PrefabRefCounts[reference.AssetGUID] = refCountIncrease;
                m_PendingPrefabs.Add(reference.AssetGUID, new PendingPrefab()
                {
                    reference = reference,
                    loadedPrefab = LoadPrefabInternal(state.WorldUnmanaged, reference, blockOnLoad),
                    numberOfAttempts = 1
                });
            }
        }

        public void ReleasePrefab(ref SystemState state, EntityPrefabReference reference)
        {
            if (m_PrefabRefCounts.TryGetValue(reference.AssetGUID, out var val))
            {
                if (val == 1)
                {
                    bool unloadScene = false;
                    m_PrefabRefCounts.Remove(reference.AssetGUID);
                    if (m_LoadedPrefabs.TryGetValue(reference.AssetGUID, out var loadedEntry))
                    {
                        //state.EntityManager.DestroyEntity(loadedEntry.mainWorldRoot);

                        loadedEntry.Destroy(m_StagingWorld.EntityManager);
                        m_LoadedPrefabs.Remove(reference.AssetGUID);
                        unloadScene = true;
                    }

                    if (m_PendingPrefabs.TryGetValue(reference.AssetGUID, out var pendingEntry))
                    {
                        m_PendingPrefabs.Remove(reference.AssetGUID);
                        unloadScene = true;
                    }

                    if (unloadScene)
                    {
                        SceneSystem.UnloadScene(state.WorldUnmanaged, reference);
                    }
                }
                else
                {
                    m_PrefabRefCounts[reference.AssetGUID] = val - 1;
                }
            }
        }

        private Entity CreatePrefabVariantStagingForPointCloud(Entity basePrefabStaging, int extraDataMask)
        {
            var entManager = m_StagingWorld.EntityManager;
            
            var newCopy = entManager.Instantiate(basePrefabStaging);

            NativeArray<Entity> allEntities;

            if (entManager.HasComponent<LinkedEntityGroup>(newCopy))
            {
                var linkedEntities = entManager.GetBuffer<LinkedEntityGroup>(newCopy, isReadOnly: true).Reinterpret<Entity>().AsNativeArray();
                allEntities = new NativeArray<Entity>(linkedEntities.Length, Allocator.Temp);
                allEntities.CopyFrom(linkedEntities);
                entManager.AddComponent<OmitLinkedEntityGroupFromPrefabInstance>(newCopy);
            }
            else
            {
                allEntities = new NativeArray<Entity>(1, Allocator.Temp);
                allEntities[0] = newCopy;

            }

            entManager.AddComponent(allEntities, typeof(Prefab));
            OnPostProcessScatterPrefab?.Invoke(allEntities, entManager, extraDataMask);
            
            //add component to tell staging system to go through the entities and optimize it for entity graphics package 
            for (int i = 0; i < allEntities.Length; ++i)
            {
                m_StagingWorld.EntityManager.AddComponentData(allEntities[i], new ScatterPointCloudStagingSystem.PrefabComponentOptimizationPending(){AddChunkComponents = !c_UseAlternativeLODSystem});
            }

            allEntities.Dispose();
            
            return newCopy;
        }

        private Entity PostProcessLoadedEntity(Entity loadedPrefab)
        {
            Entity newRoot = Entity.Null;
            using (var scope = s_PrepareLoadedPrefab.Auto())
            {
                var allEntities = PreparePrefab(loadedPrefab, EntityManager, m_StagingWorld.EntityManager);
                newRoot = allEntities[0];
                allEntities.Dispose();
            }

            return newRoot;
        }

        public static NativeList<Entity> PreparePrefab(Entity srcPrefab, EntityManager srcManager, EntityManager dstManager)
        {
            if (c_UseAlternativeLODSystem)
            {
                ScatterLODSystem.TransformPrefabToScatterLOD(srcPrefab, srcManager);
            }
            
            var allEntities = PrepareLoadedPrefabForScattering(srcPrefab, srcManager, dstManager);
            AddExtraComponents(allEntities, dstManager);
            return allEntities;
        }

        //omit the LinkedEntityGroup from instances and flatten the hierarchy (adapted from env). Also copy all entities to scattering world. Returns all children and the root (root being the last entry).
        private static NativeList<Entity> PrepareLoadedPrefabForScattering(Entity loadedPrefab,EntityManager srcEntityManager, EntityManager stagingEntityManager)
        {
            ComponentTypeSet componentsToRemove = new(new ComponentType[]
            {
                typeof(Parent), typeof(LocalTransform), typeof(LinkedEntityGroup), typeof(Static),
                typeof(EntityGuid), typeof(SceneTag), typeof(SceneSection), typeof(SceneReference)
            });
            
            //all children flattened
            var allChildren = new NativeList<Entity>(64, Allocator.Temp);
            var prefabRootSrc = loadedPrefab;
            if (srcEntityManager.HasComponent<PrefabRoot>(loadedPrefab))
            {
                prefabRootSrc = srcEntityManager.GetComponentData<PrefabRoot>(loadedPrefab).Root;
            }

            Entity prefab;
            if (srcEntityManager.HasComponent<LinkedEntityGroup>(prefabRootSrc))
            {
                var srcEntities = srcEntityManager.GetBuffer<LinkedEntityGroup>(prefabRootSrc, isReadOnly: true).Reinterpret<Entity>().AsNativeArray();
                Debug.Assert(srcEntities[0] == prefabRootSrc);
                using (var copiedEntities = new NativeArray<Entity>(srcEntities.Length, Allocator.Temp))
                using (var scope = s_PrepareLoadedPrefabCopyEntities.Auto())
                {
                    stagingEntityManager.CopyEntitiesFrom(srcEntityManager, srcEntities, copiedEntities);
                    prefab = copiedEntities[0];
                }

                var prefabRootTransform = srcEntityManager.HasComponent<LocalToWorld>(prefabRootSrc)
                    ? srcEntityManager.GetComponentData<LocalToWorld>(prefabRootSrc).Value
                    : float4x4.identity;
                var prefabRootInvTransform = math.inverse(prefabRootTransform);

                var entitiesToVisit = new NativeList<Entity>(64, Allocator.Temp);
                entitiesToVisit.Add(prefab);

                while (entitiesToVisit.Length > 0)
                {
                    var ent = entitiesToVisit[0];
                    entitiesToVisit.RemoveAtSwapBack(0);
                    allChildren.Add(ent);


                    if (stagingEntityManager.HasComponent<LinkedEntityGroup>(ent))
                    {
                        var linkedEntities = stagingEntityManager.GetBuffer<LinkedEntityGroup>(ent, isReadOnly: true).Reinterpret<Entity>().ToNativeArray(Allocator.Temp);
                        Debug.Assert(linkedEntities[0] == ent);

                        for (int i = 1; i < linkedEntities.Length; ++i)
                            entitiesToVisit.Add(linkedEntities[i]);
                    }
                }

                //force root to always have transform
                if (!stagingEntityManager.HasComponent<LocalToWorld>(prefab))
                    stagingEntityManager.AddComponentData(prefab, new LocalToWorld { Value = float4x4.identity });

                //force parent to 0
                {
                    var localToWorld = stagingEntityManager.GetComponentData<LocalToWorld>(prefab).Value;
                    localToWorld.c3.xyz = float3.zero;
                    Debug.Assert(math.all(localToWorld.Translation() == float3.zero));
                    stagingEntityManager.SetComponentData(prefab, new LocalToWorld { Value = localToWorld });
                }

                for (int i = 0; i < allChildren.Length; ++i)
                {
                    stagingEntityManager.RemoveComponent(allChildren[i], componentsToRemove);
                }

                //make all children root relative
                for (int i = 1; i < allChildren.Length; ++i)
                {
                    // skip children with no transform
                    if (!stagingEntityManager.HasComponent<LocalToWorld>(allChildren[i]))
                        continue;

                    var localToWorld = stagingEntityManager.GetComponentData<LocalToWorld>(allChildren[i]).Value;
                    localToWorld = math.mul(prefabRootInvTransform, localToWorld);
                    stagingEntityManager.AddComponentData(allChildren[i], new ScatteredInstanceParent { Value = prefab });
                    stagingEntityManager.SetComponentData(allChildren[i], new LocalToWorld { Value = localToWorld });
                }

                stagingEntityManager.AddBuffer<LinkedEntityGroup>(prefab).CopyFrom(allChildren.AsArray().Reinterpret<LinkedEntityGroup>());
                
            }
            else
            {
                var srcEntities = new NativeArray<Entity>(1, Allocator.Temp);
                srcEntities[0] = prefabRootSrc;
                using var copiedEntities = new NativeList<Entity>(1, Allocator.Temp);
                copiedEntities.Add(Entity.Null);
                stagingEntityManager.CopyEntitiesFrom(srcEntityManager, srcEntities, copiedEntities.AsArray());
                srcEntities.Dispose();

                prefab = copiedEntities[0];

                allChildren.Add(prefab);
                float4x4 transform;
                if (stagingEntityManager.HasComponent<LocalToWorld>(prefab))
                {
                    transform = stagingEntityManager.GetComponentData<LocalToWorld>(prefab).Value;
                    transform.c3.xyz = float3.zero;
                }
                else
                {
                    transform = float4x4.identity;
                }

                stagingEntityManager.AddComponentData(prefab, new LocalToWorld { Value = transform });
                
                for (int i = 0; i < allChildren.Length; ++i)
                {
                    stagingEntityManager.RemoveComponent(allChildren[i], componentsToRemove);
                }
            }

            return allChildren;
        }
        
        static void AddExtraComponents(NativeList<Entity> allEntities, EntityManager entMngr)
        {
            entMngr.AddComponent<GeneratedScatterInstanceTag>(allEntities.AsArray());
            entMngr.AddSharedComponent(allEntities.AsArray(), new ScatterPointCloudScatterId() { ScatterId = default }); //add dummy scatterId: to be setup during instantiation
            entMngr.AddSharedComponent(allEntities.AsArray(), new ScatterPointCloudScatterGroupId() { Value = default }); //add dummy scatterId: to be setup during instantiation
            entMngr.AddSharedComponent(allEntities.AsArray(), new ScatteredInstanceBatch() { RuntimeBatchId = 0xFFFFFFFF }); //add dummy: to be setup during instantiation
            entMngr.AddSharedComponent(allEntities.AsArray(), new ScatterPointCloudTileInfo() { SizeMinMax = float2.zero, DensityMinMaxAverage = int3.zero}); //add dummy: to be setup during instantiation
            entMngr.AddComponent<ScatteredInstanceRootTag>(allEntities[0]);
            for (int i = 0; i < allEntities.Length; ++i)
            {
                if (entMngr.HasComponent<MaterialMeshInfo>(allEntities[i]))
                {
                    entMngr.AddComponent<ScatteredInstanceImportanceData>(allEntities[i]);
                }
            }
            
        }
    }
}