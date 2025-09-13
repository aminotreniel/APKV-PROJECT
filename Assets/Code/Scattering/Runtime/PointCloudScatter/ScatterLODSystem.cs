
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;


namespace TimeGhost
{
    [MaterialProperty("_PerInstanceDebugColor")]
    public struct ScatterLODDebugColor : IComponentData
    {
        public float ColorPacked;
    }
    
    public struct ScatterLODMeshMaterialIndices : IComponentData
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
        
        public FixedArray32Bytes<short> MeshMatIndices;
    }   
    
    public struct ScatterMeshMaterialIndices : IComponentData
    {
        public short meshIndex;
        public short matIndex;
    }
    
    public struct ScatterLODDistances : IComponentData
    {
        public FixedArray32Bytes<float> Distances;
        public int LastLODWithValidEntry;
    }
    
    public struct IgnoreMaxDistanceCullingTag : IComponentData
    {}
    
    public struct ScatterLODSystemConfigData : IComponentData
    {
        public float ShadowCullScreenSize;
        public float ShadowCullScreenSizeVariation;
        public bool ShadowCullOutsideCamera;
        public float ShadowCullFrustrumPlaneMargin;
        public float InstanceSizeRangeInterpolateParam;
        public int MaxShadowCasterChunkChangesPerFrame;
        public int LODTransitionConstantOffset;
        public float LODTransitionMultiplier;
        public bool LODTransitionMultiplierAffectsCulling;
        public float DrawLODDebugColors;

        public float AlwaysVisibleScreenSize;
        public float NeverVisibleScreenSize;
        public float ScreenSizeCullingEasing;
        public float MaxVisibilityDistance;

        public NativeList<Color> DebugColors;
    }
    
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(EndSimulationEntityCommandBufferSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [BurstCompile]
    public partial class ScatterLODSystem : SystemBase
    {
        public const int MAX_LOD_COUNT = 8;
        
        static float GetVisibilityPropability(float screenSizeFraction, float alwaysVisibleScreenSize, float neverVisibleScreenSize, float visibilityPower)
        {
            if (math.abs(alwaysVisibleScreenSize - neverVisibleScreenSize) < 0.0001)
            {
                return screenSizeFraction >= alwaysVisibleScreenSize ? 1.0f : 0.0f;
            }

            float visibilityFrac = math.unlerp(neverVisibleScreenSize, alwaysVisibleScreenSize, screenSizeFraction);
            return math.pow(visibilityFrac, visibilityPower);
        }

        static bool ShouldBeVisible(bool hasImportanceData, float density, float probability)
        {
            if (!hasImportanceData)
            {
                return probability > 0;
            }

            if (probability >= 1.0f) return true;

            return density < probability;
        }
        
        [BurstCompile]
        private struct ProcessInstancesWithLODGroups : IJobChunk
        {
            public float3 ComparePosition;
            public float DistanceScale;
            public float LODScale;
            public int LODOffset;
            public bool ScaleAffectsCullingDistance;
            public float AlwaysVisibleScreenSize;
            public float NeverVisibleScreenSize;
            public float VisibilityLerpPower;
            public float MaxVisibilityDistance;
            
            [ReadOnly]
            public ComponentTypeHandle<WorldRenderBounds> WorldRenderBoundsType;
            [ReadOnly]
            public ComponentTypeHandle<ScatterLODMeshMaterialIndices> MeshIndicesType;
            [ReadOnly]
            public ComponentTypeHandle<ScatterLODDistances> LODDistancesType;
            [ReadOnly]
            public ComponentTypeHandle<ScatteredInstanceImportanceData> ImportanceDataType;
            public ComponentTypeHandle<MaterialMeshInfo> MaterialMeshInfoType;
            

            
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var worldBoundsArray = chunk.GetNativeArray(ref WorldRenderBoundsType);
                var lodDistancesArray = chunk.GetNativeArray(ref LODDistancesType);
                var meshIndicesArray = chunk.GetNativeArray(ref MeshIndicesType);
                var materialMeshInfoArray = chunk.GetNativeArray(ref MaterialMeshInfoType);

                bool hasImportanceData = chunk.Has<ScatteredInstanceImportanceData>();
                var importanceData = hasImportanceData ? chunk.GetNativeArray(ref ImportanceDataType) : default;
                bool ignoreDistance = chunk.Has<IgnoreMaxDistanceCullingTag>();
                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    WorldRenderBounds worldBounds = worldBoundsArray[i];
                    float distanceToCamera = math.length(ComparePosition - worldBounds.Value.Center);
                    bool hiddenByDistance = (MaxVisibilityDistance <= distanceToCamera) && !ignoreDistance;
                    
                    int meshIndex = 0;
                    int materialIndex = 0;

                    if (!hiddenByDistance)
                    {
                        float cameraPlaneSizeAtObject = distanceToCamera * DistanceScale;

                        float modelSize = worldBounds.Value.Size.x * 0.3f + worldBounds.Value.Size.y * 0.4f + worldBounds.Value.Size.z * 0.3f;
                        float screenSizeFraction = modelSize / cameraPlaneSizeAtObject;
                        float visibilityFrac = GetVisibilityPropability(screenSizeFraction, AlwaysVisibleScreenSize, NeverVisibleScreenSize, VisibilityLerpPower);
                        bool shouldBeVisible = ShouldBeVisible(hasImportanceData, hasImportanceData ? importanceData[i].RelativeDensity : 0.5f, visibilityFrac);

                        if (shouldBeVisible)
                        {
                            var meshIndices = meshIndicesArray[i];
                            var lodDistances = lodDistancesArray[i];

                            int lod;
                            if (ScaleAffectsCullingDistance)
                            {
                                lod = GetLODIndex(LODOffset, cameraPlaneSizeAtObject * LODScale, lodDistances);
                            }
                            else
                            {
                                lod = GetLODIndexWithoutScaledCulling(LODOffset, cameraPlaneSizeAtObject, LODScale, lodDistances);
                            }
                            
                            if (lod != -1)
                            {
                                materialIndex = meshIndices.GetMaterialIndex(lod);
                                meshIndex = meshIndices.GetMeshIndex(lod);
                            }
                        }
                    }
                   

                    var matMeshInfo = materialMeshInfoArray[i];
                    if (matMeshInfo.Mesh != meshIndex || matMeshInfo.Material != materialIndex)
                    {
                        matMeshInfo.Mesh = meshIndex;
                        matMeshInfo.Material = materialIndex;
                        materialMeshInfoArray[i] = matMeshInfo;
                    }
                    
                }
            }
        }
        
        [BurstCompile]
        private struct ProcessInstancesWithoutLODGroups : IJobChunk
        {
            public float3 ComparePosition;
            public float DistanceScale;
            public float AlwaysVisibleScreenSize;
            public float NeverVisibleScreenSize;
            public float VisibilityLerpPower;
            public float MaxVisibilityDistance;
            [ReadOnly]
            public ComponentTypeHandle<WorldRenderBounds> WorldRenderBoundsType;
            [ReadOnly]
            public ComponentTypeHandle<ScatterMeshMaterialIndices> MeshIndicesType;
            [ReadOnly]
            public ComponentTypeHandle<ScatteredInstanceImportanceData> ImportanceDataType;
            public ComponentTypeHandle<MaterialMeshInfo> MaterialMeshInfoType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var worldBoundsArray = chunk.GetNativeArray(ref WorldRenderBoundsType);
                var meshIndicesArray = chunk.GetNativeArray(ref MeshIndicesType);
                var materialMeshInfoArray = chunk.GetNativeArray(ref MaterialMeshInfoType);

                bool hasImportanceData = chunk.Has<ScatteredInstanceImportanceData>();
                bool ignoreDistance = chunk.Has<IgnoreMaxDistanceCullingTag>();
                var importanceData = hasImportanceData ? chunk.GetNativeArray(ref ImportanceDataType) : default;

                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    WorldRenderBounds worldBounds = worldBoundsArray[i];
                    float distanceToCamera = math.length(ComparePosition - worldBounds.Value.Center);
                    bool hiddenByDistance = (MaxVisibilityDistance <= distanceToCamera) && !ignoreDistance;
                    
                    int meshIndex = 0;
                    int materialIndex = 0;
                    
                    if (!hiddenByDistance)
                    {
                        float cameraPlaneSizeAtObject = distanceToCamera * DistanceScale;
                        float modelSize = math.max(worldBounds.Value.Size.z, math.max(worldBounds.Value.Size.x, worldBounds.Value.Size.y));
                        float screenSizeFraction = modelSize / cameraPlaneSizeAtObject;
                        float visibilityFrac = GetVisibilityPropability(screenSizeFraction, AlwaysVisibleScreenSize, NeverVisibleScreenSize, VisibilityLerpPower);
                        bool shouldBeVisible = ShouldBeVisible(hasImportanceData, hasImportanceData ? importanceData[i].RelativeDensity : 0.5f, visibilityFrac);

                        if (shouldBeVisible)
                        {
                            var meshMatIndices = meshIndicesArray[i];
                            meshIndex = meshMatIndices.meshIndex;
                            materialIndex = meshMatIndices.matIndex;
                        }
                    }
                    

                    var matMeshInfo = materialMeshInfoArray[i];
                    if (matMeshInfo.Mesh != meshIndex || matMeshInfo.Material != materialIndex)
                    {
                        matMeshInfo.Mesh = meshIndex;
                        matMeshInfo.Material = materialIndex;
                        materialMeshInfoArray[i] = matMeshInfo;
                    }
                    
                }
            }
        }
        
        
        [BurstCompile]
        private struct AssignDebugColor : IJobChunk
        {
            public float3 ComparePosition;
            public float DistanceScale;
            public float LODScale;
            public int LODOffset;
            public bool ScaleAffectsCullingDistance;
            public bool DisableDebugColor;
            [ReadOnly]
            public ComponentTypeHandle<WorldRenderBounds> RenderBoundsType;
            [ReadOnly]
            public ComponentTypeHandle<ScatterLODDistances> LODDistancesType;
            public ComponentTypeHandle<ScatterLODDebugColor> DebugColorsType;
            [ReadOnly]
            public NativeList<Color> DebugColors;

            Color GetDebugColor(int lod)
            {
                if (!DebugColors.IsCreated)
                {
                    return Color.gray;
                }
                
                int colorIndex = lod % DebugColors.Length;
                return DebugColors[colorIndex];
            }

            float PackColor(in Color32 col)
            {
                float colorPacked;
                unsafe
                {
                    fixed(Color32* ptr = &col)
                        UnsafeUtility.MemCpy(&colorPacked, ptr, UnsafeUtility.SizeOf<float>());
                }

                return colorPacked;

            }
            
            float GetDebugColorPacked(int lod)
            {
                Color col = GetDebugColor(lod);
                //invert (shader will undo inversion, done to make 0 "neutral"
                col.r = 1.0f - col.r;
                col.g = 1.0f - col.g;
                col.b = 1.0f - col.b;
                col.a = 0.0f;
                Color32 col32 = col;

                return PackColor(col32);
            }
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var worldBoundsArray = chunk.GetNativeArray(ref RenderBoundsType);
                var lodDistancesArray = chunk.GetNativeArray(ref LODDistancesType);
                var debugColorsArray = chunk.GetNativeArray(ref DebugColorsType);

                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    float dist = math.length(ComparePosition - worldBoundsArray[i].Value.Center) * DistanceScale;
                    
                    int lod;
                    if (ScaleAffectsCullingDistance)
                    {
                        lod = GetLODIndex(LODOffset, dist * LODScale, lodDistancesArray[i]);
                    }
                    else
                    {
                        lod = GetLODIndexWithoutScaledCulling(LODOffset, dist, LODScale, lodDistancesArray[i]);
                    }

                    if (lod != -1)
                    {
                        debugColorsArray[i] = new ScatterLODDebugColor()
                        {
                            ColorPacked = DisableDebugColor ? PackColor(Color.black) : GetDebugColorPacked(lod)
                        };
                    }
                    
                }
            }
        }
        
        
        [BurstCompile]
        private struct ToggleShadowCasters : IJobChunk
        {
            public float3 ComparePosition;
            public float ShadowCasterScreenSize;
            public float ShadowCasterScreenSizeVariation;
            public float DistanceScale;
            public float SizeRangeInterp;
            public int MaxChunksToProcess;
            [NativeDisableUnsafePtrRestriction]
            public UnsafeAtomicCounter32 ShadowCasterCounter;

            [ReadOnly]
            public SharedComponentTypeHandle<RenderFilterSettings> RenderFilterType;
            [ReadOnly]
            public SharedComponentTypeHandle<ScatterPointCloudTileInfo> InstanceTileInfoType;
            [ReadOnly]
            public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBoundsType;
            [NativeDisableParallelForRestriction]
            public NativeArray<ArchetypeChunk> ChunksToProcess;

            [ReadOnly] 
            public NativeArray<float4> FrustrumPlanes;

            public bool CullShadowsOutsideOfFrustrum; 

            bool ShouldCastShadows(float rand, float approximateSize)
            {
                float sizeMin = ShadowCasterScreenSize - ShadowCasterScreenSizeVariation;
                float sizeMax = ShadowCasterScreenSize + ShadowCasterScreenSizeVariation;

                float sizeToCompare = math.lerp(math.max(sizeMin, 0), sizeMax, rand);
                return sizeToCompare < approximateSize;
            }

            bool ShouldCastShadows(AABB aabb)
            {
                var frustrumIntersectionRes = Unity.Rendering.FrustumPlanes.Intersect(FrustrumPlanes, aabb);
                return frustrumIntersectionRes != Unity.Rendering.FrustumPlanes.IntersectResult.Out;

            }
            
            float3 ClosestPoint(float3 p, AABB aabb)
            {
                return math.min(math.max(p, aabb.Center - aabb.Extents), aabb.Center + aabb.Extents);
            }
            
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                ChunkWorldRenderBounds renderBounds = chunk.GetChunkComponentData(ref ChunkWorldRenderBoundsType);
                ScatterPointCloudTileInfo sizeRange = chunk.GetSharedComponent(InstanceTileInfoType);

                float distance = math.length(ComparePosition - ClosestPoint(ComparePosition, renderBounds.Value));
                float distanceScaled = math.max(distance * DistanceScale, 0.00001f);
                float approximateSize = math.lerp(sizeRange.SizeMinMax.x, sizeRange.SizeMinMax.y, SizeRangeInterp);
                
                Random randomGen = new Random((uint)unfilteredChunkIndex + 1);
                float rand = randomGen.NextFloat();

                
                bool shouldCastShadow = ShouldCastShadows(rand, approximateSize/distanceScaled);

                if (CullShadowsOutsideOfFrustrum)
                {
                    shouldCastShadow = shouldCastShadow && ShouldCastShadows(renderBounds.Value);
                }
                
                RenderFilterSettings renderFilter = chunk.GetSharedComponent(RenderFilterType);
                bool isCastingShadow = renderFilter.ShadowCastingMode != ShadowCastingMode.Off;


                if (isCastingShadow != shouldCastShadow)
                {
                    var index = ShadowCasterCounter.Add(1);
                    if (index < MaxChunksToProcess)
                    {
                        ChunksToProcess[index] = chunk;
                    }
                }

            }
        }
        
        private ComponentTypeHandle<WorldRenderBounds> m_RenderBoundsType;
        private ComponentTypeHandle<ScatterLODMeshMaterialIndices> m_LODMeshMaterialIndicesType;
        private ComponentTypeHandle<ScatterMeshMaterialIndices> m_MeshMaterialIndicesType;
        
        private ComponentTypeHandle<ScatterLODDistances> m_LODDistancesType;
        private ComponentTypeHandle<MaterialMeshInfo> m_MaterialMeshInfo;
        private ComponentTypeHandle<ScatteredInstanceImportanceData> m_InstanceImportanceDataType;
        private ComponentTypeHandle<ScatterLODDebugColor> m_DebugColorType;
        
        private ComponentTypeHandle<ChunkWorldRenderBounds> m_ChunkWorldBoundsType;
        private SharedComponentTypeHandle<RenderFilterSettings> m_RenderFilterSettingsType;
        private SharedComponentTypeHandle<ScatterPointCloudTileInfo> m_TileInfoType;
        private EntityTypeHandle m_EntityType;

        private EntityQuery m_ScatteredInstancesWithLODsQuery;
        private EntityQuery m_ScatteredInstancesWithoutLODsQuery;
        private EntityQuery m_ScatteredInstancesWithLODsDebugColorQuery;
        private EntityQuery m_ScatteredInstancesToggleShadowsQuery;
        
        private UnsafeAtomicCounter32 m_ShadowCasterChangesCounter;

        private float3 m_CameraPosition;
        private float m_CamTanFoV;
        private bool m_DebugEnabled;
        private NativeArray<float4> m_CameraFrustrumPlanes;
        private NativeArray<float4> m_OffsetCameraFrustrumPlanes;
        
        protected override void OnCreate()
        {
            OnCreate_Prefabs();
            
            m_ScatteredInstancesWithLODsQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterLODMeshMaterialIndices>(), ComponentType.ReadOnly<ScatterLODDistances>(), 
                ComponentType.ReadOnly<WorldRenderBounds>(), ComponentType.ReadWrite<MaterialMeshInfo>()
            );
            
            m_ScatteredInstancesWithoutLODsQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterMeshMaterialIndices>(), ComponentType.ReadOnly<WorldRenderBounds>(), 
                ComponentType.ReadWrite<MaterialMeshInfo>()
            );

            m_ScatteredInstancesWithLODsDebugColorQuery = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ScatterLODDistances>(), ComponentType.ReadOnly<WorldRenderBounds>(),
                ComponentType.ReadWrite<ScatterLODDebugColor>());
            
            m_ScatteredInstancesToggleShadowsQuery = EntityManager.CreateEntityQuery(
                ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(), ComponentType.ReadOnly<RenderFilterSettings>(), ComponentType.ReadOnly<ScatterPointCloudTileInfo>());

            m_RenderBoundsType = EntityManager.GetComponentTypeHandle<WorldRenderBounds>(true);
            m_LODMeshMaterialIndicesType = EntityManager.GetComponentTypeHandle<ScatterLODMeshMaterialIndices>(true);
            m_MeshMaterialIndicesType = EntityManager.GetComponentTypeHandle<ScatterMeshMaterialIndices>(true);
            m_LODDistancesType = EntityManager.GetComponentTypeHandle<ScatterLODDistances>(true);
            m_MaterialMeshInfo = EntityManager.GetComponentTypeHandle<MaterialMeshInfo>(false);
            m_InstanceImportanceDataType = EntityManager.GetComponentTypeHandle<ScatteredInstanceImportanceData>(true);
            
            m_DebugColorType = EntityManager.GetComponentTypeHandle<ScatterLODDebugColor>(false);
            
            m_RenderFilterSettingsType = EntityManager.GetSharedComponentTypeHandle<RenderFilterSettings>();
            m_TileInfoType = EntityManager.GetSharedComponentTypeHandle<ScatterPointCloudTileInfo>();
            m_ChunkWorldBoundsType = EntityManager.GetComponentTypeHandle<ChunkWorldRenderBounds>(true);
            m_EntityType = EntityManager.GetEntityTypeHandle();
            
            unsafe
            {
                m_ShadowCasterChangesCounter = new UnsafeAtomicCounter32(UnsafeUtility.Malloc(sizeof(int), 1, Allocator.Persistent));
            }
            
            

            m_CameraPosition = float3.zero;
            m_CamTanFoV = 1.0f;
            m_CameraFrustrumPlanes = new NativeArray<float4>(6, Allocator.Persistent);
            m_OffsetCameraFrustrumPlanes = new NativeArray<float4>(6, Allocator.Persistent);
            
            EntityManager.AddComponent<ScatterLODSystemConfigData>(SystemHandle);
            ref var config = ref EntityManager.GetComponentDataRW<ScatterLODSystemConfigData>(SystemHandle).ValueRW;
            config.ShadowCullScreenSize = 0.05f;
            config.ShadowCullScreenSizeVariation = 0.02f;
            config.ShadowCullOutsideCamera = false;
            config.ShadowCullFrustrumPlaneMargin = 0.0f;
            config.MaxShadowCasterChunkChangesPerFrame = 100000;
            config.DebugColors = new NativeList<Color>(8, Allocator.Persistent);
            config.AlwaysVisibleScreenSize = 0.0f;
            config.NeverVisibleScreenSize = 0.0f;
            config.MaxVisibilityDistance = 999999;
            config.LODTransitionMultiplierAffectsCulling = false;
            

            m_DebugEnabled = false;
        }

        protected override void OnDestroy()
        {
            OnDestroy_Prefabs();
            m_ScatteredInstancesWithLODsQuery.Dispose();
            m_ScatteredInstancesWithoutLODsQuery.Dispose();
            m_ScatteredInstancesWithLODsDebugColorQuery.Dispose();
            m_ScatteredInstancesToggleShadowsQuery.Dispose();

            m_CameraFrustrumPlanes.Dispose();
            m_OffsetCameraFrustrumPlanes.Dispose();
            
            ref var config = ref EntityManager.GetComponentDataRW<ScatterLODSystemConfigData>(SystemHandle).ValueRW;
            if (config.DebugColors.IsCreated)
            {
                config.DebugColors.Dispose();
            }
            
            unsafe
            {
                UnsafeUtility.Free(m_ShadowCasterChangesCounter.Counter, Allocator.Persistent);
            }
        }
        
        void UpdateCameraState()
        {
            var cts = World.GetExistingSystem<CameraTrackingSystem>();
            if (cts != SystemHandle.Null)
            {
                var cData = EntityManager.GetComponentData<CameraTrackingSystem.CameraTrackingData>(cts);
                m_CameraPosition = cData.CameraPosition;
                m_CamTanFoV = cData.TanFOV;
                m_CameraFrustrumPlanes.CopyFrom(cData.CameraFrustrumPlanes);
            }
        }

        void ApplyCameraPlaneMargin(float offset)
        {
            for (int i = 0; i < m_OffsetCameraFrustrumPlanes.Length; ++i)
            {
                float4 plane = m_CameraFrustrumPlanes[i];
                plane.w += offset;
                m_OffsetCameraFrustrumPlanes[i] = plane;
            }
        }

        protected override void OnUpdate()
        {
            m_RenderBoundsType.Update(this);
            m_LODMeshMaterialIndicesType.Update(this);
            m_MeshMaterialIndicesType.Update(this);
            m_LODDistancesType.Update(this);
            m_MaterialMeshInfo.Update(this);
            m_DebugColorType.Update(this);
            m_EntityType.Update(this);
            m_ChunkWorldBoundsType.Update(this);
            m_RenderFilterSettingsType.Update(this);
            m_TileInfoType.Update(this);
            m_InstanceImportanceDataType.Update(this);
            
            m_ShadowCasterChangesCounter.Reset();
            
            var config = EntityManager.GetComponentData<ScatterLODSystemConfigData>(SystemHandle);
            
            UpdateCameraState();
            ApplyCameraPlaneMargin(config.ShadowCullFrustrumPlaneMargin);
            
            JobHandle jobHandle = Dependency;
            float distanceScale =  m_CamTanFoV / QualitySettings.lodBias;
            NativeArray<ArchetypeChunk> chunksToProcess = new NativeArray<ArchetypeChunk>(config.MaxShadowCasterChunkChangesPerFrame, Allocator.TempJob);
            //select LOD
            {

                jobHandle = new ProcessInstancesWithLODGroups()
                {
                    ComparePosition = m_CameraPosition,
                    DistanceScale = distanceScale,
                    LODScale = config.LODTransitionMultiplier,
                    LODOffset = config.LODTransitionConstantOffset,
                    AlwaysVisibleScreenSize = config.AlwaysVisibleScreenSize,
                    NeverVisibleScreenSize = config.NeverVisibleScreenSize,
                    VisibilityLerpPower = config.ScreenSizeCullingEasing,
                    WorldRenderBoundsType = m_RenderBoundsType,
                    MeshIndicesType = m_LODMeshMaterialIndicesType,
                    LODDistancesType = m_LODDistancesType,
                    MaterialMeshInfoType = m_MaterialMeshInfo,
                    ScaleAffectsCullingDistance = config.LODTransitionMultiplierAffectsCulling,
                    ImportanceDataType = m_InstanceImportanceDataType,
                    MaxVisibilityDistance = config.MaxVisibilityDistance
                }.ScheduleParallel(m_ScatteredInstancesWithLODsQuery, jobHandle);
                
                jobHandle = new ProcessInstancesWithoutLODGroups()
                {
                    ComparePosition = m_CameraPosition,
                    DistanceScale = distanceScale,
                    AlwaysVisibleScreenSize = config.AlwaysVisibleScreenSize,
                    NeverVisibleScreenSize = config.NeverVisibleScreenSize,
                    VisibilityLerpPower = config.ScreenSizeCullingEasing,
                    WorldRenderBoundsType = m_RenderBoundsType,
                    MeshIndicesType = m_MeshMaterialIndicesType,
                    MaterialMeshInfoType = m_MaterialMeshInfo,
                    ImportanceDataType = m_InstanceImportanceDataType,
                    MaxVisibilityDistance = config.MaxVisibilityDistance
                }.ScheduleParallel(m_ScatteredInstancesWithoutLODsQuery, jobHandle);
                
                
            }
            
            
            //debug colors
            if (m_DebugEnabled || config.DrawLODDebugColors > 0)
            {
                jobHandle = new AssignDebugColor()
                {
                    DisableDebugColor = m_DebugEnabled && config.DrawLODDebugColors == 0.0f,
                    DebugColors = config.DebugColors,
                    DistanceScale = distanceScale,
                    LODOffset = config.LODTransitionConstantOffset,
                    LODScale = config.LODTransitionMultiplier,
                    ComparePosition = m_CameraPosition,
                    RenderBoundsType = m_RenderBoundsType,
                    DebugColorsType = m_DebugColorType,
                    LODDistancesType = m_LODDistancesType,
                    ScaleAffectsCullingDistance = config.LODTransitionMultiplierAffectsCulling
                }.ScheduleParallel(m_ScatteredInstancesWithLODsDebugColorQuery, jobHandle);

                m_DebugEnabled = config.DrawLODDebugColors > 0.0f;
            }
            Shader.SetGlobalFloat("_ScatterDebugLODIntensity", config.DrawLODDebugColors);

            
            {
                jobHandle = new ToggleShadowCasters()
                {
                    ComparePosition = m_CameraPosition,
                    DistanceScale = distanceScale,
                    InstanceTileInfoType = m_TileInfoType,
                    SizeRangeInterp = config.InstanceSizeRangeInterpolateParam,
                    ChunkWorldRenderBoundsType = m_ChunkWorldBoundsType,
                    RenderFilterType = m_RenderFilterSettingsType,
                    ShadowCasterScreenSize = config.ShadowCullScreenSize,
                    ShadowCasterScreenSizeVariation = config.ShadowCullScreenSizeVariation,
                    ShadowCasterCounter = m_ShadowCasterChangesCounter,
                    MaxChunksToProcess = config.MaxShadowCasterChunkChangesPerFrame,
                    ChunksToProcess = chunksToProcess,
                    FrustrumPlanes = m_OffsetCameraFrustrumPlanes,
                    CullShadowsOutsideOfFrustrum = config.ShadowCullOutsideCamera
                }.ScheduleParallel(m_ScatteredInstancesToggleShadowsQuery, jobHandle);
            }
            
            //have to complete here since we need to actually issue the changes to shadow casters
            jobHandle.Complete();

            int castersFoundToProcess;
            unsafe
            {
                castersFoundToProcess = *m_ShadowCasterChangesCounter.Counter;
            }

            for (int i = 0; i < math.min(chunksToProcess.Length, castersFoundToProcess); ++i)
            {
                ArchetypeChunk chunk = chunksToProcess[i];
                var renderFilterSettings = chunk.GetSharedComponent(m_RenderFilterSettingsType);
                bool shadowsAreOff = renderFilterSettings.ShadowCastingMode == ShadowCastingMode.Off;
                if (shadowsAreOff)
                {
                    renderFilterSettings.ShadowCastingMode = ShadowCastingMode.On;
                }
                else
                {
                    renderFilterSettings.ShadowCastingMode = ShadowCastingMode.Off;
                }
                EntityManager.SetSharedComponent(chunk, renderFilterSettings);
            }

            chunksToProcess.Dispose();
            
            Dependency = jobHandle;


        }

        public static int GetLODIndex(int constantOffset, float distance, ScatterLODDistances distances)
        {
            constantOffset = math.clamp(constantOffset, 0, MAX_LOD_COUNT - 1);
            for (int i = constantOffset; i < MAX_LOD_COUNT; ++i)
            {
                var dist = distances.Distances[i];
                if (distance < dist) return i;
            }

            return -1;
        }
        
        public static int GetLODIndexWithoutScaledCulling(int constantOffset, float distance, float distanceScale, ScatterLODDistances distances)
        {
            int lodScaled;
            int lod;
            
            constantOffset = math.clamp(constantOffset, 0, MAX_LOD_COUNT - 1);
            
            GetLODIndicesWithScaling(constantOffset, distance, distanceScale, distances, out lod, out lodScaled);
            //scaling does not make culling happen earlier
            if (lodScaled > distances.LastLODWithValidEntry)
            {
                if (lod <= distances.LastLODWithValidEntry)
                {
                    lodScaled = distances.LastLODWithValidEntry;
                }
            }
            return lodScaled;
        }
        
        public static void GetLODIndicesWithScaling(int constantOffset, float distance, float distanceScale, ScatterLODDistances distances, out int lodWithoutScale,out int lodWithScale)
        {
            float distanceScaled = distance * distanceScale;
            int lodScaled = -1;
            int lodNonScaled = -1;
            for (int i = constantOffset; i < MAX_LOD_COUNT && lodScaled == -1 || lodNonScaled == -1; ++i)
            {
                var dist = distances.Distances[i];
                if (lodNonScaled == -1 && distance < dist)
                {
                    lodNonScaled = i;
                }
                
                if (lodScaled == -1 && distanceScaled < dist)
                {
                    lodScaled = i;
                }
            }

            lodWithoutScale = lodNonScaled;
            lodWithScale = lodScaled;
        }
        
    }
}