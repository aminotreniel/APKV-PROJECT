using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Rendering;

namespace TimeGhost
{
    internal struct ScatteredInstanceChildProcessedTag : IComponentData
    {
    }

    [UpdateInGroup(typeof(SimulationSystemGroup  ))]
    [UpdateBefore(typeof(EndSimulationEntityCommandBufferSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]

    public partial struct ScatteredInstanceChildrenGatherSystem : ISystem
    {
        private EntityQuery m_ScatteredInstanceChildren;
        
        private EntityTypeHandle m_EntityTypeHandle;
        [ReadOnly]private ComponentTypeHandle<MeshLODComponent> m_MeshLODType;
        [NativeDisableParallelForRestriction]
        private BufferLookup<ScatteredInstanceChildren> m_InstanceChildrenLookup;
        
        [BurstCompile]
        private struct GatherScatteredInstanceChildrenJob : IJobChunk 
        {
            [ReadOnly] public EntityTypeHandle EntityTypeHandle;
            [ReadOnly] public ComponentTypeHandle<MeshLODComponent> MeshLODType;
            [NativeDisableContainerSafetyRestriction] public BufferLookup<ScatteredInstanceChildren> InstanceChildrenLookup;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var meshLODArray = chunk.GetNativeArray(ref MeshLODType);
                var entityArray = chunk.GetNativeArray(EntityTypeHandle);
                var enumerator = new ChunkEntityEnumerator(useEnabledMask,
                    chunkEnabledMask, chunk.Count);
                
                
                while(enumerator.NextEntityIndex(out int i))
                {
                    var meshLodComponent = meshLODArray[i];
                    
                    if (InstanceChildrenLookup.HasBuffer(meshLodComponent.Group))
                    {
                        InstanceChildrenLookup[meshLodComponent.Group].Add(new ScatteredInstanceChildren { Child = entityArray[i] });
                        
                    }
                }
            }
        }
        
        public void OnCreate(ref SystemState state)
        {
            m_ScatteredInstanceChildren = state.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<MeshLODComponent>(),
                ComponentType.ReadOnly<ScatteredInstanceRenderTileData>(),
                ComponentType.Exclude<ScatteredInstanceChildProcessedTag>());

            m_EntityTypeHandle = state.GetEntityTypeHandle();
            m_MeshLODType = state.GetComponentTypeHandle<MeshLODComponent>(true);
            m_InstanceChildrenLookup = state.GetBufferLookup<ScatteredInstanceChildren>();
        }
        
        
        
        public void OnDestroy(ref SystemState state)
        {
            m_ScatteredInstanceChildren.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            m_EntityTypeHandle.Update(ref state);
            m_MeshLODType.Update(ref state);
            m_InstanceChildrenLookup.Update(ref state);
            
            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            
            state.Dependency = GatherInstanceChildren(state.Dependency);
            ecb.AddComponent<ScatteredInstanceChildProcessedTag>(m_ScatteredInstanceChildren, EntityQueryCaptureMode.AtPlayback);
        }

        [BurstCompile]
        JobHandle GatherInstanceChildren(JobHandle handle)
        {
            var jobHandle = new GatherScatteredInstanceChildrenJob()
            {
                EntityTypeHandle = m_EntityTypeHandle,
                MeshLODType = m_MeshLODType,
                InstanceChildrenLookup = m_InstanceChildrenLookup,
            }.ScheduleParallel(m_ScatteredInstanceChildren, handle);

            return jobHandle;
        }
    }
}
