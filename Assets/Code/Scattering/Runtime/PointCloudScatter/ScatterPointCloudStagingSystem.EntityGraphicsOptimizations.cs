using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Assertions;


namespace TimeGhost
{
    public partial class ScatterPointCloudStagingSystem
    {
        //Adapted from Env
        [Flags]
        private enum ComponentOfInterest
        {
            None = 0,
            LocalToWorld = 1 << 0,
            RenderBounds = 1 << 1,
            LODWorldReferencePoint = 1 << 2,
            LODGroupWorldReferencePoint = 1 << 3,
            RootLODWorldReferencePoint = 1 << 4,
            MeshLODComponent = 1 << 5,
            MeshLODGroupComponent = 1 << 6,
            LODRange = 1 << 7,
            RootLODRange = 1 << 8
        }

        private enum TypesToReflect
        {
            LODWorldReferencePoint,
            LODGroupWorldReferencePoint,
            RootLODWorldReferencePoint,
            LODRange,
            RootLODRange,
            BuiltinMaterialPropertyUnity_MatrixPreviousM,

            SkipWorldRenderBoundsUpdate,
            SkipLODRangeUpdate,
            SkipLODWorldReferencePointUpdate,
            SkipLODGroupWorldReferencePointUpdate,
            SkipRootLODWorldReferencePointUpdate,
            SkipBuiltinMaterialPropertyUnity_MatrixPreviousMUpdate
        }

        public struct RequestPreviousMatrixTag : IComponentData
        {
        }

        internal struct RootLODRangeCopy : IComponentData
        {
            public LODRangeCopy LOD;
        }

        internal struct LODRangeCopy : IComponentData
        {
            public float MinDist;
            public float MaxDist;
            public int LODMask;

            public LODRangeCopy(MeshLODGroupComponent lodGroup, int lodMask)
            {
                float minDist = float.MaxValue;
                float maxDist = 0.0F;

                if ((lodMask & 0x01) == 0x01)
                {
                    minDist = 0.0f;
                    maxDist = math.max(maxDist, lodGroup.LODDistances0.x);
                }

                if ((lodMask & 0x02) == 0x02)
                {
                    minDist = math.min(minDist, lodGroup.LODDistances0.x);
                    maxDist = math.max(maxDist, lodGroup.LODDistances0.y);
                }

                if ((lodMask & 0x04) == 0x04)
                {
                    minDist = math.min(minDist, lodGroup.LODDistances0.y);
                    maxDist = math.max(maxDist, lodGroup.LODDistances0.z);
                }

                if ((lodMask & 0x08) == 0x08)
                {
                    minDist = math.min(minDist, lodGroup.LODDistances0.z);
                    maxDist = math.max(maxDist, lodGroup.LODDistances0.w);
                }

                if ((lodMask & 0x10) == 0x10)
                {
                    minDist = math.min(minDist, lodGroup.LODDistances0.w);
                    maxDist = math.max(maxDist, lodGroup.LODDistances1.x);
                }

                if ((lodMask & 0x20) == 0x20)
                {
                    minDist = math.min(minDist, lodGroup.LODDistances1.x);
                    maxDist = math.max(maxDist, lodGroup.LODDistances1.y);
                }

                if ((lodMask & 0x40) == 0x40)
                {
                    minDist = math.min(minDist, lodGroup.LODDistances1.y);
                    maxDist = math.max(maxDist, lodGroup.LODDistances1.z);
                }

                if ((lodMask & 0x80) == 0x80)
                {
                    minDist = math.min(minDist, lodGroup.LODDistances1.z);
                    maxDist = math.max(maxDist, lodGroup.LODDistances1.w);
                }

                MinDist = minDist;
                MaxDist = maxDist;
                LODMask = lodMask;
            }
        }


        //Jobs taken and adapted from entity graphics so we can execute them in staging world. These might be brittle since we are making assumptions about how the entities graphics package which might change in the future
        [BurstCompile]
        internal struct BoundsJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<RenderBounds> RendererBounds;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorld;
            public ComponentTypeHandle<WorldRenderBounds> WorldRenderBounds;
            public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBounds;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // This job is not written to support queries with enableable component types.
                Assert.IsFalse(useEnabledMask);

