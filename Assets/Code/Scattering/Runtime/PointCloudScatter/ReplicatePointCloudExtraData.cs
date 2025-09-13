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

#if HAS_ENVIRONMENT
    using Unity.Environment;
#endif


namespace TimeGhost
{

    [MaterialProperty("_ScatteredInstanceExtraData")]
    public struct ScatteredInstanceRenderExtraData : IComponentData
    {
        public float2 Value;
    }
    
    [MaterialProperty("_ScatteredInstanceExtraData2")]
    public struct ScatteredInstanceRenderExtraData2 : IComponentData
    {
        public float Value;
    }
    
    [MaterialProperty("_ScatteredInstanceExtraData3")]
    public struct ScatteredInstanceRenderExtraData3: IComponentData
    {
        public uint Value;
    }
    
    [BurstCompile]
    public struct ReplicatePointCloudExtraData 
    {
        
        [BurstCompile]
        [AOT.MonoPInvokeCallback(typeof(PassExtraDataToComponent))]
        private static unsafe void PassThruCopy(byte* dst, int dstStride, byte* src, int srcStride) => UnsafeUtility.MemCpy(dst, src, dstStride);

        [BurstCompile]
        [AOT.MonoPInvokeCallback(typeof(PassExtraDataToComponent))]
        private static unsafe void ConvertColor32ToFloat4(byte* dst, int dstStride, byte* src, int srcStride)
        {
            Color32 col = *(Color32*)src;
            float4 colF = new float4(col.r, col.g, col.b, col.a);
            colF /= 255.0f;
            UnsafeUtility.MemCpy(dst, &colF, UnsafeUtility.SizeOf<float4>());
        }
        
        [BurstCompile]
        [AOT.MonoPInvokeCallback(typeof(PassExtraDataToComponent))]
        private static unsafe void ConvertColor32ToFloat(byte* dst, int dstStride, byte* src, int srcStride)
        {
            float col = *(float*)src;
            UnsafeUtility.MemCpy(dst, &col, UnsafeUtility.SizeOf<float>());
        }
        
        private unsafe delegate void PassExtraDataToComponent(byte* dst, int dstStride, byte* src, int srcStride);

        private struct ExtraDataMapping
        {
            public ScatterExtraData sourceDataType;
            public ComponentType destinationComponentType;
            public FunctionPointer<PassExtraDataToComponent> conversionFunc;
        }
        
        
        public struct ScatteredInstanceRenderExtraDataProcessedTag : IComponentData
        {
        }
    
        public struct ExtraDataProcessed : ICleanupComponentData
        {
            public Hash128 extraDataBlobGuid;
        }
        
        [BurstCompile]
        private struct ReplicateParentExtraDataToChildrenRenderData : IJobChunk 
        {
            [ReadOnly] public ComponentTypeHandle<MeshLODComponent> MeshLODType;
            [ReadOnly] public ComponentLookup<ScatteredInstanceExtraData> ParentDataLookup;
            [ReadOnly]
            public NativeHashMap<Hash128, BlobAssetReference<ScatteringExtraDataBlob>>.ReadOnly ExtraDataRegistry;
            [NativeDisableParallelForRestriction]
            public DynamicComponentTypeHandle ExtraRenderDataType;

            public FunctionPointer<PassExtraDataToComponent> ConvFunc;
            public int Stride;
            public int ExtraDataTypeIndex;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var meshLODArray = chunk.GetNativeArray(ref MeshLODType);
                var extraDataDstPtr = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ExtraRenderDataType, Stride );
                var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                BlobAssetReference<ScatteringExtraDataBlob> assetBlobRef = default;
                int sourceStride = 0;
                int sourceOffset = 0;
                bool firstLoop = true;
                
