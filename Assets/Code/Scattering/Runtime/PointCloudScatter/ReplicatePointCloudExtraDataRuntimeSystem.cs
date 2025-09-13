using Unity.Burst;
using Unity.Entities;

namespace TimeGhost
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateBefore(typeof(EndInitializationEntityCommandBufferSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [BurstCompile]
    public partial struct ReplicatePointCloudExtraDataSystem : ISystem
    {
        ReplicatePointCloudExtraData m_replicateExtraDataLogic;
        
        public void OnCreate(ref SystemState state)
        {
            m_replicateExtraDataLogic.OnCreate(ref state);
        }
        
        public void OnDestroy(ref SystemState state)
        {
            m_replicateExtraDataLogic.OnDestroy(ref state);
        }

        public void OnUpdate(ref SystemState state)
        {
            m_replicateExtraDataLogic.OnUpdate(ref state);
            
            var ecb = SystemAPI.GetSingleton<EndInitializationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);

            for (int i = 0; i < m_replicateExtraDataLogic.ExtraDataTypeHandles.Length; ++i)
            {
                ecb.AddComponent<ReplicatePointCloudExtraData.ScatteredInstanceRenderExtraDataProcessedTag>(m_replicateExtraDataLogic.ScatteredInstanceChildrenPerTypeQueries[i], EntityQueryCaptureMode.AtPlayback);
                ecb.AddComponent<ReplicatePointCloudExtraData.ScatteredInstanceRenderExtraDataProcessedTag>(m_replicateExtraDataLogic.ScatteredInstancesPerTypeQueries[i], EntityQueryCaptureMode.AtPlayback);
            }
        }
    }
}