                var worldBounds = chunk.GetNativeArray(ref WorldRenderBounds);
                var localBounds = chunk.GetNativeArray(ref RendererBounds);
                var localToWorld = chunk.GetNativeArray(ref LocalToWorld);
                MinMaxAABB combined = MinMaxAABB.Empty;
                for (int i = 0; i != localBounds.Length; i++)
                {
                    var transformed = AABB.Transform(localToWorld[i].Value, localBounds[i].Value);

                    worldBounds[i] = new WorldRenderBounds { Value = transformed };
                    combined.Encapsulate(transformed);
                }

                chunk.SetChunkComponentData(ref ChunkWorldRenderBounds, new ChunkWorldRenderBounds { Value = combined });
            }
        }

        [BurstCompile]
        public struct InitializeMatrixPrevious : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorldTypeHandle;
            public DynamicComponentTypeHandle MatrixPreviousTypeHandle;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // This job is not written to support queries with enableable component types.
                Assert.IsFalse(useEnabledMask);

                var chunkLocalToWorld = chunk.GetNativeArray(ref LocalToWorldTypeHandle);
                var chunkMatrixPrevious = chunk.GetDynamicComponentDataArrayReinterpret<float4x4>(ref MatrixPreviousTypeHandle, UnsafeUtility.SizeOf<float4x4>());
                for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; i++)
                {
                    var localToWorld = chunkLocalToWorld[i].Value;
                    // The assumption is made here that if the initial value of the previous matrix is zero that
                    // it needs to be initialized to the localToWorld matrix value. This avoids issues with incorrect
                    // motion vector results on the first frame and entity is rendered.
                    if (chunkMatrixPrevious[i].Equals(float4x4.zero))
                    {
                        chunkMatrixPrevious[i] = localToWorld;
                    }
                }
            }
        }

        [BurstCompile]
        internal struct UpdateLODGroupWorldReferencePointsJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<MeshLODGroupComponent> MeshLODGroupComponent;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorld;
            public DynamicComponentTypeHandle LODGroupWorldReferencePoint;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // This job is not written to support queries with enableable component types.
                Assert.IsFalse(useEnabledMask);

                var meshLODGroupComponent = chunk.GetNativeArray(ref MeshLODGroupComponent);
                var localToWorld = chunk.GetNativeArray(ref LocalToWorld);
                var lodGroupWorldReferencePoint = chunk.GetDynamicComponentDataArrayReinterpret<float3>(ref LODGroupWorldReferencePoint, UnsafeUtility.SizeOf<float3>());
                var instanceCount = chunk.Count;

                for (int i = 0; i < instanceCount; i++)
                {
                    lodGroupWorldReferencePoint[i] = math.transform(localToWorld[i].Value, meshLODGroupComponent[i].LocalReferencePoint);
                }
            }
        }

        [BurstCompile]
        internal struct UpdateLODWorldReferencePointsJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<MeshLODComponent> MeshLODComponent;
            public EntityStorageInfoLookup EntityStorageInfoLookup;
            public DynamicComponentTypeHandle RootLODWorldReferencePoint;
            public DynamicComponentTypeHandle LODWorldReferencePoint;
            public DynamicComponentTypeHandle LODGroupWorldReferencePoint;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // This job is not written to support queries with enableable component types.
                Assert.IsFalse(useEnabledMask);
                //LODGroupWorldReferencePoint
                var rootLODWorldReferencePoint = chunk.GetDynamicComponentDataArrayReinterpret<float3>(ref RootLODWorldReferencePoint, UnsafeUtility.SizeOf<float3>());
                var lodWorldReferencePoint = chunk.GetDynamicComponentDataArrayReinterpret<float3>(ref LODWorldReferencePoint, UnsafeUtility.SizeOf<float3>());
                var meshLods = chunk.GetNativeArray(ref MeshLODComponent);
                var instanceCount = chunk.Count;

                for (int i = 0; i < instanceCount; i++)
                {
                    var meshLod = meshLods[i];
                    var lodGroupEntity = meshLod.Group;
                    float3 lodGroupWorldReferencePoint;
                    {
                        var lodGroupEntityStorageInfo = EntityStorageInfoLookup[lodGroupEntity];
                        var lodGroupEntityArray =
                            lodGroupEntityStorageInfo.Chunk.GetDynamicComponentDataArrayReinterpret<float3>(
                                ref LODGroupWorldReferencePoint, UnsafeUtility.SizeOf<float3>());

                        lodGroupWorldReferencePoint = lodGroupEntityArray[lodGroupEntityStorageInfo.IndexInChunk];
                    }


                    lodWorldReferencePoint[i] = lodGroupWorldReferencePoint;
                    var parentGroupEntity = meshLod.ParentGroup;

                    float3 rootPoint;

                    if (parentGroupEntity == Entity.Null)
                    {
                        rootPoint = new float3(0, 0, 0);
                    }
                    else
                    {
                        float3 parentGroupWorldReferencePoint;
                        {
                            var parentGroupEntityStorageInfo = EntityStorageInfoLookup[parentGroupEntity];
                            var parentGroupEntityArray =
                                parentGroupEntityStorageInfo.Chunk.GetDynamicComponentDataArrayReinterpret<float3>(
                                    ref LODGroupWorldReferencePoint, UnsafeUtility.SizeOf<float3>());

                            parentGroupWorldReferencePoint = parentGroupEntityArray[parentGroupEntityStorageInfo.IndexInChunk];
                        }

                        rootPoint = parentGroupWorldReferencePoint;
                    }

                    rootLODWorldReferencePoint[i] = rootPoint;
                }
            }
        }

        private EntityQuery m_InitializeMatrixPreviousQuery;
        private EntityQuery m_UpdateChunkWorldRendererBoundsQuery;
        private EntityQuery m_UpdateLODGroupReferencePointsQuery;
        private EntityQuery m_UpdateLODReferencePointsQuery;

        private ComponentTypeHandle<ChunkWorldRenderBounds> m_ChunkWorldRenderBoundsTypeHandleRW;
        private ComponentTypeHandle<LocalToWorld> m_LocalToWorldTypeHandleRO;
        private ComponentTypeHandle<MeshLODComponent> m_MeshLODComponentTypeHandleRO;
        private ComponentTypeHandle<MeshLODGroupComponent> m_MeshLODGroupComponentTypeHandleRO;
        private ComponentTypeHandle<RenderBounds> m_RenderBoundsTypeHandleRO;
        private ComponentTypeHandle<WorldRenderBounds> m_WorldRenderBoundsTypeHandleRW;
        private EntityStorageInfoLookup m_StorageLookup;

        private EntityQuery m_PrefabsPendingOptimization;

        private ComponentTypeReflectionUtility m_ReflectionUtility;

        void OnCreate_EntityGraphicsOptimizations()
        {
            m_ReflectionUtility.Init(EntityManager, typeof(EntitiesGraphicsSystem).Assembly, typeof(TypesToReflect));

            //We assume the contents of types that are not visible to us, do some sanity checks
            Assert.IsTrue(UnsafeUtility.SizeOf(m_ReflectionUtility.GetType((int)TypesToReflect.LODWorldReferencePoint)) ==
                          UnsafeUtility.SizeOf<float3>());
            Assert.IsTrue(UnsafeUtility.SizeOf(m_ReflectionUtility.GetType((int)TypesToReflect.LODGroupWorldReferencePoint)) ==
                          UnsafeUtility.SizeOf<float3>());
            Assert.IsTrue(UnsafeUtility.SizeOf(m_ReflectionUtility.GetType((int)TypesToReflect.RootLODWorldReferencePoint)) ==
                          UnsafeUtility.SizeOf<float3>());
            Assert.IsTrue(UnsafeUtility.SizeOf(m_ReflectionUtility.GetType((int)TypesToReflect.BuiltinMaterialPropertyUnity_MatrixPreviousM)) ==
                          UnsafeUtility.SizeOf<float4x4>());

            m_PrefabsPendingOptimization =
                EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabComponentOptimizationPending>(), ComponentType.ReadOnly<Prefab>());

            m_ChunkWorldRenderBoundsTypeHandleRW = GetComponentTypeHandle<ChunkWorldRenderBounds>();
            m_LocalToWorldTypeHandleRO = GetComponentTypeHandle<LocalToWorld>(true);
            m_MeshLODComponentTypeHandleRO = GetComponentTypeHandle<MeshLODComponent>(true);
            m_MeshLODGroupComponentTypeHandleRO = GetComponentTypeHandle<MeshLODGroupComponent>(true);
            m_RenderBoundsTypeHandleRO = GetComponentTypeHandle<RenderBounds>(true);
            m_WorldRenderBoundsTypeHandleRW = GetComponentTypeHandle<WorldRenderBounds>();
            m_StorageLookup = GetEntityStorageInfoLookup();
            {
                m_InitializeMatrixPreviousQuery = EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GeneratedScatterInstanceTag>(), ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadWrite(
                        GetManagedType(TypesToReflect.BuiltinMaterialPropertyUnity_MatrixPreviousM)));

                m_UpdateChunkWorldRendererBoundsQuery = EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GeneratedScatterInstanceTag>(), ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<RenderBounds>(),
                    ComponentType.ReadWrite<WorldRenderBounds>(),
                    ComponentType.ChunkComponent<ChunkWorldRenderBounds>());

                m_UpdateLODGroupReferencePointsQuery = EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GeneratedScatterInstanceTag>(), ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<MeshLODGroupComponent>(),
                    ComponentType.ReadWrite(GetManagedType(TypesToReflect.LODGroupWorldReferencePoint)));

                m_UpdateLODReferencePointsQuery = EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<GeneratedScatterInstanceTag>(), ComponentType.ReadOnly<LocalToWorld>(), ComponentType.ReadOnly<MeshLODComponent>(),
                    ComponentType.ReadWrite(GetManagedType(TypesToReflect.RootLODWorldReferencePoint)), ComponentType.ReadWrite(GetManagedType(TypesToReflect.LODWorldReferencePoint)));
            }
        }

        void OnDestroy_EntityGraphicsOptimizations()
        {
            m_PrefabsPendingOptimization.Dispose();
            m_ReflectionUtility.Cleanup();
        }


        void HandlePrefabOptimizations()
        {
            var pendingPrefabs = m_PrefabsPendingOptimization.ToEntityArray(Allocator.Temp);
            var optimizationSettings = m_PrefabsPendingOptimization.ToComponentDataArray<PrefabComponentOptimizationPending>(Allocator.Temp);
            var entManager = EntityManager;
            for(int i = 0; i < pendingPrefabs.Length; ++i)
            {
                OptimizeComponents(pendingPrefabs[i], optimizationSettings[i].AddChunkComponents, ref entManager);
            }

            EntityManager.RemoveComponent<PrefabComponentOptimizationPending>(pendingPrefabs);

            pendingPrefabs.Dispose();
            optimizationSettings.Dispose();
        }

        public JobHandle ScheduleOptimizationJobsForGraphics(JobHandle deps)
        {
            UpdateDynamicTypeHandles();

            m_ChunkWorldRenderBoundsTypeHandleRW.Update(this);
            m_LocalToWorldTypeHandleRO.Update(this);
            m_MeshLODComponentTypeHandleRO.Update(this);
            m_MeshLODGroupComponentTypeHandleRO.Update(this);
            m_RenderBoundsTypeHandleRO.Update(this);
            m_WorldRenderBoundsTypeHandleRW.Update(this);
            m_StorageLookup.Update(this);

            var updateBounds = new BoundsJob()
            {
                RendererBounds = m_RenderBoundsTypeHandleRO,
                LocalToWorld = m_LocalToWorldTypeHandleRO,
                WorldRenderBounds = m_WorldRenderBoundsTypeHandleRW,
                ChunkWorldRenderBounds = m_ChunkWorldRenderBoundsTypeHandleRW
            }.ScheduleParallel(m_UpdateChunkWorldRendererBoundsQuery, deps);

            var initializePrevMatrix = new InitializeMatrixPrevious()
            {
                LocalToWorldTypeHandle = m_LocalToWorldTypeHandleRO,
                MatrixPreviousTypeHandle = GetDynamicType(TypesToReflect.BuiltinMaterialPropertyUnity_MatrixPreviousM)
            }.ScheduleParallel(m_InitializeMatrixPreviousQuery, deps);

            var updateLODGroupWorldReferencePoints = new UpdateLODGroupWorldReferencePointsJob()
            {
                MeshLODGroupComponent = m_MeshLODGroupComponentTypeHandleRO,
                LocalToWorld = m_LocalToWorldTypeHandleRO,
                LODGroupWorldReferencePoint = GetDynamicType(TypesToReflect.LODGroupWorldReferencePoint)
            }.ScheduleParallel(m_UpdateLODGroupReferencePointsQuery, deps);

            var updateLODWorldReferencePoints = new UpdateLODWorldReferencePointsJob()
            {
                MeshLODComponent = m_MeshLODComponentTypeHandleRO,
                EntityStorageInfoLookup = m_StorageLookup,
                RootLODWorldReferencePoint = GetDynamicType(TypesToReflect.RootLODWorldReferencePoint),
                LODGroupWorldReferencePoint = GetDynamicType(TypesToReflect.LODGroupWorldReferencePoint),
                LODWorldReferencePoint = GetDynamicType(TypesToReflect.LODWorldReferencePoint),
            }.ScheduleParallel(m_UpdateLODReferencePointsQuery, JobHandle.CombineDependencies(updateLODGroupWorldReferencePoints, deps));

            return JobHandle.CombineDependencies(updateBounds, updateLODWorldReferencePoints, initializePrevMatrix);
        }


        ref Type GetManagedType(TypesToReflect t)
        {
            return ref m_ReflectionUtility.GetType((int)t);
        }

        ref DynamicComponentTypeHandle GetDynamicType(TypesToReflect t)
        {
            return ref m_ReflectionUtility.GetComponentHandle((int)t);
        }

        void UpdateDynamicTypeHandles()
        {
            m_ReflectionUtility.UpdateDynamicTypeHandles(ref CheckedStateRef);
        }

        private void OptimizeComponents(Entity entity, bool addChunkComponents, ref EntityManager entityManger)
        {
            UpdateDynamicTypeHandles();

            var archetype = entityManger.GetChunk(entity);
            ComponentOfInterest hasComponentTypes = ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has<LocalToWorld>() ? ComponentOfInterest.LocalToWorld : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has<RenderBounds>()
                ? ComponentOfInterest.RenderBounds
                : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has(ref GetDynamicType(TypesToReflect.LODWorldReferencePoint))
                ? ComponentOfInterest.LODWorldReferencePoint
                : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has(ref GetDynamicType(TypesToReflect.LODGroupWorldReferencePoint))
                ? ComponentOfInterest.LODGroupWorldReferencePoint
                : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has(ref GetDynamicType(TypesToReflect.RootLODWorldReferencePoint))
                ? ComponentOfInterest.RootLODWorldReferencePoint
                : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has<MeshLODComponent>()
                ? ComponentOfInterest.MeshLODComponent
                : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has<MeshLODGroupComponent>()
                ? ComponentOfInterest.MeshLODGroupComponent
                : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has(ref GetDynamicType(TypesToReflect.LODRange))
                ? ComponentOfInterest.LODRange
                : ComponentOfInterest.None;
            hasComponentTypes |= archetype.Has(ref GetDynamicType(TypesToReflect.RootLODRange))
                ? ComponentOfInterest.RootLODRange
                : ComponentOfInterest.None;

            var componentTypeListToAdd = new FixedList128Bytes<ComponentType>();
            if ((hasComponentTypes & (ComponentOfInterest.MeshLODComponent | ComponentOfInterest.RootLODRange)) ==
                ComponentOfInterest.MeshLODComponent)
            {
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.RootLODRange)));
                hasComponentTypes |= ComponentOfInterest.RootLODRange;
            }

            if ((hasComponentTypes &
                 (ComponentOfInterest.MeshLODComponent | ComponentOfInterest.LODWorldReferencePoint)) ==
                ComponentOfInterest.MeshLODComponent)
            {
                componentTypeListToAdd.Add(
                    ComponentType.ReadOnly(GetManagedType(TypesToReflect.LODWorldReferencePoint)));
                hasComponentTypes |= ComponentOfInterest.LODWorldReferencePoint;
            }

            if ((hasComponentTypes & (ComponentOfInterest.MeshLODComponent | ComponentOfInterest.RootLODWorldReferencePoint)) == ComponentOfInterest.MeshLODComponent)
            {
                componentTypeListToAdd.Add(
                    ComponentType.ReadOnly(GetManagedType(TypesToReflect.RootLODWorldReferencePoint)));
                hasComponentTypes |= ComponentOfInterest.RootLODWorldReferencePoint;
            }

            if ((hasComponentTypes & (ComponentOfInterest.MeshLODComponent | ComponentOfInterest.LODRange)) == ComponentOfInterest.MeshLODComponent)
            {
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.LODRange)));
                hasComponentTypes |= ComponentOfInterest.LODRange;
            }

            if ((hasComponentTypes & (ComponentOfInterest.MeshLODGroupComponent | ComponentOfInterest.LODGroupWorldReferencePoint)) == ComponentOfInterest.MeshLODGroupComponent)
            {
                componentTypeListToAdd.Add(
                    ComponentType.ReadOnly(GetManagedType(TypesToReflect.LODGroupWorldReferencePoint)));
                hasComponentTypes |= ComponentOfInterest.LODGroupWorldReferencePoint;
            }

            if ((hasComponentTypes & ComponentOfInterest.RenderBounds) == ComponentOfInterest.RenderBounds && addChunkComponents)
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.SkipWorldRenderBoundsUpdate)));
            if ((hasComponentTypes & ComponentOfInterest.LODRange) == ComponentOfInterest.LODRange)
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.SkipLODRangeUpdate)));
            if ((hasComponentTypes & ComponentOfInterest.LODWorldReferencePoint) == ComponentOfInterest.LODWorldReferencePoint)
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.SkipLODWorldReferencePointUpdate)));
            if ((hasComponentTypes & ComponentOfInterest.LODGroupWorldReferencePoint) == ComponentOfInterest.LODGroupWorldReferencePoint)
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.SkipLODGroupWorldReferencePointUpdate)));
            if ((hasComponentTypes & ComponentOfInterest.RootLODWorldReferencePoint) == ComponentOfInterest.RootLODWorldReferencePoint)
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.SkipRootLODWorldReferencePointUpdate)));

            if ((hasComponentTypes & ComponentOfInterest.LocalToWorld) == ComponentOfInterest.LocalToWorld 
                && archetype.Has(ref GetDynamicType(TypesToReflect.BuiltinMaterialPropertyUnity_MatrixPreviousM))
                && !archetype.Has<RequestPreviousMatrixTag>())
                componentTypeListToAdd.Add(ComponentType.ReadOnly(GetManagedType(TypesToReflect.SkipBuiltinMaterialPropertyUnity_MatrixPreviousMUpdate)));
            if ((hasComponentTypes & (ComponentOfInterest.RenderBounds | ComponentOfInterest.LocalToWorld)) == (ComponentOfInterest.RenderBounds | ComponentOfInterest.LocalToWorld) && !archetype.HasChunkComponent<ChunkWorldRenderBounds>() && addChunkComponents)
                componentTypeListToAdd.Add(ComponentType.ChunkComponent<ChunkWorldRenderBounds>());

            entityManger.AddComponent(entity, new ComponentTypeSet(componentTypeListToAdd));

            //refresh the "owning chunk" since we've made a structural change
            archetype = entityManger.GetChunk(entity);
            UpdateDynamicTypeHandles();

            var updateLODRangeJobTypes = ComponentOfInterest.LocalToWorld | ComponentOfInterest.MeshLODComponent |
                                         ComponentOfInterest.RootLODRange | ComponentOfInterest.LODRange;
            if ((hasComponentTypes & updateLODRangeJobTypes) == updateLODRangeJobTypes)
            {
                var meshLod = entityManger.GetComponentData<MeshLODComponent>(entity);
                var lodGroupEntity = meshLod.Group;
                var lodMask = meshLod.LODMask;
                var lodGroup = entityManger.GetComponentData<MeshLODGroupComponent>(lodGroupEntity);
                var parentMask = lodGroup.ParentMask;
                var parentGroupEntity = lodGroup.ParentGroup;


                NativeArray<LODRangeCopy> lodRangeArray =
                    archetype.GetDynamicComponentDataArrayReinterpret<LODRangeCopy>(
                        ref GetDynamicType(TypesToReflect.LODRange),
                        UnsafeUtility.SizeOf<LODRangeCopy>());

                NativeArray<RootLODRangeCopy> rootLODRangeArray =
                    archetype.GetDynamicComponentDataArrayReinterpret<RootLODRangeCopy>(
                        ref GetDynamicType(TypesToReflect.RootLODRange),
                        UnsafeUtility.SizeOf<RootLODRangeCopy>());

                EntityStorageInfo storageInfo = entityManger.GetStorageInfo(entity);

                //entityManger.SetComponentData(entity, new LODRange(lodGroup, lodMask));
                lodRangeArray[storageInfo.IndexInChunk] = new LODRangeCopy(lodGroup, lodMask);

                // Store LOD parent group in MeshLODComponent to avoid double indirection for every entity
                meshLod.ParentGroup = parentGroupEntity;
                entityManger.SetComponentData(entity, meshLod);

                RootLODRangeCopy rootLod;

                if (parentGroupEntity == Entity.Null)
                {
                    rootLod.LOD.MinDist = 0;
                    rootLod.LOD.MaxDist = 1048576.0f;
                    rootLod.LOD.LODMask = 0;
                }
                else
                {
                    var parentLodGroup = entityManger.GetComponentData<MeshLODGroupComponent>(parentGroupEntity);
                    rootLod.LOD = new LODRangeCopy(parentLodGroup, parentMask);
                    //CheckDeepHLODSupport(parentLodGroup.ParentGroup);
                }

                //entityManger.SetComponentData(entity, rootLod);
                rootLODRangeArray[storageInfo.IndexInChunk] = rootLod;
            }
        }
    }
}