                while(enumerator.NextEntityIndex(out int i))
                {
                    var meshLodComponent = meshLODArray[i];

                    if (ParentDataLookup.HasComponent(meshLodComponent.Group))
                    {
                        var parentData = ParentDataLookup[meshLodComponent.Group];
                        if (firstLoop) //assumption is that all the entities in a chunk share the same extra data
                        {
                            if(ExtraDataRegistry.TryGetValue(parentData.ExtraDataHash, out var datablob))
                            {
                                assetBlobRef = datablob;
                            } 
                            else
                            {
                                return;
                            }
                            firstLoop = false;
                            sourceStride = ExtraDataUtils.GetExtraDataStride(assetBlobRef.Value.ExtraDataMask);
                            sourceOffset = ExtraDataUtils.GetExtraDataOffsetFromMask(assetBlobRef.Value.ExtraDataMask, ExtraDataTypeIndex);
                        }
                        
                        int dstOffset = (i * Stride);
                        int srcOffset = (parentData.InstanceIndex * sourceStride) + sourceOffset;
                        unsafe
                        {
                            ConvFunc.Invoke((byte*)extraDataDstPtr.GetUnsafePtr() + dstOffset, Stride, (byte*)assetBlobRef.Value.Data.GetUnsafePtr() + srcOffset, sourceStride);
                        }
                    }
                }
            }
        }
        
        [BurstCompile]
        private struct ReplicateExtraDataToRenderData : IJobChunk
        {
            [ReadOnly]
            public NativeHashMap<Hash128, BlobAssetReference<ScatteringExtraDataBlob>>.ReadOnly ExtraDataRegistry;
            [ReadOnly]
            public ComponentTypeHandle<ScatteredInstanceExtraData> ExtraDataType;
            [NativeDisableParallelForRestriction]
            public DynamicComponentTypeHandle ExtraRenderDataType;
            public FunctionPointer<PassExtraDataToComponent> ConvFunc;
            public int Stride;
            public int ExtraDataTypeIndex;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var srcData = chunk.GetNativeArray(ref ExtraDataType);
                var extraDataDstPtr = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ExtraRenderDataType, Stride );

                var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);

                BlobAssetReference<ScatteringExtraDataBlob> assetBlobRef = default;
                int sourceStride = 0;
                int sourceOffset = 0;
                bool firstLoop = true;
                
                while(enumerator.NextEntityIndex(out int i))
                {
                    var srcDataEntry = srcData[i];
                    if (firstLoop) //assumption is that all the entities in a chunk share the same extra data
                    {
                        if(ExtraDataRegistry.TryGetValue(srcDataEntry.ExtraDataHash, out var datablob))
                        {
                            assetBlobRef = datablob;
                        } 
                        else
                        {
                            return;
                        }
                        firstLoop = false;
                        sourceStride = ExtraDataUtils.GetExtraDataStride(assetBlobRef.Value.ExtraDataMask);
                        sourceOffset = ExtraDataUtils.GetExtraDataOffsetFromMask(assetBlobRef.Value.ExtraDataMask, ExtraDataTypeIndex);
                    }

                    int dstOffset = (i * Stride);
                    int srcOffset = (srcDataEntry.InstanceIndex * sourceStride) + sourceOffset;
                    unsafe
                    {
                        ConvFunc.Invoke((byte*)extraDataDstPtr.GetUnsafePtr() + dstOffset, Stride, (byte*)assetBlobRef.Value.Data.GetUnsafePtr() + srcOffset, sourceStride);
                    }
                }
            }
        }

        private NativeArray<EntityQuery> m_ScatteredInstanceChildrenQueries;
        private NativeArray<EntityQuery> m_ScatteredInstancesWithRenderDataToReplicateQueries;
        
        private EntityQuery m_NewExtraDataBlobs;
        private EntityQuery m_RemovedExtraDataBlobs;
        private EntityQuery m_HandledExtraDataBlobs;
        
        private ComponentTypeHandle<MeshLODComponent> m_MeshLODType;
        private ComponentTypeHandle<ScatteredInstanceExtraData> m_ExtraDataType;
        private ComponentLookup<ScatteredInstanceExtraData> m_InstanceParentDataLookup;

        private NativeHashMap<Hash128, BlobAssetReference<ScatteringExtraDataBlob>> m_ExtraDataBlobs;

        private NativeArray<DynamicComponentTypeHandle> m_ExtraDataTypeHandles;

        private NativeArray<ExtraDataMapping> m_ExtraDataToComponentMapping;

        public NativeArray<DynamicComponentTypeHandle> ExtraDataTypeHandles => m_ExtraDataTypeHandles;
        public NativeArray<EntityQuery> ScatteredInstanceChildrenPerTypeQueries => m_ScatteredInstanceChildrenQueries;
        public NativeArray<EntityQuery> ScatteredInstancesPerTypeQueries => m_ScatteredInstancesWithRenderDataToReplicateQueries;
        
        public void OnCreate(ref SystemState state)
        {
            unsafe
            {
                FunctionPointer<PassExtraDataToComponent> passThruFuncPtr = BurstCompiler.CompileFunctionPointer<PassExtraDataToComponent>(PassThruCopy);
                FunctionPointer<PassExtraDataToComponent> convertColorToFloat4FuncPtr = BurstCompiler.CompileFunctionPointer<PassExtraDataToComponent>(ConvertColor32ToFloat4);
                FunctionPointer<PassExtraDataToComponent> convertColorToFloatFuncPtr = BurstCompiler.CompileFunctionPointer<PassExtraDataToComponent>(ConvertColor32ToFloat);
                
                
                m_ExtraDataToComponentMapping = new NativeArray<ExtraDataMapping>(3, Allocator.Persistent);
                m_ExtraDataToComponentMapping[0] = new ExtraDataMapping() { sourceDataType = ScatterExtraData.AgeHealth, destinationComponentType = ComponentType.ReadWrite<ScatteredInstanceRenderExtraData>(), conversionFunc = passThruFuncPtr};
                m_ExtraDataToComponentMapping[1] = new ExtraDataMapping() { sourceDataType = ScatterExtraData.Color, destinationComponentType = ComponentType.ReadWrite<ScatteredInstanceRenderExtraData2>(), conversionFunc = convertColorToFloatFuncPtr};
                m_ExtraDataToComponentMapping[2] = new ExtraDataMapping() { sourceDataType = ScatterExtraData.PartIndex, destinationComponentType = ComponentType.ReadWrite<ScatteredInstanceRenderExtraData3>(), conversionFunc = passThruFuncPtr};
            }
            
            m_ExtraDataTypeHandles =
                new NativeArray<DynamicComponentTypeHandle>(m_ExtraDataToComponentMapping.Length, Allocator.Persistent);
            m_ScatteredInstanceChildrenQueries =
                new NativeArray<EntityQuery>(m_ExtraDataToComponentMapping.Length, Allocator.Persistent); 
            m_ScatteredInstancesWithRenderDataToReplicateQueries =
                new NativeArray<EntityQuery>(m_ExtraDataToComponentMapping.Length, Allocator.Persistent);
            
            for (int i = 0; i < m_ExtraDataToComponentMapping.Length; ++i)
            {
                var componentType = m_ExtraDataToComponentMapping[i].destinationComponentType;
                m_ExtraDataTypeHandles[i] =
                    state.GetDynamicComponentTypeHandle(componentType);
                m_ScatteredInstanceChildrenQueries[i] = state.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<MeshLODComponent>(),
                    componentType,
                    ComponentType.Exclude<ScatteredInstanceExtraData>(),
                    ComponentType.Exclude<ScatteredInstanceRenderExtraDataProcessedTag>());
                
                m_ScatteredInstancesWithRenderDataToReplicateQueries[i] = state.EntityManager.CreateEntityQuery(
                    componentType,
                    ComponentType.ReadOnly<ScatteredInstanceExtraData>(),
                    ComponentType.Exclude<ScatteredInstanceRenderExtraDataProcessedTag>());
            }


            m_NewExtraDataBlobs = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadWrite<ScatteringExtraData>(),
                ComponentType.Exclude<ExtraDataProcessed>());
            m_RemovedExtraDataBlobs = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadWrite<ExtraDataProcessed>(),
                ComponentType.Exclude<ScatteringExtraData>());
            
            m_HandledExtraDataBlobs = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ExtraDataProcessed>(),
                ComponentType.ReadOnly<ScatteringExtraData>());
            
            m_ExtraDataType = state.GetComponentTypeHandle<ScatteredInstanceExtraData>(true);
            m_MeshLODType = state.GetComponentTypeHandle<MeshLODComponent>(true);
            m_InstanceParentDataLookup = state.GetComponentLookup<ScatteredInstanceExtraData>(true);

            m_ExtraDataBlobs =
                new NativeHashMap<Hash128, BlobAssetReference<ScatteringExtraDataBlob>>(64, Allocator.Persistent);

            
        }
        
        public void OnDestroy(ref SystemState state)
        {
            foreach (var q in m_ScatteredInstanceChildrenQueries)
            {
                q.Dispose();
            }
            
            foreach (var q in m_ScatteredInstancesWithRenderDataToReplicateQueries)
            {
                q.Dispose();
            }
            m_ScatteredInstanceChildrenQueries.Dispose();
            m_ScatteredInstancesWithRenderDataToReplicateQueries.Dispose();
            m_ExtraDataToComponentMapping.Dispose();
            m_NewExtraDataBlobs.Dispose();
            m_RemovedExtraDataBlobs.Dispose();
            m_HandledExtraDataBlobs.Dispose();
            m_ExtraDataBlobs.Dispose();
            m_ExtraDataTypeHandles.Dispose();
        }
        
        public void OnUpdate(ref SystemState state)
        {
            HandleRemovedBlobs(ref state);
            #if UNITY_EDITOR
            HandleIncrementalBakingChanges(ref state);            
            #endif
            
            HandleNewBlobs(ref state);

            for (int i = 0; i < m_ExtraDataTypeHandles.Length; ++i)
            {
                var handle = m_ExtraDataTypeHandles[i];
                handle.Update(ref state);
                m_ExtraDataTypeHandles[i] = handle;
            }
            
            m_ExtraDataType.Update(ref state);
            m_MeshLODType.Update(ref state);
            m_InstanceParentDataLookup.Update(ref state);

            state.Dependency = ReplicateExtraDataToChildren(state.Dependency);
        }

        void HandleNewBlobs(ref SystemState state)
        {
            var newBlobsArray = m_NewExtraDataBlobs.ToComponentDataArray<ScatteringExtraData>(Allocator.Temp);
            var newBlobsEntitesArray = m_NewExtraDataBlobs.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < newBlobsArray.Length; ++i)
            {
                var newBlob = newBlobsArray[i];
                var extraDataProcessed = new ExtraDataProcessed() { extraDataBlobGuid = newBlob.ExtraDataHash };
                state.EntityManager.AddComponentData(newBlobsEntitesArray[i], extraDataProcessed);
                
                m_ExtraDataBlobs[newBlob.ExtraDataHash] = newBlob.ExtraData;
            }
            
            newBlobsEntitesArray.Dispose();
            newBlobsArray.Dispose();
        }
        
        void HandleRemovedBlobs(ref SystemState state)
        {
            var cleanupArray = m_RemovedExtraDataBlobs.ToComponentDataArray<ExtraDataProcessed>(Allocator.Temp);
            foreach (var c in cleanupArray)
            {
                m_ExtraDataBlobs.Remove(c.extraDataBlobGuid);
            }
            state.EntityManager.RemoveComponent<ExtraDataProcessed>(m_RemovedExtraDataBlobs);
            cleanupArray.Dispose();
            
        }
        
#if UNITY_EDITOR
        //incremental baking might change the actual data in components that are baked but leave the cleanup component intact. Check for this case: cleanup the component and force rescatter
        void HandleIncrementalBakingChanges(ref SystemState state)
        {

            using NativeArray<Entity> entityArray = m_HandledExtraDataBlobs.ToEntityArray(Allocator.Temp);
            if (!entityArray.IsCreated || entityArray.Length == 0) return;

            foreach (var entity in entityArray)
            {
                ExtraDataProcessed cleanupComponent =
                    state.EntityManager.GetComponentData<ExtraDataProcessed>(entity);
                
                ScatteringExtraData scatteringData =
                    state.EntityManager.GetComponentData<ScatteringExtraData>(entity);

                //mismatch between cleanup and current hash: incremental baking has changed the data, remove cleanup and let it be handled as new
                if (scatteringData.ExtraDataHash != cleanupComponent.extraDataBlobGuid)
                {
                    state.EntityManager.RemoveComponent<ExtraDataProcessed>(entity);
                }
            }
        }
#endif
        
        [BurstCompile]
        JobHandle ReplicateExtraDataToChildren(JobHandle handle)
        {
            var jobHandle = handle;
            for (int i = 0; i < m_ExtraDataToComponentMapping.Length; ++i)
            {
                var destinationCompTypeInfo = TypeManager.GetTypeInfo(m_ExtraDataToComponentMapping[i].destinationComponentType.TypeIndex);
                
                jobHandle = new ReplicateParentExtraDataToChildrenRenderData()
                {
                    ParentDataLookup = m_InstanceParentDataLookup,
                    MeshLODType = m_MeshLODType,
                    ExtraDataRegistry = m_ExtraDataBlobs.AsReadOnly(),
                    ExtraDataTypeIndex = i,
                    ExtraRenderDataType = m_ExtraDataTypeHandles[i],
                    Stride = destinationCompTypeInfo.TypeSize,
                    ConvFunc = m_ExtraDataToComponentMapping[i].conversionFunc
                }.ScheduleParallel(m_ScatteredInstanceChildrenQueries[i], jobHandle);

                jobHandle = new ReplicateExtraDataToRenderData()
                {
                    ExtraDataRegistry = m_ExtraDataBlobs.AsReadOnly(),
                    ExtraDataTypeIndex = i,
                    ExtraRenderDataType = m_ExtraDataTypeHandles[i],
                    Stride = destinationCompTypeInfo.TypeSize,
                    ExtraDataType = m_ExtraDataType,
                    ConvFunc = m_ExtraDataToComponentMapping[i].conversionFunc
                }.ScheduleParallel(m_ScatteredInstancesWithRenderDataToReplicateQueries[i], jobHandle);
            }

            return jobHandle;
        }


#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#endif
        private static void RegisterPrepareScatterPrefab()
        {
            ScatterPointCloudSystem.OnPostProcessScatterPrefab += OnPrepareScatterPrefab;
        }

        
        public static void OnPrepareScatterPrefab(NativeArray<Entity> prefabEntityGroup, EntityManager entityManager, int extraDataMask)
        {
            bool hasExtraDataAgeHealth = ExtraDataUtils.HasExtraData(extraDataMask, ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.AgeHealth));
            bool hasExtraDataColor = ExtraDataUtils.HasExtraData(extraDataMask, ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.Color));
            bool hasExtraDataPartIndex = ExtraDataUtils.HasExtraData(extraDataMask, ExtraDataUtils.GetExtraDataIndex(ScatterExtraData.PartIndex));
            foreach (var e in prefabEntityGroup)
            {
                if (entityManager.HasComponent<NeedsMotionVectorsTag>(e))
                {
                    entityManager.AddComponent<PerVertexMotionVectors_Tag>(e);
                }
                
                if (hasExtraDataAgeHealth)
                {
                    entityManager.AddComponent<ScatteredInstanceRenderExtraData>(e);
                }
                if (hasExtraDataColor)
                {
                    entityManager.AddComponent<ScatteredInstanceRenderExtraData2>(e);
                }
                if (hasExtraDataPartIndex)
                {
                    entityManager.AddComponent<ScatteredInstanceRenderExtraData3>(e);
                }
                
            }
        }
    }
}
